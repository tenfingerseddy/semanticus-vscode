// verify: driven proof from round 10 of the m11/diagram-route verification (30 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness with a stand-in for the
// engine's layout sidecar (saveLayout merges the way LocalEngine.SaveLayoutAsync does), and reads the live canvas.
// The bar is Kane's ruling (29 Sep, "Slide down"), OPENING IS A VIEW, NOT A LAYOUT CHANGE:
//   rule 2: when no card is open any more, every card is on its saved spot within 0.5 px;
//   rule 6: no position a spread drew ever reaches persistPositions, saveLayout or the stored diagram.
// Round 10 added a guard to the spread's `draw` (diagram.tsx, `if (!anyOpenRef.current) { spread.current = null; return; }`):
// a result that lands when no card is open is not drawn. But it also throws away `spread.current`, the only place the
// SAVED spots live while a spread is drawn (the flow store holds the drawn ones). The model reload that took the open
// cards away (the assistant deletes or renames the tables the person has open) is not a click, so React renders it in one
// task and runs its effects, the rebuild, in a later one. When the spread's last slice runs in between, the guard clears
// `spread.current`, and the rebuild then reads the flow store (the DRAWN spots) as the saved layout (the `live` map): every
// card stays where the spread drew it with no card open, and the reload's own make-room (a reload is a layout change)
// saves the cards it moves from those spots.
// The race is one slice wide, so this drive makes it land every time it can: the test page (not the product) counts the
// slices of the spread's work (runInSlices posts 0 on its own MessageChannel before each further slice; React's scheduler
// posts null), and the harness's own hold/release seam hands the reload's getModelGraph answer to the page at the end of
// slice K. It first measures how many slices the spread takes (N), then tries K from N - 3 to N + 1, three passes, and
// stops at the first run that leaves a card off its stored spot or stores what the same change with every card closed
// does not.
//   80 tables, the assistant's star grid 40 px apart (the page made room). Twenty lookup cards open (Dim 001 to Dim 020);
//   the assistant deletes those twenty tables. Expected: the 60 cards left are drawn on their stored spots, and the stored
//   layout equals, byte for byte, what the same delete stores with every card closed.
// At 407b655e this fails (48 of 60 cards drawn off their stored spot, 7 stored entries differ from the closed run). With
// the guard only returning (leaving `spread.current` for the rebuild), it passes.
//   node diagram-route-verify-r10-drive.mjs      (OUTDIR=<dir>; THEMES=dark,light; PASSES=3; WEBROOT=<checkout>)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r10');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark,light').split(',');
const PASSES = Number(process.env.PASSES || 3);
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

// ---- fixtures (generic names), the same generator as the round 2 to 9 drives ----
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
    // The test's own slice counter (not product code): runInSlices posts 0 before each further slice of a pass; at the end
    // of slice `__armK` a held getModelGraph answer is handed to the page (the harness's own release).
    const orig = MessagePort.prototype.postMessage;
    window.__slices = 0; window.__armK = -1; window.__released = null;
    MessagePort.prototype.postMessage = function (m, ...rest) {
      const r = orig.call(this, m, ...rest);
      if (m === 0) {
        window.__slices++;
        if (window.__armK > 0 && window.__slices >= window.__armK && window.__uishotHarness && window.__uishotHarness.heldCount('getModelGraph') > 0) {
          window.__armK = -1; window.__released = window.__slices; window.__uishotHarness.release('getModelGraph');
        }
      }
      return r;
    };
  }, { graph, columns: columnsFor(graph), state: ALL_BUSMATRIX });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 300000 });
  await page.waitForSelector('.react-flow__node', { timeout: 300000 });
  await applyTheme(page, theme);
  await quiet(page, 2500);
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
const off = (cards, at, tol = 0.5) => cards.filter((c) => !at[c.id] || Math.abs(c.x - at[c.id].x) > tol || Math.abs(c.y - at[c.id].y) > tol).map((c) => c.id);
const snapshot = async (page) => {
  const cards = await boxesOf(page), st = await page.evaluate(() => JSON.stringify(window.__store)), saves = await page.evaluate(() => window.__saves.length);
  const open = await page.evaluate(() => document.querySelectorAll('button[aria-label^="Hide the columns of "]').length);
  return { cards, at: atOf(cards), st, stored: JSON.parse(st), saves, open };
};
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
const graph = big(8, 60, 12, 9);
const SET = Array.from({ length: 20 }, (_, i) => 'Dim ' + String(i + 1).padStart(3, '0'));
// The page after the assistant saved its star grid 40 px apart and the page made room for the lines.
async function madeRoom(theme) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await post(page, starGrid(await boxesOf(page), graph, 40, 1440 / 900));
  await wait(800); await quiet(page, 4500);
  return { ctx, page, errors };
}
// In the page: the assistant deletes the set (the model loses the tables and their links) and the engine says so.
const deleteSet = (page) => page.evaluate((SET) => {
  const set = new Set(SET), g = window.__graph;
  g.tables = g.tables.filter((t) => !set.has(t.name)); g.relationships = g.relationships.filter((r) => !set.has(r.fromTable) && !set.has(r.toTable));
  window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: Date.now(), origin: 'agent', deltas: SET.map((n) => ({ kind: 'remove', ref: 'table:' + n })) } }, '*');
}, SET);
const openSet = (page) => page.evaluate((SET) => { for (const c of SET) document.querySelector(`button[aria-label="Show the columns of ${c}"]`)?.click(); }, SET);

for (const theme of THEMES) {
  // 1. How many slices the spread for the twenty open cards takes on this machine (click to the canvas no longer making room).
  let N;
  {
    const { ctx, page } = await madeRoom(theme);
    N = await page.evaluate((SET) => new Promise((resolve) => {
      window.__slices = 0; let seen = false;
      const mo = new MutationObserver(() => { const on = !!document.querySelector('[data-making-room]'); if (on) seen = true; else if (seen) { mo.disconnect(); resolve(window.__slices); } });
      mo.observe(document.body, { attributes: true, subtree: true, attributeFilter: ['data-making-room'] });
      for (const c of SET) document.querySelector(`button[aria-label="Show the columns of ${c}"]`)?.click();
      setTimeout(() => { mo.disconnect(); resolve(seen ? window.__slices : 0); }, 20000);
    }), SET);
    info(`slices-${theme}`, 'the spread for twenty open lookup cards is worked out in this many further slices', `${N}`);
    await ctx.close();
  }
  if (!N) { check(`race-${theme}`, 'the spread for twenty open cards takes more than one slice here (a precondition)', false, 'it finished in its first slice'); continue; }
  // 2. Control: the same delete with every card closed.
  let control;
  {
    const { ctx, page } = await madeRoom(theme);
    await deleteSet(page); await wait(1500); await quiet(page, 4000);
    control = await snapshot(page);
    info(`control-${theme}`, 'the delete with every card closed', `${control.cards.length} cards, ${off(control.cards, storedAt(control.stored)).length} drawn off their stored spot`);
    await ctx.close();
  }
  // 3. The race: the reload's answer handed to the page at the end of slice K, K from N - 3 to N + 1.
  let bad = null, runs = 0;
  for (let pass = 0; pass < PASSES && !bad; pass++) for (let K = Math.max(1, N - 3); K <= N + 1 && !bad; K++) {
    const { ctx, page, errors } = await madeRoom(theme);
    const s0 = await snapshot(page);
    await page.evaluate(() => window.__uishotHarness.hold('getModelGraph'));
    await deleteSet(page);
    for (let i = 0; i < 40 && !(await page.evaluate(() => window.__uishotHarness.heldCount('getModelGraph'))); i++) await wait(50);
    await page.evaluate((K) => { window.__slices = 0; window.__armK = K; window.__released = null; }, K);
    await openSet(page);
    await wait(3000);
    const released = await page.evaluate(() => window.__released);
    if (released === null) await page.evaluate(() => { window.__armK = -1; window.__uishotHarness.release('getModelGraph'); });
    await wait(500); await quiet(page, 4000);
    const s1 = await snapshot(page);
    runs++;
    const drawnOff = off(s1.cards, storedAt(s1.stored));
    const differ = s1.stored.filter((e) => { const c = control.stored.find((x) => x.name === e.name); return !c || c.x !== e.x || c.y !== e.y || !!c.keep !== !!e.keep; });
    info(`race-${theme}-pass${pass + 1}-k${K}`, 'answered at the end of slice', `${released ?? 'after the spread (late)'}; ${drawnOff.length} of ${s1.cards.length} drawn off their stored spot; ${differ.length} stored entries differ from the closed run; ${s1.open} open; ${s1.saves - s0.saves} saves`);
    if (drawnOff.length || s1.st !== control.st) {
      const d = drawnOff.map((id) => { const a = s1.at[id], b = storedAt(s1.stored)[id]; return b ? Math.hypot(a.x - b.x, a.y - b.y) : 0; });
      bad = { K, released, drawnOff, differ, far: Math.round(Math.max(0, ...d)), cards: s1.cards.length, open: s1.open };
      await page.screenshot({ path: join(OUT, `reload-took-every-open-card-${theme}.png`) });
    }
    if (errors.length) info(`race-${theme}-k${K}`, 'page errors', errors.slice(0, 3).join(' | '));
    await ctx.close();
  }
  check(`race-${theme}-back`, 'a reload that takes away every open card while the spread is worked out: every card is on its stored spot within 0.5 px once no card is open (rule 2)', !bad || !bad.drawnOff.length,
    bad ? `answered at slice ${bad.released} (K ${bad.K}): ${bad.drawnOff.length} of ${bad.cards} cards drawn off their stored spot, up to ${bad.far} px (${bad.drawnOff.slice(0, 4).join(', ')}); ${bad.open} open` : `${runs} runs, K ${Math.max(1, N - 3)} to ${N + 1}`);
  check(`race-${theme}-stored`, 'and what is stored equals, byte for byte, what the same delete stores with every card closed: no spot a spread drew is saved (rule 6)', !bad || !bad.differ.length,
    bad ? `${bad.differ.length} entries differ: ${bad.differ.slice(0, 5).map((e) => `${e.name} (${Math.round(e.x)}, ${Math.round(e.y)})`).join(', ')}` : `${runs} runs`);
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
