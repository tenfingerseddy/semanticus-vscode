using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ---- Tests tab E5: the PLAN a live runner executes to produce MeasureTimingRun results -------
    // Pure plan generation — the ordered steps only. No ADOMD, no connection, nothing executed here:
    // this file decides WHAT to run (and in what order, with the DAX escaped right); a live runner
    // walks the steps (DaxCache.ClearAsync + LiveConnection.ExecuteAsync) and feeds the timings back
    // into MeasureTiming.Summarize. Kept execution-free so the shape is fully unit-testable offline.

    /// <summary>What a step does. Per measure the plan is exactly one <see cref="ClearCache"/> then one
    /// <see cref="Evaluate"/> — no warm re-run (Kane's ruling: clear cache, single run).</summary>
    public enum TimingStepKind { ClearCache, Evaluate }

    /// <summary>A measure to time: its name + home table (home table is carried for the report / lineage
    /// binding; the reference itself needs no table qualifier — measures are model-global).</summary>
    public sealed class TimingTarget
    {
        public string Measure { get; set; }
        public string HomeTable { get; set; }

        public TimingTarget() { }
        public TimingTarget(string measure, string homeTable = null) { Measure = measure; HomeTable = homeTable; }
    }

    /// <summary>One ordered step in the timing plan. A ClearCache step has no <see cref="Dax"/>; an
    /// Evaluate step carries the escaped EVALUATE query and the hard <see cref="TimeoutMs"/> ceiling the
    /// runner enforces on it.</summary>
    public sealed class TimingStep
    {
        public int Order { get; set; }          // 1-based position in the execution sequence (transparency + stable ordering)
        public string Measure { get; set; }
        public string HomeTable { get; set; }
        public TimingStepKind Kind { get; set; }
        public string Dax { get; set; }         // the EVALUATE query (Evaluate step); null for ClearCache
        public long TimeoutMs { get; set; }     // the ceiling the runner enforces (Evaluate step); 0 for ClearCache
    }

    /// <summary>
    /// Builds the ordered clear-cache + single-run plan for a set of measures. Deterministic (preserves the
    /// caller's order; dedupes repeats), and escapes measure names correctly so a name containing ']' can't
    /// break the DAX. Executes NOTHING — see the file header.
    /// </summary>
    public static class TimingPlan
    {
        /// <summary>The single timed query for one measure: <c>EVALUATE ROW("v", [Measure])</c>. ROW wraps the
        /// scalar so a bare measure is a valid table expression; "v" is a fixed, safe column name; a bare
        /// [Name] reference (no table qualifier) is unambiguous because measure names are model-global.</summary>
        public static string EvaluateQuery(string measure) => "EVALUATE\nROW(\"v\", " + MeasureRef(measure) + ")";

        /// <summary>A measure reference — <c>[Name]</c> with every literal ']' DOUBLED (the DAX escape). A
        /// measure named <c>Rev ]x</c> would otherwise close the bracket early; '[' inside is literal and is
        /// left alone (only ']' terminates the reference), matching the DAX identifier rules.</summary>
        public static string MeasureRef(string measure) => "[" + (measure ?? "").Trim().Replace("]", "]]") + "]";

        /// <summary>Emit the ordered plan: for each distinct measure, a ClearCache step then ONE Evaluate step
        /// (no warm run). Blank/duplicate targets are dropped deterministically so there is exactly one timed
        /// run per measure. The clear precedes the evaluate so the single run is genuinely cold.</summary>
        public static List<TimingStep> BuildPlan(IEnumerable<TimingTarget> targets, TimingPolicy policy)
        {
            policy = policy ?? new TimingPolicy();
            MeasureTiming.ValidatePolicy(policy);   // no plan off a garbage budget (esp. a negative TimeoutMs emitted into every step) — finding 9
            var steps = new List<TimingStep>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // measure names are model-unique case-insensitively
            var order = 0;

            foreach (var t in targets ?? Enumerable.Empty<TimingTarget>())
            {
                // A blank/null target is a CALLER bug (a measure you meant to time but never named) — surface it
                // rather than silently drop it, which would quietly shrink the plan and inflate coverage (finding 7).
                if (t == null) throw new ArgumentException("a null timing target is a caller bug (an unidentified measure)", nameof(targets));
                if (string.IsNullOrWhiteSpace(t.Measure)) throw new ArgumentException("a timing target has a blank measure name (a caller bug)", nameof(targets));
                var name = t.Measure.Trim();
                if (!seen.Add(name)) continue;   // one clear+run per distinct measure — a case-insensitive repeat is a dedupe, not junk

                steps.Add(new TimingStep
                {
                    Order = ++order, Measure = name, HomeTable = t.HomeTable, Kind = TimingStepKind.ClearCache,
                });
                steps.Add(new TimingStep
                {
                    Order = ++order, Measure = name, HomeTable = t.HomeTable, Kind = TimingStepKind.Evaluate,
                    Dax = EvaluateQuery(name), TimeoutMs = policy.TimeoutMs,
                });
            }
            return steps;
        }
    }
}
