// Send to your assistant (Plan at the heart, lane U3): every "Ask AI" button that copied a prompt becomes
// "Give to your assistant" with Copy beside it. Giving posts ONE Plan task through the person's own add-task
// control (the human-only addPlanTask), whose doer is your assistant and whose brief is the very prompt Copy
// copies. Once given, the button reads "On the plan" and opens the Plan. The DAX field keeps Copy only.
//
// Three kinds of check, as in plan-page.test.mjs:
//   1. The REQUESTS are pure functions in sendtask.tsx, executed here: what each control class asks for, how done
//      is known, the link back, and that Copy copies the same text the task carries.
//   2. The COMPONENT is rendered with react-dom/server through a loader that stubs only the bridge, and its one
//      click path (give, then On the plan) is driven through a fake React-free harness of the same functions.
//   3. The WIRING at every call site is read off the real source: each control mounts the shared component, the
//      DAX field does not, and no page still says "Ask AI".
// The clicks in a real browser are tools/uishot/cp-send-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const calls = [];
const copies = [];
const navigations = [];
// The engine's plan as the page reads it back after a reconnect (getModelPlan), set per scene below.
let enginePlan = { items: [] };
const reconnects = new Set();
const planChanges = new Set();
const STUBS = {
  './bridge': {
    rpc: async (method, ...params) => {
      calls.push({ method, params });
      if (method === 'addPlanTask') return { itemId: 'pt-0a1b2c3d', action: 'add', sentence: 'Added to the plan for your assistant. It is offered on its next call.' };
      if (method === 'getModelPlan') return enginePlan;
      throw new Error('unexpected call ' + method);
    },
    copyText: async (text) => { copies.push(text); return true; },
    navigateInStudio: (tab) => { navigations.push(tab); },
    onReconnect: (fn) => { reconnects.add(fn); return () => { reconnects.delete(fn); }; },
    onPlanChange: (fn) => { planChanges.add(fn); return () => { planChanges.delete(fn); }; },
  },
};
// The re-read a reconnect starts is coalesced with any other in the same moment, so the scene waits past that.
const reconnect = async () => { for (const fn of reconnects) fn(); await new Promise((r) => setTimeout(r, 120)); };
// plan/didChange, as the engine sends it when a task is done, declined or set aside. The re-read it starts is
// coalesced (one landing can publish twice), so the scene waits past that before it looks.
const planChanged = async () => { for (const fn of planChanges) fn({ revision: 9 }); await new Promise((r) => setTimeout(r, 120)); };
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => STUBS[name] ?? (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const srcOf = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&#x27;/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

const S = load(join(webview, 'src', 'sendtask.tsx'));

// ===================================================================================================
// 1. The words on the controls. Plain, and no assistant product is ever named.
// ===================================================================================================
assert.equal(S.GIVE_LABEL, 'Give to your assistant');
assert.equal(S.ON_PLAN_LABEL, 'On the plan');
assert.equal(S.COPY_LABEL, 'Copy');
assert.match(S.NEXT_CALL_NOTE, /next call/, 'the UI says when the assistant sees it');
const allCopy = [S.GIVE_LABEL, S.ON_PLAN_LABEL, S.COPY_LABEL, S.COPIED_LABEL, S.NEXT_CALL_NOTE, S.GIVE_TITLE, S.ON_PLAN_TITLE, S.COPY_TITLE].join('\n');
for (const product of ['Claude', 'Codex', 'Copilot', 'ChatGPT', 'Jev']) assert.ok(!allCopy.includes(product), `no product name in the copy: ${product}`);
assert.ok(!/\u2014/.test(allCopy), 'no em dash in the copy');

// ===================================================================================================
// 2. What each control class asks for (contract 1.5 and the send inventory's classes).
// ===================================================================================================
const BRIEF = 'Write a business description for Sales[Total Sales]. Use set_description.';

// An AI readiness finding: Semanticus checks the model, linked to the finding, so the rescan closes it.
const air = S.findingTask('air', { ruleId: 'DESC-MEASURE', ruleTitle: 'Measures have a description', objectRef: 'measure:Sales/Total Sales', objectName: 'Total Sales' }, BRIEF);
assert.deepEqual(air, {
  words: 'Fix "Measures have a description" on Total Sales',
  brief: BRIEF,
  sourcePage: 'readiness',
  objects: ['measure:Sales/Total Sales'],
  doneWhen: 'model_check',
  originLink: { kind: 'finding', ref: 'DESC-MEASURE|measure:Sales/Total Sales' },
});
assert.equal(air.doer, undefined, 'the doer is left to the engine, whose default for addPlanTask is your assistant');

// A model quality finding: Semanticus checks the model too. The engine reads the Model quality scan for a finding
// sent from that page (LocalEngine.Plan.cs, IsBpaFinding), so the rescan that drops it closes the task (contract 1.5).
const bpa = S.findingTask('bpa', { ruleId: 'AVOID_FLOAT', ruleName: 'Do not use floating point data types', objectRef: 'column:Sales/Amount', objectName: 'Amount' }, 'Fix this BPA violation.');
assert.equal(bpa.doneWhen, 'model_check');
assert.equal(bpa.sourcePage, 'bpa');
assert.deepEqual(bpa.originLink, { kind: 'finding', ref: 'AVOID_FLOAT|column:Sales/Amount' });
assert.deepEqual(bpa.objects, ['column:Sales/Amount']);
// Advice (the engine answers Tool "(advice)": a format rule whose fix the format fill rule withholds) is a review, not a
// fix. The title says so, and done is what the assistant says: the finding rightly stays when the person keeps the format
// they have, so a model check would push the assistant toward the very write no fix may make.
const advice = S.findingTask('bpa', { ruleId: 'INTEGER_FORMATTING', ruleName: 'Whole numbers', objectRef: 'measure:Sales/Util', objectName: 'Util' }, 'It is advice, not a fault to fix.', true);
assert.equal(advice.words, 'Review the advice "Whole numbers" on Util');
assert.equal(advice.doneWhen, 'assistant_says');
assert.deepEqual(advice.originLink, { kind: 'finding', ref: 'INTEGER_FORMATTING|measure:Sales/Util' });

// Read-only and slot classes: your assistant says. Each carries its page as the link back.
const explain = S.explainTask('Total Sales', '1,204', 'Explain why the measure "Total Sales" shows 1,204.');
assert.equal(explain.doneWhen, 'assistant_says');
assert.equal(explain.sourcePage, 'daxlab');
assert.equal(explain.words, 'Explain why Total Sales shows 1,204');
const storage = S.storageTask({ ruleName: 'High cardinality column', ref: 'column:Sales/OrderId', table: 'Sales', column: 'OrderId' }, 'A column is flagged.');
assert.equal(storage.doneWhen, 'assistant_says');
assert.equal(storage.sourcePage, 'stats');
assert.deepEqual(storage.objects, ['column:Sales/OrderId']);
// A docs section and a change-plan slot: Semanticus checks for the words where they go (has_words on the slot), not
// the object's Description, which is a different field.
const docs = S.docsTask('measure:Sales/Total Sales', 'Total Sales', 'Business context', 'Use set_doc_section.', 'businessContext');
assert.equal(docs.doneWhen, 'has_words');
assert.equal(docs.slot, 'doc:businessContext|measure:Sales/Total Sales');
assert.equal(docs.sourcePage, 'docs');
assert.deepEqual(docs.objects, ['measure:Sales/Total Sales']);
const modelDocs = S.docsTask('model', 'Contoso', 'Overview', 'x', 'overview');
assert.deepEqual(modelDocs.objects, [], 'the model itself is not an object ref');
assert.equal(modelDocs.slot, 'doc:overview|model', 'the slot names the model, so the engine can still check it');
const slot = S.slotTask({ id: 'ai:desc:1', objectRef: 'measure:Sales/Total Sales', objectName: 'Total Sales', target: 'description' }, 'Author the value.');
assert.equal(slot.doneWhen, 'has_words');
assert.equal(slot.slot, 'change:ai:desc:1');
assert.equal(slot.sourcePage, 'optimize');
const rule = S.ruleTask('bpa', 'Every measure has a format string', 'Help me write a rule.');
assert.equal(rule.sourcePage, 'bpa');
assert.equal(rule.words, 'Write a custom rule: Every measure has a format string');
assert.equal(S.ruleTask('air', 'x', 'y').sourcePage, 'readiness');

// The words are the Plan row's title: one line, never over the engine's 280.
const long = S.findingTask('air', { ruleId: 'R', ruleTitle: 'x'.repeat(400), objectRef: 'measure:A/B', objectName: 'B' }, 'b');
assert.ok(long.words.length <= 280, 'words are clamped to the engine limit');
assert.ok(!/\n/.test(S.explainTask('M', 'a\nb', 'x').words), 'words are one line');

// Copy copies exactly the brief the task carries; once given, it names the task so the assistant takes the
// waiting one instead of starting a second.
assert.equal(S.copyTextFor(BRIEF, null), BRIEF);
const after = S.copyTextFor(BRIEF, 'pt-0a1b2c3d');
assert.ok(after.startsWith(BRIEF));
assert.match(after, /pt-0a1b2c3d/);
assert.match(after, /claim_request/);

// ===================================================================================================
// 3. Giving: one addPlanTask with the engine's prompt as the brief, then On the plan opens the Plan.
// ===================================================================================================
calls.length = 0;
const sent = await S.giveToAssistant('readiness|DESC-MEASURE|measure:Sales/Total Sales', async () => air);
assert.equal(calls.length, 1, 'one call, one task');
assert.equal(calls[0].method, 'addPlanTask');
assert.deepEqual(calls[0].params, [{ ...air, sendKey: 'readiness|DESC-MEASURE|measure:Sales/Total Sales' }],
  'the request goes as one positional object, as the host passes it, carrying the control\'s key so a second press '
  + 'is the same task (Sol 6 slice 9 review)');
assert.equal(sent.taskId, 'pt-0a1b2c3d');
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), 'pt-0a1b2c3d', 'the page remembers it was given');
S.openPlan();
assert.deepEqual(navigations, ['plan']);

// ---- a reconnect re-reads what is on the plan from the engine (Sol 6 slice 9 review) ------------------------
// The key the engine keeps is the control's key, clamped to the engine's 400 characters in a stable way, so a
// long key (the explain panel's carries its filters) still names the same task after a reconnect.
assert.equal(S.wireSendKey('bpa|R|column:Sales/Amount'), 'bpa|R|column:Sales/Amount');
const longKey = 'explain|Total Sales|' + 'x'.repeat(900);
assert.ok(S.wireSendKey(longKey).length <= 400, 'a long key is clamped to the engine limit');
assert.equal(S.wireSendKey(longKey), S.wireSendKey(longKey), 'and the same key clamps the same way every time');
assert.notEqual(S.wireSendKey(longKey), S.wireSendKey(longKey + 'y'), 'two keys that differ past the limit stay apart');

const row = (id, sendKey, stateWord) => ({ id, stateWord, originLink: { kind: 'finding', ref: 'x', sendKey } });
// Only an open task holds its key: done and set aside are closed, a derived row carries none.
assert.deepEqual([...S.givenFromPlan({ items: [
  row('pt-00000001', 'readiness|A|measure:Sales/Total Sales', 'ready'),
  row('pt-00000002', 'bpa|B|column:Sales/Amount', 'done'),
  row('pt-00000003', 'docs|measure:Sales/Total Sales|business', 'set_aside'),
  row('pt-00000004', 'stats|C|column:Sales/OrderId', 'on_it'),
  { id: 'describe/model', stateWord: 'ready' },
] }).entries()], [
  ['readiness|A|measure:Sales/Total Sales', 'pt-00000001'],
  ['stats|C|column:Sales/OrderId', 'pt-00000004'],
]);
assert.equal(S.givenFromPlan(null).size, 0, 'no plan, nothing given');

// Same model after a reconnect: the task is still open on the engine's plan, so the control still says On the plan.
enginePlan = { items: [row('pt-0a1b2c3d', 'readiness|DESC-MEASURE|measure:Sales/Total Sales', 'ready')] };
calls.length = 0;
await reconnect();
assert.deepEqual(calls.map((c) => c.method), ['getModelPlan'], 'a reconnect re-reads the plan, once');
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), 'pt-0a1b2c3d',
  'the same model after a reconnect: still On the plan, so Give is not offered again');
// Another model, or the task finished in between: the engine has no open task for the key, so Give comes back.
enginePlan = { items: [] };
await reconnect();
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), null, 'nothing open for the key: Give is offered');
// A plan that cannot be read forgets rather than guessing: the engine answers a second press with the same task.
const failing = STUBS['./bridge'].rpc;
S.giveToAssistant('bpa|R|column:Sales/Amount', () => bpa);
await new Promise((r) => setTimeout(r, 0));
STUBS['./bridge'].rpc = async () => { throw new Error('no model'); };
await reconnect();
STUBS['./bridge'].rpc = failing;
assert.equal(S.sentTaskFor('bpa|R|column:Sales/Amount'), null);
// Back to the model where the finding's task is open, for the rendered scenes below.
enginePlan = { items: [row('pt-0a1b2c3d', 'readiness|DESC-MEASURE|measure:Sales/Total Sales', 'ready')] };
await reconnect();

// ---- a plan change re-reads it too (Codex F-282) ----------------------------------------------------------
// The assistant finishes the task, or declines it, or the person sets it aside, while the page stays open. The
// engine says plan/didChange; the control re-reads the plan, so the closed task's control offers Give again and
// Copy no longer tells an assistant to claim a task that is closed.
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), 'pt-0a1b2c3d');
enginePlan = { items: [row('pt-0a1b2c3d', 'readiness|DESC-MEASURE|measure:Sales/Total Sales', 'done')] };
calls.length = 0;
await planChanged();
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), null,
  'the task closed and the engine said plan/didChange: Give is offered again');
assert.deepEqual(calls.map((c) => c.method), ['getModelPlan'], 'a plan change re-reads the plan, once');
// Two plan changes in one breath (one landing can publish twice) are one read.
calls.length = 0;
for (const fn of planChanges) { fn({ revision: 10 }); fn({ revision: 11 }); }
await new Promise((r) => setTimeout(r, 120));
assert.deepEqual(calls.map((c) => c.method), ['getModelPlan'], 'two changes at once are one read');
// A control mounting reads the plan even when the page already watches the engine: a page opened after the task
// closed, with no change to hear since, still shows what the engine holds (the effect's own entry point).
enginePlan = { items: [row('pt-0a1b2c3d', 'readiness|DESC-MEASURE|measure:Sales/Total Sales', 'ready')] };
calls.length = 0;
S.watchGiven();
S.watchGiven();
await new Promise((r) => setTimeout(r, 120));
assert.deepEqual(calls.map((c) => c.method), ['getModelPlan'], 'mounting reads the plan, and a page of controls mounting at once reads it once');
assert.equal(S.sentTaskFor('readiness|DESC-MEASURE|measure:Sales/Total Sales'), 'pt-0a1b2c3d', 'and shows On the plan for the open task');
assert.match(srcOf('sendtask.tsx'), /useEffect\(\(\) => \{\s*watchGiven\(\);/, 'the control mounts through watchGiven');

// ===================================================================================================
// 4. The component, rendered. Idle: Give and Copy. Given: On the plan, Copy, and the next-call line.
// ===================================================================================================
const idle = readable(renderToStaticMarkup(React.createElement(S.GiveToAssistant, { sendKey: 'k-idle', build: () => air })));
assert.match(idle, /Give to your assistant/);
assert.match(idle, /Copy/);
assert.ok(!/Ask AI/.test(idle));
const given = readable(renderToStaticMarkup(React.createElement(S.GiveToAssistant, { sendKey: 'readiness|DESC-MEASURE|measure:Sales/Total Sales', build: () => air })));
assert.match(given, /On the plan/);
assert.match(given, /Copy/);
assert.match(given, /next call/);
assert.ok(!/Give to your assistant/.test(given), 'a given task is not offered twice');
// The rule author's one-line box comes first; Give waits for the person's words.
const ruleBox = renderToStaticMarkup(React.createElement(S.RuleCheckBox, { kind: 'bpa', prompt: (t) => 'Help: ' + t }));
assert.match(readable(ruleBox), /What should the rule check\?/);
assert.match(ruleBox, /disabled=""[^>]*>Give to your assistant|Give to your assistant/);

// ===================================================================================================
// 5. The wiring: every kept control mounts the shared component; the DAX field keeps Copy only.
// ===================================================================================================
const wired = {
  'bpa.tsx': [/<GiveToAssistant/, /findingBuilder\('bpa'/],
  'documentation.tsx': [/<GiveToAssistant/, /docsTask\(/],
  'explainpanel.tsx': [/<GiveToAssistant/, /explainTask\(/],
  'optimize.tsx': [/<GiveToAssistant/, /slotTask\(/],
  'rulesauthor.tsx': [/<RuleCheckBox/],
  // Minimal pass (8 Oct): each Modelling sub-page has ONE Give to your assistant in its header; the per-field Ask AI link is gone.
  'advmodels.tsx': [/<GiveToAssistant/],
  // And the rest of a page's findings go to the assistant in one press, with the rows' own keys, so each row reads "On the plan".
  'findings.tsx': [/giveToAssistant\(keyOf\(i\), findingBuilder\(system, i\)\)/],
  // App.tsx (lane U1 owns it): the AI readiness findings and the storage ideas.
  'App.tsx': [/findingBuilder\('air'/, /storageTask\(/, /<GiveToAssistant/],
};
for (const [file, patterns] of Object.entries(wired)) {
  const src = srcOf(file);
  for (const pattern of patterns) assert.match(src, pattern, `${file} mounts the shared Give to your assistant control`);
  assert.ok(!/'Ask AI'|>Ask AI<|Ask your assistant for help|Ask your assistant to explain/.test(src), `${file} no longer says Ask AI`);
}
const dax = srcOf('daxeditor.tsx');
assert.ok(!/GiveToAssistant|addPlanTask|copyText|Ask AI/.test(dax), 'the DAX field has no assistant link of its own: its text is not saved, and the page it sits on gives one task for the whole page');
// Nothing but sendtask.tsx calls addPlanTask from a page, so there is one way a task is given.
for (const file of ['bpa.tsx', 'documentation.tsx', 'explainpanel.tsx', 'optimize.tsx', 'rulesauthor.tsx', 'advmodels.tsx', 'findings.tsx']) {
  assert.ok(!/rpc(<[^>]*>)?\('addPlanTask'/.test(srcOf(file)), `${file} gives through sendtask.tsx, not a call of its own`);
}

console.log('send-to-assistant: ok');
