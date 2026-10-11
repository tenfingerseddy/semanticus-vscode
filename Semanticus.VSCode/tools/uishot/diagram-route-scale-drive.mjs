// Driven proof that the routed Diagram stays responsive at every model size Kane named (27 Sep ruling: lines route
// through the gaps, never behind or through a card, and the page never freezes). Serves the built bundle, opens the
// real harness with a 1, 5, 11, 30, 80 and 150-table model at 1440x900, and on the live page times the three
// moments that froze before (m11/diagram-route D1): opening the Diagram, dragging one card, and clicking a line.
// "Frozen" is the longest single task the page ran (Chrome's long-task timing), which is what a person feels as
// the page not answering. Each size also checks what is drawn: every line routed, none through a card, none on
// top of another, and (30 and up) the same with every card open. Last, the other door: a tidy grid of cards the
// person's assistant saves (save_layout, which reaches the page as layoutDidChange) with 40 and 20 px gaps, which
// sent lines through cards before (m11/diagram-route verification D2).
//   node diagram-route-scale-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SIZES=1,5,11 to pick; SIZES=300
//   adds the 300-table model, left out by default because it alone takes minutes)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-scale');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 900000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function settle(page, min = 1000) {
  await wait(min);
  const t0 = Date.now(); let calm = 0;
  while (Date.now() - t0 < 300000 && calm < 2) { const a = Date.now(); await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r()))); calm = Date.now() - a < 200 ? calm + 1 : 0; await wait(250); }
  // Making room and the routing pass run a few ms at a time (review round 5), so frames stay quick while they work:
  // wait until the page is not making room and no line is still held (drawn faded) on its old route too.
  while (Date.now() - t0 < 300000 && await page.evaluate(() => !!document.querySelector('[data-making-room], .react-flow__edge g[style*="opacity: 0.4"]'))) await wait(300);
}

// A model of n tables: about one fact in ten, one unlinked in seven, the rest lookup tables; each fact uses up to
// nine lookup tables and a few lookup tables hang off others (the same shape as test/diagram-route-scale.test.mjs).
const tbl = (name, o = {}) => Object.assign({ ref: 'table:' + name, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [name.replace(/ /g, '') + 'Key'], hasDescription: true }, o);
const rel = (fromTable, toTable) => ({ name: fromTable + '_' + toTable, fromTable, fromColumn: toTable.replace(/ /g, '') + 'Key', toTable, toColumn: toTable.replace(/ /g, '') + 'Key', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
function model(n) {
  const nf = Math.max(1, Math.round(n / 10)), ni = n > 4 ? Math.round(n / 7) : 0, nd = Math.max(0, n - nf - ni);
  const pad = (i) => String(i + 1).padStart(3, '0');
  const facts = Array.from({ length: nf }, (_, i) => 'Fact ' + pad(i)), dims = Array.from({ length: nd }, (_, i) => 'Dim ' + pad(i)), loose = Array.from({ length: ni }, (_, i) => 'Loose ' + pad(i));
  const rels = [], seen = new Set();
  const add = (m, o) => { const r = rel(m, o); if (m !== o && !seen.has(r.name)) { seen.add(r.name); rels.push(r); } };
  facts.forEach((f, fi) => { for (let k = 0; k < Math.min(9, nd); k++) add(f, dims[(fi * 7 + k * 5) % nd]); });
  for (let i = 0; i + 1 < nd && i < 16; i += 2) add(dims[i], dims[nd - 1 - i]);
  return { tables: facts.map((t, i) => tbl(t, { measures: 4 + (i % 5), columns: 8 + (i % 5) * 2, keyColumns: [] })).concat(dims.map((t, i) => tbl(t, { isDateTable: i === 0, columns: 3 + (i % 7) * 2 })))
    .concat(loose.map((t, i) => tbl(t, { isCalculated: i % 3 === 0, columns: 2 + (i % 4), keyColumns: [] }))), relationships: rels };
}
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));

const probe = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  const cards = [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom };
  });
  const lines = [...document.querySelectorAll('.react-flow__edge')].map((e) => {
    const d = (e.querySelector('path.react-flow__edge-path')?.getAttribute('d') || '').trim();
    const n = (d.match(/-?\d+(\.\d+)?/g) || []).map(Number);
    const pts = []; for (let i = 0; i + 1 < n.length; i += 2) pts.push({ x: n[i], y: n[i + 1] });
    return { id: (e.getAttribute('data-testid') || '').replace(/^rf__edge-/, ''), routed: /^M[-\d.,\sL]+$/.test(d), pts };
  });
  return { cards, lines };
});
// The same measures as diagram-route-verify-drive.mjs: a shared straight run within 1.5 px, and strict insides.
function sharedRun(A, B) {
  let run = 0;
  for (let i = 1; i < A.length; i++) for (let j = 1; j < B.length; j++) {
    const a = A[i - 1], b = A[i], c = B[j - 1], d = B[j];
    if (Math.abs(a.x - b.x) < 0.5 && Math.abs(c.x - d.x) < 0.5 && Math.abs(a.x - c.x) < 1.5) run += Math.max(0, Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y)));
    else if (Math.abs(a.y - b.y) < 0.5 && Math.abs(c.y - d.y) < 0.5 && Math.abs(a.y - c.y) < 1.5) run += Math.max(0, Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x)));
  }
  return run;
}
const drawn = (p) => {
  const unrouted = p.lines.filter((l) => !l.routed).map((l) => l.id);
  const through = new Set();
  for (const l of p.lines) for (let i = 1; i < l.pts.length; i++) { const a = l.pts[i - 1], b = l.pts[i]; for (const c of p.cards) if (Math.min(a.x, b.x) < c.x + c.w - 0.5 && Math.max(a.x, b.x) > c.x + 0.5 && Math.min(a.y, b.y) < c.y + c.h - 0.5 && Math.max(a.y, b.y) > c.y + 0.5) through.add(l.id); }
  let onTop = 0; for (let i = 0; i < p.lines.length; i++) for (let j = i + 1; j < p.lines.length; j++) if (sharedRun(p.lines[i].pts, p.lines[j].pts) > 8) onTop++;
  return { unrouted, through: [...through], onTop };
};
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
// The longest task since the last read, and the sum of all long tasks (over 50 ms), from Chrome's long-task timing.
const longTasks = (page) => page.evaluate(() => { const l = window.__LONG__.splice(0); return { max: Math.max(0, ...l), sum: l.reduce((a, b) => a + b, 0) }; });

// Budgets for the longest single task, set well above what the routed build measures on a loaded laptop in this
// software-painted harness (see the lane report) and well below what the per-line routing froze for (80 tables:
// 1.3 s on a click, whole seconds per drag step here). Opening a big model is a one-off; a drag step or a click is
// what a person feels most. Expand all on a wrapped model moves every card below an opened one, and each move used
// to have React Flow re-measure the cards one at a time (80 store updates for 80 cards).
const BUDGET = { open: { 1: 1500, 5: 1500, 11: 1500, 30: 2000, 80: 3000, 150: 5000, 300: 8000 }, expand: { 30: 2000, 80: 3000, 150: 8000, 300: 12000 }, drag: { 1: 250, 5: 250, 11: 250, 30: 300, 80: 600, 150: 1000, 300: 2000 }, click: { 1: 250, 5: 250, 11: 250, 30: 250, 80: 300, 150: 400, 300: 2500 } };
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };
const SIZES = (process.env.SIZES ?? '1,5,11,30,80,150').split(',').filter(Boolean).map(Number);

for (const n of SIZES) {
  const id = `tables-${n}`;
  const graph = model(n);
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => {
    window.__UISHOT__ = cfg; window.__LONG__ = [];
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__LONG__.push(e.duration); }).observe({ type: 'longtask', buffered: true });
  }, { graph, columns: columnsFor(graph) });
  const t0 = Date.now();
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 120000 });
  await page.waitForSelector('.react-flow__node', { timeout: 120000 });
  await settle(page, 1500);
  const opened = Date.now() - t0;
  const open = await longTasks(page);
  await applyTheme(page, 'dark'); await settle(page, 500); await longTasks(page);
  let p = await probe(page), d = drawn(p);
  check(id, 'open: every table on the canvas', p.cards.length === n, `${p.cards.length}/${n}`);
  check(id, `open: longest freeze under ${BUDGET.open[n]} ms`, open.max < BUDGET.open[n], `longest ${Math.round(open.max)} ms, long tasks ${Math.round(open.sum)} ms, settled in ${opened} ms, ${p.lines.length} lines`);
  check(id, 'open: every line routed, none through a card, none on top of another', !d.unrouted.length && !d.through.length && !d.onTop, `${d.unrouted.length} unrouted, ${d.through.length} through a card, ${d.onTop} pairs on top`);
  await page.screenshot({ path: join(OUT, `${id}-open.png`) });
  if (p.lines.length) {
    // A click on a line (the focus highlight).
    const at = await page.evaluate(() => { const e = document.querySelector('.react-flow__edge path.react-flow__edge-path'); const q = e.getPointAtLength(e.getTotalLength() / 2); const c = e.getScreenCTM(); return { x: q.x * c.a + c.e, y: q.y * c.d + c.f }; });
    await page.mouse.click(at.x, at.y); await settle(page, 600);
    const click = await longTasks(page);
    check(id, `click on a line: longest freeze under ${BUDGET.click[n]} ms`, click.max < BUDGET.click[n], `longest ${Math.round(click.max)} ms`);
    await page.mouse.click(5, 300); await settle(page, 600); await longTasks(page);
  }
  // Drag the card nearest the middle for 24 steps.
  const hdr = await page.evaluate(() => {
    const fr = document.querySelector('.react-flow').getBoundingClientRect(); const cx = fr.x + fr.width / 2, cy = fr.y + fr.height / 2;
    const vis = [...document.querySelectorAll('.react-flow__node span.font-semibold')].map((s) => s.getBoundingClientRect()).filter((r) => r.x > fr.x + 10 && r.right < fr.right - 200 && r.y > fr.y + 10 && r.bottom < fr.bottom - 150);
    vis.sort((a, b) => Math.hypot(a.x - cx, a.y - cy) - Math.hypot(b.x - cx, b.y - cy)); const r = vis[0]; return r ? { x: r.x + Math.min(20, r.width / 2), y: r.y + r.height / 2 } : null;
  });
  if (hdr) {
    await page.mouse.move(hdr.x, hdr.y); await page.mouse.down();
    for (let i = 1; i <= 24; i++) await page.mouse.move(hdr.x + i * 6, hdr.y + i * 3);
    const during = await longTasks(page);
    await page.mouse.up(); await settle(page, 1000);
    const drop = await longTasks(page);
    check(id, `drag one card: longest freeze under ${BUDGET.drag[n]} ms while it moves`, during.max < BUDGET.drag[n], `longest ${Math.round(during.max)} ms, long tasks ${Math.round(during.sum)} ms over 24 moves; the drop ${Math.round(drop.max)} ms`);
    check(id, `drop: longest freeze under ${BUDGET.open[n]} ms`, drop.max < BUDGET.open[n], `longest ${Math.round(drop.max)} ms`);
    p = await probe(page); d = drawn(p);
    check(id, 'after the drag: every line routed, none through a card, none on top of another', !d.unrouted.length && !d.through.length && !d.onTop, `${d.unrouted.length} unrouted, ${d.through.length} through a card, ${d.onTop} pairs on top`);
    await page.screenshot({ path: join(OUT, `${id}-after-drag.png`) });
  }
  if (n >= 30) {
    await clickText(page, 'Expand all'); await settle(page, 1500);
    const ex = await longTasks(page);
    check(id, `Expand all: longest freeze under ${BUDGET.expand[n]} ms`, ex.max < BUDGET.expand[n], `longest ${Math.round(ex.max)} ms, long tasks ${Math.round(ex.sum)} ms`);
    p = await probe(page); d = drawn(p);
    check(id, 'every card open: every line routed, none through a card, none on top of another', !d.unrouted.length && !d.through.length && !d.onTop, `${d.unrouted.length} unrouted, ${d.through.length} through a card, ${d.onTop} pairs on top`);
    await page.screenshot({ path: join(OUT, `${id}-expanded.png`) });
  }
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}
// ---- the other door: an assistant's tidy grid ----
for (const gap of [40, 20]) {
  const id = `assistant-grid-${gap}px`;
  const graph = model(11);   // 1 fact, 8 lookup tables, 2 unlinked
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; window.__LONG__ = []; }, { graph, columns: columnsFor(graph) });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 120000 });
  await page.waitForSelector('.react-flow__node', { timeout: 120000 });
  await settle(page, 1500);
  const sizes = Object.fromEntries((await probe(page)).cards.map((c) => [c.id, c]));
  const dims = graph.tables.filter((t) => t.name.startsWith('Dim')).map((t) => t.name), facts = graph.tables.filter((t) => t.name.startsWith('Fact')).map((t) => t.name);
  const colW = Math.max(...dims.map((d) => sizes[d].w)), rowH = Math.max(...dims.map((d) => sizes[d].h));
  const tables = dims.map((d, i) => ({ name: d, x: 300 + (i % 4) * (colW + gap), y: Math.floor(i / 4) * (rowH + gap) }))
    .concat(facts.map((f, i) => ({ name: f, x: 0, y: 2 * (rowH + gap) + 40 + i * 300 })))
    .concat(graph.tables.filter((t) => t.name.startsWith('Loose')).map((t, i) => ({ name: t.name, x: 300 + 4 * (colW + gap) + 100, y: i * (rowH + gap) })));
  await page.evaluate((tables) => window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables } }, '*'), tables);
  await settle(page, 1500);
  const p = await probe(page), d = drawn(p);
  // Every card where the assistant put it, or further right or down where the page made room for the lines (round 4,
  // Kane 28 Sep: a grid tighter than the lines need is spaced out), never left or up.
  const placed = tables.every((t) => { const c = p.cards.find((x) => x.id === t.name); return c && c.x > t.x - 1 && c.y > t.y - 1; });
  check(id, 'the saved layout moved the cards (spaced out where it is tighter than the lines need)', placed, '');
  check(id, 'every line routed, none through a card, none on top of another', !d.unrouted.length && !d.through.length && !d.onTop, `${d.unrouted.length} unrouted (${d.unrouted.join(', ')}), ${d.through.length} through a card, ${d.onTop} pairs on top`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
