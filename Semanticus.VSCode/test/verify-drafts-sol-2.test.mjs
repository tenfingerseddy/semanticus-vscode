// Verifier re-check of m11/drafts-sol round 2, 5 Oct: the builder's join item 2 ("native tree commands and the DAX editor name no model, so they are unfenced").
// The DAX editor is not the gap: its Save reads the live session at Save time and every write carries it as expectedSession, which the engine checks inside its
// own write (extension.ts writeFile, LocalEngine.GuardExpectedModel). The tree's own commands are the gap. Rename (F2), Delete (the Delete key) and Add Row-Level
// Filter each wait on the person (an input box, a modal) and then send a write that names no model and no session. When another model opens while the person
// is typing or reading (the assistant opens B over MCP), the name, the delete or the typed filter lands on B's object of the same name.
//
// Each test runs the command's REAL source (extension.ts, transpiled) with only its surroundings stubbed: the connection writes through the extension's own
// compiled FencedMessageWriter (out/modelFence.js), so a model named with withModelIdentity is seen exactly as the engine would see it. The stub engine
// applies the two fences the real one has (the request's modelIdentity, PersonFence; an expectedSession argument, GuardExpectedModel) and nothing else.
// tools/uishot/verify-dsol2-tree-drive.mjs sends the same requests to a real engine. Left failing on purpose: this is the defect.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const { FencedMessageWriter, withModelIdentity, personAction } = require(join(root, 'out', 'modelFence.js'));
const extSrc = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

/** The source of one top-level `async function name(...)` of extension.ts, transpiled to plain JavaScript. */
function commandSource(name) {
  const start = extSrc.indexOf(`\nasync function ${name}(`);
  assert.ok(start >= 0, `${name} is a function of extension.ts`);
  const next = extSrc.slice(start + 1).search(/\n(async function |function |class |const |let |export )/);
  const body = extSrc.slice(start + 1, start + 1 + next);
  return ts.transpileModule(body, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
}

/** Two models with the same objects, A open first. `switchToB()` is what the assistant does over MCP while the person is still in the prompt. */
function world() {
  const engine = {
    open: { identity: 'file:/m/A.bim', session: 's1' },
    models: {
      'file:/m/A.bim': { measures: ['Amount'], filters: {} },
      'file:/m/B.bim': { measures: ['Amount'], filters: {} },
    },
  };
  const switchToB = () => { engine.open = { identity: 'file:/m/B.bim', session: 's2' }; };
  const sent = [];
  // What vscode-jsonrpc does: the request is handed to the writer in the same synchronous step as sendRequest, so the fenced writer stamps the named model on it.
  const writer = new FencedMessageWriter({ write: (m) => { sent.push(m); return Promise.resolve(); }, onError: () => undefined, onClose: () => undefined, end() {}, dispose() {} });
  let id = 0;
  const apply = (msg) => {
    const [method, args] = [msg.method, msg.params];
    // PersonFence: a write whose request names another model than the open one is refused.
    if (msg.modelIdentity && msg.modelIdentity !== engine.open.identity) throw new Error('This page was showing another model than the one open now, so nothing was changed.');
    const m = engine.models[engine.open.identity];
    const sessionArg = { renameObject: 3, deleteObject: undefined, deleteObjects: undefined, setTablePermission: undefined }[method];
    // GuardExpectedModel: an expectedSession argument that is not the open session is refused.
    if (sessionArg !== undefined && args[sessionArg] && args[sessionArg] !== engine.open.session) throw new Error('The model changed since this was opened.');
    if (method === 'renameObject') { const name = args[0].split('/').pop(); m.measures = m.measures.map((x) => (x === name ? args[1] : x)); return { newRef: 'measure:Sales/' + args[1] }; }
    if (method === 'deleteObject') { const name = args[0].split('/').pop(); m.measures = m.measures.filter((x) => x !== name); return undefined; }
    if (method === 'deleteObjects') { for (const r of args[0]) { const name = r.split('/').pop(); m.measures = m.measures.filter((x) => x !== name); } return undefined; }
    if (method === 'setTablePermission') { if ((m.filters[args[1]] ?? '') !== (args[4] ?? '')) throw new Error('moved'); m.filters[args[1]] = args[2]; return {}; }
    if (method === 'listRoles') return [{ name: args?.[0] ?? 'Readers', tableFilters: [] }];
    if (method === 'sessionInfo') return { modelIdentity: engine.open.identity, sessionId: engine.open.session };
    return undefined;
  };
  const conn = {
    sendRequest(method, ...params) {
      const msg = { jsonrpc: '2.0', id: ++id, method, params };
      void writer.write(msg);
      try { return Promise.resolve(apply(msg)); } catch (e) { return Promise.reject(e); }
    },
  };
  return { engine, switchToB, sent, conn };
}

function run(name, w, vscodeWindow, extra = {}) {
  const errors = [];
  const window = { showInformationMessage: () => undefined, showWarningMessage: () => undefined, showErrorMessage: (m) => { errors.push(m); }, setStatusBarMessage: () => undefined, ...vscodeWindow };
  const scope = {
    conn: w.conn, vscode: { window }, tree: { refresh: () => undefined }, treeView: { selection: [] }, warnNoEngine: () => undefined,
    COPYABLE_KINDS: { measure: 'measure' }, renameFolderCmd: async () => undefined, withModelIdentity,
    selectionRefs: (n, ns) => (ns?.length ? ns : n ? [n] : []), ...extra,
  };
  const handler = new Function(...Object.keys(scope), `${commandSource(name)}\nreturn ${name};`)(...Object.values(scope));
  // Round 3 retarget (verifier, the advisor's instruction): the command runs as the one registration wrapper runs it, through the compiled personAction, with the host
  // showing A when the person asked. The steps and every assertion are unchanged.
  const fn = (...args) => personAction(args, 'file:/m/A.bim', () => handler(...args), (m) => { errors.push(m); });
  return { fn, errors };
}

const node = { ref: 'measure:Sales/Amount', name: 'Amount', kind: 'measure' };

test('tree Rename (F2): a name typed for A while B opens is not given to B\'s object of the same name', async () => {
  const w = world();
  const { fn } = run('renameCmd', w, { showInputBox: async () => { w.switchToB(); return 'Revenue'; } });   // the person types; meanwhile the assistant opens B
  await fn(node);
  assert.deepEqual(w.engine.models['file:/m/B.bim'].measures, ['Amount'], `B's measure was renamed by a name typed on A (request sent: ${JSON.stringify(w.sent.at(-1))})`);
});

test('tree Add Row-Level Filter: a filter typed for A while B opens is not written into B\'s role', async () => {
  const w = world();
  let asked = 0;
  const { fn } = run('roleAddRlsCmd', w, { showInputBox: async () => (++asked === 1 ? 'table:Sales' : (w.switchToB(), '[Region] = "West"')) });
  await fn({ name: 'Readers', kind: 'role', ref: 'role:Readers' });
  assert.equal(w.engine.models['file:/m/B.bim'].filters['table:Sales'], undefined, `B's role got the filter typed on A (request sent: ${JSON.stringify(w.sent.at(-1))})`);
});

test('control: with no model switch each command writes on A as the person asked (the stub is not refusing everything)', async () => {
  const w1 = world();
  await run('renameCmd', w1, { showInputBox: async () => 'Revenue' }).fn(node);
  assert.deepEqual(w1.engine.models['file:/m/A.bim'].measures, ['Revenue']);
  const w2 = world();
  await run('authorDelete', w2, { showWarningMessage: async () => { throw new Error('a delete asks nothing: undo is the safety net'); } }).fn(node, [node]);
  assert.deepEqual(w2.engine.models['file:/m/A.bim'].measures, []);
  const w3 = world();
  let asked = 0;
  await run('roleAddRlsCmd', w3, { showInputBox: async () => (++asked === 1 ? 'table:Sales' : '[Region] = "West"') }).fn({ name: 'Readers', kind: 'role', ref: 'role:Readers' });
  assert.equal(w3.engine.models['file:/m/A.bim'].filters['table:Sales'], '[Region] = "West"');
});

test('control: the stub honours the real fences (a request named for A, or carrying A\'s session, is refused once B is open)', async () => {
  const w = world();
  w.switchToB();
  await assert.rejects(withModelIdentity('file:/m/A.bim', () => w.conn.sendRequest('renameObject', node.ref, 'Revenue', 'human')), /another model/);
  await assert.rejects(w.conn.sendRequest('renameObject', node.ref, 'Revenue', 'human', 's1'), /changed/);
  assert.deepEqual(w.engine.models['file:/m/B.bim'].measures, ['Amount']);
});
