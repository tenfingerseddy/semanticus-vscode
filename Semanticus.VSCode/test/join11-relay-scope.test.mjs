// Join 11, J-D1 (the drafts-sol round 3 check, D1): a Studio page message that is not an 'rpc' request ran with no action scope, so its save went out from the extension's own
// writer with `namesModel: true` and no `modelIdentity`, and the strict engine rule refused it for the one open model every time (the Plan's "Save the changes you have made"
// row). The fix runs every such message as ONE person's action on the model its page showed (pageScoped + runPersonAction, extension.ts).
//
// Why this file and not test/verify-drafts-sol-3.test.mjs alone: that file calls the RAW relay handler with only `saveCommand` in scope, so it cannot see a scope opened by
// the function that registers the handler. The real engine-side proof of the same row is tools/uishot/verify-dsol3-weston-plan-save-drive.py. This file runs the REAL
// pageScoped, runPersonAction, studioRelayHandler and saveCommand sources over the compiled FencedMessageWriter and the same two engine rules, and adds the static facts.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const { FencedMessageWriter, personAction, insideAnyAction, fenceIdentity, withModelIdentity, MODEL_REFUSAL } = require(join(root, 'out', 'modelFence.js'));
const extSrc = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const bridgeSrc = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');

const A = 'file:/m/A.bim';
const B = 'file:/m/B.bim';

function functionSource(name) {
  const find = (head) => [extSrc.indexOf(`${head}${name}(`), extSrc.indexOf(`${head}${name}<`)].filter((i) => i >= 0).sort((a, b) => a - b)[0] ?? -1;
  let start = find('\nasync function ');
  if (start < 0) start = find('\nfunction ');
  assert.ok(start >= 0, `${name} is a function of extension.ts`);
  const next = extSrc.slice(start + 1).search(/\n(async function |function |class |const |let |export |\/\/\/ |\/\/ ----)/);
  const body = extSrc.slice(start + 1, start + 1 + next);
  return { text: body, js: ts.transpileModule(body, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText };
}

function world(open = { identity: A }) {
  const engine = { open, saved: [] };
  const refusals = [];
  const writer = new FencedMessageWriter({ write: () => Promise.resolve(), onError: () => undefined, onClose: () => undefined, end() {}, dispose() {} });
  let id = 0;
  const apply = (msg) => {
    if (msg.method === 'sessionInfo') return { sessionId: 's', modelIdentity: engine.open.identity, source: '/m/x.bim', modelName: 'x', hasUnsavedChanges: true };
    if (msg.modelIdentity && msg.modelIdentity !== engine.open.identity) { refusals.push(msg.method); throw new Error(MODEL_REFUSAL); }
    if (msg.namesModel && !msg.modelIdentity) { refusals.push(msg.method); throw new Error(MODEL_REFUSAL); }
    if (msg.method === 'save') { engine.saved.push(engine.open.identity); return { path: '/m/x.bim', format: 'BIM', fileCount: 1 }; }
    return undefined;
  };
  const conn = { sendRequest(method, ...params) { const msg = { jsonrpc: '2.0', id: ++id, method, params }; void writer.write(msg); try { return Promise.resolve(apply(msg)); } catch (e) { return Promise.reject(e); } } };
  return { engine, refusals, conn };
}

/** The registered handler: pageScoped ( studioRelayHandler ), with the real source of each and saveCommand behind them. */
function registered(w, hostModelIdentity) {
  const errors = [];
  const posted = [];
  const warnings = [];
  const vscode = { window: { showInformationMessage: () => undefined, setStatusBarMessage: () => undefined, showWarningMessage: async (m) => { warnings.push(m); }, showErrorMessage: (m) => { errors.push(m); } } };
  const saveScope = { conn: w.conn, vscode, propGrid: { commitFocused: async () => undefined }, commitStudioFocused: async () => undefined,
    pickFirstSaveDestination: async () => undefined, rememberLastModel: async () => undefined, watchModelDisk: () => undefined };
  const saveCommand = new Function(...Object.keys(saveScope), `${functionSource('saveCommand').js}\nreturn saveCommand;`)(...Object.values(saveScope));
  const relayScope = { saveCommand, conn: w.conn, withModelIdentity, ParameterStructures: { byPosition: 1 }, studioRpcError: async (e) => String(e.message ?? e) };
  const studioRelayHandler = new Function(...Object.keys(relayScope), `${functionSource('studioRelayHandler').js}\nreturn studioRelayHandler;`)(...Object.values(relayScope));
  const personScope = { vscode, hostModelIdentity, insideAnyAction, personAction, fenceIdentity, conn: w.conn };
  const runPersonAction = new Function(...Object.keys(personScope), `${functionSource('runPersonAction').js}\nreturn runPersonAction;`)(...Object.values(personScope));
  const pageScoped = new Function('runPersonAction', `${functionSource('pageScoped').js}\nreturn pageScoped;`)(runPersonAction);
  const panel = { webview: { postMessage: (m) => { posted.push(m); } }, dispose() {} };
  return { handler: pageScoped(studioRelayHandler(panel, false)), errors, posted, warnings };
}

test('J-D1: the Plan\'s Save row saves the open model: the page names A, A is open', async () => {
  const w = world(); const r = registered(w, A);
  await r.handler({ type: 'saveModel', id: 7, modelIdentity: A });
  assert.deepEqual(w.refusals, []);
  assert.deepEqual(w.engine.saved, [A]);
  assert.deepEqual(r.posted.find((m) => m.type === 'saveModelResult'), { type: 'saveModelResult', id: 7, saved: true });
});

test('J-D1: a page that names no model (an older page, a fallback key) saves the model the host shows', async () => {
  for (const named of [undefined, 'a-key-the-page-made-up']) {
    const w = world(); const r = registered(w, A);
    await r.handler({ type: 'saveModel', id: 8, modelIdentity: named });
    assert.deepEqual(w.engine.saved, [A], `named ${named}`);
  }
});

test('J-D1: a page that showed model B when the person pressed Save, while A is open now, saves nothing and says so', async () => {
  const w = world({ identity: A }); const r = registered(w, A);
  await r.handler({ type: 'saveModel', id: 9, modelIdentity: B });
  assert.deepEqual(w.engine.saved, [], 'nothing was written to A');
  assert.deepEqual(w.refusals, ['save']);
  assert.deepEqual(r.posted.find((m) => m.type === 'saveModelResult'), { type: 'saveModelResult', id: 9, saved: false });
});

test('J-D1: an rpc message is left alone by pageScoped (it names its own model per request)', async () => {
  const w = world(); const r = registered(w, A);
  await r.handler({ type: 'rpc', id: 1, method: 'save', params: [], modelIdentity: B });
  assert.deepEqual(w.refusals, ['save'], 'the page\'s own name B is what the request carried, not the host\'s A');
  assert.equal(r.posted.find((m) => m.type === 'rpcResult').id, 1);
});

// ---- the sweep: every path from a webview message to the engine is inside a scope that names a model.

test('J-D1 sweep: every Studio relay registration is wrapped in pageScoped, and the relay\'s own engine calls are all the rpc branch\'s withModelIdentity', () => {
  const regs = [...extSrc.matchAll(/onDidReceiveMessage\((.*)\);?\s*$/gm)].map((m) => m[1]);
  const relays = regs.filter((x) => x.includes('studioRelayHandler('));
  assert.equal(relays.length, 2, 'the Studio panel and the standalone Connections panel register the relay');
  for (const x of relays) assert.match(x, /^pageScoped\(studioRelayHandler\(panel, (true|false)\)\)$/, `unwrapped relay registration: ${x}`);
  const relay = functionSource('studioRelayHandler').text;
  const rpcAt = relay.indexOf("if (msg?.type !== 'rpc') return;");
  assert.ok(rpcAt > 0, 'the rpc branch starts after every other branch');
  assert.doesNotMatch(relay.slice(0, rpcAt), /\bconn[!?]?\.|sendRequest\(/, 'no branch before the rpc branch talks to the engine itself: they run in the scope pageScoped opens');
  const calls = [...relay.matchAll(/sendRequest\(/g)];
  assert.equal(calls.length, 2, 'the rpc branch has its two sendRequest calls');
  for (const c of calls) assert.match(relay.slice(Math.max(0, c.index - 90), c.index), /withModelIdentity\(named, \(\) => caller\.$/, 'each relay sendRequest is inside withModelIdentity');
  const wrapper = functionSource('pageScoped').text;
  assert.match(wrapper, /msg\?\.type === 'rpc'\) return handler\(msg\)/);
  assert.match(wrapper, /runPersonAction\(\[\], \(\) => handler\(msg\), msg\?\.modelIdentity\)/);
});

test('J-D1 sweep: the other webview handlers name a model on every engine write (the grid\'s set and the schema panel\'s apply), and no other page message handler exists', () => {
  const sites = [...extSrc.matchAll(/\.onDidReceiveMessage\(/g)].length;
  assert.equal(sites, 4, 'four webview message handlers in extension.ts: the relay (twice), the Properties grid, the schema panel; a fifth needs the same sweep');
  assert.match(extSrc, /withModelIdentity\(msg\.model, \(\) => conn\.sendRequest<SetResult/, 'the grid names the model its words were typed on');
  assert.match(extSrc, /withModelIdentity\(openedOn, \(\) => conn!\.sendRequest\('applySchemaUpdate'/, 'the schema panel names the model it opened on');
});

test('J-D1: the page names its model on the messages that lead to a save (saveModel, openLocalModel)', () => {
  // Since join 11 solui (U1) the stamp is applied by the ONE posting function, so every message type carries it (test/solui-page-stamp.test.mjs proves that).
  assert.match(bridgeSrc, /postToHostStamped\(\{ type: 'saveModel', id \}\)/);
  assert.match(bridgeSrc, /postToHostStamped\(\{ type: 'openLocalModel' \}\)/);
  assert.match(bridgeSrc, /vscode\.postMessage\(\{ \.\.\.message, modelIdentity: pageModelIdentity\(\) \}\)/);
});
