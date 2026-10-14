// Verifier's proof, round 2 of option B (m11/diagram-contrast, 27 September 2026). Written to FAIL on 59ff0dff and
// stay failing until the defects it names are fixed; it is not a gate the lane added. Like round 1's
// verify-surface-steps-drive.mjs it renders the real Studio bundle on the mock engine and reads every colour it
// judges FROM THE SCREENSHOT PIXELS, never from computed styles.
//   D. Content does not sit flush on the page (round 1's rule C, the addendum's "the Plan's rows"): the Plan's
//      What changed card, which every existing person sees above the list after an update, sits a visible step
//      off the page (CIE76 delta E 2.3) like the Next list right under it. plan-guide.css paints it var(--sem-bg).
//   E. A doer keeps its hue in every stock theme (colour level B: blue for Semanticus, orange for you, purple for
//      your assistant; the stock-theme doer colours are in this lane's brief). The You track's label ink has
//      oklch chroma 0.05 or more, as Semanticus's and your assistant's do. Abyss's find highlight, which VS Code
//      hands a webview as charts.orange, is grey (#eeeeee44), so its You orange comes out grey; the lane fixed the
//      same trap for Quiet Light's pink one with a source of its own and left Abyss (since retired: the person is blue now).
//   F. Text on a card holds 4.5:1 in the stock themes too (coordinator's D6 ruling names Solarized Light and Quiet
//      Light). The Optimize page's before and after chips (the old and new value of each fix, a 12% red or 14% green
//      wash over the card) carry muted text; in those two themes it reads 4.40 to 4.44:1 off the pixels.
//      test/token-contrast.test.mjs never puts text on those washes, so it passes.
//   node tools/uishot/verify-surface-r2-drive.mjs     (exit 1 while any check fails; OUTDIR=<dir> for the PNGs)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { inflateSync } from 'node:zlib';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser } from './browser.mjs';
import { applyTheme } from './themes.mjs';
import { planSeed } from './visual-scenes.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-surface-r2');
mkdirSync(OUT, { recursive: true });
const puppeteerPkg = createRequire(import.meta.url)('puppeteer-core');
const puppeteer = puppeteerPkg.default ?? puppeteerPkg;
const JND = 2.3;
const MIN_CHROMA = 0.05;

// ---- colour maths -------------------------------------------------------------------------------------------
const lin = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
function lab([r, g, b]) {
  const [R, G, B] = [lin(r), lin(g), lin(b)];
  const f = (t) => (t > 0.008856 ? Math.cbrt(t) : 7.787 * t + 16 / 116);
  const X = f((0.4124 * R + 0.3576 * G + 0.1805 * B) / 0.95047), Y = f(0.2126 * R + 0.7152 * G + 0.0722 * B), Z = f((0.0193 * R + 0.1192 * G + 0.9505 * B) / 1.08883);
  return [116 * Y - 16, 500 * (X - Y), 200 * (Y - Z)];
}
const dE = (a, b) => { const p = lab(a), q = lab(b); return Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]); };
function oklabChroma([r, g, b]) {
  const [R, G, B] = [lin(r), lin(g), lin(b)];
  const l = Math.cbrt(0.4122214708 * R + 0.5363325363 * G + 0.0514459929 * B), m = Math.cbrt(0.2119034982 * R + 0.6806995451 * G + 0.1073969566 * B), s = Math.cbrt(0.0883024619 * R + 0.2817188376 * G + 0.6299787005 * B);
  return Math.hypot(1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s);
}
const hex = (c) => '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase();

// PNG decode (zlib and the five row filters), as round 1's driver does it.
function decodePng(buf) {
  let o = 8, w = 0, h = 0, type = 0; const idat = [];
  while (o < buf.length) {
    const len = buf.readUInt32BE(o), kind = buf.toString('latin1', o + 4, o + 8), data = buf.subarray(o + 8, o + 8 + len);
    if (kind === 'IHDR') { w = data.readUInt32BE(0); h = data.readUInt32BE(4); type = data[9]; if (data[8] !== 8) throw new Error('PNG bit depth ' + data[8]); }
    else if (kind === 'IDAT') idat.push(data); else if (kind === 'IEND') break;
    o += 12 + len;
  }
  const bpp = type === 6 ? 4 : type === 2 ? 3 : 0;
  if (!bpp) throw new Error('PNG colour type ' + type);
  const raw = inflateSync(Buffer.concat(idat)), stride = w * bpp, px = Buffer.alloc(stride * h);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)), row = y * stride;
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? px[row + i - bpp] : 0, b = y ? px[row - stride + i] : 0, c = i >= bpp && y ? px[row - stride + i - bpp] : 0;
      const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c);
      const pred = f === 0 ? 0 : f === 1 ? a : f === 2 ? b : f === 3 ? (a + b) >> 1 : (pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
      px[row + i] = (src[i] + pred) & 255;
    }
  }
  return { w, h, bpp, px };
}
const pixels = (im, bx) => {
  const out = [];
  const x0 = Math.max(0, Math.round(bx.x)), y0 = Math.max(0, Math.round(bx.y)), x1 = Math.min(im.w, Math.round(bx.x + bx.w)), y1 = Math.min(im.h, Math.round(bx.y + bx.h));
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) { const i = (y * im.w + x) * im.bpp; out.push([im.px[i], im.px[i + 1], im.px[i + 2]]); }
  return out;
};
const mode = (px) => { const n = new Map(); for (const p of px) { const k = (p[0] << 16) | (p[1] << 8) | p[2]; n.set(k, (n.get(k) || 0) + 1); } let best = 0, bk = 0; for (const [k, v] of n) if (v > best) { best = v; bk = k; } return [(bk >> 16) & 255, (bk >> 8) & 255, bk & 255]; };

// ---- the page: the plan-guide driver's existing-person seed (history rows, guide flags answered from memory) ----
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const seed = `
${FIXTURE_SRC}
const __plan = makePlan({});
const __rows = Array.from({ length: 3 }, (_, i) => ({ id: 'e' + i, kind: 'edit', when: new Date(Date.now() - i * 60000).toISOString(),
  by: 'you', title: 'Hid a column', deltas: [], undoable: true, laterCount: i }));
let realApi;
Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
  get: () => function () {
    const api = realApi();
    const post = api.postMessage;
    api.postMessage = function (msg) {
      if (msg && (msg.type === 'getGuideFlags' || msg.type === 'setGuideFlag')) {
        if (msg.type === 'setGuideFlag') return;
        setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'guideFlags', id: msg.id, planTour: false, whatChanged: false } })), 0);
        return;
      }
      return post.call(this, msg);
    };
    return api;
  },
  set: (v) => { realApi = v; } });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getModelPlan', () => __plan);
  v.setResponse('getPlanHistory', () => ({ sessionId: 's', rows: __rows, more: false, kept: true }));
} });`;

if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) throw new Error('media/studio/studio.js is missing. Build the webview first.');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const url = `http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`;
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser({ quiet: true }), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function plan(theme) {
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed);
  await page.goto(url, { waitUntil: 'networkidle0', timeout: 45000 });
  await page.waitForSelector('nav button, [data-tab]', { timeout: 20000 });
  await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' });
  await page.mouse.move(1439, 899);
  await applyTheme(page, theme);
  await wait(1600);
  const png = Buffer.from(await page.screenshot({ encoding: 'base64' }), 'base64');
  writeFileSync(join(OUT, `plan-${theme}.png`), png);
  const boxes = await page.evaluate(() => {
    const R = (el) => { if (!el) return null; const r = el.getBoundingClientRect(); return r.width > 0 ? { x: r.x, y: r.y, w: r.width, h: r.height } : null; };
    const main = R(document.querySelector('main'));
    const track = (d) => R(document.querySelector(`[data-testid="plan-track-${d}"]`));
    return { wc: R(document.querySelector('.plan-wc')), list: R(document.querySelector('.pw-list')), main, you: track('you'), semanticus: track('semanticus'), assistant: track('assistant') };
  });
  await ctx.close();
  return { im: decodePng(png), boxes };
}

// The most chromatic ink in a track's label: pixels that differ from the track's own ground by at least a JND.
function inkChroma(im, box) {
  const px = pixels(im, { x: box.x + 2, y: box.y + 2, w: Math.min(160, box.w - 4), h: box.h - 4 });
  const ground = mode(px);
  let best = 0, bestPx = ground;
  for (const p of px) { if (dE(p, ground) < 12) continue; const c = oklabChroma(p); if (c > best) { best = c; bestPx = p; } }
  return { chroma: best, ink: bestPx, ground };
}

const results = [];
const record = (id, theme, ok, text) => { results.push({ id, theme, ok, text }); console.log(`${ok ? 'ok  ' : 'FAIL'} ${theme.padEnd(6)} ${id.padEnd(44)} ${text}`); };
for (const theme of ['dark', 'light']) {
  try {
    const { im, boxes } = await plan(theme);
    if (!boxes.wc) throw new Error('the What changed card did not render');
    const card = mode(pixels(im, { x: boxes.wc.x + boxes.wc.w * 0.25, y: boxes.wc.y + 4, w: 40, h: Math.max(4, boxes.wc.h - 8) }));
    const page = mode(pixels(im, { x: boxes.main.x + 2, y: boxes.main.y + 4, w: 14, h: boxes.main.h - 8 }));
    const list = boxes.list ? mode(pixels(im, { x: boxes.list.x + boxes.list.w - 30, y: boxes.list.y + 4, w: 20, h: 20 })) : null;
    const d = dE(card, page);
    record('D1 the Plan\'s What changed card', theme, d >= JND, `${hex(card)} vs page ${hex(page)}  dE ${d.toFixed(1)} (wanted ${JND} or more; the Next list beside it is ${list ? hex(list) : '?'})`);
  } catch (e) { record('D1 the Plan\'s What changed card', theme, false, 'could not measure: ' + String(e.message ?? e).slice(0, 120)); }
}
for (const theme of ['dark', 'abyss', 'soldark', 'light', 'quietlight']) {
  try {
    const { im, boxes } = await plan(theme);
    for (const d of ['semanticus', 'you', 'assistant']) {
      if (!boxes[d]) throw new Error(`the ${d} track did not render`);
      const k = inkChroma(im, boxes[d]);
      record(`E1 the ${d} track keeps its hue`, theme, k.chroma >= MIN_CHROMA, `ink ${hex(k.ink)} on ${hex(k.ground)}  oklch chroma ${k.chroma.toFixed(3)} (wanted ${MIN_CHROMA} or more)`);
    }
  } catch (e) { record('E1 doer hues', theme, false, 'could not measure: ' + String(e.message ?? e).slice(0, 120)); }
}
const lum = ([r, g, b]) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
const ratio = (p, q) => { const [a, b] = [lum(p), lum(q)].sort((x, y) => y - x); return (a + 0.05) / (b + 0.05); };
for (const theme of ['light', 'sollight', 'quietlight']) {
  try {
    const ctx = await browser.createBrowserContext();
    const page = await ctx.newPage();
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument(planSeed());
    await page.goto(url.replace('tab=plan', 'tab=optimize'), { waitUntil: 'networkidle0', timeout: 45000 });
    await wait(2600);
    await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' });
    await page.mouse.move(1439, 899);
    await applyTheme(page, theme);
    await wait(900);
    const png = Buffer.from(await page.screenshot({ encoding: 'base64' }), 'base64');
    writeFileSync(join(OUT, `optimize-${theme}.png`), png);
    // Each chip is measured whole on screen. Reading the chips off the first screen measured one the page's scroll
    // edge cut in half: its box was mostly the footer's grey and its "ink" the top row of two half-drawn glyphs
    // (join part 2, 30 Sep). So each chip is scrolled into the middle, its edges are checked to be on top (nothing
    // covers the chip and no scroller clips it), and only then are the pixels read.
    await page.evaluate(() => { window.__chips = () => [...document.querySelectorAll('.sem-card .px-1.rounded')].filter((el) => {
      const bg = getComputedStyle(el).backgroundColor, r = el.getBoundingClientRect();
      return el.textContent.trim() && r.width > 0 && r.height > 0 && !/rgba\([^)]*,\s*0\)$/.test(bg);
    }); });
    const count = await page.evaluate(() => window.__chips().length);
    if (!count) throw new Error('no before or after chip rendered');
    let worst = null;
    for (let k = 0; k < count; k++) {
      await page.evaluate((k) => window.__chips()[k].scrollIntoView({ block: 'center', inline: 'nearest' }), k);
      await wait(120);
      const c = await page.evaluate((k) => {
        const el = window.__chips()[k], r = el.getBoundingClientRect();
        const range = document.createRange(); range.selectNodeContents(el); const t = range.getBoundingClientRect();
        const on = (x, y) => { const hit = document.elementFromPoint(x, y); return !!hit && (hit === el || el.contains(hit)); };
        // The middle of each edge, not the corners: hit testing follows the chip's rounded corners.
        const whole = [[r.x + r.width / 2, r.y + 1], [r.x + r.width / 2, r.bottom - 1], [r.x + 1, r.y + r.height / 2], [r.right - 1, r.y + r.height / 2]].every(([x, y]) => on(x, y));
        return { text: el.textContent.trim(), whole, box: { x: r.x, y: r.y, w: r.width, h: r.height }, glyphs: { x: t.x, y: t.y, w: t.width, h: t.height } };
      }, k);
      if (!c.whole) throw new Error(`the chip "${c.text}" cannot be scrolled fully into view to be measured`);
      const im = decodePng(Buffer.from(await page.screenshot({ encoding: 'base64' }), 'base64'));
      const ground = mode(pixels(im, c.box));
      let ink = ground, far = 0;
      for (const p of pixels(im, c.glyphs)) { const d = Math.abs(lum(p) - lum(ground)); if (d > far) { far = d; ink = p; } }
      const r = ratio(ink, ground);
      if (!worst || r < worst.r) worst = { r, text: c.text, ink, ground };
    }
    await ctx.close();
    record('F1 text on the Optimize before and after chips', theme, worst.r >= 4.5, `lowest "${worst.text}" ${hex(worst.ink)} on ${hex(worst.ground)}  ${worst.r.toFixed(2)}:1 of ${count} chips, each measured whole on screen (wanted 4.5 or more)`);
  } catch (e) { record('F1 text on the Optimize before and after chips', theme, false, 'could not measure: ' + String(e.message ?? e).slice(0, 120)); }
}
await browser.close(); server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 1));
const bad = results.filter((r) => !r.ok);
console.log(`\n${results.length - bad.length}/${results.length} checks passed; shots in ${OUT}`);
process.exit(bad.length ? 1 : 0);
