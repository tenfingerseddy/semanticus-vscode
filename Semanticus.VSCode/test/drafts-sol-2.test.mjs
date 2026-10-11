// m11/drafts-sol round 2 (the check failed R-A, R-B and F14). The page's half of the class fixes; the engine's half is Semanticus.Tests/ModelFenceTests.cs.
//   C1  every request the page sends names the model it was showing (bridge.ts), and a Save that is still in flight when another model opens writes nothing (editbase.tsx).
//   C2  a draft ends only when the words held are still exactly the ones the engine took: the editor stays open on newer words (instructions, notes, a lesson, a rule, the spec JSON).
//   C3  words kept for a start plan that was discarded by anything but the person's Discard are listed with Copy and Discard, never deleted.
//   X   Spec name buffers and M query words of a renamed or removed object are listed; a newer value of `undefined` is shown, not waited for forever.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, makeHookRuntime, settle, ambientReact } from './helpers/webview-load.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = (name) => readFileSync(resolve(root, 'webview', 'src', name), 'utf8');

function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const findAll = (tree, pred) => { const out = []; walk(tree, (el) => { if (pred(el)) out.push(el); }); return out; };
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('');
const button = (tree, label) => find(tree, (el) => (typeof el.type === 'function' || el.type === 'button') && text(el) === label);
const area = (tree) => find(tree, (el) => el.type === 'textarea');
const type = (el, value) => el.props.onChange({ target: { value } });
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };

// ---- C1 the bridge names the model --------------------------------------------------------------------------------------------------------------------------

test('C1: every rpc the page sends names the model the page is showing, whatever editor sent it', () => {
  const posted = [];
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState: () => undefined });
  const load = makeLoader({}, null);
  const bridge = load('bridge.ts'), drafts = load('drafts.ts');
  void bridge.rpc('sessionInfo').catch(() => undefined);
  assert.equal(posted.at(-1).modelIdentity, undefined, 'a page that shows no model names none (the standalone Connections hub)');
  drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  void bridge.rpc('setAiInstructions', 'words', null, 'human', '').catch(() => undefined);
  assert.equal(posted.at(-1).modelIdentity, 'file:/m/A.bim');
  assert.deepEqual(posted.at(-1).params, ['words', null, 'human', ''], 'no method signature carries it');
  drafts.setDraftModel('live:https://x|Sales', 'Sales', 's2');
  void bridge.rpc('deleteObject', 'measure:T/M', 'human').catch(() => undefined);
  assert.equal(posted.at(-1).modelIdentity, 'live:https://x|Sales', 'it follows the page when the model changes');
  drafts.setDraftModel('name:Sales', 'Sales', 's3');   // a fallback key from an engine that sends no identity: not the engine's shape, so it is not named
  void bridge.rpc('deleteObject', 'measure:T/M', 'human').catch(() => undefined);
  assert.equal(posted.at(-1).modelIdentity, undefined);
});

// ---- the guard in a tiny component -----------------------------------------------------------------------------------------------------------------------------

function guardWorld(readNow, model = 'file:/m/A.bim', session = 's1') {
  const persisted = {};
  const load = makeLoader({ './bridge': { loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); }, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}) } }, ambientReact);
  const drafts = load('drafts');
  drafts.setDraftModel(model, '', session);   // the page sets its model before any editor renders (App.tsx)
  const { useEditBase } = load('editbase.tsx');
  const rt = makeHookRuntime();
  const s = { draft: 'typed', current: 'old', editing: true };
  const h = rt.renderHook(() => useEditBase({ current: s.current, draft: s.draft, editing: s.editing, reseed: (v) => { s.draft = v; }, readNow, persist: { scope: 'g|1' } }));
  return { drafts, h, s, guard: () => h.result };
}

test('C1: a Save still in flight when another model opens is not written, and its words stay held under the model they were typed for', async () => {
  const gate = deferred();
  const w = guardWorld(async () => { await gate.promise; return 'old'; });
  w.drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  w.h.rerender(); await settle();
  const verdict = w.guard().beforeSave();
  w.drafts.setDraftModel('file:/m/B.bim', 'B', 's2');   // the assistant opened B while the read was on its way
  gate.resolve(); await settle();
  assert.equal(await verdict, false, 'the Save does not go on to write');
  assert.equal(w.drafts.draftStore.get('file:/m/B.bim', 'g|1', ''), null, 'nothing of A is held under B');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', 'g|1', '')?.value, 'typed', 'A\'s words are still held under A');
});

test('C1: an unsaved model that got its first Save is still the same model for a Save in flight (same session, new key)', async () => {
  const gate = deferred();
  const w = guardWorld(async () => { await gate.promise; return 'old'; }, 'unsaved:k1');
  w.drafts.setDraftModel('unsaved:k1', 'Fresh', 's1');
  w.h.rerender(); await settle();
  const verdict = w.guard().beforeSave();
  w.drafts.setDraftModel('file:/m/Fresh.bim', 'Fresh', 's1');   // the page's key moved, the session did not
  gate.resolve(); await settle();
  assert.equal(await verdict, true, 'the Save goes on: it is the same model');
});

test('X: a newer value that reads back as nothing at all is shown as nothing, so Keep mine is not left disabled for good', async () => {
  const w = guardWorld(async () => undefined);
  w.drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  w.h.rerender(); await settle();
  assert.equal(await w.guard().beforeSave(), false, 'the starting point moved: the person chooses');
  w.h.rerender(); await settle();
  assert.equal(w.guard().moved, true);
  assert.equal(w.guard().theirs, null, 'undefined becomes "nothing is there now", which is something to show');
  const { EditMovedBanner } = makeLoader({}, ambientReact)('editbase.tsx');
  const banner = EditMovedBanner({ what: 'text', theirs: w.guard().theirs, onKeepMine: () => undefined, onUseNew: () => undefined });
  assert.ok(!find(banner, (el) => el.props?.['data-testid'] === 'moved-keep-mine').props.disabled, 'Keep mine can be pressed');
});

// ---- C2 R-B in each editor that closes after a save ----------------------------------------------------------------------------------------------------

function cardWorld(fileName, exportName, rpcImpl, stubs = {}) {
  const persisted = {};
  const load = makeLoader({
    './bridge': { rpc: rpcImpl, onDidChange: () => () => undefined, onActivity: () => () => undefined, loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); }, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}) },
    './hooks': { messageOf: (x) => String(x?.message ?? x), useAutoGrow: () => ({ current: null }), useRemote: () => [{ status: 'idle' }, () => undefined] },
    './knowledge': { KnowledgeView: () => null },
    './pro': { PENDING_ACCESS: '', ProPill: () => null, useFeature: () => ({}) },
    './ui': { PathDisclosure: () => null }, './docrender': { mdToHtml: (s) => s },
    ...stubs,
  }, ambientReact);
  const drafts = load('drafts');
  drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  const rt = makeHookRuntime();
  return { drafts, load, rt, exp: load(fileName)[exportName] };
}

test('C2: AI instructions: words typed while the Save is on its way keep the editor open and stay held over the saved text', async () => {
  let live = '';
  const hold = { write: null };
  const w = cardWorld('instructions.tsx', 'AiInstructionsCard', async (m, ...a) => {
    if (m === 'getAiInstructions') return { present: true, instructions: live, limit: 2000 };
    if (m === 'setAiInstructions') { if (hold.write) await hold.write.promise; live = a[0]; return { changed: true }; }
    return m === 'listPrimerSuggestions' ? { suggestions: [] } : undefined;
  });
  const props = () => ({ state: { status: 'ready', value: { present: true, instructions: live, limit: 2000 } }, onReload: () => undefined, onNotes: () => undefined, suggestions: { status: 'ready', value: { suggestions: [] } }, onSuggestionsChanged: () => undefined });
  const h = w.rt.renderHook(() => w.exp(props()));
  button(h.result, 'Edit').props.onClick(); await settle();
  type(area(h.result), 'First.'); await settle();
  hold.write = deferred();
  button(h.result, 'Save').props.onClick(); await settle(); await settle();
  type(area(h.result), 'First. Second.'); await settle();
  hold.write.resolve(); hold.write = null; await settle(); await settle(); await settle();
  assert.equal(live, 'First.', 'the engine took only what the Save carried');
  assert.ok(area(h.result), 'the editor is still open: it was not closed over words that were never saved');
  assert.equal(area(h.result).props.value, 'First. Second.', 'and it still shows them');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', 'instructions|text', '')?.value, 'First. Second.', 'held over the saved text');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', 'instructions|text', '')?.base, 'First.', '...so a later Save names what the engine holds now');
  assert.ok(!find(h.result, (el) => el.type?.name === 'EditMovedBanner'), 'their own Save is not reported as a change made by someone else');
  // the control: nothing typed meanwhile closes the editor and ends the draft
  button(h.result, 'Save').props.onClick(); await settle(); await settle(); await settle();
  assert.equal(live, 'First. Second.');
  assert.equal(area(h.result), null, 'the words the engine took were the words held: the editor closes');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', 'instructions|text', ''), null, 'and the draft ends');
});

test('C2: a lesson (the Notes card): the same rule', async () => {
  let live = { id: 'i1', text: 'Original', kind: 'insight', keys: [], status: 'approved', score: 1, uses: 0, retrievals: 0, scope: 'model', provenance: { origin: 'agent' } };
  const hold = { write: null };
  const w = cardWorld('knowledge.tsx', 'InsightCard', async (m, ...a) => {
    if (m === 'listInsights') return { insights: [live], skippedCorruptLines: 0 };
    if (m === 'editInsight') { if (hold.write) await hold.write.promise; live = { ...live, text: a[1] }; return live; }
    return undefined;
  });
  const h = w.rt.renderHook(() => w.exp({ rec: live, onChanged: () => undefined }));
  await settle();
  button(h.result, 'Edit').props.onClick(); await settle();
  const box = () => find(h.result, (el) => el.props?.['aria-label'] === 'Insight text');
  box().props.onChange({ target: { value: 'One' } }); await settle();
  hold.write = deferred();
  button(h.result, 'Save').props.onClick(); await settle(); await settle();
  box().props.onChange({ target: { value: 'One two' } }); await settle();
  hold.write.resolve(); hold.write = null; await settle(); await settle(); await settle();
  assert.equal(live.text, 'One');
  assert.ok(box(), 'the card stays open on the newer words');
  assert.equal(box().props.value, 'One two');
  assert.equal(w.drafts.draftStore.get('file:/m/A.bim', 'insight|i1', '')?.value.text, 'One two');
});

// ---- C3 words kept for a plan that is gone are listed -----------------------------------------------------------------------------------------------------------

test('C3: words kept for a start plan that is gone are listed with Copy and Discard, and Discard ends only that one', async () => {
  const persisted = {};
  const copied = [];
  const load = makeLoader({
    './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
    './bridge': { rpc: async () => undefined, onEngineState: () => () => {}, onStartPlanChange: () => () => {}, onReconnect: () => () => {}, copyText: async (t) => { copied.push(t); return true; }, onDidChange: () => () => {},
      loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); }, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}) },
    './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: '' }, './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} }, './connection': { useConnection: () => ({ openConnections: () => {} }) },
  }, ambientReact);
  const drafts = load('drafts'); drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  const words = load('startplan-drafts').draftStore;
  words.set('file:/m/A.bim', 'one', 'description', { text: 'First words.', base: '' });
  words.set('file:/m/A.bim', 'two', 'description', { text: 'Second words.', base: '' });
  drafts.draftStore.setLabel('file:/m/A.bim', 'startplan|one', 'description', 'Table one');
  const { WordsKeptAfterBuild } = load('startplan.tsx');
  const rt = makeHookRuntime();
  const h = rt.renderHook(() => WordsKeptAfterBuild({ modelKey: 'file:/m/A.bim', why: 'gone' }));
  await settle();
  const list = byTestId(h.result, 'sp-kept-gone');
  assert.ok(list, 'the page lists them');
  const rows = findAll(list, (el) => el.props?.['data-testid'] === 'sp-kept-row');
  assert.equal(rows.length, 2);
  assert.match(JSON.stringify(rows[0]), /Table one/, 'named by the row they were typed for');
  find(rows[0], (el) => el.props?.['data-testid'] === 'sp-kept-copy').props.onClick(); await settle();
  assert.match(copied[0] ?? '', /First words\./);
  find(rows[0], (el) => el.props?.['data-testid'] === 'sp-kept-discard').props.onClick(); await settle();
  assert.equal(words.get('file:/m/A.bim', 'one', 'description'), null, 'Discard ends the one');
  assert.equal(words.get('file:/m/A.bim', 'two', 'description')?.text, 'Second words.', 'and only the one');
});

import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';
test('C3: when another plan arrives, the words kept for the one that went move out of the rows\' scope to a quiet list, so they are never shown on a line of the new plan', async () => {
  const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
  const MODEL = view.destination.modelPath;
  const persisted = {};
  const engine = { plan: view };
  const load = makeLoader({
    './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
    './bridge': { rpc: async (m) => (m === 'getStartPlan' ? engine.plan : undefined), onEngineState: () => () => {}, pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
      copyText: async () => true, onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true, revealInTree: () => {},
      loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); }, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}) },
    './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: '' }, './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} }, './connection': { useConnection: () => ({ openConnections: () => {} }) },
  }, ambientReact);
  const drafts = load('drafts'); drafts.setDraftModel(MODEL, 'Model', 's1');
  const words = load('startplan-drafts').draftStore;
  words.set(MODEL, 'some-line', 'description', { text: 'Words typed for the first plan.', base: 'Old.' });
  const sp = load('startplan.tsx');
  const rt = makeHookRuntime();
  let nonce = 0;
  const h = rt.renderHook(() => sp.useStartPlan('s1', 1, nonce));
  await settle(); await settle();
  assert.equal(words.get(MODEL, 'some-line', 'description')?.text, 'Words typed for the first plan.', 'a plan that is read keeps its words on its rows as ever');
  engine.plan = { exists: false }; nonce = 1; h.rerender(); await settle(); await settle();
  assert.equal(words.get(MODEL, 'some-line', 'description')?.text, 'Words typed for the first plan.', 'the plan went: the words are where they were, and the Plan lists them');
  engine.plan = view; nonce = 2; h.rerender(); await settle(); await settle();   // another plan (a new start) arrives
  assert.equal(words.get(MODEL, 'some-line', 'description'), null, 'they are not on a row of the plan that arrived');
  assert.equal(drafts.draftStore.get(MODEL, 'startplan-gone|some-line', 'description')?.value, 'Words typed for the first plan.', 'they are kept, in the retired scope');
  const { WordsKeptAfterBuild } = load('startplan.tsx');
  rt.unmount();
  const rt2 = makeHookRuntime();
  const list = rt2.renderHook(() => WordsKeptAfterBuild({ modelKey: MODEL, why: 'retired' }));
  await settle();
  const note = byTestId(list.result, 'sp-kept-retired');
  assert.ok(note, 'one quiet line says they are kept');
  assert.ok(!JSON.stringify(note).includes('Words typed for the first plan.'), 'the words themselves are not on the page until the person opens the line');
  assert.ok(!/Draft not saved/.test(JSON.stringify(note)));
  byTestId(note, 'sp-kept-retired-toggle').props.onClick(); await settle();
  const opened = byTestId(list.result, 'sp-kept-retired');
  assert.match(JSON.stringify(opened), /Words typed for the first plan\./, 'opened, they are listed');
  assert.ok(find(opened, (el) => el.props?.['data-testid'] === 'sp-kept-copy') && find(opened, (el) => el.props?.['data-testid'] === 'sp-kept-discard'), 'with Copy and Discard');
  rt2.unmount();
});

test('C3: the plan read never deletes words by itself, and the Plan page draws the list when no plan is on screen', () => {
  const sp = src('startplan.tsx');
  assert.ok(!/draftStore\.clearModel\(draftModel\(\)\)/.test(sp), 'the read that finds no plan clears nothing');
  assert.match(src('plan.tsx'), /<WordsKeptAfterBuild modelKey=\{draftModel\(\)\} why="gone" \/>/, 'the Plan lists them while no start plan is on screen');
});

// ---- X Spec name buffers and M queries of a renamed or removed object ------------------------------------------------------------------------------------

test('X: a Spec name buffer of a renamed table, column or measure is an orphan the page lists; a box that is still there is not', async () => {
  const persisted = {};
  const load = makeLoader({ './bridge': { loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); }, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}),
    rpc: async () => undefined, onSpecChange: () => () => undefined, pickSpecFile: async () => null }, './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} }, './connection': { useConnection: () => ({ openConnections: () => {} }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] } }, ambientReact);
  const drafts = load('drafts'); drafts.setDraftModel('file:/m/A.bim', 'A', 's1');
  const { specHoldIds, specHoldName } = load('spec.tsx');
  const spec = { name: 'M', tables: [{ name: 'Sales', columns: [{ name: 'Amount' }] }], relationships: [], measures: [{ table: 'Sales', name: 'Total' }] };
  const ids = specHoldIds(spec);
  for (const id of ['model|name', 'table:Sales|name', 'col:Sales/Amount|name', 'measure:Sales/Total|format']) assert.ok(ids.includes(id), id);
  drafts.draftStore.set('file:/m/A.bim', 'spec|buf|table:Sales|name', '', { value: 'Sales2', base: 'Sales' });      // still there
  drafts.draftStore.set('file:/m/A.bim', 'spec|buf|table:Orders|name', '', { value: 'Orders2', base: 'Orders' });    // the table was renamed or removed
  drafts.draftStore.set('file:/m/A.bim', 'spec|buf|measure:Sales/Old|format', '', { value: '0.0', base: '' });
  const { orphanScopes } = load('orphandrafts.tsx');
  const orphans = orphanScopes('spec|buf|', ids).map((o) => o.scope.slice('spec|buf|'.length)).sort();
  assert.deepEqual(orphans, ['measure:Sales/Old|format', 'table:Orders|name']);
  assert.equal(specHoldName('table:Orders|name'), 'Table Orders, name');
  assert.match(src('spec.tsx'), /<OrphanDrafts prefix="spec\|buf\|"/, 'the Spec page draws the list');
});

test('X: M query words of a query that is gone are listed by the M page, over every query of the model', () => {
  const m = src('mcode.tsx');
  assert.match(m, /<OrphanDrafts prefix="mcode\|query\|"/, 'the M page draws the list');
  assert.match(m, /doc\.tables\.flatMap\(\(t\) => t\.partitions/, 'the live ids are every query of the model, not only the selected table');
  assert.equal((m.match(/\{orphanWords\}/g) ?? []).length, 2, 'in both layouts (with and without an editable query)');
});

// ---- the class: every Save that reads first goes through the one guard -----------------------------------------------------------------------------------------

test('C1: every editor that saves from a copy reads first through the one guard, and no editor names a model of its own except the documentation unmount save', () => {
  const editors = ['advmodels.tsx', 'connectionshub.tsx', 'mcode.tsx', 'spec.tsx', 'tests-authoring.tsx', 'rulesauthor.tsx', 'documentation.tsx', 'knowledge.tsx', 'instructions.tsx'];
  for (const f of editors) assert.match(src(f), /useEditBase/, f + ' saves through the guard');
  assert.match(src('editbase.tsx'), /if \(!stillOnModel\(startedOn\)\) return false;/, 'the one guard drops a Save whose model changed while it read');
  assert.match(src('bridge.ts'), /modelIdentity: pageModelIdentity\(\)/, 'the one bridge names the model on every request');
});

test('C2: every editor that closes or resets after a save asks whether the words it holds are still the words the engine took', () => {
  const SITES = [
    ['instructions.tsx', 'guard.saved(draft)'], ['knowledge.tsx', 'primerGuard.saved(draft, next.markdown ?? draft)'], ['knowledge.tsx', 'guard.saved(submitted)'],
    ['advmodels.tsx', 'guard.saved(expr)'], ['advmodels.tsx', 'fmtGuard.saved(raw)'], ['advmodels.tsx', 'guard.saved(filter)'], ['documentation.tsx', 'guard.saved(draft)'],
    ['spec.tsx', 'jsonGuard.saved(submittedJson'], ['rulesauthor.tsx', 'sameValue(formRef.current?.draft, sent)'], ['mcode.tsx', 'policyGuardRef.current.saved(sent'], ['connectionshub.tsx', 'sqlTypedSince.current'],
  ];
  const missing = SITES.filter(([file, marker]) => !src(file).includes(marker)).map(([file, marker]) => `${file}: ${marker}`);
  assert.deepEqual(missing, [], 'these editors would end a draft or close over words they never saved');
  // and none of them clears a draft under whatever model is open when the answer arrives
  assert.ok(!/draftStore\.clear\(draftModel\(\), scopeOf\(form\.editingId\)\)/.test(src('rulesauthor.tsx')), 'the rule form clears under the model it was opened on');
});
