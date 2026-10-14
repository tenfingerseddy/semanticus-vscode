// Same nodes and edges? Opens Lineage > Graph on a fixture and writes the node ids and the links the chart holds, so two
// bundles (before and after a change) can be compared as sets. Draws the default focus, then Whole model with --whole.
// Usage: node lineage-sets-drive.mjs [big|small] --webroot <Semanticus.VSCode dir> --out sets.json [--whole] [--width 1440] [--shot graph.png] [--treeshot tree.png]
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, extname, normalize } from 'node:path';
import { existsSync, statSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { makeMonitoringLineage } from './fixtures/lineage-monitoring-fixture.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const __dir = dirname(fileURLToPath(import.meta.url));
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const which = process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : 'big';
const webRoot = resolve(arg('webroot', resolve(__dir, '..', '..')));
const out = arg('out', null);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const fx = which === 'small' ? makeMonitoringLineage({ tables: 5 }) : makeMonitoringLineage();
const server = await new Promise((r) => { const s = createServer((req, res) => {
  try { const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
    if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p)); } catch { res.writeHead(500); res.end(); }
}).listen(0, '127.0.0.1', () => r(s)); });
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb'] });
const page = await browser.newPage();
await page.setViewport({ width: Number(arg('width', '1440')), height: 900, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument((f) => { window.__ECHARTS_TEST__ = 1; window.__UISHOT__ = { lineage: f.lineage, unused: f.unused, reportAnalysis: f.reportAnalysis }; }, fx);
await page.goto(`http://127.0.0.1:${server.address().port}/tools/uishot/harness.html#Lineage`, { waitUntil: 'load' });
await applyTheme(page, 'dark');
const click = async (l) => { await page.waitForFunction((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l), { timeout: 30000 }, l);
  await page.evaluate((l) => [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === l).click(), l); };
await page.waitForSelector('nav button', { timeout: 30000 });
const group = await page.waitForFunction(() => ['Explore', 'Model', 'Understand'].find((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l)), { timeout: 30000 }).then((h) => h.jsonValue());
await click(group); await click('Lineage'); await click('Graph');
const grab = () => page.evaluate(() => { const dom = document.querySelector('[_echarts_instance_]'); const inst = dom && window.__ECHARTS__.getInstanceByDom(dom); const s = inst.getModel().getSeriesByIndex(0);
  const d = s.getData(); const ids = []; for (let i = 0; i < d.count(); i++) ids.push(d.getId(i)); const g = s.getGraph ? s.getGraph() : null;
  const links = (s.option.links || s.option.edges || []).map((l) => l.source + '>' + l.target);
  return { ids: ids.sort(), links: links.sort() }; });
// Settled = the chart holds nodes and the page is not in its Drawing state (the older bundle has no such state: then the nodes are enough).
await page.waitForFunction(() => { const dom = document.querySelector('[_echarts_instance_]'); const inst = dom && window.__ECHARTS__?.getInstanceByDom(dom); let n = 0; try { n = inst.getModel().getSeriesByIndex(0).getData().count(); } catch {} return n > 0 && document.querySelector('.lineage-graph-canvas')?.getAttribute('data-drawing') !== '1'; }, { timeout: 150000, polling: 100 });
await sleep(1500);
const result = { which, focus: await grab() };
if (process.argv.includes('--whole')) { await click('Whole model'); await sleep(8000); result.whole = await grab(); }
if (arg('shot', null)) { await sleep(600); await page.screenshot({ path: arg('shot', null) }); }
if (arg('treeshot', null)) {
  // On a narrow window the Graph/Tree switch folds into one menu button named after the open view.
  const folded = await page.evaluate(() => ![...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === 'Tree'));
  if (folded) { await page.evaluate(() => [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim().startsWith('Graph')).click()); await sleep(300); }
  await click('Tree'); await sleep(2500); await page.screenshot({ path: arg('treeshot', null) }); }
if (out) writeFileSync(out, JSON.stringify(result));
console.log(`${which}: focus ${result.focus.ids.length} nodes ${result.focus.links.length} links${result.whole ? `; whole ${result.whole.ids.length} nodes ${result.whole.links.length} links` : ''}`);
await browser.close(); server.close();
