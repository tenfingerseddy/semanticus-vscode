// verify: defects the verifier found in ask_data (lane m11/jev-query at f1a9d4a1, 26 Sep). Every test here FAILS on
// f1a9d4a1 and is left failing on purpose: each one is a defect for the lane's builder to fix, with the evidence in its
// summary. The rig is AskDataTests' own (a Contoso-style model built in code, a fake query connection that answers the
// engine's DISTINCT reads from in-memory values, and a scripted Jev), copied so this file stands alone.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // the same collection as AskDataTests, for the same scratch roots
    public sealed class AskDataVerifyTests : IClassFixture<ModelTemplates>
    {
        // The same Contoso model as AskDataTests, built once and opened as a copy per rig (lean suite, lane A).
        private static ModelTemplates _templates;

        public AskDataVerifyTests(RestoreRootFixture fixture, ModelTemplates templates) { _ = fixture; _templates = templates; }

        // ---- the data behind the fake connection --------------------------------------------------------------

        /// <summary>Values the question never names as they are spelled. None of them may ever reach Jev: not even
        /// "Harborview", which the question's "Harbourview" matches with a slip of the fingers.</summary>
        private static readonly string[] Sentinels = { "Harborview", "Lakeside", "Pinecrest", "Brightwater" };

        private static readonly Dictionary<string, object[]> Values = new Dictionary<string, object[]>(StringComparer.Ordinal)
        {
            ["Store[Region]"] = new object[] { "Coast", "North", "South", "Mountain" },
            ["Store[City]"] = Sentinels.Cast<object>().ToArray(),
            ["Product[Category]"] = new object[] { "Outdoor", "Kitchen", "Apparel" },
            ["Product[ProductName]"] = new object[] { "Trail Tent", "Camp Stove", "Rain Jacket" },
            ["Date[Year]"] = new object[] { 2024L, 2025L },
            ["Date[MonthName]"] = new object[] { "January", "February", "March" },
            ["Customer[Segment]"] = new object[] { "Retail", "Trade" },
        };

        private sealed class FakeData
        {
            public readonly List<string> Queries = new List<string>();
            public int RowsToReturn = 3;

            public IEnumerable<string> Runs => Queries.Where(q => q.Contains("SUMMARIZECOLUMNS"));

            public ResultSet Execute(string q)
            {
                lock (Queries) Queries.Add(q);
                var d = Regex.Match(q, @"DISTINCT \( '((?:[^']|'')+)'\[((?:[^\]]|\]\])+)\] \)");
                if (d.Success)
                {
                    var key = d.Groups[1].Value.Replace("''", "'") + "[" + d.Groups[2].Value.Replace("]]", "]") + "]";
                    var vals = Values.TryGetValue(key, out var v) ? v : Array.Empty<object>();
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = key } }, Rows = vals.Select(x => new[] { x }).ToArray(), RowCount = vals.Length };
                }
                if (q.Contains("SUMMARIZECOLUMNS"))
                {
                    var measures = Regex.Matches(q, "\"@((?:[^\"]|\"\")+)\"").Count;
                    var groups = Regex.Matches(q.Substring(q.IndexOf("SUMMARIZECOLUMNS", StringComparison.Ordinal)), @"^\s+'[^']+'\[[^\]]+\],?$", RegexOptions.Multiline).Count;
                    var cols = Enumerable.Range(0, groups).Select(i => new ColumnDef { Name = "g" + i })
                        .Concat(Enumerable.Range(0, measures).Select(i => new ColumnDef { Name = "[@m" + i + "]" })).ToArray();
                    var rows = Enumerable.Range(0, RowsToReturn).Select(r => cols.Select((c, i) => (object)(i < groups ? "k" + r : (double)(100 - r))).ToArray()).ToArray();
                    return new ResultSet { Columns = cols, Rows = rows, RowCount = rows.Length };
                }
                return ResultSet.FromError("Unexpected query in the ask_data fixture: " + q);
            }
        }

        // ---- Jev, scripted -------------------------------------------------------------------------------------

        /// <summary>Answers query.summarize from a script over (request, question). A question the script does not name
        /// is a sure "none" (or a sure yes for q:fits).</summary>
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<DecisionRequest, DecisionQuestion, (string Choice, double P)?> _script;
            public readonly List<DecisionRequest> Seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<DecisionRequest, DecisionQuestion, (string, double)?> script) { _script = script; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Seen) Seen.Add(request);
                if (request.PointName != DecisionRegistry.QuerySummarize)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers query.summarize only"));
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var a = _script(request, q);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var p = a?.P ?? 0.97;
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append(p.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        var (choice, confidence) = a ?? (q.HasOption("none") ? "none" : q.Options[0].Id, 0.95);
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        /// <summary>The value_matches id Jev's filter question for a column is named by, read from the request.</summary>
        private static string MatchId(DecisionRequest r, string column)
        {
            var matches = (IEnumerable<object>)r.Snapshot["value_matches"];
            foreach (var m in matches.Cast<IDictionary<string, object>>())
                if ((string)m["column"] == column) return (string)m["id"];
            return null;
        }

        // ---- the rig ------------------------------------------------------------------------------------------

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public FakeData Data = new FakeData();
            public ScriptedOracle Jev;
            public LocalEngine Engine => F.Engine;
            public void Dispose() => F.Dispose();
        }

        private static async Task<Rig> BuildAsync(IEntitlement tier = null, bool connect = true, bool bothWays = false,
            Func<DecisionRequest, DecisionQuestion, (string, double)?> script = null, bool barMet = true)
        {
            var rig = new Rig { F = new DecisionFixture(tier ?? TestEntitlements.Pro) };
            var e = rig.Engine;
            await _templates.OpenCopyAsync(rig.F, bothWays ? "contoso-both" : "contoso",
                g => AskDataTests.BuildContosoModelAsync(g.Engine, bothWays), "ContosoRetail.bim");
            await rig.F.ConsentAsync();
            if (connect) e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", rig.Data.Execute));
            e.JevReadsDataForTest = barMet;
            rig.Jev = new ScriptedOracle(script ?? ((_, __) => null));
            e.UseDecisionsForTest(new DecisionService(rig.Jev, new DecisionBudget()));
            return rig;
        }

        private static Task<QueryCatalog> CatalogAsync(Rig rig) => rig.F.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));

        private static QuerySpec Spec(QueryCatalog cat, string[] groups, string[] measures, params (string col, string op, object[] vals)[] filters)
        {
            var s = new QuerySpec();
            s.GroupBy.AddRange(groups.Select(g => cat.Column(g) ?? throw new InvalidOperationException(g)));
            s.Measures.AddRange(measures.Select(m => cat.Measure(m) ?? throw new InvalidOperationException(m)));
            foreach (var (col, op, vals) in filters)
                s.Filters.Add(new QueryFilter { Column = cat.Column(col), Op = op, Values = vals.ToList() });
            return s;
        }

        private static (string, double)? CoastScript(DecisionRequest r, DecisionQuestion q)
        {
            if (q.Name == JevQueryReader.MeasureQ(1)) return ("m:Net Sales", 0.97);
            if (q.Name == JevQueryReader.GroupQ(1)) return ("c:Product[Category]", 0.93);
            if (q.Name == JevQueryReader.FilterQ(MatchId(r, "Store[Region]") ?? "-")) return (QueryOps.Is, 0.96);
            if (q.Name == JevQueryReader.FilterQ(MatchId(r, "Date[Year]") ?? "-")) return (QueryOps.Is, 0.95);
            if (q.Name == JevQueryReader.TopQ) return ("n:3", 0.91);
            if (q.Name == JevQueryReader.TopByQ) return ("m:Net Sales", 0.95);
            if (q.Name == JevQueryReader.TopEndQ) return ("highest", 0.97);
            return null;
        }

        private const string CoastQuestion = "net sales by category in the coast region for 2025, top 3";


        // ---- the defects --------------------------------------------------------------------------------------

        private static string VerifyGit(string dir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        /// <summary>ORDER BY a column that is not in the result makes the server refuse the query ("A single value for
        /// column 'Year' in table 'Date' cannot be determined", seen live on Northstar), and the reading line claims a
        /// split the query does not have ("Net Sales by Store Region, Year A to Z"). The checks must catch it before
        /// anything runs, as the other wrong parts are caught.</summary>
        [Fact]
        public async Task Verify_sorting_by_a_column_the_question_does_not_split_by_is_refused_before_it_runs()
        {
            using var rig = await BuildAsync();
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" }, SortBy = "'Date'[Year]",
            });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Contains(a.Checks, c => !c.Passed);
            Assert.Empty(rig.Data.Runs);
        }

        /// <summary>A column with more distinct values than the matcher reads (5,000) is read fine; there are simply too
        /// many. Live on Northstar, a filter on Sales[OrderNumber] said "The values of OrderNumber could not be read",
        /// which is not what happened. The refusal must say why in words that are true.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Verify_a_column_with_too_many_values_to_match_is_not_called_unreadable()
        {
            using var rig = await BuildAsync();
            var many = Enumerable.Range(1, ValueMatcher.MaxValuesPerColumn + 1000)
                .Select(i => (object)("City " + i.ToString(CultureInfo.InvariantCulture))).ToArray();
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail",
                q => q.Contains("DISTINCT ( 'Store'[City] )")
                    ? new ResultSet { Columns = new[] { new ColumnDef { Name = "City" } }, Rows = many.Select(x => new[] { x }).ToArray(), RowCount = many.Length }
                    : rig.Data.Execute(q)));
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" },
                Filters = new[] { new DataAskFilter { Column = "Store[City]", Values = new[] { "City 42" } } },
            });
            // Checking the words some other way and answering is fine too; a refusal must give the true reason.
            if (a.Outcome == DataAnswer.Answered) return;
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            var line = Assert.Single(a.Checks, c => c.Name == "values" && !c.Passed).Line;
            Assert.DoesNotContain("could not be read", line);
        }

        /// <summary>The history keeps the filter values a reading matched in the model's data ("CustomerName = ...") and
        /// the DAX with those values in it, and DataQuestionStore says they stay on this computer. But the file sits in
        /// the model's sidecar, which the product's own Commit stages (git_commit listed .semanticus/data-questions.json
        /// in its preview in the UAT lane), so the values ride the model's commits to its team repository, and every
        /// ask leaves the model folder changed. The edit ledger (history.json) is kept out of commits for the same kind
        /// of reason. After a question, the model's commit must not take the question history.</summary>
        [Fact]
        public async Task Verify_the_question_history_does_not_ride_the_models_commits()
        {
            using var rig = await BuildAsync(barMet: false);
            var ws = rig.F.Workspace;
            VerifyGit(ws, "init", "-q");
            VerifyGit(ws, "config", "user.email", "verify@test.local");
            VerifyGit(ws, "config", "user.name", "Verify Test");
            VerifyGit(ws, "config", "commit.gpgsign", "false");
            VerifyGit(ws, "add", "-A");
            VerifyGit(ws, "commit", "-q", "-m", "initial");
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Product[Category]" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "coast" } } },
            });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            var preview = await rig.Engine.GitCommitAsync("after a data question", null, false, "human");
            Assert.Null(preview.Error);
            Assert.DoesNotContain(preview.Files ?? Array.Empty<string>(), f => f.EndsWith(DataQuestionStore.FileName, StringComparison.Ordinal));
        }

        /// <summary>ask_data's description says whether Jev reads a question in words, and it says so from the same
        /// constant the engine reads (JevQueryReader.JevReads, true since Kane's 28 Sep decision). While it is true the
        /// description says Jev reads the words, and from what: the names, types, folders and descriptions the reader
        /// sends (JevQueryReader.MeasureField / ColumnField). Were it false, the description must not promise it. The
        /// test switch JevReadsDataForTest reaches the engine's path only, never this compile-time sentence.</summary>
        [Fact]
        public void Verify_the_agent_is_told_whether_Jev_reads_words_as_the_build_does()
        {
            var description = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(
                typeof(McpTools).GetMethod(nameof(McpTools.AskData)), typeof(System.ComponentModel.DescriptionAttribute))).Description;
            if (JevQueryReader.JevReads)
            {
                Assert.Contains("Jev reads", description);
                Assert.Contains("names, column types, folders and descriptions", description);
                Assert.DoesNotContain("not switched on", description);
            }
            else Assert.DoesNotContain("Jev reads", description);
        }

        /// <summary>The second split question lists the column the first already chose. In the replay's run 2, 45 of the
        /// 61 readings that came back unsure had Jev re-pick the first split in the second slot below the bar, and 16
        /// readings were unsure for that reason alone, every one of them otherwise right. The reader then says "Not sure
        /// you meant to split by Category." about a column it is sure of. A repeat of a part already chosen is not doubt.</summary>
        [Fact]
        public async Task Verify_a_second_split_that_repeats_the_first_is_not_reported_as_doubt()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(2) ? ("c:Product[Category]", 0.62) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.DoesNotContain("Not sure you meant to split by Category.", a.Unsure);
        }

        /// <summary>A true or false column is refused in engine words: "IsOpen holds truefalse values". The line is shown
        /// to a person verbatim (preflight: refused shows checks[].line), so it must be plain words.</summary>
        [Fact]
        public async Task Verify_a_true_or_false_column_is_refused_in_plain_words()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Store", "IsOpen", "Boolean", "IsOpen", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" },
                Filters = new[] { new DataAskFilter { Column = "Store[IsOpen]", Values = new[] { "true" } } },
            });
            var line = Assert.Single(a.Checks, c => c.Name == "parts" && !c.Passed).Line;
            Assert.DoesNotContain("truefalse", line);
        }

        /// <summary>ask_data's description tells the assistant to pass the settled parts back with one changed. The parts
        /// come back under the names groupBy, topBy, sortBy and maxRows, but ask_data takes group_by, top_by, sort_by and
        /// max_rows, and the door drops a name it does not know without a word: live on Northstar, ask_data with
        /// "groupBy" answered "Net Sales" (the total, 1 row) instead of "Net Sales by Store Region". Every part the
        /// assistant is told to pass back must be a name ask_data takes.</summary>
        [Fact]
        public async Task Verify_the_parts_the_assistant_is_told_to_pass_back_are_names_ask_data_takes()
        {
            using var rig = await BuildAsync();
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" }, Top = 2, TopBy = "Net Sales",
                SortBy = "Net Sales", Descending = true,
            });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IEngine>(services, rig.Engine);
            using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            var tool = ModelContextProtocol.Server.McpServerTool.Create(typeof(McpTools).GetMethod(nameof(McpTools.AskData)), target: null,
                options: new ModelContextProtocol.Server.McpServerToolCreateOptions { Services = provider });
            var takes = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            using var parts = JsonDocument.Parse(JsonSerializer.Serialize(a.Parts, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            // Either the tool takes the parts whole, or every name in them is one of its own.
            if (takes.Contains("parts")) return;
            foreach (var p in parts.RootElement.EnumerateObject())
                if (p.Value.ValueKind != JsonValueKind.Null)
                    Assert.True(takes.Contains(p.Name), "the parts carry '" + p.Name + "', which ask_data does not take (it takes " + string.Join(", ", takes.OrderBy(x => x)) + ")");
        }

        /// <summary>A model with no place of its own on disk (an unsaved model, or one opened live, whose snapshot is a
        /// temporary copy) keeps its questions in the WORKSPACE's sidecar, so every such model shares one history. Live on
        /// the UAT workspace, open_live of one model and one question, then open_live of a second model: the second model's
        /// list_data_questions showed the first model's question. The preflight contract has the page show the history
        /// note for an unsaved model; with a workspace (which the extension always gives the engine) the note never comes.
        /// A model must never be shown another model's questions.</summary>
        [Fact]
        public async Task Verify_a_model_with_no_place_on_disk_is_not_shown_another_models_questions()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Unsaved A", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            var first = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.Equal(DataAnswer.Built, first.Outcome);

            await e.CreateModelAsync("Contoso Unsaved B", 1604, discardUnsaved: true);
            await e.CreateTableAsync("Orders", "human");
            await e.CreateColumnAsync("table:Orders", "Qty", "Int64", "Qty", "human");
            await e.CreateMeasureAsync("table:Orders", "Units", "SUM ( Orders[Qty] )", "human");
            var second = await e.ListDataQuestionsAsync();
            Assert.DoesNotContain(second.Questions, q => q.Reading == first.Reading);
        }

        /// <summary>A calculation group's column is left out of what a question may split or narrow by, on purpose, but
        /// the refusal says the column does not exist: live on Northstar, 'Time Intelligence'[Time Calculation] came back
        /// "No column named 'Time Intelligence'[Time Calculation]." The line is shown to a person verbatim, so it must say
        /// what is true.</summary>
        [Fact]
        public async Task Verify_a_calculation_group_column_is_not_said_to_be_missing()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateCalculationGroupAsync("Time Calc", "human");
            var column = await rig.F.Sessions.Require().ReadAsync(m => m.Tables["Time Calc"].Columns.First().Name);
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "'Time Calc'[" + column + "]" },
            });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            var line = Assert.Single(a.Checks, c => c.Name == "parts" && !c.Passed).Line;
            Assert.DoesNotContain("No column named", line);
        }

        // ---- verify, round 2 (26 Sep): the combined m11/jev-query 2a3fa67c + m11/askdata-page 154208a3 on m11/integration
        // 568435d3. Each test below FAILS on that combination and is left failing on purpose.

        /// <summary>A refused question keeps nothing of what was asked. Live on Northstar Working (UAT lane h): the page's
        /// "Net Sales by Channel, Year = 2025, Region = Atlantis" came back "No value in Store Region matches 'Atlantis'."
        /// verbatim, as it should, but the Asked before list then showed "A question that was not run" with a Run again
        /// button that does nothing when pressed, because the record carries no parts, no reading and no words
        /// (AnswerAsync returns before it sets Parts when PartsAsync refuses). Every value refusal, every offline filter
        /// edit and every unknown name adds one such dead row, and list_data_questions hands the assistant the same empty
        /// record. DataQuestionStore promises "what was asked, how it was read, when, and what came of it".</summary>
        [Fact]
        public async Task Verify_a_refused_question_keeps_what_was_asked_so_it_can_be_asked_again()
        {
            using var rig = await BuildAsync();
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "Atlantis" } } },
            });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            var kept = (await rig.Engine.ListDataQuestionsAsync()).Questions[0];
            Assert.Equal(DataAnswer.Refused, kept.Outcome);
            Assert.True(kept.Parts != null && (kept.Parts.Measures ?? Array.Empty<string>()).Contains("Net Sales"),
                "the refused question's record holds nothing of what was asked (no parts, no reading, no words), so the page's Run again on it does nothing");
        }

        /// <summary>The assistant's ask that the QueryData gate holds for a person's approval is kept the same empty way.
        /// Live in lane h: ask_data "Units by Category, top 3" from the MCP door was held for approval, and the open page
        /// showed "A question that was not run, Your assistant, refused" whose Run again does nothing, so the person who
        /// approves cannot run the held question from the list.</summary>
        [Fact]
        public async Task Verify_the_assistants_question_held_for_approval_keeps_what_was_asked()
        {
            var before = (Semanticus.Engine.AgentPolicyStore.RootOverride, Semanticus.Engine.ApprovalLedger.RootOverride);
            var connectionsBefore = Semanticus.Engine.ConnectionRegistry.RootOverride;
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-verify-askdata-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Semanticus.Engine.AgentPolicyStore.RootOverride = root;
            Semanticus.Engine.ApprovalLedger.RootOverride = root;
            Semanticus.Engine.ConnectionRegistry.RootOverride = root;
            try
            {
                using var rig = await BuildAsync();
                // An unlabelled target counts as test since 1.4 (3a4dc874), so the question only waits for approval on a target labelled production.
                Semanticus.Engine.ConnectionRegistry.SetLabel(Semanticus.Engine.ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", "Contoso Retail", null, "azcli").Id, "prod", "human");
                var a = await McpTools.AskData(rig.Engine, measures: new[] { "Units" }, group_by: new[] { "Product[Category]" }, top: 3);
                Assert.Equal(DataAnswer.Refused, a.Outcome);
                Assert.NotNull(a.ApprovalId);
                var kept = (await rig.Engine.ListDataQuestionsAsync()).Questions[0];
                Assert.Equal("agent", kept.Origin);
                Assert.True(kept.Parts != null && (kept.Parts.Measures ?? Array.Empty<string>()).Contains("Units"),
                    "the held question's record holds nothing of what was asked, so it cannot be run again from the list once approved");
            }
            finally
            {
                Semanticus.Engine.AgentPolicyStore.RootOverride = before.Item1;
                Semanticus.Engine.ApprovalLedger.RootOverride = before.Item2;
                Semanticus.Engine.ConnectionRegistry.RootOverride = connectionsBefore;
                try { System.IO.Directory.Delete(root, true); } catch { }
            }
        }
    }
}
