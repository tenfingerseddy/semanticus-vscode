using Semanticus.Dax.Text;

namespace Semanticus.Dax;

/// <summary>
/// One trivia item (whitespace, line ending, BOM, or comment). Raw <see cref="Text"/> is always the exact
/// source slice (spec §8.1). Trivia needs no decoded ValueText (spec §8.2).
/// </summary>
public sealed class DaxTrivia
{
    private readonly DaxSourceText _source;

    internal DaxTrivia(DaxSourceText source, DaxTriviaKind kind, TextSpan span, bool isTerminated)
    {
        _source = source;
        Kind = kind;
        Span = span;
        IsTerminated = isTerminated;
    }

    public DaxTriviaKind Kind { get; }
    public TextSpan Span { get; }

    /// <summary>False only for an unterminated <see cref="DaxTriviaKind.DelimitedCommentTrivia"/> (spec §6.4).</summary>
    public bool IsTerminated { get; }

    public string Text => _source.ToString(Span);

    public override string ToString() => $"{Kind} {Span}";
}
