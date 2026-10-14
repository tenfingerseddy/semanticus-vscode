// Verifier proofs for the Queries redesign (m11/queries-redesign 55792505, verify round 1, 27 Sep 2026). Each scene
// drives the REAL Studio through the mock engine (harness.html) and asserts what option C / D1 and the function
// inventory promise. Every scene here FAILED on 55792505 when it was written: they are the defect list, left failing
// on purpose, and a fix is proven when its scene passes.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/verify-queries-drive.mjs
//   ONLY=v1,v3 runs just those scenes.
//
//   v1  The Table kind keeps the result grid's row filter, per-column filters, Reset and Clear all (inventory C:
//       "Filter rows box: Table kind", "Per-column filter, chips, Reset, Clear all: Table kind").
//   v2  The Fields drawer can be resized by dragging its edge (inventory C: "Resize the field list: Drag the drawer edge").
//   v3  A failed profile in Measure speed does not leave the previous query's split and scans on the Speed view.
//   v4  The Speed tab's time does not keep describing a query that has since changed.
//   v5  In the Table kind every value sits under its own header (a spread column put a number under the next header).
//   v6  Closing a side sheet with Escape puts the keyboard focus back on the control that opened it.
//   v7  At 1440 x 900 no Speed button is cut off inside a scrolling box (the space rule: buttons are never inside a
//       scroller; the space family scores this state hidden-action 1).
//   v8  Explain this number on a question's answer carries the question's own filter (preflight H8: the wells cannot
//       hold filters, so a context built from them explains a different number).
//   v9  A date across runs left to right in time order on the engine's unordered rows (the rules' reason says so).
//   v10 An assistant's query of a different shape is not drawn or explained under the sentence's fields.
//   v11 An assistant run the server refused does not put a speed on the Speed tab.
//   v12 Compare a rewrite, while a question owns the query, compares the question's measure across its own split.
//   v13 In the DAX view a long query stays inside its editor box and never draws over the result.
//   v14 An assistant's benchmark shows on the Speed view even after the person measured another query.
//   v15 After a question the person can go back to their own sentence without losing it (only Reset does, and re-seeds).
//   v16 The Matrix kind's rows fill the result card, as the picked mockup draws them (verify round 2: the last
//       round-1 item still open; the builder named it and the fix round did not take it).
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-queries');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `verify-queries-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function open(q = 'tier=pro', size = [1440, 900]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=daxlab&conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => !x.disabled && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);
const speedBadge = (page) => page.evaluate(() => {
  const b = [...document.querySelectorAll('[aria-label="Result views"] button')].find((x) => x.textContent.trim().startsWith('Speed'));
  return b ? b.textContent.replace(/^Speed/, '').trim() : null;
});

const results = [];
async function scene(id, what, fn) {
  if (ONLY && !ONLY.includes(id)) return;
  const fails = [];
  const expect = (ok, msg) => { if (!ok) fails.push(msg); return ok; };
  let page;
  try {
    const opened = await open();
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

await scene('v1', 'the Table kind keeps the row filter, per-column filters, Reset and Clear all', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run');
  await wait(1200);
  expect(await clickText(page, 'Table', '[aria-label="Chart kind"]'), 'the Table kind');
  await wait(600);
  const box = await page.evaluate(() => !!document.querySelector('[data-testid="queries-result"] input[aria-label="Filter rows"]'));
  expect(box, 'no "Filter rows" box in the Table kind (the old Result grid had one; filterable={false} removes it)');
  const funnels = await page.evaluate(() => document.querySelectorAll('[data-testid="queries-result"] .xe-gcell button[title*="ilter"], [data-testid="queries-result"] .xe-gcell [aria-label*="ilter"]').length);
  expect(funnels > 0, 'no per-column filter control on the result headers');
});

await scene('v2', 'the Fields drawer can be resized by dragging its edge', async (page, expect) => {
  expect(await clickText(page, 'Fields'), 'Fields');
  await wait(500);
  const edge = await page.evaluate(() => {
    const d = document.querySelector('[data-testid="queries-fields"]');
    if (!d) return { drawer: false };
    const r = d.getBoundingClientRect();
    const h = [...document.querySelectorAll('*')].find((e) => getComputedStyle(e).cursor === 'col-resize' && Math.abs(e.getBoundingClientRect().left - r.right) < 24);
    if (!h) return { drawer: true, handle: false, w: r.width };
    const hr = h.getBoundingClientRect();
    return { drawer: true, handle: true, w: r.width, x: hr.left + hr.width / 2, y: hr.top + hr.height / 2 };
  });
  expect(edge.drawer, 'the Fields drawer did not open');
  if (!expect(edge.handle, 'the Fields drawer has no drag edge (no col-resize handle at its right edge)')) return;
  await page.mouse.move(edge.x, edge.y); await page.mouse.down(); await page.mouse.move(edge.x + 160, edge.y, { steps: 8 }); await page.mouse.up();
  await wait(400);
  const w2 = await page.evaluate(() => document.querySelector('[data-testid="queries-fields"]')?.getBoundingClientRect().width ?? 0);
  expect(w2 > edge.w + 40, `dragging the edge did not widen the drawer (${Math.round(edge.w)} -> ${Math.round(w2)} px)`);
});

await scene('v3', 'a failed profile does not leave the previous query\'s split and scans on the Speed view', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1500);
  const first = await page.evaluate(() => document.querySelector('[data-testid="queries-speed"]')?.textContent || '');
  expect(/formula engine/.test(first) && /Where the time went/.test(first), 'the first measure shows the split');
  // The person changes the question (a different query), and this time the server cannot record where the time went.
  expect(await page.evaluate(() => { const b = document.querySelector('[data-well-chip="cols"] button[aria-label^="Remove"]'); if (b) b.click(); return !!b; }), 'remove the across field');
  await wait(1200);
  await page.evaluate(() => {
    const st = (avg, sd, mn, mx, runs) => ({ n: runs.length, avgMs: avg, stdDevMs: sd, minMs: mn, maxMs: mx, runsMs: runs });
    window.__uishotHarness.setResponse('benchmarkColdWarm', { runs: 5, cacheClearAvailable: true, traceAvailable: true, coldTotal: st(2210, 40, 2170, 2250, [2250, 2170]), warmTotal: st(1810, 20, 1790, 1830, [1830, 1810, 1790]),
      coldSe: st(1500, 10, 1490, 1510, [1510, 1490]), warmSe: st(1200, 10, 1190, 1210, [1210, 1200, 1190]), detail: [], rowCount: 4 });
    window.__uishotHarness.setResponse('profileDax', { error: 'The trace could not be started on this server.', totalMs: 0, feMs: 0, seMs: 0, seCpuMs: 0, seQueries: 0, seParallelism: 0, rowCount: 0, scans: [], traceAvailable: false });
  });
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed again'); await wait(300);
  // The result is now another query's, so the first query's numbers are put away and the button reads Measure speed again.
  expect(await clickText(page, 'Measure'), 'Measure'); await wait(1500);
  const after = await page.evaluate(() => document.querySelector('[data-testid="queries-speed"]')?.textContent || '');
  expect(/1,?810 ms|1\.8\d* s/.test(after), 'the new repeat-run time shows: ' + after.slice(0, 160));
  expect(!/27%/.test(after) && !/SELECT 'Store'\[Store Region\], 'Date'\[Month\]/.test(after),
    'the Speed view still shows the PREVIOUS query\'s split (27% formula engine) and its heaviest scans next to the new query\'s times');
});

await scene('v4', 'the Speed tab\'s time does not keep describing a query that has since changed', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1500);
  const measured = await speedBadge(page);
  expect(measured === '96 ms', 'the measured repeat time labels the tab: ' + measured);
  await clickText(page, 'Result', '[aria-label="Result views"]'); await wait(200);
  // A different query: take the across field out. The sentence runs again by itself (12 ms in the harness).
  await page.evaluate(() => document.querySelector('[data-well-chip="cols"] button[aria-label^="Remove"]')?.click());
  await wait(1500);
  const runTime = await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-page"] span')].map((s) => s.textContent).find((t) => /rows? · /.test(t || '')) || '');
  const badge = await speedBadge(page);
  expect(badge !== '96 ms', `after the query changed and ran again (${runTime.trim()}), the Speed tab still reads "${badge}", the measurement of the previous query`);
});

await scene('v5', 'in the Table kind every value sits under its own header', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect(await clickText(page, 'Table', '[aria-label="Chart kind"]'), 'the Table kind'); await wait(600);
  const cols = await page.evaluate(() => {
    const root = document.querySelector('[data-testid="queries-result"]');
    const textBox = (el) => { const r = document.createRange(); r.selectNodeContents(el); const b = r.getBoundingClientRect(); return { l: b.left, r: b.right }; };
    const heads = [...root.querySelectorAll('.xe-gcell')].filter((c) => c.closest('.sticky'));
    const headSpans = heads.map((h) => h.querySelector('span.truncate') || h);
    const firstRow = [...root.querySelectorAll('.sem-grid-scroll .xe-gcell')].filter((c) => !c.closest('.sticky')).slice(0, headSpans.length);
    return headSpans.map((h, i) => ({ head: h.textContent.trim(), h: textBox(h), v: firstRow[i] ? textBox(firstRow[i]) : null, value: firstRow[i]?.textContent.trim() }));
  });
  expect(cols.length > 0, 'the table has headers');
  for (const c of cols) {
    if (!c.v) continue;
    const overlap = Math.min(c.h.r, c.v.r) - Math.max(c.h.l, c.v.l);
    expect(overlap > 0, `"${c.value}" sits ${Math.round(c.v.l - c.h.r)} px right of its header "${c.head}" (header text ${Math.round(c.h.l)}-${Math.round(c.h.r)}, value ${Math.round(c.v.l)}-${Math.round(c.v.r)})`);
  }
});

await scene('v6', 'closing a side sheet with Escape puts the focus back on the control that opened it', async (page, expect) => {
  const opener = await page.evaluate(() => { const b = document.querySelector('button[aria-label^="Compare a rewrite of"]'); if (!b) return null; b.focus(); return b.getAttribute('aria-label'); });
  expect(!!opener, 'the compare icon on the Show measure');
  await page.keyboard.press('Enter'); await wait(500);
  expect(await page.evaluate(() => !!document.querySelector('[data-testid="queries-sheet"]')), 'Enter on the compare icon opens the sheet');
  await page.keyboard.press('Escape'); await wait(400);
  const now = await page.evaluate(() => ({ open: !!document.querySelector('[data-testid="queries-sheet"]'), tag: document.activeElement?.tagName, label: document.activeElement?.getAttribute?.('aria-label') }));
  expect(!now.open, 'Escape closes the sheet');
  expect(now.label === opener, `after Escape the focus is on ${now.tag}${now.label ? ' "' + now.label + '"' : ''}, not back on "${opener}"`);
});

await scene('v7', 'at 1440 x 900 no Speed button is cut off inside a scrolling box (the space rule: buttons are never inside a scroller)', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1500);
  await page.evaluate(() => { const d = document.querySelector('[data-testid="queries-server-steps"]'); if (d) d.open = true; }); await wait(400);
  expect(await clickText(page, 'Record the steps'), 'Record the steps'); await wait(1500);
  const cut = await page.evaluate(() => {
    const pageScrolls = document.scrollingElement.scrollHeight > window.innerHeight + 1;
    const scroller = (el) => { for (let p = el.parentElement; p; p = p.parentElement) { const o = getComputedStyle(p).overflowY; if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight + 1) return p; } return null; };
    return [...document.querySelectorAll('[data-testid="queries-speed"] button')].filter((b) => b.checkVisibility?.() ?? b.offsetParent).map((b) => {
      const s = scroller(b); if (!s) return null;
      const r = b.getBoundingClientRect(), c = s.getBoundingClientRect();
      const vis = Math.max(0, Math.min(r.bottom, c.bottom) - Math.max(r.top, c.top)) / Math.max(1, r.height);
      return vis < 0.75 ? `"${b.textContent.trim()}" is ${Math.round(vis * 100)}% visible inside the Speed scroller` : null;
    }).filter(Boolean).concat(pageScrolls ? [] : []);
  });
  for (const c of cut) expect(false, c + ' while the page itself does not scroll');
});

await scene('v8', 'Explain this number on a question\'s answer carries the question\'s own filter (H8: the wells cannot hold filters)', async (page, expect) => {
  // A Pro answer to "total sales by region in 2025": the parts carry Year = 2025, as the engine's answer does.
  await page.evaluate(() => window.__uishotHarness.setResponse('askData', () => ({
    outcome: 'answered', readBy: 'jev', reading: 'Total Sales by Region, Year = 2025',
    parts: { measures: ['Total Sales'], groupBy: ["'Customer'[Region]"], filters: [{ column: "'Date'[Year]", op: 'is', values: [2025] }], top: null, topBy: null, lowest: false, sortBy: null, descending: false },
    dax: "DEFINE\n    VAR __Filter1 =\n        TREATAS ( { 2025 }, 'Date'[Year] )\n    VAR __Result =\n        SUMMARIZECOLUMNS (\n            'Customer'[Region],\n            __Filter1,\n            \"@Total Sales\", [Total Sales]\n        )\nEVALUATE\n    __Result\n",
    checks: [{ name: 'parts', passed: true, line: 'Every part is in the model.' }, { name: 'rows', passed: true, line: '4 rows.' }], unsure: [],
    columns: [{ name: 'Region', type: 'String' }, { name: 'Total Sales', type: 'Decimal' }],
    rows: [['Coast', 1234.5], ['Inland', 7843.25], ['North', 4120], ['South', 10610.75]], rowCount: 4, truncated: false, elapsedMs: 38,
  })));
  await page.focus('[data-testid="queries-ask"] input');
  await page.keyboard.type('total sales by region in 2025');
  await page.keyboard.press('Enter'); await wait(1500);
  expect(await page.evaluate(() => !!document.querySelector('[data-testid="queries-ask-chips"][data-owns="yes"]')), 'the answer\'s parts own the query');
  expect(await clickText(page, 'Table', '[aria-label="Chart kind"]'), 'the Table kind'); await wait(600);
  const cell = await page.evaluate(() => {
    const c = [...document.querySelectorAll('[data-testid="queries-result"] .sem-grid-scroll .xe-gcell')].find((x) => !x.closest('.sticky') && /^[\d.,]+$/.test(x.textContent.trim()) && Number(x.textContent.replace(/,/g, '')) > 100);
    if (!c) return null; const r = c.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, t: c.textContent.trim() };
  });
  if (!expect(!!cell, 'a number in the answer')) return;
  await page.mouse.click(cell.x, cell.y, { button: 'right' }); await wait(400);
  expect(await clickText(page, 'Explain this number', '[role="menu"]'), 'the number menu offers Explain this number'); await wait(900);
  const call = await page.evaluate(() => (window.__uishotHarness.messages || []).filter((m) => m && m.method === 'explainValue').pop() || null);
  if (!expect(!!call, 'Explain this number asked the engine (explainValue)')) return;
  const sent = JSON.stringify(call.params);
  expect(/Year/.test(sent) && /2025/.test(sent), `Explain this number for ${cell.t} was asked WITHOUT the question's Year = 2025 filter: ${sent.slice(0, 300)}`);
});

// The live engine returns SUMMARIZECOLUMNS rows in no particular order (seen on FrameworkTesting / Northstar Sales
// Working: 2024-03, 2024-06, 2025-03, 2025-08, 2025-11, 2024-08 ...). This is that shape, for the seeded sentence
// (one split by, one date across, one number).
const UNORDERED = {
  columns: [{ name: "'Customer'[CustomerName]", type: 'String' }, { name: "'Date'[MonthStart]", type: 'DateTime' }, { name: '[Total Sales]', type: 'Decimal' }],
  rows: [['Aria A.', '2024-03-01T00:00:00', 300], ['Aria A.', '2024-01-01T00:00:00', 100], ['Aria A.', '2024-02-01T00:00:00', 200],
    ['Ben B.', '2024-03-01T00:00:00', 330], ['Ben B.', '2024-01-01T00:00:00', 110], ['Ben B.', '2024-02-01T00:00:00', 220]],
  rowCount: 6, truncated: false, elapsedMs: 40,
};

await scene('v9', 'a date across runs left to right in time order on the engine\'s unordered rows (the rules say "Dates run left to right")', async (page, expect) => {
  await page.evaluate((r) => window.__uishotHarness.setResponse('runDax', r), UNORDERED);
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  const reason = await page.evaluate(() => document.querySelector('[data-testid="queries-chart-reason"]')?.textContent || '');
  expect(/left to right/.test(reason), 'the rules claim time runs left to right: ' + reason);
  expect(await clickText(page, 'Matrix', '[aria-label="Chart kind"]'), 'the Matrix kind'); await wait(500);
  const heads = await page.evaluate(() => [...document.querySelectorAll('[data-testid="queries-result"] thead th')].map((t) => t.textContent.trim()));
  const months = heads.filter((h) => /^[A-Z][a-z]{2} \d{4}$/.test(h));
  const order = months.map((m) => new Date(`1 ${m}`).getTime());
  expect(months.length === 3, 'three month columns: ' + heads.join(' | '));
  expect(order.every((t, i) => i === 0 || t > order[i - 1]), `the months run ${months.join(', ')}, not in time order (the line chart draws the same order)`);
});

await scene('v10', 'an assistant\'s query of a different shape is not drawn or explained under the sentence\'s fields', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  // The person's assistant runs its own query over MCP; the result is reflected into Queries (the "your assistant ran" banner).
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
    seq: 900, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true, rowCount: 4, elapsedMs: 31,
    query: "EVALUATE SUMMARIZECOLUMNS('Store'[Channel], \"Net Sales\", [Net Sales], \"Units\", [Units])",
    result: { columns: [{ name: "Store[Channel]", type: 'String' }, { name: '[Net Sales]', type: 'Decimal' }, { name: '[Units]', type: 'Int64' }],
      rows: [['Store', 2408967.75, 1200], ['Online', 437467.2, 300], ['Pop-up', 1500, 3], ['Trade', 99000, 40]], rowCount: 4, truncated: false, elapsedMs: 31,
      query: "EVALUATE SUMMARIZECOLUMNS('Store'[Channel], \"Net Sales\", [Net Sales], \"Units\", [Units])" } } } })));
  await wait(900);
  expect(await page.evaluate(() => /your assistant/i.test(document.body.textContent)), 'the assistant banner shows');
  expect(await clickText(page, 'Table', '[aria-label="Chart kind"]'), 'the Table kind'); await wait(500);
  const cell = await page.evaluate(() => {
    // Every number in the result: the last column (Units) is where the sentence's one value sits by position.
    const cells = [...document.querySelectorAll('[data-testid="queries-result"] .sem-grid-scroll .xe-gcell')].filter((x) => !x.closest('.sticky') && /^[\d.,]+$/.test(x.textContent.trim()));
    const c = cells.find((x) => x.textContent.trim() === '1,200') || cells[cells.length - 1];
    if (!c) return null; const r = c.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, t: c.textContent.trim() };
  });
  if (!expect(!!cell, 'a number in the assistant\'s result')) return;
  await page.mouse.click(cell.x, cell.y, { button: 'right' }); await wait(400);
  const offered = await page.evaluate(() => [...document.querySelectorAll('[role="menu"] button')].map((b) => b.textContent.trim()));
  if (!offered.some((t) => t.startsWith('Explain this number'))) return;   // not offered is an honest answer
  await clickText(page, 'Explain this number', '[role="menu"]'); await wait(900);
  const call = await page.evaluate(() => (window.__uishotHarness.messages || []).filter((m) => m && m.method === 'explainValue').pop() || null);
  const sent = JSON.stringify(call?.params ?? null);
  expect(!call || (/Units/.test(sent) && /Channel/.test(sent)),
    `Explain this number on the assistant's Units ${cell.t} (Store channel) asked the engine about the sentence's fields instead: ${sent.slice(0, 300)}`);
});

await scene('v11', 'an assistant run the server refused does not put a speed on the Speed tab', async (page, expect) => {
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
    seq: 901, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: false, rowCount: 0, elapsedMs: 0,
    query: "EVALUATE SUMMARIZECOLUMNS('Store'[Channel], \"Net Sales\", [Net Sales])",
    error: "This action (previewing rows of data on a 'prod' target) needs the user's approval.",
    result: { columns: [], rows: [], rowCount: 0, truncated: false, elapsedMs: 0, cancelled: false, policyRefused: true,
      error: "This action (previewing rows of data on a 'prod' target) needs the user's approval." } } } })));
  await wait(900);
  const badge = await speedBadge(page);
  expect(!badge || !/ms|s$/.test(badge), `the Speed tab reads "${badge}" for a query the server refused to run`);
});

await scene('v12', 'Compare a rewrite, while a question owns the query, compares the question\'s measure across its own split', async (page, expect) => {
  await page.focus('[data-testid="queries-ask"] input');
  await page.keyboard.type('total sales by region');
  await page.keyboard.press('Enter'); await wait(1500);
  expect(await page.evaluate(() => !!document.querySelector('[data-testid="queries-ask-chips"][data-owns="yes"]')), 'the answer\'s parts own the query (Total Sales by Region)');
  expect(await clickText(page, 'More tools for this result'), 'the more menu'); await wait(300);
  expect(await clickText(page, 'Compare a rewrite', '[role="menu"]'), 'Compare a rewrite in the more menu'); await wait(700);
  const sheet = await page.evaluate(() => document.querySelector('[data-testid="queries-compare"]')?.innerText || '');
  expect(/Region/.test(sheet) && !/Customer name|Order date key/.test(sheet),
    `the Compare sheet tests the hidden sentence's split instead of the question's (Region): ${sheet.replace(/\s+/g, ' ').slice(0, 220)}`);
});

await scene('v13', 'in the DAX view a long query stays inside its editor box and never draws over the result', async (page, expect) => {
  await page.focus('[data-testid="queries-ask"] input');
  await page.keyboard.type('total sales by region');
  await page.keyboard.press('Enter'); await wait(1500);
  expect(await clickText(page, 'DAX', '[role="group"]'), 'the DAX view'); await wait(800);
  const m = await page.evaluate(() => {
    const ed = document.querySelector('.cm-editor'); const card = ed?.closest('.sem-card'); const res = document.querySelector('[data-primary="result"]');
    const lines = [...document.querySelectorAll('.cm-line')];
    const lastLine = lines[lines.length - 1]?.getBoundingClientRect();
    return { lines: lines.length, cardBottom: card?.getBoundingClientRect().bottom, edBottom: ed?.getBoundingClientRect().bottom, lastLineBottom: lastLine?.bottom, resultTop: res?.getBoundingClientRect().top,
      clipped: card ? getComputedStyle(card).overflow : null };
  });
  expect(m.lines >= 9, `the question's DAX is ${m.lines} lines`);
  expect(m.edBottom <= m.cardBottom + 1 || m.clipped !== 'visible',
    `the editor runs ${Math.round(m.edBottom - m.cardBottom)} px past its box (box ends ${Math.round(m.cardBottom)}, editor ${Math.round(m.edBottom)}, overflow ${m.clipped})`);
  expect(!(m.lastLineBottom > m.resultTop && m.clipped === 'visible'), `the query's last line (bottom ${Math.round(m.lastLineBottom)}) is drawn over the result card (top ${Math.round(m.resultTop)})`);
});

await scene('v14', 'an assistant\'s benchmark shows on the Speed view even after the person measured another query', async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1000);
  expect(await clickText(page, 'Speed', '[aria-label="Result views"]'), 'Speed'); await wait(300);
  expect(await clickText(page, 'Measure speed'), 'Measure speed'); await wait(1500);
  // The person's assistant benchmarks its own query over MCP (benchmark_dax, warm): first 166 ms, fastest 69 ms.
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
    seq: 902, kind: 'benchmark_dax', origin: 'agent', label: 'Benchmarked the query', ok: true, rowCount: 6, elapsedMs: 340,
    query: "EVALUATE SUMMARIZECOLUMNS('Store'[Region], \"Net Sales\", [Net Sales])",
    result: { runs: 3, firstMs: 166, warmMinMs: 69, warmMedianMs: 105, runsMs: [166, 69, 105], rowCount: 6, note: 'Wall-clock only.' } } } })));
  await wait(900);
  const speed = await page.evaluate(() => document.querySelector('[data-testid="queries-speed"]')?.innerText || '');
  expect(/your assistant/i.test(await page.evaluate(() => document.body.innerText)), 'the assistant banner shows');
  expect(/\b69 ms\b/.test(speed) && /\b166 ms\b/.test(speed),
    `the assistant's benchmark (first 166 ms, fastest 69 ms) is shown nowhere on the Speed view; it still leads with the person's earlier measure: ${speed.replace(/\s+/g, ' ').slice(0, 200)}`);
});

await scene('v15', 'after a question the person can go back to their own sentence without losing it (only Reset does, and it re-seeds)', async (page, expect) => {
  // The person's own sentence: the seeded one with its across field taken out (Total Sales by CustomerName).
  expect(await page.evaluate(() => { const b = document.querySelector('[data-well-chip="cols"] button[aria-label^="Remove"]'); if (b) b.click(); return !!b; }), 'remove the across field');
  await wait(900);
  await page.focus('[data-testid="queries-ask"] input');
  await page.keyboard.type('total sales by region');
  await page.keyboard.press('Enter'); await wait(1500);
  expect(!(await page.evaluate(() => !!document.querySelector('[data-testid="queries-wells"]'))), 'the question\'s chips replace the sentence');
  // Every control on the page and in the more menu that could lead back, other than Reset (which re-seeds).
  await clickText(page, 'More tools for this result'); await wait(300);
  const labels = await page.evaluate(() => [...document.querySelectorAll('button')].filter((b) => b.getClientRects().length && !b.disabled).map((b) => (b.getAttribute('aria-label') || b.textContent || '').replace(/\s+/g, ' ').trim()));
  await page.keyboard.press('Escape'); await wait(200);
  const way = labels.find((l) => /my fields|my own|clear the question|put the question away|drop the question/i.test(l) && !/^Reset/.test(l) && !/Show the DAX|Back to my fields/.test(l));
  let back = false;
  if (way) {
    await clickText(page, way.slice(0, 24)); await wait(900);
    back = await page.evaluate(() => { const t = document.querySelector('[data-testid="queries-wells"]')?.textContent || ''; return /CustomerName/.test(t) && !/OrderDateKey/.test(t); });
  }
  expect(back, `no control brings back the person's own sentence (Total Sales by CustomerName); the only way back is Reset, which replaces it with the starting sentence. Controls seen: ${labels.slice(0, 40).join(' | ')}`);
});

await scene('v16', "the Matrix kind's rows fill the result card, as the mockup draws them", async (page, expect) => {
  expect(await clickText(page, 'Run the query'), 'Run'); await wait(1200);
  expect(await clickText(page, 'Matrix', '[aria-label="Chart kind"]'), 'the Matrix kind'); await wait(700);
  const m = await page.evaluate(() => {
    const canvas = document.querySelector('[data-testid="queries-result"]');
    const box = canvas?.querySelector('div.overflow-auto');
    const table = box?.querySelector('table');
    const last = [...(table?.querySelectorAll('tbody tr') ?? [])].pop();
    return {
      canvasH: canvas?.getBoundingClientRect().height ?? 0,
      boxH: box?.getBoundingClientRect().height ?? 0,
      boxBottom: box?.getBoundingClientRect().bottom ?? 0,
      tableH: table?.getBoundingClientRect().height ?? 0,
      lastBottom: last?.getBoundingClientRect().bottom ?? 0,
      scrolls: box ? box.scrollHeight > box.clientHeight + 1 : false,
    };
  });
  expect(m.boxH > 0 && m.tableH > 0, 'the matrix drew');
  // With more rows than fit, the box scrolls and there is nothing to stretch. With room to spare the rows must take
  // it: a matrix that stops a third of the way down leaves the card mostly empty (the build report names this, and
  // verify round 1 listed it under the lower items).
  if (m.scrolls) return;
  const gap = Math.round(m.boxBottom - m.lastBottom);
  expect(gap <= 8, `the Matrix rows stop ${gap} px above the bottom of the result card: the rows fill `
    + `${Math.round((m.tableH / Math.max(1, m.boxH)) * 100)}% of a ${Math.round(m.boxH)} px box, so the rest of the card is empty`);
});

await browser.close(); server.close();
writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 2));
const failed = results.filter((r) => !r.pass);
console.log(`verify-queries-drive: ${results.length - failed.length}/${results.length} scenes pass`);
process.exit(failed.length ? 1 : 0);
