// Browser-driven probe for the Model Overview correctness fixes (Astra spot review 2, findings 1, 2, 3
// and the loading-copy follow-up). It serves THIS worktree's built bundle, drives the real page with real
// clicks, and MEASURES what landed: the Checks card after a run the engine did not record, the Changes
// card's publication claim, the memory band's rendered block widths at two viewports, and the hero
// sentence while the model graph is still loading or has failed.
//
// It is a measuring instrument, not a screenshot tool: every scene writes numbers to results.json next to
// its PNG, so a claim about the band can be checked against pixels instead of an eyeball.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-overview-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'overview');
mkdirSync(OUT, { recursive: true });

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

// Response overrides have to be in place before studio.js issues its first call, so they ride in on the
// setter for __uishotHarness (the harness assigns it once, before the bundle loads).
const seed = (pairs, suppressChanges) => `
  ${suppressChanges ? "window.addEventListener('message', (e) => { if (e.data && (e.data.type === 'didChange' || e.data.type === 'activity')) e.stopImmediatePropagation(); }, true);" : ''}
  let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
    get: () => __h,
    set: (v) => { __h = v; ${pairs.map(([m, val]) => `v.setResponse(${JSON.stringify(m)}, ${JSON.stringify(val)});`).join(' ')} } });`;

// 23 saved passes, then a fresh run of 23 failures the engine did not record. Astra's exact overrides.
const HISTORY_ALL_PASS = { runs: [{ runId: 'saved-1', when: '2026-09-12T06:10:00Z', live: true, health: { checked: 23, passed: 23, failed: 0, notVerifiable: 0, missing: 0 } }] };
const RUN_ALL_FAIL = { runId: 'fresh-1', when: '2026-09-14T10:15:00Z', live: true, persisted: false, health: { checked: 23, passed: 0, failed: 23, notVerifiable: 0, missing: 0 }, definitionCount: 23 };

const MEASURE = `(() => {
  const band = document.querySelector('.overview-comp');
  const cards = [...document.querySelectorAll('.overview-health-card')].map((e) => e.innerText.replace(/\\n/g, ' | '));
  const blocks = [...document.querySelectorAll('.overview-comp-block')].map((e) => ({
    title: e.title,
    share: Number(e.dataset.share ?? e.style.flexGrow) || 0,
    width: Math.round(e.getBoundingClientRect().width * 100) / 100,
    padding: getComputedStyle(e).padding,
  }));
  const bandWidth = band ? Math.round(band.getBoundingClientRect().width * 100) / 100 : null;
  const gap = band ? getComputedStyle(band).gap : null;
  const drawn = blocks.reduce((n, b) => n + b.width, 0);
  return {
    hero: (document.querySelector('.overview-hero p') || {}).innerText || null,
    state: (document.querySelector('.model-home-state') || {}).innerText || null,
    cards, bandWidth, gap,
    blocks: blocks.map((b) => Object.assign(b, {
      drawnShare: drawn > 0 ? Math.round((100 * b.width / drawn) * 100) / 100 : 0,
      // What the block CLAIMS versus what it draws, in percentage points. This is the number finding 3 is about.
      shareErrorPp: drawn > 0 ? Math.round(((100 * b.width / drawn) - b.share) * 100) / 100 : 0,
    })),
    requests: (window.__uishotHarness.messages || []).filter((m) => m.type === 'rpc' && /runTests|listTestRuns/.test(m.method)).map((m) => ({ method: m.method, params: m.params })),
    docWidth: document.documentElement.scrollWidth,
  };
})()`;

const results = [];
async function scene(name, { query = 'conn=1&live=1&tier=pro', overrides = [], suppressChanges = false, steps = [], scroll = null }) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  if (overrides.length || suppressChanges) await page.evaluateOnNewDocument(seed(overrides, suppressChanges));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(1100);
  const notes = [];
  for (const step of steps) {
    const ok = await page.evaluate((label) => {
      const vis = (el) => el.getClientRects().length > 0;
      const el = [...document.querySelectorAll('button')].filter(vis).find((b) => (b.textContent || '').trim() === label);
      if (el) el.click();
      return !!el;
    }, step);
    if (!ok) notes.push(`control "${step}" not found`);
    await wait(1400);
  }
  const shots = {};
  const measured = {};
  for (const [w, h] of [[1440, 900], [1000, 800]]) {
    await page.setViewport({ width: w, height: h, deviceScaleFactor: 2 });
    await wait(800);
    measured[`${w}`] = await page.evaluate(MEASURE);
    const file = join(OUT, `${name}-${w}.png`);
    await page.screenshot({ path: file, fullPage: false });
    shots[`${w}`] = file;
    // A card below the fold at 1000 is still a card that has to be READ, so the scene can ask for a second
    // shot with it scrolled into view rather than being reported from the DOM dump alone.
    if (scroll) {
      await page.evaluate((sel) => { const el = document.querySelector(sel); if (el) el.scrollIntoView({ block: 'center' }); }, scroll);
      await wait(500);
      const scrolled = join(OUT, `${name}-${w}-scrolled.png`);
      await page.screenshot({ path: scrolled, fullPage: false });
      shots[`${w}-scrolled`] = scrolled;
    }
  }
  results.push({ scene: name, notes, errors, shots, measured });
  await page.close();
}

// 1 + 2: saved history all-pass, a fresh unrecorded run all-fail, and no observed change notifications.
await scene('checks-and-changes', {
  query: 'conn=1&live=1&tier=pro&dirty=1',
  overrides: [['listTestRuns', HISTORY_ALL_PASS], ['runTests', RUN_ALL_FAIL]],
  suppressChanges: true,
  steps: ['Run tests'],
  scroll: '.overview-health',
});
// 2b: the same page with this webview's own change notifications left alone (edits actually observed).
await scene('changes-with-edits', { query: 'conn=1&live=1&tier=pro&dirty=1', scroll: '.overview-health' });
// 3: the memory band after a real size scan, measured at both viewports.
await scene('band-scanned', { steps: ['Scan sizes'] });
// 4: the hero while the graph is still loading, and after it fails.
await scene('hero-loading', { overrides: [['getModelGraph', '__uishotNever']] });
await scene('hero-error', { overrides: [['getModelGraph', { __uishotError: 'The engine did not answer (mock failure for review).' }]] });

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
