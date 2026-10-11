"use strict";
// The status bar's undo label for a draft that ran (composer contract 10.3: "Undo: ran 7 steps from your words";
// lane author-gaps, B10). Pure, so test/draft-undo-chip.test.mjs runs it.
//
// The engine writes the words (DraftView.undoLabel), and only once the draft changed the model. The item shows when a
// draft reports one and stays until the model moves again: the next change, or the Undo itself, is a later revision,
// and from then on one Undo would take back something else, so the label would no longer be true.
Object.defineProperty(exports, "__esModule", { value: true });
exports.draftUndoLabel = draftUndoLabel;
exports.chipAfterDraft = chipAfterDraft;
exports.chipAfterModelChange = chipAfterModelChange;
/** The label a draft/didChange carries, or null when this draft has nothing for one Undo to take back yet. */
function draftUndoLabel(view) {
    const v = view;
    if (!v || typeof v.undoLabel !== 'string' || !v.undoLabel)
        return null;
    return v.state === 'running' ? null : v.undoLabel;
}
/** The chip after a draft/didChange, given the newest model revision seen. */
function chipAfterDraft(current, view, revision) {
    const label = draftUndoLabel(view);
    if (!label)
        return current;
    return { text: label, tooltip: 'Press to undo it. The next change to the model clears this.', shownAtRevision: revision };
}
/** The chip after a model/didChange: kept for the change the draft itself made, cleared by any later one. */
function chipAfterModelChange(current, revision) {
    return current && revision > current.shownAtRevision ? null : current;
}
//# sourceMappingURL=draftUndoChip.js.map