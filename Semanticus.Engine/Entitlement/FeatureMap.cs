using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>Which operation belongs to which Pro feature, and which are free. Every name either maps to one of
    /// the four features or appears in an explicit free list; there is no third state, and
    /// <c>FeatureMapCompletenessTests</c> fails if one ever appears.
    ///
    /// Three keys, one answer. The GATE keys on the IEngine method, because the two doors do not agree on names:
    /// this engine exposes 327 MCP operations and 328 RPC methods, and the same implementation is reached under
    /// different names by each (list_table_mappings -&gt; listTableMappings -&gt; ListTableSourceMappingsAsync). The
    /// operation and RPC tables exist so a test can walk every wire name through to its method, and so a door can
    /// answer "is this Pro?" without calling it.
    ///
    /// The Tab is the tab a person would open, not the enum name, so a refusal reads like the product rather than
    /// like the code. Generated from the measured door surfaces on 2026-09-15; see docs/redesign/free-pro-matrix.md.</summary>
    public static class FeatureMap
    {
        /// <summary>One Pro tab: its feature, and the same operations under all three names.</summary>
        private sealed class Group
        {
            public readonly ProFeature Feature;
            public readonly string Tab;
            public readonly string[] Operations, RpcMethods, EngineMethods;
            public Group(ProFeature feature, string tab, string[] operations, string[] rpcMethods, string[] engineMethods)
            {
                Feature = feature; Tab = tab;
                Operations = operations; RpcMethods = rpcMethods; EngineMethods = engineMethods;
            }
        }

        private static readonly Group[] Groups =
        {
            new Group(ProFeature.ModelCreate, "Model Spec",
                new[]   // 8 operations
                {
                    "autogenerate_spec_from_fabric", "autogenerate_spec_from_model", "build_model_from_spec", "clear_spec",
                    "get_spec", "load_spec", "save_spec", "set_spec",
                },
                new[]   // the same 8 through their RPC names
                {
                    "autogenerateSpecFromFabric", "autogenerateSpecFromModel", "buildModelFromSpec", "clearSpec",
                    "getSpec", "loadSpec", "saveSpec", "setSpec",
                },
                new[]   // the same 8 as IEngine methods, the gate key
                {
                    "AutogenerateSpecFromFabricAsync", "AutogenerateSpecFromModelAsync", "BuildModelFromSpecAsync", "ClearSpecAsync",
                    "GetSpecAsync", "LoadSpecAsync", "SaveSpecAsync", "SetSpecAsync",
                }),
            new Group(ProFeature.ModelCreate, "Advanced Modelling",
                new[]   // 32 operations
                {
                    "create_calculation_group", "create_calculation_item", "create_field_parameter", "create_function",
                    "create_perspective", "create_role", "daxlib_install", "daxlib_list_installed",
                    "daxlib_package_info", "daxlib_search", "daxlib_uninstall", "daxlib_versions",
                    "define_calendar", "define_calendar_from_template", "delete_calendar", "delete_role",
                    "generate_date_table", "generate_time_intelligence", "get_perspectives", "list_calculation_groups",
                    "list_calendars", "list_roles", "mark_date_table", "set_calc_group_precedence",
                    "set_calc_item_format_string", "set_column_ols", "set_perspective_member", "set_role_member",
                    "set_role_permission", "set_table_ols", "set_table_permission", "tag_calendar_column",
                },
                new[]   // the same 32 through their RPC names
                {
                    "createCalculationGroup", "createCalculationItem", "createFieldParameter", "createFunction",
                    "createPerspective", "createRole", "daxLibInstall", "daxLibListInstalled",
                    "daxLibPackageInfo", "daxLibSearch", "daxLibUninstall", "daxLibVersions",
                    "defineCalendar", "defineCalendarFromTemplate", "deleteCalendar", "deleteRole",
                    "generateDateTable", "generateTimeIntelligence", "getPerspectives", "listCalculationGroups",
                    "listCalendars", "listRoles", "markDateTable", "setCalcGroupPrecedence",
                    "setCalcItemFormatString", "setColumnObjectPermission", "setPerspectiveMember", "setRoleMember",
                    "setRolePermission", "setTableObjectPermission", "setTablePermission", "tagCalendarColumn",
                },
                new[]   // the same 32 as IEngine methods, the gate key
                {
                    "CreateCalculationGroupAsync", "CreateCalculationItemAsync", "CreateFieldParameterAsync", "CreateFunctionAsync",
                    "CreatePerspectiveAsync", "CreateRoleAsync", "DaxLibInstallAsync", "DaxLibListInstalledAsync",
                    "DaxLibPackageInfoAsync", "DaxLibSearchAsync", "DaxLibUninstallAsync", "DaxLibVersionsAsync",
                    "DefineCalendarAsync", "DefineCalendarFromTemplateAsync", "DeleteCalendarAsync", "DeleteRoleAsync",
                    "GenerateDateTableAsync", "GenerateTimeIntelligenceAsync", "GetPerspectivesAsync", "ListCalculationGroupsAsync",
                    "ListCalendarsAsync", "ListRolesAsync", "MarkDateTableAsync", "SetCalcGroupPrecedenceAsync",
                    "SetCalcItemFormatStringAsync", "SetColumnObjectPermissionAsync", "SetPerspectiveMemberAsync", "SetRoleMemberAsync",
                    "SetRolePermissionAsync", "SetTableObjectPermissionAsync", "SetTablePermissionAsync", "TagCalendarColumnAsync",
                }),
            new Group(ProFeature.ModelCreate, "Power Query",
                new[]   // 14 operations
                {
                    "apply_schema_update", "create_data_source", "create_named_expression", "diff_schema",
                    "get_incremental_refresh_policy", "get_named_expression", "get_partition_m", "get_source_schema",
                    "list_named_expressions", "list_partitions", "remove_incremental_refresh_policy", "set_incremental_refresh_policy",
                    "set_partition_m", "update_named_expression",
                },
                new[]   // the same 14 through their RPC names
                {
                    "applySchemaUpdate", "createDataSource", "createNamedExpression", "diffSchema",
                    "getIncrementalRefreshPolicy", "getNamedExpression", "getPartitionM", "getSourceSchema",
                    "listNamedExpressions", "listPartitions", "removeIncrementalRefreshPolicy", "setIncrementalRefreshPolicy",
                    "setPartitionM", "updateNamedExpression",
                },
                new[]   // the same 14 as IEngine methods, the gate key
                {
                    "ApplySchemaUpdateAsync", "CreateDataSourceAsync", "CreateNamedExpressionAsync", "DiffSchemaAsync",
                    "GetIncrementalRefreshPolicyAsync", "GetNamedExpressionAsync", "GetPartitionMAsync", "GetSourceSchemaAsync",
                    "ListNamedExpressionsAsync", "ListPartitionsAsync", "RemoveIncrementalRefreshPolicyAsync", "SetIncrementalRefreshPolicyAsync",
                    "SetPartitionMAsync", "UpdateNamedExpressionAsync",
                }),
            new Group(ProFeature.ModelCreate, "Docs",
                new[]   // 4 operations
                {
                    "get_doc_model", "get_doc_outline", "get_doc_section", "set_doc_section",
                },
                new[]   // the same 4 through their RPC names
                {
                    "getDocModel", "getDocOutline", "getDocSection", "setDocSection",
                },
                new[]   // the same 4 as IEngine methods, the gate key
                {
                    "GetDocModelAsync", "GetDocOutlineAsync", "GetDocSectionAsync", "SetDocSectionAsync",
                }),
            new Group(ProFeature.ModelCreate, "Model notes",
                new[]   // 11 operations
                {
                    "add_insight", "approve_insight", "delete_insight", "downvote_insight",
                    "edit_insight", "get_model_primer", "list_insights", "purge_knowledge",
                    "recall_experience", "set_model_primer", "upvote_insight",
                },
                new[]   // the same 11 through their RPC names
                {
                    "addInsight", "approveInsight", "deleteInsight", "downvoteInsight",
                    "editInsight", "getPrimer", "listInsights", "purgeKnowledge",
                    "recallExperience", "setPrimer", "upvoteInsight",
                },
                new[]   // the same 11 as IEngine methods, the gate key
                {
                    "AddInsightAsync", "ApproveInsightAsync", "DeleteInsightAsync", "DownvoteInsightAsync",
                    "EditInsightAsync", "GetPrimerAsync", "ListInsightsAsync", "PurgeKnowledgeAsync",
                    "RecallExperienceAsync", "SetPrimerAsync", "UpvoteInsightAsync",
                }),
            new Group(ProFeature.Tests, "Tests",
                new[]   // 17 operations
                {
                    "capture_baseline", "clear_table_source_mapping", "compare_baseline", "delete_test",
                    "export_test_report", "get_test_run", "get_unmatched_rows", "list_table_mappings",
                    "list_test_runs", "list_tests", "reconcile_measure", "record_test_run",
                    "review_reconcile_mapping", "run_tests", "save_test", "set_table_source_mapping",
                    "try_test",
                },
                new[]   // the same 17 through their RPC names
                {
                    "captureBaseline", "clearTableSourceMapping", "compareBaseline", "deleteTest",
                    "exportTestReport", "getTestRun", "getUnmatchedRows", "listTableMappings",
                    "listTestRuns", "listTests", "reconcileMeasure", "recordTestRun",
                    "reviewReconcileMapping", "runTests", "saveTest", "setTableSourceMapping",
                    "tryTest",
                },
                new[]   // the same 17 as IEngine methods, the gate key
                {
                    "CaptureBaselineAsync", "ClearTableSourceMappingAsync", "CompareBaselineAsync", "DeleteTestDefinitionAsync",
                    "ExportTestReportAsync", "GetTestRunAsync", "GetUnmatchedRowsAsync", "ListTableSourceMappingsAsync",
                    "ListTestDefinitionsAsync", "ListTestRunsAsync", "ReconcileMeasureAsync", "RecordTestRunAsync",
                    "ReviewReconcileMappingAsync", "RunTestSuiteAsync", "SaveTestDefinitionAsync", "SetTableSourceMappingAsync",
                    "TryTestAsync",
                }),
            new Group(ProFeature.Tests, "Saved reports",
                new[]   // 3 operations
                {
                    "get_evidence", "list_evidence", "save_evidence",
                },
                new[]   // the same 3 through their RPC names
                {
                    "getEvidence", "listEvidence", "saveEvidence",
                },
                new[]   // the same 3 as IEngine methods, the gate key
                {
                    "GetEvidenceAsync", "ListEvidenceAsync", "SaveEvidenceAsync",
                }),
            new Group(ProFeature.PublishedAdvanced, "Source control",
                new[]   // 8 operations
                {
                    "git_branch", "git_checkout", "git_clone", "git_commit",
                    "git_diff", "git_log", "git_pull", "git_push",
                },
                new[]   // the same 8 through their RPC names
                {
                    "gitBranch", "gitCheckout", "gitClone", "gitCommit",
                    "gitDiff", "gitLog", "gitPull", "gitPush",
                },
                new[]   // the same 8 as IEngine methods, the gate key
                {
                    "GitBranchAsync", "GitCheckoutAsync", "GitCloneAsync", "GitCommitAsync",
                    "GitDiffAsync", "GitLogAsync", "GitPullAsync", "GitPushAsync",
                }),
            new Group(ProFeature.PublishedAdvanced, "Fabric Git",
                new[]   // 6 operations
                {
                    "fabric_git_commit", "fabric_git_connect", "fabric_git_connection", "fabric_git_disconnect",
                    "fabric_git_status", "fabric_git_update",
                },
                new[]   // the same 6 through their RPC names
                {
                    "fabricGitCommit", "fabricGitConnect", "fabricGitConnection", "fabricGitDisconnect",
                    "fabricGitStatus", "fabricGitUpdate",
                },
                new[]   // the same 6 as IEngine methods, the gate key
                {
                    "FabricGitCommitAsync", "FabricGitConnectAsync", "FabricGitConnectionAsync", "FabricGitDisconnectAsync",
                    "FabricGitStatusAsync", "FabricGitUpdateAsync",
                }),
            new Group(ProFeature.PublishedAdvanced, "CI and CD publish",
                new[]   // 2 operations
                {
                    "cicd_generate", "cicd_publish",
                },
                new[]   // the same 2 through their RPC names
                {
                    "cicdGenerate", "cicdPublish",
                },
                new[]   // the same 2 as IEngine methods, the gate key
                {
                    "CicdGenerateAsync", "CicdPublishAsync",
                }),
            new Group(ProFeature.PublishedAdvanced, "Data agent",
                new[]   // 7 operations
                {
                    "create_data_agent", "delete_data_agent", "generate_data_agent_config", "get_data_agent",
                    "list_data_agents", "publish_data_agent", "update_data_agent",
                },
                new[]   // the same 7 through their RPC names
                {
                    "createDataAgent", "deleteDataAgent", "generateDataAgentConfig", "getDataAgent",
                    "listDataAgents", "publishDataAgent", "updateDataAgent",
                },
                new[]   // the same 7 as IEngine methods, the gate key
                {
                    "CreateDataAgentAsync", "DeleteDataAgentAsync", "GenerateDataAgentConfigFromModelAsync", "GetDataAgentAsync",
                    "ListDataAgentsAsync", "PublishDataAgentAsync", "UpdateDataAgentAsync",
                }),
            new Group(ProFeature.Workflows, "Workflows",
                new[]   // 30 operations
                {
                    "abort_workflow", "activate_workflow_profile", "check_workflow", "delete_workflow",
                    "delete_workflow_template", "edit_workflow_document", "export_workflow_evidence", "get_workflow",
                    "get_workflow_document", "get_workflow_enforcement", "get_workflow_layout", "get_workflow_policy",
                    "get_workflow_template", "instantiate_workflow_template", "list_workflow_profiles", "list_workflow_templates",
                    "list_workflows", "preview_workflow_edit", "replay_check_workflow", "save_workflow",
                    "save_workflow_layout", "save_workflow_template", "set_workflow_activation", "set_workflow_binding",
                    "set_workflow_enabled", "set_workflow_enforcement", "skip_workflow_step", "start_workflow",
                    "submit_workflow_step", "upgrade_workflow",
                },
                new[]   // the same 30 through their RPC names
                {
                    "abortWorkflow", "activateWorkflowProfile", "checkWorkflow", "deleteWorkflow",
                    "deleteWorkflowTemplate", "editWorkflowDocument", "exportWorkflowEvidence", "getWorkflow",
                    "getWorkflowDocument", "getWorkflowEnforcement", "getWorkflowLayout", "getWorkflowPolicy",
                    "getWorkflowTemplate", "instantiateWorkflowTemplate", "listWorkflowProfiles", "listWorkflowTemplates",
                    "listWorkflows", "previewWorkflowEdit", "replayCheckWorkflow", "saveWorkflow",
                    "saveWorkflowLayout", "saveWorkflowTemplate", "setWorkflowActivation", "setWorkflowBinding",
                    "setWorkflowEnabled", "setWorkflowEnforcement", "skipWorkflowStep", "startWorkflow",
                    "submitWorkflowStep", "upgradeWorkflow",
                },
                new[]   // the same 30 as IEngine methods, the gate key
                {
                    "AbortWorkflowAsync", "ActivateWorkflowProfileAsync", "CheckWorkflowAsync", "DeleteWorkflowAsync",
                    "DeleteWorkflowTemplateAsync", "EditWorkflowDocumentAsync", "ExportWorkflowEvidenceAsync", "GetWorkflowAsync",
                    "GetWorkflowDocumentAsync", "GetWorkflowEnforcementAsync", "GetWorkflowLayoutAsync", "GetWorkflowPolicyAsync",
                    "GetWorkflowTemplateAsync", "InstantiateWorkflowTemplateAsync", "ListWorkflowProfilesAsync", "ListWorkflowTemplatesAsync",
                    "ListWorkflowsAsync", "PreviewWorkflowEditAsync", "ReplayCheckWorkflowAsync", "SaveWorkflowAsync",
                    "SaveWorkflowLayoutAsync", "SaveWorkflowTemplateAsync", "SetWorkflowActivationAsync", "SetWorkflowBindingAsync",
                    "SetWorkflowEnabledAsync", "SetWorkflowEnforcementAsync", "SkipWorkflowStepAsync", "StartWorkflowAsync",
                    "SubmitWorkflowStepAsync", "UpgradeWorkflowAsync",
                }),
        };

        /// <summary>The MCP operation names that are free. Everything the agent door exposes is here or in a Group.</summary>
        public static readonly IReadOnlyCollection<string> FreeOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "accept_primer_suggestion", "add_interview_question", "add_plan_item", "ai_readiness_scan",
            "ai_readiness_scan_live", "ai_readiness_summary", "analyze_cloud_reports", "analyze_reports",
            "apply_dax_script", "apply_fix", "apply_model_diff", "apply_plan",
            "apply_safe_fixes", "apply_tmdl", "benchmark_dax", "benchmark_dax_coldwarm",
            "blame_value", "bpa_fix", "bpa_fix_all", "bpa_get_fix_prompt",
            "bpa_scan", "bpa_summary", "capture_query_plan", "check_reports",
            "cherry_pick", "clear_cache", "clear_plan", "connect_local",
            "connect_xmla", "connection_context", "connection_status", "create_calculated_column",
            "create_calculated_table", "create_column", "create_directlake_table", "create_hierarchy",
            "create_history_checkpoint", "create_import_table", "create_measure", "create_model",
            "create_relationship", "create_table", "delete_interview_question", "delete_object",
            "delete_objects", "delete_sql_source", "deploy_gate", "deploy_live",
            "deploy_stage", "deployment_history", "disconnect", "dry_run",
            "duplicate_object", "enable_qna", "evaluate_and_log", "explain_value",
            "export_verified_edits", "export_vpax", "forget_connection", "get_agent_policy",
            "get_ai_instructions", "get_custom_rules", "get_dax", "get_dependencies",
            "get_entitlement", "get_fix_prompt", "get_grounding", "get_layout",
            "get_lineage", "get_model_fingerprint", "get_model_graph", "get_model_objects",
            "get_model_summary", "get_object", "get_op_catalog", "get_pipeline_stages",
            "get_plan", "get_properties", "get_reference_tree", "get_stage_items",
            "get_verified_mode", "get_workflow_run", "git_status", "harness_report",
            "impact_assessment", "impact_of", "label_connection", "lint_dax",
            "list_account_profiles", "list_columns", "list_connection_history", "list_connections",
            "list_deployment_pipelines", "list_format_templates", "list_functions", "list_history_checkpoints",
            "list_interview_questions", "list_interview_seeds", "list_local_instances", "list_measures",
            "list_objects", "list_pending_approvals", "list_primer_suggestions", "list_refresh_types",
            "list_report_scope", "list_reports", "list_restore_points", "list_sql_source_usage", "list_sql_sources",
            "list_value_history", "list_verified_edits", "list_waivers", "list_workspaces",
            "load_bpa_rules", "load_readiness_rules", "make_model_ai_ready", "model_diff",
            "model_graph_summary", "model_overview", "open_live", "open_local",
            "open_model", "optimize_measure", "pivot_measure", "prepare_working_copy",
            "preview_deploy", "preview_table", "probe_auth_prerequisites", "probe_connection_accounts",
            "probe_measure", "profile_dax", "propose_plan", "propose_replace",
            "purge_restore_points", "redo_change", "refresh_partition", "reject_primer_suggestion",
            "remember_xmla_connection", "remove_safe_objects", "rename_display_folder", "rename_object",
            "replace_in_object", "reset_bpa_rules", "reset_readiness_rules", "restore_history_checkpoint",
            "rollback_push", "run_dax", "run_dmv", "run_interview",
            "save_layout", "save_model", "save_sql_source", "script_objects",
            "search_model", "set_ai_data_schema", "set_ai_instructions", "set_column_data_type",
            "set_column_hidden", "set_compatibility_level", "set_data_category", "set_description",
            "set_display_folder", "set_measure_format", "set_measure_format_expression", "set_plan_item",
            "set_properties", "set_property", "set_publish_destination", "set_relationship_active",
            "set_relationship_cardinality", "set_relationship_crossfilter", "set_report_scope", "set_sort_by_column",
            "set_summarize_by", "set_synonyms", "set_verified_mode", "test_sql_source",
            "undo_change", "unused_objects", "unwaive_finding", "update_measure",
            "validate_dax", "validate_rule", "verify_dax_equivalence", "vpaq_scan",
            "waive_finding",
        };

        /// <summary>The RPC method names that are free, including the five with no MCP twin
        /// (cancelDax, clearReferenceBinding, pullAgentHealth, setConnectionWorkingFolder, setDefaultAccountProfile).</summary>
        public static readonly IReadOnlyCollection<string> FreeRpcMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "acceptPrimerSuggestion", "addInterviewQuestion", "addPlanItem", "aiReadinessScan",
            "aiReadinessScanLive", "analyzeCloudReports", "analyzeReports", "applyDaxScript",
            "applyDiff", "applyFix", "applyPlan", "applySafeFixes",
            "applyTmdlScript", "benchmarkColdWarm", "benchmarkDax", "blameValue",
            "bpaFix", "bpaFixAll", "bpaGetFixPrompt", "bpaScan",
            "cancelDax", "captureQueryPlan", "checkReports", "cherryPick",
            "clearCache", "clearPlan", "clearReferenceBinding", "compareModels",
            "connectLocal", "connectXmla", "connectionContext", "connectionStatus",
            "createCalculatedColumn", "createCalculatedTable", "createColumn", "createDirectLakeTable",
            "createHierarchy", "createHistoryCheckpoint", "createImportTable", "createMeasure",
            "createModel", "createRelationship", "createTable", "deleteInterviewQuestion",
            "deleteObject", "deleteObjects", "deleteSqlSource", "deployGate",
            "deployLive", "deployStage", "deploymentHistory", "disconnect",
            "dryRunOp", "duplicateObject", "enableQna", "evaluateAndLog",
            "explainValue", "exportVerifiedEdits", "exportVpax", "forgetConnection",
            "getAgentPolicy", "getAiInstructions", "getCustomRules", "getDax",
            "getDependencies", "getDependents", "getEntitlement", "getFixPrompt",
            "getGrounding", "getLayout", "getLineage", "getModelFingerprint",
            "getModelGraph", "getModelObjects", "getObject", "getObjectProperties",
            "getOpCatalog", "getOrientation", "getPipelineStages", "getPlan",
            "getStageItems", "getVerifiedMode", "getWorkflowRun", "gitStatus",
            "harnessReport", "impactAssessment", "impactOf", "labelConnection",
            "lintDax", "listAccountProfiles", "listColumns", "listConnectionHistory",
            "listConnections", "listDeploymentPipelines", "listFormatTemplates", "listFunctions",
            "listHistoryCheckpoints", "listInterviewQuestions", "listInterviewSeeds", "listLocalInstances",
            "listMeasures", "listPendingApprovals", "listPrimerSuggestions", "listReferenceTree",
            "listRefreshTypes", "listReportScope", "listReports", "listRestorePoints",
            "listSqlSourceUsage", "listSqlSources", "listTree", "listValueHistory", "listVerifiedEdits",
            "listWaivers", "listWorkspaces", "loadBpaRules", "loadReadinessRules",
            "makeAiReady", "open", "openLive", "openLocal",
            "optimizeMeasure", "pivotMeasure", "prepareWorkingCopy", "previewDeploy",
            "previewTable", "probeAuthPrerequisites", "probeConnectionAccounts", "probeMeasure",
            "profileDax", "proposePlan", "proposeReplace", "publishActivity",
            "pullAgentHealth", "purgeRestorePoints", "redo", "refreshPartition",
            "rejectPrimerSuggestion", "rememberXmlaConnection", "removeSafeObjects", "renameDisplayFolder",
            "renameObject", "replaceInObject", "resetBpaRules", "resetReadinessRules",
            "restoreHistoryCheckpoint", "rollbackPush", "runDax", "runDmv",
            "runInterview", "save", "saveLayout", "saveSqlSource",
            "scriptObjects", "searchModel", "searchModelEx", "sessionInfo",
            "setAiDataSchema", "setAiInstructions", "setColumnDataType", "setColumnMetadata",
            "setCompatibilityLevel", "setConnectionWorkingFolder", "setDax", "setDefaultAccountProfile",
            "setDescription", "setDisplayFolder", "setMeasureFormat", "setMeasureFormatExpression",
            "setObjectProperties", "setObjectProperty", "setPlanItem", "setPublishDestination",
            "setRelationship", "setRelationshipCardinality", "setReportScope", "setSynonyms",
            "setVerifiedMode", "testSqlSource", "undo", "unusedObjects",
            "unwaiveFinding", "validateDax", "validateRule", "verifyEquivalence",
            "vertiPaqScan", "waiveFinding",
        };

        /// <summary>The IEngine methods that are free. This is the list the completeness test holds to: a new engine
        /// method is unclassified until it is named here or in a Group, and the test says so.</summary>
        public static readonly IReadOnlyCollection<string> FreeEngineMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "AcceptPrimerSuggestionAsync", "AddInterviewQuestionAsync", "AddPlanItemAsync", "AiReadinessScanAsync",
            "AiReadinessScanLiveAsync", "AnalyzeCloudReportsAsync", "AnalyzeReportsAsync", "ApplyDaxScriptAsync",
            "ApplyDiffAsync", "ApplyFixAsync", "ApplyPlanAsync", "ApplySafeFixesAsync",
            "ApplyTmdlScriptAsync", "ApproveAgentActionAsync", "BenchmarkColdWarmAsync", "BenchmarkDaxAsync",
            "BlameValueAsync", "BpaFixAllAsync", "BpaFixAsync", "BpaGetFixPromptAsync",
            "BpaScanAsync", "CancelDaxAsync", "CaptureQueryPlanAsync", "CheckReportsAsync",
            "CherryPickAsync", "ClearCacheAsync", "ClearPlanAsync", "ClearReferenceBindingAsync",
            "CompareModelsAsync", "ConnectLocalAsync", "ConnectXmlaAsync", "ConnectionContextAsync",
            "ConnectionStatusAsync", "CreateCalculatedColumnAsync", "CreateCalculatedTableAsync", "CreateColumnAsync",
            "CreateDirectLakeTableAsync", "CreateHierarchyAsync", "CreateHistoryCheckpointAsync", "CreateImportTableAsync",
            "CreateMeasureAsync", "CreateModelAsync", "CreateRelationshipAsync", "CreateTableAsync",
            "DeleteInterviewQuestionAsync", "DeleteObjectAsync", "DeleteObjectsAsync", "DeleteSqlSourceAsync",
            "DenyAgentActionAsync", "DeployGateAsync", "DeployLiveAsync", "DeployStageAsync",
            "DeploymentHistoryAsync", "DisconnectAsync", "DryRunOpAsync", "DuplicateObjectAsync",
            "EnableQnaAsync", "EvaluateAndLogAsync", "ExplainValueAsync", "ExportVerifiedEditsAsync",
            "ExportVpaxAsync", "ForgetConnectionAsync", "GetAgentPolicyAsync", "GetAiInstructionsAsync",
            "GetCustomRulesAsync", "GetDaxAsync", "GetDependenciesAsync", "GetDependentsAsync",
            "GetEntitlementAsync", "GetFixPromptAsync", "GetGroundingAsync", "GetLayoutAsync",
            "GetLineageAsync", "GetModelFingerprintAsync", "GetModelGraphAsync", "GetModelObjectsAsync",
            "GetObjectAsync", "GetObjectPropertiesAsync", "GetOpCatalogAsync", "GetOrientationAsync",
            "GetPipelineStagesAsync", "GetPlanAsync", "GetStageItemsAsync", "GetVerifiedModeAsync",
            "GetWorkflowRunAsync", "GitStatusAsync", "HarnessReportAsync", "ImpactAssessmentAsync",
            "ImpactOfAsync", "LabelConnectionAsync", "LintDaxAsync", "ListAccountProfilesAsync",
            "ListColumnsAsync", "ListConnectionHistoryAsync", "ListConnectionsAsync", "ListDeploymentPipelinesAsync",
            "ListFormatTemplatesAsync", "ListFunctionsAsync", "ListHistoryCheckpointsAsync", "ListInterviewQuestionsAsync",
            "ListInterviewSeedsAsync", "ListLocalInstancesAsync", "ListMeasuresAsync", "ListPendingApprovalsAsync",
            "ListPrimerSuggestionsAsync", "ListReferenceTreeAsync", "ListRefreshTypesAsync", "ListReportScopeAsync",
            "ListReportsAsync", "ListRestorePointsAsync", "ListSqlSourceUsageAsync", "ListSqlSourcesAsync", "ListTreeAsync",
            "ListValueHistoryAsync", "ListVerifiedEditsAsync", "ListWaiversAsync", "ListWorkspacesAsync",
            "LoadBpaRulesAsync", "LoadReadinessRulesAsync", "MakeAiReadyAsync", "OpenAsync",
            "OpenLiveAsync", "OpenLocalAsync", "OptimizeMeasureAsync", "PivotMeasureAsync",
            "PrepareWorkingCopyAsync", "PreviewDeployAsync", "PreviewTableAsync", "ProbeAuthPrerequisitesAsync",
            "ProbeConnectionAccountsAsync", "ProbeMeasureAsync", "ProfileDaxAsync", "ProposePlanAsync",
            "ProposeReplaceAsync", "PublishActivityAsync", "PullAgentHealthAsync", "PurgeRestorePointsAsync",
            "RedoAsync", "RefreshPartitionAsync", "RejectPrimerSuggestionAsync", "RememberXmlaConnectionAsync",
            "RemoveSafeObjectsAsync", "RenameDisplayFolderAsync", "RenameObjectAsync", "ReplaceInObjectAsync",
            "ResetBpaRulesAsync", "ResetReadinessRulesAsync", "RestoreHistoryCheckpointAsync", "RollbackPushAsync",
            "RunDaxAsync", "RunDmvAsync", "RunInterviewAsync", "SaveAsync",
            "SaveLayoutAsync", "SaveSqlSourceAsync", "ScriptObjectsAsync", "SearchModelAsync",
            "SessionInfoAsync", "SetAgentPolicyCellAsync", "SetAgentPolicyEnabledAsync", "SetAgentPolicyPresetAsync",
            "SetAiDataSchemaAsync", "SetAiInstructionsAsync", "SetColumnDataTypeAsync", "SetColumnMetadataAsync",
            "SetCompatibilityLevelAsync", "SetConnectionWorkingFolderAsync", "SetDaxAsync", "SetDefaultAccountProfileAsync",
            "SetDescriptionAsync", "SetDisplayFolderAsync", "SetMeasureFormatAsync", "SetMeasureFormatExpressionAsync",
            "SetObjectPropertiesAsync", "SetObjectPropertyAsync", "SetPlanItemAsync", "SetPublishDestinationAsync",
            "SetRelationshipAsync", "SetRelationshipCardinalityAsync", "SetReportScopeAsync", "SetSynonymsAsync",
            "SetVerifiedModeAsync", "TestSqlSourceAsync", "UndoAsync", "UnusedObjectsAsync",
            "UnwaiveFindingAsync", "ValidateDaxAsync", "ValidateRuleAsync", "VerifyEquivalenceAsync",
            "VertiPaqScanAsync", "WaiveFindingAsync",
        };

        private sealed class Entry
        {
            public ProFeature Feature;
            public string Tab;
        }

        private static readonly Dictionary<string, Entry> ByOperation = Build(g => g.Operations);
        private static readonly Dictionary<string, Entry> ByRpcMethod = Build(g => g.RpcMethods);
        private static readonly Dictionary<string, Entry> ByEngineMethod = Build(g => g.EngineMethods);

        private static Dictionary<string, Entry> Build(Func<Group, string[]> pick)
        {
            var d = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var g in Groups)
            {
                var e = new Entry { Feature = g.Feature, Tab = g.Tab };
                foreach (var n in pick(g)) d.Add(n, e);
            }
            return d;
        }

        /// <summary>The feature and tab for an IEngine method, or false when it is free.</summary>
        public static bool TryEngineMethod(string method, out ProFeature feature, out string tab)
            => Try(ByEngineMethod, method, out feature, out tab);

        /// <summary>The feature and tab for an MCP operation name, or false when it is free.</summary>
        public static bool TryOperation(string operation, out ProFeature feature, out string tab)
            => Try(ByOperation, operation, out feature, out tab);

        /// <summary>The feature and tab for an RPC method name, or false when it is free.</summary>
        public static bool TryRpcMethod(string method, out ProFeature feature, out string tab)
            => Try(ByRpcMethod, method, out feature, out tab);

        private static bool Try(Dictionary<string, Entry> d, string key, out ProFeature feature, out string tab)
        {
            if (key != null && d.TryGetValue(key, out var e)) { feature = e.Feature; tab = e.Tab; return true; }
            feature = default; tab = null; return false;
        }

        /// <summary>Classified means named exactly once, as Pro or as free. The completeness tests assert this for
        /// every name on both doors and for every IEngine method.</summary>
        public static bool IsOperationClassified(string operation)
            => operation != null && (ByOperation.ContainsKey(operation) ^ FreeOperations.Contains(operation));

        public static bool IsRpcMethodClassified(string method)
            => method != null && (ByRpcMethod.ContainsKey(method) ^ FreeRpcMethods.Contains(method));

        public static bool IsEngineMethodClassified(string method)
            => method != null && (ByEngineMethod.ContainsKey(method) ^ FreeEngineMethods.Contains(method));

        /// <summary>Every Pro operation name, for tests and for the surface inventory.</summary>
        public static IReadOnlyCollection<string> ProOperations => ByOperation.Keys;
        public static IReadOnlyCollection<string> ProRpcMethods => ByRpcMethod.Keys;
        public static IReadOnlyCollection<string> ProEngineMethods => ByEngineMethod.Keys;

        /// <summary>The Pro operation names for one feature.</summary>
        public static IReadOnlyCollection<string> OperationsFor(ProFeature feature)
            => ByOperation.Where(kv => kv.Value.Feature == feature).Select(kv => kv.Key).ToArray();
    }
}
