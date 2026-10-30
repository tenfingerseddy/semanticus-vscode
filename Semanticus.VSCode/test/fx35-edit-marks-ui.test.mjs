// fx35 (rc34 tester). Gap 1: during the rerun after an edit, the Properties card's Totals detail read "Not scored yet. This score
// turns on when Jev scoring is released." with the licence and Pro advice on. The rc34 engine sent the stale Totals with that line;
// the engine now keeps the stale line, and a stale mark's detail is the stale line whatever text came with it (finish line B2).
// Gap 2: an edit kept, undone and made again (here during a run) showed no Equivalence on any screen, because the old Keep still
// hid it in the engine. With the engine fix the mark applies; every screen then shows Equivalence with Before and After, and its
// detail offers Keep and Undo my edit (finish line C3, E2). The card, the tree and the Checks rows all read this marks model.
import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const m = require(join(root, 'out', 'marksModel.js'));

const STALE = 'Your edit changed this measure. This result is from before the edit.';
const RELEASED = 'Not scored yet. This score turns on when Jev scoring is released.';

test('fx35 gap 1: a stale Totals detail reads the stale line, never the released line (as the rc34 engine sent it)', () => {
  const totals = { name: 'Totals', state: 'not run', sentence: RELEASED, reasonCode: 'stale', applies: true, verdict: 'unknown', pending: true, cells: [] };
  const state = m.verdictOf(totals);
  assert.equal(state, 'unknown');
  assert.equal(m.detailSentence(totals, state), STALE);
  assert.doesNotMatch(m.detailSentence(totals, state), /released/);
});

test('fx35 gap 1: the engine stale line stays as it is on every stale mark', () => {
  for (const name of ['Totals', 'Filters', 'Contexts', 'Trusted number']) {
    const mark = { name, state: 'not run', sentence: STALE, reasonCode: 'stale', applies: true, verdict: 'unknown', cells: [] };
    assert.equal(m.detailSentence(mark, 'unknown'), STALE, name);
  }
});

// The marks of Total Quantity after the edit made during a run, as both doors now read them (engine CheckedFx35Tests).
const EQUIVALENCE = { name: 'Equivalence', state: 'suspect', sentence: 'Your edit moved numbers in 6 places.', applies: true, verdict: 'review',
  inputs: 'A67DA9DF', cells: [{ context: 'Grand total', before: '628370', after: '1256740', beforeText: '628,370', afterText: '1,256,740' }] };
const AFTER_EDIT = [
  { name: 'Filters', state: 'passed', sentence: 'No ignored filter in 157 places.', applies: true, verdict: 'verified', cells: [] },
  { name: 'Contexts', state: 'passed', sentence: 'Values in all 173 places.', applies: true, verdict: 'verified', cells: [] },
  { name: 'SQL', state: 'not run', sentence: 'No SQL check is saved for this measure.', reasonCode: 'no_sql', applies: false, verdict: null },
  { name: 'Trusted number', state: 'suspect', sentence: 'The measure differs from a number you trust.', applies: true, verdict: 'broken',
    cells: [{ context: 'Grand total', before: '628370', after: '1256740', beforeText: '628,370', afterText: '1,256,740' }] },
  EQUIVALENCE,
  { name: 'Totals', state: 'passed', sentence: 'Total adds up as a sum, as expected.', applies: true, verdict: 'verified', cells: [] }];

test('fx35 gap 2: the card and the tree count Equivalence after an edit made during a run', () => {
  const card = m.cardVerdict(AFTER_EDIT, null, { checked: true });
  assert.equal(card.state, 'broken');
  assert.equal(card.more, 1, card.line);
  assert.match(card.line, /\(\+1 more\)$/);
  const row = m.treeRowItem('Total Quantity', AFTER_EDIT, { checked: true });
  assert.match(row.label, /^Total Quantity, Broken: 628,370 higher than your number \(\+1 more\)/);
  assert.match(row.tooltip, /Equivalence/);
  const shown = m.shownMarks(AFTER_EDIT, true).map(x => `${x.mark.name}:${x.state}`);
  assert.ok(shown.includes('Equivalence:review'), shown.join(' '));
});

test('fx35 gap 2: the Equivalence detail is a review with its moved numbers', () => {
  assert.equal(m.verdictOf(EQUIVALENCE), 'review');
  assert.match(m.detailSentence(EQUIVALENCE, 'review'), /moved numbers/i);
});

test('fx35 gap 2: only a Keep of this edit hides Equivalence', () => {
  const kept = AFTER_EDIT.map(x => x.name === 'Equivalence' ? { ...x, reasonCode: 'kept', applies: false, verdict: null } : x);
  assert.ok(!m.shownMarks(kept, true).some(x => x.mark.name === 'Equivalence'));
  assert.equal(m.cardVerdict(kept, null, { checked: true }).more, 0);
});
