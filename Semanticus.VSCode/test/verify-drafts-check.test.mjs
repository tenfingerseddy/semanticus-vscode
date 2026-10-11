// VERIFY (m11/drafts check): two places where typed, unsaved work is still lost. Both tests FAIL on purpose on b4df5848.
//
// V1 A published (live-bound) model loses every draft on an engine restart. App.tsx keys drafts on modelKeyOfSession(session), which is
//    `source + '|' + liveDatabase`. For a live open, `source` is the engine's local snapshot, a NEW folder on every open
//    (LiveModelExport.cs:138, `Path.Combine(TempRoot, Guid.NewGuid()...)`), and a restart reopens a live model through openLive
//    (extension.ts:1903, reopenLastModel with restarted=true). So after the restart the key differs and the typed words never show again.
// V2 The Spec tab's structured view (the inline editor the tab leads with; Edit JSON is its escape hatch) keeps its edits in component
//    state only. A tab switch unmounts the tab and the edit is gone, while the page says "Your draft is kept".
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, text, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
const specOf = (version, tables) => ({ version, source: 'manual', spec: { name: 'M', tables: tables.map((name) => ({ name, role: 'fact', columns: [] })), relationships: [], measures: [], timeIntelligence: [] } });

function makeWorld() {
  const w = { snap: specOf(1, ['Sales']), calls: [] };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getSpec') return structuredClone(w.snap);
    return {};
  };
  return w;
}
function open(disk, world, model) {
  const stubs = {
    './bridge': { rpc: world.rpc, onSpecChange: () => () => undefined, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined },
    './connection': { useConnection: () => ({ openConnections: () => undefined }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  };
  const pg = makePage(stubs, model);
  return { pg, SpecView: pg.load('spec.tsx').SpecView };
}
async function mount({ pg, SpecView }) {
  const h = pg.mount(() => SpecView({ session: { modelName: 'M' } }));
  await settleAll(); await wait(10); await settleAll();
  const btn = (label) => find(h.result, (el) => (el.type?.name === 'Btn' || el.type === 'button') && text(el) === label);
  // The structured view is SpecEditor; every inline control in it funnels through its `edit` prop (spec.tsx:145), which is what a control's commit calls.
  const editor = () => find(h.result, (el) => el.type?.name === 'SpecEditor');
  return { h, btn, editor, box: () => find(h.result, (el) => el.type === 'textarea') };
}

// The same published model, opened twice by the engine (before and after a restart): one endpoint, one dataset, two snapshot folders.
const ENDPOINT = 'powerbi://api.powerbi.com/v1.0/myorg/Finance';
const liveSession = (folder) => ({ sessionId: 's-' + folder, modelName: 'Sales', liveBound: true, liveEndpoint: ENDPOINT, liveDatabase: 'Sales',
  source: `/tmp/semanticus-live/${folder}/Sales.bim` });

test('V1 a published model: words typed before an engine restart are there after it (the live open makes a new snapshot folder)', async () => {
  const disk = makeDisk(), world = makeWorld();
  const keyOf = (s) => makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } }).drafts.modelKeyOfSession(s);
  const before = keyOf(liveSession('3f9a1c07')), after = keyOf(liveSession('b2e8d410'));
  const a = await mount(open(disk, world, before));
  a.btn('Edit JSON').props.onClick(); await settleAll();
  const typed = JSON.stringify({ name: 'Mine', tables: [], relationships: [], measures: [], timeIntelligence: [] }, null, 2);
  a.box().props.onChange({ target: { value: typed } }); await settleAll();
  a.h.unmount();
  // The engine restarts; the extension reopens the published model through openLive; the page reloads on the new session.
  const b = await mount(open(disk, world, after));
  assert.equal(b.box()?.props.value, typed, `the JSON editor opens on the typed text after the restart (draft key before ${before}, after ${after})`);
});

test('V2 Spec structured view: an inline edit survives a tab switch (the tab unmounts and mounts again)', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world, '/m/Model.bim');
  const a = await mount(page);
  // What the Model name field's commit does (spec.tsx:333): edit((s) => { s.name = v; }).
  a.editor().props.edit((s) => { s.name = 'Typed model name'; }); await settleAll();
  assert.equal(a.editor()?.props.spec.name, 'Typed model name', 'precondition: the edit is in the draft');
  assert.ok(a.btn('Save'), 'precondition: the tab shows Save for the unsaved edit');
  a.h.unmount();
  const b = await mount(page);
  assert.equal(b.editor()?.props.spec.name, 'Typed model name', 'the typed model name is still there after the tab came back');
});
