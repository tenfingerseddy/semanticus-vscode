// Before/after captures for the 1.5 visuals lane: the Diagram's default layout on a Contoso-style 4-fact model and a
// 20-table model, dark, at 1440 and 1920. Also writes the line-crossing count of each, measured on the live DOM.
//   OUTDIR=<dir> ONLY=contoso,twenty LAYOUT=<menu label> node tools/uishot/visuals-shots.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots', 'visuals');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const only = (process.env.ONLY || 'contoso,twenty').split(',');
const clickText = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith(t) && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
const probe = (page) => page.evaluate(() => {
  const lines = [...document.querySelectorAll('.react-flow__edge')].map((g) => {
    const d = g.querySelector('path.react-flow__edge-path')?.getAttribute('d') ?? '';
    const nums = (d.match(/-?\d+(\.\d+)?/g) || []).map(Number);
    const pts = []; for (let i = 0; i + 1 < nums.length; i += 2) pts.push({ x: nums[i], y: nums[i + 1] });
    return pts;
  });
  const segs = lines.map((pts) => pts.slice(1).map((b, i) => [pts[i], b]));
  let crossings = 0;
  const cross = (s, t) => {   // orthogonal segments cross when one is horizontal, the other vertical, and they meet inside both
    const h = (q) => Math.abs(q[0].y - q[1].y) < 0.5, v = (q) => Math.abs(q[0].x - q[1].x) < 0.5;
    const inside = (a, b, c) => c > Math.min(a, b) + 1 && c < Math.max(a, b) - 1;
    if (h(s) && v(t)) return inside(s[0].x, s[1].x, t[0].x) && inside(t[0].y, t[1].y, s[0].y);
    if (v(s) && h(t)) return inside(t[0].x, t[1].x, s[0].x) && inside(s[0].y, s[1].y, t[0].y);
    return false;
  };
  for (let i = 0; i < segs.length; i++) for (let j = i + 1; j < segs.length; j++)
    for (const s of segs[i]) for (const t of segs[j]) if (cross(s, t)) crossings++;
  const cards = [...document.querySelectorAll('.react-flow__node')].length;
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
  return { cards, lines: lines.length, crossings, zoom };
});
const results = [];
for (const g of only) for (const [w, h] of (g === 'plan' ? [[1490, 900], [1920, 1080]] : [[1440, 900], [1920, 1080]])) {
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${g === 'plan' ? 'conn=1&tier=pro&tab=plan' : `g=${g}&tab=diagram`}`, { waitUntil: 'networkidle0', timeout: 30000 });
  if (g === 'plan') await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 }); else await page.waitForSelector('.react-flow__node', { timeout: 20000 });
  await wait(2500);
  await applyTheme(page, 'dark');
  if (process.env.LAYOUT) {
    await clickText(page, 'Arrange'); await wait(300);
    await clickText(page, process.env.LAYOUT); await wait(1200);
  }
  await page.evaluate(() => document.querySelector('.react-flow__controls-fitview')?.click()); await wait(1000);
  const m = g === 'plan' ? await page.evaluate(() => { const els = [...document.querySelectorAll('.pw-src .pw-trunc')]; return { sources: els.map((el) => ({ text: el.textContent, clipped: el.scrollWidth > el.clientWidth })), clipped: els.filter((el) => el.scrollWidth > el.clientWidth).length }; }) : await probe(page);
  const file = `${process.env.TAG || 'shot'}-${g}-${w}.png`;
  await page.screenshot({ path: join(OUT, file) });
  results.push({ file, ...m });
  console.log(file, JSON.stringify(m));
  await ctx.close();
}
writeFileSync(join(OUT, `${process.env.TAG || 'shot'}-results.json`), JSON.stringify(results, null, 2));
await browser.close(); server.close();
