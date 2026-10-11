// What the status-bar health chip is drawn from (journey m11 F10). Pure, so test/health-chip.test.mjs runs it.
//
// The engine scores every commit on its own, a walk through the history included. Undo to here that put the model
// back exactly then read "9 new issues", because the walk re-added the findings the change had fixed, scored as if
// the walk were a new edit. A walk is not a change the person made, so the chip clears on one: the model is back
// where it was, and the chip says nothing new rather than calling the way back worse.
//
// The engine says which commits are walks: model/didChange carries `replay` ("undo" or "redo") on a walk and nothing
// on an edit (Protocol.cs, ChangeNotification.Replay). The label is the op's own words and is never read for this.

/** True when this model/didChange only walked the history (Undo, Redo, Undo to here, Redo to here). */
export function isHistoryWalk(n: { replay?: string | null } | undefined | null): boolean {
    return n?.replay === 'undo' || n?.replay === 'redo';
}

/** The health the chip shows for one model/didChange: the commit's own, or none for a history walk. */
export function chipHealthFor<T>(n: { label?: string; replay?: string | null; health?: T }): T | undefined {
    return isHistoryWalk(n) ? undefined : n.health;
}
