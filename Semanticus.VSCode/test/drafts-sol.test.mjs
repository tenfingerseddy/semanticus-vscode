// m11/drafts-sol: Sol's review of the shared draft store (join 10). The two rules, held by tests across the editors they apply to.
//   R-A  NO WRITE INTO THE WRONG MODEL OR OVER UNSEEN WORK. A write a draft produces carries the model identity it was typed for, and the ENGINE refuses it
//        when the open model is another. No editor writes on unmount into whatever session is current. (The C# proof is DraftsSolFenceTests; the fake engine
//        here is a mirror of it.)
//   R-B  A DRAFT ENDS ONLY WHEN THE ENGINE ACCEPTED EXACTLY THAT TEXT. Clear only after the write was accepted AND the held value still equals what was
//        submitted; a refusal or a failure keeps the draft open, with the reason.
// F1 documentation writes A's section into B   F2 Desktop same stem   F4 documentation clears a newer draft   F6 start plan rename   F8 orphan drafts on build
// F10 start plan words editor. (F3 and F7 are in propgrid-drafts.test.mjs, F5 in drafts-tests-authoring.test.mjs, F9 in drafts-spec.test.mjs.)
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, makeHookRuntime, settle, ambientReact } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';

function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const area = (tree) => find(tree, (el) => el.type === 'textarea');
const type = (el, value) => el.props.onChange({ target: { value } });
globalThis.window ??= globalThis;

function makeWorld(model, stubs) {
  const persisted = {};
  const state = {
    loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f),
    saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
  };
  const load = makeLoader({ ...stubs, './bridge': { ...(stubs['./bridge'] ?? {}), ...state } }, ambientReact);
  const drafts = load('drafts');
  drafts.setDraftModel(model);
  const mountEl = (el) => { const rt = makeHookRuntime(); const h = rt.renderHook(() => el.type(el.props)); return { h, rt, tree: () => h.result, unmount: () => rt.unmount() }; };
  return {
    load, drafts, persisted, mountEl,
    mount(file, exportName, props) { const rt = makeHookRuntime(); const component = load(file)[exportName]; const h = rt.renderHook(() => component(props())); return { h, rt, tree: () => h.result, unmount: () => rt.unmount() }; },
  };
}
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };

// ---- F1 and F4: the documentation section ---------------------------------------------------------------------
// A fake engine with TWO models. It mirrors LocalEngine.FenceModelIdentity: a read or write that names the identity it was typed for is refused when another model is open.
function docEngine() {
  const e = { current: 'file:/m/A.bim', sections: { 'file:/m/A.bim': '', 'file:/m/B.bim': '' }, calls: [], hold: null };
  const fence = (id) => { if (id && id !== e.current) throw new Error('These words were typed for another model than the one open now, so nothing was written. Your words are kept.'); };
  e.rpc = async (method, ...a) => {
    e.calls.push([method, ...a]);
    if (method === 'getDocSection') { fence(a[2]); return e.sections[e.current]; }
    if (method === 'setDocSection') {
      fence(a[5]);
      if (e.hold) await e.hold.promise;
      if (a[4] != null && a[4] !== e.sections[e.current]) throw new Error('This section changed in the model after you opened it, so your version was not saved. Nothing was written.');
      e.sections[e.current] = a[2]; return { changed: true };
    }
    return undefined;
  };
  return e;
}
const docStubs = (e) => ({
  './bridge': { rpc: e.rpc, onDidChange: () => () => undefined },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
  './activity': { useClaudeReflection: () => undefined },
});
const section = (w, value = '') => w.mount('documentation.tsx', 'SectionEditor', () => ({ objRef: 'model', objName: 'Model', sectionKey: 'overview', value, onSaved: () => undefined }));
const DOC_SCOPE = 'docs|model|overview';

test('F1 / R-A: an editor that unmounts after a model switch does not write its words into the other model, and keeps its draft', async () => {
  const e = docEngine();
  const w = makeWorld('file:/m/A.bim', docStubs(e));
  const a = section(w); await settle();
  type(area(a.tree()), 'Words typed for A'); await settle();
  // The person opens B: the engine swaps sessions, the page sets the new model, THEN the old editor unmounts and writes on its way out.
  e.current = 'file:/m/B.bim'; w.drafts.setDraftModel('file:/m/B.bim');
  a.unmount(); await settle(); await settle();
  assert.equal(e.sections['file:/m/B.bim'], '', 'B keeps its own (empty) section: A\'s words were never written into it');
  assert.equal(e.sections['file:/m/A.bim'], '', 'and A was not written either, because the engine is not on A now');
  assert.deepEqual(w.drafts.draftStore.get('file:/m/A.bim', DOC_SCOPE, ''), { value: 'Words typed for A', base: '' }, 'A\'s draft is kept, filed under A');
  const named = e.calls.filter((c) => c[0] === 'setDocSection' || c[0] === 'getDocSection');
  assert.ok(named.every((c) => c[c[0] === 'setDocSection' ? 6 : 3] === 'file:/m/A.bim'), 'every read and write names the model it was typed for');
});

test('F1 / R-A: the same unmount while the SAME model is open still writes (the fence is not a block)', async () => {
  const e = docEngine();
  const w = makeWorld('file:/m/A.bim', docStubs(e));
  const a = section(w); await settle();
  type(area(a.tree()), 'Words typed for A'); await settle();
  a.unmount(); await settle(); await settle();
  assert.equal(e.sections['file:/m/A.bim'], 'Words typed for A');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', DOC_SCOPE, ''), null, 'the engine took exactly those words: the draft ends');
});

test('F4 / R-B: an earlier save that completes does not delete the words typed after the editor reopened', async () => {
  const e = docEngine();
  const w = makeWorld('file:/m/A.bim', docStubs(e));
  const a = section(w); await settle();
  type(area(a.tree()), 'Paragraph one.'); await settle();
  e.hold = deferred();             // the engine is busy: the save on leaving waits
  a.unmount(); await settle();
  const b = section(w); await settle();   // back on Documentation before that save completes
  assert.equal(area(b.tree()).props.value, 'Paragraph one.', 'the editor opens on the held words');
  type(area(b.tree()), 'Paragraph one. Paragraph two.'); await settle();
  e.hold.resolve(); e.hold = null; await settle(); await settle();
  assert.equal(e.sections['file:/m/A.bim'], 'Paragraph one.', 'only the first paragraph entered the model');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', DOC_SCOPE, '')?.value, 'Paragraph one. Paragraph two.', 'the longer words are still held: the save did not own them');
});

test('F4 / R-B: a refused save keeps the draft', async () => {
  const e = docEngine();
  const w = makeWorld('file:/m/A.bim', docStubs(e));
  const a = section(w); await settle();
  type(area(a.tree()), 'Mine'); await settle();
  e.sections['file:/m/A.bim'] = 'The assistant wrote this';   // moved before the unmount's write
  a.unmount(); await settle(); await settle();
  assert.equal(e.sections['file:/m/A.bim'], 'The assistant wrote this');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', DOC_SCOPE, '')?.value, 'Mine');
});

// ---- F2: Desktop, two files with one window stem ---------------------------------------------------------------
test('F2: a Desktop key names the instance, so words typed for another instance of the same stem are listed read-only and never reach an editor', () => {
  const w = makeWorld('desktop:sales|localhost:51234|guid-west', {});
  const store = w.drafts.draftStore;
  store.set('desktop:sales|localhost:51234|guid-west', 'docs|model|methodology', '', { value: 'West words', base: '' });
  // East/Sales.pbix: the same stem, another instance.
  const east = w.drafts.modelKeyOfSession({ modelIdentity: 'desktop:sales|localhost:60321|guid-east' });
  assert.equal(store.get(east, 'docs|model|methodology', ''), null, 'no editor of East finds West\'s words');
  const other = store.otherModels(east);
  assert.equal(other.length, 1, 'they are listed under the other-models line');
  assert.equal(other[0].label, 'sales', 'named by the window stem, not by the instance coordinates');
  assert.match(w.drafts.wordsAsText(other[0].drafts), /West words/, 'and Copy gives the words');
});

// ---- the start plan: F6 rename, F10 words, F8 orphans on build ----------------------------------------------------
const spStubs = {
  './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
  './bridge': {
    rpc: async () => { throw new Error('no engine here'); }, onEngineState: () => () => {}, pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
    copyText: async () => true, onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true, revealInTree: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
};
const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
const MODEL = view.destination.modelPath;
const spWorld = () => makeWorld(MODEL, spStubs);
const M = () => makeLoader(spStubs)('startplan-model');
const Mod = M();
const renameLine = Mod.linesOf(view).find((l) => l.item?.kind === 'include_table' && Mod.lineControls(l).rename);
const lineId = renameLine.item.id, current = Mod.lineControls(renameLine).rename.current;
const wordsLine = Mod.linesOf(view).find((l) => Mod.lineControls(l).words);
const wordsId = wordsLine.item.id, wordsKind = Mod.lineControls(wordsLine).words.kind, wordsNow = Mod.lineControls(wordsLine).words.current;
const detail = (w, line, over = {}) => w.mount('startplan.tsx', 'LineDetail', () => ({ line, view, tier: { pro: false }, busy: false, onEdit: async () => true, onWords: async () => ({ saved: true, moved: false, said: null }), modelKey: MODEL, ...over }));
const sdraft = (w) => (kind, id) => w.load('startplan-drafts').draftStore.get(MODEL, id, kind);

test('F6 / R-B: a refused rename keeps the typed name and the editor open', async () => {
  const w = spWorld(); const held = sdraft(w);
  w.load('startplan-drafts').draftStore.set(MODEL, lineId, 'rename', { text: 'Existing', base: current });
  const calls = [];
  const d = detail(w, renameLine, { onEdit: async (e) => { calls.push(e); return false; } });   // the engine refused: a duplicate name
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'Editor'));
  byTestId(ed.tree(), 'sp-rename-save').props.onClick(); await settle(); await settle();
  assert.equal(calls.length, 1, 'the save was sent');
  assert.deepEqual(held('rename', lineId), { text: 'Existing', base: current }, 'the typed name is still held after the refusal');
  assert.ok(find(d.tree(), (el) => el.type?.name === 'Editor'), 'and the editor is still open');
});

test('F6 / R-B: an accepted rename ends the draft and closes the editor', async () => {
  const w = spWorld(); const held = sdraft(w);
  w.load('startplan-drafts').draftStore.set(MODEL, lineId, 'rename', { text: 'Employees', base: current });
  const d = detail(w, renameLine, { onEdit: async () => true });
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'Editor'));
  byTestId(ed.tree(), 'sp-rename-save').props.onClick(); await settle(); await settle();
  assert.equal(held('rename', lineId), null);
  assert.equal(find(d.tree(), (el) => el.type?.name === 'Editor'), null, 'the editor closed');
});

test('F6 / R-B: a failed rename (the engine is away) keeps the typed name too', async () => {
  const w = spWorld(); const held = sdraft(w);
  w.load('startplan-drafts').draftStore.set(MODEL, lineId, 'rename', { text: 'Employees', base: current });
  const d = detail(w, renameLine, { onEdit: async () => { throw new Error('Engine not connected'); } });
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'Editor'));
  byTestId(ed.tree(), 'sp-rename-save').props.onClick(); await settle(); await settle();
  assert.equal(held('rename', lineId)?.text, 'Employees');
});

test('F10 / R-B: words typed while a words save is pending are kept, and the editor stays open over what was saved', async () => {
  const w = spWorld(); const held = sdraft(w);
  const ds = w.load('startplan-drafts').draftStore;
  ds.set(MODEL, wordsId, wordsKind, { text: 'first draft', base: wordsNow });
  const gate = deferred(); const sent = [];
  const d = detail(w, wordsLine, { onWords: async (base, t) => { sent.push(t); await gate.promise; return { saved: true, moved: false, said: null }; } });
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'WordsEditor'));
  byTestId(ed.tree(), 'sp-words-save').props.onClick(); await settle();
  type(byTestId(ed.tree(), 'sp-words-input'), 'first draft with later work'); await settle();
  gate.resolve(); await settle(); await settle();
  assert.deepEqual(sent, ['first draft'], 'only the earlier words were sent');
  assert.equal(held(wordsKind, wordsId)?.text, 'first draft with later work', 'the later words are still held');
  assert.ok(find(d.tree(), (el) => el.type?.name === 'WordsEditor'), 'the editor did not close over words it never sent');
});

test('F10 / R-B: a words save that owns the text ends the draft and closes; one the engine refused keeps both', async () => {
  const w = spWorld(); const held = sdraft(w);
  const ds = w.load('startplan-drafts').draftStore;
  ds.set(MODEL, wordsId, wordsKind, { text: 'my words', base: wordsNow });
  let answer = { saved: false, moved: false, said: 'No.' };
  const d = detail(w, wordsLine, { onWords: async () => answer });
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'WordsEditor'));
  byTestId(ed.tree(), 'sp-words-save').props.onClick(); await settle(); await settle();
  assert.equal(held(wordsKind, wordsId)?.text, 'my words'); assert.ok(find(d.tree(), (el) => el.type?.name === 'WordsEditor'));
  answer = { saved: true, moved: false, said: null };
  byTestId(ed.tree(), 'sp-words-save').props.onClick(); await settle(); await settle();
  assert.equal(held(wordsKind, wordsId), null);
  assert.equal(find(d.tree(), (el) => el.type?.name === 'WordsEditor'), null);
});

test('F10 / R-B: a stale words save that completes after the row was left does not delete a newer draft', async () => {
  const w = spWorld(); const held = sdraft(w);
  const ds = w.load('startplan-drafts').draftStore;
  ds.set(MODEL, wordsId, wordsKind, { text: 'first draft', base: wordsNow });
  const gate = deferred();
  const d = detail(w, wordsLine, { onWords: async () => { await gate.promise; return { saved: true, moved: false, said: null }; } });
  await settle();
  const ed = w.mountEl(find(d.tree(), (el) => el.type?.name === 'WordsEditor'));
  byTestId(ed.tree(), 'sp-words-save').props.onClick(); await settle();
  ed.unmount(); d.unmount();   // the row is left
  ds.set(MODEL, wordsId, wordsKind, { text: 'first draft, then more', base: wordsNow });   // the row reopened and the person typed on
  gate.resolve(); await settle(); await settle();
  assert.equal(held(wordsKind, wordsId)?.text, 'first draft, then more');
});

test('F8: a draft whose row is gone (the assistant renamed it) is named in the build warning, and a build with only such drafts asks', () => {
  const w = spWorld();
  const ds = w.load('startplan-drafts').draftStore;
  const m = w.load('startplan-model');
  ds.set(MODEL, 'col:orphan-ref', 'rename', { text: 'My new name', base: 'Old' });
  w.drafts.draftStore.setLabel(MODEL, 'startplan|col:orphan-ref', 'rename', 'Old in Sales');
  const held = ds.held(MODEL);
  const labels = new Map([['col:orphan-ref', 'Old in Sales']]);
  assert.deepEqual(m.unsavedRowNames(view, held, labels), ['Old in Sales'], 'the orphan is listed, by the name it had');
  assert.deepEqual(m.unsavedRowNames(view, held), ['A line the plan no longer shows'], 'with no name kept it still asks');
});

// ---- F14: "Build without them" keeps the held words -------------------------------------------------------------------------------------------------
// Sol UAT J10-C-005: the guard said the build does not include the words; after the build they were gone and nothing said so. They now stay held after the build, listed on the built page
// with Copy and Discard, and the choice says so. Only the person's Discard (or Discard of the whole plan) ends them.
test('F14: a built plan does not clear the held words; the page lists them with Copy and Discard, and the choice says they are kept', async () => {
  const w = spWorld();
  const ds = w.load('startplan-drafts').draftStore;
  ds.set(MODEL, lineId, 'rename', { text: 'Employees', base: current });
  w.drafts.draftStore.setLabel(MODEL, `startplan|${lineId}`, 'rename', 'Employees table');
  const builtView = { ...view, built: true, approval: view.approval };
  const p = w.mount('startplan.tsx', 'StartPlanPanel', () => ({ view: builtView, onView: () => undefined, onBuilt: () => undefined, onReload: () => undefined, pro: false, stale: null }));
  await settle(); await settle();
  assert.equal(ds.get(MODEL, lineId, 'rename')?.text, 'Employees', 'building did not delete the held words');
  const body = w.mountEl(find(p.tree(), (el) => el.type?.name === 'StartPlanBody'));
  await settle();
  const keptEl = find(body.tree(), (el) => el.type?.name === 'WordsKeptAfterBuild');
  assert.ok(keptEl, 'the built page draws the kept-words list');
  const kept = w.mountEl(keptEl); await settle();
  const list = byTestId(kept.tree(), 'sp-kept-after-build');
  assert.ok(list, 'the built page lists the words that were kept');
  assert.ok(find(list, (el) => el.props?.['data-testid'] === 'sp-kept-copy'), 'with Copy');
  find(list, (el) => el.props?.['data-testid'] === 'sp-kept-discard').props.onClick(); await settle();
  assert.equal(ds.get(MODEL, lineId, 'rename'), null, 'Discard ends them');
  assert.match(w.load('startplan-model').COPY.buildWithoutTip, /stay|kept/i, 'the choice says the words are kept, not dropped');
});

// ---- F11: Keep mine is not pressable until the newer value is on screen ------------------------------------------------------------------------------
// Sol UAT J10-B-001..004: "This text changed while you were editing it" with Keep mine / Use the new one never showed the newer value (AI instructions, calc item DAX, Properties format string, Spec);
// Keep mine then Save overwrote the assistant's text in one History edit. The banner now shows the newer value beside the words, Keep mine waits for it, and the Save names that value.
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const webSrc = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src');
const preOf = (tree) => find(tree, (el) => el.type === 'pre');
const keepOf = (tree) => find(tree, (el) => el.props?.['data-testid'] === 'moved-keep-mine');

test('F11: the banner shows the newer value, and Keep mine cannot be pressed while it is not on screen', async () => {
  const w = makeWorld('file:/m/A.bim', {});
  const { EditMovedBanner } = w.load('editbase.tsx');
  const withValue = w.mountEl({ type: EditMovedBanner, props: { what: 'text', theirs: 'The assistant wrote this.', onKeepMine() {}, onUseNew() {} } });
  assert.match(String(preOf(withValue.tree()).props.children), /The assistant wrote this\./);
  assert.ok(!keepOf(withValue.tree()).props.disabled, 'the value is on screen: Keep mine can be pressed');
  const unread = w.mountEl({ type: EditMovedBanner, props: { what: 'text', theirs: undefined, onKeepMine() {}, onUseNew() {} } });
  assert.equal(keepOf(unread.tree()).props.disabled, true, 'not read yet: Keep mine waits');
  const gone = w.mountEl({ type: EditMovedBanner, props: { what: 'text', theirs: null, onKeepMine() {}, onUseNew() {} } });
  assert.match(String(preOf(gone.tree()).props.children), /nothing is there now/);
  const obj = w.mountEl({ type: EditMovedBanner, props: { what: 'check', theirs: { fields: { expectedValue: '42' } }, onKeepMine() {}, onUseNew() {} } });
  assert.match(String(preOf(obj.tree()).props.children), /"expectedValue": "42"/, 'a structured value is shown as readable JSON');
});

test('F11: documentation: the moved section is shown in the banner, and Keep mine then Save names that newer section', async () => {
  const e = docEngine();
  const w = makeWorld('file:/m/A.bim', docStubs(e));
  e.sections['file:/m/A.bim'] = 'Original';
  const a = section(w, 'Original'); await settle();
  type(area(a.tree()), 'My words'); await settle();
  e.sections['file:/m/A.bim'] = 'The assistant rewrote this';
  const b = w.mount('documentation.tsx', 'SectionEditor', () => ({ objRef: 'model', objName: 'Model', sectionKey: 'overview', value: 'The assistant rewrote this', onSaved: () => undefined })); await settle();
  const banner = find(b.tree(), (el) => el.type?.name === 'EditMovedBanner');
  assert.ok(banner, 'the section moved under the typed words');
  assert.equal(banner.props.theirs, 'The assistant rewrote this', 'the banner is given the newer section to show');
  banner.props.onKeepMine(); await settle();
  e.calls.length = 0;
  find(b.tree(), (el) => el.type === 'button' && [].concat(el.props?.children ?? []).join('') === 'Save').props.onClick(); await settle(); await settle();
  const write = e.calls.find((c) => c[0] === 'setDocSection');
  assert.equal(write?.[3], 'My words');
  assert.equal(write?.[5], 'The assistant rewrote this', 'the Save names the newer section it replaces, the one the person was shown');
});

test('F11 class: every banner in the webview is given the newer value (the source is checked, so a new editor cannot forget it)', () => {
  const missing = [];
  for (const name of readdirSync(webSrc).filter((n) => /\.tsx$/.test(n) && n !== 'editbase.tsx')) {
    const src = readFileSync(join(webSrc, name), 'utf8');
    for (const m of src.matchAll(/<EditMovedBanner\b[^>]*>/g)) if (!/\btheirs=/.test(m[0])) missing.push(name);
  }
  assert.deepEqual(missing, [], 'these editors draw the banner without showing the newer value');
});

test('note: the other-models line names a TMDL folder model by its model folder, not "definition"', () => {
  const w = makeWorld('file:/m/Other.bim', {});
  assert.equal(w.drafts.modelNameOfKey('file:/m/Northstar.SemanticModel/definition'), 'Northstar');
  assert.equal(w.drafts.modelNameOfKey('file:/m/Sales.bim'), 'Sales');
});
