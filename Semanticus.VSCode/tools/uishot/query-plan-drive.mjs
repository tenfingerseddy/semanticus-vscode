// Proof for "Record the steps returns a plan" (Kane, 17 Oct). The REAL Studio through the mock engine, but the plan the mock hands back is the REAL
// reply the engine got from the Contoso Sales service (PLAN_JSON), not the harness's invented tree. BEFORE=1 hands back the reply the service gave
// before the fix (no plan, a trace line in the note). It presses Query > Steps > Record the steps and reads what is on the page.
//   PLAN_JSON=<file> [BEFORE=1] OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/query-plan-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'query-plan');
const BEFORE = !!process.env.BEFORE;
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `qplan-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const press = async (page, sel) => { await page.waitForSelector(sel, { timeout: 8000 }); await page.$eval(sel, (e) => e.scrollIntoView({ block: 'center' })); await page.click(sel); await wait(400); };
const failures = []; const expect = (ok, what) => { if (!ok) failures.push(what); console.log((ok ? 'ok   ' : 'FAIL ') + what); };
try {
  const real = JSON.parse(readFileSync(process.env.PLAN_JSON, 'utf8'));
  const reply = BEFORE
    ? { traceAvailable: false, logicalTree: [], physicalTree: [], totalMs: 87, rowCount: 210, note: 'No query plan captured (the engine may have answered from cache / metadata only). [trace: sid=set spid=5542 warmup=3343ms raw=2]' }
    : real;
  const page = await browser.newPage();
  const errors = []; page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro&nb=seed`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  await page.evaluate((r) => window.__uishotHarness.setResponse('captureQueryPlan', r), reply);
  // an unrecorded calculation-steps note carrying a trace line, as the service gave it (UAT F30)
  await page.evaluate(() => window.__uishotHarness.setResponse('evaluateAndLog', () => ({ traceAvailable: true, elapsedMs: 90, resultColumns: [{ name: 'Net' }], resultRows: [[1]], resultRowCount: 1, entries: [],
    note: 'No evaluation-log events were captured. [trace: sid=set spid=5602 warmup=3531ms raw=2 log=0]' })));
  await page.$eval(`${t('queries-ask')} input`, (e) => { e.focus(); e.select(); }); await page.keyboard.type('total sales by region');
  await page.click(t('query-ask')); await wait(2400);
  await press(page, t('view-steps'));
  await press(page, t('plan-record')); await wait(1200);
  const body0 = await text(page, t('queries-server-steps'));
  if (BEFORE) {
    expect(/No query plan came back/.test(body0), 'before: the page says no plan came back (the service sent none)');
  } else {
    expect(!!(await page.$(t('plan'))), 'after: a plan is on the page');
    const logical = await text(page, t('plan'));
    expect(/Scan_Vertipaq|GroupSemiJoin|Order/.test(logical), 'after: the Logical plan shows real operators');
    await press(page, t('plan-physical'));
    const physical = await text(page, t('plan'));
    expect(physical.length > 100 && physical !== logical, `after: Physical shows its own plan (${physical.length} chars)`);
    await press(page, t('plan-logical'));
  }
  await press(page, t('log-measures')); await wait(1200);
  const all = await text(page, t('queries-steps'));
  expect(!/\[trace:|sid=|spid=|warmup=|Technical details/.test(all), 'no trace line or Technical details on the Steps page');
  expect(!/\[trace:|sid=|spid=|warmup=/.test(await text(page, 'body')), 'no trace line anywhere on the page');
  for (const th of ['dark', 'light']) { await applyTheme(page, th); await wait(400); await page.screenshot({ path: join(OUT, `${BEFORE ? 'before' : 'after'}-1440-${th}.png`) }); }
  expect(errors.length === 0, 'page errors: ' + errors.join('; '));
  await page.close();
} catch (e) { failures.push('driver threw: ' + e.stack); }
finally { await browser.close(); server.close(); }
if (failures.length) { console.log('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('query-plan-drive: ok');
