// One colour per actor (1.5, colours lane): the Plan's three actor rows, the driver bar in each state, the assistant sign idle and
// working, and the drive edge at 1280 and 1920 wide (and at 125% and 150% zoom), in Dark Modern and Light Modern. Real Studio bundle,
// plan fixture, no desktop. Writes PNGs and results.json (edge weight measured off the pixels, side by side).
//
//   OUTDIR=<dir> TAG=before|after node tools/uishot/colours-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'colours');
const TAG = process.env.TAG || 'after';
mkdirSync(OUT, { recursive: true });

const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const seed = `
${FIXTURE_SRC}
let __plan = makeHeartPlan({ mode: 'plain', free: false });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => { __h = v; v.setResponse('getModelPlan', () => __plan); },
});
window.__driverNow = 100000;
Date.now = () => window.__driverNow;
let __acquire;
Object.defineProperty(window, 'acquireVsCodeApi', {
  get: () => __acquire,
  set: (original) => { __acquire = () => {
    const api = original(); const post = api.postMessage;
    api.postMessage = (message) => {
      post(message);
      if (message.type === 'studioReady') window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'iDrive', assistantConnected: true } }));
    };
    return api;
  }; },
});
(() => {})()`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
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
const results = { tag: TAG, edge: [] };

async function open({ theme, width, height = 900, dpr = 1 }) {
  const page = await browser.newPage();
  await page.setViewport({ width, height, deviceScaleFactor: dpr });
  await page.evaluateOnNewDocument(seed);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0' });
  await applyTheme(page, theme);
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await page.waitForSelector('[data-testid="studio-driver-bar"]');
  await wait(800);
  return page;
}
const send = (page, message) => page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), message);
const call = (page) => send(page, { type: 'assistantCall', payload: { at: new Date(100000).toISOString(), tool: 'get_model_summary',
  assistant: { key: 'claude', name: 'Claude Code', initials: 'CC', version: '2.1.0' } } });

/** Driver states, each reached the way a person would reach it. */
async function setState(page, state) {
  if (state === 'iDrive') return;
  await page.click('[data-testid="driver-mode-claudeDrives"]');
  if (state === 'working') { await call(page); await page.waitForSelector('[data-testid="assistant-sign"][data-state="working"]'); }
  if (state === 'idle') {
    await call(page); await page.waitForSelector('[data-testid="assistant-sign"]');
    await page.evaluate(() => { window.__driverNow += 60000; });
    await wait(200); await call(page).catch(() => {});
    await page.evaluate(() => { window.__driverNow += 60000; });
    await page.waitForSelector('[data-testid="assistant-sign"][data-state="idle"]', { timeout: 15000 });
  }
  if (state === 'wheel') {
    await call(page); await page.waitForSelector('[data-testid="assistant-sign"]');
    await page.mouse.click(700, 330);
    await page.waitForSelector('[data-testid="driver-resume"]');
  }
  await wait(300);
}

for (const theme of ['dark', 'light']) {
  // The Plan page with the driver bar in each state (top of the page: bar, head, strip of three actor rows).
  for (const state of ['wheel', 'working', 'idle', 'iDrive']) {
    const page = await open({ theme, width: 1280 });
    await setState(page, state);
    await page.evaluate(() => document.querySelector('.sc-edge')?.getAnimations().forEach((a) => { a.pause(); a.currentTime = 2100; }));
    await page.screenshot({ path: join(OUT, `plan-${state}-${theme}-${TAG}.png`), clip: { x: 0, y: 0, width: 1280, height: 420 } });
    if (state === 'working' || state === 'idle') {
      await page.screenshot({ path: join(OUT, `badge-${state}-${theme}-${TAG}.png`), clip: { x: 0, y: 40, width: 360, height: 56 } });
    }
    await page.close();
  }
}

// The edge: paused at the peak of its pulse, at two widths. Each side's weight is read off the pixels.
for (const theme of ['dark', 'light']) for (const [width, height] of [[1280, 800], [1920, 1080]]) for (const dpr of [1]) {
  const page = await open({ theme, width, height, dpr });
  await setState(page, 'working');
  await page.evaluate(() => document.querySelector('.sc-edge')?.getAnimations().forEach((a) => { a.pause(); a.currentTime = 2100; }));
  await wait(200);
  const file = `edge-${width}-x${dpr}-${theme}-${TAG}.png`;
  await page.screenshot({ path: join(OUT, file) });
  const css = await page.evaluate(() => { const e = document.querySelector('.sc-edge'); const c = getComputedStyle(e); const r = e.getBoundingClientRect();
    return { border: c.borderTopWidth + ' ' + c.borderRightWidth + ' ' + c.borderBottomWidth + ' ' + c.borderLeftWidth, outline: c.outlineWidth, opacity: c.opacity, shadow: c.boxShadow, rect: [r.x, r.y, r.width, r.height], inner: [innerWidth, innerHeight] }; });
  results.edge.push({ file, width, height, dpr, theme, css });
  await page.close();
}
writeFileSync(join(OUT, `results-${TAG}.json`), JSON.stringify(results, null, 1));
await browser.close();
server.close();
console.log('colours-drive done', TAG, OUT);
