// Driven proof for the coordinator's round 8 ruling (29 Sep), restated for Kane's round 9 ruling ("Slide down"): LOOKING IS NOT A
// LAYOUT CHANGE. Opening or closing a card's columns (one card, Expand all, Collapse all), zooming, panning and selecting never
// save, never toast and never change the stored layout (get_layout byte for byte, keep marks included). Round 9: while a card is
// open the page DRAWS a spread on screen only (the cards below it slide down, nothing is hidden under an open card), and closing
// the last one puts every card back within 0.5 px. The page makes room only when the layout itself changes (the assistant saves a
// layout, a card is dragged or dropped, tables are added), and it makes room for the cards' closed boxes, so the stored layout is
// the same whichever cards happen to be open. Keep marks (the person's Put back, the assistant's keep=true) hold through any
// amount of looking. The spread itself is proved in diagram-route-r9-drive.mjs.
// Serves the built bundle, opens the real harness with a stand-in for the engine's layout sidecar (saveLayout merges the
// way LocalEngine.SaveLayoutAsync does), and reads the live canvas.
//   node diagram-route-r8-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4,5; WEBROOT=<checkout>)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r8');
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

const sameAt = (cards, at) => off(cards, at);
const snapshot = async (page) => {
  const cards = await cardsOf(page), ui = await roomUi(page), st = await page.evaluate(() => window.__store), saves = await page.evaluate(() => window.__saves.length);
  return { cards, at: Object.fromEntries(cards.map((c) => [c.id, c])), ui, st, saves };
};
// Compares a later look at the page with a snapshot: no card moved, nothing saved, no new toast, no new Put back.
// `spread`: a card is still open, so the cards are DRAWN spread (they may differ from the snapshot) and the Put back is hidden.
function unchanged(before, after, spread = false) {
  const moved = spread ? [] : sameAt(after.cards, before.at);
  const newToast = after.ui.toast && after.ui.toast !== before.ui.toast;
  const kept = before.st.filter((e) => e.keep).map((e) => e.name), lost = kept.filter((n) => !after.st.find((e) => e.name === n)?.keep);
  const sameStore = JSON.stringify(after.st) === JSON.stringify(before.st);
  const putBackOk = spread ? !after.ui.putBack : after.ui.putBack === before.ui.putBack;
  return { ok: !moved.length && after.saves === before.saves && sameStore && !newToast && putBackOk && !lost.length,
    detail: `${moved.length} moved (${moved.slice(0, 4).join(', ')}); ${after.saves - before.saves} saves; stored layout ${sameStore ? 'identical' : 'DIFFERENT'}; toast ${JSON.stringify(after.ui.toast)}; Put back ${after.ui.putBack}; ${lost.length} keep marks lost` };
}
const expandAll = async (page) => { const ok = await clickText(page, 'Expand all'); await wait(800); await quiet(page, 1800); return ok; };
const collapseAll = async (page) => { const ok = await clickText(page, 'Collapse all'); await wait(800); await quiet(page, 1800); return ok; };
const openOne = async (page, X) => { const ok = await toggle(page, X, 'Show'); await wait(700); await quiet(page, 1800); return ok; };
const closeOne = async (page, X) => { const ok = await toggle(page, X, 'Hide'); await wait(700); await quiet(page, 1800); return ok; };

// The page after the assistant's plain grid 20 px apart: the page made room (moved cards, saved them, offered the toast).
async function madeRoom(graph, theme, keepAll = false, store = []) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme, store);
  const closed = await sizesOf(page);
  const grid = plainGrid(closed, graph, 20, 1440 / 900);
  await post(page, keepAll ? grid.map((t) => ({ ...t, keep: true })) : grid);
  await wait(800); await quiet(page, 4500);
  return { ctx, page, errors, grid, closed };
}

const graph = thirty();
// ---- 1: after the page made room, a look never moves a card, saves or toasts ----
if (SECTIONS.has('1')) for (const theme of ['dark', 'light']) {
  const { ctx, page, errors, grid } = await madeRoom(graph, theme);
  const s0 = await snapshot(page);
  check(`room-${theme}`, 'precondition: the page made room for the assistant\'s grid (moved cards, saved them, offered the Put back)', off(s0.cards, Object.fromEntries(grid.map((t) => [t.name, t]))).length > 0 && s0.saves > 0 && s0.ui.putBack, `${off(s0.cards, Object.fromEntries(grid.map((t) => [t.name, t]))).length} moved, ${s0.saves} saves, Put back shown ${s0.ui.putBack}`);
  for (const X of ['Inventory', 'Sales', 'Budget', 'Date']) {
    const opened = await openOne(page, X); const s1 = await snapshot(page);
    const u1 = unchanged(s0, s1, true);
    check(`room-${theme}-open-${X}`, `${X} opened: nothing saved, stored layout identical, no new toast, Put back hidden`, opened && u1.ok, u1.detail);
    if (!u1.ok) await page.screenshot({ path: join(OUT, `room-${theme}-open-${X}.png`) });
    const closedAgain = await closeOne(page, X); const s2 = await snapshot(page);
    const u2 = unchanged(s0, s2);
    check(`room-${theme}-close-${X}`, `${X} closed again: every card is back within 0.5 px, nothing saved, no new toast, Put back offered again`, closedAgain && u2.ok, u2.detail);
  }
  const ex = await expandAll(page); const s3 = await snapshot(page); const u3 = unchanged(s0, s3, true);
  check(`room-${theme}-expand-all`, 'Expand all: 0 saves, 0 toasts, stored layout identical (0 keep marks lost), Put back hidden', ex && u3.ok, u3.detail);
  await page.screenshot({ path: join(OUT, `room-${theme}-expand-all.png`) });
  const co = await collapseAll(page); const s4 = await snapshot(page); const u4 = unchanged(s0, s4);
  check(`room-${theme}-collapse-all`, 'Collapse all: every card is back within 0.5 px of where it stood before Expand all, nothing saved, no new toast', co && u4.ok, u4.detail);
  for (let i = 0; i < 3; i++) { await expandAll(page); await collapseAll(page); }
  const u5 = unchanged(s0, await snapshot(page));
  check(`room-${theme}-many-looks`, 'three more Expand all and Collapse all rounds: every card back within 0.5 px, nothing saved', u5.ok, u5.detail);
  if (errors.length) check(`room-${theme}`, 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 2: zoom, pan and select never move a card, save or toast ----
if (SECTIONS.has('2')) {
  const { ctx, page, errors, grid } = await madeRoom(graph, 'dark');
  const s0 = await snapshot(page);
  const vp = await page.evaluate(() => { const r = document.querySelector('.react-flow__pane').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
  await page.mouse.move(vp.x + vp.w / 2, vp.y + vp.h / 2);
  for (let i = 0; i < 4; i++) { await page.mouse.wheel({ deltaY: -80 }); await wait(120); }
  for (let i = 0; i < 6; i++) { await page.mouse.wheel({ deltaY: 80 }); await wait(120); }
  await wait(500); await quiet(page, 1500);
  const zoomed = unchanged(s0, await snapshot(page));
  check('look-zoom', 'zooming in and out moves no card, saves nothing, shows no new toast', zoomed.ok, zoomed.detail);
  // Pan by dragging the empty canvas: find a point whose top element is the pane.
  const spot = await page.evaluate((vp) => { for (let y = vp.y + vp.h - 30; y > vp.y + 30; y -= 25) for (let x = vp.x + vp.w - 30; x > vp.x + 30; x -= 25) { const el = document.elementFromPoint(x, y); if (el && el.classList.contains('react-flow__pane')) return { x, y }; } return null; }, vp);
  if (spot) { await page.mouse.move(spot.x, spot.y); await page.mouse.down(); for (let i = 1; i <= 10; i++) await page.mouse.move(spot.x - 12 * i, spot.y - 8 * i); await page.mouse.up(); await wait(400); await quiet(page, 1500); }
  const panned = unchanged(s0, await snapshot(page));
  check('look-pan', 'panning the canvas moves no card, saves nothing, shows no new toast', !!spot && panned.ok, spot ? panned.detail : 'no empty canvas point to drag');
  // Select: click a card's name without moving the pointer.
  const sel = await page.evaluate(() => { const n = document.querySelector('.react-flow__node[data-id="Inventory"]'); const r = n.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + 16 }; });
  await page.mouse.click(sel.x, sel.y); await wait(500); await quiet(page, 1500);
  const selected = await page.evaluate(() => document.querySelector('.react-flow__node[data-id="Inventory"]').classList.contains('selected'));
  const picked = unchanged(s0, await snapshot(page));
  check('look-select', 'selecting a card moves no card, saves nothing, shows no new toast', selected && picked.ok, `${selected ? '' : 'not selected; '}${picked.detail}`);
  if (errors.length) check('look', 'no page errors', false, errors.join(' | '));
  await ctx.close();
}

// ---- 3: keep marks hold through any amount of looking ----
if (SECTIONS.has('3')) for (const [label, keepAll] of [['put-back', false], ['assistant-keep', true]]) {
  const { ctx, page, grid } = await madeRoom(graph, keepAll ? 'light' : 'dark', keepAll);
  if (!keepAll) await clickText(page, 'Put back');
  await wait(400); await quiet(page, 4000);
  const s0 = await snapshot(page), kept = s0.st.filter((e) => e.keep).length;
  info(`keep-${label}`, 'the kept layout', `${kept} of ${s0.st.length} tables kept, ${s0.saves} saves so far`);
  check(`keep-${label}-precondition`, 'the kept layout is where the assistant put the cards', off(s0.cards, Object.fromEntries(grid.map((t) => [t.name, t]))).length === 0 || !keepAll, `${off(s0.cards, Object.fromEntries(grid.map((t) => [t.name, t]))).length} off`);
  for (const X of ['Budget', 'Inventory', 'Returns', 'Sales']) { await openOne(page, X); await closeOne(page, X); }
  for (let i = 0; i < 2; i++) { await expandAll(page); await collapseAll(page); }
  await openOne(page, 'Inventory'); await openOne(page, 'Sales');   // and leave two open
  const u = unchanged(s0, await snapshot(page), true);
  check(`keep-${label}-looks`, `after ${label}, four single looks, two Expand all / Collapse all rounds and two cards left open: nothing saved, the stored layout identical, every keep mark stays`, u.ok, u.detail);
  await ctx.close();
  // A fresh page on the stored layout, then the same looks.
  const ctx2 = await browser.createBrowserContext();
  const { page: p2 } = await open(ctx2, graph, 1440, 900, keepAll ? 'light' : 'dark', JSON.parse(JSON.stringify(s0.st)));
  const f0 = await snapshot(p2);
  check(`keep-${label}-fresh`, 'a fresh page on that layout moves nothing and saves nothing', !off(f0.cards, s0.at).length && f0.saves === 0 && !f0.ui.toast, `${off(f0.cards, s0.at).length} moved, ${f0.saves} saves, toast ${JSON.stringify(f0.ui.toast)}`);
  await expandAll(p2); await collapseAll(p2); await openOne(p2, 'Budget'); await closeOne(p2, 'Budget');
  const fu = unchanged(f0, await snapshot(p2));
  check(`keep-${label}-fresh-looks`, 'and looking there changes nothing either', fu.ok, fu.detail);
  await ctx2.close();
}

// ---- 4: nothing is hidden under an opened card (round 9: the cards slide apart), and it lifts off the canvas with its shadow ----
if (SECTIONS.has('4')) for (const theme of ['dark', 'light']) {
  const { ctx, page } = await madeRoom(graph, theme);
  await expandAll(page);
  const facts = await page.evaluate(() => {
    const nodes = [...document.querySelectorAll('.react-flow__node')].map((el) => ({ el, id: el.getAttribute('data-id'), r: el.getBoundingClientRect(), z: Number(getComputedStyle(el).zIndex) || 0 }));
    const pairs = [];
    for (const a of nodes) for (const b of nodes) if (a !== b && Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left) > 8 && Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top) > 8) pairs.push([a, b]);
    const shadowOf = (el) => getComputedStyle(el.querySelector('div') || el).boxShadow;
    return { count: nodes.length, overlapping: pairs.length, shadows: nodes.slice(0, 3).map((n) => shadowOf(n.el)) };
  });
  info(`ontop-${theme}`, 'every card open', `${facts.count} cards, ${facts.overlapping} overlapping pairs`);
  // Open one card on its own and look at what it covers.
  await collapseAll(page);
  let proved = 0, tried = 0, bad = [];
  for (const X of ['Inventory', 'Sales', 'Budget', 'Returns', 'Customer', 'Product']) {
    await openOne(page, X);
    const r = await page.evaluate((X) => {
      const nodes = [...document.querySelectorAll('.react-flow__node')].map((el) => ({ el, id: el.getAttribute('data-id'), r: el.getBoundingClientRect() }));
      const me = nodes.find((n) => n.id === X); const covered = nodes.filter((n) => n !== me && Math.min(n.r.right, me.r.right) - Math.max(n.r.left, me.r.left) > 12 && Math.min(n.r.bottom, me.r.bottom) - Math.max(n.r.top, me.r.top) > 12);
      const zOf = (n) => Number(getComputedStyle(n.el).zIndex) || 0;
      const out = covered.map((n) => {
        const cx = (Math.max(n.r.left, me.r.left) + Math.min(n.r.right, me.r.right)) / 2, cy = (Math.max(n.r.top, me.r.top) + Math.min(n.r.bottom, me.r.bottom)) / 2;
        const top = document.elementFromPoint(cx, cy)?.closest('.react-flow__node')?.getAttribute('data-id');
        return { id: n.id, above: zOf(me) > zOf(n), topmost: top };
      });
      const card = me.el.firstElementChild; return { covered: out, shadow: card ? getComputedStyle(card).boxShadow : '', z: zOf(me) };
    }, X);
    { tried++; const ok = r.covered.length === 0 && r.shadow && r.shadow !== 'none'; if (ok) proved++; else bad.push(`${X} covers ${r.covered.map((c) => `${c.id}(above ${c.above}, top ${c.topmost})`).join(', ') || 'nothing'}, shadow ${r.shadow}`); }
    await closeOne(page, X);
  }
  check(`ontop-${theme}`, 'an opened card covers no neighbour (they slid apart) and has its shadow', tried > 0 && proved === tried, `${proved} of ${tried} opened cards covered nothing and had a shadow; ${bad.join(' | ')}`);
  await openOne(page, 'Inventory');
  await page.screenshot({ path: join(OUT, `ontop-${theme}.png`) });
  await ctx.close();
}

// ---- 5: the page makes room only when the layout changes, and for the closed boxes: the same layout whichever cards are open ----
if (SECTIONS.has('5')) {
  const layouts = {};
  for (const [label, act] of [['closed', async () => {}], ['inventory-open', (page) => openOne(page, 'Inventory')], ['all-open', (page) => expandAll(page)]]) {
    const ctx = await browser.createBrowserContext();
    const { page } = await open(ctx, graph, 1440, 900, 'dark');
    const closed = await sizesOf(page);
    const grid = plainGrid(closed, graph, 20, 1440 / 900);
    await act(page);
    const s0 = await snapshot(page);
    await post(page, grid);
    await wait(800); await quiet(page, 4500);
    const s1 = await snapshot(page);
    layouts[label] = { s1, moved: off(s1.cards, s0.at), saves: s1.saves - s0.saves };
    await ctx.close();
  }
  const ref = layouts.closed;
  check('layout-change-room', 'the assistant\'s save with every card closed still makes room (cards moved, saved, Put back offered)', ref.moved.length > 0 && ref.saves >= 1 && ref.s1.ui.putBack, `${ref.moved.length} moved, ${ref.saves} saves, Put back shown ${ref.s1.ui.putBack}`);
  for (const label of ['inventory-open', 'all-open']) {
    const l = layouts[label], diff = ref.s1.st.filter((e, i) => JSON.stringify(e) !== JSON.stringify(l.s1.st[i])).length + Math.abs(ref.s1.st.length - l.s1.st.length);
    check(`layout-change-${label}`, `the same save with ${label === 'all-open' ? 'every card' : 'Inventory'} open makes room for the closed boxes: the same stored layout as with every card closed`, diff === 0 && l.saves >= 1, `${diff} stored entries differ from the all-closed layout; ${l.saves} saves; toast ${JSON.stringify(l.s1.ui.toast)}`);
  }
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
