// cx3 (the 1.7.4-rc1 tester, 3 Nov): the Connections dialog right after a start, and the bar's two lines.
//
//   1 Right after VS Code starts, the dialog said "Open your first model" and "the engine connection was lost" with 31 saved rows,
//     until it was closed and opened again (2 of 2). The start-up open ends with a 'reconnected' message: the bridge drops every
//     read still in flight (rejectAll) and the dialog's list read was one of them. Nothing read the list again.
//   3 After the point of no return, a press on another row showed "It can no longer be stopped." and "Cancel this open to open X
//     instead." together.
//
// The REAL hub runs (hooks on the shared hook runtime, the bridge faked the way bridge.ts behaves on 'reconnected').
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// Source pins must read the same text on LF and Windows CRLF checkouts.
const src = (...p) => readFileSync(resolve(root, ...p), 'utf8').replaceAll('\r\n', '\n');
const LOST = 'Semanticus: the engine connection was lost while this request was in flight. The operation may still have completed. Verify its result before retrying.';

// Every string under an element, the inner function components drawn (they hold no hooks of their own).
const INNER = new Set(['AccountDialog', 'AccountMenu', 'JobsPanel']);
const expand = (el) => {
  if (Array.isArray(el)) return el.map(expand);
  if (!el || typeof el !== 'object') return el;
  if (typeof el.type === 'function' && INNER.has(el.type.name)) return expand(el.type(el.props ?? {}));
  if (el.props?.children === undefined) return el;
  return { ...el, props: { ...el.props, children: expand(el.props.children) } };
};
const allText = (el) => {
  if (el == null || typeof el === 'boolean') return '';
  if (typeof el === 'string' || typeof el === 'number') return String(el);
  if (Array.isArray(el)) return el.map(allText).join(' ');
  return allText(el.props?.children);
};
const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

const ROWS = Array.from({ length: 31 }, (_, i) => ({
  id: `r${i}`, kind: 'file', endpoint: `/home/user/models/Contoso${i}.SemanticModel`, database: null, modelName: `Contoso${i}`, lastUsedUtc: '2026-11-03T09:00:00Z', useCount: 2,
}));

/** The bridge, faked the way bridge.ts behaves: rpc calls wait until the test answers them, and 'reconnected' rejects every call still in
 *  flight (rejectAll) before its listeners run. `engineReady` is the one event the dialog may listen to for "the engine is there now". */
function makeWorld({ rows = ROWS } = {}) {
  const w = { rows, calls: [], inflight: [], ready: new Set(), progress: new Set(), holdList: true, failList: null };
  const answer = (method) => {
    switch (method) {
      case 'listConnections': return structuredClone(w.rows);
      case 'probeConnectionAccounts': case 'listAccountProfiles': case 'listSqlSources': return [];
      case 'listModelSqlEndpoints': return { rows: [] };
      case 'sessionInfo': return { sessionId: 's1', hasUnsavedChanges: false };
      case 'connectionContext': return null;
      default: return [];
    }
  };
  w.rpc = (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'open' || method === 'openLive') return new Promise((res, rej) => w.inflight.push({ method, res, rej }));
    if (method === 'listConnections' && w.failList) { const why = w.failList; w.failList = null; return Promise.reject(new Error(why)); }
    if (method === 'listConnections' && w.holdList) return new Promise((res, rej) => w.inflight.push({ method, res: () => res(answer(method)), rej }));
    return Promise.resolve(answer(method));
  };
  // The start-up open is done: bridge.ts rejectAll(...), then the reconnect listeners, then the engine is ready.
  w.reconnected = () => {
    for (const p of w.inflight.splice(0)) p.rej(new Error(LOST));
    for (const fn of [...w.ready]) fn();
  };
  w.engineConnected = () => { for (const fn of [...w.ready]) fn(); };
  w.releaseLists = () => { for (const p of w.inflight.filter((x) => x.method === 'listConnections')) p.res(); w.inflight = w.inflight.filter((x) => x.method !== 'listConnections'); };
  return w;
}

// A failed assert must not leave a page's clock running (the bar counts seconds while an open runs): every page ends after its test.
const mounted = [];
test.afterEach(() => { for (const h of mounted.splice(0)) h.unmount(); });

function page(world) {
  const disk = makeDisk();
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, onProgress: (fn) => { world.progress.add(fn); return () => world.progress.delete(fn); },
      onEngineReady: (fn) => { world.ready.add(fn); return () => world.ready.delete(fn); },
      announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined, navigateInStudio: () => undefined, runHostCommand: () => undefined,
      loadState: disk.loadState, saveState: disk.saveState,
    },
    './features': { planRankingOf: () => 'granted' },
    './pro': { useTier: () => 'pro' },
    './connection': { useConnection: () => ({ context: null, session: null, connectLocal: async () => undefined, connectXmla: async () => ({ ok: true }), disconnect: async () => undefined, refresh: async () => undefined }) },
    './accountpicker': { AccountPicker: () => null },
    './copy': { defaultSignInMode: (r) => r?.authMode || 'interactive' },
    './surface-escape': { useSurfaceEscape: () => undefined },
    './consent': { ConsentCard: () => null },
    './connectpickers': { DesktopPicker: () => null, WorkspacePicker: () => null },
  };
  const pg = makePage(stubs);
  const { ConnectionsHub } = pg.load('connectionshub.tsx');
  const h = pg.mount(() => ConnectionsHub({ open: true, onClose: () => undefined, standalone: true }));
  mounted.push(h);
  const view = () => expand(h.result);
  return { h, view, says: () => allText(view()) };
}

const rowNames = (tree) => findAll(tree, (el) => el.props?.['data-testid'] === 'hub-model-row').length;

test('blocker: the start-up open drops the list read in flight; the dialog reads the list again and never says "Open your first model"', async () => {
  const world = makeWorld();
  const p = page(world);
  await settleAll();
  assert.match(p.says(), /Loading your models/, 'while the engine starts, the dialog says it is loading');
  assert.doesNotMatch(p.says(), /Open your first model/);
  // The start-up open finishes: the host posts 'reconnected', and the bridge rejects the dialog's list read.
  world.reconnected();
  await settleAll();
  const seen = p.says();
  assert.doesNotMatch(seen, /Open your first model/, 'a dropped read is not an empty list: 31 models are saved');
  assert.doesNotMatch(seen, /engine connection was lost/, 'a read the dialog asks again is not a fault the person must handle');
  assert.equal(byTest(p.view(), 'hub-empty'), null);
  // The dialog asked again; the engine answers it.
  assert.ok(world.calls.filter((c) => c[0] === 'listConnections').length >= 2, 'the dialog read the list again after the engine was ready');
  world.releaseLists();
  await settleAll();
  assert.equal(rowNames(p.view()), 31, 'every saved model is on the list');
  assert.doesNotMatch(p.says(), /Open your first model|engine connection was lost/);
  p.h.unmount();
});

test('an engine that answers "not connected" first and then starts: the dialog reads the list when the engine is there', async () => {
  const world = makeWorld();
  world.holdList = false;
  world.failList = 'Engine not connected.';
  const p = page(world);
  await settleAll();
  assert.equal(byTest(p.view(), 'hub-empty'), null, 'a failed read is never shown as "Open your first model"');
  world.engineConnected();
  await settleAll();
  assert.equal(rowNames(p.view()), 31, 'the engine started: the list is read and shown');
  assert.doesNotMatch(p.says(), /Open your first model|Engine not connected/);
  p.h.unmount();
});

test('an older read that ends after a newer one never writes over it', async () => {
  const world = makeWorld();
  const p = page(world);
  await settleAll();
  // A second read starts (the engine is ready) while the first still waits; then the first fails late.
  const first = world.inflight.find((x) => x.method === 'listConnections');
  world.inflight = world.inflight.filter((x) => x !== first);
  world.engineConnected();
  await settleAll();
  world.releaseLists();
  await settleAll();
  assert.equal(rowNames(p.view()), 31);
  first.rej(new Error(LOST));
  await settleAll();
  assert.equal(rowNames(p.view()), 31, 'the late failure of the older read leaves the list as the newer read drew it');
  assert.doesNotMatch(p.says(), /engine connection was lost/);
  p.h.unmount();
});

test('a home with no saved models still says "Open your first model" once the list is read', async () => {
  const world = makeWorld({ rows: [] });
  world.holdList = false;
  const p = page(world);
  await settleAll();
  assert.ok(byTest(p.view(), 'hub-empty'), 'a real empty list says so');
  p.h.unmount();
});

test('the bridge tells the dialog the engine is ready after a reconnect and after an engine start', () => {
  const bridge = src('webview', 'src', 'bridge.ts');
  assert.ok(/export function onEngineReady\(/.test(bridge), 'one event for "the engine is there now"');
  const reconnectBranch = bridge.slice(bridge.indexOf("msg.type === 'reconnected'"), bridge.indexOf("window.addEventListener('pagehide'"));
  assert.ok(/rejectAll\([\s\S]*engineReadyListeners\.forEach/.test(reconnectBranch), "'reconnected' drops the reads in flight, then says the engine is ready");
  const stateBranch = bridge.slice(bridge.indexOf("msg.type === 'engineState'"), bridge.indexOf("msg.type === 'reconnectPending'"));
  assert.ok(/if \(connected\) engineReadyListeners\.forEach/.test(stateBranch), 'an engine that connected says the engine is ready');
});

test('3: after the point of no return, a pick on another row never says "Cancel this open"', async () => {
  const world = makeWorld();
  world.holdList = false;
  const p = page(world);
  await settleAll();
  const press = (name) => {
    const row = findAll(p.view(), (el) => el.props?.['data-testid'] === 'hub-model-row').find((el) => allText(el).includes(name));
    row.props.onClick();
  };
  press('Contoso3');
  await settleAll();
  byTest(p.view(), 'hub-open').props.onClick();
  await settleAll();
  assert.ok(world.inflight.some((x) => x.method === 'open'), 'the open runs');
  const emit = (e) => { for (const fn of [...world.progress]) fn({ opKey: 'open', runId: 'run1', total: 3, origin: 'human', ...e }); };
  emit({ done: 2, note: 'Loading the model', cancellable: true });
  await settleAll();
  press('Contoso7');
  await settleAll();
  assert.match(p.says(), /Cancel this open to open Contoso7 instead\./, 'before the point of no return, Cancel can still make way for the pick');
  emit({ done: 3, note: 'Saving it to your list', cancellable: false });
  await settleAll();
  const seen = p.says();
  assert.match(seen, /It can no longer be stopped\./);
  assert.doesNotMatch(seen, /Cancel this open/, 'the bar never offers a Cancel it does not have');
  assert.equal(byTest(p.view(), 'hub-bar-pending'), null, 'no pick line under a bar that cannot stop');
  p.h.unmount();
});

// 2 The window "properties" step (5.5 to 6.6 s in the tester's live opens) was not named in the log's slowest three. Its cause, proved in a lane with a
//   trace of the engine's model thread (cx3.md): the open's own change starts the measure checks' planner (the tree marks, the Properties card,
//   the Checks page), which holds the one model thread for 1.4 to 2.1 s a time, and the Properties read waited behind two or three of them. The
//   dialog waited for that read before it heard the open's answer, while its bar still said "Connecting for queries".
test('2: the log ranks the window steps with the engine steps, and names a step after the answer as such', async () => {
  const { makeLoader } = await import('./helpers/webview-load.mjs');
  const M = makeLoader()('../../src/openTiming.ts');
  const lines = M.openTimingLines('openLive', {
    totalMs: 8821,
    steps: [{ step: 'connect to the workspace', ms: 2554 }, { step: 'read the model definition', ms: 3092 }, { step: 'connect for queries', ms: 1875 }],
  }, [{ step: 'engine answered', ms: 8847 }, { step: 'properties', ms: 5569, after: true }, { step: 'status', ms: 342, after: true }]);
  assert.ok(lines.includes('[open] window step=properties ms=5569 after the answer'), 'a window step after the answer says so');
  const done = lines.at(-1);
  assert.equal(done, '[open] done openLive total ms=8847 slowest: window properties 5569, read the model definition 3092, connect to the workspace 2554');
});

test('2: the dialog hears the open\'s answer before Properties and the status bar read the new model', () => {
  const ext = src('src', 'extension.ts');
  const block = ext.slice(ext.indexOf("if (modelSwapped) {\n                // Each step of the open"), ext.indexOf('// Resolve the caller BEFORE announcing the model swap'));
  const answer = block.indexOf("panel.webview.postMessage({ type: 'rpcResult', id: msg.id, result })");
  assert.ok(answer > 0, 'the open\'s answer is posted inside the swap block');
  assert.ok(answer < block.indexOf('propGrid.showModel('), 'the answer goes first: Properties reads after it');
  assert.ok(answer < block.indexOf('await refreshStatus()'), 'and the status bar reads after it');
  const swap = block.indexOf("postToPanels({ type: 'reconnected', by: swapBy");   // cx4 adds `, model: swap`
  assert.ok(swap > answer && swap < block.indexOf('propGrid.showModel('), 'the swap follows the answer at once, so Studio names the new model when the dialog closes');
});

test('2: after an open the dialog closes first, then reads the list and the connection again', () => {
  const hub = src('webview', 'src', 'connectionshub.tsx');
  const tail = hub.slice(hub.indexOf('setSwitchTarget(null); setSwitchPurpose(null); setFailedAuthAttempt(null); setPendingDefault(null);'), hub.indexOf('} catch (e) {\n      const failure'));
  assert.ok(tail.indexOf('onClose()') >= 0 && tail.indexOf('onClose()') < tail.indexOf('refresh()'), 'the dialog does not wait on the model thread to close');
});

test('2: the bar names the window\'s step once the engine has finished', async () => {
  const { makeLoader } = await import('./helpers/webview-load.mjs');
  const B = makeLoader()('openbar-model.ts');
  let run = B.startOpenRun('Sales', 'in Contoso Finance', 5, 0);
  run = B.foldOpenProgress(run, { opKey: 'open', runId: 'r', done: 5, total: 5, note: 'Connecting for queries', origin: 'human', cancellable: false }, 1000);
  assert.equal(B.openStepLine(run, 4000), 'Step 5 of 5 · Connecting for queries · 4 s');
  run = B.foldOpenProgress(run, { opKey: 'open', runId: 'r', done: 5, total: 5, note: null, origin: 'human' }, 5000);
  assert.equal(B.openStepLine(run, 6000), 'Showing the model · 6 s', 'the engine is done: the window is showing the model');
  assert.deepEqual(B.openPips(run), ['done', 'done', 'done', 'done', 'done']);
});
