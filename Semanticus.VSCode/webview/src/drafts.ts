// What a person typed into an editor and has not saved yet. One store for every editor in Studio (join 11, the drafts class).
//
// A draft is the person's work, so it lives ABOVE the editor and outlives it. An editor goes away when another tab opens (every tab except Tests
// unmounts), when another row or object opens, when the page remounts on a restarted engine (App.tsx keys the whole app on the session id) and
// when the webview reloads. None of those is the person saying "throw this away". A draft ends in exactly three ways: a save the engine took,
// the person's own Discard / Cancel, or Use the new one (or Keep mine on a renamed object, which moves it). Nothing else drops it. An object with an id that
// survives a rename (an insight, a rule) is followed by that id; an object known only by its name (a calculation item, a plan line) whose words have no
// object any more is NOT dropped on a guess: orphandrafts.tsx keeps the words and asks.
//
// The key is the model's identity (never the session id: a restart opens the same model under a new session id, and that is the moment the draft
// must still be found): its file path, or for a published model its endpoint and database (modelKeyOfSession), then the SCOPE (an editor id plus the object it is on, for example `calcitem|table:Sales/YTD`), then the FIELD. The
// drafts are held in the webview's persisted state (loadState/saveState, the store every other input here uses), so a remount and a reload
// keep them too. A draft carries the value it was typed over (`base`): a save names that value as the one it replaces, so the engine refuses it
// over a newer one, and an editor that opens on a held draft asks Keep mine / Use the new one when the saved value moved meanwhile.
import { useEffect, useMemo, useState } from 'react';
import { loadState, saveState, postToHost, keptDraftsByTheExtension } from './bridge';
import { engineShaped, setPageModelIdentity } from './pagemodel';

/** One stable string per value (object keys sorted, undefined as null), so two equal values are the same whatever order their keys were built in. */
export function stable(value: unknown): string {
  const walk = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(walk);
    if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v as object).sort().map((k) => [k, walk((v as Record<string, unknown>)[k])]));
    return v === undefined ? null : v;
  };
  return JSON.stringify(walk(value));
}

/** What was typed, and the value it was typed over. Both are plain JSON. `label` is what to call the thing it was typed for, kept so the words can still be named when that thing is not there any more.
 *  `modelLabel` is what to call the MODEL it was typed on (the engine's ModelLabel), kept so the list of words kept for other models can name it. */
export interface HeldDraft { value: unknown; base: unknown; label?: string; modelLabel?: string }

export interface DraftStorage { load: () => Record<string, HeldDraft>; save: (all: Record<string, HeldDraft>) => void }

const STATE_KEY = 'studio.drafts';
// Nothing in a key reads as a separator in a path or an id: the parts are joined as JSON.
export const draftKey = (modelKey: string, scope: string, field: string): string => JSON.stringify([modelKey, scope, field]);
export const parsedKey = (k: string): [string, string, string] | null => {
  try { const p = JSON.parse(k); return Array.isArray(p) && p.length === 3 ? p as [string, string, string] : null; } catch { return null; }
};
/** One id for a scope and a field: what the "held" sets are made of. */
export const heldId = (scope: string, field: string): string => JSON.stringify([scope, field]);

/** The draft store over a storage. `snapshot` changes identity on every change, so a component can subscribe with useSyncExternalStore. */
export function createDraftStore(storage: DraftStorage, labelOfModel?: (modelKey: string) => string | undefined) {
  let all: Record<string, HeldDraft> | null = null;
  const listeners = new Set<() => void>();
  const read = (): Record<string, HeldDraft> => {
    if (all) return all;
    let loaded: Record<string, HeldDraft> = {};
    try { loaded = storage.load() ?? {}; } catch { /* state unavailable: drafts live for this page only */ }
    all = loaded && typeof loaded === 'object' ? { ...loaded } : {};
    return all;
  };
  const commit = (next: Record<string, HeldDraft>) => {
    all = next;
    try { storage.save(next); } catch { /* state unavailable: non-fatal */ }
    for (const l of [...listeners]) l();
  };
  const same = (a: unknown, b: unknown) => stable(a) === stable(b);
  const clear = (modelKey: string, scope: string, field = '') => {
    const k = draftKey(modelKey, scope, field);
    if (!(k in read())) return;
    const { [k]: _gone, ...rest } = read();
    commit(rest);
  };
  /** R-B: end a draft ONLY when the engine accepted exactly the words it holds. `submitted` is what the write carried; a draft that now holds anything else (the person kept typing while
   *  the write was on its way, or an editor reopened on them) is not the write's to end. Returns true when it ended. */
  const clearIfSubmitted = (modelKey: string, scope: string, field: string, submitted: unknown): boolean => {
    const h = read()[draftKey(modelKey, scope, field)];
    if (!h || !same(h.value, submitted)) return false;
    clear(modelKey, scope, field);
    return true;
  };
  /** The scope + field of each draft held for one model (scopes starting with `prefix`, when given). */
  const held = (modelKey: string, prefix = ''): Set<string> => {
    const out = new Set<string>();
    for (const k of Object.keys(read())) { const p = parsedKey(k); if (p && p[0] === modelKey && p[1].startsWith(prefix)) out.add(heldId(p[1], p[2])); }
    return out;
  };
  return {
    get: (modelKey: string, scope: string, field = ''): HeldDraft | null => read()[draftKey(modelKey, scope, field)] ?? null,
    /** Hold a draft. A value that is the value it started from is not a draft, so it holds nothing. */
    set(modelKey: string, scope: string, field: string, draft: HeldDraft) {
      const k = draftKey(modelKey, scope, field), now = read()[k];
      if (same(draft.value, draft.base)) { clear(modelKey, scope, field); return; }
      const label = draft.label ?? now?.label;
      const modelLabel = labelOfModel?.(modelKey) ?? now?.modelLabel;
      if (now && same(now.value, draft.value) && same(now.base, draft.base) && now.label === label && now.modelLabel === modelLabel) return;
      commit({ ...read(), [k]: { value: draft.value, base: draft.base, ...(label ? { label } : {}), ...(modelLabel ? { modelLabel } : {}) } });
    },
    /** Name what a held draft was typed for (a plan line's title, say), so it can still be named when that thing is gone. Holds nothing new. */
    setLabel(modelKey: string, scope: string, field: string, label: string) {
      const k = draftKey(modelKey, scope, field), now = read()[k];
      if (!now || now.label === label) return;
      commit({ ...read(), [k]: { ...now, label } });
    },
    /** Every held draft of one model under `prefix`, one entry per scope, with its fields. */
    scopes(modelKey: string, prefix = ''): Array<{ scope: string; label?: string; fields: Array<{ field: string; draft: HeldDraft }> }> {
      const by = new Map<string, { scope: string; label?: string; fields: Array<{ field: string; draft: HeldDraft }> }>();
      for (const [k, v] of Object.entries(read())) {
        const p = parsedKey(k);
        if (!p || p[0] !== modelKey || !p[1].startsWith(prefix)) continue;
        const e = by.get(p[1]) ?? { scope: p[1], fields: [] };
        e.fields.push({ field: p[2], draft: v });
        e.label ??= v.label;
        by.set(p[1], e);
      }
      return [...by.values()];
    },
    /** Move every draft of one scope to another scope of the same model. The words AND the value they were typed over (`base`) move together and unchanged: a draft is always
     *  typed over what its owner read, so when the target holds something else now, the row asks (Keep mine / Use the new one) and a save names the value that was read.
     *  A move never overwrites: when the target already holds a draft for any of the fields, NOTHING moves and the clashing fields are returned (the words typed on the target are the person's too). */
    moveScope(modelKey: string, from: string, to: string, label?: string): { moved: boolean; conflicts: string[] } {
      const next: Record<string, HeldDraft> = {}, moved: Record<string, HeldDraft> = {}, conflicts: string[] = [];
      const all = read();
      for (const [k, v] of Object.entries(all)) {
        const p = parsedKey(k);
        if (p && p[0] === modelKey && p[1] === from) {
          const target = draftKey(modelKey, to, p[2]);
          if (target in all) conflicts.push(p[2]);
          const l = label ?? v.label;
          moved[target] = { value: v.value, base: v.base, ...(l ? { label: l } : {}), ...(v.modelLabel ? { modelLabel: v.modelLabel } : {}) };
        } else next[k] = v;
      }
      if (conflicts.length || !Object.keys(moved).length) return { moved: false, conflicts };
      commit({ ...next, ...moved });
      return { moved: true, conflicts };
    },
    /** The models OTHER than `exceptKey` that hold words, each with a name a person can read and its drafts. The list behind "Words kept for N other models": it only reads, it never attaches a draft to an editor. */
    otherModels(exceptKey: string): Array<{ modelKey: string; label: string; drafts: Array<{ scope: string; field: string; draft: HeldDraft }> }> {
      const by = new Map<string, { modelKey: string; label: string; drafts: Array<{ scope: string; field: string; draft: HeldDraft }> }>();
      for (const [k, v] of Object.entries(read())) {
        const p = parsedKey(k);
        if (!p || p[0] === exceptKey) continue;
        const e = by.get(p[0]) ?? { modelKey: p[0], label: '', drafts: [] };
        e.drafts.push({ scope: p[1], field: p[2], draft: v });
        e.label ||= v.modelLabel ?? '';
        by.set(p[0], e);
      }
      return [...by.values()].map((e) => ({ ...e, label: e.label || modelNameOfKey(e.modelKey) }));
    },
    /** Drop every field of one scope exactly (not the scopes that merely start with it). */
    clearScope(modelKey: string, scope: string) {
      const rest: Record<string, HeldDraft> = {};
      let any = false;
      for (const [k, v] of Object.entries(read())) { const p = parsedKey(k); if (p && p[0] === modelKey && p[1] === scope) any = true; else rest[k] = v; }
      if (any) commit(rest);
    },
    clear,
    clearIfSubmitted,
    /** Every draft of one model (scopes starting with `prefix`, when given): the thing they were typed for is gone. */
    clearModel(modelKey: string, prefix = '') {
      const rest: Record<string, HeldDraft> = {};
      let any = false;
      for (const [k, v] of Object.entries(read())) { const p = parsedKey(k); if (p && p[0] === modelKey && p[1].startsWith(prefix)) any = true; else rest[k] = v; }
      if (any) commit(rest);
    },
    held,
    /** The held set as one string: it changes only when a scope gains or loses a draft, never while words are typed into one that has a draft. */
    heldSignature: (modelKey: string, prefix = ''): string => JSON.stringify([...held(modelKey, prefix)].sort()),
    /** True while any draft is held, under `prefix` when given, and for `modelKey` when given (otherwise any model). */
    any: (prefix = '', modelKey?: string): boolean => Object.keys(read()).some((k) => { const p = parsedKey(k); return !!p && p[1].startsWith(prefix) && (modelKey === undefined || p[0] === modelKey); }),
    /** Drop the drafts of one model (scopes starting with `prefix`) that `keep` does not want: the thing they were typed for is gone. Returns how many went. */
    prune(modelKey: string, prefix: string, keep: (scope: string, field: string) => boolean): number {
      const rest: Record<string, HeldDraft> = {};
      let gone = 0;
      for (const [k, v] of Object.entries(read())) { const p = parsedKey(k); if (p && p[0] === modelKey && p[1].startsWith(prefix) && !keep(p[1], p[2])) gone++; else rest[k] = v; }
      if (gone) commit(rest);
      return gone;
    },
    snapshot: (): Record<string, HeldDraft> => read(),
    subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; },
  };
}

export type DraftStore = ReturnType<typeof createDraftStore>;

/** A name for a model from its key, for words that were held before the model was named (an older page, or an engine that sends no label). */
export function modelNameOfKey(key: string): string {
  const tail = (p: string) => {
    const parts = p.replace(/[\\/]+$/, '').split(/[\\/]/), last = parts.pop() ?? '';
    // A TMDL model's own folder is always called `definition`: name it by the folder that holds it (Northstar.SemanticModel).
    const named = /^definition$/i.test(last) && parts.length ? parts.pop()! : last;
    return named.replace(/\.[^.]*$/, '') || p;
  };
  if (key.startsWith('desktop:')) return key.slice(8).split('|')[0];   // the window stem; the rest is the instance (endpoint and database), which a person does not read
  if (key.startsWith('live:')) return key.slice(5).split('|')[1] || key.slice(5);
  if (key.startsWith('unsaved:')) return 'an unsaved model';
  if (key.startsWith('name:')) return key.slice(5);
  return tail(key.startsWith('file:') ? key.slice(5) : key) || 'a model';
}

// The whole store is also kept by the extension for the workspace (workspaceState), because closing the Studio panel drops this page's own state and a
// panel cannot ask before it closes. The page hands the host every change (a short throttle, flushed when the page hides), and a Studio page opened afresh
// reads the host's copy back (window.__semanticusHeldDrafts) when its own state is empty. Workflows' Author drafts do the same (workflowheld).
const MIRROR_MS = 250;
let mirrorTimer: ReturnType<typeof setTimeout> | undefined;
let mirrorPending: Record<string, HeldDraft> | null = null;
let mirrorLast = 0;
function flushMirror() {
  if (mirrorTimer) { clearTimeout(mirrorTimer); mirrorTimer = undefined; }
  if (!mirrorPending) return;
  const value = mirrorPending; mirrorPending = null; mirrorLast = Date.now();
  try { postToHost({ type: 'draftsHeld', value }); } catch { /* no host: the page's own state still holds them */ }
}
function mirrorToHost(all: Record<string, HeldDraft>) {
  mirrorPending = all;
  const wait = MIRROR_MS - (Date.now() - mirrorLast);
  if (wait <= 0) flushMirror(); else if (!mirrorTimer) mirrorTimer = setTimeout(flushMirror, wait);
}
if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') { window.addEventListener('pagehide', flushMirror); window.addEventListener('beforeunload', flushMirror); }
const keptByHost = (): Record<string, HeldDraft> | null => { try { return keptDraftsByTheExtension?.<Record<string, HeldDraft>>() ?? null; } catch { return null; } };
const isHeld = (v: unknown): v is HeldDraft => !!v && typeof v === 'object' && 'value' in (v as object) && 'base' in (v as object);
/** KANE'S FALLBACK SWITCH (5 Oct), built and NOT switched on. True: words that come back from the host's copy (VS Code was closed and opened, or the panel was) are attached to their editors again.
 *  False: they are re-filed under a key no editor reads, so the "other models" line lists them read-only, with Copy and Discard. A tab switch, a reload and an engine restart keep the page's own
 *  state and re-attach either way. Flip this one constant (nothing else) to ship the fallback if the identity, base or move class fails a check. */
export const REATTACH_WORDS_AFTER_RESTART = true;
/** The key prefix of words held read-only (the fallback). No model identity starts with it, so no editor ever finds them. */
export const RESTORED_PREFIX = 'restored|';
const asReadOnly = (all: Record<string, HeldDraft>): Record<string, HeldDraft> => Object.fromEntries(Object.entries(all).map(([k, v]) => {
  const p = parsedKey(k);
  if (!p) return [k, v];
  const named = (v.modelLabel || modelNameOfKey(p[0])) + ' (from before VS Code restarted)';
  return [draftKey(p[0].startsWith(RESTORED_PREFIX) ? p[0] : RESTORED_PREFIX + p[0], p[1], p[2]), { ...v, modelLabel: named }];
}));
/** What the page loads: its own state when it holds any, else the host's copy (a panel that was closed and opened again, or VS Code opened again). */
export function loadHeldDrafts(own: Record<string, HeldDraft> | null | undefined, kept: Record<string, HeldDraft> | null | undefined, reattach: boolean = REATTACH_WORDS_AFTER_RESTART): Record<string, HeldDraft> {
  const pick = (m: Record<string, HeldDraft> | null | undefined) => Object.fromEntries(Object.entries(m && typeof m === 'object' ? m : {}).filter(([, v]) => isHeld(v)));
  const mine = pick(own);
  if (Object.keys(mine).length) return mine;
  const fromHost = pick(kept);
  return reattach ? fromHost : asReadOnly(fromHost);
}

let model = '';
let modelLabel = '';
/** The page's one store, in the webview's persisted state, and mirrored to the extension for the workspace. */
export const draftStore: DraftStore = createDraftStore({
  load: () => loadHeldDrafts(loadState<Record<string, HeldDraft> | null>(STATE_KEY, null), keptByHost()),
  save: (all) => { saveState(STATE_KEY, all); mirrorToHost(all); },
}, (key) => (key === model && modelLabel ? modelLabel : undefined));

// The model the page is on, as a FILE identity. App.tsx sets it while it renders the open session, before any editor under it renders, so an editor
// can read it with no prop passed down. Nothing here is held until a model is set: an editor with no model keeps its words in its own state only.
// The session each model key was shown under (when the page said): a model that gets its first Save changes key (`unsaved:` to `file:`) and is still the same session.
const sessionOfKey = new Map<string, string>();
let modelSession = '';
export const setDraftModel = (key: string, label = '', sessionId = '') => {
  model = key; modelLabel = label; modelSession = sessionId;
  if (sessionId) sessionOfKey.set(key, sessionId);
  setPageModelIdentity(key);   // every request the page sends names it (bridge.ts), so the engine can refuse a write for another model
};
/** True when `then` (the model key an editor started on) is still the model the page shows: the same key, or another key of the same session (an unsaved model saved to a file). */
export const stillOnModel = (then: string): boolean => then === model || (!!modelSession && sessionOfKey.get(then) === modelSession);
export const draftModel = (): string => model;
/** The identity of the open model for a held draft: what the ENGINE says (`SessionInfo.ModelIdentity`, LocalEngine.ModelIdentityOf), read verbatim. A client never derives
 *  identity from fields that do not mean it: a snapshot path is new on every open, a session id restarts at s1, a Desktop port and database change on every Desktop start.
 *  Only when the engine sends no field (an engine from before it, version skew) does the older rule apply: a published (live-bound) model is its endpoint and database, a local
 *  file is its path. The same rule as `draftModelKeyOf` in src/draftKey.ts (test/drafts-live-key.test.mjs holds the two equal). */
export const modelKeyOfSession = (s: { modelIdentity?: string; source?: string; liveBound?: boolean; liveEndpoint?: string; liveDatabase?: string; modelName?: string } | null | undefined): string =>
  !s ? ''
    : s.modelIdentity ? s.modelIdentity
    : s.liveBound && s.liveEndpoint ? 'live:' + s.liveEndpoint.trim() + '|' + (s.liveDatabase ?? '')
    : s.source ? s.source
    : s.modelName ? 'name:' + s.modelName : '';

/** R-A, the client's half: the identity a write names, so the ENGINE can refuse it when another model is open (LocalEngine.FenceModelIdentity). Only an identity the engine itself sent has the
 *  engine's shape; a key from the older fallback rule (a path, a name) is not one, and naming it would refuse every write, so it names nothing. */
export const fenceFor = (modelKey: string | undefined | null): string | undefined => engineShaped(modelKey);

/** Drop the open model's drafts under `prefix` whose object is no longer in the list the page just read (`live`: the ids that ARE there; scope = prefix + id, then maybe more). A deleted object takes its draft;
 *  a draft is never shown on, or saved into, another object. Pass a list that was read fresh: a failed read is `null` and drops nothing. `keep` ids always stay (for example 'new'). */
export function pruneGone(prefix: string, live: Iterable<string> | null | undefined, keep: string[] = []): number {
  const m = draftModel();
  if (!m || !live) return 0;
  const ok = new Set<string>([...live, ...keep]);
  return draftStore.prune(m, prefix, (scope) => ok.has(scope.slice(prefix.length)));
}

/** What is held for an editor on the open model, read now (for the editor's first render). `undefined` when nothing is held. */
export function heldDraft<T, B = T>(scope: string, field = ''): { value: T; base: B } | undefined {
  const m = draftModel();
  const h = m ? draftStore.get(m, scope, field) : null;
  return h ? { value: h.value as T, base: h.base as B } : undefined;
}

/** A value read from the store, read again whenever the store changes. (State and an effect, not useSyncExternalStore: the same on every React and in the tiny test runtimes.) */
function useStored<T>(read: () => T, deps: unknown[]): T {
  const [value, setValue] = useState<T>(read);
  useEffect(() => {
    const sync = () => setValue(read());
    sync();
    return draftStore.subscribe(sync);
  }, deps);   // eslint-disable-line react-hooks/exhaustive-deps
  return value;
}

/** The scopes (and fields) of the open model that hold a draft, under `prefix`. The page re-renders when one gains or loses a draft, and not on each keystroke. */
export function useHeldDrafts(prefix = '', modelKey: string = draftModel()): Set<string> {
  const signature = useStored(() => draftStore.heldSignature(modelKey, prefix), [modelKey, prefix]);
  return useMemo(() => new Set<string>(JSON.parse(signature)), [signature]);
}
/** True while the open model holds a draft for this scope. */
export function useHasDraft(scope: string, field = ''): boolean {
  const m = draftModel();
  return useStored(() => (m ? draftStore.get(m, scope, field) !== null : false), [m, scope, field]);
}
/** True while any typed, unsaved draft is held under `prefix`, for `modelKey` when given. A draft of another model is nobody's here: it must never decide what the page shows. */
export function useAnyDraft(prefix = '', modelKey?: string): boolean {
  return useStored(() => draftStore.any(prefix, modelKey), [prefix, modelKey]);
}

/** The models other than the open one that hold words, for the quiet "Words kept for N other models" line. Re-reads when a model gains or loses a draft, not on each keystroke into the open model. */
export function useOtherModels(): Array<{ modelKey: string; label: string; drafts: Array<{ scope: string; field: string; draft: HeldDraft }> }> {
  const m = draftModel();
  const signature = useStored(() => JSON.stringify(draftStore.otherModels(m).map((e) => [e.modelKey, e.label, e.drafts.length])), [m]);
  return useMemo(() => (signature === '[]' ? [] : draftStore.otherModels(m)), [signature, m]);
}

/** The words of a held draft as text a person can paste anywhere: what each was typed for (its label, else its place), then the words. Reads only. */
export function wordsAsText(drafts: Array<{ scope: string; field: string; draft: HeldDraft }>): string {
  const words = (v: unknown): string => (typeof v === 'string' ? v : JSON.stringify(v, null, 2));
  return drafts.map((d) => {
    const what = d.draft.label || [d.scope.split('|')[0], d.field].filter(Boolean).join(' / ');
    return `${what}:\n${words(d.draft.value)}`;
  }).join('\n\n');
}
