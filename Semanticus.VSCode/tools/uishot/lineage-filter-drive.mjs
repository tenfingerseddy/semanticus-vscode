// The Filter menu on Lineage (Graph and Tree) must open fully inside the view, wide enough for its content, with no
// sideways scroll, under its own button, and its Tables and Fields lists must open INSIDE the menu (Kane, 12 Oct 20:42:
// the labels were cut at the left edge, there was a horizontal scrollbar and the field list was clipped).
// Usage: node lineage-filter-drive.mjs [graph|tree] [--webroot dir] [--shot out.png] [--width 1440]
// Exit 1 when any expectation fails.
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, extname, normalize } from 'node:path';
import { existsSync, statSync, readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { makeMonitoringLineage } from './fixtures/lineage-monitoring-fixture.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const __dir = dirname(fileURLToPath(import.meta.url));
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const view = process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : 'graph';
const webRoot = resolve(arg('webroot', resolve(__dir, '..', '..')));
const width = Number(arg('width', '1440'));
const shotPath = arg('shot', null);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const fx = makeMonitoringLineage();
const server = await new Promise((r) => { const s = createServer((req, res) => {
  try { const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
    if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p)); } catch { res.writeHead(500); res.end(); }
}).listen(0, '127.0.0.1', () => r(s)); });
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb'] });
const page = await browser.newPage();
await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument((f) => { window.__ECHARTS_TEST__ = 1; window.__UISHOT__ = { lineage: f.lineage, unused: f.unused, reportAnalysis: f.reportAnalysis }; }, fx);
await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html#Lineage`, { waitUntil: 'load' });
await applyTheme(page, 'dark');
const has = (l) => page.waitForFunction((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l), { timeout: 30000 }, l);
const click = async (l, starts = false) => { await page.waitForFunction((l, s) => [...document.querySelectorAll('button')].some((b) => { const t = (b.textContent || '').trim(); return s ? t.startsWith(l) : t === l; }), { timeout: 30000 }, l, starts);
  await page.evaluate((l, s) => [...document.querySelectorAll('button')].find((b) => { const t = (b.textContent || '').trim(); return s ? t.startsWith(l) : t === l; }).click(), l, starts); };
await page.waitForSelector('nav button', { timeout: 30000 });
const group = await page.waitForFunction(() => ['Explore', 'Model', 'Understand'].find((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l)), { timeout: 30000 }).then((h) => h.jsonValue());
await click(group); await click('Lineage'); await has('Graph');
if (view === 'graph') { await click('Graph'); await page.waitForSelector('[_echarts_instance_]', { timeout: 30000 }); await page.waitForFunction(() => document.querySelector('.lineage-graph-canvas')?.getAttribute('data-drawing') !== '1', { timeout: 60000 }); }
else { await click('Tree'); await sleep(1500); }
await sleep(600);
await click('Filter', true); await sleep(300);
await click('Tables', true); await sleep(300);
await click('Fields', true); await sleep(400);
if (shotPath) await page.screenshot({ path: shotPath });
// The row can move under an open menu (a view that finishes drawing, a banner that goes). The menu must follow its button.
await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim().startsWith('Filter')); Object.assign(b.parentElement.style, { position: 'relative', left: '-96px', top: '-20px' }); });
await sleep(400);
const m = await page.evaluate(() => {
  const pop = document.querySelector('.sem-rowmenu-pop'); if (!pop) return { err: 'the Filter menu did not open' };
  const root = document.querySelector('.studio-root') || document.documentElement;
  const rr = root.getBoundingClientRect(), pr = pop.getBoundingClientRect();
  const btn = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim().startsWith('Filter'));
  const br = btn.getBoundingClientRect();
  const inputs = [...pop.querySelectorAll('input[placeholder^="Filter "]')];
  const lists = [...pop.querySelectorAll('input[type=checkbox]')].map((c) => c.closest('label')).filter(Boolean);
  const outside = lists.filter((l) => { const r = l.getBoundingClientRect(); return r.left < pr.left - 0.5 || r.right > pr.right + 0.5; }).length;
  const labels = lists.filter((l) => { const s = l.querySelector('span.truncate'); return s && s.scrollWidth > s.clientWidth + 1 && s.getBoundingClientRect().width < 60; }).length;
  return { menu: { left: Math.round(pr.left), right: Math.round(pr.right), top: Math.round(pr.top), bottom: Math.round(pr.bottom), width: Math.round(pr.width) },
    root: { left: Math.round(rr.left), right: Math.round(rr.right), bottom: Math.round(rr.bottom) }, btn: { left: Math.round(br.left), right: Math.round(br.right), bottom: Math.round(br.bottom) },
    hScroll: pop.scrollWidth > pop.clientWidth + 1, vScroll: pop.scrollHeight > pop.clientHeight + 1, searchBoxes: inputs.length,
    searchInside: inputs.every((i) => { const r = i.getBoundingClientRect(); return r.left >= pr.left && r.right <= pr.right; }), checkRows: lists.length, rowsOutside: outside, narrowLabels: labels };
});
console.log(JSON.stringify(m));
const fails = [];
if (m.err) fails.push(m.err); else {
  if (m.menu.left < m.root.left || m.menu.right > m.root.right) fails.push(`menu leaves the view (${m.menu.left}..${m.menu.right} in ${m.root.left}..${m.root.right})`);
  if (m.menu.width < 280) fails.push(`menu is narrow (${m.menu.width}px)`);
  if (m.hScroll) fails.push('the menu scrolls sideways');
  if (Math.abs(m.menu.right - m.btn.right) > 12 && Math.abs(m.menu.left - m.btn.left) > 12 || Math.abs(m.menu.top - m.btn.bottom) > 12) fails.push(`menu is not aligned to its button (menu ${m.menu.left}..${m.menu.right}, button ${m.btn.left}..${m.btn.right})`);
  if (m.searchBoxes < 2) fails.push(`both lists should be open inside the menu (found ${m.searchBoxes})`);
  if (!m.searchInside) fails.push('a list search box sticks out of the menu');
  if (m.rowsOutside) fails.push(`${m.rowsOutside} list rows stick out of the menu`);
  if (m.narrowLabels) fails.push(`${m.narrowLabels} field names are squeezed to nothing`);
}
console.log(fails.length ? 'FAIL: ' + fails.join('; ') : `PASS (${view})`);
await browser.close(); server.close();
process.exit(fails.length ? 1 : 0);
