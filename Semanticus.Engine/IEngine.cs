using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.StartPlan;

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
        Task<OpenResult> OpenAsync(string path, bool discardUnsaved = false);
        Task<OpenResult> CreateModelAsync(string name, int compatibilityLevel, bool discardUnsaved = false);
        Task<OpenResult> OpenLocalAsync(string dataSource, string database, bool discardUnsaved = false);
        // accountProfileId (Phase 2, T160): pick a SAVED account profile for THIS open only — it never repoints the tenant
        // default (that is what makeDefault does, an explicit human choice with a stated blast radius). forceReauth adds a
        // NEW profile via the Microsoft picker (a human action; the agent door is refused). origin gates the human-only paths.
        // makeDefault is NULLABLE for wire-compat: null = a LEGACY caller (a forced re-sign implies the Phase 1 tenant
        // repoint); explicit false = Phase 2 "add a profile without repointing"; explicit true = make-default.
        Task<OpenResult> OpenLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool forceReauth = false, string accountProfileId = null, bool? makeDefault = null, string loginHint = null, string origin = "human", bool discardUnsaved = false, bool silentOnly = false);
        // commit runs the accountable checkpoint: a RED deploy gate pauses; overrideReason ships anyway (recorded).
        Task<DeployReport> DeployLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human", string overrideReason = null, string confirmToken = null, string[] deleteRefs = null, string expectedEndpoint = null, string expectedDatabase = null);
        Task<RefreshTypeInfo[]> ListRefreshTypesAsync();
        Task<RefreshReport> RefreshPartitionAsync(string partitionRef, string refreshType, string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human");
        Task<TreeNode[]> ListTreeAsync(string parentRef);
        Task<ObjectInfo> GetObjectAsync(string objRef);
        Task<string> GetDaxAsync(string objRef);
        // expectedSession (optional): the SessionInfo.SessionId the caller sampled when it verified this edit belongs to
        // the live model. When non-null and it no longer matches the live session's id, the write is REFUSED before any
        // mutation — closing the model-swap race for the zero-dialog header-save path. Fencing on the session id (not the
        // source path) also fences unsaved models and catches a same-file REOPEN (same source, new session). Null = no
        // fence (both doors' other callers, and every MCP tool, are unaffected).
        // expectedRevision (round 7): an optional finer fence than expectedSession — refuse if ANY mutation landed since the
        // caller captured the revision it verified against (the rename→body pair + recovery writes; see GuardExpectedRevision).
        Task<SetResult> SetDaxAsync(string objRef, string expression, string origin, string expectedSession = null, long? expectedRevision = null, string expectedExpression = null);
        Task<SaveResult> SaveAsync(string path, string format, bool overwrite, string origin, bool pickedInDialog = false);
        Task<SessionInfo> SessionInfoAsync();
        Task<bool> ResolveFileRecoveryAsync(string expectedSession, bool restore, string origin);
        Task<ConnectionContext> ConnectionContextAsync();
        // Pro entitlement (offline-verified license) — read-only, both doors. The free tier is fully usable; Pro
        // unlocks the one-click BULK apply (apply_change_plan >1 item, apply_safe_fixes for either system). See Entitlement/.
        Task<Entitlement.EntitlementInfo> GetEntitlementAsync();
        // The free Pro month's tally, counted on this computer from what really happened: both doors (getTrialTally /
        // get_trial_tally). All zeros when the licence is not a trial token.
        Task<Entitlement.TrialTally> GetTrialTallyAsync();
        // The window-only settings and the trial start, with an agent door (F-292, F-293). The window owns both; the engine asks it by
        // notification. ReportHostSettingsAsync is PERSON-ONLY (the window tells the engine what the settings are): no MCP tool.
        /// <summary>get_personality_settings: the Studio's personality (on or quiet), sounds and volume, as the window last reported them.</summary>
        Task<HostSettingsView> GetHostSettingsAsync();
        /// <summary>set_personality_settings: set any of personality, sounds, soundVolume; the window writes the person's VS Code settings.</summary>
        Task<HostSettingsView> SetHostSettingsAsync(string personality, bool? sounds, int? soundVolume, string origin = "human");
        /// <summary>The window reports the settings (on connect and on every change).</summary>
        Task ReportHostSettingsAsync(string personality, bool sounds, int soundVolume);
        /// <summary>start_trial: ask the window to start the free Pro month for this email. Sends nothing unless the person said yes in chat.</summary>
        Task<TrialStartResult> StartTrialAsync(string email, bool personSaidYes, string origin = "human");
        // Record verified edits: read + set the human-controlled audit switch (it adds a record; every DAX write is checked anyway).
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
        // Plan-heart contract 8.6 and 8.8: undo or redo to a History entry in one commit, and the merged History.
        Task<UndoState> UndoToEntryAsync(string entryId, string origin, long? throughRevision = null);
        Task<UndoState> RedoToEntryAsync(string entryId, string origin);
        Task<PlanHistoryView> GetPlanHistoryAsync(string by = null, string objectRef = null, string before = null, int limit = 50);

        // --- Best Practice Analyzer (general-purpose) ---
        Task<BpaScorecard> BpaScanAsync();
        Task<SetResult> BpaFixAsync(string ruleId, string objRef, string origin);
        Task<BpaFixAllResult> BpaFixAllAsync(string origin);
        Task<FixPrompt> BpaGetFixPromptAsync(string ruleId, string objRef);
        Task<BpaRulesInfo> LoadBpaRulesAsync(string source, bool replace, string origin, string expectedRuleJson = null);
        Task<BpaRulesInfo> ResetBpaRulesAsync(string origin);

        // --- Custom rule authoring (both kinds) ---
        Task<ReadinessRulesInfo> LoadReadinessRulesAsync(string source, bool replace, string origin, string expectedRuleJson = null);
        Task<ReadinessRulesInfo> ResetReadinessRulesAsync(string origin);
        Task<RuleValidationResult> ValidateRuleAsync(string kind, string rules);   // compile + test-run preview, read-only
        Task<CustomRulesInfo> GetCustomRulesAsync();

        // --- AI-readiness ---
        Task<Scorecard> AiReadinessScanAsync();
        Task<Scorecard> AiReadinessScanLiveAsync();
        Task<SafeFixResult> ApplySafeFixesAsync(string origin);
        /// <summary>The reading card: the grade, the grade after the safe fixes (when asked for) and the top three issues in plain words (Kane 12 Oct 2026).</summary>
        Task<ReadingCardView> ReadingCardAsync(bool withAfter = true);
        /// <summary>Fix them: every safe fix as one undo step, then the writing as jobs for the assistant.</summary>
        Task<FixThemResult> FixThemAsync(string origin);
        Task<SetResult> ApplyFixAsync(string ruleId, string objRef, string origin);
        Task<FixPrompt> GetFixPromptAsync(string ruleId, string objRef);
        Task<GroundingBundle> GetGroundingAsync(string objRef);

        // --- Finding waivers (accepted findings) — both BPA and AI-readiness, persisted on the model ---
        Task<BatchEditResult> UnwaiveFindingsAsync(FindingFixItem[] items, string origin);
        Task<BatchEditResult> ApplyFixesAsync(FindingFixItem[] items, string origin);
        Task<BatchEditResult> DuplicateObjectsAsync(ObjectDuplicateItem[] items, string origin);
        Task<BatchEditResult> SetDocSectionsAsync(DocSectionItem[] items, string origin);
        Task<BatchEditResult> SetPerspectiveMembersAsync(PerspectiveMemberItem[] items, string origin);
        Task<BatchEditResult> SetObjectPermissionsAsync(ObjectPermissionItem[] items, string origin);
        Task<BatchPlanResult> SetPlanItemsAsync(PlanItemEdit[] items, string origin);
        Task<BatchPlanResult> AddPlanItemsAsync(PlanItemAdd[] items, string origin);
        Task<BatchStartPlanResult> EditStartPlansAsync(StartPlan.StartPlanEdit[] items, string origin);
        Task<BatchEditResult> WaiveFindingsAsync(FindingWaiverItem[] items, string origin);
        Task<BatchPlanRowsResult> PlanRowsAsync(PlanRowEdit[] items, string origin);
        Task<BatchEditResult> WriteWordsAsync(WordsWriteItem[] items, string origin);
        Task<BatchEditResult> EditObjectsAsync(ObjectEditItem[] items, string origin);
        Task<BatchEditResult> CreateMeasuresAsync(MeasureCreateItem[] items, string origin);
        Task<SetResult> WaiveFindingAsync(string system, string ruleId, string objRef, string reason, string origin);   // objRef null/'*' = rule-level; free either way
        Task<SetResult> UnwaiveFindingAsync(string system, string ruleId, string objRef, string origin);
        Task<WaiverRecord[]> ListWaiversAsync();
        Task<DaxValidation> ValidateDaxAsync(string expression, bool strict = false);
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
        Task<SetResult> SetPartitionMAsync(string partitionRef, string mExpression, string origin, string expectedM = null);
        Task<DocExpression[]> ListNamedExpressionsAsync();
        Task<string> GetNamedExpressionAsync(string name);
        Task<SetResult> UpdateNamedExpressionAsync(string name, string expression, string origin, string expectedExpression = null);
        Task<string> CreateImportTableAsync(string name, string mExpression, string origin);
        Task<string> CreateDirectLakeTableAsync(string name, string entityName, string schemaName, string sourceName, string origin);
        Task<string> CreateCalculatedTableAsync(string name, string expression, string origin);
        Task<string> CreateFieldParameterAsync(string name, FieldParameterItem[] items, string origin);   // Studio v2 Advanced Modelling
        Task<SetResult> SetColumnDataTypeAsync(string columnRef, string dataType, string origin, string expectedDataType = null, string expectedIdentity = null);
        // --- Update Schema (Tabular-Editor-style): re-read a table's SOURCE columns, diff vs the model, apply a
        //     chosen subset. The source-read needs a reachable SQL/Fabric endpoint (parsed from the partition M) and
        //     fails GRACEFULLY (Reachable=false + a message) on an offline snapshot / non-SQL source. Diff + apply
        //     are source-agnostic: supply sourceColumns to diff a synthesized schema (the manual/offline path).
        Task<SourceSchema> GetSourceSchemaAsync(string tableRef, string authMode, string tenantId, string origin = "human");
        Task<SchemaDiff> DiffSchemaAsync(string tableRef, SourceColumn[] sourceColumns, string authMode, string tenantId, string origin = "human");
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
        Task<SetResult> SetCalcItemFormatStringAsync(string calcItemRef, string formatExpression, string origin, string expectedFormat = null);
        Task<SetResult> SetCalcGroupPrecedenceAsync(string calcGroupRef, int precedence, string origin, string expectedPrecedence = null);
        // Format-string template library: read the curated catalog (offline, no session) — the picker + the agent
        // share one source of truth. The measure DYNAMIC format-string setter ("Format = Dynamic") fills the gap
        // that set_measure_format is static-only, so the dynamic templates land on a plain measure (not just calc items).
        Task<FormatTemplateInfo[]> ListFormatTemplatesAsync(string category, string search);
        Task<SetResult> SetMeasureFormatExpressionAsync(string objRef, string formatExpression, string origin, string expectedFormatExpression = null, string expectedIdentity = null);
        // DaxLib UDF package manager (Studio v2 Advanced Modelling, 6th area). Browse/versions/info = anonymous read-only
        // (no session); install (Pro, bulk) / uninstall / list operate on the open model.
        Task<DaxLibPackage[]> DaxLibSearchAsync(string text, int skip, int take);
        Task<string[]> DaxLibVersionsAsync(string id);
        Task<DaxLibPackageDetail> DaxLibPackageInfoAsync(string id, string version);
        Task<DaxLibInstallResult> DaxLibInstallAsync(string id, string version, bool replaceExisting, string origin);
        Task<DaxLibInstalledRecord[]> DaxLibListInstalledAsync();
        Task<SetResult> DaxLibUninstallAsync(string id, string origin);
        Task<SetResult> DeleteObjectAsync(string objRef, string origin);
        // Multi-select delete: N objects as ONE undo step. One ref delegates to DeleteObjectAsync.
        Task<SetResult> DeleteObjectsAsync(string[] objRefs, string origin);
        Task<string> DuplicateObjectAsync(string objRef, string newName, string targetRef, string origin);
        Task<ObjectProperty[]> GetObjectPropertiesAsync(string objRef);
        Task<SetResult> SetObjectPropertyAsync(string objRef, string propertyName, string value, string origin, string expectedValue = null, string expectedIdentity = null);
        // Set ONE property to ONE value across N objects as a single atomic, undoable change (#140) — the
        // multi-select property-grid write. One undo entry / one broadcast; a failure at any object rolls the
        // WHOLE batch back and names it. The single-object op above stays the un-audited free path.
        Task<SetResult> SetObjectPropertiesAsync(string[] objRefs, string propertyName, string value, string origin, string expectedValue = null, string[] expectedValues = null, string expectedIdentity = null);
        // Display folders as ATOMIC gestures (a folder is just its members' DisplayFolder strings — an empty one
        // cannot persist): batch file/clear many objects, and rename a folder prefix table-wide (nested included).
        // Each is ONE undo step; a mid-batch failure rolls the whole batch back.
        Task<SetResult> SetDisplayFolderAsync(string[] refs, string folder, string origin, string expectedSession = null, long? expectedRevision = null);
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
        // The model's own report scope: which reports its answers are checked against, and what came of reading them.
        Task<Lineage.ReportScopeResult> ListReportScopeAsync();
        Task<Lineage.ReportScopeResult> SetReportScopeAsync(Lineage.ReportScopeChoice[] choices, string origin);
        Task<Lineage.ReportScopeResult> CheckReportsAsync(string[] ids, bool consent, string authMode, string tenantId, string runId, string origin, System.Threading.CancellationToken cancellationToken);
        // Cloud report layer (Phase 3): discover published reports + report-aware safe-to-remove over their cloud PBIR.
        Task<CloudReport[]> ListReportsAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);   // read-only (Power BI scope)
        Task<ReportAnalysisResult> AnalyzeCloudReportsAsync(string workspaceId, string[] reportIds, bool consent, string authMode, string tenantId, string runId = null, string origin = "human", CancellationToken cancellationToken = default);
        Task<string> ScriptObjectsAsync(string[] refs, string format);
        Task<ApplyScriptResult> ApplyDaxScriptAsync(string script, string origin, string[] applyAnyway = null, string expectedSession = null);
        Task<ApplyScriptResult> ApplyTmdlScriptAsync(string script, string origin, string[] applyAnyway = null, string expectedSession = null);
        Task<SetResult> SetCompatibilityLevelAsync(int level, string origin);
        // expectedSession/expectedRevision: see SetDaxAsync — the same optional session-id swap fence + revision fence for the header-save rename path.
        Task<RenameResult> RenameObjectAsync(string objRef, string newName, string origin, string expectedSession = null, long? expectedRevision = null);
        Task<SetResult> SetColumnMetadataAsync(string objRef, bool? isHidden, string summarizeBy, string dataCategory, string sortByColumn, string origin);
        Task<SetResult> SetMeasureFormatAsync(string objRef, string formatString, string origin, string expectedSession = null, long? expectedRevision = null);
        Task<SetResult> MarkDateTableAsync(string tableRef, string dateColumn, string origin);
        Task<SetResult> SetRelationshipAsync(string relationshipName, string crossFilteringBehavior, bool? isActive, string origin);
        Task<SetResult> SetRelationshipCardinalityAsync(string relationshipName, string fromCardinality, string toCardinality, string origin);
        Task<RefreshPolicyInfo> GetIncrementalRefreshPolicyAsync(string tableRef);
        Task<SetResult> SetIncrementalRefreshPolicyAsync(string tableRef, string dateColumn, int? rollingWindowPeriods, string rollingWindowGranularity, int? incrementalPeriods, string incrementalGranularity, int? incrementalPeriodsOffset, string mode, string pollingExpression, bool autoWire, string origin, string expectedJson = null);
        Task<SetResult> RemoveIncrementalRefreshPolicyAsync(string tableRef, string origin);
        Task<ReadinessPlan> MakeAiReadyAsync(string origin, int maxQueue);
        Task<SetResult> CreateLinguisticSchemaAsync(string culture, string origin);
        Task<SetResult> SetSynonymsAsync(string objRef, string[] terms, string culture, string origin);
        Task<SetInstructionsResult> SetAiInstructionsAsync(string instructions, string culture, string origin, string expectedInstructions = null);
        Task<AiInstructionsInfo> GetAiInstructionsAsync(string culture);
        Task<SetResult> SetAiDataSchemaAsync(string objRef, bool included, string culture, string origin);

        // --- row-level security (RLS) roles ---
        Task<RoleInfo[]> ListRolesAsync();
        Task<RoleInfo> CreateRoleAsync(string name, string modelPermission, string origin);
        Task<SetResult> DeleteRoleAsync(string name, string origin);
        Task<SetResult> SetRolePermissionAsync(string name, string modelPermission, string origin);
        Task<SetTablePermissionResult> SetTablePermissionAsync(string roleName, string tableRef, string filterDax, string origin, string expectedFilter = null);
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
        Task<ConnectionStatus> ConnectXmlaAsync(string endpoint, string database, string authMode, string rawToken, string tenantId = null, bool forceReauth = false, string accountProfileId = null, bool? makeDefault = null, string loginHint = null, string origin = "human");
        Task<ConnectionStatus> ConnectLocalAsync(string dataSource, string database);
        Task<LocalInstance[]> ListLocalInstancesAsync();
        Task<ConnectionStatus> ConnectionStatusAsync();
        Task<ConnectionStatus> DisconnectAsync();
        Task<ResultSet> RunDaxAsync(string query, int maxRows, string origin = "human", CancellationToken cancellationToken = default);
        Task<CancelQueryResult> CancelDaxAsync();
        Task<ResultSet> RunDmvAsync(string query, int maxRows);
        Task<ResultSet> PreviewTableAsync(string table, int topN, string origin = "human");
        Task<ResultSet> PivotMeasureAsync(string measureExpr, string[] rowFields, string colField, string[] filters, int maxRows, string origin = "human");
        // --- Tests tab: SQL↔DAX reconciliation runner (live; feeds the pure MeasureReconciler judge) ---
        Task<ReconcileRunResult> ReconcileMeasureAsync(ReconcileRequest request, string origin = "human");
        Task<ReconcileMappingReview> ReviewReconcileMappingAsync(ReconcileMappingRequest request, string origin = "human");
        Task<VpaqReport> VertiPaqScanAsync(int topN);
        // Export the model to a VertiPaq-Analyzer .vpax (Microsoft/SQLBI Dax.Vpax) — the interchange format for VertiPaq
        // Analyzer / DAX Studio / the SQLBI ecosystem. Metadata is offline; storage stats are a future live enrichment.
        Task<VpaxExportResult> ExportVpaxAsync(string path, string origin = "human");

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
        //     evaluators + E2 store + E3 analyzer. run = FREE (ambient relationship-integrity probes +
        //     table row counts + saved reconciles, every verdict + evidence shown); persisted suite / run
        //     history = Pro. Role-filter checks and the Model interview replay left this run in 1.2.0.
        //     Read-only w.r.t. the model — a run never mutates, so no undo/broadcast rides these. ---
        /// <summary>Run the suite. <paramref name="runId"/> names the run in its progress events (<c>progress/didChange</c>, OpKey "run_tests"),
        /// so the page that started it can show "312 of 691 checks" for its own run and no other. Omitted, the run names itself.</summary>
        Task<TestSuiteRunResult> RunTestSuiteAsync(bool persist, string origin, string[] only = null, string[] sections = null, string runId = null);
        Task<ReconcileOutcome> TryTestAsync(TestDefinition def, string origin);
        Task<TestSuiteInfo> ListTestDefinitionsAsync();
        Task<TestDefinition> SaveTestDefinitionAsync(TestDefinition def, string origin, string expectedJson = null);
        Task<bool> DeleteTestDefinitionAsync(string id, string origin);
        Task<TestHistoryInfo> ListTestRunsAsync(int last);
        Task<TestRunDetail> GetTestRunAsync(string runId);
        Task<TestRunRecordResult> RecordTestRunAsync(string runId, string origin);
        Task<UnmatchedRowsResult> GetUnmatchedRowsAsync(string relationship, int limit, string origin);
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

        // --- user-edit fold (F-246): what OTHER writers committed since this assistant's previous pull. reader null =
        //     this process's MCP door; the pipe passes the connection's own id and ignores any caller value. Take-once;
        //     null when nothing changed. Plumbing for the MCP call filter, NOT an MCP tool. ---
        Task<ForeignChanges> PullForeignChangesAsync(string reader = null);

        // --- Pro-mode workflow engine: enforced, evidence-verified playbooks (library reads = free;
        //     starting a run with any enforced gate = Pro — enforcement is what's paid, not the playbook) ---
        Task<WorkflowInfo[]> ListWorkflowsAsync();
        Task<WorkflowEnforcement> GetWorkflowEnforcementAsync();
        Task<WorkflowEnforcement> SetWorkflowEnforcementAsync(string mode, string origin);
        Task<WorkflowInfo[]> SetWorkflowEnabledAsync(string name, bool enabled, string origin);
        // §9c op→workflow binding (mandatory routing). Workflows is one whole Pro feature, the policy read included.
        Task<WorkflowInfo[]> SetWorkflowBindingAsync(string op, string[] requireNames, string mode, string origin);
        // §10.6 dynamic activation (D5): show a workflow only when a condition holds. Pro, with the rest of Workflows.
        Task<WorkflowInfo[]> SetWorkflowActivationAsync(string workflow, string when, string set, string origin);
        Task<WorkflowPolicy> GetWorkflowPolicyAsync();
        Task<WorkflowProfileInfo[]> ListWorkflowProfilesAsync();
        Task<WorkflowProfileResult> ActivateWorkflowProfileAsync(string name, string origin);
        Task<WorkflowDef> GetWorkflowAsync(string name);
        Task<WorkflowDocumentResult> GetWorkflowDocumentAsync(string name, string sessionId = null);
        Task<WorkflowEditPreviewResult> PreviewWorkflowEditAsync(string name, string expectByteHash, string expectPath, string editsJson, string draftText = null, bool create = false, string sessionId = null);
        Task<WorkflowUpgradeResult> UpgradeWorkflowAsync(string name, bool dryRun = true, string expectByteHash = null, string expectPath = null, string origin = "human", string sessionId = null);
        Task<WorkflowDocumentEditResult> EditWorkflowDocumentAsync(string name, string expectByteHash, string exactText, string expectPath, string origin, string sessionId = null);
        Task<WorkflowLayout> GetWorkflowLayoutAsync(string name);
        Task<WorkflowLayout> SaveWorkflowLayoutAsync(string name, System.Collections.Generic.Dictionary<string, WorkflowPosition> positions, string expectedRevision = null);
        /// <param name="document">Run once (Plan-heart contract 21.5): the workflow's unsaved text, run as it stands in
        /// Author, instead of the saved file. Null runs the saved file.</param>
        Task<WorkflowRunView> StartWorkflowAsync(string name, string origin, string document = null);
        Task<WorkflowRunView> GetWorkflowRunAsync(string runId);
        /// <summary><paramref name="writesJson"/>: a v3 direct step's model writes, an ordered JSON list of {op, args}
        /// from the executable allowlist, applied as ONE change so the step is one undo entry (composer contract 3.4).</summary>
        Task<WorkflowRunView> SubmitWorkflowStepAsync(string runId, string stepId, string answersJson, string origin, string callGate = null, string writesJson = null);
        Task<WorkflowRunView> SkipWorkflowStepAsync(string runId, string stepId, string reason, string origin);
        Task<WorkflowRunView> AbortWorkflowAsync(string runId, string reason, string origin);
        Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidenceAsync(string runId);
        // designer/authoring surface — free (authoring is content); parse-validate-before-write
        Task<WorkflowInfo[]> SaveWorkflowAsync(string name, string markdown, string origin, bool createOnly = false, string sessionId = null);
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
        Task<InsightRecord> EditInsightAsync(string id, string text, string[] keys, string origin, string expectedJson = null);
        Task<InsightRecord> UpvoteInsightAsync(string id, string origin);
        Task<InsightRecord> DownvoteInsightAsync(string id, string origin);
        Task<SetResult> DeleteInsightAsync(string id, string origin);
        Task<PurgeResult> PurgeKnowledgeAsync(string scope, bool confirm, string origin);
        Task<InsightListResult> ListInsightsAsync(string scope, string status);
        Task<RecallResult> RecallExperienceAsync(string query, int maxResults);
        Task<PrimerDocument> GetPrimerAsync();
        Task<PrimerDocument> SetPrimerAsync(string markdown, string origin, string expectedMarkdown = null);
        Task<PrimerSuggestionList> ListPrimerSuggestionsAsync();
        Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin);
        Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin);
        Task<ModelFingerprint> GetModelFingerprintAsync();
        // The model facts snapshot (M4 contract section 1): settings, structure and DERIVED data facts, cached per
        // session, revision and report basis, and recomputed in the background after a change. Free.
        Task<Facts.ModelFacts> GetModelFactsAsync(string origin = "human");

        // --- The Model Interview (behavioral readiness): a deterministic question bank the engine executes,
        //     compares, and scores — Correct | Refused | SilentlyWrong | Unverified. The user's Claude authors
        //     questions/DAX (the /interview-model skill); the engine never infers (golden rule 1). FREE: list +
        //     one-off run + delete; PRO: add_interview_question (persisting the replayable pack). ---
        Task<InterviewListResult> ListInterviewQuestionsAsync(string scope);
        Task<InterviewQuestion> AddInterviewQuestionAsync(string question, string tier, string query, string scalarExpr, string paraphraseExpr, string[] groupBy, string[] filters, string expectedValue, string expectedMatrixJson, bool expectRefusal, string fixRuleId, string seedSource, string scope, string origin, string id = null, string replacesJson = null);
        Task<InterviewRunResult> RunInterviewAsync(string questionId, string inlineJson, bool abstained, string attemptDax, string origin);
        Task<SetResult> DeleteInterviewQuestionAsync(string id, string origin);
        Task<InterviewSeedResult> ListInterviewSeedsAsync(string source, string measure);   // verified answers + the built-in hard-question pack (free, read-only; no fabricated oracle)

        // --- The Copilot test (Kane, 15 Oct 2026): the interview as a test loop. A bank of questions with proven answers, a small student model in the person's
        //     own assistant, a grade by number for a whole round in one call, the kind of miss named, the misses grouped. The engine never infers (golden rule 1). ---
        Task<CopilotViewResult> GetCopilotViewAsync();
        Task<CopilotAddResult> AddInterviewQuestionsAsync(CopilotQuestionIn[] items, string origin);
        Task<CopilotStudentQuestions> GetStudentQuestionsAsync();
        Task<CopilotBatchResult> RunInterviewBatchAsync(string roundId, string bankKey, string student, CopilotAnswerIn[] answers, CopilotRoundChange[] changes, string origin);
        Task<CopilotRoundsView> InterviewRoundsAsync(string round);

        // --- Change-Plan engine ("analyse → review → fix everything" / incremental edits) ---
        Task<ChangePlanView> ProposePlanAsync(string scope, bool includeAi, int maxAiItems, string origin);
        // Bulk find & replace as a plan (Phase 3): search hits → rename/set_*/set_dax/set_m items; DAX-reference,
        // DAX-code and RLS matches yield NO items (reported in the view's Note). Apply rides apply_plan, now free.
        Task<ChangePlanView> ProposeReplaceAsync(SearchOptions find, string replace, int maxItems, string origin);
        Task<ChangePlanView> GetPlanAsync();
        Task<ChangePlanView> AddPlanItemAsync(string objRef, string kind, string after, string title, string[] verifyGroupBy, string[] verifyFilters, string origin);
        Task<ChangePlanView> SetPlanItemAsync(string itemId, string after, bool? approved, string origin, string expectedAfter = null);
        // overrideIds ship named items past a failed/unprovable equivalence verdict — reason required, recorded.
        Task<ApplyPlanReport> ApplyPlanAsync(string[] approvedIds, string origin, string[] overrideIds = null, string overrideReason = null);
        Task<ChangePlanView> ClearPlanAsync(string origin);

        // --- Documentation (the read-only snapshot the exporter renders + annotation-backed narrative) ---
        Task<DocModelDto> GetDocModelAsync(int topN);
        Task<DocOutline> GetDocOutlineAsync();
        Task<string> GetDocSectionAsync(string objRef, string sectionKey, string expectedIdentity = null);
        Task<SetResult> SetDocSectionAsync(string objRef, string sectionKey, string markdown, string origin, string expectedMarkdown = null, string expectedIdentity = null);

        // --- Model spec (spec-driven authoring: autogenerate -> refine -> build) ---
        Task<SpecView> GetSpecAsync();
        Task<SpecView> SetSpecAsync(string specJson, string origin, long? expectedSpecVersion = null);
        Task<SpecView> ClearSpecAsync(string origin);
        Task<SpecView> SaveSpecAsync(string path, string origin = "human");
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
        Task<ApplyDiffResult> ApplyDiffAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason = null, string confirmToken = null);
        Task<CherryPickResult> CherryPickAsync(ModelRef source, string[] refs, bool includeDependencies, bool commit, string origin);
        Task<TreeNode[]> ListReferenceTreeAsync(ModelRef reference, string origin = "human");
        Task<ConnectionContext> ClearReferenceBindingAsync();
        Task<DeployGate> DeployGateAsync(ModelRef compareTarget, string origin);

        // --- Connections: one registry, doing double duty as the agent-permissions target registry ---
        Task<ModelConnectionRecord[]> ListConnectionsAsync();

        // --- Named SQL sources: the fourth ROLE in Connections, beside Editing, Tests and Publish to ---
        // Saved once and picked by name from a check or a table mapping, so an endpoint is typed once rather
        // than per check. Holds no credential: a source carries a sign-in mode NAME, never a token.
        Task<SqlSourceRecord[]> ListSqlSourcesAsync();
        Task<SqlSourceUsage[]> ListSqlSourceUsageAsync();
        Task<SqlSourceRecord> SaveSqlSourceAsync(string id, string name, string server, string database, string authMode, string tenantId, string origin = "human", string expectedJson = null);
        Task<SqlSourceDeleteResult> DeleteSqlSourceAsync(string id, string origin = "human");
        Task<SqlSourceTestResult> TestSqlSourceAsync(string id, string origin = "human");
        Task<TableSourceMappingInfo> SetTableSourceMappingAsync(string table, string sqlSourceId, string schema, string entity, string origin = "human", string expectedJson = null);
        Task<bool> ClearTableSourceMappingAsync(string table, string origin = "human");
        Task<TableSourceMappingList> ListTableSourceMappingsAsync();
        Task<ConnectionHistoryEvent[]> ListConnectionHistoryAsync(string connectionId = null);
        Task<ConnectionAccountProbe[]> ProbeConnectionAccountsAsync();
        // ADVANCED sign-in prerequisite PREVIEW (read-only): what the engine can actually SEE for a mode (serviceprincipal
        // env vars, or the azcli session) before a user tries to connect. Presence + names only, never a secret value;
        // never an interactive prompt or a network sign-in (local reads + a local `az account show` at most).
        Task<AuthPrerequisites> ProbeAuthPrerequisitesAsync(string mode, string tenantId = null);
        // Multi-account profiles (Phase 2, T160): the saved Microsoft identities on this device (credential-free, both
        // doors). SetDefault changes the tenant default through either door under the local-write policy.
        Task<AccountProfile[]> ListAccountProfilesAsync();
        Task<AccountProfile[]> SetDefaultAccountProfileAsync(string profileId, string origin = "human");
        // Connect another assistant (1.5): the assistants Semanticus can write a connection for, and the one write that connects one.
        Task<ConnectableAssistant[]> ListAssistantsAsync();
        Task<AssistantConnectResult> ConnectAssistantAsync(string assistant, string origin = "human");
        Task<ModelConnectionRecord> RememberXmlaConnectionAsync(string endpoint, string database, string modelName, string authMode, string origin = "agent");
        Task<WorkingCopyResult> PrepareWorkingCopyAsync(string connectionId, string parentFolder, bool commit, string queryConnectionId = null, string publishConnectionId = null, string origin = "agent");
        /// <summary>Link the open model to an XMLA publish destination, or with <paramref name="clear"/> let go of the
        /// one it has (contract 19.2). Clearing with none set is a no-op whose Note says so.</summary>
        Task<ConnectionContext> SetPublishDestinationAsync(string connectionId, string origin = "agent", bool clear = false);
        Task<NewModelResult> PublishNewModelAsync(string workspace, string name, string authMode = null, string tenantId = null, string origin = "agent", string workspaceId = null, bool commit = true);
        /// <summary>Link a model to one saved SQL source as its SQL endpoint, or clear it with a null id (contract
        /// 19.3). The open model by default; a saved connection's model when <paramref name="connectionId"/> is given.</summary>
        Task<ModelSqlEndpointResult> SetModelSqlEndpointAsync(string sqlSourceId, string connectionId = null, string origin = "agent");
        /// <summary>Every model's SQL endpoint, and the saved sources no model uses as its endpoint.</summary>
        Task<ModelSqlEndpointList> ListModelSqlEndpointsAsync();
        /// <summary>Set the first environment label through either door. Only the person can change or clear an existing label.</summary>
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
        Task<FabricWorkspace[]> ListWorkspacesAsync(string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<FabricSemanticModel[]> ListWorkspaceModelsAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<WorkspaceSqlEndpointList> ListWorkspaceSqlEndpointsAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<DeploymentPipeline[]> ListDeploymentPipelinesAsync(string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<PipelineStage[]> GetPipelineStagesAsync(string pipelineId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<StageItem[]> GetStageItemsAsync(string pipelineId, string stageId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);

        // --- Deployment pipeline: preview + the GATED deploy (write lane) + history ---
        Task<DeployPreview> PreviewDeployAsync(string pipelineId, string sourceStageId, string targetStageId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<DeployStageReport> DeployStageAsync(string pipelineId, string sourceStageId, string targetStageId, string[] items, string note, bool commit, string confirmToken, bool forceOverride, string authMode, string tenantId, string origin, string overrideReason = null, CancellationToken cancellationToken = default);
        Task<DeploymentHistoryEntry[]> DeploymentHistoryAsync(string pipelineId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);

        // --- Fabric Git (workspace ⇄ git): reads + GATED writes ---
        Task<FabricGitConnection> FabricGitConnectionAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<FabricGitStatus> FabricGitStatusAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<FabricGitResult> FabricGitCommitAsync(string workspaceId, string comment, string[] items, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<FabricGitResult> FabricGitUpdateAsync(string workspaceId, string conflictPolicy, bool allowOverride, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<FabricGitResult> FabricGitConnectAsync(string workspaceId, string provider, string organization, string project, string repository, string branch, string directory, string connectionId, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<FabricGitResult> FabricGitDisconnectAsync(string workspaceId, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);

        // --- CI/CD: publish the open model's definition (Items API, GATED) + emit a fabric-cicd scaffold (no gate) ---
        Task<CicdPublishResult> CicdPublishAsync(string workspaceId, string itemId, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<CicdScaffold> CicdGenerateAsync(string target, string workspaceId, string environment, bool write, string origin = "human");

        // --- Fabric Data Agent (definition-based item): reads (free) + the model-scope generator (Pro) + dry-run writes ---
        Task<DataAgentList> ListDataAgentsAsync(string workspaceId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<DataAgentDetail> GetDataAgentAsync(string workspaceId, string agentId, string authMode, string tenantId, string origin = "human", CancellationToken cancellationToken = default);
        Task<DataAgentConfig> GenerateDataAgentConfigFromModelAsync(int maxColumnsPerTable);
        Task<DataAgentWriteReport> CreateDataAgentAsync(string workspaceId, string name, string aiInstructions, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<DataAgentWriteReport> UpdateDataAgentAsync(string workspaceId, string agentId, string aiInstructions, string datasourceFolder, string datasourceJson, string fewshotsJson, bool commit, string authMode, string tenantId, string origin, string expectedPart = null, string expectedContent = null, CancellationToken cancellationToken = default);
        Task<DataAgentWriteReport> PublishDataAgentAsync(string workspaceId, string agentId, string description, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);
        Task<DataAgentWriteReport> DeleteDataAgentAsync(string workspaceId, string agentId, bool commit, string authMode, string tenantId, string origin, CancellationToken cancellationToken = default);

        // --- Typed decisions (Jev M0a): the registry, consent, the budget, and the one proof point ---
        // Free on both doors: listing what could be sent, and agreeing or refusing, must never sit behind a
        // paywall. The points themselves are Pro, and a free session is told so in the result rather than
        // refused. Nothing here writes to the model.
        Task<DecisionPointInfo[]> GetDecisionPointsAsync();
        Task<DecisionPointInfo[]> SetDecisionPointEnabledAsync(string name, bool enabled, string origin, string expectedSession = null);
        Task<DecisionConsentInfo> GetConsentAsync();
        Task<DecisionConsentInfo> SetConsentAsync(string[] fields, int schemaVersion, string origin, string expectedScope = null);
        Task<DecisionConsentInfo> WithdrawConsentAsync(string origin);
        Task<DecisionBudgetInfo> GetDecisionBudgetAsync();
        Task<MeasureDefaultsProposal> ProposeMeasureDefaultsAsync(string objRef);

        // --- The living plan (Jev M2, docs/redesign/jev-plan.md section 2a) ---
        // The model is the truth and the plan is a view of it: the gaps are recomputed on every call and are
        // never stored. Every operation here is FREE and returns the whole plan; what Pro buys is the ORDER
        // (the plan.rank point) and, in the box, the routing. Named model_plan* throughout because the
        // change-plan family (propose_plan / apply_plan / get_plan / add_plan_item / set_plan_item /
        // clear_plan) already owns the word "plan" on both doors and means a different thing: one reviewed
        // batch of edits.
        Task<ModelPlanView> GetModelPlanAsync();
        /// <summary>The words row's whole queue, most grade first (Plan/LocalEngine.PlanBestPractice.cs). Read only.</summary>
        Task<WordsQueue> GetWordsQueueAsync(int max = 0, int offset = 0);
        Task<ModelPlanView> SkipModelPlanItemAsync(string id, string reason, string origin);
        Task<ModelPlanView> UnskipModelPlanItemAsync(string id, string origin);
        // Five verbs: start, propose, approve, cancel, and done on a target item. The digest is LAST and
        // optional because EngineRpcTarget binds by position, so a parameter anywhere else would rebind an
        // existing caller's origin onto it.
        Task<ModelPlanActionResult> ModelPlanActionAsync(string id, string action, string origin, string digest = null);
        // One-step bulk apply (groundwork contract section 3): every kept line of every listed item, each
        // quoted with the digest the person read, in ONE batch and one undo step, or nothing at all. With no
        // items and the plan's readyDigest it is Run the plan: every Ready Semanticus row, fenced on that digest
        // (Plan-heart contract 7.2). readyDigest is LAST and optional for the positional-binding reason above.
        Task<ApplyPlanItemsResult> ApplyPlanItemsAsync(PlanItemDigest[] items, string origin, string readyDigest = null);

        // The target (the Fix milestone): what this model should become. FREE on every tier, because a
        // target is the person's own intent and not work the product did for them. MarkPlanTargetDoneAsync
        // is what model_plan_action(id, "done") lands on once the verb switch has recognised a target item;
        // it is on the interface in its own right so the same thing can be asked for directly, and there is
        // only ever ONE implementation behind both ways of asking.
        Task<ModelPlanView> AddPlanTargetAsync(string text, string section, string origin);
        Task<ModelPlanView> RemovePlanTargetAsync(string id, string origin);
        Task<ModelPlanActionResult> MarkPlanTargetDoneAsync(string id, string origin);

        // ---- Plan at the heart: the Plan page's controls (contract sections 3.3, 4 and 12). Kane, 10 Oct 2026: the person's word
        // in chat is enough, so both doors reach them (EngineRpcTarget and the MCP tools); only the Permissions page stays the person's.
        Task<ModelPlanActionResult> AddPlanTaskAsync(PlanTaskRequest request);
        Task<ModelPlanView> HoldPlanItemAsync(string id, bool held);
        Task<ModelPlanView> SetPlanItemDoerAsync(string id, string doer, string origin = "human");
        Task<ModelPlanView> MovePlanTaskUpAsync(string id);
        Task<ModelPlanView> SetAssistantQueueAsync(string state);
        Task<ModelPlanView> SetPlanTaskNoteAsync(string id, string text, string origin = "human");

        // ---- Plan at the heart: repeat cards (lane RP, contract 1.7). Both doors, like the controls above: create,
        // change (edit, pause, resume) and delete. Add and change
        // are Pro (Workflows); delete is free, so a person who lost Pro can still clear a repeat away. Both doors read
        // the repeats and their next due dates on get_model_plan.
        Task<ModelPlanActionResult> AddPlanRepeatAsync(PlanRepeatRequest request);
        Task<ModelPlanView> SetPlanRepeatAsync(string id, PlanRepeatRequest request);
        Task<ModelPlanView> DeletePlanRepeatAsync(string id);

        // ---- Plan at the heart: the queue only delivers (lane E3, contract sections 3 and 6). ONE capability on
        // both doors: the assistant claims, answers and declines with the four queue ops; the person does the same
        // with I'll take it, Mark done and Could not on the human-only target. `reader` names who is asking: null is
        // the MCP door in this process; the pipe target passes its connection id, never a value off the wire.
        Task<Jev.AssistantRequestList> ListAssistantRequestsAsync(string reader = null);
        Task<Jev.HandoffResult> ClaimAssistantRequestAsync(string requestId, string reader = null);
        Task<Jev.HandoffResult> CompleteAssistantRequestAsync(string requestId, string claimToken, string text, string reader = null);
        /// <summary><paramref name="choices"/>: for a composer draft's part, 2 to 6 object refs for the person to pick
        /// from; only the ones that resolve are kept (composer contract 5.4).</summary>
        Task<Jev.HandoffResult> DeclineAssistantRequestAsync(string requestId, string claimToken, string reason, string reader = null, string[] choices = null);

        // ---- the composer's drafts (lane L4; composer contract section 6, Plan-heart contract 14). Pro, Workflows
        // group. compose_task, get_draft, edit_draft and run_draft on both doors; the held approval is the person's
        // own, on the human-only target, like approveAgentAction.
        /// <param name="document">Run once: an unsaved format-3 workflow's text, drafted as it stands in Author.</param>
        Task<Composer.DraftView> ComposeTaskAsync(string words, string workflow, string inputsJson, string stepsJson, string title, string origin, string document = null);
        Task<Composer.DraftView> GetDraftAsync(string draftId);
        Task<Composer.DraftView> EditDraftAsync(string draftId, string action, string step, string blank, string question, string text, string origin);
        Task<Composer.DraftView> RunDraftAsync(string draftId, string origin);
        Task<Composer.DraftView> ApproveHeldDraftAsync(string draftId, string origin = "human");
        /// <summary>Continue a draft's run past the step it waits at (a person's stop, or an assistant's step the person
        /// passes without its changes). Kane, 10 Oct 2026: the assistant may continue too (continue_draft); the person's word in chat is enough.</summary>
        Task<Composer.DraftView> ContinueDraftAsync(string draftId);
        /// <summary>The Plan fold line for this reader, or null when its offer has not changed (contract 6.1). The
        /// MCP success filter's data path, like PullForeignChangesAsync, and not an MCP tool.</summary>
        Task<string> PullPlanOfferAsync(string reader = null);
        // The person's side of the same queue. PERSON-ONLY on the pipe: HumanGovernanceRpcTarget.
        Task<ModelPlanView> ClaimPlanTaskAsync(string id);
        Task<ModelPlanView> CompletePlanTaskAsync(string id, string reply);
        Task<ModelPlanView> DeclinePlanTaskAsync(string id, string reason);
        // Remove a task or draft row, and put it back (Kane's UAT, 25 Sep). Both doors since 10 Oct 2026; Undo is the safety net.
        Task<PlanRemoveResult> RemovePlanRowAsync(string id);
        Task<ModelPlanView> RestorePlanRowAsync(string id);
        // Refresh plan (Kane, 11 Oct 2026): scan again now, add what is new, take the finished rows off the list. Both doors.
        Task<PlanRefreshResult> RefreshPlanAsync();
        /// <summary>describe_workflow_steps: a description cut into steps for Author, each matched to the step Semanticus
        /// knows or left as a named placeholder. Stores nothing and puts nothing on the Plan. Pro (Workflows).</summary>
        Task<Composer.DescribedWorkflow> DescribeWorkflowStepsAsync(string words);

        /// <summary>ask_data: one question about the model's numbers, answered with one fixed query shape. Parts given are
        /// taken as said; words are read by Jev on Pro once its replay clears the bar. Filter values are matched on this
        /// computer. Returns the reading line, the DAX, the checks and the rows. Never writes to the model.</summary>
        Task<Query.DataAnswer> AskDataAsync(Query.DataAsk ask, string origin = "human");

        /// <summary>list_data_questions: the open model's question history, newest first. Never the rows.</summary>
        Task<Query.DataQuestionHistory> ListDataQuestionsAsync(int limit = 50);

        /// <summary>query_notebook: the Query notebook and the model's full history of queries, newest first, or one action on a row:
        /// pin, unpin, close (leave the notebook, stay in the history), reopen (back into the notebook) or remove (delete). Not Pro.</summary>
        Task<Query.QueryNotebookView> QueryNotebookAsync(string action = "list", string id = null);
        /// <summary>A query run as written is a row of the notebook too. Returns its id, or null when the model keeps no history.</summary>
        Task<string> RecordQueryRunAsync(string dax, int rowCount, double elapsedMs, string headline = null, string origin = "human", string replaces = null);
        /// <summary>The page notes what it knows of a row: its number as shown, the line for a part that could not apply, the times.</summary>
        Task<bool> NoteQueryRowAsync(string id, QueryRowNote note);

        /// <summary>pick_chart_kind: Jev's chart pick for a Queries result (queries.chart_kind), among the kinds the chart
        /// rules leave. OFF until its replay clears the bar: until then, and whenever Jev does not pick, it returns the
        /// rules' pick by "rules" with a note. Sends the words and the result's shape, never rows. Pro (Ask your data).
        /// Never writes to the model.</summary>
        Task<Query.ChartPick> PickChartKindAsync(Query.ChartPickAsk ask);

        /// <summary>pick_check_visuals: Jev's visual pick for every check on the Validation page (checks.visual_kind), in ONE call,
        /// among the visuals the page's rules leave for each. OFF until its replay clears the bar: until then, and whenever Jev does
        /// not pick, it returns the rules' pick for every check by "rules" with a note. Sends check names, measure names, status words
        /// and counts, never a row or a value. Pro (Ask your data). Never writes to the model.</summary>
        Task<CheckVisualPicks> PickCheckVisualsAsync(CheckVisualAsk ask);

        Task<NextAction[]> GetNextActionsAsync();
        Task<AskRouteResult> RouteAskAsync(string text);

        // ---- the spec-first start (M11, design-spec-first-start-v2.md). A new model starts from a Fabric SQL source:
        // the schema and a sample are read, everything is decided on a hidden scratch model, the person approves ONE
        // plan, and one build step makes the model. These are the SAME nine members as IStartPlanEngine (the contract
        // in StartPlan/StartPlanContract.cs), declared here and not inherited: reflection on an interface does not
        // list the members it inherits, so FeatureMap's completeness walk and the gate would never have seen them.
        // StartPlanEngineSurfaceTests holds the two lists equal. Every verb is FREE in FeatureMap; the Pro line (Jev
        // deciding) is drawn inside the engine, so a free session still gets the draft, its own assistant's words and
        // one build. ApproveStartPlanAsync is on both doors (Kane, 10 Oct 2026); the approval is void the moment the plan changes.
        Task<StartPlanView> StartModelFromSqlAsync(StartModelRequest request, string origin);
        Task<StartPlanView> GetStartPlanAsync();
        Task<StartPlanView> EditStartPlanAsync(StartPlanEdit edit, string origin);
        Task<StartPlanView> GrantStartDisclosureAsync(string digest, string origin);
        Task<StartPlanView> ApproveStartPlanAsync(string digest, string origin = "human");
        Task<StartWordsPage> GetStartWordsAsync(int max, int offset);
        Task<StartWordsResult> WriteStartWordsAsync(StartWord[] words, string origin);
        Task<StartPlanBuildResult> BuildStartPlanAsync(string digest, string origin);
        Task<StartPlanView> DiscardStartPlanAsync(string origin);

        // ---- the Studio wire (1.4, Claude drives; contract sections 1, 4, 5 and 6). None of it changes the model, its files or the
        // settings. SetStudioDriverAsync is PERSON-ONLY: HumanGovernanceRpcTarget, no MCP tool.
        /// <summary>show_in_studio: say something to the person and name the place (and object, and control) it is about. The Studio
        /// decides whether its screen moves. Refused with a sentence for an unknown place or press, or an empty or long say.</summary>
        Task<StudioShowResult> ShowInStudioAsync(StudioShowRequest request);
        /// <summary>suggest_next: 2 or 3 next steps the Studio shows as buttons. A new one replaces the last.</summary>
        Task<StudioSuggestResult> SuggestNextAsync(StudioSuggestRequest request);
        /// <summary>The session's notes (100 at most, newest last) and current suggestion. The Studio reads it on mount.</summary>
        Task<StudioNotesView> GetStudioNotesAsync(string sessionId);
        /// <summary>The Next line (1.5): the one suggested next step and the journey stage. Never waits for Jev.</summary>
        Task<NextStepView> GetNextStepAsync();
        /// <summary>The person's own suggestion preference: hide / unhide (arg = step id, this session only), stopKind / resumeKind
        /// (arg = kind, kept), turnOff / turnOn (kept). Changes nothing in the model.</summary>
        Task<NextStepView> SetNextStepAsync(string action, string arg);
        /// <summary>The Studio reports its own screen: the mode (claudeDrives or iDrive) and the state (idle, claudeDriving or personHasWheel).</summary>
        Task SetStudioDriverAsync(string mode, string state);
        /// <summary>An assistant tool call began. The MCP success filter calls it first thing, for every tool, owner or attached.</summary>
        /// <summary>When the tool writes or shows something the Studio has a place for, the engine also narrates it (follow). <paramref name="focus"/> is the object ref in the call's arguments, if any.</summary>
        Task NoteAgentCallAsync(string tool, string focus = null, string client = null, string clientVersion = null, bool? commit = null);
    }
}
