using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// Retired operation ids that saved files may still name. A workflow file (a project copy, or a shipped
    /// compatibility definition such as workflows-compat/v1.2.0/make-ai-ready.md) can carry an id that has since
    /// been renamed or merged; this maps it to the operation that exists today. An alias is never a tool, is never
    /// listed in get_op_catalog, and never reaches the agent: the workflow runner shows the current id. A direct MCP
    /// call to a retired id gets <see cref="RetiredToolMessage"/> (McpDoorFilters), which names the call to make.
    ///
    /// Change-plan ITEM KINDS are a separate vocabulary with their own table (<see cref="CanonicalPlanKind"/>). The
    /// two used to share one table, which was safe only while no retired tool name was also a plan kind. bpa_fix is
    /// both: the tool folded into apply_fix, but "bpa_fix" stays the kind stored plans carry.
    ///
    /// enable_qna became create_linguistic_schema on 2026-09-23. The op creates the synonym store Copilot and data
    /// agents read; it never touched the Power BI Q&amp;A setting, which Microsoft retires in February 2027.
    /// The fourteen merges of 2026-09-24 (prompt audit decision 2) folded near-duplicate tools into their survivors.
    /// </summary>
    public static class OpAliases
    {
        private static readonly Dictionary<string, (string Current, string Call)> Retired =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["enable_qna"] = ("create_linguistic_schema", "Call create_linguistic_schema."),
            ["bpa_summary"] = ("bpa_scan", "Call bpa_scan with detail='summary'."),
            ["ai_readiness_summary"] = ("ai_readiness_scan", "Call ai_readiness_scan with detail='summary'."),
            ["ai_readiness_scan_live"] = ("ai_readiness_scan", "Call ai_readiness_scan with live=true."),
            ["model_graph_summary"] = ("get_model_graph", "Call get_model_graph with detail='summary'."),
            ["bpa_fix"] = ("apply_fix", "Call apply_fix with system='bpa'."),
            ["bpa_get_fix_prompt"] = ("get_fix_prompt", "Call get_fix_prompt with system='bpa'."),
            ["bpa_fix_all"] = ("apply_safe_fixes", "Call apply_safe_fixes with system='bpa'."),
            ["load_bpa_rules"] = ("load_rules", "Call load_rules with system='bpa'."),
            ["load_readiness_rules"] = ("load_rules", "Call load_rules with system='air'."),
            ["reset_bpa_rules"] = ("reset_rules", "Call reset_rules with system='bpa'."),
            ["reset_readiness_rules"] = ("reset_rules", "Call reset_rules with system='air'."),
            ["benchmark_dax_coldwarm"] = ("benchmark_dax", "Call benchmark_dax with mode='coldWarm'."),
            ["impact_of"] = ("impact_assessment", "Call impact_assessment with scope='model'; the answer is in modelImpact."),
            // Not merged: labelling a target was never an agent capability, so there is no successor to run.
            ["label_connection"] = (null, "Only the user can set a label, in Connections in VS Code. Read labels with list_connections."),
        };

        /// <summary>Plan item kinds that were renamed. Only kinds belong here, never tool merges.</summary>
        private static readonly Dictionary<string, string> RetiredPlanKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enable_qna"] = "create_linguistic_schema",
        };

        /// <summary>Every retired operation id.</summary>
        public static IReadOnlyCollection<string> RetiredIds => Retired.Keys;

        /// <summary>The current id for <paramref name="op"/>: the operation a retired id became, else the input
        /// unchanged (null stays null). A retired id with no successor (label_connection) is returned unchanged.</summary>
        public static string Canonical(string op) =>
            op != null && Retired.TryGetValue(op.Trim(), out var r) && r.Current != null ? r.Current : op;

        /// <summary>A step's declared ops with every retired id replaced by its current one.</summary>
        public static string[] Canonical(string[] ops) => ops?.Select(Canonical).ToArray();

        /// <summary>Whether a step's declared ops include <paramref name="op"/>, reading retired ids on either side
        /// as the operation they became.</summary>
        public static bool Contains(string[] ops, string op) =>
            ops != null && ops.Any(o => string.Equals(Canonical(o), Canonical(op), StringComparison.Ordinal));

        /// <summary>The current change-plan item kind for <paramref name="kind"/>: a renamed kind becomes its new
        /// name, anything else is returned unchanged. Tool merges never touch plan kinds.</summary>
        public static string CanonicalPlanKind(string kind) =>
            kind != null && RetiredPlanKinds.TryGetValue(kind.Trim(), out var current) ? current : kind;

        /// <summary>The teaching answer to a direct call of a retired tool, or null when <paramref name="name"/> is
        /// not retired.</summary>
        public static string RetiredToolMessage(string name)
        {
            if (name == null || !Retired.TryGetValue(name.Trim(), out var r)) return null;
            return r.Current == null
                ? $"{name} is no longer an agent tool. {r.Call}"
                : $"{name} was merged into {r.Current}. {r.Call}";
        }
    }
}
