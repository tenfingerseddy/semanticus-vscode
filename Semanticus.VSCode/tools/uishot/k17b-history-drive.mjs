// K17 again (Kane, 9 Oct, kane-handson/39.webp): the "Asked before" list still had its own scroll bar inside the card. Now it has
// none: the latest 8 questions show, "Show all" opens the rest in place, and the page scrolls. Shot with 5 and 30 entries,
// dark and light, and with the 30 opened.
//
//   OUTDIR=<dir> SIDE=<before|after> node tools/uishot/k17b-history-drive.mjs
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
  id: 'dq-k17-' + i, askedAt: new Date(Date.UTC(2026, 9, 8, 9, 0) - i * 600e3).toISOString(), question: null, reading: i < READINGS.length ? READINGS[i] : `Total Sales by Region, Year = ${2000 + i}`,
  readBy: 'you', outcome: 'answered', parts: { measures: ['Total Sales'], groupBy: [], filters: [] }, rowCount: 3 + i, origin: i % 4 === 3 ? 'agent' : 'human',
}));

try {
  for (const n of [5, 30]) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
    await wait(2500);
    await page.evaluate((list) => { window.__askDataMock.history = () => ({ questions: list }); }, rows(n));
    await page.focus('input[aria-label="Ask in your own words"]');
    await page.keyboard.type('total sales by region');
    await page.keyboard.press('Enter');
    await wait(1200);
    await page.evaluate(() => { const b = [...document.querySelectorAll('[data-testid="askdata-history"] button')].find((x) => x.textContent.trim() === 'Refresh'); b?.click(); });
    await wait(800);
    const measure = () => page.evaluate(() => {
      const card = document.querySelector('[data-testid="askdata-history"]');
      const inner = [...card.querySelectorAll('*')].filter((e) => /auto|scroll/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 1).length;
      const showAll = [...card.querySelectorAll('button')].find((x) => /^Show (all|fewer)$/.test(x.textContent.trim()));
      const doc = document.querySelector('main');
      return { rows: card.querySelectorAll('[data-testid="askdata-history-row"]').length, innerScrollBars: inner, link: showAll ? showAll.textContent.trim() : null,
        cardHeight: Math.round(card.getBoundingClientRect().height), pageScrolls: !!doc && doc.scrollHeight > doc.clientHeight + 1, pageHeight: doc ? doc.scrollHeight : 0, pageView: doc ? doc.clientHeight : 0 };
    });
    const scenes = [['', await measure()]];
    for (const [tag, m] of scenes) for (const theme of ['dark', 'light']) {
      await applyTheme(page, theme); await wait(300);
      const shot = join(OUT, `${SIDE}-${n}rows${tag}-${theme}.png`);
      await page.screenshot({ path: shot });
      results.push({ side: SIDE, n, state: tag || 'closed', theme, ...m, shot });
    }
    const m0 = scenes[0][1];
    if (SIDE === 'after') {
      expect(m0.innerScrollBars === 0, `${n}: an inner scroll bar is left`);
      expect(m0.rows === Math.min(n, 8), `${n}: ${m0.rows} rows shown`);
      expect(m0.link === (n > 8 ? 'Show all' : null), `${n}: link is ${m0.link}`);
    }
    if (n > 8 && SIDE === 'after') {
      await page.evaluate(() => { [...document.querySelectorAll('[data-testid="askdata-history"] button')].find((x) => x.textContent.trim() === 'Show all')?.click(); });
      await wait(300);
      const open = await measure();
      for (const theme of ['dark', 'light']) {
        await applyTheme(page, theme); await wait(300);
        const shot = join(OUT, `${SIDE}-${n}rows-open-${theme}.png`);
        await page.screenshot({ path: shot });
        await page.evaluate(() => { const m = document.querySelector('main'); if (m) m.scrollTop = m.scrollHeight; });
        await wait(150);
        await page.screenshot({ path: shot.replace('.png', '-scrolled.png') });
        await page.evaluate(() => { const m = document.querySelector('main'); if (m) m.scrollTop = 0; });
        results.push({ side: SIDE, n, state: 'open', theme, ...open, shot });
      }
      expect(open.rows === n, `${n}: ${open.rows} rows after Show all`);
      expect(open.innerScrollBars === 0, `${n}: an inner scroll bar after Show all`);
      expect(open.pageScrolls, `${n}: the page does not scroll after Show all`);
      expect(open.link === 'Show fewer', `${n}: link after opening is ${open.link}`);
    }
    expect(errors.length === 0, `${n}: page errors ${errors.join('; ')}`);
    await page.close();
  }
} finally {
  await browser.close(); server.close();
}
writeFileSync(join(OUT, `results-${SIDE}.json`), JSON.stringify({ results, failures }, null, 2));
console.log(`${results.length} shots in ${OUT}`);
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('k17b-history-drive: every scene held');
