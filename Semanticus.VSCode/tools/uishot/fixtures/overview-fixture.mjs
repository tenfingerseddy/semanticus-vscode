// The Overview's fixture: the plan and the reads each state of the approved Overview needs, built on the
// Plan page's own fixture so the Overview and the Plan can never be shown two different plans.
//
// Every state is a set of engine answers, not a set of page props: the driver plays them through the
// harness exactly as the engine would send them, and the page decides what to draw. The worth carries
// fromScore and toScore because the engine sends them (ModelPlanView.cs, ModelPlanWorth), and only the
// states where the engine would have rehearsed a grade carry a toScore at all.
//
// Contoso placeholders only.
import { makePlan } from './plan-fixture.mjs';

/** The Pro plan the mockup draws: Jev's item first, B 82 now, A 93 after the safe fixes. */
export function proPlan() {
  const plan = makePlan({ jev: true });
  plan.worth = { ...plan.worth, fromScore: 82, toScore: 93 };
  return plan;
}

/** Jev still looking: no Jev item yet, and the rehearsed grade not in yet either. */
export function lookingPlan() {
  const plan = makePlan({ jevPending: true });
  plan.worth = {
    from: 'B', fromScore: 82, pending: true, lines: ['the grade the safe fixes would reach is still being worked out'],
    targetLine: 'B now, and the grade the safe fixes reach is still being worked out.',
  };
  return plan;
}

/** Free: the same plan in rule order, no Jev, no scores on the items. The grade is free, so it stays. */
export function freePlan() {
  const plan = makePlan({ free: true, jev: true });
  plan.worth = { ...plan.worth, fromScore: 82, toScore: 93 };
  return plan;
}

/** Nothing left: every item done or set aside, and the grade does not move. */
export function emptyPlan() {
  const plan = makePlan({});
  plan.items.forEach((item) => {
    if (item.status !== 'skipped') { item.status = 'done'; item.state = 'done'; }
  });
  plan.history.push({
    when: new Date(Date.now() - 20 * 60000).toISOString(), kind: 'approved', actor: 'you', itemId: 'jev.measures.table:Sales',
    title: 'Tidy 2 measures in Sales', sentence: '3 changes written as one step.', text: '3 changes written as one step.',
  });
  plan.worth = { from: 'A', to: 'A', fromScore: 94, toScore: 94, unchanged: true, lines: [], targetLine: 'Stays A.' };
  plan.next = undefined;
  return plan;
}

/** No data: the same Pro plan, but the model has no query connection, so nothing can be scanned. */
export function noDataPlan() {
  const plan = makePlan({ jev: true });
  plan.worth = { from: 'C', to: 'B', fromScore: 68, toScore: 79, lines: [], targetLine: 'C, then B.' };
  return plan;
}

/** A fresh model: no tables, so nothing on the plan and nothing to grade. */
export function freshPlan() {
  return {
    modelName: 'Contoso Budget', refreshedAt: new Date().toISOString(), items: [], history: [], tables: [],
    targets: [], ranked: false, orderedBy: 'In the order the rules found them.',
    worth: { lines: [] },
  };
}

/** The recorded run the Checks tile shows: 17 of 23 pass, as in the mockup. */
export const CHECKS_17_OF_23 = {
  runs: [{ runId: 'r-overview', when: new Date(Date.now() - 3600000).toISOString(), live: true,
    health: { checked: 23, passed: 17, failed: 6, notVerifiable: 0, missing: 0 } }],
};
