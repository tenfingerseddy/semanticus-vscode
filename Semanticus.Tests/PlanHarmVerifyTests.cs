// Verifier, m11/plan-harm 8f799947 (28 Sep 2026). Left failing on purpose; not fixes.
//
// The lane's cleanup row, on a model no report's use has been read for, says of itself: "measures, a fact table's
// numbers, dates, the date table's columns, name columns and anything built in this session are never offered for
// removal." The check's journeys, rerun on the lane with the model opened again (the next day, a fresh session),
// still offer three kinds of thing that sentence and the brief's "never the date table's parts" rule out:
//   the date table's own columns when the date table is not marked yet (FY_QTR, MTH_ABBR, MTH_NO, DOW_NO: the raw
//   CSV start before Mark, and always on the SQL draft, which marks a calendar of its own);
//   a date held as a number (ONBRD_DT_KEY, HIRE_DT_KEY, START_DT_KEY: the lane's own NeverSummed calls these dates);
//   every column of a table (ref_codes: CODE, CODE_TYPE, CODE_DESC), which leaves a table with no columns.
// Each test builds that shape small, saves it, opens it again, and reads the cleanup row and its worked-out lines.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHarmVerifyTests
    {
        /// <summary>A new model with an UNMARKED date table, a date key held as a number on a dimension, and a lookup
        /// table nothing relates to. Saved, then opened again, so nothing in it was built in this session.</summary>
        private static async Task<DecisionFixture> ReopenedAsync(string file)
        {
            var f = await BuiltAndSavedAsync(file, markDateTable: false);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
            f.UseNull();
            return f;
        }

        private static async Task<DecisionFixture> BuiltAndSavedAsync(string file, bool markDateTable)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "INDUSTRY_CD", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "ONBRD_DT_KEY", "Int64", null, "human");
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "DATE_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:dim_date", "FY_QTR", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_date", "MTH_ABBR", "String", null, "human");
            await e.CreateTableAsync("ref_codes", "human");
            await e.CreateColumnAsync("table:ref_codes", "CODE_TYPE", "String", null, "human");
            await e.CreateColumnAsync("table:ref_codes", "CODE", "String", null, "human");
            await e.CreateColumnAsync("table:ref_codes", "CODE_DESC", "String", null, "human");
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_DT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/INV_DT_KEY", "column:dim_date/DATE_KEY", null, true, "human");
            await e.CreateMeasureAsync("table:fact_invoice", "Invoice Count", "COUNTROWS ( fact_invoice )", "human");
            if (markDateTable) await e.MarkDateTableAsync("table:dim_date", "CAL_DT", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>The cleanup row's listed objects and its worked-out delete targets, together.</summary>
        private static async Task<(ModelPlanItem Row, string[] Targets)> CleanupAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            Assert.True(row.Optional, "the row is not optional though no report has been read");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.NotNull(set);
            // The real candidate is still offered: the test is about what is held back, not about an empty row.
            Assert.Contains(set.Lines, l => (l.Target ?? "").EndsWith("/INDUSTRY_CD", StringComparison.Ordinal));
            return (row, set.Lines.Select(l => l.Target ?? "").ToArray());
        }

        [Fact(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file.")]
        public async Task An_unmarked_date_tables_own_columns_are_never_offered_while_no_report_has_been_read()
        {
            using var f = await ReopenedAsync("VerifyHarmDate.bim");
            var (row, targets) = await CleanupAsync(f);

            // dim_date is the table every invoice date points at, with a date column of its own: the date table, marked
            // or not. The row says "the date table's columns ... are never offered for removal".
            foreach (var name in new[] { "FY_QTR", "MTH_ABBR" })
            {
                Assert.DoesNotContain(targets, t => t == "column:dim_date/" + name);
                Assert.DoesNotContain(row.Diff, d => d.Contains("dim_date[" + name + "]", StringComparison.Ordinal));
            }
        }

        [Fact(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file.")]
        public async Task A_date_held_as_a_number_is_never_offered_while_the_row_says_dates_never_are()
        {
            using var f = await ReopenedAsync("VerifyHarmDateKey.bim");
            var (row, targets) = await CleanupAsync(f);

            // ONBRD_DT_KEY is the client's onboarding date as yyyymmdd, the kind StarterMeasureTemplates.NeverSummed
            // itself reads as a date. The row's own sentence promises dates are never offered.
            Assert.Contains(row.Why, w => w.Contains("dates", StringComparison.Ordinal) && w.Contains("never offered", StringComparison.Ordinal));
            Assert.DoesNotContain(targets, t => t == "column:dim_client/ONBRD_DT_KEY");
        }

        [Fact(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file.")]
        public async Task The_cleanup_never_offers_every_column_of_a_table()
        {
            using var f = await ReopenedAsync("VerifyHarmTable.bim");
            var (_, targets) = await CleanupAsync(f);

            // Approving all three leaves ref_codes a table with no columns at all (measured on the J8 raw start,
            // reopened: the optional row removed CODE, CODE_TYPE and CODE_DESC and left only the partition).
            var refCodes = new[] { "CODE_TYPE", "CODE", "CODE_DESC" };
            Assert.False(refCodes.All(c => targets.Contains("column:ref_codes/" + c)),
                "the cleanup offers every column of ref_codes: " + string.Join(", ", targets.Where(t => t.StartsWith("column:ref_codes/", StringComparison.Ordinal))));
        }

        // ---- once a report has been read (the optional row's own advice: "Analyze the relevant reports ... before
        // deleting") the row stops being optional, and the check's defect 1 comes back whole. Measured on the J8 raw
        // start, reopened, with one report showing two fields read: "Remove 40 unused things" was the first Needs you
        // row, five rows waited for it before any line was kept, and approving it removed CAL_DT (the marked date
        // table's date), every calendar column and nine hand-written business measures.

        /// <summary>The same shape with dim_date MARKED on CAL_DT, opened again, and a report read that shows only
        /// fact_invoice[INV_AMT].</summary>
        private static async Task<DecisionFixture> ReopenedWithAReportReadAsync(string file)
        {
            var f = await BuiltAndSavedAsync(file, markDateTable: true);
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file, "fact_invoice", "INV_AMT");
            return f;
        }

        [Fact]
        public async Task With_a_report_read_no_row_waits_for_a_cleanup_nobody_has_chosen_a_line_of()
        {
            using var f = await ReopenedWithAReportReadAsync("VerifyHarmReadWaits.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id == "cleanup/model");
            // Brief item 1: "No other row may 'wait for' it." Nobody has kept a line, so it is a list of possibilities.
            var waiting = plan.Items.Where(i => (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => w.Id == "cleanup/model")).Select(i => i.Id).ToArray();
            Assert.True(waiting.Length == 0, "rows wait for the unchosen cleanup: " + string.Join(", ", waiting));
        }

        [Fact(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file.")]
        public async Task With_a_report_read_the_cleanup_never_offers_the_marked_date_tables_date_column()
        {
            using var f = await ReopenedWithAReportReadAsync("VerifyHarmReadDate.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var targets = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines.Select(l => l.Target ?? "").ToArray();

            // CAL_DT is the column dim_date is marked on. Removing it leaves a "date table" with no date: no report
            // reads a key column directly, so a report reading can never be the reason to offer it.
            Assert.DoesNotContain(targets, t => t == "column:dim_date/CAL_DT");
            Assert.DoesNotContain(row.Diff, d => d.Contains("dim_date[CAL_DT]", StringComparison.Ordinal));
        }
    }
}
