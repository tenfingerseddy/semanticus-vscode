using Semanticus.Dax;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.LexHarness;

namespace Semanticus.Dax.Tests;

/// <summary>Dedicated §9.2 limit and §9.3 cancellation tests (A1 exit-gate row 7).</summary>
public class LimitAndCancelTests
{
    // ---------------- §9.2 candidate-length limit (DAXL1007) ----------------

    [Fact]
    public void CandidateLength_long_token_becomes_terminal_bad_DAXL1007()
    {
        var opts = new DaxLexOptions { MaximumCandidateLength = 8 };
        string src = new string('a', 20); // one 20-char identifier candidate > limit 8
        var r = Lex(src, opts);
        AssertTiling(r, src);

        AssertDiags(r, (DaxDiagnosticCodes.CandidateLengthExceeded, 0, 20));
        var real = RealTokens(r);
        Assert.Single(real);
        Assert.Equal(DaxTokenKind.BadToken, real[0].Kind);
        Assert.Equal(new TextSpan(0, 20), real[0].Span); // terminal spans the whole remainder
        Assert.Equal(src.Length, Eof(r).Span.Start);
    }

    [Fact]
    public void CandidateLength_remainder_is_failed_closed()
    {
        var opts = new DaxLexOptions { MaximumCandidateLength = 5 };
        string src = "aaaaaaaaaaaa+1+2"; // long ident then valid DAX; the tail must NOT be interpreted
        var r = Lex(src, opts);
        AssertTiling(r, src);

        AssertDiags(r, (DaxDiagnosticCodes.CandidateLengthExceeded, 0, src.Length));
        var real = RealTokens(r);
        Assert.Single(real);
        Assert.Equal(DaxTokenKind.BadToken, real[0].Kind);
        Assert.Equal(src, real[0].Text); // remainder swallowed whole (fail-closed, spec §9.2)
    }

    [Fact]
    public void CandidateLength_supersedes_unterminated_diagnostic()
    {
        var opts = new DaxLexOptions { MaximumCandidateLength = 5 };
        string src = "\"" + new string('x', 20); // unterminated string, but too long -> DAXL1007 wins, not DAXL1002
        var r = Lex(src, opts);
        AssertTiling(r, src);
        AssertDiags(r, (DaxDiagnosticCodes.CandidateLengthExceeded, 0, src.Length));
    }

    // ---------------- §9.2 the inclusive-maximum boundary ----------------

    // spec §9.2: MaximumCandidateLength is an INCLUSIVE maximum — a candidate of exactly the maximum is
    // accepted; the first length that recovers is maximum + 1. The pair below pins both sides of that single
    // unit for every scanner shape, so the boundary can never drift by one in either direction.
    private const int Max = 64;

    private static string Shape(string shape, int length) => shape switch
    {
        "identifier" => new string('a', length),
        "number" => new string('1', length),
        "whitespace" => new string(' ', length),
        "string" => "\"" + new string('x', length - 2) + "\"",
        "quoted-name" => "'" + new string('x', length - 2) + "'",
        "bracketed-name" => "[" + new string('x', length - 2) + "]",
        "line-comment" => "//" + new string('x', length - 2),
        "delimited-comment" => "/*" + new string('x', length - 4) + "*/",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown candidate shape"),
    };

    [Theory]
    [InlineData("identifier")]
    [InlineData("number")]
    [InlineData("whitespace")]
    [InlineData("string")]
    [InlineData("quoted-name")]
    [InlineData("bracketed-name")]
    [InlineData("line-comment")]
    [InlineData("delimited-comment")]
    public void A_candidate_of_exactly_the_maximum_length_is_accepted(string shape)
    {
        string src = Shape(shape, Max);
        Assert.Equal(Max, src.Length); // the case is only meaningful if it sits exactly ON the boundary
        var r = Lex(src, new DaxLexOptions { MaximumCandidateLength = Max });
        AssertTiling(r, src);
        Assert.Empty(r.Diagnostics);                       // no DAXL1007 at exactly the maximum
        Assert.DoesNotContain(RealTokens(r), t => t.Kind == DaxTokenKind.BadToken);
    }

    [Theory]
    [InlineData("identifier")]
    [InlineData("number")]
    [InlineData("whitespace")]
    [InlineData("string")]
    [InlineData("quoted-name")]
    [InlineData("bracketed-name")]
    [InlineData("line-comment")]
    [InlineData("delimited-comment")]
    public void A_candidate_one_unit_over_the_maximum_triggers_terminal_recovery(string shape)
    {
        string src = Shape(shape, Max + 1);
        Assert.Equal(Max + 1, src.Length);
        var r = Lex(src, new DaxLexOptions { MaximumCandidateLength = Max });
        AssertTiling(r, src);
        AssertDiags(r, (DaxDiagnosticCodes.CandidateLengthExceeded, 0, src.Length));
        var real = RealTokens(r);
        Assert.Single(real);
        Assert.Equal(DaxTokenKind.BadToken, real[0].Kind);
        Assert.Equal(new TextSpan(0, src.Length), real[0].Span);
        Assert.True(real[0].IsTerminalRecovery);
    }

    // ---------------- §9.2 element-count limit (DAXL1008) ----------------

    [Fact]
    public void ElementCount_limit_emits_terminal_bad_DAXL1008()
    {
        var opts = new DaxLexOptions { MaximumLexicalElementCount = 3 };
        string src = "1 2 3 4 5"; // elements: int, ws, int, ... limit hit after 3 elements
        var r = Lex(src, opts);
        AssertTiling(r, src);

        AssertDiags(r, (DaxDiagnosticCodes.ElementCountExceeded, 3, src.Length - 3));
        var real = RealTokens(r);
        Assert.Equal(DaxTokenKind.BadToken, real[^1].Kind);     // last real token is the terminal remainder
        Assert.Equal(src.Length, real[^1].Span.End);            // it runs to the end of source
        Assert.Equal(src.Length, Eof(r).Span.Start);
    }

    [Fact]
    public void ElementCount_zero_swallows_everything()
    {
        var opts = new DaxLexOptions { MaximumLexicalElementCount = 0 };
        string src = "SUM(1)";
        var r = Lex(src, opts);
        AssertTiling(r, src);
        AssertDiags(r, (DaxDiagnosticCodes.ElementCountExceeded, 0, src.Length));
        var real = RealTokens(r);
        Assert.Single(real);
        Assert.Equal(src, real[0].Text);
    }

    [Fact]
    public void Default_limits_do_not_fire_on_ordinary_input()
    {
        var r = Lex("EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total])");
        AssertTiling(r, "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Sales\", [Total])");
        Assert.Empty(r.Diagnostics);
    }

    // ---------------- §9.3 cancellation ----------------

    [Fact]
    public void Cancellation_before_beginning_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DaxLexer.Lex(DaxSourceText.From("SUM(1) + [Amount] * 2"), null, cts.Token));
    }

    [Fact]
    public void Cancellation_on_empty_input_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DaxLexer.Lex(DaxSourceText.From(""), null, cts.Token));
    }

    [Fact]
    public void Cancellation_on_large_input_throws()
    {
        // A single very long candidate exercises the same cancellation observance; a pre-canceled token must
        // be honored regardless of input size (spec §9.3).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string big = new string('a', 5_000_000);
        Assert.Throws<OperationCanceledException>(() =>
            DaxLexer.Lex(DaxSourceText.From(big), null, cts.Token));
    }

    [Fact]
    public void NoCancellation_completes_normally()
    {
        var r = Lex("A + B");
        AssertTiling(r, "A + B");
        Assert.Empty(r.Diagnostics);
    }
}
