using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Tests tab E5 — clear-cache + single-run MEASURE TIMING. Pins the pure verdict/summary core
    /// (<see cref="MeasureTiming"/>) and the plan generator (<see cref="TimingPlan"/>) OFFLINE (no live
    /// endpoint). Covers: every verdict state reachable; the precedence rules (error-beats-slow,
    /// timeout-beats-slow) that make the verdict trustworthy; the invariant I1 line ("absent is NOT a
    /// pass"); per-measure overrides; the warn-band boundary; DAX escaping; plan shape (clear precedes
    /// each single run, no warm re-run); and the summary/coverage/slowest-N math.
    /// </summary>
    public sealed class MeasureTimingTests
    {
        // ---- fixtures ----------------------------------------------------------------------------

        private static TimingPolicy Policy(long target = 1000, long timeout = 30000, double warn = 0.8,
            Dictionary<string, long>? overrides = null) =>
            new TimingPolicy { TargetMs = target, TimeoutMs = timeout, WarnRatio = warn, Overrides = overrides };

        private static MeasureTimingRun Ran(string m, long ms, bool success = true, string? error = null,
            bool timedOut = false, string? home = null) =>
            new MeasureTimingRun { Measure = m, HomeTable = home, Ran = true, DurationMs = ms, Success = success, Error = error, TimedOut = timedOut };

        // ---- (1) every verdict state is reachable ------------------------------------------------

        [Fact]
        public void Comfortably_under_budget_is_Pass()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 100), Policy(1000, warn: 0.8));   // 100 < 800 warn floor
            Assert.Equal(TimingVerdict.Pass, v.Verdict);
            Assert.Equal(TimingReason.Ok, v.Reason);
            Assert.True(v.Verifiable);
            Assert.Equal(100, v.DurationMs);
            Assert.Equal(1000, v.TargetMs);
        }

        [Fact]
        public void Over_budget_is_Fail_slow_naming_duration_and_target()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 1500), Policy(1000));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.Slow, v.Reason);
            Assert.Contains("1500", v.Detail);   // root cause names BOTH numbers
            Assert.Contains("1000", v.Detail);
            Assert.True(v.Verifiable);            // a Fail is still a verified result — it counts in coverage
        }

        [Fact]
        public void Evaluation_error_is_Fail_naming_the_error_text()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 50, success: false, error: "Column 'X' not found"), Policy(1000));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.EvaluationError, v.Reason);
            Assert.Equal("Column 'X' not found", v.Detail);   // the error IS the named root cause
        }

        [Fact]
        public void Failed_run_without_error_text_still_Fails_never_a_silent_pass()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 20, success: false, error: null), Policy(1000));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.EvaluationError, v.Reason);
            Assert.Equal("Evaluation failed.", v.Detail);
        }

        [Fact]
        public void Timeout_is_Fail_naming_the_timeout_ms()
        {
            var run = new MeasureTimingRun { Measure = "M", Ran = true, TimedOut = true, DurationMs = 30000, Success = false };
            var v = MeasureTiming.Evaluate(run, Policy(1000, timeout: 30000));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.Timeout, v.Reason);
            Assert.Contains("30000", v.Detail);
        }

        [Fact]
        public void At_or_inside_the_warn_band_is_Suspect()
        {
            // target 1000, warn 0.8 => floor 800. The band is [800, 1000].
            Assert.Equal(TimingVerdict.Suspect, MeasureTiming.Evaluate(Ran("M", 800), Policy(1000, warn: 0.8)).Verdict); // floor (inclusive)
            Assert.Equal(TimingVerdict.Suspect, MeasureTiming.Evaluate(Ran("M", 999), Policy(1000, warn: 0.8)).Verdict); // just under target
            var v = MeasureTiming.Evaluate(Ran("M", 900), Policy(1000, warn: 0.8));
            Assert.Equal(TimingReason.Borderline, v.Reason);
            Assert.True(v.Verifiable);   // Suspect is a real measurement — it counts in coverage
        }

        [Fact]
        public void Absent_run_is_NotVerifiable_and_NEVER_a_Pass()   // invariant I1
        {
            foreach (var run in new MeasureTimingRun?[] { MeasureTimingRun.NotRun("M"), new MeasureTimingRun { Measure = "M", Ran = false }, null })
            {
                var v = MeasureTiming.Evaluate(run, Policy());
                Assert.Equal(TimingVerdict.NotVerifiable, v.Verdict);
                Assert.NotEqual(TimingVerdict.Pass, v.Verdict);   // the whole point of I1
                Assert.Equal(TimingReason.NotRun, v.Reason);
                Assert.False(v.Verifiable);       // does not count toward coverage
                Assert.Equal(0, v.DurationMs);
            }
        }

        // ---- (2) precedence: the rules that make the verdict trustworthy -------------------------

        [Fact]
        public void Error_beats_slow()   // an erroring measure is broken — the strongest signal, even if 'slow' too
        {
            var v = MeasureTiming.Evaluate(Ran("M", 999999, success: false, error: "boom"), Policy(1000));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.EvaluationError, v.Reason);   // NOT Slow, despite being wildly over budget
            Assert.Equal("boom", v.Detail);
        }

        [Fact]
        public void Timeout_beats_slow()
        {
            var run = new MeasureTimingRun { Measure = "M", Ran = true, TimedOut = true, DurationMs = 999999, Success = false };
            var v = MeasureTiming.Evaluate(run, Policy(1000, timeout: 5000));
            Assert.Equal(TimingReason.Timeout, v.Reason);   // NOT Slow — the ceiling is the cause
            Assert.Contains("5000", v.Detail);
        }

        [Fact]
        public void Timeout_with_a_cancellation_error_string_is_named_timeout_not_error()
        {
            // A live runner often sets BOTH timedOut and a "query cancelled" error string. The explicit flag
            // is the truer root cause, so the verdict reads Timeout — documents the deliberate tiebreak.
            var run = new MeasureTimingRun { Measure = "M", Ran = true, TimedOut = true, DurationMs = 5000, Success = false, Error = "Operation cancelled" };
            var v = MeasureTiming.Evaluate(run, Policy(1000, timeout: 5000));
            Assert.Equal(TimingReason.Timeout, v.Reason);
        }

        // ---- (3) the warn-band boundary ----------------------------------------------------------

        [Fact]
        public void Exactly_at_target_is_Suspect_not_Pass_not_Fail()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 1000), Policy(1000, warn: 0.8));
            Assert.Equal(TimingVerdict.Suspect, v.Verdict);   // 100% of budget is not "under" (Pass) and not "over" (Fail)
            Assert.Equal(TimingReason.Borderline, v.Reason);
        }

        [Fact]
        public void Just_below_the_warn_band_is_Pass()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 799), Policy(1000, warn: 0.8));   // 799 < 800 floor
            Assert.Equal(TimingVerdict.Pass, v.Verdict);
        }

        [Fact]
        public void Just_over_target_is_Fail_not_Suspect()
        {
            var v = MeasureTiming.Evaluate(Ran("M", 1001), Policy(1000, warn: 0.8));
            Assert.Equal(TimingVerdict.Fail, v.Verdict);
            Assert.Equal(TimingReason.Slow, v.Reason);
        }

        [Fact]
        public void Default_policy_warn_ratio_is_0_8()
        {
            Assert.Equal(0.8, new TimingPolicy().WarnRatio);
            Assert.Equal(0.8, MeasureTiming.DefaultWarnRatio);
            Assert.Equal(800, new TimingPolicy { TargetMs = 1000 }.WarnThresholdFor("anything"));
        }

        [Fact]
        public void Warn_ratio_of_one_collapses_the_band_to_the_target_line()
        {
            var p = Policy(1000, warn: 1.0);
            Assert.Equal(TimingVerdict.Pass, MeasureTiming.Evaluate(Ran("M", 999), p).Verdict);      // below target passes
            Assert.Equal(TimingVerdict.Suspect, MeasureTiming.Evaluate(Ran("M", 1000), p).Verdict);  // only exactly-at-target warns
        }

        // ---- (4) per-measure target override -----------------------------------------------------

        [Fact]
        public void Per_measure_target_override_applies_and_others_use_the_suite_target()
        {
            var p = Policy(1000, overrides: new Dictionary<string, long> { ["Slow M"] = 100 });

            var overridden = MeasureTiming.Evaluate(Ran("Slow M", 150), p);   // 150 > its 100 budget (though < 1000)
            Assert.Equal(TimingVerdict.Fail, overridden.Verdict);
            Assert.Equal(TimingReason.Slow, overridden.Reason);
            Assert.Equal(100, overridden.TargetMs);
            Assert.Contains("100", overridden.Detail);

            var other = MeasureTiming.Evaluate(Ran("Other", 150), p);          // no override => suite 1000 => Pass
            Assert.Equal(TimingVerdict.Pass, other.Verdict);
            Assert.Equal(1000, other.TargetMs);
        }

        [Fact]
        public void Warn_band_tracks_the_overridden_target()
        {
            var p = Policy(1000, warn: 0.8, overrides: new Dictionary<string, long> { ["M"] = 100 });
            // override 100, warn 0.8 => floor 80. 90 is inside [80,100] => Suspect (against 100, not 1000).
            var v = MeasureTiming.Evaluate(Ran("M", 90), p);
            Assert.Equal(TimingVerdict.Suspect, v.Verdict);
            Assert.Equal(100, v.TargetMs);
        }

        // ---- (5) DAX escaping + plan queries -----------------------------------------------------

        [Fact]
        public void Measure_ref_doubles_the_right_bracket_only()
        {
            Assert.Equal("[Sales]", TimingPlan.MeasureRef("Sales"));
            Assert.Equal("[Rev ]]x]", TimingPlan.MeasureRef("Rev ]x"));   // ] doubled
            Assert.Equal("[a]]]]b]", TimingPlan.MeasureRef("a]]b"));      // two ] => four
            Assert.Equal("[A[B]", TimingPlan.MeasureRef("A[B"));          // '[' is literal, left alone
            Assert.Equal("[M]", TimingPlan.MeasureRef("  M  "));          // trimmed
        }

        [Fact]
        public void Evaluate_query_is_row_wrapped_and_escapes_the_name()
        {
            Assert.Equal("EVALUATE\nROW(\"v\", [Sales])", TimingPlan.EvaluateQuery("Sales"));
            Assert.Equal("EVALUATE\nROW(\"v\", [Rev ]]x])", TimingPlan.EvaluateQuery("Rev ]x"));
        }

        // ---- (6) plan shape: clear precedes each single run, no warm re-run ----------------------

        [Fact]
        public void Plan_is_clear_then_one_evaluate_per_measure_in_input_order()
        {
            var plan = TimingPlan.BuildPlan(new[] { new TimingTarget("A", "T1"), new TimingTarget("B", "T2") }, Policy());

            Assert.Equal(4, plan.Count);   // 2 measures x (clear + evaluate)
            // deterministic interleave, input order preserved: A-clear, A-eval, B-clear, B-eval
            Assert.Equal(new[] { TimingStepKind.ClearCache, TimingStepKind.Evaluate, TimingStepKind.ClearCache, TimingStepKind.Evaluate },
                plan.Select(s => s.Kind).ToArray());
            Assert.Equal(new[] { "A", "A", "B", "B" }, plan.Select(s => s.Measure).ToArray());
            Assert.Equal(new[] { 1, 2, 3, 4 }, plan.Select(s => s.Order).ToArray());

            foreach (var g in plan.GroupBy(s => s.Measure))
            {
                Assert.Single(g, s => s.Kind == TimingStepKind.Evaluate);    // EXACTLY one run — no warm re-run
                Assert.Single(g, s => s.Kind == TimingStepKind.ClearCache);
                var clear = g.First(s => s.Kind == TimingStepKind.ClearCache);
                var eval = g.First(s => s.Kind == TimingStepKind.Evaluate);
                Assert.True(clear.Order < eval.Order);                       // clear precedes the run => genuinely cold
            }
        }

        [Fact]
        public void Plan_evaluate_step_carries_the_query_and_timeout_clear_carries_neither()
        {
            var plan = TimingPlan.BuildPlan(new[] { new TimingTarget("Sales", "Fact") }, Policy(timeout: 12345));

            var eval = plan.Single(s => s.Kind == TimingStepKind.Evaluate);
            Assert.Equal("EVALUATE\nROW(\"v\", [Sales])", eval.Dax);
            Assert.Equal(12345, eval.TimeoutMs);
            Assert.Equal("Fact", eval.HomeTable);

            var clear = plan.Single(s => s.Kind == TimingStepKind.ClearCache);
            Assert.Null(clear.Dax);
            Assert.Equal(0, clear.TimeoutMs);
        }

        [Fact]
        public void Plan_dedupes_a_case_insensitive_repeat()
        {
            var plan = TimingPlan.BuildPlan(
                new[] { new TimingTarget("A"), new TimingTarget("a"), new TimingTarget("B") },
                Policy());

            // "a" is a case-insensitive dup of "A" (measure names are model-unique) => distinct {A, B} only.
            Assert.Equal(new[] { "A", "B" }, plan.Where(s => s.Kind == TimingStepKind.Evaluate).Select(s => s.Measure).ToArray());
            Assert.Equal(4, plan.Count);
        }

        [Fact]
        public void Empty_target_list_builds_an_empty_plan()
        {
            Assert.Empty(TimingPlan.BuildPlan(null, Policy()));
            Assert.Empty(TimingPlan.BuildPlan(Array.Empty<TimingTarget>(), Policy()));
        }

        // ---- (7) suite summary: counts, coverage, total duration, slowest-N ----------------------

        [Fact]
        public void Summary_counts_each_state_and_pairs_coverage_with_them()
        {
            var runs = new[]
            {
                Ran("Pass", 100),                                  // Pass
                Ran("Slow", 1500),                                 // Fail
                Ran("Borderline", 900),                            // Suspect
                MeasureTimingRun.NotRun("Offline"),                // NotVerifiable
            };
            var r = MeasureTiming.Summarize(runs, Policy(1000, warn: 0.8));

            Assert.Equal(4, r.Total);
            Assert.Equal(1, r.Pass);
            Assert.Equal(1, r.Fail);
            Assert.Equal(1, r.Suspect);
            Assert.Equal(1, r.NotVerifiable);
            Assert.Equal(75.0, r.CoveragePct);   // 3 verifiable / 4 total (I2 — never shown without the counts)
            // all four states are reachable through the real summarizer
            Assert.Equal(4, r.Verdicts.Length);
        }

        [Fact]
        public void Coverage_excludes_not_verifiable_which_never_counts_as_a_pass()
        {
            var runs = new[] { Ran("A", 100), MeasureTimingRun.NotRun("B"), MeasureTimingRun.NotRun("C"), MeasureTimingRun.NotRun("D") };
            var r = MeasureTiming.Summarize(runs, Policy());
            Assert.Equal(1, r.Pass);
            Assert.Equal(3, r.NotVerifiable);
            Assert.Equal(25.0, r.CoveragePct);   // 1 / 4 — the 3 not-run neither pass nor fail, just shrink coverage
        }

        [Fact]
        public void Total_duration_sums_run_wall_clock_including_slow_and_timeout_excluding_not_run()
        {
            var timedOut = new MeasureTimingRun { Measure = "T", Ran = true, TimedOut = true, DurationMs = 5000, Success = false };
            var runs = new[] { Ran("A", 100), Ran("B", 1500), timedOut, MeasureTimingRun.NotRun("C") };
            var r = MeasureTiming.Summarize(runs, Policy(1000, timeout: 5000));
            Assert.Equal(6600, r.TotalDurationMs);   // 100 + 1500 + 5000 (+ 0 for the not-run)
        }

        [Fact]
        public void Slowest_n_ranks_by_duration_desc_and_excludes_not_verifiable()
        {
            var runs = new[] { Ran("A", 100), Ran("B", 1500), Ran("C", 800), MeasureTimingRun.NotRun("D") };
            var r = MeasureTiming.Summarize(runs, Policy(), slowestN: 2);
            Assert.Equal(2, r.Slowest.Length);
            Assert.Equal("B", r.Slowest[0].Measure);   // 1500
            Assert.Equal("C", r.Slowest[1].Measure);   // 800
            Assert.All(r.Slowest, v => Assert.True(v.Verifiable));   // D (not-run) is never ranked
        }

        [Fact]
        public void Slowest_n_tie_break_is_deterministic_by_name()
        {
            var runs = new[] { Ran("X", 500), Ran("A", 500) };   // equal durations
            var r = MeasureTiming.Summarize(runs, Policy(), slowestN: 5);
            Assert.Equal(new[] { "A", "X" }, r.Slowest.Select(v => v.Measure).ToArray());   // stable: name asc
        }

        [Fact]
        public void Empty_suite_is_zero_coverage_zero_counts_zero_duration()
        {
            var r = MeasureTiming.Summarize(Array.Empty<MeasureTimingRun>(), Policy());
            Assert.Equal(0, r.Total);
            Assert.Equal(0.0, r.CoveragePct);
            Assert.Equal(0, r.TotalDurationMs);
            Assert.Empty(r.Verdicts);
            Assert.Empty(r.Slowest);
        }

        // ---- review-fix pins (adversarial hostile-input findings) --------------------------------

        // Finding 6: the reconciled Summarize cross-checks runs against the PLAN. A planned measure missing from
        // the runs is synthesized as NotVerifiable — it can't silently vanish and inflate coverage to 100%.
        [Fact]
        public void Reconciled_summary_flags_a_planned_measure_missing_from_the_runs()
        {
            var planned = new[] { new TimingTarget("A"), new TimingTarget("B") };
            var runs = new[] { Ran("A", 100) };   // B was planned but never ran

            var r = MeasureTiming.Summarize(planned, runs, Policy());
            Assert.Equal(2, r.Total);              // BOTH planned measures accounted for, not just the one that ran
            Assert.Equal(1, r.Pass);
            Assert.Equal(1, r.NotVerifiable);
            Assert.Equal(50.0, r.CoveragePct);     // 1 of 2 — NOT the vacuous 100 the unreconciled overload shows
            var b = r.Verdicts.Single(v => v.Measure == "B");
            Assert.Equal(TimingVerdict.NotVerifiable, b.Verdict);
            Assert.Equal(TimingReason.NotRun, b.Reason);
        }

        [Fact]
        public void Unreconciled_summary_cannot_see_the_omission()   // documents WHY the reconciled overload exists
        {
            var r = MeasureTiming.Summarize(new[] { Ran("A", 100) }, Policy());
            Assert.Equal(1, r.Total);
            Assert.Equal(100.0, r.CoveragePct);    // the gap ({B} was planned) is invisible without the plan
        }

        // Finding 6: two results for ONE planned measure — neither trusted, neither double-counted; the measure
        // becomes a single NotVerifiable(AmbiguousDuplicate).
        [Fact]
        public void Reconciled_summary_refuses_to_double_count_a_duplicate_result()
        {
            var planned = new[] { new TimingTarget("A") };
            var runs = new[] { Ran("A", 100), Ran("A", 200) };   // ambiguous: which is authoritative?

            var r = MeasureTiming.Summarize(planned, runs, Policy());
            Assert.Equal(1, r.Total);              // ONE verdict for the one planned measure, never two
            Assert.Equal(0, r.Pass);
            Assert.Equal(1, r.NotVerifiable);
            Assert.Equal(0.0, r.CoveragePct);
            Assert.Equal(TimingReason.AmbiguousDuplicate, r.Verdicts.Single().Reason);
            Assert.Equal(0, r.TotalDurationMs);    // neither 100 nor 200 leaked into the total
        }

        // Finding 6: a result NOT in the plan is ignored for scoring but surfaced via UnplannedResults.
        [Fact]
        public void Reconciled_summary_reports_but_does_not_score_unplanned_results()
        {
            var planned = new[] { new TimingTarget("A") };
            var runs = new[] { Ran("A", 100), Ran("Z", 9999) };   // Z isn't in the plan

            var r = MeasureTiming.Summarize(planned, runs, Policy());
            Assert.Equal(1, r.Total);              // only the planned A is scored
            Assert.Equal(1, r.Pass);
            Assert.Equal(100.0, r.CoveragePct);
            Assert.Equal(1, r.UnplannedResults);   // Z is reported, not scored
            Assert.DoesNotContain(r.Verdicts, v => v.Measure == "Z");
        }

        [Fact]
        public void Reconciled_summary_throws_on_a_blank_planned_target()
        {
            Assert.Throws<ArgumentException>(() =>
                MeasureTiming.Summarize(new[] { new TimingTarget("  ") }, new[] { Ran("A", 100) }, Policy()));
        }

        // Finding 7: a nameless run can't be attributed — NotVerifiable("unidentified"), never the silent Pass a
        // nameless-but-fast run used to earn.
        [Fact]
        public void Nameless_run_is_NotVerifiable_never_a_Pass()
        {
            var run = new MeasureTimingRun { Measure = "", Ran = true, DurationMs = 100, Success = true };
            var v = MeasureTiming.Evaluate(run, Policy());
            Assert.Equal(TimingVerdict.NotVerifiable, v.Verdict);
            Assert.NotEqual(TimingVerdict.Pass, v.Verdict);   // the whole point
            Assert.Equal(TimingReason.Unidentified, v.Reason);
            Assert.False(v.Verifiable);
        }

        // Finding 7: a blank/null PLANNED target is a caller bug — BuildPlan throws rather than silently drop it.
        [Fact]
        public void BuildPlan_throws_on_a_blank_or_null_target()
        {
            Assert.Throws<ArgumentException>(() => TimingPlan.BuildPlan(new[] { new TimingTarget("  ") }, Policy()));
            Assert.Throws<ArgumentException>(() => TimingPlan.BuildPlan(new[] { new TimingTarget("A"), null }, Policy()));
        }

        // Finding 8: a successful run reporting a NEGATIVE duration is broken instrumentation, not a fast pass.
        [Fact]
        public void Negative_duration_is_NotVerifiable_never_a_Pass()
        {
            var v = MeasureTiming.Evaluate(Ran("M", -5), Policy(1000));
            Assert.Equal(TimingVerdict.NotVerifiable, v.Verdict);
            Assert.NotEqual(TimingVerdict.Pass, v.Verdict);
            Assert.Equal(TimingReason.InvalidInstrumentation, v.Reason);
            Assert.Equal(0, v.DurationMs);   // the bogus negative is zeroed, not carried through
        }

        [Fact]
        public void Negative_duration_is_excluded_from_total_and_slowest()
        {
            var runs = new[] { Ran("A", 100), Ran("Bad", -5), Ran("C", 50) };
            var r = MeasureTiming.Summarize(runs, Policy(1000));
            Assert.Equal(150, r.TotalDurationMs);                       // 100 + 50 — the -5 never subtracts or pollutes
            Assert.DoesNotContain(r.Slowest, v => v.Measure == "Bad");  // a negative time is never ranked
            Assert.Equal(1, r.NotVerifiable);
        }

        // Finding 9: a garbage policy is caller config to SURFACE — every public entry point throws.
        [Fact]
        public void Invalid_policy_target_is_rejected_at_every_entry_point()
        {
            var bad = Policy(target: -1);
            Assert.Throws<ArgumentException>(() => MeasureTiming.Evaluate(Ran("M", 100), bad));
            Assert.Throws<ArgumentException>(() => MeasureTiming.Summarize(new[] { Ran("M", 100) }, bad));
            Assert.Throws<ArgumentException>(() => MeasureTiming.Summarize(new[] { new TimingTarget("M") }, new[] { Ran("M", 100) }, bad));
            Assert.Throws<ArgumentException>(() => TimingPlan.BuildPlan(new[] { new TimingTarget("M") }, bad));
        }

        [Fact]
        public void Invalid_policy_timeout_is_rejected()
        {
            var bad = Policy(timeout: -1);
            Assert.Throws<ArgumentException>(() => MeasureTiming.Evaluate(Ran("M", 100), bad));
            Assert.Throws<ArgumentException>(() => TimingPlan.BuildPlan(new[] { new TimingTarget("M") }, bad));   // -1 would otherwise be emitted into every step
        }

        [Fact]
        public void Non_finite_warn_ratio_is_rejected()   // NaN bypassed the range collapse and produced a garbage threshold
        {
            Assert.Throws<ArgumentException>(() => MeasureTiming.Evaluate(Ran("M", 100), Policy(warn: double.NaN)));
            Assert.Throws<ArgumentException>(() => MeasureTiming.Evaluate(Ran("M", 100), Policy(warn: double.PositiveInfinity)));
        }

        [Fact]
        public void Non_positive_per_measure_override_is_rejected()
        {
            var bad = Policy(overrides: new Dictionary<string, long> { ["M"] = -1 });
            Assert.Throws<ArgumentException>(() => MeasureTiming.Evaluate(Ran("M", 100), bad));
        }
    }
}
