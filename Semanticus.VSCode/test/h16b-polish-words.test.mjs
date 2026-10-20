// hero16b section D: the Plan, History and the start plan say plain words, and the counts agree with each other.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const src = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src');
const req = createRequire(join(src, '..', 'package.json'));
const ts = req('typescript');
const cache = new Map();
function load(name) {
  if (cache.has(name)) return cache.get(name);
  const js = ts.transpileModule(readFileSync(join(src, name + '.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
  const module = { exports: {} };
  cache.set(name, module.exports);
  new Function('exports', 'require', 'module', js)(module.exports, (spec) => spec.startsWith('./') ? load(spec.slice(2)) : req(spec), module);
  return module.exports;
}
const P = load('plan-model');
const H = load('plan-history-model');
const S = load('startplan-model');
const C = load('copy');

test('D1: a row that waits says why in one plain line, beside the task it waits for', () => {
  const item = { id: 'names/x', title: 'Fix names', stateWord: 'waiting', state: 'gap', doer: 'you', waitsFor: [{ id: 'm/1', title: 'Add 2 measures for the numbers in Sales', doer: 'semanticus' }] };
  const sub = P.subLine(item);
  assert.equal(sub.who, 'Waits for Semanticus');
  assert.equal(sub.why, P.WAITS_WHY_PLAIN);
  const row = readFileSync(join(src, 'plan-row.tsx'), 'utf8');
  assert.match(row, /className="pw-why"/);
});

test('D2: the strip and the run button add up, and the fold says how its count splits', () => {
  // "1 ready first" and "12 follow it" make the 13 on Run the plan.
  assert.deepEqual(P.trackCounts({ doer: 'semanticus', done: 0, onIt: 0, ready: 1, needsYou: 0, waiting: 12 }).map((c) => c.text), ['0 done', '0 on it', '1 ready first', '12 follow it']);
  // "3 need you" and "1 waiting" make the 4 in the fold.
  assert.equal(P.onlyYouWords([{ stateWord: 'needs_you' }, { stateWord: 'needs_you' }, { stateWord: 'needs_you' }, { stateWord: 'waiting', waitsFor: [{ id: 'a' }] }]),
    'tasks need your choice (3 ready, 1 waiting)');
  assert.equal(P.onlyYouWords([{ stateWord: 'needs_you' }, { stateWord: 'needs_you' }]), 'tasks need your choice');
});

test('D3: undo steps and the Latest mark use plain words, never the engine working labels', () => {
  assert.equal(C.editWords('edit objects'), 'Changed fields');
  assert.equal(C.editWords('write words'), 'Wrote descriptions and synonyms');
  assert.equal(C.editWords('waive findings'), 'Set findings aside');
  assert.equal(C.editWords('rename'), 'Renamed a field');
  assert.equal(C.editWords('A sentence the engine wrote'), 'A sentence the engine wrote');
  const latest = P.latestLine({ latest: { when: '2026-10-12T01:43:00Z', sentence: 'rename', entryId: 'e1' } });
  assert.equal(latest.sentence, 'Renamed a field');
  assert.equal(H.rowTitle({ kind: 'edit', title: 'edit objects', by: 'assistant' }), 'Changed fields');
});

test('KNOWN 14: a Properties-grid edit row and a baseline compare line read in plain words', () => {
  assert.equal(C.editWords('set Expression'), 'Changed a formula');
  assert.equal(C.editWords('set Description'), 'Wrote a description');
  assert.equal(C.editWords('set IsHidden on 3 objects'), 'Changed whether it is hidden on 3 objects');
  assert.equal(C.editWords('set FooBarBaz'), 'Changed the foo bar baz');
  assert.equal(H.rowTitle({ kind: 'edit', title: 'set Expression', by: 'you' }), 'Changed a formula');
  const rec = (summary) => ({ kind: 'record', title: 'compare_baseline', by: 'agent', record: { op: 'compare_baseline', summary, verdict: 'safe', seq: 1, sessionId: 's', revision: 0 } });
  assert.equal(H.metaLine(rec('SAFE: 1 downstream measure(s) unchanged across the captured grid.')), 'Nothing moved: 1 measure gave the same numbers as before.');
  assert.equal(H.metaLine(rec('SAFE: 4 downstream measure(s) unchanged across the captured grid. NOTE: 2 session edit(s) since capture. The live model reflects them only if deployed (deploy_live); an undeployed local edit is NOT validated by this compare.')),
    'Nothing moved: 4 measures gave the same numbers as before. 2 changes made since the before picture count only once published.');
  assert.equal(H.metaLine(rec('IMPACT: 2 measure(s) moved, 1 missing, of 5 baselined.')), '2 measures of 5 gave different numbers. 1 measure could not be found.');
  assert.doesNotMatch(H.metaLine(rec('SAFE: 1 downstream measure(s) unchanged across the captured grid.')), /SAFE|\(s\)|downstream|grid/);
});

test('D3: a column reads "Margin % in Sales", and a batch of unknown kind is not "objects"', () => {
  assert.equal(H.objectName('column:Sales/Margin %'), 'Margin % in Sales');
  const parent = { kind: 'task', title: 'Tidy' };
  const row = { kind: 'edit', title: 'write words', deltas: [1, 2, 3].map((i) => ({ kind: 'update', ref: `culture:en-US/${i}`, props: ['Annotations'] })) };
  const title = H.rowTitle(row, parent);
  assert.doesNotMatch(title, /objects|annotations/);
  assert.equal(title, '3 languages · notes kept with it');
});

test('D3: the start plan lists only who chose something, and names Semanticus rules as rules', () => {
  const totals = { jev: 0, rule: 59, assistant: 0, you: 0, default: 0 };
  assert.deepEqual(S.chosenByParts(totals, true).map((c) => c.text), ['Semanticus rules 59']);
  assert.deepEqual(S.chosenByParts({ ...totals, jev: 4, you: 2 }, true).map((c) => c.text), ['Jev 4', 'Semanticus rules 59', 'You 2']);
  assert.deepEqual(S.chosenByParts({ ...totals, default: 7 }, false).map((c) => c.text), ['Default 7', 'Semanticus rules 59']);
});

test('D1: "same fields" is said only when the two tasks share a field; otherwise the reason is always true', () => {
  const waiter = { id: 'names/x', title: 'Fix names', stateWord: 'waiting', state: 'gap', doer: 'you', objects: ['column:Sales/Margin'], waitsFor: [{ id: 'm/1', title: 'Add 2 measures', doer: 'semanticus' }] };
  const sharing = { id: 'm/1', title: 'Add 2 measures', objects: ['column:Sales/Margin', 'measure:Sales/Net'] };
  const apart = { id: 'm/1', title: 'Add 2 measures', objects: ['measure:Sales/Net'] };
  assert.equal(P.subLine(waiter, [waiter, sharing]).why, P.WAITS_WHY);
  assert.match(P.WAITS_WHY, /same fields/);
  assert.equal(P.subLine(waiter, [waiter, apart]).why, P.WAITS_WHY_PLAIN);
  assert.equal(P.subLine(waiter).why, P.WAITS_WHY_PLAIN);
  assert.equal(P.WAITS_WHY_PLAIN, "it needs that task's changes first");
  assert.doesNotMatch(P.WAITS_WHY_PLAIN, /same fields/);
});

test('History words a publish and its undo without the service address', () => {
  const push = { op: 'apply_model_diff', verdict: 'deployed', summary: 'pushed 2 change(s) to powerbi://api.powerbi.com/v1.0/myorg/Contoso WS/Northstar Sales Working' };
  assert.equal(H.recordWords(push), 'Sent 2 changes to Northstar Sales Working.');
  const back = { op: 'rollback_push', verdict: 'deployed', summary: 'rolled powerbi://api.powerbi.com/v1.0/myorg/Contoso WS/Northstar Sales Working back to restore point abc123: restored 1, removed 1' };
  assert.equal(H.recordWords(back), 'Northstar Sales Working went back to how it was. Put back 1, removed 1.');
  const partial = { op: 'rollback_push', verdict: 'needs-review', summary: 'rolled powerbi://x/y/Sales back to restore point abc123: restored 0, removed 1, could not restore 1' };
  assert.match(H.recordWords(partial), /^Sales did not fully go back: 1 object could not be put back\./);
  assert.equal(H.verifiedWord(partial.verdict).word, 'Needs review', 'a rollback that left something behind is not Verified');
  for (const r of [push, back, partial]) assert.doesNotMatch(H.recordWords(r), /powerbi:|rolled |restore point/);
});
