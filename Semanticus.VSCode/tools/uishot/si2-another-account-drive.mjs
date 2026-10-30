// si2: sign in another account beside a saved one, in the harness, dark and light, at 1440 and 290.
//   menu      the account menu with one saved account (alex@contoso.com) and "Sign in another account"
//   another   the press on "Sign in another account": the workspace picker, and the account it lists as
//   plain     "Workspace model": the picker as the saved account, with "Use another account" beside it
// The engine is the harness mock. listWorkspaces answers as the saved account, unless the call asks for another
// account: then it answers as sam@fabrikam.com with that account's profile id on each row, as the engine does.
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/si2-another-account-drive.mjs
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
// WEBROOT serves another built tree (the before shots use the build from before the fix).
const webRoot = process.env.WEBROOT ? resolve(process.env.WEBROOT) : resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
const TAG = process.env.TAG || 'shot';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '.', '.cache', 'semanticus-uishot', `si2-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = []; const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); };

// One saved browser account, no model in play: the case Kane met.
const START = {
  connections: [],
  connectionContext: { editing: { available: false, live: false }, querying: { available: false, live: false }, publishing: { available: false, live: false } },
  accountProfiles: [{ id: 'pf-alex', username: 'alex@contoso.com', tenantId: null, family: 'interactive', isDefault: true, signedIn: true, lastSignInUtc: '2026-10-28T09:00:00Z', lastUseUtc: '2026-10-28T09:00:00Z' }],
  accountProbes: [],
  // A file model is open; the saved account is the one the page names.
  session: { sessionId: 'uishot', modelName: 'Contoso (mock)', measures: 12, tables: 4, hasUnsavedChanges: false, liveConnected: false, currentAccount: 'alex@contoso.com' },
};

function install() {
  const H = window.__uishotHarness;
  window.__si2 = { lists: [], models: [] };
  H.setResponse('listWorkspaces', (params) => {
    window.__si2.lists.push(params);
    const another = params[3] === true;
    return another
      ? [{ id: 'ws-client', displayName: 'Fabrikam Finance', type: 'Workspace', accountProfileId: 'pf-sam', account: 'sam@fabrikam.com' }]
      : [{ id: 'ws-own', displayName: 'Contoso Sales', type: 'Workspace' }, { id: 'ws-me', displayName: 'alex@contoso.com', type: 'Personal' }];
  });
  H.setResponse('listWorkspaceModels', (params) => { window.__si2.models.push(params); return [{ id: 'm1', displayName: params[0] === 'ws-client' ? 'Fabrikam Budget' : 'Contoso Sales' }]; });
}
const text = () => (document.body.innerText || '').replace(/\s+/g, ' ');
const click = (id) => { const b = document.querySelector(`[data-testid="${id}"]`); if (b) b.click(); return !!b; };

for (const width of (process.env.WIDTHS || '1440,290').split(',').map(Number)) {
  for (const theme of (process.env.THEMES || 'dark,light').split(',')) {
    const name = `${TAG}-${width}-${theme}`;
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, START);
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-testid="hub-add-workspace"]', { timeout: 15000 });
    await page.evaluate(install);
    await applyTheme(page, theme);
    await wait(400);

    // menu
    await page.evaluate(() => document.querySelector('[data-testid="hub-account-button"]').click()); await wait(400);
    check(await page.evaluate(() => !!document.querySelector('[data-testid="hub-sign-in-another-top"]')), `${name}: the menu offers "Sign in another account"`);
    await page.screenshot({ path: join(OUT, `${name}-1-menu.png`) });

    // another: the press on "Sign in another account"
    await page.evaluate(click, 'hub-sign-in-another-top'); await wait(900);
    let t = await page.evaluate(text);
    const asked = await page.evaluate(() => window.__si2.lists.map((p) => p[3] === true));
    check(await page.evaluate(() => !!document.querySelector('[data-testid="pick-workspace"]')), `${name}: the press opens the workspace picker`);
    check(asked.length > 0 && asked.every(Boolean), `${name}: the picker asked for another account (${JSON.stringify(asked)})`);
    check(/as sam@fabrikam\.com/.test(t) && /Fabrikam Finance/.test(t), `${name}: the picker lists as the new account`);
    await page.screenshot({ path: join(OUT, `${name}-2-another.png`) });
    // pick the workspace: the models are read as the same new account
    await page.evaluate(() => [...document.querySelectorAll('[data-testid="pick-ws"]')][0]?.click()); await wait(500);
    check(await page.evaluate(() => window.__si2.models.some((p) => p[4] === 'pf-sam')), `${name}: the models are listed as the new account`);
    await page.screenshot({ path: join(OUT, `${name}-3-another-models.png`) });

    // plain: back, then "Workspace model"
    await page.evaluate(click, 'pick-back'); await wait(400);
    await page.evaluate(() => { window.__si2.lists = []; });
    await page.evaluate(click, 'hub-add-workspace'); await wait(900);
    t = await page.evaluate(text);
    check(/as alex@contoso\.com/.test(t), `${name}: "Workspace model" lists as the saved account`);
    check(await page.evaluate(() => !!document.querySelector('[data-testid="pick-another-account"]')), `${name}: "Use another account" sits beside it`);
    await page.screenshot({ path: join(OUT, `${name}-4-plain.png`) });
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    check(!overflow, `${name}: no sideways scroll`);
    check(errors.length === 0, `${name}: no page errors ${errors.join('; ')}`);
    await page.close();
  }
}
await browser.close(); server.close();
writeFileSync(join(OUT, `${TAG}-si2-drive.txt`), lines.join('\n') + '\n');
console.log(lines.join('\n'));
process.exit(fails.length ? 1 : 0);
