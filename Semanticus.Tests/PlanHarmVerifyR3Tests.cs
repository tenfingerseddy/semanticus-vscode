// Verifier, m11/plan-harm 1e36cada (29 Sep 2026), round 3. Left failing on purpose; not fixes.
//
// The ruling (coordinator, 28 and 29 Sep): the Plan's cleanup row is always optional, last and never waited on, and it
// never offers a date table's columns, dates or date keys, keys or any column a relationship uses (every part of a
// calculated key), name columns, any measure, code and lookup tables, a fact table's numbers (header facts and flat tables
// included), or anything built in this session; with reports read, only what they do not use. The round 3 fix brief:
// decide "fact" by structure, "it is on the many side of a relationship, or other facts look it up and it holds amounts".
//
// Each case builds one shape small inside the round 2 frame (dim_client with CLIENT_KEY, CLNT_NM and the control NOTE_TXT,
// an invoice fact related to it, one measure), saves it and opens it again (a fresh session), then reads the cleanup row
// and the lines "Work it out" proposes. The control must still be offered, so a missing row can never pass. What each test
// finds offered at 1e36cada:
//   N9   a header fact two facts share, relating to no dimension: ITEM_COUNT (Int64) and FX_RATE (a rate word). The fix
//        holds a looked-up fact's Double and Decimal amounts only (CleanupGuard, LocalEngine.Plan.cs:1873-1880), and the
//        same Plan's "Add explicit measures" row proposes a measure for both;
//   N10b a budget fact joined many-to-many to a dimension, stored from the dimension's side: BUDGET_UNITS (the guard reads
//        From as the many side and never reads cardinality);
//   X1   a fact's one-to-one extension table: LOYALTY_POINTS;
//   N15  a composite model's aggregation table: its group-by column CATEGORY (the sweep counts only the base column of an
//        alternateOf as used);
//   X3   a period table holding a Year > Month hierarchy with fewer than half calendar columns: its columns (IsCalendar runs
//        the half test before the hierarchy test; the fix brief says "or holding a date hierarchy");
//   X6   an unmarked calendar snowflaked to its fiscal periods and keyed by DAY_KEY: WEEKDAY_FLG, HOLIDAY_TXT;
//   X7   name columns named after their entity on plurals the singulariser misses: Branches[Branch], People[Person];
//   X8   a calculation group's own column once it is renamed from "Name": approving the row deletes it;
//   B5   an assistant's delete_if_unused of a name column or a date still files on the cleanup row (D9 fixed measures only).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TW = TabularEditor.TOMWrapper;

namespace Semanticus.Tests
{
    public sealed class PlanHarmVerifyR3Tests
    {
        private static async Task<DecisionFixture> ShapeAsync(string file, Func<DecisionFixture, Task> build, string report,
            bool pro = true, string reportTable = "fact_invoice", string reportColumn = "INV_AMT")
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "NOTE_TXT", "String", null, "human");
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            await e.CreateMeasureAsync("table:fact_invoice", "Invoice Count", "COUNTROWS ( fact_invoice )", "human");
            await build(f);
            await f.SaveAsAsync(file);
            f.UseNull();
            if (report == "read")
                await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file, reportTable, reportColumn);   // opens it again first
            else
            {
                await e.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
                f.UseNull();
            }
            return f;
        }

        private static async Task Cols(LocalEngine e, string table, params (string Name, string Type)[] columns)
        {
            await e.CreateTableAsync(table, "human");
            foreach (var (n, t) in columns) await e.CreateColumnAsync("table:" + table, n, t, null, "human");
        }

        private static Task Rel(LocalEngine e, string from, string to, bool active = true, string cross = null) =>
            e.CreateRelationshipAsync("column:" + from, "column:" + to, cross, active, "human");

        private static Func<DecisionFixture, Task> Build(string shape)
        {
            switch (shape)
            {
                case "N9": return async f =>   // two facts share a header that relates to no dimension
                {
                    var e = f.Engine;
                    await Cols(e, "fact_order_hdr", ("ORDER_ID", "Int64"), ("ORDER_DT", "DateTime"), ("FREIGHT_AMT", "Double"), ("ITEM_COUNT", "Int64"), ("FX_RATE", "Double"), ("CHANNEL_TXT", "String"));
                    await Cols(e, "fact_order_line", ("ORDER_ID", "Int64"), ("LINE_AMT", "Double"), ("QTY", "Int64"));
                    await Cols(e, "fact_shipment", ("ORDER_ID", "Int64"), ("SHIPPED_QTY", "Int64"), ("SHIP_COST", "Double"));
                    await Rel(e, "fact_order_line/ORDER_ID", "fact_order_hdr/ORDER_ID");
                    await Rel(e, "fact_shipment/ORDER_ID", "fact_order_hdr/ORDER_ID");
                };
                case "N10b": return async f =>   // a budget fact joined many-to-many, stored from the dimension's side
                {
                    var e = f.Engine;
                    await Cols(e, "dim_product", ("PRODUCT_KEY", "Int64"), ("PRODUCT_NM", "String"), ("CATEGORY", "String"), ("LIST_COST", "Double"), ("COLOR_TXT", "String"));
                    await e.CreateColumnAsync("table:fact_invoice", "PRODUCT_KEY", "Int64", null, "human");
                    await Rel(e, "fact_invoice/PRODUCT_KEY", "dim_product/PRODUCT_KEY");
                    await Cols(e, "fact_budget", ("CATEGORY", "String"), ("BUDGET_AMT", "Double"), ("BUDGET_UNITS", "Int64"), ("PLAN_TXT", "String"));
                    var r = await e.CreateRelationshipAsync("column:dim_product/CATEGORY", "column:fact_budget/CATEGORY", "BothDirections", true, "human");
                    await e.SetObjectPropertyAsync(r, "ToCardinality", "Many", "human");
                };
                case "X1": return async f =>   // a fact's one-to-one extension table
                {
                    var e = f.Engine;
                    await e.CreateColumnAsync("table:fact_invoice", "INV_KEY", "Int64", null, "human");
                    await Cols(e, "fact_invoice_ext", ("INV_KEY", "Int64"), ("DISCOUNT_AMT", "Double"), ("LOYALTY_POINTS", "Int64"), ("PROMO_TXT", "String"));
                    var r = await e.CreateRelationshipAsync("column:fact_invoice/INV_KEY", "column:fact_invoice_ext/INV_KEY", null, true, "human");
                    await e.SetObjectPropertyAsync(r, "FromCardinality", "One", "human");
                };
                case "N15": return async f =>   // a composite model with an aggregation table
                {
                    var e = f.Engine;
                    await Cols(e, "fact_sales", ("PRODUCT_KEY", "Int64"), ("SALES_AMT", "Double"), ("QTY", "Int64"));
                    await Cols(e, "dim_product", ("PRODUCT_KEY", "Int64"), ("PRODUCT_NM", "String"), ("CATEGORY", "String"));
                    await Cols(e, "agg_sales", ("CATEGORY", "String"), ("SALES_AMT_SUM", "Double"), ("ROWS_CNT", "Int64"));
                    await Rel(e, "fact_sales/PRODUCT_KEY", "dim_product/PRODUCT_KEY");
                    await e.SetObjectPropertyAsync("table:agg_sales", "IsHidden", "true", "human");
                    await f.Sessions.Current.MutateAsync("human", "composite and aggregations", m =>
                    {
                        foreach (var p in m.Tables["fact_sales"].Partitions) p.Mode = TW.ModeType.DirectQuery;
                        foreach (var p in m.Tables["dim_product"].Partitions) p.Mode = TW.ModeType.Dual;
                        var agg = m.Tables["agg_sales"];
                        agg.Columns["CATEGORY"].AddAlternateOf(m.Tables["dim_product"].Columns["CATEGORY"], TW.SummarizationType.GroupBy);
                        agg.Columns["SALES_AMT_SUM"].AddAlternateOf(m.Tables["fact_sales"].Columns["SALES_AMT"], TW.SummarizationType.Sum);
                        agg.Columns["ROWS_CNT"].AddAlternateOf(m.Tables["fact_sales"].Columns["PRODUCT_KEY"], TW.SummarizationType.Count);
                    });
                };
                case "X3": return async f =>   // a month-grain period table with a hierarchy and fewer than half calendar columns
                {
                    var e = f.Engine;
                    await Cols(e, "dim_period", ("PERIOD_KEY", "Int64"), ("PERIOD_YEAR", "Int64"), ("PERIOD_MONTH", "Int64"), ("CLOSE_STATUS", "String"), ("SEASON", "String"), ("BUDGET_VERSION", "String"), ("OWNER_TXT", "String"));
                    await e.CreateHierarchyAsync("table:dim_period", "Calendar", new[] { "PERIOD_YEAR", "PERIOD_MONTH" }, "human");
                    await Cols(e, "fact_budget", ("PERIOD_KEY", "Int64"), ("BUDGET_AMT", "Double"));
                    await Rel(e, "fact_budget/PERIOD_KEY", "dim_period/PERIOD_KEY");
                };
                case "X6": return async f =>   // an unmarked calendar, snowflaked, keyed without a date word
                {
                    var e = f.Engine;
                    await e.CreateColumnAsync("table:fact_invoice", "DAY_KEY", "Int64", null, "human");
                    await Cols(e, "dim_day", ("DAY_KEY", "Int64"), ("CAL_DATE", "DateTime"), ("WEEKDAY_FLG", "String"), ("HOLIDAY_TXT", "String"), ("FISCAL_PERIOD_KEY", "Int64"));
                    await Cols(e, "dim_fiscal_period", ("FISCAL_PERIOD_KEY", "Int64"), ("PERIOD_LABEL", "String"), ("FISCAL_YEAR", "Int64"));
                    await Rel(e, "fact_invoice/DAY_KEY", "dim_day/DAY_KEY");
                    await Rel(e, "dim_day/FISCAL_PERIOD_KEY", "dim_fiscal_period/FISCAL_PERIOD_KEY");
                };
                case "X7": return async f =>   // name columns named after their entity on plurals the singulariser misses
                {
                    var e = f.Engine;
                    await Cols(e, "Branches", ("BranchKey", "Int64"), ("Branch", "String"));
                    await Cols(e, "People", ("PersonKey", "Int64"), ("Person", "String"));
                    await e.CreateColumnAsync("table:fact_invoice", "BranchKey", "Int64", null, "human");
                    await e.CreateColumnAsync("table:fact_invoice", "PersonKey", "Int64", null, "human");
                    await Rel(e, "fact_invoice/BranchKey", "Branches/BranchKey");
                    await Rel(e, "fact_invoice/PersonKey", "People/PersonKey");
                };
                case "X8": return async f =>   // a calculation group whose own column is renamed
                {
                    var e = f.Engine;
                    var g = await e.CreateCalculationGroupAsync("Scenario", "human");
                    await e.CreateCalculationItemAsync(g, "Actual", "SELECTEDMEASURE()", "human");
                    await e.SetObjectPropertyAsync("column:Scenario/Name", "Name", "Pick", "human");
                };
                default: throw new ArgumentException(shape);
            }
        }

        /// <summary>The cleanup row and the delete targets "Work it out" proposes for it. The control must be offered.</summary>
        private static async Task<(ModelPlanItem Row, string[] Targets)> CleanupAsync(DecisionFixture f, string control = "column:dim_client/NOTE_TXT")
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.NotNull(set);
            var targets = set.Lines.Select(l => l.Target ?? "").ToArray();
            Assert.Contains(control, targets);
            return (row, targets);
        }

        /// <summary>None of <paramref name="refs"/> is a proposed target or a line of the row's diff.</summary>
        private static void AssertNeverOffered(ModelPlanItem row, string[] targets, params string[] refs)
        {
            var offered = refs.Where(r => targets.Contains(r, StringComparer.OrdinalIgnoreCase)
                    || (row.Diff ?? Array.Empty<string>()).Any(d => d.EndsWith(" " + Bracketed(r), StringComparison.Ordinal)))
                .ToArray();
            Assert.True(offered.Length == 0, "the cleanup offers " + string.Join(", ", offered)
                + " (all it offers: " + string.Join(", ", targets) + ")");
        }

        private static string Bracketed(string r)   // "column:dim_date/FY_QTR" is "dim_date[FY_QTR]" in the row's diff
        {
            var body = r.Substring(r.IndexOf(':') + 1);
            var slash = body.IndexOf('/');
            return slash < 0 ? body : body.Substring(0, slash) + "[" + body.Substring(slash + 1) + "]";
        }

        private static string FileFor(string shape, string report, bool pro = true) => "VR3-" + shape + "-" + report + (pro ? "" : "-free") + ".bim";

        // ---- D-A (HIGH): a looked-up fact keeps only its fractional amounts ------------------------------------------
        //
        // The fix reads a table other facts look up as a header fact "for its fractional amounts" only
        // (CleanupGuard, LocalEngine.Plan.cs:1873-1880: Double or Decimal, and not named as a rate, price, key or date).
        // Its whole numbers and its rates stay offerable. So a header fact that two facts share (and that relates to no
        // dimension of its own) has ITEM_COUNT and FX_RATE offered, while the same Plan's "Add explicit measures" row
        // proposes a measure for each of them: the contradiction the guard's own comment says it prevents.

        // ---- D-B (MEDIUM): an aggregation table's group-by column ----------------------------------------------------

        [Theory, InlineData("none"), InlineData("read")]
        public async Task N15_an_aggregation_tables_group_by_column_is_never_offered(string report)
        {
            // A composite model: the detail fact in DirectQuery, an Import aggregation mapped with alternateOf. The sweep
            // counts only the BASE column of a mapping as used (LineageGraph.Unused, structural), so the aggregation's own
            // group-by column is "safe", and the guard holds its sums (a table that relates to nothing and holds numbers)
            // but not its text group-by column. Removing it stops the aggregation answering by category.
            using var f = await ShapeAsync(FileFor("N15", report), Build("N15"), report);
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:agg_sales/SALES_AMT_SUM", "column:agg_sales/ROWS_CNT", "column:dim_product/CATEGORY");
            AssertNeverOffered(row, targets, "column:agg_sales/CATEGORY");
        }

        // ---- D-C (MEDIUM): a period table that holds a date hierarchy -------------------------------------------------

        // ---- D5's sibling: the singulariser ------------------------------------------------------------------------------

        // ---- a calculation group's own column ------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory, InlineData("none", true), InlineData("read", true), InlineData("none", false)]
        public async Task X8_a_calculation_groups_own_column_is_never_offered(string report, bool pro)
        {
            // The sweep calls a calculation group's column safe (TOM lets it be deleted; nothing references it by DAX), and
            // the guard holds it only when its name is a name word ("Name"). People rename it to what the group picks.
            // Keep all then Approve deletes it: "2 changes were made in one step", and the group is left with only its hidden
            // Ordinal column, saved and reopened that way (verifier evidence, probes/out5/X8-applied.json).
            using var f = await ShapeAsync(FileFor("X8", report, pro), Build("X8"), report, pro);
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:Scenario/Pick");
        }

        // ---- D9's sibling: an assistant's removal of a held-back column -------------------------------------------------------

    }
}
