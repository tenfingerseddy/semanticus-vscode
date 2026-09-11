using Semanticus.Dax.Syntax;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2PR1-M4: a recovered parse must always be able to SAY it recovered.
///
/// <para>
/// The contract is stated twice, in <c>A0-syntax-api-design.md</c> §11.2 and in the public XML doc on
/// <see cref="SyntaxToken"/>: a required-but-absent token is "a real, enumerated, zero-width token with
/// <c>IsMissing</c> true and a <c>DAXP</c> diagnostic". The point of the diagnostic half is that a later
/// phase gating on <c>GetDiagnostics()</c> must never read a malformed subtree as valid.
/// </para>
/// <para>
/// The invariant asserted here is SUBTREE-scoped rather than per-token, and that is a deliberate reading of
/// two rules that pull against each other. Depth recovery inserts a PAIR of delimiters for ONE source
/// defect; A2 §8.1 rule 5 says one source defect produces one diagnostic. Demanding a diagnostic on each of
/// the two inserted tokens would mean emitting DAXP1007 twice for one defect. So the rule enforced is: the
/// node that owns a missing token must report a <c>DAXP</c> diagnostic from within its own subtree. That is
/// the property a consumer actually needs, and it is strictly stronger than what shipped, where the
/// DAXP1007 landed on trivia OUTSIDE the call it described.
/// </para>
/// </summary>
public class MissingTokenDiagnosticTests
{
    /// <summary>
    /// The sweep. Every <c>G-P-*</c> battery row, plus sources that exhaust the nesting limit, which is the
    /// path that shipped broken.
    /// </summary>
    [Fact]
    public void Every_node_holding_a_missing_token_reports_a_parser_diagnostic()
    {
        var checked_ = 0;
        var withMissing = 0;

        foreach (var (id, source, options) in DepthExhaustingRows().Concat(ParserBattery.All()))
        {
            var tree = Parse(source, options);
            checked_++;

            foreach (var node in Nodes(tree.Root))
            {
                var missing = node.ChildNodesAndTokens()
                    .Where(c => c.AsNode() is null)
                    .Select(c => c.AsToken())
                    .Where(t => t.IsMissing)
                    .ToList();

                if (missing.Count == 0) continue;
                withMissing++;

                var codes = node.GetDiagnostics().Select(d => d.Code).ToList();
                Assert.True(
                    codes.Any(c => c.StartsWith("DAXP", StringComparison.Ordinal)),
                    $"{id} ('{source}'): {node.Kind} holds {missing.Count} missing token(s) " +
                    $"({string.Join(", ", missing.Select(t => t.Kind))}) but its subtree reports no DAXP " +
                    $"diagnostic at all (it reports: {(codes.Count == 0 ? "nothing" : string.Join(", ", codes))}). " +
                    "A later phase gating on diagnostics reads this malformed subtree as valid (A2PR1-M4).");
            }
        }

        // Non-vacuity: the sweep must actually have found missing tokens to check.
        Assert.True(checked_ > 50, $"Only {checked_} rows swept.");
        Assert.True(withMissing > 10, $"Only {withMissing} nodes with missing tokens found; the sweep proves nothing.");
    }

    /// <summary>
    /// The depth-recovery argument-list shell specifically. <c>TryOpenProduction</c> put its DAXP1007 on
    /// SKIPPED-TOKEN trivia, which then flushes onto the leading trivia of the next accepted token — a token
    /// outside the call. So the call held two missing delimiters while
    /// <c>call.ContainsDiagnostics</c> was false.
    /// </summary>
    [Fact]
    public void A_depth_recovered_call_reports_its_own_recovery()
    {
        var tree = Parse("SUM(1)", Options(maximumNestingDepth: 0));

        var call = Nodes(tree.Root).OfType<CallExpressionSyntax>().SingleOrDefault();
        Assert.NotNull(call);

        var argumentList = call.ArgumentList;
        Assert.True(argumentList.OpenParenToken.IsMissing, "This row is only meaningful if the shell was built.");
        Assert.True(argumentList.CloseParenToken.IsMissing, "This row is only meaningful if the shell was built.");

        Assert.True(argumentList.ContainsDiagnostics,
            "A2PR1-M4: the argument list holds two inserted delimiters and reports nothing.");
        Assert.Contains(
            DaxParserDiagnosticCodes.MaximumNestingDepthExceeded,
            argumentList.GetDiagnostics().Select(d => d.Code));

        // Exactly ONE DAXP1007 in the whole tree: the diagnostic was MOVED onto the token that records the
        // defect, never duplicated (A2 §8.1 rule 5).
        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded));
    }

    /// <summary>
    /// <c>Parser.Variables.cs</c>'s delimited-name path returned a bare missing identifier while its
    /// DAXP1083 went onto skipped-token trivia. The token-level contract is the one breached here, so it is
    /// the one asserted: the missing token itself owns the complaint.
    /// </summary>
    [Theory]
    [InlineData("VAR [a] = 1 RETURN 1")]
    [InlineData("VAR 'a' = 1 RETURN 1")]
    [InlineData("VAR t.a = 1 RETURN 1")]
    public void A_delimited_variable_name_puts_its_diagnostic_on_the_token_it_replaced(string source)
    {
        var tree = Parse(source);

        var missing = AllTokens(tree.Root)
            .Where(t => t.IsMissing && t.Kind == SyntaxKind.IdentifierToken)
            .ToList();

        var token = Assert.Single(missing);
        Assert.Contains(
            DaxParserDiagnosticCodes.VariableNameCannotBeDelimited,
            token.GetDiagnostics().Select(d => d.Code));

        // Moved, not duplicated.
        Assert.Equal(1, CountOf(tree, DaxParserDiagnosticCodes.VariableNameCannotBeDelimited));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Rows that exhaust the nesting limit. <c>MaximumNestingDepth = 0</c> is the smallest trigger, but the
    /// shape is identical at the default 256 for any source deep enough to reach it, so a low limit here is
    /// a shorthand and not a synthetic-only case.
    /// </summary>
    private static IEnumerable<(string Id, string Source, DaxParseOptions Options)> DepthExhaustingRows()
    {
        var zero = Options(maximumNestingDepth: 0);
        var one = Options(maximumNestingDepth: 1);

        yield return ("M4-depth-call", "SUM(1)", zero);
        yield return ("M4-depth-nested-call", "SUM(MAX(1))", one);
        yield return ("M4-depth-paren", "(1)", zero);
        yield return ("M4-depth-brace", "{1}", zero);
        yield return ("M4-depth-not", "NOT 1", zero);
        yield return ("M4-depth-sign", "- 1", zero);
        yield return ("M4-depth-var", "VAR a = 1 RETURN a", zero);
        yield return ("M4-depth-caret", "1 ^ 1 ^ 1", one);
        yield return ("M4-depth-udf", "() => (1)", Options(sourceKind: DaxSourceKind.UdfBody, maximumNestingDepth: 1));
        yield return ("M4-depth-udf-deep", "(x) => ((x))", Options(sourceKind: DaxSourceKind.UdfBody, maximumNestingDepth: 1));
    }

    /// <summary>Every node in the tree, walked ITERATIVELY (A2PR1-M2: the shipped walkers are not).</summary>
    private static IEnumerable<SyntaxNode> Nodes(SyntaxNode root)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            foreach (var child in node.ChildNodesAndTokens())
                if (child.AsNode() is { } childNode) stack.Push(childNode);
        }
    }
}
