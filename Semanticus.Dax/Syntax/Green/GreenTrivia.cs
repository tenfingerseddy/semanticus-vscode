namespace Semanticus.Dax.Syntax.Green;

/// <summary>
/// A green trivia item. Ordinary trivia carries raw text. STRUCTURED trivia
/// (<see cref="SyntaxKind.SkippedTokensTrivia"/>) carries a <see cref="GreenNode"/> instead: the
/// <c>SkippedTokens</c> node holding the original tokens with their own trivia, in source order
/// (A0 §7.4, §11.3). Its text is the structure's full text, so re-hosting a token as skipped source
/// cannot change a single code unit and round-trip survives recovery.
/// </summary>
internal sealed class GreenTrivia : GreenNode
{
    private readonly string? _text;

    internal GreenTrivia(SyntaxKind kind, string text, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : base(kind, diagnostics)
    {
        _text = text;
        FullWidth = text.Length;
        Flags = FlagsFor(diagnostics);
    }

    /// <summary>Structured trivia. The text is derived from the structure and is never stored twice.</summary>
    internal GreenTrivia(SyntaxKind kind, GreenNode structure, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : base(kind, diagnostics)
    {
        Structure = structure;
        FullWidth = structure.FullWidth;
        Flags = FlagsFor(diagnostics) | structure.Flags | GreenFlags.StructuredTrivia;
        if (kind == SyntaxKind.SkippedTokensTrivia) Flags |= GreenFlags.Skipped;
    }

    internal GreenNode? Structure { get; }

    internal bool HasStructure => Structure is not null;

    internal string Text => _text ?? Structure!.ToFullString();

    internal override bool IsTrivia => true;
    internal override int SlotCount => 0;
    internal override GreenNode? GetSlot(int index) => null;

    internal override GreenNode WithAdditionalDiagnostics(params DaxDiagnostic[] diagnostics)
        => Structure is not null
            ? new GreenTrivia(Kind, Structure, Concat(Diagnostics, diagnostics))
            : new GreenTrivia(Kind, _text!, Concat(Diagnostics, diagnostics));
}
