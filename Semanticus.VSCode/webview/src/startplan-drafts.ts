// The words typed into the start plan's editors and not saved yet (join 8 fix round 2, D1), now on the shared draft store (drafts.ts, join 11).
//
// The key is the plan's model FILE (destination path, never the session id), then the line, then the kind of words. A draft ends only on a save the engine
// took, the person's Discard or Use the new one. This file is the start plan's view of the one store: the same persisted record every other editor uses.
import { useMemo } from 'react';
import { createDraftStore as createSharedStore, draftModel, draftStore as sharedStore, useAnyDraft, useHeldDrafts as useSharedHeld, type DraftStorage, type DraftStore } from './drafts';

/** What was typed, and the words it was typed over. A save names `base` as the words it replaces, so the engine can refuse it over newer ones. */
export interface WordsDraft { text: string; base: string }

export { draftKey } from './drafts';
const SCOPE = 'startplan|';
const scopeOf = (lineId: string) => SCOPE + lineId;
const toWords = (d: { value: unknown; base: unknown } | null): WordsDraft | null => d ? { text: String(d.value ?? ''), base: String(d.base ?? '') } : null;

/** The words drafts of the start plan, over a draft store. */
export function wordsDraftsOver(store: DraftStore) {
  return {
    get: (modelKey: string, lineId: string, kind: string): WordsDraft | null => toWords(store.get(modelKey, scopeOf(lineId), kind)),
    /** Hold a draft. Words that are the words they started from (ignoring the space round them) are not a draft, so they hold nothing. */
    set(modelKey: string, lineId: string, kind: string, draft: WordsDraft) {
      if (draft.text.trim() === draft.base.trim()) store.clear(modelKey, scopeOf(lineId), kind);
      else store.set(modelKey, scopeOf(lineId), kind, { value: draft.text, base: draft.base });
    },
    clear: (modelKey: string, lineId: string, kind: string) => store.clear(modelKey, scopeOf(lineId), kind),
    /** R-B: end a draft only when it still holds exactly the words the engine took. */
    clearIfSubmitted: (modelKey: string, lineId: string, kind: string, text: string): boolean => store.clearIfSubmitted(modelKey, scopeOf(lineId), kind, text),
    /** Every words draft of one model: the plan was discarded or built, so its lines are not there to edit any more. */
    clearModel: (modelKey: string) => store.clearModel(modelKey, SCOPE),
    /** The `lineId` + `kind` of each words draft held for one model. */
    held: (modelKey: string): Set<string> => new Set([...store.held(modelKey, SCOPE)].map((id) => { const [scope, kind] = JSON.parse(id) as [string, string]; return heldId(scope.slice(SCOPE.length), kind); })),
    /** The held rows of one model as one string: it changes only when a row gains or loses a draft, never while words are typed into one that has a draft. */
    heldSignature: (modelKey: string): string => JSON.stringify([...store.held(modelKey, SCOPE)].map((id) => { const [scope, kind] = JSON.parse(id) as [string, string]; return heldId(scope.slice(SCOPE.length), kind); }).sort()),
    any: (): boolean => store.any(SCOPE),
    /** True while the given model holds any words draft. Another model's drafts do not count. */
    anyFor: (modelKey: string): boolean => store.any(SCOPE, modelKey),
    /** Keep only the drafts whose line `keep` accepts (the plan changed: a line is gone, so is its draft). */
    prune: (modelKey: string, keep: (lineId: string, kind: string) => boolean): number => store.prune(modelKey, SCOPE, (scope, kind) => keep(scope.slice(SCOPE.length), kind)),
    subscribe: (l: () => void) => store.subscribe(l),
  };
}
/** A words store over its own storage (a test's, or a page that keeps nothing). */
export const createDraftStore = (storage: DraftStorage) => wordsDraftsOver(createSharedStore(storage));
export const heldId = (lineId: string, kind: string): string => JSON.stringify([lineId, kind]);

/** The page's words drafts: the shared store, seen as the start plan's lines. */
export const draftStore = wordsDraftsOver(sharedStore);

/** The rows of one model that hold a draft. The page re-renders when a row gains or loses one, and not on each keystroke typed into a row that has one: the plan has hundreds of rows. */
export function useHeldDrafts(modelKey: string): Set<string> {
  const shared = useSharedHeld(SCOPE, modelKey);
  return useMemo(() => new Set([...shared].map((id) => { const [scope, kind] = JSON.parse(id) as [string, string]; return heldId(scope.slice(SCOPE.length), kind); })), [shared]);
}
/** True while the OPEN model holds typed, unsaved words on its start plan. App.tsx keeps the Plan on screen through an engine outage while this is true.
 *  A draft of another model is not this page's to keep: it used to hold the Plan up with no way to restart the engine (join 11, A1). */
export const useAnyWordsDraft = () => useAnyDraft(SCOPE, draftModel());

/** The one rule for keeping the Plan on screen through an engine outage: a plan to keep (a session), words to keep (the open model's drafts), and a Plan tab. */
export const keepPlanThroughOutage = (o: { hasSession: boolean; hasDraft: boolean; tab: string }): boolean =>
  o.hasSession && o.hasDraft && (o.tab === 'plan' || o.tab === 'optimize' || o.tab === 'spec');
