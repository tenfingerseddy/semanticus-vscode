import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

// Scratch attacks for the v-fx26 recheck (N1 and N2). Not committed unless a defect is found.
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const earlier = '2026-10-28T06:44:00.0000000Z';
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const X = 'DIVIDE ( [Profit], [Sales] )', Y = 'DIVIDE ( [Profit], [Sales] + 0 )';
const moved = (inputs, lastRan = '2026-10-28T07:10:00.0000000Z', extra = {}) => mark('Equivalence', 'suspect', 'This change moved these numbers.', {
  lastRan, beforeFormula: X, afterFormula: Y, inputs, cells: [{ context: 'Grand total', before: '10', after: '20' }], ...extra });
// The engine's Keep rule is MeasureCheckVerdict.Finish. CheckedVRc28Tests.The_engine_Keep_rule_... holds each case below
// against it (same inputs keeps; another used-measure edit, an own edit, an old Keep with no inputs or an empty one never keeps).
// Here: every surface reads the mark the engine sends, kept (reason kept, not applying) or applying.
const engineSends = (eq, kept) => kept ? { ...eq, reasonCode: 'kept', applies: false, verdict: null } : { ...eq, applies: true, verdict: 'review' };
const line = eq => m.cardVerdict([mark('Filters', 'passed', 'ok', { lastRan: earlier }), eq], null, { checked: true }).line;

test('N1 a: Keep, edit a used measure one level down, then two levels down, then Undo', () => {
  // The Keep holds inputs I-kept. Each edit below changes the inputs, so the engine sends Equivalence applying.
  for (const inputs of ['I-profit-edited', 'I-cost-under-profit-edited'])
    assert.match(line(engineSends(moved(inputs), false)), /^▲ Needs review/, inputs);
  // Undo back to the kept inputs: the engine sends it kept again, and no surface reads review.
  assert.match(line(engineSends(moved('I-kept'), true)), /^✓ Verified/, 'Undo back to the kept inputs brings the Keep back');
});

test('N1 b: Keep with no used measures (inputs = own body) holds, and ends on an own edit', () => {
  assert.match(line(engineSends(moved('I-own', undefined, { beforeFormula: 'SUM(x)', afterFormula: 'SUM(x)+0' }), true)), /^✓ Verified/);
  assert.match(line(engineSends(moved('I-own2', undefined, { beforeFormula: 'SUM(x)', afterFormula: 'SUM(x)+1' }), false)), /^▲ Needs review/);
});

test('N1 c: an old Keep (before fx25, no inputs) never hides a mark with inputs', () => {
  for (const lastRan of ['2026-10-28T07:10:00.0000000Z', '2026-10-28T06:00:00.0000000Z'])
    assert.match(line(engineSends(moved('b2c4e1', lastRan), false)), /^▲ Needs review/, lastRan);
});

test('N1 d: a Keep with inputs and a moved mark with an empty inputs string', () => {
  // The engine compares the strings: an empty mark inputs never equals a Keep's inputs, so it applies.
  assert.match(line(engineSends(moved(''), false)), /^▲ Needs review/);
});

const runView = ref => ({ measureRef: ref, revision: 3, running: true, lastChecked: earlier, marks: [
  mark('Filters', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
  mark('SQL', 'not run', 'This check is waiting to run.', { lastRan: earlier }),
  mark('Trusted number', 'not run', 'No trusted number is saved for this measure.', { lastRan: earlier }),
  mark('Equivalence', 'not run', 'No edit yet.', { lastRan: earlier }),
] });
const cached = (ref, trusted) => ({ measureRef: ref, lastChecked: earlier, marks: runView(ref).marks.map(x => x.name === 'Trusted number' ? trusted : x) });
const A = 'measure:Sales/Total Sales', B = 'measure:Sales/Total Cost', C = 'measure:Sales/Margin';
const cache = [
  cached(A, mark('Trusted number', 'passed', 'The measure matches your number.', { lastRan: earlier })),
  cached(B, mark('Trusted number', 'suspect', 'The measure is 5 lower than your number.', { lastRan: earlier })),
  { ...cached(C, mark('Trusted number', 'passed', 'The measure matches your number.', { lastRan: earlier })),
    marks: cached(C, mark('Trusted number', 'passed', 'The measure matches your number.', { lastRan: earlier })).marks
      .map(x => x.name === 'SQL' ? mark('SQL', 'suspect', 'The measure is 40 higher than your SQL.', { lastRan: earlier }) : x) },
];

async function cardOn(order, releaseOrder, held = new Set(order.slice(0, -1))) {
  const pending = {}; let n = 0; let releaseLast = () => {}; const last = order.length > 1 ? order.at(-1) : null; let lastReads = 0;
  const host = {
    rpc: async (method, ref) => {
      if (method === 'getMeasureMarks') return ref === last && order.indexOf(ref) === order.length - 1 && lastReads++ === 0
        ? new Promise(r => { releaseLast = () => r(runView(ref)); }) : runView(ref);
      if (method === 'getMeasureCheckCache') { const k = order[n++]; return held.has(k) ? new Promise(r => { pending[k] = () => r(cache); }) : cache; }
      return null;
    },
    onChange: () => () => {},
  };
  const runtime = makeHookRuntime();
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let reference = order[0];
  const card = runtime.renderHook(() => MeasureMarksPanel({ reference, name: 'card' }));
  try {
    await settle();
    for (const r of order.slice(1)) { reference = r; card.rerender(); await settle(); }
    for (const k of releaseOrder) { pending[k]?.(); await settle(); }
    releaseLast(); await settle(); await settle();
    const face = elements(card.result).find(e => typeof e.props?.onMark === 'function');
    return (face?.props.marks ?? []).map(x => `${x.name}:${x.state}${x.checking ? '*' : ''}`);
  } finally { runtime.unmount(); }
}

test('N2 a: open A, then B, then C during a run; A and B cache reads answer late, in either order', async () => {
  for (const rel of [[A, B], [B, A]]) {
    const shown = await cardOn([A, B, C], rel);
    console.log('N2 a', rel.join('>'), shown.join(', '));
    assert.ok(shown.includes('SQL:broken*') && shown.includes('Trusted number:verified*'), `C card shows ${shown.join(', ')}`);
  }
});

test('N2 b: open B (row ✗) during a run after A answered late; B card shows ✗ with the checking cue', async () => {
  const shown = await cardOn([A, B], [A]);
  console.log('N2 b shown', shown.join(', '));
  assert.ok(shown.includes('Trusted number:broken*'), shown.join(', '));
});

test('N2 c: open a Broken measure card during a run (no switch); it keeps ✗ with the checking cue', async () => {
  const shown = await cardOn([C], [], new Set());
  console.log('N2 c shown', shown.join(', '));
  assert.ok(shown.includes('SQL:broken*'), shown.join(', '));
  assert.equal(m.cardVerdict(cache[2].marks, null, { checked: true }).line.startsWith('✗'), true, 'the C row reads ✗');
});

test('N2 d: back to A after B; A gets its own ✓, not B ✗', async () => {
  const shown = await cardOn([A, B, A], [A, B], new Set([A, B]));
  console.log('N2 d shown', shown.join(', '));
  assert.ok(!shown.some(s => s.startsWith('Trusted number:broken')), shown.join(', '));
});
