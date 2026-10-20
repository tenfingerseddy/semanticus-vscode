// A new blank model has no file, so when a folder is opened by hand the window it was made in goes away and the new window knows
// nothing of it. The engine keeps its unsaved changes under the model's name (Session.FileRecovery.cs); this list is the host's
// half: which names have kept changes, so a window with no model can offer them back, and Open model lists them.

export const UNSAVED_BLANK_KEY = 'semanticus.unsavedBlankModels';
export interface UnsavedBlank { name: string; at: string }
export interface SessionRead { modelName?: string; source?: string; liveBound?: boolean; hasUnsavedEdits?: boolean; hasUnsavedChanges?: boolean }

const KEEP = 5;
const MAX_AGE_MS = 30 * 24 * 60 * 60 * 1000;

/** The list after one read of the open session. A pathless, unpublished model with unsaved edits is added (newest first). The same name,
 *  once its edits are all undone or it was saved to a file, is taken off, because the engine settled its kept copy. */
export function noteUnsavedBlank(list: UnsavedBlank[], info: SessionRead, now: () => number = Date.now): UnsavedBlank[] {
    const name = info.modelName;
    if (!name) return list;
    const unsaved = !!(info.hasUnsavedEdits ?? info.hasUnsavedChanges);
    const blank = !info.source && !info.liveBound;
    if (blank && unsaved) return [{ name, at: new Date(now()).toISOString() }, ...list.filter((x) => x.name !== name)].slice(0, KEEP);
    // A model with a file or a published model is not this list's business. Only the blank that was this name settles here.
    const settles = (blank && !unsaved) || (!!info.source && !unsaved && !info.liveBound);
    return settles && list.some((x) => x.name === name) ? list.filter((x) => x.name !== name) : list;
}

export function freshUnsavedBlanks(list: UnsavedBlank[] | undefined, now: () => number = Date.now): UnsavedBlank[] {
    return (list ?? []).filter((x) => { const age = now() - Date.parse(x.at); return age >= 0 && age < MAX_AGE_MS; });
}
