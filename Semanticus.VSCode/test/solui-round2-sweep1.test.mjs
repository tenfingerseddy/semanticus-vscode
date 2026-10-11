// Join 11 solui, fix round 2, sweep fix 1 (rule R-B): typed words in a box are never replaced, reset, remounted away or hidden by ANOTHER writer's change.
// Six sites from the sweep, five now (sweepA A1 and A6, sweepB 1, 5 and 6, sweepC 3):
//   1. a finding's "Why is this finding acceptable?" box (findings.tsx, used by bpa.tsx and App.tsx): the row key no longer moves when an earlier finding goes, and the
//      words are held by the finding's identity, so a remount brings them back and a finding that is gone names them;
//   2. a Plan row's note, reply or reason and its repeat form (plan-row.tsx, plan-repeat.tsx, plan.tsx): held by the row id across a move between sections, and the open
//      row stays on the list while it is open;
//   3. the start form for a new model from a SQL source (startplan.tsx): held, and named while a start plan holds the page;
//   4. the Author box on a pending change (optimize.tsx): stays with its words when the line is filled, and words for a line that left are named;
//   5. (retired 8 Oct 2026) the pinned answer box of the Model interview card, which the app no longer shows;
//   6. a draft's Add more words box and Write it yourself box (composer-draft.tsx): named when the draft runs, and held across a remount.
// The hook runtime renders one component at a time (children are elements, not rendered), so a remount is an unmount and a fresh mount over the same modules.
import assert from 'node:assert/strict';
import test from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
globalThis.location ??= { search: '?expand=all' };
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const bridgeOf = (disk, more = {}) => ({ loadState: disk.loadState, saveState: disk.saveState, postToHost: () => undefined, keptDraftsByTheExtension: () => ({}), copyText: async () => true, ...more });
/** A context the hook runtime can carry: the value is whatever the test last put in `current`. */
function withContext(React) {
  React.createContext = (d) => { const c = { current: d }; c.Provider = function Provider(p) { return p.children; }; return c; };
  React.useContext = (c) => c.current;
  return React;
}

// ---- 1. a finding's reason box -----------------------------------------------------------------------------------
function findingsPage() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': bridgeOf(disk, { revealInTree: () => undefined }), './ui': { Caret: () => null } }, MODEL);
  withContext(pg.React);
  const F = pg.load('findings.tsx');
  return { disk, pg, F };
}
const finding = (obj, rule = 'R1') => ({ ruleId: rule, ruleName: 'Rule one', category: 'Naming', severity: 2, objectRef: `column:Sales/${obj}`, objectName: obj, message: 'A finding.' });
const findingRows = (tree) => findAll(tree, (el) => typeof el.props?.onContextMenu === 'function' && el.key != null && /pl-8/.test(el.props?.className ?? ''));
const rowKeyOf = (tree, name) => findingRows(tree).find((el) => flat(find(el, (x) => x.type === 'span' && /font-medium/.test(x.props?.className ?? ''))) === name)?.key;

test('1a. a finding row keeps its key when an earlier finding in its rule is fixed or accepted (its open reason box is not remounted)', async () => {
  const { pg, F } = findingsPage();
  const live = { rows: [finding('A'), finding('B'), finding('C')] };
  const view = pg.mount(() => F.GroupedFindings({ rows: live.rows, renderActions: () => ({ type: 'span', props: {} }) }));
  const before = rowKeyOf(view.result, 'B');
  assert.ok(before, 'the row is drawn');
  live.rows = [finding('B'), finding('C')];   // the assistant fixed A
  view.rerender();
  assert.equal(rowKeyOf(view.result, 'B'), before, 'the row of B is the same row');
  live.rows = [finding('Z'), finding('B'), finding('C')];   // a rescan adds a finding before it
  view.rerender();
  assert.equal(rowKeyOf(view.result, 'B'), before);
});

test('1b. the reason typed into Accept is held by the finding: a remount opens it again, a finding that is gone names it, and ✓ ends it', async () => {
  const { pg, F, disk } = findingsPage();
  assert.ok(F.WaiveHold, 'findings.tsx gives each row a hold identity its accept box reads');
  const live = { rows: [finding('A'), finding('B')] };
  const view = pg.mount(() => F.GroupedFindings({ rows: live.rows, renderActions: () => ({ type: 'span', props: {} }), renderRuleActions: () => null }));
  const holds = findAll(view.result, (el) => el.type?.name === 'Provider' && el.props?.value?.scope);
  const holdB = holds.find((h) => /Sales\/B/.test(h.props.value.scope));
  assert.ok(holdB, 'the row of B carries its identity');
  F.WaiveHold.current = holdB.props.value;
  const waived = [];
  const mountBox = () => pg.mount(() => F.WaiveControl({ onWaive: async (t) => { waived.push(t); return true; }, onUnwaive() {} }));
  let box = mountBox();
  find(box.result, (el) => el.type?.name === 'FBtn' && /^Accept$/.test(flat(el))).props.onClick(); box.rerender();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: 'Known gap in the source' } });
  await settleAll(); box.rerender();
  box.unmount();
  box = mountBox(); await settleAll(); box.rerender();
  const input = find(box.result, (el) => el.type === 'input');
  assert.ok(input, 'the box opens again on a remount');
  assert.equal(input.props.value, 'Known gap in the source');
  box.unmount();

  // The assistant fixes B: the words are named on the list, not held where nobody sees them.
  live.rows = [finding('A')];
  view.rerender(); await settleAll(); view.rerender();
  const orphan = find(view.result, (el) => el.type?.name === 'OrphanDrafts');
  assert.ok(orphan, 'the list names words kept for a finding it no longer shows');
  const O = pg.load('orphandrafts.tsx');
  const gone = O.orphanScopes(orphan.props.prefix, orphan.props.live, orphan.props.keep);
  assert.equal(gone.length, 1);
  assert.equal(gone[0].fields[0].draft.value, 'Known gap in the source');

  // B is back (an undo): the box opens on the words; ✓ sends them and ends the draft.
  live.rows = [finding('A'), finding('B')];
  view.rerender();
  box = mountBox(); await settleAll(); box.rerender();
  find(box.result, (el) => el.type?.name === 'FBtn' && flat(el) === '✓').props.onClick(); await settleAll();
  assert.deepEqual(waived, ['Known gap in the source']);
  assert.ok(!JSON.stringify([...disk.map.values()]).includes('Known gap'), 'a sent reason is not held');
});

// ---- 2. a Plan row's note, reply and repeat form ------------------------------------------------------------------
function planRowPage() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': bridgeOf(disk), './pro': { upgradeToPro: () => undefined, useTier: () => 'pro', PENDING_ACCESS: '' }, './toolrow': { anchorUnder: () => ({}) } }, MODEL);
  return { disk, pg, R: pg.load('plan-row.tsx'), RP: pg.load('plan-repeat.tsx') };
}
const task = (over = {}) => ({ id: 'pt-00000001', title: 'Write the notes', source: 'you', who: 'you', doer: 'you', stateWord: 'ready', note: '', ...over });

test('2a. a Plan row that moves to another section (a remount) opens on the note the person typed; a note that moved meanwhile is named', async () => {
  const { pg, R } = planRowPage();
  const live = { item: task() };
  const mount = () => pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction() {}, onGo() {}, onDoer() {}, onNote() {} }));
  let row = mount(); await settleAll(); row.rerender();
  byTestId(row.result, 'plan-note').props.onChange({ target: { value: 'my note' } }); await settleAll(); row.rerender();
  row.unmount();
  live.item = task({ stateWord: 'needs_you', note: 'their note' });   // the assistant moved it to Needs you and wrote a note
  row = mount(); await settleAll(); row.rerender();
  assert.equal(byTestId(row.result, 'plan-note').props.value, 'my note', 'the note box opens on the typed words');
  assert.ok(byTestId(row.result, 'plan-note-moved'), 'the newer note is named');
  // Round 1 (K5): the Mark done reply form is gone with the Queue group, so only the note is held across the move.
  assert.equal(find(row.result, (el) => el.type === 'input' && el.props?.['aria-label'] === 'Your reply'), null);
});

test('2b. the open row stays on the list while it is open: pushed past the cap of Next, or done by the assistant', () => {
  const { R } = planRowPage();
  assert.equal(typeof R.withOpenRow, 'function');
  const a = task({ id: 'pt-a', doer: 'assistant' }), b = task({ id: 'pt-b', doer: 'assistant' }), c = task({ id: 'pt-c', doer: 'assistant', stateWord: 'done' });
  const work = { filtered: false, rows: [], justDone: [], onlyYou: [], next: [a], nextTotal: 2, done: [] };
  assert.deepEqual(R.withOpenRow(work, [a, b], 'pt-b').next.map((r) => r.id), ['pt-a', 'pt-b']);
  assert.deepEqual(R.withOpenRow(work, [a, c], 'pt-c').next.map((r) => r.id), ['pt-a', 'pt-c']);
  assert.equal(R.withOpenRow(work, [a, b], 'pt-a'), work, 'a row on the list is left where it is');
  assert.equal(R.withOpenRow(work, [a], 'pt-gone'), work, 'a row that is not in the plan is not drawn');
  const filtered = { filtered: true, rows: [a], justDone: [], onlyYou: [], next: [], nextTotal: 0, done: [] };
  assert.deepEqual(R.withOpenRow(filtered, [a, b], 'pt-b').rows.map((r) => r.id), ['pt-a', 'pt-b']);
  // A row of yours that is open and past the list goes back to the fold, where its group is.
  const mine = task({ id: 'pt-y' });
  assert.deepEqual(R.withOpenRow(work, [a, mine], 'pt-y').onlyYou.map((r) => r.id), ['pt-y']);
  assert.match(src('plan.tsx'), /withOpenRow\(/, 'the Plan draws the list through it');
  assert.match(src('plan.tsx'), /<PlanRowWordsKept /, 'the Plan names words kept for a row that is gone');
});

test('2c. words typed on a row that is gone from the plan are named on the Plan with Discard', async () => {
  const { pg, R } = planRowPage();
  const live = { item: task() };
  const row = pg.mount(() => R.PlanRowDetail({ item: live.item, items: [live.item], busy: null, onAction() {}, onGo() {}, onDoer() {}, onNote() {} }));
  await settleAll(); row.rerender();
  byTestId(row.result, 'plan-note').props.onChange({ target: { value: 'kept words' } }); await settleAll();
  row.unmount();
  const list = pg.mount(() => R.PlanRowWordsKept({ items: [] }));   // the assistant removed the task
  await settleAll(); list.rerender();
  const kept = byTestId(list.result, 'plan-words-kept');
  assert.ok(kept); assert.match(flat(kept), /Write the notes/); assert.match(flat(kept), /kept words/);
  findAll(kept, (el) => el.type === 'button' && /Discard/.test(flat(el)))[0].props.onClick(); await settleAll(); list.rerender();
  assert.equal(byTestId(list.result, 'plan-words-kept'), null);
  const still = pg.mount(() => R.PlanRowWordsKept({ items: [live.item] }));
  assert.equal(byTestId(still.result, 'plan-words-kept'), null, 'a row still in the plan is not listed');
});

test('2d. the repeat form opens again on the cadence the person set after a remount', async () => {
  const { pg, RP } = planRowPage();
  const row = { id: 'pt-00000001', title: 'Write the notes', source: 'you', doer: 'you' };
  // The Plan row's detail hands the editor a hold on the row's scope (asserted at the end), as it holds the note and the reply. This one is the same, over the draft store.
  const scope = `planrow|${row.id}`, m = MODEL;
  const hold = { read: () => pg.drafts.heldDraft(scope, 'repeat')?.value, write: (c, start) => pg.drafts.draftStore.set(m, scope, 'repeat', { value: c, base: start }), clear: () => pg.drafts.draftStore.clear(m, scope, 'repeat') };
  const mount = () => pg.mount(() => RP.RepeatEditor({ row, repeats: [], isPro: true, busy: null, onAdd() {}, onChange() {}, onDelete() {}, hold }));
  let ed = mount();
  byTestId(ed.result, 'plan-repeat-add').props.onClick(); ed.rerender();
  let formEl = find(ed.result, (el) => el.type?.name === 'CadenceForm');
  let form = pg.mount(() => formEl.type(formEl.props));
  byTestId(form.result, 'plan-repeat-every').props.onChange({ target: { value: '3' } }); await settleAll(); form.rerender();
  form.unmount(); ed.unmount();
  ed = mount(); await settleAll(); ed.rerender();
  formEl = find(ed.result, (el) => el.type?.name === 'CadenceForm');
  assert.ok(formEl, 'the form is open again');
  form = pg.mount(() => formEl.type(formEl.props)); await settleAll(); form.rerender();
  assert.equal(byTestId(form.result, 'plan-repeat-every').props.value, 3);
  // Cancel ends it: the next mount starts closed.
  formEl.props.onCancel(); form.unmount(); ed.unmount();
  ed = mount(); await settleAll(); ed.rerender();
  assert.equal(find(ed.result, (el) => el.type?.name === 'CadenceForm'), null);
  assert.match(src('plan-row.tsx'), /<RepeatEditor row=\{item\} repeats=\{repeats\} hold=\{repeatHold\}/);
  assert.match(src('plan-row.tsx'), /read: \(\) => heldDraft<RepeatCadence>\(scope, 'repeat'\)/, 'the row holds the cadence under its own scope');
  assert.doesNotMatch(src('plan-repeat.tsx'), /from '\.\/drafts'/, 'the editor reads no store of its own (plan-repeat.test.mjs renders it with no stubs)');
});

// ---- 3. the start form ----------------------------------------------------------------------------------------------
function startPage(disk = makeDisk()) {
  const pg = makePage({
    './startplan.css': {}, './bridge': bridgeOf(disk, { rpc: async () => undefined, onEngineState: () => () => {}, onStartPlanChange: () => () => {}, onReconnect: () => () => {}, onDidChange: () => () => {}, pickWorkingCopyFolder: async () => null }),
    './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: '' }, './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
    './connection': { useConnection: () => ({ openConnections: () => {} }) },
  }, MODEL);
  return { disk, pg, S: pg.load('startplan.tsx') };
}

test('3. the start form keeps the endpoint, database, name and folder when the page swaps it out (the assistant started a plan), and names them meanwhile', async () => {
  const { pg, S, disk } = startPage();
  const mount = () => pg.mount(() => S.StartForm({ sources: [], busy: false, error: null, onStart() {}, onAddOnConnections() {}, pro: true }));
  let form = mount();
  const input = (tree, pred) => find(tree, (el) => el.type === 'input' && pred(el.props));
  input(form.result, (p) => p['aria-label'] === 'SQL endpoint').props.onChange({ target: { value: 'sql.example.com' } }); form.rerender();
  input(form.result, (p) => p['aria-label'] === 'Database').props.onChange({ target: { value: 'Sales' } }); form.rerender();
  input(form.result, (p) => p['data-testid'] === 'sp-form-name').props.onChange({ target: { value: 'Sales model' } }); form.rerender();
  input(form.result, (p) => p['data-testid'] === 'sp-form-folder').props.onChange({ target: { value: '/work/sales' } }); await settleAll(); form.rerender();
  form.unmount();
  assert.equal(typeof S.StartFormKept, 'function');
  const kept = pg.mount(() => S.StartFormKept({})); await settleAll(); kept.rerender();
  const note = byTestId(kept.result, 'sp-form-kept');
  assert.ok(note, 'the words are named while the form is not on the page');
  for (const w of ['sql.example.com', 'Sales model', '/work/sales']) assert.match(flat(note), new RegExp(w.replace(/\./g, '\\.')));
  form = mount(); await settleAll(); form.rerender();
  assert.equal(input(form.result, (p) => p['aria-label'] === 'SQL endpoint').props.value, 'sql.example.com');
  assert.equal(input(form.result, (p) => p['aria-label'] === 'Database').props.value, 'Sales');
  assert.equal(input(form.result, (p) => p['data-testid'] === 'sp-form-name').props.value, 'Sales model');
  assert.equal(input(form.result, (p) => p['data-testid'] === 'sp-form-folder').props.value, '/work/sales');
  form.unmount();
  // Discard ends them.
  findAll(byTestId(kept.result, 'sp-form-kept'), (el) => el.type === 'button' && /Discard/.test(flat(el)))[0].props.onClick(); await settleAll(); kept.rerender();
  assert.equal(byTestId(kept.result, 'sp-form-kept'), null);
  form = mount(); await settleAll(); form.rerender();
  assert.equal(input(form.result, (p) => p['aria-label'] === 'SQL endpoint').props.value, '');
  assert.match(src('plan.tsx'), /<StartFormKept /, 'the Plan names them');
  void disk;
});

// ---- 4. the Author box on a pending change ----------------------------------------------------------------------------
function optimizePage(world) {
  const disk = makeDisk();
  const pg = makePage({
    './bridge': bridgeOf(disk, { rpc: async (m) => (m === 'getPlan' ? structuredClone(world.plan) : null), onPlanChange: (f) => { world.listeners.push(f); return () => undefined; } }),
    './sendtask': { GiveToAssistant: () => null, slotTask: () => ({}) }, './copy': { uiLabel: (s) => s },
  }, MODEL);
  return { pg, O: pg.load('optimize.tsx') };
}
const line = (id, over = {}) => ({ id, objectRef: `measure:Sales/${id}`, objectName: id, kind: 'set_description', source: 'ai', category: 'Descriptions', severity: 'info', risk: 'ai', target: 'description', title: `Describe ${id}`, rationale: 'r', status: 'needs_content', ...over });
const planOf = (items) => ({ planId: 'p1', revision: 1, items, summary: { total: items.length, deterministic: 0, bpa: 0, ai: items.length, renames: 0, needsContent: 0, approved: 0, rejected: 0, applied: 0, unverified: 0 } });

test('4a. the Author box stays with its words when the assistant fills the line, and names the value it filled', () => {
  const { pg, O } = optimizePage({ plan: planOf([]), listeners: [] });
  const it = line('L1', { status: 'approved', after: 'Their words' });
  const row = pg.mount(() => O.Row({ it, draft: 'My words', onDraft() {}, onToggle() {}, onSave() {}, onReject() {}, onGive: () => ({}), onApply() {} }));
  const box = find(row.result, (el) => el.type === 'input');
  assert.ok(box, 'the box is still there'); assert.equal(box.props.value, 'My words');
  const note = byTestId(row.result, 'opt-author-moved-L1');
  assert.ok(note); assert.match(flat(note), /Their words/);
});

test('4b. words typed for a line that left the list (a re-proposal) are named, and never shown on another line that took its id', async () => {
  const world = { plan: planOf([line('L1')]), listeners: [] };
  const { pg, O } = optimizePage(world);
  const view = pg.mount(() => O.OptimizeView({}));
  await settleAll(); view.rerender();
  find(view.result, (el) => el.type?.name === 'Row' && el.props.it.id === 'L1').props.onDraft('Sales in dollars'); view.rerender();
  world.listeners.forEach((f) => f(planOf([line('L1', { objectRef: 'measure:Returns/Rate', objectName: 'Rate' })])));   // the assistant re-proposed: L1 is another line now
  await settleAll(); view.rerender();
  assert.equal(find(view.result, (el) => el.type?.name === 'Row' && el.props.it.id === 'L1').props.draft, '', 'the other line does not inherit the words');
  const kept = byTestId(view.result, 'opt-words-kept');
  assert.ok(kept, 'the words are named'); assert.match(flat(kept), /Sales in dollars/);
  findAll(kept, (el) => el.type?.name === 'MiniButton' && /Discard/.test(flat(el)))[0].props.onClick(); view.rerender();
  assert.equal(byTestId(view.result, 'opt-words-kept'), null);
});

// ---- 5. (the pinned answer box left with the Model interview card on 8 Oct 2026; its numbering stays so 6 keeps its name) ------

// ---- 6. a draft's two boxes -------------------------------------------------------------------------------------------
// The engine's frozen draft scenes are a private contract file, left out of the public source mirror. The private repo
// (marked by its mirror manifest) always reads them, so a missing file still fails there; the mirror skips these tests.
const privateRepo = existsSync(join(webview, '..', '..', 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
const scenes = privateRepo ? JSON.parse(readFileSync(join(webview, '..', '..', 'docs', 'redesign', 'contracts', 'draft-view.json'), 'utf8')).scenes : null; // mirror-optional: private contract file
const sceneTest = scenes ? test : (name) => test(name, { skip: 'the frozen draft scenes are not in the public source' }, () => {});
function draftPage() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': bridgeOf(disk, { rpc: async () => null, onDraftChange: () => () => {}, onPlanChange: () => () => {} }), './workflow-run': { WorkflowRunDetail: () => null } }, MODEL);
  return { pg, D: pg.load('composer-draft.tsx') };
}
const handlers = { onEdit() {}, onRun() {}, onApprove() {}, onUndo() {}, onGo() {}, onAskWhy() {}, onCopy() {} };

sceneTest('6a. the Add more words box: words typed when the draft runs are named, and a remount opens the box on them', async () => {
  const { pg, D } = draftPage();
  const live = { v: scenes.from_words_menu };
  const mount = () => pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers }));
  let body = mount();
  byTestId(body.result, 'cd-refine').props.onChange({ target: { value: 'only the sales table' } }); await settleAll(); body.rerender();
  body.unmount();
  body = mount(); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine').props.value, 'only the sales table', 'the box opens on the words after a remount');
  live.v = { ...scenes.from_words_menu, state: 'running' };   // the assistant ran the draft
  body.rerender();
  const kept = byTestId(body.result, 'cd-refine-kept');
  assert.ok(kept, 'the words are named'); assert.match(flat(kept), /only the sales table/);
  findAll(kept, (el) => el.type === 'button' && /Discard/.test(flat(el)))[0].props.onClick(); await settleAll(); body.rerender();
  assert.equal(byTestId(body.result, 'cd-refine-kept'), null);
});

sceneTest('6b. the Write it yourself box: words typed when the draft runs are named', async () => {
  const { pg, D } = draftPage();
  const live = { v: scenes.started_waiting_for_your_assistant };
  const body = pg.mount(() => D.DraftBody({ view: live.v, busy: null, ...handlers }));
  byTestId(body.result, 'cd-write').props.onClick(); body.rerender();
  find(body.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'SUM(Sales[Amount])' } }); await settleAll(); body.rerender();
  live.v = { ...scenes.started_waiting_for_your_assistant, state: 'running' };
  body.rerender();
  const kept = byTestId(body.result, 'cd-write-kept');
  assert.ok(kept, 'the words are named'); assert.match(flat(kept), /SUM\(Sales\[Amount\]\)/);
});
