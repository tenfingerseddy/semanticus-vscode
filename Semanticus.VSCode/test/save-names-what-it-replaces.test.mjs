// Sol join 6, C4: a Save from the Documentation section editor, the calculation-item editor and the row-filter editor names the value it
// replaces, and the ENGINE compares it inside its own mutation.
//
// WHAT WENT WRONG. The guard those editors use compared the page's old copy with the page's old start, because nothing read the live value at Save.
// The Documentation page reloads 300 ms after a change, so a Save inside that moment wrote the person's words over the assistant's newer section with
// no banner. The calculation-item and row-filter editors had the same guard and the same gap.
//
// The REAL components run (hooks driven by a tiny runtime), with a fake engine that does what LocalEngine.GuardExpectedValue does: a write whose
// expected value is neither the live value nor the new one is refused with "changed in the model after you opened it".
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

globalThis.window ??= globalThis;   // the components schedule with window.setTimeout

const walk = (node, visit) => {
  if (node == null || typeof node !== 'object') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node); walk(node.props?.children, visit);
};
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('');
const button = (tree, label) => find(tree, (el) => (typeof el.type === 'function' || el.type === 'button') && text(el) === label);
const hasBanner = (tree) => !!find(tree, (el) => el.type?.name === 'EditMovedBanner');
const REFUSAL = 'This value changed in the model after you opened it, so your version was not saved. Nothing was written.';
const guardedWrite = (engine, key, expression, expected) => {
  const live = engine[key] ?? '';
  if (expected != null && live !== expected && live !== expression) throw new Error(REFUSAL);
  engine[key] = expression;
};

const baseStubs = (rpc) => ({
  './bridge': { rpc, onDidChange: () => () => undefined, exportDoc: () => undefined, printDoc: () => undefined, openExternal: () => undefined },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null },
  './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
  './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) },
  './formatMode': { inferFormatMode: () => 'static' },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
  './activity': { useClaudeReflection: () => undefined },
});
const mountWith = (rpc, file, pick, props) => {
  const rt = makeHookRuntime();
  const mod = makeLoader(baseStubs(rpc), rt.React)(file);
  const h = rt.renderHook(() => mod[pick](props()));
  return { h, mod };
};

// 1. Sol's probe: the section moved and the page has not reloaded yet (its copy still says "Original words").
test('documentation: a Save inside the page\'s reload window reads the section and is held, not written over the assistant\'s words', async () => {
  const engine = { section: 'Original words' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getDocSection') return engine.section;
    if (method === 'setDocSection') guardedWrite(engine, 'section', args[2], args[4]);
  };
  const { h } = mountWith(rpc, 'documentation.tsx', 'SectionEditor', () => ({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'notes', value: 'Original words', onSaved: () => undefined }));
  await settle();
  find(h.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'Human draft' } }); await settle();
  engine.section = 'New assistant words';   // the reload has not come back yet
  await button(h.result, 'Save').props.onClick(); await settle();
  assert.equal(engine.section, 'New assistant words', 'the assistant\'s section stands');
  assert.ok(calls.some((c) => c[0] === 'getDocSection'), 'the section is read at Save');
  assert.ok(!calls.some((c) => c[0] === 'setDocSection'), 'nothing was sent over it');
  assert.ok(hasBanner(h.result), 'the person is asked');
});

// 2. The race inside the Save: the read still shows the old value, and the section moves before the write lands. The engine refuses and the banner appears.
test('documentation: a section that moves between the Save\'s read and its write is refused by the engine, and the person is asked', async () => {
  const engine = { section: 'Original words' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getDocSection') { const seen = engine.section; engine.section = 'New assistant words'; return seen; }   // moves right after the read
    if (method === 'setDocSection') guardedWrite(engine, 'section', args[2], args[4]);
  };
  const { h } = mountWith(rpc, 'documentation.tsx', 'SectionEditor', () => ({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'notes', value: 'Original words', onSaved: () => undefined }));
  await settle();
  find(h.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'Human draft' } }); await settle();
  await button(h.result, 'Save').props.onClick(); await settle(); await settle();
  const write = calls.find((c) => c[0] === 'setDocSection');
  assert.deepEqual(write?.slice(1, 6), ['table:Sales', 'notes', 'Human draft', 'human', 'Original words'], 'the write names the section it replaces');
  assert.equal(engine.section, 'New assistant words', 'the engine refused it');
  assert.ok(hasBanner(h.result), 'the page shows the choice instead of an error');
});

// 3. Nothing moved: the Save writes, naming what it replaces.
test('documentation: a Save of a section that did not move writes and names the value it replaces', async () => {
  const engine = { section: 'Original words' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getDocSection') return engine.section;
    if (method === 'setDocSection') guardedWrite(engine, 'section', args[2], args[4]);
  };
  const { h } = mountWith(rpc, 'documentation.tsx', 'SectionEditor', () => ({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'notes', value: 'Original words', onSaved: () => calls.push(['saved']) }));
  await settle();
  find(h.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'Human draft' } }); await settle();
  await button(h.result, 'Save').props.onClick(); await settle();
  assert.equal(engine.section, 'Human draft');
  assert.equal(calls.find((c) => c[0] === 'setDocSection')?.[5], 'Original words');
  assert.ok(calls.some((c) => c[0] === 'saved'));
});

// 4. Switching away (the unmount autosave) writes one call that names what it replaces.
test('documentation: switching away names the section it started from, and a refusal keeps the words', async () => {
  const engine = { section: 'Original words' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getDocSection') { const seen = engine.section; engine.section = 'New assistant words'; return seen; }   // moves right after the read
    if (method === 'setDocSection') guardedWrite(engine, 'section', args[2], args[4]);
  };
  const rt = makeHookRuntime();
  const load = makeLoader(baseStubs(rpc), rt.React);
  const mod = load('documentation.tsx');
  load('drafts').setDraftModel('/models/Sales.bim');   // typed words are held by the model's file (drafts.ts)
  const h = rt.renderHook(() => mod.SectionEditor({ objRef: 'table:Sales', objName: 'Sales', sectionKey: 'notes', value: 'Original words', onSaved: () => undefined }));
  await settle();
  find(h.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'Typed words' } }); await settle();
  rt.unmount(); await settle(); await settle();
  assert.equal(calls.find((c) => c[0] === 'setDocSection')?.[5], 'Original words', 'the autosave names the section it started from');
  assert.equal(engine.section, 'New assistant words', 'the engine refused it');
  assert.deepEqual(load('drafts').draftStore.get('/models/Sales.bim', 'docs|table:Sales|notes', ''), { value: 'Typed words', base: 'Original words' }, 'the typed words are kept for the person, over the words they started from');
});

// 5. The calculation-item editor.
test('calculation item: a Save names the expression it replaces; one that moved after the read is refused and asks', async () => {
  const engine = { expr: 'SELECTEDMEASURE()' };
  const calls = [];
  let moveAfterRead = false;
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getDax') { const seen = engine.expr; if (moveAfterRead) engine.expr = 'SELECTEDMEASURE() * 2'; return seen; }
    if (method === 'setDax') guardedWrite(engine, 'expr', args[1], args[5]);
  };
  const state = { item: { ref: 'calcitem:Time/YTD', name: 'YTD', ordinal: 1, expression: 'SELECTEDMEASURE()' } };
  const { h } = mountWith(rpc, 'advmodels.tsx', 'CalcItemRow', () => ({ item: state.item, reload: () => calls.push(['reload']), onError: (e) => calls.push(['error', String(e)]) }));
  await settle();
  const dax = () => find(h.result, (el) => el.type?.name === 'DaxField');
  dax().props.onChange('SELECTEDMEASURE() + 1'); dax().props.onValidity?.(true); await settle();
  moveAfterRead = true;
  button(h.result, 'Save DAX').props.onClick(); await settle(); await settle();
  assert.deepEqual(calls.find((c) => c[0] === 'setDax')?.slice(1), ['calcitem:Time/YTD', 'SELECTEDMEASURE() + 1', 'human', null, null, 'SELECTEDMEASURE()'], 'the write names the expression it replaces');
  assert.equal(engine.expr, 'SELECTEDMEASURE() * 2', 'the engine refused it');
  assert.ok(hasBanner(h.result), 'the person is asked');
  assert.ok(!calls.some((c) => c[0] === 'error'), 'not shown as an error');
});

// 6. The row-filter editor.
test('row filter: a Save names the filter it replaces; one that moved after the read is refused and asks', async () => {
  const engine = { filter: 'TRUE()' };
  const calls = [];
  let moveAfterRead = false;
  const role = { name: 'Readers', modelPermission: 'Read', tableFilters: [{ table: 'Sales', filterExpression: 'TRUE()' }], members: [] };
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'listRoles') { const seen = engine.filter; if (moveAfterRead) engine.filter = 'FALSE()'; return [{ ...role, tableFilters: [{ table: 'Sales', filterExpression: seen }] }]; }
    if (method === 'setTablePermission') guardedWrite(engine, 'filter', args[2], args[4]);
  };
  const table = { name: 'Sales', ref: 'table:Sales', columns: [], isCalculationGroup: false };
  const { h } = mountWith(rpc, 'advmodels.tsx', 'TableSecurityRow', () => ({ role, table, reload: () => calls.push(['reload']), onError: (e) => calls.push(['error', String(e)]) }));
  await settle();
  const dax = () => find(h.result, (el) => el.type?.name === 'DaxField');
  dax().props.onChange('1 = 1'); dax().props.onValidity?.(true, 0); await settle();
  const save = button(h.result, 'Save filter');
  assert.ok(save, 'the row has a Save button');
  moveAfterRead = true;
  save.props.onClick(); await settle(); await settle();
  assert.deepEqual(calls.find((c) => c[0] === 'setTablePermission')?.slice(1), ['Readers', 'table:Sales', '1 = 1', 'human', 'TRUE()'], 'the write names the filter it replaces');
  assert.equal(engine.filter, 'FALSE()', 'the engine refused it');
  assert.ok(hasBanner(h.result), 'the person is asked');
  assert.ok(!calls.some((c) => c[0] === 'error'), 'not shown as an error');
});
