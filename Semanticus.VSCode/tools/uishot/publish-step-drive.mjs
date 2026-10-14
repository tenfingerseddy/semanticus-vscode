// Proof for the live publish step line on the Publish page ("Reading the live model, 0:42"). Serves THIS worktree's built bundle,
// sends the page the engine's progress events, and shoots each state. Writes PNGs and results.json.
//
//   OUTDIR=<dir> node tools/uishot/publish-step-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/semanticus-joint-review/m15/deploy-speed/shots';
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
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); };
const results = [];

async function open(q, w = 1440, h = 900) {
  const page = await browser.newPage();
  page.errors = [];
  page.on('pageerror', (e) => page.errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') page.errors.push('console: ' + m.text()); });
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${q}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(3500);   // the deep link is sent repeatedly until the session lands; the page then reads its destination
  return page;
}
const text = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; }, sel);
const count = (page, sel) => page.evaluate((s) => document.querySelectorAll(s).length, sel);
const clickSel = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); if (e) e.click(); return !!e; }, sel);
const clickText = (page, label) => page.evaluate((l) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === l && x.getClientRects().length); if (b) b.click(); return !!b; }, label);
const rowTick = (page, name) => page.evaluate((n) => {
  const row = [...document.querySelectorAll('[data-testid="publish-row"]')].find((r) => (r.textContent || '').includes(n));
  const box = row && row.querySelector('input[type="checkbox"]'); if (box) box.click(); return !!box;
}, name);
const shot = async (page, slug) => { const out = join(OUT, `${slug}.png`); await page.screenshot({ path: out }); return out; };
const goLabel = (page) => text(page, '[data-testid="publish-go"]');
const record = (slug, page, extra = {}) => results.push({ slug, errors: page.errors, ...extra });


const send = (page, payload) => page.evaluate((p) => window.postMessage({ type: 'progressDidChange', payload: p }, '*'), payload);
const page = await open('live=1&conn=1&tab=deploy');
const stepText = () => text(page, '[data-testid="publish-step"]');
expect(await stepText() === null, 'before: no step line');
await shot(page, '1-before');
await send(page, { opKey: 'deploy_live', runId: 'r1', done: 1, total: 0, note: 'Reading the live model' });
await wait(300);
expect(/^Reading the live model, 0:0\d$/.test(await stepText() || ''), `reading: ${JSON.stringify(await stepText())}`);
await wait(2200);
const later = await stepText();
expect(/^Reading the live model, 0:0[2-9]$/.test(later || ''), `the clock moves: ${JSON.stringify(later)}`);
await shot(page, '2-reading');
await send(page, { opKey: 'deploy_live', runId: 'r1', done: 4, total: 0, note: 'Sending 85 changes' });
await wait(300);
expect(/^Sending 85 changes, 0:0\d$/.test(await stepText() || ''), `sending: ${JSON.stringify(await stepText())}`);
await shot(page, '3-sending');
await send(page, { opKey: 'run_tests', runId: 'x', done: 1, total: 3, note: 'other' });
await wait(200);
expect(/^Sending 85 changes/.test(await stepText() || ''), 'another key does not move the line');
await send(page, { opKey: 'deploy_live', runId: 'r1', done: 6, total: 0, note: null });
await wait(300);
expect(await stepText() === null, 'after: the line is gone');
await shot(page, '4-after');
const width = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
expect(width, 'no sideways scroll');
record('all', page, {});
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failures, results }, null, 2));
await page.close(); await browser.close(); server.close();
if (failures.length) { console.error('FAILED:\n' + failures.join('\n')); process.exit(1); }
console.log('publish-step-drive: ok');
