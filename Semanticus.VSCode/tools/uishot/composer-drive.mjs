// Browser-driven proof of the composer's quick path on the Plan (lane U5, was composer L6), and of Ctrl J, in three
// themes.
//
// It serves THIS worktree's built bundle, drives the real React Plan page with real clicks and keys, and MEASURES what
// landed into results.json next to its PNGs: the draft's summary, its state word, Before it runs, every step row, the
// footer's buttons, the banner, and every call the page made with its arguments. A scene that looks right while
// saying the wrong thing fails on its `expect`.
//
// The engine is played by the ENGINE'S OWN frozen draft scenes (docs/redesign/contracts/draft-view.json, produced by
// Semanticus.Tests/DraftStateTests.cs), read here and inlined into the page, plus the Plan fixture U1's driver uses.
// Three scenes are derived from the frozen ones where the engine file has none (a clean Done, a Done with a step
// after the change that did not run, a part being written); each derivation changes only the fields named in it.
//
// Scenes follow the approved workflow-composer-v3.html Plan frames: the draft (On the Plan), started in Studio
// (waiting, then writing), no assistant (the three part actions), writes something wrong (Needs you with the
// assistant's question and choices), one undo (Done), after-check fails (the banner), plus stopped, held, the menu
// from words (lane L5a), Try again, Draft the steps, Ctrl J on the Plan and on another page, and free.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/composer-drive.mjs
//   ONLY=draft,waiting node tools/uishot/composer-drive.mjs      just those scenes
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
import { THEMES, applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const repo = resolve(webRoot, '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'composer');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.');
  process.exit(1);
}

const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const FROZEN = JSON.parse(readFileSync(join(repo, 'docs', 'redesign', 'contracts', 'draft-view.json'), 'utf8')).scenes;
const clone = (v) => JSON.parse(JSON.stringify(v));

// ---- the derived scenes, each changing only what it names --------------------------------------------------
const SCENES = clone(FROZEN);
{
  // One undo (Done, every check passed): the failed-check scene with its check passing.
  const d = clone(FROZEN.done_one_check_failed_after);
  delete d.banner;
  d.planWord = 'done'; delete d.planNote;
  d.notes = ['1 check ran after it; it passed.'];
  d.steps = d.steps.map((s) => (s.status === 'Failed after it ran' ? { ...s, status: 'Done' } : s));
  d.after = d.after.map((s) => ({ ...s, status: 'Done' }));
  d.record.after = d.record.after.map((a) => ({ ...a, status: a.what === 'check' ? 'passed' : 'ran', text: a.what === 'check' ? 'Return rate is 4.1%, inside 0% to 100%.' : 'Done.' }));
  d.canTryAgain = false;
  SCENES.done_clean = d;
  // Try again: the same Done, with the load after the change not run (offline), as the engine words it.
  const t = clone(d);
  t.planWord = 'checking'; t.planNote = 'Step 8: Could not run: not connected.';
  t.notes = ['1 check ran after it; it passed.', 'Step 8: Could not run: not connected.'];
  t.after = t.after.map((s) => ({ ...s, status: 'Could not run' }));
  t.canTryAgain = true;
  SCENES.done_try_again = t;
  // Started in Studio, then picked up: both parts being written.
  const w = clone(FROZEN.started_waiting_for_your_assistant);
  w.blanks = w.blanks.map((b) => ({ ...b, state: 'writing', line: 'Your assistant is writing' }));
  w.planWord = 'on_it'; w.planNote = 'Your assistant is writing';
  SCENES.writing = w;
  // From your words, its one question answered (journey m11 F8, F9): Ready, with the menu still offered to add more.
  const r = clone(FROZEN.from_words_step_picked);
  r.state = 'ready'; r.planWord = 'ready'; r.planNote = null; r.summary = 'Ready. It runs as one change.';
  r.questions = []; r.blanks = []; r.sentence = 'Answered: Which table? Sales.';
  r.steps = r.steps.map((x) => ({ ...x, status: 'Ready' }));
  SCENES.from_words_ready = r;
  // m11/sol-fixes-1 round 4, the adding rule (version 2): words that ask to take something away get no step, and the
  // draft names them with the engine's reason (DraftCopy.NoStepFor, AddingRule.TakeAwayReason) in place of an add.
  const n = clone(FROZEN.from_words_step_picked);
  n.notes = [...(n.notes ?? []), 'No step for "make sure the old role is removed". These words ask to take something away, and no step here does that yet.'];
  SCENES.from_words_no_step = n;
  // Round 7, the adding rule version 5: an add held back by the words after it is OFFERED UNTICKED, a dropped step in the
  // draft with the person's own words and one true line on why (DraftCopy.NotTickedFor, AddingRule.UnreadReason), beside the
  // take-away's own note. The step is the kind the words name; nothing about it is ticked.
  const nv = clone(FROZEN.from_words_step_picked);
  nv.title = 'Add a measure for margin and the old role should be removed';
  nv.words = nv.title;
  nv.state = 'needs_you'; nv.planWord = 'needs_you'; nv.planNote = 'Answer: Which table?';
  nv.steps = nv.steps.map((x) => ({ ...x, kept: false, status: 'Dropped' }));
  nv.sentence = 'Drafted from your words. 1 step is not ticked. The notes say why. Tick it to use it.';
  nv.notes = [...(nv.notes ?? []),
    'Not ticked: "add a measure for margin". Words after it were not read as a step and could change it. Tick it if you want it as it stands.',
    'No step for "the old role should be removed". These words ask to take something away, and no step here does that yet.'];
  SCENES.from_words_unticked_add = nv;
  // UAT S12-5-004: the run stopped because the person changed Return rate's description after the draft was made. The
  // engine's words (DraftCopy.Overwrite); nothing ran, so the draft is Needs you with the two choices.
  const o = clone(FROZEN.ready);
  const sentence = 'Returns[Return rate] has a new description since this draft was made. Keep yours, or apply the draft?';
  o.state = 'needs_you'; o.summary = 'Nothing has changed yet.'; o.planWord = 'needs_you'; o.planNote = sentence; o.approved = true;
  o.banner = { kind: 'overwrite', title: 'Changed since the draft', text: sentence };
  o.overwrite = { sentence, step: 7, objectRef: 'measure:Returns/Return rate', was: '(none)', now: 'Finance review pending.' };
  SCENES.overwrite = o;
}

const seed = (start, script, free, draftLast) => `
${FIXTURE_SRC}
window.__calls = [];
const __scenes = ${JSON.stringify(SCENES)};
const __script = ${JSON.stringify(script)};
let __scene = ${JSON.stringify(start)};
const __doerOf = (w) => w === 'needs_you' ? 'you' : w === 'on_it' || w === 'ready' && __scenes[__scene]?.state === 'waiting_for_assistant' ? 'assistant' : 'semanticus';
const __draftRow = () => {
  const v = __scenes[__scene];
  if (!v) return null;
  return { id: 'draft/' + v.draftId, section: 'Structure', title: v.title, sub: v.summary, value: '', who: 'you', status: 'ready',
    confidence: 0, why: ['You drafted this on 24 September 2026. Semanticus wrote every line.'], diff: [], touches: v.steps.length + ' steps',
    tables: [], jump: null, skipReason: null, skippedUtc: null, state: 'gap', changeSet: null, source: v.source === 'From your words' ? 'words' : 'words',
    sourcePage: null, doer: __doerOf(v.planWord), stateWord: v.planWord, stateNote: v.planNote ?? null, doneWhen: 'model_check',
    doneText: 'Semanticus runs it as one change and checks it after.', objects: [], reads: [], writes: [], footprintKnown: false,
    waitsFor: [], held: false, order: null, note: null, reply: null, claim: null, originLink: { kind: 'draft', ref: v.draftId }, events: [] };
};
let __base = makeHeartPlan({ mode: 'plain', free: ${JSON.stringify(!!free)} });
// draftLast puts the draft where the engine does, after every other row, so a draft that moves between groups moves.
const __plan = () => { const r = __draftRow(); return { ...__base, items: r ? (${JSON.stringify(!!draftLast)} ? [...__base.items, r] : [r, ...__base.items]) : __base.items }; };
const __note = (name, params) => window.__calls.push({ call: name, params: params });
const __step = (name, params) => {
  __note(name, params);
  const key = name + (name === 'editDraft' ? ':' + params[1] : '');
  if (__script[key]) __scene = __script[key];
  return __scenes[__scene];
};
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => __plan());
    v.setResponse('getDraft', (params) => { __note('getDraft', params); return __scenes[__scene]; });
    for (const name of ['editDraft', 'runDraft', 'approveDraft']) v.setResponse(name, (params) => __step(name, params));
    v.setResponse('composeTask', (params) => __step('composeTask', params));
    v.setResponse('undo', (params) => { __note('undo', params); return { undoSteps: 0 }; });
    v.setResponse('skipModelPlanItem', (params) => { __note('skipModelPlanItem', params); __base = heartSkip(__base, params[0], params[1]); return __plan(); });
    v.setResponse('addPlanTask', (params) => { __note('addPlanTask', params); const r = heartAdd(__base, params[0] || {}); __base = r.plan; return { ...r, plan: __plan() }; });
    v.setResponse('routeAsk', (params) => { __note('routeAsk', params); return { tool: 'diagram', confidence: 0.9, confidenceBar: 0.6 }; });
    for (const name of ['getPlanHistory', 'listVerifiedEdits', 'listHistoryCheckpoints']) v.setResponse(name, () => ({ entries: [], rows: [], checkpoints: [] }));
  },
});
(() => {})()`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// What a scene reads off the page. Every field is a measurement of the DOM, not of the scene data.
const MEASURE = `(() => {
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : '');
  const q = (s) => document.querySelector(s);
  const draft = q('[data-testid^="cd-draft-"]');
  const btns = (root) => root ? [...root.querySelectorAll('button')].map((b) => b.textContent.trim()).filter(Boolean) : [];
  return {
    onPlan: !!q('[data-testid="plan-page"]'),
    draftOpen: !!draft,
    state: draft?.dataset.state ?? null,
    summary: text(q('[data-testid="cd-summary"]')),
    draftNotes: [...document.querySelectorAll('.cd-notes .cd-p')].map((p) => text(p)),
    stateText: text(q('[data-testid="cd-state"]')),
    before: text(q('[data-testid="cd-before"]')),
    parts: [...document.querySelectorAll('[data-testid^="cd-part-"]')].filter((p) => p.classList.contains('cd-part')).map((p) => ({
      state: p.dataset.state, text: text(p), buttons: btns(p) })),
    steps: [...document.querySelectorAll('[data-testid^="cd-step-"]')].map((s) => ({ id: s.dataset.testid.slice(8), status: s.dataset.status, text: text(s), buttons: btns(s) })),
    banner: text(q('[data-testid="cd-banner"]')),
    bannerButtons: btns(q('[data-testid="cd-banner"]')),
    foot: text(q('[data-testid="cd-foot"]')),
    footButtons: btns(q('[data-testid="cd-foot"]')),
    menu: [...document.querySelectorAll('[data-testid^="cd-menu-"]')].filter((m) => m.classList.contains('cd-menu-row')).map((m) => text(m)),
    menuLine: text(q('[data-testid="cd-menu-line"]')),
    refine: !!q('[data-testid="cd-refine"]'),
    askBar: !!q('[data-testid="ask-bar"]'),
    askBarText: text(q('[data-testid="ask-bar"]')),
    askPlaceholder: (q('[data-testid="ask-bar-input"]') || {}).placeholder ?? null,
    askValue: (q('[data-testid="ask-bar-input"]') || {}).value ?? null,
    askFocused: !!q('[data-testid="ask-bar-input"]') && document.activeElement === q('[data-testid="ask-bar-input"]'),
    planField: (q('[data-testid="plan-field"]') || {}).value ?? null,
    // Calls parked by a hold step: sent by the page, not answered yet.
    heldAsk: window.__uishotHarness ? window.__uishotHarness.heldCount('routeAsk') : null,
    heldCompose: window.__uishotHarness ? window.__uishotHarness.heldCount('composeTask') : null,
    detail: text(q('[data-testid^="plan-detail-"]')),
    said: text(q('[data-testid="cd-said"]')),
    error: text(q('[data-testid="cd-error"]')),
    rowActions: [...document.querySelectorAll('[data-row^="draft/"] .pw-acts button:not(.pw-more)')].map((b) => b.textContent.trim()),
    // Where the draft's row is drawn: in Needs you or in Next, and its place in that group.
    draftGroup: (() => { const r = q('[data-row^="draft/"]'); return !r ? null : r.closest('[data-testid="plan-only-you"]') ? 'needs' : 'next'; })(),
    rowMenu: [...document.querySelectorAll('[data-testid="plan-row-menu"] [role="menuitem"]')].map((b) => b.textContent.trim()),
    draftTop: (() => { const r = q('[data-row^="draft/"]'); return r ? Math.round(r.getBoundingClientRect().top + window.scrollY + (q('main')?.scrollTop ?? 0)) : null; })(),
    menuLineShown: !!q('[data-testid="cd-menu-line"]'),
    calls: window.__calls || [],
    docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
  };
})()`;

const results = [];
const click = (page, sel) => page.evaluate((s) => {
  const el = document.querySelector(s);
  if (!el) return false;
  el.scrollIntoView({ block: 'center' });
  el.click();
  return true;
}, sel);

async function scene(name, { start, script = {}, free = false, tab = 'plan', open = true, steps = [], expect = null, draftLast = false } = {}) {
  if (only && !only.includes(name)) return;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: Number(process.env.UISHOT_VW || 1440), height: Number(process.env.UISHOT_VH || 1000), deviceScaleFactor: 1 });
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(start, script, free, draftLast));
  const query = `conn=1&live=1${free ? '' : '&tier=pro'}&tab=${tab}`;
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector(tab === 'plan' ? '[data-testid="plan-page"]' : 'main', { timeout: 20000 });
  await wait(1200);
  const notes = [];
  const marks = {};
  if (open && start && SCENES[start]) {
    // Round 1: a draft that needs you is a row of yours, in the closed fold at the end of the list.
    await click(page, '[data-testid="plan-only-you-toggle"]');
    await wait(300);
    if (!(await click(page, `[data-testid="plan-row-open-draft/${SCENES[start].draftId}"]`))) notes.push('the draft row did not open');
    await wait(700);
  }
  for (const step of steps) {
    let ok = true;
    if (step.click) ok = await click(page, step.click);
    else if (step.key) await page.keyboard.down('Control'), await page.keyboard.press(step.key), await page.keyboard.up('Control');
    else if (step.type) { ok = !!(await page.$(step.type)); if (ok) { await page.focus(step.type); await page.keyboard.type(step.value, { delay: 6 }); } }
    else if (step.enter) { ok = !!(await page.$(step.enter)); if (ok) { await page.focus(step.enter); await page.keyboard.press('Enter'); } }
    else if (step.hold) await page.evaluate((m) => window.__uishotHarness.hold(m), step.hold);
    else if (step.select) ok = await page.evaluate((s, v) => { const el = document.querySelector(s); if (!el) return false; el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); return true; }, step.select, step.value);
    // The engine's plan/didChange, which follows every draft edit in VS Code.
    else if (step.planChanged) await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'planDidChange', payload: null }));
    // A reading taken mid-scene, kept for the expect as m.marks[name].
    else if (step.mark) marks[step.mark] = await page.evaluate(MEASURE);
    if (!ok) notes.push(`step not found: ${JSON.stringify(step)}`);
    await wait(step.settle ?? 700);
  }
  await checkScreen(page, `composer-drive:${name}`);
  await page.setViewport({ width: Number(process.env.UISHOT_VW || 1440), height: Number(process.env.UISHOT_VH || 1000), deviceScaleFactor: 1 });
  await wait(300);
  // The draft is the subject: bring its row to the top of the view.
  await page.evaluate(() => { const r = document.querySelector('.pw-item-open') || document.querySelector('[data-testid="ask-bar"]'); r?.scrollIntoView({ block: 'start' }); });
  await wait(300);
  const measured = { ...(await page.evaluate(MEASURE)), marks };
  const shots = {};
  for (const theme of Object.keys(THEMES)) {
    await applyTheme(page, theme); await wait(300);
    const file = join(OUT, `${name}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false });
    shots[theme] = file;
  }
  if (expect) for (const failure of expect(measured)) notes.push(failure);
  if (measured.docWidth > measured.viewportWidth + 1) notes.push(`the page scrolls sideways: ${measured.docWidth} > ${measured.viewportWidth}`);
  const bad = JSON.stringify(measured).includes('\u2014') ? ['an em dash is on screen'] : [];
  if (/Claude|ChatGPT|Cursor/.test(JSON.stringify([measured.foot, measured.before, measured.banner, measured.askBarText]))) bad.push('an assistant product is named');
  notes.push(...bad);
  results.push({ scene: name, start, measured, shots, notes, errors });
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${name}${notes.length ? ': ' + notes.join(' | ') : ''}${errors.length ? ' ERR ' + errors.slice(0, 2).join(' | ') : ''}`);
  await page.close();
}

const has = (cond, message) => (cond ? [] : [message]);
const callsOf = (m, name) => m.calls.filter((c) => c.call === name);
const V = (k) => SCENES[k];

// ---- On the Plan (frame "On the Plan"): the draft, parts not sent yet -----------------------------------------
await scene('draft', {
  start: 'draft', script: { runDraft: 'started_waiting_for_your_assistant' },
  expect: (m) => [
    ...has(m.draftOpen, 'the draft did not draw under its row'),
    ...has(m.summary === V('draft').summary, `summary "${m.summary}"`),
    ...has(/Your assistant writes/.test(m.before) && /2 parts/.test(m.before), `before "${m.before}"`),
    ...has(m.footButtons.includes('Run with your assistant') && m.footButtons.includes('Edit in Workflows'), `foot ${m.footButtons}`),
    ...has(m.steps.length === 8, `${m.steps.length} step rows, expected 7 and the load after the change`),
    ...has(JSON.stringify(m.rowActions) === '[]', `an open draft row still shows ${m.rowActions}`),
  ],
});
// Started in Studio: Run with your assistant records the approval and queues the parts; nothing opens.
await scene('run-with-assistant', {
  start: 'draft', script: { runDraft: 'started_waiting_for_your_assistant' }, steps: [{ click: '[data-testid="cd-run-with"]', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'runDraft').map((c) => c.params)) === JSON.stringify([[V('draft').draftId]]), `run calls ${JSON.stringify(callsOf(m, 'runDraft'))}`),
    ...has(m.state === 'waiting_for_assistant', `state ${m.state}`),
    ...has(/Your assistant sees this on its next call\. Or copy the request\./.test(m.foot), `foot "${m.foot}"`),
    ...has(/Nothing has changed yet\./.test(m.foot), 'the footer says nothing has changed yet'),
    ...has(!m.footButtons.includes('Run with your assistant'), 'the button goes away'),
  ],
});
// No assistant: every waiting part offers Write it yourself, Copy the request, Drop.
await scene('waiting', {
  start: 'started_waiting_for_your_assistant',
  expect: (m) => [
    ...has(m.parts.length === 2 && m.parts.every((p) => JSON.stringify(p.buttons) === '["Write it yourself","Copy the request","Drop"]'), `parts ${JSON.stringify(m.parts.map((p) => p.buttons))}`),
    ...has(m.steps.find((s) => s.id === 'return-rate')?.buttons.join(',') === 'Write it yourself,Drop', 'the part step shows Write it yourself and Drop'),
    ...has(m.stateText === 'Waiting for your assistant', `state "${m.stateText}"`),
  ],
});
await scene('write-it-yourself', {
  start: 'started_waiting_for_your_assistant', script: { 'editDraft:write': 'started_waiting_for_your_assistant' },
  steps: [{ click: '[data-testid="cd-part-describe-description"] [data-testid="cd-write"]' },
    { type: '[data-testid="cd-writer"] textarea', value: 'Share of units sold that came back as returns. Higher is worse.' },
    { click: '[data-testid="cd-writer"] button[type="submit"]', settle: 900 }],
  expect: (m) => {
    const c = callsOf(m, 'editDraft');
    return has(c.length === 1 && JSON.stringify(c[0].params.slice(1)) === JSON.stringify(['write', 'describe', 'description', null, 'Share of units sold that came back as returns. Higher is worse.']), `write call ${JSON.stringify(c)}`);
  },
});
await scene('writing', {
  start: 'writing',
  expect: (m) => has(m.parts.every((p) => /Your assistant is writing/.test(p.text)), `parts ${JSON.stringify(m.parts.map((p) => p.text))}`),
});
// Writes something wrong: the assistant asks the person, with its choices.
await scene('assistant-asks', {
  start: 'needs_you_assistant_asks', script: { 'editDraft:pick': 'started_waiting_for_your_assistant' },
  steps: [{ click: '[data-testid="cd-pick"]', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params.slice(1, 3))) === JSON.stringify([['pick', 'describe']]), `pick ${JSON.stringify(callsOf(m, 'editDraft'))}`),
  ],
});
await scene('assistant-asks-open', {
  start: 'needs_you_assistant_asks',
  expect: (m) => {
    const p = m.parts.find((x) => x.state === 'needs');
    return [
      ...has(!!p && /Your assistant asks: Should it say what counts as a return\?/.test(p.text), `needs part "${p?.text}"`),
      ...has(!!p && JSON.stringify(p.buttons) === '["Return rate","Ask again","Write it yourself","Drop"]', `needs buttons ${p?.buttons}`),
      ...has(m.stateText === 'Needs you', `state "${m.stateText}"`),
    ];
  },
});
// Ready, then Approve and run: one runDraft, and the Done frame (One undo).
await scene('ready', {
  start: 'ready', script: { runDraft: 'done_clean' },
  expect: (m) => has(m.footButtons.includes('Approve and run'), `foot ${m.footButtons}`),
});
await scene('approve-and-run', {
  start: 'ready', script: { runDraft: 'done_clean' }, steps: [{ click: '[data-testid="cd-approve"]', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'runDraft').length === 1, `${callsOf(m, 'runDraft').length} run calls`),
    ...has(m.summary === 'Done. One Undo takes back steps 1 to 7.', `summary "${m.summary}"`),
    ...has(JSON.stringify(m.footButtons) === '["Undo","Make it reusable in Workflows"]', `foot ${m.footButtons}`),
    ...has(m.steps.every((s) => s.status === 'Done'), 'every step Done'),
  ],
});
// After-check fails: the banner, and Keep it anyway.
await scene('check-failed', {
  start: 'done_one_check_failed_after', script: { 'editDraft:keep_anyway': 'done_clean' },
  expect: (m) => [
    ...has(/One check failed after the change/.test(m.banner) && /134%/.test(m.banner), `banner "${m.banner}"`),
    ...has(JSON.stringify(m.bannerButtons) === '["Try again","Ask your assistant why","Keep it anyway","Undo"]', `banner buttons ${m.bannerButtons}`),
    ...has(m.stateText === '1 check failed', `state "${m.stateText}"`),
    ...has(m.steps.find((s) => s.id === 'return-rate')?.status === 'Failed after it ran', 'step 5 failed after it ran'),
  ],
});
await scene('overwrite-asked', {
  start: 'overwrite',
  expect: (m) => has(m.stateText != null && JSON.stringify(m.bannerButtons) === '["Keep yours","Apply the draft"]', `asked ${m.bannerButtons}`),
});
await scene('overwrite', {
  start: 'overwrite', script: { 'editDraft:keep_theirs': 'ready' }, steps: [{ mark: 'asked', settle: 0 }, { click: '[data-testid="cd-keep-theirs"]', settle: 900 }],
  expect: (m) => [
    ...has(/has a new description since this draft was made\. Keep yours, or apply the draft\?/.test(m.marks.asked?.banner ?? ''), `banner "${m.marks.asked?.banner}"`),
    ...has(/Yours now: Finance review pending\./.test(m.marks.asked?.banner ?? ''), 'what is there now'),
    ...has(JSON.stringify(m.marks.asked?.bannerButtons) === '["Keep yours","Apply the draft"]', `banner buttons ${m.marks.asked?.bannerButtons}`),
    ...has(JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params[1])) === '["keep_theirs"]', `keep yours ${JSON.stringify(callsOf(m, 'editDraft'))}`),
  ],
});
await scene('keep-it-anyway', {
  start: 'done_one_check_failed_after', script: { 'editDraft:keep_anyway': 'done_clean' }, steps: [{ click: '[data-testid="cd-keep-anyway"]', settle: 900 }],
  expect: (m) => has(JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params[1])) === '["keep_anyway"]' && !m.banner, `keep anyway ${JSON.stringify(callsOf(m, 'editDraft'))}`),
});
await scene('ask-why', {
  start: 'done_one_check_failed_after', steps: [{ click: '[data-testid="cd-ask-why"]', settle: 900 }],
  expect: (m) => {
    const c = callsOf(m, 'addPlanTask');
    return has(c.length === 1 && /^Why did a check fail after "Import Returns and add return measures"\? Return rate is 134%/.test(c[0].params[0].words), `ask why ${JSON.stringify(c)}`);
  },
});
await scene('undo', {
  start: 'done_one_check_failed_after', steps: [{ click: '[data-testid="cd-banner"] [data-testid="cd-undo"]', settle: 900 }],
  expect: (m) => has(callsOf(m, 'undo').length === 1, `${callsOf(m, 'undo').length} undo calls`),
});
// Try again, on a step after the change that did not run (the Sol F-269 follow-up).
await scene('try-again', {
  start: 'done_try_again', script: { 'editDraft:try_again': 'done_clean' },
  expect: (m) => [
    ...has(JSON.stringify(m.footButtons) === '["Try again","Undo","Make it reusable in Workflows"]', `foot ${m.footButtons}`),
    ...has(m.steps.find((s) => s.id === 'load')?.status === 'Could not run', 'the load could not run'),
  ],
});
await scene('try-again-pressed', {
  start: 'done_try_again', script: { 'editDraft:try_again': 'done_clean' }, steps: [{ click: '[data-testid="cd-try-again"]', settle: 900 }],
  expect: (m) => has(JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params[1])) === '["try_again"]' && m.steps.find((s) => s.id === 'load')?.status === 'Done', `try again ${JSON.stringify(callsOf(m, 'editDraft'))}`),
});
await scene('stopped', {
  start: 'stopped',
  expect: (m) => [
    ...has(/Stopped at step 2 of 2/.test(m.banner) && JSON.stringify(m.bannerButtons) === '["Undo step 1","Edit in Workflows"]', `stopped ${m.banner} ${m.bannerButtons}`),
    ...has(!m.summary, 'the stop is said once, in the banner'),
  ],
});
await scene('held-open', {
  start: 'held_for_the_person',
  expect: (m) => [
    ...has(/Rules about who can see which data wait for you to read them/.test(m.summary), `held summary "${m.summary}"`),
    ...has(m.footButtons.includes('Approve and run'), `held foot ${m.footButtons}`),
  ],
});
await scene('held', {
  start: 'held_for_the_person', script: { approveDraft: 'done_clean' }, steps: [{ click: '[data-testid="cd-approve"]', settle: 900 }],
  expect: (m) => has(JSON.stringify(callsOf(m, 'approveDraft').map((c) => c.params)) === JSON.stringify([[V('held_for_the_person').draftId]]) && callsOf(m, 'runDraft').length === 0,
    `held approve ${JSON.stringify(m.calls.filter((c) => /Draft$/.test(c.call)))}`),
});
// The menu from words (lane L5a), then a step picked and its question answered.
await scene('menu', {
  start: 'from_words_menu', script: { 'editDraft:add': 'from_words_step_picked' },
  expect: (m) => [
    ...has(m.summary === 'Pick the steps. The ones your words name come first.' && !m.menuLine, `the menu line is said once: "${m.summary}" / "${m.menuLine}"`),
    ...has(/^Add a measure/.test(m.menu[0] ?? '') && /^Describe an object/.test(m.menu[1] ?? ''), `menu ${JSON.stringify(m.menu)}`),
    ...has(m.refine, 'Add more words is offered'),
  ],
});
await scene('no-step-for-take-away', {
  start: 'from_words_no_step',
  expect: (m) => [
    ...has(m.draftNotes.includes('No step for "make sure the old role is removed". These words ask to take something away, and no step here does that yet.'),
      `the words with no step are not named on the draft: ${JSON.stringify(m.draftNotes)}`),
    ...has(!m.steps.some((x) => /Add a role/.test(x.text)), `a step adds for words that take away: ${JSON.stringify(m.steps.map((x) => x.text))}`),
  ],
});
await scene('unticked-add', {
  start: 'from_words_unticked_add',
  expect: (m) => [
    ...has(m.draftNotes.some((n) => n.startsWith('Not ticked: "add a measure for margin". Words after it were not read')),
      `the unticked add is not said with the person's words and its reason: ${JSON.stringify(m.draftNotes)}`),
    ...has(m.draftNotes.some((n) => n.startsWith('No step for "the old role should be removed".')), `the take-away is not named: ${JSON.stringify(m.draftNotes)}`),
    ...has(m.steps.length === 1 && /Add a measure/.test(m.steps[0].text) && /Dropped/.test(m.steps[0].text + JSON.stringify(m.steps[0])), `the add is not offered dropped: ${JSON.stringify(m.steps)}`),
  ],
});
await scene('menu-add', {
  start: 'from_words_menu', script: { 'editDraft:add': 'from_words_step_picked', 'editDraft:answer': 'from_words_step_picked' },
  steps: [{ click: '[data-testid="cd-menu-add-measure"] button', settle: 900 }, { select: '[data-testid="cd-q-add-measure-tableRef"] select', value: 'table:Sales', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'editDraft').map((c) => [c.params[1], c.params[4] ?? c.params[5]])) === JSON.stringify([['add', null], ['answer', 'add-measure-tableRef']])
      || JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params.slice(1))) === JSON.stringify([['add', null, null, null, 'add-measure'], ['answer', null, null, 'add-measure-tableRef', 'table:Sales']]), `menu calls ${JSON.stringify(callsOf(m, 'editDraft').map((c) => c.params))}`),
    ...has(m.steps.length === 1 && /Add a measure/.test(m.steps[0].text), `steps ${JSON.stringify(m.steps.map((s) => s.text))}`),
  ],
});
// Journey m11 F8 and F9: answer a words draft's question. The draft is the thing being worked on, so it stays open
// and in the group it was in, and the menu line goes once there are steps (it stays while the menu
// is still the whole draft). The draft is placed last, as the engine places it, so a group change would move it.
await scene('answer-keeps-place', {
  start: 'from_words_step_picked', draftLast: true, script: { 'editDraft:answer': 'from_words_ready' },
  steps: [{ mark: 'before', settle: 0 },
    { select: '[data-testid="cd-q-add-measure-tableRef"] select', value: 'table:Sales', settle: 900 },
    { planChanged: true, settle: 1200 }],
  expect: (m) => [
    ...has(m.marks.before?.draftGroup === 'needs', `before answering the draft was in ${m.marks.before?.draftGroup}`),
    ...has(m.draftOpen, 'the draft folded shut after its question was answered'),
    ...has(m.draftGroup === 'needs', `the draft moved to ${m.draftGroup} after its question was answered`),
    ...has(m.state === 'ready', `state ${m.state}`),
    ...has(!m.menuLineShown, `the stale warning is still on a ready draft: "${m.menuLine}"`),
  ],
});
await scene('menu-line-while-empty', {
  start: 'from_words_menu',
  expect: (m) => has(m.summary === 'Pick the steps. The ones your words name come first.' && !m.menuLineShown, 'the warning is said once, as the summary, while no step is picked'),
});

// Draft the steps on a row typed in your words (Plan-heart contract 14).
await scene('draft-the-steps', {
  start: null, open: false, script: { composeTask: 'from_words_menu' },
  steps: [{ click: '[data-testid="plan-only-you-toggle"]' }, { click: '[data-row="pt-3f9a2c11"] [data-testid="plan-row-more"]' }, { click: '[data-testid="plan-menu-draft"]', settle: 1200 }],
  expect: (m) => [
    ...has(callsOf(m, 'composeTask').length === 1 && callsOf(m, 'composeTask')[0].params[0] === 'Choose when the fiscal year starts', `compose ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(JSON.stringify(callsOf(m, 'skipModelPlanItem').map((c) => c.params)) === JSON.stringify([['pt-3f9a2c11', 'Drafted as steps.']]), `skip ${JSON.stringify(callsOf(m, 'skipModelPlanItem'))}`),
    ...has(m.draftOpen && m.summary === 'Pick the steps. The ones your words name come first.' && m.menu.length > 0, 'the new draft opens as the menu'),
  ],
});
// Ctrl J on the Plan: a task drafts and opens its row; on another page it opens under the header.
await scene('ctrl-j', {
  start: null, open: false, script: { composeTask: 'from_words_menu' }, steps: [{ key: 'j', settle: 600 }],
  expect: (m) => [
    ...has(m.askBar && m.askPlaceholder === 'Describe a task', `ask bar ${m.askBar} "${m.askPlaceholder}"`),
    ...has(/Esc closes it/.test(m.askBarText), 'the hint'),
  ],
});
await scene('ctrl-j-task', {
  start: null, open: false, script: { composeTask: 'from_words_menu' },
  steps: [{ key: 'j', settle: 500 }, { type: '[data-testid="ask-bar-input"]', value: 'Add a measure for total quantity and describe it' }, { enter: '[data-testid="ask-bar-input"]', settle: 1500 }],
  expect: (m) => [
    ...has(callsOf(m, 'composeTask').length === 1, `${callsOf(m, 'composeTask').length} compose calls`),
    ...has(!m.askBar, 'the ask box closes'),
    ...has(m.draftOpen && m.onPlan, 'the Plan opens with the draft open'),
  ],
});
// Journey m11 F7: words typed in the Plan's field, then Ctrl J. The ask box opened empty and the words stayed behind.
// They go with you now, and leave the Plan's field once the draft is made from them.
await scene('ctrl-j-carries-field', {
  start: null, open: false, script: { composeTask: 'from_words_menu' },
  steps: [{ type: '[data-testid="plan-field"]', value: 'Hide the key columns in the Sales table', settle: 300 }, { key: 'j', settle: 600 },
    { mark: 'opened', settle: 0 }, { enter: '[data-testid="ask-bar-input"]', settle: 1500 }],
  expect: (m) => [
    ...has(m.marks.opened?.askValue === 'Hide the key columns in the Sales table', `the ask box opened with "${m.marks.opened?.askValue}"`),
    ...has(JSON.stringify(callsOf(m, 'composeTask').map((c) => c.params[0])) === '["Hide the key columns in the Sales table"]', `compose ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(m.draftOpen && !m.askBar, 'the draft opens on the Plan and the ask box closes'),
    ...has(m.planField === '', `the Plan's field still holds "${m.planField}" after the draft was made from it`),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'Ctrl J did not also add the words as a task'),
  ],
});
// UAT S12-2-008 (lost work): Ctrl J again while the ask box is open cleared the words typed in it. It keeps them now,
// and puts the cursor back in the box.
await scene('ctrl-j-keeps-typing', {
  start: null, open: false, tab: 'bpa',
  steps: [{ key: 'j', settle: 500 }, { type: '[data-testid="ask-bar-input"]', value: 'hide the keys', settle: 200 },
    { key: 'j', settle: 400 }, { key: 'j', settle: 400 }, { mark: 'again', settle: 0 }],
  expect: (m) => [
    ...has(m.marks.again?.askValue === 'hide the keys', `after two more Ctrl J the ask box holds "${m.marks.again?.askValue}"`),
    ...has(m.marks.again?.askFocused === true, 'the cursor is back in the ask box'),
  ],
});
await scene('ctrl-j-other-page', {
  start: null, open: false, tab: 'bpa', steps: [{ key: 'j', settle: 600 }],
  expect: (m) => has(m.askBar && !m.onPlan, `ask bar on another page: ${m.askBar}, plan ${m.onPlan}`),
});
// Kane, 27 Sep: Ctrl J's question routing is out of this release, so Ctrl J drafts every line (fix-askdata-r4). A line
// that reads as a question, and a task that says "by" (the verifier's lost task, verify-askdata-r3 R1), both draft and
// neither is sent to routeAsk.
await scene('ctrl-j-question', {
  start: null, open: false, tab: 'bpa', script: { composeTask: 'from_words_menu' },
  steps: [{ key: 'j', settle: 500 }, { type: '[data-testid="ask-bar-input"]', value: 'what were net sales last month?' }, { enter: '[data-testid="ask-bar-input"]', settle: 1500 }],
  expect: (m) => [
    ...has(callsOf(m, 'routeAsk').length === 0, `${callsOf(m, 'routeAsk').length} routeAsk calls: Ctrl J routes nothing`),
    ...has(JSON.stringify(callsOf(m, 'composeTask').map((c) => c.params[0])) === '["what were net sales last month?"]', `compose ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(m.draftOpen && m.onPlan && !m.askBar, 'the Plan opens with the draft open and the ask box closes'),
  ],
});
await scene('ctrl-j-task-that-says-by', {
  start: null, open: false, tab: 'bpa', script: { composeTask: 'from_words_menu' },
  steps: [{ key: 'j', settle: 500 }, { type: '[data-testid="ask-bar-input"]', value: 'Month Name should sort by Month Number' }, { enter: '[data-testid="ask-bar-input"]', settle: 1500 }],
  expect: (m) => [
    ...has(callsOf(m, 'routeAsk').length === 0, `${callsOf(m, 'routeAsk').length} routeAsk calls`),
    ...has(JSON.stringify(callsOf(m, 'composeTask').map((c) => c.params[0])) === '["Month Name should sort by Month Number"]', `compose ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(m.draftOpen && m.onPlan, 'the Plan opens with the draft open'),
  ],
});
// Sol on slice 10 (F-289): a second Enter while the draft is still on its way sends nothing. The draft is held so the
// first one is still in flight when the next Enters land.
await scene('ctrl-j-draft-twice', {
  start: null, open: false, tab: 'bpa', script: { composeTask: 'from_words_menu' },
  steps: [{ hold: 'composeTask', settle: 100 }, { key: 'j', settle: 500 }, { type: '[data-testid="ask-bar-input"]', value: 'where do I add a hierarchy?' },
    { enter: '[data-testid="ask-bar-input"]', settle: 300 }, { enter: '[data-testid="ask-bar-input"]', settle: 300 }, { enter: '[data-testid="ask-bar-input"]', settle: 300 }],
  expect: (m) => has(m.heldCompose === 1, `${m.heldCompose} drafts sent for one`),
});
// Free: the Pro line in Draft the steps' place, and Ctrl J's.
await scene('free', {
  start: null, open: false, free: true, steps: [{ click: '[data-testid="plan-only-you-toggle"]' }, { click: '[data-row="pt-3f9a2c11"] [data-testid="plan-row-more"]' }],
  // Round 1: Draft steps is in the dots, faded with its reason (Pro) on free.
  expect: (m) => has(m.rowMenu.some((t) => /^Draft steps\s*Pro$/.test(t)), `free dots ${JSON.stringify(m.rowMenu)}`),
});
await scene('free-ctrl-j', {
  start: null, open: false, free: true, steps: [{ key: 'j', settle: 600 }],
  expect: (m) => has(/Jev drafts the steps for a task from your words in Pro\./.test(m.askBarText) && m.askPlaceholder == null, `free ask bar "${m.askBarText}"`),
});

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => r.errors.length > 0 || r.notes.length > 0);
console.log(`\n${results.length} scenes, ${results.length - bad.length} clean.`);
for (const r of bad) console.log(`  ! ${r.scene}: ${[...r.notes, ...r.errors].join(' | ')}`);
const screenBad = screenFailures();
for (const f of screenBad) console.log(`  ! screen: ${f}`);
console.log(`results: ${join(OUT, 'results.json')}`);
if (bad.length > 0 || screenBad.length > 0) process.exitCode = 1;
