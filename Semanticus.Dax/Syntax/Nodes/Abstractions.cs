using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax;

// The base-class hierarchy is A2 §7.4, exactly. It is structural, not semantic: a column reference and a
// measure reference are the same node shape, and the binder supplies symbol identity (A0 §6).

/// <summary>
/// A parse root (A2 §3.2). Every root family carries the EOF token, so the trailing trivia of any source
/// always has an owner and round-trip holds for trivia-only input.
/// </summary>
public abstract class CompilationUnitSyntax : SyntaxNode
{
    private protected CompilationUnitSyntax(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    public abstract SyntaxToken EndOfFileToken { get; }
}

/// <summary>Anything that can appear where the grammar admits an expression.</summary>
public abstract class ExpressionSyntax : SyntaxNode
{
    private protected ExpressionSyntax(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }
}

/// <summary>
/// A name IS an expression (A2 §4.4): there is no name-expression wrapper node, so a bare
/// <c>[Measure]</c> is a <see cref="BracketedNameSyntax"/> used directly as an expression.
/// </summary>
public abstract class NameSyntax : ExpressionSyntax
{
    private protected NameSyntax(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }
}

/// <summary>
/// An element of an argument list. NOT an expression (A2 §5.7 row 2, A0 §6 as corrected): an omitted
/// argument is a hole in an argument list, and typing it as an expression would make it legal in every
/// position an expression is legal, which is false.
/// </summary>
public abstract class BaseArgumentSyntax : SyntaxNode
{
    private protected BaseArgumentSyntax(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }
}

/// <summary>
/// One word of a UDF parameter's hint run (A2 §4.9.1). The node KIND records which closed vocabulary the
/// word classified into, and nothing else. When the node carries <c>DAXP1063</c> the kind was assigned by
/// first-free-category placement rather than recognition, and a consumer MUST NOT read semantic meaning
/// out of it.
/// </summary>
public abstract class ParameterHintSyntax : SyntaxNode
{
    private protected ParameterHintSyntax(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
        : base(green, parent, position, tree) { }

    /// <summary>The hint word itself: an <c>IdentifierToken</c>, or <c>TableKeyword</c> for <c>TABLE</c>.</summary>
    public abstract SyntaxToken Token { get; }
}
