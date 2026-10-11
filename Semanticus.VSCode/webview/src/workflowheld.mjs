// Unsaved Author work is kept when the person leaves Author (UAT-S02-j7-003 and UAT-S11-1-003, lost work).
//
// Leaving by the Workflows link, or by going to another page such as the Plan, unmounted Author and its draft
// with it: no warning and no file. Asking "Leave without saving?" would cover the link but not the page switch,
// and it adds a question to every exit. So nothing asks: every change in Author is written here as it happens,
// the list names what is kept with Open and Throw away, and opening the workflow again brings the work back.
// The store sits on the webview's own state, so it also outlives the Studio panel being hidden, and every change is
// also handed to the extension, which keeps a copy for the workspace: closing the Studio tab drops the webview's own
// state, and the kept work with it, with no warning (a panel cannot ask before it closes). A Studio opened afresh
// reads that copy (heldStateFrom). The verifier on m11/sol-fixes-1 round 2 found the loss live.
//
// Pure: the persistence is injected, so the rules are tested without a webview (test/workflow-held.test.mjs).

/**
 * The key a draft is kept under: a new workflow (never saved) by its file name; a saved one by its file's path, which
 * names the model it belongs to. Keyed by name alone, a same-named workflow in another model (Save As carries every
 * workflow along) wrote its changes over the work kept for this one (verifier on m11/sol-fixes-1 round 2). With no path
 * known, the name, as before.
 */
export const heldKey = (name, creating, path = null) => (creating ? `new:${name}` : path ? `wf:${path}` : `wf:${name}`);

/**
 * The work kept for one saved file: under its path, or under its name alone as an older Studio kept it, when that
 * work was made on this same file. Null when there is none; work kept for another file of the same name is not it.
 */
export function heldForFile(store, name, path) {
  const exact = path ? store.get(heldKey(name, false, path)) : null;
  if (exact) return exact;
  const older = store.get(heldKey(name, false));
  return older && older.draft?.base?.path === path ? older : null;
}

/**
 * What a Studio starts with: the webview's own state when it has any (a reload or a hidden panel keeps it), else the
 * copy the extension kept for this workspace (a Studio tab closed and opened again), else nothing.
 */
export function heldStateFrom(own, kept) {
  if (own && typeof own === 'object') return own;
  return kept && typeof kept === 'object' ? kept : {};
}

/**
 * Where each kept entry belongs, said only where its title cannot tell it from another kept entry. Two same-named
 * workflows, one per model, read the same on the list, and the wrong one was thrown away (verifier on m11/sol-fixes-1
 * round 3). A saved workflow is named by the folder of the model it belongs to (the folder its `.semanticus` sits in,
 * with the folders above it when two share a name); a new one by its file name. A Map from key to that place, or null.
 */
export function heldPlaces(entries) {
  const places = new Map();
  const byTitle = new Map();
  for (const e of entries) {
    const title = e.title || e.name;
    byTitle.set(title, [...(byTitle.get(title) ?? []), e]);
  }
  for (const group of byTitle.values()) {
    const chains = group.map((e) => (e.creating ? null : modelFolders(e.draft?.base?.path)));
    const deepest = Math.max(1, ...chains.map((c) => c?.length ?? 0));
    let depth = 1;
    const label = (c) => c.slice(-depth).join('/');
    for (; depth < deepest; depth++) {
      const labels = chains.filter((c) => c?.length).map(label);
      if (new Set(labels).size === labels.length) break;
    }
    group.forEach((e, i) => places.set(e.key, group.length < 2 ? null
      : e.creating ? { kind: 'new', file: e.name }
      : chains[i]?.length ? { kind: 'model', folder: label(chains[i]) } : null));
  }
  return places;
}

/** The folders down to a workflow file's model: everything before its `/.semanticus/`. */
function modelFolders(path) {
  if (!path) return null;
  const norm = String(path).split(String.fromCharCode(92)).join('/');
  const at = norm.lastIndexOf('/.semanticus/');
  const dir = at >= 0 ? norm.slice(0, at) : norm.slice(0, Math.max(0, norm.lastIndexOf('/')));
  return dir.split('/').filter(Boolean);
}

/** What changed in a draft, as one comparable value: its text and the edits not yet folded into it. */
export const draftShape = (draft) => JSON.stringify([draft?.sourceText ?? '', draft?.operations ?? []]);

/**
 * Is this draft worth keeping? Only real work is: an existing workflow with unsaved edits, or a new workflow that
 * was changed after Author prepared it. A Start blank that was left untouched is not kept (it would only be
 * clutter on the list); `seedShape` is that prepared draft's shape, or null when the draft was itself restored.
 */
export function worthHolding(draft, { creating, dirty, seedShape }) {
  if (!draft || !dirty) return false;
  if (!creating) return true;
  return seedShape == null || draftShape(draft) !== seedShape;
}

export function createHeldDrafts({ load = () => ({}), save = () => {}, now = () => Date.now() } = {}) {
  let held = null;
  const all = () => {
    if (!held) {
      let loaded = null;
      try { loaded = load(); } catch { loaded = null; }
      held = loaded && typeof loaded === 'object' ? { ...loaded } : {};
    }
    return held;
  };
  const persist = () => { try { save({ ...all() }); } catch { /* the in-memory copy still holds it */ } };
  return {
    hold(key, entry) { all()[key] = { ...entry, at: now() }; persist(); },
    drop(key) { if (key in all()) { delete all()[key]; persist(); } },
    get(key) { return all()[key] ?? null; },
    /** Oldest first, so the list reads in the order the work was left. */
    list() { return Object.entries(all()).map(([key, e]) => ({ key, ...e })).sort((a, b) => (a.at ?? 0) - (b.at ?? 0)); },
  };
}
