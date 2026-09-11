using Semanticus.Dax;
using Xunit;
using static Semanticus.Dax.Tests.LexHarness;
using K = Semanticus.Dax.DaxTokenKind;
using TK = Semanticus.Dax.DaxTriviaKind;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Every §10 golden as a named test (the golden id is in the test name). Each case runs <see cref="Golden"/>
/// (which asserts §7.4 tiling + determinism + source-backed raw text + EOF position + diagnostic spans) and
/// then asserts the case-specific kinds, raw/value text, and diagnostics.
/// </summary>
public class GoldenTests
{
    private static void AssertKinds(DaxLexResult r, params K[] expected) => Assert.Equal(expected, TokenKinds(r));
    private static void AssertTriviaKinds(DaxLexResult r, params TK[] expected) => Assert.Equal(expected, AllTrivia(r).Select(t => t.Kind).ToArray());

    // ---------------- §10.1 Vendored-lexer regressions ----------------

    [Fact]
    public void G_DOT_1_dotted_name_segments_and_dots()
    {
        var r = Golden("INFO.VIEW.MEASURES");
        AssertKinds(r, K.IdentifierToken, K.DotToken, K.IdentifierToken, K.DotToken, K.IdentifierToken);
        var t = RealTokens(r);
        Assert.Equal("INFO", t[0].Text);
        Assert.Equal("VIEW", t[2].Text);
        Assert.Equal("MEASURES", t[4].Text);
    }

    [Fact]
    public void G_DOT_2_double_dot()
    {
        var r = Golden("A..B");
        AssertKinds(r, K.IdentifierToken, K.DotToken, K.DotToken, K.IdentifierToken);
    }

    [Fact]
    public void G_DOT_3_leading_dot()
    {
        var r = Golden(".A");
        AssertKinds(r, K.DotToken, K.IdentifierToken);
    }

    [Fact]
    public void G_SCI_1_integer_exponent()
    {
        var r = Golden("1E5");
        AssertKinds(r, K.ScientificLiteralToken);
    }

    [Fact]
    public void G_SCI_2_decimal_negative_exponent()
    {
        var r = Golden("1.5e-10");
        AssertKinds(r, K.ScientificLiteralToken);
    }

    [Fact]
    public void G_SCI_3_dot_leading_positive_exponent()
    {
        var r = Golden(".5E+2");
        AssertKinds(r, K.ScientificLiteralToken);
    }

    [Fact]
    public void G_EQEQ_1_strict_equals_is_one_token()
    {
        var r = Golden("a==b");
        AssertKinds(r, K.IdentifierToken, K.StrictEqualsToken, K.IdentifierToken);
    }

    [Fact]
    public void G_DROP_1A_at_parameter()
    {
        var r = Golden("@p");
        AssertKinds(r, K.AtToken, K.IdentifierToken);
    }

    [Fact]
    public void G_DROP_1B_semicolon_is_bad()
    {
        var r = Golden(";");
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
        Assert.Equal(";", RealTokens(r)[0].Text);
    }

    [Fact]
    public void G_DROP_1C_hash_is_bad()
    {
        var r = Golden("#");
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    [Fact]
    public void G_DROP_1D_percent_is_bad()
    {
        var r = Golden("%");
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    [Fact]
    public void G_DROP_1E_backslash_is_bad()
    {
        var r = Golden("\\");
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
        Assert.Equal("\\", RealTokens(r)[0].Text);
    }

    [Fact]
    public void G_DROP_1F_dollar_is_bad()
    {
        var r = Golden("$");
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    [Fact]
    public void G_KEYCHAN_1_table_name_not_function_keyword()
    {
        var r = Golden("Date[Key]");
        AssertKinds(r, K.IdentifierToken, K.BracketedIdentifierToken);
        Assert.Equal("Date", RealTokens(r)[0].Text); // 'Date' is an ordinary identifier, not a keyword token
    }

    [Fact]
    public void G_DECODE_1_string_doubled_quote()
    {
        var r = Golden("\"a\"\"b\"");
        var t = RealTokens(r)[0];
        Assert.Equal(K.StringLiteralToken, t.Kind);
        Assert.Equal("\"a\"\"b\"", t.Text);       // raw unchanged
        Assert.Equal("a\"b", t.ValueText);         // decoded
    }

    [Fact]
    public void G_DECODE_2_quoted_identifier()
    {
        var r = Golden("'Sales Table'");
        var t = RealTokens(r)[0];
        Assert.Equal(K.QuotedIdentifierToken, t.Kind);
        Assert.Equal("'Sales Table'", t.Text);
        Assert.Equal("Sales Table", t.ValueText);
    }

    [Fact]
    public void G_DECODE_3_bracketed_doubled_bracket()
    {
        var r = Golden("[Col]]umn]");
        var t = RealTokens(r)[0];
        Assert.Equal(K.BracketedIdentifierToken, t.Kind);
        Assert.Equal("[Col]]umn]", t.Text);
        Assert.Equal("Col]umn", t.ValueText);
    }

    // ---------------- §10.2 Identifier and keyword goldens ----------------

    [Fact]
    public void G_ID_1_identifier_with_digits()
    {
        var r = Golden("Sales_2026");
        AssertKinds(r, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_2_leading_underscores()
    {
        var r = Golden("__x");
        AssertKinds(r, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_3_var_return_case_insensitive()
    {
        var r = Golden("vAr x=1 rEtUrN x");
        AssertKinds(r, K.VarKeyword, K.IdentifierToken, K.EqualsToken, K.IntegerLiteralToken, K.ReturnKeyword, K.IdentifierToken);
        var t = RealTokens(r);
        Assert.Equal("vAr", t[0].ValueText);       // keywords retain raw casing (spec §8.2)
        Assert.Equal("rEtUrN", t[4].ValueText);
    }

    [Fact]
    public void G_ID_4_orderby_is_one_identifier()
    {
        var r = Golden("ORDERBY");
        AssertKinds(r, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_5_builtins_are_identifiers()
    {
        var r = Golden("SUM DATE TRUE FALSE BLANK");
        AssertKinds(r, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_6_contextual_words_are_identifiers()
    {
        var r = Golden("ASC DESC ANYREF EXPR YEAR ROWS");
        AssertKinds(r, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_7_quoted_keyword_text_stays_quoted_identifier()
    {
        var r = Golden("'RETURN'[Value]");
        AssertKinds(r, K.QuotedIdentifierToken, K.BracketedIdentifierToken);
        Assert.Equal("RETURN", RealTokens(r)[0].ValueText); // never keyword-classified inside quotes
    }

    [Fact]
    public void G_ID_8_bracketed_at_is_content()
    {
        var r = Golden("[@Margin]");
        AssertKinds(r, K.BracketedIdentifierToken);
        Assert.Equal("@Margin", RealTokens(r)[0].ValueText); // '@' inside brackets is not AtToken (spec §3.5)
    }

    [Fact]
    public void G_ID_9_at_parameter()
    {
        var r = Golden("@Margin");
        AssertKinds(r, K.AtToken, K.IdentifierToken);
    }

    [Fact]
    public void G_ID_10_quoted_double_quote_is_content()
    {
        var r = Golden("'A\"\"B'");
        AssertKinds(r, K.QuotedIdentifierToken);
        Assert.Equal("A\"\"B", RealTokens(r)[0].ValueText); // double quotes are ordinary in a quoted name
    }

    [Fact]
    public void G_ID_11_quoted_escaped_apostrophe()
    {
        var r = Golden("'O''Brien'");
        AssertKinds(r, K.QuotedIdentifierToken);
        Assert.Equal("O'Brien", RealTokens(r)[0].ValueText);
    }

    // ---------------- §10.3 Numeric and literal goldens ----------------

    [Fact]
    public void G_NUM_1_integers()
    {
        var r = Golden("0 001 123");
        AssertKinds(r, K.IntegerLiteralToken, K.IntegerLiteralToken, K.IntegerLiteralToken);
    }

    [Fact]
    public void G_NUM_2_decimals()
    {
        var r = Golden("1.0 .5 001.250");
        AssertKinds(r, K.DecimalLiteralToken, K.DecimalLiteralToken, K.DecimalLiteralToken);
    }

    [Fact]
    public void G_NUM_3_integer_then_dot()
    {
        var r = Golden("1.");
        AssertKinds(r, K.IntegerLiteralToken, K.DotToken);
    }

    [Fact]
    public void G_NUM_4_integer_dot_identifier()
    {
        var r = Golden("1.e5");
        AssertKinds(r, K.IntegerLiteralToken, K.DotToken, K.IdentifierToken);
    }

    [Fact]
    public void G_NUM_5_integer_identifier_plus()
    {
        var r = Golden("1E+");
        AssertKinds(r, K.IntegerLiteralToken, K.IdentifierToken, K.PlusToken);
    }

    [Fact]
    public void G_NUM_6_unary_minus_and_caret()
    {
        var r = Golden("-2^2");
        AssertKinds(r, K.MinusToken, K.IntegerLiteralToken, K.CaretToken, K.IntegerLiteralToken);
    }

    [Fact]
    public void G_NUM_7_caret_negative()
    {
        var r = Golden("2^-3");
        AssertKinds(r, K.IntegerLiteralToken, K.CaretToken, K.MinusToken, K.IntegerLiteralToken);
    }

    [Fact]
    public void G_NUM_8_hex_is_integer_then_identifier()
    {
        var r = Golden("0xFF");
        AssertKinds(r, K.IntegerLiteralToken, K.IdentifierToken);
        var t = RealTokens(r);
        Assert.Equal("0", t[0].Text);
        Assert.Equal("xFF", t[1].Text);
    }

    [Fact]
    public void G_NUM_9_dollar_then_decimal()
    {
        var r = Golden("$1.00");
        AssertKinds(r, K.BadToken, K.DecimalLiteralToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
        Assert.Equal("1.00", RealTokens(r)[1].Text);
    }

    [Fact]
    public void G_DATE_1_date_literal_decoded()
    {
        var r = Golden("dt\"2026-07-23\"");
        var t = RealTokens(r)[0];
        Assert.Equal(K.DateLiteralToken, t.Kind);
        Assert.True(t.IsTerminated);
        Assert.Equal("2026-07-23", t.ValueText);
    }

    [Fact]
    public void G_DATE_2_invalid_body_is_still_a_date_token()
    {
        var r = Golden("DT\"bad\"");
        var t = RealTokens(r)[0];
        Assert.Equal(K.DateLiteralToken, t.Kind);
        Assert.Equal("bad", t.ValueText); // value validity is not lexical
    }

    [Fact]
    public void G_DATE_3_whitespace_breaks_prefix()
    {
        var r = Golden("dt \"2026-07-23\"");
        AssertKinds(r, K.IdentifierToken, K.StringLiteralToken);
        Assert.Equal("dt", RealTokens(r)[0].Text);
    }

    [Fact]
    public void G_STR_1_reference_looking_text_stays_one_string()
    {
        var r = Golden("\"See [Amount]\"");
        AssertKinds(r, K.StringLiteralToken);
        Assert.Equal("See [Amount]", RealTokens(r)[0].ValueText);
    }

    [Fact]
    public void G_STR_2_multiline_string_retains_crlf()
    {
        string src = Cu(0x22) + "a" + Cu(0x0D, 0x0A) + "b" + Cu(0x22); // "a<CR><LF>b"
        var r = Golden(src);
        var t = RealTokens(r)[0];
        Assert.Equal(K.StringLiteralToken, t.Kind);
        Assert.True(t.IsTerminated);
        Assert.Equal("a" + Cu(0x0D, 0x0A) + "b", t.ValueText);
        Assert.Empty(AllTrivia(r)); // the CRLF is INSIDE the token, not an EndOfLine trivia
    }

    [Fact]
    public void G_STR_3_unterminated_string_through_eof()
    {
        var r = Golden("\"unterminated");
        var t = RealTokens(r)[0];
        Assert.Equal(K.StringLiteralToken, t.Kind);
        Assert.False(t.IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedString, 0, 13));
    }

    [Fact]
    public void G_BOOL_1_true_is_call_syntax()
    {
        var r = Golden("TRUE()");
        AssertKinds(r, K.IdentifierToken, K.OpenParenToken, K.CloseParenToken);
    }

    // ---------------- §10.4 Operator / punctuation goldens ----------------

    [Fact]
    public void G_OPS_1_all_operators_once_in_order()
    {
        var r = Golden("+ - * / ^ & = == <> < <= > >= && || =>");
        AssertKinds(r,
            K.PlusToken, K.MinusToken, K.StarToken, K.SlashToken, K.CaretToken, K.AmpersandToken,
            K.EqualsToken, K.StrictEqualsToken, K.NotEqualsToken, K.LessThanToken, K.LessThanOrEqualsToken,
            K.GreaterThanToken, K.GreaterThanOrEqualsToken, K.AndAndToken, K.OrOrToken, K.LambdaArrowToken);
    }

    [Fact]
    public void G_OPS_2_triple_equals()
    {
        var r = Golden("===");
        AssertKinds(r, K.StrictEqualsToken, K.EqualsToken);
    }

    [Fact]
    public void G_OPS_3_less_equals_equals()
    {
        var r = Golden("<==");
        AssertKinds(r, K.LessThanOrEqualsToken, K.EqualsToken);
    }

    [Fact]
    public void G_OPS_4_bang_equals()
    {
        var r = Golden("!=");
        AssertKinds(r, K.BadToken, K.EqualsToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    [Fact]
    public void G_OPS_5_one_dash_dash_two_is_comment()
    {
        var r = Golden("1--2");
        AssertKinds(r, K.IntegerLiteralToken);
        AssertTriviaKinds(r, TK.DashDashCommentTrivia);
        Assert.Equal("--2", AllTrivia(r)[0].Text);
    }

    [Fact]
    public void G_OPS_6_separated_minus_minus()
    {
        var r = Golden("1 - -2");
        AssertKinds(r, K.IntegerLiteralToken, K.MinusToken, K.MinusToken, K.IntegerLiteralToken);
    }

    [Fact]
    public void G_OPS_7_empty_argument_commas()
    {
        var r = Golden("Func(1,,3)");
        AssertKinds(r, K.IdentifierToken, K.OpenParenToken, K.IntegerLiteralToken, K.CommaToken, K.CommaToken, K.IntegerLiteralToken, K.CloseParenToken);
    }

    [Fact]
    public void G_OPS_8_table_and_row_constructor()
    {
        var r = Golden("{(1,2)}");
        AssertKinds(r, K.OpenBraceToken, K.OpenParenToken, K.IntegerLiteralToken, K.CommaToken, K.IntegerLiteralToken, K.CloseParenToken, K.CloseBraceToken);
    }

    // ---------------- §10.5 Comment and trivia goldens ----------------

    [Fact]
    public void G_COM_1_slash_slash()
    {
        var r = Golden("// c");
        AssertKinds(r); // no real tokens
        AssertTriviaKinds(r, TK.SlashSlashCommentTrivia);
    }

    [Fact]
    public void G_COM_2_dash_dash()
    {
        var r = Golden("-- c");
        AssertKinds(r);
        AssertTriviaKinds(r, TK.DashDashCommentTrivia);
    }

    [Fact]
    public void G_COM_3_documentation_comment_at_is_not_a_token()
    {
        var r = Golden("/// @param x");
        AssertKinds(r);
        AssertTriviaKinds(r, TK.DocumentationCommentTrivia);
    }

    [Fact]
    public void G_COM_4_nested_delimited_comment()
    {
        var r = Golden("/* a /* b */ c */");
        AssertKinds(r);
        AssertTriviaKinds(r, TK.DelimitedCommentTrivia);
        Assert.True(AllTrivia(r)[0].IsTerminated);
    }

    [Fact]
    public void G_COM_5_unterminated_delimited_comment()
    {
        var r = Golden("/* open");
        AssertKinds(r);
        AssertTriviaKinds(r, TK.DelimitedCommentTrivia);
        Assert.False(AllTrivia(r)[0].IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedDelimitedComment, 0, 7));
    }

    [Fact]
    public void G_COM_6_inter_token_trivia_all_trailing_a()
    {
        var r = Golden("a /* c */ b");
        AssertKinds(r, K.IdentifierToken, K.IdentifierToken);
        var a = RealTokens(r)[0];
        var b = RealTokens(r)[1];
        Assert.Equal(new[] { TK.WhitespaceTrivia, TK.DelimitedCommentTrivia, TK.WhitespaceTrivia }, a.TrailingTrivia.Select(x => x.Kind).ToArray());
        Assert.Empty(b.LeadingTrivia);
    }

    [Fact]
    public void G_COM_7_comment_and_newline_trailing_two_spaces_leading()
    {
        string src = "a // c" + Cu(0x0D, 0x0A) + "  b";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        var a = RealTokens(r)[0];
        var b = RealTokens(r)[1];
        // " // c" (space + comment) + CRLF are trailing 'a'; the two spaces are leading 'b'.
        Assert.Equal(new[] { TK.WhitespaceTrivia, TK.SlashSlashCommentTrivia, TK.EndOfLineTrivia }, a.TrailingTrivia.Select(x => x.Kind).ToArray());
        Assert.Equal(new[] { TK.WhitespaceTrivia }, b.LeadingTrivia.Select(x => x.Kind).ToArray());
        Assert.Equal("  ", b.LeadingTrivia[0].Text);
    }

    [Fact]
    public void G_COM_8_leading_comment_and_spaces()
    {
        var r = Golden("/* c */  a");
        AssertKinds(r, K.IdentifierToken);
        var a = RealTokens(r)[0];
        Assert.Equal(new[] { TK.DelimitedCommentTrivia, TK.WhitespaceTrivia }, a.LeadingTrivia.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void G_COM_9_final_trivia_trailing_a()
    {
        var r = Golden("a  /* c */");
        AssertKinds(r, K.IdentifierToken);
        var a = RealTokens(r)[0];
        Assert.Equal(new[] { TK.WhitespaceTrivia, TK.DelimitedCommentTrivia }, a.TrailingTrivia.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void G_EOL_1_five_line_endings()
    {
        string src = Cu(0x0D, 0x0A, 0x0D, 0x0A, 0x85, 0x2028, 0x2029); // CRLF CRLF NEL LS PS
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        var trivia = AllTrivia(r);
        Assert.All(trivia, t => Assert.Equal(TK.EndOfLineTrivia, t.Kind));
        Assert.Equal(new[] { 2, 2, 1, 1, 1 }, trivia.Select(t => t.Span.Length).ToArray());
    }

    [Fact]
    public void G_WS_1_whitespace_run()
    {
        string src = Cu(0x09, 0x20, 0x180E, 0x20, 0x3000); // tab, space, MVS, space, ideographic space
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertTriviaKinds(r, TK.WhitespaceTrivia);
        Assert.Equal(5, AllTrivia(r)[0].Span.Length);
    }

    [Fact]
    public void G_WS_2_zero_width_space_is_bad()
    {
        string src = Cu(0x200B);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    [Fact]
    public void G_WS_3_word_joiner_is_bad()
    {
        string src = Cu(0x2060);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    // ---------------- §10.6 Unicode, BOM, and degenerate-input goldens ----------------

    [Fact]
    public void G_EMPTY_1_empty_input()
    {
        var r = Golden("");
        AssertKinds(r); // only EOF
        var eof = Eof(r);
        Assert.Equal(0, eof.Span.Start);
        Assert.Equal(0, eof.Span.Length);
        Assert.Equal("", eof.Text);
    }

    [Fact]
    public void G_ONLYTRIVIA_1_all_trivia_leading_eof()
    {
        string src = Cu(0x20, 0x09, 0x0D, 0x0A); // space tab CRLF
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r); // no real tokens
        var eof = Eof(r);
        Assert.Equal(new[] { TK.WhitespaceTrivia, TK.EndOfLineTrivia }, eof.LeadingTrivia.Select(t => t.Kind).ToArray());
    }

    [Fact]
    public void G_BOM_1_bom_leads_first_token()
    {
        string src = Cu(0xFEFF) + "SUM(1)";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.IdentifierToken, K.OpenParenToken, K.IntegerLiteralToken, K.CloseParenToken);
        var sum = RealTokens(r)[0];
        Assert.Equal(new[] { TK.ByteOrderMarkTrivia }, sum.LeadingTrivia.Select(t => t.Kind).ToArray());
    }

    [Fact]
    public void G_BOM_2_second_bom_is_bad()
    {
        string src = Cu(0xFEFF, 0xFEFF) + "1";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken, K.IntegerLiteralToken);
        var bad = RealTokens(r)[0];
        Assert.Equal(new[] { TK.ByteOrderMarkTrivia }, bad.LeadingTrivia.Select(t => t.Kind).ToArray()); // first FEFF is BOM
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 1, 1));  // second FEFF is bad
    }

    [Fact]
    public void G_UNICODE_1_quoted_identifier_preserves_emoji()
    {
        string src = "'Ventes " + Cu(0x03A9) + " " + Cu(0xD83D, 0xDE00) + "'";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.QuotedIdentifierToken);
        Assert.Equal("Ventes " + Cu(0x03A9) + " " + Cu(0xD83D, 0xDE00), RealTokens(r)[0].ValueText);
    }

    [Fact]
    public void G_UNICODE_2_bracketed_identifier_unicode()
    {
        string src = "[Montant " + Cu(0x20AC) + " " + Cu(0xD83D, 0xDE00) + "]";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BracketedIdentifierToken);
    }

    [Fact]
    public void G_UNICODE_3_bmp_scalar_is_width_one_bad()
    {
        string src = Cu(0x03A9); // Omega
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        Assert.Equal(1, RealTokens(r)[0].Span.Length);
    }

    [Fact]
    public void G_UNICODE_4_surrogate_pair_is_one_width_two_bad()
    {
        string src = Cu(0xD83D, 0xDE00); // grinning face
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        Assert.Equal(2, RealTokens(r)[0].Span.Length); // pair stays together (spec §7.1)
    }

    [Fact]
    public void G_UNICODE_5_unpaired_high_surrogate()
    {
        string src = Cu(0xD83D);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        Assert.Equal(1, RealTokens(r)[0].Span.Length);
    }

    [Fact]
    public void G_UNICODE_6_unpaired_low_surrogate()
    {
        string src = Cu(0xDE00);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        Assert.Equal(1, RealTokens(r)[0].Span.Length);
    }

    [Fact]
    public void G_UNICODE_7_identifier_then_bad_scalar()
    {
        string src = "Ventes" + Cu(0x03A9);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.IdentifierToken, K.BadToken);
        Assert.Equal("Ventes", RealTokens(r)[0].Text);
    }

    [Fact]
    public void G_NUL_1_nul_is_bad_no_truncation()
    {
        string src = Cu(0x0000);
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BadToken);
        Assert.Equal(1, RealTokens(r)[0].Span.Length);
        AssertDiags(r, (DaxDiagnosticCodes.UnrecognizedCharacter, 0, 1));
    }

    // ---------------- §10.7 Unterminated-name goldens ----------------

    [Fact]
    public void G_UNTERM_1_unterminated_quoted_through_eof()
    {
        var r = Golden("'Sales");
        var t = RealTokens(r)[0];
        Assert.Equal(K.QuotedIdentifierToken, t.Kind);
        Assert.False(t.IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedQuotedIdentifier, 0, 6));
    }

    [Fact]
    public void G_UNTERM_2_quoted_ends_before_crlf()
    {
        string src = "'Sales" + Cu(0x0D, 0x0A) + "[Amount]";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.QuotedIdentifierToken, K.BracketedIdentifierToken);
        Assert.False(RealTokens(r)[0].IsTerminated);
        Assert.True(RealTokens(r)[1].IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedQuotedIdentifier, 0, 6));
        Assert.Contains(AllTrivia(r), t => t.Kind == TK.EndOfLineTrivia); // CRLF survives as trivia
    }

    [Fact]
    public void G_UNTERM_3_unterminated_bracketed_through_eof()
    {
        var r = Golden("[Amount");
        var t = RealTokens(r)[0];
        Assert.Equal(K.BracketedIdentifierToken, t.Kind);
        Assert.False(t.IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedBracketedIdentifier, 0, 7));
    }

    [Fact]
    public void G_UNTERM_4_bracketed_ends_before_lf()
    {
        string src = "[Amount" + Cu(0x0A) + "[Next]";
        var r = Lex(src);
        AssertTiling(r, src);
        AssertDeterministic(src);
        AssertKinds(r, K.BracketedIdentifierToken, K.BracketedIdentifierToken);
        Assert.False(RealTokens(r)[0].IsTerminated);
        Assert.True(RealTokens(r)[1].IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedBracketedIdentifier, 0, 7));
    }

    [Fact]
    public void G_UNTERM_5_unterminated_date_through_eof()
    {
        var r = Golden("dt\"2026");
        var t = RealTokens(r)[0];
        Assert.Equal(K.DateLiteralToken, t.Kind);
        Assert.False(t.IsTerminated);
        AssertDiags(r, (DaxDiagnosticCodes.UnterminatedDateLiteral, 0, 7));
    }
}
