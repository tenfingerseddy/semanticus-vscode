// Verifier on m11/solui round 2 (rule R-B: typed words survive another writer's change, and a person typing alone still saves normally).
// Sites: A1 the calc group card (advmodels.tsx CalcGroupsPanel keys and the precedence box), A2 the data agent element ticks (dataagent.tsx SourceCard),
// A3 the RLS row filter and the Members box (advmodels.tsx TableSecurityRow, Members), A4 the M code refresh policy form (mcode.tsx).
// The fake React mounts one component at a time and never reconciles children, so a card that keeps its open form is proven by its KEY.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const ts = req('typescript');
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

// ---- A1 calc group card --------------------------------------------------------------------------------------------------------------------------------
function advPage(world) {
  const disk = makeDisk();
  world.listeners = []; world.calls = [];
  const pg = makePage({
    './bridge': { rpc: async (method, ...args) => { world.calls.push([method, ...args]); return structuredClone(world.answer(method, ...args) ?? {}); },
      onDidChange: (f) => { world.listeners.push(f); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, MODEL);
  return { pg, disk, adv: pg.load('advmodels.tsx') };
}
const reloadAll = async (world, view) => { world.listeners.forEach((f) => f()); await wait(420); await settleAll(); view.rerender(); await settleAll(); view.rerender(); };
const group = (name, items, precedence = 10) => ({ ref: `table:${name}`, name, precedence, items: items.map((n, i) => ({ ref: `calcitem:${name}/${n}`, name: n, ordinal: i, expression: 'x' })) });
function calcWorld(groups) { const w = { groups }; w.answer = (m) => (m === 'listCalculationGroups' ? w.groups : {}); return w; }
const cardKey = (view, name) => findAll(view.result, (el) => el.type?.name === 'CalcGroupCard').find((c) => c.props.group.name === name)?.key;

test('A1 alone: precedence typed and left saves the typed number over the value shown, and the echo leaves no note', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD'], 10)]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const live = { g: group('Time Intelligence', ['YTD'], 10) };
  const card = pg.mount(() => { const el = findAll(view.result, (x) => x.type?.name === 'CalcGroupCard')[0]; return el.type({ ...el.props, group: live.g }); });
  const box = () => find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric');
  box().props.onChange({ target: { value: '20' } }); await settleAll(); card.rerender();
  w.calls.length = 0;
  box().props.onBlur(); await settleAll();
  assert.deepEqual(w.calls.find((c) => c[0] === 'setCalcGroupPrecedence')?.slice(1), ['table:Time Intelligence', 20, 'human', '10']);
  live.g = group('Time Intelligence', ['YTD'], 20); card.rerender(); await settleAll(); card.rerender();   // the engine took it
  assert.equal(box().props.value, '20');
  assert.equal(byTestId(card.result, 'calc-precedence-moved'), null, 'no moved note after the person\'s own save');
});

test('A1 alone, FRICTION: precedence typed as "020" saves 20, and the echo then shows a moved note over the person\'s own save (no words lost, no write)', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD'], 10)]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const live = { g: group('Time Intelligence', ['YTD'], 10) };
  const card = pg.mount(() => { const el = findAll(view.result, (x) => x.type?.name === 'CalcGroupCard')[0]; return el.type({ ...el.props, group: live.g }); });
  const box = () => find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric');
  box().props.onChange({ target: { value: '020' } }); await settleAll(); card.rerender();
  box().props.onBlur(); await settleAll();
  assert.equal(w.calls.filter((c) => c[0] === 'setCalcGroupPrecedence').length, 1);
  live.g = group('Time Intelligence', ['YTD'], 20); card.rerender(); await settleAll(); card.rerender();
  const note = byTestId(card.result, 'calc-precedence-moved');
  // Recorded behaviour: the note says "The precedence is 20 now. Your 020 is kept." after the person's own save. Use 20 clears it.
  assert.ok(note, 'recorded: a stuck note appears after a normalised echo');
  assert.match(flat(note), /The precedence is 20 now\. Your 020 is kept/);
});

test('A1 card key: a group renamed together with its only item in one reload (one gone, one new, no item name in common)', async () => {
  // DEFECT: (corner) the open "+ Item" form (name, DAX, format) is plain card state, so a new key drops it with no notice.
  const w = calcWorld([group('Time Intelligence', ['YTD']), group('Currency', ['USD', 'EUR'])]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const before = cardKey(view, 'Time Intelligence');
  w.groups = [group('Time Intel', ['Year to date']), group('Currency', ['USD', 'EUR'])];   // the assistant renames the group and its item (two calls inside the 400 ms reload)
  await reloadAll(w, view);
  assert.equal(cardKey(view, 'Time Intel'), before, 'the card with the open Add item form was remounted: its typed name and DAX are gone with no notice');
});

test('A1 card key: two groups with the same item names renamed in one reload', async () => {
  // DEFECT: (corner) neither pairs (each has two matches), so both cards remount and any open "+ Item" form is lost with no notice.
  const w = calcWorld([group('Sales TI', ['Current', 'PY']), group('Cost TI', ['Current', 'PY'])]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const a = cardKey(view, 'Sales TI'), b = cardKey(view, 'Cost TI');
  w.groups = [group('Sales Time', ['Current', 'PY']), group('Cost Time', ['Current', 'PY'])];
  await reloadAll(w, view);
  const keys = [cardKey(view, 'Sales Time'), cardKey(view, 'Cost Time')];
  assert.ok(keys.includes(a) || keys.includes(b), `both cards were remounted (${a},${b} -> ${keys}): an open Add item form on either is lost`);
});

test('A1 card key: a plain rename keeps the card; one rename among unchanged groups keeps the others', async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD']), group('Currency', ['USD'])]);
  const { pg, adv } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const ti = cardKey(view, 'Time Intelligence'), cur = cardKey(view, 'Currency');
  w.groups = [group('TI', ['YTD']), group('Currency', ['USD'])];
  await reloadAll(w, view);
  assert.equal(cardKey(view, 'TI'), ti); assert.equal(cardKey(view, 'Currency'), cur);
});

// ---- A2 data agent ticks ---------------------------------------------------------------------------------------------------------------------------------
function dataAgentCard(disk, world) {
  const bridge = { rpc: world.rpc, copyText: () => undefined, loadState: disk.loadState, saveState: disk.saveState };
  const p = makePage({ './bridge': bridge }, MODEL);
  const src = readFileSync(join(webview, 'src', 'dataagent.tsx'), 'utf8') + '\nexport { SourceCard };\n';
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const stubs = {
    './bridge': bridge, './hooks': { usePersistedState: (k, v) => [v, () => undefined] }, './workflows': { Button: (x) => x },
    './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
    './connection': { useConnection: () => ({}) }, './accountpicker': { AccountPicker: () => null },
    './dataagent-schema.mjs': { isEditableElementTree: () => true, parseElementTree: (j) => [{ display_name: 'Sales', type: 'x', children: [] }].map((e) => ({ ...e, json: j })),
      updateElementSelection: (cur, path, sel) => `${cur}+${path.join('.')}${sel ? 'on' : 'off'}` },
    './copy': { uiLabel: (s) => s }, './drafts': p.load('drafts.ts'), './editbase': p.load('editbase.tsx'),
  };
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? p.React : req(n)), module, module.exports);
  return { ...p, ...module.exports };
}
async function openCard(world) {
  const pg = dataAgentCard(makeDisk(), world);
  const live = { ds: { folder: 'semantic_model-1', type: 'semantic_model', displayName: 'Sales', datasourceJson: 'DOC1' } };
  const card = pg.mount(() => pg.SourceCard({ ds: live.ds, workspaceId: 'w', agentId: 'a', tier: 'pro', authMode: 'x', tenantId: null, onChanged() { world.changed = (world.changed ?? 0) + 1; } }));
  await settleAll(); card.rerender();
  findAll(card.result, (el) => el.type === 'button' && /Show elements/.test(el.props?.['aria-label'] ?? ''))[0].props.onClick(); await settleAll(); card.rerender();
  const tree = () => find(card.result, (el) => el.type?.name === 'ElementTree');
  const flow = () => find(card.result, (el) => el.type?.name === 'WriteFlow');
  return { pg, live, card, tree, flow };
}

test('A2 alone: ticks saved by the person, then the stored schema comes back as Fabric wrote it (normalised): no moved note, no save left open', async () => {
  const world = { rpc: async (...a) => { (world.calls ??= []).push(a); return {}; } };
  const { live, card, tree, flow } = await openCard(world);
  tree().props.onSelectionChange([0], false); await settleAll(); card.rerender();
  assert.ok(flow(), 'the save is offered');
  // The person runs the save: the run names the document the ticks started from.
  await flow().props.run(true);
  assert.deepEqual(world.calls.at(-1).slice(5, 6), ['DOC1+0off']);
  assert.equal(world.calls.at(-1).at(-1), 'DOC1', 'the write names the stored schema the ticks started from');
  flow().props.onCommitted(); await settleAll(); card.rerender();
  live.ds = { ...live.ds, datasourceJson: '{normalised DOC1+0off}' }; card.rerender(); await settleAll(); card.rerender();   // the reload after the save
  assert.equal(byTestId(card.result, 'schema-moved'), null, 'no moved note after the person\'s own save');
  assert.equal(flow(), null, 'nothing is left to save');
  assert.match(flat(card.result), /Choose which fields the agent can use/);
});

test('A2 moved: ticks kept and named while the stored schema moves twice; the person still gets a save after choosing', async () => {
  const world = { rpc: async () => ({}) };
  const { live, card, tree, flow } = await openCard(world);
  tree().props.onSelectionChange([0], false); await settleAll(); card.rerender();
  live.ds = { ...live.ds, datasourceJson: 'DOC2' }; card.rerender(); await settleAll(); card.rerender();
  live.ds = { ...live.ds, datasourceJson: 'DOC3' }; card.rerender(); await settleAll(); card.rerender();
  assert.ok(byTestId(card.result, 'schema-moved'), 'the card names the change');
  assert.equal(tree().props.elements[0].json, 'DOC1+0off', 'the tree shows the person\'s ticks, not the stored schema');
  findAll(card.result, (el) => el.type === 'button' && /Keep my choices/.test(flat(el)))[0].props.onClick(); await settleAll(); card.rerender();
  assert.ok(flow(), 'Save is offered again after Keep my choices');
  assert.match(String(flow().key), /DOC1\+0off/);
});

// ---- A3 RLS row filter and Members ---------------------------------------------------------------------------------------------------------------------
const role = (name, filters = {}) => ({ name, modelPermission: 'Read', members: [], objectPermissions: [],
  tableFilters: Object.entries(filters).map(([table, filterExpression]) => ({ table, filterExpression })) });
const table = { ref: 'table:Sales', name: 'Sales', columns: [{ name: 'Amount' }], isCalculationGroup: false };
function rlsRow(world) {
  const { pg } = advPage(world);
  const adv = pg.load('advmodels.tsx');
  const live = { role: world.roles[0] };
  const row = pg.mount(() => adv.TableSecurityRow({ role: live.role, table, reload: () => undefined, onError: (e) => { world.err = e; } }));
  const dax = () => find(row.result, (el) => el.props?.ariaLabel === 'Row filter for Sales');
  const saveBtn = () => findAll(row.result, (el) => el.type?.name === 'MiniButton').find((b) => /Save filter|Saved|Fix errors/.test(flat(b)));
  return { pg, live, row, dax, saveBtn };
}
function rlsWorld(roles) { const w = { roles }; w.answer = (m) => (m === 'listRoles' ? w.roles : {}); return w; }

test('A3 alone: a typed row filter saves with the value shown as its base, and the echo leaves Saved, no moved banner, no draft mark', async () => {
  const w = rlsWorld([role('Readers', { Sales: '' })]);
  const { pg, live, row, dax, saveBtn } = rlsRow(w);
  await settleAll(); row.rerender();
  dax().props.onChange('[Amount] > 0'); await settleAll(); row.rerender();
  assert.equal(flat(saveBtn()), 'Save filter');
  w.calls.length = 0;
  saveBtn().props.onClick(); await settleAll(); await wait(5); await settleAll();
  const write = w.calls.find((c) => c[0] === 'setTablePermission');
  assert.deepEqual(write?.slice(1), ['Readers', 'table:Sales', '[Amount] > 0', 'human', '']);
  w.roles = [role('Readers', { Sales: '[Amount] > 0' })]; live.role = w.roles[0]; row.rerender(); await settleAll(); row.rerender();
  assert.equal(saveBtn(), undefined, 'no grey Saved button: Save filter shows only after an edit');
  assert.equal(find(row.result, (el) => el.type?.name === 'EditMovedBanner'), null);
  assert.equal(find(row.result, (el) => el.type?.name === 'DraftMark' && el.props.show), null);
  assert.equal(pg.drafts.draftStore.get(MODEL, 'rowfilter|Readers|Sales', '') ?? null, null, 'the draft ended');
});

test('A3 alone: the engine stores the filter trimmed (a normalised echo); what the row shows', async () => {
  const w = rlsWorld([role('Readers', { Sales: '' })]);
  const { live, row, dax, saveBtn } = rlsRow(w);
  await settleAll(); row.rerender();
  dax().props.onChange('[Amount] > 0 '); await settleAll(); row.rerender();
  saveBtn().props.onClick(); await settleAll(); await wait(5); await settleAll();
  w.roles = [role('Readers', { Sales: '[Amount] > 0' })]; live.role = w.roles[0]; row.rerender(); await settleAll(); row.rerender();
  // Recorded: no moved banner. The box keeps the typed text and Save offers it again (dirty by one space) — friction only.
  assert.equal(find(row.result, (el) => el.type?.name === 'EditMovedBanner'), null, 'no moved banner after the person\'s own save');
  w.recorded = { box: dax().props.value, button: flat(saveBtn()) };
  console.log('A3 trimmed echo shows', JSON.stringify(w.recorded));
});

test('A3 moved: the assistant changes this role\'s filter under typed words; the words stay, the newer value is named, Save writes nothing until the person chooses', async () => {
  const w = rlsWorld([role('Readers', { Sales: '[Region] = "EU"' })]);
  const { live, row, dax, saveBtn } = rlsRow(w);
  await settleAll(); row.rerender();
  dax().props.onChange('[Amount] > 0'); await settleAll(); row.rerender();
  w.roles = [role('Readers', { Sales: '[Region] = "US"' })]; live.role = w.roles[0]; row.rerender(); await settleAll(); row.rerender();
  assert.equal(dax().props.value, '[Amount] > 0', 'the typed filter stays');
  const banner = find(row.result, (el) => el.type?.name === 'EditMovedBanner');
  assert.ok(banner, 'the newer value is named');
  assert.equal(banner.props.theirs?.v ?? banner.props.theirs, '[Region] = "US"');
  w.calls.length = 0;
  saveBtn().props.onClick(); await settleAll(); await wait(5); await settleAll();
  assert.equal(w.calls.filter((c) => c[0] === 'setTablePermission').length, 0, 'no write over a value the person has not chosen against');
});

test('A3 Members alone: a typed member is added, the box empties, and the held draft ends', async () => {
  const w = rlsWorld([role('Readers')]);
  const { pg } = advPage(w);
  const adv = pg.load('advmodels.tsx');
  const membersSrc = readFileSync(join(webview, 'src', 'advmodels.tsx'), 'utf8');
  assert.match(membersSrc, /function Members\(/);
  // Members is not exported: reach it through the panel.
  w.answer = (m) => (m === 'listRoles' ? w.roles : m === 'getModelObjects' ? { tables: [] } : m === 'listColumns' || m === 'listMeasures' ? [] : {});
  const view = pg.mount(() => adv.RlsOlsPanel()); await settleAll(); view.rerender(); await settleAll(); view.rerender();
  const el = find(view.result, (x) => x.type?.name === 'Members');
  const box = pg.mount(() => el.type(el.props)); await settleAll();
  find(box.result, (x) => x.type === 'input').props.onChange({ target: { value: 'ana@contoso.com' } }); await settleAll(); box.rerender(); await settleAll();
  assert.equal(pg.drafts.draftStore.get(MODEL, 'rolemember|Readers', '')?.value, 'ana@contoso.com');
  w.calls.length = 0;
  find(box.result, (x) => x.type === 'input').props.onKeyDown({ key: 'Enter' }); await settleAll(); box.rerender(); await settleAll(); box.rerender();
  assert.deepEqual(w.calls.find((c) => c[0] === 'setRoleMember')?.slice(1), ['Readers', 'ana@contoso.com', true]);
  assert.equal(find(box.result, (x) => x.type === 'input').props.value, '');
  assert.equal(pg.drafts.draftStore.get(MODEL, 'rolemember|Readers', '')?.value ?? '', '', 'the held draft ended');
});

// ---- A4 M code refresh policy form ------------------------------------------------------------------------------------------------------------------------
const partition = (t) => ({ name: t, mode: 'Import', sourceType: 'M', source: `let S = Sql.Database("h","d"), T = Table.SelectRows(S, each [OrderDate] >= RangeStart and [OrderDate] < RangeEnd) in T` });
const policy = (tbl, incrementalPeriods, incrementalGranularity, rollingWindowPeriods = 5) => ({
  table: tbl, enabled: true, policyType: 'Basic', mode: 'Import', rollingWindowGranularity: 'Year', rollingWindowPeriods,
  incrementalGranularity, incrementalPeriods, incrementalPeriodsOffset: 0, sourceExpression: null, pollingExpression: null, wiredDateField: 'OrderDate',
});
function mWorld() {
  const w = { names: ['Sales', 'Orders'], policies: { Sales: policy('Sales', 10, 'Day'), Orders: policy('Orders', 2, 'Month') }, calls: [], changed: [] };
  w.doc = () => ({
    header: { compatibilityLevel: 1600 },
    tables: w.names.map((name) => ({ ref: 'table:' + name, name, isCalculated: false, partitions: [partition(name)] })),
    columns: w.names.map((t) => ({ table: t, name: 'OrderDate', dataType: 'DateTime', sourceColumn: 'OrderDate', isCalculated: false })),
    expressions: [
      { name: 'RangeStart', kind: 'M', expression: '#datetime(2020,1,1,0,0,0) meta [IsParameterQuery=true]' },
      { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021,1,1,0,0,0) meta [IsParameterQuery=true]' },
    ],
  });
  w.rpc = async (method, ...args) => {
    w.calls.push([method, ...args]);
    if (method === 'getDocModel') return w.doc();
    if (method === 'getIncrementalRefreshPolicy') return structuredClone(w.policies[args[0].slice('table:'.length)] ?? { table: args[0], enabled: false });
    if (method === 'setIncrementalRefreshPolicy') {
      const t = args[0].slice('table:'.length);
      w.policies[t] = { ...w.policies[t], rollingWindowPeriods: args[2], rollingWindowGranularity: args[3], incrementalPeriods: args[4], incrementalGranularity: args[5], incrementalPeriodsOffset: args[6] };
      return { warning: null };
    }
    return {};
  };
  return w;
}
async function mPage(world) {
  const disk = makeDisk();
  const pg = makePage({
    './bridge': { rpc: world.rpc, onDidChange: (fn) => { world.changed.push(fn); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './meditor': { MEditor: () => null }, './mlang': { formatM: async (s) => ({ ok: true, text: s }) }, './manalysis': { mDiagnostics: async () => [] },
    './connection': { useConnection: () => ({ conn: null, session: { sessionId: 's1' } }) }, './mtransform': { gen: {}, mQuoteIdent: (s) => s },
    './pqtransforms': { SamplePreview: () => null, AppliedStepsPanel: () => null, sourceMName: (c) => c.sourceColumn ?? c.name },
  }, MODEL);
  const { MCodeView } = pg.load('mcode.tsx');
  const h = pg.mount(() => MCodeView({}));
  await settleAll(); await wait(30); await settleAll(); h.rerender();
  const configure = () => find(h.result, (el) => el.type === 'button' && /^Configure/.test(flat(el)));
  if (configure().props['aria-expanded'] !== true) { configure().props.onClick(); await settleAll(); }
  const nums = () => findAll(h.result, (el) => el.type?.name === 'Num');
  const saveBtn = () => find(h.result, (el) => el.type === 'button' && /Save policy|Applying/.test(flat(el)));
  return { pg, h, nums, saveBtn };
}
const policyDraft = (pg, t = 'Sales') => pg.drafts.draftStore.get(MODEL, `mcode|policy|${t}`, '');
const mBanner = (h) => find(h.result, (el) => el.type?.name === 'EditMovedBanner');

test('A4 alone: a typed store window saves, the form shows the saved policy, the draft ends, no moved banner', async () => {
  const world = mWorld();
  const { pg, h, nums, saveBtn } = await mPage(world);
  nums()[0].props.onChange(7); await settleAll(); await wait(10); await settleAll(); h.rerender();
  assert.equal(policyDraft(pg)?.value?.storePeriods, 7);
  assert.ok(saveBtn(), 'Save policy is shown');
  world.calls.length = 0;
  await saveBtn().props.onClick(); await settleAll(); await wait(20); await settleAll(); h.rerender();
  const write = world.calls.find((c) => c[0] === 'setIncrementalRefreshPolicy');
  assert.ok(write, 'the save writes');
  assert.equal(write[3], 7, 'with the typed store window');
  world.changed.forEach((f) => f()); await wait(360); await settleAll(); await wait(30); await settleAll(); h.rerender();
  assert.equal(nums()[0].props.value, 7);
  assert.equal(mBanner(h), null, 'no moved banner after the person\'s own save');
  assert.equal(policyDraft(pg) ?? null, null, 'the draft ended');
});

test('A4 moved: the assistant changes the same table\'s policy under a typed form; the words stay, the newer value is named, Save writes nothing', async () => {
  const world = mWorld();
  const { h, nums, saveBtn } = await mPage(world);
  nums()[0].props.onChange(7); await settleAll(); await wait(10); await settleAll(); h.rerender();
  world.policies.Sales = policy('Sales', 10, 'Day', 9);   // the assistant sets the store window to 9 years
  world.changed.forEach((f) => f()); await wait(360); await settleAll(); await wait(30); await settleAll(); h.rerender();
  assert.equal(nums()[0].props.value, 7, 'the typed value stays');
  assert.ok(mBanner(h), 'the newer policy is named');
  world.calls.length = 0;
  if (saveBtn()) { await saveBtn().props.onClick(); await settleAll(); await wait(20); await settleAll(); }
  assert.equal(world.calls.filter((c) => c[0] === 'setIncrementalRefreshPolicy').length, 0, 'no write before the person chooses');
});
