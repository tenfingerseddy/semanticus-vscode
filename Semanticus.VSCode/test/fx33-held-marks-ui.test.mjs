// fx33 (rc32 tester, gap 1): a column-source change holds the measures that use it. The engine now sends each held mark as
// Unknown with "Publish this change to check it." (reason code "held"). Every screen reads the marks model: the tree row and
// hover (treeRowItem), the Properties and Checks page card (cardVerdict) and a mark's detail (detailSentence). Each must say
// Unknown and show the line, never "Verified" and never a generic "could not run" (spec R8; finish line E2).
import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'package.json'));
const m = require(join(root, 'out', 'marksModel.js'));

const HOLD = 'Publish this change to check it.';
const held = (name) => ({ name, state: 'not run', sentence: HOLD, reasonCode: 'held', applies: true, verdict: 'unknown', lastRan: '2026-10-29T11:06:00Z' });
const MARKS = [held('Filters'), held('Contexts'), held('Totals'),
  { name: 'SQL', state: 'not run', sentence: 'No SQL check is saved for this measure.', reasonCode: 'no_sql', applies: false, verdict: null }];

test('fx33 gap 1: the tree row and hover of a held measure say Unknown with the line', () => {
  const row = m.treeRowItem('Sales Amount', MARKS, { checked: true });
  assert.equal(row.state, 'unknown');
  assert.equal(row.description, m.GLYPH.unknown);
  assert.match(row.label, /^Sales Amount, Unknown: publish this change to check it/i);
  assert.match(row.tooltip.split('\n')[0], /Unknown: publish this change to check it/i);
  assert.doesNotMatch(row.tooltip, /Verified|could not run/);
});

test('fx33 gap 1: the card (Properties and the Checks page) leads with Unknown and the line', () => {
  const card = m.cardVerdict(MARKS, null, { checked: true });
  assert.equal(card.state, 'unknown');
  assert.match(card.line, /^○ Unknown: publish this change to check it/i);
  assert.doesNotMatch(card.line, /could not run|\+\d more/);
});

test('fx33 gap 1: each held mark detail is the line itself, Totals too (not "Checked as a sum")', () => {
  for (const mark of MARKS.slice(0, 3)) {
    assert.equal(m.verdictOf(mark), 'unknown', mark.name);
    assert.equal(m.detailSentence(mark, 'unknown'), HOLD, mark.name);
  }
});
