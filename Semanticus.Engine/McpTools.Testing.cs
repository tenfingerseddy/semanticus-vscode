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
        [McpServerTool(Name = "run_tests"), Description("TESTS (the Prove intent): run the model's whole test suite and get graded health. ZERO-SETUP AMBIENT CHECKS run on every model: per-relationship integrity (live: six data probes: orphaned foreign keys, duplicate/blank keys, row counts; an invalid relationship reparents orphans onto the hidden blank row so TOTALS STILL TIE while every per-member breakdown is silently wrong: any integrity Fail caps the grade at D) plus SQL-backed table row counts. There are NO role-filter checks here: reading a role's saved filter text never proved which rows a user can see, so that family left Tests in 1.2.0 along with the Model interview replay (create_role/set_table_permission still edit roles; run_interview still runs the interview). Saved definitions (list_tests) run too: measureReconcile defs execute the full SQL-vs-DAX reconciliation. Verdicts: Pass | Fail | Suspect (downstream of a sibling failure, its root cause named, not a second failure) | NotVerifiable (couldn't check: offline, refused, insufficient coverage. NEVER counted as a pass and NEVER graded). READ Health.Grade WITH Health.CoveragePct: the grade scores only what was decided; coverage says how much that was; RootFailures is the number that matters. Offline runs the checks it can decide without data and reports the rest NotVerifiable: connect_xmla/connect_local for the full run. SCOPE: omit only and sections to run everything. The sections you can run are 'measures' (the saved checks), 'relationships' and 'tableCounts', and each one runs ONLY itself: asking for relationships does not open a SQL connection, and asking for table counts does not probe relationships. An EMPTY only or sections list is REFUSED (it asks for nothing), and so is a section with no evaluator behind it. Agent-origin data probes are governed by the standard QueryData permission gate. Tests is a Semanticus Pro feature, reads included. persist=true also appends the run to history. Read-only.")]
        public static Task<TestSuiteRunResult> RunTests(IEngine engine,
            [Description("Also append this run's health to .semanticus/tests/runs.jsonl: the drift-trend history. A partial scope cannot be recorded.")] bool persist = false,
            [Description("Optional saved-test ids to run. Omit to run every saved test. A partial list never mints a grade and cannot be recorded.")] string[] only = null,
            [Description("Optional section names to run: measures, relationships. Omit for every section. Any other name, and an empty list, is refused with a plain note rather than quietly ignored. A partial list never mints a grade and cannot be recorded.")] string[] sections = null)
            => engine.RunTestSuiteAsync(persist, "agent", only, sections);

        [McpServerTool(Name = "try_test"), Description("TESTS: run one unsaved test definition once and return its outcome. Writes nothing to the suite. kind='measureValue' checks a measure against a trusted number; kind='measureReconcile' is the SQL-vs-DAX reconciliation. Offline the outcome is NotVerifiable. Tests is a Semanticus Pro feature, reads included. probe_measure is the free way to check one number.")]
        public static Task<ReconcileOutcome> TryTest(IEngine engine,
            [Description("The unsaved definition: {kind, title, targetRef?, paramsJson?}.")] TestDefinition def)
            => engine.TryTestAsync(def, "agent");

        [McpServerTool(Name = "list_tests"), Description("TESTS: the saved test definitions (.semanticus/tests/suite.jsonl beside the model): id, kind ('measureReconcile'), title, target binding and enabled state. Definitions bind by LineageTag when present, or by a stable tests-sidecar identity for a valid tagless object. A vanished or ambiguous target reports MISSING on the next run, never a silent pass. Ambient checks are not listed here because they derive from the model on every run_tests. Unreadable lines are counted, never thrown. Tests is a Semanticus Pro feature, reads included. Read-only. list_sql_source_usage is the free way to see which checks use a SQL source.")]
        public static Task<TestSuiteInfo> ListTests(IEngine engine) => engine.ListTestDefinitionsAsync();

        [McpServerTool(Name = "review_reconcile_mapping"), Description("TESTS: inspect how a saved SQL-vs-DAX reconciliation maps its measure to Fabric SQL. Returns the detected endpoint/database plus every candidate model table with schema/entity, and echoes the effective endpoint after optional overrides. Detection follows the measure's dependency tables; multiple reachable SQL sources are reported AMBIGUOUS and never guessed. testConnection=true performs only SELECT 1 against the effective endpoint/database (it does NOT run or change the human-accepted SQL); agent calls use the standard QueryData approval gate. Suggested next action is returned on refusal/failure. Read-only. Tests is a Semanticus Pro feature.")]
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

        [McpServerTool(Name = "save_test"), Description("TESTS: create or update a saved test definition (upsert by id; omit id to create). kind='measureReconcile': paramsJson carries the ReconcileRequest (sql, groupBy, required blankPolicy, tolerances; see reconcile_measure, which is the same runner one-off), and filterDax narrows the DAX side the way the visual that produced it was narrowed (the SQL is never rewritten: narrow it yourself). kind='measureValue': paramsJson carries the MeasureValueRequest, where filterDax narrows the measure and expressionDax tests DAX the model does not hold yet (a scalar expression, or a complete EVALUATE query run verbatim) instead of the bound measure's own formula. Pass targetRef; the engine binds by LineageTag when present and otherwise creates a stable tests-sidecar identity so valid tagless measures follow renames. The returned bindingWarning is non-null if a name fallback was unavoidable. GROUND TRUTH IS AI-DRAFTED, HUMAN-ACCEPTED: show the user the SQL and get acceptance before saving. A saved test is the user's suite, not the assistant's guess. Tests is a Semanticus Pro feature, reads included.")]
        public static Task<TestDefinition> SaveTest(IEngine engine,
            [Description("The definition: {id?, kind, title, targetTag?, targetIdentity?, targetRef?, paramsJson?, enabled?, authoredAgainst?, authoredAgainstLabel?}. Prefer targetRef and let the engine choose the durable binding. authoredAgainst/authoredAgainstLabel RECORD which Tests-and-queries connection the check was agreed against and are stamped from the live connection when you omit them; they never pin where the test runs.")] TestDefinition def)
            => engine.SaveTestDefinitionAsync(def, "agent");

        [McpServerTool(Name = "delete_test"), Description("TESTS: delete a saved test definition by id (list_tests shows ids). Returns false when the id wasn't present. Ambient checks cannot be deleted: they derive from the model; to clear a failing integrity check, fix the data it measured. Tests is a Semanticus Pro feature, reads included.")]
        public static Task<bool> DeleteTest(IEngine engine,
            [Description("The definition id to remove.")] string id)
            => engine.DeleteTestDefinitionAsync(id, "agent");

        [McpServerTool(Name = "list_test_runs"), Description("TESTS: the persisted run history for this model (chronological, fingerprint-filtered): each run's health only (grade + coverage together, verdict tallies, root-failure count), never the per-check detail, which get_test_run returns for one run at a time. The drift-trend substrate: compare health across runs to see a suite degrade before a user does. Tests is a Semanticus Pro feature, reads included. Read-only.")]
        public static Task<TestHistoryInfo> ListTestRuns(IEngine engine,
            [Description("How many most-recent runs to return (default 20).")] int last = 20)
            => engine.ListTestRunsAsync(last);

        [McpServerTool(Name = "get_test_run"), Description("TESTS: open ONE recorded run in full (list_test_runs gives the ids). Returns the run's health plus EVERY check it decided: each saved definition, each relationship check and each table row count, with its verdict, the answer it was agreed against, what it actually got, the difference, its own words, the settings it ran with (the query it asked, the filter it applied, the tolerance it allowed and the SQL source it resolved to, all AS THEY WERE at execution time, never re-read from today's definition), and its filter-context rows capped at the number the report shows with the uncapped count beside them. This is how a run from weeks ago can still be read: list_test_runs is a summary and deliberately carries none of it. A run recorded before 1.2.0 comes back with its totals and a note saying outcomes were not recorded then. Tests is a Semanticus Pro feature, reads included. Read-only.")]
        public static Task<TestRunDetail> GetTestRun(IEngine engine,
            [Description("The recorded run's id, as shown by list_test_runs.")] string runId)
            => engine.GetTestRunAsync(runId);

        [McpServerTool(Name = "record_test_run"), Description("TESTS: record a run that has ALREADY finished, exactly as it finished. Nothing is executed: this writes the snapshot of the run named by runId (its health, every check's outcome with expected/actual/difference, its model, environment, scope and duration) to the run history, and get_test_run opens it later. This is the operation to use for \"save the run I am looking at\": run_tests(persist=true) also records, but it records the run IT just executed, which is a different run against data that may have moved. Only a FULL run can be recorded, so the stored history compares like with like; a partial run is refused with a plain note. Recording the same run twice keeps one snapshot. Runs are remembered for the last few of this session, so record the one you just ran. Tests is a Semanticus Pro feature, reads included. Read-only with respect to the model.")]
        public static Task<TestRunRecordResult> RecordTestRun(IEngine engine,
            [Description("The id of a run this session already finished, as returned by run_tests.")] string runId)
            => engine.RecordTestRunAsync(runId, "agent");

        [McpServerTool(Name = "get_unmatched_rows"), Description("TESTS: the unmatched key values behind a relationship's orphan count. A relationship check reports HOW MANY fact rows point at a missing lookup key; this answers WHICH ones. Returns one row per distinct many-side key value the one-side table does not have, with how many many-side rows carry it, biggest first, capped at 200 (the cap is disclosed when it hides more). The question asked is character-for-character the one the check counts, so the list and the count can never disagree. It READS CURRENT DATA through the live model, so run after a recorded check and the data may have moved since that check was measured; the note says so. Needs a live connection. Agent calls pass the standard QueryData permission gate, because this is the same row-returning query path. Read-only. Tests is a Semanticus Pro feature.")]
        public static Task<UnmatchedRowsResult> GetUnmatchedRows(IEngine engine,
            [Description("The relationship's name exactly as a run reported it, e.g. 'Sales[CustomerKey] -> Customer[CustomerKey]'.")] string relationship,
            [Description("How many key values to return, capped at 200 (default 200).")] int limit = 200)
            => engine.GetUnmatchedRowsAsync(relationship, limit, "agent");

        [McpServerTool(Name = "export_test_report"), Description("TESTS: export the latest successful run as portable Markdown plus one sealed evidence artifact in canonical JSON and self-contained HTML. Returns {markdown, json, html, contentHash, note, error}; JSON is the record of truth, HTML is its deterministic human view, and contentHash detects any later change. The report includes model and environment context, grade WITH coverage, hard gates, root causes before detailed evidence, measure reconciliation rows only for failing measures, time-intelligence variant evidence, and relationship counts and rates. It never exports a run from a different model: if no current-model run exists, call run_tests and retry. Tests is a Semanticus Pro feature, reads included. Read-only.")]
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
