// Sol join 6, round 3 (D2): the data agent Teach panel names the instructions its box was seeded with, and when the agent's draft instructions move under typed words it
// keeps the words and offers Keep mine / Use the new one (the editbase shape, kept local because this panel is loaded by tests with a fixed set of stubs).
// Runs the real dataagent.tsx with its imports stubbed. The engine side (the compare inside the write) is Semanticus.Tests/DataAgentTests.cs.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req, makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';
import * as schemaLib from '../webview/src/dataagent-schema.mjs';

const ts = req('typescript');
const draftModules = (React) => { const real = makeLoader({ './bridge': { loadState: (k, d) => d, saveState: () => undefined } }, React); return { './drafts': real('drafts.ts'), './editbase': real('editbase.tsx') }; };
const walk = (node, visit) => {
  if (node == null || typeof node !== 'object') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node); walk(node.props?.children, visit);
};
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const findAll = (tree, pred) => { const hits = []; walk(tree, (el) => { if (pred(el)) hits.push(el); }); return hits; };

function loadPanels(rpc, React, schema = null) {
  const src = readFileSync(join(webview, 'src', 'dataagent.tsx'), 'utf8') + '\nexport { TeachPanel, FewShotEditor, SourceCard };\n';
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const Button = (props) => props;
  const stubs = {
    './bridge': { rpc, copyText: () => undefined },
    './hooks': { usePersistedState: (k, v) => [v, () => undefined] },
    './workflows': { Button },
    './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
    './connection': { useConnection: () => ({}) },
    './accountpicker': { AccountPicker: () => null },
    './dataagent-schema.mjs': schema ?? { isEditableElementTree: () => false, parseElementTree: () => [], updateElementSelection: (x) => x },
    './copy': { uiLabel: (s) => s },
    // join 11: the Teach editors hold their drafts in the shared store (no model is set here, so nothing is held)
    ...draftModules(React),
  };
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? React : req(n)), module, module.exports);
  return { ...module.exports, Button };
}

function mount(initial) {
  const calls = [];
  const rpc = async (method, ...args) => { calls.push([method, ...args]); return method === 'updateDataAgent' ? (rpc.reply ?? { status: 'ok' }) : undefined; };
  const rt = makeHookRuntime();
  const { TeachPanel, FewShotEditor, Button } = loadPanels(rpc, rt.React);
  const state = { detail: { info: { id: 'agent-1' }, draft: { aiInstructions: initial, dataSources: [] } } };
  let reloads = 0;
  const h = rt.renderHook(() => TeachPanel({ workspaceId: 'ws', agentId: 'agent-1', detail: state.detail, tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => { reloads++; } }));
  const box = () => find(h.result, (el) => el.type === 'textarea');
  const flow = () => find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save instructions…');
  const button = (label) => findAll(h.result, (el) => el.type === Button && el.props.children === label)[0];
  const type = async (value) => { box().props.onChange({ target: { value } }); await settle(); };
  const agentNow = async (text) => { state.detail = { info: { id: 'agent-1' }, draft: { aiInstructions: text, dataSources: [] } }; h.rerender(); await settle(); };
  return { rt, h, calls, rpc, box, flow, button, type, agentNow, reloads: () => reloads, FewShotEditor, Button };
}
const lastUpdate = (calls) => calls.filter((c) => c[0] === 'updateDataAgent').at(-1);

test('Teach: a Save names the instructions the box was seeded with', async () => {
  const m = mount('Use Sales Amount.');
  await m.type('Use Sales Amount. Fiscal year starts in July.');
  await m.flow().props.run(true);
  const c = lastUpdate(m.calls);
  assert.equal(c[3], 'Use Sales Amount. Fiscal year starts in July.');
  assert.equal(c[11], 'instructions');
  assert.equal(c[12], 'Use Sales Amount.');
});

test('Teach: untyped box follows the agent; typed box keeps the words and offers the choice; Save waits', async () => {
  const m = mount('One.');
  await m.agentNow('Two.');
  assert.equal(m.box().props.value, 'Two.', 'an untouched box follows the agent');
  assert.equal(m.button('Keep mine'), undefined);
  await m.type('Two. Mine.');
  await m.agentNow('Three.');
  assert.equal(m.box().props.value, 'Two. Mine.', 'the typed words stay');
  assert.ok(m.button('Keep mine') && m.button('Use the new one'), 'the choice is offered');
  assert.equal(m.flow().props.disabled, true, 'Save waits until the person chooses');
});

test('Teach: Keep mine makes the next Save name the newer instructions; Use the new one loads them and writes nothing', async () => {
  const m = mount('One.');
  await m.type('One. Mine.');
  await m.agentNow('Three.');
  m.button('Keep mine').props.onClick(); await settle();
  assert.equal(m.box().props.value, 'One. Mine.');
  assert.equal(m.button('Keep mine'), undefined, 'the choice is gone');
  await m.flow().props.run(true);
  assert.equal(lastUpdate(m.calls)[12], 'Three.', 'the person chose to replace what is stored now, and the Save names it');

  const n = mount('One.');
  await n.type('One. Mine.');
  await n.agentNow('Three.');
  n.button('Use the new one').props.onClick(); await settle();
  assert.equal(n.box().props.value, 'Three.');
  assert.equal(n.calls.filter((c) => c[0] === 'updateDataAgent').length, 0, 'nothing was written');
});

test('Teach: a refused Save reads the agent again so the choice can appear', async () => {
  const m = mount('One.');
  await m.type('One. Mine.');
  m.rpc.reply = { status: 'error', message: 'This set of AI instructions changed in the model after you opened it, so your version was not saved. Nothing was written.' };
  await assert.rejects(() => m.flow().props.run(true), /changed in the model after you opened it/);
  assert.equal(m.reloads(), 1, 'the panel asked for a fresh read');
  await m.agentNow('Three.');
  assert.ok(m.button('Keep mine'), 'the choice appears with the words kept');
  assert.equal(m.box().props.value, 'One. Mine.');
});

test('Examples editor: a Save names the examples it was seeded with', async () => {
  const calls = [];
  const rpc = async (method, ...args) => { calls.push([method, ...args]); return { status: 'ok' }; };
  const rt = makeHookRuntime();
  const { FewShotEditor } = loadPanels(rpc, rt.React);
  const ds = { folder: 'semantic_model-Sales', type: 'semantic_model', displayName: 'Sales', fewShots: [{ id: '1', question: 'q', query: 'EVALUATE 1' }] };
  const h = rt.renderHook(() => FewShotEditor({ workspaceId: 'ws', agentId: 'agent-1', ds, tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => undefined }));
  await settle();
  const flow = find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save examples…');
  await flow.props.run(true);
  const c = calls.find((x) => x[0] === 'updateDataAgent');
  assert.equal(c[11], 'fewshots');
  assert.deepEqual(JSON.parse(c[12]), { fewShots: ds.fewShots });
});

// Join 7 (K2): the examples editor reseeded its rows on every detail reload, so example rows typed but not saved vanished when a Teach save, a refused save's re-read or
// a Studio refresh loaded the agent again. It now follows the way the instructions box does: an untouched editor follows the agent, a typed one keeps its rows and, when the
// stored examples moved, offers Keep mine / Use the new one, and a Save names the examples the rows were seeded from.
function mountExamples(initial) {
  const calls = [];
  const rpc = async (method, ...args) => { calls.push([method, ...args]); return { status: 'ok' }; };
  const rt = makeHookRuntime();
  const { FewShotEditor, Button } = loadPanels(rpc, rt.React);
  // every detail reload hands over fresh objects, even when nothing moved; a render with no reload keeps the props it had
  const fresh = (fewShots) => ({ folder: 'semantic_model-Sales', type: 'semantic_model', displayName: 'Sales', fewShots: structuredClone(fewShots) });
  const state = { ds: fresh(initial) };
  const h = rt.renderHook(() => FewShotEditor({ workspaceId: 'ws', agentId: 'agent-1', ds: state.ds, tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => undefined }));
  const inputs = () => findAll(h.result, (el) => el.type === 'input');
  const flow = () => find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save examples…');
  const button = (label) => findAll(h.result, (el) => el.type === Button && el.props.children === label)[0];
  const addRow = async () => { button('+ Add example').props.onClick(); await settle(); };
  const type = async (i, field, value) => { inputs()[i * 2 + (field === 'query' ? 1 : 0)].props.onChange({ target: { value } }); await settle(); };
  const reload = async (fewShots) => { state.ds = fresh(fewShots); h.rerender(); await settle(); };
  const shown = () => { const v = inputs().map((el) => el.props.value); const out = []; for (let i = 0; i < v.length; i += 2) out.push([v[i], v[i + 1]]); return out; };
  return { calls, flow, button, addRow, type, reload, shown };
}
const saved = (calls) => calls.filter((c) => c[0] === 'updateDataAgent').at(-1);

test('Examples: rows typed but not saved survive a reload that brings the same examples', async () => {
  const m = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await m.addRow();
  await m.type(1, 'question', 'Sales by month?');
  await m.type(1, 'query', 'EVALUATE SUMMARIZE(...)');
  await m.reload([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);   // a Teach save or a refused save's re-read loads the agent again
  assert.deepEqual(m.shown(), [['q', 'EVALUATE 1'], ['Sales by month?', 'EVALUATE SUMMARIZE(...)']], 'the typed row is still there');
  assert.equal(m.button('Keep mine'), undefined, 'nothing moved, so nothing to choose');
});

test('Examples: an untouched editor follows the agent', async () => {
  const m = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await m.reload([{ id: '1', question: 'q', query: 'EVALUATE 1' }, { id: '2', question: 'r', query: 'EVALUATE 2' }]);
  assert.equal(m.shown().length, 2);
  assert.equal(m.button('Keep mine'), undefined);
});

test('Examples: typed rows stay when the stored examples moved, the choice is offered, Save waits', async () => {
  const m = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await m.addRow();
  await m.type(1, 'question', 'Mine?');
  await m.reload([{ id: '1', question: 'q', query: 'EVALUATE 1' }, { id: '2', question: 'theirs', query: 'EVALUATE 2' }]);
  assert.deepEqual(m.shown(), [['q', 'EVALUATE 1'], ['Mine?', '']], 'the typed rows stay');
  assert.ok(m.button('Keep mine') && m.button('Use the new one'), 'the choice is offered');
  assert.equal(m.flow().props.disabled, true, 'Save waits until the person chooses');
});

test('Examples: Keep mine makes the Save name the newer examples; Use the new one loads them and writes nothing', async () => {
  const newer = [{ id: '1', question: 'q', query: 'EVALUATE 1' }, { id: '2', question: 'theirs', query: 'EVALUATE 2' }];
  const m = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await m.addRow(); await m.type(1, 'question', 'Mine?');
  await m.reload(newer);
  m.button('Keep mine').props.onClick(); await settle();
  assert.equal(m.button('Keep mine'), undefined, 'the choice is gone');
  await m.flow().props.run(true);
  assert.deepEqual(JSON.parse(saved(m.calls)[12]), { fewShots: newer }, 'the Save names what is stored now');
  assert.deepEqual(JSON.parse(saved(m.calls)[6]), { fewShots: [{ id: '1', question: 'q', query: 'EVALUATE 1' }, { question: 'Mine?', query: '' }] }, 'and sends the typed rows');

  const n = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await n.addRow(); await n.type(1, 'question', 'Mine?');
  await n.reload(newer);
  n.button('Use the new one').props.onClick(); await settle();
  assert.deepEqual(n.shown(), [['q', 'EVALUATE 1'], ['theirs', 'EVALUATE 2']]);
  assert.equal(n.calls.filter((c) => c[0] === 'updateDataAgent').length, 0, 'nothing was written');
});

test('Examples: a Save names the examples the rows were seeded from, not a newer copy', async () => {
  const m = mountExamples([{ id: '1', question: 'q', query: 'EVALUATE 1' }]);
  await m.type(0, 'question', 'q edited');
  await m.reload([{ id: '1', question: 'q', query: 'EVALUATE 1' }, { id: '2', question: 'theirs', query: 'EVALUATE 2' }]);
  // an Apply already under review when the reload landed still names the seed, so the engine refuses it
  await m.flow().props.run(true);
  assert.deepEqual(JSON.parse(saved(m.calls)[12]), { fewShots: [{ id: '1', question: 'q', query: 'EVALUATE 1' }] });
});

test('Examples: after a committed Save the reload that brings the saved rows raises no choice', async () => {
  const m = mountExamples([]);
  await m.addRow(); await m.type(0, 'question', 'Sales?'); await m.type(0, 'query', 'EVALUATE 1');
  await m.reload([{ id: 'new', question: 'Sales?', query: 'EVALUATE 1' }]);
  assert.equal(m.button('Keep mine'), undefined, 'the saved rows are the typed rows');
  assert.deepEqual(m.shown(), [['Sales?', 'EVALUATE 1']]);
});

// Join 7 (K4): the schema's field ticks save the loaded document whole, so the save names the document the ticks started from and the engine compares it inside its write
// (Semanticus.Tests/DataAgentTests.cs, the schema cases). The page keeps naming the loaded document even when the agent's copy has moved since.
test('Schema ticks: a Save names the document the ticks started from', async () => {
  const calls = [];
  const rpc = async (method, ...args) => { calls.push([method, ...args]); return { status: 'ok' }; };
  const rt = makeHookRuntime();
  const { SourceCard, Button } = loadPanels(rpc, rt.React, schemaLib);
  const started = JSON.stringify({ elements: [{ display_name: 'Sales', is_selected: true, children: [{ display_name: 'Amount', is_selected: true }] }] }, null, 2);
  const ds = { folder: 'semantic_model-Sales', type: 'semantic_model', displayName: 'Sales', datasourceJson: started };
  const h = rt.renderHook(() => SourceCard({ ds, workspaceId: 'ws', agentId: 'agent-1', tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => undefined }));
  await settle();
  find(h.result, (el) => el.type === 'button' && /Show elements/.test(el.props['aria-label'] ?? '')).props.onClick(); await settle();
  find(h.result, (el) => el.type?.name === 'ElementTree').props.onSelectionChange([0, 0], false); await settle();
  const flow = find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save schema…');
  assert.ok(flow, 'a tick makes the schema dirty, so the Save is offered');
  await flow.props.run(true);
  const c = calls.find((x) => x[0] === 'updateDataAgent');
  assert.equal(c[11], 'schema');
  assert.equal(c[12], started, 'the save names the schema as it was loaded');
  assert.equal(JSON.parse(c[5]).elements[0].children[0].is_selected, false, 'and sends the ticked one');
  void Button;
});
