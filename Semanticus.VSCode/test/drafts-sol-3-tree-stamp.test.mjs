// drafts-sol round 3, J2 (the node half): a Model tree node carries the model it was fetched for, taken when the listTree request was SENT, so a context menu or a key press on a node of
// A still names A after B opens. Runs the real ModelTreeProvider and stampModel of src/extension.ts (transpiled) against a fake connection.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const ts = require('typescript');
const folders = require(join(root, 'out', 'folders.js'));
const src = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');

const A = 'file:/m/A.bim';
const B = 'file:/m/B.bim';

function slice(from, to) {
  const start = src.indexOf(from); assert.ok(start >= 0, from);
  return src.slice(start, src.indexOf(to, start));
}
const classSource = slice('function stampModel(', '// ---- display folders (TE2-style)');
const js = ts.transpileModule(classSource, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;

function build() {
  const scope = {
    vscode: { EventEmitter: class { constructor() { this.event = () => undefined; } fire() {} }, TreeItem: class {}, TreeItemCollapsibleState: {}, ThemeIcon: class {} },
    out: { appendLine: () => undefined },
    ...folders,
    refParts: (ref) => { const kind = ref.slice(0, ref.indexOf(':')); const rest = ref.slice(ref.indexOf(':') + 1); const slash = rest.indexOf('/'); return slash < 0 ? { kind, name: rest } : { kind, table: rest.slice(0, slash), name: rest.slice(slash + 1) }; },
    iconFor: () => undefined, DAX_KINDS: new Set(),
  };
  const make = new Function(...Object.keys(scope), `let hostModelIdentity;\n${js}\nreturn { ModelTreeProvider, setHost: (v) => { hostModelIdentity = v; } };`);
  return make(...Object.values(scope));
}

/** A connection whose listTree answers when the test says so, so the host can move while a read is in flight. */
function connection(answers) {
  const pending = [];
  return {
    sendRequest: (method, ref) => new Promise((resolve) => { pending.push(() => resolve(structuredClone(answers[ref ?? 'roots']))); }),
    release: () => { while (pending.length) pending.shift()(); },
  };
}
const flush = () => new Promise((r) => setImmediate(r));

const ANSWERS = {
  roots: [{ ref: 'table:Sales', name: 'Sales', kind: 'table', hasChildren: true }, { ref: 'role:Readers', name: 'Readers', kind: 'role', hasChildren: false }],
  'table:Sales': [{ ref: 'measure:Sales/Amount', name: 'Amount', kind: 'measure', hasChildren: false, displayFolder: 'KPIs' }, { ref: 'measure:Sales/Units', name: 'Units', kind: 'measure', hasChildren: false }],
};

test('J2: every node a read returns carries the model the host showed when the read was SENT, even if the host moved to B before the answer came back', async () => {
  const { ModelTreeProvider, setHost } = build();
  const tree = new ModelTreeProvider();
  const c = connection(ANSWERS); tree.attach(c);
  setHost(A);
  const roots = tree.getChildren();
  await flush(); setHost(B); c.release();   // B opens and the host learns of it while the answer is on its way
  const nodes = await roots;
  assert.deepEqual(nodes.map((n) => n.model), [A, A]);
});

test('J2: the children of a table, and the folder nodes grouped from them, carry the model; a folder takes its children\'s', async () => {
  const { ModelTreeProvider, setHost } = build();
  const tree = new ModelTreeProvider();
  const c = connection(ANSWERS); tree.attach(c);
  setHost(A);
  const kidsP = tree.getChildren({ ref: 'table:Sales', name: 'Sales', kind: 'table', hasChildren: true });
  await flush(); setHost(B); c.release();
  const kids = await kidsP;
  assert.deepEqual(kids.map((k) => [k.kind, k.model]), [['dfolder', A], ['measure', A]], 'the folder and the loose measure are both A\'s');
  setHost(B);   // later: the host shows B. A folder node asked for its children reads the cached A fan and keeps its model
  const inFolder = await tree.getChildren(kids[0]);
  assert.deepEqual(inFolder.map((k) => k.model), [A]);
});

test('J2: getParent hands the parent the model of the child it was asked for; a node that already has a model keeps it (the earliest stamp is the truth)', async () => {
  const { ModelTreeProvider, setHost } = build();
  const tree = new ModelTreeProvider();
  tree.attach(connection(ANSWERS));
  setHost(B);
  const parent = await tree.getParent({ ref: 'role:Readers', name: 'Readers', kind: 'role', hasChildren: false, model: A });
  assert.equal(parent.model, A);
  const none = await tree.getParent({ ref: 'role:Readers', name: 'Readers', kind: 'role', hasChildren: false });
  assert.equal(none.model, undefined, 'a node with no model gives its parent none: nothing is invented');
});

test('J2: with the host showing no model (no session yet) a node is stamped with nothing, and the action falls back to the host\'s view at the click', async () => {
  const { ModelTreeProvider } = build();
  const tree = new ModelTreeProvider();
  const c = connection(ANSWERS); tree.attach(c);
  const roots = tree.getChildren(); await flush(); c.release();
  assert.deepEqual((await roots).map((n) => n.model), [undefined, undefined]);
});
