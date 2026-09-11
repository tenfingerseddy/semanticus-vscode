using System.Text;

namespace Semanticus.Dax;

/// <summary>
/// Implements the §8.2 decoding table. Decoding is pure over (kind, raw text, terminated) and never
/// touches the source or spans (spec §8.1). "Strip closing delimiter only when present" == only when the
/// token is terminated (spec §8.2).
/// </summary>
internal static class ValueTextDecoder
{
    public static string Decode(DaxTokenKind kind, string raw, bool isTerminated) => kind switch
    {
        // Raw spelling, original casing — identifiers, all keywords, numbers, operators, bad tokens.
        DaxTokenKind.EndOfFileToken => string.Empty,
        DaxTokenKind.QuotedIdentifierToken => Delimited(raw, openLen: 1, esc: '\'', isTerminated),
        DaxTokenKind.BracketedIdentifierToken => Delimited(raw, openLen: 1, esc: ']', isTerminated),
        DaxTokenKind.StringLiteralToken => Delimited(raw, openLen: 1, esc: '"', isTerminated),
        DaxTokenKind.DateLiteralToken => Delimited(raw, openLen: 3, esc: '"', isTerminated), // strip dt"
        _ => raw,
    };

    // Strip the opening delimiter (openLen chars); strip the closing delimiter (one char, == esc) only when
    // terminated; then collapse each doubled esc ("" / '' / ]]) to a single char (spec §8.2).
    private static string Delimited(string raw, int openLen, char esc, bool isTerminated)
    {
        int start = Math.Min(openLen, raw.Length);
        int end = raw.Length - (isTerminated && raw.Length > start ? 1 : 0);
        if (end < start) end = start;

        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            char c = raw[i];
            if (c == esc && i + 1 < end && raw[i + 1] == esc)
            {
                sb.Append(esc);
                i++; // consumed the escaped pair
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
