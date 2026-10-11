// Verifier on m11/solui, U2 (rule R-B: a draft ends only when the engine accepted exactly that text or the person discarded it). The lane's sweep found these boxes of
// the SAME shape as Sol's A6 finding 2 (an effect sets the box from a live value, or clears it when the live context moves) and left them unfixed. Each test types into
// the box, moves the live value the way another writer does, and leaves the box; the typed words must still be in the box, or named next to it. Both fail on the lane.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, findAll, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });

const words = (tree) => JSON.stringify(tree, (k, v) => (k === '_owner' || typeof v === 'function' ? undefined : v));

test('calculation group precedence: digits typed while the assistant changes the precedence are not replaced silently', async () => {
  const disk = makeDisk();
  const calls = [];
  const group = { ref: 'table:Time Intelligence', name: 'Time Intelligence', precedence: 10, items: [] };
  // Mount the card body: it is not exported, so reach it through a panel that has one group.
  const pg2 = makePage({
    './bridge': { rpc: async (method, ...args) => { calls.push([method, ...args]); return method === 'listCalculationGroups' ? [structuredClone(group)] : {}; }, onDidChange: () => () => undefined,
      loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, 'file:/m/Model.bim');
  const adv2 = pg2.load('advmodels.tsx');
  const view = pg2.mount(() => adv2.CalcGroupsPanel());
  await settleAll(); view.rerender();
  const cardEl = find(view.result, (el) => el.type?.name === 'CalcGroupCard');
  assert.ok(cardEl, 'the panel renders the group card');
  const live = { group: { ...group } };
  const card = pg2.mount(() => cardEl.type({ ...cardEl.props, group: live.group }));
  const box = () => find(card.result, (el) => el.type === 'input' && el.props?.inputMode === 'numeric');
  box().props.onChange({ target: { value: '20' } });
  await settleAll(); card.rerender();
  assert.equal(box().props.value, '20');

  // The assistant sets the precedence to 5 over MCP; the panel reloads and the card gets the new group.
  live.group = { ...live.group, precedence: 5 };
  card.rerender(); await settleAll(); card.rerender();
  box().props.onBlur();
  await settleAll(); card.rerender();
  const shown = words(card.result);
  assert.ok(box().props.value === '20' || /20/.test(shown.replace(/"value":"5"/, '')),
    `the typed precedence 20 is gone without a word: the box shows ${box().props.value} and nothing on the card names 20`);
});

test('Power Query step rename: a name typed while the query changes elsewhere is not dropped', async () => {
  const pg = makePage({
    './bridge': { rpc: async () => ({}) },
    './grid': { ResultGrid: () => null },
  }, 'file:/m/Model.bim');
  const pq = pg.load('pqtransforms.tsx');
  const m = 'let\n    Source = Sql.Database("s", "d"),\n    #"Changed Type" = Source\nin\n    #"Changed Type"';
  const live = { token: 'Sales|q1|rev1', text: m };
  const view = pg.mount(() => pq.AppliedStepsPanel({ text: live.text, contextToken: live.token, isContextCurrent: () => true, onChange() {}, onSelect() {}, onError() {} }));
  await settleAll(); view.rerender();
  const pencil = findAll(view.result, (el) => el.type === 'button' && el.props?.title === 'Rename step')[0];
  assert.ok(pencil, 'a step can be renamed');
  pencil.props.onClick();
  await settleAll(); view.rerender();
  const input = () => find(view.result, (el) => el.type === 'input' && el.props?.autoFocus);
  input().props.onChange({ target: { value: 'Typed columns' } });
  await settleAll(); view.rerender();
  assert.equal(input()?.props.value, 'Typed columns');

  // The assistant edits this query's M; the clean editor takes the new text and the context token moves (textRevision).
  live.token = 'Sales|q1|rev2';
  live.text = m.replace('Source\nin', 'Source,\n    Added = Source\nin');
  view.rerender(); await settleAll(); view.rerender();
  assert.ok(input()?.props.value === 'Typed columns' || /Typed columns/.test(words(view.result)),
    'the typed step name "Typed columns" is gone: the rename box closed and nothing on the page names the words');
});
