// verify: driven proofs from round 3 of the m11/diagram-route verification (28 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness, measures the live canvas.
// Kane's two rulings are the bar: no line ever runs behind a card, and no two lines are drawn on top of each other.
//
//   1. A lookup table dropped onto another in the Studio's own bus matrix can be left on top of it: at 80 tables in a
//      900x900 window (Dark and Light) and with every card open at 1440x900 (Snap off and on). clearDropped's four ways
//      out all land on a neighbour, the card steps between two neighbours for its 50 tries and is saved covering the
//      card, whose lines then run behind it (test/diagram-route-verify-r3.test.mjs: 60 and 27 of 240 such drops on
//      those two layouts). The drive runs the shipped clearDropped on the live cards to find such a drop, zooms in on
//      it and makes it for real; when no drop is left on top, it drops a card onto the first lookup table with
//      neighbours on all four sides, so the check still covers the middle of the matrix. With Snap on, a card that
//      does slide lands off the 20 px grid. With every card open that fallback is zoomed in on too (at Fit no header
//      clears the top band), and the save is checked by the spread rule (dropNow's `closed`), not "where it is drawn".
//   2. The same drop inside the tidy grid the person's assistant saves, 80 px apart (the star grid, 30 tables).
//   3. The other door: the person's assistant saves a card on top of another (save_layout reaches the page as
//      layoutDidChange). The page draws it as saved and the covered card's lines run behind the card on top; a card the
//      person drops there slides clear, so the two doors give different results.
//   4. The 80-table model in the assistant's plain grid 20 px apart, the case the round 3 report left short (reported,
//      not checked): pairs of lines 0 to 1 px apart for 30 to 275 px, at all three window sizes. The Diagram draws a
//      line 1.5 px wide, so two lines within 1.5 px touch at every zoom and read as one line.
// "On top of each other" is this drive's round 1 measure: two lines within 1.5 flow px, the same way, over 8 px.
//   node diagram-route-verify-r3-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const { clearDropped } = await import(pathToFileURL(join(webRoot, 'webview', 'src', 'diagramfit.mjs')).href);
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r3');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4').split(','));
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

// ---- fixtures (generic names), the same generators as the round 2 drive ----
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

async function open(ctx, graph, w, h, theme) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => {
    window.__UISHOT__ = cfg;
    // Record what the Diagram saves to the engine, positions included (the harness default keeps names only).
    window.__saves = [];
    cfg.saveLayout = (params) => { const t = (params && params[0]) || []; window.__saves.push(JSON.parse(JSON.stringify(t))); return { saved: true, count: t.length }; };
  }, { graph, columns: columnsFor(graph) });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 180000 });
  await page.waitForSelector('.react-flow__node', { timeout: 180000 });
  await settle(page, 2500);
  await applyTheme(page, theme);
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
// Pairs of different lines within 1.5 flow px, the same way, for over 8 px, with the closest distance of each pair.
function onTop(lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const A = lines[i].pts, B = lines[j].pts;
    let run = 0, dmin = Infinity;
    for (let s = 1; s < A.length; s++) for (let t = 1; t < B.length; t++) {
      const a = A[s - 1], b = A[s], c = B[t - 1], d = B[t];
      let ov = 0, dd = Infinity;
      if (Math.abs(a.x - b.x) < 0.5 && Math.abs(c.x - d.x) < 0.5 && (dd = Math.abs(a.x - c.x)) < 1.5) ov = Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y));
      else if (Math.abs(a.y - b.y) < 0.5 && Math.abs(c.y - d.y) < 0.5 && (dd = Math.abs(a.y - c.y)) < 1.5) ov = Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x));
      if (ov > 0) { run += ov; dmin = Math.min(dmin, dd); }
    }
    if (run > 8) out.push({ pair: `${lines[i].id} + ${lines[j].id}`, run: Math.round(run), d: Math.round(dmin * 100) / 100 });
  }
  return out.sort((x, y) => y.run - x.run);
}
const covering = (p, id) => { const a = p.cards.find((c) => c.id === id); return p.cards.filter((c) => c.id !== id && a.x < c.x + c.w - 0.5 && c.x < a.x + a.w - 0.5 && a.y < c.y + c.h - 0.5 && c.y < a.y + a.h - 0.5).map((c) => c.id); };
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const post = (page, tables) => page.evaluate((tables) => window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables } }, '*'), tables);
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };
const info = (id, step, detail) => { results.push({ id, step, info: true, detail }); console.log(`INFO ${id} ${step}: ${detail}`); };
const isDim = (graph) => (n) => graph.relationships.some((r) => r.toTable === n);
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
const plainGrid = (cards, graph, gap, aspect) => {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = cards.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...cards.map((c) => c.w)), ch = Math.max(...cards.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap) }));
};

// A lookup table in the middle of the cards (another within 150 px on each side) with its header on screen, and one
// from elsewhere to drop onto it, at one of the node test's offsets. Prefers a drop the shipped clearDropped cannot clear
// (anywhere: every card, the node test's mover rule; else only cards whose header is on screen).
const OFFSETS = [[30, 20], [0, 0], [-40, 30], [10, 60]];
function pickDrop(p, graph, snap, anywhere = false) {
  const dim = isDim(graph);
  const on = (c) => anywhere || (c.hx > p.canvas.x + 30 && c.hx < p.canvas.r - 30 && c.hy > p.canvas.y + 60 && c.hy < p.canvas.b - 30);
  const side = (c, f) => p.cards.some((d) => d !== c && f(d));
  const middle = p.cards.filter((c) => dim(c.id) && on(c)
    && side(c, (d) => d.x + d.w <= c.x && c.x - (d.x + d.w) < 150 && d.y < c.y + c.h && d.y + d.h > c.y)
    && side(c, (d) => d.x >= c.x + c.w && d.x - (c.x + c.w) < 150 && d.y < c.y + c.h && d.y + d.h > c.y)
    && side(c, (d) => d.y + d.h <= c.y && c.y - (d.y + d.h) < 150 && d.x < c.x + c.w && d.x + d.w > c.x)
    && side(c, (d) => d.y >= c.y + c.h && d.y - (c.y + c.h) < 150 && d.x < c.x + c.w && d.x + d.w > c.x));
  const lookups = p.cards.filter((c) => dim(c.id));
  const moverFor = (X) => (anywhere ? lookups.find((c) => c !== X && Math.hypot(c.x - X.x, c.y - X.y) > 900)
    : p.cards.find((c) => c !== X && dim(c.id) && on(c) && (Math.abs(c.x - X.x) > 450 || Math.abs(c.y - X.y) > 300)));
  const land = (X, off) => { const at = { x: X.x + off[0], y: X.y + off[1] }; return snap ? { x: Math.round(at.x / 20) * 20, y: Math.round(at.y / 20) * 20 } : at; };
  for (const X of anywhere ? lookups : middle) for (const off of OFFSETS) {
    const D = moverFor(X); if (!D) continue;
    const at = land(X, off);
    const cards = p.cards.map((c) => (c === D ? { id: c.id, ...at, w: c.w, h: c.h } : { id: c.id, x: c.x, y: c.y, w: c.w, h: c.h }));
    const out = clearDropped(cards, [D.id]); const me = { ...cards.find((c) => c.id === D.id), ...(out[D.id] ?? {}) };
    if (cards.some((c) => c.id !== D.id && me.x < c.x + c.w && c.x < me.x + me.w && me.y < c.y + c.h && c.y < me.y + me.h)) return { X, D, off, stuck: true };
  }
  const X = middle.find((c) => moverFor(c));
  return X ? { X, D: moverFor(X), off: OFFSETS[0], stuck: false } : null;
}
// Zoom the real canvas (mouse wheel at the point) until the flow point sits at `zoom`.
async function zoomAt(page, pt, zoom) {
  for (let n = 0; n < 60; n++) {
    const v = await page.evaluate(() => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)\s*scale\(([^)]+)\)/.exec(document.querySelector('.react-flow__viewport').style.transform); const r = document.querySelector('.react-flow').getBoundingClientRect(); return { tx: Number(m[1]) + r.x, ty: Number(m[2]) + r.y, z: Number(m[3]) }; });
    if (Math.abs(v.z - zoom) < 0.03 * zoom) return;
    await page.mouse.move(v.tx + pt.x * v.z, v.ty + pt.y * v.z);
    await page.mouse.wheel({ deltaY: v.z < zoom ? -60 : 60 }); await wait(90);
  }
}
// Pan the real canvas by (dx, dy) screen px: drag from a spot of bare canvas, one whose drag stays inside the canvas.
async function panBy(page, dx, dy) {
  if (Math.abs(dx) < 2 && Math.abs(dy) < 2) return false;
  const from = await page.evaluate((dx, dy) => {
    const fr = document.querySelector('.react-flow').getBoundingClientRect();
    for (let y = fr.y + 70; y < fr.bottom - 10; y += 13) for (let x = fr.x + 10; x < fr.right - 10; x += 13) {
      if (x + dx < fr.x + 10 || x + dx > fr.right - 10 || y + dy < fr.y + 70 || y + dy > fr.bottom - 10) continue;
      const el = document.elementFromPoint(x, y);
      if (el && el.classList.contains('react-flow__pane')) return { x, y };
    }
    return null;
  }, dx, dy);
  if (!from) return false;
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(from.x + dx * i / 20, from.y + dy * i / 20);
  await page.mouse.up();
  return true;
}
async function dropCheck(id, page, graph, snap, seek = false, closed = null) {
  // Zoom in at the point between the target and the card to drop, so both headers stay on screen, and drop there.
  const zoomAndDrop = async (all) => {
    await zoomAt(page, { x: (all.X.x + all.D.x) / 2 + 40, y: (all.X.y + all.D.y) / 2 + 12 }, 0.35); await settle(page, 800);
    let p1 = await probe(page);
    let X = p1.cards.find((c) => c.id === all.X.id), D = p1.cards.find((c) => c.id === all.D.id);
    // The wheel keeps the zoom point where it was on screen, which can leave a header past the canvas's edge (a card in
    // the matrix's top row, zoomed from near the top band): pan the two headers' midpoint to the canvas's middle first.
    if (X && D && await panBy(page, (p1.canvas.x + p1.canvas.r) / 2 - (X.hx + D.hx) / 2, (p1.canvas.y + p1.canvas.b) / 2 - (X.hy + D.hy) / 2)) {
      await settle(page, 800);
      p1 = await probe(page);
      X = p1.cards.find((c) => c.id === all.X.id); D = p1.cards.find((c) => c.id === all.D.id);
    }
    const onScreen = (c) => c.hx > p1.canvas.x + 30 && c.hx < p1.canvas.r - 30 && c.hy > p1.canvas.y + 60 && c.hy < p1.canvas.b - 30;
    if (!(X && D && onScreen(X) && onScreen(D))) return false;
    await dropNow(id, page, p1, { X, D, off: all.off, stuck: all.stuck }, snap, closed);
    return true;
  };
  let all = null;
  if (seek) {
    // A drop the shipped clearDropped leaves on top, anywhere in the matrix: Fit, zoom in on it, look again.
    await clickText(page, 'Fit'); await settle(page, 800);
    all = pickDrop(await probe(page), graph, snap, true);
    if (all?.stuck && await zoomAndDrop(all)) return;
  }
  const p0 = await probe(page);
  const pick = pickDrop(p0, graph, snap);
  if (pick) return dropNow(id, page, p0, pick, snap, closed);
  // No drop is left on top and no target's header is on screen at Fit. With every card open (the spread, Kane 29 Sep)
  // Fit frames the whole matrix so small that every target's header sits in the top band `on` leaves out, so rounds 4
  // to 11 reported "no lookup table on screen with cards on all four sides" and never made the drop this section
  // promises. The fallback target (a lookup table with neighbours on all four sides) is zoomed in on the same way.
  if (all && !all.stuck && await zoomAndDrop(all)) return;
  check(id, 'a lookup table dropped onto another', false, 'no lookup table on screen with cards on all four sides');
}
// `closed`: the probe taken before Expand all, or null when no card is open. With cards open the matrix is drawn SPREAD
// (Kane, 29 Sep, "Slide down"), and a drop is saved at the card's saved spot plus the distance dragged, moved to the
// nearest clear spot of the saved layout when a card is there (lane rounds 9 and 10, the rule diagram-route-r4-drive.mjs
// checks), not where the spread draws it. Without it the save must be exactly where the card is drawn, as before.
async function dropNow(id, page, p0, pick, snap, closed = null) {
  const { X, D } = pick, OFFSET = { x: pick.off[0], y: pick.off[1] };
  const dx = (X.x + OFFSET.x - D.x) * p0.zoom, dy = (X.y + OFFSET.y - D.y) * p0.zoom;
  await page.evaluate(() => { window.__saves.length = 0; });
  await page.mouse.move(D.hx, D.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(D.hx + dx * i / 20, D.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page);
  const me = p.cards.find((c) => c.id === D.id);
  const cov = covering(p, D.id), thr = throughOf(p);
  const saved = (await page.evaluate(() => window.__saves)).flat();
  const where = `${D.id} dropped on ${X.id} + (${OFFSET.x}, ${OFFSET.y}) is drawn at (${Math.round(me.x)}, ${Math.round(me.y)})${pick.stuck ? ' (a target the shipped clearDropped cannot clear)' : ''}`;
  check(id, `${where}: it covers no card and no line runs behind a card`, !cov.length && !thr.length, `covers [${cov.join(', ')}]; ${thr.length} behind a card: ${thr.slice(0, 3).join('; ')}`);
  if (!closed) check(id, 'only the dropped card is saved, where it is drawn', saved.length > 0 && saved.every((s) => s.name === D.id && Math.abs(s.x - me.x) < 0.5 && Math.abs(s.y - me.y) < 0.5), JSON.stringify(saved).slice(0, 200));
  else {
    const c0 = closed.cards.find((c) => c.id === D.id), ddx = X.x + OFFSET.x - D.x, ddy = X.y + OFFSET.y - D.y;
    const near = (n) => Math.abs(n.x - (c0.x + ddx)) < 8 + 0.08 * Math.abs(ddx) && Math.abs(n.y - (c0.y + ddy)) < 8 + 0.08 * Math.abs(ddy);
    const clearAt = (n) => !closed.cards.some((c) => c.id !== D.id && Math.min(n.x + c0.w, c.x + c.w) - Math.max(n.x, c.x) > 1 && Math.min(n.y + c0.h, c.y + c.h) - Math.max(n.y, c.y) > 1);
    const onGrid = (n) => !snap || (Math.abs(n.x % 20) < 0.01 && Math.abs(n.y % 20) < 0.01);
    check(id, 'only the dropped card is saved, at its saved spot plus the distance it was dragged, or the nearest clear spot of the saved layout when a card is there (a spread is drawn, so not where it is drawn)',
      saved.length > 0 && saved.every((s) => s.name === D.id && (near(s) || clearAt(s)) && onGrid(s)), `${JSON.stringify(saved).slice(0, 160)}; saved spot before (${Math.round(c0.x)}, ${Math.round(c0.y)}) + drag (${Math.round(ddx)}, ${Math.round(ddy)})`);
  }
  if (snap) check(id, 'Snap on: the dropped card lands on the 20 px grid', me.x % 20 === 0 && me.y % 20 === 0, `(${me.x}, ${me.y})`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
}

// ---- 1: a lookup table dropped onto another in the Studio's own bus matrix ----
if (SECTIONS.has('1')) for (const [w, h, theme, allOpen, snap] of [[900, 900, 'dark', false, false], [900, 900, 'light', false, false], [1440, 900, 'dark', true, false], [1440, 900, 'dark', true, true]]) {
  const graph = big(8, 60, 12, 9), id = `eighty-busmatrix-${w}x${h}-${theme}${allOpen ? '-every-card-open' : ''}-snap-${snap ? 'on' : 'off'}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, w, h, theme);
  const closed = allOpen ? await probe(page) : null;
  if (allOpen) { await clickText(page, 'Expand all'); await settle(page, 2500); }
  if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
  await dropCheck(id, page, graph, snap, true, closed);
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: the same drop inside the assistant's star grid, 80 px apart ----
if (SECTIONS.has('2')) for (const theme of ['dark', 'light']) for (const snap of [false, true]) {
  const graph = thirty(), id = `thirty-star80-${theme}-snap-${snap ? 'on' : 'off'}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await post(page, starGrid((await probe(page)).cards, graph, 80, 1440 / 900)); await settle(page, 1500);
  if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
  await dropCheck(id, page, graph, snap);
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 3: the person's assistant saves a card on top of another ----
if (SECTIONS.has('3')) for (const theme of ['dark', 'light']) {
  const graph = thirty(), id = `thirty-assistant-stack-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const p0 = await probe(page);
  const X = p0.cards.find((c) => c.id === 'Supplier'), D = p0.cards.find((c) => c.id === 'Date');
  for (const [label, at] of [['30 and 20 px off', { x: X.x + 30, y: X.y + 20 }], ['half over', { x: X.x, y: X.y + X.h / 2 }]]) {
    await post(page, [{ name: D.id, x: at.x, y: at.y }]); await settle(page, 1500);
    const p = await probe(page); const thr = throughOf(p);
    check(id, `the assistant saves ${D.id} ${label} ${X.id}: no line runs behind a card (as when the person drops it there)`, !thr.length, `drawn at (${Math.round(p.cards.find((c) => c.id === D.id).x)}, ${Math.round(p.cards.find((c) => c.id === D.id).y)}), covering [${covering(p, D.id).join(', ')}]; ${thr.length} behind a card: ${thr.slice(0, 3).join('; ')}`);
    await page.screenshot({ path: join(OUT, `${id}-${label.replace(/ /g, '-')}.png`) });
  }
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 4: 80 tables in the assistant's plain grid 20 px apart ----
if (SECTIONS.has('4')) for (const [size, w, h, theme] of [['wide', 2000, 1200, 'dark'], ['laptop', 1440, 900, 'dark'], ['narrow', 900, 900, 'light']]) {
  const graph = big(8, 60, 12, 9), id = `eighty-plain20-${size}-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, w, h, theme);
  await post(page, plainGrid((await probe(page)).cards, graph, 20, w / h)); await settle(page, 2000);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  const pairs = onTop(p.lines), thr = throughOf(p), missing = graph.relationships.length - p.lines.filter((l) => l.pts.length > 1).length;
  check(id, "the assistant's plain grid (20 px): every line drawn, none behind a card, none on top of another", !pairs.length && !thr.length && !missing,
    `${missing} missing; ${thr.length} behind a card; ${pairs.length} pairs within 1.5 px: ${pairs.slice(0, 4).map((q) => `${q.pair} ${q.run} px, ${q.d} px apart`).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
