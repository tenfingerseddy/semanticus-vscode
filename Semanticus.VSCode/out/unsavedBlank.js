"use strict";
// A new blank model has no file, so when a folder is opened by hand the window it was made in goes away and the new window knows
// nothing of it. The engine keeps its unsaved changes under the model's name (Session.FileRecovery.cs); this list is the host's
// half: which names have kept changes, so a window with no model can offer them back, and Open model lists them.
Object.defineProperty(exports, "__esModule", { value: true });
exports.UNSAVED_BLANK_KEY = void 0;
exports.noteUnsavedBlank = noteUnsavedBlank;
exports.freshUnsavedBlanks = freshUnsavedBlanks;
exports.UNSAVED_BLANK_KEY = 'semanticus.unsavedBlankModels';
const KEEP = 5;
const MAX_AGE_MS = 30 * 24 * 60 * 60 * 1000;
/** The list after one read of the open session. A pathless, unpublished model with unsaved edits is added (newest first). The same name,
 *  once its edits are all undone or it was saved to a file, is taken off, because the engine settled its kept copy. */
function noteUnsavedBlank(list, info, now = Date.now) {
    const name = info.modelName;
    if (!name)
        return list;
    const unsaved = !!(info.hasUnsavedEdits ?? info.hasUnsavedChanges);
    const blank = !info.source && !info.liveBound;
    if (blank && unsaved)
        return [{ name, at: new Date(now()).toISOString() }, ...list.filter((x) => x.name !== name)].slice(0, KEEP);
    // A model with a file or a published model is not this list's business. Only the blank that was this name settles here.
    const settles = (blank && !unsaved) || (!!info.source && !unsaved && !info.liveBound);
    return settles && list.some((x) => x.name === name) ? list.filter((x) => x.name !== name) : list;
}
function freshUnsavedBlanks(list, now = Date.now) {
    return (list ?? []).filter((x) => { const age = now() - Date.parse(x.at); return age >= 0 && age < MAX_AGE_MS; });
}
//# sourceMappingURL=unsavedBlank.js.map