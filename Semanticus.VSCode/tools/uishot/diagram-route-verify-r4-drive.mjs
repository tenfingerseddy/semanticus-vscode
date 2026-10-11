// verify: driven proofs from round 4 of the m11/diagram-route verification (28 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness with a stand-in for the
// engine's layout sidecar (saveLayout merges the way LocalEngine.SaveLayoutAsync does, keep sent, not merged), and
// measures the live canvas. Kane's rulings are the bar: no line ever runs behind a card, and no two lines are drawn
// on top of each other; round 3 added lines drawn along another card's border, and speed at 300 tables.
//
//   1. A kept card (save_layout keep=true, or the person's Undo) in the assistant's grid: makeRoom skips a kept card
//      but still pushes the cards before it on, straight into it. The assistant saves its 80-table grid 20 px apart
//      keeping Fact 002 (or Fact 004): Fact 001 (Fact 003) ends on top of it and all nine of its lines leave from under
//      the kept card, behind a card (test/diagram-route-verify-r4.test.mjs: 52 of the 80 cards, kept one at a time).
//   2. Lines along another card's border at 150 tables (round 3 D5; the fix report says none are left at 150): the
//      builder's 150-table model at 900 x 900, the assistant's star grid 20 px apart and plain grid 30 px apart, in
//      whole px as an assistant saves them. Once the page made room, a line runs exactly on the side of a lookup table it
//      does not join, for the card's whole 92 px height (Fact 011 -> Dim 096 along Dim 103 and Dim 110; Fact 001 -> Dim
//      041 along Dim 038): the card looks outlined in the line's colour, and the line cannot be told from the border or
//      followed along it. (With the sizes the harness measures, in float px, the plain grid 40 px apart at 2000 x 1200
//      does the same along Dim 023's top border for 190 px.) It is intermittent: the same save on a fresh page ends in
//      different layouts from run to run (five runs of the star grid gave two saved layouts, after 3 and 4 saves; other
//      runs saved once and left the lines on the borders), because whether the page's extra rounds of making room run at
//      all depends on when each routing pass lands. So each case runs three times per theme and the check fails when
//      any run leaves a line on a border; on this laptop 6 of 12 such runs did (and 1 of 7 in an earlier sweep).
//   3. Speed at 300 tables: the assistant saves its star grid 20 px apart (the builder's 300-table model). The page
//      makes room and routes the result up to five times; one routing pass of that layout takes about 30 s on this
//      laptop, all on the page's main thread, so the page freezes for 30 to 40 s at a time and keeps moving cards for
//      80 to 100 s. The bar is the lane's own budget for the longest freeze on opening a 300-table model
//      (diagram-route-scale-drive.mjs BUDGET.open[300], 8000 ms).
//   4. Snap on, a person's drop: a lookup table in the Studio's own 30-table layout dragged up to 30 px under the card
//      above (20 mouse steps, the drag the round 3 drive makes). The drop snaps to 28 px under it, closer than the 48 px
//      the page itself keeps (ROOM_MIN), and the page leaves it there and saves it there, no toast. With Snap off the same
//      drag, or with Snap on a 26 px one, ends 48 px under. commitPositions reads the dropped card's spot from the flow
//      store (rf.getNodes()), which at the drop still holds the card one snap step back (48 px under, clear), while it
//      saves the spot the drag reports (28 px under). A race, so it runs three times per theme; 11 of 14 such drops hit it
//      in my runs.
//   5. A quick drag (two mouse moves, as a fast flick or a busy page's coalesced pointer events deliver it) of a lookup
//      table onto another in the Studio's own 30-table layout, one drop after another onto twelve different lookup
//      tables: the card is left on top of one to four other cards, no slide and no toast, and the covered cards' lines
//      run behind it (9 of 12 drops at head, 10 of 12 on the round 3 page, so round 3's D2 fix holds only for slow
//      drags). Same cause as 4: commitPositions checks the flow store's spot for the dropped card, which a quick drag
//      has not updated yet, and saves the spot the drag reports.
//   node diagram-route-verify-r4-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4,5)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r4');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4,5').split(','));
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
    // The engine's layout sidecar: an incoming table upserts (keep is sent, not merged), the rest keep theirs; every
    // call is recorded with its time. And Chrome's long-task timing, for what a person feels as the page not answering.
    window.__saves = []; window.__store = []; window.__LONG__ = [];
    new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__LONG__.push({ t: e.startTime, d: e.duration }); }).observe({ type: 'longtask', buffered: true });
    cfg.getLayout = () => ({ tables: JSON.parse(JSON.stringify(window.__store)) });
    cfg.saveLayout = (params) => {
      const t = (params && params[0]) || [];
      window.__saves.push({ t: performance.now(), tables: JSON.parse(JSON.stringify(t)) });
      for (const n of t) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
      return { saved: true, count: t.length };
    };
  }, { graph, columns: columnsFor(graph) });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 300000 });
  await page.waitForSelector('.react-flow__node', { timeout: 300000 });
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
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
// The cards a card lies on top of.
const covering = (p, id) => { const a = p.cards.find((c) => c.id === id); return p.cards.filter((c) => c.id !== id && a.x < c.x + c.w - 0.5 && c.x < a.x + a.w - 0.5 && a.y < c.y + c.h - 0.5 && c.y < a.y + a.h - 0.5).map((c) => c.id); };
// Cards drawn on top of each other, and line ends that sit under a card other than their own.
const cardsOnCards = (p) => { const out = []; for (let i = 0; i < p.cards.length; i++) for (let j = i + 1; j < p.cards.length; j++) { const a = p.cards[i], b = p.cards[j]; const ox = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x), oy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y); if (ox > 0.5 && oy > 0.5) out.push(`${a.id} and ${b.id} overlap ${Math.round(ox)} x ${Math.round(oy)} px`); } return out; };
const endsUnder = (p, graph) => {
  const rel = new Map(graph.relationships.map((r) => [r.name, r])), out = [];
  for (const l of p.lines) {
    const r = rel.get(l.id); if (!r || l.pts.length < 2) continue;
    for (const [pt, own] of [[l.pts[0], r.fromTable], [l.pts[l.pts.length - 1], r.toTable]]) for (const c of p.cards) if (c.id !== own && pt.x > c.x + 0.5 && pt.x < c.x + c.w - 0.5 && pt.y > c.y + 0.5 && pt.y < c.y + c.h - 0.5) out.push(`${l.id} leaves from under ${c.id}`);
  }
  return out;
};
// Runs within 0.5 px of the border of a card the line does not join, along it for over 8 px (the round 3 measure).
const onBorders = (p, graph) => {
  const rel = new Map(graph.relationships.map((r) => [r.name, r])), out = [];
  for (const l of p.lines) {
    const r = rel.get(l.id), ends = r ? new Set([r.fromTable, r.toTable]) : new Set();
    for (let i = 1; i < l.pts.length; i++) {
      const a = l.pts[i - 1], b = l.pts[i], v = Math.abs(a.x - b.x) < 0.01;
      if (!v && Math.abs(a.y - b.y) >= 0.01) continue;
      for (const c of p.cards) {
        if (ends.has(c.id)) continue;
        const at = v ? a.x : a.y, e0 = v ? c.x : c.y, e1 = e0 + (v ? c.w : c.h);
        const ov = Math.min(Math.max(v ? a.y : a.x, v ? b.y : b.x), v ? c.y + c.h : c.x + c.w) - Math.max(Math.min(v ? a.y : a.x, v ? b.y : b.x), v ? c.y : c.x);
        if ((Math.abs(at - e0) < 0.5 || Math.abs(at - e1) < 0.5) && ov > 8) out.push(`${l.id} along ${c.id}'s border for ${Math.round(ov)} px`);
      }
    }
  }
  return out;
};
// The assistant's save_layout: the engine merges it into the sidecar and broadcasts every stored entry.
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
  return performance.now();
}, tables);
// Waits until the page has saved nothing for `quiet` ms and is calm; the saves since `from`.
async function settleSaves(page, from, quiet = 4000, cap = 300000) {
  const t0 = Date.now(); let last = -1, since = Date.now();
  for (;;) {
    await settle(page, 400);
    const n = await page.evaluate(() => window.__saves.length);
    if (n !== last) { last = n; since = Date.now(); }
    if (Date.now() - since >= quiet || Date.now() - t0 > cap) break;
    await wait(400);
  }
  return page.evaluate((f) => window.__saves.filter((s) => s.t >= f), from);
}
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


if (SECTIONS.has('1')) for (const theme of ['dark', 'light']) for (const kept of ['Fact 002', 'Fact 004']) {
  const graph = big(8, 60, 12, 9), id = `eighty-plain20-keep-${kept.replace(' ', '')}-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const grid = plainGrid((await probe(page)).cards, graph, 20, 1440 / 900);
  const t0 = await post(page, grid.map((g) => (g.name === kept ? { ...g, keep: true } : g)));
  const sv = await settleSaves(page, t0);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page), k = p.cards.find((c) => c.id === kept), put = grid.find((g) => g.name === kept);
  info(id, `the assistant keeps ${kept} in its 80-table grid 20 px apart`, `${kept} drawn at (${Math.round(k.x)}, ${Math.round(k.y)}), saved at (${put.x}, ${put.y}); the page saved ${sv.length} times`);
  const on = cardsOnCards(p), under = endsUnder(p, graph), thr = throughOf(p);
  check(id, 'no card is left on top of another', !on.length, on.slice(0, 3).join('; '));
  check(id, 'no line leaves from under another card or runs behind one', !under.length && !thr.length, `${under.length} line ends under a card (${under.slice(0, 3).join('; ')}); ${thr.length} behind a card (${thr.slice(0, 3).join('; ')})`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

if (SECTIONS.has('2')) for (const theme of ['dark', 'light']) for (const [kind, gap] of [['star', 20], ['plain', 30]]) for (const run of [1, 2, 3]) {
  const graph = big(15, 113, 22, 9), id = `t150-${kind}${gap}-narrow-${theme}-run${run}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 900, 900, theme);
  // Whole px, as an assistant saves them: it is never told card sizes, so it spaces a grid by round numbers.
  const cards = (await probe(page)).cards.map((c) => ({ ...c, w: Math.round(c.w), h: Math.round(c.h) }));
  const grid = (kind === 'star' ? starGrid : plainGrid)(cards, graph, gap, 1).map((t) => ({ ...t, x: Math.round(t.x), y: Math.round(t.y) }));
  const t0 = await post(page, grid);
  const sv = await settleSaves(page, t0);
  await clickText(page, 'Fit'); await settle(page, 1200);
  const p = await probe(page), b = onBorders(p, graph), thr = throughOf(p), pairs = onTop(p.lines);
  info(id, 'the page made room', `saved ${sv.length} times, ${new Set(sv.flatMap((x) => x.tables.map((t) => t.name))).size} tables moved`);
  check(id, `the assistant's ${kind} grid ${gap} px apart, room made: no line along another card's border (within 0.5 px for over 8 px)`, !b.length, `${b.length}: ${b.slice(0, 4).join('; ')}`);
  check(id, 'every line drawn, none behind a card, none on top of another', !thr.length && !pairs.length && p.lines.filter((l) => l.pts.length > 1).length === graph.relationships.length, `${thr.length} behind; ${pairs.length} pairs within 1.5 px`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

if (SECTIONS.has('3')) {
  const graph = big(30, 226, 44, 9), id = 't300-star20-laptop-dark';
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, 'dark');
  const grid = starGrid((await probe(page)).cards, graph, 20, 1440 / 900);
  const t0 = await post(page, grid);
  const sv = await settleSaves(page, t0, 5000, 600000);
  const long = await page.evaluate((f) => window.__LONG__.filter((e) => e.t >= f).map((e) => e.d), t0);
  const longest = Math.round(Math.max(0, ...long)), last = sv.length ? Math.round(sv[sv.length - 1].t - t0) : 0;
  info(id, "after the assistant's star grid 20 px apart", `the page saved ${sv.length} times, the last ${last} ms after the save; long tasks ${Math.round(long.reduce((a, x) => a + x, 0))} ms in all`);
  check(id, "the longest freeze after the assistant's save stays within the lane's own 300-table budget (8000 ms)", longest < 8000, `longest single task ${longest} ms`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

if (SECTIONS.has('4')) for (const theme of ['dark', 'light']) for (const run of [1, 2, 3]) {
  const graph = thirty(), id = `thirty-snap-drop-30px-${theme}-run${run}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await clickText(page, 'Snap'); await settle(page, 600);
  const p0 = await probe(page), dim = isDim(graph);
  const col = (c) => p0.cards.filter((d) => Math.abs(d.x - c.x) < 1 && dim(d.id)).sort((a, b) => a.y - b.y);
  let above = null, mid = null;
  for (const c of p0.cards) { if (!dim(c.id)) continue; const k = col(c), i = k.indexOf(c); if (i > 0 && i < k.length - 1 && c.hx > p0.canvas.x && c.hx < p0.canvas.r && c.hy > p0.canvas.y && c.hy < p0.canvas.b) { above = k[i - 1]; mid = c; break; } }
  const dy = (above.y + above.h + 30 - mid.y) * p0.zoom;
  const t0 = await page.evaluate(() => performance.now());
  await page.mouse.move(mid.hx, mid.hy); await page.mouse.down();
  for (let i = 1; i <= 20; i++) await page.mouse.move(mid.hx, mid.hy + dy * i / 20);
  await page.mouse.up(); await settle(page, 1500);
  const p = await probe(page), at = p.cards.find((c) => c.id === mid.id);
  const gap = Math.round((at.y - (above.y + above.h)) * 10) / 10;
  const saved = await page.evaluate((f) => window.__saves.filter((x) => x.t >= f).flatMap((x) => x.tables), t0);
  const e = saved.filter((t) => t.name === mid.id).pop();
  check(id, `Snap on, ${mid.id} dragged up to 30 px under ${above.id}: it ends where its lines have room (48 px under or more)`, gap >= 48 - 0.5, `ends ${gap} px under, at (${Math.round(at.x)}, ${Math.round(at.y)}); saved ${e ? `(${Math.round(e.x)}, ${Math.round(e.y)})` : 'nothing'}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

if (SECTIONS.has('5')) for (const theme of ['dark', 'light']) {
  const graph = thirty(), dim = isDim(graph), id = `thirty-quick-drop-${theme}`;
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await clickText(page, 'Fit'); await settle(page, 800);
  const targets = (await probe(page)).cards.filter((c) => dim(c.id)).map((c) => c.id).slice(0, 12);
  let left = 0, n = 0;
  const bad = [];
  for (const target of targets) {
    const p = await probe(page), X = p.cards.find((c) => c.id === target);
    const D = p.cards.filter((c) => dim(c.id) && c.id !== X.id && c.hx > p.canvas.x && c.hx < p.canvas.r && c.hy > p.canvas.y && c.hy < p.canvas.b)
      .sort((a, b) => Math.hypot(b.x - X.x, b.y - X.y) - Math.hypot(a.x - X.x, a.y - X.y))[0];
    if (!D) continue;
    const to = { x: D.hx + (X.x + 30 - D.x) * p.zoom, y: D.hy + (X.y + 20 - D.y) * p.zoom };
    await page.mouse.move(D.hx, D.hy); await page.mouse.down();
    await page.mouse.move((D.hx + to.x) / 2, (D.hy + to.y) / 2); await page.mouse.move(to.x, to.y);
    await page.mouse.up(); await settle(page, 1200);
    const q = await probe(page), on = covering(q, D.id);
    n++;
    if (on.length) { left++; bad.push(`${D.id} onto ${X.id}: left covering ${on.join(', ')}`); }
  }
  const q = await probe(page), thr = throughOf(q);
  check(id, 'a quick drag (two mouse moves) onto another lookup table never leaves the card on top of another card', !left, `${left} of ${n} left on other cards: ${bad.slice(0, 3).join('; ')}`);
  check(id, 'after those drops no line runs behind a card', !thr.length, `${thr.length} behind a card: ${thr.slice(0, 3).join('; ')}`);
  await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
