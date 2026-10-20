// Verifier v-query-c (16 Oct): failing driver for the Query notebook at m16/query-c 65be7f7b. The REAL Studio on the mock engine.
// It FAILS (exit 1) while these defects stand:
//   V1  a row opened while Time all runs is not run (one query at a time), but its Answer shows the LAST live result: another
//       question's number, reading and chart under this row's question ("4 rows" of "total sales by region" under "total sales").
//   V2  Free: a words row opened after a DAX run cannot be asked, and shows the DAX run's number and line under its question.
//   V3  a pinned row opened again whose run fails keeps its old number in the folded row and the history, dated "just now"
//       (engine half: QueryNotebookTests.V_a_row_run_again_in_place_never_keeps_the_old_number_under_the_new_date).
// It also proves, and passes: Stop ends Time all after the call in flight, never more than one call in flight, a re-asked
// question takes its row's place and pin, a reopened tab shows the open row with the number it was asked with.
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/v-query-c-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'v-query-c');
mkdirSync(OUT, { recursive: true });
const strip = (f) => readFileSync(join(here, f), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const ALIGN = strip('query-notebook-align.js');
const MEASURE = strip('query-notebook-measure.js');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vqc-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const WIDTHS = process.env.QUICK ? [1440] : [1440, 1090];
const THEMES = ['dark', 'light'];
const results = [];
const failures = [];
const firstViews = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };
const t = (id) => `[data-testid="${id}"]`;
const align = { shots: 0, offGrid: [], ragged: [], gaps: [], inner: [] };

async function open(q, size = [1440, 900]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const methods = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean));
const calls = (page, m) => page.evaluate((x) => (window.__uishotHarness?.messages || []).filter((c) => c.method === x).map((c) => c.params ?? c.args ?? []), m);
const count = async (page, m) => (await methods(page)).filter((x) => x === m).length;
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);
const rows = (page) => page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => ({
  id: r.getAttribute('data-id'), open: r.getAttribute('data-open') === 'yes', pinned: r.getAttribute('data-pinned') === 'yes',
  text: (r.querySelector('.qn-line-text')?.textContent || '').trim(), n: (r.querySelector('.qn-line-n')?.textContent || '').trim(), time: (r.querySelector('[data-testid="row-time"]')?.textContent || '').trim(),
  dropped: !!r.querySelector('[data-testid="row-dropped"]') })));
const press = async (page, sel) => { await page.waitForSelector(sel, { timeout: 8000 }); await page.$eval(sel, (e) => e.scrollIntoView({ block: 'center' })); await page.click(sel); await wait(300); };
// A row's own controls sit faint until the row is hovered: hover first, as a person does.
const rowPress = async (page, id, testid) => { const sel = `[data-testid="notebook-row"][data-id="${id}"]`; await page.waitForSelector(sel, { timeout: 8000 }); await page.hover(sel); await wait(120); await page.click(`${sel} ${t(testid)}`); await wait(450); };
const ask = async (page, words, waitMs = 2400) => {
  await page.$eval(`${t('queries-ask')} input`, (e) => { e.focus(); e.select(); }); await page.keyboard.type(words);
  await page.click(t('query-ask')); await wait(waitMs);
};
const toTab = async (page, label) => { await page.evaluate((x) => { const b = [...document.querySelectorAll('button, [role="tab"]')].find((e) => e.textContent.trim() === x && e.getClientRects().length); b?.click(); }, label); await wait(900); };


const figure = (page) => text(page, `[data-testid="notebook-row"][data-open="yes"] ~ [data-testid="row-body"] [data-testid="figure"], [data-testid="row-body"] [data-testid="figure"]`);
const openRow = async (page) => (await rows(page)).find((r) => r.open);
const shot = async (page, slug) => { await applyTheme(page, 'dark'); await wait(300); await page.screenshot({ path: join(OUT, slug + '.png') }); };
const log = [];
const note = (k, v) => { log.push(`${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`); };
try {
  // V1. A row opened while Time all is running: the page does not run it (one query at a time), so what number does the open row show?
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed&hold=benchmarkColdWarm');
    await ask(page, 'total sales by region');
    note('V1 after ask: open row', (await openRow(page))?.text); note('V1 after ask: figure', await figure(page));
    await page.hover('[data-testid="notebook-row"][data-id="dq-seed05"]'); await wait(120);
    await page.click('[data-testid="notebook-row"][data-id="dq-seed05"] [data-testid="row-tick"]');
    await press(page, t('time-all')); await press(page, t('time-scope-selection')); await wait(600);
    note('V1 timing line', await text(page, t('time-progress')));
    await rowPress(page, 'dq-seed02', 'row-open'); await wait(600);
    const o = await openRow(page);
    note('V1 open row while timing', o?.text); note('V1 its figure', await figure(page)); note('V1 its headline line', await text(page, t('headline')));
    note('V1 runDax/askData calls', [await count(page, 'runDax'), await count(page, 'askData')]);
    expect(!(o?.text === 'total sales' && /4 rows/.test(await figure(page))), 'V1: a row opened during Time all shows another question\'s number ("4 rows" of "total sales by region" under "total sales", whose own number is $2.65M)');
    await shot(page, 'v1-open-during-timing');
    await rowPress(page, 'dq-seed05', 'row-open'); await wait(600);
    note('V1b dax row opened while timing', (await openRow(page))?.text); note('V1b figure', await figure(page));
    await shot(page, 'v1b-dax-row-during-timing');
    await page.evaluate(() => window.__uishotHarness.release('benchmarkColdWarm')); await wait(1500);
    note('V1 errors', errors);
    await page.close();
  }
  // V2. Free: a words row of the history (asked when the plan was Pro, or by the assistant) opened after a DAX run.
  {
    const { page, errors } = await open('tab=query&tier=free&nb=seed');
    await press(page, t('new-query')); await press(page, t('mode-dax')); await press(page, t('dax-run')); await wait(1500);
    note('V2 Free after DAX run: figure', await figure(page));
    await rowPress(page, 'dq-seed02', 'row-open'); await wait(800);
    note('V2 Free open words row', (await openRow(page))?.text); note('V2 its figure', await figure(page)); note('V2 headline line', await text(page, t('headline')));
    expect(!(/total sales$/.test((await openRow(page))?.text ?? '') && (await figure(page)) && !/2\.65M/.test(await figure(page))), 'V2: on Free a words row opened after a DAX run shows the DAX run\'s number under its question');
    await shot(page, 'v2-free-open-words-row');
    note('V2 sent', (await methods(page)).filter((m) => ['askData', 'listDataQuestions', 'pickChartKind'].includes(m)));
    note('V2 errors', errors);
    await page.close();
  }
  // V3. A pinned row opened again whose run fails: the open row says so; what does the folded row and the history say after?
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed&askdata=error');
    await rowPress(page, 'dq-seed02', 'row-open'); await wait(1500);
    note('V3 open after failed re-run: answer', (await text(page, t('answer'))).slice(0, 160));
    await rowPress(page, 'dq-seed02', 'row-open'); await wait(500);   // fold it
    const r = (await rows(page)).find((x) => x.id === 'dq-seed02');
    note('V3 folded row', r);
    await press(page, t('history-open'));
    const h = await page.$$eval('[data-testid="history-row"]', (x) => x.map((e) => e.textContent.replace(/\s+/g, ' ').trim()));
    note('V3 history', h);
    const mine = h.find((x) => /^total sales/.test(x)) ?? '';
    expect(!(/2\.65M/.test(mine) && /just now/.test(mine)), `V3: a re-run that failed keeps the old number under the new date ("${mine}")`);
    await shot(page, 'v3-failed-rerun-history');
    note('V3 errors', errors);
    await page.close();
  }
  // V4. Stop: Time all on All with a slow engine (each call parked); Stop after the first; exactly one call, honest end line.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed&hold=benchmarkColdWarm');
    await press(page, t('time-all')); await press(page, t('time-scope-all')); await wait(700);
    const inflight1 = await page.evaluate(() => window.__uishotHarness.heldCount('benchmarkColdWarm'));
    await press(page, t('time-stop')); await wait(300);
    note('V4 line after Stop', await text(page, t('time-progress')));
    note('V4 header stop visible during timing', await has(page, t('query-stop')));
    note('V4 cancelDax sent on Stop', await count(page, 'cancelDax'));
    await page.evaluate(() => { window.__uishotHarness.hold('benchmarkColdWarm'); });
    await page.evaluate(() => window.__uishotHarness.release('benchmarkColdWarm')); await wait(1500);
    const n = await count(page, 'benchmarkColdWarm');
    note('V4 calls', n); note('V4 end line', await text(page, t('time-progress')));
    expect(inflight1 === 1 && n === 1, `V4: Stop stops after the call in flight (in flight ${inflight1}, calls ${n})`);
    expect(/Timed 1 of 4/.test(await text(page, t('time-progress'))), 'V4: the end line says how many were timed');
    // no flood: All again with every call parked, release one at a time, never more than one in flight
    await page.evaluate(() => window.__uishotHarness.hold('benchmarkColdWarm'));
    await press(page, t('time-all')); await press(page, t('time-scope-all')); await wait(500);
    let maxIn = 0;
    for (let i = 0; i < 6; i++) {
      const held = await page.evaluate(() => window.__uishotHarness.heldCount('benchmarkColdWarm')); maxIn = Math.max(maxIn, held);
      await page.evaluate(() => { window.__uishotHarness.release('benchmarkColdWarm'); window.__uishotHarness.hold('benchmarkColdWarm'); }); await wait(500);
    }
    note('V4 All: max in flight, total calls', [maxIn, await count(page, 'benchmarkColdWarm')]);
    expect(maxIn === 1 && await count(page, 'benchmarkColdWarm') === 5, 'V4: All times 4 rows one at a time');
    note('V4 errors', errors);
    await page.close();
  }
  // V5. A re-asked question takes its row's place and keeps its pin.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    await ask(page, 'total sales');
    const r = await rows(page);
    note('V5 rows', r.map((x) => `${x.id}:${x.text}:${x.pinned ? 'pin' : ''}:${x.open ? 'open' : ''}`));
    const ts = r.filter((x) => x.text === 'total sales');
    expect(ts.length === 1 && ts[0].pinned && ts[0].id !== 'dq-seed02', 'V5: one "total sales" row, the new one, pinned');
    // and a reopen of the tab keeps the open row and its number
    const f1 = await figure(page);
    await toTab(page, 'Data'); await toTab(page, 'Query'); await wait(900);
    note('V5 after tab reopen: open row / figure', [(await openRow(page))?.text, await figure(page), f1]);
    expect((await openRow(page))?.text === 'total sales' && (await figure(page)) === f1, 'V5: a reopened tab shows the open row with the number it was asked with');
    note('V5 errors', errors);
    await page.close();
  }
  // V6. A dax row opened then a words row: each shows its own number (connected, idle: the honest re-run path).
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    await rowPress(page, 'dq-seed05', 'row-open'); await wait(1500);
    note('V6 dax row figure', [(await openRow(page))?.text, await figure(page)]);
    await rowPress(page, 'dq-seed02', 'row-open'); await wait(1500);
    note('V6 words row figure', [(await openRow(page))?.text, await figure(page)]);
    note('V6 errors', errors);
    await page.close();
  }
} catch (e) { failures.push('driver crashed: ' + (e.stack || e)); }
finally { await browser.close(); server.close(); }
writeFileSync(join(OUT, 'v-query-c-drive.json'), JSON.stringify({ log, failures }, null, 1));
console.log(log.join('\n')); console.log('FAILURES:\n' + failures.join('\n'));
process.exit(failures.length ? 1 : 0);
