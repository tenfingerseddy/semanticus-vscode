// Verifier's proof for the option B content surfaces (m11/diagram-contrast, 27 September 2026). Written to FAIL on
// f6364c68 and stay failing until the defects it names are fixed; it is not a gate the lane added.
//
// It renders the real Studio bundle on the mock engine, puts the page in Dark Modern and Light Modern (themes.mjs),
// and reads every colour it judges FROM THE SCREENSHOT PIXELS (the most common colour inside a box), never from
// computed styles. Three rules, each from the brief Kane's option B was built against:
//   A. A mark inside a lifted surface stays visible: a flat inset, a picked row, a pressed segment, the current
//      tool, a hovered row or button all differ from the fill right around them by at least a just-noticeable
//      step (CIE76 delta E 2.3). Surface-2 is one point past the card, so each of these measured 0.7 to 0.9.
//   B. No box in a box: a sticky header inside a lifted card is the card's colour (delta E 1.0 or less), not a
//      page-coloured band across it.
//   C. Content does not sit flush on the page: the Plan's rows, the Data preview grid and the Workflows list sit
//      on a ground a visible step off the page (delta E 2.3), like every other card now does.
//   node tools/uishot/verify-surface-steps-drive.mjs     (exit 1 while any check fails; OUTDIR=<dir> for the PNGs)
import { mkdirSync, writeFileSync } from 'node:fs';
import { inflateSync } from 'node:zlib';
import { resolve } from 'node:path';
import { planSeed, start, wait, webRoot } from './visual-scenes.mjs';
import { applyTheme } from './themes.mjs';

const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-surface-steps');
mkdirSync(OUT, { recursive: true });
const PRO = 'tier=pro&conn=1&live=1';
const JND = 2.3;

// ---- colour maths -------------------------------------------------------------------------------------------
const lin = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
function lab([r, g, b]) {
  const [R, G, B] = [lin(r), lin(g), lin(b)];
  const f = (t) => (t > 0.008856 ? Math.cbrt(t) : 7.787 * t + 16 / 116);
  const X = f((0.4124 * R + 0.3576 * G + 0.1805 * B) / 0.95047), Y = f(0.2126 * R + 0.7152 * G + 0.0722 * B), Z = f((0.0193 * R + 0.1192 * G + 0.9505 * B) / 1.08883);
  return [116 * Y - 16, 500 * (X - Y), 200 * (Y - Z)];
}
const dE = (a, b) => { const p = lab(a), q = lab(b); return Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]); };
const hex = (c) => '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase();

const { server, browser, base } = await start();
// The most common colour inside each box of a screenshot. The PNG is decoded here (zlib and the five PNG row
// filters), not on a canvas: a canvas read in the same headless browser came back all zero now and then under load.
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
async function modes(png, boxes) {
  const im = decodePng(Buffer.from(png, 'base64'));
  return boxes.map((bx) => {
    if (!bx) return null;
    const x0 = Math.round(bx.x), y0 = Math.round(bx.y), w = Math.max(1, Math.round(bx.w)), h = Math.max(1, Math.round(bx.h));
    if (x0 < 0 || y0 < 0 || x0 + w > im.w || y0 + h > im.h) return 'outside';
    const n = new Map();
    for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) {
      const i = (y * im.w + x) * im.bpp, k = (im.px[i] << 16) | (im.px[i + 1] << 8) | im.px[i + 2];
      n.set(k, (n.get(k) || 0) + 1);
    }
    let best = 0, bk = 0; for (const [k, v] of n) if (v > best) { best = v; bk = k; }
    return [(bk >> 16) & 255, (bk >> 8) & 255, bk & 255];
  });
}
// One retry for a screenshot the headless shell refuses under load ("Unable to capture screenshot").
const shoot = async (page) => { try { return await page.screenshot({ encoding: 'base64' }); } catch { await wait(800); return page.screenshot({ encoding: 'base64' }); } };
const box = (page, fn) => page.evaluate(fn);
const inset = (r, p = 3) => r && { x: r.x + p, y: r.y + p, w: r.w - 2 * p, h: r.h - 2 * p };
const VIS = `const V=(sel)=>[...document.querySelectorAll(sel)].filter(e=>{const r=e.getBoundingClientRect(); return r.width>0 && r.height>0 && r.right>0 && r.bottom>0 && r.left<innerWidth && r.top<innerHeight && getComputedStyle(e).visibility!=='hidden';});`;
const rectOf = `(el) => { if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; }`;

const clickText = (p, sel, text) => p.evaluate((s, t) => { const el = [...document.querySelectorAll(s)].find((e) => e.textContent.trim().startsWith(t) && e.getClientRects().length); if (!el) return false; el.click(); return true; }, sel, text);

// Each check returns the two boxes to compare and, for a hover check, the element to put the mouse on.
const CHECKS = [
  { id: 'A1 picked row in the Data > Tables card', rule: 'A', q: PRO + '&tab=data', run: async (p) => { await p.evaluate(() => document.querySelector('.xe-pick-row')?.click()); await wait(1200); },
    boxes: `${VIS} const R=${rectOf}; const cur=V('.xe-pick-row[aria-current="true"]')[0]; const next=V('.xe-pick-row').find(e=>e!==cur && !e.getAttribute('aria-current')); return [R(cur), R(next)];` },
  { id: 'A2 flat inset in the AI instructions card', rule: 'A', q: PRO + '&tab=instructions',
    boxes: `${VIS} const R=${rectOf}; const i=V('.sem-card .sem-inset')[0]; const r=R(i); return [r, r && { x: r.x, y: r.y - 7, w: r.w, h: 4 }];` },
  { id: 'A3 current tool in All tools', rule: 'A', q: PRO + '&tab=diagram', run: async (p) => { await clickText(p, 'button', 'Help'); await wait(300); await clickText(p, 'button', 'All tools'); await wait(600); },
    boxes: `${VIS} const R=${rectOf}; const c=V('.tool-chooser-menu button[aria-current="page"]')[0]; const m=R(V('.tool-chooser-menu')[0]); const r=R(c); return [r, r && m && { x: m.x + 2, y: r.y + 2, w: 4, h: r.h - 4 }];` },
  { id: 'A4 hovered tool in All tools', rule: 'A', hover: true, q: PRO + '&tab=diagram', run: async (p) => { await clickText(p, 'button', 'Help'); await wait(300); await clickText(p, 'button', 'All tools'); await wait(600); },
    target: `return [...document.querySelectorAll('.tool-chooser-menu button')].find(b => !b.getAttribute('aria-current') && b.textContent.trim() === 'Lineage' && b.getBoundingClientRect().width > 0)`,
    boxes: `${VIS} const R=${rectOf}; const t=V('.tool-chooser-menu button').find(b => !b.getAttribute('aria-current') && b.textContent.trim() === 'Lineage'); const m=R(V('.tool-chooser-menu')[0]); const r=R(t); return [r, r && m && { x: m.x + 2, y: r.y + 2, w: 4, h: r.h - 4 }];` },
  { id: 'A5 hovered zoom button on the Diagram', rule: 'A', hover: true, restCompare: true, q: PRO + '&tab=diagram', run: async () => { await wait(600); },
    target: `return [...document.querySelectorAll('.react-flow__controls-button')].filter(b => b.getBoundingClientRect().width > 0)[1]`,
    boxes: `${VIS} const R=${rectOf}; const b=R(V('.react-flow__controls-button')[1]); return [b, b];` },
  { id: 'A6 pressed segment in the Size card', rule: 'A', q: PRO + '&tab=stats', run: async () => { await wait(600); },
    boxes: `${VIS} const R=${rectOf}; const on=V('.sem-card .sem-seg-item[aria-pressed="true"]')[0]; const off=on && [...on.parentElement.querySelectorAll('.sem-seg-item')].find(e=>e.getAttribute('aria-pressed')!=='true'); return [R(on), R(off)];` },
  // The redesigned Queries page (m11/queries-redesign) shows the chart rules' pick after Run, so the check picks the
  // Table kind to get the grid, as verify-queries-drive v5 does. Besides the colour, it checks the header sits whole
  // inside its card: the result card is flush and clips (overflow hidden), so a header past its edge is cut off.
  { id: 'B1 Queries result grid header inside its card', rule: 'B', contain: true, q: PRO + '&tab=daxlab',
    run: async (p) => {
      if (!await clickText(p, 'button[aria-label="Run the query"]', 'Run')) throw new Error('no Run the query button');
      await wait(1800);
      if (!await clickText(p, '[aria-label="Chart kind"] button', 'Table')) throw new Error('no Table chart kind after Run');
      await wait(900);
    },
    boxes: `${VIS} const R=${rectOf}; const h=V('.sem-card .sem-grid-scroll .sticky')[0]; const r=R(h); const card=R(h && h.closest('.sem-card')); return [r, r && { x: r.x + 2, y: r.y + r.h + 3, w: 12, h: 20 }, card];` },
  { id: 'B2 Power Query preview grid header inside its card', rule: 'B', q: PRO + '&tab=mcode',
    boxes: `${VIS} const R=${rectOf}; const h=V('.sem-card .sem-grid-scroll .sticky')[0]; const r=R(h); return [r, r && { x: r.x + 2, y: r.y + r.h + 3, w: 12, h: 20 }];` },
  { id: 'B3 Perspectives table header inside its card', rule: 'B', q: PRO + '&tab=advmodels',
    boxes: `${VIS} const R=${rectOf}; const h=V('.sem-card .sticky.top-0')[0]; const r=R(h); return [r, r && { x: r.x + 2, y: r.y + r.h + 3, w: 12, h: 20 }];` },
  { id: "C1 the Plan's Next rows", rule: 'C', q: PRO + '&tab=plan', plan: true,
    boxes: `${VIS} const R=${rectOf}; const rows=V('[data-testid^="plan-row-"].pw-row'); const r=R(rows[rows.length > 3 ? 3 : 0]); const m=R(V('main')[0]); return [r && { x: r.x + r.w - 60, y: r.y + 2, w: 40, h: Math.max(4, r.h - 4) }, m && { x: m.x + 2, y: m.y + 4, w: 14, h: m.h - 8 }];` },
  { id: 'C2 the Data preview grid', rule: 'C', q: PRO + '&tab=data', run: async (p) => { await p.evaluate(() => document.querySelector('.xe-pick-row')?.click()); await wait(1200); },
    boxes: `${VIS} const R=${rectOf}; const g=R(V('.xe-grid-framed')[0]); const m=R(V('main')[0]); return [g && { x: g.x + g.w - 40, y: g.y + 40, w: 30, h: 60 }, m && { x: m.x + 2, y: m.y + 4, w: 14, h: m.h - 8 }];` },
  { id: 'C3 the Workflows list', rule: 'C', q: PRO + '&tab=workflows', run: async () => { await wait(1200); },
    boxes: `${VIS} const R=${rectOf}; const r=R(V('.sem-wf-row')[0]); const m=R(V('main')[0]); return [r && { x: r.x + r.w / 2, y: r.y + 3, w: 30, h: r.h - 6 }, m && { x: m.x + 2, y: m.y + 4, w: 14, h: m.h - 8 }];` },
];

const results = [];
for (const c of CHECKS) {
  for (const theme of ['dark', 'light']) {
    let page;
    try {
      page = await browser.newPage();
      await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
      if (c.plan) await page.evaluateOnNewDocument(planSeed());
      await page.goto(`${base}?${c.q}`, { waitUntil: 'networkidle0', timeout: 45000 });
      await wait(2600);
      await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' });
      await page.mouse.move(1439, 899);
      await applyTheme(page, theme); await wait(300);
      if (c.run) await c.run(page);
      await wait(300);
      let rest = null;
      const [b1, b2, card] = await box(page, new Function(c.boxes));
      if (!b1 || !b2) throw new Error('could not find the boxes to compare');
      // A header that crosses its card's edge is clipped there: say so by the edge and the pixels past it.
      const past = !c.contain ? [] : !card ? ['no card around it'] : [
        ['left', card.x - b1.x], ['top', card.y - b1.y], ['right', b1.x + b1.w - (card.x + card.w)], ['bottom', b1.y + b1.h - (card.y + card.h)],
      ].filter(([, d]) => d > 0.5).map(([e, d]) => `${Math.round(d)} px past the card's ${e} edge`);
      if (c.hover) {
        if (c.restCompare) rest = await shoot(page);
        const t = await page.evaluate((src) => { const el = new Function(src)(); if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.x + Math.min(r.width - 4, 12), y: r.y + r.height / 2 }; }, c.target);
        if (!t) throw new Error('hover target not found');
        await page.mouse.move(t.x, t.y); await wait(300);
      }
      const png = await shoot(page);
      writeFileSync(`${OUT}/${c.id.split(' ')[0]}-${theme}.png`, Buffer.from(png, 'base64'));
      const [m1] = await modes(png, [inset(b1)]);
      // A1 and A6 compare two whole rows or segments; every other check compares a box with a thin strip beside it.
      const whole = c.id.startsWith('A1') || c.id.startsWith('A6');
      const [m2] = c.restCompare ? await modes(rest, [inset(b2)]) : await modes(png, [whole ? inset(b2) : b2]);
      if (m1 === 'outside' || m2 === 'outside' || !m1 || !m2) throw new Error('a box to compare falls outside the screenshot');
      const d = dE(m1, m2);
      const ok = (c.rule === 'B' ? d <= 1.0 : d >= JND) && past.length === 0;
      results.push({ id: c.id, theme, a: hex(m1), b: hex(m2), dE: +d.toFixed(1), ...(c.contain ? { clipped: past } : {}), ok });
      console.log(`${ok ? 'ok  ' : 'FAIL'} ${theme.padEnd(5)} ${c.id.padEnd(48)} ${hex(m1)} vs ${hex(m2)}  dE ${d.toFixed(1)} (${c.rule === 'B' ? 'wanted 1.0 or less' : `wanted ${JND} or more`})${c.contain ? (past.length ? '; header ' + past.join(', ') : '; header inside its card') : ''}`);
    } catch (e) {
      results.push({ id: c.id, theme, ok: false, error: String(e.message ?? e) });
      console.log(`FAIL ${theme.padEnd(5)} ${c.id.padEnd(48)} could not measure: ${String(e.message ?? e).slice(0, 120)}`);
    } finally { if (page) await page.close(); }
  }
}
await browser.close(); server.close();
writeFileSync(`${OUT}/results.json`, JSON.stringify(results, null, 1));
const bad = results.filter((r) => !r.ok);
console.log(`\n${results.length - bad.length}/${results.length} checks passed; shots in ${OUT}`);
process.exit(bad.length ? 1 : 0);
