// Join 13, J-I2 (v-j12ui J3): words typed AFTER pressing an action must survive the engine's OK.
// One rule for all of them (R-B): the box ends only if it still holds exactly the words that were sent.
// Each test holds the OK, types more words, lets the OK arrive, and reads what the box holds.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined; globalThis.removeEventListener ??= () => undefined; globalThis.ResizeObserver ??= class { observe() {} disconnect() {} };
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const flat = (el) => el == null || typeof el === 'boolean' ? '' : Array.isArray(el) ? el.map(flat).join('')
  : typeof el === 'object' ? flat(el.props?.children) : String(el);
const press = (page, label) => find(page.result, (el) => /Btn|Button$/.test(el.type?.name ?? '') && flat(el) === label)?.props.onClick();
const pressTag = (page, label) => find(page.result, (el) => el.type === 'button' && flat(el) === label)?.props.onClick();

test('J-I2: createRole keeps the name typed after Add, and still ends when the name is unchanged', async () => {
  for (const typeMore of [true, false]) {
    const disk = makeDisk(); let ok;
    const pg = makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState, onDidChange: () => () => undefined,
      rpc: (m) => { if (m === 'listRoles') return Promise.resolve([]); if (m === 'createRole') return new Promise((r) => { ok = r; }); return Promise.resolve([]); } },
      './hooks': { useFillHeight: () => ({}) }, './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
      './ui': { Caret: () => null }, '@tanstack/react-virtual': { useVirtualizer: () => ({}) } }, MODEL);
    pg.React.createContext = (v) => ({ current: v, Provider: ({ children }) => children }); pg.React.useContext = (c) => c.current;
    const A = pg.load('advmodels.tsx');
    const page = pg.mount(() => A.RlsOlsPanel());
    await settleAll(); page.rerender();
    press(page, '+ Role'); page.rerender();
    find(page.result, (el) => el.type === 'input' && el.props.placeholder === 'role name…').props.onChange({ target: { value: 'Readers' } });
    page.rerender();
    press(page, 'Add'); await settleAll(); page.rerender();
    if (typeMore) { find(page.result, (el) => el.type === 'input' && el.props.placeholder === 'role name…').props.onChange({ target: { value: 'Readers EU' } }); page.rerender(); }
    ok(); await settleAll(); page.rerender();
    const box = find(page.result, (el) => el.type === 'input' && el.props.placeholder === 'role name…');
    if (typeMore) assert.equal(box?.props.value, 'Readers EU', 'the words typed after Add are still in the box');
    else assert.equal(box, null, 'an unchanged name ends the form on the OK');
  }
});

test('J-I2: the plan-history version name stays when it changes after Save', async () => {
  for (const typeMore of [true, false]) {
    const disk = makeDisk(); let ok; const sent = [];
    const pg = makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState, onDidChange: () => () => undefined, onActivity: () => () => undefined,
      onPlanChange: () => () => undefined, onReconnect: () => () => undefined, copyText: async () => true,
      rpc: (m, ...a) => {
        if (m === 'getPlanHistory') return Promise.resolve({ rows: [], more: false, sessionId: 's1', kept: true });
        if (m === 'listVerifiedEdits') return Promise.resolve({ records: [], chainIntact: true });
        if (m === 'listHistoryCheckpoints') return Promise.resolve({ supported: true, checkpoints: [] });
        if (m === 'createHistoryCheckpoint') { sent.push(a[0]); return new Promise((r) => { ok = r; }); }
        return Promise.resolve({});
      } }, './evidence': { Evidence: () => null }, './workflow-run': { RunEntryActions: () => null }, './plan-history.css': {} }, MODEL);
    const P = pg.load('plan-history.tsx');
    const page = pg.mount(() => P.PlanHistory({}));
    try {
    await settleAll(); page.rerender();
    find(page.result, (el) => el.props?.['data-testid'] === 'ph-save').props.onClick(); page.rerender();
    const nameBox = () => find(page.result, (el) => el.type === 'input' && el.props['aria-label'] === 'Version name');
    nameBox().props.onChange({ target: { value: 'Before the audit' } }); page.rerender();
    find(page.result, (el) => el.props?.['data-testid'] === 'ph-save-form').props.onSubmit({ preventDefault() {} });
    await settleAll(); page.rerender();
    assert.deepEqual(sent, ['Before the audit']);
    if (typeMore) { nameBox().props.onChange({ target: { value: 'Before the audit, EU' } }); page.rerender(); }
    ok({}); await settleAll(); page.rerender();
    if (typeMore) assert.equal(nameBox()?.props.value, 'Before the audit, EU', 'the words typed after Save are still in the box');
    else assert.equal(nameBox(), null, 'an unchanged name ends the form on the OK');
    } finally { page.unmount(); }   // the page's own interval would keep the test process alive
  }
});

// ---- workflows: Describe it in words ---------------------------------------------------------------------------------------------------------------------
const ts = req('typescript');
const anyModule = new Proxy({}, { get: (_t, k) => (k === '__esModule' ? true : k === 'then' ? undefined : Object.assign(function Stub() { return null; }, { displayName: String(k) })) });
function loadOne(pg, file, stubs) {
  const js = ts.transpileModule(readFileSync(join(webview, 'src', file), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', js)((n) => (n in stubs ? stubs[n] : n === 'react' ? pg.React : n === 'react-dom' ? { createPortal: (x) => x } : n.startsWith('.') ? anyModule : req(n)), module, module.exports);
  return module.exports;
}
const byTestId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

test('J-I2: the Describe words stay in the box, and nothing opens, when they change after Write the steps', async () => {
  for (const typeMore of [true, false]) {
    const pg = makePage({}, MODEL);
    pg.React.createContext = (d) => ({ _v: d, Provider: ({ children }) => children }); pg.React.useContext = (c) => c._v;
    pg.React.forwardRef = (f) => f;
    let answer; const sent = [];
    const rpc = async (method, ...a) => {
      if (method === 'describeWorkflowSteps') { sent.push(a[0]); return new Promise((r) => { answer = r; }); }
      return ({ listWorkflows: [], listWorkflowTemplates: [], getEntitlement: { tier: 'pro' }, getWorkflowPolicy: { bindings: [] },
        getWorkflowEnforcement: { enforced: true }, listWorkflowProfiles: [], getPlanHistory: { rows: [] } })[method] ?? null;
    };
    const wf = loadOne(pg, 'workflows.tsx', {
      './bridge': { rpc, onWorkflowLibraryChange: () => () => undefined, onEntitlementChange: () => () => undefined },
      './workflowdesign': { DesignMode: () => null, heldDrafts: { list: () => [], drop() {} } }, './workflowheld.mjs': { heldPlaces: () => new Map() },
      './workflowload.mjs': { createDefinitionLoader: () => ({ loadForSelection() {}, loadAfterNotification() {}, reload() {} }) }, './workflowruns.mjs': { liveRuns: () => [] },
      './workflows-model': pg.load('workflows-model.ts'),
      './workflow-rules': { LockMark: () => null, SideDrawer: () => null, TeamRulesDrawer: () => null, hasTeamRules: () => false, requiredWords: () => null },
    });
    const page = pg.mount(() => wf.WorkflowsView({ runs: { sessionId: 's1' }, onRunUpdate() {}, markOwnRun() {} }));
    await settleAll(); page.rerender();
    const panelEl = () => find(page.result, (el) => el.type?.name === 'NewPanel');
    const panel = pg.mount(() => { const el = panelEl(); return el ? el.type(el.props) : null; });
    await settleAll();
    byTestId(panel.result, 'wf-describe').props.onChange({ target: { value: 'Every month refresh Sales' } }); await settleAll(); panel.rerender();
    find(panel.result, (el) => el.type === 'form' && el.props.className === 'sem-wf-desc-row').props.onSubmit({ preventDefault() {} });
    await settleAll(); page.rerender(); panel.rerender();
    assert.deepEqual(sent, ['Every month refresh Sales']);
    if (typeMore) { byTestId(panel.result, 'wf-describe').props.onChange({ target: { value: 'Every month refresh Sales and Costs' } }); await settleAll(); panel.rerender(); }
    answer({ steps: [{ title: 'Refresh Sales' }] }); await settleAll(); page.rerender(); panel.rerender(); await settleAll();
    const view = find(page.result, (el) => el.props?.['data-testid'] === 'wf-page')?.props['data-view'];
    if (typeMore) {
      assert.equal(view, 'list', 'the words changed after the press, so the page does not move away from them');
      assert.equal(byTestId(panel.result, 'wf-describe').props.value, 'Every month refresh Sales and Costs');
    } else assert.equal(view, 'author', 'unchanged words: the steps open in Author, as before');
  }
});
