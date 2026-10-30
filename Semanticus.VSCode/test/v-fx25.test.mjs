import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

// v-fx25: defects the verifier found in the fx25 build of the d1 Checks design (N1 and N2 recheck). Each test feeds the
// marks model the shape the engine really sends, and holds the d1 spec rule the build breaks. These tests fail on 22b9bfa9c.
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');

const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const earlier = '2026-10-28T06:44:00.0000000Z';
// The engine's Keep rule is MeasureCheckVerdict.Finish (pinned case by case in CheckedVRc28Tests.The_engine_Keep_rule_...):
// a kept Equivalence leaves the engine as not applying (reason kept), any other one applies. These are the two shapes it sends.
const engineSends = (eq, kept) => kept ? { ...eq, reasonCode: 'kept', applies: false, verdict: null } : { ...eq, applies: true, verdict: 'review' };
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];

test('v-fx25 1: an old Keep (saved before fx25) ends when a later compare of the same edit ran on a changed used measure (d1 s3)', () => {
  // [Margin] = [Profit] / [Sales]. Before fx25, Kane kept his own [Margin] edit X -> Y. That Keep has formulas and no inputs,
  // and the engine's rule never lets a Keep with no inputs hide a mark.
  const X = 'DIVIDE ( [Profit], [Sales] )', Y = 'DIVIDE ( [Profit], [Sales] + 0 )';
  // On fx25, Kane sets [Margin] back to X, edits [Profit], then sets [Margin] to Y again. The last compare is X -> Y on the
  // new [Profit] body (new inputs), after the Keep, and it moves 3 numbers Kane never saw.
  const moved = engineSends(mark('Equivalence', 'suspect', 'This change moved these numbers.', { lastRan: '2026-10-28T07:10:00.0000000Z',
    beforeFormula: X, afterFormula: Y, inputs: 'b2c4e1',
    cells: ['Grand total', 'Year: 2025', 'Year: 2026'].map((context, i) => ({ context, before: String(10 + i), after: String(20 + i) })) }), false);
  const marks = [
    mark('Filters', 'passed', 'No ignored filter in 40 places checked.', { lastRan: earlier }),
    mark('Contexts', 'passed', 'Values in all 5 places checked.', { lastRan: earlier }),
    moved,
  ];
  const line = m.cardVerdict(marks, null, { checked: true }).line;
  assert.equal(m.shownMarks(marks, true).some(x => x.mark.name === 'Equivalence'), true, 'an old Keep hides a compare that ran after it, on a changed [Profit]');
  assert.match(line, /^▲ Needs review: your edit moved/, `a later compare moved [Margin] and every surface reads "${line}"`);
});

test('v-fx25 2: a card moved to another measure during a run shows that measure\'s last known marks, not the first one\'s (d1 s2 Checking)', async () => {
  // Run plan: Run is checking every measure. Kane opens Total Sales (its trusted number holds, ✓), then at once opens
  // Total Cost (its trusted number differs, the row shows ✗). The Total Sales cache read answers after the click and
  // before the Total Cost marks read.
  const view = ref => ({ measureRef: ref, revision: 3, running: true, lastChecked: earlier, marks: [
    mark('Filters', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
    mark('Contexts', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
    mark('SQL', 'not run', 'No SQL check is saved for this measure.', { lastRan: earlier }),
    mark('Trusted number', 'not run', 'No trusted number is saved for this measure.', { lastRan: earlier }),
    mark('Equivalence', 'not run', 'No edit yet.', { lastRan: earlier }),
  ] });
  const sales = 'measure:Sales/Total Sales', cost = 'measure:Sales/Total Cost';
  const withTrusted = (ref, trusted) => ({ measureRef: ref, lastChecked: earlier, marks: view(ref).marks.map(x => x.name === 'Trusted number' ? trusted : x) });
  const cache = [
    withTrusted(sales, mark('Trusted number', 'passed', 'The measure matches your number.', { lastRan: earlier })),
    withTrusted(cost, mark('Trusted number', 'suspect', 'The measure is 5 lower than your number.', { lastRan: earlier })),
  ];
  assert.equal(m.shownMarks(cache[1].marks, true).find(x => x.mark.name === 'Trusted number')?.state, 'broken',
    'the Total Cost row and the tree read ✗ from the check cache');

  let releaseFirst, releaseCost; let cacheReads = 0;
  const host = {
    rpc: async (method, ref) => {
      if (method === 'getMeasureMarks') return ref === cost ? new Promise(r => { releaseCost = () => r(view(ref)); }) : view(ref);
      if (method === 'getMeasureCheckCache') return ++cacheReads === 1 ? new Promise(r => { releaseFirst = () => r(cache); }) : cache;
      return null;
    },
    onChange: () => () => {},
  };
  const runtime = makeHookRuntime();
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let reference = sales;
  const card = runtime.renderHook(() => MeasureMarksPanel({ reference, name: 'card' }));
  try {
    await settle();
    assert.equal(typeof releaseFirst, 'function', 'the Total Sales card asked for the check cache');
    reference = cost; card.rerender();
    releaseFirst(); await settle();
    releaseCost(); await settle(); await settle();
    const face = elements(card.result).find(e => typeof e.props?.onMark === 'function');
    const shown = (face?.props.marks ?? []).map(x => `${x.name}:${x.state}`);
    assert.ok(shown.includes('Trusted number:broken'), `the Total Cost row reads ✗ but its open card shows ${shown.join(', ') || 'no marks'}`);
  } finally { runtime.unmount(); }
});
