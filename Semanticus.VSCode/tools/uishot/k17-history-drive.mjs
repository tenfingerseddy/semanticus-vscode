// K17 (Kane, 8 Oct, kane-handson/29.png): the Ask page's "Asked before" list showed its own scroll bar after about six rows
// while the rest of its card stood empty. Shot with 5 and 30 rows, at 1440 and 1100 wide, dark and light, over an answered
// question so the card is as tall as the answer beside it. The harness's mock hands back the history rows.
//
//   OUTDIR=<dir> SIDE=<before|after> node tools/uishot/k17-history-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'k17');
const SIDE = process.env.SIDE || 'after';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `k17-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const results = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };

const READINGS = ['Total Sales by Region', 'Total Sales by Category, top 10 by Total Sales', 'Total Sales, Year = 2025', 'Margin by Brand', 'Total Sales by Year'];
const rows = (n) => Array.from({ length: n }, (_, i) => ({
  id: 'dq-k17-' + i, askedAt: new Date(Date.UTC(2026, 9, 8, 9, 0) - i * 600e3).toISOString(), question: null, reading: READINGS[i % READINGS.length],
  readBy: 'you', outcome: 'answered', parts: { measures: ['Total Sales'], groupBy: [], filters: [] }, rowCount: 3 + i, origin: i % 4 === 3 ? 'agent' : 'human',
}));

try {
  for (const width of [1440, 1100]) {
    for (const n of [5, 30]) {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
      await wait(2500);
      await page.evaluate((list) => { window.__askDataMock.history = () => ({ questions: list }); }, rows(n));
      await page.focus('input[aria-label="Ask in your own words"]');
      await page.keyboard.type('total sales by region');
      await page.keyboard.press('Enter');
      await wait(1200);
      // The ask records itself first in the mock's own list; the override keeps the n rows, so refresh shows exactly them.
      await page.evaluate(() => { const b = [...document.querySelectorAll('[data-testid="askdata-history"] button')].find((x) => x.textContent.trim() === 'Refresh'); b?.click(); });
      await wait(800);
      const m = await page.evaluate(() => {
        const card = document.querySelector('[data-testid="askdata-history"]');
        const list = card?.querySelector('ul');
        const c = card.getBoundingClientRect(), l = list.getBoundingClientRect();
        const pad = parseFloat(getComputedStyle(card).paddingBottom) || 0;
        return { rows: card.querySelectorAll('[data-testid="askdata-history-row"]').length, scrolls: list.scrollHeight > list.clientHeight + 1,
          gapBelow: Math.round(c.bottom - pad - l.bottom), listHeight: Math.round(l.height), cardHeight: Math.round(c.height), contentHeight: list.scrollHeight };
      });
      for (const theme of ['dark', 'light']) {
        await applyTheme(page, theme);
        await wait(300);
        const shot = join(OUT, `${SIDE}-${width}-${n}rows-${theme}.png`);
        await page.screenshot({ path: shot });
        results.push({ side: SIDE, width, n, theme, ...m, shot });
      }
      if (SIDE === 'after') {
        expect(m.rows === n, `${width}/${n}: ${m.rows} rows shown`);
        expect(m.gapBelow <= 2, `${width}/${n}: the list stops ${m.gapBelow}px short of the card's foot`);
        expect(m.scrolls === m.contentHeight > m.listHeight, `${width}/${n}: scrolls ${m.scrolls} while content ${m.contentHeight} and list ${m.listHeight}`);
        if (n === 5) expect(!m.scrolls, `${width}/5: five rows scroll`);
      }
      expect(errors.length === 0, `${width}/${n}: page errors ${errors.join('; ')}`);
      await page.close();
    }
  }
} finally {
  await browser.close(); server.close();
}
writeFileSync(join(OUT, `results-${SIDE}.json`), JSON.stringify({ results, failures }, null, 2));
console.log(`${results.length} shots in ${OUT}`);
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('k17-history-drive: every scene held');
