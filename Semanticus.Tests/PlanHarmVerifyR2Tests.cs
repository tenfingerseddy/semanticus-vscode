// Verifier, m11/plan-harm 14516875 (29 Sep 2026), round 2. Left failing on purpose; not fixes.
//
// The ruling the lane implements (coordinator, 28 Sep): the Plan's cleanup row is always optional, last and never
// waited on, and it never offers a date table's columns, dates or date keys, keys or any column a relationship uses
// (every part of a calculated key), name columns, any measure, code and lookup tables, a fact table's numbers, or
// anything built in this session; with reports read, only what they do not use, saying how many reports were read.
// The starter measures never total a key or a date key. The row says the never list in its own words.
//
// Each test builds one shape small, saves it and opens it again (a fresh session, so nothing in it was built in this
// session), then reads the cleanup row and the lines "Work it out" proposes, with no report read and with one read.
// Every model keeps one plain unused column (dim_client[NOTE_TXT], or the shape's own) that the row must still offer,
// so a missing row can never pass a test. What each test finds offered, at 14516875:
//   S12  a single flat table (the commonest raw CSV start): its numbers QTY, AMOUNT, DISCOUNT (a fact table's numbers;
//        StarterMeasureTemplates.FactTables needs the many side of a relationship, so a lone table has none);
//   S13  a header fact that a line fact looks up: INV_AMT, TAX_AMT (on a one side, so not a fact by that reading);
//   S5   a code table with a number column (a numeric code, a sort order): STATUS_DESC, SORT_ORD (the guard's code table
//        is all text, or named ref/code/lookup);
//   S14  a month-grain calendar with no date column: CAL_YEAR, CAL_QTR, MONTH_OF_YEAR, FISCAL_YEAR (a date table needs a
//        column typed as a date or named DT/DATE);
//   S17  the name column named after its entity on a plural table: Products[Product], Customers[Customer];
//   B7   a chosen report that could not be read at all (a .pbix) is counted as read: "1 report's use of this model has
//        been read, and it does not show these", over a sweep that is model-only (AnalyzeReports: read = 0);
//   B2   a report checked again at the SAME revision is not what the row answers from (the Plan's cache key does not
//        move): a field the report now shows is still offered, and a reading that is now partial still has a row;
//   B6   Jev's total of a relationship column whose name says nothing ("Client") is not refused: the fence on Jev's
//        answer reads the name only, while the rule refuses every key and relationship column;
//   S4b  the starter row totals a part of a calculated key the relationship uses ("Total PRJ_SEQ");
//   S15  an assistant's removal of a measure is filed on the cleanup row, whose own sentence says measures never are.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanHarmVerifyR2Tests
    {
        /// <summary>Build a shape, save it, and open it again. The frame (a client dimension with a name column and
        /// the control column NOTE_TXT, an invoice fact related to it, one hand-written measure) comes first unless
        /// <paramref name="frame"/> is false. With <paramref name="reportRead"/>, one local report showing
        /// <paramref name="reportTable"/>[<paramref name="reportColumn"/>] is chosen and checked.</summary>
        private static async Task<DecisionFixture> ShapeAsync(string file, Func<LocalEngine, Task> build, bool reportRead,
            bool pro = true, bool frame = true, string reportTable = "fact_invoice", string reportColumn = "INV_AMT")
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            if (frame)
            {
                await e.CreateTableAsync("dim_client", "human");
                await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
                await e.CreateColumnAsync("table:dim_client", "NOTE_TXT", "String", null, "human");
                await e.CreateTableAsync("fact_invoice", "human");
                await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
                await e.CreateMeasureAsync("table:fact_invoice", "Invoice Count", "COUNTROWS ( fact_invoice )", "human");
            }
            await build(e);
            await f.SaveAsAsync(file);
            f.UseNull();
            if (reportRead)
                await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, file, reportTable, reportColumn);   // opens it again first
            else
            {
                await e.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
                f.UseNull();
            }
            return f;
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

        private static string FileFor(string shape, bool reportRead, bool pro = true) =>
            "VR2-" + shape + (reportRead ? "-read" : "-unread") + (pro ? "" : "-free") + ".bim";

        // ---- S4b: the starter row and a calculated key -------------------------------------------------------

        [Fact]
        public async Task S4b_the_starter_row_never_totals_a_part_of_a_calculated_relationship_key()
        {
            // A composite key on the fact, built by a calculated column the relationship uses. The cleanup knows PRJ_SEQ is
            // part of a key (CleanupGuard.Key follows a calculated key's parts); the starter rule does not, and totals it.
            using var f = await ShapeAsync(FileFor("S4b", false), async e =>
            {
                await e.CreateTableAsync("dim_project", "human");
                await e.CreateColumnAsync("table:dim_project", "PRJ_CODE", "String", null, "human");
                await e.CreateColumnAsync("table:dim_project", "PRJ_NM", "String", null, "human");
                await e.CreateTableAsync("fact_timesheet", "human");
                await e.CreateColumnAsync("table:fact_timesheet", "PRAC_CD", "String", null, "human");
                await e.CreateColumnAsync("table:fact_timesheet", "PRJ_SEQ", "Int64", null, "human");
                await e.CreateColumnAsync("table:fact_timesheet", "HRS", "Double", null, "human");
                await e.CreateCalculatedColumnAsync("table:fact_timesheet", "PRJ_LINK", "fact_timesheet[PRAC_CD] & \"-\" & fact_timesheet[PRJ_SEQ]", "human");
                await e.SetObjectPropertyAsync("column:fact_timesheet/PRJ_LINK", "DataType", "String", "human");
                await e.SetObjectPropertyAsync("column:fact_timesheet/PRJ_LINK", "IsHidden", "true", "human");
                await e.CreateRelationshipAsync("column:fact_timesheet/PRJ_LINK", "column:dim_project/PRJ_CODE", null, true, "human");
            }, reportRead: false);
            var plan = await f.Engine.GetModelPlanAsync();
            var starter = plan.Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal)).SelectMany(i => i.Diff ?? Array.Empty<string>()).ToArray();
            Assert.Contains(starter, d => d.Contains("Total HRS", StringComparison.Ordinal));   // the starter row is there
            Assert.True(!starter.Any(d => d.Contains("Total PRJ_SEQ", StringComparison.Ordinal)),
                "the starter row totals a part of the key the relationship uses: " + string.Join("; ", starter));
        }

        // ---- S5: a code table with numbers, named without ref, code or lookup ----------------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false), InlineData(true)]
        public async Task S5_a_code_table_with_a_number_column_is_never_offered(bool reportRead)
        {
            using var f = await ShapeAsync(FileFor("S5", reportRead), async e =>
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
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:lkp_priority/PRIORITY_ID", "column:lkp_priority/PRIORITY_TXT", "column:lkp_priority/WEIGHT");
            AssertNeverOffered(row, targets, "column:status/STATUS_CD", "column:status/STATUS_DESC", "column:status/SORT_ORD", "column:fact_invoice/STATUS_CD");
        }

        // ---- S12: one flat table, no relationships: the commonest raw CSV start ---------------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false, true), InlineData(true, true), InlineData(false, false)]
        public async Task S12_a_single_flat_tables_numbers_are_never_offered(bool reportRead, bool pro)
        {
            using var f = await ShapeAsync(FileFor("S12", reportRead, pro), async e =>
            {
                await e.CreateTableAsync("sales", "human");
                foreach (var (n, t) in new[] { ("ORDER_ID", "Int64"), ("ORDER_DATE", "DateTime"), ("CUSTOMER_NAME", "String"), ("PRODUCT", "String"),
                                               ("QTY", "Int64"), ("AMOUNT", "Double"), ("DISCOUNT", "Double"), ("NOTE_TXT", "String") })
                    await e.CreateColumnAsync("table:sales", n, t, null, "human");
            }, reportRead, pro, frame: false, reportTable: "sales", reportColumn: "PRODUCT");
            var (row, targets) = await CleanupAsync(f, "column:sales/NOTE_TXT");
            AssertNeverOffered(row, targets, "column:sales/ORDER_ID", "column:sales/ORDER_DATE", "column:sales/CUSTOMER_NAME");
            AssertNeverOffered(row, targets, "column:sales/QTY", "column:sales/AMOUNT", "column:sales/DISCOUNT");
        }

        // ---- S13: a header and line fact ---------------------------------------------------------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false), InlineData(true)]
        public async Task S13_a_header_facts_numbers_are_never_offered(bool reportRead)
        {
            using var f = await ShapeAsync(FileFor("S13", reportRead), async e =>
            {
                await e.CreateColumnAsync("table:fact_invoice", "INV_NO", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "TAX_AMT", "Double", null, "human");
                await e.CreateTableAsync("fact_invoice_line", "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "INV_NO", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "LINE_AMT", "Double", null, "human");
                await e.CreateColumnAsync("table:fact_invoice_line", "QTY", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice_line/INV_NO", "column:fact_invoice/INV_NO", null, true, "human");
            }, reportRead);
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:fact_invoice_line/LINE_AMT", "column:fact_invoice_line/QTY");
            AssertNeverOffered(row, targets, "column:fact_invoice/INV_AMT", "column:fact_invoice/TAX_AMT");
        }

        // ---- S14: a month-grain calendar with no date column ----------------------------------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false), InlineData(true)]
        public async Task S14_a_month_grain_calendars_columns_are_never_offered(bool reportRead)
        {
            using var f = await ShapeAsync(FileFor("S14", reportRead), async e =>
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
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:dim_period/PERIOD_KEY", "column:dim_period/MONTH_NM", "column:fact_budget/BUDGET_AMT");
            AssertNeverOffered(row, targets, "column:dim_period/CAL_YEAR", "column:dim_period/CAL_QTR", "column:dim_period/MONTH_OF_YEAR", "column:dim_period/FISCAL_YEAR");
        }

        // ---- S15: the assistant door adds a removal of a held-back object --------------------------------------------

        [Fact(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file.")]
        public async Task S15_an_assistants_removal_of_a_measure_does_not_become_the_cleanup_rows_own_offer()
        {
            using var f = await ShapeAsync(FileFor("S15", false), e => Task.CompletedTask, reportRead: false);
            await f.Engine.AddPlanItemAsync("measure:fact_invoice/Invoice Count", "delete_if_unused", null, "Remove Invoice Count", null, null, "agent");

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            var lines = row.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>();
            // The row says measures are never offered for removal; a line on the row that removes one contradicts it.
            Assert.Contains(row.Why, w => w.Contains("measures", StringComparison.Ordinal) && w.Contains("never offered", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => string.Equals(l.Target, "measure:fact_invoice/Invoice Count", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(row.Diff ?? Array.Empty<string>(), d => d.Contains("Invoice Count", StringComparison.Ordinal));
        }

        // ---- S17: a name column named after its entity -----------------------------------------------------------------

        [Theory(Skip = "Superseded by PlanHarmVerifyR5Tests (Kane 30 Sep 2026, the cleanup row is a suggestion list): this case asserted the old never-offered rule; its restatement is in that file."), InlineData(false), InlineData(true)]
        public async Task S17_a_name_column_named_after_its_entity_is_never_offered(bool reportRead)
        {
            using var f = await ShapeAsync(FileFor("S17", reportRead), async e =>
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
            var (row, targets) = await CleanupAsync(f);
            AssertNeverOffered(row, targets, "column:Products/Product", "column:Customers/Customer");
        }

        // ---- B2: the cached facts: a report whose use changed, checked again at the same revision ---------------------

        [Fact]
        public async Task B2_a_report_checked_again_after_it_changed_is_what_the_row_answers_from_even_at_the_same_revision()
        {
            var file = FileFor("B2", true);
            using var f = await ShapeAsync(file, async e =>
            {
                await e.CreateColumnAsync("table:dim_client", "SEGMENT_TXT", "String", null, "human");
            }, reportRead: true);
            var before = await f.Engine.GetModelPlanAsync();
            var row = Assert.Single(before.Items, i => i.Id == "cleanup/model");
            Assert.Contains(row.Diff, d => d.EndsWith(" dim_client[NOTE_TXT]", StringComparison.Ordinal));

            // The report now shows dim_client[NOTE_TXT] too (a person added it to a visual), and is checked again. No model
            // edit in between: the revision does not move.
            var visual = Path.Combine(f.Workspace, "Reports-" + Path.GetFileNameWithoutExtension(file), "Main.Report", "definition", "pages", "p1", "visuals", "v1", "visual.json");
            string Projection(string table, string column) =>
                "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": " + JsonSerializer.Serialize(table) + " } }, \"Property\": " + JsonSerializer.Serialize(column) + " } } }";
            System.IO.File.WriteAllText(visual, "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                + Projection("fact_invoice", "INV_AMT") + ", " + Projection("dim_client", "NOTE_TXT") + " ] } } } } }");
            await PlanHarmKit.CheckReportsAgainAsync(f);

            // The sweep itself knows at once; the Plan row must say the same thing.
            var sweep = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(sweep.Items, i => i.Ref == "column:dim_client/NOTE_TXT" && i.Verdict == "safe");
            var after = await f.Engine.GetModelPlanAsync();
            var again = Assert.Single(after.Items, i => i.Id == "cleanup/model");
            Assert.True(!again.Diff.Any(d => d.EndsWith(" dim_client[NOTE_TXT]", StringComparison.Ordinal)),
                "the row still offers NOTE_TXT, which the report checked again now shows: " + string.Join("; ", again.Diff) + " / " + again.Sub);
        }

        [Fact]
        public async Task B2_a_report_checked_again_that_can_no_longer_be_fully_read_takes_the_row_away_even_at_the_same_revision()
        {
            var file = FileFor("B2b", true);
            using var f = await ShapeAsync(file, e => Task.CompletedTask, reportRead: true);
            Assert.Contains(OpenRows(await f.Engine.GetModelPlanAsync()), i => i.Id == "cleanup/model");

            // A second visual that cannot be parsed (a half-written file). Checked again with no model edit in between, the
            // reading is partial, so nothing can be called unused: the sweep demotes every "safe" to "caution".
            var v2 = Path.Combine(f.Workspace, "Reports-" + Path.GetFileNameWithoutExtension(file), "Main.Report", "definition", "pages", "p1", "visuals", "v2");
            Directory.CreateDirectory(v2);
            System.IO.File.WriteAllText(Path.Combine(v2, "visual.json"), "{ \"visual\": { \"query\": ");
            await PlanHarmKit.CheckReportsAgainAsync(f);
            var sweep = await f.Engine.UnusedObjectsAsync();
            Assert.DoesNotContain(sweep.Items, i => i.Verdict == "safe");

            var plan = await f.Engine.GetModelPlanAsync();
            var row = OpenRows(plan).SingleOrDefault(i => i.Id == "cleanup/model");
            Assert.True(row == null, "the row still offers " + string.Join("; ", row?.Diff ?? Array.Empty<string>())
                + " from a reading that is no longer the one in force: " + (row?.Why?.FirstOrDefault() ?? ""));
        }

        // ---- B7: a chosen report that could not be read at all ---------------------------------------------------

        [Fact]
        public async Task B7_a_chosen_report_that_could_not_be_read_is_never_counted_as_read()
        {
            // A report chosen under Lineage whose definition cannot be read: a legacy .pbix, a PBIRLegacy published report,
            // a moved folder. Checking it records "could not be fully checked"; the sweep falls back to the model alone
            // (AnalyzeReports: read = 0). The row must not tell the person that a report was read and does not show these.
            var file = FileFor("B7", false);
            using var f = await ShapeAsync(file, e => Task.CompletedTask, reportRead: false);
            var pbix = Path.Combine(f.Workspace, "Sales Report.pbix");
            System.IO.File.WriteAllBytes(pbix, new byte[] { 0x50, 0x4B, 0x03, 0x04 });   // a zip header: what a .pbix starts with
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Sales Report", Path = pbix } }, "human");
            var check = await f.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
            Assert.Equal(0, check.Checked);   // nothing was read

            var plan = await f.Engine.GetModelPlanAsync();
            var row = OpenRows(plan).SingleOrDefault(i => i.Id == "cleanup/model");
            Assert.True(row == null || !row.Why.Any(w => w.Contains("has been read", StringComparison.Ordinal) || w.Contains("the report that was read", StringComparison.Ordinal)),
                "no report was read, and the row says: " + string.Join(" / ", row?.Why ?? Array.Empty<string>()));
        }

        private static System.Collections.Generic.IEnumerable<ModelPlanItem> OpenRows(ModelPlanView plan) =>
            plan.Items.Where(i => i.Status != ModelPlanStatuses.Skipped && i.Status != ModelPlanStatuses.Done
                               && i.State != ModelPlanItemStates.Applied && i.State != ModelPlanItemStates.Done);

        // ---- B6: the starter row on the Jev path, for a renamed foreign key ------------------------------------------

        private static string StarterFixture(params (string Digest, string Choice, double Confidence)[] rows) =>
            "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[" + string.Join(",", rows.Select(r =>
                "{\"digest\":" + JsonSerializer.Serialize(r.Digest) + ",\"point\":\"measure.starter\""
                + ",\"usage\":{\"input_tokens\":700,\"output_tokens\":20}"
                + ",\"answers\":{\"aggregation\":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(r.Choice)
                + ",\"confidence\":" + r.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}")) + "]}";

        [Fact]
        public async Task B6_Jevs_total_of_a_relationship_column_whose_name_says_nothing_is_refused_like_the_rules()
        {
            // The rule never totals a key or a column a relationship uses, whatever its name (RuleChoice). Jev's answer is
            // fenced by the column's NAME alone (StarterMeasuresFamily.Map, NeverSummed), so the foreign key "Client" (the
            // many end of a relationship, named as people name it) can come back as "Total Client".
            var f = new DecisionFixture(TestEntitlements.Pro);
            using (f)
            {
                var e = f.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Customer", "human");
                await e.CreateColumnAsync("table:Customer", "Client", "Int64", null, "human");
                await e.CreateColumnAsync("table:Customer", "Customer Name", "String", null, "human");
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Client", "Int64", null, "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.CreateRelationshipAsync("column:Sales/Client", "column:Customer/Client", null, null, "human");
                await f.SaveAsAsync("VR2-B6.bim");
                f.UseNull();

                e.StarterJevDecidesForTest = true;
                await f.ConsentAsync();
                var point = DecisionRegistry.Require("measure.starter");
                var prepared = await f.Sessions.Current.ReadAsync(m => StarterMeasuresFamily.Prepare(m, e.TryGetCurrentFacts()));
                var digests = prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName,
                    a => new DecisionRequest(point.Name, point.SchemaVersion, a.Snapshot, a.Questions).Digest, StringComparer.Ordinal);
                Assert.Equal("none", prepared.Asks.Single(a => a.ColumnName == "Client").RuleOption);   // the rule's own answer
                f.UseRecorded(StarterFixture((digests["Sales/Client"], "sum", 0.97), (digests["Sales/Amount"], "sum", 0.97)));

                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                var plan = await e.GetModelPlanAsync();
                var targets = plan.Items.Where(i => i.ChangeSet != null).SelectMany(i => i.ChangeSet.Lines).Select(l => l.Target ?? "").ToArray();
                Assert.Contains("measure:Sales/Total Amount", targets);   // Jev's answers were read
                Assert.DoesNotContain("measure:Sales/Total Client", targets);
            }
        }
    }
}
