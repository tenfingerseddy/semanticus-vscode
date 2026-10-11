// UAT-J7-S5-001: Ctrl+S in the native DAX editor applied the formula to the model and left the model's file as it was, so the person who pressed
// Save had a model with the formula and a file without it. Ctrl+S in Studio and in Properties writes the file, so the DAX editor does too when the
// formula is the only unsaved thing in the model; when something else is unsaved it says what it did and points at Save model.
//
// Runs the real DaxFileSystem class (sliced from src/extension.ts) against a fake engine and a fake saveCommand (the same function Studio's Ctrl+S runs).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { personAction } = createRequire(import.meta.url)(join(root, 'out', 'modelFence.js'));   // a Save is the person's action: its scope opens on the model that is open
const ts = createRequire(join(root, 'webview', 'package.json'))('typescript');
const source = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
const between = (first, last) => source.slice(source.indexOf(first), source.indexOf(last, source.indexOf(first)));
const compile = (text) => ts.transpileModule(text, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const classJs = compile(between(source.includes('function movedFormulaError') ? 'function movedFormulaError' : 'class DaxFileSystem', '// ---- DAX language service'));

function scene(info, { saveResult = true } = {}) {
  const uri = { toString: () => 'semanticus:/measure/Sales/Total.dax', scheme: 'semanticus' };
  const log = { status: [], fileSaves: 0 };
  const vscode = {
    EventEmitter: class { constructor() { this.event = () => undefined; } fire() {} },
    FileType: { File: 1 }, FileChangeType: { Changed: 2 },
    FileSystemError: class extends Error { static Unavailable(message) { return new this(message); } },
    workspace: { textDocuments: [{ uri }], save: async () => undefined },
    window: { setStatusBarMessage: (text) => { log.status.push(text); }, showWarningMessage: () => Promise.resolve(undefined), showTextDocument: async () => undefined },
    commands: { executeCommand: async () => undefined },
  };
  const engine = { dax: '1', revision: 1, session: 'A' };
  const conn = { sendRequest: async (method, ...args) => {
    if (method === 'getDax') return engine.dax;
    if (method === 'sessionInfo') return { sessionId: engine.session, revision: engine.revision, ...info };
    if (method === 'setDax') { engine.dax = args[1]; engine.revision++; return { revision: engine.revision, changed: true }; }
    throw new Error('Unexpected method: ' + method);
  } };
  const saveCommand = async () => { log.fileSaves++; return saveResult; };
  const make = new Function('vscode', 'uriForRef', 'refFromUri', 'daxHeaderDocs', 'DAX_SCHEME', 'displayDax', 'isDaxHeaderDoc', 'buildDaxHeader',
    'modelKeyOf', 'identityToken', 'splitDaxHeader', 'headerUriModelKey', 'decideDaxSave', 'shouldApplyDaxBuffer', 'noteMeasureSaved', 'getPendingRename', 'saveCommand', 'personAction',
    classJs + '\nreturn DaxFileSystem;');
  const DaxFileSystem = make(vscode, () => uri, () => 'measure:Sales/Total', new Set(), 'semanticus', (s) => s, () => false, () => '',
    () => 'A', (s) => s, (raw) => ({ header: null, body: raw }), () => undefined, () => ({ kind: 'body' }), (before, after) => before !== after, () => undefined, () => undefined, saveCommand, personAction);
  const fsys = new DaxFileSystem(() => conn);
  fsys.attach(conn);
  return { uri, fsys, engine, log, write: (text) => fsys.writeFile(uri, Buffer.from(text, 'utf8')), read: async () => Buffer.from(await fsys.readFile(uri)).toString('utf8') };
}
const settle = () => new Promise((r) => setTimeout(r, 5));

// 1. A model with a file and nothing else unsaved: the Save also writes the file.
{
  const s = scene({ source: '/m/Model.bim', hasUnsavedChanges: false });
  await s.read();
  await s.write('1 + 1');
  await settle();
  assert.equal(s.engine.dax, '1 + 1', 'the formula is in the model');
  assert.equal(s.log.fileSaves, 1, 'Ctrl+S in the DAX editor writes the model file, as it does in Studio and Properties');
  assert.deepEqual(s.log.status, [], 'the file save says its own words, this editor adds none');
}

// 2. Something else is unsaved: the file write would carry it too, so the key says what it did and does not write the file.
{
  const s = scene({ source: '/m/Model.bim', hasUnsavedChanges: true });
  await s.read();
  await s.write('1 + 1');
  await settle();
  assert.equal(s.engine.dax, '1 + 1');
  assert.equal(s.log.fileSaves, 0, 'other unsaved changes are not written by this key');
  assert.equal(s.log.status.length, 1);
  assert.match(s.log.status[0], /The formula is in the model\. Use Save model to write the file\./);
}

// 3. A model with no file yet (a new model) or a live model: the key says what it did.
for (const info of [{ source: '', hasUnsavedChanges: false }, { source: '/m/Model.bim', liveBound: true, hasUnsavedChanges: false }]) {
  const s = scene(info);
  await s.read();
  await s.write('1 + 1');
  await settle();
  assert.equal(s.log.fileSaves, 0);
  assert.match(s.log.status[0], /Use Save model to write the file/);
}

// 4. The file save was declined or failed: the formula is still in the model, and the person is told the file was not written.
{
  const s = scene({ source: '/m/Model.bim', hasUnsavedChanges: false }, { saveResult: false });
  await s.read();
  await s.write('1 + 1');
  await settle();
  assert.equal(s.log.fileSaves, 1);
  assert.match(s.log.status[0], /The formula is in the model\. Use Save model to write the file\./);
}
console.log('dax-editor-ctrl-s-file: ok');
