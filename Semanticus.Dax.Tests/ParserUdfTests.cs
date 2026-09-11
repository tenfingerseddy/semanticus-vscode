using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.6, <c>G-P-UDF-001</c>..<c>-022</c>: the model-UDF lambda-body root (§4.9) and the hint run
/// (§4.9.1). The hint rows are the ones that matter most: all six occupancy patterns occur in real model
/// files, so classification is by CLOSED VOCABULARY and the hints are one source-ordered list, never three
/// fixed slots (review finding 2).
/// </summary>
public class ParserUdfTests
{
    [Fact]
    public void G_P_UDF_001_an_empty_parameter_list()
    {
        var tree = GoldenUdf("() => 1", """
            UdfBodyCompilationUnit [0..7)
              LambdaExpression [0..7)
                ParameterList [0..2)
                  OpenParenToken [0..1) "("
                  CloseParenToken [1..2) ")"
                LambdaArrowToken [3..5) "=>"
                LiteralExpression [6..7)
                  IntegerLiteralToken [6..7) "1"
              EndOfFileToken [7..7)
            """);

        Assert.Empty(Lambda(tree).ParameterList.Parameters);
        Assert.Empty(tree.GetDiagnostics());
    }

    // A2 §5.6 row 1: an absent OPTIONAL NODE child is a null slot and the accessor returns null.
    [Fact]
    public void G_P_UDF_002_one_parameter_with_no_type_clause()
    {
        var tree = GoldenUdf("(x) => x", """
            UdfBodyCompilationUnit [0..8)
              LambdaExpression [0..8)
                ParameterList [0..3)
                  OpenParenToken [0..1) "("
                  Parameter [1..2)
                    IdentifierToken [1..2) "x"
                  CloseParenToken [2..3) ")"
                LambdaArrowToken [4..6) "=>"
                IdentifierName [7..8)
                  IdentifierToken [7..8) "x"
              EndOfFileToken [8..8)
            """);

        var parameter = Assert.Single(Lambda(tree).ParameterList.Parameters);
        Assert.Null(parameter.TypeClause);
        Assert.Null(parameter.Default);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_UDF_003_subtype_only()
    {
        var tree = GoldenUdf("(position: STRING) => position", """
            UdfBodyCompilationUnit [0..30)
              LambdaExpression [0..30)
                ParameterList [0..18)
                  OpenParenToken [0..1) "("
                  Parameter [1..17)
                    IdentifierToken [1..9) "position"
                    ParameterTypeClause [9..17)
                      ColonToken [9..10) ":"
                      SubtypeHint [11..17)
                        IdentifierToken [11..17) "STRING"
                  CloseParenToken [17..18) ")"
                LambdaArrowToken [19..21) "=>"
                IdentifierName [22..30)
                  IdentifierToken [22..30) "position"
              EndOfFileToken [30..30)
            """);

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.IsType<SubtypeHintSyntax>(Assert.Single(clause.Hints));
        Assert.Null(clause.TypeHint);
        Assert.NotNull(clause.SubtypeHint);
        Assert.Null(clause.PassingModeHint);
    }

    /// <summary>
    /// Mode only: the occupancy pattern that makes positional hint assignment impossible. Measured 10 times
    /// in the corpus (A2 §4.9.1).
    /// </summary>
    [Fact]
    public void G_P_UDF_004_passing_mode_only()
    {
        var tree = GoldenUdf("(startTime: EXPR) => startTime", """
            UdfBodyCompilationUnit [0..30)
              LambdaExpression [0..30)
                ParameterList [0..17)
                  OpenParenToken [0..1) "("
                  Parameter [1..16)
                    IdentifierToken [1..10) "startTime"
                    ParameterTypeClause [10..16)
                      ColonToken [10..11) ":"
                      PassingModeHint [12..16)
                        IdentifierToken [12..16) "EXPR"
                  CloseParenToken [16..17) ")"
                LambdaArrowToken [18..20) "=>"
                IdentifierName [21..30)
                  IdentifierToken [21..30) "startTime"
              EndOfFileToken [30..30)
            """);

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.IsType<PassingModeHintSyntax>(Assert.Single(clause.Hints));
        Assert.Null(clause.TypeHint);
        Assert.Null(clause.SubtypeHint);
        Assert.Empty(tree.GetDiagnostics());
    }

    [Fact]
    public void G_P_UDF_005_type_plus_mode()
    {
        var tree = GoldenUdf("(c: ANYREF EXPR) => 1", """
            UdfBodyCompilationUnit [0..21)
              LambdaExpression [0..21)
                ParameterList [0..16)
                  OpenParenToken [0..1) "("
                  Parameter [1..15)
                    IdentifierToken [1..2) "c"
                    ParameterTypeClause [2..15)
                      ColonToken [2..3) ":"
                      TypeHint [4..10)
                        IdentifierToken [4..10) "ANYREF"
                      PassingModeHint [11..15)
                        IdentifierToken [11..15) "EXPR"
                  CloseParenToken [15..16) ")"
                LambdaArrowToken [17..19) "=>"
                LiteralExpression [20..21)
                  IntegerLiteralToken [20..21) "1"
              EndOfFileToken [21..21)
            """);

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Collection(clause.Hints, h => Assert.IsType<TypeHintSyntax>(h), h => Assert.IsType<PassingModeHintSyntax>(h));
        Assert.Null(clause.SubtypeHint);
    }

    [Fact]
    public void G_P_UDF_006_type_plus_subtype()
    {
        var tree = GoldenUdf("(n: SCALAR INT64) => n", """
            UdfBodyCompilationUnit [0..22)
              LambdaExpression [0..22)
                ParameterList [0..17)
                  OpenParenToken [0..1) "("
                  Parameter [1..16)
                    IdentifierToken [1..2) "n"
                    ParameterTypeClause [2..16)
                      ColonToken [2..3) ":"
                      TypeHint [4..10)
                        IdentifierToken [4..10) "SCALAR"
                      SubtypeHint [11..16)
                        IdentifierToken [11..16) "INT64"
                  CloseParenToken [16..17) ")"
                LambdaArrowToken [18..20) "=>"
                IdentifierName [21..22)
                  IdentifierToken [21..22) "n"
              EndOfFileToken [22..22)
            """);

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Collection(clause.Hints, h => Assert.IsType<TypeHintSyntax>(h), h => Assert.IsType<SubtypeHintSyntax>(h));
        Assert.Null(clause.PassingModeHint);
    }

    [Fact]
    public void G_P_UDF_007_subtype_plus_mode()
    {
        var tree = GoldenUdf("(e: NUMERIC EXPR) => e", """
            UdfBodyCompilationUnit [0..22)
              LambdaExpression [0..22)
                ParameterList [0..17)
                  OpenParenToken [0..1) "("
                  Parameter [1..16)
                    IdentifierToken [1..2) "e"
                    ParameterTypeClause [2..16)
                      ColonToken [2..3) ":"
                      SubtypeHint [4..11)
                        IdentifierToken [4..11) "NUMERIC"
                      PassingModeHint [12..16)
                        IdentifierToken [12..16) "EXPR"
                  CloseParenToken [16..17) ")"
                LambdaArrowToken [18..20) "=>"
                IdentifierName [21..22)
                  IdentifierToken [21..22) "e"
              EndOfFileToken [22..22)
            """);

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Collection(clause.Hints, h => Assert.IsType<SubtypeHintSyntax>(h), h => Assert.IsType<PassingModeHintSyntax>(h));
        Assert.Null(clause.TypeHint);
    }

    /// <summary>
    /// A2 §4.9.1 rule 1 and §6.2 rule 3: <c>TABLE</c> arrives as a <c>TableKeyword</c> and is a REQUIRED
    /// terminal of <c>hint-word</c>. That is contextual grammar, not keyword admission, so it produces no
    /// diagnostic and its token kind is never rewritten.
    /// </summary>
    [Fact]
    public void G_P_UDF_008_table_is_a_required_hint_terminal()
    {
        var tree = GoldenUdf("(t: TABLE VAL) => t", """
            UdfBodyCompilationUnit [0..19)
              LambdaExpression [0..19)
                ParameterList [0..14)
                  OpenParenToken [0..1) "("
                  Parameter [1..13)
                    IdentifierToken [1..2) "t"
                    ParameterTypeClause [2..13)
                      ColonToken [2..3) ":"
                      TypeHint [4..9)
                        TableKeyword [4..9) "TABLE"
                      PassingModeHint [10..13)
                        IdentifierToken [10..13) "VAL"
                  CloseParenToken [13..14) ")"
                LambdaArrowToken [15..17) "=>"
                IdentifierName [18..19)
                  IdentifierToken [18..19) "t"
              EndOfFileToken [19..19)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Equal(SyntaxKind.TableKeyword, clause.TypeHint!.Token.Kind);
    }

    /// <summary>
    /// A2 §4.9.1 rule 4: an unrecognized word takes the first free category and receives <c>DAXP1063</c>
    /// (the only PR-1 Warning). The parameter survives, and the <c>DAXP1063</c> is precisely what tells the
    /// binder the node's kind is PROVISIONAL and must not be read as meaning.
    /// </summary>
    [Fact]
    public void G_P_UDF_009_an_unrecognized_hint_word_survives_as_a_provisional_node()
    {
        var tree = GoldenUdf("(x: WIDGET) => x", """
            UdfBodyCompilationUnit [0..16)
              LambdaExpression [0..16)
                ParameterList [0..11)
                  OpenParenToken [0..1) "("
                  Parameter [1..10)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..10)
                      ColonToken [2..3) ":"
                      TypeHint [4..10)
                        IdentifierToken [4..10) "WIDGET"
                  CloseParenToken [10..11) ")"
                LambdaArrowToken [12..14) "=>"
                IdentifierName [15..16)
                  IdentifierToken [15..16) "x"
              EndOfFileToken [16..16)
            """,
            Warn(DaxParserDiagnosticCodes.UnrecognizedParameterHint, 4, 6));

        var hint = Assert.Single(Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!.Hints);
        Assert.IsType<TypeHintSyntax>(hint);
        Assert.True(hint.ContainsDiagnostics);
        Assert.Equal(DaxDiagnosticSeverity.Warning, tree.GetDiagnostics()[0].Severity);
    }

    /// <summary>
    /// A2 §4.9.1 rule 5 and §8.4 rule 6: out-of-order words keep their kinds AND their source positions;
    /// only the FIRST out-of-order word is reported. Reordering would break round-trip, which is exactly why
    /// the three fixed slots were rejected.
    /// </summary>
    [Fact]
    public void G_P_UDF_010_out_of_order_hints_stay_in_source_order()
    {
        var tree = GoldenUdf("(x: EXPR ANYREF) => x", """
            UdfBodyCompilationUnit [0..21)
              LambdaExpression [0..21)
                ParameterList [0..16)
                  OpenParenToken [0..1) "("
                  Parameter [1..15)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..15)
                      ColonToken [2..3) ":"
                      PassingModeHint [4..8)
                        IdentifierToken [4..8) "EXPR"
                      TypeHint [9..15)
                        IdentifierToken [9..15) "ANYREF"
                  CloseParenToken [15..16) ")"
                LambdaArrowToken [17..19) "=>"
                IdentifierName [20..21)
                  IdentifierToken [20..21) "x"
              EndOfFileToken [21..21)
            """,
            Err(DaxParserDiagnosticCodes.ParameterHintsOutOfOrder, 9, 6));

        Assert.Equal("(x: EXPR ANYREF) => x", tree.Root.ToFullString());
        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Collection(clause.Hints, h => Assert.IsType<PassingModeHintSyntax>(h), h => Assert.IsType<TypeHintSyntax>(h));
        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.ParameterHintsOutOfOrder));
    }

    /// <summary>
    /// A2 §4.9.1 rule 6: a duplicated category stays its OWN node; the second and each later word carries
    /// the diagnostic, never the first. <c>SCALAR SCALAR</c> is two nodes, not one node plus a lost token —
    /// which the three fixed slots could not have represented at all.
    /// </summary>
    [Fact]
    public void G_P_UDF_011_a_duplicate_hint_stays_its_own_node()
    {
        var tree = GoldenUdf("(x: SCALAR SCALAR) => x", """
            UdfBodyCompilationUnit [0..23)
              LambdaExpression [0..23)
                ParameterList [0..18)
                  OpenParenToken [0..1) "("
                  Parameter [1..17)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..17)
                      ColonToken [2..3) ":"
                      TypeHint [4..10)
                        IdentifierToken [4..10) "SCALAR"
                      TypeHint [11..17)
                        IdentifierToken [11..17) "SCALAR"
                  CloseParenToken [17..18) ")"
                LambdaArrowToken [19..21) "=>"
                IdentifierName [22..23)
                  IdentifierToken [22..23) "x"
              EndOfFileToken [23..23)
            """,
            Err(DaxParserDiagnosticCodes.DuplicateParameterHint, 11, 6));

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Equal(2, clause.Hints.Count);
        Assert.All(clause.Hints, h => Assert.IsType<TypeHintSyntax>(h));
        Assert.Same(clause.Hints[0].Green, clause.TypeHint!.Green);      // derived = the FIRST of that kind
        Assert.False(clause.Hints[0].ContainsDiagnostics);               // never the first
        Assert.True(clause.Hints[1].ContainsDiagnostics);
    }

    /// <summary>
    /// A2 §4.9.1 rule 7 and §8.4 rules 4/6: the first three words are emitted, the remainder becomes
    /// skipped-token trivia with ONE <c>DAXP1064</c> spanning it. A, B and C are unclassified, so each also
    /// takes a <c>DAXP1063</c> under rule 4.
    /// </summary>
    [Fact]
    public void G_P_UDF_012_a_fourth_hint_word_is_skipped_as_one_run()
    {
        var tree = GoldenUdf("(x: A B C D) => x", """
            UdfBodyCompilationUnit [0..17)
              LambdaExpression [0..17)
                ParameterList [0..12)
                  OpenParenToken [0..1) "("
                  Parameter [1..9)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..9)
                      ColonToken [2..3) ":"
                      TypeHint [4..5)
                        IdentifierToken [4..5) "A"
                      SubtypeHint [6..7)
                        IdentifierToken [6..7) "B"
                      PassingModeHint [8..9)
                        IdentifierToken [8..9) "C"
                  CloseParenToken [11..12) ")"
                    lead SkippedTokensTrivia [10..11)
                      SkippedTokens [10..11) RECOVERY
                        IdentifierToken [10..11) "D"
                LambdaArrowToken [13..15) "=>"
                IdentifierName [16..17)
                  IdentifierToken [16..17) "x"
              EndOfFileToken [17..17)
            """,
            Warn(DaxParserDiagnosticCodes.UnrecognizedParameterHint, 4, 1),
            Warn(DaxParserDiagnosticCodes.UnrecognizedParameterHint, 6, 1),
            Warn(DaxParserDiagnosticCodes.UnrecognizedParameterHint, 8, 1),
            Err(DaxParserDiagnosticCodes.TooManyParameterHints, 10, 1));

        Assert.Equal(3, Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!.Hints.Count);
        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.TooManyParameterHints));
    }

    // A2 §4.9.1 rule 8: an empty run is an EMPTY Hints list plus DAXP1062, zero width immediately after ':'.
    [Fact]
    public void G_P_UDF_013_a_colon_with_no_hint_run()
    {
        var tree = GoldenUdf("(x: ) => x", """
            UdfBodyCompilationUnit [0..10)
              LambdaExpression [0..10)
                ParameterList [0..5)
                  OpenParenToken [0..1) "("
                  Parameter [1..3)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..3)
                      ColonToken [2..3) ":"
                  CloseParenToken [4..5) ")"
                LambdaArrowToken [6..8) "=>"
                IdentifierName [9..10)
                  IdentifierToken [9..10) "x"
              EndOfFileToken [10..10)
            """,
            Err(DaxParserDiagnosticCodes.ParameterHintExpected, 3, 0));

        var clause = Assert.Single(Lambda(tree).ParameterList.Parameters).TypeClause!;
        Assert.Empty(clause.Hints);
        Assert.Null(clause.TypeHint);
        Assert.Null(clause.SubtypeHint);
        Assert.Null(clause.PassingModeHint);
    }

    [Fact]
    public void G_P_UDF_014_a_default_value_with_no_type_clause()
    {
        var tree = GoldenUdf("(x = 1) => x", """
            UdfBodyCompilationUnit [0..12)
              LambdaExpression [0..12)
                ParameterList [0..7)
                  OpenParenToken [0..1) "("
                  Parameter [1..6)
                    IdentifierToken [1..2) "x"
                    DefaultValueClause [3..6)
                      EqualsToken [3..4) "="
                      LiteralExpression [5..6)
                        IntegerLiteralToken [5..6) "1"
                  CloseParenToken [6..7) ")"
                LambdaArrowToken [8..10) "=>"
                IdentifierName [11..12)
                  IdentifierToken [11..12) "x"
              EndOfFileToken [12..12)
            """);

        var parameter = Assert.Single(Lambda(tree).ParameterList.Parameters);
        Assert.Null(parameter.TypeClause);
        Assert.NotNull(parameter.Default);
        Assert.Empty(tree.GetDiagnostics());
    }

    // A0-language-scope §5.1: optional-before-required PARSES; the objection is semantic, not syntactic.
    [Fact]
    public void G_P_UDF_015_a_default_after_a_type_clause_and_a_later_required_parameter()
    {
        var tree = GoldenUdf("(x: INT64 = 1, y) => x + y", """
            UdfBodyCompilationUnit [0..26)
              LambdaExpression [0..26)
                ParameterList [0..17)
                  OpenParenToken [0..1) "("
                  Parameter [1..13)
                    IdentifierToken [1..2) "x"
                    ParameterTypeClause [2..9)
                      ColonToken [2..3) ":"
                      SubtypeHint [4..9)
                        IdentifierToken [4..9) "INT64"
                    DefaultValueClause [10..13)
                      EqualsToken [10..11) "="
                      LiteralExpression [12..13)
                        IntegerLiteralToken [12..13) "1"
                  CommaToken [13..14) ","
                  Parameter [15..16)
                    IdentifierToken [15..16) "y"
                  CloseParenToken [16..17) ")"
                LambdaArrowToken [18..20) "=>"
                BinaryExpression [21..26)
                  IdentifierName [21..22)
                    IdentifierToken [21..22) "x"
                  PlusToken [23..24) "+"
                  IdentifierName [25..26)
                    IdentifierToken [25..26) "y"
              EndOfFileToken [26..26)
            """);

        Assert.Empty(tree.GetDiagnostics());
        var parameters = Lambda(tree).ParameterList.Parameters;
        Assert.Equal(2, parameters.Count);
        Assert.NotNull(parameters[0].TypeClause);
        Assert.NotNull(parameters[0].Default);
        Assert.Null(parameters[1].Default);
    }

    // A0 §11.2 names this case: a zero-width '=>' plus DAXP1060.
    [Fact]
    public void G_P_UDF_016_a_missing_arrow_is_inserted()
    {
        var tree = GoldenUdf("(x) 1", """
            UdfBodyCompilationUnit [0..5)
              LambdaExpression [0..5)
                ParameterList [0..3)
                  OpenParenToken [0..1) "("
                  Parameter [1..2)
                    IdentifierToken [1..2) "x"
                  CloseParenToken [2..3) ")"
                LambdaArrowToken [4..4) MISSING
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "1"
              EndOfFileToken [5..5)
            """,
            Err(DaxParserDiagnosticCodes.LambdaArrowExpected, 4, 0));

        Assert.True(Lambda(tree).ArrowToken.IsMissing);
    }

    [Fact]
    public void G_P_UDF_017_a_trailing_comma_yields_a_missing_parameter_name()
    {
        var tree = GoldenUdf("(x,) => x", """
            UdfBodyCompilationUnit [0..9)
              LambdaExpression [0..9)
                ParameterList [0..4)
                  OpenParenToken [0..1) "("
                  Parameter [1..2)
                    IdentifierToken [1..2) "x"
                  CommaToken [2..3) ","
                  Parameter [3..3)
                    IdentifierToken [3..3) MISSING
                  CloseParenToken [3..4) ")"
                LambdaArrowToken [5..7) "=>"
                IdentifierName [8..9)
                  IdentifierToken [8..9) "x"
              EndOfFileToken [9..9)
            """,
            Err(DaxParserDiagnosticCodes.ParameterNameExpected, 3, 0));

        var parameters = Lambda(tree).ParameterList.Parameters;
        Assert.Equal(2, parameters.Count);
        Assert.True(parameters[1].Identifier.IsMissing);
    }

    /// <summary>
    /// A2 §4.9: a lambda is a ROOT-ONLY construct. Parsed as an expression, the <c>=&gt;</c> is unexpected
    /// input with its own <c>DAXP1067</c>, and (as corrected by §14 defect 9) the trailing <c>x</c> is
    /// unexpected on its own account and collapses into its own <c>DAXP1005</c> run.
    /// </summary>
    [Fact]
    public void G_P_UDF_018_an_arrow_inside_an_expression_never_forms_a_lambda()
    {
        var tree = Golden("1 + ((x) => x)", """
            ExpressionCompilationUnit [0..14)
              BinaryExpression [0..14)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                ParenthesizedExpression [4..14)
                  OpenParenToken [4..5) "("
                  ParenthesizedExpression [5..8)
                    OpenParenToken [5..6) "("
                    IdentifierName [6..7)
                      IdentifierToken [6..7) "x"
                    CloseParenToken [7..8) ")"
                  CloseParenToken [13..14) ")"
                    lead SkippedTokensTrivia [9..13)
                      SkippedTokens [9..13) RECOVERY
                        LambdaArrowToken [9..11) "=>"
                        IdentifierToken [12..13) "x"
              EndOfFileToken [14..14)
            """,
            Err(DaxParserDiagnosticCodes.LambdaOnlyAtUdfRoot, 9, 2),
            Err(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, 12, 1));

        // The arrow survives only as skipped source; it is not a surface token and forms no lambda.
        Assert.DoesNotContain(tree.Root.DescendantTokens(), t => t.Kind == SyntaxKind.LambdaArrowToken);
        Assert.Contains(AllTokens(tree.Root), t => t.Kind == SyntaxKind.LambdaArrowToken);
        Assert.DoesNotContain(Render(tree.Root), "LambdaExpression", StringComparison.Ordinal);
    }

    // Real corpus form: a line comment before the parameter list. The root still finds it.
    [Fact]
    public void G_P_UDF_019_a_leading_line_comment_before_the_parameter_list()
    {
        var tree = GoldenUdf("// doc\n(x) => x", """
            UdfBodyCompilationUnit [7..15)
              LambdaExpression [7..15)
                ParameterList [7..10)
                  OpenParenToken [7..8) "("
                  Parameter [8..9)
                    IdentifierToken [8..9) "x"
                  CloseParenToken [9..10) ")"
                LambdaArrowToken [11..13) "=>"
                IdentifierName [14..15)
                  IdentifierToken [14..15) "x"
              EndOfFileToken [15..15)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.Contains(Lambda(tree).ParameterList.OpenParenToken.LeadingTrivia,
            t => t.Kind == SyntaxKind.SlashSlashCommentTrivia);
    }

    // A0-language-scope §5.2: the JSDoc header is DocumentationCommentTrivia and is NOT parsed.
    [Fact]
    public void G_P_UDF_020_a_documentation_comment_header_is_unparsed_trivia()
    {
        const string source = "/// <summary>\n/// Adds one.\n/// </summary>\n/// <param name=\"x\">the value</param>\n(x) => x + 1";

        var tree = GoldenUdf(source, """
            UdfBodyCompilationUnit [81..93)
              LambdaExpression [81..93)
                ParameterList [81..84)
                  OpenParenToken [81..82) "("
                  Parameter [82..83)
                    IdentifierToken [82..83) "x"
                  CloseParenToken [83..84) ")"
                LambdaArrowToken [85..87) "=>"
                BinaryExpression [88..93)
                  IdentifierName [88..89)
                    IdentifierToken [88..89) "x"
                  PlusToken [90..91) "+"
                  LiteralExpression [92..93)
                    IntegerLiteralToken [92..93) "1"
              EndOfFileToken [93..93)
            """);

        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal(4, Lambda(tree).ParameterList.OpenParenToken.LeadingTrivia
            .Count(t => t.Kind == SyntaxKind.DocumentationCommentTrivia));
    }

    /// <summary>
    /// A2 §4.9 (review finding 8): a missing opening <c>(</c> inserts a zero-width one, emits exactly ONE
    /// <c>DAXP1068</c>, and parses the list FROM THAT SAME TOKEN — nothing skipped, no lookahead.
    /// </summary>
    [Fact]
    public void G_P_UDF_021_a_missing_open_paren_is_inserted_without_skipping()
    {
        var tree = GoldenUdf("x) => x", """
            UdfBodyCompilationUnit [0..7)
              LambdaExpression [0..7)
                ParameterList [0..2)
                  OpenParenToken [0..0) MISSING
                  Parameter [0..1)
                    IdentifierToken [0..1) "x"
                  CloseParenToken [1..2) ")"
                LambdaArrowToken [3..5) "=>"
                IdentifierName [6..7)
                  IdentifierToken [6..7) "x"
              EndOfFileToken [7..7)
            """,
            Err(DaxParserDiagnosticCodes.ParameterListOpenParenExpected, 0, 0));

        var list = Lambda(tree).ParameterList;
        Assert.True(list.OpenParenToken.IsMissing);
        Assert.Equal("x", Assert.Single(list.Parameters).Identifier.Text);
        Assert.False(list.CloseParenToken.IsMissing);
    }

    // A2 §4.9: the ordinary missing-')' rule then applies ON TOP, one each.
    [Fact]
    public void G_P_UDF_022_a_missing_open_paren_and_a_missing_close_paren()
    {
        var tree = GoldenUdf("x => x", """
            UdfBodyCompilationUnit [0..6)
              LambdaExpression [0..6)
                ParameterList [0..1)
                  OpenParenToken [0..0) MISSING
                  Parameter [0..1)
                    IdentifierToken [0..1) "x"
                  CloseParenToken [2..2) MISSING
                LambdaArrowToken [2..4) "=>"
                IdentifierName [5..6)
                  IdentifierToken [5..6) "x"
              EndOfFileToken [6..6)
            """,
            Err(DaxParserDiagnosticCodes.ParameterListOpenParenExpected, 0, 0),
            Err(DaxParserDiagnosticCodes.CloseParenExpected, 2, 0));

        var lambda = Lambda(tree);
        Assert.True(lambda.ParameterList.OpenParenToken.IsMissing);
        Assert.True(lambda.ParameterList.CloseParenToken.IsMissing);
        Assert.Equal("x", Assert.Single(lambda.ParameterList.Parameters).Identifier.Text);
        Assert.Equal(SyntaxKind.LambdaArrowToken, lambda.ArrowToken.Kind);
        Assert.False(lambda.ArrowToken.IsMissing);
    }
}
