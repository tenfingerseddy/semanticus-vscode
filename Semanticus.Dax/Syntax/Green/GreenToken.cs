namespace Semanticus.Dax.Syntax.Green;

/// <summary>
/// A green token. It stores its RAW TEXT as a plain string, never a <c>TextSpan</c> into the source:
/// green is positionless (A0 §3.1), and a span would silently re-introduce a position.
///
/// <para>
/// A missing token (A0 §11.2) has empty <see cref="Text"/>, zero width, and <see cref="IsMissing"/> true.
/// It is a real, enumerated element of the tree. That is the opposite of an ABSENT optional token, which
/// is a null green slot and is never enumerated at all (A2 §5.6).
/// </para>
/// </summary>
internal sealed class GreenToken : GreenNode
{
    private static readonly GreenTrivia[] NoTrivia = Array.Empty<GreenTrivia>();

    internal GreenToken(
        SyntaxKind kind,
        string text,
        string valueText,
        bool isMissing = false,
        IReadOnlyList<GreenTrivia>? leadingTrivia = null,
        IReadOnlyList<GreenTrivia>? trailingTrivia = null,
        IReadOnlyList<DaxDiagnostic>? diagnostics = null,
        Action? onTriviaVisit = null)
        : base(kind, diagnostics)
    {
        Text = text;
        ValueText = valueText;
        IsMissing = isMissing;
        LeadingTrivia = leadingTrivia ?? NoTrivia;
        TrailingTrivia = trailingTrivia ?? NoTrivia;

        var flags = FlagsFor(diagnostics);
        if (isMissing) flags |= GreenFlags.Missing;
        if (kind == SyntaxKind.BadToken) flags |= GreenFlags.BadToken;

        var width = text.Length;
        foreach (var trivia in LeadingTrivia)
        {
            width += trivia.FullWidth;
            flags |= trivia.Flags;
            onTriviaVisit?.Invoke();
        }
        LeadingTriviaWidth = width - text.Length;
        foreach (var trivia in TrailingTrivia)
        {
            width += trivia.FullWidth;
            flags |= trivia.Flags;
            onTriviaVisit?.Invoke();
        }
        TrailingTriviaWidth = width - text.Length - LeadingTriviaWidth;

        FullWidth = width;
        Flags = flags;
    }

    /// <summary>
    /// Same trivia, new diagnostics. Widths and trivia flags are copied so attaching a diagnostic does
    /// not rewalk every trivia item (the construction walk already accounted them).
    /// </summary>
    private GreenToken(GreenToken template, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(template.Kind, diagnostics)
    {
        Text = template.Text;
        ValueText = template.ValueText;
        IsMissing = template.IsMissing;
        LeadingTrivia = template.LeadingTrivia;
        TrailingTrivia = template.TrailingTrivia;
        LeadingTriviaWidth = template.LeadingTriviaWidth;
        TrailingTriviaWidth = template.TrailingTriviaWidth;
        FullWidth = template.FullWidth;
        var flags = template.Flags & ~GreenFlags.Diagnostics;
        Flags = flags | FlagsFor(diagnostics);
    }

    /// <summary>Exact source spelling (A0 §5.3). Empty for a missing token and for EOF.</summary>
    internal string Text { get; }

    /// <summary>Decoded convenience text (A0 §5.3). Never replaces <see cref="Text"/> in the tree.</summary>
    internal string ValueText { get; }

    internal bool IsMissing { get; }

    internal IReadOnlyList<GreenTrivia> LeadingTrivia { get; }
    internal IReadOnlyList<GreenTrivia> TrailingTrivia { get; }

    internal int LeadingTriviaWidth { get; }
    internal int TrailingTriviaWidth { get; }

    internal override bool IsToken => true;
    internal override int SlotCount => 0;
    internal override GreenNode? GetSlot(int index) => null;

    internal override GreenNode WithAdditionalDiagnostics(params DaxDiagnostic[] diagnostics)
        => new GreenToken(this, Concat(Diagnostics, diagnostics));

    internal GreenToken WithTrivia(IReadOnlyList<GreenTrivia>? leading, IReadOnlyList<GreenTrivia>? trailing, Action? onTriviaVisit = null)
        => new(Kind, Text, ValueText, IsMissing, leading, trailing, Diagnostics, onTriviaVisit);
}
