using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.3, <c>G-P-CALL-001</c>..<c>-009</c>: the argument-list grammar (§4.5), and above all the
/// empty-versus-omitted pin — <c>F()</c> has zero elements and no phantom omitted argument, while
/// <c>F(1,,3)</c> carries a real zero-width <c>OmittedArgumentSyntax</c> between retained commas.
/// </summary>
public class ParserCallTests
{
    [Fact]
    public void G_P_CALL_001_an_empty_argument_list_has_no_omitted_argument()
    {
        var tree = Golden("F()", """
            ExpressionCompilationUnit [0..3)
              CallExpression [0..3)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..3)
                  OpenParenToken [1..2) "("
                  CloseParenToken [2..3) ")"
              EndOfFileToken [3..3)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Empty(arguments);
        Assert.Equal(0, arguments.SeparatorCount);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_CALL_002_one_argument()
    {
        var tree = Golden("F(1)", """
            ExpressionCompilationUnit [0..4)
              CallExpression [0..4)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..4)
                  OpenParenToken [1..2) "("
                  Argument [2..3)
                    LiteralExpression [2..3)
                      IntegerLiteralToken [2..3) "1"
                  CloseParenToken [3..4) ")"
              EndOfFileToken [4..4)
            """);

        Assert.IsType<ArgumentSyntax>(Assert.Single(Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments));
    }

    // A0-language-scope §5.1: "Func(1,,3) contains an explicit OmittedArgumentSyntax; it is not collapsed away."
    [Fact]
    public void G_P_CALL_003_an_interior_hole_is_an_explicit_omitted_argument()
    {
        var tree = Golden("F(1,,3)", """
            ExpressionCompilationUnit [0..7)
              CallExpression [0..7)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..7)
                  OpenParenToken [1..2) "("
                  Argument [2..3)
                    LiteralExpression [2..3)
                      IntegerLiteralToken [2..3) "1"
                  CommaToken [3..4) ","
                  OmittedArgument [4..4)
                  CommaToken [4..5) ","
                  Argument [5..6)
                    LiteralExpression [5..6)
                      IntegerLiteralToken [5..6) "3"
                  CloseParenToken [6..7) ")"
              EndOfFileToken [7..7)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Equal(3, arguments.Count);
        Assert.Equal(2, arguments.SeparatorCount);
        Assert.IsType<ArgumentSyntax>(arguments[0]);
        Assert.IsType<OmittedArgumentSyntax>(arguments[1]);
        Assert.Equal(0, arguments[1].FullSpan.Length);
        Assert.IsType<ArgumentSyntax>(arguments[2]);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_CALL_004_two_omitted_arguments_around_one_comma()
    {
        var tree = Golden("F(,)", """
            ExpressionCompilationUnit [0..4)
              CallExpression [0..4)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..4)
                  OpenParenToken [1..2) "("
                  OmittedArgument [2..2)
                  CommaToken [2..3) ","
                  OmittedArgument [3..3)
                  CloseParenToken [3..4) ")"
              EndOfFileToken [4..4)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Equal(2, arguments.Count);
        Assert.Equal(1, arguments.SeparatorCount);
        Assert.All(arguments, a => Assert.IsType<OmittedArgumentSyntax>(a));
    }

    [Fact]
    public void G_P_CALL_005_a_trailing_hole()
    {
        var tree = Golden("F(1,)", """
            ExpressionCompilationUnit [0..5)
              CallExpression [0..5)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..5)
                  OpenParenToken [1..2) "("
                  Argument [2..3)
                    LiteralExpression [2..3)
                      IntegerLiteralToken [2..3) "1"
                  CommaToken [3..4) ","
                  OmittedArgument [4..4)
                  CloseParenToken [4..5) ")"
              EndOfFileToken [5..5)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.IsType<ArgumentSyntax>(arguments[0]);
        Assert.IsType<OmittedArgumentSyntax>(arguments[1]);
    }

    [Fact]
    public void G_P_CALL_006_a_leading_hole()
    {
        var tree = Golden("F(,1)", """
            ExpressionCompilationUnit [0..5)
              CallExpression [0..5)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..5)
                  OpenParenToken [1..2) "("
                  OmittedArgument [2..2)
                  CommaToken [2..3) ","
                  Argument [3..4)
                    LiteralExpression [3..4)
                      IntegerLiteralToken [3..4) "1"
                  CloseParenToken [4..5) ")"
              EndOfFileToken [5..5)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.IsType<OmittedArgumentSyntax>(arguments[0]);
        Assert.IsType<ArgumentSyntax>(arguments[1]);
    }

    [Fact]
    public void G_P_CALL_007_nested_calls_and_an_inner_empty_list()
    {
        var tree = Golden("F(G(1), H())", """
            ExpressionCompilationUnit [0..12)
              CallExpression [0..12)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..12)
                  OpenParenToken [1..2) "("
                  Argument [2..6)
                    CallExpression [2..6)
                      IdentifierName [2..3)
                        IdentifierToken [2..3) "G"
                      ArgumentList [3..6)
                        OpenParenToken [3..4) "("
                        Argument [4..5)
                          LiteralExpression [4..5)
                            IntegerLiteralToken [4..5) "1"
                        CloseParenToken [5..6) ")"
                  CommaToken [6..7) ","
                  Argument [8..11)
                    CallExpression [8..11)
                      IdentifierName [8..9)
                        IdentifierToken [8..9) "H"
                      ArgumentList [9..11)
                        OpenParenToken [9..10) "("
                        CloseParenToken [10..11) ")"
                  CloseParenToken [11..12) ")"
              EndOfFileToken [12..12)
            """);

        var outer = Assert.IsType<CallExpressionSyntax>(Expr(tree));
        var inner = Assert.IsType<CallExpressionSyntax>(Assert.IsType<ArgumentSyntax>(outer.ArgumentList.Arguments[1]).Expression);
        Assert.Empty(inner.ArgumentList.Arguments);
    }

    // A2 §8.4 rule 2: the inserted ')' is zero width and the diagnostic sits at the raw end of the last
    // accepted token, because the current token is EOF.
    [Fact]
    public void G_P_CALL_008_a_missing_close_paren_is_inserted_zero_width_at_eof()
    {
        var tree = Golden("F(1", """
            ExpressionCompilationUnit [0..3)
              CallExpression [0..3)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..3)
                  OpenParenToken [1..2) "("
                  Argument [2..3)
                    LiteralExpression [2..3)
                      IntegerLiteralToken [2..3) "1"
                  CloseParenToken [3..3) MISSING
              EndOfFileToken [3..3)
            """,
            Err(DaxParserDiagnosticCodes.CloseParenExpected, 3, 0));

        var close = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.CloseParenToken;
        Assert.True(close.IsMissing);
        Assert.Equal(0, close.FullSpan.Length);
        Assert.True(tree.Root.ContainsRecovery);
    }

    // A2 §4.8 position 2: an argument IS a full-expression position.
    [Fact]
    public void G_P_CALL_009_a_variable_expression_is_a_legal_argument()
    {
        var tree = Golden("F(VAR x = 1 RETURN x)", """
            ExpressionCompilationUnit [0..21)
              CallExpression [0..21)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..21)
                  OpenParenToken [1..2) "("
                  Argument [2..20)
                    VariableExpression [2..20)
                      VariableDeclaration [2..11)
                        VarKeyword [2..5) "VAR"
                        IdentifierToken [6..7) "x"
                        EqualsToken [8..9) "="
                        LiteralExpression [10..11)
                          IntegerLiteralToken [10..11) "1"
                      ReturnClause [12..20)
                        ReturnKeyword [12..18) "RETURN"
                        IdentifierName [19..20)
                          IdentifierToken [19..20) "x"
                  CloseParenToken [20..21) ")"
              EndOfFileToken [21..21)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var argument = Assert.IsType<ArgumentSyntax>(Assert.Single(Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments));
        Assert.IsType<VariableExpressionSyntax>(argument.Expression);
    }
}
