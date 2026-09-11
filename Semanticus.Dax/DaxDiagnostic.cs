using Semanticus.Dax.Text;

namespace Semanticus.Dax;

public enum DaxDiagnosticSeverity
{
    Hidden,
    Info,
    Warning,
    Error
}

/// <summary>
/// A lexical diagnostic. A1 reserves codes DAXL1001-DAXL1008 (spec §7.3); A4 may add codes but MUST NOT
/// renumber or repurpose these. Diagnostics always carry a source span inside <c>[0, SourceText.Length]</c>.
/// </summary>
public sealed class DaxDiagnostic
{
    public DaxDiagnostic(string code, DaxDiagnosticSeverity severity, string message, TextSpan span)
    {
        Code = code;
        Severity = severity;
        Message = message;
        Span = span;
    }

    public string Code { get; }
    public DaxDiagnosticSeverity Severity { get; }
    public string Message { get; }
    public TextSpan Span { get; }

    public override string ToString() => $"{Code} {Span}: {Message}";
}

/// <summary>The A1-reserved lexical diagnostic codes (spec §7.3).</summary>
public static class DaxDiagnosticCodes
{
    public const string UnrecognizedCharacter = "DAXL1001";     // ordinary BadToken
    public const string UnterminatedString = "DAXL1002";
    public const string UnterminatedQuotedIdentifier = "DAXL1003";
    public const string UnterminatedBracketedIdentifier = "DAXL1004";
    public const string UnterminatedDateLiteral = "DAXL1005";
    public const string UnterminatedDelimitedComment = "DAXL1006";
    public const string CandidateLengthExceeded = "DAXL1007";   // MaximumCandidateLength
    public const string ElementCountExceeded = "DAXL1008";      // MaximumLexicalElementCount
}
