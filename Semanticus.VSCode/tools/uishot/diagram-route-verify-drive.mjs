// verify: driven proofs from the m11/diagram-route verification (27 Sep), left failing on purpose for the builder.
// Needs the built bundle (npm run build:webview). Serves it, opens the real harness, and measures the live canvas.
//
//   1. Lines drawn on top of each other. The wrapped bus matrix routes lines through shared channels with 4 tracks
//      assigned by edge index, so lines 0, 4, 8... of a channel run along the same line. Checked for 5, 11 and 30
//      tables at 2000x1200, 1440x900 and 900x900, collapsed and with every card open: no two lines may share a
//      straight run over 8 flow px. The single-row base (b0c7095c) has none for the 11-table model.
//   2. The 80-table model freezes the page. Every routed line re-runs A* over every card on every render, and a
//      drag frame re-renders every line (about 2 renders a frame), so one full re-route of 80 lines (~1.1 s) is paid
//      per mouse move. Budgets are several times the base's measured cost, so a busy machine cannot trip them:
//      a click on a line under 300 ms of script (base 2 to 14 ms, head 1.1 to 1.8 s) and a 24-step drag of one card
//      under 12 s of script (base 3.3 to 3.8 s, head 59 to 69 s).
//   3. Pins for the wrap itself (these PASS on head): no gate failed when busMatrixPositions ignored the canvas shape
//      (the lane's route driver stayed 270/270 and the space check stayed 0). The fitted diagram's shape must be
//      within 1.5x of the canvas's for the 11-table model at 900x900 and the 30-table model at 1440x900.
//   4. A card dragged close under another. Drag the card below Date up until the channel between them is 36 flow px
//      (the cards do not overlap; the app's own reflow gap is 48): the router gives up on the lines that leave Date's
//      bottom (a stub of 11 + track px must end outside every card's 10 px margin), they fall back to the old step
//      line and run straight through the dragged card. The ruling was never behind a card. Fails at 44 px and less.
//   node diagram-route-verify-drive.mjs          (OUTDIR=<dir> for the PNGs and results.json)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify');
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
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 900000, args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
// Wait until two animation frames in a row come back quickly: an 80-table render takes seconds on head.
async function settle(page, min = 1500) {
  await wait(min);
  const t0 = Date.now(); let calm = 0;
  while (Date.now() - t0 < 600000 && calm < 2) { const a = Date.now(); await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r()))); calm = Date.now() - a < 200 ? calm + 1 : 0; await wait(300); }
}

// ---- fixtures, injected so the same driver runs on the base too: harness.html's ?g=eleven and ?g=thirty, a 5-table star and an 80-table model ----
const tbl = (name, o = {}) => Object.assign({ ref: 'table:' + name, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [name.replace(/ /g, '') + 'Key'], hasDescription: true }, o);
const rel = (fromTable, toTable) => ({ name: fromTable + '_' + toTable, fromTable, fromColumn: toTable.replace(/ /g, '') + 'Key', toTable, toColumn: toTable.replace(/ /g, '') + 'Key', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
const FIVE = { tables: [tbl('Sales', { measures: 8, columns: 10, keyColumns: [] }), tbl('Date', { isDateTable: true, columns: 12 }), tbl('Product'), tbl('Customer', { columns: 9 }), tbl('Store', { columns: 6 })],
  relationships: [rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Customer'), rel('Sales', 'Store')] };
function eleven() {
  const dims = ['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Geography', 'Channel'];
  return { tables: [tbl('Sales', { measures: 18, columns: 14, keyColumns: [] }), tbl('Inventory', { measures: 6, columns: 9, keyColumns: [] }), tbl('Returns', { measures: 5, columns: 8, keyColumns: [] })]
    .concat(dims.map((n, i) => tbl(n, { isDateTable: n === 'Date', columns: 4 + (i % 5) * 3 }))),
  relationships: [rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Customer'), rel('Sales', 'Store'), rel('Sales', 'Promotion'), rel('Sales', 'Currency'), rel('Sales', 'Channel'),
    rel('Inventory', 'Date'), rel('Inventory', 'Product'), rel('Inventory', 'Store'),
    rel('Returns', 'Date'), rel('Returns', 'Product'), { ...rel('Returns', 'Customer'), isActive: false }, rel('Returns', 'Channel'), rel('Customer', 'Geography')] };
}
function thirty() {
  const facts = ['Sales', 'Inventory', 'Returns', 'Budget'];
  const dims = ['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Geography', 'Channel', 'Warehouse', 'Supplier', 'Employee', 'Region', 'Scenario', 'Account', 'Department', 'Segment', 'Carrier', 'Reason', 'Ship Mode', 'Brand'];
  const loose = ['Notes', 'Measures', 'Parameters', 'Readme', 'Sort Order', 'Colours'];
  const rels = [], seen = new Set();
  const add = (r) => { if (!seen.has(r.name)) { seen.add(r.name); rels.push(r); } };
  facts.forEach((f, fi) => { for (let k = 0; k < 7; k++) add(rel(f, dims[(fi * 4 + k * 3) % 18])); });
  [rel('Customer', 'Geography'), rel('Store', 'Region'), rel('Product', 'Brand'), rel('Warehouse', 'Region')].forEach(add);
  return { tables: facts.map((n, i) => tbl(n, { measures: 6 + i * 3, columns: 8 + i * 2, keyColumns: [] })).concat(dims.map((n, i) => tbl(n, { isDateTable: n === 'Date', columns: 3 + (i % 6) * 2 })))
    .concat(loose.map((n, i) => tbl(n, { isCalculated: i % 2 === 0, columns: 2 + i, keyColumns: [] }))), relationships: rels };
}
function eighty() {
  const facts = Array.from({ length: 8 }, (_, i) => 'Fact ' + String(i + 1).padStart(2, '0'));
  const dims = Array.from({ length: 60 }, (_, i) => 'Dim ' + String(i + 1).padStart(2, '0'));
  const loose = Array.from({ length: 12 }, (_, i) => 'Loose ' + String(i + 1).padStart(2, '0'));
  const rels = [], seen = new Set();
  facts.forEach((f, fi) => { for (let k = 0; k < 9; k++) { const r = rel(f, dims[(fi * 7 + k * 5) % 60]); if (!seen.has(r.name)) { seen.add(r.name); rels.push(r); } } });
  for (let i = 0; i < 15; i += 2) rels.push(rel(dims[i], dims[59 - i]));
  return { tables: facts.map((n, i) => tbl(n, { measures: 4 + i, columns: 8 + (i % 5) * 2, keyColumns: [] })).concat(dims.map((n, i) => tbl(n, { isDateTable: i === 0, columns: 3 + (i % 7) * 2 })))
    .concat(loose.map((n, i) => tbl(n, { isCalculated: i % 3 === 0, columns: 2 + (i % 4), keyColumns: [] }))), relationships: rels };
}
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));

async function open(ctx, { g, graph }, w, h) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  if (graph) await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, { graph, columns: columnsFor(graph) });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${g ? 'g=' + g + '&' : ''}tab=diagram`, { waitUntil: 'networkidle0', timeout: 60000 });
  await page.waitForSelector('.react-flow__node', { timeout: 60000 });
  await settle(page, 2500);
  await applyTheme(page, 'dark');
  return { page, errors };
}
const probe = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  const fr = document.querySelector('.react-flow').getBoundingClientRect();
  const cards = [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect();
    return { x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom };
  });
  const lines = [...document.querySelectorAll('.react-flow__edge')].map((e) => {
    const n = (e.querySelector('path.react-flow__edge-path')?.getAttribute('d')?.match(/-?\d+(\.\d+)?/g) || []).map(Number);
    const pts = []; for (let i = 0; i + 1 < n.length; i += 2) pts.push({ x: n[i], y: n[i + 1] });
    return { id: (e.getAttribute('data-testid') || '').replace(/^rf__edge-/, ''), pts };
  });
  return { zoom, canvas: { w: fr.width, h: fr.height }, cards, lines };
});
function sharedRun(A, B) {
  let run = 0;
  for (let i = 1; i < A.length; i++) for (let j = 1; j < B.length; j++) {
    const a = A[i - 1], b = A[i], c = B[j - 1], d = B[j];
    if (Math.abs(a.x - b.x) < 0.5 && Math.abs(c.x - d.x) < 0.5 && Math.abs(a.x - c.x) < 1.5) run += Math.max(0, Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y)));
    else if (Math.abs(a.y - b.y) < 0.5 && Math.abs(c.y - d.y) < 0.5 && Math.abs(a.y - c.y) < 1.5) run += Math.max(0, Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x)));
  }
  return run;
}
const onTop = (lines) => { const out = []; for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) { const r = sharedRun(lines[i].pts, lines[j].pts); if (r > 8) out.push(`${lines[i].id} + ${lines[j].id} ${Math.round(r)}px`); } return out; };
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const pick = async (page, text) => {
  if (await clickText(page, text)) return true;
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith('Arrange')); if (b) b.click(); });
  await wait(300);
  return clickText(page, text);
};
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };

const SIZES = [['wide', 2000, 1200], ['laptop', 1440, 900], ['narrow', 900, 900]];
// ---- 1 and 3: lines on top of each other, and the wrap pins ----
for (const [name, src] of [['five', { graph: FIVE }], ['eleven', { graph: eleven() }], ['thirty', { graph: thirty() }]]) for (const [size, w, h] of SIZES) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, src, w, h);
  const id = `${name}-${size}`;
  for (const state of ['collapsed', 'expanded']) {
    if (state === 'expanded') { await pick(page, 'Expand all'); await settle(page, 2000); }
    await clickText(page, 'Fit'); await settle(page, 1200);
    const p = await probe(page);
    await page.screenshot({ path: join(OUT, `${id}-${state}.png`) });
    const bad = onTop(p.lines);
    check(id, `${state}: no two lines are drawn on top of each other`, bad.length === 0, bad.length ? `${bad.length} pairs share a straight run over 8 px: ${bad.slice(0, 4).join(', ')}` : '');
    if (state === 'collapsed' && ((name === 'eleven' && size === 'narrow') || (name === 'thirty' && size === 'laptop'))) {
      const bw = Math.max(...p.cards.map((c) => c.x + c.w)) - Math.min(...p.cards.map((c) => c.x));
      const bh = Math.max(...p.cards.map((c) => c.y + c.h)) - Math.min(...p.cards.map((c) => c.y));
      const shape = (bw / bh) / (p.canvas.w / p.canvas.h);
      check(id, 'the wrap: the diagram is within 1.5x of the canvas shape', shape <= 1.5 && shape >= 1 / 1.5, `diagram ${Math.round(bw)} x ${Math.round(bh)} on a ${Math.round(p.canvas.w)} x ${Math.round(p.canvas.h)} canvas, shape ${shape.toFixed(2)}`);
    }
  }
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}
// ---- 4: a card dragged close under another ----
{
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, { graph: eleven() }, 1440, 900);
  const cardsNow = () => page.evaluate(() => {
    const z = Number((/scale\(([^)]+)\)/.exec(document.querySelector('.react-flow__viewport').style.transform) || [0, 1])[1]);
    return { z, cards: [...document.querySelectorAll('.react-flow__node')].map((el) => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect(); const h = el.querySelector('span.font-semibold').getBoundingClientRect(); return { id: el.getAttribute('data-id'), x: +m[1], y: +m[2], w: r.width / z, h: r.height / z, hx: h.x + 10, hy: h.y + h.height / 2 }; }) };
  });
  const { z, cards } = await cardsNow();
  // Date and the card under it in its column; failing that, the first two cards stacked in any column.
  const stackOf = (top) => cards.filter((c) => c !== top && Math.abs(c.x - top.x) < 1 && c.y > top.y).sort((a, b) => a.y - b.y)[0];
  let date = cards.find((c) => c.id === 'Date'), below = date && stackOf(date);
  if (!below) for (const c of [...cards].sort((a, b) => a.y - b.y)) { const b = stackOf(c); if (b) { date = c; below = b; break; } }
  if (!below) { check('eleven-laptop', 'a card dragged close under another: every line still routes round the cards', false, 'no two cards are stacked in one column, so nothing could be dragged under another'); await ctx.close(); }
  else {
  const dy = (date.y + date.h + 36 - below.y) * z;
  await page.mouse.move(below.hx, below.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(below.hx, below.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page);
  const after = (await cardsNow()).cards.find((c) => c.id === below.id);
  const unrouted = await page.evaluate(() => [...document.querySelectorAll('.react-flow__edge')].filter((e) => !/^M[-\d.,\sL]+$/.test((e.querySelector('path.react-flow__edge-path')?.getAttribute('d') || '').trim())).map((e) => (e.getAttribute('data-testid') || '').replace(/^rf__edge-/, '')));
  const through = [];
  for (const l of p.lines) for (let i = 1; i < l.pts.length; i++) { const a = l.pts[i - 1], b = l.pts[i]; for (const c of p.cards) if (Math.min(a.x, b.x) < c.x + c.w - 0.5 && Math.max(a.x, b.x) > c.x + 0.5 && Math.min(a.y, b.y) < c.y + c.h - 0.5 && Math.max(a.y, b.y) > c.y + 0.5) through.push(l.id); }
  const gap = Math.round(after.y - (date.y + date.h));
  check('eleven-laptop', `${below.id} dragged to ${gap} px under ${date.id}: every line still routes round the cards`, unrouted.length === 0 && through.length === 0, `${unrouted.length} lines fell back to the step line (${unrouted.join(', ')}); ${[...new Set(through)].length} run behind a card`);
  await page.screenshot({ path: join(OUT, 'eleven-laptop-tight-drag.png') });
  await ctx.close();
  }
}
// ---- 2: the 80-table model ----
{
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, { graph: eighty() }, 1440, 900);
  const cdp = await page.createCDPSession();
  await cdp.send('Performance.enable');
  const scriptMs = async () => { const { metrics } = await cdp.send('Performance.getMetrics'); return metrics.find((m) => m.name === 'ScriptDuration').value * 1000; };
  const at = await page.evaluate(() => { const e = document.querySelector('.react-flow__edge path.react-flow__edge-path'); const p = e.getPointAtLength(e.getTotalLength() / 2); const c = e.getScreenCTM(); return { x: p.x * c.a + c.e, y: p.y * c.d + c.f }; });
  let s0 = await scriptMs(); await page.mouse.click(at.x, at.y); await settle(page, 1000); const click = (await scriptMs()) - s0;
  check('eighty-laptop', 'a click on a line costs under 300 ms of script', click < 300, `${Math.round(click)} ms`);
  await page.mouse.click(5, 300); await settle(page, 1000);
  const hdr = await page.evaluate(() => {
    const fr = document.querySelector('.react-flow').getBoundingClientRect(); const cx = fr.x + fr.width / 2, cy = fr.y + fr.height / 2;
    const vis = [...document.querySelectorAll('.react-flow__node span.font-semibold')].map((s) => s.getBoundingClientRect()).filter((r) => r.x > fr.x + 10 && r.right < fr.right - 300 && r.y > fr.y + 10 && r.bottom < fr.bottom - 200);
    vis.sort((a, b) => Math.hypot(a.x - cx, a.y - cy) - Math.hypot(b.x - cx, b.y - cy)); const r = vis[0]; return { x: r.x + Math.min(20, r.width / 2), y: r.y + r.height / 2 };
  });
  s0 = await scriptMs();
  await page.mouse.move(hdr.x, hdr.y); await page.mouse.down();
  for (let i = 1; i <= 24; i++) await page.mouse.move(hdr.x + i * 8, hdr.y + i * 4);
  await page.mouse.up(); await settle(page, 1000);
  const drag = (await scriptMs()) - s0;
  check('eighty-laptop', 'a 24-step drag of one card costs under 12 s of script', drag < 12000, `${Math.round(drag)} ms`);
  await page.screenshot({ path: join(OUT, 'eighty-laptop-after-drag.png') });
  await ctx.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
