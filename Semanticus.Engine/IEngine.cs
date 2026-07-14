using System.Threading.Tasks;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// The single operation surface that BOTH doors call. The RPC door (UI) and the MCP door
    /// (Claude) are thin adapters over the same IEngine, so there is exactly one implementation of
    /// each operation — no divergent code paths. <c>origin</c> tags who initiated a mutation
    /// (human | agent) for the shared undo timeline and change notifications.
    /// </summary>
    public interface IEngine
    {
        Task<OpenResult> OpenAsync(string path);
        Task<OpenResult> CreateModelAsync(string name, int compatibilityLevel);
        Task<OpenResult> OpenLocalAsync(string dataSource, string database);
        Task<OpenResult> OpenLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool forceReauth = false);
        // commit runs the accountable checkpoint: a RED deploy gate pauses; overrideReason ships anyway (recorded).
        Task<DeployReport> DeployLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human", string overrideReason = null);
        Task<RefreshTypeInfo[]> ListRefreshTypesAsync();
        Task<RefreshReport> RefreshPartitionAsync(string partitionRef, string refreshType, string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human");
        Task<TreeNode[]> ListTreeAsync(string parentRef);
        Task<ObjectInfo> GetObjectAsync(string objRef);
        Task<string> GetDaxAsync(string objRef);
        Task<SetResult> SetDaxAsync(string objRef, string expression, string origin);
        Task<SaveResult> SaveAsync(string path, string format);
        Task<SessionInfo> SessionInfoAsync();
        Task<ConnectionContext> ConnectionContextAsync();
        // Pro entitlement (offline-verified license) — read-only, both doors. The free tier is fully usable; Pro
        // unlocks the one-click BULK apply (apply_change_plan >1 item, bpa_fix_all, apply_safe_fixes). See Entitlement/.
        Task<Entitlement.EntitlementInfo> GetEntitlementAsync();
        // Verified Mode (Pro toggle): read + set the human-controlled "verify every edit" runtime state.
        Task<VerifiedModeState> GetVerifiedModeAsync();
        Task<VerifiedModeState> SetVerifiedModeAsync(bool on, string origin);
        Task<ModelGraph> GetModelGraphAsync();
        // §6 orientation: the blessed session-start primer (get_model_summary). ONE composite read across BOTH
        // doors (workflow runs + the L0 experience tail have no standalone IEngine surface, so it can't be
        // assembled from separate calls) — a RemoteEngine forwards it as one RPC to the owner. Token-budgeted.
        Task<ModelSummary> GetOrientationAsync();
        // §5 telemetry: read-only harness-ergonomics report over the L0 experience log (docs/harness-engineering.md).
        Task<HarnessReportResult> HarnessReportAsync(int topN);
        // Diagram-layout sidecar (D1): engine-owned positions, keyed by LineageTag, persisted beside the model and
        // excluded from the model diff. Dual-drive so the agent and the UI share one layout. See LayoutStore.
        Task<LayoutData> GetLayoutAsync();
        Task<SaveLayoutResult> SaveLayoutAsync(LayoutNode[] tables, string origin);
        Task<SearchResult> SearchModelAsync(string query, int max);
        Task<SearchResult> SearchModelAsync(SearchOptions opts);                 // detailed search (modes/scope/fields/spans)
        Task<ReplaceResult> ReplaceInObjectAsync(ReplaceRequest req, string origin); // MatchClass-routed safe replace (rename/set/dax)
        Task<MeasureRow[]> ListMeasuresAsync();
        Task<ColumnRow[]> ListColumnsAsync();
        Task<ModelObjects> GetModelObjectsAsync();
        Task<UndoState> UndoAsync(string origin);
        Task<UndoState> RedoAsync(string origin);

        // --- Best Practice Analyzer (general-purpose) ---
        Task<BpaScorecard> BpaScanAsync();
        Task<SetResult> BpaFixAsync(string ruleId, string objRef, string origin);
        Task<BpaFixAllResult> BpaFixAllAsync(string origin);
        Task<FixPrompt> BpaGetFixPromptAsync(string ruleId, string objRef);
        Task<BpaRulesInfo> LoadBpaRulesAsync(string source, bool replace, string origin);
        Task<BpaRulesInfo> ResetBpaRulesAsync(string origin);

        // --- Custom rule authoring (both kinds) ---
        Task<ReadinessRulesInfo> LoadReadinessRulesAsync(string source, bool replace, string origin);
        Task<ReadinessRulesInfo> ResetReadinessRulesAsync(string origin);
        Task<RuleValidationResult> ValidateRuleAsync(string kind, string rules);   // compile + test-run preview, read-only
        Task<CustomRulesInfo> GetCustomRulesAsync();

        // --- AI-readiness ---
        Task<Scorecard> AiReadinessScanAsync();
        Task<Scorecard> AiReadinessScanLiveAsync();
        Task<SafeFixResult> ApplySafeFixesAsync(string origin);
        Task<SetResult> ApplyFixAsync(string ruleId, string objRef, string origin);
        Task<FixPrompt> GetFixPromptAsync(string ruleId, string objRef);
        Task<GroundingBundle> GetGroundingAsync(string objRef);

        // --- Finding waivers (accepted findings) — both BPA and AI-readiness, persisted on the model ---
        Task<SetResult> WaiveFindingAsync(string system, string ruleId, string objRef, string reason, string origin);   // objRef null/'*' = rule-level (Pro)
        Task<SetResult> UnwaiveFindingAsync(string system, string ruleId, string objRef, string origin);
        Task<WaiverRecord[]> ListWaiversAsync();
        Task<DaxValidation> ValidateDaxAsync(string expression);
        // Deterministic DAX best-practice lint (token-path; read-only, ungated) — the DAX-quality ruleset.
        Task<Semanticus.Analysis.DaxLintResult> LintDaxAsync(string expression);
        Task<SetResult> SetDescriptionAsync(string objRef, string text, string origin);
        Task<string> CreateMeasureAsync(string tableRef, string name, string expression, string origin, string displayFolder = null);   // displayFolder: born filed, one undo step
        Task<TreeNode[]> ListFunctionsAsync();
        Task<string> CreateFunctionAsync(string name, string expression, string origin);
        Task<string> CreateTableAsync(string name, string origin);
        Task<string> CreateColumnAsync(string tableRef, string name, string dataType, string sourceColumn, string origin);
        Task<string> CreateDataSourceAsync(string name, string server, string database, string origin);
        Task<string> CreateNamedExpressionAsync(string name, string expression, string origin);
        Task<PartitionInfo[]> ListPartitionsAsync(string tableRef);
        Task<string> GetPartitionMAsync(string partitionRef);
        Task<SetResult> SetPartitionMAsync(string partitionRef, string mExpression, string origin);
        Task<DocExpression[]> ListNamedExpressionsAsync();
        Task<string> GetNamedExpressionAsync(string name);
        Task<SetResult> UpdateNamedExpressionAsync(string name, string expression, string origin);
        Task<string> CreateImportTableAsync(string name, string mExpression, string origin);
        Task<string> CreateDirectLakeTableAsync(string name, string entityName, string schemaName, string sourceName, string origin);
        Task<string> CreateCalculatedTableAsync(string name, string expression, string origin);
        Task<string> CreateFieldParameterAsync(string name, FieldParameterItem[] items, string origin);   // Studio v2 Advanced Modelling
        Task<SetResult> SetColumnDataTypeAsync(string columnRef, string dataType, string origin);
        // --- Update Schema (Tabular-Editor-style): re-read a table's SOURCE columns, diff vs the model, apply a
        //     chosen subset. The source-read needs a reachable SQL/Fabric endpoint (parsed from the partition M) and
        //     fails GRACEFULLY (Reachable=false + a message) on an offline snapshot / non-SQL source. Diff + apply
        //     are source-agnostic: supply sourceColumns to diff a synthesized schema (the manual/offline path).
        Task<SourceSchema> GetSourceSchemaAsync(string tableRef, string authMode, string tenantId);
        Task<SchemaDiff> DiffSchemaAsync(string tableRef, SourceColumn[] sourceColumns, string authMode, string tenantId);
        Task<ApplySchemaResult> ApplySchemaUpdateAsync(string tableRef, SchemaUpdateItem[] items, string origin);
        Task<string> CreateCalculatedColumnAsync(string tableRef, string name, string expression, string origin);
        Task<string> CreateRelationshipAsync(string fromColumnRef, string toColumnRef, string crossFilter, bool? isActive, string origin);
        Task<string> CreateHierarchyAsync(string tableRef, string name, string[] levelColumns, string origin);
        Task<string> CreateCalculationGroupAsync(string name, string origin);
        Task<string> CreateCalculationItemAsync(string calcGroupRef, string name, string expression, string origin);
        // Perspectives (Studio v2 Advanced Modelling). Delete/rename reuse DeleteObjectAsync/RenameObjectAsync.
        Task<CalcGroupInfo[]> ListCalculationGroupsAsync();
        Task<string> CreatePerspectiveAsync(string name, string origin);
        Task<SetResult> SetPerspectiveMemberAsync(string perspectiveRef, string objRef, bool include, string origin);
        Task<PerspectiveInfo[]> GetPerspectivesAsync();
        Task<SetResult> SetCalcItemFormatStringAsync(string calcItemRef, string formatExpression, string origin);
        Task<SetResult> SetCalcGroupPrecedenceAsync(string calcGroupRef, int precedence, string origin);
        // Format-string template library: read the curated catalog (offline, no session) — the picker + the agent
        // share one source of truth. The measure DYNAMIC format-string setter ("Format = Dynamic") fills the gap
        // that set_measure_format is static-only, so the dynamic templates land on a plain measure (not just calc items).
        Task<FormatTemplateInfo[]> ListFormatTemplatesAsync(string category, string search);
        Task<SetResult> SetMeasureFormatExpressionAsync(string objRef, string formatExpression, string origin);
        // DaxLib UDF package manager (Studio v2 Advanced Modelling, 6th area). Browse/versions/info = anonymous read-only
        // (no session); install (Pro, bulk) / uninstall / list operate on the open model.
        Task<DaxLibPackage[]> DaxLibSearchAsync(string text, int skip, int take);
        Task<string[]> DaxLibVersionsAsync(string id);
        Task<DaxLibPackageDetail> DaxLibPackageInfoAsync(string id, string version);
        Task<DaxLibInstallResult> DaxLibInstallAsync(string id, string version, bool replaceExisting, string origin);
        Task<DaxLibInstalledRecord[]> DaxLibListInstalledAsync();
        Task<SetResult> DaxLibUninstallAsync(string id, string origin);
        Task<SetResult> DeleteObjectAsync(string objRef, string origin);
        Task<string> DuplicateObjectAsync(string objRef, string newName, string targetRef, string origin);
        Task<ObjectProperty[]> GetObjectPropertiesAsync(string objRef);
        Task<SetResult> SetObjectPropertyAsync(string objRef, string propertyName, string value, string origin);
        // Set ONE property to ONE value across N objects as a single atomic, undoable change (#140) — the
        // multi-select property-grid write. One undo entry / one broadcast; a failure at any object rolls the
        // WHOLE batch back and names it. The single-object op above stays the un-audited free path.
        Task<SetResult> SetObjectPropertiesAsync(string[] objRefs, string propertyName, string value, string origin);
        // Display folders as ATOMIC gestures (a folder is just its members' DisplayFolder strings — an empty one
        // cannot persist): batch file/clear many objects, and rename a folder prefix table-wide (nested included).
        // Each is ONE undo step; a mid-batch failure rolls the whole batch back.
        Task<SetResult> SetDisplayFolderAsync(string[] refs, string folder, string origin);
        Task<FolderRenameResult> RenameDisplayFolderAsync(string tableRef, string fromPath, string toPath, string origin);
        Task<DependentInfo[]> GetDependentsAsync(string objRef);
        Task<DependentInfo[]> GetDependenciesAsync(string objRef);

        // --- Lineage & Impact ("Measure Killer") — Phase 1: OFFLINE (TOM-only). Read-only, both doors. ---
        Task<LineageResult> GetLineageAsync();
        Task<ImpactResult> ImpactOfAsync(string objRef);
        Task<Lineage.ImpactAssessmentResult> ImpactAssessmentAsync(Lineage.ImpactAssessmentRequest request);
        Task<UnusedResult> UnusedObjectsAsync();
        Task<ReportAnalysisResult> AnalyzeReportsAsync(string[] paths);   // report-aware (local PBIR) safe-to-remove
        // The sweep's ACT half: delete the verified-safe set as ONE undoable transaction, re-verified at apply time
        // (a stale "safe" downgrades to skipped). refs narrows to the caller's candidates (null/empty = the whole
        // current safe set); reportPaths makes verification report-aware. >1 item = Pro; a single item stays free.
        Task<RemoveSafeReport> RemoveSafeObjectsAsync(string[] refs, string[] reportPaths, string origin);
        // Cloud report layer (Phase 3): discover published reports + report-aware safe-to-remove over their cloud PBIR.
        Task<CloudReport[]> ListReportsAsync(string workspaceId, string authMode, string tenantId);   // read-only (Power BI scope)
        Task<ReportAnalysisResult> AnalyzeCloudReportsAsync(string workspaceId, string[] reportIds, bool consent, string authMode, string tenantId);
        Task<string> ScriptObjectsAsync(string[] refs, string format);
        Task<ApplyScriptResult> ApplyDaxScriptAsync(string script, string origin);
        Task<ApplyScriptResult> ApplyTmdlScriptAsync(string script, string origin);
        Task<SetResult> SetCompatibilityLevelAsync(int level, string origin);
        Task<RenameResult> RenameObjectAsync(string objRef, string newName, string origin);
        Task<SetResult> SetColumnMetadataAsync(string objRef, bool? isHidden, string summarizeBy, string dataCategory, string sortByColumn, string origin);
        Task<SetResult> SetMeasureFormatAsync(string objRef, string formatString, string origin);
        Task<SetResult> MarkDateTableAsync(string tableRef, string dateColumn, string origin);
        Task<SetResult> SetRelationshipAsync(string relationshipName, string crossFilteringBehavior, bool? isActive, string origin);
        Task<SetResult> SetRelationshipCardinalityAsync(string relationshipName, string fromCardinality, string toCardinality, string origin);
        Task<RefreshPolicyInfo> GetIncrementalRefreshPolicyAsync(string tableRef);
        Task<SetResult> SetIncrementalRefreshPolicyAsync(string tableRef, string dateColumn, int? rollingWindowPeriods, string rollingWindowGranularity, int? incrementalPeriods, string incrementalGranularity, int? incrementalPeriodsOffset, string mode, string pollingExpression, bool autoWire, string origin);
        Task<SetResult> RemoveIncrementalRefreshPolicyAsync(string tableRef, string origin);
        Task<ReadinessPlan> MakeAiReadyAsync(string origin, int maxQueue);
        Task<SetResult> EnableQnaAsync(string culture, string origin);
        Task<SetResult> SetSynonymsAsync(string objRef, string[] terms, string culture, string origin);
        Task<SetInstructionsResult> SetAiInstructionsAsync(string instructions, string culture, string origin);
        Task<AiInstructionsInfo> GetAiInstructionsAsync(string culture);
        Task<SetResult> SetAiDataSchemaAsync(string objRef, bool included, string culture, string origin);

        // --- row-level security (RLS) roles ---
        Task<RoleInfo[]> ListRolesAsync();
        Task<RoleInfo> CreateRoleAsync(string name, string modelPermission, string origin);
        Task<SetResult> DeleteRoleAsync(string name, string origin);
        Task<SetResult> SetRolePermissionAsync(string name, string modelPermission, string origin);
        Task<SetTablePermissionResult> SetTablePermissionAsync(string roleName, string tableRef, string filterDax, string origin);
        Task<SetResult> SetRoleMemberAsync(string roleName, string memberName, bool add, string origin);
        Task<SetResult> SetTableObjectPermissionAsync(string roleName, string tableRef, string permission, string origin);
        Task<SetResult> SetColumnObjectPermissionAsync(string roleName, string columnRef, string permission, string origin);

        // --- authoring generators (calendar / time-intelligence) ---
        Task<GenerateResult> GenerateDateTableAsync(string tableName, string startExpr, string endExpr, bool markAsDate, string origin);
        Task<GenerateResult> GenerateTimeIntelligenceAsync(string baseMeasureRef, string dateColumn, string[] variants, string displayFolder, string origin);

        // --- calendar-based time intelligence (CL 1701+) ---
        Task<CalendarListResult> ListCalendarsAsync(string tableRef);
        Task<CalendarResult> DefineCalendarAsync(string tableRef, string name, CalendarMappingSpec[] mappings, string description, string origin);
        Task<SetResult> DeleteCalendarAsync(string tableRef, string name, string origin);
        Task<CalendarResult> TagCalendarColumnAsync(string tableRef, string calendarName, string column, string timeUnit, bool associated, bool remove, string origin);
        Task<CalendarResult> DefineCalendarFromTemplateAsync(string template, string tableName, string dateColumn, int fiscalStartMonth, string startExpr, string endExpr, string calendarName, string origin);

        // --- live connectivity (attached-readonly DAX/DMV) ---
        Task<ConnectionStatus> ConnectXmlaAsync(string endpoint, string database, string authMode, string rawToken);
        Task<ConnectionStatus> ConnectLocalAsync(string dataSource, string database);
        Task<LocalInstance[]> ListLocalInstancesAsync();
        Task<ConnectionStatus> ConnectionStatusAsync();
        Task<ConnectionStatus> DisconnectAsync();
        Task<ResultSet> RunDaxAsync(string query, int maxRows, string origin = "human");
        Task<ResultSet> RunDmvAsync(string query, int maxRows);
        Task<ResultSet> PreviewTableAsync(string table, int topN, string origin = "human");
        Task<ResultSet> PivotMeasureAsync(string measureExpr, string[] rowFields, string colField, string[] filters, int maxRows, string origin = "human");
        // --- Tests tab: SQL↔DAX reconciliation runner (live; feeds the pure MeasureReconciler judge) ---
        Task<ReconcileRunResult> ReconcileMeasureAsync(ReconcileRequest request, string origin = "human");
        Task<ReconcileMappingReview> ReviewReconcileMappingAsync(ReconcileMappingRequest request, string origin = "human");
        Task<VpaqReport> VertiPaqScanAsync(int topN);
        // Export the model to a VertiPaq-Analyzer .vpax (Microsoft/SQLBI Dax.Vpax) — the interchange format for VertiPaq
        // Analyzer / DAX Studio / the SQLBI ecosystem. Metadata is offline; storage stats are a future live enrichment.
        Task<VpaxExportResult> ExportVpaxAsync(string path);

        // --- AI-native DAX optimize/verify loop (live connection) ---
        Task<BenchmarkResult> BenchmarkDaxAsync(string query, int runs);
        Task<ServerTimings> ProfileDaxAsync(string query);
        Task<EvalLogResult> EvaluateAndLogAsync(string query, int maxRows, string origin = "human");
        Task<EquivalenceResult> VerifyEquivalenceAsync(string exprA, string exprB, string[] groupBy, string[] filters, int maxRows);
        Task<ClearCacheResult> ClearCacheAsync(bool confirm);
        Task<ColdWarmBenchmark> BenchmarkColdWarmAsync(string query, int runs, bool clearForCold, bool confirm);
        Task<QueryPlanResult> CaptureQueryPlanAsync(string query);

        // --- Verified Edits: the enforced author→prove→benchmark→apply workflow (needs a live connection) ---
        Task<OptimizeMeasureResult> OptimizeMeasureAsync(string measureRef, string[] candidates, string[] verifyGroupBy, string[] verifyFilters, bool apply, string origin);
        // --- Verified Edits: multi-context evidence probe (read-only; needs a live connection) ---
        Task<ProbeResult> ProbeMeasureAsync(string expr, string primaryAxis, ProbeScenario[] scenarios, bool includeDefaults, int rowCap);
        // --- Verified Edits: value-capture-at-edit-start (free; needs a live connection) ---
        Task<BaselineCaptureResult> CaptureBaselineAsync(string objRef, string[] groupBy, string[] filters, bool includeDependents, int maxMeasures, int rowCap, string label, string origin);
        Task<BaselineCompareResult> CompareBaselineAsync(string captureId, string label, string origin);
        // --- Number time-machine (feature #3): "what moved this number?" over the ambient vital-signs
        //     history. Pro with a SOFT gate: free returns Status="pro" + a plain invitation, never throws. ---
        Task<BlameResult> BlameValueAsync(string measureRef, string context, string sinceCheckpoint, string origin);
        Task<ValueHistoryResult> ListValueHistoryAsync(string measureRef, string context);
        // --- Explain This Number (feature #2): the deterministic "why is this number what it is?" dossier —
        //     value re-derived in the cell's exact filter context, dependency chain, source lineage, top
        //     contributors (non-additive-guarded), the why-is-this-blank checklist, RLS advisory. FREE,
        //     read-only; offline degrades to the metadata-only dossier (Evidence.Available=false). ---
        Task<ExplainDossier> ExplainValueAsync(string measureRef, ExplainFilterContext context, bool decompose, string decomposeBy, int topK, string origin);
        // --- Verified Edits: the append-only, hash-chained audit trail (list = free read; export = Pro) ---
        Task<VerifiedEditsChain> ListVerifiedEditsAsync();
        Task<string> ExportVerifiedEditsAsync(string format);

        // --- Tests tab (the Prove intent, docs/tests-tab-spec.md): the suite coordinator over the E1/E4
        //     evaluators + E2 store + E3 analyzer. run = FREE (ambient relationship-integrity probes + static
        //     security + saved reconciles, every verdict + evidence shown); persisted suite / run history = Pro.
        //     Read-only w.r.t. the model — a run never mutates, so no undo/broadcast rides these. ---
        Task<TestSuiteRunResult> RunTestSuiteAsync(bool persist, string origin);
        Task<TestSuiteInfo> ListTestDefinitionsAsync();
        Task<TestDefinition> SaveTestDefinitionAsync(TestDefinition def, string origin);
        Task<bool> DeleteTestDefinitionAsync(string id, string origin);
        Task<TestHistoryInfo> ListTestRunsAsync(int last);
        Task<TestReportResult> ExportTestReportAsync();

        // --- model-scoped shared evidence: explicit persistence of the existing sealed artifact, stored under
        //     .semanticus/evidence/<model-identity>; no silent writes and no second renderer/format. ---
        Task<EvidenceLibrary> ListEvidenceAsync();
        Task<Semanticus.Engine.Evidence.EvidenceArtifact> GetEvidenceAsync(string id);
        Task<EvidenceSaveResult> SaveEvidenceAsync(string source, string sourceId, string origin);

        // --- live activity broadcast: surface a NON-mutating run (the agent's run_dax/profile/…) to every door ---
        Task PublishActivityAsync(ActivityEvent e);

        // --- health delta (feature #4): the MCP success filter's drain of the agent-origin delta(s) stashed for
        //     ONE tool call (correlationId; null = identity-less commits only). Take-once; null when free /
        //     no model / nothing since the last pull. Plumbing, NOT an MCP tool. ---
        Task<HealthDelta> PullAgentHealthAsync(string correlationId = null);

        // --- Pro-mode workflow engine: enforced, evidence-verified playbooks (library reads = free;
        //     starting a run with any enforced gate = Pro — enforcement is what's paid, not the playbook) ---
        Task<WorkflowInfo[]> ListWorkflowsAsync();
        Task<WorkflowEnforcement> GetWorkflowEnforcementAsync();
        Task<WorkflowEnforcement> SetWorkflowEnforcementAsync(string mode, string origin);
        Task<WorkflowInfo[]> SetWorkflowEnabledAsync(string name, bool enabled, string origin);
        // §9c op→workflow binding (mandatory routing): setting a hard|warn binding is Pro; the policy read is free.
        Task<WorkflowInfo[]> SetWorkflowBindingAsync(string op, string[] requireNames, string mode, string origin);
        // §10.6 dynamic activation (D5): show a workflow only when a condition holds; writing a rule is Pro.
        Task<WorkflowInfo[]> SetWorkflowActivationAsync(string workflow, string when, string set, string origin);
        Task<WorkflowPolicy> GetWorkflowPolicyAsync();
        Task<WorkflowProfileInfo[]> ListWorkflowProfilesAsync();
        Task<WorkflowProfileResult> ActivateWorkflowProfileAsync(string name, string origin);
        Task<WorkflowDef> GetWorkflowAsync(string name);
        Task<WorkflowRunView> StartWorkflowAsync(string name, string origin);
        Task<WorkflowRunView> GetWorkflowRunAsync(string runId);
        Task<WorkflowRunView> SubmitWorkflowStepAsync(string runId, string stepId, string answersJson, string origin);
        Task<WorkflowRunView> SkipWorkflowStepAsync(string runId, string stepId, string reason, string origin);
        Task<WorkflowRunView> AbortWorkflowAsync(string runId, string reason, string origin);
        Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidenceAsync(string runId);
        // designer/authoring surface — free (authoring is content); parse-validate-before-write
        Task<WorkflowInfo[]> SaveWorkflowAsync(string name, string markdown, string origin);
        Task<WorkflowInfo[]> DeleteWorkflowAsync(string name, string origin);
        // §10 workflow TEMPLATES (the customisation layer) — all FREE (authoring is content). A template is a
        // recipe with declared slots the user fills; instantiate renders it into a concrete workflow through a
        // deterministic, STRUCTURE-PRESERVING, admission-gated pipeline (the injection defence lives there).
        Task<WorkflowTemplateInfo[]> ListWorkflowTemplatesAsync();
        Task<WorkflowTemplate> GetWorkflowTemplateAsync(string name);
        Task<WorkflowTemplateInfo[]> SaveWorkflowTemplateAsync(string name, string markdown, string origin);
        Task<WorkflowTemplateInfo[]> DeleteWorkflowTemplateAsync(string name, string origin);
        Task<WorkflowInfo[]> InstantiateWorkflowTemplateAsync(string templateName, string newName, string valuesJson, string origin);
        Task<OpInfo[]> GetOpCatalogAsync();
        // Learning Loop L4 admission dry-run: parse + statically resolve a workflow against the op surface + its gate inputs.
        Task<WorkflowCheckReport> CheckWorkflowAsync(string name);
        // Learning Loop L4 REPLAY check: extends check_workflow — rehearses each step op via dry_run driven by the
        // workflow's exemplar-run answers (guaranteed rollback); DAX probes are surfaced replayable, not executed.
        Task<WorkflowReplayReport> ReplayCheckWorkflowAsync(string name);
        // Universal dry-run contract (docs/harness-engineering.md §4): rehearse any single model-mutating op via its
        // real code path — exact would-be deltas + the op's result, guaranteed no mutation/undo/broadcast/audit.
        Task<DryRunReport> DryRunOpAsync(string op, string argsJson);

        // --- Learning Loop L1 (knowledge store) + L2 (recall): the user's own insight store — plain JSONL,
        //     delta-only, provenance-tagged, write-gated. Capture + recall-view are FREE (the compounding
        //     machinery — distillation/enforcement — is Pro, later). Recall is DETERMINISTIC; the agent ranks. ---
        Task<InsightRecord> AddInsightAsync(string text, string[] keys, string kind, string scope, bool fingerprintScoped, string origin);
        Task<InsightRecord> ApproveInsightAsync(string id, string origin);
        Task<InsightRecord> EditInsightAsync(string id, string text, string[] keys, string origin);
        Task<InsightRecord> UpvoteInsightAsync(string id, string origin);
        Task<InsightRecord> DownvoteInsightAsync(string id, string origin);
        Task<SetResult> DeleteInsightAsync(string id, string origin);
        Task<PurgeResult> PurgeKnowledgeAsync(string scope, bool confirm, string origin);
        Task<InsightListResult> ListInsightsAsync(string scope, string status);
        Task<RecallResult> RecallExperienceAsync(string query, int maxResults);
        Task<PrimerDocument> GetPrimerAsync();
        Task<PrimerDocument> SetPrimerAsync(string markdown, string origin);
        Task<PrimerSuggestionList> ListPrimerSuggestionsAsync();
        Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin);
        Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin);
        Task<ModelFingerprint> GetModelFingerprintAsync();

        // --- The Model Interview (behavioral readiness): a deterministic question bank the engine executes,
        //     compares, and scores — Correct | Refused | SilentlyWrong | Unverified. The user's Claude authors
        //     questions/DAX (the /interview-model skill); the engine never infers (golden rule 1). FREE: list +
        //     one-off run + delete; PRO: add_interview_question (persisting the replayable pack). ---
        Task<InterviewListResult> ListInterviewQuestionsAsync(string scope);
        Task<InterviewQuestion> AddInterviewQuestionAsync(string question, string tier, string query, string scalarExpr, string paraphraseExpr, string[] groupBy, string[] filters, string expectedValue, string expectedMatrixJson, bool expectRefusal, string fixRuleId, string seedSource, string scope, string origin);
        Task<InterviewRunResult> RunInterviewAsync(string questionId, string inlineJson, bool abstained, string attemptDax, string origin);
        Task<SetResult> DeleteInterviewQuestionAsync(string id, string origin);
        Task<InterviewSeedResult> ListInterviewSeedsAsync(string source, string measure);   // verified answers + the built-in hard-question pack (free, read-only; no fabricated oracle)

        // --- Change-Plan engine ("analyse → review → fix everything" / incremental edits) ---
        Task<ChangePlanView> ProposePlanAsync(string scope, bool includeAi, int maxAiItems, string origin);
        // Bulk find & replace as a plan (Phase 3): search hits → rename/set_*/set_dax/set_m items; DAX-reference,
        // DAX-code and RLS matches yield NO items (reported in the view's Note). Apply rides apply_plan's Pro gate.
        Task<ChangePlanView> ProposeReplaceAsync(SearchOptions find, string replace, int maxItems, string origin);
        Task<ChangePlanView> GetPlanAsync();
        Task<ChangePlanView> AddPlanItemAsync(string objRef, string kind, string after, string title, string[] verifyGroupBy, string[] verifyFilters, string origin);
        Task<ChangePlanView> SetPlanItemAsync(string itemId, string after, bool? approved, string origin);
        // overrideIds ship named items past a failed/unprovable equivalence verdict — reason required, recorded.
        Task<ApplyPlanReport> ApplyPlanAsync(string[] approvedIds, string origin, string[] overrideIds = null, string overrideReason = null);
        Task<ChangePlanView> ClearPlanAsync(string origin);

        // --- Documentation (the read-only snapshot the exporter renders + annotation-backed narrative) ---
        Task<DocModelDto> GetDocModelAsync(int topN);
        Task<DocOutline> GetDocOutlineAsync();
        Task<string> GetDocSectionAsync(string objRef, string sectionKey);
        Task<SetResult> SetDocSectionAsync(string objRef, string sectionKey, string markdown, string origin);

        // --- Model spec (spec-driven authoring: autogenerate -> refine -> build) ---
        Task<SpecView> GetSpecAsync();
        Task<SpecView> SetSpecAsync(string specJson, string origin);
        Task<SpecView> ClearSpecAsync(string origin);
        Task<SpecView> SaveSpecAsync(string path);
        Task<SpecView> LoadSpecAsync(string path, string origin);
        Task<SpecBuildReport> BuildModelFromSpecAsync(string origin);
        Task<SpecView> AutogenerateSpecFromModelAsync(string origin);
        Task<SpecView> AutogenerateSpecFromFabricAsync(string server, string database, string authMode, string storageMode, string origin, string tenantId = null);

        // --- Source control (local git, the ALM lane) ---
        Task<GitStatus> GitStatusAsync();
        Task<GitDiffResult> GitDiffAsync(string path, bool staged);
        Task<GitLogEntry[]> GitLogAsync(int max);
        Task<GitCommitResult> GitCommitAsync(string message, string[] files, bool commit, string origin);
        Task<HistoryCheckpointList> ListHistoryCheckpointsAsync(int max = 50);
        Task<HistoryCheckpointResult> CreateHistoryCheckpointAsync(string label, bool commit, string origin);
        Task<HistoryRestoreResult> RestoreHistoryCheckpointAsync(string hash, bool restore, string origin);
        Task<GitActionResult> GitBranchAsync(string name, bool create, bool checkout, string origin);
        Task<GitActionResult> GitCheckoutAsync(string gitRef, string origin);
        Task<GitActionResult> GitPullAsync(string origin);
        Task<GitActionResult> GitPushAsync(string remote, string branch, bool confirm, string origin);
        Task<GitActionResult> GitCloneAsync(string url, string directory, string origin);

        // --- Model compare (ALM Toolkit) + the deploy gate ---
        Task<ModelDiff> CompareModelsAsync(ModelRef left, ModelRef right, bool includeEqual = false, string origin = "human");
        Task<ApplyDiffResult> ApplyDiffAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason = null);
        Task<CherryPickResult> CherryPickAsync(ModelRef source, string[] refs, bool includeDependencies, bool commit, string origin);
        Task<TreeNode[]> ListReferenceTreeAsync(ModelRef reference, string origin = "human");
        Task<DeployGate> DeployGateAsync(ModelRef compareTarget);

        // --- Connections: one registry, doing double duty as the agent-permissions target registry ---
        Task<ModelConnectionRecord[]> ListConnectionsAsync();
        Task<ModelConnectionRecord> RememberXmlaConnectionAsync(string endpoint, string database, string modelName, string authMode, string origin = "agent");
        Task<WorkingCopyResult> PrepareWorkingCopyAsync(string connectionId, string parentFolder, bool commit, string queryConnectionId = null, string publishConnectionId = null, string origin = "agent");
        Task<ConnectionContext> SetPublishDestinationAsync(string connectionId, string origin = "agent");
        Task<ModelConnectionRecord> LabelConnectionAsync(string id, string label, string origin = "agent");
        Task<ModelConnectionRecord> SetConnectionWorkingFolderAsync(string id, string folder);
        Task<bool> ForgetConnectionAsync(string id, string origin = "agent");

        // --- Agent policy + approvals (the permissions matrix + the "waiting for you" queue) ---
        Task<AgentPolicy> GetAgentPolicyAsync();
        Task<AgentPolicy> SetAgentPolicyPresetAsync(string preset, string origin);
        Task<AgentPolicy> SetAgentPolicyCellAsync(string capability, string label, string action, string origin);
        Task<AgentPolicy> SetAgentPolicyEnabledAsync(bool enabled, string origin);
        Task<ApprovalRecord[]> ListPendingApprovalsAsync();
        Task<ApprovalRecord> ApproveAgentActionAsync(string id, string origin);
        Task<bool> DenyAgentActionAsync(string id, string origin);

        // --- Restore points: the undo for a push to a published model (a live delete is otherwise permanent) ---
        Task<RestorePointRecord[]> ListRestorePointsAsync(string endpoint = null, string database = null);
        Task<RollbackResult> RollbackPushAsync(string restorePointId, bool commit, string authMode = null, string origin = "human");
        Task<RestorePointPurgeResult> PurgeRestorePointsAsync(string id = null, int? olderThanDays = null,
            bool confirm = false, string confirmToken = null, string origin = "human");

        // --- Fabric REST (the cloud ALM lane — read-only discovery) ---
        Task<FabricWorkspace[]> ListWorkspacesAsync(string authMode, string tenantId);
        Task<DeploymentPipeline[]> ListDeploymentPipelinesAsync(string authMode, string tenantId);
        Task<PipelineStage[]> GetPipelineStagesAsync(string pipelineId, string authMode, string tenantId);
        Task<StageItem[]> GetStageItemsAsync(string pipelineId, string stageId, string authMode, string tenantId);

        // --- Deployment pipeline: preview + the GATED deploy (write lane) + history ---
        Task<DeployPreview> PreviewDeployAsync(string pipelineId, string sourceStageId, string targetStageId, string authMode, string tenantId);
        Task<DeployStageReport> DeployStageAsync(string pipelineId, string sourceStageId, string targetStageId, string[] items, string note, bool commit, string confirmToken, bool forceOverride, string authMode, string tenantId, string origin, string overrideReason = null);
        Task<DeploymentHistoryEntry[]> DeploymentHistoryAsync(string pipelineId, string authMode, string tenantId);

        // --- Fabric Git (workspace ⇄ git): reads + GATED writes ---
        Task<FabricGitConnection> FabricGitConnectionAsync(string workspaceId, string authMode, string tenantId);
        Task<FabricGitStatus> FabricGitStatusAsync(string workspaceId, string authMode, string tenantId);
        Task<FabricGitResult> FabricGitCommitAsync(string workspaceId, string comment, string[] items, bool commit, string authMode, string tenantId, string origin);
        Task<FabricGitResult> FabricGitUpdateAsync(string workspaceId, string conflictPolicy, bool allowOverride, bool commit, string authMode, string tenantId, string origin);
        Task<FabricGitResult> FabricGitConnectAsync(string workspaceId, string provider, string organization, string project, string repository, string branch, string directory, string connectionId, bool commit, string authMode, string tenantId, string origin);
        Task<FabricGitResult> FabricGitDisconnectAsync(string workspaceId, bool commit, string authMode, string tenantId, string origin);

        // --- CI/CD: publish the open model's definition (Items API, GATED) + emit a fabric-cicd scaffold (no gate) ---
        Task<CicdPublishResult> CicdPublishAsync(string workspaceId, string itemId, bool commit, string authMode, string tenantId, string origin);
        Task<CicdScaffold> CicdGenerateAsync(string target, string workspaceId, string environment, bool write);

        // --- Fabric Data Agent (definition-based item): reads (free) + the model-scope generator (Pro) + dry-run writes ---
        Task<DataAgentList> ListDataAgentsAsync(string workspaceId, string authMode, string tenantId);
        Task<DataAgentDetail> GetDataAgentAsync(string workspaceId, string agentId, string authMode, string tenantId);
        Task<DataAgentConfig> GenerateDataAgentConfigFromModelAsync(int maxColumnsPerTable);
        Task<DataAgentWriteReport> CreateDataAgentAsync(string workspaceId, string name, string aiInstructions, bool commit, string authMode, string tenantId, string origin);
        Task<DataAgentWriteReport> UpdateDataAgentAsync(string workspaceId, string agentId, string aiInstructions, string datasourceFolder, string datasourceJson, string fewshotsJson, bool commit, string authMode, string tenantId, string origin);
        Task<DataAgentWriteReport> PublishDataAgentAsync(string workspaceId, string agentId, string description, bool commit, string authMode, string tenantId, string origin);
        Task<DataAgentWriteReport> DeleteDataAgentAsync(string workspaceId, string agentId, bool commit, string authMode, string tenantId, string origin);
    }
}
