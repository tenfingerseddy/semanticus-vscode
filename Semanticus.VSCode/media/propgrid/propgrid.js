// Properties grid webview logic. Loaded by the extension's PropertyGridProvider AND by the uishot screenshot
// harness (tools/uishot/propgrid.html), so this file is the single source of truth — edit here, not inline.
const vscode = acquireVsCodeApi();
const root = document.getElementById('root');
let state = null;
let filter = '';
// Collapsed category names (persist across object switches in this view). Metadata starts folded: its lineage tag and
// timestamps are bookkeeping, and every other category holds something a person edits.
const collapsed = new Set(['Metadata']);
let lastError = null;          // { name, error } from the most recent failed set
let templates = null;          // format-template catalog (pushed once by the provider; null until then)
let comboOpen = null;          // property name whose format-string picker popup is open
const fx = { open: false, draft: null, base: null, bases: null, moved: false };   // Format expression editor state (draft survives re-renders + failed applies); base = the stored expression the draft started from, moved = the stored one changed under typed words
// Typed, not yet written text per property: name -> { base, draft, moved }. `base` is the stored value the words started from (null for a multi-select
// that shows "(multiple values)", which has no single start), `moved` is true while the stored value is neither the base nor the words. The draft survives a
// re-render (a model change reloads the whole grid), and a write always names its base, which the engine compares inside its own write (Sol join 6, round 2).
const drafts = new Map();
// Words SENT and not yet confirmed (R-B, drafts-sol F7): held key [model, object, name] -> { value, base, bases }. They stay in the held state under their own model and object until a load
// of that object shows the engine took them, so a selection change or a reload while the write is pending cannot lose them, and a refusal that arrives later is routed to its owner.
const inflight = new Map();
let pendingFocusKey = null;    // a host 'focusName' (F2 rename) intent, KEYED to the object it was raised for (the load key);
                               // null = none, true = wildcard (host sent no key). Applied only when THAT object loads.

// HELD DRAFTS (join 11, the drafts class). The words above live in this page's memory for the object on screen. They are also held in the webview's
// persisted state, so selecting another object, a view reload and an engine restart keep them: none of those is the person saying "throw this away".
// A multi-select typed over objects that DIFFER keeps each object's own starting value (`bases`, parallel to the selection's refs): the write names all of them and the engine compares each one.
// The key is the model FILE the host sends (never a session id), the selection's ref key (never the header name: two same-named objects on different
// tables are different targets) and the property; each entry is { value, base }, the words and the stored value they were typed over. A draft ends
// only on a save the engine took (the load shows the words as stored), the person's Cancel or Use the new one, or words typed back to the stored value.
// A write that is on its way is not held: a refusal gives the words back (restoreSent) and they are held again from there.
const HELD_STATE_KEY = 'propgrid.drafts';
const FX_FIELD = 'FormatStringExpression';   // the format editor's draft (fx), held under its property's name
let heldModel = '';    // the open model's file identity, from the host's 'load'; '' = unknown, and then nothing is held past this page
let heldObject = null; // the selection's ref key the drafts on screen belong to
function readHeld() {
  try { const s = vscode.getState && vscode.getState(); const all = s && s[HELD_STATE_KEY]; return all && typeof all === 'object' ? Object.assign({}, all) : {}; }
  catch (_) { return {}; }   // state unavailable: drafts live for this page only
}
function writeHeld(all) {
  try { const s = (vscode.getState && vscode.getState()) || {}; vscode.setState(Object.assign({}, s, { [HELD_STATE_KEY]: all })); }
  catch (_) { /* state unavailable: non-fatal */ }
}
function heldEntry(value, base, bases) { return Array.isArray(bases) ? { value, base, bases } : { value, base }; }
function sameList(a, b) { return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => x === b[i]); }
// The starting value of each selected object, as the host sent it for a row whose objects differ (null when the row does not differ, or the host sent none).
function basesOf(p) { return p && p.varies && Array.isArray(p.values) ? p.values.slice() : null; }
// A held draft is MOVED when what is stored now is not what it was typed over: the one value, or (objects that differed) every object's own value. A draft over differing objects with no
// recorded values cannot prove it is not over unseen work, so it asks too (R-A: an expected null may not skip the check for a draft's write).
function movedOver(d, p) {
  if (d.base !== null) return (p.value || '') !== d.base;
  return !(p.varies && sameList(d.bases, p.values));
}
function heldParts(k) { try { const p = JSON.parse(k); return Array.isArray(p) && p.length === 3 ? p : null; } catch (_) { return null; } }
// Only boxes that hold typed text hold a draft (a checkbox or a list writes on the gesture itself).
function holdsText(p) { return !!p && !p.readOnly && (p.kind === 'string' || p.kind === 'number'); }
function fxTypedNow() { return fx.open && fx.draft != null && String(fx.draft).trim() !== String(fx.base == null ? '' : fx.base).trim(); }
// Write the drafts of the object on screen into the held state (theirs replace whatever was held for it; other objects' and models' stay as they are).
// A draft whose row is not on screen (a load whose property read failed comes with no rows) is kept, never dropped: only the screen decides about a row.
function persistObject() {
  if (!heldModel || heldObject == null) return;
  const all = readHeld();
  const fxOnScreen = !!(state && (state.props || []).some((x) => x.kind === 'formatExpression'));
  for (const k of Object.keys(all)) {
    const p = heldParts(k);
    if (!p || p[0] !== heldModel || p[1] !== heldObject) continue;
    if (p[2] === FX_FIELD && !fxOnScreen) continue;   // the format editor's row is not on screen: its held words stay as they are
    delete all[k];
  }
  // Words sent and not confirmed stay held (any object, any model: the key says which); words typed since win over them.
  for (const [k, v] of inflight) all[k] = heldEntry(v.value, v.base, v.bases);
  for (const [name, d] of drafts) {
    const p = propNamed(name);
    if ((p && !holdsText(p)) || (d.base !== null && d.draft === d.base)) continue;
    all[JSON.stringify([heldModel, heldObject, name])] = heldEntry(d.draft, d.base, d.bases);
  }
  if (fxTypedNow()) all[JSON.stringify([heldModel, heldObject, FX_FIELD])] = heldEntry(fx.draft, fx.base, fx.bases);
  writeHeld(all);
}
// Bring back what is held for the object on screen and not already in this page's memory (the page's own words are newer): each box gets its words,
// and the format editor opens on its own. Runs on every load, so words kept while their row was missing come back with the row.
function restoreHeld(props) {
  if (!heldModel || heldObject == null) return;
  const byName = new Map((props || []).map((p) => [p.name, p]));
  for (const [k, v] of Object.entries(readHeld())) {
    const p = heldParts(k);
    if (!p || p[0] !== heldModel || p[1] !== heldObject || !v || typeof v.value !== 'string') continue;
    if (inflight.has(k)) continue;   // sent and waiting for the engine: the box shows what the engine says, and a refusal gives the words back
    const base = typeof v.base === 'string' ? v.base : null;
    const bases = Array.isArray(v.bases) ? v.bases : null;
    if (p[2] === FX_FIELD) {
      const fp = (props || []).find((x) => x.kind === 'formatExpression');
      if (fp && !fp.readOnly && fx.draft == null) { fx.open = true; fx.draft = v.value; fx.base = base; fx.bases = bases; fx.moved = false; }
    } else if (!drafts.has(p[2])) {
      const row = byName.get(p[2]);
      if (!row || holdsText(row)) drafts.set(p[2], { base, bases, draft: v.value, moved: false });
    }
  }
}

window.addEventListener('message', e => {
  const m = e.data;
  if (m.type === 'load') {
    // Selection identity = the provider's ref key, NOT the header name: two same-named objects on different
    // tables are different targets, and a surviving draft could be Applied to the wrong one. (Name is only a
    // fallback for a host that doesn't send a key.) Another model is another selection too, even under the same ref.
    const selKey = (x) => x.key != null ? x.key : x.name;
    const model = typeof m.model === 'string' ? m.model : '';
    // Compared with the object the drafts on screen belong to, which an 'empty' (an engine going away) does not forget.
    if (heldObject === null || heldObject !== selKey(m) || model !== heldModel) {
      // new selection → fresh editors, then whatever is held for THIS object on THIS model (the previous object's drafts stay held under its own key)
      fx.open = false; fx.draft = null; fx.base = null; fx.bases = null; fx.moved = false; comboOpen = null; drafts.clear();
      heldModel = model; heldObject = selKey(m);
    }
    restoreHeld(m.props);
    state = m; lastError = null;
    reconcileDrafts();
    confirmSent(m.props);
    // A draft that matches the canonical value has landed (or was never changed) — drop it so later edits
    // by the other driver aren't shadowed by a stale draft.
    if (fx.draft != null) { const p = (state.props || []).find(x => x.kind === 'formatExpression'); if (p && p.value === fx.draft.trim()) { fx.draft = null; fx.moved = false; } }
    // The stored expression changed (the assistant set it) while words are typed in the editor: keep the words, say so, and
    // let the person choose. Apply would otherwise write the old copy over the new expression without a word (UAT stale-copy class).
    { const p = (state.props || []).find(x => x.kind === 'formatExpression');
      fx.moved = !!(fx.open && fx.draft != null && p && !p.varies && fx.base != null && (p.value || '') !== fx.base && (p.value || '') !== fx.draft.trim())
        || !!(fx.open && fx.draft != null && p && p.varies && fx.base === null && fx.bases && !sameList(fx.bases, p.values)); }
    render();
    tryFocusName();   // a 'focusName' that arrived before this object loaded lands now
  }
  else if (m.type === 'focusName') { pendingFocusKey = (m.key != null ? m.key : true); tryFocusName(); }   // F2 rename: caret into the Name row (keyed to its object)
  else if (m.type === 'setError') {
    // A refusal belongs to the object it was sent for. The host names it (model and selection key); an older host does not, and then it is the object on screen.
    const named = typeof m.model === 'string' && typeof m.key === 'string';
    const owner = JSON.stringify([named ? m.model : heldModel, named ? m.key : heldObject, m.name]);
    const sent = inflight.get(owner);
    inflight.delete(owner);
    if (!named || (m.model === heldModel && m.key === heldObject)) { lastError = { name: m.name, error: m.error }; restoreSent(m.name, sent); render(); }
    else persistObject();   // not on screen: the words stay held under their own object, and come back with it
  }
  else if (m.type === 'setOk') {
    // The engine took a write: its words are not the person's to keep any more, whichever object is on screen. Only THOSE words end: words typed since stay.
    const k = JSON.stringify([typeof m.model === 'string' ? m.model : heldModel, typeof m.key === 'string' ? m.key : heldObject, m.name]);
    inflight.delete(k);
    const all = readHeld(), e = all[k];
    if (e && typeof e.value === 'string' && e.value.trim() === String(m.value == null ? '' : m.value).trim()) { delete all[k]; writeHeld(all); }
    if (state) render();
  }
  else if (m.type === 'formatTemplates') { templates = m.templates || []; if (state) render(); }
  else if (m.type === 'applyFocused') {
    const notice = applyFocusedField();
    // Answer AFTER any 'set' above was posted: messages arrive in order, so the host knows the write is on its way
    // when it sees this. A Save asked for the field the person was just typing in and waits for that write.
    // A draft this gesture did not write is said out loud (the host shows the line), never dropped without a word.
    if (m.id != null) vscode.postMessage({ type: 'applied', id: m.id, notice: notice || undefined });
  }
  else if (m.type === 'empty') { state = null; root.innerHTML = '<div class="empty">Open a model to inspect its properties. Select model objects to edit them; clear the selection to return to model settings.</div>'; }
});
// A Save (or the apply key) commits ONE thing: the field the person is in. An open format editor counts only while its own box has the focus, the
// person typed a change into it, and its stored expression has not moved under the words (then the person chooses with Apply or Use the new one).
// Applying it any other way wrote an untouched empty multi-select over every selected measure's dynamic format, or a stale draft over a newer
// expression, from a disk-save gesture (Sol join 6, C3).
const FX_NOT_SAVED = 'Your format draft was not saved; open it and press Apply.';
const MOVED_NOT_SAVED = 'A property change was not saved because its value changed first. Open Properties and choose Keep mine or Use the new one.';
// Returns a plain line when words the person typed were NOT written by this gesture, else null.
function applyFocusedField() {
  if (!state) return null;
  const el = document.activeElement;
  // The format editor's words count as typed when they differ from where they started. Focus elsewhere means this gesture does not write them.
  const fxTyped = fx.open && fx.draft != null && String(fx.draft).trim() !== String(fx.base == null ? '' : fx.base).trim();
  if (fx.open && el && el.classList && el.classList.contains('fx-ta')) {
    const fxApply = root.querySelector('[data-fx-apply]');
    const typed = String(el.value == null ? '' : el.value).trim();
    if (fxApply && !fx.moved && typed !== String(fx.base == null ? '' : fx.base).trim()) { fxApply.click(); return null; }
    return fxTyped || typed !== String(fx.base == null ? '' : fx.base).trim() ? FX_NOT_SAVED : null;
  }
  let notice = fxTyped ? FX_NOT_SAVED : null;
  if (!el || !el.getAttribute || !el.getAttribute('data-prop')) return notice;
  const name = el.getAttribute('data-prop');
  const value = el.type === 'checkbox' ? String(el.checked) : el.value;
  const canonical = (state.props || []).find((p) => p.name === name);
  if (canonical && !canonical.varies && el.type !== 'checkbox' && value === (canonical.value || '')) return notice;
  if (!commit(name, value)) notice = notice || MOVED_NOT_SAVED;
  return notice;
}
// Draft bookkeeping. noteDraft records words as they are typed; reconcileDrafts runs on every load and decides, per draft, whether the words landed (the stored
// value is now the words), are still in step (the stored value is the base) or must be asked about (the stored value moved under them).
function propNamed(name) { return (state && state.props || []).find((p) => p.name === name); }
function noteDraft(el) {
  const name = el.getAttribute('data-prop');
  const p = propNamed(name);
  if (!p) return;
  const d = drafts.get(name) || { base: p.varies ? null : (p.value || ''), bases: basesOf(p), draft: '', moved: false };
  d.draft = el.value;
  if (d.base !== null && d.draft === d.base) drafts.delete(name); else drafts.set(name, d);
  persistObject();
  showDraftMark(name, drafts.has(name));
}
// The quiet "Draft not saved" mark on a row that holds typed words. Typing does not re-render the grid (the caret would jump), so the mark's slot is
// filled in place as the first word is typed or the last one is taken back.
const DRAFT_MARK = '<span class="draft-mark" role="status" title="You typed here and have not saved it. It is kept if you select another object.">Draft not saved</span>';
function draftSlotHtml(name, show) { return '<div class="draft-slot" data-draft-for="' + esc(name) + '">' + (show ? DRAFT_MARK : '') + '</div>'; }
function showDraftMark(name, show) {
  const slot = root.querySelector('[data-draft-for="' + name + '"]');
  if (slot && (slot.innerHTML !== '') !== show) slot.innerHTML = show ? DRAFT_MARK : '';
}
function reconcileDrafts() {
  for (const [name, d] of Array.from(drafts.entries())) {
    const p = propNamed(name);
    if (!p) continue;   // its row is not in this load (a failed property read): the words are kept, not shown, until it comes back
    if ((p.value || '') === d.draft) { drafts.delete(name); continue; }
    d.moved = movedOver(d, p);
  }
}
// A load that shows the engine took the words ends the hold on them (R-B): only then are they not the person's to keep.
function confirmSent(props) {
  if (!heldModel || heldObject == null) return;
  let any = false;
  for (const k of Array.from(inflight.keys())) {
    const p = heldParts(k);
    if (!p || p[0] !== heldModel || p[1] !== heldObject) continue;
    const row = (props || []).find((x) => x.name === p[2]);
    if (!row) continue;
    const sent = inflight.get(k);
    if (String(row.value || '').trim() === String(sent.value || '').trim() && !row.varies) { inflight.delete(k); any = true; }
  }
  if (any) persistObject();
}
function restoreSent(name, sent) {
  const s = sent || inflight.get(JSON.stringify([heldModel, heldObject, name]));
  inflight.delete(JSON.stringify([heldModel, heldObject, name]));
  const p = propNamed(name);
  if (!s || !p || p.kind === 'formatExpression' || drafts.has(name) || (p.value || '') === s.value) { persistObject(); return; }   // the format editor keeps its own draft (fx)
  const d = { base: s.base, bases: s.bases, draft: s.value, moved: false };
  d.moved = movedOver(d, p);
  drafts.set(name, d);
  persistObject();
}
// One write. It names the value the words started from; a draft whose stored value moved is not written until the person chose. Returns false when it held back.
function commit(name, value, expectedOverride, basesOverride) {
  const d = drafts.get(name);
  if (d && d.moved && expectedOverride === undefined) { lastError = null; render(); return false; }
  const p = propNamed(name);
  const base = expectedOverride !== undefined ? expectedOverride : (d ? d.base : (p && !p.varies ? (p.value || '') : null));
  // Objects that differed have no one starting value: every object's own goes with the write, and the engine compares each inside its atomic batch.
  const bases = basesOverride !== undefined ? basesOverride : (d ? d.bases || null : basesOf(p));
  if (heldModel && heldObject != null) inflight.set(JSON.stringify([heldModel, heldObject, name]), { value, base, bases });
  drafts.delete(name);
  persistObject();   // on its way to the engine: held under its own model and object until a load shows the engine took it (a refusal gives it back through restoreSent)
  showDraftMark(name, false);
  lastError = null;
  const msg = { type: 'set', name, value, expected: base };
  if (bases) msg.expectedAll = bases;
  msg.model = heldModel;
  msg.key = heldObject;   // the selection these words were typed on: the host refuses the write if the grid is on another by the time it arrives
  vscode.postMessage(msg);
  return true;
}
// What is stored now, as text a person can read: the one value, or (objects that differ) each object's own, in the order of the selection.
function storedHtml(p) {
  if (p.varies) return Array.isArray(p.values) ? p.values.map((v, i) => (i + 1) + ': ' + esc(v === '' ? '(empty)' : v)).join('<br>') : '(the selected objects hold different values)';
  return esc(p.value === '' || p.value == null ? '(empty)' : p.value);
}
function esc(s){ return String(s==null?'':s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

// F2 rename (host 'focusName'): put the caret in the Name row with its text selected. The rename intent
// outranks a filter or a collapsed category that hides the row — both are cleared if they're in the way.
// A selection with no editable Name row (read-only, or none at all) leaves focus where it is, silently.
//
// The intent is KEYED to the object it was raised for: F2 on A, then selecting B before A loads must NOT
// let B consume A's intent (focus steal + a rename aimed at the wrong object). A load whose key differs
// from the pending key IS that selection change, so the intent is discarded, not applied.
function tryFocusName() {
  if (pendingFocusKey == null || !state) return;   // no intent, or nothing loaded yet — keep waiting
  const cur = state.key != null ? state.key : state.name;
  if (pendingFocusKey !== true && pendingFocusKey !== cur) { pendingFocusKey = null; return; }   // moved to a different object → drop it
  let el = root.querySelector('input[type="text"][data-prop="Name"]');
  if (!el && (filter !== '' || collapsed.size > 0)) { filter = ''; collapsed.clear(); render(); el = root.querySelector('input[type="text"][data-prop="Name"]'); }
  pendingFocusKey = null;   // one-shot either way — a stale intent must never grab focus later
  if (!el) return;
  el.scrollIntoView({ block: 'nearest' });
  el.focus();
  el.select();
}
// Multiline editor only for genuinely multi-line content — Description, DAX/expression bodies, or any value that
// actually contains a newline. NOT length-based: long single-line values (Format String, lineage tags, source
// queries) stay as text inputs (they scroll horizontally) instead of overflowing a short textarea and showing
// scrollbar arrows that read like number-spinners.
function isMultiline(p){ return p.kind === 'string' && (p.name === 'Description' || /Expression/.test(p.name) || (p.value||'').indexOf('\n') >= 0); }
function oneLine(s){ return String(s||'').replace(/\s+/g, ' ').trim(); }
function enumSelectHtml(p, inv, dp) {
  const opts = (p.options || []).slice();
  if (!p.varies && p.value && opts.indexOf(p.value) < 0) opts.unshift(p.value);
  const clearable = p.name === 'DataCategory' || opts.indexOf('') >= 0;
  let h = '<select class="' + inv + '" ' + dp + '>';
  if (p.varies) h += '<option value="" disabled selected>(multiple)</option>';
  else if (clearable) h += '<option value=""' + (p.value === '' ? ' selected' : '') + '>None</option>';
  for (const o of opts) {
    if (o === '') continue;
    h += '<option value="' + esc(o) + '"' + (!p.varies && o === p.value ? ' selected' : '') + '>' + esc(o) + '</option>';
  }
  return h + '</select>';
}

// WORDS KEPT FOR OTHER OBJECTS (join 11 solui, round 3). Typed words are held under the object they were typed on. When the grid shows another object (the person selected one, the assistant
// renamed or deleted this one, a read failed, another model opened) those words are not in any box. One quiet line says they exist and where, collapsed by default (another object's
// words are never drawn into this page until the person opens the line), with Copy and Discard. It only reads: it never writes the words to an object.
// Every held key is in a box, in flight (sent, not yet answered), or counted here.
let keptOpen = false;
let keptRows = [];   // the rows drawn now: { keys, text }, so a button names a row by its index and never by a key in the markup
function keptWords() {
  const rows = new Map();   // "[model, object]" -> { model, object, keys: [key], items: [{ name, value }] }
  for (const [k, v] of Object.entries(readHeld())) {
    const p = heldParts(k);
    if (!p || !v || typeof v.value !== 'string' || !v.value.trim() || inflight.has(k)) continue;
    // The shown object's own words are in its boxes (or its format editor) while their row is on screen. A row that is not on screen (a failed read, a renamed object) is counted.
    if (p[0] === heldModel && p[1] === heldObject && state && (p[2] === FX_FIELD ? (state.props || []).some((x) => x.kind === 'formatExpression') : !!propNamed(p[2]))) continue;
    const id = JSON.stringify([p[0], p[1]]);
    const row = rows.get(id) || { model: p[0], object: p[1], keys: [], items: [] };
    row.keys.push(k); row.items.push({ name: p[2], value: v.value });
    rows.set(id, row);
  }
  return Array.from(rows.values());
}
// A ref key (`["measure:Sales/Total Sales"]`) as words a person reads: "Sales / Total Sales".
function objectWords(key) {
  try { const refs = JSON.parse(key); if (Array.isArray(refs)) return refs.map((r) => String(r).replace(/^[a-z]+:/i, '').split('/').join(' / ')).join(', '); } catch (_) { /* a host that sends the name */ }
  return String(key);
}
function modelWords(model) { const parts = String(model || '').replace(/^file:/, '').split(/[\\/]/); return (parts.pop() || parts.pop() || '').replace(/\.[^.]*$/, ''); }
function keptHtml() {
  keptRows = keptWords();
  if (keptRows.length === 0) return '';
  const n = keptRows.length;
  let h = '<div class="kept" data-kept role="status"><button type="button" class="kept-toggle" data-kept-toggle aria-expanded="' + keptOpen + '">Words kept for ' + (n === 1 ? '1 other object' : n + ' other objects') + '</button>';
  if (keptOpen) keptRows.forEach((r, i) => {
    const where = modelWords(r.model) && r.model !== heldModel ? ' <span class="kept-model">(' + esc(modelWords(r.model)) + ')</span>' : '';
    h += '<div class="kept-row" data-kept-row="' + i + '"><div><b>' + esc(objectWords(r.object)) + '</b>' + where + '. Nothing here is saved.</div>'
      + r.items.map((it) => '<div class="kept-words"><span class="kept-prop">' + esc(it.name) + ':</span> <code>' + esc(it.value.length > 160 ? it.value.slice(0, 160) + '...' : it.value) + '</code></div>').join('')
      + '<div class="kept-acts"><button type="button" class="btn" data-kept-copy="' + i + '" title="Copy these words so you can paste them where they belong.">Copy</button> <button type="button" class="btn" data-kept-discard="' + i + '" title="Drop these words for good.">Discard</button></div></div>';
  });
  return h + '</div>';
}
function keptText(r) { return r.items.map((it) => objectWords(r.object) + ' / ' + it.name + ':\n' + it.value).join('\n\n'); }
function wireKept() {
  const t = root.querySelector('[data-kept-toggle]');
  if (t) t.addEventListener('click', () => { keptOpen = !keptOpen; render(); });
  root.querySelectorAll('[data-kept-copy]').forEach((b) => b.addEventListener('click', () => {
    const r = keptRows[Number(b.getAttribute('data-kept-copy'))];
    if (!r) return;
    try { const c = navigator.clipboard; if (c && c.writeText) c.writeText(keptText(r)); } catch (_) { /* no clipboard: the words stay listed */ }
  }));
  root.querySelectorAll('[data-kept-discard]').forEach((b) => b.addEventListener('click', () => {
    const r = keptRows[Number(b.getAttribute('data-kept-discard'))];
    if (!r) return;
    const all = readHeld();
    for (const k of r.keys) if (!inflight.has(k)) delete all[k];
    writeHeld(all);
    render();
  }));
}

function render() {
  if (!state) return;
  const f = filter.trim().toLowerCase();
  const groups = {};
  for (const p of state.props) {
    if (f && !((p.displayName||'').toLowerCase().includes(f) || (p.name||'').toLowerCase().includes(f))) continue;
    (groups[p.category] = groups[p.category] || []).push(p);
  }
  let h = '<div class="hdr">' + esc(state.name) + (state.multi ? ' <span class="multi">(multi-edit)</span>' : '') + '</div>';
  h += '<div class="search"><input id="f" type="search" placeholder="Filter properties…" value="' + esc(filter) + '"></div>';
  const cats = Object.keys(groups);
  if (cats.length === 0) h += '<div class="empty">No properties match “' + esc(filter) + '”.</div>';
  for (const cat of cats) {
    // A filter that matches a Metadata row opens Metadata, so the new default never hides a match.
    const isCol = collapsed.has(cat) && !(f !== '' && cat === 'Metadata');
    h += '<div class="cat' + (isCol ? ' collapsed' : '') + '" data-cat="' + esc(cat) + '"><span class="twist">▾</span>' + esc(cat) + '</div>';
    if (!isCol) for (const p of groups[cat]) h += rowHtml(p);
  }
  h += keptHtml();
  // A re-render replaces every box. The one the person is typing in keeps its focus and caret, so words typed there are not cut off by a model change.
  const act = document.activeElement;
  const held = act && act.getAttribute && act.getAttribute('data-prop') && (act.tagName === 'INPUT' || act.tagName === 'TEXTAREA') && act.type !== 'checkbox'
    ? { name: act.getAttribute('data-prop'), start: act.selectionStart, end: act.selectionEnd } : null;
  root.innerHTML = h;
  wire();
  wireKept();
  persistObject();   // every gesture that re-renders (a load, a choice, Cancel, Apply, a refusal) leaves the held state matching the screen
  if (held && drafts.has(held.name)) {
    const again = root.querySelector('[data-prop="' + held.name + '"]');
    if (again && again.focus) { again.focus(); try { if (held.start != null) again.setSelectionRange(held.start, held.end); } catch (_) { /* a type with no caret */ } }
  }
}

function rowHtml(p) {
  const dr = drafts.get(p.name);
  if (dr && !(p.kind === 'formatExpression' || p.readOnly || p.kind === 'bool' || p.kind === 'enum')) p = Object.assign({}, p, { value: dr.draft });   // the person's unwritten words, not the stored value
  const dp = 'data-prop="' + esc(p.name) + '"';
  const ph = p.varies ? ' placeholder="(multiple values)"' : '';
  const errHere = lastError && lastError.name === p.name;
  const inv = errHere ? ' invalid' : '';
  let editor, enterHint = '';
  if (p.kind === 'formatExpression') editor = fxHtml(p);   // before the readOnly branch: a locked row states WHY
        else if (p.readOnly) editor = '<span class="ro" title="' + esc(p.value) + '">' + (p.varies ? '<em>(multiple)</em>' : esc(p.value) || 'Not set') + '</span>';
  else if (p.kind === 'bool') editor = '<input type="checkbox" class="' + inv + '" ' + dp + (p.value === 'True' ? ' checked' : '') + (p.varies ? ' indeterminate' : '') + '>';
  else if (p.kind === 'enum') editor = enumSelectHtml(p, inv, dp);
  else if (p.kind === 'number') editor = '<input type="number" class="' + inv + '" ' + dp + ph + ' value="' + esc(p.value) + '">';
  else if (isMultiline(p)) {
    const commitEnter = p.name === 'Description';
    const rows = Math.min(commitEnter ? 3 : 8, Math.max(2, (p.value||'').split('\n').length));
    editor = '<textarea class="' + inv + '" ' + dp + ph + (commitEnter ? ' data-commit-enter' : '') + ' rows="' + rows + '">' + esc(p.value) + '</textarea>';
    if (commitEnter) enterHint = '<div class="hint enter-hint">Enter applies. Shift+Enter adds a line.</div>';   // shown only while the box has focus (propgrid.css)
  }
  else {
    // Plain text input — plus the two prefill affordances: engine-supplied suggestions (e.g. display folders
    // already in use) as a native autocomplete, and the curated format-string picker on Format String rows.
    // Free text always stays available.
    const dl = (p.suggestions && p.suggestions.length) ? ' list="dl-' + esc(p.name) + '"' : '';
    editor = '<input type="text" class="' + inv + '" ' + dp + ph + dl + ' value="' + esc(p.value) + '">';
    if (dl) editor += '<datalist id="dl-' + esc(p.name) + '">' + p.suggestions.map(s => '<option value="' + esc(s) + '">').join('') + '</datalist>';
    if (p.name === 'FormatString' && templates && templates.length) {
      editor = '<div class="combo">' + editor
        + '<button class="combo-btn" data-combo="' + esc(p.name) + '" title="Pick a common format">▾</button>'
        + (comboOpen === p.name ? comboPopHtml() : '') + '</div>';
    }
  }
  const err = errHere ? '<div class="err">' + esc(lastError.error) + '</div>' : '';
  // The newer value is SHOWN beside the words (F11): Keep mine only puts the words back as a draft to be saved on purpose, over the value shown here, and never writes by itself.
  const movedNote = dr && dr.moved ? '<div class="err moved" role="alert">This value changed while you were typing. Your words are still here and nothing was written.'
    + '<div class="moved-theirs" data-moved-theirs="' + esc(p.name) + '"><b>The new value:</b> <code>' + storedHtml(propNamed(p.name)) + '</code></div>'
    + '<button class="btn" data-keep="' + esc(p.name) + '" title="Keep your words. They stay here to be saved, over the value shown above.">Keep mine</button> <button class="btn" data-usenew="' + esc(p.name) + '">Use the new one</button></div>' : '';
  const hint = (p.hint && p.kind !== 'formatExpression') ? '<div class="hint">' + esc(p.hint) + '</div>' : '';
  const slot = p.kind === 'formatExpression' ? (p.readOnly ? '' : draftSlotHtml(p.name, fxTypedNow()))
    : holdsText(p) ? draftSlotHtml(p.name, !!dr) : '';
  return '<div class="rowx"><label title="' + esc(p.description || p.name) + '">' + esc(p.displayName) + '</label>' + editor + slot + enterHint + movedNote + err + hint + '</div>';
}

// The Format String picker popup: the catalog's pinned "common" entries grouped by category, each with its
// documented example output as a live preview. Picking one writes through the same edit path as typing it.
function comboPopHtml() {
  const items = templates.filter(t => t.kind === 'static' && t.common);
  let h = '<div class="combo-pop">';
  let cat = '';
  for (const t of items) {
    if (t.category !== cat) { cat = t.category; h += '<div class="combo-cat">' + esc(cat) + '</div>'; }
    h += '<div class="combo-item" data-fmt="' + esc(t.formatString) + '" title="' + esc(t.formatString + (t.note ? '\n' + t.note : '')) + '">'
       + '<span>' + esc(t.name) + '</span><code>' + esc(t.exampleOutput) + '</code></div>';
  }
  h += '<div class="combo-note">Or type your own format in the box.</div></div>';
  return h;
}

// The measure Format expression row: a status line (does a dynamic format exist?) with an Edit affordance that
// expands into a lightweight DAX editor — monospace textarea, insert-from-template picker (the catalog's dynamic
// entries), Apply/Cancel. Below compatibility level 1601 the row states the requirement instead of a dead control.
function fxHtml(p) {
  if (p.readOnly) return '<span class="ro fx-locked">' + esc(p.hint || 'Not available for this model.') + '</span>';
  const has = !p.varies && (p.value || '').trim().length > 0;
  const status = p.varies ? '<em class="fx-none">(multiple values)</em>'
    : has ? '<code class="fx-prev" title="' + esc(p.value) + '">' + esc(oneLine(p.value)) + '</code>'
    : '<span class="fx-none">None (the static format applies)</span>';
  let h = '<div class="fx-row">' + status + '<button class="btn" data-fx-edit>' + (has ? 'Edit' : 'Add') + '</button></div>';
  if (fx.open) {
    const dyn = (templates || []).filter(t => t.kind === 'dynamic');
    let pick = '';
    if (dyn.length) {
      let cat = '', opts = '<option value="">Insert a template…</option>';
      for (const t of dyn) {
        if (t.category !== cat) { if (cat) opts += '</optgroup>'; cat = t.category; opts += '<optgroup label="' + esc(cat) + '">'; }
        opts += '<option value="' + esc(t.formatString) + '">' + esc(t.name) + '</option>';
      }
      if (cat) opts += '</optgroup>';
      pick = '<select class="fx-tpl">' + opts + '</select>';
    }
    const val = fx.draft != null ? fx.draft : (p.varies ? '' : p.value || '');
    h += '<div class="fx-panel">' + pick
      + '<textarea class="fx-ta" spellcheck="false" rows="' + Math.min(10, Math.max(3, val.split('\n').length + 1)) + '">' + esc(val) + '</textarea>'
      + '<div class="fx-msg">' + (fx.moved ? 'This expression changed while you were editing it. Your words are still here. Apply writes them over the new one. The new one is: <code>' + storedHtml(p) + '</code>' : '') + '</div>'
      + '<div class="fx-actions"><button class="btn primary" data-fx-apply>Apply</button>' + (fx.moved ? '<button class="btn" data-fx-usenew>Use the new one</button>' : '') + '<button class="btn" data-fx-cancel>Cancel</button>'
      + '<span class="hint">Apply with an empty box to go back to the static format string.</span></div>'
      + '</div>';
  }
  return h;
}

// Cheap structural check before an apply — balanced quotes/parentheses OUTSIDE string literals and quoted names
// ("" and '' are the DAX escapes). Catches the obvious paste accidents without pretending to be a DAX parser.
function formatStringProblem(s) {
  if (!s) return null;
  let inQuote = false, brackets = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inQuote) {
      if (c !== '"') continue;
      if (s[i + 1] === '"') { i++; continue; }
      inQuote = false;
      continue;
    }
    if (c === '"') { inQuote = true; continue; }
    if (c === '[') { brackets++; continue; }
    if (c === ']') {
      if (brackets === 0) return "This format string has a ']' with no matching '['.";
      brackets--;
    }
  }
  if (inQuote) return 'This format string has an unclosed quote.';
  if (brackets > 0) return "This format string has an unclosed '['. Add the missing ']'.";
  return null;
}

function daxBalanceProblem(s) {
  let depth = 0, inStr = false, inName = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inStr) { if (c === '"') { if (s[i + 1] === '"') i++; else inStr = false; } continue; }
    if (inName) { if (c === "'") { if (s[i + 1] === "'") i++; else inName = false; } continue; }
    if (c === '"') inStr = true;
    else if (c === "'") inName = true;
    else if (c === '(') depth++;
    else if (c === ')') { depth--; if (depth < 0) return 'There is a ")" without a matching "(".'; }
  }
  if (inStr) return 'A double-quoted text is not closed.';
  if (inName) return 'A single-quoted name is not closed.';
  if (depth > 0) return 'A "(" is not closed.';
  return null;
}

// The same fit the Studio's boxes use (webview/src/hooks.ts, `fitToText`), for the same reasons. Nothing makes the
// two copies one, and the proofs (in tools/uishot/) do not all run against both. Both copies:
// space-autogrow-drive.mjs (typing keeps the page still), space-r3-drive.mjs (a box never shorter than its text) and
// space-final-drive.mjs cases A and B (Backspace, the pause, a cut). This copy only: space-r3v-drive.mjs case F (the width unrounded) and space-r4-drive.mjs case E (the rows floor,
// no scrollbar below the cap). The Studio's only: space-r3v-drive.mjs cases C and E, space-r4-drive.mjs cases A to D,
// and space-r4v-drive.mjs case A, the re-fit at a width the box was never fitted at, which this copy has too but no
// proof reaches, because the harness panel already scrolls before any of its boxes is fitted.
//
// THE BOX IS NEVER COLLAPSED TO BE MEASURED. 'height: auto' used to be how its content height was read. It moves the
// panel: for that instant the box is short, every scrolling ancestor loses that much scroll range and the browser
// clamps its position, and restoring the height does not restore it, so typing at the end of a long description
// jumped the panel back to the top on every key. And in a real window it cuts the text off: while the box is short
// the panel around it stops overflowing and gives back its scrollbar's width, the text is measured that much wider
// than it will be, and the height that comes back is too small for it. So a box only grows by its own answer
// (scrollHeight past clientHeight is exactly what it hides), and a hidden twin at the box's unrounded width, its rows
// and its zoom is measured only when the box may need to shrink; the box's own answer still has the last word.
const WRAP_STYLES = ['fontFamily','fontSize','fontWeight','fontStyle','fontVariant','fontStretch','lineHeight',
  'letterSpacing','wordSpacing','textTransform','textIndent','whiteSpace','wordBreak','overflowWrap','tabSize',
  'direction','paddingTop','paddingRight','paddingBottom','paddingLeft'];
let growTwin = null;
// The text's own width, unrounded: clientWidth is a whole pixel, and a twin half a pixel wider than the text wraps
// into fewer lines (a 496-character description got 310 px for 328 px of text at a 203.688 px text area).
function textWidth(ta, cs){
  const w = parseFloat(cs.width);
  if (cs.boxSizing !== 'border-box') return w;
  return w - (parseFloat(cs.borderLeftWidth) || 0) - (parseFloat(cs.borderRightWidth) || 0) - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
}
function zoomAbove(el){
  let z = 1;
  for (let a = el; a && a !== document.body; a = a.parentElement) { const v = parseFloat(getComputedStyle(a).zoom); if (Number.isFinite(v) && v > 0) z *= v; }
  return z;
}
function neededHeight(ta, cs){
  const inner = textWidth(ta, cs);
  if (!(inner > 0)) return 0;
  if (!growTwin) { growTwin = document.createElement('textarea'); growTwin.setAttribute('aria-hidden', 'true'); growTwin.tabIndex = -1; growTwin.readOnly = true; }
  growTwin.wrap = ta.wrap || 'soft';
  growTwin.rows = ta.rows || 2;
  growTwin.style.cssText = 'position:absolute;top:0;left:-99999px;visibility:hidden;overflow:hidden;resize:none;border:0;height:auto;min-height:0;max-height:none;box-sizing:content-box;';
  for (const p of WRAP_STYLES) growTwin.style[p] = cs[p];
  growTwin.style.zoom = String(zoomAbove(ta));
  growTwin.style.width = inner + 'px';
  growTwin.value = ta.value;
  document.body.appendChild(growTwin);
  const text = growTwin.scrollHeight;
  growTwin.remove();
  if (cs.boxSizing === 'border-box') return text + (parseFloat(cs.borderTopWidth) || 0) + (parseFloat(cs.borderBottomWidth) || 0);
  return text - (parseFloat(cs.paddingTop) || 0) - (parseFloat(cs.paddingBottom) || 0);
}
function growToText(ta, cs){
  for (let pass = 0; pass < 4; pass++) {
    const hidden = ta.scrollHeight - ta.clientHeight;
    if (hidden <= 0) return;
    const was = ta.offsetHeight;
    ta.style.height = (parseFloat(cs.height) + hidden) + 'px';
    if (ta.offsetHeight <= was) return;   // at its cap (60vh), where scrolling itself is what the cap is for
  }
}
function isInsertion(prev, next){
  const n = prev.length;
  if (next.length < n) return false;
  let head = 0;
  while (head < n && prev.charCodeAt(head) === next.charCodeAt(head)) head++;
  let tail = 0;
  while (tail < n - head && prev.charCodeAt(n - 1 - tail) === next.charCodeAt(next.length - 1 - tail)) tail++;
  return head + tail === n;
}
const fittedText = new WeakMap();
const fittedWidth = new WeakMap();   // the text width each box was last fitted at, read as the observer reads it
// `remeasure` false only for a typed or pasted insertion into the text this box was last fitted to.
function autoGrow(ta, remeasure = true){
  const held = [];
  for (let a = ta.parentElement; a && a !== document.documentElement; a = a.parentElement) if (a.scrollTop > 0) held.push([a, a.scrollTop]);
  const doc = document.scrollingElement;
  if (doc && doc.scrollTop > 0) held.push([doc, doc.scrollTop]);
  const cs = getComputedStyle(ta);
  if (remeasure) { const need = neededHeight(ta, cs); if (need > 0) ta.style.height = need + 'px'; }
  if (ta.clientWidth > 0) growToText(ta, cs);
  fittedText.set(ta, ta.value);
  fittedWidth.set(ta, textWidth(ta, cs));
  for (const [a, top] of held) if (a.scrollTop !== top) a.scrollTop = top;
}
// A few characters taken out at one place with no line break among them: one Backspace or Delete, or a word.
function isKeyDeletion(prev, next){
  const gone = prev.length - next.length;
  if (gone <= 0 || gone > 32) return false;
  let head = 0;
  while (head < next.length && prev.charCodeAt(head) === next.charCodeAt(head)) head++;
  if (prev.slice(head + gone) !== next.slice(head)) return false;
  return !prev.slice(head, head + gone).includes('\n');
}
// A Backspace can only leave a box too tall, never too short, so its twin waits for a pause in the typing.
const shrinkTimers = new WeakMap();
function autoGrowTyped(ta){
  const prev = fittedText.get(ta);
  clearTimeout(shrinkTimers.get(ta));
  if (prev !== undefined && isKeyDeletion(prev, ta.value)) {
    autoGrow(ta, false);
    shrinkTimers.set(ta, setTimeout(() => { if (ta.isConnected) autoGrow(ta); }, 300));
  } else autoGrow(ta, prev === undefined || !isInsertion(prev, ta.value));
}
// The panel is resized far more often than it is re-rendered: re-fit every box so a narrower panel never leaves text
// hidden inside one (the space rule, 27 September 2026). A resize event covers the panel being dragged; the observer
// below also covers a text-size change, which fires none. A box re-fits when its width is not the one it was last
// fitted at (the observer's previous report is not the reference: a box narrowed between its fit and its first report
// kept the wider width's height and hid the end of its text). Only a width change re-fits, so writing a height cannot
// set the observer off again; the width is unrounded, because a fraction of a pixel can move a line break.
window.addEventListener('resize', () => document.querySelectorAll('textarea').forEach((ta) => autoGrow(ta)));
const growWatch = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver((entries) => {
  for (const e of entries) {
    const ta = e.target;
    if (textWidth(ta, getComputedStyle(ta)) !== fittedWidth.get(ta)) autoGrow(ta);
  }
});
function wire() {
  const fi = document.getElementById('f');
  if (fi) fi.addEventListener('input', () => { filter = fi.value; const at = fi.selectionStart; render(); const nf = document.getElementById('f'); if (nf) { nf.focus(); try { nf.setSelectionRange(at, at); } catch(_){} } });
  root.querySelectorAll('textarea').forEach(ta => { autoGrow(ta); ta.addEventListener('input', () => autoGrowTyped(ta)); if (growWatch) growWatch.observe(ta); });   // size to content → no overflow scrollbar arrows
  root.querySelectorAll('textarea[data-commit-enter]').forEach(ta => {
    ta.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
      e.preventDefault();
      commit(ta.getAttribute('data-prop'), ta.value);
    });
  });
  root.querySelectorAll('.cat').forEach(el => el.addEventListener('click', () => { const c = el.getAttribute('data-cat'); if (collapsed.has(c)) collapsed.delete(c); else collapsed.add(c); render(); }));
  root.querySelectorAll('[data-prop]').forEach(el => {
    if (el.hasAttribute('indeterminate')) el.indeterminate = true;   // multi-select varies → tri-state checkbox
    if (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && el.type !== 'checkbox')) el.addEventListener('input', () => noteDraft(el));
    el.addEventListener('change', () => {
      lastError = null;
      const value = el.type === 'checkbox' ? String(el.checked) : el.value;
      const name = el.getAttribute('data-prop');
      if (name === 'FormatString') {
        const problem = formatStringProblem(value);
        if (problem) { lastError = { name, error: problem }; render(); return; }
      }
      commit(name, value);
    });
  });
  // A draft whose stored value moved: the person chooses. Keep mine WRITES the words at once, naming the stored value as the one it replaces (the person chose to
  // replace it). Leaving the box sends no change event, so a kept draft that waited for one was lost on the next selection. The box and its words stay put and
  // only the note goes, which is the normal saved state. A refusal (the value moved again) comes back through setError, which gives the words and the choice back.
  root.querySelectorAll('[data-keep]').forEach(b => b.addEventListener('click', () => {
    const name = b.getAttribute('data-keep'); const d = drafts.get(name); const p = propNamed(name);
    if (!d || !p) { render(); return; }
    // Keep mine does NOT write. It takes the newer value (shown beside the words) as the one the words now start from and gives them back as an ordinary draft: pressing Enter or leaving the box
    // saves them over it, on purpose, naming it (F11, Sol UAT J10-B: it used to write at once, over a value the person never saw).
    d.base = p.varies ? null : (p.value || ''); d.bases = basesOf(p); d.moved = false;
    persistObject(); render();
  }));
  root.querySelectorAll('[data-usenew]').forEach(b => b.addEventListener('click', () => { drafts.delete(b.getAttribute('data-usenew')); render(); }));

  // Format-string picker: toggle per row; pick → write through the normal edit path; click-away closes.
  root.querySelectorAll('.combo-btn').forEach(b => b.addEventListener('click', (e) => {
    e.stopPropagation();
    const name = b.getAttribute('data-combo');
    comboOpen = comboOpen === name ? null : name;
    render();
  }));
  root.querySelectorAll('.combo-item').forEach(it => it.addEventListener('click', () => {
    commit(comboOpen, it.getAttribute('data-fmt'));
    comboOpen = null;
    render();
  }));

  // Format expression editor.
  const fxProp = (state.props || []).find(x => x.kind === 'formatExpression');
  const fxEdit = root.querySelector('[data-fx-edit]');
  if (fxEdit) fxEdit.addEventListener('click', () => { fx.open = true; fx.base = fxProp && !fxProp.varies ? (fxProp.value || '') : null; fx.bases = basesOf(fxProp); fx.moved = false; render(); });
  const fxUseNew = root.querySelector('[data-fx-usenew]');
  if (fxUseNew) fxUseNew.addEventListener('click', () => { fx.draft = null; fx.moved = false; fx.base = fxProp ? (fxProp.value || '') : null; fx.bases = basesOf(fxProp); render(); });
  const fxTa = root.querySelector('.fx-ta');
  const fxHeldNow = () => { persistObject(); showDraftMark(fxProp ? fxProp.name : FX_FIELD, fxTypedNow()); };
  if (fxTa) fxTa.addEventListener('input', () => { fx.draft = fxTa.value; fxHeldNow(); });
  const fxTpl = root.querySelector('.fx-tpl');
  if (fxTpl) fxTpl.addEventListener('change', () => {
    const v = fxTpl.value;
    if (v && fxTa) { fxTa.value = v; fx.draft = v; autoGrow(fxTa); fxTa.focus(); fxHeldNow(); }
    fxTpl.selectedIndex = 0;
  });
  const fxCancel = root.querySelector('[data-fx-cancel]');
  if (fxCancel) fxCancel.addEventListener('click', () => { fx.open = false; fx.draft = null; fx.moved = false; render(); });
  const fxApply = root.querySelector('[data-fx-apply]');
  if (fxApply) fxApply.addEventListener('click', () => {
    const value = (fxTa ? fxTa.value : '').trim();
    const problem = value ? daxBalanceProblem(value) : null;
    const msg = root.querySelector('.fx-msg');
    if (problem) { if (msg) msg.textContent = problem; return; }
    lastError = null;
    // Apply names the expression it replaces. After the person was told it moved (and pressed Apply anyway) that is the one stored NOW; otherwise the one the words started from.
    const stored = fxProp && !fxProp.varies ? (fxProp.value || '') : null;
    const expected = fx.moved ? stored : (fx.base != null ? fx.base : stored);
    const wasMoved = fx.moved;
    fx.draft = fxTa ? fxTa.value : null;   // keep the draft: a refusal re-opens with the text intact
    fx.open = false; fx.moved = false;
    commit(fxProp ? fxProp.name : 'FormatStringExpression', value, expected, wasMoved || !fx.bases ? basesOf(fxProp) : fx.bases);
    render();
  });
}
document.addEventListener('click', (e) => {
  if (comboOpen && !(e.target instanceof Element && e.target.closest('.combo'))) { comboOpen = null; render(); }
});
document.addEventListener('keydown', (e) => {
  if (e.defaultPrevented || e.isComposing) return;
  const mac = /mac/i.test(navigator.platform || '');
  const mod = mac ? e.metaKey : e.ctrlKey;
  if (!mod || e.altKey) return;
  const typing = !!(e.target && e.target.closest && e.target.closest('input, textarea, select, [contenteditable="true"]'));
  const key = (e.key || '').toLowerCase();
  let command = null;
  if (e.shiftKey && key === 'p') command = 'workbench.action.showCommands';
  else if (!e.shiftKey && key === 's') command = 'semanticus.save';
  else if (!typing && key === 'z') command = e.shiftKey ? 'semanticus.redo' : 'semanticus.undo';
  else if (!typing && !e.shiftKey && key === 'y') command = 'semanticus.redo';
  if (!command) return;
  e.preventDefault();
  e.stopPropagation();
  vscode.postMessage({ type: 'runCommand', command, model: heldModel || undefined });   // the model this grid shows: Undo pressed here is refused when another model is open by now (join 11 solui, U1)
}, true);
vscode.postMessage({ type: 'ready' });
