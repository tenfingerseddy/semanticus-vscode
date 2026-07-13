using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.Lineage
{
    /// <summary>One request for the composed change-impact referee. Scope is explicit so a model-only assessment
    /// can be complete within its declared boundary without implying that published reports were checked.</summary>
    public sealed class ImpactAssessmentRequest
    {
        public string ObjectRef { get; set; }
        public string Intent { get; set; } = "change";              // change | rename | remove | restructure
        public string Scope { get; set; } = "modelAndReports";      // model | modelAndReports
        public string[] ReportPaths { get; set; } = Array.Empty<string>();
    }

    public sealed class ImpactCoverageArea
    {
        public string Area { get; set; }                             // model | reports | saved-tests | interview
        public string Status { get; set; }                           // complete | excluded | scoped | incomplete | unknown | planned
        public int Checked { get; set; }
        public int Unknown { get; set; }
        public string Detail { get; set; }
    }

    public sealed class ImpactReportHit
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int Visuals { get; set; }
        public string[] UsedRefs { get; set; } = Array.Empty<string>();
    }

    public sealed class ImpactReplayCheck
    {
        public string Id { get; set; }
        public string Kind { get; set; }                             // ambient-suite | saved-test | interview-question
        public string Title { get; set; }
        public string TargetRef { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ImpactNextAction
    {
        public string Op { get; set; }
        public string Args { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The one engine-owned answer consumed by both doors, Lineage verbs and the R6 workflows. Verdict uses
    /// the shared five-word vocabulary as a string so every transport renders the same token.</summary>
    public sealed class ImpactAssessmentResult
    {
        public string ObjectRef { get; set; }
        public string ObjectName { get; set; }
        public string ObjectKind { get; set; }
        public string ModelName { get; set; }
        public string Intent { get; set; }
        public string Scope { get; set; }
        public string Verdict { get; set; }                          // Verified | NeedsReview | Broken | Unknown
        public ImpactResult ModelImpact { get; set; }
        public ImpactReportHit[] ReportImpact { get; set; } = Array.Empty<ImpactReportHit>();
        public int ReportsImpacted { get; set; }
        public int VisualsImpacted { get; set; }
        public ImpactReplayCheck[] ReplayChecks { get; set; } = Array.Empty<ImpactReplayCheck>();
        public int ReplayChecksOmitted { get; set; }
        public ImpactCoverageArea[] Coverage { get; set; } = Array.Empty<ImpactCoverageArea>();
        public string[] Unknowns { get; set; } = Array.Empty<string>();
        public string Summary { get; set; }
        public ImpactNextAction SuggestedNextAction { get; set; }
    }

    internal static class ImpactAssessmentBuilder
    {
        private const int CheckCap = 100;

        public static ImpactAssessmentResult Build(Model model, ImpactAssessmentRequest request,
            ReportAnalysisResult reports, TestSuiteInfo tests, InterviewListResult interview)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            request ??= new ImpactAssessmentRequest();
            var objectRef = (request.ObjectRef ?? "").Trim();
            if (objectRef.Length == 0)
                throw new ArgumentException("impact_assessment needs objectRef. Run search_model or get_lineage, then retry with a returned ref.");
            var intent = NormalizeIntent(request.Intent);
            var scope = NormalizeScope(request.Scope);
            if (scope == "model" && (request.ReportPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) ?? false))
                throw new ArgumentException("scope='model' deliberately excludes reports. Omit reportPaths, or retry with scope='modelAndReports'.");

            var impact = LineageGraph.Impact(model, objectRef);
            var affected = new HashSet<string>(impact.Impacted.Select(x => x.Ref), StringComparer.OrdinalIgnoreCase) { impact.Root };
            var reportHits = BuildReportHits(reports, affected);
            var unknowns = BuildUnknowns(intent, scope, reports, tests, interview);
            var checks = BuildChecks(affected, tests, interview);
            var relevantSavedTests = checks.Count(x => x.Kind == "saved-test");
            var knownReplay = checks.Count(x => x.Kind != "ambient-suite");
            var knownImpact = impact.Impacted.Length > 0 || reportHits.Length > 0;
            var verdict = intent == "remove" && knownImpact ? "Broken"
                : knownImpact || knownReplay > 0 ? "NeedsReview"
                : unknowns.Count > 0 ? "Unknown"
                : "Verified";

            return new ImpactAssessmentResult
            {
                ObjectRef = impact.Root,
                ObjectName = impact.RootName,
                ObjectKind = impact.RootKind,
                ModelName = string.IsNullOrWhiteSpace(model.Database?.Name) ? model.Name : model.Database.Name,
                Intent = intent,
                Scope = scope,
                Verdict = verdict,
                ModelImpact = impact,
                ReportImpact = reportHits,
                ReportsImpacted = reportHits.Length,
                VisualsImpacted = reportHits.Sum(x => x.Visuals),
                ReplayChecks = checks.Take(CheckCap).ToArray(),
                ReplayChecksOmitted = Math.Max(0, checks.Count - CheckCap),
                Coverage = BuildCoverage(scope, reports, tests, interview, relevantSavedTests),
                Unknowns = unknowns.ToArray(),
                Summary = Summary(verdict, intent, impact, reportHits, knownReplay, unknowns.Count),
                SuggestedNextAction = NextAction(request, impact, checks, unknowns),
            };
        }

        internal static string NormalizeIntent(string value)
        {
            var v = (value ?? "change").Trim().ToLowerInvariant();
            if (v == "edit") v = "change";
            if (v == "delete") v = "remove";
            if (v == "change" || v == "rename" || v == "remove" || v == "restructure") return v;
            throw new ArgumentException("Unknown intent '" + value + "'. Use change, rename, remove, or restructure.");
        }

        internal static string NormalizeScope(string value)
        {
            var v = string.IsNullOrWhiteSpace(value) ? "modelAndReports" : value.Trim();
            if (string.Equals(v, "model", StringComparison.OrdinalIgnoreCase)) return "model";
            if (string.Equals(v, "modelAndReports", StringComparison.OrdinalIgnoreCase)) return "modelAndReports";
            throw new ArgumentException("Unknown scope '" + value + "'. Use model or modelAndReports.");
        }

        private static ImpactReportHit[] BuildReportHits(ReportAnalysisResult reports, HashSet<string> affected)
        {
            if (reports?.Reports == null) return Array.Empty<ImpactReportHit>();
            var hits = new List<ImpactReportHit>();
            foreach (var report in reports.Reports.Where(x => x.Read))
            {
                var used = (report.UsedRefs ?? Array.Empty<string>()).Where(affected.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (used.Length == 0) continue;
                var visuals = (report.Visuals ?? Array.Empty<ReportVisualUsage>())
                    .Count(v => (v.UsedRefs ?? Array.Empty<string>()).Any(affected.Contains));
                hits.Add(new ImpactReportHit { Path = report.Path, Name = report.Name, Visuals = visuals, UsedRefs = used });
            }
            return hits.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static List<ImpactReplayCheck> BuildChecks(HashSet<string> affected, TestSuiteInfo tests, InterviewListResult interview)
        {
            var checks = new List<ImpactReplayCheck>
            {
                new ImpactReplayCheck
                {
                    Id = "ambient-suite", Kind = "ambient-suite", Title = "Relationships, table integrity and security",
                    Reason = "Run the ambient suite after the change to catch structural and security regressions.",
                },
            };
            foreach (var test in tests?.Definitions ?? Array.Empty<TestDefinition>())
            {
                if (!test.Enabled || string.IsNullOrWhiteSpace(test.TargetRef) || !affected.Contains(test.TargetRef)) continue;
                checks.Add(new ImpactReplayCheck
                {
                    Id = test.Id, Kind = "saved-test", Title = test.Title, TargetRef = test.TargetRef,
                    Reason = "Its bound measure is the change root or inside the transitive blast radius.",
                });
            }
            foreach (var q in interview?.Questions ?? Array.Empty<InterviewQuestion>())
                checks.Add(new ImpactReplayCheck
                {
                    Id = q.Id, Kind = "interview-question", Title = q.Question,
                    Reason = "Interview questions are not dependency-indexed yet, so replay the current model's pack rather than guessing which question is unaffected.",
                });
            return checks;
        }

        private static List<string> BuildUnknowns(string intent, string scope, ReportAnalysisResult reports,
            TestSuiteInfo tests, InterviewListResult interview)
        {
            var unknowns = new List<string>();
            if (scope == "modelAndReports")
            {
                if (reports == null)
                    unknowns.Add("Published-report usage was not checked. Supply local PBIR reportPaths, or review Published reports in Lineage.");
                else
                {
                    if (reports.ReportsRead == 0) unknowns.Add("No supplied report definition was readable, so report impact is unknown.");
                    if (reports.ReportsUnreadable > 0) unknowns.Add($"{reports.ReportsUnreadable} supplied report definition(s) could not be read.");
                    var unresolved = reports.Reports?.Sum(x => x.Unresolved) ?? 0;
                    if (unresolved > 0) unknowns.Add($"{unresolved} report field reference(s) could not be matched to the open model.");
                    unknowns.Add("Only the supplied report definitions were checked; reports outside that set may still use the object.");
                }
            }
            if ((intent == "rename" || intent == "remove" || intent == "restructure"))
                unknowns.Add("Free-form M text and external bindings are not reference-fixed by the TOM dependency graph; review them before applying this structural change.");
            if ((tests?.UnreadableLines ?? 0) > 0)
                unknowns.Add($"{tests.UnreadableLines} saved Test definition line(s) were unreadable and could not be scheduled for replay.");
            if ((interview?.Questions?.Length ?? 0) > 0)
                unknowns.Add("Model Interview questions are not dependency-indexed; the assessment schedules the whole current-model pack for replay.");
            return unknowns;
        }

        private static ImpactCoverageArea[] BuildCoverage(string scope, ReportAnalysisResult reports,
            TestSuiteInfo tests, InterviewListResult interview, int relevantSavedTests)
        {
            var coverage = new List<ImpactCoverageArea>
            {
                new ImpactCoverageArea { Area = "model", Status = "complete", Checked = 1, Detail = "TOM dependencies and structural references were traversed transitively." },
            };
            if (scope == "model")
                coverage.Add(new ImpactCoverageArea { Area = "reports", Status = "excluded", Detail = "The caller explicitly requested model-only scope." });
            else if (reports == null)
                coverage.Add(new ImpactCoverageArea { Area = "reports", Status = "unknown", Unknown = 1, Detail = "No report definitions were supplied." });
            else
            {
                var unresolved = reports.Reports?.Sum(x => x.Unresolved) ?? 0;
                coverage.Add(new ImpactCoverageArea
                {
                    Area = "reports", Status = reports.ReportsRead > 0 && reports.ReportsUnreadable == 0 && unresolved == 0 ? "scoped" : "incomplete",
                    Checked = reports.ReportsRead, Unknown = reports.ReportsUnreadable + unresolved,
                    Detail = "Coverage is limited to the supplied PBIR definitions; it never claims tenant-wide completeness.",
                });
            }
            coverage.Add(new ImpactCoverageArea
            {
                Area = "saved-tests", Status = (tests?.UnreadableLines ?? 0) == 0 ? "planned" : "incomplete",
                Checked = relevantSavedTests, Unknown = tests?.UnreadableLines ?? 0,
                Detail = "Relevant current-model saved Tests are selected by their bound target ref.",
            });
            coverage.Add(new ImpactCoverageArea
            {
                Area = "interview", Status = (interview?.Questions?.Length ?? 0) == 0 ? "complete" : "planned",
                Checked = interview?.Questions?.Length ?? 0,
                Detail = (interview?.Questions?.Length ?? 0) == 0 ? "No current-model Interview questions exist." : "The whole current-model pack is scheduled because question dependencies are not indexed.",
            });
            return coverage.ToArray();
        }

        private static string Summary(string verdict, string intent, ImpactResult impact, ImpactReportHit[] reports,
            int replay, int unknowns)
        {
            var known = impact.Impacted.Length + reports.Length;
            if (verdict == "Broken") return $"The requested {intent} has {known} known impact(s); removal is not safe as proposed.";
            if (verdict == "NeedsReview") return $"The requested {intent} has {known} known impact(s) and {replay} saved check(s) to replay before applying it.";
            if (verdict == "Unknown") return $"No known impact was found, but {unknowns} coverage gap(s) prevent a clear result.";
            return "No impact was found within the explicitly declared model-only scope.";
        }

        private static ImpactNextAction NextAction(ImpactAssessmentRequest request, ImpactResult impact,
            List<ImpactReplayCheck> checks, List<string> unknowns)
        {
            if (unknowns.Any(x => x.StartsWith("Published-report usage", StringComparison.Ordinal)))
                return new ImpactNextAction
                {
                    Op = "impact_assessment",
                    Args = $"objectRef='{impact.Root}', intent='{NormalizeIntent(request.Intent)}', scope='modelAndReports', reportPaths=[<PBIR paths>]",
                    Reason = "Repeat the assessment with the report definitions that form the intended review scope.",
                };
            if (impact.Measures > 0)
                return new ImpactNextAction
                {
                    Op = "capture_baseline", Args = $"objRef='{impact.Root}', includeDependents=true, groupBy=[<representative columns>]",
                    Reason = "Freeze representative values for the affected measures before changing the model.",
                };
            return new ImpactNextAction
            {
                Op = "run_tests", Args = "persist=false",
                Reason = checks.Count > 1 ? "Run the ambient suite and the scheduled saved checks before editing." : "Establish the current structural and security baseline before editing.",
            };
        }
    }
}
