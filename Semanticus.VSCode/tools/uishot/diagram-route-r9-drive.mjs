// Driven proof for Kane's round 9 ruling (29 Sep, "Slide down"): OPENING IS A VIEW, NOT A LAYOUT CHANGE. While any card is
// open the page draws a SPREAD on screen only (the cards below an open card slide down by its growth, then apart until no
// card covers another and the lines have room), worked out from the SAVED layout plus the set of open cards. The spread
// saves nothing, shows no toast, offers no Put back and changes no keep mark; closing the last open card (or Collapse all)
// puts every card back on its saved spot. While spread, zoom, pan, select and a link save nothing (the page's own make-room
// waits until the spread ends); a drag moves that card's SAVED spot by the distance it was dragged and saves only that card;
// an arrange button, the assistant's save_layout, adding a table or switching diagram ends the spread first; leaving the
// Diagram and coming back redraws it. No position the spread drew ever reaches the engine's layout.
// Serves the built bundle, opens the real harness with a stand-in for the engine's layout sidecar (saveLayout merges the way
// LocalEngine.SaveLayoutAsync does; createRelationship adds a link and the next getModelGraph answers with it), and reads
// the live canvas.
//   node diagram-route-r9-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4,5,6,7,8,9; WEBROOT=<checkout>)
//   sections: 1 one card, 2 Expand all, 3 several looks in a row, 4 zoom / pan / select, 5 drag, 6 link, 7 assistant's save,
//             8 leave and return, 9 the routing time after Expand all at 300 tables (reported, not checked)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r9');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4,5,5b,6,7,8,10,9').split(','));
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
  return { cards, at: atOf(cards), ui, st, stored: JSON.parse(st), saves };
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

// ---- 1: one card opened slides the cards below it down, and closing puts everything back ----
if (SECTIONS.has('1')) for (const theme of THEMES) {
  const { ctx, page, errors, grid } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  check(`one-${theme}`, 'precondition: the page made room for the assistant\'s grid (moved cards, saved them, offered the Put back)', off(s0.cards, atOf(grid.map((t) => ({ id: t.name, ...t })))).length > 0 && s0.saves > 0 && s0.ui.putBack, `${s0.saves} saves, Put back shown ${s0.ui.putBack}`);
  let movedAny = 0;
  for (const X of ['Inventory', 'Sales', 'Budget', 'Date', 'Product']) {
    const opened = await openOne(page, X); const s1 = await snapshot(page);
    const u = untouched(s0, s1, true);
    check(`one-${theme}-open-${X}`, `${X} opened: nothing saved, stored layout identical, no toast, Put back hidden`, opened && u.ok, u.detail);
    const ov = overlaps(s1.cards);
    check(`one-${theme}-nohide-${X}`, `${X} opened: no card is drawn over another, so nothing is hidden under the open card`, ov.length === 0, `${ov.length} pairs: ${ov.slice(0, 4).join(', ')}`);
    // The cards below the opened one, in its column, moved down by at least how much it grew.
    const me0 = s0.at[X], me1 = s1.cards.find((c) => c.id === X);
    const grew = me1.h - me0.h;
    const below = s0.cards.filter((c) => c.id !== X && c.y >= me0.y + me0.h - 1 && Math.min(c.x + c.w, me0.x + me0.w) - Math.max(c.x, me0.x) > 1);
    const slid = below.filter((c) => s1.at[c.id].y - c.y >= grew - 1);
    const moved = off(s1.cards, s0.at);
    movedAny += moved.length;
    check(`one-${theme}-slide-${X}`, `${X} opened (grew ${Math.round(grew)} px): every card below it in its column slid down by at least that`, slid.length === below.length, `${slid.length} of ${below.length} below it slid; ${moved.length} cards moved in all`);
    if (X === 'Inventory') await page.screenshot({ path: join(OUT, `one-${theme}-open-${X}.png`) });
    const closedAgain = await closeOne(page, X); const s2 = await snapshot(page);
    const back = off(s2.cards, s0.at);
    const u2 = untouched(s0, s2, false);
    check(`one-${theme}-close-${X}`, `${X} closed again: every card is back within 0.5 px, nothing saved, Put back offered again`, closedAgain && !back.length && u2.ok, `${back.length} cards off (${back.slice(0, 4).join(', ')}); ${u2.detail}`);
  }
  check(`one-${theme}-moved`, 'the spread really moved cards (the picture slides, not just a flag)', movedAny > 0, `${movedAny} card moves over five looks`);
  if (errors.length) check(`one-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: Expand all spreads every card, and Collapse all puts every card back; the same open cards always draw the same spread ----
if (SECTIONS.has('2')) for (const [theme, keepAll] of [['dark', false], ['light', true]]) {
  const { ctx, page, errors } = await madeRoom(graph, theme, keepAll);
  const s0 = await snapshot(page);
  const ex = await expandAll(page); const s1 = await snapshot(page); const u1 = untouched(s0, s1, true);
  check(`all-${theme}-expand`, `Expand all${keepAll ? ' on a layout the assistant kept' : ''}: nothing saved, stored layout identical (keep marks too), no toast, Put back hidden`, ex && u1.ok, u1.detail);
  const ov = overlaps(s1.cards);
  check(`all-${theme}-nohide`, 'Expand all: no card is drawn over another', ov.length === 0, `${ov.length} pairs: ${ov.slice(0, 5).join(', ')}`);
  const moved = off(s1.cards, s0.at);
  check(`all-${theme}-moved`, 'Expand all: the cards moved apart to make space for the open ones', moved.length > 0, `${moved.length} of ${s1.cards.length} moved`);
  const oc = await openIds(page);
  check(`all-${theme}-open`, 'Expand all: every card is open', oc.length === s1.cards.length, `${oc.length}/${s1.cards.length}`);
  await page.screenshot({ path: join(OUT, `all-${theme}-expand-all.png`) });
  const co = await collapseAll(page); const s2 = await snapshot(page); const u2 = untouched(s0, s2, false); const back = off(s2.cards, s0.at);
  check(`all-${theme}-collapse`, 'Collapse all: every card is back within 0.5 px of where it stood, nothing saved, Put back offered again', co && !back.length && u2.ok, `${back.length} off (${back.slice(0, 4).join(', ')}); ${u2.detail}`);
  for (let i = 0; i < 3; i++) { await expandAll(page); await collapseAll(page); }
  const s3 = await snapshot(page);
  check(`all-${theme}-rounds`, 'three more Expand all and Collapse all rounds: every card back within 0.5 px, nothing saved', !off(s3.cards, s0.at).length && untouched(s0, s3, false).ok, `${off(s3.cards, s0.at).length} off; ${untouched(s0, s3, false).detail}`);
  // Closing the cards one at a time: the last close ends the spread.
  await expandAll(page);
  const names = (await openIds(page));
  let clean = true, why = '';
  for (let i = 0; i < names.length; i++) {
    await toggle(page, names[i], 'Hide'); await wait(250);
    if (i === names.length - 1) await settleAfter(page, 2500);
    else if (i % 6 === 0) { await settleAfter(page, 1200); const s = await snapshot(page); const o = overlaps(s.cards); if (o.length || s.saves !== s0.saves) { clean = false; why = `after closing ${i + 1}: ${o.length} overlaps, ${s.saves - s0.saves} saves`; } }
  }
  const s4 = await snapshot(page);
  check(`all-${theme}-close-each`, 'closing the open cards one by one: no overlap on the way, nothing saved, and the last close puts every card back within 0.5 px', clean && !off(s4.cards, s0.at).length && untouched(s0, s4, false).ok, `${why}; ${off(s4.cards, s0.at).length} off (${off(s4.cards, s0.at).slice(0, 4).join(', ')}); ${untouched(s0, s4, false).detail}`);
  if (errors.length) check(`all-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 3: the same set of open cards always draws the same spread, whatever order they were opened in ----
if (SECTIONS.has('3')) {
  const { ctx, page, errors } = await madeRoom(graph, 'dark');
  const s0 = await snapshot(page);
  const set = ['Inventory', 'Sales', 'Budget', 'Date'];
  for (const X of set) await openOne(page, X);
  const a = await snapshot(page);
  await collapseAll(page);
  for (const X of [...set].reverse()) await openOne(page, X);
  const b = await snapshot(page);
  check('same-order', 'the same four cards opened in the opposite order draw the same spread (within 0.5 px)', !off(b.cards, a.at).length, `${off(b.cards, a.at).length} differ: ${off(b.cards, a.at).slice(0, 5).join(', ')}`);
  // Opening one more, then closing it, comes back to the same spread; nothing is saved on the way.
  await openOne(page, 'Product'); const c = await snapshot(page); await closeOne(page, 'Product'); const d = await snapshot(page);
  check('same-more', 'opening one more card redraws the spread for the new set (still nothing saved, no card over another) and closing it returns to the earlier spread', !overlaps(c.cards).length && !off(d.cards, a.at).length && untouched(s0, d, true).ok, `${overlaps(c.cards).length} overlaps with five open; ${off(d.cards, a.at).length} differ after closing it; ${untouched(s0, d, true).detail}`);
  await collapseAll(page);
  const e = await snapshot(page);
  check('same-back', 'after Collapse all every card is back within 0.5 px of the saved spot', !off(e.cards, s0.at).length && untouched(s0, e, false).ok, `${off(e.cards, s0.at).length} off; ${untouched(s0, e, false).detail}`);
  if (errors.length) check('same', 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 4: zoom, pan and select while spread save nothing, toast nothing and move no card ----
if (SECTIONS.has('4')) {
  const { ctx, page, errors } = await madeRoom(graph, 'dark');
  const s0 = await snapshot(page);
  await expandAll(page);
  const s1 = await snapshot(page);
  const vp = await page.evaluate(() => { const r = document.querySelector('.react-flow__pane').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
  await page.mouse.move(vp.x + vp.w / 2, vp.y + vp.h / 2);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel({ deltaY: -80 }); await wait(120); }
  for (let i = 0; i < 6; i++) { await page.mouse.wheel({ deltaY: 80 }); await wait(120); }
  await wait(500); await quiet(page, 1500);
  const z = await snapshot(page);
  check('spread-zoom', 'zooming in and out while spread: no card moves, nothing saved, no toast', !off(z.cards, s1.at).length && untouched(s0, z, true).ok, `${off(z.cards, s1.at).length} moved; ${untouched(s0, z, true).detail}`);
  const spot = await page.evaluate((vp) => { for (let y = vp.y + vp.h - 30; y > vp.y + 30; y -= 25) for (let x = vp.x + vp.w - 30; x > vp.x + 30; x -= 25) { const el = document.elementFromPoint(x, y); if (el && el.classList.contains('react-flow__pane')) return { x, y }; } return null; }, vp);
  if (spot) { await page.mouse.move(spot.x, spot.y); await page.mouse.down(); for (let i = 1; i <= 10; i++) await page.mouse.move(spot.x - 12 * i, spot.y - 8 * i); await page.mouse.up(); await wait(400); await quiet(page, 1500); }
  const pn = await snapshot(page);
  check('spread-pan', 'panning while spread: no card moves, nothing saved, no toast', !!spot && !off(pn.cards, s1.at).length && untouched(s0, pn, true).ok, `${off(pn.cards, s1.at).length} moved; ${spot ? untouched(s0, pn, true).detail : 'no empty canvas point to drag'}`);
  await clickText(page, 'Fit'); await wait(900);
  const sel = await page.evaluate(() => { const n = document.querySelector('.react-flow__node[data-id="Inventory"]'); const r = n.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + 16 }; });
  await page.mouse.click(sel.x, sel.y); await wait(500); await quiet(page, 1500);
  const selected = await page.evaluate(() => document.querySelector('.react-flow__node[data-id="Inventory"]').classList.contains('selected'));
  const pk = await snapshot(page);
  check('spread-select', 'selecting a card while spread: no card moves, nothing saved, no toast', selected && !off(pk.cards, s1.at).length && untouched(s0, pk, true).ok, `${selected ? '' : 'not selected; '}${off(pk.cards, s1.at).length} moved; ${untouched(s0, pk, true).detail}`);
  await collapseAll(page);
  const s2 = await snapshot(page);
  check('spread-back', 'after Collapse all every card is back within 0.5 px, nothing saved', !off(s2.cards, s0.at).length && untouched(s0, s2, false).ok, `${off(s2.cards, s0.at).length} off; ${untouched(s0, s2, false).detail}`);
  if (errors.length) check('spread-look', 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 5: a drag while spread moves that card's SAVED spot by the distance it was dragged and saves only that card ----
if (SECTIONS.has('5')) for (const [theme, keepAll] of [['dark', true], ['light', false]]) {
  const { ctx, page, errors } = await madeRoom(graph, theme, keepAll);
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory'); await openOne(page, 'Sales'); await openOne(page, 'Budget');
  // Zoom out so there is empty canvas beside the diagram, then drag the Date card (a small one) into it.
  const vp = await page.evaluate(() => { const r = document.querySelector('.react-flow__pane').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
  await page.mouse.move(vp.x + vp.w / 2, vp.y + vp.h / 2);
  for (let i = 0; i < 5; i++) { await page.mouse.wheel({ deltaY: 100 }); await wait(120); }
  // The wheel zoom eases in: wait until the zoom stops changing before reading it or measuring anything.
  for (let last = -1, i = 0; i < 40; i++) { const z = await zoomOf(page); if (Math.abs(z - last) < 1e-6) break; last = z; await wait(250); }
  await quiet(page, 1500);
  const zoom = await zoomOf(page);
  const s1 = await snapshot(page);
  const drag = await page.evaluate(() => {
    const el = document.querySelector('.react-flow__node[data-id="Date"]'); const h = el.querySelector('span.font-semibold').getBoundingClientRect();
    const pane = document.querySelector('.react-flow__pane').getBoundingClientRect();
    const cards = [...document.querySelectorAll('.react-flow__node')].map((n) => n.getBoundingClientRect());
    // The empty spot: the pane's free area that no card covers, the biggest gap to the right or below.
    const pts = [];
    // Well inside the pane: near its edge React Flow pans the canvas while a card is dragged, which moves what is measured.
    for (let y = pane.y + 120; y < pane.bottom - 140; y += 20) for (let x = pane.x + 120; x < pane.right - 260; x += 20) {
      const box = { l: x - 10, t: y - 10, r: x + 130, b: y + 60 };
      if (!cards.some((c) => c.left < box.r && c.right > box.l && c.top < box.b && c.bottom > box.t)) pts.push({ x, y });
    }
    const cx = (pane.x + pane.right) / 2, cy = (pane.y + pane.bottom) / 2;
    pts.sort((a, b) => Math.hypot(a.x - cx, a.y - cy) - Math.hypot(b.x - cx, b.y - cy));
    return { from: { x: h.x + 10, y: h.y + h.height / 2 }, to: pts.length ? pts[0] : null };
  });
  if (!drag.to) { check(`drag-${theme}`, 'an empty spot on the canvas to drop into', false, 'none found'); await ctx.close(); continue; }
  const dx = drag.to.x - drag.from.x, dy = drag.to.y - drag.from.y;
  const dt = () => page.evaluate(() => document.querySelector('.react-flow__node[data-id="Date"]').style.transform);
  const t0 = await dt();
  await page.mouse.move(drag.from.x, drag.from.y); const t1 = await dt(); await page.mouse.down(); const t2 = await dt();
  for (let i = 1; i <= 24; i++) await page.mouse.move(drag.from.x + dx * i / 24, drag.from.y + dy * i / 24);
  // Where the card is at the moment it is let go (React Flow starts a drag a few px late, so this is not the pointer's distance).
  const t3 = await dt();
  const xy = (t) => { const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(t); return { x: Number(m[1]), y: Number(m[2]) }; };
  const raw = xy(t3), start = xy(t2);
  await page.mouse.up(); await settleAfter(page, 1500);
  info(`drag-${theme}`, 'dragged from, let go at (flow px), pointer distance in flow px', `(${start.x}, ${start.y}) to (${raw.x.toFixed(1)}, ${raw.y.toFixed(1)}); pointer ${(dx / zoom).toFixed(1)}, ${(dy / zoom).toFixed(1)}`);
  const s2 = await snapshot(page);
  const newSaves = await page.evaluate((n) => window.__saves.slice(n), s0.saves);
  const entries = newSaves.flatMap((s) => s.tables);
  check(`drag-${theme}-saved-once`, 'the drag saved only the dragged card, once', newSaves.length === 1 && entries.length === 1 && entries[0].name === 'Date', JSON.stringify(newSaves.map((s) => s.tables.map((t) => t.name))));
  const savedDate = s2.stored.find((e) => e.name === 'Date');
  const wantSaved = { x: s0.at.Date.x + raw.x - start.x, y: s0.at.Date.y + raw.y - start.y };
  // Round 10 (L2): the old saved spot plus the distance, moved to the nearest clear spot of the SAVED layout when a card is on it there.
  const onCard = (at) => overlaps(s0.cards.map((c) => (c.id === 'Date' ? { ...c, x: at.x, y: at.y } : c))).filter((q) => q.split('/').includes('Date')).length > 0;
  const closeToWanted = !!savedDate && Math.abs(savedDate.x - wantSaved.x) < 1.5 && Math.abs(savedDate.y - wantSaved.y) < 1.5;
  check(`drag-${theme}-saved-spot`, 'its saved spot is the old saved spot plus the distance it was dragged (within 1.5 px), or a spot clear of every card in the saved layout (the page moved it there, as it does a drop with every card closed)',
    !!savedDate && (closeToWanted || !onCard(savedDate)),
    `saved (${savedDate?.x?.toFixed(1)}, ${savedDate?.y?.toFixed(1)}), wanted (${wantSaved.x.toFixed(1)}, ${wantSaved.y.toFixed(1)}), a card on the wanted spot: ${onCard(wantSaved)}; old saved (${s0.at.Date.x.toFixed(1)}, ${s0.at.Date.y.toFixed(1)}), zoom ${zoom.toFixed(3)}`);
  const others = s2.stored.filter((e) => e.name !== 'Date'), was = s0.stored.filter((e) => e.name !== 'Date');
  check(`drag-${theme}-others-stored`, 'every other card\'s stored spot and keep mark is exactly as before', JSON.stringify(others) === JSON.stringify(was), `${others.filter((e, i) => JSON.stringify(e) !== JSON.stringify(was[i])).length} differ`);
  if (keepAll) check(`drag-${theme}-keep-cleared`, 'the dragged card\'s keep mark is cleared, as any drag does (the rest stay kept)', !savedDate?.keep && others.every((e) => e.keep), `Date keep ${savedDate?.keep}; ${others.filter((e) => !e.keep).length} others lost their mark`);
  else check(`drag-${theme}-keep-cleared`, 'the dragged card has no keep mark', !savedDate?.keep, `Date keep ${savedDate?.keep}`);
  const moved = off(s2.cards, s1.at).filter((id) => id !== 'Date');
  check(`drag-${theme}-others-drawn`, 'every other card keeps its place in the spread (within 0.5 px)', !moved.length, `${moved.length} moved: ${moved.slice(0, 5).join(', ')}`);
  const dd = s2.at.Date;
  const cleared = Math.abs(dd.x - raw.x) > 1.5 || Math.abs(dd.y - raw.y) > 1.5;
  check(`drag-${theme}-drawn`, 'the dragged card is drawn where it was let go, or at the nearest clear spot when that is on a card or out of view, and clear of every card', !overlaps(s2.cards).length, `drawn (${dd.x.toFixed(1)}, ${dd.y.toFixed(1)}), let go (${raw.x.toFixed(1)}, ${raw.y.toFixed(1)}), ${cleared ? 'moved to a clear spot' : 'kept where let go'}; ${overlaps(s2.cards).length} overlaps`);
  await page.screenshot({ path: join(OUT, `drag-${theme}-spread.png`) });
  await clickText(page, 'Collapse all'); await settleAfter(page, 4000);
  const s3 = await snapshot(page);
  // The saved spot may sit on a card in the saved layout (the drag was clear in the spread, not in the saved layout); then the page
  // makes room for it once, now the spread has ended, and only the cards in that save may differ.
  const later = await page.evaluate((n) => window.__saves.slice(n).flatMap((x) => x.tables.map((t) => t.name)), s2.saves);
  const expectAt = { ...s0.at, Date: { ...s0.at.Date, x: savedDate.x, y: savedDate.y } };
  const offAfter = off(s3.cards, expectAt).filter((id) => !later.includes(id));
  check(`drag-${theme}-collapse`, 'Collapse all: the dragged card sits on its new saved spot and every other card is back where it stood before the spread (a card the page then moved for room is the only exception)', !offAfter.length, `${offAfter.length} off: ${offAfter.slice(0, 5).join(', ')}; the make-room moved ${later.length} (${later.slice(0, 6).join(', ')})`);
  if (errors.length) check(`drag-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 5b: a card dragged onto another while spread is drawn clear of it; its saved spot is the nearest clear spot of the saved layout, so the spread's end makes no room (round 10, L2) ----
if (SECTIONS.has('5b')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory');
  await clickText(page, 'Fit'); await wait(900);
  for (let last = -1, i = 0; i < 40; i++) { const z = await zoomOf(page); if (Math.abs(z - last) < 1e-6) break; last = z; await wait(250); }
  const s1 = await snapshot(page);
  const zoom = await zoomOf(page);
  const hd = (id) => page.evaluate((id) => { const h = document.querySelector(`.react-flow__node[data-id="${id}"] span.font-semibold`).getBoundingClientRect(); return { x: h.x + 10, y: h.y + h.height / 2 }; }, id);
  const from = await hd('Date'), to = await hd('Store');
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  for (let i = 1; i <= 24; i++) await page.mouse.move(from.x + (to.x - from.x + 12) * i / 24, from.y + (to.y - from.y + 8) * i / 24);
  await page.mouse.up(); await settleAfter(page, 1500);
  const s2 = await snapshot(page);
  const saves1 = await page.evaluate((n) => window.__saves.slice(n).map((x) => x.tables.map((t) => t.name)), s0.saves);
  check(`dropon-${theme}-saved`, 'while spread the drag saved only the dragged card, once, and offered no toast', saves1.length === 1 && saves1[0].length === 1 && saves1[0][0] === 'Date' && !s2.ui.toast, JSON.stringify(saves1) + ' toast ' + JSON.stringify(s2.ui.toast));
  check(`dropon-${theme}-clear`, 'it is drawn clear of every card (no card over another)', !overlaps(s2.cards).length, `${overlaps(s2.cards).length} overlaps`);
  const dropSaved = s2.stored.find((e) => e.name === 'Date');
  const sized = s0.cards.map((c) => (c.id === 'Date' ? { ...c, x: dropSaved.x, y: dropSaved.y } : c));
  const wasOverlap = overlaps(sized).filter((p) => p.split('/').includes('Date'));
  info(`dropon-${theme}`, 'the dragged card\'s saved spot overlaps in the closed layout', `${wasOverlap.join(', ') || 'no'}; zoom ${zoom.toFixed(3)}`);
  await clickText(page, 'Collapse all');
  let toastSeen = null;
  for (let i = 0; i < 40 && !toastSeen; i++) { await wait(150); toastSeen = (await roomUi(page)).toast; }
  await quiet(page, 4000);
  const s3 = await snapshot(page);
  const roomSaves = await page.evaluate((n) => window.__saves.slice(n).map((x) => x.tables.map((t) => t.name)), s0.saves + 1);
  // Round 10 (L2): the saved spot is already the nearest clear spot of the saved layout, so when the spread ends the page makes no room:
  // no other card moves, nothing more is saved, no toast, no Put back.
  check(`dropon-${theme}-clear-saved`, 'its saved spot sits on no card in the saved layout', wasOverlap.length === 0, `overlaps with ${wasOverlap.join(', ') || 'none'}`);
  check(`dropon-${theme}-room`, 'when the spread ends the page makes no room: no toast, no Put back, nothing more saved', !toastSeen && roomSaves.length === 0 && !s3.ui.putBack, `toast ${JSON.stringify(toastSeen)}; saves ${JSON.stringify(roomSaves)}; Put back ${s3.ui.putBack}`);
  check(`dropon-${theme}-clear-closed`, 'and no card overlaps once the cards are closed', !overlaps(s3.cards).length, `${overlaps(s3.cards).length} overlaps`);
  if (errors.length) check(`dropon-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 6: linking two columns while spread saves nothing; the page's make-room waits and then runs once, with its toast and one Undo ----
if (SECTIONS.has('6')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  await openOne(page, 'Sales'); await openOne(page, 'Notes');
  await clickText(page, 'Fit'); await wait(900);
  // Bring both cards' first column onto the screen at a readable zoom: zoom in on the midpoint between them.
  const rows = () => page.evaluate(() => {
    const row = (id) => { const el = document.querySelector(`.react-flow__node[data-id="${id}"] .sem-colrow`); if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; };
    const pane = document.querySelector('.react-flow__pane').getBoundingClientRect();
    return { a: row('Sales'), b: row('Notes'), pane: { x: pane.x, y: pane.y, r: pane.right, b: pane.bottom } };
  });
  let r = await rows();
  const mid = { x: (r.a.x + r.b.x) / 2, y: (r.a.y + r.b.y) / 2 };
  await page.mouse.move(mid.x, mid.y);
  for (let i = 0; i < 6; i++) {
    r = await rows();
    const inPane = (p) => p.x > r.pane.x + 30 && p.x < r.pane.r - 30 && p.y > r.pane.y + 30 && p.y < r.pane.b - 30;
    if (inPane(r.a) && inPane(r.b) && (await zoomOf(page)) > 0.5) break;
    await page.mouse.move((r.a.x + r.b.x) / 2, (r.a.y + r.b.y) / 2); await page.mouse.wheel({ deltaY: -100 }); await wait(200);
  }
  r = await rows();
  const s1 = await snapshot(page);
  const visible = [r.a, r.b].every((p) => p && p.x > r.pane.x && p.x < r.pane.r && p.y > r.pane.y && p.y < r.pane.b);
  check(`link-${theme}-ready`, 'precondition: both open cards\' first columns are on the screen', visible, JSON.stringify({ a: r.a, b: r.b }));
  if (visible) {
    await page.mouse.move(r.a.x, r.a.y); await page.mouse.down();
    for (let i = 1; i <= 20; i++) await page.mouse.move(r.a.x + (r.b.x - r.a.x) * i / 20, r.a.y + (r.b.y - r.a.y) * i / 20);
    await page.mouse.up(); await wait(1000); await quiet(page, 2500);
    const made = await page.evaluate(() => window.__created.length);
    info(`link-${theme}`, 'the page\'s own toast after the drag', JSON.stringify(await page.evaluate(() => [...document.querySelectorAll('.shadow-lg span')].map((x) => x.textContent))));
    const s2 = await snapshot(page);
    check(`link-${theme}-made`, 'the link was created (the model now has one more relationship and the line is drawn)', made === 1, `${made} created`);
    check(`link-${theme}-while-spread`, 'while spread: the link saved nothing and stored nothing, and no make-room toast, and the Put back stays hidden', s2.saves === s0.saves && s2.st === s0.st && !(s2.ui.toast && s2.ui.toast !== s0.ui.toast) && !s2.ui.putBack, `${s2.saves - s0.saves} saves; stored ${s2.st === s0.st ? 'identical' : 'DIFFERENT'}; toast ${JSON.stringify(s2.ui.toast)}; Put back ${s2.ui.putBack}`);
    check(`link-${theme}-nohide`, 'while spread (after the link): no card is drawn over another', !overlaps(s2.cards).length, `${overlaps(s2.cards).length} overlaps`);
    await page.screenshot({ path: join(OUT, `link-${theme}-spread.png`) });
    await clickText(page, 'Collapse all');
    let toastSeen = null;
    for (let i = 0; i < 40 && !toastSeen; i++) { await wait(150); toastSeen = (await roomUi(page)).toast; }
    await quiet(page, 4000);
    const s3 = await snapshot(page);
    const roomSaves = await page.evaluate((n) => window.__saves.slice(n), s0.saves);
    const moved = off(s3.cards, s0.at);
    // The link needs a bigger box on Sales (one more line leaves it), so the cards below it follow by that growth: the page saves
    // them silently, as it always did (only room the lines need is offered as a Put back). It runs once, when the spread ends.
    check(`link-${theme}-room-once`, 'when the spread ends the page makes room ONCE: one save, and only the cards it moved are in it', roomSaves.length === 1 && roomSaves[0].tables.length === moved.length && roomSaves[0].tables.every((t) => moved.includes(t.name)), `${roomSaves.length} saves of ${roomSaves.map((x) => x.tables.map((t) => t.name).join('+')).join(' | ')}; moved ${moved.join(', ')}; toast ${JSON.stringify(toastSeen)}`);
    const stored = atOf(s3.stored.map((e) => ({ id: e.name, x: e.x, y: e.y })));
    check(`link-${theme}-room-saved`, 'what it saved is where the cards are drawn now (no spread spot was saved)', !off(s3.cards, stored).length, `${off(s3.cards, stored).length} cards drawn off their stored spot`);
    check(`link-${theme}-room-clear`, 'and nothing overlaps once it has run', !overlaps(s3.cards).length, `${overlaps(s3.cards).length} overlaps`);
    info(`link-${theme}`, 'the make-room after the link', `${moved.length} cards moved (${moved.join(', ')}); toast ${JSON.stringify(toastSeen)}`);
  }
  if (errors.length) check(`link-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 7: the assistant's save_layout while spread ends the spread first: what is stored is what it saved (plus the page's make-room for the closed boxes), never a drawn spot ----
if (SECTIONS.has('7')) {
  const stores = {};
  for (const [label, act] of [['closed', async () => {}], ['two-open', async (page) => { await openOne(page, 'Inventory'); await openOne(page, 'Sales'); }], ['all-open', (page) => expandAll(page)]]) {
    const ctx = await browser.createBrowserContext();
    const { page } = await open(ctx, graph, 1440, 900, 'dark');
    const closed = await boxesOf(page);
    const grid = plainGrid(closed, graph, 20, 1440 / 900);
    await act(page);
    const before = await snapshot(page);
    await post(page, grid);
    await wait(800); await quiet(page, 4500);
    const s1 = await snapshot(page);
    const openNow = await openIds(page);
    stores[label] = { s1, open: openNow, before };
    if (label !== 'closed') {
      // Nothing the spread drew was saved: no save entry equals a spread spot that differs from what the assistant or the closed pass would give.
      const spreadOnly = before.cards.filter((c) => Math.abs(c.x - (before.stored.find((e) => e.name === c.id)?.x ?? c.x)) > 0.5 || Math.abs(c.y - (before.stored.find((e) => e.name === c.id)?.y ?? c.y)) > 0.5);
      info(`agent-${label}`, 'cards the spread had moved off their saved spots when the assistant saved', `${spreadOnly.length}`);
      await collapseAll(page);
      const s2 = await snapshot(page);
      const back = off(s2.cards, atOf(s2.stored.map((e) => ({ id: e.name, x: e.x, y: e.y }))));
      check(`agent-${label}-close`, `after Collapse all every card is on its stored spot (the stored layout is what is drawn), ${label}`, !back.length, `${back.length} off: ${back.slice(0, 5).join(', ')}`);
      stores[label].afterClose = s2;
    }
    await ctx.close();
  }
  const ref = stores.closed.s1;
  check('agent-closed-room', 'the assistant\'s save with every card closed still makes room (cards moved, saved, Put back offered)', ref.saves >= 1 && ref.ui.putBack, `${ref.saves} saves, Put back ${ref.ui.putBack}`);
  for (const label of ['two-open', 'all-open']) {
    const l = stores[label];
    const same = JSON.stringify(l.afterClose.stored) === JSON.stringify(stores.closed.s1.stored);
    check(`agent-${label}-stored`, `the assistant's save with ${label === 'all-open' ? 'every card' : 'two cards'} open stores exactly what it stores with every card closed (byte for byte)`, same, same ? '' : `${l.afterClose.stored.filter((e, i) => JSON.stringify(e) !== JSON.stringify(stores.closed.s1.stored[i])).length} entries differ`);
    check(`agent-${label}-spread-back`, `while cards are open after the save the spread is drawn again from the new saved layout (no card over another)`, !overlaps(l.s1.cards).length, `${overlaps(l.s1.cards).length} overlaps; ${l.open.length} open`);
  }
}

// ---- 8: leaving the Diagram and coming back redraws the spread for whatever is still open, and nothing was saved ----
if (SECTIONS.has('8')) {
  const { ctx, page, errors } = await madeRoom(graph, 'dark');
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory'); await openOne(page, 'Sales');
  const spread = await snapshot(page);
  // Leave: click another tab in the Studio's tab strip; come back by clicking Diagram.
  const tabs = await page.evaluate(() => [...document.querySelectorAll('[role="tab"], nav button, header button')].map((b) => (b.textContent || '').trim()).filter(Boolean).slice(0, 40));
  const other = await page.evaluate(() => {
    const els = [...document.querySelectorAll('[role="tab"], nav button, button')].filter((b) => b.getClientRects().length);
    const label = (b) => (b.getAttribute('aria-label') || b.textContent || '').trim();
    const t = els.find((b) => /^(Overview|Plan)$/.test(label(b)));
    if (!t) return null; t.click(); return label(t);
  });
  await wait(1500);
  const gone = await page.evaluate(() => !document.querySelector('.react-flow__node'));
  const clickLabel = (re) => page.evaluate((src) => {
    const re = new RegExp(src);
    const els = [...document.querySelectorAll('[role="tab"], nav button, button')].filter((b) => b.getClientRects().length);
    const t = els.find((b) => re.test((b.getAttribute('aria-label') || b.textContent || '').trim()));
    if (!t) return false; t.click(); return true;
  }, re.source);
  await clickLabel(/^Explore$/); await wait(700);
  const back = await clickLabel(/^Diagram$/);
  await wait(500);
  info('return', 'tabs seen, the one clicked to leave, whether the canvas was gone, whether Diagram was found again', JSON.stringify({ tabs: tabs.slice(0, 12), other, gone, back }));
  if (other && gone && back) {
    await page.waitForSelector('.react-flow__node', { timeout: 60000 }); await wait(1500); await quiet(page, 3000);
    const s1 = await snapshot(page);
    const still = await openIds(page);
    check('return-open', 'the cards that were open are still open', still.includes('Inventory') && still.includes('Sales') && still.length === 2, still.join(', '));
    check('return-spread', 'the spread is drawn again (the same as before leaving, within 0.5 px)', !off(s1.cards, spread.at).length && !overlaps(s1.cards).length, `${off(s1.cards, spread.at).length} differ; ${overlaps(s1.cards).length} overlaps`);
    check('return-saved', 'nothing was saved by leaving or coming back, and the stored layout is identical', s1.saves === s0.saves && s1.st === s0.st, `${s1.saves - s0.saves} saves; stored ${s1.st === s0.st ? 'identical' : 'DIFFERENT'}`);
    await collapseAll(page);
    const s2 = await snapshot(page);
    check('return-close', 'closing them puts every card back within 0.5 px of the saved spot', !off(s2.cards, s0.at).length, `${off(s2.cards, s0.at).length} off`);
  } else check('return', 'the drive found a tab to leave to and the Diagram tab to come back to', false, JSON.stringify({ other, gone, back }));
  if (errors.length) check('return', 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 10: an arrange button and switching diagram end the spread first; nothing the spread drew is saved ----
if (SECTIONS.has('10')) for (const theme of THEMES) {
  const { ctx, page, errors } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  await openOne(page, 'Inventory'); await openOne(page, 'Sales');
  const spread = await snapshot(page);
  check(`arrange-${theme}-precondition`, 'precondition: two cards open and spread', overlaps(spread.cards).length === 0 && off(spread.cards, s0.at).length > 0, `${off(spread.cards, s0.at).length} cards moved by the spread`);
  await clickText(page, 'Vertical'); await wait(1000); await quiet(page, 4500);
  const a1 = await snapshot(page);
  const entries = a1.stored.length;
  check(`arrange-${theme}-saved`, 'Vertical while spread saved the arranged layout (every table stored)', a1.saves > s0.saves && entries >= graph.tables.length, `${a1.saves - s0.saves} saves; ${entries} stored entries`);
  check(`arrange-${theme}-spread`, 'the cards still open are spread again on the new layout: no card over another, two open', overlaps(a1.cards).length === 0 && (await openIds(page)).length === 2, `${overlaps(a1.cards).length} overlaps`);
  await collapseAll(page);
  const a2 = await snapshot(page);
  const stored = atOf(a2.stored.map((e) => ({ id: e.name, x: e.x, y: e.y })));
  const back = off(a2.cards, stored);
  check(`arrange-${theme}-close`, 'after Collapse all every card is on its stored spot: what the arrange stored, never a spread spot', !back.length, `${back.length} off: ${back.slice(0, 5).join(', ')}`);
  // Control: the same arrange with every card closed stores the same layout for the closed boxes when none was open at the time.
  // Switch diagram: open cards, make a new diagram and come back: the spread is drawn again, nothing saved.
  await openOne(page, 'Inventory'); await openOne(page, 'Sales');
  const b0 = await snapshot(page);
  await page.evaluate(() => document.querySelector('button[aria-label="New diagram"]').click()); await wait(1200);
  const inNew = await page.evaluate(() => document.querySelectorAll('.react-flow__node').length);
  await page.keyboard.press('Escape'); await wait(300);
  await page.evaluate(() => { const sel = document.querySelector('select[title="Switch diagram"]'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; set.call(sel, 'all'); sel.dispatchEvent(new Event('change', { bubbles: true })); });
  await wait(1500); await quiet(page, 3500);
  const b1 = await snapshot(page);
  check(`switch-${theme}`, 'switching to a new diagram and back: the spread is drawn again (same as before, 0.5 px), nothing saved', inNew === 0 && !off(b1.cards, b0.at).length && b1.saves === b0.saves && b1.st === b0.st, `${inNew} cards on the new diagram; ${off(b1.cards, b0.at).length} differ; ${b1.saves - b0.saves} saves; stored ${b1.st === b0.st ? 'identical' : 'DIFFERENT'}`);
  await collapseAll(page);
  const b2 = await snapshot(page);
  check(`switch-${theme}-close`, 'closing them puts every card back on its stored spot', !off(b2.cards, atOf(b2.stored.map((e) => ({ id: e.name, x: e.x, y: e.y })))).length, `${off(b2.cards, atOf(b2.stored.map((e) => ({ id: e.name, x: e.x, y: e.y })))).length} off`);
  if (errors.length) check(`arrange-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 9: the routing time after Expand all at 300 tables (reported): the click to the last held line landing ----
if (SECTIONS.has('9')) {
  const g300 = big(30, 226, 44, 9, 1);
  const times = [];
  for (let run = 0; run < Number(process.env.TIMING_RUNS || 2); run++) {
    const ctx = await browser.createBrowserContext();
    const { page, errors } = await open(ctx, g300, 1440, 900, 'dark', [], 4000);
    const s0 = await snapshot(page);
    const t0 = Date.now();
    await clickText(page, 'Expand all');
    let landed = null;
    for (;;) {
      const st = await page.evaluate(() => ({ busy: !!document.querySelector('[data-making-room]'), held: document.querySelectorAll('.react-flow__edge g[style*="opacity: 0.4"]').length, open: document.querySelectorAll('button[aria-label^="Hide the columns of "]').length }));
      if (st.open > 0 && !st.busy && !st.held) { if (landed === null) landed = Date.now(); if (Date.now() - landed > 1500) break; } else landed = null;
      if (Date.now() - t0 > 120000) break;
      await wait(100);
    }
    const ms = landed === null ? -1 : landed - t0;
    times.push(ms);
    const s1 = await snapshot(page);
    info(`timing-${run + 1}`, 'Expand all at 300 tables: milliseconds from the click until the routing pass has landed (nothing held, nothing making room)', `${ms} ms; ${s1.saves - s0.saves} saves; ${errors.length} page errors`);
    if (run === 0) check('timing-saves', 'Expand all at 300 tables saved nothing', s1.saves === s0.saves && s1.st === s0.st, `${s1.saves - s0.saves} saves`);
    await ctx.close();
  }
  info('timing', 'Expand all at 300 tables, routing time in ms over the runs (73671d98: 3500 to 4200; round 8: 6500 to 6800)', JSON.stringify(times));
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
