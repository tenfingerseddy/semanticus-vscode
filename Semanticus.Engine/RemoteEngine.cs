using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using StreamJsonRpc;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// <see cref="IEngine"/> implemented as a proxy over the named pipe to a running owner engine.
    /// This is what lets the MCP door (Claude's process) attach to the SAME live session the VS Code
    /// UI owns: the MCP tools call IEngine, which here forwards every operation to the owner. The
    /// owner's ChangeBus then broadcasts the agent's edit to the UI client → live cross-process dual-drive.
    /// </summary>
    public sealed class RemoteEngine : IEngine, System.IDisposable
    {
        private readonly ResilientRpc _rpc;

        private RemoteEngine(ResilientRpc rpc) { _rpc = rpc; }

        /// <summary>Attach to the owner engine's pipe. Pass <paramref name="workspace"/> to make the proxy
        /// RESTART-RESILIENT: when the owner dies (the UI has an engine-restart button, so this is routine),
        /// the next call re-reads .semanticus/engine.json, re-attaches to the new owner, and retries once —
        /// instead of every subsequent tool call failing until the whole MCP server is reconnected.
        /// Without a workspace (tests/smokes on a fixed pipe) a lost connection stays fatal.</summary>
        public static async Task<RemoteEngine> ConnectAsync(string pipeName, string workspace = null, int timeoutMs = 5000)
        {
            var (rpc, pipe) = await DialAsync(pipeName, timeoutMs);
            return new RemoteEngine(new ResilientRpc(rpc, pipe, workspace, timeoutMs));
        }

        private static async Task<(JsonRpc rpc, Stream pipe)> DialAsync(string pipeName, int timeoutMs)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutMs);
            var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
            rpc.StartListening();
            return (rpc, pipe);
        }

        /// <summary>The single RPC chokepoint every proxy method funnels through (same InvokeAsync surface as
        /// JsonRpc, so the ~230 one-liners above/below stay untouched). On a LOST CONNECTION it re-resolves the
        /// owner from the broker file and retries the call once; a server-side error (RemoteInvocationException)
        /// is NOT a lost connection and propagates untouched.</summary>
        private sealed class ResilientRpc : System.IDisposable
        {
            private readonly string _workspace;        // null → fixed-pipe mode, no re-attach
            private readonly int _timeoutMs;
            private readonly System.Threading.SemaphoreSlim _swap = new(1, 1);
            private JsonRpc _rpc;
            private Stream _pipe;

            internal ResilientRpc(JsonRpc rpc, Stream pipe, string workspace, int timeoutMs)
            { _rpc = rpc; _pipe = pipe; _workspace = workspace; _timeoutMs = timeoutMs; }

            public async Task<T> InvokeAsync<T>(string method, params object[] args)
            {
                var current = _rpc;
                try { return await current.InvokeAsync<T>(method, args); }
                catch (System.Exception ex) when (IsConnectionLost(ex))
                {
                    await ReattachAsync(current, ex);
                    try { return await _rpc.InvokeAsync<T>(method, args); }
                    catch (RemoteInvocationException rex) { throw AfterRestart(rex); }
                }
            }

            public async Task InvokeAsync(string method, params object[] args)
            {
                var current = _rpc;
                try { await current.InvokeAsync(method, args); }
                catch (System.Exception ex) when (IsConnectionLost(ex))
                {
                    await ReattachAsync(current, ex);
                    try { await _rpc.InvokeAsync(method, args); }
                    catch (RemoteInvocationException rex) { throw AfterRestart(rex); }
                }
            }

            private static bool IsConnectionLost(System.Exception ex)
                => ex is ConnectionLostException || ex is System.ObjectDisposedException || ex is IOException;

            /// <summary>The retried call ran on a FRESH engine — a failure there usually means "no open model".
            /// Say so, instead of letting a bare session error imply the agent did something wrong.</summary>
            private static System.Exception AfterRestart(RemoteInvocationException rex)
                => new System.InvalidOperationException(
                    "The owner engine RESTARTED mid-session (re-attached automatically, but its previous in-memory session is gone). "
                    + "The retried call then failed: " + rex.Message
                    + " If that is a no-open-model error, re-open the model (open_model / open_live) and re-apply unsaved work.", rex);

            private async Task ReattachAsync(JsonRpc lostRpc, System.Exception cause)
            {
                await _swap.WaitAsync();
                try
                {
                    if (!ReferenceEquals(_rpc, lostRpc)) return;   // another call already re-attached
                    var info = _workspace != null ? EngineBroker.ReadInfo(_workspace) : null;
                    if (info == null || !EngineBroker.IsAlive(info))
                        throw new System.InvalidOperationException(
                            "The connection to the owner engine was lost and no running engine was found to re-attach to"
                            + (_workspace != null ? $" (workspace: {_workspace})" : " (fixed-pipe attach; re-attach needs a workspace)")
                            + ". Restart the engine (VS Code starts it on activation; there is also a status-bar restart button), then retry.", cause);
                    var (rpc, pipe) = await DialAsync(info.PipeName, _timeoutMs);
                    var oldRpc = _rpc; var oldPipe = _pipe;
                    _rpc = rpc; _pipe = pipe;
                    try { oldRpc.Dispose(); } catch { }
                    try { oldPipe.Dispose(); } catch { }
                }
                finally { _swap.Release(); }
            }

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
                _swap.Dispose();
            }
        }

        public Task<OpenResult> OpenAsync(string path) => _rpc.InvokeAsync<OpenResult>("open", path);
        public Task<OpenResult> CreateModelAsync(string name, int compatibilityLevel) => _rpc.InvokeAsync<OpenResult>("createModel", name, compatibilityLevel);
        public Task<OpenResult> OpenLocalAsync(string dataSource, string database) => _rpc.InvokeAsync<OpenResult>("openLocal", dataSource, database);
        public Task<OpenResult> OpenLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool forceReauth = false) => _rpc.InvokeAsync<OpenResult>("openLive", endpoint, database, authMode, rawToken, tenantId, forceReauth);
        public Task<DeployReport> DeployLiveAsync(string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human", string overrideReason = null) => _rpc.InvokeAsync<DeployReport>("deployLive", endpoint, database, authMode, rawToken, tenantId, commit, origin, overrideReason);
        public Task<RefreshTypeInfo[]> ListRefreshTypesAsync() => _rpc.InvokeAsync<RefreshTypeInfo[]>("listRefreshTypes");
        public Task<RefreshReport> RefreshPartitionAsync(string partitionRef, string refreshType, string endpoint, string database, string authMode, string rawToken, string tenantId, bool commit, string origin = "human") => _rpc.InvokeAsync<RefreshReport>("refreshPartition", partitionRef, refreshType, endpoint, database, authMode, rawToken, tenantId, commit, origin);
        public Task<TreeNode[]> ListTreeAsync(string parentRef) => _rpc.InvokeAsync<TreeNode[]>("listTree", parentRef);
        public Task<ObjectInfo> GetObjectAsync(string objRef) => _rpc.InvokeAsync<ObjectInfo>("getObject", objRef);
        public Task<string> GetDaxAsync(string objRef) => _rpc.InvokeAsync<string>("getDax", objRef);
        public Task<SetResult> SetDaxAsync(string objRef, string expression, string origin) => _rpc.InvokeAsync<SetResult>("setDax", objRef, expression, origin);
        public Task<SaveResult> SaveAsync(string path, string format) => _rpc.InvokeAsync<SaveResult>("save", path, format);
        public Task<SessionInfo> SessionInfoAsync() => _rpc.InvokeAsync<SessionInfo>("sessionInfo");
        public Task<ConnectionContext> ConnectionContextAsync() => _rpc.InvokeAsync<ConnectionContext>("connectionContext");
        public Task<ModelConnectionRecord> RememberXmlaConnectionAsync(string endpoint, string database, string modelName, string authMode, string origin = "human") => _rpc.InvokeAsync<ModelConnectionRecord>("rememberXmlaConnection", endpoint, database, modelName, authMode, origin);
        public Task<WorkingCopyResult> PrepareWorkingCopyAsync(string connectionId, string parentFolder, bool commit, string queryConnectionId = null, string publishConnectionId = null, string origin = "human") => _rpc.InvokeAsync<WorkingCopyResult>("prepareWorkingCopy", connectionId, parentFolder, commit, queryConnectionId, publishConnectionId, origin);
        public Task<ConnectionContext> SetPublishDestinationAsync(string connectionId, string origin = "human") => _rpc.InvokeAsync<ConnectionContext>("setPublishDestination", connectionId, origin);
        public Task<Entitlement.EntitlementInfo> GetEntitlementAsync() => _rpc.InvokeAsync<Entitlement.EntitlementInfo>("getEntitlement");
        public Task<VerifiedModeState> GetVerifiedModeAsync() => _rpc.InvokeAsync<VerifiedModeState>("getVerifiedMode");
        public Task<VerifiedModeState> SetVerifiedModeAsync(bool on, string origin) => _rpc.InvokeAsync<VerifiedModeState>("setVerifiedMode", on, origin);
        public Task<ModelGraph> GetModelGraphAsync() => _rpc.InvokeAsync<ModelGraph>("getModelGraph");
        public Task<ModelSummary> GetOrientationAsync() => _rpc.InvokeAsync<ModelSummary>("getOrientation");
        public Task<HarnessReportResult> HarnessReportAsync(int topN) => _rpc.InvokeAsync<HarnessReportResult>("harnessReport", topN);
        public Task<LayoutData> GetLayoutAsync() => _rpc.InvokeAsync<LayoutData>("getLayout");
        public Task<SaveLayoutResult> SaveLayoutAsync(LayoutNode[] tables, string origin) => _rpc.InvokeAsync<SaveLayoutResult>("saveLayout", tables, origin);
        public Task<VpaxExportResult> ExportVpaxAsync(string path) => _rpc.InvokeAsync<VpaxExportResult>("exportVpax", path);
        public Task<SearchResult> SearchModelAsync(string query, int max) => _rpc.InvokeAsync<SearchResult>("searchModel", query, max);
        public Task<SearchResult> SearchModelAsync(SearchOptions opts) => _rpc.InvokeAsync<SearchResult>("searchModelEx", opts);
        public Task<ReplaceResult> ReplaceInObjectAsync(ReplaceRequest req, string origin) => _rpc.InvokeAsync<ReplaceResult>("replaceInObject", req, origin);
        public Task<MeasureRow[]> ListMeasuresAsync() => _rpc.InvokeAsync<MeasureRow[]>("listMeasures");
        public Task<ColumnRow[]> ListColumnsAsync() => _rpc.InvokeAsync<ColumnRow[]>("listColumns");
        public Task<ModelObjects> GetModelObjectsAsync() => _rpc.InvokeAsync<ModelObjects>("getModelObjects");
        public Task<UndoState> UndoAsync(string origin) => _rpc.InvokeAsync<UndoState>("undo", origin);
        public Task<UndoState> RedoAsync(string origin) => _rpc.InvokeAsync<UndoState>("redo", origin);

        public Task<BpaScorecard> BpaScanAsync() => _rpc.InvokeAsync<BpaScorecard>("bpaScan");
        public Task<SetResult> BpaFixAsync(string ruleId, string objRef, string origin) => _rpc.InvokeAsync<SetResult>("bpaFix", ruleId, objRef, origin);
        public Task<BpaFixAllResult> BpaFixAllAsync(string origin) => _rpc.InvokeAsync<BpaFixAllResult>("bpaFixAll", origin);
        public Task<BpaRulesInfo> LoadBpaRulesAsync(string source, bool replace, string origin) => _rpc.InvokeAsync<BpaRulesInfo>("loadBpaRules", source, replace, origin);
        public Task<BpaRulesInfo> ResetBpaRulesAsync(string origin) => _rpc.InvokeAsync<BpaRulesInfo>("resetBpaRules", origin);
        public Task<FixPrompt> BpaGetFixPromptAsync(string ruleId, string objRef) => _rpc.InvokeAsync<FixPrompt>("bpaGetFixPrompt", ruleId, objRef);
        public Task<ReadinessRulesInfo> LoadReadinessRulesAsync(string source, bool replace, string origin) => _rpc.InvokeAsync<ReadinessRulesInfo>("loadReadinessRules", source, replace, origin);
        public Task<ReadinessRulesInfo> ResetReadinessRulesAsync(string origin) => _rpc.InvokeAsync<ReadinessRulesInfo>("resetReadinessRules", origin);
        public Task<RuleValidationResult> ValidateRuleAsync(string kind, string rules) => _rpc.InvokeAsync<RuleValidationResult>("validateRule", kind, rules);
        public Task<CustomRulesInfo> GetCustomRulesAsync() => _rpc.InvokeAsync<CustomRulesInfo>("getCustomRules");

        public Task<Scorecard> AiReadinessScanAsync() => _rpc.InvokeAsync<Scorecard>("aiReadinessScan");
        public Task<Scorecard> AiReadinessScanLiveAsync() => _rpc.InvokeAsync<Scorecard>("aiReadinessScanLive");
        public Task<SafeFixResult> ApplySafeFixesAsync(string origin) => _rpc.InvokeAsync<SafeFixResult>("applySafeFixes", origin);
        public Task<SetResult> ApplyFixAsync(string ruleId, string objRef, string origin) => _rpc.InvokeAsync<SetResult>("applyFix", ruleId, objRef, origin);
        public Task<FixPrompt> GetFixPromptAsync(string ruleId, string objRef) => _rpc.InvokeAsync<FixPrompt>("getFixPrompt", ruleId, objRef);
        public Task<GroundingBundle> GetGroundingAsync(string objRef) => _rpc.InvokeAsync<GroundingBundle>("getGrounding", objRef);
        public Task<SetResult> WaiveFindingAsync(string system, string ruleId, string objRef, string reason, string origin) => _rpc.InvokeAsync<SetResult>("waiveFinding", system, ruleId, objRef, reason, origin);
        public Task<SetResult> UnwaiveFindingAsync(string system, string ruleId, string objRef, string origin) => _rpc.InvokeAsync<SetResult>("unwaiveFinding", system, ruleId, objRef, origin);
        public Task<Semanticus.Analysis.WaiverRecord[]> ListWaiversAsync() => _rpc.InvokeAsync<Semanticus.Analysis.WaiverRecord[]>("listWaivers");
        public Task<DaxValidation> ValidateDaxAsync(string expression) => _rpc.InvokeAsync<DaxValidation>("validateDax", expression);
        public Task<SetResult> SetDescriptionAsync(string objRef, string text, string origin) => _rpc.InvokeAsync<SetResult>("setDescription", objRef, text, origin);
        public Task<string> CreateMeasureAsync(string tableRef, string name, string expression, string origin, string displayFolder = null) => _rpc.InvokeAsync<string>("createMeasure", tableRef, name, expression, origin, displayFolder);
        public Task<TreeNode[]> ListFunctionsAsync() => _rpc.InvokeAsync<TreeNode[]>("listFunctions");
        public Task<string> CreateFunctionAsync(string name, string expression, string origin) => _rpc.InvokeAsync<string>("createFunction", name, expression, origin);
        public Task<string> CreateTableAsync(string name, string origin) => _rpc.InvokeAsync<string>("createTable", name, origin);
        public Task<string> CreateColumnAsync(string tableRef, string name, string dataType, string sourceColumn, string origin) => _rpc.InvokeAsync<string>("createColumn", tableRef, name, dataType, sourceColumn, origin);
        public Task<string> CreateDataSourceAsync(string name, string server, string database, string origin) => _rpc.InvokeAsync<string>("createDataSource", name, server, database, origin);
        public Task<string> CreateNamedExpressionAsync(string name, string expression, string origin) => _rpc.InvokeAsync<string>("createNamedExpression", name, expression, origin);
        public Task<PartitionInfo[]> ListPartitionsAsync(string tableRef) => _rpc.InvokeAsync<PartitionInfo[]>("listPartitions", tableRef);
        public Task<string> GetPartitionMAsync(string partitionRef) => _rpc.InvokeAsync<string>("getPartitionM", partitionRef);
        public Task<SetResult> SetPartitionMAsync(string partitionRef, string mExpression, string origin) => _rpc.InvokeAsync<SetResult>("setPartitionM", partitionRef, mExpression, origin);
        public Task<DocExpression[]> ListNamedExpressionsAsync() => _rpc.InvokeAsync<DocExpression[]>("listNamedExpressions");
        public Task<string> GetNamedExpressionAsync(string name) => _rpc.InvokeAsync<string>("getNamedExpression", name);
        public Task<SetResult> UpdateNamedExpressionAsync(string name, string expression, string origin) => _rpc.InvokeAsync<SetResult>("updateNamedExpression", name, expression, origin);
        public Task<string> CreateImportTableAsync(string name, string mExpression, string origin) => _rpc.InvokeAsync<string>("createImportTable", name, mExpression, origin);
        public Task<string> CreateDirectLakeTableAsync(string name, string entityName, string schemaName, string sourceName, string origin) => _rpc.InvokeAsync<string>("createDirectLakeTable", name, entityName, schemaName, sourceName, origin);
        public Task<string> CreateCalculatedTableAsync(string name, string expression, string origin) => _rpc.InvokeAsync<string>("createCalculatedTable", name, expression, origin);
        public Task<string> CreateFieldParameterAsync(string name, FieldParameterItem[] items, string origin) => _rpc.InvokeAsync<string>("createFieldParameter", name, items, origin);
        public Task<SetResult> SetColumnDataTypeAsync(string columnRef, string dataType, string origin) => _rpc.InvokeAsync<SetResult>("setColumnDataType", columnRef, dataType, origin);
        public Task<SourceSchema> GetSourceSchemaAsync(string tableRef, string authMode, string tenantId) => _rpc.InvokeAsync<SourceSchema>("getSourceSchema", tableRef, authMode, tenantId);
        public Task<SchemaDiff> DiffSchemaAsync(string tableRef, SourceColumn[] sourceColumns, string authMode, string tenantId) => _rpc.InvokeAsync<SchemaDiff>("diffSchema", tableRef, sourceColumns, authMode, tenantId);
        public Task<ApplySchemaResult> ApplySchemaUpdateAsync(string tableRef, SchemaUpdateItem[] items, string origin) => _rpc.InvokeAsync<ApplySchemaResult>("applySchemaUpdate", tableRef, items, origin);
        public Task<string> CreateCalculatedColumnAsync(string tableRef, string name, string expression, string origin) => _rpc.InvokeAsync<string>("createCalculatedColumn", tableRef, name, expression, origin);
        public Task<string> CreateRelationshipAsync(string fromColumnRef, string toColumnRef, string crossFilter, bool? isActive, string origin) => _rpc.InvokeAsync<string>("createRelationship", fromColumnRef, toColumnRef, crossFilter, isActive, origin);
        public Task<string> CreateHierarchyAsync(string tableRef, string name, string[] levelColumns, string origin) => _rpc.InvokeAsync<string>("createHierarchy", tableRef, name, levelColumns, origin);
        public Task<string> CreateCalculationGroupAsync(string name, string origin) => _rpc.InvokeAsync<string>("createCalculationGroup", name, origin);
        public Task<string> CreateCalculationItemAsync(string calcGroupRef, string name, string expression, string origin) => _rpc.InvokeAsync<string>("createCalculationItem", calcGroupRef, name, expression, origin);
        public Task<CalcGroupInfo[]> ListCalculationGroupsAsync() => _rpc.InvokeAsync<CalcGroupInfo[]>("listCalculationGroups");
        public Task<string> CreatePerspectiveAsync(string name, string origin) => _rpc.InvokeAsync<string>("createPerspective", name, origin);
        public Task<SetResult> SetPerspectiveMemberAsync(string perspectiveRef, string objRef, bool include, string origin) => _rpc.InvokeAsync<SetResult>("setPerspectiveMember", perspectiveRef, objRef, include, origin);
        public Task<PerspectiveInfo[]> GetPerspectivesAsync() => _rpc.InvokeAsync<PerspectiveInfo[]>("getPerspectives");
        public Task<SetResult> SetCalcItemFormatStringAsync(string calcItemRef, string formatExpression, string origin) => _rpc.InvokeAsync<SetResult>("setCalcItemFormatString", calcItemRef, formatExpression, origin);
        public Task<FormatTemplateInfo[]> ListFormatTemplatesAsync(string category, string search) => _rpc.InvokeAsync<FormatTemplateInfo[]>("listFormatTemplates", category, search);
        public Task<SetResult> SetMeasureFormatExpressionAsync(string objRef, string formatExpression, string origin) => _rpc.InvokeAsync<SetResult>("setMeasureFormatExpression", objRef, formatExpression, origin);
        public Task<SetResult> SetCalcGroupPrecedenceAsync(string calcGroupRef, int precedence, string origin) => _rpc.InvokeAsync<SetResult>("setCalcGroupPrecedence", calcGroupRef, precedence, origin);
        public Task<DaxLibPackage[]> DaxLibSearchAsync(string text, int skip, int take) => _rpc.InvokeAsync<DaxLibPackage[]>("daxLibSearch", text, skip, take);
        public Task<string[]> DaxLibVersionsAsync(string id) => _rpc.InvokeAsync<string[]>("daxLibVersions", id);
        public Task<DaxLibPackageDetail> DaxLibPackageInfoAsync(string id, string version) => _rpc.InvokeAsync<DaxLibPackageDetail>("daxLibPackageInfo", id, version);
        public Task<DaxLibInstallResult> DaxLibInstallAsync(string id, string version, bool replaceExisting, string origin) => _rpc.InvokeAsync<DaxLibInstallResult>("daxLibInstall", id, version, replaceExisting, origin);
        public Task<DaxLibInstalledRecord[]> DaxLibListInstalledAsync() => _rpc.InvokeAsync<DaxLibInstalledRecord[]>("daxLibListInstalled");
        public Task<SetResult> DaxLibUninstallAsync(string id, string origin) => _rpc.InvokeAsync<SetResult>("daxLibUninstall", id, origin);
        public Task<SetResult> DeleteObjectAsync(string objRef, string origin) => _rpc.InvokeAsync<SetResult>("deleteObject", objRef, origin);
        public Task<string> DuplicateObjectAsync(string objRef, string newName, string targetRef, string origin) => _rpc.InvokeAsync<string>("duplicateObject", objRef, newName, origin, targetRef);   // wire order: targetRef appended after origin (legacy 3-arg compat)
        public Task<ObjectProperty[]> GetObjectPropertiesAsync(string objRef) => _rpc.InvokeAsync<ObjectProperty[]>("getObjectProperties", objRef);
        public Task<SetResult> SetObjectPropertyAsync(string objRef, string propertyName, string value, string origin) => _rpc.InvokeAsync<SetResult>("setObjectProperty", objRef, propertyName, value, origin);
        public Task<SetResult> SetObjectPropertiesAsync(string[] objRefs, string propertyName, string value, string origin) => _rpc.InvokeAsync<SetResult>("setObjectProperties", objRefs, propertyName, value, origin);
        public Task<SetResult> SetDisplayFolderAsync(string[] refs, string folder, string origin) => _rpc.InvokeAsync<SetResult>("setDisplayFolder", refs, folder, origin);
        public Task<FolderRenameResult> RenameDisplayFolderAsync(string tableRef, string fromPath, string toPath, string origin) => _rpc.InvokeAsync<FolderRenameResult>("renameDisplayFolder", tableRef, fromPath, toPath, origin);
        public Task<DependentInfo[]> GetDependentsAsync(string objRef) => _rpc.InvokeAsync<DependentInfo[]>("getDependents", objRef);
        public Task<DependentInfo[]> GetDependenciesAsync(string objRef) => _rpc.InvokeAsync<DependentInfo[]>("getDependencies", objRef);
        public Task<LineageResult> GetLineageAsync() => _rpc.InvokeAsync<LineageResult>("getLineage");
        public Task<ImpactResult> ImpactOfAsync(string objRef) => _rpc.InvokeAsync<ImpactResult>("impactOf", objRef);
        public Task<Lineage.ImpactAssessmentResult> ImpactAssessmentAsync(Lineage.ImpactAssessmentRequest request) => _rpc.InvokeAsync<Lineage.ImpactAssessmentResult>("impactAssessment", request);
        public Task<UnusedResult> UnusedObjectsAsync() => _rpc.InvokeAsync<UnusedResult>("unusedObjects");
        public Task<ReportAnalysisResult> AnalyzeReportsAsync(string[] paths) => _rpc.InvokeAsync<ReportAnalysisResult>("analyzeReports", new object[] { paths });
        public Task<RemoveSafeReport> RemoveSafeObjectsAsync(string[] refs, string[] reportPaths, string origin) => _rpc.InvokeAsync<RemoveSafeReport>("removeSafeObjects", new object[] { refs, reportPaths, origin });
        public Task<CloudReport[]> ListReportsAsync(string workspaceId, string authMode, string tenantId) => _rpc.InvokeAsync<CloudReport[]>("listReports", workspaceId, authMode, tenantId);
        public Task<ReportAnalysisResult> AnalyzeCloudReportsAsync(string workspaceId, string[] reportIds, bool consent, string authMode, string tenantId) => _rpc.InvokeAsync<ReportAnalysisResult>("analyzeCloudReports", new object[] { workspaceId, reportIds, consent, authMode, tenantId });
        public Task<string> ScriptObjectsAsync(string[] refs, string format) => _rpc.InvokeAsync<string>("scriptObjects", refs, format);
        public Task<ApplyScriptResult> ApplyDaxScriptAsync(string script, string origin) => _rpc.InvokeAsync<ApplyScriptResult>("applyDaxScript", script, origin);
        public Task<ApplyScriptResult> ApplyTmdlScriptAsync(string script, string origin) => _rpc.InvokeAsync<ApplyScriptResult>("applyTmdlScript", script, origin);
        public Task<SetResult> SetCompatibilityLevelAsync(int level, string origin) => _rpc.InvokeAsync<SetResult>("setCompatibilityLevel", level, origin);
        public Task<RenameResult> RenameObjectAsync(string objRef, string newName, string origin) => _rpc.InvokeAsync<RenameResult>("renameObject", objRef, newName, origin);
        public Task<SetResult> SetColumnMetadataAsync(string objRef, bool? isHidden, string summarizeBy, string dataCategory, string sortByColumn, string origin) => _rpc.InvokeAsync<SetResult>("setColumnMetadata", objRef, isHidden, summarizeBy, dataCategory, sortByColumn, origin);
        public Task<SetResult> SetMeasureFormatAsync(string objRef, string formatString, string origin) => _rpc.InvokeAsync<SetResult>("setMeasureFormat", objRef, formatString, origin);
        public Task<SetResult> MarkDateTableAsync(string tableRef, string dateColumn, string origin) => _rpc.InvokeAsync<SetResult>("markDateTable", tableRef, dateColumn, origin);
        public Task<RefreshPolicyInfo> GetIncrementalRefreshPolicyAsync(string tableRef) => _rpc.InvokeAsync<RefreshPolicyInfo>("getIncrementalRefreshPolicy", tableRef);
        public Task<SetResult> SetIncrementalRefreshPolicyAsync(string tableRef, string dateColumn, int? rollingWindowPeriods, string rollingWindowGranularity, int? incrementalPeriods, string incrementalGranularity, int? incrementalPeriodsOffset, string mode, string pollingExpression, bool autoWire, string origin) => _rpc.InvokeAsync<SetResult>("setIncrementalRefreshPolicy", tableRef, dateColumn, rollingWindowPeriods, rollingWindowGranularity, incrementalPeriods, incrementalGranularity, incrementalPeriodsOffset, mode, pollingExpression, autoWire, origin);
        public Task<SetResult> RemoveIncrementalRefreshPolicyAsync(string tableRef, string origin) => _rpc.InvokeAsync<SetResult>("removeIncrementalRefreshPolicy", tableRef, origin);
        public Task<SetResult> SetRelationshipAsync(string relationshipName, string crossFilteringBehavior, bool? isActive, string origin) => _rpc.InvokeAsync<SetResult>("setRelationship", relationshipName, crossFilteringBehavior, isActive, origin);
        public Task<SetResult> SetRelationshipCardinalityAsync(string relationshipName, string fromCardinality, string toCardinality, string origin) => _rpc.InvokeAsync<SetResult>("setRelationshipCardinality", relationshipName, fromCardinality, toCardinality, origin);
        public Task<ReadinessPlan> MakeAiReadyAsync(string origin, int maxQueue) => _rpc.InvokeAsync<ReadinessPlan>("makeAiReady", origin, maxQueue);
        public Task<SetResult> EnableQnaAsync(string culture, string origin) => _rpc.InvokeAsync<SetResult>("enableQna", culture, origin);
        public Task<SetResult> SetSynonymsAsync(string objRef, string[] terms, string culture, string origin) => _rpc.InvokeAsync<SetResult>("setSynonyms", objRef, terms, culture, origin);
        public Task<SetInstructionsResult> SetAiInstructionsAsync(string instructions, string culture, string origin) => _rpc.InvokeAsync<SetInstructionsResult>("setAiInstructions", instructions, culture, origin);
        public Task<AiInstructionsInfo> GetAiInstructionsAsync(string culture) => _rpc.InvokeAsync<AiInstructionsInfo>("getAiInstructions", culture);
        public Task<SetResult> SetAiDataSchemaAsync(string objRef, bool included, string culture, string origin) => _rpc.InvokeAsync<SetResult>("setAiDataSchema", objRef, included, culture, origin);
        public Task<RoleInfo[]> ListRolesAsync() => _rpc.InvokeAsync<RoleInfo[]>("listRoles");
        public Task<RoleInfo> CreateRoleAsync(string name, string modelPermission, string origin) => _rpc.InvokeAsync<RoleInfo>("createRole", name, modelPermission, origin);
        public Task<SetResult> DeleteRoleAsync(string name, string origin) => _rpc.InvokeAsync<SetResult>("deleteRole", name, origin);
        public Task<SetResult> SetRolePermissionAsync(string name, string modelPermission, string origin) => _rpc.InvokeAsync<SetResult>("setRolePermission", name, modelPermission, origin);
        public Task<SetTablePermissionResult> SetTablePermissionAsync(string roleName, string tableRef, string filterDax, string origin) => _rpc.InvokeAsync<SetTablePermissionResult>("setTablePermission", roleName, tableRef, filterDax, origin);
        public Task<SetResult> SetRoleMemberAsync(string roleName, string memberName, bool add, string origin) => _rpc.InvokeAsync<SetResult>("setRoleMember", roleName, memberName, add, origin);
        public Task<SetResult> SetTableObjectPermissionAsync(string roleName, string tableRef, string permission, string origin) => _rpc.InvokeAsync<SetResult>("setTableObjectPermission", roleName, tableRef, permission, origin);
        public Task<SetResult> SetColumnObjectPermissionAsync(string roleName, string columnRef, string permission, string origin) => _rpc.InvokeAsync<SetResult>("setColumnObjectPermission", roleName, columnRef, permission, origin);
        public Task<GenerateResult> GenerateDateTableAsync(string tableName, string startExpr, string endExpr, bool markAsDate, string origin) => _rpc.InvokeAsync<GenerateResult>("generateDateTable", tableName, startExpr, endExpr, markAsDate, origin);
        public Task<GenerateResult> GenerateTimeIntelligenceAsync(string baseMeasureRef, string dateColumn, string[] variants, string displayFolder, string origin) => _rpc.InvokeAsync<GenerateResult>("generateTimeIntelligence", baseMeasureRef, dateColumn, variants, displayFolder, origin);
        public Task<CalendarListResult> ListCalendarsAsync(string tableRef) => _rpc.InvokeAsync<CalendarListResult>("listCalendars", tableRef);
        public Task<CalendarResult> DefineCalendarAsync(string tableRef, string name, CalendarMappingSpec[] mappings, string description, string origin) => _rpc.InvokeAsync<CalendarResult>("defineCalendar", tableRef, name, mappings, description, origin);
        public Task<SetResult> DeleteCalendarAsync(string tableRef, string name, string origin) => _rpc.InvokeAsync<SetResult>("deleteCalendar", tableRef, name, origin);
        public Task<CalendarResult> TagCalendarColumnAsync(string tableRef, string calendarName, string column, string timeUnit, bool associated, bool remove, string origin) => _rpc.InvokeAsync<CalendarResult>("tagCalendarColumn", tableRef, calendarName, column, timeUnit, associated, remove, origin);
        public Task<CalendarResult> DefineCalendarFromTemplateAsync(string template, string tableName, string dateColumn, int fiscalStartMonth, string startExpr, string endExpr, string calendarName, string origin) => _rpc.InvokeAsync<CalendarResult>("defineCalendarFromTemplate", template, tableName, dateColumn, fiscalStartMonth, startExpr, endExpr, calendarName, origin);
        public Task<ConnectionStatus> ConnectXmlaAsync(string endpoint, string database, string authMode, string rawToken) => _rpc.InvokeAsync<ConnectionStatus>("connectXmla", endpoint, database, authMode, rawToken);
        public Task<ConnectionStatus> ConnectLocalAsync(string dataSource, string database) => _rpc.InvokeAsync<ConnectionStatus>("connectLocal", dataSource, database);
        public Task<LocalInstance[]> ListLocalInstancesAsync() => _rpc.InvokeAsync<LocalInstance[]>("listLocalInstances");
        public Task<ConnectionStatus> ConnectionStatusAsync() => _rpc.InvokeAsync<ConnectionStatus>("connectionStatus");
        public Task<ConnectionStatus> DisconnectAsync() => _rpc.InvokeAsync<ConnectionStatus>("disconnect");
        public Task<ResultSet> RunDaxAsync(string query, int maxRows, string origin = "human") => _rpc.InvokeAsync<ResultSet>("runDax", query, maxRows, origin);
        public Task<ResultSet> RunDmvAsync(string query, int maxRows) => _rpc.InvokeAsync<ResultSet>("runDmv", query, maxRows);
        public Task<ResultSet> PreviewTableAsync(string table, int topN, string origin = "human") => _rpc.InvokeAsync<ResultSet>("previewTable", table, topN, origin);
        public Task<ResultSet> PivotMeasureAsync(string measureExpr, string[] rowFields, string colField, string[] filters, int maxRows, string origin = "human") => _rpc.InvokeAsync<ResultSet>("pivotMeasure", measureExpr, rowFields, colField, filters, maxRows, origin);
        public Task<ReconcileRunResult> ReconcileMeasureAsync(ReconcileRequest request, string origin = "human") => _rpc.InvokeAsync<ReconcileRunResult>("reconcileMeasure", request, origin);
        public Task<ReconcileMappingReview> ReviewReconcileMappingAsync(ReconcileMappingRequest request, string origin = "human") => _rpc.InvokeAsync<ReconcileMappingReview>("reviewReconcileMapping", request, origin);
        public Task<VpaqReport> VertiPaqScanAsync(int topN) => _rpc.InvokeAsync<VpaqReport>("vertiPaqScan", topN);
        public Task<BenchmarkResult> BenchmarkDaxAsync(string query, int runs) => _rpc.InvokeAsync<BenchmarkResult>("benchmarkDax", query, runs);
        public Task<ServerTimings> ProfileDaxAsync(string query) => _rpc.InvokeAsync<ServerTimings>("profileDax", query);
        public Task<EvalLogResult> EvaluateAndLogAsync(string query, int maxRows, string origin = "human") => _rpc.InvokeAsync<EvalLogResult>("evaluateAndLog", query, maxRows, origin);
        public Task<EquivalenceResult> VerifyEquivalenceAsync(string exprA, string exprB, string[] groupBy, string[] filters, int maxRows) => _rpc.InvokeAsync<EquivalenceResult>("verifyEquivalence", exprA, exprB, groupBy, filters, maxRows);
        public Task<ClearCacheResult> ClearCacheAsync(bool confirm) => _rpc.InvokeAsync<ClearCacheResult>("clearCache", confirm);
        public Task<ColdWarmBenchmark> BenchmarkColdWarmAsync(string query, int runs, bool clearForCold, bool confirm) => _rpc.InvokeAsync<ColdWarmBenchmark>("benchmarkColdWarm", query, runs, clearForCold, confirm);
        public Task<QueryPlanResult> CaptureQueryPlanAsync(string query) => _rpc.InvokeAsync<QueryPlanResult>("captureQueryPlan", query);
        public Task<Semanticus.Analysis.DaxLintResult> LintDaxAsync(string expression) => _rpc.InvokeAsync<Semanticus.Analysis.DaxLintResult>("lintDax", expression);
        public Task<OptimizeMeasureResult> OptimizeMeasureAsync(string measureRef, string[] candidates, string[] verifyGroupBy, string[] verifyFilters, bool apply, string origin) => _rpc.InvokeAsync<OptimizeMeasureResult>("optimizeMeasure", measureRef, candidates, verifyGroupBy, verifyFilters, apply, origin);
        public Task<ProbeResult> ProbeMeasureAsync(string expr, string primaryAxis, ProbeScenario[] scenarios, bool includeDefaults, int rowCap) => _rpc.InvokeAsync<ProbeResult>("probeMeasure", expr, primaryAxis, scenarios, includeDefaults, rowCap);
        public Task<BaselineCaptureResult> CaptureBaselineAsync(string objRef, string[] groupBy, string[] filters, bool includeDependents, int maxMeasures, int rowCap, string label, string origin) => _rpc.InvokeAsync<BaselineCaptureResult>("captureBaseline", objRef, groupBy, filters, includeDependents, maxMeasures, rowCap, label, origin);
        public Task<BaselineCompareResult> CompareBaselineAsync(string captureId, string label, string origin) => _rpc.InvokeAsync<BaselineCompareResult>("compareBaseline", captureId, label, origin);
        public Task<BlameResult> BlameValueAsync(string measureRef, string context, string sinceCheckpoint, string origin) => _rpc.InvokeAsync<BlameResult>("blameValue", measureRef, context, sinceCheckpoint, origin);
        public Task<ValueHistoryResult> ListValueHistoryAsync(string measureRef, string context) => _rpc.InvokeAsync<ValueHistoryResult>("listValueHistory", measureRef, context);
        public Task<ExplainDossier> ExplainValueAsync(string measureRef, ExplainFilterContext context, bool decompose, string decomposeBy, int topK, string origin) => _rpc.InvokeAsync<ExplainDossier>("explainValue", measureRef, context, decompose, decomposeBy, topK, origin);
        public Task<VerifiedEditsChain> ListVerifiedEditsAsync() => _rpc.InvokeAsync<VerifiedEditsChain>("listVerifiedEdits");
        public Task<string> ExportVerifiedEditsAsync(string format) => _rpc.InvokeAsync<string>("exportVerifiedEdits", format);
        public Task<TestSuiteRunResult> RunTestSuiteAsync(bool persist, string origin) => _rpc.InvokeAsync<TestSuiteRunResult>("runTests", persist, origin);
        public Task<TestSuiteInfo> ListTestDefinitionsAsync() => _rpc.InvokeAsync<TestSuiteInfo>("listTests");
        public Task<TestDefinition> SaveTestDefinitionAsync(TestDefinition def, string origin) => _rpc.InvokeAsync<TestDefinition>("saveTest", def, origin);
        public Task<bool> DeleteTestDefinitionAsync(string id, string origin) => _rpc.InvokeAsync<bool>("deleteTest", id, origin);
        public Task<TestHistoryInfo> ListTestRunsAsync(int last) => _rpc.InvokeAsync<TestHistoryInfo>("listTestRuns", last);
        public Task<TestReportResult> ExportTestReportAsync() => _rpc.InvokeAsync<TestReportResult>("exportTestReport");
        public Task<EvidenceLibrary> ListEvidenceAsync() => _rpc.InvokeAsync<EvidenceLibrary>("listEvidence");
        public Task<Semanticus.Engine.Evidence.EvidenceArtifact> GetEvidenceAsync(string id) => _rpc.InvokeAsync<Semanticus.Engine.Evidence.EvidenceArtifact>("getEvidence", id);
        public Task<EvidenceSaveResult> SaveEvidenceAsync(string source, string sourceId, string origin) => _rpc.InvokeAsync<EvidenceSaveResult>("saveEvidence", source, sourceId, origin);
        // Forward to the OWNER so its ChangeBus broadcasts the activity to the UI client (cross-process live feed).
        public Task PublishActivityAsync(ActivityEvent e) => _rpc.InvokeAsync("publishActivity", e);
        // The health mailbox lives on the OWNER engine (where commits happen); the attached MCP filter drains it
        // here. The correlation id crosses the pipe on the PULL only — per-op stamping can't cross RPC today, so
        // an attached process's commits land in the owner's UNSCOPED slot, which every pull also drains (see
        // AgentHealthMailbox's attach-mode note).
        public Task<HealthDelta> PullAgentHealthAsync(string correlationId = null) => _rpc.InvokeAsync<HealthDelta>("pullAgentHealth", correlationId);

        public Task<WorkflowInfo[]> ListWorkflowsAsync() => _rpc.InvokeAsync<WorkflowInfo[]>("listWorkflows");
        public Task<WorkflowEnforcement> GetWorkflowEnforcementAsync() => _rpc.InvokeAsync<WorkflowEnforcement>("getWorkflowEnforcement");
        public Task<WorkflowEnforcement> SetWorkflowEnforcementAsync(string mode, string origin) => _rpc.InvokeAsync<WorkflowEnforcement>("setWorkflowEnforcement", mode, origin);
        public Task<WorkflowDef> GetWorkflowAsync(string name) => _rpc.InvokeAsync<WorkflowDef>("getWorkflow", name);
        public Task<WorkflowRunView> StartWorkflowAsync(string name, string origin) => _rpc.InvokeAsync<WorkflowRunView>("startWorkflow", name, origin);
        public Task<WorkflowRunView> GetWorkflowRunAsync(string runId) => _rpc.InvokeAsync<WorkflowRunView>("getWorkflowRun", runId);
        public Task<WorkflowRunView> SubmitWorkflowStepAsync(string runId, string stepId, string answersJson, string origin) => _rpc.InvokeAsync<WorkflowRunView>("submitWorkflowStep", runId, stepId, answersJson, origin);
        public Task<WorkflowRunView> SkipWorkflowStepAsync(string runId, string stepId, string reason, string origin) => _rpc.InvokeAsync<WorkflowRunView>("skipWorkflowStep", runId, stepId, reason, origin);
        public Task<WorkflowRunView> AbortWorkflowAsync(string runId, string reason, string origin) => _rpc.InvokeAsync<WorkflowRunView>("abortWorkflow", runId, reason, origin);
        public Task<Semanticus.Engine.Evidence.EvidenceArtifact> ExportWorkflowEvidenceAsync(string runId) => _rpc.InvokeAsync<Semanticus.Engine.Evidence.EvidenceArtifact>("exportWorkflowEvidence", runId);
        public Task<WorkflowInfo[]> SaveWorkflowAsync(string name, string markdown, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("saveWorkflow", name, markdown, origin);
        public Task<WorkflowInfo[]> DeleteWorkflowAsync(string name, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("deleteWorkflow", name, origin);
        public Task<WorkflowTemplateInfo[]> ListWorkflowTemplatesAsync() => _rpc.InvokeAsync<WorkflowTemplateInfo[]>("listWorkflowTemplates");
        public Task<WorkflowTemplate> GetWorkflowTemplateAsync(string name) => _rpc.InvokeAsync<WorkflowTemplate>("getWorkflowTemplate", name);
        public Task<WorkflowTemplateInfo[]> SaveWorkflowTemplateAsync(string name, string markdown, string origin) => _rpc.InvokeAsync<WorkflowTemplateInfo[]>("saveWorkflowTemplate", name, markdown, origin);
        public Task<WorkflowTemplateInfo[]> DeleteWorkflowTemplateAsync(string name, string origin) => _rpc.InvokeAsync<WorkflowTemplateInfo[]>("deleteWorkflowTemplate", name, origin);
        public Task<WorkflowInfo[]> InstantiateWorkflowTemplateAsync(string templateName, string newName, string valuesJson, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("instantiateWorkflowTemplate", templateName, newName, valuesJson, origin);
        public Task<WorkflowInfo[]> SetWorkflowEnabledAsync(string name, bool enabled, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("setWorkflowEnabled", name, enabled, origin);
        public Task<WorkflowInfo[]> SetWorkflowBindingAsync(string op, string[] requireNames, string mode, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("setWorkflowBinding", op, requireNames, mode, origin);
        public Task<WorkflowInfo[]> SetWorkflowActivationAsync(string workflow, string when, string set, string origin) => _rpc.InvokeAsync<WorkflowInfo[]>("setWorkflowActivation", workflow, when, set, origin);
        public Task<WorkflowPolicy> GetWorkflowPolicyAsync() => _rpc.InvokeAsync<WorkflowPolicy>("getWorkflowPolicy");
        public Task<WorkflowProfileInfo[]> ListWorkflowProfilesAsync() => _rpc.InvokeAsync<WorkflowProfileInfo[]>("listWorkflowProfiles");
        public Task<WorkflowProfileResult> ActivateWorkflowProfileAsync(string name, string origin) => _rpc.InvokeAsync<WorkflowProfileResult>("activateWorkflowProfile", name, origin);
        public Task<OpInfo[]> GetOpCatalogAsync() => _rpc.InvokeAsync<OpInfo[]>("getOpCatalog");
        public Task<WorkflowCheckReport> CheckWorkflowAsync(string name) => _rpc.InvokeAsync<WorkflowCheckReport>("checkWorkflow", name);
        public Task<WorkflowReplayReport> ReplayCheckWorkflowAsync(string name) => _rpc.InvokeAsync<WorkflowReplayReport>("replayCheckWorkflow", name);
        public Task<DryRunReport> DryRunOpAsync(string op, string argsJson) => _rpc.InvokeAsync<DryRunReport>("dryRunOp", op, argsJson);

        public Task<InsightRecord> AddInsightAsync(string text, string[] keys, string kind, string scope, bool fingerprintScoped, string origin) => _rpc.InvokeAsync<InsightRecord>("addInsight", text, keys, kind, scope, fingerprintScoped, origin);
        public Task<InsightRecord> ApproveInsightAsync(string id, string origin) => _rpc.InvokeAsync<InsightRecord>("approveInsight", id, origin);
        public Task<InsightRecord> EditInsightAsync(string id, string text, string[] keys, string origin) => _rpc.InvokeAsync<InsightRecord>("editInsight", id, text, keys, origin);
        public Task<InsightRecord> UpvoteInsightAsync(string id, string origin) => _rpc.InvokeAsync<InsightRecord>("upvoteInsight", id, origin);
        public Task<InsightRecord> DownvoteInsightAsync(string id, string origin) => _rpc.InvokeAsync<InsightRecord>("downvoteInsight", id, origin);
        public Task<SetResult> DeleteInsightAsync(string id, string origin) => _rpc.InvokeAsync<SetResult>("deleteInsight", id, origin);
        public Task<PurgeResult> PurgeKnowledgeAsync(string scope, bool confirm, string origin) => _rpc.InvokeAsync<PurgeResult>("purgeKnowledge", scope, confirm, origin);
        public Task<InsightListResult> ListInsightsAsync(string scope, string status) => _rpc.InvokeAsync<InsightListResult>("listInsights", scope, status);
        public Task<RecallResult> RecallExperienceAsync(string query, int maxResults) => _rpc.InvokeAsync<RecallResult>("recallExperience", query, maxResults);
        public Task<PrimerDocument> GetPrimerAsync() => _rpc.InvokeAsync<PrimerDocument>("getPrimer");
        public Task<PrimerDocument> SetPrimerAsync(string markdown, string origin) => _rpc.InvokeAsync<PrimerDocument>("setPrimer", markdown, origin);
        public Task<PrimerSuggestionList> ListPrimerSuggestionsAsync() => _rpc.InvokeAsync<PrimerSuggestionList>("listPrimerSuggestions");
        public Task<PrimerSuggestionDecision> AcceptPrimerSuggestionAsync(string id, string origin) => _rpc.InvokeAsync<PrimerSuggestionDecision>("acceptPrimerSuggestion", id, origin);
        public Task<PrimerSuggestionDecision> RejectPrimerSuggestionAsync(string id, string origin) => _rpc.InvokeAsync<PrimerSuggestionDecision>("rejectPrimerSuggestion", id, origin);
        public Task<ModelFingerprint> GetModelFingerprintAsync() => _rpc.InvokeAsync<ModelFingerprint>("getModelFingerprint");

        public Task<InterviewListResult> ListInterviewQuestionsAsync(string scope) => _rpc.InvokeAsync<InterviewListResult>("listInterviewQuestions", scope);
        public Task<InterviewQuestion> AddInterviewQuestionAsync(string question, string tier, string query, string scalarExpr, string paraphraseExpr, string[] groupBy, string[] filters, string expectedValue, string expectedMatrixJson, bool expectRefusal, string fixRuleId, string seedSource, string scope, string origin) => _rpc.InvokeAsync<InterviewQuestion>("addInterviewQuestion", question, tier, query, scalarExpr, paraphraseExpr, groupBy, filters, expectedValue, expectedMatrixJson, expectRefusal, fixRuleId, seedSource, scope, origin);
        public Task<InterviewRunResult> RunInterviewAsync(string questionId, string inlineJson, bool abstained, string attemptDax, string origin) => _rpc.InvokeAsync<InterviewRunResult>("runInterview", questionId, inlineJson, abstained, attemptDax, origin);
        public Task<SetResult> DeleteInterviewQuestionAsync(string id, string origin) => _rpc.InvokeAsync<SetResult>("deleteInterviewQuestion", id, origin);
        public Task<InterviewSeedResult> ListInterviewSeedsAsync(string source, string measure) => _rpc.InvokeAsync<InterviewSeedResult>("listInterviewSeeds", source, measure);

        public Task<ChangePlanView> ProposePlanAsync(string scope, bool includeAi, int maxAiItems, string origin) => _rpc.InvokeAsync<ChangePlanView>("proposePlan", scope, includeAi, maxAiItems, origin);
        public Task<ChangePlanView> ProposeReplaceAsync(SearchOptions find, string replace, int maxItems, string origin) => _rpc.InvokeAsync<ChangePlanView>("proposeReplace", find, replace, maxItems, origin);
        public Task<ChangePlanView> GetPlanAsync() => _rpc.InvokeAsync<ChangePlanView>("getPlan");
        public Task<ChangePlanView> AddPlanItemAsync(string objRef, string kind, string after, string title, string[] verifyGroupBy, string[] verifyFilters, string origin) => _rpc.InvokeAsync<ChangePlanView>("addPlanItem", objRef, kind, after, title, verifyGroupBy, verifyFilters, origin);
        public Task<ChangePlanView> SetPlanItemAsync(string itemId, string after, bool? approved, string origin) => _rpc.InvokeAsync<ChangePlanView>("setPlanItem", itemId, after, approved, origin);
        public Task<ApplyPlanReport> ApplyPlanAsync(string[] approvedIds, string origin, string[] overrideIds = null, string overrideReason = null) => _rpc.InvokeAsync<ApplyPlanReport>("applyPlan", new object[] { approvedIds, origin, overrideIds, overrideReason });
        public Task<ChangePlanView> ClearPlanAsync(string origin) => _rpc.InvokeAsync<ChangePlanView>("clearPlan", origin);
        public Task<DocModelDto> GetDocModelAsync(int topN) => _rpc.InvokeAsync<DocModelDto>("getDocModel", topN);
        public Task<DocOutline> GetDocOutlineAsync() => _rpc.InvokeAsync<DocOutline>("getDocOutline");
        public Task<string> GetDocSectionAsync(string objRef, string sectionKey) => _rpc.InvokeAsync<string>("getDocSection", objRef, sectionKey);
        public Task<SetResult> SetDocSectionAsync(string objRef, string sectionKey, string markdown, string origin) => _rpc.InvokeAsync<SetResult>("setDocSection", objRef, sectionKey, markdown, origin);
        public Task<SpecView> GetSpecAsync() => _rpc.InvokeAsync<SpecView>("getSpec");
        public Task<SpecView> SetSpecAsync(string specJson, string origin) => _rpc.InvokeAsync<SpecView>("setSpec", specJson, origin);
        public Task<SpecView> ClearSpecAsync(string origin) => _rpc.InvokeAsync<SpecView>("clearSpec", origin);
        public Task<SpecView> SaveSpecAsync(string path) => _rpc.InvokeAsync<SpecView>("saveSpec", path);
        public Task<SpecView> LoadSpecAsync(string path, string origin) => _rpc.InvokeAsync<SpecView>("loadSpec", path, origin);
        public Task<SpecBuildReport> BuildModelFromSpecAsync(string origin) => _rpc.InvokeAsync<SpecBuildReport>("buildModelFromSpec", origin);
        public Task<SpecView> AutogenerateSpecFromModelAsync(string origin) => _rpc.InvokeAsync<SpecView>("autogenerateSpecFromModel", origin);
        public Task<SpecView> AutogenerateSpecFromFabricAsync(string server, string database, string authMode, string storageMode, string origin, string tenantId = null) => _rpc.InvokeAsync<SpecView>("autogenerateSpecFromFabric", server, database, authMode, storageMode, origin, tenantId);
        public Task<GitStatus> GitStatusAsync() => _rpc.InvokeAsync<GitStatus>("gitStatus");
        public Task<GitDiffResult> GitDiffAsync(string path, bool staged) => _rpc.InvokeAsync<GitDiffResult>("gitDiff", path, staged);
        public Task<GitLogEntry[]> GitLogAsync(int max) => _rpc.InvokeAsync<GitLogEntry[]>("gitLog", max);
        public Task<GitCommitResult> GitCommitAsync(string message, string[] files, bool commit, string origin) => _rpc.InvokeAsync<GitCommitResult>("gitCommit", message, files, commit, origin);
        public Task<HistoryCheckpointList> ListHistoryCheckpointsAsync(int max = 50) => _rpc.InvokeAsync<HistoryCheckpointList>("listHistoryCheckpoints", max);
        public Task<HistoryCheckpointResult> CreateHistoryCheckpointAsync(string label, bool commit, string origin) => _rpc.InvokeAsync<HistoryCheckpointResult>("createHistoryCheckpoint", label, commit, origin);
        public Task<HistoryRestoreResult> RestoreHistoryCheckpointAsync(string hash, bool restore, string origin) => _rpc.InvokeAsync<HistoryRestoreResult>("restoreHistoryCheckpoint", hash, restore, origin);
        public Task<GitActionResult> GitBranchAsync(string name, bool create, bool checkout, string origin) => _rpc.InvokeAsync<GitActionResult>("gitBranch", name, create, checkout, origin);
        public Task<GitActionResult> GitCheckoutAsync(string gitRef, string origin) => _rpc.InvokeAsync<GitActionResult>("gitCheckout", gitRef, origin);
        public Task<GitActionResult> GitPullAsync(string origin) => _rpc.InvokeAsync<GitActionResult>("gitPull", origin);
        public Task<GitActionResult> GitPushAsync(string remote, string branch, bool confirm, string origin) => _rpc.InvokeAsync<GitActionResult>("gitPush", remote, branch, confirm, origin);
        public Task<GitActionResult> GitCloneAsync(string url, string directory, string origin) => _rpc.InvokeAsync<GitActionResult>("gitClone", url, directory, origin);
        public Task<ModelDiff> CompareModelsAsync(ModelRef left, ModelRef right, bool includeEqual = false, string origin = "human") => _rpc.InvokeAsync<ModelDiff>("compareModels", left, right, includeEqual, origin);
        public Task<ApplyDiffResult> ApplyDiffAsync(ModelRef left, ModelRef right, string[] selectedRefs, bool commit, string origin, string overrideReason = null) => _rpc.InvokeAsync<ApplyDiffResult>("applyDiff", left, right, selectedRefs, commit, origin, overrideReason);
        public Task<CherryPickResult> CherryPickAsync(ModelRef source, string[] refs, bool includeDependencies, bool commit, string origin) => _rpc.InvokeAsync<CherryPickResult>("cherryPick", source, refs, includeDependencies, commit, origin);
        public Task<TreeNode[]> ListReferenceTreeAsync(ModelRef reference, string origin = "human") => _rpc.InvokeAsync<TreeNode[]>("listReferenceTree", reference, origin);
        public Task<DeployGate> DeployGateAsync(ModelRef compareTarget) => _rpc.InvokeAsync<DeployGate>("deployGate", compareTarget);
        public Task<ModelConnectionRecord[]> ListConnectionsAsync() => _rpc.InvokeAsync<ModelConnectionRecord[]>("listConnections");
        public Task<ModelConnectionRecord> LabelConnectionAsync(string id, string label, string origin = "agent") => _rpc.InvokeAsync<ModelConnectionRecord>("labelConnection", id, label, origin);
        public Task<ModelConnectionRecord> SetConnectionWorkingFolderAsync(string id, string folder) => _rpc.InvokeAsync<ModelConnectionRecord>("setConnectionWorkingFolder", id, folder);
        public Task<bool> ForgetConnectionAsync(string id, string origin = "agent") => _rpc.InvokeAsync<bool>("forgetConnection", id, origin);
        public Task<AgentPolicy> GetAgentPolicyAsync() => _rpc.InvokeAsync<AgentPolicy>("getAgentPolicy");
        public Task<AgentPolicy> SetAgentPolicyPresetAsync(string preset, string origin) => _rpc.InvokeAsync<AgentPolicy>("setAgentPolicyPreset", preset, origin);
        public Task<AgentPolicy> SetAgentPolicyCellAsync(string capability, string label, string action, string origin) => _rpc.InvokeAsync<AgentPolicy>("setAgentPolicyCell", capability, label, action, origin);
        public Task<AgentPolicy> SetAgentPolicyEnabledAsync(bool enabled, string origin) => _rpc.InvokeAsync<AgentPolicy>("setAgentPolicyEnabled", enabled, origin);
        public Task<ApprovalRecord[]> ListPendingApprovalsAsync() => _rpc.InvokeAsync<ApprovalRecord[]>("listPendingApprovals");
        public Task<ApprovalRecord> ApproveAgentActionAsync(string id, string origin) => _rpc.InvokeAsync<ApprovalRecord>("approveAgentAction", id, origin);
        public Task<bool> DenyAgentActionAsync(string id, string origin) => _rpc.InvokeAsync<bool>("denyAgentAction", id, origin);
        public Task<RestorePointRecord[]> ListRestorePointsAsync(string endpoint = null, string database = null) => _rpc.InvokeAsync<RestorePointRecord[]>("listRestorePoints", endpoint, database);
        public Task<RollbackResult> RollbackPushAsync(string restorePointId, bool commit, string authMode = null, string origin = "human") => _rpc.InvokeAsync<RollbackResult>("rollbackPush", restorePointId, commit, authMode, origin);
        public Task<RestorePointPurgeResult> PurgeRestorePointsAsync(string id = null, int? olderThanDays = null,
            bool confirm = false, string confirmToken = null, string origin = "human") =>
            _rpc.InvokeAsync<RestorePointPurgeResult>("purgeRestorePoints", id, olderThanDays, confirm, confirmToken, origin);
        public Task<FabricWorkspace[]> ListWorkspacesAsync(string authMode, string tenantId) => _rpc.InvokeAsync<FabricWorkspace[]>("listWorkspaces", authMode, tenantId);
        public Task<DeploymentPipeline[]> ListDeploymentPipelinesAsync(string authMode, string tenantId) => _rpc.InvokeAsync<DeploymentPipeline[]>("listDeploymentPipelines", authMode, tenantId);
        public Task<PipelineStage[]> GetPipelineStagesAsync(string pipelineId, string authMode, string tenantId) => _rpc.InvokeAsync<PipelineStage[]>("getPipelineStages", pipelineId, authMode, tenantId);
        public Task<StageItem[]> GetStageItemsAsync(string pipelineId, string stageId, string authMode, string tenantId) => _rpc.InvokeAsync<StageItem[]>("getStageItems", pipelineId, stageId, authMode, tenantId);
        public Task<DeployPreview> PreviewDeployAsync(string pipelineId, string sourceStageId, string targetStageId, string authMode, string tenantId) => _rpc.InvokeAsync<DeployPreview>("previewDeploy", pipelineId, sourceStageId, targetStageId, authMode, tenantId);
        public Task<DeployStageReport> DeployStageAsync(string pipelineId, string sourceStageId, string targetStageId, string[] items, string note, bool commit, string confirmToken, bool forceOverride, string authMode, string tenantId, string origin, string overrideReason = null) => _rpc.InvokeAsync<DeployStageReport>("deployStage", pipelineId, sourceStageId, targetStageId, items, note, commit, confirmToken, forceOverride, authMode, tenantId, origin, overrideReason);
        public Task<DeploymentHistoryEntry[]> DeploymentHistoryAsync(string pipelineId, string authMode, string tenantId) => _rpc.InvokeAsync<DeploymentHistoryEntry[]>("deploymentHistory", pipelineId, authMode, tenantId);
        public Task<FabricGitConnection> FabricGitConnectionAsync(string workspaceId, string authMode, string tenantId) => _rpc.InvokeAsync<FabricGitConnection>("fabricGitConnection", workspaceId, authMode, tenantId);
        public Task<FabricGitStatus> FabricGitStatusAsync(string workspaceId, string authMode, string tenantId) => _rpc.InvokeAsync<FabricGitStatus>("fabricGitStatus", workspaceId, authMode, tenantId);
        public Task<FabricGitResult> FabricGitCommitAsync(string workspaceId, string comment, string[] items, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<FabricGitResult>("fabricGitCommit", workspaceId, comment, items, commit, authMode, tenantId, origin);
        public Task<FabricGitResult> FabricGitUpdateAsync(string workspaceId, string conflictPolicy, bool allowOverride, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<FabricGitResult>("fabricGitUpdate", workspaceId, conflictPolicy, allowOverride, commit, authMode, tenantId, origin);
        public Task<FabricGitResult> FabricGitConnectAsync(string workspaceId, string provider, string organization, string project, string repository, string branch, string directory, string connectionId, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<FabricGitResult>("fabricGitConnect", workspaceId, provider, organization, project, repository, branch, directory, connectionId, commit, authMode, tenantId, origin);
        public Task<FabricGitResult> FabricGitDisconnectAsync(string workspaceId, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<FabricGitResult>("fabricGitDisconnect", workspaceId, commit, authMode, tenantId, origin);
        public Task<CicdPublishResult> CicdPublishAsync(string workspaceId, string itemId, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<CicdPublishResult>("cicdPublish", workspaceId, itemId, commit, authMode, tenantId, origin);
        public Task<CicdScaffold> CicdGenerateAsync(string target, string workspaceId, string environment, bool write) => _rpc.InvokeAsync<CicdScaffold>("cicdGenerate", target, workspaceId, environment, write);
        public Task<DataAgentList> ListDataAgentsAsync(string workspaceId, string authMode, string tenantId) => _rpc.InvokeAsync<DataAgentList>("listDataAgents", workspaceId, authMode, tenantId);
        public Task<DataAgentDetail> GetDataAgentAsync(string workspaceId, string agentId, string authMode, string tenantId) => _rpc.InvokeAsync<DataAgentDetail>("getDataAgent", workspaceId, agentId, authMode, tenantId);
        public Task<DataAgentConfig> GenerateDataAgentConfigFromModelAsync(int maxColumnsPerTable) => _rpc.InvokeAsync<DataAgentConfig>("generateDataAgentConfig", maxColumnsPerTable);
        public Task<DataAgentWriteReport> CreateDataAgentAsync(string workspaceId, string name, string aiInstructions, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<DataAgentWriteReport>("createDataAgent", workspaceId, name, aiInstructions, commit, authMode, tenantId, origin);
        public Task<DataAgentWriteReport> UpdateDataAgentAsync(string workspaceId, string agentId, string aiInstructions, string datasourceFolder, string datasourceJson, string fewshotsJson, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<DataAgentWriteReport>("updateDataAgent", workspaceId, agentId, aiInstructions, datasourceFolder, datasourceJson, fewshotsJson, commit, authMode, tenantId, origin);
        public Task<DataAgentWriteReport> PublishDataAgentAsync(string workspaceId, string agentId, string description, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<DataAgentWriteReport>("publishDataAgent", workspaceId, agentId, description, commit, authMode, tenantId, origin);
        public Task<DataAgentWriteReport> DeleteDataAgentAsync(string workspaceId, string agentId, bool commit, string authMode, string tenantId, string origin) => _rpc.InvokeAsync<DataAgentWriteReport>("deleteDataAgent", workspaceId, agentId, commit, authMode, tenantId, origin);

        public void Dispose() => _rpc.Dispose();
    }
}
