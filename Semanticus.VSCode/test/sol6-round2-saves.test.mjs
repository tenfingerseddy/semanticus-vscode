// Sol join 6, round 2 (the class close), the webview side: the calculation item's Save format.
//
// WHAT WENT WRONG. The format editor on a calculation item sent setCalcItemFormatString(ref, typed) with no read at Save and no value it replaces.
// The page reloads 350 ms after a change, so a Save inside that moment wrote the person's format over the assistant's newer one with no banner. And the
// control's own effect replaced the words typed in its Dynamic box whenever the stored value moved.
//
// The REAL CalcItemRow runs (hooks driven by a tiny runtime), with a fake engine that does what LocalEngine.GuardExpectedValue does.
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
const hasBanner = (tree) => !!find(tree, (el) => el.type?.name === 'EditMovedBanner');
const REFUSAL = 'This format changed in the model after you opened it, so your version was not saved. Nothing was written.';

const stubs = (rpc) => ({
  './bridge': { rpc, onDidChange: () => () => undefined, exportDoc: () => undefined, printDoc: () => undefined, openExternal: () => undefined },
  './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
  './daxeditor': { DaxField: () => null },
  './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) },
  './ui': { Caret: () => null },
  '@tanstack/react-virtual': { useVirtualizer: () => ({}) },
  './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
  './activity': { useClaudeReflection: () => undefined },
});

function scene({ page = '"0.0%"', live = page, afterRead } = {}) {
  const engine = { fmt: live };
  const calls = [];
  const rpc = async (method, ...args) => {
    calls.push([method, ...args]);
    if (method === 'getObjectProperties') { const seen = engine.fmt; afterRead?.(engine); return [{ name: 'FormatStringExpression', value: seen }]; }
    if (method === 'setCalcItemFormatString') {
      const expected = args[3];
      if (expected != null && engine.fmt !== expected && engine.fmt !== args[1]) throw new Error(REFUSAL);
      engine.fmt = args[1];
    }
  };
  const rt = makeHookRuntime();
  const mod = makeLoader(stubs(rpc), rt.React)('advmodels.tsx');
  let item = { ref: 'calcitem:Time/YoY %', name: 'YoY %', ordinal: 2, expression: 'DIVIDE(1, 2)', formatStringExpression: page };
  const h = rt.renderHook(() => mod.CalcItemRow({ item, reload: () => calls.push(['reload']), onError: (e) => calls.push(['error', String(e)]) }));
  const control = () => find(h.result, (el) => el.type?.name === 'FormatStringControl');
  return { engine, calls, h, control, moveItem: (value) => { item = { ...item, formatStringExpression: value }; h.rerender(); }, writes: () => calls.filter((c) => c[0] === 'setCalcItemFormatString') };
}

test('calculation item format: a Save names the value the Save read as the one it replaces, and writes', async () => {
  const s = scene();
  await settle();
  s.control().props.onDraft('"0.0 %"');
  await settle();
  s.control().props.onSave('"0.0 %"');
  await settle(); await settle();
  assert.equal(s.writes().length, 1, 'one write');
  assert.equal(s.writes()[0][4], '"0.0%"', 'it names the format the Save read');
  assert.equal(s.engine.fmt, '"0.0 %"', 'and the person\'s format landed');
  assert.ok(!hasBanner(s.h.result), 'no banner on the plain path');
});

test('calculation item format: a format that moves between the Save\'s read and its write is refused by the engine, and the person is asked', async () => {
  const s = scene({ afterRead: (engine) => { engine.fmt = '"0.00 %"'; } });   // the assistant writes right after the read
  await settle();
  s.control().props.onDraft('"0.0 %"');
  await settle();
  s.control().props.onSave('"0.0 %"');
  await settle(); await settle(); await settle();
  assert.equal(s.engine.fmt, '"0.00 %"', 'the assistant\'s newer format stands');
  assert.equal(s.writes().length, 1, 'the write was sent and refused');
  assert.ok(hasBanner(s.h.result), 'the person is asked');
  assert.equal(s.control().props.draft, '"0.0 %"', 'and the typed words are still in the box');
  assert.ok(!s.calls.some((c) => c[0] === 'error'), 'the refusal is the banner, not a red error');
});

test('calculation item format: words typed in the Dynamic box are kept, with the choice shown, when the stored format moves under them', async () => {
  const s = scene();
  await settle();
  s.control().props.onDraft('"my own format"');
  await settle();
  s.engine.fmt = '"0.00 %"';
  s.moveItem('"0.00 %"');   // the page reloaded: the stored format is the assistant's
  await settle();
  assert.equal(s.control().props.draft, '"my own format"', 'the typed words are not replaced');
  assert.ok(hasBanner(s.h.result), 'the banner offers Keep mine / Use the new one');
  assert.equal(s.writes().length, 0, 'nothing was written');
});

test('calculation item format: an untouched box follows the stored format', async () => {
  const s = scene();
  await settle();
  s.moveItem('"0.00 %"');
  await settle();
  assert.equal(s.control().props.draft, '"0.00 %"', 'nothing was typed, so the box follows the stored format');
  assert.ok(!hasBanner(s.h.result), 'and there is nothing to ask');
});
