using Semanticus.Dax;
using Semanticus.Dax.Text;

namespace Semanticus.DaxSpike;

// Categorised token kinds the parser reasons about. First-party DaxLexer output is mapped into these.
// Ident and FuncName are parser-equivalent (see Parser.cs); the first-party lexer has no function/enum
// catalogue (spec §2.5), so every bare name — including SUM, TRUE, FALSE, BLANK, ASC, DESC — arrives as
// Ident. The Kw* set is kept for the query-structural keywords the corpus report buckets on.
public enum TokenKind
{
    Number, String, DateLit, Ident, QuotedName, BracketName,
    OpenParen, CloseParen, OpenBrace, CloseBrace, Comma,
    Plus, Minus, Star, Slash, Caret, Amp,
    Eq, EqEq, Ne, Lt, Le, Gt, Ge, AmpAmp, PipePipe, Lambda, Colon,
    KwNot, KwIn, KwVar, KwReturn, KwTrue, KwFalse, KwBlank,
    KwDefine, KwEvaluate, KwOrder, KwBy, KwStart, KwAt, KwAsc, KwDesc,
    FuncName, Eof, Unknown,
}

// One significant lexeme with its EXACT source span (Stop inclusive). Trivia is not attached yet; the
// TokenTape reconstructs it from the source gaps between significant lexemes (Trivia.cs).
internal sealed record Lexeme(TokenKind Kind, int Start, int Stop, string Raw, string Value);

// D2 re-point (A1 exit-gate rows 3-4): significant-token extraction now runs over the FIRST-PARTY
// Semanticus.Dax.DaxLexer instead of the vendored ANTLR lexer. The lexing path is 100% first-party; the
// spike's own TriviaScanner (Trivia.cs) still tiles the inter-token gaps, so round-trip stays byte-exact.
//
// Two mappings preserve the spike's dotted-name-free grammar and its recovery model:
//   (1) dotted names — the first-party lexer correctly emits Identifier '.' Identifier '.' ... with NO
//       character loss (spec §3.4), unlike the vendored lexer which DROPPED the dot and the next char.
//       The spike grammar has no dotted-name node, so a contiguous Ident (Dot Ident)+ run is still merged
//       into one Ident lexeme — but now over an INTACT source slice, not a reconstruction of dropped text.
//   (2) BadToken / AtToken — the first-party lexer emits a real BadToken per unrecognised scalar and an
//       AtToken for a bare '@' (spec §3.5, §7.1); the vendored path silently dropped both. The spike has
//       no token kind for either, so the adapter routes them back into the gap, where TriviaScanner
//       captures them as Bad trivia — identical observable behaviour to the vendored path, so the corpus
//       round-trip and gap-bucket classification are unchanged.
internal static class DaxLexAdapter
{
    public static List<Lexeme> Lex(string source)
    {
        source ??= "";
        var result = DaxLexer.Lex(DaxSourceText.From(source));

        // Real tokens only (trivia lives on the tokens and is ignored here — the gap scanner re-derives it).
        var toks = new List<DaxToken>(result.Tokens.Count);
        foreach (var t in result.Tokens)
            if (t.Kind != DaxTokenKind.EndOfFileToken)
                toks.Add(t);

        var outp = new List<Lexeme>(toks.Count);
        int n = toks.Count;
        for (int i = 0; i < n; i++)
        {
            var t = toks[i];
            var kind = Map(t.Kind);
            if (kind is null) continue; // BadToken / AtToken / standalone DotToken -> gap -> Bad trivia

            int start = t.Span.Start, stop = t.Span.End - 1; // Stop is inclusive
            string raw = t.Text, value = t.ValueText;
            var cat = kind.Value;

            // (1) dotted-name merge: contiguous  name '.' name ('.' name)*  -> one Ident lexeme.
            if (cat == TokenKind.Ident)
            {
                bool merged = false;
                while (i + 2 < n
                       && toks[i + 1].Kind == DaxTokenKind.DotToken
                       && Map(toks[i + 2].Kind) == TokenKind.Ident
                       && toks[i + 1].Span.Start == stop + 1               // '.' immediately follows the name
                       && toks[i + 2].Span.Start == toks[i + 1].Span.End)  // next name immediately follows '.'
                {
                    i += 2;
                    stop = toks[i].Span.End - 1;
                    merged = true;
                }
                if (merged) { raw = source.Substring(start, stop - start + 1); value = raw; }
            }

            outp.Add(new Lexeme(cat, start, stop, raw, value));
        }
        return outp;
    }

    // First-party DaxTokenKind -> spike TokenKind. null == "not a significant lexeme" (drop to the gap).
    private static TokenKind? Map(DaxTokenKind k) => k switch
    {
        DaxTokenKind.IntegerLiteralToken or DaxTokenKind.DecimalLiteralToken or DaxTokenKind.ScientificLiteralToken => TokenKind.Number,
        DaxTokenKind.StringLiteralToken => TokenKind.String,
        DaxTokenKind.DateLiteralToken => TokenKind.DateLit,
        DaxTokenKind.IdentifierToken => TokenKind.Ident,
        DaxTokenKind.QuotedIdentifierToken => TokenKind.QuotedName,
        DaxTokenKind.BracketedIdentifierToken => TokenKind.BracketName,

        DaxTokenKind.OpenParenToken => TokenKind.OpenParen,
        DaxTokenKind.CloseParenToken => TokenKind.CloseParen,
        DaxTokenKind.OpenBraceToken => TokenKind.OpenBrace,
        DaxTokenKind.CloseBraceToken => TokenKind.CloseBrace,
        DaxTokenKind.CommaToken => TokenKind.Comma,
        DaxTokenKind.ColonToken => TokenKind.Colon,

        DaxTokenKind.PlusToken => TokenKind.Plus,
        DaxTokenKind.MinusToken => TokenKind.Minus,
        DaxTokenKind.StarToken => TokenKind.Star,
        DaxTokenKind.SlashToken => TokenKind.Slash,
        DaxTokenKind.CaretToken => TokenKind.Caret,
        DaxTokenKind.AmpersandToken => TokenKind.Amp,

        DaxTokenKind.EqualsToken => TokenKind.Eq,
        DaxTokenKind.StrictEqualsToken => TokenKind.EqEq,   // '==' is natively ONE token now (spec §5.1)
        DaxTokenKind.NotEqualsToken => TokenKind.Ne,
        DaxTokenKind.LessThanToken => TokenKind.Lt,
        DaxTokenKind.LessThanOrEqualsToken => TokenKind.Le,
        DaxTokenKind.GreaterThanToken => TokenKind.Gt,
        DaxTokenKind.GreaterThanOrEqualsToken => TokenKind.Ge,
        DaxTokenKind.AndAndToken => TokenKind.AmpAmp,
        DaxTokenKind.OrOrToken => TokenKind.PipePipe,
        DaxTokenKind.LambdaArrowToken => TokenKind.Lambda,

        // Query-structural keywords the spike models (and the corpus report buckets on).
        DaxTokenKind.DefineKeyword => TokenKind.KwDefine,
        DaxTokenKind.EvaluateKeyword => TokenKind.KwEvaluate,
        DaxTokenKind.OrderKeyword => TokenKind.KwOrder,
        DaxTokenKind.ByKeyword => TokenKind.KwBy,
        DaxTokenKind.StartKeyword => TokenKind.KwStart,
        DaxTokenKind.AtKeyword => TokenKind.KwAt,
        DaxTokenKind.VarKeyword => TokenKind.KwVar,
        DaxTokenKind.ReturnKeyword => TokenKind.KwReturn,
        DaxTokenKind.NotKeyword => TokenKind.KwNot,
        DaxTokenKind.InKeyword => TokenKind.KwIn,

        // Reserved DEFINE-block words the spike has no distinct kind for. They are name-like, and the parser
        // treats Ident/FuncName identically, so mapping to Ident preserves parse-tree shape. (This also keeps
        // a table named e.g. `Table` used bare — TableKeyword under case-insensitive matching — a NameRef.)
        DaxTokenKind.MeasureKeyword or DaxTokenKind.ColumnKeyword or DaxTokenKind.TableKeyword
            or DaxTokenKind.FunctionKeyword or DaxTokenKind.WithKeyword or DaxTokenKind.VisualKeyword
            or DaxTokenKind.ShapeKeyword or DaxTokenKind.AxisKeyword or DaxTokenKind.GroupKeyword
            or DaxTokenKind.TotalKeyword or DaxTokenKind.DensifyKeyword => TokenKind.Ident,

        // Dropped to the gap (-> Bad trivia), matching the vendored path's silent drop:
        //  - BadToken: any unrecognised scalar (';', '#', '$', '%', '\', lone surrogate, ...);
        //  - AtToken: a bare '@' (the spike has no XMLA query-parameter production);
        //  - DotToken: a standalone dot (dotted-name runs are merged above before this is reached).
        DaxTokenKind.BadToken or DaxTokenKind.AtToken or DaxTokenKind.DotToken => null,

        _ => null,
    };
}
