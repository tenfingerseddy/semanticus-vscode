// cx1 repro: the Connections dialog with 29 saved models (Kane, 3 Nov). Shoots where Connect and the account choice land.
//   OUTDIR=<folder> node tools/uishot/cx1-hub29-drive.mjs
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
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `cx1-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light').split(',');

// cx1 (Kane, 3 Nov, 1.7.3): "we shouldnt have to scroll to see connect". 29 saved connections, Contoso-style names, two of them
// duplicates (the same model under another spelling of its address). Most published rows have no live sign-in on this device,
// as after a restart, so their Open asks which account to use.
const ago = (m) => new Date(Date.now() - m * 60000).toISOString();
const WS = ['Contoso Finance', 'Contoso Finance [Dev]', 'Fabrikam Retail', 'Northwind Ops', 'Adventure Works Demo', 'Tailspin Analytics'];
const MODELS = ['SM_Revenue', 'SM_Costs', 'Sales', 'Inventory', 'SM_Headcount', 'Marketing', 'Operations', 'SM_Forecast'];
const connections = [];
let i = 0;
for (const w of WS) for (const m of MODELS.slice(0, 2 + (i % 3))) {
  if (connections.length >= 17) break;
  connections.push({ id: 'cx' + i, kind: 'xmla', endpoint: `powerbi://api.powerbi.com/v1.0/myorg/${w}`, database: m, modelName: m, authMode: 'interactive', tenantId: 'contoso.com', label: i % 4 === 0 ? 'dev' : undefined, useCount: 3, lastUsedUtc: ago(i * 60) }); i++;
}
connections.push({ id: 'cx-dup1', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Finance', database: 'SM_Revenue', modelName: 'SM_Revenue', authMode: 'interactive', tenantId: 'contoso.com', useCount: 1, lastUsedUtc: ago(i++ * 60) });
connections.push({ id: 'cx-dup2', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Fabrikam Retail/', database: 'Sales', modelName: 'Sales', authMode: 'interactive', tenantId: 'contoso.com', useCount: 1, lastUsedUtc: ago(i++ * 60) });
for (let k = 0; k < 10; k++) connections.push({ id: 'cf' + k, kind: 'file', endpoint: `/home/user/Downloads/contoso-extract${1 + (k % 2)}/contoso-reports-main/${MODELS[k % MODELS.length]}.SemanticModel`, modelName: 'Model', useCount: 1, lastUsedUtc: ago(i++ * 60) });
const START = {
  connections,
  connectionContext: { editing: { available: true, modelName: 'SM_Revenue', kind: 'xmla', sourceConnectionId: 'cx0', connectionId: 'cx0', live: true }, querying: { available: false, live: false }, publishing: { available: false, live: false } },
  accountProfiles: [{ id: 'pf1', username: 'avery@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: true, signedIn: true, lastSignInUtc: ago(60), lastUseUtc: ago(30) }],
  accountProbes: [{ id: 'cx0', account: 'avery@contoso.com', tenantId: 'contoso.com' }],
};
function install() {
  const H = window.__uishotHarness;
  const ws = [];
  for (let n = 0; n < 29; n++) ws.push({ id: 'w' + n, displayName: ['Contoso', 'Fabrikam', 'Northwind', 'Tailspin', 'Adventure Works'][n % 5] + ' ' + ['Finance', 'Retail', 'Ops', 'Sales', 'Analytics', 'Demo'][n % 6] + (n > 9 ? ' ' + n : ''), type: 'Workspace' });
  H.setResponse('listWorkspaces', () => ws);
  H.setResponse('listWorkspaceModels', () => [{ id: 'm1', displayName: 'SM_Revenue' }, { id: 'm2', displayName: 'SM_Costs' }]);
  H.setResponse('openLive', () => new Promise(() => {}));   // an open that does not answer: what the person sees while it runs
}
// Where a control is: inside the window and inside its scrolling box, or only after a scroll.
const where = (sel, nth) => {
  const all = [...document.querySelectorAll(sel)].filter((x) => x.getClientRects().length);
  const e = all[nth || 0];
  if (!e) return { found: false };
  const r = e.getBoundingClientRect();
  let box = e.parentElement;
  while (box && !(box.scrollHeight > box.clientHeight + 2 && /(auto|scroll)/.test(getComputedStyle(box).overflowY))) box = box.parentElement;
  const b = box ? box.getBoundingClientRect() : { top: 0, bottom: innerHeight };
  const inView = r.top >= Math.max(0, b.top) - 1 && r.bottom <= Math.min(innerHeight, b.bottom) + 1;
  return { found: true, text: (e.textContent || '').trim().slice(0, 60), top: Math.round(r.top), bottom: Math.round(r.bottom), viewport: innerHeight, boxTop: Math.round(b.top), boxBottom: Math.round(b.bottom), inView, scrollTop: box ? Math.round(box.scrollTop) : 0 };
};
const clickNth = (sel, nth) => { const all = [...document.querySelectorAll(sel)].filter((x) => x.getClientRects().length); if (all[nth]) all[nth].click(); return !!all[nth]; };
const scrollTo = (sel, nth) => { const all = [...document.querySelectorAll(sel)]; all[nth]?.scrollIntoView({ block: 'center' }); return !!all[nth]; };

const SCENES = [
  { slug: 'list', desc: 'the list of 29 saved models, as Kane saw it', run: async () => ({}) },
  { slug: 'open-row-15', desc: 'scrolled to row 15 and pressed Open: its account choice opens at the top of the list, out of view', run: async (page) => {
      await page.evaluate(scrollTo, '[data-testid="hub-row-open"]', 14); await wait(300);
      const pressed = await page.evaluate(clickNth, '[data-testid="hub-row-open"]', 14); await wait(700);
      return { pressed, dialog: await page.evaluate(where, '[data-testid="hub-account-dialog"]', 0), primary: await page.evaluate(where, '[data-testid="hub-open-default"]', 0) };
    } },
  { slug: 'workspace-29', desc: 'Workspace model with 29 workspaces: Connect sits below the whole list', run: async (page) => {
      await page.evaluate(clickNth, '[data-testid="hub-add-workspace"]', 0); await wait(900);
      await page.evaluate(clickNth, '[data-testid="pick-ws"]', 3); await wait(600);
      await page.evaluate(clickNth, '[data-testid="pick-model"]', 0); await wait(400);
      return { connect: await page.evaluate(where, '[data-testid="pick-connect"]', 0) };
    } },
  { slug: 'opening', desc: 'Open on a signed-in row while the open runs: every Open greys out and nothing says what is happening', run: async (page) => {
      const pressed = await page.evaluate(clickNth, '[data-testid="hub-row-open"]', 1); await wait(900);
      return { pressed, disabledOpens: await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-row-open"]')].filter((b) => b.disabled).length),
        statusText: await page.evaluate(() => (document.querySelector('.hub-main [role="status"], .hub-main [aria-live]')?.textContent || '').trim()) };
    } },
];
const results = [];
for (const theme of THEMES) for (const [vw, vh] of [[1280, 800]]) for (const scene of SCENES) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 1 });
  const start = JSON.parse(JSON.stringify(START));
  if (scene.slug === 'opening') start.accountProbes = start.connections.filter((c) => c.kind === 'xmla').map((c) => ({ id: c.id, account: 'avery@contoso.com', tenantId: 'contoso.com' }));
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, start);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="hub-list"]', { timeout: 15000 });
  await page.evaluate(install);
  await applyTheme(page, theme);
  await wait(500);
  const seen = await scene.run(page);
  seen.rows = await page.evaluate(() => document.querySelectorAll('[data-testid="hub-model-row"]').length);
  const out = join(OUT, `cx1-${scene.slug}-${theme}-${vw}.png`);
  await page.screenshot({ path: out });
  results.push({ theme, vw, scene: scene.slug, desc: scene.desc, seen, errors, out });
  await page.close();
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'cx1-results.json'), JSON.stringify(results, null, 2));
for (const r of results) console.log(r.theme, r.vw, r.scene, JSON.stringify(r.seen), r.errors.length ? 'ERR ' + r.errors[0] : '');
