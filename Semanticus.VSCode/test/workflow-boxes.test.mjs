#!/usr/bin/env node
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

const {
  lossyReasons,
  boxesView,
  EMITTER_FRONTMATTER_KEYS,
  EMITTER_STEP_KEYS,
  EMITTER_INPUT_KEYS,
  EMITTER_VERIFY_KEYS,
  LOSSY_DEF_KEYS,
  LOSSY_STEP_KEYS,
  CONTRACT_INPUT_KEYS,
  CONTRACT_VERIFY_KEYS,
} = await import(new URL('../webview/src/workflowboxes.mjs', import.meta.url).href);

const { createDefinitionLoader, onDefinitionArrived } = await import(
  new URL('../webview/src/workflowload.mjs', import.meta.url).href
);

// ---- the REAL draft/emitter path -------------------------------------------------------------------
// Not a restatement: the shipped `defToDraft`/`emitMarkdown` source is lifted verbatim out of
// workflowdesign.tsx between its emitter-core markers and executed. The region carries type-only
// annotations and no JSX, so stripping the annotations yields exactly the shipped JavaScript.
// The strip is done by the repo's existing `typescript` dev dependency via `ts.transpileModule`
// (the same extract-and-execute pattern as test/lineage-actions.test.mjs) rather than by Node's
// built-in type stripping, which CI's pinned Node 20 does not have.
const designSrc = readFileSync(resolve(root, 'webview/src/workflowdesign.tsx'), 'utf8');
const regionStart = designSrc.indexOf('// #region emitter-core');
const regionEnd = designSrc.indexOf('// #endregion emitter-core');
assert.ok(regionStart >= 0 && regionEnd > regionStart,
  'workflowdesign.tsx must keep its emitter-core markers so tests can run the shipped emitter');
const emitterRegion = designSrc.slice(regionStart, regionEnd);
assert.doesNotMatch(emitterRegion.replace(/=>/g, ''), /<[A-Za-z]/,
  'the emitter-core region must stay JSX-free so the shipped source is executable as written');
const { default: ts } = await import('typescript');
const emitterJs = ts.transpileModule(emitterRegion, {
  compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext },
}).outputText;
// The temp dir is owned from the moment it exists: the exit hook is registered before anything that
// can throw, and the `finally` removes it on the failing path too rather than leaking it to the run.
const emitterDir = mkdtempSync(join(tmpdir(), 'wf-emitter-core-'));
const dropEmitterDir = () => rmSync(emitterDir, { recursive: true, force: true });
process.on('exit', dropEmitterDir);
let emitterModule;
try {
  const emitterFile = join(emitterDir, 'emitcore.mjs');
  writeFileSync(emitterFile, emitterJs);
  emitterModule = await import(pathToFileURL(emitterFile).href);
} finally {
  dropEmitterDir();
}
const { defToDraft, emitMarkdown } = emitterModule;

const lossless = {
  name: 'new-measure',
  title: 'Author a verified measure',
  description: 'Create a measure.',
  version: 1,
  strictness: 'hard',
  triggers: ['create_measure'],
  source: 'stock',
  steps: [
    { id: 'step-1', number: 1, title: 'Capture intent', instructions: 'Ask.', ops: ['get_grounding'], hasExplicitId: false },
  ],
};

assert.deepEqual(lossyReasons(lossless), [], 'a v1 file the Outline emitter can keep must not be lossy');
assert.ok(lossyReasons({ ...lossless, provenance: { derived_from: '' } }).some((r) => r.includes('provenance')),
  'any present provenance key, including an empty value, must block the lossy emitter');
assert.ok(lossyReasons({ ...lossless, provenance: { note: '' } }).length > 0,
  'an empty-string provenance value is still a present key');
assert.deepEqual(lossyReasons({ ...lossless, provenance: {} }), [],
  'an empty provenance bag has no present key and is not lossy by itself');
assert.ok(lossyReasons({ ...lossless, schemaVersion: 2 }).some((r) => r.includes('schemaVersion')));
assert.ok(lossyReasons({
  ...lossless,
  steps: [{ ...lossless.steps[0], forEach: { inLiteral: ['Sales'], as: 'table', maxIterations: 25 } }],
}).some((r) => /forEach/i.test(r)));
assert.ok(lossyReasons({
  ...lossless,
  steps: [{ ...lossless.steps[0], call: { workflow: 'other', with: { x: '1' }, returns: ['y'] } }],
}).some((r) => /call/i.test(r)));
assert.ok(lossyReasons({
  ...lossless,
  steps: [{ ...lossless.steps[0], id: 'capture', hasExplicitId: true }],
}).some((r) => /explicit/i.test(r)));

// ---- a saved definition arrives (the rule DesignMode itself applies) --------------------------------
// `onDefinitionArrived` is the function the component's effect calls, not a restatement of it.
const savedDef = { ...lossless, title: 'agent saved title' };
const dirtyArrival = onDefinitionArrived({ creating: false, dirty: true }, savedDef);
assert.equal(dirtyArrival.liveDef.title, 'agent saved title', 'Boxes must refresh from the agent save');
assert.equal(dirtyArrival.reseedDraft, false, 'a dirty Outline draft must survive a same-selection refresh');
const cleanArrival = onDefinitionArrived({ creating: false, dirty: false }, savedDef);
assert.equal(cleanArrival.reseedDraft, true, 'a clean Outline must re-seed from the saved file');
assert.equal(onDefinitionArrived({ creating: true, dirty: false }, savedDef).reseedDraft, false,
  'a brand-new playbook is never re-seeded from a saved file');
assert.equal(boxesView(dirtyArrival.liveDef).steps[0].title, 'Capture intent',
  'Boxes renders the refreshed definition');

const rich = boxesView({
  ...lossless,
  provenance: { derived_from: '' },
  steps: [
    {
      id: 'review',
      number: 1,
      title: 'Review each table',
      instructions: 'Walk the list.',
      ops: ['list_tables'],
      hasExplicitId: true,
      when: 'inputs.go.answered',
      forEach: { inInput: 'tables', as: 'table', maxIterations: 40 },
      call: { workflow: 'hygiene-pass', with: { target: '[[loop.table]]' }, returns: ['grade'] },
    },
    {
      id: 'step-2',
      number: 2,
      title: 'Finish',
      instructions: '',
      ops: [],
      hasExplicitId: false,
    },
  ],
});
assert.equal(rich.steps[0].idKind, 'explicit');
assert.match(rich.steps[0].idLabel, /id:\s*review/);
assert.equal(rich.steps[1].idKind, 'positional');
assert.match(rich.steps[1].idLabel, /position\s*2/);
assert.equal(rich.steps[0].forEach.maxIterations, 40);
assert.equal(rich.steps[0].forEach.in, 'inputs.tables');
assert.equal(rich.steps[0].forEach.as, 'table');
assert.equal(rich.steps[0].call.workflow, 'hygiene-pass');
assert.equal(rich.steps[0].call.with.target, '[[loop.table]]');
assert.deepEqual(rich.steps[0].call.returns, ['grade']);
assert.equal(rich.connectors, 'file-order-arrows');
assert.match(rich.notRunMeaning, /not a run/i);
assert.doesNotMatch(rich.notRunMeaning, /raw/i);

const design = read('webview/src/workflowdesign.tsx');
const canvas = read('webview/src/workflowcanvas.tsx');
const workflows = read('webview/src/workflows.tsx');
const boxesSrc = read('webview/src/workflowboxes.mjs');
const harness = read('tools/uishot/harness.html');

assert.match(design, /data-wf-view-btn="outline"/, 'lossless files need an Outline control');
assert.match(design, /data-wf-view-btn="boxes"/, 'lossless files need a Boxes control');
assert.match(design, /lossyReasons\(liveDef\)/, 'lossy files must be classified before the emitter can run');
assert.match(design, /view === 'boxes' \|\| lossy/, 'lossy files stay on Boxes');
assert.equal(rich.steps[0].id, 'review', 'Canvas uses explicit engine IDs for shared positions');
assert.equal(rich.steps[1].id, 'step-2', 'Canvas uses positional engine IDs when no explicit ID exists');
assert.match(canvas, /label: 'file order'/, 'connectors follow file order');
assert.match(canvas, /nodesConnectable=\{false\}/, 'the linear view cannot author execution edges');
assert.match(canvas, /deleteKeyCode=\{null\}/, 'Delete cannot remove a workflow step from the view');
assert.match(design, /Not a run/, 'the honest not-run meaning must be on the view');
assert.doesNotMatch(design, /raw (file|text) is (shown|displayed)|raw instruction text is shown/i,
  'the banner must not claim omitted raw text is displayed');
const boxesBanner = design.match(/<Banner color="var\(--sem-warn\)">[\s\S]*?<\/Banner>/);
assert.ok(boxesBanner, 'lossy Boxes view must have a warning banner');
assert.doesNotMatch(boxesBanner[0], /parsed/i, 'Boxes banner must not say parsed');
assert.match(design, /sem-wf-boxes/, 'the new view has its own overflow class');
assert.match(design, /onDefinitionArrived/, 'agent saves must refresh Boxes through the shared arrival rule');
assert.match(workflows, /onWorkflowLibraryChange/, 'library notifications stay the agent-save door');
assert.match(workflows, /getWorkflow/, 'an agent save must refetch the selected definition so Boxes can refresh');

assert.equal(new Set(LOSSY_DEF_KEYS).has('provenance'), true);
for (const key of ['when', 'forEach', 'call', 'hasExplicitId']) {
  assert.ok(LOSSY_STEP_KEYS.includes(key), `${key} must stay on the loss list so Draft cannot outrun it`);
}
assert.match(boxesSrc, /Object\.keys\(prov\)/, 'provenance presence is key-set presence, not a truthy value');
assert.doesNotMatch(boxesSrc, /if\s*\(\s*v\s*\)/, 'empty provenance values must not be skipped as falsy');

assert.match(harness, /wfMode === 'boxes'|wf === 'boxes'|wfParam === 'boxes'/, 'harness must render the boxes state');
assert.match(harness, /rich-loss|richloss/, 'harness must render the rich-loss state');


// =====================================================================================================
// BLOCKER 1 , the loss guard must cover the WHOLE gate input / verify contract, not a hand-picked few.
// Every case below is proved twice: `lossyReasons` must name the field, AND the shipped
// defToDraft -> emitMarkdown path must be shown to actually drop it.
// =====================================================================================================

// One step carrying a gate, so a nested field has somewhere real to live.
const gated = (input = {}, verify = {}) => ({
  ...lossless,
  steps: [{
    id: 'step-1', number: 1, title: 'Capture intent', instructions: 'Ask.', ops: ['get_grounding'],
    hasExplicitId: false,
    gate: {
      strictness: null,
      inputs: [{ name: 'witnessDax', question: 'Paste the measure.', type: 'text', required: 'required', ...input }],
      verify: [{ kind: 'dax_equivalence', when: '', probe: '', scope: '', intent: '', ...verify }],
    },
  }],
});

// The emitted file for a definition , the honest test of "was the field kept?".
const emitFor = (def) => emitMarkdown(defToDraft(def));

// A gate whose every field the emitter keeps is not lossy, and survives the round trip.
const keptAll = gated({ type: 'enum', required: 'optional' }, { when: 'inputs.witnessDax.answered', probe: 'witnessDax', scope: 'model', intent: 'rename' });
assert.deepEqual(lossyReasons(keptAll), [], 'a gate using only emitter-kept fields must not be lossy');
{
  const md = emitFor(keptAll);
  for (const kept of ['type: enum', 'required: optional', 'when: inputs.witnessDax.answered', 'probe: witnessDax', 'scope: model', 'intent: rename']) {
    assert.ok(md.includes(kept), `the emitter must still write ${kept}`);
  }
}

// The two cases the final review named, each proved through the real emitter.
{
  const def = gated({ daxPurity: 'no-bare-measures' });
  assert.ok(lossyReasons(def).some((r) => /daxPurity/.test(r)),
    'an input daxPurity the emitter cannot keep must be named as a loss');
  assert.doesNotMatch(emitFor(def), /daxPurity/, 'the shipped emitter really does drop daxPurity');
  assert.equal(Object.hasOwn(defToDraft(def).steps[0].inputs[0], 'daxPurity'), false,
    'the shipped defToDraft really does drop daxPurity');
}
{
  const def = gated({}, { pinnedShapes: ['grand_total'] });
  assert.ok(lossyReasons(def).some((r) => /pinnedShapes/.test(r)),
    'a verify pinnedShapes the emitter cannot keep must be named as a loss');
  assert.doesNotMatch(emitFor(def), /pinnedShapes|grand_total/, 'the shipped emitter really does drop pinnedShapes');
  assert.equal(Object.hasOwn(defToDraft(def).steps[0].verify[0], 'pinnedShapes'), false,
    'the shipped defToDraft really does drop pinnedShapes');
}

// ...and EVERY other field on the contract, enumerated from the contract itself rather than from a list
// this test maintains by hand , so the coverage cannot quietly fall behind the engine.
const NON_DEFAULT = {
  daxPurity: 'no-bare-measures', scope: 'run',
  pinnedShapes: ['grand_total'], openShapes: ['cross'], openShapesFrom: 'disputes',
  openMismatch: 'countersign', anchors: 'lockedAnchors',
};
const droppedInputKeys = CONTRACT_INPUT_KEYS.filter((k) => !EMITTER_INPUT_KEYS.includes(k));
const droppedVerifyKeys = CONTRACT_VERIFY_KEYS.filter((k) => !EMITTER_VERIFY_KEYS.includes(k));
assert.ok(droppedInputKeys.length && droppedVerifyKeys.length,
  'the contract must have fields the emitter cannot keep, or this whole guard is pointless');
for (const key of droppedInputKeys) {
  const value = NON_DEFAULT[key];
  assert.ok(value !== undefined, `this test needs a non-default sample for input ${key}`);
  const def = gated({ [key]: value });
  assert.ok(lossyReasons(def).some((r) => r.includes(key)), `input ${key} must be named as a loss`);
  assert.doesNotMatch(emitFor(def), new RegExp(key), `the emitter must be shown to drop input ${key}`);
}
for (const key of droppedVerifyKeys) {
  const value = NON_DEFAULT[key];
  assert.ok(value !== undefined, `this test needs a non-default sample for verify ${key}`);
  const def = gated({}, { [key]: value });
  assert.ok(lossyReasons(def).some((r) => r.includes(key)), `verify ${key} must be named as a loss`);
  assert.doesNotMatch(emitFor(def), new RegExp(key), `the emitter must be shown to drop verify ${key}`);
}

// Semantic defaults are NOT a loss: the field is present but says exactly what its absence says.
// Getting this wrong would push every ordinary v1 file onto read-only Boxes.
const DEFAULTED = [
  gated({ daxPurity: null }),
  gated({ daxPurity: undefined }),
  gated({ scope: null }),
  gated({ scope: 'iteration' }),                 // "Null (or 'iteration') is the default" , Workflow.cs
  gated({ type: 'text', required: 'answer-or-decline' }),
  gated({}, { pinnedShapes: [] }),               // "Both empty (the default)" , Workflow.cs
  gated({}, { openShapes: [] }),
  gated({}, { openShapesFrom: null, openMismatch: null, anchors: null }),
];
for (const def of DEFAULTED) {
  assert.deepEqual(lossyReasons(def), [],
    `a gate field holding its semantic default must not push a valid v1 file onto read-only Boxes: ${JSON.stringify(def.steps[0].gate)}`);
}

// An unknown future field the emitter cannot keep is still a loss , the guard is key-set based, so it
// cannot silently fall behind the engine contract the way a hand-maintained list does.
assert.ok(lossyReasons(gated({ someFutureInputField: 'x' })).length > 0,
  'an unrecognised non-default input field must be treated as a loss');
assert.ok(lossyReasons(gated({}, { someFutureVerifyField: 'x' })).length > 0,
  'an unrecognised non-default verify field must be treated as a loss');

// The kept-key lists the guard subtracts must be the ones the emitter actually writes.
for (const key of EMITTER_INPUT_KEYS) assert.ok(CONTRACT_INPUT_KEYS.includes(key), `${key} must be a contract input key`);
for (const key of EMITTER_VERIFY_KEYS) assert.ok(CONTRACT_VERIFY_KEYS.includes(key), `${key} must be a contract verify key`);
assert.deepEqual(EMITTER_INPUT_KEYS.filter((k) => !CONTRACT_INPUT_KEYS.includes(k)), [],
  'the emitter cannot claim to keep an input field the contract does not have');
assert.deepEqual(EMITTER_VERIFY_KEYS.filter((k) => !CONTRACT_VERIFY_KEYS.includes(k)), [],
  'the emitter cannot claim to keep a verify field the contract does not have');

// A lossy gate keeps the file on read-only Boxes, which is what the loss guard is for.
assert.equal(boxesView(gated({ daxPurity: 'no-bare-measures' })).lossy.length > 0, true,
  'a gate-level loss must reach the Boxes view so the file stays read-only');

// =====================================================================================================
// BLOCKER 2 , one selection/request validity rule for BOTH doors.
// Driven through `createDefinitionLoader`, the module the React component calls; there is no second
// simulation of the component to disagree with.
// =====================================================================================================

function loaderHarness(initial) {
  let selection = initial;
  const pending = [];
  const installed = [];
  const loader = createDefinitionLoader({
    rpc: (_method, name) => new Promise((resolve, reject) => pending.push({ name, resolve, reject })),
    setDef: (d) => installed.push(d),
    currentSelection: () => selection,
  });
  return {
    loader, installed, pending,
    select(name) { selection = name; },
    // resolve the oldest still-unsettled request for `name`
    async deliver(name, def) {
      const req = pending.find((r) => r.name === name && !r.done);
      assert.ok(req, `expected an in-flight getWorkflow for ${name}`);
      req.done = true; req.resolve(def);
      await new Promise((r) => setTimeout(r, 0));
    },
    async fail(name) {
      const req = pending.find((r) => r.name === name && !r.done);
      assert.ok(req, `expected an in-flight getWorkflow for ${name}`);
      req.done = true; req.reject(new Error('gone'));
      await new Promise((r) => setTimeout(r, 0));
    },
    last() { return installed[installed.length - 1]; },
  };
}

// A delayed response for A must not install after the person has moved to B.
{
  const h = loaderHarness('a');
  const cleanupA = h.loader.loadForSelection('a');   // the selection effect for A
  h.select('b');
  cleanupA();                                        // React runs the previous effect's cleanup...
  h.loader.loadForSelection('b');                    // ...then the effect for B
  await h.deliver('b', { name: 'b' });
  await h.deliver('a', { name: 'a' });               // A finally answers, far too late
  assert.equal(h.last().name, 'b', 'a slow response for a workflow no longer selected must not install');
  assert.equal(h.installed.filter((d) => d && d.name === 'a').length, 0,
    'the superseded workflow definition must never reach the shared state');
}

// Same race, but the stale request came from a LIBRARY NOTIFICATION , the path that had no guard at all.
{
  const h = loaderHarness('a');
  h.loader.loadForSelection('a');
  await h.deliver('a', { name: 'a', title: 'A' });
  h.loader.loadAfterNotification();                  // an agent saved something; refetch A
  h.select('b');
  h.loader.loadForSelection('b');
  await h.deliver('b', { name: 'b', title: 'B' });
  await h.deliver('a', { name: 'a', title: 'A (stale)' });
  assert.equal(h.last().title, 'B',
    'a notification refetch for a workflow the person has left must not overwrite the selected one');
}

// Overlapping saves on the SAME workflow: the newer response wins, whatever order they answer in.
{
  const h = loaderHarness('a');
  h.loader.loadForSelection('a');
  await h.deliver('a', { name: 'a', title: 'first' });
  h.loader.loadAfterNotification();                  // save #1
  h.loader.loadAfterNotification();                  // save #2, starts before #1 answers
  const [older, newer] = h.pending.filter((r) => r.name === 'a' && !r.done);
  newer.done = true; newer.resolve({ name: 'a', title: 'newest' });
  await new Promise((r) => setTimeout(r, 0));
  older.done = true; older.resolve({ name: 'a', title: 'older' });
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(h.last().title, 'newest', 'an older overlapping save response must not overwrite a newer one');
}

// A same-selection refresh MUST still land , the guard cannot be "never update".
{
  const h = loaderHarness('a');
  h.loader.loadForSelection('a');
  await h.deliver('a', { name: 'a', title: 'saved' });
  h.loader.loadAfterNotification();
  await h.deliver('a', { name: 'a', title: 'saved again' });
  assert.equal(h.last().title, 'saved again', 'a save on the workflow still open must refresh Boxes');
}

// A failure for a workflow no longer selected must not blank the definition now on screen.
{
  const h = loaderHarness('a');
  const cleanupA = h.loader.loadForSelection('a');
  h.select('b');
  cleanupA();
  h.loader.loadForSelection('b');
  await h.deliver('b', { name: 'b' });
  await h.fail('a');
  assert.ok(h.last() && h.last().name === 'b', 'a stale failure must not clear the selected definition');
}

// A human save on the open workflow refreshes it; a human save that is superseded does not install.
{
  const h = loaderHarness('a');
  h.loader.loadForSelection('a');
  await h.deliver('a', { name: 'a', title: 'before' });
  h.loader.reload('a');
  await h.deliver('a', { name: 'a', title: 'after save' });
  assert.equal(h.last().title, 'after save', 'a human save must refresh the open definition');
  h.loader.reload('a');
  h.select('b');
  h.loader.loadForSelection('b');
  await h.deliver('b', { name: 'b', title: 'B' });
  await h.deliver('a', { name: 'a', title: 'stale save' });
  assert.equal(h.last().title, 'B', 'a save response for a workflow already left must not install');
}

// Clearing the selection still clears the definition.
{
  const h = loaderHarness(null);
  h.loader.loadForSelection(null);
  assert.equal(h.last(), null, 'no selection means no definition');
}

// A notification with nothing selected asks for nothing.
{
  const h = loaderHarness(null);
  h.loader.loadAfterNotification();
  assert.equal(h.pending.length, 0, 'a notification with no selection must not fetch');
}

// The duplicated unguarded path must be gone from the component: both doors go through the loader.
assert.match(workflows, /createDefinitionLoader/, 'both doors must load through the shared loader');
assert.doesNotMatch(workflows, /rpc<WorkflowDef>\('getWorkflow'/,
  'workflows.tsx must not keep its own getWorkflow call beside the shared loader');
assert.equal((workflows.match(/'getWorkflow'/g) ?? []).length, 0,
  'the only getWorkflow request is the one inside the shared loader');
// The third door: a human save from the Author pane refreshes through the same rule.
assert.match(workflows, /defLoad\.reload\(/, 'a human save must refresh through the shared loader');
const loadSrc = read('webview/src/workflowload.mjs');
assert.equal((loadSrc.match(/rpc\(/g) ?? []).length, 1,
  'the loader must issue its request in exactly one place, so one validity rule covers both doors');

// D-091 / D-098: a project copy (including lossy Canvas and unparseable files) can be deleted, and
// stock still offers Customise even when Outline cannot keep the file.
assert.doesNotMatch(design, /lossy \? \(\s*<span[^>]*>Read-only workflow/,
  'lossy must not replace Customise and Delete with a dead Read-only label');
assert.match(design, /readOnlyStock && !editing/,
  'Customise stays on the stock read-only path, including lossy stock');
assert.match(design, /workflow file has a format error/);
const parseErrAt = design.indexOf('workflow file has a format error');
const parseErrPanel = design.slice(parseErrAt, design.indexOf('</Panel>', parseErrAt));
assert.match(design, /deleteWorkflow/);
assert.match(parseErrPanel, /delNamed|Delete/, 'an unparseable user copy must offer Delete');

const documentEditor = read('webview/src/workflowdocument.tsx');
assert.match(documentEditor, /deleteWorkflow/, 'the file editor must be able to delete a project copy');

// D-100: Home search filters as you type; Enter is not the only way to see results.
assert.match(workflows, /placeholder="Search playbooks"/);
assert.match(workflows, /data-wf-home-search/);
assert.match(workflows, /No playbooks match/);
assert.match(workflows, /matchesFilter\(w,/, 'Home search reuses the Library matcher');

const scenarios = read('webview/src/workflowscenarios.tsx');
assert.match(scenarios, /instantiateWorkflowTemplate[\s\S]{0,500}onApplied\(\)/,
  'filling a template must not replace the library with the one new playbook');

console.log('workflow boxes tests passed');
