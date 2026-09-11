import assert from 'node:assert/strict';
import test from 'node:test';
import { workflowLayoutSession } from '../webview/src/workflowlayout.mjs';

const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const layout = (revision, x = 0) => ({ name: 'sample', revision, positions: { 'step-1': { x, y: 30 } } });
function fixture() {
  const calls = [], applied = [], errors = [];
  let listener;
  const session = workflowLayoutSession({ name: 'sample',
    rpc(method, ...params) { const reply = deferred(); calls.push({ method, params, ...reply }); return reply.promise; },
    subscribe(fn) { listener = fn; return () => { listener = undefined; }; },
    apply: p => applied.push(p), error: e => errors.push(e) });
  return { session, calls, applied, errors, emit: v => listener?.(v) };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('initial reads use positional bridge arguments; disposal fences a late selection reply', async () => {
  const f = fixture();
  assert.deepEqual(f.calls[0].params, ['sample']);
  f.session.dispose(); f.calls[0].resolve(layout('one'));
  await f.session.ready;
  assert.deepEqual(f.applied, []);
});

test('a newer notification wins over the initial read and other workflows are ignored', async () => {
  const f = fixture();
  f.emit({ ...layout('other'), name: 'other' });
  f.emit(layout('two', 20)); f.calls[0].resolve(layout('one'));
  await f.session.ready;
  assert.deepEqual(f.applied, [layout('two', 20).positions]);
  f.emit(layout('three', 30));
  assert.deepEqual(f.applied.at(-1), layout('three', 30).positions);
  f.session.dispose();
});

test('a save requested during initial load retains its revision and serializes later writes', async () => {
  const f = fixture();
  const first = f.session.save(layout('', 10).positions);
  const second = f.session.save(layout('', 20).positions);
  f.calls[0].resolve(layout('one')); await tick();
  assert.equal(f.calls.length, 2);
  assert.deepEqual(f.calls[1].params, ['sample', layout('', 10).positions, 'one']);
  f.calls[1].resolve(layout('two', 10)); await first; await tick();
  assert.equal(f.calls[2].params[2], 'two');
  f.calls[2].resolve(layout('three', 20)); await second;
  assert.deepEqual(f.applied, []);
  f.session.dispose();
});

test('failed writes retain local positions and block external updates until successful reload', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.emit(layout('external', 99)); f.calls[1].reject(new Error('Workflow or layout changed'));
  await save;
  assert.match(f.errors.at(-1), /changed/);
  f.emit(layout('external', 99)); assert.equal(f.applied.length, 1);
  const reload = f.session.refresh(); await tick(); f.calls[2].reject(new Error('offline')); await reload;
  f.emit(layout('external', 99)); assert.equal(f.applied.length, 1);
  const retry = f.session.refresh(); await tick(); f.calls[3].resolve(layout('external', 99)); await retry;
  assert.equal(f.applied.at(-1)['step-1'].x, 99);
  assert.equal(f.errors.at(-1), ''); f.session.dispose();
});

test('reload waits for pending saves and cannot install an older revision', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  const reload = f.session.refresh(); await tick();
  assert.equal(f.calls.length, 2);
  f.calls[1].resolve(layout('two', 10)); await save; await tick();
  f.calls[2].resolve(layout('three', 30)); await reload;
  const next = f.session.save({}); await tick();
  assert.equal(f.calls[3].params[2], 'three');
  f.calls[3].resolve(layout('four')); await next; f.session.dispose();
});

test('load failures block ordinary saves, but explicit Reset can recover a corrupt layout', async () => {
  const f = fixture(); f.calls[0].reject(new Error('corrupt layout')); await f.session.ready;
  await f.session.save(layout('', 10).positions);
  assert.equal(f.calls.length, 1);
  assert.match(f.errors.at(-1), /Load the shared layout/);
  const reset = f.session.save({}); await tick();
  assert.deepEqual(f.calls[1].params, ['sample', {}, undefined]);
  f.calls[1].resolve(layout('reset')); await reset;
  assert.equal(f.errors.at(-1), ''); f.session.dispose();
});

test('late echoes of own saves cannot undo a newer shared update', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.calls[1].resolve(layout('two', 10)); await save;
  f.emit(layout('external', 99)); f.emit(layout('two', 10));
  assert.equal(f.applied.at(-1)['step-1'].x, 99);
  f.emit(layout('two', 10));
  assert.equal(f.applied.at(-1)['step-1'].x, 10, 'restoring an earlier layout can reuse its content hash');
  f.session.dispose();
});

test('an echo received before the save reply does not hide a later restore of those positions', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.emit(layout('two', 10)); f.calls[1].resolve(layout('two', 10)); await save;
  f.emit(layout('external', 99)); f.emit(layout('two', 10));
  assert.equal(f.applied.at(-1)['step-1'].x, 10); f.session.dispose();
});

test('a foreign layout received after our echo wins when our save reply arrives late', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.emit(layout('two', 10));
  f.emit(layout('external', 99));
  f.calls[1].resolve(layout('two', 10)); await save;
  assert.equal(f.applied.at(-1)['step-1'].x, 99);
  const next = f.session.save(layout('', 100).positions); await tick();
  assert.equal(f.calls[2].params[2], 'external', 'the next drag must fence the shared version now shown');
  f.calls[2].resolve(layout('next', 100)); await next;
  f.session.dispose();
});

test('the newest foreign notification wins even when our echo arrives after the reply', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.emit(layout('external-first', 50)); f.emit(layout('external-last', 99));
  f.calls[1].resolve(layout('two', 10)); await save;
  assert.equal(f.applied.at(-1)['step-1'].x, 99);
  f.emit(layout('two', 10));
  assert.equal(f.applied.at(-1)['step-1'].x, 99, 'our delayed echo cannot undo the foreign save');
  const next = f.session.save({}); await tick();
  assert.equal(f.calls[2].params[2], 'external-last');
  f.calls[2].resolve(layout('reset')); await next; f.session.dispose();
});

test('queued user moves keep their own write fence and conflict instead of overwriting a foreign save', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const first = f.session.save(layout('', 10).positions); await tick();
  const second = f.session.save(layout('', 20).positions);
  f.emit(layout('two', 10)); f.emit(layout('external', 99));
  f.calls[1].resolve(layout('two', 10)); await first; await tick();
  assert.equal(f.applied.length, 1, 'the foreign notification must not replace the queued local drag');
  assert.equal(f.calls[2].params[2], 'two', 'adopting the foreign revision here would allow a silent overwrite');
  f.calls[2].reject(new Error('Workflow or layout changed')); await second;
  f.emit(layout('external', 99)); assert.equal(f.applied.length, 1);
  assert.match(f.errors.at(-1), /changed/);
  const reload = f.session.refresh(); await tick();
  f.calls[3].resolve(layout('external', 99)); await reload;
  assert.equal(f.applied.at(-1)['step-1'].x, 99);
  const next = f.session.save(layout('', 100).positions); await tick();
  assert.equal(f.calls[4].params[2], 'external');
  f.calls[4].resolve(layout('new-local', 100)); await next;
  assert.equal(f.applied.length, 2, 'a successful reload must retire buffered notifications from the failed save');
  f.session.dispose();
});

test('a foreign restore can repeat our hash before our save reply without disappearing', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  f.emit(layout('two', 10)); f.emit(layout('external', 99)); f.emit(layout('two', 10));
  f.calls[1].resolve(layout('two', 10)); await save;
  assert.equal(f.applied.at(-1)['step-1'].x, 10, 'consume only the first matching echo; the final restore is newer');
  f.emit(layout('external', 99)); f.emit(layout('two', 10));
  assert.equal(f.applied.at(-1)['step-1'].x, 10); f.session.dispose();
});

test('notifications preceding our acknowledged echo cannot undo that save', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const save = f.session.save(layout('', 10).positions); await tick();
  // Another client edits and restores the starting hash before our pending write is admitted.
  f.emit(layout('external', 99)); f.emit(layout('one')); f.emit(layout('two', 10));
  f.calls[1].resolve(layout('two', 10)); await save;
  assert.equal(f.applied.length, 1, 'keep the displayed local drag, which supersedes these older notifications');
  const next = f.session.save({}); await tick();
  assert.equal(f.calls[2].params[2], 'two');
  f.calls[2].resolve(layout('reset')); await next; f.session.dispose();
});

test('disposal during a save drops queued writes and late errors', async () => {
  const f = fixture(); f.calls[0].resolve(layout('one')); await f.session.ready;
  const first = f.session.save(layout('', 10).positions); await tick();
  const second = f.session.save(layout('', 20).positions);
  f.session.dispose(); f.calls[1].reject(new Error('late error'));
  await first; await second;
  assert.equal(f.calls.length, 2); assert.equal(f.errors.at(-1), '');
});
