namespace Semanticus.Dax.Lexing;

/// <summary>
/// Deterministic, culture-free character classification (spec section 9.5). The whitespace and line-ending
/// sets are EXPLICIT for cross-platform determinism; the lexer MUST NOT use <c>char.IsWhiteSpace</c>,
/// Unicode category tables, or current culture to expand them (spec section 6.1). Code points are written
/// as numeric literals (never as literal control chars) so the source stays unambiguous and pure-ASCII.
/// </summary>
internal static class CharInfo
{
    public const char Bom = (char)0xFEFF;
    public const char Cr = (char)0x000D;
    public const char Lf = (char)0x000A;
    public const char Quote = (char)0x0022;      // "
    public const char Apostrophe = (char)0x0027; // '

    // ASCII digit only - numeric recognition never uses culture (spec section 9.5).
    public static bool IsDigit(char c) => c >= '0' && c <= '9';

    // Bare-name policy is deliberately ASCII (spec section 3.1): [A-Za-z_][A-Za-z0-9_]*.
    public static bool IsIdentifierStart(char c) => c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    public static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || IsDigit(c);

    // Explicit horizontal-whitespace table (spec section 6.1). U+2000..U+200A is contiguous; the rest are
    // listed individually. Notably NOT here: U+200B ZERO WIDTH SPACE and U+2060 WORD JOINER (both BadToken),
    // and U+FEFF (BOM only at position 0, otherwise BadToken).
    public static bool IsHorizontalWhitespace(char c)
    {
        int u = c;
        return u == 0x0009 // CHARACTER TABULATION
            || u == 0x000B // LINE TABULATION
            || u == 0x000C // FORM FEED
            || u == 0x0020 // SPACE
            || u == 0x00A0 // NO-BREAK SPACE
            || u == 0x1680 // OGHAM SPACE MARK
            || u == 0x180E // MONGOLIAN VOWEL SEPARATOR (legacy vendored compatibility)
            || (u >= 0x2000 && u <= 0x200A) // EN QUAD .. HAIR SPACE
            || u == 0x202F // NARROW NO-BREAK SPACE
            || u == 0x205F // MEDIUM MATHEMATICAL SPACE
            || u == 0x3000; // IDEOGRAPHIC SPACE
    }

    // Logical line-ending starters (spec section 6.2): CR, LF, NEL, LS, PS. CRLF is handled as one width-2
    // unit by the scanner; this predicate only reports that a line ending begins here.
    public static bool IsLineEndingStart(char c)
    {
        int u = c;
        return u == 0x000D  // CR
            || u == 0x000A  // LF
            || u == 0x0085  // NEL
            || u == 0x2028  // LINE SEPARATOR
            || u == 0x2029; // PARAGRAPH SEPARATOR
    }
}
