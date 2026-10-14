// Chart colours too similar (Kane, 13 Oct 2026): Ask your data, "sales by month and year", Chart view, clustered bars, in Dark Modern
// and Light Modern. The REAL Studio page through the harness's mock engine; the answer is a Contoso-style stand-in with the real
// shape: columns Month and Year, 12 months by 3 years. Run it on the build before and after the fix (TAG names the shots).
//
//   OUTDIR=<dir> TAG=before|after node tools/uishot/chart-colours-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'chart-colours');
const TAG = process.env.TAG || 'after';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `chartcolours-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const failures = [];

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const YEARS = [2023, 2024, 2025];
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1280, height: 800, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
  await wait(2500);
  await page.evaluate((months, years) => {
    const m = window.__askDataMock; const own = m.answer.bind(m);
    m.answer = (ask) => {
      if (!ask.question) return own(ask);
      const a = JSON.parse(JSON.stringify(own({ measures: ['Total Sales'], groupBy: ["'Customer'[Region]"] })));
      const rows = months.flatMap((mo, mi) => years.map((y, yi) => [mo, y, Math.round((820000 + mi * 31000 + yi * 190000 + ((mi * 7919) % 5) * 23000) * 100) / 100]));
      return { ...a, statement: 'This shows Total Sales for each Month and Year.', parts: { measures: ['Total Sales'], groupBy: ["'Date'[Month]", "'Date'[Year]"], filters: [] },
        columns: [{ name: 'Date[Month]', type: 'String' }, { name: 'Date[Year]', type: 'Int64' }, a.columns[a.columns.length - 1]],
        rows, rowCount: rows.length, checks: (a.checks ?? []).map((c) => (c.name === 'rows' ? { ...c, line: `${rows.length} rows.` } : c)) };
    };
  }, MONTHS, YEARS);
  await page.focus('input[aria-label="Ask in your own words"]');
  await page.keyboard.type('sales by month and year');
  await page.keyboard.press('Enter');
  await wait(1500);
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === 'Chart'); b?.click(); });
  await wait(900);
  const chart = await page.$('[data-testid="askdata-chart"]');
  if (!chart) failures.push('no chart drawn');
  for (const t of ['dark', 'light']) {
    await applyTheme(page, t); await wait(500);
    const file = join(OUT, `month-by-year-${TAG}-${t}.png`);
    await page.screenshot({ path: file, clip: chart ? await chart.boundingBox() : undefined });
    results.push({ theme: t, file });
  }
  if (errors.length) failures.push('page errors ' + errors.join('; '));
  await page.close();
} finally { await browser.close(); server.close(); }
writeFileSync(join(OUT, `results-${TAG}.json`), JSON.stringify({ results, failures }, null, 2));
console.log(`${results.length} shots in ${OUT}`);
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
