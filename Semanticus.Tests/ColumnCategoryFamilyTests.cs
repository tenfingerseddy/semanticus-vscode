// The column category family (T268, M4 columns; the fair replay of 26 September). For every visible text or number
// column with no data category yet, which kind of real-world thing its values hold. docs/redesign/jev-columns-replay.md
// ("Third run") is the evidence: against a key checked from each column's own data, Jev when sure, else the rule,
// beat the rule on 4,969 columns (98.3% against 97.4%, p 0.000004) and on the blind hand labels (98.7% against 96.9%).
//
// WHAT IS PROVED HERE, AND HOW.
//   Free: no category item, nothing asked, no run started, nothing measured. Counted off the oracle.
//   Pro: one item per table, "Categorize N columns in T", owned by Jev, already proposed, every line a decision on
//   column.category with an engine-built reason and check.
//   Picks come only from the options the engine lists for the column's type: a place picked for a number, and an
//   option that was never offered, end as no line.
//   Below the bar there is no Jev line at all, so the rule decides (the Pro path Kane approved): the geographic
//   rule's finding still names the column and the column is left as it was.
//   A sure "none" is no line.
//   Approve writes the kept categories as one undo step.
//   What goes to Jev: the four declared fields, names, types and derived facts only, never the column's own
//   category or any value.

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
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ColumnCategoryFamilyTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public ColumnCategoryFamilyTests(ModelTemplates templates) => _templates = templates;

        private const string Point = "column.category";

        /// <summary>Stores holds Country, Zip, Store Name, Region Label, City and Lat; Sales holds Amount. Never asked:
        /// a column that already has a category (Link), a hidden column (Internal) and a date (Opened).</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "stores", b => BuildModelAsync(b), file);
            f.UseNull();
            return f;
        }

        private static async Task BuildModelAsync(DecisionFixture f)
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
            await e.CreateColumnAsync("table:Stores", "Opened", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Stores", "Link", "String", null, "human");
            await e.SetObjectPropertyAsync("column:Stores/Link", "DataCategory", "WebUrl", "human");
            await e.CreateColumnAsync("table:Stores", "Internal", "String", null, "human");
            await e.SetColumnMetadataAsync("column:Stores/Internal", true, null, null, null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
        }

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        private static async Task<Dictionary<string, string>> DigestsAsync(DecisionFixture f)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnCategoryFamily.Prepare(m, null));
            return prepared.Asks.ToDictionary(
                a => a.TableName + "/" + a.ColumnName,
                a => new DecisionRequest(point.Name, point.SchemaVersion, a.Snapshot, a.Questions).Digest,
                StringComparer.Ordinal);
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"data_category\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Pro, permission given, answers recorded: Country (sure), Zip a postal code (sure), Lat a latitude
        /// (sure), Store Name none (sure), Region Label a city but only likely, City a city but unsure, and Amount a
        /// city (sure, and wrong for a number).</summary>
        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f, string zip = "PostalCode", string city = "City", double cityConfidence = 0.5)
        {
            f.Engine.ColumnJevDecidesForTest = p => p == "column.category" ? true : (bool?)null;   // off in the product since the 26 Sep re-test; these tests exercise it live
            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            var recorded = new RecordedDecisionOracle(Fixture(new[]
            {
                (d["Stores/Country"], "Country", 0.97),
                (d["Stores/Zip"], zip, 0.92),
                (d["Stores/Lat"], "Latitude", 0.9),
                (d["Stores/Store Name"], "none", 0.95),
                (d["Stores/Region Label"], "City", 0.7),
                (d["Stores/City"], city, cityConfidence),
                (d["Sales/Amount"], "City", 0.95),
            }), "jev-test-1");
            var capturing = new CapturingOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static async Task<string> CategoryOf(DecisionFixture f, string columnRef) =>
            (await f.Engine.GetObjectAsync(columnRef)).Properties["dataCategory"]?.ToString() ?? "";

        // ---- free -------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_free_session_asks_nothing_and_has_no_column_category_item()
        {
            using var f = await ModelAsync("ColCatFree.bim", pro: false);
            await f.ConsentAsync();
            var oracle = f.UseRecorded(Fixture(new[] { (new string('0', 64), "Country", 0.99) }));

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.SetDescriptionAsync("table:Stores", "Where we sell.", "human");
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
            Assert.Equal(0, f.Engine.FactsComputationsForTest);
        }

        // ---- Pro --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Pro_puts_one_Categorize_row_per_table_with_only_the_sure_picks()
        {
            using var f = await ModelAsync("ColCatPro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal)).ToArray();

            var stores = Assert.Single(rows);
            Assert.Equal("jev.column-category.table:Stores", stores.Id);
            Assert.Equal("Categorize 3 columns in Stores", stores.Title);
            Assert.Equal(ModelPlanSections.Meaning, stores.Section);
            Assert.Equal(ModelPlanOwners.Jev, stores.Who);
            Assert.Equal(ModelPlanItemStates.Proposed, stores.State);
            Assert.False(stores.ChangeSet.Stale, stores.ChangeSet.StaleReason);

            // Sure picks only: Store Name was a sure none, Region Label likely and City unsure, so the rule has those.
            Assert.Equal(new[] { "column:Stores/Country", "column:Stores/Lat", "column:Stores/Zip" },
                         stores.ChangeSet.Lines.Select(l => l.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            foreach (var line in stores.ChangeSet.Lines)
            {
                Assert.Equal("set_data_category", line.Op);
                Assert.Equal("dataCategory", line.Property);
                Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
                Assert.Equal(Point, line.Provenance.Point);
                Assert.Equal("sure", line.Provenance.ConfidenceWord);
                Assert.True(line.Kept);
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                Assert.DoesNotContain("\n", line.Reason, StringComparison.Ordinal);
                Assert.True(line.Check.Exists, line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", line.Check.Text, StringComparison.Ordinal);
            }
            string After(string target) => stores.ChangeSet.Lines.Single(l => l.Target == target).After;
            Assert.Equal("Country", After("column:Stores/Country"));
            Assert.Equal("PostalCode", After("column:Stores/Zip"));
            Assert.Equal("Latitude", After("column:Stores/Lat"));
        }

        [Fact]
        public async Task Approve_writes_the_kept_categories_as_one_undo_step()
        {
            using var f = await ModelAsync("ColCatApprove.bim");
            await AnswerAsync(f);
            var stores = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-category.table:Stores");

            var result = await f.Engine.ModelPlanActionAsync(stores.Id, "approve", "human", stores.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal("Country", await CategoryOf(f, "column:Stores/Country"));
            Assert.Equal("PostalCode", await CategoryOf(f, "column:Stores/Zip"));
            Assert.Equal("Latitude", await CategoryOf(f, "column:Stores/Lat"));

            await f.Engine.UndoAsync("human");
            Assert.Equal("", await CategoryOf(f, "column:Stores/Country"));
            Assert.Equal("", await CategoryOf(f, "column:Stores/Zip"));
            Assert.Equal("", await CategoryOf(f, "column:Stores/Lat"));
        }

        [Fact]
        public async Task A_second_read_changes_nothing_so_a_digest_stays_approvable()
        {
            using var f = await ModelAsync("ColCatStable.bim");
            await AnswerAsync(f);

            var first = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-category.table:Stores");
            var second = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.column-category.table:Stores");

            Assert.Equal(first.ChangeSet.Digest, second.ChangeSet.Digest);
        }

        [Fact]
        public async Task The_point_switches_off_on_its_own()
        {
            using var f = await ModelAsync("ColCatSwitch.bim");
            await AnswerAsync(f);
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-category.table:Stores");

            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-category.table:Stores");

            await f.Engine.SetDecisionPointEnabledAsync(Point, false, "human");
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal));
            Assert.DoesNotContain((await SettledPlanAsync(f)).Items, i => i.Id.StartsWith("jev.column-category.", StringComparison.Ordinal));
        }

        // ---- picks come only from the options -----------------------------------------------------------------

        [Fact]
        public async Task A_place_picked_for_a_number_is_refused_not_applied()
        {
            using var f = await ModelAsync("ColCatType.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            Assert.DoesNotContain(plan.Items, i => i.Id == "jev.column-category.table:Sales");
            Assert.Equal("", await CategoryOf(f, "column:Sales/Amount"));
        }

        [Fact]
        public async Task An_option_that_was_never_offered_is_no_line()
        {
            using var f = await ModelAsync("ColCatBogus.bim");
            await AnswerAsync(f, zip: "Galaxy");

            var stores = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-category.table:Stores");
            Assert.DoesNotContain(stores.ChangeSet.Lines, l => l.Target == "column:Stores/Zip");
            Assert.Contains(stores.ChangeSet.Lines, l => l.Target == "column:Stores/Country");
        }

        [Fact]
        public void Map_takes_only_a_sure_category_from_the_column_options()
        {
            var text = new ColumnCategoryFamily.Ask { Ref = "column:Stores/Town", ColumnName = "Town", TableRef = "table:Stores", TableName = "Stores", DataType = "String" };
            var number = new ColumnCategoryFamily.Ask { Ref = "column:Stores/Lat", ColumnName = "Lat", TableRef = "table:Stores", TableName = "Stores", DataType = "Double" };

            List<ChangeItem> Run(ColumnCategoryFamily.Ask ask, string pick, double conf)
            {
                var lines = new List<ChangeItem>();
                ColumnCategoryFamily.Map(ask, pick, conf, 0.85, lines, new List<ModelPlanChangeRefusal>());
                return lines;
            }
            Assert.Equal("City", Assert.Single(Run(text, "City", 0.9)).After);
            Assert.Empty(Run(text, "City", 0.84));          // below the bar: the rule decides
            Assert.Empty(Run(text, "none", 0.99));          // nothing to do
            Assert.Empty(Run(text, "Galaxy", 0.99));        // never offered
            Assert.Empty(Run(number, "City", 0.99));        // a place is not a number
            Assert.Equal("Latitude", Assert.Single(Run(number, "Latitude", 0.9)).After);
            Assert.DoesNotContain("City", ColumnCategoryFamily.OptionsFor("Double"));
            Assert.Contains("Latitude", ColumnCategoryFamily.OptionsFor("Double"));
            Assert.Contains("City", ColumnCategoryFamily.OptionsFor("String"));
            Assert.Empty(ColumnCategoryFamily.OptionsFor("DateTime"));
        }

        [Fact]
        public void A_link_category_needs_values_that_are_links_when_the_column_was_measured()
        {
            var ask = new ColumnCategoryFamily.Ask { Ref = "column:Stores/Site", ColumnName = "Site", TableRef = "table:Stores", TableName = "Stores", DataType = "String", UrlShare = 0 };
            var lines = new List<ChangeItem>();
            var refused = new List<ModelPlanChangeRefusal>();
            ColumnCategoryFamily.Map(ask, "WebUrl", 0.95, 0.85, lines, refused);
            Assert.Empty(lines);
            Assert.Contains(refused, r => r.Target == "column:Stores/Site");
        }

        // ---- below the bar the rule decides ---------------------------------------------------------------------

        [Fact]
        public async Task Below_the_bar_the_rule_keeps_the_column()
        {
            using var f = await ModelAsync("ColCatUnsure.bim");
            await AnswerAsync(f);
            await SettledPlanAsync(f);

            Assert.Equal("", await CategoryOf(f, "column:Stores/City"));
            var findings = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(findings.Findings, x => x.RuleId == "CAT-GEO" && (x.ObjectRef ?? "").Contains("Stores/City", StringComparison.Ordinal));
        }

        // ---- the two gaps the fair replay named (26 September) ----------------------------------------------

        private static async Task<string[]> GeoRuleTargetsAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.SingleOrDefault(i => i.Id == "safefix/Formatting");
            if (row == null) return Array.Empty<string>();
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            return proposed.Plan.Items.Single(i => i.Id == row.Id).ChangeSet.Lines
                .Where(l => l.Property == "dataCategory").Select(l => l.Target).ToArray();
        }

        [Fact]
        public async Task A_sure_none_takes_the_geographic_rules_line_off_that_column()
        {
            using (var control = await ModelAsync("ColCatNoneControl.bim"))
            {
                await AnswerAsync(control);
                await SettledPlanAsync(control);
                Assert.Contains("column:Stores/City", await GeoRuleTargetsAsync(control));   // unsure: the rule has it
            }

            using var f = await ModelAsync("ColCatNone.bim");
            await AnswerAsync(f, city: "none", cityConfidence: 0.95);
            await SettledPlanAsync(f);
            Assert.DoesNotContain("column:Stores/City", await GeoRuleTargetsAsync(f));
            // Running that row (approved with its digest) leaves the column alone: verifier D6 replaced this test's old
            // last step, which approved with no digest and so proved nothing, with
            // ColumnFamiliesVerifyTests.Category_approving_the_rule_row_after_a_sure_none_leaves_the_column_alone.
        }

        [Fact]
        public void Below_the_bar_a_measured_column_falls_back_to_the_value_share_rule_the_replay_tested()
        {
            static Semanticus.Engine.Facts.MeasuredFacts Shares(double city = 0, double postcode = 0) => new Semanticus.Engine.Facts.MeasuredFacts
            {
                Distinct = 20,
                PlaceShare = new Semanticus.Engine.Facts.PlaceShare { City = city, Postcode = postcode },
            };
            List<ChangeItem> Run(string name, Semanticus.Engine.Facts.MeasuredFacts m, string pick = "none", double conf = 0.5)
            {
                var ask = new ColumnCategoryFamily.Ask { Ref = "column:Stores/" + name, ColumnName = name, TableRef = "table:Stores", TableName = "Stores", DataType = "String", Measured = m };
                var lines = new List<ChangeItem>();
                ColumnCategoryFamily.Map(ask, pick, conf, 0.85, lines, new List<ModelPlanChangeRefusal>());
                return lines;
            }

            // Values that are 90% city names are a city, whatever the name says; the names-only rule sees nothing.
            var town = Assert.Single(Run("Town", Shares(city: 0.9)));
            Assert.Equal("City", town.After);
            Assert.Null(town.DecisionConfidence);                    // a rule line, not Jev's
            Assert.Equal(ColumnCategoryFamily.ValueRuleId, town.RuleId);
            // A zip-named column whose values are not code shaped is none (the names-only rule says PostalCode).
            Assert.Empty(Run("Zip", Shares(postcode: 0)));
            Assert.True(ColumnCategoryFamily.Decides(0.5, 0.85, measured: true));   // measured: the family decides, so the rule stands aside
            // Not measured: nothing from the family, so the geographic rule keeps the column.
            Assert.Empty(Run("Town", null));
            Assert.False(ColumnCategoryFamily.Decides(0.5, 0.85, measured: false));
            Assert.True(ColumnCategoryFamily.Decides(0.9, 0.85, measured: false));
        }

        // ---- what goes --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Only_names_types_and_derived_facts_go_to_Jev()
        {
            using var f = await ModelAsync("ColCatSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            Assert.Equal(7, sent.Length);
            var declared = DecisionRegistry.Require(Point).SnapshotFields;
            foreach (var r in sent)
            {
                Assert.Equal(declared.OrderBy(x => x, StringComparer.Ordinal), r.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                // The fair replay's facts, key for key (ColumnAskFacts.cs; verifier D4): the replay's column block, its
                // model shape and every key of its derived facts.
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["column_facts"]));
                Assert.Equal(new[] { "data_type", "is_a_calculated_column", "is_key", "measure_count_in_the_same_table", "measured",
                                     "model_tables", "one_to_one_partners", "relationship_role", "table_rows",
                                     "this_column_is_used_in_a_relationship", "used_as_sort_target", "used_by_measures" },
                             doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                // The table's other columns go by name only, as the replay sent them.
                using var others = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["table_columns"]));
                foreach (var c in others.RootElement.EnumerateArray()) Assert.Equal(JsonValueKind.String, c.ValueKind);
            }
            // Asked by nobody: a column that already has a category, a hidden column, a date.
            foreach (var name in new[] { "Link", "Internal", "Opened" })
                Assert.DoesNotContain(sent, r => JsonSerializer.Serialize(r.Snapshot["column_name"]) == "\"" + name + "\"");
        }

        [Fact]
        public void The_measured_facts_sent_are_the_replay_facts_and_nothing_else()
        {
            var measured = new Semanticus.Engine.Facts.MeasuredFacts
            {
                Distinct = 12, BlankShare = 0.1, UrlShare = 0.2, ImageUrlShare = 0, EmailShare = 0,
                PlaceShare = new Semanticus.Engine.Facts.PlaceShare { Country = 0.9, Region = 0, City = 0.1, Postcode = 0 },
            };
            // The replay's own measured block (gather-pbix.py derive_measured, less the one-to-one list and the byte counts
            // it dropped before sending), under its own key names.
            var names = ColumnAskFacts.Measured(measured).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "blankShare", "cardinality", "distinct", "emailShare", "imageUrlShare", "looksLikeDayName",
                "looksLikeMonthName", "looksLikeMonthNumber", "looksLikePeriodNumber", "looksLikeQuarter", "looksLikeYear",
                "placeShare", "sampled", "uniqueOnEveryRow", "urlShare", "wholeNumbers" }, names);
            Assert.Empty(ColumnAskFacts.Measured(null));
        }
    }
}
