// cx5 (the 1.7.4-rc3 tester, 3 Nov): two blockers left on the open path.
//
//   1 At "Step 5 of 5 · The model is open", for 1 to 3 s before the dialog closed, the status bar read "zz-dax-pilot2... · 8 measures"
//     (pilot2 has 12), Properties read "Checking 6 of 8" and the tree's top row named the old model over pilot2's tables, 5 of 5 opens.
//     Cause, from the tester's own output log and status bar samples: the engine swaps the session and says so ("didChange rev=0 Opened
//     <B>") and only then connects for queries (2.6 s) before it answers. The window's model/didChange handler painted the status bar's
//     name from B's session (refreshSyncChip), read B's tables into the tree (tree.refresh) and pushed A's Properties again, while the
//     measure count, the tree's row and the check card still held A's values. cx4 covered only the time after the answer.
//   2 Connections > Workspace model > Paste an address instead > Service identity: "Connect and remember" sat below the fold, and the
//     open showed no "Step n of 5", no seconds and no Cancel. The hub drew its bar only on the saved list and the Workspace model screen.
//
//   Rule now: from the press, every per-model surface drops any value whose session is not the one being opened. The press says
//   "Opening <B>..." (no figure of A); a notification that names a session the window does not show moves every surface to
//   "Reading <B>..." at once; the open's own reads then fill in B's values. A stopped or failed open reads A again.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = (...p) => readFileSync(resolve(root, ...p), 'utf8');
const allText = (el) => {
  if (el == null || typeof el === 'boolean') return '';
  if (typeof el === 'string' || typeof el === 'number') return String(el);
  if (Array.isArray(el)) return el.map(allText).join(' ');
  return allText(el.props?.children);
};
const INNER = new Set(['AccountDialog', 'AccountMenu', 'JobsPanel']);
const expand = (el) => {
  if (Array.isArray(el)) return el.map(expand);
  if (!el || typeof el !== 'object') return el;
  if (typeof el.type === 'function' && INNER.has(el.type.name)) return expand(el.type(el.props ?? {}));
  if (el.props?.children === undefined) return el;
  return { ...el, props: { ...el.props, children: expand(el.props.children) } };
};
const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

const A = { sessionId: 's-a', name: 'Contoso Local' };
const B = { sessionId: 's-b', name: 'Contoso Published' };

// ---- 1: the host's open watch, driven in the order the tester's log shows -----------------------------------------------------------------------------------

/** What the window's three per-model surfaces show, written by the real OpenWatch through the same calls extension.ts makes. */
function makeScreen() {
  const s = { shown: A.sessionId, status: `${A.name} · 8 measures`, row: `${A.name} · Local model · 8 measures`, props: `${A.name} · Checking 6 of 8`, log: [] };
  const M = makeLoader()('../../src/openWatch.ts');
  const R = makeLoader()('../../src/swapReading.ts');
  const surfaces = {
    opening: (name) => { s.status = M.openingLine(name); s.row = `${A.name} · ${M.openingLine(name)}`; s.props = M.openingLine(name); s.log.push(['opening', name]); },
    reading: (swap) => { s.status = `${swap.name} · ${R.swapReadingLine(swap.name)}`; s.row = `${swap.name} · Reading...`; s.props = R.swapReadingLine(swap.name); s.log.push(['reading', swap.sessionId]); },
    restore: () => { s.status = `${A.name} · 8 measures`; s.row = `${A.name} · Local model · 8 measures`; s.props = `${A.name} · Checking 6 of 8`; s.log.push(['restore']); },
  };
  const watch = new M.OpenWatch(surfaces, () => s.shown);
  return { s, watch, M };
}
const showsA = (s) => [s.status, s.row, s.props].some((t) => /8 measures|6 of 8|Local model/.test(t));

test('1: from the press, the status bar, the tree row and Properties show no figure of the old model', () => {
  const { s, watch } = makeScreen();
  watch.begin(B.name);
  assert.equal(showsA(s), false, `at the press: ${JSON.stringify(s)}`);
  assert.match(s.status, /Opening Contoso Published\.\.\./);
  // A count read of A that was in flight lands now: it is dropped.
  assert.equal(watch.drops(A.sessionId), true, 'a value read for the model being left is dropped while the open runs');
});

test('1: the engine swaps and says so before it answers (Step 5): every surface names B and reads it, none keeps A', () => {
  const { s, watch } = makeScreen();
  watch.begin(B.name);
  // "didChange rev=0 origin=system label=Opened Contoso Published", 2.6 s before the answer, while the window still shows A.
  assert.equal(watch.seen(B.sessionId, 'Contoso Published'), 'moved');
  assert.deepEqual(s.log.at(-1), ['reading', B.sessionId]);
  assert.equal(showsA(s), false, `at Step 5, before the answer: ${JSON.stringify(s)}`);
  assert.match(s.status, /^Contoso Published · Reading Contoso Published\.\.\.$/);
  assert.equal(watch.drops(A.sessionId), true, 'a late read of A after the swap is dropped');
  assert.equal(watch.drops(B.sessionId), false, 'B\'s own reads fill in');
  // A second notice of the same swap does not paint again.
  assert.equal(watch.seen(B.sessionId), 'moved');
  assert.equal(s.log.filter((x) => x[0] === 'reading').length, 1);
});

test('1: once the window shows B, B\'s notices are plain changes; a notice for the shown model during the open changes nothing', () => {
  const { s, watch } = makeScreen();
  const id = watch.begin(B.name);
  assert.equal(watch.seen(A.sessionId), 'shown', 'a change to the model still open is not a swap');
  watch.seen(B.sessionId, B.name);
  s.shown = B.sessionId;   // setStatusFromInfo read B
  assert.equal(watch.seen(B.sessionId), 'shown');
  watch.end(id, true);
  assert.equal(watch.drops(A.sessionId), false, 'after the open, nothing is masked');
  assert.equal(s.log.some((x) => x[0] === 'restore'), false, 'a good open reads nothing of A again');
});

test('1: a stopped or failed open shows the model that stays open again; an older open\'s end does not touch a newer one', () => {
  const { s, watch } = makeScreen();
  const first = watch.begin(B.name);
  watch.end(first, false);
  assert.deepEqual(s.log.at(-1), ['restore']);
  assert.equal(showsA(s), true, 'Cancel: A is open and says so');
  const one = watch.begin(B.name), two = watch.begin('Contoso Other');
  watch.end(one, false);
  assert.notDeepEqual(s.log.at(-1), ['restore'], 'the older open\'s end leaves the newer open\'s mask');
  assert.equal(watch.drops(A.sessionId), true);
  watch.end(two, true);
  assert.equal(watch.drops(A.sessionId), false);
});

test('1: the name the press shows comes from the open\'s own arguments', () => {
  const { M } = makeScreen();
  assert.equal(M.openTargetName('openLive', ['powerbi://api.powerbi.com/v1.0/myorg/Contoso', 'Contoso Published', 'serviceprincipal']), 'Contoso Published');
  assert.equal(M.openTargetName('openLive', ['powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Retail', null]), 'the model in Contoso Retail');
  assert.equal(M.openTargetName('open', ['/home/user/models/Contoso Retail/ContosoRetail.SemanticModel', false]), 'ContosoRetail.SemanticModel',
    'a file open names the file: the model inside can carry another name');
  assert.equal(M.openTargetName('open', ['/home/user/models/ContosoRetail.SemanticModel/definition']), 'ContosoRetail.SemanticModel');
  assert.equal(M.openTargetName('open', ['C:\\models\\Contoso.bim']), 'Contoso.bim');
  assert.equal(M.openTargetName('openLocal', ['localhost:51234', 'Contoso Desktop']), 'Contoso Desktop');
  assert.equal(M.openTargetName('openLocal', ['localhost:51234', null]), 'the Power BI Desktop model');
  assert.equal(M.openedNameOf('Opened Contoso Published'), 'Contoso Published', 'the engine\'s own label names the model');
  assert.equal(M.openedNameOf('Renamed a measure'), undefined);
  assert.equal(M.isOpenMethod('openLive') && M.isOpenMethod('open') && M.isOpenMethod('openLocal'), true);
  assert.equal(M.isOpenMethod('listConnections'), false);
});

// ---- 1: extension.ts runs the watch on every path the tester's log shows -------------------------------------------------------------------------------------

test('1: the model/didChange handler asks the watch first, and a moved session reads B instead of repainting A', () => {
  const ext = src('src', 'extension.ts');
  const start = ext.indexOf("connected.onNotification('model/didChange'");
  const body = ext.slice(start, ext.indexOf("connected.onNotification('entitlement/didChange'", start));
  const ask = body.indexOf('openWatch.seen(n.sessionId');
  assert.ok(ask > 0, 'the handler asks the watch about the session');
  for (const later of ['refreshChecksCount()', 'tree.refresh()', 'propGrid.refresh()', 'refreshSyncChip()']) {
    const at = body.indexOf(later);
    assert.ok(at < 0 || at > ask, `${later} runs only after the session check`);
  }
  assert.match(body.slice(ask, ask + 300), /if \(moved\) followSwap\(n\.sessionId\)/, 'a moved session is followed');
  const follow = ext.slice(ext.indexOf('function followSwap('), ext.indexOf('function followSwap(') + 500);
  assert.match(follow, /tree\?\.refresh\(\)[\s\S]*refreshStatus\(\)/, 'and read in full (setStatusFromInfo), not half-painted');
});

test('1: the relay starts the watch before the engine is asked, and ends it on the answer and on a failure', () => {
  const ext = src('src', 'extension.ts');
  const relay = ext.slice(ext.indexOf('function studioRelayHandler'), ext.indexOf('function navigateStudio'));
  const begin = relay.indexOf('openWatch.begin(');
  const send = relay.indexOf('caller.sendRequest(msg.method');
  assert.ok(begin > 0 && begin < send, 'the press masks before the request goes');
  assert.match(relay, /openWatch\.end\(openId, true\)/);
  assert.match(relay, /catch \(e: any\) \{[\s\S]{0,300}openWatch\.end\(openId, false\)/, 'a stopped or failed open reads the model that stays open');
});

test('1: every late read of the old model is dropped: the checks count, the status from a session read, the tree marks', () => {
  const ext = src('src', 'extension.ts');
  const count = ext.slice(ext.indexOf('function refreshChecksCount'), ext.indexOf('function showSwapReading'));
  assert.match(count, /openWatch\.drops\(page\.sessionId\)/, 'a count read for the model being left is dropped');
  const info = ext.slice(ext.indexOf('function setStatusFromInfo'), ext.indexOf('let offeredFileRecoverySession'));
  assert.match(info, /openWatch\.drops\(info\?\.sessionId\)[\s\S]{0,120}showOpening/, 'a session read of the model being left keeps the mask');
});

test('1: Properties says the open line, and the tree row drops its figures while the open runs', () => {
  const pg = src('media', 'propgrid', 'propgrid.js');
  assert.match(pg, /m\.type === 'reading'[^\n]*m\.line/, 'Properties shows the line the host sends');
  const ext = src('src', 'extension.ts');
  assert.match(ext, /if \(id\?\.opening\) \{ row\.description = openingLine\(id\.opening\)/, 'the tree row names the open, not the old count');
});

// ---- 2: the pasted-address screen ---------------------------------------------------------------------------------------------------------------------------

function makeWorld() {
  const w = { calls: [], inflight: [], ready: new Set(), progress: new Set() };
  w.rpc = (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'openLive' || method === 'open') return new Promise((res, rej) => w.inflight.push({ method, res, rej }));
    switch (method) {
      case 'listConnections': return Promise.resolve([{ id: 'r1', kind: 'file', endpoint: '/home/user/models/Contoso1.SemanticModel', modelName: 'Contoso1', lastUsedUtc: '2026-11-03T09:00:00Z' }]);
      case 'sessionInfo': return Promise.resolve({ sessionId: 's-a', hasUnsavedChanges: false });
      case 'cancelOpen': return Promise.resolve({ stopped: true, state: 'stopping' });
      case 'listModelSqlEndpoints': return Promise.resolve({ rows: [] });
      case 'connectionContext': return Promise.resolve(null);
      default: return Promise.resolve([]);
    }
  };
  w.emit = (e) => { for (const fn of [...w.progress]) fn({ opKey: 'open', runId: 'run7', total: 5, origin: 'human', ...e }); };
  return w;
}
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
async function toAddForm(p) {
  await settleAll();
  byTest(p.view(), 'hub-account-button').props.onClick();
  await settleAll();
  byTest(p.view(), 'hub-other-sign-in').props.onClick();
  await settleAll();
  byTest(p.view(), 'hub-endpoint-input').props.onChange({ target: { value: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso' } });
  await settleAll();
  byTest(p.view(), 'hub-authmode-select').props.onChange({ target: { value: 'serviceprincipal' } });
  await settleAll();
  byTest(p.view(), 'hub-tenant-input').props.onChange({ target: { value: 'contoso.com' } });
  await settleAll();
}
const slotOf = (tree) => find(tree, (el) => typeof el.props?.className === 'string' && el.props.className.split(' ').includes('hub-bar-slot'));
const mainOf = (tree) => find(tree, (el) => typeof el.props?.className === 'string' && el.props.className.split(' ').includes('hub-main'));

test('2: on the pasted-address form, "Connect and remember" sits in the bar at the foot of the dialog, which never scrolls', async () => {
  const world = makeWorld();
  const p = page(world);
  await toAddForm(p);
  const v = p.view();
  const slot = slotOf(v);
  assert.ok(slot, 'the bar row');
  const act = byTest(slot, 'hub-add-connect');
  assert.ok(act, 'the Connect button is in the bar row, out of the page that scrolls');
  assert.equal(allText(act), 'Connect and remember');
  assert.equal(findAll(mainOf(v), (el) => allText(el) === 'Connect and remember' && el.type === 'button').length, 0, 'one Connect on screen, not a second at the end of the form');
  assert.match(allText(slot), /Signs in with Service identity/, 'the bar says how it signs in');
  assert.equal(act.props.disabled, false);
});

test('2: the pasted-address open shows the same bar as a row open: "Step n of 5", the seconds and Cancel', async () => {
  const world = makeWorld();
  const p = page(world);
  await toAddForm(p);
  byTest(slotOf(p.view()), 'hub-add-connect').props.onClick();
  await settleAll();
  assert.ok(world.inflight.some((x) => x.method === 'openLive'), 'the open runs');
  let slot = slotOf(p.view());
  assert.match(allText(byTest(slot, 'hub-bar-step')), /Starting the open · \d+ s/, 'the bar counts from the press, before the engine names a step');
  world.emit({ done: 2, note: 'Connecting to the workspace', cancellable: true });
  await settleAll();
  slot = slotOf(p.view());
  assert.match(allText(byTest(slot, 'hub-bar-step')), /^Step 2 of 5 · Connecting to the workspace · \d+ s$/);
  assert.equal(findAll(slot, (el) => el.props?.['data-pip'] != null).length, 5, 'five step marks');
  const cancel = byTest(slot, 'hub-bar-cancel');
  assert.ok(cancel, 'Cancel');
  cancel.props.onClick();
  await settleAll();
  assert.ok(world.calls.some((c) => c[0] === 'cancelOpen' && c[1] === 'run7'), 'Cancel stops this open');
  world.inflight.find((x) => x.method === 'openLive').rej(new Error('Stopped. Contoso did not open, and nothing changed.'));
  await settleAll();
  assert.match(allText(slotOf(p.view())), /did not open, and nothing changed/, 'the bar says it stopped');
  assert.ok(byTest(p.view(), 'hub-endpoint-input'), 'a stopped open keeps the form and what was typed');
  assert.equal(byTest(p.view(), 'hub-endpoint-input').props.value, 'powerbi://api.powerbi.com/v1.0/myorg/Contoso');
});

// Found in the lane (reverse open, pilot2 to a local model, 1 sample of 0.2 s): another repaint of the tree (a check-state push) read the new
// model's tables before the swap notice landed, under the old model's row. While an open runs, the tree's top read names its session first.
test('1: while an open runs, the tree\'s top read asks which session it reads and hands it to the watch', () => {
  const ext = src('src', 'extension.ts');
  const gc = ext.slice(ext.indexOf('    async getChildren(n?: TreeNode)'), ext.indexOf("sendRequest<TreeNode[]>('listTree', null)"));
  assert.match(gc, /if \(openWatch\.opening\) \{[\s\S]*sendRequest<SessionInfo>\('sessionInfo'\)[\s\S]*openWatch\.seen\(/, 'the tree asks before it reads the tables');
});

// Found in the lane under load (a reverse open while the engine battery ran): the tree row kept the open model's figures for 1.2 s after the
// press, because a full repaint waits on the engine's tree read. The press repaints the row alone, from what the window already knows.
test('1: the press repaints the tree\'s model row alone, without waiting on a read of the tables', () => {
  const ext = src('src', 'extension.ts');
  const mark = ext.slice(ext.indexOf('    markOpening(name: string): void {'), ext.indexOf('    markOpening(name: string): void {') + 600);
  assert.match(mark, /this\._emitter\.fire\(this\.identityEl\)/, 'only the row is drawn again');
  const node = ext.slice(ext.indexOf('    private identityNode(): TreeNode {'), ext.indexOf('    private identityNode(): TreeNode {') + 500);
  assert.match(node, /this\.identityEl/, 'the row is one element across repaints, so it can be drawn alone');
});
