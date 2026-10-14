// The assistant's next step on the Studio side (1.5), run against the real model and store with a fake engine.
//   the Next bar under the header is gone from every page (Kane, 13 Oct); the Plan header's slot is the one place a step shows
//   Ask builds addPlanTask with the step's words, doer assistant, source page suggest and send key next:<id>; Do it maps each op
//   steady (slot-calm): a failed read, a step-less answer or the rules' answer over Jev's pick never blinks the step out
//   no string has an em dash or "one click"
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview } from './helpers/webview-load.mjs';

const calls = [];
let answer = null;
let pushed = null;
const bridge = {
  rpc: async (method, ...params) => { calls.push([method, ...params]); if (answer instanceof Error) throw answer; return answer; },
  saveModelFromPage: async () => { calls.push(['saveModelFromPage']); return true; },
  onStudioNextStep: (fn) => { pushed = fn; return () => {}; },
  onReconnect: () => () => {},
};
const load = makeLoader({ '../bridge': bridge, './studio-notes.css': {} });
const M = load('studio/next-model.ts');
const L = load('studio/next-line.tsx');

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}

// The mock's five moments, word for word (option A, MOMENTS).
const DOIT = {
  save: { op: 'save', label: 'Save', done: 'Saved.' },
  checks: { op: 'run_checks', label: 'Run checks', done: 'Checks running.' },
  wf: { op: 'draft_workflow', label: 'Draft workflow', done: 'Draft workflow made. Open Workflows to see it.' },
};
export const MOMENTS = [
  { key: 'open', stage: 2, kind: 'run-plan', ask: 'Ask your assistant to run the plan', doIt: null, src: 'From your Plan',
    why: { free: '14 tasks are ready. They run in Plan order.', pro: '14 tasks are ready. Jev put "Mark Date as the date table" first.' } },
  { key: 'changed', stage: 2, kind: 'save', ask: 'Save your changes', doIt: DOIT.save, src: 'Not saved yet',
    why: { free: '6 changes are not saved yet.', pro: '6 changes are not saved yet. Save before the next task.' } },
  { key: 'plandone', stage: 3, kind: 'check-then-publish', lead: 'Your plan is done.', ask: 'Check the numbers, then publish to test', doIt: DOIT.checks, src: 'Plan done',
    why: { free: '12 checks test the measures the plan changed.', pro: '12 checks test the measures the plan changed. Then test is the next stop.' } },
  { key: 'saved', stage: 3, kind: 'run-checks', ask: 'Run your checks', doIt: DOIT.checks, src: 'Saved, checks not run',
    why: { free: '12 checks test the measures you changed. They have not run since your save.', pro: '12 checks test the measures you changed. Run them before you publish.' } },
  { key: 'repeat', stage: 7, kind: 'workflow', ask: 'Save these steps as a workflow', doIt: DOIT.wf, src: 'Same steps 3 times',
    why: { free: 'You did the same 3 steps on 4 tables.', pro: 'You did the same 3 steps on 4 tables. 3 more tables need them.' } },
];
export function viewOf(m, tier) {
  return { id: `ns-${m.key}0000000`.slice(0, 15), on: true, stage: m.stage, stageName: 'Get it AI ready', hidden: false,
    step: { kind: m.kind, lead: m.lead ?? '', ask: m.ask, why: m.why[tier], source: tier === 'pro' ? 'Jev chose this' : m.src,
      chosenBy: tier === 'pro' ? 'jev' : 'rules', prompt: `Please: ${m.ask.toLowerCase()}.`, doIt: m.doIt } };
}
await check('Ask builds the right addPlanTask payload; the press leaves its text in place', async () => {
  const v = viewOf(MOMENTS[0], 'free');
  assert.deepEqual(M.askRequest(v, v.step), { words: 'Please: ask your assistant to run the plan.', doer: 'assistant', sourcePage: 'suggest', sendKey: `next:${v.id}` });
  L.nextStore.set(v); L.nextStore.press(v.id, M.ADDED_TEXT);
  assert.equal(L.nextStore.getSnapshot().pressed.text, 'Added. Tell Claude go.', 'the press leaves its text in place');
  L.nextStore.set({ ...v, id: 'ns-other000000' });
  assert.equal(L.nextStore.getSnapshot().pressed, null, 'a new step clears the press');
});

// ---- the steady step (slot-calm, 13 Oct): the step never blinks out while the engine works out the next one ----------------------
const steady = (L2 = load('studio/next-store.ts')) => {
  let t = 0; const timers = [];
  const store = L2.createNextStore(() => t, (fn, ms) => { const e = { at: t + ms, fn }; timers.push(e); return () => { e.fn = null; }; });
  const tick = (ms) => { t += ms; for (const e of [...timers]) if (e.fn && e.at <= t) { const f = e.fn; e.fn = null; f(); } };
  return { store, tick };
};
const jevView = (key) => viewOf(MOMENTS.find((m) => m.key === key), 'pro');
const rulesView = (key) => viewOf(MOMENTS.find((m) => m.key === key), 'free');

await check('steady: a failed read or a step-less answer keeps the step on show, then lets go after 4 s', () => {
  const { store, tick } = steady();
  store.set(jevView('changed'));
  store.set(null);
  assert.equal(store.getSnapshot().view?.step?.ask, 'Save your changes', 'a lost read is not an empty slot');
  store.set({ ...jevView('changed'), step: null });
  assert.equal(store.getSnapshot().view?.step?.ask, 'Save your changes', 'a recompute with no step yet is not an empty slot');
  tick(3900);
  assert.equal(store.getSnapshot().view?.step?.ask, 'Save your changes');
  tick(200);
  assert.equal(store.getSnapshot().view?.step ?? null, null, 'after 4 s there really is no step');
});

await check('steady: the rules answer does not replace the pick of Jev while Jev is still choosing', () => {
  const { store, tick } = steady();
  store.set(jevView('changed'));
  store.set(rulesView('saved'));
  assert.equal(store.getSnapshot().view.step.kind, 'save', 'still the step on show');
  tick(1300);
  store.set(jevView('saved'));
  assert.equal(store.getSnapshot().view.step.kind, 'run-checks', 'Jev landed: one change');
  tick(5000);
  assert.equal(store.getSnapshot().view.step.kind, 'run-checks', 'the held rules answer never shows afterwards');
});

await check('steady: a rules answer that Jev never replaces shows after 3 s; a real change on Free shows at once', () => {
  const a = steady();
  a.store.set(jevView('changed')); a.store.set(rulesView('saved')); a.tick(3100);
  assert.equal(a.store.getSnapshot().view.step.kind, 'run-checks');
  const b = steady();
  b.store.set(rulesView('changed')); b.store.set(rulesView('saved'));
  assert.equal(b.store.getSnapshot().view.step.kind, 'run-checks');
});

await check('steady: off and hidden are the person\'s word, so they show at once', () => {
  const { store } = steady();
  store.set(jevView('changed'));
  store.set({ ...jevView('changed'), hidden: true, step: null });
  assert.equal(store.getSnapshot().view.hidden, true);
  store.set(jevView('changed'));
  store.set({ ...jevView('changed'), on: false, step: null });
  assert.equal(store.getSnapshot().view.on, false);
});

await check('steady: the same step coming again changes nothing and keeps a press', () => {
  const { store, tick } = steady();
  const v = jevView('changed');
  store.set(v); store.press(v.id, M.ADDED_TEXT);
  store.set(null); store.set({ ...v });
  tick(10000);
  assert.equal(store.getSnapshot().view.id, v.id);
  assert.equal(store.getSnapshot().pressed?.text, M.ADDED_TEXT);
});

await check('Do it maps each op to the existing engine call', () => {
  assert.deepEqual(M.doItCall('save', {}), { kind: 'save' });
  assert.deepEqual(M.doItCall('run_checks', {}), { kind: 'rpc', method: 'runTests', params: [false, 'human', null, null] });
  assert.equal(M.doItCall('draft_workflow', { prompt: 'p' }).method, 'composeTask');
  assert.equal(M.doItCall('launch', {}), null);
});

await check('a failed engine read leaves no step', async () => {
  L.nextStore.set({ id: 'x', on: false, stage: 1 });   // nothing on show, so nothing is held
  answer = new Error('no such method'); await L.loadNextStep();
  assert.equal(L.nextStore.getSnapshot().view, null);
  answer = null;
});

await check('copy: no em dash and no "one click" in the next step words', () => {
  const src = ['studio/next-model.ts', 'studio/next-line.tsx', 'studio/next-store.ts'].map((f) => readFileSync(join(webview, 'src', f), 'utf8')).join('\n');
  const all = [src, ...MOMENTS.flatMap((m) => [m.ask, m.src, m.why.free, m.why.pro, m.lead ?? ''])].join('\n');
  assert.ok(!all.includes('—'), 'em dash');
  assert.ok(!/one click/i.test(all));
});

await check('the pushed studioNextStep message replaces the view', () => {
  // Mounting registers the listener; SSR runs no effect, so call the bridge hook the way the effect does.
  L.nextStore.set(null);
  const v = viewOf(MOMENTS[3], 'free');
  bridge.onStudioNextStep((x) => L.nextStore.set(x));
  pushed(v);
  assert.equal(L.nextStore.getSnapshot().view.id, v.id);
});

// ---- the assistant's own suggestions panel stays (Kane, 13 Oct: "Kane LIKES this and it must stay") -------------------------------
// "Here is what I would do next." with up to three buttons and "From your plan: ..." is the suggest_next feature in What I did.
// It is not the Next bar. Removing the bar must leave it drawn and working exactly as it was.
const pins = [];
const sbridge = { rpc: async (m, ...p) => { pins.push([m, ...p]); return null; } };
const noteStore = { notesStore: { markStepAdded: (i) => pins.push(['markStepAdded', i]), markStepFailed: () => {} } };
const loadS = makeLoader({ '../bridge': sbridge, './notes-runtime': noteStore, './studio-notes.css': {} });
const Sg = loadS('studio/suggest.tsx');
const G = loadS('studio/suggest-model.ts');
const React = (await import('node:module')).createRequire(join(webview, 'package.json'))('react');
const { renderToStaticMarkup } = (await import('node:module')).createRequire(join(webview, 'package.json'))('react-dom/server');
const KANE_SUGGESTION = {
  id: 'sg-000000000050', say: 'Here is what I would do next.', at: '2026-10-13T09:30:00.000Z', sessionId: 's1',
  steps: [
    { label: 'Apply the metadata fixes', prompt: 'Apply the metadata fixes the model check found.' },
    { label: 'Fix the Headcount measure', prompt: 'Fix the Headcount measure.' },
    { label: 'Write measure and table descriptions', prompt: 'Write measure and table descriptions.' },
  ],
};

await check('the suggestions panel still draws the assistant\'s words and its three buttons, as in Kane\'s screenshot', () => {
  const html = renderToStaticMarkup(React.createElement(Sg.Suggest, { view: { suggestion: KANE_SUGGESTION, added: [], failed: null } }));
  assert.ok(html.includes('data-testid="suggest"') && html.includes('aria-label="Next steps"'));
  assert.ok(html.includes('Here is what I would do next.'));
  const labels = [...html.matchAll(/data-testid="suggest-step"[^>]*>([^<]*)</g)].map((m) => m[1]);
  assert.deepEqual(labels, ['Apply the metadata fixes', 'Fix the Headcount measure', 'Write measure and table descriptions']);
  assert.ok(html.includes('class="sem-btn sem-btn-sm"'), 'the same buttons');
});

await check('the panel\'s buttons keep working: a press adds the words to the Plan for the assistant and marks the step added', async () => {
  pins.length = 0;
  const step = G.stepsOf(KANE_SUGGESTION)[1];
  assert.deepEqual(G.taskRequest(KANE_SUGGESTION, 1, step), { words: 'Fix the Headcount measure.', doer: 'assistant', sourcePage: 'suggest', sendKey: 'suggest:sg-000000000050:1' });
  const src = readFileSync(join(webview, 'src', 'studio', 'suggest.tsx'), 'utf8');
  assert.match(src, /rpc\('addPlanTask', taskRequest\(suggestion, index, steps\[index\]\)\)/);
  assert.match(src, /notesStore\.markStepAdded\(index\)/);
  assert.equal(G.fromYourPlan(G.stepsOf(KANE_SUGGESTION), { id: 'q', title: 'Fix 16 Model quality findings' }), 'From your plan: Fix 16 Model quality findings');
  const done = renderToStaticMarkup(React.createElement(Sg.Suggest, { view: { suggestion: KANE_SUGGESTION, added: [1], failed: null } }));
  assert.ok(done.includes('Added. Tell Claude go.'), 'a pressed step turns into the added text');
});

await check('the panel is still mounted under the note strip and keeps its stylesheet rules (the Next bar removal left it alone)', () => {
  const strip = readFileSync(join(webview, 'src', 'studio', 'note-strip.tsx'), 'utf8');
  assert.match(strip, /\{suggestion && <Suggest view=\{suggestion\} \/>\}/);
  const css = readFileSync(join(webview, 'src', 'studio', 'studio-notes.css'), 'utf8');
  for (const cls of ['.sn-suggest', '.sn-steps', '.sn-added', '.sn-failed', '.sn-say', '.sn-how']) assert.ok(css.includes(cls), `${cls} rule kept`);
});

await check('the header slot reads the same suggestion: the engine takes the assistant\'s first step as the next step', () => {
  const cs = readFileSync(join(webview, '..', '..', 'Semanticus.Engine', 'LocalEngine.NextStep.cs'), 'utf8');
  assert.match(cs, /CurrentAssistantSuggestion\(\)/);
  assert.match(cs, /Ask = first\.Label/, 'the slot shows the first button of the same suggest_next');
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('next-line: all passed');
