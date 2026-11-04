// T326, finding F-302 (Codex PR #407 comment 4435488517): keep SQL endpoint edits typed while the saved source is still being read.
//
// A row that names its saved SQL endpoint opens the form on what the row knows (f172 fault 4) and asks for the saved sources again.
// While that read is on its way the form is editable but has no starting point, so the first read reseeded EVERY field and the words
// the person typed in the wait were lost. Now the read fills only the fields the person has not touched; the typed ones stay, they are
// a draft over the saved source (marked, held, and Save names the source it read). The real connectionshub.tsx runs here.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, draftMark, movedBanner, settleAll, heldKeys } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

const SOURCE = { id: 'sql1', name: 'Fabrikam ledger', server: 'fabrikam-sql.database.windows.net', database: 'Ledger', authMode: 'devicecode', tenantId: 'fabrikam.com' };

function makeWorld() {
  const w = { reads: 0, pending: [], calls: [], source: structuredClone(SOURCE) };
  w.rpc = (method, ...args) => {
    w.calls.push([method, ...args]);
    switch (method) {
      case 'listConnections': return Promise.resolve([{ id: 'x1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso', database: 'Contoso Sales', modelName: 'Contoso Sales', authMode: 'interactive', tenantId: 'contoso.com' }]);
      case 'listAccountProfiles': return Promise.resolve([]);
      case 'probeConnectionAccounts': return Promise.resolve([]);
      case 'listSqlSources': {
        // The first read fails (the engine was busy); every later read waits until the test lets it arrive.
        if (++w.reads === 1) return Promise.reject(new Error('The engine is busy.'));
        return new Promise((resolve) => w.pending.push(() => resolve([structuredClone(w.source)])));
      }
      case 'listModelSqlEndpoints': return Promise.resolve({ rows: [{ connectionId: 'x1', modelName: 'Contoso Sales', sqlEndpoint: { sourceId: 'sql1', name: SOURCE.name, server: SOURCE.server, database: SOURCE.database } }] });
      case 'saveSqlSource': {
        const [id, name, server, database, authMode, tenantId] = args;
        w.source = { id, name, server, database, authMode, tenantId: tenantId ?? '' };
        return Promise.resolve(structuredClone(w.source));
      }
      case 'setModelSqlEndpoint': return Promise.resolve({});
      case 'sessionInfo': return Promise.resolve({ sessionId: 's1', hasUnsavedChanges: false });
      default: return Promise.resolve([]);
    }
  };
  /** Let every read that is waiting arrive now. */
  w.arrive = async () => { const go = w.pending.splice(0); go.forEach((f) => f()); await settleAll(); };
  return w;
}

function open(world, disk) {
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, onProgress: () => () => undefined, onEngineReady: () => () => undefined, announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined,
      navigateInStudio: () => undefined, runHostCommand: () => undefined, loadState: disk.loadState, saveState: disk.saveState,
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
  return { pg, ConnectionsHub: pg.load('connectionshub.tsx').ConnectionsHub };
}

const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

async function openFormBeforeRead() {
  const world = makeWorld(), disk = makeDisk();
  const page = open(world, disk);
  const h = page.pg.mount(() => page.ConnectionsHub({ open: true, onClose: () => undefined, standalone: true }));
  await settleAll();
  const row = () => findAll(h.result, (x) => typeof x.props?.className === 'string' && /^hub-row\b/.test(x.props.className))
    .find((x) => find(x, (y) => typeof y.props?.className === 'string' && /hub-row-name/.test(y.props.className) && text(y) === 'Contoso Sales')) ?? null;
  byTest(row(), 'hub-row-overflow').props.onClick({ currentTarget: { focus: () => undefined } }); await settleAll();
  find(row(), (el) => el.type?.name === 'MenuItem' && /SQL endpoint/.test(text(el))).props.onClick(); await settleAll();
  assert.ok(byTest(row(), 'hub-sql-form'), 'the form is open');
  assert.equal(world.pending.length, 1, 'the saved sources are being read again, and that read has not arrived');
  const form = () => byTest(row(), 'hub-sql-form');
  const value = (id) => byTest(form(), id)?.props.value;
  const type = async (id, v) => { byTest(form(), id).props.onChange({ target: { value: v } }); await settleAll(); };
  const picker = () => find(form(), (el) => el.type?.name === 'AccountPicker');
  return { world, disk, h, row, form, value, type, picker };
}

test('T326: words typed while the saved source is still being read stay when it arrives, and only the untouched fields fill', async () => {
  const f = await openFormBeforeRead();
  // The form opened on what the row knows. Its sign-in starts as the model's (the row does not carry the source's).
  assert.equal(f.value('hub-sql-server'), SOURCE.server);
  assert.equal(f.picker().props.tenantId, 'contoso.com', 'before the read, the sign-in is the model\'s');

  await f.type('hub-sql-server', 'fabrikam-sql-2.database.windows.net');
  await f.type('hub-sql-name', 'Fabrikam ledger copy');
  await f.world.arrive();

  assert.equal(f.value('hub-sql-server'), 'fabrikam-sql-2.database.windows.net', 'the server the person typed stays');
  assert.equal(f.value('hub-sql-name'), 'Fabrikam ledger copy', 'the name the person typed stays');
  assert.equal(f.value('hub-sql-database'), 'Ledger', 'an untouched field holds the saved value');
  assert.equal(f.picker().props.tenantId, 'fabrikam.com', 'the untouched tenant fills from the saved source');
  assert.equal(f.picker().props.mode, 'devicecode', 'the untouched sign-in fills from the saved source');
  assert.equal(movedBanner(f.form()), null, 'the read that arrived is not taken as a change made elsewhere');
  assert.ok(draftMark(f.form()), 'the typed words are marked as a draft');
  assert.ok(heldKeys(f.disk).some(([, scope]) => scope.startsWith('hub|sql|') && scope.includes('sql1')), 'and held, so closing the hub keeps them');

  // Save writes the typed words over the saved source it read, and the untouched fields carry the saved values.
  await byTest(f.form(), 'hub-sql-save').props.onClick(); await f.world.arrive();
  const save = f.world.calls.find((c) => c[0] === 'saveSqlSource');
  assert.ok(save, 'Save wrote');
  assert.deepEqual(save.slice(1, 7), ['sql1', 'Fabrikam ledger copy', 'fabrikam-sql-2.database.windows.net', 'Ledger', 'devicecode', 'fabrikam.com']);
  f.h.unmount();
});

test('T326 guard: with nothing typed in the wait, the form follows the saved source and holds no draft', async () => {
  const f = await openFormBeforeRead();
  await f.world.arrive();
  assert.equal(f.value('hub-sql-server'), SOURCE.server);
  assert.equal(f.picker().props.tenantId, 'fabrikam.com');
  assert.equal(f.picker().props.mode, 'devicecode');
  assert.equal(draftMark(f.form()), null, 'nothing typed, nothing marked');
  assert.equal(movedBanner(f.form()), null);
  assert.equal(heldKeys(f.disk).filter(([, scope]) => scope.startsWith('hub|sql|')).length, 0, 'nothing held');
  f.h.unmount();
});
