namespace Semanticus.Dax;

/// <summary>
/// The v1 trivia inventory — reproduced EXACTLY from spec §2.2. An unterminated delimited comment stays
/// <see cref="DelimitedCommentTrivia"/> with <c>IsTerminated == false</c> and a diagnostic; it does not
/// get a second kind (spec §2.2).
/// </summary>
public enum DaxTriviaKind
{
    None = 0, // Sentinel only; never emitted.

    WhitespaceTrivia,
    EndOfLineTrivia,
    ByteOrderMarkTrivia,
    SlashSlashCommentTrivia,
    DashDashCommentTrivia,
    DocumentationCommentTrivia,
    DelimitedCommentTrivia,

    // Constructed by parser recovery, never by the lexer:
    SkippedTokensTrivia
}
