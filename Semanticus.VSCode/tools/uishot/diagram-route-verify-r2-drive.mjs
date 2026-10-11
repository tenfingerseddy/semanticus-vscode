// verify: driven proofs from round 2 of the m11/diagram-route verification (28 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness, measures the live canvas.
//
//   1. A card dragged close under another sends lines behind it. In the 30-table bus matrix (1440x900), drag a lookup
//      table up until about 10 px separate it from the one above it (another card sits further down the column): the
//      lines that end on the upper card's bottom run straight through the dragged card. Also 80 tables. A bottom
//      handle's stub is excused from the card check inside any grown box that holds the handle, which is meant for its
//      own card; see test/diagram-route-verify-r2.test.mjs case 1. At about 17 px the lines go round.
//   2. Lines drawn on top of each other in a tidy grid the person's assistant saves (save_layout reaches the page as
//      layoutDidChange): the 30-table model as a plain grid 40 px apart and as a star grid 20 px apart, and the
//      80-table model as a star grid 40 px apart. Real VS Code over the MCP door drew 3 and 6 pairs for the first two.
//      Kane's decision on round 3 (28 Sep, test/diagram-route-r4.test.mjs header) changed what the grid's arrival looks
//      like: a saved layout with no room for its lines is moved apart just enough, the moved spots are saved, and one
//      Put back returns them. So "the grid moved the cards" is now checked in two steps instead of one exact compare
//      that the ruling makes fail by design (rounds 4 to 11 carried it as a known miss): every card is on the assistant's
//      spot or on a spot the page saved for it and offers to put back, and after Put back every card is on the
//      assistant's exact spot again and stays there. The line checks run on the page's made-room layout, as drawn.
//   3. The Studio's own bus matrix with every card open, 150 tables at 1440x900: two lines share 169 px.
//   4. Lines vanish for seconds. SemRelEdge draws nothing for a line whose ends moved until the settled routing pass
//      lands, and a burst of re-flow on a big model runs several passes of half a second or more: after one drag, then
//      Expand all, at 150 tables every line was gone for 2.2 to 2.5 s here (a fresh Expand all: 0.3 s; real VS Code:
//      2.6 s). The lane report says a line is left out for at most 250 ms. Budget 500 ms.
// "On top of each other" is this drive's round 1 measure: two lines within 1.5 flow px, the same way, over 8 px.
//   node diagram-route-verify-r2-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r2');
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
async function settle(page, min = 1500) {
  await wait(min);
  const t0 = Date.now(); let calm = 0;
  while (Date.now() - t0 < 600000 && calm < 2) { const a = Date.now(); await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => r()))); calm = Date.now() - a < 200 ? calm + 1 : 0; await wait(300); }
}

// ---- fixtures (generic names) ----
const tbl = (name, o = {}) => Object.assign({ ref: 'table:' + name, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [name.replace(/ /g, '') + 'Key'], hasDescription: true }, o);
const rel = (fromTable, toTable) => ({ name: fromTable + '_' + toTable, fromTable, fromColumn: toTable.replace(/ /g, '') + 'Key', toTable, toColumn: toTable.replace(/ /g, '') + 'Key', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
const dedupe = (rels) => { const seen = new Set(); return rels.filter((r) => (r.fromTable !== r.toTable && !seen.has(r.name) ? (seen.add(r.name), true) : false)); };
function thirty() {
  const facts = ['Sales', 'Inventory', 'Returns', 'Budget'];
  const dims = ['Date', 'Product', 'Customer', 'Store', 'Promotion', 'Currency', 'Geography', 'Channel', 'Warehouse', 'Supplier', 'Employee', 'Region', 'Scenario', 'Account', 'Department', 'Segment', 'Carrier', 'Reason', 'Ship Mode', 'Brand'];
  const loose = ['Notes', 'Measures', 'Parameters', 'Readme', 'Sort Order', 'Colours'];
  const rels = [];
  facts.forEach((f, fi) => { for (let k = 0; k < 7; k++) rels.push(rel(f, dims[(fi * 4 + k * 3) % 18])); });
  rels.push(rel('Customer', 'Geography'), rel('Store', 'Region'), rel('Product', 'Brand'), rel('Warehouse', 'Region'));
  return { tables: facts.map((n, i) => tbl(n, { measures: 6 + i * 3, columns: 8 + i * 2, keyColumns: [] })).concat(dims.map((n, i) => tbl(n, { isDateTable: n === 'Date', columns: 3 + (i % 6) * 2 })))
    .concat(loose.map((n, i) => tbl(n, { isCalculated: i % 2 === 0, columns: 2 + i, keyColumns: [] }))), relationships: dedupe(rels) };
}
// nFacts facts joining perFact lookup tables each (the first `hubs` shared by every fact), a few snowflakes, and
// nIso unlinked tables.
function big(nFacts, nDims, nIso, perFact, hubs = 0) {
  const pad = (i) => String(i + 1).padStart(3, '0');
  const facts = Array.from({ length: nFacts }, (_, i) => 'Fact ' + pad(i)), dims = Array.from({ length: nDims }, (_, i) => 'Dim ' + pad(i)), iso = Array.from({ length: nIso }, (_, i) => 'Loose ' + pad(i));
  const rels = [];
  facts.forEach((f, fi) => { for (let h = 0; h < hubs; h++) rels.push(rel(f, dims[h])); for (let k = 0; k < perFact - hubs; k++) rels.push(rel(f, dims[hubs + ((fi * 7 + k * 5) % (nDims - hubs))])); });
  for (let i = hubs; i + 1 < Math.min(nDims, 16 + hubs); i += 2) rels.push(rel(dims[i], dims[nDims - 1 - i + hubs]));
  return { tables: facts.map((n, i) => tbl(n, { measures: 4 + (i % 6), columns: 8 + (i % 5) * 2, keyColumns: [] })).concat(dims.map((n, i) => tbl(n, { isDateTable: i === 0, columns: 3 + (i % 7) * 2 })))
    .concat(iso.map((n, i) => tbl(n, { isCalculated: i % 3 === 0, columns: 2 + (i % 4), keyColumns: [] }))), relationships: dedupe(rels) };
}
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));

async function open(ctx, graph, w, h) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => {
    window.__UISHOT__ = cfg;
    // Record what the Diagram saves to the engine, positions and keep marks included (the harness default keeps names only).
    window.__saves = [];
    cfg.saveLayout = (params) => { const t = (params && params[0]) || []; window.__saves.push(JSON.parse(JSON.stringify(t))); return { saved: true, count: t.length }; };
  }, { graph, columns: columnsFor(graph) });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 180000 });
  await page.waitForSelector('.react-flow__node', { timeout: 180000 });
  await settle(page, 2500);
  await applyTheme(page, 'dark');
  return { page, errors };
}
const probe = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  const fr = document.querySelector('.react-flow').getBoundingClientRect();
  const cards = [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect(); const hd = el.querySelector('span.font-semibold').getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom, hx: hd.x + 10, hy: hd.y + hd.height / 2 };
  });
  const lines = [...document.querySelectorAll('.react-flow__edge')].map((e) => {
    const n = (e.querySelector('path.react-flow__edge-path')?.getAttribute('d')?.match(/-?\d+(\.\d+)?/g) || []).map(Number);
    const pts = []; for (let i = 0; i + 1 < n.length; i += 2) pts.push({ x: n[i], y: n[i + 1] });
    return { id: (e.getAttribute('data-testid') || '').replace(/^rf__edge-/, ''), pts };
  });
  return { zoom, canvas: { x: fr.x, y: fr.y, r: fr.right, b: fr.bottom }, cards, lines };
});
const throughOf = (p) => { const out = new Set(); for (const l of p.lines) for (let i = 1; i < l.pts.length; i++) { const a = l.pts[i - 1], b = l.pts[i]; for (const c of p.cards) if (Math.min(a.x, b.x) < c.x + c.w - 0.5 && Math.max(a.x, b.x) > c.x + 0.5 && Math.min(a.y, b.y) < c.y + c.h - 0.5 && Math.max(a.y, b.y) > c.y + 0.5) out.add(`${l.id} through ${c.id}`); } return [...out]; };
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
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };

// ---- 1: a card dragged close under another ----
for (const [name, graph] of [['thirty', thirty()], ['eighty', big(8, 60, 12, 9)]]) {
  const id = `${name}-laptop`;
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, graph, 1440, 900);
  const p0 = await probe(page);
  const isDim = (t) => graph.relationships.some((r) => r.toTable === t);
  const col = (c) => p0.cards.filter((d) => isDim(d.id) && Math.abs(d.x - c.x) < 1).sort((a, b) => a.y - b.y);
  let pick = null;
  for (const c of p0.cards) {
    if (!isDim(c.id)) continue;
    const k = col(c), i = k.indexOf(c);
    if (i > 0 && i < k.length - 1 && c.hx > p0.canvas.x && c.hx < p0.canvas.r && c.hy > p0.canvas.y && c.hy < p0.canvas.b) { pick = { above: k[i - 1], mid: c }; break; }
  }
  if (!pick) { check(id, 'a card dragged about 10 px under another: no line runs behind a card', false, 'no lookup table on screen with one above and one below it'); await ctx.close(); continue; }
  const dy = (pick.above.y + pick.above.h + 5 - pick.mid.y) * p0.zoom;   // lands about 10 px under (the drag adds a few)
  await page.mouse.move(pick.mid.hx, pick.mid.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(pick.mid.hx, pick.mid.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page);
  const after = p.cards.find((c) => c.id === pick.mid.id);
  const gap = Math.round((after.y - (pick.above.y + pick.above.h)) * 10) / 10;
  const bad = throughOf(p);
  check(id, `${pick.mid.id} dragged to ${gap} px under ${pick.above.id}: no line runs behind a card`, bad.length === 0, bad.slice(0, 4).join(', '));
  await page.screenshot({ path: join(OUT, `${id}-close-drag.png`) });
  await ctx.close();
}

// ---- 2: an assistant's tidy grid ----
const plainGrid = (cards, graph, gap, aspect) => {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = cards.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...cards.map((c) => c.w)), ch = Math.max(...cards.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }));
};
const starGrid = (cards, graph, gap, aspect) => {
  const r = graph.relationships, by = new Map(cards.map((c) => [c.id, c]));
  const facts = cards.filter((c) => r.some((x) => x.fromTable === c.id) && !r.some((x) => x.toTable === c.id)).map((c) => c.id).sort();
  const dims = cards.filter((c) => r.some((x) => x.toTable === c.id)).map((c) => c.id).sort();
  const loose = cards.filter((c) => !r.some((x) => x.toTable === c.id || x.fromTable === c.id)).map((c) => c.id).sort();
  const fw = Math.max(190, ...facts.map((f) => by.get(f).w)), cw = Math.max(190, ...dims.map((d) => by.get(d).w)), ch = Math.max(92, ...dims.map((d) => by.get(d).h));
  const cols = Math.max(1, Math.round(Math.sqrt(dims.length * aspect * ch / cw)));
  const out = []; let y = 0;
  for (const f of facts) { out.push({ name: f, x: 0, y }); y += by.get(f).h + gap; }
  const x0 = fw + gap + 60;
  dims.forEach((d, i) => out.push({ name: d, x: x0 + (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }));
  const lx = x0 + cols * (cw + gap) + 60;
  loose.forEach((t, i) => out.push({ name: t, x: lx + Math.floor(i / 12) * (cw + gap), y: (i % 12) * (ch + gap) }));
  return out;
};
for (const [name, graph, kind, gap] of [['thirty', thirty(), 'plain', 40], ['thirty', thirty(), 'star', 20], ['eighty', big(8, 60, 12, 9), 'star', 40]]) {
  const id = `${name}-${kind}${gap}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900);
  const p0 = await probe(page);
  const tables = (kind === 'plain' ? plainGrid : starGrid)(p0.cards, graph, gap, 1440 / 900);
  await page.evaluate(() => { window.__saves.length = 0; });
  await page.evaluate((tables) => window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables } }, '*'), tables);
  await settle(page, 2000);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  const at = (q, name) => q.cards.find((x) => x.id === name);
  const same = (a, b) => !!a && !!b && Math.abs(a.x - b.x) < 1 && Math.abs(a.y - b.y) < 1;
  // Step 1: each card is where the assistant put it, or where the page's make-room moved it, saved there (last save wins).
  const saved = new Map(); for (const s of (await page.evaluate(() => window.__saves)).flat()) saved.set(s.name, s);
  const moved = tables.filter((t) => !same(at(p, t.name), t));
  const stray = moved.filter((t) => !at(p, t.name) || !same(saved.get(t.name), at(p, t.name))).map((t) => t.name);
  const offer = await page.evaluate(() => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Put back' && b.getClientRects().length));
  check(id, `the assistant's ${kind} grid (${gap} px) moved the cards: each on its spot, or moved by the page to make room, saved there and offered to put back`,
    !stray.length && (!moved.length || offer), `${moved.length} of ${tables.length} moved to make room; not on a saved spot: [${stray.slice(0, 4).join(', ')}]; Put back ${offer ? 'offered' : 'not offered'}`);
  const bad = onTop(p.lines), thr = throughOf(p);
  check(id, `the assistant's ${kind} grid (${gap} px): no line behind a card, no two lines drawn on top of each other`, !bad.length && !thr.length, `${thr.length} behind a card; ${bad.length} pairs on top: ${bad.slice(0, 4).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  // Step 2: Put back returns every card to the assistant's exact spot, and they stay there (kept).
  if (moved.length) {
    await clickText(page, 'Put back'); await settle(page, 2000);
    const back = await probe(page);
    const off = tables.filter((t) => !same(at(back, t.name), t)).map((t) => t.name);
    check(id, `the assistant's ${kind} grid (${gap} px): Put back leaves every card on the assistant's exact spot`, !off.length, `${off.length} off: [${off.slice(0, 4).join(', ')}]`);
    await page.screenshot({ path: join(OUT, `${id}-put-back.png`) });
  }
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 3 and 4: 150 tables, every card open; how long the lines are gone after Expand all ----
{
  const id = 'tables150-laptop';
  const graph = big(15, 113, 22, 9, 1);
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900);
  const n = graph.relationships.length;
  // One ordinary drag first: after it, Expand all re-flows the cards in a longer burst than on a fresh diagram.
  const hdr = await page.evaluate(() => { const fr = document.querySelector('.react-flow').getBoundingClientRect(); const s = [...document.querySelectorAll('.react-flow__node span.font-semibold')].map((x) => x.getBoundingClientRect()).find((r) => r.x > fr.x + 300 && r.y > fr.y + 100 && r.right < fr.right - 200 && r.bottom < fr.bottom - 200); return s ? { x: s.x + 10, y: s.y + s.height / 2 } : null; });
  if (hdr) { await page.mouse.move(hdr.x, hdr.y); await page.mouse.down(); for (let i = 1; i <= 24; i++) await page.mouse.move(hdr.x + i * 6, hdr.y + i * 3); await page.mouse.up(); await settle(page, 1500); }
  await page.evaluate(() => {
    window.__rec = { on: true, counts: [] };
    const tick = (t) => { if (!window.__rec.on) return; window.__rec.counts.push([t, document.querySelectorAll('.react-flow__edge path.react-flow__edge-path').length]); requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
  });
  await clickText(page, 'Expand all'); await settle(page, 3000);
  // From the first frame that shows fewer lines to the first frame that shows them all again: a page that blocks
  // right after dropping the lines paints one empty frame and keeps it on screen for the whole block.
  const gone = await page.evaluate((n) => { window.__rec.on = false; let first = null, back = null; for (const [t, k] of window.__rec.counts) { if (k < n) { if (first === null) first = t; back = null; } else if (first !== null && back === null) back = t; } return first === null ? 0 : Math.round((back ?? performance.now()) - first); }, n);
  check(id, 'Expand all after a drag: the lines are never gone for more than 500 ms', gone <= 500, `lines missing for ${gone} ms`);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  const bad = onTop(p.lines);
  check(id, 'every card open: no two lines drawn on top of each other', bad.length === 0, `${bad.length} pairs: ${bad.slice(0, 4).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}-expanded.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
