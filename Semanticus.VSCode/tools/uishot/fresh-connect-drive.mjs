// fix-fresh-connect: the Connections hub for a person with NO saved account, in the harness, dark and light at 1440.
//   menu       the Sign in menu is one Sign in (no "pick a workspace model" line)
//   starting   the engine is not there yet: "Starting Semanticus...", never "Engine not connected."
//   failed     the engine could not start: the reason, Try again, Restart engine
//   recovered  the engine comes up by itself: the picker lists the workspaces with nobody pressing anything
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/fresh-connect-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `fc-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = []; const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); };

// A person on a clean home: nothing saved, no account.
const START = { connections: [], connectionContext: { editing: { available: false, live: false }, querying: { available: false, live: false }, publishing: { available: false, live: false } }, accountProfiles: [], accountProbes: [] };
const WORKSPACES = [{ id: 'ws-demo', displayName: 'Semanticus Demo', type: 'Workspace' }, { id: 'ws-me', displayName: 'avery@contoso.com', type: 'Personal' }];

function install(engine) {
  const H = window.__uishotHarness;
  window.__fc = { engine, calls: 0 };
  H.setResponse('listWorkspaces', () => { window.__fc.calls++; return window.__fc.engine === 'up' ? [{ id: 'ws-demo', displayName: 'Semanticus Demo', type: 'Workspace' }, { id: 'ws-me', displayName: 'avery@contoso.com', type: 'Personal' }] : { __uishotError: 'Engine not connected.' }; });
  H.setResponse('listWorkspaceModels', () => [{ id: 'm1', displayName: 'Contoso Sales' }]);
}
const clickText = (words) => { const b = [...document.querySelectorAll('button')].find((x) => words.every((w) => (x.textContent || '').includes(w))); if (b) b.click(); return !!b; };
const text = () => (document.body.innerText || '').replace(/\s+/g, ' ');
const send = (data) => window.dispatchEvent(new MessageEvent('message', { data }));

for (const theme of (process.env.THEMES || 'dark,light').split(',')) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, START);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="hub-add-workspace"]', { timeout: 15000 });
  await page.evaluate(install, 'down');
  await applyTheme(page, theme);
  await wait(400);

  // menu
  await page.evaluate(() => document.querySelector('[data-testid="hub-account-button"]').click()); await wait(400);
  let t = await page.evaluate(text);
  check(!/Pick a workspace model to sign in/.test(t) && !/No saved accounts yet/.test(t), `${theme}: the empty Sign in menu has no "pick a workspace model" line`);
  check(await page.evaluate(() => !!document.querySelector('[data-testid="hub-sign-in-first"]')), `${theme}: the empty menu is one Sign in`);
  check(await page.evaluate(() => !document.querySelector('[data-testid="hub-sign-in-another-top"]')), `${theme}: no "Sign in another account" before there is an account`);
  await page.screenshot({ path: join(OUT, `fc-${theme}-1-menu.png`) });

  // one press on that Sign in: the workspace page, reading as the person pressed (engine away -> starting)
  await page.evaluate(() => document.querySelector('[data-testid="hub-sign-in-first"]').click()); await wait(900);
  t = await page.evaluate(text);
  check(await page.evaluate(() => !!document.querySelector('[data-testid="pick-workspace"]')), `${theme}: Sign in opens the workspace page`);
  check(/Starting Semanticus/.test(t) && !/Engine not connected/.test(t), `${theme}: a starting engine says "Starting Semanticus..." and never "Engine not connected."`);
  check(await page.evaluate(() => window.__fc.calls) >= 1, `${theme}: the page asked the engine for the workspaces`);
  await page.screenshot({ path: join(OUT, `fc-${theme}-2-starting.png`) });

  // a real failure: the reason, Try again, Restart engine
  await page.evaluate(send, { type: 'engineState', connected: false, reason: 'Another VS Code window is using this folder with an older version of Semanticus. Close that window, then try again.' }); await wait(500);
  t = await page.evaluate(text);
  check(/older version of Semanticus/.test(t) && !/Engine not connected/.test(t), `${theme}: a real failure says what broke`);
  check(await page.evaluate(() => !!document.querySelector('[data-testid="pick-ws-retry"]') && !!document.querySelector('[data-testid="pick-engine-restart"]')), `${theme}: and offers Try again and Restart engine`);
  await page.screenshot({ path: join(OUT, `fc-${theme}-3-failed.png`) });

  // the engine comes up by itself: nobody presses anything
  await page.evaluate(() => { window.__fc.engine = 'up'; }); await page.evaluate(send, { type: 'engineState', connected: true }); await wait(1200);
  t = await page.evaluate(text);
  check(/Semanticus Demo/.test(t) && !/Engine not connected|Starting Semanticus/.test(t), `${theme}: the picker lists the workspaces by itself once the engine is up`);
  await page.screenshot({ path: join(OUT, `fc-${theme}-4-recovered.png`) });
  check(errors.length === 0, `${theme}: no page errors ${errors.join('; ')}`);
  await page.close();
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'fresh-connect-drive.txt'), lines.join('\n') + '\n');
console.log(lines.join('\n'));
process.exit(fails.length ? 1 : 0);
