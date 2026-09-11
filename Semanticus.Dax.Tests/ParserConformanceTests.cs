using Semanticus.Dax.Syntax;
using Semanticus.Dax.Text;
using Xunit;
using static Semanticus.Dax.Tests.ParseHarness;

namespace Semanticus.Dax.Tests;

/// <summary>
/// The A2 §12.1 exit-gate items that are conformance assertions rather than goldens: the frozen
/// <see cref="SyntaxKind"/> inventory (§7, gate item 3), lexical-diagnostic conservation (§8.1 rule 6, gate
/// item 7), capability neutrality (§9, gate item 8), nine-source-kind parity (§3.1, gate item 9), the typed
/// root accessors (§3.2), and the zero-dependency rule (§12.1 item 11).
/// </summary>
public class ParserConformanceTests
{
    // ---- §7 SyntaxKind inventory (gate item 3) ----------------------------------------------------

    /// <summary>
    /// Every kind A2 §7.2 and §7.3 name, with its exact frozen number. A0-syntax-api-design §12.3 freezes
    /// these forever, so a renumber, an addition, or a removal must fail HERE rather than silently ship.
    /// </summary>
    public static readonly (string Name, int Value)[] FrozenKinds =
    {
        ("None", 0),

        // §7.2 punctuation and structural (1-99)
        ("OpenParenToken", 1), ("CloseParenToken", 2), ("OpenBraceToken", 3), ("CloseBraceToken", 4),
        ("CommaToken", 5), ("DotToken", 6), ("AtToken", 7), ("ColonToken", 8),

        // §7.2 operators (100-199)
        ("PlusToken", 100), ("MinusToken", 101), ("StarToken", 102), ("SlashToken", 103),
        ("CaretToken", 104), ("AmpersandToken", 105), ("EqualsToken", 106), ("StrictEqualsToken", 107),
        ("NotEqualsToken", 108), ("LessThanToken", 109), ("LessThanOrEqualsToken", 110),
        ("GreaterThanToken", 111), ("GreaterThanOrEqualsToken", 112), ("AndAndToken", 113),
        ("OrOrToken", 114), ("LambdaArrowToken", 115),

        // §7.2 literals (200-249)
        ("IntegerLiteralToken", 200), ("DecimalLiteralToken", 201), ("ScientificLiteralToken", 202),
        ("DateLiteralToken", 203), ("StringLiteralToken", 204),

        // §7.2 names (250-299)
        ("IdentifierToken", 250), ("QuotedIdentifierToken", 251), ("BracketedIdentifierToken", 252),

        // §7.2 terminal / recovery (300-349)
        ("BadToken", 300), ("EndOfFileToken", 301),

        // §7.2 reserved keywords (1000-1099), A1 §2.4 table order
        ("DefineKeyword", 1000), ("EvaluateKeyword", 1001), ("OrderKeyword", 1002), ("ByKeyword", 1003),
        ("StartKeyword", 1004), ("AtKeyword", 1005), ("VarKeyword", 1006), ("ReturnKeyword", 1007),
        ("MeasureKeyword", 1008), ("ColumnKeyword", 1009), ("TableKeyword", 1010), ("FunctionKeyword", 1011),
        ("WithKeyword", 1012), ("VisualKeyword", 1013), ("ShapeKeyword", 1014), ("AxisKeyword", 1015),
        ("GroupKeyword", 1016), ("TotalKeyword", 1017), ("DensifyKeyword", 1018), ("InKeyword", 1019),
        ("NotKeyword", 1020),

        // §7.2 trivia (1200-1299), A1 §2.2 order
        ("WhitespaceTrivia", 1200), ("EndOfLineTrivia", 1201), ("ByteOrderMarkTrivia", 1202),
        ("SlashSlashCommentTrivia", 1203), ("DashDashCommentTrivia", 1204),
        ("DocumentationCommentTrivia", 1205), ("DelimitedCommentTrivia", 1206), ("SkippedTokensTrivia", 1207),

        // §7.3 expressions (2000-2199)
        ("ParenthesizedExpression", 2000), ("PrefixUnaryExpression", 2001), ("BinaryExpression", 2002),
        ("MembershipExpression", 2003), ("CallExpression", 2004), ("LiteralExpression", 2005),

        // §7.3 names (2200-2299)
        ("IdentifierName", 2200), ("QuotedName", 2201), ("BracketedName", 2202), ("QualifiedName", 2203),
        ("DottedName", 2204),

        // §7.3 arguments and lists (2300-2399)
        ("ArgumentList", 2300), ("Argument", 2301), ("OmittedArgument", 2302),

        // §7.3 variables (2400-2499)
        ("VariableExpression", 2400), ("VariableDeclaration", 2401), ("ReturnClause", 2402),

        // §7.3 constructors (2500-2599)
        ("TableConstructorExpression", 2500), ("RowConstructorExpression", 2501),

        // §7.3 UDF (2600-2699)
        ("LambdaExpression", 2600), ("ParameterList", 2601), ("Parameter", 2602),
        ("ParameterTypeClause", 2603), ("TypeHint", 2604), ("SubtypeHint", 2605),
        ("PassingModeHint", 2606), ("DefaultValueClause", 2607),

        // §7.3 roots (3000-3099). 3002-3004 are PR-2's and must be absent.
        ("ExpressionCompilationUnit", 3000), ("UdfBodyCompilationUnit", 3001),

        // §7.3 recovery (4000-4099). 4002 is PR-2's and must be absent.
        ("SkippedTokens", 4000), ("MissingExpression", 4001)
    };

    [Fact]
    public void Every_syntax_kind_has_its_exact_frozen_numeric_value()
    {
        foreach (var (name, value) in FrozenKinds)
        {
            Assert.True(Enum.TryParse<SyntaxKind>(name, ignoreCase: false, out var kind),
                $"A2 §7: SyntaxKind.{name} is missing.");
            Assert.Equal(value, (int)kind);
        }
    }

    [Fact]
    public void The_syntax_kind_inventory_is_exactly_the_specified_set()
    {
        // No extra members either: an unlisted kind means §7 and the enum have diverged.
        Assert.Equal(
            FrozenKinds.Select(k => $"{k.Name}={k.Value}").OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            Enum.GetValues<SyntaxKind>().Select(k => $"{k}={(int)k}").OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A2 §12.1 item 3: no value is assigned in <c>3002-3004</c>, <c>3100-3499</c>, or <c>4002</c>. The
    /// other §7.1 reserved blocks are asserted too, because a PR-1 kind landing in any of them would burn a
    /// number PR-2 or a post-v1 language version is holding.
    /// </summary>
    [Theory]
    [InlineData(350, 999)]      // future token kinds
    [InlineData(1100, 1199)]    // future keyword tokens
    [InlineData(1300, 1999)]    // unallocated
    [InlineData(2700, 2999)]    // PR-1 family growth
    [InlineData(3002, 3004)]    // PR-2 roots
    [InlineData(3100, 3499)]    // PR-2 statements, definitions, visual shape, query-only names
    [InlineData(3500, 3999)]    // post-v1 DaxLanguageVersion surfaces
    [InlineData(4002, 4099)]    // 4002 = PR-2 UnknownStatement
    [InlineData(4100, 99999)]   // never assigned in v1
    public void No_syntax_kind_is_assigned_in_a_reserved_range(int lowInclusive, int highInclusive)
    {
        var offenders = Enum.GetValues<SyntaxKind>()
            .Where(k => (int)k >= lowInclusive && (int)k <= highInclusive)
            .Select(k => $"{k}={(int)k}")
            .ToArray();

        Assert.Empty(offenders);
    }

    // ---- §8.1 rule 6 conservation (gate item 7) ---------------------------------------------------

    /// <summary>
    /// A2 §8.1 rule 6 over the whole battery: the tree carries exactly the lexer's <c>DAXL</c> diagnostics,
    /// as a multiset of <c>(Code, Severity, Span)</c> triples. <c>Message</c> does not participate, because
    /// A0 §10.2 declares prose unstable and an invariant keyed on it would fail for a wording change.
    /// </summary>
    [Fact]
    public void Lexical_diagnostics_are_conserved_across_the_whole_battery()
    {
        var checkedInputs = 0;
        foreach (var (id, source, options) in ParserBattery.All())
        {
            var tree = Parse(source, options);
            var lexed = DaxLexer.Lex(DaxSourceText.From(source));
            var fromTree = tree.GetDiagnostics().Where(d => d.Code.StartsWith("DAXL", StringComparison.Ordinal)).ToList();

            Assert.True(lexed.Diagnostics.Count == fromTree.Count,
                $"{id}: lexer produced {lexed.Diagnostics.Count} DAXL diagnostics, tree reports {fromTree.Count}.");
            Assert.Equal(
                lexed.Diagnostics.Select(Key).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                fromTree.Select(Key).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            checkedInputs++;
        }

        Assert.True(checkedInputs > 100, $"the battery must actually be swept ({checkedInputs} inputs)");

        static string Key(DaxDiagnostic d) => $"{d.Code}/{d.Severity}[{d.Span.Start}..{d.Span.End})";
    }

    /// <summary>
    /// The invariant is only meaningful if some inputs actually carry lexical diagnostics, and only sharp if
    /// <c>Message</c> is genuinely excluded. Both are asserted here so the sweep above cannot pass vacuously.
    /// </summary>
    [Fact]
    public void The_conservation_sweep_is_not_vacuous_and_ignores_message_prose()
    {
        var withLexical = ParserBattery.All()
            .Count(row => DaxLexer.Lex(DaxSourceText.From(row.Source)).Diagnostics.Count > 0);
        Assert.True(withLexical >= 2, $"only {withLexical} battery inputs carry a DAXL diagnostic");

        // Re-hosting into skipped-token trivia preserves code, severity and span identically (§8.1 rule 4).
        var tree = Parse("SUM(#$%)");
        var lexed = DaxLexer.Lex(DaxSourceText.From("SUM(#$%)"));
        var treeSide = tree.GetDiagnostics().Where(d => d.Code.StartsWith("DAXL", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, treeSide.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(lexed.Diagnostics[i].Code, treeSide[i].Code);
            Assert.Equal(lexed.Diagnostics[i].Severity, treeSide[i].Severity);
            Assert.Equal(lexed.Diagnostics[i].Span, treeSide[i].Span);
        }
    }

    // ---- §9 capability neutrality (gate item 8) ---------------------------------------------------

    /// <summary>
    /// A2 §9 over the whole battery, under <c>null</c>, a deliberately minimal profile, and a deliberately
    /// maximal one: byte-identical serialized trees AND identical diagnostic lists. This is the only defence
    /// against a capability check leaking into a production.
    /// </summary>
    [Fact]
    public void Capability_profiles_never_change_the_tree_or_the_diagnostics()
    {
        foreach (var (id, source, options) in ParserBattery.All())
        {
            string? tree = null;
            string? diagnostics = null;

            foreach (var profile in CapabilityProfiles)
            {
                var parsed = Parse(source, Clone(options, capabilities: profile));
                tree ??= Render(parsed.Root);
                diagnostics ??= Serialize(parsed);
                Assert.True(tree == Render(parsed.Root), $"{id}: capability profile changed the tree.");
                Assert.True(diagnostics == Serialize(parsed), $"{id}: capability profile changed the diagnostics.");
            }
        }
    }

    [Fact]
    public void A_contradictory_capability_profile_is_still_neutral()
    {
        // A CL 1200 host claiming a CL 1702 feature. Absence means UNKNOWN, not false (A0 §9.4) — and none
        // of it can reach the parser anyway, which is the point.
        const string source = "(x: TABLE EXPR) => SUM(x[c])";
        var contradictory = new DaxTargetCapabilities(1200, "contradictory", new[] { "UDF", "NoUDF" });

        var baseline = ParseUdf(source);
        var probed = Parse(source, Options(sourceKind: DaxSourceKind.UdfBody, capabilities: contradictory));

        Assert.Equal(Render(baseline.Root), Render(probed.Root));
        Assert.Equal(Serialize(baseline), Serialize(probed));
    }

    // ---- §3.1 nine-source-kind parity (gate item 9) -----------------------------------------------

    /// <summary>
    /// A2 §3.1: for the nine expression source kinds the parse is STRUCTURALLY IDENTICAL — same node kinds,
    /// same child order, same spans, same diagnostics. The source kind selects binder context only, so the
    /// golden corpus does not multiply by 13.
    /// </summary>
    [Fact]
    public void The_nine_expression_source_kinds_parse_identically_across_the_battery()
    {
        Assert.Equal(9, ExpressionSourceKinds.Length);

        foreach (var (id, source, options) in ParserBattery.All())
        {
            if (options.SourceKind == DaxSourceKind.UdfBody) continue;

            string? tree = null;
            string? diagnostics = null;

            foreach (var kind in ExpressionSourceKinds)
            {
                var parsed = Parse(source, Clone(options, sourceKind: kind));
                tree ??= Render(parsed.Root);
                diagnostics ??= Serialize(parsed);
                Assert.True(tree == Render(parsed.Root), $"{id}: source kind {kind} changed the tree.");
                Assert.True(diagnostics == Serialize(parsed), $"{id}: source kind {kind} changed the diagnostics.");
                Assert.IsType<ExpressionCompilationUnitSyntax>(parsed.Root);
            }
        }
    }

    [Fact]
    public void Every_source_kind_maps_to_the_root_family_the_table_gives_it()
    {
        // A2 §3.1, read the other way: thirteen kinds, five root grammars, two implemented in PR-1.
        foreach (var kind in ExpressionSourceKinds)
            Assert.IsType<ExpressionCompilationUnitSyntax>(Parse("1", Options(sourceKind: kind)).Root);

        Assert.IsType<UdfBodyCompilationUnitSyntax>(Parse("() => 1", Options(sourceKind: DaxSourceKind.UdfBody)).Root);

        // The PR-2 families are reserved and must fail loudly rather than silently produce a wrong root.
        foreach (var kind in new[] { DaxSourceKind.Query, DaxSourceKind.VisualCalculation, DaxSourceKind.FormulaDefinition })
            Assert.Throws<NotSupportedException>(() => Parse("1", Options(sourceKind: kind)));
    }

    // ---- §3.2 typed root accessors ----------------------------------------------------------------

    [Fact]
    public void GetExpressionRoot_throws_on_a_mismatched_family()
    {
        var udf = ParseUdf("() => 1");

        Assert.Throws<InvalidOperationException>(() => udf.GetExpressionRoot());
        Assert.False(udf.TryGetExpressionRoot(out var expression));
        Assert.Null(expression);

        Assert.True(udf.TryGetUdfBodyRoot(out var body));
        Assert.NotNull(body);
        Assert.Same(udf.Root, udf.GetUdfBodyRoot());
        Assert.Same(udf.GetUdfBodyRoot(), body);
    }

    [Fact]
    public void GetUdfBodyRoot_throws_on_a_mismatched_family()
    {
        var expression = Parse("1 + 1");

        Assert.Throws<InvalidOperationException>(() => expression.GetUdfBodyRoot());
        Assert.False(expression.TryGetUdfBodyRoot(out var body));
        Assert.Null(body);

        Assert.True(expression.TryGetExpressionRoot(out var root));
        Assert.NotNull(root);
        Assert.Same(expression.Root, expression.GetExpressionRoot());
        Assert.Same(expression.GetExpressionRoot(), root);
    }

    /// <summary>
    /// A2 §3.2: root type is a function of <c>Options.SourceKind</c> ALONE, never of the input's content.
    /// A recovery-riddled parse still returns the correct root type, and the accessor is a projection of the
    /// already-parsed root rather than a reparse.
    /// </summary>
    [Fact]
    public void Root_type_is_a_function_of_the_source_kind_alone()
    {
        foreach (var junk in new[] { "", ")))", "#$%", "VAR", "=> => =>", "((((((" })
        {
            var expression = Parse(junk);
            Assert.IsType<ExpressionCompilationUnitSyntax>(expression.Root);
            // The accessor is a PROJECTION of the parsed root, not a reparse: same instance, every time.
            Assert.Same(expression.Root, expression.GetExpressionRoot());
            Assert.Throws<InvalidOperationException>(() => expression.GetUdfBodyRoot());

            var udf = ParseUdf(junk);
            Assert.IsType<UdfBodyCompilationUnitSyntax>(udf.Root);
            Assert.Same(udf.Root, udf.GetUdfBodyRoot());
            Assert.Throws<InvalidOperationException>(() => udf.GetExpressionRoot());
        }

        // A projection, not a reparse: the same root instance comes back every time.
        var tree = Parse("1 + 1");
        Assert.Same(tree.Root, tree.GetExpressionRoot());
        Assert.Same(tree.GetExpressionRoot(), tree.GetExpressionRoot());
    }

    [Fact]
    public void ParseExpression_rejects_a_non_expression_source_kind()
    {
        // A0 §7.1: the entrypoint rejects it rather than silently parsing the wrong grammar.
        Assert.Throws<ArgumentException>(() =>
            DaxSyntaxTree.ParseExpression("1", Options(sourceKind: DaxSourceKind.UdfBody)));

        // ...and ParseUdfBody coerces the kind rather than refusing a caller's otherwise-valid options.
        var coerced = DaxSyntaxTree.ParseUdfBody("() => 1", Options(sourceKind: DaxSourceKind.Measure));
        Assert.Equal(DaxSourceKind.UdfBody, coerced.Options.SourceKind);
        Assert.IsType<UdfBodyCompilationUnitSyntax>(coerced.Root);
    }

    // ---- §12.1 item 11: zero dependencies ---------------------------------------------------------

    /// <summary>
    /// A2 §12.1 item 11 / A1 §11.3 row 9: <c>Semanticus.Dax</c> carries no runtime package or project
    /// reference and no reference to Microsoft, TOM, ANTLR, Tabular Editor, or <c>Semanticus.Core</c>.
    /// Asserted from the loaded assembly, so it holds for what actually shipped rather than for what the
    /// csproj says.
    /// </summary>
    [Fact]
    public void Semanticus_Dax_has_no_forbidden_assembly_references()
    {
        var referenced = typeof(DaxSyntaxTree).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (var name in referenced)
            Assert.True(
                name is "System.Runtime" or "System.Collections" or "System.Linq" or "System.Memory" or
                        "System.Runtime.InteropServices" or "netstandard" or "System.Private.CoreLib" or
                        "System.Text.Encoding.Extensions" or "System.Threading",
                $"A2 §12.1 item 11: unexpected reference {name}.");

        foreach (var forbidden in new[] { "Microsoft", "Tabular", "Antlr", "Semanticus.Core", "Semanticus.Analysis" })
            Assert.DoesNotContain(referenced, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }
}
