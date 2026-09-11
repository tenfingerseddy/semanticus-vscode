using System.Text;
using Semanticus.Dax.Syntax;
using Semanticus.Dax.Text;
using Xunit;

namespace Semanticus.Dax.Tests;

/// <summary>
/// The parser companion to <see cref="LexHarness"/>, and it carries the same rigour for the same reason:
/// a golden that asserts a diagnostic code but not its span passes for a right code over a wrong span.
///
/// <para>
/// <see cref="Golden(string, string, Diag[])"/> is the single entry every A2 §11 row goes through. It
/// asserts the §11 preamble in full — exact node kinds, exact child order, exact spans, missing/recovery
/// flags, the diagnostic list as a SET of <c>(Code, Severity, Span)</c> triples, and ordinal round-trip —
/// and then adds the invariants that make the pinned span numbers self-verifying rather than transcribed:
/// child full-spans must TILE the parent's exactly, every token's span must slice its own raw text out of
/// the source, and the root's full span must be the whole source. A wrong span cannot satisfy those and
/// still equal the literal.
/// </para>
/// <para>
/// It also runs A2 §12.1 items 7, 8 and 9 on every row rather than once on the corpus: lexical-diagnostic
/// conservation (§8.1 rule 6), capability neutrality over three profiles (§9), and nine-source-kind parity
/// (§3.1). Those are cheap here and they turn "the corpus pass covered it" into "every golden covers it".
/// </para>
/// </summary>
internal static class ParseHarness
{
    // A2 §9: null, a deliberately minimal profile, and a deliberately maximal one. The middle profile is
    // mutually contradictory on purpose (CL 1200 host claiming a UDF capability) — the invariant is that
    // NOTHING here can reach the parser at all.
    public static readonly DaxTargetCapabilities?[] CapabilityProfiles =
    {
        null,
        new(compatibilityLevel: 1200, productVersion: "minimal", capabilities: Array.Empty<string>()),
        new(compatibilityLevel: 1702, productVersion: "maximal", capabilities: new[] { "UDF", "Calendars", "VisualCalculations", "AnythingElse" })
    };

    /// <summary>The nine source kinds that collapse onto the expression root (A2 §3.1).</summary>
    public static readonly DaxSourceKind[] ExpressionSourceKinds =
    {
        DaxSourceKind.Expression, DaxSourceKind.Measure, DaxSourceKind.CalculatedColumn,
        DaxSourceKind.CalculatedTable, DaxSourceKind.CalculationItem, DaxSourceKind.FormatStringExpression,
        DaxSourceKind.RowLevelSecurity, DaxSourceKind.DetailRows, DaxSourceKind.DataCoverage
    };

    // ---- entry points ---------------------------------------------------------------------------

    public static DaxSyntaxTree Parse(string source, DaxParseOptions? options = null)
        => DaxSyntaxTree.Parse(DaxSourceText.From(source), options ?? DaxParseOptions.Default);

    public static DaxSyntaxTree ParseUdf(string source)
        => DaxSyntaxTree.ParseUdfBody(source);

    public static DaxParseOptions Options(
        bool allowLeadingEquals = false,
        DaxSourceKind sourceKind = DaxSourceKind.Expression,
        DaxTargetCapabilities? capabilities = null,
        int maximumNestingDepth = 256)
        => new()
        {
            SourceKind = sourceKind,
            AllowLeadingEquals = allowLeadingEquals,
            TargetCapabilities = capabilities,
            MaximumNestingDepth = maximumNestingDepth
        };

    // ---- the golden entry -----------------------------------------------------------------------

    public static DaxSyntaxTree Golden(string source, string expectedTree, params Diag[] expectedDiagnostics)
        => Golden(source, DaxParseOptions.Default, expectedTree, expectedDiagnostics);

    public static DaxSyntaxTree GoldenUdf(string source, string expectedTree, params Diag[] expectedDiagnostics)
        => Golden(source, Options(sourceKind: DaxSourceKind.UdfBody), expectedTree, expectedDiagnostics);

    public static DaxSyntaxTree Golden(string source, DaxParseOptions options, string expectedTree, params Diag[] expectedDiagnostics)
    {
        var tree = Parse(source, options);

        Assert.Equal(Normalize(expectedTree), Render(tree.Root));
        AssertDiagnostics(tree, expectedDiagnostics);
        AssertUniversal(tree, source, options);
        return tree;
    }

    /// <summary>
    /// Everything that must hold for EVERY input, golden or fuzz (A2 §2 rules 3-5, §3.1, §8.1 rule 6, §9).
    /// Kept separate from <see cref="Golden"/> so the property tests can reuse it verbatim.
    /// </summary>
    public static void AssertUniversal(DaxSyntaxTree tree, string source, DaxParseOptions options)
    {
        // A2 §2 rule 4: tree-level tiling, ordinally.
        Assert.Equal(source, tree.Root.ToFullString());

        AssertSpansTile(tree.Root, source);
        AssertLexicalConservation(tree, source);
        AssertCapabilityNeutral(source, options);
        AssertSourceKindParity(source, options);
        AssertDeterministic(source, options);
    }

    // ---- rendering ------------------------------------------------------------------------------

    /// <summary>
    /// A compact deterministic rendering: kind, span, raw text, and the missing/recovery flags, indented by
    /// depth. Ordinary trivia is deliberately NOT rendered — round-trip plus the tiling assertion already
    /// pin every code unit of it — but SKIPPED-TOKEN trivia is, because where a rejected token landed is the
    /// whole point of half of §11.7 and §11.8.
    /// </summary>
    public static string Render(SyntaxNode root)
    {
        var builder = new StringBuilder();
        RenderNode(root, 0, builder);
        return builder.ToString();
    }

    private static void RenderNode(SyntaxNode node, int depth, StringBuilder builder)
    {
        Indent(builder, depth).Append(node.Kind).Append(' ').Append(Span(node.Span));
        if (node.IsRecoveryNode) builder.Append(" RECOVERY");
        builder.Append('\n');

        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } childNode) RenderNode(childNode, depth + 1, builder);
            else RenderToken(child.AsToken(), depth + 1, builder);
        }
    }

    private static void RenderToken(SyntaxToken token, int depth, StringBuilder builder)
    {
        Indent(builder, depth).Append(token.Kind).Append(' ').Append(Span(token.Span));
        if (token.IsMissing) builder.Append(" MISSING");
        else if (token.Text.Length > 0) builder.Append(" \"").Append(Escape(token.Text)).Append('"');
        builder.Append('\n');

        RenderStructuredTrivia(token.LeadingTrivia, "lead", depth + 1, builder);
        RenderStructuredTrivia(token.TrailingTrivia, "trail", depth + 1, builder);
    }

    private static void RenderStructuredTrivia(SyntaxTriviaList trivia, string side, int depth, StringBuilder builder)
    {
        foreach (var item in trivia)
        {
            if (item.GetStructure() is not { } structure) continue;
            Indent(builder, depth).Append(side).Append(' ').Append(item.Kind).Append(' ').Append(Span(item.Span)).Append('\n');
            RenderNode(structure, depth + 1, builder);
        }
    }

    private static StringBuilder Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);

    private static string Span(TextSpan span) => $"[{span.Start}..{span.End})";

    private static string Escape(string text) => text
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");

    /// <summary>
    /// Goldens are written as C# raw string literals, which already strip the common indentation, so this
    /// only normalizes line endings and the trailing newline.
    /// </summary>
    private static string Normalize(string expected)
    {
        var text = expected.Replace("\r\n", "\n").Trim('\n');
        return text.Length == 0 ? string.Empty : text + "\n";
    }

    // ---- diagnostics ----------------------------------------------------------------------------

    /// <summary>
    /// A2 §11 preamble and §8.4 rule 7: the diagnostic list is asserted as a SET of
    /// <c>(Code, Severity, Span)</c> triples. Never message prose (A0 §10.2 declares it unstable) and never
    /// order (order among equal starts is explicitly unspecified). Nondecreasing <c>Span.Start</c> IS
    /// asserted, because §8.1 rule 7 promises it.
    /// </summary>
    public static void AssertDiagnostics(DaxSyntaxTree tree, params Diag[] expected)
    {
        var actual = tree.GetDiagnostics();

        for (var i = 1; i < actual.Count; i++)
            Assert.True(actual[i].Span.Start >= actual[i - 1].Span.Start,
                $"A2 §8.1 rule 7: diagnostics must be in nondecreasing Span.Start order ({actual[i - 1].Span.Start} then {actual[i].Span.Start}).");

        Assert.Equal(
            expected.Select(d => d.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            actual.Select(d => new Diag(d.Code, d.Severity, d.Span.Start, d.Span.Length).ToString())
                  .OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    public static Diag Err(string code, int start, int length) => new(code, DaxDiagnosticSeverity.Error, start, length);

    public static Diag Warn(string code, int start, int length) => new(code, DaxDiagnosticSeverity.Warning, start, length);

    public readonly record struct Diag(string Code, DaxDiagnosticSeverity Severity, int Start, int Length)
    {
        public override string ToString() => $"{Code}/{Severity}[{Start}..{Start + Length})";
    }

    // ---- universal invariants -------------------------------------------------------------------

    /// <summary>
    /// A0 §4.3 / A1 §7.4 lifted to the tree: a node's children TILE its full span exactly, in source order,
    /// with no gap and no overlap, and every token's raw span slices its own text out of the source. This is
    /// what makes the span numbers inside a golden literal a proof rather than a transcript.
    /// </summary>
    public static void AssertSpansTile(SyntaxNode root, string source)
    {
        Assert.Equal(0, root.FullSpan.Start);
        Assert.Equal(source.Length, root.FullSpan.End);
        TileNode(root, source);
    }

    private static void TileNode(SyntaxNode node, string source)
    {
        var cursor = node.FullSpan.Start;
        var any = false;

        foreach (var child in node.ChildNodesAndTokens())
        {
            any = true;
            Assert.Equal(cursor, child.FullSpan.Start);
            cursor = child.FullSpan.End;

            if (child.AsNode() is { } childNode)
            {
                Assert.Same(node, childNode.Parent);
                TileNode(childNode, source);
            }
            else
            {
                var token = child.AsToken();
                Assert.Equal(token.Text.Length, token.Span.Length);
                Assert.Equal(token.Text, source.Substring(token.Span.Start, token.Span.Length));
                if (token.IsMissing) Assert.Equal(0, token.Span.Length);

                var triviaCursor = token.FullSpan.Start;
                foreach (var trivia in token.LeadingTrivia)
                {
                    Assert.Equal(triviaCursor, trivia.Span.Start);
                    Assert.Equal(trivia.Text, source.Substring(trivia.Span.Start, trivia.Span.Length));
                    triviaCursor = trivia.Span.End;
                }
                Assert.Equal(token.Span.Start, triviaCursor);
                triviaCursor = token.Span.End;
                foreach (var trivia in token.TrailingTrivia)
                {
                    Assert.Equal(triviaCursor, trivia.Span.Start);
                    Assert.Equal(trivia.Text, source.Substring(trivia.Span.Start, trivia.Span.Length));
                    triviaCursor = trivia.Span.End;
                }
                Assert.Equal(token.FullSpan.End, triviaCursor);
            }
        }

        // A childless node is one of the two zero-width recovery/hole nodes (A2 §7.3 kinds 4001, 2302).
        if (any) Assert.Equal(node.FullSpan.End, cursor);
        else Assert.Equal(0, node.FullSpan.Length);
    }

    /// <summary>
    /// A2 §8.1 rule 6, mechanically: the tree carries exactly the lexer's <c>DAXL</c> diagnostics, as a
    /// multiset of <c>(Code, Severity, Span)</c> triples. <c>Message</c> deliberately does not participate.
    /// </summary>
    public static void AssertLexicalConservation(DaxSyntaxTree tree, string source)
    {
        var lexed = DaxLexer.Lex(DaxSourceText.From(source));
        var fromTree = tree.GetDiagnostics().Where(d => d.Code.StartsWith("DAXL", StringComparison.Ordinal)).ToList();

        Assert.Equal(lexed.Diagnostics.Count, fromTree.Count);
        Assert.Equal(
            lexed.Diagnostics.Select(Key).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            fromTree.Select(Key).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        static string Key(DaxDiagnostic d) => $"{d.Code}/{d.Severity}[{d.Span.Start}..{d.Span.End})";
    }

    /// <summary>A2 §9: byte-identical trees and identical diagnostics under every capability profile.</summary>
    public static void AssertCapabilityNeutral(string source, DaxParseOptions options)
    {
        string? baseline = null;
        string? baselineDiagnostics = null;

        foreach (var profile in CapabilityProfiles)
        {
            var tree = Parse(source, Clone(options, capabilities: profile));
            var rendered = Render(tree.Root);
            var diagnostics = Serialize(tree);

            baseline ??= rendered;
            baselineDiagnostics ??= diagnostics;
            Assert.Equal(baseline, rendered);
            Assert.Equal(baselineDiagnostics, diagnostics);
        }
    }

    /// <summary>
    /// A2 §3.1: for the nine expression source kinds the parse is structurally identical and carries
    /// identical diagnostics. Skipped for the UDF-body root, which is its own family.
    /// </summary>
    public static void AssertSourceKindParity(string source, DaxParseOptions options)
    {
        if (options.SourceKind == DaxSourceKind.UdfBody) return;

        string? baseline = null;
        string? baselineDiagnostics = null;

        foreach (var kind in ExpressionSourceKinds)
        {
            var tree = Parse(source, Clone(options, sourceKind: kind));
            baseline ??= Render(tree.Root);
            baselineDiagnostics ??= Serialize(tree);
            Assert.Equal(baseline, Render(tree.Root));
            Assert.Equal(baselineDiagnostics, Serialize(tree));
        }
    }

    /// <summary>A2 §2 rule 5: the same inputs yield the same tree and the same diagnostics, run to run.</summary>
    public static void AssertDeterministic(string source, DaxParseOptions options)
    {
        var first = Parse(source, options);
        var second = Parse(source, options);
        Assert.Equal(Render(first.Root), Render(second.Root));
        Assert.Equal(Serialize(first), Serialize(second));
    }

    public static string Serialize(DaxSyntaxTree tree)
        => string.Join("\n", tree.GetDiagnostics().Select(d => $"{d.Code}/{d.Severity}[{d.Span.Start}..{d.Span.End})"));

    public static DaxParseOptions Clone(
        DaxParseOptions options,
        DaxSourceKind? sourceKind = null,
        DaxTargetCapabilities? capabilities = null)
        => new()
        {
            LanguageVersion = options.LanguageVersion,
            SourceKind = sourceKind ?? options.SourceKind,
            AllowLeadingEquals = options.AllowLeadingEquals,
            TargetCapabilities = capabilities,
            MaximumNestingDepth = options.MaximumNestingDepth
        };

    // ---- shape helpers used by the spec-derived assertions ---------------------------------------

    public static ExpressionSyntax Expr(DaxSyntaxTree tree) => tree.GetExpressionRoot().Expression;

    public static LambdaExpressionSyntax Lambda(DaxSyntaxTree tree) => tree.GetUdfBodyRoot().Lambda;

    public static string[] Codes(DaxSyntaxTree tree) => tree.GetDiagnostics().Select(d => d.Code).ToArray();

    public static int CountOf(DaxSyntaxTree tree, string code) => tree.GetDiagnostics().Count(d => d.Code == code);

    /// <summary>Every token of the tree INCLUDING those re-hosted in skipped-token trivia, in source order.</summary>
    public static List<SyntaxToken> AllTokens(SyntaxNode root)
        => root.DescendantTokens(descendIntoStructuredTrivia: true).ToList();

    public static string Repeat(string unit, int count)
    {
        var builder = new StringBuilder(unit.Length * count);
        for (var i = 0; i < count; i++) builder.Append(unit);
        return builder.ToString();
    }
}
