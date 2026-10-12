import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const ts = require('typescript');
const source = readFileSync(new URL('../webview/src/studio/said-model.ts', import.meta.url), 'utf8');
const mod = { exports: {} };
new Function('exports', ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(mod.exports);
const { SaidModel, saidCopy, undoEntry, canUndoGroup, appendEdit } = mod.exports;
const synonymRun = new SaidModel();
for (let i = 1; i <= 50; i++) {
  synonymRun.startCall('demo', `synonyms-${i}`, 'set_synonyms');
  synonymRun.change({ sessionId: 'demo', revision: i, origin: 'agent', by: 'assistant',
    deltas: [{ ref: 'culture:en-US', kind: 'update', props: ['Content'] }],
    words: { verb: 'added synonyms to', noun: 'fields', count: 1, targets: [`measure:Sales/Field ${i}`],
      sentence: `Your assistant added synonyms to Field ${i}.` } });
}
assert.equal(saidCopy(synonymRun.current), 'Your assistant added synonyms to 50 fields.');
assert.equal(synonymRun.current.firstRevision, 1);
assert.equal(synonymRun.current.lastRevision, 50);
assert.equal(undoEntry([{ id: 'first-synonym', kind: 'edit', sessionId: 'demo', revision: 1, undoable: true }], synonymRun.current), 'first-synonym');
let folded = [];
for (let i = 1; i <= 50; i++) {
  folded = appendEdit(folded, { seq: -i, kind: 'edit', origin: 'agent', ok: true, target: `call-${i}`,
    words: { verb: 'added synonyms to', noun: 'fields', count: 1, targets: [`measure:Sales/Field ${i}`],
      sentence: `Your assistant added synonyms to Field ${i}.` } });
}
assert.equal(folded.length, 1);
assert.equal(folded[0].label, 'Your assistant added synonyms to 50 fields.');
folded = appendEdit(folded, { ...folded[0], words: { verb: 'added synonyms to', noun: 'fields', count: 1,
  targets: ['measure:Sales/Field 51'], sentence: 'Your assistant added synonyms to Field 51.' } });
assert.equal(folded[0].label, 'Your assistant added synonyms to 51 fields.', 'updating a folded entry keeps its earlier fields');
folded.unshift({ seq: 70, kind: 'run_dax', origin: 'agent', ok: true, label: 'Ran a query' });
folded = appendEdit(folded, { seq: -51, kind: 'edit', origin: 'agent', ok: true, target: 'call-51', words: {
  verb: 'added synonyms to', noun: 'fields', count: 1, targets: ['measure:Sales/Other'], sentence: 'Your assistant added synonyms to Other.' } });
assert.equal(folded.length, 3, 'a different action stops the fold');
folded = appendEdit(folded, { ...folded[0], seq: -52, target: 'call-52', editEpoch: 'demo:1' });
assert.equal(folded.length, 4, 'a person edit stops the feed fold even if it has no feed entry');
const twoFields = new SaidModel();
twoFields.startCall('demo', 'two');
twoFields.change({ sessionId: 'demo', revision: 1, origin: 'agent', deltas: [{ ref: 'culture:en-US', kind: 'update' }],
  words: { verb: 'added synonyms to', noun: 'fields', count: 1, targets: ['measure:Sales/Total'], sentence: 'Your assistant added synonyms to Total.' } });
twoFields.change({ sessionId: 'demo', revision: 2, origin: 'agent', deltas: [{ ref: 'culture:en-US', kind: 'update' }],
  words: { verb: 'added synonyms to', noun: 'fields', count: 1, targets: ['measure:Sales/Margin'], sentence: 'Your assistant added synonyms to Margin.' } });
assert.equal(saidCopy(twoFields.current), 'Your assistant added synonyms to Total and Margin.');
synonymRun.change({ sessionId: 'demo', revision: 51, origin: 'human', by: 'you', deltas: [{ ref: 'table:Sales', kind: 'update' }] });
assert.equal(synonymRun.current, null, 'a person edit stops the said fold');
const edit = (revision, refs, extra = {}) => ({ sessionId: 'demo', revision, origin: 'agent', by: 'assistant',
  deltas: refs.map((ref) => ({ ref, kind: 'update', props: ['Description'] })), ...extra });
const model = new SaidModel();
model.startCall('demo', 'call-1');
model.change(edit(1, ['measure:Sales/Net Sales']));
model.change(edit(2, ['measure:Sales/Total Cost', 'measure:Sales/Net Sales']));
assert.equal(saidCopy(model.current), 'Your assistant changed 2 descriptions.');
assert.equal(model.current.firstRevision, 1);
assert.equal(model.current.lastRevision, 2);
model.startCall('demo', 'call-2');
model.change(edit(3, ['measure:Sales/Units']));
assert.equal(saidCopy(model.current), 'Your assistant changed Units.');
assert.equal(model.current.id, 'call-2');
assert.equal(saidCopy({ ...model.current, deltas: [{ ref: 'table:Sales/2026', kind: 'update' }] }), 'Your assistant changed Sales/2026.');
assert.equal(saidCopy({ ...model.current, deltas: [{ ref: 'measure:Sales/Net/Sales', kind: 'update' }] }), 'Your assistant changed Net/Sales.');
model.change(edit(4, ['table:Sales'], { by: 'you', origin: 'human' }));
assert.equal(model.current, null, 'a person edit closes the assistant group');
model.change(edit(5, ['table:Sales'], { replay: 'undo' }));
assert.equal(model.current, null, 'a history walk makes no line');
model.change(edit(6, ['table:Sales'], { by: null }));
assert.equal(saidCopy(model.current), 'Your assistant changed Sales.');
model.endCall();
model.change(edit(7, ['table:Sales'], { by: 'workflow' }));
assert.equal(model.current, null, 'explicit By wins over origin');
model.startCall('demo', 'call-3');
model.change(edit(8, ['table:Sales', 'table:Date'], { deltas: [
  { ref: 'table:Sales', kind: 'add' }, { ref: 'table:Date', kind: 'add' },
] }));
assert.equal(saidCopy(model.current), 'Your assistant added Sales and Date.');
assert.equal(undoEntry([{ id: 'task', kind: 'task', children: [
  { id: 'edit-8', kind: 'edit', sessionId: 'demo', revision: 8, undoable: true },
] }], model.current), 'edit-8');
model.change(edit(9, ['table:Product'], { cause: { title: 'Update sales words' } }));
assert.equal(saidCopy(model.current), 'Your assistant ran "Update sales words". 3 changes.');
model.change(edit(10, [], { sessionId: 'other' }));
assert.equal(model.current, null, 'a different model clears the group');
console.log('PASS assistant grouping, copy and exact History entry');

const planRun = new SaidModel();
planRun.startCall('demo', 'plan-call', 'apply_plan_items');
planRun.change(edit(11, ['column:Sales/Key'], { by: 'semanticus', origin: 'system', cause: { kind: 'plan_item', title: 'Hide keys', approvedBy: 'assistant' } }));
planRun.change(edit(12, ['column:Customer/Key'], { by: 'semanticus', origin: 'system', cause: { kind: 'plan_item', title: 'Hide keys', approvedBy: 'assistant' } }));
assert.equal(saidCopy(planRun.current), 'Your assistant ran the plan. 2 changes.');
planRun.activity({ kind: 'apply_plan', origin: 'agent', result: { applied: 4 } });
planRun.activity({ kind: 'apply_plan', origin: 'agent', result: { applied: 2 } });
assert.equal(saidCopy(planRun.current), 'Your assistant ran the plan. 6 changes.');
assert.equal(saidCopy({ ...planRun.current, plan: false, title: undefined, deltas: [{ ref: 'model:Culture/en-US', kind: 'update' }] }), "Your assistant changed the model's language.");
const saved = new SaidModel();
saved.startCall('demo', 'save-call', 'save_model');
saved.change(edit(12, [], { label: 'save', by: 'assistant' }));
assert.equal(saidCopy(saved.current), 'Your assistant saved the model.');
assert.equal(saved.current.saved, true);
saved.change(edit(12, [], { label: 'save' }));
assert.equal(saved.current.id, 'save-call', 'a duplicate save notification keeps one line');
const group = { sessionId: 'demo', firstRevision: 11, lastRevision: 12, deltas: [{ ref: 'column:Sales/Key', kind: 'update' }] };
const row = (revision, ref, by = 'assistant') => ({ id: `e${revision}`, kind: 'edit', sessionId: 'demo', revision, by, undoable: true, deltas: [{ ref, kind: 'update' }] });
assert.equal(canUndoGroup([row(13, 'measure:Sales/Amount'), row(11, 'column:Sales/Key')], group), true);
assert.equal(canUndoGroup([row(13, 'column:Sales/Key'), row(11, 'column:Sales/Key')], group), false);
assert.equal(canUndoGroup([row(13, 'measure:Sales/Amount', 'you'), row(11, 'column:Sales/Key')], group), false);
console.log('PASS plan-run line, plain model settings, save line and unrelated assistant undo');

// Exercise the native provider with only VS Code rendering and the clock replaced.
const extension = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
const provider = extension.slice(extension.indexOf('class ModelTreeProvider'), extension.indexOf('// ---- display folders (TE2-style)'));
const nativeJs = ts.transpileModule(provider, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
const timers = new Map();
let timerId = 0;
let refreshes = 0;
const vscode = {
  EventEmitter: class { event = () => {}; fire() { refreshes++; } },
  TreeItem: class { constructor(label) { this.label = label; } },
  TreeItemCollapsibleState: { None: 0, Collapsed: 1 },
  ThemeIcon: class { constructor(id, color) { this.id = id; this.color = color; } },
  ThemeColor: class { constructor(id) { this.id = id; } },
};
const tree = new Function('vscode', 'iconFor', 'DAX_KINDS', 'setTimeout', 'clearTimeout',
  nativeJs + '; return new ModelTreeProvider();')(vscode, () => ({ id: 'symbol-method' }), new Set(['measure']),
  (fn, ms) => { assert.equal(ms, 3000); timers.set(++timerId, fn); return timerId; }, (id) => timers.delete(id));
const node = { ref: 'measure:Sales/Net Sales', name: 'Net Sales', kind: 'measure', hasChildren: false };
assert.equal(tree.getTreeItem(node).iconPath.id, 'symbol-method');
tree.flashAssistant([node.ref]);
assert.equal(tree.getTreeItem(node).iconPath.id, 'circle-filled');
assert.equal(tree.getTreeItem(node).iconPath.color.id, 'charts.purple');
assert.equal(tree.getTreeItem({ ...node, ref: 'measure:Sales/Units' }).iconPath.id, 'symbol-method');
for (const fn of timers.values()) fn();
timers.clear();
assert.equal(tree.getTreeItem(node).iconPath.id, 'symbol-method');
assert.equal(refreshes, 2);
tree.flashAssistant([node.ref]);
tree.clearAssistantFlash();
assert.equal(timers.size, 0);
assert.equal(tree.getTreeItem(node).iconPath.id, 'symbol-method');
console.log('PASS native assistant dot, expiry and model-swap cleanup');

const binding = extension.slice(extension.indexOf('async function bindGridAndTree'), extension.indexOf('function refreshOpenDaxEditors'));
const bindingJs = ts.transpileModule(binding, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
const noop = () => {};
const bind = new Function('consentAsks', 'propGrid', 'refreshStatus', 'rememberLastModel', 'watchModelDisk',
  'rebuildDaxSymbols', 'refreshOpenDaxEditors', 'postToPanels', 'setTimeout', 'resyncPropertiesFromTree',
  bindingJs + '; return bindGridAndTree;')({ noteSwap: noop }, { showModel: async () => {} }, async () => ({}),
  noop, noop, noop, noop, noop, noop, noop);
tree.flashAssistant([node.ref]);
await bind('Replacement model', tree);
assert.equal(tree.getTreeItem(node).iconPath.id, 'symbol-method', 'a person opening another model clears the old dot');
console.log('PASS person model-open clears assistant marks');

// A5 (hero16b): one unit. A 26-field edit said "29 changes" to the person and "changed 26 fields" to Claude.
// The line counts the fields asked about, and names the formulas a rename also updated.
const fieldsRun = new SaidModel();
const targets = Array.from({ length: 26 }, (_, i) => `column:Sales/Field ${i}`);
const formulas = ['measure:Sales/Net', 'measure:Sales/Margin', 'measure:Sales/Units'];
fieldsRun.startCall('demo', 'fields-call', 'edit_objects');
fieldsRun.change({ sessionId: 'demo', revision: 30, origin: 'agent', by: 'assistant', cause: { title: 'Name the columns' },
  deltas: [...targets.map((ref) => ({ ref, kind: 'update', props: ['Name'], before: 'Old' })), ...formulas.map((ref) => ({ ref, kind: 'update', props: ['Expression'] }))],
  words: { verb: 'changed', noun: 'fields', count: 26, targets, sentence: 'Your assistant changed 26 fields.' } });
assert.equal(saidCopy(fieldsRun.current), 'Your assistant ran "Name the columns". 26 fields, and 3 formulas that use their names.');
fieldsRun.current = { ...fieldsRun.current, deltas: targets.map((ref) => ({ ref, kind: 'update', props: ['Name'] })) };
assert.equal(saidCopy(fieldsRun.current), 'Your assistant ran "Name the columns". 26 fields.');
console.log('PASS one unit in the said line');

// A5 follow-up (verifier): the engine names a model setting by words ("the model's description"), never by its ref ("model:"),
// so no ref is in the asked set. One change must still read "1 change", not "1 model setting, and 1 other change".
const settingRun = new SaidModel();
settingRun.startCall('demo', 'setting-call', 'edit_objects');
settingRun.change({ sessionId: 'demo', revision: 31, origin: 'agent', by: 'assistant', cause: { title: 'Describe the model' },
  deltas: [{ ref: 'model:', kind: 'update', props: ['Description'] }],
  words: { verb: 'changed', noun: 'model settings', count: 1, targets: ["the model's description"], sentence: "Your assistant changed the model's description." } });
assert.equal(saidCopy(settingRun.current), 'Your assistant ran "Describe the model". 1 change.');
console.log('PASS a mapped target is not counted as an extra');
