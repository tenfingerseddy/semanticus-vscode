// verify (m11/header-stable round 2): the assistant's feed mark must never cover a focus ring, and it must really be
// painted when live and not when idle. The mark is a badge centred on the top-right corner of "Your assistant"
// (styles.css .studio-feed-mark): it overhangs the chip by 2px up and right, plus a 1px ring in the page colour. The
// chip's own focus ring (button:focus-visible, 2px at a 2px offset) runs through that corner, and so does the ring of
// the control to its right (the approvals or Plan count, 4px away; Publish at fold 2 and deeper). A positioned badge
// paints over both.
//
// How it is judged: the same page, zoom, width and focus are shot at 4x idle and then live. The only thing that
// changes between the two shots is the badge, so a pixel that is the focus ring's colour idle and is not live is a
// piece of focus ring the badge covered. The badge's own pixels are counted too: most of its disc must be the text
// colour live, and none of it idle (a transparent or unpainted badge passed header-drive's computed-style check).
//
// Read-only against the app. Writes the 4x crops it judged to OUTDIR. Exits 1 on any failure.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(tmpdir(), 'semanticus-feed-mark-focus');
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
const DPR = 4;
const ZOOMS = [80, 100, 130];
// The approvals count is the chip's neighbour in the harness's default state; with no approvals waiting it is Publish,
// whose gap to the chip's group is 4px from fold 2 down (1000px at 100% is fold 3).
const CASES = [
  { slug: 'approvals-1300', width: 1300, approvals: true },
  { slug: 'publish-1000', width: 1000, approvals: false },
];

// Pixels come back through a blank page's canvas, so the check needs no image library.
const decoder = await browser.newPage();
const pixels = (b64) => decoder.evaluate(async (data) => {
  const img = new Image(); img.src = 'data:image/png;base64,' + data; await img.decode();
  const c = document.createElement('canvas'); c.width = img.width; c.height = img.height;
  const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0);
  return { w: img.width, h: img.height, d: Array.from(ctx.getImageData(0, 0, img.width, img.height).data) };
}, b64);
const rgb = (s) => s.slice(s.indexOf('(') + 1, s.indexOf(')')).split(/[ ,/]+/).slice(0, 3).map(Number);
const near = (d, i, c, tol = 40) => Math.hypot(d[i] - c[0], d[i + 1] - c[1], d[i + 2] - c[2]) < tol;

const failures = [];
const report = [];
for (const job of CASES) {
  const page = await browser.newPage();
  await page.setViewport({ width: job.width, height: 800, deviceScaleFactor: DPR });
  await page.evaluateOnNewDocument((approvals) => {
    window.addEventListener('message', (e) => { if (e.data && e.data.type === 'activity' && !window.__allowActivity) e.stopImmediatePropagation(); }, true);
    if (!approvals) window.__UISHOT__ = Object.assign({}, window.__UISHOT__, { pendingApprovals: [] });
  }, job.approvals);
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?live=1&conn=1&tier=pro`, { waitUntil: 'networkidle2', timeout: 30000 });
  await page.waitForSelector('header.studio-chrome', { timeout: 15000 });
  await wait(2000);
  const shots = {};
  for (const state of ['idle', 'live']) {
    if (state === 'live') {
      await page.evaluate(() => { window.__allowActivity = true; });
      await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'activity', payload: { seq: 1, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true } }));
      // Live is read off the chip's own title (it names the latest activity), not off the badge, so this wait does not
      // depend on how the mark is drawn.
      await page.waitForFunction(() => [...document.querySelectorAll('header.studio-chrome button[aria-haspopup="menu"]')]
        .some((b) => (b.getAttribute('title') || '').includes('Ran DAX query')), { timeout: 10000 });
      await wait(500);
    }
    for (const z of ZOOMS) {
      await page.evaluate((level) => window.__uishotHarness.dispatch({ type: 'studioZoom', level }), z);
      await page.waitForFunction((want) => getComputedStyle(document.querySelector('.studio-root')).zoom === want, { timeout: 10000 }, String(z / 100));
      await wait(700);
      for (const focus of ['none', 'chip', 'next']) {
        await page.evaluate(() => { if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur(); });
        await page.keyboard.press('Shift');   // keyboard modality, so focus() shows the ring exactly as a Tab would
        const g = await page.evaluate((which) => {
          const h = document.querySelector('header.studio-chrome');
          const chip = [...h.querySelectorAll('button[aria-haspopup="menu"]')].find((b) => (b.textContent || '').includes('Your assistant'));
          const after = chip.nextElementSibling && chip.nextElementSibling.tagName === 'BUTTON' ? chip.nextElementSibling : null;
          const next = after || chip.parentElement.nextElementSibling.querySelector('button');
          const el = which === 'chip' ? chip : which === 'next' ? next : null;
          if (el) el.focus();
          const r = chip.getBoundingClientRect();
          const mk = chip.querySelector('.studio-feed-mark');
          const m = mk ? mk.getBoundingClientRect() : null;
          // The chip's words, as the glyphs' own box (a Range over the text node), and how far the badge's paint
          // reaches past its box (the spread of a box-shadow halo, when it has one).
          const words = [...chip.querySelectorAll('span')].find((s) => (s.textContent || '').trim() === 'Your assistant');
          const range = document.createRange(); if (words) range.selectNodeContents(words);
          const tr = words ? range.getBoundingClientRect() : null;
          const shadow = mk ? getComputedStyle(mk, '::before').boxShadow : 'none';
          const spread = shadow && shadow !== 'none' ? parseFloat((shadow.match(/(-?[\d.]+)px/g) || [])[3] || '0') : 0;
          const probe = document.createElement('span'); probe.style.color = 'var(--sem-fg)'; h.appendChild(probe);
          const fg = getComputedStyle(probe).color; probe.remove();
          return { fold: h.dataset.fold + (h.dataset.brandFolded ? 'b' : ''), chip: [r.left, r.top, r.right, r.bottom], mark: m ? [m.left, m.top, m.right, m.bottom] : null, fg,
            text: tr ? [tr.left, tr.top, tr.right, tr.bottom] : null, spread,
            next: (next.getAttribute('aria-label') || next.textContent || '').trim().slice(0, 24),
            focusVisible: el ? el.matches(':focus-visible') : null, ring: el ? getComputedStyle(el).outlineColor : null };
        }, focus);
        await wait(200);
        const clip = { x: g.chip[2] - 16, y: Math.max(0, g.chip[1] - 10), width: 36, height: 26 };
        const b64 = await page.screenshot({ clip, encoding: 'base64' });
        writeFileSync(join(OUT, `${job.slug}-${z}-${state}-${focus}.png`), Buffer.from(b64, 'base64'));
        shots[`${z}-${focus}-${state}`] = { g, clip, px: await pixels(b64) };
      }
    }
  }
  await page.close();
  for (const z of ZOOMS) {
    const where = `${job.slug} at ${z}%`;
    // The badge is painted live and not idle: count text-colour pixels inside its disc.
    const live = shots[`${z}-none-live`]; const idle = shots[`${z}-none-idle`];
    const disc = (s) => {
      if (!s.g.mark) return { n: 0, tot: 0, missing: true };
      const [l, t, r, b] = s.g.mark; const cx = (l + r) / 2, cy = (t + b) / 2, rad = (r - l) / 2 - 0.35; const fg = rgb(s.g.fg);
      let n = 0, tot = 0;
      for (let y = 0; y < s.px.h; y++) for (let x = 0; x < s.px.w; x++) {
        const X = s.clip.x + (x + 0.5) / DPR, Y = s.clip.y + (y + 0.5) / DPR;
        if ((X - cx) ** 2 + (Y - cy) ** 2 > rad * rad) continue;
        tot++; if (near(s.px.d, (y * s.px.w + x) * 4, fg)) n++;
      }
      return { n, tot };
    };
    const dl = disc(live), di = disc(idle);
    // This half follows the badge design: a mark drawn another way (a ring on the chip) needs its own paint check here.
    if (dl.missing) failures.push(`${where}: no .studio-feed-mark on the chip, so the painted check has nothing to read`);
    else if (dl.n < dl.tot * 0.6) failures.push(`${where}: the live badge paints only ${dl.n} of ${dl.tot} pixels of its disc`);
    if (di.n > di.tot * 0.05) failures.push(`${where}: the idle badge paints ${di.n} of ${di.tot} pixels of its disc`);
    // The badge keeps clear of the chip's words: at least 1 CSS px between its paint (halo included) and the glyphs.
    // Moving it off the focus rings must not walk it into "Your assistant" (builder, round 3).
    if (live.g.mark && live.g.text) {
      const [ml, mt, mr, mb] = live.g.mark.map((v, i) => v + (i < 2 ? -1 : 1) * live.g.spread);
      const [tl, tt, trr, tb] = live.g.text;
      const dx = Math.max(0, tl - mr, ml - trr), dy = Math.max(0, tt - mb, mt - tb);
      const gap = Math.hypot(dx, dy);
      report.push(`${where}: the live badge is ${gap.toFixed(2)} CSS px from the chip's words`);
      if (gap < 1) failures.push(`${where}: the live badge is ${gap.toFixed(2)} CSS px from "Your assistant", closer than 1px`);
    } else if (live.g.mark) failures.push(`${where}: could not find the words "Your assistant" in the chip`);
    // No focus ring loses a pixel to the badge.
    for (const focus of ['chip', 'next']) {
      const a = shots[`${z}-${focus}-idle`]; const b = shots[`${z}-${focus}-live`];
      if (!a.g.focusVisible || !b.g.focusVisible) { failures.push(`${where}: ${focus} did not show its focus ring`); continue; }
      const ring = rgb(a.g.ring);
      let base = 0, lost = 0; const xs = [], ys = [];
      for (let y = 0; y < a.px.h; y++) for (let x = 0; x < a.px.w; x++) {
        const i = (y * a.px.w + x) * 4;
        if (!near(a.px.d, i, ring)) continue;
        base++;
        if (!near(b.px.d, i, ring)) { lost++; xs.push(a.clip.x + x / DPR - a.g.chip[2]); ys.push(a.clip.y + y / DPR - a.g.chip[1]); }
      }
      const who = focus === 'chip' ? 'Your assistant' : `its neighbour "${a.g.next}"`;
      const span = lost ? ` (x ${Math.min(...xs).toFixed(1)} to ${Math.max(...xs).toFixed(1)}, y ${Math.min(...ys).toFixed(1)} to ${Math.max(...ys).toFixed(1)} CSS px from the chip's top-right corner)` : '';
      report.push(`${where} fold ${a.g.fold}: ${who} focus ring ${base} px at 4x, ${lost} covered by the live badge${span}`);
      // Not one pixel (brief, round 3): idle and live are the same page with the same focus, so nothing but the badge
      // can take a ring pixel away, and a whole ring measures 0 here.
      if (lost > 0) failures.push(`${where}: the live badge covers ${lost} of ${base} focus-ring pixels of ${who}${span}`);
    }
  }
}
await browser.close(); server.close();
for (const line of report) console.log('  ' + line);
console.log(failures.length ? `FAIL the feed mark covers a focus ring or is not painted as it should be:\n  ${failures.join('\n  ')}` : 'ok the feed mark covers no focus ring, is painted live and hidden idle');
console.log(`crops in ${OUT}`);
if (failures.length) process.exit(1);
