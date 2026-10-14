// Query show (Kane, 13 Oct 2026): "when queries get run by assistant they dont show. you have to expand and they dont
// use the space very well". Drives the REAL Studio through the mock engine (harness.html).
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/query-show-drive.mjs
//   ONLY=q1,q2 runs just those scenes. SHOTS=1 also writes the dark and light screens at 1440 and 1000 wide.
//
//   q1  An assistant's run_dax (the person was on Speed, with their own Build wells) shows its RESULT at once: the Result
//       tab is selected, the rows are on screen, the DAX it ran is shown read-only, and the person's wells are not shown
//       as if they made it.
//   q2  Speed never shows numbers that belong to another query: one short line and Measure speed, no stale warning.
//   q3  Back to my fields brings the person's wells back and runs them.
//   q4  Run (the person's own) opens on Result, even from Speed.
//   q5  Speed is compact: no big empty gap between the figures and Server steps, and no empty Every run band.
//   q6  The result area fills the page height under an assistant's run.
//   p1  The person's own Run over an assistant result runs their query and brings the wells back.
//   p2  Speed never shows another query's numbers. p3  The assistant timing THIS query shows them, labelled.
//   p6  The row count is said once. p7  No empty band under Server steps when nothing is measured.
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'query-show');
mkdirSync(OUT, { recursive: true });
const ONLY = process.env.ONLY ? process.env.ONLY.split(',') : null;
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `query-show-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function open(size = [1440, 900], theme = 'dark') {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=daxlab&conn=1&live=1&tier=pro`, { waitUntil: 'networkidle0', timeout: 60000 });
  await applyTheme(page, theme);
  await wait(2500);
  return { page, errors };
}
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => !x.disabled && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);

const ASSISTANT_DAX = "EVALUATE FILTER(ADDCOLUMNS(VALUES('Store'[Channel]), \"Net Sales\", [Net Sales]), [Net Sales] > 0)";
const ASSISTANT_ROWS = [['Store', 2408967.75], ['Online', 437467.2], ['Pop-up', 1500], ['Trade', 99000]];
const assistantRun = (page) => page.evaluate((dax, rows) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
  seq: 910, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true, rowCount: rows.length, elapsedMs: 310, query: dax,
  result: { columns: [{ name: 'Store[Channel]', type: 'String' }, { name: '[Net Sales]', type: 'Decimal' }], rows, rowCount: rows.length, truncated: false, elapsedMs: 310, query: dax } } } })), ASSISTANT_DAX, ASSISTANT_ROWS);
const pressedTab = (page) => page.evaluate(() => [...document.querySelectorAll('[aria-label="Result views"] button')].find((b) => b.getAttribute('aria-pressed') === 'true')?.textContent.replace(/[\d.,]+\s*(ms|s)?$/, '').trim() ?? null);
const text = (page, sel) => page.evaluate((s) => document.querySelector(s)?.innerText ?? null, sel);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);
// The person's own query, measured on Speed, then the assistant runs a different one.
async function personMeasuresThenAssistantRuns(page, expect) {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1800);
  await assistantRun(page); await wait(900);
}

const results = [];
async function scene(id, what, fn, size, theme) {
  if (ONLY && !ONLY.includes(id)) return;
  const fails = [];
  const expect = (ok, msg) => { if (!ok) fails.push(msg); return ok; };
  let page;
  try {
    const opened = await open(size, theme);
    page = opened.page;
    await fn(page, expect);
    await page.screenshot({ path: `${OUT}/${id}.png` });
    expect(opened.errors.length === 0, 'page errors: ' + opened.errors.join('; '));
  } catch (e) {
    fails.push('threw: ' + String(e?.message ?? e).slice(0, 300));
  } finally { if (page) await page.close(); }
  results.push({ id, what, pass: fails.length === 0, fails });
  console.log(`${fails.length ? 'FAIL' : 'PASS'} ${id}: ${what}${fails.length ? '\n  - ' + fails.join('\n  - ') : ''}`);
}

await scene('q1', 'an assistant run shows its result at once: Result selected, its rows, its DAX, not the person\'s wells', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  const tab = await pressedTab(page);
  expect(tab === 'Result', `the selected view is "${tab}", not Result`);
  const grid = (await text(page, '[data-testid="queries-result"]')) ?? '';
  expect(/Pop-up/.test(grid) && /Trade/.test(grid), 'the assistant\'s rows are not on screen: ' + grid.replace(/\s+/g, ' ').slice(0, 120));
  const dax = (await text(page, '[data-testid="queries-assistant-dax"]')) ?? '';
  expect(/ADDCOLUMNS/.test(dax), 'the DAX the assistant ran is not shown read-only: ' + dax.slice(0, 80));
  expect(!(await has(page, '[data-testid="queries-wells"]')), 'the person\'s Build wells are shown beside the assistant\'s result');
});

await scene('q2', 'Speed shows no numbers that belong to another query, only one short line and Measure speed', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(400);
  const sp = (await text(page, '[data-testid="queries-speed"]')) ?? '';
  expect(!(await has(page, '[data-testid="queries-speed-stale"]')) && !/measured before/.test(sp), 'the stale warning shows');
  expect(!(await has(page, '[data-testid="queries-speed-answer"]')), 'speed figures of another query are on screen');
  expect(!/Every run/.test(sp), 'an Every run section of another query is on screen');
  expect(await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-speed"] button')].some((b) => /^Measure speed/.test(b.textContent.trim()))), 'no Measure speed button');
  expect(await page.evaluate(() => /Measure speed/.test(document.querySelector('[data-testid="queries-speed"] button')?.textContent ?? '')), 'Measure speed is not first');
});

await scene('q3', 'Back to my fields brings the wells back', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  expect(await clickText(page, 'Back to my fields'), 'a Back to my fields button'); await wait(1500);
  expect(await has(page, '[data-testid="queries-wells"]'), 'the wells did not come back');
  expect(!(await has(page, '[data-testid="queries-assistant-dax"]')), 'the assistant\'s DAX is still shown');
});

await scene('q4', 'Run opens on Result, even from Speed', async (page, expect) => {
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect((await pressedTab(page)) === 'Result', 'Result is not selected after Run');
});

await scene('q5', 'Speed is compact: no big gap above Server steps, nothing crushed into a thin band', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1800);
  const m = await page.evaluate(() => {
    const box = document.querySelector('[data-testid="queries-speed-numbers"]'); const steps = document.querySelector('[data-testid="queries-server-steps"]');
    const kids = box ? [...box.children] : []; const last = kids[kids.length - 1];
    const crushed = [...document.querySelectorAll('[data-testid="queries-speed"] details')].filter((d) => d.scrollHeight > d.clientHeight + 2).length;
    return { scrolls: !!box && box.scrollHeight > box.clientHeight + 1, crushed,
      gap: steps && last ? Math.round(steps.getBoundingClientRect().top - last.getBoundingClientRect().bottom) : -1 };
  });
  expect(m.crushed === 0, `${m.crushed} section(s) are cut off inside their box`);
  expect(m.scrolls || (m.gap >= 0 && m.gap <= 40), `${m.gap} px of empty space between the figures and Server steps`);
}, [1440, 1300]);

await scene('q6', 'the result area fills the page height under an assistant\'s run', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  const m = await page.evaluate(() => {
    const r = document.querySelector('[data-primary="result"]')?.getBoundingClientRect(); const pg = document.querySelector('[data-testid="queries-page"]')?.getBoundingClientRect();
    return { top: r?.top ?? 0, bottom: r?.bottom ?? 0, pageBottom: pg?.bottom ?? 0, h: r?.height ?? 0 };
  });
  expect(m.h > 250 && m.pageBottom - m.bottom <= 32, `the result is ${Math.round(m.h)} px tall and ends ${Math.round(m.pageBottom - m.bottom)} px above the page bottom (the page padding is 24)`);
});

const prof = (page, q, seq) => page.evaluate((q, seq) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
  seq, kind: 'profile_dax', origin: 'agent', label: 'Profiled server timings', ok: true, elapsedMs: 77, query: q,
  result: { totalMs: 4321, feMs: 1000, seMs: 3321, seCpuMs: 3000, seQueries: 3, seParallelism: 1, rowCount: 4, scans: [], traceAvailable: true } } } })), q, seq);

await scene('p1', 'the person\'s own Run over an assistant result runs their query and shows the wells again', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1500);
  expect(await has(page, '[data-testid="queries-wells"]'), 'Run did not bring the wells back');
  expect(!(await has(page, '[data-testid="queries-assistant-dax"]')), 'the assistant\'s DAX is still shown after Run');
  expect(!/Pop-up/.test((await text(page, '[data-testid="queries-result"]')) ?? ''), 'the assistant\'s rows are still the result after Run');
});

await scene('p2', 'Speed never shows another query\'s numbers, even when the assistant timed one while its run is on screen', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  await prof(page, 'EVALUATE ROW("x", [Net Sales])', 920); await wait(900);
  const sp = (await text(page, '[data-testid="queries-speed"]')) ?? '';
  expect(!/4[,.]?321|4\.3/.test(sp), 'Speed shows another query\'s numbers: ' + sp.replace(/\s+/g, ' ').slice(0, 160));
  expect(!(await has(page, '[data-testid="queries-speed-timed"]')), 'Timed by your assistant shows for another query');
  expect(!/ROW\(/.test(sp), 'the other query\'s text is on Speed');
});

await scene('p3', 'the assistant timing THIS query shows its numbers and "Timed by your assistant", without repeating the DAX', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  await prof(page, ASSISTANT_DAX, 921); await wait(900);
  const sp = (await text(page, '[data-testid="queries-speed"]')) ?? '';
  expect(/4[,.]?321|4\.3/.test(sp), 'the numbers for this query are not shown: ' + sp.replace(/\s+/g, ' ').slice(0, 160));
  expect(/Timed by your assistant/.test(sp), 'no "Timed by your assistant" line');
  expect(!/EVALUATE/.test(sp), 'the query text is repeated on Speed');
});

await scene('p6', 'the result header says the row count once', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  const card = (await text(page, '[data-primary="result"]')) ?? '';
  const n = (card.match(/\b4 rows\b/g) ?? []).length;
  expect(n === 1, `"4 rows" appears ${n} times in the result card`);
});

await scene('p7', 'with nothing measured the Speed panel ends after Server steps, with no empty band under it', async (page, expect) => {
  await personMeasuresThenAssistantRuns(page, expect);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(500);
  const m = await page.evaluate(() => {
    const card = document.querySelector('[data-primary="result"]')?.getBoundingClientRect(); const steps = document.querySelector('[data-testid="queries-server-steps"]')?.getBoundingClientRect();
    return { under: card && steps ? Math.round(card.bottom - steps.bottom) : -1 };
  });
  expect(m.under >= 0 && m.under <= 40, `${m.under} px of empty card under Server steps`);
});

if (process.env.SHOTS) {
  for (const [w, h] of [[1440, 900], [1000, 800]]) for (const theme of ['dark', 'light']) {
    const { page } = await open([w, h], theme);
    const ex = () => true;
    await personMeasuresThenAssistantRuns(page, ex);
    await page.screenshot({ path: `${OUT}/assistant-result-${theme}-${w}.png` });
    await clickText(page, 'Speed', '[aria-label="Result views"]'); await wait(500);
    await page.screenshot({ path: `${OUT}/assistant-speed-${theme}-${w}.png` });
    await page.close();
  }
}

await browser.close(); server.close();
writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2));
const failed = results.filter((r) => !r.pass);
console.log(`query-show-drive: ${results.length - failed.length}/${results.length} scenes pass`);
process.exit(failed.length ? 1 : 0);
