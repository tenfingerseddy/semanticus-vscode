// f172, the four 1.7.2 small fixes (Kane, 31 Oct: "Build as drawn", the mockup semanticus-1.7.2-small-fixes-mockup.html).
// 1. Cancel on a browser sign-in for another account ends the wait: the screen is back at its start, the saved account and its workspaces.
// 2. The Studio header names the account and the tenant in use on the identity's second line, and on a phone in the action row.
// 3. A long History row shows its first line, and the whole text can be read: the row opens, nothing is cut.
// 4. A SQL endpoint form opened before the saved sources were read opens on the saved endpoint, its fields filled.
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
const src = (name) => readFileSync(resolve(root, 'webview', 'src', name), 'utf8');
const load = makeLoader({});
const deep = (el) => [].concat(el?.props?.children ?? []).flat(Infinity).map((c) => typeof c === 'string' || typeof c === 'number' ? String(c) : c && typeof c === 'object' ? deep(c) : '').join('');
const byTest = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

// ---- 1. Cancel on a browser sign-in -------------------------------------------------------------------------------
test('fault 1: Cancel on "Use another account" goes back to the saved account and its workspaces', async () => {
  const saved = [{ id: 'ws1', displayName: 'Contoso Finance' }, { id: 'ws2', displayName: 'Contoso Sales' }];
  const calls = [];
  const answer = (kind) => (method, ...args) => {
    calls.push([kind, method, ...args]);
    if (method !== 'listWorkspaces') return Promise.resolve([]);
    return args[3] === true ? new Promise(() => undefined) : Promise.resolve(structuredClone(saved));   // another account: a browser nobody finishes
  };
  const pg = makePage({ './bridge': { rpc: answer('automatic'), rpcPressed: answer('pressed'), onEngineState: () => () => undefined, runHostCommand: () => undefined } });
  const { WorkspacePicker } = pg.load('connectpickers.tsx');
  const h = pg.mount(() => WorkspacePicker({ account: 'alex@contoso.com', initialTenant: 'contoso.com', pressedFirst: true,
    onBack() {}, onPasteAddress() {}, onConnect: async () => null }));
  await settleAll();
  const view = () => h.result.props;
  assert.equal(view().workspaces.kind, 'ready', 'the saved account lists its workspaces');
  assert.equal(view().account, 'alex@contoso.com');

  view().onAnotherAccount(); await settleAll();
  assert.equal(view().askingAccount, true, 'the new sign-in asks for the account');
  assert.equal(view().workspaces.kind, 'loading');

  view().onCancelSignIn(); await settleAll();
  assert.equal(view().askingAccount, false, 'no sign-in runs, so the header does not ask to pick an account in the browser');
  assert.equal(view().account, 'alex@contoso.com', 'the header names the saved account again');
  assert.equal(view().signIn ?? null, null, 'no "Sign-in did not finish" row: the person ended it');
  assert.equal(view().workspaces.kind, 'ready', 'the saved account\'s workspaces are back');
  assert.deepEqual(view().workspaces.items.map((w) => w.id).sort(), ['ws1', 'ws2']);
  // A later list reads as the saved account again, with no new sign-in.
  view().onRetry(); await settleAll();
  assert.deepEqual(calls.filter((c) => c[1] === 'listWorkspaces').at(-1).slice(5), [false, null]);
  h.unmount();
});

test('fault 1: Cancel of a saved-account sign-in still says the sign-in did not finish', async () => {
  const pg = makePage({ './bridge': { rpc: () => new Promise(() => undefined), rpcPressed: () => new Promise(() => undefined), onEngineState: () => () => undefined, runHostCommand: () => undefined } });
  const { WorkspacePicker } = pg.load('connectpickers.tsx');
  const h = pg.mount(() => WorkspacePicker({ account: 'alex@contoso.com', initialTenant: 'contoso.com', pressedFirst: true,
    onBack() {}, onPasteAddress() {}, onConnect: async () => null }));
  await settleAll();
  h.result.props.onCancelSignIn(); await settleAll();
  assert.equal(h.result.props.signIn, 'unfinished');
  assert.equal(h.result.props.account, 'alex@contoso.com');
  h.unmount();
});

// The three paths the rc1 tester found (verdict 2026-10-31-rc721-try-1): the start screen was saved only when its list was ready.
const mountPicker = (bridge, props = {}) => {
  const pg = makePage({ './bridge': { onEngineState: () => () => undefined, runHostCommand: () => undefined, ...bridge } });
  const { WorkspacePicker } = pg.load('connectpickers.tsx');
  return pg.mount(() => WorkspacePicker({ account: 'alex@contoso.com', initialTenant: 'contoso.com', pressedFirst: true,
    onBack() {}, onPasteAddress() {}, onConnect: async () => null, ...props }));
};
// What a person reads in the header: the view's own words, drawn.
const headerLine = (props) => { const { WorkspacePickerView } = makeLoader({ './bridge': {} })('connectpickers'); return deep(byTest(WorkspacePickerView(props), 'pick-as')); };

test('fault 1, path A: a saved list that failed comes back after Cancel on "Use another account"', async () => {
  const NO_ACCESS = 'alex@contoso.com has no access to workspaces in contoso.com.';
  const answer = (_m, ...args) => args[3] === true ? new Promise(() => undefined) : Promise.reject(new Error(NO_ACCESS));
  const h = mountPicker({ rpc: answer, rpcPressed: answer });
  await settleAll();
  const view = () => h.result.props;
  assert.equal(view().workspaces.kind, 'failed');
  view().onAnotherAccount(); await settleAll();
  assert.equal(view().askingAccount, true, 'a sign-in runs: the header asks for the account');
  view().onCancelSignIn(); await settleAll();
  assert.equal(view().askingAccount, false, 'no sign-in runs after Cancel');
  assert.doesNotMatch(headerLine(view()), /browser window/, 'the header does not ask to pick in the browser');
  assert.equal(view().account, 'alex@contoso.com', 'the saved account is named again');
  assert.equal(view().waitingToList, false);
  assert.deepEqual(view().workspaces, { kind: 'failed', message: NO_ACCESS }, 'the saved account\'s reason is back');
  h.unmount();
});

test('fault 1, path B: a saved sign-in that did not finish comes back after Cancel on "Use another account"', async () => {
  const h = mountPicker({ rpc: () => new Promise(() => undefined), rpcPressed: () => new Promise(() => undefined) });
  await settleAll();
  const view = () => h.result.props;
  view().onCancelSignIn(); await settleAll();
  assert.equal(view().signIn, 'unfinished', 'the saved account\'s own sign-in did not finish');
  view().onAnotherAccount(); await settleAll();
  assert.equal(view().askingAccount, true);
  assert.equal(view().signIn ?? null, null);
  view().onCancelSignIn(); await settleAll();
  assert.equal(view().askingAccount, false, 'no sign-in runs after Cancel');
  assert.doesNotMatch(headerLine(view()), /browser window/);
  assert.equal(view().account, 'alex@contoso.com');
  assert.equal(view().waitingToList, false);
  assert.equal(view().signIn, 'unfinished', 'the start screen is back: the saved sign-in did not finish, Try again');
  h.unmount();
});

test('fault 1, path C: the account menu\'s "Sign in another account" asks for the browser only while a sign-in runs', async () => {
  // With a tenant: the sign-in starts at once, and Cancel ends it.
  const h = mountPicker({ rpc: () => new Promise(() => undefined), rpcPressed: () => new Promise(() => undefined) }, { account: null, anotherAccount: true });
  await settleAll();
  const view = () => h.result.props;
  assert.equal(view().askingAccount, true, 'the sign-in started: the header asks for the account');
  view().onCancelSignIn(); await settleAll();
  assert.equal(view().askingAccount, false, 'no sign-in runs after Cancel');
  assert.doesNotMatch(headerLine(view()), /browser window/);
  assert.equal(view().waitingToList, true, 'back at its start, which waits for List workspaces');
  view().onRetry(); await settleAll();
  assert.equal(view().askingAccount, true, 'List workspaces starts the sign-in again');
  h.unmount();
  // With no tenant: nothing starts until List workspaces, so nothing asks for the browser yet.
  const h2 = mountPicker({ rpc: () => new Promise(() => undefined), rpcPressed: () => new Promise(() => undefined) }, { account: null, anotherAccount: true, initialTenant: '' });
  await settleAll();
  assert.equal(h2.result.props.waitingToList, true);
  assert.equal(h2.result.props.askingAccount, false, 'no sign-in has started');
  assert.doesNotMatch(headerLine(h2.result.props), /browser window/);
  h2.unmount();
});

// ---- 2. The Studio header names the account -----------------------------------------------------------------------
test('fault 2: the header line names the account and the tenant, short on a phone, long on hover', () => {
  const { studioIdentityLine } = load('studio-identity');
  assert.deepEqual(studioIdentityLine('megan@contoso.com', 'contoso.com'),
    { line: 'megan@contoso.com, contoso.com', short: 'megan, contoso.com', title: 'Signed in as megan@contoso.com, tenant contoso.com' });
  assert.deepEqual(studioIdentityLine('megan@contoso.com', undefined),
    { line: 'megan@contoso.com', short: 'megan', title: 'Signed in as megan@contoso.com' });
  assert.equal(studioIdentityLine(undefined, 'contoso.com'), null, 'no account in use: no line');
  assert.equal(studioIdentityLine('', ''), null);
});

test('fault 2: the header draws the line in the identity slot and a phone line in the action row, at the same height', () => {
  const app = src('App.tsx');
  const css = src('styles.css');
  assert.match(app, /import \{ studioIdentityLine \} from '\.\/studio-identity'/);
  assert.match(app, /className="studio-identity min-w-0">\s*<div[^\n]*modelName[^\n]*<\/div>\s*\{identityLine && <div className="truncate" data-testid="studio-account-line" title=\{identityLine\.title\}>\{identityLine\.line\}<\/div>\}/,
    'the line sits in the existing second-line slot of .studio-identity');
  assert.match(app, /<span className="studio-account-meta[^"]*" data-testid="studio-account-meta" title=\{identityLine\.title\}>\{identityLine\.short\}<\/span>/, 'the phone line in the action row');
  assert.match(app, /const headerSignature = \[[^\]]*identityLine\?\.line/, 'the fold is measured again when the line changes');
  assert.match(css, /\.studio-account-meta \{ display: none;/, 'the phone line is hidden until the header folds fully');
  assert.match(css, /\.studio-chrome\[data-fold="4"\] \.studio-account-meta \{ display: block;/);
  assert.match(src('tokens.css'), /--sem-bar-h: 40px/, 'the header height is unchanged');
});

// ---- 3. A long History row ----------------------------------------------------------------------------------------
const FAILED = { kind: 'open', ok: false, account: 'megan@contoso.com', tenantId: 'contoso.com', modelName: 'Contoso Sales', whenUtc: '2026-10-31T05:37:00Z',
  detail: 'signing in or opening the model failed: the account megan@contoso.com can sign in to contoso.com but has no access to the workspace Contoso UAT. Ask a workspace admin to add this account as a Member or Contributor, or open the model as another account. The saved account was left unchanged.' };

test('fault 3: a History row splits into who, where and when, then the reason', () => {
  const { historyParts, historySubLine } = load('historyrow');
  const p = historyParts(FAILED);
  assert.match(p.who, /^Contoso Sales · megan@contoso\.com · tenant contoso\.com · /);
  assert.doesNotMatch(p.who, /signing in/);
  assert.match(p.reason, /^Signing in or opening the model failed: /, 'the reason starts as a sentence');
  assert.match(p.reason, /The saved account was left unchanged\.$/, 'the whole reason, nothing cut');
  assert.equal(historySubLine(FAILED).includes('signing in or opening the model failed'), true, 'the closed row keeps its one line');
  assert.equal(historyParts({ kind: 'open', ok: true, modelName: 'Contoso Sales' }).reason, null);
});

test('fault 3: a row that does not fit opens to its whole text, and a row that fits does not change', () => {
  const { HistoryRowView } = load('connectionshub-parts');
  let toggled = 0;
  const closed = HistoryRowView({ event: FAILED, open: false, cut: true, onToggle: () => { toggled++; } });
  const btn = find(closed, (el) => el.type === 'button');
  assert.ok(btn, 'a cut row is a button');
  assert.equal(btn.props['aria-expanded'], false);
  assert.match(deep(closed), /▸/, 'a cut row shows that it opens');
  assert.ok(find(closed, (el) => typeof el.props?.className === 'string' && /\btruncate\b/.test(el.props.className) && el.props['data-history-line'] === 'sub'), 'closed, the second line is one line');
  btn.props.onClick(); assert.equal(toggled, 1);

  const opened = HistoryRowView({ event: FAILED, open: true, cut: true, onToggle: () => undefined });
  const obtn = find(opened, (el) => el.type === 'button');
  assert.equal(obtn.props['aria-expanded'], true);
  assert.match(deep(opened), /▾/);
  const reason = find(opened, (el) => el.props?.['data-history-line'] === 'reason');
  assert.ok(reason, 'the reason stands on its own line');
  assert.doesNotMatch(String(reason.props.className), /\btruncate\b/, 'the reason wraps, nothing is cut');
  assert.match(deep(reason), /The saved account was left unchanged\.$/);
  const who = find(opened, (el) => el.props?.['data-history-line'] === 'who');
  assert.doesNotMatch(String(who.props.className), /\btruncate\b/);

  const fits = HistoryRowView({ event: { kind: 'open', ok: true, modelName: 'Contoso Sales' }, open: false, cut: false, onToggle: () => undefined });
  assert.equal(find(fits, (el) => el.type === 'button'), null, 'a row that fits is not a button');
  assert.doesNotMatch(deep(fits), /[▸▾]/, 'and has no marker');
});

test('fault 3: the History page draws each row with the shared row view', () => {
  const hub = src('connectionshub.tsx');
  assert.match(hub, /history\.map\(\(e, i\) => <HistoryRow key=\{i\} event=\{e\} \/>\)/);
  assert.doesNotMatch(hub, /title=\{e\.detail \|\| e\.endpoint\}>\{historySubLine\(e\)\}/, 'no hover-only reason');
});

// ---- 4. A SQL endpoint form opens on the saved endpoint -----------------------------------------------------------
function open(world) {
  const stubs = {
    './bridge': {
      rpc: world.rpc, openLocalModel: async () => undefined, pickWorkingCopyFolder: async () => null, onConnectionChange: () => () => undefined,
      onOpenConnections: () => () => undefined, onProgress: () => () => undefined, onEngineReady: () => () => undefined, announceConnectionChange: () => undefined, closeConnectionsPanel: () => undefined,
      navigateInStudio: () => undefined, runHostCommand: () => undefined, loadState: makeDisk().loadState, saveState: makeDisk().saveState,
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

test('fault 4: a SQL endpoint form opened before the saved sources were read opens on the saved endpoint', async () => {
  const source = { id: 'sql1', name: 'Fabrikam ledger', server: 'fabrikam-sql.database.windows.net', database: 'Ledger', authMode: 'devicecode', tenantId: 'fabrikam.com',
    lastTestOk: true, lastTestedUtc: '2026-10-31T05:00:00Z' };
  let sourceReads = 0;
  const calls = [];
  const rpc = (method, ...args) => {
    calls.push([method, ...args]);
    switch (method) {
      case 'listConnections': return Promise.resolve([{ id: 'x1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso', database: 'Contoso Sales', modelName: 'Contoso Sales', authMode: 'interactive', tenantId: 'contoso.com' }]);
      case 'listAccountProfiles': return Promise.resolve([]);
      case 'probeConnectionAccounts': return Promise.resolve([]);
      case 'listSqlSources': return ++sourceReads === 1 ? Promise.reject(new Error('The engine is busy.')) : Promise.resolve([structuredClone(source)]);
      case 'listModelSqlEndpoints': return Promise.resolve({ rows: [{ connectionId: 'x1', modelName: 'Contoso Sales', sqlEndpoint: { sourceId: 'sql1', name: source.name, server: source.server, database: source.database } }] });
      case 'sessionInfo': return Promise.resolve({ sessionId: 's1', hasUnsavedChanges: false });
      default: return Promise.resolve([]);
    }
  };
  const page = open({ rpc });
  const h = page.pg.mount(() => page.ConnectionsHub({ open: true, onClose: () => undefined, standalone: true }));
  await settleAll();
  assert.equal(sourceReads, 1, 'the first read of the saved sources failed');
  const row = () => findAll(h.result, (x) => typeof x.props?.className === 'string' && /^hub-row\b/.test(x.props.className))
    .find((x) => find(x, (y) => typeof y.props?.className === 'string' && /hub-row-name/.test(y.props.className) && text(y) === 'Contoso Sales')) ?? null;
  byTest(row(), 'hub-row-overflow').props.onClick({ currentTarget: { focus: () => undefined } }); await settleAll();
  const item = find(row(), (el) => el.type?.name === 'MenuItem' && deep(el).includes('SQL endpoint: Fabrikam ledger'));
  assert.ok(item, 'the row names its saved endpoint');
  item.props.onClick(); await settleAll();
  assert.ok(byTest(row(), 'hub-sql-form'), 'the form is open');
  assert.equal(byTest(row(), 'hub-sql-name').props.value, 'Fabrikam ledger', 'the name of the saved endpoint, not a blank new one');
  assert.equal(byTest(row(), 'hub-sql-server').props.value, 'fabrikam-sql.database.windows.net');
  assert.equal(byTest(row(), 'hub-sql-database').props.value, 'Ledger');
  assert.ok(sourceReads >= 2, 'the saved sources are read again');
  assert.equal(byTest(row(), 'hub-sql-saved')?.props.value, 'sql1', 'the list shows the saved endpoint, not "A new one"');
  assert.match(deep(row()), /Last test worked/, 'the last test line of the saved endpoint');
  const picker = find(row(), (el) => el.type?.name === 'AccountPicker');
  assert.equal(picker.props.tenantId, 'fabrikam.com', 'the saved endpoint\'s tenant, not the model\'s');
  assert.equal(picker.props.mode, 'devicecode', 'the saved endpoint\'s sign-in');
  assert.equal(find(row(), (el) => el.type?.name === 'EditMovedBanner'), null, 'the read that arrived is not taken as a change made elsewhere');
  h.unmount();
});
