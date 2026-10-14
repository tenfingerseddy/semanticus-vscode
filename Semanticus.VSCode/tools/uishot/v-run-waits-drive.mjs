// Verifier shot for run-waits: plays the engine's REAL plan before and after one press (dumped by VerifyRunWaitsTests), presses
// Run the plan with a real click, and screenshots the Plan with the header the engine wrote.
//   BEFORE=<plan.json> AFTER=<plan.json> SENTENCE="..." ITEMS=a,b OUTDIR=<dir> NAME=<name> node tools/uishot/v-run-waits-drive.mjs
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
const OUT = process.env.OUTDIR; const NAME = process.env.NAME || 'press';
mkdirSync(OUT, { recursive: true });
const S0 = JSON.parse(readFileSync(process.env.BEFORE, 'utf8'));
// WAVES: "result.json:plan.json,..." one engine answer per press call, in order (the page's wave loop calls again while a wave ran).
const WAVES = process.env.WAVES.split(',').map((w) => { const [r, pl] = w.split(':'); return { ...JSON.parse(readFileSync(r, 'utf8')), plan: JSON.parse(readFileSync(pl, 'utf8')) }; });
const S1 = WAVES[WAVES.length - 1].plan;
const WAITJEV = process.env.WAITJEV || '';   // a row id: during the press the engine says "waiting for Jev" on it every second
const seed = `
const __wj = ${JSON.stringify(WAITJEV)};
const __S = ${JSON.stringify({ S0, S1 })}; const __W = ${JSON.stringify(WAVES)};
let __stage = 0; window.__calls = [];
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getModelPlan', () => { window.__calls.push('getModelPlan'); return __stage ? __S.S1 : __S.S0; });
  v.setResponse('applyPlanItems', () => { const w = __W[Math.min(window.__calls.filter((c) => c === 'applyPlanItems').length, __W.length - 1)]; window.__calls.push('applyPlanItems');
    const last = window.__calls.filter((c) => c === 'applyPlanItems').length > __W.length;
    if (__wj) for (let k = 0; k <= 11; k++) setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'planRunProgress', payload: { runId: 'r1', itemIds: [__wj], phase: 'waitjev', wave: 1, waitedSeconds: k, ms: k * 1000 } } })), k * 1000);
    return new Promise((r) => setTimeout(() => { __stage = 1; r(last ? { performed: false, sentence: 'Nothing is ready.', plan: __S.S1 } : w);
    setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange', payload: { sessionId: 'uishot', revision: 9, origin: 'human', label: 'Run the plan' } } })), 50); }, __wj ? 12500 : 1500)); });
}});`;
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const page = await browser.newPage();
const errors = []; page.on('pageerror', (e) => errors.push(e.message));
await page.setViewport({ width: 1680, height: 1050, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument(seed);
await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
await applyTheme(page, 'dark'); await wait(1200);
await page.screenshot({ path: join(OUT, `${NAME}-1-before.png`) });
await page.evaluate(() => document.querySelector('[data-testid="plan-run"]').click());
if (WAITJEV) {
  await wait(8300); await applyTheme(page, 'dark');
  await page.screenshot({ path: join(OUT, `${NAME}-1b-waiting.png`) });
  const row = await page.evaluate((id) => (document.querySelector(`[data-row="${id}"] .pw-state`)?.textContent || '').trim(), WAITJEV);
  const head = await page.evaluate(() => (document.querySelector('.pw-head, header')?.innerText || '').replace(/\s+/g, ' ').slice(0, 300));
  console.log('WAITING ROW:', row, '| HEAD:', head);
}
await wait(9000); await applyTheme(page, 'dark'); await wait(300);
await page.screenshot({ path: join(OUT, `${NAME}-2-after.png`) });
const seen = await page.evaluate(() => {
  const t = (s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim();
  return { receipt: t('[data-testid="plan-run-receipt"]'), told: t('[data-testid="plan-told"]'), run: t('[data-testid="plan-run"]'), body: document.body.innerText.slice(0, 900),
    states: [...document.querySelectorAll('[data-testid="plan-list"] [data-row]')].map((r) => r.dataset.row + ' ' + r.dataset.state), calls: window.__calls };
});
writeFileSync(join(OUT, `${NAME}-seen.json`), JSON.stringify({ seen, errors }, null, 2));
console.log(JSON.stringify({ receipt: seen.receipt, told: seen.told, run: seen.run, errors }, null, 1));
await browser.close(); server.close();
