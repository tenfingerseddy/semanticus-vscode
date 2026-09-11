using Semanticus.Dax;
using Xunit;
using static Semanticus.Dax.Tests.LexHarness;
using K = Semanticus.Dax.DaxTokenKind;
using TK = Semanticus.Dax.DaxTriviaKind;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Spec §6.6 leading/trailing ownership, asserted EXPLICITLY.
///
/// The tiling harness cannot police §6.6 at all: trailing trivia of the preceding token and leading trivia
/// of the following token occupy the same flat position, so source reconstruction is byte-identical either
/// way. Every ownership bug is invisible to tiling — which is exactly how a multi-line delimited comment came
/// to be treated as containing no line break. These tests name the owner of every trivia item.
/// </summary>
public class TriviaOwnershipTests
{
    /// <summary>Whole-stream ownership rendering: <c>&lt;lead&gt;[TOKEN]&lt;trail&gt;</c> per token, space-joined.</summary>
    private static string Ownership(string src)
    {
        var r = Lex(src);
        AssertTiling(r, src);  // ownership must never come at the cost of the flat partition
        AssertDeterministic(src);
        var parts = new List<string>();
        foreach (var t in r.Tokens)
        {
            var lead = string.Join("", t.LeadingTrivia.Select(x => $"<{Abbrev(x.Kind)}:{Esc(x.Text)}>"));
            var trail = string.Join("", t.TrailingTrivia.Select(x => $"<{Abbrev(x.Kind)}:{Esc(x.Text)}>"));
            parts.Add($"{lead}[{(t.Kind == K.EndOfFileToken ? "EOF" : Esc(t.Text))}]{trail}");
        }
        return string.Join(" ", parts);
    }

    private static string Abbrev(TK k) => k switch
    {
        TK.WhitespaceTrivia => "ws",
        TK.EndOfLineTrivia => "eol",
        TK.ByteOrderMarkTrivia => "bom",
        TK.SlashSlashCommentTrivia => "//",
        TK.DashDashCommentTrivia => "--",
        TK.DocumentationCommentTrivia => "///",
        TK.DelimitedCommentTrivia => "/**/",
        _ => k.ToString(),
    };

    private static string Esc(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    // ---------------- the defect: a line break INSIDE a delimited comment ----------------

    [Fact]
    public void Multiline_delimited_comment_is_the_split_item_and_the_space_leads_the_next_token()
    {
        // spec §6.6 (ratified at trivia-ITEM granularity): the item that CONTAINS the first line break stays
        // trailing on 'a'; every item after it leads 'b'. Deciding by trivia KIND misses the break entirely —
        // one DelimitedCommentTrivia item is not an EndOfLineTrivia item — and wrongly trails the space too.
        Assert.Equal("[a]</**/:/*x\\n*/> <ws: >[b] [EOF]", Ownership("a/*x\n*/ b"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Every_line_ending_inside_a_delimited_comment_splits_attachment(string eol)
    {
        string src = $"a /*x{eol}y*/ b";
        var r = Lex(src);
        AssertTiling(r, src);
        var a = RealTokens(r)[0];
        var b = RealTokens(r)[1];
        Assert.Equal(new[] { TK.WhitespaceTrivia, TK.DelimitedCommentTrivia }, a.TrailingTrivia.Select(x => x.Kind).ToArray());
        Assert.Equal(new[] { TK.WhitespaceTrivia }, b.LeadingTrivia.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void Only_the_first_break_carrying_item_splits_the_run()
    {
        // Two multi-line comments: the FIRST is the split item; the second and everything after lead 'b'.
        Assert.Equal("[a]</**/:/*1\\n*/> </**/:/*2\\n*/><ws: >[b] [EOF]", Ownership("a/*1\n*//*2\n*/ b"));
    }

    [Fact]
    public void A_nested_multiline_comment_is_still_one_splitting_item()
    {
        Assert.Equal("[a]</**/:/* /* x\\n*/ */> <ws: >[b] [EOF]", Ownership("a/* /* x\n*/ */ b"));
    }

    [Fact]
    public void An_unterminated_multiline_comment_at_the_end_still_trails_the_last_token()
    {
        // Rule 6: final trivia trails the last real token, whether or not it holds a line break.
        Assert.Equal("[a]<ws: ></**/:/*x\\n> [EOF]", Ownership("a /*x\n"));
    }

    [Fact]
    public void An_initial_multiline_comment_leads_the_first_token_rule_4()
    {
        // Rule 4 is unconditional: initial trivia leads the first real token even though it holds a break.
        Assert.Equal("</**/:/*x\\n*/><ws: >[a] [EOF]", Ownership("/*x\n*/ a"));
    }

    // ---------------- the §6.6 worked examples, with ownership named ----------------

    [Fact]
    public void G_COM_6_no_line_break_means_everything_trails_the_preceding_token()
        => Assert.Equal("[a]<ws: ></**/:/* c */><ws: > [b] [EOF]", Ownership("a /* c */ b"));

    [Fact]
    public void G_COM_7_comment_then_crlf_trails_and_the_indent_leads()
        => Assert.Equal("[a]<ws: ><//:// c><eol:\\r\\n> <ws:  >[b] [EOF]", Ownership("a // c\r\n  b"));

    [Fact]
    public void G_COM_8_initial_trivia_leads_the_first_token()
        => Assert.Equal("</**/:/* c */><ws:  >[a] [EOF]", Ownership("/* c */  a"));

    [Fact]
    public void G_COM_9_final_trivia_trails_the_last_token()
        => Assert.Equal("[a]<ws:  ></**/:/* c */> [EOF]", Ownership("a  /* c */"));

    [Fact]
    public void Two_blank_lines_split_after_the_first_line_ending()
        => Assert.Equal("[a]<eol:\\n> <eol:\\n>[b] [EOF]", Ownership("a\n\nb"));

    [Fact]
    public void A_line_comment_never_owns_its_own_terminator()
    {
        // Rule 7: the comment stops before the newline, and the separate EndOfLineTrivia is the split item.
        Assert.Equal("[a]<--:-- c><eol:\\n> <ws: >[b] [EOF]", Ownership("a-- c\n b"));
    }

    [Fact]
    public void Bom_and_initial_trivia_lead_the_first_token()
        => Assert.Equal("<bom:\uFEFF><ws: >[a] [EOF]", Ownership(Cu(0xFEFF) + " a"));

    [Fact]
    public void With_no_real_token_every_item_leads_eof_rule_5()
        => Assert.Equal("<ws: ></**/:/*x\\n*/><eol:\\n>[EOF]", Ownership(" /*x\n*/\n"));

    [Fact]
    public void Rule_8_every_trivia_item_is_attached_exactly_once()
    {
        // A dense mixture: BOM, whitespace, all three line-comment kinds, a multi-line block comment and
        // several line endings. Every item must appear exactly once across the leading/trailing lists, in
        // source order (AssertTiling proves the partition; this proves ordering and no double-attachment).
        string src = Cu(0xFEFF) + " a // x\r\n-- y\n/// z\n /*p\nq*/ b\t/* r */\n";
        var r = Lex(src);
        AssertTiling(r, src);
        var starts = AllTrivia(r).Select(t => t.Span.Start).ToArray();
        Assert.Equal(starts.OrderBy(s => s).ToArray(), starts);
        Assert.Equal(starts.Distinct().Count(), starts.Length);
    }
}
