using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Lineage;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Compose every shipped deterministic impact signal without mutating the model. The reports it
        /// checks against come from the model's own report scope (set_report_scope / check_reports), so the page,
        /// the assistant, the cleanup list and the apply-time recheck all answer from the same reading. Local
        /// report folders handed in on the request are folded into that scope for this one call. Report parsing is
        /// off-dispatcher; model traversal and name reconciliation stay on the single model dispatcher.</summary>
        public async Task<ImpactAssessmentResult> ImpactAssessmentAsync(ImpactAssessmentRequest request)
        {
            request ??= new ImpactAssessmentRequest();
            var scopeName = ImpactAssessmentBuilder.NormalizeScope(request.Scope);
            var intent = ImpactAssessmentBuilder.NormalizeIntent(request.Intent);
            if (string.IsNullOrWhiteSpace(request.ObjectRef))
                throw new ArgumentException("impact_assessment needs objectRef. Run search_model or get_lineage, then retry with a returned ref.");
            if (scopeName == "model" && (request.ReportPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) ?? false))
                throw new ArgumentException("scope='model' deliberately excludes reports. Omit reportPaths, or retry with scope='modelAndReports'.");

            var session = _sessions.Require();
            // Tests is Pro, so a free impact assessment does not read the saved checks. It says so in Unknowns
            // and in Coverage rather than reporting a confident zero, which would be worse than not answering.
            var savedChecksArePro = !Entitlement.FeatureGrants.Grants(_entitlement, Entitlement.ProFeature.Tests);
            var tests = savedChecksArePro ? null : await ListTestDefinitionsCoreAsync();

            // The engine-owned scope first; then any local folder handed in on the request, for this call only.
            List<(string path, string error, ReportDefinitionReader.ParseResult result)> parsed = null;
            var adHoc = new List<ReportScopeReport>();
            if (scopeName == "modelAndReports")
            {
                parsed = CheckedReportPartsFor(session);
                var handed = (request.ReportPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (handed.Count > 0)
                {
                    parsed ??= new List<(string path, string error, ReportDefinitionReader.ParseResult result)>();
                    var now = DateTime.UtcNow.ToString("o");
                    foreach (var p in handed)
                    {
                        if (parsed.Any(x => string.Equals(x.path, p, StringComparison.OrdinalIgnoreCase))) continue;
                        var pr = ReportDefinitionReader.ReadLocalPbir(p);
                        parsed.Add((p, pr.DefinitionFound ? null : "No readable report definition was found at that path.", pr));
                        // A folder handed in on the call was read for this answer, so it belongs in the account of
                        // what was checked. It is NOT saved: only set_report_scope changes the model's own list.
                        adHoc.Add(new ReportScopeReport
                        {
                            Id = "local:" + p, Kind = "local",
                            Name = System.IO.Path.GetFileName(p.TrimEnd('/', '\\')),
                            Source = p,
                            State = pr.DefinitionFound && pr.SkippedParts == 0
                                ? ReportScopeStates.Checked : ReportScopeStates.CouldNotBeFullyChecked,
                            CheckedWhenUtc = now,
                            Note = pr.DefinitionFound ? null : "No readable report definition was found at that path.",
                        });
                    }
                }
            }

            var normalized = new ImpactAssessmentRequest
            {
                ObjectRef = request.ObjectRef.Trim(), Intent = intent, Scope = scopeName,
                ReportPaths = request.ReportPaths ?? Array.Empty<string>(),
            };
            var modelName = await session.ReadAsync(m => string.IsNullOrWhiteSpace(m.Database?.Name) ? m.Name : m.Database.Name);
            var scope = scopeName == "model" ? null : ScopeSnapshot(session, modelName);
            if (scope != null && adHoc.Count > 0)
                scope = ReportScopeResult.From(modelName, scope.Reports.Concat(adHoc), scope.Note);
            return await session.ReadAsync(model =>
            {
                var reports = parsed == null || parsed.Count == 0 ? null : LineageGraph.AnalyzeReports(model, parsed);
                return ImpactAssessmentBuilder.Build(model, normalized, reports, tests, scope, savedChecksArePro);
            });
        }
    }
}
