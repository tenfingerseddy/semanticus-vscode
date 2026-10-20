// Connections, slice C, lane C3: the Power BI Desktop instance picker and the workspace-then-model picker.
//
// The design (connections-minimal.html, Kane 24 Sep: "connections design approved") replaces two things the hub
// did badly: "Running desktop model" quietly took the FIRST running copy, and "Published model" made a person
// paste an XMLA address. The pickers list instead. This file proves the parts that are logic and words:
//   1. The pure rules in connectpickers-model.ts, executed: the names, the endpoint a pick becomes, the order.
//   2. The views rendered for real with react-dom/server, one per state, read as a person reads them. The three
//      states the lane names are the ones asserted hardest: no Desktop running shows ONE plain line and the
//      typed-address route; a workspace with no models says so; a sign-in failure shows the engine's own
//      sentence, not a rewrite of it.
//   3. The wiring, read off the source: which engine calls the pickers make, and nothing else.
// The clicks are driven in a real browser by tools/uishot/c3-pickers-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const STUBS = {
  './bridge': { rpc: async () => { throw new Error('the test loader answers no calls; drive the app for that'); } },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (STUBS[name] ? STUBS[name] : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|disabled|aria-[\w-]+|title|placeholder|autoComplete)="[^"]*"/gi, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ').trim();
const testids = (html) => [...html.matchAll(/data-testid="([^"]+)"/g)].map((m) => m[1]);

const M = load(join(webview, 'src', 'connectpickers-model.ts'));
const V = load(join(webview, 'src', 'connectpickers.tsx'));
const h = React.createElement;
const noop = () => {};
const draw = (C, props) => renderToStaticMarkup(h(C, props));

// ===================================================================================================
// 1. The pure rules
// ===================================================================================================
{
  // A running copy is named by its report when the engine knows it, never by Desktop's workspace folder id.
  assert.equal(M.instanceName({ port: 51000, title: 'Contoso Sales', dataSource: 'localhost:51000' }), 'Contoso Sales');
  assert.equal(M.instanceName({ port: 51000, title: 'AnalysisServicesWorkspace_2b0c1f9e', dataSource: 'localhost:51000' }),
    'Power BI Desktop report', 'a Desktop workspace folder id is not a name a person knows');
  assert.equal(M.instanceName({ port: 51000, title: '', dataSource: 'localhost:51000' }), 'Power BI Desktop report');
  assert.equal(M.instanceDetail({ port: 52214, title: 'x' }), 'localhost:52214', 'the address falls back to the port');

  // The workspace a pick becomes: the same /myorg/<name> shape the engine's WorkspaceNameFromEndpoint reads back.
  assert.equal(M.workspaceEndpoint({ id: 'a', displayName: 'Contoso Sales' }), 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales');
  assert.equal(M.workspaceEndpoint({ id: 'p', displayName: 'My workspace', type: 'Personal' }), 'powerbi://api.powerbi.com/v1.0/myorg/me',
    'a personal workspace is reached as "me", not by its display name');
  assert.equal(M.workspaceEndpoint({ id: 'b', displayName: 'Ops/Finance?#1' }), 'powerbi://api.powerbi.com/v1.0/myorg/Ops%2FFinance%3F%231',
    'a name that would break the address is escaped, and only those characters');
  assert.equal(M.workspaceName({ id: 'p', displayName: 'avery', type: 'Personal' }), 'My workspace');

  // Order: My workspace first, then by name, ignoring case. The lists are the engine's; only the order is ours.
  const sorted = M.sortWorkspaces([{ id: '1', displayName: 'zeta' }, { id: '2', displayName: 'Alpha' }, { id: '3', displayName: 'me', type: 'Personal' }]);
  assert.deepEqual(sorted.map((w) => w.id), ['3', '2', '1']);
  assert.deepEqual(M.sortModels([{ id: 'b', displayName: 'budget' }, { id: 'a', displayName: 'Actuals' }]).map((m) => m.id), ['a', 'b']);

  // The engine's sentence is shown as the engine wrote it. Only the transport's "Error: " prefix goes.
  assert.equal(M.engineSentence(new Error('Your sign-in as avery@contoso.com has run out.')), 'Your sign-in as avery@contoso.com has run out.');
  assert.equal(M.engineSentence('Error: Fabric said 403.'), 'Fabric said 403.');
  assert.ok(M.engineSentence(new Error('')).length > 0, 'an empty failure still says something');

  // The environment choices are the hub's own four, and "Local" is not offered for a workspace model.
  assert.deepEqual(M.WORKSPACE_ENV_OPTIONS.map((o) => o.value), ['', 'dev', 'uat', 'prod']);

  // The choice a Connect hands the host carries everything the existing connect needs, and nothing typed.
  const choice = M.workspaceChoice({ id: 'w', displayName: 'Contoso Finance' }, { id: 'm', displayName: 'Contoso Budget' }, 'prod', 'interactive', '');
  assert.deepEqual(choice, { endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Finance', database: 'Contoso Budget',
    workspaceName: 'Contoso Finance', modelName: 'Contoso Budget', authMode: 'interactive', tenantId: null, label: 'prod' });
  assert.equal(M.workspaceChoice({ id: 'w', displayName: 'W' }, { id: 'm', displayName: 'M' }, '', 'azcli', ' contoso.com ').tenantId, 'contoso.com',
    'a tenant is carried only for the advanced sign-ins, trimmed');
  assert.equal(M.workspaceChoice({ id: 'w', displayName: 'W' }, { id: 'm', displayName: 'M' }, '', 'interactive', 'stale.com').tenantId, null,
    'a browser sign-in never carries a tenant left over from an advanced attempt');
}

// ===================================================================================================
// 2. The Desktop picker, state by state
// ===================================================================================================
const desk = (state, extra = {}) => draw(V.DesktopPickerView, {
  state, address: '', addressOpen: false, busyKey: null, rowError: null,
  onBack: noop, onRetry: noop, onUseForTests: noop, onOpen: noop, onToggleAddress: noop, onAddress: noop, ...extra,
});
{
  // No Desktop running: ONE plain line, a way to look again, and the typed-address route. No empty list.
  const html = desk({ kind: 'ready', items: [] });
  const text = readable(html);
  assert.match(text, /No report is open in Power BI Desktop\. Open one, then look again\./);
  assert.equal((html.match(/data-testid="pick-desktop-none"/g) || []).length, 1, 'exactly one plain line');
  assert.ok(testids(html).includes('pick-desktop-address-toggle'), 'the typed-address route is offered');
  assert.ok(testids(html).includes('pick-desktop-retry'), 'looking again is one click');
  assert.ok(!testids(html).includes('pick-desktop-row'), 'no rows are drawn for nothing');

  // The route open: one address box and the same two actions a listed copy has.
  const typed = desk({ kind: 'ready', items: [] }, { addressOpen: true, address: 'localhost:51000' });
  assert.ok(testids(typed).includes('pick-desktop-address'), 'the address box is there once the route is chosen');
  assert.match(readable(typed), /Use for tests/);
  assert.match(readable(typed), /Open/);

  // Two copies running: both listed, each with Use for tests and Open. Nothing is chosen for the person.
  const two = desk({ kind: 'ready', items: [
    { port: 51000, title: 'Contoso Sales', dataSource: 'localhost:51000' },
    { port: 52214, title: 'AnalysisServicesWorkspace_77aa', dataSource: 'localhost:52214' }] });
  assert.equal(testids(two).filter((t) => t === 'pick-desktop-row').length, 2);
  assert.equal(testids(two).filter((t) => t === 'pick-desktop-tests').length, 2);
  assert.equal(testids(two).filter((t) => t === 'pick-desktop-open').length, 2);
  assert.match(readable(two), /Contoso Sales localhost:51000/);
  assert.match(readable(two), /Power BI Desktop report localhost:52214/);
  assert.doesNotMatch(readable(two), /AnalysisServicesWorkspace/, 'the folder id never reaches the screen');

  // A failed open is said on the row that failed, in the engine's words.
  const failed = desk({ kind: 'ready', items: [{ port: 51000, title: 'Contoso Sales', dataSource: 'localhost:51000' }] },
    { rowError: { key: 'localhost:51000', message: 'Power BI Desktop is not running Contoso Sales. Open it, then try again.' } });
  assert.match(readable(failed), /Power BI Desktop is not running Contoso Sales\. Open it, then try again\./);

  // The list could not be read: the engine's sentence, never "none running".
  const unread = desk({ kind: 'failed', message: 'Could not read the running copies: access denied.' });
  assert.match(readable(unread), /Could not read the running copies: access denied\./);
  assert.doesNotMatch(readable(unread), /No report is open/, 'a failed read is never drawn as "none running"');

  const loading = desk({ kind: 'loading' });
  assert.match(readable(loading), /Looking for Power BI Desktop/);
}

// ===================================================================================================
// 3. The workspace picker, state by state
// ===================================================================================================
const WS = [{ id: 'ws-s', displayName: 'Contoso Sales' }, { id: 'ws-f', displayName: 'Contoso Finance' }, { id: 'ws-o', displayName: 'Contoso Ops' }];
const wsView = (extra = {}) => draw(V.WorkspacePickerView, {
  account: 'avery@contoso.com', workspaces: { kind: 'ready', items: WS }, wsId: null, models: null, modelId: null,
  env: '', authMode: 'interactive', tenant: '', otherOpen: false, busy: false, connectError: null,
  onBack: noop, onRetry: noop, onPickWorkspace: noop, onPickModel: noop, onEnv: noop, onAuthMode: noop, onTenant: noop,
  onToggleOther: noop, onPasteAddress: noop, onConnect: noop, ...extra,
});
{
  const first = wsView();
  const text = readable(first);
  assert.match(text, /as avery@contoso\.com/, 'the account the lists are read as is named');
  assert.equal(testids(first).filter((t) => t === 'pick-ws').length, 3);
  assert.match(text, /Pick a workspace/, 'the model column says what to do first');
  assert.ok(/data-testid="pick-connect"[^>]*disabled/.test(first) || /disabled[^>]*data-testid="pick-connect"/.test(first),
    'Connect waits for a model');
  assert.ok(testids(first).includes('pick-other-ways'), 'Other ways to sign in is always reachable');
  assert.doesNotMatch(text, /XMLA|endpoint/i, 'no engine words on the page');

  // A workspace with no models says so, by name, instead of drawing an empty column.
  const empty = readable(wsView({ wsId: 'ws-o', models: { kind: 'ready', items: [] } }));
  assert.match(empty, /Contoso Ops has no models you can open\./);

  // Models listed once a workspace is picked; picking one enables Connect.
  const picked = wsView({ wsId: 'ws-f', models: { kind: 'ready', items: [{ id: 'm1', displayName: 'Contoso Finance' }, { id: 'm2', displayName: 'Contoso Budget' }] }, modelId: 'm2' });
  assert.equal(testids(picked).filter((t) => t === 'pick-model').length, 2);
  assert.ok(!/data-testid="pick-connect"[^>]*disabled/.test(picked) && !/disabled[^>]*data-testid="pick-connect"/.test(picked), 'Connect is live once a model is picked');
  assert.match(readable(picked), /Environment/);

  // Sign-in failure on the workspace list: the engine's own sentence, and the other sign-in ways opened for it.
  const SENTENCE = 'Your sign-in as avery@contoso.com has run out. Sign in again to list your workspaces.';
  const signin = wsView({ workspaces: { kind: 'failed', message: SENTENCE } });
  assert.match(readable(signin), new RegExp(SENTENCE.replace(/[.@]/g, '\\$&')));
  assert.ok(testids(signin).includes('pick-ws-retry'));
  assert.ok(testids(signin).includes('pick-other-ways'));
  assert.ok(!testids(signin).includes('pick-connect') && !testids(signin).includes('pick-env'),
    'with no list there is nothing to connect, so no dead Connect button and no environment question');

  // The same for the model list and for the connect itself.
  assert.match(readable(wsView({ wsId: 'ws-f', models: { kind: 'failed', message: 'Fabric refused the list: 403.' } })), /Fabric refused the list: 403\./);
  assert.match(readable(wsView({ wsId: 'ws-f', models: { kind: 'ready', items: [{ id: 'm1', displayName: 'X' }] }, modelId: 'm1', connectError: 'The model is paused.' })), /The model is paused\./);

  // No workspaces at all is a sentence too.
  assert.match(readable(wsView({ workspaces: { kind: 'ready', items: [] } })), /This account cannot see any workspaces\./);

  // Other ways to sign in: the sign-in choice, the tenant for the advanced ones, and the pasted address.
  const other = wsView({ otherOpen: true, authMode: 'serviceprincipal' });
  assert.ok(testids(other).includes('pick-authmode'));
  assert.ok(testids(other).includes('pick-tenant'), 'a service identity names its tenant');
  assert.ok(testids(other).includes('pick-paste-address'), 'the pasted address stays, behind Other ways to sign in');
  assert.ok(testids(other).includes('pick-signin-check'), 'the service identity setup check stays, behind Other ways to sign in');
  assert.ok(!testids(wsView({ otherOpen: true, authMode: 'interactive' })).includes('pick-tenant'), 'a browser sign-in picks its own tenant');
}

// ===================================================================================================
// 4. The wiring, read off the source
// ===================================================================================================
{
  const src = readFileSync(join(webview, 'src', 'connectpickers.tsx'), 'utf8');
  const calls = [...src.matchAll(/(?:\brpc(?:Pressed)?|\(pressed \? rpcPressed : rpc\))\s*(?:<[^()]*?>)?\s*\(\s*'([^']+)'/g)].map((m) => m[1]).sort();
  assert.deepEqual([...new Set(calls)], ['listLocalInstances', 'listWorkspaceModels', 'listWorkspaces', 'probeAuthPrerequisites'],
    'the pickers only READ; every open, connect and test attach goes back to the host, which keeps the unsaved-changes check');
  for (const f of ['connectpickers.tsx', 'connectpickers-model.ts']) {
    const bytes = readFileSync(join(webview, 'src', f));
    assert.ok(!bytes.includes(Buffer.from(String.fromCharCode(0x2014), 'utf8')), `${f} carries no em dash`);
    assert.ok(![...bytes].some((b) => b < 0x20 && b !== 0x0a && b !== 0x0d), `${f} carries no raw control byte`);
  }
}

// ===================================================================================================
// 5. A press is spent when the picker closes (UAT round 2: a no-folder window's Workspace model page started the browser sign-in on entry)
// ===================================================================================================
{
  // The hub keeps its view while it is closed, and the picker is drawn only while the hub is open. The press that opened the picker was
  // kept, so the next time the hub opened on that view the picker read the list as pressed and a sign-in window opened with no press.
  const H = load(join(webview, 'src', 'connectionshub-model.ts'));
  assert.equal(H.pickerStillPressed(true, true, 'workspace'), true, 'the open picker the button just drew keeps its press');
  assert.equal(H.pickerStillPressed(true, false, 'workspace'), false, 'closing the hub spends the press');
  assert.equal(H.pickerStillPressed(true, true, 'open'), false, 'leaving the picker spends the press');
  assert.equal(H.pickerStillPressed(false, true, 'workspace'), false, 'a view nobody pressed stays automatic');
  const hub = readFileSync(join(webview, 'src', 'connectionshub.tsx'), 'utf8');
  assert.match(hub, /pickerPressed\.current = pickerStillPressed\(pickerPressed\.current, shown, view\)/, 'the hub spends the press when it closes or leaves the picker');
}

// ===================================================================================================
// 6. A sign-in that worked is reused (round 2 UAT N13: Workspace model opened a browser sign-in after a service identity open)
// ===================================================================================================
{
  const recs = [
    { id: 'a', kind: 'xmla', authMode: 'interactive', lastUsedUtc: '2026-10-18T01:00:00Z' },
    { id: 'b', kind: 'xmla', authMode: 'serviceprincipal', tenantId: 'contoso.com', lastUsedUtc: '2026-10-18T02:00:00Z' },
    { id: 'c', kind: 'local', authMode: 'azcli', lastUsedUtc: '2026-10-18T03:00:00Z' },
  ];
  assert.deepEqual(M.signInToReuse(recs, 'a'), { authMode: 'interactive', tenantId: '' }, 'the model in play wins');
  assert.deepEqual(M.signInToReuse(recs, null), { authMode: 'serviceprincipal', tenantId: 'contoso.com' }, 'else the published model opened last (a local copy has no sign-in)');
  assert.deepEqual(M.signInToReuse([], null), { authMode: 'interactive', tenantId: '' }, 'nothing known starts at the browser');
  assert.equal(M.signInToReuse([{ id: 'd', kind: 'xmla', authMode: 'devicecode' }], 'd').authMode, 'interactive', 'a way the picker does not offer falls back to the browser');
  // The row of a model opened live does not claim a publish target nobody chose (round 2 UAT N22).
  const Hub = load(join(webview, 'src', 'connectionshub-model.ts'));
  const live = { id: 'a', kind: 'xmla' };
  const ctxOf = (implied) => ({ editing: { available: true, connectionId: 'a' }, querying: { available: false }, publishing: { available: true, connectionId: 'a', implied } });
  assert.deepEqual(Hub.rowJobs(live, ctxOf(true)), ['editing'], 'implied: editing only');
  assert.deepEqual(Hub.rowJobs(live, ctxOf(false)), ['editing', 'publish target'], 'once chosen or published to: it is the publish target');
  // One "Other ways to sign in" on screen after a sign-in that did not finish (round 2 UAT N14).
  const unfinished = readable(wsView({ signIn: 'unfinished', workspaces: { kind: 'loading' } }));
  assert.equal((unfinished.match(/Other ways to sign in/g) || []).length, 1, 'the words appear once');
}

console.log('connect pickers: rules, Desktop states, workspace states and wiring all hold');
