// fx34 (rc33 tester, gap 1): after an edit that doubled Total Quantity, Trusted number, Filters, Contexts and Totals kept their
// pre-edit passes, so every screen read "Verified" with the old number. The engine now sends each number mark from before the
// edit as Unknown with "Your edit changed this measure. This result is from before the edit." (reason code "stale") until its
// rerun replaces it. Every screen reads the marks model: the tree row and hover (treeRowItem), the card (cardVerdict) and a
// mark's detail (detailSentence). None may say Verified or show the old number (finish line C3).
import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const m = require(join(root, 'out', 'marksModel.js'));

const STALE = 'Your edit changed this measure. This result is from before the edit.';
const stale = (name) => ({ name, state: 'not run', sentence: STALE, reasonCode: 'stale', applies: true, verdict: 'unknown', lastRan: '2026-10-29T13:32:29Z', cells: [] });
const MARKS = [stale('Filters'), stale('Contexts'), stale('Trusted number'), stale('Totals'),
  { name: 'Equivalence', state: 'suspect', sentence: 'Your edit moved numbers in 6 places.', applies: true, verdict: 'review', reasonCode: 'kept',
    cells: [{ context: 'Grand total', before: '628370', after: '1256740', beforeText: '628,370', afterText: '1,256,740' }] },
  { name: 'SQL', state: 'not run', sentence: 'No SQL check is saved for this measure.', reasonCode: 'no_sql', applies: false, verdict: null }];
// After Keep the engine hides Equivalence ("kept" does not apply).
const KEPT = MARKS.map(x => x.name === 'Equivalence' ? { ...x, applies: false, verdict: null } : x);

test('fx34 gap 1: after Keep the tree row and hover of an edited measure say Unknown, never Verified', () => {
  const row = m.treeRowItem('Total Quantity', KEPT, { checked: true });
  assert.equal(row.state, 'unknown');
  assert.match(row.label, /^Total Quantity, Unknown: your edit changed this measure/i);
  assert.doesNotMatch(row.tooltip, /Verified|628,370/);
});

test('fx34 gap 1: after Keep the card leads with Unknown and one line, not "Verified by 5 checks"', () => {
  const card = m.cardVerdict(KEPT, null, { checked: true });
  assert.equal(card.state, 'unknown');
  assert.match(card.line, /^○ Unknown: your edit changed this measure, so it is checked again/i);
  assert.doesNotMatch(card.line, /Verified|could not run|\+\d more/);
});

test('fx34 gap 1: each stale mark detail is the line itself, with no old number', () => {
  for (const mark of MARKS.slice(0, 4)) {
    assert.equal(m.verdictOf(mark), 'unknown', mark.name);
    assert.equal(m.detailSentence(mark, 'unknown'), STALE, mark.name);
  }
});
