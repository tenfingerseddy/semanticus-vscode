using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// A2 §7.3 kind 2200. The identifier is an <c>IdentifierToken</c> and never a keyword token: a reserved
/// keyword is never a clean bare name (A2 §6.2 rule 1).
/// </summary>
public sealed class IdentifierNameSyntax : NameSyntax
{
    internal IdentifierNameSyntax(GreenIdentifierName green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken Identifier => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitIdentifierName(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitIdentifierName(this);

    public IdentifierNameSyntax Update(SyntaxToken identifier)
    {
        if (identifier == Identifier) return this;
        return new IdentifierNameSyntax(new GreenIdentifierName(RequiredGreen(identifier, nameof(identifier)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public IdentifierNameSyntax WithIdentifier(SyntaxToken identifier) => Update(identifier);
}

/// <summary>
/// A2 §7.3 kind 2201. A bare quoted name is a name expression in its own right: <c>'Fiscal'</c> with no
/// following bracketed name is a standalone quoted name, not an error and not a fragment (A2 §4.4).
/// Whether it denotes a table or a calendar is a binder decision.
/// </summary>
public sealed class QuotedNameSyntax : NameSyntax
{
    internal QuotedNameSyntax(GreenQuotedName green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken Identifier => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitQuotedName(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitQuotedName(this);

    public QuotedNameSyntax Update(SyntaxToken identifier)
    {
        if (identifier == Identifier) return this;
        return new QuotedNameSyntax(new GreenQuotedName(RequiredGreen(identifier, nameof(identifier)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public QuotedNameSyntax WithIdentifier(SyntaxToken identifier) => Update(identifier);
}

/// <summary>A2 §7.3 kind 2202: <c>[Amount]</c>, usable directly as an expression.</summary>
public sealed class BracketedNameSyntax : NameSyntax
{
    internal BracketedNameSyntax(GreenBracketedName green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SyntaxToken Identifier => GetToken(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitBracketedName(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitBracketedName(this);

    public BracketedNameSyntax Update(SyntaxToken identifier)
    {
        if (identifier == Identifier) return this;
        return new BracketedNameSyntax(new GreenBracketedName(RequiredGreen(identifier, nameof(identifier)), Green.Diagnostics), null, 0, SyntaxTree);
    }

    public BracketedNameSyntax WithIdentifier(SyntaxToken identifier) => Update(identifier);
}

/// <summary>
/// A2 §7.3 kind 2203: <c>Table[Column]</c>. Qualification binds greedily and is TOKEN adjacency, not
/// character adjacency, so trivia between the qualifier and the bracketed name does not break it
/// (A2 §5.4).
/// </summary>
public sealed class QualifiedNameSyntax : NameSyntax
{
    internal QualifiedNameSyntax(GreenQualifiedName green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    /// <summary>An <see cref="IdentifierNameSyntax"/>, <see cref="QuotedNameSyntax"/>, or <see cref="DottedNameSyntax"/> (the last with <c>DAXP1042</c>).</summary>
    public NameSyntax Qualifier => (NameSyntax)GetRequiredNode(0);

    public BracketedNameSyntax Name => (BracketedNameSyntax)GetRequiredNode(1);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitQualifiedName(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitQualifiedName(this);

    public QualifiedNameSyntax Update(NameSyntax qualifier, BracketedNameSyntax name)
    {
        if (qualifier == Qualifier && name == Name) return this;
        return new QualifiedNameSyntax(
            new GreenQualifiedName(RequiredGreen(qualifier, nameof(qualifier)), RequiredGreen(name, nameof(name)), Green.Diagnostics),
            null, 0, SyntaxTree);
    }

    public QualifiedNameSyntax WithQualifier(NameSyntax qualifier) => Update(qualifier, Name);
    public QualifiedNameSyntax WithName(BracketedNameSyntax name) => Update(Qualifier, name);
}

/// <summary>
/// A2 §7.3 kind 2204: <c>INFO.VIEW.MEASURES</c>. Segments are a separated list with <c>DotToken</c>
/// separators; every dot is retained (A1 §3.4) and an empty segment becomes a zero-width missing
/// identifier with <c>DAXP1041</c> rather than a silently dropped dot.
/// </summary>
public sealed class DottedNameSyntax : NameSyntax
{
    internal DottedNameSyntax(GreenDottedName green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public SeparatedSyntaxList<IdentifierNameSyntax> Segments => GetSeparatedList<IdentifierNameSyntax>(0);

    public override void Accept(DaxSyntaxVisitor visitor) => visitor.VisitDottedName(this);
    public override TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor) where TResult : default => visitor.VisitDottedName(this);

    public DottedNameSyntax Update(SeparatedSyntaxList<IdentifierNameSyntax> segments)
    {
        if (ReferenceEquals(segments.Green, Segments.Green)) return this;
        return new DottedNameSyntax(new GreenDottedName(segments.Green, Green.Diagnostics), null, 0, SyntaxTree);
    }

    public DottedNameSyntax WithSegments(SeparatedSyntaxList<IdentifierNameSyntax> segments) => Update(segments);
}
