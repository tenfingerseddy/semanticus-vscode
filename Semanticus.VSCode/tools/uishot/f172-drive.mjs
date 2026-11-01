// f172: the four 1.7.2 small fixes in the harness, light and dark, at 1280 and 390 (the mockup's widths).
//   f1  "Workspace model" as the saved account, "Use another account", then Cancel while the browser waits: back at the start
//   f2  the Studio header: the account and the tenant under the model name (desktop) or in the action row (phone)
//   f3  History with a long failure reason: the first row opened, nothing cut
//   f4  a SQL endpoint form opened after a first read of the saved sources failed: it opens on the saved endpoint
// The engine is the harness mock. Contoso names only; no sign-in ever answers.
//   OUTDIR=<dir> [WIDTHS=1280,390] [THEMES=light,dark] node tools/uishot/f172-drive.mjs
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
const SCRATCH = resolve(process.env.HOME ?? '.', '.cache', 'semanticus-uishot', `f172-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = []; const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); };
const text = () => (document.body.innerText || '').replace(/\s+/g, ' ');

async function fresh(width, theme, query, cfg) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width, height: 900, deviceScaleFactor: 2 });
  if (cfg) await page.evaluateOnNewDocument((c) => { window.__UISHOT__ = c; }, cfg);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await applyTheme(page, theme);
  await wait(400);
  return { page, errors };
}
const noSideways = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);

const ALEX = { accountProfiles: [{ id: 'pf-alex', username: 'alex@contoso.com', tenantId: null, accountTenantId: 'contoso.com', family: 'interactive', isDefault: true, signedIn: true, lastSignInUtc: '2026-10-31T03:00:00Z', lastUseUtc: '2026-10-31T03:00:00Z' }],
  connections: [], accountProbes: [],
  connectionContext: { editing: { available: false, live: false }, querying: { available: false, live: false }, publishing: { available: false, live: false } },
  session: { sessionId: 'uishot', modelName: 'Contoso Sales', measures: 12, tables: 4, hasUnsavedChanges: false, liveConnected: false, currentAccount: 'alex@contoso.com', currentTenant: 'contoso.com' } };
const when = (min) => new Date(Date.now() - min * 60000).toISOString();
const HISTORY = [
  { id: 'h4', connectionId: 'cn2', kind: 'open', ok: false, account: 'megan@contoso.com', tenantId: 'contoso.com', database: 'Contoso', modelName: 'Contoso Sales', whenUtc: when(3),
    detail: 'signing in or opening the model failed: the account megan@contoso.com can sign in to contoso.com but has no access to the workspace Contoso UAT. Ask a workspace admin to add this account as a Member or Contributor, or open the model as another account. The saved account was left unchanged.' },
  { id: 'h3', connectionId: 'cn3', kind: 'signin', ok: false, tenantId: 'contoso.com', database: 'Finance', whenUtc: when(9),
    detail: 'sign-in failed before an account was chosen (the sign-in window was closed before it finished). Nothing was saved, and the model stays as it was.' },
  { id: 'h2', connectionId: 'cn2', kind: 'signin', ok: true, account: 'megan@contoso.com', tenantId: 'contoso.com', database: 'Contoso', modelName: 'Contoso Sales', whenUtc: when(20) },
  { id: 'h1', connectionId: 'cn2', kind: 'open', ok: true, account: 'megan@contoso.com', tenantId: 'contoso.com', database: 'Contoso', modelName: 'Contoso Sales', whenUtc: when(28) },
];
const WAREHOUSE = { id: 'sql-3f21a9c4e0b7d5a2', name: 'Contoso warehouse', server: 'contoso-sql.database.windows.net', database: 'Warehouse',
  authMode: 'interactive', tenantId: 'contoso.com', createdUtc: when(4000), lastTestedUtc: when(35), lastTestOk: true };

for (const width of (process.env.WIDTHS || '1280,390').split(',').map(Number)) {
  for (const theme of (process.env.THEMES || 'light,dark').split(',')) {
    const name = (f) => `${f}-${width}-${theme}`;

    // f1
    {
      const { page, errors } = await fresh(width, theme, '?view=connections', ALEX);
      await page.waitForSelector('[data-testid="hub-add-workspace"]', { timeout: 15000 });
      await page.evaluate(() => window.__uishotHarness.setResponse('listWorkspaces', (p) => p[3] === true ? '__uishotNever'
        : [{ id: 'ws-me', displayName: 'alex@contoso.com', type: 'Personal' }, { id: 'ws-f', displayName: 'Contoso Finance', type: 'Workspace' }, { id: 'ws-s', displayName: 'Contoso Sales', type: 'Workspace' }]));
      await page.evaluate(() => document.querySelector('[data-testid="hub-add-workspace"]').click()); await wait(900);
      if (!(await page.evaluate(() => document.querySelectorAll('[data-testid="pick-ws"]').length))) {
        await page.evaluate(() => document.querySelector('[data-testid="pick-ws-relist"]')?.click()); await wait(900);
      }
      const start = await page.evaluate(() => document.querySelector('[data-testid="pick-as"]')?.innerText);
      await page.evaluate(() => document.querySelector('[data-testid="pick-another-account"]').click()); await wait(2000);
      const waiting = await page.evaluate(() => ({ as: document.querySelector('[data-testid="pick-as"]')?.innerText, cancel: !!document.querySelector('[data-testid="pick-signin-cancel"]') }));
      check(waiting.cancel && /browser window/.test(waiting.as || ''), `${name('f1')}: waiting shows Cancel and the browser line (${waiting.as})`);
      await page.screenshot({ path: join(OUT, `${name('f1')}-waiting.png`) });
      await page.evaluate(() => document.querySelector('[data-testid="pick-signin-cancel"]').click()); await wait(600);
      const after = await page.evaluate(() => ({ as: document.querySelector('[data-testid="pick-as"]')?.innerText, ws: document.querySelectorAll('[data-testid="pick-ws"]').length,
        unfinished: !!document.querySelector('[data-testid="pick-signin-unfinished"]'), another: !!document.querySelector('[data-testid="pick-another-account"]') }));
      check(after.as === start && /alex@contoso\.com/.test(after.as || ''), `${name('f1')}: after Cancel the line is the start line again (${after.as})`);
      check(after.ws === 3 && !after.unfinished && after.another, `${name('f1')}: three workspaces, no "did not finish", Use another account (${JSON.stringify(after)})`);
      check(await noSideways(page), `${name('f1')}: no sideways scroll`);
      check(errors.length === 0, `${name('f1')}: no page errors ${errors.join('; ')}`);
      await page.screenshot({ path: join(OUT, `${name('f1')}-after.png`) });
      await page.close();
    }

    // f2
    {
      const { page, errors } = await fresh(width, theme, '', null);
      await page.waitForSelector('header.studio-chrome', { timeout: 15000 }); await wait(800);
      const h = await page.evaluate(() => {
        const header = document.querySelector('header.studio-chrome');
        const shown = (el) => !!el && getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().width > 0;
        const line = document.querySelector('[data-testid="studio-account-line"]');
        const meta = document.querySelector('[data-testid="studio-account-meta"]');
        return { height: Math.round(header.getBoundingClientRect().height), fold: header.dataset.fold, line: shown(line) ? line.innerText : null, meta: shown(meta) ? meta.innerText : null, title: line?.title };
      });
      lines.push(`${name('f2')}: ${JSON.stringify(h)}`);
      if (width >= 1000) check(h.line === 'megan@contoso.com, contoso.com' && h.height === 40, `${name('f2')}: the desktop line under the model name, header 40 px`);
      else check(h.meta === 'megan, contoso.com', `${name('f2')}: the phone line in the action row`);
      check(errors.length === 0, `${name('f2')}: no page errors ${errors.join('; ')}`);
      await page.screenshot({ path: join(OUT, `${name('f2')}-header.png`), clip: { x: 0, y: 0, width, height: width >= 1000 ? 160 : 210 } });
      await page.close();
    }

    // f3
    {
      const { page, errors } = await fresh(width, theme, '?view=connections', null);
      await page.waitForSelector('[data-testid="hub-account-button"]', { timeout: 15000 });
      await page.evaluate((hist) => window.__uishotHarness.setResponse('listConnectionHistory', hist), HISTORY);
      await page.evaluate(() => document.querySelector('[data-testid="hub-account-button"]').click()); await wait(400);
      await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-account-menu"] button')].find((b) => b.textContent.trim() === 'History')?.click()); await wait(700);
      const closed = await page.evaluate(() => [...document.querySelectorAll('.hub-history-row')].map((r) => ({ button: r.tagName === 'BUTTON', marker: r.querySelector('.hub-history-marker')?.textContent || '' })));
      lines.push(`${name('f3')}: closed ${JSON.stringify(closed)}`);
      lines.push(`${name('f3')}: lines ${JSON.stringify(await page.evaluate(() => [...document.querySelectorAll('[data-history-line="sub"]')].map((el) => [el.scrollWidth, el.clientWidth, getComputedStyle(el).display, getComputedStyle(el).overflow])))}`);
      check(closed.length === 4 && closed[0].button && closed[0].marker === '▸', `${name('f3')}: the long row is cut and shows it opens`);
      await page.screenshot({ path: join(OUT, `${name('f3')}-closed.png`) });
      await page.evaluate(() => document.querySelector('.hub-history-row').click()); await wait(400);
      const open = await page.evaluate(() => { const r = document.querySelector('.hub-history-row'); const reason = r.querySelector('[data-history-line="reason"]');
        return { expanded: r.getAttribute('aria-expanded'), reason: reason?.innerText, cut: reason ? reason.scrollWidth > reason.clientWidth + 1 : null }; });
      check(open.expanded === 'true' && /The saved account was left unchanged\.$/.test(open.reason || '') && open.cut === false, `${name('f3')}: open, the whole reason, nothing cut`);
      check(await noSideways(page), `${name('f3')}: no sideways scroll`);
      check(errors.length === 0, `${name('f3')}: no page errors ${errors.join('; ')}`);
      await page.screenshot({ path: join(OUT, `${name('f3')}-open.png`) });
      await page.close();
    }

    // f4
    {
      const { page, errors } = await fresh(width, theme, '?view=connections&sql=error', null);
      await page.waitForSelector('[data-testid="hub-add-workspace"]', { timeout: 15000 }); await wait(500);
      await page.evaluate((w) => window.__uishotHarness.setResponse('listSqlSources', [w]), WAREHOUSE);
      await page.evaluate(() => { const r = [...document.querySelectorAll('.hub-row')].find((x) => (x.querySelector('.hub-row-name')?.textContent || '') === 'Contoso Sales'); r?.querySelector('[data-testid="hub-row-overflow"]')?.click(); });
      await wait(300);
      const item = await page.evaluate(() => { const b = [...document.querySelectorAll('[role="menuitem"]')].find((x) => x.textContent.includes('SQL endpoint: Contoso warehouse')); b?.click(); return !!b; });
      check(item, `${name('f4')}: the row names its saved endpoint`);
      await wait(700);
      const form = await page.evaluate(() => { const f = document.querySelector('[data-testid="hub-sql-form"]'); f?.scrollIntoView({ block: 'center' });
        const sel = f?.querySelector('[data-testid="hub-sql-saved"]');
        return { saved: sel ? sel.options[sel.selectedIndex]?.text : null, name: f?.querySelector('[data-testid="hub-sql-name"]')?.value, server: f?.querySelector('[data-testid="hub-sql-server"]')?.value,
          last: /Last test worked/.test(f?.innerText || '') }; });
      lines.push(`${name('f4')}: ${JSON.stringify(form)}`);
      check(form.saved === 'Contoso warehouse' && form.name === 'Contoso warehouse' && form.server === 'contoso-sql.database.windows.net' && form.last, `${name('f4')}: the form opens on the saved endpoint, filled`);
      check(await noSideways(page), `${name('f4')}: no sideways scroll`);
      check(errors.length === 0, `${name('f4')}: no page errors ${errors.join('; ')}`);
      await wait(200);
      await page.screenshot({ path: join(OUT, `${name('f4')}-form.png`) });
      await page.close();
    }
  }
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'f172-drive.txt'), lines.join('\n') + '\n');
console.log(lines.join('\n'));
process.exit(fails.length ? 1 : 0);
