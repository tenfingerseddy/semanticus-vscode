// Acceptance driver for the Query speed view on a FAST query and a SLOW one (Kane's hands-on of 1.5.104, 17 Oct). The REAL Studio through the mock
// engine, Measure speed pressed, shot in Dark Modern and Light Modern at 1440 and 1090 wide with the alignment measure on every shot.
//   fast: one line ("Fast: 75 ms. Nothing to fix."), no split sentence, no split bars; the detail has no "Not run" column, no scan under 1 ms,
//         bars that show the times, and every run behind its own button.
//   slow: the split, the scans and the cause are still there.
//   PHASE=before skips the checks and only takes the pictures (run against the old build).
//
//   OUTDIR=<dir> PHASE=after SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/speed-view-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'query');
mkdirSync(OUT, { recursive: true });
const strip = (f) => readFileSync(join(here, f), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const ALIGN = strip('query-notebook-align.js');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `query-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const WIDTHS = process.env.QUICK ? [1440] : [1440, 1090];
const THEMES = ['dark', 'light'];
const results = [];
const failures = [];
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

// Shots of the page in both themes at both widths, with the alignment measure on each.
async function shoot(page, slug, desc, { check = true, height = 800 } = {}) {
  for (const w of WIDTHS) {
    await page.setViewport({ width: w, height: w === 1440 ? 900 : height, deviceScaleFactor: 1 });
    await wait(350);
    for (const th of THEMES) {
      await applyTheme(page, th);
      await wait(350);
      if (check) {
        const a = await page.evaluate(`(${ALIGN})()`);
        if (a) {
          align.shots += 1;
          for (const k of ['offGrid', 'ragged', 'gaps', 'inner']) for (const x of a[k]) align[k].push(`${slug} ${w} ${th}: ${x}`);
        }
        const side = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
        expect(!side, `${slug} ${w} ${th}: the page scrolls sideways`);
      }
      const file = join(OUT, `${slug}-${w}-${th}.png`);
      await page.screenshot({ path: file });
      results.push({ slug, width: w, theme: th, file, desc });
    }
    await applyTheme(page, null);
  }
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await wait(200);
}

const PHASE = process.env.PHASE || 'after';
const check = (ok, what) => { if (PHASE === 'before') return ok; return expect(ok, what); };
const st = (avg, sd, mn, mx, runs) => ({ n: runs.length, avgMs: avg, stdDevMs: sd, minMs: mn, maxMs: mx, runsMs: runs });
const NONE = st(0, 0, 0, 0, []);
const FAST_RUNS = [79, 76, 77, 78, 67];
// Kane's shot: five repeat runs (no first run), 16 ms on the server, 72 ms round trip, one storage query from memory, 0 ms in storage.
const FAST = {
  benchmarkColdWarm: { runs: 5, cacheClearAvailable: true, traceAvailable: true, coldTotal: NONE, coldSe: NONE, warmTotal: st(75.4, 4.3, 67, 79, FAST_RUNS), warmSe: st(0, 0, 0, 0, [0, 0, 0, 0, 0]),
    detail: FAST_RUNS.map((ms, i) => ({ index: i + 1, cold: false, totalMs: ms, seMs: 0, seQueries: 1 })), rowCount: 4 },
  profileDax: { totalMs: 16, wallMs: 72, setupMs: 2, feMs: 16, seMs: 0, seCpuMs: 0, seQueries: 1, seCacheHits: 1, seParallelism: 0, rowCount: 4, traceAvailable: true,
    scans: [{ line: 1, kind: 'VertiPaqScan', subclass: 'Scan', durationMs: 0, cpuMs: 0, rows: 0, query: 'SELECT 1' }] },
};

async function measure(page, fast) {
  if (fast) await page.evaluate((r) => { for (const k of Object.keys(r)) window.__uishotHarness.setResponse(k, r[k]); }, FAST);
  await press(page, t('view-speed'));
  await press(page, t('speed-start')); await wait(2800);
}

try {
  for (const kind of ['fast', 'slow']) {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    await ask(page, 'total sales by region');
    await measure(page, kind === 'fast');
    const first = await text(page, t('speed-card'));
    await shoot(page, `${kind}-1-first`, `${kind} query: the Speed view right after Measure speed`);
    if (kind === 'fast') {
      check(/Fast: 75 ms\. Nothing to fix\./.test(await text(page, t('speed-insight'))), `fast: the headline is the answer (${await text(page, t('speed-insight'))})`);
      check(!/calculation|reading data|\d+%/.test(first), `fast: no split sentence on the first view`);
      check(!(await has(page, '.qv-splits')) && !(await has(page, '.qv-legend')), 'fast: no split bars and no legend');
    } else {
      check(/reading data|calculation/.test(await text(page, t('speed-insight'))), 'slow: the sentence still says where the time went');
      check(await has(page, '.qv-splits'), 'slow: the split bars are there');
    }
    await press(page, t('fold-time'));
    const detail = await text(page, t('time-card'));
    check(!/Not run/.test(detail), 'detail: no cell says Not run');
    if (kind === 'fast') {
      check(/Repeat runs only/.test(detail) && !/First run/.test(await text(page, t('coldwarm'))), 'fast: no First run column, one quiet "Repeat runs only"');
      check(!(await has(page, t('seq-0'))), 'fast: no scan of 0 ms is listed');
      const hs = await page.$$eval('.qv-run-bar', (b) => b.map((x) => x.getBoundingClientRect().height));
      check(hs.length === 5 && Math.abs(hs[4] / hs[0] - 67 / 79) < 0.02, `fast: the bars show the times (${hs.map((h) => h.toFixed(1)).join(', ')})`);
      check(/72 ms round trip/.test(detail) && /16 ms/.test(detail) && /on the server/.test(detail) && /One profiled run/.test(detail), 'fast: the server time and the round trip are both labelled');
    } else {
      check(await has(page, t('seq-0')), 'slow: the scans are listed');
      check(/First run/.test(await text(page, t('coldwarm'))), 'slow: the First run column is there');
    }
    check(!(await has(page, t('every-run'))), `${kind}: every run is closed by default`);
    await shoot(page, `${kind}-2-detail`, `${kind} query: Where the time went open, every run closed`);
    if (PHASE !== 'before') {
      await press(page, t('fold-every-run'));
      check(await has(page, t('every-run')), `${kind}: Show every run opens the table`);
      await shoot(page, `${kind}-3-every-run`, `${kind} query: every run open`, { check: false });
    }
    check(errors.length === 0, `${kind}: page errors ${errors.join('; ')}`);
    await page.close();
  }
} catch (e) {
  failures.push('driver crashed: ' + (e?.stack || e));
} finally {
  await browser.close();
  server.close();
}

for (const k of ['offGrid', 'ragged', 'gaps', 'inner']) check(align[k].length === 0, `alignment ${k}: ${[...new Set(align[k])].slice(0, 6).join(' ; ')}`);
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures, align }, null, 2));
console.log(`speed-view-drive (${PHASE}): ${results.length} screenshots in ${OUT}; alignment on ${align.shots} shots: off grid ${align.offGrid.length}, ragged ${align.ragged.length}, gaps ${align.gaps.length}, inner ${align.inner.length}`);
if (failures.length && PHASE !== 'before') { console.log('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log(`speed-view-drive: ${PHASE === 'before' ? 'pictures taken' : 'all checks passed'}`);
