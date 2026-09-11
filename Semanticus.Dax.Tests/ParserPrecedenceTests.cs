using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.1, <c>G-P-PREC-001</c>..<c>-022</c>: the operator ladder (§4.2), the associativity pins (§5.1),
/// the three A0-workplan §1.4 exponent/sign pins, and the §4.8 rule that a variable expression is produced
/// only at a full-expression position.
///
/// <para>
/// Each row carries the exact tree — kinds, child order, spans, missing/recovery flags — plus the
/// diagnostic list as a set of <c>(Code, Severity, Span)</c> triples, and then restates the §11 "Asserted"
/// column in typed C# so the claim the row exists for is checked independently of the rendering.
/// </para>
/// </summary>
public class ParserPrecedenceTests
{
    // A0-workplan §1.4 pin 1: the sign is level 3, OUTSIDE the power, so -2^2 is -(2^2).
    [Fact]
    public void G_P_PREC_001_sign_is_outside_the_power()
    {
        var tree = Golden("-2 ^ 2", """
            ExpressionCompilationUnit [0..6)
              PrefixUnaryExpression [0..6)
                MinusToken [0..1) "-"
                BinaryExpression [1..6)
                  LiteralExpression [1..2)
                    IntegerLiteralToken [1..2) "2"
                  CaretToken [3..4) "^"
                  LiteralExpression [5..6)
                    IntegerLiteralToken [5..6) "2"
              EndOfFileToken [6..6)
            """);

        var prefix = Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.MinusToken, prefix.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.CaretToken, Assert.IsType<BinaryExpressionSyntax>(prefix.Operand).OperatorToken.Kind);
    }

    // A0-workplan §1.4 pin 3: a signed exponent is legal, because the RHS of ^ comes from level 3.
    [Fact]
    public void G_P_PREC_002_a_signed_exponent_is_legal()
    {
        var tree = Golden("2 ^ -3", """
            ExpressionCompilationUnit [0..6)
              BinaryExpression [0..6)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "2"
                CaretToken [2..3) "^"
                PrefixUnaryExpression [4..6)
                  MinusToken [4..5) "-"
                  LiteralExpression [5..6)
                    IntegerLiteralToken [5..6) "3"
              EndOfFileToken [6..6)
            """);

        var power = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.IsType<PrefixUnaryExpressionSyntax>(power.Right);
    }

    // A0-workplan §1.4 pin 2 / A2 §5.1: ^ is RIGHT associative.
    [Fact]
    public void G_P_PREC_003_power_is_right_associative()
    {
        var tree = Golden("2 ^ 3 ^ 2", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "2"
                CaretToken [2..3) "^"
                BinaryExpression [4..9)
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "3"
                  CaretToken [6..7) "^"
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "2"
              EndOfFileToken [9..9)
            """);

        var power = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.IsType<LiteralExpressionSyntax>(power.Left);          // NOT ((2^3)^2)
        Assert.IsType<BinaryExpressionSyntax>(power.Right);
    }

    [Fact]
    public void G_P_PREC_004_prefix_minus_nests()
    {
        var tree = Golden("- - 2", """
            ExpressionCompilationUnit [0..5)
              PrefixUnaryExpression [0..5)
                MinusToken [0..1) "-"
                PrefixUnaryExpression [2..5)
                  MinusToken [2..3) "-"
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
              EndOfFileToken [5..5)
            """);

        var outer = Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree));
        Assert.IsType<PrefixUnaryExpressionSyntax>(outer.Operand);
    }

    [Fact]
    public void G_P_PREC_005_multiplication_binds_tighter_than_addition()
    {
        var tree = Golden("1 + 2 * 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                BinaryExpression [4..9)
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                  StarToken [6..7) "*"
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        var addition = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.PlusToken, addition.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.StarToken, Assert.IsType<BinaryExpressionSyntax>(addition.Right).OperatorToken.Kind);
    }

    [Fact]
    public void G_P_PREC_006_additive_is_left_associative()
    {
        var tree = Golden("1 - 2 - 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                BinaryExpression [0..5)
                  LiteralExpression [0..1)
                    IntegerLiteralToken [0..1) "1"
                  MinusToken [2..3) "-"
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                MinusToken [6..7) "-"
                LiteralExpression [8..9)
                  IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        Assert.IsType<BinaryExpressionSyntax>(Assert.IsType<BinaryExpressionSyntax>(Expr(tree)).Left);
    }

    [Fact]
    public void G_P_PREC_007_concatenation_is_left_associative()
    {
        var tree = Golden("1 & 2 & 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                BinaryExpression [0..5)
                  LiteralExpression [0..1)
                    IntegerLiteralToken [0..1) "1"
                  AmpersandToken [2..3) "&"
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                AmpersandToken [6..7) "&"
                LiteralExpression [8..9)
                  IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        Assert.IsType<BinaryExpressionSyntax>(Assert.IsType<BinaryExpressionSyntax>(Expr(tree)).Left);
    }

    [Fact]
    public void G_P_PREC_008_concatenation_binds_tighter_than_comparison()
    {
        var tree = Golden("a = b & c", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "a"
                EqualsToken [2..3) "="
                BinaryExpression [4..9)
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "b"
                  AmpersandToken [6..7) "&"
                  IdentifierName [8..9)
                    IdentifierToken [8..9) "c"
              EndOfFileToken [9..9)
            """);

        var comparison = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.EqualsToken, comparison.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.AmpersandToken, Assert.IsType<BinaryExpressionSyntax>(comparison.Right).OperatorToken.Kind);
    }

    // A0-language-scope §14.2: NOT is level 8, so it sits OUTSIDE a level-7 comparison.
    [Fact]
    public void G_P_PREC_009_not_is_outside_a_comparison()
    {
        var tree = Golden("NOT a = b", """
            ExpressionCompilationUnit [0..9)
              PrefixUnaryExpression [0..9)
                NotKeyword [0..3) "NOT"
                BinaryExpression [4..9)
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  IdentifierName [8..9)
                    IdentifierToken [8..9) "b"
              EndOfFileToken [9..9)
            """);

        Assert.IsType<BinaryExpressionSyntax>(Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree)).Operand);
    }

    // ...and INSIDE a level-9 conjunction, which is the other half of the same pin.
    [Fact]
    public void G_P_PREC_010_not_is_inside_a_conjunction()
    {
        var tree = Golden("NOT a && b", """
            ExpressionCompilationUnit [0..10)
              BinaryExpression [0..10)
                PrefixUnaryExpression [0..5)
                  NotKeyword [0..3) "NOT"
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "a"
                AndAndToken [6..8) "&&"
                IdentifierName [9..10)
                  IdentifierToken [9..10) "b"
              EndOfFileToken [10..10)
            """);

        Assert.IsType<PrefixUnaryExpressionSyntax>(Assert.IsType<BinaryExpressionSyntax>(Expr(tree)).Left);
    }

    [Fact]
    public void G_P_PREC_011_and_binds_tighter_than_or()
    {
        var tree = Golden("a && b || c", """
            ExpressionCompilationUnit [0..11)
              BinaryExpression [0..11)
                BinaryExpression [0..6)
                  IdentifierName [0..1)
                    IdentifierToken [0..1) "a"
                  AndAndToken [2..4) "&&"
                  IdentifierName [5..6)
                    IdentifierToken [5..6) "b"
                OrOrToken [7..9) "||"
                IdentifierName [10..11)
                  IdentifierToken [10..11) "c"
              EndOfFileToken [11..11)
            """);

        var or = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.OrOrToken, or.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.AndAndToken, Assert.IsType<BinaryExpressionSyntax>(or.Left).OperatorToken.Kind);
    }

    // A2 §5.1: a chained comparison PARSES, left associatively, and gets no DAXP diagnostic. The host's
    // objection is DAXB, which needs a binder the parser does not have.
    [Fact]
    public void G_P_PREC_012_chained_comparison_parses_with_no_parser_diagnostic()
    {
        var tree = Golden("a < b < c", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                BinaryExpression [0..5)
                  IdentifierName [0..1)
                    IdentifierToken [0..1) "a"
                  LessThanToken [2..3) "<"
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "b"
                LessThanToken [6..7) "<"
                IdentifierName [8..9)
                  IdentifierToken [8..9) "c"
              EndOfFileToken [9..9)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.IsType<BinaryExpressionSyntax>(Assert.IsType<BinaryExpressionSyntax>(Expr(tree)).Left);
    }

    [Fact]
    public void G_P_PREC_013_strict_equals_is_one_token()
    {
        var tree = Golden("a == b", """
            ExpressionCompilationUnit [0..6)
              BinaryExpression [0..6)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "a"
                StrictEqualsToken [2..4) "=="
                IdentifierName [5..6)
                  IdentifierToken [5..6) "b"
              EndOfFileToken [6..6)
            """);

        var binary = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.StrictEqualsToken, binary.OperatorToken.Kind);
        Assert.Equal("==", binary.OperatorToken.Text);
    }

    [Fact]
    public void G_P_PREC_014_in_produces_a_membership_expression()
    {
        var tree = Golden("[x] IN {1,2}", """
            ExpressionCompilationUnit [0..12)
              MembershipExpression [0..12)
                BracketedName [0..3)
                  BracketedIdentifierToken [0..3) "[x]"
                InKeyword [4..6) "IN"
                TableConstructorExpression [7..12)
                  OpenBraceToken [7..8) "{"
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                  CommaToken [9..10) ","
                  LiteralExpression [10..11)
                    IntegerLiteralToken [10..11) "2"
                  CloseBraceToken [11..12) "}"
              EndOfFileToken [12..12)
            """);

        var membership = Assert.IsType<MembershipExpressionSyntax>(Expr(tree));
        Assert.IsType<BracketedNameSyntax>(membership.Left);
        Assert.IsType<TableConstructorExpressionSyntax>(membership.Right);
    }

    // A2 §5.1: there is no combined NOT IN operator. NOT is level 8, IN is level 7.
    [Fact]
    public void G_P_PREC_015_there_is_no_not_in_operator()
    {
        var tree = Golden("NOT [x] IN {1}", """
            ExpressionCompilationUnit [0..14)
              PrefixUnaryExpression [0..14)
                NotKeyword [0..3) "NOT"
                MembershipExpression [4..14)
                  BracketedName [4..7)
                    BracketedIdentifierToken [4..7) "[x]"
                  InKeyword [8..10) "IN"
                  TableConstructorExpression [11..14)
                    OpenBraceToken [11..12) "{"
                    LiteralExpression [12..13)
                      IntegerLiteralToken [12..13) "1"
                    CloseBraceToken [13..14) "}"
              EndOfFileToken [14..14)
            """);

        Assert.IsType<MembershipExpressionSyntax>(Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree)).Operand);
    }

    // A2 §5.5: NOT( is the operator over a parenthesized expression, never a call. No diagnostic.
    [Fact]
    public void G_P_PREC_016_not_paren_is_never_a_call()
    {
        var tree = Golden("NOT(ISBLANK([x]))", """
            ExpressionCompilationUnit [0..17)
              PrefixUnaryExpression [0..17)
                NotKeyword [0..3) "NOT"
                ParenthesizedExpression [3..17)
                  OpenParenToken [3..4) "("
                  CallExpression [4..16)
                    IdentifierName [4..11)
                      IdentifierToken [4..11) "ISBLANK"
                    ArgumentList [11..16)
                      OpenParenToken [11..12) "("
                      Argument [12..15)
                        BracketedName [12..15)
                          BracketedIdentifierToken [12..15) "[x]"
                      CloseParenToken [15..16) ")"
                  CloseParenToken [16..17) ")"
              EndOfFileToken [17..17)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var prefix = Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.NotKeyword, prefix.OperatorToken.Kind);
        Assert.IsType<ParenthesizedExpressionSyntax>(prefix.Operand);
    }

    /// <summary>
    /// A2 §4.8 and §8.3 (as corrected by §14 defect 3): <c>VAR</c> in operand position is not a production,
    /// it is a stopper. One <c>DAXP1001</c> at that position, nothing consumed there, and the rest becomes
    /// the ordinary trailing run. The addition is NOT a binary expression over a variable expression.
    /// </summary>
    [Fact]
    public void G_P_PREC_017_var_in_operand_position_is_recovery()
    {
        var tree = Golden("1 + VAR x = 1 RETURN x", """
            ExpressionCompilationUnit [0..22)
              BinaryExpression [0..3)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                MissingExpression [4..4) RECOVERY
              EndOfFileToken [22..22)
                lead SkippedTokensTrivia [4..22)
                  SkippedTokens [4..22) RECOVERY
                    VarKeyword [4..7) "VAR"
                    IdentifierToken [8..9) "x"
                    EqualsToken [10..11) "="
                    IntegerLiteralToken [12..13) "1"
                    ReturnKeyword [14..20) "RETURN"
                    IdentifierToken [21..22) "x"
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 4, 0),
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 4, 18));

        var binary = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.IsType<MissingExpressionSyntax>(binary.Right);
        Assert.True(binary.Right.IsRecoveryNode);
    }

    /// <summary>A2 §4.8 position 3: parenthesized IS a full-expression position, so this parses cleanly.</summary>
    [Fact]
    public void G_P_PREC_018_a_parenthesized_variable_expression_parses()
    {
        var tree = Golden("1 + (VAR x = 1 RETURN x)", """
            ExpressionCompilationUnit [0..24)
              BinaryExpression [0..24)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                ParenthesizedExpression [4..24)
                  OpenParenToken [4..5) "("
                  VariableExpression [5..23)
                    VariableDeclaration [5..14)
                      VarKeyword [5..8) "VAR"
                      IdentifierToken [9..10) "x"
                      EqualsToken [11..12) "="
                      LiteralExpression [13..14)
                        IntegerLiteralToken [13..14) "1"
                    ReturnClause [15..23)
                      ReturnKeyword [15..21) "RETURN"
                      IdentifierName [22..23)
                        IdentifierToken [22..23) "x"
                  CloseParenToken [23..24) ")"
              EndOfFileToken [24..24)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var parenthesized = Assert.IsType<ParenthesizedExpressionSyntax>(Assert.IsType<BinaryExpressionSyntax>(Expr(tree)).Right);
        Assert.IsType<VariableExpressionSyntax>(parenthesized.Expression);
    }

    // A2PR1-S6: + is level 5, so it binds tighter than & (level 6). 1 & 2 + 3 is 1 & (2 + 3).
    [Fact]
    public void G_P_PREC_019_addition_binds_tighter_than_concatenation()
    {
        var tree = Golden("1 & 2 + 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                AmpersandToken [2..3) "&"
                BinaryExpression [4..9)
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                  PlusToken [6..7) "+"
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        var concat = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.AmpersandToken, concat.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.PlusToken, Assert.IsType<BinaryExpressionSyntax>(concat.Right).OperatorToken.Kind);
    }

    // A2PR1-S6: the same ladder, other order. 1 + 2 & 3 is (1 + 2) & 3.
    [Fact]
    public void G_P_PREC_020_addition_still_binds_tighter_on_the_left()
    {
        var tree = Golden("1 + 2 & 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                BinaryExpression [0..5)
                  LiteralExpression [0..1)
                    IntegerLiteralToken [0..1) "1"
                  PlusToken [2..3) "+"
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                AmpersandToken [6..7) "&"
                LiteralExpression [8..9)
                  IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        var concat = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.AmpersandToken, concat.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.PlusToken, Assert.IsType<BinaryExpressionSyntax>(concat.Left).OperatorToken.Kind);
    }

    // A2PR1-S6: * is level 4, so it binds tighter than & (level 6). 1 & 2 * 3 is 1 & (2 * 3).
    [Fact]
    public void G_P_PREC_021_multiplication_binds_tighter_than_concatenation()
    {
        var tree = Golden("1 & 2 * 3", """
            ExpressionCompilationUnit [0..9)
              BinaryExpression [0..9)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                AmpersandToken [2..3) "&"
                BinaryExpression [4..9)
                  LiteralExpression [4..5)
                    IntegerLiteralToken [4..5) "2"
                  StarToken [6..7) "*"
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "3"
              EndOfFileToken [9..9)
            """);

        var concat = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.AmpersandToken, concat.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.StarToken, Assert.IsType<BinaryExpressionSyntax>(concat.Right).OperatorToken.Kind);
    }

    // A2PR1-S6: && is level 9, so & (level 6) binds tighter. a & b && c is (a & b) && c.
    [Fact]
    public void G_P_PREC_022_concatenation_binds_tighter_than_conjunction()
    {
        var tree = Golden("a & b && c", """
            ExpressionCompilationUnit [0..10)
              BinaryExpression [0..10)
                BinaryExpression [0..5)
                  IdentifierName [0..1)
                    IdentifierToken [0..1) "a"
                  AmpersandToken [2..3) "&"
                  IdentifierName [4..5)
                    IdentifierToken [4..5) "b"
                AndAndToken [6..8) "&&"
                IdentifierName [9..10)
                  IdentifierToken [9..10) "c"
              EndOfFileToken [10..10)
            """);

        var conjunction = Assert.IsType<BinaryExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.AndAndToken, conjunction.OperatorToken.Kind);
        Assert.Equal(SyntaxKind.AmpersandToken, Assert.IsType<BinaryExpressionSyntax>(conjunction.Left).OperatorToken.Kind);
    }
}
