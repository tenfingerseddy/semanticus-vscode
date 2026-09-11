using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.7, <c>G-P-KW-001</c>..<c>-017</c>: the §6.3 position table. A reserved keyword is never a clean
/// bare name; the two declaration-name slots RETAIN the token with an Error; <c>TABLE</c> in the hint
/// position is contextual grammar; and a rejected keyword is never silently dropped, so every row also
/// asserts exact round-trip.
/// </summary>
public class ParserKeywordTests
{
    /// <summary>
    /// P1 rejection, the shape every row-1 keyword takes: one <c>DAXP1043</c> spanning the keyword, the
    /// keyword to skipped-token trivia, and the residue parsed on its own merits.
    /// </summary>
    [Fact]
    public void G_P_KW_001_total_is_rejected_at_p1_and_the_bracketed_name_survives()
    {
        var tree = Golden("TOTAL[Amount]", """
            ExpressionCompilationUnit [5..13)
              BracketedName [5..13)
                BracketedIdentifierToken [5..13) "[Amount]"
                  lead SkippedTokensTrivia [0..5)
                    SkippedTokens [0..5) RECOVERY
                      TotalKeyword [0..5) "TOTAL"
              EndOfFileToken [13..13)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 5));

        Assert.IsType<BracketedNameSyntax>(Expr(tree));
        Assert.Equal("TOTAL[Amount]", tree.Root.ToFullString());
        Assert.Equal(SyntaxKind.TotalKeyword, AllTokens(tree.Root).First().Kind);   // the kind is NEVER rewritten
    }

    /// <summary>
    /// A2 §6.3 note "P5 / P6 keep the shape": one input, one spelling, TWO positions, two outcomes. The
    /// declaration slot retains <c>Total</c> as a <c>TotalKeyword</c> with <c>DAXP1084</c>; the reference in
    /// the return body is a P1 position and is rejected with <c>DAXP1043</c>.
    /// </summary>
    [Fact]
    public void G_P_KW_002_a_keyword_is_retained_in_the_declaration_slot_and_rejected_as_a_reference()
    {
        var tree = Golden("VAR Total = 1 RETURN Total", """
            ExpressionCompilationUnit [0..26)
              VariableExpression [0..20)
                VariableDeclaration [0..13)
                  VarKeyword [0..3) "VAR"
                  TotalKeyword [4..9) "Total"
                  EqualsToken [10..11) "="
                  LiteralExpression [12..13)
                    IntegerLiteralToken [12..13) "1"
                ReturnClause [14..20)
                  ReturnKeyword [14..20) "RETURN"
                  MissingExpression [21..21) RECOVERY
              EndOfFileToken [26..26)
                lead SkippedTokensTrivia [21..26)
                  SkippedTokens [21..26) RECOVERY
                    TotalKeyword [21..26) "Total"
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsVariableName, 4, 5),
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 21, 5));

        var declaration = Assert.IsType<VariableExpressionSyntax>(Expr(tree)).Declarations[0];
        Assert.Equal(SyntaxKind.TotalKeyword, declaration.Identifier.Kind);   // retained, kind not rewritten
        Assert.False(declaration.Identifier.IsMissing);
        Assert.Equal("VAR Total = 1 RETURN Total", tree.Root.ToFullString());
    }

    [Fact]
    public void G_P_KW_003_group_is_rejected_at_p1()
    {
        var tree = Golden("GROUP[Id]", """
            ExpressionCompilationUnit [5..9)
              BracketedName [5..9)
                BracketedIdentifierToken [5..9) "[Id]"
                  lead SkippedTokensTrivia [0..5)
                    SkippedTokens [0..5) RECOVERY
                      GroupKeyword [0..5) "GROUP"
              EndOfFileToken [9..9)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 5));

        Assert.IsType<BracketedNameSyntax>(Expr(tree));
    }

    [Fact]
    public void G_P_KW_004_measure_is_rejected_at_p1()
    {
        var tree = Golden("MEASURE[Value]", """
            ExpressionCompilationUnit [7..14)
              BracketedName [7..14)
                BracketedIdentifierToken [7..14) "[Value]"
                  lead SkippedTokensTrivia [0..7)
                    SkippedTokens [0..7) RECOVERY
                      MeasureKeyword [0..7) "MEASURE"
              EndOfFileToken [14..14)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 7));

        Assert.IsType<BracketedNameSyntax>(Expr(tree));
    }

    /// <summary>
    /// The PR-2-critical row (A2 §10.3): <c>COLUMN</c> must stay a stop token. If PR-1 admitted it as a
    /// name, a PR-2 parser recovering from a malformed <c>DEFINE MEASURE</c> body would swallow the next
    /// definition entirely.
    /// </summary>
    [Fact]
    public void G_P_KW_005_column_stays_a_stop_token()
    {
        var tree = Golden("COLUMN[Value]", """
            ExpressionCompilationUnit [6..13)
              BracketedName [6..13)
                BracketedIdentifierToken [6..13) "[Value]"
                  lead SkippedTokensTrivia [0..6)
                    SkippedTokens [0..6) RECOVERY
                      ColumnKeyword [0..6) "COLUMN"
              EndOfFileToken [13..13)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 6));

        Assert.Equal(SyntaxKind.ColumnKeyword, AllTokens(tree.Root).First().Kind);
    }

    [Fact]
    public void G_P_KW_006_the_call_and_the_argument_list_survive_a_rejected_qualifier()
    {
        var tree = Golden("SUM(TABLE[X])", """
            ExpressionCompilationUnit [0..13)
              CallExpression [0..13)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..13)
                  OpenParenToken [3..4) "("
                  Argument [9..12)
                    BracketedName [9..12)
                      BracketedIdentifierToken [9..12) "[X]"
                        lead SkippedTokensTrivia [4..9)
                          SkippedTokens [4..9) RECOVERY
                            TableKeyword [4..9) "TABLE"
                  CloseParenToken [12..13) ")"
              EndOfFileToken [13..13)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 4, 5));

        var call = Assert.IsType<CallExpressionSyntax>(Expr(tree));
        Assert.Single(call.ArgumentList.Arguments);
        Assert.False(call.ArgumentList.CloseParenToken.IsMissing);
    }

    /// <summary>
    /// A2 §6.3 note "P2, P3, P4 never arise independently": the keyword is stopped at P1, and what remains
    /// parses as a dotted call with a missing leading segment.
    /// </summary>
    [Fact]
    public void G_P_KW_007_a_rejected_leading_dotted_segment()
    {
        var tree = Golden("AXIS.SUB.F(1)", """
            ExpressionCompilationUnit [4..13)
              CallExpression [4..13)
                DottedName [4..10)
                  IdentifierName [0..0)
                    IdentifierToken [0..0) MISSING
                  DotToken [4..5) "."
                    lead SkippedTokensTrivia [0..4)
                      SkippedTokens [0..4) RECOVERY
                        AxisKeyword [0..4) "AXIS"
                  IdentifierName [5..8)
                    IdentifierToken [5..8) "SUB"
                  DotToken [8..9) "."
                  IdentifierName [9..10)
                    IdentifierToken [9..10) "F"
                ArgumentList [10..13)
                  OpenParenToken [10..11) "("
                  Argument [11..12)
                    LiteralExpression [11..12)
                      IntegerLiteralToken [11..12) "1"
                  CloseParenToken [12..13) ")"
              EndOfFileToken [13..13)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 4),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 4, 0));

        Assert.Equal("AXIS.SUB.F(1)", tree.Root.ToFullString());
        var dotted = Assert.IsType<DottedNameSyntax>(Assert.IsType<CallExpressionSyntax>(Expr(tree)).Name);
        Assert.True(dotted.Segments[0].Identifier.IsMissing);
    }

    // The residue is a PARENTHESIZED expression, not a call: the keyword never became a callable name.
    [Fact]
    public void G_P_KW_008_a_rejected_call_name_leaves_a_parenthesized_expression()
    {
        var tree = Golden("START(1)", """
            ExpressionCompilationUnit [5..8)
              ParenthesizedExpression [5..8)
                OpenParenToken [5..6) "("
                  lead SkippedTokensTrivia [0..5)
                    SkippedTokens [0..5) RECOVERY
                      StartKeyword [0..5) "START"
                LiteralExpression [6..7)
                  IntegerLiteralToken [6..7) "1"
                CloseParenToken [7..8) ")"
              EndOfFileToken [8..8)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 5));

        Assert.IsType<ParenthesizedExpressionSyntax>(Expr(tree));
    }

    [Fact]
    public void G_P_KW_009_a_rejected_operand_leaves_the_rest_of_the_ladder_intact()
    {
        var tree = Golden("AT + 1", """
            ExpressionCompilationUnit [3..6)
              PrefixUnaryExpression [3..6)
                PlusToken [3..4) "+"
                  lead SkippedTokensTrivia [0..3)
                    SkippedTokens [0..2) RECOVERY
                      AtKeyword [0..2) "AT"
                LiteralExpression [5..6)
                  IntegerLiteralToken [5..6) "1"
              EndOfFileToken [6..6)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 2));

        Assert.IsType<PrefixUnaryExpressionSyntax>(Expr(tree));
    }

    /// <summary>
    /// The most extreme P5 case: <c>RETURN</c> itself in the declaration-name slot. It is RETAINED with
    /// <c>DAXP1084</c>, the tree keeps both <c>VAR</c> and <c>RETURN</c> structures, and round-trip holds.
    /// </summary>
    [Fact]
    public void G_P_KW_010_return_is_retained_in_the_declaration_name_slot()
    {
        var tree = Golden("VAR RETURN = 1 RETURN 1", """
            ExpressionCompilationUnit [0..23)
              VariableExpression [0..23)
                VariableDeclaration [0..14)
                  VarKeyword [0..3) "VAR"
                  ReturnKeyword [4..10) "RETURN"
                  EqualsToken [11..12) "="
                  LiteralExpression [13..14)
                    IntegerLiteralToken [13..14) "1"
                ReturnClause [15..23)
                  ReturnKeyword [15..21) "RETURN"
                  LiteralExpression [22..23)
                    IntegerLiteralToken [22..23) "1"
              EndOfFileToken [23..23)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsVariableName, 4, 6));

        var variable = Assert.IsType<VariableExpressionSyntax>(Expr(tree));
        Assert.Equal(SyntaxKind.ReturnKeyword, variable.Declarations[0].Identifier.Kind);
        Assert.False(variable.Return.ReturnKeyword.IsMissing);
        Assert.Equal("VAR RETURN = 1 RETURN 1", tree.Root.ToFullString());
    }

    // A2 §8.3 row 2: NOT keeps its level-8 role, so its MISSING OPERAND is the only complaint.
    [Fact]
    public void G_P_KW_011_not_keeps_its_role_and_only_its_operand_is_missing()
    {
        var tree = Golden("SUM(NOT)", """
            ExpressionCompilationUnit [0..8)
              CallExpression [0..8)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..8)
                  OpenParenToken [3..4) "("
                  Argument [4..7)
                    PrefixUnaryExpression [4..7)
                      NotKeyword [4..7) "NOT"
                      MissingExpression [7..7) RECOVERY
                  CloseParenToken [7..8) ")"
              EndOfFileToken [8..8)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 7, 0));

        Assert.Equal(0, CountOf(tree, DaxParserDiagnosticCodes.KeywordNotPermittedAsName));
        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.ExpressionExpected));
    }

    // A2 §8.3 row 3: IN is not a stopper, so it takes exactly one DAXP1043 and NO companion DAXP1001.
    [Fact]
    public void G_P_KW_012_in_at_a_required_primary_takes_exactly_one_diagnostic()
    {
        var tree = Golden("SUM(IN)", """
            ExpressionCompilationUnit [0..7)
              CallExpression [0..7)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..7)
                  OpenParenToken [3..4) "("
                  Argument [4..4)
                    MissingExpression [4..4) RECOVERY
                  CloseParenToken [6..7) ")"
                    lead SkippedTokensTrivia [4..6)
                      SkippedTokens [4..6) RECOVERY
                        InKeyword [4..6) "IN"
              EndOfFileToken [7..7)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 4, 2));

        Assert.Single(tree.GetDiagnostics());
        Assert.Equal(0, CountOf(tree, DaxParserDiagnosticCodes.ExpressionExpected));
    }

    /// <summary>
    /// A2 §8.3 row 1 plus the "one diagnostic is scoped to that POSITION" clause: the stopper wins, so the
    /// argument gets <c>DAXP1001</c> and no <c>DAXP1043</c>; the unconsumed <c>RETURN</c> is then still junk
    /// to the enclosing argument list and is skipped in the ordinary way, with <c>DAXP1005</c>. Two true
    /// statements at two positions, neither an echo of the other.
    /// </summary>
    [Fact]
    public void G_P_KW_013_a_stopper_wins_and_the_skip_is_a_separate_statement()
    {
        var tree = Golden("SUM(RETURN)", """
            ExpressionCompilationUnit [0..11)
              CallExpression [0..11)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..11)
                  OpenParenToken [3..4) "("
                  Argument [4..4)
                    MissingExpression [4..4) RECOVERY
                  CloseParenToken [10..11) ")"
                    lead SkippedTokensTrivia [4..10)
                      SkippedTokens [4..10) RECOVERY
                        ReturnKeyword [4..10) "RETURN"
              EndOfFileToken [11..11)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 4, 0),
            Err(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, 4, 6));

        Assert.Equal(0, CountOf(tree, DaxParserDiagnosticCodes.KeywordNotPermittedAsName));
    }

    /// <summary>
    /// A2 §6.1 finding 4 and §6.2 rule 1: a delimited spelling arrives from A1 as a delimited-identifier
    /// token, NEVER as a keyword token, so it never reaches the §6 rule at all. These must carry NO
    /// diagnostic whatsoever — the draft's claim that a strict parser would break them was simply wrong.
    /// </summary>
    [Theory]
    [InlineData("'Total'[x]", SyntaxKind.QualifiedName)]
    [InlineData("[Group]", SyntaxKind.BracketedName)]
    [InlineData("[Column]", SyntaxKind.BracketedName)]
    [InlineData("'Group'", SyntaxKind.QuotedName)]
    public void G_P_KW_014_delimited_keyword_spellings_are_ordinary_undiagnosed_names(string source, SyntaxKind expected)
    {
        var tree = Parse(source);
        AssertUniversal(tree, source, DaxParseOptions.Default);

        Assert.Empty(tree.GetDiagnostics());
        Assert.False(tree.Root.ContainsRecovery);
        Assert.Equal(expected, Expr(tree).Kind);
        Assert.All(tree.Root.DescendantTokens(), t => Assert.False(DaxSyntaxFacts.IsKeyword(t.Kind)));
    }

    // A1 §2.4 matches keywords OrdinalIgnoreCase, so lower case is rejected identically; raw casing is
    // preserved in the skipped trivia.
    [Fact]
    public void G_P_KW_015_keyword_matching_is_case_insensitive_and_raw_casing_survives()
    {
        var tree = Golden("define[x]", """
            ExpressionCompilationUnit [6..9)
              BracketedName [6..9)
                BracketedIdentifierToken [6..9) "[x]"
                  lead SkippedTokensTrivia [0..6)
                    SkippedTokens [0..6) RECOVERY
                      DefineKeyword [0..6) "define"
              EndOfFileToken [9..9)
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 0, 6));

        Assert.Equal("define", AllTokens(tree.Root).First().Text);   // raw casing, not normalized
        Assert.Equal("define[x]", tree.Root.ToFullString());
    }

    /// <summary>
    /// One token spelling, three positions, three outcomes: P6 parameter name RETAINED with
    /// <c>DAXP1069</c> (Error); P7 hint word is required grammar with NO diagnostic; P1 body reference
    /// REJECTED with <c>DAXP1043</c>. This is the row that proves a kind-only predicate could not have
    /// expressed the rule, which is why <c>IsAdmittedKeywordName</c> was removed (A2 §6.2).
    /// </summary>
    [Fact]
    public void G_P_KW_016_one_keyword_three_positions_three_outcomes()
    {
        var tree = GoldenUdf("(TABLE: TABLE VAL) => TABLE", """
            UdfBodyCompilationUnit [0..27)
              LambdaExpression [0..21)
                ParameterList [0..18)
                  OpenParenToken [0..1) "("
                  Parameter [1..17)
                    TableKeyword [1..6) "TABLE"
                    ParameterTypeClause [6..17)
                      ColonToken [6..7) ":"
                      TypeHint [8..13)
                        TableKeyword [8..13) "TABLE"
                      PassingModeHint [14..17)
                        IdentifierToken [14..17) "VAL"
                  CloseParenToken [17..18) ")"
                LambdaArrowToken [19..21) "=>"
                MissingExpression [22..22) RECOVERY
              EndOfFileToken [27..27)
                lead SkippedTokensTrivia [22..27)
                  SkippedTokens [22..27) RECOVERY
                    TableKeyword [22..27) "TABLE"
            """,
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsParameterName, 1, 5),
            Err(DaxParserDiagnosticCodes.KeywordNotPermittedAsName, 22, 5));

        var parameter = Assert.Single(Lambda(tree).ParameterList.Parameters);
        Assert.Equal(SyntaxKind.TableKeyword, parameter.Identifier.Kind);          // P6 retained
        Assert.Equal(SyntaxKind.TableKeyword, parameter.TypeClause!.TypeHint!.Token.Kind);
        Assert.False(parameter.TypeClause.TypeHint.ContainsDiagnostics);           // P7 undiagnosed
        Assert.Equal(DaxDiagnosticSeverity.Error, tree.GetDiagnostics()[0].Severity);
        Assert.Equal("(TABLE: TABLE VAL) => TABLE", tree.Root.ToFullString());
    }

    // ---- G-P-KW-017: the all-21-keyword sweep at P1 ------------------------------------------------

    /// <summary>
    /// A2 §6.3 rows 1 and 2, as a table over EVERY reserved keyword, so a future keyword addition cannot
    /// silently change admission. <c>Diag</c> means exactly one <c>DAXP1043</c> and no
    /// <c>IdentifierNameSyntax</c> is ever built over the token; <c>Role</c> means the keyword's own
    /// production applies and NO <c>DAXP1043</c> is emitted anywhere.
    /// </summary>
    public static IEnumerable<object[]> AllKeywordsAtP1()
    {
        // The 21 A1 §2.4 keywords in table order, with their §6.3 P1 outcome.
        var rows = new (string Spelling, SyntaxKind Kind, bool Rejected)[]
        {
            ("DEFINE", SyntaxKind.DefineKeyword, true),
            ("EVALUATE", SyntaxKind.EvaluateKeyword, true),
            ("ORDER", SyntaxKind.OrderKeyword, true),
            ("BY", SyntaxKind.ByKeyword, true),
            ("START", SyntaxKind.StartKeyword, true),
            ("AT", SyntaxKind.AtKeyword, true),
            ("VAR", SyntaxKind.VarKeyword, false),        // Role: §4.8, a variable expression
            ("RETURN", SyntaxKind.ReturnKeyword, false),  // Role: stopper, §8.3 row 1
            ("MEASURE", SyntaxKind.MeasureKeyword, true),
            ("COLUMN", SyntaxKind.ColumnKeyword, true),
            ("TABLE", SyntaxKind.TableKeyword, true),
            ("FUNCTION", SyntaxKind.FunctionKeyword, true),
            ("WITH", SyntaxKind.WithKeyword, true),
            ("VISUAL", SyntaxKind.VisualKeyword, true),
            ("SHAPE", SyntaxKind.ShapeKeyword, true),
            ("AXIS", SyntaxKind.AxisKeyword, true),
            ("GROUP", SyntaxKind.GroupKeyword, true),
            ("TOTAL", SyntaxKind.TotalKeyword, true),
            ("DENSIFY", SyntaxKind.DensifyKeyword, true),
            ("IN", SyntaxKind.InKeyword, true),
            ("NOT", SyntaxKind.NotKeyword, false)         // Role: level 8 prefix operator
        };

        foreach (var row in rows) yield return new object[] { row.Spelling, row.Kind, row.Rejected };
    }

    /// <summary>
    /// The three ROLE keywords only, for the sweep that checks each one reaches its own production.
    ///
    /// <para>
    /// Filtering the DATA SOURCE, not the method body. Binding that test to all 21 rows and opening it with
    /// <c>if (rejected) return;</c> meant xUnit reported 21 green cases of which 18 executed no assertion at
    /// all, and the pull request's "217 new tests" figure counted every one of them (A2PR1-M5). The case
    /// count now tells the truth: 3.
    /// </para>
    /// </summary>
    public static IEnumerable<object[]> RoleKeywordsAtP1()
        => AllKeywordsAtP1().Where(row => !(bool)row[2]);

    [Theory]
    [MemberData(nameof(AllKeywordsAtP1))]
    public void G_P_KW_017_every_keyword_at_p1_takes_its_position_table_outcome(string spelling, SyntaxKind kind, bool rejected)
    {
        var tree = Parse(spelling);
        AssertUniversal(tree, spelling, DaxParseOptions.Default);

        // A2 §2 rule 1: the token kind is never rewritten, wherever it ends up.
        // Exactly one token has raw text: the keyword itself. Every other token in a Role parse is a
        // zero-width insertion, so filtering on text keeps this honest without listing recovery shapes.
        var token = Assert.Single(AllTokens(tree.Root), t => t.Text.Length > 0);
        Assert.Equal(kind, token.Kind);
        Assert.Equal(spelling, token.Text);

        Assert.Equal(rejected ? 1 : 0, CountOf(tree, DaxParserDiagnosticCodes.KeywordNotPermittedAsName));

        // A2 §6.2 rule 1: no IdentifierName is ever built over a keyword token, in either outcome.
        Assert.DoesNotContain("IdentifierName", Render(tree.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void G_P_KW_017_the_sweep_covers_every_keyword_kind_exactly_once()
    {
        // A guard on the guard: if a keyword is added to A1's inventory, this fails rather than the sweep
        // silently shrinking.
        var swept = AllKeywordsAtP1().Select(r => (SyntaxKind)r[1]).ToHashSet();
        var declared = Enum.GetValues<SyntaxKind>().Where(k => (int)k is >= 1000 and <= 1099).ToHashSet();

        Assert.Equal(21, declared.Count);
        Assert.Equal(declared, swept);

        // The Role subset is derived, never hand-listed, so it cannot fall out of step with the table above.
        // Pinning its size means a new Role keyword shows up as a failure here rather than as a row nobody
        // notices is missing from the production sweep.
        var roles = RoleKeywordsAtP1().Select(r => (SyntaxKind)r[1]).ToHashSet();
        Assert.Equal(
            new HashSet<SyntaxKind> { SyntaxKind.VarKeyword, SyntaxKind.ReturnKeyword, SyntaxKind.NotKeyword },
            roles);
    }

    [Theory]
    [MemberData(nameof(RoleKeywordsAtP1))]
    public void G_P_KW_017_the_role_keywords_reach_their_own_production(string spelling, SyntaxKind kind, bool rejected)
    {
        Assert.False(rejected, "RoleKeywordsAtP1 must yield only Role rows.");

        var tree = Parse(spelling);
        var rendered = Render(tree.Root);

        // Each Role keyword must actually reach its production, not merely avoid DAXP1043.
        switch (kind)
        {
            case SyntaxKind.VarKeyword:
                Assert.Contains("VariableExpression", rendered, StringComparison.Ordinal);
                break;
            case SyntaxKind.NotKeyword:
                Assert.Contains("PrefixUnaryExpression", rendered, StringComparison.Ordinal);
                break;
            case SyntaxKind.ReturnKeyword:
                // The stopper outcome: nothing consumed at the primary position, one DAXP1001 there.
                Assert.Contains("MissingExpression", rendered, StringComparison.Ordinal);
                Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.ExpressionExpected));
                break;
            default:
                Assert.Fail($"{spelling} is marked Role but has no asserted production.");
                break;
        }
    }
}
