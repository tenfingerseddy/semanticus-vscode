// A8: ChangePlanState resets its ci sequence on each proposal. A new line
// must not overwrite the held words of an old line that reused that id.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const flat = (el) => el == null || typeof el === 'boolean' ? '' : Array.isArray(el) ? el.map(flat).join('')
  : typeof el === 'object' ? flat(el.props?.children) : String(el);
const item = (name) => ({ id: 'ci-0001', objectRef: `measure:Sales/${name}`, objectName: name,
  kind: 'set_description', source: 'ai', category: 'Descriptions', severity: 'info', risk: 'ai',
  target: 'description', title: `Describe ${name}`, rationale: 'Add business context.', status: 'needs_content' });
const planOf = (id, name) => ({ planId: id, revision: 1, items: [item(name)], summary: {
  total: 1, deterministic: 0, bpa: 0, ai: 1, renames: 0, needsContent: 1,
  approved: 0, rejected: 0, applied: 0, unverified: 0,
} });

test('A8: typing on a re-proposed line keeps the previous line words with the reused id', async () => {
  const disk = makeDisk();
  let changed;
  const pg = makePage({
    './bridge': { loadState: disk.loadState, saveState: disk.saveState,
      rpc: async (method) => { assert.equal(method, 'getPlan'); return planOf('p1', 'Revenue'); },
      onPlanChange: (fn) => { changed = fn; return () => undefined; } },
    './sendtask': { GiveToAssistant: () => null, slotTask: () => ({}) },
    './copy': { uiLabel: (s) => s },
  }, MODEL);
  const O = pg.load('optimize.tsx');
  const view = pg.mount(() => O.OptimizeView({}));
  await settleAll(); view.rerender();
  const row = () => find(view.result, (el) => el.type?.name === 'Row');
  row().props.onDraft('Net sales in AUD. Excludes intercompany.');
  view.rerender();
  // The assistant proposed again after filling Revenue. The first content
  // line in the fresh ChangePlanState is Rate, which also gets ci-0001.
  changed(planOf('p2', 'Rate'));
  await settleAll(); view.rerender();
  assert.equal(row().props.draft, '', 'Revenue words must not be shown as Rate words');
  const kept = () => find(view.result, (el) => el.props?.['data-testid'] === 'opt-words-kept');
  assert.match(flat(kept()), /Net sales in AUD/);
  row().props.onDraft('Returns divided by total orders.');
  await settleAll(); view.rerender();
  assert.equal(row().props.draft, 'Returns divided by total orders.');
  assert.match(flat(kept()), /Net sales in AUD\. Excludes intercompany\./,
    'typing Rate must not discard the unsaved Revenue description');
});
