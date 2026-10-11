// verify (m11/sol6-ui, the class hunt): the calculation item's FORMAT editor is the sibling of the expression editor Sol's C4 fixed, on the
// same row (CalcItemRow, advmodels.tsx). Its "Save format" sends setCalcItemFormatString(ref, typed) with no read at Save and no value it
// replaces, so the engine cannot compare inside its write. The calculation groups page reloads 350 ms after a change: a Save inside that
// moment writes the person's format over the assistant's newer one without a word (the same window as C4's 300 ms). This test fails until
// the Save names what it replaces (or reads first and asks) the way the expression Save on the same row now does.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, makeHookRuntime, settle } from './helpers/webview-load.mjs';

globalThis.window ??= globalThis;

const walk = (node, visit) => {
  if (node == null || typeof node !== 'object') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node); walk(node.props?.children, visit);
};
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const REFUSAL = 'This value changed in the model after you opened it, so your version was not saved. Nothing was written.';

const stubs = (rpc) => ({
  './bridge': { rpc, onDidChange: () => () => undefined, exportDoc: () => undefined, printDoc: () => undefined, openExternal: () => undefined },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null },
  './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
  './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) },
  './formatMode': { inferFormatMode: () => 'dynamic' },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
  './activity': { useClaudeReflection: () => undefined },
});

test('calculation item format: a Save inside the page\'s reload window is not written over the assistant\'s newer format', async () => {
  // The engine's copy. A fake that does what LocalEngine.GuardExpectedValue does when the write names its starting point.
  const engine = { fmt: '"0.0%"' };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'setCalcItemFormatString') {
      const expected = args[3];
      if (expected != null && engine.fmt !== expected && engine.fmt !== args[1]) throw new Error(REFUSAL);
      engine.fmt = args[1];
    }
  };
  const rt = makeHookRuntime();
  const mod = makeLoader(stubs(rpc), rt.React)('advmodels.tsx');
  const item = { ref: 'calcitem:Time/YoY %', name: 'YoY %', ordinal: 2, expression: 'DIVIDE(1, 2)', formatStringExpression: '"0.0%"' };
  const h = rt.renderHook(() => mod.CalcItemRow({ item, reload: () => calls.push(['reload']), onError: (e) => calls.push(['error', String(e)]) }));
  await settle();
  const fmt = find(h.result, (el) => el.type?.name === 'FormatStringControl');
  assert.ok(fmt, 'the row has a format control');
  engine.fmt = '"0.00 %"';   // the assistant rewrote this item's format; the page reloads 350 ms later and has not yet
  fmt.props.onSave('"0.0 %"');   // the person's Save format, with what they typed
  await settle(); await settle();
  assert.equal(engine.fmt, '"0.00 %"', `the assistant's newer format was written over without a word (calls: ${JSON.stringify(calls)})`);
});
