using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.Lineage;

namespace Semanticus.Engine
{
    [McpServerToolType]
    public static class McpToolsImpactAssessment
    {
        [McpServerTool(Name = "impact_assessment"), Description("IMPACT: compose the deterministic change assessment before editing, renaming, restructuring or removing one model object. Returns the transitive TOM blast radius, the affected reports and visuals from THIS MODEL'S REPORT SCOPE (set_report_scope + check_reports), the current-model saved checks to run afterwards, what was checked and every gap as a plain sentence, one verdict token, and the exact next op. scope='modelAndReports' is the honest default: with no report checked it returns Unknown/NeedsReview, never green, and the next op is check_reports. scope='model' deliberately proves only model-internal impact. modelImpact holds the model's own blast radius: every DAX dependent, plus for a column the relationships, hierarchies and sort-by columns that use it, each with its depth, and counts by kind. scope='model' returns only that half. reportPaths folds a LOCAL report folder into the scope for this one call. Free and read-only; it omits report contents and does not apply the change.")]
        public static async Task<ImpactAssessmentResult> ImpactAssessment(IEngine engine,
            [Description("Object ref from search_model or get_lineage")] string objectRef,
            [Description("change, rename, remove, or restructure")] string intent = "change",
            [Description("modelAndReports (default) or model")] string scope = "modelAndReports",
            [Description("Optional local PBIR project/report definition paths that form the explicit report review scope")] string[] reportPaths = null)
        {
            var result = await engine.ImpactAssessmentAsync(new ImpactAssessmentRequest
            {
                ObjectRef = objectRef, Intent = intent, Scope = scope, ReportPaths = reportPaths,
            });
            McpTools.Emit(engine, new ActivityEvent
            {
                Kind = "impact_assessment", Origin = "agent", Label = $"Assessed {result.Intent} impact",
                Target = result.ObjectRef, Ok = result.Verdict != "Broken", Error = result.Verdict == "Broken" ? result.Summary : null,
                Result = result.Summary + (result.SuggestedNextAction == null ? "" : $" Next: {result.SuggestedNextAction.Op}({result.SuggestedNextAction.Args})."),
            });
            return result;
        }
    }
}
