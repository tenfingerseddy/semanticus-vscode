#!/usr/bin/env node
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { applyWorkflowOperation, boxesFromEditModel, createWorkflowDraft } from '../webview/src/workflowdraft.mjs';
import { createDefinitionLoader } from '../webview/src/workflowload.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

const model = {
  format: 'v2', restrictions: [], choices: {},
  definition: { key: 'workflow', name: 'rich', steps: [
    { key: 'step:review', id: 'review', idKind: 'explicit-stable', number: 1, title: 'Review each table', instructions: 'Walk the list.', ops: ['list_tables'],
      when: 'inputs.go.answered', forEach: { source: { kind: 'input', name: 'tables' }, as: 'table', maxIterations: 40 },
      call: { workflow: 'hygiene-pass', with: [{ key: 'with:target', name: 'target', value: '[[loop.table]]' }], returns: ['grade'] } },
    { key: 'step:2', id: 'step-2', idKind: 'implicit-positional', number: 2, title: 'Finish', instructions: '', ops: [], when: null, forEach: null, call: null },
  ] },
};

const boxes = boxesFromEditModel(model, (name) => name === 'hygiene-pass' ? 'Tidy the model' : name);
assert.equal(boxes[0].id, 'review', 'Canvas layout identity is the runtime step id');
assert.equal(boxes[0].key, 'step:review', 'form selection keeps the opaque edit target');
assert.equal(boxes[0].when, 'inputs.go.answered');
assert.deepEqual(boxes[0].forEach, { in: 'inputs.tables', as: 'table', maxIterations: 40 });
assert.deepEqual(boxes[0].call, { workflow: 'Tidy the model', with: { target: '[[loop.table]]' }, returns: ['grade'] });
assert.match(boxes[1].idLabel, /made stable when steps move/);

const canvas = read('webview/src/workflowcanvas.tsx');
const design = read('webview/src/workflowdesign.tsx');
const forms = read('webview/src/workflowforms.tsx');
assert.doesNotMatch(canvas, /(?:^|[,\{]\s*)label:\s*['"`]file order/im, 'Canvas arrows do not show an implementation label');
assert.match(canvas, /ariaLabel: `File order:/, 'Canvas order remains available to assistive technology');
assert.match(canvas, /nodesConnectable=\{false\}/, 'Canvas cannot author runtime edges');
assert.match(canvas, /Select a step to edit it\. Steps run in this order\./);
assert.match(canvas, /When \{step\.when\}/, 'a condition is shown as its own box line');
assert.match(canvas, /For each \{step\.forEach\.as/, 'a repeat is shown as its own box line');
assert.match(canvas, /Calls \{step\.call\.workflow\}/, 'a call is shown as its own box line');
assert.match(design, /previewWorkflowEdit/, 'existing definitions save through engine preview');
assert.match(design, /editWorkflowDocument/, 'reviewed exact text uses the guarded writer');
assert.doesNotMatch(design, /emitMarkdown|defToDraft|isLossy/, 'the old whole-file emitter and loss fence are gone');
assert.match(forms, /Runs only when/);
assert.match(forms, /Repeats for each item of/);
assert.match(forms, /Calls another workflow/);
assert.match(forms, /model\.choices\.verifyKinds/, 'all engine-projected verify choices remain reachable');
assert.match(forms, /pinnedShapes/);
assert.match(forms, /openShapesFrom/);
assert.match(forms, /daxPurity/);

test('a verify pinnedShapes the emitter cannot keep must be named as a loss', () => {
  const withVerify = structuredClone(model);
  withVerify.definition.steps[0].gate = {
    strictness: 'warn', inputs: [], verify: [{
      key: 'verify:shape', kind: 'dax_equivalence', pinnedShapes: ['cross'], openShapes: [],
    }],
  };
  const document = { exactText: 'saved source', byteHash: 'hash-1', path: '/project/rich.md', editModel: withVerify };
  const draft = applyWorkflowOperation(createWorkflowDraft(document, 'session-1'), {
    op: 'set_field', target: 'verify:shape', field: 'pinnedShapes', expect: ['cross'], value: ['grand_total'],
  });
  assert.deepEqual(draft.editModel.definition.steps[0].gate.verify[0].pinnedShapes, ['grand_total']);
  assert.deepEqual(JSON.parse(JSON.stringify(draft.operations))[0].value, ['grand_total']);
  assert.doesNotMatch(design, /emitMarkdown|lossyReasons/,
    'the replacement editor must not send pinnedShapes through the retired lossy whole-file emitter');
});

test('a slow response for a workflow no longer selected must not install', async () => {
  let selected = 'a';
  const requests = [];
  const installed = [];
  const loader = createDefinitionLoader({
    rpc: (_method, name) => new Promise((resolve) => requests.push({ name, resolve })),
    setDef: (definition) => installed.push(definition),
    currentSelection: () => selected,
  });
  const leaveA = loader.loadForSelection('a');
  selected = 'b';
  leaveA();
  loader.loadForSelection('b');
  requests.find((request) => request.name === 'b').resolve({ name: 'b' });
  await new Promise((resolve) => setTimeout(resolve, 0));
  requests.find((request) => request.name === 'a').resolve({ name: 'a' });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(installed.map((definition) => definition.name), ['b']);
});

test('the step form offers a browsable action picker and keeps the type-the-name quick path', () => {
  assert.match(forms, /What should this step be allowed to do\?/, 'the picker opens with the question, the way 1.1.3 asked it');
  assert.match(forms, /Choose actions/, 'the step form has a control that opens the picker');
  assert.match(forms, /groupOpCatalog\(catalog, \{ filter, present: actionPresentation \}\)/, 'the panel groups the real catalog and filters it');
  assert.match(forms, /data-wf-op-question=\{question\.question\}/, 'groups are rendered by question');
  assert.match(forms, /data-wf-op-shelf=\{shelf\.shelf\}/, 'shelves are rendered under their question');
  assert.match(forms, /data-wf-op-choice=\{op\.name\}/, 'each action is its own tick target');
  assert.match(forms, /<small>\{op\.description\}<\/small>/, 'each action shows its plain one-line description');
  assert.match(forms, /list="sem-wf-op-list"/, 'the datalist stays as the quick path for anyone who knows the name');
  assert.match(forms, /step\.ops\.includes\(op\) \? step\.ops\.filter/, 'ticking adds and unticking removes through the one ops field');
  assert.doesNotMatch(forms, /rpc<OpInfo\[\]>\('getOpCatalog'\)[\s\S]{0,80}\n\s*\}, \[\]\);\n\s*return/, 'the catalog is fetched once per session, not once per selected step');
  assert.match(forms, /<WordBudget text=\{step\.instructions\} \/>/, 'the soft word budget sits under the instructions field');
  assert.match(forms, /words > INSTRUCTIONS_WORD_BUDGET &&/, 'the budget only nudges above the threshold and never blocks');
});

console.log('workflow shared boxes and form contract tests passed');
