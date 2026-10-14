// The fixes for the verifier's FAIL on m11/m4-columns-fair (26 September): what ColumnFamiliesVerifyTests does not
// already prove. Each test states one requirement from the fix brief.
//
//   D4  The question that ships is the fair replay's question: for a fixture model in the replay's facts format, the
//       engine's payload (state and questions, as the vendor body carries them) equals the replay's payload mapped by
//       tools/jev-replay/columns/product-payload.mjs, which the node test there pins to the same expected file.
//   D4  With no data connected, the structure still goes, exactly as the replay's builder writes an unmeasured column.
//   D4  and D5  The rows say plainly when Jev saw names only, and how far the batches have got.
//   D3  Approving Jev's Average on a column the author set to None adds no BPA finding and no health warning.
//   D3  With nothing decided (Free, the point off) every scan is exactly what it was.
//   Ruling  A flag or a line number is never summed: not by the principled rule and not by Jev.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ColumnFamiliesFixTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public ColumnFamiliesFixTests(ModelTemplates templates) => _templates = templates;

        private const string Category = "column.category";
        private const string Summarize = "column.summarize";

        // ---- D4: the payload equals the replay's, by the stated mapping ------------------------------------------

        private static string FixtureDir() =>
            Path.Combine(DecisionRegistryTests.RepoRoot(), "tools", "jev-replay", "columns", "test", "fixtures");

        /// <summary>The fixture model (fair-equivalence.json) built in the engine: the same tables, columns in the same
        /// order, the same key, relationship, measures and sort-by.</summary>
        private async Task<DecisionFixture> EquivalenceModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "equivalence", BuildEquivalenceAsync, "ColFixEquivalence.bim");
            f.UseNull();
            return f;
        }

        private static async Task BuildEquivalenceAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Customer Name", "String", null, "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await e.SetObjectPropertyAsync("column:Customer/CustomerKey", "IsKey", "true", "human");
            await e.CreateTableAsync("Sales", "human");
            foreach (var (name, type) in new[] { ("CustomerKey", "Int64"), ("Amount", "Double"), ("Unit Price", "Double"), ("Month Number", "Int64"),
                                                 ("Order Line", "Int64"), ("Region", "String"), ("Region Order", "Int64") })
                await e.CreateColumnAsync("table:Sales", name, type, null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM('Sales'[Amount])", "human");
            await e.CreateMeasureAsync("table:Sales", "Average Price", "AVERAGE(Sales [Unit Price])", "human");
            await e.SetColumnMetadataAsync("column:Sales/Region", null, null, null, "Region Order", "human");
        }

        /// <summary>The fixture's measured facts and row counts as the engine's model facts.</summary>
        private static Semanticus.Engine.Facts.ModelFacts FactsFromFixture(JsonElement model)
        {
            var tables = new List<TableFacts>();
            foreach (var t in model.GetProperty("tables").EnumerateArray())
            {
                var tableName = t.GetProperty("name").GetString();
                var columns = new List<ColumnFacts>();
                foreach (var c in t.GetProperty("columns").EnumerateArray())
                {
                    var m = c.GetProperty("measured");
                    var place = m.GetProperty("placeShare");
                    columns.Add(new ColumnFacts
                    {
                        Ref = "column:" + tableName + "/" + c.GetProperty("name").GetString(),
                        Name = c.GetProperty("name").GetString(),
                        Measured = new MeasuredFacts
                        {
                            Distinct = m.GetProperty("distinct").GetInt64(),
                            BlankShare = m.GetProperty("blankShare").GetDouble(),
                            WholeNumbers = m.GetProperty("wholeNumbers").GetBoolean(),
                            UniqueOnEveryRow = m.GetProperty("uniqueOnEveryRow").GetBoolean(),
                            LooksLikeYear = m.GetProperty("looksLikeYear").GetBoolean(),
                            LooksLikeMonthName = m.GetProperty("looksLikeMonthName").GetBoolean(),
                            LooksLikeMonthNumber = m.GetProperty("looksLikeMonthNumber").GetBoolean(),
                            LooksLikeDayName = m.GetProperty("looksLikeDayName").GetBoolean(),
                            LooksLikeQuarter = m.GetProperty("looksLikeQuarter").GetBoolean(),
                            LooksLikePeriodNumber = m.GetProperty("looksLikePeriodNumber").GetBoolean(),
                            PlaceShare = new PlaceShare
                            {
                                Country = place.GetProperty("country").GetDouble(),
                                Region = place.GetProperty("region").GetDouble(),
                                City = place.GetProperty("city").GetDouble(),
                                Postcode = place.GetProperty("postcode").GetDouble(),
                            },
                            UrlShare = m.GetProperty("urlShare").GetDouble(),
                            ImageUrlShare = m.GetProperty("imageUrlShare").GetDouble(),
                            EmailShare = m.GetProperty("emailShare").GetDouble(),
                            Sampled = m.GetProperty("sampled").GetBoolean(),
                            OneToOneWith = m.GetProperty("oneToOneWith").EnumerateArray().Select(n => "column:" + tableName + "/" + n.GetString()).ToArray(),
                            // What the storage scan measures and the replay dropped before sending: never sent here either.
                            DictionaryBytes = 90210, DataBytes = 31337, Cardinality = 77777,
                        },
                    });
                }
                tables.Add(new TableFacts { Ref = "table:" + tableName, Name = tableName, Rows = t.GetProperty("rows").GetInt64(), Columns = columns.ToArray() });
            }
            return new Semanticus.Engine.Facts.ModelFacts { Data = ModelFactsData.Local, Tables = tables.ToArray() };
        }

        /// <summary>The vendor body's state and questions for one ask, exactly as JevDecisionOracle writes it.</summary>
        private static JsonElement Body(string point, IReadOnlyDictionary<string, object> snapshot, IReadOnlyList<DecisionQuestion> questions)
        {
            var p = DecisionRegistry.Require(point);
            var request = new DecisionRequest(p.Name, p.SchemaVersion, snapshot, questions);
            return JsonDocument.Parse(JevDecisionOracle.BuildBody(request, DecisionDigest.CanonicalSnapshotJson(snapshot))).RootElement.Clone();
        }

        /// <summary>Null when equal as JSON values (object keys in any order, lists in order, numbers by value), else the
        /// path of the first difference.</summary>
        private static string Differs(JsonElement a, JsonElement b, string path)
        {
            if (a.ValueKind != b.ValueKind) return path + ": " + a.ValueKind + " against " + b.ValueKind;
            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var ak = a.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    var bk = b.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    if (!ak.SequenceEqual(bk)) return path + ": keys [" + string.Join(",", ak) + "] against [" + string.Join(",", bk) + "]";
                    foreach (var k in ak) { var d = Differs(a.GetProperty(k), b.GetProperty(k), path + "." + k); if (d != null) return d; }
                    return null;
                case JsonValueKind.Array:
                    var ai = a.EnumerateArray().ToArray(); var bi = b.EnumerateArray().ToArray();
                    if (ai.Length != bi.Length) return path + ": " + ai.Length + " items against " + bi.Length;
                    for (var i = 0; i < ai.Length; i++) { var d = Differs(ai[i], bi[i], path + "[" + i + "]"); if (d != null) return d; }
                    return null;
                case JsonValueKind.Number:
                    return a.GetDouble() == b.GetDouble() ? null : path + ": " + a.GetRawText() + " against " + b.GetRawText();
                case JsonValueKind.String:
                    return a.GetString() == b.GetString() ? null : path + ": \"" + a.GetString() + "\" against \"" + b.GetString() + "\"";
                default:
                    return null;
            }
        }

        [Fact]
        public async Task The_ask_that_ships_is_the_fair_replays_ask_for_the_same_column()
        {
            using var fixtureDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "fair-equivalence.json")));
            using var expectedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "fair-equivalence-expected.json")));
            var facts = FactsFromFixture(fixtureDoc.RootElement.GetProperty("models")[0]);
            using var f = await EquivalenceModelAsync();

            var got = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var category = await f.Sessions.Current.ReadAsync(m => ColumnCategoryFamily.Prepare(m, facts).Asks);
            foreach (var a in category) got[Category + "|" + a.TableName + "|" + a.ColumnName] = Body(Category, a.Snapshot, a.Questions);
            var summarize = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, facts).Asks);
            foreach (var a in summarize) got[Summarize + "|" + a.TableName + "|" + a.ColumnName] = Body(Summarize, a.Snapshot, a.Questions);

            var expected = expectedDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), got.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (key, want) in expected)
            {
                var body = got[key];
                Assert.Null(Differs(body.GetProperty("state"), want.GetProperty("state"), key + " state"));
                Assert.Null(Differs(body.GetProperty("questions"), want.GetProperty("questions"), key + " questions"));
            }
        }

        [Fact]
        public async Task With_no_data_the_structure_still_goes_as_the_replays_builder_writes_an_unmeasured_column()
        {
            using var f = await EquivalenceModelAsync();
            var ask = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null).Asks.Single(a => a.TableName == "Sales" && a.ColumnName == "Amount"));
            var facts = JsonDocument.Parse(DecisionDigest.CanonicalSnapshotJson(ask.Snapshot)).RootElement.GetProperty("column_facts");
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("table_rows").ValueKind);
            Assert.Empty(facts.GetProperty("measured").EnumerateObject());
            Assert.Empty(facts.GetProperty("one_to_one_partners").EnumerateArray());
            // What the model itself says still goes: the measure that names it, the table's shape, the relationship facts.
            Assert.Equal(1, facts.GetProperty("used_by_measures").GetInt32());
            Assert.Equal(2, facts.GetProperty("measure_count_in_the_same_table").GetInt32());
            Assert.Equal("none", facts.GetProperty("relationship_role").GetString());
            Assert.Equal(2, facts.GetProperty("model_tables").GetArrayLength());
        }

        // ---- the rows say what Jev saw, and how far it has got ----------------------------------------------------

        /// <summary>Stores and Sales as the verifier's fixture, answered with sure picks so both families have rows.</summary>
        private async Task<DecisionFixture> SmallModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "small", BuildSmallAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildSmallAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Stores", "human");
            await e.CreateColumnAsync("table:Stores", "Country", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "City", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Unit Price", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "IsWeekend", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Line", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Weekend Flag", "Int64", null, "human");
            foreach (var c in new[] { "IsWeekend", "Order Line", "Weekend Flag", "Unit Price" })
                await e.SetColumnMetadataAsync("column:Sales/" + c, null, "None", null, null, "human");
        }

        /// <summary>Answers every ask of the two column points from a table of (column name, choice, confidence), whatever
        /// its digest, behind a gate a test can close to hold a run while it asks.</summary>
        private sealed class ByNameOracle : IDecisionOracle
        {
            private readonly Dictionary<string, (string Choice, double Confidence)> _answers;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            private TaskCompletionSource<bool> _gate = Open0();
            public ByNameOracle(Dictionary<string, (string, double)> answers) { _answers = answers; }
            private static TaskCompletionSource<bool> Open0() { var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(true); return t; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public void Close() { lock (_seen) _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
            public void Open() { TaskCompletionSource<bool> g; lock (_seen) g = _gate; g.TrySetResult(true); }
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Task wait;
                lock (_seen) { _seen.Add(request); wait = _gate.Task; }
                await wait.ConfigureAwait(false);
                if (request.PerQuestion)   // column.summarize asks a table's columns at once (m11/jev-batch)
                    return BatchedColumnAnswers.Compose(request, (plain, col) => _answers.TryGetValue(plain + "|" + col, out var a) ? a : ("sum", 0.5));
                var column = JsonSerializer.Serialize(request.Snapshot["column_name"]).Trim('"');
                var question = request.Questions[0].Name;
                var key = question + "|" + column;
                var (choice, confidence) = _answers.TryGetValue(key, out var a) ? a
                    : question == "data_category" ? ("none", 0.95) : ("sum", 0.5);
                var fixture = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":500,\"output_tokens\":20},\"answers\":{" + JsonSerializer.Serialize(question)
                    + ":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(choice) + ",\"confidence\":"
                    + confidence.ToString(CultureInfo.InvariantCulture) + "}}}]}";
                return await new RecordedDecisionOracle(fixture, "jev-test-1").AskAsync(request, ct).ConfigureAwait(false);
            }
        }

        private static async Task<ByNameOracle> AnswerAsync(DecisionFixture f, Dictionary<string, (string, double)> answers)
        {
            f.Engine.ColumnJevDecidesForTest = p => p == "column.category" ? true : (bool?)null;   // off in the product since the 26 Sep re-test; these tests exercise it live
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != Category && n != Summarize))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            var oracle = new ByNameOracle(answers);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            return oracle;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static Dictionary<string, (string, double)> SureAnswers() => new Dictionary<string, (string, double)>(StringComparer.Ordinal)
        {
            ["data_category|Country"] = ("Country", 0.97),
            ["summarize_by|Unit Price"] = ("average", 0.93),
            ["summarize_by|Amount"] = ("sum", 0.95),
        };

        [Fact]
        public async Task With_no_data_connected_the_rows_say_Jev_saw_names_only()
        {
            using var f = await SmallModelAsync("ColFixNamesOnly.bim");
            await AnswerAsync(f, SureAnswers());
            var plan = await SettledPlanAsync(f);
            foreach (var id in new[] { "jev.column-category.table:Stores", "jev.column-summarize.table:Sales" })
            {
                var row = Assert.Single(plan.Items, i => i.Id == id);
                Assert.Contains(row.Why, w => w.StartsWith("No data was read for these columns, so Jev saw names only", StringComparison.Ordinal));
            }
        }

        private static async Task BuildRatesAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Attributes", "human");
            for (var i = 1; i <= 61; i++)
                await f.Engine.CreateColumnAsync("table:Attributes", "Rate " + i.ToString("00", CultureInfo.InvariantCulture), "Double", null, "human");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task While_columns_wait_for_a_later_batch_the_row_says_how_far_Jev_has_got()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            using (f)
            {
                await _templates.OpenCopyAsync(f, "rates61", BuildRatesAsync, "ColFixBatches.bim");
                f.UseNull();
                var answers = Enumerable.Range(1, 61).ToDictionary(i => "summarize_by|Rate " + i.ToString("00", CultureInfo.InvariantCulture), _ => ("average", 0.95), StringComparer.Ordinal);
                var oracle = await AnswerAsync(f, answers);
                await f.Engine.SetDecisionPointEnabledAsync(Category, false, "human");

                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                // The first batch has landed; hold the next one so the row can be read while it waits.
                oracle.Close();
                var waiting = await f.Engine.GetModelPlanAsync();
                var row = Assert.Single(waiting.Items, i => i.Id == "jev.column-summarize.table:Attributes");
                Assert.Contains("Jev has looked at 60 of 61 columns in this model so far. The rest follow.", row.Why);
                Assert.Equal(60, row.ChangeSet.Lines.Count(l => l.Op == "set_summarize_by"));

                oracle.Open();
                var done = await SettledPlanAsync(f);
                var all = Assert.Single(done.Items, i => i.Id == "jev.column-summarize.table:Attributes");
                Assert.DoesNotContain(all.Why, w => w.StartsWith("Jev has looked at", StringComparison.Ordinal));
                Assert.Equal(61, all.ChangeSet.Lines.Count(l => l.Op == "set_summarize_by"));
                Assert.Equal(61, all.ChangeSet.Lines.Count(l => l.Op == "create_measure"));
                // Every column asked exactly once over the two batches.
                // Requests are per table chunk now (m11/jev-batch, ChunkSize 30): 61 columns are 30 + 30 (the first run's 60) and 1 (the second).
                var sent = oracle.Seen.Where(r => r.PointName == Summarize).ToArray();
                Assert.Equal(3, sent.Length);
                Assert.Equal(61, sent.SelectMany(BatchedColumnAnswers.Columns).Count());
                Assert.Equal(61, sent.SelectMany(BatchedColumnAnswers.Columns).Distinct(StringComparer.Ordinal).Count());
            }
        }

        // ---- Kane's redesign (26 Sep): approving the row is good practice, so health never reads worse ----------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approving_the_row_sets_columns_to_None_adds_the_measures_and_raises_no_health_warning()
        {
            using var f = await SmallModelAsync("ColFixHealth.bim");
            await AnswerAsync(f, SureAnswers());
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            // Unit Price was set not to add up by the author: no summarize line, only its measure (and the column is hidden).
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Unit Price" && l.Op == "set_summarize_by");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "measure:Sales/Average Unit Price" && l.Op == "create_measure");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Amount" && l.After == "None");

            string[] Findings(Semanticus.Analysis.Scorecard c) => c.Findings.Where(x => !x.Waived).Select(x => x.RuleId + "|" + x.ObjectRef).ToArray();
            string[] Violations(Semanticus.Analysis.BpaScorecard c) => c.Violations.Where(v => !v.Waived).Select(v => v.RuleId + "|" + v.ObjectRef).ToArray();
            var readinessBefore = Findings(await f.Engine.AiReadinessScanAsync());
            var bpaBefore = Violations(await f.Engine.BpaScanAsync());

            ChangeNotification last = null;
            Action<ChangeNotification> handler = n => { if (n != null) last = n; };
            f.Sessions.Bus.Changed += handler;
            ModelPlanActionResult approved;
            try { approved = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest); }
            finally { f.Sessions.Bus.Changed -= handler; }
            Assert.True(approved.Performed, approved.Sentence);

            // JOIN, part 2 (m11/rule-clashes, 28 Sep): with no linguistic schema every visible field already counts as "no synonyms"
            // from the start (SYN-FIELD, beside SYN-SCHEMA's own finding; the Synonyms category scored 0 before and after), so the two
            // measures this row adds are two more of those. That standing work is not a fault of the approval; everything else is.
            static bool StandingSynonymWork(string finding) => finding.StartsWith("SYN-FIELD|", StringComparison.Ordinal) || finding.StartsWith("SYN-TABLE|", StringComparison.Ordinal);
            var readinessAdded = Findings(await f.Engine.AiReadinessScanAsync()).Except(readinessBefore, StringComparer.Ordinal).Where(x => !StandingSynonymWork(x)).ToArray();
            var bpaAdded = Violations(await f.Engine.BpaScanAsync()).Except(bpaBefore, StringComparer.Ordinal).ToArray();
            Assert.True(readinessAdded.Length == 0, "approving added readiness findings: " + string.Join(", ", readinessAdded));
            Assert.True(bpaAdded.Length == 0, "approving added BPA findings: " + string.Join(", ", bpaAdded));
            // The health chip: nothing new at Warning or above, and no grade drop.
            var health = last?.Health;
            Assert.True(health == null || (health.New ?? Array.Empty<string>()).Length == 0,
                "the health check reported: " + string.Join(", ", health?.New ?? Array.Empty<string>()) + " " + health?.Grade);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_with_synonyms_and_camel_cased_columns_also_reads_no_worse_after_approving()
        {
            // UAT lane n, 26 Sep: on Northstar (synonyms kept, "ListPrice") approving first raised "4 new issues": the new
            // measure's name read as a code (NAME-MEASURE) and it had no synonyms (SYN-FIELD). Both are the family's to get right.
            using var f = await SmallModelAsync("ColFixHealthWords.bim");
            await f.Engine.CreateColumnAsync("table:Sales", "ListPrice", "Double", null, "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "human");
            var answers = SureAnswers();
            answers["summarize_by|ListPrice"] = ("average", 0.96);
            await AnswerAsync(f, answers);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "measure:Sales/Average List Price" && l.Op == "create_measure");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "measure:Sales/Average List Price" && l.Op == "set_synonyms");

            ChangeNotification last = null;
            Action<ChangeNotification> handler = n => { if (n != null) last = n; };
            f.Sessions.Bus.Changed += handler;
            ModelPlanActionResult approved;
            try { approved = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest); }
            finally { f.Sessions.Bus.Changed -= handler; }
            Assert.True(approved.Performed, approved.Sentence);
            var health = last?.Health;
            Assert.True(health == null || (health.New ?? Array.Empty<string>()).Length == 0,
                "the health check reported: " + string.Join(", ", health?.New ?? Array.Empty<string>()) + " " + health?.Grade);
        }

        [Fact]
        public async Task The_readiness_and_BPA_scans_read_every_rule_whole_whatever_Jev_decided()
        {
            using var f = await SmallModelAsync("ColFixWhole.bim");
            await AnswerAsync(f, SureAnswers());
            await SettledPlanAsync(f);
            // Nothing is filtered out of a scan for a decided column (Kane, 26 Sep: DAC and BPA advice stays whole).
            var viaEngine = await f.Engine.AiReadinessScanAsync();
            var plain = await f.Sessions.Current.ReadAsync(m => new Semanticus.Analysis.ReadinessAnalyzer().Analyze(m));
            Assert.Equal(plain.Findings.Select(x => x.RuleId + "|" + x.ObjectRef).OrderBy(x => x, StringComparer.Ordinal),
                         viaEngine.Findings.Select(x => x.RuleId + "|" + x.ObjectRef).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Contains(viaEngine.Findings, x => x.RuleId == "DAC-IMPLICIT-MEASURE" && x.ObjectRef == "column:Sales/Amount");
            // On the Plan the explicit-measures row keeps naming Amount until the gap really closes: a row that leaves the
            // Plan reads as done, and it is not done until the family's row is approved (UAT lane n, 26 Sep).
            var plan = await f.Engine.GetModelPlanAsync();
            var explicitRow = plan.Items.Single(i => i.Id == "proposal/implicit-measures");
            Assert.NotEqual(ModelPlanStatuses.Done, explicitRow.Status);
            Assert.Contains("Amount", string.Join("|", explicitRow.Diff ?? Array.Empty<string>()), StringComparison.Ordinal);
            var sales = plan.Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
            var after = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(after.Items, i => i.Id == "proposal/implicit-measures" && i.Status != ModelPlanStatuses.Done
                                                     && string.Join("|", i.Diff ?? Array.Empty<string>()).Contains("Amount", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Apply_safe_fixes_leaves_a_column_Jev_decided_alone()
        {
            using var f = await SmallModelAsync("ColFixSafeFixes.bim");
            await AnswerAsync(f, new Dictionary<string, (string, double)>(StringComparer.Ordinal) { ["data_category|City"] = ("none", 0.95) });
            await SettledPlanAsync(f);
            await f.Engine.ApplySafeFixesAsync("agent");
            Assert.Equal("", (await f.Engine.GetObjectAsync("column:Stores/City")).Properties["dataCategory"]?.ToString() ?? "");
        }

        // ---- round 3 (coordinator, 26 Sep): what the Plan adds on purpose is never offered as unused ----------------

        [Fact]
        public async Task A_base_measure_the_Plan_added_is_never_offered_as_unused_on_either_door()
        {
            // UAT lane n at 521b0a71: approving the base-measure rows took "Remove N unused things" from 35 to 36, because
            // no report uses the new measures yet. The Plan wrote them on purpose, so neither door may offer them.
            using var f = await SmallModelAsync("ColFixOnPurpose.bim");
            await AnswerAsync(f, SureAnswers());
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var added = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            Assert.NotEmpty(added);
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);

            // The scan both doors read (MCP unused_objects, RPC unusedObjects, the Plan's cleanup row).
            var unused = await f.Engine.UnusedObjectsAsync();
            var offered = added.Where(a => unused.Items.Any(i => string.Equals(i.Ref, a, StringComparison.OrdinalIgnoreCase))).ToArray();
            Assert.True(offered.Length == 0, "offered as unused: " + string.Join(", ", offered));
            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = plan.Items.SingleOrDefault(i => i.Id == "cleanup/model");
            foreach (var a in added)
                Assert.DoesNotContain(a.Substring(a.IndexOf('/') + 1), string.Join("|", cleanup?.Diff ?? Array.Empty<string>()), StringComparison.Ordinal);

            // remove_safe_objects, asked for one by name, leaves it and says why in plain words.
            var report = await f.Engine.RemoveSafeObjectsAsync(new[] { added[0] }, null, "agent");
            Assert.Equal(0, report.Count);
            var skipped = Assert.Single(report.Skipped);
            Assert.Contains("added it on purpose", skipped.Reason, StringComparison.Ordinal);

            // Undo takes the measures and their mark back together.
            await f.Engine.UndoAsync("human");
            Assert.DoesNotContain((await f.Engine.ListMeasuresAsync()), x => added.Contains("measure:" + x.Table + "/" + x.Name));
        }

        [Fact]
        public async Task Time_intelligence_measures_Semanticus_generates_are_never_offered_as_unused()
        {
            using var f = await SmallModelAsync("ColFixOnPurposeTi.bim", pro: false);
            await f.Engine.CreateTableAsync("Calendar", "human");
            await f.Engine.CreateColumnAsync("table:Calendar", "Date", "DateTime", null, "human");
            await f.Engine.SetObjectPropertyAsync("column:Calendar/Date", "IsKey", "true", "human");
            await f.Engine.SetObjectPropertyAsync("table:Calendar", "DataCategory", "Time", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Amount", "SUM('Sales'[Amount])", "human");
            var made = await f.Engine.GenerateTimeIntelligenceAsync("measure:Sales/Total Amount", "'Calendar'[Date]", new[] { "YTD", "PY" }, null, "human");
            var refs = made.Created.Select(c => c.Ref).ToArray();
            Assert.NotEmpty(refs);
            var unused = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(unused.Items, i => refs.Contains(i.Ref, StringComparer.OrdinalIgnoreCase));
        }

        // ---- the 26 Sep re-test: data category failed its bar and is off; base measures passed ------------------------

        [Fact]
        public async Task After_its_retest_data_category_is_off_and_the_rule_keeps_the_column_while_base_measures_stay_on()
        {
            using var f = await SmallModelAsync("ColFixRetest.bim");
            await f.ConsentAsync();
            var oracle = new ByNameOracle(SureAnswers());
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            var plan = await SettledPlanAsync(f);

            Assert.False(ColumnCategoryFamily.JevDecides);
            Assert.True(ColumnSummarizeFamily.JevDecides);
            Assert.DoesNotContain(oracle.Seen, r => r.PointName == Category);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal));
            Assert.Contains(plan.Items, i => i.Id == "jev.column-summarize.table:Sales");
            // The geographic rule keeps City, as for a free session.
            var findings = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(findings.Findings, x => x.RuleId == "CAT-GEO" && x.ObjectRef == "column:Stores/City");
        }

        // ---- the ruling: a flag or a line number is never summed ----------------------------------------------------

        [Fact]
        public async Task A_flag_or_a_line_number_is_never_summed_by_the_rule_or_by_Jev()
        {
            using var f = await SmallModelAsync("ColFixFlags.bim");
            // Jev unsure on IsWeekend and Order Line (the principled rule decides), and SURE of a sum on Weekend Flag.
            var answers = SureAnswers();
            answers["summarize_by|IsWeekend"] = ("sum", 0.5);
            answers["summarize_by|Order Line"] = ("sum", 0.5);
            answers["summarize_by|Weekend Flag"] = ("sum", 0.97);
            await AnswerAsync(f, answers);
            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.SingleOrDefault(i => i.Id == "jev.column-summarize.table:Sales");
            // No total for a flag or a line number: no SUM measure on any of them.
            var sums = (sales?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Op == "create_measure" && l.After.StartsWith("SUM", StringComparison.Ordinal)
                            && (l.After.Contains("IsWeekend") || l.After.Contains("Order Line") || l.After.Contains("Weekend Flag")))
                .Select(l => l.Target).ToArray();
            Assert.True(sums.Length == 0, "summed: " + string.Join(", ", sums));
            // Jev's sure sum on the flag is left out with a plain reason a person reads before deciding.
            var refused = sales?.ChangeSet?.Refused ?? Array.Empty<ModelPlanChangeRefusal>();
            Assert.Contains(refused, r => r.Target == "column:Sales/Weekend Flag" && r.Reason.Contains("adding those up means nothing", StringComparison.Ordinal));

            Assert.Equal("none", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "IsReturn" }));
            Assert.Equal("none", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "OrderLine" }));
            Assert.Equal("sum", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "Line Total" }));
            Assert.Equal("sum", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "Island Sales" }));
            // The same cases the node test pins for the re-test's port (tools/jev-replay/columns/rules.mjs productRuleSummarize).
            Assert.Equal("none", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "Has Discount Rate" }));
            Assert.Equal("none", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "Seq" }));
            Assert.Equal("sum", ColumnSummarizeFamily.PrincipledRule(new ColumnSummarizeFamily.Ask { ColumnName = "Quantity" }));
            Assert.False(ColumnSummarizeFamily.FlagOrOrdinal("Isolation Time"));
        }
    }

    /// <summary>For the column tests' hand-made oracles (m11/jev-batch): column.summarize now asks a table's columns in ONE request, with
    /// one question per column named "summarize_by:column:Sales/Amount". This reads such a request question by question and answers each
    /// from a pick, exactly as a per-column oracle answered one request per column. column.category is not batched and never comes here.</summary>
    internal static class BatchedColumnAnswers
    {
        /// <summary>The plain question and the column's name from a batched question's name ("summarize_by:column:Sales/Amount").</summary>
        public static (string Plain, string Column) Split(string questionName)
        {
            var at = questionName.IndexOf(':');
            var objectRef = questionName.Substring(at + 1);
            return (questionName.Substring(0, at), objectRef.Substring(objectRef.IndexOf('/') + 1));
        }

        /// <summary>Every column a request asks about, whichever shape it has.</summary>
        public static string[] Columns(DecisionRequest request) => request.PerQuestion
            ? request.Questions.Select(q => Split(q.Name).Column).ToArray()
            : new[] { JsonSerializer.Serialize(request.Snapshot["column_name"]).Trim('"') };

        /// <summary>One reply for a batched request: for each question, the pick for its (plain question, column).</summary>
        public static DecisionAnswers Compose(DecisionRequest request, Func<string, string, (string Choice, double Confidence)> pick)
        {
            var body = new StringBuilder("{");
            var first = true;
            foreach (var question in request.Questions)
            {
                var (plain, column) = Split(question.Name);
                var (choice, confidence) = pick(plain, column);
                if (!first) body.Append(',');
                first = false;
                body.Append(JsonSerializer.Serialize(question.Name)).Append(":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                    .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            body.Append('}');
            return DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, "jev-test-1", 500 * request.Questions.Count, 20 * request.Questions.Count);
        }
    }
}
