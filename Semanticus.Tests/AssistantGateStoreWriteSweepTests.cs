using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join part 6, area B, the self-check of the "model and arguments only" list (the lane m11/sol6-gate, fix round 2). That list was made by hand and a
    /// sample of it found a third of the rows wrong: rows that delete or overwrite an item in a store beside the model, which the model's revision does not
    /// move with. So the list is no longer trusted, it is measured. For every row on it, the write runs through the real assistant door (Locked, a yes, the
    /// same call again) on a model with EVERY store populated, and the files outside the model's own file are hashed before and after (the engine's home and
    /// the model's folder, with the project's .semanticus), together with the spec and plan kept in the session's memory. A row that changes any of them is a
    /// row that writes a store, and it fails here until it is moved to a witness list (a Basis, a path witness) or to the append-only list with its reason.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateStoreWriteSweepTests : GateFixtureBase
    {
        private readonly ITestOutputHelper _out;
        private readonly string _registryRoot = ConnectionRegistry.RootOverride;

        public AssistantGateStoreWriteSweepTests(RestoreRootFixture fixture, ITestOutputHelper output) : base(fixture) { _out = output; }

        public override void Dispose() { ConnectionRegistry.RootOverride = _registryRoot; base.Dispose(); }

        private const string Locked = AssistantGateKit.Locked;

        /// <summary>Rows that cannot be driven from a unit test, each with the reason, and the way the rest of the suite or the audit covers them. This list
        /// is counted in the test's output and capped; a row only comes off it by gaining a recipe.</summary>
        internal static readonly Dictionary<string, string> NotDrivable = new(StringComparer.Ordinal)
        {
            ["DaxLibUninstallAsync"] = "needs a package installed from the DaxLib registry (a network call); it edits the model's functions and the install record in the model's own annotations (audit: full table)",
            ["OptimizeMeasureAsync"] = "needs a live connection to race the candidates; it writes one model edit and the audit record in the model's own annotations, and with apply it appends one line to the vitals log .semanticus/vitals/vitals.jsonl (audit: full table)",
            ["SubmitWorkflowStepAsync"] = "needs a started workflow run; run state is engine memory and a step's writes are model edits (audit: full table)",
        };

        // ---- the fixture: every store populated ----------------------------------------------------------------------

        private static string Workflow(string name) => "---\nschemaVersion: 3\nname: " + name + "\ntitle: A workflow\n---\n"
            + "## Step 1: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n";

        private static string Template(string name) => "---\nkind: template\nname: " + name + "\ntitle: A template {{surface}}\nversion: 1\nstrictness: warn\nslots:\n"
            + "  - name: surface\n    question: \"What surface?\"\n    type: text\n    required: required\n    example: \"the dashboard\"\n---\n\n## Step 1: Do it\nCertify {{surface}}.\n";

        private sealed class Rig
        {
            public LocalEngine Engine;
            public string ModelPath;
            public string ModelFolder;
            public string StateDir;
            public string WorkflowName = "wf-a", TemplateName = "tpl-a", TestId, QuestionId, NoteId, SqlSourceId, ConnectionId, RelationshipName, DaxScript, TmdlScript;
        }

        private async Task<Rig> PopulatedAsync(string tag)
        {
            // each row gets its own engine home, so a store one row fills is not the next row's
            var stateDir = Path.Combine(Root, "homes", tag);
            Directory.CreateDirectory(stateDir);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, stateDir);
            ConnectionRegistry.RootOverride = stateDir;   // the machine registries (connections, SQL sources) live here
            var (engine, sessions, path) = await OpenAsync(tag);
            var rig = new Rig { Engine = engine, StateDir = stateDir, ModelPath = path, ModelFolder = Path.GetDirectoryName(path) };
            // the person's stores: a note, a workflow, a template, a stored test, an interview question, a spec, a plan line, a SQL source, a connection,
            // a diagram layout, a workflow canvas, a report scope, the Primer
            rig.NoteId = (await engine.AddInsightAsync("The Region column is the sales region", new[] { "column:Sales/Region" }, "fact", "project", false, "human")).Id;
            await engine.SaveWorkflowAsync(rig.WorkflowName, Workflow(rig.WorkflowName), "human");
            await engine.SaveWorkflowTemplateAsync(rig.TemplateName, Template(rig.TemplateName), "human");
            rig.TestId = (await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Id = null, Kind = TestKinds.MeasureReconcile, Title = "A test", TargetRef = "measure:Sales/Total",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = "measure:Sales/Total", Sql = "SELECT 1", BlankPolicy = "zero" }),
            }, "human")).Id;
            rig.QuestionId = (await engine.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", 1)", null, null, null, null, "1", null, false, null, "user", "project", "human", null)).Id;
            await engine.SetSpecAsync("{\"name\":\"S\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
            var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "reviewed words", null, null, null, "human")).Items.Single().Id;
            await engine.SetPlanItemAsync(line, null, true, "human");
            rig.SqlSourceId = (await engine.SaveSqlSourceAsync(null, "Warehouse", "srv-a", "db-a", "interactive", null, "human")).Id;
            rig.ConnectionId = (await engine.RememberXmlaConnectionAsync("powerbi://api.powerbi.com/v1.0/myorg/Space", "Sales", "Sales", "interactive", "human")).Id;
            await engine.SaveLayoutAsync(new[] { new LayoutNode { Ref = "table:Sales", Name = "Sales", LineageTag = "t-sales", X = 10, Y = 20, Width = 200, Height = 100 } }, "human");
            await engine.SaveWorkflowLayoutAsync(rig.WorkflowName, new Dictionary<string, WorkflowPosition> { ["read"] = new WorkflowPosition { X = 5, Y = 6 } });
            await engine.SetReportScopeAsync(new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "R", Path = Path.Combine(rig.ModelFolder, "R.Report") } }, "human");
            await engine.SetPrimerAsync(PrimerContract.Template("Gate").Replace("_Add what people and the AI Assistant should know._", "The person's own words."), "human");
            // model families some rows need to find: functions and calendars (1702), shared expressions, a date table with a calendar, a display
            // folder, words to replace, an import table to hang a refresh policy on, and the scripts of a measure to edit
            await engine.SetCompatibilityLevelAsync(1702, "human");
            await engine.CreateNamedExpressionAsync("src", "\"server\"", "human");
            await engine.CreateNamedExpressionAsync("FxRate", "1", "human");
            var dates = await engine.CreateTableAsync("Dates", "human");
            await engine.CreateColumnAsync(dates, "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync(dates, "Year", "Int64", "Year", "human");
            await engine.CreateColumnAsync(dates, "IsHoliday", "Boolean", "IsHoliday", "human");
            await engine.DefineCalendarAsync(dates, "Cal", new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" }, new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" } }, null, "human");
            await engine.SetDisplayFolderAsync(new[] { "measure:Sales/Total" }, "Old", "human");
            await engine.SetDescriptionAsync("measure:Sales/Total", "the old words", "human");
            var ir = await engine.CreateImportTableAsync("IR", "let\n    Source = #table(type table [order_dt = datetime], {})\nin\n    Source", "human");
            await engine.CreateColumnAsync(ir, "OrderDate", "DateTime", "order_dt", "human");
            rig.RelationshipName = await sessions.Current.ReadAsync(m => m.Relationships.OfType<TabularEditor.TOMWrapper.SingleColumnRelationship>().First().Name);
            rig.DaxScript = (await engine.ScriptObjectsAsync(new[] { "measure:Sales/Margin" }, "dax")).Replace("\n1\n", "\n10\n");
            rig.TmdlScript = (await engine.ScriptObjectsAsync(new[] { "measure:Sales/Margin" }, "tmdl")).Replace("= 1", "= 10");
            // unsaved model work, so every row runs against a model that is not clean
            await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
            return rig;
        }

        /// <summary>Everything a store write could leave a trace in, except the model's own file and the model: the engine's home, the model's folder
        /// (the project's .semanticus with it), and the spec and plan the session keeps in memory.</summary>
        private async Task<Dictionary<string, string>> TraceAsync(Rig rig, string row)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in AssistantGateKit.Files(rig.StateDir))
            {
                var key = "home/" + kv.Key.Replace('\\', '/');
                if (!HomeLogs.Contains(key)) map[key] = kv.Value;
            }
            var modelFile = Path.GetFileName(rig.ModelPath);
            foreach (var kv in AssistantGateKit.Files(rig.ModelFolder))
            {
                // the model's own file, and the ledger of the model's own commits (every commit writes it, and the revision binds it)
                if (string.Equals(kv.Key, modelFile, StringComparison.Ordinal) || Ledger.Contains(kv.Key.Replace('\\', '/'))) continue;
                map["model/" + kv.Key.Replace('\\', '/')] = kv.Value;
            }
            var spec = await rig.Engine.GetSpecAsync();
            map["memory/spec"] = JsonSerializer.Serialize(spec.Spec);
            // what the person wrote on the plan: not the revision stamp, the "before" value or the status, which follow the model
            map["memory/plan"] = JsonSerializer.Serialize((await rig.Engine.GetPlanAsync()).Items.Select(i => new { i.Id, i.ObjectRef, i.Kind, i.Title, i.After }));
            return map;
        }

        // ---- the arguments of each row, by parameter name ---------------------------------------------------------------

        private static Dictionary<string, object> R(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static readonly HashSet<string> Ledger = new(StringComparer.Ordinal) { ".semanticus/history.json", ".semanticus/history.json.tmp" };

        /// <summary>Logs of what happened, not words anyone wrote: the connection timeline gains a line when a model is opened.</summary>
        private static readonly HashSet<string> HomeLogs = new(StringComparer.Ordinal) { "home/connection-history.json", "home/connection-history.json.bak" };

        private static readonly string[] NoText = Array.Empty<string>();

        /// <summary>Valid arguments for each row, against the fixture's model. A row not named here is called with arguments that mean nothing, which
        /// writes nothing, so the test counts such a row as NOT exercised and fails on it.</summary>
        private static Dictionary<string, Func<Rig, Dictionary<string, object>>> Recipes() => new(StringComparer.Ordinal)
        {
            ["AddInsightAsync"] = r => R("text", "Another note about the model", "keys", new[] { "table:Sales" }, "kind", "fact", "scope", "project", "fingerprintScoped", false),
            ["AddInterviewQuestionAsync"] = r => R("question", "How many rows?", "tier", "value", "query", "EVALUATE ROW(\"v\", 2)", "expectedValue", "2", "expectRefusal", false, "seedSource", "user", "scope", "project"),
            ["ApplyDaxScriptAsync"] = r => R("script", r.DaxScript),
            ["ApplyFixAsync"] = r => R("ruleId", "FMT-SUMMARIZE", "objRef", "column:Sales/ProductId"),
            ["ApplySafeFixesAsync"] = r => R(),
            ["ApplySchemaUpdateAsync"] = r => R("tableRef", "table:Sales", "items", new[] { new SchemaUpdateItem { Change = "add", Column = "Extra", DataType = "String", SourceColumn = "Extra" } }),
            ["ApplyTmdlScriptAsync"] = r => R("script", r.TmdlScript),
            ["BpaFixAllAsync"] = r => R(),
            ["BpaFixAsync"] = r => R("ruleId", "AVOID_FLOATING_POINT_DATA_TYPES", "objRef", "column:Sales/Amount"),
            ["ClearTableSourceMappingAsync"] = r => R("table", "Sales"),
            ["CreateCalculatedColumnAsync"] = r => R("tableRef", "table:Sales", "name", "Calc", "expression", "1"),
            ["CreateCalculatedTableAsync"] = r => R("name", "CalcT", "expression", "ROW(\"a\", 1)"),
            ["CreateCalculationGroupAsync"] = r => R("name", "Second"),
            ["CreateCalculationItemAsync"] = r => R("calcGroupRef", "table:Scenario", "name", "Extra", "expression", "SELECTEDMEASURE()"),
            ["CreateColumnAsync"] = r => R("tableRef", "table:Sales", "name", "Extra", "dataType", "String", "sourceColumn", "Extra"),
            ["CreateDataSourceAsync"] = r => R("name", "DS", "server", "srv", "database", "db"),
            ["CreateDirectLakeTableAsync"] = r => R("name", "DL", "entityName", "ent", "schemaName", "dbo", "sourceName", "src"),
            ["CreateFieldParameterAsync"] = r => R("name", "FP", "items", new[] { new FieldParameterItem { ObjectRef = "measure:Sales/Total" }, new FieldParameterItem { ObjectRef = "measure:Sales/Margin" } }),
            ["CreateFunctionAsync"] = r => R("name", "Fn", "expression", "(x) => x + 1"),
            ["CreateHierarchyAsync"] = r => R("tableRef", "table:Product", "name", "H", "levelColumns", new[] { "Name", "ProductId" }),
            ["CreateImportTableAsync"] = r => R("name", "Imp", "mExpression", "let Source = 1 in Source"),
            ["CreateLinguisticSchemaAsync"] = r => R("culture", "en-US"),
            ["CreateMeasureAsync"] = r => R("tableRef", "table:Sales", "name", "Extra", "expression", "1"),
            ["CreateNamedExpressionAsync"] = r => R("name", "Another", "expression", "1"),
            ["CreatePerspectiveAsync"] = r => R("name", "Second"),
            ["CreateRelationshipAsync"] = r => R("fromColumnRef", "column:Sales/Code", "toColumnRef", "column:Product/Name", "crossFilter", "OneDirection", "isActive", (bool?)false),
            ["CreateRoleAsync"] = r => R("name", "Auditors", "modelPermission", "read"),
            ["CreateTableAsync"] = r => R("name", "Empty"),
            ["DefineCalendarAsync"] = r => R("tableRef", "Dates", "name", "Cal2", "mappings", new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" } }),
            ["DefineCalendarFromTemplateAsync"] = r => R("template", "gregorian", "tableName", "Dates", "dateColumn", "Date", "fiscalStartMonth", 1, "startExpr", "DATE(2020,1,1)", "endExpr", "DATE(2021,12,31)", "calendarName", "Standard"),
            ["DeleteCalendarAsync"] = r => R("tableRef", "Dates", "name", "Cal"),
            ["DeleteInterviewQuestionAsync"] = r => R("id", r.QuestionId),
            ["DeleteObjectAsync"] = r => R("objRef", "measure:Sales/Margin"),
            ["DeleteObjectsAsync"] = r => R("objRefs", new[] { "measure:Sales/Margin" }),
            ["DeleteRoleAsync"] = r => R("name", "Managers"),
            ["DeleteSqlSourceAsync"] = r => R("id", r.SqlSourceId),
            ["DeleteTestDefinitionAsync"] = r => R("id", r.TestId),
            ["DeleteWorkflowAsync"] = r => R("name", r.WorkflowName),
            ["DeleteWorkflowTemplateAsync"] = r => R("name", r.TemplateName),
            ["DownvoteInsightAsync"] = r => R("id", r.NoteId),
            ["DuplicateObjectAsync"] = r => R("objRef", "measure:Sales/Total", "newName", "Total copy"),
            ["ForgetConnectionAsync"] = r => R("id", r.ConnectionId),
            ["GenerateDateTableAsync"] = r => R("tableName", "Calendar", "startExpr", "DATE(2020,1,1)", "endExpr", "DATE(2021,12,31)", "markAsDate", true),
            ["GenerateTimeIntelligenceAsync"] = r => R("baseMeasureRef", "measure:Sales/Total", "dateColumn", "column:Sales/Region", "variants", new[] { "YTD" }),
            ["MakeAiReadyAsync"] = r => R("maxQueue", 5),
            ["MarkDateTableAsync"] = r => R("tableRef", "Dates", "dateColumn", "Date"),
            ["RedoAsync"] = r => R(),
            ["RememberXmlaConnectionAsync"] = r => R("endpoint", "powerbi://api.powerbi.com/v1.0/myorg/Space", "database", "Sales", "modelName", "Renamed model", "authMode", "interactive"),
            ["RemoveIncrementalRefreshPolicyAsync"] = r => R("tableRef", "table:Sales"),
            ["RemoveSafeObjectsAsync"] = r => R("refs", new[] { "measure:Sales/Margin" }, "reportPaths", NoText),
            ["RenameDisplayFolderAsync"] = r => R("tableRef", "table:Sales", "fromPath", "Old", "toPath", "New"),
            ["RenameObjectAsync"] = r => R("objRef", "measure:Sales/Margin", "newName", "Margin 2"),
            ["ReplaceInObjectAsync"] = r => R("req", new ReplaceRequest { Ref = "measure:Sales/Total", Field = "description", Find = "old", Replace = "new" }),
            ["ResetBpaRulesAsync"] = r => R(),
            ["ResetReadinessRulesAsync"] = r => R(),
            ["SaveLayoutAsync"] = r => R("tables", new[] { new LayoutNode { Ref = "table:Sales", Name = "Sales", LineageTag = "t-sales", X = 400, Y = 300, Width = 200, Height = 100 } }),
            ["SaveSqlSourceAsync"] = r => R("id", r.SqlSourceId, "name", "Warehouse 2", "server", "srv-b", "database", "db-b", "authMode", "interactive"),
            ["SaveWorkflowLayoutAsync"] = r => R("name", r.WorkflowName, "positions", new Dictionary<string, WorkflowPosition> { ["read"] = new WorkflowPosition { X = 50, Y = 60 } }),
            ["SetAiDataSchemaAsync"] = r => R("objRef", "table:Sales", "included", true),
            ["SetAiInstructionsAsync"] = r => R("instructions", "Answer from Sales only."),
            ["SetCalcGroupPrecedenceAsync"] = r => R("calcGroupRef", "table:Scenario", "precedence", 3),
            ["SetCalcItemFormatStringAsync"] = r => R("calcItemRef", "calcitem:Scenario/Plan", "formatExpression", "\"0.0\""),
            ["SetColumnDataTypeAsync"] = r => R("columnRef", "column:Sales/Code", "dataType", "Int64"),
            ["SetColumnMetadataAsync"] = r => R("objRef", "column:Sales/Region", "isHidden", (bool?)true),
            ["SetColumnObjectPermissionAsync"] = r => R("roleName", "Managers", "columnRef", "column:Sales/Amount", "permission", "none"),
            ["SetCompatibilityLevelAsync"] = r => R("level", 1703),
            ["SetConnectionWorkingFolderAsync"] = r => R("id", r.ConnectionId, "folder", Path.Combine(r.ModelFolder, "work")),
            ["SetDaxAsync"] = r => R("objRef", "measure:Sales/Margin", "expression", "2"),
            ["SetDescriptionAsync"] = r => R("objRef", "measure:Sales/Margin", "text", "Words"),
            ["SetDisplayFolderAsync"] = r => R("refs", new[] { "measure:Sales/Margin" }, "folder", "Money"),
            ["SetDocSectionAsync"] = r => R("objRef", "measure:Sales/Total", "sectionKey", "purpose", "markdown", "It sums the sales."),
            ["SetIncrementalRefreshPolicyAsync"] = r => R("tableRef", "table:IR", "dateColumn", "OrderDate", "rollingWindowPeriods", (int?)5, "rollingWindowGranularity", "Year", "incrementalPeriods", (int?)10, "incrementalGranularity", "Day", "incrementalPeriodsOffset", (int?)0, "mode", "Import", "autoWire", true),
            ["SetMeasureFormatAsync"] = r => R("objRef", "measure:Sales/Total", "formatString", "0.00"),
            ["SetMeasureFormatExpressionAsync"] = r => R("objRef", "measure:Sales/Total", "formatExpression", "\"0.00\""),
            ["SetModelSqlEndpointAsync"] = r => R("sqlSourceId", r.SqlSourceId),
            ["SetObjectPropertiesAsync"] = r => R("objRefs", new[] { "measure:Sales/Total", "measure:Sales/Margin" }, "propertyName", "Description", "value", "Same words"),
            ["SetObjectPropertyAsync"] = r => R("objRef", "measure:Sales/Total", "propertyName", "Description", "value", "Words"),
            ["SetPartitionMAsync"] = r => R("partitionRef", "partition:Sales/P1", "mExpression", "let Source = 3 in Source"),
            ["SetPerspectiveMemberAsync"] = r => R("perspectiveRef", "perspective:View", "objRef", "table:Sales", "include", true),
            ["SetPublishDestinationAsync"] = r => R("connectionId", r.ConnectionId),
            ["SetRelationshipAsync"] = r => R("relationshipName", r.RelationshipName, "crossFilteringBehavior", "BothDirections"),
            ["SetRelationshipCardinalityAsync"] = r => R("relationshipName", r.RelationshipName, "fromCardinality", "Many", "toCardinality", "One"),
            ["SetReportScopeAsync"] = r => R("choices", new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "R2", Path = Path.Combine(r.ModelFolder, "R2.Report") } }),
            ["SetRoleMemberAsync"] = r => R("roleName", "Managers", "memberName", "someone@example.com", "add", true),
            ["SetRolePermissionAsync"] = r => R("name", "Managers", "modelPermission", "readRefresh"),
            ["SetSynonymsAsync"] = r => R("objRef", "measure:Sales/Total", "terms", new[] { "revenue" }, "culture", "en-US"),
            ["SetTableObjectPermissionAsync"] = r => R("roleName", "Managers", "tableRef", "table:Product", "permission", "none"),
            ["SetTablePermissionAsync"] = r => R("roleName", "Managers", "tableRef", "table:Sales", "filterDax", "[Region] = \"B\""),
            ["SetTableSourceMappingAsync"] = r => R("table", "Sales", "sqlSourceId", r.SqlSourceId, "schema", "dbo", "entity", "Sales"),
            ["TagCalendarColumnAsync"] = r => R("tableRef", "Dates", "calendarName", "Cal", "column", "IsHoliday", "timeUnit", "Year", "associated", true, "remove", false),
            ["UndoAsync"] = r => R(),
            ["UnwaiveFindingAsync"] = r => R("system", "bpa", "ruleId", "AVOID_FLOATING_POINT_DATA_TYPES", "objRef", "column:Sales/Amount"),
            ["UpdateNamedExpressionAsync"] = r => R("name", "FxRate", "expression", "2"),
            ["UpvoteInsightAsync"] = r => R("id", r.NoteId),
            ["WaiveFindingAsync"] = r => R("system", "bpa", "ruleId", "AVOID_FLOATING_POINT_DATA_TYPES", "objRef", "column:Sales/Amount", "reason", "known"),
            ["WaiveFindingsAsync"] = r => R("items", new[] { new FindingWaiverItem { System = "bpa", RuleId = "AVOID_FLOATING_POINT_DATA_TYPES", ObjRef = "column:Sales/Amount", Reason = "known" } }),
            ["WriteWordsAsync"] = r => R("items", new[] { new WordsWriteItem { ObjRef = "measure:Sales/Total", Description = "Sales total." } }),
            ["EditObjectsAsync"] = r => R("items", new[] { new ObjectEditItem { ObjRef = "measure:Sales/Total", Set = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("{\"description\":\"Sales total.\"}") } }),
            ["CreateMeasuresAsync"] = r => R("items", new[] { new MeasureCreateItem { TableRef = "table:Sales", Name = "Bulk total", Expression = "1" } }),
            ["UnwaiveFindingsAsync"] = r => R("items", new[] { new FindingFixItem { System = "bpa", RuleId = "AVOID_FLOATING_POINT_DATA_TYPES", ObjRef = "column:Sales/Amount" } }),
            ["ApplyFixesAsync"] = r => R("items", new[] { new FindingFixItem { System = "bpa", RuleId = "FMT-SUMMARIZE", ObjRef = "column:Sales/ProductId" } }),
            ["DuplicateObjectsAsync"] = r => R("items", new[] { new ObjectDuplicateItem { ObjRef = "measure:Sales/Total", NewName = "Total copy" } }),
            ["SetDocSectionsAsync"] = r => R("items", new[] { new DocSectionItem { ObjRef = "measure:Sales/Total", SectionKey = "purpose", Markdown = "It sums the sales." } }),
            ["SetPerspectiveMembersAsync"] = r => R("items", new[] { new PerspectiveMemberItem { PerspectiveRef = "perspective:View", ObjRef = "table:Sales", Include = true } }),
            ["SetObjectPermissionsAsync"] = r => R("items", new[] { new ObjectPermissionItem { RoleName = "Managers", ObjRef = "table:Product", Permission = "none" } }),
        };

        private readonly Dictionary<string, (byte[] Before, byte[] After)> Appended = new(StringComparer.Ordinal);

        private static object[] ArgsFor(MethodInfo method, Dictionary<string, object> given)
        {
            var ps = method.GetParameters();
            var args = new object[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (string.Equals(p.Name, "origin", StringComparison.Ordinal)) args[i] = "agent";
                else if (given.TryGetValue(p.Name, out var v)) args[i] = v;
                else if (p.HasDefaultValue) args[i] = p.DefaultValue;
                else if (Nullable.GetUnderlyingType(p.ParameterType) != null || !p.ParameterType.IsValueType) args[i] = null;
                else args[i] = Activator.CreateInstance(p.ParameterType);
            }
            return args;
        }

        // ---- the sweep -------------------------------------------------------------------------------------------------

        /// <summary>What a row did when the assistant called it with a yes: it wrote (the call returned), or it did not (a refusal, an error).</summary>
        private async Task<(bool wrote, string note)> RunRowAsync(Rig rig, MethodInfo method, object[] args)
        {
            AgentPolicyStore.SetPreset(Locked, "human");
            var gate = Gate(rig.Engine);
            try
            {
                try { await AssistantGateKit.InvokeAsync(gate, method, args); return (true, "passed with no question"); }
                catch (InvalidOperationException ex)
                {
                    var open = (await rig.Engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();
                    if (open.Length == 0) return (false, "refused: " + ex.Message);
                    foreach (var p in open) await rig.Engine.ApproveAgentActionAsync(p.Id, "human");
                }
                await AssistantGateKit.InvokeAsync(gate, method, args);
                return (true, "passed after a yes");
            }
            catch (Exception ex) { return (false, "refused after the yes: " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Per row: the files and memory it changed, and whether it was exercised. Shared by the two tests below.</summary>
        private async Task<List<(string row, bool wrote, string note, string[] changed, Dictionary<string, string> before, Dictionary<string, string> after)>> SweepAsync(IEnumerable<string> rows)
        {
            var recipes = Recipes();
            var result = new List<(string, bool, string, string[], Dictionary<string, string>, Dictionary<string, string>)>();
            foreach (var name in rows)
            {
                if (NotDrivable.ContainsKey(name)) continue;
                var method = typeof(IEngine).GetMethods().First(m => m.Name == name);
                if (!recipes.TryGetValue(name, out var recipe)) { result.Add((name, false, "no recipe", Array.Empty<string>(), null, null)); continue; }
                var rig = await PopulatedAsync("row-" + name.Replace("Async", ""));
                using (rig.Engine)
                {
                    var before = await TraceAsync(rig, name);
                    var logFile = AssistantGateWitnessTests.AppendsToOneStore.TryGetValue(name, out var appends) ? Path.Combine(rig.ModelFolder, appends.File) : null;
                    var bytesBefore = logFile != null && File.Exists(logFile) ? File.ReadAllBytes(logFile) : null;
                    var (wrote, note) = await RunRowAsync(rig, method, ArgsFor(method, recipe(rig)));
                    var after = await TraceAsync(rig, name);
                    var bytesAfter = logFile != null && File.Exists(logFile) ? File.ReadAllBytes(logFile) : null;
                    Appended[name] = (bytesBefore, bytesAfter);
                    result.Add((name, wrote, note, AssistantGateKit.Differences(before, after), before, after));
                }
            }
            return result;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_row_filed_as_model_and_arguments_only_changes_no_file_beside_the_model_and_no_memory_beside_it()
        {
            var rows = AssistantGateWitnessTests.ModelAndArgumentsRows;
            var sweep = await SweepAsync(rows);
            var notExercised = sweep.Where(s => !s.wrote).Select(s => s.row + " (" + s.note + ")").ToArray();
            var wroteAStore = sweep.Where(s => s.changed.Length > 0).Select(s => s.row + " changed " + string.Join(", ", s.changed)).ToArray();
            var report = Environment.GetEnvironmentVariable("GATE_SWEEP_REPORT");
            if (!string.IsNullOrEmpty(report))
                File.WriteAllLines(report, sweep.Select(s => s.row + " | " + (s.wrote ? "exercised" : "NOT EXERCISED") + " | " + s.note + " | changed: " + (s.changed.Length == 0 ? "-" : string.Join(", ", s.changed))));
            _out.WriteLine("rows on the list: " + rows.Length + "; not drivable: " + rows.Count(NotDrivable.ContainsKey) + "; exercised: " + sweep.Count(s => s.wrote) + "; wrote a store: " + wroteAStore.Length);
            foreach (var s in sweep.Where(s => !s.wrote)) _out.WriteLine("NOT EXERCISED " + s.row + ": " + s.note);
            foreach (var w in wroteAStore) _out.WriteLine("WROTE A STORE " + w);
            Assert.True(wroteAStore.Length == 0, "These rows are filed as 'model and arguments only' but write a store beside the model. Give each a Basis or a path witness, or put it on the append-only list with its reason:\n" + string.Join("\n", wroteAStore));
            Assert.True(notExercised.Length == 0, "These rows were not exercised with a yes, so the list is not proven for them. Give the row a recipe that works, or name it in NotDrivable with the reason:\n" + string.Join("\n", notExercised));
            var undriven = rows.Where(NotDrivable.ContainsKey).ToArray();
            Assert.True(undriven.Length <= 6, "too many rows that the self-check cannot drive: " + string.Join(", ", undriven));
        }

        /// <summary>Lean suite: the default tier tries the first row of the append-only list; every row is the sweep.</summary>
        [Fact]
        public Task Every_row_on_the_append_only_list_changes_only_its_one_file_and_keeps_every_byte_already_in_it_sample() =>
            AppendOnlyAsync(AssistantGateWitnessTests.AppendsToOneStore.Keys.Take(1).ToArray());

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task Every_row_on_the_append_only_list_changes_only_its_one_file_and_keeps_every_byte_already_in_it() =>
            AppendOnlyAsync(AssistantGateWitnessTests.AppendsToOneStore.Keys.ToArray());

        private async Task AppendOnlyAsync(string[] rows)
        {
            var sweep = await SweepAsync(rows);
            var problems = new List<string>();
            foreach (var s in sweep)
            {
                var (file, _) = AssistantGateWitnessTests.AppendsToOneStore[s.row];
                var key = "model/" + file;
                if (!s.wrote) { problems.Add(s.row + " was not exercised: " + s.note); continue; }
                if (!s.changed.SequenceEqual(new[] { key })) problems.Add(s.row + " changed " + string.Join(", ", s.changed.DefaultIfEmpty("nothing")) + " instead of only " + key);
                var (before, after) = Appended[s.row];
                if (before == null || after == null || after.Length <= before.Length || !after.Take(before.Length).SequenceEqual(before)) problems.Add(s.row + " did not leave the bytes already in " + key + " as they were and add after them");
            }
            _out.WriteLine("append-only rows: " + rows.Length + "; exercised: " + sweep.Count(s => s.wrote));
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        // ---- the rows that replace or delete in a store: a yes goes stale when the person changes the store ------------------------------

        /// <summary>For each row that replaces or deletes something in a store beside the model: what the person does to that store after saying yes.</summary>
        private static Dictionary<string, Func<Rig, Task>> PersonChanges() => new(StringComparer.Ordinal)
        {
            ["DeleteWorkflowAsync"] = r => r.Engine.SaveWorkflowAsync(r.WorkflowName, Workflow(r.WorkflowName).Replace("A workflow", "The person rewrote it"), "human"),
            ["DeleteWorkflowTemplateAsync"] = r => r.Engine.SaveWorkflowTemplateAsync(r.TemplateName, Template(r.TemplateName).Replace("A template", "The person rewrote it"), "human"),
            ["DeleteTestDefinitionAsync"] = async r => { var t = (await r.Engine.ListTestDefinitionsAsync()).Definitions.Single(x => x.Id == r.TestId); t.Title = "The person rewrote it"; await r.Engine.SaveTestDefinitionAsync(t, "human"); },
            ["DeleteInterviewQuestionAsync"] = r => r.Engine.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", 5)", null, null, null, null, "5", null, false, null, "user", "project", "human", r.QuestionId),
            ["AddInterviewQuestionAsync"] = r => r.Engine.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", 5)", null, null, null, null, "5", null, false, null, "user", "project", "human", r.QuestionId),
            ["SaveLayoutAsync"] = r => r.Engine.SaveLayoutAsync(new[] { new LayoutNode { Ref = "table:Sales", Name = "Sales", LineageTag = "t-sales", X = 900, Y = 900, Width = 200, Height = 100 } }, "human"),
            ["SaveWorkflowLayoutAsync"] = r => r.Engine.SaveWorkflowLayoutAsync(r.WorkflowName, new Dictionary<string, WorkflowPosition> { ["read"] = new WorkflowPosition { X = 700, Y = 700 } }),
            ["SetReportScopeAsync"] = r => r.Engine.SetReportScopeAsync(new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Mine", Path = Path.Combine(r.ModelFolder, "Mine.Report") } }, "human"),
            ["SetTableSourceMappingAsync"] = r => r.Engine.SetTableSourceMappingAsync("Sales", r.SqlSourceId, "person", "chose", "human"),
            ["ClearTableSourceMappingAsync"] = r => r.Engine.SetTableSourceMappingAsync("Sales", r.SqlSourceId, "person", "chose", "human"),
            ["SaveSqlSourceAsync"] = r => r.Engine.SaveSqlSourceAsync(r.SqlSourceId, "Warehouse", "srv-person", "db-person", "interactive", null, "human"),
            ["DeleteSqlSourceAsync"] = r => r.Engine.SaveSqlSourceAsync(r.SqlSourceId, "Warehouse", "srv-person", "db-person", "interactive", null, "human"),
            ["RememberXmlaConnectionAsync"] = r => r.Engine.SetConnectionWorkingFolderAsync(r.ConnectionId, Path.Combine(r.ModelFolder, "person-folder")),
            ["SetConnectionWorkingFolderAsync"] = r => r.Engine.SetConnectionWorkingFolderAsync(r.ConnectionId, Path.Combine(r.ModelFolder, "person-folder")),
            ["ForgetConnectionAsync"] = r => r.Engine.SetConnectionWorkingFolderAsync(r.ConnectionId, Path.Combine(r.ModelFolder, "person-folder")),
            ["SetPublishDestinationAsync"] = r => r.Engine.SetConnectionWorkingFolderAsync(r.ConnectionId, Path.Combine(r.ModelFolder, "person-folder")),
            ["SetModelSqlEndpointAsync"] = async r => { await r.Engine.SetModelSqlEndpointAsync(null, null, "human"); },
        };

        /// <summary>Arguments for the row, with the id an interview edit needs.</summary>
        private static Dictionary<string, object> StoreRowArgs(string name, Rig rig)
        {
            var given = Recipes()[name](rig);
            if (name == "AddInterviewQuestionAsync") given["id"] = rig.QuestionId;
            return given;
        }

        /// <summary>Lean suite: the default tier tries one store-replace row (a workflow deleted after the person rewrote it); all 17 are the sweep.</summary>
        [Fact]
        public Task A_yes_for_a_row_that_replaces_or_deletes_in_a_store_is_not_spent_after_the_person_changes_that_store_sample() =>
            YesNotSpentAsync(name => name == "DeleteWorkflowAsync");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task A_yes_for_a_row_that_replaces_or_deletes_in_a_store_is_not_spent_after_the_person_changes_that_store() => YesNotSpentAsync(_ => true);

        private async Task YesNotSpentAsync(Func<string, bool> pick)
        {
            var problems = new List<string>();
            foreach (var (name, change) in PersonChanges().Where(kv => pick(kv.Key)))
            {
                var method = typeof(IEngine).GetMethods().First(m => m.Name == name);
                var rig = await PopulatedAsync("stale-" + name.Replace("Async", ""));
                using (rig.Engine)
                {
                    await ClearQuestionsAsync(rig.Engine);   // the questions of the rows before are not this row's
                    var args = ArgsFor(method, StoreRowArgs(name, rig));
                    AgentPolicyStore.SetPreset(Locked, "human");
                    var gate = Gate(rig.Engine);
                    try { await AssistantGateKit.InvokeAsync(gate, method, args); problems.Add(name + " passed with no question under Locked"); continue; }
                    catch (InvalidOperationException) { }
                    var asked = (await rig.Engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();
                    if (asked.Length != 1) { problems.Add(name + " asked " + asked.Length + " questions, not one"); continue; }
                    await rig.Engine.ApproveAgentActionAsync(asked[0].Id, "human");
                    await change(rig);
                    var afterPerson = await TraceAsync(rig, name);
                    try { await AssistantGateKit.InvokeAsync(gate, method, args); problems.Add(name + " was spent on a store the person had changed"); }
                    catch (InvalidOperationException) { }
                    var afterRetry = await TraceAsync(rig, name);
                    var moved = AssistantGateKit.Differences(afterPerson, afterRetry);
                    if (moved.Length > 0) problems.Add(name + " changed " + string.Join(", ", moved) + " after the person's change");
                    if ((await rig.Engine.ListPendingApprovalsAsync()).Count(p => p.GrantedUtc == null) != 1) problems.Add(name + " did not ask again");
                }
            }
            _out.WriteLine("rows tried: " + PersonChanges().Count(kv => pick(kv.Key)));
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_that_replaces_or_deletes_in_a_store_checks_the_store_under_its_own_lock()
        {
            // The door's question is asked before the write; the check under the store's lock is what holds when the store moves in between. Call the engine
            // with a ticket bound to a state the store is not in (the person changed it after the door looked), as the door's own call would be.
            var problems = new List<string>();
            foreach (var (name, change) in PersonChanges())
            {
                var method = typeof(IEngine).GetMethods().First(m => m.Name == name);
                var rig = await PopulatedAsync("lock-" + name.Replace("Async", ""));
                using (rig.Engine)
                {
                    var args = ArgsFor(method, StoreRowArgs(name, rig));
                    var before = await TraceAsync(rig, name);
                    var stale = new AssistantTicket
                    {
                        Method = name, GrantKey = "test", ContentBasis = "a state the store is not in", ExpectedRevision = -1,
                        Capabilities = new[] { AgentCapability.DeployFile, AgentCapability.EditLocal },
                    };
                    try
                    {
                        using (GateContext.Enter(name, stale)) await AssistantGateKit.InvokeAsync(rig.Engine, method, args);
                        problems.Add(name + " went ahead under a ticket for a state the store was not in");
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("changed after you approved")) { }
                    catch (Exception ex) { problems.Add(name + " refused for another reason: " + ex.GetType().Name + ": " + ex.Message); }
                    var moved = AssistantGateKit.Differences(before, await TraceAsync(rig, name));
                    if (moved.Length > 0) problems.Add(name + " changed " + string.Join(", ", moved) + " although it refused");
                }
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public async Task Undo_and_redo_of_a_step_that_owns_the_Primer_refuse_when_the_person_changed_the_file()
        {
            var (engine, _, path) = await OpenAsync("undo-primer");
            using (engine)
            {
                var primer = PrimerContract.Template("Gate");
                await engine.SetPrimerAsync(primer, "human");   // the newest step owns the Primer file
                var file = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path), ".semanticus", "primers"), "*.md").Single();
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);

                await RefusedAsync(() => gate.UndoAsync("agent"));
                await ApproveAsync(engine);
                File.WriteAllText(file, primer + "\nThe person kept writing here.\n");   // not through the engine: the file moved
                var words = File.ReadAllText(file);
                await RefusedAsync(() => gate.UndoAsync("agent"));
                Assert.Equal(words, File.ReadAllText(file));
                Assert.Contains("The person kept writing here.", File.ReadAllText(file));

                // and the other way: undone by the person, then the file is moved, then the assistant redoes
                await ClearQuestionsAsync(engine);
                File.WriteAllText(file, primer);
                await engine.UndoAsync("human");
                await RefusedAsync(() => gate.RedoAsync("agent"));
                await ApproveAsync(engine);
                File.WriteAllText(file, "# The person wrote a different Primer by hand\n");
                var handWritten = File.ReadAllText(file);
                await RefusedAsync(() => gate.RedoAsync("agent"));
                Assert.Equal(handWritten, File.ReadAllText(file));
            }
        }

        [Fact]
        public void Rows_that_the_self_check_cannot_drive_are_real_held_rows_on_the_list()
        {
            var listed = AssistantGateWitnessTests.ModelAndArgumentsRows;
            foreach (var kv in NotDrivable) Assert.Contains(kv.Key, listed);
        }
    }
}
