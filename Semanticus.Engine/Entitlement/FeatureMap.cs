using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>Which operation belongs to which Pro feature, and which are free. Every name either maps to a
    /// feature or appears in an explicit free list; there is no third state, and
    /// <c>FeatureMapCompletenessTests</c> fails if one ever appears.
    ///
    /// The paid set went from 143 operations to 31 on 22 September 2026, when Kane set the line "free is the
    /// tool, Pro is the tool that works for you". Advanced modelling, Validation, Reports, Model Spec, Docs,
    /// Model notes, Power Query and the advanced half of Published all became free, and the 112 operations
    /// behind them moved to the free lists below. What is left is Workflows (30) and the ask box (1).
    ///
    /// Three keys, one answer. The GATE keys on the IEngine method, because the two doors do not agree on names:
    /// this engine exposes 327 MCP operations and 328 RPC methods, and the same implementation is reached under
    /// different names by each (list_table_mappings -&gt; listTableMappings -&gt; ListTableSourceMappingsAsync). The
    /// operation and RPC tables exist so a test can walk every wire name through to its method, and so a door can
    /// answer "is this Pro?" without calling it.
    ///
    /// The Tab is the tab a person would open, not the enum name, so a refusal reads like the product rather than
    /// like the code. Generated from the measured door surfaces on 2026-09-15; docs/redesign/free-pro-matrix.md
    /// records the line as it stood that day, which is three features wider than this file now is.</summary>
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
            new Group(ProFeature.Batch, "Changing many plan rows in one call",
                new[] { "plan_rows" }, new[] { "planRows" }, new[] { "PlanRowsAsync" }),
            new Group(ProFeature.Batch, "Unwaiving many findings in one call",
                new[] { "unwaive_findings" }, new[] { "unwaiveFindings" }, new[] { "UnwaiveFindingsAsync" }),
            new Group(ProFeature.Batch, "Fixing many findings in one call",
                new[] { "apply_fixes" }, new[] { "applyFixes" }, new[] { "ApplyFixesAsync" }),
            new Group(ProFeature.Batch, "Duplicating many objects in one call",
                new[] { "duplicate_objects" }, new[] { "duplicateObjects" }, new[] { "DuplicateObjectsAsync" }),
            new Group(ProFeature.Batch, "Writing many doc sections in one call",
                new[] { "set_doc_sections" }, new[] { "setDocSections" }, new[] { "SetDocSectionsAsync" }),
            new Group(ProFeature.Batch, "Changing many perspective cells in one call",
                new[] { "set_perspective_members" }, new[] { "setPerspectiveMembers" }, new[] { "SetPerspectiveMembersAsync" }),
            new Group(ProFeature.Batch, "Changing many permission cells in one call",
                new[] { "set_object_permissions" }, new[] { "setObjectPermissions" }, new[] { "SetObjectPermissionsAsync" }),
            new Group(ProFeature.Batch, "Changing many plan items in one call",
                new[] { "set_plan_items" }, new[] { "setPlanItems" }, new[] { "SetPlanItemsAsync" }),
            new Group(ProFeature.Batch, "Changing many plan items in one call",
                new[] { "add_plan_items" }, new[] { "addPlanItems" }, new[] { "AddPlanItemsAsync" }),
            new Group(ProFeature.Batch, "Changing many plan items in one call",
                new[] { "edit_start_plans" }, new[] { "editStartPlans" }, new[] { "EditStartPlansAsync" }),
            new Group(ProFeature.Batch, "Creating many measures in one call",
                new[] { "create_measures" }, new[] { "createMeasures" }, new[] { "CreateMeasuresAsync" }),
            new Group(ProFeature.Batch, "Editing many objects in one call",
                new[] { "edit_objects" }, new[] { "editObjects" }, new[] { "EditObjectsAsync" }),
            // Kane, 11 Oct: Pro writes many waivers or words in one call; the single tools stay free.
            new Group(ProFeature.Batch, "Waiving many findings in one call",
                new[] { "waive_findings" }, new[] { "waiveFindings" }, new[] { "WaiveFindingsAsync" }),
            new Group(ProFeature.Batch, "Writing words for many objects in one call",
                new[] { "write_words" }, new[] { "writeWords" }, new[] { "WriteWordsAsync" }),
            // Kane, 22 Sep: ask is Jev, so it is Pro. One operation and one group of its own, because the box
            // is not a tab: it sits on the Overview, which is free, and only the box inside it is not. The tab
            // name is therefore what the box is CALLED on that page, so the refusal reads "Ask about this
            // model is a Semanticus Pro feature" rather than naming a page that is free to open.
            new Group(ProFeature.Ask, "Ask about this model",
                new[] { "route_ask" },
                new[] { "routeAsk" },
                new[] { "RouteAskAsync" }),
            // Kane, 26 Sep: "Anything involving Jev is always Pro only." Ask your data is Jev reading a data question,
            // and the parts builder and the question history go with it, so all of it is Pro and a free session is
            // refused before anything is read, matched or sent.
            new Group(ProFeature.AskData, "Ask your data",
                new[] { "ask_data", "list_data_questions", "pick_chart_kind" },
                new[] { "askData", "listDataQuestions", "pickChartKind" },
                new[] { "AskDataAsync", "ListDataQuestionsAsync", "PickChartKindAsync" }),
            new Group(ProFeature.Workflows, "Workflows",
                new[]   // 35 operations: 30, the composer's four draft ops (lane L4, Kane 24 Sep: draft ops stay Pro), and describe_workflow_steps (Kane's UAT, 25 Sep)
                {
                    "compose_task", "get_draft", "edit_draft", "run_draft", "describe_workflow_steps",
                    // Held drafts and repeats (Kane, 10 Oct 2026: the assistant may do what the person does, except the Permissions page).
                    "approve_draft", "continue_draft", "add_plan_repeat", "set_plan_repeat",
                    "abort_workflow", "activate_workflow_profile", "check_workflow", "delete_workflow",
                    "delete_workflow_template", "edit_workflow_document", "export_workflow_evidence", "get_workflow",
                    "get_workflow_document", "get_workflow_enforcement", "get_workflow_layout", "get_workflow_policy",
                    "get_workflow_template", "instantiate_workflow_template", "list_workflow_profiles", "list_workflow_templates",
                    "list_workflows", "preview_workflow_edit", "replay_check_workflow", "save_workflow",
                    "save_workflow_layout", "save_workflow_template", "set_workflow_activation", "set_workflow_binding",
                    "set_workflow_enabled", "set_workflow_enforcement", "skip_workflow_step", "start_workflow",
                    "submit_workflow_step", "upgrade_workflow",
                },
                new[]   // the same 35 through their RPC names
                {
                    "composeTask", "getDraft", "editDraft", "runDraft", "describeWorkflowSteps",
                    "approveDraft", "continueDraft", "addPlanRepeat", "setPlanRepeat",
                    "abortWorkflow", "activateWorkflowProfile", "checkWorkflow", "deleteWorkflow",
                    "deleteWorkflowTemplate", "editWorkflowDocument", "exportWorkflowEvidence", "getWorkflow",
                    "getWorkflowDocument", "getWorkflowEnforcement", "getWorkflowLayout", "getWorkflowPolicy",
                    "getWorkflowTemplate", "instantiateWorkflowTemplate", "listWorkflowProfiles", "listWorkflowTemplates",
                    "listWorkflows", "previewWorkflowEdit", "replayCheckWorkflow", "saveWorkflow",
                    "saveWorkflowLayout", "saveWorkflowTemplate", "setWorkflowActivation", "setWorkflowBinding",
                    "setWorkflowEnabled", "setWorkflowEnforcement", "skipWorkflowStep", "startWorkflow",
                    "submitWorkflowStep", "upgradeWorkflow",
                },
                new[]   // the same 35 as IEngine methods, the gate key, plus four that are person-only, on the human-only
                        // target with no MCP op or EngineRpcTarget entry: Approve and run on a held draft, Continue past a
                        // stop in a draft (lane author-gaps, B9), and the two repeat controls at the end (38)
                {
                    "ComposeTaskAsync", "GetDraftAsync", "EditDraftAsync", "RunDraftAsync", "ApproveHeldDraftAsync", "ContinueDraftAsync", "DescribeWorkflowStepsAsync",
                    "AbortWorkflowAsync", "ActivateWorkflowProfileAsync", "CheckWorkflowAsync", "DeleteWorkflowAsync",
                    "DeleteWorkflowTemplateAsync", "EditWorkflowDocumentAsync", "ExportWorkflowEvidenceAsync", "GetWorkflowAsync",
                    "GetWorkflowDocumentAsync", "GetWorkflowEnforcementAsync", "GetWorkflowLayoutAsync", "GetWorkflowPolicyAsync",
                    "GetWorkflowTemplateAsync", "InstantiateWorkflowTemplateAsync", "ListWorkflowProfilesAsync", "ListWorkflowTemplatesAsync",
                    "ListWorkflowsAsync", "PreviewWorkflowEditAsync", "ReplayCheckWorkflowAsync", "SaveWorkflowAsync",
                    "SaveWorkflowLayoutAsync", "SaveWorkflowTemplateAsync", "SetWorkflowActivationAsync", "SetWorkflowBindingAsync",
                    "SetWorkflowEnabledAsync", "SetWorkflowEnforcementAsync", "SkipWorkflowStepAsync", "StartWorkflowAsync",
                    "SubmitWorkflowStepAsync", "UpgradeWorkflowAsync",
                    // Repeat cards (Plan at the heart, lane RP, contract 1.7): creating and changing a repeat is Pro,
                    // in the Workflows group. Person-only, so there is no MCP operation or EngineRpcTarget entry for
                    // either. These two, ApproveHeldDraftAsync and ContinueDraftAsync are the only names this group has on one door and
                    // not the others.
                    "AddPlanRepeatAsync", "SetPlanRepeatAsync",
                }),
        };

        /// <summary>The MCP operation names that are free. Everything the agent door exposes is here or in a Group.</summary>
        public static readonly IReadOnlyCollection<string> FreeOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "accept_primer_suggestion", "add_interview_question", "add_plan_item", "ai_readiness_scan",
            "analyze_cloud_reports", "analyze_reports", "apply_dax_script", "apply_fix",
            "apply_model_diff", "apply_plan", "apply_safe_fixes", "apply_tmdl",
            "benchmark_cold_warm", "benchmark_dax", "blame_value", "bpa_scan", "cancel_query", "capture_query_plan", "check_reports",
            "cherry_pick", "clear_cache", "clear_plan", "connect_local",
            "connect_xmla", "connection_context", "connection_status", "create_calculated_column",
            "create_calculated_table", "create_column", "create_directlake_table", "create_hierarchy",
            "create_history_checkpoint", "create_import_table", "create_measure", "create_model",
            "create_relationship", "create_table", "delete_interview_question", "delete_object",
            "delete_objects", "delete_sql_source", "deploy_gate", "deploy_live",
            "deploy_stage", "deployment_history", "disconnect", "dry_run",
            "duplicate_object", "create_linguistic_schema", "evaluate_and_log", "explain_value",
            "export_verified_edits", "export_vpax", "forget_connection", "get_agent_policy",
            "get_ai_instructions", "get_custom_rules", "get_dax", "get_dependencies",
            "get_consent", "get_decision_budget", "get_decision_points", "get_entitlement",
            "get_fix_prompt", "get_grounding", "get_layout",
            "get_lineage", "get_model_facts", "get_model_fingerprint", "get_model_graph", "get_model_objects",
            "get_model_summary", "get_object", "get_op_catalog", "get_pipeline_stages",
            "propose_measure_defaults", "set_consent", "set_decision_point_enabled", "withdraw_consent",
            // The living plan (Jev M2). Every operation is FREE: a free session sees the whole plan, with the
            // same items, the same reasons and the same exact changes, in rule order. What Pro buys is the
            // ORDER and the routing, and that is gated inside the decision points (plan.rank, ask.route), not
            // here. Gating the operations would hide the gaps from the people most likely to have them.
            "get_model_plan", "get_words_queue", "skip_model_plan_item", "unskip_model_plan_item", "model_plan_action",
            // History inside the Plan (Plan-heart contract 13): free, like undo itself.
            "get_plan_history",
            "resolve_file_recovery",
            // Bulk apply is free in the map for the same reason model_plan_action is: the Pro line is drawn inside
            // the operation, where it can answer with the plan and record the refusal. It refuses every free
            // session, because applying several items at once is running the plan (F-238).
            "apply_plan_items",
            "get_next_actions",
            // The Studio wire (1.4, Claude drives): the assistant says what it is doing and what it would do next. Free on every tier.
            "show_in_studio", "suggest_next",
            // The target (the Fix milestone). Free for a further reason of its own: a target is a sentence
            // the person wrote about their own model, so charging for it would be charging them to write
            // down what they want.
            "add_plan_target", "remove_plan_target",
            // The Plan page's controls (Kane, 10 Oct 2026): free, like asking for a task.
            "add_plan_task", "pause_plan_item", "set_plan_doer", "move_plan_task_up", "set_assistant_queue", "set_plan_task_note",
            "remove_plan_row", "restore_plan_row", "refresh_plan", "delete_plan_repeat", "approve_start_plan",
            // The queue (Plan at the heart, lane E3, contract 3.3 and 13): free on every tier, because asking for a
            // task runs no Jev. The composer's draft ops stay in the Workflows Pro group.
            "list_assistant_requests", "claim_request", "complete_request", "decline_request",
            "get_plan", "get_properties", "get_reference_tree", "get_stage_items",
            "get_verified_mode", "get_workflow_run", "git_status", "harness_report",
            "impact_assessment", "label_connection", "lint_dax",
            "list_account_profiles", "list_columns", "list_connection_history", "list_connections",
            "list_deployment_pipelines", "list_format_templates", "list_functions", "list_history_checkpoints",
            "list_interview_questions", "list_interview_seeds", "list_local_instances", "list_measures",
            "list_objects", "list_pending_approvals", "list_primer_suggestions", "list_refresh_types",
            "list_model_sql_endpoints", "list_report_scope", "list_reports", "list_restore_points", "list_sql_source_usage", "list_sql_sources",
            "list_value_history", "list_verified_edits", "list_waivers", "list_workspace_models", "list_workspace_sql_endpoints", "list_workspaces",
            "load_rules", "make_model_ai_ready", "model_diff",
            "model_overview", "open_live", "open_local",
            "open_model", "optimize_measure", "pivot_measure", "prepare_working_copy",
            "preview_deploy", "preview_table", "probe_auth_prerequisites", "probe_connection_accounts",
            "probe_measure", "profile_dax", "propose_plan", "propose_replace",
            "purge_restore_points", "redo_change", "refresh_partition", "reject_primer_suggestion",
            "remember_xmla_connection", "remove_safe_objects", "rename_display_folder", "rename_object",
            "replace_in_object", "reset_rules", "restore_history_checkpoint",
            "rollback_push", "run_dax", "run_dmv", "run_interview",
            "save_layout", "save_model", "save_sql_source", "script_objects",
            "search_model", "set_ai_data_schema", "set_ai_instructions", "set_column_data_type",
            "set_column_hidden", "set_compatibility_level", "set_data_category", "set_default_account_profile", "set_description",
            "set_display_folder", "set_measure_format", "set_measure_format_expression", "set_plan_item",
            "set_model_sql_endpoint", "publish_new_model", "set_properties", "set_property", "set_publish_destination", "set_relationship_active",
            "set_relationship_cardinality", "set_relationship_crossfilter", "set_report_scope", "set_sort_by_column",
            "set_summarize_by", "set_synonyms", "set_verified_mode", "test_sql_source",
            "undo_change", "unused_objects", "unwaive_finding", "update_measure",
            "validate_dax", "validate_rule", "verify_dax_equivalence", "vpaq_scan",
            "waive_finding",
            // FREE SINCE 22 SEPTEMBER 2026. Kane: "free is the tool, Pro is the tool that works for you."
            // Eight tabs left the paid line in one decision and took 112 operations with them, grouped below
            // by the tab they used to belong to so the move can be read and reversed. Nothing about them
            // changed except who may call them.
            // Model Spec
            "autogenerate_spec_from_fabric", "autogenerate_spec_from_model", "build_model_from_spec", "clear_spec",
            "get_spec", "load_spec", "save_spec", "set_spec",
            // The spec-first start (M11): free on every tier. A free session gets the plan, its own assistant's words and
            // ONE build; Jev deciding for the person is the paid part, and that line is drawn inside the engine (the decide
            // loop asks nothing on a free session), not here. Approving the plan is person-only: no operation, no RPC entry.
            "start_model_from_sql", "get_start_plan", "edit_start_plan", "grant_start_disclosure",
            "get_start_words", "write_start_words", "build_start_plan", "discard_start_plan",
            // Advanced Modelling
            "create_calculation_group", "create_calculation_item", "create_field_parameter", "create_function",
            "create_perspective", "create_role", "daxlib_install", "daxlib_list_installed",
            "daxlib_package_info", "daxlib_search", "daxlib_uninstall", "daxlib_versions",
            "define_calendar", "define_calendar_from_template", "delete_calendar", "delete_role",
            "generate_date_table", "generate_time_intelligence", "get_perspectives", "list_calculation_groups",
            "list_calendars", "list_roles", "mark_date_table", "set_calc_group_precedence",
            "set_calc_item_format_string", "set_column_ols", "set_perspective_member", "set_role_member",
            "set_role_permission", "set_table_ols", "set_table_permission", "tag_calendar_column",
            // Power Query
            "apply_schema_update", "create_data_source", "create_named_expression", "diff_schema",
            "get_incremental_refresh_policy", "get_named_expression", "get_partition_m", "get_source_schema",
            "list_named_expressions", "list_partitions", "remove_incremental_refresh_policy", "set_incremental_refresh_policy",
            "set_partition_m", "update_named_expression",
            // Docs
            "get_doc_model", "get_doc_outline", "get_doc_section", "set_doc_section",
            // Model notes
            "add_insight", "approve_insight", "delete_insight", "downvote_insight",
            "edit_insight", "get_model_primer", "list_insights", "purge_knowledge",
            "recall_experience", "set_model_primer", "upvote_insight",
            // Tests
            "capture_baseline", "clear_table_source_mapping", "compare_baseline", "delete_test",
            "export_test_report", "get_test_run", "get_unmatched_rows", "list_table_mappings",
            "list_test_runs", "list_tests", "reconcile_measure", "record_test_run",
            "review_reconcile_mapping", "run_tests", "save_test", "set_table_source_mapping",
            "try_test",
            // Saved reports
            "get_evidence", "list_evidence", "save_evidence",
            // Source control
            "git_branch", "git_checkout", "git_clone", "git_commit",
            "git_diff", "git_log", "git_pull", "git_push",
            // Fabric Git
            "fabric_git_commit", "fabric_git_connect", "fabric_git_connection", "fabric_git_disconnect",
            "fabric_git_status", "fabric_git_update",
            // CI and CD publish
            "cicd_generate", "cicd_publish",
            // Data agent
            "create_data_agent", "delete_data_agent", "generate_data_agent_config", "get_data_agent",
            "list_data_agents", "publish_data_agent", "update_data_agent",
        };

        /// <summary>The RPC method names that are free, including the six with no MCP twin
        /// (cancelDax, clearReferenceBinding, pullAgentHealth, pullForeignChanges, setConnectionWorkingFolder,
        /// setDefaultAccountProfile).</summary>
        public static readonly IReadOnlyCollection<string> FreeRpcMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "resolveFileRecovery",
            "acceptPrimerSuggestion", "addInterviewQuestion", "addPlanItem", "aiReadinessScan",
            "aiReadinessScanLive", "analyzeCloudReports", "analyzeReports", "applyDaxScript",
            "applyDiff", "applyFix", "applyPlan", "applySafeFixes",
            "applyTmdlScript", "benchmarkColdWarm", "benchmarkDax", "blameValue",
            "bpaFix", "bpaFixAll", "bpaGetFixPrompt", "bpaScan", "buildIdentity",
            "cancelDax", "captureQueryPlan", "checkReports", "cherryPick",
            "clearCache", "clearPlan", "clearReferenceBinding", "compareModels",
            "connectLocal", "connectXmla", "connectionContext", "connectionStatus",
            "createCalculatedColumn", "createCalculatedTable", "createColumn", "createDirectLakeTable",
            "createHierarchy", "createHistoryCheckpoint", "createImportTable", "createMeasure",
            "createModel", "createRelationship", "createTable", "deleteInterviewQuestion",
            "deleteObject", "deleteObjects", "deleteSqlSource", "deployGate",
            "deployLive", "deployStage", "deploymentHistory", "disconnect", "publishNewModel",
            "dryRunOp", "duplicateObject", "createLinguisticSchema", "evaluateAndLog",
            "explainValue", "exportVerifiedEdits", "exportVpax", "forgetConnection",
            "getAgentPolicy", "getAiInstructions", "getCustomRules", "getDax",
            "getDependencies", "getDependents", "getEntitlement", "getFixPrompt",
            "getGrounding", "getLayout", "getLineage", "getModelFacts", "getModelFingerprint",
            "getModelGraph", "getModelObjects", "getObject", "getObjectProperties",
            "getOpCatalog", "getOrientation", "getPipelineStages", "getPlan",
            "getConsent", "getDecisionBudget", "getDecisionPoints",
            "proposeMeasureDefaults", "setConsent", "setDecisionPointEnabled", "withdrawConsent",
            "getModelPlan", "getWordsQueue", "skipModelPlanItem", "unskipModelPlanItem", "modelPlanAction", "applyPlanItems",
            "addPlanTarget", "removePlanTarget", "markPlanTargetDone",
            // The Plan page's controls (Kane, 10 Oct 2026): free, like asking for a task. Both doors reach them.
            "addPlanTask", "holdPlanItem", "setPlanItemDoer", "movePlanTaskUp", "setAssistantQueue", "setPlanTaskNote",
            "removePlanRow", "restorePlanRow", "refreshPlan", "deletePlanRepeat", "approveStartPlan",
            "listAssistantRequests", "claimRequest", "completeRequest", "declineRequest", "pullPlanOffer",
            "getNextActions",
            // The Studio wire (1.4). The driver report, setStudioDriver, is on HumanGovernanceRpcTarget and so not on this list.
            "showInStudio", "suggestNext", "getStudioNotes", "noteAgentCall",
            "getStageItems", "getVerifiedMode", "getWorkflowRun", "gitStatus",
            "harnessReport", "impactAssessment", "impactOf", "labelConnection",
            "lintDax", "listAccountProfiles", "listColumns", "listConnectionHistory",
            "listConnections", "listDeploymentPipelines", "listFormatTemplates", "listFunctions",
            "listHistoryCheckpoints", "listInterviewQuestions", "listInterviewSeeds", "listLocalInstances",
            "listMeasures", "listPendingApprovals", "listPrimerSuggestions", "listReferenceTree",
            "listRefreshTypes", "listReportScope", "listReports", "listRestorePoints",
            "listModelSqlEndpoints", "listSqlSourceUsage", "listSqlSources", "listTree", "listValueHistory", "listVerifiedEdits",
            "listWaivers", "listWorkspaceModels", "listWorkspaceSqlEndpoints", "listWorkspaces", "loadBpaRules", "loadReadinessRules",
            "makeAiReady", "open", "openLive", "openLocal",
            "optimizeMeasure", "pivotMeasure", "prepareWorkingCopy", "previewDeploy",
            "previewTable", "probeAuthPrerequisites", "probeConnectionAccounts", "probeMeasure",
            "profileDax", "proposePlan", "proposeReplace", "publishActivity",
            "pullAgentHealth", "pullForeignChanges", "purgeRestorePoints", "redo", "refreshPartition",
            "rejectPrimerSuggestion", "rememberXmlaConnection", "removeSafeObjects", "renameDisplayFolder",
            "renameObject", "replaceInObject", "resetBpaRules", "resetReadinessRules",
            "restoreHistoryCheckpoint", "rollbackPush", "runDax", "runDmv",
            "runInterview", "save", "saveLayout", "saveSqlSource",
            "scriptObjects", "searchModel", "searchModelEx", "sessionInfo",
            "setAiDataSchema", "setAiInstructions", "setColumnDataType", "setColumnMetadata",
            "setCompatibilityLevel", "setConnectionWorkingFolder", "setDax", "setDefaultAccountProfile",
            "setDescription", "setDisplayFolder", "setMeasureFormat", "setMeasureFormatExpression",
            "setModelSqlEndpoint", "setObjectProperties", "setObjectProperty", "setPlanItem", "setPublishDestination",
            "setRelationship", "setRelationshipCardinality", "setReportScope", "setSynonyms",
            "setVerifiedMode", "testSqlSource", "undo", "unusedObjects",
            "getPlanHistory",   // History inside the Plan (Plan-heart E4): free
            "unwaiveFinding", "validateDax", "validateRule", "verifyEquivalence",
            "vertiPaqScan", "waiveFinding",
            // FREE SINCE 22 SEPTEMBER 2026. Kane: "free is the tool, Pro is the tool that works for you."
            // Eight tabs left the paid line in one decision and took 112 operations with them, grouped below
            // by the tab they used to belong to so the move can be read and reversed. Nothing about them
            // changed except who may call them.
            // Model Spec
            "autogenerateSpecFromFabric", "autogenerateSpecFromModel", "buildModelFromSpec", "clearSpec",
            "getSpec", "loadSpec", "saveSpec", "setSpec",
            // The spec-first start (M11), the same eight under their RPC names.
            "startModelFromSql", "getStartPlan", "editStartPlan", "grantStartDisclosure",
            "getStartWords", "writeStartWords", "buildStartPlan", "discardStartPlan",
            // Advanced Modelling
            "createCalculationGroup", "createCalculationItem", "createFieldParameter", "createFunction",
            "createPerspective", "createRole", "daxLibInstall", "daxLibListInstalled",
            "daxLibPackageInfo", "daxLibSearch", "daxLibUninstall", "daxLibVersions",
            "defineCalendar", "defineCalendarFromTemplate", "deleteCalendar", "deleteRole",
            "generateDateTable", "generateTimeIntelligence", "getPerspectives", "listCalculationGroups",
            "listCalendars", "listRoles", "markDateTable", "setCalcGroupPrecedence",
            "setCalcItemFormatString", "setColumnObjectPermission", "setPerspectiveMember", "setRoleMember",
            "setRolePermission", "setTableObjectPermission", "setTablePermission", "tagCalendarColumn",
            // Power Query
            "applySchemaUpdate", "createDataSource", "createNamedExpression", "diffSchema",
            "getIncrementalRefreshPolicy", "getNamedExpression", "getPartitionM", "getSourceSchema",
            "listNamedExpressions", "listPartitions", "removeIncrementalRefreshPolicy", "setIncrementalRefreshPolicy",
            "setPartitionM", "updateNamedExpression",
            // Docs
            "getDocModel", "getDocOutline", "getDocSection", "setDocSection",
            // Model notes
            "addInsight", "approveInsight", "deleteInsight", "downvoteInsight",
            "editInsight", "getPrimer", "listInsights", "purgeKnowledge",
            "recallExperience", "setPrimer", "upvoteInsight",
            // Tests
            "captureBaseline", "clearTableSourceMapping", "compareBaseline", "deleteTest",
            "exportTestReport", "getTestRun", "getUnmatchedRows", "listTableMappings",
            "listTestRuns", "listTests", "reconcileMeasure", "recordTestRun",
            "reviewReconcileMapping", "runTests", "saveTest", "setTableSourceMapping",
            "tryTest",
            // Saved reports
            "getEvidence", "listEvidence", "saveEvidence",
            // Source control
            "gitBranch", "gitCheckout", "gitClone", "gitCommit",
            "gitDiff", "gitLog", "gitPull", "gitPush",
            // Fabric Git
            "fabricGitCommit", "fabricGitConnect", "fabricGitConnection", "fabricGitDisconnect",
            "fabricGitStatus", "fabricGitUpdate",
            // CI and CD publish
            "cicdGenerate", "cicdPublish",
            // Data agent
            "createDataAgent", "deleteDataAgent", "generateDataAgentConfig", "getDataAgent",
            "listDataAgents", "publishDataAgent", "updateDataAgent",
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
            "DenyAgentActionAsync", "DeployGateAsync", "DeployLiveAsync", "DeployStageAsync", "PublishNewModelAsync",
            "DeploymentHistoryAsync", "DisconnectAsync", "DryRunOpAsync", "DuplicateObjectAsync",
            "CreateLinguisticSchemaAsync", "EvaluateAndLogAsync", "ExplainValueAsync", "ExportVerifiedEditsAsync",
            "ExportVpaxAsync", "ForgetConnectionAsync", "GetAgentPolicyAsync", "GetAiInstructionsAsync",
            "GetConsentAsync", "GetDecisionBudgetAsync", "GetDecisionPointsAsync",
            "ProposeMeasureDefaultsAsync", "SetConsentAsync", "SetDecisionPointEnabledAsync", "WithdrawConsentAsync",
            "GetModelPlanAsync", "GetWordsQueueAsync", "SkipModelPlanItemAsync", "UnskipModelPlanItemAsync", "ModelPlanActionAsync", "ApplyPlanItemsAsync",
            "AddPlanTargetAsync", "RemovePlanTargetAsync", "MarkPlanTargetDoneAsync",
            // The Plan controls (Plan at the heart, contract 3.3 and 13): free on every tier, because asking for a task
            // runs no Jev. On both doors since Kane's decision of 10 Oct 2026.
            "AddPlanTaskAsync", "HoldPlanItemAsync", "SetPlanItemDoerAsync", "MovePlanTaskUpAsync",
            "SetAssistantQueueAsync", "SetPlanTaskNoteAsync",
            // The queue (lane E3): the four agent ops and the fold's pull, and the person's side of the same queue
            // (their own claim, Mark done and Could not), which lives on HumanGovernanceRpcTarget.
            "ListAssistantRequestsAsync", "ClaimAssistantRequestAsync", "CompleteAssistantRequestAsync",
            "DeclineAssistantRequestAsync", "PullPlanOfferAsync",
            "ClaimPlanTaskAsync", "CompletePlanTaskAsync", "DeclinePlanTaskAsync",
            // Deleting a repeat is free (lane RP), so a person who lost Pro can still clear one away.
            "DeletePlanRepeatAsync",
            // Remove a task or draft row, and its Undo (Kane's UAT, 25 Sep): free, like adding one.
            "RemovePlanRowAsync", "RestorePlanRowAsync", "RefreshPlanAsync",
            "GetNextActionsAsync",
            // The Studio wire (1.4): free on every tier. SetStudioDriverAsync is the Studio's own report, person-only like the Plan controls above.
            "ShowInStudioAsync", "SuggestNextAsync", "GetStudioNotesAsync", "NoteAgentCallAsync", "SetStudioDriverAsync",
            "GetCustomRulesAsync", "GetDaxAsync", "GetDependenciesAsync", "GetDependentsAsync",
            "GetEntitlementAsync", "GetFixPromptAsync", "GetGroundingAsync", "GetLayoutAsync",
            "GetLineageAsync", "GetModelFactsAsync", "GetModelFingerprintAsync", "GetModelGraphAsync", "GetModelObjectsAsync",
            "GetObjectAsync", "GetObjectPropertiesAsync", "GetOpCatalogAsync", "GetOrientationAsync",
            "GetPipelineStagesAsync", "GetPlanAsync", "GetStageItemsAsync", "GetVerifiedModeAsync",
            "GetWorkflowRunAsync", "GitStatusAsync", "HarnessReportAsync", "ImpactAssessmentAsync",
            "ImpactOfAsync", "LabelConnectionAsync", "LintDaxAsync", "ListAccountProfilesAsync",
            "ListColumnsAsync", "ListConnectionHistoryAsync", "ListConnectionsAsync", "ListDeploymentPipelinesAsync",
            "ListFormatTemplatesAsync", "ListFunctionsAsync", "ListHistoryCheckpointsAsync", "ListInterviewQuestionsAsync",
            "ListInterviewSeedsAsync", "ListLocalInstancesAsync", "ListMeasuresAsync", "ListPendingApprovalsAsync",
            "ListPrimerSuggestionsAsync", "ListReferenceTreeAsync", "ListRefreshTypesAsync", "ListReportScopeAsync",
            "ListModelSqlEndpointsAsync", "ListReportsAsync", "ListRestorePointsAsync", "ListSqlSourceUsageAsync", "ListSqlSourcesAsync", "ListTreeAsync",
            "ListValueHistoryAsync", "ListVerifiedEditsAsync", "ListWaiversAsync", "ListWorkspaceModelsAsync", "ListWorkspaceSqlEndpointsAsync", "ListWorkspacesAsync",
            "LoadBpaRulesAsync", "LoadReadinessRulesAsync", "MakeAiReadyAsync", "OpenAsync",
            "OpenLiveAsync", "OpenLocalAsync", "OptimizeMeasureAsync", "PivotMeasureAsync",
            "PrepareWorkingCopyAsync", "PreviewDeployAsync", "PreviewTableAsync", "ProbeAuthPrerequisitesAsync",
            "ProbeConnectionAccountsAsync", "ProbeMeasureAsync", "ProfileDaxAsync", "ProposePlanAsync",
            "ProposeReplaceAsync", "PublishActivityAsync", "PullAgentHealthAsync", "PullForeignChangesAsync", "PurgeRestorePointsAsync",
            "RedoAsync", "RefreshPartitionAsync", "RejectPrimerSuggestionAsync", "RememberXmlaConnectionAsync",
            "RemoveSafeObjectsAsync", "RenameDisplayFolderAsync", "RenameObjectAsync", "ReplaceInObjectAsync",
            "ResetBpaRulesAsync", "ResetReadinessRulesAsync", "RestoreHistoryCheckpointAsync", "RollbackPushAsync",
            "RunDaxAsync", "RunDmvAsync", "RunInterviewAsync", "SaveAsync",
            "SaveLayoutAsync", "SaveSqlSourceAsync", "ScriptObjectsAsync", "SearchModelAsync",
            "SessionInfoAsync", "SetAgentPolicyCellAsync", "SetAgentPolicyEnabledAsync", "SetAgentPolicyPresetAsync",
            "SetAiDataSchemaAsync", "SetAiInstructionsAsync", "SetColumnDataTypeAsync", "SetColumnMetadataAsync",
            "SetCompatibilityLevelAsync", "SetConnectionWorkingFolderAsync", "SetDaxAsync", "SetDefaultAccountProfileAsync",
            "SetDescriptionAsync", "SetDisplayFolderAsync", "SetMeasureFormatAsync", "SetMeasureFormatExpressionAsync",
            "SetModelSqlEndpointAsync", "SetObjectPropertiesAsync", "SetObjectPropertyAsync", "SetPlanItemAsync", "SetPublishDestinationAsync",
            "SetRelationshipAsync", "SetRelationshipCardinalityAsync", "SetReportScopeAsync", "SetSynonymsAsync",
            "SetVerifiedModeAsync", "TestSqlSourceAsync", "UndoAsync", "UnusedObjectsAsync",
            "GetPlanHistoryAsync", "UndoToEntryAsync", "RedoToEntryAsync",   // Plan-heart E4: free, like undo
            "ResolveFileRecoveryAsync",
            "UnwaiveFindingAsync", "ValidateDaxAsync", "ValidateRuleAsync", "VerifyEquivalenceAsync",
            "VertiPaqScanAsync", "WaiveFindingAsync",
            // FREE SINCE 22 SEPTEMBER 2026. Kane: "free is the tool, Pro is the tool that works for you."
            // Eight tabs left the paid line in one decision and took 112 operations with them, grouped below
            // by the tab they used to belong to so the move can be read and reversed. Nothing about them
            // changed except who may call them.
            // Model Spec
            "AutogenerateSpecFromFabricAsync", "AutogenerateSpecFromModelAsync", "BuildModelFromSpecAsync", "ClearSpecAsync",
            "GetSpecAsync", "LoadSpecAsync", "SaveSpecAsync", "SetSpecAsync",
            // The spec-first start (M11), as IEngine methods, the gate key. Nine: ApproveStartPlanAsync joined the other eight
            // on both doors on 10 Oct 2026 (approve_start_plan).
            "StartModelFromSqlAsync", "GetStartPlanAsync", "EditStartPlanAsync", "GrantStartDisclosureAsync",
            "ApproveStartPlanAsync", "GetStartWordsAsync", "WriteStartWordsAsync", "BuildStartPlanAsync", "DiscardStartPlanAsync",
            // Advanced Modelling
            "CreateCalculationGroupAsync", "CreateCalculationItemAsync", "CreateFieldParameterAsync", "CreateFunctionAsync",
            "CreatePerspectiveAsync", "CreateRoleAsync", "DaxLibInstallAsync", "DaxLibListInstalledAsync",
            "DaxLibPackageInfoAsync", "DaxLibSearchAsync", "DaxLibUninstallAsync", "DaxLibVersionsAsync",
            "DefineCalendarAsync", "DefineCalendarFromTemplateAsync", "DeleteCalendarAsync", "DeleteRoleAsync",
            "GenerateDateTableAsync", "GenerateTimeIntelligenceAsync", "GetPerspectivesAsync", "ListCalculationGroupsAsync",
            "ListCalendarsAsync", "ListRolesAsync", "MarkDateTableAsync", "SetCalcGroupPrecedenceAsync",
            "SetCalcItemFormatStringAsync", "SetColumnObjectPermissionAsync", "SetPerspectiveMemberAsync", "SetRoleMemberAsync",
            "SetRolePermissionAsync", "SetTableObjectPermissionAsync", "SetTablePermissionAsync", "TagCalendarColumnAsync",
            // Power Query
            "ApplySchemaUpdateAsync", "CreateDataSourceAsync", "CreateNamedExpressionAsync", "DiffSchemaAsync",
            "GetIncrementalRefreshPolicyAsync", "GetNamedExpressionAsync", "GetPartitionMAsync", "GetSourceSchemaAsync",
            "ListNamedExpressionsAsync", "ListPartitionsAsync", "RemoveIncrementalRefreshPolicyAsync", "SetIncrementalRefreshPolicyAsync",
            "SetPartitionMAsync", "UpdateNamedExpressionAsync",
            // Docs
            "GetDocModelAsync", "GetDocOutlineAsync", "GetDocSectionAsync", "SetDocSectionAsync",
            // Model notes
            "AddInsightAsync", "ApproveInsightAsync", "DeleteInsightAsync", "DownvoteInsightAsync",
            "EditInsightAsync", "GetPrimerAsync", "ListInsightsAsync", "PurgeKnowledgeAsync",
            "RecallExperienceAsync", "SetPrimerAsync", "UpvoteInsightAsync",
            // Tests
            "CaptureBaselineAsync", "ClearTableSourceMappingAsync", "CompareBaselineAsync", "DeleteTestDefinitionAsync",
            "ExportTestReportAsync", "GetTestRunAsync", "GetUnmatchedRowsAsync", "ListTableSourceMappingsAsync",
            "ListTestDefinitionsAsync", "ListTestRunsAsync", "ReconcileMeasureAsync", "RecordTestRunAsync",
            "ReviewReconcileMappingAsync", "RunTestSuiteAsync", "SaveTestDefinitionAsync", "SetTableSourceMappingAsync",
            "TryTestAsync",
            // Saved reports
            "GetEvidenceAsync", "ListEvidenceAsync", "SaveEvidenceAsync",
            // Source control
            "GitBranchAsync", "GitCheckoutAsync", "GitCloneAsync", "GitCommitAsync",
            "GitDiffAsync", "GitLogAsync", "GitPullAsync", "GitPushAsync",
            // Fabric Git
            "FabricGitCommitAsync", "FabricGitConnectAsync", "FabricGitConnectionAsync", "FabricGitDisconnectAsync",
            "FabricGitStatusAsync", "FabricGitUpdateAsync",
            // CI and CD publish
            "CicdGenerateAsync", "CicdPublishAsync",
            // Data agent
            "CreateDataAgentAsync", "DeleteDataAgentAsync", "GenerateDataAgentConfigFromModelAsync", "GetDataAgentAsync",
            "ListDataAgentsAsync", "PublishDataAgentAsync", "UpdateDataAgentAsync",
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
