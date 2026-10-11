// Sol join 6, C1: "Apply mine anyway" is a continuation of an apply made in ONE model. The note can stay open while the person opens another
// model, and the second request used to carry no model identity, so the engine resolved the same ref in the NEW model and wrote there.
// This runs the extension's real reportScriptApply and both Apply commands (sliced from extension.ts) against a fake engine that, like the
// real one, refuses a continuation whose session is not the live one. Nothing here reads the source for a regex: the requests are observed.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ts = createRequire(join(root, 'webview', 'package.json'))('typescript');
const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
const compile = (text) => ts.transpileModule(text, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const between = (first, last) => ext.slice(ext.indexOf(first), ext.indexOf(last, ext.indexOf(first)));
const code = compile(between('async function reportScriptApply(', '/// Set IsHidden on the whole selection'));

for (const kind of ['DAX', 'TMDL']) {
  const method = kind === 'DAX' ? 'applyDaxScript' : 'applyTmdlScript';
  const command = kind === 'DAX' ? 'applyDaxScriptCmd' : 'applyTmdlScriptCmd';
  let liveSession = 'A';
  let click;
  const shown = { errors: [], warnings: [] };
  const writes = [];
  const sent = [];
  const script = '// @object measure:Sales/Total @base:123456789abc\n10\n';
  const editor = { document: { isClosed: false, getText: () => script } };
  const vscode = {
    window: {
      activeTextEditor: editor,
      showWarningMessage: (text, action) => { shown.warnings.push(text); return action ? new Promise((r) => { click = r; }) : Promise.resolve(undefined); },
      showInformationMessage() {},
      showErrorMessage: (m) => { shown.errors.push(m); },
    },
  };
  // The fake engine: a conflict on the first (non-continuation) apply, and the real fence on every apply that carries a session.
  const conn = {
    sendRequest: async (name, ...args) => {
      if (name === 'sessionInfo') return { sessionId: liveSession, revision: 1 };
      sent.push({ name, args, liveSession });
      const [text, , anyway, expectedSession] = args;
      if (expectedSession != null && expectedSession !== liveSession) throw new Error('The model changed before this edit landed. Nothing was written.');
      if (!anyway) return { applied: [], skipped: [], conflicts: ['measure:Sales/Total'], notes: ['Sales/Total changed after scripting.'] };
      writes.push({ into: liveSession, text, anyway });
      return { applied: anyway, skipped: [], notes: ['Your version was written.'] };
    },
  };
  const make = new Function('vscode', 'tree', 'conn', 'scriptToResend', 'warnNoEngine',
    code + `\nreturn { ${command}, reportScriptApply };`);
  const api = make(vscode, { refresh() {} }, conn, (source, text) => (source ? source.getText() : text), () => {});

  const pending = api[command]();
  while (!click) await new Promise((r) => setTimeout(r, 1));
  liveSession = 'B';   // the person opens another model while the note is still on screen
  click('Apply mine anyway');
  await pending;
  await new Promise((r) => setTimeout(r, 5));

  const second = sent[1];
  assert.equal(sent[0].name, method, `${kind}: the first apply`);
  assert.equal(sent[0].args[3], 'A', `${kind}: the first apply names the model it was made in`);
  assert.equal(second.name, method, `${kind}: the continuation is the same call`);
  assert.deepEqual(second.args[2], ['measure:Sales/Total'], `${kind}: it names only the conflicts`);
  assert.equal(second.args[3], 'A', `${kind}: the continuation carries the FIRST apply's model, not whichever is open now`);
  assert.deepEqual(writes, [], `${kind}: nothing was written into the other model`);
  assert.equal(shown.errors.length, 1, `${kind}: the person is told`);
  assert.match(shown.errors[0], /Nothing was written/, `${kind}: and told that nothing was written`);
}

// Same model: the continuation still writes.
{
  let click;
  const writes = [];
  const script = '// @object measure:Sales/Total @base:123456789abc\n10\n';
  const vscode = { window: { activeTextEditor: { document: { isClosed: false, getText: () => script } },
    showWarningMessage: (t, a) => (a ? new Promise((r) => { click = r; }) : Promise.resolve()), showInformationMessage() {}, showErrorMessage(m) { throw new Error(m); } } };
  const conn = { sendRequest: async (name, ...args) => {
    if (name === 'sessionInfo') return { sessionId: 'A', revision: 1 };
    if (!args[2]) return { applied: [], skipped: [], conflicts: ['measure:Sales/Total'], notes: [] };
    assert.equal(args[3], 'A');
    writes.push(args[2]);
    return { applied: args[2], skipped: [], notes: [] };
  } };
  const api = new Function('vscode', 'tree', 'conn', 'scriptToResend', 'warnNoEngine', code + '\nreturn { applyDaxScriptCmd };')(vscode, { refresh() {} }, conn, (s, t) => (s ? s.getText() : t), () => {});
  const pending = api.applyDaxScriptCmd();
  while (!click) await new Promise((r) => setTimeout(r, 1));
  click('Apply mine anyway');
  await pending;
  await new Promise((r) => setTimeout(r, 5));
  assert.deepEqual(writes, [['measure:Sales/Total']], 'in the same model the continuation still writes');
}

console.log('script-apply-session: all checks passed');
