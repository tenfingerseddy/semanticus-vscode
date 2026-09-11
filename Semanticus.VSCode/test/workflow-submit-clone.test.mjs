// D-171, round 4 acceptance retest of 2026-09-11. The Submit step button threw
// "Failed to execute postMessage on MessagePort: PointerEvent object could not be cloned", so no
// workflow, calendar or journey case could advance on the UI door.
//
// One binding caused it: `onClick={doSubmit}` handed the React click event to doSubmit's first
// parameter, which is a gate override. That event then rode along in the rpc arguments. The bridge
// gives those arguments to postMessage, which structured-clones them, and a DOM event is a platform
// object that cannot be cloned, so the call died inside the webview and the step never moved.
//
// Two halves, both kept here. The source guard names the binding that caused it. The executed half
// runs the real wire builder and clones its output exactly as the bridge does.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gateArg, submitStepArgs } from '../webview/src/workflowsubmit.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (f) => readFileSync(resolve(root, f), 'utf8');
const workflows = read('webview/src/workflows.tsx');

// ---- the binding that caused it ------------------------------------------------------------------
// Binding the submit handler straight to onClick is what put a PointerEvent on the wire: React calls a
// click handler with the event, and the handler read it as its gate override.
assert.ok(
  !/onClick=\{doSubmit\}/.test(workflows),
  'the Submit step button must call its handler, not be bound to it bare: the click event became the gate override and postMessage cannot clone a PointerEvent (round 4, D-171)');
assert.match(
  workflows,
  /onClick=\{\(\) => \{ void doSubmit\(\); \}\}/,
  'the Submit step button calls doSubmit with no argument, so no click event can reach the gate override');
// The wire arguments are built in one place, so a second caller cannot hand-assemble them.
assert.match(
  workflows,
  /submitStepArgs\(runId, stepId, answersJson, 'human', callGate\)/,
  'the submit rpc posts the wire arguments built by the shared module');

// ---- what the bridge does with them --------------------------------------------------------------
// postMessage structured-clones its argument. A DOM event is a platform object and fails that clone,
// which is the reported failure: "PointerEvent object could not be cloned".
const clickEvent = {
  type: 'click', target: null, nativeEvent: null,
  preventDefault() {}, stopPropagation() {}, persist() {},
};
assert.throws(
  () => structuredClone(['wfr-1', 'step-2', '{}', 'human', clickEvent]),
  { name: 'DataCloneError' },
  'a click event in the rpc arguments is the un-cloneable payload that killed the Submit step');

// ---- the gate override is a gate, never an event -------------------------------------------------
assert.equal(gateArg('on'), 'on', 'a gate override that is a real gate string survives');
assert.equal(gateArg('off'), 'off');
assert.equal(gateArg(undefined), undefined, 'no override from a hand-off-free step');
assert.equal(gateArg(''), undefined, 'an empty string is not a gate');
assert.equal(gateArg(null), undefined);
assert.equal(gateArg(clickEvent), undefined, 'a click event is dropped, never forwarded as a gate');

// ---- the wire arguments survive the clone --------------------------------------------------------
{
  const args = submitStepArgs('wfr-1', 'step-2', '{"daxExpression":"SUM(Sales[Amount])"}', 'human', undefined);
  assert.deepEqual(args, ['wfr-1', 'step-2', '{"daxExpression":"SUM(Sales[Amount])"}', 'human', null], 'an ordinary submit carries a null gate');
  assert.doesNotThrow(() => structuredClone(args), 'an ordinary submit survives the structured clone postMessage performs');

  // The exact shape a bare binding used to produce: an event where the gate belongs. It must still post.
  const fromEvent = submitStepArgs('wfr-1', 'step-2', '{}', 'human', clickEvent);
  assert.equal(fromEvent[4], null, 'a bare binding costs the gate override and nothing else');
  assert.doesNotThrow(() => structuredClone(fromEvent), 'a submit from a bare binding can no longer poison the wire arguments');

  // The hand-off buttons pass a real gate override and must keep it.
  const handedOff = submitStepArgs('wfr-1', 'step-2', '{}', 'human', 'on');
  assert.equal(handedOff[4], 'on', 'the hand-off gate override reaches the engine unchanged');
  assert.doesNotThrow(() => structuredClone(handedOff));
  assert.deepEqual(submitStepArgs('wfr-9', 'step-1', '{}', 'human', 'off'), ['wfr-9', 'step-1', '{}', 'human', 'off']);
}

console.log('workflow submit clone tests passed');
