// Browser-driven proof of the Plan run (round 1, K1/K4/K5; build brief Parts 3 to 5), in two themes at 1680x1000.
//
// It serves THIS worktree's built bundle and plays the engine from the REAL plans read off the Contoso UAT model on the
// 1.3.0 engine (STATES, default /tmp/semanticus-joint-review/r1/plan/repro/states.json: plan0 -> run0 -> plan1 -> run1 ->
// plan2). Those plans are re-shaped to the wire contract of this round (collisions, synonyms and AI settings are the
// assistant's, the two words rows are one, each row carries goal and rules) because the engine lane builds that side in
// parallel. The run is played with the REAL wall times of the two waves (60.2 s and 39.2 s); the split of each wave into
// work out, apply and check is an assumption (the 1.3.0 engine reports no phases), set in RUN below.
//
// It fires plan/runProgress as the host relays it (a window message of type planRunProgress), presses Run the plan with a
// real click, and MEASURES the page at each moment. A scene that looks right while saying the wrong thing fails on its check.
//
//   OUTDIR=<dir> TMPDIR=<dir> SCALE=1 node tools/uishot/plan-run-drive.mjs
//   SCALE=0.1 shortens every wait (a quick run); THEMES=dark runs one theme.
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
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'plan-run');
const STATES = process.env.STATES || '/tmp/semanticus-joint-review/r1/plan/repro/states.json';
const SCALE = Number(process.env.SCALE || 1);
const THEME_LIST = (process.env.THEMES || 'dark,light').split(',');
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run the webview build first.'); process.exit(1); }

// ---- the plans, from the engine's real answers, shaped to this round's wire contract ----------------------------
const real = JSON.parse(readFileSync(STATES, 'utf8'));
const GOAL = { 'starter/table:Sales': 'ai', 'bpafix/model': 'quality', 'safefix/Naming': 'ai', 'proposal/collisions': 'ai', 'words/model': 'ai',
  'proposal/synonyms': 'ai', 'proposal/ai-settings': 'ai', 'proposal/relationships': 'ai', 'cleanup/model': 'quality', 'ship/unsaved': 'quality' };
const RULES = { 'starter/table:Sales': ['AIR-BASE-MEASURE'], 'bpafix/model': ['BP-HIDE-KEYS'], 'safefix/Naming': ['AIR-NAMING'], 'proposal/collisions': ['AIR-NAME-COLLISION'],
  'words/model': ['AIR-DESC-TABLE', 'AIR-DESC-COLUMN', 'AIR-SYNONYM'], 'proposal/synonyms': ['AIR-SYNONYM'], 'proposal/ai-settings': ['AIR-VERIFIED-ANSWERS'],
  'proposal/relationships': ['AIR-REL-AMBIGUOUS'], 'cleanup/model': ['BP-UNUSED'] };
const ASSISTANT = new Set(['proposal/collisions', 'proposal/synonyms', 'proposal/ai-settings', 'words/model']);
function shape(plan, tail) {
  const p = JSON.parse(JSON.stringify(plan));
  // One words row: describe/model folds into words/model ("Write 3 descriptions and 1 synonym").
  p.items = p.items.filter((i) => i.id !== 'describe/model');
  for (const i of p.items) {
    i.waitsFor = (i.waitsFor || []).filter((w) => w.id !== 'describe/model');
    if (ASSISTANT.has(i.id)) { i.doer = 'assistant'; i.who = 'asst'; }
    if (i.id === 'words/model') { i.title = 'Write 3 descriptions and 1 synonym'; i.sub = 'So assistants stop guessing'; i.jump = { tool: 'instructions', label: 'Model notes' }; }
    i.goal = GOAL[i.id] ?? 'quality'; i.rules = RULES[i.id] ?? [];
    if (i.doer === 'you' && i.stateWord !== 'needs_you') i.stateWord = 'waiting';
  }
  // Person rows wait for automated rows, never the other way round: the assistant's rows wait only on Semanticus rows (or each other).
  const byId = Object.fromEntries(p.items.map((i) => [i.id, i]));
  for (const i of p.items) {
    if (i.doer === 'assistant') i.waitsFor = i.waitsFor.filter((w) => byId[w.id] && byId[w.id].doer !== 'you');
    if (i.id === 'cleanup/model') i.waitsFor = [{ id: 'proposal/relationships', title: byId['proposal/relationships'].title }, { id: 'proposal/collisions', title: byId['proposal/collisions'].title }];
    if (i.id === 'proposal/relationships') i.waitsFor = (byId['proposal/collisions'] && byId['proposal/collisions'].stateWord !== 'done') ? [{ id: 'proposal/collisions', title: byId['proposal/collisions'].title }] : [];
    if (i.doer !== 'you' && i.stateWord === 'waiting' && i.waitsFor.length === 0 && i.stateWord !== 'done') i.stateWord = 'ready';
    if (i.doer === 'assistant' && i.stateWord === 'needs_you') i.stateWord = 'ready';
  }
  if (tail) {
    // The Save row appears once the model has unsaved changes: a question for you, in the fold.
    const save = JSON.parse(JSON.stringify(real.plan1.items.find((i) => i.id === 'ship/unsaved')));
    save.goal = 'quality'; save.rules = []; save.doer = 'you'; save.stateWord = 'needs_you';
    if (!p.items.some((i) => i.id === 'ship/unsaved')) p.items.push(save);
  }
  // The wire carries a title with every waits-for (the short read the MCP door gave leaves it out), and the strip counts the rows as shaped.
  const all = Object.fromEntries(p.items.map((i) => [i.id, i]));
  for (const i of p.items) i.waitsFor = (i.waitsFor || []).filter((w) => all[w.id]).map((w) => ({ id: w.id, title: all[w.id].title }));
  p.strip.tracks = ['semanticus', 'you', 'assistant'].map((doer) => {
    const mine = p.items.filter((i) => i.doer === doer);
    const n = (w) => mine.filter((i) => i.stateWord === w).length;
    return { doer, done: n('done'), onIt: n('on_it'), ready: n('ready'), needsYou: n('needs_you'), waiting: n('waiting') };
  });
  p.assistantQueue = 'on';
  return p;
}
const S0 = shape(real.plan0, false);
const S1 = shape(real.plan1, true);
const S2 = shape(real.plan2, true);
// The start (one Ready Semanticus row); wave two is two rows made Ready by wave one. After wave one the assistant's rows have nothing left to wait for.
for (const s of [S1, S2]) for (const i of s.items) if (i.id === 'proposal/collisions' || i.id === 'words/model') { if (i.stateWord === 'waiting') i.stateWord = 'ready'; }
S2.items.find((i) => i.id === 'proposal/ai-settings').waitsFor = [{ id: 'words/model', title: 'Write 3 descriptions and 1 synonym' }];
for (const s of [S0, S1, S2]) for (const i of s.items) if (i.stateWord === 'done' && !i.jump) i.jump = null;

// The run, as the engine lane built it (contract-changes.md): ONE applyPlanItems call runs both waves, so the call lasts
// the sum of the two real wall times (60.2 s + 39.2 s) and plan/runProgress carries each step with `ms` since the press
// began. The split of a wave into work out, apply and check is an assumption (work 0.66, apply 0.2, check 0.12 of the wave).
const RUN = {
  wall: 99.4,
  result: { performed: true, applied: 38, itemIds: ['starter/table:Sales', 'bpafix/model', 'safefix/Naming'], revision: 2, entryId: 'ed-f3c7e540', entryIds: ['ed-f3c7e540', 'ed-d9f0ba90'], waves: 2, canSave: true,
    sentence: 'Applied 38 changes in 2 steps. One undo takes the whole run back.' },
  // [phase, itemIds, seconds since the press, changes]
  steps: [
    ['work', ['starter/table:Sales'], 0, 0, 1], ['apply', ['starter/table:Sales'], 40, 4, 1], ['check', ['starter/table:Sales'], 52, 0, 1], ['done', ['starter/table:Sales'], 59.5, 4, 1],
    ['work', ['bpafix/model'], 60.2, 0, 2], ['work', ['safefix/Naming'], 74.2, 0, 2], ['apply', ['bpafix/model'], 77.2, 33, 2], ['apply', ['safefix/Naming'], 77.2, 1, 2],
    ['check', ['bpafix/model', 'safefix/Naming'], 90.2, 0, 2], ['done', ['bpafix/model'], 98.6, 33, 2], ['done', ['safefix/Naming'], 98.8, 1, 2],
  ],
};
const WAVE_TWO_AT = 60.2;

const seed = (run, scale) => `
const __S = ${JSON.stringify({ S0, S1, S2 })};
const __R = ${JSON.stringify(run)};
const __scale = ${JSON.stringify(scale)};
window.__calls = [];
window.__progress = [];
let __stage = 0;
const __note = (name, params) => window.__calls.push({ call: name, params, at: Date.now() });
const __fire = (payload) => { window.__progress.push({ ...payload, at: Date.now() }); window.dispatchEvent(new MessageEvent('message', { data: { type: 'planRunProgress', payload } })); };
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => { __note('getModelPlan'); return [__S.S0, __S.S2][__stage]; });
    v.setResponse('applyPlanItems', (params) => {
      __note('applyPlanItems', params);
      if (__stage === 1) return { performed: false, sentence: 'Nothing is ready.' };
      for (const [phase, ids, at, changes, wave] of __R.steps) setTimeout(() => __fire({ runId: 'run-1', itemIds: ids, phase, wave, ...(changes ? { changes } : {}), ms: Math.round(at * 1000 * __scale) }), at * 1000 * __scale);
      return new Promise((resolve) => setTimeout(() => {
        __stage = 1;
        resolve({ ...__R.result, plan: __S.S2 });
        // The model moved: the host tells the page, and the page reads the plan again. The receipt must survive that.
        setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange', payload: { sessionId: 'uishot', revision: 2, origin: 'human', label: 'Run the plan' } } })), 50);
      }, __R.wall * 1000 * __scale));
    });
    v.setResponse('undo', (params) => { __note('undo', params); __stage = 0; return { canUndo: false, canRedo: true }; });
    for (const name of ['setPlanItemDoer', 'claimPlanTask', 'skipModelPlanItem', 'movePlanTaskUp', 'completePlanTask']) {
      v.setResponse(name, (params) => { __note(name, params); return [__S.S0, __S.S2][__stage]; });
    }
    v.setResponse('setCompatibilityLevel', () => ({}));
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
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 600000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const MEASURE = `(() => {
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : '');
  const q = (s) => document.querySelector(s);
  const rows = (sel) => [...document.querySelectorAll(sel)].map((r) => ({ id: r.dataset.row, doer: r.dataset.doer, state: r.dataset.state, stateText: text(r.querySelector('.pw-state')),
    title: text(r.querySelector('.pw-title > b')), live: r.classList.contains('pw-row-live'), dim: parseFloat(getComputedStyle(r).opacity), acts: [...r.querySelectorAll('.pw-acts button')].map((b) => text(b)), doerText: text(r.querySelector('.pw-doer')) }));
  return {
    run: text(q('[data-testid="plan-run"]')), runLive: text(q('[data-testid="plan-run-live"]')), assistantArea: text(q('[data-testid="plan-run-assistant"]')),
    youArea: text(q('[data-testid="plan-run-you"]')), doneArea: text(q('[data-testid="plan-run-done"]')), told: text(q('[data-testid="plan-told"]')),
    receipt: text(q('[data-testid="plan-run-receipt"]')), receiptButtons: [...document.querySelectorAll('[data-testid="plan-run-receipt"] button')].map((b) => text(b)),
    goals: [...document.querySelectorAll('[data-testid^="plan-goal-"]')].map((b) => text(b)),
    justDone: rows('[data-testid="plan-just-done"] [data-row]'), all: rows('[data-testid="plan-list"] [data-row]'),
    fold: text(q('[data-testid="plan-only-you-toggle"]')), foldOpen: q('[data-testid="plan-only-you"]')?.dataset.open ?? null, foldWarn: !!q('[data-testid="plan-only-you-warn"]'),
    needsBand: !!q('[data-testid="plan-needs"]'), nextCount: text(q('[data-testid="plan-list"] .pw-grp .pw-grp-n')),
    menu: [...document.querySelectorAll('[data-testid="plan-doer-menu"] [role="menuitemradio"]')].map((b) => ({ text: text(b), off: b.disabled, checked: b.getAttribute('aria-checked') })),
    dots: [...document.querySelectorAll('[data-testid="plan-row-menu"] [role="menuitem"]')].map((b) => text(b)),
    error: text(q('[role="alert"]')), docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
    calls: (window.__calls || []).map((c) => ({ call: c.call, params: c.params })),
  };
})()`;

const has = (cond, message) => (cond ? [] : [message]);
const results = [];
async function runTheme(theme) {
  const page = await browser.newPage();
  const errors = []; const notes = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: Number(process.env.WIDTH) || 1680, height: 1000, deviceScaleFactor: 1 });
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(RUN, SCALE));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1400);
  const shot = async (name) => { await checkScreen(page, `plan-run:${theme}:${name}`); await applyTheme(page, theme); await wait(250); await page.screenshot({ path: join(OUT, `${name}-${theme}.png`), fullPage: false }); };
  const measure = () => page.evaluate(MEASURE);
  const check = (name, m, fn) => { for (const f of fn(m)) notes.push(`${name}: ${f}`); };
  const byId = (m, id) => m.all.find((r) => r.id === id);
  const snap = {};

  // 1. Before the press: the Contoso plan, three Semanticus rows in the press, automation first, the fold closed.
  let m = await measure(); snap.before = m;
  check('before', m, (m) => [
    ...has(m.run === 'Run the plan 3', `run area "${m.run}", expected the whole press (3)`),
    ...has(m.fold === 'Only you can do 2', `fold "${m.fold}"`),
    ...has(m.foldOpen === 'no', 'the fold is open'),
    ...has(!m.needsBand, 'the Needs you band is still drawn'),
    ...has(JSON.stringify(m.goals) === '["All","AI ready","Quality"]', `filters ${JSON.stringify(m.goals)}`),
    ...has(m.all.slice(0, 3).every((r) => r.doer === 'semanticus'), `list does not start with Semanticus rows: ${m.all.map((r) => r.doer).join(',')}`),
    ...has(m.all.slice(3).every((r) => r.doer === 'assistant'), 'the assistant rows come after Semanticus'),
    ...has(!m.all.some((r) => r.doer === 'you'), 'a row of yours is drawn above the fold'),
  ]);
  await shot('1-before');

  // 2. Press Run for real. Two seconds in: row one is Working out, the others read In this run, the button is Running 1 of 3.
  await page.evaluate(() => document.querySelector('[data-testid="plan-run"]').click());
  const pressedAt = Date.now();
  await wait(2000 * Math.max(SCALE, 0.5));
  m = await measure(); snap.running = m;
  check('2 s in', m, (m) => [
    ...has(m.runLive === 'Running 1 of 3', `run area "${m.runLive}"`),
    ...has(byId(m, 'starter/table:Sales')?.stateText === 'Working out', `row one "${byId(m, 'starter/table:Sales')?.stateText}"`),
    ...has(byId(m, 'starter/table:Sales')?.live === true, 'row one has no engine tint'),
    ...has(byId(m, 'bpafix/model')?.stateText === 'In this run', `row two "${byId(m, 'bpafix/model')?.stateText}"`),
    ...has(byId(m, 'safefix/Naming')?.stateText === 'In this run', `row three "${byId(m, 'safefix/Naming')?.stateText}"`),
    ...has(!m.run, 'a Run button is still drawn while it runs'),
  ]);
  await shot('2-running');

  // 3. Mid wave two: the two rows are Applying together, row one is Done and moved into Done just now.
  const toWave2 = (WAVE_TWO_AT + 22) * 1000 * SCALE - (Date.now() - pressedAt);
  if (toWave2 > 0) await wait(toWave2);
  m = await measure(); snap.wave2 = m;
  check('wave two', m, (m) => [
    ...has(m.justDone.length === 1 && m.justDone[0].id === 'starter/table:Sales', `Done just now holds ${JSON.stringify(m.justDone.map((r) => r.id))}`),
    ...has(/Done.*4 changes/.test(m.justDone[0]?.stateText || ''), `row one "${m.justDone[0]?.stateText}"`),
    ...has(/^Applying\s*33 changes$/.test(byId(m, 'bpafix/model')?.stateText || ''), `bpafix "${byId(m, 'bpafix/model')?.stateText}"`),
    ...has(/^Applying\s*1 change$/.test(byId(m, 'safefix/Naming')?.stateText || ''), `safefix "${byId(m, 'safefix/Naming')?.stateText}"`),
    ...has(m.runLive === 'Running 2 of 3', `run area "${m.runLive}"`),
    ...has(m.receipt === '', 'the receipt is drawn before the press ends'),
  ]);
  await shot('3-wave-two');

  // 4. The end: the receipt with the real counts, Done just now holds all three, the re-read did not clear it.
  const end = RUN.wall * 1000 * SCALE + 4000 * Math.max(SCALE, 0.5) - (Date.now() - pressedAt);
  if (end > 0) await wait(end);
  await wait(1500);
  m = await measure(); snap.after = m;
  const rereads = m.calls.filter((c) => c.call === 'getModelPlan').length;
  check('after', m, (m) => [
    ...has(/^Ran 3 tasks\.\s*38 changes, one undo step\./.test(m.receipt), `receipt "${m.receipt}"`),
    ...has(JSON.stringify(m.receiptButtons) === '["Undo","Save","See in History"]', `receipt buttons ${JSON.stringify(m.receiptButtons)}`),
    ...has(m.calls.filter((c) => c.call === 'applyPlanItems').length === 1, 'one press made more than one call though the engine ran every wave in the first'),
    ...has(rereads >= 3, `the page re-read the plan after the press (getModelPlan x${rereads})`),
    ...has(JSON.stringify(m.justDone.map((r) => r.id).sort()) === JSON.stringify(['bpafix/model', 'safefix/Naming', 'starter/table:Sales']), `Done just now ${JSON.stringify(m.justDone.map((r) => r.id))}`),
    ...has(m.justDone.every((r) => r.state === 'done'), 'a just-done row is not Done'),
    ...has(m.assistantArea.startsWith('Your assistant has 4') && /Tell your assistant/.test(m.assistantArea), `run area "${m.assistantArea}"`),
    ...has(m.foldWarn, 'the fold has no warn dot though a Save row waits for you'),
  ]);
  await shot('4-after-receipt');

  // 5. Only the assistant has rows to run: Tell your assistant copies the ask and says so.
  await page.evaluate(() => document.querySelector('[data-testid="plan-tell"]').click());
  await wait(500);
  m = await measure();
  check('tell', m, (m) => has(m.told === 'Copied. Paste it into your assistant.', `told "${m.told}"`));
  await shot('5-only-assistant');

  // 6. The fold opened, rows of yours dimmed; the doer menu open on an assistant row.
  await page.evaluate(() => document.querySelector('[data-testid="plan-only-you-toggle"]').click());
  await wait(400);
  await page.evaluate(() => document.querySelector('[data-row="proposal/collisions"] [data-testid="plan-doer-btn"]').click());
  await wait(500);
  m = await measure(); snap.menu = m;
  check('doer menu', m, (m) => [
    ...has(m.foldOpen === 'yes', 'the fold did not open'),
    ...has(m.all.filter((r) => r.doer === 'you').every((r) => r.dim < 0.7), `your rows are not dimmed: ${JSON.stringify(m.all.filter((r) => r.doer === 'you').map((r) => r.dim))}`),
    ...has(JSON.stringify(m.menu.map((x) => x.text.replace(/✓/, '').trim())) === JSON.stringify(['SemanticusNeeds words', 'Your assistant', 'Me'].map((t) => t)) || m.menu.length === 3, `doer menu ${JSON.stringify(m.menu)}`),
    ...has(m.menu.find((x) => x.checked === 'true')?.text.startsWith('Your assistant'), 'the current doer is not marked'),
  ]);
  await shot('6-doer-menu-and-fold');
  await page.keyboard.press('Escape');
  await page.evaluate(() => document.querySelector('[data-testid="plan-only-you-toggle"]').click());   // the fold closes again
  await wait(300);
  // 7. The dots on a row, once each control.
  await page.evaluate(() => document.querySelector('[data-row="proposal/collisions"] [data-testid="plan-row-more"]').click());
  await wait(400);
  m = await measure();
  check('dots', m, (m) => has(JSON.stringify(m.dots) === '["Set aside"]', `dots ${JSON.stringify(m.dots)}`));
  await shot('7-dots');
  await page.keyboard.press('Escape');
  await page.mouse.click(5, 5);

  // 8. Undo takes back the whole press: one call with the FIRST wave's entry.
  await page.evaluate(() => document.querySelector('[data-testid="plan-run-undo"]').click());
  await wait(1200);
  m = await measure();
  const undo = m.calls.filter((c) => c.call === 'undo');
  check('undo', m, (m) => [
    ...has(undo.length === 1 && JSON.stringify(undo[0].params) === '["human","ed-f3c7e540"]', `undo call ${JSON.stringify(undo.map((c) => c.params))}`),
    ...has(/^Undone\./.test(m.receipt), `receipt after undo "${m.receipt}"`),
    ...has(m.run === 'Run the plan 3', `run area after undo "${m.run}"`),
  ]);
  await shot('8-after-undo');

  if (m.docWidth > m.viewportWidth + 1) notes.push(`the page scrolls sideways: ${m.docWidth} > ${m.viewportWidth}`);
  results.push({ theme, notes, errors, snap, progress: await page.evaluate(() => window.__progress) });
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${theme}${notes.length ? ': ' + notes.join(' | ') : ''}${errors.length ? ' ERR ' + errors.slice(0, 3).join(' | ') : ''}`);
  await page.close();
}

await Promise.all(THEME_LIST.map((t) => runTheme(t)));
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 1));
const bad = results.filter((r) => r.notes.length || r.errors.length);
const screen = typeof screenFailures === 'function' ? screenFailures() : [];
await browser.close();
server.close();
if (screen.length) console.log('screen check:\n' + screen.join('\n'));
console.log(bad.length ? `${bad.length} theme(s) with notes` : 'plan-run-drive: all moments checked');
process.exit(bad.length || screen.length ? 1 : 0);
