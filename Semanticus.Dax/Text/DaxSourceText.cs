namespace Semanticus.Dax.Text;

/// <summary>
/// Immutable UTF-16 source buffer the lexer consumes (spec §1). Once a <see cref="DaxSourceText"/>
/// exists the lexer MUST NOT drop any code unit (spec §7.4): re-encoding the unchanged string with the
/// same encoding reproduces the same bytes. Storage is a plain string; public behavior is independent
/// of the representation.
/// </summary>
public sealed class DaxSourceText
{
    private readonly string _text;

    private DaxSourceText(string text, string? path)
    {
        _text = text;
        Path = path;
    }

    public static DaxSourceText From(string text, string? path = null)
        => new(text ?? throw new ArgumentNullException(nameof(text)), path);

    public int Length => _text.Length;
    public string? Path { get; }

    public char this[int position] => _text[position];

    /// <summary>The whole buffer. This is the reconstruction target for spec §7.4 property 2.</summary>
    public string Text => _text;

    /// <summary>
    /// The exact source slice for a span. This is the single source of raw token/trivia text — spec §8.1:
    /// <c>Text == SourceText.ToString(Span)</c>; raw text is never a stored decoded replacement.
    /// </summary>
    public string ToString(TextSpan span) => _text.Substring(span.Start, span.Length);

    public override string ToString() => _text;
}
