using System.Diagnostics.CodeAnalysis;
using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// One parsed tree (A0 §7.1). The root type is a function of <see cref="Options"/>'s
/// <see cref="DaxSourceKind"/> ALONE, never of the input's content: a recovery-riddled parse still returns
/// the correct root type (A2 §3.2).
/// </summary>
public sealed class DaxSyntaxTree
{
    private readonly GreenSyntaxNode _greenRoot;
    private CompilationUnitSyntax? _root;

    private DaxSyntaxTree(DaxSourceText text, DaxParseOptions options, GreenSyntaxNode greenRoot)
    {
        Text = text;
        Options = options;
        _greenRoot = greenRoot;
    }

    public DaxSourceText Text { get; }

    public DaxParseOptions Options { get; }

    /// <summary>
    /// Typed as <see cref="CompilationUnitSyntax"/> so consumers never begin with a cast (A2 §3.2). Use the
    /// typed root accessors to reach a specific family.
    /// </summary>
    public CompilationUnitSyntax Root
        => _root ??= (CompilationUnitSyntax)_greenRoot.CreateRed(null, 0, this);

    // ---- entrypoints ----------------------------------------------------------------------------

    public static DaxSyntaxTree Parse(DaxSourceText text, DaxParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var effective = options ?? DaxParseOptions.Default;
        RequireImplementedRoot(effective.SourceKind);
        return new DaxSyntaxTree(text, effective, DaxParser.ParseRoot(text, effective, cancellationToken));
    }

    /// <summary>Rejects a non-expression source kind in its options (A0 §7.1).</summary>
    public static DaxSyntaxTree ParseExpression(string text, DaxParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var effective = options ?? DaxParseOptions.Default;
        if (GetRootFamily(effective.SourceKind) != DaxRootFamily.Expression)
            throw new ArgumentException($"ParseExpression requires an expression source kind; {effective.SourceKind} maps to the {GetRootFamily(effective.SourceKind)} root.", nameof(options));
        return Parse(DaxSourceText.From(text), effective, cancellationToken);
    }

    /// <summary>Sets <see cref="DaxSourceKind.UdfBody"/> but otherwise uses the same lexer and parser.</summary>
    public static DaxSyntaxTree ParseUdfBody(string text, DaxParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var effective = options ?? DaxParseOptions.Default;
        if (effective.SourceKind != DaxSourceKind.UdfBody) effective = new DaxParseOptions
        {
            LanguageVersion = effective.LanguageVersion,
            SourceKind = DaxSourceKind.UdfBody,
            AllowLeadingEquals = effective.AllowLeadingEquals,
            TargetCapabilities = effective.TargetCapabilities,
            MaximumNestingDepth = effective.MaximumNestingDepth
        };
        return Parse(DaxSourceText.From(text), effective, cancellationToken);
    }

    /// <summary>
    /// RESERVED for PR-2. The query grammar is deliberately unspecified until a corpus of real generated
    /// queries exists; PR-2 must fill it against that corpus, not invent it from one documented example
    /// (A2 §1.1, §10).
    /// </summary>
    public static DaxSyntaxTree ParseQuery(string text, DaxParseOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The query root is reserved for PR-2 and is not implemented. See A2 §10.");

    /// <summary>RESERVED for PR-2, for the same reason as <see cref="ParseQuery"/>.</summary>
    public static DaxSyntaxTree ParseVisualCalculation(string text, DaxParseOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The visual-calculation root is reserved for PR-2 and is not implemented. See A2 §10.");

    // ---- typed root accessors (A2 §3.2) ---------------------------------------------------------

    /// <summary>
    /// A projection of the already-parsed root, never a reparse. Throws when <see cref="Options"/>'s source
    /// kind does not map to this root family per A2 §3.1; it never returns null and never returns a
    /// differently shaped root.
    /// </summary>
    public ExpressionCompilationUnitSyntax GetExpressionRoot()
        => Root as ExpressionCompilationUnitSyntax
           ?? throw new InvalidOperationException($"Source kind {Options.SourceKind} maps to the {GetRootFamily(Options.SourceKind)} root, not the expression root.");

    public bool TryGetExpressionRoot([NotNullWhen(true)] out ExpressionCompilationUnitSyntax? root)
    {
        root = Root as ExpressionCompilationUnitSyntax;
        return root is not null;
    }

    public UdfBodyCompilationUnitSyntax GetUdfBodyRoot()
        => Root as UdfBodyCompilationUnitSyntax
           ?? throw new InvalidOperationException($"Source kind {Options.SourceKind} maps to the {GetRootFamily(Options.SourceKind)} root, not the UDF-body root.");

    public bool TryGetUdfBodyRoot([NotNullWhen(true)] out UdfBodyCompilationUnitSyntax? root)
    {
        root = Root as UdfBodyCompilationUnitSyntax;
        return root is not null;
    }

    // PR-2 reserved (A2 §3.2, §10.2). Do not implement here; they need the query grammar first.
    //   public QueryCompilationUnitSyntax GetQueryRoot();
    //   public VisualCalculationCompilationUnitSyntax GetVisualCalculationRoot();
    //   public FormulaDefinitionCompilationUnitSyntax GetFormulaDefinitionRoot();

    // ---- diagnostics and edits ------------------------------------------------------------------

    /// <summary>
    /// Every <c>DAXL</c> and <c>DAXP</c> diagnostic in the tree, merged in nondecreasing <c>Span.Start</c>
    /// (A2 §8.1 rule 7). They are collected from the green elements they are attached to, which is what
    /// makes the conservation invariant (A2 §8.1 rule 6) meaningful: a lexical diagnostic appears here
    /// exactly as many times as the lexer produced it, with the same code, severity, and span.
    /// </summary>
    public IReadOnlyList<DaxDiagnostic> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collected = new List<DaxDiagnostic>();
        DiagnosticCollector.Collect(_greenRoot, collected);
        return DiagnosticCollector.Sorted(collected);
    }

    /// <summary>
    /// A full reparse is a legal v1 implementation (A0 §7.1); incremental green reuse is an optimization,
    /// not observable semantics, so the result must be indistinguishable from a clean parse.
    /// </summary>
    public DaxSyntaxTree WithChangedText(DaxSourceText newText, CancellationToken cancellationToken = default)
        => Parse(newText, Options, cancellationToken);

    // ---- source kind -> root family (A2 §3.1) ---------------------------------------------------

    internal static DaxRootFamily GetRootFamily(DaxSourceKind kind) => kind switch
    {
        DaxSourceKind.Expression or DaxSourceKind.Measure or DaxSourceKind.CalculatedColumn or
        DaxSourceKind.CalculatedTable or DaxSourceKind.CalculationItem or DaxSourceKind.FormatStringExpression or
        DaxSourceKind.RowLevelSecurity or DaxSourceKind.DetailRows or DaxSourceKind.DataCoverage
            => DaxRootFamily.Expression,

        DaxSourceKind.UdfBody => DaxRootFamily.UdfBody,
        DaxSourceKind.Query => DaxRootFamily.Query,
        DaxSourceKind.VisualCalculation => DaxRootFamily.VisualCalculation,
        DaxSourceKind.FormulaDefinition => DaxRootFamily.FormulaDefinition,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped DaxSourceKind.")
    };

    private static void RequireImplementedRoot(DaxSourceKind kind)
    {
        var family = GetRootFamily(kind);
        if (family is DaxRootFamily.Expression or DaxRootFamily.UdfBody) return;
        throw new NotSupportedException($"The {family} root is reserved for PR-2 and is not implemented. See A2 §10.");
    }
}

/// <summary>
/// The five root grammars thirteen source kinds collapse onto (A2 §3.1). Goldens are authored once per
/// root, not once per source kind.
/// </summary>
internal enum DaxRootFamily
{
    Expression,
    UdfBody,
    Query,
    VisualCalculation,
    FormulaDefinition
}
