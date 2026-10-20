// Browser proof for fix-connections-blank (1.6): every way into Connections leaves the Studio standing. The hub used to
// return early before two hooks, so the first time `shown` flipped React threw error #310 and the Studio went blank.
// Each press below must open the hub, leave the Studio visible, and raise no page error. Closing and pressing again too.
//
//   SEMANTICUS_BROWSER=/usr/bin/chromium [THEMES=dark] node tools/uishot/connections-blank-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'connections-blank');
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('Build the webview first.'); process.exit(1); }
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `cb-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };

// What the person sees: the Studio shell has content, and the hub is or is not up.
const look = (page) => page.evaluate(() => ({
  studio: (document.getElementById('root')?.innerText ?? '').trim().length,
  header: !!document.querySelector('.studio-header-action'),
  hub: !!document.querySelector('[data-testid="hub-account-button"], [data-testid="hub-add-workspace"], .hub-frame, [role="dialog"]'),
}));
const pressText = (page, label) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button, [role="menuitem"]')].find((x) => x.textContent.trim() === t && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, label);
const pressId = (page, id) => page.evaluate((i) => { const b = document.querySelector(`[data-testid="${i}"]`); if (b && b.getClientRects().length) { b.click(); return true; } return false; }, id);
const close = async (page) => { await page.keyboard.press('Escape'); await wait(500); };

// Each way in. `go` returns false when the control is not on screen, which fails the run rather than skipping silently.
const ROUTES = [
  ['header Connections button', (p) => pressText(p, 'Connections'), 'live=1&tab=plan'],
  ['footer Editing segment (the model name / unsaved press)', (p) => pressId(p, 'connections-editing'), 'live=1&conn=1&tab=plan'],
  ['footer Tests and queries segment', (p) => pressId(p, 'connections-querying'), 'live=1&conn=1&tab=plan'],
  ['footer Publishing segment', (p) => pressId(p, 'connections-publishing'), 'live=1&conn=1&tab=plan'],
  ['host message: open Connections', (p) => p.evaluate(() => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'openConnections', section: 'open' } })); return true; }), 'live=1&conn=1&tab=plan'],
  ['footer Editing segment with unsaved changes', (p) => pressId(p, 'connections-editing'), 'live=1&conn=1&dirty=1&tab=plan'],
  ['Tests page: Change test model', async (p) => { await p.evaluate(() => { const b = [...document.querySelectorAll('nav button, [data-tab], button')].find((x) => /^(Tests|Query|Queries)$/.test(x.textContent.trim())); b?.click(); }); await wait(700); return pressId(p, 'change-model'); }, 'live=1&conn=1&tab=query'],
];
for (const theme of (process.env.THEMES || 'dark').split(',')) {
  console.log(`== ${theme}`);
  for (const [name, go, q] of ROUTES) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('nav button, [data-tab], button', { timeout: 20000 });
    await applyTheme(page, theme);
    await wait(800);
    const before = await look(page);
    check(before.studio > 50, `${name}: the Studio shows before the press`);
    const pressed = await go(page);
    await wait(900);
    const open = await look(page);
    check(pressed, `${name}: the control was on screen and pressed`);
    check(errors.length === 0, `${name}: no React or page error after the press ${errors.length ? JSON.stringify(errors[0]).slice(0, 160) : ''}`);
    check(open.studio > 50 && open.hub, `${name}: the Studio did not go blank and the hub is up (text=${open.studio}, hub=${open.hub})`);
    await page.screenshot({ path: join(OUT, `${theme}-${name.replace(/\W+/g, '-')}.png`) });
    await close(page);
    const closed = await look(page);
    check(closed.studio > 50, `${name}: the Studio is still there after closing`);
    await go(page); await wait(700);
    check((await look(page)).studio > 50 && errors.length === 0, `${name}: pressing again is fine`);
    await page.close();
  }
}
await browser.close(); server.close();
console.log(failed ? `${failed} FAILED` : 'all ok'); process.exit(failed ? 1 : 0);
