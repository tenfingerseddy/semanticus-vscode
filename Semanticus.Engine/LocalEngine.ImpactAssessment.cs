using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Lineage;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Compose every shipped deterministic impact signal without mutating the model. Report parsing is
        /// off-dispatcher; model traversal and name reconciliation stay on the single model dispatcher.</summary>
        public async Task<ImpactAssessmentResult> ImpactAssessmentAsync(ImpactAssessmentRequest request)
        {
            request ??= new ImpactAssessmentRequest();
            var scope = ImpactAssessmentBuilder.NormalizeScope(request.Scope);
            var intent = ImpactAssessmentBuilder.NormalizeIntent(request.Intent);
            if (string.IsNullOrWhiteSpace(request.ObjectRef))
                throw new ArgumentException("impact_assessment needs objectRef. Run search_model or get_lineage, then retry with a returned ref.");
            if (scope == "model" && (request.ReportPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) ?? false))
                throw new ArgumentException("scope='model' deliberately excludes reports. Omit reportPaths, or retry with scope='modelAndReports'.");

            var session = _sessions.Require();
            var tests = await ListTestDefinitionsAsync();
            var interview = await ListInterviewQuestionsAsync(null);
            var parsed = scope == "modelAndReports" && request.ReportPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) == true
                ? request.ReportPaths.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => (path: p, result: Lineage.ReportDefinitionReader.ReadLocalPbir(p))).ToList()
                : null;
            var normalized = new ImpactAssessmentRequest { ObjectRef = request.ObjectRef.Trim(), Intent = intent, Scope = scope, ReportPaths = request.ReportPaths ?? Array.Empty<string>() };
            return await session.ReadAsync(model =>
            {
                var reports = parsed == null ? null : Lineage.LineageGraph.AnalyzeReports(model, parsed);
                return ImpactAssessmentBuilder.Build(model, normalized, reports, tests, interview);
            });
        }
    }
}
