// Verifier's proofs for m11/space-rule round 3 (ee784631). Every case here was first seen in a real VS Code window
// (UAT lane k, Electron 42.10.0 / Chrome 148, 28 September 2026) and is reproduced in the harness so it can fail.
//
// A. Review, Docs: after a long narrative is written, the settings column and the preview (the two columns meant to
//    be as tall as the window) collapse to their 240 px floor and stay there, with 500 to 720 px of empty page under
//    them, and the settings column then hides its own Accent colour, Light/Dark, Logo and "Reset to defaults" inside
//    a column that scrolls. Cause: useFillHeight subtracts the page's scrollTop from the distance to the page bottom,
//    which is right for a box that scrolls with the page and wrong for a sticky one, whose top stops moving once it
//    sticks; the narrative growing re-measures the columns (their grid's ResizeObserver) while the page is scrolled.
//    Real window: 240 px columns, 522 px empty below them at 1440 x 1000, 722 px at 2000 x 1200.
// B. The window-sized pages (Field parameters, DaxLib) under VS CODE'S OWN ZOOM MODEL. VS Code starts Chromium with
//    --disable-blink-features=...StandardizedBrowserZoom (its out/main.js), so inside a webview getBoundingClientRect
//    is NOT scaled by a CSS zoom and currentCSSZoom does not exist. Round 3's useFillHeight divides a distance that is
//    already in the element's own pixels by the zoom, so in the real window the pages overshoot the page bottom at 80%
//    and 90% (the page scrolls around a list that also scrolls) and stop short at 110% and 120%. Round 2's code was
//    right there (measured with its bundle in the same window: 25 to 35 px at 80 to 110%). The harness browser runs
//    standardized zoom, which is why space-r2-drive case 8 (the verifier's own round 2 case, measured only in the
//    harness) passes while the real window fails. This case launches its browser with VS Code's switch.
// C. An empty growing box lost its rows floor: the AI instructions editor (rows 12) and each Docs narrative box
//    (rows 4, "four rows is the floor") shrink to one 48 px line when empty, because the hidden twin is measured at
//    height 0 without the box's rows. Round 2's fit kept 250 px and 90 px.
// D. A space scene whose click misses must fail. edit-instructions-editing still finds its button by the exact word
//    "Edit" and ignores a miss (visual-scenes.mjs), so a renamed button leaves the read view measured as the editing
//    state and the run exits 0. Planted with fixtures/space-plant-r3.cjs.
// E. A growing box can still end up shorter than its text with no scrollbar, at 100% and with no zoom at all: the
//    hidden twin is sized from el.clientWidth, a whole number, so when the box's text area is 309.5 px the twin is
//    310 px and wraps into fewer lines. Real window: Studio 1545 px wide, Docs narrative box 327.5 px, 40 px of the
//    text cut off and its first two lines scrolled out of reach.
// F. The same half pixel in the Properties grid's own copy of the fit: a long description below its cap is one line
//    short and scrolls inside itself.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-r3v-drive.mjs
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { SPACE_SCENES, start, wait, webRoot } from './visual-scenes.mjs';
import { findBrowser } from './browser.mjs';

const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const { server, browser, base } = await start();
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };
const NARRATIVE = Array.from({ length: 40 }, (_, i) => `Line ${i + 1}: revenue is recognised at ship date and returns are netted off in the month they are processed.`).join('\n');

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
  for (let i = 0; i < 6; i++) {
    const z = await page.evaluate(() => parseInt(document.querySelector('.studio-root')?.style.zoom || '100', 10));
    if (z === target) return;
    await page.evaluate((dy) => window.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, cancelable: true })), z < target ? -100 : 100);
    await wait(450);
  }
};

// A. Docs, the two window-height columns after a long narrative is written, read back at the top of the page.
for (const size of [[1440, 900], [2000, 1200]]) {
  const page = await scene(browser, 'review-docs', size);
  try {
    const typed = await page.evaluate((text) => {
      const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
      if (!ta) return false;
      ta.focus(); ta.select(); document.execCommand('insertText', false, text);
      return true;
    }, NARRATIVE);
    if (!typed) throw new Error('no Docs narrative box on the page, so nothing was measured');
    await wait(900);
    await page.evaluate(() => { document.querySelector('main').scrollTop = 0; });
    await wait(500);
    const m = await page.evaluate(() => {
      const main = document.querySelector('main');
      const mb = main.getBoundingClientRect().bottom;
      const grid = document.querySelector('.sem-docs-page');
      const [settings, preview] = grid ? [...grid.children] : [];
      const gap = (el) => (el ? Math.round(mb - el.getBoundingClientRect().bottom) : null);
      const reset = [...document.querySelectorAll('main button')].find((b) => b.textContent.trim() === 'Reset to defaults');
      let shown = null;
      if (reset) {
        let sc = reset.parentElement;
        while (sc && sc !== main && !(/(auto|scroll)/.test(getComputedStyle(sc).overflowY) && sc.scrollHeight > sc.clientHeight + 1)) sc = sc.parentElement;
        const r = reset.getBoundingClientRect();
        const q = (sc && sc !== main ? sc : main).getBoundingClientRect();
        shown = Math.round(100 * Math.max(0, Math.min(r.bottom, q.bottom) - Math.max(r.top, q.top)) / Math.max(1, r.height));
      }
      return { settings: gap(settings), preview: gap(preview), settingsH: settings ? Math.round(settings.getBoundingClientRect().height) : null, previewH: preview ? Math.round(preview.getBoundingClientRect().height) : null, reset: !!reset, shown, scrolls: main.scrollHeight > main.clientHeight + 4 };
    });
    if (m.settings == null || m.preview == null) throw new Error('the Docs page has no settings or preview column');
    // A sticky column may sit up to about a page margin past the fold at the top so that it fills when it sticks;
    // what it may not do is stop far short of the page bottom with nothing below it.
    const fits = (g) => g >= -64 && g <= 80;
    report(fits(m.settings) && fits(m.preview), `Docs ${size.join('x')}, a long narrative written: the settings and preview columns reach the page bottom`,
      `settings ${m.settingsH} px tall ending ${m.settings} px above the page bottom, preview ${m.previewH} px ending ${m.preview} px above it (the rule allows 80)`);
    report(m.reset && m.shown >= 75, `Docs ${size.join('x')}, a long narrative written: "Reset to defaults" is not hidden in a box that scrolls`,
      m.reset ? `${m.shown}% of it shows inside its column` : 'no Reset to defaults on the page');
  } finally { await page.close(); }
}

// C. Empty growing boxes keep their rows floor.
{
  const page = await scene(browser, 'edit-instructions-editing', [1440, 1000]);
  try {
    const m = await page.evaluate(async () => {
      const ta = document.getElementById('overview-instructions-editor');
      if (!ta) return null;
      ta.focus(); ta.select(); document.execCommand('delete');
      await new Promise((r) => setTimeout(r, 700));
      const cs = getComputedStyle(ta);
      const floor = ta.rows * parseFloat(cs.lineHeight) + parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
      return { h: ta.offsetHeight, rows: ta.rows, floor: Math.round(floor), chars: ta.value.length };
    });
    if (!m) throw new Error('the AI instructions editor is not on the page');
    report(m.chars === 0 && m.h >= m.floor - 2, 'AI instructions editor emptied: its rows stay the floor', `${m.h} px for ${m.rows} rows that need ${m.floor} px`);
  } finally { await page.close(); }
}
{
  const page = await scene(browser, 'review-docs', [1440, 900]);
  try {
    const boxes = await page.evaluate(() => [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].filter((t) => t.getClientRects().length && !t.value).map((t) => {
      const cs = getComputedStyle(t);
      return { h: t.offsetHeight, rows: t.rows, floor: Math.round(t.rows * parseFloat(cs.lineHeight) + parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth)) };
    }));
    if (!boxes.length) throw new Error('no empty Docs narrative box on the page, so nothing was measured');
    const short = boxes.filter((b) => b.h < b.floor - 2);
    report(!short.length, 'Docs, an empty narrative box: four rows stay the floor', short.length ? `${short.length} of ${boxes.length} empty boxes are ${short[0].h} px for ${short[0].rows} rows that need ${short[0].floor} px` : `all ${boxes.length} empty boxes hold their rows`);
  } finally { await page.close(); }
}

// E. A growing box measured half a pixel too wide. The fit sizes its hidden twin from el.clientWidth, which the
//    browser rounds to a whole pixel, while the box's text area is fractional (a 3-of-12 column is 327.5 px wide at a
//    1530 px Studio). When the fraction is .5 or more the twin is up to half a pixel WIDER than the text it stands
//    for, a line that breaks in the box does not break in the twin, and the box ends up that many lines short with
//    overflow hidden. Real window, 28 September: Studio 1545 px wide with its 15 px page scrollbar (so the same
//    327.5 px box), this text, a 630 px box for 668 px of text, 40 px cut off and its first two lines scrolled out of
//    reach. No zoom is involved. Round 2's fit, which read the box itself, holds all of it at the same width.
{
  let seed = 7;
  const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
  const WORDS = 'revenue returns ship date month grain order line customer store product margin budget forecast fiscal quarter week region channel discount promotion currency exchange rate net gross tax freight allocation inventory a an of to is in [Total Sales] CALCULATE(SUM(Sales[Amount])) https://example.invalid/some/long/path/that/does/not/break'.split(' ');
  const body = Array.from({ length: 12 }, (_, i) => `- ${i + 1}: ` + Array.from({ length: 3 + Math.floor(rnd() * 20) }, () => WORDS[Math.floor(rnd() * WORDS.length)]).join(' ')).join('\n');
  const seen = [];
  for (const w of [1530, 1531, 1532, 1533, 1534, 1535]) {
    const page = await scene(browser, 'review-docs', [w, 900]);
    try {
      const m = await page.evaluate(async (v) => {
        const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length);
        if (!ta) return null;
        ta.focus(); ta.select(); document.execCommand('insertText', false, v);
        await new Promise((r) => setTimeout(r, 1500));
        const cs = getComputedStyle(ta);
        const text = parseFloat(cs.width) - parseFloat(cs.borderLeftWidth) - parseFloat(cs.borderRightWidth) - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
        return { text, hidden: ta.scrollHeight - ta.clientHeight, ov: cs.overflowY };
      }, body);
      if (!m) throw new Error('no Docs narrative box on the page, so nothing was measured');
      seen.push({ w, ...m });
    } finally { await page.close(); }
  }
  const halves = seen.filter((s) => s.text % 1 >= 0.5);
  if (!halves.length) throw new Error(`no window from 1530 to 1535 gives the narrative box a text area of .5 px or more (${seen.map((s) => s.text).join(', ')}), so the case would prove nothing; pick widths where one does`);
  const cut = seen.filter((s) => s.hidden > 1 && /(hidden|clip)/.test(s.ov));
  report(!cut.length, 'Docs, a narrative box whose text area is a fraction of a pixel wide: it holds all of its text',
    cut.length ? cut.map((s) => `${s.w} px window, text area ${s.text} px: ${s.hidden} px of the text below the box's edge, overflow ${s.ov}`).join('; ')
      : `held at ${seen.map((s) => `${s.w} (${s.text} px)`).join(', ')}`);
}

// F. The Properties description keeps its own copy of the fit (media/propgrid/propgrid.js) and is half a pixel too
//    wide the same way: at the harness's 420 px panel the box's text area is 203.688 px and its twin 204 px, so the
//    long description gets a 310 px box for 328 px of text and scrolls inside itself, far under its own cap. That box
//    is overflow auto, so round 3's own proof (space-r3-drive case 4, which counts hidden overflow only) cannot see
//    it. Round 2's fit gave it 330 px with nothing hidden.
{
  const page = await browser.newPage();
  try {
    await page.setViewport({ width: 420, height: 800, deviceScaleFactor: 1 });
    await page.goto(base.replace('harness.html', 'propgrid.html?s=measure&desc=long'), { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(1500);
    const m = await page.evaluate(() => {
      const ta = [...document.querySelectorAll('textarea[data-commit-enter]')].find((t) => t.getClientRects().length && t.value.length > 400);
      if (!ta) return null;
      const cs = getComputedStyle(ta);
      return { chars: ta.value.length, h: ta.offsetHeight, over: ta.scrollHeight - ta.clientHeight, cap: parseFloat(cs.maxHeight), text: parseFloat(cs.width) - parseFloat(cs.borderLeftWidth) - parseFloat(cs.borderRightWidth) - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight) };
    });
    if (!m) throw new Error('no long Properties description on the page, so nothing was measured');
    const capped = Number.isFinite(m.cap) && m.h >= m.cap - 1;
    report(m.over <= 1 || capped, 'Properties, a long description under its cap: the box holds all of its text without scrolling itself',
      `${m.chars} characters, box ${m.h} px with ${m.over} px of its text below its edge, cap ${m.cap} px, text area ${m.text} px wide`);
  } finally { await page.close(); }
}
await browser.close();

// B. The window-sized pages in VS Code's own zoom model.
{
  const vsc = await puppeteer.launch({
    executablePath: process.env.SEMANTICUS_BROWSER || findBrowser({ quiet: true }), headless: true, protocolTimeout: 180000,
    args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', '--disable-blink-features=StandardizedBrowserZoom'],
  });
  try {
    for (const [size, zooms] of [[[1440, 900], [90, 80]], [[2000, 1200], [90, 80, 110, 120]]]) {
      for (const id of ['adv-fieldparameters', 'adv-daxlib']) {
        for (const z of zooms) {
          const page = await scene(vsc, id, size);
          try {
            await zoomTo(page, z);
            await wait(900);
            const m = await page.evaluate(() => {
              const root = document.querySelector('.studio-root');
              const zoom = parseFloat(getComputedStyle(root).zoom) || 1;
              const probe = document.createElement('div'); probe.style.cssText = 'position:absolute;left:0;top:0;width:100px;height:1px;visibility:hidden'; root.appendChild(probe);
              const scale = probe.getBoundingClientRect().width / 100; probe.remove();
              const main = document.querySelector('main');
              const low = Math.max(...[...main.querySelectorAll('.sem-card')].filter((c) => c.getClientRects().length).map((c) => c.getBoundingClientRect().bottom));
              return { zoom: root.style.zoom || '100%', vscodeModel: Math.abs(scale - 1) < 0.01 && !('currentCSSZoom' in root), empty: Math.round((main.getBoundingClientRect().bottom - low) * zoom / scale), scrolls: main.scrollHeight > main.clientHeight + 4 };
            });
            if (!m.vscodeModel) throw new Error('this browser is not in VS Code\'s zoom model, so the case would prove nothing');
            if (m.zoom !== `${z}%`) throw new Error(`the Studio text size reached ${m.zoom}, not ${z}%`);
            // Smaller text (80, 90%): the cards fill the window and the page does not scroll. Larger text (110, 120%):
            // the page may scroll when a page's own minimum heights force it, but the cards may not stop short.
            const ok = z < 100 ? !m.scrolls && m.empty >= 0 && m.empty <= 80 : m.empty <= 80;
            report(ok, `${id} ${size.join('x')} at Studio ${m.zoom} in VS Code's zoom model, the cards reach the page bottom`,
              m.empty < 0 ? `the cards run ${-m.empty} px past the page bottom${m.scrolls ? ' and the page scrolls' : ''}` : `${m.empty} px of the page empty below the cards (the rule allows 80)${m.scrolls ? ', and the page scrolls' : ''}`);
          } finally { await page.close(); }
        }
      }
    }
  } finally { await vsc.close(); }
}
server.close();

// D. The real scoreboard with the edit-instructions-editing scene's button renamed. It must not report a pass.
{
  const only = 'edit-instructions-editing';
  const out = mkdtempSync(join(tmpdir(), 'space-r3v-'));
  const r = spawnSync(process.execPath, ['--require', join(webRoot, 'tools', 'uishot', 'fixtures', 'space-plant-r3.cjs'), join(webRoot, 'tools', 'uishot', 'visual-check.mjs')], {
    cwd: webRoot, env: { ...process.env, PLANT: 'rename-edit', ONLY: only, SPACE: 'only', OUTDIR: out, SEMANTICUS_VISUAL_SEED: '' }, encoding: 'utf8', timeout: 900000,
  });
  rmSync(out, { recursive: true, force: true });
  const text = `${r.stdout ?? ''}\n${r.stderr ?? ''}`;
  const selftest = /self-test: .*(not caught|should score zero)/.test(text);
  const named = text.split('\n').some((l) => l.startsWith('  - ') && l.includes(only));
  report(!selftest && r.status !== 0 && named, 'visual-check with the AI instructions Edit button renamed',
    selftest ? 'the self-test failed, so the plant proved nothing' : r.status === 0 ? `exit 0: ${only} was scored as a pass` : `exit ${r.status}${named ? '' : `, but no problem names ${only}`}`);
}

if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
