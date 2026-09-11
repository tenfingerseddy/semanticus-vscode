namespace Semanticus.Dax.Syntax.Green;

/// <summary>
/// A green node with ordered child slots. Slot order is SOURCE order (A2 §7.3) and is also the order
/// <c>ChildNodesAndTokens()</c> yields. A null slot means an optional child is absent: it occupies no
/// position and is never enumerated (A2 §5.6 rows 1-2).
/// </summary>
internal abstract class GreenSyntaxNode : GreenNode
{
    private readonly GreenNode?[] _slots;

    private protected GreenSyntaxNode(SyntaxKind kind, GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics, Action? onSlotVisit = null)
        : base(kind, diagnostics)
    {
        _slots = slots;

        var flags = FlagsFor(diagnostics);
        if (kind is SyntaxKind.MissingExpression or SyntaxKind.SkippedTokens) flags |= GreenFlags.RecoveryNode;

        var width = 0;
        foreach (var slot in slots)
        {
            if (slot is not null)
            {
                width += slot.FullWidth;
                flags |= slot.Flags;
            }
            // List copies and this aggregation are one 256-item contract. A flat argument or skipped-token
            // list is thousands of slots after the last Eat/Skip poll; leaving the walk uncounted was a late gap.
            onSlotVisit?.Invoke();
        }

        FullWidth = width;
        Flags = flags;
    }

    internal override int SlotCount => _slots.Length;

    internal override GreenNode? GetSlot(int index) => _slots[index];

    /// <summary>The raw slot array. Internal, and never handed out beyond green construction.</summary>
    internal GreenNode?[] Slots => _slots;

    internal override GreenNode WithAdditionalDiagnostics(params DaxDiagnostic[] diagnostics)
        => Rebuild(_slots, Concat(Diagnostics, diagnostics));

    /// <summary>Same runtime type, same slots, different diagnostics. One line per concrete kind.</summary>
    private protected abstract GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics);

    /// <summary>
    /// Materializes the public red wrapper for this green node. Keeping the factory ON the green class
    /// means adding a node kind cannot forget to teach a central switch about it.
    /// </summary>
    internal abstract SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree);
}

/// <summary>
/// The internal list container backing <see cref="SyntaxList{TNode}"/>,
/// <see cref="SeparatedSyntaxList{TNode}"/> and <see cref="SyntaxTokenList"/>.
///
/// <para>
/// It carries <see cref="SyntaxKind.None"/> deliberately. A list is a slot holder, not a language
/// construct: it is never surfaced as a red <see cref="SyntaxNode"/> and never appears in
/// <c>ChildNodesAndTokens()</c> (its children are flattened into the owner's child sequence), so giving
/// it a real kind would burn a frozen number on something no consumer can ever observe.
/// </para>
/// <para>
/// A separated list stores its children ALTERNATING: item, separator, item, separator, item. Every
/// separator and every missing element is retained (A0 §7.5); consumers never reconstruct separators.
/// </para>
/// </summary>
internal sealed class GreenSyntaxList : GreenSyntaxNode
{
    internal static readonly GreenSyntaxList Empty = new(Array.Empty<GreenNode?>(), null);

    internal GreenSyntaxList(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics = null, Action? onSlotVisit = null)
        : base(SyntaxKind.None, slots, diagnostics, onSlotVisit)
    {
    }

    internal override bool IsList => true;

    private protected override GreenSyntaxNode Rebuild(GreenNode?[] slots, IReadOnlyList<DaxDiagnostic>? diagnostics)
        => new GreenSyntaxList(slots, diagnostics);

    internal override SyntaxNode CreateRed(SyntaxNode? parent, int position, DaxSyntaxTree tree)
        => throw new InvalidOperationException("A green list has no red node; it is projected through SyntaxList/SeparatedSyntaxList/SyntaxTokenList.");
}
