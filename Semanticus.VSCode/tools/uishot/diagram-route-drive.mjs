// Driven proof for the Diagram under the space rule (27 Sep): the bus matrix wraps its lookup tables into rows to
// fit the screen, and every relationship line is routed through the gaps between cards, never behind one.
// Serves the built bundle, opens the real harness with a 4-table, an 11-table and a 30-table model at 2000x1200,
// 1440x900 and 900x900, collapsed and with every card open, in Dark Modern and Light Modern. On the live DOM it
// measures every card and every drawn line in flow coordinates and fails when a line segment passes through a card's
// box, a line is not a routed right-angle path, or two cards overlap. It also pins the wrap itself: for the 11 and
// 30-table models the whole diagram's shape must come within 1.5x of the canvas's (a one-row strip is 2.2x or
// more; m11/diagram-route verification D4). Writes a PNG per case and results.json.
//   node diagram-route-drive.mjs            (OUTDIR=<dir> to choose where the PNGs and results.json land)
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'diagram-route');
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
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// Cards and lines in FLOW coordinates. A line's path must be M/L only (the router's output); anything else means
// it fell back to the old step line, which is reported, not silently measured.
const probe = (page) => page.evaluate(() => {
  const vp = document.querySelector('.react-flow__viewport');
  const zoom = vp ? Number((/scale\(([^)]+)\)/.exec(vp.style.transform) || [0, 1])[1]) : 1;
  const cards = [...document.querySelectorAll('.react-flow__node')].map((el) => {
    const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(el.style.transform) || [0, 0, 0];
    const r = el.getBoundingClientRect();
    return { id: el.getAttribute('data-id'), x: Number(m[1]), y: Number(m[2]), w: r.width / zoom, h: r.height / zoom,
      open: !!el.querySelector('button[aria-label^="Hide the columns of"]') };
  });
  const lines = [...document.querySelectorAll('.react-flow__edge')].map((g) => {
    const d = g.querySelector('path.react-flow__edge-path')?.getAttribute('d') ?? '';
    const routed = /^M[-\d.,\sL]+$/.test(d.trim());
    const nums = (d.match(/-?\d+(\.\d+)?/g) || []).map(Number);
    const pts = []; for (let i = 0; i + 1 < nums.length; i += 2) pts.push({ x: nums[i], y: nums[i + 1] });
    return { id: g.getAttribute('data-id') || g.getAttribute('data-testid') || '', routed, pts };
  });
  const fr = document.querySelector('.react-flow').getBoundingClientRect();
  return { cards, lines, canvas: { w: fr.width, h: fr.height } };
});
const overlapsOf = (cards) => {
  const out = [];
  for (let i = 0; i < cards.length; i++) for (let j = i + 1; j < cards.length; j++) {
    const a = cards[i], b = cards[j];
    if (a.x < b.x + b.w - 0.5 && b.x < a.x + a.w - 0.5 && a.y < b.y + b.h - 0.5 && b.y < a.y + a.h - 0.5) out.push(a.id + ' / ' + b.id);
  }
  return out;
};
// The driver's own crossing check (strict inside, half a pixel of tolerance for rounding).
const crossingsOf = (lines, cards) => {
  const out = [];
  for (const l of lines) for (let i = 1; i < l.pts.length; i++) {
    const a = l.pts[i - 1], b = l.pts[i];
    const x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y);
    for (const c of cards) if (x0 < c.x + c.w - 0.5 && x1 > c.x + 0.5 && y0 < c.y + c.h - 0.5 && y1 > c.y + 0.5) out.push(`${l.id} through ${c.id}`);
  }
  return [...new Set(out)];
};
const diagonal = (lines) => lines.filter((l) => l.pts.some((p, i) => i && Math.abs(p.x - l.pts[i - 1].x) > 0.5 && Math.abs(p.y - l.pts[i - 1].y) > 0.5)).map((l) => l.id);
const clickText = (page, text) => page.evaluate((t) => {
  const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  if (b) b.click(); return !!b;
}, text);
// Expand all sits on the row on a wide canvas and inside the Arrange menu on a narrow one.
const pick = async (page, text) => {
  if (await clickText(page, text)) return true;
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith('Arrange')); if (b) b.click(); });
  await wait(300);
  return clickText(page, text);
};

const SIZES = [['wide', 2000, 1200], ['laptop', 1440, 900], ['narrow', 900, 900]];
const GRAPHS = [['four', 4], ['eleven', 11], ['thirty', 30]];
const results = [];
let failed = 0;
const check = (id, step, ok, detail) => { results.push({ id, step, ok, detail }); if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${step}${detail ? ': ' + detail : ''}`); };

for (const theme of ['dark', 'light']) for (const [g, n] of GRAPHS) for (const [size, w, h] of SIZES) {
  const ctx = await browser.createBrowserContext();   // fresh storage: every case opens the default bus matrix
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?g=${g}&tab=diagram`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('.react-flow__node', { timeout: 20000 });
  await wait(2500);
  await applyTheme(page, theme);
  const id = `${theme}-${g}-${size}`;
  for (const state of ['collapsed', 'expanded']) {
    if (state === 'expanded') { check(id, 'Expand all found', await pick(page, 'Expand all'), ''); await wait(1800); }
    await clickText(page, 'Fit'); await wait(900);
    const p = await probe(page);
    await page.screenshot({ path: join(OUT, `${id}-${state}.png`) });
    const bb = { w: Math.max(...p.cards.map((c) => c.x + c.w)) - Math.min(...p.cards.map((c) => c.x)), h: Math.max(...p.cards.map((c) => c.y + c.h)) - Math.min(...p.cards.map((c) => c.y)) };
    check(id, `${state}: every table is on the canvas`, p.cards.length === n, `${p.cards.length}/${n}`);
    if (state === 'expanded') check(id, 'expanded: every card is open', p.cards.every((c) => c.open), `${p.cards.filter((c) => c.open).length}/${p.cards.length}`);
    check(id, `${state}: no card overlaps another`, overlapsOf(p.cards).length === 0, overlapsOf(p.cards).join(', '));
    check(id, `${state}: every line is routed`, p.lines.length > 0 && p.lines.every((l) => l.routed), p.lines.filter((l) => !l.routed).map((l) => l.id).join(', '));
    check(id, `${state}: every segment is horizontal or vertical`, diagonal(p.lines).length === 0, diagonal(p.lines).join(', '));
    check(id, `${state}: no line runs behind a card`, crossingsOf(p.lines, p.cards).length === 0, crossingsOf(p.lines, p.cards).join(', '));
    const shape = (bb.w / bb.h) / (p.canvas.w / p.canvas.h);
    if (state === 'collapsed' && g !== 'four') check(id, 'collapsed: the wrap brings the diagram within 1.5x of the canvas shape', shape < 1.5 && shape > 1 / 1.5, `diagram ${Math.round(bb.w)} x ${Math.round(bb.h)} on a ${Math.round(p.canvas.w)} x ${Math.round(p.canvas.h)} canvas, shape ${shape.toFixed(2)}`);
    else console.log(`     ${id} ${state}: diagram ${Math.round(bb.w)} x ${Math.round(bb.h)}, shape ${shape.toFixed(2)} of the canvas, ${p.lines.length} lines`);
  }
  check(id, 'no page errors', errors.length === 0, errors.join(' | '));
  await ctx.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failed, results }, null, 2));
console.log(`\n${results.filter((r) => r.ok).length} passed, ${failed} failed. PNGs + results.json in ${OUT}`);
await browser.close();
server.close();
process.exit(failed ? 1 : 0);
