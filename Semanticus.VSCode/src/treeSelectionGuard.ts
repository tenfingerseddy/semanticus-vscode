// Tells a person's click in the Model tree from the tree's own refresh. A model edit (Claude renaming the shown object)
// refreshes the tree, and the tree then drops the selection or puts the same one back. That is not the person, so it must
// not take the wheel from Claude. A real click still counts: it picks something new, or it comes after the quiet window.
export const REFRESH_WINDOW_MS = 1000;

export function createTreeSelectionGuard(now: () => number = Date.now, windowMs = REFRESH_WINDOW_MS) {
    let refreshAt = -Infinity;
    let last: string[] = [];
    return {
        noteRefresh(): void { refreshAt = now(); },
        // 'refresh' when the event is only the tree redrawing; 'person' otherwise.
        classify(refs: readonly string[]): 'person' | 'refresh' {
            const inWindow = now() - refreshAt < windowMs;
            const same = refs.length === last.length && refs.every((r) => last.includes(r));
            const refreshed = inWindow && (refs.length === 0 || same);
            if (!refreshed) last = [...refs];   // the old selection stays the reference through a refresh drop
            return refreshed ? 'refresh' : 'person';
        },
    };
}
