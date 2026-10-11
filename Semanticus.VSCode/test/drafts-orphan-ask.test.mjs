// W3 (join 11, drafts fix round 2): an object known by its NAME (a calculation item, a start plan line) looks "gone" when the assistant renames it.
// Typed words for it used to be dropped on that guess. They are now kept, the page says so, and the person chooses: Keep mine on the renamed object
// (the words move to it WITH the value they were typed over, nothing is saved; round 3 stopped re-basing them onto the target's current value, which hid a change the
// person never read, see test/verify-drafts-check-2.test.mjs V5) or Discard. An object with an id that survives a rename (an insight)
// is followed by that id and never comes here (drafts-class.test.mjs, insight tests: the assistant's rewrite keeps the draft and asks Keep mine / Use the new one).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, settleAll, wait } from './helpers/drafts-mount.mjs';
import { startPlanView, makeStartPlanState } from '../tools/uishot/fixtures/startplan-fixture.mjs';

globalThis.window ??= globalThis;
const MODEL = '/models/Sales.bim';
const calls = [];
const live = { expression: 'SELECTEDMEASURE()', format: '' };
const advStubs = (disk) => ({
  './bridge': { rpc: async (m, ...a) => { calls.push([m, ...a]); if (m === 'getDax') return live.expression; if (m === 'getObjectProperties') return [{ name: 'FormatStringExpression', value: live.format }]; return undefined; },
    onDidChange: () => () => undefined, loadState: disk.loadState, saveState: disk.saveState },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) }, './formatMode': { inferFormatMode: () => 'dynamic' },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) }, './activity': { useClaudeReflection: () => undefined },
});
const item = (ref, name, expression) => ({ ref, name, ordinal: 1, expression, formatStringExpression: '' });
const dax = (tree) => find(tree, (el) => el.type?.name === 'DaxField');
const button = (tree, label) => find(tree, (el) => el.type === 'button' && text(el) === label);

// A group card's real rows come from the panel; the panel's own ask is the OrphanDrafts element it renders over its list, mounted here over the same modules.
function calcWorld() {
  const disk = makeDisk();
  const pg = makePage(advStubs(disk), MODEL);
  const { CalcItemRow } = pg.load('advmodels.tsx');
  const { OrphanDrafts } = pg.load('orphandrafts.tsx');
  const row = (it) => pg.mount(() => CalcItemRow({ item: it, reload: () => undefined, onError: () => undefined }));
  const groups = (list) => list.map((i) => i.ref);
  const ask = (list) => pg.mount(() => OrphanDrafts({
    prefix: 'calcitem|', live: groups(list), what: 'calculation item', nameOf: (r) => r.replace(/^calcitem:/, '').replace('/', ' / '),
    targetsFor: () => list.map((i) => ({ id: i.ref, label: 'Time / ' + i.name })) }));
  return { disk, pg, row, ask };
}

test('W3 calc item: the assistant renames an item whose DAX was typed: the words are kept and asked about, not dropped', async () => {
  const w = calcWorld();
  const a = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(a.result).props.onChange('CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))'); await settleAll();
  a.unmount();
  const list = [item('calcitem:Time/Year to date', 'Year to date', live.expression)];   // the assistant's rename: the old ref is not listed now
  const q = w.ask(list); await settleAll();
  assert.ok(w.pg.drafts.draftStore.get(MODEL, 'calcitem|calcitem:Time/YTD', 'expression'), 'the typed words are still held');
  const notice = find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts');
  assert.ok(notice, 'the page says so');
  assert.match(JSON.stringify(q.result), /Time \/ YTD/, 'it names the item as the page shows it');
  assert.match(JSON.stringify(q.result), /CALCULATE\(SELECTEDMEASURE\(\), DATESYTD/, 'and shows the words');
  assert.equal(button(q.result, 'Keep mine on it').props.disabled, true, 'Keep mine waits for a chosen item');
  assert.ok(button(q.result, 'Discard'));
});

test('W3 calc item: Keep mine on the renamed item moves the words onto it, over the value they were typed over, and the row opens on them (asking, when its value moved)', async () => {
  const w = calcWorld();
  const a = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(a.result).props.onChange('MY DAX'); await settleAll();
  a.unmount();
  const list = [item('calcitem:Time/Year to date', 'Year to date', 'SELECTEDMEASURE() + 0')];
  const q = w.ask(list); await settleAll();
  const writesBefore = calls.length;
  find(q.result, (el) => el.type === 'select').props.onChange({ target: { value: 'calcitem:Time/Year to date' } }); await settleAll();
  button(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  assert.equal(w.pg.drafts.draftStore.get(MODEL, 'calcitem|calcitem:Time/YTD', 'expression'), null, 'the old item holds nothing now');
  const held = w.pg.drafts.draftStore.get(MODEL, 'calcitem|calcitem:Time/Year to date', 'expression');
  assert.equal(held.value, 'MY DAX', 'the words are the same');
  assert.equal(held.base, 'SELECTEDMEASURE()', 'still typed over the DAX the person read (not the renamed item\'s current DAX, which they never saw), so a save names that value');
  assert.equal(calls.slice(writesBefore).filter((c) => /^set|^create|^delete/.test(c[0])).length, 0, 'nothing was written');
  const b = w.row(list[0]); await settleAll();
  assert.equal(dax(b.result).props.value, 'MY DAX', 'the renamed item\'s row opens on the words');
  assert.ok(find(b.result, (el) => el.type?.name === 'EditMovedBanner'), 'and asks Keep mine / Use the new one, because the renamed item holds a different DAX now');
  const q2 = w.ask(list); await settleAll();
  assert.equal(find(q2.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), null, 'the ask is gone');
});

test('W3 calc item: Discard ends them; an item that is still listed is never asked about; a failed read asks nothing', async () => {
  const w = calcWorld();
  const a = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(a.result).props.onChange('MY DAX'); await settleAll();
  a.unmount();
  const still = w.ask([item('calcitem:Time/YTD', 'YTD', live.expression)]); await settleAll();
  assert.equal(find(still.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), null, 'the item is still there: nothing to ask');
  const q = w.ask([item('calcitem:Time/QTD', 'QTD', live.expression)]); await settleAll();
  assert.ok(find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'));
  const { OrphanDrafts } = w.pg.load('orphandrafts.tsx');
  const unread = w.pg.mount(() => OrphanDrafts({ prefix: 'calcitem|', live: null, what: 'calculation item', nameOf: () => '', targetsFor: () => [] })); await settleAll();
  assert.equal(unread.result, null, 'a list that did not read (null) asks nothing and drops nothing');
  button(q.result, 'Discard').props.onClick(); await settleAll();
  assert.equal(w.pg.drafts.draftStore.get(MODEL, 'calcitem|calcitem:Time/YTD', 'expression'), null, 'Discard ends the draft');
});

test('W3 calc item: through the REAL panel: the assistant renames the item, the panel re-reads, and the typed DAX is still held and asked about', async () => {
  const disk = makeDisk();
  let groups = [{ ref: 'table:Time', name: 'Time', precedence: 10, items: [item('calcitem:Time/YTD', 'YTD', live.expression)] }];
  let changed = () => undefined;
  const stubs = advStubs(disk);
  stubs['./bridge'] = { ...stubs['./bridge'], rpc: async (m) => (m === 'listCalculationGroups' ? structuredClone(groups) : undefined), onDidChange: (f) => { changed = f; return () => undefined; } };
  const pg = makePage(stubs, MODEL);
  const { CalcGroupsPanel } = pg.load('advmodels.tsx');
  const h = pg.mount(() => CalcGroupsPanel()); await settleAll(); await wait(10); await settleAll();
  assert.ok(find(h.result, (el) => el.type?.name === 'CalcGroupCard'), 'precondition: the panel lists the group');
  pg.drafts.draftStore.set(MODEL, 'calcitem|calcitem:Time/YTD', 'expression', { value: 'MY DAX', base: live.expression });   // what the item's row holds while it is typed in
  groups = [{ ref: 'table:Time', name: 'Time', precedence: 10, items: [item('calcitem:Time/Year to date', 'Year to date', live.expression)] }];   // the assistant renamed it
  changed(); await wait(400); await settleAll();
  assert.ok(pg.drafts.draftStore.get(MODEL, 'calcitem|calcitem:Time/YTD', 'expression'), 'the panel did not drop the words');
  const askEl = find(h.result, (el) => el.type?.name === 'OrphanDrafts');
  assert.ok(askEl, 'the panel carries the ask');
  const { OrphanDrafts } = pg.load('orphandrafts.tsx');
  const shown = pg.mount(() => OrphanDrafts(askEl.props)); await settleAll();
  assert.ok(find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), 'and, over the list it just read, the ask is showing the kept words');
});

test('W3 calc item: another model\'s words are not this page\'s to ask about', async () => {
  const w = calcWorld();
  w.pg.drafts.draftStore.set('/models/Other.bim', 'calcitem|calcitem:Time/Old', 'expression', { value: 'x', base: 'y' });
  const q = w.ask([item('calcitem:Time/YTD', 'YTD', 'z')]); await settleAll();
  assert.equal(find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), null);
});

// ---- the start plan ----------------------------------------------------------------------------------------------------
const planView = () => JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
function planWorld() {
  const disk = makeDisk();
  const pg = makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } }, MODEL);
  const M = pg.load('startplan-model.ts');
  const { OrphanDrafts } = pg.load('orphandrafts.tsx');
  const ask = (view) => pg.mount(() => OrphanDrafts({ prefix: 'startplan|', live: M.rowNames(view).keys(), what: 'plan line', nameOf: () => 'A line of the plan',
    targetsFor: (f) => M.moveTargets(view, f) }));
  return { pg, M, ask };
}
const findLine = (M, view, pred) => M.linesOf(view).find((l) => pred(M.lineControls(l), l));

test('W3 plan line: the assistant renames a line (its id is made from its name, so the id changes): typed words are kept, named, and can be moved', async () => {
  const w = planWorld(), view = planView();
  const line = findLine(w.M, view, (c) => c.rename);
  assert.ok(line, 'precondition: the fixture has a line that can be renamed');
  const oldId = line.item.id, oldName = w.M.lineTitle(line);
  const { draftStore } = w.pg.drafts;
  draftStore.set(MODEL, 'startplan|' + oldId, 'rename', { value: 'My better name', base: line.item.objectName, label: oldName });
  // The assistant's rename: the same line, a new name, so a new id.
  const renamed = planView(); const same = findLine(w.M, renamed, (c, l) => l.item.id === oldId);
  same.item.id = 'ln-renamed0'; same.item.objectName = 'Assistant name'; same.item.title = 'Assistant name';
  same.item.objectRef = (same.item.objectRef ?? '').replace(/[^:/]*$/, 'Assistant name');
  const q = w.ask(renamed); await settleAll();
  assert.ok(draftStore.get(MODEL, 'startplan|' + oldId, 'rename'), 'the typed name is still held');
  assert.ok(find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), 'the plan says so');
  assert.match(JSON.stringify(q.result), new RegExp(oldName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')), 'and names the line by the title it had');
  const opts = findAll(q.result, (el) => el.type === 'option').map((o) => text(o));
  assert.ok(opts.includes('Assistant name'), `the renamed line is offered: ${JSON.stringify(opts)}`);
  find(q.result, (el) => el.type === 'select').props.onChange({ target: { value: 'ln-renamed0' } }); await settleAll();
  button(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  assert.equal(draftStore.get(MODEL, 'startplan|' + oldId, 'rename'), null);
  const moved = draftStore.get(MODEL, 'startplan|ln-renamed0', 'rename');
  assert.equal(moved.value, 'My better name'); assert.equal(moved.base, line.item.objectName, 'still typed over the name the person read, so the line asks when its name moved');
});

test('W3 plan line: a line that is still in the plan, or a plan not read fresh, asks nothing; Discard ends the words', async () => {
  const w = planWorld(), view = planView();
  const line = findLine(w.M, view, (c) => c.rename);
  const { draftStore } = w.pg.drafts;
  draftStore.set(MODEL, 'startplan|' + line.item.id, 'rename', { value: 'x', base: 'y' });
  assert.equal(find(w.ask(view).result, (el) => el.props?.['data-testid'] === 'orphan-drafts'), null, 'the line is there');
  draftStore.set(MODEL, 'startplan|ln-gone0000', 'rename', { value: 'typed', base: 'was' });
  const q = w.ask(view); await settleAll();
  assert.ok(find(q.result, (el) => el.props?.['data-testid'] === 'orphan-drafts'));
  button(q.result, 'Discard').props.onClick(); await settleAll();
  assert.equal(draftStore.get(MODEL, 'startplan|ln-gone0000', 'rename'), null);
  assert.ok(draftStore.get(MODEL, 'startplan|' + line.item.id, 'rename'), 'the other row\'s words stay');
});

test('W3 the panel: the plan no longer drops a draft on a guess, and names each held draft by its row', async () => {
  const { readFileSync } = await import('node:fs');
  const s = readFileSync(new URL('../webview/src/startplan.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(s, /pruneStartDrafts/, 'no silent prune by id');
  assert.match(s, /<OrphanDrafts prefix="startplan\|"/);
  assert.match(s, /sharedDrafts\.setLabel\(/, 'a held draft carries the title of its row, kept for when the row is gone');
  void wait;
});
