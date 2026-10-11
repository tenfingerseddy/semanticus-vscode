// Join 11, the drafts class: the incremental refresh policy form keeps what the person typed when the tab goes away.
//
// The policy form (M code tab) held its fields in component state only, so a tab switch, a restarted engine (App keys the page on
// the session id) or a reload dropped them. It now holds a draft per table in the shared draft store (webview/src/drafts.ts) under
// `mcode|policy|<table>`, with the policy it was typed over; the tab opens the form again on it, asks Keep mine / Use the new one
// when the assistant changed the policy meanwhile, and Save then names the policy it read. The draft ends on a save the engine took,
// Discard, Use the new one, and Remove policy, never on an unmount. The real mcode.tsx runs here (bridge faked).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, draftMark, movedBanner, settleAll, wait, heldKeys } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;   // the tab schedules with window.setTimeout

const partition = (t) => ({ name: t, mode: 'Import', sourceType: 'M', source: `let S = Sql.Database("h","d"), T = Table.SelectRows(S, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in T` });
const docModel = () => ({
  header: { compatibilityLevel: 1600 },
  tables: ['Sales', 'Orders'].map((name) => ({ ref: 'table:' + name, name, isCalculated: false, partitions: [partition(name)] })),
  columns: ['Sales', 'Orders'].map((table) => ({ table, name: 'OrderDate', dataType: 'DateTime', sourceColumn: 'OrderDate', isCalculated: false })),
  expressions: [
    { name: 'RangeStart', kind: 'M', expression: '#datetime(2020,1,1,0,0,0) meta [IsParameterQuery=true]' },
    { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021,1,1,0,0,0) meta [IsParameterQuery=true]' },
  ],
});
const policy = (table, incrementalPeriods, incrementalGranularity, rollingWindowPeriods = 5) => ({
  table, enabled: true, policyType: 'Basic', mode: 'Import', rollingWindowGranularity: 'Year', rollingWindowPeriods,
  incrementalGranularity, incrementalPeriods, incrementalPeriodsOffset: 0, sourceExpression: null, pollingExpression: null, wiredDateField: 'OrderDate',
});

function makeWorld() {
  const w = { policies: { Sales: policy('Sales', 10, 'Day'), Orders: policy('Orders', 2, 'Month') }, calls: [], changed: [] };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getDocModel') return docModel();
    if (method === 'getIncrementalRefreshPolicy') return structuredClone(w.policies[args[0].slice('table:'.length)]);
    if (method === 'setIncrementalRefreshPolicy') {
      const t = args[0].slice('table:'.length);
      const [, , storeP, storeG, refreshP, refreshG, offset, mode] = args;
      w.policies[t] = { ...policy(t, refreshP, refreshG, storeP), rollingWindowGranularity: storeG, incrementalPeriodsOffset: offset, mode };
      return {};
    }
    if (method === 'removeIncrementalRefreshPolicy') { const t = args[0].slice('table:'.length); w.policies[t] = { table: t, enabled: false }; return {}; }
    return {};
  };
  return w;
}

function open(disk, world, model = '/m/Model.bim', sessionId = 's1') {
  const stubs = {
    './bridge': {
      rpc: world.rpc, onDidChange: (fn) => { world.changed.push(fn); return () => undefined; },
      loadState: disk.loadState, saveState: disk.saveState,
    },
    './meditor': { MEditor: () => null }, './mlang': { formatM: async (s) => ({ ok: true, text: s }) }, './manalysis': { mDiagnostics: async () => [] },
    './connection': { useConnection: () => ({ conn: null, session: { sessionId } }) }, './mtransform': { gen: {}, mQuoteIdent: (s) => s },
    './pqtransforms': { SamplePreview: () => null, AppliedStepsPanel: () => null, sourceMName: (c) => c.sourceColumn ?? c.name },
  };
  const pg = makePage(stubs, model);
  return { pg, MCodeView: pg.load('mcode.tsx').MCodeView };
}
async function mount({ pg, MCodeView }) {
  const h = pg.mount(() => MCodeView({}));
  await settleAll(); await wait(30); await settleAll();
  const btn = (label) => find(h.result, (el) => el.type === 'button' && text(el) === label);
  const configure = () => find(h.result, (el) => el.type === 'button' && /^Configure/.test(text(el)));
  const nums = () => findAll(h.result, (el) => el.type?.name === 'Num');
  return {
    h, btn, nums,
    isOpen: () => configure()?.props['aria-expanded'] === true,
    openForm: async () => { if (configure().props['aria-expanded'] !== true) { configure().props.onClick(); await settleAll(); } },
    typeStore: async (v) => { nums()[0].props.onChange(v); await settleAll(); },
    pick: async (table) => { find(h.result, (el) => el.type?.name === 'MLane').props.onSelectTable(table); await settleAll(); await wait(30); await settleAll(); },
    save: async () => { await btn('Save policy').props.onClick(); await settleAll(); },
  };
}
const saves = (w) => w.calls.filter((c) => c[0] === 'setIncrementalRefreshPolicy');

test('policy form: a value typed, the tab unmounted (tab switch) and mounted again: the form opens on it, marked as a draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeStore(7);
  a.h.unmount();
  const b = await mount(page);
  assert.ok(b.isOpen(), 'the policy form is open again');
  assert.equal(b.nums()[0]?.props.value, 7, 'with the typed store window');
  assert.equal(b.nums()[1]?.props.value, 10, 'and the rest of the saved policy');
  assert.ok(draftMark(b.h.result), 'the quiet Draft not saved mark shows');
  assert.equal(movedBanner(b.h.result), null, 'nothing moved, so nothing to choose');
});

test('policy form: a value typed, the page reloads (an engine restart or a webview reload): the form opens on it', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openForm(); await a.typeStore(7);
  a.h.unmount();
  const b = await mount(open(disk, world));
  assert.ok(b.isOpen());
  assert.equal(b.nums()[0]?.props.value, 7);
  assert.ok(draftMark(b.h.result));
});

test('policy form: the assistant changed the policy meanwhile: the value stays, the choice is asked, and Keep mine saves naming the policy it read', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openForm(); await a.typeStore(7);
  a.h.unmount();
  world.policies.Sales = policy('Sales', 3, 'Month');   // set_incremental_refresh_policy on the MCP door while the tab was away
  const b = await mount(open(disk, world));
  assert.equal(b.nums()[0]?.props.value, 7, 'the typed store window is kept');
  assert.ok(movedBanner(b.h.result), 'the choice is asked');
  await b.save();
  assert.equal(saves(world).length, 0, 'Save writes nothing while the choice is open');
  movedBanner(b.h.result).props.onKeepMine(); await settleAll();
  await b.save();
  const w = saves(world).at(-1);
  assert.ok(w, 'after Keep mine the Save writes');
  assert.equal(w[3], 7, 'the person\'s store window');
  assert.equal(JSON.parse(w.at(-1)).incrementalPeriods, 3, 'and the write names the policy it read, the assistant\'s');
});

test('policy form: a save the engine took, Discard, Use the new one and Remove policy each end the draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeStore(7);
  assert.equal(heldKeys(disk).length, 1, 'the typed form is held');
  await a.save(); await wait(10); await settleAll();
  assert.equal(world.policies.Sales.rollingWindowPeriods, 7);
  assert.deepEqual(heldKeys(disk), [], 'a save the engine took ends the draft');

  await a.typeStore(8);
  a.btn('Discard').props.onClick(); await settleAll();
  assert.equal(a.nums()[0].props.value, 7, 'Discard shows the saved policy again');
  assert.deepEqual(heldKeys(disk), [], 'Discard ends the draft');

  await a.typeStore(9);
  a.h.unmount();
  world.policies.Sales = policy('Sales', 3, 'Month', 4);
  const b = await mount(page);
  movedBanner(b.h.result).props.onUseNew(); await settleAll();
  assert.equal(b.nums()[0].props.value, 4, 'Use the new one loads the assistant\'s policy');
  assert.deepEqual(heldKeys(disk), [], 'Use the new one ends the draft');

  await b.typeStore(6);
  await b.btn('Remove policy').props.onClick(); await settleAll();
  assert.deepEqual(heldKeys(disk), [], 'Remove policy ends the draft');
});

test('policy form: a draft on another table or another model is not shown', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeStore(7);
  await a.pick('Orders');
  assert.deepEqual([a.nums()[0]?.props.value, a.nums()[1]?.props.value], [5, 2], 'Orders opens on its own policy');
  assert.equal(draftMark(a.h.result), null);
  await a.pick('Sales');
  assert.equal(a.nums()[0]?.props.value, 7, 'and Sales still has the typed value');
  assert.ok(draftMark(a.h.result));
  a.h.unmount();
  const b = await mount(open(disk, world, '/m/Other.bim'));
  await b.openForm();
  assert.equal(b.nums()[0]?.props.value, 5, 'another model never shows this model\'s draft');
  assert.equal(draftMark(b.h.result), null);
});

test('policy form: an engine restart (a new session on the same model file) opens the tab on the table that holds the draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world, '/m/Model.bim', 's1'));
  await a.pick('Orders');
  await a.openForm(); await a.typeStore(8);
  a.h.unmount();
  const b = await mount(open(disk, world, '/m/Model.bim', 's2'));
  assert.ok(b.isOpen(), 'the policy form is open again');
  assert.equal(b.nums()[1]?.props.value, 2, 'on Orders, the table the draft is for');
  assert.equal(b.nums()[0]?.props.value, 8, 'with the typed store window');
});

// ---- F12 (drafts-sol): the Power Query / M query editor keeps what is typed -------------------------------------------------------------------------
// Sol UAT J10-A-005 / J10-R-002: "// note" typed into an M query and not saved was gone after a tab switch, an area switch or a reload, with no mark and no warning. The M lane held its text
// in component state only. It now holds it in the shared store (`mcode|query|<target id>`), over the text it was typed over, and shows "Draft not saved".
const mLane = async (o) => {
  const view = await mount(o);
  const el = find(view.h.result, (x) => x.type?.name === 'MLane');
  const lane = o.pg.mount(() => el.type(el.props));
  await settleAll(); await wait(30); await settleAll();
  const editor = () => find(lane.result, (x) => x.type?.name === 'MEditor');
  return { view, lane, editor, mark: () => !!find(lane.result, (x) => x.type?.name === 'DraftMark' && x.props.show), btn: (label) => find(lane.result, (x) => x.type === 'button' && text(x) === label) };
};
const M_ORIGINAL = partition('Sales').source;

test('F12: M typed and not saved is held on every change, marked, and back after a tab switch', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mLane(open(disk, world));
  assert.equal(a.editor().props.value, M_ORIGINAL, 'the editor opens on the query as saved');
  assert.equal(a.mark(), false);
  a.editor().props.onChange('// note\n' + M_ORIGINAL); await settleAll();
  assert.equal(a.mark(), true, 'typed and not saved: the mark shows');
  assert.ok(heldKeys(disk).some(([, scope]) => /^mcode\|query\|/.test(scope)), 'the words are in the store');
  a.view.h.unmount(); a.lane.unmount();   // the tab (or the area) is left
  const b = await mLane(open(disk, world));
  assert.equal(b.editor().props.value, '// note\n' + M_ORIGINAL, 'the typed words are back');
  assert.equal(b.mark(), true);
  assert.equal(saves(world).length + world.calls.filter((c) => c[0] === 'setPartitionM').length, 0, 'nothing was written by coming back');
});

test('F12: the words survive a reload, and a query that changed meanwhile asks (Reload server M) instead of being replaced', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mLane(open(disk, world));
  a.editor().props.onChange('// note\n' + M_ORIGINAL); await settleAll();
  a.view.h.unmount(); a.lane.unmount();
  const changed = docModel(); changed.tables[0].partitions[0].source = M_ORIGINAL + ' // the assistant edited this';
  const rpc2 = world.rpc; world.rpc = async (method, ...args) => (method === 'getDocModel' ? structuredClone(changed) : rpc2(method, ...args));
  const b = await mLane(open(disk, world));
  assert.equal(b.editor().props.value, '// note\n' + M_ORIGINAL, 'the person\'s words are not replaced');
  assert.ok(b.btn('Reload server M'), 'the newer query is offered, and Save waits for the choice');
});

test('F12: Revert ends the draft; typing it back ends it', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mLane(open(disk, world));
  a.editor().props.onChange('x'); await settleAll();
  assert.equal(heldKeys(disk).filter(([, scope]) => /^mcode\|query\|/.test(scope)).length, 1);
  a.btn('Revert').props.onClick(); await settleAll();
  assert.equal(heldKeys(disk).filter(([, scope]) => /^mcode\|query\|/.test(scope)).length, 0);
  a.view.h.unmount(); a.lane.unmount();
});
