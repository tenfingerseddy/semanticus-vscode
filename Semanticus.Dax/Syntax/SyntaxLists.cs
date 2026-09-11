using System.Collections;
using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax;

/// <summary>
/// An ordered list of nodes in one slot (A0 §7.5). Immutable value type; a default instance is an empty
/// list. Elements are materialized on demand, so reference identity of an element is not a guarantee
/// (A0 §3.2).
/// </summary>
public readonly struct SyntaxList<TNode> : IReadOnlyList<TNode> where TNode : SyntaxNode
{
    private readonly GreenSyntaxList? _green;
    private readonly SyntaxNode? _parent;
    private readonly int _position;
    private readonly DaxSyntaxTree? _tree;

    internal SyntaxList(GreenSyntaxList? green, SyntaxNode? parent, int position, DaxSyntaxTree? tree)
    {
        _green = green;
        _parent = parent;
        _position = position;
        _tree = tree;
    }

    internal GreenSyntaxList Green => _green ?? GreenSyntaxList.Empty;

    public int Count => _green?.SlotCount ?? 0;

    public TNode this[int index]
    {
        get
        {
            if (_green is null || index < 0 || index >= _green.SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
            var position = _position;
            for (var i = 0; i < index; i++) position += _green.GetSlot(i)?.FullWidth ?? 0;
            var element = (GreenSyntaxNode)_green.GetSlot(index)!;
            // _tree is null only for an empty detached list, which has no element to materialize.
            return (TNode)element.CreateRed(_parent, position, _tree!);
        }
    }

    /// <summary>The list's own full span, or a zero-width span at its position when empty.</summary>
    public TextSpan FullSpan => new(_position, _green?.FullWidth ?? 0);

    public IEnumerator<TNode> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Builds a DETACHED list from existing nodes, for <c>Update</c>/<c>WithX</c> and rewriters.</summary>
    public static SyntaxList<TNode> Create(IEnumerable<TNode> nodes)
    {
        if (nodes is null) throw new ArgumentNullException(nameof(nodes));
        var items = new List<GreenNode?>();
        DaxSyntaxTree? tree = null;
        foreach (var node in nodes)
        {
            if (node is null) throw new ArgumentException("A list element cannot be null.", nameof(nodes));
            tree ??= node.SyntaxTree;
            items.Add(node.Green);
        }
        return new SyntaxList<TNode>(GreenFactory.List(items), null, 0, tree);
    }
}

/// <summary>
/// A list of nodes separated by tokens (A0 §7.5). It retains EVERY separator and EVERY element, including
/// missing and omitted ones; consumers must never reconstruct separators from positions.
///
/// <para>Backing layout is alternating: item, separator, item, separator, item.</para>
/// </summary>
public readonly struct SeparatedSyntaxList<TNode> : IReadOnlyList<TNode> where TNode : SyntaxNode
{
    private readonly GreenSyntaxList? _green;
    private readonly SyntaxNode? _parent;
    private readonly int _position;
    private readonly DaxSyntaxTree? _tree;

    internal SeparatedSyntaxList(GreenSyntaxList? green, SyntaxNode? parent, int position, DaxSyntaxTree? tree)
    {
        _green = green;
        _parent = parent;
        _position = position;
        _tree = tree;
    }

    internal GreenSyntaxList Green => _green ?? GreenSyntaxList.Empty;

    private int SlotCount => _green?.SlotCount ?? 0;

    public int Count => (SlotCount + 1) / 2;

    public int SeparatorCount => SlotCount / 2;

    public TNode this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            var slot = index * 2;
            return (TNode)((GreenSyntaxNode)_green!.GetSlot(slot)!).CreateRed(_parent, SlotPosition(slot), _tree!);
        }
    }

    public SyntaxToken GetSeparator(int index)
    {
        if (index < 0 || index >= SeparatorCount) throw new ArgumentOutOfRangeException(nameof(index));
        var slot = index * 2 + 1;
        return new SyntaxToken((GreenToken)_green!.GetSlot(slot)!, _parent, SlotPosition(slot), _tree);
    }

    /// <summary>Elements and separators interleaved, in source order.</summary>
    public IEnumerable<SyntaxNodeOrToken> GetWithSeparators()
    {
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var green = _green!.GetSlot(slot);
            if (green is null) continue;
            yield return green is GreenToken token
                ? new SyntaxNodeOrToken(new SyntaxToken(token, _parent, SlotPosition(slot), _tree))
                : new SyntaxNodeOrToken(((GreenSyntaxNode)green).CreateRed(_parent, SlotPosition(slot), _tree!));
        }
    }

    public TextSpan FullSpan => new(_position, _green?.FullWidth ?? 0);

    public IEnumerator<TNode> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Builds a DETACHED separated list. <paramref name="separators"/> is items-1 or items long.</summary>
    public static SeparatedSyntaxList<TNode> Create(IReadOnlyList<TNode> nodes, IReadOnlyList<SyntaxToken> separators)
    {
        if (nodes is null) throw new ArgumentNullException(nameof(nodes));
        if (separators is null) throw new ArgumentNullException(nameof(separators));

        var items = new List<GreenNode>(nodes.Count);
        DaxSyntaxTree? tree = null;
        foreach (var node in nodes)
        {
            if (node is null) throw new ArgumentException("A list element cannot be null.", nameof(nodes));
            tree ??= node.SyntaxTree;
            items.Add(node.Green);
        }

        var greenSeparators = new List<GreenToken>(separators.Count);
        foreach (var separator in separators)
        {
            if (separator.Green is null) throw new ArgumentException("A separator cannot be a default SyntaxToken.", nameof(separators));
            greenSeparators.Add(separator.Green);
        }

        return new SeparatedSyntaxList<TNode>(GreenFactory.SeparatedList(items, greenSeparators), null, 0, tree);
    }

    private int SlotPosition(int slot)
    {
        var position = _position;
        for (var i = 0; i < slot; i++) position += _green!.GetSlot(i)?.FullWidth ?? 0;
        return position;
    }
}

/// <summary>An ordered list of tokens in one slot; backs <c>SkippedTokensSyntax.Tokens</c> (A2 §7.3 kind 4000).</summary>
public readonly struct SyntaxTokenList : IReadOnlyList<SyntaxToken>
{
    private readonly GreenSyntaxList? _green;
    private readonly SyntaxNode? _parent;
    private readonly int _position;
    private readonly DaxSyntaxTree? _tree;

    internal SyntaxTokenList(GreenSyntaxList? green, SyntaxNode? parent, int position, DaxSyntaxTree? tree)
    {
        _green = green;
        _parent = parent;
        _position = position;
        _tree = tree;
    }

    internal GreenSyntaxList Green => _green ?? GreenSyntaxList.Empty;

    public int Count => _green?.SlotCount ?? 0;

    public SyntaxToken this[int index]
    {
        get
        {
            if (_green is null || index < 0 || index >= _green.SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
            var position = _position;
            for (var i = 0; i < index; i++) position += _green.GetSlot(i)?.FullWidth ?? 0;
            return new SyntaxToken((GreenToken)_green.GetSlot(index)!, _parent, position, _tree);
        }
    }

    public IEnumerator<SyntaxToken> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static SyntaxTokenList Create(IReadOnlyList<SyntaxToken> tokens)
    {
        if (tokens is null) throw new ArgumentNullException(nameof(tokens));
        var items = new List<GreenNode?>(tokens.Count);
        foreach (var token in tokens)
        {
            if (token.Green is null) throw new ArgumentException("A default SyntaxToken cannot be a list element.", nameof(tokens));
            items.Add(token.Green);
        }
        return new SyntaxTokenList(GreenFactory.List(items), null, 0, null);
    }
}

/// <summary>The trivia owned by one token, on one side (A0 §7.5).</summary>
public readonly struct SyntaxTriviaList : IReadOnlyList<SyntaxTrivia>
{
    private readonly IReadOnlyList<GreenTrivia>? _green;
    private readonly SyntaxToken _token;
    private readonly int _position;
    private readonly DaxSyntaxTree? _tree;

    internal SyntaxTriviaList(IReadOnlyList<GreenTrivia>? green, SyntaxToken token, int position, DaxSyntaxTree? tree)
    {
        _green = green;
        _token = token;
        _position = position;
        _tree = tree;
    }

    public int Count => _green?.Count ?? 0;

    public SyntaxTrivia this[int index]
    {
        get
        {
            if (_green is null || index < 0 || index >= _green.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var position = _position;
            for (var i = 0; i < index; i++) position += _green[i].FullWidth;
            return new SyntaxTrivia(_green[index], _token, position, _tree);
        }
    }

    public IEnumerator<SyntaxTrivia> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A child that may be a node or a token, without forcing a cast at every call site (A0 §7.5).</summary>
public readonly struct SyntaxNodeOrToken : IEquatable<SyntaxNodeOrToken>
{
    private readonly SyntaxNode? _node;
    private readonly SyntaxToken _token;

    internal SyntaxNodeOrToken(SyntaxNode node)
    {
        _node = node;
        _token = default;
    }

    internal SyntaxNodeOrToken(SyntaxToken token)
    {
        _node = null;
        _token = token;
    }

    public bool IsNode => _node is not null;

    public bool IsToken => _node is null && _token.Kind != SyntaxKind.None;

    public SyntaxKind Kind => _node?.Kind ?? _token.Kind;

    public SyntaxNode? AsNode() => _node;

    public SyntaxToken AsToken() => _token;

    public SyntaxNode? Parent => _node?.Parent ?? _token.Parent;

    public TextSpan Span => _node?.Span ?? _token.Span;

    public TextSpan FullSpan => _node?.FullSpan ?? _token.FullSpan;

    public string ToFullString() => _node?.ToFullString() ?? _token.ToFullString();

    public override string ToString() => _node?.ToString() ?? _token.ToString();

    public bool Equals(SyntaxNodeOrToken other) => ReferenceEquals(_node, other._node) && _token == other._token;

    public override bool Equals(object? obj) => obj is SyntaxNodeOrToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_node, _token);

    public static bool operator ==(SyntaxNodeOrToken left, SyntaxNodeOrToken right) => left.Equals(right);

    public static bool operator !=(SyntaxNodeOrToken left, SyntaxNodeOrToken right) => !left.Equals(right);
}
