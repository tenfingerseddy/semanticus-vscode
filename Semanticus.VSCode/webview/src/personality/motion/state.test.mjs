import { test } from 'node:test';
import assert from 'node:assert/strict';
import { motionPolicy, countAt, gradeFrames, settleChoices, isBigWin, runFrames, historyTransition } from './state.mjs';

test('motion is On and sound is Off by default', () => {
  const policy = motionPolicy();
  assert.equal(policy.moving, true); assert.equal(policy.sound, false); assert.equal(policy.sparkle, true);
});
test('Quiet keeps a shorter useful animation and removes sound and sparkle', () => {
  const quiet = motionPolicy({ personality: 'quiet', sound: true });
  assert.equal(quiet.moving, true); assert.equal(quiet.duration, 160);
  assert.equal(quiet.sparkle, false); assert.equal(quiet.sound, false);
});
test('reduced motion has the same final value without a clock', () => {
  const reduced = motionPolicy({ reducedMotion: true });
  assert.equal(reduced.duration, 0); assert.equal(reduced.moving, false); assert.equal(reduced.sparkle, false);
});
test('hidden surfaces have no motion, sparkle or sound', () => {
  const hidden = motionPolicy({ visible: false, sound: true });
  assert.equal(hidden.duration, 0); assert.equal(hidden.sound, false); assert.equal(hidden.sparkle, false);
});
test('a bulk count reaches the real count and is monotonic', () => {
  const frames = Array.from({ length: 101 }, (_, i) => countAt(0, 885, i / 100));
  assert.equal(frames[0], 0); assert.equal(frames.at(-1), 885);
  assert.ok(frames.every((v, i) => i === 0 || v >= frames[i - 1]));
});
test('undo counts down and a new counter cannot exceed its target', () => {
  assert.equal(countAt(42, 37, 1), 37); assert.equal(countAt(42, 37, 0), 42);
  assert.equal(countAt(0, 885, 2), 885); assert.equal(countAt(0, 885, -1), 0);
});
test('grade flips in both directions and preserves unknown engine grades', () => {
  assert.deepEqual(gradeFrames('D', 'A'), ['D', 'C', 'B', 'A']);
  assert.deepEqual(gradeFrames('A', 'D'), ['A', 'B', 'C', 'D']);
  assert.deepEqual(gradeFrames('D', '?'), ['?']); assert.deepEqual(gradeFrames('A', 'A'), ['A']);
});
test('Jev sorts stably and does not mutate choices or infer the winner', () => {
  const choices = [{ id: 'due', confidence: 0.33 }, { id: 'order', confidence: 0.92 }, { id: 'ship', confidence: 0.33 }];
  const before = structuredClone(choices);
  assert.deepEqual(settleChoices(choices).map(c => c.id), ['order', 'due', 'ship']);
  assert.deepEqual(choices, before);
});
test('only three confirmed big wins permit a sparkle', () => {
  for (const kind of ['grade-a', 'zero-findings', 'first-publish']) assert.equal(isBigWin(kind), true);
  for (const kind of ['saved', 'bulk', 'published', 'error', undefined]) assert.equal(isBigWin(kind), false);
  assert.equal(isBigWin('first-publish', { production: true }), false);
  assert.equal(isBigWin('grade-a', { ok: false }), false);
  assert.equal(isBigWin('zero-findings', { destructive: true }), false);
});

test('History detects confirmed undo and redo, and leaves a failed call silent', () => {
  const before = [{ id: 'a', revision: 37 }, { id: 'b', revision: 38 }, { id: 'c', revision: 42 }];
  const after = before.map(row => ({ ...row, undone: row.id !== 'a' }));
  assert.deepEqual(historyTransition(before, after), { changes: [after[1], after[2]], direction: 'undo', fromRevision: 42, toRevision: 37 });
  assert.equal(historyTransition(after, before).direction, 'redo');
  assert.equal(historyTransition(before, structuredClone(before)), null);
});
test('History ignores newly loaded rows and never invents a missing destination number', () => {
  assert.equal(historyTransition([], [{ id: 'new', undone: true }]), null);
  const transition = historyTransition([{ id: 'a', revision: 42 }], [{ id: 'a', revision: 42, undone: true }]);
  assert.equal(transition.toRevision, undefined); assert.equal(transition.changes.length, 1);
});

function fakeClock(duration) {
  let time = 0, id = 0;
  const queue = new Map(), frames = [], cancelled = [];
  const clock = runFrames({ duration, frame: k => frames.push(k), now: () => time,
    requestFrame: callback => { queue.set(++id, callback); return id; }, cancelFrame: token => { queue.delete(token); cancelled.push(token); } });
  const advance = ms => { time += ms; const pending = [...queue.values()]; queue.clear(); pending.forEach(callback => callback(time)); };
  return { clock, frames, queue, cancelled, advance };
}
test('the finite clock stops on completion', () => {
  const { frames, queue, advance } = fakeClock(100);
  advance(50); advance(50); advance(100);
  assert.deepEqual(frames, [0, 0.5, 1]); assert.equal(queue.size, 0);
});
test('hiding during motion finishes once and cancels the pending frame', () => {
  const { clock, frames, queue, advance, cancelled } = fakeClock(100);
  advance(30); clock.finish(); advance(100); clock.finish();
  assert.deepEqual(frames, [0, 0.3, 1]); assert.equal(queue.size, 0); assert.equal(cancelled.length, 1);
});
test('unmount cancels the clock without a later state write', () => {
  const { clock, frames, queue, advance } = fakeClock(100);
  clock.stop(); advance(100); assert.deepEqual(frames, [0]); assert.equal(queue.size, 0);
});
test('instant mode allocates no frame', () => {
  const { frames, queue } = fakeClock(0); assert.deepEqual(frames, [1]); assert.equal(queue.size, 0);
});
