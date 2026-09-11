namespace Semanticus.Dax.Syntax;

/// <summary>
/// The public syntax-kind inventory. Numeric values are FROZEN FOREVER (A0-syntax-api-design §12.3):
/// they are never changed and never reused, so PR-2 cannot renumber PR-1 and a post-v1 language version
/// cannot collide with either. The layout is A2 §7.1's range map, reproduced exactly.
///
/// Consumers MUST include a default branch: this enum is not exhaustive and gains members in minor
/// releases (A0 §12.3).
///
/// <para>
/// RESERVED RANGES — DO NOT ASSIGN. A member in any of these is a defect, not an addition:
/// </para>
/// <list type="bullet">
///   <item><c>350-999</c> future token kinds.</item>
///   <item><c>1100-1199</c> future keyword tokens.</item>
///   <item><c>1300-1999</c> unallocated.</item>
///   <item><c>2700-2999</c> PR-1 family growth.</item>
///   <item><c>3002-3004</c> PR-2 roots: QueryCompilationUnit, VisualCalculationCompilationUnit,
///         FormulaDefinitionCompilationUnit (A2 §7.3, §10.1).</item>
///   <item><c>3100-3199</c> PR-2 query statements · <c>3200-3299</c> PR-2 query definitions ·
///         <c>3300-3399</c> PR-2 visual shape · <c>3400-3499</c> PR-2 query-only expressions and names.</item>
///   <item><c>3500-3999</c> post-v1 <see cref="DaxLanguageVersion"/> surfaces.</item>
///   <item><c>4002</c> PR-2 recovery: UnknownStatement.</item>
///   <item><c>4100+</c> never assigned in v1.</item>
/// </list>
/// </summary>
public enum SyntaxKind
{
    None = 0,

    // ---- Punctuation and structural tokens (1-99), A2 §7.2 --------------------------------------
    OpenParenToken = 1,
    CloseParenToken = 2,
    OpenBraceToken = 3,
    CloseBraceToken = 4,
    CommaToken = 5,
    DotToken = 6,
    AtToken = 7,
    ColonToken = 8,

    // ---- Operator tokens (100-199) --------------------------------------------------------------
    PlusToken = 100,
    MinusToken = 101,
    StarToken = 102,
    SlashToken = 103,
    CaretToken = 104,
    AmpersandToken = 105,
    EqualsToken = 106,
    StrictEqualsToken = 107,
    NotEqualsToken = 108,
    LessThanToken = 109,
    LessThanOrEqualsToken = 110,
    GreaterThanToken = 111,
    GreaterThanOrEqualsToken = 112,
    AndAndToken = 113,
    OrOrToken = 114,
    LambdaArrowToken = 115,

    // ---- Literal tokens (200-249) ---------------------------------------------------------------
    IntegerLiteralToken = 200,
    DecimalLiteralToken = 201,
    ScientificLiteralToken = 202,
    DateLiteralToken = 203,
    StringLiteralToken = 204,

    // ---- Name tokens (250-299) ------------------------------------------------------------------
    IdentifierToken = 250,
    QuotedIdentifierToken = 251,
    BracketedIdentifierToken = 252,

    // ---- Terminal / recovery tokens (300-349) ---------------------------------------------------
    BadToken = 300,
    EndOfFileToken = 301,

    // ---- Reserved-keyword tokens (1000-1099), A1 §2.4 table order -------------------------------
    DefineKeyword = 1000,
    EvaluateKeyword = 1001,
    OrderKeyword = 1002,
    ByKeyword = 1003,
    StartKeyword = 1004,
    AtKeyword = 1005,
    VarKeyword = 1006,
    ReturnKeyword = 1007,
    MeasureKeyword = 1008,
    ColumnKeyword = 1009,
    TableKeyword = 1010,
    FunctionKeyword = 1011,
    WithKeyword = 1012,
    VisualKeyword = 1013,
    ShapeKeyword = 1014,
    AxisKeyword = 1015,
    GroupKeyword = 1016,
    TotalKeyword = 1017,
    DensifyKeyword = 1018,
    InKeyword = 1019,
    NotKeyword = 1020,

    // ---- Trivia kinds (1200-1299), A1 §2.2 order ------------------------------------------------
    WhitespaceTrivia = 1200,
    EndOfLineTrivia = 1201,
    ByteOrderMarkTrivia = 1202,
    SlashSlashCommentTrivia = 1203,
    DashDashCommentTrivia = 1204,
    DocumentationCommentTrivia = 1205,
    DelimitedCommentTrivia = 1206,
    SkippedTokensTrivia = 1207,

    // ---- Expression nodes (2000-2199), A2 §7.3 --------------------------------------------------
    ParenthesizedExpression = 2000,
    PrefixUnaryExpression = 2001,
    BinaryExpression = 2002,
    MembershipExpression = 2003,
    CallExpression = 2004,
    LiteralExpression = 2005,

    // ---- Name nodes (2200-2299) -----------------------------------------------------------------
    IdentifierName = 2200,
    QuotedName = 2201,
    BracketedName = 2202,
    QualifiedName = 2203,
    DottedName = 2204,

    // ---- Argument / list nodes (2300-2399) ------------------------------------------------------
    ArgumentList = 2300,
    Argument = 2301,
    OmittedArgument = 2302,

    // ---- Variable nodes (2400-2499) -------------------------------------------------------------
    VariableExpression = 2400,
    VariableDeclaration = 2401,
    ReturnClause = 2402,

    // ---- Constructor nodes (2500-2599) ----------------------------------------------------------
    TableConstructorExpression = 2500,
    RowConstructorExpression = 2501,

    // ---- UDF nodes (2600-2699) ------------------------------------------------------------------
    LambdaExpression = 2600,
    ParameterList = 2601,
    Parameter = 2602,
    ParameterTypeClause = 2603,
    TypeHint = 2604,
    SubtypeHint = 2605,
    PassingModeHint = 2606,
    DefaultValueClause = 2607,

    // ---- Roots (3000-3099). 3002-3004 are PR-2's and are deliberately absent. --------------------
    ExpressionCompilationUnit = 3000,
    UdfBodyCompilationUnit = 3001,

    // ---- Recovery nodes (4000-4099). 4002 (UnknownStatement) is PR-2's and is deliberately absent.
    SkippedTokens = 4000,
    MissingExpression = 4001
}
