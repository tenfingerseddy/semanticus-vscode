// fx33 (rc32 tester, gap 2): after a full VS Code restart the tree showed no mark on any measure and every hover read
// "Not checked yet", also for a Broken measure, while the Checks page and Properties showed the last marks at once. Reload
// Window worked. On a cold start the engine starts with no model: the tree's first read of the check states (at attach) gets
// nothing, and the reopen then asks again (resetChecks) and refreshes at once. The refresh started a newer read, and the
// newer read did not ask for the states, so the tree never got the saved marks. Finish line C7: a fresh open shows the last
// marks in the tree before any run. Runs the real ModelTreeProvider of src/extension.ts (transpiled) against a fake engine.
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
const marks = require(join(root, 'out', 'marksModel.js'));
const src = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');

function slice(from, to) {
  const start = src.indexOf(from); assert.ok(start >= 0, from);
  return src.slice(start, src.indexOf(to, start));
}
const js = ts.transpileModule(slice('function stampModel(', '// ---- display folders (TE2-style)'),
  { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;

function build() {
  const scope = {
    vscode: {
      EventEmitter: class { constructor() { this.event = () => undefined; } fire() {} },
      TreeItem: class { constructor(label) { this.label = label; } }, TreeItemCollapsibleState: {}, ThemeIcon: class {}, ThemeColor: class {},
    },
    out: { appendLine: () => undefined },
    treeSelectionGuard: { noteRefresh: () => undefined },
    ...folders,
    acceptStates: marks.acceptStates, stateMarks: marks.stateMarks, treeRowItem: marks.treeRowItem,
    iconFor: () => undefined, DAX_KINDS: new Set(['measure']),
  };
  const make = new Function(...Object.keys(scope), `let hostModelIdentity;\n${js}\nreturn { ModelTreeProvider };`);
  return make(...Object.values(scope)).ModelTreeProvider;
}

const COST = 'measure:Sales/Total Cost';
const BROKEN = {
  measureRef: COST, version: 7, lastChecked: '2026-10-29T11:06:00Z', verdict: 'broken',
  marks: [{ name: 'Trusted number', state: 'suspect', verdict: 'broken', applies: true, sentence: '89,765,267.02 higher than your number.', lastRan: '2026-10-29T11:06:00Z' }],
};

/** An engine that starts with no model, as after a full restart: every model read fails until the reopen opens one. Each
 *  answer waits for the test to release it, so the reads can overlap the way the start path makes them overlap. */
function coldEngine() {
  let open = false;
  const pending = [];
  const calls = [];
  return {
    calls,
    open: () => { open = true; },
    sendRequest(method) {
      calls.push(method);
      const model = open;
      return new Promise((resolve, reject) => pending.push(() => {
        if (!model) return reject(new Error('No model is open.'));
        if (method === 'getChecksPage') return resolve({ measures: [{ ref: COST }] });
        if (method === 'getMeasureCheckCache') return resolve([structuredClone(BROKEN)]);
        return resolve(null);
      }));
    },
    async drain() { for (let i = 0; i < 20; i++) { while (pending.length) pending.shift()(); await new Promise(r => setImmediate(r)); } },
  };
}

function row(tree) {
  const item = tree.getTreeItem({ ref: COST, name: 'Total Cost', kind: 'measure', hasChildren: false });
  return { label: item.accessibilityInformation?.label ?? '', tooltip: String(item.tooltip ?? ''), description: String(item.description ?? '') };
}

test('fx33 gap 2: on a cold start (attach with no model, then the reopen resets and refreshes) the tree shows the saved Broken mark', async () => {
  const ModelTreeProvider = build();
  const tree = new ModelTreeProvider();
  const engine = coldEngine();
  tree.attach(engine);              // the engine is up, with no model yet
  await engine.drain();
  engine.open();                    // reopenLastModel opens pilot2 by itself
  tree.resetChecks();               // bindGridAndTree (session/didChange): another model opened
  tree.refresh();                   // ... and redraws at once
  tree.refresh();                   // the start path redraws once more after the reopen
  await engine.drain();
  const shown = row(tree);
  assert.ok(engine.calls.filter(m => m === 'getMeasureCheckCache').length >= 2,
    'the tree must read the check states again once the model is open: ' + engine.calls.join(', '));
  assert.match(shown.label, /Broken/, 'tree row label after a cold start: ' + JSON.stringify(shown));
  assert.doesNotMatch(shown.tooltip, /Not checked yet/, 'hover after a cold start: ' + JSON.stringify(shown));
});

test('fx33 gap 2: a refresh while the states read is out does not drop the states the reset asked for', async () => {
  const ModelTreeProvider = build();
  const tree = new ModelTreeProvider();
  const engine = coldEngine();
  engine.open();
  tree.attach(engine);
  tree.refresh();                   // model/didChange lands before the attach read answers
  await engine.drain();
  assert.match(row(tree).label, /Broken/, 'tree row label: ' + JSON.stringify(row(tree)));
});
