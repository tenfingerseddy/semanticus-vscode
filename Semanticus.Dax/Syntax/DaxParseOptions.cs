namespace Semanticus.Dax.Syntax;

/// <summary>
/// What kind of source is being parsed (A0 §7.1). Thirteen kinds collapse onto five root grammars
/// (A2 §3.1); for the nine expression kinds the parse is structurally IDENTICAL and the kind selects only
/// binder context.
/// </summary>
public enum DaxSourceKind
{
    Expression,
    Measure,
    CalculatedColumn,
    CalculatedTable,
    CalculationItem,
    FormatStringExpression,
    RowLevelSecurity,
    DetailRows,
    DataCoverage,
    UdfBody,
    Query,
    VisualCalculation,
    FormulaDefinition
}

/// <summary>
/// The language surface to parse against (A0 §12.4). A new grammar surface ships as a NEW value; <c>V1</c>
/// keeps producing the same trees and diagnostics forever.
/// </summary>
public enum DaxLanguageVersion
{
    V1,

    /// <summary>The newest implemented version. For interactive tooling only; the Kernel never relies on it.</summary>
    Latest
}

/// <summary>
/// A neutral carrier describing the target host (A0 §9.4). It is NOT a TOM wrapper and it is NOT a parser
/// input.
///
/// <para>
/// NOTHING in <c>Semanticus.Dax</c> may read this. A2 §9 is normative: the parser reads
/// <c>TargetCapabilities</c> ZERO times, and for a fixed language version and root family the same source
/// must yield an identical tree and an identical diagnostic list under every profile, including null,
/// empty, and mutually contradictory ones. Capabilities may only ever add <c>DAXF</c> diagnostics, and
/// <c>DAXF</c> emission is deferred out of A2 entirely. This type is carried on the options and handed to
/// the binder untouched.
/// </para>
/// </summary>
public sealed class DaxTargetCapabilities
{
    public static DaxTargetCapabilities Unknown { get; } = new();

    public DaxTargetCapabilities(
        int? compatibilityLevel = null,
        string? productVersion = null,
        IEnumerable<string>? capabilities = null)
    {
        CompatibilityLevel = compatibilityLevel;
        ProductVersion = productVersion;
        Capabilities = capabilities is null
            ? EmptySet
            : new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Database compatibility level, or null when the host is unknown.</summary>
    public int? CompatibilityLevel { get; }

    /// <summary>Server/product/version string, or null when unknown.</summary>
    public string? ProductVersion { get; }

    /// <summary>
    /// Host capability flags, compared ordinal-ignore-case. Absence means UNKNOWN, not false (A0 §9.4):
    /// lack of host information must never be reported as an invented denial.
    /// </summary>
    public IReadOnlySet<string> Capabilities { get; }

    public bool Has(string capability) => Capabilities.Contains(capability);
}

/// <summary>Parse options (A0 §7.1).</summary>
public sealed class DaxParseOptions
{
    /// <summary>Pins <see cref="DaxLanguageVersion.V1"/>; it does NOT silently mean <c>Latest</c> (A0 §7.1).</summary>
    public static DaxParseOptions Default { get; } = new();

    public DaxLanguageVersion LanguageVersion { get; init; } = DaxLanguageVersion.V1;

    public DaxSourceKind SourceKind { get; init; } = DaxSourceKind.Expression;

    /// <summary>
    /// Defaults to FALSE (A2 §5.2 pin 4). It is a pure DIAGNOSTIC switch: a leading <c>=</c> is always
    /// consumed into the root's slot either way, and this only decides whether <c>DAXP1020</c> is emitted.
    /// Kernel model paths, which read TOM/TMDL expression properties where a leading <c>=</c> is invalid,
    /// get the honest error; formula-bar and paste paths opt in.
    /// </summary>
    public bool AllowLeadingEquals { get; init; }

    /// <summary>Carried for the binder and never read by the parser (A2 §9).</summary>
    public DaxTargetCapabilities? TargetCapabilities { get; init; }

    /// <summary>
    /// Concurrently open grammar productions (A2 §2.1). Default 256: the deepest delimiter nesting in the
    /// measured 3,555-expression corpus is 7, so 256 is roughly 36x the observed maximum while keeping
    /// recursive-descent frames far inside a default 1 MB stack.
    /// </summary>
    public int MaximumNestingDepth { get; init; } = 256;
}
