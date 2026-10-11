// Round 11 of m11/diagram-route (30 Sep): every event that ENDS a spread, raced against a spread that is still being worked out.
// Needs the built bundle (npm run build:webview). Serves it, opens the real harness with a stand-in for the engine's layout
// sidecar (saveLayout merges the way LocalEngine.SaveLayoutAsync does), and reads the live canvas.
// Why it exists: rounds 9 and 10 each fixed one way a spread that is still being worked out outlives the open cards, and the
// next round found another (R9-1 Collapse all; R10-1 a model reload). The saved spots must never live only inside the spread's
// state, so this drive races EVERY spread-ending event against the work instead of one at a time:
//   close        the person closes every open card
//   collapse     Collapse all
//   reload-gone  the assistant deletes the tables the person has open (the model reloads and no card is open)
//   reload-named the assistant renames them (the model reloads; the names the cards were open under are gone)
//   switch       a switch of diagram (New, then back)
//   save         the assistant's save_layout
//   arrange      an arrange (Bus matrix)
// Each is fired at slice K of the spread's work, K from the second slice (the first runs inside the page's own effect, where no click can land) to the last. The test page (not the product) counts the
// slices (runInSlices posts 0 on its own MessageChannel before each further slice) and runs the event inside that hook, so it
// lands between two slices every time; a reload's answer is held by the harness's own hold/release seam and handed over the
// same way. The spread's slices are cut by a clock (SLICE_MS of performance.now), so a busy machine changes how many there are:
// the test page adds a fixed step to performance.now on every call, and inside a slice lets no real time in, so a slice is a
// fixed number of steps and N is the same on every run (27 at 80 tables, 93 at 300).
// After the event the drive closes any card still open (Collapse all) and asks, at 80 and 300 tables:
//   0 cards drawn off their stored spot (0.5 px), 0 off where the same event leaves them with every card closed,
//   get_layout (the stored layout, keep marks included) byte for byte equal to that same event with every card closed,
//   and for the events that write nothing (close, collapse, switch) 0 saves.
// For an arrange the control is the same arrange with the spread settled (an arrange lays out the open cards' real boxes).
//   node diagram-route-r11-race-drive.mjs      (OUTDIR=<dir>; THEMES=dark,light; SIZES=80,300; EVENTS=close,collapse,...; WEBROOT=<checkout>; DET=0; HUBS=1)
// The race is proved able to fail at both sizes (the join, 30 Sep): with draw's guard made to empty the holder (round 10's bug),
// reload-gone and reload-named go red at the last slice, K 27 of 27 at 80 tables and K 93 of 93 at 300, and pass without it.
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-r11-race');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark,light').split(',');
const SIZES = (process.env.SIZES || '80,300').split(',').map(Number);
const ALL_EVENTS = ['close', 'collapse', 'reload-gone', 'reload-named', 'switch', 'save', 'arrange'];
const EVENTS = (process.env.EVENTS || ALL_EVENTS.join(',')).split(',').filter((e) => ALL_EVENTS.includes(e));   // EVENTS=none only measures the slice count
const MIX = process.env.MIX === '1';
const GAP = Number(process.env.GAP || 40), OPEN = Number(process.env.OPEN || 0);   // the assistant's star grid gap, and how many lookup cards to open (0: the size's default)
const PASSES = Number(process.env.PASSES || 0);   // 0: 2 passes of the sweep at 80 tables, 1 at 300
const STEP = Number(process.env.STEP || 1);   // ms added to performance.now on each call, so a 12 ms slice is a fixed number of steps (12 at 1 ms)
const DET = process.env.DET !== '0';   // DET=0: real time also runs inside a slice (the drive before the join), so N moves with the machine's load
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 1800000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = []; let failed = 0;
const check = (id, what, ok, detail = '') => { results.push({ id, what, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${what}${detail ? ': ' + detail : ''}`); };
const info = (id, what, detail) => { results.push({ id, what, info: true, detail }); console.log(`INFO ${id} ${what}: ${detail}`); };

// ---- fixtures (generic names), the same generator as the round 2 to 10 drives ----
const tbl = (name, o = {}) => Object.assign({ ref: 'table:' + name, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [name.replace(/ /g, '') + 'Key'], hasDescription: true }, o);
const rel = (fromTable, toTable) => ({ name: fromTable + '_' + toTable, fromTable, fromColumn: toTable.replace(/ /g, '') + 'Key', toTable, toColumn: toTable.replace(/ /g, '') + 'Key', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
const dedupe = (rels) => { const seen = new Set(); return rels.filter((r) => (r.fromTable !== r.toTable && !seen.has(r.name) ? (seen.add(r.name), true) : false)); };
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

async function open(ctx, graph, w, h, theme) {
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((cfg) => {
    window.__UISHOT__ = cfg;
    window.__saves = []; window.__store = []; window.__graph = JSON.parse(JSON.stringify(cfg.graph));
    cfg.getLayout = () => ({ tables: JSON.parse(JSON.stringify(window.__store)) });
    cfg.saveLayout = (params) => {
      const t = (params && params[0]) || [];
      window.__saves.push({ t: performance.now(), tables: JSON.parse(JSON.stringify(t)) });
      for (const n of t) { const e = { name: n.name, x: n.x, y: n.y, ...(n.keep ? { keep: true } : {}) }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
      return { saved: true, count: t.length };
    };
    cfg.getModelGraph = () => JSON.parse(JSON.stringify(window.__graph));   // a NEW object each time, as the engine's answer is
    // A slice is a fixed number of steps: every call of performance.now moves the clock on by STEP ms as well. Inside a slice of
    // the spread's work (a runInSlices message, data 0) the clock is ONLY those steps (the join, J1): real time leaking into a
    // slice cut it short on a busy machine, so the same 300-table spread took 524 to 641 slices from run to run and a K meant for
    // its last slices landed after it. With it frozen a slice is SLICE_MS / STEP job steps, and N moves by the first slice at most
    // (that one runs inside the page's own effect, where the clock is not frozen).
    const real = performance.now.bind(performance); let extra = 0, last = 0, inSlice = 0;
    performance.now = () => {
      if (inSlice && cfg.det) return (last += cfg.step);
      const v = real() + (extra += cfg.step);
      if (v > last) last = v; else { extra += last - v + cfg.step; last += cfg.step; }
      return last;
    };
    const onmsg = Object.getOwnPropertyDescriptor(MessagePort.prototype, 'onmessage');
    Object.defineProperty(MessagePort.prototype, 'onmessage', { configurable: true, enumerable: onmsg.enumerable, get() { return onmsg.get.call(this); },
      set(fn) { onmsg.set.call(this, typeof fn !== 'function' ? fn : function (e) { if (e.data !== 0) return fn.call(this, e); inSlice++; try { return fn.call(this, e); } finally { inSlice--; } }); } });
    // The test's own slice counter (not product code): runInSlices posts 0 before each further slice of a pass. At slice
    // `__armK` the armed event runs, inside that post, so it lands between two slices.
    const orig = MessagePort.prototype.postMessage;
    window.__slices = 0; window.__armK = -1; window.__armFn = null; window.__fired = null;
    MessagePort.prototype.postMessage = function (m, ...rest) {
      const r = orig.call(this, m, ...rest);
      if (m === 0) {
        window.__slices++;
        if (window.__armK > 0 && window.__slices >= window.__armK && window.__armFn) {
          const fn = window.__armFn; window.__armK = -1; window.__armFn = null;
          window.__fired = { slice: window.__slices, making: !!document.querySelector('[data-making-room]'), open: document.querySelectorAll('button[aria-label^="Hide the columns of "]').length };
          new Function(fn)();
        }
      }
      return r;
    };
  }, { graph, columns: columnsFor(graph), state: ALL_BUSMATRIX, step: STEP, det: DET });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 600000 });
  await page.waitForSelector('.react-flow__node', { timeout: 600000 });
  await applyTheme(page, theme);
  await quiet(page, 2500);
  return { page, errors };
}
// Until the page has not been making room, held a line or saved anything for `q` ms.
async function quiet(page, q = 2500, cap = 900000) {
  const t0 = Date.now(); let last = -1, since = Date.now();
  for (;;) {
    const s = await page.evaluate(() => ({ busy: !!document.querySelector('[data-making-room], .react-flow__edge g[style*="opacity: 0.4"]'), n: window.__saves.length }));
    if (s.n !== last) { last = s.n; since = Date.now(); }
    if (s.busy) since = Date.now();
    if (Date.now() - since >= q || Date.now() - t0 > cap) return;
    await wait(150);
  }
}
const boxesOf = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]);
  return [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform); const r = el.getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom };
  });
});
const atOf = (cards) => Object.fromEntries(cards.map((c) => [c.id, c]));
const storedAt = (stored) => Object.fromEntries(stored.map((e) => [e.name, { x: e.x, y: e.y }]));
const off = (cards, at, tol = 0.5) => cards.filter((c) => at[c.id] && (Math.abs(c.x - at[c.id].x) > tol || Math.abs(c.y - at[c.id].y) > tol)).map((c) => c.id);
const snapshot = async (page) => {
  const cards = await boxesOf(page), st = await page.evaluate(() => JSON.stringify(window.__store)), saves = await page.evaluate(() => window.__saves.length);
  const open = await page.evaluate(() => document.querySelectorAll('button[aria-label^="Hide the columns of "]').length);
  return { cards, at: atOf(cards), st, stored: JSON.parse(st), saves, open };
};
const clickText = (page, text) => page.evaluate((text) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === text && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
}, tables);
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
function plainGrid(sizes, graph, gap, aspect) {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = sizes.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...sizes.map((c) => c.w)), ch = Math.max(...sizes.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: Math.round((i % cols) * (cw + gap)), y: Math.round(Math.floor(i / cols) * (ch + gap)) }));
}
const gridOf = (cards, graph, gap) => (process.env.GRID === 'plain' ? plainGrid(cards, graph, gap, 1440 / 900) : starGrid(cards, graph, gap, 1440 / 900));
const SHAPES = { 80: { graph: () => big(8, 60, 12, 9), open: 20, ks: (N, M) => [2, Math.floor(N / 2), ...Array.from({ length: M - N + 4 }, (_, i) => N - 3 + i)] }, 300: { graph: () => big(30, 226, 44, 9, Number(process.env.HUBS ?? 0)), open: 40, ks: (N, M) => [2, ...[1, 2, 3].map((q) => Math.floor((N * q) / 4)), ...Array.from({ length: M - N + 4 }, (_, i) => N - 3 + i)] } };
// Why 300 tables has no hub by default (the join, 30 Sep, J1 of verify round 11): with one lookup table joined from all 30 fact
// tables (HUBS=1), that card is by far the tallest, and the assistant's star grid (starGrid) sizes EVERY cell by the tallest
// card. Every cell then had room for any opened lookup card, the spread's first make-room found nothing to move, and the spread
// was one slice: nothing to race. With no hub the cells fit the ordinary cards and the same 40-card spread is real work, as it is
// in real VS Code (about 511 slices there, cut by real time): 93 slices here with the slice clock frozen (DET, STEP 1; 27 at 80 tables), 524 to 641
// with real time let in. A 300-table run is over a minute, so the sweep samples the middle (2 and the quarters) and takes every K
// of the tail, where a late result meets the reload.
const pad3 = (i) => String(i + 1).padStart(3, '0');
// The open set: the first n lookup cards, or (MIX=1) every fact card and the first n lookup cards, so the spread has tall cards to make room for.
const setOf = (n, nFacts) => [...Array.from({ length: nFacts }, (_, i) => 'Fact ' + pad3(i)), ...Array.from({ length: n }, (_, i) => 'Dim ' + pad3(i))];
const renamed = (n) => 'Dim R' + n.slice(4);
// The page after the assistant saved its star grid 40 px apart and the page made room for the lines.
async function madeRoom(graph, theme) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  const closed = await boxesOf(page);
  if (process.env.FIT === '1') { await clickText(page, 'Fit'); await wait(2500); }   // every card on screen, so every card is measured
  if (GAP > 0) await post(page, gridOf(closed, graph, GAP));   // GAP=0: keep the page's own bus matrix
  if (process.env.WATCH) for (let i = 0; i < 12; i++) { await wait(5000); info('watch', 'after the assistant save', `${(i + 1) * 5} s: making ${await page.evaluate(() => !!document.querySelector('[data-making-room]'))}, saves ${await page.evaluate(() => window.__saves.length)}, held lines ${await page.evaluate(() => document.querySelectorAll('.react-flow__edge g[style*="opacity: 0.4"]').length)}, off ${off(await boxesOf(page), storedAt(JSON.parse(await page.evaluate(() => JSON.stringify(window.__store))))).length}`); }
  await wait(800); await quiet(page, 4500);
  // A big layout takes the page a while to draw: until every card is on its stored spot (and the page is quiet), so the run starts from a settled page.
  for (let i = 0; i < 1800; i++) {
    const stored = storedAt(JSON.parse(await page.evaluate(() => JSON.stringify(window.__store))));
    if (!off(await boxesOf(page), stored, 0.5).length) break;
    await wait(100);
  }
  await quiet(page, 4500);
  return { ctx, page, errors, closed };
}
// In the page: the assistant deletes the set, or renames it (the model changes and the engine says so). The reload's answer is
// held by the harness first when `hold`, so the event can hand it over.
const changeModel = (page, SET, how) => page.evaluate((SET, how) => {
  const set = new Set(SET), g = window.__graph, deltas = [];
  if (how === 'delete') {
    g.tables = g.tables.filter((t) => !set.has(t.name)); g.relationships = g.relationships.filter((r) => !set.has(r.fromTable) && !set.has(r.toTable));
    for (const n of SET) deltas.push({ kind: 'remove', ref: 'table:' + n });
  } else {
    const nn = (n) => (set.has(n) ? n.split(' ')[0] + ' R' + n.split(' ')[1] : n);
    for (const t of g.tables) if (set.has(t.name)) { t.name = nn(t.name); t.ref = 'table:' + t.name; t.keyColumns = [t.name.replace(/ /g, '') + 'Key']; }
    for (const r of g.relationships) { r.fromTable = nn(r.fromTable); r.toTable = nn(r.toTable); r.name = r.fromTable + '_' + r.toTable; }
    for (const n of SET) { deltas.push({ kind: 'remove', ref: 'table:' + n }); deltas.push({ kind: 'add', ref: 'table:' + nn(n) }); }
  }
  window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: Date.now(), origin: 'agent', deltas } }, '*');
}, SET, how);
const openSet = (page, SET) => page.evaluate((SET) => { for (const c of SET) document.querySelector(`button[aria-label="Show the columns of ${c}"]`)?.click(); }, SET);
const arm = (page, K, fn) => page.evaluate((K, fn) => { window.__slices = 0; window.__armK = K; window.__armFn = fn; window.__fired = null; }, K, fn);
const BTN = (t) => `[...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === '${t}' && x.getClientRects().length)?.click();`;
const finish = async (page, ev) => {
  if (ev === 'switch') {
    await wait(1200); await page.keyboard.press('Escape'); await wait(300);
    await page.evaluate(() => { const sel = document.querySelector('select[title="Switch diagram"]'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; set.call(sel, 'all'); sel.dispatchEvent(new Event('change', { bubbles: true })); });
    await wait(800);
  } else await wait(ev === 'save' || ev === 'arrange' ? 1200 : 600);
  if (await page.evaluate(() => document.querySelectorAll('button[aria-label^="Hide the columns of "]').length)) { await clickText(page, 'Collapse all'); await wait(800); }
  await quiet(page, 4500);
};

for (const size of SIZES) for (const theme of THEMES) {
  const shape = SHAPES[size], graph = shape.graph(), SET = setOf(OPEN || shape.open, MIX ? graph.tables.filter((t) => t.name.startsWith('Fact')).length : 0), tag = `${size}-${theme}`;
  // 1. How many further slices the spread for the open set takes (click to the canvas no longer making room), measured twice.
  const Ns = [];
  for (let i = 0; i < 2; i++) {
    const { ctx, page } = await madeRoom(graph, theme);
    const t0 = Date.now(), cardsBefore = (await boxesOf(page)).length;
    Ns.push(await page.evaluate((SET, ms) => new Promise((resolve) => {
      window.__slices = 0; let seen = false;
      const mo = new MutationObserver(() => { const on = !!document.querySelector('[data-making-room]'); if (on) seen = true; else if (seen) { mo.disconnect(); resolve(window.__slices); } });
      mo.observe(document.body, { attributes: true, subtree: true, attributeFilter: ['data-making-room'] });
      for (const c of SET) document.querySelector(`button[aria-label="Show the columns of ${c}"]`)?.click();
      setTimeout(() => { mo.disconnect(); resolve(seen ? window.__slices : 0); }, ms);
    }), SET, Number(process.env.MEASURE_MS || 600000)));
    info(`measure-${tag}`, 'the measuring run', `${cardsBefore} cards, ${await page.evaluate(() => document.querySelectorAll('button[aria-label^="Hide the columns of "]').length)} open after, ${((Date.now() - t0) / 1000).toFixed(1)} s, making now ${await page.evaluate(() => !!document.querySelector('[data-making-room]'))}, ${off(await boxesOf(page), storedAt(JSON.parse(await page.evaluate(() => JSON.stringify(window.__store))))).length} off their stored spot, saves ${await page.evaluate(() => window.__saves.length)}`);
    await ctx.close();
  }
  const N = Math.min(...Ns), M = Math.max(...Ns);   // the work's slice count moves by a few with the machine: the sweep covers the tail of both
  info(`slices-${tag}`, `the spread for ${SET.length} open lookup cards is worked out in this many further slices (two measures)`, Ns.join(', '));
  // A spread of one slice has no work to race, so it fails the drive at every size (it did at 300 tables until the join: see SHAPES).
  if (N < 4) { check(`precondition-${tag}`, 'the spread takes several slices here', false, `${N}`); continue; }
  const KS = [...new Set(shape.ks(N, M).filter((k) => k >= 1 && k <= M))];

  for (const ev of EVENTS) {
    // 2. The control: what the same event leaves with every card closed (an arrange: with the spread settled).
    let control, s0;
    {
      const { ctx, page } = await madeRoom(graph, theme);
      s0 = await snapshot(page);
      if (ev === 'close' || ev === 'collapse' || ev === 'switch') control = s0;
      else if (ev === 'reload-gone' || ev === 'reload-named') { await changeModel(page, SET, ev === 'reload-gone' ? 'delete' : 'rename'); await wait(1500); await quiet(page, 4500); control = await snapshot(page); }
      else if (ev === 'save') { await post(page, gridOf(await boxesOf(page), graph, 20)); await wait(1200); await quiet(page, 4500); control = await snapshot(page); }
      else { await openSet(page, SET); await wait(500); await quiet(page, 4500); await clickText(page, 'Bus matrix'); await wait(1200); await quiet(page, 4500); await clickText(page, 'Collapse all'); await wait(800); await quiet(page, 4500); control = await snapshot(page); }
      await ctx.close();
    }
    // 3. The race, at each K.
    const bad = []; let landedWorking = 0, runs = 0;
    for (const K of Array.from({ length: PASSES || (size <= 80 ? 2 : 1) }, () => KS).flat()) {
      const { ctx, page, errors } = await madeRoom(graph, theme);
      const before = await snapshot(page);
      let fn;
      if (ev === 'close') fn = `for (const b of document.querySelectorAll('button[aria-label^="Hide the columns of "]')) b.click();`;
      else if (ev === 'collapse') fn = BTN('Collapse all');
      else if (ev === 'switch') fn = `document.querySelector('button[aria-label="New diagram"]').click();`;
      else if (ev === 'arrange') fn = BTN('Bus matrix');
      else if (ev === 'save') {
        const tables = gridOf(await boxesOf(page), graph, 20);
        fn = `const tables = ${JSON.stringify(tables)}; for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); } window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');`;
      } else {
        await page.evaluate(() => window.__uishotHarness.hold('getModelGraph'));
        await changeModel(page, SET, ev === 'reload-gone' ? 'delete' : 'rename');
        for (let i = 0; i < 40 && !(await page.evaluate(() => window.__uishotHarness.heldCount('getModelGraph'))); i++) await wait(50);
        fn = `window.__uishotHarness.release('getModelGraph');`;
      }
      await arm(page, K, fn);
      await openSet(page, SET);
      // the spread's work takes as long as it takes; the event has fired by the time the canvas stops making room
      for (let i = 0; i < 6000; i++) { if (await page.evaluate(() => window.__fired || !document.querySelector('[data-making-room]'))) { if (i > 4 || await page.evaluate(() => window.__fired)) break; } await wait(100); }
      const fired = await page.evaluate(() => window.__fired);
      if (fired === null) await page.evaluate((fn) => { window.__armK = -1; window.__armFn = null; new Function(fn)(); }, fn);   // late: after the spread
      await finish(page, ev);
      const s1 = await snapshot(page);
      runs++; if (fired?.making) landedWorking++;
      const offStored = off(s1.cards, storedAt(s1.stored)), offControl = off(s1.cards, control.at);
      const missing = control.cards.filter((c) => !s1.at[c.id]).length + s1.cards.filter((c) => !control.at[c.id]).length;
      const differ = s1.st !== control.st, wrote = (ev === 'close' || ev === 'collapse' || ev === 'switch') && s1.saves !== before.saves;
      info(`race-${ev}-${tag}-run${runs}-k${K}`, 'fired', `${fired ? `at slice ${fired.slice} of ${N}, ${fired.open} open, spread still worked out: ${fired.making}` : 'after the spread (late)'}; ${offStored.length} of ${s1.cards.length} off their stored spot; ${offControl.length} off the closed run; stored ${differ ? 'DIFFERENT from' : 'identical to'} the closed run; ${s1.open} open; ${s1.saves - before.saves} saves`);
      if (offStored.length || offControl.length || missing || differ || s1.open || wrote || errors.length) {
        bad.push({ K, fired, offStored: offStored.length, offControl: offControl.length, missing, differ, open: s1.open, wrote, errors: errors.slice(0, 2), eg: offStored.slice(0, 3) });
        if (bad.length === 1) await page.screenshot({ path: join(OUT, `${ev}-${tag}-k${K}.png`) });
      }
      await ctx.close();
    }
    check(`race-${ev}-${tag}-landed`, 'at least one run had the event land while the spread was still being worked out (a precondition)', landedWorking > 0, `${landedWorking} of ${runs} runs`);
    check(`race-${ev}-${tag}`, 'after the event and a Collapse all: 0 cards off their stored spot, 0 off the closed run, get_layout byte for byte the closed run, no card open' + (ev === 'close' || ev === 'collapse' || ev === 'switch' ? ', 0 saves' : ''), !bad.length,
      bad.length ? bad.map((b) => `K ${b.K}${b.fired ? ` (slice ${b.fired.slice})` : ' (late)'}: ${b.offStored} off stored, ${b.offControl} off closed run${b.differ ? ', stored differs' : ''}${b.missing ? `, ${b.missing} cards missing or extra` : ''}${b.open ? `, ${b.open} open` : ''}${b.wrote ? ', wrote' : ''}${b.errors.length ? ', errors ' + b.errors.join(' | ') : ''}`).join('; ') : `${runs} runs, K ${KS.join(', ')}`);
  }
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
