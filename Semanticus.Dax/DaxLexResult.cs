using Semanticus.Dax.Text;

namespace Semanticus.Dax;

/// <summary>
/// The outcome of one lex operation: the flat token stream (always ending in exactly one zero-width
/// <see cref="DaxTokenKind.EndOfFileToken"/>, spec §1) plus lexical diagnostics in source order.
/// </summary>
public sealed class DaxLexResult
{
    internal DaxLexResult(DaxSourceText source, IReadOnlyList<DaxToken> tokens, IReadOnlyList<DaxDiagnostic> diagnostics)
    {
        Source = source;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    public DaxSourceText Source { get; }

    /// <summary>All tokens in source order, including the terminal EOF token.</summary>
    public IReadOnlyList<DaxToken> Tokens { get; }

    public IReadOnlyList<DaxDiagnostic> Diagnostics { get; }
}
