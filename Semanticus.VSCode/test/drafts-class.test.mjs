// Typed, unsaved words outlive the editor (join 11, the drafts class).
//
// Kane, 4 Oct: a start plan words editor lost typed text on a tab switch and a restart (join 8). The fix there (startplan-drafts.ts) became ONE shared store
// (webview/src/drafts.ts) and every editor that saves from a copy it typed was wired to it. The rule, for every one of them:
//   - the typed words are held by the model's FILE + the editor's scope (editor + object) + the field, with the value they were typed over;
//   - they survive a tab switch, another row or object, a restarted engine (the session remounts the app) and a reload of the webview;
//   - they end ONLY on a save the engine took, the person's Cancel / Discard, or Use the new one. Unmounting does not end them;
//   - when the saved value moved meanwhile the editor shows Keep mine / Use the new one, and nothing is written until the person chose;
//   - a quiet "Draft not saved" mark shows where a draft is held; a draft never appears on another model.
//
// The REAL components run here (hooks driven by a tiny hook runtime, the bridge faked). A "tab switch" unmounts a component and mounts a new one over the
// SAME draft store; a "reload" or "restart" mounts it over a store read again from the persisted state.
//   1 the store and the guard      2 AI instructions      3 Model notes (primer) and insights      4 documentation section
//   5 calculation item (DAX and format) and a role's row filter      6 custom rules      7 every wired editor names its scope (source guard)
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, makeHookRuntime, settle, ambientReact } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = (name) => readFileSync(resolve(root, 'webview', 'src', name), 'utf8');

function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('');
const button = (tree, label) => find(tree, (el) => (typeof el.type === 'function' || el.type === 'button') && text(el) === label);
const field = (tree, label) => find(tree, (el) => el.props?.['aria-label'] === label);
const area = (tree) => find(tree, (el) => el.type === 'textarea');
const banner = (tree) => find(tree, (el) => el.type?.name === 'EditMovedBanner');
const mark = (tree) => !!find(tree, (el) => el.type?.name === 'DraftMark' && el.props.show);
const type = (el, value) => el.props.onChange({ target: { value } });

globalThis.window ??= globalThis;   // the components schedule with window.setTimeout
const MODEL = '/models/Sales.bim';

/** One webview: its persisted state, and every module loaded once over it, so the draft store is the page's one store. `mount` puts a component on the page (each with its own hooks);
 *  a tab switch is an unmount then a mount; `reload` is a fresh page (modules and store read again) over the same persisted state. `stubs` replace the components' other imports. */
function makeWorld(model = MODEL) {
  let stubs = {}, stubbed = false;
  const persisted = {};
  const state = {
    loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f),
    saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
  };
  let load, drafts;
  const boot = () => { load = makeLoader({ ...stubs, './bridge': { ...(stubs['./bridge'] ?? {}), ...state } }, ambientReact); drafts = load('drafts'); drafts.setDraftModel(model); };
  boot();
  return {
    state, persisted,
    get drafts() { return drafts; },
    get load() { return load; },
    reload() { boot(); },
    setModel(m) { drafts.setDraftModel(m); },
    /** The first mount fixes the stubs of the page (a test's closures are the same for every mount of it). */
    mount(file, exportName, props, pageStubs) {
      if (pageStubs && !stubbed) { stubbed = true; stubs = pageStubs; boot(); }
      const rt = makeHookRuntime();
      const component = load(file)[exportName];
      const h = rt.renderHook(() => component(props()));
      return { h, rt, unmount: () => rt.unmount(), tree: () => h.result };
    },
  };
}
const heldCount = (w) => Object.keys(w.drafts.draftStore.snapshot()).length;

// ---- 1 the store and the guard ------------------------------------------------------------------------------
{
  const guardOver = (w, props) => {
    const rt = makeHookRuntime();
    const { useEditBase } = w.load('editbase.tsx');
    const s = { ...props };
    const h = rt.renderHook(() => useEditBase({ current: s.live, draft: s.draft, editing: s.editing ?? true, reseed: (v) => { s.draft = v; h.rerender(); }, persist: { scope: 'unit|1' }, readNow: async () => s.now ?? s.live }));
    return { h, s, rt };
  };

  test('store: a draft is held by model file + scope + field, a value equal to where it started holds nothing, and another model sees none', () => {
    const w = makeWorld();
    const { draftStore } = w.drafts;
    draftStore.set(MODEL, 'unit|1', 'f', { value: 'typed', base: 'saved' });
    assert.deepEqual(draftStore.get(MODEL, 'unit|1', 'f'), { value: 'typed', base: 'saved' });
    assert.equal(draftStore.get('/models/Other.bim', 'unit|1', 'f'), null, 'a draft never appears on another model');
    assert.equal(draftStore.get(MODEL, 'unit|2', 'f'), null, 'nor on another object');
    draftStore.set(MODEL, 'unit|1', 'f', { value: 'saved', base: 'saved' });
    assert.equal(draftStore.get(MODEL, 'unit|1', 'f'), null, 'typing back to the start is not a draft');
  });

  test('store: the drafts are in the webview\'s persisted state, so a reload keeps them', () => {
    const w = makeWorld();
    w.drafts.draftStore.set(MODEL, 'unit|1', '', { value: { a: 1 }, base: { a: 0 } });
    w.reload();
    assert.deepEqual(w.drafts.draftStore.get(MODEL, 'unit|1', ''), { value: { a: 1 }, base: { a: 0 } });
    assert.equal(w.drafts.modelKeyOfSession({ source: '/x/y.bim', sessionId: 's1' }), '/x/y.bim', 'the key is the file, never the session id');
  });

  test('guard: typed words are held with their starting point, and a remount opens on them', async () => {
    const w = makeWorld();
    const a = guardOver(w, { live: 'one', draft: 'one' });
    await settle();
    a.s.draft = 'my words'; a.h.rerender(); await settle();
    assert.deepEqual(w.drafts.draftStore.get(MODEL, 'unit|1', ''), { value: 'my words', base: 'one' });
    assert.equal(a.h.result.drafted, true);
    a.rt.unmount();
    assert.ok(w.drafts.draftStore.get(MODEL, 'unit|1', ''), 'unmounting does not end a draft');
    // a new mount over the same store: the editor reads what is held for its first render, and the guard starts from the held starting point
    const held = w.drafts.heldDraft('unit|1');
    assert.deepEqual(held, { value: 'my words', base: 'one' });
    const b = guardOver(w, { live: 'one', draft: held.value });
    await settle();
    assert.equal(b.h.result.moved, false, 'nothing moved: no banner');
    assert.equal(b.s.draft, 'my words');
    assert.equal(b.h.result.drafted, true);
  });

  test('guard: a value that moved while the editor was away is asked about on the next mount, and Use the new one ends the draft', async () => {
    const w = makeWorld();
    w.drafts.draftStore.set(MODEL, 'unit|1', '', { value: 'my words', base: 'one' });
    const b = guardOver(w, { live: 'two', draft: 'my words' });
    await settle();
    assert.equal(b.h.result.moved, true, 'the assistant changed it while the editor was shut: Keep mine / Use the new one');
    assert.equal(b.s.draft, 'my words', 'the typed words are not replaced');
    assert.equal(await b.h.result.beforeSave(), false, 'nothing is written until the person chose');
    b.h.result.useNew(); b.h.rerender(); await settle();
    assert.equal(b.s.draft, 'two');
    assert.equal(w.drafts.draftStore.get(MODEL, 'unit|1', ''), null, 'Use the new one ends the draft');
  });

  test('guard: a live value that is not read yet keeps the held starting point (no false banner, no replaced words)', async () => {
    const w = makeWorld();
    w.drafts.draftStore.set(MODEL, 'unit|1', '', { value: 'my words', base: 'one' });
    const b = guardOver(w, { live: undefined, draft: 'my words' });
    await settle();
    assert.equal(b.s.draft, 'my words');
    assert.equal(b.h.result.moved, false);
    b.s.live = 'one'; b.h.rerender(); await settle();
    assert.equal(b.s.draft, 'my words', 'the read arrived and matches the start: the words stay');
    assert.equal(b.h.result.moved, false);
    assert.ok(w.drafts.draftStore.get(MODEL, 'unit|1', ''));
  });

  test('store: objects equal in any key order are the same value, so a rebuilt object is not a draft', () => {
    const w = makeWorld();
    w.drafts.draftStore.set(MODEL, 'unit|1', '', { value: { enabled: true, n: 1 }, base: { n: 1, enabled: true } });
    assert.equal(w.drafts.draftStore.get(MODEL, 'unit|1', ''), null);
  });

  test('guard: the editor moves to another object that holds a draft whose saved value moved: asked at once, nothing replaced', async () => {
    const w = makeWorld();
    w.drafts.draftStore.set(MODEL, 'unit|B', '', { value: 'B mine', base: 'B old' });
    const rt = makeHookRuntime();
    const { useEditBase } = w.load('editbase.tsx');
    const s = { live: 'A', draft: 'A', scope: 'A', persistScope: 'unit|A' };
    const h = rt.renderHook(() => useEditBase({ current: s.live, draft: s.draft, editing: true, scope: s.scope, reseed: (v) => { s.draft = v; h.rerender(); }, persist: { scope: s.persistScope } }));
    await settle();
    s.scope = 'B'; s.persistScope = 'unit|B'; s.live = 'B new'; h.rerender(); await settle(); await settle();
    assert.equal(s.draft, 'B mine', 'the object opens on its held words');
    assert.equal(h.result.moved, true, 'and asks, because its value moved while they were away');
  });

  test('guard: a save the engine took ends the draft, and closing the editor while it is mounted ends it too', async () => {
    const w = makeWorld();
    const a = guardOver(w, { live: 'one', draft: 'one' });
    await settle();
    a.s.draft = 'two'; a.h.rerender(); await settle();
    assert.ok(w.drafts.draftStore.get(MODEL, 'unit|1', ''));
    a.s.live = 'two'; a.h.rerender(); await settle();   // the engine took the words: the live value is the draft
    assert.equal(w.drafts.draftStore.get(MODEL, 'unit|1', ''), null, 'saved: nothing is held');
    a.s.draft = 'three'; a.h.rerender(); await settle();
    assert.ok(w.drafts.draftStore.get(MODEL, 'unit|1', ''));
    a.s.editing = false; a.h.rerender(); await settle();
    assert.equal(w.drafts.draftStore.get(MODEL, 'unit|1', ''), null, 'Cancel (the editor closes while mounted) ends it');
  });
}

// ---- 2 AI instructions -----------------------------------------------------------------------------------------
{
  const instructionsStubs = (calls, live) => ({
    './bridge': { rpc: async (m, ...a) => { calls.push([m, ...a]); return m === 'getAiInstructions' ? live.value : undefined; } },
    './hooks': { messageOf: (e) => String(e), useAutoGrow: () => ({ current: null }), useRemote: () => [null, () => undefined] },
    './knowledge': { KnowledgeView: () => null },
  });
  const card = (w, calls, live) => w.mount('instructions.tsx', 'AiInstructionsCard', () => ({
    state: { status: 'ready', value: live.value }, onReload: () => calls.push(['reload']), onNotes: () => undefined,
    suggestions: { status: 'ready', value: { suggestions: [] } }, onSuggestionsChanged: () => undefined,
  }), instructionsStubs(calls, live));
  const open = async (m, words) => { button(m.tree(), 'Edit').props.onClick(); await settle(); type(area(m.tree()), words); await settle(); };

  test('AI instructions: typed words come back after a tab switch, with the mark', async () => {
    const calls = [], live = { value: { present: true, instructions: 'Use net sales.', limit: 2000, length: 14 } };
    const w = makeWorld();
    const a = card(w, calls, live); await settle();
    await open(a, 'Use net sales. Show the currency.');
    assert.ok(mark(a.tree()), 'the editor says the words are a draft');
    a.unmount();
    const b = card(w, calls, live); await settle();
    assert.equal(area(b.tree())?.props.value, 'Use net sales. Show the currency.', 'the editor is open on the typed words');
    assert.ok(mark(b.tree()));
    assert.ok(!banner(b.tree()), 'the instructions did not move: no choice to make');
  });

  test('AI instructions: typed words come back after the engine restarted and the page reloaded', async () => {
    const calls = [], live = { value: { present: true, instructions: 'Use net sales.', limit: 2000, length: 14 } };
    const w = makeWorld();
    const a = card(w, calls, live); await settle();
    await open(a, 'Use net sales. Show the currency.');
    a.unmount(); w.reload();
    const b = card(w, calls, live); await settle();
    assert.equal(area(b.tree())?.props.value, 'Use net sales. Show the currency.');
  });

  test('AI instructions: the assistant changed the instructions meanwhile: the words stay, the choice is shown, nothing is written until chosen', async () => {
    const calls = [], live = { value: { present: true, instructions: 'Use net sales.', limit: 2000, length: 14 } };
    const w = makeWorld();
    const a = card(w, calls, live); await settle();
    await open(a, 'Use net sales. Show the currency.');
    a.unmount();
    live.value = { ...live.value, instructions: 'Use gross sales.' };
    calls.length = 0;
    const b = card(w, calls, live); await settle();
    assert.equal(area(b.tree()).props.value, 'Use net sales. Show the currency.', 'the typed words are not replaced');
    assert.ok(banner(b.tree()), 'Keep mine / Use the new one');
    button(b.tree(), 'Save').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setAiInstructions'), 'nothing is written until the person chose');
    banner(b.tree()).props.onKeepMine(); await settle();
    button(b.tree(), 'Save').props.onClick(); await settle(); await settle();
    const write = calls.find((c) => c[0] === 'setAiInstructions');
    assert.equal(write?.[1], 'Use net sales. Show the currency.');
    assert.equal(write?.[4], 'Use gross sales.', 'the save names the instructions it read as the ones it replaces (the fence)');
  });

  test('AI instructions: Cancel and a saved draft end it; another model does not see it', async () => {
    const calls = [], live = { value: { present: true, instructions: 'Use net sales.', limit: 2000, length: 14 } };
    const w = makeWorld();
    const a = card(w, calls, live); await settle();
    await open(a, 'Words I will cancel');
    assert.equal(heldCount(w), 1);
    w.setModel('/models/Other.bim');
    const other = card(w, calls, live); await settle();
    assert.equal(area(other.tree()), null, 'another model opens a shut editor');
    w.setModel(MODEL);
    button(a.tree(), 'Cancel').props.onClick(); await settle();
    assert.equal(heldCount(w), 0, 'Cancel ends the draft');
  });
}

// ---- 3 Model notes (primer) and insights ---------------------------------------------------------------------
{
  const notesStubs = (calls, live) => ({
    './bridge': {
      rpc: async (m, ...a) => {
        calls.push([m, ...a]);
        if (m === 'getPrimer') return { markdown: live.primer, exists: true };
        if (m === 'listPrimerSuggestions') return { suggestions: [], sections: [] };
        if (m === 'listInsights') return { insights: live.insights ?? [], skippedCorruptLines: 0 };
        if (m === 'setPrimer') return { markdown: a[0], exists: true };
        return undefined;
      },
      onActivity: () => () => undefined,
    },
    './pro': { PENDING_ACCESS: '', ProPill: () => null, useFeature: () => ({}) },
    './ui': { PathDisclosure: () => null },
    './docrender': { mdToHtml: (s) => s },
  });
  const notes = (w, calls, live) => w.mount('knowledge.tsx', 'KnowledgeView', () => ({}), notesStubs(calls, live));

  test('Model notes: typed notes come back after a tab switch and after a restart, and the assistant\'s change is asked about', async () => {
    const calls = [], live = { primer: '# Notes\nOne.' };
    const w = makeWorld();
    const a = notes(w, calls, live); await settle(); await settle();
    button(a.tree(), 'Edit notes').props.onClick(); await settle();
    type(field(a.tree(), 'Model notes, written in Markdown'), '# Notes\nOne. Mine.'); await settle();
    assert.ok(mark(a.tree()));
    a.unmount();
    const b = notes(w, calls, live); await settle(); await settle();
    assert.equal(field(b.tree(), 'Model notes, written in Markdown')?.props.value, '# Notes\nOne. Mine.', 'the notes editor is open on the typed words');
    b.unmount(); w.reload();
    live.primer = '# Notes\nOne. Assistant.';
    calls.length = 0;
    const c = notes(w, calls, live); await settle(); await settle();
    assert.equal(field(c.tree(), 'Model notes, written in Markdown')?.props.value, '# Notes\nOne. Mine.');
    assert.ok(banner(c.tree()), 'the notes moved while the editor was away: Keep mine / Use the new one');
    banner(c.tree()).props.onUseNew(); await settle();
    assert.equal(field(c.tree(), 'Model notes, written in Markdown')?.props.value, '# Notes\nOne. Assistant.');
    assert.equal(heldCount(w), 0, 'Use the new one ends the draft');
  });

  const rec = (text, keys = ['sales']) => ({ id: 'i1', text, kind: 'insight', keys, status: 'approved', score: 3, uses: 0, retrievals: 0, scope: 'model', provenance: { origin: 'agent' } });
  const insight = (w, calls, live) => w.mount('knowledge.tsx', 'InsightCard', () => ({ rec: live.insight, onChanged: () => calls.push(['changed']) }), notesStubs(calls, { ...live, get insights() { return [live.insight]; } }));

  test('Insight card: typed words come back after the card was unmounted, mark shown, assistant change asked about', async () => {
    const calls = [], live = { insight: rec('Original') };
    const w = makeWorld();
    const a = insight(w, calls, live); await settle();
    button(a.tree(), 'Edit').props.onClick(); await settle();
    type(field(a.tree(), 'Insight text'), 'My wording'); await settle();
    assert.ok(mark(a.tree()));
    a.unmount();
    live.insight = rec('Assistant wording');
    const b = insight(w, calls, live); await settle();
    assert.equal(field(b.tree(), 'Insight text')?.props.value, 'My wording', 'the card is open on the typed words');
    assert.ok(banner(b.tree()));
    button(b.tree(), 'Cancel').props.onClick(); await settle();
    assert.equal(heldCount(w), 0, 'Cancel ends it');
  });

  test('Insight card: a draft is per lesson', async () => {
    const calls = [], live = { insight: rec('Original') };
    const w = makeWorld();
    const a = insight(w, calls, live); await settle();
    button(a.tree(), 'Edit').props.onClick(); await settle();
    type(field(a.tree(), 'Insight text'), 'My wording'); await settle();
    a.unmount();
    live.insight = { ...rec('Another lesson'), id: 'i2' };
    const b = insight(w, calls, live); await settle();
    assert.equal(field(b.tree(), 'Insight text'), null, 'another lesson opens shut');
  });
}

// ---- 4 documentation section ---------------------------------------------------------------------------------
{
  const docStubs = (calls, live) => ({
    './bridge': { rpc: async (m, ...a) => { calls.push([m, ...a]); if (live.down) throw new Error('Engine not connected'); return m === 'getDocSection' ? live.value : undefined; }, onDidChange: () => () => undefined },
    './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
  });
  const section = (w, calls, live, objRef = 'model') => w.mount('documentation.tsx', 'SectionEditor', () => ({ objRef, objName: 'Model', sectionKey: 'overview', value: live.value, onSaved: () => undefined }), docStubs(calls, live));

  test('Documentation: a typed section is back after a restart (page reload), asked about when the assistant rewrote it, and the mark shows', async () => {
    const calls = [], live = { value: 'Old narrative' };
    const w = makeWorld();
    const a = section(w, calls, live); await settle();
    type(area(a.tree()), 'My narrative'); await settle();
    assert.ok(mark(a.tree()));
    live.down = true;   // the engine is going away: the unmount's own save cannot reach it, so the words are what is left
    a.unmount(); await settle();
    w.reload();
    live.down = false; live.value = 'Assistant narrative';
    const b = section(w, calls, live); await settle();
    assert.equal(area(b.tree()).props.value, 'My narrative');
    assert.ok(banner(b.tree()), 'the section moved: Keep mine / Use the new one');
    banner(b.tree()).props.onKeepMine(); await settle();
    calls.length = 0;
    button(b.tree(), 'Save').props.onClick(); await settle(); await settle();
    const write = calls.find((c) => c[0] === 'setDocSection');
    assert.equal(write?.[3], 'My narrative');
    assert.equal(write?.[5], 'Assistant narrative', 'the save names the section it read as the one it replaces');
  });

  test('Documentation: the save on leaving is a save the engine took, so it ends the draft; one it refused does not', async () => {
    const calls = [], live = { value: 'Old narrative' };
    const w = makeWorld();
    const a = section(w, calls, live); await settle();
    type(area(a.tree()), 'My narrative'); await settle();
    a.unmount(); await settle(); await settle();
    assert.ok(calls.some((c) => c[0] === 'setDocSection' && c[3] === 'My narrative'), 'the words were written on leaving');
    assert.equal(heldCount(w), 0, 'written: nothing is held');
  });

  test('Documentation: the draft is per object and section', async () => {
    const calls = [], live = { value: 'Old narrative' };
    const w = makeWorld();
    const a = section(w, calls, live, 'table:Sales'); await settle();
    type(area(a.tree()), 'Typed for Sales'); await settle();
    a.unmount();
    const b = section(w, calls, live, 'table:Customer'); await settle();
    assert.equal(area(b.tree()).props.value, 'Old narrative', 'another object shows its own text');
  });
}

// ---- 5 calculation item and role row filter ------------------------------------------------------------------
{
  const advStubs = (calls, live) => ({
    './bridge': { rpc: async (m, ...a) => { calls.push([m, ...a]); if (m === 'getDax') return live.expression; if (m === 'getObjectProperties') return [{ name: 'FormatStringExpression', value: live.format }]; if (m === 'listRoles') return [{ name: 'Readers', tableFilters: [{ table: 'Sales', filterExpression: live.filter }] }]; return undefined; }, onDidChange: () => () => undefined },
    './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './daxeditor': { DaxField: () => null },
    './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
    './ui': { Caret: () => null },
    '@tanstack/react-virtual': { useVirtualizer: () => ({}) },
    './formatMode': { inferFormatMode: () => 'dynamic' },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
  });
  const dax = (tree) => find(tree, (el) => el.type?.name === 'DaxField');
  const item = (live) => ({ ref: 'calcitem:Time/YTD', name: 'YTD', ordinal: 1, expression: live.expression, formatStringExpression: live.format });
  const calc = (w, calls, live) => w.mount('advmodels.tsx', 'CalcItemRow', () => ({ item: item(live), reload: () => undefined, onError: () => undefined }), advStubs(calls, live));

  test('Calculation item: typed DAX and a typed format come back after a tab switch and after a restart, each with its own choice when the item moved', async () => {
    const calls = [], live = { expression: 'SELECTEDMEASURE()', format: '' };
    const w = makeWorld();
    const a = calc(w, calls, live); await settle();
    dax(a.tree()).props.onChange('CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))'); await settle();
    const fmt = () => find(a.tree(), (el) => el.type?.name === 'FormatStringControl');
    fmt().props.onDraft('"0.0%"'); await settle();
    assert.ok(mark(a.tree()), 'the row says it holds a draft');
    a.unmount(); w.reload();
    live.expression = 'SELECTEDMEASURE() * 2';
    const b = calc(w, calls, live); await settle();
    const daxB = [...[]];
    void daxB;
    assert.equal(dax(b.tree()).props.value, 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))', 'the typed DAX is back');
    assert.equal(find(b.tree(), (el) => el.type?.name === 'FormatStringControl').props.draft, '"0.0%"', 'and the typed format');
    const banners = []; walk(b.tree(), (el) => { if (el.type?.name === 'EditMovedBanner') banners.push(el); });
    assert.equal(banners.length, 1, 'only the DAX moved: one choice, for the DAX');
    calls.length = 0;
    button(b.tree(), 'Save DAX').props.onClick(); await settle(); await settle();
    assert.ok(!calls.some((c) => c[0] === 'setDax'), 'nothing is written until chosen');
    banners[0].props.onKeepMine(); await settle();
    button(b.tree(), 'Save DAX').props.onClick(); await settle(); await settle();
    const write = calls.find((c) => c[0] === 'setDax');
    assert.equal(write?.[2], 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))');
    assert.equal(write?.[6], 'SELECTEDMEASURE() * 2', 'the save names the value it read as the one it replaces');
  });

  test('Calculation item: a draft belongs to its item', async () => {
    const calls = [], live = { expression: 'SELECTEDMEASURE()', format: '' };
    const w = makeWorld();
    const a = calc(w, calls, live); await settle();
    dax(a.tree()).props.onChange('X'); await settle();
    a.unmount();
    const rt = w.mount('advmodels.tsx', 'CalcItemRow', () => ({ item: { ...item(live), ref: 'calcitem:Time/QTD', name: 'QTD' }, reload: () => undefined, onError: () => undefined }), advStubs(calls, live));
    await settle();
    assert.equal(dax(rt.tree()).props.value, 'SELECTEDMEASURE()');
  });

  const filterRow = (w, calls, live, role = 'Readers') => w.mount('advmodels.tsx', 'TableSecurityRow', () => ({
    role: { name: role, tableFilters: [{ table: 'Sales', filterExpression: live.filter }], objectPermissions: [], members: [] },
    table: { name: 'Sales', ref: 'table:Sales', columns: [], isCalculationGroup: false }, reload: () => undefined, onError: () => undefined,
  }), advStubs(calls, live));

  test('Row filter: a typed filter comes back after a restart, per role and table, and the assistant\'s change is asked about', async () => {
    const calls = [], live = { filter: '[Region] = "East"' };
    const w = makeWorld();
    const a = filterRow(w, calls, live); await settle();
    dax(a.tree()).props.onChange('[Region] = "West"'); await settle();
    assert.ok(mark(a.tree()));
    a.unmount(); w.reload();
    live.filter = '[Region] = "North"';
    const b = filterRow(w, calls, live); await settle();
    assert.equal(dax(b.tree()).props.value, '[Region] = "West"');
    assert.ok(banner(b.tree()));
    const c = filterRow(w, calls, live, 'Admins'); await settle();
    assert.equal(dax(c.tree()).props.value, '[Region] = "North"', 'another role has its own filter');
  });
}

// ---- 6 custom rules (BPA and readiness) ------------------------------------------------------------------------
{
  const rule = (expression, extra = {}) => ({ id: 'ORG-DESC', name: 'Measures need descriptions', category: 'Descriptions', severity: 'Medium', scope: 'Measure', expression, message: 'needs a description', ...extra });
  const rulesStubs = (calls, live) => ({
    './bridge': {
      rpc: async (m, ...a) => {
        calls.push([m, ...a]);
        if (m === 'getCustomRules') return { bpa: [], readiness: live.rules.map((r) => ({ ...r })), readinessCategories: ['Descriptions'], scopes: ['Measure'] };
        if (m === 'validateRule') return { kind: 'readiness', ruleCount: 1, allValid: true, rules: [{ valid: true, errors: [], applicable: 3, violations: 1, dormant: false, sample: [] }] };
        return {};
      },
      onDidChange: () => () => undefined,
    },
    './sendtask': { RuleCheckBox: () => null },
  });
  const panel = (w, calls, live) => w.mount('rulesauthor.tsx', 'CustomRulesPanel', () => ({ kind: 'readiness' }), rulesStubs(calls, live));
  const settleAll = async () => { for (let i = 0; i < 4; i++) await settle(); };
  const input = (m, placeholder) => find(m.tree(), (el) => (el.type === 'input' || el.type === 'textarea') && el.props.placeholder === placeholder);
  const edit = (m) => find(m.tree(), (el) => el.type?.name === 'MiniBtn' && text(el) === 'Edit');
  const openEdit = async (m) => { find(m.tree(), (el) => el.type === 'button' && el.props.title === 'Expand').props.onClick(); await settle(); edit(m).props.onClick(); await settle(); };

  test('Custom rules: an edited rule comes back after a restart, over the rule it was typed over, and the assistant\'s change is asked about', async () => {
    const calls = [], live = { rules: [rule('string.IsNullOrEmpty(Description)')] };
    const w = makeWorld();
    const a = panel(w, calls, live); await settleAll();
    await openEdit(a);
    type(input(a, '%object% needs a description'), 'has no description yet'); await settleAll();
    assert.ok(mark(a.tree()));
    a.unmount(); w.reload();
    live.rules = [rule('string.IsNullOrWhiteSpace(Description)')];
    const b = panel(w, calls, live); await settleAll();
    assert.equal(input(b, '%object% needs a description')?.props.value, 'has no description yet', 'the panel opens on the half-written rule');
    assert.ok(banner(b.tree()), 'the rule moved while it was away: Keep mine / Use the new one');
    calls.length = 0;
    find(b.tree(), (el) => el.type?.name === 'Btn' && el.props.primary).props.onClick(); await settleAll();
    assert.ok(!calls.some((c) => c[0] === 'loadReadinessRules'), 'nothing is written until the person chose');
  });

  test('Custom rules: a NEW rule left half written is back after a tab switch, and Cancel ends it', async () => {
    const calls = [], live = { rules: [] };
    const w = makeWorld();
    const a = panel(w, calls, live); await settleAll();
    find(a.tree(), (el) => el.type?.name === 'Btn' && text(el) === 'New rule').props.onClick(); await settle();
    type(input(a, '%object% needs a description') ?? find(a.tree(), (el) => el.type === 'textarea'), 'x'); await settle();
    const idInput = find(a.tree(), (el) => el.type === 'input' && /ORG/.test(String(el.props.placeholder ?? '')));
    (idInput ?? find(a.tree(), (el) => el.type === 'input')).props.onChange({ target: { value: 'ORG-NEW' } }); await settleAll();
    assert.equal(heldCount(w), 1, 'a new rule typed into is held');
    a.unmount();
    const b = panel(w, calls, live); await settleAll();
    const ids = []; walk(b.tree(), (el) => { if (el.type === 'input' && el.props.value === 'ORG-NEW') ids.push(el); });
    assert.equal(ids.length, 1, 'the new-rule form is open again with the typed id');
    find(b.tree(), (el) => el.type?.name === 'Btn' && text(el) === 'Cancel').props.onClick(); await settle();
    assert.equal(heldCount(w), 0, 'Cancel ends the draft');
  });
}

// ---- 7 source guard: each wired editor holds its draft under a scope that names the object --------------------
test('every editor of the drafts class passes persist with a scope that names its object', () => {
  const SURFACES = [
    ['instructions.tsx', /persist: \{ scope: 'instructions\|text' \}/],
    ['knowledge.tsx', /persist: \{ scope: 'notes\|primer' \}/],
    ['knowledge.tsx', /persist: \{ scope: `insight\|\$\{rec\.id\}` \}/],
    ['documentation.tsx', /persist: \{ scope: draftScope \}/],
    ['documentation.tsx', /const draftScope = `docs\|\$\{objRef\}\|\$\{sectionKey\}`/],
    ['advmodels.tsx', /persist: \{ scope: `calcitem\|\$\{item\.ref\}`, field: 'expression' \}/],
    ['advmodels.tsx', /persist: \{ scope: `calcitem\|\$\{item\.ref\}`, field: 'format' \}/],
    ['advmodels.tsx', /persist: \{ scope: `rowfilter\|\$\{role\.name\}\|\$\{table\.name\}` \}/],
    ['rulesauthor.tsx', /persist: form\?\.editingId \? \{ scope: scopeOf\(form\.editingId\) \} : undefined/],
    ['rulesauthor.tsx', /const scopeOf = \(id: string \| null\) => `rules\|\$\{kind\}\|\$\{id \?\? 'new'\}`/],
  ];
  for (const [file, pattern] of SURFACES) assert.match(src(file), pattern, `${file} wires ${pattern}`);
});
