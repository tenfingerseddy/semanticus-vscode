// Join 11, the drafts class: the data agent's Teach editors keep what the person typed when the editor goes away.
//
// The Teach instructions box and the example question/query rows held their words in component state only, so a tab switch,
// another agent, a restarted engine (App keys the page on the session id) or a reload dropped them. They now hold a draft in the
// shared draft store (webview/src/drafts.ts) under `dataagent|<agent>|teach` and `dataagent|<agent>|fewshots|<folder>`, with the
// value it was typed over. Each editor opens on its held draft; when the saved value moved meanwhile it asks Keep mine / Use the
// new one, and a Save names the value the person chose to replace. The draft ends on a save the engine took, Discard, or Use the
// new one, never on an unmount. The real dataagent.tsx runs here (TeachPanel and FewShotEditor, imports stubbed, bridge faked).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, draftMark, settleAll, heldKeys } from './helpers/drafts-mount.mjs';

const ts = req('typescript');
const MODEL = '/m/Model.bim';

/** One page over `disk`: the real dataagent.tsx with its two editors reachable. */
function page(disk, world, model = MODEL) {
  const bridge = { rpc: world.rpc, copyText: () => undefined, loadState: disk.loadState, saveState: disk.saveState };
  const p = makePage({ './bridge': bridge }, model);
  const src = readFileSync(join(webview, 'src', 'dataagent.tsx'), 'utf8') + '\nexport { TeachPanel, FewShotEditor };\n';
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const Button = (props) => props;
  const stubs = {
    './bridge': bridge,
    './hooks': { usePersistedState: (k, v) => [v, () => undefined] },
    './workflows': { Button },
    './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
    './connection': { useConnection: () => ({}) },
    './accountpicker': { AccountPicker: () => null },
    './dataagent-schema.mjs': { isEditableElementTree: () => false, parseElementTree: () => [], updateElementSelection: (x) => x },
    './copy': { uiLabel: (s) => s },
    './drafts': p.load('drafts.ts'),
    './editbase': p.load('editbase.tsx'),
  };
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? p.React : req(n)), module, module.exports);
  return { ...p, ...module.exports, Button };
}

/** Fabric's copy of one agent, and a fake of the engine's update (it refuses a save that names a start that is no longer stored). */
function makeWorld(instr, fewShots = []) {
  const w = { agents: { 'agent-1': { instr, fewShots: structuredClone(fewShots) }, 'agent-2': { instr: 'Agent two.', fewShots: [] } }, calls: [] };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method !== 'updateDataAgent') return undefined;
    const [, agentId, typed, folder, , fewshotsJson, commit] = args;
    const what = args[10], expected = args[11];
    if (!commit) return { status: 'dry-run' };
    const a = w.agents[agentId];
    if (what === 'instructions') {
      if (expected != null && expected !== a.instr) return { status: 'error', message: 'This set of AI instructions changed in the model after you opened it, so your version was not saved. Nothing was written.' };
      a.instr = typed;
    } else if (what === 'fewshots') {
      if (expected != null && JSON.stringify(JSON.parse(expected).fewShots) !== JSON.stringify(a.fewShots)) return { status: 'error', message: 'changed in the model after you opened it' };
      a.fewShots = JSON.parse(fewshotsJson).fewShots.map((r, i) => ({ id: 'n' + i, ...r }));
      void folder;
    }
    return { status: 'ok' };
  };
  return w;
}
const writes = (w) => w.calls.filter((c) => c[0] === 'updateDataAgent' && c[7] === true);

// ---- the Teach instructions box ---------------------------------------------------------------------------------------------
function teach(pg, world, agent = 'agent-1') {
  const state = { agent };
  const detail = () => ({ info: { id: state.agent }, draft: { aiInstructions: world.agents[state.agent].instr, dataSources: [] } });
  let reloads = 0;
  const h = pg.mount(() => pg.TeachPanel({ workspaceId: 'ws', agentId: state.agent, detail: state.detail ?? (state.detail = detail()), tier: 'pro', authMode: 'azcli', tenantId: null,
    onChanged: () => { reloads++; } }));
  const box = () => find(h.result, (el) => el.type === 'textarea');
  const flow = () => find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save instructions…');
  const button = (label) => findAll(h.result, (el) => el.type === pg.Button && el.props.children === label)[0];
  return {
    h, box, flow, button, reloads: () => reloads,
    type: async (value) => { box().props.onChange({ target: { value } }); await settleAll(); },
    /** The page reads the agent again (a Studio refresh, the reload after a save). */
    reload: async () => { state.detail = detail(); h.rerender(); await settleAll(); },
    /** Another agent is picked in the rail while the panel stays on the page. */
    pick: async (id) => { state.agent = id; state.detail = detail(); h.rerender(); await settleAll(); },
  };
}

test('Teach: words typed, the panel unmounted (tab switch) and mounted again: the words are back and marked as a draft', async () => {
  const disk = makeDisk(), world = makeWorld('Use Sales Amount.');
  const pg = page(disk, world);
  const a = teach(pg, world); await settleAll();
  await a.type('Use Sales Amount. Fiscal year starts in July.');
  a.h.unmount();
  const b = teach(pg, world); await settleAll();
  assert.equal(b.box().props.value, 'Use Sales Amount. Fiscal year starts in July.', 'the typed words open again');
  assert.ok(draftMark(b.h.result), 'the quiet Draft not saved mark shows');
  assert.equal(b.button('Keep mine'), undefined, 'nothing moved, so nothing to choose');
});

test('Teach: words typed, the page reloads (an engine restart or a webview reload): the words are back', async () => {
  const disk = makeDisk(), world = makeWorld('Use Sales Amount.');
  const a = teach(page(disk, world), world); await settleAll();
  await a.type('Mine.');
  a.h.unmount();
  const b = teach(page(disk, world), world); await settleAll();
  assert.equal(b.box().props.value, 'Mine.');
  assert.ok(draftMark(b.h.result));
});

test('Teach: the assistant changed the instructions meanwhile: the words stay, the choice is asked, and Keep mine saves naming the newer text', async () => {
  const disk = makeDisk(), world = makeWorld('One.');
  const a = teach(page(disk, world), world); await settleAll();
  await a.type('One. Mine.');
  a.h.unmount();
  world.agents['agent-1'].instr = 'Three.';   // update_data_agent on the MCP door while the panel was away
  const b = teach(page(disk, world), world); await settleAll();
  assert.equal(b.box().props.value, 'One. Mine.', 'the typed words are kept');
  assert.ok(b.button('Keep mine') && b.button('Use the new one'), 'the choice is asked');
  assert.equal(b.flow().props.disabled, true, 'Save waits until the person chooses');
  assert.equal(writes(world).length, 0, 'nothing was written');
  b.button('Keep mine').props.onClick(); await settleAll();
  await b.flow().props.run(true);
  const w = writes(world).at(-1);
  assert.equal(w[3], 'One. Mine.');
  assert.equal(w[12], 'Three.', 'the Save names the instructions the person chose to replace');
});

test('Teach: a save the engine took, Discard, and Use the new one each end the draft', async () => {
  const disk = makeDisk(), world = makeWorld('One.');
  const pg = page(disk, world);
  const a = teach(pg, world); await settleAll();
  await a.type('Saved words.');
  assert.ok(heldKeys(disk).length === 1, 'the typed words are held');
  await a.flow().props.run(true); a.flow().props.onCommitted?.({ status: 'ok' }); await settleAll();
  assert.equal(world.agents['agent-1'].instr, 'Saved words.');
  assert.deepEqual(heldKeys(disk), [], 'a save the engine took ends the draft');
  await a.reload();
  assert.equal(draftMark(a.h.result), null);

  await a.type('Thrown away.');
  assert.equal(heldKeys(disk).length, 1);
  a.button('Discard').props.onClick(); await settleAll();
  assert.equal(a.box().props.value, 'Saved words.', 'Discard shows the saved instructions again');
  assert.deepEqual(heldKeys(disk), [], 'Discard ends the draft');

  await a.type('Mine again.');
  a.h.unmount();
  world.agents['agent-1'].instr = 'Theirs.';
  const b = teach(pg, world); await settleAll();
  b.button('Use the new one').props.onClick(); await settleAll();
  assert.equal(b.box().props.value, 'Theirs.');
  assert.deepEqual(heldKeys(disk), [], 'Use the new one ends the draft');
  assert.equal(writes(world).length, 1, 'only the first save wrote');
});

test('Teach: a draft on another agent or another model is not shown here', async () => {
  const disk = makeDisk(), world = makeWorld('One.');
  const pg = page(disk, world);
  const a = teach(pg, world); await settleAll();
  await a.type('Agent one words.');
  await a.pick('agent-2');
  assert.equal(a.box().props.value, 'Agent two.', 'another agent opens on its own instructions');
  assert.equal(draftMark(a.h.result), null);
  await a.pick('agent-1');
  assert.equal(a.box().props.value, 'Agent one words.', 'and coming back finds the words');
  a.h.unmount();
  const other = teach(page(disk, world, '/m/Other.bim'), world); await settleAll();
  assert.equal(other.box().props.value, 'One.', 'another model never shows this model\'s draft');
  assert.equal(draftMark(other.h.result), null);
});

// ---- the example question/query rows -----------------------------------------------------------------------------------------
function examples(pg, world, folder = 'semantic_model-Sales', agent = 'agent-1') {
  const ds = () => ({ folder, type: 'semantic_model', displayName: 'Sales', fewShots: structuredClone(world.agents[agent].fewShots) });
  const state = { ds: ds() };
  const h = pg.mount(() => pg.FewShotEditor({ workspaceId: 'ws', agentId: agent, ds: state.ds, tier: 'pro', authMode: 'azcli', tenantId: null, onChanged: () => undefined,
    draftScope: `dataagent|${agent}|fewshots|${folder}` }));
  const inputs = () => findAll(h.result, (el) => el.type === 'input');
  const flow = () => find(h.result, (el) => el.type?.name === 'WriteFlow' && el.props.label === 'Save examples…');
  const button = (label) => findAll(h.result, (el) => el.type === pg.Button && el.props.children === label)[0];
  const shown = () => { const v = inputs().map((el) => el.props.value); const out = []; for (let i = 0; i < v.length; i += 2) out.push([v[i], v[i + 1]]); return out; };
  return {
    h, flow, button, shown,
    addRow: async () => { button('+ Add example').props.onClick(); await settleAll(); },
    type: async (i, field, value) => { inputs()[i * 2 + (field === 'query' ? 1 : 0)].props.onChange({ target: { value } }); await settleAll(); },
    reload: async () => { state.ds = ds(); h.rerender(); await settleAll(); },
  };
}
const ONE = [{ id: '1', question: 'q', query: 'EVALUATE 1' }];

test('Examples: rows typed, the editor unmounted and mounted again (tab switch, another folder): the rows are back', async () => {
  const disk = makeDisk(), world = makeWorld('', ONE);
  const pg = page(disk, world);
  const a = examples(pg, world); await settleAll();
  await a.addRow(); await a.type(1, 'question', 'Sales by month?'); await a.type(1, 'query', 'EVALUATE X');
  a.h.unmount();
  const b = examples(pg, world); await settleAll();
  assert.deepEqual(b.shown(), [['q', 'EVALUATE 1'], ['Sales by month?', 'EVALUATE X']]);
  assert.ok(draftMark(b.h.result), 'the quiet Draft not saved mark shows');
  const other = examples(pg, world, 'semantic_model-Other'); await settleAll();
  assert.deepEqual(other.shown(), [['q', 'EVALUATE 1']], 'another source folder has its own rows');
  assert.equal(draftMark(other.h.result), null);
});

test('Examples: rows typed, the page reloads: the rows are back', async () => {
  const disk = makeDisk(), world = makeWorld('', ONE);
  const a = examples(page(disk, world), world); await settleAll();
  await a.type(0, 'question', 'q edited');
  a.h.unmount();
  const b = examples(page(disk, world), world); await settleAll();
  assert.deepEqual(b.shown(), [['q edited', 'EVALUATE 1']]);
  assert.ok(draftMark(b.h.result));
});

test('Examples: the stored examples moved meanwhile: the rows stay, the choice is asked, and Keep mine saves naming the newer examples', async () => {
  const disk = makeDisk(), world = makeWorld('', ONE);
  const a = examples(page(disk, world), world); await settleAll();
  await a.addRow(); await a.type(1, 'question', 'Mine?');
  a.h.unmount();
  world.agents['agent-1'].fewShots = [...ONE, { id: '2', question: 'theirs', query: 'EVALUATE 2' }];
  const b = examples(page(disk, world), world); await settleAll();
  assert.deepEqual(b.shown(), [['q', 'EVALUATE 1'], ['Mine?', '']], 'the typed rows are kept');
  assert.ok(b.button('Keep mine') && b.button('Use the new one'), 'the choice is asked');
  assert.equal(b.flow().props.disabled, true);
  assert.equal(writes(world).length, 0);
  b.button('Keep mine').props.onClick(); await settleAll();
  await b.flow().props.run(true);
  const w = writes(world).at(-1);
  assert.ok(w, 'after Keep mine the Save writes');
  assert.deepEqual(JSON.parse(w[12]), { fewShots: [...ONE, { id: '2', question: 'theirs', query: 'EVALUATE 2' }] }, 'the Save names the examples the person chose to replace');
});

test('Examples: a save the engine took, Discard, and Use the new one each end the draft', async () => {
  const disk = makeDisk(), world = makeWorld('', ONE);
  const pg = page(disk, world);
  const a = examples(pg, world); await settleAll();
  await a.type(0, 'question', 'saved q');
  assert.equal(heldKeys(disk).length, 1);
  await a.flow().props.run(true); a.flow().props.onCommitted?.({ status: 'ok' }); await settleAll();
  assert.deepEqual(heldKeys(disk), [], 'a save the engine took ends the draft');
  await a.reload();
  await a.type(0, 'question', 'thrown away');
  a.button('Discard').props.onClick(); await settleAll();
  assert.deepEqual(a.shown(), [['saved q', 'EVALUATE 1']]);
  assert.deepEqual(heldKeys(disk), [], 'Discard ends the draft');
  await a.type(0, 'question', 'mine');
  a.h.unmount();
  world.agents['agent-1'].fewShots = [{ id: '9', question: 'theirs', query: 'EVALUATE 9' }];
  const b = examples(pg, world); await settleAll();
  b.button('Use the new one').props.onClick(); await settleAll();
  assert.deepEqual(b.shown(), [['theirs', 'EVALUATE 9']]);
  assert.deepEqual(heldKeys(disk), [], 'Use the new one ends the draft');
});

test('Examples: a draft for another model is not shown', async () => {
  const disk = makeDisk(), world = makeWorld('', ONE);
  const a = examples(page(disk, world), world); await settleAll();
  await a.type(0, 'question', 'mine');
  a.h.unmount();
  const b = examples(page(disk, world, '/m/Other.bim'), world); await settleAll();
  assert.deepEqual(b.shown(), [['q', 'EVALUATE 1']]);
  assert.equal(draftMark(b.h.result), null);
});
