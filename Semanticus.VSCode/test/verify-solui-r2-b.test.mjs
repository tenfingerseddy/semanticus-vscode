// Verifier on m11/solui fix round 2 (rule R-B: typed words end only when the engine accepted exactly them or the person discarded them). A sample of the swept sites,
// each driven twice: once with ANOTHER writer moving the value under the typed words (the words must stay in the box or be named), and once with the person alone
// (the write must carry the typed words, and no moved or kept note may be left behind by the person's own save).
// Sites: B1 a Plan row's note, reply, reason and repeat form; B2 a finding's accept reason; B3 a composer draft's Add more words and Write it yourself boxes;
// B4 the Workflows New panel. (Round 1, K5: the Could not and Mark done reply forms are gone from the row, and their two B1 tests with them.) The hook runtime renders one component at a time, so a remount is an unmount and a fresh mount over the same modules.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
globalThis.location ??= { search: '?expand=all' };
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const bridgeOf = (disk, more = {}) => ({ loadState: disk.loadState, saveState: disk.saveState, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}), copyText: async () => true, ...more });
const heldAll = (disk) => JSON.stringify([...disk.map.values()]);
function withContext(React) {
  React.createContext = (d) => { const c = { current: d }; c.Provider = function Provider(p) { return p.children; }; return c; };
  React.useContext = (c) => c.current;
  return React;
}

// ---- B1. a Plan row ---------------------------------------------------------------------------------------------------------------------------------------
function planRowPage() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': bridgeOf(disk), './pro': { upgradeToPro: () => undefined, useTier: () => 'pro', PENDING_ACCESS: '' }, './toolrow': { anchorUnder: () => ({}) } }, MODEL);
  return { disk, pg, R: pg.load('plan-row.tsx'), RP: pg.load('plan-repeat.tsx') };
}
const task = (over = {}) => ({ id: 'pt-0000000b', title: 'Check the totals', source: 'you', who: 'you', doer: 'you', stateWord: 'ready', note: 'old note', ...over });
const noteBox = (t) => byTestId(t, 'plan-note');
const saveNote = (t) => find(t, (el) => el.type === 'button' && el.props?.type === 'submit' && flat(el) === 'Save note');

test('B1 other writer: a note typed while the assistant rewrites it and moves the row stays, the newer note is named, and Use the new note is the person\'s choice', async () => {
  const { pg, R, disk } = planRowPage();
  const live = { item: task() };
  const mount = () => pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction() {}, onGo() {}, onDoer() {}, onNote() {} }));
  let row = mount(); await settleAll(); row.rerender();
  noteBox(row.result).props.onChange({ target: { value: 'my words' } }); await settleAll(); row.rerender();
  // The assistant writes a note on the row (no remount yet): the box keeps the words and names the change.
  live.item = task({ note: 'their note 1' }); row.rerender(); await settleAll(); row.rerender();
  assert.equal(noteBox(row.result).props.value, 'my words');
  assert.ok(byTestId(row.result, 'plan-note-moved'), 'the newer note is named while the row is still drawn');
  // Then it takes the row (Next -> on it): the row is drawn anew.
  row.unmount();
  live.item = task({ note: 'their note 2', stateWord: 'on_it', doer: 'assistant' });
  row = mount(); await settleAll(); row.rerender();
  assert.equal(noteBox(row.result).props.value, 'my words', 'the box opens on the typed words');
  const moved = byTestId(row.result, 'plan-note-moved');
  assert.ok(moved, 'the note that moved while the words were away is named');
  // The person chooses the newer note: the box shows it and nothing is held.
  find(moved, (el) => el.type === 'button').props.onClick(); await settleAll(); row.rerender();
  assert.equal(noteBox(row.result).props.value, 'their note 2');
  assert.equal(byTestId(row.result, 'plan-note-moved'), null);
  assert.ok(!heldAll(disk).includes('my words'), 'a choice of the newer note ends the held words');
});

test('B1 person alone: a note saved with nothing else changing goes out as typed, settles, and leaves no note and no held words', async () => {
  const { pg, R, disk } = planRowPage();
  const live = { item: task() };
  const sent = [];
  const row = pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction() {}, onGo() {}, onDoer() {}, onNote: (it, text) => sent.push(text) }));
  await settleAll(); row.rerender();
  noteBox(row.result).props.onChange({ target: { value: 'check Q3 first' } }); await settleAll(); row.rerender();
  assert.equal(saveNote(row.result).props.disabled, false, 'Save note is offered');
  find(row.result, (el) => el.type === 'form' && el.props?.className === 'pw-note').props.onSubmit({ preventDefault() {} });
  assert.deepEqual(sent, ['check Q3 first']);
  live.item = task({ note: 'check Q3 first' });   // the engine took it
  row.rerender(); await settleAll(); row.rerender();
  assert.equal(noteBox(row.result).props.value, 'check Q3 first');
  assert.equal(byTestId(row.result, 'plan-note-moved'), null, 'no moved note after the person\'s own save');
  assert.ok(!heldAll(disk).includes('check Q3 first'), 'nothing is held once the engine took the words');
  // Typing again offers Save again (not hidden for good).
  noteBox(row.result).props.onChange({ target: { value: 'check Q4 too' } }); row.rerender();
  assert.equal(saveNote(row.result).props.disabled, false);
});

// DEFECT (friction, not harm): the engine trims a note (PlanTasks.cs SetPlanTaskNoteAsync, text.Trim()), so a note saved with a trailing space comes back without it, and the
// person's own save then shows "The note changed while you typed" and keeps the words held under the row (plan-row.tsx useKeptText + the planrow| hold). The person typed alone.
test('B1 person alone: a note saved with a trailing space (the engine trims it) leaves no "changed while you typed" note and no held words', async () => {
  const { pg, R, disk } = planRowPage();
  const live = { item: task() };
  const row = pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction() {}, onGo() {}, onDoer() {}, onNote() {} }));
  await settleAll(); row.rerender();
  noteBox(row.result).props.onChange({ target: { value: 'check Q3 first ' } }); await settleAll(); row.rerender();
  find(row.result, (el) => el.type === 'form' && el.props?.className === 'pw-note').props.onSubmit({ preventDefault() {} });
  live.item = task({ note: 'check Q3 first' });   // what the engine stores
  row.rerender(); await settleAll(); row.rerender();
  assert.equal(byTestId(row.result, 'plan-note-moved'), null, 'the person\'s own save is shown as another writer\'s change');
  assert.ok(!heldAll(disk).includes('check Q3 first '), 'the saved words stay held under the row');
});

test('B1 repeat form: a cadence set and saved goes out and is not held; a cadence set and left open survives a remount', async () => {
  const { pg, RP, disk } = planRowPage();
  const row = { id: 'pt-0000000b', title: 'Check the totals', source: 'you', doer: 'you' };
  const scope = `planrow|${row.id}`;
  const hold = { read: () => pg.drafts.heldDraft(scope, 'repeat')?.value, write: (c, start) => pg.drafts.draftStore.set(MODEL, scope, 'repeat', { value: c, base: start }), clear: () => pg.drafts.draftStore.clear(MODEL, scope, 'repeat') };
  const added = [];
  const mount = () => pg.mount(() => RP.RepeatEditor({ row, repeats: [], isPro: true, busy: null, onAdd: async (r) => { added.push(r); return true; }, onChange() {}, onDelete() {}, hold }));
  let ed = mount();
  byTestId(ed.result, 'plan-repeat-add').props.onClick(); ed.rerender();
  let formEl = find(ed.result, (el) => el.type?.name === 'CadenceForm');
  let form = pg.mount(() => formEl.type(formEl.props));
  // Opening the form and changing nothing holds nothing.
  await settleAll();
  assert.ok(!heldAll(disk).includes('"repeat"'), 'an untouched form holds nothing');
  byTestId(form.result, 'plan-repeat-every').props.onChange({ target: { value: '4' } }); await settleAll(); form.rerender();
  form.unmount(); ed.unmount();
  ed = mount(); await settleAll(); ed.rerender();
  formEl = find(ed.result, (el) => el.type?.name === 'CadenceForm');
  assert.ok(formEl, 'the form opens again on the held cadence');
  form = pg.mount(() => formEl.type(formEl.props)); await settleAll(); form.rerender();
  assert.equal(byTestId(form.result, 'plan-repeat-every').props.value, 4);
  // Save: the cadence goes out and the hold ends.
  find(form.result, (el) => el.type === 'form').props.onSubmit({ preventDefault() {} }); await settleAll();
  form.unmount(); ed.rerender();
  assert.equal(added.length, 1); assert.equal(added[0].cadence.every, 4);
  assert.equal(pg.drafts.heldDraft(scope, 'repeat'), undefined, 'a saved cadence is not held');
  ed.unmount(); ed = mount(); await settleAll(); ed.rerender();
  assert.equal(find(ed.result, (el) => el.type?.name === 'CadenceForm'), null, 'the next mount starts closed');
});

// ---- B2. a finding's accept reason -------------------------------------------------------------------------------------------------------------------------
function findingsPage(disk = makeDisk()) {
  const pg = makePage({ './bridge': bridgeOf(disk, { revealInTree: () => undefined }), './ui': { Caret: () => null } }, MODEL);
  withContext(pg.React);
  return { disk, pg, F: pg.load('findings.tsx') };
}
const finding = (obj, rule = 'R1', sev = 2) => ({ ruleId: rule, ruleName: `Rule ${rule}`, category: 'Naming', severity: sev, objectRef: `column:Sales/${obj}`, objectName: obj, message: 'A finding.' });
const holdOf = (tree, obj) => findAll(tree, (el) => el.type?.name === 'Provider' && el.props?.value?.scope).find((h) => h.props.value.scope.includes(`Sales/${obj}`))?.props.value;
const openReason = async (pg, F, hold, more = {}) => {
  F.WaiveHold.current = hold;
  const box = pg.mount(() => F.WaiveControl({ onWaive: async () => true, onUnwaive() {}, ...more }));
  await settleAll(); box.rerender();
  return box;
};

test('B2 other writer: a reason typed on C survives two rescans that fix A and B and reorder the rule, then is named when the assistant accepts C itself', async () => {
  const { pg, F } = findingsPage();
  const live = { rows: [finding('A'), finding('B'), finding('C')] };
  const view = pg.mount(() => F.GroupedFindings({ rows: live.rows, holdAs: 'bpa', renderActions: () => ({ type: 'span', props: {} }) }));
  let box = await openReason(pg, F, holdOf(view.result, 'C'));
  find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))).props.onClick(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: 'Legacy name kept for reports' } }); await settleAll(); box.rerender();
  box.unmount();   // the rescan shifts the rows (a remount at the old index)
  live.rows = [finding('C'), finding('Z')]; view.rerender(); await settleAll(); view.rerender();
  box = await openReason(pg, F, holdOf(view.result, 'C'));
  assert.equal(find(box.result, (el) => el.type === 'input')?.props.value, 'Legacy name kept for reports', 'the box opens on the reason under C after the shift');
  box.unmount();
  live.rows = [finding('Z')]; view.rerender(); await settleAll(); view.rerender();   // the assistant accepted C
  const orphan = find(view.result, (el) => el.type?.name === 'OrphanDrafts');
  assert.ok(orphan, 'the list names a reason for a finding it does not show');
  const O = pg.load('orphandrafts.tsx');
  const gone = O.orphanScopes(orphan.props.prefix, orphan.props.live, orphan.props.keep);
  assert.equal(gone.length, 1); assert.equal(gone[0].fields[0].draft.value, 'Legacy name kept for reports');
});

test('B2 a reason held on the BPA page is not offered on the AI readiness list (another holdAs)', async () => {
  const { pg, F } = findingsPage();
  const bpa = pg.mount(() => F.GroupedFindings({ rows: [finding('A')], holdAs: 'bpa', renderActions: () => ({ type: 'span', props: {} }) }));
  const box = await openReason(pg, F, holdOf(bpa.result, 'A'));
  find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))).props.onClick(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: 'bpa only reason' } }); await settleAll();
  const air = pg.mount(() => F.GroupedFindings({ rows: [finding('Q')], renderActions: () => ({ type: 'span', props: {} }) }));
  const orphan = find(air.result, (el) => el.type?.name === 'OrphanDrafts');
  const O = pg.load('orphandrafts.tsx');
  assert.equal(O.orphanScopes(orphan.props.prefix, orphan.props.live, orphan.props.keep).length, 0);
});

test('B2 person alone: a reason accepted with ✓ goes out trimmed, the box closes, nothing is held, and a remount shows Accept again', async () => {
  const { pg, F, disk } = findingsPage();
  const view = pg.mount(() => F.GroupedFindings({ rows: [finding('A')], holdAs: 'bpa', renderActions: () => ({ type: 'span', props: {} }) }));
  const hold = holdOf(view.result, 'A');
  const waived = [];
  let box = await openReason(pg, F, hold, { onWaive: async (t) => { waived.push(t); return true; } });
  find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))).props.onClick(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: ' known gap ' } }); await settleAll(); box.rerender();
  find(box.result, (el) => el.type?.name === 'FBtn' && flat(el) === '✓').props.onClick(); await settleAll(); box.rerender();
  assert.deepEqual(waived, ['known gap']);
  assert.equal(find(box.result, (el) => el.type === 'input'), null, 'the box closes');
  assert.ok(!heldAll(disk).includes('known gap'), 'nothing is held');
  box.unmount(); box = await openReason(pg, F, hold);
  assert.ok(find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))), 'a remount shows the button, not an open box');
  // Esc ends typed words too.
  find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))).props.onClick(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: 'never mind' } }); await settleAll(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onKeyDown({ key: 'Escape' }); await settleAll();
  assert.ok(!heldAll(disk).includes('never mind'));
});

// ---- B3. a composer draft's two boxes ----------------------------------------------------------------------------------------------------------------------
// The engine's frozen draft scenes are a private contract file, left out of the public source mirror. The private repo
// (marked by its mirror manifest) always reads them, so a missing file still fails there; the mirror skips these tests.
const privateRepo = existsSync(join(webview, '..', '..', 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
const scenes = privateRepo ? JSON.parse(readFileSync(join(webview, '..', '..', 'docs', 'redesign', 'contracts', 'draft-view.json'), 'utf8')).scenes : null; // mirror-optional: private contract file
const sceneTest = scenes ? test : (name) => test(name, { skip: 'the frozen draft scenes are not in the public source' }, () => {});
function draftPage(disk = makeDisk()) {
  const pg = makePage({ './bridge': bridgeOf(disk, { rpc: async () => null, onDraftChange: () => () => {}, onPlanChange: () => () => {} }), './workflow-run': { WorkflowRunDetail: () => null } }, MODEL);
  return { disk, pg, D: pg.load('composer-draft.tsx') };
}
const handlers = { onEdit() {}, onRun() {}, onApprove() {}, onUndo() {}, onGo() {}, onAskWhy() {}, onCopy() {} };

sceneTest('B3 other writer: Add more words keeps its words while the assistant changes the draft, and names them with Copy when the assistant runs it', async () => {
  const { pg, D } = draftPage();
  const live = { v: scenes.from_words_menu };
  const copied = [];
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers, onCopy: (t) => copied.push(t) }));
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'and the returns table' } }); await settleAll(); body.rerender();
  live.v = { ...scenes.from_words_menu, summary: 'Changed by your assistant' }; body.rerender(); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, 'and the returns table', 'a change to the draft keeps the words in the box');
  live.v = { ...scenes.from_words_menu, state: 'running' }; body.rerender(); await settleAll(); body.rerender();
  const kept = byTestId(body.result, 'cd-refine-kept');
  assert.ok(kept); assert.match(flat(kept), /and the returns table/);
  findAll(kept, (el) => el.type === 'button' && flat(el) === 'Copy')[0].props.onClick();
  assert.deepEqual(copied, ['and the returns table']);
});

sceneTest('B3 person alone: Add more words sent goes out trimmed, the box empties, and no kept note or held words remain', async () => {
  const { pg, D, disk } = draftPage();
  const edits = [];
  const live = { v: scenes.from_words_menu };
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers, onEdit: (a, x) => { edits.push([a, x]); return true; } }));
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'only 2024 ' } }); await settleAll(); body.rerender();
  find(body.result, (el) => el.type === 'form' && el.props?.className === 'cd-refine').props.onSubmit({ preventDefault() {} }); await settleAll(); body.rerender();
  assert.deepEqual(edits, [['refine', { text: 'only 2024' }]]);
  assert.equal(byTestId(body.result, 'cd-refine').props.value, '');
  assert.ok(!heldAll(disk).includes('only 2024'));
  live.v = { ...scenes.from_words_menu, state: 'running' }; body.rerender(); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-words-kept'), null, 'the person\'s own words are not named as kept after they were sent');
});

sceneTest('B3 person alone: Write it yourself saved and taken ends the words, and the draft moving on after shows no kept note', async () => {
  const { pg, D, disk } = draftPage();
  const live = { v: scenes.started_waiting_for_your_assistant };
  const edits = [];
  let resolveEdit;
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers, onEdit: (a, x) => { edits.push([a, x]); return new Promise((r) => { resolveEdit = r; }); } }));
  byTestId(body.result, 'cd-write').props.onClick(); body.rerender();
  find(body.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'DIVIDE ( 1, 2 )' } }); await settleAll(); body.rerender();
  byTestId(body.result, 'cd-writer').props.onSubmit({ preventDefault() {} });
  assert.deepEqual(edits[0], ['write', { step: 'return-rate', blank: 'formula', text: 'DIVIDE ( 1, 2 )' }]);
  resolveEdit(true); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-writer'), null, 'the writer closes once the engine took it');
  assert.ok(!heldAll(disk).includes('DIVIDE'), 'nothing is held');
  live.v = { ...scenes.started_waiting_for_your_assistant, state: 'running' }; body.rerender(); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-words-kept'), null, 'no kept note for words the engine took');
});

// DEFECT (lost words, present on the base a4a522aa too, not fixed by round 2): the page's `call` (composer-draft.tsx:372-380) catches every rpc error and RESOLVES, and the
// writer's submit (composer-draft.tsx:116) closes the box on that resolve. The engine refuses a written part that fails its check ("Not written: ... Change it and write it
// again.", DraftRunner.cs:1895) and refuses a draft another writer ran meanwhile, so the person's formula is closed away and its hold cleared; reopening the writer starts
// from the part's own text (openWriter, composer-draft.tsx:85). The words end without the engine accepting them, against R-B.
sceneTest('B3 person alone: a written formula the engine refuses stays in the box (or is named) after Save', async () => {
  const { pg, D, disk } = draftPage();
  const live = { v: scenes.started_waiting_for_your_assistant };
  // As the real wiring: the call wrapper caught the refusal, showed it, and resolved.
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers, onEdit: async () => undefined }));
  byTestId(body.result, 'cd-write').props.onClick(); body.rerender();
  find(body.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'SUMX ( Returns, Returns[Qty] ) / 0' } }); await settleAll(); body.rerender();
  byTestId(body.result, 'cd-writer').props.onSubmit({ preventDefault() {} }); await settleAll(); body.rerender();
  const shown = JSON.stringify(body.result, (k, v) => (k === '_owner' || typeof v === 'function' ? undefined : v));
  assert.ok(/SUMX \( Returns/.test(shown) || heldAll(disk).includes('SUMX ( Returns'),
    'the refused formula is gone: the writer closed, the hold was cleared, and nothing on the draft names the words');
});

// ---- B4. the Workflows New panel ---------------------------------------------------------------------------------------------------------------------------
const ts = req('typescript');
const anyModule = new Proxy({}, { get: (_t, k) => (k === '__esModule' ? true : k === 'then' ? undefined : Object.assign(function Stub() { return null; }, { displayName: String(k) })) });
function loadOne(pg, file, stubs) {
  const src = readFileSync(join(webview, 'src', file), 'utf8');
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? pg.React : n === 'react-dom' ? { createPortal: (x) => x } : n.startsWith('.') ? anyModule : req(n)), module, module.exports);
  return module.exports;
}
function workflowsWorld(library = []) {
  const pg = makePage({}, MODEL);
  pg.React.createContext = (d) => ({ _v: d, Provider: ({ children }) => children });
  pg.React.useContext = (c) => c._v;
  pg.React.forwardRef = (f) => f;
  const world = { library, onLib: [], calls: [] };
  const rpc = async (method, ...args) => { world.calls.push([method, ...args]); return ({
    listWorkflows: world.library, listWorkflowTemplates: [], getEntitlement: { tier: 'pro' }, getWorkflowPolicy: { bindings: [] },
    getWorkflowEnforcement: { enforced: true }, listWorkflowProfiles: [], getPlanHistory: { rows: [] }, getWorkflow: { name: 'monthly', steps: [] },
    describeWorkflowSteps: { steps: [{ action: 'refresh' }] },
  })[method] ?? null; };
  const wf = loadOne(pg, 'workflows.tsx', {
    './bridge': { rpc, onWorkflowLibraryChange: (f) => { world.onLib.push(f); return () => undefined; }, onEntitlementChange: () => () => undefined },
    './workflowdesign': { DesignMode: function DesignMode() { return null; }, heldDrafts: { list: () => [], drop() {} } }, './workflowheld.mjs': { heldPlaces: () => new Map() },
    './workflowload.mjs': { createDefinitionLoader: () => ({ loadForSelection() {}, loadAfterNotification() {}, reload() {} }) }, './workflowruns.mjs': { liveRuns: () => [] },
    './workflows-model': pg.load('workflows-model.ts'),
    './workflow-rules': { LockMark: () => null, SideDrawer: () => null, TeamRulesDrawer: () => null, hasTeamRules: () => false, requiredWords: () => null },
  });
  world.page = pg.mount(() => wf.WorkflowsView({ runs: { sessionId: 's1' }, onRunUpdate() {}, markOwnRun() {} }));
  world.panelEl = () => find(world.page.result, (el) => el.type?.name === 'NewPanel');
  world.pg = pg;
  return world;
}

test('B4 other writer: a Start blank name typed in the New panel stays when the assistant saves the first workflow', async () => {
  const w = workflowsWorld();
  await settleAll(); w.page.rerender();
  const panel = w.pg.mount(() => { const el = w.panelEl(); return el ? el.type(el.props) : null; });
  await settleAll();
  byTestId(panel.result, 'wf-start-blank').props.onClick(); panel.rerender(); await settleAll();
  byTestId(panel.result, 'wf-blank-title').props.onChange({ target: { value: 'Quarter close' } }); panel.rerender(); await settleAll();
  w.library = [{ name: 'monthly', title: 'Monthly', stepCount: 2, source: 'user' }];
  w.onLib.forEach((f) => f(w.library)); await settleAll(); w.page.rerender();
  assert.ok(w.panelEl(), 'the panel with a typed name stays');
  panel.rerender();
  assert.equal(byTestId(panel.result, 'wf-blank-title').props.value, 'Quarter close');
});

test('B4 person alone: Write the steps opens Author, and back on the list the New panel is not stuck open', async () => {
  const w = workflowsWorld([{ name: 'monthly', title: 'Monthly', stepCount: 2, source: 'user' }]);
  await settleAll(); w.page.rerender();
  find(w.page.result, (el) => el.props?.['data-testid'] === 'wf-new').props.onClick(); await settleAll(); w.page.rerender();
  const panel = w.pg.mount(() => { const el = w.panelEl(); return el ? el.type(el.props) : null; });
  await settleAll();
  byTestId(panel.result, 'wf-describe').props.onChange({ target: { value: 'Refresh Sales every month' } }); panel.rerender(); await settleAll();
  find(panel.result, (el) => el.type === 'form' && el.props?.className === 'sem-wf-desc-row').props.onSubmit({ preventDefault() {} });
  await settleAll(); w.page.rerender(); await settleAll();
  assert.ok(w.calls.some(([m, t]) => m === 'describeWorkflowSteps' && t === 'Refresh Sales every month'), 'the words go out as typed');
  const author = find(w.page.result, (el) => el.type?.name === 'DesignMode');
  assert.ok(author, 'Author opens');
  panel.unmount();   // React unmounts the panel with the list
  author.props.onBack(); await settleAll(); w.page.rerender();
  assert.equal(w.panelEl(), null, 'back on the list, the New panel is closed (not held open by words already used)');
  assert.ok(find(w.page.result, (el) => el.props?.['data-testid'] === 'wf-new'), 'New workflow is offered');
});
