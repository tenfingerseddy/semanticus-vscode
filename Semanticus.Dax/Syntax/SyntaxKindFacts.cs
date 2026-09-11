namespace Semanticus.Dax.Syntax;

/// <summary>
/// The ONLY permitted conversion between A1's frozen lexical enums and <see cref="SyntaxKind"/>
/// (A2 §7.2: "A total mapping function ... is the only permitted conversion; there is no implicit
/// numeric relationship between the two enums"). Both maps are exhaustive switches that throw on an
/// unmapped value, so adding a lexical kind without extending the map fails loudly instead of
/// silently producing <see cref="SyntaxKind.None"/>.
/// </summary>
internal static class SyntaxKindFacts
{
    internal static SyntaxKind FromTokenKind(DaxTokenKind kind) => kind switch
    {
        DaxTokenKind.None => SyntaxKind.None,

        DaxTokenKind.BadToken => SyntaxKind.BadToken,
        DaxTokenKind.EndOfFileToken => SyntaxKind.EndOfFileToken,

        DaxTokenKind.IdentifierToken => SyntaxKind.IdentifierToken,
        DaxTokenKind.QuotedIdentifierToken => SyntaxKind.QuotedIdentifierToken,
        DaxTokenKind.BracketedIdentifierToken => SyntaxKind.BracketedIdentifierToken,

        DaxTokenKind.IntegerLiteralToken => SyntaxKind.IntegerLiteralToken,
        DaxTokenKind.DecimalLiteralToken => SyntaxKind.DecimalLiteralToken,
        DaxTokenKind.ScientificLiteralToken => SyntaxKind.ScientificLiteralToken,
        DaxTokenKind.DateLiteralToken => SyntaxKind.DateLiteralToken,
        DaxTokenKind.StringLiteralToken => SyntaxKind.StringLiteralToken,

        DaxTokenKind.DefineKeyword => SyntaxKind.DefineKeyword,
        DaxTokenKind.EvaluateKeyword => SyntaxKind.EvaluateKeyword,
        DaxTokenKind.OrderKeyword => SyntaxKind.OrderKeyword,
        DaxTokenKind.ByKeyword => SyntaxKind.ByKeyword,
        DaxTokenKind.StartKeyword => SyntaxKind.StartKeyword,
        DaxTokenKind.AtKeyword => SyntaxKind.AtKeyword,
        DaxTokenKind.VarKeyword => SyntaxKind.VarKeyword,
        DaxTokenKind.ReturnKeyword => SyntaxKind.ReturnKeyword,
        DaxTokenKind.MeasureKeyword => SyntaxKind.MeasureKeyword,
        DaxTokenKind.ColumnKeyword => SyntaxKind.ColumnKeyword,
        DaxTokenKind.TableKeyword => SyntaxKind.TableKeyword,
        DaxTokenKind.FunctionKeyword => SyntaxKind.FunctionKeyword,
        DaxTokenKind.WithKeyword => SyntaxKind.WithKeyword,
        DaxTokenKind.VisualKeyword => SyntaxKind.VisualKeyword,
        DaxTokenKind.ShapeKeyword => SyntaxKind.ShapeKeyword,
        DaxTokenKind.AxisKeyword => SyntaxKind.AxisKeyword,
        DaxTokenKind.GroupKeyword => SyntaxKind.GroupKeyword,
        DaxTokenKind.TotalKeyword => SyntaxKind.TotalKeyword,
        DaxTokenKind.DensifyKeyword => SyntaxKind.DensifyKeyword,
        DaxTokenKind.InKeyword => SyntaxKind.InKeyword,
        DaxTokenKind.NotKeyword => SyntaxKind.NotKeyword,

        DaxTokenKind.OpenParenToken => SyntaxKind.OpenParenToken,
        DaxTokenKind.CloseParenToken => SyntaxKind.CloseParenToken,
        DaxTokenKind.OpenBraceToken => SyntaxKind.OpenBraceToken,
        DaxTokenKind.CloseBraceToken => SyntaxKind.CloseBraceToken,
        DaxTokenKind.CommaToken => SyntaxKind.CommaToken,
        DaxTokenKind.DotToken => SyntaxKind.DotToken,
        DaxTokenKind.AtToken => SyntaxKind.AtToken,
        DaxTokenKind.ColonToken => SyntaxKind.ColonToken,

        DaxTokenKind.PlusToken => SyntaxKind.PlusToken,
        DaxTokenKind.MinusToken => SyntaxKind.MinusToken,
        DaxTokenKind.StarToken => SyntaxKind.StarToken,
        DaxTokenKind.SlashToken => SyntaxKind.SlashToken,
        DaxTokenKind.CaretToken => SyntaxKind.CaretToken,
        DaxTokenKind.AmpersandToken => SyntaxKind.AmpersandToken,

        DaxTokenKind.EqualsToken => SyntaxKind.EqualsToken,
        DaxTokenKind.StrictEqualsToken => SyntaxKind.StrictEqualsToken,
        DaxTokenKind.NotEqualsToken => SyntaxKind.NotEqualsToken,
        DaxTokenKind.LessThanToken => SyntaxKind.LessThanToken,
        DaxTokenKind.LessThanOrEqualsToken => SyntaxKind.LessThanOrEqualsToken,
        DaxTokenKind.GreaterThanToken => SyntaxKind.GreaterThanToken,
        DaxTokenKind.GreaterThanOrEqualsToken => SyntaxKind.GreaterThanOrEqualsToken,

        DaxTokenKind.AndAndToken => SyntaxKind.AndAndToken,
        DaxTokenKind.OrOrToken => SyntaxKind.OrOrToken,
        DaxTokenKind.LambdaArrowToken => SyntaxKind.LambdaArrowToken,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped DaxTokenKind. A2 §7.2 requires a total map.")
    };

    internal static SyntaxKind FromTriviaKind(DaxTriviaKind kind) => kind switch
    {
        DaxTriviaKind.None => SyntaxKind.None,
        DaxTriviaKind.WhitespaceTrivia => SyntaxKind.WhitespaceTrivia,
        DaxTriviaKind.EndOfLineTrivia => SyntaxKind.EndOfLineTrivia,
        DaxTriviaKind.ByteOrderMarkTrivia => SyntaxKind.ByteOrderMarkTrivia,
        DaxTriviaKind.SlashSlashCommentTrivia => SyntaxKind.SlashSlashCommentTrivia,
        DaxTriviaKind.DashDashCommentTrivia => SyntaxKind.DashDashCommentTrivia,
        DaxTriviaKind.DocumentationCommentTrivia => SyntaxKind.DocumentationCommentTrivia,
        DaxTriviaKind.DelimitedCommentTrivia => SyntaxKind.DelimitedCommentTrivia,
        DaxTriviaKind.SkippedTokensTrivia => SyntaxKind.SkippedTokensTrivia,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped DaxTriviaKind. A2 §7.2 requires a total map.")
    };

    /// <summary>A2 §7.2: keyword tokens occupy 1000-1099.</summary>
    internal static bool IsKeywordKind(SyntaxKind kind) => (int)kind is >= 1000 and <= 1099;

    /// <summary>A2 §7.2: trivia kinds occupy 1200-1299.</summary>
    internal static bool IsTriviaKind(SyntaxKind kind) => (int)kind is >= 1200 and <= 1299;

    /// <summary>Every token kind: 1-349 plus the keyword block.</summary>
    internal static bool IsTokenKind(SyntaxKind kind) => (int)kind is (>= 1 and <= 349) or (>= 1000 and <= 1099);

    /// <summary>Node kinds: everything at 2000 and above.</summary>
    internal static bool IsNodeKind(SyntaxKind kind) => (int)kind >= 2000;
}
