using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The SQL-vs-DAX reconciler — the correctness spine of the Tests tab. These pin the hard semantics
    /// OFFLINE (no connection): float tolerance (never ==), the BLANK/NULL/0 matrix under all three
    /// policies, the VertiPaq blank-row trap (a matching grand total must NOT be reportable as a pass when
    /// a member disagrees), missing-cell handling, WorstOffender selection, and — the review round —
    /// the run-level Status/coverage model that makes "!AnyMismatch is not a pass" structurally true:
    /// total-only and all-unverifiable runs are never green, malformed input is an InputError, and
    /// coverage is counted against the INPUT cardinality.
    /// </summary>
    public sealed class MeasureReconcilerTests
    {
        // ---- builders -------------------------------------------------------------------------------
        private static TolerancePolicy Pol(BlankPolicy b) =>
            new TolerancePolicy { Relative = 1e-7, Absolute = 1e-9, Blank = b };

        private static ReconcileCell Val(string[] key, decimal dax, decimal sql) =>
            new ReconcileCell { GroupingKey = key, Dax = dax, Sql = sql };

        // DAX BLANK() on one side (present, empty). SQL side supplied by the caller.
        private static ReconcileCell DaxBlankSql(string[] key, decimal? sql, bool sqlNull = false) =>
            new ReconcileCell { GroupingKey = key, Dax = null, DaxBlank = true, Sql = sql, SqlNull = sqlNull };

        // A MISSING row (no join match) on one side: value null AND the empty flag false — distinct from a
        // present BLANK/NULL. This is the state the "absent from source/model" wording keys off.
        private static ReconcileCell MissingDax(string[] key, decimal sql) =>
            new ReconcileCell { GroupingKey = key, Dax = null, DaxBlank = false, Sql = sql };

        private static ReconcileCell MissingSql(string[] key, decimal dax) =>
            new ReconcileCell { GroupingKey = key, Dax = dax, Sql = null, SqlNull = false };

        private static ReconcileResult One(ReconcileCell c, BlankPolicy b) =>
            MeasureReconciler.Reconcile(new[] { c }, Pol(b));

        private static ReconcileVerdict VerdictOf(ReconcileResult r) => r.Cells.Single().Verdict;

        private static readonly string[] K = { "East" };   // an arbitrary single-member key
        private static readonly string[] Total = new string[0];  // grand total = empty key

        // ============================================================================================
        //  (1) FLOAT TOLERANCE — never '=='
        // ============================================================================================

        [Fact]
        public void Exact_equal_values_match_with_zero_delta()
        {
            var r = One(Val(K, 1000m, 1000m), BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));
            Assert.Equal(0m, r.Cells[0].Delta);
            Assert.False(r.AnyMismatch);
        }

        [Fact]
        public void Tiny_float_difference_within_relative_tolerance_matches()
        {
            // 0.05 off a million: threshold = rel(1e-7) * 1e6 = 0.1, so 0.05 is inside. A naive == would fail.
            var r = One(Val(K, 1000000.05m, 1000000m), BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));
        }

        [Fact]
        public void Difference_beyond_relative_tolerance_mismatches()
        {
            // 0.2 off a million: threshold 0.1, so it fails.
            var r = One(Val(K, 1000000.2m, 1000000m), BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(r));
            Assert.True(r.AnyMismatch);
        }

        [Fact]
        public void Relative_tolerance_scales_with_magnitude()
        {
            // At 1e9 the relative window is 100; +50 passes, +150 fails — proves the term is rel*abs(sql).
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(Val(K, 1000000050m, 1000000000m), BlankPolicy.BlankIsZero)));
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(Val(K, 1000000150m, 1000000000m), BlankPolicy.BlankIsZero)));
        }

        [Fact]
        public void Sql_zero_boundary_falls_back_to_the_absolute_floor()
        {
            // sql==0 => the relative term vanishes; only the 1e-9 absolute floor decides.
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(Val(K, 0.0000000005m, 0m), BlankPolicy.BlankIsZero)));   // 5e-10 <= 1e-9
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(Val(K, 0.001m, 0m), BlankPolicy.BlankIsZero)));       // 1e-3  >  1e-9
        }

        [Fact]
        public void Both_zero_is_a_clean_match()
        {
            var r = One(Val(K, 0m, 0m), BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));
            Assert.Equal(0.0, r.Cells[0].RelDelta);
        }

        [Fact]
        public void Delta_exactly_equal_to_the_threshold_matches_pinning_less_than_or_equal()
        {
            // Threshold = max(Absolute 0.5, Relative 0 * 100) = 0.5, an exactly-representable double. A delta
            // of exactly 0.5 must MATCH (pins the boundary as <=, not <); a hair beyond must FAIL.
            var policy = new TolerancePolicy { Absolute = 0.5, Relative = 0.0, Blank = BlankPolicy.BlankIsZero };
            Assert.Equal(ReconcileVerdict.Match, MeasureReconciler.Reconcile(new[] { Val(K, 100.5m, 100m) }, policy).Cells.Single().Verdict);
            Assert.Equal(ReconcileVerdict.Mismatch, MeasureReconciler.Reconcile(new[] { Val(K, 100.5000001m, 100m) }, policy).Cells.Single().Verdict);
        }

        [Fact]
        public void Negative_sql_uses_its_absolute_value_for_the_relative_window()
        {
            // sql=-1e6 => window = rel(1e-7) * abs(-1e6) = 0.1. A -0.05 delta is inside; -0.2 is outside. If
            // abs(sql) were NOT used, max(1e-9, -0.1) would collapse to the floor and 0.05 would spuriously fail.
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(Val(K, -1000000.05m, -1000000m), BlankPolicy.BlankIsZero)));
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(Val(K, -1000000.2m, -1000000m), BlankPolicy.BlankIsZero)));
        }

        // ============================================================================================
        //  (2) BLANK / NULL / ZERO matrix — the subtlest part. Test EVERY combination per policy.
        // ============================================================================================

        // ---- BlankIsZero: blank==null==0 ----
        [Fact]
        public void Zero_policy_blank_vs_zero_matches()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(DaxBlankSql(K, 0m), BlankPolicy.BlankIsZero)));

        [Fact]
        public void Zero_policy_null_vs_zero_matches()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(
                new ReconcileCell { GroupingKey = K, Dax = 0m, Sql = null, SqlNull = true }, BlankPolicy.BlankIsZero)));

        [Fact]
        public void Zero_policy_blank_vs_null_matches()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(DaxBlankSql(K, null, sqlNull: true), BlankPolicy.BlankIsZero)));

        [Fact]
        public void Zero_policy_blank_vs_real_value_mismatches()
        {
            // blank read as 0 vs 500 => a real divergence, with the honest magnitude in the delta.
            var r = One(DaxBlankSql(K, 500m), BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(r));
            Assert.Equal(-500m, r.Cells[0].Delta);
        }

        // ---- BlankIsNull: blank~null, both differ from 0 ----
        [Fact]
        public void Null_policy_blank_vs_null_matches_as_both_empty()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(DaxBlankSql(K, null, sqlNull: true), BlankPolicy.BlankIsNull)));

        [Fact]
        public void Null_policy_blank_vs_zero_mismatches()
            => Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(DaxBlankSql(K, 0m), BlankPolicy.BlankIsNull)));

        [Fact]
        public void Null_policy_null_vs_zero_mismatches()
            => Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(
                new ReconcileCell { GroupingKey = K, Dax = 0m, Sql = null, SqlNull = true }, BlankPolicy.BlankIsNull)));

        [Fact]
        public void Null_policy_blank_vs_value_mismatches()
            => Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(DaxBlankSql(K, 500m), BlankPolicy.BlankIsNull)));

        [Fact]
        public void Null_policy_value_vs_value_still_matches()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(Val(K, 42m, 42m), BlankPolicy.BlankIsNull)));

        // ---- BlankIsDistinct: blank != null != 0 ----
        [Fact]
        public void Distinct_policy_blank_vs_null_is_unverifiable()
        {
            // The one honest "cannot judge" outcome: a DAX BLANK is not equatable to a SQL NULL here.
            var r = One(DaxBlankSql(K, null, sqlNull: true), BlankPolicy.BlankIsDistinct);
            Assert.Equal(ReconcileVerdict.UnverifiableBlank, VerdictOf(r));
            Assert.False(r.AnyMismatch);          // unverifiable does NOT fail the run...
            Assert.Equal(1, r.Unverifiable);      // ...but it IS surfaced, never hidden
        }

        [Fact]
        public void Distinct_policy_blank_vs_zero_mismatches()
            => Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(DaxBlankSql(K, 0m), BlankPolicy.BlankIsDistinct)));

        [Fact]
        public void Distinct_policy_blank_vs_value_mismatches()
            => Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(DaxBlankSql(K, 500m), BlankPolicy.BlankIsDistinct)));

        [Fact]
        public void Distinct_policy_value_vs_sql_null_mismatches_reversed_direction()
        {
            // The reverse of blank-vs-value: a real DAX value facing a present SQL NULL. One side empty, one
            // side a value => MISMATCH under BlankIsDistinct (blank/null/0 are all distinct).
            var c = new ReconcileCell { GroupingKey = K, Dax = 42m, Sql = null, SqlNull = true };
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(c, BlankPolicy.BlankIsDistinct)));
        }

        [Fact]
        public void Distinct_policy_value_vs_value_matches()
            => Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(Val(K, 7m, 7m), BlankPolicy.BlankIsDistinct)));

        // ============================================================================================
        //  (3) THE VERTIPAQ BLANK-ROW TRAP — the whole point. Total ties, a member is wrong.
        // ============================================================================================

        [Fact]
        public void Grand_total_matches_but_a_member_disagrees_is_never_reported_as_a_pass()
        {
            // Classic invalid-relationship reparenting: the grand total is computed by its OWN query and
            // still ties (350==350) while a member breakdown is wrong (West 200 vs 250). A reconciler that
            // trusted the total would LIE. Here AnyMismatch must be true and the summary must name it.
            var cells = new[]
            {
                Val(Total, 350m, 350m),          // grand total: ties
                Val(new[] { "East" }, 100m, 100m),  // member: matches
                Val(new[] { "West" }, 200m, 250m),  // member: WRONG
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));

            Assert.True(r.AnyMismatch);                                 // impossible to call this a pass
            Assert.Equal(ReconcileStatus.Mismatch, r.Status);
            Assert.Equal(2, r.Matches);
            Assert.Equal(1, r.Mismatches);
            Assert.Contains("Grand total matches but", r.Summary);
            Assert.Contains("members disagree", r.Summary);
            Assert.Equal(new[] { "West" }, r.WorstOffender.GroupingKey); // the offender the UI jumps to
        }

        [Fact]
        public void A_total_only_reconcile_is_insufficient_coverage_even_when_it_ties()
        {
            // The bug the review caught: fed ONLY the (matching) grand total, the OLD code said "All clear".
            // A matching total cannot rule out the blank-row trap — an invalid relationship can reparent rows
            // onto the blank member while the total still ties. So a total-only run is InsufficientCoverage,
            // NOT a pass, even though nothing mismatched.
            var r = MeasureReconciler.Reconcile(new[] { Val(Total, 350m, 350m) }, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InsufficientCoverage, r.Status);
            Assert.False(r.AnyMismatch);                       // unchanged: no cell mismatched...
            Assert.True(r.HasGrandTotal);
            Assert.Equal(0, r.MemberCellsChecked);             // ...but zero members were verified
            Assert.DoesNotContain("All clear", r.Summary);
            Assert.Contains("Insufficient coverage", r.Summary);
            Assert.Contains("blank", r.Summary);               // names the trap
        }

        [Fact]
        public void Grand_total_recognised_even_when_key_is_null()
        {
            // A null GroupingKey is the grand total just like an empty array.
            var cells = new[]
            {
                new ReconcileCell { GroupingKey = null, Dax = 10m, Sql = 10m },
                Val(new[] { "West" }, 200m, 250m),
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Contains("Grand total matches but", r.Summary);
        }

        [Fact]
        public void Multiple_grand_total_cells_are_an_input_error()
        {
            // Two empty-key cells make the trap narrative order-dependent (which "total" wins
            // FirstOrDefault?), so the run fails closed as InputError — but the cells are still CLASSIFIED
            // and tallied honestly rather than short-circuited away.
            var cells = new[] { Val(Total, 350m, 350m), Val(Total, 10m, 10m) };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Equal(2, r.Matches);                       // both classified, both tie
            Assert.Equal(0, r.InvalidInputs);                 // the fault is ambiguity, not a bad cell
            Assert.Contains("grand-total", r.Summary);
            Assert.Equal(cells.Length, r.Matches + r.Mismatches + r.Unverifiable + r.InvalidInputs); // conservation
        }

        [Fact]
        public void Duplicate_grand_totals_do_not_hide_a_member_mismatch()
        {
            // The round-2 catch: the old short-circuit reported ONLY the duplicate-total complaint, leaving
            // a member mismatch invisible (Mismatches=0). Now the members are classified anyway: the status
            // still fails closed as InputError, but the tallies and summary name the mismatch too.
            var cells = new[]
            {
                Val(Total, 350m, 350m),
                Val(Total, 10m, 10m),                 // the duplicate
                Val(new[] { "West" }, 200m, 250m),    // member: WRONG — must not be hidden
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Equal(1, r.Mismatches);
            Assert.True(r.AnyMismatch);
            Assert.Contains("grand-total", r.Summary);
            Assert.Contains("1 mismatched", r.Summary);
        }

        // ============================================================================================
        //  (4) MISSING CELLS — a key on one side only. Never silently dropped.
        // ============================================================================================

        [Fact]
        public void Null_policy_key_present_in_the_model_absent_from_the_source_is_a_mismatch()
        {
            // DAX has a value; SQL produced no row (Sql=null, SqlNull=false => absent, not a NULL).
            var r = One(MissingSql(K, 500m), BlankPolicy.BlankIsNull);
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(r));
            Assert.Contains("absent from the source", r.Cells[0].Explanation);
        }

        [Fact]
        public void Null_policy_key_present_in_the_source_absent_from_the_model_is_a_mismatch()
        {
            var r = One(MissingDax(K, 500m), BlankPolicy.BlankIsNull);
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(r));
            Assert.Contains("absent from the model", r.Cells[0].Explanation);
        }

        [Fact]
        public void Zero_policy_missing_vs_value_mismatches_in_both_directions()
        {
            // Under BlankIsZero a missing side reads as 0, so missing-vs-500 is 0-vs-500 => a real mismatch.
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(MissingDax(K, 500m), BlankPolicy.BlankIsZero)));  // source has it, model doesn't
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(MissingSql(K, 500m), BlankPolicy.BlankIsZero)));  // model has it, source doesn't
        }

        [Fact]
        public void Distinct_policy_missing_vs_value_mismatches_in_both_directions()
        {
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(MissingDax(K, 500m), BlankPolicy.BlankIsDistinct)));
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(MissingSql(K, 500m), BlankPolicy.BlankIsDistinct)));
        }

        [Fact]
        public void Zero_policy_missing_vs_exact_zero_matches()
        {
            // A dropped slice read as 0 facing an exact 0 is a legitimate MATCH for an additive measure.
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(MissingDax(K, 0m), BlankPolicy.BlankIsZero)));
        }

        [Fact]
        public void Zero_policy_missing_vs_value_within_the_absolute_floor_matches()
        {
            // 0 (missing) vs 5e-10 sql: sql==0? no, but |delta| 5e-10 <= 1e-9 floor => match.
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(MissingDax(K, 0.0000000005m), BlankPolicy.BlankIsZero)));
        }

        [Fact]
        public void Zero_policy_missing_vs_value_outside_the_absolute_floor_mismatches()
        {
            // 0 (missing) vs 0.001: window = max(1e-9, 1e-7*0.001=1e-10) = 1e-9; 1e-3 > 1e-9 => mismatch.
            Assert.Equal(ReconcileVerdict.Mismatch, VerdictOf(One(MissingDax(K, 0.001m), BlankPolicy.BlankIsZero)));
        }

        [Fact]
        public void A_missing_row_facing_an_empty_is_not_a_false_mismatch()
        {
            // A dropped DAX row is indistinguishable from BLANK for a measure: absent-vs-NULL under
            // BlankIsZero is 0-vs-0, a legitimate MATCH — not a spurious failure.
            var c = new ReconcileCell { GroupingKey = K, Dax = null, DaxBlank = false, Sql = null, SqlNull = true };
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(One(c, BlankPolicy.BlankIsZero)));
        }

        // ============================================================================================
        //  (5) WORST OFFENDER — the largest relative delta
        // ============================================================================================

        [Fact]
        public void Worst_offender_is_the_largest_relative_delta_not_the_largest_absolute()
        {
            // A: abs 10 but rel 0.10.  B: abs 100 and rel 1.00.  B is proportionally worse => the offender.
            var cells = new[]
            {
                Val(new[] { "A" }, 110m, 100m),   // rel 0.10
                Val(new[] { "B" }, 200m, 100m),   // rel 1.00  <-- worst
                Val(new[] { "C" }, 100m, 100m),   // match
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(new[] { "B" }, r.WorstOffender.GroupingKey);
        }

        [Fact]
        public void A_structural_mismatch_outranks_a_finite_percentage_offender()
        {
            // A missing-row mismatch has no computable relative delta; it is the loudest kind of divergence
            // (a whole slice disagreed to exist) so it must win worst-offender over a mere 50% numeric miss.
            var cells = new[]
            {
                Val(new[] { "num" }, 150m, 100m),   // rel 0.50
                MissingSql(new[] { "gone" }, 5m),   // absent from source
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsNull));
            Assert.Equal(new[] { "gone" }, r.WorstOffender.GroupingKey);
        }

        [Fact]
        public void No_mismatches_means_no_worst_offender()
        {
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, Pol(BlankPolicy.BlankIsZero));
            Assert.Null(r.WorstOffender);
        }

        // ============================================================================================
        //  (6) RUN-LEVEL STATUS + COVERAGE — !AnyMismatch is NOT a pass
        // ============================================================================================

        [Fact]
        public void All_matching_member_cells_are_reconciled_with_coverage_facts()
        {
            var cells = new[] { Val(Total, 10m, 10m), Val(new[] { "x" }, 3m, 3m), Val(new[] { "y" }, 7m, 7m) };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.Reconciled, r.Status);
            Assert.False(r.AnyMismatch);
            Assert.Equal(3, r.Matches);
            Assert.True(r.HasGrandTotal);
            Assert.Equal(2, r.MemberCellsChecked);           // grand total is not a member
            Assert.Contains("All clear", r.Summary);
        }

        [Fact]
        public void An_all_unverifiable_run_is_nothing_verifiable_and_never_says_all_clear()
        {
            // One BLANK-vs-NULL under BlankIsDistinct: zero mismatches, but nothing could be judged. The old
            // code printed "All clear: 0/1..." — a lie. It is NothingVerifiable.
            var r = One(DaxBlankSql(K, null, sqlNull: true), BlankPolicy.BlankIsDistinct);
            Assert.Equal(ReconcileStatus.NothingVerifiable, r.Status);
            Assert.False(r.AnyMismatch);
            Assert.Equal(1, r.Unverifiable);
            Assert.DoesNotContain("All clear", r.Summary);
            Assert.Contains("Nothing verifiable", r.Summary);
        }

        [Fact]
        public void A_mixed_match_and_unverifiable_run_is_reconciled_but_never_says_all_clear()
        {
            // A verifiable member match plus an unverifiable blank: green (a real match, no mismatch) but the
            // summary must NOT claim "All clear" while a cell went unchecked.
            var cells = new[]
            {
                Val(new[] { "East" }, 5m, 5m),                         // verifiable match
                DaxBlankSql(new[] { "West" }, null, sqlNull: true),    // unverifiable (distinct)
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsDistinct));
            Assert.Equal(ReconcileStatus.Reconciled, r.Status);
            Assert.Equal(1, r.MemberCellsChecked);
            Assert.Equal(1, r.Unverifiable);
            Assert.DoesNotContain("All clear", r.Summary);
            Assert.Contains("needs review", r.Summary);
        }

        // ============================================================================================
        //  (7) INPUT VALIDATION — malformed input is surfaced, never silently swallowed
        // ============================================================================================

        [Fact]
        public void Null_input_cells_are_counted_not_silently_dropped()
        {
            // A null entry among valid cells is malformed input. The old code skipped it, understating
            // coverage; now it is counted and the run is an InputError, and conservation holds against the
            // INPUT cardinality (2), not the survivor list (1).
            var cells = new[] { Val(K, 5m, 5m), null };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Equal(1, r.Matches);
            Assert.Equal(1, r.InvalidInputs);
            Assert.Single(r.Cells);                            // only the valid cell got a verdict
            Assert.Equal(cells.Length, r.Matches + r.Mismatches + r.Unverifiable + r.InvalidInputs);
            Assert.Contains("null", r.Summary);
        }

        [Fact]
        public void A_null_tolerance_policy_is_an_input_error_not_a_silent_default()
        {
            // The old code silently applied BlankIsZero + the DaxBench tolerances. That is a policy GUESS that
            // can equate BLANK/NULL/0 without the caller choosing it. A null policy is now an InputError; a
            // caller wanting the defaults passes TolerancePolicy.Default explicitly.
            var r = MeasureReconciler.Reconcile(new[] { DaxBlankSql(K, 0m) }, null);
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Contains("policy", r.Summary);
        }

        [Fact]
        public void The_explicit_default_policy_still_works_when_passed()
        {
            // TolerancePolicy.Default remains available — it just must be passed deliberately.
            var r = MeasureReconciler.Reconcile(new[] { DaxBlankSql(K, 0m) }, TolerancePolicy.Default);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));   // blank vs 0 matches under BlankIsZero
        }

        [Theory]
        [InlineData(double.NaN, 1e-7)]
        [InlineData(1e-9, double.NaN)]
        [InlineData(double.PositiveInfinity, 1e-7)]
        [InlineData(1e-9, double.PositiveInfinity)]
        [InlineData(-1.0, 1e-7)]
        [InlineData(1e-9, -0.5)]
        public void A_non_finite_or_negative_tolerance_is_an_input_error_not_a_throw(double absolute, double relative)
        {
            // NaN/Infinity/negative can never define a sane window (an infinite threshold greens everything).
            // We refuse with a teaching summary — critically, WITHOUT throwing mid-loop.
            var policy = new TolerancePolicy { Absolute = absolute, Relative = relative, Blank = BlankPolicy.BlankIsZero };
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, policy);
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Contains("tolerance", r.Summary);
        }

        [Fact]
        public void A_suspiciously_loose_relative_tolerance_is_flagged_but_still_runs()
        {
            // rel 0.5 (50%) is above the 1% ceiling. 150 vs 100 is a "match" at that width — but the run is
            // flagged SuspiciouslyLoose and the summary warns, so the UI never presents it as tight.
            var policy = new TolerancePolicy { Absolute = 0.0, Relative = 0.5, Blank = BlankPolicy.BlankIsZero };
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 150m, 100m) }, policy);
            Assert.True(r.SuspiciouslyLoose);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));
            Assert.Contains("loose", r.Summary);
        }

        [Fact]
        public void A_tight_tolerance_is_not_flagged_loose()
        {
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, Pol(BlankPolicy.BlankIsZero));
            Assert.False(r.SuspiciouslyLoose);
        }

        [Fact]
        public void The_loose_relative_boundary_is_strictly_greater_than_one_percent()
        {
            // Pins the ceiling comparison as strict >: exactly 1% is the documented limit and NOT flagged;
            // a hair beyond it is.
            var atCeiling = new TolerancePolicy { Absolute = 0.0, Relative = 0.01, Blank = BlankPolicy.BlankIsZero };
            Assert.False(MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, atCeiling).SuspiciouslyLoose);

            var justOver = new TolerancePolicy { Absolute = 0.0, Relative = 0.0100001, Blank = BlankPolicy.BlankIsZero };
            Assert.True(MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, justOver).SuspiciouslyLoose);
        }

        [Fact]
        public void A_loose_absolute_tolerance_relative_to_the_runs_data_is_flagged()
        {
            // The round-2 catch: Relative=0 with a huge Absolute silently greened 100-vs-0. Absolute has no
            // universal ceiling (it is scale-dependent), so it is judged against THIS run's data: the window
            // admits >1% error on the largest observed magnitude (100) => flagged, even though the relative
            // knob is squeaky-tight.
            var policy = new TolerancePolicy { Absolute = double.MaxValue, Relative = 0.0, Blank = BlankPolicy.BlankIsZero };
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 100m, 0m) }, policy);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));   // the window really does green it...
            Assert.True(r.SuspiciouslyLoose);                     // ...which is exactly why we must warn
            Assert.Contains("loose", r.Summary);
        }

        [Fact]
        public void A_proportionate_absolute_tolerance_is_not_flagged_loose()
        {
            // Absolute 0.5 against values around 100: the window admits 0.5% error on the largest observed
            // magnitude — under the 1% line, so no flag.
            var policy = new TolerancePolicy { Absolute = 0.5, Relative = 0.0, Blank = BlankPolicy.BlankIsZero };
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 100.2m, 100m) }, policy);
            Assert.Equal(ReconcileVerdict.Match, VerdictOf(r));
            Assert.False(r.SuspiciouslyLoose);
        }

        [Fact]
        public void An_all_zero_run_never_flags_the_absolute_rule()
        {
            // With every observed value 0 there is no scale to judge the absolute window against — flagging
            // would be noise, so the rule stands down (mirrors the dormant-or-dock convention).
            var policy = new TolerancePolicy { Absolute = 1000.0, Relative = 0.0, Blank = BlankPolicy.BlankIsZero };
            var r = MeasureReconciler.Reconcile(new[] { Val(K, 0m, 0m) }, policy);
            Assert.False(r.SuspiciouslyLoose);
        }

        [Fact]
        public void An_undefined_blank_policy_throws_rather_than_guessing_semantics()
        {
            // A bad cast / corrupt deserialize has no defensible BLANK/NULL semantics. Fail loud (caller bug),
            // never silently treat it as one of the real policies.
            var policy = new TolerancePolicy { Absolute = 1e-9, Relative = 1e-7, Blank = (BlankPolicy)999 };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MeasureReconciler.Reconcile(new[] { Val(K, 5m, 5m) }, policy));
        }

        [Fact]
        public void An_undefined_blank_policy_throws_even_for_empty_input()
        {
            // Round-2 catch: the throw used to live inside per-cell classification, so an empty (or all-null)
            // input sailed past it and returned NothingVerifiable. The enum is now validated up front, before
            // any short-circuit path.
            var policy = new TolerancePolicy { Absolute = 1e-9, Relative = 1e-7, Blank = (BlankPolicy)999 };
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MeasureReconciler.Reconcile(Array.Empty<ReconcileCell>(), policy));
        }

        // ============================================================================================
        //  (8) NUMERIC ROBUSTNESS — an unrepresentable difference is unverifiable, not a crash
        // ============================================================================================

        [Fact]
        public void A_decimal_subtraction_overflow_is_unsupported_numeric_not_a_crash()
        {
            // MaxValue - MinValue (= 2 * MaxValue) overflows decimal's ~7.9e28 range. We refuse to clamp,
            // round, drop, or crash: the cell is UnsupportedNumeric, counts as unverifiable, and the run is
            // NothingVerifiable (nothing could be judged) rather than throwing.
            var r = MeasureReconciler.Reconcile(new[] { Val(K, decimal.MaxValue, decimal.MinValue) }, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileVerdict.UnsupportedNumeric, VerdictOf(r));
            Assert.Null(r.Cells[0].Delta);
            Assert.Equal(1, r.Unverifiable);
            Assert.Equal(ReconcileStatus.NothingVerifiable, r.Status);
            Assert.False(r.AnyMismatch);
        }

        [Fact]
        public void A_preclassified_unsupported_dax_side_is_unsupported_numeric()
        {
            // The runner met a value it could not represent (NaN/Infinity/out-of-range) and hands the FACT
            // over via the per-side flag instead of clamping, dropping, or aborting the whole run. The cell
            // is UnsupportedNumeric with the side named — counted toward unverifiable, never a pass or fail.
            var c = new ReconcileCell { GroupingKey = K, DaxUnsupported = true, Sql = 500m };
            var r = One(c, BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileVerdict.UnsupportedNumeric, VerdictOf(r));
            Assert.Contains("DAX side", r.Cells[0].Explanation);
            Assert.Equal(1, r.Unverifiable);
            Assert.False(r.AnyMismatch);
        }

        [Fact]
        public void Both_sides_preclassified_unsupported_is_unsupported_numeric()
        {
            var c = new ReconcileCell { GroupingKey = K, DaxUnsupported = true, SqlUnsupported = true };
            var r = One(c, BlankPolicy.BlankIsDistinct);
            Assert.Equal(ReconcileVerdict.UnsupportedNumeric, VerdictOf(r));
            Assert.Contains("both sides", r.Cells[0].Explanation);
            Assert.Equal(1, r.Unverifiable);
            Assert.Equal(ReconcileStatus.NothingVerifiable, r.Status);
        }

        [Fact]
        public void An_unsupported_flag_alongside_a_value_is_a_contradictory_input_error()
        {
            // Provenance must be mutually exclusive per side (the contract's rule): a flag saying "we could
            // not represent this" NEXT TO a represented value means the runner's coercion contradicts itself.
            // We refuse to guess which signal to believe — the cell is invalid input and the run fails closed.
            var contradictory = new ReconcileCell { GroupingKey = K, Dax = 123m, DaxUnsupported = true, Sql = 500m };
            var cells = new[] { Val(new[] { "ok" }, 5m, 5m), contradictory };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Equal(1, r.InvalidInputs);
            Assert.Contains("contradictory", r.Summary);
            Assert.Equal(cells.Length, r.Matches + r.Mismatches + r.Unverifiable + r.InvalidInputs); // conservation
        }

        [Fact]
        public void An_unsupported_flag_alongside_a_blank_flag_is_contradictory_and_named_as_such()
        {
            // Round-3 catch: this combination was DETECTED correctly but described as "alongside a value".
            // The fault wording must name the actual conflict — the summary now says "a value or a
            // blank/null flag", covering both contradictory shapes.
            var contradictory = new ReconcileCell { GroupingKey = K, DaxUnsupported = true, DaxBlank = true, Sql = 500m };
            var r = One(contradictory, BlankPolicy.BlankIsZero);
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.Equal(1, r.InvalidInputs);
            Assert.Contains("contradictory", r.Summary);
            Assert.Contains("blank/null flag", r.Summary);   // the message names this conflict shape too
        }

        [Fact]
        public void A_contradictory_grand_total_still_counts_as_supplied_and_fails_closed()
        {
            // Round-3 catch: HasGrandTotal used to derive from post-filter verdicts, so a run whose ONLY
            // total was contradictory claimed "no total supplied" while the fault accounting had seen one.
            // HasGrandTotal is provenance (a total WAS supplied) and now reads from the input; there is no
            // fail-open risk because the run is InputError and MemberCellsChecked is still zero — a run with
            // only an unjudgeable total can never read as covered.
            var badTotal = new ReconcileCell { GroupingKey = Total, Dax = 350m, DaxUnsupported = true, Sql = 350m };
            var r = MeasureReconciler.Reconcile(new[] { badTotal }, Pol(BlankPolicy.BlankIsZero));
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.True(r.HasGrandTotal);                    // supplied, even though it could not be judged
            Assert.Equal(0, r.MemberCellsChecked);           // and nothing whatsoever was covered
            Assert.Equal(1, r.InvalidInputs);
            Assert.Empty(r.Cells);                           // no verdict was fabricated for it
        }

        // ============================================================================================
        //  EDGES
        // ============================================================================================

        [Fact]
        public void Empty_input_is_a_clean_no_op()
        {
            var r = MeasureReconciler.Reconcile(Array.Empty<ReconcileCell>(), Pol(BlankPolicy.BlankIsZero));
            Assert.Empty(r.Cells);
            Assert.Equal(ReconcileStatus.NothingVerifiable, r.Status);
            Assert.Equal(0, r.Matches);
            Assert.Equal(0, r.Mismatches);
            Assert.Equal(0, r.InvalidInputs);
            Assert.False(r.AnyMismatch);
            Assert.Null(r.WorstOffender);
            Assert.Equal("No cells to reconcile.", r.Summary);
        }

        [Fact]
        public void Counts_reconcile_to_the_input_total()
        {
            // matches + mismatches + unverifiable + invalidInputs == input cells — the invariant the UI relies
            // on, now conserved against the INPUT cardinality.
            var cells = new[]
            {
                Val(new[] { "ok" }, 5m, 5m),                        // match
                Val(new[] { "bad" }, 9m, 5m),                       // mismatch
                DaxBlankSql(new[] { "huh" }, null, sqlNull: true),  // unverifiable (distinct)
            };
            var r = MeasureReconciler.Reconcile(cells, Pol(BlankPolicy.BlankIsDistinct));
            Assert.Equal(1, r.Matches);
            Assert.Equal(1, r.Mismatches);
            Assert.Equal(1, r.Unverifiable);
            Assert.Equal(0, r.InvalidInputs);
            Assert.Equal(cells.Length, r.Matches + r.Mismatches + r.Unverifiable + r.InvalidInputs);
        }
    }
}
