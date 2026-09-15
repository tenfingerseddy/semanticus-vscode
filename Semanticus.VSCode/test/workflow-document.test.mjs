import assert from 'node:assert/strict';
import { test } from 'node:test';
import * as documentState from '../webview/src/workflowdocument.mjs';
import { acceptDocumentSave, applyTextareaChange, canApplyUpgrade, canPreviewUpgrade, createDocumentLoader, receiveDocument } from '../webview/src/workflowdocument.mjs';
import {
  applyWorkflowOperation, boxesFromEditModel, countWords, createWorkflowDraft, editWorkflowSource, groupOpCatalog,
  installWorkflowPreview, INSTRUCTIONS_WORD_BUDGET, mapLayoutPositions, newTempKey, receiveWorkflowDocument,
  redoWorkflowDraft, undoWorkflowDraft, uniqueStepId, UNFILED_OPS, workflowDraftDirty, workflowPreviewArgs,
} from '../webview/src/workflowdraft.mjs';

const document = (exactText, byteHash = exactText, path = '/project/.semanticus/workflows/review.md') => ({
  name: 'review', library: 'user', path, exactText, byteHash,
  metadata: { parses: true, parseError: null, schemaVersion: 2, title: 'Review', version: 1, stepIds: ['review'], explicitIds: true },
});

test('reviewed saved source can explicitly become the base without losing the draft', () => {
  const original = document('original', 'hash-1');
  const latest = document('agent update', 'hash-2');
  const draft = { ...receiveDocument(null, original), text: 'my reconciled edit' };
  const stale = receiveDocument(draft, latest);
  assert.equal(stale.base.byteHash, 'hash-1', 'refresh cannot authorize an overwrite');
  const rebased = documentState.rebaseDocument(stale, latest);
  assert.equal(rebased.text, draft.text);
  assert.equal(rebased.base.byteHash, 'hash-2', 'the next explicit save uses the reviewed hash');
  assert.equal(rebased.base.path, original.path);
  assert.deepEqual(rebased.latest, latest);
  assert.deepEqual(receiveDocument(JSON.parse(JSON.stringify(rebased)), latest), rebased, 'the rebased draft survives navigation');
  assert.equal(canPreviewUpgrade(rebased), false, 'rebasing does not consume the dirty draft for an upgrade');
  const next = receiveDocument(rebased, document('another outside edit', 'hash-3'));
  assert.notEqual(next.base.byteHash, next.latest.byteHash, 'another edit disables saving again');
  assert.equal(next.text, draft.text);
});

test('rebasing refuses a changed review, another path, and stock source without changing the draft', () => {
  const original = document('original', 'hash-1');
  const reviewed = document('reviewed', 'hash-2');
  const stale = receiveDocument({ ...receiveDocument(null, original), text: 'keep this draft' }, reviewed);
  const snapshot = JSON.stringify(stale);
  for (const incoming of [document('newer', 'hash-3'), document('reviewed', 'hash-2', '/other/review.md'),
    { ...reviewed, library: 'stock' }]) {
    assert.throws(() => documentState.rebaseDocument({ ...stale, latest: incoming }, reviewed), /no longer matches this review/);
  }
  const other = document('reviewed', 'hash-2', '/other/review.md');
  assert.throws(() => documentState.rebaseDocument({ ...stale, latest: other }, other), /no longer matches this review/, 'even a reviewed other-project file cannot become the base');
  const stock = { ...reviewed, library: 'stock' };
  assert.throws(() => documentState.rebaseDocument({ ...stale, latest: stock }, stock), /no longer matches this review/);
  assert.throws(() => documentState.rebaseDocument({ ...stale, base: { ...original, library: 'stock' } }, reviewed), /no longer matches this review/);
  assert.equal(JSON.stringify(stale), snapshot);
});

test('clean buffers follow changes; dirty buffers keep their original write fence and text', () => {
  const first = document('original', 'hash-1');
  const newer = document('agent update', 'hash-2');
  const clean = receiveDocument(null, first);
  assert.equal(receiveDocument(clean, newer).text, 'agent update');
  const dirty = { ...clean, text: 'my unsaved edit' };
  const stale = receiveDocument(dirty, newer);
  assert.equal(stale.text, 'my unsaved edit');
  assert.equal(stale.base.byteHash, 'hash-1');
  assert.equal(stale.latest.byteHash, 'hash-2');
  assert.deepEqual(receiveDocument(JSON.parse(JSON.stringify(stale)), newer), stale, 'a retained draft survives leaving and reopening the editor');
  assert.equal(receiveDocument(dirty, document('other project', 'hash-3', '/other/review.md')).text, 'other project', 'drafts cannot cross document paths');
});

test('refused saves retain invalid or conflicting drafts and the original base hash', () => {
  const original = receiveDocument(null, document('original', 'hash-1'));
  for (const saved of [original.base, document('external edit', 'hash-2')]) {
    const draft = { ...original, text: 'invalid or conflicting draft' };
    const refused = acceptDocumentSave(draft, saved, draft.text, false);
    assert.equal(refused.text, draft.text);
    assert.equal(refused.base.byteHash, 'hash-1');
    assert.equal(refused.base.exactText, 'original');
    assert.deepEqual(refused.latest, saved);
  }
});

test('save results acknowledge exactly the submitted draft, including no-op results', () => {
  const first = receiveDocument(null, document('old', 'hash-1'));
  const saved = document('submitted', 'hash-2');
  const clean = acceptDocumentSave({ ...first, text: 'submitted' }, saved, 'submitted', true);
  assert.equal(clean.text, clean.base.exactText);
  assert.equal(clean.base.byteHash, 'hash-2');
  const stillTyping = acceptDocumentSave({ ...first, text: 'a newer edit' }, saved, 'submitted', true);
  assert.equal(stillTyping.text, 'a newer edit');
  assert.equal(stillTyping.base.byteHash, 'hash-2');
  assert.deepEqual(acceptDocumentSave(first, first.base, 'old', false), first);
  const equalBytes = acceptDocumentSave({ ...first, text: 'submitted' }, saved, 'submitted', false);
  assert.equal(equalBytes.text, equalBytes.base.exactText);
  assert.equal(equalBytes.base.byteHash, 'hash-2');
});

test('textarea edits preserve BOM and untouched CRLF, LF, CR and final-newline bytes', () => {
  const bom = String.fromCharCode(0xfeff);
  const exact = bom + 'one\r\ntwo\nthree\r';
  assert.equal(applyTextareaChange(exact, 'one\ntwo\nthree\n'), exact, 'an untouched textarea does not normalize the file');
  assert.equal(applyTextareaChange(exact, 'one\nTWO\nthree\n'), bom + 'one\r\nTWO\nthree\r');
  assert.equal(applyTextareaChange('one\r\ntwo', 'one\nnew\ntwo'), 'one\r\nnew\r\ntwo');
  assert.equal(applyTextareaChange('one\r\ntwo\r\n', 'one\n'), 'one\r\n');
  assert.equal(applyTextareaChange('one', ''), '');
  assert.equal(applyTextareaChange(bom + 'one', ''), bom, 'editing content keeps its encoding marker');
});

test('late document reads and late failures cannot replace a newer selection or update', async () => {
  const requests = [];
  const received = [];
  const errors = [];
  const loader = createDocumentLoader({
    read: () => new Promise((resolve, reject) => requests.push({ resolve, reject })),
    onDocument: (value) => received.push(value), onError: (error) => errors.push(error),
  });
  const old = loader.load('review');
  const latest = loader.load('review');
  requests[1].resolve(document('latest'));
  await latest;
  requests[0].resolve(document('stale'));
  assert.equal(await old, null);
  assert.deepEqual(received.map((value) => value.exactText), ['latest']);
  const abandoned = loader.load('review');
  loader.invalidate();
  requests[2].reject(new Error('old read failed'));
  await abandoned;
  assert.deepEqual(errors, []);
});

test('upgrade previews require clean saved source and never consume a local draft', () => {
  const saved = document('v1 source', 'hash-1');
  saved.metadata.schemaVersion = 1;
  const clean = receiveDocument(null, saved);
  assert.equal(canPreviewUpgrade(clean), true);
  const dirty = { ...clean, text: 'local draft' };
  assert.equal(canPreviewUpgrade(dirty), false);
  assert.equal(dirty.text, 'local draft');
  assert.equal(canPreviewUpgrade({ ...clean, latest: document('external source', 'hash-2') }), false);
  assert.equal(canPreviewUpgrade({ ...clean, latest: document('v1 source', 'hash-1', '/other/review.md') }), false);
  assert.equal(canPreviewUpgrade(receiveDocument(null, { ...saved, metadata: { ...saved.metadata, parses: false } })), false);
  assert.equal(canPreviewUpgrade(null), false);
});

test('apply uses a matching reviewed source, not the preview changed flag', () => {
  const saved = document('v1 source', 'hash-1');
  const clean = receiveDocument(null, saved);
  const preview = { name: saved.name, dryRun: true, changed: false, canApply: true, document: saved,
    proposedText: 'v2 source', diff: '-v1 source +v2 source', parseError: null };
  assert.equal(canApplyUpgrade(clean, preview), true, 'safe previews always have changed:false');
  assert.equal(canApplyUpgrade({ ...clean, text: 'local draft' }, preview), false);
  assert.equal(canApplyUpgrade(receiveDocument(clean, document('new source', 'hash-2')), preview), false);
  assert.equal(canApplyUpgrade(receiveDocument(clean, document('v1 source', 'hash-1', '/other/review.md')), preview), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, canApply: false }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, dryRun: false }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, parseError: 'Invalid format' }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, proposedText: saved.exactText }), false, 'v2 no-ops cannot apply');
  assert.equal(canApplyUpgrade(clean, { ...preview, proposedText: null }), false);
  assert.equal(canApplyUpgrade(clean, null), false);
});

test('stock upgrades can preview but need a fresh project-copy preview to apply', () => {
  const stock = { ...document('v1 source', 'same-hash', '/stock/review.md'), library: 'stock' };
  const preview = { dryRun: true, changed: false, canApply: false, document: stock, proposedText: 'v2 source', diff: 'diff' };
  const clean = receiveDocument(null, stock);
  assert.equal(canPreviewUpgrade(clean), true);
  assert.equal(canApplyUpgrade(clean, preview), false);
  const copy = receiveDocument(clean, document(stock.exactText, stock.byteHash));
  assert.equal(canApplyUpgrade(copy, preview), false, 'matching bytes at a new path still need a new preview');
  assert.equal(canApplyUpgrade(copy, { ...preview, canApply: true, document: copy.base }), true);
});

const projectedDocument = () => ({
  ...document('exact source', 'hash-1'),
  editModel: {
    format: 'v2', restrictions: [], choices: { defaultMaxIterations: 25, maxIterations: 100 },
    definition: {
      key: 'workflow', schemaVersion: 2, name: 'review', kind: 'workflow', title: 'Review', description: '', whenToUse: '', version: 1,
      strictness: 'hard', triggers: [], tags: [], provenance: [{ key: 'provenance:owner', name: 'owner', value: 'finance' }], slots: [],
      steps: [
        { key: 'step:1', fingerprint: 'fp-1', id: 'step-1', idKind: 'implicit-positional', number: 1, title: 'Collect tables', instructions: '', ops: [], when: null,
          forEach: null, call: null, gate: { strictness: null, inputs: [{ key: 'step:1/input:tables', fingerprint: 'input-fp', name: 'tables', question: 'Tables?', type: 'text', required: 'required', scope: 'iteration' }], verify: [] } },
        { key: 'step:2', fingerprint: 'fp-2', id: 'review', idKind: 'explicit-stable', number: 2, title: 'Review each table', instructions: 'Review it.', ops: ['list_tables'], when: 'inputs.tables.answered',
          forEach: { source: { kind: 'input', name: 'tables' }, as: 'table', maxIterations: 40 },
          call: { workflow: 'callee', with: [{ key: 'step:2/call.with:target', name: 'target', value: '[[loop.table]]' }], returns: ['grade'] },
          gate: { strictness: 'warn', inputs: [], verify: [{ key: 'step:2/verify:dax_equivalence', fingerprint: 'verify-fp', kind: 'dax_equivalence', when: null, probe: 'expected', scope: 'object', intent: null, pinnedShapes: ['cross'], openShapes: [], openShapesFrom: null, openMismatch: null, anchors: null }] } },
      ],
    },
  },
});

test('one workflow draft composes rich fields, stable structural edits, serialization, and undo', () => {
  let draft = createWorkflowDraft(projectedDocument(), 'session-7');
  draft = applyWorkflowOperation(draft, { op: 'set_field', target: 'step:2', field: 'instructions', expect: 'Review it.', value: 'Review it and record evidence.' });
  draft = applyWorkflowOperation(draft, { op: 'set_field', target: 'step:2', field: 'forEach.maxIterations', expect: 40, value: 60 });
  draft = applyWorkflowOperation(draft, { op: 'set_map_entry', target: 'step:2', field: 'call.with', name: 'target', expect: '[[loop.table]]', value: '[[loop.table.name]]' });
  draft = applyWorkflowOperation(draft, { op: 'insert_item', target: 'step:2', field: 'gate.verify', after: 'step:2/verify:dax_equivalence', tempKey: 'new:verify', value: { kind: 'bpa_clean', pinnedShapes: [], openShapes: [] } });
  const beforeMove = draft;
  draft = applyWorkflowOperation(draft, { op: 'move_step', target: 'step:2', after: null, expectOrder: ['step:1', 'step:2'] }, true);
  assert.equal(draft.operations[0].op, 'stabilize_step_ids', 'stabilization leads the complete edit list');
  assert.equal(draft.operations.at(-1).op, 'move_step');
  assert.equal(draft.editModel.definition.steps[0].key, 'step:2', 'opaque keys survive local reorder');
  assert.equal(draft.editModel.definition.steps[1].id, 'collect-tables', 'unstable ids use the engine contract derivation');
  assert.equal(draft.editModel.definition.steps[0].call.with[0].value, '[[loop.table.name]]');
  assert.equal(draft.editModel.definition.steps[0].gate.verify.length, 2);
  assert.deepEqual(JSON.parse(workflowPreviewArgs(draft)[3]), draft.operations, 'editsJson is one lossless serialized list');
  const undone = undoWorkflowDraft(draft);
  assert.deepEqual(undone.editModel, beforeMove.editModel);
  assert.deepEqual(redoWorkflowDraft(undone).editModel, draft.editModel);
  assert.equal(boxesFromEditModel(draft.editModel, (name) => name === 'callee' ? 'Check model' : name)[0].call.workflow, 'Check model');
});

test('source, notifications, preview projection, and positions remain on the same draft', () => {
  const base = createWorkflowDraft(projectedDocument(), 'session-7');
  const changed = applyWorkflowOperation(base, { op: 'set_field', target: 'workflow', field: 'title', expect: 'Review', value: 'Review safely' });
  const latest = { ...projectedDocument(), path: '/project/.semanticus/workflows-moved/review.md', exactText: 'external source', byteHash: 'hash-2' };
  const notified = receiveWorkflowDocument(changed, latest, 'replacement-session');
  assert.equal(notified.base.byteHash, 'hash-1');
  assert.equal(notified.latest.byteHash, 'hash-2');
  assert.equal(notified.base.path, '/project/.semanticus/workflows/review.md', 'a resolved-path change cannot advance the dirty write fence');
  assert.equal(notified.sessionId, 'session-7', 'a replaced session cannot silently rebind a dirty draft');
  assert.equal(notified.editModel.definition.title, 'Review safely');

  const source = editWorkflowSource(notified, 'source draft');
  assert.equal(source.editModel, null);
  assert.deepEqual(source.operations, []);
  assert.equal(workflowDraftDirty(source), true);
  const projected = installWorkflowPreview(source, { proposedText: 'source draft', editModel: projectedDocument().editModel, issues: [], keyChanges: [] });
  assert.equal(projected.sourceText, 'source draft');
  assert.equal(projected.editModel.definition.steps[1].when, 'inputs.tables.answered');
  assert.deepEqual(mapLayoutPositions({ 'step-1': { x: 10, y: 20 }, review: { x: 40, y: 50 } }, [{ oldKey: 'step:1', newKey: 'step:collect-tables', oldStepId: 'step-1', newStepId: 'collect-tables' }]),
    { 'collect-tables': { x: 10, y: 20 }, review: { x: 40, y: 50 } });
});

test('every structural and collection operation updates the one local projection', () => {
  let draft = createWorkflowDraft(projectedDocument(), 'session-7');
  draft = applyWorkflowOperation(draft, { op: 'set_map_entry', target: 'workflow', field: 'provenance', name: 'reviewedBy', expect: null, value: 'finance' });
  assert.equal(draft.editModel.definition.provenance.at(-1).value, 'finance');
  draft = applyWorkflowOperation(draft, { op: 'remove_map_entry', target: 'workflow', field: 'provenance', name: 'owner', expect: 'finance' });
  assert.deepEqual(draft.editModel.definition.provenance.map((entry) => entry.name), ['reviewedBy']);

  draft = applyWorkflowOperation(draft, { op: 'insert_item', target: 'workflow', field: 'slots', after: null, tempKey: 'new:slot', value: {
    name: 'subject', question: 'What should be reviewed?', type: 'text', required: 'required', values: [],
  } });
  draft = applyWorkflowOperation(draft, { op: 'insert_item', target: 'workflow', field: 'slots', after: 'new:slot', tempKey: 'new:slot-2', value: {
    name: 'context', question: 'What context matters?', type: 'text', required: 'optional', values: [],
  } });
  draft = applyWorkflowOperation(draft, { op: 'move_item', target: 'new:slot-2', after: null, expectOrder: ['new:slot', 'new:slot-2'] });
  assert.deepEqual(draft.editModel.definition.slots.map((slot) => slot.name), ['context', 'subject']);
  draft = applyWorkflowOperation(draft, { op: 'remove_item', target: 'new:slot', expectFingerprint: '' });
  assert.deepEqual(draft.editModel.definition.slots.map((slot) => slot.name), ['context']);

  draft = applyWorkflowOperation(draft, { op: 'add_step', after: 'step:2', tempKey: 'new:step', value: { id: 'record-result', title: 'Record result', instructions: '', ops: [] } }, true);
  draft = applyWorkflowOperation(draft, { op: 'copy_step', target: 'new:step', after: 'new:step', tempKey: 'new:step-copy', newId: 'record-result-copy' }, true);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.id), ['collect-tables', 'review', 'record-result', 'record-result-copy']);
  draft = applyWorkflowOperation(draft, { op: 'remove_step', target: 'new:step', expectFingerprint: '' }, true);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.id), ['collect-tables', 'review', 'record-result-copy']);
  assert.equal(draft.operations.filter((operation) => operation.op === 'stabilize_step_ids').length, 1);
  assert.equal(draft.operations[0].op, 'stabilize_step_ids');
});

// =====================================================================================================
// A WORKFLOW WITH NO STEPS MUST STILL BE ABLE TO GET ONE.
// 1.2.0 shipped an editor whose only add control lived inside a selected step's form. A new workflow has
// zero steps, so nothing could be selected and there was no way to author a first step at all (Kane's
// "under author there is no add step button"). The Steps rail, the Canvas and the empty form area all now
// mint the same operation through this module, so this is the shape every one of those controls emits.
// What this does NOT prove: that the controls are on screen and reachable. That is the journey driver
// (Semanticus.VSCode/tools/drive-authoring.mjs), which clicks them in the built bundle.
// =====================================================================================================
test('a zero-step draft can be given its first step, then more, in any position', () => {
  const blank = {
    name: 'blank', library: 'user', path: '/project/.semanticus/workflows/blank.md',
    exactText: '---\nschemaVersion: 2\nname: blank\n---\n', byteHash: 'hash-blank',
    metadata: { parses: true, parseError: null, schemaVersion: 2, title: 'Blank', version: 1, stepIds: [], explicitIds: true },
    editModel: {
      format: 'v2', restrictions: [], choices: {},
      definition: { key: 'workflow', schemaVersion: 2, name: 'blank', kind: 'workflow', title: 'Blank', description: '',
        version: 1, triggers: [], tags: [], provenance: [], slots: [], steps: [] },
    },
  };
  let draft = createWorkflowDraft(blank);
  assert.equal(draft.editModel.definition.steps.length, 0, 'the fixture is the empty case the add controls exist for');
  assert.equal(workflowDraftDirty(draft), false);

  // The empty state's "Add the first step": after is null, which the patcher reads as position 0.
  const first = newTempKey();
  assert.match(first, /^new:/, 'a draft-only key must be recognisable as one');
  draft = applyWorkflowOperation(draft, { op: 'add_step', after: null, tempKey: first,
    value: { id: uniqueStepId('New step', draft.editModel), title: 'New step', instructions: '', ops: [] } }, true);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.title), ['New step']);
  assert.equal(draft.editModel.definition.steps[0].key, first, 'the new step is addressable by the key the control minted');
  assert.equal(draft.editModel.definition.steps[0].number, 1);
  assert.equal(workflowDraftDirty(draft), true, 'adding a step is an unsaved draft change, not a write');
  assert.equal(draft.operations.filter((operation) => operation.op === 'stabilize_step_ids').length, 0,
    'an empty workflow has no positional ids to stabilize, so the first step must not drag one in');

  // The rail's persistent "+ Add step": after is the last step, so it appends.
  const second = newTempKey();
  const last = draft.editModel.definition.steps[draft.editModel.definition.steps.length - 1].key;
  draft = applyWorkflowOperation(draft, { op: 'add_step', after: last, tempKey: second,
    value: { id: uniqueStepId('New step', draft.editModel), title: 'Second', instructions: '', ops: [] } }, true);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.title), ['New step', 'Second']);
  assert.notEqual(draft.editModel.definition.steps[0].id, draft.editModel.definition.steps[1].id,
    'uniqueStepId must not hand the same id to two steps');

  // The rail's between-steps insert: after is the step it sits under, so it lands in the middle.
  const middle = newTempKey();
  draft = applyWorkflowOperation(draft, { op: 'add_step', after: first, tempKey: middle,
    value: { id: uniqueStepId('Middle', draft.editModel), title: 'Middle', instructions: '', ops: [] } }, true);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.title), ['New step', 'Middle', 'Second']);
  assert.deepEqual(draft.editModel.definition.steps.map((step) => step.number), [1, 2, 3], 'renumbering follows an insert');

  // Canvas reads the same draft, so a step added anywhere is on the canvas with no save in between.
  assert.deepEqual(boxesFromEditModel(draft.editModel).map((box) => box.title), ['New step', 'Middle', 'Second']);
  assert.equal(draft.operations.length, 3, 'three adds, and nothing else was smuggled into the draft');
  assert.equal(undoWorkflowDraft(draft).editModel.definition.steps.length, 2, 'each add is one undo step');
});

// =====================================================================================================
// THE BROWSABLE ACTION PICKER AND THE SOFT WORD BUDGET.
// 1.2.0 replaced 1.1.3's question > shelf > action picker with a flat datalist, so a step's actions could
// only be chosen by someone who already knew the op name, and it dropped the instructions word budget.
// This is the grouping and counting the restored picker renders. What it does NOT prove: that the panel
// is on screen and clickable. That is the journey driver (Semanticus.VSCode/tools/drive-oppicker.mjs).
// =====================================================================================================
// The names here are deliberately NOT real op names: the coverage oracle globs test sources for op
// names, and a fixture that merely shapes a catalog is not evidence that anything covers those ops.
// The questions and shelves are the ratified strings, because the grouping is what is under test.
const catalog = [
  { name: 'sample_connect', description: 'Connect to a model open on this computer.', question: 'Open a model and connect', shelf: 'Connections and targets' },
  { name: 'sample_open', description: 'Open a semantic model from a file or folder.', question: 'Open a model and connect', shelf: 'Connections and targets' },
  { name: 'sample_metrics', description: 'List every metric in the model.', question: 'See what is in the model', shelf: 'Measures, DAX and queries' },
  { name: 'sample_peek', description: 'Peek at the data in a table.', question: 'See what is in the model', shelf: 'Tables, columns and hierarchies' },
  { name: 'sample_metric_add', description: 'Create a metric with an expression.', question: 'Change the model', shelf: 'Measures, DAX and queries' },
  { name: 'sample_unfiled', description: 'An op the taxonomy has not filed yet.', question: null, shelf: null },
];

test('the action catalog groups into question then shelf, in the order the engine sent it', () => {
  const grouped = groupOpCatalog(catalog);
  assert.deepEqual(grouped.questions.map((question) => question.question),
    ['Open a model and connect', 'See what is in the model', 'Change the model', UNFILED_OPS],
    'questions keep the engine order and are never re-sorted; an unfiled op stays reachable at the end');
  assert.deepEqual(grouped.questions[1].shelves.map((shelf) => shelf.shelf),
    ['Measures, DAX and queries', 'Tables, columns and hierarchies'], 'shelves keep the engine order inside a question');
  assert.deepEqual(grouped.questions[0].shelves[0].ops.map((op) => op.name), ['sample_connect', 'sample_open']);
  assert.equal(grouped.questions[1].count, 2, 'a question counts every action under all of its shelves');
  assert.equal(grouped.total, catalog.length, 'an unfiltered picker offers the whole surface, never a truncated sample');
  assert.equal(grouped.questions[0].shelves[0].ops[0].description, 'Connect to a model open on this computer.',
    'every action carries its plain one-line description, which is what makes the list browsable');
});

test('the picker filters on name, title and description, and hides actions the step already has', () => {
  assert.deepEqual(groupOpCatalog(catalog, { filter: 'metric' }).questions.flatMap((question) => question.shelves.flatMap((shelf) => shelf.ops.map((op) => op.name))),
    ['sample_metrics', 'sample_metric_add'], 'a filter matches the op name');
  assert.deepEqual(groupOpCatalog(catalog, { filter: 'Peek at the data' }).questions.flatMap((question) => question.shelves.flatMap((shelf) => shelf.ops.map((op) => op.name))),
    ['sample_peek'], 'a filter matches the plain description, so a name you do not know is still findable');
  const filtered = groupOpCatalog(catalog, { filter: 'metric' });
  assert.deepEqual(filtered.questions.map((question) => question.question), ['See what is in the model', 'Change the model'],
    'a group with no surviving action disappears rather than showing as empty');
  assert.equal(filtered.first, 'sample_metrics', 'the first hit is offered so Enter can take it');
  assert.equal(groupOpCatalog(catalog, { exclude: ['sample_connect', 'sample_open'] }).questions.length, 3,
    'excluding every action of a question drops the question with it');
  assert.equal(groupOpCatalog(catalog, { filter: 'nothing matches this' }).total, 0);
  assert.equal(groupOpCatalog(null).total, 0, 'a catalog that has not arrived yet is an empty picker, not a crash');
  const presented = groupOpCatalog([{ name: 'sample_evidence', description: 'raw', question: 'Prove it is right', shelf: 'Tests, numbers and evidence' }],
    { present: () => ({ label: 'Evidence report', description: 'Export the sealed report.' }) });
  assert.equal(presented.questions[0].shelves[0].ops[0].label, 'Evidence report', 'an action with a friendlier title shows it');
});

test('the instructions word budget counts words and only warns above the 1.1.3 threshold', () => {
  assert.equal(INSTRUCTIONS_WORD_BUDGET, 150, 'the soft budget is the threshold the 1.1.3 designer used');
  assert.equal(countWords(''), 0);
  assert.equal(countWords('   '), 0, 'whitespace alone is not a word');
  assert.equal(countWords('Walk the list.'), 3);
  assert.equal(countWords('one\ntwo   three\tfour'), 4, 'any run of whitespace separates words');
  assert.equal(countWords(null), 0, 'a step with no instructions yet counts zero rather than throwing');
  const long = Array.from({ length: INSTRUCTIONS_WORD_BUDGET + 1 }, () => 'word').join(' ');
  assert.equal(countWords(long) > INSTRUCTIONS_WORD_BUDGET, true, 'the nudge fires one word past the budget');
  assert.equal(countWords(Array.from({ length: INSTRUCTIONS_WORD_BUDGET }, () => 'word').join(' ')) > INSTRUCTIONS_WORD_BUDGET, false,
    'a step exactly at the budget is quiet, because the budget is guidance and never a limit');
});
