// Lane author-gaps (slice 11): the Studio side of the planned pieces the audit on 82572fa1 found missing.
//
//   B7  Author edits a format-3 workflow's own fields: the values it sends are the engine's shape (every key, the
//       reader's defaults), so the next change to the same list matches what the engine read back; the local mirror
//       moves who does a step and its action together, as the engine writes them; the form draws the Brief, the
//       questions, the parts and the checks as fields, and Add question and Add check write v3 fields, not a gate.
//   B6  Run once: a workflow with a step Semanticus runs is drafted, any other starts guided; the header offers Run once
//       while the work is unsaved.
//   B8  Make it reusable on a draft from words opens the engine's format-3 rendering in Author.
//   B9  A run paused at a person's stop offers Continue (the person's own), an assistant's step Continue without it;
//       steps move up and down before a run.
//   B10 The status bar's undo label shows the engine's words once a draft changed the model and clears on the next change.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'webview', 'src');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const calls = [];
const STUBS = {
  './bridge': { rpc: async (...a) => { calls.push(a); return null; }, copyText: async () => true, onDraftChange: () => () => {},
    onPlanChange: () => () => {}, onDidChange: () => () => {}, runHostCommand: () => {}, loadState: (_k, d) => d, saveState: () => {} },
  './pro': { useTier: () => 'pro', PENDING_ACCESS: 'Checking your Pro access', upgradeToPro: () => {} },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : existsSync(file + '.ts') ? file + '.ts' : file + '.mjs';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true, allowJs: true },
    fileName: full.endsWith('.mjs') ? full.replace(/\.mjs$/, '.js') : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => STUBS[spec] ?? (spec.startsWith('.') ? (spec.endsWith('.css') ? {} : load(resolve(dirname(full), spec))) : req(spec));
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  cache.set(full, module.exports);
  return module.exports;
}
let failures = 0;
function check(name, fn) { try { fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); } }
const read = (p) => readFileSync(p, 'utf8');

// Loaded softly, so a missing module fails each check that needs it rather than the whole file.
const soft = (f) => { try { return load(f); } catch { return {}; } };
const V = soft(join(src, 'workflow-v3model.ts'));

check('B7: the values Author sends are the engine shape, with the reader defaults filled in', () => {
  assert.deepEqual(V.questionValue({ name: 'table', question: 'Which table?' }),
    { name: 'table', question: 'Which table?', type: 'text', required: 'required', by: 'you', values: [], from: null, jev: null, default: null });
  // A list of choices belongs to an enum only; the engine refuses values on any other kind.
  assert.deepEqual(V.questionValue({ name: 'x', type: 'text', values: ['a'] }).values, []);
  assert.deepEqual(V.blankValue({ name: 'formula', kind: 'dax', fills: 'expression' }),
    { name: 'formula', kind: 'dax', fills: 'expression', by: 'assistant', dependsOn: [], maxChars: null, choices: null, generator: null });
  // min and max travel as text, the way the engine keeps them.
  assert.deepEqual(V.checkValue({ kind: 'result_range', phase: 'after', min: 0, max: 1 }),
    { kind: 'result_range', phase: 'after', target: null, min: '0', max: '1', of: [], when: null });
  assert.equal(V.phaseOfCheck({ written: ['valid_dax'], after: ['result_range'] }, 'valid_dax'), 'written');
  assert.equal(V.nextName('answer', ['answer', 'answer2']), 'answer3');
});

check('B6: Run once drafts a workflow with a step Semanticus runs, and starts any other as a guided run', () => {
  const def = (schemaVersion, steps, extra = {}) => ({ schemaVersion, steps, inputs: [], assistantNotes: null, ...extra });
  const step = (performer, extra = {}) => ({ performer, action: null, questions: [], blanks: [], checks: [], brief: null, ...extra });
  assert.equal(V.runOnceKind(def(3, [step('semanticus'), step('person')])), 'draft');
  assert.equal(V.runOnceKind(def(3, [step('assistant')])), 'guided');
  assert.equal(V.runOnceKind(def(2, [step(null)])), 'guided');
  // N3 (lane l9-finish): an all-assistant format-3 file with a field only a draft runs runs as a draft, as the engine
  // decides (WorkflowParser.IsComposed), never as a guided run that says it does not run the field.
  assert.equal(V.runOnceKind(def(3, [step('assistant', { brief: 'Time it cold and warm.' })])), 'draft');
  assert.equal(V.runOnceKind(def(3, [step('assistant', { checks: [{ kind: 'valid_dax' }] })])), 'draft');
  assert.equal(V.runOnceKind(def(3, [step('assistant')], { assistantNotes: 'Keep it short.' })), 'draft');
  assert.equal(V.runOnceKind(def(3, [step('assistant')], { inputs: [{ name: 'table' }] })), 'draft');
  const design = read(join(src, 'workflowdesign.tsx'));
  assert.match(design, />Run once<\/button>/);
  assert.doesNotMatch(design, /Save first\. A run uses the saved workflow\./);
  const page = read(join(src, 'workflows.tsx'));
  assert.match(page, /rpc<\{ draftId: string \}>\('composeTask', null, name, null, null, title, 'human', text\)/);
  assert.match(page, /rpc<WorkflowRunView>\('startWorkflow', name, 'human', text\)/);
});

// Lane l9-finish (coordinator, 26 Sep): Author's check picker showed the engine's ids ("valid_dax"). Every kind the
// engine offers reads as plain words on screen; an id never reaches it, not even for a kind this list does not know yet.
check('Author names every check in plain words and never shows an engine id', () => {
  const F = load(join(src, 'workflow-v3fields.tsx'));
  const engineKinds = {
    before: ['name_free', 'key_unique', 'dates_within', 'source_matches'],
    written: ['valid_dax', 'dax_lint', 'refs_within', 'max_chars', 'no_formula_text', 'valid_m', 'choice_member'],
    after: ['result_range', 'row_count', 'dax_probe', 'bpa_clean', 'some_future_kind'],
  };
  const all = Object.values(engineKinds).flat();
  const step = { key: 'step:units', id: 'units', performer: 'semanticus', action: { op: 'create_measure', args: [] }, questions: [], blanks: [],
    brief: null, checks: all.map((kind) => ({ kind, phase: null, target: null, min: null, max: null, of: [], when: null })) };
  const model = { definition: { key: 'workflow', schemaVersion: 3, steps: [step], assistantNotes: null, inputs: [] }, restrictions: [],
    choices: { executableOps: ['create_measure'], checkKinds: engineKinds, questionTypes: ['text'], questionBy: ['you'], blankKinds: ['dax'], blankBy: ['assistant'] } };
  const html = renderToStaticMarkup(React.createElement(F.V3StepFields, { model, step, apply: () => {} }));
  const shown = html.replace(/<[^>]*>/g, '\n');   // the text on screen, attribute values left out
  for (const id of all) assert.doesNotMatch(shown, new RegExp(`(^|\\W)${id}(\\W|$)`), `the id ${id} reaches the screen`);
  assert.match(html, />The formula is valid DAX</);
  assert.match(html, />The name is free</);
  assert.match(html, />It passes the best practice rules</);
});

// The same rule for the guided gate's check picker, found in lane e: a gate on an assistant step listed dax_probe,
// dax_equivalence and the rest by id.
check('Author names every guided gate check in plain words too', () => {
  const F = load(join(src, 'workflowforms.tsx'));
  const kinds = ['dax_probe', 'dax_equivalence', 'expected_values', 'anchor_coverage', 'readiness_rescan', 'bpa_clean', 'benchmark_delta',
    'workflow_admissible', 'interview_replay', 'baseline_captured', 'impact_assessment', 'baseline_exists', 'baseline_unchanged', 'tests_replay',
    'plan_item_staged', 'plan_item_applied'];
  const step = { key: 'step:a', id: 'a', ops: [], gate: { key: 'gate:a', strictness: null, inputs: [], verify: [{ key: 'verify:a:0', kind: 'dax_probe', when: null, probe: null, scope: null }] } };
  const model = { definition: { key: 'workflow', schemaVersion: 3, steps: [step] }, restrictions: [],
    choices: new Proxy({ verifyKinds: kinds, verifyScopes: ['object', 'model'], strictness: ['hard', 'warn', 'off'] }, { get: (t, k) => (k in t ? t[k] : []) }) };
  const html = renderToStaticMarkup(React.createElement(F.GateEditor, { model, step, apply: () => {}, inputNames: [] }));
  const shown = html.replace(/<[^>]*>/g, '\n');
  for (const id of kinds) assert.doesNotMatch(shown, new RegExp(`(^|\\W)${id}(\\W|$)`), `the id ${id} reaches the screen`);
});

check('B7: the local mirror moves who does a step and its action together, as the engine writes them', () => {
  const D = load(join(src, 'workflowdraft.mjs'));
  const model = { definition: { key: 'workflow', schemaVersion: 3, steps: [
    { key: 'step:a', id: 'a', idKind: 'explicit-stable', performer: 'assistant', action: null, blanks: [], questions: [], checks: [] },
    { key: 'step:b', id: 'b', idKind: 'explicit-stable', performer: 'semanticus', action: { op: 'create_measure', args: [] }, blanks: [{ name: 'formula' }], questions: [], checks: [] }] } };
  const draft = D.createWorkflowDraft({ name: 'w', library: 'user', path: '', exactText: '', byteHash: '', metadata: { schemaVersion: 3 }, editModel: model });
  const given = D.applyWorkflowOperation(draft, { op: 'set_field', target: 'step:a', field: 'action', expect: null, value: { op: 'set_description', args: [] } });
  assert.equal(given.editModel.definition.steps[0].performer, 'semanticus');
  const back = D.applyWorkflowOperation(given, { op: 'set_field', target: 'step:b', field: 'performer', expect: 'semanticus', value: 'person' });
  assert.equal(back.editModel.definition.steps[1].action, null);
  assert.deepEqual(back.editModel.definition.steps[1].blanks, []);
});

check('B7: a v3 step draws its Brief, questions, parts and checks as fields, and Add question writes a v3 field', () => {
  const F = load(join(src, 'workflow-v3fields.tsx'));
  const step = { key: 'step:units', id: 'units', performer: 'semanticus', action: { op: 'create_measure', args: [{ name: 'expression', value: '{{blanks.formula}}', values: null }] },
    questions: [], blanks: [{ name: 'formula', kind: 'dax', fills: 'expression', by: 'assistant', dependsOn: [], maxChars: null, choices: null, generator: null }],
    brief: 'Sum the quantity.', checks: [{ kind: 'valid_dax', phase: 'written', target: null, min: null, max: null, of: [], when: null }] };
  const model = { definition: { key: 'workflow', schemaVersion: 3, steps: [step], assistantNotes: 'Keep it short.', inputs: [] }, restrictions: [],
    choices: { executableOps: ['create_measure', 'set_description'], checkKinds: { before: ['name_free'], written: ['valid_dax'], after: ['result_range'] },
      questionTypes: ['text', 'enum'], questionBy: ['you'], blankKinds: ['dax'], blankBy: ['assistant', 'you'] } };
  const ops = [];
  const html = renderToStaticMarkup(React.createElement(F.V3StepFields, { model, step, apply: (o) => ops.push(o) }));
  for (const id of ['wf-who', 'wf-action-op', 'wf-brief', 'wf-v3-questions', 'wf-blank-formula', 'wf-check-valid_dax']) assert.match(html, new RegExp(`data-testid="${id}"`), id);
  assert.match(html, /<textarea[^>]*data-testid="wf-brief"[^>]*>Sum the quantity\.<\/textarea>/);
  assert.match(html, /Checked once written/);
  assert.match(html, />Add question</);
  assert.match(html, />Add check</);
  assert.doesNotMatch(html, /change in Source/i);
  const settings = renderToStaticMarkup(React.createElement(F.V3SettingsFields, { model, apply: () => {} }));
  assert.match(settings, /<textarea[^>]*data-testid="wf-notes"[^>]*>Keep it short\.<\/textarea>/);
  assert.match(settings, /Inputs asked each run/);
  // The step form uses these on a format-3 workflow, and keeps the gate editor for a guided one.
  const forms = read(join(src, 'workflowforms.tsx'));
  assert.match(forms, /\{editable && <V3StepFields model=\{model\} step=\{step\} apply=\{apply\} \/>\}/);
});

check('B8: Make it reusable on a draft from words opens the engine rendering in Author, unsaved', () => {
  const page = read(join(src, 'workflows.tsx'));
  assert.match(page, /'editDraft', draftId, 'open_in_author'/);
  assert.match(page, /onOpenDocument=\{\(document, title\) => openAuthor\(null, \{ title, document \}\)\}/);
  assert.doesNotMatch(page, /not built yet/);
  const design = read(join(src, 'workflowdesign.tsx'));
  // The engine's text, renamed only when its name is taken by a kept new workflow (verifier on m11/sol-fixes-1, D2).
  assert.match(design, /editWorkflowSource\(current\.current!, derived === seed\.document\.name \? seed\.document\.text : M\.withWorkflowName\(seed\.document\.text, derived\)\)/);
});

const C = soft(join(src, 'composer-model.ts'));
const base = { draftId: 'dr-1', title: 'Units then revenue', source: 'From the Plan', approved: true, summary: '', notes: [], questions: [], blanks: [],
  after: [], undoSteps: 2, partsWaiting: 0 };
const stepsOf = (tags2) => [
  { id: 'step-1', number: 1, title: 'Add Units', changes: 'Adds 1 measure', tags: [], kept: true, status: 'Done' },
  { id: 'step-2', number: 2, title: 'Look', changes: 'Stops here for you', tags: tags2, kept: true, status: 'Waiting' },
  { id: 'step-3', number: 3, title: 'Add Revenue', changes: 'Adds 1 measure', tags: [], kept: true, status: 'Waiting' }];

check('B9: a person\'s stop offers Continue as the one primary button; an assistant\'s step offers Continue without it', () => {
  const person = { ...base, state: 'needs_you', steps: stepsOf(['you']), pausedAt: 'step-2', canContinue: true };
  assert.equal(C.hasRun(person), true);                       // its steps are fixed once it ran
  assert.equal(C.primaryOf(person), 'continue');
  assert.equal(C.pausedStep(person).person, true);
  const asst = { ...base, state: 'waiting_for_assistant', steps: stepsOf(['asst']), pausedAt: 'step-2', canContinue: true };
  assert.equal(C.primaryOf(asst), 'none');
  assert.equal(C.pausedStep(asst).person, false);
  const D = load(join(src, 'composer-draft.tsx'));
  const h = { onEdit: () => {}, onRun: () => {}, onApprove: () => {}, onUndo: () => {}, onGo: () => {}, onAskWhy: () => {}, onCopy: () => {}, onContinue: () => {} };
  const one = renderToStaticMarkup(React.createElement(D.DraftBody, { view: person, busy: null, ...h }));
  assert.match(one, /data-testid="cd-continue"[^>]*>Continue</);
  const two = renderToStaticMarkup(React.createElement(D.DraftBody, { view: asst, busy: null, ...h }));
  assert.match(two, /data-testid="cd-continue"[^>]*>Continue without it</);
});

check('B9: a question on a person\'s stop is asked at the stop, beside Continue, not in Before it runs', () => {
  const q = { name: 'verdict', question: 'Does Units sold look right?', type: 'enum', by: 'you', values: ['Yes', 'No'], step: 'step-2', atStop: true };
  const before = { ...base, state: 'ready', steps: stepsOf(['you']).map((s) => ({ ...s, status: undefined })), questions: [q] };
  assert.deepEqual(C.askedQuestions(before), []);
  assert.deepEqual(C.stopQuestions(before), []);
  const paused = { ...before, state: 'needs_you', pausedAt: 'step-2', canContinue: true };
  assert.deepEqual(C.stopQuestions(paused).map((x) => x.name), ['verdict']);
  const D = load(join(src, 'composer-draft.tsx'));
  const html = renderToStaticMarkup(React.createElement(D.DraftBody, { view: paused, busy: null, onEdit: () => {}, onRun: () => {}, onApprove: () => {},
    onUndo: () => {}, onGo: () => {}, onAskWhy: () => {}, onCopy: () => {}, onContinue: () => {} }));
  assert.match(html, /data-testid="cd-stop"[\s\S]*Does Units sold look right\?/);
});

check('B9: steps move up and down before a run, and not after', () => {
  const fresh = { ...base, approved: false, state: 'ready', steps: stepsOf([]).map((s) => ({ ...s, status: undefined })) };
  assert.equal(C.canMove(fresh, fresh.steps[0], -1), false);
  assert.equal(C.canMove(fresh, fresh.steps[0], 1), true);
  const D = load(join(src, 'composer-draft.tsx'));
  const html = renderToStaticMarkup(React.createElement(D.DraftBody, { view: fresh, busy: null, onEdit: () => {}, onRun: () => {}, onApprove: () => {},
    onUndo: () => {}, onGo: () => {}, onAskWhy: () => {}, onCopy: () => {} }));
  assert.match(html, /data-testid="cd-down-step-1"/);
  const ran = { ...fresh, state: 'done' };
  assert.equal(C.canMove(ran, ran.steps[1], 1), false);
});

check('B10: the undo label shows the engine\'s words once a draft changed the model, and the next change clears it', () => {
  const js = ts.transpileModule(read(join(root, 'src', 'draftUndoChip.ts')), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
  const m = { exports: {} };
  new Function('module', 'exports', js)(m, m.exports);
  const U = m.exports;
  assert.equal(U.draftUndoLabel({ state: 'running', undoLabel: 'Undo: ran 2 steps from your words' }), null);
  assert.equal(U.draftUndoLabel({ state: 'ready' }), null);
  const chip = U.chipAfterDraft(null, { state: 'done', undoLabel: 'Undo: ran 7 steps from your words' }, 41);
  assert.equal(chip.text, 'Undo: ran 7 steps from your words');
  assert.equal(U.chipAfterModelChange(chip, 41), chip);     // the draft's own change
  assert.equal(U.chipAfterModelChange(chip, 42), null);     // any later change, the Undo included
  const ext = read(join(root, 'src', 'extension.ts'));
  assert.match(ext, /draftUndoStatus\.command = 'semanticus\.undo'/);
  assert.match(ext, /renderDraftUndoChip\(chipAfterDraft\(draftUndoChip, v, lastModelRevision\)\)/);
});

check('no em dash in any file this lane added or rewrote', () => {
  for (const f of [join(src, 'workflow-v3fields.tsx'), join(src, 'workflow-v3model.ts'), join(root, 'src', 'draftUndoChip.ts')])
    assert.equal(read(f).includes('—'), false, f);
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
