using System.Text;

namespace Semanticus.Dax.Syntax.Green;

/// <summary>
/// Aggregated properties of a green subtree, computed ONCE at construction (A0 §3.1) so that a red
/// wrapper never has to walk to answer <c>ContainsRecovery</c> or <c>ContainsDiagnostics</c>.
/// </summary>
[Flags]
internal enum GreenFlags
{
    None = 0,
    Diagnostics = 1 << 0,
    Missing = 1 << 1,
    Skipped = 1 << 2,
    StructuredTrivia = 1 << 3,
    BadToken = 1 << 4,
    RecoveryNode = 1 << 5,

    /// <summary>A0 §7.2: missing token, skipped-token trivia, bad token, or explicit recovery node.</summary>
    Recovery = Missing | Skipped | BadToken | RecoveryNode
}

/// <summary>
/// The internal, immutable, parentless, POSITIONLESS half of the tree (A0 §3.1). A green node knows its
/// kind, its ordered slots, its full width, its aggregate flags, and any diagnostics attached directly to
/// it. It never knows where it sits in a file and never references a red wrapper.
///
/// <para>
/// Diagnostics are attached to the green element that owns the diagnosed source (A2 §8.1 rules 1-3); the
/// red layer only projects them. Note the one wrinkle this creates: <see cref="DaxDiagnostic"/> carries an
/// absolute <c>TextSpan</c>, so a green subtree that carries diagnostics is only reusable in a tree over
/// the same text. PR-1 reparses on every edit (A0 §7.1 permits it), so the case does not arise yet;
/// a future incremental reuse pass must not share a diagnostic-carrying subtree across an edit.
/// </para>
/// </summary>
internal abstract class GreenNode
{
    private protected GreenNode(SyntaxKind kind, IReadOnlyList<DaxDiagnostic>? diagnostics)
    {
        Kind = kind;
        Diagnostics = diagnostics is { Count: > 0 } ? diagnostics : null;
    }

    internal SyntaxKind Kind { get; }

    /// <summary>Diagnostics attached to THIS element only. Ancestors aggregate; they never re-attach.</summary>
    internal IReadOnlyList<DaxDiagnostic>? Diagnostics { get; }

    /// <summary>Width in UTF-16 code units, including owned trivia. Computed once at construction.</summary>
    internal int FullWidth { get; private protected set; }

    internal GreenFlags Flags { get; private protected set; }

    internal abstract int SlotCount { get; }

    internal abstract GreenNode? GetSlot(int index);

    /// <summary>
    /// Writes the exact source text of this subtree, trivia included. The walk is ITERATIVE, over an
    /// explicit stack, and must stay that way: A0 §5.4 promises exact round-trip for EVERY finite input with
    /// no depth caveat, and golden <c>G-P-REC-009</c> builds a 100,000-deep left-associative chain from a
    /// parse loop that never recursed (A2 §14 defect 5). A recursive writer overflowed on a tree the parser
    /// had already produced successfully, which moves the failure rather than removing it.
    ///
    /// <para>
    /// Stack items are either a <see cref="GreenNode"/> still to expand or a raw <see cref="string"/> ready
    /// to append. Children are pushed in reverse so they pop in source order. A token with no structured
    /// trivia — the overwhelmingly common case — is emitted inline without touching the stack.
    /// </para>
    /// </summary>
    internal void WriteTo(StringBuilder builder)
    {
        var stack = new Stack<object>();
        stack.Push(this);

        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item is string text)
            {
                builder.Append(text);
                continue;
            }

            switch ((GreenNode)item)
            {
                case GreenToken token when !token.ContainsStructuredTrivia:
                    foreach (var trivia in token.LeadingTrivia) builder.Append(trivia.Text);
                    builder.Append(token.Text);
                    foreach (var trivia in token.TrailingTrivia) builder.Append(trivia.Text);
                    break;

                case GreenToken token:
                    for (var i = token.TrailingTrivia.Count - 1; i >= 0; i--) stack.Push(token.TrailingTrivia[i]);
                    stack.Push(token.Text);
                    for (var i = token.LeadingTrivia.Count - 1; i >= 0; i--) stack.Push(token.LeadingTrivia[i]);
                    break;

                case GreenTrivia { Structure: { } structure }:
                    stack.Push(structure);
                    break;

                case GreenTrivia trivia:
                    builder.Append(trivia.Text);
                    break;

                case var node:
                    for (var i = node.SlotCount - 1; i >= 0; i--)
                        if (node.GetSlot(i) is { } slot)
                            stack.Push(slot);
                    break;
            }
        }
    }

    /// <summary>
    /// Green is immutable, so adding a diagnostic produces a NEW node carrying the union. The parser needs
    /// this because it often decides a construct is faulty only after the node has been built.
    /// </summary>
    internal abstract GreenNode WithAdditionalDiagnostics(params DaxDiagnostic[] diagnostics);

    internal virtual bool IsToken => false;
    internal virtual bool IsTrivia => false;

    /// <summary>True for the internal list container, which is a slot holder and never a public node.</summary>
    internal virtual bool IsList => false;

    internal bool ContainsDiagnostics => (Flags & GreenFlags.Diagnostics) != 0;
    internal bool ContainsMissing => (Flags & GreenFlags.Missing) != 0;
    internal bool ContainsSkipped => (Flags & GreenFlags.Skipped) != 0;
    internal bool ContainsStructuredTrivia => (Flags & GreenFlags.StructuredTrivia) != 0;
    internal bool ContainsBadToken => (Flags & GreenFlags.BadToken) != 0;
    internal bool ContainsRecovery => (Flags & GreenFlags.Recovery) != 0;

    internal string ToFullString()
    {
        var builder = new StringBuilder(FullWidth);
        WriteTo(builder);
        return builder.ToString();
    }

    /// <summary>
    /// The first token of positive full width, or null when the subtree is entirely zero width. Zero-width
    /// slots are skipped so that an inserted missing token does not make an enclosing node's
    /// <c>Span</c> collapse onto the trivia of a real neighbour (A0 §4.3).
    /// </summary>
    internal GreenNode? GetFirstTerminal()
    {
        GreenNode? node = this;
        while (node is not null && !node.IsToken)
        {
            GreenNode? next = null;
            for (var i = 0; i < node.SlotCount; i++)
            {
                var child = node.GetSlot(i);
                if (child is { FullWidth: > 0 }) { next = child; break; }
            }
            node = next;
        }
        return node;
    }

    internal GreenNode? GetLastTerminal()
    {
        GreenNode? node = this;
        while (node is not null && !node.IsToken)
        {
            GreenNode? next = null;
            for (var i = node.SlotCount - 1; i >= 0; i--)
            {
                var child = node.GetSlot(i);
                if (child is { FullWidth: > 0 }) { next = child; break; }
            }
            node = next;
        }
        return node;
    }

    internal int GetLeadingTriviaWidth() => (GetFirstTerminal() as GreenToken)?.LeadingTriviaWidth ?? 0;

    internal int GetTrailingTriviaWidth() => (GetLastTerminal() as GreenToken)?.TrailingTriviaWidth ?? 0;

    private protected static GreenFlags FlagsFor(IReadOnlyList<DaxDiagnostic>? diagnostics)
        => diagnostics is { Count: > 0 } ? GreenFlags.Diagnostics : GreenFlags.None;

    private protected static IReadOnlyList<DaxDiagnostic>? Concat(
        IReadOnlyList<DaxDiagnostic>? existing,
        IReadOnlyList<DaxDiagnostic>? additional)
    {
        if (additional is not { Count: > 0 }) return existing;
        if (existing is not { Count: > 0 }) return additional;
        var combined = new DaxDiagnostic[existing.Count + additional.Count];
        for (var i = 0; i < existing.Count; i++) combined[i] = existing[i];
        for (var i = 0; i < additional.Count; i++) combined[existing.Count + i] = additional[i];
        return combined;
    }
}
