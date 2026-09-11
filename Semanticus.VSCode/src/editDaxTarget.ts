/** Pick the DAX object Edit DAX should open. Palette invocations pass no node. */

export interface EditDaxNode { ref?: string; name?: string; kind?: string }

export function resolveEditDaxNode(
    clicked: EditDaxNode | undefined | null,
    selection: readonly EditDaxNode[] | undefined | null,
    daxKinds?: Set<string>,
): EditDaxNode | undefined {
    const ok = (n: EditDaxNode | undefined | null): n is EditDaxNode =>
        !!n?.ref && (!daxKinds || !n.kind || daxKinds.has(n.kind));
    if (ok(clicked)) return clicked;
    return (selection ?? []).find(ok);
}
