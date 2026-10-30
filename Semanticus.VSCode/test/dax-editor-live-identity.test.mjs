// Run the real editor open, virtual file system, and save decision. The UI can expose the tree before
// refreshStatus finishes binding an openLive session, so its first editor URI has no mk.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';
import { decideDaxSave, identityToken, pickOpenDaxHref, splitDaxHeader, shouldApplyDaxBuffer } from '../out/daxHeader.js';
import { personAction } from '../out/modelFence.js';

const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
const slice = (start, end) => source.slice(source.indexOf(start), source.indexOf(end, source.indexOf(start)));
const compile = text => ts.transpileModule(text, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
const ast = ts.createSourceFile('extension.ts', source, ts.ScriptTarget.Latest, true);
const declaration = name => ast.statements.find(node => node.name?.text === name)?.getText(ast) ?? '';
const ref = 'measure:Sales/Category % of Total Sales';
function makeUri(query = '') {
  return { scheme: 'semanticus', query, path: '/measure:Sales/Category % of Total Sales.dax',
    toString: () => 'semanticus:/measure:Sales/Category % of Total Sales.dax' + (query ? '?' + query : ''),
    with: ({ query }) => makeUri(query) };
}
const initial = 'DIVIDE ( [Sales Amount], CALCULATE ( [Sales Amount], REMOVEFILTERS ( Product ) ) )';
const edited = '[Sales Amount]';

function scene() {
  let uri = makeUri();
  const engine = {
    sessionId: 'live-session', revision: 0, modelIdentity: 'live:example|Demo', liveBound: true,
    source: '/scratch/semanticus-live/1234/Demo.bim', dax: initial, writes: [], fileSaves: 0,
    openedLive: false,
  };
  let duringGet;
  const conn = { sendRequest: async (method, ...args) => {
    if (method === 'listConnections') return [];
    if (method === 'openLive') {
      assert.equal(args[0], 'powerbi://example/Demo'); assert.equal(args[1], 'Demo');
      engine.openedLive = true;
      return { sessionId: engine.sessionId, modelName: 'Demo' };
    }
    if (method === 'sessionInfo') return { ...engine };
    if (method === 'getDax') { const value = engine.dax; duringGet?.(); return value; }
    if (method === 'setDax') {
      assert.equal(args[0], ref);
      assert.equal(args[3], engine.sessionId, 'Save fences the live session');
      assert.equal(args[5], engine.dax, 'Save names the formula the editor read');
      engine.writes.push(args); engine.dax = args[1];
      return { revision: ++engine.revision, changed: true };
    }
    throw new Error('Unexpected RPC: ' + method);
  } };
  const vscode = {
    EventEmitter: class { event() {} fire() {} },
    FileSystemError: class extends Error { static Unavailable(message) { return new this(message); } },
    Uri: { parse: text => makeUri(new URL(text).search.slice(1)) },
    Position: class {}, Location: class { constructor(uri) { this.uri = uri; } },
    TabInputText: class {},
    languages: { setTextDocumentLanguage: async () => {} },
    workspace: { textDocuments: [], openTextDocument: async target => {
      uri = target;
      const text = Buffer.from(await fsys.readFile(uri)).toString();
      return { uri, getText: () => text };
    } },
    window: { setStatusBarMessage() {}, tabGroups: { all: [] }, showTextDocument: async () => {} },
  };
  const context = {
    Buffer, vscode, personAction, decideDaxSave, identityToken, splitDaxHeader, shouldApplyDaxBuffer,
    headerUriModelKey: u => new URLSearchParams(u.query).get('mk') ?? undefined,
    isDaxHeaderDoc: () => false, refFromUri: () => ref, displayDax: value => value,
    noteMeasureSaved() {}, saveCommand: async () => { engine.fileSaves++; return true; },
    conn, daxHeaderModelKey: undefined, pickOpenDaxHref, uriForRef: () => makeUri(),
    validateDaxDoc() {}, checkedEditor: { refresh() {} }, URLSearchParams,
    daxSymbols: { measures: [{ name: 'Category % of Total Sales', ref }], columns: [] },
    eqi: (a, b) => a.toLowerCase() === b.toLowerCase(),
  };
  const Fsys = runInNewContext(compile(slice('function modelKeyOf(', 'function clearDaxHeaderDocsOnSwap('))
    + compile(slice('function movedFormulaError(', '// ---- DAX language service')) + '; DaxFileSystem;', context);
  const fsys = new Fsys(() => conn);
  const openAt = runInNewContext(compile(declaration('resolveDaxEditorUri') + declaration('openDaxAt')) + '; openDaxAt;', context);
  const definition = runInNewContext(compile(declaration('resolveDaxDefinition')) + '; resolveDaxDefinition;', context);
  const openLive = runInNewContext(compile(declaration('openLiveTarget')) + '; openLiveTarget;');
  return { engine, fsys, read: () => fsys.readFile(uri), write: () => fsys.writeFile(uri, Buffer.from(edited)),
    open: () => openAt(uri, ref),
    linkOpen: async () => vscode.workspace.openTextDocument((await definition({ kind: 'measure', name: 'Category % of Total Sales' })).uri),
    uri: () => uri,
    openLive: () => openLive(conn, { kind: 'live', endpoint: 'powerbi://example/Demo', database: 'Demo' }, false, true),
    duringGet: fn => { duringGet = fn; } };
}

// Exercise the actual UI bind order. Tree refresh can open the editor while the status
// read is pending. No driver command, cached model key, or tree-only identity is supplied.
{
  const s = scene();
  await s.openLive();
  assert.equal(s.engine.openedLive, true, 'the extension opened the live model through openLiveTarget');
  let opened;
  let treeRefreshed;
  const opening = new Promise(resolve => { treeRefreshed = resolve; });
  let finishStatus;
  const status = new Promise(resolve => { finishStatus = resolve; });
  const bind = runInNewContext(compile(slice('async function bindGridAndTree(', 'function refreshOpenDaxEditors(')) + '; bindGridAndTree;', {
    propGrid: { showModel: async () => {} }, refreshStatus: () => status,
    rememberLastModel: async () => {}, watchModelDisk() {}, rebuildDaxSymbols() {}, refreshOpenDaxEditors() {},
    postToPanels() {}, resyncPropertiesFromTree() {}, setTimeout: fn => fn(),
  });
  const binding = bind('Demo', { clearAssistantFlash() {}, resetChecks() {}, refresh() { opened = s.open(); treeRefreshed(); } });
  await opening;
  await opened;
  await s.write();
  assert.equal(s.engine.dax, edited, 'a DAX edit saves on the openLive session before status binding finishes');
  assert.equal(s.engine.writes.length, 1);
  assert.equal(s.engine.fileSaves, 0, 'Save applies DAX to the live session');
  finishStatus(s.engine); await binding;
}

// A dirty editor keeps the identity captured in its URI. Save cannot adopt the
// currently open model, and a later reread cannot replace that ownership.
{
  const s = scene(); await s.open();
  s.engine.sessionId = 'another-session'; s.engine.source = '/scratch/semanticus-live/5678/Other.bim';
  s.engine.modelIdentity = 'live:example|Other';
  await s.read();
  await assert.rejects(s.write, /different model session/);
  assert.equal(s.engine.writes.length, 0);
}

// A model swap while the formula is read must not associate its bytes with another session.
{
  const s = scene();
  s.duringGet(() => { s.engine.sessionId = 'another-session'; s.engine.source = '/scratch/semanticus-live/5678/Other.bim'; });
  await s.open();
  await assert.rejects(s.write, /different model session/);
  assert.equal(s.engine.writes.length, 0);
}

// No successful editor read means no ownership to recover at Save time.
{
  const s = scene();
  await assert.rejects(s.write, /identity is missing/);
  assert.equal(s.engine.writes.length, 0);
}
// A restored URI with no ownership must not adopt another model merely because VS Code reread it.
{
  const s = scene();
  s.engine.source = '/scratch/semanticus-live/5678/Other.bim'; s.engine.sessionId = 'another-session';
  s.engine.dax = '200';
  await s.read();
  await assert.rejects(s.write, /identity is missing/);
  assert.equal(s.engine.writes.length, 0);
}
// A definition link also names the live session before the status cache is ready.
{
  const s = scene();
  const doc = await s.linkOpen();
  assert.equal(doc.getText(), initial);
  assert.equal(new URLSearchParams(s.uri().query).get('mk'), identityToken(s.engine.source));
  await s.write();
  assert.equal(s.engine.dax, edited);
}
console.log('dax-editor-live-identity: 6 scenarios passed');
