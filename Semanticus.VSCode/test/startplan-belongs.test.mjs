// Round 2, leftover: a start plan from an earlier SQL start belongs only to the model it was made for. It must not show on a live model.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

const load = makeLoader({}, req('react'));
const { startPlanBelongs, startPageMode, builtBandWords } = load('startplan-model.ts');

const plan = { destination: { folder: '/home/me/models', name: 'Northstar Source', modelPath: '/home/me/models/Northstar Source' } };

test('the plan shows when no model is open or the open model is the one it builds', () => {
  assert.equal(startPlanBelongs(plan, null), true);
  assert.equal(startPlanBelongs(plan, { source: '' }), true);
  assert.equal(startPlanBelongs(plan, { source: '/home/me/models/Northstar Source/model.bim' }), true);
});

test('the plan stays off a model opened live, and off a different model', () => {
  assert.equal(startPlanBelongs(plan, { liveBound: true, source: 'powerbi://api.powerbi.com/v1.0/myorg/Dev' }), false);
  assert.equal(startPlanBelongs(plan, { source: '/home/me/other/Sales' }), false);
  assert.equal(startPlanBelongs(null, null), false);
});

test('a built plan is left as it is', () => {
  assert.equal(startPlanBelongs({ ...plan, built: true }, { liveBound: true }), true);
});

// K25 (9 Oct, the live demo): right after Approve and build the whole built plan (242 lines) held the Plan page, and the model's own
// rows, its grade strip and Run the plan sat behind "Discard this plan". A built plan is a short band over the ordinary Plan.
test('a plan being decided holds the page; a built plan is a band over the ordinary Plan', () => {
  assert.equal(startPageMode(null, false), 'none');
  assert.equal(startPageMode({ built: false }, false), 'plan');
  assert.equal(startPageMode({ built: true }, false), 'band');
  assert.equal(startPageMode({ built: true }, true), 'plan');   // "See what was built" opens the whole plan again
});

test('the built band says what was built and how to take it back, in plain words', () => {
  const view = {
    destination: { name: 'Contoso Sales (test K25)' },
    sections: [{ id: 'tables', groups: [{ lines: [{ kept: true }, { kept: true }, { kept: false }, { kept: true, held: true }] }] }],
  };
  assert.equal(builtBandWords(view), 'Contoso Sales (test K25) was built from this plan. 2 lines built. One undo takes it back.');
  assert.equal(builtBandWords({ destination: {}, sections: [] }), 'The model was built from this plan. 0 lines built. One undo takes it back.');
});
