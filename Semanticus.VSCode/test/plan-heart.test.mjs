// The Plan at the heart (lane U1): the pure view logic in plan-model.ts, the fixture the driver plays, and the
// components that can be rendered without an engine.
//
// What is executed here, one line each:
//   the fixture IS E1's shape file (docs/redesign/contracts/plan-item.json), row for row
//   each state word has its Studio label, and the page reads the engine's word rather than making one
//   which actions show on which row: Up only on the assistant's tasks, Pause on every row but On it, Resume on a
//     paused row, Approve only on a Semanticus row with a worked-out change set
//   the strip's segment cap and its three counts
//   Needs you, then Next capped at seven with Show all
//   the field: Enter adds a row and never navigates
//   Run the plan: one call, no items, the readyDigest last; free shows the Pro line, never a dead button
//   "What your assistant sees" equals, byte for byte, the engine's fold line over the same fixture (the other half of
//     that check is Semanticus.Tests/PlanHeartFoldParityTests.cs)
process.env.TZ = 'UTC';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import {
  HEART_SHAPE, makeHeartPlan, heartHold, heartRun, heartUp, heartQueue, heartAdd, heartClaim, heartComplete,
} from '../tools/uishot/fixtures/plan-heart-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');
const webview = join(root, 'webview');
const src = join(webview, 'src');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

let stubTier = 'pro';
const calls = [];
const STUBS = {
  './bridge': {
    rpc: async (...args) => { calls.push(args); return null; },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
    onPlanChange: () => () => {}, onSpecChange: () => () => {}, copyText: async () => {}, pickSpecFile: async () => null,
  },
  './connection': { useConnection: () => ({ session: { sessionId: 'test' }, context: null }) },
  './pro': {
    useTier: () => stubTier, useFeature: () => (stubTier === 'pro' ? 'granted' : 'denied'),
    isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null,
    PENDING_ACCESS: 'Checking your Pro access', upgradeToPro: () => {},
  },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => {
    if (STUBS[spec]) return STUBS[spec];
    if (spec.startsWith('.')) {
      if (spec.endsWith('.css')) return {};
      const out = load(resolve(dirname(full), spec));
      return out;
    }
    return req(spec);
  };
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  cache.set(full, module.exports);
  return module.exports;
}
const P = load(join(src, 'plan-model.ts'));
// Read as LF: the Windows CI checkout has CRLF endings (`* text=auto`), so the fold fixtures came back with \r\n
// while plan-model.ts assistantSees and the engine's PlanTaskQueue both join with \n on every platform (PR 396).
const read = (p) => readFileSync(p, 'utf8').replace(/\r\n/gu, '\n');
// E1's shape file is a private contract file, left out of the public source mirror. The private repo (marked by its
// mirror manifest) always reads it, so a missing file still fails there; the mirror skips the checks that compare to it.
const privateRepo = existsSync(join(repo, 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
const shapeFile = privateRepo ? JSON.parse(read(join(repo, 'docs', 'redesign', 'contracts', 'plan-item.json'))) : null; // mirror-optional: private contract file
const byId = (plan, id) => plan.items.find((r) => r.id === id);
const ids = (rows) => rows.map((r) => r.id);
let failures = 0;
const pending = [];
function check(name, fn) {
  try {
    const out = fn();
    if (out && typeof out.then === 'function') {
      pending.push(out.then(() => console.log(`ok - ${name}`), (error) => { failures++; console.error(`not ok - ${name}\n${error.stack}`); }));
    } else console.log(`ok - ${name}`);
  } catch (error) { failures++; console.error(`not ok - ${name}\n${error.stack}`); }
}

const shapeCheck = shapeFile ? check : (name) => console.log(`skip - ${name} (the shape file is not in the public source)`);

// ---- the fixture is the shape file ---------------------------------------------------------------------
shapeCheck('the fixture carries E1 shape file rows, strip and switch unchanged', () => {
  assert.deepEqual(HEART_SHAPE.rows, shapeFile.rows);
  assert.deepEqual(HEART_SHAPE.strip, shapeFile.strip);
  assert.equal(HEART_SHAPE.assistantQueue, shapeFile.assistantQueue);
  const plan = makeHeartPlan();
  const added = plan.items.filter((r, i) => JSON.stringify(r) !== JSON.stringify(shapeFile.rows[i])).map((r) => r.id);
  assert.deepEqual(added, ['safefix/Visibility', 'datetable/table:Date'], 'only the two change sets are added to the sample');
});

// ---- words ---------------------------------------------------------------------------------------------
shapeCheck('every state word the engine can send has its Studio label', () => {
  assert.deepEqual(Object.keys(P.STATE_WORD_LABEL).sort(), [...shapeFile.enums.stateWord].sort());
  assert.deepEqual(Object.keys(P.SOURCE_LABEL).sort(), [...shapeFile.enums.source].sort());
  assert.deepEqual(Object.keys(P.DOER_LABEL).sort(), [...shapeFile.enums.doer].sort());
  assert.equal(P.STATE_WORD_LABEL.checking, 'Applied, checking');
  assert.equal(P.STATE_WORD_LABEL.needs_you, 'Needs you');
  assert.equal(P.STATE_WORD_LABEL.set_aside, 'Set aside');
});

shapeCheck('the page reads the engine state word and never makes one up', () => {
  assert.equal(P.stateWordOf({ id: 'x', stateWord: 'paused', status: 'ready' }), 'paused');
  assert.equal(P.stateWordOf({ id: 'x', status: 'done' }), 'done');
  const plan = makeHeartPlan();
  assert.deepEqual(plan.items.map((r) => P.stateWordOf(r)), shapeFile.rows.map((r) => r.stateWord));
});

check('a Send row names the page it came from, and a jump reads area then page', () => {
  const plan = makeHeartPlan();
  assert.equal(P.sourceLine(byId(plan, 'pt-9e2f7a10'), () => 'AI readiness'), 'You added · Review › AI readiness');
  assert.equal(P.sourceLine(byId(plan, 'jev.measures.table:Sales')), 'Semanticus Pro');
  assert.equal(P.pagePlace({ tool: 'bpa', label: 'Model quality (BPA)' }), 'Review › Model quality (BPA)');
  assert.equal(P.pagePlace({ tool: 'workflows', label: 'Workflows' }), 'Workflows');
});

check('PAGE_AREA agrees with the Studio strip (App.tsx TAB_GROUPS)', () => {
  const app = read(join(src, 'App.tsx'));
  const block = app.slice(app.indexOf('const TAB_GROUPS'), app.indexOf('];', app.indexOf('const TAB_GROUPS')));
  for (const group of block.matchAll(/\{ id: '(\w+)', label: '([^']+)', tabs: \[([\s\S]*?)\] \}/g)) {
    for (const tab of group[3].matchAll(/\{ id: '(\w+)', label: '[^']+' \}/g)) {
      assert.equal(P.PAGE_AREA[tab[1]], group[2], `${tab[1]} sits in ${group[2]}`);
    }
  }
});

// ---- actions -------------------------------------------------------------------------------------------
// Round 1, K5: a row has the doer menu, at most one main button, and the dots. Pause, Resume, Could not, Choose, Answer,
// Show the changes and the Queue group are gone from the page; each control is on a row once.
check('the row controls: Pause, Resume, Could not, Choose, Answer, Show the changes and Queue are gone from the model', () => {
  for (const gone of ['rowActions', 'queueActions', 'doerChoices', 'showsPause']) assert.equal(P[gone], undefined, `${gone} is deleted`);
  assert.deepEqual(Object.keys(P.ACTION_WORD).sort(), ['approve', 'aside', 'done', 'draft', 'open', 'putback', 'remove', 'repeat', 'save', 'up']);
  const plan = makeHeartPlan({ mode: 'queue' });
  for (const row of plan.items) {
    const ids = [P.mainAction(row, false), ...P.rowMenuActions(row, { items: plan.items, grant: 'granted', canRepeat: true })].filter(Boolean).map((a) => a.id);
    assert.equal(new Set(ids).size, ids.length, `${row.id}: no control twice (${ids})`);
    for (const dead of ['pause', 'resume', 'couldnot', 'choose', 'answer', 'workout', 'take', 'give']) assert.ok(!ids.includes(dead), `${row.id} has no ${dead}`);
  }
});

check('Move up is in the dots, on the assistant tasks only, and the first one cannot go higher', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  const menu = (r) => P.rowMenuActions(r, { items: plan.items });
  const withUp = plan.items.filter((r) => menu(r).some((a) => a.id === 'up'));
  assert.deepEqual(ids(withUp).sort(), ['pt-0d6e33b8', 'pt-3b7d0c21', 'pt-9e2f7a10', 'pt-c4e1a9f3']);
  for (const r of withUp) assert.equal(P.doerOf(r), 'assistant');
  const up = (id) => menu(byId(plan, id)).find((a) => a.id === 'up');
  assert.equal(up('pt-3b7d0c21').disabled, true, 'first in the order');
  assert.equal(up('pt-9e2f7a10').disabled, false);
  assert.equal(up('pt-9e2f7a10').label, 'Move up');
  // A derived row the assistant does (a Jev row) is not a task and has no place in the order.
  const plain = makeHeartPlan();
  assert.ok(!P.rowMenuActions(byId(plain, 'jev.describe.table:Product'), { items: plain.items }).some((a) => a.id === 'up'));
});

check('Approve only on a row that waits on the person (on free also a Ready Semanticus row with a worked-out set); On it shows no button', () => {
  const plan = makeHeartPlan();
  const approve = (free) => plan.items.filter((r) => P.mainAction(r, free)?.id === 'approve');
  assert.deepEqual(ids(approve(false)), [], 'on Pro a Ready Semanticus row is run by Run the plan, not approved by hand');
  assert.deepEqual(ids(approve(true)), ['safefix/Visibility', 'datetable/table:Date'], 'on free the Run button is Pro, so Approve stands in');
  assert.equal(P.mainAction(byId(plan, 'pt-7c01d4e9'), false), null);
  assert.equal(P.mainAction(byId(plan, 'jev.measures.table:Sales'), false), null, 'a page row (Choose) has no main button: its page link is on the row');
  assert.equal(P.mainAction(byId(plan, 'pt-a41b6f02'), false), null, 'the assistant holds this task');
});

check('Mark done is the main button when the person holds a task; Put back on a set-aside row; Save on the Save row; Open on a draft', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  const taken = heartClaim(plan, 'pt-3b7d0c21');
  assert.equal(P.mainAction(byId(taken, 'pt-3b7d0c21'), false)?.id, 'done');
  assert.equal(P.mainAction(byId(taken, 'pt-3b7d0c21'), false)?.primary, true);
  assert.equal(P.mainAction(byId(plan, 'pt-3f9a2c11'), false)?.id, 'done', 'a task for the person');
  const aside = { ...byId(plan, 'pt-3f9a2c11'), stateWord: 'set_aside', status: 'skipped' };
  assert.deepEqual(P.mainAction(aside, false), { id: 'putback', label: 'Put back' });
  const save = { ...byId(plan, 'pt-3f9a2c11'), id: 'ship/unsaved', doer: 'you', stateWord: 'needs_you', who: 'you' };
  assert.deepEqual(P.mainAction(save, false), { id: 'save', label: 'Save', primary: true });
  const draft = { ...byId(plan, 'safefix/Visibility'), id: 'draft/d-1', stateWord: 'needs_you', changeSet: null };
  assert.deepEqual(P.mainAction(draft, false), { id: 'open', label: 'Open', primary: true });
});

// Coordinator 26 Sep (golden rule 2): the cleanup is chosen on the Plan itself, as the agent door does it with
// model_plan_action propose, set_plan_item and approve. Round 1: opening the row works its lines out (what Show the changes did).
check('the cleanup row has no button until its lines are worked out, then Approve takes the kept ones', () => {
  const plan = makeHeartPlan();
  const base = { ...byId(plan, 'cleanup/unused-measures'), id: 'cleanup/model', doer: 'you', stateWord: 'needs_you',
    who: 'you', jump: { tool: 'lineage', label: 'Lineage' } };
  assert.equal(P.worksOutOnRow(base), true);
  assert.equal(P.mainAction({ ...base, changeSet: null }, false), null, 'opening the row works the lines out; no Show the changes button');
  const line = (id, kept) => ({ id, planItemId: id, line: '- Old Margin', op: 'delete_if_unused', target: 'measure:Measures/Old Margin', kept });
  const set = (kept) => ({ id: 'cs-1', state: 'proposed', digest: 'dg-1', lines: [line('ci-0001', kept), line('ci-0002', false)] });
  const dropped = P.mainAction({ ...base, changeSet: set(false) }, false);
  assert.ok(dropped && dropped.id === 'approve' && dropped.primary && dropped.disabled, 'Approve waits for a kept line');
  const keptOne = P.mainAction({ ...base, changeSet: set(true) }, false);
  assert.ok(keptOne && keptOne.id === 'approve' && keptOne.primary && !keptOne.disabled, 'one kept line is enough');
  // Any other derived choice has no main button: its page link is on the row.
  const other = { ...base, id: 'proposal/dates', jump: { tool: 'advmodels', label: 'Advanced modelling' } };
  assert.equal(P.mainAction(other, false), null);
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /P\.worksOutOnRow\(item\) && P\.stateWordOf\(item\) === 'needs_you' && P\.changeLines\(item\.changeSet\)\.length === 0/u, 'opening works the lines out');
  assert.match(page, /'modelPlanAction', item\.id, 'propose', 'human', null\)/u);
});

check('Keep all and Drop all name exactly the lines that would change, by their handles', () => {
  const plan = makeHeartPlan({ mode: 'cleanup' });
  const set = byId(plan, 'cleanup/model').changeSet;
  // Round 5 of m11/plan-harm: the cleanup is a suggestion list. Three lines arrive ticked (suggested), three arrive unticked.
  assert.deepEqual(set.lines.map((l) => l.kept), [true, true, true, false, false, false]);
  assert.ok(set.lines.every((l) => typeof l.reason === 'string' && l.reason.length > 0), 'every line says why it starts where it does');
  assert.deepEqual(set.lines.map((l) => !!l.suggested), [true, true, true, false, false, false], 'only a ticked line is a suggestion');
  assert.deepEqual(P.bulkRefs(set, true), ['ci-0004', 'ci-0005', 'ci-0006']);
  assert.deepEqual(P.bulkRefs(set, false), ['ci-0001', 'ci-0002', 'ci-0003']);
  set.lines[3].kept = true;
  assert.deepEqual(P.bulkRefs(set, true), ['ci-0005', 'ci-0006']);
  assert.deepEqual(P.bulkRefs({ ...set, state: 'applied' }, true), [], 'a receipt cannot be decided');
  assert.equal(P.KEEP_ALL, 'Keep all');
  assert.equal(P.DROP_ALL, 'Drop all');
});

check('the person side: Me takes an assistant task as your own, then Mark done; Could not is gone', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  assert.deepEqual(P.doerCall(byId(plan, 'pt-3b7d0c21'), 'you'), ['claimPlanTask', 'pt-3b7d0c21'], 'Me on an assistant task takes it');
  // An assistant holds the geography task: the doer menu is closed (first claim wins), the dots still set it aside
  // (UAT S12-5-002: the claimed row had no Set aside control).
  assert.equal(P.doerMenu(byId(plan, 'pt-a41b6f02')), null);
  assert.deepEqual(P.rowMenuActions(byId(plan, 'pt-a41b6f02'), { items: plan.items }).map((a) => a.id), ['aside', 'remove']);
  const taken = heartClaim(plan, 'pt-3b7d0c21');
  assert.deepEqual(P.mainAction(byId(taken, 'pt-3b7d0c21'), false)?.id, 'done');
  const done = heartComplete(taken, 'pt-3b7d0c21', 'Written.');
  assert.equal(byId(done, 'pt-3b7d0c21').stateWord, 'checking', 'a has_words task is checked, not assumed done');
  assert.equal(byId(done, 'pt-3b7d0c21').reply, 'Written.');
});

check('the doer menu says Give to your assistant in the Send buttons own words (U3 GIVE_LABEL)', () => {
  const send = read(join(src, 'sendtask.tsx'));
  const give = /export const GIVE_LABEL = '([^']+)';/u.exec(send)?.[1];
  assert.equal(P.GIVE_TO_ASSISTANT, give);
});

check('the doer menu is Semanticus, Your assistant, Me; Semanticus is faded with its reason where it cannot work', () => {
  const plan = makeHeartPlan();
  const letRun = (id) => P.doerMenu(byId(plan, id)).find((c) => c.doer === 'semanticus');
  assert.deepEqual(P.doerMenu(byId(plan, 'pt-9e2f7a10')).map((c) => c.label), ['Semanticus', 'Your assistant', 'Me']);
  assert.equal(letRun('pt-9e2f7a10').disabled, true, 'a task is never run by Semanticus');
  assert.equal(letRun('pt-9e2f7a10').why, 'Needs words', 'a task that needs words says so');
  assert.equal(letRun('pt-3f9a2c11').why, 'Semanticus cannot apply this one by itself');
  assert.equal(letRun('safefix/Visibility').disabled, false);
  // Closed where the doer is not the person's to change.
  assert.equal(P.doerMenu(byId(plan, 'pt-7c01d4e9')), null, 'On it');
  assert.equal(P.doerMenu({ ...byId(plan, 'safefix/Visibility'), stateWord: 'done' }), null, 'a finished row');
  assert.equal(P.doerMenu({ ...byId(plan, 'safefix/Visibility'), id: 'draft/d-1' }), null, 'a draft');
});

check('each doer choice makes one call: Your assistant and Semanticus set the doer; Me takes an assistant task or sets the doer', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  assert.deepEqual(P.doerCall(byId(plan, 'safefix/Visibility'), 'assistant'), ['setPlanItemDoer', 'safefix/Visibility', 'assistant']);
  assert.deepEqual(P.doerCall(byId(plan, 'pt-9e2f7a10'), 'you'), ['claimPlanTask', 'pt-9e2f7a10']);
  assert.deepEqual(P.doerCall(byId(plan, 'safefix/Visibility'), 'you'), ['setPlanItemDoer', 'safefix/Visibility', 'you']);
  assert.deepEqual(P.doerCall(byId(plan, 'pt-3f9a2c11'), 'semanticus'), ['setPlanItemDoer', 'pt-3f9a2c11', 'semanticus']);
  assert.equal(P.doerCall(byId(plan, 'safefix/Visibility'), 'semanticus'), null, 'the current doer changes nothing');
  assert.equal(P.doerCall(byId(plan, 'pt-3f9a2c11'), 'you'), null);
});

// ---- the list ------------------------------------------------------------------------------------------
check('Semanticus rows, then the assistant, then the rows only you can do in a fold; Next is capped at seven with the rest behind Show all', () => {
  const plan = makeHeartPlan();
  const view = P.workView(plan.items, { showAll: true });
  const doers = view.next.map((r) => P.doerOf(r));
  assert.deepEqual(doers, [...doers].sort((a, b) => ['semanticus', 'assistant'].indexOf(a) - ['semanticus', 'assistant'].indexOf(b)), 'Semanticus first, then the assistant');
  assert.ok(view.next.every((r) => P.doerOf(r) !== 'you'), 'no row of yours in Next');
  assert.ok(view.onlyYou.length > 0 && view.onlyYou.every((r) => P.doerOf(r) === 'you'), 'the fold holds your rows');
  assert.ok(view.onlyYou.some((r) => P.stateWordOf(r) === 'needs_you'), 'a question for you is in the fold (the fold shows a warn dot)');
  assert.equal(view.needsYou, undefined, 'the Needs you band is gone');
  const capped = P.workView(plan.items, {});
  assert.equal(capped.next.length, Math.min(P.NEXT_CAP, view.nextTotal));
  assert.equal(P.NEXT_CAP, 7);
  assert.equal(capped.nextTotal, view.next.length, 'Next counts the automated rows only');
});

// The space rule (27 September 2026): the page passes the rows that fit the window, and seven stays the floor.
check('Next shows the rows that fit the window, never fewer than seven', () => {
  const plan = makeHeartPlan();
  assert.equal(P.workView(plan.items, { cap: 9 }).next.length, 9);
  assert.equal(P.workView(plan.items, { cap: 40 }).next.length, 10);
  assert.equal(P.workView(plan.items, { cap: 3 }).next.length, P.NEXT_CAP);
});

// Journey m11 F8: answering an open draft's question made it Ready, so it left Needs you for the end of Next, past
// the cap, and the row the person was working on vanished from under them. The open row keeps its place.
check('the row a person has open stays in the fold, at its place, until they close it', () => {
  const plan = makeHeartPlan();
  const fold = P.workView(plan.items, {}).onlyYou;
  const second = fold[1];
  assert.ok(second, 'the sample has two rows of yours');
  // The person gives it to the assistant while it is open: it would leave the fold for Next.
  const moved = plan.items.map((i) => (i.id === second.id ? { ...i, doer: 'assistant', who: 'asst' } : i));
  assert.ok(!ids(P.workView(moved, {}).onlyYou).includes(second.id), 'without the keep it leaves the fold');
  const kept = P.workView(moved, { keep: { id: second.id, index: 1, group: 'you' } });
  assert.equal(kept.onlyYou[1].id, second.id, 'with it, it stays second in the fold');
  assert.ok(!ids(kept.next).includes(second.id), 'and is not drawn twice');
  assert.equal(kept.nextTotal, P.workView(moved, {}).nextTotal - 1, 'Next counts only what it draws');
  assert.deepEqual(ids(P.workView(plan.items, { keep: { id: second.id, index: 1, group: 'you' } }).onlyYou), ids(fold),
    'a row still in the fold is where it would be anyway');
  const done = plan.items.map((i) => (i.id === second.id ? { ...i, stateWord: 'done', status: 'done' } : i));
  assert.ok(!ids(P.workView(done, { keep: { id: second.id, index: 1, group: 'you' } }).onlyYou).includes(second.id), 'a closed row is not kept');
});

check('a track filter shows that doer whole, the assistant in the order it takes them', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  const view = P.workView(plan.items, { track: 'assistant' });
  assert.equal(view.filtered, true);
  assert.deepEqual(ids(view.rows), ['pt-a41b6f02', 'pt-3b7d0c21', 'pt-9e2f7a10', 'pt-0d6e33b8', 'pt-c4e1a9f3']);
  const obj = P.workView(makeHeartPlan().items, { object: 'table:Date' });
  assert.deepEqual(ids(obj.rows), ['pt-3f9a2c11', 'datetable/table:Date'], 'every open row that touches Date');
});

check('the goal filter files every row under exactly one goal, as the engine tags it; the filters are All, AI ready, Quality', () => {
  const plan = makeHeartPlan();
  const counts = { ai: 0, quality: 0, aims: 0 };
  for (const r of plan.items) counts[P.goalOf(r)]++;
  assert.equal(counts.ai + counts.quality + counts.aims, plan.items.length);
  assert.equal(P.goalOf(byId(plan, 'pt-9e2f7a10')), 'aims');
  assert.equal(P.goalOf(byId(plan, 'jev.describe.table:Product')), 'ai');
  assert.equal(P.goalOf(byId(plan, 'safefix/Visibility')), 'ai', 'the shape file tags the visibility fixes ai: an AI readiness rule found them');
  assert.equal(P.goalOf(byId(plan, 'cleanup/unused-measures')), 'quality');
  assert.deepEqual(P.GOAL_ORDER.map((g) => P.GOAL_LABEL[g]), ['All', 'AI ready', 'Quality']);
  // The engine's tag wins over the page guess (a relationships row jumps to Diagram, which is not an AI page).
  assert.equal(P.goalOf({ ...byId(plan, 'safefix/Visibility'), goal: 'quality' }), 'quality');
  assert.equal(P.goalOf({ ...byId(plan, 'jev.describe.table:Product'), goal: 'quality' }), 'quality');
  assert.equal(P.goalOf({ ...byId(plan, 'safefix/Visibility'), goal: 'aims' }), 'aims');
  const view = P.workView(plan.items, { goal: 'ai', showAll: true });
  assert.ok(ids(view.next).includes('safefix/Visibility'), 'the AI ready filter shows a row the engine tags ai');
  const flipped = P.workView(plan.items.map((r) => (r.id === 'safefix/Visibility' ? { ...r, goal: 'quality' } : r)), { goal: 'ai', showAll: true });
  assert.ok(!ids(flipped.next).includes('safefix/Visibility'), 'the AI ready filter leaves out a row the engine tags quality');
  assert.ok(!ids(view.next).some((id) => P.goalOf(byId(plan, id)) === 'quality'));
});

check('a row that fixes a rule both AI readiness and Model quality raise shows under both the AI ready and the Quality filter', () => {
  const plan = makeHeartPlan();
  const both = plan.items.map((r) => (r.id === 'safefix/Visibility' ? { ...r, goal: 'ai', goals: ['ai', 'quality'] } : r));
  const row = byId({ items: both }, 'safefix/Visibility');
  assert.deepEqual(P.goalsOf(row), ['ai', 'quality']);
  assert.equal(P.goalOf(row), 'ai', 'its own goal is still the first');
  for (const goal of ['ai', 'quality']) assert.ok(ids(P.workView(both, { goal, showAll: true }).next).includes('safefix/Visibility'), `the ${goal} filter shows it`);
  // A row with one goal, or an older engine that sends no goals, stays under that one goal.
  assert.deepEqual(P.goalsOf(byId(plan, 'cleanup/unused-measures')), ['quality']);
  assert.ok(!ids(P.workView(plan.items, { goal: 'quality', showAll: true }).next).includes('safefix/Visibility'));
});

// ---- the strip -----------------------------------------------------------------------------------------
check('the strip draws one segment per row up to twelve, then a count', () => {
  const big = { doer: 'semanticus', done: 9, onIt: 2, ready: 6, needsYou: 1, waiting: 2 };
  const { segs, more } = P.stripSegments(big);
  assert.equal(segs.length, 12);
  assert.equal(more, 8);
  assert.deepEqual(segs.slice(0, 11), [...Array(9).fill('done'), 'on_it', 'on_it']);
  assert.deepEqual(P.stripSegments({ doer: 'you', done: 2, onIt: 0, ready: 0, needsYou: 2, waiting: 0 }).segs,
    ['done', 'done', 'needs_you', 'needs_you']);
});

check('the three counts on a track say need you only when all of the open work does', () => {
  const [sem, you, asst] = P.stripTracks(makeHeartPlan());
  assert.deepEqual(P.trackCounts(sem).map((c) => c.text), ['5 done', '1 on it', '3 ready first', '1 follows it']);
  assert.deepEqual(P.trackCounts(you).map((c) => c.text), ['2 done', '0 on it', '2 need you']);
  assert.equal(P.trackCounts(you)[2].warn, true);
  assert.deepEqual(P.trackCounts(asst).map((c) => c.text), ['4 done', '1 on it', '2 ready', '2 waiting']);
  assert.equal(P.assistantChipCount(makeHeartPlan()), 3, 'the chip counts Ready plus On it');
});

check('the grade, Latest and updated lines, and the honest free line', () => {
  const plan = makeHeartPlan();
  assert.deepEqual(P.gradeLine(plan.strip), { now: 'B', after: 'A' });
  assert.deepEqual(P.latestLine(plan.strip), { time: '10:46', sentence: 'Semanticus hid 3 Promotion key columns', entryId: 'le-2c9d10f4' });
  assert.equal(P.updatedLine(plan.strip), 'Semanticus Pro updated the Plan at 10:46');
  assert.equal(P.updatedLine(makeHeartPlan({ free: true }).strip), 'Updated after your last change at 10:46');
});

// Honesty lane: the engine's own order sentence and its Jev-still-looking flag were on the wire and never drawn.
check('the order line is the engine sentence, and Jev looking is said only while it is', () => {
  assert.deepEqual(P.orderLines({ items: [], history: [], tables: [], orderedBy: 'In value order, highest first.', jevPending: false }),
    ['In value order, highest first.']);
  assert.deepEqual(P.orderLines({ items: [], history: [], tables: [], orderedBy: 'In the order the rules found them, while Jev works out which matter most.', jevPending: true }),
    ['In the order the rules found them, while Jev works out which matter most.', P.JEV_LOOKING_LINE]);
  assert.deepEqual(P.orderLines({ items: [], history: [], tables: [] }), [], 'an older engine that sends neither draws nothing');
  assert.deepEqual(P.orderLines({ items: [], history: [], tables: [], orderedBy: 'In the order the rules found them, because no suggestion service is set up, so nothing was sent and nothing changed.', jevPending: false }), [],
    'Jev simply not being set up is not news: no order line (Kane, 8 Oct)');
  assert.deepEqual(P.orderLines({ items: [], history: [], tables: [], orderedBy: 'Jev is not available right now. The Plan is in rule order.', jevPending: false }),
    ['Jev is not available right now. The Plan is in rule order.'], 'a real outage still says so, once');
  assert.ok(!/\u2014/.test(P.JEV_LOOKING_LINE));
});

// ---- the field -----------------------------------------------------------------------------------------
check('Enter on the field adds a row and never navigates', () => {
  assert.deepEqual(P.fieldSubmit('  Add a   Store hierarchy '), { words: 'Add a Store hierarchy' });
  assert.equal(P.fieldSubmit('   '), null);
  // UAT S12-1-005: a long paste was cut to 280 characters with no word said. Nothing is cut on the page now: the engine
  // keeps the whole text and makes its start the title, and the field says so before Enter.
  const long = Array.from({ length: 5000 }, (_, i) => `word${i + 1}`).join(' ');
  assert.equal(P.fieldSubmit(long).words, long);
  assert.equal(P.fieldHint('Add a Store hierarchy'), P.FIELD_HINT);
  assert.match(P.fieldHint(long), /whole text is kept/u);
  assert.doesNotMatch(P.fieldHint(long), /\u2014/u);
  const field = read(join(src, 'plan.tsx'));
  const input = field.slice(field.indexOf('data-testid="plan-field"'), field.indexOf('data-testid="plan-field"') + 400);
  assert.doesNotMatch(input, /maxLength/u, 'the field never cuts a paste');
  assert.equal(P.FIELD_PLACEHOLDER, 'Describe a task or an aim');
  const page = read(join(src, 'plan.tsx'));
  const keydown = page.slice(page.indexOf('data-testid="plan-field"'), page.indexOf('data-testid="plan-field"') + 900);
  assert.match(keydown, /addTask\(/u, 'Enter adds a task');
  assert.doesNotMatch(keydown, /onGo\(/u, 'Enter never navigates');
  const added = heartAdd(makeHeartPlan(), { words: 'Add a Store hierarchy' });
  const row = byId(added.plan, added.itemId);
  assert.equal(row.doer, 'assistant', 'a typed task starts with your assistant (Kane 24 Sep)');
});

// N15: the field is a growing textarea that keeps the person's line breaks. Enter adds; Shift+Enter is a new line.
check('the field keeps line breaks, Enter adds and Shift+Enter makes a new line', () => {
  const typed = 'Add a Store hierarchy\n  Country,   then City \n\n\n\nHide the keys  ';
  assert.deepEqual(P.fieldSubmit(typed), { words: 'Add a Store hierarchy\nCountry, then City\n\nHide the keys' });
  assert.deepEqual(P.fieldSubmit('one\r\ntwo'), { words: 'one\ntwo' });
  assert.equal(P.fieldSubmit(' \n \n '), null);
  assert.equal(P.fieldAddsOn({ key: 'Enter', shiftKey: false }), true);
  assert.equal(P.fieldAddsOn({ key: 'Enter', shiftKey: true }), false, 'Shift+Enter makes a new line');
  assert.equal(P.fieldAddsOn({ key: 'Enter', shiftKey: false, isComposing: true }), false, 'an IME composing is not an add');
  assert.equal(P.fieldAddsOn({ key: 'a', shiftKey: false }), false);
  const page = read(join(src, 'plan.tsx'));
  const at = page.indexOf('data-testid="plan-field"');
  const tag = page.slice(page.lastIndexOf('<', at), at);
  assert.match(tag, /<textarea\b/u, 'the field is a textarea, so it can hold more than one line');
  const around = page.slice(at, at + 900);
  assert.match(around, /P\.fieldAddsOn\(/u, 'Enter goes through the one rule');
  assert.match(around, /rows=\{1\}/u, 'the field starts one line tall and grows');
  assert.equal(P.FIELD_HINT, 'Enter adds it to the Plan. Shift+Enter starts a new line.');
});

// N9: a row's Objects field names its objects, or says how many there are, before it ever says nothing.
check('a row with no lines yet names its objects or says how many there are', () => {
  assert.equal(P.objectsLine({ id: 'x', objects: ['column:Sales/Amount', 'measure:Sales/Total'] }), 'Sales[Amount], Total');
  assert.equal(P.objectsLine({ id: 'x', objects: [], touches: '5 objects; no change until you choose, the page shows each case' }), '5 objects');
  assert.equal(P.objectsLine({ id: 'x', touches: '3 measures in Sales' }), '3 measures in Sales');
  assert.equal(P.objectsLine({ id: 'x' }), 'Nothing named yet.');
  const row = read(join(src, 'plan-row.tsx'));
  assert.match(row, /P\.objectsLine\(item\)/u, 'the row detail reads the one rule');
});

// ---- Run the plan --------------------------------------------------------------------------------------
check('Run the plan is one call a wave with no items and the readyDigest last; the engine takes the Ready Semanticus set', () => {
  const plan = makeHeartPlan();
  assert.deepEqual(ids(P.runSet(plan.items)), ['safefix/Visibility', 'datetable/table:Date', 'cleanup/unused-measures']);
  assert.deepEqual(P.runCall(plan), ['applyPlanItems', [], 'human', plan.readyDigest]);
  const held = heartHold(plan, 'cleanup/unused-measures', true);
  assert.equal(P.runSet(held.items).length, 2, 'a held row is skipped by Run the plan');
  assert.notEqual(held.readyDigest, plan.readyDigest, 'a hold moves the fence');
  const stale = heartRun(held, [], plan.readyDigest);
  assert.equal(stale.performed, false);
  assert.match(stale.sentence, /moved since you read it/u);
  const ran = heartRun(plan, [], plan.readyDigest);
  assert.equal(ran.applied, 3);
  assert.equal(byId(ran.plan, 'timeintel/measure:Sales/Sales Amount').stateWord, 'ready', 'its blocker ran, so it is Ready now');
});

// Round 1, K1: ONE press runs every wave, and the button counts the whole press.
check('pressSet counts a Semanticus row that waits only on a Ready Semanticus row, again and again', () => {
  const plan = makeHeartPlan();
  assert.deepEqual(ids(P.runSet(plan.items)).includes('datetable/table:Date'), true);
  // timeintel waits on the date table, which is Ready: it joins the press, so the button says 4, not 3.
  assert.deepEqual(ids(P.pressSet(plan.items)), ['safefix/Visibility', 'datetable/table:Date', 'timeintel/measure:Sales/Sales Amount', 'cleanup/unused-measures']);
  const chain = [
    { id: 'a', doer: 'semanticus', stateWord: 'ready', title: 'a' },
    { id: 'b', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'a' }], title: 'b' },
    { id: 'c', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'b', title: 'b' }], title: 'c' },
    { id: 'd', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'b', title: 'b' }, { id: 'x', title: 'x' }], title: 'd' },
    { id: 'e', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'a' }], held: true, title: 'e' },
    { id: 'f', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'a' }], title: 'f' },
    { id: 'draft/f', doer: 'semanticus', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'a' }], title: 'a draft' },
    { id: 'g', doer: 'assistant', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'a' }], title: 'g' },
    { id: 'x', doer: 'you', stateWord: 'needs_you', title: 'x' },
  ];
  assert.deepEqual(ids(P.pressSet(chain)), ['a', 'b', 'c', 'f'], 'b waits on a, c on b (transitively); d also waits on a person row, e is held, a draft and an assistant row never join');
  assert.deepEqual(ids(P.pressSet(chain.filter((r) => r.id !== 'a'))), [], 'with nothing Ready, nothing joins');
});

check('one press makes every wave: it calls again with the new readyDigest, stops at five, on a refusal and when nothing ran', async () => {
  const mk = (n) => ({ items: [{ id: `r${n}`, doer: 'semanticus', stateWord: 'ready', title: 'r' }], history: [], tables: [], readyDigest: `d${n}`, revision: n });
  const calls = [];
  const apply = async (call) => {
    calls.push(call[3]);
    const n = calls.length;
    return { performed: true, applied: 10 * n, itemIds: [`r${n}`], revision: n, entryId: `en${n}`, plan: n < 3 ? mk(n) : { ...mk(n), items: [] } };
  };
  const out = await P.runAllWaves(mk(0), apply, async () => { throw new Error('no re-read needed'); });
  assert.deepEqual(calls, ['d0', 'd1', 'd2'], 'each wave is fenced on the digest of the plan the last one answered with');
  assert.deepEqual(out.receipt, { kind: 'ran', rows: 3, changes: 60, entryId: 'en1', revision: 3, note: null }, 'the FIRST wave\'s entry is kept for Undo');
  // The engine runs every wave inside ONE call (contract change 1): one call, the whole press in its answer, canSave from the engine.
  const one = await P.runAllWaves(mk(0), async () => ({ performed: true, applied: 38, itemIds: ['r1', 'r2', 'r3'], revision: 2, entryId: 'en1', canSave: true, plan: { ...mk(2), items: [] } }), async () => mk(2));
  assert.deepEqual(one.receipt, { kind: 'ran', rows: 3, changes: 38, entryId: 'en1', revision: 2, note: null, canSave: true });
  // Five waves at most, even if the plan never runs dry.
  let n = 0;
  const forever = await P.runAllWaves(mk(0), async () => { n++; return { performed: true, applied: 1, itemIds: ['r'], revision: n, entryId: `e${n}`, plan: mk(n) }; }, async () => mk(0));
  assert.equal(n, P.MAX_WAVES);
  assert.equal(P.MAX_WAVES, 5);
  assert.equal(forever.receipt.rows, 5);
  // A refusal on the first wave is the answer, with no receipt; one on a later wave is said on the receipt.
  const refused = await P.runAllWaves(mk(0), async () => ({ performed: false, sentence: 'The plan moved since you read it. Look again.' }), async () => mk(0));
  assert.equal(refused.receipt, null);
  assert.equal(refused.refusal, 'The plan moved since you read it. Look again.');
  const fresh = { ...mk(2), items: [] };
  const shown = [];
  const refreshed = await P.runAllWaves(mk(0), async () => ({ performed: false, sentence: 'This row needs you.', plan: fresh }),
    async () => { throw new Error('the refusal already returned the plan'); }, p => shown.push(p));
  assert.equal(refreshed.plan, fresh, 'a refusal returns its fresh plan to the page');
  assert.deepEqual(shown, [fresh], 'a refusal updates the visible plan too');
  const handoff = 'Semanticus finished its tasks. 2 tasks are queued for your assistant. It sees them on its next call.';
  const handed = await P.runAllWaves(mk(0), async () => ({ performed: true, applied: 3, itemIds: ['r1'], note: handoff, plan: fresh }), async () => fresh);
  assert.equal(handed.receipt.note, handoff, 'the engine handoff stays beside the run receipt');
  let k = 0;
  const later = await P.runAllWaves(mk(0), async () => (++k === 1
    ? { performed: true, applied: 4, itemIds: ['r1'], revision: 1, entryId: 'e1', plan: mk(1) }
    : { performed: false, sentence: 'Stopped.' }), async () => mk(1));
  assert.equal(later.receipt.rows, 1);
  assert.equal(later.receipt.note, 'Stopped.');
  // A wave that throws after one wrote: the receipt says what was done, because the model HAS changed.
  let t = 0;
  const thrown = await P.runAllWaves(mk(0), async () => { if (++t === 1) return { performed: true, applied: 4, itemIds: ['r1'], revision: 1, entryId: 'e1', plan: mk(1) }; throw new Error('boom'); }, async () => mk(1));
  assert.equal(thrown.receipt.rows, 1);
  assert.equal(thrown.receipt.note, 'boom');
  await assert.rejects(P.runAllWaves(mk(0), async () => { throw new Error('first'); }, async () => mk(0)), /first/u, 'a first wave that throws changed nothing and throws');
  // A wave that ran nothing ends the press.
  let z = 0;
  const idle = await P.runAllWaves(mk(0), async () => { z++; return { performed: true, applied: 0, itemIds: [], revision: 1, plan: mk(0) }; }, async () => mk(0));
  assert.equal(z, 1);
  assert.equal(idle.receipt, null);
});

check('the receipt says the real counts, in the words of the mockup, and the run area says what is left in words', () => {
  assert.equal(P.ranWords(3), 'Ran 3 tasks.');
  assert.equal(P.ranWords(1), 'Ran 1 task.');
  assert.equal(P.changesWords(38), '38 changes, one undo step.');
  assert.equal(P.changesWords(1), '1 change, one undo step.');
  assert.equal(P.TELL_ASSISTANT, 'Work through my Semanticus plan.');
  const plan = makeHeartPlan();
  assert.deepEqual(P.runAreaOf(plan.items), { kind: 'run', count: 4 });
  const open = (doer) => ({ id: `o-${doer}`, doer, stateWord: 'ready', title: 'x' });
  assert.deepEqual(P.runAreaOf([open('assistant'), open('assistant'), open('you')]), { kind: 'assistant', count: 2 });
  assert.deepEqual(P.runAreaOf([open('you')]), { kind: 'you' });
  assert.deepEqual(P.runAreaOf([{ ...open('you'), stateWord: 'done' }]), { kind: 'done' });
  assert.deepEqual(P.runAreaOf([]), { kind: 'done' });
});

check('plan/runProgress: a row walks Working out, Applying, Checking, Done; a batch gives no row a count that is not its own', () => {
  let steps = {};
  const ev = (phase, itemIds, extra = {}) => ({ runId: 'r1', itemIds, phase, wave: 1, ms: 120, ...extra });
  steps = P.foldProgress(steps, ev('work', ['a']));
  assert.deepEqual(steps.a, { step: 'work', ms: 120 });
  steps = P.foldProgress(steps, ev('apply', ['a', 'b'], { changes: 34 }));
  assert.equal(steps.a.step, 'apply');
  assert.equal(steps.a.changes, undefined, 'a batch total is not a row\'s count');
  assert.equal(P.stepNote(steps.a), null);
  steps = P.foldProgress(steps, ev('apply', ['c'], { changes: 33 }));
  assert.equal(P.stepNote(steps.c), '33 changes');
  steps = P.foldProgress(steps, ev('check', ['a', 'b']));
  steps = P.foldProgress(steps, ev('done', ['a'], { changes: 1 }));
  assert.deepEqual([steps.a.step, P.stepNote(steps.a)], ['done', '1 change']);
  steps = P.foldProgress(steps, ev('skipped', ['b'], { reason: 'It changed while the run was going.' }));
  assert.equal(P.stepNote(steps.b), 'It changed while the run was going.');
  assert.equal(P.foldProgress(steps, { nonsense: true }), steps, 'a malformed event changes nothing');
  assert.deepEqual(Object.keys(P.RUN_STEP_WORD).sort(), ['apply', 'check', 'done', 'queued', 'skipped', 'work']);
  assert.deepEqual([P.RUN_STEP_WORD.work, P.RUN_STEP_WORD.apply, P.RUN_STEP_WORD.check, P.RUN_STEP_WORD.queued], ['Working out', 'Applying', 'Checking', 'In this run']);
  // A row the press holds and the run has not reached reads In this run.
  assert.deepEqual(P.stepOf('z', {}, new Set(['z'])), { step: 'queued' });
  assert.equal(P.stepOf('z', {}, null), null);
  assert.equal(P.stepOf('a', steps, new Set(['a'])).step, 'done', 'the engine\'s step wins over In this run');
  assert.deepEqual(P.runningWords(['a', 'b', 'c'], { a: { step: 'done' }, b: { step: 'apply' } }), { text: 'Running 2 of 3', fraction: 0.5 });
  assert.equal(P.runningWords(['a', 'b', 'c'], {}).text, 'Running 1 of 3');
});

check('Done just now: a row that turned done is kept in its own group, with the rest of the done rows left to History', () => {
  const plan = makeHeartPlan();
  const done = plan.items.map((i) => (i.id === 'safefix/Visibility' || i.id === 'pt-9e2f7a10' ? { ...i, stateWord: 'done', status: 'done' } : i));
  const without = P.workView(done, { showAll: true });
  assert.deepEqual(without.justDone, [], 'with no option, done rows are History\'s');
  const view = P.workView(done, { showAll: true, justDone: new Set(['safefix/Visibility', 'pt-9e2f7a10', 'not-a-row']) });
  assert.deepEqual(ids(view.justDone), ['safefix/Visibility', 'pt-9e2f7a10']);
  assert.ok(!ids(view.next).includes('safefix/Visibility'), 'and not in Next');
  // A family row that is open again (new findings under the same id) is a Next row, not a Done one.
  const again = done.map((i) => (i.id === 'safefix/Visibility' ? { ...i, stateWord: 'ready', status: 'ready' } : i));
  assert.deepEqual(ids(P.workView(again, { justDone: new Set(['safefix/Visibility']) }).justDone), []);
});

check('rowCoveringFinding: the Readiness page says On the plan for a finding a plan row already carries', () => {
  const rows = [
    { id: 'words/model', rules: ['AIR-DESC-TABLE', 'AIR-SYN'], objects: ['table:Sales', 'measure:Sales/Margin'], stateWord: 'ready' },
    { id: 'proposal/collisions', rules: ['AIR-COLLIDE'], objects: [], stateWord: 'needs_you' },
    { id: 'old', rules: ['AIR-OLD'], objects: [], stateWord: 'done' },
  ];
  assert.equal(P.rowCoveringFinding(rows, 'AIR-DESC-TABLE', 'table:Sales'), 'words/model');
  assert.equal(P.rowCoveringFinding(rows, 'air-desc-table', 'TABLE:sales'), 'words/model', 'matched without regard to case');
  assert.equal(P.rowCoveringFinding(rows, 'AIR-DESC-TABLE', 'table:Product'), null, 'a row that lists objects must hold this one');
  assert.equal(P.rowCoveringFinding(rows, 'AIR-COLLIDE', 'table:Anything'), 'proposal/collisions', 'a row that lists none carries the rule');
  assert.equal(P.rowCoveringFinding(rows, 'AIR-OLD', 'x'), null, 'a finished row carries nothing');
  assert.equal(P.rowCoveringFinding(null, 'AIR-OLD', 'x'), null);
  const app = read(join(src, 'App.tsx'));
  assert.doesNotMatch(app.slice(app.indexOf('function Hero('), app.indexOf('function Categories(')), /Review these together/u, 'the Readiness Hero no longer offers a button that sent nothing');
  assert.match(app, /<GiveOrOnPlan ruleId=\{f\.ruleId\} objectRef=\{f\.objectRef\}/u, 'a finding\'s control is On the plan or Give to your assistant');
});

check('the page source runs the plan through runAllWaves and has no one-at-a-time walk left', () => {
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /P\.runAllWaves\(/u);
  assert.match(page, /P\.runCall\(/u);
  assert.doesNotMatch(page, /runnableItems\(/u, 'the page-side walk is deleted (contract 7.2)');
  assert.doesNotMatch(page, /plan-gauge|TargetHeader|<Tile /u, 'the gauge, tiles and target card are gone');
  assert.doesNotMatch(read(join(src, 'plan-model.ts')), /'Ask your assistant'/u, '"Ask your assistant" sent nothing and is gone');
});

// K1 bug 3: the page's own re-read cleared the result sentence. The receipt has a state of its own and the re-read never touches it.
check('the receipt is held in its own state, and the page\'s re-read of the model never clears it', () => {
  const page = read(join(src, 'plan.tsx'));
  const reread = page.slice(page.indexOf('const mine = ++generation.current;'), page.indexOf('}, [sessionId, revision, changeNonce, retry]);'));
  assert.ok(reread.length > 100, 'found the re-read effect');
  assert.match(reread, /setSentence\(null\)/u, 'the re-read still clears the page\'s sentence');
  assert.doesNotMatch(reread, /setReceipt|setSteps|setJustDone/u, 'and nothing of the press');
  assert.match(page, /const \[receipt, setReceipt\] = useState<Receipt \| null>\(null\)/u);
  const planDidChange = page.slice(page.indexOf('const off = onPlanChange('), page.indexOf('return () => { off();'));
  assert.doesNotMatch(planDidChange, /setReceipt/u, 'a plan/didChange re-read leaves it too');
  // Only these end a receipt: the next press, an Undo, and a different model.
  const ends = [...page.matchAll(/setReceipt\(/gu)].length;
  assert.ok(ends >= 4 && ends <= 8, `setReceipt is called in a few known places (${ends})`);
  // Undo takes back the whole press: the FIRST wave's entry.
  assert.match(page, /rpc\('undo', 'human', receipt\.entryId\)/u);
});

// ---- argument order, which does not throw when it is wrong (kept from the retired page's section 19) ---------
check('the positional calls keep their order: digest last on approve, the line handle first on keep or drop', () => {
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /'modelPlanAction', item\.id, 'approve', 'human', item\.changeSet\?\.digest \?\? ''\)/u,
    'modelPlanAction(id, action, origin, digest): the digest is LAST');
  assert.match(page, /rpc\('setPlanItem', lineId, null, kept\)/u, 'setPlanItem(lineHandle, after, approved)');
  assert.match(page, /'setAssistantQueue', 'paused'\)/u);
  assert.match(page, /'completePlanTask', item\.id, text \|\| null\)/u);
  assert.doesNotMatch(page, /holdPlanItem|declinePlanTask/u, 'Pause and Could not are not called from the page any more (the engine verbs stay)');
  assert.match(page, /const call = P\.doerCall\(row, doer\);/u, 'the doer menu sends the one call doerCall names');
});

// ---- rendered pieces -----------------------------------------------------------------------------------
const Page = load(join(src, 'plan.tsx'));
check('an empty model offers its two starts, each with a button that says what it does', () => {
  const starts = [
    { id: 'sql', label: 'From a SQL source', line: 'Read the tables and keys from a SQL database into a draft.', jump: { tool: 'spec', label: 'Model Spec' } },
    { id: 'blank', label: 'New blank model', line: 'Start with nothing and add tables yourself.', jump: { tool: 'connections', label: 'Connections' } },
  ];
  const picked = [];
  const html = renderToStaticMarkup(React.createElement(Page.PlanStarts, { starts, onStart: (s) => picked.push(s.id) }));
  assert.match(html, /data-testid="plan-start-sql"/u);
  assert.match(html, /From a SQL source/u);
  assert.match(html, /data-testid="plan-start-blank-go"[^>]*>Create</u);
  assert.doesNotMatch(html, /Nothing to do on the plan/u);
  assert.doesNotMatch(html, /\u2014/u);
});

check('the ways to start show whenever the engine sends them, even with a row open (verify 28 Sep, H3)', () => {
  // A new model's words row, or a task the person typed, is an open row: the starts must not wait for none to be open.
  const page = readFileSync(join(src, 'plan.tsx'), 'utf8');
  const view = page.slice(page.indexOf('export function PlanView'));
  const starts = view.indexOf('<PlanStarts');
  const emptyBranch = view.indexOf('items.filter(P.isOpenRow).length === 0');
  assert.ok(starts > 0 && emptyBranch > 0, 'PlanView draws PlanStarts and has its empty branch');
  assert.ok(starts < emptyBranch, 'PlanStarts is drawn before, and outside, the no-open-rows branch');
  assert.match(view.slice(starts - 80, starts), /showPlan\.starts\?\.length && $/u);
});

check('free shows the Pro line where Run the plan stands, never a dead button', () => {
  stubTier = 'denied';
  const base = { plan: makeHeartPlan(), running: false, onRun: () => {} };
  const free = renderToStaticMarkup(React.createElement(Page.RunArea, { ...base, grant: 'denied' }));
  assert.match(free, /data-testid="plan-pro-line"/u);
  assert.doesNotMatch(free, /data-testid="plan-run"/u);
  const pro = renderToStaticMarkup(React.createElement(Page.RunArea, { ...base, grant: 'granted' }));
  assert.match(pro, /data-testid="plan-run"/u);
  assert.match(pro, />Run the plan\s*</u);
  assert.match(pro, />4<\/span>/u, 'the count is the whole press');
  stubTier = 'pro';
});

check('the run area: Running N of M with a bar, Your assistant has N, Only you can do the rest, Plan done; never a grey button', () => {
  const plan = makeHeartPlan();
  const html = (props) => renderToStaticMarkup(React.createElement(Page.RunArea, { plan, grant: 'granted', running: false, onRun: () => {}, ...props }));
  const running = html({ running: true, held: ['a', 'b', 'c'], steps: { a: { step: 'done' } } });
  assert.match(running, /data-testid="plan-run-live"[^>]*>Running 2 of 3</u);
  assert.match(running, /pw-run-bar/u);
  assert.doesNotMatch(running, /<button/u, 'while it runs there is no button to look dead');
  const items = (doer, n) => Array.from({ length: n }, (_, i) => ({ id: `${doer}${i}`, title: 't', doer, stateWord: 'ready' }));
  const asst = html({ plan: { ...plan, items: items('assistant', 3) }, told: false });
  assert.match(asst, /Your assistant has <b class="sem-num">3<\/b>/u);
  assert.match(asst, /data-testid="plan-tell"[^>]*>Copy request</u);
  assert.doesNotMatch(asst, /disabled/u);
  assert.match(html({ plan: { ...plan, items: items('assistant', 1) }, told: true }), /data-testid="plan-told"[^>]*>Copied\. Paste it into your assistant\.</u);
  assert.match(html({ plan: { ...plan, items: items('you', 2) } }), /data-testid="plan-run-you"[^>]*>The remaining tasks need your choice\. Open one to see what to choose\.</u);
  // One task reads as one (hero16 N8: "1 tasks need your choice"), and the line says what the choice is for.
  assert.match(html({ plan: { ...plan, items: items('you', 1) } }), /data-testid="plan-run-you"[^>]*>The remaining task needs your choice\. Open it to see what to choose\.</u);
  assert.match(html({ plan: { ...plan, items: items('assistant', 1) } }), /Your assistant has <b class="sem-num">1<\/b> task</u);
  assert.match(html({ plan: { ...plan, items: [] } }), /data-testid="plan-run-done"/u);
  assert.match(html({ plan: { ...plan, items: [] } }), />Plan done</u);
  for (const m of [running, asst]) assert.doesNotMatch(m, /\u2014/u);
});

check('Done just now says what its count leaves out: a task that was no longer needed was not run, and earlier done tasks are counted in the strip (hero16 N8, H7)', () => {
  const done = (id, note) => ({ id, title: id, doer: 'semanticus', stateWord: 'done', status: 'done', stateNote: note });
  const strip = (n) => ({ tracks: [{ doer: 'semanticus', done: n, onIt: 0, ready: 0, needsYou: 0, waiting: 0 }] });
  const closed = P.justDoneNote([done('a'), done('b', 'No longer needed')], { items: [], strip: strip(2) });
  assert.match(closed, /^Open one to see what it changed\. 1 was no longer needed, so it was not run\.$/u);
  assert.match(P.justDoneNote([done('a')], { items: [], strip: strip(6) }), /5 more tasks finished earlier\.$/u, 'the strip says 6 done and 1 is listed, so the line accounts for the other 5');
  assert.match(P.justDoneNote([done('a')], { items: [], strip: strip(2) }), /1 more task finished earlier\.$/u);
  assert.equal(P.justDoneNote([done('a')], { items: [], strip: strip(1) }), 'Open one to see what it changed.');
});

check('the receipt: Ran 3 tasks. 38 changes, one undo step. with Undo, Save and See in History; Saved once saved; Undo only while the model has not moved on', () => {
  const ran = { kind: 'ran', rows: 3, changes: 38, entryId: 'en-1', revision: 2, note: null, saved: false };
  const html = (props) => renderToStaticMarkup(React.createElement(Page.RunReceipt, { receipt: ran, canUndo: true, canSave: true, busy: false, onUndo: () => {}, onSave: () => {}, onHistory: () => {}, ...props }));
  const full = html({});
  assert.match(full, />Ran 3 tasks\.</u);
  assert.match(full, />38 changes, one undo step\.</u);
  assert.match(full, /data-testid="plan-run-undo"[^>]*>Undo</u);
  assert.match(full, /data-testid="plan-run-save"[^>]*>Save</u);
  assert.match(full, /data-testid="plan-run-history"[^>]*>See in History</u);
  const saved = html({ receipt: { ...ran, saved: true } });
  assert.match(saved, /data-testid="plan-run-saved"[^>]*>Saved\.</u);
  assert.doesNotMatch(saved, /plan-run-save"/u, 'one press saves; nothing saves by itself');
  const moved = html({ canUndo: false });
  assert.doesNotMatch(moved, /plan-run-undo/u, 'Undo takes back everything from the first batch on, so it goes once the model moved');
  assert.match(moved, /The model has changed since\. History can take it back\./u);
  assert.doesNotMatch(html({ canSave: false }), /plan-run-save/u, 'a live model is never saved from here');
  assert.match(html({ receipt: { kind: 'said', text: 'The plan moved since you read it. Look again.' } }), /The plan moved since you read it\. Look again\./u);
  assert.doesNotMatch(full, /\u2014/u);
});

check('the line beside "16 changes" never says "Nothing was changed"; a live model saves from the same Save button, with its own words', async () => {
  assert.equal(P.laterWaveNote('Nothing was changed. 1 row was left out: add 2 measures (Jev is looking again).'), '1 row was left out: add 2 measures (Jev is looking again).');
  assert.equal(P.laterWaveNote('Nothing was changed, because of mark date. It cannot be made.'), 'It stopped because of mark date. It cannot be made.');
  assert.equal(P.laterWaveNote('Nothing was changed.'), null);
  assert.equal(P.laterWaveNote(null), null);
  assert.equal(P.laterWaveNote('The plan moved since you read it. Read it again.'), 'The plan moved since you read it. Read it again.');
  const mk = (n) => ({ items: [{ id: `r${n}`, doer: 'semanticus', stateWord: 'ready', title: 'r' }], history: [], tables: [], readyDigest: `d${n}`, revision: n });
  let k = 0;
  const out = await P.runAllWaves(mk(0), async () => (++k === 1
    ? { performed: true, applied: 16, itemIds: ['r1'], revision: 1, entryId: 'e1', canSave: true, plan: mk(1) }
    : { performed: false, sentence: 'Nothing was changed. 1 row was left out: x.' }), async () => mk(1));
  assert.equal(out.receipt.note, '1 row was left out: x.');
  const html = renderToStaticMarkup(React.createElement(Page.RunReceipt, { receipt: { ...out.receipt, saved: true, savedText: 'Saved 16 changes to Northstar Sales Working.' }, canUndo: true, canSave: true, busy: false, onUndo: () => {}, onSave: () => {}, onHistory: () => {} }));
  assert.match(html, /data-testid="plan-run-saved"[^>]*>Saved 16 changes to Northstar Sales Working\.</u);
  assert.doesNotMatch(html, /Nothing was changed/u);
  const page = readFileSync(join(src, 'plan.tsx'), 'utf8');
  assert.match(page, /hostSession\?\.liveBound/u, 'the Save press reads whether the model is live');
  assert.match(page, /saveToService\(\)/u, 'a live model saves to its workspace');
});

// A composer draft that is Ready is Semanticus's to run, but with its own Approve and run (slice 10), never inside
// Run the plan's batch: the engine leaves it out of the Ready set (PlanRunState.cs IsReady). Counted here, the
// button said 1 and applied nothing (Sol 6 slice 9 review).
const readyDraft = {
  ...byId(makeHeartPlan(), 'safefix/Visibility'), id: 'draft/d-0a1b2c3d', title: 'Add a Store hierarchy',
  doer: 'semanticus', stateWord: 'ready', stateNote: null, held: false, changeSet: null, jump: null,
  sub: 'Ready. It runs as one change.',
  originLink: { kind: 'draft', ref: 'd-0a1b2c3d' }, waitsFor: [],
};
check('a Ready draft is not in Run the plan: with only a draft Ready, the run area shows no count and offers nothing', () => {
  const plan = { ...makeHeartPlan(), items: [readyDraft], readyDigest: 'digest-of-nothing' };
  assert.deepEqual(P.runSet(plan.items), [], 'the draft is not counted');
  assert.equal(P.runCall(plan), null, 'and Run the plan offers no call');
  const html = renderToStaticMarkup(React.createElement(Page.RunArea, { plan, grant: 'granted', running: false, onRun: () => {} }));
  assert.doesNotMatch(html, /data-testid="plan-run"/u, 'with only a draft Ready there is no Run button, grey or otherwise');
  assert.doesNotMatch(html, /pw-run-n/u, 'and no count, not even 0');
  // Alongside ordinary Ready rows the draft is still left out.
  const mixed = { ...makeHeartPlan(), items: [...makeHeartPlan().items, readyDraft] };
  assert.deepEqual(ids(P.runSet(mixed.items)), ['safefix/Visibility', 'datetable/table:Date', 'cleanup/unused-measures']);
  assert.ok(!ids(P.pressSet(mixed.items)).includes('draft/d-0a1b2c3d'), 'and out of the press');
});

const Row = load(join(src, 'plan-row.tsx'));
check('the waiting line names who acts next on the task it waits for', () => {
  const base = byId(makeHeartPlan(), 'timeintel/measure:Sales/Sales Amount');
  const at = (doer) => P.subLine({ ...base, waitsFor: [{ ...base.waitsFor[0], ...(doer === undefined ? {} : { doer }) }] });
  assert.equal(at('you').who, 'Waits for you');
  assert.equal(at('assistant').who, 'Waits for your assistant');
  assert.equal(at('semanticus').who, 'Waits for Semanticus');
  assert.equal(at(undefined).who, 'Waits for');
  assert.equal(at(null).who, 'Waits for');
  assert.equal(at('you').waits, 'Mark Date as the date table');
  const html = renderToStaticMarkup(React.createElement(Row.PlanRow, {
    item: { ...base, waitsFor: [{ ...base.waitsFor[0], doer: 'you' }] }, items: [], open: false, busy: null,
    onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {},
  }));
  assert.match(html, /Waits for you: <b>Mark Date as the date table<\/b>/u);
});

check('a row draws source, title, sub or Waits for, doer, state, page link and actions', () => {
  const plan = makeHeartPlan();
  const html = renderToStaticMarkup(React.createElement(Row.PlanRow, {
    item: byId(plan, 'timeintel/measure:Sales/Sales Amount'), items: plan.items, open: false, busy: null,
    onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {},
  }));
  assert.match(html, /Found by Semanticus/u);
  assert.match(html, /Add year to date and last year measures/u);
  assert.match(html, /Waits for: <b>Mark Date as the date table<\/b>/u, 'no doer on the wait keeps the plain words');
  assert.match(html, /Semanticus/u);
  assert.match(html, /Waiting/u);
  assert.match(html, /Edit › Advanced modelling/u);
  assert.match(html, /data-testid="plan-doer-btn"/u, 'the doer is a button that opens the menu');
  assert.match(html, /data-testid="plan-row-more"/u, 'and the dots hold the rest');
  assert.doesNotMatch(html, />Pause<|>Resume<|>Choose<|>Answer<|>Up</u, 'no control from the old row');
  assert.doesNotMatch(html, /data-testid="plan-act-/u, 'a Waiting Semanticus row needs nothing from you, so it has no main button');
});

// Verifier follow-up (v-runplan): a row that waits for a row the person set aside says so plainly, and offers Put back for THAT row.
check('a row that waits for a set-aside row names it as set aside and offers Put back for the row it waits for', () => {
  const plan = makeHeartPlan();
  const blocker = { ...byId(plan, 'datetable/table:Date'), status: 'skipped', stateWord: 'set_aside' };
  const items = plan.items.map((i) => (i.id === blocker.id ? blocker : i));
  const item = byId(plan, 'timeintel/measure:Sales/Sales Amount');
  const sub = P.subLine(item, items);
  assert.equal(sub.aside?.id, 'datetable/table:Date');
  const acted = [];
  const html = renderToStaticMarkup(React.createElement(Row.PlanRow, {
    item, items, open: false, busy: null,
    onToggle: () => {}, onAction: (row, action) => acted.push([row.id, action]), onDoer: () => {}, onGo: () => {},
  }));
  assert.match(html, /Waits for &#x27;Mark Date as the date table&#x27;, which you set aside/u);
  assert.doesNotMatch(html, /Waits for: <b>/u, 'the plain waits line is replaced, not added to');
  assert.match(html, /data-testid="plan-act-putback"[^>]*title="Put back &#x27;Mark Date as the date table&#x27;"[^>]*>Put back</u);
  // A row that waits for an open row keeps the plain line and no Put back.
  const plain = renderToStaticMarkup(React.createElement(Row.PlanRow, {
    item, items: plan.items, open: false, busy: null, onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {},
  }));
  assert.match(plain, /Waits for: <b>Mark Date as the date table<\/b>/u);
  assert.doesNotMatch(plain, /plan-act-putback/u);
  assert.equal(P.subLine(item, plan.items).aside, undefined);
});

check('a row shows its step in the state column, a person row is dimmed and last, a finished row sits in Done just now', () => {
  const plan = makeHeartPlan();
  const item = byId(plan, 'safefix/Visibility');
  const row = (props) => renderToStaticMarkup(React.createElement(Row.PlanRow, { item, items: plan.items, open: false, busy: null,
    onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {}, ...props }));
  const apply = row({ step: { step: 'apply', changes: 33 } });
  assert.match(apply, /pw-state-spin pw-state-run/u);
  assert.match(apply, /data-step="apply"[\s\S]*>Applying<[\s\S]*>33 changes</u);
  assert.match(apply, /class="pw-row pw-row-live"/u, 'the engine tint');
  assert.match(row({ step: { step: 'work' } }), />Working out</u);
  assert.match(row({ step: { step: 'check' } }), />Checking</u);
  assert.match(row({ step: { step: 'queued' } }), /pw-state-faint[\s\S]*>In this run</u);
  const done = row({ item: { ...item, stateWord: 'done' }, step: { step: 'done', changes: 4 }, justDone: true });
  assert.match(done, /pw-state-good[\s\S]*>Done<[\s\S]*>4 changes</u);
  assert.match(done, /pw-row-justdone/u);
  assert.match(row({ item: { ...byId(plan, 'jev.describe.table:Product'), stateWord: 'done' }, justDone: true }), />by your assistant</u);
  // UAT-R2-S10b-004: a row another change closed says so, and never claims its owner did the work.
  const closed = row({ item: { ...byId(plan, 'jev.describe.table:Product'), stateWord: 'done', stateNote: 'No longer needed', why: ['Fix 7 best-practice issues closed this.'] }, justDone: true });
  assert.match(closed, /No longer needed/u);
  assert.doesNotMatch(closed, /by your assistant/u);
  const yours = row({ item: byId(plan, 'pt-3f9a2c11') });
  assert.match(yours, /class="pw-row pw-row-you"/u);
  assert.doesNotMatch(apply, /pw-row-you/u);
  const css = read(join(src, 'styles.css'));
  assert.match(css, /\.pw-fold \.pw-row-you \{ opacity: 0\.62; \}/u, 'dimmed in the fold only');
  assert.match(css, /\.pw-fold \.pw-row-you:hover[^{]*\{ opacity: 1; \}/u, 'full on hover');
});

check('the list: Semanticus rows, then the assistant, then a fold; the fold is closed, shows a warn dot, and the Needs you band is gone', () => {
  const page = read(join(src, 'plan.tsx'));
  assert.doesNotMatch(page, /plan-needs|work\.needsYou|pw-needs/u);
  assert.match(page, /data-testid="plan-only-you"/u);
  assert.match(page, /P\.onlyYouWords\(work\.onlyYou\)/u);
  assert.equal(P.onlyYouWords([{ stateWord: 'needs_you' }]), 'task needs your choice', 'one task reads "1 task needs your choice"');
  assert.match(page, /const \[foldOpen, setFoldOpen\] = useState\(false\)/u, 'closed by default');
  assert.match(page, /plan-only-you-warn/u);
  assert.match(page, /Done just now/u);
});

check('a Ready draft row says its own state: it runs on its own, not with Run the plan, with no Approve here yet', () => {
  const html = renderToStaticMarkup(React.createElement(Row.PlanRow, {
    item: readyDraft, items: [readyDraft], open: false, busy: null, onToggle: () => {}, onAction: () => {}, onGo: () => {},
  }));
  assert.match(html, /data-testid="plan-row-draft"[^>]*>Runs on its own, not with Run the plan</u);
  assert.doesNotMatch(html, /one undo\b/u, 'a step-by-step draft makes an undo step per run of changes, so the row never promises one');
  assert.match(html, /not part of Run the plan/u, 'its title says why the count leaves it out');
  assert.doesNotMatch(html, /data-testid="plan-act-approve"/u, 'Approve and run is slice 10 (lane U5), not here');
  // Only a Ready draft says it: an ordinary Ready row, and a draft in any other state, do not.
  assert.equal(P.draftOwnState(byId(makeHeartPlan(), 'safefix/Visibility')), null);
  assert.equal(P.draftOwnState({ ...readyDraft, stateWord: 'needs_you', doer: 'you' }), null);
});

check('the open row is for reading: why, done when, objects, the note and reply, and no control rows', () => {
  const plan = makeHeartPlan({ mode: 'queue' });
  const html = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: byId(plan, 'pt-3b7d0c21'), busy: null, onNote: () => {},
  }));
  for (const words of ['Done when', 'Objects', 'Your note', 'Its reply', 'Why', 'Save note', 'Keep each under 20 words.']) {
    assert.ok(html.includes(words), `detail says ${words}`);
  }
  for (const gone of ['Who does it', 'Give to your assistant', "I&#x27;ll do it", 'Let it run', 'Where it is done', 'Queue', "I&#x27;ll take it", 'Set aside',
    'Could not', 'Mark done', 'Steps', 'Repeats', 'Draft the steps', 'Pause']) {
    assert.ok(!html.includes(gone), `detail no longer says ${gone}`);
  }
  assert.match(html, /No reply yet\. It reads this on its next call\./u);
  assert.doesNotMatch(html, /plan-repeat/u, 'the repeat editor is drawn only when the dots asked for it');
  const rep = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: byId(plan, 'safefix/Visibility'), busy: null, onNote: () => {}, showRepeat: true, isPro: true, repeats: [],
    onRepeat: { add: () => {}, change: () => {}, remove: () => {} },
  }));
  assert.match(rep, /data-testid="plan-repeat"/u, 'Repeat... in the dots draws the existing editor inside the open row');
});

check('Give to your assistant on a found row reaches the assistant as a task through the doer menu (contract 3.2 and 4)', () => {
  const plan = makeHeartPlan();
  const describe = byId(plan, 'jev.describe.table:Product');
  assert.equal(describe.doer, 'assistant', 'a Describe row is the assistant\'s by its kind');
  assert.ok(P.givesAsTask(describe), 'and still offers Give, because the queue offers tasks only');
  assert.deepEqual(P.doerCall(describe, 'assistant'), ['setPlanItemDoer', 'jev.describe.table:Product', 'assistant'], 'choosing Your assistant on it makes the task');
  assert.equal(P.givesAsTask(byId(plan, 'safefix/Visibility')), false, 'a row Semanticus runs is not handed over from the queue');
  assert.equal(P.givesAsTask(byId(plan, 'pt-3f9a2c11')), false, 'a task is already a task');
  assert.equal(P.doerCall(byId(plan, 'pt-9e2f7a10'), 'assistant'), null, 'a task that is already the assistant\'s changes nothing');
});

check('A row with edits filed under it says what it changed and opens them in History (contract 1.6)', () => {
  const plan = makeHeartPlan();
  const done = { ...byId(plan, 'safefix/Visibility'), stateWord: 'done', events: ['ed-00000001', 'ed-00000002'] };
  let opened = null;
  const html = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: done, busy: null, onNote: () => {},
    onHistory: (e) => { opened = e; },
  }));
  assert.ok(html.includes('What it changed'));
  assert.ok(html.includes('2 changes, filed in History'));
  assert.ok(html.includes('Open in History'));
  const none = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: { ...done, events: [] }, busy: null, onNote: () => {},
  }));
  assert.ok(!none.includes('What it changed'), 'a row with nothing filed under it says nothing');
  assert.equal(P.eventsLine(1), '1 change, filed in History');
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /onHistory=\{\(entry\) => \{ setRowEntry\(entry\); setView\('hist'\); \}\}/u, 'the link switches to History on that edit');
  assert.equal(opened, null);
});

check('A track lists its Done rows under its open rows, and a Done row opens with what it changed (contract 1.6, 9)', () => {
  const plan = makeHeartPlan();
  const done = { ...byId(plan, 'safefix/Visibility'), id: 'safefix/Done', title: 'Hide 3 key columns', stateWord: 'done',
    status: 'done', events: ['ed-00000009'] };
  const checking = { ...byId(plan, 'datetable/table:Date'), id: 'datetable/Checking', stateWord: 'checking' };
  const theirs = { ...byId(plan, 'pt-9e2f7a10'), id: 'pt-0000d0e1', stateWord: 'done' };
  const items = [...plan.items, done, checking, theirs];
  const view = P.workView(items, { track: 'semanticus' });
  assert.deepEqual(ids(view.done), ['safefix/Done', 'datetable/Checking'], 'the track\'s Done and Applied, checking rows, as the strip counts them');
  assert.ok(!ids(view.rows).includes('safefix/Done'), 'and not among its open rows');
  assert.deepEqual(ids(P.workView(items, { track: 'assistant' }).done), ['pt-0000d0e1'], 'each track its own');
  assert.deepEqual(P.workView(items, {}).done, [], 'the unfiltered list keeps no Done group');
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /data-testid="plan-done"/u, 'the filtered list draws a Done group');
  assert.match(page, /work\.done\.map\(\(r\) => renderRow\(r\)\)/u, 'each Done row opens like any row');
});

// ---- what the assistant sees ---------------------------------------------------------------------------
check('What your assistant sees is the engine field on the view; the page keeps no port of the fold', () => {
  const src = read(join(root, 'webview', 'src', 'plan-model.ts'));
  assert.doesNotMatch(src, /export function assistantSees/u, 'the TS port of the fold line is gone');
  assert.doesNotMatch(src, /FOLD_ALL_INDEPENDENT|FOLD_FOOTER/u, 'and so is its copy of the engine sentences');
  const page = read(join(root, 'webview', 'src', 'plan.tsx'));
  assert.match(page, /showPlan\.assistantSees/u, 'the queue panel shows the engine field');
  // The mock engine the drives use plays the same text the engine writes over the same rows
  // (Semanticus.Tests/PlanHeartFoldParityTests.cs holds the engine to these two files).
  const fixtures = join(root, 'tools', 'uishot', 'fixtures');
  const on = read(join(fixtures, 'plan-heart-fold-on.txt'));
  const paused = read(join(fixtures, 'plan-heart-fold-paused.txt'));
  const queued = makeHeartPlan({ mode: 'queue' });
  assert.equal(queued.assistantSees, on);
  assert.equal(heartQueue(queued, 'paused').assistantSees, paused);
  // The track numbers the offer as the engine numbered it, and numbers nothing while paused.
  assert.deepEqual([...P.offerNumbers(queued)], [['pt-3b7d0c21', 1], ['pt-9e2f7a10', 2]]);
  assert.equal(P.offerNumbers(heartQueue(queued, 'paused')).size, 0);
  assert.match(heartUp(queued, 'pt-9e2f7a10').assistantSees, /1\. Describe 8 Product measures/u);
});

check('Offer numbers come from the engine, a draft part sharing its draft number', () => {
  const plan = { assistantQueue: 'on', items: [
    { id: 'pt-00000001', title: 'a', offerNumber: 1 }, { id: 'pt-00000002', title: 'b', offerNumber: 2 },
    { id: 'pt-00000003', title: 'c', offerNumber: 2 }, { id: 'describe/model', title: 'd', offerNumber: null },
  ] };
  assert.deepEqual([...P.offerNumbers(plan)], [['pt-00000001', 1], ['pt-00000002', 2], ['pt-00000003', 2]]);
});

check('Remove (Kane, UAT 25 Sep): every task and draft row offers it in its menu; a found row and a draft part do not', () => {
  const plan = makeHeartPlan({ mode: 'plain' });
  const byId = (id) => plan.items.find((i) => i.id === id);
  const task = byId('pt-3f9a2c11');
  assert.deepEqual(P.rowMenuActions(task).map((a) => a.label), ['Set aside', 'Remove']);
  assert.deepEqual(P.rowMenuActions(byId('safefix/Visibility')).map((a) => a.label), ['Set aside'], 'a found row is set aside, not removed');
  assert.deepEqual(P.rowMenuActions({ ...task, id: 'draft/dr-00000001', status: 'ready' }).map((a) => a.label), ['Remove'], 'a draft is removed with its draft');
  assert.deepEqual(P.rowMenuActions({ ...task, stateWord: 'set_aside' }).map((a) => a.label), ['Remove'], 'a set-aside task has Put back as its main button and Remove in the dots');
  assert.equal(P.removable({ ...task, originLink: { kind: 'draft', ref: 'dr-00000001' } }), false, 'a draft part goes with its draft');
  assert.equal(P.removable({ ...task, id: 'draft/dr-00000001', status: 'ready' }), true);
  assert.equal(P.removable({ ...task, id: 'draft/dr-00000001', status: 'running' }), false, 'a running draft finishes first');
  // A task the assistant holds can still be removed: the person decides, and the assistant is told.
  assert.equal(P.removable({ ...task, stateWord: 'on_it', doer: 'assistant', claim: { claimedBy: 'assistant' } }), true);
  assert.ok(P.rowMenuActions({ ...task, doer: 'assistant', stateWord: 'ready' }, { items: plan.items }).some((a) => a.id === 'remove'), 'the dots offer Remove beside Set aside');
  // The rest of the dots, each only where it fits: Draft steps on a task from your words (Pro), Repeat... where a repeat can be made.
  const words = { ...task, source: 'words', stateWord: 'ready', doer: 'assistant' };
  assert.ok(P.rowMenuActions(words, { items: plan.items, grant: 'granted' }).some((a) => a.id === 'draft' && !a.why), 'Draft steps on Pro');
  assert.equal(P.rowMenuActions(words, { items: plan.items, grant: 'denied' }).find((a) => a.id === 'draft').why, 'Pro', 'faded with its reason on free');
  assert.ok(!P.rowMenuActions(words, { items: plan.items, grant: 'unknown' }).some((a) => a.id === 'draft'), 'nothing while the licence is being read');
  assert.ok(P.rowMenuActions(words, { items: plan.items, canRepeat: true }).some((a) => a.id === 'repeat' && a.label === 'Repeat\u2026'));
  assert.ok(!P.rowMenuActions(words, { items: plan.items }).some((a) => a.id === 'repeat'));
});

// ---- the column rows say who chose each line (m11/m4-columns-fair verifier D2, 26 Sep) -------------------
check('On a column row Jev works, a line the principled rule chose says "rule"; a Jev line says how sure', () => {
  const line = (id, after, provenance) => ({ id, planItemId: 'jev.column-summarize.table:Sales', line: `${id}: summarize by ${after}`,
    op: 'set_summarize_by', target: `column:Sales/${id}`, property: 'summarizeBy', before: 'None', after, kept: true, provenance,
    reason: 'r', check: { exists: true, text: 'c' } });
  const set = { id: 'jev.column-summarize.table:Sales', state: 'proposed', digest: 'd', lines: [
    line('UnitPrice', 'Average', { kind: 'decision', point: 'column.summarize', confidence: 0.93, confidenceWord: 'sure' }),
    line('Amount', 'Sum', { kind: 'rule', rule: 'SUMMARIZE-PRINCIPLED' }),
  ] };
  const html = renderToStaticMarkup(React.createElement(Page.ChangeList, { set, position: 0, busy: null, onDecideLine: () => {}, markRules: true }));
  assert.match(html, /data-testid="plan-jevword-0-UnitPrice"[^>]*>.*?>sure</u);
  assert.match(html, /data-testid="plan-ruleword-0-Amount"[^>]*>.*?>rule</u);
  assert.doesNotMatch(html, /plan-ruleword-0-UnitPrice/u, 'a Jev line carries its confidence, not "rule"');
  assert.match(read(join(src, 'plan.tsx')), /markRules=\{item\.who === 'jev'\}/u, 'the Plan marks rule lines on the rows Jev works');
  // Good practice applied to every column is not "the rule standing in for an unsure Jev": its marker says so.
  assert.equal(P.ruleWordTitle({ provenance: { kind: 'rule', rule: 'SUMMARIZE-OFF' } }), P.POLICY_WORD_TITLE);
  assert.equal(P.ruleWordTitle({ provenance: { kind: 'rule', rule: 'SUMMARIZE-PRINCIPLED' } }), P.RULE_WORD_TITLE);
  });

// ---- what Copilot sees (verifier D5 and the copy lines, 26 Sep) -------------------------------------------
check('On a row Jev works, a line its rule chose says "rule" where a Jev line says how sure Jev was', () => {
  const line = (id, provenance, kept = true) => ({ id, planItemId: id, line: `${id}: leave out of what Copilot sees`, op: 'set_ai_data_schema',
    target: `column:Sales/${id}`, property: 'aiDataSchema', before: 'Included', after: 'Excluded', kept, provenance, reason: 'r', check: { exists: true, text: 'c' } });
  const set = { id: 'jev.ai-schema.table:Sales', state: 'proposed', digest: 'd', lines: [
    line('LineKey', { kind: 'decision', point: 'ai.schema', confidence: 0.95, confidenceWord: 'sure' }),
    line('CustomerKey', { kind: 'rule', rule: 'AI-SCHEMA-RULE' }),
  ] };
  const html = renderToStaticMarkup(React.createElement(Page.ChangeList, { set, position: 0, busy: null, onDecideLine: () => {}, markRules: true }));
  assert.match(html, /data-testid="plan-jevword-0-LineKey"[^>]*>.*?>sure</u);
  assert.match(html, /data-testid="plan-ruleword-0-CustomerKey"[^>]*>.*?>rule</u);
  assert.doesNotMatch(html, /plan-ruleword-0-LineKey/u, 'a Jev line carries its confidence, not "rule"');
  const plain = renderToStaticMarkup(React.createElement(Page.ChangeList, { set, position: 0, busy: null, onDecideLine: () => {} }));
  assert.doesNotMatch(plain, /plan-ruleword/u, 'a row the rules run on their own carries no marker');
  // The property reads in plain words, never the engine's camelCase name.
  assert.match(html, /What Copilot sees/u);
  assert.doesNotMatch(html, /aiDataSchema/u);
  const page = read(join(src, 'plan.tsx'));
  assert.match(page, /markRules=\{item\.who === 'jev'\}/u, 'the Plan marks rule lines on the rows Jev works');
});

check('property names read as words: a known one by its label, any other split at its capitals', () => {
  assert.equal(P.propertyWords('aiDataSchema'), 'What Copilot sees');
  assert.equal(P.propertyWords('formatString'), 'Format');
  assert.equal(P.propertyWords('someNewSetting'), 'Some new setting');
  assert.equal(P.propertyWords('Expression'), 'Expression');
  assert.equal(P.changeValues({ property: 'isHidden', before: 'false', after: 'true' }).propertyWords, 'Hidden');
});

check('a row Semanticus runs does not promise that anybody reads a note on its next call', () => {
  const plan = makeHeartPlan();
  const semanticus = byId(plan, 'safefix/Visibility');
  assert.equal(P.doerOf(semanticus), 'semanticus');
  const html = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: { ...semanticus, reply: null }, busy: null, onNote: () => {},
  }));
  assert.doesNotMatch(html, /next call/u);
  assert.equal(P.noReplyWords('assistant', {}), 'No reply yet. It reads this on its next call.');
  assert.equal(P.noReplyWords('you', {}), 'No reply yet.');
});

check('the Objects line names what the best-practice and words rows touch, not "Nothing named yet" (verify M5)', () => {
  const tables = [{ name: 'Sales', count: 40, what: '24 columns, 7 measures' }, { name: 'Customer', count: 30, what: '20 columns' },
    { name: 'Product', count: 9, what: '9 columns' }, { name: 'Date', count: 4, what: '4 columns' }, { name: 'Region', count: 2, what: '2 columns' }];
  assert.equal(P.objectsLine({ objects: ['column:Sales/Amount', 'measure:Sales/Total'], touches: '2 objects' }), 'Sales[Amount], Total', 'named objects read as before');
  const many = P.objectsLine({ objects: [], touches: '85 objects', tables });
  assert.equal(many, '85 objects in Sales (24 columns, 7 measures), Customer (20 columns), Product (9 columns) and 2 more tables.');
  assert.doesNotMatch(many, /Nothing named yet/u);
  assert.equal(P.objectsLine({ objects: [], touches: '3 objects', tables: [tables[0]] }), '3 objects in Sales (24 columns, 7 measures).');
  const noTables = P.objectsLine({ objects: [], touches: '12 objects', diff: ['~ Sales[Money]: measures should carry a format', '+ Sales[Share]: a description', '~ Customer[City]: x', '~ Customer[Id]: y'] });
  assert.equal(noTables, 'Sales[Money], Sales[Share], Customer[City] and 9 more.');
  assert.equal(P.objectsLine({ objects: [], touches: 'no change until you choose' }), 'Nothing named yet.', 'a row with nothing to say still says so');
  // The rendered detail of the two rows uses it.
  const plan = makeHeartPlan();
  const html = renderToStaticMarkup(React.createElement(Row.PlanRowDetail, {
    item: { ...byId(plan, 'safefix/Visibility'), objects: [], touches: '85 objects', tables }, busy: null, onNote: () => {},
  }));
  assert.match(html, /85 objects in Sales \(24 columns, 7 measures\)/u);
});

check('the strip states waiting tasks and says which tasks can run first', () => {
  assert.deepEqual(P.trackCounts({ doer: 'semanticus', done: 0, onIt: 0, ready: 1, needsYou: 0, waiting: 13 }).map((c) => c.text),
    ['0 done', '0 on it', '1 ready first', '13 follow it']);
  assert.deepEqual(P.trackCounts({ doer: 'semanticus', done: 0, onIt: 0, ready: 2, needsYou: 1, waiting: 3 }).map((c) => c.text),
    ['0 done', '0 on it', '2 ready first', '1 need you', '3 follow it']);
});
check('a Ready assistant task keeps its next-check note visible', () => {
  const html = renderToStaticMarkup(React.createElement(Row.StateWord, {
    item: { id: 'words', doer: 'assistant', stateWord: 'ready', stateNote: 'when your assistant next checks the Plan' },
  }));
  assert.match(html, /class="pw-state-note">when your assistant checks</u);
});
check('a waiting task names every dependency', () => {
  const item = { id: 'format', stateWord: 'waiting', waitsFor: [{ id: 'a', title: 'Add measures' }, { id: 'b', title: 'Rename columns' }] };
  assert.equal(P.subLine(item).waits, 'Add measures; Rename columns');
});
await Promise.all(pending);
if (failures) { console.error(`\n${failures} plan-heart check(s) failed`); process.exit(1); }
console.log('\nplan-heart: all checks passed');

// Kane, 12 Oct: the lane counts used the monospace number font and wrapped inside a phrase ("4 ready first" onto a second line).
check('the lane counts use the Studio UI font with tabular figures and break only between counts', () => {
  const css = readFileSync(new URL('../webview/src/styles.css', import.meta.url), 'utf8');
  const src = readFileSync(new URL('../webview/src/plan-strip.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(src, /className="pw-counts sem-num"/, 'the monospace number class is not on the counts');
  const rule = css.match(/\.pw-counts \{[^}]*\}/)[0];
  assert.match(rule, /font-family: var\(--sem-font-ui\)/);
  assert.match(rule, /font-variant-numeric: tabular-nums/);
  assert.match(rule, /flex-wrap: wrap/);
  assert.match(css, /\.pw-counts > span \{ white-space: nowrap; \}/, 'each count stays whole');
  assert.match(css, /\.pw-track \{[^}]*grid-template-columns: 144px minmax\(0, 1fr\) minmax\(max-content, 380px\)/, 'at full width a lane keeps its counts on one line');
  assert.match(css, /@media \(max-width: 640px\) \{\s*\.pw-track \{ grid-template-columns: minmax\(0, 1fr\)/, 'on a narrow window the lane stacks');
});

check('the Plan order line carries a Billing link only when the engine sends a billing address, and the header chip re-reads the plan', () => {
  const plan = readFileSync(new URL('../webview/src/plan.tsx', import.meta.url), 'utf8');
  assert.match(plan, /showPlan\.billingUrl && <button[^\n]*data-testid="plan-billing"[^\n]*>Billing<\/button>/);
  assert.match(plan, /<ProPlanWatch inactive=\{!!showPlan\.billingUrl\} \/>/);
  const model = readFileSync(new URL('../webview/src/plan-model.ts', import.meta.url), 'utf8');
  assert.match(model, /billingUrl\?: string;/);
});
