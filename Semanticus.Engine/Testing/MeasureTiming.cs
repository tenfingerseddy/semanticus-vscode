using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ---- Tests tab E5: clear-cache + single-run MEASURE TIMING (pure core) -----------------------
    // Kane's ruling (2026-07-10): the Tests tab does NOT need warm/cold medians — clear the cache, run
    // once, judge the number. Cold-vs-warm distributions stay DaxBench's job. This file is the PURE
    // verdict/summary logic (no ADOMD, no connection); TimingPlan.cs emits what a live runner executes.

    /// <summary>The four honest states (Tests-tab invariant I1: "Unknown is not a pass").
    /// A run either passes cleanly, fails (broken/over-budget/timed-out), is borderline (Suspect),
    /// or was never measured (NotVerifiable — counts in NEITHER direction, never inflates the grade).</summary>
    public enum TimingVerdict { Pass, Fail, Suspect, NotVerifiable }

    /// <summary>The categorical ROOT CAUSE behind a verdict (invariant I3: report causes, not counts).
    /// Ok = comfortably under budget; Borderline = under budget but inside the warn band; Slow = over
    /// budget; Timeout = hit the hard ceiling; EvaluationError = the measure errored (broken); NotRun =
    /// no measurement exists (offline / cache-clear refused). The three trailing states are ALSO
    /// NotVerifiable, but name a DIFFERENT reason the result can't be trusted: Unidentified = the run
    /// carries no measure name (can't attribute the number); InvalidInstrumentation = a broken (negative)
    /// duration; AmbiguousDuplicate = more than one result was reported for a single planned measure, so we
    /// can't know which is authoritative (never silently double-counted).</summary>
    public enum TimingReason { Ok, Borderline, Slow, Timeout, EvaluationError, NotRun, Unidentified, InvalidInstrumentation, AmbiguousDuplicate }

    /// <summary>The timing budget for a suite. A suite-level <see cref="TargetMs"/> with optional
    /// per-measure <see cref="Overrides"/>, a hard <see cref="TimeoutMs"/> the live runner enforces, and
    /// a <see cref="WarnRatio"/> early-warning band under target.</summary>
    public sealed class TimingPolicy
    {
        public long TargetMs { get; set; } = 1000;        // the suite budget a run must beat
        public long TimeoutMs { get; set; } = 30000;      // hard ceiling — a run past this is cancelled => Fail(timeout)

        // A passing run that has already consumed >= WarnRatio of its budget is flagged Suspect, not Pass:
        // it is trending toward the budget (data grows, the measure creeps). Default 0.8 = a 20% headroom
        // warning band — wide enough to catch a measure creeping toward its limit, narrow enough that
        // genuinely fast measures still read Pass; mirrors the common "80% of quota" capacity alert.
        public double WarnRatio { get; set; } = MeasureTiming.DefaultWarnRatio;

        public Dictionary<string, long> Overrides { get; set; }   // per-measure target overrides (keyed by measure name)

        /// <summary>The effective target for a measure: its override if one is set (and positive), else the suite target.</summary>
        public long TargetFor(string measure) =>
            (Overrides != null && measure != null && Overrides.TryGetValue(measure, out var t) && t > 0) ? t : TargetMs;

        /// <summary>The Suspect-band floor for a measure: a run at or above this (but not over target) is Borderline.
        /// Computed as an integer ms so the boundary is exact and deterministic (no float drift at 0.7999*target).
        /// A ratio outside (0,1) has no meaningful band — >1 would land ABOVE target (already Fail) and <=0 would
        /// warn on every pass — so both collapse to "warn only AT the target line" (threshold = target).</summary>
        public long WarnThresholdFor(string measure)
        {
            var target = TargetFor(measure);
            var r = WarnRatio;
            if (r <= 0 || r >= 1) return target;
            return (long)Math.Ceiling(target * r);
        }
    }

    /// <summary>One clear-cache + single-run RESULT the reconciler judges. A measure that was planned but
    /// not executed (offline endpoint, cache-clear refused) is carried as <c>Ran=false</c> — NOT dropped —
    /// so coverage counts it honestly (invariant I1: it becomes NotVerifiable, never a silent pass).</summary>
    public sealed class MeasureTimingRun
    {
        public string Measure { get; set; }
        public string HomeTable { get; set; }      // carried through for the report (not used in the verdict)
        public bool Ran { get; set; }              // false => absent/offline/not executed => NotVerifiable
        public long DurationMs { get; set; }       // wall-clock of the single timed run (0 when !Ran)
        public bool Success { get; set; }          // the evaluation returned a result (no DAX/engine error)
        public string Error { get; set; }          // the named root cause when Success is false
        public bool TimedOut { get; set; }         // the run hit the hard TimeoutMs ceiling and was cancelled

        /// <summary>A planned-but-unexecuted measure (the honest offline record → NotVerifiable).</summary>
        public static MeasureTimingRun NotRun(string measure, string homeTable = null) =>
            new MeasureTimingRun { Measure = measure, HomeTable = homeTable, Ran = false };
    }

    /// <summary>The verdict for one measure: the four-state <see cref="TimingVerdict"/>, its categorical
    /// <see cref="TimingReason"/>, a human <see cref="Detail"/> that NAMES the numbers, and the effective
    /// target used. <see cref="Verifiable"/> is false ONLY for NotVerifiable — it is the coverage gauge.</summary>
    public sealed class MeasureTimingVerdict
    {
        public string Measure { get; set; }
        public string HomeTable { get; set; }
        public TimingVerdict Verdict { get; set; }
        public TimingReason Reason { get; set; }
        public string Detail { get; set; }         // names duration / target / timeout / error — the root cause
        public long DurationMs { get; set; }       // 0 when the measure was not run
        public long TargetMs { get; set; }         // the EFFECTIVE target (after any per-measure override)
        public bool Verifiable { get; set; }       // false only for NotVerifiable — drives Coverage (I2)
    }

    /// <summary>The suite roll-up: verdict counts + coverage % + total wall-clock + a slowest-N ranking.
    /// Coverage and the counts are always emitted together (invariant I2: a grade is never shown without
    /// the coverage it was scored over — the grade itself is E3/TestHealthAnalyzer's job, not E5's).</summary>
    public sealed class TimingSuiteReport
    {
        public int Total { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public int Suspect { get; set; }
        public int NotVerifiable { get; set; }
        public double CoveragePct { get; set; }    // 100 * verifiable / total (0 for an empty suite)
        public long TotalDurationMs { get; set; }  // wall-clock actually spent running (excludes not-run / negative)
        public int UnplannedResults { get; set; }  // results NOT in the plan (extras / unnamed) — reported, never scored
        public MeasureTimingVerdict[] Verdicts { get; set; } = Array.Empty<MeasureTimingVerdict>();
        public MeasureTimingVerdict[] Slowest { get; set; } = Array.Empty<MeasureTimingVerdict>();
    }

    /// <summary>
    /// The E5 verdict/summary core. <see cref="Evaluate"/> judges ONE clear-cache+single-run result against
    /// a <see cref="TimingPolicy"/>; <see cref="Summarize"/> rolls a suite up into counts + coverage + a
    /// slowest-N ranking. Pure and deterministic — no ADOMD, no connection, no clock — so every branch is
    /// unit-testable offline (the runner that produces <see cref="MeasureTimingRun"/>s executes TimingPlan).
    /// </summary>
    public static class MeasureTiming
    {
        /// <summary>Warn when a run has consumed >= 80% of its budget — a 20% headroom early-warning band.</summary>
        public const double DefaultWarnRatio = 0.8;

        /// <summary>Judge one clear-cache+single-run result. Precedence is DELIBERATE control flow, strongest
        /// signal first: NotVerifiable (no data) → EvaluationError (broken, beats slow) → Timeout → Slow →
        /// Suspect (borderline) → Pass.</summary>
        public static MeasureTimingVerdict Evaluate(MeasureTimingRun run, TimingPolicy policy)
        {
            policy = policy ?? new TimingPolicy();
            ValidatePolicy(policy);   // a garbage budget is caller config to SURFACE, not a data condition to absorb (finding 9)
            var target = policy.TargetFor(run?.Measure);

            // (0) No measurement exists — offline / not executed / cache-clear refused. NotVerifiable, and
            //     NEVER a pass (invariant I1). Checked first: every other field is meaningless when nothing ran.
            if (run == null || !run.Ran)
                return V(run, TimingVerdict.NotVerifiable, TimingReason.NotRun, target,
                    "Not run: no live connection, or the cache clear was refused.", verifiable: false);

            // (0b) IDENTITY is prerequisite to any verdict: a run with no measure name can't be tied to anything,
            //      so it is NotVerifiable ("unidentified result"), NEVER the silent Pass a nameless-but-fast run
            //      used to earn (finding 7). Checked before error/timing — we can't even say WHAT errored/ran.
            if (string.IsNullOrWhiteSpace(run.Measure))
                return V(run, TimingVerdict.NotVerifiable, TimingReason.Unidentified, target,
                    "Unidentified result: the run carries no measure name.", verifiable: false);

            // (1) Evaluation ERROR beats slow: a measure that errors is a BROKEN measure — E5's strongest
            //     signal (a broken measure that happens to finish 'fast' is not a pass). Guarded by
            //     !TimedOut so a timeout that ALSO surfaced a cancellation-error string is named as a
            //     timeout at (2), not as a raw error — the explicit flag is the truer root cause.
            if (!run.Success && !run.TimedOut)
                return V(run, TimingVerdict.Fail, TimingReason.EvaluationError, target,
                    string.IsNullOrWhiteSpace(run.Error) ? "Evaluation failed." : run.Error.Trim(), verifiable: true);

            // (2) Timeout: hit the hard ceiling. Named with TimeoutMs so the report says WHY it failed
            //     (a definite hang), not a vague 'slow'.
            if (run.TimedOut)
                return V(run, TimingVerdict.Fail, TimingReason.Timeout, target,
                    "Timed out after " + policy.TimeoutMs + " ms.", verifiable: true);

            // (2b) A successful run reporting a NEGATIVE duration is broken instrumentation, not a fast measure:
            //      a negative sails under every budget and used to Pass (finding 8). NotVerifiable — and because
            //      V zeroes a not-verifiable duration, it also drops out of the suite total and slowest-N. Reached
            //      only when Success && !TimedOut (an errored/timed-out negative is already named at (1)/(2)).
            if (run.DurationMs < 0)
                return V(run, TimingVerdict.NotVerifiable, TimingReason.InvalidInstrumentation, target,
                    "Invalid instrumentation: negative duration (" + run.DurationMs + " ms).", verifiable: false);

            // (3) Over budget — Slow. Name the duration AND the target it broke (a cause, not a bare count).
            if (run.DurationMs > target)
                return V(run, TimingVerdict.Fail, TimingReason.Slow, target,
                    run.DurationMs + " ms, over the " + target + " ms budget.", verifiable: true);

            // (4) Borderline — under budget but inside the warn band [WarnThreshold, target]. An early
            //     warning (a measure creeping toward its budget), not a failure: Suspect, so it is visible
            //     without failing the suite. Exactly-at-target lands here (100% of budget is not "under").
            var warn = policy.WarnThresholdFor(run.Measure);
            if (run.DurationMs >= warn)
                return V(run, TimingVerdict.Suspect, TimingReason.Borderline, target,
                    run.DurationMs + " ms, within the warn band (>= " + warn + " of the " + target + " ms budget).",
                    verifiable: true);

            // (5) Comfortably under budget — Pass.
            return V(run, TimingVerdict.Pass, TimingReason.Ok, target,
                run.DurationMs + " ms (budget " + target + " ms).", verifiable: true);
        }

        /// <summary>UNRECONCILED roll-up: judges exactly the runs it is handed — verdict counts, coverage %,
        /// total wall-clock, slowest-N. It has NO plan to cross-check against, so a measure that was PLANNED but
        /// silently missing from <paramref name="runs"/> is simply not here and cannot shrink coverage — that gap
        /// is invisible. Use the plan-aware overload (<see cref="Summarize(IEnumerable{TimingTarget},
        /// IEnumerable{MeasureTimingRun}, TimingPolicy, int)"/>) whenever a plan exists, so omissions are caught.</summary>
        public static TimingSuiteReport Summarize(IEnumerable<MeasureTimingRun> runs, TimingPolicy policy, int slowestN = 5)
        {
            policy = policy ?? new TimingPolicy();
            ValidatePolicy(policy);
            var verdicts = (runs ?? Enumerable.Empty<MeasureTimingRun>())
                .Select(r => Evaluate(r, policy)).ToArray();
            return BuildReport(verdicts, slowestN, unplannedResults: 0);
        }

        /// <summary>PLAN-RECONCILED roll-up — the honest one when a plan exists. Reconciles the runs against the
        /// PLANNED measures by normalized identity (trimmed, ordinal/case-insensitive — the model-unique rule
        /// BuildPlan uses): every planned-but-missing measure gets a synthesized NotVerifiable (NotRun) verdict so
        /// a silent omission can NEVER inflate coverage; two results for one measure collapse to a single
        /// NotVerifiable (AmbiguousDuplicate) — neither is trusted, neither double-counted; and results that
        /// aren't in the plan (including nameless ones) are ignored for scoring but surfaced via
        /// <see cref="TimingSuiteReport.UnplannedResults"/>. A blank/null planned target is a caller bug (thrown).</summary>
        public static TimingSuiteReport Summarize(IEnumerable<TimingTarget> plannedTargets,
            IEnumerable<MeasureTimingRun> runs, TimingPolicy policy, int slowestN = 5)
        {
            policy = policy ?? new TimingPolicy();
            ValidatePolicy(policy);

            // Canonical planned identities, first-seen order, deduped case-insensitively (a measure name is
            // model-unique case-insensitively — the same identity rule BuildPlan applies). A null CONTAINER is an
            // empty plan (as in BuildPlan); a null/blank ENTRY is surfaced, never dropped — dropping it is exactly
            // the silent omission this overload exists to catch.
            var planned = new List<TimingTarget>();
            var plannedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in plannedTargets ?? Enumerable.Empty<TimingTarget>())
            {
                if (t == null) throw new ArgumentException("a null planned target is a caller bug (an unidentified measure)", nameof(plannedTargets));
                if (string.IsNullOrWhiteSpace(t.Measure)) throw new ArgumentException("a planned target has a blank measure name (a caller bug)", nameof(plannedTargets));
                var name = t.Measure.Trim();
                if (plannedSeen.Add(name)) planned.Add(new TimingTarget(name, t.HomeTable));
            }

            // Bucket the runs by normalized identity: lets us find the ONE run per measure, DETECT duplicates
            // (refuse to double-count), and count results that aren't in the plan. A blank-named run can't map to
            // any (non-blank) planned measure, so it counts as unplanned — never a silent pass.
            var runsByName = new Dictionary<string, List<MeasureTimingRun>>(StringComparer.OrdinalIgnoreCase);
            var blankNamedRuns = 0;
            foreach (var run in runs ?? Enumerable.Empty<MeasureTimingRun>())
            {
                if (run == null) continue;
                var key = (run.Measure ?? "").Trim();
                if (key.Length == 0) { blankNamedRuns++; continue; }
                if (!runsByName.TryGetValue(key, out var list)) runsByName[key] = list = new List<MeasureTimingRun>();
                list.Add(run);
            }

            var verdicts = new List<MeasureTimingVerdict>(planned.Count);
            foreach (var p in planned)
            {
                runsByName.TryGetValue(p.Measure, out var matches);
                if (matches == null || matches.Count == 0)
                    // planned but never executed — the honest offline record, synthesized, never dropped (I1).
                    verdicts.Add(Evaluate(MeasureTimingRun.NotRun(p.Measure, p.HomeTable), policy));
                else if (matches.Count == 1)
                    verdicts.Add(Evaluate(matches[0], policy));
                else
                    // more than one result for one measure — we can't know which is authoritative, so NEITHER is
                    // trusted and neither is counted: one NotVerifiable verdict naming the ambiguity as the cause.
                    verdicts.Add(new MeasureTimingVerdict
                    {
                        Measure = p.Measure, HomeTable = p.HomeTable,
                        Verdict = TimingVerdict.NotVerifiable, Reason = TimingReason.AmbiguousDuplicate,
                        Detail = matches.Count + " results reported for one measure: ambiguous, cannot verify.",
                        DurationMs = 0, TargetMs = policy.TargetFor(p.Measure), Verifiable = false,
                    });
            }

            // Extras: every result whose identity isn't in the plan (plus the nameless ones) — reported, not scored.
            var unplanned = blankNamedRuns + runsByName.Where(kv => !plannedSeen.Contains(kv.Key)).Sum(kv => kv.Value.Count);
            return BuildReport(verdicts.ToArray(), slowestN, unplanned);
        }

        // The shared count/coverage/duration/slowest math for both overloads. NEGATIVE durations never reach here
        // in a NotVerifiable row (V zeroes those), but a Fail can still carry a bogus negative (an errored run that
        // also reported < 0) — so the total sums only positive durations and slowest-N excludes negatives too.
        private static TimingSuiteReport BuildReport(MeasureTimingVerdict[] verdicts, int slowestN, int unplannedResults)
        {
            var notVerifiable = verdicts.Count(v => v.Verdict == TimingVerdict.NotVerifiable);
            var total = verdicts.Length;
            var verifiable = total - notVerifiable;   // == pass+fail+suspect (the four states partition the suite)

            // Coverage = the share we could actually time (I2 — never a grade without it). NotVerifiable counts in
            // NEITHER direction; it just shrinks coverage. 0 for an empty suite (nothing was timed).
            var coverage = total == 0 ? 0.0 : Math.Round(100.0 * verifiable / total, 1);

            var slowest = verdicts.Where(v => v.Verifiable && v.DurationMs >= 0)
                .OrderByDescending(v => v.DurationMs)
                .ThenBy(v => v.Measure, StringComparer.Ordinal)
                .Take(Math.Max(0, slowestN))
                .ToArray();

            return new TimingSuiteReport
            {
                Total = total,
                Pass = verdicts.Count(v => v.Verdict == TimingVerdict.Pass),
                Fail = verdicts.Count(v => v.Verdict == TimingVerdict.Fail),
                Suspect = verdicts.Count(v => v.Verdict == TimingVerdict.Suspect),
                NotVerifiable = notVerifiable,
                CoveragePct = coverage,
                TotalDurationMs = verdicts.Where(v => v.DurationMs > 0).Sum(v => v.DurationMs),
                UnplannedResults = unplannedResults,
                Verdicts = verdicts, Slowest = slowest,
            };
        }

        // A policy is CALLER configuration; garbage config is a bug to surface, not a data condition to absorb
        // (finding 9). Validated at every public entry point (Evaluate / both Summarize / BuildPlan).
        internal static void ValidatePolicy(TimingPolicy policy)
        {
            if (policy.TargetMs <= 0)
                throw new ArgumentException($"TargetMs must be > 0 (was {policy.TargetMs}).", nameof(policy));
            if (policy.TimeoutMs <= 0)
                throw new ArgumentException($"TimeoutMs must be > 0 (was {policy.TimeoutMs}).", nameof(policy));
            if (!double.IsFinite(policy.WarnRatio))
                throw new ArgumentException($"WarnRatio must be a finite number (was {policy.WarnRatio}).", nameof(policy));
            if (policy.Overrides != null)
                foreach (var kv in policy.Overrides)
                    if (kv.Value <= 0)
                        throw new ArgumentException($"per-measure target override for '{kv.Key}' must be > 0 (was {kv.Value}).", nameof(policy));
        }

        private static MeasureTimingVerdict V(MeasureTimingRun run, TimingVerdict verdict, TimingReason reason,
            long target, string detail, bool verifiable) =>
            new MeasureTimingVerdict
            {
                Measure = run?.Measure,
                HomeTable = run?.HomeTable,
                Verdict = verdict,
                Reason = reason,
                Detail = detail,
                // A NotVerifiable row carries NO trustworthy duration (not-run, unidentified, negative/invalid) — 0
                // it, so a broken/absent measurement can never leak into the suite total or the slowest-N ranking.
                DurationMs = (verifiable && run != null && run.Ran) ? run.DurationMs : 0,
                TargetMs = target,
                Verifiable = verifiable,
            };
    }
}
