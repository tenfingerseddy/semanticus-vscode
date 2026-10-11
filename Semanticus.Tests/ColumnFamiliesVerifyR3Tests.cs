// VERIFIER'S TESTS, round 3, for m11/m4-columns-fair at 3d2df82d: column.summarize as the base-measure decision
// (Kane, 26 Sep: "Jev should set summarisation of columns off as a best practice. it should suggest an explicit base
// measure aggregation instead"), the coordinator's hide and Excel/MDX lines ("each a droppable line saying why"), and
// the Semanticus_AddedOnPurpose mark. Each test states one thing the brief or a ruling requires. The ones marked
// DEFECT fail at 3d2df82d on purpose and are left failing; the rest pass there and pin what holds.
//
//   1. DEFECT: a measure the model already has is proposed again when its formula carries a comment.
//   2. DEFECT: the same when the existing measure wraps the same total in CALCULATE or writes it as SUMX over its
//      own table (the lane's commit says "never a second copy ... any name, any spelling").
//   3. A measure in another table, spelled in another case and quoting, is found (holds).
//   4. DEFECT: two tables with a number column of the same name (Sales[Quantity], Returns[Quantity]) are both offered
//      "Total Quantity". Approving one leaves the other column switched to "don't add up" with no measure at all.
//   5. DEFECT: the same two rows, approved together, change nothing.
//   6. DEFECT: a person who drops a column's measure still gets the column hidden and taken out of Excel/MDX, and
//      the hiding line's own reason ("... now does the adding up") is then false. The ruling: hide "once its measure
//      exists".
//   7. DEFECT: a person who drops the hide line still gets the column taken out of Excel/MDX while it stays in
//      view; that line's reason says "the column is hidden".
//   8. One undo takes back every line of an approved row (summarize, hidden, Excel/MDX, the measures and their mark);
//      one redo puts every one back, mark, format and description included; nothing is offered as unused (holds).
//   9. A person can still delete a marked measure, on either door, one or several at once (holds).
//  10. The mark survives saving and reopening, as .bim and as TMDL, and still keeps the measure out of the cleanup
//      (holds).
//  11. The mark never reaches a live push and never holds a new measure back (holds).
//  12. DEFECT: after approving a base-measure row, a live push cannot carry the Excel/MDX lines: every such column is
//      reported as "cannot sync" on this push and on every later one, and the service keeps the attribute on.
//  13. A column already set not to add up, and already hidden, gets only its measure (holds).
//  14. Time-intelligence measures keep their mark through undo and redo (holds).
//  15. DEFECT: a line the person dropped is said to "arrive dropped because Jev was unsure", on a row where Jev was
//      sure of every pick. This family never drops a line itself, so on its rows the sentence is always false. Seen in
//      UAT lane h on Northstar after dropping the four Average List Price lines.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ColumnFamiliesVerifyR3Tests
    {
        private const string Summarize = "column.summarize";
        private const string SalesRow = "jev.column-summarize.table:Sales";

        // ---- fixtures ------------------------------------------------------------------------------------

        /// <summary>Answers every summarize ask from a table of (column name, choice, confidence), whatever its digest.
        /// A column not in the table gets an unsure "sum", so the principled rule decides it.</summary>
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

        /// <summary>Consent, only column.summarize on, and Jev sure of: Amount sum, Quantity sum, Discount sum, Unit
        /// Price average, CustomerKey none.</summary>
        private static async Task AnswerAsync(DecisionFixture f)
        {
            await f.ConsentAsync();
            foreach (var p in DecisionRegistry.Points.Select(p => p.Name).Where(n => n != Summarize))
                await f.Engine.SetDecisionPointEnabledAsync(p, false, "human");
            var answers = new Dictionary<string, (string, double)>(StringComparer.Ordinal)
            {
                ["Amount"] = ("sum", 0.95), ["Quantity"] = ("sum", 0.95), ["Discount"] = ("sum", 0.95),
                ["Unit Price"] = ("average", 0.95), ["CustomerKey"] = ("none", 0.95),
            };
            f.Engine.UseDecisionsForTest(new DecisionService(new ByName(answers), new DecisionBudget()));
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>Customer: CustomerKey (one side). Sales: CustomerKey (many side), Amount, Quantity, Unit Price,
        /// Discount. Saved, so consent can bind. <paramref name="extra"/> adds to it before the save.</summary>
        private static async Task<DecisionFixture> SalesAsync(string file, Func<LocalEngine, Task> extra = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Unit Price", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Discount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            if (extra != null) await extra(e);
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static async Task<string> Prop(DecisionFixture f, string objRef, string property) =>
            (await f.Engine.GetObjectAsync(objRef)).Properties[property]?.ToString() ?? "";

        private static Task<bool> Mdx(DecisionFixture f, string columnRef) =>
            f.Sessions.Current.ReadAsync(m => ((Column)ObjectRefs.Resolve(m, columnRef)).IsAvailableInMDX);

        private static Task<string> Mark(DecisionFixture f, string objRef) =>
            f.Sessions.Current.ReadAsync(m => Semanticus.Engine.Lineage.LineageGraph.AddedOnPurpose(ObjectRefs.Resolve(m, objRef)));

        private static Task<string> Snapshot(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));

        /// <summary>The measures whose formula is exactly FUNCTION('table'[column]), judged here rather than by the
        /// family's own matcher.</summary>
        private static Task<string[]> MeasuresComputing(DecisionFixture f, string function, string table, string column) =>
            f.Sessions.Current.ReadAsync(m => m.AllMeasures
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Expression ?? "",
                    @"^\s*" + function + @"\s*\(\s*'?" + System.Text.RegularExpressions.Regex.Escape(table) + @"'?\s*\["
                    + System.Text.RegularExpressions.Regex.Escape(column) + @"\]\s*\)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(x => x.Table.Name + "/" + x.Name).ToArray());

        private static ModelPlanItem Row(ModelPlanView plan, string id) => plan.Items.SingleOrDefault(i => i.Id == id);

        private static IEnumerable<ModelPlanChangeLine> MeasureLinesFor(ModelPlanItem row, string table, string column) =>
            (row?.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Op == "create_measure" && (l.After ?? "").Contains("'" + table + "'[" + column + "]", StringComparison.Ordinal));

        // ---- 1 to 3. never a second copy of a measure the model has ---------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_measure_the_model_has_is_not_proposed_again_when_its_formula_carries_a_comment()
        {
            using var f = await SalesAsync("R3DupComment.bim", async e =>
                await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( 'Sales'[Quantity] ) // units sold", "human"));
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var again = MeasureLinesFor(sales, "Sales", "Quantity").Select(l => l.Target + " = " + l.After).ToArray();
            // FAILS at 3d2df82d: "measure:Sales/Total Quantity = SUM ( 'Sales'[Quantity] )", beside Units.
            Assert.True(again.Length == 0, "Units already adds up Quantity, but the row proposes: " + string.Join(", ", again));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_measure_the_model_has_is_not_proposed_again_when_it_is_wrapped_in_CALCULATE_or_written_as_SUMX()
        {
            using var f = await SalesAsync("R3DupWrapped.bim", async e =>
            {
                await e.CreateMeasureAsync("table:Sales", "Revenue", "CALCULATE ( SUM ( Sales[Amount] ) )", "human");
                await e.CreateMeasureAsync("table:Sales", "Discounts", "SUMX ( Sales, Sales[Discount] )", "human");
            });
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var again = MeasureLinesFor(sales, "Sales", "Amount").Concat(MeasureLinesFor(sales, "Sales", "Discount"))
                .Select(l => l.Target + " = " + l.After).ToArray();
            // FAILS at 3d2df82d: Total Amount beside Revenue, and Total Discount beside Discounts.
            Assert.True(again.Length == 0, "the model already computes these totals, but the row proposes: " + string.Join(", ", again));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_measure_in_another_table_and_another_spelling_is_found()
        {
            using var f = await SalesAsync("R3DupElsewhere.bim", async e =>
            {
                await e.CreateTableAsync("Key Figures", "human");
                await e.CreateMeasureAsync("table:Key Figures", "Avg Price", "average('sales'[unit price])", "human");
            });
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.Empty(MeasureLinesFor(sales, "Sales", "Unit Price"));
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Unit Price" && r.Reason.StartsWith("Avg Price already gives", StringComparison.Ordinal));
        }

        // ---- 4 and 5. two tables, one column name ---------------------------------------------------------------

        private static Task<DecisionFixture> TwoTablesAsync(string file) => SalesAsync(file, async e =>
        {
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "Quantity", "Int64", null, "human");
        });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_two_tables_with_a_number_column_of_the_same_name_each_get_their_base_measure()
        {
            using var f = await TwoTablesAsync("R3SameName.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
            var returns = Row(await SettledPlanAsync(f), "jev.column-summarize.table:Returns");
            Assert.NotNull(returns);
            var approved = await f.Engine.ModelPlanActionAsync(returns.Id, "approve", "human", returns.ChangeSet.Digest);
            Assert.True(approved.Performed, approved.Sentence);

            Assert.Single(await MeasuresComputing(f, "SUM", "Sales", "Quantity"));
            // FAILS at 3d2df82d: the Returns row lost its measure ("A measure named Total Quantity already exists in this
            // model, so no second one was proposed"), yet still switched Returns[Quantity] to "don't add up".
            var returnsMeasure = await MeasuresComputing(f, "SUM", "Returns", "Quantity");
            Assert.True(returnsMeasure.Length == 1,
                "Returns[Quantity] is now summarize " + await Prop(f, "column:Returns/Quantity", "summarizeBy") + " with no measure adding it up; the row said: "
                + string.Join(" | ", returns.ChangeSet.Refused.Select(r => r.Reason)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_two_rows_offered_together_can_be_approved_together()
        {
            using var f = await TwoTablesAsync("R3SameNameTogether.bim");
            await AnswerAsync(f);
            var plan = await SettledPlanAsync(f);
            var sales = Row(plan, SalesRow);
            var returns = Row(plan, "jev.column-summarize.table:Returns");
            var both = await f.Engine.ApplyPlanItemsAsync(new[]
            {
                new PlanItemDigest { Id = sales.Id, Digest = sales.ChangeSet.Digest },
                new PlanItemDigest { Id = returns.Id, Digest = returns.ChangeSet.Digest },
            }, "human");
            // FAILS at 3d2df82d: "Nothing was changed, because Total Quantity in add 1 measure for the numbers in Returns
            // could not be made: A measure named Total Quantity already exists in this model".
            Assert.True(both.Performed, both.Sentence);
        }

        // ---- 6 and 7. the hide and Excel/MDX lines follow the measure --------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_column_whose_measure_a_person_dropped_stays_in_view_and_in_Excel()
        {
            using var f = await SalesAsync("R3DropMeasure.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var measureLines = sales.ChangeSet.Lines.Where(l => l.Target == "measure:Sales/Total Quantity").ToArray();
            Assert.Contains(measureLines, l => l.Op == "create_measure");
            foreach (var l in measureLines) await f.Engine.SetPlanItemAsync(l.PlanItemId, null, approved: false, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            var hideLine = ready.ChangeSet.Lines.SingleOrDefault(l => l.Target == "column:Sales/Quantity" && l.Op == "set_column_hidden" && l.Kept);
            var approved = await f.Engine.ModelPlanActionAsync(ready.Id, "approve", "human", ready.ChangeSet.Digest);
            Assert.True(approved.Performed, approved.Sentence);

            Assert.Empty(await MeasuresComputing(f, "SUM", "Sales", "Quantity"));
            var hidden = await Prop(f, "column:Sales/Quantity", "isHidden");
            var mdx = await Mdx(f, "column:Sales/Quantity");
            // FAILS at 3d2df82d: Quantity is hidden and out of Excel/MDX, with no measure doing its adding.
            Assert.True(hidden != "True" && mdx,
                "Quantity: hidden " + hidden + ", in Excel/MDX " + mdx + ", and no measure adds it up; a kept hiding line was on the row: " + (hideLine != null));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_column_a_person_kept_in_view_stays_available_to_Excel()
        {
            using var f = await SalesAsync("R3DropHide.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var hide = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/Amount" && l.Op == "set_column_hidden");
            await f.Engine.SetPlanItemAsync(hide.PlanItemId, null, approved: false, "human");
            var ready = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            var approved = await f.Engine.ModelPlanActionAsync(ready.Id, "approve", "human", ready.ChangeSet.Digest);
            Assert.True(approved.Performed, approved.Sentence);

            Assert.Equal("False", await Prop(f, "column:Sales/Amount", "isHidden"));
            // FAILS at 3d2df82d: Amount stays in the field list but is taken out of Excel/MDX.
            Assert.True(await Mdx(f, "column:Sales/Amount"), "Amount is visible but no longer available to Excel/MDX");
        }

        // ---- 8. one undo, one redo, every line and the mark ------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_one_undo_and_one_redo_move_every_line_and_the_mark()
        {
            using var f = await SalesAsync("R3UndoRedo.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var measures = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            var columns = new[] { "Amount", "Quantity", "Unit Price", "Discount" };
            Assert.Equal(4, measures.Length);
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);

            async Task Applied(bool on)
            {
                foreach (var c in columns)
                {
                    Assert.Equal(on ? "None" : "Default", await Prop(f, "column:Sales/" + c, "summarizeBy"));
                    Assert.Equal(on ? "True" : "False", await Prop(f, "column:Sales/" + c, "isHidden"));
                    Assert.Equal(!on, await Mdx(f, "column:Sales/" + c));
                }
                var names = (await f.Engine.ListMeasuresAsync()).Select(x => "measure:" + x.Table + "/" + x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var m in measures)
                {
                    Assert.Equal(on, names.Contains(m));
                    if (!on) continue;
                    Assert.StartsWith("the measure for ", await Mark(f, m), StringComparison.Ordinal);
                    Assert.False(string.IsNullOrEmpty(await Prop(f, m, "formatString")));
                    Assert.StartsWith("The ", await Prop(f, m, "description"), StringComparison.Ordinal);
                }
            }

            await Applied(true);
            await f.Engine.UndoAsync("human");
            await Applied(false);
            await f.Engine.RedoAsync("human");
            await Applied(true);
            var unused = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(unused.Items, i => measures.Contains(i.Ref, StringComparer.OrdinalIgnoreCase));
            var cleanup = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model");
            foreach (var m in measures)
                Assert.DoesNotContain(m.Substring(m.IndexOf('/') + 1), string.Join("|", cleanup?.Diff ?? Array.Empty<string>()), StringComparison.Ordinal);
        }

        // ---- 9. a person can still delete it ---------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_person_can_delete_a_marked_measure_on_either_door()
        {
            using var f = await SalesAsync("R3Delete.bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var measures = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
            foreach (var m in measures) Assert.NotNull(await Mark(f, m));

            Assert.True((await f.Engine.DeleteObjectAsync(measures[0], "human")).Changed);           // the UI door's origin
            Assert.True((await f.Engine.DeleteObjectAsync(measures[1], "agent")).Changed);           // the MCP door's origin
            Assert.True((await f.Engine.DeleteObjectsAsync(new[] { measures[2], measures[3] }, "human")).Changed);
            Assert.Empty(await f.Engine.ListMeasuresAsync());
        }

        // ---- 10. save and reopen -------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("bim")]
        [InlineData("tmdl")]
        public async Task Summarize_the_mark_survives_saving_and_reopening(string format)
        {
            using var f = await SalesAsync("R3Save" + format + ".bim");
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var measures = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);

            var path = Path.Combine(f.Workspace, format == "bim" ? "R3Saved.bim" : "R3SavedTmdl");
            await f.Engine.SaveAsync(path, format, true, "human");
            await f.Engine.OpenAsync(path, discardUnsaved: true);
            foreach (var m in measures) Assert.StartsWith("the measure for ", await Mark(f, m), StringComparison.Ordinal);
            var unused = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(unused.Items, i => measures.Contains(i.Ref, StringComparer.OrdinalIgnoreCase));
        }

        // ---- 11 and 12. a live push ------------------------------------------------------------------------------

        private static async Task<(Microsoft.AnalysisServices.Tabular.Model Src, Microsoft.AnalysisServices.Tabular.Model Live, string[] Measures)> ApprovedAgainstBeforeAsync(DecisionFixture f)
        {
            await AnswerAsync(f);
            var before = await Snapshot(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var measures = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
            Assert.True((await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest)).Performed);
            var src = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(await Snapshot(f)).Model;
            var live = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(before).Model;
            return (src, live, measures);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_the_mark_never_reaches_a_live_push_and_never_holds_a_new_measure()
        {
            using var f = await SalesAsync("R3PushMark.bim");
            var (src, live, measures) = await ApprovedAgainstBeforeAsync(f);
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            foreach (var m in measures)
            {
                var name = m.Substring(m.IndexOf('/') + 1);
                Assert.Contains("add measure:Sales/" + name, rep.Changes);
                Assert.DoesNotContain(rep.Unmatched, u => u.StartsWith(m, StringComparison.Ordinal));
                Assert.Empty(live.Tables["Sales"].Measures[name].Annotations);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_live_push_after_approving_carries_every_line_of_the_row()
        {
            using var f = await SalesAsync("R3PushMdx.bim");
            var (src, live, _) = await ApprovedAgainstBeforeAsync(f);
            var rep = LiveDeploy.SyncModels(src, live, apply: true);
            // FAILS at 3d2df82d: column:Sales/Amount, Quantity, Unit Price and Discount each "carries authored column metadata
            // this live push cannot sync; deploy via TMDL/XMLA", and the service keeps them available to Excel/MDX.
            Assert.True(rep.Unmatched.Length == 0, "the live push could not carry: " + string.Join(" | ", rep.Unmatched));
            Assert.False(live.Tables["Sales"].Columns["Amount"].IsAvailableInMDX);
            var again = LiveDeploy.SyncModels(src, live, apply: false);
            Assert.True(again.Unmatched.Length == 0, "a second push still reports: " + string.Join(" | ", again.Unmatched));
        }

        // ---- 13. a column that already does not add up, and is hidden ---------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_column_already_not_adding_up_and_hidden_gets_only_its_measure()
        {
            using var f = await SalesAsync("R3AlreadyDone.bim", async e =>
                await e.SetColumnMetadataAsync("column:Sales/Discount", true, "None", null, null, "human"));
            await AnswerAsync(f);
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            var onColumn = sales.ChangeSet.Lines.Where(l => l.Target == "column:Sales/Discount").Select(l => l.Op).ToArray();
            Assert.Empty(onColumn);
            Assert.Single(MeasureLinesFor(sales, "Sales", "Discount"));
        }

        // ---- 14. time intelligence keeps its mark through undo and redo -----------------------------------------------

        [Fact]
        public async Task Time_intelligence_measures_keep_their_mark_through_undo_and_redo()
        {
            // The model SalesAsync builds plus a marked-style date table, written as a file and opened in one call (14 engine writes before).
            var calendar = PlanRowsBim.Table("Calendar", new[] { "Date:DateTime:key" });
            calendar["dataCategory"] = "Time";
            using var f = await PlanRowsBim.OpenAsync("Contoso", "R3Ti.bim", new object[]
            {
                PlanRowsBim.Table("Customer", new[] { "CustomerKey:Int64" }),
                PlanRowsBim.Table("Sales", new[] { "CustomerKey:Int64", "Amount:Double", "Quantity:Int64", "Unit Price:Double", "Discount:Double" },
                    PlanRowsBim.M("Sales Total", "SUM('Sales'[Amount])")),
                calendar,
            }, new object[] { PlanRowsBim.Rel("Sales", "CustomerKey", "Customer", "CustomerKey") });
            var made = await f.Engine.GenerateTimeIntelligenceAsync("measure:Sales/Sales Total", "'Calendar'[Date]", new[] { "YTD", "PY" }, null, "human");
            var refs = made.Created.Select(c => c.Ref).ToArray();
            Assert.NotEmpty(refs);
            await f.Engine.UndoAsync("human");
            await f.Engine.RedoAsync("human");
            foreach (var r in refs) Assert.StartsWith("a time-intelligence measure of", await Mark(f, r), StringComparison.Ordinal);
            var unused = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(unused.Items, i => refs.Contains(i.Ref, StringComparer.OrdinalIgnoreCase));
        }
        // ---- 15. a person's drop is not blamed on Jev --------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Summarize_a_line_a_person_dropped_is_not_said_to_be_dropped_because_Jev_was_unsure()
        {
            using var f = await SalesAsync("R3DropWords.bim");
            await AnswerAsync(f);                       // Jev is sure (0.95) of every pick
            var sales = Row(await SettledPlanAsync(f), SalesRow);
            Assert.DoesNotContain(sales.Why, w => w.Contains("dropped because Jev was unsure", StringComparison.Ordinal));
            var hide = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/Amount" && l.Op == "set_column_hidden");
            await f.Engine.SetPlanItemAsync(hide.PlanItemId, null, approved: false, "human");
            var after = Row(await f.Engine.GetModelPlanAsync(), SalesRow);
            // FAILS at 3d2df82d: "1 line arrives dropped because Jev was unsure. Keep it to include it."
            var blamed = after.Why.Where(w => w.Contains("because Jev was unsure", StringComparison.Ordinal)).ToArray();
            Assert.True(blamed.Length == 0, "the person dropped the line, but the row says: " + string.Join(" | ", blamed));
        }
    }
}
