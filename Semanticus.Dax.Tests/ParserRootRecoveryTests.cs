using System.Diagnostics;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2 §11.8, <c>G-P-ROOT-001</c>..<c>-009</c> and <c>G-P-REC-001</c>..<c>-015</c>: the expression root
/// (§4.1), the leading-<c>=</c> pin (§5.2), the optional-token-absent representation (§5.6), and the
/// recovery mechanics with the §8.4 span-anchoring rules.
/// </summary>
public class ParserRootRecoveryTests
{
    private const string TreeOfEqualsOnePlusOne = """
        ExpressionCompilationUnit [0..7)
          EqualsToken [0..1) "="
          BinaryExpression [2..7)
            LiteralExpression [2..3)
              IntegerLiteralToken [2..3) "1"
            PlusToken [4..5) "+"
            LiteralExpression [6..7)
              IntegerLiteralToken [6..7) "1"
          EndOfFileToken [7..7)
        """;

    /// <summary>
    /// A2 §5.2 pins 2-3 plus §2 rule 5: <c>AllowLeadingEquals</c> is a PURE DIAGNOSTIC switch. The
    /// <c>=</c> is always consumed into the slot, so <c>-001</c> and <c>-002</c> assert the SAME tree and
    /// two different diagnostic lists — which is the two-tuple determinism claim, stated as a test.
    /// </summary>
    [Fact]
    public void G_P_ROOT_001_a_permitted_leading_equals_is_undiagnosed()
    {
        var tree = Golden("= 1 + 1", Options(allowLeadingEquals: true), TreeOfEqualsOnePlusOne);

        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal(SyntaxKind.EqualsToken, tree.GetExpressionRoot().LeadingEqualsToken.Kind);
    }

    [Fact]
    public void G_P_ROOT_002_a_forbidden_leading_equals_yields_the_same_tree_and_one_diagnostic()
    {
        var permitted = Parse("= 1 + 1", Options(allowLeadingEquals: true));
        var forbidden = Golden("= 1 + 1", Options(allowLeadingEquals: false), TreeOfEqualsOnePlusOne,
            Err(DaxParserDiagnosticCodes.LeadingEqualsNotPermitted, 0, 1));

        // The tree is byte-identical; only the diagnostic list moved (A2 §2 rule 5, §5.2 pin 3).
        Assert.Equal(Render(permitted.Root), Render(forbidden.Root));
        Assert.NotEqual(Serialize(permitted), Serialize(forbidden));
        Assert.Equal(SyntaxKind.EqualsToken, forbidden.GetExpressionRoot().LeadingEqualsToken.Kind);
    }

    /// <summary>
    /// A2 §5.6 row 2, the row that is easy to get wrong: an absent OPTIONAL TOKEN is a null green slot. The
    /// accessor returns a DEFAULT <c>SyntaxToken</c> whose kind is <c>None</c>; it is not enumerated by any
    /// walk, it has no width, its <c>IsMissing</c> is false, and absence is NOT recovery.
    /// </summary>
    [Fact]
    public void G_P_ROOT_003_an_absent_leading_equals_is_not_a_tree_token()
    {
        var tree = Golden("1 = 1", """
            ExpressionCompilationUnit [0..5)
              BinaryExpression [0..5)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                EqualsToken [2..3) "="
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "1"
              EndOfFileToken [5..5)
            """);

        var root = tree.GetExpressionRoot();
        var absent = root.LeadingEqualsToken;

        Assert.Equal(SyntaxKind.None, absent.Kind);
        Assert.False(absent.IsMissing);
        Assert.Equal(0, absent.FullSpan.Length);
        Assert.DoesNotContain(root.ChildNodesAndTokens(), c => c.Kind == SyntaxKind.None);
        Assert.DoesNotContain(root.DescendantTokens(), t => t.Kind == SyntaxKind.None);
        Assert.False(root.ContainsRecovery);

        // ...and the '=' that IS in the tree is the binary operator, never re-read as the root token.
        Assert.Equal(SyntaxKind.EqualsToken, Assert.IsType<BinaryExpressionSyntax>(root.Expression).OperatorToken.Kind);
    }

    // A2 §4.1: '==' is a distinct A1 kind and is never the root token, so this is ordinary recovery.
    [Fact]
    public void G_P_ROOT_004_strict_equals_is_never_the_root_token()
    {
        var tree = Golden("== 1", """
            ExpressionCompilationUnit [3..4)
              LiteralExpression [3..4)
                IntegerLiteralToken [3..4) "1"
                  lead SkippedTokensTrivia [0..3)
                    SkippedTokens [0..2) RECOVERY
                      StrictEqualsToken [0..2) "=="
              EndOfFileToken [4..4)
            """,
            Err(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, 0, 2));

        Assert.Equal(SyntaxKind.None, tree.GetExpressionRoot().LeadingEqualsToken.Kind);
    }

    /// <summary>
    /// A2 §4.1: trailing content becomes <c>SkippedTokensTrivia</c> on the EOF token with one
    /// <c>DAXP1006</c>. The root's <c>Expression</c> child is never widened to swallow it, so the second
    /// fragment stays visibly separate.
    /// </summary>
    [Fact]
    public void G_P_ROOT_005_a_second_fragment_is_not_swallowed_into_the_first()
    {
        var tree = Golden("1 + 1 2 + 2", """
            ExpressionCompilationUnit [0..11)
              BinaryExpression [0..5)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "1"
              EndOfFileToken [11..11)
                lead SkippedTokensTrivia [6..11)
                  SkippedTokens [6..11) RECOVERY
                    IntegerLiteralToken [6..7) "2"
                    PlusToken [8..9) "+"
                    IntegerLiteralToken [10..11) "2"
            """,
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 6, 5));

        Assert.Equal(new TextSpan(0, 5), tree.GetExpressionRoot().Expression.Span);
    }

    [Fact]
    public void G_P_ROOT_006_empty_source()
    {
        var tree = Golden("", """
            ExpressionCompilationUnit [0..0)
              MissingExpression [0..0) RECOVERY
              EndOfFileToken [0..0)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 0, 0));

        Assert.IsType<MissingExpressionSyntax>(tree.GetExpressionRoot().Expression);
        Assert.Equal(0, tree.GetExpressionRoot().EndOfFileToken.Span.Start);
    }

    // Every root family carries the EOF token, so trivia-only source still has an owner and round-trips.
    [Fact]
    public void G_P_ROOT_007_trivia_only_source_is_owned_by_eof()
    {
        var tree = Golden("   // just a comment", """
            ExpressionCompilationUnit [20..20)
              MissingExpression [0..0) RECOVERY
              EndOfFileToken [20..20)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 20, 0));

        var eof = tree.GetExpressionRoot().EndOfFileToken;
        Assert.Equal(2, eof.LeadingTrivia.Count);
        Assert.Equal("   // just a comment", eof.ToFullString());
    }

    /// <summary>
    /// <c>G-P-ROOT-008</c> (A2 §3.1) and <c>G-P-ROOT-009</c> (§9). These hold for every row of the whole
    /// battery — <see cref="ParseHarness.AssertUniversal"/> runs both on every golden — so this row states
    /// them once, explicitly, on a source that exercises names, calls, variables and recovery at once.
    /// </summary>
    [Fact]
    public void G_P_ROOT_008_and_009_source_kind_parity_and_capability_neutrality()
    {
        const string source = "VAR a = SUM('T'[c], ) RETURN a + TOTAL[x]";

        AssertSourceKindParity(source, DaxParseOptions.Default);
        AssertCapabilityNeutral(source, DaxParseOptions.Default);

        // ...and spelled out, so the row is not merely a call into a helper.
        var baseline = Parse(source, Options(sourceKind: DaxSourceKind.Expression));
        foreach (var kind in ExpressionSourceKinds)
        {
            var tree = Parse(source, Options(sourceKind: kind));
            Assert.Equal(Render(baseline.Root), Render(tree.Root));
            Assert.Equal(Serialize(baseline), Serialize(tree));
            Assert.IsType<ExpressionCompilationUnitSyntax>(tree.Root);
        }

        foreach (var profile in CapabilityProfiles)
        {
            var tree = Parse(source, Options(capabilities: profile));
            Assert.Equal(Render(baseline.Root), Render(tree.Root));
            Assert.Equal(Serialize(baseline), Serialize(tree));
        }
    }

    // ---- recovery ---------------------------------------------------------------------------------

    [Fact]
    public void G_P_REC_001_an_unclosed_call()
    {
        var tree = Golden("SUM(", """
            ExpressionCompilationUnit [0..4)
              CallExpression [0..4)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..4)
                  OpenParenToken [3..4) "("
                  Argument [4..4)
                    MissingExpression [4..4) RECOVERY
                  CloseParenToken [4..4) MISSING
              EndOfFileToken [4..4)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 4, 0),
            Err(DaxParserDiagnosticCodes.CloseParenExpected, 4, 0));

        Assert.True(tree.Root.ContainsRecovery);
    }

    [Fact]
    public void G_P_REC_002_three_stray_close_parens()
    {
        var tree = Golden(")))", """
            ExpressionCompilationUnit [3..3)
              MissingExpression [0..0) RECOVERY
              EndOfFileToken [3..3)
                lead SkippedTokensTrivia [0..3)
                  SkippedTokens [0..3) RECOVERY
                    CloseParenToken [0..1) ")"
                    CloseParenToken [1..2) ")"
                    CloseParenToken [2..3) ")"
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 0, 0),
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 0, 3));

        Assert.Equal(3, AllTokens(tree.Root).Count(t => t.Kind == SyntaxKind.CloseParenToken));
    }

    /// <summary>
    /// A2 §8.1 rule 5 and §8.4 rule 4: one run, every token of which already owns a lexical diagnostic, so
    /// <c>DAXP1005</c> is suppressed entirely. <c>DAXL1001</c> x3, <c>DAXP1005</c> x0. The
    /// <c>DAXP1001</c> that remains is a different statement at a different position — the argument is
    /// missing — by the same reasoning §8.3 uses to bless the two diagnostics of <c>SUM(RETURN)</c>.
    /// </summary>
    [Fact]
    public void G_P_REC_003_a_run_of_bad_tokens_produces_no_parser_echo()
    {
        var tree = Golden("SUM(#$%)", """
            ExpressionCompilationUnit [0..8)
              CallExpression [0..8)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..8)
                  OpenParenToken [3..4) "("
                  Argument [4..4)
                    MissingExpression [4..4) RECOVERY
                  CloseParenToken [7..8) ")"
                    lead SkippedTokensTrivia [4..7)
                      SkippedTokens [4..7) RECOVERY
                        BadToken [4..5) "#"
                        BadToken [5..6) "$"
                        BadToken [6..7) "%"
              EndOfFileToken [8..8)
            """,
            Err(DaxDiagnosticCodes.UnrecognizedCharacter, 4, 1),
            Err(DaxDiagnosticCodes.UnrecognizedCharacter, 5, 1),
            Err(DaxDiagnosticCodes.UnrecognizedCharacter, 6, 1),
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 7, 0));

        Assert.Equal(3, CountOf(tree, DaxDiagnosticCodes.UnrecognizedCharacter));
        Assert.Equal(0, CountOf(tree, DaxParserDiagnosticCodes.UnexpectedTokensSkipped));
        Assert.Equal(3, AllTokens(tree.Root).Count(t => t.Kind == SyntaxKind.BadToken));
    }

    // A2 §8.1 rules 1-4: the lexical diagnostic is attached to the owning token, exactly once.
    [Fact]
    public void G_P_REC_004_an_unterminated_string_keeps_its_lexical_diagnostic_exactly_once()
    {
        var tree = Golden("\"unterminated", """
            ExpressionCompilationUnit [0..13)
              LiteralExpression [0..13)
                StringLiteralToken [0..13) "\"unterminated"
              EndOfFileToken [13..13)
            """,
            Err(DaxDiagnosticCodes.UnterminatedString, 0, 13));

        var token = Assert.IsType<LiteralExpressionSyntax>(Expr(tree)).Token;
        Assert.Equal(SyntaxKind.StringLiteralToken, token.Kind);
        Assert.Single(token.GetDiagnostics());
        Assert.Equal(1, CountOf(tree, DaxDiagnosticCodes.UnterminatedString));
    }

    // A0 §11.6: a stray closing delimiter at the root is junk to be skipped, never a missing-token loop.
    [Fact]
    public void G_P_REC_005_an_extra_close_paren_is_skipped_not_looped()
    {
        var tree = Golden("SUM(1))", """
            ExpressionCompilationUnit [0..7)
              CallExpression [0..6)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..6)
                  OpenParenToken [3..4) "("
                  Argument [4..5)
                    LiteralExpression [4..5)
                      IntegerLiteralToken [4..5) "1"
                  CloseParenToken [5..6) ")"
              EndOfFileToken [7..7)
                lead SkippedTokensTrivia [6..7)
                  SkippedTokens [6..7) RECOVERY
                    CloseParenToken [6..7) ")"
            """,
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 6, 1));

        Assert.False(Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.CloseParenToken.IsMissing);
    }

    [Fact]
    public void G_P_REC_006_a_run_of_commas_terminates()
    {
        var tree = Golden(",,,,", """
            ExpressionCompilationUnit [4..4)
              MissingExpression [0..0) RECOVERY
              EndOfFileToken [4..4)
                lead SkippedTokensTrivia [0..4)
                  SkippedTokens [0..4) RECOVERY
                    CommaToken [0..1) ","
                    CommaToken [1..2) ","
                    CommaToken [2..3) ","
                    CommaToken [3..4) ","
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 0, 0),
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 0, 4));

        Assert.Equal(4, AllTokens(tree.Root).Count(t => t.Kind == SyntaxKind.CommaToken));
    }

    /// <summary>
    /// A2 §2.1 as read by §14 defect 6: <c>DAXP1007</c> is one per depth-limit EVENT, and unwinding
    /// legitimately leaves one missing-delimiter diagnostic per still-open production. So this asserts that
    /// the limit fires BEFORE any stack overflow and that round-trip holds; it deliberately does NOT assert
    /// a total diagnostic count.
    /// </summary>
    [Fact]
    public void G_P_REC_007_three_hundred_open_parens_hit_the_depth_limit_not_the_stack()
    {
        var source = Repeat("(", 300);
        var tree = Parse(source);

        Assert.Equal(source, tree.Root.ToFullString());
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            "A2 §2.1: DAXP1007 must fire before any stack overflow.");
        AssertSpansTile(tree.Root, source);
    }

    /// <summary>
    /// A2 §11.8 as corrected by §14 defect 4: the minuses must be SPACE-separated. A1 §2.2 lexes <c>--</c>
    /// as a line comment, so an unspaced run is one comment, the whole input is trivia, and the golden would
    /// test nothing. Spaced, it drives prefix-unary descent and the counter fires on the 257th.
    /// </summary>
    [Fact]
    public void G_P_REC_008_five_thousand_spaced_minuses_hit_the_depth_counter_not_the_stack()
    {
        var source = Repeat("- ", 5000);
        var tree = Parse(source);

        Assert.Equal(source, tree.Root.ToFullString());
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            "A2 §2.1: the depth counter, not the stack, is what stops prefix-unary descent.");
        AssertSpansTile(tree.Root, source);

        // The corrected shape is load-bearing: prove the input is NOT one line comment.
        var lexed = DaxLexer.Lex(DaxSourceText.From(source));
        Assert.Equal(5000, lexed.Tokens.Count(t => t.Kind == DaxTokenKind.MinusToken));
        Assert.DoesNotContain(lexed.Tokens.SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia)),
            t => t.Kind == DaxTriviaKind.DashDashCommentTrivia);
    }

    /// <summary>
    /// A2 §11.8 / §14 defect 5. The binary ladder loop is iterative and always was; what overflowed was
    /// <c>GreenNode.WriteTo</c> and the diagnostic collector, which recursed over depth. Both are now
    /// iterative and this row runs at the full 100,000 operands.
    ///
    /// <para>
    /// NOT USED HERE, DELIBERATELY. T182 step 1 made <c>DescendantTokens</c> and its three dependents
    /// iterative at 200,000. The callback <c>DaxSyntaxVisitor</c>/<c>DaxSyntaxWalker</c> and
    /// <c>DaxSyntaxRewriter</c> surfaces still recurse pending steps 2-3, and this suite's own renderer and
    /// tiling walk recurse too. This row therefore keeps walking the left spine iteratively through typed
    /// accessors and pins the depth at the full 100,000 the spec asks for.
    /// </para>
    /// </summary>
    [Fact]
    public void G_P_REC_009_one_hundred_thousand_binary_operators_do_not_consume_stack()
    {
        const int operators = 100_000;
        var source = string.Join(" + ", Enumerable.Repeat("1", operators + 1));

        var tree = Parse(source);

        Assert.Equal(source, tree.Root.ToFullString());     // GreenNode.WriteTo, iterative
        Assert.Empty(tree.GetDiagnostics());                // DiagnosticCollector, iterative

        // The left spine really is 100,000 deep, walked without recursion.
        var depth = 0;
        ExpressionSyntax current = tree.GetExpressionRoot().Expression;
        while (current is BinaryExpressionSyntax binary)
        {
            Assert.Equal(SyntaxKind.PlusToken, binary.OperatorToken.Kind);
            depth++;
            current = binary.Left;
        }

        Assert.Equal(operators, depth);
        Assert.IsType<LiteralExpressionSyntax>(current);
    }

    // A0 §11.5, VAR block: resynchronize at RETURN, so the return clause survives intact.
    [Fact]
    public void G_P_REC_010_a_var_block_resynchronizes_at_return()
    {
        var tree = Golden("VAR a = ) RETURN a", """
            ExpressionCompilationUnit [0..18)
              VariableExpression [0..18)
                VariableDeclaration [0..7)
                  VarKeyword [0..3) "VAR"
                  IdentifierToken [4..5) "a"
                  EqualsToken [6..7) "="
                  MissingExpression [8..8) RECOVERY
                ReturnClause [10..18)
                  ReturnKeyword [10..16) "RETURN"
                    lead SkippedTokensTrivia [8..10)
                      SkippedTokens [8..9) RECOVERY
                        CloseParenToken [8..9) ")"
                  IdentifierName [17..18)
                    IdentifierToken [17..18) "a"
              EndOfFileToken [18..18)
            """,
            Err(DaxParserDiagnosticCodes.ExpressionExpected, 8, 0),
            Err(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, 8, 1));

        var variable = Assert.IsType<VariableExpressionSyntax>(Expr(tree));
        Assert.False(variable.Return.ReturnKeyword.IsMissing);
        Assert.Equal("a", Assert.IsType<IdentifierNameSyntax>(variable.Return.Expression).Identifier.Text);
    }

    [Fact]
    public void G_P_REC_011_omitted_arguments_survive_alongside_whitespace()
    {
        var tree = Golden("F(1, , )", """
            ExpressionCompilationUnit [0..8)
              CallExpression [0..8)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "F"
                ArgumentList [1..8)
                  OpenParenToken [1..2) "("
                  Argument [2..3)
                    LiteralExpression [2..3)
                      IntegerLiteralToken [2..3) "1"
                  CommaToken [3..4) ","
                  OmittedArgument [5..5)
                  CommaToken [5..6) ","
                  OmittedArgument [7..7)
                  CloseParenToken [7..8) ")"
              EndOfFileToken [8..8)
            """);

        var arguments = Assert.IsType<CallExpressionSyntax>(Expr(tree)).ArgumentList.Arguments;
        Assert.Equal(3, arguments.Count);
        Assert.IsType<OmittedArgumentSyntax>(arguments[1]);
        Assert.IsType<OmittedArgumentSyntax>(arguments[2]);
        Assert.Empty(tree.GetDiagnostics());
    }

    /// <summary>
    /// A1 property F-BAD, lifted to the tree: take EVERY <c>G-P-*</c> input, insert a <c>BadToken</c> at
    /// every token boundary, and assert that round-trip and progress still hold. Progress means the parse
    /// terminates and yields a tree; the timeout is what makes "terminates" an assertion rather than a hope.
    /// </summary>
    [Fact]
    public void G_P_REC_012_a_bad_token_at_every_boundary_preserves_round_trip_and_progress()
    {
        var mutations = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var (id, source, options) in ParserBattery.All())
        {
            foreach (var position in BoundariesOf(source))
            {
                var mutated = source[..position] + "#" + source[position..];
                var tree = Parse(mutated, options);

                Assert.Equal(mutated, tree.Root.ToFullString());
                AssertSpansTile(tree.Root, mutated);
                AssertLexicalConservation(tree, mutated);
                mutations++;

                Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(2),
                    $"A0 §11.6 progress: the battery stopped making progress around {id} at {position}.");
            }
        }

        Assert.True(mutations > 500, $"the mutation sweep must actually run ({mutations} mutations)");
    }

    /// <summary>Every token boundary of the source, from A1's own partition (A1 §7.4 property 1).</summary>
    private static IEnumerable<int> BoundariesOf(string source)
    {
        var seen = new SortedSet<int>();
        foreach (var token in DaxLexer.Lex(DaxSourceText.From(source)).Tokens)
        {
            seen.Add(token.Span.Start);
            seen.Add(token.Span.End);
        }
        return seen;
    }

    // A2 §8.4 rule 4: a CONTIGUOUS skipped run collapses to ONE diagnostic, not one per token.
    [Fact]
    public void G_P_REC_013_a_skipped_run_is_one_diagnostic()
    {
        var tree = Golden("SUM(1 2 3)", """
            ExpressionCompilationUnit [0..10)
              CallExpression [0..10)
                IdentifierName [0..3)
                  IdentifierToken [0..3) "SUM"
                ArgumentList [3..10)
                  OpenParenToken [3..4) "("
                  Argument [4..5)
                    LiteralExpression [4..5)
                      IntegerLiteralToken [4..5) "1"
                  CloseParenToken [9..10) ")"
                    lead SkippedTokensTrivia [6..9)
                      SkippedTokens [6..9) RECOVERY
                        IntegerLiteralToken [6..7) "2"
                        IntegerLiteralToken [8..9) "3"
              EndOfFileToken [10..10)
            """,
            Err(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, 6, 3));

        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.UnexpectedTokensSkipped));
    }

    [Fact]
    public void G_P_REC_014_a_trailing_run_is_one_diagnostic()
    {
        var tree = Golden("1 + 1 2 3 4", """
            ExpressionCompilationUnit [0..11)
              BinaryExpression [0..5)
                LiteralExpression [0..1)
                  IntegerLiteralToken [0..1) "1"
                PlusToken [2..3) "+"
                LiteralExpression [4..5)
                  IntegerLiteralToken [4..5) "1"
              EndOfFileToken [11..11)
                lead SkippedTokensTrivia [6..11)
                  SkippedTokens [6..11) RECOVERY
                    IntegerLiteralToken [6..7) "2"
                    IntegerLiteralToken [8..9) "3"
                    IntegerLiteralToken [10..11) "4"
            """,
            Err(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression, 6, 5));

        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.UnexpectedContentAfterExpression));
    }

    /// <summary>
    /// A2 §8.4 rule 5: repeated INDEPENDENT faults do not collapse. Each empty dotted segment is its own
    /// insertion point with its own zero-width span, so <c>A..B..C</c> yields two <c>DAXP1041</c>.
    /// </summary>
    [Fact]
    public void G_P_REC_015_two_empty_dotted_segments_are_two_diagnostics()
    {
        var tree = Golden("A..B..C", """
            ExpressionCompilationUnit [0..7)
              DottedName [0..7)
                IdentifierName [0..1)
                  IdentifierToken [0..1) "A"
                DotToken [1..2) "."
                IdentifierName [2..2)
                  IdentifierToken [2..2) MISSING
                DotToken [2..3) "."
                IdentifierName [3..4)
                  IdentifierToken [3..4) "B"
                DotToken [4..5) "."
                IdentifierName [5..5)
                  IdentifierToken [5..5) MISSING
                DotToken [5..6) "."
                IdentifierName [6..7)
                  IdentifierToken [6..7) "C"
              EndOfFileToken [7..7)
            """,
            Err(DaxParserDiagnosticCodes.DottedNameNotCallable, 0, 7),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 2, 0),
            Err(DaxParserDiagnosticCodes.EmptyDottedSegment, 5, 0));

        Assert.Equal(2, CountOf(tree, DaxParserDiagnosticCodes.EmptyDottedSegment));
        Assert.Equal(5, Assert.IsType<DottedNameSyntax>(Expr(tree)).Segments.Count);
    }
}
