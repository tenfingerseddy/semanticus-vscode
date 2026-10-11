// Join 11 solui, fix round 2, U2 (rule R-B): typed words survive another writer's change in four more boxes. The verifier's sweep file covers the precedence box and the step
// rename box; this file covers what that file cannot: a calc group card that keeps its open Add item form across a rename of its group, the Keep mine / Use the new choice on the
// precedence box, the step whose words are named when the step is gone, and the data agent's ticks when the stored schema moves.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview, req } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const ts = req('typescript');
const words = (tree) => JSON.stringify(tree, (k, v) => (k === '_owner' || typeof v === 'function' ? undefined : v));

const flat = (el) => (el == null || el === false ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));

function calcPanel(groups) {
  const disk = makeDisk();
  const world = { groups, calls: [], listeners: [] };
  const pg = makePage({
    './bridge': { rpc: async (method, ...args) => { world.calls.push([method, ...args]); return method === 'listCalculationGroups' ? structuredClone(world.groups) : {}; },
      onDidChange: (f) => { world.listeners.push(f); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, 'file:/m/Model.bim');
  const adv = pg.load('advmodels.tsx');
  const view = pg.mount(() => adv.CalcGroupsPanel());
  const reload = async () => { world.listeners.forEach((f) => f()); await wait(420); await settleAll(); view.rerender(); };
  return { pg, world, view, reload };
}
const group = (name, precedence = 10, items = ['YTD']) => ({ ref: `table:${name}`, name, precedence, items: items.map((n, i) => ({ ref: `calcitem:${name}/${n}`, name: n, ordinal: i, expression: 'x' })) });
const cards = (view) => findAll(view.result, (el) => el.type?.name === 'CalcGroupCard');

test('a calc group card keeps its key when its group is renamed (its open Add item form is not remounted); another group does not inherit it; one gone and one new group pair up (round 3)', async () => {
  const p = calcPanel([group('Time Intelligence')]);
  await settleAll(); p.view.rerender();
  const first = cards(p.view)[0].key;
  assert.ok(first, 'the card has a key');
  p.world.groups = [group('Time Intel')];   // the assistant renames the group; its items are the same
  await p.reload();
  assert.equal(cards(p.view).length, 1);
  assert.equal(cards(p.view)[0].key, first, 'the rename keeps the card, so a form open on it stays');
  p.world.groups = [group('Time Intel'), group('Currency', 5, ['USD'])];   // a new group is a new card
  await p.reload();
  const [a, b] = cards(p.view);
  assert.equal(a.key, first); assert.notEqual(b.key, first);
  // Round 3 (deliberate contract change C-2): the page cannot tell a group renamed together with its items from one group deleted and another created, so one gone and one new
  // pair up in list order and the card keeps its key. Nothing is lost either way: the open form and the precedence words are held in the draft store, so the key only keeps focus.
  p.world.groups = [group('Currency', 5, ['USD']), group('Margin', 1, ['A', 'B'])];   // Time Intel gone, Margin new with other items: read as a rename
  await p.reload();
  const keys = cards(p.view).map((c) => c.key);
  assert.ok(keys.includes(first), 'one gone and one new group pair up, so the card keeps its key (the words are held in the store either way)');
});

test('the precedence box: a blur over a moved value writes nothing; Keep mine writes the typed value over the value shown; Use the new one takes it', async () => {
  const p = calcPanel([group('Time Intelligence', 10)]);
  await settleAll(); p.view.rerender();
  const live = { g: group('Time Intelligence', 10) };
  const mount = () => p.pg.mount(() => { const el = cards(p.view)[0]; return el.type({ ...el.props, group: live.g }); });
  const card = mount();
  const box = () => find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric');
  box().props.onChange({ target: { value: '20' } }); await settleAll(); card.rerender();
  live.g = group('Time Intelligence', 5); card.rerender(); await settleAll(); card.rerender();
  p.world.calls.length = 0;
  box().props.onBlur(); await settleAll(); card.rerender();
  assert.equal(p.world.calls.filter((c) => c[0] === 'setCalcGroupPrecedence').length, 0, 'a blur is not a decision');
  assert.equal(box().props.value, '20');
  const note = find(card.result, (el) => el.props?.['data-testid'] === 'calc-precedence-moved');
  assert.ok(note, 'the newer value is named'); assert.match(flat(note), /The precedence is 5 now\. Your 20 is kept/);
  const keepMine = findAll(note, (el) => el.type?.name === 'MiniButton').find((b) => /Keep mine/.test(flat(b)));
  keepMine.props.onClick(); await settleAll();
  const write = p.world.calls.find((c) => c[0] === 'setCalcGroupPrecedence');
  assert.deepEqual(write?.slice(1), ['table:Time Intelligence', 20, 'human', '5'], 'it names the value shown as the one it replaces');
  // Use the new one drops the words.
  card.rerender();
  const useNew = findAll(find(card.result, (el) => el.props?.['data-testid'] === 'calc-precedence-moved'), (el) => el.type?.name === 'MiniButton').find((b) => /Use 5/.test(flat(b)));
  useNew.props.onClick(); await settleAll(); card.rerender();
  assert.equal(box().props.value, '5'); assert.equal(find(card.result, (el) => el.props?.['data-testid'] === 'calc-precedence-moved'), null);
});

test('the step rename box: a step that is gone from the new text leaves the typed name named, and another query closes the box', async () => {
  const pg = makePage({ './bridge': { rpc: async () => ({}) }, './grid': { ResultGrid: () => null } }, 'file:/m/Model.bim');
  const pq = pg.load('pqtransforms.tsx');
  const m = 'let\n    Source = Sql.Database("s", "d"),\n    #"Changed Type" = Source\nin\n    #"Changed Type"';
  const live = { token: 't1', text: m, identity: 'Sales|q1' };
  const view = pg.mount(() => pq.AppliedStepsPanel({ text: live.text, contextToken: live.token, identity: live.identity, isContextCurrent: () => true, onChange() {}, onSelect() {}, onError() {} }));
  const edit = async () => {
    findAll(view.result, (el) => el.type === 'button' && el.props?.title === 'Rename step')[1].props.onClick(); await settleAll(); view.rerender();
    find(view.result, (el) => el.type === 'input' && el.props?.autoFocus).props.onChange({ target: { value: 'Typed columns' } }); await settleAll(); view.rerender();
  };
  await settleAll(); view.rerender(); await edit();
  live.token = 't2'; live.text = 'let\n    Source = Sql.Database("s", "d")\nin\n    Source';   // the step is gone
  view.rerender(); await settleAll(); view.rerender();
  const note = find(view.result, (el) => el.props?.['data-testid'] === 'step-rename-lost');
  assert.ok(note); assert.match(flat(note), /Changed Type[^]*Typed columns/);
  // Another query: the box closes, nothing is left open for the wrong query.
  live.token = 't3'; live.text = m; view.rerender(); await settleAll(); view.rerender(); await edit();
  live.identity = 'Sales|q2'; live.token = 't4'; view.rerender(); await settleAll(); view.rerender();
  assert.equal(find(view.result, (el) => el.type === 'input' && el.props?.autoFocus), null);
});

function dataAgentCard(disk, world) {
  const bridge = { rpc: world.rpc, copyText: () => undefined, loadState: disk.loadState, saveState: disk.saveState };
  const p = makePage({ './bridge': bridge }, 'file:/m/Model.bim');
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

test('data agent: ticks not yet saved stay when the stored schema changes; the card says so and asks; the save still names the document the ticks started from', async () => {
  const disk = makeDisk();
  const pg = dataAgentCard(disk, { rpc: async () => ({}) });
  const live = { ds: { folder: 'semantic_model-1', type: 'semantic_model', displayName: 'Sales', datasourceJson: 'DOC1' } };
  const card = pg.mount(() => pg.SourceCard({ ds: live.ds, workspaceId: 'w', agentId: 'a', tier: 'pro', authMode: 'x', tenantId: null, onChanged() {} }));
  await settleAll(); card.rerender();
  findAll(card.result, (el) => el.type === 'button' && /Show elements/.test(el.props?.['aria-label'] ?? ''))[0].props.onClick(); await settleAll(); card.rerender();
  const tree = () => find(card.result, (el) => el.type?.name === 'ElementTree');
  tree().props.onSelectionChange([0], false); await settleAll(); card.rerender();
  const flow = () => find(card.result, (el) => el.type?.name === 'WriteFlow');
  assert.ok(flow(), 'a tick makes the schema dirty: the save is offered');
  assert.match(String(flow().key), /DOC1\+0off/);

  live.ds = { ...live.ds, datasourceJson: 'DOC2' };   // another writer changed the stored schema
  card.rerender(); await settleAll(); card.rerender();
  assert.ok(find(card.result, (el) => el.props?.['data-testid'] === 'schema-moved'), 'the card names the change');
  assert.equal(flow(), null, 'no save runs over a schema the person has not seen');
  // Use the stored schema: the ticks go, by the person's press.
  findAll(card.result, (el) => el.type === 'button' && /Use the stored schema/.test(flat(el)))[0].props.onClick(); await settleAll(); card.rerender();
  assert.equal(find(card.result, (el) => el.props?.['data-testid'] === 'schema-moved'), null);
  assert.equal(flow(), null);
  // Tick again, move again, Keep my choices: the save is offered over the stored schema shown now.
  tree().props.onSelectionChange([0], true); await settleAll(); card.rerender();
  live.ds = { ...live.ds, datasourceJson: 'DOC3' }; card.rerender(); await settleAll(); card.rerender();
  findAll(card.result, (el) => el.type === 'button' && /Keep my choices/.test(flat(el)))[0].props.onClick(); await settleAll(); card.rerender();
  assert.ok(flow(), 'kept ticks can be saved'); assert.match(String(flow().key), /DOC2\+0on/);
});

test('data agent: a card with nothing ticked still follows the stored schema', async () => {
  const disk = makeDisk();
  const pg = dataAgentCard(disk, { rpc: async () => ({}) });
  const live = { ds: { folder: 'semantic_model-1', type: 'semantic_model', displayName: 'Sales', datasourceJson: 'DOC1' } };
  const card = pg.mount(() => pg.SourceCard({ ds: live.ds, workspaceId: 'w', agentId: 'a', tier: 'pro', authMode: 'x', tenantId: null, onChanged() {} }));
  await settleAll(); card.rerender();
  live.ds = { ...live.ds, datasourceJson: 'DOC2' }; card.rerender(); await settleAll(); card.rerender();
  assert.equal(find(card.result, (el) => el.props?.['data-testid'] === 'schema-moved'), null);
  assert.equal(find(card.result, (el) => el.type?.name === 'ElementTree'), null, 'closed card');
});

test('the precedence box with nothing typed still follows the model (an undo or the assistant shows at once)', async () => {
  const p = calcPanel([group('Time Intelligence', 10)]);
  await settleAll(); p.view.rerender();
  const live = { g: group('Time Intelligence', 10) };
  const card = p.pg.mount(() => { const el = cards(p.view)[0]; return el.type({ ...el.props, group: live.g }); });
  const box = () => find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric');
  live.g = group('Time Intelligence', 7); card.rerender(); await settleAll(); card.rerender();
  assert.equal(box().props.value, '7');
  assert.equal(find(card.result, (el) => el.props?.['data-testid'] === 'calc-precedence-moved'), null);
});
