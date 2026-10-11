// verify: driven proof from round 7 of the m11/diagram-route verification (29 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness with a stand-in for the
// engine's layout sidecar (saveLayout merges the way LocalEngine.SaveLayoutAsync does: keep is sent, not merged), and
// reads the live canvas. The bar is the coordinator's round 7 ruling, A PUT BACK STAYS PUT: after the person's Put back
// the page moves nothing, "the page only makes room again when the layout changes (a new save by the assistant, a drag,
// new tables)". A look at one card's columns, opened and closed again, is not one of those, and it leaves the layout as
// it was. Yet:
//
//   1. After the person's Put back (the 30-table model, the assistant's plain grid 20 px apart, 1440 x 900), a fresh page
//      on the stored layout moves nothing, as it should. The person opens one card that has put-back cards below it and
//      closes it again. The cards below move down by its growth and back up again (right, Kane 25 Sep), but the page
//      saves the cards that growth moved WITHOUT their keep mark (diagram.tsx land -> persistPositions(changedPositions
//      (from, next)), keep false). Once the card is closed they are back where they were put back, no longer kept, and
//      closer to their neighbours than their lines need, so the settle pass moves them apart again, saves them and offers
//      the toast ("Moved 3 tables so the lines have room.") and a second Put back. On this laptop every card tried did it
//      (8 of 8 in the 30-table grid, 1 to 4 put-back cards moved each time; 6 of 6 in the 80-table grid). It is older
//      than round 7: the round 6 head does the same (3 of 4), and the round 5 head too (4 of 4).
//   2. The same for the assistant's own keep: save_layout with keep=true on every table, which its description says keeps
//      "that exact spot". The person opens and closes one card: the tables below it lose keep and the page moves them
//      (4 of 4), so the assistant's next get_layout shows them moved and not kept.
//   3. After the Put back, Expand all and then Collapse all (every card opened and closed at once): the page moves the
//      put-back cards again.
// At c5251892 all 8 checks fail. A scratch change (not in the lane) that saves a kept card moved by a growth WITH its keep
// mark (in land, the moved cards that are kept go through persistPositions(..., true)) makes all 8 pass: nothing moves,
// no toast, no mark lost.
//   node diagram-route-verify-r7-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r7');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3').split(','));
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

// ---- fixtures (generic names), the same generator as the round 2 to 6 drives ----
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
const columnsFor = (g) => g.tables.flatMap((t) => Array.from({ length: t.columns }, (_, i) => {
  const name = i === 0 && t.keyColumns.length ? t.keyColumns[0] : t.name.replace(/ /g, '') + 'Field' + (i + 1);
  return { ref: 'column:' + t.name + '/' + name, name, table: t.name, dataType: i % 3 === 0 ? 'Int64' : 'String', displayFolder: '', formatString: '', summarizeBy: 'None', dataCategory: '', isKey: i === 0 && t.keyColumns.length > 0, isHidden: false, isCalculated: false, hasDescription: false, description: '', expression: '' };
}));
const ALL_BUSMATRIX = { diagrams: [{ id: 'all', name: 'All tables', all: true, tables: [], positions: {}, layout: 'busmatrix' }], activeDiagramId: 'all' };

async function open(ctx, graph, w, h, theme, store = []) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg, store) => {
    window.__UISHOT__ = cfg;
    window.__saves = []; window.__store = store;
    cfg.getLayout = () => ({ tables: JSON.parse(JSON.stringify(window.__store)) });
    cfg.saveLayout = (params) => {
      const t = (params && params[0]) || [];
      window.__saves.push({ t: performance.now(), tables: JSON.parse(JSON.stringify(t)) });
      for (const n of t) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
      return { saved: true, count: t.length };
    };
  }, { graph, columns: columnsFor(graph), state: ALL_BUSMATRIX }, store);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 300000 });
  await page.waitForSelector('.react-flow__node', { timeout: 300000 });
  await applyTheme(page, theme);
  await quiet(page);
  return { page, errors };
}
// Until the page has not been making room, held a line or saved anything for `q` ms (making room and routing run a few
// ms at a time, review round 5).
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
const cardsOf = (page) => page.evaluate(() => [...document.querySelectorAll('.react-flow__node')].map((el) => {
  const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform);
  return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]) };
}));
const sizesOf = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  return [...document.querySelectorAll('.react-flow__node')].map((el) => { const r = el.getBoundingClientRect(); return { id: el.getAttribute('data-id'), w: r.width / zoom, h: r.height / zoom }; });
});
const roomUi = (page) => page.evaluate(() => ({
  toast: [...document.querySelectorAll('span')].map((s) => s.textContent || '').find((t) => /lines have room/.test(t)) ?? null,
  putBack: [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Put back' && b.getClientRects().length),
}));
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const toggle = (page, id, how) => page.evaluate((id, how) => { const b = document.querySelector(`button[aria-label="${how} the columns of ${id}"]`); if (b) b.click(); return !!b; }, id, how);
// The assistant's save_layout: the engine merges it into the sidecar and broadcasts every stored entry.
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
}, tables);
// The assistant's plain grid (the round 4 drive's), in whole px as an assistant saves it.
function plainGrid(sizes, graph, gap, aspect) {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = sizes.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...sizes.map((c) => c.w)), ch = Math.max(...sizes.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: Math.round((i % cols) * (cw + gap)), y: Math.round(Math.floor(i / cols) * (ch + gap)) }));
}
const off = (cards, at) => cards.filter((c) => Math.abs(c.x - at[c.id].x) > 0.5 || Math.abs(c.y - at[c.id].y) > 0.5).map((c) => c.id);

// The stored layout after the assistant's grid: either the person's Put back of what the page moved, or (keepAll) the
// assistant's save_layout with keep=true on every table.
async function keptLayout(graph, theme, keepAll) {
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, graph, 1440, 900, theme);
  const grid = plainGrid(await sizesOf(page), graph, 20, 1440 / 900);
  await post(page, keepAll ? grid.map((t) => ({ ...t, keep: true })) : grid);
  await wait(800); await quiet(page, 4500);
  const offered = keepAll ? null : await clickText(page, 'Put back');
  await wait(300); await quiet(page);
  const store = await page.evaluate(() => window.__store), cards = await cardsOf(page);
  await ctx.close();
  return { grid, at: Object.fromEntries(grid.map((t) => [t.name, t])), store, cards, offered };
}
// A fresh page on that stored layout, then `act`; afterwards every card must be where it was kept, nothing offered, and
// every keep mark still stored.
async function afterLook(id, graph, theme, L, label, act) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme, JSON.parse(JSON.stringify(L.store)));
  const before = off(await cardsOf(page), L.at), kept = L.store.filter((e) => e.keep).map((e) => e.name);
  const did = await act(page);
  const cards = await cardsOf(page), ui = await roomUi(page), st = await page.evaluate(() => window.__store);
  const moved = off(cards, L.at), lost = kept.filter((n) => !st.find((e) => e.name === n)?.keep);
  check(id, `${label}: every card stays where it was kept, nothing is offered and every keep mark stays`, did && !before.length && !moved.length && !ui.toast && !ui.putBack && !lost.length,
    `${did ? '' : 'could not do it; '}${before.length} off before the look; ${moved.length} moved after it (${moved.slice(0, 4).join(', ')}); toast ${JSON.stringify(ui.toast)}; Put back shown ${ui.putBack}; ${lost.length} keep marks lost (${lost.slice(0, 4).join(', ')})`);
  if (moved.length) await page.screenshot({ path: join(OUT, `${id}.png`) });
  if (errors.length) check(id, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}
const lookAt = (X) => async (page) => {
  if (!(await toggle(page, X, 'Show'))) return false;
  await wait(600); await quiet(page);
  if (!(await toggle(page, X, 'Hide'))) return false;
  await wait(600); await quiet(page);
  return true;
};
// A card with a kept card straight below it in its column (its growth moves that card).
const withKeptBelow = (L, sizes) => {
  const sz = new Map(sizes.map((s) => [s.id, s])), kept = new Set(L.store.filter((e) => e.keep).map((e) => e.name));
  return L.grid.filter((c) => L.grid.some((d) => d.name !== c.name && kept.has(d.name) && d.y > c.y + sz.get(c.name).h - 1 && d.x < c.x + sz.get(c.name).w && c.x < d.x + sz.get(d.name).w)).map((c) => c.name);
};

const graph = thirty();
let sizes = null;
{
  const ctx = await browser.createBrowserContext();
  const { page } = await open(ctx, graph, 1440, 900, 'dark');
  sizes = await sizesOf(page);
  await ctx.close();
}
// ---- 1: after the person's Put back, a look at one card's columns ----
if (SECTIONS.has('1') || SECTIONS.has('3')) {
  const L = await keptLayout(graph, 'dark', false);
  info('thirty-put-back', 'the Put back', `offered ${L.offered}; ${L.store.filter((e) => e.keep).length} of ${L.store.length} tables kept; ${off(L.cards, L.at).length} off the assistant's spots`);
  if (SECTIONS.has('1')) for (const X of withKeptBelow(L, sizes).slice(0, 4)) await afterLook(`thirty-put-back-look-${X.replace(/ /g, '')}-dark`, graph, 'dark', L, `after the Put back, ${X} opened and closed`, lookAt(X));
  // ---- 3: after the Put back, Expand all then Collapse all ----
  if (SECTIONS.has('3')) for (const theme of ['dark', 'light']) await afterLook(`thirty-put-back-expand-collapse-${theme}`, graph, theme, L, 'after the Put back, Expand all then Collapse all', async (page) => {
    if (!(await clickText(page, 'Expand all'))) return false;
    await wait(800); await quiet(page);
    if (!(await clickText(page, 'Collapse all'))) return false;
    await wait(800); await quiet(page);
    return true;
  });
}
// ---- 2: the assistant kept every table (save_layout keep=true), a look at one card's columns ----
if (SECTIONS.has('2')) {
  const L = await keptLayout(graph, 'light', true);
  info('thirty-assistant-kept', 'the assistant kept every table', `${L.store.filter((e) => e.keep).length} of ${L.store.length} tables kept; ${off(L.cards, L.at).length} off its spots`);
  for (const X of withKeptBelow(L, sizes).slice(0, 2)) await afterLook(`thirty-assistant-kept-look-${X.replace(/ /g, '')}-light`, graph, 'light', L, `after the assistant kept every table, ${X} opened and closed`, lookAt(X));
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
