using System.Text;

namespace Semanticus.DaxSpike;

// A0 green-tree prototype: immutable, parentless, positionless nodes (design §3.1). Width is
// computed once; a parentless green tree round-trips by concatenating leaf full-text in order.
// Red wrappers are intentionally omitted (the task makes green + exact round-trip the deliverable);
// absolute spans/parent traversal would be a thin lazy layer over these, per design §3.2.

public enum NodeKind
{
    // roots / expressions
    CompilationUnit, LiteralExpr, NameRef, QualifiedRef, BracketRef, ParenExpr,
    RowConstructor, TableConstructor, CallExpr, ArgumentList, Argument, OmittedArgument,
    UnaryExpr, BinaryExpr, MembershipExpr, VarExpr, VarDecl, ReturnClause, DottedName,
    // recovery
    SkippedTokens,
    // token leaf marker (GreenToken carries its own TokenKind)
    Token,
}

// Trivia families the design §5.1 asks for. "Bad" = source the lexer dropped or couldn't classify.
public enum TriviaKind { Whitespace, EndOfLine, SingleLineComment, DelimitedComment, Bad }

public readonly record struct GreenTrivia(TriviaKind Kind, string Text)
{
    public int Width => Text.Length;
}

public abstract class GreenNode
{
    public NodeKind Kind { get; }
    public int FullWidth { get; }
    public bool ContainsSkipped { get; }
    public bool ContainsMissing { get; }

    protected GreenNode(NodeKind kind, int fullWidth, bool skipped, bool missing)
    { Kind = kind; FullWidth = fullWidth; ContainsSkipped = skipped; ContainsMissing = missing; }

    public abstract void WriteTo(StringBuilder sb);
    public abstract IEnumerable<GreenNode> Children { get; }

    public string ToFullString() { var sb = new StringBuilder(FullWidth); WriteTo(sb); return sb.ToString(); }

    // Debug s-expression for golden structure assertions (kinds + token raw text).
    public string ToSExpr()
    {
        var sb = new StringBuilder();
        Render(this, sb);
        return sb.ToString();
    }
    private static void Render(GreenNode n, StringBuilder sb)
    {
        if (n is GreenToken t) { sb.Append('\'').Append(t.RawText.Length == 0 ? (t.IsMissing ? "<missing>" : "") : t.RawText).Append('\''); return; }
        sb.Append('(').Append(n.Kind);
        foreach (var c in n.Children) { sb.Append(' '); Render(c, sb); }
        sb.Append(')');
    }
}

// A leaf: leading trivia + exact raw source spelling + trailing trivia (design §3.1/§5.2).
public sealed class GreenToken : GreenNode
{
    public TokenKind TokenKind { get; }
    public string RawText { get; }        // exact source slice (never the lexer's decoded Text)
    public string ValueText { get; }      // decoded convenience value (design §5.3)
    public bool IsMissing { get; }
    public IReadOnlyList<GreenTrivia> Leading { get; }
    public IReadOnlyList<GreenTrivia> Trailing { get; }

    public GreenToken(TokenKind tk, string raw, string valueText, bool isMissing,
                      IReadOnlyList<GreenTrivia> leading, IReadOnlyList<GreenTrivia> trailing)
        : base(NodeKind.Token, Width(leading) + raw.Length + Width(trailing), false, isMissing)
    { TokenKind = tk; RawText = raw; ValueText = valueText; IsMissing = isMissing; Leading = leading; Trailing = trailing; }

    private static int Width(IReadOnlyList<GreenTrivia> tl) { int w = 0; foreach (var t in tl) w += t.Width; return w; }

    public override IEnumerable<GreenNode> Children => Array.Empty<GreenNode>();

    public override void WriteTo(StringBuilder sb)
    {
        foreach (var t in Leading) sb.Append(t.Text);
        sb.Append(RawText);
        foreach (var t in Trailing) sb.Append(t.Text);
    }
}

// A structural node: an ordered list of child green nodes/tokens (design §3.1 slots).
public sealed class GreenSyntax : GreenNode
{
    private readonly GreenNode[] _slots;
    public GreenSyntax(NodeKind kind, params GreenNode[] slots)
        : base(kind, Sum(slots), AnySkipped(kind, slots), AnyMissing(slots))
    { _slots = slots; }

    private static int Sum(GreenNode[] s) { int w = 0; foreach (var n in s) w += n.FullWidth; return w; }
    private static bool AnySkipped(NodeKind k, GreenNode[] s) { if (k == NodeKind.SkippedTokens) return true; foreach (var n in s) if (n.ContainsSkipped) return true; return false; }
    private static bool AnyMissing(GreenNode[] s) { foreach (var n in s) if (n.ContainsMissing) return true; return false; }

    public IReadOnlyList<GreenNode> Slots => _slots;
    public override IEnumerable<GreenNode> Children => _slots;
    public override void WriteTo(StringBuilder sb) { foreach (var n in _slots) n.WriteTo(sb); }
}
