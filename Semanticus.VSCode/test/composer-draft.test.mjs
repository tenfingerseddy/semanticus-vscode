// The composer's quick path on the Plan (lane U5, was composer L6), and the ask box's Ctrl J.
//
// What is executed here, one line each, against the engine's own frozen scenes (docs/redesign/contracts/draft-view.json,
// produced by Semanticus.Tests/DraftStateTests.cs, so the page is tested against what the engine really sends):
//   the footer's one primary button in each state: Run with your assistant, Approve and run, or none while it waits
//   a held draft's Approve and run is approveDraft (the person's own), every other one runDraft
//   a waiting part offers exactly Write it yourself, Copy the request and Drop; a part that needs you offers the
//     assistant's choices, Ask again, Write it yourself and Drop; a written part offers nothing
//   a step row shows Keep and Drop, or Write it yourself and Drop while its part is out, or the engine's status after
//   the banner's buttons: a failed after-check gets Ask your assistant why, Keep it anyway and Undo
//   the menu (lane L5a): matches first, the rest behind Show all, Add more words on a words draft only
//   a "You choose" question with a model list draws a picker, never a text box
//   the rendered draft carries the engine's own sentences, both tags, and no product name and no em dash
//   Ctrl J: the shell opens the ask box from any page; a question routes, a task drafts
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');
const webview = join(root, 'webview');
const src = join(webview, 'src');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const calls = [];
const STUBS = {
  './bridge': {
    rpc: async (...args) => { calls.push(args); return null; }, copyText: async () => true,
    onDraftChange: () => () => {}, onPlanChange: () => () => {}, onDidChange: () => () => {}, runHostCommand: () => {},
  },
  './pro': { useTier: () => 'pro', PENDING_ACCESS: 'Checking your Pro access', upgradeToPro: () => {} },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true, allowJs: true },
    // An .mjs module (the guided run's helpers, lane l9-finish) transpiles as JavaScript, like author-gaps.test.mjs does.
    fileName: full.endsWith('.mjs') ? full.replace(/\.mjs$/, '.js') : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => {
    if (STUBS[spec]) return STUBS[spec];
    if (spec.startsWith('.')) return spec.endsWith('.css') ? {} : load(resolve(dirname(full), spec));
    return req(spec);
  };
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  cache.set(full, module.exports);
  return module.exports;
}

const read = (p) => readFileSync(p, 'utf8');
// The frozen draft scenes are a private contract file, left out of the public source mirror. The private repo (marked
// by its mirror manifest) always reads them, so a missing file still fails there; the mirror skips the scene checks.
const privateRepo = existsSync(join(repo, 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
const scenes = privateRepo ? JSON.parse(read(join(repo, 'docs', 'redesign', 'contracts', 'draft-view.json'))).scenes : null; // mirror-optional: private contract file
const C = load(join(src, 'composer-model.ts'));
let failures = 0;
function check(name, fn) {
  try { fn(); console.log(`ok - ${name}`); } catch (error) { failures++; console.error(`not ok - ${name}\n${error.stack}`); }
}
const blank = (v, name) => v.blanks.find((b) => b.name === name);
const sceneCheck = scenes ? check : (name) => console.log(`skip - ${name} (the frozen draft scenes are not in the public source)`);

sceneCheck('every frozen scene the page draws is one the engine produced', () => {
  for (const k of ['draft', 'started_waiting_for_your_assistant', 'needs_you_assistant_asks', 'ready', 'done_one_check_failed_after',
    'held_for_the_person', 'stopped', 'from_words_menu', 'from_words_step_picked']) assert.ok(scenes[k], k);
});

sceneCheck('the footer has one primary button, and none while the parts wait', () => {
  assert.equal(C.primaryOf(scenes.draft), 'run_with_assistant');
  assert.equal(C.primaryOf(scenes.started_waiting_for_your_assistant), 'none');
  assert.equal(C.primaryOf(scenes.ready), 'approve_and_run');
  assert.equal(C.primaryOf(scenes.held_for_the_person), 'approve_and_run');
  assert.equal(C.primaryOf(scenes.done_one_check_failed_after), 'none');
  assert.equal(C.primaryOf(scenes.from_words_menu), 'none', 'nothing to run before a step is picked');
});

sceneCheck('a held draft is approved by the person on the human-only target', () => {
  assert.deepEqual(C.approveCall(scenes.held_for_the_person), ['approveDraft', scenes.held_for_the_person.draftId]);
  assert.deepEqual(C.approveCall(scenes.ready), ['runDraft', scenes.ready.draftId]);
});

sceneCheck('a part waiting for words offers exactly the three actions, with or without an assistant attached', () => {
  const w = scenes.started_waiting_for_your_assistant;
  assert.deepEqual(C.partActions(blank(w, 'formula'), w), ['write', 'copy', 'drop']);
  assert.deepEqual(C.partActions(blank(scenes.draft, 'formula'), scenes.draft), ['write', 'copy', 'drop']);
  assert.ok(blank(w, 'formula').request.startsWith('Semanticus has 1 part for you to write in'), 'the engine wrote the request');
});

sceneCheck('a part that needs you offers the choices, Ask again, Write it yourself and Drop; a written part offers none', () => {
  const n = scenes.needs_you_assistant_asks;
  assert.deepEqual(C.partActions(blank(n, 'description'), n), ['pick', 'ask_again', 'write', 'drop']);
  assert.deepEqual(C.partActions(blank(scenes.ready, 'formula'), scenes.ready), []);
});

sceneCheck('a step row: Keep and Drop, Write it yourself and Drop while its part is out, the status after a run', () => {
  const w = scenes.started_waiting_for_your_assistant;
  assert.equal(C.stepControl(w.steps.find((s) => s.id === 'import'), w), 'keepdrop');
  assert.equal(C.stepControl(w.steps.find((s) => s.id === 'return-rate'), w), 'writedrop');
  const done = scenes.done_one_check_failed_after;
  assert.equal(C.stepControl(done.steps[0], done), 'status');
});

sceneCheck('the banner: a failed after-check asks why, keeps it anyway, or undoes; a stop undoes step 1', () => {
  assert.deepEqual(C.bannerActions(scenes.done_one_check_failed_after), ['try_again', 'ask_why', 'keep_anyway', 'undo']);   // this scene's after step could not run too
  assert.deepEqual(C.bannerActions(scenes.stopped), ['undo_step', 'edit']);
  assert.equal(C.stateLabel(scenes.done_one_check_failed_after), '1 check failed');
  assert.deepEqual(C.doneActions(scenes.done_one_check_failed_after), [], 'the banner carries the buttons');
});

sceneCheck('a failed after-check banner still offers Try again when another step after the change could not run (UAT-J9-S10c-001)', () => {
  const v = { ...scenes.done_one_check_failed_after, canTryAgain: true };
  assert.deepEqual(C.bannerActions(v), ['try_again', 'ask_why', 'keep_anyway', 'undo']);
  assert.deepEqual(C.bannerActions({ ...v, canTryAgain: false }), ['ask_why', 'keep_anyway', 'undo'], 'nothing to run again, nothing offered');
  assert.deepEqual(C.bannerActions({ ...scenes.stopped, canTryAgain: true }), ['undo_step', 'edit'], 'only a failed check carries it');
});

sceneCheck('Try again shows on a done draft with a step after the change that did not run', () => {
  const v = { ...scenes.ready, state: 'done', banner: undefined, canTryAgain: true };
  assert.deepEqual(C.doneActions(v), ['try_again', 'undo', 'reusable']);
  assert.deepEqual(C.doneActions({ ...v, canTryAgain: false }), ['undo', 'reusable']);
});

sceneCheck('the menu: matches first, the rest behind Show all; Add more words only on a words draft', () => {
  const m = scenes.from_words_menu;
  const { rows, more } = C.menuRows(m, false);
  assert.ok(rows.length > 0 && rows.every((r) => r.matched));
  assert.equal(rows.length + more, m.menu.length);
  assert.equal(C.menuRows(m, true).rows.length, m.menu.length);
  assert.equal(m.menuLine, 'Pick the steps. The ones your words name come first.');
  assert.ok(C.canRefine(m));
  assert.ok(!C.canRefine(scenes.ready));
});

sceneCheck('a question with a model list draws a picker, and an enum with a few values draws radios', () => {
  const q = scenes.from_words_step_picked.questions[0];
  assert.equal(C.questionInput(q), 'select');
  assert.ok(C.choicesOf(q).some(([ref]) => ref === 'table:Sales'));
  assert.equal(C.questionInput({ type: 'enum', values: ['Units', 'Amount'] }), 'radio');
  assert.equal(C.questionInput({ type: 'text' }), 'text');
});

// UAT S12-2-005: an answer the engine holds that is not one of the choices showed the first choice (Sales) as if it were
// picked. The picker shows what the engine holds.
sceneCheck('a picker shows an answer that is not one of its choices as it is, never the first choice', () => {
  const q = { ...scenes.from_words_step_picked.questions[0], answer: '42' };
  const choices = C.choicesOf(q);
  assert.equal(choices[0][0], '42');
  assert.match(choices[0][1], /42/u);
  assert.match(choices[0][1], /Not in this model/u);
  const picked = { ...q, answer: 'table:Sales' };
  assert.ok(!C.choicesOf(picked).some(([, label]) => /Not in this model/u.test(label)));
});

// RETIRED by fix-askdata-r4 (Kane, 27 Sep: Ctrl J's question routing is out of this release): five checks of the
// routing rule isQuestion ("a question routes, a task drafts", "words that read as a question route", "a bare split
// drafts", "a task that says by or per drafts", "the labelled sets, both ways"). The rule is gone with the routing;
// Ctrl J drafts every line, which ask-box.test.mjs and verify-askdata.test.mjs read off the bar.
check('the ask box: Ctrl J drafts every line, and there is no routing rule left to misread one', () => {
  assert.equal(typeof C.isQuestion, 'undefined', 'the routing rule left with the routing');
});

// ---- the draft's Plan row -----------------------------------------------------------------------------------
const P = load(join(src, 'plan-model.ts'));
check('a draft row offers Open, the primary while it waits on you; nothing else sits on the row', () => {
  const row = (stateWord) => ({ id: 'draft/dr-0a1b2c3d', stateWord, status: 'ready', source: 'words', doer: 'you' });
  const open = (w) => { const a = P.mainAction(row(w), false); return a && [a.id, !!a.primary]; };
  assert.deepEqual(open('needs_you'), ['open', true]);
  assert.deepEqual(open('ready'), ['open', true]);
  assert.deepEqual(open('checking'), ['open', false]);
  assert.equal(P.mainAction(row('on_it'), false), null);
  assert.equal(P.mainAction(row('done'), false), null);
  // Round 1: a draft's dots hold Remove and nothing else (no doer to change, no Set aside).
  assert.deepEqual(P.rowMenuActions(row('ready')).map((a) => a.id), ['remove']);
  assert.equal(P.doerMenu(row('ready')), null);
});

check('Draft the steps is offered on an open task in your words, and not on a sent one, a taken one or a draft', () => {
  const task = { id: 'pt-0a1b2c3d', source: 'words', stateWord: 'ready', status: 'ready', doer: 'assistant' };
  assert.ok(P.offersDraftSteps(task));
  assert.ok(!P.offersDraftSteps({ ...task, source: 'you' }));
  assert.ok(!P.offersDraftSteps({ ...task, stateWord: 'on_it' }));
  assert.ok(!P.offersDraftSteps({ ...task, stateWord: 'done', status: 'done' }));
  assert.ok(!P.offersDraftSteps({ ...task, id: 'draft/dr-0a1b2c3d' }));
});

check('the Plan draws a draft row open as the composer draft, and the task row offers Draft steps in its dots', () => {
  const plan = read(join(src, 'plan.tsx'));
  assert.match(plan, /P\.isDraftItem\(item\) && <ComposerDraft/);
  assert.match(plan, /'composeTask', item\.title/);
  assert.match(plan, /case 'draft': if \(grant === 'granted'\) void draftSteps\(item\)/, 'Draft steps in the dots drafts on Pro');
  assert.equal(P.ACTION_WORD.draft, 'Draft steps');
  const task = { id: 'pt-0a1b2c3d', source: 'words', stateWord: 'ready', status: 'ready', doer: 'assistant' };
  assert.ok(P.rowMenuActions(task, { grant: 'granted' }).some((a) => a.id === 'draft' && !a.why));
  assert.equal(P.rowMenuActions(task, { grant: 'denied' }).find((a) => a.id === 'draft').why, 'Pro', 'on free it is faded with its reason');
});

// ---- the rendered draft ----------------------------------------------------------------------------------
const D = load(join(src, 'composer-draft.tsx'));
const noop = () => {};
const handlers = { onEdit: noop, onRun: noop, onApprove: noop, onUndo: noop, onGo: noop, onAskWhy: noop, onCopy: noop };
const html = (v, extra = {}) => renderToStaticMarkup(React.createElement(D.DraftBody, { view: v, busy: null, ...handlers, ...extra }));

sceneCheck('the draft shows the engine summary, Before it runs with both tags, and the waiting line', () => {
  const w = scenes.started_waiting_for_your_assistant;
  const out = html(w);
  assert.ok(out.includes(w.summary));
  assert.ok(out.includes('Before it runs'));
  assert.ok(out.includes('Your assistant writes'));
  assert.ok(out.includes('Your assistant sees this on its next call. Or copy the request.'));
  assert.ok(out.includes('Nothing has changed yet.'));
  assert.ok(out.includes('Copy the request') && out.includes('Write it yourself') && out.includes('Drop'));
  assert.ok(!out.includes('Run with your assistant'), 'no primary while the parts wait');
});

sceneCheck('the first draft offers Run with your assistant; a ready one Approve and run', () => {
  assert.ok(html(scenes.draft).includes('Run with your assistant'));
  assert.ok(html(scenes.ready).includes('Approve and run'));
});

sceneCheck('a failed after-check shows the banner with its three buttons', () => {
  const out = html(scenes.done_one_check_failed_after);
  assert.ok(out.includes('One check failed after the change'));
  for (const w of ['Ask your assistant why', 'Keep it anyway', 'Undo']) assert.ok(out.includes(w), w);
});

sceneCheck('the menu draws the line, the matches and Add more words', () => {
  const m = scenes.from_words_menu;
  const out = html(m);
  assert.ok(out.includes(m.menuLine));
  assert.equal(out.split(m.menuLine).length - 1, 1, 'the menu line is said once');
  assert.ok(out.includes(m.menu[0].title));
  assert.ok(out.includes('Add more words'));
  assert.ok(!out.includes('Jev'), 'the menu names no Jev: Jev is never asked on a draft from words');
});

// Journey m11 F9: the line stayed in yellow on a draft with a step picked, and on the one that said Ready.
sceneCheck('once a step is picked, the menu line is no longer said', () => {
  const picked = scenes.from_words_step_picked;
  assert.ok(picked.steps.length > 0 && picked.menuLine, 'the engine still sends the menu and its line');
  const out = html(picked);
  assert.ok(!out.includes('data-testid="cd-menu-line"'), 'no warning line under a picked step');
  assert.ok(out.includes(picked.menu[0].title), 'the menu is still there to add another step');
  const ready = { ...picked, state: 'ready', planWord: 'ready', summary: 'Ready. It runs as one change.', questions: [] };
  assert.ok(!html(ready).includes(picked.menuLine), 'and not on the Ready draft either');
});

sceneCheck('no product name and no em dash in any scene the draft draws', () => {
  for (const [k, v] of Object.entries(scenes)) {
    const out = html(v);
    assert.ok(!/Claude|Copilot Chat|Cursor|ChatGPT/.test(out), `${k} names an assistant product`);
    assert.ok(!out.includes('\u2014'), `${k} has an em dash`);
  }
  for (const f of ['composer-draft.tsx', 'composer-model.ts']) assert.ok(!read(join(src, f)).includes('\u2014'), `${f} has an em dash`);
});

sceneCheck('Edit in Workflows hands the draft to Author as a route seed, never as a model object', () => {
  assert.equal(C.authorTarget(scenes.ready), `draft:${scenes.ready.draftId}`);
  assert.match(read(join(src, 'App.tsx')), /tool === 'workflows' && target\?\.startsWith\('draft:'\)\) return \{ kind: 'draft'/);
  const R = load(join(src, 'route.ts'));
  assert.equal(R.refToObject(`draft:${scenes.ready.draftId}`), undefined);
});

// UAT S12-5-004 (lost work): a draft wrote over a description the person had changed after the draft was made. The
// engine now stops and asks; the banner offers Keep yours first, then Apply the draft, and shows what is there now.
sceneCheck('a value changed since the draft was made: Keep yours or Apply the draft, and what is there now', () => {
  const v = { ...scenes.ready, state: 'needs_you',
    banner: { kind: 'overwrite', title: 'Changed since the draft', text: 'Sales[Amount] has a new description since this draft was made. Keep yours, or apply the draft?' },
    overwrite: { sentence: 'x', step: 1, objectRef: 'column:Sales/Amount', was: '(none)', now: 'Finance review pending.' } };
  assert.deepEqual(C.bannerActions(v), ['keep_theirs', 'apply_draft']);
  const out = html(v);
  assert.match(out, /data-testid="cd-keep-theirs"[^>]*>Keep yours</);
  assert.match(out, /data-testid="cd-apply-draft"[^>]*>Apply the draft</);
  assert.match(out, /Yours now: Finance review pending\./);
});

// ---- Ctrl J ----------------------------------------------------------------------------------------------
check('Ctrl J opens the ask box from any page, and the host keybinding forwards it', () => {
  const app = read(join(src, 'App.tsx'));
  assert.match(app, /askOpen/);
  assert.match(app, /isAskKey\(e\)/);
  const ask = load(join(src, 'askbox.tsx'));
  assert.ok(ask.isAskKey({ ctrlKey: true, metaKey: false, altKey: false, shiftKey: false, key: 'j' }));
  assert.ok(!ask.isAskKey({ ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, key: 'j' }));
  assert.ok(!ask.isAskKey({ ctrlKey: true, metaKey: false, altKey: true, shiftKey: false, key: 'j' }));
  const pkg = JSON.parse(read(join(root, 'package.json')));
  assert.ok(pkg.contributes.keybindings.some((k) => k.command === 'semanticus.studioAsk' && k.key === 'ctrl+j'
    && k.when.includes("activeWebviewPanelId == 'semanticusStudio'")));
  assert.match(read(join(root, 'src', 'extension.ts')), /semanticus\.studioAsk/);
  // Ctrl J only drafts (Kane, 27 Sep), so its placeholder no longer offers to answer a question about the model.
  assert.equal(ask.ASK_BAR_PLACEHOLDER, 'Describe a task');
  assert.equal(ask.ASK_BAR_HINT, 'Esc closes it');
});

check('the draft channel reaches the page: draft/didChange is forwarded and heard', () => {
  assert.match(read(join(root, 'src', 'extension.ts')), /'draft\/didChange'/);
  assert.match(read(join(src, 'bridge.ts')), /export function onDraftChange/);
});

if (failures) { console.error(`${failures} composer check(s) failed`); process.exit(1); }
console.log('composer-draft: all checks passed');
