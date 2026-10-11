// W4 (join 11, drafts fix round 2): "Save them first" (A3) did nothing visible when only COLUMN rows held typed words: it looked for a line row and
// found none (startplan.tsx saveFirst). It now opens the first row that holds words, a line or the column list of its table, and says what it did.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, text, settleAll } from './helpers/drafts-mount.mjs';
import { startPlanView, makeStartPlanState } from '../tools/uishot/fixtures/startplan-fixture.mjs';

globalThis.window ??= globalThis;
const MODEL = '/models/Contoso.SemanticModel';
const view = () => JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
function world() {
  const disk = makeDisk();
  const stubs = {
    './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
    './bridge': { rpc: async () => ({}), onEngineState: () => () => undefined, pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => undefined, runHostCommand: () => undefined, copyText: async () => true,
      onDidChange: () => () => undefined, onActivity: () => () => undefined, onPlanChange: () => () => undefined, onReconnect: () => () => undefined, navigateInStudio: () => undefined, claimConsentAsk: async () => true,
      revealInTree: () => undefined, loadState: disk.loadState, saveState: disk.saveState },
    './pro': { useTier: () => 'pro', upgradeToPro: () => undefined, PENDING_ACCESS: 'Checking your Pro access...' },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => undefined }, './connection': { useConnection: () => ({ openConnections: () => undefined }) },
  };
  const pg = makePage(stubs, MODEL);
  return { pg, M: pg.load('startplan-model.ts'), P: pg.load('startplan.tsx'), D: pg.load('startplan-drafts.ts') };
}
const body = (w, v) => w.pg.mount(() => w.P.StartPlanBody({ view: v, pro: false, busy: null, error: null, sentence: null, changed: new Set(), stale: undefined, onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {},
  onDiscard() {}, onWords: async () => ({}), onKeepName() {} }));
const press = (tree) => find(tree, (el) => /^sp-(approve-build|build)-head$/.test(el.props?.['data-testid'] ?? ''));

test('W4 firstHeldRow: lines come before columns, in the order of the page, and it counts the rows', () => {
  const w = world(), v = view();
  const g = (v.sections ?? []).flatMap((s) => s.groups ?? []).find((x) => (x.columns ?? []).length);
  const col = g.columns[0];
  const only = w.M.firstHeldRow(v, [JSON.stringify([`col:${col.ref}`, 'rename'])]);
  assert.equal(only.column, true); assert.equal(only.table, g.table); assert.equal(only.name, `${col.name} in ${g.table}`); assert.equal(only.count, 1);
  const line = w.M.linesOf(v).find((l) => w.M.lineControls(l).rename);
  const both = w.M.firstHeldRow(v, [JSON.stringify([`col:${col.ref}`, 'rename']), JSON.stringify([line.item.id, 'rename'])]);
  assert.equal(both.column, false); assert.equal(both.id, line.item.id); assert.equal(both.count, 2);
  assert.equal(w.M.firstHeldRow(v, [JSON.stringify(['ln-gone', 'rename'])]), null, 'a row that is not on the page is not found');
});

test('W4 Save them first with words only in a COLUMN: the column list of its table opens and the page says what it did', async () => {
  const w = world(), v = view();
  const g = (v.sections ?? []).flatMap((s) => s.groups ?? []).find((x) => (x.columns ?? []).length);
  const col = g.columns[0];
  w.D.draftStore.set(MODEL, `col:${col.ref}`, 'rename', { text: 'Better name', base: col.name });
  const a = body(w, v); await settleAll();
  const colList = (t) => find(t, (el) => el.type?.name === 'ColumnList' && el.props.columns?.[0]?.ref === col.ref);
  assert.ok(colList(a.result), 'precondition: the table\'s column list is on the page');
  assert.equal(colList(a.result).props.open, false, 'precondition: the column list is shut');
  press(a.result).props.onClick(); await settleAll();
  const ask = find(a.result, (el) => el.type?.name === 'UnsavedWordsAsk');
  assert.ok(ask, 'the build asks about the unsaved words');
  assert.ok(ask.props.names.some((n) => n.includes(col.name)));
  ask.props.onSaveFirst(); await settleAll();
  assert.equal(colList(a.result).props.open, true, 'the column list that holds the words is open now');
  assert.equal(find(a.result, (el) => el.type?.name === 'UnsavedWordsAsk'), null, 'the ask is closed');
  const said = find(a.result, (el) => el.props?.['data-testid'] === 'sp-opened-held');
  assert.ok(said, 'the page says what it did');
  assert.match(JSON.stringify(said.props.children), new RegExp(`Opened ${col.name} in ${g.table}`));
  assert.match(JSON.stringify(said.props.children), /Nothing was built/);
});

test('W4 Save them first with words on a line still opens that line, and says so', async () => {
  const w = world(), v = view();
  const line = w.M.linesOf(v).find((l) => w.M.lineControls(l).rename);
  w.D.draftStore.set(MODEL, line.item.id, 'rename', { text: 'Better', base: line.item.objectName });
  const a = body(w, v); await settleAll();
  press(a.result).props.onClick(); await settleAll();
  find(a.result, (el) => el.type?.name === 'UnsavedWordsAsk').props.onSaveFirst(); await settleAll();
  const row = find(a.result, (el) => el.type?.name === 'LineRow' && el.props.line.item.id === line.item.id);
  assert.equal(row.props.open, true, 'the row is open');
  assert.match(JSON.stringify(find(a.result, (el) => el.props?.['data-testid'] === 'sp-opened-held').props.children), /Opened/);
  void text;
});
