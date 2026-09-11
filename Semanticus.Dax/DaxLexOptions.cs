namespace Semanticus.Dax;

/// <summary>
/// Lexer resource limits (spec §9.1). The v1 default values and their recovery behavior are normative;
/// they MAY become advanced parse options in A3 (spec §9.1).
/// </summary>
public sealed class DaxLexOptions
{
    /// <summary>Tokens + trivia, excluding EOF (spec §9.1). Default 1,000,000.</summary>
    public int MaximumLexicalElementCount { get; init; } = 1_000_000;

    /// <summary>Max UTF-16 code units in a single candidate token/trivia (spec §9.1). Default 1,048,576.</summary>
    public int MaximumCandidateLength { get; init; } = 1_048_576;

    public static DaxLexOptions Default { get; } = new();
}
