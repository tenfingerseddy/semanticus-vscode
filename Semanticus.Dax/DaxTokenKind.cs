namespace Semanticus.Dax;

/// <summary>
/// The v1 lexical token inventory — reproduced EXACTLY from spec §2.1. Adding a kind requires amending
/// the spec and the public API baseline (spec §2.1); built-in DAX functions MUST NOT add token kinds.
/// </summary>
public enum DaxTokenKind
{
    None = 0, // Sentinel only; never emitted.

    BadToken,
    EndOfFileToken,

    IdentifierToken,
    QuotedIdentifierToken,
    BracketedIdentifierToken,

    IntegerLiteralToken,
    DecimalLiteralToken,
    ScientificLiteralToken,
    DateLiteralToken,
    StringLiteralToken,

    DefineKeyword,
    EvaluateKeyword,
    OrderKeyword,
    ByKeyword,
    StartKeyword,
    AtKeyword,
    VarKeyword,
    ReturnKeyword,
    MeasureKeyword,
    ColumnKeyword,
    TableKeyword,
    FunctionKeyword,
    WithKeyword,
    VisualKeyword,
    ShapeKeyword,
    AxisKeyword,
    GroupKeyword,
    TotalKeyword,
    DensifyKeyword,
    InKeyword,
    NotKeyword,

    OpenParenToken,
    CloseParenToken,
    OpenBraceToken,
    CloseBraceToken,
    CommaToken,
    DotToken,
    AtToken,
    ColonToken,

    PlusToken,
    MinusToken,
    StarToken,
    SlashToken,
    CaretToken,
    AmpersandToken,

    EqualsToken,
    StrictEqualsToken,
    NotEqualsToken,
    LessThanToken,
    LessThanOrEqualsToken,
    GreaterThanToken,
    GreaterThanOrEqualsToken,

    AndAndToken,
    OrOrToken,
    LambdaArrowToken
}
