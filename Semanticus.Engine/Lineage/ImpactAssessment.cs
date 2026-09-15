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
        /// <summary>Optional LOCAL report folders, folded into the model's report scope for this one call. The
        /// normal path is to choose reports once (set_report_scope) and read them (check_reports); this stays for a
        /// caller that has a local folder in hand.</summary>
        public string[] ReportPaths { get; set; } = Array.Empty<string>();
    }

    /// <summary>What this answer was actually checked against. Named, so no surface has to invent it.</summary>
    public sealed class ImpactCheckedScope
    {
        /// <summary>The model the answer was traversed against, by name.</summary>
        public string Model { get; set; }
        public ReportScopeReport[] Reports { get; set; } = Array.Empty<ReportScopeReport>();
        public int ReportsChecked { get; set; }
        public int ReportsListed { get; set; }
        public string CheckedWhenUtc { get; set; }
        /// <summary>Everything this check does NOT establish, one plain sentence each.</summary>
        public string[] Gaps { get; set; } = Array.Empty<string>();
    }

    public sealed class ImpactCoverageArea
    {
        public string Area { get; set; }                             // model | reports | saved-tests
        public string Status { get; set; }                           // complete | excluded | scoped | incomplete | unknown | planned
        public int Checked { get; set; }
        public int Unknown { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>Where in ONE visual the field shows up. Astra: hiding the raw visual type by dropping every
    /// detail was not the repair asked for. The type is translated here; the id stays for a person who needs it.</summary>
    public sealed class ImpactVisualHit
    {
        public string Page { get; set; }
        /// <summary>The visual's own title when the report has one, else null.</summary>
        public string Visual { get; set; }
        /// <summary>The kind of visual in words ("Bar chart"), never the report's raw type token.</summary>
        public string VisualKind { get; set; }
        /// <summary>The raw type token, kept for a person who asks for the detail. Never a headline.</summary>
        public string VisualKindId { get; set; }
        /// <summary>Which of the affected things this visual shows: the field itself, or something that depends
        /// on it. That distinction is the whole point of showing the detail.</summary>
        public string[] UsedRefs { get; set; } = Array.Empty<string>();
        public bool ViaDependent { get; set; }
    }

    public sealed class ImpactReportHit
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int Visuals { get; set; }
        public string[] UsedRefs { get; set; } = Array.Empty<string>();
        /// <summary>The pages and visuals the field appears on. Restored after the first build dropped them.</summary>
        public ImpactVisualHit[] Details { get; set; } = Array.Empty<ImpactVisualHit>();
    }

    public sealed class ImpactReplayCheck
    {
        public string Id { get; set; }
        public string Kind { get; set; }                             // ambient-suite | saved-test
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
        /// <summary>The report half of the answer, said honestly: what was found in the reports that were read,
        /// and what was not read. Null only when the caller asked for model-only scope.</summary>
        public string ReportSummary { get; set; }
        /// <summary>What this answer was checked against: the model, the named reports with state and time, gaps.</summary>
        public ImpactCheckedScope Checked { get; set; }
        public ImpactNextAction SuggestedNextAction { get; set; }
    }

    internal static class ImpactAssessmentBuilder
    {
        private const int CheckCap = 100;

        public static ImpactAssessmentResult Build(Model model, ImpactAssessmentRequest request,
            ReportAnalysisResult reports, TestSuiteInfo tests, ReportScopeResult scope, bool savedChecksArePro = false)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            request ??= new ImpactAssessmentRequest();
            var objectRef = (request.ObjectRef ?? "").Trim();
            if (objectRef.Length == 0)
                throw new ArgumentException("impact_assessment needs objectRef. Run search_model or get_lineage, then retry with a returned ref.");
            var intent = NormalizeIntent(request.Intent);
            var scopeName = NormalizeScope(request.Scope);
            if (scopeName == "model" && (request.ReportPaths?.Any(p => !string.IsNullOrWhiteSpace(p)) ?? false))
                throw new ArgumentException("scope='model' deliberately excludes reports. Omit reportPaths, or retry with scope='modelAndReports'.");

            var impact = LineageGraph.Impact(model, objectRef);
            var affected = new HashSet<string>(impact.Impacted.Select(x => x.Ref), StringComparer.OrdinalIgnoreCase) { impact.Root };
            var reportHits = BuildReportHits(reports, affected, impact.Root);
            var unknowns = BuildUnknowns(intent, scopeName, reports, tests, scope, savedChecksArePro);
            var checks = BuildChecks(affected, tests);
            var relevantSavedTests = checks.Count(x => x.Kind == "saved-test");
            var knownReplay = checks.Count(x => x.Kind != "ambient-suite");
            var knownImpact = impact.Impacted.Length > 0 || reportHits.Length > 0;
            var verdict = intent == "remove" && knownImpact ? "Broken"
                : knownImpact || knownReplay > 0 ? "NeedsReview"
                : unknowns.Count > 0 ? "Unknown"
                : "Verified";
            var modelName = string.IsNullOrWhiteSpace(model.Database?.Name) ? model.Name : model.Database.Name;

            return new ImpactAssessmentResult
            {
                ObjectRef = impact.Root,
                ObjectName = impact.RootName,
                ObjectKind = impact.RootKind,
                ModelName = modelName,
                Intent = intent,
                Scope = scopeName,
                Verdict = verdict,
                ModelImpact = impact,
                ReportImpact = reportHits,
                ReportsImpacted = reportHits.Length,
                VisualsImpacted = reportHits.Sum(x => x.Visuals),
                ReplayChecks = checks.Take(CheckCap).ToArray(),
                ReplayChecksOmitted = Math.Max(0, checks.Count - CheckCap),
                Coverage = BuildCoverage(scopeName, reports, tests, relevantSavedTests, savedChecksArePro),
                Unknowns = unknowns.ToArray(),
                Summary = Summary(intent, impact),
                ReportSummary = scopeName == "model" ? null : ReportSummary(impact, reportHits, scope),
                Checked = new ImpactCheckedScope
                {
                    Model = modelName,
                    Reports = scopeName == "model" ? Array.Empty<ReportScopeReport>() : (scope?.Reports ?? Array.Empty<ReportScopeReport>()),
                    ReportsChecked = scopeName == "model" ? 0 : (scope?.Checked ?? 0),
                    ReportsListed = scopeName == "model" ? 0 : (scope?.Listed ?? 0),
                    CheckedWhenUtc = scopeName == "model" ? null : scope?.CheckedWhenUtc,
                    Gaps = unknowns.ToArray(),
                },
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

        /// <summary>A report's own type token in words. "barChart" is the report format's word, not a person's.</summary>
        internal static string PlainVisualKind(string visualType)
        {
            if (string.IsNullOrWhiteSpace(visualType)) return "Visual";
            var spaced = System.Text.RegularExpressions.Regex.Replace(visualType.Trim(), "(?<=[a-z0-9])(?=[A-Z])", " ");
            spaced = spaced.Replace('_', ' ').Replace('-', ' ');
            spaced = System.Text.RegularExpressions.Regex.Replace(spaced, "\\s+", " ").Trim();
            return spaced.Length == 0 ? "Visual" : char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant();
        }

        private static ImpactReportHit[] BuildReportHits(ReportAnalysisResult reports, HashSet<string> affected, string root)
        {
            if (reports?.Reports == null) return Array.Empty<ImpactReportHit>();
            var hits = new List<ImpactReportHit>();
            foreach (var report in reports.Reports.Where(x => x.Read))
            {
                var used = (report.UsedRefs ?? Array.Empty<string>()).Where(affected.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (used.Length == 0) continue;
                var touching = (report.Visuals ?? Array.Empty<ReportVisualUsage>())
                    .Where(v => (v.UsedRefs ?? Array.Empty<string>()).Any(affected.Contains)).ToArray();
                hits.Add(new ImpactReportHit
                {
                    Path = report.Path, Name = report.Name, Visuals = touching.Length, UsedRefs = used,
                    Details = touching.Take(200).Select(v =>
                    {
                        var shown = (v.UsedRefs ?? Array.Empty<string>()).Where(affected.Contains).ToArray();
                        return new ImpactVisualHit
                        {
                            Page = v.Page,
                            Visual = v.Visual,
                            VisualKind = PlainVisualKind(v.VisualType),
                            VisualKindId = v.VisualType,
                            UsedRefs = shown,
                            // The field itself, or only something downstream of it. A person deleting a column
                            // needs to know a visual shows it THROUGH a measure, not directly.
                            ViaDependent = shown.Length > 0 && !shown.Contains(root, StringComparer.OrdinalIgnoreCase),
                        };
                    }).ToArray(),
                });
            }
            return hits.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // The automatic suite checks structure. It does NOT prove security, and the old title said it did.
        private static List<ImpactReplayCheck> BuildChecks(HashSet<string> affected, TestSuiteInfo tests)
        {
            var checks = new List<ImpactReplayCheck>
            {
                new ImpactReplayCheck
                {
                    Id = "ambient-suite", Kind = "ambient-suite", Title = "Relationships and table integrity",
                    Reason = "Run the automatic checks after the change to catch structural problems.",
                },
            };
            foreach (var test in tests?.Definitions ?? Array.Empty<TestDefinition>())
            {
                if (!test.Enabled || string.IsNullOrWhiteSpace(test.TargetRef) || !affected.Contains(test.TargetRef)) continue;
                checks.Add(new ImpactReplayCheck
                {
                    Id = test.Id, Kind = "saved-test", Title = test.Title, TargetRef = test.TargetRef,
                    Reason = "This check is bound to something on this list. Run it after the change.",
                });
            }
            return checks;
        }

        // Every gap, as a sentence a person can act on. The report half comes from the model's own report scope,
        // so the page and the assistant read the same account of what was and was not checked.
        private static List<string> BuildUnknowns(string intent, string scopeName, ReportAnalysisResult reports,
            TestSuiteInfo tests, ReportScopeResult scope, bool savedChecksArePro)
        {
            var unknowns = new List<string>();
            if (savedChecksArePro)
                unknowns.Add("Saved checks were not looked at, because Tests is a Semanticus Pro feature. Any check bound to something on this list is missing from it.");
            if (scopeName == "modelAndReports")
            {
                if (scope != null && scope.Gaps.Length > 0) unknowns.AddRange(scope.Gaps);
                else if (reports == null) unknowns.Add("Reports have not been checked. Choose reports to check this against.");
                else unknowns.Add("Only the reports listed here were checked. A report that is not on this list can still use this field.");
                var unresolved = reports?.Reports?.Sum(x => x.Unresolved) ?? 0;
                if (unresolved > 0)
                    unknowns.Add(unresolved + " thing" + (unresolved == 1 ? "" : "s") + " a report shows could not be matched to this model, so its use is not fully known.");
            }
            if (intent == "rename" || intent == "remove" || intent == "restructure")
                unknowns.Add("Data-loading steps written by hand, and anything outside this model that reads it, are not part of this check. Look at those before making a structural change.");
            if ((tests?.UnreadableLines ?? 0) > 0)
                unknowns.Add(tests.UnreadableLines + " saved check" + (tests.UnreadableLines == 1 ? "" : "s") + " could not be read, so " + (tests.UnreadableLines == 1 ? "it was" : "they were") + " left out of this list.");
            return unknowns;
        }

        private static ImpactCoverageArea[] BuildCoverage(string scopeName, ReportAnalysisResult reports,
            TestSuiteInfo tests, int relevantSavedTests, bool savedChecksArePro)
        {
            var coverage = new List<ImpactCoverageArea>
            {
                new ImpactCoverageArea { Area = "model", Status = "complete", Checked = 1, Detail = "Everything in the model that uses it was traced, step by step." },
            };
            if (scopeName == "model")
                coverage.Add(new ImpactCoverageArea { Area = "reports", Status = "excluded", Detail = "The caller asked for the model on its own." });
            else if (reports == null)
                coverage.Add(new ImpactCoverageArea { Area = "reports", Status = "unknown", Unknown = 1, Detail = "No report has been checked yet." });
            else
            {
                var unresolved = reports.Reports?.Sum(x => x.Unresolved) ?? 0;
                coverage.Add(new ImpactCoverageArea
                {
                    Area = "reports", Status = reports.ReportsRead > 0 && reports.ReportsUnreadable == 0 && unresolved == 0 ? "scoped" : "incomplete",
                    Checked = reports.ReportsRead, Unknown = reports.ReportsUnreadable + unresolved,
                    Detail = "Only the reports on this model's list were read. It never claims to cover every report.",
                });
            }
            coverage.Add(new ImpactCoverageArea
            {
                Area = "saved-tests",
                Status = savedChecksArePro ? "unknown" : (tests?.UnreadableLines ?? 0) == 0 ? "planned" : "incomplete",
                Checked = relevantSavedTests, Unknown = tests?.UnreadableLines ?? 0,
                Detail = savedChecksArePro
                    ? "Saved checks were not read: Tests is a Semanticus Pro feature."
                    : "Saved checks are picked by the thing they are bound to. Nothing here runs them.",
            });
            return coverage.ToArray();
        }

        // The answer, as a sentence. Not a verdict word, not a count of "known impacts": the thing a person asked.
        internal static string Summary(string intent, ImpactResult impact)
        {
            var name = impact.RootName;
            var parts = new List<string>();
            void Add(int n, string one, string many) { if (n > 0) parts.Add(n + " " + (n == 1 ? one : many)); }
            Add(impact.Measures, "measure", "measures");
            Add(impact.Columns, "column", "columns");
            Add(impact.Tables, "table", "tables");
            Add(impact.Relationships, "relationship", "relationships");
            Add(impact.Other, "other thing", "other things");
            if (parts.Count == 0) return "Nothing in the model uses " + name + ".";
            var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
            return intent == "remove"
                ? "Deleting " + name + " would break " + list + "."
                : name + " is used by " + list + ".";
        }

        // The report half. Never a clean "no use" when something was not read, and never a claim about reports
        // that do not contain the field. Every sentence here is pinned by docs/report-scope-sentences.json, which
        // the page's own formatter is held to independently: Astra's ruling 2 found the two doors already meaning
        // different things about a stale reading, which no amount of shared vocabulary fixes on its own.
        internal static string ReportSummary(ImpactResult impact, ImpactReportHit[] hits, ReportScopeResult scope)
        {
            var name = impact.RootName;
            var listed = scope?.Listed ?? 0;
            var checkedCount = scope?.Checked ?? 0;
            var partly = scope?.CouldNotBeFullyChecked ?? 0;
            var notChecked = scope?.NotChecked ?? 0;
            var needs = scope?.NeedsChecking ?? 0;
            if (listed == 0) return "Report use is unknown. No reports have been chosen for this model yet.";
            // A reading the model has moved past is NOT a reading that never happened: it has its own sentence and
            // its own next step, and the page says exactly this.
            if (checkedCount == 0 && partly == 0 && needs > 0)
                return "Report use needs checking again. The model changed after "
                    + (needs == 1 ? "1 listed report was" : needs + " listed reports were") + " read.";
            if (checkedCount == 0 && partly == 0)
                return "Report use is unknown. The chosen reports still need checking.";

            var tail = new List<string>();
            if (notChecked > 0) tail.Add((notChecked == 1 ? "1 listed report was" : notChecked + " listed reports were") + " not checked");
            if (partly > 0) tail.Add((partly == 1 ? "1 listed report" : partly + " listed reports") + " could not be fully checked");
            if (needs > 0) tail.Add((needs == 1 ? "1 listed report needs" : needs + " listed reports need") + " checking again");
            var suffix = tail.Count == 0 ? "" : " " + string.Join("; ", tail) + ".";

            if (hits.Length == 0)
            {
                var read = scope.Reports.Where(r => r.State == ReportScopeStates.Checked).Select(r => r.Name).ToArray();
                return "No use of " + name + " or the things that depend on it was found in the "
                    + (checkedCount == 1 ? "1 report checked" : checkedCount + " reports checked")
                    + (read.Length > 0 ? " (" + JoinNames(read) + ")" : "") + "." + suffix;
            }
            // Name the reports that ACTUALLY contain it. Naming every checked report here told a person that a
            // report which does not use the field does use it, which is the opposite of the intended reassurance.
            var others = checkedCount - hits.Length;
            var otherSentence = others <= 0 ? ""
                : " The other " + (others == 1 ? "checked report does" : others + " checked reports do") + " not use it.";
            return name + " or something that depends on it appears in "
                + (hits.Length == 1 ? "1 checked report" : hits.Length + " checked reports")
                + " (" + JoinNames(hits.Select(h => h.Name).ToArray()) + ")." + otherSentence + suffix;
        }

        private static string JoinNames(string[] names)
        {
            if (names.Length == 0) return "";
            if (names.Length == 1) return names[0];
            if (names.Length <= 4) return string.Join(", ", names.Take(names.Length - 1)) + " and " + names[names.Length - 1];
            return string.Join(", ", names.Take(3)) + " and " + (names.Length - 3) + " more";
        }

        private static ImpactNextAction NextAction(ImpactAssessmentRequest request, ImpactResult impact,
            List<ImpactReplayCheck> checks, List<string> unknowns)
        {
            if (unknowns.Any(x => x.StartsWith("Reports have not been checked", StringComparison.Ordinal)))
                return new ImpactNextAction
                {
                    Op = "check_reports",
                    Args = "ids=[] (every report chosen for this model), consent=true for published reports",
                    Reason = "Choose the reports this model should be checked against, then read them, so the answer covers reports too.",
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
                Reason = checks.Count > 1 ? "Run the automatic checks and the saved checks on this list before editing." : "Establish the current structural baseline before editing.",
            };
        }
    }
}
