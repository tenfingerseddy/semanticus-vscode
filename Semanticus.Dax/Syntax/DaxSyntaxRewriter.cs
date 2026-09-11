namespace Semanticus.Dax.Syntax;

/// <summary>
/// A rewriter (A0 §8.3). Default behavior returns the ORIGINAL node when no child changed, so an
/// untouched subtree keeps its identity, its trivia, its missing tokens, and its diagnostics. Trivia is
/// never normalized: formatting is a separate opt-in service (A0 §5.5, §12.2 A3 item 5).
///
/// <para>
/// A rewritten node is DETACHED (no parent, position 0) until it is placed back into a tree, which is
/// exactly how each <c>Update</c> behaves.
/// </para>
/// </summary>
public abstract class DaxSyntaxRewriter : DaxSyntaxVisitor<SyntaxNode?>
{
    private readonly bool _visitIntoStructuredTrivia;

    protected DaxSyntaxRewriter(bool visitIntoStructuredTrivia = false) => _visitIntoStructuredTrivia = visitIntoStructuredTrivia;

    protected bool VisitIntoStructuredTrivia => _visitIntoStructuredTrivia;

    /// <summary>Tokens are returned unchanged by default, which is what preserves trivia.</summary>
    public virtual SyntaxToken VisitToken(SyntaxToken token) => token;

    public virtual SyntaxTrivia VisitTrivia(SyntaxTrivia trivia) => trivia;

    /// <summary>
    /// Rewrites every element. A rewritten element that comes back null is DROPPED, which is how a
    /// rewriter deletes a list member.
    /// </summary>
    public virtual SyntaxList<TNode> VisitList<TNode>(SyntaxList<TNode> list) where TNode : SyntaxNode
    {
        List<TNode>? rewritten = null;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var visited = Visit(item) as TNode;
            if (rewritten is null && (visited is null || !ReferenceEquals(visited.Green, item.Green)))
            {
                rewritten = new List<TNode>(list.Count);
                for (var j = 0; j < i; j++) rewritten.Add(list[j]);
            }
            if (rewritten is not null && visited is not null) rewritten.Add(visited);
        }
        return rewritten is null ? list : SyntaxList<TNode>.Create(rewritten);
    }

    /// <summary>
    /// Rewrites elements and separators. Dropping an element also drops the separator that followed it, so
    /// the alternating layout stays coherent; every remaining separator is retained (A0 §7.5).
    /// </summary>
    public virtual SeparatedSyntaxList<TNode> VisitList<TNode>(SeparatedSyntaxList<TNode> list) where TNode : SyntaxNode
    {
        var changed = false;
        var items = new List<TNode>(list.Count);
        var separators = new List<SyntaxToken>(list.SeparatorCount);

        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var visited = Visit(item) as TNode;
            if (visited is null) { changed = true; continue; }
            if (!ReferenceEquals(visited.Green, item.Green)) changed = true;
            items.Add(visited);

            if (i >= list.SeparatorCount) continue;
            var separator = list.GetSeparator(i);
            var visitedSeparator = VisitToken(separator);
            if (visitedSeparator != separator) changed = true;
            separators.Add(visitedSeparator);
        }

        // A dropped element can leave one separator too many; trim from the tail so the list stays legal.
        while (separators.Count > 0 && separators.Count >= items.Count && items.Count < list.Count) separators.RemoveAt(separators.Count - 1);

        return changed ? SeparatedSyntaxList<TNode>.Create(items, separators) : list;
    }

    public virtual SyntaxTokenList VisitList(SyntaxTokenList list)
    {
        var changed = false;
        var tokens = new List<SyntaxToken>(list.Count);
        foreach (var token in list)
        {
            var visited = VisitToken(token);
            if (visited != token) changed = true;
            tokens.Add(visited);
        }
        return changed ? SyntaxTokenList.Create(tokens) : list;
    }

    // ---- Roots --------------------------------------------------------------------------------

    public override SyntaxNode? VisitExpressionCompilationUnit(ExpressionCompilationUnitSyntax node)
        => node.Update(VisitToken(node.LeadingEqualsToken), (ExpressionSyntax)Visit(node.Expression)!, VisitToken(node.EndOfFileToken));

    public override SyntaxNode? VisitUdfBodyCompilationUnit(UdfBodyCompilationUnitSyntax node)
        => node.Update((LambdaExpressionSyntax)Visit(node.Lambda)!, VisitToken(node.EndOfFileToken));

    // ---- Expressions --------------------------------------------------------------------------

    public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        => node.Update(VisitToken(node.OpenParenToken), (ExpressionSyntax)Visit(node.Expression)!, VisitToken(node.CloseParenToken));

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        => node.Update(VisitToken(node.OperatorToken), (ExpressionSyntax)Visit(node.Operand)!);

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        => node.Update((ExpressionSyntax)Visit(node.Left)!, VisitToken(node.OperatorToken), (ExpressionSyntax)Visit(node.Right)!);

    public override SyntaxNode? VisitMembershipExpression(MembershipExpressionSyntax node)
        => node.Update((ExpressionSyntax)Visit(node.Left)!, VisitToken(node.InKeyword), (ExpressionSyntax)Visit(node.Right)!);

    public override SyntaxNode? VisitCallExpression(CallExpressionSyntax node)
        => node.Update((NameSyntax)Visit(node.Name)!, (ArgumentListSyntax)Visit(node.ArgumentList)!);

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
        => node.Update(VisitToken(node.Token));

    public override SyntaxNode? VisitVariableExpression(VariableExpressionSyntax node)
        => node.Update(VisitList(node.Declarations), (ReturnClauseSyntax)Visit(node.Return)!);

    public override SyntaxNode? VisitTableConstructorExpression(TableConstructorExpressionSyntax node)
        => node.Update(VisitToken(node.OpenBraceToken), VisitList(node.Elements), VisitToken(node.CloseBraceToken));

    public override SyntaxNode? VisitRowConstructorExpression(RowConstructorExpressionSyntax node)
        => node.Update(VisitToken(node.OpenParenToken), VisitList(node.Fields), VisitToken(node.CloseParenToken));

    public override SyntaxNode? VisitLambdaExpression(LambdaExpressionSyntax node)
        => node.Update((ParameterListSyntax)Visit(node.ParameterList)!, VisitToken(node.ArrowToken), (ExpressionSyntax)Visit(node.Body)!);

    public override SyntaxNode? VisitMissingExpression(MissingExpressionSyntax node) => node;

    // ---- Names --------------------------------------------------------------------------------

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => node.Update(VisitToken(node.Identifier));

    public override SyntaxNode? VisitQuotedName(QuotedNameSyntax node) => node.Update(VisitToken(node.Identifier));

    public override SyntaxNode? VisitBracketedName(BracketedNameSyntax node) => node.Update(VisitToken(node.Identifier));

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        => node.Update((NameSyntax)Visit(node.Qualifier)!, (BracketedNameSyntax)Visit(node.Name)!);

    public override SyntaxNode? VisitDottedName(DottedNameSyntax node) => node.Update(VisitList(node.Segments));

    // ---- Arguments ----------------------------------------------------------------------------

    public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node)
        => node.Update(VisitToken(node.OpenParenToken), VisitList(node.Arguments), VisitToken(node.CloseParenToken));

    public override SyntaxNode? VisitArgument(ArgumentSyntax node) => node.Update((ExpressionSyntax)Visit(node.Expression)!);

    public override SyntaxNode? VisitOmittedArgument(OmittedArgumentSyntax node) => node;

    // ---- Variables ----------------------------------------------------------------------------

    public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
        => node.Update(VisitToken(node.VarKeyword), VisitToken(node.Identifier), VisitToken(node.EqualsToken), (ExpressionSyntax)Visit(node.Initializer)!);

    public override SyntaxNode? VisitReturnClause(ReturnClauseSyntax node)
        => node.Update(VisitToken(node.ReturnKeyword), (ExpressionSyntax)Visit(node.Expression)!);

    // ---- UDF ----------------------------------------------------------------------------------

    public override SyntaxNode? VisitParameterList(ParameterListSyntax node)
        => node.Update(VisitToken(node.OpenParenToken), VisitList(node.Parameters), VisitToken(node.CloseParenToken));

    public override SyntaxNode? VisitParameter(ParameterSyntax node)
        => node.Update(VisitToken(node.Identifier), (ParameterTypeClauseSyntax?)Visit(node.TypeClause), (DefaultValueClauseSyntax?)Visit(node.Default));

    public override SyntaxNode? VisitParameterTypeClause(ParameterTypeClauseSyntax node)
        => node.Update(VisitToken(node.ColonToken), VisitList(node.Hints));

    public override SyntaxNode? VisitTypeHint(TypeHintSyntax node) => node.Update(VisitToken(node.Token));

    public override SyntaxNode? VisitSubtypeHint(SubtypeHintSyntax node) => node.Update(VisitToken(node.Token));

    public override SyntaxNode? VisitPassingModeHint(PassingModeHintSyntax node) => node.Update(VisitToken(node.Token));

    public override SyntaxNode? VisitDefaultValueClause(DefaultValueClauseSyntax node)
        => node.Update(VisitToken(node.EqualsToken), (ExpressionSyntax)Visit(node.Expression)!);

    // ---- Recovery -----------------------------------------------------------------------------

    public override SyntaxNode? VisitSkippedTokens(SkippedTokensSyntax node)
        => _visitIntoStructuredTrivia ? node.Update(VisitList(node.Tokens)) : node;
}
