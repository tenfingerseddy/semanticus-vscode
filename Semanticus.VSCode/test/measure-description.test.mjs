import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

const reference = 'measure:Sales/Sales Amount';
const initial = { ref: reference, description: '', oneLine: '', provenance: 'not recorded', match: { label: 'not scored yet' }, proposal: null };
const noChanges = () => () => {};

test('the Description shows absence promptly and sends no keep request', async () => {
  const calls = [];
  const runtime = makeHookRuntime();
  const load = makeLoader({ './bridge': { onDidChange: noChanges, rpc: async (method, ...args) => {
    calls.push([method, ...args]);
    return method === 'getMeasureDescription' ? initial : { state: 'unavailable', message: 'No assistant is connected to this session.' };
  } } }, runtime.React);
  const { useMeasureDescription } = load('measure-description.tsx');
  const hook = runtime.renderHook(() => useMeasureDescription(reference));
  await settle();
  await hook.result.request();
  assert.equal(hook.result.busy, false);
  assert.match(hook.result.note, /No assistant is connected/);
  assert.deepEqual(calls, [['getMeasureDescription', reference], ['draftMeasureDescription', reference]]);
  runtime.unmount();
});

test('a ready draft is a proposal until one keep press', async () => {
  const calls = [];
  let kept = false;
  const ready = { id: 'contoso-proposal', state: 'ready', text: 'Adds the Contoso sales.', message: 'Review the proposal.' };
  const runtime = makeHookRuntime();
  const load = makeLoader({ './bridge': { onDidChange: noChanges, rpc: async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'keepMeasureDescription') { kept = true; return { changed: true }; }
    if (method === 'getMeasureDescription') return kept ? { ...initial, description: ready.text, provenance: 'ai', proposal: { ...ready, state: 'kept' } } : initial;
    return ready;
  } } }, runtime.React);
  const { useMeasureDescription } = load('measure-description.tsx');
  const hook = runtime.renderHook(() => useMeasureDescription(reference));
  await settle();
  await hook.result.request();
  assert.equal(hook.result.view.description, '');
  assert.equal(hook.result.proposal.text, ready.text);
  assert.equal(kept, false);
  await hook.result.keep();
  assert.equal(kept, true);
  assert.equal(calls.filter(c => c[0] === 'keepMeasureDescription').length, 1);
  assert.equal(hook.result.view.provenance, 'ai');
  runtime.unmount();
});

test('an existing Description uses propose and waiting progress ends at give-up', async () => {
  const calls = [];
  const waiting = { id: 'contoso-waiting', state: 'waiting', message: 'Waiting for your assistant.' };
  let reads = 0;
  const runtime = makeHookRuntime();
  const load = makeLoader({ './bridge': { onDidChange: noChanges, rpc: async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getMeasureDescription') return { ...initial, description: 'Contoso sales.', proposal: ++reads === 1 ? null : { ...waiting, state: 'timedOut', message: 'Your assistant did not answer in 60 seconds.' } };
    return waiting;
  } } }, runtime.React);
  const { useMeasureDescription } = load('measure-description.tsx');
  const hook = runtime.renderHook(() => useMeasureDescription(reference));
  await settle();
  await hook.result.request();
  assert.equal(hook.result.proposal.state, 'waiting');
  await new Promise(r => setTimeout(r, 650));
  assert.match(hook.result.note, /did not answer/);
  assert.ok(calls.some(c => c[0] === 'proposeMeasureDescription'));
  runtime.unmount();
});

test('a Description edit from either door refreshes provenance and score', async () => {
  let listener;
  let view = initial;
  const runtime = makeHookRuntime();
  const load = makeLoader({ './bridge': {
    onDidChange: fn => { listener = fn; return () => { listener = null; }; },
    rpc: async () => view,
  } }, runtime.React);
  const { useMeasureDescription } = load('measure-description.tsx');
  const hook = runtime.renderHook(() => useMeasureDescription(reference));
  await settle();
  view = { ...initial, description: 'A Contoso edit.', provenance: 'person', match: { label: 'Matches 95%' } };
  assert.equal(typeof listener, 'function');
  listener({ deltas: [{ ref: reference, props: ['Description'] }] });
  await settle();
  assert.equal(hook.result.view.provenance, 'person');
  assert.equal(hook.result.view.match.label, 'Matches 95%');
  runtime.unmount();
  assert.equal(listener, null);
});
