using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// The registry: one row for every method of <see cref="IEngine"/>, saying what the gate does with an assistant's call to it
    /// (<see cref="GateEntry"/>). There is NO default: a method with no row is refused, a row for a method that is gone fails
    /// the completeness test, and a new method cannot be reached by an assistant until someone decides what it is.
    ///
    /// How the rows are chosen (the principle every row follows): a call PASSES when it changes neither the person's model, nor
    /// its files, nor the notes beside it, nor the engine's settings, and it is HELD otherwise. Held calls ask under the
    /// person's policy exactly as the matrix says today (Allow below Locked, Ask under Locked). Live writes and row-reading
    /// calls keep the gates their own ops already have. Controls over what the assistant may do are the person's.
    /// </summary>
    internal static class GateRegistry
    {
        private static readonly Dictionary<string, GateEntry> Rows = Build();

        public static GateEntry Find(string method) => method != null && Rows.TryGetValue(method, out var e) ? e : null;
        public static IReadOnlyCollection<GateEntry> All => Rows.Values;

        // ---- the person-only sentence the assistant is sent away with ------------------------------------------------

        public const string PersonOnlySentence =
            "That is the person's to do. It decides what the assistant may do or work on, so the assistant cannot change it. Ask the person to do it in Semanticus.";
        public const string CannotPreviewSentence =
            "This cannot be previewed: it saves outside the model. Run it for real to be asked.";
        public const string PlanPersonOnlySentence =
            "Aims and tasks are the person's to add, remove and mark done. Ask them to do it in Semanticus.";

        // ---- row makers ------------------------------------------------------------------------------------------

        private static Dictionary<string, GateEntry> Build()
        {
            var d = new Dictionary<string, GateEntry>(StringComparer.Ordinal);
            void Add(GateEntry e)
            {
                if (!d.TryAdd(e.Method, e)) throw new InvalidOperationException("The gate registry names " + e.Method + " twice.");
            }
            void Pass(string why, params string[] names) { foreach (var n in names) Add(new GateEntry { Method = n, Kind = GateKind.Pass, Why = why }); }
            // A Pass that changes nothing in the model, its files or the engine's settings, yet changes something a rehearsal's rollback
            // does not cover (the session, a cursor, an in-memory proposal, a queue): a dry run may not run it.
            void NoPreview(string why, params string[] names) { foreach (var n in names) Add(new GateEntry { Method = n, Kind = GateKind.Pass, Why = why, Preview = false }); }
            void Live(string why, AgentCapability cap, params string[] names) { foreach (var n in names) Add(new GateEntry { Method = n, Kind = GateKind.Live, Capability = cap, Why = why }); }
            void Data(string why, params string[] names) { foreach (var n in names) Add(new GateEntry { Method = n, Kind = GateKind.Data, Capability = AgentCapability.QueryData, Why = why }); }
            void Person(string why, params string[] names) { foreach (var n in names) Add(new GateEntry { Method = n, Kind = GateKind.Person, Capability = AgentCapability.Governance, Why = why }); }
            // A model edit: the "Edit the open model" row, bound to the revision the person saw.
            void Edit(string name, string words, Func<GateCall, Task<string>> describe = null) =>
                Add(Edits(name, words, describe));
            // An edit a rehearsal must not run: its work is not all in the model's own undo (the timeline, a script of many steps that read
            // each other, a rule or a waiver kept beside the model), so the rollback would not bring it back.
            void EditNoPreview(string name, string words, Func<GateCall, Task<string>> describe = null) =>
                Add(Edits(name, words, describe, preview: false));
            // A file written beside or from the model: the "Write model files" row.
            void Sidecar(string name, string words) => Add(Sidecars(name, words));
            void Files(string name, string words, string[] paths, bool modelFiles = false, AgentCapability[] also = null) =>
                Add(PathWrites(name, words, paths, modelFiles, also));
            void Settings(string name, string words) =>
                Add(new GateEntry { Method = name, Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Witness = GateWitness.None, Words = words });
            void Dyn(string name, Func<GateCall, Task<GateEntry>> choose) => Add(new GateEntry { Method = name, Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Choose = choose });

            // ======================================= PASS: reads, probes, previews =====================================
            const string reads = "A read: it changes nothing in the model, its files or the engine's settings.";
            Pass(reads,
                "ListRefreshTypesAsync", "ListTreeAsync", "GetObjectAsync", "GetDaxAsync", "SessionInfoAsync", "ConnectionContextAsync",
                "GetEntitlementAsync", "GetVerifiedModeAsync", "GetModelGraphAsync", "GetOrientationAsync", "HarnessReportAsync",
                "GetLayoutAsync", "SearchModelAsync", "ListMeasuresAsync", "ListColumnsAsync", "GetModelObjectsAsync",
                "GetPlanHistoryAsync", "BpaScanAsync", "BpaGetFixPromptAsync", "ValidateRuleAsync", "GetCustomRulesAsync",
                "AiReadinessScanAsync", "AiReadinessScanLiveAsync", "GetFixPromptAsync", "GetGroundingAsync", "ListWaiversAsync",
                "ValidateDaxAsync", "LintDaxAsync", "ListFunctionsAsync", "ListPartitionsAsync", "GetPartitionMAsync",
                "ListNamedExpressionsAsync", "GetNamedExpressionAsync", "ListCalculationGroupsAsync", "GetPerspectivesAsync",
                "ListFormatTemplatesAsync", "GetObjectPropertiesAsync", "GetDependentsAsync", "GetDependenciesAsync", "GetLineageAsync",
                "ImpactOfAsync", "ImpactAssessmentAsync", "UnusedObjectsAsync", "ListReportScopeAsync", "ScriptObjectsAsync",
                "GetIncrementalRefreshPolicyAsync", "GetAiInstructionsAsync", "ListRolesAsync", "ListCalendarsAsync",
                "ListVerifiedEditsAsync", "ExportVerifiedEditsAsync", "ListTestDefinitionsAsync", "ListTestRunsAsync", "GetTestRunAsync",
                "ExportTestReportAsync", "ListEvidenceAsync", "GetEvidenceAsync", "ListWorkflowsAsync", "GetWorkflowEnforcementAsync",
                "GetWorkflowPolicyAsync", "ListWorkflowProfilesAsync", "GetWorkflowAsync", "GetWorkflowDocumentAsync",
                "PreviewWorkflowEditAsync", "GetWorkflowLayoutAsync", "GetWorkflowRunAsync", "ExportWorkflowEvidenceAsync",
                "ListWorkflowTemplatesAsync", "GetWorkflowTemplateAsync", "GetOpCatalogAsync", "CheckWorkflowAsync",
                "ListInsightsAsync", "RecallExperienceAsync", "GetPrimerAsync", "ListPrimerSuggestionsAsync", "GetModelFingerprintAsync",
                "ListInterviewQuestionsAsync", "ListInterviewSeedsAsync", "GetPlanAsync", "GetDocModelAsync", "GetDocOutlineAsync",
                "GetDocSectionAsync", "GetSpecAsync", "GitStatusAsync", "GitDiffAsync", "GitLogAsync", "ListHistoryCheckpointsAsync",
                "CompareModelsAsync", "ListReferenceTreeAsync", "DeployGateAsync", "ListConnectionsAsync",
                "ListSqlSourcesAsync", "ListSqlSourceUsageAsync", "ListTableSourceMappingsAsync", "ListConnectionHistoryAsync",
                "ProbeConnectionAccountsAsync", "ProbeAuthPrerequisitesAsync", "ListAccountProfilesAsync", "GetAgentPolicyAsync",
                "ListPendingApprovalsAsync", "ListRestorePointsAsync", "ListModelSqlEndpointsAsync", "GetDecisionPointsAsync",
                "GetConsentAsync", "GetDecisionBudgetAsync", "ProposeMeasureDefaultsAsync", "GetModelPlanAsync", "GetWordsQueueAsync",
                "GetNextActionsAsync", "RouteAskAsync", "ListDataQuestionsAsync", "PickChartKindAsync",
                "DescribeWorkflowStepsAsync", "GetDraftAsync");
            // The assistant's own lookups of other systems: read-only calls out, nothing written here.
            Pass("A read of another system (its schema, its list, its status): nothing is written here.",
                "GetSourceSchemaAsync", "DiffSchemaAsync", "DaxLibSearchAsync", "DaxLibVersionsAsync", "DaxLibPackageInfoAsync",
                "DaxLibListInstalledAsync", "ListWorkspacesAsync", "ListWorkspaceModelsAsync", "ListDeploymentPipelinesAsync",
                "GetPipelineStagesAsync", "GetStageItemsAsync", "PreviewDeployAsync", "DeploymentHistoryAsync", "FabricGitConnectionAsync",
                "FabricGitStatusAsync", "ListDataAgentsAsync", "GetDataAgentAsync", "GenerateDataAgentConfigFromModelAsync",
                "ListReportsAsync", "AnalyzeReportsAsync", "AnalyzeCloudReportsAsync", "CheckReportsAsync", "ListLocalInstancesAsync",
                "ConnectionStatusAsync");
            Pass("A measurement or a trace on the model or a live copy of it: it computes and returns, and writes nothing.",
                "VertiPaqScanAsync", "BenchmarkDaxAsync", "ProfileDaxAsync", "VerifyEquivalenceAsync", "BenchmarkColdWarmAsync",
                "CaptureQueryPlanAsync", "ProbeMeasureAsync", "BlameValueAsync", "ExplainValueAsync", "ListValueHistoryAsync",
                "CompareBaselineAsync", "RunDmvAsync", "TryTestAsync");
            NoPreview("Changes nothing in the model or its files, but it clears or stops something the engine keeps, which a rehearsal's rollback does not bring back.",
                "CancelDaxAsync", "ClearCacheAsync", "ClearReferenceBindingAsync", "SetPlanItemAsync");
            NoPreview("A check that runs rehearsals of its own, so it cannot run inside one.", "ReplayCheckWorkflowAsync");
            Pass("A read of the sanctioned assistant queue.", "ListAssistantRequestsAsync");
            NoPreview("The engine's own signal or the sanctioned assistant queue (the Plan contract, section 12): the person made the task, and claiming, answering or declining it is the assistant's part of it. It moves a cursor or a queue, which a rehearsal cannot undo.",
                "PublishActivityAsync", "PullAgentHealthAsync", "PullForeignChangesAsync", "PullPlanOfferAsync",
                "ClaimAssistantRequestAsync", "DeclineAssistantRequestAsync", "SkipModelPlanItemAsync", "UnskipModelPlanItemAsync");
            // The rehearsal runs the op through this same gate and rolls the model's changes back, so the one thing a rehearsal may not
            // do is start another (its scope would be torn down under the first).
            NoPreview("A rehearsal: it runs the op through the gate and rolls the model's changes back. It cannot start another.",
                "DryRunOpAsync");
            NoPreview("An analysis proposal that lives in the session's memory until someone applies it; applying is held (ApplyPlanAsync). A rehearsal would leave it behind. A proposal that would drop lines the person decided on, and clearing the plan, are held (see below).",
                "AddPlanItemAsync");
            NoPreview("Opens or closes a connection for reading. What it unlocks (live writes, rows) is gated at its own op, and the target's label is the person's. It changes the session, which a rehearsal cannot undo.",
                "ConnectXmlaAsync", "ConnectLocalAsync", "DisconnectAsync");
            Pass("A probe of a saved SQL source: its own op asks for the data it reads (QueryData) and it writes nothing.", "TestSqlSourceAsync");
            NoPreview("Labelling a target is the person's: the policy reads the label, so the engine refuses it for an assistant.", "LabelConnectionAsync");
            // A setting of how the engine reaches things (accounts, SQL sources, publish destination, working folder) is written to the
            // engine's home, and engine settings are held: asked about under Locked, the Allow of every other preset unchanged.
            Add(SettingWitnessed("RememberXmlaConnectionAsync", "remember the connection to {database:raw} at {endpoint:raw}", c => c.Engine.GateBasisStoreFileAsync("connections"), "the saved connections it updates"));
            Add(SettingWitnessed("ForgetConnectionAsync", "forget the connection {id:raw}", c => c.Engine.GateBasisStoreFileAsync("connections"), "the saved connection it removes"));
            Add(SettingWitnessed("SetConnectionWorkingFolderAsync", "put the working copy of the connection {id:raw} in {folder:raw}", c => c.Engine.GateBasisStoreFileAsync("connections"), "the saved connection it changes"));
            Add(SettingWitnessed("SaveSqlSourceAsync", "save the SQL source {name:raw} (server {server:raw}, database {database:raw})", c => string.IsNullOrWhiteSpace(c.Arg<string>("id")) ? Task.FromResult("new source") : c.Engine.GateBasisStoreFileAsync("sql-sources"), "the saved SQL source an id replaces"));
            Add(SettingWitnessed("DeleteSqlSourceAsync", "delete the SQL source {id:raw}", c => c.Engine.GateBasisStoreFileAsync("sql-sources"), "the saved SQL sources it removes from"));
            Add(SettingWitnessed("SetTableSourceMappingAsync", "map the table {table:raw} to {schema:raw}.{entity:raw} in the SQL source {sqlSourceId:raw}", c => c.Engine.GateBasisStoreFileAsync("table-sources"), "the table mappings it replaces"));
            Add(SettingWitnessed("ClearTableSourceMappingAsync", "stop mapping the table {table:raw} to a SQL source", c => c.Engine.GateBasisStoreFileAsync("table-sources"), "the table mappings it removes from"));
            Add(SettingWitnessed("SetModelSqlEndpointAsync", "point the model at the SQL source {sqlSourceId:raw}", c => c.Engine.GateBasisStoreFileAsync("model-sql-endpoints"), "the SQL endpoint link it replaces"));
            Add(SettingWitnessed("SetPublishDestinationAsync", "set the publish destination to the connection {connectionId:raw}", c => c.Engine.GateBasisStoreFileAsync("connections"), "the saved connection it links"));
            NoPreview("A step in a guided workflow run, which is state the engine keeps in memory. A step that carries writes is held (SubmitWorkflowStepAsync). A rehearsal would leave the run changed.",
                "StartWorkflowAsync", "SkipWorkflowStepAsync", "AbortWorkflowAsync", "ComposeTaskAsync", "EditDraftAsync");
            NoPreview("Taking permission back only ever narrows what leaves the machine, so it stays free for the assistant. It is a setting, so a rehearsal cannot run it.",
                "WithdrawConsentAsync");

            // ======================================= DATA and LIVE: gated at their own op ===============================
            Data("Reads rows of data: gated at its own op (QueryData, a session grant per target).",
                "RunDaxAsync", "PreviewTableAsync", "PivotMeasureAsync", "ReconcileMeasureAsync", "ReviewReconcileMappingAsync",
                "EvaluateAndLogAsync", "GetModelFactsAsync", "GetUnmatchedRowsAsync", "AskDataAsync");
            Live("A write to a published model: gated at its own op, which reads the target's label.", AgentCapability.DeployLive,
                "DeployLiveAsync", "DeployStageAsync", "CicdPublishAsync", "PublishDataAgentAsync", "FabricGitCommitAsync", "FabricGitUpdateAsync",
                "FabricGitConnectAsync", "FabricGitDisconnectAsync", "CreateDataAgentAsync", "UpdateDataAgentAsync", "DeleteDataAgentAsync",
                "PublishNewModelAsync");
            Live("A rollback of a published model: gated at its own op.", AgentCapability.Rollback, "RollbackPushAsync", "PurgeRestorePointsAsync");
            Live("A refresh of a published model: gated at its own op.", AgentCapability.Refresh, "RefreshPartitionAsync");

            // ======================================= PERSON: controls over the assistant ================================
            Person("The policy is the person's: an assistant that could change it could free itself.",
                "SetAgentPolicyPresetAsync", "SetAgentPolicyCellAsync", "SetAgentPolicyEnabledAsync", "ApproveAgentActionAsync", "DenyAgentActionAsync");
            Person("Aims, tasks, repeats, the queue switch, Pause and Resume, Up, the doer switch and a task's note are the person's (Plan contract, section 12).",
                "AddPlanTargetAsync", "RemovePlanTargetAsync", "MarkPlanTargetDoneAsync", "AddPlanTaskAsync", "HoldPlanItemAsync",
                "SetPlanItemDoerAsync", "MovePlanTaskUpAsync", "SetAssistantQueueAsync", "SetPlanTaskNoteAsync", "AddPlanRepeatAsync",
                "SetPlanRepeatAsync", "DeletePlanRepeatAsync", "ClaimPlanTaskAsync", "CompletePlanTaskAsync", "DeclinePlanTaskAsync",
                "RemovePlanRowAsync", "RestorePlanRowAsync");
            Person("Approving a held draft and pressing Continue are the person's own stops (Plan contract, section 12).",
                "ApproveHeldDraftAsync", "ContinueDraftAsync");
            Person("It repoints every remembered model of a tenant at another account, so only the person does it.", "SetDefaultAccountProfileAsync");

            // ======================================= CONSENT and the RATCHET ============================================
            Add(new GateEntry
            {
                Method = "SetConsentAsync", Kind = GateKind.Consent, Capability = AgentCapability.Governance,
                Why = "Agreeing that the model's details may be sent out is the person's, with the field list in front of them, in every preset.",
            });
            Add(new GateEntry
            {
                Method = "SetWorkflowBindingAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision,
                Words = "require the workflow {requireNames:raw} for {op:raw} (mode {mode:raw})",
                Loosens = c => Task.FromResult(c.Engine.GateLoosensBinding(c.Arg<string>("op"), c.Arg<string[]>("requireNames"), c.Arg<string>("mode"))),
                Why = "A binding is a mandate the person put on the assistant: it may be made stricter, never taken off.",
            });
            Add(new GateEntry
            {
                Method = "SetWorkflowEnforcementAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision,
                Words = "set workflow enforcement to {mode:raw}",
                Loosens = c => Task.FromResult(!string.Equals(c.Arg<string>("mode")?.Trim(), "hard", StringComparison.OrdinalIgnoreCase)),
                Why = "Enforcement is how strictly the person's workflows bind the assistant: it may be made hard, never relaxed.",
            });
            Add(new GateEntry
            {
                Method = "SetWorkflowEnabledAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision,
                Words = "turn the workflow {name:raw} on",
                Loosens = c => Task.FromResult(!c.Arg<bool>("enabled")),
                Why = "Turning a workflow off hides one the person may have bound the assistant to: it may be turned on, never off.",
            });
            Add(new GateEntry
            {
                Method = "SetWorkflowActivationAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision,
                Words = "set the rule for showing the workflow {workflow:raw} to {set:raw} when {when:raw}",
                Loosens = c => Task.FromResult(string.Equals(c.Arg<string>("set")?.Trim(), "off", StringComparison.OrdinalIgnoreCase)),
                Why = "A rule that hides a workflow can hide a mandate: it may show one, never hide one.",
            });
            Add(new GateEntry
            {
                Method = "ActivateWorkflowProfileAsync", Kind = GateKind.Person, Capability = AgentCapability.Governance,
                Why = "A set of team rules replaces the whole policy layer, loosening included, so it is the person's.",
            });
            Add(new GateEntry
            {
                Method = "SetVerifiedModeAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.EditLocal, Witness = GateWitness.None,
                Words = "turn Record verified edits on",
                Loosens = c => Task.FromResult(!c.Arg<bool>("on")),
                Why = "Record verified edits is the person's audit switch: the assistant may turn it on, never off.",
            });
            Add(new GateEntry
            {
                Method = "SetDecisionPointEnabledAsync", Kind = GateKind.Ratchet, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision,
                Words = "turn the Jev question {name:raw} off",
                Loosens = c => Task.FromResult(c.Arg<bool>("enabled")),
                Why = "A Jev question sends model details out. The assistant may switch one off, never on.",
            });

            // ======================================= HOLD: edits to the open model ======================================
            // --- walks and plain undo: the words name the rows they take back
            EditNoPreview("UndoAsync", "undo the newest change to the open model", c => c.Engine.GateDescribeStepAsync(redo: false));
            EditNoPreview("RedoAsync", "redo the change that was taken back in the open model", c => c.Engine.GateDescribeStepAsync(redo: true));
            EditNoPreview("UndoToEntryAsync", "undo the changes down to the history row {entryId:raw}", c => c.Engine.GateDescribeWalkAsync(c, redo: false));
            EditNoPreview("RedoToEntryAsync", "redo the changes up to the history row {entryId:raw}", c => c.Engine.GateDescribeWalkAsync(c, redo: true));
            // --- scripts and bulk work: the words show what is written
            EditNoPreview("ApplyDaxScriptAsync", "apply this DAX script", c => c.Engine.GateDescribeDaxScript(c.Arg<string>("script")));
            EditNoPreview("ApplyTmdlScriptAsync", "apply this TMDL script", c => c.Engine.GateDescribeTmdlScript(c.Arg<string>("script")));
            EditNoPreview("ApplySafeFixesAsync", "apply every fix the AI-readiness check calls safe", c => c.Engine.GateDescribeSafeFixes());
            Edit("BpaFixAllAsync", "apply every Best Practice fix that can be made safely", c => c.Engine.GateDescribeBpaFixAll());
            EditNoPreview("MakeAiReadyAsync", "make the model AI ready: write the safe fixes and queue the descriptions that need words");
            // The plan's lines live in the session's memory, so the model's revision does not move when one is edited: the grant is bound to
            // their content too, and the apply checks it again under the plan's own gate.
            Add(new GateEntry
            {
                Method = "ApplyPlanAsync", Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false,
                Words = "apply the approved plan items {approvedIds:raw}",
                Describe = c => c.Engine.GateDescribePlanItems(c.Arg<string[]>("approvedIds")),
                Basis = c => c.Engine.GateBasisPlanItems(c), OutsideInputs = new[] { "the change plan's lines in the session's memory" },
            });
            EditNoPreview("ApplyPlanItemsAsync", "run the plan rows {items:raw}", c => c.Engine.GateDescribePlanItemDigests(c));
            // A draft sits in the draft store, and edit_draft changes it after the yes: the grant is bound to the draft's content (what a person's Approve and run covers).
            Add(HeldOnMemory("RunDraftAsync", c => c.Engine.GateDescribeDraftAsync(c), c => c.Engine.GateBasisDraftAsync(c), "the draft in the draft store"));
            // The spec sits in the session's memory, so the model's revision does not move when it is replaced: the grant is bound to the spec the person was
            // shown, and the build checks it again under the spec's own gate and builds from exactly what it checked (Sol 6.1, join 6 item 4).
            Add(HeldOnMemory("BuildModelFromSpecAsync", async c => "build the model from the spec (" + await c.Engine.GateSpecSummaryAsync().ConfigureAwait(false)
                    + "): it adds the tables, columns, measures and relationships the spec names",
                c => c.Engine.GateBasisSpecAsync(c), "the spec in the session's memory"));
            Edit("RemoveSafeObjectsAsync", "delete the unused objects {refs}");
            Edit("ApplyFixAsync", "apply the AI-readiness fix {ruleId:raw} to {objRef}");
            Edit("BpaFixAsync", "apply the Best Practice fix {ruleId:raw} to {objRef}");
            Edit("ApplySchemaUpdateAsync", "update the columns of {tableRef} from their source: {items}");
            // A rule set may be a file or a URL, which the model's revision knows nothing about: the grant is bound to the text the source holds, and the load uses that text.
            Add(EditWitnessed("LoadBpaRulesAsync", "load these Best Practice rules into the model: {source}", c => c.Engine.GateBasisRuleSourceAsync(c), "the rules file or URL it reads"));
            Add(EditWitnessed("LoadReadinessRulesAsync", "load these AI-readiness rules into the model: {source}", c => c.Engine.GateBasisRuleSourceAsync(c), "the rules file or URL it reads"));
            EditNoPreview("ResetBpaRulesAsync", "put the Best Practice rules back to the built-in set");
            EditNoPreview("ResetReadinessRulesAsync", "put the AI-readiness rules back to the built-in set");
            Edit("ReplaceInObjectAsync", "replace text in the model", c => LocalEngine.GateDescribeReplace(c.Arg<ReplaceRequest>("req")));
            Add(EditWitnessed("DaxLibInstallAsync", "install the DAX Lib package {id:raw} {version:raw} into the model", c => c.Engine.GateBasisDaxLibAsync(c), "the package as the DAX Lib registry serves it"));
            EditNoPreview("DaxLibUninstallAsync", "remove the DAX Lib package {id:raw} from the model");
            // --- creating
            Edit("CreateTableAsync", "add the table {name:raw}");
            Edit("CreateColumnAsync", "add the column {name:raw} ({dataType:raw}, from {sourceColumn:raw}) to {tableRef}");
            Edit("CreateMeasureAsync", "add the measure [{name:raw}] to {tableRef}: {expression}");
            Edit("CreateCalculatedColumnAsync", "add the calculated column {name:raw} to {tableRef}: {expression}");
            Edit("CreateCalculatedTableAsync", "add the calculated table {name:raw}: {expression}");
            Edit("CreateCalculationGroupAsync", "add the calculation group {name:raw}");
            Edit("CreateCalculationItemAsync", "add the calculation item {name:raw} to {calcGroupRef}: {expression}");
            Edit("CreateDataSourceAsync", "add the data source {name:raw} (server {server:raw}, database {database:raw})");
            Edit("CreateNamedExpressionAsync", "add the shared expression {name:raw}: {expression}");
            Edit("CreateImportTableAsync", "add the table {name:raw}, loaded by this Power Query: {mExpression}");
            Edit("CreateDirectLakeTableAsync", "add the Direct Lake table {name:raw} (entity {entityName:raw})");
            Edit("CreateFieldParameterAsync", "add the field parameter {name:raw} with: {items}");
            Edit("CreateFunctionAsync", "add the function {name:raw}: {expression}");
            Edit("CreateRelationshipAsync", "relate {fromColumnRef} to {toColumnRef} (cross filter {crossFilter:raw}, active {isActive})");
            Edit("CreateHierarchyAsync", "add the hierarchy {name:raw} to {tableRef} with the levels {levelColumns:raw}");
            Edit("CreatePerspectiveAsync", "add the perspective {name:raw}");
            Edit("CreateRoleAsync", "add the role {name:raw} with the permission {modelPermission:raw}");
            Edit("CreateLinguisticSchemaAsync", "create the Q&A language settings for {culture:raw}");
            Edit("DuplicateObjectAsync", "copy {objRef} as {newName:raw}");
            Edit("GenerateDateTableAsync", "add the date table {tableName:raw}, from {startExpr:raw} to {endExpr:raw}");
            Edit("GenerateTimeIntelligenceAsync", "add the time-intelligence measures {variants:raw} for {baseMeasureRef}");
            Edit("DefineCalendarAsync", "define the calendar {name:raw} on {tableRef}");
            Edit("DefineCalendarFromTemplateAsync", "define a {template:raw} calendar on the table {tableName:raw}");
            Edit("TagCalendarColumnAsync", "tag the column {column:raw} of the calendar {calendarName:raw} on {tableRef}");
            // --- changing and deleting
            Edit("SetDescriptionAsync", "set the description of {objRef} to: {text}");
            Edit("SetDaxAsync", "set the formula of {objRef} to: {expression}");
            Edit("SetMeasureFormatAsync", "set the number format of {objRef} to: {formatString}");
            Edit("SetMeasureFormatExpressionAsync", "set the format expression of {objRef} to: {formatExpression}");
            Edit("SetObjectPropertyAsync", "set {propertyName:raw} of {objRef} to: {value}");
            Edit("SetObjectPropertiesAsync", "set {propertyName:raw} of {objRefs} to: {value}");
            Edit("SetColumnMetadataAsync", "change {objRef}: hidden {isHidden}, summarize by {summarizeBy:raw}, data category {dataCategory:raw}, sort by {sortByColumn:raw}");
            Edit("SetColumnDataTypeAsync", "set the data type of {columnRef} to {dataType:raw}");
            Edit("SetDisplayFolderAsync", "put {refs} in the display folder {folder}");
            Edit("RenameDisplayFolderAsync", "rename the display folder {fromPath} to {toPath} in {tableRef}");
            Edit("RenameObjectAsync", "rename {objRef} to: {newName}");
            Edit("DeleteObjectAsync", "delete {objRef}");
            Edit("DeleteObjectsAsync", "delete {objRefs}");
            Edit("DeleteRoleAsync", "delete the role {name:raw}");
            Edit("DeleteCalendarAsync", "delete the calendar {name:raw} from {tableRef}");
            Edit("SetPartitionMAsync", "set the Power Query of {partitionRef} to: {mExpression}");
            Edit("UpdateNamedExpressionAsync", "set the shared expression {name:raw} to: {expression}");
            Edit("SetCompatibilityLevelAsync", "set the compatibility level to {level}");
            Edit("MarkDateTableAsync", "mark {tableRef} as the date table, using {dateColumn:raw}");
            Edit("SetRelationshipAsync", "change the relationship {relationshipName:raw}: cross filter {crossFilteringBehavior:raw}, active {isActive}");
            Edit("SetRelationshipCardinalityAsync", "set the cardinality of the relationship {relationshipName:raw} to {fromCardinality:raw} to {toCardinality:raw}");
            Edit("SetCalcGroupPrecedenceAsync", "set the precedence of {calcGroupRef} to {precedence}");
            Edit("SetCalcItemFormatStringAsync", "set the format of {calcItemRef} to: {formatExpression}");
            Edit("SetPerspectiveMemberAsync", "{include?add|take out} {objRef} {include?to|from} {perspectiveRef}");
            Edit("SetIncrementalRefreshPolicyAsync", "set an incremental refresh policy on {tableRef} (date column {dateColumn:raw}, window {rollingWindowPeriods} {rollingWindowGranularity:raw})");
            Edit("RemoveIncrementalRefreshPolicyAsync", "remove the incremental refresh policy from {tableRef}");
            Edit("SetSynonymsAsync", "set the synonyms of {objRef} ({culture:raw}) to: {terms}");
            Edit("SetAiInstructionsAsync", "set the AI instructions ({culture:raw}) to: {instructions}");
            Edit("SetAiDataSchemaAsync", "set whether {objRef} is part of the AI data schema to {included}");
            Edit("SetDocSectionAsync", "write the documentation section {sectionKey:raw} of {objRef}: {markdown}");
            Edit("SetRolePermissionAsync", "set the permission of the role {name:raw} to {modelPermission:raw}");
            Edit("SetTablePermissionAsync", "set the row filter of the role {roleName:raw} on {tableRef} to: {filterDax}");
            Edit("SetRoleMemberAsync", "{add?add|remove} {memberName:raw} {add?to|from} the role {roleName:raw}");
            Edit("SetTableObjectPermissionAsync", "set the permission of the role {roleName:raw} on {tableRef} to {permission:raw}");
            Edit("SetColumnObjectPermissionAsync", "set the permission of the role {roleName:raw} on {columnRef} to {permission:raw}");
            EditNoPreview("WaiveFindingAsync", "waive the finding {ruleId:raw} on {objRef}: {reason}");
            EditNoPreview("UnwaiveFindingAsync", "stop waiving the finding {ruleId:raw} on {objRef}");

            // ======================================= the spec-first start (M11, join 6 J1) ==============================
            // Every verb of the start is the assistant writing: it makes a model and its files, edits the plan the person will approve, writes words into
            // it, or builds the model from it. Under Locked each is asked about like any other assistant write. Reads pass, approving is the person's.
            NoPreview("A read of the start plan or its open words: it changes nothing in the model or its files (it may start the plan's own background work, which a rehearsal cannot undo).",
                "GetStartPlanAsync", "GetStartWordsAsync");
            Person("Approving the start plan is the person's own act: the build step checks the approval, so an assistant that could approve could build whatever it liked (Plan contract, section 12).",
                "ApproveStartPlanAsync");
            Add(new GateEntry
            {
                Method = "StartModelFromSqlAsync", Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Also = new[] { AgentCapability.DeployFile },
                Witness = GateWitness.Revision | GateWitness.Paths, Preview = false,
                ModelName = c => c.Arg<Semanticus.Engine.StartPlan.StartModelRequest>("request")?.Name,
                PathsOf = c => new[] { c.Arg<Semanticus.Engine.StartPlan.StartModelRequest>("request")?.Folder },
                Describe = c =>
                {
                    var r = c.Arg<Semanticus.Engine.StartPlan.StartModelRequest>("request");
                    return Task.FromResult("start the new model " + (r?.Name ?? "") + " from the SQL source " + (r?.Database ?? "") + " on " + (r?.Server ?? "")
                        + ", and save it in the folder " + (r?.Folder ?? ""));
                },
            });
            Add(StartEdits("EditStartPlanAsync", c =>
            {
                var e = c.Arg<Semanticus.Engine.StartPlan.StartPlanEdit>("edit");
                return "change the start plan: " + (e?.Op ?? "an edit").Replace('_', ' ') + (string.IsNullOrWhiteSpace(e?.Target) ? "" : " " + GateWords.NameOf(e.Target)) + (string.IsNullOrWhiteSpace(e?.Value) ? "" : " to " + e.Value);
            }));
            // Letting Jev see the source and its tables is the person's own question in every preset, like set_consent: the assistant's call puts it, with the
            // source, the tables and the sample setting, and answers that it asked. The question is bound to the plan's digest (Sol 6.1, join 6 item 1).
            Add(new GateEntry
            {
                Method = "GrantStartDisclosureAsync", Kind = GateKind.Consent, Capability = AgentCapability.Governance,
                Why = "Agreeing that a source's tables may be sent out is the person's, with the source and the tables in front of them, in every preset.",
            });
            Add(StartEdits("WriteStartWordsAsync", c =>
            {
                var w = c.Arg<Semanticus.Engine.StartPlan.StartWord[]>("words") ?? Array.Empty<Semanticus.Engine.StartPlan.StartWord>();
                var shown = string.Join("; ", w.Take(5).Select(x => (x?.Kind ?? "words") + " for " + GateWords.NameOf(x?.Ref ?? "?") + ": " + (x?.Text ?? "")));
                return "write " + w.Length + (w.Length == 1 ? " word" : " words") + " into the start plan: " + shown + (w.Length > 5 ? "; and " + (w.Length - 5) + " more" : "");
            }));
            Add(StartEdits("BuildStartPlanAsync", c => "build the new model " + (c.Engine.GateStartPlanModelName() ?? "") + " from the start plan, which keeps " + c.Engine.GateStartPlanKept()
                + (c.Engine.GateStartPlanApproved() ? ", and which you approved" : ", and which you have not approved yet, so the build is refused until you do"), AgentCapability.EditLocal));
            Add(StartEdits("DiscardStartPlanAsync", c => "throw away the start plan for " + (c.Engine.GateStartPlanModelName() ?? "the new model") + ", which keeps " + c.Engine.GateStartPlanKept()
                + ", with its decisions, its words and its approval"));

            // ======================================= HOLD: files =======================================================
            Add(new GateEntry
            {
                Method = "SaveAsync", Kind = GateKind.Hold, Capability = AgentCapability.DeployFile,
                Witness = GateWitness.Revision | GateWitness.Paths | GateWitness.ModelFiles, PathArgs = new[] { "path" },
                Words = "save the open model to {path:raw}",
                Describe = c => Task.FromResult(string.IsNullOrWhiteSpace(c.Arg<string>("path"))
                    ? "save the open model to the file it was opened from"
                    : "save the open model to " + c.Arg<string>("path")),
            });
            Files("ExportVpaxAsync", "export the model as a VertiPaq file at {path:raw}", new[] { "path" });
            Add(new GateEntry
            {
                // the engine saves a spec at the path with a doubled .json collapsed, so the approval names that path
                Method = "SaveSpecAsync", Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision | GateWitness.Paths,
                Words = "save the model spec to {path:raw}",
                Basis = c => c.Engine.GateBasisSpecAsync(c), OutsideInputs = new[] { "the spec in the session's memory" },
                PathsOf = c => new[] { string.IsNullOrWhiteSpace(c.Arg<string>("path")) ? null : LocalEngine.CollapseDuplicateJsonExtension(c.Arg<string>("path").Trim()) },
            });
            Files("GitCloneAsync", "clone {url:raw} into {directory:raw}", new[] { "directory" });
                                                            Files("GitCheckoutAsync", "switch the model's repository to {gitRef:raw}, which can replace the model files", Array.Empty<string>(), modelFiles: true);
            Files("GitPullAsync", "pull the latest commits into the model's repository, which can replace the model files", Array.Empty<string>(), modelFiles: true);
                                    // --- notes and records kept beside the model
            Add(Witnessed("SaveLayoutAsync", "save the diagram positions of {tables}", c => c.Engine.GateBasisStoreFileAsync("layout"), "the diagram layout file it rewrites"));
            Add(Witnessed("SaveWorkflowLayoutAsync", "save the positions of the workflow {name:raw}", c => c.Engine.GateBasisWorkflowLayoutAsync(c), "the workflow canvas file it replaces"));
            Add(Witnessed("SetReportScopeAsync", "choose which reports count when finding unused objects: {choices}", c => c.Engine.GateBasisStoreFileAsync("report-scope"), "the list of reports it replaces"));
            // The rest of this block read or replace content in a store beside the model (a test, a question, a workflow, the Primer, the newest test report): each is bound to what it
            // reads or replaces, and the op checks it again under its own lock (join 6, area B).
            Add(Witnessed("SaveEvidenceAsync", "save the evidence {source:raw} {sourceId:raw}", c => c.Engine.GateBasisEvidenceAsync(c), "the newest test report or workflow evidence it saves"));
            Add(Witnessed("SaveTestDefinitionAsync", "save the test {def}", c => c.Engine.GateBasisTestDefinitionAsync(c), "the stored test it replaces"));
            Add(Witnessed("DeleteTestDefinitionAsync", "delete the test {id:raw}", c => c.Engine.GateBasisTestDefinitionByIdAsync(c), "the stored test it deletes"));
            Sidecar("RecordTestRunAsync", "record the test run {runId:raw}");
            Sidecar("CaptureBaselineAsync", "save a baseline of {objRef} named {label}");
            Add(Witnessed("AddInterviewQuestionAsync", "save the interview question {question}", c => c.Engine.GateBasisInterviewEditAsync(c), "the stored interview question an id replaces"));
            Add(Witnessed("DeleteInterviewQuestionAsync", "delete the interview question {id:raw}", c => c.Engine.GateBasisInterviewDeleteAsync(c), "the stored interview question it deletes"));
            Add(Witnessed("RunInterviewAsync", "run the interview question {questionId:raw} and record how it went", c => c.Engine.GateBasisInterviewAsync(c), "the stored interview question it runs"));
            Sidecar("AddInsightAsync", "save this note about the model: {text}");
            // A note edit, an approval and a delete are for the note as it read when the person was asked: notes do not move the model's revision, so the
            // grant is bound to the note's words and checked again under the knowledge gate (Sol 6.1, join 6 item 6). A vote only moves a count.
            Add(NoteWrite("ApproveInsightAsync", "approve the note {id:raw}", describe: c => c.Engine.GateDescribeNoteAsync(c, "approve", edit: false)));
            Add(NoteWrite("EditInsightAsync", "change the note {id:raw} to: {text}", describe: c => c.Engine.GateDescribeNoteAsync(c, "change", edit: true)));
            Sidecar("UpvoteInsightAsync", "mark the note {id:raw} as useful");
            Sidecar("DownvoteInsightAsync", "mark the note {id:raw} as not useful");
            Add(NoteWrite("DeleteInsightAsync", "delete the note {id:raw}", describe: c => c.Engine.GateDescribeNoteAsync(c, "delete", edit: false)));
            Add(NoteWrite("PurgeKnowledgeAsync", "erase the notes kept for this {scope:raw}", c => c.Engine.GateBasisPurgeAsync(c)));
            Add(Witnessed("SetPrimerAsync", "write the model primer: {markdown}", c => c.Engine.GateBasisPrimerAsync(c), "the Primer it replaces", AgentCapability.EditLocal));   // the Primer is written through the model's own commit, so the grant covers both rows
            Add(Witnessed("AcceptPrimerSuggestionAsync", "accept the primer suggestion {id:raw}", c => c.Engine.GateBasisPrimerSuggestionAsync(c), "the suggestion and the Primer it is added to", AgentCapability.EditLocal));
            Add(Witnessed("RejectPrimerSuggestionAsync", "reject the primer suggestion {id:raw}", c => c.Engine.GateBasisPrimerSuggestionAsync(c), "the suggestion it dismisses"));
            Add(Witnessed("SaveWorkflowAsync", "save the workflow {name:raw}: {markdown}", c => c.Engine.GateBasisWorkflowFileAsync(c), "the workflow file it overwrites"));
            Add(Witnessed("DeleteWorkflowAsync", "delete the workflow {name:raw}", c => c.Engine.GateBasisWorkflowFileAsync(c), "the workflow file it deletes"));
            Add(Witnessed("SaveWorkflowTemplateAsync", "save the workflow template {name:raw}: {markdown}", c => c.Engine.GateBasisTemplateFileAsync(c), "the template file it overwrites"));
            Add(Witnessed("DeleteWorkflowTemplateAsync", "delete the workflow template {name:raw}", c => c.Engine.GateBasisTemplateFileAsync(c), "the template file it deletes"));
            Add(Witnessed("InstantiateWorkflowTemplateAsync", "make the workflow {newName:raw} from the template {templateName:raw}", c => c.Engine.GateBasisInstantiateAsync(c), "the template it fills in"));
            Sidecar("EditWorkflowDocumentAsync", "change the workflow {name:raw}: {exactText}");

            // ======================================= HOLD: decided by the call ==========================================
            // An open that would throw away work is an edit of the open model; one that loses nothing is not. What it would lose is defined once
            // (LocalEngine.OpenLoss): unsaved model edits, a spec held in the session, the plan lines the person decided on. The grant is bound to
            // the revision and to the spec and plan lines, which the revision does not move with, and the swap checks both again (RefuseStaleDiscard).
            foreach (var open in new[] { ("OpenAsync", "open the model at {path:raw}"), ("CreateModelAsync", "start a new empty model {name:raw}"),
                                         ("OpenLocalAsync", "open the model that is running in Power BI Desktop"),
                                         ("OpenLiveAsync", "open the published model {database:raw} at {endpoint:raw}") })
            {
                var (name, words) = open;
                Dyn(name, async c =>
                {
                    var loss = await c.Engine.GateOpenLossAsync(c.Arg<bool>("discardUnsaved")).ConfigureAwait(false);
                    return loss.Any
                        ? new GateEntry
                        {
                            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false,
                            Words = words + ". " + loss.Question(),
                            Basis = x => x.Engine.GateBasisOpenLossAsync(x), OutsideInputs = new[] { "the spec and the plan lines in the session's memory" },
                        }
                        : Passing(name, "An open that loses nothing: the person's own work is not touched. It replaces the session, which a rehearsal cannot undo.", preview: false);
                });
            }
            Dyn("OptimizeMeasureAsync", c => Task.FromResult(c.Arg<bool>("apply")
                ? Edits("OptimizeMeasureAsync", "replace the formula of {measureRef} with the fastest of the candidates {candidates}", null)
                : Passing("OptimizeMeasureAsync", "With apply false it races the candidates and changes nothing.")));
            // A copy reads another model (a file, a published model) that the open model's revision knows nothing about: the grant is bound to where the source
            // really is and to the definition of each object in it, and the copy checks them against the same load it copies from (Sol 6.1, join 6 item 5).
            Dyn("CherryPickAsync", c => Task.FromResult(c.Arg<bool>("commit")
                ? new GateEntry
                {
                    Method = "CherryPickAsync", Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false,
                    Words = "copy {refs} from {source:raw} into the open model",
                    Basis = x => x.Engine.GateBasisCherryPickAsync(x), OutsideInputs = new[] { "the source model's definitions" },
                }
                : Passing("CherryPickAsync", "Without commit it is a preview and changes nothing.")));
            Dyn("ApplyDiffAsync", c =>
            {
                if (!c.Arg<bool>("commit")) return Task.FromResult(Passing("ApplyDiffAsync", "Without commit it is a preview and changes nothing."));
                var right = c.Arg<ModelRef>("right");
                var kind = (right?.Kind ?? "").Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "workspace":
                        return Task.FromResult(new GateEntry { Method = "ApplyDiffAsync", Kind = GateKind.Live, Capability = AgentCapability.DeployLive, Why = "A write to a published model: gated at its own op." });
                    case "file":
                        return Task.FromResult(new GateEntry
                        {
                            Method = "ApplyDiffAsync", Kind = GateKind.Hold, Capability = AgentCapability.DeployFile,
                            Witness = GateWitness.Revision | GateWitness.Paths,
                            PathsOf = x => new[] { x.Arg<ModelRef>("right")?.Path },
                            Describe = x => Task.FromResult("merge the chosen changes " + GateWords.Value("selectedRefs", x.Arg<string[]>("selectedRefs")) + " into the file " + x.Arg<ModelRef>("right")?.Path),
                        });
                    case "session":
                        return Task.FromResult(new GateEntry
                        {
                            Method = "ApplyDiffAsync", Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false,
                            Describe = x => Task.FromResult("merge the chosen changes " + GateWords.Value("selectedRefs", x.Arg<string[]>("selectedRefs")) + " into the open model"),
                        });
                    default:
                        // An unknown kind is refused inside the op; asking about it would only be noise. Nothing is written.
                        return Task.FromResult(Passing("ApplyDiffAsync", "A target the op refuses: nothing is written."));
                }
            });
            // Replacing or clearing what the person may be holding in the session (the spec in the Spec tab, the reviewed lines of the change plan) throws work
            // away that no revision and no undo covers. Under Locked it is held, bound to the content it would discard; with nothing there to lose it passes
            // (Sol 6.1, join 6 item 7). Below Locked the matrix allows it, as before.
            const string nothingToLose = "Nothing is held here, so nothing is thrown away.";
            const string spec = "the spec in the session's memory";
            Dyn("SetSpecAsync", async c => await c.Engine.GateSpecHasContentAsync().ConfigureAwait(false)
                ? HeldOnMemory("SetSpecAsync", async x => "replace the model spec (" + await x.Engine.GateSpecSummaryAsync().ConfigureAwait(false) + ") with a new one ("
                    + x.Engine.GateSpecJsonSummary(x.Arg<string>("specJson")) + "). The spec is not in the model, so Undo does not bring the old one back", x => x.Engine.GateBasisSpecAsync(x), spec)
                : Passing("SetSpecAsync", nothingToLose, preview: false));
            Dyn("ClearSpecAsync", async c => await c.Engine.GateSpecHasContentAsync().ConfigureAwait(false)
                ? HeldOnMemory("ClearSpecAsync", async x => "clear the model spec (" + await x.Engine.GateSpecSummaryAsync().ConfigureAwait(false)
                    + "). The spec is not in the model, so Undo does not bring it back", x => x.Engine.GateBasisSpecAsync(x), spec)
                : Passing("ClearSpecAsync", nothingToLose, preview: false));
            Dyn("LoadSpecAsync", async c => await c.Engine.GateSpecHasContentAsync().ConfigureAwait(false)
                ? HeldOnMemory("LoadSpecAsync", async x => "replace the model spec (" + await x.Engine.GateSpecSummaryAsync().ConfigureAwait(false) + ") with the one in the file "
                    + x.Arg<string>("path") + ". The spec is not in the model, so Undo does not bring the old one back", x => x.Engine.GateBasisLoadSpecAsync(x), spec + " and the file it is read from")
                : Passing("LoadSpecAsync", nothingToLose, preview: false));
            Dyn("AutogenerateSpecFromModelAsync", async c => await c.Engine.GateSpecHasContentAsync().ConfigureAwait(false)
                ? HeldOnMemory("AutogenerateSpecFromModelAsync", async x => "replace the model spec (" + await x.Engine.GateSpecSummaryAsync().ConfigureAwait(false)
                    + ") with one made from the open model. The spec is not in the model, so Undo does not bring the old one back", x => x.Engine.GateBasisSpecAsync(x), spec)
                : Passing("AutogenerateSpecFromModelAsync", nothingToLose, preview: false));
            Dyn("AutogenerateSpecFromFabricAsync", async c => await c.Engine.GateSpecHasContentAsync().ConfigureAwait(false)
                ? HeldOnMemory("AutogenerateSpecFromFabricAsync", async x => "replace the model spec (" + await x.Engine.GateSpecSummaryAsync().ConfigureAwait(false) + ") with one read from the Fabric warehouse "
                    + x.Arg<string>("database") + " at " + x.Arg<string>("server") + ". The spec is not in the model, so Undo does not bring the old one back", x => x.Engine.GateBasisSpecAsync(x), spec)
                : Passing("AutogenerateSpecFromFabricAsync", nothingToLose, preview: false));
            const string plan = "the change plan in the session's memory";
            Dyn("ClearPlanAsync", c => Task.FromResult(c.Engine.GatePlanHasLines()
                ? HeldOnMemory("ClearPlanAsync", x => Task.FromResult("clear the whole change plan (" + x.Engine.GatePlanSummary() + "), including every line you approved. The plan is not in the model, so Undo does not bring it back"),
                    x => x.Engine.GateBasisPlanAll(x), plan)
                : Passing("ClearPlanAsync", nothingToLose, preview: false)));
            Dyn("ProposePlanAsync", c => Task.FromResult(c.Engine.GatePlanHasPersonWork()
                ? HeldOnMemory("ProposePlanAsync", x => Task.FromResult("replace the change plan (" + x.Engine.GatePlanSummary() + ") with a new proposal, which drops the lines you decided on"),
                    x => x.Engine.GateBasisPlanAll(x), plan)
                : Passing("ProposePlanAsync", "No line on the plan was decided on by the person, so a new proposal loses none of their work.", preview: false)));
            Dyn("ProposeReplaceAsync", c => Task.FromResult(c.Engine.GatePlanHasPersonWork()
                ? HeldOnMemory("ProposeReplaceAsync", x => Task.FromResult("replace the change plan (" + x.Engine.GatePlanSummary() + ") with a find-and-replace proposal, which drops the lines you decided on"),
                    x => x.Engine.GateBasisPlanAll(x), plan)
                : Passing("ProposeReplaceAsync", "No line on the plan was decided on by the person, so a new proposal loses none of their work.", preview: false)));
            // Calls with a preview side: the preview changes nothing and passes, the call that writes is held.
            const string previews = "Without the flag that makes it write, it only shows what it would do.";
            Dyn("GitCommitAsync", c => Task.FromResult(c.Arg<bool>("commit")
                ? PathWrites("GitCommitAsync", "save the model and commit it: {message}", Array.Empty<string>(), true, null) : Passing("GitCommitAsync", previews)));
            Dyn("CreateHistoryCheckpointAsync", c => Task.FromResult(c.Arg<bool>("commit")
                ? PathWrites("CreateHistoryCheckpointAsync", "save a version of the model named {label}", Array.Empty<string>(), true, null) : Passing("CreateHistoryCheckpointAsync", previews)));
            Dyn("RestoreHistoryCheckpointAsync", c => Task.FromResult(c.Arg<bool>("restore")
                ? PathWrites("RestoreHistoryCheckpointAsync", "restore the model files to the saved version {hash:raw}", Array.Empty<string>(), true, new[] { AgentCapability.EditLocal })
                : Passing("RestoreHistoryCheckpointAsync", previews)));
            Dyn("GitBranchAsync", c => Task.FromResult(!string.IsNullOrWhiteSpace(c.Arg<string>("name")) && (c.Arg<bool>("create") || c.Arg<bool>("checkout"))
                ? PathWrites("GitBranchAsync", "work with the branch {name:raw} (create {create}, switch {checkout}), which can replace the model files", Array.Empty<string>(), true, null)
                : Passing("GitBranchAsync", "With no name, or neither create nor switch, it only lists the branches.")));
            Dyn("GitPushAsync", c => Task.FromResult(c.Arg<bool>("confirm")
                ? Witnessed("GitPushAsync", "push the model's repository to {remote:raw} {branch:raw}", x => x.Engine.GateBasisGitPushAsync(x), "the commits the branch and its upstream point at")
                : Passing("GitPushAsync", previews)));
            Dyn("PrepareWorkingCopyAsync", c => Task.FromResult(c.Arg<bool>("commit")
                ? PathWrites("PrepareWorkingCopyAsync", "make a working copy of the connection {connectionId:raw} in {parentFolder:raw}", new[] { "parentFolder" }, false, null)
                : Passing("PrepareWorkingCopyAsync", previews)));
            Dyn("CicdGenerateAsync", c => Task.FromResult(c.Arg<bool>("write")
                ? new GateEntry
                {
                    Method = "CicdGenerateAsync", Kind = GateKind.Hold, Capability = AgentCapability.DeployFile,
                    Witness = GateWitness.Revision | GateWitness.ModelFiles, Describe = x => x.Engine.GateDescribeCicdAsync(x),
                }
                : Passing("CicdGenerateAsync", "Without write it only returns the files it would make.")));
            Dyn("RunTestSuiteAsync", c => Task.FromResult(c.Arg<bool>("persist")
                ? Witnessed("RunTestSuiteAsync", "run the tests and keep the record of the run", x => x.Engine.GateBasisSuiteAsync(x), "the tests held in the test store")
                : Passing("RunTestSuiteAsync", "Without persist it runs and returns, and keeps nothing.")));
            Dyn("UpgradeWorkflowAsync", c => Task.FromResult(c.Arg<bool>("dryRun")
                ? Passing("UpgradeWorkflowAsync", "A dry run shows the upgrade and writes nothing.")
                : Sidecars("UpgradeWorkflowAsync", "upgrade the workflow {name:raw} to the current format")));
            Dyn("SubmitWorkflowStepAsync", c => Task.FromResult(!string.IsNullOrWhiteSpace(c.Arg<string>("writesJson"))
                ? Edits("SubmitWorkflowStepAsync", "submit the workflow step {stepId:raw} and apply its writes: {writesJson:raw}", null, preview: false)
                : Passing("SubmitWorkflowStepAsync", "Answers only: the run is state the engine keeps in memory, and a step's writes are held.", preview: false)));
            Dyn("CompleteAssistantRequestAsync", c => Task.FromResult(!string.IsNullOrWhiteSpace(c.Arg<string>("text")) && c.Arg<string>("text").TrimStart().StartsWith("[")
                    && c.Arg<string>("text").Trim() != "[]"
                ? Edits("CompleteAssistantRequestAsync", "finish the request {requestId:raw} and apply its changes: {text:raw}", null, preview: false)
                : Passing("CompleteAssistantRequestAsync", "A reply with no changes: the sanctioned way the assistant answers the person's task.", preview: false)));
            Dyn("ModelPlanActionAsync", c =>
            {
                // 'approve' applies the changes kept on a row and 'start' (the default) runs it: both write the model. Propose and
                // cancel write nothing, and a done on an aim is refused inside, so they pass.
                var action = string.IsNullOrWhiteSpace(c.Arg<string>("action")) ? "start" : c.Arg<string>("action").Trim().ToLowerInvariant();
                return Task.FromResult(action is "approve" or "start"
                    ? Edits("ModelPlanActionAsync", "carry out the plan action {action:raw} on the row {id:raw}", c.Engine.GateDescribeModelPlanActionAsync, preview: false)
                    : Passing("ModelPlanActionAsync", "Propose and cancel write nothing; a done on an aim is refused inside the op (Plan contract, section 12). Its memory is kept beside the model, so a rehearsal does not run it.", preview: false));
            });

            return d;
        }

        // ---- entry builders the Dyn rows share --------------------------------------------------------------------

        private static GateEntry Edits(string name, string words, Func<GateCall, Task<string>> describe, bool preview = true) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Words = words, Describe = describe,
            Preview = preview ? null : false,
        };

        /// <summary>A verb of the spec-first start, bound to the model's revision and never rehearsed (the plan, its scratch and its words live in the engine's own
        /// state, which a rehearsal's rollback does not cover). The plan is kept OUTSIDE the model, so editing it, writing words into it, giving Jev permission for
        /// it and throwing it away are the "Write model files" row and are stopped at the engine's own chokepoint (GuardOutsideWrite); only the build commits to the
        /// model, so it is the "Edit the open model" row.</summary>
        private static GateEntry StartEdits(string name, Func<GateCall, string> words, AgentCapability capability = AgentCapability.DeployFile) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = capability, Witness = GateWitness.Revision, Preview = false,
            Describe = c => Task.FromResult(words(c)), ModelName = c => c.Engine.GateStartPlanModelName(),
        };

        /// <summary>A write whose effect depends on something that lives in the session's memory (the spec, the plan), which the model's revision does not move
        /// with: bound to the revision AND to <paramref name="basis"/>, a hash of that state, checked again under the store's own gate. Never rehearsed.</summary>
        private static GateEntry HeldOnMemory(string name, Func<GateCall, Task<string>> describe, Func<GateCall, Task<string>> basis, string input) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false,
            Describe = describe, Basis = basis, OutsideInputs = new[] { input },
        };

        /// <summary>A write to a note kept beside the model, bound to the note's words as they read when the person was asked.</summary>
        private static GateEntry NoteWrite(string name, string words, Func<GateCall, Task<string>> basis = null, Func<GateCall, Task<string>> describe = null)
        {
            var e = Witnessed(name, words, basis ?? (c => c.Engine.GateBasisInsightAsync(c)), "the note in the knowledge store");
            return new GateEntry
            {
                Method = e.Method, Kind = e.Kind, Capability = e.Capability, Also = e.Also, Witness = e.Witness, Words = e.Words, Basis = e.Basis, OutsideInputs = e.OutsideInputs,
                Describe = describe,
            };
        }

        /// <summary>A write beside the model (the "Write model files" row) that reads or replaces something the revision does not move with: bound to the
        /// revision AND to <paramref name="basis"/>, and checked again by the op under its own lock. Words come from the template.</summary>
        private static GateEntry Witnessed(string name, string words, Func<GateCall, Task<string>> basis, string input, params AgentCapability[] also) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Also = also, Witness = GateWitness.Revision, Words = words,
            Basis = basis, OutsideInputs = new[] { input },
        };

        /// <summary>The same for a write that lands in the open model (the "Edit the open model" row), never rehearsed.</summary>
        private static GateEntry EditWitnessed(string name, string words, Func<GateCall, Task<string>> basis, string input) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.EditLocal, Witness = GateWitness.Revision, Preview = false, Words = words,
            Basis = basis, OutsideInputs = new[] { input },
        };

        /// <summary>The same for a setting kept in a machine file (the "Write model files" row, no revision): bound to <paramref name="basis"/> alone, since the model's
        /// revision has nothing to say about it.</summary>
        private static GateEntry SettingWitnessed(string name, string words, Func<GateCall, Task<string>> basis, string input) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Witness = GateWitness.None, Words = words,
            Basis = basis, OutsideInputs = new[] { input },
        };

        private static GateEntry Sidecars(string name, string words) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Witness = GateWitness.Revision, Words = words,
        };

        private static GateEntry PathWrites(string name, string words, string[] paths, bool modelFiles, AgentCapability[] also) => new GateEntry
        {
            Method = name, Kind = GateKind.Hold, Capability = AgentCapability.DeployFile, Also = also ?? Array.Empty<AgentCapability>(),
            Witness = GateWitness.Revision | (paths.Length > 0 ? GateWitness.Paths : GateWitness.None) | (modelFiles ? GateWitness.ModelFiles : GateWitness.None),
            Words = words, PathArgs = paths,
        };

        private static GateEntry Passing(string name, string why, bool preview = true) => new GateEntry { Method = name, Kind = GateKind.Pass, Why = why, Preview = preview ? null : false };
    }
}
