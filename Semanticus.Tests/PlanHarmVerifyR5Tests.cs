// Verifier, m11/plan-harm 1a9cc554 (30 Sep 2026), round 5. THE RESTATEMENT.
//
// Kane's ruling (30 Sep): the cleanup row is a suggestion list for a lean model that the person reviews. Every column the
// sweep calls unused is LISTED, each with where it starts (ticked or not) and a plain reason; the offer rule v2 and its belts
// are the DEFAULT PRE-SELECTION, not a filter that hides columns. So the 35 earlier verify cases that said "never offered"
// now say "never pre-ticked". Those four files stay unedited (they still fail where they said "never offered"); each case is
// restated here on EXACTLY the shape it was written on: the earlier files' own fixture builders are called by reflection, and
// a build that lived inline in a test method is copied here word for word.
//
// Restated, old case beside new:
//   R2  S5 (none, read), S13 (none, read), S14 (none, read), S17 (none, read)  ->  R2_S5/S13/S14/S17_... never pre-ticked
//   R2  S12 (3: none Pro, read Pro, none Free)                                  ->  R2_S12_... (the flat table's own text is the
//                                                                                   control no longer: nothing of a table nothing
//                                                                                   relates to starts ticked, so the case asserts
//                                                                                   that and checks the frame-less row lists them)
//   R2  S15                                                                     ->  R2_S15_... (the row's sentences, not "never offered")
//   R3  N9 (3), N10b (2), X1, X3, X6, X7                                        ->  R3_..._never_pre_ticked
//   R3  B5                                                                      ->  R3_B5_... (an assistant's removal is never pre-ticked)
//   Verify  unmarked date table, date as a number, every column of a table,
//           marked date column with a report read                               ->  V_..._never_pre_ticked
//   R4  S12 relabelled (3)                                                      ->  R4_S12_..._never_pre_ticked
//   R4  H1 (3), H2 (3)                                                          ->  R4_H1/H2_...: with no Jev both START TICKED by the
//                                                                                   rule (ruling item 3: "by the belts or by Jev"), so
//                                                                                   the restatement is what the ruling charges: each is
//                                                                                   a listed line with a reason, marked suggested, and
//                                                                                   nothing removes it before the person looks (Run the
//                                                                                   plan and the assistant's apply_plan). Jev's untick
//                                                                                   of both on the real service is the report's live run.
//
// "Never pre-ticked" means: if the column is a line, it arrives unticked, not marked suggested, with a "Not ticked: " reason,
// and it is not in the row's diff (which shows only what starts ticked). And on every shape, nothing is hidden: the lines are
// exactly the columns the sweep calls safe (measures aside), each with a reason.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHarmVerifyR5Tests
    {
        // ---- the earlier files' own fixtures, borrowed ----------------------------------------------------------------

        private static Task<DecisionFixture> Borrow(Type owner, string method, params object[] args)
        {
            var m = owner.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingMethodException(owner.Name, method);
            return (Task<DecisionFixture>)m.Invoke(null, args);
        }

        private static Task<DecisionFixture> R2Shape(string file, Func<LocalEngine, Task> build, bool reportRead, bool pro = true,
            bool frame = true, string reportTable = "fact_invoice", string reportColumn = "INV_AMT") =>
            Borrow(typeof(PlanHarmVerifyR2Tests), "ShapeAsync", file, build, reportRead, pro, frame, reportTable, reportColumn);

        private static Task<DecisionFixture> R3Shape(string file, string shape, string report, bool pro = true)
        {
            var build = (Func<DecisionFixture, Task>)typeof(PlanHarmVerifyR3Tests)
                .GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { shape });
            return Borrow(typeof(PlanHarmVerifyR3Tests), "ShapeAsync", file, build, report, pro, "fact_invoice", "INV_AMT");
        }

        private static string FileFor(string shape, string mode) => "VR5-" + shape + "-" + mode + ".bim";

        // ---- reading the list ---------------------------------------------------------------------------------------

        private sealed class Listed
        {
            public ModelPlanItem Row;
            public ModelPlanChangeSet Set;
            public ModelPlanChangeLine Line(string target) =>
                Set.Lines.SingleOrDefault(l => string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The row, and the lines Work it out lists. Nothing hidden: the lines are exactly the sweep's safe columns, each
        /// with a reason. The control (when given) is listed and starts ticked, so a row that suggests nothing cannot pass.</summary>
        private static async Task<Listed> ListAsync(DecisionFixture f, string control = "column:dim_client/NOTE_TXT")
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.NotNull(set);

            var sweep = await f.Engine.UnusedObjectsCoreAsync(keepVisibleUntilReportsChecked: false);
            var safeColumns = (sweep?.Items ?? Array.Empty<UnusedItem>())
                .Where(i => (string.Equals(i.Verdict, "safe", StringComparison.Ordinal) || string.Equals(i.Verdict, "visibleAttribute", StringComparison.Ordinal))
                         && !string.Equals(i.Kind, "measure", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Ref).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
            var lines = set.Lines.Select(l => l.Target).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.Equal(safeColumns, lines);
            Assert.All(set.Lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason), l.Target + " has no reason"));

            var listed = new Listed { Row = row, Set = set };
            if (control != null)
            {
                var c = listed.Line(control);
                Assert.True(c != null, "the control " + control + " is not listed");
                Assert.True(c.Kept && c.Suggested, "the control " + control + " does not start ticked as a suggestion");
            }
            return listed;
        }

        /// <summary>None of <paramref name="refs"/> starts ticked: a listed one is unticked, not suggested, and says why; none is in
        /// the row's diff (the diff is what starts ticked).</summary>
        private static void AssertNeverPreTicked(Listed l, params string[] refs)
        {
            foreach (var r in refs)
            {
                var line = l.Line(r);
                if (line != null)
                {
                    Assert.False(line.Kept, r + " starts ticked (" + line.Reason + ")");
                    Assert.False(line.Suggested, r + " is marked suggested");
                    Assert.StartsWith("Not ticked: ", line.Reason ?? "", StringComparison.Ordinal);
                }
                Assert.DoesNotContain(l.Row.Diff ?? Array.Empty<string>(), d => d.EndsWith(" " + Bracketed(r), StringComparison.Ordinal));
            }
        }

        private static string Bracketed(string r)
        {
            var body = r.Substring(r.IndexOf(':') + 1);
            var slash = body.IndexOf('/');
            return slash < 0 ? body : body.Substring(0, slash) + "[" + body.Substring(slash + 1) + "]";
        }

        // ---- R2 (PlanHarmVerifyR2Tests), builds copied word for word -------------------------------------------------

        [Theory, InlineData(false)]
        public Task R2_S5_a_code_table_with_a_number_column_is_never_pre_ticked_sample(bool reportRead) => R2_S5_a_code_table_with_a_number_column_is_never_pre_ticked(reportRead);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false), InlineData(true)]
        public async Task R2_S5_a_code_table_with_a_number_column_is_never_pre_ticked(bool reportRead)
        {
            using var f = await R2Shape(FileFor("S5", reportRead ? "read" : "none"), async e =>
            {
                await e.CreateTableAsync("status", "human");
                await e.CreateColumnAsync("table:status", "STATUS_CD", "Int64", null, "human");
                await e.CreateColumnAsync("table:status", "STATUS_DESC", "String", null, "human");
                await e.CreateColumnAsync("table:status", "SORT_ORD", "Int64", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "STATUS_CD", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/STATUS_CD", "column:status/STATUS_CD", null, true, "human");
                await e.CreateTableAsync("lkp_priority", "human");
                await e.CreateColumnAsync("table:lkp_priority", "PRIORITY_ID", "Int64", null, "human");
                await e.CreateColumnAsync("table:lkp_priority", "PRIORITY_TXT", "String", null, "human");
                await e.CreateColumnAsync("table:lkp_priority", "WEIGHT", "Double", null, "human");
            }, reportRead);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:lkp_priority/PRIORITY_ID", "column:lkp_priority/PRIORITY_TXT", "column:lkp_priority/WEIGHT");
            AssertNeverPreTicked(l, "column:status/STATUS_CD", "column:status/STATUS_DESC", "column:status/SORT_ORD", "column:fact_invoice/STATUS_CD");
        }

        [Theory, InlineData(false, true)]
        public Task R2_S12_a_single_flat_tables_columns_are_listed_and_none_is_pre_ticked_sample(bool reportRead, bool pro) => R2_S12_a_single_flat_tables_columns_are_listed_and_none_is_pre_ticked(reportRead, pro);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task R2_S12_a_single_flat_tables_columns_are_listed_and_none_is_pre_ticked(bool reportRead, bool pro)
        {
            using var f = await R2Shape(FileFor("S12", (reportRead ? "read" : "none") + (pro ? "" : "-free")), async e =>
            {
                await e.CreateTableAsync("sales", "human");
                foreach (var (n, t) in new[] { ("ORDER_ID", "Int64"), ("ORDER_DATE", "DateTime"), ("CUSTOMER_NAME", "String"), ("PRODUCT", "String"),
                                               ("QTY", "Int64"), ("AMOUNT", "Double"), ("DISCOUNT", "Double"), ("NOTE_TXT", "String") })
                    await e.CreateColumnAsync("table:sales", n, t, null, "human");
            }, reportRead, pro, frame: false, reportTable: "sales", reportColumn: "PRODUCT");
            // No control: round 4 ruled that a table nothing relates to never starts ticked, the old control included.
            var l = await ListAsync(f, control: null);
            Assert.Contains(l.Set.Lines, x => x.Target == "column:sales/NOTE_TXT");   // listed all the same
            AssertNeverPreTicked(l, "column:sales/ORDER_ID", "column:sales/ORDER_DATE", "column:sales/CUSTOMER_NAME");
            AssertNeverPreTicked(l, "column:sales/QTY", "column:sales/AMOUNT", "column:sales/DISCOUNT", "column:sales/NOTE_TXT", "column:sales/PRODUCT");
        }

        [Theory, InlineData(false)]
        public Task R2_S13_a_header_facts_numbers_are_never_pre_ticked_sample(bool reportRead) => R2_S13_a_header_facts_numbers_are_never_pre_ticked(reportRead);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false), InlineData(true)]
        public async Task R2_S13_a_header_facts_numbers_are_never_pre_ticked(bool reportRead)
        {
            using var f = await R2Shape(FileFor("S13", reportRead ? "read" : "none"), async e =>
            {
                await e.CreateColumnAsync("table:fact_invoice", "INV_NO", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "TAX_AMT", "Double", null, "human");
                await e.CreateTableAsync("fact_invoice_line", "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "INV_NO", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "LINE_AMT", "Double", null, "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "QTY", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice_line/INV_NO", "column:fact_invoice/INV_NO", null, true, "human");
            }, reportRead);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:fact_invoice_line/LINE_AMT", "column:fact_invoice_line/QTY");
            AssertNeverPreTicked(l, "column:fact_invoice/INV_AMT", "column:fact_invoice/TAX_AMT");
        }

        [Theory, InlineData(false)]
        public Task R2_S14_a_month_grain_calendars_columns_are_never_pre_ticked_sample(bool reportRead) => R2_S14_a_month_grain_calendars_columns_are_never_pre_ticked(reportRead);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false), InlineData(true)]
        public async Task R2_S14_a_month_grain_calendars_columns_are_never_pre_ticked(bool reportRead)
        {
            using var f = await R2Shape(FileFor("S14", reportRead ? "read" : "none"), async e =>
            {
                await e.CreateTableAsync("dim_period", "human");
                foreach (var (n, t) in new[] { ("PERIOD_KEY", "Int64"), ("CAL_YEAR", "Int64"), ("CAL_QTR", "Int64"), ("MONTH_OF_YEAR", "Int64"),
                                               ("MONTH_NM", "String"), ("FISCAL_YEAR", "Int64") })
                    await e.CreateColumnAsync("table:dim_period", n, t, null, "human");
                await e.CreateTableAsync("fact_budget", "human");
                await e.CreateColumnAsync("table:fact_budget", "PERIOD_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:fact_budget", "BUDGET_AMT", "Double", null, "human");
                await e.CreateRelationshipAsync("column:fact_budget/PERIOD_KEY", "column:dim_period/PERIOD_KEY", null, true, "human");
            }, reportRead);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:dim_period/PERIOD_KEY", "column:dim_period/MONTH_NM", "column:fact_budget/BUDGET_AMT");
            AssertNeverPreTicked(l, "column:dim_period/CAL_YEAR", "column:dim_period/CAL_QTR", "column:dim_period/MONTH_OF_YEAR", "column:dim_period/FISCAL_YEAR");
        }

        [Fact]
        public async Task R2_S15_an_assistants_removal_of_a_measure_is_not_a_line_on_the_cleanup_and_the_row_says_measures_are_left_off()
        {
            using var f = await R2Shape(FileFor("S15", "none"), e => Task.CompletedTask, reportRead: false);
            await f.Engine.AddPlanItemAsync("measure:fact_invoice/Invoice Count", "delete_if_unused", null, "Remove Invoice Count", null, null, "agent");

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var lines = row.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>();
            // The old "measures ... never offered" sentence is now the purpose sentence and the measures sentence.
            Assert.Contains(LocalEngine.CleanupPurposeSentence, row.Why);
            Assert.Contains(row.Why, w => w.Contains("unused measure", StringComparison.Ordinal) && w.Contains("not on this list", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, x => string.Equals(x.Target, "measure:fact_invoice/Invoice Count", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(row.Diff ?? Array.Empty<string>(), d => d.Contains("Invoice Count", StringComparison.Ordinal));
        }

        [Theory, InlineData(false)]
        public Task R2_S17_a_name_column_named_after_its_entity_is_never_pre_ticked_sample(bool reportRead) => R2_S17_a_name_column_named_after_its_entity_is_never_pre_ticked(reportRead);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false), InlineData(true)]
        public async Task R2_S17_a_name_column_named_after_its_entity_is_never_pre_ticked(bool reportRead)
        {
            using var f = await R2Shape(FileFor("S17", reportRead ? "read" : "none"), async e =>
            {
                await e.CreateTableAsync("Products", "human");
                await e.CreateColumnAsync("table:Products", "ProductKey", "Int64", null, "human");
                await e.CreateColumnAsync("table:Products", "Product", "String", null, "human");
                await e.CreateTableAsync("Customers", "human");
                await e.CreateColumnAsync("table:Customers", "CustomerKey", "Int64", null, "human");
                await e.CreateColumnAsync("table:Customers", "Customer", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "ProductKey", "Int64", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "CustomerKey", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/ProductKey", "column:Products/ProductKey", null, true, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/CustomerKey", "column:Customers/CustomerKey", null, true, "human");
            }, reportRead);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:Products/Product", "column:Customers/Customer");
        }

        // ---- R3 (PlanHarmVerifyR3Tests), its own Build(shape) ----------------------------------------------------------

        [Theory, InlineData("none", true)]
        public Task R3_N9_a_header_fact_that_two_facts_share_its_counts_and_rates_are_never_pre_ticked_sample(string report, bool pro) => R3_N9_a_header_fact_that_two_facts_share_its_counts_and_rates_are_never_pre_ticked(report, pro);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData("none", true), InlineData("read", true), InlineData("none", false)]
        public async Task R3_N9_a_header_fact_that_two_facts_share_its_counts_and_rates_are_never_pre_ticked(string report, bool pro)
        {
            using var f = await R3Shape(FileFor("N9", report + (pro ? "" : "-free")), "N9", report, pro);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:fact_order_line/LINE_AMT", "column:fact_order_line/QTY", "column:fact_shipment/SHIPPED_QTY",
                "column:fact_shipment/SHIP_COST", "column:fact_order_hdr/FREIGHT_AMT", "column:fact_order_hdr/ORDER_DT");
            AssertNeverPreTicked(l, "column:fact_order_hdr/ITEM_COUNT", "column:fact_order_hdr/FX_RATE");
        }

        [Theory, InlineData("none")]
        public Task R3_N10b_a_budget_fact_joined_many_to_many_from_the_dimensions_side_its_numbers_are_never_pre_ticked_sample(string report) => R3_N10b_a_budget_fact_joined_many_to_many_from_the_dimensions_side_its_numbers_are_never_pre_ticked(report);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData("none"), InlineData("read")]
        public async Task R3_N10b_a_budget_fact_joined_many_to_many_from_the_dimensions_side_its_numbers_are_never_pre_ticked(string report)
        {
            using var f = await R3Shape(FileFor("N10b", report), "N10b", report);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:fact_budget/CATEGORY", "column:dim_product/CATEGORY", "column:fact_budget/BUDGET_AMT");
            AssertNeverPreTicked(l, "column:fact_budget/BUDGET_UNITS");
        }

        [Fact]
        public async Task R3_X1_a_facts_one_to_one_extension_its_numbers_are_never_pre_ticked()
        {
            using var f = await R3Shape(FileFor("X1", "none"), "X1", "none");
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:fact_invoice/INV_KEY", "column:fact_invoice_ext/INV_KEY", "column:fact_invoice_ext/DISCOUNT_AMT");
            AssertNeverPreTicked(l, "column:fact_invoice_ext/LOYALTY_POINTS");
        }

        [Fact]
        public async Task R3_X3_a_period_table_holding_a_date_hierarchy_its_columns_are_never_pre_ticked()
        {
            using var f = await R3Shape(FileFor("X3", "none"), "X3", "none");
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:dim_period/CLOSE_STATUS", "column:dim_period/SEASON", "column:dim_period/BUDGET_VERSION", "column:dim_period/OWNER_TXT");
        }

        [Fact]
        public async Task R3_X6_an_unmarked_calendar_snowflaked_to_its_fiscal_periods_its_columns_are_never_pre_ticked()
        {
            using var f = await R3Shape(FileFor("X6", "none"), "X6", "none");
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:dim_day/CAL_DATE", "column:dim_fiscal_period/FISCAL_YEAR");
            AssertNeverPreTicked(l, "column:dim_day/WEEKDAY_FLG", "column:dim_day/HOLIDAY_TXT");
        }

        [Fact]
        public async Task R3_X7_a_name_column_named_after_its_entity_on_an_es_or_irregular_plural_is_never_pre_ticked()
        {
            using var f = await R3Shape(FileFor("X7", "none"), "X7", "none");
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:Branches/Branch", "column:People/Person");
        }

        [Fact]
        public async Task R3_B5_an_assistants_removal_of_a_name_column_or_a_date_is_never_pre_ticked_on_the_cleanup_row()
        {
            using var f = await Borrow(typeof(PlanHarmVerifyR3Tests), "ShapeAsync", FileFor("B5", "none"),
                (Func<DecisionFixture, Task>)(async fx => await fx.Engine.CreateColumnAsync("table:dim_client", "ONBOARDED_ON", "DateTime", null, "human")),
                "none", true, "fact_invoice", "INV_AMT");
            await f.Engine.AddPlanItemAsync("column:dim_client/CLNT_NM", "delete_if_unused", null, "Remove CLNT_NM", null, null, "agent");
            await f.Engine.AddPlanItemAsync("column:dim_client/ONBOARDED_ON", "delete_if_unused", null, "Remove ONBOARDED_ON", null, null, "agent");

            // The lines exist only once the set is proposed (ListAsync does the same); a read before it saw no lines and passed for nothing.
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            Assert.Contains(LocalEngine.CleanupPurposeSentence, row.Why);
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.NotNull(set);
            var lines = set.Lines;
            Assert.Contains(lines, x => x.Kept && x.Suggested);   // the control: the set suggests something, so an empty read cannot pass
            var ticked = lines.Where(x => x.Kept && (x.Target == "column:dim_client/CLNT_NM" || x.Target == "column:dim_client/ONBOARDED_ON"))
                              .Select(x => x.Target).ToArray();
            Assert.True(ticked.Length == 0, "the cleanup row starts the assistant's removal of " + string.Join(", ", ticked) + " ticked");
            Assert.DoesNotContain(lines, x => x.Suggested && (x.Target == "column:dim_client/CLNT_NM" || x.Target == "column:dim_client/ONBOARDED_ON"));
        }

        // ---- the first verify file (PlanHarmVerifyTests), its own fixtures ---------------------------------------------

        private static async Task<Listed> VerifyListAsync(DecisionFixture f)
        {
            // That file's control was dim_client[INDUSTRY_CD]; it keeps it.
            var l = await ListAsync(f, "column:dim_client/INDUSTRY_CD");
            Assert.True(l.Row.Optional);
            return l;
        }

        [Fact]
        public async Task V_an_unmarked_date_tables_own_columns_are_never_pre_ticked_while_no_report_has_been_read()
        {
            using var f = await Borrow(typeof(PlanHarmVerifyTests), "ReopenedAsync", "VR5-VDate.bim");
            var l = await VerifyListAsync(f);
            AssertNeverPreTicked(l, "column:dim_date/FY_QTR", "column:dim_date/MTH_ABBR");
        }

        [Fact]
        public async Task V_a_date_held_as_a_number_is_never_pre_ticked_and_the_row_says_it_is_a_suggestion()
        {
            using var f = await Borrow(typeof(PlanHarmVerifyTests), "ReopenedAsync", "VR5-VDateKey.bim");
            var l = await VerifyListAsync(f);
            Assert.Contains(LocalEngine.CleanupPurposeSentence, l.Row.Why);
            AssertNeverPreTicked(l, "column:dim_client/ONBRD_DT_KEY");
        }

        [Fact]
        public async Task V_the_cleanup_never_pre_ticks_every_column_of_a_table()
        {
            using var f = await Borrow(typeof(PlanHarmVerifyTests), "ReopenedAsync", "VR5-VTable.bim");
            var l = await VerifyListAsync(f);
            var refCodes = new[] { "CODE_TYPE", "CODE", "CODE_DESC" }.Select(c => "column:ref_codes/" + c).ToArray();
            Assert.False(refCodes.All(r => l.Line(r)?.Kept == true), "the cleanup starts every column of ref_codes ticked");
            AssertNeverPreTicked(l, refCodes);   // the table nothing relates to: none of it starts ticked
        }

        [Fact]
        public async Task V_with_a_report_read_the_marked_date_tables_date_column_is_never_pre_ticked()
        {
            using var f = await Borrow(typeof(PlanHarmVerifyTests), "ReopenedWithAReportReadAsync", "VR5-VReadDate.bim");
            var l = await ListAsync(f, control: null);
            AssertNeverPreTicked(l, "column:dim_date/CAL_DT");
        }

        // ---- R4 (PlanHarmVerifyR4Tests), its own fixtures ---------------------------------------------------------------

        [Theory, InlineData(false, true)]
        public Task R4_S12_relabelled_a_single_flat_tables_columns_are_never_pre_ticked_with_the_frames_column_as_the_control_sample(bool reportRead, bool pro) => R4_S12_relabelled_a_single_flat_tables_columns_are_never_pre_ticked_with_the_frames_column_as_the_control(reportRead, pro);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task R4_S12_relabelled_a_single_flat_tables_columns_are_never_pre_ticked_with_the_frames_column_as_the_control(bool reportRead, bool pro)
        {
            using var f = await Borrow(typeof(PlanHarmVerifyR4Tests), "FlatBesideTheFrameAsync",
                FileFor("R4S12", (reportRead ? "read" : "none") + (pro ? "" : "-free")), reportRead, pro);
            var l = await ListAsync(f);
            AssertNeverPreTicked(l, "column:sales/ORDER_ID", "column:sales/ORDER_DATE", "column:sales/CUSTOMER_NAME");
            AssertNeverPreTicked(l, "column:sales/QTY", "column:sales/AMOUNT", "column:sales/DISCOUNT");
            AssertNeverPreTicked(l, "column:sales/NOTE_TXT", "column:sales/PRODUCT");
        }

        /// <summary>H1 and H2 with no Jev: the rule starts them ticked (the belts are frozen). Ruling items 2 and 3: the line is
        /// listed with a reason and marked suggested, and nothing removes it before the person has looked.</summary>
        private static async Task AssertHeldOutReviewedFirstAsync(DecisionFixture f, string target)
        {
            var l = await ListAsync(f);
            var line = l.Line(target);
            Assert.NotNull(line);
            Assert.True(line.Kept && line.Suggested, target + " should arrive as an unreviewed suggestion with no Jev");
            Assert.False(string.IsNullOrWhiteSpace(line.Reason));

            var table = target.Substring(target.IndexOf(':') + 1).Split('/');
            async Task<bool> Present() => await f.Sessions.Current.ReadAsync(m => m.Tables[table[0]].Columns.Contains(table[1]));

            // Run the plan: the cleanup is never in the ready set, and a Free session is refused outright.
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain("cleanup/model", plan.ReadyRows());
            await f.Engine.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest ?? "x");
            Assert.True(await Present(), target + " was removed by Run the plan before the person looked");
            // The assistant's own apply_plan, nothing named and then the line named: an untouched suggestion is not applied.
            await f.Engine.ApplyPlanAsync(null, "agent");
            Assert.True(await Present(), target + " was removed by the assistant's apply_plan before the person looked");
            await f.Engine.ApplyPlanAsync(new[] { line.PlanItemId }, "agent");
            Assert.True(await Present(), target + " was removed by the assistant's apply_plan naming it before the person looked");
        }

        [Theory, InlineData(false, true)]
        public Task R4_H1_a_facts_text_that_holds_a_date_is_listed_and_never_removed_before_the_person_looks_sample(bool reportRead, bool pro) => R4_H1_a_facts_text_that_holds_a_date_is_listed_and_never_removed_before_the_person_looks(reportRead, pro);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task R4_H1_a_facts_text_that_holds_a_date_is_listed_and_never_removed_before_the_person_looks(bool reportRead, bool pro)
        {
            using var f = await Borrow(typeof(PlanHarmVerifyR4Tests), "HeldOutShapeAsync",
                FileFor("H1", (reportRead ? "read" : "none") + (pro ? "" : "-free")),
                (Func<LocalEngine, Task>)(e => e.CreateColumnAsync("table:fact_invoice", "BILLED_AS", "String", null, "human")), reportRead, pro);
            await AssertHeldOutReviewedFirstAsync(f, "column:fact_invoice/BILLED_AS");
        }

        [Theory, InlineData(false, true)]
        public Task R4_H2_a_dimensions_alternate_key_is_listed_and_never_removed_before_the_person_looks_sample(bool reportRead, bool pro) => R4_H2_a_dimensions_alternate_key_is_listed_and_never_removed_before_the_person_looks(reportRead, pro);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task R4_H2_a_dimensions_alternate_key_is_listed_and_never_removed_before_the_person_looks(bool reportRead, bool pro)
        {
            using var f = await Borrow(typeof(PlanHarmVerifyR4Tests), "HeldOutShapeAsync",
                FileFor("H2", (reportRead ? "read" : "none") + (pro ? "" : "-free")),
                (Func<LocalEngine, Task>)(async e =>
                {
                    await e.CreateTableAsync("dim_item", "human");
                    await e.CreateColumnAsync("table:dim_item", "ITEM_KEY", "Int64", null, "human");
                    await e.CreateColumnAsync("table:dim_item", "Item", "String", null, "human");
                    await e.CreateColumnAsync("table:dim_item", "BARCODE", "String", null, "human");
                    await e.CreateColumnAsync("table:fact_invoice", "ITEM_KEY", "Int64", null, "human");
                    await e.CreateRelationshipAsync("column:fact_invoice/ITEM_KEY", "column:dim_item/ITEM_KEY", null, true, "human");
                }), reportRead, pro);
            await AssertHeldOutReviewedFirstAsync(f, "column:dim_item/BARCODE");
        }
    }
}
