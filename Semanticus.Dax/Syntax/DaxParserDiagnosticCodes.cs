namespace Semanticus.Dax.Syntax;

/// <summary>
/// The <c>DAXP</c> parser diagnostic catalogue for PR-1 (A2 §8.2). Codes and default severities are
/// STABLE (A0 §10.2); prose is not, so messages here are short and plain and no test may assert on them.
///
/// <para>
/// <c>DAXP1200-DAXP1299</c> is reserved for PR-2, which must not extend these blocks (A2 §10.5).
/// The parser deliberately owns none of the result-category, overload, arity, unknown-function,
/// unresolved-name, shadowing, or recursion diagnostics (all <c>DAXB</c>), nor any compatibility-level or
/// host-build diagnostic (all <c>DAXF</c>, deferred), nor any <c>DAXL</c> code (A1 owns those).
/// </para>
/// </summary>
public static class DaxParserDiagnosticCodes
{
    public const string ExpressionExpected = "DAXP1001";
    public const string CloseParenExpected = "DAXP1002";
    public const string CloseBraceExpected = "DAXP1003";
    public const string CommaExpected = "DAXP1004";
    public const string UnexpectedTokensSkipped = "DAXP1005";
    public const string UnexpectedContentAfterExpression = "DAXP1006";
    public const string MaximumNestingDepthExceeded = "DAXP1007";
    public const string IdentifierExpected = "DAXP1008";

    public const string LeadingEqualsNotPermitted = "DAXP1020";

    public const string DottedNameNotCallable = "DAXP1040";
    public const string EmptyDottedSegment = "DAXP1041";
    public const string DottedNameCannotQualify = "DAXP1042";
    public const string KeywordNotPermittedAsName = "DAXP1043";
    public const string NotCallable = "DAXP1045";

    public const string LambdaArrowExpected = "DAXP1060";
    public const string ParameterNameExpected = "DAXP1061";
    public const string ParameterHintExpected = "DAXP1062";
    public const string UnrecognizedParameterHint = "DAXP1063";
    public const string TooManyParameterHints = "DAXP1064";
    public const string ParameterHintsOutOfOrder = "DAXP1065";
    public const string DuplicateParameterHint = "DAXP1066";
    public const string LambdaOnlyAtUdfRoot = "DAXP1067";
    public const string ParameterListOpenParenExpected = "DAXP1068";
    public const string KeywordNotPermittedAsParameterName = "DAXP1069";

    public const string ReturnExpected = "DAXP1080";
    public const string VariableNameExpected = "DAXP1081";
    public const string VariableEqualsExpected = "DAXP1082";
    public const string VariableNameCannotBeDelimited = "DAXP1083";
    public const string KeywordNotPermittedAsVariableName = "DAXP1084";

    /// <summary>
    /// The default severity for a code, per the A2 §8.2 Sev column. Two are deliberate and must not drift:
    /// <c>DAXP1063</c> is the ONLY Warning, because a future Microsoft subtype must not turn a valid model
    /// into an error; <c>DAXP1069</c> is an Error, because A0-language-scope §5.2 flatly prohibits a
    /// reserved keyword as a parameter name — retaining the token is recovery, not permission.
    /// </summary>
    public static DaxDiagnosticSeverity GetDefaultSeverity(string code) => code switch
    {
        UnrecognizedParameterHint => DaxDiagnosticSeverity.Warning,

        ExpressionExpected or CloseParenExpected or CloseBraceExpected or CommaExpected
            or UnexpectedTokensSkipped or UnexpectedContentAfterExpression or MaximumNestingDepthExceeded
            or IdentifierExpected or LeadingEqualsNotPermitted or DottedNameNotCallable
            or EmptyDottedSegment or DottedNameCannotQualify or KeywordNotPermittedAsName or NotCallable
            or LambdaArrowExpected or ParameterNameExpected or ParameterHintExpected
            or TooManyParameterHints or ParameterHintsOutOfOrder or DuplicateParameterHint
            or LambdaOnlyAtUdfRoot or ParameterListOpenParenExpected or KeywordNotPermittedAsParameterName
            or ReturnExpected or VariableNameExpected or VariableEqualsExpected
            or VariableNameCannotBeDelimited or KeywordNotPermittedAsVariableName
            => DaxDiagnosticSeverity.Error,

        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a PR-1 DAXP code.")
    };

    /// <summary>
    /// Short, plain default message text. Prose is explicitly NOT stable (A0 §10.2), so no golden and no
    /// conformance invariant may key on these strings.
    /// </summary>
    public static string GetDefaultMessage(string code) => code switch
    {
        ExpressionExpected => "Expression expected.",
        CloseParenExpected => "')' expected.",
        CloseBraceExpected => "'}' expected.",
        CommaExpected => "',' expected.",
        UnexpectedTokensSkipped => "Unexpected tokens; they were skipped.",
        UnexpectedContentAfterExpression => "Unexpected content after the end of the expression.",
        MaximumNestingDepthExceeded => "Maximum nesting depth exceeded.",
        IdentifierExpected => "Identifier expected.",
        LeadingEqualsNotPermitted => "A leading '=' is not permitted for this source kind.",
        DottedNameNotCallable => "A dotted name is valid only as a function or UDF name.",
        EmptyDottedSegment => "Empty segment in a dotted name.",
        DottedNameCannotQualify => "A dotted name cannot qualify a bracketed name.",
        KeywordNotPermittedAsName => "Reserved keyword is not permitted as a name in this position.",
        NotCallable => "Only an identifier or a dotted name can be called.",
        LambdaArrowExpected => "'=>' expected after the parameter list.",
        ParameterNameExpected => "Parameter name expected.",
        ParameterHintExpected => "Parameter type hint expected after ':'.",
        UnrecognizedParameterHint => "Unrecognized parameter type hint.",
        TooManyParameterHints => "Too many parameter type hints; at most three are permitted.",
        ParameterHintsOutOfOrder => "Parameter type hints are out of order; the order is type, subtype, passing mode.",
        DuplicateParameterHint => "Duplicate parameter type hint.",
        LambdaOnlyAtUdfRoot => "A lambda is permitted only as a UDF body root.",
        ParameterListOpenParenExpected => "'(' expected at the start of a UDF parameter list.",
        KeywordNotPermittedAsParameterName => "A reserved keyword cannot be used as a parameter name.",
        ReturnExpected => "RETURN expected after the variable declarations.",
        VariableNameExpected => "Variable name expected.",
        VariableEqualsExpected => "'=' expected in a variable declaration.",
        VariableNameCannotBeDelimited => "A variable name cannot be delimited or dotted.",
        KeywordNotPermittedAsVariableName => "A reserved keyword cannot be used as a variable name.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a PR-1 DAXP code.")
    };
}
