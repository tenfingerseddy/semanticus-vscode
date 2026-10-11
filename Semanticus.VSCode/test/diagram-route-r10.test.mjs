// Review round 10 of m11/diagram-route (30 Sep). The Diagram reloads its model when the engine announces a change that can change
// its tables or lines. A redo of an added relationship announces NO delta for the add (TOM fires no event for an add, and only an
// edit's commit reads the added objects), so the Diagram kept drawing the old lines until it was opened again (L3). The engine
// marks a walk of the timeline with `replay` ("undo" or "redo") on model/didChange, and that is what the page now reloads on.
// The rest of round 10 lives in diagram.tsx, which no node test loads: tools/uishot/diagram-route-r10-drive.mjs proves it live.
import assert from 'node:assert/strict';
import { changeReloadsDiagram } from '../webview/src/diagramfit.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

ok('a redo that brings a relationship back carries no delta for it, and reloads the Diagram', () => {
  assert.equal(changeReloadsDiagram({ replay: 'redo', deltas: [] }), true);
});
ok('an undo walk reloads too, whatever its deltas say', () => {
  assert.equal(changeReloadsDiagram({ replay: 'undo', deltas: [] }), true);
  assert.equal(changeReloadsDiagram({ replay: 'undo', deltas: [{ kind: 'update', ref: 'measure:Sales/Amount' }] }), true);
});
ok('a structure, add or remove delta reloads, as before', () => {
  for (const kind of ['structure', 'add', 'remove']) assert.equal(changeReloadsDiagram({ deltas: [{ kind, ref: 'relationship:A_B' }] }), true, kind);
});
ok('a change with no delta list at all reloads, as before', () => {
  assert.equal(changeReloadsDiagram({}), true);
  assert.equal(changeReloadsDiagram(undefined), true);
});
ok('a plain property update of an ordinary edit does not reload the Diagram', () => {
  assert.equal(changeReloadsDiagram({ deltas: [{ kind: 'update', ref: 'measure:Sales/Amount', props: ['Description'] }] }), false);
  assert.equal(changeReloadsDiagram({ deltas: [] }), false);
  assert.equal(changeReloadsDiagram({ replay: null, deltas: [] }), false);
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
