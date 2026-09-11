using Semanticus.Dax.Lexing;
using Semanticus.Dax.Text;

namespace Semanticus.Dax;

/// <summary>
/// The first-party, handwritten DAX lexer (spec §1). It consumes an immutable <see cref="DaxSourceText"/>
/// and produces tokens, trivia, lexical diagnostics, and exactly one zero-width EOF token. It runs no
/// inference, knows no functions/TOM/model, and is independent of ANTLR and Tabular Editor.
/// </summary>
public static class DaxLexer
{
    public static DaxLexResult Lex(DaxSourceText source, DaxLexOptions? options = null, CancellationToken cancellationToken = default)
        => LexCore(source, options, cancellationToken, null);

    /// <summary>
    /// Internal, test-only entry (spec §9.3 / §11.1 F-CANCEL). <paramref name="checkpointObserver"/> is invoked
    /// immediately before EVERY cancellation-token check with the current absolute UTF-16 scan position, so the
    /// 4,096-unit polling bound is deterministically provable and a test can cancel from a known checkpoint. It
    /// is never part of the public surface and has zero effect on public output when null.
    /// </summary>
    internal static DaxLexResult LexCore(DaxSourceText source, DaxLexOptions? options, CancellationToken cancellationToken, Action<int>? checkpointObserver)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return new Scanner(source, options ?? DaxLexOptions.Default, cancellationToken, checkpointObserver).Lex();
    }
}

/// <summary>
/// The scanning + trivia-attachment engine. One instance per lex call. Implements the §1.2 recognition
/// order, §7 recovery, §9 limits/cancellation, then attaches trivia per §6.6.
/// </summary>
internal sealed class Scanner
{
    // A flat, positionless scan record. Materialized into DaxToken/DaxTrivia during attachment.
    private readonly struct RawElement
    {
        public readonly bool IsTrivia;
        public readonly DaxTokenKind TokenKind;
        public readonly DaxTriviaKind TriviaKind;
        public readonly int Start;
        public readonly int Length;
        public readonly bool IsTerminated;
        public readonly bool IsTerminalRecovery; // spec §9.2: the single resource-limit remainder token

        public RawElement(bool isTrivia, DaxTokenKind tk, DaxTriviaKind trk, int start, int length, bool terminated, bool terminalRecovery = false)
        {
            IsTrivia = isTrivia; TokenKind = tk; TriviaKind = trk; Start = start; Length = length; IsTerminated = terminated;
            IsTerminalRecovery = terminalRecovery;
        }
    }

    private readonly DaxSourceText _srcObj;
    private readonly string _src;
    private readonly int _len;
    private readonly int _maxElements;
    private readonly int _maxCand;
    private readonly CancellationToken _ct;
    private readonly Action<int>? _checkpointObserver; // spec §11.1: test-only seam; null on the public path

    private readonly List<RawElement> _elements = new();
    private readonly List<DaxDiagnostic> _diags = new();
    private int _pos;
    private bool _stop;            // set when a resource-limit terminal fired
    private int _lastCheckpoint;   // spec §9.3: absolute position of the previous cancellation checkpoint

    public Scanner(DaxSourceText source, DaxLexOptions options, CancellationToken ct, Action<int>? checkpointObserver = null)
    {
        _srcObj = source;
        _src = source.Text;
        _len = source.Length;
        _maxElements = options.MaximumLexicalElementCount;
        _maxCand = options.MaximumCandidateLength;
        _ct = ct;
        _checkpointObserver = checkpointObserver;
    }

    public DaxLexResult Lex()
    {
        Checkpoint(_pos); // spec §9.3: observe cancellation before beginning (position 0)
        Scan();
        var tokens = Attach();
        return new DaxLexResult(_srcObj, tokens, _diags);
    }

    // ---- Phase 1: flat scan (spec §1.2 recognition order) ----

    private void Scan()
    {
        while (true)
        {
            Checkpoint(_pos); // spec §9.3: before emitting each lexical element

            // spec §9.2: element-count limit reached with source remaining -> one terminal BadToken + DAXL1008.
            if (_elements.Count >= _maxElements && _pos < _len)
            {
                EmitTerminal(_pos, DaxDiagnosticCodes.ElementCountExceeded, "Maximum lexical-element count exceeded.");
                return;
            }
            if (_pos >= _len) return;

            ScanNext(_pos);
            if (_stop) return; // candidate-length terminal fired
        }
    }

    private char Peek(int i) => i < _len ? _src[i] : '\0'; // '\0' sentinel; never compared against a real NUL

    private void ScanNext(int start)
    {
        char c = _src[start];

        // (1) BOM only at absolute position zero (spec §1.2, §6.5). Elsewhere U+FEFF is a BadToken.
        if (start == 0 && c == CharInfo.Bom) { AddTrivia(DaxTriviaKind.ByteOrderMarkTrivia, start, start + 1, true, null); return; }

        // (2) whitespace, line endings, comments.
        if (CharInfo.IsHorizontalWhitespace(c)) { ScanWhitespace(start); return; }
        if (CharInfo.IsLineEndingStart(c)) { ScanEndOfLine(start, c); return; }
        if (c == '/' && Peek(start + 1) == '/') { ScanLineComment(start, Peek(start + 2) == '/'); return; }
        if (c == '-' && Peek(start + 1) == '-') { ScanDashComment(start); return; }
        if (c == '/' && Peek(start + 1) == '*') { ScanDelimitedComment(start); return; }

        // (3) dt"..." date literal — no trivia between dt and the quote (spec §4.3).
        if (IsDatePrefix(start)) { ScanStringLike(start, DaxTokenKind.DateLiteralToken, 2, DaxDiagnosticCodes.UnterminatedDateLiteral, "Unterminated date literal."); return; }

        // (4) string / quoted-name / bracketed-name delimiters.
        if (c == CharInfo.Quote) { ScanStringLike(start, DaxTokenKind.StringLiteralToken, 0, DaxDiagnosticCodes.UnterminatedString, "Unterminated string literal."); return; }
        if (c == CharInfo.Apostrophe) { ScanNameDelimited(start, DaxTokenKind.QuotedIdentifierToken, CharInfo.Apostrophe, DaxDiagnosticCodes.UnterminatedQuotedIdentifier, "Unterminated quoted identifier."); return; }
        if (c == '[') { ScanNameDelimited(start, DaxTokenKind.BracketedIdentifierToken, ']', DaxDiagnosticCodes.UnterminatedBracketedIdentifier, "Unterminated bracketed identifier."); return; }

        // (5) numeric literal.
        if (IsNumericStart(start)) { ScanNumber(start); return; }

        // (6) bare identifier, then whole-lexeme keyword classification.
        if (CharInfo.IsIdentifierStart(c)) { ScanIdentifier(start); return; }

        // (7)/(8) longest valid multi-char operator, else single-char punctuation/operator.
        if (TryScanOperator(start, out var opKind, out int opEnd)) { AddToken(opKind, start, opEnd, true, null); return; }

        // (9) one BadToken.
        ScanBadToken(start);
    }

    private void ScanWhitespace(int start)
    {
        int p = start + 1;
        while (p < _len && CharInfo.IsHorizontalWhitespace(_src[p])) // maximal run (spec §6.1)
        {
            Poll(p);
            if (TooLong(start, p)) return;
            p++;
        }
        AddTrivia(DaxTriviaKind.WhitespaceTrivia, start, p, true, null);
    }

    private void ScanEndOfLine(int start, char c)
    {
        int end = start + 1;
        if (c == CharInfo.Cr && end < _len && _src[end] == CharInfo.Lf) end++; // CRLF is one trivia, width 2 (spec §6.2)
        AddTrivia(DaxTriviaKind.EndOfLineTrivia, start, end, true, null);
    }

    // A line comment runs from its opener through the last unit before a line ending or EOF; it does NOT
    // include the terminating line ending (spec §6.3). '///'+ is documentation-comment trivia (spec §6.3).
    private void ScanLineComment(int start, bool isDoc)
    {
        int end = ScanToLineEnd(start, start + 2);
        if (_stop) return; // the §9.2 candidate-length terminal already consumed the remainder
        AddTrivia(isDoc ? DaxTriviaKind.DocumentationCommentTrivia : DaxTriviaKind.SlashSlashCommentTrivia, start, end, true, null);
    }

    private void ScanDashComment(int start)
    {
        int end = ScanToLineEnd(start, start + 2);
        if (_stop) return;
        AddTrivia(DaxTriviaKind.DashDashCommentTrivia, start, end, true, null);
    }

    private int ScanToLineEnd(int start, int from)
    {
        int p = from;
        while (p < _len && !CharInfo.IsLineEndingStart(_src[p]))
        {
            Poll(p);
            if (TooLong(start, p)) return p;
            p++;
        }
        return p;
    }

    // Nested delimited comment with an ITERATIVE depth counter (spec §6.4), never recursion.
    private void ScanDelimitedComment(int start)
    {
        int p = start + 2;
        int depth = 1;
        while (p < _len)
        {
            Poll(p);
            if (TooLong(start, p)) return;
            char ch = _src[p];
            if (ch == '/' && p + 1 < _len && _src[p + 1] == '*') { depth++; Poll(p + 1); p += 2; continue; }
            if (ch == '*' && p + 1 < _len && _src[p + 1] == '/')
            {
                depth--; Poll(p + 1); p += 2;
                if (depth == 0) { AddTrivia(DaxTriviaKind.DelimitedCommentTrivia, start, p, true, null); return; }
                continue;
            }
            p++; // all other units, incl. line endings, are content (spec §6.4)
        }
        // Unclosed: consume through EOF, IsTerminated=false, DAXL1006 (spec §6.4, §7.2).
        AddTrivia(DaxTriviaKind.DelimitedCommentTrivia, start, _len, false, Diag(DaxDiagnosticCodes.UnterminatedDelimitedComment, start, _len, "Unterminated delimited comment."));
    }

    private bool IsDatePrefix(int start)
    {
        char c0 = _src[start];
        if (c0 != 'd' && c0 != 'D') return false;
        char c1 = Peek(start + 1);
        if (c1 != 't' && c1 != 'T') return false;
        return Peek(start + 2) == CharInfo.Quote;
    }

    // Shared scanner for string bodies (spec §4.1) used by StringLiteral (prefix 0) and DateLiteral (prefix 2,
    // the 'dt' before the quote). Doubled quotes ("") are escapes; physical line endings stay inside the token
    // (spec §2.3); an unmatched opening quote consumes through EOF as one unterminated token (spec §7.2).
    private void ScanStringLike(int start, DaxTokenKind kind, int prefix, string code, string message)
    {
        int p = start + prefix + 1; // first body char (past the opening quote)
        while (p < _len)
        {
            Poll(p);
            if (TooLong(start, p)) return;
            if (_src[p] == CharInfo.Quote)
            {
                if (p + 1 < _len && _src[p + 1] == CharInfo.Quote) { Poll(p + 1); p += 2; continue; } // escaped ""
                AddToken(kind, start, p + 1, true, null); // closing quote consumed
                return;
            }
            p++;
        }
        AddToken(kind, start, _len, false, Diag(code, start, _len, message));
    }

    // Quoted names (' ') and bracketed names ([ ]) share recovery: doubled close-char is an escape ('' or ]]),
    // a physical line break terminates recovery BEFORE the break, and EOF also terminates (spec §3.2, §3.3, §7.2).
    private void ScanNameDelimited(int start, DaxTokenKind kind, char closeChar, string code, string message)
    {
        int p = start + 1;
        while (p < _len)
        {
            Poll(p);
            if (TooLong(start, p)) return;
            char ch = _src[p];
            if (CharInfo.IsLineEndingStart(ch)) break; // unterminated: stop before the line break (not swallowed)
            if (ch == closeChar)
            {
                if (p + 1 < _len && _src[p + 1] == closeChar) { Poll(p + 1); p += 2; continue; } // escaped '' or ]]
                AddToken(kind, start, p + 1, true, null);
                return;
            }
            p++;
        }
        AddToken(kind, start, p, false, Diag(code, start, p, message));
    }

    private bool IsNumericStart(int start)
    {
        char c = _src[start];
        if (CharInfo.IsDigit(c)) return true;
        return c == '.' && start + 1 < _len && CharInfo.IsDigit(_src[start + 1]); // ".5" style (spec §4.2)
    }

    // Numeric forms (spec §4.2). Recognition is non-speculative: a dot is only consumed as a fraction when a
    // digit follows, and an exponent marker is only consumed when a valid [eE][+-]?Digit+ follows.
    //
    // The structural units BETWEEN digit runs — the accepted decimal point, the exponent marker, the optional
    // exponent sign — are consumed outside ScanDigits, so they must be polled explicitly (spec §9.3). They are
    // few, but each one lands wherever the digits happened to end: a run ending exactly on the bound puts the
    // next poll one or two units past it. Only COMMITTED units are polled; the abandoned `look` lookahead
    // consumes nothing and must not move the checkpoint.
    private void ScanNumber(int start)
    {
        int p = start;
        bool hasDot = false, hasExp = false;

        if (CharInfo.IsDigit(_src[p]))
        {
            if (!ScanDigits(start, ref p)) return;
            if (p < _len && _src[p] == '.' && p + 1 < _len && CharInfo.IsDigit(_src[p + 1]))
            {
                Poll(p);                             // the accepted decimal point is a consumed unit
                if (TooLong(start, p)) return;
                p++;
                if (!ScanDigits(start, ref p)) return;
                hasDot = true;
            }
        }
        else // ".digits" (IsNumericStart guaranteed a digit after the dot)
        {
            p++; // the dot — the first unit of the candidate, already checkpointed by the per-element poll
            if (!ScanDigits(start, ref p)) return;
            hasDot = true;
        }

        if (p < _len && (_src[p] == 'e' || _src[p] == 'E'))
        {
            int look = p + 1;
            if (look < _len && (_src[look] == '+' || _src[look] == '-')) look++;
            if (look < _len && CharInfo.IsDigit(_src[look]))
            {
                for (int q = p; q < look; q++)       // the marker, and the sign when present, are now committed
                {
                    Poll(q);
                    if (TooLong(start, q)) return;
                }
                p = look;
                if (!ScanDigits(start, ref p)) return;
                hasExp = true;
            }
            // else the 'e' is left for the next rule (e.g. "1E" -> Integer, Identifier("E")).
        }

        var kind = hasExp ? DaxTokenKind.ScientificLiteralToken : hasDot ? DaxTokenKind.DecimalLiteralToken : DaxTokenKind.IntegerLiteralToken;
        AddToken(kind, start, p, true, null);
    }

    /// <summary>A maximal digit run. Returns false when the §9.2 candidate-length terminal fired.</summary>
    private bool ScanDigits(int start, ref int p)
    {
        while (p < _len && CharInfo.IsDigit(_src[p]))
        {
            Poll(p);
            if (TooLong(start, p)) return false;
            p++;
        }
        return true;
    }

    private void ScanIdentifier(int start)
    {
        int p = start + 1;
        while (p < _len && CharInfo.IsIdentifierPart(_src[p]))
        {
            Poll(p);
            if (TooLong(start, p)) return;
            p++;
        }
        // Keyword classification happens only after MAXIMAL identifier scanning (spec §2.4): "ORDERBY" is one
        // identifier, not ORDER+BY. Comparison is OrdinalIgnoreCase over the whole raw spelling.
        var kind = ClassifyKeyword(_src.AsSpan(start, p - start));
        AddToken(kind, start, p, true, null);
    }

    private void ScanBadToken(int start)
    {
        char c = _src[start];
        // One Unicode scalar value: a valid surrogate pair is ONE bad token (width 2); an unpaired surrogate
        // is width one (spec §7.1). A pair MUST NOT be split into two bad tokens.
        int width = char.IsHighSurrogate(c) && start + 1 < _len && char.IsLowSurrogate(_src[start + 1]) ? 2 : 1;
        int end = start + width;
        AddToken(DaxTokenKind.BadToken, start, end, true, Diag(DaxDiagnosticCodes.UnrecognizedCharacter, start, end, "Unrecognized character."));
    }

    private bool TryScanOperator(int start, out DaxTokenKind kind, out int end)
    {
        char c = _src[start];
        char n = Peek(start + 1);
        switch (c)
        {
            case '(': kind = DaxTokenKind.OpenParenToken; end = start + 1; return true;
            case ')': kind = DaxTokenKind.CloseParenToken; end = start + 1; return true;
            case '{': kind = DaxTokenKind.OpenBraceToken; end = start + 1; return true;
            case '}': kind = DaxTokenKind.CloseBraceToken; end = start + 1; return true;
            case ',': kind = DaxTokenKind.CommaToken; end = start + 1; return true;
            case '.': kind = DaxTokenKind.DotToken; end = start + 1; return true;      // reached only when not a number (spec §3.4)
            case '@': kind = DaxTokenKind.AtToken; end = start + 1; return true;        // '@' outside a delimiter is always AtToken (spec §3.5)
            case ':': kind = DaxTokenKind.ColonToken; end = start + 1; return true;
            case '+': kind = DaxTokenKind.PlusToken; end = start + 1; return true;
            case '-': kind = DaxTokenKind.MinusToken; end = start + 1; return true;     // '--' already handled as a comment (spec §5.3)
            case '*': kind = DaxTokenKind.StarToken; end = start + 1; return true;
            case '/': kind = DaxTokenKind.SlashToken; end = start + 1; return true;     // '//' and '/*' already handled as comments (spec §5.3)
            case '^': kind = DaxTokenKind.CaretToken; end = start + 1; return true;
            case '&':
                if (n == '&') { kind = DaxTokenKind.AndAndToken; end = start + 2; } else { kind = DaxTokenKind.AmpersandToken; end = start + 1; }
                return true;
            case '=':
                if (n == '=') { kind = DaxTokenKind.StrictEqualsToken; end = start + 2; }      // '==' is ONE token (spec §5.1)
                else if (n == '>') { kind = DaxTokenKind.LambdaArrowToken; end = start + 2; }
                else { kind = DaxTokenKind.EqualsToken; end = start + 1; }
                return true;
            case '<':
                if (n == '>') { kind = DaxTokenKind.NotEqualsToken; end = start + 2; }
                else if (n == '=') { kind = DaxTokenKind.LessThanOrEqualsToken; end = start + 2; }
                else { kind = DaxTokenKind.LessThanToken; end = start + 1; }
                return true;
            case '>':
                if (n == '=') { kind = DaxTokenKind.GreaterThanOrEqualsToken; end = start + 2; }
                else { kind = DaxTokenKind.GreaterThanToken; end = start + 1; }
                return true;
            case '|':
                if (n == '|') { kind = DaxTokenKind.OrOrToken; end = start + 2; return true; } // '||' only; single '|' has no token
                break;
        }
        kind = DaxTokenKind.None; end = start;
        return false;
    }

    private static DaxTokenKind ClassifyKeyword(ReadOnlySpan<char> s)
    {
        // Length-bucketed OrdinalIgnoreCase match over the 21 reserved words (spec §2.4). No allocation.
        switch (s.Length)
        {
            case 2:
                if (Eq(s, "BY")) return DaxTokenKind.ByKeyword;
                if (Eq(s, "AT")) return DaxTokenKind.AtKeyword;
                if (Eq(s, "IN")) return DaxTokenKind.InKeyword;
                break;
            case 3:
                if (Eq(s, "VAR")) return DaxTokenKind.VarKeyword;
                if (Eq(s, "NOT")) return DaxTokenKind.NotKeyword;
                break;
            case 4:
                if (Eq(s, "WITH")) return DaxTokenKind.WithKeyword;
                if (Eq(s, "AXIS")) return DaxTokenKind.AxisKeyword;
                break;
            case 5:
                if (Eq(s, "ORDER")) return DaxTokenKind.OrderKeyword;
                if (Eq(s, "START")) return DaxTokenKind.StartKeyword;
                if (Eq(s, "TABLE")) return DaxTokenKind.TableKeyword;
                if (Eq(s, "SHAPE")) return DaxTokenKind.ShapeKeyword;
                if (Eq(s, "GROUP")) return DaxTokenKind.GroupKeyword;
                if (Eq(s, "TOTAL")) return DaxTokenKind.TotalKeyword;
                break;
            case 6:
                if (Eq(s, "DEFINE")) return DaxTokenKind.DefineKeyword;
                if (Eq(s, "RETURN")) return DaxTokenKind.ReturnKeyword;
                if (Eq(s, "COLUMN")) return DaxTokenKind.ColumnKeyword;
                if (Eq(s, "VISUAL")) return DaxTokenKind.VisualKeyword;
                break;
            case 7:
                if (Eq(s, "MEASURE")) return DaxTokenKind.MeasureKeyword;
                if (Eq(s, "DENSIFY")) return DaxTokenKind.DensifyKeyword;
                break;
            case 8:
                if (Eq(s, "EVALUATE")) return DaxTokenKind.EvaluateKeyword;
                if (Eq(s, "FUNCTION")) return DaxTokenKind.FunctionKeyword;
                break;
        }
        return DaxTokenKind.IdentifierToken;
    }

    private static bool Eq(ReadOnlySpan<char> s, string keyword) => s.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    // ---- element emission + resource limits ----

    private void AddToken(DaxTokenKind kind, int start, int end, bool terminated, DaxDiagnostic? diag)
        => Add(new RawElement(false, kind, DaxTriviaKind.None, start, end - start, terminated), diag);

    private void AddTrivia(DaxTriviaKind kind, int start, int end, bool terminated, DaxDiagnostic? diag)
        => Add(new RawElement(true, DaxTokenKind.None, kind, start, end - start, terminated), diag);

    private void Add(RawElement e, DaxDiagnostic? diag)
    {
        // spec §9.2 backstop. The scanners fire the limit AT the boundary (see TooLong), so a long candidate
        // is never scanned in full; this catch-all still covers the fixed-width elements that have no scan
        // loop (a width-2 line ending, operator, or surrogate-pair BadToken under a tiny limit).
        if (e.Length > _maxCand)
        {
            EmitTerminal(e.Start, DaxDiagnosticCodes.CandidateLengthExceeded, "Maximum token/trivia length exceeded.");
            return;
        }
        _elements.Add(e);
        if (diag is not null) _diags.Add(diag);
        _pos = e.Start + e.Length;
    }

    /// <summary>
    /// spec §9.2: the candidate-length limit MUST limit the WORK, not merely be checked once the candidate
    /// has already been scanned end to end. <paramref name="p"/> is the exclusive end scanned so far, so
    /// <c>p - start</c> is the length already proven to belong to this candidate: the moment that exceeds
    /// the limit, terminal recovery fires at the boundary and the remainder is never scanned. Output is
    /// identical to the post-hoc check (same terminal BadToken, same DAXL1007, same tiling) — only the work
    /// changes. Returns true when the terminal fired and the caller must abandon the candidate.
    /// </summary>
    private bool TooLong(int start, int p)
    {
        if (p - start <= _maxCand) return false;
        EmitTerminal(start, DaxDiagnosticCodes.CandidateLengthExceeded, "Maximum token/trivia length exceeded.");
        return true;
    }

    private void EmitTerminal(int start, string code, string message)
    {
        // The remainder token is the SOLE exception to one-scalar bad-token formation (spec §9.2) and is
        // flagged internally so the parser can stop at it instead of correlating DAXL1007/DAXL1008 by span.
        _elements.Add(new RawElement(false, DaxTokenKind.BadToken, DaxTriviaKind.None, start, _len - start, true, terminalRecovery: true));
        _diags.Add(new DaxDiagnostic(code, DaxDiagnosticSeverity.Error, message, new TextSpan(start, _len - start)));
        _pos = _len;
        _stop = true;
    }

    private static DaxDiagnostic Diag(string code, int start, int end, string message)
        => new(code, DaxDiagnosticSeverity.Error, message, new TextSpan(start, end - start));

    // spec §9.3: cancellation is polled at least every 4,096 code units inside a long candidate, and the
    // bound is measured between CONSECUTIVE checkpoints. "Landed exactly on a multiple of 4,096" is not a
    // bound — a scanner that steps over a boundary (a two-unit escape pair, a comment delimiter, a numeric
    // decimal point or exponent marker) never polls at all. The decision is therefore DISTANCE-based, taken
    // against the last checkpoint, so a boundary is caught when CROSSED and not only when hit.
    //
    // The invariant that makes it a bound: every unit consumed INSIDE a candidate is offered to Poll — the
    // two-unit advances poll their skipped unit and ScanNumber polls its structural transitions. A candidate's
    // opening delimiter ('dt"', '[', an escape's first unit) needs no poll of its own because Scan() takes a
    // mandatory checkpoint at the candidate's start, which is at most three units earlier.
    //
    // Consequence, deliberate: checkpoints follow the candidate, not a global grid. A candidate starting at
    // position 1 polls at 4097 and 8193, not 4096 and 8192. Public output is unaffected; the position sequence
    // is pinned by Checkpoint_positions_are_pinned_for_an_off_grid_candidate.
    private const int PollBound = 4096;

    private void Poll(int p) { if (p - _lastCheckpoint >= PollBound) Checkpoint(p); }

    // spec §9.3 / §11.1: report the absolute UTF-16 scan position to the internal observer (test seam),
    // then perform the cancellation-token check. The observer is null on the public path and cannot alter
    // output; when set, it MAY request cancellation, which the following check then honors at this position.
    private void Checkpoint(int pos)
    {
        _lastCheckpoint = pos;
        _checkpointObserver?.Invoke(pos);
        _ct.ThrowIfCancellationRequested();
    }

    // ---- Phase 2: trivia attachment (spec §6.6) ----

    private List<DaxToken> Attach()
    {
        var tokens = new List<DaxToken>();

        var real = new List<int>();
        for (int i = 0; i < _elements.Count; i++)
            if (!_elements[i].IsTrivia) real.Add(i);

        // Rule 5: no real token -> all trivia is leading trivia of EOF.
        if (real.Count == 0)
        {
            var lead = new List<DaxTrivia>(_elements.Count);
            foreach (var e in _elements) lead.Add(MakeTrivia(e));
            tokens.Add(new DaxToken(_srcObj, DaxTokenKind.EndOfFileToken, new TextSpan(_len, 0), true, lead, null));
            return tokens;
        }

        int r = real.Count;
        var leading = new List<DaxTrivia>[r];
        var trailing = new List<DaxTrivia>[r];
        for (int j = 0; j < r; j++) { leading[j] = new List<DaxTrivia>(); trailing[j] = new List<DaxTrivia>(); }

        // Rule 4: initial trivia is leading trivia of the first real token.
        for (int k = 0; k < real[0]; k++) leading[0].Add(MakeTrivia(_elements[k]));

        // Rules 1-3: inter-token trivia splits at the first line break.
        for (int j = 0; j < r - 1; j++)
        {
            int a = real[j], b = real[j + 1];
            int firstEol = -1;
            for (int k = a + 1; k < b; k++)
                if (ContainsLineBreak(_elements[k])) { firstEol = k; break; }

            if (firstEol < 0)
            {
                for (int k = a + 1; k < b; k++) trailing[j].Add(MakeTrivia(_elements[k])); // rule 1: all trailing
            }
            else
            {
                for (int k = a + 1; k <= firstEol; k++) trailing[j].Add(MakeTrivia(_elements[k])); // rule 2: through first line break
                for (int k = firstEol + 1; k < b; k++) leading[j + 1].Add(MakeTrivia(_elements[k])); // rule 3: remainder leads next
            }
        }

        // Rule 6: final trivia after the last real token is trailing trivia of that token (EOF is NOT a
        // following real token for rule 3 when a real token exists — spec §6.6 note).
        int last = real[r - 1];
        for (int k = last + 1; k < _elements.Count; k++) trailing[r - 1].Add(MakeTrivia(_elements[k]));

        for (int j = 0; j < r; j++)
        {
            var e = _elements[real[j]];
            tokens.Add(new DaxToken(_srcObj, e.TokenKind, new TextSpan(e.Start, e.Length), e.IsTerminated, leading[j], trailing[j], e.IsTerminalRecovery));
        }
        tokens.Add(new DaxToken(_srcObj, DaxTokenKind.EndOfFileToken, new TextSpan(_len, 0), true, null, null));
        return tokens;
    }

    /// <summary>
    /// spec §6.6: attachment is decided per trivia ITEM, and the item that CONTAINS the first line break is
    /// the split point. Kind alone cannot answer that — a multi-line delimited comment is ONE item whose
    /// text holds the line break, so testing for <c>EndOfLineTrivia</c> makes that break invisible and the
    /// following whitespace wrongly trails the preceding token. Only a delimited comment needs the scan:
    /// whitespace and BOM cannot contain a line ending, and a line comment always stops before its own.
    /// </summary>
    private bool ContainsLineBreak(in RawElement e)
    {
        if (e.TriviaKind == DaxTriviaKind.EndOfLineTrivia) return true;
        if (e.TriviaKind != DaxTriviaKind.DelimitedCommentTrivia) return false;
        int end = e.Start + e.Length;
        for (int i = e.Start; i < end; i++)
            if (CharInfo.IsLineEndingStart(_src[i])) return true;
        return false;
    }

    private DaxTrivia MakeTrivia(RawElement e) => new(_srcObj, e.TriviaKind, new TextSpan(e.Start, e.Length), e.IsTerminated);
}
