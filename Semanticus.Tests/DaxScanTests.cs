using System.Linq;
using Semanticus.Analysis;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The structural helpers under <see cref="DaxScan"/> — the token path the lint rules stand on. These
    /// pin the primitives directly (depth discipline, exact-span root detection, VAR body scoping, delimited-name
    /// capture) so a regression in a helper surfaces here, not as a mystery false-positive three rules away. The
    /// documented fail-safe behaviours (unknown-root VAR ⇒ table; <c>T[C]</c> vs <c>'T'[C]</c> compare UNEQUAL) are
    /// asserted as-is — they are contracts the rules depend on, not bugs.</summary>
    public sealed class DaxScanTests
    {
        private static System.Collections.Generic.List<DaxScan.Tok> Tok(string dax) => DaxScan.Tokenize(dax);

        // ---- VarDecls: names + table-vs-scalar classification ----
        [Fact]
        public void VarDecls_captures_name_and_classifies_table_body()
        {
            var toks = Tok("VAR t = FILTER(Sales, Sales[Amt] > 0) RETURN COUNTROWS(t)");
            var vars = DaxScan.VarDecls(toks);
            var v = Assert.Single(vars);
            Assert.Equal("t", v.Name);
            Assert.True(v.TableValued);   // FILTER root ⇒ table
        }

        [Fact]
        public void VarDecls_classifies_scalar_expression_body_as_not_table()
        {
            var v = Assert.Single(DaxScan.VarDecls(Tok("VAR a = [M] + 1 RETURN a")));
            Assert.False(v.TableValued);   // measure ref + operator ⇒ scalar
        }

        [Fact]
        public void VarDecls_classifies_table_constructor_as_table()
        {
            var v = Assert.Single(DaxScan.VarDecls(Tok("VAR a = {1,2} RETURN COUNTROWS(a)")));
            Assert.True(v.TableValued);   // {…} constructor ⇒ table
        }

        [Fact]
        public void VarDecls_body_ends_at_return_inside_a_call_arg()
        {
            // A VAR/RETURN block nested as the first arg of CALCULATE — the body must stop at RETURN, not run past
            // the comma into the next arg. Body is exactly the literal 1.
            var toks = Tok("CALCULATE( VAR a = 1 RETURN SUMX(T, [x]), ALL(T) )");
            var v = Assert.Single(DaxScan.VarDecls(toks));
            Assert.Equal("a", v.Name);
            Assert.Equal("1", DaxScan.NormText(toks, v.BodyStart, v.BodyEnd));
            Assert.False(v.TableValued);
        }

        [Fact]
        public void VarDecls_finds_none_when_absent()
        {
            Assert.Empty(DaxScan.VarDecls(Tok("SUMX ( FILTER(T, T[x] > 0), [y] )")));
        }

        // ---- MatchingRParen on nested calls ----
        [Fact]
        public void MatchingRParen_pairs_the_outer_call()
        {
            // SUM ( CALCULATE ( [x] ) ) — the '(' at index 1 matches the final ')' at index 6, skipping the inner pair.
            var toks = Tok("SUM ( CALCULATE ( [x] ) )");
            Assert.Equal(DaxScan.Kind.LParen, toks[1].Kind);
            Assert.Equal(6, DaxScan.MatchingRParen(toks, 1));
        }

        [Fact]
        public void MatchingRParen_returns_minus_one_for_non_lparen()
        {
            var toks = Tok("SUM ( [x] )");
            Assert.Equal(-1, DaxScan.MatchingRParen(toks, 0));   // index 0 is the Word, not an LParen
        }

        // ---- RootCallName: EXACT span only ----
        [Fact]
        public void RootCallName_matches_a_whole_range_call()
        {
            var toks = Tok("SUM(T[A])");
            Assert.Equal("SUM", DaxScan.RootCallName(toks, 0, toks.Count));
        }

        [Fact]
        public void RootCallName_null_when_call_does_not_span_the_range()
        {
            var toks = Tok("SUM(T[A]) + 1");   // the call is only part of the range → not the root
            Assert.Null(DaxScan.RootCallName(toks, 0, toks.Count));
        }

        // ---- TopLevelOps: depth discipline ----
        [Fact]
        public void TopLevelOps_ignores_ops_inside_parens()
        {
            // (a+b)/c — the '+' sits one level deeper inside the group; only the '/' is top-level.
            var toks = Tok("(a+b)/c");
            var ops = DaxScan.TopLevelOps(toks, 0, toks.Count).Select(o => o.op).ToArray();
            Assert.Equal(new[] { "/" }, ops);
        }

        [Fact]
        public void TopLevelOps_collects_multiple_at_the_top()
        {
            var toks = Tok("SUM(S[A]) / SUM(S[Q]) + 1");
            var ops = DaxScan.TopLevelOps(toks, 0, toks.Count).Select(o => o.op).ToArray();
            Assert.Equal(new[] { "/", "+" }, ops);   // the '/' inside SUM's args is deeper; these two are top-level
        }

        // ---- NormText: case-insensitivity + the documented delim-class fail-safe ----
        [Fact]
        public void NormText_is_case_insensitive_on_identifiers()
        {
            Assert.Equal(DaxScan.NormText(Tok("SUM(T[A])"), 0, Tok("SUM(T[A])").Count),
                         DaxScan.NormText(Tok("sum(t[a])"), 0, Tok("sum(t[a])").Count));
        }

        [Fact]
        public void NormText_treats_quoted_and_bare_table_refs_as_unequal()
        {
            // T[C] vs 'T'[C] — same column, but NormText keeps the delimiter class, so they compare UNEQUAL.
            // A documented false-negative (the safe direction for the divide-guard equality check).
            var bare = DaxScan.NormText(Tok("T[C]"), 0, Tok("T[C]").Count);
            var quoted = DaxScan.NormText(Tok("'T'[C]"), 0, Tok("'T'[C]").Count);
            Assert.NotEqual(bare, quoted);
        }

        // ---- ArgRanges: an empty call has zero args ----
        [Fact]
        public void ArgRanges_empty_call_has_no_args()
        {
            var toks = Tok("FOO()");
            var lparen = toks.FindIndex(t => t.Kind == DaxScan.Kind.LParen);
            Assert.Empty(DaxScan.ArgRanges(toks, lparen));
        }

        [Fact]
        public void ArgRanges_splits_top_level_commas_only()
        {
            // FOO(a, BAR(b, c)) — two top-level args; the inner comma is deeper and does not split.
            var toks = Tok("FOO(a, BAR(b, c))");
            var lparen = toks.FindIndex(t => t.Kind == DaxScan.Kind.LParen);
            Assert.Equal(2, DaxScan.ArgRanges(toks, lparen).Count);
        }

        // ---- Tokenize: Inner/Delim capture including escaped ]] and '' ----
        [Fact]
        public void Tokenize_captures_bracketed_name_inner_and_delim()
        {
            var toks = Tok("[Amt]");
            var t = Assert.Single(toks);
            Assert.Equal(DaxScan.Kind.Name, t.Kind);
            Assert.Equal('[', t.Delim);
            Assert.Equal("Amt", t.Inner);
            Assert.Equal("", t.Text);   // Text is empty for a Name — rules must never keyword-match its content
        }

        [Fact]
        public void Tokenize_unescapes_a_doubled_close_bracket()
        {
            var t = Assert.Single(Tok("[Col]]Name]"));   // ]] is the escaped ]
            Assert.Equal('[', t.Delim);
            Assert.Equal("Col]Name", t.Inner);
        }

        [Fact]
        public void Tokenize_unescapes_a_doubled_quote_in_a_table_name()
        {
            var toks = Tok("'Tab''le'[C]");   // '' is the escaped ' inside a quoted table name
            Assert.Equal(DaxScan.Kind.Name, toks[0].Kind);
            Assert.Equal('\'', toks[0].Delim);
            Assert.Equal("Tab'le", toks[0].Inner);
            Assert.Equal('[', toks[1].Delim);
            Assert.Equal("C", toks[1].Inner);
        }

        [Fact]
        public void Tokenize_captures_a_string_literal_inner_with_doubled_quote()
        {
            var t = Assert.Single(Tok("\"a\"\"b\""));   // "" is the escaped " inside a string literal
            Assert.Equal(DaxScan.Kind.String, t.Kind);
            Assert.Equal('"', t.Delim);
            Assert.Equal("a\"b", t.Inner);
            Assert.Equal("", t.Text);
        }
    }
}
