// Timing driver for Lineage > Graph on a big monitoring-style model (and a small one, for comparison).
// It opens Lineage (Tree), then does a REAL mouse click on Graph and records, from inside the page:
//   cold / warm    the FIRST Graph switch of the session, and a later one. Each has:
//     firstFrame   ms from the click to the first frame painted after it (rAF, so a blocked thread shows up here)
//     settled      ms from the click until the graph has node positions and the Drawing state is over
//     longMax/longCount/longTotal  main-thread tasks over 50 ms (PerformanceObserver longtask) after the click
//     maxStall     the longest gap in a 10 ms heartbeat (what a person feels as a freeze)
//     cpuMs        renderer CPU time (does not move with how busy the machine is)
//   treeDelay      a REAL click on Tree 100 ms after a Graph click, while the graph is working: ms the browser held it//   treeBackMs     a REAL click on Tree 150 ms after the Graph click: ms the browser held it before the page ran it
//   layoutHash     a hash of the settled node positions (two runs must match: the picture is stable)
// Usage: node lineage-perf-drive.mjs [big|small] [--throttle 4] [--webroot <Semanticus.VSCode dir>] [--shot out.png] [--json out.json] [--runs 2]
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
const throttle = Number(arg('throttle', '1'));
const webRoot = resolve(arg('webroot', resolve(__dir, '..', '..')));
const runs = Number(arg('runs', '2'));
const shotPath = arg('shot', null);
const jsonPath = arg('json', null);
const treeShotPath = arg('treeshot', null);
const wholeShotPath = arg('wholeshot', null);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };

const fx = which === 'small' ? makeMonitoringLineage({ tables: 5 }) : makeMonitoringLineage();
console.log(`fixture ${which}: ${JSON.stringify(fx.stats)}  throttle=${throttle}x  webroot=${webRoot}`);

const server = await new Promise((r) => { const s = createServer((req, res) => {
  try {
    const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
    if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
  } catch { res.writeHead(500); res.end(); }
}).listen(0, '127.0.0.1', () => r(s)); });
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars'] });

// Probe installed before any page script runs.
const PROBE = () => {
  const P = window.__lp = { long: [], stall: 0, inputs: [], clickAt: 0, first: 0, settled: 0 };
  try { new PerformanceObserver((l) => { for (const e of l.getEntries()) P.long.push([Math.round(e.startTime), Math.round(e.duration)]); }).observe({ type: 'longtask', buffered: true }); } catch {}
  let last = performance.now();
  setInterval(() => { const n = performance.now(); if (P.clickAt) P.stall = Math.max(P.stall, n - last - 10); last = n; }, 10);
};

async function once(label) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.evaluateOnNewDocument((f) => { window.__ECHARTS_TEST__ = 1; window.__UISHOT__ = { lineage: f.lineage, unused: f.unused, reportAnalysis: f.reportAnalysis }; }, fx);
  await page.evaluateOnNewDocument(PROBE);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html#Lineage`, { waitUntil: 'load' });
  await applyTheme(page, 'dark');
  const btn = async (text) => page.evaluateHandle((t) => [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === t && b.getClientRects().length), text);
  const has = (l) => page.waitForFunction((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l), { timeout: 20000 }, l);
  const click = async (l) => { await has(l); await page.evaluate((l) => [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === l).click(), l); };
  await page.waitForSelector('nav button', { timeout: 20000 });
  await page.waitForFunction(() => ['Explore', 'Model', 'Understand'].some((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l)), { timeout: 20000 });
  const group = await page.evaluate(() => ['Explore', 'Model', 'Understand'].find((l) => [...document.querySelectorAll('button')].some((b) => (b.textContent || '').trim() === l)));
  await click(group); await click('Lineage'); await has('Graph');
  await sleep(1500);   // let the Tree settle so only the Graph switch is measured
  if (throttle > 1) await (await page.createCDPSession()).send('Emulation.setCPUThrottlingRate', { rate: throttle });
  await page.evaluate(() => {
    const P = window.__lp;
    const grab = () => { const dom = document.querySelector('[_echarts_instance_]'); return dom && window.__ECHARTS__?.getInstanceByDom(dom); };
    const hasLayout = () => { try { const d = grab()?.getModel().getSeriesByIndex(0)?.getData(); return !!(d && d.count() > 0 && d.getItemLayout(0)); } catch { return false; } };
    const drawing = () => document.querySelector('.lineage-graph-canvas')?.getAttribute('data-drawing') === '1';
    // arm(): forget what came before; the next pointerdown is the click being measured
    P.arm = () => { P.long.length = 0; P.stall = 0; P.clickAt = 0; P.first = 0; P.settled = 0; P.treeDelay = null; };
    document.addEventListener('pointerdown', (e) => { if (!P.clickAt) { P.clickAt = e.timeStamp; P.stall = 0; requestAnimationFrame(() => requestAnimationFrame(() => { P.first = performance.now(); })); } }, true);
    document.addEventListener('click', (e) => { const b = e.target.closest && e.target.closest('button'); if (b && (b.textContent || '').trim() === 'Tree' && P.treeDelay == null) P.treeDelay = Math.round(performance.now() - e.timeStamp); }, true);
    const tick = () => { if (P.clickAt && !P.settled && hasLayout() && !drawing()) P.settled = performance.now(); requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    P.snap = () => { const long = P.long.filter((x) => x[1] >= 50).map((x) => [x[0] - Math.round(P.clickAt), x[1]]).filter((x) => x[0] >= -5);
      return { firstFrame: P.first ? Math.round(P.first - P.clickAt) : null, settled: P.settled ? Math.round(P.settled - P.clickAt) : null,
        longCount: long.length, longMax: long.reduce((m, x) => Math.max(m, x[1]), 0), longTotal: long.reduce((t, x) => t + x[1], 0), maxStall: Math.round(P.stall), longList: long }; };
  });
  const realClick = async (label) => { const h = await btn(label); const r = await h.boundingBox(); await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2); };
  const settle = () => page.waitForFunction(() => window.__lp.settled > 0, { timeout: 150000, polling: 100 }).catch(() => {});
  const cdp = process.argv.includes('--profile') ? await page.createCDPSession() : null;
  // 1. COLD: the first Graph switch of the session (first time the chart starts)
  await page.evaluate(() => window.__lp.arm());
  const cpu0 = await rendererCpu();
  await realClick('Graph'); await settle(); await sleep(300);
  const cold = await page.evaluate(() => window.__lp.snap());
  const treeBox = await (await btn('Tree')).boundingBox();   // where Tree sits while Graph is showing: read now, because a busy page cannot be asked later
  cold.cpuMs = Math.round(await rendererCpu() - cpu0);
  // 2. back to Tree
  await realClick('Tree'); await page.waitForSelector('[data-testid="lineage-tree"][aria-pressed="true"]', { timeout: 150000 }).catch(() => {}); await sleep(1200);
  if (treeShotPath && label === 'last') await page.screenshot({ path: treeShotPath });
  // 3. RESPONSIVE: Graph, then a real Tree click 100 ms later, while the graph is still working. treeDelay = how long the browser held the click.
  await page.evaluate(() => window.__lp.arm());
  // Neither click is awaited: a blocked page would hold the first one's reply and hide the very delay being measured.
  const graphBox = await (await btn('Graph')).boundingBox();
  const g = page.mouse.click(graphBox.x + graphBox.width / 2, graphBox.y + graphBox.height / 2);
  await sleep(100);
  const t = page.mouse.click(treeBox.x + treeBox.width / 2, treeBox.y + treeBox.height / 2);
  await Promise.all([g, t]);
  await page.waitForFunction(() => window.__lp.treeDelay != null, { timeout: 150000, polling: 50 }).catch(() => {});
  const treeDelay = await page.evaluate(() => window.__lp.treeDelay);
  await sleep(1500);
  // 4. WARM: Graph again, wait until it is drawn
  if (cdp) { await cdp.send('Profiler.enable'); await cdp.send('Profiler.setSamplingInterval', { interval: 500 }); await cdp.send('Profiler.start'); }
  await page.evaluate(() => window.__lp.arm());
  const cpu1 = await rendererCpu();
  await realClick('Graph'); await settle(); await sleep(400);
  const warm = await page.evaluate(() => window.__lp.snap());
  warm.cpuMs = Math.round(await rendererCpu() - cpu1);
  if (cdp) { const { profile } = await cdp.send('Profiler.stop'); const self = new Map(); const byId = new Map(profile.nodes.map((n) => [n.id, n])); const dt = profile.timeDeltas; profile.samples.forEach((id, i) => { const n = byId.get(id); const k = n.callFrame.functionName + ' ' + (n.callFrame.url.split('/').pop() || '') + ':' + n.callFrame.lineNumber; self.set(k, (self.get(k) || 0) + (dt[i] || 0) / 1000); }); console.log('TOP SELF (ms):'); [...self.entries()].sort((a, b) => b[1] - a[1]).slice(0, 18).forEach(([k, v]) => console.log('  ' + v.toFixed(0).padStart(6) + '  ' + k)); }
  if (shotPath && label === 'last') { if (throttle > 1) await (await page.createCDPSession()).send('Emulation.setCPUThrottlingRate', { rate: 1 }); await sleep(600); await page.screenshot({ path: shotPath }); }
  // 5. WHOLE MODEL: every thing on the page (850 on the big fixture), the heaviest picture this view draws
  let whole = null;
  if (process.argv.includes('--whole')) {
    await page.evaluate(() => window.__lp.arm());
    await realClick('Whole model');
    await page.waitForFunction(() => { const dom = document.querySelector('[_echarts_instance_]'); const inst = dom && window.__ECHARTS__?.getInstanceByDom(dom); let n = 0; try { n = inst.getModel().getSeriesByIndex(0).getData().count(); } catch {} if (n > 500 && document.querySelector('.lineage-graph-canvas')?.getAttribute('data-drawing') === '0') { window.__lp.settled = performance.now(); return true; } return false; }, { timeout: 150000, polling: 50 }).catch(() => {});
    await sleep(400);
    whole = await page.evaluate(() => window.__lp.snap());
    whole.shown = await page.evaluate(() => (document.body.innerText.match(/(\d+)( of \d+)? things/) || [])[0] || null);
    if (wholeShotPath && label === 'last') await page.screenshot({ path: wholeShotPath });
  }
  const r = await page.evaluate(() => {
    const dom = document.querySelector('[_echarts_instance_]'); const inst = dom && window.__ECHARTS__.getInstanceByDom(dom);
    let hash = null, n = 0, pos = [];
    if (inst) { const d = inst.getModel().getSeriesByIndex(0).getData(); n = d.count();
      for (let i = 0; i < n; i++) { const l = d.getItemLayout(i); if (l) pos.push(d.getId(i) + ':' + Math.round(l[0]) + ',' + Math.round(l[1])); }
      pos.sort(); let h = 5381; const s = pos.join('|'); for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) | 0; hash = (h >>> 0).toString(16); }
    return { nodes: n, hash, shown: (document.body.innerText.match(/(\d+)( of \d+)? things/) || [])[0] || null };
  });
  r.cold = cold; r.warm = warm; r.whole = whole; r.treeDelay = treeDelay; r.errors = errors.slice(0, 3);
  await page.close();
  return r;
}

const bsess = await browser.target().createCDPSession();
const rendererCpu = async () => { const { processInfo } = await bsess.send('SystemInfo.getProcessInfo'); return processInfo.filter((p) => p.type === 'renderer').reduce((t, p) => t + p.cpuTime, 0) * 1000; };
const results = [];
for (let i = 0; i < runs; i++) { const r = await once(i === runs - 1 ? 'last' : 'run'); results.push(r); { const f = (m) => `first ${m.firstFrame} settled ${m.settled} longMax ${m.longMax} (n=${m.longCount}) stall ${m.maxStall} cpu ${m.cpuMs}ms`; console.log(`run ${i + 1}: cold[${f(r.cold)}] warm[${f(r.warm)}] treeDelay ${r.treeDelay}ms ${r.whole ? 'WHOLE[' + f(r.whole) + ' ' + r.whole.shown + '] ' : ''}nodes ${r.nodes} hash ${r.hash}`, JSON.stringify(r.warm.longList)); } }
console.log('layout stable across runs:', new Set(results.map((r) => r.hash)).size === 1 ? 'YES' : 'NO');
if (jsonPath) writeFileSync(jsonPath, JSON.stringify({ which, throttle, stats: fx.stats, results }, null, 2));
await browser.close(); server.close();
