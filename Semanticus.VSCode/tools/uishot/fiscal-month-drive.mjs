// UAT N1 (Sol H8): a fiscal month field draws in the model's sort-by order, Jul to Jun, never Jan to Dec, and the
// query it runs brings the sort-by column and orders by it. Drives the REAL Studio through the mock engine.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/fiscal-month-drive.mjs
//
// The mock model has Date[Fiscal Month] sorted by Date[FiscalMonthNo]; the engine's rows come back in no set order.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'fiscal-month');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `fiscal-month-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const FISCAL = ['Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'];
const scrambled = ['Mar', 'Jan', 'Jul', 'Oct', 'Jun', 'Aug', 'Dec', 'Feb', 'Sep', 'Nov', 'Apr', 'May'];
const col = (name, table, dataType, opts = {}) => ({ ref: `column:${table}/${name}`, name, table, dataType, displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '',
  isKey: false, isHidden: false, isCalculated: false, hasDescription: true, description: 'x', expression: '', ...opts });
const CFG = {
  columns: [col('Category', 'Product', 'String'), col('Fiscal Month', 'Date', 'String', { sortByColumn: 'FiscalMonthNo' }), col('FiscalMonthNo', 'Date', 'Int64', { isHidden: true })],
  measures: [{ ref: 'measure:Sales/Net Sales', name: 'Net Sales', table: 'Sales', displayFolder: '', formatString: '#,##0', isHidden: false, hasDescription: true, description: 'x', expression: 'SUM ( Sales[Amount] )' }],
};
const RUN = {
  columns: [{ name: 'Product[Category]', type: 'String' }, { name: 'Date[Fiscal Month]', type: 'String' }, { name: 'Date[FiscalMonthNo]', type: 'Int64' }, { name: '[Net Sales]', type: 'Decimal' }],
  rows: ['Bikes', 'Clothing'].flatMap((c, ci) => scrambled.map((m) => [c, m, FISCAL.indexOf(m) + 1, (ci + 1) * 1000 + FISCAL.indexOf(m) * 90 + 200])),
  rowCount: 24, truncated: false, elapsedMs: 31,
};

const fails = [];
const expect = (ok, msg) => { if (!ok) fails.push(msg); return ok; };
const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, CFG);
await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=daxlab&conn=1&live=1&tier=pro`, { waitUntil: 'networkidle0', timeout: 60000 });
await wait(2500);
await page.evaluate((r) => window.__uishotHarness.setResponse('runDax', r), RUN);
const clickText = (label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => !x.disabled && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, 'body');
expect(await clickText('Run the query'), 'Run'); await wait(1500);

const sent = await page.evaluate(() => (window.__uishotHarness.messages || []).filter((m) => m && m.method === 'runDax').map((m) => JSON.stringify(m.params)).pop() || '');
expect(/FiscalMonthNo/.test(sent) && /ORDER BY/.test(sent), 'the query brings the sort-by column and orders by it: ' + sent.slice(0, 300));
const reason = await page.evaluate(() => document.querySelector('[data-testid="queries-chart-reason"]')?.textContent || '');
expect(/Dates run left to right/.test(reason), 'the order was set by the model, so the rules may say so: ' + reason);
const axis = await page.evaluate(() => {
  const g = document.querySelector('[data-testid="queries-result"]');
  return g ? g.textContent.replace(/\s+/g, ' ') : '';
});
await page.screenshot({ path: `${OUT}/fiscal-line.png` });
expect(await clickText('Matrix'), 'the Matrix kind'); await wait(700);
const heads = await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-result"] thead th')].map((t) => t.textContent.trim()));
expect(JSON.stringify(heads.slice(1)) === JSON.stringify(FISCAL), 'matrix columns run Jul to Jun: ' + heads.join(' | '));
await page.screenshot({ path: `${OUT}/fiscal-matrix.png` });
expect(errors.length === 0, 'page errors: ' + errors.join('; '));
console.log(`${fails.length ? 'FAIL' : 'PASS'} fiscal-month: Jul to Jun by the model's sort-by${fails.length ? '\n  - ' + fails.join('\n  - ') : ''}`);
void axis;
await browser.close(); server.close();
process.exit(fails.length ? 1 : 0);
