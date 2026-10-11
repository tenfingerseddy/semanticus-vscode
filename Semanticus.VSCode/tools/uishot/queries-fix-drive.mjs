// Proofs for the Queries redesign fix round (m11/queries-redesign, after verify round 1). The verifier's own scenes
// (verify-queries-drive.mjs, v1 to v15) prove the defects it wrote tests for; these cover the rest of its list, the
// ones it left without a failing test: D12 (the rows and chips half of Open in Queries) and the lower copy items.
// Each scene FAILED on df69ac19 (the head the verifier left) and passes on the fix.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/queries-fix-drive.mjs
//   ONLY=f1,f3 runs just those scenes.
//
//   f1  Open in Queries from Ask your data lands the chips, the editor's DAX and the rows, with no second ask and no run.
//   f2  "Put back my query" goes away once the person types a newer query over the question's DAX.
//   f3  While a query runs, the result says it is running, not "Press Run".
//   f4  A check saved from a question in Queries says it came from Queries, not from Ask your data.
//   f5  The Fields list marks the fields the result on screen uses.
//   f6  The last comparison's verdict is visible in the DAX view without opening the sheet.
//   f7  Speed speaks no engine shorthand: no FE, SE, Subclass or xmSQL.
//   f8  The staleness note sits in the result's own line, beside the rows it is about.
//   f9  D6: on Pro, a chart Jev picked is labelled "Jev picked", the rules' pick is marked beside it, and one click
//       changes it and one goes back; while the engine answers "rules" (the point is off) the label stays the rules'.
//   f10 D6: Free never asks for a chart pick.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'queries-fix');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `queries-fix-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function open(q, tab = 'daxlab') {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=${tab}&conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => !x.disabled && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);
const methods = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).map((m) => m && m.method).filter(Boolean));
const count = (list, m) => list.filter((x) => x === m).length;
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
async function askQueries(page, words) {
  await page.focus('[data-testid="queries-ask"] input');
  await page.keyboard.type(words);
  await page.keyboard.press('Enter'); await wait(1500);
}

const results = [];
async function scene(id, what, q, fn, tab) {
  if (ONLY && !ONLY.includes(id)) return;
  const fails = [];
  const expect = (ok, msg) => { if (!ok) fails.push(msg); return ok; };
  let page;
  try {
    const opened = await open(q, tab);
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

await scene('f1', 'Open in Queries lands the chips, the DAX and the rows, with no second ask and no run', 'tier=pro', async (page, expect) => {
  // Build "Total Sales by Region" on the Ask your data page by its chips, as a person would.
  const chip = (k) => page.evaluate((kind) => { const b = document.querySelector(`[data-chip="${kind}"]`); if (b) b.click(); return !!b; }, k);
  const pick = async (label) => { await wait(200); return clickText(page, label, '[data-testid="askdata-menu"]'); };
  await chip('add'); await pick('Total Sales'); await wait(800);
  await chip('add'); await pick('A column to split by'); await wait(200);
  await pick('Region'); await wait(900);
  expect(/rows/.test(await text(page, '[data-testid="askdata-answer"]')), 'the page answered');
  const before = await methods(page);
  expect(await clickText(page, 'Open in Queries'), 'Open in Queries on the answer'); await wait(1800);
  const after = await methods(page);
  const s = await page.evaluate(() => ({
    chips: !!document.querySelector('[data-testid="queries-ask-chips"][data-owns="yes"]'),
    region: /Region/.test(document.querySelector('[data-testid="queries-ask-chips"]')?.textContent || ''),
    dax: /SUMMARIZECOLUMNS/.test(document.querySelector('.cm-content')?.textContent || ''),
    rows: [...document.querySelectorAll('[aria-label="Result views"] button')].find((b) => b.textContent.trim().startsWith('Result'))?.textContent.replace(/^Result/, '').trim() || '',
  }));
  expect(s.dax, 'the editor holds the engine\'s DAX');
  expect(s.chips && s.region, 'the question\'s chips are on the sentence row, and they own the query');
  expect(/^\d+/.test(s.rows) && Number.parseInt(s.rows, 10) > 0, `the result holds the answer's rows (Result tab reads "${s.rows}")`);
  expect(count(after, 'askData') === count(before, 'askData'), `no second ask (askData ${count(before, 'askData')} -> ${count(after, 'askData')})`);
  expect(count(after, 'runDax') === count(before, 'runDax'), `no run (runDax ${count(before, 'runDax')} -> ${count(after, 'runDax')})`);
}, 'askdata');

await scene('f2', '"Put back my query" goes away once a newer query is typed over the question\'s DAX', 'tier=pro', async (page, expect) => {
  expect(await clickText(page, 'DAX', '[role="group"]'), 'the DAX view'); await wait(500);
  await page.click('.cm-content'); await page.keyboard.down('Control'); await page.keyboard.press('a'); await page.keyboard.up('Control');
  await page.keyboard.type('EVALUATE ROW("mine", 1)'); await wait(300);
  await askQueries(page, 'total sales by region');
  expect(!!(await page.$('[data-testid="queries-putback"]')), 'the question kept the person\'s query and offers it back');
  await page.click('.cm-content'); await page.keyboard.down('Control'); await page.keyboard.press('a'); await page.keyboard.up('Control');
  await page.keyboard.type('EVALUATE ROW("newer", 2)'); await wait(500);
  expect(!(await page.$('[data-testid="queries-putback"]')), 'after a newer query is typed, "Put back my query" still offers to replace it with the older one');
});

await scene('f3', 'while a query runs, the result says it is running, not "Press Run"', 'tier=pro&dax=hang', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(700);
  const body = await text(page, '[data-testid="queries-result"]');
  expect(!/Press Run/.test(body), `while the query runs the result reads "${body}"`);
  expect(/Running the query/.test(body), `the result says the query is running: "${body}"`);
});

await scene('f4', 'a check saved from a question in Queries says it came from Queries', 'tier=pro', async (page, expect) => {
  await askQueries(page, 'total sales by region');
  expect(await clickText(page, 'Save measure check'), 'Save measure check'); await wait(300);
  expect(await clickText(page, 'Save a number from this answer'), 'Save a number from this answer'); await wait(500);
  expect(await clickText(page, 'Continue'), 'Continue in the chooser'); await wait(1000);
  const body = await page.evaluate(() => document.body.innerText);
  expect(/This number came from your question in Queries/.test(body) && !/This number came from Ask your data/.test(body),
    'the check drawer says the number came from Ask your data, from a question asked in Queries');
});

await scene('f5', 'the Fields list marks the fields the result on screen uses', 'tier=pro', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Fields'), 'Fields'); await wait(400);
  await page.type('[data-testid="queries-fields"] input[aria-label="Find a field"]', 'Total Sales'); await wait(400);
  const marked = await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-fields"] [data-field-in-use]')].map((m) => m.parentElement?.textContent || ''));
  expect(marked.some((t) => /Total Sales/.test(t)), 'Total Sales, which the sentence shows, carries no in-use mark in Fields');
});

await scene('f6', 'the last comparison\'s verdict is visible in the DAX view without opening the sheet', 'tier=pro', async (page, expect) => {
  expect(await page.evaluate(() => { const b = document.querySelector('button[aria-label^="Compare a rewrite of"]'); if (b) b.click(); return !!b; }), 'the compare icon'); await wait(500);
  expect(await clickText(page, 'Compare', '[data-testid="queries-compare"]'), 'Compare'); await wait(900);
  await page.keyboard.press('Escape'); await wait(300);
  expect(await clickText(page, 'DAX', '[role="group"]'), 'the DAX view'); await wait(500);
  const v = await text(page, '[data-testid="queries-page"]');
  expect(/Last comparison: match/.test(v), 'no verdict for the last comparison in the DAX view');
});

await scene('f7', 'Speed speaks no engine shorthand', 'tier=pro', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1500);
  const speed = await page.evaluate(() => document.querySelector('[data-testid="queries-speed"]')?.innerText || '');
  const hits = (speed.match(/\bFE\b|\bSE\b|Subclass|xmSQL/g) || []);
  expect(hits.length === 0, `Speed shows engine shorthand: ${[...new Set(hits)].join(', ')}`);
});

await scene('f8', 'the staleness note sits in the result\'s own line', 'tier=pro&dirty=1', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  const where = await page.evaluate(() => {
    const note = document.querySelector('.query-stale'); const card = document.querySelector('[data-primary="result"]');
    return { note: !!note, inCard: !!(note && card && card.contains(note)) };
  });
  expect(where.note, 'the staleness note shows while there are unsaved edits');
  expect(where.inCard, 'the staleness note sits above the result, outside the result card, not in its own line');
});

await scene('f9', 'a chart Jev picked is labelled as Jev\'s; a "rules" answer keeps the rules\' label', 'tier=pro', async (page, expect) => {
  // The point is off by default, so the engine answers "rules": the label must say so.
  await page.evaluate(() => window.__uishotHarness.setResponse('pickChartKind', { kind: 'line', by: 'rules', reason: 'Numbers over time read best as a line from left to right.', note: "Jev's chart pick is off until it clears its bar." }));
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect(/Picked by the chart rules/.test(await text(page, '[data-testid="queries-chart-reason"]')), 'a "rules" answer keeps the rules\' label');
  const asked = await page.evaluate(() => (window.__uishotHarness.messages || []).filter((m) => m && m.method === 'pickChartKind').pop() || null);
  expect(!!asked, 'Pro asks the engine for the chart pick');
  const sent = JSON.stringify(asked?.params ?? null);
  expect(!/Aria|Ben B|\d{4,}/.test(sent), 'the ask carries the result\'s shape, never its rows: ' + sent.slice(0, 200));
  // Switched on and sure, Jev picks Matrix for the same shape.
  await page.evaluate(() => window.__uishotHarness.setResponse('pickChartKind', { kind: 'matrix', by: 'jev', reason: 'Rows and columns of numbers read best as a grid.', confidence: 0.93 }));
  await page.evaluate(() => { const b = document.querySelector('[data-well-chip="cols"] button[aria-label^="Remove"]'); b?.click(); }); await wait(1400);
  await page.evaluate(() => window.__uishotHarness.setResponse('runDax', undefined));
  const r1 = await text(page, '[data-testid="queries-chart-reason"]');
  const pressed = await page.evaluate(() => [...document.querySelectorAll('[aria-label="Chart kind"] button[aria-pressed="true"]')].map((b) => b.textContent.trim()));
  if (/Jev picked/.test(r1)) {
    // The engine's reason no longer names the kind, so the label does not read "Jev picked Matrix: Matrix: ..."
    // (verify round 2, lower item; the engine side is ChartPickTests.A_reason_does_not_name_the_kind_...).
    expect((r1.match(/Matrix/g) || []).length === 1, 'the label names the kind once: ' + r1);
    expect(pressed.some((t) => t.startsWith('Matrix')), 'Jev\'s pick is the chart shown: ' + pressed.join(','));
    expect(await clickText(page, 'Table', '[aria-label="Chart kind"]'), 'one click changes it'); await wait(400);
    expect(/Your pick/.test(await text(page, '[data-testid="queries-chart-reason"]')), 'the person\'s pick is labelled theirs');
    expect(await clickText(page, "Back to Jev's pick"), 'one click goes back to Jev\'s pick'); await wait(400);
    expect(/Jev picked Matrix/.test(await text(page, '[data-testid="queries-chart-reason"]')), 'back on Jev\'s pick');
  } else expect(false, 'a pick made by Jev is not labelled as Jev\'s: ' + r1);
});

await scene('f10', 'Free never asks for a chart pick', 'tier=free', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1400);
  const calls = (await methods(page)).filter((m) => m === 'pickChartKind').length;
  expect(calls === 0, `Free sent ${calls} pickChartKind calls`);
  expect(/Picked by the chart rules/.test(await text(page, '[data-testid="queries-chart-reason"]')), 'Free shows the rules\' pick');
});

await browser.close(); server.close();
writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2));
const failed = results.filter((r) => !r.pass);
console.log(`queries-fix-drive: ${results.length - failed.length}/${results.length} scenes pass`);
process.exit(failed.length ? 1 : 0);
