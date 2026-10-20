// Explain this number, the split (Kane, 17 Oct: "what makes it up seems fairly useless"). The REAL Studio through the mock engine, shot in Dark Modern
// and Light Modern at 1440 and 1090 wide. WHAT IT PROVES: the card has no heading of its own inside the Query view (nothing clipped), every share and
// amount sits inside the card (nothing cut off at the right edge), the page does not scroll sideways, the row "Split by" lists the columns the engine
// offered with the engine's pick on, and one press asks the engine again with that column (decomposeBy). WHAT IT DOES NOT: prove the engine; the live
// proof is in the lane report.
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/explain-split-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'explain-split');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `explain-split-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };
const t = (id) => `[data-testid="${id}"]`;
const calls = (page, m) => page.evaluate((x) => (window.__uishotHarness?.messages || []).filter((c) => c.method === x).map((c) => c.params ?? c.args ?? []), m);
const press = async (page, sel) => { await page.waitForSelector(sel, { timeout: 8000 }); await page.$eval(sel, (e) => e.scrollIntoView({ block: 'center' })); await page.click(sel); await wait(400); };

// What must hold on the card at this size: nothing cut off on the right, no heading of its own, nothing sideways.
const measure = () => page_measure;
const page_measure = () => {
  const card = document.querySelector('[data-testid="explain-card"]');
  if (!card) return { error: 'no card' };
  const box = card.getBoundingClientRect();
  const out = { cut: [], heading: document.querySelector('[data-explain-panel] [data-explain-header]') ? 'has a header' : null, rows: 0, side: document.documentElement.scrollWidth > window.innerWidth + 1 };
  for (const row of card.querySelectorAll('[data-testid="explain-contributors"] > div')) {
    out.rows += 1;
    for (const c of row.children) { const r = c.getBoundingClientRect(); if (r.right > box.right + 0.5 || r.left < box.left - 0.5) out.cut.push((c.textContent || '').trim().slice(0, 30)); }
  }
  let holder = card.parentElement; // any scroll box between the card and the page that clips it sideways
  out.clipX = card.scrollWidth > card.clientWidth + 1;
  return out;
};

try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro&nb=seed`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  await page.$eval(`${t('queries-ask')} input`, (e) => { e.focus(); e.select(); });
  await page.keyboard.type('total sales by region');
  await page.click(t('query-ask')); await wait(2400);
  // An answer of many rows is never explained by its first row on the engine's own choice: the card asks once which, with rows to press.
  const before0 = (await calls(page, 'explainValue')).length;
  await press(page, t('answer-more')); await press(page, t('answer-explain-open')); await wait(600);
  expect(!!(await page.$(t('explain-pick'))), 'Explain on an answer of many rows asks which number first');
  const pickRows = await page.$$eval(t('explain-pick-row'), (b) => b.map((x) => x.textContent.trim()));
  expect(pickRows.length >= 2, `the rows to press are listed (${JSON.stringify(pickRows)})`);
  expect((await calls(page, 'explainValue')).length === before0, 'nothing was explained before the person pressed a row');
  const second = pickRows[1] ?? '';
  const rowEls = await page.$$(t('explain-pick-row'));
  await rowEls[1].click(); await wait(1500);
  const asked0 = (await calls(page, 'explainValue')).slice(before0);
  const member = asked0[0]?.[1]?.filters?.[0]?.members?.[0];
  expect(asked0.length === 1 && !!member && second.startsWith(member), `pressing a row explains that row (${JSON.stringify(member)} for "${second}")`);
  expect(!!(await page.$(t('explain-card') + ' [data-explain-panel]')), 'the dossier opens as a view of the row');
  expect(!!(await page.$(t('explain-split-by'))), 'the row Split by is on the card');
  const labels = () => page.$$eval(`${t('explain-split-by')} .ex-by-opt`, (b) => b.map((x) => ({ text: x.textContent.trim(), on: x.getAttribute('aria-pressed') === 'true', column: x.getAttribute('data-column') })));
  let opts = await labels();
  expect(opts.length === 5 && opts[0].on && opts.filter((o) => o.on).length === 1, `the engine's pick is on and the others are offered (${JSON.stringify(opts)})`);

  const shoot = async (slug) => {
    for (const w of [1440, 1090]) {
      await page.setViewport({ width: w, height: w === 1440 ? 900 : 800, deviceScaleFactor: 1 }); await wait(400);
      for (const th of ['dark', 'light']) {
        await applyTheme(page, th); await wait(350);
        const m = await page.evaluate(`(${page_measure})()`);
        expect(m.cut.length === 0, `${slug} ${w} ${th}: cut off at the edge: ${m.cut.join(', ')}`);
        expect(!m.heading, `${slug} ${w} ${th}: ${m.heading}`);
        expect(!m.side && !m.clipX, `${slug} ${w} ${th}: sideways overflow`);
        await page.screenshot({ path: join(OUT, `${slug}-${w}-${th}.png`) });
      }
      await applyTheme(page, null);
    }
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 }); await wait(200);
  };
  await shoot('01-pick');

  const before = (await calls(page, 'explainValue')).length;
  await press(page, `${t('explain-split-by')} [data-column="'Date'[Quarter]"]`); await wait(1500);
  const asked = await calls(page, 'explainValue');
  expect(asked.length === before + 1 && asked[asked.length - 1][3] === "'Date'[Quarter]", `one press asks the engine again with that column (${JSON.stringify(asked[asked.length - 1]?.slice(2))})`);
  opts = await labels();
  expect(opts.length === 5 && opts.filter((o) => o.on).length === 1 && opts.find((o) => o.on).column === "'Date'[Quarter]", `the row stays and the new column is on (${JSON.stringify(opts)})`);
  expect((await page.$$(`${t('explain-contributors')} > div`)).length === 4, 'the four quarters are the parts');
  await shoot('02-chosen');
  // The table says which City: two columns of one name are told apart. No inner scroll box. The query text stays behind its own disclosure.
  opts = await labels();
  expect(opts.some((o) => o.text === 'Customer City') && opts.some((o) => o.text === 'Store City') && !opts.some((o) => o.text === 'City'), `two City columns are told apart (${opts.map((o) => o.text).join(', ')})`);
  expect(/split by Date Quarter/.test(await page.$eval(t('explain-card'), (e) => e.innerText)), 'the note says the table too: split by Date Quarter');
  const scrollers = await page.$$eval(`${t('explain-card')} *`, (els) => els.filter((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 1).map((e) => e.className));
  expect(scrollers.length === 0, `no inner scroll box on the card (${scrollers.join(', ')})`);
  await page.$$eval(`${t('explain-card')} details > summary`, (b) => b.filter((x) => /Where the data comes from/.test(x.textContent)).forEach((x) => x.click())); await wait(300);
  const lin = await page.$eval(t('explain-card'), (e) => e.innerText);
  expect(/Loaded from its source/.test(lin) && !/Sql\.Database|let Source/.test(lin), 'where the data comes from is in words, with no query text showing');
  await page.$$eval(`${t('explain-card')} .ex-code > summary`, (b) => b.forEach((x) => x.click())); await wait(300);
  expect(/Sql\.Database/.test(await page.$eval(t('explain-card'), (e) => e.innerText)), 'the query text opens behind its own disclosure');
  await shoot('03-open');
  expect(errors.length === 0, 'page errors ' + errors.join('; '));
  await page.close();
} finally {
  await browser.close(); server.close();
}
if (failures.length) { console.log('FAIL\n' + failures.map((f) => ' - ' + f).join('\n')); process.exit(1); }
console.log('explain-split-drive: ok, shots in ' + OUT);
