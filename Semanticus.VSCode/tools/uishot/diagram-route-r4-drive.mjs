// Review round 4 of m11/diagram-route (28 Sep): the Diagram makes room for its lines (Kane: "Yes, make room"). When a
// saved layout leaves no room for the lines, the page moves cards apart just enough, saves the moved spots, and one
// Undo puts them back. Needs the built bundle (npm run build:webview). Serves it, opens the real harness, measures the
// live canvas the way the round 3 verification did. Kane's two rulings are the bar: no line ever runs behind a card,
// and no two lines are drawn on top of each other; round 3 added lines drawn along another card's border.
//
//   1. The assistant's tidy grids (plain 0, 10, 20 and 40 px apart, star 20 and 40 px) at 30, 80 (and the 80-table
//      model with three hub tables), 150 and 300 tables, at 2000x1200, 1440x900 and 900x900, Dark and Light: no card
//      on another, no line behind a card, no two lines closer than NEAR (3) px along each other for over RUN (4) px, no
//      line within 0.5 px of the border of a card it does not join for over 8 px, every line drawn, and every card the
//      page moved saved where it is drawn.
//   2. The assistant saves a card on top of another, half over it, and 4, 8 and 13 px under one.
//   3. The person drops a card onto another where cards sit close on every side (the Studio's own 80-table bus matrix
//      at 900x900, and with every card open at 1440x900) and inside the assistant's star grid 80 px apart, Snap off and
//      on; and drags a card 8 px under another. Only the dropped card moves and is saved, where it is drawn, on the
//      20 px grid with Snap on.
//   4. Undo: the toast's Undo and the row's Put back each put back exactly the cards the page moved and save them
//      kept; Ctrl+Z stays the model's undo and leaves the layout alone; a graph reload and a fresh page leave the
//      put-back cards there; a drag of a kept card clears its mark; Undo after a drop puts the card where it was dropped.
//   node diagram-route-r4-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4; QUICK=1 for a
//                                         smaller section 1: 30 and 80 tables at one window size each; MODELS=t150,t300
//                                         to run section 1 for those models only)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r4');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4').split(','));
const QUICK = !!process.env.QUICK;
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
  // Making room and the routing pass run a few ms at a time (review round 5), so frames stay quick while they work:
  // wait until the page is not making room and no line is still held (drawn faded) on its old route too.
  while (Date.now() - t0 < 600000 && await page.evaluate(() => !!document.querySelector('[data-making-room], .react-flow__edge g[style*="opacity: 0.4"]'))) await wait(300);
}

// ---- fixtures (generic names), the round 2 and 3 drives' generators ----
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
function big(nFacts, nDims, nIso, perFact, hubs = 0) {
  const pad = (i) => String(i + 1).padStart(3, '0');
  const facts = Array.from({ length: nFacts }, (_, i) => 'Fact ' + pad(i)), dims = Array.from({ length: nDims }, (_, i) => 'Dim ' + pad(i)), iso = Array.from({ length: nIso }, (_, i) => 'Loose ' + pad(i));
  const rels = [];
  facts.forEach((f, fi) => { for (let h = 0; h < hubs; h++) rels.push(rel(f, dims[h])); for (let k = 0; k < perFact - hubs; k++) rels.push(rel(f, dims[hubs + ((fi * 7 + k * 5) % (nDims - hubs))])); });
  for (let i = hubs; i + 1 < Math.min(nDims, 16 + hubs); i += 2) rels.push(rel(dims[i], dims[nDims - 1 - i + hubs]));
  return { tables: facts.map((n, i) => tbl(n, { measures: 4 + (i % 6), columns: 8 + (i % 5) * 2, keyColumns: [] })).concat(dims.map((n, i) => tbl(n, { isDateTable: i === 0, columns: 3 + (i % 7) * 2 })))
    .concat(iso.map((n, i) => tbl(n, { isCalculated: i % 3 === 0, columns: 2 + (i % 4), keyColumns: [] }))), relationships: dedupe(rels) };
}
const MODELS = { thirty, eighty: () => big(8, 60, 12, 9), hub80: () => big(8, 60, 12, 9, 3), t150: () => big(15, 113, 22, 9), t300: () => big(30, 226, 44, 9) };
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));

// A page on the harness with a small stand-in for the engine's layout sidecar: saveLayout merges into it the way
// LocalEngine.SaveLayoutAsync does (keep is sent, not merged) and records every call; getLayout reads it back.
async function open(ctx, graph, w, h, theme, { store = [], state = null } = {}) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg, store) => {
    window.__UISHOT__ = cfg;
    window.__saves = [];
    window.__store = store;
    cfg.getLayout = () => ({ tables: JSON.parse(JSON.stringify(window.__store)) });
    cfg.saveLayout = (params) => {
      const t = (params && params[0]) || [];
      window.__saves.push(JSON.parse(JSON.stringify(t)));
      for (const n of t) {
        const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) };
        const i = window.__store.findIndex((s) => s.name === n.name);
        if (i >= 0) window.__store[i] = e; else window.__store.push(e);
      }
      return { saved: true, count: t.length };
    };
  }, { graph, columns: columnsFor(graph), ...(state ? { state } : {}) }, store);
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
  const toast = [...document.querySelectorAll('span')].map((s) => s.textContent || '').find((t) => /lines have room/.test(t)) ?? null;
  const putBack = [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Put back' && b.getClientRects().length);
  return { zoom, canvas: { x: fr.x, y: fr.y, r: fr.right, b: fr.bottom }, cards, lines, toast, putBack };
});
const throughOf = (p) => { const out = new Set(); for (const l of p.lines) for (let i = 1; i < l.pts.length; i++) { const a = l.pts[i - 1], b = l.pts[i]; for (const c of p.cards) if (Math.min(a.x, b.x) < c.x + c.w - 0.5 && Math.max(a.x, b.x) > c.x + 0.5 && Math.min(a.y, b.y) < c.y + c.h - 0.5 && Math.max(a.y, b.y) > c.y + 0.5) out.add(`${l.id} through ${c.id}`); } return [...out]; };
// Pairs of different lines closer than 3 px, the same way, along each other for over 4 px (the router's NEAR and RUN).
function closePairs(lines) {
  const out = [];
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {
    const A = lines[i].pts, B = lines[j].pts;
    let run = 0, dmin = Infinity;
    for (let s = 1; s < A.length; s++) for (let t = 1; t < B.length; t++) {
      const a = A[s - 1], b = A[s], c = B[t - 1], d = B[t];
      let ov = 0, dd = Infinity;
      if (Math.abs(a.x - b.x) < 0.01 && Math.abs(c.x - d.x) < 0.01 && (dd = Math.abs(a.x - c.x)) < 3 - 1e-6) ov = Math.min(Math.max(a.y, b.y), Math.max(c.y, d.y)) - Math.max(Math.min(a.y, b.y), Math.min(c.y, d.y));
      else if (Math.abs(a.y - b.y) < 0.01 && Math.abs(c.y - d.y) < 0.01 && (dd = Math.abs(a.y - c.y)) < 3 - 1e-6) ov = Math.min(Math.max(a.x, b.x), Math.max(c.x, d.x)) - Math.max(Math.min(a.x, b.x), Math.min(c.x, d.x));
      if (ov > 0) { run += ov; dmin = Math.min(dmin, dd); }
    }
    if (run > 4) out.push({ pair: `${lines[i].id} + ${lines[j].id}`, run: Math.round(run), d: Math.round(dmin * 100) / 100 });
  }
  return out.sort((x, y) => y.run - x.run);
}
// Runs within `within` px of the border of a card the line does not join, along it for over 8 px.
function onBorders(p, within) {
  const out = [];
  for (const l of p.lines) {
    const [from, to] = l.id.split('_');
    for (let i = 1; i < l.pts.length; i++) {
      const a = l.pts[i - 1], b = l.pts[i], v = Math.abs(a.x - b.x) < 0.01;
      if (!v && Math.abs(a.y - b.y) >= 0.01) continue;
      for (const c of p.cards) {
        if (c.id === from || c.id === to) continue;
        const at = v ? a.x : a.y, e0 = v ? c.x : c.y, e1 = e0 + (v ? c.w : c.h);
        const ov = Math.min(Math.max(v ? a.y : a.x, v ? b.y : b.x), v ? c.y + c.h : c.x + c.w) - Math.max(Math.min(v ? a.y : a.x, v ? b.y : b.x), v ? c.y : c.x);
        if ((Math.abs(at - e0) < within || Math.abs(at - e1) < within) && ov > 8) out.push(`${l.id} along ${c.id} for ${Math.round(ov)} px`);
      }
    }
  }
  return out;
}
const coveringPairs = (p) => { const out = []; for (let i = 0; i < p.cards.length; i++) for (let j = i + 1; j < p.cards.length; j++) { const a = p.cards[i], c = p.cards[j]; if (a.x < c.x + c.w - 0.5 && c.x < a.x + a.w - 0.5 && a.y < c.y + c.h - 0.5 && c.y < a.y + a.h - 0.5) out.push(`${a.id} on ${c.id}`); } return out; };
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
// The assistant's save_layout: the engine stores it (keep only where sent) and broadcasts it to the page.
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
}, tables);
const saves = async (page) => (await page.evaluate(() => window.__saves)).flat();
const clearSaves = (page) => page.evaluate(() => { window.__saves.length = 0; });
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
// Every line drawn, none behind a card, no card on another, no two lines closer than 3 px, none on a card's border.
function lineChecks(id, label, p, graph) {
  const missing = graph.relationships.length - p.lines.filter((l) => l.pts.length > 1).length;
  const thr = throughOf(p), cov = coveringPairs(p), pairs = closePairs(p.lines), hug = onBorders(p, 0.5), hug2 = onBorders(p, 2);
  check(id, `${label}: every line drawn, none behind a card, no card on another`, !missing && !thr.length && !cov.length, `${missing} missing; ${thr.length} behind a card: ${thr.slice(0, 3).join('; ')}; ${cov.length} cards on another: ${cov.slice(0, 3).join('; ')}`);
  check(id, `${label}: no two lines closer than 3 px along each other for over 4 px`, !pairs.length, `${pairs.length} pairs: ${pairs.slice(0, 4).map((q) => `${q.pair} ${q.run} px, ${q.d} px apart`).join(', ')}`);
  check(id, `${label}: no line along a card's border (within 0.5 px for over 8 px)`, !hug.length, `${hug.length}: ${hug.slice(0, 3).join('; ')}`);
  info(id, `${label}: lines within 2 px of a card's border for over 8 px`, `${hug2.length}${hug2.length ? ': ' + hug2.slice(0, 3).join('; ') : ''}`);
}
// Every card whose drawn spot differs from where it was put was saved there (the assistant reads it on get_layout).
async function savedWhereDrawn(id, page, p, put) {
  const s = await saves(page), last = new Map(s.map((n) => [n.name, n]));
  const moved = p.cards.filter((c) => put[c.id] && (Math.abs(put[c.id].x - c.x) > 0.5 || Math.abs(put[c.id].y - c.y) > 0.5));
  const wrong = moved.filter((c) => { const n = last.get(c.id); return !n || Math.abs(n.x - c.x) > 0.5 || Math.abs(n.y - c.y) > 0.5; });
  check(id, `every card the page moved is saved where it is drawn (${moved.length} moved)`, !wrong.length, wrong.slice(0, 3).map((c) => `${c.id} drawn (${Math.round(c.x)}, ${Math.round(c.y)}), saved ${JSON.stringify(last.get(c.id) ?? null)}`).join('; '));
  return moved;
}

// ---- 1: the assistant's tidy grids ----
const WINDOWS = { wide: [2000, 1200, 'dark'], laptop: [1440, 900, 'light'], narrow: [900, 900, 'dark'] };
const GRIDS = [['plain', 0], ['plain', 10], ['plain', 20], ['plain', 40], ['star', 20], ['star', 40]];
const PLAN1 = QUICK ? [['thirty', ['laptop']], ['eighty', ['wide']]]
  : [['thirty', ['wide', 'laptop', 'narrow']], ['eighty', ['wide', 'laptop', 'narrow']], ['hub80', ['laptop']], ['t150', ['laptop', 'narrow']], ['t300', ['wide', 'laptop']]];
const ONLY = process.env.MODELS ? new Set(process.env.MODELS.split(',')) : null;   // e.g. MODELS=t150,t300 for section 1
if (SECTIONS.has('1')) for (const [model, wins] of PLAN1) for (const win of wins) {
  if (ONLY && !ONLY.has(model)) continue;
  const [w, h, theme] = WINDOWS[win];
  const graph = MODELS[model]();
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, w, h, theme);
  const cards0 = (await probe(page)).cards;
  for (const [kind, gap] of GRIDS) {
    if (model === 't300' && gap < 20) continue;   // the 20 px case already makes room across the whole 300-table grid
    const id = `${model}-${kind}${gap}-${win}-${theme}`;
    const grid = (kind === 'plain' ? plainGrid : starGrid)(cards0, graph, gap, w / h);
    await clearSaves(page);
    await post(page, grid); await settle(page, 2500);
    const p = await probe(page);
    const put = Object.fromEntries(grid.map((g) => [g.name, g]));
    lineChecks(id, `the assistant's ${kind} grid ${gap} px apart`, p, graph);
    if (process.env.DUMP) writeFileSync(join(OUT, `${id}.raw.json`), JSON.stringify({ cards: p.cards, lines: p.lines.map((l) => ({ id: l.id, routed: true, pts: l.pts })) }));
    const moved = await savedWhereDrawn(id, page, p, put);
    info(id, 'the page made room', `${moved.length} of ${p.cards.length} cards moved; toast: ${p.toast ?? 'none'}`);
    if (gap === 20 || model === 'eighty') await page.screenshot({ path: join(OUT, `${id}.png`) });
  }
  if (errors.length) check(`${model}-${win}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: the assistant saves a card on top of, over or just under another ----
if (SECTIONS.has('2')) for (const theme of ['dark', 'light']) {
  const graph = thirty(), id = `thirty-assistant-close-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const p0 = await probe(page);
  const S = p0.cards.find((c) => c.id === 'Supplier'), D = p0.cards.find((c) => c.id === 'Date'), C = p0.cards.find((c) => c.id === 'Customer');
  const cases = [['Date', '30 and 20 px off Supplier', { x: S.x + 30, y: S.y + 20 }], ['Date', 'half over Supplier', { x: S.x, y: S.y + S.h / 2 }],
    ['Customer', '4 px under Date', { x: D.x, y: D.y + D.h + 4 }], ['Customer', '8 px under Date', { x: D.x, y: D.y + D.h + 8 }], ['Customer', '13 px under Date', { x: D.x, y: D.y + D.h + 13 }]];
  for (const [who, label, at] of cases) {
    // Back to the Studio's own spots first, so each case starts from the same layout.
    await post(page, p0.cards.map((c) => ({ name: c.id, x: c.x, y: c.y }))); await settle(page, 1200);
    await clearSaves(page);
    await post(page, [{ name: who, x: at.x, y: at.y }]); await settle(page, 1800);
    const p = await probe(page);
    lineChecks(id, `the assistant saves ${who} ${label}`, p, graph);
    await savedWhereDrawn(id, page, p, { [who]: at });
    const me = p.cards.find((c) => c.id === who);
    info(id, `${who} ${label}`, `saved at (${Math.round(at.x)}, ${Math.round(at.y)}), drawn at (${Math.round(me.x)}, ${Math.round(me.y)})`);
    await page.screenshot({ path: join(OUT, `${id}-${label.replace(/ /g, '-')}.png`) });
  }
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  void C;
  await ctx.close();
}

// ---- 3: the person drops a card among close neighbours ----
// Zoom the real canvas (mouse wheel at a flow point) until it shows that point at `zoom`.
async function zoomAt(page, pt, zoom) {
  for (let n = 0; n < 60; n++) {
    const v = await page.evaluate(() => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)\s*scale\(([^)]+)\)/.exec(document.querySelector('.react-flow__viewport').style.transform); const r = document.querySelector('.react-flow').getBoundingClientRect(); return { tx: Number(m[1]) + r.x, ty: Number(m[2]) + r.y, z: Number(m[3]) }; });
    if (Math.abs(v.z - zoom) < 0.03 * zoom) return;
    await page.mouse.move(v.tx + pt.x * v.z, v.ty + pt.y * v.z);
    await page.mouse.wheel({ deltaY: v.z < zoom ? -60 : 60 }); await wait(90);
  }
}
// A lookup table with another lookup table close on each side, and a lookup table a few cards away to drop onto it.
function middleTarget(p, graph) {
  const dim = isDim(graph);
  const side = (c, f) => p.cards.some((d) => d !== c && f(d));
  const mids = p.cards.filter((c) => dim(c.id)
    && side(c, (d) => d.x + d.w <= c.x && c.x - (d.x + d.w) < 150 && d.y < c.y + c.h && d.y + d.h > c.y)
    && side(c, (d) => d.x >= c.x + c.w && d.x - (c.x + c.w) < 150 && d.y < c.y + c.h && d.y + d.h > c.y)
    && side(c, (d) => d.y + d.h <= c.y && c.y - (d.y + d.h) < 150 && d.x < c.x + c.w && d.x + d.w > c.x)
    && side(c, (d) => d.y >= c.y + c.h && d.y - (c.y + c.h) < 150 && d.x < c.x + c.w && d.x + d.w > c.x));
  for (const X of mids) {
    const D = p.cards.find((c) => c !== X && dim(c.id) && Math.abs(c.y - X.y) < 30 && Math.abs(c.x - X.x) > 450 && Math.abs(c.x - X.x) < 900);
    if (D) return { X, D };
  }
  return null;
}
async function drag(page, from, dx, dy, steps = 20) {
  await page.mouse.move(from.hx, from.hy); await page.mouse.down();
  for (let i = 1; i <= steps; i++) await page.mouse.move(from.hx + dx * i / steps, from.hy + dy * i / steps);
  await page.mouse.up();
}
// `closed`: the probe taken before Expand all (round 9: with cards open the cards are drawn SPREAD, so a drop is saved at the card's
// saved spot plus the distance dragged, which is not where the spread draws it), or null when nothing is open.
async function dropOnto(id, page, graph, snap, off = [30, 20], closed = null) {
  await clickText(page, 'Fit'); await settle(page, 800);
  let p = await probe(page);
  const pick = middleTarget(p, graph);
  if (!pick) { check(id, 'a lookup table with close neighbours on every side to drop onto', false, 'none found'); return null; }
  await zoomAt(page, { x: (pick.X.x + pick.D.x) / 2 + 95, y: pick.X.y + 46 }, 0.5); await settle(page, 800);
  p = await probe(page);
  const X = p.cards.find((c) => c.id === pick.X.id), D = p.cards.find((c) => c.id === pick.D.id);
  await clearSaves(page);
  await drag(page, D, (X.x + off[0] - D.x) * p.zoom, (X.y + off[1] - D.y) * p.zoom);
  await settle(page, 1500);
  const q = await probe(page);
  const me = q.cards.find((c) => c.id === D.id);
  const s = await saves(page);
  lineChecks(id, `${D.id} dropped on ${X.id} + (${off[0]}, ${off[1]}), drawn at (${Math.round(me.x)}, ${Math.round(me.y)})`, q, graph);
  if (!closed) check(id, 'only the dropped card is saved, where it is drawn', s.length > 0 && s.every((n) => n.name === D.id && Math.abs(n.x - me.x) < 0.5 && Math.abs(n.y - me.y) < 0.5), JSON.stringify(s).slice(0, 200));
  else {
    const c0 = closed.cards.find((c) => c.id === D.id), dx = X.x + off[0] - D.x, dy = X.y + off[1] - D.y;
    const near = (n) => Math.abs(n.x - (c0.x + dx)) < 8 + 0.08 * Math.abs(dx) && Math.abs(n.y - (c0.y + dy)) < 8 + 0.08 * Math.abs(dy);
    // Round 10 (L2): that spot, moved to the nearest clear spot of the SAVED layout (the closed boxes) when a card is on it there, and
    // on the 20 px grid with Snap on.
    const clearAt = (n) => !closed.cards.some((c) => c.id !== D.id && Math.min(n.x + c0.w, c.x + c.w) - Math.max(n.x, c.x) > 1 && Math.min(n.y + c0.h, c.y + c.h) - Math.max(n.y, c.y) > 1);
    const onGrid = (n) => !snap || (Math.abs(n.x % 20) < 0.01 && Math.abs(n.y % 20) < 0.01);
    check(id, 'only the dropped card is saved, at its saved spot plus the distance it was dragged, or the nearest clear spot of the saved layout when a card is there (a spread is drawn, so not where it is drawn)', s.length > 0 && s.every((n) => n.name === D.id && (near(n) || clearAt(n)) && onGrid(n)), `${JSON.stringify(s).slice(0, 160)}; saved spot before (${Math.round(c0.x)}, ${Math.round(c0.y)}) + drag (${Math.round(dx)}, ${Math.round(dy)})`);
  }
  const others = q.cards.filter((c) => { const b = p.cards.find((o) => o.id === c.id); return c.id !== D.id && (Math.abs(b.x - c.x) > 0.5 || Math.abs(b.y - c.y) > 0.5); });
  check(id, 'no other card moved', !others.length, others.slice(0, 3).map((c) => c.id).join(', '));
  if (snap) check(id, 'Snap on: the dropped card lands on the 20 px grid', Math.abs(me.x % 20) < 0.01 && Math.abs(me.y % 20) < 0.01, `(${me.x}, ${me.y})`);
  info(id, 'how far the card went from the drop', `${Math.round(Math.hypot(me.x - X.x - off[0], me.y - X.y - off[1]))} px; toast: ${q.toast ?? 'none'}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  return { X, D, me, q };
}
if (SECTIONS.has('3')) {
  for (const [w, h, theme, allOpen, snap] of [[900, 900, 'dark', false, false], [900, 900, 'light', false, true], [1440, 900, 'dark', true, false], [1440, 900, 'light', true, true]]) {
    const graph = MODELS.eighty(), id = `eighty-busmatrix-${w}x${h}-${theme}${allOpen ? '-every-card-open' : ''}-snap-${snap ? 'on' : 'off'}`;
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, graph, w, h, theme);
    let closed = null;
    if (allOpen) { await clickText(page, 'Fit'); await settle(page, 800); closed = await probe(page); await clickText(page, 'Expand all'); await settle(page, 2500); }
    if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
    await dropOnto(id, page, graph, snap, [30, 20], closed);
    if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
    await ctx.close();
  }
  for (const [theme, snap] of [['dark', false], ['light', true]]) {
    const graph = thirty(), id = `thirty-star80-${theme}-snap-${snap ? 'on' : 'off'}`;
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, graph, 1440, 900, theme);
    await post(page, starGrid((await probe(page)).cards, graph, 80, 1440 / 900)); await settle(page, 1500);
    if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
    await dropOnto(id, page, graph, snap);
    if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
    await ctx.close();
  }
  // A card dragged 8 px under another, Snap off and on: it ends where its lines have room, and only it is saved.
  for (const [theme, snap] of [['dark', false], ['light', true]]) {
    const graph = thirty(), id = `thirty-close-drag-${theme}-snap-${snap ? 'on' : 'off'}`;
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, graph, 1440, 900, theme);
    if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
    await clickText(page, 'Fit'); await settle(page, 800);
    const p = await probe(page);
    const D = p.cards.find((c) => c.id === 'Date'), C = p.cards.find((c) => c.id === 'Customer');
    await clearSaves(page);
    await drag(page, C, (D.x - C.x) * p.zoom, (D.y + D.h + 8 - C.y) * p.zoom);
    await settle(page, 1500);
    const q = await probe(page);
    const me = q.cards.find((c) => c.id === 'Customer'), s = await saves(page);
    lineChecks(id, `Customer dragged 8 px under Date, drawn at (${Math.round(me.x)}, ${Math.round(me.y)})`, q, graph);
    check(id, 'only the dragged card is saved, where it is drawn', s.length > 0 && s.every((n) => n.name === 'Customer' && Math.abs(n.x - me.x) < 0.5 && Math.abs(n.y - me.y) < 0.5), JSON.stringify(s).slice(0, 200));
    if (snap) check(id, 'Snap on: it lands on the 20 px grid', Math.abs(me.x % 20) < 0.01 && Math.abs(me.y % 20) < 0.01, `(${me.x}, ${me.y})`);
    await page.screenshot({ path: join(OUT, `${id}.png`) });
    if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
    await ctx.close();
  }
}

// ---- 4: one Undo puts back what the page moved, and it stays put ----
const ALL_BUSMATRIX = { diagrams: [{ id: 'all', name: 'All tables', all: true, tables: [], positions: {}, layout: 'busmatrix' }], activeDiagramId: 'all' };
if (SECTIONS.has('4')) for (const [how, theme] of [['toast', 'dark'], ['row', 'light']]) {
  const graph = thirty(), id = `thirty-undo-${how}-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const grid = plainGrid((await probe(page)).cards, graph, 20, 1440 / 900);
  const put = Object.fromEntries(grid.map((g) => [g.name, g]));
  // The toast closes 3.5 s after it opens, so look soon after the room is made (the row's Put back stays).
  await post(page, grid); await wait(1200);
  const p1 = await probe(page);
  const moved = p1.cards.filter((c) => Math.abs(put[c.id].x - c.x) > 0.5 || Math.abs(put[c.id].y - c.y) > 0.5).map((c) => c.id);
  check(id, 'the page made room and says so, with an Undo', moved.length > 0 && /^Moved \d+ tables? so the lines have room\.$/.test(p1.toast ?? '') && p1.putBack, `${moved.length} moved; toast: ${p1.toast}; Put back shown: ${p1.putBack}`);
  await page.screenshot({ path: join(OUT, `${id}-made-room.png`) });
  if (how === 'row') {
    // Ctrl+Z is the model's undo (the timeline both doors share), so it leaves the layout alone.
    await clearSaves(page);
    await page.mouse.click(p1.canvas.r - 60, p1.canvas.b - 60); await page.keyboard.down('Control'); await page.keyboard.press('z'); await page.keyboard.up('Control');
    await settle(page, 1200);
    const pz = await probe(page);
    const shifted = pz.cards.filter((c) => { const b = p1.cards.find((o) => o.id === c.id); return Math.abs(b.x - c.x) > 0.5 || Math.abs(b.y - c.y) > 0.5; });
    check(id, 'Ctrl+Z stays the model undo: no card moves and nothing is saved', !shifted.length && !(await saves(page)).length && pz.putBack, `${shifted.length} moved; Put back still shown: ${pz.putBack}`);
    await wait(3000);   // the toast has closed: the row's Put back is what is left
  }
  await clearSaves(page);
  if (how === 'toast') await clickText(page, 'Undo');
  else await clickText(page, 'Put back');
  await settle(page, 1500);
  const p2 = await probe(page);
  const notBack = moved.filter((n) => { const c = p2.cards.find((q) => q.id === n); return Math.abs(c.x - put[n].x) > 0.5 || Math.abs(c.y - put[n].y) > 0.5; });
  const s = await saves(page);
  check(id, `one Undo (${how}) puts every moved card back where the assistant saved it`, !notBack.length, notBack.slice(0, 4).join(', '));
  // A Put back stays put (review round 7): every card the page moved is saved, kept, where the assistant saved it, and so
  // is each never-moved card the page would still have moved past a kept one, kept where it stands (unchanged spot).
  const beside = s.filter((n) => !moved.includes(n.name)).map((n) => n.name);
  check(id, 'the put-back is saved, kept, for the cards the page moved (and the cards beside them it keeps where they stand)', moved.every((n) => s.some((e) => e.name === n)) && s.every((n) => n.keep === true && Math.abs(n.x - put[n.name].x) < 0.5 && Math.abs(n.y - put[n.name].y) < 0.5), JSON.stringify(s).slice(0, 240));
  info(id, 'cards kept beside the put-back ones', beside.join(', ') || 'none');
  check(id, 'after the Undo there is nothing left to put back', !p2.putBack, `Put back shown: ${p2.putBack}`);
  // A graph reload (another door changed the model) runs the settle pass again: the kept cards stay.
  await clearSaves(page);
  await page.evaluate(() => window.postMessage({ type: 'didChange', payload: { deltas: [{ kind: 'structure' }] } }, '*'));
  await settle(page, 2500);
  const p3 = await probe(page);
  const movedAgain = p3.cards.filter((c) => Math.abs(c.x - put[c.id].x) > 0.5 || Math.abs(c.y - put[c.id].y) > 0.5).map((c) => c.id);
  check(id, 'a graph reload leaves the put-back cards where they are', !movedAgain.length && !(await saves(page)).length, movedAgain.slice(0, 4).join(', '));
  await page.screenshot({ path: join(OUT, `${id}-put-back.png`) });
  const store = await page.evaluate(() => window.__store);
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
  if (how !== 'toast') continue;
  // A fresh page on the same saved layout (a tab switch, a reload): the kept cards stay; a drag clears one mark.
  const ctx2 = await browser.createBrowserContext();
  const { page: pg, errors: e2 } = await open(ctx2, graph, 1440, 900, theme, { store, state: ALL_BUSMATRIX });
  const p4 = await probe(pg);
  // A Put back stays put (review round 7): EVERY card is drawn where it was put back or left, never-moved cards that touch a
  // kept card included, and nothing is saved, so no toast and no second Put back either.
  const off = p4.cards.filter((c) => Math.abs(c.x - put[c.id].x) > 0.5 || Math.abs(c.y - put[c.id].y) > 0.5).map((c) => c.id);
  check(id, 'a fresh page on the saved layout draws the kept cards where they were put back', !off.length && !(await saves(pg)).length && !p4.putBack, `${off.slice(0, 4).join(', ')}; Put back shown: ${p4.putBack}`);
  const k = p4.cards.find((c) => c.id === moved[0]);
  await clearSaves(pg);
  await drag(pg, k, 0, 400 * p4.zoom);
  await settle(pg, 1500);
  const s2 = await saves(pg);
  check(id, `a drag of the kept ${k.id} saves it without keep (its mark clears)`, s2.some((n) => n.name === k.id) && s2.every((n) => !n.keep), JSON.stringify(s2).slice(0, 200));
  if (e2.length) check(id, 'no page errors (fresh page)', false, e2.join(' | '));
  await ctx2.close();
}
// Undo after a drop that slid clear: the card goes back where the person dropped it.
if (SECTIONS.has('4')) {
  const graph = thirty(), id = 'thirty-undo-drop-dark';
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, 'dark');
  await clickText(page, 'Fit'); await settle(page, 800);
  const p = await probe(page);
  const X = p.cards.find((c) => c.id === 'Supplier'), D = p.cards.find((c) => c.id === 'Promotion');
  await drag(page, D, (X.x + 30 - D.x) * p.zoom, (X.y + 20 - D.y) * p.zoom);
  await settle(page, 1500);
  const q = await probe(page), slid = q.cards.find((c) => c.id === D.id);
  check(id, `${D.id} dropped on ${X.id} slides clear and says so`, !coveringPairs(q).length && /so the lines have room/.test(q.toast ?? ''), `drawn at (${Math.round(slid.x)}, ${Math.round(slid.y)}); toast: ${q.toast}`);
  await clearSaves(page);
  await clickText(page, 'Undo'); await settle(page, 1200);
  const r = await probe(page), back = r.cards.find((c) => c.id === D.id), s = await saves(page);
  // Where it was dropped: on Supplier again (a drag lands a few px from the pointer's path, so not exactly X + (30, 20)).
  const onX = back.x < X.x + X.w && X.x < back.x + back.w && back.y < X.y + X.h && X.y < back.y + back.h;
  check(id, 'Undo puts it back on the card it was dropped on, saved kept there (and any card beside it kept where it stands)', onX && s.some((n) => n.name === D.id) && s.every((n) => { const c = r.cards.find((o) => o.id === n.name); return n.keep === true && Math.abs(n.x - c.x) < 0.5 && Math.abs(n.y - c.y) < 0.5; }), `drawn at (${Math.round(back.x)}, ${Math.round(back.y)}), Supplier at (${Math.round(X.x)}, ${Math.round(X.y)}); saved ${JSON.stringify(s)}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
