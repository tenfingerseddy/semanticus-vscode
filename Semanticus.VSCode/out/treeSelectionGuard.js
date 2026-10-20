"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.gridStaysOnRedraw = exports.REFRESH_WINDOW_MS = void 0;
exports.createTreeSelectionGuard = createTreeSelectionGuard;
// Tells a person's click in the Model tree from the tree's own refresh. A model edit (Claude renaming the shown object)
// refreshes the tree, and the tree then drops the selection or puts the same one back. That is not the person, so it must
// not take the wheel from Claude. A real click still counts: it picks something new, or it comes after the quiet window.
exports.REFRESH_WINDOW_MS = 1000;
/** A rename in the grid makes the tree redraw, and the redraw drops the selection. That is not the person clearing it: the grid keeps the renamed object. */
const gridStaysOnRedraw = (kind, count) => kind === 'refresh' && count === 0;
exports.gridStaysOnRedraw = gridStaysOnRedraw;
function createTreeSelectionGuard(now = Date.now, windowMs = exports.REFRESH_WINDOW_MS) {
    let refreshAt = -Infinity;
    let last = [];
    return {
        noteRefresh() { refreshAt = now(); },
        // 'refresh' when the event is only the tree redrawing; 'person' otherwise.
        classify(refs) {
            const inWindow = now() - refreshAt < windowMs;
            const same = refs.length === last.length && refs.every((r) => last.includes(r));
            const refreshed = inWindow && (refs.length === 0 || same);
            if (!refreshed)
                last = [...refs]; // the old selection stays the reference through a refresh drop
            return refreshed ? 'refresh' : 'person';
        },
    };
}
//# sourceMappingURL=treeSelectionGuard.js.map