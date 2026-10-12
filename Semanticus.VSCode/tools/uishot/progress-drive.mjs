// Proof for the progress line on Review > Validation ("312 of 691 checks" while a run goes). It serves THIS worktree's built
// bundle, starts a run that never finishes, sends the page progress for ITS OWN run (and for another run, which must not
// move the count), and shoots each state in Dark Modern and Light Modern. The rendered text goes to results.json.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=<chromium> node tools/uishot/progress-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { THEMES } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'progress');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `run-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];

for (const [themeKey, theme] of Object.entries(THEMES).filter(([k]) => k !== 'hc')) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 1366, height: 768, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&testrun=hang`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await page.evaluate((t) => {
    document.body.className = t.bodyClass;
    for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
  }, theme);
  await wait(600);
  const click = (label, within) => page.evaluate((l, w) => {
    const vis = (el) => el.getClientRects().length > 0;
    const root = w ? document.querySelector(w) : document;
    const el = [...root.querySelectorAll('button, [role="tab"]')].filter(vis).find((b) => b.textContent.trim() === l);
    if (el) el.click();
    return !!el;
  }, label, within);
  if (!await click('Review', 'nav[aria-label="Studio areas"]')) throw new Error('no Review area');
  await wait(900);
  if (!await click('Validation')) throw new Error('no Validation tab');
  await wait(900);
  if (!await click('Run checks')) throw new Error('no Run checks button');
  await wait(500);
  const read = () => page.evaluate(() => {
    const el = document.querySelector('.sem-tests-status');
    const bar = el?.querySelector('[role="progressbar"]');
    return { status: el ? el.innerText.replace(/\n+/g, ' | ').trim() : null, bar: bar ? bar.getAttribute('aria-valuenow') : null, runId: window.__uishotLastRunId };
  });
  const send = (payload) => page.evaluate((p) => window.postMessage({ type: 'progressDidChange', payload: p }, '*'), payload);
  const scenes = [];
  const snap = async (name) => {
    await page.screenshot({ path: join(OUT, `progress-${themeKey}-${name}.png`) });
    scenes.push({ name, ...(await read()) });
  };
  await snap('before-count');
  const runId = (await read()).runId;
  if (!runId) throw new Error('the page sent no run id');
  await send({ opKey: 'run_tests', runId, done: 312, total: 691, note: 'saved checks' });
  await wait(400);
  await snap('312-of-691');
  await send({ opKey: 'run_tests', runId: 'assistant-run', done: 600, total: 691 });
  await wait(300);
  await snap('other-run-ignored');
  await send({ opKey: 'run_tests', runId, done: 691, total: 691 });
  await wait(400);
  await snap('grading');
  results.push({ theme: theme.label, errors, scenes });
  await page.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results.map((r) => ({ theme: r.theme, errors: r.errors, scenes: r.scenes })), null, 1));
await browser.close();
server.close();
