// Acceptance driver for the cp/sql-sources-hub lane: Connections > SQL sources, the fourth role.
//
// Every shot here is the REAL component driven through the mock engine, not a drawing: the harness answers
// listSqlSources / saveSqlSource / testSqlSource / deleteSqlSource / listTests / listTableMappings with the
// shapes cp/tests-engine-b shipped, and its arrays MUTATE, so "after a test" and "after a refused remove"
// are the component's own state. No live SQL is contacted and none is claimed.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
// findBrowser prefers a cached chrome-headless-shell and says so when it has to fall back to a full browser.
// The fallback is a real hazard here, not a nicety: a running chromium on this machine takes the launch over
// and the session closes mid-drive, which reads as a component failure and is not one. Install the shell with
// `npx @puppeteer/browsers install chrome-headless-shell@stable` before trusting a run that used the fallback.
// Chromium's own scratch, on real disk rather than the shared tmpfs: --disable-dev-shm-usage sends it to
// TMPDIR, and /tmp here is a tmpfs other work fills, where a screenshot fails as "Unable to capture
// screenshot" and reads exactly like a component fault.
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `sql-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';

// `press` entries are either a data-testid (clicks the FIRST match) or { testid, nth } for a later row.
// `type` fills the Add form so the Save path is driven with real typed values, not a pre-seeded object.
const JOBS = [
  { slug: 'sql-list', q: 'view=connections', desc: 'the role list with two sources, one failed test' },
  // The direct-open route, driven rather than asserted in source: the hub starts on Open a model and is ASKED for
  // the SQL sources section the way an outside caller asks (the Tests page's "Add a SQL source..."). If this lands
  // on the list without the rail being clicked, the route works.
  { slug: 'sql-direct-open', q: 'view=connections', rail: 'open', route: 'sqlsources', desc: 'opened straight on SQL sources from outside' },
  { slug: 'sql-setup-role', q: 'view=connections', rail: 'setup', desc: 'the fourth role card in Current setup' },
  { slug: 'sql-add-form', q: 'view=connections', press: ['hub-sql-add'], desc: 'the Add form on the shared account picker' },
  // Empty boxes are refused in field order, so all three are filled here: this shot is about the LAST rule,
  // the pasted connection string, which is the one that would otherwise put a password on the wire.
  { slug: 'sql-add-missing', q: 'view=connections', press: ['hub-sql-add'], then: ['hub-sql-save'], desc: 'Save with nothing typed, refused in the form' },
  { slug: 'sql-add-refused', q: 'view=connections', press: ['hub-sql-add'], type: { 'hub-sql-name': 'Contoso warehouse two', 'hub-sql-server': 'Server=x;User Id=sa;Password=hunter2', 'hub-sql-database': 'Warehouse' }, then: ['hub-sql-save'], desc: 'a pasted connection string refused in the form' },
  { slug: 'sql-add-filled', q: 'view=connections', press: ['hub-sql-add'], type: { 'hub-sql-name': 'Ops reporting', 'hub-sql-server': 'ops-sql.database.windows.net', 'hub-sql-database': 'Reporting' }, desc: 'the Add form filled in and ready to save' },
  { slug: 'sql-test-ok', q: 'view=connections', press: ['hub-sql-test'], desc: 'a Test connection that worked' },
  { slug: 'sql-test-failed', q: 'view=connections', press: [{ testid: 'hub-sql-test', nth: 1 }], desc: 'a Test connection that failed, in plain words' },
  { slug: 'sql-remove-refused', q: 'view=connections', press: ['hub-sql-remove', 'hub-sql-remove-confirm-btn'], desc: 'a Remove refused, naming what still uses it' },
  { slug: 'sql-empty', q: 'view=connections&sql=empty', desc: 'the first visit' },
  { slug: 'sql-loading', q: 'view=connections&sql=loading', settle: 300, desc: 'the loading state' },
  { slug: 'sql-error', q: 'view=connections&sql=error', desc: 'a list that could not be read (never drawn as "you have none")' },
  // NO Escape scene here on purpose. A STANDALONE hub has nowhere to close to: onClose asks the host to
  // close the panel, which a harness page cannot show, so a shot of it would prove nothing either way. The
  // hub's Escape is proven where it matters, in the Studio overlay: cp-tests-drive's
  // journey-3-escape-closes-only-the-hub shows the hub gone and the unfinished check still standing.
];

const clickTestid = (spec) => {
  const testid = typeof spec === 'string' ? spec : spec.testid;
  const nth = typeof spec === 'string' ? 0 : (spec.nth || 0);
  const all = [...document.querySelectorAll(`[data-testid="${testid}"]`)].filter((x) => x.getClientRects().length);
  const el = all[nth];
  if (el) el.click();
  return !!el;
};

const results = [];
for (const job of JOBS) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 2 });
  let note = '';
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-hubnav]', { timeout: 15000 });
    await wait(900);
    // The rail is the way in. The Tests page uses openConnectionsOnView('sqlsources') to land here directly;
    // the standalone harness has no host section flag, so the driver clicks the same rail item a person would.
    const rail = job.rail || 'sqlsources';
    const okRail = await page.evaluate((v) => { const b = document.querySelector(`[data-hubnav="${v}"]`); if (b) b.click(); return !!b; }, rail);
    if (!okRail) note += `rail "${rail}" not found; `;
    await wait(job.settle ?? 800);
    // An outside caller asking for a section. Same host message both doors receive, so overlay and standalone
    // converge on one behaviour; the Tests page reaches the same setView through openConnectionsOnView.
    if (job.route) {
      await page.evaluate((s) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'openConnections', section: s } })), job.route);
      await wait(800);
    }
    for (const spec of job.press || []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `action "${JSON.stringify(spec)}" not found; `;
      await wait(700);
    }
    for (const [testid, value] of Object.entries(job.type || {})) {
      const ok = await page.evaluate((t) => { const e = document.querySelector(`[data-testid="${t}"]`); if (e) e.focus(); return !!e; }, testid);
      if (!ok) { note += `field "${testid}" not found; `; continue; }
      await page.keyboard.type(value, { delay: 8 });
      await wait(120);
    }
    for (const spec of job.then || []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `action "${JSON.stringify(spec)}" not found; `;
      await wait(700);
    }

    // What the page actually SAYS, so no shot can be reported as proving something it does not show.
    const seen = await page.evaluate(() => {
      const txt = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').trim() : null; };
      const all = (sel) => [...document.querySelectorAll(sel)].map((e) => (e.textContent || '').trim());
      return {
        rows: all('[data-testid="hub-sql-row"]').length,
        rowText: all('[data-testid="hub-sql-row"]').map((t) => t.replace(/\s+/g, ' ')),
        failedBadges: all('[data-testid="hub-sql-failed"]').length,
        empty: txt('[data-testid="hub-sql-empty"]'),
        loading: txt('[data-testid="hub-sql-loading"]'),
        error: txt('[data-testid="hub-sql-error"]'),
        formError: txt('[data-testid="hub-sql-form-error"]'),
        formOpen: !!document.querySelector('[data-testid="hub-sql-form"]'),
        picker: !!document.querySelector('[data-testid="hub-sql-form"] [data-testid="account-picker"]'),
        testResult: txt('[data-testid="hub-sql-test-result"]'),
        refused: txt('[data-testid="hub-sql-refused"]'),
        roleCards: all('article h3').filter((t) => /^(Editing|Tests and queries|Publish to|SQL sources)$/.test(t)),
        heading: txt('h2'),
        railSelected: [...document.querySelectorAll('[data-hubnav]')].filter((b) => getComputedStyle(b).fontWeight === '600').map((b) => b.getAttribute('data-hubnav')),
        passwordBoxes: document.querySelectorAll('input[type="password"]').length,
        hubShown: !!document.querySelector('[data-hubnav]'),
      };
    });
    const out = join(OUT, `${LABEL}-${job.slug}-1366x900.png`);
    await page.screenshot({ path: out, fullPage: false });
    results.push({ job: job.slug, desc: job.desc, seen, errors, note, out });
  } catch (e) {
    results.push({ job: job.slug, desc: job.desc, errors, note: note + 'FATAL: ' + e.message });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-sql-sources-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
