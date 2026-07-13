using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Tests tab's MCP door (the "Prove" intent, docs/tests-tab-spec.md) — its own
    /// [McpServerToolType] class, discovered by Program.cs's WithToolsFromAssembly() alongside the
    /// main McpTools surface (the McpToolsHarness precedent). Read-only w.r.t. the model: a test
    /// run never mutates anything, so nothing here carries an undo entry or a broadcast.
    /// </summary>
    [McpServerToolType]
    public static class McpToolsTesting
    {
        [McpServerTool(Name = "run_tests"), Description("TESTS (the Prove intent): run the model's whole test suite and get graded health. ZERO-SETUP AMBIENT CHECKS run on every model: per-relationship integrity (live: six data probes — orphaned foreign keys, duplicate/blank keys, row counts; an invalid relationship reparents orphans onto the hidden blank row so TOTALS STILL TIE while every per-member breakdown is silently wrong — any integrity Fail caps the grade at D) + per-role static security (a tautology filter like 1=1 restricts nothing). Saved definitions (list_tests) run too — measureReconcile defs execute the full SQL-vs-DAX reconciliation. Verdicts: Pass | Fail | Suspect (downstream of a sibling failure, its root cause named — not a second failure) | NotVerifiable (couldn't check: offline, refused, insufficient coverage — NEVER counted as a pass and NEVER graded). READ Health.Grade WITH Health.CoveragePct — the grade scores only what was decided; coverage says how much that was; RootFailures is the number that matters. Offline runs the static checks and reports the rest NotVerifiable — connect_xmla/connect_local for the full run. Agent-origin data probes are governed by the standard QueryData permission gate. FREE including all evidence; persist=true also appends the run to history (Pro). Read-only.")]
        public static Task<TestSuiteRunResult> RunTests(IEngine engine,
            [Description("Also append this run's health to .semanticus/tests/runs.jsonl — the drift-trend history (Pro; free runs are evaluated in full but not stored).")] bool persist = false)
            => engine.RunTestSuiteAsync(persist, "agent");

        [McpServerTool(Name = "list_tests"), Description("TESTS: the saved test definitions (.semanticus/tests/suite.jsonl beside the model): id, kind ('measureReconcile'), title, target binding and enabled state. Definitions bind by LineageTag when present, or by a stable tests-sidecar identity for a valid tagless object. A vanished or ambiguous target reports MISSING on the next run, never a silent pass. Ambient checks are not listed here because they derive from the model on every run_tests. Unreadable lines are counted, never thrown. FREE, read-only.")]
        public static Task<TestSuiteInfo> ListTests(IEngine engine) => engine.ListTestDefinitionsAsync();

        [McpServerTool(Name = "review_reconcile_mapping"), Description("TESTS: inspect how a saved SQL-vs-DAX reconciliation maps its measure to Fabric SQL. Returns the detected endpoint/database plus every candidate model table with schema/entity, and echoes the effective endpoint after optional overrides. Detection follows the measure's dependency tables; multiple reachable SQL sources are reported AMBIGUOUS and never guessed. testConnection=true performs only SELECT 1 against the effective endpoint/database (it does NOT run or change the human-accepted SQL); agent calls use the standard QueryData approval gate. Suggested next action is returned on refusal/failure. FREE, read-only.")]
        public static async Task<ReconcileMappingReview> ReviewReconcileMapping(IEngine engine,
            [Description("The measure plus optional connection override: {measureRef, server?, database?, authMode?, tenantId?, testConnection?}.")] ReconcileMappingRequest request)
        {
            var r = await engine.ReviewReconcileMappingAsync(request, "agent");
            McpTools.Emit(engine, new ActivityEvent
            {
                Kind = "review_reconcile_mapping",
                Origin = "agent",
                Label = request?.TestConnection == true ? "Tested reconciliation SQL connection" : "Reviewed reconciliation SQL mapping",
                Ok = r.Error == null && (!r.Tested || r.Connected),
                Error = r.Error ?? r.TestError,
                ApprovalId = r.ApprovalId,
                Result = r.Error == null ? r.Note : null,
            });
            return r;
        }

        [McpServerTool(Name = "save_test"), Description("TESTS: create or update a saved test definition (upsert by id; omit id to create). kind='measureReconcile': paramsJson carries the ReconcileRequest (sql, groupBy, required blankPolicy, tolerances; see reconcile_measure, which is the same runner one-off). Pass targetRef; the engine binds by LineageTag when present and otherwise creates a stable tests-sidecar identity so valid tagless measures follow renames. The returned bindingWarning is non-null if a name fallback was unavoidable. GROUND TRUTH IS AI-DRAFTED, HUMAN-ACCEPTED: show the user the SQL and get acceptance before saving. A saved test is the user's suite, not the assistant's guess. Pro; running the ambient suite is free.")]
        public static Task<TestDefinition> SaveTest(IEngine engine,
            [Description("The definition: {id?, kind, title, targetTag?, targetIdentity?, targetRef?, paramsJson?, enabled?}. Prefer targetRef and let the engine choose the durable binding.")] TestDefinition def)
            => engine.SaveTestDefinitionAsync(def, "agent");

        [McpServerTool(Name = "delete_test"), Description("TESTS: delete a saved test definition by id (list_tests shows ids). Returns false when the id wasn't present. Ambient checks cannot be deleted — they derive from the model; to clear a failing integrity check, fix the data it measured. FREE.")]
        public static Task<bool> DeleteTest(IEngine engine,
            [Description("The definition id to remove.")] string id)
            => engine.DeleteTestDefinitionAsync(id, "agent");

        [McpServerTool(Name = "list_test_runs"), Description("TESTS: the persisted run history for this model (chronological, fingerprint-filtered) — each run's health only (grade + coverage together, verdict tallies, root-failure count), never per-cell evidence (evidence lives on the live run). The drift-trend substrate: compare health across runs to see a suite degrade before a user does. Pro (run_tests itself is free and shows everything live); free callers get a teaching note, never an error. Read-only.")]
        public static Task<TestHistoryInfo> ListTestRuns(IEngine engine,
            [Description("How many most-recent runs to return (default 20).")] int last = 20)
            => engine.ListTestRunsAsync(last);

        [McpServerTool(Name = "export_test_report"), Description("TESTS: export the latest successful run as portable Markdown plus one sealed evidence artifact in canonical JSON and self-contained HTML. Returns {markdown, json, html, contentHash, note, error}; JSON is the record of truth, HTML is its deterministic human view, and contentHash detects any later change. The report includes model and environment context, grade WITH coverage, hard gates, root causes before detailed evidence, measure reconciliation rows only for failing measures, time-intelligence variant evidence, relationship counts and rates, and static security plus object visibility. It never exports a run from a different model: if no current-model run exists, call run_tests and retry. Pro with a soft gate: free callers receive a teaching note, not an exception. Read-only.")]
        public static async Task<TestReportResult> ExportTestReport(IEngine engine)
        {
            var r = await engine.ExportTestReportAsync();
            var exported = !string.IsNullOrEmpty(r.Markdown);
            McpTools.Emit(engine, new ActivityEvent
            {
                Kind = "export_test_report",
                Origin = "agent",
                Label = exported ? "Exported test report" : r.Error != null ? "Test report export failed" : "Test report export refused",
                Ok = exported,
                Error = exported ? null : r.Error ?? r.Note,
                // The agent already owns both artifacts. The activity feed gets only the outcome, never either blob.
                Result = exported ? "Report artifacts generated" : r.Note,
            });
            return r;
        }
    }
}
