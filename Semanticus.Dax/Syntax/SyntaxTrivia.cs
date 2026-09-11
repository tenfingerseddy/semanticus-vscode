using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// One trivia item owned by a token (A0 §7.4). Only <see cref="SyntaxKind.SkippedTokensTrivia"/> has
/// structure: its structured node holds the original skipped tokens in source order, with their own
/// trivia. Structured-trivia tokens are NOT emitted by the normal surface-token walk; a consumer asks for
/// them explicitly with <c>descendIntoStructuredTrivia: true</c>.
/// </summary>
public readonly struct SyntaxTrivia : IEquatable<SyntaxTrivia>
{
    internal readonly GreenTrivia? Green;
    private readonly DaxSyntaxTree? _tree;

    internal SyntaxTrivia(GreenTrivia? green, SyntaxToken token, int position, DaxSyntaxTree? tree)
    {
        Green = green;
        Token = token;
        Position = position;
        _tree = tree;
    }

    public SyntaxKind Kind => Green?.Kind ?? SyntaxKind.None;

    /// <summary>The token that owns this trivia (A0 §5.2 gives trivia to tokens, never to productions).</summary>
    public SyntaxToken Token { get; }

    public int Position { get; }

    public TextSpan Span => new(Position, Green?.FullWidth ?? 0);

    /// <summary>Trivia owns no trivia of its own, so <see cref="FullSpan"/> equals <see cref="Span"/>.</summary>
    public TextSpan FullSpan => Span;

    public string Text => Green?.Text ?? string.Empty;

    public bool HasStructure => Green?.HasStructure ?? false;

    public SyntaxNode? GetStructure()
        => Green?.Structure is GreenSyntaxNode structure && _tree is not null
            ? structure.CreateRed(null, Position, _tree)
            : null;

    public IReadOnlyList<DaxDiagnostic> GetDiagnostics()
    {
        if (Green is null) return Array.Empty<DaxDiagnostic>();
        var collected = new List<DaxDiagnostic>();
        DiagnosticCollector.Collect(Green, collected);
        return DiagnosticCollector.Sorted(collected);
    }

    public string ToFullString() => Text;

    public override string ToString() => Text;

    public bool Equals(SyntaxTrivia other)
        => ReferenceEquals(Green, other.Green) && Token == other.Token && Position == other.Position;

    public override bool Equals(object? obj) => obj is SyntaxTrivia other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Green, Token, Position);

    public static bool operator ==(SyntaxTrivia left, SyntaxTrivia right) => left.Equals(right);

    public static bool operator !=(SyntaxTrivia left, SyntaxTrivia right) => !left.Equals(right);
}
