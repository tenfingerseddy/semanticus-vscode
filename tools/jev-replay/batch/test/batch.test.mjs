// The scorer's own arithmetic, on synthetic results (no key, no network): the 95% bar, the harm rule, and what is left out.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { comparePoint, isNeutral, isOn, scoreResults, toMarkdown, THRESHOLD, BAR } from '../score.mjs';

const arm = (rows, extra = {}) => ({ requests: 1, inputTokens: 100, wallMs: 1000, rows, ...extra });
const row = (key, choice, confidence) => ({ key, choice, confidence });

test('the bar and the threshold are the protocol\'s, fixed before the run', () => {
  assert.equal(BAR, 0.85);
  assert.equal(THRESHOLD, 0.95);
});

test('the neutral option of each point is the one that leaves a line out', () => {
  assert.equal(isNeutral('column.summarize', 'a', 'none'), true);
  assert.equal(isNeutral('column.summarize', 'a', 'sum'), false);
  assert.equal(isNeutral('column.format', 'a', 'text_or_none'), true);
  assert.equal(isNeutral('ai.schema', 'a', 'include'), true);
  assert.equal(isNeutral('ai.schema', 'a', 'exclude'), false);
  assert.equal(isNeutral('measure.folder_and_format', 'm#display_folder', 'none'), true);
  assert.equal(isNeutral('measure.folder_and_format', 'm#display_folder', 'folder:Money'), false);
  assert.equal(isNeutral('measure.folder_and_format', 'm#format_family', 'text_or_none'), true);
  assert.equal(isNeutral('compose.draft', 'k', 'no'), true);
  assert.equal(isNeutral('compose.draft', 'k', 'yes'), false);
});

test('a line is on only when sure and not neutral', () => {
  assert.equal(isOn('ai.schema', row('a', 'exclude', 0.9)), true);
  assert.equal(isOn('ai.schema', row('a', 'exclude', 0.84)), false);
  assert.equal(isOn('ai.schema', row('a', 'include', 0.99)), false);
  assert.equal(isOn('ai.schema', row('a', null, 0)), false);
});

test('twenty answers with one changed is exactly 95% and passes when nothing turned on', () => {
  const oldRows = [], newRows = [];
  for (let i = 0; i < 20; i++) { oldRows.push(row('o' + i, 'include', 0.95)); newRows.push(row('o' + i, i === 0 ? 'exclude' : 'include', i === 0 ? 0.6 : 0.95)); }
  const p = comparePoint('ai.schema', [{ model: 'm', old: arm(oldRows), new: arm(newRows) }]);
  assert.equal(p.compared, 20);
  assert.equal(p.same, 19);
  assert.equal(p.share, 0.95);
  assert.equal(p.harm.length, 0);
  assert.equal(p.verdict, 'PASS');
});

test('a change that turns a left-out line into a ticked one is harm and fails even at 100 answers', () => {
  const oldRows = [], newRows = [];
  for (let i = 0; i < 100; i++) { oldRows.push(row('o' + i, 'include', 0.95)); newRows.push(row('o' + i, i === 0 ? 'exclude' : 'include', 0.95)); }
  const p = comparePoint('ai.schema', [{ model: 'm', old: arm(oldRows), new: arm(newRows) }]);
  assert.equal(p.share, 0.99);
  assert.equal(p.harm.length, 1);
  assert.equal(p.verdict, 'FAIL');
});

test('a line turned off is a change but never harm', () => {
  const p = comparePoint('ai.schema', [{ model: 'm', old: arm([row('a', 'exclude', 0.95), ...Array.from({ length: 30 }, (_, i) => row('x' + i, 'include', 0.9))]),
    new: arm([row('a', 'include', 0.95), ...Array.from({ length: 30 }, (_, i) => row('x' + i, 'include', 0.9))]) }]);
  assert.equal(p.changed.length, 1);
  assert.equal(p.harm.length, 0);
  assert.equal(p.verdict, 'PASS');
});

test('below 95% fails, and an object one arm could not answer is counted out, never as agreement', () => {
  const oldRows = [row('a', 'sum', 0.9), row('b', 'sum', 0.9), row('c', 'none', 0.9)];
  const newRows = [row('a', 'sum', 0.9), row('b', 'none', 0.9), row('c', null, 0)];
  const p = comparePoint('column.summarize', [{ model: 'm', old: arm(oldRows), new: arm(newRows) }]);
  assert.equal(p.compared, 2);
  assert.equal(p.unanswered, 1);
  assert.equal(p.share, 0.5);
  assert.equal(p.verdict, 'FAIL');
});

test('requests, tokens and wall time are summed over the models', () => {
  const p = comparePoint('column.format', [
    { model: 'a', old: arm([row('x', 'percent', 0.9)], { requests: 10, inputTokens: 1000, wallMs: 2000 }), new: arm([row('x', 'percent', 0.9)], { requests: 2, inputTokens: 400, wallMs: 500 }) },
    { model: 'b', old: arm([row('x', 'percent', 0.9)], { requests: 5, inputTokens: 500, wallMs: 1000 }), new: arm([row('x', 'percent', 0.9)], { requests: 1, inputTokens: 200, wallMs: 300 }) },
  ]);
  assert.deepEqual(p.requests, { old: 15, new: 3 });
  assert.deepEqual(p.tokens, { old: 1500, new: 600 });
  assert.deepEqual(p.wallMs, { old: 3000, new: 800 });
});

test('the markdown lists every changed answer and marks harm', () => {
  const points = scoreResults({ models: [{ id: 'm', points: { 'ai.schema': { old: arm([row('a', 'include', 0.95)]), new: arm([row('a', 'exclude', 0.95)]) } } }] });
  const md = toMarkdown(points);
  assert.match(md, /ai\.schema/);
  assert.match(md, /- \*m: a: include \(0\.95\) to exclude \(0\.95\)/);
});
