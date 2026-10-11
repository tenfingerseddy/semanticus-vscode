// Join 11 solui, fix round 2, sweep 2 (rule R-B): typed words in a box are never replaced, reset, remounted, hidden or moved to another object by ANOTHER
// writer's change with no press by the person. The boxes here are the ones the two sweeps found in advmodels.tsx, mcode.tsx and pqtransforms.tsx:
//   1. the RLS row filter of a role and table whose role (or table) the assistant renamed: the held words are listed, never held where nobody can see them;
//   2. the add member box: its words belong to the role they were typed for, never to the role the page falls back to when that role goes;
//   3. the classic date table form across a compatibility level change: the same form, not a new one;
//   4. the incremental refresh form of a table the assistant renamed: the held words are listed;
//   5. the Power Query column menu: it stays open across a reconnect, follows its column by name, and never applies to another column;
//   6. a calc group card keeps its key when its group is renamed and also gains or loses an item.
// The fake React here mounts one component at a time and never reconciles children, so a remount is proven by the child's KEY, and kept words by the store.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const flat = (el) => (el == null || el === false ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const MODEL = 'file:/m/Model.bim';

// ---- advmodels.tsx -------------------------------------------------------------------------------------------------------------------------------------
function advPage(world) {
  const disk = makeDisk();
  world.listeners = [];
  const pg = makePage({
    './bridge': { rpc: async (method, ...args) => { (world.calls ??= []).push([method, ...args]); return structuredClone(world.answer(method, ...args) ?? {}); },
      onDidChange: (f) => { world.listeners.push(f); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, MODEL);
  return { pg, disk, adv: pg.load('advmodels.tsx'), orphans: pg.load('orphandrafts.tsx') };
}
const reloadAll = async (world, view) => { world.listeners.forEach((f) => f()); await wait(420); await settleAll(); view.rerender(); await settleAll(); view.rerender(); };

function rlsWorld(roles) {
  const w = { roles, tables: ['Sales'] };
  w.answer = (m) => m === 'listRoles' ? w.roles
    : m === 'listColumns' ? w.tables.map((t) => ({ table: t, name: 'Amount', dataType: 'Decimal' }))
    : m === 'listMeasures' ? [] : m === 'getModelObjects' ? { tables: w.tables.map((name) => ({ name })) } : {};
  return w;
}
const role = (name, extra = {}) => ({ name, modelPermission: 'Read', tableFilters: [], members: [], objectPermissions: [], ...extra });

test('1. RLS: a role the assistant renamed leaves the typed row filter listed (never held unseen); Keep mine moves it to the renamed role and its row opens on it', async () => {
  const world = rlsWorld([role('Readers')]);
  const { pg, adv, orphans } = advPage(world);
  const view = pg.mount(() => adv.RlsOlsPanel());
  await settleAll(); view.rerender(); await settleAll(); view.rerender();
  // A table with no filter is one line (EmptyTableSecurity) until it has a filter or typed words; both carry the role and the table.
  const row = () => find(view.result, (el) => el.type?.name === 'TableSecurityRow' || el.type?.name === 'EmptyTableSecurity');
  assert.ok(row(), 'the Sales row is drawn');
  // The person typed a filter on Readers / Sales (the row holds it in the store, as useEditBase does).
  pg.drafts.draftStore.set(MODEL, 'rowfilter|Readers|Sales', '', { value: '[Amount] > 0', base: '' });
  world.roles = [role('Viewers')];   // the assistant renames the role
  await reloadAll(world, view);
  assert.equal(row().props.role.name, 'Viewers');
  const ask = find(view.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'rowfilter|');
  assert.ok(ask, 'the panel lists row filter words whose role or table is not here now');
  const shown = pg.mount(() => orphans.OrphanDrafts(ask.props)); await settleAll(); shown.rerender();
  const entry = find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-rowfilter|Readers|Sales');
  assert.ok(entry, 'the words typed for Readers / Sales are named');
  assert.match(flat(entry), /\[Amount\] > 0/);
  assert.match(flat(entry), /Readers/);
  const targets = ask.props.targetsFor(['', ''], 'Readers|Sales').map((t) => t.id);
  assert.deepEqual(targets, ['Viewers|Sales']);
  const keyBefore = row().key;
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-pick').props.onChange({ target: { value: 'Viewers|Sales' } }); shown.rerender();
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-keep').props.onClick(); await settleAll(); view.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'rowfilter|Viewers|Sales', '')?.value, '[Amount] > 0', 'the words moved to the renamed role');
  assert.notEqual(row().key, keyBefore, 'the row opens again, so it reads the words that were moved onto it');
  // A row filter typed for a role that is still here is not listed.
  const again = find(view.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'rowfilter|');
  assert.ok([...again.props.live].includes('Viewers|Sales'));
  const after = pg.mount(() => orphans.OrphanDrafts(again.props)); await settleAll();
  assert.equal(after.result, null, 'nothing is left to ask about');
});

test('2. Members: the add member box belongs to its role; when the assistant renames the selected role the words are not aimed at the role the page falls back to', async () => {
  const world = rlsWorld([role('Readers'), role('Admins')]);
  const { pg, adv, orphans } = advPage(world);
  const view = pg.mount(() => adv.RlsOlsPanel());
  await settleAll(); view.rerender(); await settleAll(); view.rerender();
  const members = () => find(view.result, (el) => el.type?.name === 'Members');
  assert.equal(members().props.role.name, 'Readers');
  const keyReaders = members().key;
  assert.ok(keyReaders != null, 'the box is keyed by its role, so another role never inherits what was typed');
  // The person types a member name for Readers.
  const box = pg.mount(() => members().type(members().props)); await settleAll();
  find(box.result, (el) => el.type === 'input').props.onChange({ target: { value: 'ana@contoso.com' } }); await settleAll(); box.rerender(); await settleAll();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'rolemember|Readers', '')?.value, 'ana@contoso.com', 'the typed name is held for Readers');
  world.roles = [role('Admins'), role('Viewers')];   // the assistant renames Readers; the page falls back to the first role
  await reloadAll(world, view);
  assert.equal(members().props.role.name, 'Admins');
  assert.notEqual(members().key, keyReaders, 'the box for Admins is another box');
  const forAdmins = pg.mount(() => members().type(members().props)); await settleAll();
  assert.equal(find(forAdmins.result, (el) => el.type === 'input').props.value, '', 'nothing typed for Readers shows in the Admins box');
  world.calls.length = 0;
  find(forAdmins.result, (el) => el.type === 'input').props.onKeyDown({ key: 'Enter' }); await settleAll();
  assert.equal(world.calls.filter((c) => c[0] === 'setRoleMember').length, 0, 'nothing is added to Admins');
  const ask = find(view.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'rolemember|');
  assert.ok(ask, 'the words typed for the role that is not here now are listed');
  const shown = pg.mount(() => orphans.OrphanDrafts(ask.props)); await settleAll(); shown.rerender();
  assert.match(flat(find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-rolemember|Readers')), /ana@contoso\.com/);
});

test('3. Calendars: the classic date table form is the same form across a compatibility level change (a stable key at the same place)', async () => {
  const w = { supported: false };
  w.answer = (m) => m === 'listCalendars' ? { calendarsSupported: w.supported, compatibilityLevel: w.supported ? 1701 : 1600, calendars: [] }
    : m === 'listColumns' || m === 'listMeasures' ? [] : m === 'getModelObjects' ? { tables: [] } : {};
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalendarsPanel());
  await settleAll(); view.rerender();
  const classic = () => (view.result?.props?.children ?? []).flat().find((el) => el?.type?.name === 'ClassicDateTable');
  const before = classic();
  assert.ok(before, 'the form is a direct child of the page');
  assert.ok(before.key != null, 'the form has a key');
  const rootType = view.result.type;
  w.supported = true;   // the assistant raises the level to 1701
  await reloadAll(w, view);
  const after = classic();
  assert.ok(after, 'the form is still a direct child of the page');
  assert.equal(view.result.type, rootType);
  assert.equal(after.key, before.key, 'the same key: React keeps the form and what was typed in it');
  // The Fixed vs Flexible help fold is a tooltip on the template picker now (minimal pass, A9), so it has no component to key.
});

test('6. a calc group card keeps its key when its group is renamed and also gains or loses an item, and when two groups are renamed at once', async () => {
  const group = (name, items, precedence = 10) => ({ ref: `table:${name}`, name, precedence, items: items.map((n, i) => ({ ref: `calcitem:${name}/${n}`, name: n, ordinal: i, expression: 'x' })) });
  const w = { groups: [group('Time Intelligence', ['YTD']), group('Currency', ['USD', 'EUR'])] };
  w.answer = (m) => m === 'listCalculationGroups' ? w.groups : {};
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel());
  await settleAll(); view.rerender();
  const keyOf = (name) => findAll(view.result, (el) => el.type?.name === 'CalcGroupCard').find((c) => c.props.group.name === name)?.key;
  const ti = keyOf('Time Intelligence'), cur = keyOf('Currency');
  w.groups = [group('Time Intel', ['YTD', 'MTD']), group('Currency', ['USD', 'EUR'])];   // a rename that also adds an item
  await reloadAll(w, view);
  assert.equal(keyOf('Time Intel'), ti, 'a rename plus an added item is still that card');
  w.groups = [group('TI', ['YTD']), group('Money', ['USD', 'EUR'])];   // two renames in one reload (and an item removed)
  await reloadAll(w, view);
  assert.equal(keyOf('TI'), ti); assert.equal(keyOf('Money'), cur);
  // Round 3 (deliberate contract change C-2): one gone and one new group pair up in list order even with no item in common (a rename of the group and all its items looks the same).
  w.groups = [group('Margin', ['A', 'B']), group('Money', ['USD', 'EUR'])];   // TI gone, Margin new with no item in common: read as a rename
  await reloadAll(w, view);
  assert.equal(keyOf('Margin'), ti);
  assert.equal(keyOf('Money'), cur);
});

// ---- mcode.tsx -------------------------------------------------------------------------------------------------------------------------------------------
const partition = (t) => ({ name: t, mode: 'Import', sourceType: 'M', source: `let S = Sql.Database("h","d"), T = Table.SelectRows(S, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in T` });
const policy = (table, incrementalPeriods, incrementalGranularity, rollingWindowPeriods = 5) => ({
  table, enabled: true, policyType: 'Basic', mode: 'Import', rollingWindowGranularity: 'Year', rollingWindowPeriods,
  incrementalGranularity, incrementalPeriods, incrementalPeriodsOffset: 0, sourceExpression: null, pollingExpression: null, wiredDateField: 'OrderDate',
});
function mWorld() {
  const w = { names: ['Sales', 'Orders'], policies: { Sales: policy('Sales', 10, 'Day'), Orders: policy('Orders', 2, 'Month') }, calls: [], changed: [] };
  w.doc = () => ({
    header: { compatibilityLevel: 1600 },
    tables: w.names.map((name) => ({ ref: 'table:' + name, name, isCalculated: false, partitions: [partition(name)] })),
    columns: w.names.map((table) => ({ table, name: 'OrderDate', dataType: 'DateTime', sourceColumn: 'OrderDate', isCalculated: false })),
    expressions: [
      { name: 'RangeStart', kind: 'M', expression: '#datetime(2020,1,1,0,0,0) meta [IsParameterQuery=true]' },
      { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021,1,1,0,0,0) meta [IsParameterQuery=true]' },
    ],
  });
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getDocModel') return w.doc();
    if (method === 'getIncrementalRefreshPolicy') return structuredClone(w.policies[args[0].slice('table:'.length)] ?? { table: args[0], enabled: false });
    return {};
  };
  return w;
}

test('4. M code: a table the assistant renamed leaves the typed refresh policy listed; Keep mine puts it on the renamed table and the form shows it', async () => {
  const disk = makeDisk(), world = mWorld();
  const pg = makePage({
    './bridge': { rpc: world.rpc, onDidChange: (fn) => { world.changed.push(fn); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './meditor': { MEditor: () => null }, './mlang': { formatM: async (s) => ({ ok: true, text: s }) }, './manalysis': { mDiagnostics: async () => [] },
    './connection': { useConnection: () => ({ conn: null, session: { sessionId: 's1' } }) }, './mtransform': { gen: {}, mQuoteIdent: (s) => s },
    './pqtransforms': { SamplePreview: () => null, AppliedStepsPanel: () => null, sourceMName: (c) => c.sourceColumn ?? c.name },
  }, MODEL);
  const { MCodeView } = pg.load('mcode.tsx'), { OrphanDrafts } = pg.load('orphandrafts.tsx');
  const h = pg.mount(() => MCodeView({}));
  await settleAll(); await wait(30); await settleAll(); h.rerender();
  const configure = () => find(h.result, (el) => el.type === 'button' && /^Configure/.test(flat(el)));
  if (configure().props['aria-expanded'] !== true) { configure().props.onClick(); await settleAll(); }
  const nums = () => findAll(h.result, (el) => el.type?.name === 'Num');
  nums()[0].props.onChange(7); await settleAll(); await wait(10); await settleAll();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'mcode|policy|Sales', '')?.value?.storePeriods, 7, 'the typed store window is held for Sales');
  world.names = ['Sales 2', 'Orders']; world.policies['Sales 2'] = world.policies.Sales; delete world.policies.Sales;   // the assistant renames the table
  world.changed.forEach((f) => f()); await wait(360); await settleAll(); await wait(30); await settleAll(); h.rerender();
  const ask = find(h.result, (el) => el.type?.name === 'OrphanDrafts' && el.props.prefix === 'mcode|policy|');
  assert.ok(ask, 'the refresh card lists policy words whose table is not here now');
  const shown = pg.mount(() => OrphanDrafts(ask.props)); await settleAll(); shown.rerender();
  assert.ok(find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-mcode|policy|Sales'), 'the words typed for Sales are named');
  assert.deepEqual(ask.props.targetsFor([''], 'Sales').map((t) => t.id).sort(), ['Orders', 'Sales 2']);
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-pick').props.onChange({ target: { value: 'Sales 2' } }); shown.rerender();
  find(shown.result, (el) => el.props?.['data-testid'] === 'orphan-keep').props.onClick(); await settleAll(); await wait(30); await settleAll(); h.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'mcode|policy|Sales 2', '')?.value?.storePeriods, 7);
  if (configure().props['aria-expanded'] !== true) { configure().props.onClick(); await settleAll(); }
  assert.equal(nums()[0].props.value, 7, 'the form on Sales 2 shows the words that were moved onto it');
});

// ---- pqtransforms.tsx ------------------------------------------------------------------------------------------------------------------------------------
function preview(live) {
  const pg = makePage({ './bridge': { rpc: async () => ({ columns: [], rows: [], rowCount: 0, elapsedMs: 0 }) }, './grid': { ResultGrid: () => null } }, MODEL);
  const pq = pg.load('pqtransforms.tsx');
  const view = pg.mount(() => pq.SamplePreview({ tableName: 'Sales', docColumns: live.cols, mText: 'let S = 1 in S', contextToken: 't', isContextCurrent: () => true,
    connected: live.connected, profileRevision: 0, apply() {}, fail() {} }));
  return { pg, view };
}
const col = (name) => ({ name, type: 'Text', mName: name });
const menuOf = (view) => find(view.result, (el) => el.type?.name === 'ColumnMenu');
const openOn = async (view, ci) => {
  find(view.result, (el) => el.type?.name === 'ResultGrid').props.onColumnMenu(ci, { getBoundingClientRect: () => ({ left: 10, bottom: 20 }) });
  await settleAll(); view.rerender();
};

test('5a. Power Query column menu: a reconnect does not close the open menu (and its typed form) when its column is still the same one', async () => {
  const live = { cols: [col('Region'), col('Amount')], connected: false };
  const { view } = preview(live);
  await settleAll(); view.rerender();
  await openOn(view, 1);
  const key = menuOf(view)?.key;
  assert.equal(menuOf(view).props.col.name, 'Amount');
  live.connected = true; view.rerender(); await settleAll(); view.rerender();
  assert.ok(menuOf(view), 'the menu is still open after the connection came back');
  assert.equal(menuOf(view).key, key, 'and it is the same menu');
  assert.equal(menuOf(view).props.col.name, 'Amount');
});

test('5b. Power Query column menu: it follows its column by name when the list shifts, and when the column is gone it says so, keeps the form and applies nothing', async () => {
  const live = { cols: [col('Region'), col('Amount')], connected: false };
  const { pg, view } = preview(live);
  await settleAll(); view.rerender();
  await openOn(view, 1);
  const key = menuOf(view).key;
  assert.ok(key != null, 'the menu is keyed by its column');
  live.cols = [col('Added'), col('Region'), col('Amount')]; view.rerender(); await settleAll(); view.rerender();   // the assistant adds a column before it
  assert.equal(menuOf(view).props.col.name, 'Amount', 'never retargeted onto the column now at that place');
  assert.equal(menuOf(view).key, key);
  assert.equal(find(view.result, (el) => el.type?.name === 'ResultGrid').props.menuCol, 2, 'the header mark follows the column');
  live.cols = [col('Region')]; view.rerender(); await settleAll(); view.rerender();   // the assistant drops Amount
  const m = menuOf(view);
  assert.ok(m, 'the menu stays open, so the typed form stays on screen');
  assert.equal(m.key, key);
  assert.equal(m.props.col.name, 'Amount');
  assert.equal(m.props.gone, true);
  // The menu itself renders (no read of an undefined column), says the column is gone, and its form cannot apply.
  const runs = [];
  const menu = pg.mount(() => m.type({ ...m.props, run: async (...a) => { runs.push(a); return true; } }));
  const note = find(menu.result, (el) => el.props?.['data-testid'] === 'column-gone');
  assert.ok(note, 'the menu says the column is not here now');
  assert.match(flat(note), /Amount/);
  assert.equal(findAll(menu.result, (el) => el.type?.name === 'MenuItem').length, 0, 'no action is offered on a column that is gone');
});

test('5c. Power Query column menu: an open Rename form on a column that went cannot apply, and keeps its words', async () => {
  const live = { cols: [col('Region'), col('Amount')], connected: false };
  const { pg, view } = preview(live);
  await settleAll(); view.rerender();
  await openOn(view, 1);
  const m0 = menuOf(view);
  const props = { ...m0.props };
  const runs = [];
  const menu = pg.mount(() => m0.type({ ...props, run: async (...a) => { runs.push(a); return true; } }));
  findAll(menu.result, (el) => el.type?.name === 'MenuItem').find((x) => /Rename/.test(flat(x))).props.onClick(); await settleAll(); menu.rerender();
  const form = () => find(menu.result, (el) => el.type?.name === 'RenameForm');
  assert.ok(form());
  props.gone = true; menu.rerender();   // the column went while the form was open
  assert.ok(form(), 'the form stays');
  assert.equal(form().props.blocked, true, 'and cannot apply');
  await form().props.onApply('Revenue');
  assert.equal(runs.length, 0, 'nothing is written against another column, or a column that is not there');
});
