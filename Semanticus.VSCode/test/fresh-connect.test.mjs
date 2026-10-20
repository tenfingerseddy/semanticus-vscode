// fix-fresh-connect (Kane, 17 Oct, hands-on of 1.5.104): a person with no saved connections pressed Workspace model and
// met "Engine not connected." with a red dot, then a Sign in menu that said "No saved accounts yet. Pick a workspace
// model to sign in." and pointed back at the same page. Only an engine restart got out of it.
//
// Two causes, both proven in a real Extension Development Host before this file was written:
//   (a) the host answered a page's call with "Engine not connected." AT ONCE while its own start or retry was still
//       running (or had failed with a reason it kept to itself), and the picker never asked again when the engine came up;
//   (b) with no saved account the Sign in menu had nothing to press, only a sentence pointing at Workspace model.
// The fix: the host waits for the engine before it answers, says WHY when it cannot, the picker waits and re-asks by itself,
// and an empty account menu is one Sign in press that starts the browser sign-in.
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
const read = (...p) => readFileSync(join(root, ...p), 'utf8');

const STUBS = { './bridge': { rpc: async () => { throw new Error('no calls here'); }, rpcPressed: async () => { throw new Error('no calls here'); }, runHostCommand: () => {}, onEngineState: () => () => {} } };
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true }, fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (STUBS[name] ? STUBS[name] : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const testids = (html) => [...html.matchAll(/data-testid="([^"]+)"/g)].map((m) => m[1]);

const M = load(join(webview, 'src', 'connectpickers-model.ts'));
const V = load(join(webview, 'src', 'connectpickers.tsx'));
const h = React.createElement;
const noop = () => {};
const view = (extra = {}) => renderToStaticMarkup(h(V.WorkspacePickerView, {
  account: null, workspaces: { kind: 'loading' }, wsId: null, models: null, modelId: null, env: '', authMode: 'interactive', tenant: '',
  otherOpen: false, busy: false, connectError: null, onBack: noop, onRetry: noop, onPickWorkspace: noop, onPickModel: noop, onEnv: noop,
  onAuthMode: noop, onTenant: noop, onToggleOther: noop, onPasteAddress: noop, onConnect: noop, ...extra,
}));

// ---- (a) the picker: an engine that is starting is a wait, not a failure -------------------------------------------------
{
  assert.equal(M.isEngineAway(new Error('Engine not connected.')), true, 'the host\'s own answer is recognised');
  assert.equal(M.isEngineAway(new Error('Fabric said 403.')), false, 'any other sentence is the engine\'s and is shown as written');

  const starting = view({ engine: { state: 'starting' } });
  assert.match(readable(starting), /Starting Semanticus/, 'a starting engine is said plainly');
  assert.doesNotMatch(readable(starting), /Engine not connected/, 'and never as "Engine not connected."');
  assert.ok(!testids(starting).includes('pick-ws-failed'), 'a start is not drawn as a failure');
  assert.ok(!testids(starting).includes('pick-ws-retry'), 'a wait has nothing to press');

  const failed = view({ engine: { state: 'failed', reason: 'The engine stopped as it started (code 154). Restart the engine, or open Show details for the reason.' } });
  assert.match(readable(failed), /stopped as it started/, 'a real failure says what broke');
  assert.ok(testids(failed).includes('pick-ws-retry'), 'and offers Try again');
  assert.ok(testids(failed).includes('pick-engine-restart'), 'and Restart engine');
  assert.doesNotMatch(readable(failed), /Engine not connected/);

  // The ordinary view is unchanged for a person whose engine is fine.
  const ok = view({ workspaces: { kind: 'ready', items: [{ id: 'w', displayName: 'Semanticus Demo' }] } });
  assert.match(readable(ok), /Semanticus Demo/);
  assert.match(readable(ok), /You may be asked to sign in/);
}
{
  const src = read('webview', 'src', 'connectpickers.tsx');
  assert.match(src, /onEngineState\(/, 'the picker hears the engine come up');
  assert.match(src, /isEngineAway\(e\)/, 'and tells the host\'s not-connected answer from the engine\'s own sentences');
}

// ---- (a) the host: it waits for the engine before it answers, and keeps the reason ---------------------------------------
{
  const ext = read('src', 'extension.ts');
  assert.match(ext, /async function waitForEngine\(/, 'one place waits for the engine');
  assert.match(ext, /if \(!conn\) \{ await waitForEngine\(\); \}|if \(!conn && !\(await waitForEngine\(\)\)\)/, 'a page\'s call waits for a start or retry that is running before it is refused');
  assert.match(ext, /engineFailureReason/, 'the host keeps why the last start failed');
  assert.match(ext, /type: 'engineState', connected: false, reason: \(engineFailureReason = /, 'and sends the reason with the not-connected state');
  assert.match(ext, /type: 'engineState', connected: !!conn, reason: /, 'a page that opens late is told the reason too');
  // The two doors a new person uses first wait for a starting engine and say why when it cannot start.
  assert.match(ext, /async function quickOpenModelCommand[\s\S]{0,200}if \(!conn\) await waitForEngine\(\);/, 'Open a model waits for a starting engine');
  assert.match(ext, /async function openConnectionsManager[\s\S]{0,700}if \(!conn\) await waitForEngine\(\);/, 'Connections waits for a starting engine');
  assert.match(ext, /function warnNoEngine\(\) \{\s+void vscode\.window\.showWarningMessage\(engineFailureReason \?\? 'Semanticus engine not connected\.', 'Restart engine'\)/, 'and the warning carries the reason when there is one');
  const br = read('webview', 'src', 'bridge.ts');
  assert.match(br, /onEngineState\(fn: \(connected: boolean, reason\?: string\) => void\)/, 'the bridge passes the reason on');
}

// ---- (b) the account menu with nothing saved is one Sign in press --------------------------------------------------------
{
  const hub = read('webview', 'src', 'connectionshub.tsx');
  const a = hub.indexOf('function AccountMenu'); const b = hub.indexOf('function AccountDialog');
  const menu = hub.slice(a, b);
  assert.doesNotMatch(menu, /Pick a workspace model to sign in/, 'no "pick a workspace model" line when there is nothing to pick');
  assert.match(menu, /data-testid="hub-sign-in-first"/, 'an empty menu has one Sign in');
  assert.match(menu, /profiles\.length === 0[\s\S]{0,400}openPicker\(\)/, 'and it starts the sign-in at once, on the workspace page');
  assert.match(menu, /profiles\.length > 0 &&[\s\S]{0,200}hub-sign-in-another-top|profiles\.length === 0 \? null/, 'Sign in another account shows only when there is an account already');
}
console.log('fresh-connect ok');
