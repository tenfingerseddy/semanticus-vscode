// FX172: drive the real card and run controls as the rc battery does. The engine replies are delayed deliberately:
// opening Properties does not wait for marks, and its performance plan can take longer than the battery's 500 ms.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const find = (node, id) => elements(node).find(e => e.props?.['data-testid'] === id);
const button = (node, label) => elements(node).find(e => e.type === 'button' && [e.props.children].flat().join('') === label);
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const reference = 'measure:Sales/Total Quantity';
const ran = '2026-10-30T08:00:00Z';
const marks = ['Totals', 'Filters', 'Contexts'].map(name => ({ name, state: 'passed', sentence: 'The check passed.', applies: true, verdict: 'verified', lastRan: ran }));
const timing = { name: 'Performance', state: 'not run', applies: true, pending: true, progress: 'Timing. About 30 seconds.' };
const timed = { name: 'Performance', state: 'passed', applies: true, verdict: 'verified', lastRan: ran, sentence: 'The measure is fast.',
  cells: [{ context: 'Grand total', after: '628370' }],
  timings: [{ context: 'Grand total', medianMs: 20, minMs: 18, maxMs: 22, coldMs: 30 }] };
const plan = includePerformance => ({ id: includePerformance ? 'with-performance' : 'without-performance', includePerformance,
  totalMeasures: 12, batchSize: 40, estimatedSeconds: 240,
  picks: Array.from({ length: 12 }, (_, i) => ({ measureRef: i ? `measure:Sales/M${i}` : reference, group: 'core', reason: 'Used by 2 other measures.' })) });

// Exercise Studio's real request/reply and check-state bridge. Only the engine endpoint is replaced.
function bridgeHost(call) {
  const previous = { window: globalThis.window, acquire: globalThis.acquireVsCodeApi };
  const win = new EventTarget();
  win.setTimeout = setTimeout; win.clearTimeout = clearTimeout;
  const deliver = data => win.dispatchEvent(new MessageEvent('message', { data }));
  globalThis.window = win;
  globalThis.acquireVsCodeApi = () => ({ postMessage: message => {
    if (message.type !== 'rpc') return;
    Promise.resolve().then(() => call(message.method, ...message.params)).then(
      result => deliver({ type: 'rpcResult', id: message.id, result }),
      error => deliver({ type: 'rpcResult', id: message.id, error: error.message }));
  } });
  const bridge = makeLoader({ './pagemodel': { pageModelIdentity: () => 'Contoso.bim' } })('./bridge.ts');
  return {
    host: { rpc: bridge.rpc, onChange: bridge.onDidChange, onCheckState: bridge.onCheckState },
    push: payload => deliver({ type: 'checkState', payload: JSON.parse(JSON.stringify(payload)) }),
    dispose() { bridge.rejectAll('Test ended.'); globalThis.window = previous.window; globalThis.acquireVsCodeApi = previous.acquire; },
  };
}

test('FX172: Properties offers Add a check while its marks load, then Time this measure runs with performance', async () => {
  const runtime = makeHookRuntime();
  const reading = deferred();
  const pushes = new Set();
  const calls = [];
  let enabled = false;
  const host = { rpc: async (method, ...args) => {
    calls.push({ method, args });
    if (method === 'setMeasurePerformance') enabled = true;
    if (method === 'getMeasureMarks') return enabled
      ? { measureRef: reference, version: 8, running: true, lastChecked: ran, marks: [...marks, timing] } : reading.promise;
    if (method === 'getMeasureCheckCache') return reading.promise.then(r => [r]);
    return null;
  }, onChange: () => () => {}, onCheckState: fn => { pushes.add(fn); return () => pushes.delete(fn); } };
  const { MeasureMarksPanel, CardFace } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference }));
  const face = () => elements(view.result).find(e => e.type === CardFace).props;
  const addRuntime = makeHookRuntime();
  const { AddCheck } = makeLoader({ './marks-host': { useMarksHost: () => host } }, addRuntime.React)('./marks-panel.tsx');
  const detailRuntime = makeHookRuntime();
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => host } }, detailRuntime.React)('./marks-panel.tsx');
  try {
    assert.equal(face().verdict.line, '', 'the n171 loading layout stays');
    assert.ok(face().add, 'the battery opens Properties and presses Add a check before the reads answer');
    // Load the same component with its own hook slots, as React gives a child component.
    const child = addRuntime.renderHook(() => AddCheck(face().add.props));
    button(child.result, 'Add a check').props.onClick();
    const time = button(child.result, 'Time this measure');
    assert.ok(time && !time.props.disabled);
    await time.props.onClick();
    await settle();
    assert.deepEqual(calls.find(c => c.method === 'setMeasurePerformance')?.args, [null, reference, true]);
    assert.ok(calls.some(c => c.method === 'getMeasureMarks' && c.args[2] === true && c.args[4] === true));
    assert.match(find(face().notes, 'mk-timing').props.children, /^Timing\. About/);
    pushes.forEach(fn => fn({ states: [{ measureRef: reference, version: 9, running: false, lastChecked: ran, marks: [...marks, timed] }] }));
    face().onMark('Performance');
    const detail = detailRuntime.renderHook(() => MarkCard(face().detail.props));
    assert.equal(elements(detail.result).filter(e => e.type === 'table').length, 1, 'Performance has one table, including when cells are present');
    assert.deepEqual(elements(detail.result).filter(e => e.type === 'th').map(e => e.props.children), ['Where', 'Median', 'Spread', 'Cold']);
    reading.resolve({ measureRef: reference, version: 7, running: false, lastChecked: ran, marks });
    await settle();
    assert.ok(face().marks.some(m => m.name === 'Performance'), 'the initial late read cannot remove the completed timing');
  } finally { detailRuntime.unmount(); addRuntime.unmount(); runtime.unmount(); }
});

test('FX172: after Stop, Include performance and Run wait for the selected plan, show Timing, and Stop settles', async () => {
  const runtime = makeHookRuntime();
  const performancePlan = deferred();
  let completed = deferred();
  const calls = [];
  let performance = false;
  let runCount = 0;
  let version = 10;
  const state = (running, measured) => ({ measureRef: reference, version: ++version, running, lastChecked: ran,
    marks: measured ? [...marks, timing] : marks });
  const wire = bridgeHost(async (method, ...args) => {
    calls.push({ method, args });
    if (method === 'planMeasureRun') return args[0].includePerformance ? performancePlan.promise : plan(false);
    if (method === 'runMeasurePlan') {
      runCount++;
      completed = deferred();
      performance = args[0] === plan(true).id;
      wire.push({ states: [state(true, performance)], run: { active: true, done: 0, total: 12 } });
      return completed.promise;
    }
    if (method === 'stopMeasureRun') {
      wire.push({ states: [state(false, false)], run: { active: false, done: 0, total: 0 } });
      completed.resolve({ stopped: true, results: [] });
    }
    if (method === 'getMeasureMarks') return state(false, false);
    if (method === 'getMeasureCheckCache') return [state(false, false)];
  });
  const host = wire.host;
  const { RunPlan, PlanLine, ChooseBody } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./run-plan.tsx');
  const view = runtime.renderHook(() => RunPlan({ measures: [] }));
  const line = () => PlanLine(elements(view.result).find(e => e.type === PlanLine).props);
  const cardRuntime = makeHookRuntime();
  const { MeasureMarksPanel, CardFace } = makeLoader({ './marks-host': { useMarksHost: () => host } }, cardRuntime.React)('./marks-panel.tsx');
  const card = cardRuntime.renderHook(() => MeasureMarksPanel({ reference }));
  const face = () => elements(card.result).find(e => e.type === CardFace).props;
  try {
    await settle();
    // The first run's Stop pushes idle and n171 requests a replacement plan.
    button(line(), 'Run').props.onClick();
    await settle();
    wire.push({ states: [{ ...state(false, false), lastChecked: new Date().toISOString() }], run: { active: true, done: 1, total: 12 } });
    await settle();
    assert.equal(elements(line()).find(e => e.type === 'progress').props.value, 1);
    button(line(), 'Stop').props.onClick();
    await settle();
    runCount = 0;
    find(line(), 'plan-choose').props.onClick();
    const choose = ChooseBody(elements(view.result).find(e => e.type === ChooseBody).props);
    find(choose, 'plan-performance').props.onChange({ target: { checked: true } });
    await settle();
    // The selected plan still has not answered when the battery presses Run.
    button(line(), 'Run').props.onClick();
    assert.ok(find(line(), 'plan-stop'), 'Stop must remain available while the plan is read');
    assert.equal(elements(line()).find(e => e.type === 'progress').props.value, 0, 'a new run must not show the previous run count while planning');
    await settle();
    assert.equal(runCount, 0, 'Run executed the old plan with Include performance off');
    performancePlan.resolve(plan(true));
    await settle(); await settle();
    assert.equal(performance, true, 'the engine must receive the performance plan');
    assert.equal(runCount, 1);
    const words = find(face().notes, 'mk-timing').props.children;
    assert.match(words, /^Timing\. About/);
    assert.doesNotMatch(words, /\d+ of \d+ runs|Cold|Warm/);
    button(line(), 'Stop').props.onClick();
    await settle();
    assert.equal(find(line(), 'plan-stop'), undefined);
    assert.equal(button(line(), 'Run').props.disabled, false);
    assert.equal(find(face().notes, 'mk-timing'), undefined);
    assert.match(face().verdict.line, /^✓ Verified by 3 checks/);
  } finally { cardRuntime.unmount(); runtime.unmount(); wire.dispose(); }
});

test('FX172: Stop while the performance plan is loading settles immediately and its late reply starts no checks', async () => {
  const runtime = makeHookRuntime();
  const performancePlan = deferred();
  const calls = [];
  const host = { rpc: async (method, ...args) => {
    calls.push(method);
    if (method === 'planMeasureRun') return args[0].includePerformance ? performancePlan.promise : plan(false);
  }, onChange: () => () => {}, onCheckState: () => () => {} };
  const { RunPlan, PlanLine, ChooseBody } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./run-plan.tsx');
  const view = runtime.renderHook(() => RunPlan({ measures: [] }));
  const line = () => PlanLine(elements(view.result).find(e => e.type === PlanLine).props);
  try {
    await settle();
    find(line(), 'plan-choose').props.onClick();
    const choose = ChooseBody(elements(view.result).find(e => e.type === ChooseBody).props);
    find(choose, 'plan-performance').props.onChange({ target: { checked: true } });
    await settle();
    button(line(), 'Run').props.onClick();
    button(line(), 'Stop').props.onClick();
    assert.equal(find(line(), 'plan-stop'), undefined, 'Stop must not wait for a planning reply');
    performancePlan.resolve(plan(true));
    await settle(); await settle();
    assert.equal(calls.filter(m => m === 'runMeasurePlan').length, 0, 'the plan reply started checks after Stop');
    assert.ok(calls.includes('stopMeasureRun'));
  } finally { runtime.unmount(); }
});
