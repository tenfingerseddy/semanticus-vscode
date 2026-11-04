// fx171 (Kane, 30 Oct 09:0x, and the cx1 connections tester round). A model in a client's tenant, opened as an account of that
// tenant. Each test runs the real Connections page (or the real picker view) in the hook runtime with a stand-in engine.
// Kane A1: the account line read "as <home> (was <client account>)", so the account in use was unclear.
// Kane A2: the SQL endpoint sign-in went to the home tenant; the model's own connection already knows its tenant.
// cx1 blocker 1: a browser sign-in from the workspace picker could not reach a client's tenant.
// cx1 blocker 2: Cancel on "Sign in another account" left every Open button disabled until a reload.
// cx1 blocker 3: the footer said "Publish: Not set yet" while Jobs named the model.
// cx1 blocker 4: the account menu's "Sign in" did not sign in, and "Other ways to sign in" opened Add a model by its address.
// cx1 gap 5: the account choice said "Open with the default account" and named no account and no tenant.
// cx1 gap 10: the tenant box lost its label once filled, so it was a bare tenant id.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeDisk, makePage, find, findAll, text, settleAll } from './helpers/drafts-mount.mjs';
import { makeLoader } from './helpers/webview-load.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const GUEST = '22222222-bbbb-4bbb-8bbb-222222222222';
const deep = (el) => [].concat(el?.props?.children ?? []).flat(Infinity).map((c) => typeof c === 'string' || typeof c === 'number' ? String(c) : c && typeof c === 'object' ? deep(c) : '').join('');
// The page draws its account choice and account menu as inner components; the hook runtime leaves those as elements, so
// a test draws them (they hold no hooks of their own) and looks inside.
const INNER = new Set(['AccountDialog', 'AccountMenu', 'JobsPanel']);
const expand = (el) => {
  if (Array.isArray(el)) return el.map(expand);
  if (!el || typeof el !== 'object') return el;
  if (typeof el.type === 'function' && INNER.has(el.type.name)) return expand(el.type(el.props ?? {}));
  if (el.props?.children === undefined) return el;
  return { ...el, props: { ...el.props, children: expand(el.props.children) } };
};
const byTest = (tree, id) => find(expand(tree), (el) => el.props?.['data-testid'] === id);

function makeWorld({ record = {}, profiles = [] } = {}) {
  const w = {
    records: [{ id: 'x1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Workspace%20A', database: 'Contoso Sales', modelName: 'Contoso Sales', authMode: 'interactive', tenantId: GUEST, lastAccount: 'alex@contoso-a.example', ...record }],
    profiles,
    calls: [],
    pending: [],
  };
  w.rpc = (method, ...args) => {
    w.calls.push([method, ...args]);
    switch (method) {
      case 'listConnections': return Promise.resolve(structuredClone(w.records));
      case 'listAccountProfiles': return Promise.resolve(structuredClone(w.profiles));
      case 'probeConnectionAccounts': return Promise.resolve([]);
      case 'listSqlSources': return Promise.resolve([]);
      case 'listModelSqlEndpoints': return Promise.resolve({ rows: [] });
      case 'sessionInfo': return Promise.resolve({ sessionId: 's1', hasUnsavedChanges: false });
      case 'openLive': return new Promise((r) => w.pending.push(r));   // a browser sign-in nobody finishes
      default: return Promise.resolve([]);
    }
  };
  return w;
}

function open(world, extra = {}) {
  const picker = { last: null };
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, onProgress: () => () => undefined, onEngineReady: () => () => undefined, announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined,
      navigateInStudio: () => undefined, runHostCommand: () => undefined, loadState: makeDisk().loadState, saveState: makeDisk().saveState,
    },
    './features': { planRankingOf: () => 'granted' },
    './pro': { useTier: () => 'pro' },
    './connection': { useConnection: () => ({ context: extra.context ?? null, session: extra.session ?? null, connectLocal: async () => undefined, connectXmla: async () => ({ ok: true }), disconnect: async () => undefined, refresh: async () => undefined }) },
    './accountpicker': { AccountPicker: () => null },
    './copy': { defaultSignInMode: (r) => r?.authMode || 'interactive' },
    './surface-escape': { useSurfaceEscape: () => undefined },
    './consent': { ConsentCard: () => null },
    './connectpickers': { DesktopPicker: () => null, WorkspacePicker: () => null },
  };
  const pg = makePage(stubs);
  return { pg, picker, ConnectionsHub: pg.load('connectionshub.tsx').ConnectionsHub };
}

function workspace(props = {}) {
  const calls = [];
  const pg = makePage({ './bridge': {
    rpc: async (...args) => { calls.push(['automatic', ...args]); return []; },
    rpcPressed: async (...args) => { calls.push(['pressed', ...args]); return []; },
    onEngineState: () => () => {}, runHostCommand: () => {},
  } });
  const { WorkspacePicker } = pg.load('connectpickers.tsx');
  const h = pg.mount(() => WorkspacePicker({ pressedFirst: true, onBack() {}, onPasteAddress() {}, onConnect: async () => null, ...props }));
  return { h, calls };
}

test('FX174: a first workspace sign-in waits for the tenant or List workspaces, including another account', async () => {
  for (const anotherAccount of [false, true]) {
    const { h, calls } = workspace({ anotherAccount });
    await settleAll();
    assert.equal(calls.filter((c) => c[0] === 'pressed').length, 0, 'opening the page cannot start a tenantless browser');
    assert.equal(h.result.props.otherOpen, true, 'the tenant and List workspaces are visible');
    h.result.props.onTenant(GUEST); await settleAll();
    assert.equal(calls.filter((c) => c[0] === 'pressed').length, 0, 'typing does not sign in');
    await h.result.props.onRetry(); await settleAll();
    assert.deepEqual(calls.find((c) => c[0] === 'pressed').slice(1, 4), ['listWorkspaces', 'interactive', GUEST]);
    h.unmount();
  }
});

test('FX174: a home-only account can list workspaces in one press without a tenant', async () => {
  const { h, calls } = workspace(); await settleAll(); calls.length = 0;
  await h.result.props.onRetry(); await settleAll();
  assert.deepEqual(calls[0].slice(0, 4), ['pressed', 'listWorkspaces', 'interactive', null]);
  assert.equal(h.result.props.workspaces.kind, 'ready');
  h.unmount();
});

test('FX174: the browser keeps the saved tenant on entry and on Use another account', async () => {
  const { h, calls } = workspace({ initialTenant: GUEST }); await settleAll();
  assert.equal(h.result.props.tenant, GUEST);
  assert.equal(calls.find((c) => c[0] === 'pressed')[3], GUEST);
  await h.result.props.onAnotherAccount(); await settleAll();
  assert.equal(calls.at(-1)[3], GUEST);
  assert.equal(calls.at(-1)[5], true);
  h.unmount();
});

test('FX174: the last browser connection and a saved home account supply the picker tenant', () => {
  const { signInToReuse } = makeLoader({})('connectpickers-model.ts');
  assert.equal(signInToReuse(makeWorld().records, null).tenantId, GUEST);
  const home = { tenantId: null, accountTenantId: 'home.example', lastUseUtc: '2026-10-30', family: 'interactive', signedIn: true };
  assert.equal(signInToReuse([], null, [home]).tenantId, 'home.example');
  assert.deepEqual(signInToReuse([{ id: 'sp', kind: 'xmla', authMode: 'serviceprincipal' }], null, [home], 'interactive'),
    { authMode: 'interactive', tenantId: 'home.example' }, 'a browser choice uses the saved home account even after a service identity');
});

test('FX174: the account menu reuses the saved home tenant after a service identity', async () => {
  const page = open(makeWorld({ record: { authMode: 'serviceprincipal', tenantId: null, lastAccount: null },
    profiles: [{ id: 'home', username: 'alex@home.example', family: 'interactive', tenantId: null, accountTenantId: 'home.example', signedIn: true }] }));
  const m = await mount(page);
  byTest(m.h.result, 'hub-account-button').props.onClick(); await settleAll();
  byTest(m.h.result, 'hub-sign-in-another-top').props.onClick(); await settleAll();
  const picker = find(m.h.result, (el) => el.type?.name === 'WorkspacePicker');
  assert.equal(picker.props.initialTenant, 'home.example');
  assert.equal(picker.props.initialAuthMode, 'interactive');
  m.h.unmount();
});

test('FX174: Cancel and closing Connections release a pending SQL test without a reload or late result', async () => {
  for (const close of [false, true]) {
    const world = makeWorld();
    const original = world.rpc;
    const source = { id: 'sql1', name: 'Contoso warehouse', server: 'contoso.database.windows.net', database: 'Warehouse', authMode: 'interactive', tenantId: GUEST };
    let finish;
    world.rpc = (method, ...args) => {
      if (method === 'listSqlSources') return Promise.resolve([source]);
      if (method === 'listModelSqlEndpoints') return Promise.resolve({ rows: [{ connectionId: 'x1', modelName: 'Contoso Sales', sqlEndpoint: { sourceId: source.id, ...source } }] });
      if (method === 'saveSqlSource') return Promise.resolve(source);
      if (method === 'testSqlSource') { world.calls.push([method, ...args]); return new Promise((resolve) => { finish = resolve; }); }
      return original(method, ...args);
    };
    const page = open(world);
    const m = await mount(page);
    await m.menu('SQL endpoint');
    // Use the existing saved source so the test starts without editing a record.
    const select = byTest(m.row(), 'hub-sql-saved');
    if (select) { select.props.onChange({ target: { value: source.id } }); await settleAll(); }
    byTest(m.row(), 'hub-sql-test').props.onClick(); await settleAll();
    assert.equal(typeof finish, 'function', 'the SQL test is waiting');
    if (close) {
      m.h.unmount();
    } else {
      find(m.row(), (el) => el.type === 'button' && text(el) === 'Cancel').props.onClick(); await settleAll();
      await m.menu('SQL endpoint');
      assert.equal(byTest(m.row(), 'hub-sql-save').props.disabled, false);
      assert.equal(byTest(m.row(), 'hub-sql-unlink').props.disabled, false);
    }
    assert.ok(world.calls.some((c) => c[0] === 'cancelSqlSourceTest' && c[1] === source.id), 'the engine test is cancelled too');
    finish({ ok: true, note: 'late result' }); await settleAll();
    if (!close) {
      assert.doesNotMatch(deep(m.h.result), /late result/);
      const beforeSave = world.calls.length;
      byTest(m.row(), 'hub-sql-save').props.onClick(); await settleAll();
      assert.ok(world.calls.slice(beforeSave).some((c) => c[0] === 'setModelSqlEndpoint' && c[1] === source.id), 'Save links the source after Cancel');
      await m.menu('SQL endpoint');
      byTest(m.row(), 'hub-sql-unlink').props.onClick(); await settleAll();
      assert.ok(world.calls.some((c) => c[0] === 'setModelSqlEndpoint' && c[1] === null), 'Remove endpoint works after Cancel');
      m.h.unmount();
    }
  }
});

async function mount(page) {
  const h = page.pg.mount(() => page.ConnectionsHub({ open: true, onClose: () => undefined, standalone: true }));
  await settleAll();
  const row = () => findAll(h.result, (x) => typeof x.props?.className === 'string' && /^hub-row\b/.test(x.props.className))
    .find((x) => find(x, (y) => typeof y.props?.className === 'string' && /hub-row-name/.test(y.props.className) && text(y) === 'Contoso Sales')) ?? null;
  const menu = async (label) => {
    byTest(row(), 'hub-row-overflow').props.onClick({ currentTarget: { focus: () => undefined } }); await settleAll();
    find(row(), (el) => el.type?.name === 'MenuItem' && deep(el).includes(label)).props.onClick(); await settleAll();
  };
  return { h, row, menu };
}

test('blocker 2: Cancel on "Sign in another account" while the browser sign-in waits gives every Open button back', async () => {
  const world = makeWorld();
  const page = open(world);
  const m = await mount(page);
  // cx2: the row is picked, and the one Open is in the bar at the bottom of the dialog.
  byTest(m.row(), 'hub-model-row').props.onClick(); await settleAll();
  await m.menu('Open as another account');
  const dialog = () => byTest(m.h.result, 'hub-account-dialog');
  const barOpen = () => byTest(m.h.result, 'hub-open');
  assert.ok(dialog(), 'the account choice is open');
  byTest(dialog(), 'hub-sign-in-another').props.onClick(); await settleAll();
  assert.equal(world.calls.filter((c) => c[0] === 'openLive').length, 1, 'the sign-in started');
  assert.equal(barOpen(), null, 'while it waits, the bar shows the open and its Cancel, not a second Open');
  assert.ok(byTest(m.h.result, 'hub-bar-cancel'), 'the bar offers Cancel while it waits');
  find(dialog(), (el) => el.type === 'button' && text(el) === 'Cancel').props.onClick(); await settleAll();
  assert.equal(dialog(), null, 'the choice closes');
  assert.ok(world.calls.some((c) => c[0] === 'cancelOpen'), 'the engine is asked to stop the open too (cx2)');
  assert.equal(barOpen().props.disabled, false, 'Open works again after Cancel, with no reload');
  // The sign-in that was left answers late: the page stays usable and says nothing about it.
  world.pending.forEach((r) => r({})); await settleAll();
  assert.equal(barOpen().props.disabled, false);
  m.h.unmount();
});

test('gap 5: with no saved account for the model, the account choice names the tenant and says it signs in', async () => {
  const page = open(makeWorld());
  const m = await mount(page);
  await m.menu('Open as another account');
  const dialog = byTest(m.h.result, 'hub-account-dialog');
  const primary = byTest(dialog, 'hub-open-default');
  assert.doesNotMatch(deep(primary), /default account/, 'no unnamed "default account"');
  assert.match(deep(primary), /Sign in and open/);
  assert.match(deep(dialog), new RegExp(GUEST), 'the tenant the sign-in goes to is on the card');
  m.h.unmount();
});

test('gap 5: with a saved account in the model\'s tenant, the account choice names it', async () => {
  const page = open(makeWorld({ profiles: [{ id: 'p1', username: 'alex@contoso-a.example', tenantId: null, accountTenantId: GUEST, family: 'interactive', signedIn: true, isDefault: false }] }));
  const m = await mount(page);
  await m.menu('Open as another account');
  const dialog = byTest(m.h.result, 'hub-account-dialog');
  assert.ok(findAll(dialog, (el) => el.props?.['data-testid'] === 'hub-account-row').length === 1, 'the saved account in that tenant is listed');
  assert.match(deep(byTest(dialog, 'hub-open-default')), /alex@contoso-a\.example/);
  m.h.unmount();
});

test('Kane A2: the SQL endpoint form of a model signs in where the model lives, even when the model kept no tenant', async () => {
  const page = open(makeWorld({ record: { tenantId: null }, profiles: [{ id: 'p1', username: 'alex@contoso-a.example', tenantId: null, accountTenantId: GUEST, family: 'interactive', signedIn: true, isDefault: false }] }));
  const m = await mount(page);
  await m.menu('Add SQL endpoint');
  const picker = find(m.row(), (el) => el.type?.name === 'AccountPicker');
  assert.ok(picker, 'the form is open');
  assert.equal(picker.props.tenantId, GUEST, 'the tenant of the account that opened the model');
  m.h.unmount();
});

test('blocker 4: the account menu\'s Sign in starts a browser sign-in, and the address form is called by its name', async () => {
  const session = { sessionId: 's1' };
  const context = { editing: { available: true, live: true, kind: 'xmla', connectionId: 'x1' } };
  const page = open(makeWorld({ record: { authMode: 'serviceprincipal' } }), { session, context });
  const m = await mount(page);
  byTest(m.h.result, 'hub-account-button')?.props.onClick?.(); await settleAll();
  const menu = byTest(m.h.result, 'hub-account-menu');
  assert.ok(menu, 'the account menu is open');
  assert.doesNotMatch(deep(menu), /Other ways to sign in/, 'no "sign in" words on the button that opens the address form');
  assert.match(deep(byTest(menu, 'hub-other-sign-in')), /Add a model by its address/);
  byTest(menu, 'hub-sign-in-first').props.onClick(); await settleAll();
  const picker = find(m.h.result, (el) => el.type?.name === 'WorkspacePicker');
  assert.equal(picker?.props.initialAuthMode, 'interactive', 'Sign in reads the list with a browser sign-in, not the service identity');
  m.h.unmount();
});

test('Kane A1: the account line says who opens it and who opened it last, never "(was ...)"', () => {
  const hub = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');
  const ext = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
  assert.doesNotMatch(hub, /\(was \$\{was\}\)/);
  assert.doesNotMatch(ext, /\(was \$\{was\}\)/);
  assert.match(hub, /opens as \$\{account\}, last opened as \$\{was\}/);
});

test('blocker 1: the workspace picker takes a tenant for a browser sign-in, and the list and Connect use it', () => {
  const load = makeLoader({ './bridge': { rpc: async () => [], rpcPressed: async () => [], runHostCommand: () => undefined } });
  const { WorkspacePickerView } = load('connectpickers.tsx');
  const props = { workspaces: { kind: 'ready', items: [] }, wsId: null, models: null, modelId: null, env: '', authMode: 'interactive', tenant: '', otherOpen: true, busy: false, connectError: null,
    onBack() {}, onRetry() {}, onPickWorkspace() {}, onPickModel() {}, onEnv() {}, onAuthMode() {}, onTenant() {}, onToggleOther() {}, onPasteAddress() {}, onConnect() {} };
  const view = WorkspacePickerView(props);
  assert.ok(byTest(view, 'pick-tenant'), 'a browser sign-in offers the tenant box');
  assert.ok(byTest(view, 'pick-ws-relist'), 'and a way to list that tenant\'s workspaces');
  const src = readFileSync(resolve(root, 'webview', 'src', 'connectpickers.tsx'), 'utf8');
  assert.doesNotMatch(src, /const tenantArg = \(\) => \(isAdvancedSignIn\(authMode\)/, 'the tenant is not dropped for a browser sign-in');
});

test('blocker 3: the footer and Jobs say the same thing about where Publish goes', () => {
  const bar = readFileSync(resolve(root, 'webview', 'src', 'contextbar.tsx'), 'utf8');
  const hub = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');
  assert.match(bar, /publishWords\(publishing\)/, 'the footer reads the shared words');
  assert.match(hub, /publishWords\(publishing\)/, 'Jobs reads the same shared words');
  const { publishWords } = makeLoader({})('connectpickers-model.ts');
  const implied = publishWords({ available: true, implied: true, modelName: 'Contoso Sales' });
  assert.equal(implied.main, 'Contoso Sales');
  assert.match(implied.detail, /Contoso Sales/, 'and says where a publish would go');
  assert.equal(publishWords({ available: true, implied: false, modelName: 'Contoso Sales' }).main, 'Contoso Sales');
  assert.equal(publishWords(null).main, 'Not linked');
});

test('gap 10: a filled tenant box keeps its label', () => {
  const load = makeLoader({});
  const { AccountPicker } = load('accountpicker.tsx');
  const view = AccountPicker({ mode: 'interactive', onMode() {}, tenantId: GUEST, onTenantId() {}, tenantLabel: 'where the database lives (optional)' });
  assert.match(deep(view), /Tenant, where the database lives/);
});

test('gap 6: the account menu says which account and tenant the model in play uses', async () => {
  const session = { sessionId: 's1', currentAccount: 'alex@contoso-a.example', currentTenant: GUEST };
  const context = { editing: { available: true, live: true, kind: 'xmla', connectionId: 'x1' } };
  const page = open(makeWorld(), { session, context });
  const m = await mount(page);
  byTest(m.h.result, 'hub-account-button').props.onClick(); await settleAll();
  const line = byTest(byTest(m.h.result, 'hub-account-menu'), 'hub-in-use');
  assert.ok(line, 'one line names the account in use');
  assert.match(deep(line), /alex@contoso-a\.example/);
  assert.match(deep(line), new RegExp('tenant ' + GUEST));
  assert.match(deep(line), /in use for Contoso Sales/);
  m.h.unmount();
});
