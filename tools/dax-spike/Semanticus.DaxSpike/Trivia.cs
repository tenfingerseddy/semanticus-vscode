namespace Semanticus.DaxSpike;

// Scans a raw inter-token GAP string into structured trivia, tiling it EXACTLY (concatenating the
// pieces reproduces the gap). This is deliberately independent of the lexer's hidden tokens, so it
// captures characters the vendored lexer DROPS (unmatched '@', ';', the letters lost after a dotted
// '.') as Bad trivia — that is what makes byte-exact round-trip hold despite the lexer defects.
internal static class TriviaScanner
{
    // NewLine set from DAXLexer.g4: LF, CR, NEL(U+0085), LS(U+2028), PS(U+2029). CRLF is paired by the caller.
    private static bool IsEol(char c) => c == (char)0x0A || c == (char)0x0D || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029;

    // Whitespace set from DAXLexer.g4 (UnicodeClassZS + Tab/VT/FF), excluding the newlines above.
    private static bool IsWs(char c)
    {
        switch ((int)c)
        {
            case 0x09: case 0x0B: case 0x0C:               // Tab, VT, FF
            case 0x20: case 0xA0: case 0x1680: case 0x180E: // space, NBSP, Ogham, Mongolian vowel sep
            case 0x2000: case 0x2001: case 0x2002: case 0x2003:
            case 0x2004: case 0x2005: case 0x2006: case 0x2008:
            case 0x2009: case 0x200A: case 0x202F: case 0x205F:
            case 0x3000:
                return true;
            default: return false;
        }
    }

    public static List<GreenTrivia> Scan(string s)
    {
        var r = new List<GreenTrivia>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\r' && i + 1 < n && s[i + 1] == '\n') { r.Add(new(TriviaKind.EndOfLine, "\r\n")); i += 2; continue; }
            if (IsEol(c)) { r.Add(new(TriviaKind.EndOfLine, c.ToString())); i++; continue; }
            if (IsWs(c))
            {
                int j = i; while (j < n && IsWs(s[j])) j++;
                r.Add(new(TriviaKind.Whitespace, s.Substring(i, j - i))); i = j; continue;
            }
            // line comments: // or --  (to end of line, exclusive of the newline)
            if ((c == '/' && i + 1 < n && s[i + 1] == '/') || (c == '-' && i + 1 < n && s[i + 1] == '-'))
            {
                int j = i + 2; while (j < n && !IsEol(s[j])) j++;
                r.Add(new(TriviaKind.SingleLineComment, s.Substring(i, j - i))); i = j; continue;
            }
            // delimited comment /* ... */ (nested, unterminated -> to end)
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int j = i + 2, depth = 1;
                while (j < n && depth > 0)
                {
                    if (j + 1 < n && s[j] == '/' && s[j + 1] == '*') { depth++; j += 2; }
                    else if (j + 1 < n && s[j] == '*' && s[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                }
                r.Add(new(TriviaKind.DelimitedComment, s.Substring(i, j - i))); i = j; continue;
            }
            // anything else = a character the lexer could not tokenise; preserve it as Bad trivia.
            int k = i;
            while (k < n && !IsWs(s[k]) && !IsEol(s[k])
                   && !(s[k] == '/' && k + 1 < n && (s[k + 1] == '/' || s[k + 1] == '*'))
                   && !(s[k] == '-' && k + 1 < n && s[k + 1] == '-'))
                k++;
            if (k == i) k++; // guarantee progress
            r.Add(new(TriviaKind.Bad, s.Substring(i, k - i))); i = k;
        }
        return r;
    }

    public static int FirstEol(List<GreenTrivia> t)
    {
        for (int i = 0; i < t.Count; i++) if (t[i].Kind == TriviaKind.EndOfLine) return i;
        return -1;
    }
}

// Precomputes each significant lexeme's leading/trailing trivia from source gaps, following the
// design §5.2 ownership rule (no line break => all trailing of preceding; else prefix-through-first-
// -line-break is trailing, remainder is leading of following; BOF => leading of first). Also yields a
// synthetic EOF token owning the final trivia. The parser consumes these into GreenTokens on demand.
internal sealed class TokenTape
{
    private readonly List<Lexeme> _lex;
    private readonly List<GreenTrivia>[] _leading;
    private readonly List<GreenTrivia>[] _trailing;
    private readonly List<GreenTrivia> _eofLeading;

    public int Count => _lex.Count;                 // number of significant lexemes (EOF excluded)
    public TokenKind Kind(int i) => i < _lex.Count ? _lex[i].Kind : TokenKind.Eof;

    public TokenTape(string source, List<Lexeme> lex)
    {
        _lex = lex;
        int n = lex.Count;
        _leading = new List<GreenTrivia>[n];
        _trailing = new List<GreenTrivia>[n];
        for (int i = 0; i < n; i++) { _leading[i] = new(); _trailing[i] = new(); }
        _eofLeading = new();

        int prevStop = -1;
        for (int k = 0; k < n; k++)
        {
            int gapStart = prevStop + 1, gapEnd = lex[k].Start;
            var gap = TriviaScanner.Scan(source.Substring(gapStart, gapEnd - gapStart));
            if (k == 0) _leading[0].AddRange(gap);           // BOF: all leading of first
            else Split(gap, _trailing[k - 1], _leading[k]);  // trailing-through-newline / leading-rest
            prevStop = lex[k].Stop;
        }
        // final gap -> trailing of last real token (through first newline) then EOF leading.
        int fs = prevStop + 1;
        var tail = TriviaScanner.Scan(source.Substring(fs));
        if (n == 0) _eofLeading.AddRange(tail);
        else Split(tail, _trailing[n - 1], _eofLeading);
    }

    private static void Split(List<GreenTrivia> gap, List<GreenTrivia> trailing, List<GreenTrivia> nextLeading)
    {
        int eol = TriviaScanner.FirstEol(gap);
        if (eol < 0) { trailing.AddRange(gap); return; }              // no newline: all trailing
        for (int i = 0; i <= eol; i++) trailing.Add(gap[i]);          // through first newline: trailing
        for (int i = eol + 1; i < gap.Count; i++) nextLeading.Add(gap[i]); // rest: leading of next
    }

    public GreenToken Real(int i)
    {
        var lx = _lex[i];
        return new GreenToken(lx.Kind, lx.Raw, lx.Value, isMissing: false, _leading[i], _trailing[i]);
    }
    public GreenToken Eof() => new GreenToken(TokenKind.Eof, "", "", isMissing: false, _eofLeading, Array.Empty<GreenTrivia>());
    public static GreenToken Missing(TokenKind expected) =>
        new GreenToken(expected, "", "", isMissing: true, Array.Empty<GreenTrivia>(), Array.Empty<GreenTrivia>());
}
