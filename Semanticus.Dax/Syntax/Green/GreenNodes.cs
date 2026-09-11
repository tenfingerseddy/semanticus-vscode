namespace Semanticus.Dax.Syntax.Green;

// One green class per PR-1 node kind, with the EXACT ordered child slots of A2 §7.3. The bodies are
// mechanical on purpose: the slot order in the public constructor IS the source order, so a slot list
// that reads wrong here is visibly wrong against the spec table.

// ---- Expressions (2000-2005) --------------------------------------------------------------------

internal sealed class GreenParenthesizedExpression : GreenSyntaxNode
{
    internal GreenParenthesizedExpression(GreenToken openParenToken, GreenSyntaxNode expression, GreenToken closeParenToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { openParenToken, expression, closeParenToken }, diagnostics) { }

    private GreenParenthesizedExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ParenthesizedExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenParenthesizedExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ParenthesizedExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenPrefixUnaryExpression : GreenSyntaxNode
{
    internal GreenPrefixUnaryExpression(GreenToken operatorToken, GreenSyntaxNode operand, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { operatorToken, operand }, diagnostics) { }

    private GreenPrefixUnaryExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.PrefixUnaryExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenPrefixUnaryExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new PrefixUnaryExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenBinaryExpression : GreenSyntaxNode
{
    internal GreenBinaryExpression(GreenSyntaxNode left, GreenToken operatorToken, GreenSyntaxNode right, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { left, operatorToken, right }, diagnostics) { }

    private GreenBinaryExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.BinaryExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenBinaryExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new BinaryExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenMembershipExpression : GreenSyntaxNode
{
    internal GreenMembershipExpression(GreenSyntaxNode left, GreenToken inKeyword, GreenSyntaxNode right, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { left, inKeyword, right }, diagnostics) { }

    private GreenMembershipExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.MembershipExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenMembershipExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new MembershipExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenCallExpression : GreenSyntaxNode
{
    internal GreenCallExpression(GreenSyntaxNode name, GreenSyntaxNode argumentList, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { name, argumentList }, diagnostics) { }

    private GreenCallExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.CallExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenCallExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new CallExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenLiteralExpression : GreenSyntaxNode
{
    internal GreenLiteralExpression(GreenToken token, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { token }, diagnostics) { }

    private GreenLiteralExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.LiteralExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenLiteralExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new LiteralExpressionSyntax(this, parent, position, tree);
}

// ---- Names (2200-2204) --------------------------------------------------------------------------

internal sealed class GreenIdentifierName : GreenSyntaxNode
{
    internal GreenIdentifierName(GreenToken identifier, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { identifier }, diagnostics) { }

    private GreenIdentifierName(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.IdentifierName, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenIdentifierName(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new IdentifierNameSyntax(this, parent, position, tree);
}

internal sealed class GreenQuotedName : GreenSyntaxNode
{
    internal GreenQuotedName(GreenToken identifier, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { identifier }, diagnostics) { }

    private GreenQuotedName(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.QuotedName, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenQuotedName(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new QuotedNameSyntax(this, parent, position, tree);
}

internal sealed class GreenBracketedName : GreenSyntaxNode
{
    internal GreenBracketedName(GreenToken identifier, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { identifier }, diagnostics) { }

    private GreenBracketedName(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.BracketedName, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenBracketedName(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new BracketedNameSyntax(this, parent, position, tree);
}

internal sealed class GreenQualifiedName : GreenSyntaxNode
{
    internal GreenQualifiedName(GreenSyntaxNode qualifier, GreenSyntaxNode name, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { qualifier, name }, diagnostics) { }

    private GreenQualifiedName(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.QualifiedName, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenQualifiedName(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new QualifiedNameSyntax(this, parent, position, tree);
}

internal sealed class GreenDottedName : GreenSyntaxNode
{
    internal GreenDottedName(GreenSyntaxList segments, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { segments }, diagnostics) { }

    private GreenDottedName(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.DottedName, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenDottedName(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new DottedNameSyntax(this, parent, position, tree);
}

// ---- Arguments and lists (2300-2302) ------------------------------------------------------------

internal sealed class GreenArgumentList : GreenSyntaxNode
{
    internal GreenArgumentList(GreenToken openParenToken, GreenSyntaxList arguments, GreenToken closeParenToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { openParenToken, arguments, closeParenToken }, diagnostics) { }

    private GreenArgumentList(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ArgumentList, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenArgumentList(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ArgumentListSyntax(this, parent, position, tree);
}

internal sealed class GreenArgument : GreenSyntaxNode
{
    internal GreenArgument(GreenSyntaxNode expression, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { expression }, diagnostics) { }

    private GreenArgument(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.Argument, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenArgument(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ArgumentSyntax(this, parent, position, tree);
}

/// <summary>No children, zero width (A2 §4.5). Produced only inside a non-empty argument list.</summary>
internal sealed class GreenOmittedArgument : GreenSyntaxNode
{
    internal GreenOmittedArgument(IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(Array.Empty<GreenNode?>(), diagnostics) { }

    private GreenOmittedArgument(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.OmittedArgument, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenOmittedArgument(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new OmittedArgumentSyntax(this, parent, position, tree);
}

// ---- Variables (2400-2402) ----------------------------------------------------------------------

internal sealed class GreenVariableExpression : GreenSyntaxNode
{
    internal GreenVariableExpression(GreenSyntaxList declarations, GreenSyntaxNode returnClause, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { declarations, returnClause }, diagnostics) { }

    private GreenVariableExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.VariableExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenVariableExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new VariableExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenVariableDeclaration : GreenSyntaxNode
{
    internal GreenVariableDeclaration(GreenToken varKeyword, GreenToken identifier, GreenToken equalsToken, GreenSyntaxNode initializer, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { varKeyword, identifier, equalsToken, initializer }, diagnostics) { }

    private GreenVariableDeclaration(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.VariableDeclaration, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenVariableDeclaration(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new VariableDeclarationSyntax(this, parent, position, tree);
}

internal sealed class GreenReturnClause : GreenSyntaxNode
{
    internal GreenReturnClause(GreenToken returnKeyword, GreenSyntaxNode expression, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { returnKeyword, expression }, diagnostics) { }

    private GreenReturnClause(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ReturnClause, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenReturnClause(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ReturnClauseSyntax(this, parent, position, tree);
}

// ---- Constructors (2500-2501) -------------------------------------------------------------------

internal sealed class GreenTableConstructorExpression : GreenSyntaxNode
{
    internal GreenTableConstructorExpression(GreenToken openBraceToken, GreenSyntaxList elements, GreenToken closeBraceToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { openBraceToken, elements, closeBraceToken }, diagnostics) { }

    private GreenTableConstructorExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.TableConstructorExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenTableConstructorExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new TableConstructorExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenRowConstructorExpression : GreenSyntaxNode
{
    internal GreenRowConstructorExpression(GreenToken openParenToken, GreenSyntaxList fields, GreenToken closeParenToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { openParenToken, fields, closeParenToken }, diagnostics) { }

    private GreenRowConstructorExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.RowConstructorExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenRowConstructorExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new RowConstructorExpressionSyntax(this, parent, position, tree);
}

// ---- UDF (2600-2607) ----------------------------------------------------------------------------

internal sealed class GreenLambdaExpression : GreenSyntaxNode
{
    internal GreenLambdaExpression(GreenSyntaxNode parameterList, GreenToken arrowToken, GreenSyntaxNode body, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { parameterList, arrowToken, body }, diagnostics) { }

    private GreenLambdaExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.LambdaExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenLambdaExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new LambdaExpressionSyntax(this, parent, position, tree);
}

internal sealed class GreenParameterList : GreenSyntaxNode
{
    internal GreenParameterList(GreenToken openParenToken, GreenSyntaxList parameters, GreenToken closeParenToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { openParenToken, parameters, closeParenToken }, diagnostics) { }

    private GreenParameterList(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ParameterList, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenParameterList(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ParameterListSyntax(this, parent, position, tree);
}

/// <summary>
/// A2 §7.3: <c>T Identifier</c> · <c>N TypeClause?</c> · <c>N Default?</c>. Both optional NODE children
/// are null slots when absent, so the accessor returns null and nothing is enumerated (A2 §5.6 row 1).
/// </summary>
internal sealed class GreenParameter : GreenSyntaxNode
{
    internal GreenParameter(GreenToken identifier, GreenSyntaxNode? typeClause, GreenSyntaxNode? defaultValue, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { identifier, typeClause, defaultValue }, diagnostics) { }

    private GreenParameter(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.Parameter, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenParameter(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ParameterSyntax(this, parent, position, tree);
}

internal sealed class GreenParameterTypeClause : GreenSyntaxNode
{
    internal GreenParameterTypeClause(GreenToken colonToken, GreenSyntaxList hints, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { colonToken, hints }, diagnostics) { }

    private GreenParameterTypeClause(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ParameterTypeClause, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenParameterTypeClause(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ParameterTypeClauseSyntax(this, parent, position, tree);
}

internal sealed class GreenTypeHint : GreenSyntaxNode
{
    internal GreenTypeHint(GreenToken token, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { token }, diagnostics) { }

    private GreenTypeHint(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.TypeHint, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenTypeHint(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new TypeHintSyntax(this, parent, position, tree);
}

internal sealed class GreenSubtypeHint : GreenSyntaxNode
{
    internal GreenSubtypeHint(GreenToken token, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { token }, diagnostics) { }

    private GreenSubtypeHint(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.SubtypeHint, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenSubtypeHint(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new SubtypeHintSyntax(this, parent, position, tree);
}

internal sealed class GreenPassingModeHint : GreenSyntaxNode
{
    internal GreenPassingModeHint(GreenToken token, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { token }, diagnostics) { }

    private GreenPassingModeHint(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.PassingModeHint, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenPassingModeHint(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new PassingModeHintSyntax(this, parent, position, tree);
}

internal sealed class GreenDefaultValueClause : GreenSyntaxNode
{
    internal GreenDefaultValueClause(GreenToken equalsToken, GreenSyntaxNode expression, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { equalsToken, expression }, diagnostics) { }

    private GreenDefaultValueClause(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.DefaultValueClause, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenDefaultValueClause(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new DefaultValueClauseSyntax(this, parent, position, tree);
}

// ---- Roots (3000-3001) --------------------------------------------------------------------------

/// <summary>
/// A2 §7.3 kind 3000. The leading <c>=</c> slot is NULL when absent; the accessor then returns a default
/// <see cref="SyntaxToken"/> whose kind is <see cref="SyntaxKind.None"/>, which is not a tree token and is
/// never enumerated (A2 §5.2 pin 1, §5.6 row 2).
/// </summary>
internal sealed class GreenExpressionCompilationUnit : GreenSyntaxNode
{
    internal GreenExpressionCompilationUnit(GreenToken? leadingEqualsToken, GreenSyntaxNode expression, GreenToken endOfFileToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { leadingEqualsToken, expression, endOfFileToken }, diagnostics) { }

    private GreenExpressionCompilationUnit(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.ExpressionCompilationUnit, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenExpressionCompilationUnit(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new ExpressionCompilationUnitSyntax(this, parent, position, tree);
}

internal sealed class GreenUdfBodyCompilationUnit : GreenSyntaxNode
{
    internal GreenUdfBodyCompilationUnit(GreenSyntaxNode lambda, GreenToken endOfFileToken, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { lambda, endOfFileToken }, diagnostics) { }

    private GreenUdfBodyCompilationUnit(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.UdfBodyCompilationUnit, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenUdfBodyCompilationUnit(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new UdfBodyCompilationUnitSyntax(this, parent, position, tree);
}

// ---- Recovery (4000-4001) -----------------------------------------------------------------------

/// <summary>A2 §7.3 kind 4000: the structured node behind <c>SkippedTokensTrivia</c>.</summary>
internal sealed class GreenSkippedTokens : GreenSyntaxNode
{
    internal GreenSkippedTokens(GreenSyntaxList tokens, IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(new GreenNode?[] { tokens }, diagnostics) { }

    private GreenSkippedTokens(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.SkippedTokens, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenSkippedTokens(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new SkippedTokensSyntax(this, parent, position, tree);
}

/// <summary>
/// A2 §7.3 kind 4001. No children, zero width, <c>IsRecoveryNode == true</c>. It replaces the D2 spike's
/// habit of fabricating an identifier name with a missing token: fabricating a name asserts "there is a
/// name here", which is false and misleads every consumer (A2 §5.6).
/// </summary>
internal sealed class GreenMissingExpression : GreenSyntaxNode
{
    internal GreenMissingExpression(IReadOnlyList<DaxDiagnostic>? diagnostics = null)
        : this(Array.Empty<GreenNode?>(), diagnostics) { }

    private GreenMissingExpression(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        : base(SyntaxKind.MissingExpression, slots, diagnostics) { }

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics) => new GreenMissingExpression(slots, diagnostics);
    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree) => new MissingExpressionSyntax(this, parent, position, tree);
}
