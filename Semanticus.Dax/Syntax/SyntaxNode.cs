using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// The public, immutable red wrapper over a green node (A0 §3.2, §7.2). It adds what green deliberately
/// lacks: a parent, an absolute position, typed children, and lazy materialization.
///
/// <para>
/// A node's <see cref="Position"/> is the start of its <see cref="FullSpan"/>, derived from the parent's
/// position plus the widths of the preceding slots. Nothing is stored twice.
/// </para>
/// </summary>
public abstract class SyntaxNode
{
    internal readonly GreenSyntaxNode Green;
    private readonly DaxSyntaxTree _tree;
    private SyntaxNode?[]? _materializedNodes;

    // C4 test seam: counts work pulled from the lazy token walk without changing the public surface.
    internal static readonly AsyncLocal<Action<SyntaxNodeOrToken>?> DescendantTokenVisitObserver = new();

    private protected SyntaxNode(GreenSyntaxNode green, SyntaxNode? parent, int position, DaxSyntaxTree tree)
    {
        Green = green;
        Parent = parent;
        Position = position;
        _tree = tree;
    }

    public SyntaxKind Kind => Green.Kind;

    /// <summary>
    /// The tree this node belongs to. A node produced by <c>Update</c>/<c>WithX</c> is DETACHED — it has no
    /// parent and its position is 0 — and reports the tree it was derived from, as provenance. Re-attach it
    /// by rewriting the root and reparsing or by using a rewriter.
    /// </summary>
    public DaxSyntaxTree SyntaxTree => _tree;

    public SyntaxNode? Parent { get; }

    /// <summary>Absolute UTF-16 offset of this node's <see cref="FullSpan"/>.</summary>
    public int Position { get; }

    /// <summary>Includes all leading and trailing trivia owned by this node (A0 §4.3).</summary>
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>Excludes the first token's leading trivia and the last token's trailing trivia (A0 §4.3).</summary>
    public TextSpan Span => TextSpan.FromBounds(
        Position + Green.GetLeadingTriviaWidth(),
        Position + Green.FullWidth - Green.GetTrailingTriviaWidth());

    public bool ContainsDiagnostics => Green.ContainsDiagnostics;

    /// <summary>A0 §7.2: this node or a descendant holds a missing token, skipped-token trivia, a bad token, or a recovery node.</summary>
    public bool ContainsRecovery => Green.ContainsRecovery;

    /// <summary>True only for the nodes that exist because recovery ran: MissingExpression and SkippedTokens.</summary>
    public virtual bool IsRecoveryNode => false;

    public IEnumerable<SyntaxNode> ChildNodes()
    {
        foreach (var child in ChildNodesAndTokens())
            if (child.AsNode() is { } node)
                yield return node;
    }

    /// <summary>
    /// Children in SOURCE order, which is slot order (A2 §7.3). A null slot (an absent optional child) is
    /// skipped entirely; list slots are flattened into their elements and separators.
    /// </summary>
    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        var position = Position;
        for (var i = 0; i < Green.SlotCount; i++)
        {
            var slot = Green.GetSlot(i);
            if (slot is null) continue;

            if (slot.IsList)
            {
                var elementPosition = position;
                for (var j = 0; j < slot.SlotCount; j++)
                {
                    var element = slot.GetSlot(j);
                    if (element is null) continue;
                    yield return Wrap(element, elementPosition);
                    elementPosition += element.FullWidth;
                }
            }
            else
            {
                yield return Wrap(slot, position);
            }

            position += slot.FullWidth;
        }
    }

    /// <summary>
    /// Every token under this node, in source order. Tokens inside structured trivia (skipped source) are
    /// NOT part of the ordinary surface walk (A0 §7.4) unless explicitly requested.
    /// </summary>
    public IEnumerable<SyntaxToken> DescendantTokens(bool descendIntoStructuredTrivia = false)
    {
        var stack = new Stack<TokenWalkItem>();
        stack.Push(TokenWalkItem.ChildrenOf(this));

        try
        {
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (item.Children is { } children)
                {
                    if (!children.MoveNext())
                    {
                        children.Dispose();
                        continue;
                    }

                    stack.Push(item);
                    var child = children.Current;
                    DescendantTokenVisitObserver.Value?.Invoke(child);
                    if (child.AsNode() is { } node)
                    {
                        stack.Push(TokenWalkItem.ChildrenOf(node));
                        continue;
                    }

                    var token = child.AsToken();
                    if (descendIntoStructuredTrivia)
                    {
                        stack.Push(TokenWalkItem.StructuresIn(token.TrailingTrivia));
                        stack.Push(TokenWalkItem.Emit(token));
                        stack.Push(TokenWalkItem.StructuresIn(token.LeadingTrivia));
                    }
                    else
                    {
                        stack.Push(TokenWalkItem.Emit(token));
                    }
                    continue;
                }

                if (item.Trivia is { } trivia)
                {
                    if (!trivia.MoveNext())
                    {
                        trivia.Dispose();
                        continue;
                    }

                    stack.Push(item);
                    if (trivia.Current.GetStructure() is { } structure)
                        stack.Push(TokenWalkItem.ChildrenOf(structure));
                    continue;
                }

                yield return item.Token;
            }
        }
        finally
        {
            foreach (var item in stack) item.Dispose();
        }
    }

    public IEnumerable<SyntaxTrivia> DescendantTrivia(bool descendIntoStructuredTrivia = false)
    {
        if (!descendIntoStructuredTrivia)
        {
            foreach (var token in DescendantTokens())
            {
                foreach (var trivia in token.LeadingTrivia) yield return trivia;
                foreach (var trivia in token.TrailingTrivia) yield return trivia;
            }
            yield break;
        }

        var stack = new Stack<TriviaWalkItem>();
        stack.Push(TriviaWalkItem.ChildrenOf(this));

        try
        {
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (item.Children is { } children)
                {
                    if (!children.MoveNext())
                    {
                        children.Dispose();
                        continue;
                    }

                    stack.Push(item);
                    var child = children.Current;
                    if (child.AsNode() is { } node)
                    {
                        stack.Push(TriviaWalkItem.ChildrenOf(node));
                    }
                    else
                    {
                        var token = child.AsToken();
                        stack.Push(TriviaWalkItem.TriviaIn(token.TrailingTrivia));
                        stack.Push(TriviaWalkItem.TriviaIn(token.LeadingTrivia));
                    }
                    continue;
                }

                var trivia = item.Trivia!;
                if (!trivia.MoveNext())
                {
                    trivia.Dispose();
                    continue;
                }

                stack.Push(item);
                var current = trivia.Current;
                yield return current;
                if (current.GetStructure() is { } structure)
                    stack.Push(TriviaWalkItem.ChildrenOf(structure));
            }
        }
        finally
        {
            foreach (var item in stack) item.Dispose();
        }
    }

    /// <summary>The first token, skipping zero-width (missing) tokens unless asked for them.</summary>
    public SyntaxToken GetFirstToken(bool includeZeroWidth = false)
    {
        foreach (var token in DescendantTokens())
            if (includeZeroWidth || token.FullSpan.Length > 0)
                return token;
        return default;
    }

    public SyntaxToken GetLastToken(bool includeZeroWidth = false)
    {
        var last = default(SyntaxToken);
        foreach (var token in DescendantTokens())
            if (includeZeroWidth || token.FullSpan.Length > 0)
                last = token;
        return last;
    }

    /// <summary>
    /// Every diagnostic attached to this node or anything under it, in nondecreasing <c>Span.Start</c>
    /// (A2 §8.1 rule 7). Diagnostics live on the green element that owns the diagnosed source; ancestors
    /// aggregate and never re-attach (A2 §8.1 rules 2-3), so nothing is reported twice.
    /// </summary>
    public IReadOnlyList<DaxDiagnostic> GetDiagnostics()
    {
        var collected = new List<DaxDiagnostic>();
        DiagnosticCollector.Collect(Green, collected);
        return DiagnosticCollector.Sorted(collected);
    }

    /// <summary>The exact source text of <see cref="FullSpan"/>, trivia included. It never formats.</summary>
    public string ToFullString() => Green.ToFullString();

    /// <summary>The exact source text of <see cref="Span"/>, exterior trivia excluded. It never formats.</summary>
    public override string ToString()
    {
        var full = Green.ToFullString();
        var start = Green.GetLeadingTriviaWidth();
        var length = Green.FullWidth - start - Green.GetTrailingTriviaWidth();
        return full.Substring(start, length);
    }

    public void WriteTo(TextWriter writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        writer.Write(ToFullString());
    }

    public abstract void Accept(DaxSyntaxVisitor visitor);

    public abstract TResult? Accept<TResult>(DaxSyntaxVisitor<TResult> visitor);

    // ---- slot access used by the typed accessors ------------------------------------------------

    private protected int GetSlotOffset(int index)
    {
        var offset = Position;
        for (var i = 0; i < index; i++) offset += Green.GetSlot(i)?.FullWidth ?? 0;
        return offset;
    }

    /// <summary>
    /// An optional token child that is absent returns a DEFAULT token (<c>Kind == None</c>), which is not a
    /// tree token and is never enumerated (A2 §5.6 row 2). A required-but-absent token is a real zero-width
    /// green token with <c>IsMissing == true</c> and IS returned here.
    /// </summary>
    private protected SyntaxToken GetToken(int index)
        => Green.GetSlot(index) is GreenToken token
            ? new SyntaxToken(token, this, GetSlotOffset(index), _tree)
            : default;

    private protected SyntaxNode? GetNode(int index)
    {
        if (Green.GetSlot(index) is not GreenSyntaxNode green || green.IsList) return null;

        var cache = _materializedNodes ??= new SyntaxNode?[Green.SlotCount];
        var existing = Volatile.Read(ref cache[index]);
        if (existing is not null) return existing;

        var created = green.CreateRed(this, GetSlotOffset(index), _tree);
        // Reference identity of red wrappers is not a public guarantee (A0 §3.2), so a benign race that
        // materializes twice is harmless; the interlock just stops the cache from flapping.
        return Interlocked.CompareExchange(ref cache[index], created, null) ?? created;
    }

    private protected SyntaxNode GetRequiredNode(int index)
        => GetNode(index) ?? throw new InvalidOperationException($"{Kind} slot {index} is required but the green slot is null.");

    private protected SyntaxList<TNode> GetList<TNode>(int index) where TNode : SyntaxNode
        => Green.GetSlot(index) is GreenSyntaxList list
            ? new SyntaxList<TNode>(list, this, GetSlotOffset(index), _tree)
            : default;

    private protected SeparatedSyntaxList<TNode> GetSeparatedList<TNode>(int index) where TNode : SyntaxNode
        => Green.GetSlot(index) is GreenSyntaxList list
            ? new SeparatedSyntaxList<TNode>(list, this, GetSlotOffset(index), _tree)
            : default;

    private protected SyntaxTokenList GetTokenList(int index)
        => Green.GetSlot(index) is GreenSyntaxList list
            ? new SyntaxTokenList(list, this, GetSlotOffset(index), _tree)
            : default;

    /// <summary>A required token slot cannot hold a default token; that would silently drop a child.</summary>
    private protected static GreenToken RequiredGreen(SyntaxToken token, string parameterName)
        => token.Green ?? throw new ArgumentException("A required token child cannot be a default SyntaxToken.", parameterName);

    private protected static GreenSyntaxNode RequiredGreen(SyntaxNode node, string parameterName)
        => node?.Green ?? throw new ArgumentNullException(parameterName);

    private SyntaxNodeOrToken Wrap(GreenNode green, int position)
        => green is GreenToken token
            ? new SyntaxNodeOrToken(new SyntaxToken(token, this, position, _tree))
            : new SyntaxNodeOrToken(((GreenSyntaxNode)green).CreateRed(this, position, _tree));

    private readonly struct TokenWalkItem
    {
        private TokenWalkItem(
            IEnumerator<SyntaxNodeOrToken>? children,
            IEnumerator<SyntaxTrivia>? trivia,
            SyntaxToken token)
        {
            Children = children;
            Trivia = trivia;
            Token = token;
        }

        internal IEnumerator<SyntaxNodeOrToken>? Children { get; }
        internal IEnumerator<SyntaxTrivia>? Trivia { get; }
        internal SyntaxToken Token { get; }

        internal static TokenWalkItem ChildrenOf(SyntaxNode node)
            => new(node.ChildNodesAndTokens().GetEnumerator(), null, default);

        internal static TokenWalkItem StructuresIn(SyntaxTriviaList trivia)
            => new(null, trivia.GetEnumerator(), default);

        internal static TokenWalkItem Emit(SyntaxToken token) => new(null, null, token);

        internal void Dispose()
        {
            Children?.Dispose();
            Trivia?.Dispose();
        }
    }

    private readonly struct TriviaWalkItem
    {
        private TriviaWalkItem(IEnumerator<SyntaxNodeOrToken>? children, IEnumerator<SyntaxTrivia>? trivia)
        {
            Children = children;
            Trivia = trivia;
        }

        internal IEnumerator<SyntaxNodeOrToken>? Children { get; }
        internal IEnumerator<SyntaxTrivia>? Trivia { get; }

        internal static TriviaWalkItem ChildrenOf(SyntaxNode node)
            => new(node.ChildNodesAndTokens().GetEnumerator(), null);

        internal static TriviaWalkItem TriviaIn(SyntaxTriviaList trivia)
            => new(null, trivia.GetEnumerator());

        internal void Dispose()
        {
            Children?.Dispose();
            Trivia?.Dispose();
        }
    }
}

/// <summary>
/// Walks a green subtree collecting attached diagnostics. It descends into tokens, their trivia, and
/// structured trivia, because a token re-hosted as skipped source keeps its lexical diagnostics
/// unchanged (A2 §8.1 rule 4) and those must still surface.
/// </summary>
internal static class DiagnosticCollector
{
    /// <summary>
    /// ITERATIVE, over an explicit stack, for the same reason <c>GreenNode.WriteTo</c> is: golden
    /// <c>G-P-REC-009</c> parses a 100,000-deep left-associative chain without recursing, so a walker that
    /// recursed over depth would overflow on a tree the parser built successfully (A2 §14 defect 5).
    /// Children are pushed in reverse so they pop in source order, which keeps the pre-sort sequence
    /// identical to a depth-first walk and therefore reproducible run to run (A2 §2 rule 5).
    /// </summary>
    internal static void Collect(GreenNode green, List<DaxDiagnostic> into)
    {
        var stack = new Stack<GreenNode>();
        stack.Push(green);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Diagnostics is { } own) into.AddRange(own);

            if (node is GreenToken token)
            {
                for (var i = token.TrailingTrivia.Count - 1; i >= 0; i--) stack.Push(token.TrailingTrivia[i]);
                for (var i = token.LeadingTrivia.Count - 1; i >= 0; i--) stack.Push(token.LeadingTrivia[i]);
                continue;
            }

            if (node is GreenTrivia trivia)
            {
                if (trivia.Structure is { } structure) stack.Push(structure);
                continue;
            }

            for (var i = node.SlotCount - 1; i >= 0; i--)
                if (node.GetSlot(i) is { } slot)
                    stack.Push(slot);
        }
    }

    /// <summary>Nondecreasing <c>Span.Start</c>. Order among equal starts is unspecified (A0 §10.2), so a STABLE sort keeps it at least reproducible.</summary>
    internal static IReadOnlyList<DaxDiagnostic> Sorted(List<DaxDiagnostic> diagnostics)
        => diagnostics.OrderBy(d => d.Span.Start).ToArray();
}
