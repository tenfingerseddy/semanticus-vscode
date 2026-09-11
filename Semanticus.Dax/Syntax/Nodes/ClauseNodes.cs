using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// A2 §7.3 kind 2300. <c>F()</c> has ZERO elements and zero separators: no phantom omitted argument is
/// produced for an empty list (A2 §4.5).
/// </summary>
public sealed class ArgumentListSyntax : SyntaxNode
{
    internal ArgumentListSyntax(GreenArgumentList green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OpenParenToken => GetToken(0);
    public SeparatedSyntaxList<BaseArgumentSyntax> Arguments => GetSeparatedList<BaseArgumentSyntax>(1);
    public SyntaxToken CloseParenToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitArgumentList(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitArgumentList(this);

    public ArgumentListSyntax Update(SyntaxToken openParenToken, SeparatedSyntaxList<BaseArgumentSyntax> arguments, SyntaxToken closeParenToken)
    {
        if (openParenToken == OpenParenToken && ReferenceEquals(arguments.Green, Arguments.Green) && closeParenToken == CloseParenToken) return this;
        return new ArgumentListSyntax(
            new GreenArgumentList(RequiredGreen(openParenToken, nameof(openParenToken)), arguments.Green, RequiredGreen(closeParenToken, nameof(closeParenToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ArgumentListSyntax WithOpenParenToken(SyntaxToken openParenToken) => Update(openParenToken, Arguments, CloseParenToken);
    public ArgumentListSyntax WithArguments(SeparatedSyntaxList<BaseArgumentSyntax> arguments) => Update(OpenParenToken, arguments, CloseParenToken);
    public ArgumentListSyntax WithCloseParenToken(SyntaxToken closeParenToken) => Update(OpenParenToken, Arguments, closeParenToken);
}

/// <summary>A2 §7.3 kind 2301: one supplied argument.</summary>
public sealed class ArgumentSyntax : BaseArgumentSyntax
{
    internal ArgumentSyntax(GreenArgument green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public ExpressionSyntax Expression => (ExpressionSyntax)GetRequiredNode(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitArgument(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitArgument(this);

    public ArgumentSyntax Update(ExpressionSyntax expression)
    {
        if (expression == Expression) return this;
        return new ArgumentSyntax(new GreenArgument(RequiredGreen(expression, nameof(expression)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public ArgumentSyntax WithExpression(ExpressionSyntax expression) => Update(expression);
}

/// <summary>
/// A2 §7.3 kind 2302: the explicit hole in <c>F(1,,3)</c>. No children, zero width, and NOT an expression
/// (A2 §5.7 row 2) — it is only ever an argument-list element, between retained commas.
/// </summary>
public sealed class OmittedArgumentSyntax : BaseArgumentSyntax
{
    internal OmittedArgumentSyntax(GreenOmittedArgument green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitOmittedArgument(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitOmittedArgument(this);
}

/// <summary>
/// A2 §7.3 kind 2401. The <c>Identifier</c> slot holds an <c>IdentifierToken</c>, or a RETAINED reserved
/// keyword token plus <c>DAXP1084</c> — retained, not accepted, because refusing it would destroy the whole
/// declaration over a naming rule (A2 §6.2 rule 4).
/// </summary>
public sealed class VariableDeclarationSyntax : SyntaxNode
{
    internal VariableDeclarationSyntax(GreenVariableDeclaration green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken VarKeyword => GetToken(0);
    public SyntaxToken Identifier => GetToken(1);
    public SyntaxToken EqualsToken => GetToken(2);
    public ExpressionSyntax Initializer => (ExpressionSyntax)GetRequiredNode(3);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitVariableDeclaration(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitVariableDeclaration(this);

    public VariableDeclarationSyntax Update(SyntaxToken varKeyword, SyntaxToken identifier, SyntaxToken equalsToken, ExpressionSyntax initializer)
    {
        if (varKeyword == VarKeyword && identifier == Identifier && equalsToken == EqualsToken && initializer == Initializer) return this;
        return new VariableDeclarationSyntax(
            new GreenVariableDeclaration(
                RequiredGreen(varKeyword, nameof(varKeyword)),
                RequiredGreen(identifier, nameof(identifier)),
                RequiredGreen(equalsToken, nameof(equalsToken)),
                RequiredGreen(initializer, nameof(initializer)),
                Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public VariableDeclarationSyntax WithVarKeyword(SyntaxToken varKeyword) => Update(varKeyword, Identifier, EqualsToken, Initializer);
    public VariableDeclarationSyntax WithIdentifier(SyntaxToken identifier) => Update(VarKeyword, identifier, EqualsToken, Initializer);
    public VariableDeclarationSyntax WithEqualsToken(SyntaxToken equalsToken) => Update(VarKeyword, Identifier, equalsToken, Initializer);
    public VariableDeclarationSyntax WithInitializer(ExpressionSyntax initializer) => Update(VarKeyword, Identifier, EqualsToken, initializer);
}

/// <summary>A2 §7.3 kind 2402.</summary>
public sealed class ReturnClauseSyntax : SyntaxNode
{
    internal ReturnClauseSyntax(GreenReturnClause green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken ReturnKeyword => GetToken(0);
    public ExpressionSyntax Expression => (ExpressionSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitReturnClause(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitReturnClause(this);

    public ReturnClauseSyntax Update(SyntaxToken returnKeyword, ExpressionSyntax expression)
    {
        if (returnKeyword == ReturnKeyword && expression == Expression) return this;
        return new ReturnClauseSyntax(
            new GreenReturnClause(RequiredGreen(returnKeyword, nameof(returnKeyword)), RequiredGreen(expression, nameof(expression)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ReturnClauseSyntax WithReturnKeyword(SyntaxToken returnKeyword) => Update(returnKeyword, Expression);
    public ReturnClauseSyntax WithExpression(ExpressionSyntax expression) => Update(ReturnKeyword, expression);
}

/// <summary>A2 §7.3 kind 2601. <c>()</c> is a valid empty parameter list; real model UDFs use it.</summary>
public sealed class ParameterListSyntax : SyntaxNode
{
    internal ParameterListSyntax(GreenParameterList green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken OpenParenToken => GetToken(0);
    public SeparatedSyntaxList<ParameterSyntax> Parameters => GetSeparatedList<ParameterSyntax>(1);
    public SyntaxToken CloseParenToken => GetToken(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitParameterList(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitParameterList(this);

    public ParameterListSyntax Update(SyntaxToken openParenToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenToken)
    {
        if (openParenToken == OpenParenToken && ReferenceEquals(parameters.Green, Parameters.Green) && closeParenToken == CloseParenToken) return this;
        return new ParameterListSyntax(
            new GreenParameterList(RequiredGreen(openParenToken, nameof(openParenToken)), parameters.Green, RequiredGreen(closeParenToken, nameof(closeParenToken)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ParameterListSyntax WithOpenParenToken(SyntaxToken openParenToken) => Update(openParenToken, Parameters, CloseParenToken);
    public ParameterListSyntax WithParameters(SeparatedSyntaxList<ParameterSyntax> parameters) => Update(OpenParenToken, parameters, CloseParenToken);
    public ParameterListSyntax WithCloseParenToken(SyntaxToken closeParenToken) => Update(OpenParenToken, Parameters, closeParenToken);
}

/// <summary>
/// A2 §7.3 kind 2602. <c>TypeClause</c> and <c>Default</c> are OPTIONAL NODE children: absent means a null
/// green slot, the accessor returns null, and nothing is enumerated (A2 §5.6 row 1). The identifier may be
/// a retained reserved keyword plus <c>DAXP1069</c> (A2 §6.2 rule 4).
/// </summary>
public sealed class ParameterSyntax : SyntaxNode
{
    internal ParameterSyntax(GreenParameter green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken Identifier => GetToken(0);
    public ParameterTypeClauseSyntax? TypeClause => (ParameterTypeClauseSyntax?)GetNode(1);
    public DefaultValueClauseSyntax? Default => (DefaultValueClauseSyntax?)GetNode(2);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitParameter(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitParameter(this);

    public ParameterSyntax Update(SyntaxToken identifier, ParameterTypeClauseSyntax? typeClause, DefaultValueClauseSyntax? defaultValue)
    {
        if (identifier == Identifier && typeClause == TypeClause && defaultValue == Default) return this;
        return new ParameterSyntax(
            new GreenParameter(RequiredGreen(identifier, nameof(identifier)), typeClause?.Green, defaultValue?.Green, Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ParameterSyntax WithIdentifier(SyntaxToken identifier) => Update(identifier, TypeClause, Default);
    public ParameterSyntax WithTypeClause(ParameterTypeClauseSyntax? typeClause) => Update(Identifier, typeClause, Default);
    public ParameterSyntax WithDefault(DefaultValueClauseSyntax? defaultValue) => Update(Identifier, TypeClause, defaultValue);
}

/// <summary>
/// A2 §7.3 kind 2603. ONE source-ordered list, never three fixed slots: hints are classified by closed
/// vocabulary, not by position, so <c>EXPR ANYREF</c> must stay in source order (reordering would break
/// round-trip) and <c>SCALAR SCALAR</c> must be able to hold two type hints (A2 §4.9.1).
/// </summary>
public sealed class ParameterTypeClauseSyntax : SyntaxNode
{
    internal ParameterTypeClauseSyntax(GreenParameterTypeClause green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken ColonToken => GetToken(0);

    /// <summary>The only stored children, in source order. A consumer that needs every hint reads this.</summary>
    public SyntaxList<ParameterHintSyntax> Hints => GetList<ParameterHintSyntax>(1);

    /// <summary>Derived: the FIRST type hint in <see cref="Hints"/>, or null. Convenience, never a second source of truth.</summary>
    public TypeHintSyntax? TypeHint => FirstOfKind<TypeHintSyntax>();

    /// <summary>Derived: the FIRST subtype hint in <see cref="Hints"/>, or null.</summary>
    public SubtypeHintSyntax? SubtypeHint => FirstOfKind<SubtypeHintSyntax>();

    /// <summary>Derived: the FIRST passing-mode hint in <see cref="Hints"/>, or null.</summary>
    public PassingModeHintSyntax? PassingModeHint => FirstOfKind<PassingModeHintSyntax>();

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitParameterTypeClause(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitParameterTypeClause(this);

    public ParameterTypeClauseSyntax Update(SyntaxToken colonToken, SyntaxList<ParameterHintSyntax> hints)
    {
        if (colonToken == ColonToken && ReferenceEquals(hints.Green, Hints.Green)) return this;
        return new ParameterTypeClauseSyntax(
            new GreenParameterTypeClause(RequiredGreen(colonToken, nameof(colonToken)), hints.Green, Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public ParameterTypeClauseSyntax WithColonToken(SyntaxToken colonToken) => Update(colonToken, Hints);
    public ParameterTypeClauseSyntax WithHints(SyntaxList<ParameterHintSyntax> hints) => Update(ColonToken, hints);

    private THint? FirstOfKind<THint>() where THint : ParameterHintSyntax
    {
        foreach (var hint in Hints)
            if (hint is THint match)
                return match;
        return null;
    }
}

/// <summary>A2 §7.3 kind 2604: <c>ANYVAL</c>, <c>SCALAR</c>, <c>TABLE</c>, <c>ANYREF</c>, <c>CALENDARREF</c>, <c>COLUMNREF</c>, <c>MEASUREREF</c>, <c>TABLEREF</c>.</summary>
public sealed class TypeHintSyntax : ParameterHintSyntax
{
    internal TypeHintSyntax(GreenTypeHint green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public override SyntaxToken Token => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitTypeHint(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitTypeHint(this);

    public TypeHintSyntax Update(SyntaxToken token)
    {
        if (token == Token) return this;
        return new TypeHintSyntax(new GreenTypeHint(RequiredGreen(token, nameof(token)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public TypeHintSyntax WithToken(SyntaxToken token) => Update(token);
}

/// <summary>A2 §7.3 kind 2605: <c>BOOLEAN</c>, <c>DATETIME</c>, <c>DECIMAL</c>, <c>DOUBLE</c>, <c>INT64</c>, <c>NUMERIC</c>, <c>STRING</c>, <c>VARIANT</c>.</summary>
public sealed class SubtypeHintSyntax : ParameterHintSyntax
{
    internal SubtypeHintSyntax(GreenSubtypeHint green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public override SyntaxToken Token => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitSubtypeHint(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitSubtypeHint(this);

    public SubtypeHintSyntax Update(SyntaxToken token)
    {
        if (token == Token) return this;
        return new SubtypeHintSyntax(new GreenSubtypeHint(RequiredGreen(token, nameof(token)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public SubtypeHintSyntax WithToken(SyntaxToken token) => Update(token);
}

/// <summary>A2 §7.3 kind 2606: <c>VAL</c> or <c>EXPR</c>.</summary>
public sealed class PassingModeHintSyntax : ParameterHintSyntax
{
    internal PassingModeHintSyntax(GreenPassingModeHint green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public override SyntaxToken Token => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitPassingModeHint(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitPassingModeHint(this);

    public PassingModeHintSyntax Update(SyntaxToken token)
    {
        if (token == Token) return this;
        return new PassingModeHintSyntax(new GreenPassingModeHint(RequiredGreen(token, nameof(token)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public PassingModeHintSyntax WithToken(SyntaxToken token) => Update(token);
}

/// <summary>A2 §7.3 kind 2607. Inside a parameter, <c>=</c> can only introduce a default (A2 §4.9).</summary>
public sealed class DefaultValueClauseSyntax : SyntaxNode
{
    internal DefaultValueClauseSyntax(GreenDefaultValueClause green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken EqualsToken => GetToken(0);
    public ExpressionSyntax Expression => (ExpressionSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitDefaultValueClause(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitDefaultValueClause(this);

    public DefaultValueClauseSyntax Update(SyntaxToken equalsToken, ExpressionSyntax expression)
    {
        if (equalsToken == EqualsToken && expression == Expression) return this;
        return new DefaultValueClauseSyntax(
            new GreenDefaultValueClause(RequiredGreen(equalsToken, nameof(equalsToken)), RequiredGreen(expression, nameof(expression)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public DefaultValueClauseSyntax WithEqualsToken(SyntaxToken equalsToken) => Update(equalsToken, Expression);
    public DefaultValueClauseSyntax WithExpression(ExpressionSyntax expression) => Update(EqualsToken, expression);
}

/// <summary>
/// A2 §7.3 kind 4000: the structured node behind <c>SkippedTokensTrivia</c>. It holds the original tokens,
/// with their own trivia, in source order and with their raw spans untouched, so unexpected source is
/// preserved rather than discarded and round-trip survives recovery (A0 §11.3).
/// </summary>
public sealed class SkippedTokensSyntax : SyntaxNode
{
    internal SkippedTokensSyntax(GreenSkippedTokens green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxTokenList Tokens => GetTokenList(0);

    public override bool IsRecoveryNode => true;

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitSkippedTokens(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitSkippedTokens(this);

    public SkippedTokensSyntax Update(SyntaxTokenList tokens)
    {
        if (ReferenceEquals(tokens.Green, Tokens.Green)) return this;
        return new SkippedTokensSyntax(new GreenSkippedTokens(tokens.Green, Green.Diagnostics), null, 0, SyntaxTree);
    }

    public SkippedTokensSyntax WithTokens(SyntaxTokenList tokens) => Update(tokens);
}
