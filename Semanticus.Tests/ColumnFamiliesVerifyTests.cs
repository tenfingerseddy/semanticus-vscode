// VERIFIER'S TESTS for m11/m4-columns-fair at 0e6515d5 (column.category and column.summarize, the fair replay of
// 26 September). Each test states one thing the lane claims or the brief requires. Some pass and pin behaviour the
// lane's own tests do not reach; the ones marked DEFECT fail on 0e6515d5 on purpose and are left failing.
//
//   1. A sure summarize pick and an unsure one end differently on the same column (the lane's own sure-pick test
//      can no longer tell them apart, its report says so).
//   2. DEFECT: a sure "none" category keeps the geographic rule off the column only until the next edit. While
//      Jev is asked again, the rule's line is back on the Plan and approving the rule's row writes the category.
//   3. DEFECT: the same for a sure summarize pick and the older summarize rules.
//   4. A permission given before the two points existed covers neither, and nothing is asked for them.
//   5. Free asks nothing and keeps the older rules for both settings.
//   6. What goes to Jev is names, types and derived counts, shares and flags: never a value.
//   7. One undo takes back every line of an approved row, redo puts them all back, a second undo takes them back.
//   8. DEFECT: a category column past the first sixty is never asked, on this run or any later one, while the
//      first sixty keep no category (Jev's commonest sure answer is none). The family's own comment says the rest
//      are asked on a later run.
//   9. DEFECT: the same starvation for summarize: a number column past the first sixty is never asked.
//  10. DEFECT: the product's ask does not send the facts the fair replay scored (its fair arm sent the column's
//      measure use, the table's row count, the one-to-one partners and the sort-target flag; the product sends
//      none of them), so the replay's evidence is for a different question.
//  11. DEFECT: a Jev row that carries a line the principled rule decided (Jev was unsure) still says "Jev chose
//      each setting". Seen on Northstar in the UAT lane: "Set how 1 column adds up in Date", From Jev, whose only
//      line is the rule's IsWeekend None to Sum, "IsWeekend is an amount or a quantity".
//  12. DEFECT: approving Jev's sure Average on a column the author set to None makes the product's own readiness
//      check flag that column (DAC-IMPLICIT-MEASURE). Seen in the UAT lane: approving "Set how 2 columns add up in
//      Product" raised "This change worsened model health" with 4 new issues and an offer to undo it.
//
// AMENDED BY THE BUILDER, 26 SEPTEMBER, FOR KANE'S REDESIGN OF column.summarize (relayed by the coordinator): "Jev should
// set summarisation of columns off as a best practice. It should suggest an explicit base measure aggregation
// instead." Every number column Jev hears back on now goes to None, and Jev's pick chooses the column's explicit base
// measure. Tests 1, 7 and 12 asserted the retired design (a sure pick keeping a column's summarize, summarizeBy on every
// line of the row, Average written to the column); each keeps its intent on the new design and says what changed.

using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class ColumnFamiliesVerifyTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public ColumnFamiliesVerifyTests(ModelTemplates templates) => _templates = templates;

        private const string Category = "column.category";
        private const string Summarize = "column.summarize";

        // ---- fixtures ------------------------------------------------------------------------------------

        /// <summary>Stores: Country, Zip, Store Name, Region Label, City, Lat. Sales: Amount, Quantity, Unit Price,
        /// Fiscal Year, Order Number, Month Number, CustomerKey (many side). Customer: CustomerKey (one side).</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "contoso", BuildContosoAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildContosoAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Stores", "human");
            await e.CreateColumnAsync("table:Stores", "Country", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "Zip", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "Store Name", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "Region Label", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "City", "String", null, "human");
            await e.CreateColumnAsync("table:Stores", "Lat", "Double", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Unit Price", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Fiscal Year", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Number", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Month Number", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
        }

        /// <summary>A recorded oracle behind a gate that can be closed, so a run can be held while it asks. It also
        /// keeps every request it saw.</summary>
        private sealed class GatedOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            private TaskCompletionSource<bool> _gate = Opened();
            public GatedOracle(IDecisionOracle inner) { _inner = inner; }
            private static TaskCompletionSource<bool> Opened()
            {
                var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                t.SetResult(true);
                return t;
            }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public void Close() { lock (_seen) _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
            public void Open() { TaskCompletionSource<bool> g; lock (_seen) g = _gate; g.TrySetResult(true); }
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Task wait;
                lock (_seen) { _seen.Add(request); wait = _gate.Task; }
                await wait.ConfigureAwait(false);
                return await _inner.AskAsync(request, ct).ConfigureAwait(false);
            }
        }

        private static async Task<Dictionary<string, string>> CategoryDigestsAsync(DecisionFixture f)
        {
            var point = DecisionRegistry.Require(Category);
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnCategoryFamily.Prepare(m, null));
            return prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName,
                a => new DecisionRequest(point.Name, point.SchemaVersion, a.Snapshot, a.Questions).Digest, StringComparer.Ordinal);
        }

        private static async Task<Dictionary<string, string>> SummarizeDigestsAsync(DecisionFixture f)
        {
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            // The family asks a table's columns at once (m11/jev-batch): a fixture entry is keyed by the column's ref, not a digest.
            return prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName, a => a.Ref, StringComparer.Ordinal);
        }

        private static string Fixture(IEnumerable<(string Point, string Question, string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (point, question, digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":").Append(JsonSerializer.Serialize(point))
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{").Append(JsonSerializer.Serialize(question)).Append(":{\"type\":\"choice\",\"choice\":")
                  .Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Consent, then a gated recorded oracle answering the category and summarize asks as given, with
        /// every other point switched off so only these two families ask.</summary>
        private static async Task<GatedOracle> AnswerAsync(DecisionFixture f, string city = "none", double cityConfidence = 0.95,
                                                           string orderNumber = "sum", double orderNumberConfidence = 0.9)
        {
            f.Engine.ColumnJevDecidesForTest = p => p == "column.category" ? true : (bool?)null;   // off in the product since the 26 Sep re-test; these tests exercise it live
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != Category && n != Summarize))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            var c = await CategoryDigestsAsync(f);
            var s = await SummarizeDigestsAsync(f);
            var rows = new List<(string, string, string, string, double)>
            {
                (Category, "data_category", c["Stores/Country"], "Country", 0.97),
                (Category, "data_category", c["Stores/Zip"], "PostalCode", 0.92),
                (Category, "data_category", c["Stores/Lat"], "Latitude", 0.9),
                (Category, "data_category", c["Stores/Store Name"], "none", 0.95),
                (Category, "data_category", c["Stores/City"], city, cityConfidence),
                (Summarize, "summarize_by", s["Sales/Unit Price"], "average", 0.93),
                (Summarize, "summarize_by", s["Sales/Fiscal Year"], "none", 0.9),
                (Summarize, "summarize_by", s["Sales/Order Number"], orderNumber, orderNumberConfidence),
                (Summarize, "summarize_by", s["Sales/Month Number"], "none", 0.6),
                (Summarize, "summarize_by", s["Sales/Amount"], "sum", 0.95),
                (Summarize, "summarize_by", s["Sales/Quantity"], "sum", 0.95),
            };
            var gated = new GatedOracle(new ByObjectRecordedOracle(Fixture(rows), "jev-test-1"));
            f.Engine.UseDecisionsForTest(new DecisionService(gated, new DecisionBudget()));
            return gated;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>The targets of the readiness rules' formatting row for one property, as proposing it shows them.</summary>
        private static async Task<string[]> RuleTargetsAsync(DecisionFixture f, string property)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.SingleOrDefault(i => i.Id == "safefix/Formatting");
            if (row == null) return Array.Empty<string>();
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            Assert.True(proposed.Plan != null, proposed.Sentence);
            return (proposed.Plan.Items.SingleOrDefault(i => i.Id == row.Id)?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Property == property).Select(l => l.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        }

        /// <summary>Propose the rules' formatting row, as a person or an agent would, and approve exactly the set it
        /// shows (approve without the set's digest is refused, so it proves nothing). Returns the targets it named for
        /// one property and the approval's result.</summary>
        private static async Task<(string[] Targets, ModelPlanActionResult Approved)> ProposeAndApproveRuleRowAsync(DecisionFixture f, string property)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.SingleOrDefault(i => i.Id == "safefix/Formatting");
            if (row == null) return (Array.Empty<string>(), null);
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            var set = proposed.Plan.Items.Single(i => i.Id == row.Id).ChangeSet;
            var targets = (set?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Where(l => l.Property == property).Select(l => l.Target).ToArray();
            var approved = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", set?.Digest);
            return (targets, approved);
        }

        private static async Task<string> PropertyOf(DecisionFixture f, string objRef, string property) =>
            (await f.Engine.GetObjectAsync(objRef)).Properties[property]?.ToString() ?? "";

        // ---- 1. sure and unsure end differently -----------------------------------------------------------------

        [Fact]
        public async Task Summarize_a_sure_sum_keeps_the_column_but_an_unsure_sum_hands_it_to_the_principled_rule()
        {
            // Sure: Jev's "sum" decides Order Number's base measure, a total, as Jev's line. (Amended for the redesign: the
            // column itself now always goes to None, by the good-practice rule, sure or not.)
            using (var sure = await ModelAsync("VerifySumSure.bim"))
            {
                await AnswerAsync(sure, orderNumber: "sum", orderNumberConfidence: 0.9);
                var row = (await SettledPlanAsync(sure)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
                var total = Assert.Single(row.ChangeSet.Lines, l => l.Op == "create_measure" && l.After.Contains("'Sales'[Order Number]"));
                Assert.Equal(ModelPlanChangeKinds.Decision, total.Provenance.Kind);
                Assert.DoesNotContain("column:Sales/Order Number", await RuleTargetsAsync(sure, "summarizeBy"));
            }

            // Unsure, the same answer: the principled rule decides, and a number named like an id is never summed.
            using var unsure = await ModelAsync("VerifySumUnsure.bim");
            await AnswerAsync(unsure, orderNumber: "sum", orderNumberConfidence: 0.6);
            var sales = (await SettledPlanAsync(unsure)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var line = Assert.Single(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Order Number");
            Assert.Equal("None", line.After);
            Assert.Equal(ModelPlanChangeKinds.Rule, line.Provenance.Kind);
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.After.Contains("'Sales'[Order Number]"));
            Assert.DoesNotContain("column:Sales/Order Number", await RuleTargetsAsync(unsure, "summarizeBy"));
        }

        // ---- 2 and 3. DEFECT: a decision holds only until the next edit ------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Category_a_sure_none_keeps_the_geographic_rule_off_the_column_while_Jev_is_asked_again()
        {
            using var f = await ModelAsync("VerifyCatNoneHolds.bim");
            var gate = await AnswerAsync(f, city: "none", cityConfidence: 0.95);
            await SettledPlanAsync(f);
            // Settled, the lane's claim holds: the geographic rule no longer names City.
            Assert.DoesNotContain("column:Stores/City", await RuleTargetsAsync(f, "dataCategory"));

            // A person adds a column to Stores. Every Stores ask is new, so Jev is asked again; hold that run.
            gate.Close();
            await f.Engine.CreateColumnAsync("table:Stores", "Opened On", "DateTime", null, "human");
            var (whileAsking, approved) = await ProposeAndApproveRuleRowAsync(f, "dataCategory");
            var written = await PropertyOf(f, "column:Stores/City", "dataCategory");
            gate.Open();
            await f.Engine.JevSettledForTest();

            // FAILS on 0e6515d5: the rule's line is back while Jev is asked again, and approving that row writes City.
            Assert.True(!whileAsking.Contains("column:Stores/City") && written == "",
                "while Jev was asked again the geographic rule named City: " + whileAsking.Contains("column:Stores/City")
                + "; approving the rule's row (performed: " + approved?.Performed + ") wrote the category '" + written
                + "' on a column Jev was sure is none");
        }

        [Fact]
        public async Task Category_approving_the_rule_row_after_a_sure_none_leaves_the_column_alone()
        {
            // The lane's own test ends "Nor does running that row write it" with approve and NO digest, which the
            // engine refuses whatever the row holds, so that step proved nothing. This is the same step, approved.
            using var f = await ModelAsync("VerifyCatNoneApprove.bim");
            await AnswerAsync(f, city: "none", cityConfidence: 0.95);
            await SettledPlanAsync(f);
            // The rules' row still has work of its own (Customer's key sums by default and nobody asked about it).
            var (targets, approved) = await ProposeAndApproveRuleRowAsync(f, "dataCategory");
            Assert.DoesNotContain("column:Stores/City", targets);
            Assert.True(approved != null && approved.Performed, approved?.Sentence);
            Assert.Equal("", await PropertyOf(f, "column:Stores/City", "dataCategory"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_sure_pick_keeps_the_summarize_rules_off_the_column_while_Jev_is_asked_again()
        {
            using var f = await ModelAsync("VerifySumHolds.bim");
            var gate = await AnswerAsync(f, orderNumber: "sum", orderNumberConfidence: 0.9);
            await SettledPlanAsync(f);
            Assert.DoesNotContain("column:Sales/Order Number", await RuleTargetsAsync(f, "summarizeBy"));

            gate.Close();
            await f.Engine.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            var (whileAsking, approved) = await ProposeAndApproveRuleRowAsync(f, "summarizeBy");
            var written = await PropertyOf(f, "column:Sales/Order Number", "summarizeBy");
            gate.Open();
            await f.Engine.JevSettledForTest();

            // FAILS on 0e6515d5: the summarize rules name Order Number again, and approving their row writes None.
            Assert.True(!whileAsking.Contains("column:Sales/Order Number") && written == "Default",
                "while Jev was asked again the summarize rules named Order Number: " + whileAsking.Contains("column:Sales/Order Number")
                + "; approving the rule's row (performed: " + approved?.Performed + ") set it to '" + written + "' against Jev's sure sum");
        }

        // ---- 4. consent ----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_permission_from_before_the_two_points_covers_neither_and_nothing_is_asked_for_them()
        {
            using var f = await ModelAsync("VerifyConsentOld.bim");
            await AnswerAsync(f);
            // The permission a person gave on the build before these points: version 4, without their fields.
            var older = DecisionRegistry.DeclaredFields
                .Where(x => !x.StartsWith(Category + ":", StringComparison.Ordinal) && !x.StartsWith(Summarize + ":", StringComparison.Ordinal))
                .ToArray();
            await f.Engine.SetConsentAsync(older, DecisionRegistry.ConsentSchemaVersion, "human");
            var consent = await f.Engine.GetConsentAsync();
            var plan = await SettledPlanAsync(f);

            Assert.False(consent.Current);
            Assert.Contains(Category + ":", consent.Reason, StringComparison.Ordinal);
            Assert.Contains(Summarize + ":", consent.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal)
                                                   || i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal));
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);

            // Agreeing again to the list in force switches both on.
            await f.ConsentAsync();
            var after = await SettledPlanAsync(f);
            Assert.Contains(after.Items, i => i.Id == "jev.column-category.table:Stores");
            Assert.Contains(after.Items, i => i.Id == "jev.column-summarize.table:Sales");
        }

        // ---- 5. Free -------------------------------------------------------------------------------------------

        [Fact]
        public async Task Free_asks_nothing_and_keeps_the_older_rules_for_both_settings()
        {
            using var f = await ModelAsync("VerifyFree.bim", pro: false);
            await f.ConsentAsync();
            var oracle = f.UseRecorded("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[]}");
            var plan = await SettledPlanAsync(f);

            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.Contains("column:Stores/City", await RuleTargetsAsync(f, "dataCategory"));
            var summarize = await RuleTargetsAsync(f, "summarizeBy");
            Assert.Contains("column:Sales/Order Number", summarize);
            Assert.Contains("column:Sales/Month Number", summarize);
        }

        // ---- 6. no values in the payload -------------------------------------------------------------------------

        [Fact]
        public async Task What_goes_to_Jev_is_names_types_and_derived_facts_never_a_value()
        {
            using var f = await ModelAsync("VerifyPayload.bim");
            var facts = new Semanticus.Engine.Facts.ModelFacts
            {
                Data = ModelFactsData.Local,
                Tables = new[]
                {
                    new TableFacts
                    {
                        Ref = "table:Stores", Name = "Stores", Rows = 4096,
                        Columns = new[]
                        {
                            new ColumnFacts
                            {
                                Ref = "column:Stores/City", Name = "City", DataType = "String",
                                Measured = new MeasuredFacts
                                {
                                    Distinct = 377, BlankShare = 0.01, UrlShare = 0, ImageUrlShare = 0, EmailShare = 0,
                                    PlaceShare = new PlaceShare { Country = 0.02, Region = 0.1, City = null, Postcode = 0 },
                                    OneToOneWith = new[] { "Store Name" }, DictionaryBytes = 90210, DataBytes = 31337, Cardinality = 377,
                                },
                            },
                        },
                    },
                    new TableFacts
                    {
                        Ref = "table:Sales", Name = "Sales", Rows = 81234,
                        Columns = new[]
                        {
                            new ColumnFacts
                            {
                                Ref = "column:Sales/Amount", Name = "Amount", DataType = "Double",
                                Measured = new MeasuredFacts { Distinct = 5123, BlankShare = 0, WholeNumbers = false, OneToOneWith = new[] { "Order Number" } },
                            },
                        },
                    },
                },
            };
            var categoryAsks = await f.Sessions.Current.ReadAsync(m => ColumnCategoryFamily.Prepare(m, facts).Asks);
            var summarizeAsks = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, facts).Asks);
            // What column.summarize actually sends is one request per table (m11/jev-batch): the same payload rule holds for it.
            var summarizeBatches = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Batches(ColumnSummarizeFamily.Prepare(m, facts)).Select(b => b.Snapshot).ToList());
            Assert.NotEmpty(summarizeBatches);

            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                "Stores", "Customer", "Sales", "Country", "Zip", "Store Name", "Region Label", "City", "Lat", "CustomerKey",
                "Amount", "Quantity", "Unit Price", "Fiscal Year", "Order Number", "Month Number",
                "String", "Double", "Int64", "DateTime", "Boolean", "Decimal", "none", "one", "many",
            };
            var snapshots = categoryAsks.Select(a => a.Snapshot).Concat(summarizeAsks.Select(a => a.Snapshot)).Concat(summarizeBatches).ToList();
            Assert.NotEmpty(snapshots);
            foreach (var snapshot in snapshots)
            {
                var json = DecisionDigest.CanonicalSnapshotJson(snapshot);
                foreach (var leaked in new[] { "90210", "31337", "81234", "4096" })
                    Assert.DoesNotContain(leaked, json, StringComparison.Ordinal);   // bytes and row counts stay home
                foreach (var word in new[] { "\"min", "\"max", "\"sample\"", "\"samples\"", "\"values\"", "\"value\"" })
                    Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
                foreach (var s in Strings(JsonDocument.Parse(json).RootElement))
                    Assert.True(names.Contains(s), "a string that is not a name, a type or a role went: " + s);
            }
        }

        private static IEnumerable<string> Strings(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: yield return e.GetString(); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) foreach (var s in Strings(p.Value)) yield return s; break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) foreach (var s in Strings(x)) yield return s; break;
            }
        }

        // ---- 7. undo and redo move a row as one -----------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_undo_takes_back_an_approved_row_and_redo_puts_every_line_back()
        {
            using var f = await ModelAsync("VerifyUndoRedo.bim");
            await AnswerAsync(f);
            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var stores = plan.Items.Single(i => i.Id == "jev.column-category.table:Stores");
            var salesTargets = sales.ChangeSet.Lines.Select(l => (l.Target, l.After)).ToArray();
            // Amended for the redesign: the row now holds column lines (summarizeBy) and new measures.
            var salesColumns = sales.ChangeSet.Lines.Where(l => l.Op == "set_summarize_by").Select(l => (l.Target, l.After)).ToArray();
            var salesMeasures = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            Assert.NotEmpty(salesMeasures);
            async Task Measures(bool exist)
            {
                var names = (await f.Engine.ListMeasuresAsync()).Select(x => "measure:" + x.Table + "/" + x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var m in salesMeasures) Assert.Equal(exist, names.Contains(m));
            }
            var storesTargets = stores.ChangeSet.Lines.Select(l => (l.Target, l.After)).ToArray();
            Assert.True(salesTargets.Length >= 3, "the summarize row should change several columns");
            Assert.True(storesTargets.Length >= 3, "the category row should change several columns");

            async Task Check(IEnumerable<(string Target, string After)> lines, string property, bool applied, string empty)
            {
                foreach (var (target, after) in lines)
                    Assert.Equal(applied ? after : empty, await PropertyOf(f, target, property));
            }

            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
            await Check(salesColumns, "summarizeBy", true, "Default"); await Measures(true);
            await f.Engine.UndoAsync("human");
            await Check(salesColumns, "summarizeBy", false, "Default"); await Measures(false);
            await f.Engine.RedoAsync("human");
            await Check(salesColumns, "summarizeBy", true, "Default"); await Measures(true);
            await f.Engine.UndoAsync("human");
            await Check(salesColumns, "summarizeBy", false, "Default"); await Measures(false);

            var fresh = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-category.table:Stores");
            Assert.True((await f.Engine.ModelPlanActionAsync(fresh.Id, "approve", "human", fresh.ChangeSet.Digest)).Performed);
            await Check(storesTargets, "dataCategory", true, "");
            await f.Engine.UndoAsync("human");
            await Check(storesTargets, "dataCategory", false, "");
            await f.Engine.RedoAsync("human");
            await Check(storesTargets, "dataCategory", true, "");
            await f.Engine.UndoAsync("human");
            await Check(storesTargets, "dataCategory", false, "");

            // Back where it started: each row is on the Plan once more, with the same lines and no line twice.
            var again = await SettledPlanAsync(f);
            var s2 = again.Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var c2 = again.Items.Single(i => i.Id == "jev.column-category.table:Stores");
            Assert.Equal(salesTargets.OrderBy(x => x.Target), s2.ChangeSet.Lines.Select(l => (l.Target, l.After)).OrderBy(x => x.Target));
            Assert.Equal(storesTargets.OrderBy(x => x.Target), c2.ChangeSet.Lines.Select(l => (l.Target, l.After)).OrderBy(x => x.Target));
        }

        // ---- 8 and 9. DEFECT: the columns past the first sixty ---------------------------------------------------

        private async Task<DecisionFixture> WideModelAsync(string file, string dataType, int columns)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "wide-" + dataType + "-" + columns, async b =>
            {
                await b.Engine.CreateModelAsync("Contoso", 1604);
                await b.Engine.CreateTableAsync("Attributes", "human");
                for (var i = 1; i <= columns; i++)
                    await b.Engine.CreateColumnAsync("table:Attributes", "Attribute " + i.ToString("00", CultureInfo.InvariantCulture), dataType, null, "human");
            }, file);
            f.UseNull();
            return f;
        }

        /// <summary>Answers every ask of one point with one sure choice, whatever its digest.</summary>
        private sealed class SureOracle : IDecisionOracle
        {
            private readonly string _point, _question, _choice;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public SureOracle(string point, string question, string choice) { _point = point; _question = question; _choice = choice; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                if (request.PerQuestion)   // column.summarize asks a table's columns at once (m11/jev-batch)
                    return Task.FromResult(BatchedColumnAnswers.Compose(request, (plain, col) => (_choice, 0.95)));
                var fixture = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":500,\"output_tokens\":20},\"answers\":{"
                    + JsonSerializer.Serialize(_question) + ":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(_choice)
                    + ",\"confidence\":0.95}}}]}";
                return new RecordedDecisionOracle(fixture, "jev-test-1").AskAsync(request, ct);
            }
        }

        private static async Task<string[]> AskedColumnsOverTwoRunsAsync(DecisionFixture f, string point, string question, string choice)
        {
            f.Engine.ColumnJevDecidesForTest = p => p == "column.category" ? true : (bool?)null;   // off in the product since the 26 Sep re-test; these tests exercise it live
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != point))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            var oracle = new SureOracle(point, question, choice);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            await SettledPlanAsync(f);
            // A later run: an unrelated edit, and the Plan read again.
            await f.Engine.SetDescriptionAsync("table:Attributes", "Everything we know about a store.", "human");
            await SettledPlanAsync(f);
            return oracle.Seen.Where(r => r.PointName == point)
                .SelectMany(BatchedColumnAnswers.Columns)
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Category_a_column_past_the_first_sixty_is_asked_on_a_later_run()
        {
            using var f = await WideModelAsync("VerifyCatWide.bim", "String", 61);
            var asked = await AskedColumnsOverTwoRunsAsync(f, Category, "data_category", "none");
            Assert.Equal(60, asked.Length - (asked.Contains("Attribute 61") ? 1 : 0));
            // FAILS on 0e6515d5: never asked, run after run, because the first sixty keep no category.
            Assert.True(asked.Contains("Attribute 61"), "asked over two runs: " + asked.Length + " columns, the last " + asked.Last());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_column_past_the_first_sixty_is_asked_on_a_later_run()
        {
            using var f = await WideModelAsync("VerifySumWide.bim", "Double", 61);
            var asked = await AskedColumnsOverTwoRunsAsync(f, Summarize, "summarize_by", "sum");
            // FAILS on 0e6515d5: every run takes the same first sixty number columns.
            Assert.True(asked.Contains("Attribute 61"), "asked over two runs: " + asked.Length + " columns, the last " + asked.Last());
        }

        // ---- 10. DEFECT: the product's question is not the one the replay scored ----------------------------------

        // ---- 11. DEFECT: a rule line under a row that says Jev chose it ----------------------------------------------

        [Fact]
        public async Task Summarize_a_row_with_a_rule_line_does_not_say_Jev_chose_each_setting()
        {
            using var f = await ModelAsync("VerifyRowWords.bim");
            await AnswerAsync(f);   // Month Number: Jev unsure (0.6), so the principled rule decides it
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var ruleLines = sales.ChangeSet.Lines.Where(l => l.Provenance.Kind == ModelPlanChangeKinds.Rule).Select(l => l.Target).ToArray();
            Assert.Contains("column:Sales/Month Number", ruleLines);
            // FAILS on 0e6515d5: the row's own words say every setting in it was Jev's choice.
            var claim = sales.Why.FirstOrDefault(w => w.Contains("Jev chose each setting", StringComparison.Ordinal));
            Assert.True(claim == null, "the row carries " + ruleLines.Length + " line(s) the rule decided, and says: " + claim);
        }

        // ---- 12. DEFECT: Jev's approved decision is flagged by the product's own readiness check -------------------

        [Fact]
        public async Task Summarize_approving_Jevs_average_does_not_make_the_readiness_check_flag_the_column()
        {
            using var f = await ModelAsync("VerifyHealth.bim");
            // The author set Unit Price to not add up, as Northstar's ListPrice and UnitCost are.
            await f.Engine.SetColumnMetadataAsync("column:Sales/Unit Price", null, "None", null, null, "human");
            await AnswerAsync(f);   // Unit Price: Jev sure average (0.93)
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            // Amended for the redesign: Jev's Average is Unit Price's explicit base measure; the column stays None.
            var measure = Assert.Single(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.After.Contains("'Sales'[Unit Price]"));
            Assert.StartsWith("AVERAGE", measure.After, StringComparison.Ordinal);

            string[] RulesOn(Semanticus.Analysis.Scorecard card, string objRef) => card.Findings
                .Where(x => string.Equals(x.ObjectRef, objRef, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.RuleId).OrderBy(r => r, StringComparer.Ordinal).ToArray();
            var before = RulesOn(await f.Engine.AiReadinessScanAsync(), "column:Sales/Unit Price");
            var approved = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.True(approved.Performed, approved.Sentence);
            Assert.Equal("None", await PropertyOf(f, "column:Sales/Unit Price", "summarizeBy"));
            var after = await f.Engine.AiReadinessScanAsync();
            var added = RulesOn(after, "column:Sales/Unit Price").Except(before, StringComparer.Ordinal).ToArray();
            Assert.True(added.Length == 0, "approving Jev's row added readiness findings on Unit Price: " + string.Join(", ", added));
            // JOIN, part 2 (m11/rule-clashes, 28 Sep: SYN-FIELD counts from the start): with no linguistic schema every visible field
            // already carries SYN-FIELD beside SYN-SCHEMA's one finding, Unit Price included, so the new measure's SYN-FIELD is that
            // same standing work, not a fault of the approval (HealthProbe.StandingSynonymWork; ColumnFamiliesFixTests reads it the
            // same way). Only while there is no schema and the column itself carried it: once a schema exists the family gives the
            // measure its own synonyms (ColumnSummarizeFamily HasLinguistic), and a SYN-FIELD on it would be a real new finding.
            var noSchema = await f.Sessions.Current.ReadAsync(m => !m.Cultures.Any(c => c.ContentType == TabularEditor.TOMWrapper.ContentType.Json && !string.IsNullOrEmpty(c.Content)));
            var standing = noSchema && before.Contains("SYN-FIELD", StringComparer.Ordinal);
            var onMeasure = RulesOn(after, measure.Target).Where(r => !(standing && r == "SYN-FIELD")).ToArray();
            Assert.True(onMeasure.Length == 0, "the new measure is flagged: " + string.Join(", ", onMeasure));
        }

        [Fact]
        public async Task The_column_asks_send_the_facts_the_fair_replay_scored()
        {
            // tools/jev-replay/columns/build-calls-fair.mjs (facts()): the fair arm's derived facts for every column.
            var replayed = new[] { "is_key", "relationship_role", "used_by_measures", "table_rows", "one_to_one_partners", "used_as_sort_target", "measured" };
            using var f = await ModelAsync("VerifyReplayFacts.bim");
            var category = await f.Sessions.Current.ReadAsync(m => ColumnCategoryFamily.Prepare(m, null).Asks.First().Snapshot);
            var summarize = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null).Asks.First().Snapshot);
            foreach (var snapshot in new[] { category, summarize })
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(snapshot["column_facts"]));
                var sent = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var missing = replayed.Where(k => !sent.Contains(k)).ToArray();
                Assert.True(missing.Length == 0, "the replay's fair arm sent facts this ask does not: " + string.Join(", ", missing));
            }

            // The batched request (m11/jev-batch) sends the same facts: each column's entry carries its own, and the table's row
            // count, which every column of the table shares, goes once under table_facts (a map keyed by the table's name, m16/jev-batch).
            var batch = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Batches(ColumnSummarizeFamily.Prepare(m, null)).First().Snapshot);
            using var tableFacts = JsonDocument.Parse(JsonSerializer.Serialize(batch["table_facts"]));
            Assert.NotEmpty(tableFacts.RootElement.EnumerateObject());
            Assert.All(tableFacts.RootElement.EnumerateObject(), t => Assert.True(t.Value.TryGetProperty("table_rows", out _), "the table's row count is not in table_facts of " + t.Name));
            using var entries = JsonDocument.Parse(JsonSerializer.Serialize(batch["columns"]));
            Assert.NotEqual(0, entries.RootElement.GetArrayLength());
            foreach (var entry in entries.RootElement.EnumerateArray())
            {
                var sent = entry.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var missing = replayed.Where(k => k != "table_rows" && !sent.Contains(k)).ToArray();
                Assert.True(missing.Length == 0, "the replay's fair arm sent facts this batched entry does not: " + string.Join(", ", missing));
            }
        }
    }
}
