// The environment picked in the Connections address form lands on the saved record (fleet6 and fleet8 rechecks, gap 1: Production was
// saved as "Not set (treated as Development)", so Connections read Development for a production model).
//
// The real connectionshub.tsx runs here. The fake engine keeps the one rule that made the label vanish: a write whose request names a
// model other than the one open is refused (Semanticus.Engine/ModelFence.cs, PersonFence), and the page names the model it showed
// (pagemodel.ts, stamped on every request by bridge.ts). The Studio re-reads the session only after the open, at a time no code here
// controls, so these tests keep the page on the model it showed before: the label must land whatever that timing is.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makePage, find, findAll, text, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

const ADDRESS = 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Demo';
const MODEL = 'Contoso Sales';
const BEFORE = 'file:/m/Northstar';
const FENCE = 'This page was showing another model than the one open now, so nothing was changed. Anything you typed is kept.';

function makeWorld({ unsaved = false, signIn = true, saved = false } = {}) {
  const w = { open: BEFORE, unsaved, records: [], calls: [], refused: [], page: null };
  if (saved) w.records.push({ id: 'x1', kind: 'xmla', endpoint: ADDRESS, database: MODEL, modelName: MODEL, authMode: 'interactive', lastUsedUtc: '2026-11-01T09:00:00Z' });
  const fenced = new Set(['labelConnection', 'forgetConnection', 'rememberXmlaConnection', 'setPublishDestination', 'saveSqlSource']);
  w.rpc = async (method, ...args) => {
    const named = w.page?.pageModelIdentity();
    w.calls.push([method, named, ...args]);
    if (fenced.has(method) && named && named !== w.open) { w.refused.push(method); throw new Error(FENCE); }
    switch (method) {
      case 'sessionInfo': return { sessionId: w.open === BEFORE ? 's1' : 's2', modelIdentity: w.open, hasUnsavedChanges: w.unsaved, source: w.open === BEFORE ? '/m/Northstar' : null };
      case 'listConnections': return structuredClone(w.records);
      case 'openLive': {
        const [endpoint, database] = args;
        if (!signIn) throw new Error('Service identity is not set up on this computer.');
        // The engine keeps the address it was given and the model it resolved, and the open makes the new model the open one.
        const id = 'x-' + database;
        const was = w.records.find((r) => r.id === id || (r.endpoint === endpoint && r.database === database));
        const rec = { ...(was ?? { id }), kind: 'xmla', endpoint, database, modelName: database, authMode: args[2], lastUsedUtc: '2026-11-02T03:00:00Z' };
        w.records = [rec, ...w.records.filter((r) => r !== was)];
        w.open = 'live:' + endpoint + '|' + database; w.unsaved = false;
        return { context: { editing: { modelName: database } } };
      }
      case 'labelConnection': {
        const [id, label] = args;
        const rec = w.records.find((r) => r.id === id);
        if (!rec) throw new Error('No saved connection ' + id);
        rec.label = label || undefined;
        return structuredClone(rec);
      }
      default: return [];
    }
  };
  return w;
}

function open(world) {
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, onProgress: () => () => undefined, onEngineReady: () => () => undefined, announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined,
      navigateInStudio: () => undefined, runHostCommand: () => undefined, loadState: () => null, saveState: () => undefined,
    },
    './features': { planRankingOf: () => 'granted' },
    './pro': { useTier: () => 'pro' },
    './connection': { useConnection: () => ({ context: null, session: null, connectLocal: async () => undefined, connectXmla: async () => undefined, disconnect: async () => undefined, refresh: async () => undefined }) },
    './accountpicker': { AccountPicker: () => null },
    './copy': { defaultSignInMode: (r) => r?.authMode || 'interactive' },
    './surface-escape': { useSurfaceEscape: () => undefined },
    './consent': { ConsentCard: () => null },
    './connectpickers': { DesktopPicker: () => null, WorkspacePicker: () => null },
  };
  const pg = makePage(stubs, BEFORE);   // the Studio names the model it shows (App.tsx setDraftModel)
  world.page = pg.load('pagemodel.ts');
  return { pg, ConnectionsHub: pg.load('connectionshub.tsx').ConnectionsHub };
}

const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
async function addByAddress(world, { environment = 'prod', authMode = 'serviceprincipal' } = {}) {
  const { pg, ConnectionsHub } = open(world);
  const h = pg.mount(() => ConnectionsHub({ open: true, onClose: () => undefined, standalone: false, initialView: 'add' }));
  await settleAll();
  byTest(h.result, 'hub-endpoint-input').props.onChange({ target: { value: ADDRESS } }); await settleAll();
  find(h.result, (el) => el.type === 'input' && el.props.placeholder === 'Optional if the workspace has one model').props.onChange({ target: { value: MODEL } }); await settleAll();
  byTest(h.result, 'hub-authmode-select').props.onChange({ target: { value: authMode } }); await settleAll();
  if (authMode === 'serviceprincipal') { byTest(h.result, 'hub-tenant-input').props.onChange({ target: { value: 'contoso.com' } }); await settleAll(); }
  const env = () => find(findAll(h.result, (el) => el.type === 'label' && [].concat(el.props.children ?? []).some((c) => c === 'Environment'))[0], (el) => el.type === 'select');
  env().props.onChange({ target: { value: environment } }); await settleAll();
  assert.equal(env().props.value, environment, 'the form holds the environment the person picked');
  await find(h.result, (el) => el.type === 'form').props.onSubmit({ preventDefault: () => undefined });
  await settleAll();
  return h;
}
const saved = (world) => world.records.find((r) => r.endpoint === ADDRESS && r.database === MODEL);

test('a new address with Production: the saved record reads prod, though the Studio has not read the new session yet', async () => {
  const world = makeWorld();
  await addByAddress(world);
  assert.equal(world.open, 'live:' + ADDRESS + '|' + MODEL, 'the model opened');
  assert.deepEqual(world.refused, [], 'no write was refused for naming the model the page showed before the open');
  assert.equal(saved(world)?.label, 'prod', 'Connections reads Production for this model, not Not set');
});

test('a new address with Production over unsaved changes: Discard opens it and the saved record still reads prod', async () => {
  const world = makeWorld({ unsaved: true });
  const h = await addByAddress(world);
  const discard = byTest(h.result, 'hub-open-discard');
  assert.ok(discard, 'the unsaved-changes question shows first');
  assert.equal(world.open, BEFORE, 'nothing opened before the answer');
  await discard.props.onClick(); await settleAll();
  assert.equal(world.open, 'live:' + ADDRESS + '|' + MODEL, 'Discard opened the model');
  assert.equal(saved(world)?.label, 'prod', 'the environment picked before the question lands on the record the open saved');
});

test('an address saved under another spelling, with Production: the record the open saves reads prod too (PR #409 Codex P1)', async () => {
  // The engine keys a saved record on the exact address bytes (ConnectionRegistry.IdFor), so the same model typed with another host case or
  // %20 for a space saves a second record, first in the list. The agent policy reads the first record with the same address in any case
  // (ConnectionRegistry.FindByEndpoint), so that record must carry the environment the person picked.
  const world = makeWorld();
  world.records.push({ id: 'x0', kind: 'xmla', endpoint: 'powerbi://API.powerbi.com/v1.0/myorg/Contoso%20Demo', database: MODEL, modelName: MODEL, authMode: 'interactive', lastUsedUtc: '2026-11-01T09:00:00Z' });
  await addByAddress(world);
  assert.equal(world.open, 'live:' + ADDRESS + '|' + MODEL, 'the model opened');
  assert.equal(world.records.length, 2, 'the open saved a second record under the spelling typed now');
  const plain = (a) => decodeURI(a).toLowerCase();
  const read = world.records.find((r) => plain(r.endpoint) === plain(ADDRESS) && r.database.toLowerCase() === MODEL.toLowerCase());
  assert.equal(read?.label, 'prod', 'the record the agent policy reads carries Production');
});

test('an address already saved, with Production, whose connect does not finish (no sign-in): the saved record reads prod', async () => {
  const world = makeWorld({ signIn: false, saved: true });
  await addByAddress(world);
  assert.equal(world.open, BEFORE, 'the connect did not finish, so the open model is the same');
  assert.equal(world.records.length, 1, 'no second record');
  assert.equal(saved(world)?.label, 'prod', 'the saved model carries the environment the person picked');
});

test('a new address whose connect does not finish saves nothing: the form keeps no record and labels none', async () => {
  const world = makeWorld({ signIn: false });
  await addByAddress(world);
  assert.equal(world.records.length, 0, 'a failed connect still saves no record');
  assert.equal(world.calls.filter((c) => c[0] === 'labelConnection').length, 0, 'and labels nothing');
});

test('Not set stays Not set: no label call when the person picked no environment', async () => {
  const world = makeWorld();
  await addByAddress(world, { environment: '' });
  assert.equal(saved(world)?.label, undefined);
  assert.equal(world.calls.filter((c) => c[0] === 'labelConnection').length, 0);
});

test('the label request names the model open at that moment, and only that request does (the page keeps naming what it shows)', async () => {
  const world = makeWorld();
  await addByAddress(world);
  const label = world.calls.find((c) => c[0] === 'labelConnection');
  assert.equal(label?.[1], world.open, 'the label request named the model the open made');
  assert.equal(world.page.pageModelIdentity(), BEFORE, 'the page itself still names the model it showed until the Studio reads the new session');
});
