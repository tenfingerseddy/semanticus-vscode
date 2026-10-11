using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    // Sol's hands-on test of the final M11 build (90716ced): "wrong number shown as right" items that live in the engine.
    // Each test failed on 90716ced and passes with its fix.

    /// <summary>N2 (H9): Size by table / vpaq_scan counted storage entries as model columns (153 for 55, Sales 42 for 15,
    /// OrderNumber twice). The DMV result below is the shape the storage DMVs return: one row per column, plus one
    /// attribute-hierarchy row (H$) per column, relationship indexes (R$), user hierarchies (U$) and a RowNumber row per
    /// table. Not run live: no connection was available to this lane.</summary>
    public sealed class UatN2VpaqColumnCountTests
    {
        private static ResultSet Rs(string[] names, params object[][] rows) =>
            new ResultSet { Columns = names.Select(n => new ColumnDef { Name = n }).ToArray(), Rows = rows };

        private static readonly string[] SegNames = { "DIMENSION_NAME", "TABLE_ID", "COLUMN_ID", "USED_SIZE" };
        private static readonly string[] ColNames = { "DIMENSION_NAME", "TABLE_ID", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" };

        // Sales has 3 model columns. Storage lists 3 + RowNumber + 3 attribute hierarchies + 1 relationship + 1 user hierarchy.
        private static (ResultSet seg, ResultSet col, ResultSet tbl) Captured() => (
            Rs(SegNames,
                new object[] { "Sales", "Sales (10)", "OrderNumber", 100L },
                new object[] { "Sales", "Sales (10)", "Amount", 200L },
                new object[] { "Sales", "Sales (10)", "CustomerKey", 50L },
                new object[] { "Sales", "H$Sales (10)\\OrderNumber (1)", "OrderNumber", 7L },
                new object[] { "Sales", "H$Sales (10)\\Amount (2)", "Amount", 8L },
                new object[] { "Sales", "R$Sales (10)\\Customer (3)", "Customer", 9L }),
            Rs(ColNames,
                new object[] { "Sales", "Sales (10)", "OrderNumber", "OrderNumber", "2", 30L, 4L },
                new object[] { "Sales", "Sales (10)", "Amount", "Amount", "2", 0L, 0L },
                new object[] { "Sales", "Sales (10)", "CustomerKey", "CustomerKey", "2", 0L, 0L },
                new object[] { "Sales", "Sales (10)", "RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61", "RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61", "2", 0L, 0L },
                new object[] { "Sales", "H$Sales (10)\\OrderNumber (1)", "OrderNumber", "OrderNumber", "1", 11L, 2L },
                new object[] { "Sales", "H$Sales (10)\\Amount (2)", "Amount", "Amount", "1", 12L, 0L },
                new object[] { "Sales", "H$Sales (10)\\CustomerKey (4)", "CustomerKey", "CustomerKey", "1", 13L, 0L },
                new object[] { "Sales", "R$Sales (10)\\Customer (3)", "Customer", "Customer", "1", 14L, 0L },
                new object[] { "Sales", "U$Sales (10)\\Geo (5)", "Geo", "Geo", "1", 15L, 0L }),
            Rs(new[] { "DIMENSION_NAME", "ROWS_COUNT" }, new object[] { "Sales", 500L }));

        [Fact]
        public void A_storage_structure_is_not_a_column_and_a_column_is_listed_once()
        {
            var (seg, col, tbl) = Captured();
            var r = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import);

            Assert.Equal(3, r.ColumnCount);
            Assert.Equal(3, r.Tables.Single().Columns);
            Assert.Equal(new[] { "Amount", "CustomerKey", "OrderNumber" }, r.TopColumns.Select(c => c.Column).OrderBy(x => x).ToArray());
            Assert.Single(r.TopColumns, c => c.Column == "OrderNumber");
        }

        [Fact]
        public void The_bytes_of_the_structures_are_not_lost_when_they_stop_being_columns()
        {
            var (seg, col, tbl) = Captured();
            var all = col.Rows.Where(x => !x[2].ToString().StartsWith("RowNumber")).Sum(x => (long)x[5] + (long)x[6]) + seg.Rows.Sum(x => (long)x[3]);
            var r = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import);

            Assert.Equal(all, r.ModelSize);
            Assert.Equal(all, r.Tables.Single().Size);
            // the base column keeps ITS OWN data (segments of the base structure), not a hierarchy's
            Assert.Equal(100L, r.TopColumns.Single(c => c.Column == "OrderNumber").DataSize);
        }

        [Fact]
        public void Without_a_table_id_the_name_prefix_still_tells_a_structure_from_a_column()
        {
            // an older or reduced result: no TABLE_ID, the hierarchy and relationship rows carry the prefix in COLUMN_ID
            var seg = Rs(new[] { "DIMENSION_NAME", "COLUMN_ID", "USED_SIZE" }, new object[] { "Sales", "Amount", 5L });
            var col = Rs(new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" },
                new object[] { "Sales", "Amount", "Amount", "2", 1L, 0L },
                new object[] { "Sales", "Amount", "H$Amount", "1", 2L, 0L },
                new object[] { "Sales", "Customer", "R$Customer", "1", 3L, 0L });
            var r = VertiPaq.Compute(seg, col, Rs(new[] { "DIMENSION_NAME", "ROWS_COUNT" }, new object[] { "Sales", 1L }), 25, VpaqStorageMode.Import);

            Assert.Equal(1, r.ColumnCount);
            Assert.Equal("Amount", r.TopColumns.Single().Column);
        }

        [Fact]
        public void The_models_own_column_list_decides_what_a_column_is_when_it_was_read()
        {
            // a structure row that carries no prefix and no TABLE_ID cannot be told apart by name; the model's list can
            var seg = Rs(new[] { "DIMENSION_NAME", "COLUMN_ID", "USED_SIZE" }, new object[] { "Sales", "Amount", 5L });
            var col = Rs(new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" },
                new object[] { "Sales", "Amount", "Amount", "2", 1L, 0L },
                new object[] { "Sales", "Customer (rel)", "Customer (rel)", "1", 3L, 0L });
            var modelTables = Rs(new[] { "ID", "Name" }, new object[] { 7L, "Sales" });
            var modelColumns = Rs(new[] { "TableID", "ExplicitName", "InferredName", "Type" },
                new object[] { 7L, "Amount", null, 1L },
                new object[] { 7L, "RowNumber-1", null, 3L });
            var r = VertiPaq.Compute(seg, col, Rs(new[] { "DIMENSION_NAME", "ROWS_COUNT" }, new object[] { "Sales", 1L }), 25, VpaqStorageMode.Import,
                null, modelTables, modelColumns);

            Assert.Equal(1, r.ColumnCount);
            Assert.Equal(5L + 1 + 3, r.ModelSize);
        }

        [Fact]
        public void A_model_list_that_matches_no_storage_row_is_set_aside_not_trusted_into_a_count_of_zero()
        {
            var (seg, col, tbl) = Captured();
            var r = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import, null,
                Rs(new[] { "ID", "Name" }, new object[] { 1L, "Elsewhere" }),
                Rs(new[] { "TableID", "ExplicitName", "InferredName", "Type" }, new object[] { 1L, "Other", null, 1L }));
            Assert.Equal(3, r.ColumnCount);
        }

        [Fact]
        public void A_model_list_that_failed_to_read_changes_nothing()
        {
            var (seg, col, tbl) = Captured();
            var r = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import, null,
                ResultSet.FromError("no such DMV"), ResultSet.FromError("no such DMV"));
            Assert.Equal(3, r.ColumnCount);
        }
    }

    /// <summary>N3 (H10): impact_assessment with reportPaths showed fieldsUsed=0 and visuals=0 for a report that uses
    /// several fields and visuals, while the same answer's impact list named the visuals it touches. The row was
    /// built before the report was read; its numbers now come from the reading that builds the impact list.</summary>
    public sealed class UatN3ImpactCoverageTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static string CardOf(string entity, string measure) =>
            "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
            "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"" + measure + "\" } } } ] } } } } }";

        // Three visuals on one report; they use three distinct model measures, and two of them depend on the assessed one.
        private static async Task<(string reportRoot, string root, string[] used)> Fixture(LocalEngine engine, string dir)
        {
            var table = (await engine.ListMeasuresAsync()).First().Table;
            var other = (await engine.ListMeasuresAsync()).First().Name;
            var root = await engine.CreateMeasureAsync("table:" + table, "Cov Base", "1", "human");
            await engine.CreateMeasureAsync("table:" + table, "Cov User", "[Cov Base] + 1", "human");
            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var reportRoot = Path.Combine(dir, "Reports");
            var def = Path.Combine(reportRoot, "R.Report", "definition");
            Directory.CreateDirectory(Path.Combine(def, "pages", "Overview", "visuals", "A"));
            Directory.CreateDirectory(Path.Combine(def, "pages", "Overview", "visuals", "B"));
            Directory.CreateDirectory(Path.Combine(def, "pages", "Overview", "visuals", "C"));
            File.WriteAllText(Path.Combine(def, "report.json"), "{}");
            File.WriteAllText(Path.Combine(def, "pages", "Overview", "visuals", "A", "visual.json"), CardOf(entity, "Cov Base"));
            File.WriteAllText(Path.Combine(def, "pages", "Overview", "visuals", "B", "visual.json"), CardOf(entity, "Cov User"));
            File.WriteAllText(Path.Combine(def, "pages", "Overview", "visuals", "C", "visual.json"), CardOf(entity, other));
            return (reportRoot, root, new[] { "Cov Base", "Cov User", other });
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string Model) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-uat-n3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new ProTier(), root), sessions, root, model);
        }

        [Fact]
        public async Task A_report_handed_in_by_path_reports_the_fields_and_visuals_it_was_read_to_use()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var (reportRoot, root, used) = await Fixture(x.Engine, x.Root);

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
                    { ObjectRef = root, Intent = "change", Scope = "modelAndReports", ReportPaths = new[] { reportRoot } });

                    var hit = Assert.Single(result.ReportImpact);
                    Assert.Equal(2, hit.Visuals);                       // A and B touch the assessed measure or a dependent
                    var row = Assert.Single(result.Checked.Reports);
                    Assert.Equal("checked", row.State);
                    Assert.Equal(used.Length, row.FieldsUsed);          // 3 distinct fields read, was 0
                    Assert.Equal(3, row.Visuals);                       // all 3 visuals read, was 0
                    Assert.True(row.Visuals >= hit.Visuals, "the coverage can never read as less than the impact it sits beside");
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_report_on_the_models_own_list_agrees_with_the_check_that_read_it()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var (reportRoot, root, used) = await Fixture(x.Engine, x.Root);
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "R", Path = reportRoot } }, "human");
                    var checkedRows = await new EngineRpcTarget(x.Engine).checkReports(null, true);
                    var read = Assert.Single(checkedRows.Reports);

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = root, Scope = "modelAndReports" });
                    var row = Assert.Single(result.Checked.Reports);
                    Assert.Equal(read.FieldsUsed, row.FieldsUsed);
                    Assert.Equal(read.Visuals, row.Visuals);
                    Assert.Equal(used.Length, row.FieldsUsed);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_path_that_holds_no_report_stays_at_zero_and_says_it_could_not_be_read()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var (_, root, _) = await Fixture(x.Engine, x.Root);
                    var empty = Path.Combine(x.Root, "NoReport");
                    Directory.CreateDirectory(empty);

                    var result = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
                    { ObjectRef = root, Scope = "modelAndReports", ReportPaths = new[] { empty } });
                    var row = Assert.Single(result.Checked.Reports);
                    Assert.NotEqual("checked", row.State);
                    Assert.Equal(0, row.FieldsUsed);
                    Assert.Equal(0, row.Visuals);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }

    /// <summary>N4 (H11): the Workflows list read a run that finished with nothing verified as "passed". The history
    /// report now keeps the verdict its own evidence page opens with, which is what a list can show.</summary>
    public sealed class UatN4RunVerdictTests
    {
        private static string Answers1(string target) =>
            "{\"target\":\"" + target + "\",\"changeIntent\":\"Clarify the business name\"," +
            "\"reportPaths\":{\"declined\":true,\"reason\":\"No report definitions are in this review scope\"}}";

        private static async Task<WorkflowRunView> RunToTheEnd(LocalEngine engine)
        {
            await FrozenStarter.UseAsync(engine, "check-blast-radius");
            var target = (await engine.ListMeasuresAsync()).First().Ref;
            var run = await engine.StartWorkflowAsync("check-blast-radius", "human");
            run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", Answers1(target), "human");
            run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-2", "{\"baselineCapture\":{\"declined\":true,\"reason\":\"No live query model is connected\"}}", "human");
            run = await engine.SubmitWorkflowStepAsync(run.RunId, "step-3", "{}", "human");
            return await engine.SubmitWorkflowStepAsync(run.RunId, "step-4",
                "{\"decision\":\"Proceed to reviewed implementation\",\"residualRisk\":\"Report bindings were explicitly excluded\"}", "human");
        }

        [Fact]
        public async Task A_run_finished_with_the_checks_off_keeps_its_own_verdict_and_never_reads_as_verified()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("NoChecks.bim");
            await f.Engine.SetWorkflowEnforcementAsync("off", "human");
            var run = await RunToTheEnd(f.Engine);
            Assert.Equal("completed", run.Status);
            await f.Sessions.Ledger.FlushAsync();

            var row = (await f.Engine.GetPlanHistoryAsync()).Rows.Single(r => r.Kind == "run");
            Assert.False(string.IsNullOrEmpty(row.Report.Verdict), "the report keeps a verdict word");
            Assert.NotEqual("Verified", row.Report.Verdict);   // the list used to call this "passed"
            Assert.Equal(0, row.Report.StepsVerified);
            Assert.True(row.Report.StepsTotal > 0);
            var artifact = await f.Engine.ExportWorkflowEvidenceAsync(run.RunId);
            using var json = System.Text.Json.JsonDocument.Parse(artifact.Json);
            Assert.Equal(json.RootElement.GetProperty("verdict").ToString(), row.Report.Verdict, ignoreCase: true);
        }

        [Fact]
        public async Task The_kept_verdict_is_the_one_the_runs_own_evidence_page_opens_with()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("WithChecks.bim");
            var run = await RunToTheEnd(f.Engine);
            await f.Sessions.Ledger.FlushAsync();

            var row = (await f.Engine.GetPlanHistoryAsync()).Rows.Single(r => r.Kind == "run");
            var artifact = await f.Engine.ExportWorkflowEvidenceAsync(run.RunId);
            using var json = System.Text.Json.JsonDocument.Parse(artifact.Json);
            Assert.Equal(json.RootElement.GetProperty("verdict").ToString(), row.Report.Verdict, ignoreCase: true);
        }
    }
}
