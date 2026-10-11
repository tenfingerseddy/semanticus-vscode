// Verifier check of m11/drafts-sol round 3, 5 Oct (item 4 of the check: no false refusal in steady state). The Plan page's "Save the changes you have made" row
// (plan.tsx, savesOnRow, the row 'ship/unsaved') saves through the host: the page posts { type: 'saveModel' } and the Studio relay runs saveCommand(). That message is
// not a command, so no registerPersonCommand wrapper opened a scope for it, and it is not an 'rpc' message, so the relay's withModelIdentity does not name the page's
// model either. saveCommand's `save` request therefore goes out from the extension's own writer with `namesModel: true` and no `modelIdentity`, and the round-3 strict
// rule refuses it (SaveAsync is a Hold row, so Fenced; it is not in ModelFence.Unnamed) whenever a model is open, which is every time a Save row is shown.
// The person presses Save on the Plan, nothing is saved, and they read "This page was showing another model than the one open now" about the model they are looking at.
//
// The relay handler and saveCommand are the REAL source of extension.ts (transpiled), with only their surroundings stubbed. The connection writes through the extension's
// own compiled FencedMessageWriter, and the stub engine applies the two rules the real PersonFence applies to a request from that writer (a request that names another
// model is refused; one that names none while a model is open is refused, ModelFence.cs). Left failing on purpose: this is the defect.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const { FencedMessageWriter, personAction, insideAnyAction, fenceIdentity, MODEL_REFUSAL } = require(join(root, 'out', 'modelFence.js'));
const extSrc = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

const A = 'file:/m/A.bim';

/** The source of one top-level function of extension.ts (`async function name(` or `function name(`), transpiled to plain JavaScript. */
function functionSource(name) {
  const find = (head) => [extSrc.indexOf(`${head}${name}(`), extSrc.indexOf(`${head}${name}<`)].filter((i) => i >= 0).sort((a, b) => a - b)[0] ?? -1;
  let start = find('\nasync function ');
  if (start < 0) start = find('\nfunction ');
  assert.ok(start >= 0, `${name} is a function of extension.ts`);
  const next = extSrc.slice(start + 1).search(/\n(async function |function |class |const |let |export |\/\/\/ |\/\/ ----)/);
  const body = extSrc.slice(start + 1, start + 1 + next);
  return ts.transpileModule(body, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
}

/** Model A open with unsaved changes. A write that gets through is recorded against the open model. */
function world() {
  const engine = { open: { identity: A, session: 's-a', source: '/m/A.bim' }, saved: [] };
  const refusals = [];
  const writer = new FencedMessageWriter({ write: () => Promise.resolve(), onError: () => undefined, onClose: () => undefined, end() {}, dispose() {} });
  let id = 0;
  const apply = (msg) => {
    if (msg.method === 'sessionInfo') return { sessionId: engine.open.session, modelIdentity: engine.open.identity, source: engine.open.source, modelName: 'A', hasUnsavedChanges: true };
    // PersonFence, round 2: the request names another model than the open one.
    if (msg.modelIdentity && msg.modelIdentity !== engine.open.identity) { refusals.push(msg.method); throw new Error(MODEL_REFUSAL); }
    // PersonFence, round 3: the extension's own writer names a model on every request; a Fenced write that names none is refused while a model is open.
    if (msg.namesModel && !msg.modelIdentity) { refusals.push(msg.method); throw new Error(MODEL_REFUSAL); }
    if (msg.method === 'save') { engine.saved.push(engine.open.identity); return { path: engine.open.source, format: 'BIM', fileCount: 1 }; }
    return undefined;
  };
  const conn = {
    sendRequest(method, ...params) {
      const msg = { jsonrpc: '2.0', id: ++id, method, params };
      void writer.write(msg);   // the fenced writer stamps the request in the same synchronous step, as vscode-jsonrpc does
      try { return Promise.resolve(apply(msg)); } catch (e) { return Promise.reject(e); }
    },
  };
  return { engine, refusals, conn };
}

/** How the Studio panel registers its relay handler in the source under test: the registration line read from extension.ts, so the harness wraps the handler exactly as
 *  production does (join 11 wraps it in pageScoped; before that it was registered raw). Retargeted at the join (as a2079819 did for round 2): the steps and assertions are unchanged. */
const registration = /panel\.webview\.onDidReceiveMessage\((pageScoped\()?studioRelayHandler\(panel, false\)/.exec(extSrc);
assert.ok(registration, 'the Studio panel registers studioRelayHandler(panel, false)');

/** The relay handler the Studio panel registers, wrapped as extension.ts wraps it, with the real saveCommand (and the real runPersonAction and pageScoped) behind it.
 *  `hostModelIdentity` is the model the host shows: A, the open one. */
function relay(w, hostModelIdentity = A) {
  const errors = [];
  const posted = [];
  const vscode = { window: { showInformationMessage: () => undefined, setStatusBarMessage: () => undefined, showWarningMessage: async () => undefined, showErrorMessage: (m) => { errors.push(m); } } };
  const scope = {
    conn: w.conn, vscode, propGrid: { commitFocused: async () => undefined }, commitStudioFocused: async () => undefined,
    pickFirstSaveDestination: async () => undefined, rememberLastModel: async () => undefined, watchModelDisk: () => undefined,
  };
  const saveCommand = new Function(...Object.keys(scope), `${functionSource('saveCommand')}\nreturn saveCommand;`)(...Object.values(scope));
  const handlerScope = { saveCommand };
  const studioRelayHandler = new Function(...Object.keys(handlerScope), `${functionSource('studioRelayHandler')}\nreturn studioRelayHandler;`)(...Object.values(handlerScope));
  const panel = { webview: { postMessage: (m) => { posted.push(m); } }, dispose() {} };
  let handler = studioRelayHandler(panel, false);
  if (registration[1]) {
    const personScope = { vscode, hostModelIdentity, insideAnyAction, personAction, fenceIdentity, conn: w.conn };
    const runPersonAction = new Function(...Object.keys(personScope), `${functionSource('runPersonAction')}\nreturn runPersonAction;`)(...Object.values(personScope));
    const pageScoped = new Function('runPersonAction', `${functionSource('pageScoped')}\nreturn pageScoped;`)(runPersonAction);
    handler = pageScoped(handler);
  }
  return { handler, saveCommand, errors, posted };
}

test('the Plan page\'s Save row (the page\'s saveModel message) saves the model that is open, the one the page shows', async () => {
  const w = world();
  const r = relay(w);
  await r.handler({ type: 'saveModel', id: 7, modelIdentity: A });   // the page names the model it shows, as join 11's page does (an older page names none: the host's A)
  assert.deepEqual(w.engine.saved, [A], `the save of A was refused (${w.refusals.join(', ')}); the person read: ${r.errors.join(' | ')}`);
  assert.deepEqual(r.posted.find((m) => m.type === 'saveModelResult'), { type: 'saveModelResult', id: 7, saved: true });
});

test('control: the same saveCommand saves A when it runs as a person\'s action on A (Ctrl+S, the palette), so the stub is not refusing every save', async () => {
  const w = world();
  const r = relay(w);
  const saved = await personAction([], A, () => r.saveCommand());
  assert.equal(saved, true);
  assert.deepEqual(w.engine.saved, [A]);
});
