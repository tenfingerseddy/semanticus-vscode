// The fixes for the verifier's round 3 FAIL on m11/m4-columns-fair (27 September): the edges of each fix that
// ColumnFamiliesVerifyR3Tests does not already pin. Each test states one requirement.
//
//   D1/D2  An existing measure is found by what it computes, and only then: a filter, another table, an expression or
//          an iterator over a filtered table is a different measure and never blocks a new one.
//   D3     A name something else in the model already has, with the table's name tried too, leaves the column exactly
//          as it is (no "don't add up" with no measure) and says so.
//   D3     A table-named measure teaches the model's naming without its table, so the next table's name is its own.
//   D5/D6  The lines follow the measure both ways: keeping the hide line after dropping the measure brings back the
//          whole group the drop took (the measure, its format and description, the hide and the Excel line).
//   D7     A NEW column taken out of Excel reaches the live model too, instead of being refused as "cannot create".
//
// Then Sol's review of the fix (27 September), one test per finding:
//   S1  A line worked out again after the model moved never comes back kept while the line it needs stays dropped.
//   S2  apply_plan with only the hide and Excel lines named applies neither: they need the measure.
//   S3  A live push never turns Excel/MDX off on a live column that a live hierarchy still uses.
//   S4  Two different aggregations of two columns never get the same measure name.
//   S5  A measure named like its own table and column ("Total Cost" in table Total) does not teach a bare name.
//
// And the coordinator's final batch (27 September):
//   F1  A row's title counts only the lines still kept: drop one of two measures and it says "Add 1 measure".
//   F2  No line title speaks engine words ("MDX", "Summarize by None").
//   F3  When the model's pattern already says Avg, a column named "Avg ..." does not get it twice, as long as the
//       plainer name is still unique.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    public sealed class ColumnFamiliesFixR4Tests
    {
        private const string Summarize = "column.summarize";
        private const string SalesRow = "jev.column-summarize.table:Sales";

        // ---- fixtures (the verifier's shape: Jev sure of every pick by column name) --------------------------------

        private sealed class ByName : IDecisionOracle
        {
            private readonly Dictionary<string, (string Choice, double Confidence)> _answers;
            public ByName(Dictionary<string, (string, double)> answers) { _answers = answers; }
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PerQuestion)   // column.summarize asks a table's columns at once (m11/jev-batch)
                    return BatchedColumnAnswers.Compose(request, (plain, col) => _answers.TryGetValue(col, out var a) ? a : ("sum", 0.5));
                var column = JsonSerializer.Serialize(request.Snapshot["column_name"]).Trim('"');
                var question = request.Questions[0].Name;
                var (choice, confidence) = _answers.TryGetValue(column, out var a) ? a : ("sum", 0.5);
                var fixture = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":500,\"output_tokens\":20},\"answers\":{" + JsonSerializer.Serialize(question)
                    + ":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(choice) + ",\"confidence\":"
                    + confidence.ToString(CultureInfo.InvariantCulture) + "}}}]}";
                return await new RecordedDecisionOracle(fixture, "jev-test-1").AskAsync(request, ct).ConfigureAwait(false);
            }
        }

        private static async Task AnswerAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != Summarize))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            var answers = new Dictionary<string, (string, double)>(StringComparer.Ordinal)
            {
                ["Amount"] = ("sum", 0.95), ["Quantity"] = ("sum", 0.95),
            };
            f.Engine.UseDecisionsForTest(new DecisionService(new ByName(answers), new DecisionBudget()));
        }

        private static async Task AnswerAsync(DecisionFixture f, Dictionary<string, (string, double)> answers)
        {
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != Summarize))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(new ByName(answers), new DecisionBudget()));
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static async Task<DecisionFixture> SalesAsync(string file, Func<LocalEngine, Task> extra = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            if (extra != null) await extra(e);
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static ModelPlanItem Row(ModelPlanView plan, string id) => plan.Items.SingleOrDefault(i => i.Id == id);

        private static async Task<string> Prop(DecisionFixture f, string objRef, string property) =>
            (await f.Engine.GetObjectAsync(objRef)).Properties[property]?.ToString() ?? "";

        // ---- D1/D2: by meaning, and only by meaning -----------------------------------------------------------------

        [Theory]
        [InlineData("SUM ( 'Sales'[Quantity] ) // units sold", "SUM")]
        [InlineData("/* units */ sum(Sales[Quantity])", "SUM")]
        [InlineData("CALCULATE ( SUM ( Sales[Quantity] ) )", "SUM")]
        [InlineData("( ( SUM ( Sales[Quantity] ) ) )", "SUM")]
        [InlineData("SUMX ( Sales, Sales[Quantity] )", "SUM")]
        [InlineData("sumx('Sales', [Quantity])", "SUM")]
        [InlineData("AVERAGEX ( Sales, 'Sales'[Quantity] )", "AVERAGE")]
        [InlineData("SUM ( [Quantity] )", "SUM")]
        public void The_same_total_is_the_same_measure_however_it_is_written(string dax, string function)
        {
            var meaning = BaseMeasures.Meaning(dax, "Sales");
            Assert.NotNull(meaning);
            Assert.Equal(function, meaning.Value.Function);
            Assert.Equal("Sales", meaning.Value.Table, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Quantity", meaning.Value.Column, StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), Sales[Region] = \"West\" )")]
        [InlineData("SUMX ( Returns, Sales[Quantity] )")]
        [InlineData("SUMX ( Sales, Sales[Quantity] * Sales[Price] )")]
        [InlineData("SUMX ( FILTER ( Sales, Sales[Quantity] > 0 ), Sales[Quantity] )")]
        [InlineData("SUM ( Sales[Quantity] ) + 1")]
        [InlineData("SUM ( Sales[Quantity] ) - SUM ( Sales[Returned] )")]
        [InlineData("VAR q = SUM ( Sales[Quantity] ) RETURN q * 2")]
        [InlineData("")]
        public void A_different_calculation_is_never_taken_for_the_plain_total(string dax)
        {
            Assert.Null(BaseMeasures.Meaning(dax, "Sales"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_filtered_total_does_not_stop_the_plain_base_measure()
        {
            using var f = await SalesAsync("R4Filtered.bim", async e =>
                await e.CreateMeasureAsync("table:Sales", "Big Orders", "CALCULATE ( SUM ( Sales[Quantity] ), Sales[Amount] > 100 )", "human"));
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.Contains(sales.ChangeSet.Lines, l => l.Op == "create_measure" && (l.After ?? "").Contains("'Sales'[Quantity]", StringComparison.Ordinal));
        }

        // ---- D3: a name that is not free, even with the table's name -----------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_measure_whose_name_is_taken_everywhere_leaves_its_column_as_it_is_and_says_so()
        {
            using var f = await SalesAsync("R4Taken.bim", async e =>
            {
                await e.CreateMeasureAsync("table:Sales", "Total Quantity", "SUM ( Sales[Amount] ) * 2", "human");
                await e.CreateMeasureAsync("table:Sales", "Total Sales Quantity", "SUM ( Sales[Amount] ) * 3", "human");
            });
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Quantity");
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Quantity"
                && r.Reason.Contains("already named", StringComparison.Ordinal) && r.Reason.Contains("keeps adding up by itself", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_name_taken_by_another_calculation_gets_the_tables_name_instead()
        {
            using var f = await SalesAsync("R4Qualified.bim", async e =>
                await e.CreateMeasureAsync("table:Sales", "Total Quantity", "SUM ( Sales[Amount] ) * 2", "human"));
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.Contains(sales.ChangeSet.Lines, l => l.Op == "create_measure" && l.Target == "measure:Sales/Total Sales Quantity");
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_named_measure_teaches_the_pattern_without_its_table()
        {
            using var f = await SalesAsync("R4Pattern.bim", async e =>
                await e.CreateMeasureAsync("table:Sales", "Total Sales Quantity", "SUM ( Sales[Quantity] )", "human"));
            var names = await f.Sessions.Current.ReadAsync(m => BaseMeasures.Read(m));
            Assert.Equal("Total Amount", names.NameFor("sum", "Amount"));
            Assert.Equal("Total Returns Quantity", names.NameFor("sum", "Quantity", "Returns"));
        }

        // ---- D5/D6: the lines follow the measure, both ways --------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Keeping_the_hide_line_after_dropping_the_measure_keeps_the_measure_too()
        {
            using var f = await SalesAsync("R4KeepBack.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var create = sales.ChangeSet.Lines.Single(l => l.Op == "create_measure" && l.Target == "measure:Sales/Total Quantity");
            await f.Engine.SetPlanItemAsync(create.PlanItemId, null, approved: false, "human");
            var dropped = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            Assert.All(dropped.ChangeSet.Lines.Where(l => l.Target == "measure:Sales/Total Quantity"
                                                           || (l.Target == "column:Sales/Quantity" && l.Op != "set_summarize_by")),
                       l => Assert.False(l.Kept, l.Op + " on " + l.Target + " is still kept"));
            Assert.Contains(dropped.ChangeSet.Lines, l => l.Target == "column:Sales/Quantity" && l.Op == "set_summarize_by" && l.Kept);

            var hide = dropped.ChangeSet.Lines.Single(l => l.Target == "column:Sales/Quantity" && l.Op == "set_column_hidden");
            await f.Engine.SetPlanItemAsync(hide.PlanItemId, null, approved: true, "human");
            var back = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            // The keep undoes the drop: the measure, its format and description, and the Excel line all come back.
            Assert.All(back.ChangeSet.Lines.Where(l => l.Target == "measure:Sales/Total Quantity" || l.Target == "column:Sales/Quantity"),
                       l => Assert.True(l.Kept, l.Op + " on " + l.Target + " is not kept"));
            Assert.True((await f.Engine.ModelPlanActionAsync(back.Id, "approve", "human", back.ChangeSet.Digest)).Performed);
            Assert.Equal("True", await Prop(f, "column:Sales/Quantity", "isHidden"));
            Assert.Contains(await f.Engine.ListMeasuresAsync(), m => m.Name == "Total Quantity");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_dropped_line_is_said_to_be_dropped_in_plain_words()
        {
            using var f = await SalesAsync("R4DropWords.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var hide = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/Amount" && l.Op == "set_column_hidden");
            await f.Engine.SetPlanItemAsync(hide.PlanItemId, null, approved: false, "human");
            var after = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            // The hide and the Excel line that needs it: two lines, both dropped by the person.
            Assert.Contains(after.Why, w => w == "2 changes are unticked, so running the plan leaves them out. Tick them to include them.");
        }

        // ---- D7: a new column out of Excel reaches the live model ----------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_new_column_taken_out_of_Excel_is_created_live_with_that_setting()
        {
            static TOM.Model Model()
            {
                var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
                var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
                t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                t.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Int64, LineageTag = "tag-amount" });
                db.Model.Tables.Add(t);
                return db.Model;
            }
            var live = Model();
            var src = Model();
            src.Tables["Sales"].Columns.Add(new TOM.DataColumn { Name = "Qty", SourceColumn = "Qty", DataType = TOM.DataType.Int64, LineageTag = "tag-qty", IsHidden = true, IsAvailableInMDX = false });
            src.Tables["Sales"].Columns.Add(new TOM.CalculatedColumn { Name = "Twice", Expression = "Sales[Amount] * 2", LineageTag = "tag-twice", IsHidden = true, IsAvailableInMDX = false });

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.True(rep.Unmatched.Length == 0, string.Join(" | ", rep.Unmatched));
            Assert.False(live.Tables["Sales"].Columns["Qty"].IsAvailableInMDX);
            Assert.False(live.Tables["Sales"].Columns["Twice"].IsAvailableInMDX);
            Assert.Equal(0, LiveDeploy.SyncModels(src, live, apply: false).TotalChanges);
        }

        // ---- Sol's review: S1 to S5 --------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S1_a_line_worked_out_again_never_comes_back_kept_while_the_line_it_needs_is_dropped()
        {
            using var f = await SalesAsync("R4Resync.bim", async e =>
                await e.SetColumnMetadataAsync("column:Sales/Quantity", null, null, null, "Amount", "human"));
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Quantity" && l.Op == "set_is_available_in_mdx");
            var create = sales.ChangeSet.Lines.Single(l => l.Op == "create_measure" && l.Target == "measure:Sales/Total Quantity");
            await f.Engine.SetPlanItemAsync(create.PlanItemId, null, approved: false, "human");

            // The sort-by goes, so the Excel line can now be offered: it arrives needing a hide that is dropped.
            await f.Engine.SetColumnMetadataAsync("column:Sales/Quantity", null, null, null, "", "human");
            var again = Row(await SettledPlanAsync(f), SalesRow);
            var mdx = again.ChangeSet.Lines.SingleOrDefault(l => l.Target == "column:Sales/Quantity" && l.Op == "set_is_available_in_mdx");
            Assert.NotNull(mdx);
            Assert.False(mdx.Kept, "the Excel line came back kept while its hide and measure stay dropped");
            Assert.True((await f.Engine.ModelPlanActionAsync(again.Id, "approve", "human", again.ChangeSet.Digest)).Performed);
            Assert.True(await f.Sessions.Current.ReadAsync(m => ((TabularEditor.TOMWrapper.Column)ObjectRefs.Resolve(m, "column:Sales/Quantity")).IsAvailableInMDX));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S2_apply_plan_with_only_the_hide_and_Excel_lines_named_applies_neither()
        {
            using var f = await SalesAsync("R4Partial.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var ids = sales.ChangeSet.Lines.Where(l => l.Target == "column:Sales/Quantity" && (l.Op == "set_column_hidden" || l.Op == "set_is_available_in_mdx"))
                .Select(l => l.PlanItemId).ToArray();
            Assert.True(ids.Length == 2, "hide and Excel lines on Quantity: " + ids.Length);
            var report = await f.Engine.ApplyPlanAsync(ids, "agent");
            Assert.True(report.AppliedCount == 0, "applied " + report.AppliedCount + ": "
                + string.Join(" | ", report.Items.Select(i => i.Kind + " " + i.Status + " " + i.Note)));
            Assert.All(report.Items, i => Assert.Contains("needs", i.Note ?? "", StringComparison.Ordinal));
            Assert.Equal("False", await Prop(f, "column:Sales/Quantity", "isHidden"));
            Assert.True(await f.Sessions.Current.ReadAsync(m => ((TabularEditor.TOMWrapper.Column)ObjectRefs.Resolve(m, "column:Sales/Quantity")).IsAvailableInMDX));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void S3_a_live_push_never_turns_Excel_off_on_a_column_a_live_hierarchy_uses()
        {
            static TOM.Model Model()
            {
                var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
                var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
                t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
                t.Columns.Add(new TOM.DataColumn { Name = "Qty", SourceColumn = "Qty", DataType = TOM.DataType.Int64, LineageTag = "tag-qty" });
                db.Model.Tables.Add(t);
                return db.Model;
            }
            var live = Model();
            var h = new TOM.Hierarchy { Name = "By Qty", LineageTag = "tag-h" };
            h.Levels.Add(new TOM.Level { Name = "Qty", Ordinal = 0, Column = live.Tables["Sales"].Columns["Qty"], LineageTag = "tag-l" });
            live.Tables["Sales"].Hierarchies.Add(h);
            var src = Model();
            src.Tables["Sales"].Columns["Qty"].IsAvailableInMDX = false;

            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            Assert.True(live.Tables["Sales"].Columns["Qty"].IsAvailableInMDX, "the live hierarchy's column was taken out of Excel/MDX");
            Assert.Contains(rep.Unmatched, u => u.StartsWith("column:Sales/Qty", StringComparison.Ordinal) && u.Contains("hierarchy", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S4_two_different_aggregations_never_get_the_same_measure_name()
        {
            using var f = await SalesAsync("R4CrossName.bim", async e =>
            {
                await e.CreateTableAsync("Seed", "human");
                await e.CreateColumnAsync("table:Seed", "Rate", "Double", null, "human");
                await e.CreateMeasureAsync("table:Seed", "Total Avg Rate", "AVERAGE ( Seed[Rate] )", "human");
                await e.CreateTableAsync("Prices", "human");
                await e.CreateColumnAsync("table:Prices", "Cost", "Double", null, "human");
                await e.CreateColumnAsync("table:Sales", "Avg Cost", "Double", null, "human");
            });
            // "Total Avg {0}" is the model's average pattern, so the average of Prices[Cost] and the default total of
            // Sales[Avg Cost] would both be "Total Avg Cost".
            await AnswerAsync(f, new Dictionary<string, (string, double)>(StringComparer.Ordinal)
            {
                ["Avg Cost"] = ("sum", 0.95), ["Cost"] = ("average", 0.95), ["Rate"] = ("average", 0.95),
                ["Amount"] = ("none", 0.95), ["Quantity"] = ("none", 0.95),
            });
            var plan = await SettledPlanAsync(f);
            var names = plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet.Lines).Where(l => l.Op == "create_measure").Select(l => l.Target.Substring(l.Target.IndexOf('/') + 1)).ToArray();
            Assert.True(names.Length == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "two measures share a name: " + string.Join(", ", names));
            var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal) && i.ChangeSet?.Lines?.Any(l => l.Op == "create_measure") == true)
                .Select(i => new PlanItemDigest { Id = i.Id, Digest = i.ChangeSet.Digest }).ToArray();
            var both = await f.Engine.ApplyPlanItemsAsync(rows, "human");
            Assert.True(both.Performed, both.Sentence);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S5_a_measure_named_like_its_table_and_column_does_not_teach_a_bare_name()
        {
            using var f = await SalesAsync("R4TotalTable.bim", async e =>
            {
                await e.CreateTableAsync("Total", "human");
                await e.CreateColumnAsync("table:Total", "Cost", "Double", null, "human");
                await e.CreateMeasureAsync("table:Total", "Total Cost", "SUM ( Total[Cost] )", "human");
            });
            var names = await f.Sessions.Current.ReadAsync(m => BaseMeasures.Read(m));
            Assert.Equal("Total Amount", names.NameFor("sum", "Amount"));
        }

        // ---- the final batch: F1 to F3 -----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task F1_a_rows_title_counts_only_the_lines_still_kept()
        {
            using var f = await SalesAsync("R4Title.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.Equal("Add 2 measures for the numbers in Sales", sales.Title);
            var quantity = sales.ChangeSet.Lines.Single(l => l.Op == "create_measure" && l.Target == "measure:Sales/Total Quantity");
            await f.Engine.SetPlanItemAsync(quantity.PlanItemId, null, approved: false, "human");
            Assert.Equal("Add 1 measure for the numbers in Sales", Row(await f.Engine.GetModelPlanAsync(), SalesRow).Title);
            var amount = sales.ChangeSet.Lines.Single(l => l.Op == "create_measure" && l.Target == "measure:Sales/Total Amount");
            await f.Engine.SetPlanItemAsync(amount.PlanItemId, null, approved: false, "human");
            Assert.Equal("Set 2 columns to Don't summarize in Sales", Row(await f.Engine.GetModelPlanAsync(), SalesRow).Title);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task F2_no_line_title_speaks_engine_words()
        {
            using var f = await SalesAsync("R4Words.bim");
            await AnswerAsync(f);
            var titles = Row(await SettledPlanAsync(f), SalesRow).ChangeSet.Lines.Select(l => l.Line ?? "").Distinct().ToArray();
            Assert.Contains(titles, t => t.Length > 0);
            foreach (var word in new[] { "MDX", "Summarize by", "None", "isHidden", "isAvailable", "formatString", "summarizeBy" })
                Assert.DoesNotContain(titles, t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task F3_the_models_Avg_pattern_is_not_doubled_while_the_plainer_name_is_unique()
        {
            using var f = await SalesAsync("R4AvgAvg.bim", async e =>
            {
                await e.CreateTableAsync("Seed", "human");
                await e.CreateColumnAsync("table:Seed", "Rate", "Double", null, "human");
                await e.CreateMeasureAsync("table:Seed", "Total Avg Rate", "AVERAGE ( Seed[Rate] )", "human");
                await e.CreateTableAsync("Prices", "human");
                await e.CreateColumnAsync("table:Prices", "Avg Cost", "Double", null, "human");
                await e.CreateTableAsync("Shipping", "human");
                await e.CreateColumnAsync("table:Shipping", "Weight", "Double", null, "human");
                await e.CreateColumnAsync("table:Shipping", "Avg Weight", "Double", null, "human");
            });
            await AnswerAsync(f, new Dictionary<string, (string, double)>(StringComparer.Ordinal)
            {
                ["Avg Cost"] = ("average", 0.95), ["Weight"] = ("average", 0.95), ["Avg Weight"] = ("average", 0.95),
                ["Rate"] = ("average", 0.95), ["Amount"] = ("none", 0.95), ["Quantity"] = ("none", 0.95),
            });
            var plan = await SettledPlanAsync(f);
            string[] Made(string table) => Row(plan, "jev.column-summarize.table:" + table)?.ChangeSet.Lines
                .Where(l => l.Op == "create_measure").Select(l => l.Target.Substring(l.Target.IndexOf('/') + 1)).OrderBy(n => n, StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            // "Total Avg Avg Cost" says Avg twice; the plainer name is free, so it is the one.
            Assert.Equal(new[] { "Total Avg Cost" }, Made("Prices"));
            // In Shipping the plainer "Total Avg Weight" is Weight's own name, so Avg Weight keeps the longer one: both get a
            // measure and neither is refused.
            Assert.Equal(new[] { "Total Avg Avg Weight", "Total Avg Weight" }, Made("Shipping"));
            Assert.Empty(Row(plan, "jev.column-summarize.table:Shipping").ChangeSet.Refused);
        }
    }
}
