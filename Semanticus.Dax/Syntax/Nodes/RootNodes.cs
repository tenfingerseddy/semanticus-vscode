using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// A2 §7.3 kind 3000, the root for all nine expression source kinds (A2 §3.1). Their parses are
/// structurally identical; the source kind selects binder context only.
/// </summary>
public sealed class ExpressionCompilationUnitSyntax : CompilationUnitSyntax
{
    internal ExpressionCompilationUnitSyntax(GreenExpressionCompilationUnit green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    /// <summary>
    /// The optional leading <c>=</c>. When the source has none the green slot is NULL and this returns a
    /// DEFAULT token whose kind is <see cref="SyntaxKind.None"/> — not a tree token: no position, no width,
    /// <c>IsMissing == false</c>, never enumerated, and it does not set <c>ContainsRecovery</c>
    /// (A2 §5.2 pin 1, §5.6 row 2). Absence is not recovery.
    ///
    /// <para>
    /// When the first token IS an <c>=</c> it is always consumed into this slot whatever
    /// <c>AllowLeadingEquals</c> says; that option only decides whether <c>DAXP1020</c> is emitted
    /// (A2 §5.2 pins 2-3), so the tree shape never depends on a parse option.
    /// </para>
    /// </summary>
    public SyntaxToken LeadingEqualsToken => GetToken(0);

    public ExpressionSyntax Expression => (ExpressionSyntax)GetRequiredNode(1);

    public override SyntaxToken EndOfFileToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitExpressionCompilationUnit(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitExpressionCompilationUnit(this);

    /// <summary>Pass a default <see cref="SyntaxToken"/> for <paramref name="leadingEqualsToken"/> to leave the slot absent.</summary>
    public ExpressionCompilationUnitSyntax Update(SyntaxToken leadingEqualsToken, ExpressionSyntax expression, SyntaxToken endOfFileToken)
    {
        if (leadingEqualsToken == LeadingEqualsToken && expression == Expression && endOfFileToken == EndOfFileToken) return this;
        return new ExpressionCompilationUnitSyntax(
            new GreenExpressionCompilationUnit(leadingEqualsToken.Green, RequiredGreen(expression, nameof(expression)), RequiredGreen(endOfFileToken, nameof(endOfFileToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ExpressionCompilationUnitSyntax WithLeadingEqualsToken(SyntaxToken leadingEqualsToken) => Update(leadingEqualsToken, Expression, EndOfFileToken);
    public ExpressionCompilationUnitSyntax WithExpression(ExpressionSyntax expression) => Update(LeadingEqualsToken, expression, EndOfFileToken);
    public ExpressionCompilationUnitSyntax WithEndOfFileToken(SyntaxToken endOfFileToken) => Update(LeadingEqualsToken, Expression, endOfFileToken);
}

/// <summary>A2 §7.3 kind 3001: the model-UDF lambda-body root (A2 §4.9).</summary>
public sealed class UdfBodyCompilationUnitSyntax : CompilationUnitSyntax
{
    internal UdfBodyCompilationUnitSyntax(GreenUdfBodyCompilationUnit green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public LambdaExpressionSyntax Lambda => (LambdaExpressionSyntax)GetRequiredNode(0);

    public override SyntaxToken EndOfFileToken => GetToken(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitUdfBodyCompilationUnit(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitUdfBodyCompilationUnit(this);

    public UdfBodyCompilationUnitSyntax Update(LambdaExpressionSyntax lambda, SyntaxToken endOfFileToken)
    {
        if (lambda == Lambda && endOfFileToken == EndOfFileToken) return this;
        return new UdfBodyCompilationUnitSyntax(
            new GreenUdfBodyCompilationUnit(RequiredGreen(lambda, nameof(lambda)), RequiredGreen(endOfFileToken, nameof(endOfFileToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public UdfBodyCompilationUnitSyntax WithLambda(LambdaExpressionSyntax lambda) => Update(lambda, EndOfFileToken);
    public UdfBodyCompilationUnitSyntax WithEndOfFileToken(SyntaxToken endOfFileToken) => Update(Lambda, endOfFileToken);
}
