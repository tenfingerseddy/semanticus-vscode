using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax.Green;

/// <summary>
/// Construction helpers the parser uses to turn A1's flat lexical stream into green nodes. Everything
/// here is internal: green is not part of the public contract (A0 §3.1, §12.2).
/// </summary>
internal static class GreenFactory
{
    internal static readonly GreenNode?[] NoSlots = Array.Empty<GreenNode?>();

    internal static GreenTrivia Trivia(DaxTrivia trivia, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        => new(SyntaxKindFacts.FromTriviaKind(trivia.Kind), trivia.Text, diagnostics);

    /// <summary>
    /// A green token over an A1 token. Raw text is copied as a string because green is positionless
    /// (A0 §3.1) — storing the A1 <c>TextSpan</c> would smuggle a position into the green tree.
    /// </summary>
    internal static GreenToken Token(
        DaxToken token,
        IReadOnlyList<GreenTrivia>? leadingTrivia = null,
        IReadOnlyList<GreenTrivia>? trailingTrivia = null,
        IReadOnlyList<DaxDiagnostic>? diagnostics = null,
        Action? onTriviaVisit = null)
        => new(SyntaxKindFacts.FromTokenKind(token.Kind), token.Text, token.ValueText, false, leadingTrivia, trailingTrivia, diagnostics, onTriviaVisit);

    /// <summary>
    /// A required-but-absent token (A0 §11.2): empty text, zero width, <c>IsMissing == true</c>. It IS a
    /// tree element and IS enumerated, unlike an absent optional token, which is a null slot (A2 §5.6).
    /// </summary>
    internal static GreenToken MissingToken(SyntaxKind kind, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        => new(kind, string.Empty, string.Empty, true, null, null, diagnostics);

    internal static GreenTrivia SkippedTokensTrivia(IReadOnlyList<GreenToken> tokens, IReadOnlyList<DaxDiagnostic>? diagnostics = null, Action? onVisit = null)
        => new(SyntaxKind.SkippedTokensTrivia, new GreenSkippedTokens(List(tokens, onVisit)), diagnostics);

    internal static GreenSyntaxList List(IReadOnlyList<GreenNode?> children, Action? onVisit = null)
        => CopyList(children, onVisit);

    internal static GreenSyntaxList List(IReadOnlyList<GreenToken> tokens, Action? onVisit = null)
        => CopyList(tokens, onVisit);

    /// <summary>
    /// Interleaves items and separators into the alternating layout a separated list expects:
    /// item, separator, item, separator, item. Every separator and every element is retained (A0 §7.5),
    /// including a trailing separator with no following element.
    /// </summary>
    internal static GreenSyntaxList SeparatedList(IReadOnlyList<GreenNode> items, IReadOnlyList<GreenToken> separators, Action? onVisit = null)
    {
        if (items.Count == 0 && separators.Count == 0) return GreenSyntaxList.Empty;

        var slots = new GreenNode?[items.Count + separators.Count];
        int i = 0, s = 0, w = 0;
        while (i < items.Count || s < separators.Count)
        {
            if (i < items.Count)
            {
                slots[w++] = items[i++];
                onVisit?.Invoke();
            }
            if (s < separators.Count)
            {
                slots[w++] = separators[s++];
                onVisit?.Invoke();
            }
        }
        return new GreenSyntaxList(slots, diagnostics: null, onVisit);
    }

    private static GreenSyntaxList CopyList<T>(IReadOnlyList<T> children, Action? onVisit) where T : GreenNode?
    {
        if (children.Count == 0) return GreenSyntaxList.Empty;
        var slots = new GreenNode?[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            slots[i] = children[i];
            onVisit?.Invoke();
        }
        return new GreenSyntaxList(slots, diagnostics: null, onVisit);
    }
}

/// <summary>
/// A2 §8.1 rule 1: each <c>DaxLexResult.Diagnostics</c> entry attaches to the unique lexical element whose
/// RAW SPAN contains the diagnostic's <c>Span.Start</c>. A1 §7.4 properties 1 and 3 guarantee that element
/// exists and is unique, so there is no tie-break to invent. This index exists so green construction can
/// do that lookup once, per element, in source order — and so the conservation invariant (§8.1 rule 6)
/// has exactly one implementation to be right or wrong about.
/// </summary>
internal sealed class LexicalDiagnosticIndex
{
    private readonly IReadOnlyList<DaxDiagnostic> _diagnostics;
    private readonly bool[] _claimed;
    private int _cursor;      // first index that is still unclaimed
    private int _remaining;

    internal LexicalDiagnosticIndex(DaxLexResult lexResult)
    {
        _diagnostics = lexResult.Diagnostics;
        _claimed = new bool[_diagnostics.Count];
        _remaining = _diagnostics.Count;
    }

    /// <summary>
    /// The diagnostics owned by the element occupying <paramref name="rawSpan"/>, or null when none.
    /// A diagnostic can be claimed only once, whatever order elements are visited in, so "exactly once"
    /// (A2 §8.1 rule 3) is enforced here rather than trusted. The lexer emits in source order (A1 §7.3),
    /// so the cursor makes the ordinary in-order walk cheap.
    /// </summary>
    internal IReadOnlyList<DaxDiagnostic>? Take(TextSpan rawSpan)
    {
        if (_remaining == 0) return null;

        List<DaxDiagnostic>? owned = null;
        for (var i = _cursor; i < _diagnostics.Count; i++)
        {
            if (_claimed[i]) continue;

            var start = _diagnostics[i].Span.Start;
            if (start >= rawSpan.End && rawSpan.Length != 0) break;   // source-ordered: nothing later can match

            // A zero-width element (EOF, a missing token) owns only a zero-width diagnostic at its position.
            var contains = rawSpan.Length == 0 ? start == rawSpan.Start : rawSpan.Contains(start);
            if (!contains) continue;

            _claimed[i] = true;
            _remaining--;
            (owned ??= new List<DaxDiagnostic>()).Add(_diagnostics[i]);
        }

        while (_cursor < _claimed.Length && _claimed[_cursor]) _cursor++;
        return owned;
    }

    /// <summary>Diagnostics not yet claimed by any element. Must be zero once construction finishes.</summary>
    internal int UnclaimedCount => _remaining;
}
