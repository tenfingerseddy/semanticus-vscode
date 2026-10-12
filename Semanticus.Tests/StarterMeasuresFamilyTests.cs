// The starter measures family (T268, jev-decision-audit.md section 5.2 and X-5): for every number column on a
// fact table that no measure uses yet, whether it gets a base measure and which aggregation.
//
// WHAT IS PROVED HERE, AND HOW.
//   The templates: each option has one name, one DAX and one format rule, and the autogenerate path uses them.
//   The rule: a key or relationship column gets none; otherwise summarize-by picks, and Default means sum.
//   Free: the rule's item is on the Plan like any rule item, and Jev is never asked, counted off the oracle.
//   Pro, with the replay bar met: one Jev item per fact table, every line a decision on measure.starter, the
//   unsure ones dropped, and the rule's items stood aside.
//   A column a measure already uses gets no line. Apply creates the measures in ONE undo step.
//   What goes to Jev: the four declared fields, derived shares only, never a value.
//
// THE DIGESTS ARE COMPUTED FROM THE FAMILY'S OWN REQUEST. The recorded oracle is keyed on the whole request, so
// the test reads each column's request off StarterMeasuresFamily.Prepare, the same call the run makes, and keys
// its fixture on that. If the run ever sent a different request, no answer here would match it.

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
using ModelFacts = Semanticus.Engine.Facts.ModelFacts;
using ModelFactsData = Semanticus.Engine.Facts.ModelFactsData;
using TableFacts = Semanticus.Engine.Facts.TableFacts;
using ColumnFacts = Semanticus.Engine.Facts.ColumnFacts;
using MeasuredFacts = Semanticus.Engine.Facts.MeasuredFacts;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StarterMeasuresFamilyTests
    {
        private const string Point = "measure.starter";

        /// <summary>A star: Customer is the dimension, Sales and Returns are the facts. Sales has a key, an amount
        /// with a format, a quantity, a unit price the author set to Average, a discount set to None, and a column
        /// a measure already sums. Returns has a key and a quantity.</summary>
        private static async Task<DecisionFixture> StarAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "UnitPrice", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/UnitPrice", null, "Average", null, null, "human");
            await e.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/Discount", null, "None", null, null, "human");
            await e.CreateColumnAsync("table:Sales", "Covered", "Double", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Covered So Far", "SUM ( Sales[Covered] )", "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await e.CreateRelationshipAsync("column:Returns/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>Every request the engine sends, kept, and then answered by the recorded oracle behind it.</summary>
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

        /// <summary>The family's own request for each candidate column, keyed by the column's name.</summary>
        private static async Task<Dictionary<string, string>> DigestsAsync(DecisionFixture f)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await f.Sessions.Current.ReadAsync(m => StarterMeasuresFamily.Prepare(m, f.Engine.TryGetCurrentFacts()));
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
                  .Append(",\"usage\":{\"input_tokens\":700,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"aggregation\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Pro, permission given, the replay bar met for this engine, and Jev's answers recorded: a
        /// distinct count for the key (sure), a total for the amount (sure), a total for the quantity (likely),
        /// an average for the unit price (unsure), none for the discount, and a total for the returned quantity.</summary>
        private static async Task<CapturingOracle> JevAnswersAsync(DecisionFixture f)
        {
            f.Engine.StarterJevDecidesForTest = true;
            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            var recorded = new RecordedDecisionOracle(Fixture(new[]
            {
                (d["Sales/CustomerKey"], "distinctcount", 0.9),
                (d["Sales/Amount"], "sum", 0.95),
                (d["Sales/Quantity"], "sum", 0.7),
                (d["Sales/UnitPrice"], "average", 0.4),
                (d["Sales/Discount"], "none", 0.9),
                (d["Returns/Qty"], "sum", 0.92),
                (d["Returns/CustomerKey"], "none", 0.9),
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

        private static async Task<string[]> MeasureNamesAsync(DecisionFixture f) =>
            (await f.Engine.ListMeasuresAsync()).Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // ---- the templates and the rule -----------------------------------------------------------------------

        [Fact]
        public void Each_option_maps_to_one_name_one_formula_and_one_format_rule()
        {
            var expected = new (string Option, string Name, string Dax, string Format)[]
            {
                ("sum", "Total Amount", "SUM ( 'Sales'[Amount] )", "#,0.00"),
                ("average", "Average Amount", "AVERAGE ( 'Sales'[Amount] )", "#,0.00"),
                ("min", "Minimum Amount", "MIN ( 'Sales'[Amount] )", "#,0.00"),
                ("max", "Maximum Amount", "MAX ( 'Sales'[Amount] )", "#,0.00"),
                ("count", "Count of Amount", "COUNT ( 'Sales'[Amount] )", "#,0"),
                ("distinctcount", "Distinct Count of Amount", "DISTINCTCOUNT ( 'Sales'[Amount] )", "#,0"),
            };
            foreach (var x in expected)
            {
                Assert.Equal(x.Name, StarterMeasureTemplates.Name(x.Option, "Amount"));
                Assert.Equal(x.Dax, StarterMeasureTemplates.Dax(x.Option, "Sales", "Amount"));
                Assert.Equal(x.Format, StarterMeasureTemplates.Format(x.Option, "#,0.00"));
            }
            // Every option but a count inherits the column's format, and has none when the column has none.
            Assert.Null(StarterMeasureTemplates.Format("sum", null));
            Assert.Equal("#,0", StarterMeasureTemplates.Format("count", null));
            // None makes nothing.
            Assert.Null(StarterMeasureTemplates.Name("none", "Amount"));
            Assert.Null(StarterMeasureTemplates.Dax("none", "Sales", "Amount"));
            // The options are the registry's, in the registry's order, so the request and the templates agree.
            Assert.Equal(DecisionRegistry.StarterAggregations, StarterMeasureTemplates.Options);
            // A quote in a table name and a closing bracket in a column name are escaped, not broken.
            Assert.Equal("SUM ( 'Bob''s Sales'[Net ]]Amount] )", StarterMeasureTemplates.Dax("sum", "Bob's Sales", "Net ]Amount"));
        }

        [Fact]
        public void Key_columns_get_none_under_the_rule_and_summarize_by_picks_otherwise()
        {
            Assert.Equal("none", StarterMeasureTemplates.RuleChoice(true, "none", "Sum"));
            Assert.Equal("none", StarterMeasureTemplates.RuleChoice(false, "many", "Sum"));
            Assert.Equal("none", StarterMeasureTemplates.RuleChoice(false, "one", "Default"));
            Assert.Equal("sum", StarterMeasureTemplates.RuleChoice(false, "none", "Default"));
            Assert.Equal("sum", StarterMeasureTemplates.RuleChoice(false, "none", "Sum"));
            Assert.Equal("average", StarterMeasureTemplates.RuleChoice(false, "none", "Average"));
            Assert.Equal("min", StarterMeasureTemplates.RuleChoice(false, "none", "Min"));
            Assert.Equal("max", StarterMeasureTemplates.RuleChoice(false, "none", "Max"));
            Assert.Equal("count", StarterMeasureTemplates.RuleChoice(false, "none", "Count"));
            Assert.Equal("distinctcount", StarterMeasureTemplates.RuleChoice(false, "none", "DistinctCount"));
            Assert.Equal("none", StarterMeasureTemplates.RuleChoice(false, "none", "None"));
        }

        [Fact]
        public async Task The_spec_autogenerate_path_uses_the_same_templates_and_rule()
        {
            using var f = await StarAsync("StarterSpec.bim", pro: false);

            var spec = (await f.Engine.AutogenerateSpecFromModelAsync("human")).Spec;
            var proposed = spec.Measures.Where(x => x.DisplayFolder == "Base Measures").ToArray();

            var total = proposed.Single(x => x.Name == "Total Amount");
            Assert.Equal("SUM ( 'Sales'[Amount] )", total.Dax);
            Assert.Equal("#,0.00", total.FormatString);
            // The author said the unit price averages, so the starter is an average, not a total.
            var average = proposed.Single(x => x.Name == "Average UnitPrice");
            Assert.Equal("AVERAGE ( 'Sales'[UnitPrice] )", average.Dax);
            // A foreign key is never totalled, and a column set to None gets nothing.
            Assert.DoesNotContain(proposed, x => x.Name.Contains("CustomerKey", StringComparison.Ordinal));
            Assert.DoesNotContain(proposed, x => x.Name.Contains("Discount", StringComparison.Ordinal));
            // Only the totals are time-intelligence bases.
            Assert.Contains("Total Amount", spec.TimeIntelligenceBaseMeasures);
            Assert.DoesNotContain("Average UnitPrice", spec.TimeIntelligenceBaseMeasures);
        }

        /// <summary>The rule's starter rows standing aside once Jev decides the starter measures is not their work being
        /// done (review round 3, the sibling of the date table's DT4): neither the Done register nor the History's
        /// sentence may count them as done.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_rule_rows_standing_aside_for_Jev_are_not_told_as_done_in_History()
        {
            using var f = await StarAsync("StarterAsideHistory.bim");
            f.Engine.StarterJevDecidesForTest = false;
            var first = await f.Engine.GetModelPlanAsync();
            Assert.Contains(first.Items, i => i.Id == "starter/table:Sales");

            await JevAnswersAsync(f);
            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(plan.Items, i => StarterMeasuresFamily.IsRuleItem(i.Id));
            var entry = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            Assert.Contains("Add 3 simple measures in Sales", entry.Gone);
            Assert.Empty(entry.Completed);
            Assert.DoesNotContain("done", entry.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(plan.Items, i => i.Status == ModelPlanStatuses.Done && StarterMeasuresFamily.IsRuleItem(i.Id));
        }

        /// <summary>The starter half of the stand-aside rule (L2 of the joint verifier's plan-waits check, adopted from its
        /// probe): a starter row stands aside only while the gap it names is still there. The person makes Sales' starter
        /// measures in the same window where Jev starts deciding them, so Sales' row left because its work is done, and only
        /// Returns' row stood aside. Read off the flag alone, both would be told as replaced by a suggestion.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_starter_row_the_person_finished_while_Jev_starts_deciding_is_told_as_done_not_replaced()
        {
            using var f = await StarAsync("StarterPersonAside.bim");
            f.Engine.StarterJevDecidesForTest = false;
            var first = await f.Engine.GetModelPlanAsync();
            var sales = first.Items.Single(i => i.Id == "starter/table:Sales");
            var returns = first.Items.Single(i => i.Id == "starter/table:Returns");
            foreach (var d in sales.Diff)
            {
                // "+ <measure>: from <column>"
                var body = d.Substring(2);
                var at = body.LastIndexOf(": from ", StringComparison.Ordinal);
                await f.Engine.CreateMeasureAsync("table:Sales", body.Substring(0, at), "SUM ( Sales[" + body.Substring(at + 7) + "] )", "human");
            }
            // Jev starts deciding the starter measures in the same window: the bar met, permission given, a service there.
            f.Engine.StarterJevDecidesForTest = true;
            await f.ConsentAsync();
            f.UseRecorded(Fixture(new[] { (new string('0', 64), "sum", 0.99) }));
            var plan = await f.Engine.GetModelPlanAsync();

            var entry = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            var told = entry.Sentence + " | gone: " + string.Join(", ", entry.Gone ?? Array.Empty<string>())
                     + " | completed: " + string.Join(", ", entry.Completed ?? Array.Empty<string>());
            Assert.DoesNotContain(plan.Items, i => i.Id == "starter/table:Sales" && i.Status != ModelPlanStatuses.Done);
            // Sales' gap is gone, so its row is done; Returns' gap is open, so its row stands aside for Jev.
            Assert.True((entry.Completed ?? Array.Empty<string>()).Contains(sales.Title), told);
            Assert.False((entry.Completed ?? Array.Empty<string>()).Contains(returns.Title), told);
            Assert.True(entry.Sentence.Contains("1 task replaced by a suggestion", StringComparison.Ordinal), told);
        }

        // ---- free ---------------------------------------------------------------------------------------------

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_free_session_composes_no_Jev_item_calls_no_oracle_and_sees_the_rule_item()
        {
            using var f = await StarAsync("StarterFree.bim", pro: false);
            // The bar met and permission given, so a zero below is the free gate and nothing else.
            f.Engine.StarterJevDecidesForTest = true;
            await f.ConsentAsync();
            var oracle = f.UseRecorded(Fixture(new[] { (new string('0', 64), "sum", 0.99) }));

            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.", StringComparison.Ordinal));
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);

            // The rule's items, one per fact table, like every other rule item a free session sees.
            var starter = plan.Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal))
                              .OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "starter/table:Returns", "starter/table:Sales" }, starter.Select(i => i.Id).ToArray());
            var sales = starter.Single(i => i.Id == "starter/table:Sales");
            Assert.Equal(ModelPlanOwners.Auto, sales.Who);
            Assert.Equal(ModelPlanSections.Meaning, sales.Section);
            Assert.Equal("Add 3 simple measures in Sales", sales.Title);

            // Exactly like every other rule item: free to read and to work out, Pro to run.
            await f.Engine.ModelPlanActionAsync(sales.Id, "propose", "human");
            var approve = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human");
            Assert.False(approve.Performed);
            Assert.StartsWith("Running the plan is part of Semanticus Pro.", approve.Sentence, StringComparison.Ordinal);
        }

        // ---- Pro ----------------------------------------------------------------------------------------------

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Pro_composes_one_Jev_item_per_fact_table_with_the_recorded_oracle()
        {
            using var f = await StarAsync("StarterPro.bim");
            var oracle = await JevAnswersAsync(f);

            var plan = await SettledPlanAsync(f);
            var jev = plan.Items.Where(i => i.Id.StartsWith("jev.starter.", StringComparison.Ordinal))
                          .OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "jev.starter.table:Returns", "jev.starter.table:Sales" }, jev.Select(i => i.Id).ToArray());
            // The rule's items stand aside while Jev decides; there is one proposal per measure, not two.
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("starter/", StringComparison.Ordinal));
            // One ask per candidate column: seven, across two fact tables. Customer is a dimension; Covered is used.
            Assert.Equal(7, oracle.Seen.Count(r => r.PointName == Point));

            var sales = jev.Single(i => i.Id == "jev.starter.table:Sales");
            Assert.Equal(ModelPlanOwners.Jev, sales.Who);
            Assert.Equal(ModelPlanSections.Meaning, sales.Section);
            Assert.Equal("Add 4 simple measures in Sales", sales.Title);
            Assert.Equal("Add 1 simple measure in Returns", jev.Single(i => i.Id == "jev.starter.table:Returns").Title);

            foreach (var line in jev.SelectMany(i => i.ChangeSet.Lines))
            {
                Assert.Contains(line.Op, new[] { "create_measure", "set_measure_format" });
                Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
                Assert.Equal(Point, line.Provenance.Point);
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                Assert.True(line.Check?.Exists == true, line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
            }

            // Jev picked the option; the engine built the name, the formula and the format.
            var key = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Distinct Count of CustomerKey" && l.Op == "create_measure");
            Assert.Equal("DISTINCTCOUNT ( 'Sales'[CustomerKey] )", key.After);
            Assert.Equal("#,0", sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Distinct Count of CustomerKey" && l.Op == "set_measure_format").After);
            Assert.Equal("#,0.00", sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Amount" && l.Op == "set_measure_format").After);
            Assert.Equal("likely", sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Total Quantity").Provenance.ConfidenceWord);
            // None proposes nothing.
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target.Contains("Discount", StringComparison.Ordinal));
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task An_unsure_line_arrives_dropped()
        {
            using var f = await StarAsync("StarterUnsure.bim");
            await JevAnswersAsync(f);

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == "jev.starter.table:Sales");
            var unsure = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Average UnitPrice");

            Assert.Equal("unsure", unsure.Provenance.ConfidenceWord);
            Assert.False(unsure.Kept);
            Assert.All(sales.ChangeSet.Lines.Where(l => l.Target != "measure:Sales/Average UnitPrice"), l => Assert.True(l.Kept, l.Id));
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_column_a_measure_already_uses_gets_no_line()
        {
            using var f = await StarAsync("StarterCovered.bim");
            var oracle = await JevAnswersAsync(f);

            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(plan.Items.SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()),
                l => l.Target.Contains("Covered", StringComparison.Ordinal) && l.Op == "create_measure");
            Assert.DoesNotContain(oracle.Seen.Where(r => r.PointName == Point), r => JsonSerializer.Serialize(r.Snapshot["column_name"]) == "\"Covered\"");

            // The rule agrees, on the model itself.
            var summary = await f.Sessions.Current.ReadAsync(m => StarterMeasuresFamily.RuleSummary(m));
            Assert.DoesNotContain(summary.SelectMany(t => t.Measures), x => x.Column == "Covered");
            // Adding a measure over a column takes it out of the candidates.
            await f.Engine.CreateMeasureAsync("table:Returns", "Returned", "SUM ( Returns[Qty] )", "human");
            var after = await f.Sessions.Current.ReadAsync(m => StarterMeasuresFamily.RuleSummary(m));
            Assert.DoesNotContain(after, t => t.Table == "Returns");
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Key_columns_get_no_rule_line_and_every_rule_line_names_its_rule()
        {
            using var f = await StarAsync("StarterRuleKeys.bim", pro: false);
            var plan = await f.Engine.GetModelPlanAsync();
            var sales = plan.Items.Single(i => i.Id == "starter/table:Sales");

            var proposed = await f.Engine.ModelPlanActionAsync(sales.Id, "propose", "human");

            var lines = proposed.Plan.Items.Single(i => i.Id == sales.Id).ChangeSet.Lines;
            Assert.DoesNotContain(lines, l => l.Target.Contains("CustomerKey", StringComparison.Ordinal));
            Assert.Equal(new[] { "measure:Sales/Average UnitPrice", "measure:Sales/Total Amount", "measure:Sales/Total Quantity" },
                lines.Where(l => l.Op == "create_measure").Select(l => l.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            Assert.All(lines, l =>
            {
                Assert.Equal(ModelPlanChangeKinds.Rule, l.Provenance.Kind);
                Assert.Equal(StarterMeasuresFamily.RuleId, l.Provenance.Rule);
                Assert.Null(l.Provenance.Confidence);
                Assert.True(l.Kept, l.Id);
            });
        }

        // ---- one owner per column --------------------------------------------------------------------------

        /// <summary>The old "Add N explicit measures" item (DAC-IMPLICIT-MEASURE) and the starter family must not
        /// both propose a measure for the same column: the starter family owns every number column on a fact table
        /// that no measure uses yet. The old item keeps the columns outside that, here a dimension's number column.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_old_explicit_measures_item_leaves_every_starter_column_to_the_starter_family()
        {
            using var f = await StarAsync("StarterOneOwner.bim", pro: false);
            await f.Engine.CreateColumnAsync("table:Customer", "CreditLimit", "Double", null, "human");
            await f.SaveAsAsync("StarterOneOwner.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var starterColumns = await f.Sessions.Current.ReadAsync(m =>
                StarterMeasureTemplates.Candidates(m).SelectMany(x => x.Columns).Select(c => c.Table.Name + "[" + c.Name + "]").ToArray());
            Assert.NotEmpty(starterColumns);
            var old = plan.Items.SingleOrDefault(i => i.Id == "proposal/implicit-measures");

            Assert.NotNull(old);   // the dimension column is still the old item's
            var oldText = string.Join("\n", old.Diff.Concat(new[] { old.Sub }));
            foreach (var column in starterColumns)
                Assert.DoesNotContain(column, oldText, StringComparison.Ordinal);
            Assert.Contains("CreditLimit", oldText, StringComparison.Ordinal);
            // What the old item keeps: a dimension's number column, and a fact column that already has a measure
            // but still totals by default. Neither is a starter column.
            Assert.Equal(new[] { "+ Customer[CreditLimit]", "+ Sales[Covered]" }, old.Diff.Select(d => d.Split(':')[0]).ToArray());
            Assert.Equal("Add 2 explicit measures", old.Title);
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_old_explicit_measures_item_goes_when_the_starter_family_covers_every_column()
        {
            using var f = await StarAsync("StarterOnlyOwner.bim", pro: false);
            // The one fact column outside the starter family stops totalling by default, so every column the old
            // item would name is now a starter column.
            await f.Engine.SetColumnMetadataAsync("column:Sales/Covered", null, "None", null, null, "human");
            await f.SaveAsAsync("StarterOnlyOwner.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            Assert.Contains(plan.Items, i => i.Id.StartsWith("starter/", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.Items, i => i.Id == "proposal/implicit-measures");
        }

        // ---- apply --------------------------------------------------------------------------------------------

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Approving_the_rule_item_creates_the_measures_in_one_undo_step()
        {
            // Pro with no permission given: the rule decides, and approving a rule item is Pro like every run.
            using var f = await StarAsync("StarterApprove.bim");
            var before = await MeasureNamesAsync(f);
            var plan = await f.Engine.GetModelPlanAsync();
            var proposed = await f.Engine.ModelPlanActionAsync("starter/table:Sales", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "starter/table:Sales").ChangeSet;
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.ModelPlanActionAsync("starter/table:Sales", "approve", "human", set.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal(revision + 1, f.Sessions.Current.Revision);
            var total = await f.Engine.GetObjectAsync("measure:Sales/Total Amount");
            Assert.Equal("SUM ( 'Sales'[Amount] )", total.Properties["expression"]?.ToString());
            Assert.Equal("#,0.00", total.Properties["formatString"]?.ToString());
            Assert.Contains("Average UnitPrice", await MeasureNamesAsync(f));

            await f.Engine.UndoAsync("human");

            Assert.Equal(before, await MeasureNamesAsync(f));
        }

        /// <summary>N10: start on the rule row refused with "Nothing in this release runs this one for you yet" while
        /// Approve and Run the plan applied it. Start now applies it through the same approve path: one undo step.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Start_on_the_rule_item_applies_it_through_the_approve_path()
        {
            using var f = await StarAsync("StarterStart.bim");
            var before = await MeasureNamesAsync(f);
            await f.Engine.GetModelPlanAsync();
            var revision = f.Sessions.Current.Revision;

            var result = await McpTools.ModelPlanAction(f.Engine, "starter/table:Sales", "start");

            Assert.True(result.Performed, result.Sentence);
            Assert.DoesNotContain("Nothing in this release", result.Sentence ?? "", StringComparison.Ordinal);
            Assert.Equal(revision + 1, f.Sessions.Current.Revision);
            Assert.Contains("Total Amount", await MeasureNamesAsync(f));

            await f.Engine.UndoAsync("human");
            Assert.Equal(before, await MeasureNamesAsync(f));
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Bulk_apply_creates_every_fact_tables_measures_in_one_undo_step()
        {
            using var f = await StarAsync("StarterBulk.bim");
            await JevAnswersAsync(f);
            var before = await MeasureNamesAsync(f);
            var plan = await SettledPlanAsync(f);
            var items = plan.Items.Where(i => i.Id.StartsWith("jev.starter.", StringComparison.Ordinal))
                            .Select(i => new PlanItemDigest { Id = i.Id, Digest = i.ChangeSet.Digest }).ToArray();
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(items, "human");

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal(revision + 1, result.Revision);
            var names = await MeasureNamesAsync(f);
            Assert.Contains("Distinct Count of CustomerKey", names);
            Assert.Contains("Total Amount", names);
            Assert.Contains("Total Quantity", names);
            Assert.Contains("Total Qty", names);
            Assert.DoesNotContain("Average UnitPrice", names);   // unsure, so dropped, so not written

            await f.Engine.UndoAsync("human");

            Assert.Equal(before, await MeasureNamesAsync(f));
        }

        [Fact]
        public async Task A_name_taken_since_the_line_was_made_is_refused_in_a_plain_sentence()
        {
            using var f = await StarAsync("StarterTaken.bim");
            await f.Engine.GetModelPlanAsync();
            var proposed = await f.Engine.ModelPlanActionAsync("starter/table:Returns", "propose", "human");
            var line = proposed.Plan.Items.Single(i => i.Id == "starter/table:Returns").ChangeSet.Lines.Single(l => l.Op == "create_measure");
            // Somebody else takes the name on another table before the line is applied.
            await f.Engine.CreateMeasureAsync("table:Customer", "Total Qty", "1", "human");

            var report = await f.Engine.ApplyPlanAsync(new[] { line.PlanItemId }, "human");
            var item = report.Items.Single(i => i.Id == line.PlanItemId);

            Assert.Equal("failed", item.Status);
            Assert.Contains("Total Qty already exists", item.Note, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", item.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(await f.Engine.ListMeasuresAsync(), x => x.Name.StartsWith("Total Qty ", StringComparison.Ordinal));
        }

        // ---- what goes to Jev ---------------------------------------------------------------------------------

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_facts_sent_are_the_declared_fields_and_derived_shares_never_a_value()
        {
            using var f = await StarAsync("StarterPrivacy.bim");
            var oracle = await JevAnswersAsync(f);
            await SettledPlanAsync(f);

            var point = DecisionRegistry.Require(Point);
            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            Assert.NotEmpty(sent);
            var columnFactKeys = new[] { "data_type", "format_string", "is_calculated", "is_hidden", "is_key", "measured", "relationship_role", "summarize_by" };
            foreach (var request in sent)
            {
                Assert.Equal(point.SnapshotFields.OrderBy(x => x, StringComparer.Ordinal), request.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                var facts = JsonSerializer.SerializeToElement(request.Snapshot["column_facts"]);
                Assert.Equal(columnFactKeys, facts.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
            }

            // With data measured, only shares go: the counts behind them do not, and nothing that is a value does.
            var measured = new ModelFacts
            {
                Data = ModelFactsData.Local,
                Tables = new[]
                {
                    new TableFacts
                    {
                        Ref = "table:Sales", Name = "Sales", Rows = 8123,
                        Columns = new[]
                        {
                            new ColumnFacts
                            {
                                Ref = "column:Sales/Amount", Name = "Amount", DataType = "Double",
                                Measured = new MeasuredFacts { Distinct = 2031, BlankShare = 0.0125, WholeNumbers = false },
                            },
                        },
                    },
                },
            };
            var snapshot = await f.Sessions.Current.ReadAsync(m =>
            {
                var sales = m.Tables["Sales"];
                return StarterMeasuresFamily.Snapshot(sales, sales.Columns["Amount"], "none", measured, point);
            });
            var json = DecisionDigest.CanonicalSnapshotJson(snapshot);
            var block = JsonSerializer.SerializeToElement(snapshot["column_facts"]).GetProperty("measured");
            Assert.Equal(new[] { "blank_share", "distinct_share", "sampled", "unique_on_every_row", "whole_numbers" },
                block.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(0.25, block.GetProperty("distinct_share").GetDouble());
            Assert.DoesNotContain("8123", json, StringComparison.Ordinal);
            Assert.DoesNotContain("2031", json, StringComparison.Ordinal);
            foreach (var word in new[] { "min", "max", "sample\"", "values", "rows" })
                Assert.DoesNotContain("\"" + word, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
