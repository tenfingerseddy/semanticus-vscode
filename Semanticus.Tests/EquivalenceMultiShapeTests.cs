using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verified Edits — the MULTI-SHAPE equivalence gate (the ProBench "X12" fix). SUMMARIZECOLUMNS(cols, …)
    /// returns ONLY the fully-crossed LEAF rows — every dimension pinned in filter context — so a wrong-SCOPE
    /// rewrite can return the SAME value as the correct measure at every leaf yet DIVERGE at a subtotal or the
    /// grand total, which a leaves-only gate never tested. These tests pin the fix OFFLINE (no live endpoint) by
    /// (1) asserting the query builder emits the grand-total + per-column-subtotal + leaf shapes and dedupes the
    /// degenerate grid sizes, and (2) driving <see cref="DaxBench.VerifyEquivalenceCoreAsync"/> with a FAKE executor
    /// that returns leaves-equal-but-diverging-at-a-coarser-shape data — proving the gate now FAILS it, while an
    /// equal-everywhere rewrite still PASSES.
    /// </summary>
    public sealed class EquivalenceMultiShapeTests
    {
        private static readonly string[] Grid2 = { "Date[Year]", "Product[Category]" };

        // ---- (1) shape coverage: the builder tests grand total + each single-column subtotal + the leaves ----

        [Fact]
        public void Two_column_grid_builds_grand_total_each_subtotal_and_the_leaves()
        {
            var qs = DaxBench.BuildComparisonQueries("[A]", "[B]", Grid2, null);
            Assert.Equal(4, qs.Count);

            // grand total: neither grid column appears as a group-by
            Assert.Contains(qs, q => !q.Contains("Date[Year]") && !q.Contains("Product[Category]"));
            // each bare single-column subtotal
            Assert.Contains(qs, q => q.Contains("Date[Year]") && !q.Contains("Product[Category]"));
            Assert.Contains(qs, q => q.Contains("Product[Category]") && !q.Contains("Date[Year]"));
            // the fully-crossed leaves
            Assert.Contains(qs, q => q.Contains("Date[Year]") && q.Contains("Product[Category]"));

            // every shape still compares A vs B
            Assert.All(qs, q => { Assert.Contains("\"__A\"", q); Assert.Contains("\"__B\"", q); });
        }

        [Fact]
        public void Empty_grid_is_the_grand_total_only_identical_to_the_pre_fix_query()
        {
            var qs = DaxBench.BuildComparisonQueries("[A]", "[B]", Array.Empty<string>(), null);
            Assert.Single(qs);                         // no redundant re-runs
            Assert.DoesNotContain("Date[Year]", qs[0]);
        }

        [Fact]
        public void One_column_grid_is_that_column_plus_the_grand_total_no_duplicate()
        {
            var qs = DaxBench.BuildComparisonQueries("[A]", "[B]", new[] { "Date[Year]" }, null);
            Assert.Equal(2, qs.Count);                 // {grand total, Date[Year]} — leaf == the single subtotal, deduped
            Assert.Contains(qs, q => !q.Contains("Date[Year]"));   // grand total
            Assert.Contains(qs, q => q.Contains("Date[Year]"));    // the column
        }

        [Fact]
        public void Shapes_dedupe_so_a_one_column_leaf_is_not_run_twice()
        {
            var shapes = DaxBench.BuildComparisonShapes(new[] { "Date[Year]" });
            Assert.Equal(2, shapes.Count);
            Assert.Contains(shapes, s => s.Length == 0);
            Assert.Contains(shapes, s => s.Length == 1 && s[0] == "Date[Year]");
        }

        // ---- (2) behavior: a leaves-equal-but-wrong-at-a-coarser-shape rewrite now FAILS ----

        // A fake executor: routes each shape query to a canned ResultSet keyed by which grid columns it groups by.
        private static Func<string, int, Task<ResultSet>> Fake(Dictionary<string, ResultSet> byShapeKey)
            => (q, maxRows) =>
            {
                var cols = Grid2.Where(q.Contains).ToArray();          // the shape this query groups by
                var key = string.Join("|", cols);
                Assert.True(byShapeKey.ContainsKey(key), $"unexpected shape query for columns [{key}]");
                return Task.FromResult(byShapeKey[key]);
            };

        // Build a ResultSet for a shape: group-by column defs + __A/__B, then the rows (each = key values then a, b).
        private static ResultSet Res(string[] shapeCols, params object[][] rows)
        {
            var cols = shapeCols.Select(c => new ColumnDef { Name = c })
                .Concat(new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } }).ToArray();
            return new ResultSet { Columns = cols, Rows = rows, RowCount = rows.Length };
        }

        [Fact]
        public async Task Diverges_at_the_grand_total_only_now_FAILS_though_every_leaf_matched()
        {
            // Leaves + both subtotals AGREE; only the grand total diverges (the classic wrong-denominator /
            // over-broad-ALL bug that a leaves-only gate green-lit).
            var leaf = Res(Grid2, new object[] { 2023, "Bikes", 10.0, 10.0 }, new object[] { 2023, "Audio", 5.0, 5.0 });
            var byShape = new Dictionary<string, ResultSet>
            {
                [""] = Res(Array.Empty<string>(), new object[] { 100.0, 90.0 }),                 // grand total DIFFERS
                ["Date[Year]"] = Res(new[] { "Date[Year]" }, new object[] { 2023, 15.0, 15.0 }), // subtotal agrees
                ["Product[Category]"] = Res(new[] { "Product[Category]" }, new object[] { "Bikes", 10.0, 10.0 }, new object[] { "Audio", 5.0, 5.0 }),
                ["Date[Year]|Product[Category]"] = leaf,
            };

            // Sanity: the leaf shape ALONE is all-equal — the old leaves-only gate would have PASSED this rewrite.
            Assert.All(leaf.Rows, r => Assert.True(DaxBench.ValuesEqual(r[^2], r[^1])));

            var eq = await DaxBench.VerifyEquivalenceCoreAsync(Fake(byShape), "[Correct]", "[Wrong]", Grid2, null, 100000);

            Assert.Null(eq.Error);
            Assert.False(eq.AllMatch);                                   // the new gate CATCHES it
            Assert.Equal(1, eq.MismatchCount);
            Assert.Contains(eq.Mismatches, m => m.Context == "(grand total)" && m.ValueA == "100" && m.ValueB == "90");
        }

        [Fact]
        public async Task Diverges_at_a_single_column_subtotal_now_FAILS()
        {
            var leaf = Res(Grid2, new object[] { 2023, "Bikes", 10.0, 10.0 });
            var byShape = new Dictionary<string, ResultSet>
            {
                [""] = Res(Array.Empty<string>(), new object[] { 10.0, 10.0 }),                  // grand total agrees
                ["Date[Year]"] = Res(new[] { "Date[Year]" }, new object[] { 2023, 50.0, 40.0 }), // subtotal DIFFERS
                ["Product[Category]"] = Res(new[] { "Product[Category]" }, new object[] { "Bikes", 10.0, 10.0 }),
                ["Date[Year]|Product[Category]"] = leaf,
            };

            var eq = await DaxBench.VerifyEquivalenceCoreAsync(Fake(byShape), "[Correct]", "[Wrong]", Grid2, null, 100000);

            Assert.False(eq.AllMatch);
            Assert.Contains(eq.Mismatches, m => m.Context == "Date[Year]=2023" && m.ValueA == "50" && m.ValueB == "40");
        }

        [Fact]
        public async Task Equal_at_every_shape_still_PASSES()
        {
            var byShape = new Dictionary<string, ResultSet>
            {
                [""] = Res(Array.Empty<string>(), new object[] { 15.0, 15.0 }),
                ["Date[Year]"] = Res(new[] { "Date[Year]" }, new object[] { 2023, 15.0, 15.0 }),
                ["Product[Category]"] = Res(new[] { "Product[Category]" }, new object[] { "Bikes", 10.0, 10.0 }, new object[] { "Audio", 5.0, 5.0 }),
                ["Date[Year]|Product[Category]"] = Res(Grid2, new object[] { 2023, "Bikes", 10.0, 10.0 }, new object[] { 2023, "Audio", 5.0, 5.0 }),
            };

            var eq = await DaxBench.VerifyEquivalenceCoreAsync(Fake(byShape), "[Correct]", "[Correct]", Grid2, null, 100000);

            Assert.Null(eq.Error);
            Assert.True(eq.AllMatch);
            Assert.Equal(0, eq.MismatchCount);
            // RowsCompared is the GRID (leaf) coverage, NOT the sum across shapes — the leaf cross had 2 rows.
            Assert.Equal(2, eq.RowsCompared);
        }

        [Fact]
        public async Task Empty_grid_with_a_matching_grand_total_stays_zero_coverage_not_proven()
        {
            // An empty selection: the requested GRID (leaf cross) has NO members, but a constant/ALL-based measure
            // still yields a matching grand-total row. Before the multi-shape change the leaf-only query stayed at
            // 0 rows => callers correctly downgraded to UNVERIFIED. The grand-total row must NOT flip that: coverage
            // is driven by the grid (leaf) shape, so RowsCompared stays 0 even though AllMatch is (vacuously) true.
            var byShape = new Dictionary<string, ResultSet>
            {
                [""] = Res(Array.Empty<string>(), new object[] { 42.0, 42.0 }),   // grand total present + AGREES
                ["Date[Year]"] = Res(new[] { "Date[Year]" }),                     // no members
                ["Product[Category]"] = Res(new[] { "Product[Category]" }),       // no members
                ["Date[Year]|Product[Category]"] = Res(Grid2),                    // the requested grid: ZERO rows
            };

            var eq = await DaxBench.VerifyEquivalenceCoreAsync(Fake(byShape), "[Correct]", "[Correct]", Grid2, null, 100000);

            Assert.Null(eq.Error);
            Assert.True(eq.AllMatch);                 // nothing diverged...
            Assert.Equal(0, eq.RowsCompared);         // ...but the requested grid compared NOTHING => callers stay unverified
        }

        [Fact]
        public async Task Null_executor_returns_a_clean_error_not_an_NRE()
        {
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(null, "[A]", "[B]", Grid2, null, 100000);
            Assert.False(eq.AllMatch);
            Assert.Equal("No query executor supplied.", eq.Error);
        }

        [Fact]
        public async Task An_executor_error_on_any_shape_surfaces_and_stops_the_proof()
        {
            var byShape = new Dictionary<string, ResultSet>
            {
                [""] = new ResultSet { Error = "query failed" },
            };
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(Fake(byShape), "[A]", "[B]", Grid2, null, 100000);
            Assert.False(eq.AllMatch);
            Assert.Equal("query failed", eq.Error);
        }
    }
}
