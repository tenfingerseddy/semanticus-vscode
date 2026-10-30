import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

// v-fx23: defects the verifier found in the fx23 build of the d1 Checks design. Each test feeds the marks model the
// shape the engine really sends, and holds the d1 spec rule the build breaks. These tests fail on 44027669a.
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');

const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const earlier = '2026-10-28T06:44:00.0000000Z';
// The engine's Keep rule is MeasureCheckVerdict.Finish (pinned case by case in CheckedVRc28Tests.The_engine_Keep_rule_...):
// a kept Equivalence leaves the engine as not applying (reason kept), any other one applies. These are the two shapes it sends.
const engineSends = (eq, kept) => kept ? { ...eq, reasonCode: 'kept', applies: false, verdict: null } : { ...eq, applies: true, verdict: 'review' };

// The engine's view while a run is in progress (LocalEngine.MeasureMarkRunner.cs StartMarkRun): every mark starts from
// EmptyMarks("This check is waiting to run."), and LastRan is copied from the previous run of the measure.
const waitingView = () => [
  mark('Filters', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
  mark('Contexts', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
  mark('SQL', 'not run', 'No SQL check is saved for this measure.'),
  mark('Trusted number', 'not run', 'No trusted number is saved for this measure.'),
  mark('Equivalence', 'not run', 'No edit yet.'),
  mark('Totals', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.', { lastRan: earlier }),
  mark('Alignment', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.'),
  mark('Performance', 'not run', 'not timed'),
];

test('v-fx23 1: a mark that is waiting in a run is not Unknown (d1 s2: Checking is not a verdict; Unknown is a mark that could not run)', () => {
  const waiting = waitingView().find(x => x.name === 'Filters');
  assert.notEqual(m.verdictOf(waiting, true), 'unknown', 'a waiting Filters mark reads ○ Unknown on the card while "Checking now"');
  const shown = m.shownMarks(waitingView(), true).map(x => `${x.mark.name}:${x.state}`);
  assert.deepEqual(shown.filter(s => s.endsWith(':unknown')), [], `the card shows ${shown.join(', ')} during a re-check`);
});

test('v-fx23 2: Keep holds on every surface after an edit of another measure (d1 s7.5; one verdict on every surface)', () => {
  // Kane edits Total Quantity. Equivalence moves numbers. He presses Keep: the engine keeps the inputs it compared on.
  const marks = [
    mark('Filters', 'passed', 'No ignored filter in 157 places checked.', { lastRan: earlier }),
    mark('Contexts', 'passed', 'Values in all 5 places checked.', { lastRan: earlier }),
    engineSends(mark('Equivalence', 'suspect', 'This change moved these numbers.', { lastRan: '2026-10-28T06:49:00.0000000Z', inputs: 'I-quantity',
      cells: ['Grand total', 'City: Aachen', 'City: Aalen'].map((context, i) => ({ context, before: String(10 + i), after: String(20 + i) })) }), true),
  ];
  // He then edits a different measure. Total Quantity's inputs did not change, so the engine still sends the mark as kept,
  // and every surface reads that one mark: the card (cardVerdict), the Checks row (rowVerdict) and the tree (treeRowItem).
  const card = m.cardVerdict(marks, null, { checked: true }).line;
  const row = m.rowVerdict(marks, null, true);
  const tree = m.treeRowItem('Total Quantity', marks, { checked: true });
  assert.equal(row, 'verified', 'the Checks row gives another verdict than the card');
  assert.equal(tree.description, '✓', 'the tree row gives another verdict than the card');
  assert.match(card, /^✓ Verified/, 'the kept edit comes back as Needs review after an unrelated edit');
});

test('v-fx23 3: the Checks header stays within 12 words with a Broken measure and its number (d1 s4.2 budget; the lane\'s own shot 04-broken)', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const page = load('./checks-page.tsx');
  // The 04-broken state of the lane's driver: 1 broken, 1 needs review, 2 unknown, 8 verified, and the trusted number differs.
  const header = renderToStaticMarkup(React.createElement(page.ChecksHeader, {
    verdict: '1 broken · 1 needs review · 2 unknown · 8 verified', problems: '1 number differs', actionable: null,
    tools: React.createElement('button', { 'aria-label': 'More' }, '⋯') }));
  const text = header.replace(/<[^>]*>/g, ' ');
  const count = (text.match(/[A-Za-z0-9][A-Za-z0-9%:.,'’()\-]*/g) ?? []).length;
  assert.ok(count <= 12, `the header is ${count} words: ${text.replace(/\s+/g, ' ').trim()}`);
});
