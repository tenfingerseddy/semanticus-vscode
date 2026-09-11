using System.Diagnostics;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// A2PR1-M1 and A2PR1-M2: what a DEEP formula does to the process.
///
/// <para>
/// Two kinds of assertion live here and the difference matters.
/// </para>
/// <list type="number">
///   <item>
///     <b>In-process</b> rows assert that the depth counter, not the stack, is what stops a descent. They
///     are safe because a guarded descent never gets deep enough to overflow. A row that asserts the guard
///     FIRED is the sharper test: it fails on a bypassed guard long before the stack would notice.
///   </item>
///   <item>
///     <b>Out-of-process</b> rows assert an exit code. They exist because a .NET
///     <c>StackOverflowException</c> cannot be caught and terminates the process, so an in-process version
///     would take the test host down and read as an infrastructure flake rather than as a failure. The deep
///     work runs in <c>Semanticus.DaxDeepProbe</c> and this suite only reads the child's exit code.
///   </item>
/// </list>
/// </summary>
public class DeepFormulaTests
{
    // ---- A2PR1-M1: the caret chain -------------------------------------------------------------------

    /// <summary>
    /// G-P-REC-008b, in-process half. Level 2 is right associative, so the right operand DESCENDS: each
    /// caret opens a nested production and the 256-deep cap must fire, exactly as it does for a run of
    /// prefix minuses (G-P-REC-008).
    ///
    /// <para>
    /// Before the fix this asserted nothing about the stack and everything about the guard, and it failed:
    /// <c>ParsePower</c> and <c>ParseUnary</c> were the only ladder functions on no <c>TryOpenProduction</c>
    /// path at all, so a 1,000-caret chain parsed to a 1,000-deep tree with ZERO <c>DAXP1007</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void G_P_REC_008b_a_caret_chain_hits_the_depth_counter_not_the_stack()
    {
        const int carets = 1_000;
        var source = Repeat("1 ^ ", carets) + "1";

        var tree = Parse(source);

        Assert.Equal(source, tree.Root.ToFullString());
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            $"A2 §2.1: {carets} carets is far past the 256 default, so the depth counter must stop the " +
            "descent. Zero DAXP1007 means ParsePower's right-operand descent opens no guarded production " +
            "and the CLR stack is the only thing left to stop it (A2PR1-M1).");
    }

    /// <summary>
    /// The same defect at the depth the guard is actually set to: the tree's depth must be a function of
    /// <see cref="DaxParseOptions.MaximumNestingDepth"/>, never of the input's length. 40 carets under a
    /// limit of 8 is the sharpest form of that, because a wrong answer here is 40 and a right one is 9.
    ///
    /// <para>
    /// EXACTLY <c>limit + 1</c>, and the extra one is not slack. The caret that is refused has already been
    /// eaten, so its binary node is still built, with a <c>MissingExpression</c> right operand carrying the
    /// refusal — the ladder records what was written (A2 §2 rule 4) rather than dropping the operator. The
    /// 8 nodes below it are the 8 productions the counter permitted.
    /// </para>
    /// </summary>
    [Fact]
    public void A_caret_chain_nests_exactly_as_deep_as_the_limit_allows_and_no_deeper()
    {
        const int limit = 8;
        const int carets = 40;
        var options = Options(maximumNestingDepth: limit);
        var source = Repeat("1 ^ ", carets) + "1";

        var tree = Parse(source, options);

        Assert.Equal(source, tree.Root.ToFullString());
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            "A2 §2.1: the caret chain must be stopped by MaximumNestingDepth, whatever it is set to.");

        var spine = RightSpine(Expr(tree));

        Assert.Equal(limit + 1, spine.Count);
        Assert.IsType<MissingExpressionSyntax>(spine[^1].Right);
    }

    /// <summary>
    /// G-P-REC-008b, out-of-process half. 10,000 carets, in a child process, exit code 0 required.
    /// Pre-fix the child died with 0xC00000FD (stack overflow) at anything above roughly 5,000 carets.
    /// </summary>
    [Fact]
    public void G_P_REC_008b_ten_thousand_carets_do_not_kill_the_process()
        => AssertProbeSurvives("power", 10_000);

    // ---- A2PR1-S1: the depth counter must be exact ---------------------------------------------------

    /// <summary>
    /// A2PR1-S1. <c>ParseParameterList</c> closed a production it never opened, so the UDF lambda body
    /// parsed one level DEEPER than <c>MaximumNestingDepth</c> permits and <c>_depth</c> finished a UDF
    /// parse at -1.
    ///
    /// <para>
    /// Fixed with M1 in the same commit deliberately: M1's guard is only as good as the counter it reads,
    /// and a counter that drifts negative silently buys back the level the guard just refused.
    /// </para>
    /// </summary>
    [Fact]
    public void A_udf_lambda_body_does_not_parse_one_level_deeper_than_the_limit()
    {
        var tree = ParseHarness.Parse("() => (1)", Options(sourceKind: DaxSourceKind.UdfBody, maximumNestingDepth: 1));

        Assert.Equal("() => (1)", tree.Root.ToFullString());
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            "A2 §2.1: the lambda occupies the single permitted level, so the parenthesized body must be " +
            "refused. An unmatched CloseProduction in ParseParameterList handed that level back (A2PR1-S1).");
    }

    /// <summary>
    /// The invariant behind S1, asserted directly: every <c>TryOpenProduction</c> that returned true is
    /// matched by exactly one <c>CloseProduction</c>, so the counter returns to zero. Pre-fix a UDF parse
    /// ended at -1, which is a bypass waiting for a second unmatched pair.
    /// </summary>
    [Theory]
    [InlineData("() => 1", DaxSourceKind.UdfBody)]
    [InlineData("(x, y) => x + y", DaxSourceKind.UdfBody)]
    [InlineData("(x: INT64 VAL = 1) => (x)", DaxSourceKind.UdfBody)]
    [InlineData("() => (1)", DaxSourceKind.UdfBody)]
    [InlineData("SUM([a]) + 1", DaxSourceKind.Expression)]
    [InlineData("VAR a = (1) RETURN a", DaxSourceKind.Expression)]
    public void The_nesting_depth_counter_returns_to_zero(string source, DaxSourceKind sourceKind)
    {
        var finalDepth = DaxParser.ParseRootAndReportFinalDepth(
            DaxSourceText.From(source), Options(sourceKind: sourceKind));

        Assert.Equal(0, finalDepth);
    }

    /// <summary>The same, at a limit low enough that recovery runs on every one of them.</summary>
    [Theory]
    [InlineData("() => (1)", DaxSourceKind.UdfBody)]
    [InlineData("(x) => ((x))", DaxSourceKind.UdfBody)]
    [InlineData("((((1))))", DaxSourceKind.Expression)]
    [InlineData("1 ^ 1 ^ 1 ^ 1 ^ 1", DaxSourceKind.Expression)]
    public void The_nesting_depth_counter_returns_to_zero_after_depth_recovery(string source, DaxSourceKind sourceKind)
    {
        var options = Options(sourceKind: sourceKind, maximumNestingDepth: 1);
        var finalDepth = DaxParser.ParseRootAndReportFinalDepth(DaxSourceText.From(source), options);

        Assert.Equal(0, finalDepth);

        // Non-vacuity: recovery really did run, so the zero above is not the trivial no-recovery case.
        var tree = Parse(source, options);
        Assert.True(CountOf(tree, DaxParserDiagnosticCodes.MaximumNestingDepthExceeded) >= 1,
            "This row is only meaningful if depth recovery fired.");
    }

    // ---- A2PR1-M2: the four public walkers, MEASURED not assumed ------------------------------------

    /// <summary>
    /// T182 step 1. The four token-walk commands now run at the contract's 200,000-term floor. The callback
    /// walker and rewriter rows stay skipped because steps 2-3 require a new guarded and replacement API
    /// shape; step 1 must not silently change those public callbacks. There is no visitor overflow row at
    /// 3,000: Linux Debug and Release both complete that depth, so 3,000 is not a visitor overflow.
    /// </summary>
    [Theory]
    [InlineData("descendant-tokens", 200_000)]
    [InlineData("descendant-trivia", 200_000)]
    [InlineData("first-token", 200_000)]
    [InlineData("last-token", 200_000)]
    public void C1_token_walks_survive_a_deep_flat_chain(string probe, int terms)
        => AssertProbeSurvives(probe, terms);

    [Theory(Skip = "T182 steps 2-3 remain open: walker and rewriter need a catchable net and replacement API, not a step 1 implementation change.")]
    [InlineData("walker", 3_000)]
    [InlineData("rewriter", 5_000)]
    public void A_recursive_callback_walker_survives_a_deep_flat_chain(string probe, int terms)
        => AssertProbeSurvives(probe, terms);

    [Fact]
    public void C2_token_kind_and_span_sequence_matches_the_recursive_contract()
    {
        const int terms = 2_000;
        var chain = Repeat("1 + ", terms - 1) + "1";
        var trees = new[]
        {
            Parse($"F({chain}, 2)"),
            Parse($"VAR x = {chain} RETURN x"),
            ParseUdf($"(x) => F({chain}, x)"),
            Parse($"{chain} ) recovery")
        };

        foreach (var tree in trees)
        {
            var expected = RecursiveTokens(tree.Root)
                .Select(TokenContract).ToArray();
            var actual = tree.Root.DescendantTokens()
                .Select(TokenContract).ToArray();

            Assert.Equal(expected, actual);
        }

        static (SyntaxKind Kind, TextSpan Span, TextSpan FullSpan) TokenContract(SyntaxToken token)
            => (token.Kind, token.Span, token.FullSpan);
    }

    [Fact]
    public void C3_nested_structured_trivia_interleaving_survives_depth()
        => AssertProbeSurvives("structured-trivia", 200_000);

    [Fact]
    public void C4_get_first_token_does_not_materialize_the_remaining_walk()
    {
        const int terms = 200_000;
        var tree = Parse(Repeat("1 + ", terms - 1) + "1");
        var touched = 0;
        SyntaxNode.DescendantTokenVisitObserver.Value = _ => touched++;

        try
        {
            var first = tree.Root.GetFirstToken();
            Assert.Equal(SyntaxKind.IntegerLiteralToken, first.Kind);
            Assert.Equal(new TextSpan(0, 1), first.Span);
            Assert.InRange(touched, terms, terms + 4);
        }
        finally
        {
            SyntaxNode.DescendantTokenVisitObserver.Value = null;
        }
    }

    /// <summary>
    /// The durable floors. Step 1 raises the four iterative token commands to 200,000 while preserving the
    /// measured 2,000 floor for the callback visitor, walker and rewriter that remain open. 3,000 is not a
    /// visitor overflow on Linux Debug or Release, so it is not a floor and not a skip.
    /// </summary>
    [Theory]
    [InlineData("descendant-tokens", 200_000)]
    [InlineData("descendant-trivia", 200_000)]
    [InlineData("first-token", 200_000)]
    [InlineData("last-token", 200_000)]
    [InlineData("visitor", 2_000)]
    [InlineData("walker", 2_000)]
    [InlineData("rewriter", 2_000)]
    public void A_public_walker_survives_up_to_its_published_limit(string probe, int terms)
        => AssertProbeSurvives(probe, terms);

    private static IEnumerable<SyntaxToken> RecursiveTokens(SyntaxNode node)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } childNode)
            {
                foreach (var token in RecursiveTokens(childNode)) yield return token;
            }
            else
            {
                yield return child.AsToken();
            }
        }
    }

    // ---- the out-of-process runner ------------------------------------------------------------------

    private static void AssertProbeSurvives(string probe, int terms)
    {
        var (exitCode, output, error) = RunProbe(probe, terms);

        Assert.True(exitCode == 0,
            $"Probe '{probe}' at {terms} terms exited {exitCode} (0x{exitCode:X8}). " +
            "A nonzero code that is not 2 means the CLR killed the child process, which for this input " +
            $"means the stack overflowed.{Environment.NewLine}stdout: {output}{Environment.NewLine}stderr: {error}");
    }

    private static (int ExitCode, string Output, string Error) RunProbe(string probe, int terms)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ProbePath.Value);
        startInfo.ArgumentList.Add(probe);
        startInfo.ArgumentList.Add(terms.ToString());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the depth probe.");

        // BOTH pipes are drained concurrently, deliberately. A .NET stack overflow prints "Stack overflow."
        // and then thousands of repeated frames to STDERR; reading stdout to the end first fills the stderr
        // pipe buffer, the child blocks writing, and the two processes deadlock forever. That deadlock is
        // exactly what this gate looked like when it was written the obvious way.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(milliseconds: 120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"The depth probe '{probe}' did not finish within two minutes; killed it rather than reporting a pass.");
        }

        return (process.ExitCode, Drain(output), Drain(error));

        static string Drain(Task<string> pipe)
            => pipe.Wait(TimeSpan.FromSeconds(30)) ? pipe.Result.Trim() : "<pipe did not drain>";
    }

    /// <summary>
    /// The probe is built by a <c>ReferenceOutputAssembly="false"</c> project reference, so it is BUILT
    /// beside us but not copied into our output. Locating it by walking up to the repository root keeps the
    /// gate honest: if the probe is missing this THROWS rather than skipping, because a gate that cannot run
    /// is not a gate that passed.
    /// </summary>
    private static readonly Lazy<string> ProbePath = new(() =>
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent?.Name
            ?? throw new InvalidOperationException($"Cannot read the build configuration from '{testOutput.FullName}'.");

        for (var directory = testOutput; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tools", "dax-deep-probe", "Semanticus.DaxDeepProbe",
                "bin", configuration, "net8.0", "Semanticus.DaxDeepProbe.dll");

            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"Semanticus.DaxDeepProbe.dll ({configuration}) was not found above '{AppContext.BaseDirectory}'. " +
            "The out-of-process depth gate cannot run, so it cannot pass. Build " +
            "tools/dax-deep-probe/Semanticus.DaxDeepProbe.");
    });

    /// <summary>The right-associative spine, outermost first. Walked iteratively, for obvious reasons.</summary>
    private static List<BinaryExpressionSyntax> RightSpine(ExpressionSyntax expression)
    {
        var spine = new List<BinaryExpressionSyntax>();
        while (expression is BinaryExpressionSyntax binary)
        {
            spine.Add(binary);
            expression = binary.Right;
        }
        return spine;
    }
}
