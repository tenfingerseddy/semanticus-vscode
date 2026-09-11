using Semanticus.Dax.Text;

namespace Semanticus.Dax;

/// <summary>
/// One lexical token. Raw <see cref="Text"/> is the exact source slice (spec §8.1); <see cref="ValueText"/>
/// is derived, lazy, convenience data (spec §8.2) whose computation never mutates Text/spans/round-trip
/// (spec §8.1, property F-RAWVALUE §11.1). Every token carries leading and trailing trivia (spec §6.6).
/// </summary>
public sealed class DaxToken
{
    private static readonly IReadOnlyList<DaxTrivia> Empty = Array.Empty<DaxTrivia>();

    private readonly DaxSourceText _source;
    private string? _valueText; // lazily computed cache; a null cache means "not yet computed".

    internal DaxToken(
        DaxSourceText source,
        DaxTokenKind kind,
        TextSpan span,
        bool isTerminated,
        IReadOnlyList<DaxTrivia>? leadingTrivia = null,
        IReadOnlyList<DaxTrivia>? trailingTrivia = null,
        bool isTerminalRecovery = false)
    {
        _source = source;
        Kind = kind;
        Span = span;
        IsTerminated = isTerminated;
        LeadingTrivia = leadingTrivia ?? Empty;
        TrailingTrivia = trailingTrivia ?? Empty;
        IsTerminalRecovery = isTerminalRecovery;
    }

    public DaxTokenKind Kind { get; }

    /// <summary>The token's own raw span (excludes owned trivia). Zero-width only for EOF (spec §7.4).</summary>
    public TextSpan Span { get; }

    /// <summary>
    /// False only for an unterminated string/date/quoted-name/bracketed-name token (spec §7.2). True for
    /// every other kind, including EOF and BadToken (termination is not a concept there).
    /// </summary>
    public bool IsTerminated { get; }

    public IReadOnlyList<DaxTrivia> LeadingTrivia { get; }
    public IReadOnlyList<DaxTrivia> TrailingTrivia { get; }

    /// <summary>
    /// True only for the single remainder <see cref="DaxTokenKind.BadToken"/> produced by resource-limit
    /// recovery (spec §9.2: DAXL1007 / DAXL1008) — the sole exception to one-scalar bad-token formation.
    /// Spec §9.2 requires it to be identifiable internally without a public token kind, so the A2 parser
    /// can stop rather than parse an unscanned remainder as ordinary DAX, and does not have to re-derive
    /// the fact by correlating a diagnostic code and span. Internal by design (see InternalsVisibleTo);
    /// it is not part of the public A1 surface.
    /// </summary>
    internal bool IsTerminalRecovery { get; }

    /// <summary>Exact source spelling. For EOF this is the empty string (zero-width span).</summary>
    public string Text => _source.ToString(Span);

    /// <summary>Decoded convenience text per the §8.2 table. Computed once, then cached.</summary>
    public string ValueText => _valueText ??= ValueTextDecoder.Decode(Kind, Text, IsTerminated);

    public override string ToString() => $"{Kind} {Span} \"{Text}\"";
}
