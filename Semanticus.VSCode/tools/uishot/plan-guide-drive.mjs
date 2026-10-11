// Browser-driven proof of the Plan's first-open guidance (Plan-heart contract 10, lane U4): the six-step tour, the
// What changed card, and Help's way back in, in dark, light and high contrast.
//
// It serves THIS worktree's built bundle and plays the HOST's half itself: the harness answers no guide message, so
// this driver wraps acquireVsCodeApi and keeps the two once-flags in the page's localStorage, which survives a
// reload exactly as context.globalState survives a window reload. Every scene starts in a fresh browser context,
// which is a fresh profile: no flags at all.
//
// THE FINISH LINE it measures: a fresh profile opens the Plan and sees step 1 anchored on the strip; after Done and
// a reload nothing shows; Help's "Show the tour" starts it again.
//
// ANCHORS. The tour looks for data-tour attributes that the Plan's own files carry. Until those files carry them,
// STANDIN=1 marks the nearest parts of the page that exists with them (see STANDIN_ANCHORS), and every shot and
// result from such a run says so.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium [STANDIN=1] [THEMES=dark,light,hc] \
//     node tools/uishot/plan-guide-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'plan-guide');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark,light,hc').split(',');
const STANDIN = process.env.STANDIN === '1';
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Build the webview first (cd webview && npm run build).');
  process.exit(1);
}
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-fixture.mjs'), 'utf8').replace(/^export /gm, '');

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

// The parts of the pre-redesign Plan page that stand in for the new one's anchors. The queue switch has no
// stand-in on purpose: that step then shows its fallback, centred with no ring, which is itself a state to see.
const STANDIN_ANCHORS = {
  strip: '[data-testid="plan-strip"]',
  row: '.plan-list .plan-item',
  doer: '[data-testid="plan-chips-1"]',
  field: '[data-testid="plan-target"]',
  'history-row': '[data-testid^="plan-history-entry-"]',
  history: '[data-testid="plan-view-hist"]',
};

// Runs in the page before the bundle. `history` is how many rows getPlanHistory answers with: 0 is a new person.
const seed = (history, standin) => `
${FIXTURE_SRC}
const __plan = makePlan({});
const __rows = Array.from({ length: ${history} }, (_, i) => ({ id: 'e' + i, kind: 'edit', when: new Date(Date.now() - i * 60000).toISOString(),
  by: 'you', title: 'Hid a column', deltas: [], undoable: true, laterCount: i }));
window.__guideHost = [];
const FLAGS = 'uishot-guide-flags';
const readFlags = () => { try { return JSON.parse(localStorage.getItem(FLAGS) || '{}'); } catch { return {}; } };
let realApi;
Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
  get: () => function () {
    const api = realApi();
    const post = api.postMessage;
    api.postMessage = function (msg) {
      if (msg && (msg.type === 'getGuideFlags' || msg.type === 'setGuideFlag')) {
        window.__guideHost.push(msg);
        const f = readFlags();
        if (msg.type === 'setGuideFlag') {
          if (msg.flag === 'planTour') f['semanticus.guide.planTour'] = { done: true, at: new Date().toISOString() };
          if (msg.flag === 'whatChanged') f['semanticus.guide.whatChanged.' + msg.cardId] = true;
          localStorage.setItem(FLAGS, JSON.stringify(f));
          return;
        }
        setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'guideFlags', id: msg.id,
          planTour: !!(f['semanticus.guide.planTour'] || {}).done, whatChanged: f['semanticus.guide.whatChanged.' + msg.cardId] === true } })), 0);
        return;
      }
      return post.call(this, msg);
    };
    return api;
  },
  set: (v) => { realApi = v; } });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getModelPlan', () => __plan);
  v.setResponse('getPlanHistory', () => ({ sessionId: 's', rows: __rows, more: false, kept: true }));
} });
${standin ? `
const __anchors = ${JSON.stringify(STANDIN_ANCHORS)};
const __mark = () => { for (const [name, sel] of Object.entries(__anchors)) {
  const el = document.querySelector(sel);
  if (el && el.getAttribute('data-tour') !== name && !document.querySelector('[data-tour="' + name + '"]')) el.setAttribute('data-tour', name);
} };
new MutationObserver(__mark).observe(document, { subtree: true, childList: true });` : ''}
`;

const MEASURE = `(() => {
  const box = (el) => { if (!el) return null; const r = el.getBoundingClientRect(); return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) }; };
  const tour = document.querySelector('.plan-tour');
  const ring = document.querySelector('.plan-tour-ring');
  const wc = document.querySelector('.plan-wc');
  const step = tour ? tour.querySelector('.plan-tour-count')?.textContent : null;
  const anchorName = tour ? ['strip','row','doer','field','queue-switch','history-row','history'].find((n) => {
    const a = document.querySelector('[data-tour="' + n + '"]'); if (!a || !ring) return false;
    const ar = a.getBoundingClientRect(), rr = ring.getBoundingClientRect();
    return Math.abs(ar.left - rr.left - 6) < 2 && Math.abs(ar.top - rr.top - 6) < 2;
  }) ?? null : null;
  const cardBox = box(tour);
  const inView = cardBox ? cardBox.x >= 0 && cardBox.y >= 0 && cardBox.x + cardBox.w <= innerWidth && cardBox.y + cardBox.h <= innerHeight : null;
  return { step, title: tour?.querySelector('.plan-tour-title')?.textContent ?? null, text: tour?.innerText ?? null,
    anchoredOn: anchorName, card: cardBox, focusInTour: tour ? tour.contains(document.activeElement) : null, ring: box(ring), cardInView: inView,
    whatChanged: wc ? wc.innerText : null, helpOpen: !!document.querySelector('.plan-guide-reopen'),
    host: window.__guideHost.slice(), flags: JSON.parse(localStorage.getItem('uishot-guide-flags') || '{}') };
})()`;

const click = (page, text, scope = 'button') => page.evaluate((t, sc) => {
  const b = [...document.querySelectorAll(sc)].find((x) => x.textContent.trim() === t && x.getClientRects().length);
  if (b) b.click();
  return !!b;
}, text, scope);

const results = [];
async function open(ctx, { history, tab = 'plan', theme }) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(history, STANDIN));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=${tab}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1400);
  return { page, errors };
}
async function shot(page, theme, name, extra = {}) {
  await wait(500); // the step card eases into place over .35s
  const m = await page.evaluate(MEASURE);
  const file = join(OUT, `${theme}-7-${name}.png`);
  await page.screenshot({ path: file });
  results.push({ theme, scene: name, standin: STANDIN, ...m, ...extra });
  return m;
}
async function reload(page, theme) {
  await page.reload({ waitUntil: 'networkidle0' });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1400);
}

const EXPECT_ANCHOR = ['strip', 'row', 'doer', 'field', 'queue-switch', 'history-row'];
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } };

for (const theme of THEMES) {
  // ---- a new person: the tour, all six steps, Done, a reload, and Help's way back in -----------------------
  {
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, { history: 0, theme });
    for (let i = 0; i < 6; i += 1) {
      const m = await shot(page, theme, `tour-${i + 1}`);
      check(m.step === `${i + 1} of 6`, `${theme} tour step ${i + 1} shows (${m.step})`);
      check(m.cardInView === true, `${theme} step ${i + 1} card is wholly on screen`);
      check(m.focusInTour === true, `${theme} step ${i + 1} has the keyboard (focus is on its Next or Done)`);
      if (!(STANDIN && EXPECT_ANCHOR[i] === 'queue-switch')) {
        check(m.anchoredOn === EXPECT_ANCHOR[i] || (i === 5 && m.anchoredOn === 'history'), `${theme} step ${i + 1} ring is on ${EXPECT_ANCHOR[i]} (got ${m.anchoredOn})`);
      }
      if (i < 5) check(await click(page, 'Next'), `${theme} Next on step ${i + 1}`);
    }
    check(await click(page, 'Done'), `${theme} Done on step 6`);
    await wait(300);
    let m = await page.evaluate(MEASURE);
    check(m.step === null, `${theme} Done closes the tour`);
    check(!!m.flags['semanticus.guide.planTour']?.done, `${theme} Done sets the tour flag`);
    check(m.flags['semanticus.guide.whatChanged.m11-1'] === true, `${theme} the tour showing set the card flag`);
    await reload(page, theme);
    m = await shot(page, theme, 'later');
    check(m.step === null && m.whatChanged === null, `${theme} after Done and a reload nothing shows`);
    check(await click(page, '?'), `${theme} the Plan header ? is there`);
    await wait(400);
    m = await shot(page, theme, 'help-reopen');
    check(m.helpOpen, `${theme} ? opens Help at the Plan with the way back in`);
    check(await click(page, 'Show the tour'), `${theme} Show the tour in Help`);
    m = await shot(page, theme, 'tour-restarted');
    check(m.step === '1 of 6', `${theme} Help's Show the tour starts it again at step 1`);
    await page.keyboard.press('Escape');
    await wait(300);
    m = await page.evaluate(MEASURE);
    check(m.step === null, `${theme} Escape closes the tour from step 1`);
    check(await click(page, '?'), `${theme} ? again`);
    await wait(300);
    check(await click(page, 'Show the tour'), `${theme} Show the tour again`);
    await wait(600);
    check(await click(page, 'Close'), `${theme} Close at step 1`);
    await wait(300);
    m = await page.evaluate(MEASURE);
    check(m.step === null, `${theme} Close works at any step`);
    results.push({ theme, scene: 'tour-errors', errors });
    check(errors.length === 0, `${theme} no page errors: ${errors.join(' | ')}`);
    await ctx.close();
  }
  // ---- an existing person: the card, Show the tour from it, and nothing twice ------------------------------
  {
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, { history: 3, theme });
    let m = await shot(page, theme, 'update');
    check(m.whatChanged?.startsWith('What changed'), `${theme} an existing person sees the What changed card`);
    check(m.step === null, `${theme} and not the tour`);
    check(m.flags['semanticus.guide.whatChanged.m11-1'] === true, `${theme} the card showing sets its flag`);
    await reload(page, theme);
    m = await page.evaluate(MEASURE);
    check(m.whatChanged === null && m.step === null, `${theme} a reload before Done shows nothing twice`);
    await ctx.close();
  }
  {
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, { history: 3, theme });
    check(await click(page, 'Show the tour'), `${theme} Show the tour on the card`);
    let m = await shot(page, theme, 'update-then-tour');
    check(m.step === '1 of 6' && m.whatChanged === null, `${theme} the card gives way to the tour`);
    check(await click(page, 'Close'), `${theme} Close`);
    await reload(page, theme);
    m = await page.evaluate(MEASURE);
    check(m.whatChanged === null && m.step === null, `${theme} and neither comes back`);
    await ctx.close();
  }
  // ---- from another page: Help's menu goes to the Plan and starts the tour ---------------------------------
  {
    const ctx = await browser.createBrowserContext();
    const { page } = await open(ctx, { history: 3, tab: 'bpa', theme });
    check(await click(page, 'Help'), `${theme} Help in the header`);
    await wait(200);
    await shot(page, theme, 'help-menu');
    check(await click(page, 'Show the tour', '[role="menuitem"]'), `${theme} Show the tour heads the Help menu`);
    await wait(1200);
    const m = await shot(page, theme, 'help-menu-tour');
    check(m.step === '1 of 6', `${theme} from Model quality, it goes to the Plan and starts the tour`);
    await ctx.close();
  }
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
await browser.close();
server.close();
console.log(`plan-guide-drive: ${results.length} results in ${OUT}${STANDIN ? ' (STAND-IN anchors on the pre-U1 Plan page)' : ''}; ${failed} failed`);
process.exit(failed ? 1 : 0);
