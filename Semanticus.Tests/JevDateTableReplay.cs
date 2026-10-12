// The date.table replay (T268, M4 point 3): does Jev decide the date table better than the rules?
//
// The live fact runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It
// drives the product's own path end to end: each labelled model is built in a real engine, the real DateTableFamily
// builds the request through the real family run (GetModelPlan, then the background run), the real JevDecisionOracle
// sends it to the vendor, and the Plan is read back. Nothing is mirrored, so what is scored is what the product does.
//
// The labelled set is tools/jev-replay/datetable/labels.json (reviewer-written: what each model's author should
// decide). The bar is tools/jev-replay/datetable/bar.json, committed before the first run. Both rules are worked out
// here on the same built model: the shipped rule (DATE-MARK plus PlanDateMark, what the Plan does today) and the
// pilot's name rule over the engine's own options. Raw results go OUTSIDE the repository
// (~/.cache/jev-replay-datetable/results.json, overridable with JEV_REPLAY_OUT); tools/jev-replay/datetable/score.mjs
// turns them into aggregate numbers and the verdict. The key is read from ~/.config/typesafe/env into this process
// only, for one client, and is never printed, returned or written.
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevDateTableReplay"
//   node tools/jev-replay/datetable/score.mjs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;
using ModelFacts = Semanticus.Engine.Facts.ModelFacts;
using ModelFactsData = Semanticus.Engine.Facts.ModelFactsData;
using TableFacts = Semanticus.Engine.Facts.TableFacts;
using ColumnFacts = Semanticus.Engine.Facts.ColumnFacts;
using MeasuredFacts = Semanticus.Engine.Facts.MeasuredFacts;

namespace Semanticus.Tests
{
    /// <summary>A fact that runs only when JEV_REPLAY_LIVE is exactly "1", and says so when it is skipped.</summary>
    public sealed class JevDateTableReplayFactAttribute : FactAttribute
    {
        public JevDateTableReplayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("JEV_REPLAY_LIVE") != "1")
                Skip = "The date.table replay calls the Jev service; set JEV_REPLAY_LIVE=1 to run it.";
        }
    }

    public sealed class JevDateTableReplay
    {
        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        /// <summary>The labelled set: labels.json (round 1), heldout-labels.json with JEV_REPLAY_SET=heldout (round 2), or
        /// both with JEV_REPLAY_SET=round3, scored against key.json with every date column's declared data as facts.</summary>
        private static string SetName() => Environment.GetEnvironmentVariable("JEV_REPLAY_SET") switch
        {
            "heldout" => "heldout-labels.json",
            "round3" => "round3",
            _ => "labels.json",
        };

        private static bool Round3 => SetName() == "round3";

        private static IEnumerable<JsonElement> Items(JsonDocument[] docs) =>
            docs.SelectMany(d => d.RootElement.GetProperty("items").EnumerateArray());

        /// <summary>Round 3: the key's declared data for every date column, measured, as the snapshot gives it.</summary>
        private static ModelFacts TruthFacts(JsonElement keyItem)
        {
            var facts = keyItem.GetProperty("facts").EnumerateObject().ToArray();
            if (facts.Length == 0) return new ModelFacts { Data = ModelFactsData.Local };
            return new ModelFacts
            {
                Data = ModelFactsData.Local,
                Tables = facts.GroupBy(p => p.Name.Split('/')[0]).Select(g => new TableFacts
                {
                    Ref = "table:" + g.Key, Name = g.Key,
                    Columns = g.Select(p => new ColumnFacts
                    {
                        Ref = "column:" + p.Name, Name = p.Name.Split('/')[1], DataType = "DateTime",
                        Measured = new MeasuredFacts
                        {
                            UniqueOnEveryRow = p.Value.GetString() != "repeats",
                            ContiguousDays = p.Value.GetString() != "gaps",
                        },
                    }).ToArray(),
                }).ToArray(),
            };
        }

        private static JsonDocument Labels(string file = null) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "datetable", file ?? SetName())));

        private static string Key()
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }

        // ---- one labelled model into a real engine ----------------------------------------------------------

        private static async Task<DecisionFixture> BuildAsync(JsonElement item)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Sessions.Current.MutateAsync("human", "build labelled model", m =>
            {
                foreach (var t in item.GetProperty("tables").EnumerateArray())
                {
                    var name = t.GetProperty("name").GetString();
                    var cols = t.GetProperty("cols").EnumerateArray().Select(c => c.GetString().Split(':')).ToArray();
                    if (t.TryGetProperty("calendar", out var cal))
                    {
                        var ct = m.AddCalculatedTable(name, cal.GetString());
                        foreach (var c in cols)
                        {
                            var col = CalculatedTableColumn.CreateNew(ct, c[0]);
                            col.DataType = Enum.Parse<DataType>(c[1]);
                        }
                    }
                    else
                    {
                        var table = m.AddTable(name);
                        foreach (var c in cols) table.AddDataColumn(c[0], c[0], null, Enum.Parse<DataType>(c[1]));
                    }
                }
                foreach (var r in item.GetProperty("relationships").EnumerateArray().Select(x => x.GetString()))
                {
                    var ends = r.Split('>');
                    var from = ends[0].Split('/');
                    var to = ends[1].Split('/');
                    var rel = m.AddRelationship();
                    rel.FromColumn = m.Tables[from[0]].Columns[from[1]];
                    rel.ToColumn = m.Tables[to[0]].Columns[to[1]];
                }
                foreach (var me in item.GetProperty("measures").EnumerateArray().Select(x => x.GetString().Split('|')))
                    m.Tables[me[0]].AddMeasure(me[1], me[2]);
                if (item.TryGetProperty("calculationGroup", out var cg))
                {
                    var g = m.AddCalculationGroup(cg.GetProperty("name").GetString());
                    foreach (var ci in cg.GetProperty("items").EnumerateArray())
                        g.AddCalculationItem(ci[0].GetString(), ci[1].GetString());
                }
            });
            await f.SaveAsAsync(item.GetProperty("id").GetString() + ".bim");
            return f;
        }

        /// <summary>The labelled facts as the model facts snapshot a live model would give. No facts means no data.</summary>
        private static ModelFacts FactsOf(JsonElement item)
        {
            var facts = item.GetProperty("facts").EnumerateObject().ToArray();
            if (facts.Length == 0) return new ModelFacts { Data = ModelFactsData.None };
            return new ModelFacts
            {
                Data = ModelFactsData.Local,
                Tables = facts.GroupBy(p => p.Name.Split('/')[0]).Select(g => new TableFacts
                {
                    Ref = "table:" + g.Key,
                    Name = g.Key,
                    Columns = g.Select(p => new ColumnFacts
                    {
                        Ref = "column:" + p.Name,
                        Name = p.Name.Split('/')[1],
                        DataType = "DateTime",
                        Measured = new MeasuredFacts
                        {
                            UniqueOnEveryRow = p.Value.GetString() != "repeats",
                            ContiguousDays = p.Value.GetString() != "gaps",
                        },
                    }).ToArray(),
                }).ToArray(),
            };
        }

        // ---- the two rules, on the same built model -----------------------------------------------------------

        /// <summary>What the Plan does today: needed only when DATE-MARK fires; then the one table PlanDateMark names,
        /// marked; needed with no such table is the person's choice (null here, no decision).</summary>
        private static string ShippedRule(Model m)
        {
            if (m.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.Ordinal))) return "none";
            if (!ReadinessRuleSet.UsesTimeIntelligence(m)) return "none";
            var mark = PlanDateMark.Candidate(m);
            return mark == null ? null : "table:" + mark.Table;
        }

        /// <summary>The pilot's name rule over the engine's options (bar.json, nameRule).</summary>
        private static string NameRule(Model m, IReadOnlyList<string> options)
        {
            var named = options.Where(o => o.StartsWith("table:", StringComparison.Ordinal))
                .Select(o => o.Substring("table:".Length))
                .Where(n => n.IndexOf("date", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("calendar", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => string.Equals(n, "Date", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Calendar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
            if (named != null) return "table:" + named;
            var many = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromTable));
            var related = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>().SelectMany(r => new[] { r.FromTable, r.ToTable }));
            var datedFact = m.Tables.Any(t => (many.Contains(t) || !related.Contains(t)) && t.Columns.Any(c => c.DataType == DataType.DateTime));
            return ReadinessRuleSet.UsesTimeIntelligence(m) || datedFact ? "generate" : "none";
        }

        // ---- not live: the labels line up with the engine --------------------------------------------------------

        [Theory]
        [InlineData("labels.json", 30)]
        [InlineData("heldout-labels.json", 25)]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_labels_line_up_with_the_engine(string file, int atLeast)
        {
            using var labels = Labels(file);
            var n = 0;
            foreach (var item in labels.RootElement.GetProperty("items").EnumerateArray())
            {
                n++;
                var id = item.GetProperty("id").GetString();
                using var f = await BuildAsync(item);
                var facts = FactsOf(item);
                var prepared = await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, facts));
                // Since the switch-on only the table choice is Jev's: a model is asked only where time intelligence says a
                // date table is needed and a table is offered, and then exactly one question.
                if (prepared.Ask != null) Assert.Single(prepared.Ask.Questions);
                var label = item.GetProperty("label").GetProperty("table").GetString();
                if (label != "generate" && label != "none")
                    Assert.True(await f.Sessions.Current.ReadAsync(m => m.Tables.Contains(label)), id + " labels a table the model does not have");
            }
            Assert.True(n >= atLeast, n + " labelled models");
        }

        /// <summary>THE POPULATION THAT DECIDES THE SWITCH, read off the engine. The switch is decided on the models the shipped
        /// product asks Jev about (DateTableFamily.Prepare returns an ask), over both labelled sets with the key's declared data
        /// as facts, which is how round 3 ran. That set is written down beside the scorer (asked-population.json) so the rescore
        /// can be run against it, and this test fails the moment the engine asks about a different set, printing the set it asks
        /// about now. Before it existed, the README's recipe pointed at a check that printed nothing about which models are asked,
        /// so the 11 could not be reproduced from the repository (plan-waits targeted verify, L4).</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_models_the_product_asks_about_are_the_population_written_beside_the_scorer()
        {
            using var keyDoc = Labels("key.json");
            var keyById = keyDoc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString(), k => k);
            using var labels = Labels("labels.json");
            using var heldout = Labels("heldout-labels.json");
            var asked = new List<string>();
            var n = 0;
            foreach (var item in Items(new[] { labels, heldout }))
            {
                n++;
                var id = item.GetProperty("id").GetString();
                using var f = await BuildAsync(item);
                var facts = TruthFacts(keyById[id]);
                if (await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, facts).Ask != null)) asked.Add(id);
            }

            using var onFile = Labels("asked-population.json");
            var written = onFile.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Equal(64, n);
            Assert.True(written.SequenceEqual(asked),
                "The engine now asks Jev about " + asked.Count + " of " + n + " labelled models, not the " + written.Length
                + " in tools/jev-replay/datetable/asked-population.json. Write these ids there, in this order, and rescore: "
                + JsonSerializer.Serialize(asked));
        }

        // ---- live -------------------------------------------------------------------------------------------------

        private sealed class RecordingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            public RecordingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public DecisionRequest Request;
            public DecisionAnswers Answers;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var a = await _inner.AskAsync(request, ct).ConfigureAwait(false);
                if (request.PointName == DecisionRegistry.DateTable) { Request = request; Answers = a; }
                return a;
            }
        }

        [JevDateTableReplayFact]
        public async Task Replay_the_labelled_models_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-datetable");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);

            var docs = Round3 ? new[] { Labels("labels.json"), Labels("heldout-labels.json") } : new[] { Labels() };
            using var keyDoc = Labels("key.json");
            var keyById = keyDoc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString(), k => k);
            var budget = new DecisionBudget(900_000);
            using var jev = new JevDecisionOracle(Key(), budget);
            var rows = new List<object>();
            foreach (var item in Items(docs))
            {
                var id = item.GetProperty("id").GetString();
                using var f = await BuildAsync(item);
                var facts = Round3 ? TruthFacts(keyById[id]) : FactsOf(item);
                await f.ConsentAsync();
                foreach (var p in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.PlanRank })
                    await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
                f.Engine.DateTableJevDecidesForTest = true;
                f.Engine.DateTableFactsForTest = () => facts;
                var recording = new RecordingOracle(jev);
                f.Engine.UseDecisionsForTest(new DecisionService(recording, budget));

                var prepared = await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, facts));
                var options = prepared.Ask?.Questions.SingleOrDefault(q => q.Name == DateTableFamily.TableQuestion)?.Options.Select(o => o.Id).ToArray()
                              ?? new[] { DateTableFamily.Generate };   // no table to offer: a new one is the only answer
                var shipped = await f.Sessions.Current.ReadAsync(ShippedRule);
                var byName = await f.Sessions.Current.ReadAsync(m => NameRule(m, options));

                var started = DateTime.UtcNow;
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var plan = await f.Engine.GetModelPlanAsync();
                var ms = (DateTime.UtcNow - started).TotalMilliseconds;

                var line = plan.Items.Where(i => i.Id.StartsWith("jev.date-table.", StringComparison.Ordinal))
                               .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).FirstOrDefault();
                var a = recording.Answers;
                var table = a?.Decided == true ? a.For(DateTableFamily.TableQuestion) : null;
                var needed = a?.Decided == true ? a.For("needed") : null;
                var ruleNeeded = await f.Sessions.Current.ReadAsync(m => ReadinessRuleSet.UsesTimeIntelligence(m));
                var ruleTable = await f.Sessions.Current.ReadAsync(m => PlanDateMark.Candidate(m) is PlanDateMarkCandidate c ? "table:" + c.Table : null);
                var mark = a?.Decided == true ? a.For("mark") : null;
                rows.Add(new
                {
                    id,
                    source = item.GetProperty("source").GetString(),
                    measured = facts.Data != ModelFactsData.None,
                    options,
                    label = item.GetProperty("label").GetProperty("table").GetString(),
                    labelMark = item.GetProperty("label").GetProperty("mark").GetBoolean(),
                    alsoOk = item.GetProperty("label").TryGetProperty("alsoOk", out var ok) ? ok.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>(),
                    asked = recording.Request != null,
                    decided = a?.Decided == true,
                    notDecidedReason = a?.Decided == true ? null : a?.NotDecidedReason,
                    jev = needed?.Noul == false ? "none" : table?.Choice,
                    jevNeeded = needed?.Noul,
                    jevNeededConfidence = needed?.Confidence,
                    jevTable = table?.Choice,
                    jevTableConfidence = table?.Confidence,
                    ruleNeeded,
                    ruleTable,
                    jevConfidence = table?.Confidence,
                    jevMark = mark?.Noul,
                    jevMarkConfidence = mark?.Confidence,
                    line = line == null ? null : new { op = line.Op, target = line.Target, kept = line.Kept, word = line.Provenance?.ConfidenceWord },
                    shipped,
                    nameRule = byName,
                    inputTokens = a?.InputTokens ?? 0,
                    outputTokens = a?.OutputTokens ?? 0,
                    ms,
                });
            }

            var result = new { point = DecisionRegistry.DateTable, set = SetName(), ranUtc = DateTime.UtcNow, rows };
            foreach (var d in docs) d.Dispose();
            File.WriteAllText(Path.Combine(outDir, Round3 ? "round3-results.json" : SetName() == "labels.json" ? "results.json" : "heldout-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Assert.NotEmpty(rows);
        }
    }
}
