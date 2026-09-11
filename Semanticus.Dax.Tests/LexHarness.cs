using System.Text;
using Semanticus.Dax;
using Semanticus.Dax.Text;
using Xunit;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Shared helpers: lexing, the §7.4 tiling-property assertions (applied to every golden AND every fuzz
/// input, per the task), and safe construction of special-character source (built from code units at
/// runtime so no source file needs literal control characters).
/// </summary>
internal static class LexHarness
{
    public static DaxLexResult Lex(string source, DaxLexOptions? options = null)
        => DaxLexer.Lex(DaxSourceText.From(source), options);

    /// <summary>Build a string from raw UTF-16 code units (supports BOM, controls, and lone surrogates).</summary>
    public static string Cu(params int[] codeUnits)
    {
        var chars = new char[codeUnits.Length];
        for (int i = 0; i < codeUnits.Length; i++) chars[i] = (char)codeUnits[i];
        return new string(chars);
    }

    public static DaxTokenKind[] TokenKinds(DaxLexResult r)
        => r.Tokens.Where(t => t.Kind != DaxTokenKind.EndOfFileToken).Select(t => t.Kind).ToArray();

    public static DaxToken[] RealTokens(DaxLexResult r)
        => r.Tokens.Where(t => t.Kind != DaxTokenKind.EndOfFileToken).ToArray();

    public static DaxToken Eof(DaxLexResult r) => r.Tokens[^1];

    public static string[] DiagCodes(DaxLexResult r) => r.Diagnostics.Select(d => d.Code).ToArray();

    /// <summary>
    /// Exact diagnostic assertion: code AND span, in order. Spec §10 requires goldens to assert diagnostic
    /// spans, not merely codes — a DAXL1003 reported over [0,0) satisfies a code-only assertion and an
    /// in-bounds check, but is wrong.
    /// </summary>
    public static void AssertDiags(DaxLexResult r, params (string Code, int Start, int Length)[] expected)
    {
        Assert.Equal(
            expected.Select(e => $"{e.Code}[{e.Start}..{e.Start + e.Length})").ToArray(),
            r.Diagnostics.Select(d => $"{d.Code}[{d.Span.Start}..{d.Span.End})").ToArray());
    }

    public static string Repeat(string unit, int count)
    {
        var sb = new StringBuilder(unit.Length * count);
        for (int i = 0; i < count; i++) sb.Append(unit);
        return sb.ToString();
    }

    /// <summary>Every trivia item in source order (leading then trailing, across all tokens).</summary>
    public static List<DaxTrivia> AllTrivia(DaxLexResult r)
    {
        var list = new List<DaxTrivia>();
        foreach (var t in r.Tokens)
        {
            list.AddRange(t.LeadingTrivia);
            list.AddRange(t.TrailingTrivia);
        }
        return list;
    }

    // ---- §7.4 mechanically-testable tiling properties (asserted on every golden + every fuzz input) ----

    public static void AssertTiling(DaxLexResult r, string source)
    {
        // Ordered flat walk: leading trivia, token, trailing trivia — for every token in stream order.
        var spans = new List<TextSpan>();
        int eofCount = 0;
        foreach (var t in r.Tokens)
        {
            foreach (var lt in t.LeadingTrivia)
            {
                Assert.True(lt.Span.Length > 0, "F-PROGRESS: trivia must have positive width."); // §7.4(4)
                spans.Add(lt.Span);
            }

            if (t.Kind == DaxTokenKind.EndOfFileToken)
            {
                eofCount++;
                Assert.Equal(0, t.Span.Length);                 // §7.4(4): only EOF is zero width
                Assert.Equal(source.Length, t.Span.Start);      // §7.4(1): EOF is zero-width at S.Length
            }
            else
            {
                Assert.True(t.Span.Length > 0, "F-PROGRESS: non-EOF token must have positive width."); // §7.4(4)
            }
            spans.Add(t.Span);

            foreach (var tt in t.TrailingTrivia)
            {
                Assert.True(tt.Span.Length > 0, "F-PROGRESS: trivia must have positive width.");
                spans.Add(tt.Span);
            }
        }

        Assert.Equal(1, eofCount); // exactly one EOF, and (by construction) it is last
        Assert.Equal(DaxTokenKind.EndOfFileToken, r.Tokens[^1].Kind);

        // §7.4(1) Partition: contiguous, non-overlapping, start 0, end S.Length.
        int cursor = 0;
        foreach (var s in spans)
        {
            Assert.Equal(cursor, s.Start);
            cursor = s.End;
        }
        Assert.Equal(source.Length, cursor);

        // §7.4(2) Reconstruction: concatenated raw slices == S (ordinal).
        var sb = new StringBuilder(source.Length);
        foreach (var s in spans) sb.Append(source.Substring(s.Start, s.Length));
        Assert.Equal(source, sb.ToString(), ignoreCase: false, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false);

        // Raw Text on tokens/trivia is the source slice (spec §8.1) — proves raw is source-backed (§7.4(3)).
        foreach (var t in r.Tokens)
        {
            Assert.Equal(source.Substring(t.Span.Start, t.Span.Length), t.Text);
            foreach (var tr in t.LeadingTrivia) Assert.Equal(source.Substring(tr.Span.Start, tr.Span.Length), tr.Text);
            foreach (var tr in t.TrailingTrivia) Assert.Equal(source.Substring(tr.Span.Start, tr.Span.Length), tr.Text);
        }

        // Diagnostics point inside [0, S.Length] and are in nondecreasing Span.Start order (spec §7.3).
        for (int i = 0; i < r.Diagnostics.Count; i++)
        {
            var d = r.Diagnostics[i];
            Assert.True(d.Span.Start >= 0 && d.Span.End <= source.Length, "diagnostic span out of bounds");
            Assert.True(d.Span.Length >= 0, "diagnostic span has negative length");
            if (i > 0)
                Assert.True(d.Span.Start >= r.Diagnostics[i - 1].Span.Start,
                    $"§7.3: diagnostics must be in nondecreasing Span.Start order ({r.Diagnostics[i - 1].Span.Start} then {d.Span.Start})");
        }

        // §7.4 F-SURROGATE: no partition boundary splits a valid surrogate pair.
        AssertNoSplitPairs(spans, source);
    }

    private static void AssertNoSplitPairs(List<TextSpan> spans, string source)
    {
        // A span slice is always whole, so a pair can only be split at a boundary BETWEEN spans. Collect the
        // boundary positions and assert none falls between a high surrogate and its following low surrogate.
        var boundaries = new HashSet<int>();
        foreach (var s in spans) { boundaries.Add(s.Start); boundaries.Add(s.End); }
        foreach (int b in boundaries)
        {
            if (b > 0 && b < source.Length && char.IsHighSurrogate(source[b - 1]) && char.IsLowSurrogate(source[b]))
                Assert.Fail($"F-SURROGATE: boundary at {b} splits a valid surrogate pair.");
        }
    }

    /// <summary>§7.4(5) F-DETERMINISM: a second lex yields identical observable output.</summary>
    public static void AssertDeterministic(string source, DaxLexOptions? options = null)
    {
        var a = Lex(source, options);
        var b = Lex(source, options);
        Assert.Equal(Serialize(a), Serialize(b));
    }

    public static string Serialize(DaxLexResult r)
    {
        var sb = new StringBuilder();
        foreach (var t in r.Tokens)
        {
            sb.Append("TOK ").Append(t.Kind).Append(' ').Append(t.Span).Append(" term=").Append(t.IsTerminated)
              .Append(" val=<").Append(t.ValueText).Append(">\n");
            foreach (var lt in t.LeadingTrivia) sb.Append("  L ").Append(lt.Kind).Append(' ').Append(lt.Span).Append(" term=").Append(lt.IsTerminated).Append('\n');
            foreach (var tt in t.TrailingTrivia) sb.Append("  T ").Append(tt.Kind).Append(' ').Append(tt.Span).Append(" term=").Append(tt.IsTerminated).Append('\n');
        }
        foreach (var d in r.Diagnostics)
            sb.Append("DIAG ").Append(d.Code).Append(' ').Append(d.Span).Append(' ').Append(d.Severity).Append(" msg=<").Append(d.Message).Append(">\n");
        return sb.ToString();
    }

    /// <summary>Combined golden invariant: tiling + determinism, run on every §10 case.</summary>
    public static DaxLexResult Golden(string source)
    {
        var r = Lex(source);
        AssertTiling(r, source);
        AssertDeterministic(source);
        return r;
    }
}
