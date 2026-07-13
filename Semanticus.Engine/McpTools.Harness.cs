using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// docs/harness-engineering.md §5 telemetry tool, in its own [McpServerToolType] class so it is
    /// conflict-free with the main McpTools surface (both are discovered by Program.cs's
    /// WithToolsFromAssembly(), which scans the assembly for every [McpServerToolType]).
    /// </summary>
    [McpServerToolType]
    public static class McpToolsHarness
    {
        [McpServerTool(Name = "harness_report"), Description("Read-only harness-ergonomics report over the L0 experience log (.semanticus/experience.jsonl): per-op counts + error rates, retry clusters (>=2 consecutive same-op failures), the top-N recurring error messages, top flail sites (failures-before-a-success per op), and the four harness KPIs (task success rate, tokens/outcome [not derivable from L0 v1], flail rate per 100 events, human interventions). Use it to find where agents flail so you can fix TOOL ERGONOMICS (better error messages, terser results) — a permanent improvement, cheaper than learning machinery. Fail-soft: a missing/empty/corrupt log returns an empty report with a note, never an error. Does not mutate the model.")]
        public static Task<HarnessReportResult> HarnessReport(IEngine engine,
            [Description("How many rows to keep in the error-frequency and flail-site tables (default 10).")] int topN = 10)
            => engine.HarnessReportAsync(topN);
    }
}
