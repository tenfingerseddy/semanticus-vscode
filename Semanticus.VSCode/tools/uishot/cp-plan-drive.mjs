// Browser-driven proof of the Plan at the heart (lane U1), on both sides of the free and Pro line, in three themes.
//
// It serves THIS worktree's built bundle, drives the real React page with real clicks and real keys, and MEASURES what
// landed into results.json next to its PNGs: the strip's counts, the rows (source, doer, state, page, buttons),
// the opened detail, the switch, what the assistant sees, and every call the page made with its arguments. A scene
// that looks right while saying the wrong thing fails on its `expect`.
//
// The engine is played by fixtures/plan-heart-fixture.mjs, which IS E1's shape file
// (docs/redesign/contracts/plan-item.json) plus a small mock of the person controls. Its source is INLINED into the
// page, because the mock answers as a function of each call's params and a function does not survive JSON.
//
// Scenes follow the approved mockup plan-heart.html: section 2 (the page, a track filtered), 3 (a row opened), 5 (the
// assistant's track with the switch on and paused), 6 (Run the plan and "Now ready"), plus the field, Pause, Up and
// the free Pro line. Each scene is shot in Dark Modern, Light Modern and Dark High Contrast.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-plan-drive.mjs
//   ONLY=queue-on,run node tools/uishot/cp-plan-drive.mjs      just those scenes
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'plan');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;

if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.');
  process.exit(1);
}

// The fixture, as browser-ready source, with the engine's current assistant note.
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '')
  .replaceAll('on its next call', 'when your assistant next checks the Plan');
// U2's History engine, so opening History from the Plan draws the real History rather than a half-answered one.
const HISTORY_SRC = readFileSync(join(here, 'fixtures', 'plan-history-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const FOLD_ON = readFileSync(join(here, 'fixtures', 'plan-heart-fold-on.txt'), 'utf8');
const FOLD_PAUSED = readFileSync(join(here, 'fixtures', 'plan-heart-fold-paused.txt'), 'utf8');

// The engine stub, installed before studio.js makes its first call (it rides in on the harness setter, as every
// driver's seed does). tools/uishot/visual-scenes.mjs lifts THIS TEMPLATE out of the source text for the screen
// check by its opening and closing text, so keep this template's first and last lines as they are, and never
// quote them in a comment above it (the lift finds the first match).
const seed = (mode, free) => `
${FIXTURE_SRC}
window.__planCalls = [];
${typeof HISTORY_SRC === 'string' ? `window.__ph = (() => { ${HISTORY_SRC}\n return makeHistoryEngine({}); })();` : ''}
let __plan = makeHeartPlan({ mode: ${JSON.stringify(mode)}, free: ${JSON.stringify(!!free)} });
if (__plan.orderedBy === 'In value order, highest first.') __plan.orderedBy = 'Tasks with the greatest expected benefit come first.';
if (${JSON.stringify(mode)} === 'collisions') { const base = JSON.parse(JSON.stringify(__plan.items.find((r) => r.doer === 'you') || __plan.items[0])); __plan.items = [{ ...base, id: 'proposal/collisions', title: 'Fix 3 names that Copilot reads as the same word', sub: 'Group1Key, Group2Key, PRAC_CD', stateWord: 'needs_you', status: 'proposed', jump: { tool: 'search', label: 'Find and replace', find: ['Group1Key', 'Group1 Key', 'PRAC_CD'] } }]; }
const __note = (name, params) => window.__planCalls.push({ call: name, params: params });
const __person = (name, fn) => (params) => { __note(name, params); __plan = fn(__plan, params); return __plan; };
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => __plan);
    if (window.__ph) for (const name of ['getPlanHistory', 'listVerifiedEdits', 'listHistoryCheckpoints']) {
      v.setResponse(name, (params) => { __note(name, params); return window.__ph[name](...(params || [])); });
    }
    v.setResponse('holdPlanItem', __person('holdPlanItem', (p, a) => heartHold(p, a[0], a[1])));
    v.setResponse('movePlanTaskUp', __person('movePlanTaskUp', (p, a) => heartUp(p, a[0])));
    v.setResponse('setPlanItemDoer', __person('setPlanItemDoer', (p, a) => heartDoer(p, a[0], a[1])));
    v.setResponse('setAssistantQueue', __person('setAssistantQueue', (p, a) => heartQueue(p, a[0])));
    v.setResponse('setPlanTaskNote', __person('setPlanTaskNote', (p, a) => heartNote(p, a[0], a[1])));
    v.setResponse('claimPlanTask', __person('claimPlanTask', (p, a) => heartClaim(p, a[0])));
    v.setResponse('completePlanTask', __person('completePlanTask', (p, a) => heartComplete(p, a[0], a[1])));
    v.setResponse('declinePlanTask', __person('declinePlanTask', (p, a) => heartDecline(p, a[0], a[1])));
    v.setResponse('skipModelPlanItem', __person('skipModelPlanItem', (p, a) => heartSkip(p, a[0], a[1])));
    // Remove and its Undo (Kane's UAT, 25 Sep): person-only on the engine, answered here by the mock.
    window.__removedRows = {};
    v.setResponse('removePlanRow', (params) => { __note('removePlanRow', params); const r = heartRemove(__plan, params[0]); __plan = r.result.plan; window.__removedRows[params[0]] = r; return r.result; });
    v.setResponse('restorePlanRow', (params) => { __note('restorePlanRow', params); __plan = heartRestore(__plan, window.__removedRows[params[0]]); return __plan; });
    // Refresh plan (Kane, 11 Oct): the engine scans again, adds what is new, and takes the finished rows off the list.
    v.setResponse('refreshPlan', (params) => { __note('refreshPlan', params);
      const done = __plan.items.filter((r) => r.stateWord === 'done' || r.status === 'done');
      const base = JSON.parse(JSON.stringify(__plan.items.find((r) => r.doer === 'semanticus' && r.stateWord === 'ready') || __plan.items[0]));
      const fresh = { ...base, id: 'safefix/Store', title: 'Hide 2 key columns on Store', sub: 'StoreKey, RegionKey', why: ['Store has 2 key columns that people do not need to see.'],
        stateWord: 'ready', status: 'ready', state: 'gap', changeSet: null, waitsFor: [], held: false, doer: 'semanticus', who: 'auto', source: 'found',
        originLink: null, claim: null, note: null, stateNote: null, order: null, jump: { tool: 'readiness', label: 'AI readiness' } };
      __plan = { ...__plan, items: [...__plan.items.filter((r) => !done.includes(r)), fresh], __doneBase: { semanticus: 0, you: 0, assistant: 0 } };
      __plan = recount(__plan);
      const sentence = '1 new task, ' + done.length + ' done task' + (done.length === 1 ? '' : 's') + ' cleared.';
      return { added: 1, addedIds: [fresh.id], cleared: done.length, sentence, plan: __plan }; });
    v.setResponse('addPlanTask', (params) => { __note('addPlanTask', params); const r = heartAdd(__plan, params[0] || {}); __plan = r.plan; return r; });
    v.setResponse('applyPlanItems', (params) => { __note('applyPlanItems', params); const r = heartRun(__plan, params[0], params[2]); __plan = r.plan; return r; });
    // Keep or drop one line: answered with a change plan by the engine, so the page reads the plan again.
    v.setResponse('setPlanItem', (params) => { __note('setPlanItem', params); __plan = heartDecideLine(__plan, params[0], params[2]); return { items: [] }; });
    v.setResponse('modelPlanAction', (params) => { __note('modelPlanAction', params); const r = heartApprove(__plan, params[0], params[3]); __plan = r.plan; return r; });
    // Raise compatibility level on the facts line (moved from the Overview, lane R1): the call is recorded.
    v.setResponse('setCompatibilityLevel', (params) => { __note('setCompatibilityLevel', params); return { compatibilityLevel: params[0] }; });
    // The field's route suggestion (Pro): a place, never an action.
    v.setResponse('routeAsk', (params) => { __note('routeAsk', params); const t = String(params[0]);
      // An answer that never comes, so a scene can read what the field shows while newer words wait on theirs.
      if (/in March$/i.test(t)) return '__uishotNever';
      if (/hierarch/i.test(t)) return { tool: 'diagram', confidence: 0.9, confidenceBar: 0.6 };
      // Two readings, as AskRoutePoint.Readings answers them since fix-askdata-r6: a line that splits a number and
      // tells reads as a task first, one that plainly asks reads as a question first, and neither hides the other.
      if (/double counting/i.test(t)) return { tool: null, area: null, confidence: 0, asked: false, reading: 'task',
        sentence: 'Keep it as a task. Semanticus does not know a page for this one.',
        other: { kind: 'question', tool: 'askdata', area: 'explore', confidence: 1, line: 'Or ask it as a question.' } };
      if (/show me|ask your data/i.test(t)) return { tool: 'askdata', area: 'explore', confidence: 1, asked: false, reading: 'question',
        other: { kind: 'task', tool: null, confidence: 0, line: 'Keep it as a task. Semanticus does not know a page for this one.' } };
      return { tool: null, fallback: 'menu', confidence: 0, asked: false, sentence: 'Nothing here matches those words yet. Try a page name, or the name of a thing in the model.', reason: 'no match' }; });
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

// What a scene reads off the page. Every field is a measurement of the DOM, not of the fixture.
const MEASURE = `(() => {
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : '');
  const q = (s) => document.querySelector(s);
  return {
    onPlan: !!q('[data-testid="plan-page"]'),
    // Ask your data's own question box, read off that page once a button has opened it (verify-askdata-r6 L2).
    onAskData: !!q('[data-testid="askdata-page"]'),
    askWords: (q('input[aria-label="Ask in your own words"]') || {}).value ?? null,
    // Find and replace, once a Plan row's Choose has opened it with names filled in (plan statement check 2, item 6).
    findBox: (q('input[aria-label="Find"]') || {}).value ?? null,
    findRegexOn: [...document.querySelectorAll('.sem-seg-item')].some((b) => b.textContent.trim() === 'Regular expression' && b.getAttribute('aria-pressed') === 'true'),
    view: (q('[data-testid="plan-page"]') || {}).dataset?.view ?? null,
    track: (q('[data-testid="plan-page"]') || {}).dataset?.track ?? null,
    grade: text(q('[data-testid="plan-grade"]')),
    bestPractice: text(q('[data-testid="plan-best-practice"]')),
    latest: text(q('[data-testid="plan-latest"]')),
    tracks: [...document.querySelectorAll('[data-testid^="plan-track-"]')].map((t) => ({
      doer: t.dataset.testid.slice('plan-track-'.length), pressed: t.getAttribute('aria-pressed') === 'true',
      segs: t.querySelectorAll('.pw-seg').length, counts: text(t.querySelector('.pw-counts')) })),
    facts: text(q('[data-testid="plan-facts"]')),
    field: (q('[data-testid="plan-field"]') || {}).placeholder ?? null,
    pageNotes: text(q('.studio-page-notes-pop')),
    fieldValue: (q('[data-testid="plan-field"]') || {}).value ?? null,
    fieldTag: (q('[data-testid="plan-field"]') || {}).tagName ?? null,
    fieldHeight: Math.round((q('[data-testid="plan-field"]')?.getBoundingClientRect().height) ?? 0),
    // One line of the field is one bar height (the --sem-bar-h token), read off the page rather than written here.
    fieldOneLine: parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--sem-bar-h')) || 0,
    fieldHint: text(q('[data-testid="plan-field-hint"]')),
    suggest: text(q('[data-testid="plan-field-suggest"]')),
    queue: [...document.querySelectorAll('[data-testid^="plan-queue-"]')].filter((b) => b.getAttribute('aria-pressed') === 'true').map((b) => b.textContent.trim()),
    // Round 1: rows only you can do sit in a closed fold at the end of the list (no Needs you band).
    fold: text(q('[data-testid="plan-only-you-toggle"]')),
    foldCount: Number((text(q('[data-testid="plan-only-you-toggle"]')).match(/(\\d+)$/) || [])[1] || 0),
    foldRows: [...document.querySelectorAll('[data-testid="plan-only-you"] [data-row]')].map((r) => r.dataset.row),
    doerMenu: [...document.querySelectorAll('[data-testid="plan-doer-menu"] [role="menuitemradio"]')].map((b) => ({ text: b.textContent.replace(/\\s+/g, ' ').trim(), off: b.disabled, checked: b.getAttribute('aria-checked') === 'true' })),
    rows: [...document.querySelectorAll('[data-testid="plan-list"] [data-row]')].map((r) => ({
      id: r.dataset.row, doer: r.dataset.doer, state: r.dataset.state,
      source: text(r.querySelector('.pw-src')), title: text(r.querySelector('.pw-title > b')), sub: text(r.querySelector('.pw-sub')),
      stateText: text(r.querySelector('.pw-state')), page: text(r.querySelector('.pw-page')), meta: text(r.querySelector('[data-testid="plan-row-meta"]')),
      actions: [...r.querySelectorAll('.pw-acts button:not(.pw-more)')].map((b) => (b.disabled ? b.textContent.trim() + ' (off)' : b.textContent.trim())),
      more: !!r.querySelector('[data-testid="plan-row-more"]') })),
    menu: [...document.querySelectorAll('[data-testid="plan-row-menu"] [role="menuitem"]')].map((b) => b.textContent.trim()),
    // Jev format suggestions (the format fill rule, addendum 2 item 11): counted off the change-set lines the page draws.
    suggestionNotes: [...document.querySelectorAll('[data-testid^="plan-suggestion-"]')].map((e) => e.textContent.replace(/\\s+/g, ' ').trim()),
    suggestionRows: document.querySelectorAll('[data-line][data-suggestion="yes"]').length,
    suggestionRowsWithTick: document.querySelectorAll('[data-line][data-suggestion="yes"] .plan-tick').length,
    suggestionRowsWithWord: document.querySelectorAll('[data-line][data-suggestion="yes"] [data-testid^="plan-jevword-"]').length,
    provedRowsWithTick: document.querySelectorAll('[data-line]:not([data-suggestion]) .plan-tick').length,
    provedRowsWithWord: document.querySelectorAll('[data-line]:not([data-suggestion]) [data-testid^="plan-jevword-"]').length,
    toast: text(q('[data-testid="plan-toast"]')),
    detail: text(q('[data-testid^="plan-detail-"]')),
    openRow: text(q('[data-testid="plan-list"] .pw-item-open')),
    showAll: text(q('[data-testid="plan-show-all"]')),
    run: text(q('[data-testid="plan-run"]')),
    runReceipt: text(q('[data-testid="plan-run-receipt"]')),
    proLine: text(q('[data-testid="plan-pro-line"]')),
    sees: (q('[data-testid="plan-sees-text"]') || {}).textContent ?? null,
    said: text(q('[data-testid="plan-said"]')),
    refreshButton: text(q('[data-testid="plan-refresh"]')),
    queueLine: text(q('[data-testid="plan-queue-line"]')),
    // The Overview's jobs on the Plan (lane R1, contract 11.1): the object line and Raise compatibility level.
    showing: text(q('[data-testid="plan-list"] .pw-grp')),
    objectActions: [...document.querySelectorAll('[data-testid="plan-object-actions"] button')].map((b) => b.textContent.trim()),
    raise: text(q('[data-testid="plan-raise"]')),
    starts: [...document.querySelectorAll('[data-testid="plan-starts"] .pw-start')].map((e) => e.dataset.testid),
    empty: !!q('[data-testid="plan-empty"]'),
    historyObject: text(q('[data-testid="ph-object"]')),
    historyRows: document.querySelectorAll('[data-testid="plan-history"] [data-hid]').length,
    error: text(q('[role="alert"]')),
    planCalls: window.__planCalls || [],
    docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
    docHeight: document.documentElement.scrollHeight, viewportHeight: window.innerHeight,
    headerFold: document.querySelector('header.studio-chrome')?.dataset.fold ?? null,
    anchor: window.__anchor || null,
    // Where the opened row's change list starts its text, against where the row's title starts (v-surfaceB-final
    // L2): the screen judge never sees the change list, because its first text is never the leftmost in the card.
    changesAlign: (() => {
      const firstX = (el) => {
        if (!el) return null;
        const tw = document.createTreeWalker(el, NodeFilter.SHOW_TEXT); let n; let x = null;
        while ((n = tw.nextNode())) {
          if (!n.textContent.trim()) continue;
          const rg = document.createRange(); rg.selectNodeContents(n); const r = rg.getBoundingClientRect();
          if (r.width && (x === null || r.left < x)) x = r.left;
        }
        return x === null ? null : Math.round(x * 10) / 10;
      };
      const open = q('[data-testid="plan-list"] .pw-item-open');
      return open ? { changes: firstX(open.querySelector('.pw-changes')), title: firstX(open.querySelector('.pw-title')) } : null;
    })(),
  };
})()`;

const results = [];
const act = (page, step) => page.evaluate((s) => {
  const el = document.querySelector(`[data-testid="${s}"]`);
  if (!el) return false;
  el.scrollIntoView({ block: 'center' });
  el.click();
  return true;
}, step);
const actIn = (page, row, testId) => page.evaluate((r, t) => {
  const el = document.querySelector(`[data-row="${r}"] [data-testid="${t}"]`);
  if (!el) return false;
  el.click();
  return true;
}, row, testId);

async function scene(name, { mode = 'plain', free = false, steps = [], scrollTo = null, expect = null, tab = 'plan', extra = '', width = 1440 } = {}) {
  if (only && !only.includes(name)) return;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height: 1000, deviceScaleFactor: 1 });
  // The sample's times are UTC (10:46 in the mockup), so the page is read on a UTC clock.
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(mode, free));
  const query = `conn=1&live=1${free ? '' : '&tier=pro'}${tab ? `&tab=${tab}` : ''}${extra}`;
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await wait(1400);
  const notes = [];
  for (const step of steps) {
    let ok;
    if (step.testId) ok = await act(page, step.testId);
    else if (step.tree) ok = await page.evaluate((ref) => { window.__uishotHarness.dispatch({ type: 'treeSelection', sessionId: 'uishot', ref }); return true; }, step.tree);
    else if (step.area) ok = await page.evaluate((t) => {
      const el = [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].find((b) => b.textContent.trim().startsWith(t));
      if (el) el.click();
      return !!el;
    }, step.area);
    // A host navigation message, as a keybinding sends it (Ctrl+Shift+1 is group:plan).
    else if (step.nav) ok = await page.evaluate((tab) => { window.__uishotHarness.dispatch({ type: 'navigate', sessionId: 'uishot', tab }); return true; }, step.nav);
    else if (step.press) ok = await page.evaluate((t) => {
      const el = [...document.querySelectorAll('[data-testid="plan-object-actions"] button')].find((b) => b.textContent.trim() === t);
      if (el) el.click();
      return !!el;
    }, step.press);
    else if (step.row) ok = await actIn(page, step.row, step.click);
    else if (step.typeKeys) {
      const target = `[data-testid="${step.typeKeys}"]`;
      ok = !!(await page.$(target));
      if (ok) { await page.focus(target); await page.keyboard.type(step.value, { delay: 8 }); }
    } else if (step.enter) {
      const target = `[data-testid="${step.enter}"]`;
      ok = !!(await page.$(target));
      if (ok) { await page.focus(target); await page.keyboard.press('Enter'); }
    } else if (step.click) {
      ok = await page.evaluate((sel) => { const el = document.querySelector(sel); if (el) el.click(); return !!el; }, step.click);
    } else if (step.shiftEnter) {
      // N15: Shift+Enter in the field is a new line, never an add.
      const target = `[data-testid="${step.shiftEnter}"]`;
      ok = !!(await page.$(target));
      if (ok) { await page.focus(target); await page.keyboard.down('Shift'); await page.keyboard.press('Enter'); await page.keyboard.up('Shift'); }
    } else if (step.pointerOnMark) {
      // The pointer rests on the marked row, where a person's hand is on its way to the row's Approve.
      const a = await page.evaluate(() => window.__anchor);
      ok = !!a;
      if (ok) await page.mouse.move(a.x, a.y);
    } else if (step.eval) {
      // Page code a scene runs itself (a live update pushed from "the engine", a measurement). Returns true when done.
      ok = await page.evaluate(step.eval);
    } else ok = false;
    if (!ok) notes.push(`step not found: ${JSON.stringify(step)}`);
    await wait(step.settle ?? 600);
  }
  await checkScreen(page, `plan-drive:${name}`);
  await page.setViewport({ width, height: 1000, deviceScaleFactor: 1 });
  await wait(300);
  if (scrollTo) {
    const found = await page.evaluate((sel) => {
      const el = document.querySelector(`[data-testid="${sel}"]`);
      if (!el) return false;
      el.scrollIntoView({ block: 'center' });
      return true;
    }, scrollTo);
    if (!found) notes.push(`scroll target not found: ${scrollTo}`);
    await wait(300);
  }
  const measured = await page.evaluate(MEASURE);
  const shots = {};
  for (const theme of Object.keys(THEMES)) {
    await applyTheme(page, theme); await wait(300);
    const file = join(OUT, `${name}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false });
    shots[theme] = file;
  }
  if (expect) for (const failure of expect(measured)) notes.push(failure);
  if (measured.docWidth > measured.viewportWidth + 1) notes.push(`the page scrolls sideways: ${measured.docWidth} > ${measured.viewportWidth}`);
  results.push({ scene: name, mode, free, measured, shots, notes, errors });
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${name}${notes.length ? ': ' + notes.join(' | ') : ''}${errors.length ? ' ERR ' + errors.slice(0, 2).join(' | ') : ''}`);
  await page.close();
}

const has = (cond, message) => (cond ? [] : [message]);
const row = (m, id) => m.rows.find((r) => r.id === id);
const callsOf = (m, name) => m.planCalls.filter((c) => c.call === name);

// ---- section 2: the page -----------------------------------------------------------------------------
await scene('page', {
  expect: (m) => [
    ...has(m.grade === 'AI readiness B now to A when the plan is done', `grade line "${m.grade}"`),
    ...has(m.bestPractice === 'Best practice C', `best practice "${m.bestPractice}"`),
    ...has(/^Latest\s*10:46\s*Semanticus hid 3 Promotion key columns\s*History$/.test(m.latest), `latest "${m.latest}"`),
    ...has(JSON.stringify(m.tracks.map((t) => t.counts)) === JSON.stringify(['5 done1 on it3 ready first1 follows it', '2 done0 on it2 need you', '4 done1 on it2 ready2 waiting']), `track counts ${JSON.stringify(m.tracks.map((t) => t.counts))}`),
    ...has(m.field === 'Describe a task or an aim', `field placeholder ${m.field}`),
    // Round 1: the rows only you can do are a closed fold, last; the Next list is Semanticus rows then the assistant's.
    ...has(m.fold === '2 tasks need your choice' && JSON.stringify(m.foldRows) === '[]', `fold "${m.fold}" with rows ${JSON.stringify(m.foldRows)}`),
    ...has(m.rows.length === 9 && m.rows.every((r) => r.doer !== 'you'), `${m.rows.length} rows, ${m.rows.map((r) => r.doer)}`),
    ...has(m.showAll === 'Show all 10', `show all "${m.showAll}"`),
    ...has(m.run === 'Run the plan 4', `run "${m.run}"`),
    ...has(JSON.stringify(m.queue) === '["On"]', `switch ${JSON.stringify(m.queue)}`),
    // On Pro a Ready Semanticus row is run by Run the plan: it has no button of its own (Approve is the free tier's).
    ...has(row(m, 'safefix/Visibility')?.actions.length === 0, `Hide keys actions ${row(m, 'safefix/Visibility')?.actions}`),
    ...has(row(m, 'pt-7c01d4e9')?.actions.length === 0, 'the On it run shows no button'),
    ...has(m.rows.every((r) => !r.actions.some((a) => /^(Pause|Resume|Choose|Answer|Up|Could not)/.test(a))), 'a removed control is still on a row'),
    ...has(row(m, 'timeintel/measure:Sales/Sales Amount')?.sub === 'Waits for: Mark Date as the date table', 'Waits for names the row'),
  ],
});
await scene('waiting-open', { steps: [{ testId: 'plan-row-open-timeintel/measure:Sales/Sales Amount', settle: 900 }],
  scrollTo: 'plan-detail-timeintel/measure:Sales/Sales Amount', expect: (m) => [
    ...has(/Finish these tasks first/.test(m.detail), `dependency detail "${m.detail}"`),
  ] });
await scene('page-show-all', { steps: [{ testId: 'plan-show-all' }], expect: (m) => has(m.rows.length === 10, `${m.rows.length} rows after Show all`) });
await scene('strip-filtered', {
  steps: [{ testId: 'plan-track-semanticus' }],
  expect: (m) => [
    ...has(m.track === 'semanticus', `track ${m.track}`),
    ...has(m.rows.every((r) => r.doer === 'semanticus') && m.rows.length === 5, `rows ${m.rows.map((r) => r.doer)}`),
    // The tour's one read of History on load (lane U4) is not the filter's.
    ...has(m.planCalls.filter((c) => !/^(getPlanHistory|listVerifiedEdits|listHistoryCheckpoints)$/.test(c.call)).length === 0, 'filtering a track makes no call'),
  ],
});
// ---- Refresh plan (Kane, 11 Oct): before and after, once a run has finished rows, on the Semanticus track that lists them ----
await scene('refresh-before', {
  steps: [{ testId: 'plan-run', settle: 900 }, { testId: 'plan-track-semanticus', settle: 900 }],
  expect: (m) => [
    ...has(m.refreshButton === 'Refresh plan', `button "${m.refreshButton}"`),
    ...has(m.rows.some((r) => r.state === 'done'), `no done rows before: ${m.rows.map((r) => r.state)}`),
  ],
});
await scene('refresh-after', {
  steps: [{ testId: 'plan-run', settle: 900 }, { testId: 'plan-track-semanticus', settle: 900 }, { testId: 'plan-refresh', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'refreshPlan').length === 1, `refresh calls ${JSON.stringify(callsOf(m, 'refreshPlan'))}`),
    ...has(/^1 new task, \d+ done tasks? cleared\.$/.test(m.said), `note "${m.said}"`),
    ...has(!m.rows.some((r) => r.state === 'done'), `done rows left: ${m.rows.filter((r) => r.state === 'done').map((r) => r.id)}`),
    ...has(m.rows.some((r) => r.id === 'safefix/Store'), `new row missing: ${m.rows.map((r) => r.id)}`),
  ],
});
await scene('latest-opens-history', { steps: [{ testId: 'plan-latest' }], expect: (m) => has(m.view === 'hist', `view ${m.view}`) });

// ---- section 3: a row opened ---------------------------------------------------------------------------
await scene('row-open', {
  steps: [{ testId: 'plan-row-open-jev.describe.table:Product' }], scrollTo: 'plan-detail-jev.describe.table:Product',
  // The opened row is for reading: no control rows (the doer menu, the page link and the dots are on the row itself).
  expect: (m) => [
    ...['Why', 'Done when', 'Objects', 'Your note'].flatMap((w) => has(m.detail.includes(w), `detail lacks "${w}"`)),
    ...['Who does it', 'Give to your assistant', "I'll do it", 'Let it run', 'Where it is done', 'Queue', 'Steps', 'Repeats'].flatMap((w) => has(!m.detail.includes(w), `detail still says "${w}"`)),
  ],
});
// A Jev row with a long change set, opened and scrolled: the Studio itself must not grow a second scroll, because
// that scrollbar narrows the header and folds the areas into More (m11 M4 lane n: the nav vanished).
await scene('row-open-lines-keeps-nav', {
  mode: 'lines', steps: [{ testId: 'plan-row-open-jev.describe.table:Product', settle: 900 }], scrollTo: 'plan-detail-jev.describe.table:Product',
  expect: (m) => [
    ...has(m.docHeight <= m.viewportHeight + 1, `the Studio scrolls as a whole: ${m.docHeight} > ${m.viewportHeight}`),
    ...has(m.headerFold !== '4', 'the areas folded into More'),
  ],
});
// A Jev row that carries a proved line and two SUGGESTIONS (a ratio, and a count over blank spaces): the suggestions print the
// plain note as words on the row, and carry no check mark and no "sure" (the format fill rule, addendum 2 item 11).
await scene('row-open-suggestion', {
  mode: 'suggest', steps: [{ testId: 'plan-row-open-jev.describe.table:Product', settle: 900 }], scrollTo: 'plan-detail-jev.describe.table:Product',
  expect: (m) => {
    return [
      ...has(m.suggestionNotes.length === 2 && m.suggestionNotes.every((n) => n === 'Jev suggests this format; check it fits before you run it.'), `the note is not on both suggestion rows as text: ${JSON.stringify(m.suggestionNotes)}`),
      ...has(m.suggestionRows === 2, `${m.suggestionRows} suggestion rows`),
      ...has(m.suggestionRowsWithTick === 0 && m.suggestionRowsWithWord === 0, 'a suggestion row carries a check mark or a word'),
      ...has(m.provedRowsWithTick === 1 && m.provedRowsWithWord === 1, 'the proved row lost its check mark or its word'),
    ];
  },
});
// A line somebody wrote over, held after a new plan or a redraw (m11 join part 5): the words and the engine's reason stay visible on the row,
// unticked, with no check mark and no confidence word.
await scene('row-open-held-line', {
  mode: 'held', steps: [{ testId: 'plan-row-open-jev.describe.table:Product', settle: 900 }], scrollTo: 'plan-detail-jev.describe.table:Product',
  expect: (m) => [
    ...has(m.openRow.includes('Money in, as finance counts it.'), 'the held words are not on the row'),
    ...has(m.openRow.includes('Your words are kept here from the plan before.'), 'the held line does not say why it is held'),
    ...has(m.provedRowsWithTick === 0 && m.provedRowsWithWord === 0, 'a held line carries a check mark or a confidence word'),
  ],
});
// Widths where this sample's header (it carries a run chip and a pending count) holds the five areas once the brand
// word folds, and did not before: both folded the areas into More (m11 M4, where a 792px Studio lost its nav to a
// status dot on the assistant chip). Narrower than about 820 this sample's header genuinely has no room for them.
for (const width of [824, 840]) {
  await scene(`header-keeps-areas-${width}`, { width, expect: (m) => has(m.headerFold !== '4', `the areas folded into More at ${width}px`) });
}
// The cleanup is a suggestion list for a lean model (round 5 of m11/plan-harm; Kane, 30 Sep 2026). The row says what it is for and that the
// person reviews it, and every unused column is a line: three arrive ticked as suggestions, three arrive unticked, and each has its reason.
// The row can be set aside whole (the Set aside control) or customised line by line. This scene is the one to read: it opens the row.
await scene('cleanup-review', {
  mode: 'cleanup', steps: [{ testId: 'plan-only-you-toggle' }, { testId: 'plan-row-open-cleanup/model', settle: 900 }],
  scrollTo: 'plan-keep-all-0',
  expect: (m) => [
    ...has(row(m, 'cleanup/model')?.actions.includes('Approve'), `cleanup actions ${row(m, 'cleanup/model')?.actions}`),
    ...has(callsOf(m, 'setPlanItem').length === 0, 'looking at the list decided nothing'),
  ],
});
// The lines that start UNTICKED, each with its reason: Jev's one ("Not ticked: Jev would leave it out ..."), a date and a key.
await scene('cleanup-unticked', {
  mode: 'cleanup', steps: [{ testId: 'plan-only-you-toggle' }, { testId: 'plan-row-open-cleanup/model', settle: 900 }],
  scrollTo: 'plan-reason-0-CLEANUP-UNUSED/column:Sales/BilledOn#object',
  expect: (m) => [
    ...has(callsOf(m, 'setPlanItem').length === 0, 'looking at the unticked lines decided nothing'),
  ],
});
// Keep all and Drop all on the cleanup's change list (Kane's standing rule: one click stays one click). Three lines arrive ticked and three
// unticked; one press keeps the three that are not, and Approve then takes the kept set in one step.
await scene('cleanup-keep-all', {
  mode: 'cleanup', steps: [{ testId: 'plan-only-you-toggle' }, { testId: 'plan-row-open-cleanup/model', settle: 900 }, { testId: 'plan-keep-all-0', settle: 1200 }],
  scrollTo: 'plan-keep-all-0',
  expect: (m) => [
    ...has(callsOf(m, 'setPlanItem').length === 3 && callsOf(m, 'setPlanItem').every((c) => c.params[2] === true),
      `keep calls ${JSON.stringify(callsOf(m, 'setPlanItem'))}`),
    ...has(row(m, 'cleanup/model')?.actions.includes('Approve'), `cleanup actions ${row(m, 'cleanup/model')?.actions}`),
    // The change list starts level with the row's title (v-surfaceB-final L2).
    ...has(m.changesAlign && m.changesAlign.changes !== null && Math.abs(m.changesAlign.changes - m.changesAlign.title) < 1,
      `the change list starts at ${m.changesAlign?.changes}, the row title at ${m.changesAlign?.title}`),
  ],
});
await scene('cleanup-drop-all', {
  mode: 'cleanup', steps: [{ testId: 'plan-only-you-toggle' }, { testId: 'plan-row-open-cleanup/model', settle: 900 }, { testId: 'plan-drop-all-0', settle: 1200 }],
  expect: (m) => [
    ...has(callsOf(m, 'setPlanItem').filter((c) => c.params[2] === false).length === 3, `drop calls ${JSON.stringify(callsOf(m, 'setPlanItem'))}`),
    ...has(row(m, 'cleanup/model')?.actions.includes('Approve (off)'), `cleanup actions ${row(m, 'cleanup/model')?.actions}`),
  ],
});
// The doer on the row is the menu: Semanticus, Your assistant, Me. Me on a found row sets the doer to you, and the row moves to the fold.
await scene('row-doer-switch', {
  steps: [{ row: 'jev.describe.table:Product', click: 'plan-doer-btn' }, { testId: 'plan-doer-opt-you' }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'setPlanItemDoer').map((c) => c.params)) === JSON.stringify([['jev.describe.table:Product', 'you']]), `doer calls ${JSON.stringify(callsOf(m, 'setPlanItemDoer'))}`),
    ...has(!row(m, 'jev.describe.table:Product') && m.foldCount === 3, `the row now sits in the fold (${m.fold})`),
  ],
});
await scene('row-doer-menu', {
  steps: [{ row: 'jev.describe.table:Product', click: 'plan-doer-btn' }],
  expect: (m) => [
    ...has(m.doerMenu.length === 3 && /^Semanticus/.test(m.doerMenu[0].text) && /^Your assistant/.test(m.doerMenu[1].text) && /^Me/.test(m.doerMenu[2].text), `menu ${JSON.stringify(m.doerMenu)}`),
    ...has(m.doerMenu[1]?.checked === true && !m.doerMenu[1]?.off && !m.doerMenu[2]?.off, 'the current doer is marked and Your assistant and Me are open'),
  ],
});
// Pause and Resume are gone from the row (Me or Set aside does the same job): their scene went with them.

// ---- the field -----------------------------------------------------------------------------------------
await scene('field-add', {
  steps: [{ typeKeys: 'plan-field', value: 'Add a Product hierarchy', settle: 1200 }, { enter: 'plan-field', settle: 900 }],
  expect: (m) => [
    ...has(m.onPlan && m.view === 'work', 'Enter left the Plan'),
    ...has(callsOf(m, 'addPlanTask').length === 1 && callsOf(m, 'addPlanTask')[0].params[0].words === 'Add a Product hierarchy', `add calls ${JSON.stringify(callsOf(m, 'addPlanTask'))}`),
    ...has(m.fieldValue === '', 'the field clears'),
    ...has(!!m.detail && m.detail.includes('Your note'), 'the new row opens'),
  ],
});
// UAT S12-1-005: a long paste is never cut. The field says what happens before Enter, the whole text goes to the
// engine, the row's title is its start, and the detail shows every word in a box that scrolls.
const LONG_PASTE = Array.from({ length: 120 }, (_, i) => `word${i + 1}`).join(' ');
await scene('field-long', {
  steps: [{ typeKeys: 'plan-field', value: LONG_PASTE, settle: 1200 }, { enter: 'plan-field', settle: 900 }],
  scrollTo: 'plan-detail-fulltext',
  expect: (m) => [
    ...has(callsOf(m, 'addPlanTask').length === 1 && callsOf(m, 'addPlanTask')[0].params[0].words === LONG_PASTE, 'the whole text goes to the engine'),
    ...has(!!m.detail && m.detail.includes('word120'), 'the detail shows every word'),
  ],
});
// N15: the field is a textarea that keeps the person's lines. Shift+Enter starts a line and adds nothing; the field
// grows to show both lines; Enter sends the words with the break in them.
await scene('field-lines-typing', {
  steps: [{ typeKeys: 'plan-field', value: 'Add a Store hierarchy', settle: 600 }, { shiftEnter: 'plan-field', settle: 400 },
    { typeKeys: 'plan-field', value: 'Country, then City, then Store', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'addPlanTask').length === 0, 'Shift+Enter added the task'),
    ...has(m.fieldValue === 'Add a Store hierarchy\nCountry, then City, then Store', `the field holds ${JSON.stringify(m.fieldValue)}`),
    ...has(m.fieldTag === 'TEXTAREA', `the field is a ${m.fieldTag}`),
    ...has(m.fieldHeight > m.fieldOneLine, `the field did not grow for a second line (${m.fieldHeight} <= ${m.fieldOneLine})`),
  ],
});
await scene('field-lines', {
  steps: [{ typeKeys: 'plan-field', value: 'Add a Store hierarchy', settle: 600 }, { shiftEnter: 'plan-field', settle: 400 },
    { typeKeys: 'plan-field', value: 'Country, then City, then Store', settle: 900 }, { enter: 'plan-field', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'addPlanTask').length === 1
      && callsOf(m, 'addPlanTask')[0].params[0].words === 'Add a Store hierarchy\nCountry, then City, then Store',
      `add calls ${JSON.stringify(callsOf(m, 'addPlanTask'))}`),
    ...has(m.fieldValue === '', 'the field clears'),
    ...has(m.fieldHeight <= m.fieldOneLine, `the field did not shrink back to one line (${m.fieldHeight} > ${m.fieldOneLine})`),
  ],
});
// N4: Pending changes' notes, read beside a Plan whose rows carry proposed change sets. The engine files every change
// line under the Plan item it serves (LocalEngine.Plan.cs), so the notes must not call that later work.
await scene('pending-notes-beside-sets', {
  mode: 'lines', tab: 'optimize', steps: [{ click: 'button[aria-label="Page notes"]', settle: 700 }],
  expect: (m) => [
    ...has(!!m.pageNotes, 'the Pending changes notes did not open'),
    ...has(!/not done yet|will become items/.test(m.pageNotes ?? ''), `the notes still say sets are not items: ${m.pageNotes}`),
    ...has(/under the item it belongs to/.test(m.pageNotes ?? ''), `the notes do not say where a change sits: ${m.pageNotes}`),
  ],
});
await scene('field-long-hint', {
  steps: [{ typeKeys: 'plan-field', value: LONG_PASTE, settle: 1200 }],
  expect: (m) => has(/whole text is kept/u.test(m.fieldHint ?? ''), `hint "${m.fieldHint}"`),
});
await scene('field-suggest', {
  steps: [{ typeKeys: 'plan-field', value: 'Add a Store hierarchy', settle: 1500 }],
  expect: (m) => [
    ...has(/Diagram/.test(m.suggest), `suggestion "${m.suggest}"`),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'typing adds nothing'),
  ],
});

// fix-askdata-r5 (V1): words no page placed are a task in this field, so the engine's no-match line is not shown under
// it ("Try a page name" is advice for a different box). The field still asked, and Enter still adds the row.
await scene('field-task-stays-quiet', {
  steps: [{ typeKeys: 'plan-field', value: 'Give Net Sales a currency format', settle: 1500 }],
  expect: (m) => [
    ...has(m.suggest === '', `the field shows "${m.suggest}" under a task nothing placed`),
    ...has(callsOf(m, 'routeAsk').length >= 1, 'the field asked where the words are done'),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'typing adds nothing'),
  ],
});

// fix-askdata-r6 (coordinator, 28 September): a line that can be read two ways shows BOTH choices. The task reading
// is first here and says exactly what the row Enter adds says, and Ask your data is the named second choice with a way
// through to it. Before this the field read "Ask your data is the place for that." with no task choice at all, over a
// row that said Semanticus knew no page (verify-askdata-r5 F1).
await scene('field-two-readings-task-first', {
  steps: [{ typeKeys: 'plan-field', value: 'Totals per region are double counting', settle: 1500 }],
  expect: (m) => [
    ...has(/Semanticus does not know a page for this one\./.test(m.suggest), `suggestion "${m.suggest}"`),
    ...has(/Or ask it as a question\./.test(m.suggest), 'Ask your data is named as the second choice'),
    ...has(!/Ask your data is the place for that/.test(m.suggest), 'and never as the answer'),
    ...has(/Open Ask your data/.test(m.suggest), 'with a way through to it'),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'typing adds nothing'),
  ],
});
// The other way round: a line that plainly asks reads as a question first, and keeping it as a task is still named.
await scene('field-two-readings-question-first', {
  steps: [{ typeKeys: 'plan-field', value: 'please show me net sales by region', settle: 1500 }],
  expect: (m) => [
    ...has(/Ask your data is the place for that\./.test(m.suggest), `suggestion "${m.suggest}"`),
    ...has(/Keep it as a task\./.test(m.suggest), 'keeping it as a task is still named'),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'typing adds nothing'),
  ],
});

// fix-askdata-final (verify-askdata-r6 L2): Open Ask your data puts the typed words IN that page's question box. The
// hand-over was proved only by reading App.tsx and askdata.tsx for the right lines, so breaking the wiring and leaving
// those lines in place failed nothing (mutants JC and JD). Here the button is pressed and the box is read. The words
// were typed across a line break (Shift+Enter), and the one-line box gets them on one line.
await scene('field-open-ask-carries-words', {
  steps: [
    { typeKeys: 'plan-field', value: 'Totals  per region', settle: 100 }, { shiftEnter: 'plan-field', settle: 100 },
    { typeKeys: 'plan-field', value: 'are double counting', settle: 1500 },
    { click: '[data-testid="ask-other-jump"]', settle: 1500 },
  ],
  expect: (m) => [
    ...has(m.onAskData, 'Open Ask your data did not open Ask your data'),
    ...has(m.askWords === 'Totals per region are double counting', `the question box holds "${m.askWords}"`),
    // The field asks about the very words Enter adds (fieldSubmit's), so the row can keep the page the field showed.
    ...has(callsOf(m, 'routeAsk').some((c) => c.params[0] === 'Totals per region' + String.fromCharCode(10) + 'are double counting'),
      `routeAsk was sent ${JSON.stringify(callsOf(m, 'routeAsk').map((c) => c.params[0]))}`),
    ...has(callsOf(m, 'addPlanTask').length === 0, 'opening a page adds nothing'),
  ],
});
// fix-askdata-final (verify-askdata-r6 F1): Enter sends the page the field showed, so the row names it. A line the
// service placed on a page carries that page; a line whose task reading names none carries none.
await scene('field-enter-keeps-the-page', {
  steps: [{ typeKeys: 'plan-field', value: 'Add a Store hierarchy', settle: 1500 }, { enter: 'plan-field', settle: 900 },
    { typeKeys: 'plan-field', value: 'Totals per region are double counting', settle: 1500 }, { enter: 'plan-field', settle: 900 }],
  expect: (m) => {
    const sent = callsOf(m, 'addPlanTask').map((c) => c.params[0]);
    return [
      ...has(sent.length === 2, `${sent.length} addPlanTask calls`),
      ...has(sent[0]?.words === 'Add a Store hierarchy' && sent[0]?.fieldPage === 'diagram', `first add sent ${JSON.stringify(sent[0])}`),
      ...has(sent[1]?.words === 'Totals per region are double counting' && !('fieldPage' in (sent[1] ?? {})), `second add sent ${JSON.stringify(sent[1])}`),
    ];
  },
});
// verify-askdata-r6 L4: a line that only names the page asks for the page, not a question on it, so Ask your data opens
// with an empty box instead of holding "go to ask your data".
await scene('field-open-ask-page-name-only', {
  steps: [{ typeKeys: 'plan-field', value: 'go to ask your data', settle: 1500 }, { click: '[data-testid="ask-jump"]', settle: 1500 }],
  expect: (m) => [
    ...has(m.onAskData, 'Open Ask your data did not open Ask your data'),
    ...has(m.askWords === '', `the question box holds "${m.askWords}"`),
  ],
});
// verify-askdata-r6 L10: the answer is for the words it answered. Typing on hides it at once, instead of leaving the
// older answer up, with a button already carrying the newer words, until the wait and the call finish. The mock never
// answers the newer words, so what is on screen here is only ever what the older answer left behind.
await scene('field-answer-goes-while-typing', {
  steps: [{ typeKeys: 'plan-field', value: 'Totals per region are double counting', settle: 1500 },
    { typeKeys: 'plan-field', value: ' in March', settle: 150 }],
  expect: (m) => [
    ...has(m.suggest === '', `the field still shows "${m.suggest}" for the older words`),
  ],
});

// ---- section 5: the assistant's track and the switch --------------------------------------------------
await scene('queue-on', {
  mode: 'queue', steps: [{ testId: 'plan-track-assistant' }],
  expect: (m) => [
    ...has(m.sees === FOLD_ON, `what the assistant sees differs from the engine fold line:\n${m.sees}`),
    ...has(JSON.stringify(m.rows.map((r) => r.id)) === JSON.stringify(['pt-a41b6f02', 'pt-3b7d0c21', 'pt-9e2f7a10', 'pt-0d6e33b8', 'pt-c4e1a9f3']), `order ${m.rows.map((r) => r.id)}`),
    ...has(row(m, 'pt-3b7d0c21')?.actions.length === 0, `first task actions ${row(m, 'pt-3b7d0c21')?.actions}`),
    ...has(row(m, 'pt-3b7d0c21')?.title.startsWith('1') && row(m, 'pt-9e2f7a10')?.title.startsWith('2'), 'the offer is numbered as the fold numbers it'),
    ...has(row(m, 'pt-c4e1a9f3')?.meta === 'Not offered yet', 'the waiting task says Not offered yet'),
    ...has(m.queueLine === 'Your assistant gets these when it next checks the Plan', `queue line "${m.queueLine}"`),
    ...has(row(m, 'pt-0d6e33b8')?.actions.length === 0, `paused task actions ${row(m, 'pt-0d6e33b8')?.actions}`),
  ],
});
await scene('queue-paused', {
  mode: 'queue', steps: [{ testId: 'plan-track-assistant' }, { testId: 'plan-queue-paused' }],
  expect: (m) => [
    ...has(m.sees === FOLD_PAUSED, `paused text differs:\n${m.sees}`),
    ...has(JSON.stringify(callsOf(m, 'setAssistantQueue').map((c) => c.params)) === '[["paused"]]', 'one switch call'),
    ...has(JSON.stringify(m.queue) === '["Paused"]', `switch ${m.queue}`),
    ...has(row(m, 'pt-a41b6f02')?.meta === 'Finishes, even when paused', 'the claimed task finishes, even when paused'),
    ...has(m.queueLine === 'Paused by you. Your assistant gets no new tasks.', `queue line "${m.queueLine}"`),
  ],
});
await scene('queue-up', {
  mode: 'queue', steps: [{ testId: 'plan-track-assistant' }, { row: 'pt-9e2f7a10', click: 'plan-row-more' }, { testId: 'plan-menu-up' }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'movePlanTaskUp').map((c) => c.params)) === '[["pt-9e2f7a10"]]', 'one Up call'),
    ...has(/^Plan: 2 tasks are ready for you\.[^\n]*\n1\. Describe 8 Product measures/.test(m.sees ?? ''), `the fold follows the order: ${m.sees}`),
  ],
});
await scene('queue-take', {
  // Taken by you, the task leaves the assistant's track, so this is read on the whole list.
  mode: 'queue', steps: [{ testId: 'plan-show-all' }, { row: 'pt-3b7d0c21', click: 'plan-doer-btn' }, { testId: 'plan-doer-opt-you' }, { testId: 'plan-only-you-toggle' }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'claimPlanTask').map((c) => c.params)) === '[["pt-3b7d0c21"]]', 'Me on an assistant task is one claim call'),
    ...has(/^Mark done$/.test(row(m, 'pt-3b7d0c21')?.actions.join(',') ?? ''), `the taken task offers Mark done (${row(m, 'pt-3b7d0c21')?.actions})`),
  ],
});

// ---- Remove, with Undo (Kane's UAT on build ee57ba84, 25 Sep: "no way to delete a task once you add it") ----------
await scene('remove-menu', {
  steps: [{ testId: 'plan-only-you-toggle' }, { row: 'pt-3f9a2c11', click: 'plan-row-more' }],
  expect: (m) => [
    ...has(row(m, 'pt-3f9a2c11')?.more === true, 'the task has no row menu'),
    ...has(JSON.stringify(m.menu) === '["Repeat…","Draft steps","Set aside","Remove"]', `row menu ${JSON.stringify(m.menu)}`),
    ...has(row(m, 'safefix/Visibility')?.more === true, 'a found row has its dots'),
  ],
});
await scene('remove-task', {
  steps: [{ testId: 'plan-only-you-toggle' }, { row: 'pt-3f9a2c11', click: 'plan-row-more' }, { testId: 'plan-menu-remove', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'removePlanRow').map((c) => c.params)) === '[["pt-3f9a2c11"]]', `remove calls ${JSON.stringify(callsOf(m, 'removePlanRow'))}`),
    ...has(!m.rows.some((r) => r.id === 'pt-3f9a2c11') && !m.foldRows.includes('pt-3f9a2c11'), 'the removed task is still on the plan'),
    ...has(/^Removed "Choose when the fiscal year starts" from the Plan\.\s*Undo$/.test(m.toast), `toast "${m.toast}"`),
  ],
});
await scene('remove-undo', {
  steps: [{ testId: 'plan-only-you-toggle' }, { row: 'pt-3f9a2c11', click: 'plan-row-more' }, { testId: 'plan-menu-remove', settle: 900 }, { testId: 'plan-toast-undo', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'restorePlanRow').map((c) => c.params)) === '[["pt-3f9a2c11"]]', `restore calls ${JSON.stringify(callsOf(m, 'restorePlanRow'))}`),
    ...has(m.foldRows.includes('pt-3f9a2c11'), 'Undo did not put the task back'),
    ...has(m.toast === '', `the toast stayed after Undo: "${m.toast}"`),
  ],
});
await scene('remove-claimed', {
  mode: 'queue', steps: [{ testId: 'plan-track-assistant' }, { row: 'pt-a41b6f02', click: 'plan-row-more' }, { testId: 'plan-menu-remove', settle: 900 }],
  expect: (m) => [
    ...has(JSON.stringify(callsOf(m, 'removePlanRow').map((c) => c.params)) === '[["pt-a41b6f02"]]', `remove calls ${JSON.stringify(callsOf(m, 'removePlanRow'))}`),
    ...has(!m.rows.some((r) => r.id === 'pt-a41b6f02'), 'the claimed task is still on the plan'),
    ...has(/Your assistant was on it and reads this on its next call\./.test(m.toast), `toast "${m.toast}"`),
  ],
});
// Remove is in the dots only: the detail has no Queue group, so its second copy and its scene are gone.

// ---- section 6: Run the plan, then Now ready ----------------------------------------------------------
await scene('run', {
  steps: [{ testId: 'plan-run', settle: 900 }],
  expect: (m) => {
    const calls = callsOf(m, 'applyPlanItems');
    return [
      // This mock answers one wave a call, so one press makes the second call on the digest of the first answer (an engine that runs every wave in one call needs only the first).
      ...has(calls.length === 2, `${calls.length} applyPlanItems calls, expected two waves`),
      ...has(calls[0] && JSON.stringify(calls[0].params.slice(0, 2)) === '[[],"human"]' && /^rd-/.test(calls[0].params[2]), `run args ${JSON.stringify(calls[0]?.params)}`),
      // The time measures waited only on the date table, which the press runs, so they are in the same press and Done.
      ...has(/Ran 4 tasks/.test(m.runReceipt), `the press ran the time measures too (${m.runReceipt})`),
      ...has(/^Ran \d+ tasks?\./.test(m.runReceipt), `receipt "${m.runReceipt}"`),
    ];
  },
});

// Run the plan, then pick a track with finished rows: the filtered list grows a Done group whose header must sit on
// the list's padding like every other header (v-surfaceB-final L1). The screen judge checks it.
await scene('done-group', {
  steps: [{ testId: 'plan-run', settle: 900 }, { testId: 'plan-track-semanticus', settle: 900 }],
  expect: (m) => has(m.track === 'semanticus', `track ${m.track}`),
});

// ---- free ----------------------------------------------------------------------------------------------
await scene('free', {
  free: true,
  expect: (m) => [
    ...has(!m.run, 'no Run the plan button on free'),
    ...has(/Pro runs the plan for you/.test(m.proLine), `pro line "${m.proLine}"`),
    ...has(!m.rows.some((r) => /^Jev/.test(r.source)), 'no Jev row on free'),
    ...has(/Updated after your last change/.test(m.facts), `facts "${m.facts}"`),
  ],
});
await scene('empty', { mode: 'empty', expect: (m) => has(m.rows.length === 0, 'rows on an empty plan') });
// The best-practice build rows (lane m11/plan-rows): the engine's own two rows at the top, and an empty model's starts.
await scene('bpa-rows', { mode: 'bparows', expect: (m) => [
  ...has(m.rows.some((r) => r.id === 'bpafix/model'), 'the best-practice row'),
  ...has(m.rows.some((r) => r.id === 'words/model'), 'the words row'),
] });
await scene('bpa-row-open', { mode: 'bparows', steps: [{ testId: 'plan-row-open-bpafix/model', settle: 900 }], expect: () => [] });
await scene('words-row-open', { mode: 'bparows', steps: [{ testId: 'plan-row-open-words/model', settle: 900 }], expect: () => [] });
// Item 6: the collisions row's Choose opens Find and replace with the clashing names filled in, as one pattern, not empty.
await scene('collisions-choose', {
  mode: 'collisions', steps: [{ testId: 'plan-only-you-toggle' }, { testId: 'plan-row-page', settle: 1500 }], tab: 'plan',
  expect: (m) => [
    ...has(m.findBox === '(?:Group1Key|Group1 Key|PRAC_CD)', `Find box reads ${JSON.stringify(m.findBox)}`),
    ...has(m.findRegexOn === true, 'the Regular expression switch is not on'),
  ],
});
await scene('starts', { mode: 'starts', expect: (m) => [
  ...has(m.rows.length === 0, 'rows on an empty model'),
  ...has(m.starts.join(',') === 'plan-start-sql,plan-start-blank', `the two starts (${m.starts.join(',')})`),
  ...has(!m.empty, '"Nothing to do on the plan" beside the starts'),
] });
// A model with no tables and a row open: the starts still show above it (verify, 28 Sep, H3).
await scene('starts-open', { mode: 'starts-open', expect: (m) => [
  ...has(m.rows.length + m.foldCount === 1, `one open row (${m.rows.length} listed, ${m.foldCount} in the fold)`),
  ...has(m.starts.join(',') === 'plan-start-sql,plan-start-blank', `the two starts beside an open row (${m.starts.join(',')})`),
] });

// ---- the Overview's jobs, now on the Plan (slice 11, lane R1; contract 11.1 and 15) ------------------------------
// The start page: no tab asked for lands on the Plan.
await scene('start-page', { tab: '', expect: (m) => [...has(m.onPlan && m.view === 'work', `landed on ${m.onPlan ? m.view : 'something else'}`)] });
// The old Overview id and area word still land, on the Plan.
await scene('alias-modelhome', { tab: 'modelhome', expect: (m) => has(m.onPlan && m.view === 'work', 'modelhome did not land on the Plan') });
await scene('alias-overview-area', { tab: 'group:overview', expect: (m) => has(m.onPlan && m.view === 'work', 'the overview area word did not land on the Plan') });
// Journey m11 F2: from History, the Plan area button (and Ctrl+Shift+1, which More > Plan shares) goes back to Work.
await scene('plan-button-leaves-history', { steps: [{ testId: 'plan-view-hist' }, { area: 'Plan', settle: 900 }], expect: (m) => [
  ...has(m.onPlan && m.view === 'work', `pressing Plan on History left the Plan on ${m.view}`),
] });
await scene('plan-key-leaves-history', { steps: [{ testId: 'plan-view-hist' }, { nav: 'group:plan', settle: 900 }], expect: (m) => [
  ...has(m.onPlan && m.view === 'work', `Ctrl+Shift+1 on History left the Plan on ${m.view}`),
] });
// And the press is not replayed: a Plan opened later by the old Edit History id still lands on History.
await scene('history-after-plan-press', { steps: [{ area: 'Plan', settle: 700 }, { area: 'Explore', settle: 900 }, { nav: 'history', settle: 1200 }], expect: (m) => [
  ...has(m.onPlan && m.view === 'hist', `the old History id landed on ${m.view} after an earlier Plan press`),
] });
// The old Edit History id lands on the Plan's History view.
await scene('alias-history', { tab: 'history', expect: (m) => [
  ...has(m.onPlan && m.view === 'hist', `history landed on view ${m.view}`),
  ...has(m.historyRows > 0, 'the History list drew no rows'),
] });
// A table picked in the model tree: the list says Showing it, and its actions sit on the line. No tab is asked for:
// the start page is the Plan, and a repeated deep link would clear the picked object as a real navigation does.
await scene('object-line', { tab: '', extra: '&initialSelection=table:Sales', expect: (m) => [
  ...has(/^Showing Sales/.test(m.showing), `showing "${m.showing}"`),
  ...has(JSON.stringify(m.objectActions) === JSON.stringify(['Preview data', 'Edit Power Query', 'Show in diagram', 'Lineage', 'Table size', 'Find in this table', 'Add measure', 'History']),
    `object actions ${JSON.stringify(m.objectActions)}`),
] });
// A measure, and its History: the Plan's own History, filtered to that object (contract 11.2).
await scene('object-history', { tab: '', extra: '&initialSelection=measure:Sales/Sales Amount',
  steps: [{ press: 'History' }], expect: (m) => [
    ...has(m.view === 'hist', `view ${m.view} after History`),
    ...has(/Showing changes to Sales Amount/.test(m.historyObject), `history filter "${m.historyObject}"`),
  ] });
// The tree selection is the fallback, as it was on the Overview: pick a table, go elsewhere, press Plan, and the
// object line still shows it (coordinator, slice 11). The area button does not clear it.
// Two ways in: the table picked while on the Plan and then the area left and re-entered, and the table picked in the
// tree while another page is open. The second is the one the route object alone missed.
await scene('object-line-after-area', { tab: '', extra: '&initialSelection=table:Sales',
  steps: [{ area: 'Explore', settle: 900 }, { area: 'Plan', settle: 900 }], expect: (m) => [
    ...has(/^Showing Sales/.test(m.showing), `showing "${m.showing}" after pressing Plan`),
  ] });
await scene('object-line-picked-elsewhere', { tab: '',
  steps: [{ area: 'Explore', settle: 900 }, { tree: 'table:Sales', settle: 600 }, { area: 'Plan', settle: 900 }, { area: 'Plan', settle: 900 }], expect: (m) => [
    ...has(m.onPlan && m.view === 'work', 'the Plan area button did not land on the Plan'),
    ...has(/^Showing Sales/.test(m.showing), `showing "${m.showing}" after pressing Plan`),
    ...has(m.objectActions.includes('Preview data'), `object actions ${JSON.stringify(m.objectActions)}`),
  ] });
// A picked object with no open rows: "Nothing open here." sits on the list's padding (v-surfaceB-final L2). The
// screen judge checks it; no other scene draws the line.
await scene('object-none', { tab: '', extra: '&initialSelection=table:Nowhere', expect: (m) => [
  ...has(/^Showing Nowhere/.test(m.showing), `showing "${m.showing}"`),
  ...has(m.rows.length === 0, `${m.rows.length} rows for a table with none`),
] });
// An empty plan still offers the picked object's actions.
await scene('object-line-empty', { mode: 'empty', tab: '', extra: '&initialSelection=table:Sales', expect: (m) => [
  ...has(m.objectActions.includes('Preview data'), `object actions on an empty plan ${JSON.stringify(m.objectActions)}`),
] });
// Raise compatibility level: shown only while the level blocks a named capability, and it calls the free operation.
await scene('raise-copy', { extra: '&compat=1604', expect: () => [] });
await scene('raise-level', { extra: '&compat=1604', steps: [{ testId: 'plan-raise-level', settle: 900 }], expect: (m) => [
  ...has(JSON.stringify(callsOf(m, 'setCompatibilityLevel').map((c) => c.params)) === '[[1701]]', `raise calls ${JSON.stringify(callsOf(m, 'setCompatibilityLevel'))}`),
] });
// ---- rows stay still under the pointer while the plan updates (UAT lane p, 26 Sep) ------------------------------
// A person reads a row and moves to press its Approve; meanwhile Jev lands, or a batch finishes, and the plan re-reads.
// The rows ABOVE change (one appears, one goes, one opens), and the row under the pointer must not move: on lane p a
// click aimed at Approve landed on another row's Pause. Each scene marks a row in the lower half of the screen, pushes
// an update that changes what is above it, and measures the marked row's top before and after.
const markRow = (pick) => `(() => {
  const rows = [...document.querySelectorAll('[data-testid="plan-list"] [data-row]')];
  const row = rows[(${pick})(rows.length)];
  if (!row) return false;
  row.scrollIntoView({ block: 'center' });
  const box = row.getBoundingClientRect();
  const k = rows.indexOf(row);
  window.__anchor = { id: row.dataset.row, above: k > 0 ? rows[k - 1].dataset.row : null,
    before: Math.round(box.top), x: Math.round(box.left + box.width * 0.8), y: Math.round(box.top + 12) };
  return true;
})()`;
const pushUpdate = (change) => `(() => {
  __plan = (${change})(__plan);
  window.dispatchEvent(new MessageEvent('message', { data: { type: 'planDidChange', payload: {} } }));
  return true;
})()`;
const readMark = `(() => {
  const a = window.__anchor; if (!a) return false;
  const row = document.querySelector('[data-row="' + CSS.escape(a.id) + '"]');
  a.after = row ? Math.round(row.getBoundingClientRect().top) : null;
  return true;
})()`;
const stays = (m) => has(m.anchor && m.anchor.after != null && Math.abs(m.anchor.after - m.anchor.before) <= 1,
  `the marked row moved under the pointer: ${JSON.stringify(m.anchor)}`);
const newRow = (id) => `{ ...p.items.find((r) => r.id === window.__anchor.above), id: '${id}', title: 'A row that arrived above', changeSet: null }`;
// Filler rows BELOW, so the marked row sits mid-page and the list can scroll either way (at the very end of the list
// the browser clamps the scroll, and a removal above would pass for the wrong reason).
const pad = { eval: pushUpdate(`(p) => ({ ...p, items: [...p.items, ...Array.from({ length: 10 }, (_, i) => ({ ...p.items[p.items.length - 1], id: 'pt-f111' + String(i).padStart(4, '0'), title: 'Filler row ' + (i + 1), changeSet: null }))] })`), settle: 900 };
// Each change lands on the row just above the marked one, between the top of the screen and the pointer.
const at = 'p.items.findIndex((r) => r.id === window.__anchor.above)';
const mark = [{ testId: 'plan-show-all' }, pad, { eval: markRow('(n) => n - 8') }, { pointerOnMark: true }];
await scene('update-adds-a-row-above', {
  steps: [...mark, { eval: pushUpdate(`(p) => ({ ...p, items: [...p.items.slice(0, ${at}), ${newRow('safefix/Arrived')}, ...p.items.slice(${at})] })`), settle: 900 },
    { eval: readMark }],
  expect: stays,
});
await scene('update-removes-a-row-above', {
  steps: [...mark, { eval: pushUpdate(`(p) => ({ ...p, items: p.items.filter((r) => r.id !== window.__anchor.above) })`), settle: 900 },
    { eval: readMark }],
  expect: stays,
});
// The row above is open and its lines change (Jev lands with more lines): it grows.
await scene('update-grows-an-open-row-above', {
  steps: [{ testId: 'plan-show-all' }, pad, { eval: markRow('(n) => n - 8') },
    { eval: `(() => { const el = document.querySelector('[data-row="' + CSS.escape(window.__anchor.above) + '"] .pw-title'); if (!el) return false; el.click(); return true; })()`, settle: 700 },
    { eval: markRow('() => -1').replace('const row = rows[(() => -1)(rows.length)];', 'const row = document.querySelector(\'[data-row="\' + CSS.escape(window.__anchor.id) + \'"]\');') }, { pointerOnMark: true },
    { eval: pushUpdate(`(p) => ({ ...p, items: p.items.map((r) => r.id === window.__anchor.above ? { ...r, why: [...(r.why || []), 'A reason that arrived with the update, long enough to take a line of its own in the open row above the pointer.', 'And a second one, so the open row grows by more than a few pixels.'] } : r) })`), settle: 900 },
    { eval: readMark }],
  expect: stays,
});

// ---- verify (round 2, 26 Sep): a control INSIDE the open row stays still too ------------------------------------------
// The fix above holds the TOP of the row under the pointer, else the open row's top. A person reviewing an open row has
// the pointer on its lines (Keep, Drop), and those sit below the row's own reasons, outside the row's [data-row] header.
// When that same open row grows above the pointer (Jev lands with a reason or a line, a batch's progress line comes or
// goes, the agent door keeps or drops a line), the header stays put and the control under the pointer moves. On lane p
// (verify-r2) a Drop moved 16px when the other door dropped a line in the same open row, which added a reason above it.
const markControl = `(() => {
  const keep = document.querySelector('[data-testid^="plan-keep-0-"]');
  if (!keep) return false;
  keep.scrollIntoView({ block: 'center' });
  const box = keep.getBoundingClientRect();
  window.__anchor = { id: keep.dataset.testid, before: Math.round(box.top), x: Math.round(box.left + box.width / 2), y: Math.round(box.top + box.height / 2) };
  return true;
})()`;
const readControl = `(() => {
  const a = window.__anchor; if (!a) return false;
  const el = document.querySelector('[data-testid="' + a.id + '"]');
  a.after = el ? Math.round(el.getBoundingClientRect().top) : null;
  return true;
})()`;
await scene('update-grows-the-open-row-under-the-pointer', {
  mode: 'lines',
  steps: [pad, { testId: 'plan-row-open-jev.describe.table:Product', settle: 900 }, { eval: markControl }, { pointerOnMark: true },
    { eval: pushUpdate(`(p) => ({ ...p, items: p.items.map((r) => r.id === 'jev.describe.table:Product' ? { ...r, why: [...(r.why || []), 'A reason that arrived with the update, long enough to take a line of its own in the open row above the pointer.', 'And a second one, so the open row grows by more than a few pixels.'] } : r) })`), settle: 900 },
    { eval: readControl }],
  expect: stays,
});

await scene('raise-level-shown', { extra: '&compat=1604', expect: (m) => has(/set to level 1604\. Calendars need level 1701\./.test(m.raise), `raise line "${m.raise}"`) });
await scene('raise-level-not-blocking', { extra: '&compat=1702', expect: (m) => has(m.raise === '', `raise line shown at 1702: "${m.raise}"`) });

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
