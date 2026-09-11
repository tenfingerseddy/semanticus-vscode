using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// A token in the red tree (A0 §7.3).
///
/// <para>
/// A DEFAULT <c>SyntaxToken</c> is not a tree token. Its <see cref="Kind"/> is
/// <see cref="SyntaxKind.None"/>, its <see cref="IsMissing"/> is false, it has no position and no width,
/// and it is NEVER yielded by <c>ChildNodesAndTokens()</c>, <c>DescendantTokens()</c>, or any walk. It is
/// the signal an accessor returns for an OPTIONAL token child that is absent (A2 §5.6 row 2).
/// </para>
/// <para>
/// A required-but-absent token is the opposite in every respect: a real, enumerated, zero-width token with
/// <see cref="IsMissing"/> true and a <c>DAXP</c> diagnostic (A0 §11.2). Absence is not recovery; a missing
/// token is.
/// </para>
/// </summary>
public readonly struct SyntaxToken : IEquatable<SyntaxToken>
{
    internal readonly GreenToken? Green;
    private readonly DaxSyntaxTree? _tree;

    internal SyntaxToken(GreenToken? green, SyntaxNode? parent, int position, DaxSyntaxTree? tree)
    {
        Green = green;
        Parent = parent;
        Position = position;
        _tree = tree;
    }

    /// <summary>Provenance for structured trivia materialization; null on a detached token.</summary>
    internal DaxSyntaxTree? Tree => _tree;

    public SyntaxKind Kind => Green?.Kind ?? SyntaxKind.None;

    public SyntaxNode? Parent { get; }

    /// <summary>Absolute offset of <see cref="FullSpan"/>. Zero for a default token, which has no position.</summary>
    public int Position { get; }

    /// <summary>The token's raw text only, excluding its trivia.</summary>
    public TextSpan Span => new(Position + (Green?.LeadingTriviaWidth ?? 0), Green?.Text.Length ?? 0);

    public TextSpan FullSpan => new(Position, Green?.FullWidth ?? 0);

    /// <summary>Exact source spelling (A0 §5.3). Empty for a missing token, for EOF, and for a default token.</summary>
    public string Text => Green?.Text ?? string.Empty;

    /// <summary>Decoded convenience text (A0 §5.3). It never replaces <see cref="Text"/> in the tree.</summary>
    public string ValueText => Green?.ValueText ?? string.Empty;

    public bool IsMissing => Green?.IsMissing ?? false;

    public bool ContainsDiagnostics => Green?.ContainsDiagnostics ?? false;

    public SyntaxTriviaList LeadingTrivia
        => Green is null ? default : new SyntaxTriviaList(Green.LeadingTrivia, this, Position, _tree);

    public SyntaxTriviaList TrailingTrivia
        => Green is null
            ? default
            : new SyntaxTriviaList(Green.TrailingTrivia, this, Position + Green.LeadingTriviaWidth + Green.Text.Length, _tree);

    public IReadOnlyList<DaxDiagnostic> GetDiagnostics()
    {
        if (Green is null) return Array.Empty<DaxDiagnostic>();
        var collected = new List<DaxDiagnostic>();
        DiagnosticCollector.Collect(Green, collected);
        return DiagnosticCollector.Sorted(collected);
    }

    /// <summary>
    /// A DETACHED copy carrying different leading trivia: it has no parent and no position until it is put
    /// back into a tree through <c>Update</c>/<c>WithX</c>. Returns a default token unchanged, because a
    /// default token is not a tree token and cannot own trivia.
    /// </summary>
    public SyntaxToken WithLeadingTrivia(IEnumerable<SyntaxTrivia> trivia)
        => Green is null ? this : new SyntaxToken(Green.WithTrivia(ToGreen(trivia), Green.TrailingTrivia), null, 0, _tree);

    public SyntaxToken WithTrailingTrivia(IEnumerable<SyntaxTrivia> trivia)
        => Green is null ? this : new SyntaxToken(Green.WithTrivia(Green.LeadingTrivia, ToGreen(trivia)), null, 0, _tree);

    public string ToFullString() => Green?.ToFullString() ?? string.Empty;

    public override string ToString() => Text;

    public bool Equals(SyntaxToken other)
        => ReferenceEquals(Green, other.Green) && ReferenceEquals(Parent, other.Parent) && Position == other.Position;

    public override bool Equals(object? obj) => obj is SyntaxToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Green, Parent, Position);

    public static bool operator ==(SyntaxToken left, SyntaxToken right) => left.Equals(right);

    public static bool operator !=(SyntaxToken left, SyntaxToken right) => !left.Equals(right);

    private static GreenTrivia[] ToGreen(IEnumerable<SyntaxTrivia> trivia)
    {
        if (trivia is null) throw new ArgumentNullException(nameof(trivia));
        var list = new List<GreenTrivia>();
        foreach (var item in trivia)
        {
            if (item.Green is null) throw new ArgumentException("A default SyntaxTrivia cannot be attached to a token.", nameof(trivia));
            list.Add(item.Green);
        }
        return list.ToArray();
    }
}
