using Semanticus.Dax;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.LexHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Spec §9.3 / §11.1 F-CANCEL: the lexer exposes an internal, test-only cancellation-checkpoint observer,
/// invoked immediately before each cancellation-token check with the current absolute UTF-16 scan position.
/// These are the two mandated tests — the polling-bound proof and the cancel-from-the-observer proof — that
/// timing-based or already-canceled tests cannot supply. The seam is internal (see InternalsVisibleTo) and
/// has zero effect on public output when unset.
///
/// The observer is also the ONLY way to prove the §9.2 candidate-length limit actually limits WORK, so the
/// boundary-stop tests live here too: final-output assertions cannot distinguish a lexer that stops at the
/// limit from one that scans a 200,000-unit candidate to the end and rejects it afterwards.
/// </summary>
public class CancellationCheckpointTests
{
    // Each candidate is one lexical element well over 4,096 UTF-16 units, terminated so it is well-formed.
    public static IEnumerable<object[]> LongCandidates()
    {
        const int n = 10_000;
        yield return new object[] { "identifier", new string('x', n) };
        yield return new object[] { "number", new string('1', n) };
        yield return new object[] { "string", "\"" + new string('x', n) + "\"" };
        yield return new object[] { "quoted-name", "'" + new string('x', n) + "'" };
        yield return new object[] { "bracketed-name", "[" + new string('x', n) + "]" };
        yield return new object[] { "date", "dt\"" + new string('0', n) + "\"" };
        yield return new object[] { "whitespace", new string(' ', n) };
        yield return new object[] { "delimited-comment", "/*" + new string('x', n) + "*/" };
    }

    /// <summary>
    /// The shapes a single-unit-body test cannot police: every one of these scanners consumes TWO units per
    /// step (an escape pair, or a comment delimiter). Their bodies start at an odd offset, so under a poll
    /// that fired only on landing exactly on a multiple of 4,096 no even position was ever examined and no
    /// checkpoint EVER fired inside the candidate — one 10,000-unit gap, silently unbounded.
    /// </summary>
    public static IEnumerable<object[]> TwoUnitAdvanceCandidates()
    {
        const int pairs = 5_000; // 10,000 body units, every one of them consumed two at a time
        yield return new object[] { "escaped-quote string", "\"" + Repeat("\"\"", pairs) + "\"" };
        yield return new object[] { "escaped-apostrophe quoted-name", "'" + Repeat("''", pairs) + "'" };
        yield return new object[] { "escaped-bracket bracketed-name", "[" + Repeat("]]", pairs) + "]" };
        // Offset by a leading token so the comment body starts at an odd position, then open/close nested
        // comments — the depth counter advances two units on every /* and every */.
        yield return new object[] { "offset nested comment", "a/*" + Repeat("/**/", pairs / 2) + "*/" };
    }

    /// <summary>
    /// Numeric candidates that CONSUME structural units between digit runs: the accepted decimal point, the
    /// exponent marker, and the optional exponent sign. A digits-only candidate structurally cannot catch a
    /// skip there, because those units are traversed outside the digit loop — the decimal point by a bare
    /// <c>p++</c> and the exponent marker/sign by the <c>look</c> lookahead. Each case below places the
    /// transition exactly on the 4,096th unit, which is the only offset that turns the skip into a breach.
    /// </summary>
    public static IEnumerable<object[]> NumericTransitionCandidates()
    {
        const int run = 4096;
        // Minimal repros: the transition IS the boundary unit, so the candidate need only reach just past it.
        yield return new object[] { "decimal point on the boundary", new string('1', run) + ".0", 4096 };
        yield return new object[] { "exponent marker on the boundary", new string('1', run) + "e+0", 4096 };
        yield return new object[] { "exponent marker, unsigned", new string('1', run) + "e0", 4096 };
        // ...and the same transitions inside candidates long enough to poll repeatedly afterwards.
        yield return new object[] { "long decimal across the boundary", new string('1', run) + "." + new string('0', 6000), 8192 };
        yield return new object[] { "long scientific across the boundary", new string('1', run) + "e+" + new string('0', 6000), 8192 };
    }

    // (a) Within each long candidate, consecutive checkpoint positions differ by <= 4,096 (spec §9.3, §11.1).
    [Theory]
    [MemberData(nameof(LongCandidates))]
    public void Checkpoint_positions_within_a_long_candidate_stay_within_4096(string name, string src)
        => AssertPollingBound(name, src);

    // (a2) The same bound, for scanners that advance two units at a time (spec §9.3: the bound is measured
    //      between consecutive checkpoints, not between multiples of 4,096).
    [Theory]
    [MemberData(nameof(TwoUnitAdvanceCandidates))]
    public void Checkpoint_bound_holds_when_the_scanner_advances_two_units_at_a_time(string name, string src)
        => AssertPollingBound(name, src);

    // (a3) The same bound across the numeric structural transitions, which no digit run can reach.
    [Theory]
    [MemberData(nameof(NumericTransitionCandidates))]
    public void Checkpoint_bound_holds_across_numeric_transitions(string name, string src, int minimumTop)
        => AssertPollingBound(name, src, minimumTop);

    private static void AssertPollingBound(string name, string src, int minimumTop = 8192)
    {
        var positions = new List<int>();
        var r = DaxLexer.LexCore(DaxSourceText.From(src), null, default, positions.Add);

        Assert.Empty(r.Diagnostics);                        // each candidate is well-formed + terminated
        AssertTiling(r, src);
        // The candidate is long enough that intra-candidate polling actually fired (not a vacuous pass).
        Assert.True(positions.Count >= 3, $"{name}: too few checkpoints ({positions.Count})");
        Assert.True(positions.Max() >= minimumTop, $"{name}: candidate did not exercise the bound (max {positions.Max()})");

        for (int i = 1; i < positions.Count; i++)
        {
            int gap = positions[i] - positions[i - 1];
            Assert.True(gap >= 0 && gap <= 4096,
                $"{name}: checkpoint gap {positions[i - 1]}->{positions[i]} ({gap}) exceeds the 4096-unit bound");
        }
    }

    // (b) Cancelling from inside the observer throws OperationCanceledException at that exact checkpoint,
    //     before another lexical element is emitted (spec §9.3, §11.1).
    [Fact]
    public void Cancel_from_the_observer_throws_at_that_checkpoint_before_the_next_element()
    {
        // One long whitespace candidate: cancel at the first intra-candidate poll (4096), well before the
        // candidate would complete at 10,000 — so the whitespace trivia is never emitted.
        string src = new string(' ', 10_000);
        using var cts = new CancellationTokenSource();
        var positions = new List<int>();
        Action<int> observer = pos =>
        {
            positions.Add(pos);
            if (pos >= 4096) cts.Cancel(); // request cancellation from inside the observer
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxLexer.LexCore(DaxSourceText.From(src), null, cts.Token, observer));

        Assert.Equal(4096, positions[^1]);               // threw AT the cancel checkpoint...
        Assert.DoesNotContain(positions, p => p > 4096); // ...nothing scanned or emitted past it
        Assert.True(4096 < src.Length);                  // and the candidate was mid-scan, not completed
    }

    // (b2) The same proof inside a two-unit-advance candidate: cancellation must be honored mid-escape-run,
    //      not only after the whole 10,002-unit string has been scanned.
    [Fact]
    public void Cancel_from_the_observer_inside_an_escaped_quote_string()
    {
        string src = "\"" + Repeat("\"\"", 5_000) + "\"";
        using var cts = new CancellationTokenSource();
        var positions = new List<int>();
        Action<int> observer = pos =>
        {
            positions.Add(pos);
            if (pos >= 4096) cts.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() =>
            DaxLexer.LexCore(DaxSourceText.From(src), null, cts.Token, observer));

        Assert.Equal(4096, positions[^1]);
        Assert.DoesNotContain(positions, p => p > 4096);
        Assert.True(4096 < src.Length / 2);
    }

    [Fact]
    public void Checkpoint_positions_are_pinned_for_an_off_grid_candidate()
    {
        // Polling is DISTANCE-based (from the previous checkpoint), not grid-based (multiples of 4,096), so a
        // candidate that does not begin on the grid does not poll on the grid either. Here the '+' token moves
        // the identifier's start to 1: the mandatory per-element Checkpoint(1) becomes the origin and the
        // interior checkpoints land at 4097 and 8193 — NOT the 4096 and 8192 a grid-based poll produced. That
        // is a deliberate consequence of the §9.3 fix (the bound is between consecutive checkpoints and is
        // still exactly 4,096 here), and public output is unaffected. Pinned so it cannot drift unnoticed.
        string src = "+" + new string('x', 10_000);
        var positions = new List<int>();
        var r = DaxLexer.LexCore(DaxSourceText.From(src), null, default, positions.Add);

        AssertTiling(r, src);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(new[] { 0, 0, 1, 4097, 8193, 10001 }, positions);
        for (int i = 1; i < positions.Count; i++)
            Assert.True(positions[i] - positions[i - 1] <= 4096, "off-grid polling still respects the bound");
    }

    // ---------------- §9.2: the candidate-length limit must limit WORK, not just the output ----------------

    private const int OversizeLimit = 10_000;
    private const int OversizeBody = 200_000; // 20x the limit: an unbounded scan is unmistakable

    /// <summary>One oversized candidate per scanner, each starting at position 0.</summary>
    public static IEnumerable<object[]> OversizedCandidates()
    {
        string body = new string('x', OversizeBody);
        yield return new object[] { "identifier", body };
        yield return new object[] { "number", new string('1', OversizeBody) };
        yield return new object[] { "whitespace", new string(' ', OversizeBody) };
        yield return new object[] { "string", "\"" + body };
        yield return new object[] { "date", "dt\"" + new string('0', OversizeBody) };
        yield return new object[] { "quoted-name", "'" + body };
        yield return new object[] { "bracketed-name", "[" + body };
        yield return new object[] { "line-comment", "//" + body };
        yield return new object[] { "doc-comment", "///" + body };
        yield return new object[] { "dash-comment", "--" + body };
        yield return new object[] { "delimited-comment", "/*" + body };
        yield return new object[] { "escaped-quote string", "\"" + Repeat("\"\"", OversizeBody / 2) };
        yield return new object[] { "nested-comment", "/*" + Repeat("/**/", OversizeBody / 4) };
    }

    [Theory]
    [MemberData(nameof(OversizedCandidates))]
    public void Candidate_length_limit_stops_the_scan_at_the_boundary(string name, string src)
    {
        var opts = new DaxLexOptions { MaximumCandidateLength = OversizeLimit };
        var positions = new List<int>();
        var r = DaxLexer.LexCore(DaxSourceText.From(src), opts, default, positions.Add);

        // 1. The §9.2 final output is exactly what it was before the limit became a work bound.
        AssertTiling(r, src);
        AssertDiags(r, (DaxDiagnosticCodes.CandidateLengthExceeded, 0, src.Length));
        var real = RealTokens(r);
        Assert.Single(real);
        Assert.Equal(DaxTokenKind.BadToken, real[0].Kind);
        Assert.Equal(new TextSpan(0, src.Length), real[0].Span);
        Assert.Equal(src.Length, Eof(r).Span.Start);

        // 2. ...and the scanner never worked past the boundary. Without an in-scan limit the checkpoints run
        //    to ~196,608 here; with it they stop one unit past the limit at the latest.
        Assert.True(positions.Max() <= OversizeLimit + 1,
            $"{name}: scanning ran to {positions.Max()}, past the {OversizeLimit}-unit candidate limit");
    }

    [Fact]
    public void Terminal_recovery_tokens_are_internally_identifiable()
    {
        // spec §9.2: "The terminal remainder token ... MUST be identifiable internally for diagnostics but
        // does not require a public token kind." The A2 parser needs this to stop rather than parse the
        // unscanned remainder as ordinary DAX, without correlating DAXL1007/DAXL1008 by code and span.
        var byLength = Lex(new string('a', 64), new DaxLexOptions { MaximumCandidateLength = 8 });
        Assert.True(RealTokens(byLength).Single().IsTerminalRecovery);

        var byCount = Lex("1 2 3 4 5", new DaxLexOptions { MaximumLexicalElementCount = 3 });
        Assert.True(RealTokens(byCount)[^1].IsTerminalRecovery);
        Assert.All(RealTokens(byCount)[..^1], t => Assert.False(t.IsTerminalRecovery));

        // An ordinary BadToken is NOT terminal recovery, and neither is EOF.
        var ordinary = Lex("#$%");
        Assert.All(RealTokens(ordinary), t => Assert.False(t.IsTerminalRecovery));
        Assert.False(Eof(ordinary).IsTerminalRecovery);
    }
}
