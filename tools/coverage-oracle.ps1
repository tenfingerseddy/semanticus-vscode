param(
    [string]$OutputPath = 'docs/mcp-surface-inventory.json',
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-PascalName([string]$operation) {
    (($operation -split '_') | ForEach-Object {
        if ($_.Length -eq 0) { return '' }
        $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
    }) -join ''
}

function Get-Family([string]$operation) {
    switch -Regex ($operation) {
        '^(open_|create_model|save_model|session_info|get_entitlement|undo_change|redo_change|list_local_instances|connect_local|connect_xmla|disconnect|connection_status)' { return 'session-and-lifecycle' }
        '^(model_overview|get_model_summary|get_orientation|get_model_fingerprint|get_model_objects|list_objects|get_object|list_measures|list_columns|get_model_graph|model_graph_summary|search_model|get_dependencies|get_dependents|script_objects)' { return 'orientation-and-model-reads' }
        '^(create_|delete_object|duplicate_object|rename_object|apply_dax_script|apply_tmdl|set_compatibility_level)' { return 'object-authoring' }
        '^(get_properties|set_property|set_properties|set_description|set_display_folder|rename_display_folder|set_measure_format|set_measure_format_expression|set_column_|set_sort_by_column|set_data_category|set_summarize_by|set_relationship_|get_named_expression|list_named_expressions|update_named_expression|get_partition_m|set_partition_m|list_partitions|list_calculation_groups|list_format_templates|get_perspectives|set_perspective_member|set_calc_group_precedence|set_calc_item_format_string)' { return 'metadata-and-properties' }
        '^(get_dax|update_measure|validate_dax|run_dax|run_dmv|preview_table|pivot_measure|benchmark_|profile_dax|capture_query_plan|evaluate_and_log|clear_cache|verify_dax_equivalence|lint_dax|optimize_measure|probe_measure|reconcile_measure|explain_value|blame_value|list_value_history|list_functions|vpaq_scan)' { return 'dax-and-query' }
        '^(get_source_schema|diff_schema|apply_schema_update|preview_m|profile_m|get_source_sql|set_source_sql|get_incremental_refresh_policy|set_incremental_refresh_policy|remove_incremental_refresh_policy|list_refresh_types)' { return 'source-and-m' }
        '^(list_calendars|define_calendar|delete_calendar|tag_calendar_column|define_calendar_from_template|generate_date_table|mark_date_table|generate_time_intelligence)' { return 'calendars' }
        '^(create_role|delete_role|list_roles|set_role_|set_table_permission|set_table_ols|set_column_ols)' { return 'model-security' }
        '^(ai_readiness_|make_model_ai_ready|apply_safe_fixes|apply_fix|get_fix_prompt|get_grounding|load_readiness_rules|reset_readiness_rules|get_custom_rules|validate_rule|enable_qna|set_synonyms|set_ai_|get_ai_instructions)' { return 'ai-readiness' }
        '^(bpa_|load_bpa_rules|reset_bpa_rules|list_waivers|waive_finding|unwaive_finding)' { return 'bpa-and-waivers' }
        '^(get_lineage|impact_of|impact_assessment|unused_objects|remove_safe_objects|analyze_reports|analyze_cloud_reports|list_reports)' { return 'lineage-and-impact' }
        '^(model_diff|apply_model_diff|cherry_pick|get_reference_tree|git_|create_history_checkpoint|list_history_checkpoints|restore_history_checkpoint)' { return 'compare-and-source-control' }
        '^(list_connections|connection_context|remember_xmla_connection|forget_connection|label_connection|set_connection_working_folder|set_publish_destination|prepare_working_copy)' { return 'connections' }
        '^(deploy_|preview_deploy|deployment_history|list_workspaces|list_deployment_pipelines|get_pipeline_stages|get_stage_items|fabric_git_|cicd_|rollback_push|list_restore_points|purge_restore_points|refresh_partition)' { return 'deployment-and-fabric' }
        '^(list_data_agents|get_data_agent|generate_data_agent_config|create_data_agent|update_data_agent|delete_data_agent|publish_data_agent)' { return 'data-agent' }
        '^(propose_plan|get_plan|set_plan_item|add_plan_item|apply_plan|clear_plan|capture_baseline|compare_baseline|get_verified_mode|set_verified_mode|list_verified_edits|export_verified_edits)' { return 'change-plan-and-verified-edits' }
        '^(save_test|delete_test|list_tests|run_tests|list_test_runs|review_reconcile_mapping|add_interview_question|delete_interview_question|list_interview_questions|list_interview_seeds|run_interview|get_evidence|list_evidence|save_evidence|export_test_report)' { return 'tests-and-evidence' }
        '^(list_workflows|get_workflow|save_workflow|delete_workflow|check_workflow|start_workflow|get_workflow_run|submit_workflow_step|skip_workflow_step|abort_workflow|get_op_catalog|set_workflow_|get_workflow_|export_workflow_evidence|replay_check_workflow|list_workflow_templates|get_workflow_template|save_workflow_template|delete_workflow_template|instantiate_workflow_template|list_workflow_profiles|activate_workflow_profile)' { return 'workflows' }
        '^(get_model_primer|set_model_primer|list_primer_suggestions|accept_primer_suggestion|reject_primer_suggestion|get_model_fingerprint|recall_experience|add_insight|edit_insight|delete_insight|approve_insight|upvote_insight|downvote_insight|list_insights|purge_knowledge)' { return 'primer-and-learning' }
        '^(get_agent_policy|set_agent_policy|list_pending_approvals|approve_agent_action|deny_agent_action)' { return 'agent-governance' }
        '^(get_doc_|set_doc_|get_spec|set_spec|clear_spec|save_spec|load_spec|build_model_from_spec|autogenerate_spec_)' { return 'documentation-and-spec' }
        '^(daxlib_)' { return 'daxlib' }
        '^(get_layout|save_layout|export_vpax|dry_run|search_model_ex|propose_replace|replace_in_object|harness_report)' { return 'cross-cutting-tools' }
        default { return 'unclassified' }
    }
}

function Get-ReleaseClass([string]$operation) {
    if ($operation -in @('analyze_cloud_reports', 'publish_data_agent')) { return 'deferred' }
    if ($operation -match '^(fabric_git_commit|fabric_git_update|fabric_git_connect|fabric_git_disconnect|deploy_stage|cicd_publish)$') { return 'dry-run-only' }
    if ($operation -match '^(create_data_agent|update_data_agent|delete_data_agent|generate_data_agent_config|list_data_agents|get_data_agent)$') { return 'experimental' }
    if ($operation -eq 'refresh_partition') { return 'experimental' }
    if ($operation -eq 'deploy_live') { return 'supervised' }
    return 'supported'
}

function Get-Risk([string]$operation) {
    if ($operation -match '(deploy|fabric|cicd|publish|entitlement|verified|dry_run|apply_plan|save_model|undo_change|redo_change|agent_policy|approve_agent|deny_agent|refresh_partition)') { return 'critical' }
    if ($operation -match '^(create_|set_|update_|delete_|apply_|rename_|remove_|purge_|restore_|rollback_|git_|tag_|define_|mark_|generate_)') { return 'high' }
    return 'standard'
}

$mcpPaths = @(git ls-files 'Semanticus.Engine/McpTools*.cs' | Sort-Object)
$mcpSource = ($mcpPaths | ForEach-Object { Get-Content -LiteralPath $_ -Raw }) -join "`n"
$toolNames = [regex]::Matches($mcpSource, 'McpServerTool\(Name\s*=\s*"([^"]+)"\)') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$tracked = @(git ls-files)
$evidenceFiles = @($tracked | Where-Object {
    ($_ -match '^(Semanticus\.(Tests|Smoke|RpcSmoke|McpSmoke|AirSmoke|CicdSmoke|LearnSmoke|LearnBench)/|Semanticus\.VSCode/(src/test|test)/)') -and
    ($_ -match '\.(cs|mjs)$')
})
$evidenceText = @{}
foreach ($file in $evidenceFiles) {
    $nativePath = $file -replace '/', [IO.Path]::DirectorySeparatorChar
    if (Test-Path -LiteralPath $nativePath) {
        $evidenceText[$file] = Get-Content -LiteralPath $nativePath -Raw
    }
}

$operations = foreach ($tool in $toolNames) {
    $pascal = Get-PascalName $tool
    $searchNames = @($tool, $pascal)
    if ($tool -eq 'vpaq_scan') { $searchNames += 'VertiPaqScan' }
    $hits = @($evidenceText.Keys | Where-Object {
        $text = $evidenceText[$_]
        @($searchNames | Where-Object { $text.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
    } | Sort-Object)
    [ordered]@{
        operation = $tool
        family = Get-Family $tool
        releaseClass = Get-ReleaseClass $tool
        risk = Get-Risk $tool
        referenceEvidence = $hits
    }
}

$rpcSource = Get-Content -LiteralPath 'Semanticus.Engine/EngineRpcTarget.cs' -Raw
$rpcCount = ([regex]::Matches($rpcSource, 'public\s+(?:async\s+)?Task(?:<[^>]+>)?\s+[A-Za-z0-9_]+\s*\(')).Count
$iEngineSource = Get-Content -LiteralPath 'Semanticus.Engine/IEngine.cs' -Raw
$iEngineCount = ([regex]::Matches($iEngineSource, '^\s*Task<', [Text.RegularExpressions.RegexOptions]::Multiline)).Count

$appSource = Get-Content -LiteralPath 'Semanticus.VSCode/webview/src/App.tsx' -Raw
$groupStart = $appSource.IndexOf('const TAB_GROUPS', [StringComparison]::Ordinal)
$groupEnd = $appSource.IndexOf('const TAB_TO_GROUP', [StringComparison]::Ordinal)
$groupText = $appSource.Substring($groupStart, $groupEnd - $groupStart)
$studioTabs = @([regex]::Matches($groupText, "\{ id: '([^']+)', label: '[^']+' \}") |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$studioType = [regex]::Match($appSource, "type StudioTab = (?<types>[^;]+);").Groups['types'].Value
$allStudioTabs = @([regex]::Matches($studioType, "'([^']+)'") |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

$sourceHashInput = ($mcpSource + "`n" + $rpcSource + "`n" + $iEngineSource + "`n" + $groupText) -replace "`r`n?", "`n"
$hashBytes = [Text.Encoding]::UTF8.GetBytes($sourceHashInput)
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($hashBytes)).ToLowerInvariant()

$unclassified = @($operations | Where-Object { $_.family -eq 'unclassified' })
$withoutReference = @($operations | Where-Object { $_.referenceEvidence.Count -eq 0 })
$inventory = [ordered]@{
    schemaVersion = 1
    sourceHash = $sourceHash
    sources = @($mcpPaths) + @('Semanticus.Engine/EngineRpcTarget.cs', 'Semanticus.Engine/IEngine.cs', 'Semanticus.VSCode/webview/src/App.tsx')
    summary = [ordered]@{
        mcpOperations = $operations.Count
        rpcActions = $rpcCount
        engineMethods = $iEngineCount
        studioDestinations = $allStudioTabs.Count
        groupedStudioDestinations = $studioTabs.Count
        trackedEvidenceFiles = $evidenceText.Count
        operationsWithoutTrackedTestReference = $withoutReference.Count
        unclassifiedOperations = $unclassified.Count
    }
    studioDestinations = $allStudioTabs
    groupedStudioDestinations = $studioTabs
    operations = $operations
}

$json = $inventory | ConvertTo-Json -Depth 8
$resolvedOutput = Join-Path $root $OutputPath
if ($Check) {
    if (-not (Test-Path -LiteralPath $resolvedOutput)) {
        throw "Coverage inventory is missing: $OutputPath. Run tools/coverage-oracle.ps1."
    }
    $existing = (Get-Content -LiteralPath $resolvedOutput -Raw).TrimEnd()
    if ($existing -ne $json.TrimEnd()) {
        throw "Coverage inventory is stale. Run tools/coverage-oracle.ps1 and commit the result."
    }
    if ($unclassified.Count -gt 0) {
        throw "Coverage inventory has unclassified MCP operations: $($unclassified.operation -join ', ')"
    }
    Write-Output "Coverage inventory current: $($operations.Count) MCP operations; $($withoutReference.Count) without a tracked test reference."
    exit 0
}

[IO.File]::WriteAllText($resolvedOutput, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Wrote ${OutputPath}: $($operations.Count) MCP operations; $($withoutReference.Count) without a tracked test reference; $($unclassified.Count) unclassified."
