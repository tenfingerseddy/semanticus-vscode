using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The reconciler RUNNER's pure core — the join, key discipline, coercion, and duplicate detection that feed the
    /// MeasureReconciler judge (docs/tests-tab-runner-contract.md sections a, b, c, e). These pin the parts that decide
    /// whether the judge is handed HONEST cells, offline (no connection): a green verdict is only as trustworthy as the
    /// join and coercion beneath it. The live query paths (LocalEngine.Testing.cs) are covered by the smoke/live gate.
    /// </summary>
    public sealed class ReconcileRunnerTests
    {
        // ---- helpers --------------------------------------------------------------------------------
        private static ReconcileSourceRow Row(object keyPart, CoercedValue value) =>
            new ReconcileSourceRow
            {
                DisplayKey = new[] { ReconcileKeyEncoder.Display(keyPart) },
                MatchKey = ReconcileKeyEncoder.ComposeKey(new[] { keyPart }),
                Value = value,
            };

        private static CoercedValue Num(decimal d) => CoercedValue.OfValue(d);

        // ============================================================================================
        //  (b) NUMERIC COERCION — finite decimal | present-empty | not-representable, never thrown
        // ============================================================================================

        [Fact]
        public void Coerce_null_is_present_empty()
        {
            var c = ReconcileCoercion.Coerce(null);
            Assert.True(c.Empty);
            Assert.False(c.Unsupported);
            Assert.Null(c.Value);
        }

        [Fact]
        public void Coerce_dbnull_is_present_empty()
        {
            var c = ReconcileCoercion.Coerce(DBNull.Value);
            Assert.True(c.Empty);
        }

        [Theory]
        [InlineData(1000L)]
        [InlineData(0)]
        [InlineData((short)7)]
        public void Coerce_integral_types_become_decimal(object raw)
        {
            var c = ReconcileCoercion.Coerce(raw);
            Assert.True(c.Value.HasValue);
            Assert.False(c.Empty);
            Assert.False(c.Unsupported);
        }

        [Fact]
        public void Coerce_double_value_becomes_decimal()
        {
            var c = ReconcileCoercion.Coerce(123.5d);
            Assert.Equal(123.5m, c.Value);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(1e30)]     // beyond decimal's ~7.9e28 range
        [InlineData(-1e30)]
        public void Coerce_nonfinite_or_out_of_range_double_is_unsupported(double raw)
        {
            var c = ReconcileCoercion.Coerce(raw);
            Assert.True(c.Unsupported);
            Assert.False(c.Value.HasValue);
            Assert.False(c.Empty);
        }

        [Fact]
        public void Coerce_ulong_max_fits_decimal()
        {
            var c = ReconcileCoercion.Coerce(ulong.MaxValue);
            Assert.True(c.Value.HasValue);
            Assert.Equal((decimal)ulong.MaxValue, c.Value.Value);
        }

        [Theory]
        [InlineData("123")]     // a numeric-looking STRING is text, not a number — not silently parsed
        [InlineData(true)]
        public void Coerce_nonnumeric_types_are_unsupported(object raw)
        {
            var c = ReconcileCoercion.Coerce(raw);
            Assert.True(c.Unsupported);
        }

        [Fact]
        public void Coerce_unsupported_sentinel_is_unsupported()
        {
            // The SQL runner substitutes this for a decimal(38,...) it could not read — one cell, not the whole run.
            var c = ReconcileCoercion.Coerce(ReconcileValues.UnsupportedCell);
            Assert.True(c.Unsupported);
        }

        // ============================================================================================
        //  (c) KEY DISCIPLINE — null/blank/empty, numeric-vs-text, timezone, integer-width, delimiter
        // ============================================================================================

        [Fact]
        public void Key_null_and_empty_string_and_text_are_all_distinct()
        {
            var k1 = ReconcileKeyEncoder.ComposeKey(new object[] { null });
            var k2 = ReconcileKeyEncoder.ComposeKey(new object[] { "" });
            var k3 = ReconcileKeyEncoder.ComposeKey(new object[] { "x" });
            Assert.NotEqual(k1, k2);
            Assert.NotEqual(k2, k3);
            Assert.NotEqual(k1, k3);
            Assert.NotEqual("", k1);   // a null member is never the empty (grand-total) key
        }

        [Fact]
        public void Key_numeric_and_text_lookalikes_do_not_collide()
        {
            var num = ReconcileKeyEncoder.ComposeKey(new object[] { 2020 });
            var txt = ReconcileKeyEncoder.ComposeKey(new object[] { "2020" });
            Assert.NotEqual(num, txt);
        }

        [Fact]
        public void Key_integer_width_is_normalised_across_providers()
        {
            // DAX hands back Int64, SQL hands back int — the SAME logical member must line up.
            var fromDax = ReconcileKeyEncoder.ComposeKey(new object[] { 2020L });
            var fromSql = ReconcileKeyEncoder.ComposeKey(new object[] { 2020 });
            var asDecimal = ReconcileKeyEncoder.ComposeKey(new object[] { 2020.0m });
            Assert.Equal(fromDax, fromSql);
            Assert.Equal(fromDax, asDecimal);
        }

        [Fact]
        public void Key_integral_double_folds_onto_the_integer_token()
        {
            // A floating 2020.0 that is exactly integral must still line up with a 2020 integer member.
            Assert.Equal(ReconcileKeyEncoder.ComposeKey(new object[] { 2020L }),
                         ReconcileKeyEncoder.ComposeKey(new object[] { 2020.0d }));
        }

        [Fact]
        public void Key_distinct_doubles_do_not_collide_after_lossless_encoding()
        {
            // P1-4: (decimal)double is LOSSY — double.Epsilon narrows to 0 and would collide with a real 0. The
            // lossless "#F:" round-trip must keep them distinct.
            var epsilon = ReconcileKeyEncoder.ComposeKey(new object[] { double.Epsilon });
            var zero = ReconcileKeyEncoder.ComposeKey(new object[] { 0d });
            Assert.NotEqual(epsilon, zero);
        }

        [Fact]
        public void Key_two_doubles_that_both_narrow_to_the_same_decimal_stay_distinct()
        {
            // 0.1 + 0.2 == 0.30000000000000004, distinct from 0.3 — but (decimal) of both is 0.3m. G17 keeps them apart.
            var a = ReconcileKeyEncoder.ComposeKey(new object[] { 0.1d + 0.2d });
            var b = ReconcileKeyEncoder.ComposeKey(new object[] { 0.3d });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Key_integral_double_above_2pow53_does_not_fold_onto_a_bigint()
        {
            // DECIDED + documented: above 2^53 a double cannot represent every integer, so an integral double there is
            // NOT folded onto the "#:" integer token — it will not falsely match a bigint of the "same" value. It
            // reports as distinct (fail-loud: a possible mismatch surfaces, never a false reconcile).
            const long big = 9007199254740993L;      // 2^53 + 1 — not exactly representable as a double
            var asBigint = ReconcileKeyEncoder.ComposeKey(new object[] { big });
            var asDouble = ReconcileKeyEncoder.ComposeKey(new object[] { (double)big });
            Assert.NotEqual(asBigint, asDouble);
        }

        [Fact]
        public void Key_datetime_offsets_are_distinct_from_bare_datetime()
        {
            var bare = ReconcileKeyEncoder.ComposeKey(new object[] { new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) });
            var offset = ReconcileKeyEncoder.ComposeKey(new object[] { new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.FromHours(10)) });
            Assert.NotEqual(bare, offset);
        }

        [Fact]
        public void Key_is_delimiter_safe_across_arities()
        {
            // A value containing the separator must not forge a boundary, and differing arities must not collide.
            var a = ReconcileKeyEncoder.ComposeKey(new object[] { "a\u001Fb" });
            var b = ReconcileKeyEncoder.ComposeKey(new object[] { "a", "b" });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Key_composite_order_matters()
        {
            var ab = ReconcileKeyEncoder.ComposeKey(new object[] { "a", "b" });
            var ba = ReconcileKeyEncoder.ComposeKey(new object[] { "b", "a" });
            Assert.NotEqual(ab, ba);
        }

        [Fact]
        public void Display_key_is_never_empty_for_a_member()
        {
            Assert.Equal("(null)", ReconcileKeyEncoder.Display(null));
            Assert.Equal("(empty)", ReconcileKeyEncoder.Display(""));
            Assert.Equal("East", ReconcileKeyEncoder.Display("East"));
        }

        // ============================================================================================
        //  (a) FULL OUTER JOIN — one cell per union key, one-sided keys preserved, never inner
        // ============================================================================================

        [Fact]
        public void Join_matches_keys_and_pairs_values()
        {
            var dax = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var sql = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            Assert.Null(o.DuplicateError);
            Assert.Equal(2, o.Cells.Count);
            Assert.All(o.Cells, c => Assert.True(c.Dax.HasValue && c.Sql.HasValue));
            Assert.Equal(0, o.MissingInDax);
            Assert.Equal(0, o.MissingInSql);
        }

        [Fact]
        public void Join_preserves_a_key_present_only_in_sql_as_missing_dax()
        {
            var dax = new[] { Row("East", Num(100m)) };
            var sql = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            Assert.Equal(2, o.Cells.Count);
            var west = o.Cells.Single(c => c.GroupingKey.SequenceEqual(new[] { "West" }));
            // MISSING on the DAX side: value null, blank flag false (distinct from a present blank).
            Assert.Null(west.Dax);
            Assert.False(west.DaxBlank);
            Assert.True(west.Sql.HasValue);
            Assert.Equal(1, o.MissingInDax);
        }

        [Fact]
        public void Join_preserves_a_key_present_only_in_dax_as_missing_sql()
        {
            var dax = new[] { Row("East", Num(100m)), Row("North", Num(5m)) };
            var sql = new[] { Row("East", Num(100m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            var north = o.Cells.Single(c => c.GroupingKey.SequenceEqual(new[] { "North" }));
            Assert.Null(north.Sql);
            Assert.False(north.SqlNull);
            Assert.True(north.Dax.HasValue);
            Assert.Equal(1, o.MissingInSql);
        }

        [Fact]
        public void Join_carries_present_empty_and_unsupported_flags_through()
        {
            var dax = new[] { Row("East", CoercedValue.PresentEmpty), Row("West", CoercedValue.NotRepresentable) };
            var sql = new[] { Row("East", Num(0m)), Row("West", Num(1m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            var east = o.Cells.Single(c => c.GroupingKey.SequenceEqual(new[] { "East" }));
            Assert.True(east.DaxBlank);
            Assert.Null(east.Dax);
            var west = o.Cells.Single(c => c.GroupingKey.SequenceEqual(new[] { "West" }));
            Assert.True(west.DaxUnsupported);
            Assert.Null(west.Dax);
            Assert.False(west.DaxBlank);
        }

        [Fact]
        public void Join_refuses_duplicate_key_on_dax_side_naming_it()
        {
            var dax = new[] { Row("East", Num(100m)), Row("East", Num(101m)) };
            var sql = new[] { Row("East", Num(100m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            Assert.NotNull(o.DuplicateError);
            Assert.Contains("DAX", o.DuplicateError);
            Assert.Contains("East", o.DuplicateError);
            Assert.Empty(o.Cells);   // never a Cartesian
        }

        [Fact]
        public void Join_refuses_duplicate_key_on_sql_side()
        {
            var dax = new[] { Row("East", Num(100m)) };
            var sql = new[] { Row("East", Num(100m)), Row("East", Num(100m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            Assert.NotNull(o.DuplicateError);
            Assert.Contains("SQL", o.DuplicateError);
            Assert.Empty(o.Cells);
        }

        // ============================================================================================
        //  END-TO-END through the judge — the join + coercion actually produce a correct verdict
        // ============================================================================================

        [Fact]
        public void Joined_cells_reconcile_green_through_the_judge()
        {
            var dax = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var sql = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            var r = MeasureReconciler.Reconcile(o.Cells, TolerancePolicy.Default);
            Assert.Equal(ReconcileStatus.Reconciled, r.Status);
            Assert.Equal(2, r.MemberCellsChecked);
        }

        [Fact]
        public void Blank_row_trap_surfaces_a_member_mismatch_from_joined_cells()
        {
            // The classic corruption: the grand total ties, but a member is wrong. The join + judge must catch it.
            var total = new ReconcileCell { GroupingKey = Array.Empty<string>(), Dax = 300m, Sql = 300m };
            var dax = new[] { Row("East", Num(250m)), Row("West", Num(50m)) };
            var sql = new[] { Row("East", Num(100m)), Row("West", Num(200m)) };
            var o = ReconcileJoiner.FullOuterJoin(dax, sql);
            var cells = new List<ReconcileCell> { total };
            cells.AddRange(o.Cells);
            var r = MeasureReconciler.Reconcile(cells, TolerancePolicy.Default);
            Assert.Equal(ReconcileStatus.Mismatch, r.Status);
            Assert.True(r.Mismatches >= 1);
        }
    }
}
