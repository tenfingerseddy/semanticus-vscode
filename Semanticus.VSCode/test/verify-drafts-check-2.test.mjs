// VERIFY (m11/drafts re-check, round 2): V3 FAILS on purpose on 9467858f.
//
// V3 A published model's typed words do not survive a COLD start (VS Code closed and opened again, or the window reloaded).
//    Round 2 keys a live-bound session on 'live:' + endpoint + '|' + database (drafts.ts modelKeyOfSession, src/draftKey.ts). But a cold start
//    does not open a published model live: reopenLastModel with restarted=false opens the cached snapshot as a PLAIN FILE when it is still on disk
//    (extension.ts `offlineFirst`; the engine sweeps a snapshot only when it is older than a day, LiveModelExport.SweepStale). That session has no
//    LiveOrigin, so liveBound is false and the key is the snapshot path (reopen-behaviour.mjs `fileSessionFromSnapshot` says the same).
//    So the same model is keyed two ways: words typed while it was open live are not shown after a cold start, and words typed after a cold start
//    are keyed on a temp folder that the next live open replaces, so they are never shown again.
//    This drives the REAL reopenLastModel (the reopen bench) and the REAL page (the Spec tab's Edit JSON, as V1 does).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeBench, LIVE_TARGET, liveSession, fileSessionFromSnapshot } from './reopen-behaviour.mjs';
import { makeDisk, makePage, find, settleAll, wait, text } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;

const SNAP = '/fixture/semanticus-live/5e1d22aa/Contoso.bim';
// The engine's answer to each open: a live open makes a session bound to the published model; a plain open of the snapshot file makes a file session.
const bench = () => makeBench({
  pathExists: (p) => p === SNAP,
  sessionAfterOpen: (method, args) => method === 'open' ? fileSessionFromSnapshot({ source: args[0] }) : liveSession({ source: SNAP }),
});

// Since r1/reopen a cold start first reopens the published model live with the sign-in this machine already holds. These
// cases are about the cached copy, which opens when that silent reconnect cannot (no saved sign-in).
const silentFails = (b) => { b.sandbox.openThrows = new Error('A sign in is needed.'); b.sandbox.openThrowsOnly = ['openLive']; };

const keyOf = (disk, s) => makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } }).drafts.modelKeyOfSession(s);

test('V3 precondition: a cold start opens the published model from its snapshot as a plain file (no live binding)', async () => {
  const b = bench();
  b.seed(LIVE_TARGET);
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: true });
  assert.equal(b.sandbox.currentSession.liveBound, true);
  assert.equal(b.sandbox.currentSession.source, SNAP);
  silentFails(b);   // r1/reopen: a cold start tries the live model silently first; this path is when that fails
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: false });
  assert.deepEqual(b.openCalls().map((c) => c.method), ['openLive', 'openLive', 'open'], 'the restart opens live; the cold start tries live silently, then opens the snapshot file');
  assert.equal(b.openCalls()[2].args[0], SNAP);
  assert.equal(b.sandbox.currentSession.liveBound, false);
});

test('V3 a published model: the session before a cold start and after it is one draft key', async () => {
  const b = bench(), disk = makeDisk();
  b.seed(LIVE_TARGET);
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: true });
  const live = keyOf(disk, b.sandbox.currentSession);
  silentFails(b);   // r1/reopen: a cold start tries the live model silently first; this path is when that fails
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: false });
  const cold = keyOf(disk, b.sandbox.currentSession);
  assert.equal(cold, live, `one published model, one draft key (open live: ${live}; after a cold start: ${cold})`);
});

// The page: words typed into the Spec tab's Edit JSON while the model is open live, then VS Code is opened again.
const specOf = (version) => ({ version, source: 'manual', spec: { name: 'M', tables: [{ name: 'Sales', role: 'fact', columns: [] }], relationships: [], measures: [], timeIntelligence: [] } });
function openSpec(disk, model) {
  const rpc = async (method) => (method === 'getSpec' ? specOf(1) : {});
  const pg = makePage({
    './bridge': { rpc, onSpecChange: () => () => undefined, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined },
    './connection': { useConnection: () => ({ openConnections: () => undefined }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, model);
  return pg.load('spec.tsx').SpecView && { pg, SpecView: pg.load('spec.tsx').SpecView };
}
async function mount({ pg, SpecView }) {
  const h = pg.mount(() => SpecView({ session: { modelName: 'M' } }));
  await settleAll(); await wait(10); await settleAll();
  const btn = (label) => find(h.result, (el) => (el.type?.name === 'Btn' || el.type === 'button') && text(el) === label);
  return { h, btn, box: () => find(h.result, (el) => el.type === 'textarea') };
}

test('V3 the page: Edit JSON words typed while the published model is open live are there after a cold start', async () => {
  const b = bench(), disk = makeDisk();
  b.seed(LIVE_TARGET);
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: true });
  const a = await mount(openSpec(disk, keyOf(disk, b.sandbox.currentSession)));
  a.btn('Edit JSON').props.onClick(); await settleAll();
  const typed = JSON.stringify({ name: 'Mine', tables: [], relationships: [], measures: [], timeIntelligence: [] }, null, 2);
  a.box().props.onChange({ target: { value: typed } }); await settleAll();
  a.h.unmount();
  // VS Code is closed and opened again: the extension reopens the model offline from its snapshot.
  silentFails(b);   // r1/reopen: a cold start tries the live model silently first; this path is when that fails
  await b.sandbox.reopenLastModel(b.context, b.connection, { restarted: false });
  const c = await mount(openSpec(disk, keyOf(disk, b.sandbox.currentSession)));
  assert.equal(c.box()?.props.value, typed, 'the JSON editor opens on the typed text after the cold start');
});

// V4 A held Spec structured edit, after a REAL engine restart, can be saved over a newer spec without the ask.
//    spec.tsx holds the edit with `base: { version }`, the spec version it was typed over, and asks only when the version differs. But the version is a
//    counter in the engine's memory, one per session (Spec.cs SpecStore: `_version`, a new SessionContext.Spec on every open), so a restarted engine
//    starts again at 0 with no spec. The first spec anyone sets there is version 1 again. A draft typed over version 1 of the OLD session then matches
//    it: no "changed elsewhere" ask, and Save names version 1, which the engine takes, writing the person's edit over a spec they never saw.
//    (The builder's restart test, drafts-spec "the page reloads", keeps the same engine world, so the counter never starts again.)
function specWorld(version, tables) {
  const w = { snap: specOf2(version, tables), calls: [] };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getSpec') return structuredClone(w.snap);
    if (method === 'setSpec') {
      const [json, , expected] = args;
      if (expected != null && expected !== w.snap.version) throw new Error('This spec changed in the model after you opened it, so your version was not saved.');
      w.snap = { version: w.snap.version + 1, source: 'manual', spec: JSON.parse(json) };
      return structuredClone(w.snap);
    }
    return {};
  };
  return w;
}
const specOf2 = (version, tables) => ({ version, source: 'manual', spec: tables && { name: 'M', tables: tables.map((name) => ({ name, role: 'fact', columns: [] })), relationships: [], measures: [], timeIntelligence: [] } });
function openWorld(disk, world, model) {
  const pg = makePage({
    './bridge': { rpc: world.rpc, onSpecChange: () => () => undefined, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined },
    './connection': { useConnection: () => ({ openConnections: () => undefined }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, model);
  return { pg, SpecView: pg.load('spec.tsx').SpecView };
}

test('V4 Spec structured edit held over a restart: a spec set in the NEW engine (same version number) is asked about before Save writes over it', async () => {
  const disk = makeDisk();
  const oldEngine = specWorld(1, ['Sales']);   // the person autogenerated once: version 1
  const a = await mount(openWorld(disk, oldEngine, '/m/Model.bim'));
  find(a.h.result, (el) => el.type?.name === 'SpecEditor').props.edit((s) => { s.name = 'Typed name'; }); await settleAll();
  a.h.unmount();
  // The engine restarts: a new session, no spec, version 0. The assistant then sets its own spec once: version 1 again, other content.
  const newEngine = specWorld(0, null);
  await newEngine.rpc('setSpec', JSON.stringify(specOf2(0, ['Orders', 'Customers']).spec), 'agent', 0);
  newEngine.calls.length = 0;
  const b = await mount(openWorld(disk, newEngine, '/m/Model.bim'));
  const editor = find(b.h.result, (el) => el.type?.name === 'SpecEditor');
  assert.equal(editor?.props.spec.name, 'Typed name', 'precondition: the held edit is shown after the restart');
  await b.btn('Save').props.onClick(); await settleAll();
  const writes = newEngine.calls.filter((c) => c[0] === 'setSpec');
  assert.equal(writes.length, 0, `Save must not write the held edit over the assistant's spec (Orders, Customers) before the person is asked; it wrote ${JSON.stringify(writes.map((w) => JSON.parse(w[1]).tables?.map((t) => t.name)))} naming version ${writes[0]?.[3]}`);
});

// V5 / V6 W3: the ask for a calculation item the assistant renamed (orphandrafts.tsx, Keep mine on it).
// V5 Keep mine re-bases the words onto the chosen item's CURRENT value (advmodels.tsx rebase, drafts.ts moveScope). When the assistant renamed the item
//    AND changed its DAX, the person never sees that DAX: the ask shows only their own words and the item's name, the row then opens with no
//    Keep mine / Use the new one choice, and Save names the assistant's new DAX as the value it replaces, so the engine takes it. The save should name the
//    value the person typed over (or ask), so a change they never read is asked about first.
// V6 Keep mine onto an item that already holds its own typed words replaces those words with no warning (moveScope: "they replace a draft the target
//    already held"). The words typed on the target are gone; the ask never said so.
const calcCalls = [];
const calcLive = { expression: 'SELECTEDMEASURE()' };
const calcStubs = (disk) => ({
  './bridge': { rpc: async (m, ...a) => { calcCalls.push([m, ...a]); if (m === 'getDax') return calcLive.expression; if (m === 'getObjectProperties') return [{ name: 'FormatStringExpression', value: '' }]; return undefined; },
    onDidChange: () => () => undefined, loadState: disk.loadState, saveState: disk.saveState },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) }, './formatMode': { inferFormatMode: () => 'dynamic' },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) }, './activity': { useClaudeReflection: () => undefined },
});
const calcItem = (ref, name, expression) => ({ ref, name, ordinal: 1, expression, formatStringExpression: '' });
const daxOf = (tree) => find(tree, (el) => el.type?.name === 'DaxField');
const buttonOf = (tree, label) => find(tree, (el) => el.type === 'button' && text(el) === label);
function calcPage() {
  const disk = makeDisk(), pg = makePage(calcStubs(disk), '/models/Sales.bim');
  const { CalcItemRow } = pg.load('advmodels.tsx'), { OrphanDrafts } = pg.load('orphandrafts.tsx');
  const row = (it) => pg.mount(() => CalcItemRow({ item: it, reload: () => undefined, onError: () => undefined }));
  const ask = (list) => pg.mount(() => OrphanDrafts({ prefix: 'calcitem|', live: list.map((i) => i.ref), what: 'calculation item', nameOf: (r) => r,
    targetsFor: () => list.map((i) => ({ id: i.ref, label: i.name })),
    rebase: (ref, f) => { const it = list.find((i) => i.ref === ref); return f === 'format' ? (it?.formatStringExpression ?? '') : (it?.expression ?? ''); } }));
  return { pg, row, ask };
}
async function keepMineOn(w, list, to) {
  const q = w.ask(list); await settleAll();
  find(q.result, (el) => el.type === 'select').props.onChange({ target: { value: to } }); await settleAll();
  buttonOf(q.result, 'Keep mine on it').props.onClick(); await settleAll();
}

test('V5 W3 calc item renamed AND its DAX changed by the assistant: after Keep mine on it, the change the person never read is asked about before Save writes over it', async () => {
  const w = calcPage();
  calcLive.expression = 'SELECTEDMEASURE()';
  const a = w.row(calcItem('calcitem:Time/YTD', 'YTD', 'SELECTEDMEASURE()')); await settleAll();
  daxOf(a.result).props.onChange('MY DAX'); await settleAll();
  a.unmount();
  // The assistant renames YTD to "Year to date" and rewrites its DAX.
  const renamed = calcItem('calcitem:Time/Year to date', 'Year to date', 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))');
  calcLive.expression = renamed.expression;
  await keepMineOn(w, [renamed], renamed.ref);
  const held = w.pg.drafts.draftStore.get('/models/Sales.bim', 'calcitem|' + renamed.ref, 'expression');
  assert.equal(held?.value, 'MY DAX', 'precondition: the words moved onto the renamed item');
  const b = w.row(renamed); await settleAll();
  const asked = find(b.result, (el) => el.type?.name === 'EditMovedBanner');
  assert.ok(asked || held.base === 'SELECTEDMEASURE()',
    `the assistant's new DAX was never shown to the person, so the row must ask (Keep mine / Use the new one) or the save must name the value typed over; the draft's base is "${held.base}" and no choice is shown`);
});

test('V6 W3 Keep mine on an item that already holds typed words: those words are not dropped without a word', async () => {
  const w = calcPage();
  calcLive.expression = 'SELECTEDMEASURE()';
  const a = w.row(calcItem('calcitem:Time/YTD', 'YTD', 'SELECTEDMEASURE()')); await settleAll();
  daxOf(a.result).props.onChange('WORDS FOR YTD'); await settleAll(); a.unmount();
  const mtd = calcItem('calcitem:Time/MTD', 'MTD', 'SELECTEDMEASURE()');
  const b = w.row(mtd); await settleAll();
  daxOf(b.result).props.onChange('WORDS FOR MTD'); await settleAll(); b.unmount();
  // YTD is renamed by the assistant; the person picks MTD in the list (it is the only one, or a slip of the hand) and presses Keep mine on it.
  const q = w.ask([mtd]); await settleAll();
  const warned = /MTD/.test(JSON.stringify(find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'))) && /already|replace|own words/i.test(JSON.stringify(q.result));
  find(q.result, (el) => el.type === 'select').props.onChange({ target: { value: mtd.ref } }); await settleAll();
  buttonOf(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  const held = w.pg.drafts.draftStore.get('/models/Sales.bim', 'calcitem|' + mtd.ref, 'expression');
  assert.ok(warned || held?.value === 'WORDS FOR MTD' || JSON.stringify(held?.value).includes('WORDS FOR MTD'),
    `the words typed on MTD ("WORDS FOR MTD") were replaced by "${held?.value}" with no warning`);
});
