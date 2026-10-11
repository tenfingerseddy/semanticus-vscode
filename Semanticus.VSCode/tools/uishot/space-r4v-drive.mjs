// Verifier's proof for m11/space-rule round 4 (e14ede52). First seen in a real VS Code window (UAT lane k, 28 September
// 2026) and reproduced here so it can fail.
//
// A. A growing box fitted BEFORE a later box brings the page scrollbar keeps the height it had at the wider width, and
//    the end of its text is cut off with no scrollbar. Round 4's useAutoGrow only records a box's FIRST ResizeObserver
//    report and does not re-fit on it (webview/src/hooks.ts, `if (!first) fitToText(el, true)`), on the stated grounds
//    that the layout effect has just fitted the box. That is true of the width the box had at its fit, not of the width
//    it has at its first report. Docs mounts its three narrative boxes in one commit and fits them in order: Overview
//    is fitted while the page does not scroll, then Methodology grows, the page overflows, its scrollbar takes width
//    and Overview is narrowed, all before the first report is delivered, which then carries the NARROWER width and is
//    only recorded. Round 3 seeded the observer with the width at the fit (`let seen = el.clientWidth`), so that first
//    report re-fitted the box. Real window, head's VSIX: Review, Docs opened after a reload with saved narratives, at
//    2000 x 1200 and the default 100%, an ordinary 373-character Overview is 210 px for 248 px of text (40 px cut off);
//    at 1440 x 1000 and 90%, 130 px for 248 px (120 px cut off). Round 3's bundle in the same window: 250 px, 0 hidden.
//
// B. Plan's Next list is sized to the window (plan.tsx: "as many as fit the window"), but only when the page mounts or
//    the window resizes: a Studio text-size change (Ctrl + wheel, CSS zoom, no resize event) is never re-measured. At
//    80% the rows are smaller and the list keeps the count it had at 100%, so the page is left empty under a list that
//    still hides rows behind "Show all"; opened at 80% the same page shows them. Real window, 2000 x 1200, Northstar:
//    100% to 80% left 224 px empty under 10 rows and "Show all 13"; reopened at 80% it showed all 14 rows.
//
// Headless Chromium gives a scrollbar no width (see scrollbar-width.mjs), so the page scrollbar is emulated with the
// browser's own `scrollbar-gutter: stable` on <main>, switched on exactly while <main> overflows and re-checked on every
// layout read the fit makes (scrollHeight, clientHeight, clientWidth, offsetHeight, offsetWidth). It therefore appears
// in the same layout as the growth that causes it, which is what a classic scrollbar does, and it is seen by the fit's
// own reads exactly as a real one is. The Overview text is six paragraphs that each fit on one line at the width the
// box has with no page scrollbar and not at the width it has with one, measured in the page in the box's own font, so
// the case bites in either zoom model and at any font; a size where no such text exists throws instead of passing.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-r4v-drive.mjs            (CASES=B for one case)
import { createRequire } from 'node:module';
import { planSeed, start, wait } from './visual-scenes.mjs';
import { findBrowser } from './browser.mjs';

const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const { server, browser: harness, base } = await start();
const vscode = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser({ quiet: true }), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', '--disable-blink-features=StandardizedBrowserZoom'],
});
// CASES=B runs only that case (a quicker re-run); unset runs them all.
const want = (c) => !process.env.CASES || process.env.CASES.includes(c);
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };
const DOCS = `${base}?tier=pro&conn=1&live=1&tab=docs`;
const LONG = Array.from({ length: 40 }, (_, i) => `Line ${i + 1}: revenue is recognised at ship date and returns are netted off in the month they are processed.`).join('\n');

function emulatePageScrollbar() {
  const get = (p, n) => Object.getOwnPropertyDescriptor(p, n).get;
  const SH = get(Element.prototype, 'scrollHeight'), CH = get(Element.prototype, 'clientHeight');
  let busy = false;
  const sync = () => {
    if (busy || window.__noEmu) return;
    busy = true;
    try {
      const m = document.querySelector('main');
      if (m) { const want = SH.call(m) > CH.call(m) + 1 ? 'stable' : 'auto'; if (m.style.scrollbarGutter !== want) m.style.scrollbarGutter = want; }
    } finally { busy = false; }
  };
  for (const [p, n] of [[Element.prototype, 'scrollHeight'], [Element.prototype, 'clientHeight'], [Element.prototype, 'clientWidth'], [HTMLElement.prototype, 'offsetHeight'], [HTMLElement.prototype, 'offsetWidth']]) {
    const g = get(p, n);
    Object.defineProperty(p, n, { configurable: true, get() { sync(); return g.call(this); } });
  }
  const loop = () => { sync(); requestAnimationFrame(loop); };
  requestAnimationFrame(loop);
  window.__emulatedScrollbar = true;
}
// The model's own narrative, written into the harness's getDocModel answer before the app reads it.
function seedModelNarrative(sections) {
  window.addEventListener('message', (e) => {
    const r = e.data && e.data.type === 'rpcResult' && e.data.result;
    if (r && r.header && r.modelNarrative) r.modelNarrative = { author: 'human', updatedUtc: 'x', sections: sections.map(([key, markdown]) => ({ key, markdown })) };
  }, true);
}
const zoomTo = async (page, target) => {
  for (let i = 0; i < 8; i++) {
    const z = await page.evaluate(() => parseInt(document.querySelector('.studio-root')?.style.zoom || '100', 10));
    if (z === target) return;
    await page.evaluate((dy) => window.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, cancelable: true })), z < target ? -100 : 100);
    await wait(450);
  }
  throw new Error(`the Studio text size never reached ${target}%`);
};
const modelOf = (page) => page.evaluate(() => {
  const root = document.querySelector('.studio-root') || document.body;
  const probe = document.createElement('div');
  probe.style.cssText = 'position:absolute;left:0;top:0;width:100px;height:1px;visibility:hidden;zoom:2';
  root.appendChild(probe);
  const w = probe.getBoundingClientRect().width;
  probe.remove();
  return Math.abs(w - 100) < 1 ? 'vscode' : 'harness';
});

// Six paragraphs that each fit on one line of Overview with no page scrollbar and not with one: the widths are read
// from the page itself (the box's own font and zoom), the scrollbar being the emulated one switched on for the reading.
async function tightText(b, size, zoom) {
  const page = await b.newPage();
  try {
    await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument(() => { window.__noEmu = true; });
    await page.evaluateOnNewDocument(emulatePageScrollbar);
    await page.evaluateOnNewDocument(seedModelNarrative, [['overview', 'x']]);
    await page.goto(DOCS, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    await zoomTo(page, zoom);
    await wait(700);
    return await page.evaluate(() => {
      const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
      if (!ta) return null;
      const main = document.querySelector('main');
      const inner = () => { const cs = getComputedStyle(ta); return parseFloat(cs.width) - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) - parseFloat(cs.borderLeftWidth) - parseFloat(cs.borderRightWidth); };
      const w0 = inner();
      main.style.scrollbarGutter = 'stable';
      const w1 = inner();
      main.style.scrollbarGutter = 'auto';
      const cs = getComputedStyle(ta);
      const span = document.createElement('span');
      span.style.cssText = 'position:absolute;visibility:hidden;white-space:pre;left:0;top:0';
      for (const p of ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'letterSpacing', 'wordSpacing', 'fontStretch', 'fontVariant']) span.style[p] = cs[p];
      ta.parentElement.appendChild(span);
      // A width in the box's own pixels, whichever zoom model the page is in.
      const width = (s) => { span.textContent = s; const r = span.getBoundingClientRect().width; return r / (span.offsetWidth ? r / span.offsetWidth : 1); };
      const words = 'Net revenue is recognised at ship date and returns are netted off in the month they are processed by finance'.split(' ');
      const paras = [];
      for (let p = 0; p < 6; p++) {
        let s = '', i = p;
        while (true) { const next = s ? s + ' ' + words[i % words.length] : words[i % words.length]; if (width(next) > w0 - 0.25) break; s = next; i++; }
        for (const c of ['x', 'a', 'e', 'i', '.']) { while (width(s + c) <= w0 - 0.25) s += c; }
        paras.push(s);
      }
      const widths = paras.map(width);
      span.remove();
      return { w0, w1, widths, bites: w1 < w0 - 0.5 && widths.every((x) => x > w1 + 0.05 && x <= w0), text: paras.join('\n'), scrolls: main.scrollHeight > main.clientHeight + 1 };
    });
  } finally { await page.close(); }
}

// Whether the page scrolls with Overview holding `text` and nothing written anywhere else: if it does, Overview's own
// growth brings the scrollbar and its fit already sees it, and the case could not bite.
async function scrollsWith(b, size, zoom, text) {
  const page = await b.newPage();
  try {
    await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument(emulatePageScrollbar);
    await page.evaluateOnNewDocument(seedModelNarrative, [['overview', text]]);
    await page.goto(DOCS, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    await zoomTo(page, zoom);
    await wait(900);
    return await page.evaluate(() => { const m = document.querySelector('main'); return m.scrollHeight > m.clientHeight + 1; });
  } finally { await page.close(); }
}

// A. Review, Docs, opened with a saved Overview and a long saved Methodology.
if (want('A')) for (const [model, b] of [['harness', harness], ['vscode', vscode]]) {
  // The Studio's own size inside a real 2000 x 1200 VS Code window (1652 x 1076), where the fault was seen at the
  // default 100%; then the same Studio at the first other text size where a one-line paragraph can tell the two widths
  // apart (the narrative font is monospace, so at some sizes no whole number of characters falls between them). The
  // real 1440 x 1000 window showed the fault at 90% too, but the harness's Docs page is taller than Northstar's, so
  // there the six-line Overview alone already scrolls the page.
  const size = [1652, 1076];
  const cases = [100];
  for (const z of [90, 110, 80, 120]) {
    const t = await tightText(b, size, z);
    if (t && !t.scrolls && t.bites && !await scrollsWith(b, size, z, t.text)) { cases.push(z); break; }
  }
  if (cases.length < 2) throw new Error(`A ${model} model: at no text size from 80% to 120% can the case bite, so it would prove nothing off 100%`);
  for (const zoom of cases) {
    const tag = `A ${model} model, Docs, a Studio ${size[0]}x${size[1]} at ${zoom}%`;
    const t = await tightText(b, size, zoom);
    if (!t) throw new Error(`${tag}: no Docs narrative box on the page, so nothing was measured`);
    if (t.scrolls) throw new Error(`${tag}: the page scrolls with only a one-letter Overview, so no scrollbar can arrive later and the case would prove nothing`);
    if (!t.bites) throw new Error(`${tag}: no one-line paragraph fits the box without the page scrollbar and not with it (${t.w0} and ${t.w1} px, widths ${t.widths.map((x) => x.toFixed(1)).join(', ')}), so the case would prove nothing`);
    if (await scrollsWith(b, size, zoom, t.text)) throw new Error(`${tag}: the page already scrolls with only the six-line Overview, so its fit sees the scrollbar and the case would prove nothing`);
    const page = await b.newPage();
    try {
      await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
      await page.evaluateOnNewDocument(emulatePageScrollbar);
      await page.evaluateOnNewDocument(seedModelNarrative, [['overview', 'x']]);
      await page.goto(DOCS, { waitUntil: 'networkidle0', timeout: 60000 });
      await wait(2600);
      if (await modelOf(page) !== model) throw new Error(`${tag}: the browser is not in the ${model} zoom model, so the case would prove nothing`);
      await zoomTo(page, zoom);
      // Reload the page at this text size with the saved narratives: the Studio keeps its text size in its own state.
      await page.evaluateOnNewDocument(seedModelNarrative, [['overview', t.text], ['glossary', 'GMV is gross merchandise value.'], ['methodology', LONG]]);
      await page.reload({ waitUntil: 'networkidle0', timeout: 60000 });
      await wait(2600);
      await zoomTo(page, zoom);
      // Leave and come back, so the three boxes mount together at this text size, the way opening the page does.
      const pick = (name) => page.evaluate((re) => {
        const sel = [...document.querySelectorAll('aside.sem-card')].pop()?.querySelector('select');
        const option = sel && [...sel.options].find((o) => new RegExp(re).test(o.textContent));
        if (!option) return false;
        sel.value = option.value; sel.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      }, name);
      if (!await pick('Sales') || (await wait(900), !await pick('^\\s*\\S\\s*Contoso'))) throw new Error(`${tag}: the narrative picker has no Sales table or no model entry, so the boxes were never remounted`);
      await wait(1500);
      const m = await page.evaluate(() => {
        const main = document.querySelector('main');
        const boxes = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].filter((x) => x.getClientRects().length).map((x) => ({ chars: x.value.length, h: x.offsetHeight, hidden: x.scrollHeight - x.clientHeight, ov: getComputedStyle(x).overflowY }));
        return { emulated: !!window.__emulatedScrollbar && main.style.scrollbarGutter === 'stable', scrolls: main.scrollHeight > main.clientHeight + 1, boxes };
      });
      if (!m.emulated || !m.scrolls) throw new Error(`${tag}: the page does not scroll with its emulated scrollbar after the long Methodology, so the case would prove nothing`);
      const ov = m.boxes[0];
      if (!ov || ov.chars !== t.text.length) throw new Error(`${tag}: Overview does not hold the seeded ${t.text.length} characters (${ov ? ov.chars : 'no box'}), so the case would prove nothing`);
      report(m.boxes.every((x) => x.hidden <= 0), `${tag}: every narrative box holds its whole text after the page scrollbar arrived with a later box`,
        m.boxes.map((x, i) => `box ${i + 1} ${x.chars} characters in ${x.h} px, ${x.hidden > 0 ? `${x.hidden} px of it cut off (overflow ${x.ov})` : 'nothing hidden'}`).join('; ')
        + ` (Overview's text width ${t.w0.toFixed(2)} px with no page scrollbar, ${t.w1.toFixed(2)} px with one)`);
    } finally { await page.close(); }
  }
}

// B. Plan, a Studio text-size change with the list on screen: Next must use the room the change frees.
if (want('B')) for (const [model, b] of [['harness', harness], ['vscode', vscode]]) {
  const tag = `B ${model} model, Plan, a Studio 1652x1076 from 100% to 80%`;
  const page = await b.newPage();
  try {
    await page.setViewport({ width: 1652, height: 1076, deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument(planSeed());
    await page.goto(`${base}?tier=pro&conn=1&live=1&tab=plan`, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    if (await modelOf(page) !== model) throw new Error(`${tag}: the browser is not in the ${model} zoom model, so the case would prove nothing`);
    const read = () => page.evaluate(() => {
      const root = document.querySelector('.studio-root');
      const z = parseFloat(getComputedStyle(root).zoom) || 1;
      const probe = document.createElement('div'); probe.style.cssText = 'position:absolute;width:100px;height:1px;visibility:hidden'; root.appendChild(probe);
      const f = z / (probe.getBoundingClientRect().width / 100); probe.remove();   // rect px to screen px, either model
      const main = document.querySelector('main');
      const list = document.querySelector('[data-testid="plan-list"]');
      if (!list) return null;
      const rows = [...list.querySelectorAll('div[data-testid^="plan-row-"]')].filter((r) => r.getClientRects().length && !/^plan-row-(open|page|more)/.test(r.dataset.testid));
      const more = document.querySelector('[data-testid="plan-show-all"]');
      return { rows: rows.length, rowH: rows.length ? Math.round(rows[rows.length - 1].getBoundingClientRect().height * f) : 0, more: more ? more.textContent.trim() : null,
        below: Math.round((main.getBoundingClientRect().bottom - list.getBoundingClientRect().bottom) * f), scrolls: main.scrollHeight > main.clientHeight + 1 };
    });
    const before = await read();
    if (!before) throw new Error(`${tag}: no Plan list on the page, so nothing was measured`);
    if (!before.more) throw new Error(`${tag}: at 100% the list already shows every row, so a smaller text size frees room for nothing and the case would prove nothing`);
    await zoomTo(page, 80);
    await wait(1200);
    const after = await read();
    report(!after.more || after.below < after.rowH || after.scrolls, `${tag}: Next grows into the room the smaller text frees`,
      `at 100% ${before.rows} rows and "${before.more}", ${before.below} px under the list; at 80% ${after.rows} rows${after.more ? ` and "${after.more}"` : ''}, ${after.below} px of empty page under the list (a row is ${after.rowH} px)`);
  } finally { await page.close(); }
}

await harness.close();
await vscode.close();
server.close();
if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
