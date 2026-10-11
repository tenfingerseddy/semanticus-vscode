// Round 3 (join 11, drafts): a MOVE of typed words (Keep mine on a renamed calculation item or plan line) never rewrites what they were typed over and never overwrites.
//   the words move WITH the value they were typed over (V5): a pure rename (the target still holds that value) asks nothing; a rename that also changed the value asks
//   a move onto a target that already holds words for the field changes nothing and returns the clashing fields (V6); the ask offers that target disabled, with the reason
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, text, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
const MODEL = '/models/Sales.bim';
const live = { expression: 'SELECTEDMEASURE()' };
const calls = [];
const stubs = (disk) => ({
  './bridge': { rpc: async (m, ...a) => { calls.push([m, ...a]); if (m === 'getDax') return live.expression; if (m === 'getObjectProperties') return [{ name: 'FormatStringExpression', value: '' }]; return undefined; },
    onDidChange: () => () => undefined, loadState: disk.loadState, saveState: disk.saveState },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) }, './formatMode': { inferFormatMode: () => 'dynamic' },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) }, './activity': { useClaudeReflection: () => undefined },
});
const item = (ref, name, expression) => ({ ref, name, ordinal: 1, expression, formatStringExpression: '' });
const dax = (tree) => find(tree, (el) => el.type?.name === 'DaxField');
const button = (tree, label) => find(tree, (el) => el.type === 'button' && text(el) === label);
function world() {
  const disk = makeDisk(), pg = makePage(stubs(disk), MODEL);
  const { CalcItemRow } = pg.load('advmodels.tsx'), { OrphanDrafts } = pg.load('orphandrafts.tsx');
  return {
    pg, store: pg.drafts.draftStore,
    row: (it) => pg.mount(() => CalcItemRow({ item: it, reload: () => undefined, onError: () => undefined })),
    ask: (list) => pg.mount(() => OrphanDrafts({ prefix: 'calcitem|', live: list.map((i) => i.ref), what: 'calculation item', nameOf: (r) => r,
      targetsFor: () => list.map((i) => ({ id: i.ref, label: i.name })) })),
  };
}
const pick = async (q, value) => { find(q.result, (el) => el.type === 'select').props.onChange({ target: { value } }); await settleAll(); };

test('moveScope keeps the value the words were typed over, and returns what it did', () => {
  const w = world(), s = w.store;
  s.set(MODEL, 'calcitem|a', 'expression', { value: 'MINE', base: 'READ', label: 'A' });
  const r = s.moveScope(MODEL, 'calcitem|a', 'calcitem|b');
  assert.deepEqual(r, { moved: true, conflicts: [] });
  assert.equal(s.get(MODEL, 'calcitem|a', 'expression'), null);
  const moved = s.get(MODEL, 'calcitem|b', 'expression');
  assert.equal(moved.value, 'MINE'); assert.equal(moved.base, 'READ', 'not re-based onto anything'); assert.equal(moved.label, 'A');
});

test('moveScope onto a scope that already holds a draft for a field moves nothing and names the field', () => {
  const w = world(), s = w.store;
  s.set(MODEL, 'calcitem|a', 'expression', { value: 'MINE A', base: 'x' });
  s.set(MODEL, 'calcitem|a', 'format', { value: 'FMT A', base: 'y' });
  s.set(MODEL, 'calcitem|b', 'format', { value: 'FMT B', base: 'y' });
  const r = s.moveScope(MODEL, 'calcitem|a', 'calcitem|b');
  assert.equal(r.moved, false); assert.deepEqual(r.conflicts, ['format']);
  assert.equal(s.get(MODEL, 'calcitem|a', 'expression').value, 'MINE A', 'both fields stay where they were');
  assert.equal(s.get(MODEL, 'calcitem|a', 'format').value, 'FMT A');
  assert.equal(s.get(MODEL, 'calcitem|b', 'format').value, 'FMT B', 'the target\'s own words are untouched');
  assert.equal(s.get(MODEL, 'calcitem|b', 'expression'), null, 'and the other field did not move alone');
});

test('a PURE rename (the target holds the value the words were typed over): Keep mine asks nothing', async () => {
  const w = world();
  live.expression = 'SELECTEDMEASURE()';
  const a = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(a.result).props.onChange('MY DAX'); await settleAll(); a.unmount();
  const renamed = item('calcitem:Time/Year to date', 'Year to date', live.expression);   // the assistant renamed it and changed nothing else
  const q = w.ask([renamed]); await settleAll();
  await pick(q, renamed.ref);
  button(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  assert.equal(w.store.get(MODEL, 'calcitem|' + renamed.ref, 'expression').value, 'MY DAX');
  const b = w.row(renamed); await settleAll();
  assert.equal(dax(b.result).props.value, 'MY DAX', 'the row opens on the words');
  assert.equal(find(b.result, (el) => el.type?.name === 'EditMovedBanner'), null, 'and asks nothing: nothing the person did not read has changed');
});

test('a rename that also changed the value asks, and nothing is written', async () => {
  const w = world();
  live.expression = 'SELECTEDMEASURE()';
  const a = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(a.result).props.onChange('MY DAX'); await settleAll(); a.unmount();
  const renamed = item('calcitem:Time/Year to date', 'Year to date', 'CALCULATE(SELECTEDMEASURE(), DATESYTD(Date[Date]))');
  live.expression = renamed.expression;
  const q = w.ask([renamed]); await settleAll();
  await pick(q, renamed.ref);
  const before = calls.length;
  button(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  const b = w.row(renamed); await settleAll();
  assert.ok(find(b.result, (el) => el.type?.name === 'EditMovedBanner'), 'the row asks Keep mine / Use the new one');
  assert.equal(calls.slice(before).filter((c) => /^set|^create|^delete/.test(c[0])).length, 0, 'nothing was written');
});

test('a target that already holds typed words is offered disabled with the reason, and the button writes nothing', async () => {
  const w = world();
  live.expression = 'SELECTEDMEASURE()';
  const ytd = w.row(item('calcitem:Time/YTD', 'YTD', live.expression)); await settleAll();
  dax(ytd.result).props.onChange('WORDS FOR YTD'); await settleAll(); ytd.unmount();
  const mtd = item('calcitem:Time/MTD', 'MTD', live.expression), qtd = item('calcitem:Time/QTD', 'QTD', live.expression);
  const m = w.row(mtd); await settleAll();
  dax(m.result).props.onChange('WORDS FOR MTD'); await settleAll(); m.unmount();
  const q = w.ask([mtd, qtd]); await settleAll();
  const opts = findAll(q.result, (el) => el.type === 'option');
  const mtdOpt = opts.find((o) => o.props.value === mtd.ref), qtdOpt = opts.find((o) => o.props.value === qtd.ref);
  assert.equal(mtdOpt.props.disabled, true, 'MTD cannot take them');
  assert.match(text(mtdOpt) + JSON.stringify(mtdOpt.props.children), /already has words you typed/);
  assert.ok(!qtdOpt.props.disabled, 'QTD can');
  await pick(q, mtd.ref);   // a slip: the button is pressed anyway
  button(q.result, 'Keep mine on it').props.onClick(); await settleAll();
  assert.equal(w.store.get(MODEL, 'calcitem|' + mtd.ref, 'expression').value, 'WORDS FOR MTD', 'the target\'s words are kept');
  assert.equal(w.store.get(MODEL, 'calcitem|calcitem:Time/YTD', 'expression').value, 'WORDS FOR YTD', 'and so are the moving words, where they were');
  assert.match(JSON.stringify(q.result), /Nothing moved/, 'the page says so');
});
