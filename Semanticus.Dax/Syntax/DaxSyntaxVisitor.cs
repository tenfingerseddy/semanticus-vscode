namespace Semanticus.Dax.Syntax;

/// <summary>
/// A void visitor (A0 §8.1). Every method is VIRTUAL and defaults to descending, never throwing: a
/// consumer must tolerate node kinds it has never heard of, because adding a kind is a minor release
/// (A0 §12.3, A2 §7.4). A visitor that throws on an unknown kind would turn a language addition into a
/// crash in every downstream rule.
/// </summary>
public abstract class DaxSyntaxVisitor
{
    public virtual void Visit(SyntaxNode? node) => node?.Accept(this);

    /// <summary>Descends into child nodes. Overriding this changes the default for every kind at once.</summary>
    protected virtual void DefaultVisit(SyntaxNode node)
    {
        foreach (var child in node.ChildNodes()) Visit(child);
    }

    // Roots
    public virtual void VisitExpressionCompilationUnit(ExpressionCompilationUnitSyntax node) => DefaultVisit(node);
    public virtual void VisitUdfBodyCompilationUnit(UdfBodyCompilationUnitSyntax node) => DefaultVisit(node);

    // Expressions
    public virtual void VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitBinaryExpression(BinaryExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitMembershipExpression(MembershipExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitCallExpression(CallExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitLiteralExpression(LiteralExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitVariableExpression(VariableExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitTableConstructorExpression(TableConstructorExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitRowConstructorExpression(RowConstructorExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitLambdaExpression(LambdaExpressionSyntax node) => DefaultVisit(node);
    public virtual void VisitMissingExpression(MissingExpressionSyntax node) => DefaultVisit(node);

    // Names
    public virtual void VisitIdentifierName(IdentifierNameSyntax node) => DefaultVisit(node);
    public virtual void VisitQuotedName(QuotedNameSyntax node) => DefaultVisit(node);
    public virtual void VisitBracketedName(BracketedNameSyntax node) => DefaultVisit(node);
    public virtual void VisitQualifiedName(QualifiedNameSyntax node) => DefaultVisit(node);
    public virtual void VisitDottedName(DottedNameSyntax node) => DefaultVisit(node);

    // Arguments
    public virtual void VisitArgumentList(ArgumentListSyntax node) => DefaultVisit(node);
    public virtual void VisitArgument(ArgumentSyntax node) => DefaultVisit(node);
    public virtual void VisitOmittedArgument(OmittedArgumentSyntax node) => DefaultVisit(node);

    // Variables
    public virtual void VisitVariableDeclaration(VariableDeclarationSyntax node) => DefaultVisit(node);
    public virtual void VisitReturnClause(ReturnClauseSyntax node) => DefaultVisit(node);

    // UDF
    public virtual void VisitParameterList(ParameterListSyntax node) => DefaultVisit(node);
    public virtual void VisitParameter(ParameterSyntax node) => DefaultVisit(node);
    public virtual void VisitParameterTypeClause(ParameterTypeClauseSyntax node) => DefaultVisit(node);
    public virtual void VisitTypeHint(TypeHintSyntax node) => DefaultVisit(node);
    public virtual void VisitSubtypeHint(SubtypeHintSyntax node) => DefaultVisit(node);
    public virtual void VisitPassingModeHint(PassingModeHintSyntax node) => DefaultVisit(node);
    public virtual void VisitDefaultValueClause(DefaultValueClauseSyntax node) => DefaultVisit(node);

    // Recovery
    public virtual void VisitSkippedTokens(SkippedTokensSyntax node) => DefaultVisit(node);
}

/// <summary>A visitor that produces a value (A0 §8.1). The default returns <c>default</c>; it never throws.</summary>
public abstract class DaxSyntaxVisitor<TResult>
{
    public virtual TResult? Visit(SyntaxNode? node) => node is null ? default : node.Accept(this);

    protected virtual TResult? DefaultVisit(SyntaxNode node) => default;

    // Roots
    public virtual TResult? VisitExpressionCompilationUnit(ExpressionCompilationUnitSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitUdfBodyCompilationUnit(UdfBodyCompilationUnitSyntax node) => DefaultVisit(node);

    // Expressions
    public virtual TResult? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitBinaryExpression(BinaryExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitMembershipExpression(MembershipExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitCallExpression(CallExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitLiteralExpression(LiteralExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitVariableExpression(VariableExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitTableConstructorExpression(TableConstructorExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitRowConstructorExpression(RowConstructorExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitLambdaExpression(LambdaExpressionSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitMissingExpression(MissingExpressionSyntax node) => DefaultVisit(node);

    // Names
    public virtual TResult? VisitIdentifierName(IdentifierNameSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitQuotedName(QuotedNameSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitBracketedName(BracketedNameSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitQualifiedName(QualifiedNameSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitDottedName(DottedNameSyntax node) => DefaultVisit(node);

    // Arguments
    public virtual TResult? VisitArgumentList(ArgumentListSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitArgument(ArgumentSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitOmittedArgument(OmittedArgumentSyntax node) => DefaultVisit(node);

    // Variables
    public virtual TResult? VisitVariableDeclaration(VariableDeclarationSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitReturnClause(ReturnClauseSyntax node) => DefaultVisit(node);

    // UDF
    public virtual TResult? VisitParameterList(ParameterListSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitParameter(ParameterSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitParameterTypeClause(ParameterTypeClauseSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitTypeHint(TypeHintSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitSubtypeHint(SubtypeHintSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitPassingModeHint(PassingModeHintSyntax node) => DefaultVisit(node);
    public virtual TResult? VisitDefaultValueClause(DefaultValueClauseSyntax node) => DefaultVisit(node);

    // Recovery
    public virtual TResult? VisitSkippedTokens(SkippedTokensSyntax node) => DefaultVisit(node);
}

/// <summary>How deep a walker descends (A0 §8.2).</summary>
public enum SyntaxWalkerDepth
{
    Nodes,
    Tokens,
    Trivia,

    /// <summary>Also descends into skipped-token trivia. Dependency extraction must NOT use this depth: skipped source is not executable DAX.</summary>
    StructuredTrivia
}

/// <summary>
/// A walker with an explicit depth (A0 §8.2). Depth is a constructor argument, not a default, so a
/// dependency extractor cannot accidentally walk comments or skipped source as if it were live code.
/// </summary>
public abstract class DaxSyntaxWalker : DaxSyntaxVisitor
{
    private readonly SyntaxWalkerDepth _depth;

    protected DaxSyntaxWalker(SyntaxWalkerDepth depth = SyntaxWalkerDepth.Nodes) => _depth = depth;

    protected SyntaxWalkerDepth Depth => _depth;

    protected override void DefaultVisit(SyntaxNode node)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.AsNode() is { } childNode) Visit(childNode);
            else if (_depth >= SyntaxWalkerDepth.Tokens) VisitToken(child.AsToken());
        }
    }

    public virtual void VisitToken(SyntaxToken token)
    {
        if (_depth < SyntaxWalkerDepth.Trivia) return;
        foreach (var trivia in token.LeadingTrivia) VisitTrivia(trivia);
        foreach (var trivia in token.TrailingTrivia) VisitTrivia(trivia);
    }

    public virtual void VisitTrivia(SyntaxTrivia trivia)
    {
        if (_depth < SyntaxWalkerDepth.StructuredTrivia) return;
        if (trivia.GetStructure() is { } structure) Visit(structure);
    }
}
