// Proofs for m11/space-rule round 4, beside the verifier's space-r3v-drive.mjs. Each case failed on 985c5ad3 (round
// 3) except F, which guards a round 2 fix that nothing guarded (the verifier's M2) and is proved by undoing it.
//
// Most cases run in BOTH zoom models, because round 3 was right in one and wrong in the other. VS Code starts its
// Chromium with --disable-blink-features=...StandardizedBrowserZoom, so inside a VS Code webview getBoundingClientRect
// is not scaled by the Studio text size; the harness browser runs standardized zoom, where it is. A fix that assumes
// either model fails the other model's run of the same case, so these cases tell the two apart.
//
// A. Docs, a long narrative written: the settings and preview columns reach the page bottom and keep Reset to
//    defaults in view, with the page at its top AND scrolled down to the narrative's end (where the columns stick),
//    at 80, 100 and 120% in both zoom models. Round 3 left them at their 240 px floor.
// B. Typing at the end of a long Docs narrative with the caret in view does not move the page, at 80, 90 and 120%
//    in both models. Round 3's caret follow mixed rects with the box's own pixels, and in the harness's model moved
//    the page 276 to 345 px at 80% (the verifier's R7).
// C. A paste into the empty AI instructions editor leaves the caret in view, at 80 and 120% in both models. Round 3
//    left it 138 px below the pane at 120% in the harness's model.
// D. A key typed into a long text lays out no hidden twin unless the box changed height. Round 3 laid out the whole
//    text on a twin two or three times per key, about 68 ms a key at 50,000 characters against round 2's 8 ms (the
//    verifier's R6). A timing assertion on a shared machine is a coin toss, so this counts the twins, which is what
//    the time was spent on, and prints the times for the report.
// E. Properties: short and empty boxes keep their rows floor (50 px for two rows, not one 30 px line), and no box shows
//    a scrollbar below its cap.
// F. Connections at 2000 x 1200: each row's Open sits at the end of the row's capped reading line, not at the far
//    edge of the window. Nothing guarded the reading line (with `.sem-row-line` emptied every other drive passed).
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-r4-drive.mjs            (CASES=F for one case)
import { createRequire } from 'node:module';
import { SPACE_SCENES, start, wait } from './visual-scenes.mjs';
import { findBrowser } from './browser.mjs';

const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const { server, browser: harness, base } = await start();
const vscode = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser({ quiet: true }), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', '--disable-blink-features=StandardizedBrowserZoom'],
});
const MODELS = [['harness', harness], ['vscode', vscode]];
// CASES=AB runs only those cases (a quicker re-run); unset runs them all.
const want = (c) => !process.env.CASES || process.env.CASES.includes(c);
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };
const NARRATIVE = Array.from({ length: 40 }, (_, i) => `Line ${i + 1}: revenue is recognised at ship date and returns are netted off in the month they are processed.`).join('\n');
const LONGER = Array.from({ length: 30 }, (_, i) => `Paragraph ${i + 1}. Answer as a careful finance analyst, and say plainly when the model cannot answer the question asked.`).join('\n\n');

async function scene(b, id, [w, h]) {
  const s = SPACE_SCENES.find((x) => x.id === id);
  const page = await b.newPage();
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
  await page.goto(`${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2600);
  if (s.run) await s.run(page);
  await wait(700);
  return page;
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
// Which zoom model this page is in, so a case can never quietly run in the wrong one.
const modelOf = (page) => page.evaluate(() => {
  const root = document.querySelector('.studio-root') || document.body;
  const probe = document.createElement('div');
  probe.style.cssText = 'position:absolute;left:0;top:0;width:100px;height:1px;visibility:hidden;zoom:2';
  root.appendChild(probe);
  const w = probe.getBoundingClientRect().width;
  probe.remove();
  return Math.abs(w - 100) < 1 ? 'vscode' : 'harness';
});
const checkModel = async (page, want) => {
  const got = await modelOf(page);
  if (got !== want) throw new Error(`this browser is in the ${got} zoom model, not the ${want} one, so the case would prove nothing`);
};
const pasteNarrative = (page, text) => page.evaluate((v) => {
  const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
  if (!ta) return false;
  ta.focus(); ta.select(); document.execCommand('insertText', false, v);
  return true;
}, text);

// A. The Docs columns after a long narrative, at the top of the page and stuck at its end.
if (want('A')) for (const [model, b] of MODELS) {
  for (const z of [80, 100, 120]) {
    const page = await scene(b, 'review-docs', [1440, 900]);
    try {
      await checkModel(page, model);
      await zoomTo(page, z);
      if (!await pasteNarrative(page, NARRATIVE)) throw new Error('no Docs narrative box on the page, so nothing was measured');
      await wait(900);
      const read = () => page.evaluate(() => {
        const main = document.querySelector('main');
        const q = main.getBoundingClientRect();
        const cols = [...document.querySelectorAll('.sem-docs-col')];
        const reset = [...main.querySelectorAll('button')].find((x) => x.textContent.trim() === 'Reset to defaults');
        let shown = 0;
        if (reset) {
          const r = reset.getBoundingClientRect();
          const c = reset.closest('.sem-docs-col').getBoundingClientRect();
          const lo = Math.max(r.top, c.top, q.top), hi = Math.min(r.bottom, c.bottom, q.bottom);
          shown = Math.round(100 * Math.max(0, hi - lo) / Math.max(1, r.height));
        }
        // Distances in screen pixels of this page, whichever model it is in: the rect of a 100 px probe says how
        // many rect pixels one screen pixel is here.
        const root = document.querySelector('.studio-root');
        const probe = document.createElement('div'); probe.style.cssText = 'position:absolute;width:100px;height:1px;visibility:hidden'; root.appendChild(probe);
        const unit = probe.getBoundingClientRect().width / 100; probe.remove();
        const zoom = parseFloat(getComputedStyle(root).zoom) || 1;
        const px = (d) => Math.round(d * (Math.abs(unit - 1) < 0.01 ? zoom : 1));
        return { scrollTop: Math.round(main.scrollTop), scrolls: main.scrollHeight > main.clientHeight + 4, cols: cols.map((c) => px(q.bottom - c.getBoundingClientRect().bottom)), tops: cols.map((c) => px(c.getBoundingClientRect().top - q.top)), shown, cols2: cols.length };
      });
      await page.evaluate(() => { document.querySelector('main').scrollTop = 0; });
      await wait(400);
      const top = await read();
      await page.evaluate(() => { const m = document.querySelector('main'); m.scrollTop = m.scrollHeight; });
      await wait(400);
      const end = await read();
      if (top.cols2 !== 2 || !top.scrolls) throw new Error(`the Docs page has ${top.cols2} window-height columns and ${top.scrolls ? 'scrolls' : 'does not scroll'}, so the case would prove nothing`);
      const fits = (g) => g >= -64 && g <= 80;
      for (const [state, m] of [['at the top', top], ['scrolled to the end', end]]) {
        report(m.cols.every(fits) && m.shown >= 75, `A ${model} model, Docs 1440x900 at ${z}%, a long narrative, ${state}: both columns reach the page bottom and Reset to defaults shows`,
          `columns end ${m.cols.join(' and ')} px above the page bottom (the rule allows -64 to 80), their tops ${m.tops.join(' and ')} px down, Reset ${m.shown}% shown, page scrollTop ${m.scrollTop}`);
      }
    } finally { await page.close(); }
  }
}

// B. Typing at the end of a long narrative, the caret 200 screen px above the fold.
if (want('B')) for (const [model, b] of MODELS) {
  for (const z of [80, 90, 120]) {
    const page = await scene(b, 'review-docs', [1440, 1000]);
    try {
      await checkModel(page, model);
      await zoomTo(page, z);
      if (!await pasteNarrative(page, NARRATIVE)) throw new Error('no Docs narrative box on the page, so nothing was measured');
      await wait(900);
      const setup = await page.evaluate(() => {
        const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
        const main = ta.closest('main');
        const own = main.getBoundingClientRect().width / main.offsetWidth;
        const r = ta.getBoundingClientRect(); const q = main.getBoundingClientRect();
        main.scrollTop += (r.bottom - (q.bottom - 200 * own)) / own;
        ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
        return Math.round(main.scrollTop);
      });
      await wait(300);
      const tops = [];
      for (const k of ['a', 'b', 'c', ' ', 'd', 'e']) {
        await page.keyboard.type(k);
        await wait(200);
        tops.push(await page.evaluate(() => Math.round(document.querySelector('main').scrollTop)));
      }
      const moved = tops.map((t) => t - setup);
      report(moved.every((d) => Math.abs(d) <= 1), `B ${model} model, Docs at ${z}%: typing at the end of a long narrative with the caret in view does not move the page`,
        `scrollTop ${setup}, then moved ${moved.join(', ')} px over six keys`);
    } finally { await page.close(); }
  }
}

// C. A paste into the empty AI instructions editor, then the caret must be inside the pane that scrolls.
if (want('C')) for (const [model, b] of MODELS) {
  for (const z of [80, 120]) {
    const page = await scene(b, 'edit-instructions-editing', [1440, 1000]);
    try {
      await checkModel(page, model);
      await zoomTo(page, z);
      const m = await page.evaluate(async (text) => {
        const ta = document.getElementById('overview-instructions-editor');
        if (!ta) return null;
        ta.focus(); ta.select(); document.execCommand('insertText', false, text);
        await new Promise((r) => setTimeout(r, 900));
        let pane = ta.parentElement;
        while (pane && !(/(auto|scroll)/.test(getComputedStyle(pane).overflowY) && pane.scrollHeight > pane.clientHeight)) pane = pane.parentElement;
        if (!pane) return { none: true };
        const own = ta.getBoundingClientRect().width / ta.offsetWidth;
        const cs = getComputedStyle(ta);
        const caret = ta.getBoundingClientRect().bottom - (parseFloat(cs.paddingBottom) + parseFloat(cs.borderBottomWidth)) * own;
        const q = pane.getBoundingClientRect();
        return { below: Math.round(caret - q.bottom), above: Math.round(q.top - caret), line: Math.round(parseFloat(cs.lineHeight) * own), hidden: ta.scrollHeight - ta.clientHeight, chars: ta.value.length };
      }, LONGER);
      if (!m) throw new Error('the AI instructions editor is not on the page');
      if (m.none) throw new Error('nothing scrolls after the paste, so the case would prove nothing');
      report(m.below <= 1 && m.above < -m.line && m.hidden <= 0, `C ${model} model, AI instructions at ${z}%: after a paste of ${m.chars} characters the caret is in view`,
        m.below > 1 ? `the caret is ${m.below} rect px below the pane` : m.above >= -m.line ? `the caret is above the pane (${m.above})` : `in view, ${-m.below} rect px above the pane's bottom, ${m.hidden} px of the text hidden`);
    } finally { await page.close(); }
  }
}

// D. How many times a key lays out a hidden twin, at the end and in the middle of a 50,000-character narrative.
if (want('D')) {
  const page = await scene(harness, 'review-docs', [1440, 900]);
  try {
    const m = await page.evaluate(async () => {
      const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
      if (!ta) return null;
      const line = 'Revenue is recognised at ship date and returns are netted off in the month they are processed. ';
      let v = ''; while (v.length < 50000) v += line + (v.length % 7 === 0 ? '\n' : '');
      ta.focus(); ta.select(); document.execCommand('insertText', false, v.slice(0, 50000));
      await new Promise((r) => setTimeout(r, 900));
      let twins = 0;
      const mo = new MutationObserver((recs) => { for (const r of recs) for (const n of r.addedNodes) if (n.nodeName === 'TEXTAREA' && n.getAttribute('aria-hidden') === 'true') twins++; });
      mo.observe(document.body, { childList: true });
      const run = async (at) => {
        const keys = [];
        for (let i = 0; i < 20; i++) {
          const pos = at === 'end' ? ta.value.length : 25000;
          ta.setSelectionRange(pos, pos);
          const h0 = ta.offsetHeight;
          twins = 0;
          const t0 = performance.now();
          document.execCommand('insertText', false, 'x');
          const ms = performance.now() - t0;
          await new Promise((r) => setTimeout(r, 0));
          keys.push({ ms, twins, grew: ta.offsetHeight !== h0 });
          await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 10)));
        }
        const ms = keys.map((k) => k.ms).sort((a, b) => a - b);
        return { still: keys.filter((k) => !k.grew).length, stillTwins: keys.filter((k) => !k.grew).reduce((s, k) => s + k.twins, 0), grewTwins: keys.filter((k) => k.grew).reduce((s, k) => s + k.twins, 0), grew: keys.filter((k) => k.grew).length, median: Math.round(ms[10] * 10) / 10, max: Math.round(ms[19] * 10) / 10 };
      };
      const end = await run('end');
      const mid = await run('middle');
      mo.disconnect();
      return { chars: ta.value.length, end, mid };
    });
    if (!m) throw new Error('no Docs narrative box on the page, so nothing was measured');
    report(m.end.stillTwins === 0 && m.end.grewTwins === 0, 'D typing at the end of a 50,000-character narrative lays out no twin',
      `${m.end.stillTwins + m.end.grewTwins} twins over 20 keys (${m.end.grew} of them grew the box); median ${m.end.median} ms a key, max ${m.end.max} (printed, not asserted)`);
    report(m.mid.stillTwins === 0 && m.mid.grewTwins <= m.mid.grew, 'D typing in the middle of it lays out a twin only for a key that changed the box\'s height',
      `${m.mid.stillTwins} twins over the ${m.mid.still} keys that left the height alone, ${m.mid.grewTwins} over the ${m.mid.grew} that changed it; median ${m.mid.median} ms a key, max ${m.mid.max} (printed, not asserted)`);
  } finally { await page.close(); }
}

// E. Properties: every box holds its rows and nothing below its cap scrolls itself.
if (want('E')) for (const q of ['s=measure', 's=measure&desc=long', 's=column']) {
  const page = await harness.newPage();
  try {
    await page.setViewport({ width: 420, height: 800, deviceScaleFactor: 1 });
    await page.goto(base.replace('harness.html', `propgrid.html?${q}`), { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(1500);
    const boxes = await page.evaluate(() => [...document.querySelectorAll('textarea')].filter((t) => t.getClientRects().length).map((t) => {
      const cs = getComputedStyle(t);
      const floor = t.rows * parseFloat(cs.lineHeight) + parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
      const cap = parseFloat(cs.maxHeight);
      return { chars: t.value.length, h: t.offsetHeight, floor: Math.round(floor), over: t.scrollHeight - t.clientHeight, capped: Number.isFinite(cap) && t.offsetHeight >= cap - 1 };
    }));
    if (!boxes.length) throw new Error(`no text box on propgrid.html?${q}, so nothing was measured`);
    const short = boxes.filter((x) => x.h < x.floor - 2);
    const scrolling = boxes.filter((x) => x.over > 0 && !x.capped);
    report(!short.length && !scrolling.length, `E Properties (${q}): every box holds its rows and none below its cap scrolls itself`,
      short.length ? `${short.length} of ${boxes.length} boxes under their rows, e.g. ${short[0].chars} characters in ${short[0].h} px for ${short[0].floor}`
        : scrolling.length ? `${scrolling.length} box(es) hide text below the cap, e.g. ${scrolling[0].over} px of ${scrolling[0].chars} characters`
          : `${boxes.length} boxes, heights ${boxes.map((x) => x.h).join(', ')} px`);
  } finally { await page.close(); }
}

// F. Connections at 2000 x 1200: Open ends each row's reading line.
if (want('F')) {
  const page = await scene(harness, 'connections', [2000, 1200]);
  try {
    const m = await page.evaluate(() => {
      // The cap in the row's own font (it is in ch), read from the token and not from the rule under test.
      const first = document.querySelector('[data-testid="hub-row-open"]')?.closest('.sem-list-row');
      if (!first) return { cap: 0, rows: [] };
      const probe = document.createElement('div');
      probe.style.cssText = 'position:absolute;visibility:hidden;width:var(--sem-row-max)';
      first.appendChild(probe);
      const cap = probe.getBoundingClientRect().width; probe.remove();
      const rows = [...document.querySelectorAll('[data-testid="hub-row-open"]')].filter((b) => b.getClientRects().length).map((b) => {
        const row = b.closest('.sem-list-row');
        return { open: Math.round(b.getBoundingClientRect().left - row.getBoundingClientRect().left), row: Math.round(row.getBoundingClientRect().width) };
      });
      return { cap: Math.round(cap), rows };
    });
    if (m.rows.length < 3) throw new Error(`only ${m.rows.length} Connections rows with Open on the page, so the case would prove nothing`);
    const worst = Math.max(...m.rows.map((r) => r.open));
    const wide = m.rows[0].row;
    if (wide < m.cap + 300) throw new Error(`the rows are ${wide} px wide against a ${m.cap} px reading line, so a missing cap would not show here`);
    report(worst <= m.cap + 64, 'F Connections 2000x1200: every row\'s Open sits at the end of its capped reading line',
      `Open at most ${worst} px into rows ${wide} px wide, the reading line capped at ${m.cap} px (${m.rows.length} rows)`);
  } finally { await page.close(); }
}

await harness.close();
await vscode.close();
server.close();
if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
