// A8: a repeat removed by another window makes setPlanRepeat refuse.
// The real repeat form must keep the person's cadence until an OK reply.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeDisk, makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const byId = (tree, id) => find(tree, (el) => el.props?.['data-testid'] === id);

test('A8: a refused repeat change keeps the edited cadence in its form and store', async () => {
  const disk = makeDisk();
  const pg = makePage({ './bridge': { loadState: disk.loadState, saveState: disk.saveState } }, MODEL);
  const RP = pg.load('plan-repeat.tsx');
  const row = { id: 'pt-00000001', title: 'Check the totals', source: 'you', doer: 'you' };
  const saved = { id: 'rp-00000001', words: row.title, fromItemId: row.id, doer: 'you',
    cadence: { kind: 'weekly', every: 1, day: 'monday' }, cadenceText: 'Every week on Monday',
    nextDueUtc: '2026-10-12T00:00:00Z', held: false };
  const scope = `planrow|${row.id}`;
  const hold = {
    read: () => pg.drafts.heldDraft(scope, 'repeat')?.value,
    write: (c, start) => pg.drafts.draftStore.set(MODEL, scope, 'repeat', { value: c, base: start }),
    clear: () => pg.drafts.draftStore.clear(MODEL, scope, 'repeat'),
  };
  let answer;
  const calls = [];
  const ed = pg.mount(() => RP.RepeatEditor({ row, repeats: [saved], isPro: true, busy: null, hold,
    onAdd() {}, onDelete() {},
    onChange: (id, req) => { calls.push([id, req]); return new Promise((resolve) => { answer = resolve; }); },
  }));
  byId(ed.result, 'plan-repeat-change').props.onClick();
  ed.rerender();
  const formElement = () => find(ed.result, (el) => el.type?.name === 'CadenceForm');
  const form = pg.mount(() => formElement().type(formElement().props));
  byId(form.result, 'plan-repeat-every').props.onChange({ target: { value: '3' } });
  await settleAll(); form.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, scope, 'repeat')?.value.every, 3);
  byId(form.result, 'plan-repeat-form').props.onSubmit({ preventDefault() {} });
  assert.equal(calls[0][1].cadence.every, 3);
  // This is PlanView.controlOk's refusal result. The engine rejects an id
  // removed by another person: Plan/PlanRepeats.cs SetPlanRepeatAsync.
  answer(false);
  await settleAll(); ed.rerender();
  assert.equal(pg.drafts.draftStore.get(MODEL, scope, 'repeat')?.value.every, 3,
    'a failed write must not delete the cadence the person still needs to save');
  assert.ok(formElement(), 'the refused cadence remains editable');
});
