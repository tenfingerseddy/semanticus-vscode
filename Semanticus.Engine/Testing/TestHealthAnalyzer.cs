using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>One saved reconcile definition's run outcome, reduced to the shared vocabulary. The analyzer
    /// grades on <see cref="ReconcileRunResult.Status"/> + the caveat facts (never AnyMismatch alone): an
    /// UNCAVEATED Reconciled is Pass; Mismatch is Fail; everything else (insufficient coverage, input error,
    /// not connected, refused) is NotVerifiable — checked-and-uncertain must never move the grade (I1).</summary>
    public sealed class ReconcileOutcome
    {
        public string DefId { get; set; }
        public string Title { get; set; }
        public string TargetRef { get; set; }
        public Verdict Verdict { get; set; }
        public string Message { get; set; }
        public bool Missing { get; set; }            // the bound target vanished — surfaced, never silently dropped

        // ---- drill-down evidence (carried on the LIVE run only; the lean history record never stores it) ----
        // All nullable/absent-tolerant: an offline or refused run has a verdict but no evidence, and the UI
        // renders the gap honestly rather than fake it.
        public string Sql { get; set; }              // the human-accepted ground-truth SQL (the caller's own text)
        public string Dax { get; set; }              // the engine-authored DAX actually executed
        public CompareRow[] Rows { get; set; }       // capped: grand total first, then worst mismatches, then the rest
        public int RowsTotal { get; set; }           // uncapped cell count, so "showing N of M" is honest
        public int Matches { get; set; }
        public int Mismatches { get; set; }
        public int Unverifiable { get; set; }
        public long? DurationMs { get; set; }        // the timing pass's cold ms when budgeted, else the warm reconcile query ms
        public long? SqlDurationMs { get; set; }
        public long? BudgetMs { get; set; }          // the user-declared budget (never a default — see the coordinator)
        public Verdict? TimingVerdict { get; set; }  // clear-cache single-run judgement; null when no budget was set
        public string TimingDetail { get; set; }     // names the numbers (duration / budget / error), the E5 Detail
        public string CreatedBy { get; set; }        // provenance: "human" | "agent" (accepted ground truth)
        public string CreatedWhen { get; set; }
        public string ToleranceNote { get; set; }    // effective tolerances + blank policy (+ a loose-window warning)

        /// <summary>E6: the base measure's time-intelligence variants, each judged CONSISTENT with the base
        /// via a deterministic identity (TimeIntelligenceVariants). Evidence about the family, not independent
        /// tests: they never enter the category verdicts; the Explanation travels so a NotVerifiable chip can
        /// say WHY (I1). Null until the coordinator wiring runs them.</summary>
        public TiVariantVerdict[] Variants { get; set; }
    }

    /// <summary>One filter context of a reconciliation, flattened for the UI's compare table. Verdict speaks the
    /// SHARED vocabulary (Pass/Fail/NotVerifiable — a cell is never Suspect); the nuance lives in Explanation.</summary>
    public sealed class CompareRow
    {
        public string Context { get; set; }          // joined grouping key; "Grand total" for the empty key
        public decimal? Sql { get; set; }            // null = SQL NULL / missing (the explanation says which)
        public decimal? Dax { get; set; }            // null = DAX BLANK / missing
        public decimal? Delta { get; set; }          // DAX minus SQL; null when there is no numeric comparison
        public Verdict Verdict { get; set; }
        public string Explanation { get; set; }
        public bool GrandTotal { get; set; }
    }

    public sealed class TestCategoryHealth
    {
        public string Category { get; set; }         // Correctness | Integrity | Security | Performance
        public double Weight { get; set; }
        public bool HasChecks { get; set; }          // false ⇒ dormant: contributes nothing to the overall
        public int Checked { get; set; }             // verdict-bearing checks in this category
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Suspect { get; set; }
        public int NotVerifiable { get; set; }
        public double Score { get; set; }            // 100 * Passed / (Passed + Failed); Suspect/NV excluded (I1)
    }

    /// <summary>The graded suite health. Grade and CoveragePct are ONE unit (I2) — no door or surface may show
    /// one without the other; RootFailures is the headline number (I3) — Suspects are cascade, not cause.</summary>
    public sealed class TestHealth
    {
        public double Overall { get; set; }
        public string Grade { get; set; }
        public string[] GatedBy { get; set; } = Array.Empty<string>();
        public double CoveragePct { get; set; }      // decisive / all verdict-bearing checks, suite-wide
        public int Checked { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Suspect { get; set; }
        public int NotVerifiable { get; set; }
        public int Missing { get; set; }
        public int RootFailures { get; set; }
        public TestCategoryHealth[] Categories { get; set; } = Array.Empty<TestCategoryHealth>();
    }

    /// <summary>
    /// E3 — clones the ReadinessAnalyzer scoring discipline for tests: weighted categories, a category with no
    /// decisive checks stays DORMANT (excluded from the weighted average — the Applicable=0 dormant-or-dock
    /// convention; its NotVerifiable checks still count in COVERAGE, so an offline run reads "A on 0% coverage",
    /// which I2 makes impossible to mistake for health), and hard gates override the average downward. PURE:
    /// reports in, health out — no I/O, no model, identical on both doors.
    /// </summary>
    public static class TestHealthAnalyzer
    {
        // Weighted like ReadinessAnalyzer.Weights: correctness (the wedge) heaviest, then data integrity,
        // security, performance. Normalized over PRESENT categories, so a model with no saved reconcile tests
        // is graded on what was actually testable rather than docked for suite breadth (coverage carries that).
        private static readonly (string Name, double Weight)[] Categories =
        {
            ("Correctness", 0.40),
            ("Integrity", 0.30),
            ("Security", 0.20),
            ("Performance", 0.10),   // active only when a user DECLARES budgets (E5): no budget, no timing verdicts, stays dormant
        };

        public static TestHealth Analyze(
            RelationshipIntegrityReport relationships,
            SecurityStaticReport security,
            IReadOnlyList<ReconcileOutcome> reconciles,
            IReadOnlyList<Verdict> timing = null)
        {
            reconciles ??= Array.Empty<ReconcileOutcome>();
            timing ??= Array.Empty<Verdict>();
            var relChecks = relationships?.Relationships?.SelectMany(r => r.Checks).Where(c => c != null).ToList() ?? new List<CheckResult>();
            var tableChecks = relationships?.TableRowCounts?.Select(r => r.Check).Where(c => c != null).ToList() ?? new List<CheckResult>();
            var integrityChecks = relChecks.Concat(tableChecks).ToList();
            var secChecks = security?.Filters?.Select(f => f.Check).Where(c => c != null).ToList() ?? new List<CheckResult>();
            // MISSING definitions are surfaced in the tally but are NOT verdict-bearing: the test didn't run
            // against anything, so it can move neither the grade nor the coverage denominator dishonestly —
            // it gets its own loud count instead (hard thing #4: the suite rots visibly).
            var missing = reconciles.Count(o => o.Missing);
            var reconVerdicts = reconciles.Where(o => !o.Missing).Select(o => o.Verdict).ToList();

            var cats = new List<TestCategoryHealth>();
            foreach (var (name, weight) in Categories)
            {
                List<Verdict> v = name switch
                {
                    "Correctness" => reconVerdicts,
                    "Integrity" => integrityChecks.Select(c => c.Verdict).ToList(),
                    "Security" => secChecks.Select(c => c.Verdict).ToList(),
                    // Timing verdicts exist only for measures whose test DECLARED a budget (an over-budget run is a
                    // user-defined failure, so it may move the grade); no budgets ⇒ empty ⇒ the category stays dormant.
                    "Performance" => timing.ToList(),
                    _ => new List<Verdict>(),
                };
                int pass = v.Count(x => x == Verdict.Pass), fail = v.Count(x => x == Verdict.Fail);
                int susp = v.Count(x => x == Verdict.Suspect), nv = v.Count(x => x == Verdict.NotVerifiable);
                var decisive = pass + fail;
                cats.Add(new TestCategoryHealth
                {
                    Category = name,
                    Weight = weight,
                    // Dormant unless something DECIDED: a category that is all-NotVerifiable (offline probes)
                    // must not average in at 100 — that would grade the un-checked as healthy (I1).
                    HasChecks = decisive > 0,
                    Checked = v.Count,
                    Passed = pass,
                    Failed = fail,
                    Suspect = susp,
                    NotVerifiable = nv,
                    Score = decisive > 0 ? Math.Round(100.0 * pass / decisive, 1) : 100.0,
                });
            }

            var present = cats.Where(c => c.HasChecks).ToList();
            var wsum = present.Sum(c => c.Weight);
            var raw = wsum > 0 ? present.Sum(c => c.Weight * c.Score) / wsum : 100.0;

            // Hard gate (mirrors ReadinessAnalyzer's LIMIT-SCALE cap): a FAILED integrity check is trust-destroying
            // — orphans land on the hidden blank row so totals still tie while every per-member breakdown is wrong,
            // and duplicate keys silently fan out fact rows. A mostly-green suite must never present an A over that.
            var gatedBy = new List<string>();
            var overall = raw;
            var integrityFails = integrityChecks.Count(c => c.Verdict == Verdict.Fail);
            if (integrityFails > 0)
            {
                overall = Math.Min(overall, 60);
                gatedBy.Add($"{integrityFails} integrity check(s) failed: capped at D until the data is fixed");
            }

            int checkedAll = cats.Sum(c => c.Checked);
            int nvAll = cats.Sum(c => c.NotVerifiable);
            int suspAll = cats.Sum(c => c.Suspect);
            var decisiveAll = cats.Sum(c => c.Passed + c.Failed);
            return new TestHealth
            {
                Overall = Math.Round(overall, 1),
                Grade = GradeFor(overall),
                GatedBy = gatedBy.ToArray(),
                // Coverage counts DECIDED checks only: Suspect is "awaiting its root cause", NotVerifiable is
                // "couldn't check" — neither is coverage a user can lean on.
                CoveragePct = checkedAll > 0 ? Math.Round(100.0 * decisiveAll / checkedAll, 1) : 0.0,
                Checked = checkedAll,
                Passed = cats.Sum(c => c.Passed),
                Failed = cats.Sum(c => c.Failed),
                Suspect = suspAll,
                NotVerifiable = nvAll,
                Missing = missing,
                // E4 demotes intra-relationship cascades and the coordinator demotes cross-MEASURE cascades
                // (DemoteDependentFailures) before this runs, so every surviving Fail IS a root cause. The
                // relationship→measure cascade is still a later refinement; until then those Fails count as
                // roots — over-reporting roots is honest, under-reporting hides causes.
                RootFailures = cats.Sum(c => c.Failed),
                Categories = cats.ToArray(),
            };
        }

        // The one grade scale (same thresholds as ReadinessAnalyzer.GradeFor).
        private static string GradeFor(double s) => s >= 90 ? "A" : s >= 80 ? "B" : s >= 70 ? "C" : s >= 60 ? "D" : "F";
    }
}
