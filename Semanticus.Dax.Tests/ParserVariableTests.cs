using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.5, <c>G-P-VAR-001</c>..<c>-010</c>: <c>VAR</c>/<c>RETURN</c> (§4.7). Declarations stay in source
/// order and are never reordered, merged, or flattened; shadowing is a <c>DAXB</c> question and the parser
/// says nothing about it.
/// </summary>
public class ParserVariableTests
{
    [Fact]
    public void G_P_VAR_001_one_declaration_and_a_return_clause()
    {
        var tree = Golden("VAR a = 1 RETURN a", """
            ExpressionCompilationUnit [0..18)
              VariableExpression [0..18)
                VariableDeclaration [0..9)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                ReturnClause [10..18)
                  ReturnKeyword [10..16) "RETURN"
                  IdentifierName [17..18)
                    IdentifierToken [17..18) "a"
              EndOfFileToken [18..18)
            """);

        var variable = Assert.IsType<VariableExpressionSyntax>(Expr(tree));
        Assert.Single(variable.Declarations);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_VAR_002_declarations_stay_in_source_order()
    {
        var tree = Golden("VAR a = 1 VAR b = a RETURN b", """
            ExpressionCompilationUnit [0..28)
              VariableExpression [0..28)
                VariableDeclaration [0..9)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                VariableDeclaration [10..19)
                  VarKeyword [10..13) "VAR"
                  IdentifierToken [14..15) "b"
                  EqualsToken [16..17) "="
                  IdentifierName [18..19)
                    IdentifierToken [18..19) "a"
                ReturnClause [20..28)
                  ReturnKeyword [20..26) "RETURN"
                  IdentifierName [27..28)
                    IdentifierToken [27..28) "b"
              EndOfFileToken [28..28)
            """);

        var declarations = Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations;
        Assert.Equal(2, declarations.Count);
        Assert.Equal("a", declarations[0].Identifier.Text);
        Assert.Equal("b", declarations[1].Identifier.Text);
    }

    // A2 §4.7: shadowing is NOT a parse error. Duplicate-name policy is DAXB.
    [Fact]
    public void G_P_VAR_003_shadowing_is_not_a_parse_error()
    {
        var tree = Golden("VAR a = 1 VAR a = 2 RETURN a", """
            ExpressionCompilationUnit [0..28)
              VariableExpression [0..28)
                VariableDeclaration [0..9)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                VariableDeclaration [10..19)
                  VarKeyword [10..13) "VAR"
                  IdentifierToken [14..15) "a"
                  EqualsToken [16..17) "="
                  LiteralExpression [18..19)
                    IntegerLiteralToken [18..19) "2"
                ReturnClause [20..28)
                  ReturnKeyword [20..26) "RETURN"
                  IdentifierName [27..28)
                    IdentifierToken [27..28) "a"
              EndOfFileToken [28..28)
            """);

        Assert.Equal(2, Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations.Count);
        Assert.Empty(tree.GetDiagnostics());
    }

    // A2 §4.8 position 4: an initializer is a full-expression position.
    [Fact]
    public void G_P_VAR_004_a_nested_variable_expression_in_an_initializer()
    {
        var tree = Golden("VAR a = VAR b = 1 RETURN b RETURN a", """
            ExpressionCompilationUnit [0..35)
              VariableExpression [0..35)
                VariableDeclaration [0..26)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  VariableExpression [8..26)
                    VariableDeclaration [8..17)
                      VarKeyword [8..11) "VAR"
                      IdentifierToken [12..13) "b"
                      EqualsToken [14..15) "="
                      LiteralExpression [16..17)
                        IntegerLiteralToken [16..17) "1"
                    ReturnClause [18..26)
                      ReturnKeyword [18..24) "RETURN"
                      IdentifierName [25..26)
                        IdentifierToken [25..26) "b"
                ReturnClause [27..35)
                  ReturnKeyword [27..33) "RETURN"
                  IdentifierName [34..35)
                    IdentifierToken [34..35) "a"
              EndOfFileToken [35..35)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var outer = Assert.IsType<VariableExpressionSyntax>(Expr(tree));
        Assert.IsType<VariableExpressionSyntax>(outer.Declarations[0].Initializer);
    }

    [Fact]
    public void G_P_VAR_005_a_nested_variable_expression_in_the_return_body()
    {
        var tree = Golden("VAR a = 1 RETURN VAR b = 2 RETURN b", """
            ExpressionCompilationUnit [0..35)
              VariableExpression [0..35)
                VariableDeclaration [0..9)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                ReturnClause [10..35)
                  ReturnKeyword [10..16) "RETURN"
                  VariableExpression [17..35)
                    VariableDeclaration [17..26)
                      VarKeyword [17..20) "VAR"
                      IdentifierToken [21..22) "b"
                      EqualsToken [23..24) "="
                      LiteralExpression [25..26)
                        IntegerLiteralToken [25..26) "2"
                    ReturnClause [27..35)
                      ReturnKeyword [27..33) "RETURN"
                      IdentifierName [34..35)
                        IdentifierToken [34..35) "b"
              EndOfFileToken [35..35)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.IsType<VariableExpressionSyntax>(Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Return.Expression);
    }

    /// <summary>
    /// A0 §11.2 names this case: a zero-width <c>RETURN</c> plus a <c>MissingExpression</c> body, with
    /// <c>DAXP1080</c> as the WHOLE story — the body carries no additional <c>DAXP1001</c>.
    /// </summary>
    [Fact]
    public void G_P_VAR_006_a_missing_return_is_inserted_with_one_diagnostic()
    {
        var tree = Golden("VAR a = 1", """
            ExpressionCompilationUnit [0..9)
              VariableExpression [0..9)
                VariableDeclaration [0..9)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  LiteralExpression [8..9)
                    IntegerLiteralToken [8..9) "1"
                ReturnClause [9..9)
                  ReturnKeyword [9..9) MISSING
                  MissingExpression [9..9) RECOVERY
              EndOfFileToken [9..9)
            """,
            Err(DaxParserDiagnosticCodes.ReturnExpected, 9, 0));

        var returnClause = Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Return;
        Assert.True(returnClause.ReturnKeyword.IsMissing);
        Assert.IsType<MissingExpressionSyntax>(returnClause.Expression);
    }

    [Fact]
    public void G_P_VAR_007_a_missing_variable_name()
    {
        var tree = Golden("VAR = 1 RETURN 1", """
            ExpressionCompilationUnit [0..16)
              VariableExpression [0..16)
                VariableDeclaration [0..7)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..4) MISSING
                  EqualsToken [4..5) "="
                  LiteralExpression [6..7)
                    IntegerLiteralToken [6..7) "1"
                ReturnClause [8..16)
                  ReturnKeyword [8..14) "RETURN"
                  LiteralExpression [15..16)
                    IntegerLiteralToken [15..16) "1"
              EndOfFileToken [16..16)
            """,
            Err(DaxParserDiagnosticCodes.VariableNameExpected, 4, 0));

        Assert.True(Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations[0].Identifier.IsMissing);
    }

    [Fact]
    public void G_P_VAR_008_a_missing_equals()
    {
        var tree = Golden("VAR a 1 RETURN a", """
            ExpressionCompilationUnit [0..16)
              VariableExpression [0..16)
                VariableDeclaration [0..7)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..6) MISSING
                  LiteralExpression [6..7)
                    IntegerLiteralToken [6..7) "1"
                ReturnClause [8..16)
                  ReturnKeyword [8..14) "RETURN"
                  IdentifierName [15..16)
                    IdentifierToken [15..16) "a"
              EndOfFileToken [16..16)
            """,
            Err(DaxParserDiagnosticCodes.VariableEqualsExpected, 6, 0));

        Assert.True(Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations[0].EqualsToken.IsMissing);
    }

    /// <summary>
    /// A0-language-scope §3.4 puts delimited and dotted variable names OUT. The offending run becomes
    /// skipped-token trivia with one <c>DAXP1083</c> over the run, the slot takes a missing identifier, and
    /// round-trip survives. <c>DAXP1081</c> is for a name that is genuinely absent, so the two never both
    /// fire.
    /// </summary>
    [Fact]
    public void G_P_VAR_009_a_quoted_variable_name_is_rejected_and_preserved()
    {
        var tree = Golden("VAR 'a' = 1 RETURN 1", """
            ExpressionCompilationUnit [0..20)
              VariableExpression [0..20)
                VariableDeclaration [0..11)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..4) MISSING
                  EqualsToken [8..9) "="
                    lead SkippedTokensTrivia [4..8)
                      SkippedTokens [4..7) RECOVERY
                        QuotedIdentifierToken [4..7) "'a'"
                  LiteralExpression [10..11)
                    IntegerLiteralToken [10..11) "1"
                ReturnClause [12..20)
                  ReturnKeyword [12..18) "RETURN"
                  LiteralExpression [19..20)
                    IntegerLiteralToken [19..20) "1"
              EndOfFileToken [20..20)
            """,
            Err(DaxParserDiagnosticCodes.VariableNameCannotBeDelimited, 4, 3));

        Assert.DoesNotContain(DaxParserDiagnosticCodes.VariableNameExpected, Codes(tree));
        Assert.True(Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations[0].Identifier.IsMissing);
    }

    // A0-language-scope §3.4: the `__` prefix is an ordinary legal identifier.
    [Fact]
    public void G_P_VAR_010_a_double_underscore_name_is_legal()
    {
        var tree = Golden("VAR __a = 1 RETURN __a", """
            ExpressionCompilationUnit [0..22)
              VariableExpression [0..22)
                VariableDeclaration [0..11)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..7) "__a"
                  EqualsToken [8..9) "="
                  LiteralExpression [10..11)
                    IntegerLiteralToken [10..11) "1"
                ReturnClause [12..22)
                  ReturnKeyword [12..18) "RETURN"
                  IdentifierName [19..22)
                    IdentifierToken [19..22) "__a"
              EndOfFileToken [22..22)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal("__a", Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations[0].Identifier.Text);
    }
}
