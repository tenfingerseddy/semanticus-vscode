using System.Collections.Generic;

namespace Semanticus.Engine
{
    // ============================================================================================
    // [T215] The ratified operation taxonomy: option A, "the tree", as shown on the decision page
    // Kane ratified 2026-08-01, no renames. Every operation lives in exactly ONE shelf under exactly
    // ONE question. This file is the single home of the mapping: get_op_catalog stamps each OpInfo
    // from here, so BOTH doors (the Studio picker and the MCP surface) read the same categories.
    //
    // The names are law. Changing a question or shelf string, or refiling an operation, is a
    // product decision for Kane, not a refactor; OpTaxonomyTests holds this file to the ratified
    // set verbatim. A NEW operation must be added here in the same commit that adds its
    // [McpServerTool] (the test fails the build with a named list otherwise) and its placement
    // should be flagged for ratification in the PR.
    // ============================================================================================
    public static class OpTaxonomy
    {
        /// <summary>The ten questions, in the ratified page's order (the picker's top level).</summary>
        public static readonly string[] Questions =
        {
            "Open a model and connect",
            "See what is in the model",
            "Change the model",
            "Make it better",
            "Prove it is right",
            "Track versions and compare",
            "Ship it out",
            "Teach the AI about this model",
            "Set the rules of the work",
            "Look things up",
        };

        /// <summary>op name -> (question, shelf). Grouped by question, then shelf, in page order.</summary>
        private static readonly Dictionary<string, (string Question, string Shelf)> Map =
            new Dictionary<string, (string, string)>(System.StringComparer.Ordinal)
        {
            // Open a model and connect > Connections and targets (20)
            { "connect_local", ("Open a model and connect", "Connections and targets") },
            { "connect_xmla", ("Open a model and connect", "Connections and targets") },
            { "connection_context", ("Open a model and connect", "Connections and targets") },
            { "connection_status", ("Open a model and connect", "Connections and targets") },
            { "create_model", ("Open a model and connect", "Connections and targets") },
            { "disconnect", ("Open a model and connect", "Connections and targets") },
            { "forget_connection", ("Open a model and connect", "Connections and targets") },
            { "label_connection", ("Open a model and connect", "Connections and targets") },
            { "list_account_profiles", ("Open a model and connect", "Connections and targets") },
            { "list_connection_history", ("Open a model and connect", "Connections and targets") },
            { "list_connections", ("Open a model and connect", "Connections and targets") },
            { "list_local_instances", ("Open a model and connect", "Connections and targets") },
            { "open_live", ("Open a model and connect", "Connections and targets") },
            { "open_local", ("Open a model and connect", "Connections and targets") },
            { "open_model", ("Open a model and connect", "Connections and targets") },
            { "prepare_working_copy", ("Open a model and connect", "Connections and targets") },
            { "probe_auth_prerequisites", ("Open a model and connect", "Connections and targets") },
            { "probe_connection_accounts", ("Open a model and connect", "Connections and targets") },
            { "remember_xmla_connection", ("Open a model and connect", "Connections and targets") },
            { "set_publish_destination", ("Open a model and connect", "Connections and targets") },
            // See what is in the model > Measures, DAX and queries (7)
            { "evaluate_and_log", ("See what is in the model", "Measures, DAX and queries") },
            { "get_dependencies", ("See what is in the model", "Measures, DAX and queries") },
            { "list_functions", ("See what is in the model", "Measures, DAX and queries") },
            { "list_measures", ("See what is in the model", "Measures, DAX and queries") },
            { "pivot_measure", ("See what is in the model", "Measures, DAX and queries") },
            { "run_dax", ("See what is in the model", "Measures, DAX and queries") },
            { "run_dmv", ("See what is in the model", "Measures, DAX and queries") },
            // See what is in the model > Reports and what uses this model (7)
            { "analyze_cloud_reports", ("See what is in the model", "Reports and what uses this model") },
            { "analyze_reports", ("See what is in the model", "Reports and what uses this model") },
            { "get_lineage", ("See what is in the model", "Reports and what uses this model") },
            { "impact_assessment", ("See what is in the model", "Reports and what uses this model") },
            { "impact_of", ("See what is in the model", "Reports and what uses this model") },
            { "list_reports", ("See what is in the model", "Reports and what uses this model") },
            { "unused_objects", ("See what is in the model", "Reports and what uses this model") },
            // See what is in the model > Speed and size (7)
            { "benchmark_dax", ("See what is in the model", "Speed and size") },
            { "benchmark_dax_coldwarm", ("See what is in the model", "Speed and size") },
            { "capture_query_plan", ("See what is in the model", "Speed and size") },
            { "clear_cache", ("See what is in the model", "Speed and size") },
            { "export_vpax", ("See what is in the model", "Speed and size") },
            { "profile_dax", ("See what is in the model", "Speed and size") },
            { "vpaq_scan", ("See what is in the model", "Speed and size") },
            // See what is in the model > Tables, columns and hierarchies (5)
            { "get_model_objects", ("See what is in the model", "Tables, columns and hierarchies") },
            { "get_object", ("See what is in the model", "Tables, columns and hierarchies") },
            { "list_columns", ("See what is in the model", "Tables, columns and hierarchies") },
            { "list_objects", ("See what is in the model", "Tables, columns and hierarchies") },
            { "preview_table", ("See what is in the model", "Tables, columns and hierarchies") },
            // See what is in the model > The model as a whole (5)
            { "get_model_fingerprint", ("See what is in the model", "The model as a whole") },
            { "get_model_summary", ("See what is in the model", "The model as a whole") },
            { "model_overview", ("See what is in the model", "The model as a whole") },
            { "script_objects", ("See what is in the model", "The model as a whole") },
            { "search_model", ("See what is in the model", "The model as a whole") },
            // See what is in the model > Relationships and the diagram (4)
            { "get_layout", ("See what is in the model", "Relationships and the diagram") },
            { "get_model_graph", ("See what is in the model", "Relationships and the diagram") },
            { "model_graph_summary", ("See what is in the model", "Relationships and the diagram") },
            { "save_layout", ("See what is in the model", "Relationships and the diagram") },
            // See what is in the model > Tests, numbers and evidence (3)
            { "blame_value", ("See what is in the model", "Tests, numbers and evidence") },
            { "explain_value", ("See what is in the model", "Tests, numbers and evidence") },
            { "list_value_history", ("See what is in the model", "Tests, numbers and evidence") },
            // Change the model > Measures, DAX and queries (19)
            { "apply_dax_script", ("Change the model", "Measures, DAX and queries") },
            { "create_calculated_column", ("Change the model", "Measures, DAX and queries") },
            { "create_calculated_table", ("Change the model", "Measures, DAX and queries") },
            { "create_calculation_group", ("Change the model", "Measures, DAX and queries") },
            { "create_calculation_item", ("Change the model", "Measures, DAX and queries") },
            { "create_field_parameter", ("Change the model", "Measures, DAX and queries") },
            { "create_function", ("Change the model", "Measures, DAX and queries") },
            { "create_measure", ("Change the model", "Measures, DAX and queries") },
            { "daxlib_install", ("Change the model", "Measures, DAX and queries") },
            { "daxlib_list_installed", ("Change the model", "Measures, DAX and queries") },
            { "daxlib_package_info", ("Change the model", "Measures, DAX and queries") },
            { "daxlib_uninstall", ("Change the model", "Measures, DAX and queries") },
            { "daxlib_versions", ("Change the model", "Measures, DAX and queries") },
            { "get_dax", ("Change the model", "Measures, DAX and queries") },
            { "list_calculation_groups", ("Change the model", "Measures, DAX and queries") },
            { "set_calc_group_precedence", ("Change the model", "Measures, DAX and queries") },
            { "set_calc_item_format_string", ("Change the model", "Measures, DAX and queries") },
            { "update_measure", ("Change the model", "Measures, DAX and queries") },
            { "validate_dax", ("Change the model", "Measures, DAX and queries") },
            // Change the model > Where the data comes from (14)
            { "apply_schema_update", ("Change the model", "Where the data comes from") },
            { "create_data_source", ("Change the model", "Where the data comes from") },
            { "create_named_expression", ("Change the model", "Where the data comes from") },
            { "diff_schema", ("Change the model", "Where the data comes from") },
            { "get_incremental_refresh_policy", ("Change the model", "Where the data comes from") },
            { "get_named_expression", ("Change the model", "Where the data comes from") },
            { "get_partition_m", ("Change the model", "Where the data comes from") },
            { "get_source_schema", ("Change the model", "Where the data comes from") },
            { "list_named_expressions", ("Change the model", "Where the data comes from") },
            { "list_partitions", ("Change the model", "Where the data comes from") },
            { "remove_incremental_refresh_policy", ("Change the model", "Where the data comes from") },
            { "set_incremental_refresh_policy", ("Change the model", "Where the data comes from") },
            { "set_partition_m", ("Change the model", "Where the data comes from") },
            { "update_named_expression", ("Change the model", "Where the data comes from") },
            // Change the model > The model as a whole (14)
            { "add_plan_item", ("Change the model", "The model as a whole") },
            { "apply_plan", ("Change the model", "The model as a whole") },
            { "apply_tmdl", ("Change the model", "The model as a whole") },
            { "clear_plan", ("Change the model", "The model as a whole") },
            { "dry_run", ("Change the model", "The model as a whole") },
            { "get_plan", ("Change the model", "The model as a whole") },
            { "propose_plan", ("Change the model", "The model as a whole") },
            { "propose_replace", ("Change the model", "The model as a whole") },
            { "redo_change", ("Change the model", "The model as a whole") },
            { "replace_in_object", ("Change the model", "The model as a whole") },
            { "save_model", ("Change the model", "The model as a whole") },
            { "set_compatibility_level", ("Change the model", "The model as a whole") },
            { "set_plan_item", ("Change the model", "The model as a whole") },
            { "undo_change", ("Change the model", "The model as a whole") },
            // Change the model > Tables, columns and hierarchies (14)
            { "create_column", ("Change the model", "Tables, columns and hierarchies") },
            { "create_directlake_table", ("Change the model", "Tables, columns and hierarchies") },
            { "create_hierarchy", ("Change the model", "Tables, columns and hierarchies") },
            { "create_import_table", ("Change the model", "Tables, columns and hierarchies") },
            { "create_table", ("Change the model", "Tables, columns and hierarchies") },
            { "delete_object", ("Change the model", "Tables, columns and hierarchies") },
            { "delete_objects", ("Change the model", "Tables, columns and hierarchies") },
            { "duplicate_object", ("Change the model", "Tables, columns and hierarchies") },
            { "remove_safe_objects", ("Change the model", "Tables, columns and hierarchies") },
            { "rename_object", ("Change the model", "Tables, columns and hierarchies") },
            { "set_column_data_type", ("Change the model", "Tables, columns and hierarchies") },
            { "set_data_category", ("Change the model", "Tables, columns and hierarchies") },
            { "set_sort_by_column", ("Change the model", "Tables, columns and hierarchies") },
            { "set_summarize_by", ("Change the model", "Tables, columns and hierarchies") },
            // Change the model > How the model looks and reads (12)
            { "create_perspective", ("Change the model", "How the model looks and reads") },
            { "get_perspectives", ("Change the model", "How the model looks and reads") },
            { "get_properties", ("Change the model", "How the model looks and reads") },
            { "rename_display_folder", ("Change the model", "How the model looks and reads") },
            { "set_column_hidden", ("Change the model", "How the model looks and reads") },
            { "set_description", ("Change the model", "How the model looks and reads") },
            { "set_display_folder", ("Change the model", "How the model looks and reads") },
            { "set_measure_format", ("Change the model", "How the model looks and reads") },
            { "set_measure_format_expression", ("Change the model", "How the model looks and reads") },
            { "set_perspective_member", ("Change the model", "How the model looks and reads") },
            { "set_properties", ("Change the model", "How the model looks and reads") },
            { "set_property", ("Change the model", "How the model looks and reads") },
            // Change the model > The model spec (8)
            { "autogenerate_spec_from_fabric", ("Change the model", "The model spec") },
            { "autogenerate_spec_from_model", ("Change the model", "The model spec") },
            { "build_model_from_spec", ("Change the model", "The model spec") },
            { "clear_spec", ("Change the model", "The model spec") },
            { "get_spec", ("Change the model", "The model spec") },
            { "load_spec", ("Change the model", "The model spec") },
            { "save_spec", ("Change the model", "The model spec") },
            { "set_spec", ("Change the model", "The model spec") },
            // Change the model > Dates and time (8)
            { "define_calendar", ("Change the model", "Dates and time") },
            { "define_calendar_from_template", ("Change the model", "Dates and time") },
            { "delete_calendar", ("Change the model", "Dates and time") },
            { "generate_date_table", ("Change the model", "Dates and time") },
            { "generate_time_intelligence", ("Change the model", "Dates and time") },
            { "list_calendars", ("Change the model", "Dates and time") },
            { "mark_date_table", ("Change the model", "Dates and time") },
            { "tag_calendar_column", ("Change the model", "Dates and time") },
            // Change the model > Relationships and the diagram (4)
            { "create_relationship", ("Change the model", "Relationships and the diagram") },
            { "set_relationship_active", ("Change the model", "Relationships and the diagram") },
            { "set_relationship_cardinality", ("Change the model", "Relationships and the diagram") },
            { "set_relationship_crossfilter", ("Change the model", "Relationships and the diagram") },
            // Make it better > Findings and fixes (15)
            { "apply_fix", ("Make it better", "Findings and fixes") },
            { "apply_safe_fixes", ("Make it better", "Findings and fixes") },
            { "bpa_fix", ("Make it better", "Findings and fixes") },
            { "bpa_fix_all", ("Make it better", "Findings and fixes") },
            { "bpa_scan", ("Make it better", "Findings and fixes") },
            { "bpa_summary", ("Make it better", "Findings and fixes") },
            { "get_custom_rules", ("Make it better", "Findings and fixes") },
            { "lint_dax", ("Make it better", "Findings and fixes") },
            { "list_waivers", ("Make it better", "Findings and fixes") },
            { "load_bpa_rules", ("Make it better", "Findings and fixes") },
            { "optimize_measure", ("Make it better", "Findings and fixes") },
            { "reset_bpa_rules", ("Make it better", "Findings and fixes") },
            { "unwaive_finding", ("Make it better", "Findings and fixes") },
            { "validate_rule", ("Make it better", "Findings and fixes") },
            { "waive_finding", ("Make it better", "Findings and fixes") },
            // Prove it is right > Tests, numbers and evidence (24)
            { "add_interview_question", ("Prove it is right", "Tests, numbers and evidence") },
            { "capture_baseline", ("Prove it is right", "Tests, numbers and evidence") },
            { "compare_baseline", ("Prove it is right", "Tests, numbers and evidence") },
            { "delete_interview_question", ("Prove it is right", "Tests, numbers and evidence") },
            { "delete_test", ("Prove it is right", "Tests, numbers and evidence") },
            { "export_test_report", ("Prove it is right", "Tests, numbers and evidence") },
            { "export_verified_edits", ("Prove it is right", "Tests, numbers and evidence") },
            { "get_evidence", ("Prove it is right", "Tests, numbers and evidence") },
            { "get_verified_mode", ("Prove it is right", "Tests, numbers and evidence") },
            { "list_evidence", ("Prove it is right", "Tests, numbers and evidence") },
            { "list_interview_questions", ("Prove it is right", "Tests, numbers and evidence") },
            { "list_test_runs", ("Prove it is right", "Tests, numbers and evidence") },
            { "list_tests", ("Prove it is right", "Tests, numbers and evidence") },
            { "list_verified_edits", ("Prove it is right", "Tests, numbers and evidence") },
            { "probe_measure", ("Prove it is right", "Tests, numbers and evidence") },
            { "reconcile_measure", ("Prove it is right", "Tests, numbers and evidence") },
            { "review_reconcile_mapping", ("Prove it is right", "Tests, numbers and evidence") },
            { "run_interview", ("Prove it is right", "Tests, numbers and evidence") },
            { "run_tests", ("Prove it is right", "Tests, numbers and evidence") },
            { "save_evidence", ("Prove it is right", "Tests, numbers and evidence") },
            { "save_test", ("Prove it is right", "Tests, numbers and evidence") },
            { "try_test", ("Prove it is right", "Tests, numbers and evidence") },
            { "set_verified_mode", ("Prove it is right", "Tests, numbers and evidence") },
            { "verify_dax_equivalence", ("Prove it is right", "Tests, numbers and evidence") },
            // Track versions and compare > Versions and history (14)
            { "cherry_pick", ("Track versions and compare", "Versions and history") },
            { "create_history_checkpoint", ("Track versions and compare", "Versions and history") },
            { "get_reference_tree", ("Track versions and compare", "Versions and history") },
            { "git_branch", ("Track versions and compare", "Versions and history") },
            { "git_checkout", ("Track versions and compare", "Versions and history") },
            { "git_clone", ("Track versions and compare", "Versions and history") },
            { "git_commit", ("Track versions and compare", "Versions and history") },
            { "git_diff", ("Track versions and compare", "Versions and history") },
            { "git_log", ("Track versions and compare", "Versions and history") },
            { "git_pull", ("Track versions and compare", "Versions and history") },
            { "git_status", ("Track versions and compare", "Versions and history") },
            { "list_history_checkpoints", ("Track versions and compare", "Versions and history") },
            { "model_diff", ("Track versions and compare", "Versions and history") },
            { "restore_history_checkpoint", ("Track versions and compare", "Versions and history") },
            // Ship it out > Publishing and deployment (24)
            { "apply_model_diff", ("Ship it out", "Publishing and deployment") },
            { "cicd_generate", ("Ship it out", "Publishing and deployment") },
            { "cicd_publish", ("Ship it out", "Publishing and deployment") },
            { "deploy_gate", ("Ship it out", "Publishing and deployment") },
            { "deploy_live", ("Ship it out", "Publishing and deployment") },
            { "deploy_stage", ("Ship it out", "Publishing and deployment") },
            { "deployment_history", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_commit", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_connect", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_connection", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_disconnect", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_status", ("Ship it out", "Publishing and deployment") },
            { "fabric_git_update", ("Ship it out", "Publishing and deployment") },
            { "get_pipeline_stages", ("Ship it out", "Publishing and deployment") },
            { "get_stage_items", ("Ship it out", "Publishing and deployment") },
            { "git_push", ("Ship it out", "Publishing and deployment") },
            { "list_deployment_pipelines", ("Ship it out", "Publishing and deployment") },
            { "list_restore_points", ("Ship it out", "Publishing and deployment") },
            { "list_workspaces", ("Ship it out", "Publishing and deployment") },
            { "preview_deploy", ("Ship it out", "Publishing and deployment") },
            { "publish_data_agent", ("Ship it out", "Publishing and deployment") },
            { "purge_restore_points", ("Ship it out", "Publishing and deployment") },
            { "refresh_partition", ("Ship it out", "Publishing and deployment") },
            { "rollback_push", ("Ship it out", "Publishing and deployment") },
            // Ship it out > Who can see what (8)
            { "create_role", ("Ship it out", "Who can see what") },
            { "delete_role", ("Ship it out", "Who can see what") },
            { "list_roles", ("Ship it out", "Who can see what") },
            { "set_column_ols", ("Ship it out", "Who can see what") },
            { "set_role_member", ("Ship it out", "Who can see what") },
            { "set_role_permission", ("Ship it out", "Who can see what") },
            { "set_table_ols", ("Ship it out", "Who can see what") },
            { "set_table_permission", ("Ship it out", "Who can see what") },
            // Ship it out > The model write-up (4)
            { "get_doc_model", ("Ship it out", "The model write-up") },
            { "get_doc_outline", ("Ship it out", "The model write-up") },
            { "get_doc_section", ("Ship it out", "The model write-up") },
            { "set_doc_section", ("Ship it out", "The model write-up") },
            // Teach the AI about this model > Teach the AI (32)
            { "accept_primer_suggestion", ("Teach the AI about this model", "Teach the AI") },
            { "add_insight", ("Teach the AI about this model", "Teach the AI") },
            { "ai_readiness_scan", ("Teach the AI about this model", "Teach the AI") },
            { "ai_readiness_scan_live", ("Teach the AI about this model", "Teach the AI") },
            { "ai_readiness_summary", ("Teach the AI about this model", "Teach the AI") },
            { "approve_insight", ("Teach the AI about this model", "Teach the AI") },
            { "create_data_agent", ("Teach the AI about this model", "Teach the AI") },
            { "delete_data_agent", ("Teach the AI about this model", "Teach the AI") },
            { "delete_insight", ("Teach the AI about this model", "Teach the AI") },
            { "downvote_insight", ("Teach the AI about this model", "Teach the AI") },
            { "edit_insight", ("Teach the AI about this model", "Teach the AI") },
            { "enable_qna", ("Teach the AI about this model", "Teach the AI") },
            { "generate_data_agent_config", ("Teach the AI about this model", "Teach the AI") },
            { "get_ai_instructions", ("Teach the AI about this model", "Teach the AI") },
            { "get_data_agent", ("Teach the AI about this model", "Teach the AI") },
            { "get_grounding", ("Teach the AI about this model", "Teach the AI") },
            { "get_model_primer", ("Teach the AI about this model", "Teach the AI") },
            { "list_data_agents", ("Teach the AI about this model", "Teach the AI") },
            { "list_insights", ("Teach the AI about this model", "Teach the AI") },
            { "list_primer_suggestions", ("Teach the AI about this model", "Teach the AI") },
            { "load_readiness_rules", ("Teach the AI about this model", "Teach the AI") },
            { "make_model_ai_ready", ("Teach the AI about this model", "Teach the AI") },
            { "purge_knowledge", ("Teach the AI about this model", "Teach the AI") },
            { "recall_experience", ("Teach the AI about this model", "Teach the AI") },
            { "reject_primer_suggestion", ("Teach the AI about this model", "Teach the AI") },
            { "reset_readiness_rules", ("Teach the AI about this model", "Teach the AI") },
            { "set_ai_data_schema", ("Teach the AI about this model", "Teach the AI") },
            { "set_ai_instructions", ("Teach the AI about this model", "Teach the AI") },
            { "set_model_primer", ("Teach the AI about this model", "Teach the AI") },
            { "set_synonyms", ("Teach the AI about this model", "Teach the AI") },
            { "update_data_agent", ("Teach the AI about this model", "Teach the AI") },
            { "upvote_insight", ("Teach the AI about this model", "Teach the AI") },
            // Set the rules of the work > Workflows and rules (35)
            { "abort_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "activate_workflow_profile", ("Set the rules of the work", "Workflows and rules") },
            { "check_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "delete_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "delete_workflow_template", ("Set the rules of the work", "Workflows and rules") },
            { "export_workflow_evidence", ("Set the rules of the work", "Workflows and rules") },
            { "get_agent_policy", ("Set the rules of the work", "Workflows and rules") },
            { "get_entitlement", ("Set the rules of the work", "Workflows and rules") },
            { "get_op_catalog", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_document", ("Set the rules of the work", "Workflows and rules") },
            { "edit_workflow_document", ("Set the rules of the work", "Workflows and rules") },
            { "upgrade_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_layout", ("Set the rules of the work", "Workflows and rules") },
            { "save_workflow_layout", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_enforcement", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_policy", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_run", ("Set the rules of the work", "Workflows and rules") },
            { "get_workflow_template", ("Set the rules of the work", "Workflows and rules") },
            { "harness_report", ("Set the rules of the work", "Workflows and rules") },
            { "instantiate_workflow_template", ("Set the rules of the work", "Workflows and rules") },
            { "list_pending_approvals", ("Set the rules of the work", "Workflows and rules") },
            { "list_workflow_profiles", ("Set the rules of the work", "Workflows and rules") },
            { "list_workflow_templates", ("Set the rules of the work", "Workflows and rules") },
            { "list_workflows", ("Set the rules of the work", "Workflows and rules") },
            { "replay_check_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "save_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "save_workflow_template", ("Set the rules of the work", "Workflows and rules") },
            { "set_workflow_activation", ("Set the rules of the work", "Workflows and rules") },
            { "set_workflow_binding", ("Set the rules of the work", "Workflows and rules") },
            { "set_workflow_enabled", ("Set the rules of the work", "Workflows and rules") },
            { "set_workflow_enforcement", ("Set the rules of the work", "Workflows and rules") },
            { "skip_workflow_step", ("Set the rules of the work", "Workflows and rules") },
            { "start_workflow", ("Set the rules of the work", "Workflows and rules") },
            { "submit_workflow_step", ("Set the rules of the work", "Workflows and rules") },
            // Look things up > Reference lists and instruction sheets (6)
            { "bpa_get_fix_prompt", ("Look things up", "Reference lists and instruction sheets") },
            { "daxlib_search", ("Look things up", "Reference lists and instruction sheets") },
            { "get_fix_prompt", ("Look things up", "Reference lists and instruction sheets") },
            { "list_format_templates", ("Look things up", "Reference lists and instruction sheets") },
            { "list_interview_seeds", ("Look things up", "Reference lists and instruction sheets") },
            { "list_refresh_types", ("Look things up", "Reference lists and instruction sheets") },
        };

        /// <summary>question -> its shelves in the ratified page's top-to-bottom order (the picker's
        /// second level). Order is part of what Kane ratified, so it is data here, never re-derived.</summary>
        private static readonly Dictionary<string, string[]> Shelves =
            new Dictionary<string, string[]>(System.StringComparer.Ordinal)
        {
            { "Open a model and connect", new[] { "Connections and targets" } },
            { "See what is in the model", new[] { "Measures, DAX and queries", "Reports and what uses this model", "Speed and size", "Tables, columns and hierarchies", "The model as a whole", "Relationships and the diagram", "Tests, numbers and evidence" } },
            { "Change the model", new[] { "Measures, DAX and queries", "Where the data comes from", "The model as a whole", "Tables, columns and hierarchies", "How the model looks and reads", "The model spec", "Dates and time", "Relationships and the diagram" } },
            { "Make it better", new[] { "Findings and fixes" } },
            { "Prove it is right", new[] { "Tests, numbers and evidence" } },
            { "Track versions and compare", new[] { "Versions and history" } },
            { "Ship it out", new[] { "Publishing and deployment", "Who can see what", "The model write-up" } },
            { "Teach the AI about this model", new[] { "Teach the AI" } },
            { "Set the rules of the work", new[] { "Workflows and rules" } },
            { "Look things up", new[] { "Reference lists and instruction sheets" } },
        };

        /// <summary>The ratified shelf order under one question (empty for an unknown question).</summary>
        public static IReadOnlyList<string> ShelvesOf(string question)
            => question != null && Shelves.TryGetValue(question, out var s) ? s : System.Array.Empty<string>();

        /// <summary>Sort key for the catalog: the op's position in the ratified tree (question index,
        /// then shelf index). An op the taxonomy does not know sorts LAST, visibly, not interleaved.</summary>
        public static (int QuestionIndex, int ShelfIndex) OrderOf(string op)
        {
            if (!TryGet(op, out var q, out var shelf)) return (int.MaxValue, int.MaxValue);
            var qi = System.Array.IndexOf(Questions, q);
            var si = System.Array.IndexOf(Shelves[q], shelf);
            return (qi, si);
        }

        public static int Count => Map.Count;

        public static IEnumerable<string> Ops => Map.Keys;

        /// <summary>True with the ratified question and shelf, or false for an op this file does not
        /// know (a new tool whose placement has not been ratified yet — the guard test names it).</summary>
        public static bool TryGet(string op, out string question, out string shelf)
        {
            if (op != null && Map.TryGetValue(op, out var home)) { question = home.Question; shelf = home.Shelf; return true; }
            question = null; shelf = null; return false;
        }
    }
}
