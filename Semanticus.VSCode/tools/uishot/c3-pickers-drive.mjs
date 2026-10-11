// Acceptance driver for lane C3 (slice C, Connections): the Power BI Desktop instance picker and the
// workspace-then-model picker, driven through the REAL Connections hub in the harness, in three themes.
//
// NEEDS THE MOUNT. The pickers live in webview/src/connectpickers.tsx; the hub mounts them through the patch lane
// C3 hands to C2 (lane-reports/mC-c3.md). Without it the "Power BI Desktop" and "Published model" cards open the
// old paths and every scene here reports `mounted: false` rather than passing.
//
// The engine is the harness's mock, extended per scene through window.__UISHOT__ and __uishotHarness.setResponse:
// two running Desktop copies (or none), four workspaces, a model list per workspace (one of them empty), and a
// connectXmla that REMEMBERS what it connected, the way the engine's ConnectionRegistry does, so the finish-line
// scene shows a picked model arriving in the list with nothing typed. Every call the page makes is recorded and
// returned in the results, so a shot never has to be trusted for what was sent.
//
//   OUTDIR=... SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/c3-pickers-drive.mjs
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `c3-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light,hc').split(',');

// ---- the fixture the page is started with (window.__UISHOT__), owned here so connectXmla can add to it -----------
const ago = (m) => new Date(Date.now() - m * 60000).toISOString();
const START = {
  connections: [
    { id: 'cn-retail', kind: 'file', endpoint: 'C:\\Models\\Contoso Retail', modelName: 'Contoso Retail', label: 'local', useCount: 9, lastUsedUtc: ago(5) },
    { id: 'cn-sales', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales', database: 'Contoso Sales', modelName: 'Contoso Sales', authMode: 'interactive', label: 'uat', tenantId: 'contoso.com', lastAccount: 'avery@contoso.com', useCount: 4, lastUsedUtc: ago(60) },
    { id: 'cn-local', kind: 'localDesktop', endpoint: 'localhost:51000', database: 'Contoso', modelName: 'Contoso local test', label: 'local', useCount: 7, lastUsedUtc: ago(30) },
  ],
  connectionContext: {
    editing: { available: true, modelName: 'Contoso Retail', source: 'C:\\Models\\Contoso Retail', kind: 'file', sourceConnectionId: 'cn-retail', live: false },
    querying: { available: false, live: false },
    publishing: { available: false, live: false },
  },
  accountProfiles: [{ id: 'pf1', username: 'avery@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: true, signedIn: true, lastSignInUtc: ago(60), lastUseUtc: ago(30) }],
  accountProbes: [{ id: 'cn-sales', account: 'avery@contoso.com', tenantId: 'contoso.com' }],
};

// ---- the engine answers, installed after load (functions cannot cross evaluateOnNewDocument as values) ----------
function install(mode) {
  const H = window.__uishotHarness;
  const cfg = window.__UISHOT__;
  window.__c3 = { calls: [] };
  const log = (name, params) => window.__c3.calls.push([name, ...(params || [])]);
  const INSTANCES = [
    { port: 51000, title: 'Contoso Sales', dataSource: 'localhost:51000' },
    { port: 52214, title: 'AnalysisServicesWorkspace_77aa0c3e', dataSource: 'localhost:52214' },
  ];
  const WORKSPACES = [
    { id: 'ws-ops', displayName: 'Contoso Ops', type: 'Workspace' },
    { id: 'ws-fin', displayName: 'Contoso Finance', type: 'Workspace', capacityId: 'cap1' },
    { id: 'ws-me', displayName: 'avery@contoso.com', type: 'Personal' },
    { id: 'ws-sales', displayName: 'Contoso Sales', type: 'Workspace', capacityId: 'cap1' },
  ];
  const MODELS = {
    'ws-fin': [{ id: 'm-budget', displayName: 'Contoso Budget' }, { id: 'm-fin', displayName: 'Contoso Finance' }],
    'ws-sales': [{ id: 'm-sales', displayName: 'Contoso Sales' }],
    'ws-me': [{ id: 'm-scratch', displayName: 'Scratch model' }],
    'ws-ops': [],
  };
  H.setResponse('listLocalInstances', (p) => { log('listLocalInstances', p); return mode.desktop === 'none' ? [] : INSTANCES; });
  H.setResponse('listWorkspaces', (p) => {
    log('listWorkspaces', p);
    return mode.ws === 'signin' ? { __uishotError: 'Your sign-in as avery@contoso.com has run out. Sign in again to list your workspaces.' } : WORKSPACES;
  });
  H.setResponse('listWorkspaceModels', (p) => { log('listWorkspaceModels', p); return MODELS[p[0]] || []; });
  H.setResponse('probeAuthPrerequisites', (p) => {
    log('probeAuthPrerequisites', p);
    return { mode: p[0], ready: false, keyVaultSupported: false, requirements: [
      { label: 'Client id', envNames: ['AZURE_CLIENT_ID'], present: true },
      { label: 'Client secret', envNames: ['AZURE_CLIENT_SECRET'], present: false },
      { label: 'Tenant', envNames: ['AZURE_TENANT_ID'], present: false }] };
  });
  H.setResponse('connectLocal', (p) => {
    log('connectLocal', p);
    if (mode.desktop === 'fail') return { connected: false, message: 'Power BI Desktop is not running Contoso Sales any more. Open it, then look again.' };
    cfg.connectionContext.querying = { available: true, modelName: 'Contoso Sales', source: p[0], kind: 'localDesktop', endpoint: p[0], live: true };
    return { connected: true, kind: 'local', dataSource: p[0], message: 'Connected.' };
  });
  // The engine remembers what connectXmla connected (ConnectionRegistry) and makes it the Tests model.
  H.setResponse('connectXmla', (p) => {
    log('connectXmla', p);
    const id = 'cn-new-' + cfg.connections.length;
    cfg.connections.unshift({ id, kind: 'xmla', endpoint: p[0], database: p[1], modelName: p[1], authMode: p[2], tenantId: p[4] || 'contoso.com', lastAccount: 'avery@contoso.com', useCount: 1, lastUsedUtc: new Date().toISOString() });
    cfg.connectionContext.querying = { available: true, modelName: p[1], source: p[0], connectionId: id, kind: 'xmla', endpoint: p[0], database: p[1], live: true };
    return { connected: true, kind: 'xmla', dataSource: p[0], database: p[1], account: 'avery@contoso.com' };
  });
  H.setResponse('labelConnection', (p) => { log('labelConnection', p); const r = cfg.connections.find((x) => x.id === p[0]); if (r) r.label = p[1]; return r || {}; });
  H.setResponse('openLocal', (p) => { log('openLocal', p); return {}; });
}

const clickText = (words) => {
  const b = [...document.querySelectorAll('button')].find((x) => words.every((w) => (x.textContent || '').includes(w)));
  if (b) b.click();
  return !!b;
};
const clickTestid = (spec) => {
  const [testid, nth] = Array.isArray(spec) ? spec : [spec, 0];
  const all = [...document.querySelectorAll(`[data-testid="${testid}"]`)].filter((x) => x.getClientRects().length);
  if (all[nth]) all[nth].click();
  return !!all[nth];
};

// Each scene: which card opens the picker, the engine mode, then the clicks, typing and selects, in order.
// The one-page hub (lane C2) opens the pickers from its add buttons.
const DESK = ['Power BI Desktop'];
const WS = ['Workspace model'];
const JOBS = [
  { slug: 'desk-list', card: DESK, desc: 'two running copies listed, each with Use for tests and Open; the folder id is never shown' },
  { slug: 'desk-none', card: DESK, mode: { desktop: 'none' }, desc: 'none running: one plain line, Look again, and the typed-address route' },
  { slug: 'desk-address', card: DESK, mode: { desktop: 'none' }, steps: [['click', 'pick-desktop-address-toggle'], ['type', 'pick-desktop-address', 'localhost:51000']], desc: 'the typed-address route, filled in' },
  { slug: 'desk-row-failed', card: DESK, mode: { desktop: 'fail' }, steps: [['click', 'pick-desktop-tests']], desc: 'a failed test attach, said on the row that failed, in the engine words' },
  { slug: 'desk-tests-ok', card: DESK, steps: [['click', 'pick-desktop-tests']], desc: 'Use for tests on a picked copy: connectLocal gets THAT address, and Connections closes' },
  { slug: 'ws-list', card: WS, desc: 'workspaces listed as the signed-in account; My workspace first' },
  { slug: 'ws-models', card: WS, steps: [['click', ['pick-ws', 1]]], desc: 'Contoso Finance picked: its two models listed' },
  { slug: 'ws-picked', card: WS, steps: [['click', ['pick-ws', 1]], ['click', ['pick-model', 0]], ['select', 'pick-env', 'prod']], desc: 'a model and an environment picked; Connect is live' },
  { slug: 'ws-empty', card: WS, steps: [['click', ['pick-ws', 2]]], desc: 'a workspace with no models says so' },
  { slug: 'ws-signin-failed', card: WS, mode: { ws: 'signin' }, desc: 'the workspace list refused: the engine sentence, Try again, and the other sign-in ways' },
  { slug: 'ws-other', card: WS, steps: [['click', 'pick-other-ways'], ['select', 'pick-authmode', 'serviceprincipal']], desc: 'Other ways to sign in: service identity, its setup check, and the pasted address' },
  { slug: 'ws-connected', card: WS, steps: [['click', ['pick-ws', 1]], ['click', ['pick-model', 0]], ['select', 'pick-env', 'prod'], ['click', 'pick-connect']], desc: 'FINISH LINE: picked, not typed; the model arrives in the list' },
  { slug: 'ws-connected-setup', card: WS, steps: [['click', ['pick-ws', 1]], ['click', ['pick-model', 0]], ['select', 'pick-env', 'prod'], ['click', 'pick-connect']], desc: 'FINISH LINE: the new model holds the Tests job on the Now strip' },
];

const results = [];
for (const theme of THEMES) {
  for (const job of JOBS) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 2 });
    await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, JSON.parse(JSON.stringify(START)));
    let note = '';
    try {
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('[data-testid="hub-now-change"]', { timeout: 15000 });
      await page.evaluate(install, job.mode || {});
      await applyTheme(page, theme);
      await wait(500);
      if (!(await page.evaluate(clickText, job.card))) note += `card "${job.card[0]}" not found; `;
      await wait(700);
      const mounted = await page.evaluate(() => !!document.querySelector('[data-testid="pick-desktop"], [data-testid="pick-workspace"]'));
      for (const [kind, target, value] of job.steps || []) {
        let ok = true;
        if (kind === 'click') ok = await page.evaluate(clickTestid, target);
        else if (kind === 'rail') ok = await page.evaluate((v) => { const b = document.querySelector(`[data-hubnav="${v}"]`); if (b) b.click(); return !!b; }, target);
        else if (kind === 'select') ok = await page.evaluate((t, v) => {
          const e = document.querySelector(`[data-testid="${t}"]`); if (!e) return false;
          const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; set.call(e, v);
          e.dispatchEvent(new Event('change', { bubbles: true })); return true; }, target, value);
        else if (kind === 'type') {
          ok = await page.evaluate((t) => { const e = document.querySelector(`[data-testid="${t}"]`); if (e) e.focus(); return !!e; }, target);
          if (ok) await page.keyboard.type(value, { delay: 8 });
        }
        if (!ok) note += `${kind} "${JSON.stringify(target)}" not found; `;
        await wait(kind === 'click' && target === 'pick-connect' ? 1200 : 600);
      }
      const seen = await page.evaluate(() => {
        const txt = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; };
        const all = (sel) => [...document.querySelectorAll(sel)].map((e) => (e.textContent || '').replace(/\s+/g, ' ').trim());
        const connect = document.querySelector('[data-testid="pick-connect"]');
        return {
          view: document.querySelector('[data-testid="pick-desktop"]') ? 'desktop' : document.querySelector('[data-testid="pick-workspace"]') ? 'workspace' : 'hub',
          desktopRows: all('[data-testid="pick-desktop-row"]'),
          desktopNone: txt('[data-testid="pick-desktop-none"]'),
          rowError: txt('[data-testid="pick-row-error"]'),
          workspaces: all('[data-testid="pick-ws"]'),
          models: all('[data-testid="pick-model"]'),
          modelsNone: txt('[data-testid="pick-models-none"]'),
          wsFailed: txt('[data-testid="pick-ws-failed"]'),
          connectEnabled: connect ? !connect.disabled : null,
          otherWays: !!document.querySelector('[data-testid="pick-other"]'),
          pasteRoute: !!document.querySelector('[data-testid="pick-paste-address"], [data-testid="pick-desktop-address-toggle"]'),
          signInCheck: txt('[data-testid="pick-signin-check"]'),
          hubRows: all('[data-testid="hub-model-row"]').slice(0, 4),
          testsCard: all('[data-testid="hub-now"]').slice(0, 1),
          hostMessages: (window.__uishotHarness.messages || []).map((m) => m.type || m.command || JSON.stringify(m).slice(0, 60)).slice(-4),
          calls: window.__c3.calls,
          emDash: document.body.innerText.includes('\u2014'),
          engineWords: /\bXMLA\b|\bendpoint\b|\bdataset\b/i.test([...document.querySelectorAll('[data-testid="pick-desktop"], [data-testid="pick-workspace"]')].map((e) => e.innerText).join(' ')),
        };
      });
      const out = join(OUT, `c3-${job.slug}-${theme}-1280x900.png`);
      await page.screenshot({ path: out, fullPage: false });
      results.push({ theme, job: job.slug, desc: job.desc, mounted, seen, errors, note, out });
    } catch (e) {
      results.push({ theme, job: job.slug, desc: job.desc, errors, note: note + 'FATAL: ' + e.message });
    } finally { await page.close(); }
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results-c3-pickers.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => !r.mounted || r.note || r.errors.length || r.seen?.emDash || r.seen?.engineWords);
console.log(`c3 pickers: ${results.length} scenes, ${bad.length} with a problem`);
for (const r of bad) console.log(`  ${r.theme} ${r.job}: mounted=${r.mounted} ${r.note} ${r.errors.join(' | ')}${r.seen?.emDash ? ' EM DASH' : ''}${r.seen?.engineWords ? ' ENGINE WORDS' : ''}`);
