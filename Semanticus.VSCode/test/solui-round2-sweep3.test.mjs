// Join 11 solui, fix round 2, sweep 3 (rule R-B): words typed in a box or form are never replaced, reset, remounted or hidden by ANOTHER writer's change
// (the assistant over MCP, a live data reload) with no press by the person. One test (or a pair) per site of the sweep: the Spec name boxes on rows that
// moved, the Spec "+ Table" box across a cleared spec, the Workflows New panel across the first saved workflow, the Size by table filter across a model
// change, a result grid's column filters across a new column set, the data agent's generated config across a new publish destination and a failed
// re-read, and the Lineage report ticks across a changed report list. The fake React here cannot remount a child, so a test drives the child that React
// would reuse with the props React would hand it, or asserts the tree keeps the child where it was.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
globalThis.ResizeObserver ??= class { observe() {} disconnect() {} };
const ts = req('typescript');
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

/** Context for the fake React (the shared helper has none): a context's value is whatever the test puts in `_v`. */
function withContext(React) {
  const made = [];
  React.createContext = (d) => { const c = { _v: d, Provider: ({ children }) => children }; made.push(c); return c; };
  React.useContext = (c) => c._v;
  React.forwardRef = (f) => f;
  React.Component ??= class {};
  return made;
}
/** Any module the test does not care about: every name is a component that draws nothing. */
const anyModule = new Proxy({}, { get: (_t, k) => (k === '__esModule' ? true : k === 'then' ? undefined : Object.assign(function Stub() { return null; }, { displayName: String(k) })) });
/** One webview file over the page's React with its imports stubbed, plus names it does not export (`extra`). Relative imports not stubbed draw nothing. */
function loadOne(pg, file, stubs, extra = '') {
  const src = readFileSync(join(webview, 'src', file), 'utf8') + (extra ? `\n${extra}\n` : '');
  const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? pg.React : n === 'react-dom' ? { createPortal: (x) => x } : n.startsWith('.') ? anyModule : req(n)), module, module.exports);
  return module.exports;
}

// ---- 1 and 2: the Spec page ------------------------------------------------------------------------------------------------------------------------------
function specWorld(spec) {
  const disk = makeDisk();
  const w = { snap: { version: 1, source: 'manual', spec }, listeners: [], disk };
  const pg = makePage({
    './bridge': { rpc: async (method) => (method === 'getSpec' ? structuredClone(w.snap) : {}), onSpecChange: (f) => { w.listeners.push(f); return () => undefined; }, pickSpecFile: async () => null, loadState: disk.loadState, saveState: disk.saveState },
    './connectionshub': { listSqlSources: async () => [], openSqlSources() {} },
    './connection': { useConnection: () => ({ openConnections() {} }) },
    './copy': { SIGN_IN_MODES: [] }, './daxeditor': { DaxField: () => null }, './dax': { EMPTY_MODEL: {} }, './advmodels': { FORMAT_PRESETS: [] },
  }, MODEL);
  w.pg = pg;
  w.spec = pg.load('spec.tsx');
  w.view = pg.mount(() => w.spec.SpecView({ session: { modelName: 'M' } }));
  w.held = (scope) => pg.drafts.draftStore.get(MODEL, scope)?.value;
  w.reload = async (next) => { w.snap = { ...w.snap, version: w.snap.version + 1, spec: next }; w.listeners.forEach((f) => f()); await wait(300); await settleAll(); w.view.rerender(); };
  return w;
}
const table = (name, cols = []) => ({ name, role: 'fact', columns: cols.map((c) => ({ name: c, dataType: 'String' })) });
const specOf = (tables, measures = []) => ({ name: 'M', tables, relationships: [], measures, timeIntelligence: [] });
const mountEl = (pg, el) => pg.mount(() => el.type(el.props));

/** The real NameInput, from the real tree (SpecView, SpecEditor, TableCard). */
async function nameInputType(w) {
  await settleAll(); w.view.rerender();
  const editor = mountEl(w.pg, find(w.view.result, (el) => el.type?.name === 'SpecEditor')); await settleAll();
  const card = mountEl(w.pg, find(editor.result, (el) => el.type?.name === 'TableCard')); await settleAll();
  return { NameInput: find(card.result, (el) => el.type?.name === 'NameInput').type, editor, card };
}

test('site 1: a Spec name box that React reuses for ANOTHER table (the assistant removed or reordered tables) shows that table, and the words follow their own table', async () => {
  const w = specWorld(specOf([table('Sales'), table('Orders')]));
  const { NameInput } = await nameInputType(w);
  const moved = find(w.view.result, (el) => el.type?.name === 'MovedWords');
  const notice = mountEl(w.pg, moved);
  // Two index slots, as TableCard key={ti} draws them. Each box is the instance React keeps in that slot.
  const commits = [[], []];
  const slots = [{ t: 'Sales' }, { t: 'Orders' }];
  const box = (i) => w.pg.mount(() => NameInput({ hold: `table:${slots[i].t}|name`, value: slots[i].t, ariaLabel: 'Table name', onCommit: (v) => commits[i].push([slots[i].t, v]) }));
  const b0 = box(0), b1 = box(1);
  await settleAll();
  b0.result.props.onChange({ target: { value: 'Sales typed' } }); await settleAll(); b0.rerender();
  assert.equal(w.held('spec|buf|table:Sales|name'), 'Sales typed');
  // The assistant puts Orders first: slot 0 now draws Orders, slot 1 draws Sales.
  slots[0] = { t: 'Orders' }; slots[1] = { t: 'Sales' };
  b0.rerender(); b1.rerender(); await settleAll(); b0.rerender(); b1.rerender(); notice.rerender();
  assert.equal(b0.result.props.value, 'Orders', 'the box in slot 0 shows the table in slot 0, not the words typed for Sales');
  assert.equal(b1.result.props.value, 'Sales typed', 'the words typed for Sales are on the Sales box');
  assert.equal(findAll(notice.result, (el) => el.props?.['data-testid'] === 'spec-words-keep-mine').length, 0, 'no box names words over a value they were not typed over');
  b0.result.props.onBlur(); await settleAll();
  assert.deepEqual(commits[0], [], 'a blur on the Orders box writes nothing into Orders');
  b1.result.props.onBlur(); await settleAll();
  assert.deepEqual(commits[1], [['Sales', 'Sales typed']], 'the words land on Sales, the table they were typed for');
  // The assistant removes the table under typed words: the box in its slot shows the next table, and the words stay held and listed.
  b1.result.props.onChange({ target: { value: 'Sales again' } }); await settleAll(); b1.rerender();
  slots[1] = { t: 'Returns' }; b1.rerender(); await settleAll(); b1.rerender();
  assert.equal(b1.result.props.value, 'Returns');
  assert.equal(w.held('spec|buf|table:Sales|name'), 'Sales again', 'the words of the table that went are kept, for the list of words with no box');
});

test('site 1: renaming a table in its own box carries the words held on its other boxes to the new name', async () => {
  const w = specWorld(specOf([table('Sales', ['Amount'])], [{ table: 'Sales', name: 'Total', dax: '1' }]));
  w.pg.drafts.draftStore.set(MODEL, 'spec|buf|table:Sales|entity', '', { value: 'fact_sales', base: '' });
  w.pg.drafts.draftStore.set(MODEL, 'spec|buf|measure:Sales/Total|format', '', { value: '0.0', base: '' });
  const { card } = await nameInputType(w);
  find(card.result, (el) => el.type?.name === 'NameInput' && el.props.ariaLabel === 'Table name').props.onCommit('Sales EU');
  await settleAll();
  assert.equal(w.held('spec|buf|table:Sales EU|entity'), 'fact_sales');
  assert.equal(w.held('spec|buf|measure:Sales EU/Total|format'), '0.0');
  assert.equal(w.held('spec|buf|table:Sales|entity'), undefined);
});

test('site 2: the "+ Table" name box is held, opens again on its words after the spec went and came back, and is listed while there is no spec', async () => {
  const w = specWorld(specOf([table('Sales')]));
  await settleAll(); w.view.rerender();
  const editor = mountEl(w.pg, find(w.view.result, (el) => el.type?.name === 'SpecEditor')); await settleAll();
  const adderEl = find(editor.result, (el) => el.type?.name === 'Section' && el.props.action).props.action;
  const adder = mountEl(w.pg, adderEl);
  find(adder.result, (el) => el.type === 'button').props.onClick(); await settleAll();
  find(adder.result, (el) => el.type === 'input').props.onChange({ target: { value: 'Customers' } }); await settleAll();
  // The assistant clears the spec: the editor (and the box) go, the wizard shows.
  adder.unmount(); editor.unmount();
  await w.reload(null);
  assert.ok(find(w.view.result, (el) => el.type?.name === 'SpecWizard'), 'no spec: the wizard shows');
  const orphans = mountEl(w.pg, find(w.view.result, (el) => el.type?.name === 'OrphanDrafts'));
  const row = byTestId(orphans.result, 'orphan-spec|buf|new-table|name');
  assert.ok(row, 'the typed table name is listed while its box is not on the page');
  assert.match(flat(row), /Customers/);
  // The assistant sets a spec again: the box opens on the words.
  await w.reload(specOf([table('Sales')]));
  const editor2 = mountEl(w.pg, find(w.view.result, (el) => el.type?.name === 'SpecEditor')); await settleAll();
  const adder2 = mountEl(w.pg, find(editor2.result, (el) => el.type?.name === 'Section' && el.props.action).props.action); await settleAll();
  assert.equal(find(adder2.result, (el) => el.type === 'input')?.props.value, 'Customers');
  // Cancel is the person's press: it ends the words.
  find(adder2.result, (el) => el.type?.name === 'IconBtn').props.onClick(); await settleAll();
  assert.equal(w.held('spec|buf|new-table|name'), undefined);
});

// ---- 3: Workflows New panel ----------------------------------------------------------------------------------------------------------------------------
test('site 3: words typed in the New panel stay on the page when the assistant saves the first workflow', async () => {
  const pg = makePage({}, MODEL);
  withContext(pg.React);
  const world = { library: [], onLib: [] };
  const rpc = async (method) => ({
    listWorkflows: world.library, listWorkflowTemplates: [], getEntitlement: { tier: 'pro' }, getWorkflowPolicy: { bindings: [] },
    getWorkflowEnforcement: { enforced: true }, listWorkflowProfiles: [], getPlanHistory: { rows: [] }, getWorkflow: { name: 'monthly', steps: [] },
  })[method] ?? null;
  const wf = loadOne(pg, 'workflows.tsx', {
    './bridge': { rpc, onWorkflowLibraryChange: (f) => { world.onLib.push(f); return () => undefined; }, onEntitlementChange: () => () => undefined },
    './workflowdesign': { DesignMode: () => null, heldDrafts: { list: () => [], drop() {} } },
    './workflowheld.mjs': { heldPlaces: () => new Map() },
    './workflowload.mjs': { createDefinitionLoader: () => ({ loadForSelection() {}, loadAfterNotification() {}, reload() {} }) },
    './workflowruns.mjs': { liveRuns: () => [] },
    './workflows-model': pg.load('workflows-model.ts'),
    './workflow-rules': { LockMark: () => null, SideDrawer: () => null, TeamRulesDrawer: () => null, hasTeamRules: () => false, requiredWords: () => null },
  });
  const page = pg.mount(() => wf.WorkflowsView({ runs: { sessionId: 's1' }, onRunUpdate() {}, markOwnRun() {} }));
  await settleAll(); page.rerender();
  const panelEl = () => find(page.result, (el) => el.type?.name === 'NewPanel');
  assert.ok(panelEl(), 'an empty library shows the New panel');
  const panel = pg.mount(() => { const el = panelEl(); return el ? el.type(el.props) : null; });
  await settleAll();
  byTestId(panel.result, 'wf-describe').props.onChange({ target: { value: 'Every month refresh Sales' } }); await settleAll();
  // The assistant saves the first workflow.
  world.library = [{ name: 'monthly', title: 'Monthly', stepCount: 2, source: 'user' }];
  world.onLib.forEach((f) => f(world.library)); await settleAll(); page.rerender();
  assert.ok(panelEl(), 'the panel with typed words stays on the page');
  panel.rerender(); await settleAll();
  assert.equal(byTestId(panel.result, 'wf-describe').props.value, 'Every month refresh Sales');
  assert.ok(panelEl().props.onClose, 'and it can be closed now, by the person');
  // Close is the person's press: the panel goes.
  panelEl().props.onClose(); await settleAll(); page.rerender(); panel.rerender(); await settleAll(); page.rerender();
  assert.equal(panelEl(), null);
});

test('site 3: a New panel with nothing typed still closes when the first workflow arrives', async () => {
  const pg = makePage({}, MODEL);
  withContext(pg.React);
  const world = { library: [], onLib: [] };
  const rpc = async (method) => ({ listWorkflows: world.library, listWorkflowTemplates: [], getEntitlement: { tier: 'pro' }, getWorkflowPolicy: { bindings: [] }, getWorkflowEnforcement: { enforced: true }, listWorkflowProfiles: [], getPlanHistory: { rows: [] }, getWorkflow: { name: 'monthly', steps: [] } })[method] ?? null;
  const wf = loadOne(pg, 'workflows.tsx', {
    './bridge': { rpc, onWorkflowLibraryChange: (f) => { world.onLib.push(f); return () => undefined; }, onEntitlementChange: () => () => undefined },
    './workflowdesign': { DesignMode: () => null, heldDrafts: { list: () => [], drop() {} } }, './workflowheld.mjs': { heldPlaces: () => new Map() },
    './workflowload.mjs': { createDefinitionLoader: () => ({ loadForSelection() {}, loadAfterNotification() {}, reload() {} }) }, './workflowruns.mjs': { liveRuns: () => [] },
    './workflows-model': pg.load('workflows-model.ts'),
    './workflow-rules': { LockMark: () => null, SideDrawer: () => null, TeamRulesDrawer: () => null, hasTeamRules: () => false, requiredWords: () => null },
  });
  const page = pg.mount(() => wf.WorkflowsView({ runs: { sessionId: 's1' }, onRunUpdate() {}, markOwnRun() {} }));
  await settleAll(); page.rerender();
  const panel = pg.mount(() => { const el = find(page.result, (e) => e.type?.name === 'NewPanel'); return el ? el.type(el.props) : null; });
  await settleAll();
  world.library = [{ name: 'monthly', title: 'Monthly', stepCount: 2, source: 'user' }];
  world.onLib.forEach((f) => f(world.library)); await settleAll(); page.rerender();
  assert.equal(find(page.result, (e) => e.type?.name === 'NewPanel'), null);
  panel.unmount();
});

// ---- 5: Size by table filter --------------------------------------------------------------------------------------------------------------------------
test('site 5: the Size by table overview stays on the page through a model change, so its "Filter tables" box keeps its words', async () => {
  const pg = makePage({}, MODEL);
  withContext(pg.React);
  const world = { didChange: [], graph: { tables: [{ name: 'Sales', columns: 2, measures: 1 }], relationships: [] }, failGraph: false };
  const rpc = async (method) => {
    if (method === 'getModelGraph') { if (world.failGraph) throw new Error('no'); return structuredClone(world.graph); }
    return method === 'listColumns' ? [] : {};
  };
  const app = loadOne(pg, 'App.tsx', {
    './bridge': { rpc, onDidChange: (f) => { world.didChange.push(f); return () => undefined; }, onReconnect: () => () => undefined, loadState: (k, d) => d, saveState() {} },
    './connection': { useConnection: () => ({ conn: null, session: { sessionId: 's1' }, context: null }) },
    './activity': { useClaudeReflection: () => undefined },
  }, 'export { StorageTabStateProvider };');
  const holder = pg.mount(() => app.StorageTabStateProvider({ active: true, children: null }));
  await settleAll(); holder.rerender();
  const value = () => holder.result.props.value;
  assert.ok(value().overviewMeta, 'the overview has the graph');
  world.didChange.forEach((f) => f());   // another writer changed the model: the graph is read again
  holder.rerender();
  assert.equal(value().meta, null, 'the fresh graph is not here yet (a stale one never selects an object)');
  assert.ok(value().overviewMeta, 'the overview keeps the last graph it drew, so its filter box is not remounted');
  await settleAll(); holder.rerender();
  assert.ok(value().meta && value().overviewMeta === value().meta);
  // The page draws the overview from the kept graph.
  assert.match(readFileSync(join(webview, 'src', 'App.tsx'), 'utf8'), /\{!report && overviewMeta && \(/);
});

// ---- 6: result grid column filters --------------------------------------------------------------------------------------------------------------------
test('site 6: a new column set keeps the open filter box with its typed value and sets applied filters aside, saying so; the person brings them back', async () => {
  const pg = makePage({ '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) } }, MODEL);
  const { ResultGrid } = pg.load('grid.tsx');
  const live = { columns: [{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }], rows: [['a', 1], ['b', 9]] };
  const grid = pg.mount(() => ResultGrid({ columns: live.columns, rows: live.rows }));
  await settleAll(); grid.rerender();
  const ev = { stopPropagation() {}, currentTarget: { getBoundingClientRect: () => ({ left: 0, bottom: 0 }) } };
  const pop = () => find(grid.result, (el) => el.type?.name === 'FilterPopover');
  // Apply a filter on Amount.
  find(grid.result, (el) => el.props?.['data-funnel'] === 1).props.onClick(ev); await settleAll();
  pop().props.onApply({ kind: 'number', op: 'gt', a: 5 }); await settleAll();
  assert.match(flat(grid.result), /Amount/);
  // Open the filter box on Name (the person is typing a value in it).
  find(grid.result, (el) => el.props?.['data-funnel'] === 0).props.onClick(ev); await settleAll();
  assert.equal(pop().props.name, 'Name');
  // The assistant's query changes the column set: Name moves, Amount is gone.
  live.columns = [{ name: 'Region', type: 'string' }, { name: 'Name', type: 'string' }]; live.rows = [['x', 'a']];
  grid.rerender(); await settleAll(); grid.rerender();
  assert.ok(pop(), 'the open filter box stays open, so the value typed in it stays');
  assert.equal(pop().props.name, 'Name');
  const aside = byTestId(grid.result, 'grid-filters-aside');
  assert.ok(aside, 'the filter that has no column now is kept and named');
  assert.match(flat(aside), /Amount/);
  // Amount comes back: the person brings the filter back.
  live.columns = [{ name: 'Name', type: 'string' }, { name: 'Amount', type: 'double' }]; live.rows = [['a', 1], ['b', 9]];
  grid.rerender(); await settleAll(); grid.rerender();
  findAll(byTestId(grid.result, 'grid-filters-aside'), (el) => el.type === 'button').find((b) => /Use them again/.test(flat(b))).props.onClick(); await settleAll();
  assert.equal(byTestId(grid.result, 'grid-filters-aside'), null);
  assert.match(flat(grid.result), /1 of 2/);
});

// ---- 7: data agent --------------------------------------------------------------------------------------------------------------------------------------
function dataAgent(world) {
  const disk = makeDisk();
  const pg = makePage({}, MODEL);
  const bridge = { rpc: world.rpc, copyText: () => undefined, loadState: disk.loadState, saveState: disk.saveState };
  const mod = loadOne(pg, 'dataagent.tsx', {
    './bridge': bridge, './hooks': { usePersistedState: (k, v) => pg.React.useState(v) }, './workflows': { Button: (x) => x },
    './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
    './connection': { useConnection: () => ({ context: world.context, openConnections() {} }) }, './accountpicker': { AccountPicker: () => null },
    './dataagent-schema.mjs': { isEditableElementTree: () => true, parseElementTree: (j) => [{ display_name: 'Sales', type: 'x', children: [], json: j }],
      updateElementSelection: (cur, path, sel) => `${cur}+${path.join('.')}${sel ? 'on' : 'off'}` },
    './copy': { uiLabel: (s) => s, mayStartSignIn: () => true }, './drafts': pg.drafts, './editbase': pg.load('editbase.tsx'),
  }, 'export { ScopePanel };');
  return { pg, ...mod };
}
const detail = { info: { id: 'a', name: 'Agent' }, draft: { dataSources: [] }, published: null };

test('site 7: ticks on a generated config stay when the publish destination changes; the panel says so and nothing is applied until the person chooses', async () => {
  const world = { context: { publishing: { connectionId: 'c1', modelName: 'Sales A' }, editing: { modelName: 'M' } },
    rpc: async (m) => (m === 'generateDataAgentConfig' ? { datasourceJson: 'GEN', displayName: 'M' } : {}) };
  const d = dataAgent(world);
  const panel = d.pg.mount(() => d.ScopePanel({ workspaceId: 'w', agentId: 'a', detail, tier: 'pro', authMode: 'x', tenantId: null, onChanged() {} }));
  await settleAll();
  findAll(panel.result, (el) => el.type?.name === 'Button').find((b) => /Add this model/.test(flat(b))).props.onClick(); await settleAll();
  find(panel.result, (el) => el.type?.name === 'ElementTree').props.onSelectionChange([0], false); await settleAll();
  const flow = () => find(panel.result, (el) => el.type?.name === 'WriteFlow');
  assert.match(String(flow().key), /GEN\+0off/);
  world.context = { ...world.context, publishing: { connectionId: 'c2', modelName: 'Sales B' } };   // another door changed the destination
  panel.rerender(); await settleAll(); panel.rerender();
  assert.ok(find(panel.result, (el) => el.type?.name === 'ElementTree'), 'the config with the ticks is still on the page');
  const moved = byTestId(panel.result, 'config-dest-moved');
  assert.ok(moved, 'the panel says the destination changed');
  assert.equal(flow(), null, 'nothing is applied over a destination the person has not seen');
  findAll(moved, (el) => el.type === 'button').find((b) => /Keep my ticks/.test(flat(b))).props.onClick(); await settleAll();
  assert.equal(byTestId(panel.result, 'config-dest-moved'), null);
  assert.match(String(flow().key), /GEN\+0off/, 'the ticks the person kept can be applied');
});

test('site 7: a generated config with no ticks still goes when the destination changes', async () => {
  const world = { context: { publishing: { connectionId: 'c1' } }, rpc: async (m) => (m === 'generateDataAgentConfig' ? { datasourceJson: 'GEN', displayName: 'M' } : {}) };
  const d = dataAgent(world);
  const panel = d.pg.mount(() => d.ScopePanel({ workspaceId: 'w', agentId: 'a', detail, tier: 'pro', authMode: 'x', tenantId: null, onChanged() {} }));
  await settleAll();
  findAll(panel.result, (el) => el.type?.name === 'Button').find((b) => /Add this model/.test(flat(b))).props.onClick(); await settleAll();
  world.context = { publishing: { connectionId: 'c2' } }; panel.rerender(); await settleAll(); panel.rerender();
  assert.equal(find(panel.result, (el) => el.type?.name === 'ElementTree'), null);
});

test('site 7: a re-read of the agent that fails keeps the panels (and the words in them) on the page, with the error above them', async () => {
  const world = { context: {}, fail: false };
  world.rpc = async (m) => {
    if (m === 'listWorkspaces') return [{ id: 'w', displayName: 'W' }];
    if (m === 'listDataAgents') return { agents: [{ id: 'a', name: 'Agent' }], observedItemTypes: [] };
    if (m === 'getDataAgent') { if (world.fail) throw new Error('The service did not answer.'); return structuredClone(detail); }
    return {};
  };
  const d = dataAgent(world);
  const page = d.pg.mount(() => d.DataAgentView());
  for (let i = 0; i < 4; i++) { await settleAll(); page.rerender(); }
  const ship = () => find(page.result, (el) => el.type?.name === 'ShipPanel');
  assert.ok(ship(), 'the agent is open');
  world.fail = true;
  ship().props.onChanged(); await settleAll(); page.rerender();
  assert.ok(ship(), 'the Publish panel (and its description box) stays');
  assert.ok(find(page.result, (el) => el.type?.name === 'ScopePanel'), 'the Scope panel (and its ticks) stays');
  assert.match(flat(page.result), /The service did not answer\./);
});

// ---- 8: Lineage report ticks ----------------------------------------------------------------------------------------------------------------------------
test('site 8: the report ticks the person made stay when the chosen report list changes; a report that went loses its tick, a new one is ticked', async () => {
  const pg = makePage({}, MODEL);
  const contexts = withContext(pg.React);
  const lin = loadOne(pg, 'lineage.tsx', {
    './bridge': { rpc: async () => ({}), onDidChange: () => () => undefined, onProgress: () => () => undefined },
    './connection': { useConnection: () => ({ session: null }) },
    './lineage-model': pg.load('lineage-model.ts'),
  }, 'export { DrawerHost };');
  const ctx = contexts.find((c) => c._v === null);
  const state = { sel: new Set(), scope: { reports: [{ id: 'r1' }, { id: 'r2' }, { id: 'r3' }] } };
  const host = pg.mount(() => {
    const [sel, setSel] = pg.React.useState(state.sel);
    state.sel = sel;
    ctx._v = { scope: state.scope, setScope() {}, setUsage() {}, setDrawerOpen() {}, busy: null, setBusy() {}, error: null, setError() {}, refreshScope() {},
      workspaces: null, setWorkspaces() {}, ws: '', setWs() {}, signInMode: 'x', setSignInMode() {}, tenant: '', setTenant() {}, reports: null, setReports() {},
      sel, setSel, consent: false, setConsent() {}, folderPath: '', setFolderPath() {}, gates: { cloudGen: { current: 0 }, tenantOwner: { current: null } } };
    return lin.DrawerHost({ onPick() {}, returningTo: null });
  });
  await settleAll(); host.rerender();
  assert.deepEqual([...state.sel].sort(), ['r1', 'r2', 'r3'], 'every chosen report starts ticked');
  // The person unticks r2.
  ctx._v.setSel(new Set(['r1', 'r3'])); await settleAll();
  // The assistant drops r3 and adds r4.
  state.scope = { reports: [{ id: 'r1' }, { id: 'r2' }, { id: 'r4' }] };
  host.rerender(); await settleAll(); host.rerender();
  assert.deepEqual([...state.sel].sort(), ['r1', 'r4'], 'r2 stays unticked, r3 is gone, r4 is new and ticked');
  // A swap that keeps the count (r4 out, r5 in) is followed too.
  state.scope = { reports: [{ id: 'r1' }, { id: 'r2' }, { id: 'r5' }] };
  host.rerender(); await settleAll(); host.rerender();
  assert.deepEqual([...state.sel].sort(), ['r1', 'r5']);
});
