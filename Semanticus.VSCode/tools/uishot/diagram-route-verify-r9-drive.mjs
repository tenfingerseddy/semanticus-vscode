// verify: driven proof from round 9 of the m11/diagram-route verification (29 Sep), left failing on purpose for the
// builder. Needs the built bundle (npm run build:webview). Serves it, opens the real harness with a stand-in for the
// engine's layout sidecar (saveLayout merges the way LocalEngine.SaveLayoutAsync does), and reads the live canvas.
// The bar is Kane's round 9 ruling (29 Sep, "Slide down"), OPENING IS A VIEW, NOT A LAYOUT CHANGE:
//   rule 2: closing the last open card (or Collapse all) ends the spread, every card returns to its saved spot within
//           0.5 px, nothing is saved;
//   rule 6: no position a spread drew ever reaches persistPositions, saveLayout or the stored diagram.
// The spread's room for the lines is worked out a few ms at a time (roomSteps in runInSlices). When the last card closes
// while that work is still running, nothing stops it: the spread effect's "no card open" branch (diagram.tsx, the
// useLayoutEffect of the spread: `if (!nodes.some((n) => n.data.expanded)) { spread.current = null; return; }`) and the
// settle effect's early return (`if (!changed && !settleAll.current) ... return`) both return before dropRoom(). When
// the work ends, its callback draws the spread anyway (`later` -> `done` -> `draw`), with every card closed: the cards
// jump to where the spread would put them and stay there, and `spread.current` is cleared again on the next render, so
// the page now takes those spots for the saved layout. Its next make-room (after a link) saves them.
//   1. The 30-table model, the assistant's plain grid 20 px apart (the page made room), 1440 x 900: Expand all, and
//      Collapse all the moment the spread starts working. Every card should be back on its saved spot.
//   2. The 80-table model, the assistant's star grid 40 px apart: a person's Collapse all half a second after Expand all.
//   3. After 1: the core journey, two cards opened and linked (the model gains the link, as a column drag makes), then
//      Collapse all. The page's make-room for the link saves the cards that follow the grown card: it saves them on the
//      spread's columns, and afterwards the page draws cards away from what is stored (what get_layout answers).
//   4. Or after 1: the person clicks the Put back the page offers again once the cards are closed. Its pass keeps, where
//      they stand, the cards the page would still move: some of them exactly on the spots the spread drew, now kept.
// At 6c784780 every check fails. Doing the same with Collapse all clicked after the spread has finished passes them all.
//   node diagram-route-verify-r9-drive.mjs      (OUTDIR=<dir> for the PNGs and results.json; SECTIONS=1,2,3,4)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route-verify-r9');
mkdirSync(OUT, { recursive: true });
const SECTIONS = new Set((process.env.SECTIONS || '1,2,3,4').split(','));
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
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const toggle = (page, id, how) => page.evaluate((id, how) => { const b = document.querySelector(`button[aria-label="${how} the columns of ${id}"]`); if (b) b.click(); return !!b; }, id, how);
const post = (page, tables) => page.evaluate((tables) => {
  for (const n of tables) { const e = { name: n.name, x: n.x, y: n.y }; const i = window.__store.findIndex((s) => s.name === n.name); if (i >= 0) window.__store[i] = e; else window.__store.push(e); }
  window.postMessage({ type: 'layoutDidChange', payload: { origin: 'agent', tables: JSON.parse(JSON.stringify(window.__store)) } }, '*');
}, tables);
function plainGrid(cards, graph, gap, aspect) {
  const role = (n) => { const r = graph.relationships; const from = r.some((x) => x.fromTable === n), to = r.some((x) => x.toTable === n); return from && !to ? 0 : to ? 1 : 2; };
  const names = cards.map((c) => c.id).sort((a, b) => role(a) - role(b) || a.localeCompare(b));
  const cw = Math.max(...cards.map((c) => c.w)), ch = Math.max(...cards.map((c) => c.h));
  const cols = Math.max(1, Math.round(Math.sqrt(names.length * aspect * ch / cw)));
  return names.map((name, i) => ({ name, x: Math.round((i % cols) * (cw + gap)), y: Math.round(Math.floor(i / cols) * (ch + gap)) }));
}
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
// The page after the assistant saved its grid and the page made room for the lines (moved cards, saved them).
async function madeRoom(graph, grid, gap, theme) {
  const ctx = await browser.createBrowserContext();
  const { page, errors } = await open(ctx, graph, 1440, 900, theme);
  await post(page, grid(await boxesOf(page), graph, gap, 1440 / 900));
  await wait(800); await quiet(page, 4500);
  return { ctx, page, errors };
}
// In the page: click Expand all, then click Collapse all `after` ms later, or (after === 'working') the moment the canvas
// says the spread is still being worked out (data-making-room); if it never says so, 3 s after the click. Returns when,
// and whether the spread was still being worked out at that moment.
const expandThenCollapse = (page, after) => page.evaluate((after) => new Promise((resolve) => {
  const t0 = performance.now(); let done = false;
  const btn = (t) => [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  const close = () => { if (done) return; done = true; const making = !!document.querySelector('[data-making-room]'); btn('Collapse all').click(); resolve({ at: Math.round(performance.now() - t0), making }); };
  if (after === 'working') {
    const mo = new MutationObserver(() => { if (document.querySelector('[data-making-room]')) { mo.disconnect(); close(); } });
    mo.observe(document.body, { attributes: true, subtree: true, attributeFilter: ['data-making-room'] });
    setTimeout(() => { mo.disconnect(); close(); }, 3000);
  } else setTimeout(close, after);
  btn('Expand all').click();
}), after);

const stranded = {};
// ---- 1 and 2: Collapse all while the spread is still being worked out ----
for (const [sec, name, graph, grid, gap, after] of [['1', 'thirty-plain20', thirty(), plainGrid, 20, 'working'], ['2', 'eighty-star40', big(8, 60, 12, 9), starGrid, 40, 500]]) {
  if (!SECTIONS.has(sec) && !(sec === '1' && (SECTIONS.has('3') || SECTIONS.has('4')))) continue;
  for (const theme of sec === '1' ? ['dark', 'light'] : ['light']) {
    const { ctx, page, errors } = await madeRoom(graph, grid, gap, theme);
    const s0 = await snapshot(page);
    const how = await expandThenCollapse(page, after);
    await wait(1000); await quiet(page, 3000);
    const s1 = await snapshot(page);
    const back = off(s1.cards, s0.at);
    const far = back.map((id) => Math.hypot(s1.at[id].x - s0.at[id].x, s1.at[id].y - s0.at[id].y));
    info(`${name}-${theme}`, after === 'working' ? 'Collapse all clicked the moment the spread started working' : `Collapse all clicked ${after} ms after Expand all`, `clicked at ${how.at} ms, the spread still being worked out: ${how.making}`);
    if (SECTIONS.has(sec)) check(`${name}-${theme}-back`, 'every card is back on its saved spot within 0.5 px once the last card is closed, nothing saved (rule 2)', !back.length && s1.saves === s0.saves && s1.st === s0.st && s1.open === 0,
      `${back.length} of ${s1.cards.length} cards off their saved spot, up to ${Math.round(Math.max(0, ...far))} px (${back.slice(0, 4).join(', ')}); ${s1.saves - s0.saves} saves; stored ${s1.st === s0.st ? 'identical' : 'DIFFERENT'}; ${s1.open} open`);
    if (back.length) await page.screenshot({ path: join(OUT, `${name}-${theme}-after-collapse.png`) });
    // ---- 3: then the core journey: two cards opened and linked, then Collapse all ----
    if (sec === '1' && theme === 'dark' && SECTIONS.has('3')) {
      await toggle(page, 'Sales', 'Show'); await wait(600); await quiet(page, 2000);
      await toggle(page, 'Notes', 'Show'); await wait(600); await quiet(page, 2500);
      const n1 = (await snapshot(page)).saves;
      await page.evaluate(() => {
        window.__graph.relationships.push({ name: 'Sales_Notes_link', fromTable: 'Sales', fromColumn: 'SalesField1', toTable: 'Notes', toColumn: 'NotesField1', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true });
        window.postMessage({ type: 'didChange', payload: { sessionId: 's', revision: 2, origin: 'human', deltas: [{ kind: 'add', ref: 'relationship:Sales_Notes_link' }] } }, '*');
      });
      await wait(1200); await quiet(page, 3000);
      await clickText(page, 'Collapse all'); await wait(1000); await quiet(page, 4000);
      const s2 = await snapshot(page);
      const entries = (await page.evaluate((n) => window.__saves.slice(n), n1)).flatMap((x) => x.tables);
      // A saved card on the column the spread drew it in, off the column it was saved in before: its spot came from the spread.
      const fromSpread = entries.filter((e) => s1.at[e.name] && s0.at[e.name] && Math.abs(e.x - s1.at[e.name].x) < 0.5 && Math.abs(s1.at[e.name].x - s0.at[e.name].x) > 0.5);
      check('link-after-collapse-saved', 'the make-room after the link saves no spot a spread drew (rule 6)', fromSpread.length === 0,
        `${entries.length} saved (${entries.map((e) => `${e.name} at ${e.x.toFixed(0)}, ${e.y.toFixed(0)}`).join('; ')}); ${fromSpread.length} on a spread column: ${fromSpread.map((e) => `${e.name} x ${e.x.toFixed(0)} (the spread's ${s1.at[e.name].x.toFixed(0)}, saved before ${s0.at[e.name].x.toFixed(0)})`).join('; ')}`);
      const drawnOff = off(s2.cards, storedAt(s2.stored));
      check('link-after-collapse-drawn', 'afterwards every card is drawn on its stored spot (what get_layout answers is what the person sees)', drawnOff.length === 0, `${drawnOff.length} of ${s2.cards.length} drawn off their stored spot (${drawnOff.slice(0, 4).join(', ')})`);
      await page.screenshot({ path: join(OUT, 'link-after-collapse-dark.png') });
    }
    // ---- 4: or, after 1, the person clicks the Put back the page offers again once the cards are closed ----
    if (sec === '1' && theme === 'light' && SECTIONS.has('4')) {
      const n1 = s1.saves;
      const clicked = await clickText(page, 'Put back'); await wait(1000); await quiet(page, 4000);
      const s2 = await snapshot(page);
      const entries = (await page.evaluate((n) => window.__saves.slice(n), n1)).flatMap((x) => x.tables);
      // Saved exactly where the spread drew it, a spot that is not where it was saved before the first open.
      const onSpread = entries.filter((e) => s1.at[e.name] && s0.at[e.name] && Math.abs(e.x - s1.at[e.name].x) < 0.5 && Math.abs(e.y - s1.at[e.name].y) < 0.5 && (Math.abs(s1.at[e.name].x - s0.at[e.name].x) > 0.5 || Math.abs(s1.at[e.name].y - s0.at[e.name].y) > 0.5));
      check('putback-after-collapse-saved', 'the Put back saves no card on a spot the spread drew, and marks none of those kept (rule 6)', clicked && onSpread.length === 0,
        `Put back clicked ${clicked}; ${entries.length} saved; ${onSpread.length} exactly on their spread spot, ${onSpread.filter((e) => e.keep).length} of them kept: ${onSpread.map((e) => e.name).join(', ')}`);
      const drawnOff = off(s2.cards, storedAt(s2.stored));
      check('putback-after-collapse-drawn', 'afterwards every card is drawn on its stored spot', drawnOff.length === 0, `${drawnOff.length} of ${s2.cards.length} drawn off their stored spot`);
    }
    if (errors.length) check(`${name}-${theme}`, 'no page errors', false, errors.slice(0, 3).join(' | '));
    await ctx.close();
  }
}

writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed, ${results.filter((r) => r.info).length} reported. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
