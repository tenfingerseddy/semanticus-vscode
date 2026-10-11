// Driven proof for starting a model from a SQL source (plan statement check, 28 Sep 2026, defects 4, 6 and 9).
// Serves the built bundle, drives the real harness with real clicks, records the RPC calls the page makes, and writes
// a PNG per case and theme plus results.json. The harness's two stub SQL endpoints (SQL_SOURCES, the same records
// Connections lists) stand in for the one the person saved: no browser sign-in and no network are involved.
//
//   OUTDIR=<dir> SPEC_JSON=<j8 draft from the engine's own mapping> node sql-start-drive.mjs
//
// Cases:
//   hub-sql     Connections: From a SQL source beside New blank model opens the start plan's own form at once (no
//               blank-model name step since the M11 wiring step): the saved endpoints, the model name, an empty folder.
//               Read the source calls startModelFromSql with that endpoint, the name and the folder, and NOTHING else
//               (no createModel), then the hub closes and the Plan shows the start plan. The engine is played by
//               fixtures/startplan-fixture.mjs.
//   standalone-sql  The Connections panel opened on From a SQL source with no model open (the host's first-run path, initialView createsql):
//               ONLY the start form shows. The New blank model card (its name box and its Create button) must not open above it
//               (plan statement check 4, defect 6).
//   draft       A model that already HAS tables keeps the wizard's SQL step (the two-step path): read the tables on the
//               first saved endpoint, and autogenerateSpecFromFabric is called with THAT record's server, database,
//               sign-in and tenant; the returned draft shows the notes on the links it turned off.
//   find-tool   A 792px Studio (a 1440 window with the sidebar open): More, Find a tool. The menu must sit inside the
//               window, and on an empty model its From a SQL source row must land on the same start form.
//   ask         Ctrl J, "model spec": RECORDED ONLY. The lane wrote it when the bar routed (routeAsk); since Kane's ruling
//               of 27 Sep Ctrl J drafts every line (composeTask) and routes nothing, so the record shows a draft (m11 join).
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots', 'sql-start');
mkdirSync(OUT, { recursive: true });
const SPEC = process.env.SPEC_JSON ? JSON.parse(readFileSync(process.env.SPEC_JSON, 'utf8')) : null;
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

// The start plan's mock engine, inlined as startplan-drive.mjs does: a start answers with the plan, and the Plan reads it.
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'startplan-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const installStartPlan = (page) => page.evaluate(`${FIXTURE_SRC}
  window.__sp = makeStartPlanState({ free: true }); window.__spNone = true;
  window.__uishotHarness.setResponse('getStartPlan', () => (window.__spNone ? { exists: false } : startPlanView(window.__sp)));
  // As the engine does (LocalEngine.StartModelFromSqlAsync, NotifyStartPlanChanged): the start answers with the plan AND
  // broadcasts startPlan/didChange, which is how a Plan page that was already up hears of it.
  window.__uishotHarness.setResponse('startModelFromSql', () => { window.__spNone = false;
    setTimeout(() => window.__uishotHarness.dispatch({ type: 'startPlanDidChange', payload: {} }), 50); return startPlanView(window.__sp); });
  true`);
const rpcCalls = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).filter((m) => m && m.type === 'rpc').map((m) => ({ method: m.method, params: m.params })));
const hostCommands = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).filter((m) => m && m.type === 'runCommand').map((m) => m.command));
const text = (page) => page.evaluate(() => document.body.innerText);
const clickTestid = (page, id) => page.evaluate((t) => { const b = document.querySelector(`[data-testid="${t}"]`); if (b) b.click(); return !!b; }, id);
const clickText = (page, label, root = 'body') => page.evaluate((t, r) => {
  const b = [...document.querySelectorAll(`${r} button, ${r} [role="menuitem"]`)].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, label, root);
const typeInto = async (page, selector, value) => {
  await page.focus(selector);
  await page.keyboard.type(value);
};

async function open(vw, vh, query) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 2 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${query ? '?' + query : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(1000);
  if (SPEC) await page.evaluate((spec) => window.__uishotHarness.setResponse('autogenerateSpecFromFabric', { version: 7, source: 'autogenerate-fabric', spec }), SPEC);
  return { page, errors };
}

async function hubToStartForm(page) {
  await page.click('[data-testid="connections-editing"]');
  await page.waitForSelector('[role="dialog"][aria-label="Connections"]', { timeout: 15000 });
  await wait(400);
  const adds = await page.evaluate(() => {
    const blank = document.querySelector('[data-testid="hub-add-blank"]');
    const sql = document.querySelector('[data-testid="hub-add-sql"]');
    return { blank: !!blank, sql: !!sql, beside: !!blank && !!sql && blank.nextElementSibling === sql, sqlText: sql ? sql.textContent.trim() : null };
  });
  await clickTestid(page, 'hub-add-sql');
  await page.waitForSelector('[role="dialog"][aria-label="Connections"] [data-testid="sp-form"]', { timeout: 10000 });
  await wait(400);
  const card = await text(page);
  const nameStep = !!(await page.$('[data-testid="hub-create-name"]'));
  await typeInto(page, '[data-testid="sp-form-name"]', 'Contoso Consulting');
  await typeInto(page, '[data-testid="sp-form-folder"]', '/home/kane/models/Contoso Consulting');
  return { adds, nameStep, formTitle: card.includes('Start a model from a SQL source'), cardSays: card.includes('no password is kept'),
    savedListed: await page.evaluate(() => [...document.querySelectorAll('[data-testid="sp-form"] .sp-pick label')].map((x) => x.innerText.replace(/\s+/g, ' ').trim())) };
}

const results = [];
const THEMES = ['dark', 'light'];

for (const theme of THEMES) {
  // ---- hub-sql: Connections opens the start form at once; Read the source starts the model and the plan --------------
  {
    const { page, errors } = await open(1440, 900, 'spec=empty');
    await applyTheme(page, theme);
    await installStartPlan(page);
    const r = { case: 'hub-sql', theme, errors, shots: [] };
    try {
      Object.assign(r, await hubToStartForm(page));
      let out = join(OUT, `hub-sql-form-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      if (theme === 'dark') { await checkScreen(page, 'sql-start-hub-form'); await applyTheme(page, theme); }
      await clickTestid(page, 'sp-form-start');
      await page.waitForFunction(() => !document.querySelector('[role="dialog"][aria-label="Connections"]'), { timeout: 15000 });
      await page.waitForSelector('[data-testid="sp-page"]', { timeout: 15000 });
      await wait(600);
      const calls = await rpcCalls(page);
      r.startCalled = calls.filter((c) => c.method === 'startModelFromSql').map((c) => c.params);
      r.createCalled = calls.filter((c) => c.method === 'createModel').length;
      r.onPlan = true;
      out = join(OUT, `hub-sql-plan-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      const req = r.startCalled[0]?.[0] ?? {};
      r.verdict = [
        r.adds.beside ? null : 'From a SQL source is not beside New blank model',
        !r.nameStep ? null : 'a blank-model name step came first',
        r.formTitle ? null : 'the start form did not open in the hub',
        r.startCalled.length === 1 ? null : `startModelFromSql called ${r.startCalled.length} times`,
        r.createCalled === 0 ? null : 'createModel was called: a blank model was made first',
        req.name === 'Contoso Consulting' && req.folder === '/home/kane/models/Contoso Consulting' && req.server && req.database ? null : `request ${JSON.stringify(req)}`,
        r.startCalled[0]?.[1] === 'human' ? null : 'the start was not the person\'s',
      ].filter(Boolean);
      if (r.verdict.length) r.threw = r.verdict.join('; ');
    } catch (e) { r.threw = e.message; try { const out = join(OUT, `hub-sql-FAILED-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out); } catch { /* closed */ } }
    results.push(r); await page.close();
  }

  // ---- standalone-sql: the panel opened on From a SQL source shows the start form alone ---------------------------------
  {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    const r = { case: 'standalone-sql', theme, errors, shots: [] };
    try {
      await page.setViewport({ width: 1100, height: 800, deviceScaleFactor: 2 });
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections&section=createsql&home=nomodel`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('[data-testid="sp-form"]', { timeout: 15000 });
      await wait(500);
      await applyTheme(page, theme);
      r.blankCard = await page.evaluate(() => ({
        nameBox: !!document.querySelector('[data-testid="hub-create-name"]'),
        createButton: !!document.querySelector('[data-testid="hub-create-submit"]'),
        cardTitles: [...document.querySelectorAll('.sem-card-title')].map((x) => x.textContent.trim()).filter((t) => t === 'New blank model'),
      }));
      r.formAbove = await page.evaluate(() => { const f = document.querySelector('[data-testid="sp-form"]'); const t = f ? f.getBoundingClientRect().top : -1; return Math.round(t); });
      const out = join(OUT, `standalone-sql-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      if (theme === 'dark') { await checkScreen(page, 'sql-start-standalone'); await applyTheme(page, theme); }
      r.verdict = [
        r.blankCard.nameBox || r.blankCard.createButton || r.blankCard.cardTitles.length ? 'the New blank model card opened above the start form' : null,
      ].filter(Boolean);
      if (r.verdict.length) r.threw = r.verdict.join('; ');
    } catch (e) { r.threw = e.message; try { const out = join(OUT, `standalone-sql-FAILED-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out); } catch { /* closed */ } }
    results.push(r); await page.close();
  }

  // ---- draft: a model WITH tables keeps the wizard's SQL step (the two-step path) -----------------------------------
  {
    const { page, errors } = await open(1440, 900, 'coldNav=spec:start:sql');
    await applyTheme(page, theme);
    const r = { case: 'draft', theme, errors, shots: [] };
    try {
      await page.waitForSelector('[data-testid="spec-sql-step"]', { timeout: 15000 });
      await page.waitForFunction(() => document.querySelectorAll('[data-testid="spec-sql-saved"]').length > 0, { timeout: 10000 });
      r.savedListed = await page.evaluate(() => [...document.querySelectorAll('[data-testid="spec-sql-saved"]')].map((x) => x.innerText.replace(/\s+/g, ' ').trim()));
      r.typedFieldsHidden = !(await text(page)).includes('Tenant (optional)');
      let out = join(OUT, `wizard-sql-step-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      if (theme === 'dark') { await checkScreen(page, 'sql-start-step'); await applyTheme(page, theme); }

      await clickTestid(page, 'spec-sql-type');
      await wait(300);
      r.fallbackShowsTyping = (await text(page)).includes('Tenant (optional)');
      out = join(OUT, `wizard-sql-typed-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      await clickText(page, 'Use a SQL endpoint from Connections');
      await page.waitForFunction(() => document.querySelectorAll('[data-testid="spec-sql-saved"]').length > 0, { timeout: 5000 });

      await clickText(page, 'Read the tables');
      await wait(1200);
      r.draftCall = (await rpcCalls(page)).filter((c) => c.method === 'autogenerateSpecFromFabric').map((c) => c.params);
      if (SPEC) {
        await page.waitForFunction(() => document.querySelectorAll('[data-testid="spec-rel-note"]').length > 0, { timeout: 10000 });
        r.relNotes = await page.evaluate(() => [...document.querySelectorAll('[data-testid="spec-rel-note"]')].map((x) => x.innerText.trim()));
        r.dateChip = await page.evaluate(() => (document.body.innerText.match(/date table: [^\n]*/) || [null])[0]);
        const rels = await page.$('[data-testid="spec-rel-note"]');
        if (rels) await rels.evaluate((el) => el.scrollIntoView({ block: 'center' }));
        await wait(300);
        out = join(OUT, `wizard-draft-notes-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
        if (theme === 'dark') { await checkScreen(page, 'sql-start-draft'); await applyTheme(page, theme); }
      }
    } catch (e) { r.threw = e.message; try { const out = join(OUT, `hub-sql-FAILED-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out); } catch { /* closed */ } }
    results.push(r); await page.close();
  }

  // ---- find-tool at 792 wide, the Studio a 1440 window with the sidebar open leaves --------------------------------
  {
    const { page, errors } = await open(792, 900, 'spec=empty');
    await applyTheme(page, theme);
    const r = { case: 'find-tool-792', theme, errors, shots: [] };
    try {
      // The header's Find a tool button left on 8 Oct 2026: the list opens from Help > All tools.
      r.via = 'Help';
      await clickText(page, 'Help', '.studio-chrome'); await wait(300);
      await clickText(page, 'All tools', '[role="menu"][aria-label="Help"]');
      await page.waitForSelector('[role="menu"][aria-label="All tools"]', { timeout: 5000 });
      await wait(300);
      r.menu = await page.evaluate(() => {
        const m = document.querySelector('[role="menu"][aria-label="All tools"]').getBoundingClientRect();
        const rows = [...document.querySelectorAll('[role="menu"][aria-label="All tools"] [role="menuitem"]')];
        const cut = rows.filter((b) => { const x = b.getBoundingClientRect(); return x.left < 0 || x.right > window.innerWidth; }).map((b) => b.textContent.trim());
        return { left: Math.round(m.left), right: Math.round(m.right), window: window.innerWidth, cutRows: cut,
          planRows: [...document.querySelectorAll('[role="menu"][aria-label="All tools"] section[aria-label="Plan"] [role="menuitem"]')].map((b) => b.textContent.trim()) };
      });
      const out = join(OUT, `find-tool-792-${theme}.png`); await page.screenshot({ path: out }); r.shots.push(out);
      await clickText(page, 'From a SQL source', '[role="menu"][aria-label="All tools"]');
      await page.waitForSelector('[data-testid="sp-form"]', { timeout: 10000 });
      r.landedOnStartForm = true;
    } catch (e) { r.threw = e.message; }
    results.push(r); await page.close();
  }
}

// ---- ask: recorded only (Ctrl J routes nothing since 27 Sep) ------------------------------------------------------
{
  const { page, errors } = await open(1440, 900, 'spec=empty&tier=pro');
  const r = { case: 'ask', errors, shots: [] };
  try {
    await page.evaluate(() => window.__uishotHarness.setResponse('routeAsk', { tool: 'spec', area: 'plan', confidence: 1, asked: false }));
    await page.keyboard.down('Control'); await page.keyboard.press('j'); await page.keyboard.up('Control');
    const bar = await page.waitForSelector('[data-testid="ask-bar-input"]', { timeout: 5000 }).catch(() => null);
    if (!bar) r.note = 'no ask bar input (the harness tier may show the Pro pill)';
    else {
      await page.keyboard.type('model spec');
      await page.keyboard.press('Enter');
      await wait(900);
      const calls = await rpcCalls(page);
      r.routeAsk = calls.filter((c) => c.method === 'routeAsk').map((c) => c.params);
      r.composeTask = calls.filter((c) => c.method === 'composeTask').length;
      const out = join(OUT, 'ask-model-spec.png'); await page.screenshot({ path: out }); r.shots.push(out);
    }
  } catch (e) { r.threw = e.message; }
  r.hostCommands = await hostCommands(page).catch(() => []);
  results.push(r); await page.close();
}

await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
for (const r of results) console.log(JSON.stringify({ ...r, shots: r.shots.length }));
const screen = screenFailures();
console.log(screen.length ? `screen check: ${screen.length} problem(s)\n  ${screen.join('\n  ')}` : 'screen check: clean');
if (screen.length || results.some((r) => r.threw)) process.exit(1);
