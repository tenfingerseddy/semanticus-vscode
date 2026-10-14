// A8: run the real Pending changes owner. An accepted save may end only
// the words that were submitted, not words typed while its reply was pending.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const line = { id: 'L1', objectRef: 'measure:Sales/Revenue', objectName: 'Revenue',
  kind: 'set_description', source: 'ai', category: 'Descriptions', severity: 'info',
  risk: 'ai', target: 'description', title: 'Describe Revenue', rationale: 'Add business context.', status: 'needs_content' };
const planOf = (item) => ({ planId: 'p1', revision: 1, items: [item], summary: {
  total: 1, deterministic: 0, bpa: 0, ai: 1, renames: 0, needsContent: 1,
  approved: 0, rejected: 0, applied: 0, unverified: 0,
} });

test('A8: an Optimize save reply keeps the newer description typed before it arrived', async () => {
  const disk = makeDisk();
  const calls = [];
  let reply;
  const pg = makePage({
    './bridge': { loadState: disk.loadState, saveState: disk.saveState,
      onPlanChange: () => () => undefined,
      rpc: (method, ...args) => {
        calls.push([method, ...args]);
        if (method === 'getPlan') return Promise.resolve(planOf(line));
        if (method === 'setPlanItem') return new Promise((resolve) => { reply = resolve; });
        throw new Error(`Unexpected call: ${method}`);
      } },
    './sendtask': { GiveToAssistant: () => null, slotTask: () => ({}) },
    './copy': { uiLabel: (s) => s },
    './personality/motion': { BulkCount: () => null },
  }, MODEL);
  const O = pg.load('optimize.tsx');
  const view = pg.mount(() => O.OptimizeView({}));
  await settleAll(); view.rerender();
  const row = () => find(view.result, (el) => el.type?.name === 'Row' && el.props.it.id === 'L1');
  row().props.onDraft('Net sales in AUD.');
  view.rerender();
  row().props.onSave();
  assert.deepEqual(calls.at(-1), ['setPlanItem', 'L1', 'Net sales in AUD.', null, 'human', '']);
  row().props.onDraft('Net sales in AUD. Excludes intercompany.');
  view.rerender();
  assert.equal(row().props.draft, 'Net sales in AUD. Excludes intercompany.');
  reply(planOf({ ...line, status: 'proposed', after: 'Net sales in AUD.' }));
  await settleAll(); view.rerender();
  assert.equal(row().props.draft, 'Net sales in AUD. Excludes intercompany.',
    'the engine accepted the first text only; the later text must remain available to save');
});
