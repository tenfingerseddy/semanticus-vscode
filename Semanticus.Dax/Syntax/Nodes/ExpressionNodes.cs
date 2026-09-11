using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax;

// Child accessors are named exactly as the A2 §7.3 slot tables name them, and the slot INDEX in each
// accessor is the source-order position from that table. Update/WithX are mechanical (A0 §7.6): they
// return a detached node, preserving the original node's own diagnostics unless the caller replaces them.

/// <summary>A2 §7.3 kind 2000: <c>( expression )</c>. One top-level comma makes it a row constructor instead (A2 §5.3).</summary>
public sealed class ParenthesizedExpressionSyntax : ExpressionSyntax
{
    internal ParenthesizedExpressionSyntax(GreenParenthesizedExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OpenParenToken => GetToken(0);
    public ExpressionSyntax Expression => (ExpressionSyntax)GetRequiredNode(1);
    public SyntaxToken CloseParenToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitParenthesizedExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitParenthesizedExpression(this);

    public ParenthesizedExpressionSyntax Update(SyntaxToken openParenToken, ExpressionSyntax expression, SyntaxToken closeParenToken)
    {
        if (openParenToken == OpenParenToken && expression == Expression && closeParenToken == CloseParenToken) return this;
        return new ParenthesizedExpressionSyntax(
            new GreenParenthesizedExpression(RequiredGreen(openParenToken, nameof(openParenToken)), RequiredGreen(expression, nameof(expression)), RequiredGreen(closeParenToken, nameof(closeParenToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ParenthesizedExpressionSyntax WithOpenParenToken(SyntaxToken openParenToken) => Update(openParenToken, Expression, CloseParenToken);
    public ParenthesizedExpressionSyntax WithExpression(ExpressionSyntax expression) => Update(OpenParenToken, expression, CloseParenToken);
    public ParenthesizedExpressionSyntax WithCloseParenToken(SyntaxToken closeParenToken) => Update(OpenParenToken, Expression, closeParenToken);
}

/// <summary>A2 §7.3 kind 2001: a level-3 sign or the level-8 <c>NOT</c>.</summary>
public sealed class PrefixUnaryExpressionSyntax : ExpressionSyntax
{
    internal PrefixUnaryExpressionSyntax(GreenPrefixUnaryExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OperatorToken => GetToken(0);
    public ExpressionSyntax Operand => (ExpressionSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitPrefixUnaryExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitPrefixUnaryExpression(this);

    public PrefixUnaryExpressionSyntax Update(SyntaxToken operatorToken, ExpressionSyntax operand)
    {
        if (operatorToken == OperatorToken && operand == Operand) return this;
        return new PrefixUnaryExpressionSyntax(
            new GreenPrefixUnaryExpression(RequiredGreen(operatorToken, nameof(operatorToken)), RequiredGreen(operand, nameof(operand)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public PrefixUnaryExpressionSyntax WithOperatorToken(SyntaxToken operatorToken) => Update(operatorToken, Operand);
    public PrefixUnaryExpressionSyntax WithOperand(ExpressionSyntax operand) => Update(OperatorToken, operand);
}

/// <summary>A2 §7.3 kind 2002. Every binary level except <c>IN</c>, which is a membership expression.</summary>
public sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    internal BinaryExpressionSyntax(GreenBinaryExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public ExpressionSyntax Left => (ExpressionSyntax)GetRequiredNode(0);
    public SyntaxToken OperatorToken => GetToken(1);
    public ExpressionSyntax Right => (ExpressionSyntax)GetRequiredNode(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitBinaryExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitBinaryExpression(this);

    public BinaryExpressionSyntax Update(ExpressionSyntax left, SyntaxToken operatorToken, ExpressionSyntax right)
    {
        if (left == Left && operatorToken == OperatorToken && right == Right) return this;
        return new BinaryExpressionSyntax(
            new GreenBinaryExpression(RequiredGreen(left, nameof(left)), RequiredGreen(operatorToken, nameof(operatorToken)), RequiredGreen(right, nameof(right)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public BinaryExpressionSyntax WithLeft(ExpressionSyntax left) => Update(left, OperatorToken, Right);
    public BinaryExpressionSyntax WithOperatorToken(SyntaxToken operatorToken) => Update(Left, operatorToken, Right);
    public BinaryExpressionSyntax WithRight(ExpressionSyntax right) => Update(Left, OperatorToken, right);
}

/// <summary>
/// A2 §7.3 kind 2003: <c>left IN right</c>. There is no combined <c>NOT IN</c> operator: <c>NOT</c> is
/// level 8, so <c>NOT [a] IN {1}</c> is <c>NOT([a] IN {1})</c> (A2 §5.1).
/// </summary>
public sealed class MembershipExpressionSyntax : ExpressionSyntax
{
    internal MembershipExpressionSyntax(GreenMembershipExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public ExpressionSyntax Left => (ExpressionSyntax)GetRequiredNode(0);
    public SyntaxToken InKeyword => GetToken(1);
    public ExpressionSyntax Right => (ExpressionSyntax)GetRequiredNode(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitMembershipExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitMembershipExpression(this);

    public MembershipExpressionSyntax Update(ExpressionSyntax left, SyntaxToken inKeyword, ExpressionSyntax right)
    {
        if (left == Left && inKeyword == InKeyword && right == Right) return this;
        return new MembershipExpressionSyntax(
            new GreenMembershipExpression(RequiredGreen(left, nameof(left)), RequiredGreen(inKeyword, nameof(inKeyword)), RequiredGreen(right, nameof(right)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public MembershipExpressionSyntax WithLeft(ExpressionSyntax left) => Update(left, InKeyword, Right);
    public MembershipExpressionSyntax WithInKeyword(SyntaxToken inKeyword) => Update(Left, inKeyword, Right);
    public MembershipExpressionSyntax WithRight(ExpressionSyntax right) => Update(Left, InKeyword, right);
}

/// <summary>
/// A2 §7.3 kind 2004. <c>SUM</c>, <c>INFO.VIEW.TABLES</c>, and a user UDF are all call expressions;
/// built-in functions never get a node class of their own (A0 §6).
/// </summary>
public sealed class CallExpressionSyntax : ExpressionSyntax
{
    internal CallExpressionSyntax(GreenCallExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public NameSyntax Name => (NameSyntax)GetRequiredNode(0);
    public ArgumentListSyntax ArgumentList => (ArgumentListSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitCallExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitCallExpression(this);

    public CallExpressionSyntax Update(NameSyntax name, ArgumentListSyntax argumentList)
    {
        if (name == Name && argumentList == ArgumentList) return this;
        return new CallExpressionSyntax(
            new GreenCallExpression(RequiredGreen(name, nameof(name)), RequiredGreen(argumentList, nameof(argumentList)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public CallExpressionSyntax WithName(NameSyntax name) => Update(name, ArgumentList);
    public CallExpressionSyntax WithArgumentList(ArgumentListSyntax argumentList) => Update(Name, argumentList);
}

/// <summary>
/// A2 §7.3 kind 2005. <c>TRUE</c>, <c>FALSE</c>, and <c>BLANK</c> are NOT literals: A1 lexes them as
/// identifiers, so they reach the tree as names or calls (A2 §4.3).
/// </summary>
public sealed class LiteralExpressionSyntax : ExpressionSyntax
{
    internal LiteralExpressionSyntax(GreenLiteralExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken Token => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitLiteralExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitLiteralExpression(this);

    public LiteralExpressionSyntax Update(SyntaxToken token)
    {
        if (token == Token) return this;
        return new LiteralExpressionSyntax(new GreenLiteralExpression(RequiredGreen(token, nameof(token)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public LiteralExpressionSyntax WithToken(SyntaxToken token) => Update(token);
}

/// <summary>
/// A2 §7.3 kind 2400: one or more <c>VAR</c> declarations plus a <c>RETURN</c> clause. Declarations stay
/// in SOURCE order and are never reordered, merged, or flattened; scope resolution is the binder's job
/// (A2 §4.7). Shadowing is not a parse error.
/// </summary>
public sealed class VariableExpressionSyntax : ExpressionSyntax
{
    internal VariableExpressionSyntax(GreenVariableExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxList<VariableDeclarationSyntax> Declarations => GetList<VariableDeclarationSyntax>(0);
    public ReturnClauseSyntax Return => (ReturnClauseSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitVariableExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitVariableExpression(this);

    public VariableExpressionSyntax Update(SyntaxList<VariableDeclarationSyntax> declarations, ReturnClauseSyntax returnClause)
    {
        if (ReferenceEquals(declarations.Green, Declarations.Green) && returnClause == Return) return this;
        return new VariableExpressionSyntax(
            new GreenVariableExpression(declarations.Green, RequiredGreen(returnClause, nameof(returnClause)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public VariableExpressionSyntax WithDeclarations(SyntaxList<VariableDeclarationSyntax> declarations) => Update(declarations, Return);
    public VariableExpressionSyntax WithReturn(ReturnClauseSyntax returnClause) => Update(Declarations, returnClause);
}

/// <summary>A2 §7.3 kind 2500: <c>{ ... }</c>. The empty form <c>{}</c> is legal and undiagnosed (A2 §4.6).</summary>
public sealed class TableConstructorExpressionSyntax : ExpressionSyntax
{
    internal TableConstructorExpressionSyntax(GreenTableConstructorExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OpenBraceToken => GetToken(0);
    public SeparatedSyntaxList<ExpressionSyntax> Elements => GetSeparatedList<ExpressionSyntax>(1);
    public SyntaxToken CloseBraceToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitTableConstructorExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitTableConstructorExpression(this);

    public TableConstructorExpressionSyntax Update(SyntaxToken openBraceToken, SeparatedSyntaxList<ExpressionSyntax> elements, SyntaxToken closeBraceToken)
    {
        if (openBraceToken == OpenBraceToken && ReferenceEquals(elements.Green, Elements.Green) && closeBraceToken == CloseBraceToken) return this;
        return new TableConstructorExpressionSyntax(
            new GreenTableConstructorExpression(RequiredGreen(openBraceToken, nameof(openBraceToken)), elements.Green, RequiredGreen(closeBraceToken, nameof(closeBraceToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public TableConstructorExpressionSyntax WithOpenBraceToken(SyntaxToken openBraceToken) => Update(openBraceToken, Elements, CloseBraceToken);
    public TableConstructorExpressionSyntax WithElements(SeparatedSyntaxList<ExpressionSyntax> elements) => Update(OpenBraceToken, elements, CloseBraceToken);
    public TableConstructorExpressionSyntax WithCloseBraceToken(SyntaxToken closeBraceToken) => Update(OpenBraceToken, Elements, closeBraceToken);
}

/// <summary>
/// A2 §7.3 kind 2501: <c>( expr , expr ... )</c>. ARITY decides, in every grammar position, including the
/// left operand of <c>IN</c> (A2 §5.3). DAX has no comma operator, so the shape is unambiguous on its own
/// and consulting context could only lose information.
/// </summary>
public sealed class RowConstructorExpressionSyntax : ExpressionSyntax
{
    internal RowConstructorExpressionSyntax(GreenRowConstructorExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OpenParenToken => GetToken(0);
    public SeparatedSyntaxList<ExpressionSyntax> Fields => GetSeparatedList<ExpressionSyntax>(1);
    public SyntaxToken CloseParenToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitRowConstructorExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitRowConstructorExpression(this);

    public RowConstructorExpressionSyntax Update(SyntaxToken openParenToken, SeparatedSyntaxList<ExpressionSyntax> fields, SyntaxToken closeParenToken)
    {
        if (openParenToken == OpenParenToken && ReferenceEquals(fields.Green, Fields.Green) && closeParenToken == CloseParenToken) return this;
        return new RowConstructorExpressionSyntax(
            new GreenRowConstructorExpression(RequiredGreen(openParenToken, nameof(openParenToken)), fields.Green, RequiredGreen(closeParenToken, nameof(closeParenToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public RowConstructorExpressionSyntax WithOpenParenToken(SyntaxToken openParenToken) => Update(openParenToken, Fields, CloseParenToken);
    public RowConstructorExpressionSyntax WithFields(SeparatedSyntaxList<ExpressionSyntax> fields) => Update(OpenParenToken, fields, CloseParenToken);
    public RowConstructorExpressionSyntax WithCloseParenToken(SyntaxToken closeParenToken) => Update(OpenParenToken, Fields, closeParenToken);
}

/// <summary>
/// A2 §7.3 kind 2600. A lambda is a ROOT-ONLY construct: a <c>=&gt;</c> met inside an expression is
/// unexpected input, not an anonymous lambda (A2 §4.9).
/// </summary>
public sealed class LambdaExpressionSyntax : ExpressionSyntax
{
    internal LambdaExpressionSyntax(GreenLambdaExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public ParameterListSyntax ParameterList => (ParameterListSyntax)GetRequiredNode(0);
    public SyntaxToken ArrowToken => GetToken(1);
    public ExpressionSyntax Body => (ExpressionSyntax)GetRequiredNode(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitLambdaExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitLambdaExpression(this);

    public LambdaExpressionSyntax Update(ParameterListSyntax parameterList, SyntaxToken arrowToken, ExpressionSyntax body)
    {
        if (parameterList == ParameterList && arrowToken == ArrowToken && body == Body) return this;
        return new LambdaExpressionSyntax(
            new GreenLambdaExpression(RequiredGreen(parameterList, nameof(parameterList)), RequiredGreen(arrowToken, nameof(arrowToken)), RequiredGreen(body, nameof(body)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public LambdaExpressionSyntax WithParameterList(ParameterListSyntax parameterList) => Update(parameterList, ArrowToken, Body);
    public LambdaExpressionSyntax WithArrowToken(SyntaxToken arrowToken) => Update(ParameterList, arrowToken, Body);
    public LambdaExpressionSyntax WithBody(ExpressionSyntax body) => Update(ParameterList, ArrowToken, body);
}

/// <summary>
/// A2 §7.3 kind 4001. No children, zero width, and honestly a recovery node. It exists so the parser never
/// fabricates an <c>IdentifierName</c> over a missing token, which would assert "there is a name here" when
/// there is not (A2 §5.6).
/// </summary>
public sealed class MissingExpressionSyntax : ExpressionSyntax
{
    internal MissingExpressionSyntax(GreenMissingExpression green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public override bool IsRecoveryNode => true;

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitMissingExpression(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitMissingExpression(this);
}
