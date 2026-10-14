// Dark 1440 captures for the matrix-choice lane: the default Diagram, the dot grid, and what the dot grid draws.
//   ROOT=<Semanticus.VSCode to serve> OUT=<dir> TAG=<name> ONLY=contoso,edge,forty,sixty MODE=default|grid node tools/uishot/matrix-choice-shots.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(process.env.ROOT || join(here, '..', '..'));
const OUT = process.env.OUT || join(here, 'shots', 'matrix-choice');
const TAG = process.env.TAG || 'shot';
const MODE = process.env.MODE || 'default';
const WIDTH = Number(process.env.WIDTH || 1440), HEIGHT = Number(process.env.HEIGHT || 900);
mkdirSync(OUT, { recursive: true });
// Tools come from THIS checkout (the browser finder, the themes); the page is served from ROOT (this one, or a 1.4.0 copy).
const { findBrowser, requireSupportedNode } = await import(join(here, 'browser.mjs'));
const { applyTheme } = await import(join(here, 'themes.mjs'));
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const T = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.json': 'application/json', '.png': 'image/png' };
const server = createServer((q, s) => {
  const p = normalize(join(ROOT, decodeURIComponent(q.url.split('?')[0])));
  if (!p.startsWith(ROOT) || !existsSync(p) || statSync(p).isDirectory()) { s.writeHead(404); return s.end(); }
  s.writeHead(200, { 'content-type': T[extname(p)] || 'application/octet-stream' }); s.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));

const tbl = (name, o = {}) => Object.assign({ ref: 'table:' + name, name, isHidden: false, isDateTable: false, isCalculated: false, isFieldParameter: false, columns: 8, measures: 0, keyColumns: [name + 'Key'], hasDescription: true }, o);
const rel = (f, t, o = {}) => Object.assign({ name: f + '_' + t, fromTable: f, fromColumn: t + 'Key', toTable: t, toColumn: t + 'Key', fromCardinality: 'Many', toCardinality: 'One', crossFilter: 'OneDirection', isActive: true }, o);
const fact = (n) => tbl(n, { keyColumns: [] });
const CONTOSO = { tables: [fact('Returns'), fact('Sales'), fact('Budget'), fact('Inventory'), tbl('Store'), tbl('Customer'), tbl('Product'), tbl('Date', { isDateTable: true }), tbl('Measures', { measures: 11, columns: 1, keyColumns: [] })],
  relationships: [rel('Returns', 'Customer'), rel('Returns', 'Date'), rel('Returns', 'Product'), rel('Returns', 'Store'), rel('Sales', 'Customer'), rel('Sales', 'Date'), rel('Sales', 'Product'), rel('Sales', 'Store'),
    rel('Budget', 'Date'), rel('Budget', 'Product'), rel('Budget', 'Store'), rel('Inventory', 'Date'), rel('Inventory', 'Product'), rel('Inventory', 'Store')] };
// The verifier's role-playing and header model (v-visuals/edge-repro.mjs).
const EDGE = { tables: [fact('Sales'), fact('Order Header'), fact('Order Line'), tbl('Date', { isDateTable: true }), tbl('Product'), tbl('Category'), tbl('Customer'), tbl('Notes', { keyColumns: [], isCalculated: true })],
  relationships: [rel('Sales', 'Date', { name: 'Sales_OrderDate' }), rel('Sales', 'Date', { name: 'Sales_ShipDate', fromColumn: 'ShipDateKey', isActive: false }), rel('Sales', 'Product'), rel('Sales', 'Customer', { crossFilter: 'BothDirections' }),
    rel('Product', 'Category'), rel('Order Line', 'Order Header'), rel('Order Header', 'Date'), rel('Order Header', 'Customer'), rel('Order Line', 'Product')] };
// A 40-table model with fact-to-fact links, shaped like a workspace-admin model: 14 facts, two headers, 17 lookup tables, outriggers, loose tables.
const FACTS = ['Activity Event', 'Access Read', 'Refresh Run', 'Query Log', 'Export Job', 'Share Event', 'Login Event', 'Alert Fired', 'Gateway Call', 'Storage Scan', 'Usage Daily', 'Cost Line', 'Audit Entry', 'Report View'];
const DIMS = ['Date', 'Workspace', 'User', 'Capacity', 'Item', 'Region', 'Device', 'App', 'Team', 'Tenant', 'Gateway', 'Dataset', 'Report', 'Role', 'Cost Centre', 'Plan', 'Domain'];
const LOOSE = ['Parameters', 'Notes', 'Glossary', 'Measures', 'Field Picker', 'Calendar Help'];
const links = [];
FACTS.forEach((f, i) => { if (f === 'Access Read') return; [0, 1, 2, 3 + (i % 5), 8 + (i % 7)].forEach((k) => { if (!links.some((l) => l.name === f + '_' + DIMS[k])) links.push(rel(f, DIMS[k])); }); });
links.push(rel('Access Read', 'Date'), rel('Access Read', 'Workspace'), rel('Access Read', 'User'));
links.push(rel('Activity Event', 'Access Read'), rel('Refresh Run', 'Access Read'), rel('Query Log', 'Access Read'), rel('Export Job', 'Access Read'));
links.push(rel('Workspace', 'Capacity'), rel('Item', 'Workspace'), rel('Dataset', 'Workspace'), rel('Report', 'Dataset'), rel('Device', 'User'));
const FORTY = { tables: FACTS.map(fact).concat(DIMS.map((n) => tbl(n, { isDateTable: n === 'Date' }))).concat(LOOSE.map((n) => tbl(n, { keyColumns: [], isCalculated: n === 'Notes' }))), relationships: links };
const GRAPHS = { contoso: CONTOSO, edge: EDGE, forty: FORTY };

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const clickText = (page, text) => page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith(t) && x.getClientRects().length); if (b) b.click(); return !!b; }, text);
const measure = (page) => page.evaluate(() => {
  const canvas = document.querySelector('.xe-canvas')?.getBoundingClientRect();
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
  const nodes = [...document.querySelectorAll('.react-flow__node')].map((n) => n.getBoundingClientRect());
  const paths = [...document.querySelectorAll('.react-flow__edge-path')].map((p) => { const b = p.getBoundingClientRect(); return Math.round(Math.max(b.width, b.height) / zoom); });
  const dots = document.querySelectorAll('[data-matrix-dot]').length, tags = document.querySelectorAll('[data-link-tag]').length;
  const chips = [...document.querySelectorAll('.sem-canvas-chip')].map((c) => ({ text: c.textContent.slice(0, 40), clipped: c.scrollWidth > c.clientWidth + 1 || c.scrollHeight > c.clientHeight + 1, inside: c.getBoundingClientRect().right <= canvas.right + 1 && c.getBoundingClientRect().left >= canvas.left - 1 }));
  const rs = [...document.querySelectorAll('.sem-canvas-chip')].map((c) => c.getBoundingClientRect());
  const chipsOverlap = rs.length > 1 && rs[0].right > rs[1].left && rs[0].bottom > rs[1].top && rs[0].top < rs[1].bottom;
  const cut = nodes.filter((r) => r.right > canvas.right + 1 || r.left < canvas.left - 1).length;
  const rightmost = nodes.length ? Math.max(...nodes.map((r) => r.right)) : 0;
  return { zoom: Number(zoom.toFixed(3)), textPx: Number((zoom * 11).toFixed(1)), cards: nodes.length, cardsCutByCanvasEdge: cut, rightmostCardRight: Math.round(rightmost), canvasRight: Math.round(canvas.right),
    chipsOverlap, lineEdges: paths.length, longestLine: Math.max(0, ...paths), linesOver80: paths.filter((l) => l > 80).length, dots, tags, chips };
});
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const results = [];
for (const name of (process.env.ONLY || 'contoso').split(',')) {
  const page = await (await browser.createBrowserContext()).newPage();
  await page.setViewport({ width: WIDTH, height: HEIGHT, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((g) => { window.__UISHOT__ = { graph: g }; }, GRAPHS[name]);
  await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.react-flow__node', { timeout: 20000 }); await wait(2500); await applyTheme(page, 'dark'); await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' }); await wait(500);   // a still frame: the both-ways line's dash moves
  if (MODE === 'grid') { await clickText(page, 'Arrange'); await wait(300); await clickText(page, 'Dot grid'); await wait(1500); await page.keyboard.press('Escape'); await wait(300); }
  // The page's "assistant changed" banner comes and goes by timing and moves the canvas by 41 px, so both captures drop it and fit again.
  await page.evaluate(() => { const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); let n; while ((n = w.nextNode())) if (/^Your assistant changed/.test(n.textContent || '')) { let e = n.parentElement; while (e && e.getBoundingClientRect().width < innerWidth * 0.9) e = e.parentElement; if (e) e.style.display = 'none'; break; } });
  await wait(600); await page.evaluate(() => document.querySelector('.react-flow__controls-fitview')?.click()); await wait(1200);
  const first = await measure(page);
  await page.screenshot({ path: join(OUT, `${TAG}-${name}-${MODE}-${WIDTH}.png`) });
  await (await page.$('.xe-canvas')).screenshot({ path: join(OUT, `${TAG}-${name}-${MODE}-${WIDTH}-canvas.png`) });   // the canvas alone: the page banner above it comes and goes
  let fit = null;
  if (process.env.FIT) {
    await page.evaluate(() => document.querySelector('.react-flow__controls-fitview')?.click()); await wait(1200);
    fit = await measure(page);
    await page.screenshot({ path: join(OUT, `${TAG}-${name}-${MODE}-${WIDTH}-fit.png`) });
  }
  let hover = null;
  if (process.env.HOVER) {   // point at the first link tag and read which card is outlined
    const pos = await page.evaluate(() => { const g = document.querySelector('[data-link-tag]'); if (!g) return null; const r = g.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; });
    if (pos) { await page.mouse.move(pos.x, pos.y); await wait(400); hover = await page.evaluate(() => [...document.querySelectorAll('.sem-link-partner')].map((n) => n.getAttribute('data-id'))); await page.screenshot({ path: join(OUT, `${TAG}-${name}-${MODE}-${WIDTH}-hover.png`) }); }
  }
  results.push({ name, mode: MODE, first, fit, hover }); console.log(name, MODE, JSON.stringify({ first, fit, hover }));
  await page.close();
}
writeFileSync(join(OUT, `${TAG}-${MODE}-results.json`), JSON.stringify(results, null, 2));
await browser.close(); server.close();
