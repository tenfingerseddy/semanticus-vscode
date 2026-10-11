// Join 11, the drafts class: the Connections hub's SQL endpoint form keeps what the person typed when the hub goes away.
//
// The form under a model's row held its fields in component state only, so closing the hub, a tab switch, a restarted engine (App
// keys the page on the session id) or a reload dropped them. It now holds a draft in the shared draft store (webview/src/drafts.ts)
// under `hub|sql|["<row>","<source id or new>"]`, with the saved source it was typed over (or the form as it opened, for a new one).
// The hub opens the form again on it, asks Keep mine / Use the new one when the saved source changed meanwhile (save_sql_source on
// the other door), and Save then names the source it read. The draft ends on a save the engine took, Cancel, and Use the new one,
// never on an unmount. The real connectionshub.tsx runs here (bridge faked, the rows' own render called by hand).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, draftMark, movedBanner, settleAll, heldKeys } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

function makeWorld() {
  const w = {
    records: [{ id: 'c1', kind: 'file', endpoint: '/m/Model.bim', modelName: 'Sales model' }, { id: 'c2', kind: 'file', endpoint: '/m/Other.bim', modelName: 'Other model' }],
    sources: [{ id: 's1', name: 'Warehouse', server: 'wh.example.net', database: 'WH', authMode: 'interactive', tenantId: '' }],
    links: { c1: 's1' },
    calls: [],
  };
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    switch (method) {
      case 'listConnections': return structuredClone(w.records);
      case 'listSqlSources': return structuredClone(w.sources);
      case 'listModelSqlEndpoints': return { rows: w.records.map((r) => ({ connectionId: r.id, sqlEndpoint: w.links[r.id] ? { sourceId: w.links[r.id], name: w.sources.find((s) => s.id === w.links[r.id])?.name } : null })) };
      case 'saveSqlSource': {
        const [id, name, server, database, authMode, tenantId, , expected] = args;
        const was = id ? w.sources.find((s) => s.id === id) : null;
        if (was && expected != null) {
          const now = JSON.stringify({ name: was.name ?? null, server: was.server ?? null, database: was.database ?? null, authMode: was.authMode ?? null, tenantId: was.tenantId ?? null });
          if (now !== expected) throw new Error('This SQL source changed in the model after you opened it, so your version was not saved.');
        }
        const rec = { id: id || 's' + (w.sources.length + 1), name, server, database, authMode, tenantId: tenantId ?? '' };
        w.sources = [...w.sources.filter((s) => s.id !== rec.id), rec];
        return structuredClone(rec);
      }
      case 'setModelSqlEndpoint': { const [sourceId, conn] = args; w.links[conn] = sourceId; return {}; }
      default: return [];
    }
  };
  return w;
}

function open(disk, world, model = '/m/Model.bim') {
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined,
      navigateInStudio: () => undefined, runHostCommand: () => undefined, loadState: disk.loadState, saveState: disk.saveState,
    },
    './features': { planRankingOf: () => 'granted' },
    './pro': { useTier: () => 'pro' },
    './connection': { useConnection: () => ({ context: null, session: null, connectLocal: async () => undefined, connectXmla: async () => undefined, disconnect: async () => undefined, refresh: async () => undefined }) },
    './accountpicker': { AccountPicker: () => null },
    './copy': { defaultSignInMode: (r) => r?.authMode || 'interactive' },
    './surface-escape': { useSurfaceEscape: () => undefined },
    './consent': { ConsentCard: () => null },
    './connectpickers': { DesktopPicker: () => null, WorkspacePicker: () => null },
    './connectpickers-model': { ENV_OPTIONS: [] },
  };
  const pg = makePage(stubs, model);
  return { pg, ConnectionsHub: pg.load('connectionshub.tsx').ConnectionsHub };
}
async function mount({ pg, ConnectionsHub }) {
  const h = pg.mount(() => ConnectionsHub({ open: true, onClose: () => undefined, standalone: true }));
  await settleAll();
  /** One row: the hub draws its rows inline (never as an inner component, F15), so a row is found by the model name it shows. */
  const NAMES = { c1: 'Sales model', c2: 'Other model' };
  const row = (id) => findAll(h.result, (x) => typeof x.props?.className === 'string' && /^hub-row\b/.test(x.props.className))
    .find((x) => find(x, (y) => typeof y.props?.className === 'string' && /hub-row-name/.test(y.props.className) && text(y) === NAMES[id])) ?? null;
  const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
  const form = (id = 'c1') => { const r = row(id); return r && byTest(r, 'hub-sql-form'); };
  return {
    h, row, form,
    name: (id = 'c1') => byTest(form(id), 'hub-sql-name')?.props.value,
    server: (id = 'c1') => byTest(form(id), 'hub-sql-server')?.props.value,
    openForm: async (id = 'c1') => {
      byTest(row(id), 'hub-row-overflow').props.onClick({ currentTarget: { focus: () => undefined } }); await settleAll();
      find(row(id), (el) => el.type?.name === 'MenuItem' && /SQL endpoint|Add SQL endpoint/.test(text(el))).props.onClick(); await settleAll();
    },
    typeName: async (v, id = 'c1') => { byTest(form(id), 'hub-sql-name').props.onChange({ target: { value: v } }); await settleAll(); },
    typeServer: async (v, id = 'c1') => { byTest(form(id), 'hub-sql-server').props.onChange({ target: { value: v } }); await settleAll(); },
    typeDatabase: async (v, id = 'c1') => { byTest(form(id), 'hub-sql-database').props.onChange({ target: { value: v } }); await settleAll(); },
    click: async (testid, id = 'c1') => { await byTest(form(id), testid).props.onClick(); await settleAll(); },
    cancel: async (id = 'c1') => { find(form(id), (el) => el.type === 'button' && text(el) === 'Cancel').props.onClick(); await settleAll(); },
    banner: (id = 'c1') => movedBanner(form(id)),
  };
}
const saves = (w) => w.calls.filter((c) => c[0] === 'saveSqlSource');

test('SQL endpoint form: a name typed, the hub unmounted and mounted again: the form opens on it under its row, marked as a draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeName('Warehouse (prod)');
  a.h.unmount();
  const b = await mount(page);
  assert.ok(b.form(), 'the form is open again under its row');
  assert.equal(b.name(), 'Warehouse (prod)', 'with the typed name');
  assert.equal(b.server(), 'wh.example.net', 'and the rest of the saved source');
  assert.ok(draftMark(b.form()), 'the quiet Draft not saved mark shows');
  assert.equal(b.banner(), null, 'nothing moved, so nothing to choose');
});

test('SQL endpoint form: a new source typed, the page reloads (an engine restart or a webview reload): the form opens on it', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openForm('c2'); await a.typeName('New warehouse', 'c2'); await a.typeServer('new.example.net', 'c2');
  a.h.unmount();
  const b = await mount(open(disk, world));
  assert.equal(b.name('c2'), 'New warehouse');
  assert.equal(b.server('c2'), 'new.example.net');
  assert.ok(draftMark(b.form('c2')));
});

test('SQL endpoint form: the saved source changed meanwhile: the typing stays, the choice is asked, and Keep mine saves naming the source it read', async () => {
  const disk = makeDisk(), world = makeWorld();
  const a = await mount(open(disk, world));
  await a.openForm(); await a.typeName('Mine');
  a.h.unmount();
  world.sources = [{ ...world.sources[0], server: 'moved.example.net' }];   // save_sql_source on the MCP door while the hub was away
  const b = await mount(open(disk, world));
  assert.equal(b.name(), 'Mine', 'the typed name is kept');
  assert.ok(b.banner(), 'the choice is asked');
  await b.click('hub-sql-save');
  assert.equal(saves(world).length, 0, 'Save writes nothing while the choice is open');
  b.banner().props.onKeepMine(); await settleAll();
  await b.click('hub-sql-save');
  const w = saves(world).at(-1);
  assert.ok(w, 'after Keep mine the Save writes');
  assert.equal(w[2], 'Mine');
  assert.equal(JSON.parse(w[8]).server, 'moved.example.net', 'and names the source it read, the newer one');
});

test('SQL endpoint form: a save the engine took, Cancel and Use the new one each end the draft', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeName('Saved name');
  assert.equal(heldKeys(disk).length, 1, 'the typing is held');
  await a.click('hub-sql-save');
  assert.equal(world.sources[0].name, 'Saved name');
  assert.deepEqual(heldKeys(disk), [], 'a save the engine took ends the draft');

  await a.openForm('c2'); await a.typeName('A new one', 'c2'); await a.typeServer('new.example.net', 'c2'); await a.typeDatabase('NEW', 'c2');
  assert.equal(heldKeys(disk).length, 1);
  await a.click('hub-sql-save', 'c2');
  assert.equal(world.sources.length, 2, 'the new source was saved');
  assert.deepEqual(heldKeys(disk), [], 'saving a new source ends its draft too');

  await a.openForm(); await a.typeName('Cancelled');
  await a.cancel();
  assert.deepEqual(heldKeys(disk), [], 'Cancel ends the draft');

  await a.openForm(); await a.typeName('Mine');
  a.h.unmount();
  world.sources = world.sources.map((s) => (s.id === 's1' ? { ...s, server: 'theirs.example.net' } : s));
  const b = await mount(page);
  b.banner().props.onUseNew(); await settleAll();
  assert.equal(b.server(), 'theirs.example.net', 'Use the new one loads the saved source');
  assert.deepEqual(heldKeys(disk), [], 'Use the new one ends the draft');
});

test('SQL endpoint form: a draft on another row or another model is not shown', async () => {
  const disk = makeDisk(), world = makeWorld();
  const page = open(disk, world);
  const a = await mount(page);
  await a.openForm(); await a.typeName('Mine');
  await a.openForm('c2');
  assert.equal(a.name('c2'), '', 'another row opens on its own form');
  assert.equal(draftMark(a.form('c2')), null);
  assert.ok(draftMark(a.row('c1')), 'the row that holds a draft says so');
  await a.openForm('c1');
  assert.equal(a.name('c1'), 'Mine', 'and opening it again finds the typing');
  a.h.unmount();
  const b = await mount(open(disk, world, '/m/Other.bim'));
  assert.equal(b.form(), null, 'another model never opens this model\'s draft');
  assert.equal(draftMark(b.row('c1')), null);
});

// F15 (drafts-sol): the SQL form lost focus after every key. ModelRow is declared inside ConnectionsHub, and the list mounted it as <ModelRow />: a new component type on every
// render, so every keystroke (the form's state lives in the hub) tore the row down and the inputs with it. Only the first characters survived. The row is now called, not mounted.
test('F15: no row is an inner component element, so typing a form field re-renders the same inputs and a full server and database name goes in', async () => {
  const disk = makeDisk(), world = makeWorld();
  const m = await mount(open(disk, world));
  assert.equal(find(m.h.result, (x) => x.type?.name === 'ModelRow'), null, 'rows are drawn inline: an element of a type minted per render would remount its inputs on every key');
  await m.openForm();
  const typeInto = async (field, full) => { for (let i = 1; i <= full.length; i++) { await (field === 'server' ? m.typeServer(full.slice(0, i)) : m.typeDatabase(full.slice(0, i))); } };
  await typeInto('server', 'contoso-sql.database.windows.net');
  await typeInto('database', 'WarehouseProd');
  assert.equal(m.server(), 'contoso-sql.database.windows.net');
  assert.equal(byTestIn(m, 'hub-sql-database'), 'WarehouseProd');
  assert.equal(find(m.h.result, (x) => x.type?.name === 'ModelRow'), null, 'and still none after typing');
});
const byTestIn = (m, id) => find(m.form(), (el) => el.props?.['data-testid'] === id)?.props.value;
