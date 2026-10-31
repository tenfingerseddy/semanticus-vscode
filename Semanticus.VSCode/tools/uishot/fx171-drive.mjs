// fx171: the Connections screens for a model in a client's tenant, before and after the fix, in the harness.
//   1-row        the saved models; Contoso Sales opens as one account and was last opened as another (Kane A1)
//   2-choice     "Open as another account" on Contoso Sales: the account choice (cx1 gap 5)
//   3-cancel     "Sign in another account", then Cancel while the browser waits: the Open buttons (cx1 blocker 2)
//   4-menu       the account menu with a service identity in use (cx1 blocker 4)
//   5-picker     Workspace model, "Other ways to sign in" open with a browser sign-in (cx1 blocker 1)
//   6-sqlform    "Add SQL endpoint" on Contoso Sales: the sign-in and its tenant (Kane A2, cx1 gap 10)
//   7-jobs       the Jobs panel: where Publish goes (cx1 blocker 3)
//   8-history    History from the account menu: failure rows name the model, account and tenant (cx1 gap 8)
// Fake identities only. The engine is the harness mock; openLive is held, so no sign-in ever answers.
//   WEBROOT=<built tree> OUTDIR=<dir> TAG=before|after node tools/uishot/fx171-drive.mjs
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
const webRoot = process.env.WEBROOT ? resolve(process.env.WEBROOT) : resolve(here, '..', '..');
const harnessRoot = resolve(here, '..', '..');   // the harness page and its scripts come from this tree
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
const TAG = process.env.TAG || 'shot';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const url = decodeURIComponent((req.url || '/').split('?')[0].split('#')[0]);
  const base = url.startsWith('/tools/uishot/') ? harnessRoot : webRoot;
  const p = normalize(join(base, url));
  if (!p.startsWith(base) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '.', '.cache', 'semanticus-uishot', `fx171-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = [];

const HOME = '11111111-aaaa-4aaa-8aaa-111111111111';
const GUEST = '22222222-bbbb-4bbb-8bbb-222222222222';
const now = new Date().toISOString();
const CONTOSO = { id: 'cx-a', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Workspace%20A', database: 'Contoso Sales', modelName: 'Contoso Sales',
  authMode: 'interactive', tenantId: GUEST, lastAccount: 'alex@contoso-a.example', lastUsedUtc: now, useCount: 3 };
const PILOT = { id: 'cx-p', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Semanticus%20Demo', database: 'Northwind Demo', modelName: 'Northwind Demo',
  authMode: 'serviceprincipal', tenantId: '', lastUsedUtc: now, useCount: 5 };
const START = {
  connections: [PILOT, CONTOSO],
  accountProbes: [{ id: 'cx-a', account: 'kane@home.example', previousAccount: 'alex@contoso-a.example', tenantId: GUEST }],
  accountProfiles: [],
  sqlSources: [],
  connectionContext: {
    editing: { available: true, live: true, modelName: 'Northwind Demo', kind: 'xmla', connectionId: 'cx-p', source: PILOT.endpoint, endpoint: PILOT.endpoint, authMode: 'serviceprincipal' },
    querying: { available: true, live: true, modelName: 'Northwind Demo', kind: 'xmla', connectionId: 'cx-p', source: PILOT.endpoint, endpoint: PILOT.endpoint, authMode: 'serviceprincipal' },
    publishing: { available: true, live: true, implied: true, modelName: 'Northwind Demo', kind: 'xmla', connectionId: 'cx-p', source: PILOT.endpoint, endpoint: PILOT.endpoint },
    relationship: 'sameInstance',
  },
  session: { sessionId: 'uishot', modelName: 'Northwind Demo', measures: 12, tables: 7, hasUnsavedChanges: false, liveConnected: true },
};

const text = () => (document.body.innerText || '').replace(/\s+/g, ' ');
const rowOf = (name) => [...document.querySelectorAll('.hub-row')].find((r) => (r.querySelector('.hub-row-name')?.textContent || '') === name);

async function fresh(width, theme) {
  const page = await browser.newPage();
  page.on('pageerror', (e) => lines.push('pageerror ' + e.message));
  await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, START);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="hub-add-workspace"]', { timeout: 15000 });
  await page.evaluate(() => { const H = window.__uishotHarness; H.hold('openLive'); H.setResponse('listWorkspaces', [{ id: 'ws1', displayName: 'Contoso Workspace A', type: 'Workspace' }]);
    H.setResponse('listConnectionHistory', [
      { id: 'h2', connectionId: 'cx-a', kind: 'open', ok: false, account: 'kane@home.example', tenantId: '22222222-bbbb-4bbb-8bbb-222222222222', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Workspace%20A', database: 'Contoso Sales', modelName: 'Contoso Sales', detail: 'signing in or opening the model failed; the saved account was left unchanged', whenUtc: new Date().toISOString() },
      { id: 'h1', connectionId: 'cx-a', kind: 'signin', ok: false, tenantId: '22222222-bbbb-4bbb-8bbb-222222222222', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Workspace%20A', database: 'Contoso Sales', modelName: 'Contoso Sales', detail: 'sign-in failed before an account was chosen (the sign-in did not complete).', whenUtc: new Date().toISOString() },
    ]); });
  await applyTheme(page, theme);
  await wait(500);
  return page;
}
async function menuItem(page, rowName, label) {
  await page.evaluate((n) => { const r = [...document.querySelectorAll('.hub-row')].find((x) => (x.querySelector('.hub-row-name')?.textContent || '') === n); r?.querySelector('[data-testid="hub-row-overflow"]')?.click(); }, rowName);
  await wait(300);
  return page.evaluate((l) => { const b = [...document.querySelectorAll('[role="menuitem"]')].find((x) => x.textContent.includes(l)); b?.click(); return !!b; }, label);
}
const shot = (page, n) => page.screenshot({ path: join(OUT, `${TAG}-${n}.png`) });
const note = async (page, what) => lines.push(`${TAG} ${what}`);

for (const width of (process.env.WIDTHS || '1440').split(',').map(Number)) {
  const theme = process.env.THEME || 'dark';
  let page = await fresh(width, theme);
  await page.evaluate(() => { const r = [...document.querySelectorAll('.hub-row')].find((x) => (x.querySelector('.hub-row-name')?.textContent || '') === 'Contoso Sales'); r?.scrollIntoView(); });
  await note(page, '1-row: ' + await page.evaluate((s) => [...document.querySelectorAll('.hub-row')].map((r) => r.innerText.replace(/\s+/g, ' ')).join(' | '), ''));
  await shot(page, '1-row');

  await menuItem(page, 'Contoso Sales', 'Open as another account'); await wait(400);
  await note(page, '2-choice: ' + await page.evaluate(() => document.querySelector('[data-testid="hub-account-dialog"]')?.innerText.replace(/\s+/g, ' ')));
  await shot(page, '2-choice');

  await page.evaluate(() => document.querySelector('[data-testid="hub-sign-in-another"]')?.click()); await wait(400);
  await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-account-dialog"] button')].find((b) => b.textContent.trim() === 'Cancel')?.click()); await wait(400);
  const disabled = await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-row-open"]')].map((b) => b.disabled));
  await note(page, '3-cancel: Open disabled after Cancel ' + JSON.stringify(disabled));
  await shot(page, '3-cancel');
  await page.close();

  page = await fresh(width, theme);
  await page.evaluate(() => document.querySelector('[data-testid="hub-account-button"]')?.click()); await wait(400);
  await note(page, '4-menu: ' + await page.evaluate(() => document.querySelector('[data-testid="hub-account-menu"]')?.innerText.replace(/\s+/g, ' ')));
  await shot(page, '4-menu');
  await page.close();

  page = await fresh(width, theme);
  await page.evaluate(() => document.querySelector('[data-testid="hub-add-workspace"]')?.click()); await wait(800);
  await page.evaluate(() => document.querySelector('[data-testid="pick-other-ways"]')?.click()); await wait(300);
  await page.evaluate(() => { const s = document.querySelector('[data-testid="pick-authmode"]'); if (s && s.value !== 'interactive') { s.value = 'interactive'; s.dispatchEvent(new Event('change', { bubbles: true })); } }); await wait(400);
  const hasTenant = await page.evaluate(() => !!document.querySelector('[data-testid="pick-tenant"]'));
  if (hasTenant) {
    await page.type('[data-testid="pick-tenant"]', GUEST); await wait(300);
  }
  await note(page, '5-picker: tenant box for browser sign-in ' + hasTenant + '; ' + await page.evaluate(() => document.querySelector('[data-testid="pick-as"]')?.innerText));
  await shot(page, '5-picker');
  await page.close();

  page = await fresh(width, theme);
  await menuItem(page, 'Contoso Sales', 'Add SQL endpoint'); await wait(400);
  await page.evaluate(() => document.querySelector('[data-testid="hub-sql-form"]')?.scrollIntoView()); await wait(200);
  await note(page, '6-sqlform: ' + await page.evaluate(() => document.querySelector('[data-testid="account-picker"]')?.innerText.replace(/\s+/g, ' ') + ' tenant=' + document.querySelector('[data-testid="account-picker"] input')?.value));
  await shot(page, '6-sqlform');
  await page.close();

  page = await fresh(width, theme);
  await page.evaluate(() => document.querySelector('[data-testid="hub-account-button"]')?.click()); await wait(300);
  await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-account-menu"] button')].find((b) => b.textContent.trim() === 'History')?.click()); await wait(500);
  await note(page, '8-history: ' + await page.evaluate(() => document.body.innerText.replace(/\s+/g, ' ').slice(0, 400)));
  await shot(page, '8-history');
  await page.close();

  page = await fresh(width, theme);
  await page.evaluate(() => document.querySelector('[data-testid="hub-now-change"]')?.click()); await wait(400);
  await note(page, '7-jobs: ' + await page.evaluate(() => (document.querySelector('.hub-change') || document.body).innerText.replace(/\s+/g, ' ').slice(0, 300)));
  await shot(page, '7-jobs');
  await page.close();
}
await browser.close(); server.close();
writeFileSync(join(OUT, `${TAG}-fx171-drive.txt`), lines.join('\n') + '\n');
console.log(lines.join('\n'));
