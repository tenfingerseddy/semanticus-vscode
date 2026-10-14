using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The MCP door's call-tool filters, registered in one place so the stdio server (Program.Mcp) and the in-process
    /// door tests run the exact same chain. The retired-tool answer comes first: a call to a merged or removed tool
    /// name gets a sentence naming the call that replaced it, instead of the SDK's bare "Unknown tool". The SDK runs
    /// call-tool filters before its own unknown-name refusal (ToolMergeTests proves it on SDK 1.4.1). Then
    /// McpErrorBoundary turns a failure into the engine's teaching message, and McpHealthAppender adds the
    /// health-delta block to a mutating tool's result. EngineUpdateGuard sits outside all of them and adds the
    /// restart line to every result while this process is older than the installed files.
    /// </summary>
    public static class McpDoorFilters
    {
        public static void Add(IMcpRequestFilterBuilder f, IEngine engine)
        {
            // Outermost, so every result carries the "Semanticus was updated, restart the assistant" line once this
            // process is older than the files installed beside it, errors and retired names included.
            f.AddCallToolFilter(EngineUpdateGuard.Wrap);
            f.AddCallToolFilter(RetiredTools);
            f.AddCallToolFilter(UnknownArguments);
            f.AddCallToolFilter(McpErrorBoundary.Wrap);
            f.AddCallToolFilter(next => McpHealthAppender.Wrap(engine, next));
            f.AddListToolsFilter(LocalEditWords);
        }

        /// <summary>What every model-edit tool says about where its edit lands. On Kane's Claude Desktop test (13 Oct 2026) Claude's own
        /// safety check refused delete_objects and define_calendar "as a change to a shared resource, because this model is linked to a Power
        /// BI workspace", and Claude could not tell whether an edit reaches the workspace at once. It does not: an edit changes the engine's
        /// in-memory model, and only a save back to the workspace (Studio's Save on a model opened from one) or a publish (deploy_live,
        /// apply_model_diff with a target) writes there (measured on m15/doer: the only live SaveChanges is LiveDeploy).</summary>
        public const string LocalEditSentence =
            " This edits Semanticus's working copy on this computer only, also for a model opened from a Power BI workspace: nothing reaches"
            + " the workspace until the model is saved back to it or published, and undo_change takes the edit back.";

        /// <summary>The tools that change the open model as one undoable step and write nowhere else. A tool that writes a file, a sidecar
        /// or a live model is not here, so the sentence is never said about one.</summary>
        public static readonly IReadOnlyCollection<string> LocalEditTools = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "apply_dax_script", "apply_fix", "apply_fixes", "apply_plan", "apply_plan_items", "apply_safe_fixes", "apply_tmdl",
            "create_calculated_column", "create_calculated_table", "create_calculation_group", "create_calculation_item", "create_column",
            "create_directlake_table", "create_field_parameter", "create_function", "create_hierarchy", "create_import_table",
            "create_measure", "create_measures", "create_named_expression", "create_perspective", "create_relationship", "create_role",
            "create_table", "define_calendar", "define_calendar_from_template", "delete_calendar", "delete_object", "delete_objects",
            "delete_role", "duplicate_object", "duplicate_objects", "edit_objects", "fix_them", "generate_date_table",
            "generate_time_intelligence", "make_model_ai_ready", "mark_date_table", "model_plan_action", "remove_incremental_refresh_policy",
            "remove_safe_objects", "rename_display_folder", "rename_object", "replace_in_object", "set_ai_data_schema", "set_ai_instructions",
            "set_calc_group_precedence", "set_calc_item_format_string", "set_column_data_type", "set_column_hidden", "set_column_ols",
            "set_compatibility_level", "set_data_category", "set_description", "set_display_folder", "set_incremental_refresh_policy",
            "set_measure_format", "set_measure_format_expression", "set_object_permissions", "set_partition_m", "set_perspective_member",
            "set_perspective_members", "set_properties", "set_property", "set_relationship_active", "set_relationship_cardinality",
            "set_relationship_crossfilter", "set_role_member", "set_role_permission", "set_sort_by_column", "set_summarize_by",
            "set_synonyms", "set_table_ols", "set_table_permission", "tag_calendar_column", "update_measure", "update_named_expression",
            "write_words",
        };

        /// <summary>Add <see cref="LocalEditSentence"/> to each model-edit tool as the tool list goes out, so one sentence says it the same way
        /// on every tool and no description can drift from it.</summary>
        internal static McpRequestHandler<ListToolsRequestParams, ListToolsResult> LocalEditWords(
            McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
            async (request, cancellationToken) =>
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                foreach (var tool in result?.Tools ?? new List<Tool>())
                    if (tool != null && LocalEditTools.Contains(tool.Name) && (tool.Description == null || !tool.Description.Contains(LocalEditSentence)))
                        tool.Description = (tool.Description ?? "").TrimEnd() + LocalEditSentence;
                return result;
            };

        /// <summary>Answer a retired tool name with its replacement; every other call passes through untouched.</summary>
        internal static McpRequestHandler<CallToolRequestParams, CallToolResult> RetiredTools(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) =>
            {
                var message = OpAliases.RetiredToolMessage(request?.Params?.Name);
                if (message == null) return next(request, ct);
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
                });
            };

        /// <summary>Refuse, in words, an argument a strict tool does not take. The SDK binds the names it knows and drops
        /// the rest without a word, which is harmless for most tools and wrong for ask_data: its answer hands the
        /// assistant a parts object to pass back, and a dropped "groupBy" once turned "Net Sales by Region" into one
        /// total. Only the tools named here are strict.</summary>
        internal static McpRequestHandler<CallToolRequestParams, CallToolResult> UnknownArguments(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) =>
            {
                var p = request?.Params;
                if (p?.Name != "ask_data" || p.Arguments == null) return next(request, ct);
                var message = McpTools.UnknownAskDataArgument(p.Arguments.Keys);
                if (message == null) return next(request, ct);
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
                });
            };
    }
}
