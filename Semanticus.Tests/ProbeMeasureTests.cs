using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verified Edits — <c>probe_measure</c>. The deterministic query/filter BUILDERS and the refuse/degrade
    /// paths are asserted OFFLINE (CI-safe); the live scenario execution (blank/coverage/additivity across contexts,
    /// e.g. catching an ALLSELECTED wrong-% denominator) is exercised by the smoke against a real XMLA endpoint.</summary>
    public sealed class ProbeMeasureTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }
        private static async Task<LocalEngine> OpenAsync()
        {
            var e = new LocalEngine(new SessionManager(), new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        // ---- deterministic builders (pure, no live) ----
        [Fact]
        public void BuildProbeQuery_has_sentinel_axis_filters_and_order_by()
        {
            var q = DaxBench.BuildProbeQuery("SUM('Sales'[Amount])", new[] { "'Date'[Month]" }, new[] { "TREATAS ( { 2023 }, 'Date'[Year] )" });
            Assert.Contains("SUMMARIZECOLUMNS", q);
            Assert.Contains("'Date'[Month]", q);                              // axis (inner context)
            Assert.Contains("TREATAS ( { 2023 }, 'Date'[Year] )", q);        // filter arg (outer context)
            Assert.Contains("\"v\", (\nSUM('Sales'[Amount])\n    )", q);   // comment-proof paren wrap (InlineScalar)
            Assert.Contains("\"__present\", 1", q);                          // sentinel retains all-blank rows
            Assert.Contains("ORDER BY 'Date'[Month]", q);
        }

        [Fact]
        public void BuildProbeQuery_grand_total_has_sentinel_but_no_order_by()
        {
            var q = DaxBench.BuildProbeQuery("[X]", Array.Empty<string>(), Array.Empty<string>());
            Assert.Contains("\"__present\", 1", q);
            Assert.DoesNotContain("ORDER BY", q);
        }

        [Fact]
        public void CompileMemberFilter_quotes_strings_leaves_numbers_bare()
        {
            Assert.Equal("TREATAS ( { \"Bikes\", \"Accessories\" }, 'Product'[Category] )",
                DaxBench.CompileMemberFilter("'Product'[Category]", new[] { "Bikes", "Accessories" }));
            Assert.Equal("TREATAS ( { 2022, 2023 }, 'Date'[Year] )",
                DaxBench.CompileMemberFilter("'Date'[Year]", new[] { "2022", "2023" }));
        }

        [Fact]
        public void CompileEmptyFilter_is_filter_all_false()
            => Assert.Equal("FILTER ( ALL ( 'Date'[Year] ), FALSE () )", DaxBench.CompileEmptyFilter("'Date'[Year]"));

        [Fact]
        public void BuildScalarContextQuery_evaluates_the_measure_at_the_control_slice()
        {
            var q = DaxBench.BuildScalarContextQuery("[Revenue YoY %]", "'Date'[Year]=2025");
            Assert.Contains("\"v\", CALCULATE", q);
            Assert.Contains("[Revenue YoY %]", q);
            Assert.Contains("'Date'[Year]=2025", q);
        }

        [Fact]
        public void BuildScalarContextQuery_terminates_a_trailing_context_comment()
        {
            var q = DaxBench.BuildScalarContextQuery("[X]", "'Date'[Year]=2025 -- trusted slice");
            Assert.Contains("-- trusted slice\n    )", q);
        }

        [Theory]
        [InlineData("12.4", "", "12.4")]
        [InlineData("'Date'[Year]=2025 ~ 12.4", "'Date'[Year]=2025", "12.4")]
        [InlineData("measure:Sales/Revenue YoY % ~ 'Date'[Year]=2025 ~ 12.4", "'Date'[Year]=2025", "12.4")]
        [InlineData("(grand total) ~ 87.4", "", "87.4")]
        [InlineData("some ~ label", "", "some ~ label")]
        public void SplitControlValue_only_routes_the_explicit_numeric_control_shape(string raw, string context, string value)
        {
            var actual = DaxBench.SplitControlValue(raw);
            Assert.Equal(context, actual.Context);
            Assert.Equal(value, actual.Value);
        }

        // ---- engine offline behavior ----
        [Fact]
        public async Task Rank_measure_is_refused_as_unfaithful()
        {
            using var e = await OpenAsync();
            var r = await e.ProbeMeasureAsync("RANKX ( ALL ( 'Product' ), [Sales] )", "'Product'[Category]", null, true, 5000);
            Assert.Equal("unfaithful", r.Status);   // rank depends on the full report member set — refuse, don't fake
            Assert.NotNull(r.Fidelity);
        }

        [Fact]
        public async Task Offline_probe_returns_no_connection_not_a_crash()
        {
            using var e = await OpenAsync();
            var r = await e.ProbeMeasureAsync("SUM ( 'Sales'[Amount] )", "'Date'[Month]", null, true, 5000);
            Assert.Equal("no-connection", r.Status);   // CI has no live endpoint → structured refusal
            Assert.NotNull(r.Fidelity);
        }

        [Fact]
        public async Task Empty_expression_is_error()
        {
            using var e = await OpenAsync();
            var r = await e.ProbeMeasureAsync("   ", "'Date'[Month]", null, true, 5000);
            Assert.Equal("error", r.Status);
        }

        // ---- comment-proof inlining (caught LIVE 2026-07-03): a stored measure body ending in a line comment
        // must not comment out the joining comma and kill the whole SUMMARIZECOLUMNS query. InlineScalar wraps the
        // expression in parens with a newline before ')' — value-preserving, comment-terminating.
        [Fact]
        public void Probe_query_survives_a_trailing_line_comment()
        {
            var q = DaxBench.BuildProbeQuery(
                "COUNTROWS ( SUMMARIZE ( Sales, Product[Brand] ) ) -- comment",
                new[] { "'Date'[Year]" }, Array.Empty<string>());
            Assert.Contains("-- comment\n    )", q);              // the newline terminates the comment BEFORE the ')'
            Assert.Contains("\"__present\", 1", q);                // the sentinel arg survives (was being swallowed)
            foreach (var line in q.Split('\n'))
            {
                var c = line.IndexOf("--", StringComparison.Ordinal);
                if (c >= 0) Assert.DoesNotContain(",", line.Substring(c));   // no live comma ever sits behind a comment
            }
        }

        [Fact]
        public void Inline_scalar_wraps_with_a_comment_terminating_newline()
        {
            Assert.Equal("(\nSUM ( S[A] ) -- note\n    )", DaxBench.InlineScalar("  SUM ( S[A] ) -- note  "));
        }

        // LIVE-ONLY (smoke / SEMANTICUS_LIVE_TEST): probe an ALLSELECTED %-of-selected measure and assert the
        // multi-select scenario's denominator ≠ grand total — i.e. the probe CATCHES the wrong-% bug a naive
        // single-context test misses; plus blank/coverage/additivity vs hand-checked values.
    }
}
