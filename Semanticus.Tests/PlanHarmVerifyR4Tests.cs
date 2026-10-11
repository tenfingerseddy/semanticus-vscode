// Verifier, m11/plan-harm 875ff87f (30 Sep 2026), round 4.
//
// THE CLEANUP OFFER RULE, version 2 (coordinator, 29 Sep) replaces rounds 1 to 3's never-offer list as the gate. Among other
// things it never offers anything on a table nothing relates to. Round 2's S12 case
// (PlanHarmVerifyR2Tests.S12_a_single_flat_tables_numbers_are_never_offered) kept its plain control column on the flat table
// itself (sales[NOTE_TXT]), so under version 2 its three cases stop at the control, before any harm assertion runs. The ruling
// moves the control and never the rule: the verifier relabels S12 here, with the frame's plain column dim_client[NOTE_TXT] as
// the control, and keeps every harm assertion as it was. The round 2 file stays unedited.
//
// The shape is round 2's: the frame (dim_client with CLIENT_KEY, CLNT_NM and the control NOTE_TXT, an invoice fact related to
// it, one hand-written measure) beside one flat sales table that relates to nothing. The model is saved and opened again (a
// fresh session, so nothing in it was built in this session), then the cleanup row and the lines "Work it out" proposes are
// read, with no report read and with one read report showing sales[PRODUCT].

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHarmVerifyR4Tests
    {
        /// <summary>Round 2's frame with round 2's flat table beside it, saved and opened again. With
        /// <paramref name="reportRead"/>, one local report showing sales[PRODUCT] is chosen and checked.</summary>
        private static async Task<DecisionFixture> FlatBesideTheFrameAsync(string file, bool reportRead, bool pro)
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
            await e.CreateTableAsync("sales", "human");
            foreach (var (n, t) in new[] { ("ORDER_ID", "Int64"), ("ORDER_DATE", "DateTime"), ("CUSTOMER_NAME", "String"), ("PRODUCT", "String"),
                                           ("QTY", "Int64"), ("AMOUNT", "Double"), ("DISCOUNT", "Double"), ("NOTE_TXT", "String") })
                await e.CreateColumnAsync("table:sales", n, t, null, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            if (reportRead)
                await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file, "sales", "PRODUCT");   // opens it again first
            else
            {
                await e.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
                f.UseNull();
            }
            return f;
        }

        /// <summary>The cleanup row and the delete targets "Work it out" proposes for it. The control must be offered, so a
        /// missing row can never pass.</summary>
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

        private static string Bracketed(string r)   // "column:sales/QTY" is "sales[QTY]" in the row's diff
        {
            var body = r.Substring(r.IndexOf(':') + 1);
            var slash = body.IndexOf('/');
            return slash < 0 ? body : body.Substring(0, slash) + "[" + body.Substring(slash + 1) + "]";
        }

        private static string FileFor(string shape, bool reportRead, bool pro) =>
            "VR4-" + shape + (reportRead ? "-read" : "-unread") + (pro ? "" : "-free") + ".bim";

        // ---- S12, relabelled: one flat table that relates to nothing, beside the frame ----------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task S12_relabelled_a_single_flat_tables_columns_are_never_offered_with_the_frames_column_as_the_control(bool reportRead, bool pro)
        {
            using var f = await FlatBesideTheFrameAsync(FileFor("S12", reportRead, pro), reportRead, pro);
            var (row, targets) = await CleanupAsync(f);
            // Round 2's harm assertions, unchanged: the flat table's key, date and name, and its numbers.
            AssertNeverOffered(row, targets, "column:sales/ORDER_ID", "column:sales/ORDER_DATE", "column:sales/CUSTOMER_NAME");
            AssertNeverOffered(row, targets, "column:sales/QTY", "column:sales/AMOUNT", "column:sales/DISCOUNT");
            // Version 2: a table nothing relates to is never offered, its text included (round 2's old control).
            AssertNeverOffered(row, targets, "column:sales/NOTE_TXT", "column:sales/PRODUCT");
        }

        // ---- The held-out run (round 4): two harmful offers, in every mode -------------------------------------------
        //
        // Sol's blind held-out set (10 models, labels sealed before the one run; evidence in
        // lane-reports/v-plan-harm-r4-evidence/heldout) failed the stop rule's harm side: two columns labelled H were offered
        // in all four modes (Free and Pro, no report and one read report). Both are the ruling's residual risk R2, a text
        // column that is secretly a date or a key with no signal word, which the ruling charges through the held-out set.
        // These two cases are that harm, rebuilt small. They are REGRESSION EVIDENCE FROM A SPENT SET: the set cannot judge a
        // change again, and a change is judged on a fresh held-out set (the ruling: "no tuning on that set"). The belts are
        // frozen, so neither is to be fixed by adding a word. The ruling's first fallback (drop fact and bridge text) reaches
        // H1; neither it nor the second (unlock only by a name for the table or the default label) reaches H2, whose table
        // has a column named for it: H2 is the coordinator's and Kane's decision (the third fallback, or another design).

        /// <summary>The frame plus <paramref name="build"/>, saved and opened again; with <paramref name="reportRead"/>, one
        /// read report shows fact_invoice[INV_AMT].</summary>
        private static async Task<DecisionFixture> HeldOutShapeAsync(string file, Func<LocalEngine, Task> build, bool reportRead, bool pro)
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
            await build(e);
            await f.SaveAsAsync(file);
            f.UseNull();
            if (reportRead)
                await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file, "fact_invoice", "INV_AMT");   // opens it again first
            else
            {
                await e.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
                f.UseNull();
            }
            return f;
        }

        /// <summary>H1 (held-out adv-4, UsageLedger[BilledAs]): a fact's text column that holds the billing date as text
        /// ("2024-01-31") and has no date word. The promise: dates are never offered, however they are held. The fact is only
        /// ever the many side and holds an amount, so the label gate is skipped and its text is offered.</summary>
        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task H1_a_facts_text_that_holds_a_date_with_no_date_word_is_never_offered(bool reportRead, bool pro)
        {
            using var f = await HeldOutShapeAsync(FileFor("H1", reportRead, pro),
                e => e.CreateColumnAsync("table:fact_invoice", "BILLED_AS", "String", null, "human"), reportRead, pro);
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:fact_invoice/BILLED_AS");
        }

        /// <summary>H2 (held-out real-wwi-dw, Stock Item[Barcode]): a dimension's text column that identifies each row (a
        /// barcode, the item's alternate key) with no key word, on a table whose label is found. The promise: keys are never
        /// offered, whatever their name.</summary>
        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task H2_a_dimensions_text_that_is_its_alternate_key_with_no_key_word_is_never_offered(bool reportRead, bool pro)
        {
            using var f = await HeldOutShapeAsync(FileFor("H2", reportRead, pro), async e =>
            {
                await e.CreateTableAsync("dim_item", "human");
                await e.CreateColumnAsync("table:dim_item", "ITEM_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:dim_item", "Item", "String", null, "human");
                await e.CreateColumnAsync("table:dim_item", "BARCODE", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "ITEM_KEY", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/ITEM_KEY", "column:dim_item/ITEM_KEY", null, true, "human");
            }, reportRead, pro);
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:dim_item/BARCODE");
        }
    }
}
