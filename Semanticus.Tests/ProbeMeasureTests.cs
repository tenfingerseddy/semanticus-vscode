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
            // MEASURE-FAITHFUL: the fully-qualified raw body is DEFINE-d as a real measure (host derived from the
            // expression's own table ref, identifier collision-free) and the value column REFERENCES it — not an
            // inline extension column that would skip the deployed measure's implicit CALCULATE.
            var id = System.Text.RegularExpressions.Regex.Match(q, @"MEASURE 'Sales'\[(__smx_[0-9a-f]{8}_probe)\] = \(\nSUM\('Sales'\[Amount\]\)\n    \)");
            Assert.True(id.Success, "expected a DEFINE MEASURE on 'Sales' with a generated __smx id:\n" + q);
            Assert.Contains("\"v\", [" + id.Groups[1].Value + "]", q);        // referenced by the SAME generated id
            Assert.Contains("\"__present\", 1", q);                          // sentinel retains all-blank rows
            Assert.Contains("ORDER BY 'Date'[Month]", q);
        }

        [Fact]
        public void BuildProbeQuery_wraps_a_context_transition_expression_as_a_deployed_measure()
        {
            // A body whose INLINE vs MEASURE evaluation differ structurally: an average-of-a-measure needs the row
            // context turned into filter context (implicit CALCULATE) that only a real measure gets. With a spec that
            // classifies [Total Sales] as a real measure, the query must DEFINE the body and reference it — never
            // splice it inline (the H04 fidelity gap).
            var expr = "AVERAGEX ( VALUES ( 'Product'[Category] ), [Total Sales] )";
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "Total Sales" } };
            var q = DaxBench.BuildProbeQuery(expr, new[] { "'Date'[Year]" }, System.Array.Empty<string>(), spec, out var note);
            Assert.StartsWith("DEFINE", q);
            Assert.Matches(@"MEASURE 'Product'\[__smx_[0-9a-f]{8}_probe\] = \(", q);   // host from the expression's first table ref
            Assert.DoesNotContain("\"v\", (\n" + expr, q);                   // the old inline extension-column shape is gone
            Assert.Null(note);                                               // full fidelity — no caveat
        }

        // BLOCKER 1: unqualified column refs bind relative to the measure's HOME table — without target identity a
        // DEFINE host cannot be chosen safely, so the builder must fall back to inline AND disclose it.
        [Fact]
        public void BuildProbeQuery_unqualified_column_ref_without_target_falls_back_inline_with_note()
        {
            var spec = new DaxQuerySpec { HomeTable = "Sales", ModelMeasureNames = new[] { "Total Sales" } };
            var q = DaxBench.BuildProbeQuery("SUM ( [Amount] )", new[] { "'Date'[Year]" }, System.Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("DEFINE", q);                              // no guessed home table
            Assert.Contains("\"v\", (\nSUM ( [Amount] )\n    )", q);         // honest inline evaluation
            Assert.NotNull(note);
            Assert.Contains("[Amount]", note);                               // names the suspect ref
            Assert.Contains("INLINE", note);
        }

        // BLOCKER 1 (converse): the same expression WITH target identity uses the target's REAL home table —
        // unqualified refs then bind exactly as the deployed measure's do.
        [Fact]
        public void BuildProbeQuery_unqualified_column_ref_with_target_uses_the_real_home_table()
        {
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "Total Sales", ModelMeasureNames = new[] { "Total Sales" } };
            var q = DaxBench.BuildProbeQuery("SUM ( [Amount] )", new[] { "'Date'[Year]" }, System.Array.Empty<string>(), spec, out var note);
            Assert.Contains("MEASURE 'Sales'[Total Sales] = (\nSUM ( [Amount] )\n    )", q);
            Assert.Null(note);
        }

        // BLOCKER 2: when the target measure is known, the query-scoped DEFINE SHADOWS the deployed measure's real
        // name, so ISSELECTEDMEASURE/SELECTEDMEASURENAME (calc groups) observe the true identity.
        [Fact]
        public void BuildProbeQuery_known_target_shadows_the_deployed_measure_name()
        {
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "Revenue YoY %", ModelMeasureNames = new[] { "Revenue YoY %" }, ModelHasCalcGroups = true };
            var q = DaxBench.BuildProbeQuery("DIVIDE ( SUM ( 'Sales'[Amt] ), [Base] ) -- rewrite", new[] { "'Date'[Year]" }, System.Array.Empty<string>(), spec, out var note);
            Assert.Contains("MEASURE 'Sales'[Revenue YoY %] = (", q);        // the REAL name, not a generated id
            Assert.Contains("\"v\", [Revenue YoY %]", q);
            Assert.Null(note);                                               // identity preserved — no calc-group caveat
        }

        // BLOCKER 2 (no identity) : generated names + calc groups in the model → the caveat must surface.
        [Fact]
        public void BuildProbeQuery_calc_groups_without_identity_raise_the_fidelity_note()
        {
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "Base" }, ModelHasCalcGroups = true };
            var q = DaxBench.BuildProbeQuery("DIVIDE ( SUM ( 'Sales'[Amt] ), [Base] )", System.Array.Empty<string>(), System.Array.Empty<string>(), spec, out var note);
            Assert.Contains("DEFINE", q);                                    // still measure-faithful for context transition
            Assert.NotNull(note);
            Assert.Contains("calculation groups", note);
        }

        [Fact]
        public void BuildProbeQuery_bare_measure_ref_grand_total_stays_inline_already_faithful()
        {
            // A bare measure ref carries its own implicit CALCULATE inline AND keeps the deployed measure's
            // calc-group identity (a DEFINE wrapper would rename what SELECTEDMEASURENAME sees) — so it stays
            // inline, note-free, byte-compatible with the legacy control-total query text.
            var q = DaxBench.BuildProbeQuery("[X]", System.Array.Empty<string>(), System.Array.Empty<string>(),
                new DaxQuerySpec { HomeTable = "Sales", ModelMeasureNames = new[] { "X" } }, out var note);
            Assert.DoesNotContain("DEFINE", q);
            Assert.Contains("\"v\", (\n[X]\n    )", q);
            Assert.Null(note);
        }

        // BLOCKER 4: no session spec (or a cross-model one withheld by the engine) — the host derives from the
        // expression's OWN table ref, which provably exists on the model the query runs on.
        [Fact]
        public void BuildProbeQuery_without_spec_derives_the_host_from_the_expression_not_a_guess()
        {
            var q = DaxBench.BuildProbeQuery("SUM ( 'Sales'[Amt] )", new[] { "'Date'[Year]" }, System.Array.Empty<string>());
            Assert.Matches(@"MEASURE 'Sales'\[__smx_[0-9a-f]{8}_probe\]", q);   // 'Sales' comes from the expression itself
        }

        // BLOCKER 4 (builder half): for a RAW expression with its own table ref, the expression-derived host wins
        // over the spec's session-derived fallback table — the session table may not exist on the endpoint model.
        [Fact]
        public void BuildProbeQuery_expression_derived_host_beats_the_spec_fallback_table()
        {
            var spec = new DaxQuerySpec { HomeTable = "SessionOnlyTable", ModelMeasureNames = System.Array.Empty<string>() };
            var q = DaxBench.BuildProbeQuery("SUM ( 'Sales'[Amt] )", new[] { "'Date'[Year]" }, System.Array.Empty<string>(), spec, out _);
            Assert.Contains("MEASURE 'Sales'[", q);
            Assert.DoesNotContain("SessionOnlyTable", q);
        }

        // SHOULD-FIX 5: token-aware scanning — escaped quotes parse whole, comments and string literals never
        // contribute a phantom host table.
        [Fact]
        public void BuildProbeQuery_parses_escaped_quote_table_names_whole()
        {
            var q = DaxBench.BuildProbeQuery("SUM ( 'O''Brien Sales'[Amount] )", System.Array.Empty<string>(), System.Array.Empty<string>());
            Assert.Contains("MEASURE 'O''Brien Sales'[", q);                 // NOT 'Brien Sales' (the naive-regex misparse)
        }

        [Fact]
        public void BuildProbeQuery_ignores_table_refs_inside_comments_and_strings()
        {
            var expr = "/* 'Phantom'[x] */ SUM ( 'Real'[Amt] ) + IF ( FALSE (), LEN ( \"'Str'[y]\" ) ) -- 'Trailing'[z]";
            var q = DaxBench.BuildProbeQuery(expr, System.Array.Empty<string>(), System.Array.Empty<string>());
            Assert.Contains("MEASURE 'Real'[", q);                           // the only ref OUTSIDE comments/strings
            Assert.DoesNotContain("MEASURE 'Phantom'", q);
            Assert.DoesNotContain("MEASURE 'Str'", q);
            Assert.DoesNotContain("MEASURE 'Trailing'", q);
        }

        // BLOCKER 3: generated DEFINE identifiers must dodge both the model's real measures and names referenced
        // inside the candidate text; deterministic hashing makes the dodge testable.
        [Fact]
        public void BuildProbeQuery_generated_id_dodges_a_colliding_model_measure()
        {
            var expr = "SUM ( 'Sales'[Amt] )";
            var q1 = DaxBench.BuildProbeQuery(expr, System.Array.Empty<string>(), System.Array.Empty<string>());
            var id1 = System.Text.RegularExpressions.Regex.Match(q1, @"\[(__smx_[0-9a-f]{8}_probe)\]").Groups[1].Value;
            Assert.NotEmpty(id1);
            // A model that REALLY has a measure named exactly like the would-be identifier forces a re-roll.
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { id1 } };
            var q2 = DaxBench.BuildProbeQuery(expr, System.Array.Empty<string>(), System.Array.Empty<string>(), spec, out _);
            var id2 = System.Text.RegularExpressions.Regex.Match(q2, @"\[(__smx_[0-9a-f]{8}(_\d+)?_probe)\]").Groups[1].Value;
            Assert.NotEmpty(id2);
            Assert.NotEqual(id1, id2);                                       // collision detected and avoided
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
