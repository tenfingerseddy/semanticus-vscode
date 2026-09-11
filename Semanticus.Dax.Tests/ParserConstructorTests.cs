using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.4, <c>G-P-CTOR-001</c>..<c>-011</c>: the constructors (§4.6) and the sharpest pin in the
/// specification — ARITY decides row-versus-parenthesized, in every grammar position, with no lookahead
/// and no context (§5.3).
/// </summary>
public class ParserConstructorTests
{
    [Fact]
    public void G_P_CTOR_001_one_expression_in_parentheses_is_a_parenthesized_expression()
    {
        var tree = Golden("(1)", """
            ExpressionCompilationUnit [0..3)
              ParenthesizedExpression [0..3)
                OpenParenToken [0..1) "("
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CloseParenToken [2..3) ")"
              EndOfFileToken [3..3)
            """);

        Assert.IsType<ParenthesizedExpressionSyntax>(Expr(tree));
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_CTOR_002_one_top_level_comma_makes_a_row_constructor()
    {
        var tree = Golden("(1, 2)", """
            ExpressionCompilationUnit [0..6)
              RowConstructorExpression [0..6)
                OpenParenToken [0..1) "("
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CommaToken [2..3) ","
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "2"
                CloseParenToken [5..6) ")"
              EndOfFileToken [6..6)
            """);

        var row = Assert.IsType<RowConstructorExpressionSyntax>(Expr(tree));
        Assert.Equal(2, row.Fields.Count);
        Assert.Equal(1, row.Fields.SeparatorCount);
        Assert.Empty(tree.GetDiagnostics());
    }

    /// <summary>
    /// A2 §5.3: the row is the LEFT operand of <c>IN</c>. A context-driven rule would need unbounded
    /// lookahead past the matching <c>)</c> to find the <c>IN</c>; arity needs none.
    /// </summary>
    [Fact]
    public void G_P_CTOR_003_a_row_constructor_left_of_in_needs_no_lookahead()
    {
        var tree = Golden("(a, b) IN {(1,2)}", """
            ExpressionCompilationUnit [0..17)
              MembershipExpression [0..17)
                RowConstructorExpression [0..6)
                  OpenParenToken [0..1) "("
                  IdentifierName [1..2)
                    IdentifierToken [1..2) "a"
                  CommaToken [2..3) ","
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "b"
                  CloseParenToken [5..6) ")"
                InKeyword [7..9) "IN"
                TableConstructorExpression [10..17)
                  OpenBraceToken [10..11) "{"
                  RowConstructorExpression [11..16)
                    OpenParenToken [11..12) "("
                    LiteralExpression [12..13)
                      IntegerLiteralToken [12..13) "1"
                    CommaToken [13..14) ","
                    LiteralExpression [14..15)
                      IntegerLiteralToken [14..15) "2"
                    CloseParenToken [15..16) ")"
                  CloseBraceToken [16..17) "}"
              EndOfFileToken [17..17)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var membership = Assert.IsType<MembershipExpressionSyntax>(Expr(tree));
        Assert.IsType<RowConstructorExpressionSyntax>(membership.Left);
    }

    [Fact]
    public void G_P_CTOR_004_a_table_constructor_of_scalars()
    {
        var tree = Golden("{1,2,3}", """
            ExpressionCompilationUnit [0..7)
              TableConstructorExpression [0..7)
                OpenBraceToken [0..1) "{"
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CommaToken [2..3) ","
                LiteralExpression [3..4)
                  IntegerLiteralToken [3..4) "2"
                CommaToken [4..5) ","
                LiteralExpression [5..6)
                  IntegerLiteralToken [5..6) "3"
                CloseBraceToken [6..7) "}"
              EndOfFileToken [7..7)
            """);

        var table = Assert.IsType<TableConstructorExpressionSyntax>(Expr(tree));
        Assert.Equal(3, table.Elements.Count);
        Assert.Equal(2, table.Elements.SeparatorCount);
    }

    /// <summary>
    /// A2 §4.6 / §5.7 row 1: a row inside braces is an ordinary <c>RowConstructorExpression</c> ELEMENT.
    /// There is no third "constructor row" node family, and A0 §6 was amended in place to match.
    /// </summary>
    [Fact]
    public void G_P_CTOR_005_a_row_nests_inside_a_table_constructor_with_no_special_node()
    {
        var tree = Golden("{(1,2,3)}", """
            ExpressionCompilationUnit [0..9)
              TableConstructorExpression [0..9)
                OpenBraceToken [0..1) "{"
                RowConstructorExpression [1..8)
                  OpenParenToken [1..2) "("
                  LiteralExpression [2..3)
                    IntegerLiteralToken [2..3) "1"
                  CommaToken [3..4) ","
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                  CommaToken [5..6) ","
                  LiteralExpression [6..7)
                    IntegerLiteralToken [6..7) "3"
                  CloseParenToken [7..8) ")"
                CloseBraceToken [8..9) "}"
              EndOfFileToken [9..9)
            """);

        var table = Assert.IsType<TableConstructorExpressionSyntax>(Expr(tree));
        Assert.Equal(3, Assert.IsType<RowConstructorExpressionSyntax>(Assert.Single(table.Elements)).Fields.Count);
    }

    // A2 §4.6: the empty form is accepted; host acceptance is a deferred capability question, not a parse one.
    [Fact]
    public void G_P_CTOR_006_an_empty_table_constructor_has_no_diagnostic()
    {
        var tree = Golden("{}", """
            ExpressionCompilationUnit [0..2)
              TableConstructorExpression [0..2)
                OpenBraceToken [0..1) "{"
                CloseBraceToken [1..2) "}"
              EndOfFileToken [2..2)
            """);

        Assert.Empty(Assert.IsType<TableConstructorExpressionSyntax>(Expr(tree)).Elements);
        Assert.Empty(tree.GetDiagnostics());
        Assert.False(tree.Root.ContainsRecovery);
    }

    // A2 §4.6: '(' ')' is a PARENTHESIZED expression with a missing child, not an empty row.
    [Fact]
    public void G_P_CTOR_007_empty_parentheses_are_a_parenthesized_expression_with_a_missing_child()
    {
        var tree = Golden("()", """
            ExpressionCompilationUnit [0..2)
              ParenthesizedExpression [0..2)
                OpenParenToken [0..1) "("
                MissingExpression [1..1) RECOVERY
                CloseParenToken [1..2) ")"
              EndOfFileToken [2..2)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 1, 0));

        var parenthesized = Assert.IsType<ParenthesizedExpressionSyntax>(Expr(tree));
        Assert.IsType<MissingExpressionSyntax>(parenthesized.Expression);
        Assert.True(parenthesized.Expression.IsRecoveryNode);
    }

    [Fact]
    public void G_P_CTOR_008_a_trailing_comma_makes_the_last_field_missing()
    {
        var tree = Golden("(1,)", """
            ExpressionCompilationUnit [0..4)
              RowConstructorExpression [0..4)
                OpenParenToken [0..1) "("
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CommaToken [2..3) ","
                MissingExpression [3..3) RECOVERY
                CloseParenToken [3..4) ")"
              EndOfFileToken [4..4)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 3, 0));

        var row = Assert.IsType<RowConstructorExpressionSyntax>(Expr(tree));
        Assert.Equal(2, row.Fields.Count);
        Assert.IsType<MissingExpressionSyntax>(row.Fields[1]);
    }

    [Fact]
    public void G_P_CTOR_009_a_trailing_comma_in_a_table_constructor()
    {
        var tree = Golden("{1,}", """
            ExpressionCompilationUnit [0..4)
              TableConstructorExpression [0..4)
                OpenBraceToken [0..1) "{"
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CommaToken [2..3) ","
                MissingExpression [3..3) RECOVERY
                CloseBraceToken [3..4) "}"
              EndOfFileToken [4..4)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 3, 0));

        var table = Assert.IsType<TableConstructorExpressionSyntax>(Expr(tree));
        Assert.Equal(2, table.Elements.Count);
        Assert.IsType<MissingExpressionSyntax>(table.Elements[1]);
    }

    [Fact]
    public void G_P_CTOR_010_a_missing_close_brace()
    {
        var tree = Golden("{1, 2", """
            ExpressionCompilationUnit [0..5)
              TableConstructorExpression [0..5)
                OpenBraceToken [0..1) "{"
                LiteralExpression [1..2)
                  IntegerLiteralToken [1..2) "1"
                CommaToken [2..3) ","
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "2"
                CloseBraceToken [5..5) MISSING
              EndOfFileToken [5..5)
            """,
            Err(DaxParserDiagnosticCodes.CloseBraceExpected, 5, 0));

        var table = Assert.IsType<TableConstructorExpressionSyntax>(Expr(tree));
        Assert.True(table.CloseBraceToken.IsMissing);
        Assert.Equal(0, table.CloseBraceToken.FullSpan.Length);
    }

    // A2 §5.3 again, from the other side: a row as a single argument is ONE argument, not two.
    [Fact]
    public void G_P_CTOR_011_a_row_constructor_is_one_argument()
    {
        var tree = Golden("F((1,2))", """
            ExpressionCompilationUnit [0..8)
              CallExpression [0..8)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..8)
                  OpenParenToken [1..2) "("
                  Argument [2..7)
                    RowConstructorExpression [2..7)
                      OpenParenToken [2..3) "("
                      LiteralExpression [3..4)
                        IntegerLiteralToken [3..4) "1"
                      CommaToken [4..5) ","
                      LiteralExpression [5..6)
                        IntegerLiteralToken [5..6) "2"
                      CloseParenToken [6..7) ")"
                  CloseParenToken [7..8) ")"
              EndOfFileToken [8..8)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Single(arguments);
        Assert.Equal(0, arguments.SeparatorCount);
        Assert.IsType<RowConstructorExpressionSyntax>(Assert.IsType<ArgumentSyntax>(arguments[0]).Expression);
    }
}
