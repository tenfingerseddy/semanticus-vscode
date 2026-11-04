// cx2: the Connections bar (option A, Kane 3 Nov 11:44). With 29 saved models and 29 workspaces it measures where the one Open
// (Connect), the account place and the account choice land, plays an open with the engine's steps, and presses Cancel.
// It exits 1 when Open or Connect, the account choice, the step or Cancel is out of view or missing, so it is a test as well as a shot.
//   OUTDIR=<folder> [WEBROOT=<another Semanticus.VSCode>] [TAG=before|after] node tools/uishot/cx2-bar-drive.mjs
// WEBROOT serves another build (the before shots use fix/cx1's), with this file's scenes and the same fixture.
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
const webRoot = resolve(process.env.WEBROOT || resolve(here, '..', '..'));
const OUT = process.env.OUTDIR || resolve(here, '..', '..', 'shots');
const TAG = process.env.TAG || 'after';
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
const SCRATCH = resolve(process.env.TMPDIR || resolve(OUT, '..', 'tmp'), `cx2-chrome-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light').split(',');
const SIZES = (process.env.SIZES || '1280x800,390x844').split(',').map((s) => s.split('x').map(Number));

// The cx1 fixture: 29 saved models, Contoso-style names, two duplicates, ten local folders.
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
  accountProfiles: [
    { id: 'pf1', username: 'avery@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: true, signedIn: true, lastSignInUtc: ago(60), lastUseUtc: ago(30) },
    { id: 'pf2', username: 'megan@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: false, signedIn: true, lastSignInUtc: ago(90), lastUseUtc: ago(80) },
  ],
  accountProbes: [{ id: 'cx0', account: 'avery@contoso.com', tenantId: 'contoso.com' }],
};
function install() {
  const H = window.__uishotHarness;
  const ws = [];
  for (let n = 0; n < 29; n++) ws.push({ id: 'w' + n, displayName: ['Contoso', 'Fabrikam', 'Northwind', 'Tailspin', 'Adventure Works'][n % 5] + ' ' + ['Finance', 'Retail', 'Ops', 'Sales', 'Analytics', 'Demo'][n % 6] + (n > 9 ? ' ' + n : ''), type: 'Workspace' });
  H.setResponse('listWorkspaces', () => ws);
  H.setResponse('listWorkspaceModels', () => [{ id: 'm1', displayName: 'SM_Revenue' }, { id: 'm2', displayName: 'SM_Costs' }, { id: 'm3', displayName: 'Inventory' }]);
  H.setResponse('sessionInfo', () => ({ sessionId: 's1', hasUnsavedChanges: false }));
  H.setResponse('cancelOpen', () => ({ stopped: true, state: 'stopped' }));
  H.hold('openLive');   // the open runs until the scene answers it
}
// Where a control is: inside the window and the dialog, and inside its scrolling box.
const where = (sel, nth) => {
  const all = [...document.querySelectorAll(sel)].filter((x) => x.getClientRects().length);
  const e = all[nth || 0];
  if (!e) return { found: false };
  const r = e.getBoundingClientRect();
  const dlg = document.querySelector('[role="dialog"][aria-label="Connections"] > div')?.getBoundingClientRect() ?? { top: 0, bottom: innerHeight, left: 0, right: innerWidth };
  let box = e.parentElement;
  while (box && !(box.scrollHeight > box.clientHeight + 2 && /(auto|scroll)/.test(getComputedStyle(box).overflowY))) box = box.parentElement;
  const b = box ? box.getBoundingClientRect() : { top: 0, bottom: innerHeight };
  const inView = r.top >= Math.max(0, b.top, dlg.top) - 1 && r.bottom <= Math.min(innerHeight, b.bottom, dlg.bottom) + 1 && r.left >= dlg.left - 1 && r.right <= dlg.right + 1 && r.width > 0;
  return { found: true, text: (e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 90), top: Math.round(r.top), bottom: Math.round(r.bottom), viewport: innerHeight, inView, scrolls: !!box };
};
const clickNth = (sel, nth) => { const all = [...document.querySelectorAll(sel)].filter((x) => x.getClientRects().length); if (all[nth]) { all[nth].scrollIntoView({ block: 'center' }); all[nth].click(); } return !!all[nth]; };
const barText = () => (document.querySelector('[data-testid="hub-bar"]')?.textContent || '').replace(/\s+/g, ' ').trim();
const rpcCalls = (method) => window.__uishotHarness.messages.filter((m) => m && m.type === 'rpc' && m.method === method).map((m) => m.params);
const progress = (done, note, cancellable = true) => ({ type: 'progressDidChange', payload: { opKey: 'open', runId: 'run1', done, total: 5, note, origin: 'human', cancellable } });

// A failed expectation is recorded with the scene, and the run exits 1 at the end.
const fails = [];
const expect = (ok, scene, what, seen) => { if (!ok) fails.push({ scene, what, seen }); return ok; };

const SCENES = [
  { slug: 'list', desc: '29 saved models, a row far down picked: the bar names it and holds Open', run: async (page, s) => {
      const picked = await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 25); await wait(400);
      const open = await page.evaluate(where, '[data-testid="hub-open"]', 0);
      const local = await page.evaluate(barText);
      expect(open.found && open.inView, s, 'Open in view after picking row 26', open);
      expect(/Opens from this computer\. No account is needed\./.test(local), s, 'a local row says no account is needed', local);
      await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 18); await wait(300);   // the last published row
      const change = await page.evaluate(where, '[data-testid="hub-bar-change"]', 0);
      expect(change.found && change.inView, s, 'the account place with Change in view', change);
      expect(/Open as/.test(await page.evaluate(barText)), s, 'the bar names the account', await page.evaluate(barText));
      return { picked, open, local, change, bar: await page.evaluate(barText) };
    } },
  { slug: 'account', desc: 'a published row with no live sign-in, Change pressed: the account choice opens in the bar, in view', run: async (page, s) => {
      await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 14); await wait(300);
      await page.evaluate(clickNth, '[data-testid="hub-bar-change"]', 0); await wait(500);
      const dialog = await page.evaluate(where, '[data-testid="hub-account-dialog"]', 0);
      const primary = await page.evaluate(where, '[data-testid="hub-open-default"]', 0);
      expect(dialog.found && dialog.top >= 0 && dialog.bottom <= dialog.viewport, s, 'the account choice in view', dialog);
      expect(primary.found && primary.inView, s, 'its Use button in view', primary);
      return { dialog, primary };
    } },
  { slug: 'workspace', desc: 'Workspace model with 29 workspaces, a model picked: Connect and the account in the bar, in view', run: async (page, s) => {
      await page.evaluate(clickNth, '[data-testid="hub-add-workspace"]', 0); await wait(900);
      await page.evaluate(clickNth, '[data-testid="pick-ws"]', 27); await wait(600);
      await page.evaluate(clickNth, '[data-testid="pick-model"]', 1); await wait(400);
      const connect = await page.evaluate(where, '[data-testid="pick-connect"]', 0);
      expect(connect.found && connect.inView, s, 'Connect in view with 29 workspaces', connect);
      const bar = await page.evaluate(barText);
      expect(/Connect as/.test(bar), s, 'the bar says who it connects as', bar);
      return { connect, bar };
    } },
  { slug: 'opening', signedIn: true, desc: 'Open on a row while the engine reads the model: "Step 3 of 5", five marks, Cancel', run: async (page, s) => {
      await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 1); await wait(200);
      await page.evaluate(clickNth, '[data-testid="hub-open"]', 0); await wait(300);
      for (const [d, n] of [[1, 'Signing in'], [2, 'Connecting to the workspace'], [3, 'Reading the model definition']]) { await page.evaluate((m) => window.__uishotHarness.dispatch(m), progress(d, n)); await wait(150); }
      await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 20); await wait(300);   // a pick while it opens
      const step = await page.evaluate(where, '[data-testid="hub-bar-step"]', 0);
      const cancel = await page.evaluate(where, '[data-testid="hub-bar-cancel"]', 0);
      expect(step.found && step.inView && /Step 3 of 5 · Reading the model definition/.test(step.text), s, 'the live step in view', step);
      expect(cancel.found && cancel.inView, s, 'Cancel in view', cancel);
      expect(/Cancel this open to open/.test(await page.evaluate(barText)), s, 'a pick during the open says to cancel first', await page.evaluate(barText));
      return { step, cancel, bar: await page.evaluate(barText) };
    } },
  { slug: 'cancelled', signedIn: true, desc: 'Cancel pressed during the open: the engine is asked to stop it, and the bar says nothing changed', run: async (page, s) => {
      await page.evaluate(clickNth, '[data-testid="hub-model-row"]', 1); await wait(200);
      await page.evaluate(clickNth, '[data-testid="hub-open"]', 0); await wait(300);
      await page.evaluate((m) => window.__uishotHarness.dispatch(m), progress(2, 'Connecting to the workspace')); await wait(200);
      await page.evaluate(clickNth, '[data-testid="hub-bar-cancel"]', 0); await wait(300);
      const asked = await page.evaluate(rpcCalls, 'cancelOpen');
      expect(asked.length > 0 && asked[0][0] === 'run1', s, 'Cancel asks the engine to stop this run', asked);
      await page.evaluate(() => { const H = window.__uishotHarness; H.setResponse('openLive', { __uishotError: 'Stopped. SM_Costs did not open, and nothing changed.' }); H.release('openLive'); }); await wait(500);
      const bar = await page.evaluate(barText);
      expect(/Stopped\. .* did not open, and nothing changed\./.test(bar), s, 'the stopped line in the bar', bar);
      expect((await page.evaluate(where, '[data-testid="hub-open"]', 0)).found, s, 'Open is back', null);
      return { asked, bar };
    } },
  { slug: 'ws-opening', desc: 'Connect on the Workspace model screen while it signs in: the step and Cancel in the bar', run: async (page, s) => {
      await page.evaluate(clickNth, '[data-testid="hub-add-workspace"]', 0); await wait(900);
      await page.evaluate(clickNth, '[data-testid="pick-ws"]', 27); await wait(600);
      await page.evaluate(clickNth, '[data-testid="pick-model"]', 0); await wait(300);
      await page.evaluate(clickNth, '[data-testid="pick-connect"]', 0); await wait(300);
      await page.evaluate((m) => window.__uishotHarness.dispatch(m), progress(1, 'Signing in')); await wait(3600);
      const step = await page.evaluate(where, '[data-testid="hub-bar-step"]', 0);
      const cancel = await page.evaluate(where, '[data-testid="hub-bar-cancel"]', 0);
      expect(step.found && step.inView && /Step 1 of 5 · Signing in/.test(step.text), s, 'the step in view on the Workspace model screen', step);
      expect(cancel.found && cancel.inView, s, 'Cancel in view on the Workspace model screen', cancel);
      expect(/Finish the sign-in in your browser/.test(await page.evaluate(barText)), s, 'the sign-in step says it waits for the browser', await page.evaluate(barText));
      return { step, cancel, bar: await page.evaluate(barText) };
    } },
];
const only = process.env.SCENES ? process.env.SCENES.split(',') : null;
const results = [];
for (const theme of THEMES) for (const [vw, vh] of SIZES) for (const scene of SCENES) {
  if (only && !only.includes(scene.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 1 });
  const start = JSON.parse(JSON.stringify(START));
  // A row with a live sign-in opens at once; one without asks which account first (the account scene).
  if (scene.signedIn) start.accountProbes = start.connections.filter((c) => c.kind === 'xmla').map((c) => ({ id: c.id, account: 'avery@contoso.com', tenantId: 'contoso.com' }));
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, start);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="hub-list"]', { timeout: 15000 });
  await page.evaluate(install);
  await applyTheme(page, theme);
  await wait(500);
  const name = `${scene.slug}-${theme}-${vw}`;
  let seen;
  try { seen = await scene.run(page, name); } catch (e) { seen = { error: String(e) }; fails.push({ scene: name, what: 'the scene ran', seen: String(e) }); }
  seen.rows = await page.evaluate(() => document.querySelectorAll('[data-testid="hub-model-row"]').length);
  const overflow = await page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => e.getClientRects().length && e.getBoundingClientRect().right > innerWidth + 1).length);
  seen.overflow = overflow;
  const out = join(OUT, `${TAG}-${name}.png`);
  await page.screenshot({ path: out });
  results.push({ theme, vw, scene: scene.slug, desc: scene.desc, seen, errors, out });
  if (errors.length) fails.push({ scene: name, what: 'no page errors', seen: errors });
  await page.close();
}
await browser.close(); server.close();
writeFileSync(join(OUT, `${TAG}-results.json`), JSON.stringify({ webRoot, results, fails }, null, 2));
for (const r of results) console.log(r.theme, r.vw, r.scene, JSON.stringify(r.seen).slice(0, 300), r.errors.length ? 'ERR ' + r.errors[0] : '');
console.log(fails.length ? `FAIL ${fails.length}: ` + fails.map((f) => `${f.scene}: ${f.what}`).join('; ') : 'PASS');
process.exit(fails.length ? 1 : 0);
