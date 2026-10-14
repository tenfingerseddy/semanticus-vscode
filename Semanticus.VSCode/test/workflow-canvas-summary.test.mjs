#!/usr/bin/env node
// 1.5 visuals: the Workflow Canvas showed a format 3 step as an empty box ("0 actions, 0 checks") even when the step had
// an action and checks, because the box and the counts read the format 1 fields (ops, gate.verify). A box now says who
// does the step, what it does and how many checks it has, in plain words, from either format.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { boxesFromEditModel } from '../webview/src/workflowdraft.mjs';
import { canvasCounts, plainWords, stepSummary } from '../webview/src/workflowboxes.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

const check = (kind) => ({ kind, phase: null, target: null, min: null, max: null, of: [], when: null });
const v3 = {
  format: 'v3', restrictions: [], choices: {},
  definition: { key: 'workflow', name: 'return-rate', steps: [
    { key: 'step:add', id: 'add', idKind: 'explicit-stable', number: 1, title: 'Add the measure Return Rate', instructions: '', ops: [], when: null, forEach: null, call: null,
      performer: 'semanticus', action: { op: 'create_measure', args: [{ name: 'tableRef', value: '{{inputs.table}}', values: null }] },
      checks: [check('name_free'), check('valid_dax'), check('bpa_clean')], blanks: [{ name: 'formula', kind: 'dax', fills: 'expression' }], questions: [] },
    { key: 'step:describe', id: 'describe', idKind: 'explicit-stable', number: 2, title: 'Describe it for Copilot', instructions: '', ops: [], when: null, forEach: null, call: null,
      performer: 'assistant', action: null, checks: [], blanks: [], questions: [] },
    { key: 'step:ask', id: 'ask', idKind: 'explicit-stable', number: 3, title: 'Pick the table', instructions: '', ops: [], when: null, forEach: null, call: null,
      performer: 'person', action: null, checks: [], blanks: [], questions: [] },
  ] },
};

test('a format 3 step with an action and 3 checks is not an empty box', () => {
  const boxes = boxesFromEditModel(v3);
  const sum = stepSummary(boxes[0]);
  assert.equal(sum.who, 'semanticus');
  assert.equal(sum.action, 'Create measure');
  assert.equal(sum.checks, 3);
  assert.deepEqual(canvasCounts(boxes), { steps: 3, actions: 1, checks: 3 }, 'the chip counts the action and the checks, not "0 actions, 0 checks"');
});

test('each step says who does it', () => {
  const [a, b, c] = boxesFromEditModel(v3).map(stepSummary);
  assert.equal(a.whoWords, 'Semanticus does it');
  assert.equal(b.who, 'assistant');
  assert.equal(b.whoWords, 'Your assistant does it');
  assert.equal(b.action, null, 'a step the assistant does has no Semanticus action');
  assert.equal(b.detail, 'Follows the step text');
  assert.equal(c.detail, 'Waits for you');
  assert.equal(c.who, 'person');
  assert.equal(c.whoWords, 'You do this one');
});

test('a format 1 step keeps its actions and its gate checks', () => {
  const v1 = { format: 'v2', restrictions: [], choices: {}, definition: { key: 'workflow', name: 'old', steps: [
    { key: 'step:1', id: 'step-1', idKind: 'implicit-positional', number: 1, title: 'Look', instructions: '', ops: ['list_tables', 'list_measures'], when: null, forEach: null, call: null,
      gate: { verify: [{ kind: 'bpa_clean' }] } },
  ] } };
  const boxes = boxesFromEditModel(v1);
  const sum = stepSummary(boxes[0]);
  assert.equal(sum.action, 'List tables and 1 more');
  assert.equal(sum.checks, 1);
  assert.deepEqual(canvasCounts(boxes), { steps: 1, actions: 2, checks: 1 });
});

test('words are plain: no snake case, no camel case, and DAX stays DAX', () => {
  assert.equal(plainWords('verify_dax_equivalence'), 'Verify DAX equivalence');
  assert.equal(plainWords('create_measure'), 'Create measure');
  assert.equal(plainWords('tableRef'), 'Table ref');
});

test('the canvas box and the counts use the summary, and the editor counts the same way', () => {
  const canvas = read('webview/src/workflowcanvas.tsx');
  const design = read('webview/src/workflowdesign.tsx');
  assert.match(canvas, /stepSummary\(step\)/, 'the box reads the summary');
  assert.match(design, /canvasCounts\(boxes\)/, 'the chip counts through the shared helper');
});
