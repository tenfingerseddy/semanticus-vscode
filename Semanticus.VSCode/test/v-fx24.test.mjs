import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

// v-fx24: defects the verifier found in the fx24 build of the d1 Checks design. Each test feeds the marks model the
// shape the engine really sends, and holds the d1 spec rule the build breaks. These tests fail on 74069e24c.
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');

const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const earlier = '2026-10-28T06:44:00.0000000Z';
// The engine's Keep rule is MeasureCheckVerdict.Finish (pinned case by case in CheckedVRc28Tests.The_engine_Keep_rule_...):
// a kept Equivalence leaves the engine as not applying (reason kept), any other one applies. These are the two shapes it sends.
const engineSends = (eq, kept) => kept ? { ...eq, reasonCode: 'kept', applies: false, verdict: null } : { ...eq, applies: true, verdict: 'review' };
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];

test('v-fx24 1: after Keep on an edit of a measure it uses, the next edit of that measure shows Equivalence again (d1 s3)', () => {
  // [Margin] = [Profit] / [Sales]. Kane edits [Profit]. The commit hook (LocalEngine.MeasureMarkRunner.cs AfterCommit) starts a
  // compare run for [Margin] too. CompareMarkEditAsync stores [Margin]'s own body as BeforeFormula and AfterFormula, and that
  // body did not change, so both read the same text. Kane presses Keep: the engine keeps the inputs it compared on (I-profit-1).
  const body = 'DIVIDE ( [Profit], [Sales] )';
  // Kane edits [Profit] again. A new compare run of [Margin] moves 6 numbers, after the Keep. Its formulas read the same text.
  // The [Profit] edit changed [Margin]'s inputs (I-profit-2), so the Keep of I-profit-1 does not hold and the engine sends it applying.
  const moved = engineSends(mark('Equivalence', 'suspect', 'This change moved these numbers.', { lastRan: '2026-10-28T07:05:00.0000000Z',
    beforeFormula: body, afterFormula: body, inputs: 'I-profit-2',
    cells: ['Grand total', 'Year: 2025', 'Year: 2026'].map((context, i) => ({ context, before: String(10 + i), after: String(20 + i) })) }), false);
  const marks = [
    mark('Filters', 'passed', 'No ignored filter in 40 places checked.', { lastRan: earlier }),
    mark('Contexts', 'passed', 'Values in all 5 places checked.', { lastRan: earlier }),
    moved,
  ];
  const line = m.cardVerdict(marks, null, { checked: true }).line;
  assert.equal(m.shownMarks(marks, true).some(x => x.mark.name === 'Equivalence'), true, 'Keep of the first [Profit] edit hides the Equivalence mark of the second [Profit] edit');
  assert.match(line, /^▲ Needs review: your edit moved/, `a later edit moved [Margin] and every surface reads "${line}"`);
});

test('v-fx24 2: a card opened while its measure is checked again keeps the Broken mark the Checks row shows (d1 s2 Checking)', async () => {
  // Run plan: Run is checking every measure. Kane opens Total Cost, whose trusted number differs (the row shows ✗).
  // getMeasureMarks answers the run's view (StartMarkRun: EmptyMarks, LastRan copied from the last run); the check cache
  // (GetMeasureCheckCacheAsync, what the row and the tree read) holds the last known result of each mark not reached yet.
  const engineView = { revision: 3, running: true, lastChecked: earlier, marks: [
    mark('Filters', 'passed', 'No ignored filter in 157 places checked.', { lastRan: new Date().toISOString() }),
    mark('Contexts', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
    mark('SQL', 'not run', 'No SQL check is saved for this measure.', { lastRan: earlier }),
    mark('Trusted number', 'not run', 'No trusted number is saved for this measure.', { lastRan: earlier }),
    mark('Equivalence', 'not run', 'No edit yet.', { lastRan: earlier }),
    mark('Totals', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
    mark('Alignment', 'not run', 'Not scored yet.'),
    mark('Performance', 'not run', 'not timed'),
  ] };
  const trusted = mark('Trusted number', 'suspect', 'The measure is 5 lower than your number.', { lastRan: earlier });
  const cache = [{ measureRef: 'measure:Sales/Total Cost', lastChecked: earlier, marks: engineView.marks.map(x => x.name === 'Trusted number' ? trusted : x) }];
  const row = m.shownMarks(cache[0].marks, true).find(x => x.mark.name === 'Trusted number');
  assert.equal(row?.state, 'broken', 'the Checks row and the tree read ✗ from the check cache');

  const runtime = makeHookRuntime();
  const host = {
    rpc: async method => method === 'getMeasureMarks' ? engineView : method === 'getMeasureCheckCache' ? cache : null,
    onChange: () => () => {},
  };
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference: 'measure:Sales/Total Cost', name: 'Total Cost' }));
  try {
    await settle();
    const face = elements(view.result).find(e => typeof e.props?.onMark === 'function');
    const shown = (face?.props.marks ?? []).map(x => `${x.name}:${x.state}`);
    assert.ok(shown.includes('Trusted number:broken'), `the row reads ✗ but the open card shows ${shown.join(', ') || 'no marks'}`);
  } finally { runtime.unmount(); }
});
