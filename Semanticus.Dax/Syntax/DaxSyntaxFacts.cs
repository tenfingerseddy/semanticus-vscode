using Semanticus.Dax.Lexing;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// Kind predicates and the operator ladder (A0 §7.6).
///
/// <para>
/// There is deliberately NO <c>IsAdmittedKeywordName</c>. Admission is position-dependent — the same
/// keyword is retained in a <c>VAR</c> declaration slot, required in a UDF hint position, and rejected as a
/// reference — so a kind-only predicate cannot express why, and shipping one would invite exactly the wrong
/// reasoning (A2 §6.2). Use <see cref="IsKeyword"/> plus the position the token was found in.
/// </para>
/// </summary>
public static class DaxSyntaxFacts
{
    /// <summary>One of the 21 reserved keywords (A1 §2.4).</summary>
    public static bool IsKeyword(SyntaxKind kind) => SyntaxKindFacts.IsKeywordKind(kind);

    /// <summary>
    /// A literal TOKEN kind. <c>TRUE</c>, <c>FALSE</c>, and <c>BLANK</c> are not here: A1 lexes them as
    /// identifiers, so they reach the tree as names or calls (A2 §4.3).
    /// </summary>
    public static bool IsLiteral(SyntaxKind kind) => kind is
        SyntaxKind.IntegerLiteralToken or
        SyntaxKind.DecimalLiteralToken or
        SyntaxKind.ScientificLiteralToken or
        SyntaxKind.DateLiteralToken or
        SyntaxKind.StringLiteralToken;

    public static bool IsTrivia(SyntaxKind kind) => SyntaxKindFacts.IsTriviaKind(kind);

    public static bool IsBinaryOperator(SyntaxKind kind) => GetBinaryOperatorPrecedence(kind) != 0;

    /// <summary>
    /// The A0-language-scope §3.2 ladder level, 1-10, or 0 when the kind is not a binary operator.
    /// LOWER binds TIGHTER: level 2 (<c>^</c>) binds tighter than level 10 (<c>||</c>).
    ///
    /// <para>
    /// <c>^</c> is right associative and takes its right operand from level 3, which is what
    /// simultaneously produces all three A0-workplan §1.4 pins: <c>2^3^2 == 2^(3^2)</c>,
    /// <c>-2^2 == -(2^2)</c>, and a legal signed exponent <c>2^-3</c> (A2 §4.2). Prefix <c>-</c> (level 3)
    /// and prefix <c>NOT</c> (level 8) are unary and are not reported here.
    /// </para>
    /// </summary>
    public static int GetBinaryOperatorPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.CaretToken => 2,

        SyntaxKind.StarToken or SyntaxKind.SlashToken => 4,

        SyntaxKind.PlusToken or SyntaxKind.MinusToken => 5,

        SyntaxKind.AmpersandToken => 6,

        SyntaxKind.EqualsToken or SyntaxKind.StrictEqualsToken or SyntaxKind.NotEqualsToken or
        SyntaxKind.LessThanToken or SyntaxKind.LessThanOrEqualsToken or
        SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanOrEqualsToken or
        SyntaxKind.InKeyword => 7,

        SyntaxKind.AndAndToken => 9,

        SyntaxKind.OrOrToken => 10,

        _ => 0
    };

    /// <summary>
    /// The canonical spelling of a fixed-text kind: punctuation, operators, and keywords. Null for kinds
    /// whose text comes from the source (identifiers, literals, trivia) and for every node kind.
    /// Keywords are matched case-insensitively by A1, so this is the CANONICAL spelling, not the only one.
    /// </summary>
    public static string? GetFixedText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.OpenParenToken => "(",
        SyntaxKind.CloseParenToken => ")",
        SyntaxKind.OpenBraceToken => "{",
        SyntaxKind.CloseBraceToken => "}",
        SyntaxKind.CommaToken => ",",
        SyntaxKind.DotToken => ".",
        SyntaxKind.AtToken => "@",
        SyntaxKind.ColonToken => ":",

        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.CaretToken => "^",
        SyntaxKind.AmpersandToken => "&",
        SyntaxKind.EqualsToken => "=",
        SyntaxKind.StrictEqualsToken => "==",
        SyntaxKind.NotEqualsToken => "<>",
        SyntaxKind.LessThanToken => "<",
        SyntaxKind.LessThanOrEqualsToken => "<=",
        SyntaxKind.GreaterThanToken => ">",
        SyntaxKind.GreaterThanOrEqualsToken => ">=",
        SyntaxKind.AndAndToken => "&&",
        SyntaxKind.OrOrToken => "||",
        SyntaxKind.LambdaArrowToken => "=>",

        SyntaxKind.EndOfFileToken => "",

        SyntaxKind.DefineKeyword => "DEFINE",
        SyntaxKind.EvaluateKeyword => "EVALUATE",
        SyntaxKind.OrderKeyword => "ORDER",
        SyntaxKind.ByKeyword => "BY",
        SyntaxKind.StartKeyword => "START",
        SyntaxKind.AtKeyword => "AT",
        SyntaxKind.VarKeyword => "VAR",
        SyntaxKind.ReturnKeyword => "RETURN",
        SyntaxKind.MeasureKeyword => "MEASURE",
        SyntaxKind.ColumnKeyword => "COLUMN",
        SyntaxKind.TableKeyword => "TABLE",
        SyntaxKind.FunctionKeyword => "FUNCTION",
        SyntaxKind.WithKeyword => "WITH",
        SyntaxKind.VisualKeyword => "VISUAL",
        SyntaxKind.ShapeKeyword => "SHAPE",
        SyntaxKind.AxisKeyword => "AXIS",
        SyntaxKind.GroupKeyword => "GROUP",
        SyntaxKind.TotalKeyword => "TOTAL",
        SyntaxKind.DensifyKeyword => "DENSIFY",
        SyntaxKind.InKeyword => "IN",
        SyntaxKind.NotKeyword => "NOT",

        _ => null
    };

    /// <summary>
    /// True when <paramref name="text"/> would lex as a bare <c>IdentifierToken</c>: the ASCII bare-name
    /// policy <c>[A-Za-z_][A-Za-z0-9_]*</c> (A1 §3.1), and not one of the 21 reserved keywords, which A1
    /// matches case-insensitively (A1 §2.4). A caller writing a name that fails this must delimit it.
    /// </summary>
    public static bool IsValidBareIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!CharInfo.IsIdentifierStart(text[0])) return false;
        for (var i = 1; i < text.Length; i++)
            if (!CharInfo.IsIdentifierPart(text[i]))
                return false;
        return !KeywordSpellings.Contains(text);
    }

    private static readonly HashSet<string> KeywordSpellings = BuildKeywordSpellings();

    private static HashSet<string> BuildKeywordSpellings()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var value = 1000; value <= 1020; value++)
            if (GetFixedText((SyntaxKind)value) is { } text)
                set.Add(text);
        return set;
    }
}
