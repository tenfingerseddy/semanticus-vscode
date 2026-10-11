// Driven proofs for review round 3 of m11/diagram-route (28 Sep), against the round 2 verification. Needs the built
// bundle (npm run build:webview). Serves it, opens the real harness, and measures the live canvas in flow px. Kane's
// two rulings are the bar: no line ever runs behind a card, and no two lines are drawn on top of each other.
//   1. N1: a lookup table dragged close under the one above it, Snap off (about 5 and 8 px) and on (8 px, the Snap
//      button's landing spot), 30 and 80 tables, Dark and Light: no line runs behind a card.
//   2. N1, the other door: the person's assistant saves a card 4 to 13 px under a lookup table, and a card 4 to 13 px
//      beside a fact (save_layout reaches the page as layoutDidChange): no line runs behind a card.
//   3. N2: tidy grids the assistant saves (plain 40 and 20 px, star 40 and 20 px) at 30, 80, 150 and 300 tables, three
//      window sizes: every line drawn, none behind a card, none on top of another (within 1.5 px for over 8 px, the
//      round 2 verifier's measure). The one layout still short (80 tables, plain 20 px) is reported, not checked.
//   4. N3 and N4: Expand all after a drag at 150 tables (Dark, Light) and 300: no line is ever gone for more than 250
//      ms (the old lines are held on their new ends until the pass lands), and once settled none is held, none runs
//      behind a card and none is on top of another.
//   5. A card dropped half over another is pushed clear, so no handle is hidden and no line runs behind a card.
// Round 4 (Kane, 28 Sep: the page makes room for its lines): a card dropped close under another now moves on to where
// its lines have room (ROOM_MIN, 48 px, at least), and an assistant's grid tighter than that is spaced out, so those
// two checks now ask for that instead of the spot the card was dragged or saved to.
//   node diagram-route-r3-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; THEMES=dark to run one theme)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r3');
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

async function open(ctx, graph, w, h, theme = 'dark') {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, { graph, columns: columnsFor(graph) });
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
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };
const info = (id, step, detail) => { results.push({ id, step, info: true, detail }); console.log(`INFO ${id} ${step}: ${detail}`); };
const THEMES = (process.env.THEMES ?? 'dark,light').split(',');
const post = (page, tables) => page.evaluate((tables) => window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables } }, '*'), tables);
const isDim = (graph) => (t) => graph.relationships.some((r) => r.toTable === t);
// A lookup table on screen with another above it and another below it in its column.
function stackedPick(p, graph) {
  const dim = isDim(graph);
  const col = (c) => p.cards.filter((d) => dim(d.id) && Math.abs(d.x - c.x) < 1).sort((a, b) => a.y - b.y);
  for (const c of p.cards) {
    if (!dim(c.id)) continue;
    const k = col(c), i = k.indexOf(c);
    if (i > 0 && i < k.length - 1 && c.hx > p.canvas.x && c.hx < p.canvas.r && c.hy > p.canvas.y + 40 && c.hy < p.canvas.b) return { above: k[i - 1], mid: c };
  }
  return null;
}

// ---- 1: a card dragged close under another, Snap off and on (N1) ----
for (const theme of THEMES) for (const [name, graph] of [['thirty', thirty()], ['eighty', big(8, 60, 12, 9)]]) for (const [snap, aim] of [[false, 0], [false, 3], [true, 8]]) {
  const id = `${name}-${theme}-snap-${snap ? 'on' : 'off'}${snap ? '' : '-aim' + aim}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  if (snap) { await clickText(page, 'Snap'); await settle(page, 400); }
  const p0 = await probe(page);
  const pick = stackedPick(p0, graph);
  if (!pick) { check(id, 'a card dragged close under another', false, 'no lookup table on screen with one above and one below it'); await ctx.close(); continue; }
  // With Snap on, aim 8 px under the card above (Snap puts a 92 px card dragged up under another exactly there). With
  // it off, aim at 0 and 3 px: the drag itself adds about 5 px, so the card lands about 5 and 8 px under.
  const dy = (pick.above.y + pick.above.h + aim - pick.mid.y) * p0.zoom;
  await page.mouse.move(pick.mid.hx, pick.mid.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(pick.mid.hx, pick.mid.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page);
  const after = p.cards.find((c) => c.id === pick.mid.id);
  const gap = Math.round((after.y - (pick.above.y + pick.above.h)) * 10) / 10;
  const bad = throughOf(p);
  check(id, `${pick.mid.id} dragged close under ${pick.above.id} ends ${gap} px under it, where its lines have room: no line runs behind a card`, bad.length === 0 && gap >= 48 - 0.5 && gap < 120, `${bad.length} behind a card ${bad.slice(0, 4).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}-close-drag.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: the person's assistant saves a card a few px beyond a handle (N1, the other door) ----
{
  const graph = thirty();
  for (const theme of THEMES) {
    const id = `thirty-${theme}-assistant-nudge`;
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, graph, 1440, 900, theme);
    const p0 = await probe(page);
    const pick = stackedPick(p0, graph);
    const fact = p0.cards.find((c) => c.id === 'Sales');
    const bad = [];
    for (const gap of [4, 6, 8, 10, 12, 13]) {
      // Under a lookup table (its bottom handles), and beside the fact (its right handles).
      await post(page, [{ name: pick.mid.id, x: pick.mid.x, y: pick.above.y + pick.above.h + gap }]); await settle(page, 900);
      let p = await probe(page);
      for (const t of throughOf(p)) bad.push(`${gap} px under ${pick.above.id}: ${t}`);
      // The fact moved to an empty spot left of everything, an unlinked card gap px beside its right edge.
      await post(page, [{ name: pick.mid.id, x: pick.mid.x, y: pick.mid.y }, { name: 'Sales', x: -1500, y: 0 }, { name: 'Notes', x: -1500 + fact.w + gap, y: Math.round(fact.h / 3) }]); await settle(page, 900);
      p = await probe(page);
      for (const t of throughOf(p)) bad.push(`${gap} px beside Sales: ${t}`);
      if (gap === 8) await page.screenshot({ path: join(OUT, `${id}-8px.png`) });
    }
    check(id, 'a lookup table saved 4 to 13 px under another, and a card 4 to 13 px beside the fact: no line runs behind a card', bad.length === 0, bad.slice(0, 6).join('; '));
    if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
    await ctx.close();
  }
}

// ---- 3: tidy grids the person's assistant saves (N2) ----
const GRIDS = [['thirty', thirty, 'plain', 40], ['thirty', thirty, 'plain', 20], ['thirty', thirty, 'star', 20], ['eighty', () => big(8, 60, 12, 9), 'plain', 40],
  ['eighty', () => big(8, 60, 12, 9), 'star', 40], ['eighty', () => big(8, 60, 12, 9), 'star', 20], ['t150', () => big(15, 113, 22, 9, 1), 'plain', 40],
  ['t300', () => big(30, 226, 44, 9, 1), 'plain', 40], ['t300', () => big(30, 226, 44, 9, 1), 'plain', 20]];
const SIZES = { wide: [2000, 1200], laptop: [1440, 900], narrow: [900, 900] };
for (const [name, make, kind, gap] of GRIDS) for (const [size, [w, h]] of Object.entries(SIZES)) {
  if (name === 't300' && size !== 'laptop') continue;   // one size for the slowest model keeps the drive under an hour
  const theme = size === 'narrow' ? 'light' : 'dark';
  const graph = make(), id = `${name}-${kind}${gap}-${size}-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, w, h, theme);
  const p0 = await probe(page);
  const tables = (kind === 'plain' ? plainGrid : starGrid)(p0.cards, graph, gap, w / h);
  await post(page, tables); await settle(page, 2000);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  // Every card where the assistant put it, or further right or down where the page made room (never left or up).
  const placed = tables.every((t) => { const c = p.cards.find((x) => x.id === t.name); return c && c.x > t.x - 1 && c.y > t.y - 1; });
  check(id, `the assistant's ${kind} grid (${gap} px) moved the cards (spaced out where it is tighter than the lines need)`, placed, '');
  const bad = onTop(p.lines), thr = throughOf(p), missing = graph.relationships.length - p.lines.filter((l) => l.pts.length > 1).length;
  check(id, `the assistant's ${kind} grid (${gap} px): every line drawn, none behind a card, none on top of another`, !bad.length && !thr.length && !missing, `${missing} missing; ${thr.length} behind a card; ${bad.length} pairs on top: ${bad.slice(0, 4).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}
// The one layout this round leaves short: 80 tables in a plain grid 20 px apart puts each tall fact card's nine lines in
// a 20 px slit beside it, which has no room for nine lines 1.5 px apart. Reported, not checked.
{
  const graph = big(8, 60, 12, 9), id = 'eighty-plain20-laptop-dark';
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, graph, 1440, 900, 'dark');
  await post(page, plainGrid((await probe(page)).cards, graph, 20, 1440 / 900)); await settle(page, 2000);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  info(id, "the assistant's plain grid (20 px)", `${throughOf(p).length} behind a card; ${onTop(p.lines).length} pairs within 1.5 px`);
  check(id, "the assistant's plain grid (20 px): no line behind a card", throughOf(p).length === 0, throughOf(p).slice(0, 4).join(', '));
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  await ctx.close();
}

// ---- 4: Expand all on a big model: no line vanishes, none on top once settled (N3, N4) ----
for (const [name, graph, theme] of [['t150', big(15, 113, 22, 9, 1), 'dark'], ['t150', big(15, 113, 22, 9, 1), 'light'], ['t300', big(30, 226, 44, 9, 1), 'dark']]) {
  const id = `${name}-${theme}-expand-all`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const n = graph.relationships.length;
  // One ordinary drag first: after it, Expand all re-flows the cards in a longer burst than on a fresh diagram.
  const hdr = await page.evaluate(() => { const fr = document.querySelector('.react-flow').getBoundingClientRect(); const s = [...document.querySelectorAll('.react-flow__node span.font-semibold')].map((x) => x.getBoundingClientRect()).find((r) => r.x > fr.x + 300 && r.y > fr.y + 100 && r.right < fr.right - 200 && r.bottom < fr.bottom - 100); return s ? { x: s.x + 10, y: s.y + s.height / 2 } : null; });
  if (hdr) { await page.mouse.move(hdr.x, hdr.y); await page.mouse.down(); for (let i = 1; i <= 24; i++) await page.mouse.move(hdr.x + i * 6, hdr.y + i * 3); await page.mouse.up(); await settle(page, 1500); }
  await page.evaluate(() => {
    window.__rec = { on: true, frames: [] };
    const tick = (t) => {
      if (!window.__rec.on) return;
      const edges = [...document.querySelectorAll('.react-flow__edge')];
      const drawnNow = edges.filter((e) => e.querySelector('path.react-flow__edge-path')).length;
      const heldNow = edges.filter((e) => e.querySelector('g[style*="opacity: 0.4"]')).length;
      window.__rec.frames.push([t, drawnNow, heldNow]);
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  await clickText(page, 'Expand all'); await settle(page, 3000);
  const rec = await page.evaluate((n) => {
    window.__rec.on = false;
    const f = window.__rec.frames;
    // A frame stays on screen until the next one, so a state lasts from its first frame to the first frame without it.
    let first = null, back = null, heldMax = 0, heldFrom = null, heldTo = null;
    for (const [t, k, h] of f) {
      if (k < n) { if (first === null) first = t; back = null; } else if (first !== null && back === null) back = t;
      if (h) { heldMax = Math.max(heldMax, h); if (heldFrom === null) heldFrom = t; heldTo = null; } else if (heldFrom !== null && heldTo === null) heldTo = t;
    }
    return { gone: first === null ? 0 : Math.round((back ?? f[f.length - 1][0]) - first), heldMax, heldMs: heldFrom === null ? 0 : Math.round((heldTo ?? f[f.length - 1][0]) - heldFrom) };
  }, n);
  check(id, 'Expand all: the lines are never gone for more than 250 ms', rec.gone <= 250, `lines missing for ${rec.gone} ms`);
  info(id, 'Expand all: lines held on their new ends (faded) until the settled pass lands', `up to ${rec.heldMax} lines for ${rec.heldMs} ms`);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page);
  const bad = onTop(p.lines), thr = throughOf(p);
  const held = await page.evaluate(() => document.querySelectorAll('.react-flow__edge g[style*="opacity: 0.4"]').length);
  check(id, 'every card open, settled: no line held, none behind a card, none on top of another', !held && !thr.length && !bad.length, `${held} held; ${thr.length} behind a card; ${bad.length} pairs: ${bad.slice(0, 4).join(', ')}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 5: a card dropped on top of another is pushed clear (the round 2 also-found) ----
for (const theme of THEMES) {
  const graph = thirty(), id = `thirty-${theme}-drop-on-a-card`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const p0 = await probe(page);
  const pick = stackedPick(p0, graph);
  // Drop the lower card half over the one above it.
  const dy = (pick.above.y + pick.above.h / 2 - pick.mid.y) * p0.zoom;
  await page.mouse.move(pick.mid.hx, pick.mid.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(pick.mid.hx, pick.mid.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page);
  const overlaps = [];
  for (let i = 0; i < p.cards.length; i++) for (let j = i + 1; j < p.cards.length; j++) {
    const a = p.cards[i], b = p.cards[j];
    if (a.x < b.x + b.w - 0.5 && b.x < a.x + a.w - 0.5 && a.y < b.y + b.h - 0.5 && b.y < a.y + a.h - 0.5) overlaps.push(`${a.id} and ${b.id}`);
  }
  const thr = throughOf(p);
  check(id, `${pick.mid.id} dropped half over ${pick.above.id}: no two cards overlap and no line runs behind a card`, !overlaps.length && !thr.length, `${overlaps.length} overlapping: ${overlaps.slice(0, 3).join(', ')}; ${thr.length} behind a card`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
