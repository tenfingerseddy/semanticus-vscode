using System.Text;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.DaxDeepProbe;

/// <summary>
/// One deep-formula probe per invocation, in its own process.
///
/// <para>
/// Exit 0 means the probe completed and its own checks held. Exit 2 means a check failed. Any other exit
/// code is the CLR killing the process, which for these inputs means the stack overflowed: on Windows that
/// surfaces as 0xC0000409 or 0xC00000FD, on Linux as a SIGSEGV/SIGABRT-derived code. The caller only needs
/// "0 or not 0"; the code is reported so a reader can tell a failed assertion from a dead process.
/// </para>
/// <para>
/// Nothing here catches <c>StackOverflowException</c>, because .NET does not permit it. That is the whole
/// reason this program exists.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("usage: Semanticus.DaxDeepProbe <probe> [terms]");
            Console.Error.WriteLine("probes: power, descendant-tokens, descendant-trivia, first-token, last-token, structured-trivia, visitor, walker, rewriter, render");
            return 64;
        }

        var probe = args[0];
        var terms = args.Length == 2 ? int.Parse(args[1]) : 10_000;

        switch (probe)
        {
            case "power": return Power(terms);
            case "descendant-tokens": return DescendantTokens(terms);
            case "descendant-trivia": return DescendantTrivia(terms);
            case "first-token": return FirstToken(terms);
            case "last-token": return LastToken(terms);
            case "structured-trivia": return StructuredTrivia(terms);
            case "visitor": return Visitor(terms);
            case "walker": return Walker(terms);
            case "rewriter": return Rewriter(terms);
            case "render": return Render(terms);
            default:
                Console.Error.WriteLine($"unknown probe '{probe}'");
                return 64;
        }
    }

    // ---- A2PR1-M1: the caret chain -------------------------------------------------------------------

    /// <summary>
    /// <c>1 ^ 1 ^ 1 ...</c>. Level 2 is right associative, so each caret descends through
    /// <c>ParseUnary</c> into <c>ParsePower</c> again. Before the fix neither frame opened a guarded
    /// production, so the chain recursed on the CLR stack until the process died.
    /// </summary>
    private static int Power(int carets)
    {
        var source = Chain("1 ^ ", carets) + "1";
        var tree = DaxSyntaxTree.Parse(DaxSourceText.From(source));

        // Round-trip is the totality claim (A2 §2 rule 4) and it must survive depth recovery.
        if (!string.Equals(source, tree.Root.ToFullString(), StringComparison.Ordinal))
            return Fail("power: round-trip broken");

        var depthDiagnostics = tree.GetDiagnostics().Count(d => d.Code == "DAXP1007");
        Console.WriteLine($"power carets={carets} ok diagnostics={tree.GetDiagnostics().Count} DAXP1007={depthDiagnostics}");

        // Either the chain is genuinely iterative (no depth diagnostic) or the depth counter stopped it.
        // What must NOT happen is the stack deciding, which is what a nonzero exit code would show.
        return 0;
    }

    // ---- A2PR1-M2: the four public walkers -----------------------------------------------------------

    private static int DescendantTokens(int terms)
    {
        var (tree, source) = PlusChain(terms);
        var count = tree.Root.DescendantTokens().Count();
        Console.WriteLine($"descendant-tokens terms={terms} tokens={count} length={source.Length}");
        return count > 0 ? 0 : Fail("descendant-tokens: no tokens");
    }

    private static int DescendantTrivia(int terms)
    {
        var (tree, _) = PlusChain(terms);
        var count = tree.Root.DescendantTrivia().Count();
        Console.WriteLine($"descendant-trivia terms={terms} trivia={count}");
        return 0;
    }

    private static int FirstToken(int terms)
    {
        var (tree, _) = PlusChain(terms);
        var token = tree.Root.GetFirstToken();
        Console.WriteLine($"first-token terms={terms} kind={token.Kind} span={token.Span.Start}");
        return 0;
    }

    private static int LastToken(int terms)
    {
        var (tree, _) = PlusChain(terms);
        var token = tree.Root.GetLastToken();
        Console.WriteLine($"last-token terms={terms} kind={token.Kind} span={token.Span.Start}");
        return 0;
    }

    /// <summary>C3: structured trivia may itself contain tokens that own structured trivia.</summary>
    private static int StructuredTrivia(int terms)
    {
        var parsed = DaxSyntaxTree.ParseExpression("1");
        var root = parsed.GetExpressionRoot();
        var literal = (LiteralExpressionSyntax)root.Expression;
        var ordinary = new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ");

        GreenToken Nested(string text)
        {
            var token = new GreenToken(SyntaxKind.IdentifierToken, text, text, leadingTrivia: new[] { ordinary });
            for (var i = 0; i < terms; i++)
                token = new GreenToken(
                    SyntaxKind.IdentifierToken,
                    text,
                    text,
                    leadingTrivia: new[] { GreenFactory.SkippedTokensTrivia(new[] { token }) });
            return token;
        }

        var leading = GreenFactory.SkippedTokensTrivia(new[] { Nested("L") });
        var trailing = GreenFactory.SkippedTokensTrivia(new[] { Nested("R") });
        var surfaceGreen = new GreenToken(
            SyntaxKind.IntegerLiteralToken,
            "S",
            "S",
            leadingTrivia: new[] { leading },
            trailingTrivia: new[] { trailing });
        var surface = new SyntaxToken(surfaceGreen, null, 0, parsed);
        var testRoot = root.WithExpression(literal.WithToken(surface));

        var tokenCount = 0;
        foreach (var token in testRoot.DescendantTokens(descendIntoStructuredTrivia: true))
        {
            string expectedText;
            int expectedStart;
            if (tokenCount <= terms)
            {
                expectedText = "L";
                expectedStart = tokenCount + 1;
            }
            else if (tokenCount == terms + 1)
            {
                expectedText = "S";
                expectedStart = terms + 2;
            }
            else if (tokenCount <= 2 * terms + 2)
            {
                expectedText = "R";
                expectedStart = terms + 4 + tokenCount - (terms + 2);
            }
            else
            {
                expectedText = string.Empty;
                expectedStart = 2 * terms + 5;
            }

            if (token.Text != expectedText || token.Span != new TextSpan(expectedStart, expectedText.Length))
                return Fail($"structured-trivia: token {tokenCount} was '{token.Text}' {token.Span}, expected '{expectedText}' at {expectedStart}");
            tokenCount++;
        }

        var triviaCount = 0;
        foreach (var trivia in testRoot.DescendantTrivia(descendIntoStructuredTrivia: true))
        {
            var armIndex = triviaCount % (terms + 2);
            var expectedStart = triviaCount < terms + 2 ? 0 : terms + 3;
            var expectedKind = armIndex == terms + 1 ? SyntaxKind.WhitespaceTrivia : SyntaxKind.SkippedTokensTrivia;
            var expectedLength = armIndex == terms + 1 ? 1 : terms + 2 - armIndex;
            if (trivia.Kind != expectedKind || trivia.Span != new TextSpan(expectedStart, expectedLength))
                return Fail($"structured-trivia: trivia {triviaCount} was {trivia.Kind} {trivia.Span}, expected {expectedKind} [{expectedStart}..{expectedStart + expectedLength})");
            triviaCount++;
        }

        var expectedTokens = 2L * (terms + 1) + 2; // two nested arms, surface token, EOF
        var expectedTrivia = 2L * (terms + 2);     // outer + nested skipped trivia and one leaf space per arm
        if (tokenCount != expectedTokens || triviaCount != expectedTrivia)
            return Fail($"structured-trivia: tokens={tokenCount}/{expectedTokens} trivia={triviaCount}/{expectedTrivia}");

        Console.WriteLine($"structured-trivia depth={terms} tokens={tokenCount} trivia={triviaCount} surface-index={terms + 1}");
        return 0;
    }

    /// <summary>
    /// Direct <c>DaxSyntaxVisitor</c> probe. Both subclasses count <c>VisitLiteralExpression</c>
    /// callbacks exactly; a plus chain of N terms must produce N callbacks. The void visitor uses the
    /// stock recursive <c>DefaultVisit</c>. The generic visitor has to supply its own descent because
    /// <c>DaxSyntaxVisitor&lt;TResult&gt;.DefaultVisit</c> returns <c>default</c> and does not walk.
    /// </summary>
    private static int Visitor(int terms)
    {
        var (tree, _) = PlusChain(terms);

        var voidVisitor = new LiteralCountingVisitor();
        voidVisitor.Visit(tree.Root);
        if (voidVisitor.Literals != terms)
            return Fail($"visitor: void counted {voidVisitor.Literals} literals, expected {terms}");

        var resultVisitor = new LiteralCountingVisitor<int>();
        resultVisitor.Visit(tree.Root);
        if (resultVisitor.Literals != terms)
            return Fail($"visitor: generic counted {resultVisitor.Literals} literals, expected {terms}");

        Console.WriteLine($"visitor terms={terms} literals={voidVisitor.Literals}");
        return 0;
    }

    private static int Walker(int terms)
    {
        var (tree, _) = PlusChain(terms);
        var walker = new CountingWalker();
        walker.Visit(tree.Root);
        Console.WriteLine($"walker terms={terms} nodes={walker.Nodes} tokens={walker.Tokens} trivia={walker.Trivia}");
        return walker.Nodes > 0 ? 0 : Fail("walker: no nodes");
    }

    private static int Rewriter(int terms)
    {
        var (tree, source) = PlusChain(terms);
        var rewritten = new IdentityRewriter().Visit(tree.Root);
        if (rewritten is null) return Fail("rewriter: null result");
        if (!string.Equals(source, rewritten.ToFullString(), StringComparison.Ordinal))
            return Fail("rewriter: identity rewrite changed the text");
        Console.WriteLine($"rewriter terms={terms} ok length={source.Length}");
        return 0;
    }

    /// <summary>The suite's own renderer, which is <c>ChildNodesAndTokens</c> recursion, for comparison.</summary>
    private static int Render(int terms)
    {
        var (tree, _) = PlusChain(terms);
        var builder = new StringBuilder();
        RenderNode(tree.Root, builder);
        Console.WriteLine($"render terms={terms} chars={builder.Length}");
        return 0;
    }

    private static void RenderNode(SyntaxNode node, StringBuilder builder)
    {
        builder.Append(node.Kind).Append('\n');
        foreach (var child in node.ChildNodesAndTokens())
            if (child.AsNode() is { } childNode) RenderNode(childNode, builder);
    }

    // ---- shared -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>1 + 1 + ...</c>. The additive level is an unbounded <c>while</c> loop BY DESIGN, so the depth cap
    /// never fires and the tree really is <paramref name="terms"/>-1 deep on its left spine. That is the
    /// shape a machine-generated formula produces and the shape the recursive walkers overflow on.
    /// </summary>
    private static (DaxSyntaxTree Tree, string Source) PlusChain(int terms)
    {
        var source = string.Join(" + ", Enumerable.Repeat("1", terms));
        return (DaxSyntaxTree.Parse(DaxSourceText.From(source)), source);
    }

    private static string Chain(string unit, int count)
    {
        var builder = new StringBuilder(unit.Length * count);
        for (var i = 0; i < count; i++) builder.Append(unit);
        return builder.ToString();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private sealed class LiteralCountingVisitor : DaxSyntaxVisitor
    {
        public int Literals { get; private set; }

        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            Literals++;
            base.VisitLiteralExpression(node);
        }
    }

    private sealed class LiteralCountingVisitor<TResult> : DaxSyntaxVisitor<TResult>
    {
        public int Literals { get; private set; }

        protected override TResult? DefaultVisit(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes()) Visit(child);
            return default;
        }

        public override TResult? VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            Literals++;
            return base.VisitLiteralExpression(node);
        }
    }

    private sealed class CountingWalker : DaxSyntaxWalker
    {
        public CountingWalker() : base(SyntaxWalkerDepth.Trivia) { }

        public int Nodes { get; private set; }
        public int Tokens { get; private set; }
        public int Trivia { get; private set; }

        public override void Visit(SyntaxNode? node)
        {
            if (node is not null) Nodes++;
            base.Visit(node);
        }

        public override void VisitToken(SyntaxToken token)
        {
            Tokens++;
            base.VisitToken(token);
        }

        public override void VisitTrivia(SyntaxTrivia trivia)
        {
            Trivia++;
            base.VisitTrivia(trivia);
        }
    }

    private sealed class IdentityRewriter : DaxSyntaxRewriter
    {
    }
}
