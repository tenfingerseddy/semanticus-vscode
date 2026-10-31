// n171 (rc34 and rc35 tester Next lists, for 1.7.1). Each test runs the real webview component in the hook runtime.
// rc35 Next 1: after a restart, the Properties card read "Not checked yet: no run has included it" for about 3 s while the tree
// row showed the mark, because the card had no state of its own until its first reads answered. The card holds its layout with an
// empty verdict line until the marks load (d1 spec 4.4: "Reading the marks." is cut; hold the layout while it loads).
// rc34 Next 4: "Saved. Checking it now." stayed under Add a check for 30 minutes and more, long after the check ended.
// rc34 Next 5: the Model page in Properties showed the plan with an active Run while a run was going on the Checks page. The
// engine's check-state push now says when a run is going on, so every run plan shows it.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const find = (view, id) => elements(view.result).find(e => e.props?.['data-testid'] === id);
const button = (view, label) => elements(view.result).find(e => e.type === 'button' && [e.props.children].flat().join('') === label);
const ref = 'measure:Sales/Gross Profit';
const ran = new Date().toISOString();
const SAVED = [
  { name: 'Totals', state: 'passed', sentence: 'Total adds up as a sum.', lastRan: ran, applies: true, verdict: 'verified', total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: false } },
  { name: 'Filters', state: 'passed', sentence: 'No ignored filter.', lastRan: ran, applies: true, verdict: 'verified' },
  { name: 'Contexts', state: 'passed', sentence: 'Values in all places.', lastRan: ran, applies: true, verdict: 'verified' },
];

test('rc35 Next 1: before the marks load, the card holds an empty verdict line, never "Not checked yet"', async () => {
  const runtime = makeHookRuntime();
  const answers = [];
  // After a restart the engine reads the saved states first: both reads wait.
  const host = { rpc: method => ['getMeasureMarks', 'getMeasureCheckCache'].includes(method) ? new Promise(r => answers.push({ method, r })) : new Promise(() => {}),
    onChange: () => () => {}, onCheckState: () => () => {} };
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference: ref }));
  const face = () => elements(view.result).find(e => typeof e.props?.onMark === 'function');
  try {
    await settle();
    assert.equal(face().props.verdict.line, '');
    assert.equal(face().props.verdict.state, null);
    assert.doesNotMatch(face().props.verdict.line, /Not checked yet/);
    assert.ok(face().props.add, 'Add a check keeps its place while the marks load (FX172)');
    for (const a of answers) a.r(a.method === 'getMeasureMarks'
      ? { measureRef: ref, revision: 1, running: false, version: 4, lastChecked: ran, marks: SAVED }
      : [{ measureRef: ref, version: 4, lastChecked: ran, marks: SAVED }]);
    await settle(); await settle();
    assert.match(face().props.verdict.line, /^✓ Verified by 3 checks/);
  } finally { runtime.unmount(); }
});

test('rc35 Next 1: a measure that was never checked still reads "Not checked yet" once its marks load', async () => {
  const runtime = makeHookRuntime();
  const host = { rpc: async method => method === 'getMeasureMarks' ? { measureRef: ref, revision: 1, running: false, marks: [] }
    : method === 'getMeasureCheckCache' ? [] : null, onChange: () => () => {}, onCheckState: () => () => {} };
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference: ref }));
  try {
    await settle(); await settle();
    const face = elements(view.result).find(e => typeof e.props?.onMark === 'function');
    assert.match(face.props.verdict.line, /^Not checked yet/);
  } finally { runtime.unmount(); }
});

test('rc34 Next 4: "Saved. Checking it now." goes when the check of the saved number ends', async () => {
  const runtime = makeHookRuntime();
  const host = { rpc: async () => ({}), onChange: () => () => {}, open() {} };
  const { AddCheck } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let props = { reference: 'measure:Sales/Total Quantity', marks: SAVED, running: false, onRecheck() {} };
  const view = runtime.renderHook(() => AddCheck(props));
  try {
    button(view, 'Add a check').props.onClick();
    button(view, 'Save a number you trust').props.onClick();
    elements(view.result).find(e => e.type === 'input').props.onChange({ target: { value: '628370' } });
    await button(view, 'Save and run').props.onClick();
    await settle();
    assert.equal(find(view, 'mk-add-note')?.props.children, 'Saved. Checking it now.');
    props = { ...props, running: true }; view.rerender();
    assert.equal(find(view, 'mk-add-note')?.props.children, 'Saved. Checking it now.', 'the line stays while the check runs');
    const trusted = { name: 'Trusted number', state: 'passed', sentence: 'Same as your number.', lastRan: new Date(Date.now() + 1000).toISOString(), applies: true, verdict: 'verified' };
    props = { ...props, running: false, marks: [...SAVED, trusted] }; view.rerender();
    assert.equal(find(view, 'mk-add-note'), undefined, 'the line outlived the check');
  } finally { runtime.unmount(); }
});

test('rc34 Next 4: the line also goes when the check ends with no new mark (it could not run)', async () => {
  const runtime = makeHookRuntime();
  const host = { rpc: async () => ({}), onChange: () => () => {}, open() {} };
  const { AddCheck } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let props = { reference: 'measure:Sales/Total Quantity', marks: SAVED, running: false, onRecheck() {} };
  const view = runtime.renderHook(() => AddCheck(props));
  try {
    button(view, 'Add a check').props.onClick();
    button(view, 'Save a number you trust').props.onClick();
    elements(view.result).find(e => e.type === 'input').props.onChange({ target: { value: '1' } });
    await button(view, 'Save and run').props.onClick();
    await settle();
    props = { ...props, running: true }; view.rerender();
    props = { ...props, running: false }; view.rerender();
    assert.equal(find(view, 'mk-add-note'), undefined);
  } finally { runtime.unmount(); }
});

test('rc34 Next 5: a run plan shows a run that another screen started, with no active Run, and the plan again after it', async () => {
  const runtime = makeHookRuntime();
  const pushes = new Set();
  let plans = 0;
  const plan = { id: 'p1', totalMeasures: 12, batchSize: 40, estimatedSeconds: 240, sentence: 'Checking 12 of 12: 3 core, 9 more. About 4 minutes.',
    picks: Array.from({ length: 12 }, (_, i) => ({ measureRef: `measure:Sales/M${i}`, group: 'core', reason: 'Used by 2 other measures.' })) };
  const host = { rpc: async method => method === 'planMeasureRun' ? (plans++, plan) : null, onChange: () => () => {},
    onCheckState: fn => { pushes.add(fn); return () => pushes.delete(fn); } };
  const { RunPlan } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./run-plan.tsx');
  const view = runtime.renderHook(() => RunPlan({ measures: [] }));
  const line = () => elements(view.result).find(e => typeof e.props?.onRun === 'function')?.props;
  const push = change => pushes.forEach(fn => fn(change));
  try {
    await settle();
    assert.equal(line().running, false);
    assert.equal(line().canRun, true);
    // A push from an engine that does not say (no run field) changes nothing.
    push({ sessionId: 's', version: 8, states: [] });
    assert.equal(line().running, false);
    // The Checks page pressed Run: the engine pushes the run with each state it writes.
    push({ sessionId: 's', version: 9, states: [], run: { active: true, done: 0, total: 12 } });
    assert.equal(line().running, true, 'Properties keeps an active Run during the run');
    assert.equal(line().total, 12);
    push({ sessionId: 's', version: 10, states: [{ measureRef: 'measure:Sales/M0', version: 10, lastChecked: new Date().toISOString(), marks: [] }], run: { active: true, done: 5, total: 12 } });
    assert.equal(line().done, 5);
    const before = plans;
    push({ sessionId: 's', version: 11, states: [], run: { active: false, done: 0, total: 0 } });
    await settle();
    assert.equal(line().running, false, 'the run ended');
    assert.ok(plans > before, 'the plan is made again after the run');
  } finally { runtime.unmount(); }
});

// rc34 Next 2: the Checks list clipped a row's More menu, so Turn off and Delete could not be pressed. The list must not clip;
// tools/uishot/n171-drive.mjs presses-tests every item of the open menu in a real browser.
test('rc34 Next 2: the Checks list does not clip what opens from a row', () => {
  const css = readFileSync(new URL('../webview/src/checks-page.css', import.meta.url), 'utf8');
  const list = /\.cp-list\s*\{([^}]*)\}/.exec(css)?.[1] ?? '';
  assert.ok(list, 'the .cp-list rule');
  assert.doesNotMatch(list, /overflow\s*:\s*(hidden|clip|auto|scroll)/);
});
