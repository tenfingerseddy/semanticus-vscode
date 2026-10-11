// Driven proof for review round 10 of m11/diagram-route (30 Sep). Kane's "Slide down" ruling stands (a spread is a view: every
// card returns to its saved spot within 0.5 px when the last card closes, and no spot a spread drew ever reaches the store).
//   R9-1  every way a spread that is still being worked out can outlive the open set: Collapse all at several moments at 80
//         tables, a model reload (a link), the assistant's save and a switch of diagram in the middle of the work.
//   L1/L2 a drag while spread saves ONLY the dragged card: its old saved spot plus the distance, moved to the nearest clear
//         spot in the SAVED layout (on the 20 px grid with Snap on); no toast, no Put back, and when the spread ends no
//         other card moves because of it.
//   L3    a redo that brings a relationship back reloads the Diagram.
// Serves the built bundle and opens the real harness with a stand-in for the engine's layout sidecar, as the round 9 drive does.
//   node diagram-route-r10-drive.mjs      (OUTDIR=<dir>; SECTIONS=1,2,3,4,5; THEMES=dark,light; WEBROOT=<checkout>)
//   sections: 1 drop among cards, 2 Snap, 3 Collapse all at several moments (80 tables), 4 reload / assistant's save / switch
//             while the spread is worked out, 5 redo of a link
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r10');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4,5').split(','));
const THEMES = (process.env.THEMES || 'dark,light').split(',');
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
const results = []; let failed = 0;
const check = (id, what, ok, detail = '') => { results.push({ id, what, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${what}${detail ? ': ' + detail : ''}`); };
const info = (id, what, detail) => { results.push({ id, what, info: true, detail }); console.log(`INFO ${id} ${what}: ${detail}`); };

// ---- fixtures (generic names), the same generator as the round 2 to 8 drives ----
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
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));
const ALL_BUSMATRIX = { diagrams: [{ id: 'all', name: 'All tables', all: true, tables: [], positions: {}, layout: 'busmatrix' }], activeDiagramId: 'all' };

async function open(ctx, graph, w, h, theme, store = [], quietFor = 2500) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg, store) => {
    window.__UISHOT__ = cfg;
    window.__saves = []; window.__store = store; window.__graph = JSON.parse(JSON.stringify(cfg.graph)); window.__created = [];
    cfg.getLayout = () => ({ tables: JSON.parse(JSON.stringify(window.__store)) });
    cfg.saveLayout = (params) => {
      const t = (params && params[0]) || [];
      window.__saves.push({ t: performance.now(), tables: JSON.parse(JSON.stringify(t)) });
      for (const n of t) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
      return { saved: true, count: t.length };
    };
    // A link: the next model graph carries it (a NEW object each time, as the engine's answer is).
    cfg.getModelGraph = () => JSON.parse(JSON.stringify(window.__graph));
    cfg.createRelationship = (p) => {
      const split = (ref) => { const r = String(ref).slice('column:'.length); const i = r.indexOf('/'); return { table: r.slice(0, i), column: r.slice(i + 1) }; };
      const m = split(p[0]), o = split(p[1]);
      const name = `${m.table}_${o.table}_link`;
      window.__graph.relationships.push({ name, fromTable: m.table, fromColumn: m.column, toTable: o.table, toColumn: o.column, fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
      window.__created.push(name);
      return 'relationship:' + name;
    };
  }, { graph, columns: columnsFor(graph), state: ALL_BUSMATRIX }, store);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 300000 });
  await page.waitForSelector('.react-flow__node', { timeout: 300000 });
  await applyTheme(page, theme);
  await quiet(page, quietFor);
  return { page, errors };
}
// Until the page has not been making room, held a line or saved anything for `q` ms.
async function quiet(page, q = 2500, cap = 300000) {
  const t0 = Date.now(); let last = -1, since = Date.now();
  for (;;) {
    const s = await page.evaluate(() => ({ busy: !!document.querySelector('[data-making-room], .react-flow__edge g[style*="opacity: 0.4"]'), n: window.__saves.length }));
    if (s.n !== last) { last = s.n; since = Date.now(); }
    if (s.busy) since = Date.now();
    if (Date.now() - since >= q || Date.now() - t0 > cap) return;
    await wait(150);
  }
}
// Every card as drawn: its flow position from the transform, its size from the live box (in flow px).
const boxesOf = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  return [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom };
  });
});
const zoomOf = (page) => page.evaluate(() => Number((/scale\(([^)]+)\)/.exec(document.querySelector('.react-flow__viewport').style.transform) || [0, 1])[1]));
const roomUi = (page) => page.evaluate(() => ({
  toast: [...document.querySelectorAll('span')].map((s) => s.textContent || '').find((t) => /lines have room/.test(t)) ?? null,
  putBack: [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Put back' && b.getClientRects().length),
}));
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const toggle = (page, id, how) => page.evaluate((id, how) => { const b = document.querySelector(`button[aria-label="${how} the columns of ${id}"]`); if (b) b.click(); return !!b; }, id, how);
const openIds = (page) => page.evaluate(() => [...document.querySelectorAll('button[aria-label^="Hide the columns of "]')].map((b) => b.getAttribute('aria-label').slice('Hide the columns of '.length)));
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
}, tables);
function plainGrid(sizes, graph, gap, aspect) {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = sizes.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...sizes.map((c) => c.w)), ch = Math.max(...sizes.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: Math.round((i % cols) * (cw + gap)), y: Math.round(Math.floor(i / cols) * (ch + gap)) }));
}
const atOf = (cards) => Object.fromEntries(cards.map((c) => [c.id, c]));
// The ids whose drawn spot differs from `at` by more than `tol` px.
const off = (cards, at, tol = 0.5) => cards.filter((c) => !at[c.id] || Math.abs(c.x - at[c.id].x) > tol || Math.abs(c.y - at[c.id].y) > tol).map((c) => c.id);
// Pairs of cards drawn one over another (more than 1 px each way).
const overlaps = (cards) => { const out = []; for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) { const a = cards[i], b = cards[j]; if (Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x) > 1 && Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y) > 1) out.push(`${a.id}/${b.id}`); } return out; };
const snapshot = async (page) => {
  const cards = await boxesOf(page), ui = await roomUi(page), st = await page.evaluate(() => JSON.stringify(window.__store)), saves = await page.evaluate(() => window.__saves.length);
  const open = await page.evaluate(() => document.querySelectorAll('button[aria-label^="Hide the columns of "]').length);
  return { cards, at: atOf(cards), ui, st, stored: JSON.parse(st), saves, open };
};
// A later look at the page against a snapshot taken before the first card opened: nothing saved, nothing stored differently
// (get_layout byte for byte, keep marks included), no new toast. `spread` says a card is still open, so the Put back is hidden.
function untouched(before, after, spread) {
  const newToast = after.ui.toast && after.ui.toast !== before.ui.toast;
  const kept = before.stored.filter((e) => e.keep).map((e) => e.name), lost = kept.filter((n) => !after.stored.find((e) => e.name === n)?.keep);
  const putBackOk = spread ? !after.ui.putBack : after.ui.putBack === before.ui.putBack;
  return { ok: after.saves === before.saves && after.st === before.st && !newToast && putBackOk && !lost.length,
    detail: `${after.saves - before.saves} saves; stored layout ${after.st === before.st ? 'identical' : 'DIFFERENT'}; toast ${JSON.stringify(newToast ? after.ui.toast : null)}; Put back ${after.ui.putBack} (${spread ? 'hidden while spread' : 'as before'}: ${putBackOk}); ${lost.length} keep marks lost` };
}
const settleAfter = async (page, q = 1800) => { await wait(800); await quiet(page, q); };
const expandAll = async (page) => { const ok = await clickText(page, 'Expand all'); await settleAfter(page); return ok; };
const collapseAll = async (page) => { const ok = await clickText(page, 'Collapse all'); await settleAfter(page); return ok; };
const openOne = async (page, X) => { const ok = await toggle(page, X, 'Show'); await settleAfter(page); return ok; };
const closeOne = async (page, X) => { const ok = await toggle(page, X, 'Hide'); await settleAfter(page); return ok; };

// The page after the assistant's plain grid 20 px apart: the page made room (moved cards, saved them, offered the toast).
async function madeRoom(graph, theme, keepAll = false, store = []) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme, store);
  const closed = await boxesOf(page);
  const grid = plainGrid(closed, graph, 20, 1440 / 900);
  await post(page, keepAll ? grid.map((t) => ({ ...t, keep: true })) : grid);
  await wait(800); await quiet(page, 4500);
  return { ctx, page, errors, grid, closed };
}

const graph = thirty();
const storedAt = (stored) => Object.fromEntries(stored.map((e) => [e.name, { x: e.x, y: e.y }]));
function starGrid(cards, graph, gap, aspect) {
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
  return out.map((t) => ({ ...t, x: Math.round(t.x), y: Math.round(t.y) }));
}
// The 80-table page after the assistant's grid and the page's make-room.
async function madeRoomWith(graph, grid, gap, theme) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await post(page, grid(await boxesOf(page), graph, gap, 1440 / 900));
  await wait(800); await quiet(page, 4500);
  return { ctx, page, errors };
}
// In the page: click Expand all, then run `then` (a function of the page's document) `after` ms later. Returns when it ran and
// whether the spread was still being worked out at that moment (the canvas says so with data-making-room).
const expandThen = (page, after, thenSrc) => page.evaluate((after, thenSrc) => new Promise((resolve) => {
  const t0 = performance.now();
  const btn = (t) => [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  setTimeout(() => { const making = !!document.querySelector('[data-making-room]'); new Function('btn', thenSrc)(btn); resolve({ at: Math.round(performance.now() - t0), making }); }, after);
  btn('Expand all').click();
}), after, thenSrc);
const linkPush = (page, from, to) => page.evaluate((from, to) => {
  const g = window.__graph, name = `${from}_${to}_link`;
  const fc = (t) => t.replace(/ /g, '') + 'Field1';
  if (!g.relationships.some((r) => r.name === name)) g.relationships.push({ name, fromTable: from, fromColumn: fc(from), toTable: to, toColumn: fc(to), fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
  window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: 2, origin: 'human', deltas: [{ kind: 'add', ref: 'relationship:' + name }] } }, '*');
}, from, to);
const freeSpot = (page) => page.evaluate(() => {
  const pane = document.querySelector('.react-flow__pane').getBoundingClientRect();
  const cards = [...document.querySelectorAll('.react-flow__node')].map((n) => n.getBoundingClientRect());
  const pts = [];
  for (let y = pane.y + 120; y < pane.bottom - 140; y += 20) for (let x = pane.x + 120; x < pane.right - 260; x += 20) {
    const box = { l: x - 10, t: y - 10, r: x + 130, b: y + 60 };
    if (!cards.some((c) => c.left < box.r && c.right > box.l && c.top < box.b && c.bottom > box.t)) pts.push({ x, y });
  }
  const cx = (pane.x + pane.right) / 2, cy = (pane.y + pane.bottom) / 2;
  pts.sort((a, b) => Math.hypot(a.x - cx, a.y - cy) - Math.hypot(b.x - cx, b.y - cy));
  return pts.length ? pts[0] : null;
});
const headOf = (page, id) => page.evaluate((id) => { const h = document.querySelector(`.react-flow__node[data-id="${id}"] span.font-semibold`).getBoundingClientRect(); return { x: h.x + 10, y: h.y + h.height / 2 }; }, id);
const nodeXY = (page, id) => page.evaluate((id) => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(document.querySelector(`.react-flow__node[data-id="${id}"]`).style.transform); return { x: Number(m[1]), y: Number(m[2]) }; }, id);
const drag = async (page, from, to) => {
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  for (let i = 1; i <= 24; i++) await page.mouse.move(from.x + (to.x - from.x) * i / 24, from.y + (to.y - from.y) * i / 24);
  await page.mouse.up();
};
const fit = async (page) => { await clickText(page, 'Fit'); await wait(900); for (let last = -1, i = 0; i < 40; i++) { const z = await zoomOf(page); if (Math.abs(z - last) < 1e-6) break; last = z; await wait(250); } };
const g80 = () => big(8, 60, 12, 9);
// A toast about room appearing at any moment in the next `ms` ms (other than the one already there).
const toastWithin = async (page, ms, was) => { let t = null; for (let i = 0; i < ms / 150 && !t; i++) { await wait(150); const x = (await roomUi(page)).toast; if (x && x !== was) t = x; } return t; };

// ---- 1: a card dropped on another while spread: only it is saved, at its old saved spot plus the distance moved to the nearest clear spot of the SAVED layout; no toast, no Put back, and when the spread ends no other card moves ----
if (SECTIONS.has('1')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory');
  await fit(page);
  const from = await headOf(page, 'Date'), to = await headOf(page, 'Store');
  await drag(page, from, { x: to.x + 12, y: to.y + 8 }); await settleAfter(page, 1500);
  const s2 = await snapshot(page);
  const saves1 = await page.evaluate((n) => window.__saves.slice(n).map((x) => x.tables.map((t) => t.name)), s0.saves);
  const dropSaved = s2.stored.find((e) => e.name === 'Date');
  check(`drop-${theme}-saved`, 'the drag saved only the dragged card, once, with no toast and no Put back', saves1.length === 1 && saves1[0].length === 1 && saves1[0][0] === 'Date' && !s2.ui.toast && !s2.ui.putBack, JSON.stringify(saves1) + ' toast ' + JSON.stringify(s2.ui.toast) + ' Put back ' + s2.ui.putBack);
  const sized = s0.cards.map((c) => (c.id === 'Date' ? { ...c, x: dropSaved.x, y: dropSaved.y } : c));
  const hit = overlaps(sized).filter((p) => p.split('/').includes('Date'));
  check(`drop-${theme}-clear-saved`, 'its saved spot sits on no card in the SAVED layout (the nearest clear spot)', hit.length === 0, `overlaps with ${hit.join(', ') || 'none'}; saved (${dropSaved.x}, ${dropSaved.y}), before (${s0.at.Date.x}, ${s0.at.Date.y})`);
  const before = await page.evaluate(() => window.__saves.length);
  await clickText(page, 'Collapse all');
  const toast = await toastWithin(page, 5000, s0.ui.toast);
  await quiet(page, 3500);
  const s3 = await snapshot(page);
  const later = await page.evaluate((n) => window.__saves.slice(n).flatMap((x) => x.tables.map((t) => t.name)), before);
  check(`drop-${theme}-nothing-else`, 'when the spread ends the page moves no other card, saves nothing more and shows no toast or Put back', !later.length && !toast && !s3.ui.putBack, `${later.length} cards saved after Collapse all (${later.slice(0, 6).join(', ')}); toast ${JSON.stringify(toast)}; Put back ${s3.ui.putBack}`);
  const expectAt = { ...s0.at, Date: { ...s0.at.Date, x: dropSaved.x, y: dropSaved.y } };
  const moved = off(s3.cards, expectAt);
  check(`drop-${theme}-closed`, 'every other card is back on the spot it had before, and the dragged card is on its new saved spot', !moved.length, `${moved.length} off: ${moved.slice(0, 6).join(', ')}`);
  await page.screenshot({ path: join(OUT, `drop-${theme}-closed.png`) });
  if (errors.length) check(`drop-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: Snap on, a card dragged into empty canvas while spread is saved on the 20 px grid, and only it ----
if (SECTIONS.has('2')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  await clickText(page, 'Snap'); await wait(500); await quiet(page, 2000);
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory'); await openOne(page, 'Sales'); await openOne(page, 'Budget');
  const vp = await page.evaluate(() => { const r = document.querySelector('.react-flow__pane').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
  await page.mouse.move(vp.x + vp.w / 2, vp.y + vp.h / 2);
  for (let i = 0; i < 5; i++) { await page.mouse.wheel({ deltaY: 100 }); await wait(120); }
  for (let last = -1, i = 0; i < 40; i++) { const z = await zoomOf(page); if (Math.abs(z - last) < 1e-6) break; last = z; await wait(250); }
  await quiet(page, 1500);
  const spot = await freeSpot(page);
  if (!spot) { check(`snap-${theme}`, 'an empty spot on the canvas to drop into', false, 'none found'); await ctx.close(); continue; }
  const before = await page.evaluate(() => window.__saves.length);
  await drag(page, await headOf(page, 'Date'), spot); await settleAfter(page, 1500);
  const s2 = await snapshot(page);
  const saves1 = await page.evaluate((n) => window.__saves.slice(n).map((x) => x.tables.map((t) => t.name)), before);
  const d = s2.stored.find((e) => e.name === 'Date');
  check(`snap-${theme}-saved`, 'Snap on: only the dragged card is saved, once', saves1.length === 1 && saves1[0].length === 1 && saves1[0][0] === 'Date', JSON.stringify(saves1));
  check(`snap-${theme}-grid`, 'and its saved spot is on the 20 px grid', !!d && Math.abs(d.x % 20) < 0.01 && Math.abs(d.y % 20) < 0.01, `saved (${d?.x}, ${d?.y}); saved before (${s0.at.Date.x}, ${s0.at.Date.y})`);
  const sized = s0.cards.map((c) => (c.id === 'Date' ? { ...c, x: d.x, y: d.y } : c));
  check(`snap-${theme}-clear-saved`, 'and it sits on no card in the SAVED layout', !overlaps(sized).filter((p) => p.split('/').includes('Date')).length, overlaps(sized).filter((p) => p.split('/').includes('Date')).join(', ') || 'none');
  const b2 = await page.evaluate(() => window.__saves.length);
  await clickText(page, 'Collapse all'); const toast = await toastWithin(page, 4000, s0.ui.toast); await quiet(page, 3500);
  const s3 = await snapshot(page);
  const later = await page.evaluate((n) => window.__saves.slice(n).flatMap((x) => x.tables.map((t) => t.name)), b2);
  const moved = off(s3.cards, { ...s0.at, Date: { ...s0.at.Date, x: d.x, y: d.y } });
  check(`snap-${theme}-closed`, 'when the spread ends no other card moves and nothing more is saved', !later.length && !moved.length && !toast && !s3.ui.putBack, `${later.length} saved after (${later.slice(0, 5).join(', ')}); ${moved.length} off (${moved.slice(0, 5).join(', ')}); toast ${JSON.stringify(toast)}; Put back ${s3.ui.putBack}`);
  if (errors.length) check(`snap-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 3: Collapse all at several moments while the spread is still being worked out (80 tables): every card back on its saved spot, nothing saved ----
if (SECTIONS.has('3')) for (const [theme, after] of [['dark', 200], ['dark', 450], ['dark', 700], ['dark', 1000], ['light', 350], ['light', 800]]) {
  const { ctx, page, errors } = await madeRoomWith(g80(), starGrid, 40, theme);
  const s0 = await snapshot(page);
  const how = await expandThen(page, after, "btn('Collapse all').click();");
  await wait(1000); await quiet(page, 3000);
  const s1 = await snapshot(page);
  const back = off(s1.cards, s0.at);
  info(`sweep-${theme}-${after}`, 'Collapse all clicked', `at ${how.at} ms after Expand all; the spread still being worked out: ${how.making}`);
  check(`sweep-${theme}-${after}-back`, 'every card is back on its saved spot within 0.5 px, nothing saved, nothing stored differently', !back.length && s1.saves === s0.saves && s1.st === s0.st && s1.open === 0, `${back.length} of ${s1.cards.length} off (${back.slice(0, 4).join(', ')}); ${s1.saves - s0.saves} saves; stored ${s1.st === s0.st ? 'identical' : 'DIFFERENT'}; ${s1.open} open`);
  if (errors.length) check(`sweep-${theme}-${after}`, 'no page errors', false, errors.slice(0, 3).join(' | '));
  await ctx.close();
}

// ---- 4: a model reload, the assistant's save and a switch of diagram in the middle of the spread's work ----
if (SECTIONS.has('4')) {
  const theme = 'dark';
  const A = ['Fact 002', 'Dim 050'];
  // a. a link (a model reload) while the spread is worked out: what is stored is what the same link stores with every card closed
  {
    const c = await madeRoomWith(g80(), starGrid, 40, theme);
    await linkPush(c.page, ...A); await wait(1200); await quiet(c.page, 4000);
    const control = await snapshot(c.page); await c.ctx.close();
    const { ctx, page, errors } = await madeRoomWith(g80(), starGrid, 40, theme);
    const s0 = await snapshot(page);
    const how = await expandThen(page, 250, `btn('Collapse all');`);
    await linkPush(page, ...A);
    await wait(600); await clickText(page, 'Collapse all'); await wait(1200); await quiet(page, 4500);
    const s1 = await snapshot(page);
    info('reload', 'the link came 250 ms after Expand all; Collapse all 600 ms after that', `the spread still being worked out at the link: ${how.making}`);
    check('reload-stored', 'the make-room for the link stores exactly what it stores with every card closed (no spread spot)', s1.st === control.st, `${JSON.parse(s1.st).filter((e, i) => JSON.stringify(e) !== JSON.stringify(JSON.parse(control.st)[i])).length} entries differ`);
    const offs = off(s1.cards, storedAt(s1.stored));
    check('reload-drawn', 'every card is drawn on its stored spot and none is open', !offs.length && s1.open === 0, `${offs.length} of ${s1.cards.length} drawn off their stored spot (${offs.slice(0, 4).join(', ')}); ${s1.open} open`);
    if (errors.length) check('reload', 'no page errors', false, errors.slice(0, 3).join(' | '));
    await ctx.close();
  }
  // b. the assistant's save while the spread is worked out
  {
    const g = g80();
    const c = await madeRoomWith(g, starGrid, 40, theme);
    const plain = plainGrid(await boxesOf(c.page), g, 20, 1440 / 900);   // from the closed boxes, for the control and the run alike
    await post(c.page, plain); await wait(1000); await quiet(c.page, 4500);
    const control = await snapshot(c.page); await c.ctx.close();
    const { ctx, page, errors } = await madeRoomWith(g80(), starGrid, 40, theme);
    const how = await expandThen(page, 250, `btn('Collapse all');`);
    await post(page, plain);
    await wait(600); await clickText(page, 'Collapse all'); await wait(1200); await quiet(page, 4500);
    const s1 = await snapshot(page);
    info('assistant', 'the assistant saved 250 ms after Expand all; Collapse all 600 ms after that', `the spread still being worked out at the save: ${how.making}`);
    const offs = off(s1.cards, storedAt(s1.stored));
    check('assistant-drawn', 'every card is drawn on its stored spot and none is open', !offs.length && s1.open === 0, `${offs.length} of ${s1.cards.length} drawn off their stored spot (${offs.slice(0, 4).join(', ')}); ${s1.open} open`);
    const offc = off(s1.cards, atOf(control.cards));
    check('assistant-stored', 'and the cards are where the same save leaves them with every card closed', !offc.length, `${offc.length} differ from the closed run (${offc.slice(0, 4).join(', ')})`);
    if (errors.length) check('assistant', 'no page errors', false, errors.slice(0, 3).join(' | '));
    await ctx.close();
  }
  // c. a switch of diagram while the spread is worked out: leaving and coming back, then Collapse all
  {
    const { ctx, page, errors } = await madeRoomWith(g80(), starGrid, 40, theme);
    const s0 = await snapshot(page);
    await expandThen(page, 250, `document.querySelector('button[aria-label="New diagram"]').click();`);
    await wait(1200);
    await page.keyboard.press('Escape'); await wait(300);
    await page.evaluate(() => { const sel = document.querySelector('select[title="Switch diagram"]'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; set.call(sel, 'all'); sel.dispatchEvent(new Event('change', { bubbles: true })); });
    await wait(500); await clickText(page, 'Collapse all'); await wait(1200); await quiet(page, 4500);
    const s1 = await snapshot(page);
    const back = off(s1.cards, s0.at);
    check('switch-back', 'every card is back on its saved spot, nothing saved, nothing stored differently', !back.length && s1.saves === s0.saves && s1.st === s0.st && s1.open === 0, `${back.length} of ${s1.cards.length} off (${back.slice(0, 4).join(', ')}); ${s1.saves - s0.saves} saves; stored ${s1.st === s0.st ? 'identical' : 'DIFFERENT'}; ${s1.open} open`);
    if (errors.length) check('switch', 'no page errors', false, errors.slice(0, 3).join(' | '));
    await ctx.close();
  }
}

// ---- 5: a redo that brings a relationship back reloads the Diagram ----
if (SECTIONS.has('5')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  const lines = () => page.evaluate(() => document.querySelectorAll('.react-flow__edge').length);
  const n0 = await lines();
  // The model gained a link and the engine says so the way it says a redo: a walk, with no delta for an add (TOM fires no event for one).
  await page.evaluate(() => {
    window.__graph.relationships.push({ name: 'Sales_Notes_link', fromTable: 'Sales', fromColumn: 'SalesField1', toTable: 'Notes', toColumn: 'NotesField1', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
    window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: 3, origin: 'human', label: 'redo', replay: 'redo', deltas: [] } }, '*');
  });
  await wait(1500); await quiet(page, 2500);
  const n1 = await lines();
  check(`redo-${theme}`, 'the redo that brings a link back shows the link (one more line)', n1 === n0 + 1, `${n0} lines before, ${n1} after`);
  // An undo (the link removed) already reloads: the same check the other way round.
  await page.evaluate(() => {
    window.__graph.relationships = window.__graph.relationships.filter((r) => r.name !== 'Sales_Notes_link');
    window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: 4, origin: 'human', label: 'undo', replay: 'undo', deltas: [{ kind: 'remove', ref: 'relationship:Sales_Notes_link' }] } }, '*');
  });
  await wait(1500); await quiet(page, 2500);
  check(`undo-${theme}`, 'and the undo that takes it away removes it again', (await lines()) === n0, `${await lines()} lines, ${n0} at first`);
  if (errors.length) check(`redo-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
