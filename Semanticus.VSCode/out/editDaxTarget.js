"use strict";
/** Pick the DAX object Edit DAX should open. Palette invocations pass no node. */
Object.defineProperty(exports, "__esModule", { value: true });
exports.resolveEditDaxNode = resolveEditDaxNode;
function resolveEditDaxNode(clicked, selection, daxKinds) {
    const ok = (n) => !!n?.ref && (!daxKinds || !n.kind || daxKinds.has(n.kind));
    if (ok(clicked))
        return clicked;
    return (selection ?? []).find(ok);
}
//# sourceMappingURL=editDaxTarget.js.map