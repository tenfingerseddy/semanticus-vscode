// Review round 6 of m11/diagram-route (29 Sep): a kept card is a wall in the live page, and the canvas keeps painting while
// the page makes room at 300 tables. Needs the built bundle (npm run build:webview). Serves it, opens the real harness
// and measures the live canvas the way the round 5 verification did.
//
//   1. The assistant saves a grid with keep on one card (the cases the round 5 verifier found, at 30, 80 and 150
//      tables, Snap off and on): the page makes room, the kept card stays where it was saved, no card is left on top of
//      it, and no line runs behind any card.
//   2. 300 tables, the assistant's star grid 20 px apart: while the page makes room the lines held on their old routes
//      are drawn faded with stroke-opacity, never with a group opacity (an opacity group on each of 278 lines made the
//      software compositor take up to 1.9 s to draw one frame), and no two frames are more than 500 ms apart.
//   node diagram-route-r6-drive.mjs      (OUTDIR=<dir> for results.json; SECTIONS=1,2; SHOTS=1 for a PNG of each kept-card case;
//                                         WEBROOT=<checkout> to run against another checkout's built bundle)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(process.env.WEBROOT || join(here, '..', '..'));   // WEBROOT=<another checkout> serves its bundle instead
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r6');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2').split(','));
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

// ---- 1: a kept card is a wall ----
const WINDOWS = { wide: [2000, 1200, 'dark'], laptop: [1440, 900, 'light'], narrow: [900, 900, 'dark'] };
const CASES = [
  ['eighty', 'star', 40, 'wide', false, 'Dim 048'], ['eighty', 'star', 40, 'wide', false, 'Dim 041'], ['eighty', 'plain', 0, 'laptop', false, 'Fact 001'],
  ['eighty', 'plain', 10, 'narrow', true, 'Dim 041'], ['eighty', 'star', 40, 'laptop', true, 'Loose 001'], ['eighty', 'star', 20, 'wide', true, 'Dim 055'],
  ['thirty', 'plain', 0, 'laptop', false, 'Inventory'], ['thirty', 'plain', 0, 'laptop', false, 'Promotion'],
  ['t150', 'star', 20, 'wide', false, 'Dim 065'], ['t150', 'plain', 0, 'narrow', false, 'Dim 001'], ['t150', 'plain', 0, 'laptop', false, 'Fact 001'],
];
if (SECTIONS.has('1')) for (const [model, kind, gap, win, snap, kept] of CASES) {
  const [w, h, theme] = WINDOWS[win];
  const id = `keep ${model} ${kind} ${gap} px ${w}x${h}${snap ? ' Snap' : ''} keeping ${kept}`;
  const graph = MODELS[model]();
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, w, h, theme);
  if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
  const cards0 = (await probe(page)).cards;
  const grid = (kind === 'star' ? starGrid : plainGrid)(cards0, graph, gap, w / h).map((t) => ({ ...t, x: Math.round(t.x), y: Math.round(t.y), ...(t.name === kept ? { keep: true } : {}) }));
  const put = Object.fromEntries(grid.map((t) => [t.name, t]));
  await clearSaves(page);
  await post(page, grid);
  await settle(page, 1500);
  const p = await probe(page);
  const k = p.cards.find((c) => c.id === kept);
  const on = p.cards.filter((c) => c !== k && Math.min(c.x + c.w, k.x + k.w) - Math.max(c.x, k.x) > 0.5 && Math.min(c.y + c.h, k.y + k.h) - Math.max(c.y, k.y) > 0.5).map((c) => c.id);
  check(id, 'the kept card is drawn where it was saved', Math.abs(k.x - put[kept].x) < 0.5 && Math.abs(k.y - put[kept].y) < 0.5, `drawn (${Math.round(k.x)}, ${Math.round(k.y)}), saved (${put[kept].x}, ${put[kept].y})`);
  check(id, 'no card is left on top of the kept card', !on.length, on.join(', '));
  const thr = throughOf(p);
  check(id, 'no line runs behind any card', !thr.length, `${thr.length}: ${thr.slice(0, 3).join('; ')}`);
  check(id, 'no card on another card', !coveringPairs(p).length, coveringPairs(p).slice(0, 3).join('; '));
  check(id, 'no page errors', !errors.length, errors.slice(0, 2).join('; '));
  await savedWhereDrawn(id, page, p, put);
  if (process.env.SHOTS) await page.screenshot({ path: join(OUT, id.replace(/[^a-z0-9]+/gi, '-') + '.png') });
  await ctx.close();
}

// ---- 2: the canvas keeps painting while the page makes room at 300 tables ----
if (SECTIONS.has('2')) {
  const id = 't300 star 20 px 1440x900';
  const [w, h, theme] = WINDOWS.laptop;
  const graph = MODELS.t300();
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, graph, w, h, 'dark');
  const cards0 = (await probe(page)).cards;
  const grid = starGrid(cards0, graph, 20, w / h).map((t) => ({ ...t, x: Math.round(t.x), y: Math.round(t.y) }));
  await page.evaluate(() => {
    const r = window.__frames = { on: true, t: [], groupOpacity: 0, heldSeen: 0 };
    const tick = (t) => {
      if (!r.on) return;
      r.t.push(t);
      if (t - (r.last ?? -1e9) > 500) {   // a look at a few held lines twice a second, so the check does not slow the frames it measures
        r.last = t;
        for (const g of [...document.querySelectorAll('.react-flow__edge g[style*="opacity: 0.4"]')].slice(0, 12)) { r.heldSeen++; if (Number(getComputedStyle(g).opacity) < 1) r.groupOpacity++; }
      }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  const t0 = await page.evaluate(() => performance.now());
  await post(page, grid);
  let makingEnd = null;
  for (let n = 0; n < 4000; n++) {
    const busy = await page.evaluate(() => ({ making: !!document.querySelector('[data-making-room]'), held: document.querySelectorAll('.react-flow__edge g[style*="opacity: 0.4"]').length }));
    if (busy.making) makingEnd = null; else if (makingEnd === null) makingEnd = await page.evaluate(() => performance.now());
    if (!busy.making && !busy.held && n > 8) break;
    await wait(250);
  }
  const r = await page.evaluate((t0) => { window.__frames.on = false; const t = window.__frames.t.filter((x) => x >= t0); let gap = 0, at = 0; for (let i = 1; i < t.length; i++) if (t[i] - t[i - 1] > gap) { gap = t[i] - t[i - 1]; at = t[i - 1] - t0; } return { frames: t.length, gap: Math.round(gap), at: Math.round(at), groupOpacity: window.__frames.groupOpacity, heldSeen: window.__frames.heldSeen }; }, t0);
  check(id, 'lines held on their old routes are faded with stroke-opacity, not a group opacity (no held line has an opacity below 1)', r.heldSeen > 0 && r.groupOpacity === 0, `${r.groupOpacity} of ${r.heldSeen} held-line looks had a group opacity below 1`);
  check(id, 'no two frames more than 500 ms apart while the page makes room and routes', r.gap <= 500, `longest gap ${r.gap} ms at ${r.at} ms after the save, ${r.frames} frames`);
  info(id, 'making room ended', makingEnd === null ? 'not measured' : `${Math.round(makingEnd - t0)} ms after the save`);
  await ctx.close();
}

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 1));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported`);
process.exit(failed ? 1 : 0);
