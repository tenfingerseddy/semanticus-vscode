using Semanticus.Dax;
using Xunit;
using static Semanticus.Dax.Tests.LexHarness;
using K = Semanticus.Dax.DaxTokenKind;
using TK = Semanticus.Dax.DaxTriviaKind;

namespace Semanticus.Dax.Tests;

/// <summary>
/// EXHAUSTIVE coverage of the spec's closed tables — the §2.4 reserved keywords and the §6.1 whitespace set.
/// Sampled coverage lets a missing or wrong row pass unnoticed, so each table is walked in full and each is
/// cross-checked for completeness against the enum / against the whole BMP.
/// </summary>
public class ExhaustiveTableTests
{
    // The complete §2.4 table, transcribed from the specification (all 21 rows).
    public static readonly (string Spelling, K Kind)[] ReservedKeywords =
    {
        ("DEFINE", K.DefineKeyword),
        ("EVALUATE", K.EvaluateKeyword),
        ("ORDER", K.OrderKeyword),
        ("BY", K.ByKeyword),
        ("START", K.StartKeyword),
        ("AT", K.AtKeyword),
        ("VAR", K.VarKeyword),
        ("RETURN", K.ReturnKeyword),
        ("MEASURE", K.MeasureKeyword),
        ("COLUMN", K.ColumnKeyword),
        ("TABLE", K.TableKeyword),
        ("FUNCTION", K.FunctionKeyword),
        ("WITH", K.WithKeyword),
        ("VISUAL", K.VisualKeyword),
        ("SHAPE", K.ShapeKeyword),
        ("AXIS", K.AxisKeyword),
        ("GROUP", K.GroupKeyword),
        ("TOTAL", K.TotalKeyword),
        ("DENSIFY", K.DensifyKeyword),
        ("IN", K.InKeyword),
        ("NOT", K.NotKeyword),
    };

    public static IEnumerable<object[]> AllKeywords() => ReservedKeywords.Select(k => new object[] { k.Spelling, k.Kind });

    [Fact]
    public void The_keyword_table_has_exactly_21_rows_and_covers_every_keyword_kind()
    {
        Assert.Equal(21, ReservedKeywords.Length);
        Assert.Equal(21, ReservedKeywords.Select(k => k.Spelling).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Completeness in the other direction: every *Keyword member of the enum is produced by a row above,
        // so a new kind cannot be added without a golden.
        var declared = Enum.GetValues<K>().Where(k => k.ToString().EndsWith("Keyword", StringComparison.Ordinal)).ToArray();
        Assert.Equal(declared.OrderBy(k => k.ToString()).ToArray(), ReservedKeywords.Select(k => k.Kind).OrderBy(k => k.ToString()).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllKeywords))]
    public void Every_reserved_keyword_lexes_case_insensitively_and_keeps_its_raw_spelling(string spelling, K kind)
    {
        foreach (string cased in new[] { spelling.ToUpperInvariant(), spelling.ToLowerInvariant(), Alternate(spelling) })
        {
            var r = Golden(cased);
            Assert.Equal(new[] { kind }, TokenKinds(r));
            var t = RealTokens(r)[0];
            Assert.Equal(cased, t.Text);        // spec §2.4: never uppercased or normalized
            Assert.Equal(cased, t.ValueText);   // spec §8.2: keyword ValueText is the raw spelling
            Assert.Equal(cased.Length, t.Span.Length);
        }
    }

    [Theory]
    [MemberData(nameof(AllKeywords))]
    public void A_reserved_keyword_only_matches_the_whole_lexeme(string spelling, K kind)
    {
        // spec §2.4: classification happens only after MAXIMAL identifier scanning.
        Assert.Equal(new[] { K.IdentifierToken }, TokenKinds(Golden(spelling + "X")));
        Assert.Equal(new[] { K.IdentifierToken }, TokenKinds(Golden(spelling + "_1")));
        Assert.Equal(new[] { K.IdentifierToken }, TokenKinds(Golden("X" + spelling)));
        // ...but it does match when it stands alone next to punctuation.
        Assert.Equal(new[] { kind, K.OpenParenToken, K.CloseParenToken }, TokenKinds(Golden(spelling + "()")));
        // ...and never inside a delimiter (spec §2.4 / G-ID-7).
        Assert.Equal(new[] { K.QuotedIdentifierToken }, TokenKinds(Golden("'" + spelling + "'")));
        Assert.Equal(new[] { K.BracketedIdentifierToken }, TokenKinds(Golden("[" + spelling + "]")));
    }

    private static string Alternate(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            chars[i] = i % 2 == 0 ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }

    // ---------------- §6.1 explicit whitespace table ----------------

    // The complete §6.1 table, transcribed from the specification (all 21 code points, in spec order).
    public static readonly int[] WhitespaceTable =
    {
        0x0009, 0x000B, 0x000C, 0x0020, 0x00A0, 0x1680, 0x180E,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x202F, 0x205F, 0x3000,
    };

    public static IEnumerable<object[]> AllWhitespace() => WhitespaceTable.Select(u => new object[] { u });

    [Theory]
    [MemberData(nameof(AllWhitespace))]
    public void Every_table_entry_is_whitespace_trivia_on_its_own(int codeUnit)
    {
        string src = Cu(codeUnit);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        Assert.Empty(RealTokens(r));
        Assert.Equal(new[] { TK.WhitespaceTrivia }, AllTrivia(r).Select(t => t.Kind).ToArray());
        Assert.Equal(1, AllTrivia(r)[0].Span.Length);
    }

    [Fact]
    public void The_whole_table_in_one_run_is_a_single_maximal_whitespace_trivia()
    {
        Assert.Equal(21, WhitespaceTable.Length);
        string src = Cu(WhitespaceTable);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        Assert.Equal(new[] { TK.WhitespaceTrivia }, AllTrivia(r).Select(t => t.Kind).ToArray()); // maximal run
        Assert.Equal(WhitespaceTable.Length, AllTrivia(r)[0].Span.Length);
    }

    [Fact]
    public void No_code_unit_outside_the_table_is_ever_whitespace_trivia()
    {
        // Exhaustive over the whole BMP: classification is the explicit table and nothing else (spec §6.1,
        // §9.5 — no char.IsWhiteSpace, no Unicode category tables, no culture). This is the assertion that
        // catches a silent widening of the set.
        var table = new HashSet<int>(WhitespaceTable);
        for (int u = 0; u <= 0xFFFF; u++)
        {
            var r = Lex(Cu(u));
            bool isWs = AllTrivia(r).Any(t => t.Kind == TK.WhitespaceTrivia);
            Assert.True(isWs == table.Contains(u), $"U+{u:X4}: whitespace={isWs}, table={table.Contains(u)}");
        }
    }

    [Fact]
    public void The_notable_exclusions_are_bad_tokens_not_whitespace()
    {
        foreach (int u in new[] { 0x200B, 0x2060 }) // ZERO WIDTH SPACE, WORD JOINER (spec §6.1)
        {
            var r = Lex(Cu(u));
            Assert.Equal(new[] { K.BadToken }, TokenKinds(r));
            AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
        }
    }

    // ---------------- §6.2 standalone CR and standalone LF ----------------

    [Fact]
    public void A_standalone_CR_is_one_width_one_line_ending()
    {
        string src = "a" + Cu(0x0D) + "b";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        var eol = AllTrivia(r).Single();
        Assert.Equal(TK.EndOfLineTrivia, eol.Kind);
        Assert.Equal(1, eol.Span.Length);
        Assert.Equal(Cu(0x0D), eol.Text);                       // exact raw form, never normalized (spec §6.2)
        Assert.Equal(new[] { eol.Span.Start }, RealTokens(r)[0].TrailingTrivia.Select(t => t.Span.Start).ToArray());
    }

    [Fact]
    public void A_standalone_LF_is_one_width_one_line_ending()
    {
        string src = "a" + Cu(0x0A) + "b";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        var eol = AllTrivia(r).Single();
        Assert.Equal(TK.EndOfLineTrivia, eol.Kind);
        Assert.Equal(1, eol.Span.Length);
        Assert.Equal(Cu(0x0A), eol.Text);
    }

    [Fact]
    public void CR_and_LF_pair_only_in_that_order()
    {
        // LF-then-CR is two separate line endings; CRLF is one width-2 item (spec §6.2).
        string reversed = Cu(0x0A, 0x0D);
        var r = Lex(reversed);
        AssertTiling(r, reversed);
        Assert.Equal(new[] { 1, 1 }, AllTrivia(r).Select(t => t.Span.Length).ToArray());

        string crlf = Cu(0x0D, 0x0A);
        var r2 = Lex(crlf);
        AssertTiling(r2, crlf);
        Assert.Equal(new[] { 2 }, AllTrivia(r2).Select(t => t.Span.Length).ToArray());

        // A CR at end of source has no LF to join and stays width one.
        string trailingCr = "a" + Cu(0x0D);
        var r3 = Lex(trailingCr);
        AssertTiling(r3, trailingCr);
        Assert.Equal(new[] { 1 }, AllTrivia(r3).Select(t => t.Span.Length).ToArray());

        // CR followed by a non-LF unit likewise.
        string crThenX = Cu(0x0D) + "x";
        var r4 = Lex(crThenX);
        AssertTiling(r4, crThenX);
        Assert.Equal(new[] { 1 }, AllTrivia(r4).Select(t => t.Span.Length).ToArray());
    }

    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x0A)]
    public void A_standalone_CR_or_LF_terminates_quoted_and_bracketed_recovery(int eol)
    {
        // spec §7.2: recovery stops BEFORE the break and the break is not swallowed.
        string q = "'Sales" + Cu(eol) + "x";
        var rq = Lex(q);
        AssertTiling(rq, q);
        AssertDiags(rq, (DaxDiagnosticCodes.UnterminatedQuotedIdentifier, 0, 6));
        Assert.Contains(AllTrivia(rq), t => t.Kind == TK.EndOfLineTrivia);

        string b = "[Amount" + Cu(eol) + "x";
        var rb = Lex(b);
        AssertTiling(rb, b);
        AssertDiags(rb, (DaxDiagnosticCodes.UnterminatedBracketedIdentifier, 0, 7));
        Assert.Contains(AllTrivia(rb), t => t.Kind == TK.EndOfLineTrivia);
    }

    // ---------------- §7.3 multiple diagnostics, in source order ----------------

    [Fact]
    public void Three_bad_tokens_produce_three_diagnostics_in_source_order()
    {
        // spec §7.1: unknown runs are never coalesced; §7.3: each diagnostic appears exactly once, in
        // nondecreasing Span.Start order.
        var r = Golden("#$%");
        AssertKindsAre(r, K.BadToken, K.BadToken, K.BadToken);
        AssertDiags(r,
            (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1),
            (DaxDiagnosticCodes.UnrecognizedCharacter, 1, 1),
            (DaxDiagnosticCodes.UnrecognizedCharacter, 2, 1));
    }

    [Fact]
    public void Mixed_diagnostic_kinds_are_reported_once_each_in_source_order()
    {
        //   [0,2)  "[a"   unterminated bracketed identifier, stops before the LF
        //    2     LF
        //   [3,4)  "#"    bad token
        //   [4,6)  "'b"   unterminated quoted identifier, stops before the LF
        //    6     LF
        //   [7,9)  "\"c"  unterminated string through EOF
        string src = "[a" + Cu(0x0A) + "#'b" + Cu(0x0A) + "\"c";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertDiags(r,
            (DaxDiagnosticCodes.UnterminatedBracketedIdentifier, 0, 2),
            (DaxDiagnosticCodes.UnrecognizedCharacter, 3, 1),
            (DaxDiagnosticCodes.UnterminatedQuotedIdentifier, 4, 2),
            (DaxDiagnosticCodes.UnterminatedString, 7, 2));
    }

    private static void AssertKindsAre(DaxLexResult r, params K[] expected) => Assert.Equal(expected, TokenKinds(r));
}
