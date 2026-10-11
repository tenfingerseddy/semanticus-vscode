// Sol join 6, C2: a native DAX editor must not keep stale text after another human write (a script apply, another editor), and a
// save must carry the formula the editor started from so the ENGINE refuses when the live formula has moved.
//
// Runs the real DaxFileSystem class (sliced from src/extension.ts) against a fake engine that does what LocalEngine.SetDaxAsync does:
// it numbers every commit, broadcasts it, and refuses a setDax whose expectedExpression is neither the live formula nor the new one.
// The probe scenario (Sol): save "1 + 1", a script apply writes 99 through the UI (origin human, no replay), the editor re-reads, the person
// adds " + 0" and saves. Before the fix the re-read returned the kept "1 + 1" and the save wrote "1 + 1 + 0" over 99.
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

function scene(pendingRename) {
  const uri = { toString: () => 'semanticus:/measure/Sales/Total.dax', scheme: 'semanticus' };
  const log = { warnings: [], saved: [], reverted: 0 };
  let pickNext;
  const vscode = {
    EventEmitter: class { constructor() { this.event = () => undefined; } fire() {} },
    FileType: { File: 1 }, FileChangeType: { Changed: 2 },
    FileSystemError: class extends Error { static Unavailable(message) { return new this(message); } },
    workspace: { textDocuments: [{ uri }], save: async (u) => { log.saved.push(u.toString()); } },
    window: {
      setStatusBarMessage() {},
      showWarningMessage: (text, ...buttons) => { log.warnings.push({ text, buttons }); return Promise.resolve(pickNext?.()); },
      showTextDocument: async () => undefined,
    },
    commands: { executeCommand: async (c) => { if (c === 'workbench.action.files.revert') log.reverted++; } },
  };
  const engine = { dax: '1', revision: 1, session: 'A', setCalls: [], askedRefs: [] };
  const conn = { sendRequest: async (method, ...args) => {
    if (method === 'getDax') { engine.askedRefs.push(args[0]); return engine.dax; }
    if (method === 'sessionInfo') return { sessionId: engine.session, revision: engine.revision };
    if (method === 'setDax') {
      engine.setCalls.push(args);
      const [, expression, , expectedSession, , expectedExpression] = args;
      if (expectedSession != null && expectedSession !== engine.session) throw new Error('The model changed before this edit landed. Nothing was written.');
      const live = engine.dax ?? '';
      if (expectedExpression != null && live !== expectedExpression && live !== expression) throw new Error('This formula changed in the model after you opened it, so your version was not saved. Nothing was written.');
      if (live !== expression) { engine.dax = expression; engine.revision++; }
      return { revision: engine.revision, changed: live !== expression };
    }
    throw new Error('Unexpected method: ' + method);
  } };
  const make = new Function('vscode', 'uriForRef', 'refFromUri', 'daxHeaderDocs', 'DAX_SCHEME', 'displayDax', 'isDaxHeaderDoc', 'buildDaxHeader',
    'modelKeyOf', 'identityToken', 'splitDaxHeader', 'headerUriModelKey', 'decideDaxSave', 'shouldApplyDaxBuffer', 'noteMeasureSaved', 'getPendingRename', 'personAction',
    classJs + '\nreturn DaxFileSystem;');
  const DaxFileSystem = make(vscode, () => uri, () => 'measure:Sales/Total', new Set(), 'semanticus', (s) => s, () => false, () => '',
    () => 'A', (s) => s, (raw) => ({ header: null, body: raw }), () => undefined, () => ({ kind: 'body' }), (before, after) => before !== after, () => undefined, () => pendingRename, personAction);
  const fsys = new DaxFileSystem(() => conn);
  fsys.attach(conn);
  const read = async () => Buffer.from(await fsys.readFile(uri)).toString('utf8');
  const write = (text) => fsys.writeFile(uri, Buffer.from(text, 'utf8'));
  // What the engine's model/didChange does after any commit.
  const commit = (text, { origin = 'human', replay } = {}) => {
    engine.dax = text; engine.revision++;
    fsys.signalChanged('measure:Sales/Total', origin !== 'human' || !!replay, { sessionId: engine.session, revision: engine.revision });
  };
  return { uri, fsys, engine, read, write, commit, log, pick: (f) => { pickNext = f; } };
}

// 1. Sol's probe: another HUMAN write (a script apply) must reach the editor, and the next save must stand on it.
{
  const s = scene();
  assert.equal(await s.read(), '1');
  await s.write('1 + 1');
  // The echo of the editor's own write: the commit it just made. It must not drop the kept bytes.
  s.fsys.signalChanged('measure:Sales/Total', false, { sessionId: 'A', revision: s.engine.revision });
  assert.equal(await s.read(), '1 + 1', 'the echo of the editor\'s own save keeps the bytes it wrote');
  s.commit('99');   // a script apply: origin human, no replay
  const reread = await s.read();
  assert.equal(reread, '99', 'a human write that is not this editor\'s own is shown, not the kept old bytes');
  await s.write(reread + ' + 0');
  assert.equal(s.engine.dax, '99 + 0', 'the save stands on the formula the editor now shows');
}

// 2. A DIRTY editor (no re-read yet) saves over a newer formula: the save names the formula it started from and the engine refuses.
{
  const s = scene();
  assert.equal(await s.read(), '1');
  s.commit('99');   // lands while the person is typing; the dirty buffer is not re-read
  await assert.rejects(() => s.write('1 + 1 + 0'), /changed in the model after you opened it/, 'the engine refuses a save from an older starting point');
  assert.equal(s.engine.dax, '99', 'the newer formula stands');
  assert.equal(s.engine.setCalls.at(-1)[5], '1', 'the save carried the formula the editor started from');
  assert.equal(s.log.warnings.length, 1, 'the person is offered the choice');
  assert.deepEqual(s.log.warnings[0].buttons, ['Keep mine', 'Use the new one']);
}

// 3. "Keep mine" takes the model's formula as the new starting point and saves again; the engine still checks that one.
{
  const s = scene();
  await s.read();
  s.commit('99');
  s.pick(() => 'Keep mine');
  await assert.rejects(() => s.write('1 + 1 + 0'), /changed in the model after you opened it/);
  await new Promise((r) => setTimeout(r, 5));
  assert.deepEqual(s.log.saved, ['semanticus:/measure/Sales/Total.dax'], 'Keep mine saves the editor again');
  await s.write('1 + 1 + 0');
  assert.equal(s.engine.dax, '1 + 1 + 0', 'and the second save is the person\'s choice');
}

// 4. "Use the new one" drops the person's words: the editor is reverted and the kept bytes are gone.
{
  const s = scene();
  await s.read();
  s.commit('99');
  s.pick(() => 'Use the new one');
  await assert.rejects(() => s.write('mine'));
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(s.log.reverted, 1, 'the editor is reverted to the model');
  assert.equal(await s.read(), '99');
}

// 5. A commit that arrives before the engine's reply to the save: the kept bytes would be older than the model, so they are not kept.
{
  const s = scene();
  await s.read();
  const original = s.engine.setCalls;
  const realSend = s.fsys.conn().sendRequest;
  s.fsys.conn = () => ({ sendRequest: async (m, ...a) => {
    const r = await realSend(m, ...a);
    if (m === 'setDax') s.commit('99');   // another writer commits before the reply reaches the editor
    return r;
  } });
  await s.write('1 + 1');
  assert.equal(await s.read(), '99', 'bytes older than the newest commit seen are not kept');
  assert.ok(original);
}

// 6. A commit of another SESSION with a low revision is not an echo of the editor's own write.
{
  const s = scene();
  await s.read();
  await s.write('1 + 1');
  s.engine.dax = 'other model'; s.engine.session = 'B';
  s.fsys.signalChanged('measure:Sales/Total', false, { sessionId: 'B', revision: 1 });
  assert.equal(await s.read(), 'other model');
}

// 6b. (Sol join 6, round 2, item e) After a rename that committed but whose body write was refused, the object lives at the RENAMED ref. "Keep mine" reads the
// formula to start from there: the old ref no longer exists, and reading it ended in "Could not finish that" with the words kept but nothing saved.
{
  const s = scene({ newRef: 'measure:Sales/Renamed', modelKey: 'A', sessionId: 'A', revision: 2 });
  await s.read();
  s.commit('99');
  s.pick(() => 'Keep mine');
  await assert.rejects(() => s.write('1 + 1 + 0'), /changed in the model after you opened it/);
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(s.engine.askedRefs.at(-1), 'measure:Sales/Renamed', 'Keep mine reads the formula at the renamed ref, where the object now lives');
  assert.deepEqual(s.log.saved, ['semanticus:/measure/Sales/Total.dax'], 'and then saves the editor again');
  assert.equal(s.log.warnings.length, 1, 'with no "Could not finish that" warning after the choice');
}

// 7. The call site passes the commit's session and revision, and no longer decides by origin alone.
assert.match(source, /daxFs\.signalChanged\(d\.ref, n\.origin !== 'human' \|\| !!n\.replay, \{ sessionId: n\.sessionId, revision: n\.revision \}\)/);

console.log('dax editor save fence: ok');
