using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.2, <c>G-P-NAME-001</c>..<c>-019</c>: the name grammar (§4.4), greedy qualification on TOKEN
/// adjacency (§5.4), the dotted-name rules, and the bare quoted name (§4.4, review finding 3).
/// </summary>
public class ParserNameTests
{
    [Fact]
    public void G_P_NAME_001_identifier_qualifies_a_bracketed_name()
    {
        var tree = Golden("Sales[Amount]", """
            ExpressionCompilationUnit [0..13)
              QualifiedName [0..13)
                IdentifierName [0..5)
                  IdentifierToken [0..5) "Sales"
                BracketedName [5..13)
                  BracketedIdentifierToken [5..13) "[Amount]"
              EndOfFileToken [13..13)
            """);

        var qualified = Assert.IsType<QualifiedNameSyntax>(Expr(tree));
        Assert.IsType<IdentifierNameSyntax>(qualified.Qualifier);
        Assert.IsType<BracketedNameSyntax>(qualified.Name);
    }

    [Fact]
    public void G_P_NAME_002_quoted_name_qualifies_a_bracketed_name()
    {
        var tree = Golden("'Sales Table'[Amount]", """
            ExpressionCompilationUnit [0..21)
              QualifiedName [0..21)
                QuotedName [0..13)
                  QuotedIdentifierToken [0..13) "'Sales Table'"
                BracketedName [13..21)
                  BracketedIdentifierToken [13..21) "[Amount]"
              EndOfFileToken [21..21)
            """);

        Assert.IsType<QuotedNameSyntax>(Assert.IsType<QualifiedNameSyntax>(Expr(tree)).Qualifier);
    }

    // A2 §4.4: names ARE expressions; there is no name-expression wrapper node.
    [Fact]
    public void G_P_NAME_003_a_bare_bracketed_name_is_an_expression()
    {
        var tree = Golden("[Amount]", """
            ExpressionCompilationUnit [0..8)
              BracketedName [0..8)
                BracketedIdentifierToken [0..8) "[Amount]"
              EndOfFileToken [8..8)
            """);

        Assert.IsType<BracketedNameSyntax>(Expr(tree));
        Assert.Empty(tree.GetDiagnostics());
    }

    // A2 §5.4: adjacency is TOKEN adjacency. Whitespace does not break qualification.
    [Fact]
    public void G_P_NAME_004_whitespace_does_not_break_qualification()
    {
        var tree = Golden("Sales   [Amount]", """
            ExpressionCompilationUnit [0..16)
              QualifiedName [0..16)
                IdentifierName [0..5)
                  IdentifierToken [0..5) "Sales"
                BracketedName [8..16)
                  BracketedIdentifierToken [8..16) "[Amount]"
              EndOfFileToken [16..16)
            """);

        Assert.IsType<QualifiedNameSyntax>(Expr(tree));
    }

    [Fact]
    public void G_P_NAME_005_a_newline_does_not_break_qualification()
    {
        var tree = Golden("Sales\n[Amount]", """
            ExpressionCompilationUnit [0..14)
              QualifiedName [0..14)
                IdentifierName [0..5)
                  IdentifierToken [0..5) "Sales"
                BracketedName [6..14)
                  BracketedIdentifierToken [6..14) "[Amount]"
              EndOfFileToken [14..14)
            """);

        Assert.IsType<QualifiedNameSyntax>(Expr(tree));
    }

    [Fact]
    public void G_P_NAME_006_comment_trivia_does_not_break_qualification_and_is_preserved()
    {
        var tree = Golden("Sales /*c*/ [Amount]", """
            ExpressionCompilationUnit [0..20)
              QualifiedName [0..20)
                IdentifierName [0..5)
                  IdentifierToken [0..5) "Sales"
                BracketedName [12..20)
                  BracketedIdentifierToken [12..20) "[Amount]"
              EndOfFileToken [20..20)
            """);

        var qualified = Assert.IsType<QualifiedNameSyntax>(Expr(tree));

        // The comment survives as trivia on a token, which is the half a shape-only render cannot show.
        var trivia = qualified.Name.Identifier.LeadingTrivia.Concat(
            ((IdentifierNameSyntax)qualified.Qualifier).Identifier.TrailingTrivia).ToList();
        Assert.Contains(trivia, t => t.Kind == SyntaxKind.DelimitedCommentTrivia && t.Text == "/*c*/");
    }

    [Fact]
    public void G_P_NAME_007_a_three_segment_dotted_call()
    {
        var tree = Golden("INFO.VIEW.MEASURES()", """
            ExpressionCompilationUnit [0..20)
              CallExpression [0..20)
                DottedName [0..18)
                  IdentifierName [0..4)
                    IdentifierToken [0..4) "INFO"
                  DotToken [4..5) "."
                  IdentifierName [5..9)
                    IdentifierToken [5..9) "VIEW"
                  DotToken [9..10) "."
                  IdentifierName [10..18)
                    IdentifierToken [10..18) "MEASURES"
                ArgumentList [18..20)
                  OpenParenToken [18..19) "("
                  CloseParenToken [19..20) ")"
              EndOfFileToken [20..20)
            """);

        var call = Assert.IsType<CallExpressionSyntax>(Expr(tree));
        var dotted = Assert.IsType<DottedNameSyntax>(call.Name);
        Assert.Equal(3, dotted.Segments.Count);
        Assert.Equal(2, dotted.Segments.SeparatorCount);
        Assert.Empty(call.ArgumentList.Arguments);
    }

    [Fact]
    public void G_P_NAME_008_a_daxlib_style_dotted_call()
    {
        var tree = Golden("DaxLib.Format.Pct(1)", """
            ExpressionCompilationUnit [0..20)
              CallExpression [0..20)
                DottedName [0..17)
                  IdentifierName [0..6)
                    IdentifierToken [0..6) "DaxLib"
                  DotToken [6..7) "."
                  IdentifierName [7..13)
                    IdentifierToken [7..13) "Format"
                  DotToken [13..14) "."
                  IdentifierName [14..17)
                    IdentifierToken [14..17) "Pct"
                ArgumentList [17..20)
                  OpenParenToken [17..18) "("
                  Argument [18..19)
                    LiteralExpression [18..19)
                      IntegerLiteralToken [18..19) "1"
                  CloseParenToken [19..20) ")"
              EndOfFileToken [20..20)
            """);

        Assert.Equal(3, Assert.IsType<DottedNameSyntax>(Assert.IsType<CallExpressionSyntax>(Expr(tree)).Name).Segments.Count);
    }

    // A2 §4.4: a dotted name not immediately followed by '(' is DAXP1040, spanning the DottedName NODE.
    [Fact]
    public void G_P_NAME_009_a_dotted_name_that_is_not_called()
    {
        var tree = Golden("INFO.VIEW.MEASURES", """
            ExpressionCompilationUnit [0..18)
              DottedName [0..18)
                IdentifierName [0..4)
                  IdentifierToken [0..4) "INFO"
                DotToken [4..5) "."
                IdentifierName [5..9)
                  IdentifierToken [5..9) "VIEW"
                DotToken [9..10) "."
                IdentifierName [10..18)
                  IdentifierToken [10..18) "MEASURES"
              EndOfFileToken [18..18)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameNotCallable, 0, 18));

        Assert.IsType<DottedNameSyntax>(Expr(tree));
    }

    /// <summary>
    /// A2 §11.2 as corrected by §14 defect 8: <c>A..B</c> carries BOTH <c>DAXP1041</c> (an empty segment)
    /// and <c>DAXP1040</c> (a dotted name that is not called). The two §4.4 rules apply independently and
    /// neither is an echo of the other. Both dots are retained (A1 §3.4).
    /// </summary>
    [Fact]
    public void G_P_NAME_010_an_empty_middle_segment()
    {
        var tree = Golden("A..B", """
            ExpressionCompilationUnit [0..4)
              DottedName [0..4)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "A"
                DotToken [1..2) "."
                IdentifierName [2..2)
                  IdentifierToken [2..2) MISSING
                DotToken [2..3) "."
                IdentifierName [3..4)
                  IdentifierToken [3..4) "B"
              EndOfFileToken [4..4)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameNotCallable, 0, 4),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 2, 0));

        var dotted = Assert.IsType<DottedNameSyntax>(Expr(tree));
        Assert.Equal(3, dotted.Segments.Count);
        Assert.True(dotted.Segments[1].Identifier.IsMissing);
        Assert.Equal(2, dotted.Segments.SeparatorCount);
    }

    [Fact]
    public void G_P_NAME_011_a_missing_trailing_segment()
    {
        var tree = Golden("A.", """
            ExpressionCompilationUnit [0..2)
              DottedName [0..2)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "A"
                DotToken [1..2) "."
                IdentifierName [2..2)
                  IdentifierToken [2..2) MISSING
              EndOfFileToken [2..2)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameNotCallable, 0, 2),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 2, 0));

        Assert.True(Assert.IsType<DottedNameSyntax>(Expr(tree)).Segments[1].Identifier.IsMissing);
    }

    [Fact]
    public void G_P_NAME_012_a_missing_leading_segment()
    {
        var tree = Golden(".A", """
            ExpressionCompilationUnit [0..2)
              DottedName [0..2)
                IdentifierName [0..0)
                  IdentifierToken [0..0) MISSING
                DotToken [0..1) "."
                IdentifierName [1..2)
                  IdentifierToken [1..2) "A"
              EndOfFileToken [2..2)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameNotCallable, 0, 2),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 0, 0));

        Assert.True(Assert.IsType<DottedNameSyntax>(Expr(tree)).Segments[0].Identifier.IsMissing);
    }

    // A1 §3.4 already consumed `.5` as a decimal literal, so no dotted-name conflict exists (A2 §4.4).
    [Fact]
    public void G_P_NAME_013_dot_five_is_a_decimal_literal()
    {
        var tree = Golden(".5", """
            ExpressionCompilationUnit [0..2)
              LiteralExpression [0..2)
                DecimalLiteralToken [0..2) ".5"
              EndOfFileToken [2..2)
            """);

        Assert.Equal(SyntaxKind.DecimalLiteralToken, Assert.IsType<LiteralExpressionSyntax>(Expr(tree)).Token.Kind);
        Assert.Empty(tree.GetDiagnostics());
    }

    // A2 §4.4: the typed structure survives and the binder is told plainly that it is invalid.
    [Fact]
    public void G_P_NAME_014_a_dotted_name_cannot_qualify_a_bracketed_name()
    {
        var tree = Golden("A.B[C]", """
            ExpressionCompilationUnit [0..6)
              QualifiedName [0..6)
                DottedName [0..3)
                  IdentifierName [0..1)
                    IdentifierToken [0..1) "A"
                  DotToken [1..2) "."
                  IdentifierName [2..3)
                    IdentifierToken [2..3) "B"
                BracketedName [3..6)
                  BracketedIdentifierToken [3..6) "[C]"
              EndOfFileToken [6..6)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameCannotQualify, 0, 3));

        Assert.IsType<DottedNameSyntax>(Assert.IsType<QualifiedNameSyntax>(Expr(tree)).Qualifier);
    }

    // A2 §4.5: the call SHAPE is retained (A0 §11.1 goal 3) rather than degenerating into two fragments.
    [Fact]
    public void G_P_NAME_015_a_quoted_callable_name_keeps_the_call_shape()
    {
        var tree = Golden("'Table'(1)", """
            ExpressionCompilationUnit [0..10)
              CallExpression [0..10)
                QuotedName [0..7)
                  QuotedIdentifierToken [0..7) "'Table'"
                ArgumentList [7..10)
                  OpenParenToken [7..8) "("
                  Argument [8..9)
                    LiteralExpression [8..9)
                      IntegerLiteralToken [8..9) "1"
                  CloseParenToken [9..10) ")"
              EndOfFileToken [10..10)
            """,
            Err(DaxParserDiagnosticCodes.NotCallable, 0, 7));

        var call = Assert.IsType<CallExpressionSyntax>(Expr(tree));
        Assert.IsType<QuotedNameSyntax>(call.Name);
        Assert.Single(call.ArgumentList.Arguments);
    }

    // A2 §4.3 / A0-language-scope §3.3: TRUE, FALSE, BLANK are IdentifierTokens, never literal nodes.
    [Theory]
    [InlineData("TRUE()", 4)]
    [InlineData("FALSE()", 5)]
    [InlineData("BLANK()", 5)]
    public void G_P_NAME_016_true_false_blank_are_calls_over_identifier_names(string source, int nameLength)
    {
        var tree = Golden(source, $"""
            ExpressionCompilationUnit [0..{source.Length})
              CallExpression [0..{source.Length})
                IdentifierName [0..{nameLength})
                  IdentifierToken [0..{nameLength}) "{source[..nameLength]}"
                ArgumentList [{nameLength}..{source.Length})
                  OpenParenToken [{nameLength}..{nameLength + 1}) "("
                  CloseParenToken [{nameLength + 1}..{source.Length}) ")"
              EndOfFileToken [{source.Length}..{source.Length})
            """);

        var call = Assert.IsType<CallExpressionSyntax>(Expr(tree));
        Assert.IsType<IdentifierNameSyntax>(call.Name);
        Assert.Empty(tree.GetDiagnostics());
        Assert.DoesNotContain(tree.Root.DescendantTokens(), t => t.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.StringLiteralToken);
    }

    /// <summary>
    /// The vendored keyword-channel regression (A0-greentree-results §8): <c>Date</c> is not a DAX keyword
    /// at all, so it stays an <c>IdentifierToken</c> and <c>Date[Key]</c> is an ordinary qualified name.
    /// </summary>
    [Fact]
    public void G_P_NAME_017_date_is_an_ordinary_name()
    {
        var tree = Golden("Date[Key]", """
            ExpressionCompilationUnit [0..9)
              QualifiedName [0..9)
                IdentifierName [0..4)
                  IdentifierToken [0..4) "Date"
                BracketedName [4..9)
                  BracketedIdentifierToken [4..9) "[Key]"
              EndOfFileToken [9..9)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.IsType<IdentifierNameSyntax>(Assert.IsType<QualifiedNameSyntax>(Expr(tree)).Qualifier);
    }

    /// <summary>
    /// A2 §4.4 (review finding 3): a bare quoted name is a name expression in its own right. This is the
    /// exact form the shipped calendars lane authors, so it must not be a fragment and must not be an error.
    /// </summary>
    [Fact]
    public void G_P_NAME_018_a_bare_quoted_calendar_reference_is_a_standalone_quoted_name()
    {
        var tree = Golden("TOTALYTD([Sales], 'Fiscal')", """
            ExpressionCompilationUnit [0..27)
              CallExpression [0..27)
                IdentifierName [0..8)
                  IdentifierToken [0..8) "TOTALYTD"
                ArgumentList [8..27)
                  OpenParenToken [8..9) "("
                  Argument [9..16)
                    BracketedName [9..16)
                      BracketedIdentifierToken [9..16) "[Sales]"
                  CommaToken [16..17) ","
                  Argument [18..26)
                    QuotedName [18..26)
                      QuotedIdentifierToken [18..26) "'Fiscal'"
                  CloseParenToken [26..27) ")"
              EndOfFileToken [27..27)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Equal(2, arguments.Count);
        Assert.IsType<QuotedNameSyntax>(Assert.IsType<ArgumentSyntax>(arguments[1]).Expression);
    }

    [Fact]
    public void G_P_NAME_019_a_bare_quoted_name_at_the_root()
    {
        var tree = Golden("'Sales Table'", """
            ExpressionCompilationUnit [0..13)
              QuotedName [0..13)
                QuotedIdentifierToken [0..13) "'Sales Table'"
              EndOfFileToken [13..13)
            """);

        Assert.IsType<QuotedNameSyntax>(Expr(tree));
        Assert.Empty(tree.GetDiagnostics());
    }
}
