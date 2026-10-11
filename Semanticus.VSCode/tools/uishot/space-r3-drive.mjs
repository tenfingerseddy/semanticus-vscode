// Round 3 of m11/space-rule: A GROWING BOX IS NEVER SHORTER THAN ITS TEXT.
//
// The three boxes that grow with their text (the AI instructions editor and the Docs narrative through useAutoGrow
// in webview/src/hooks.ts, and the Properties description through autoGrow in media/propgrid/propgrid.js) hide
// their overflow, so a box that is even slightly too short does not scroll: the end of the text is gone, with
// nothing on screen to say so. In a real VS Code window that is exactly what happened -- 60 px of the AI
// instructions editor at a Studio text size of 80%, 800 px of a Docs narrative, 60 px of a Docs narrative while
// plain typing at 100% -- and every harness proof passed, because headless Chromium gives scrollbars no layout
// width and the fault needs one. tools/uishot/scrollbar-width.mjs says what was measured and how the harness
// forces the one effect that matters; case 0 here proves that forcing still bites before anything else is trusted.
//
// Cases:
//   0. The forced scrollbar catches a fit that collapses its box, and leaves the same box alone when it is off.
//   1. The AI instructions editor, at Studio text sizes 100, 90, 80, 90, 100, 110 and 120 per cent, one real
//      Ctrl + wheel notch at a time, with the forced scrollbar on.
//   2. The Docs narrative, with a forty-line narrative typed into it, the same seven text sizes.
//   3. Typing at the end of a long text, with the forced scrollbar on: every key leaves the box holding all of it.
//   4. The Properties description, with the forced scrollbar on, at the panel's own widths.
//   5. The Properties panel narrowed with no window resize at all: only the ResizeObserver can catch that, and
//      nothing in the lane held it up.
//   6. A paste that puts the caret far below the fold: the pane follows it instead of leaving the person typing
//      where they cannot see.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-r3-drive.mjs
import { SPACE_SCENES, start, wait } from './visual-scenes.mjs';
import { hasClassicScrollbars, installClassicScrollbars } from './scrollbar-width.mjs';

const { server, browser, origin, base } = await start();
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };

// Longer than the editor's own limit-free fixture, so a paste of it runs well past the bottom of the pane.
const LONGER = Array.from({ length: 30 }, (_, i) => `Paragraph ${i + 1}. Answer as a careful finance analyst, and say plainly when the model cannot answer the question asked.`).join('\n\n');
const NARRATIVE = Array.from({ length: 40 }, (_, i) => `Line ${i + 1}: revenue is recognised at ship date and returns are netted off in the month they are processed.`).join('\n');
// Out to 80 per cent and back in to 120, one real notch at a time, the way a person turns the wheel.
const ZOOM_STEPS = [100, 100, -100, -100, -100, -100];

async function openScene(id, [w, h], { classic = true } = {}) {
  const s = SPACE_SCENES.find((x) => x.id === id);
  const page = await browser.newPage();
  if (classic) await installClassicScrollbars(page);
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
  await page.goto(s.page ? base.replace('harness.html', s.page) : `${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2600);
  if (s.run) await s.run(page);
  await wait(900);
  if (classic && !await hasClassicScrollbars(page)) throw new Error(`the forced scrollbar did not reach ${id}, so nothing it measures means anything`);
  return page;
}

// Every visible box of this shape: how much of its text is below its own lower edge, and whether it could be
// reached by scrolling the box at all.
const boxesBelow = (page, sel) => page.evaluate((q) => [...document.querySelectorAll(q)].filter((t) => t.getClientRects().length && t.value)
  .map((t) => ({ hidden: Math.round(t.scrollHeight - t.clientHeight), overflow: getComputedStyle(t).overflowY, height: Math.round(t.clientHeight), chars: t.value.length })), sel);
const cutOff = (boxes) => boxes.filter((b) => b.hidden > 4 && /(hidden|clip)/.test(b.overflow));
// A case that found no box passes every assertion about boxes, which is how a proof quietly stops proving
// anything after a rename. Every read of a page asserts it found one.
function boxesOrThrow(boxes, what) {
  if (!boxes.length) throw new Error(`no ${what} on the page, so nothing was measured`);
  return boxes;
}
const studioZoom = (page) => page.evaluate(() => document.querySelector('.studio-root')?.style.zoom || '100%');

// 0. The self-test. The fixture holds the fit the product used to have and nothing else.
for (const classic of [true, false]) {
  const page = await browser.newPage();
  try {
    if (classic) await installClassicScrollbars(page);
    await page.setViewport({ width: 900, height: 700, deviceScaleFactor: 1 });
    await page.goto(`${origin}/tools/uishot/fixtures/space-scrollbar.html`, { waitUntil: 'networkidle0', timeout: 30000 });
    await wait(300);
    if (classic && !await hasClassicScrollbars(page)) throw new Error('the forced scrollbar did not reach its own self-test page');
    // The fixture fits itself as it parses, which is before anything can be added to the page. Fit it again now,
    // with the scrollbar in place, which is the state under test.
    await page.evaluate(() => window.fitByCollapsing());
    await wait(200);
    const [box] = boxesOrThrow(await boxesBelow(page, '#t'), 'collapsing-fit box');
    const ok = classic ? box.hidden > 4 : box.hidden <= 4;
    report(ok, `self-test: a fit that collapses its box, forced scrollbar ${classic ? 'on' : 'off'}`,
      classic ? `${box.hidden} px of the text below the box's edge (the forced scrollbar must show this fault)`
        : `${box.hidden} px below the edge (with no forced scrollbar the same fit is fine, so the emulation is what showed it)`);
  } finally { await page.close(); }
}

// 1 and 2. The Studio text size, one real notch at a time, from 100 out to 80 and back in to 120.
// A box whose OWN max-width binds cannot be widened by a scrollbar going away, so it is immune and proves
// nothing; the narrow window is where the AI instructions editor takes its width from the card around it, which
// is the state the real VS Code window was in when 60 px of it went missing.
for (const c of [
  { id: 'edit-instructions-editing', size: [640, 900], box: '#overview-instructions-editor', label: 'AI instructions editor, a narrow window' },
  { id: 'edit-instructions-editing', size: [1440, 1000], box: '#overview-instructions-editor', label: 'AI instructions editor' },
  { id: 'review-docs', size: [1440, 1000], box: 'textarea[placeholder^="Markdown"]', label: 'Docs narrative', long: true },
]) {
  const page = await openScene(c.id, c.size);
  try {
    if (c.long) {
      await page.evaluate((q, text) => { const ta = [...document.querySelectorAll(q)].find((t) => t.getClientRects().length); ta.focus(); ta.select(); document.execCommand('insertText', false, text); }, c.box, NARRATIVE);
      await wait(700);
    }
    const seen = [];
    let short = false;
    for (const step of [0, ...ZOOM_STEPS]) {
      if (step) { await page.evaluate((dy) => window.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, cancelable: true })), step); await wait(500); }
      await wait(400);
      const zoom = await studioZoom(page);
      const bad = cutOff(boxesOrThrow(await boxesBelow(page, c.box), c.label));
      seen.push(zoom);
      if (bad.length) { short = true; report(false, `${c.label} at Studio ${zoom}`, `${bad[0].hidden} px of the text is below the box's edge with overflow ${bad[0].overflow}`); break; }
    }
    if (!short) {
      const sizes = [...new Set(seen)];
      const reached = ['80%', '90%', '100%', '110%', '120%'].filter((z) => sizes.includes(z));
      const ok = reached.length === 5;
      report(ok, `${c.label}, every Studio text size from 80% to 120%`, ok ? `all of its text shows at ${sizes.join(', ')}` : `only reached ${sizes.join(', ')}, so 80 to 120 per cent was not tested`);
    }
  } finally { await page.close(); }
}

// 3. Typing at the end of a long text. Every key re-fits the box, and every re-fit is a chance to leave it short.
{
  const page = await openScene('review-docs', [1440, 1000]);
  try {
    await page.evaluate((text) => { const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length); ta.focus(); ta.select(); document.execCommand('insertText', false, text); }, NARRATIVE);
    await wait(700);
    await page.evaluate(() => { const ta = [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length); ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length); });
    let worst = null;
    for (const k of ['m', 'o', 'r', 'e', ' ', 'n', 'o', 'w']) {
      await page.keyboard.type(k);
      await wait(120);
      const bad = cutOff(boxesOrThrow(await boxesBelow(page, 'textarea[placeholder^="Markdown"]'), 'Docs narrative'));
      if (bad.length && (!worst || bad[0].hidden > worst.hidden)) worst = bad[0];
    }
    report(!worst, 'Docs narrative, typing at the end of a long text', worst ? `${worst.hidden} px of the text ended below the box's edge` : 'the box held all of its text through every key');
  } finally { await page.close(); }
}

// 6. A paste that lands the caret far below the fold. The scroll positions are held so typing does not move the
//    page, and that same hold used to stop the browser following the caret: pasting a long text into the AI
//    instructions editor left the pane at its top with the caret about 600 px below anything visible, and typing
//    went on out of sight until the person scrolled.
{
  const page = await openScene('edit-instructions-editing', [1440, 1000]);
  try {
    const paste = await page.evaluate((text) => {
      const ta = document.getElementById('overview-instructions-editor');
      let pane = ta.parentElement;
      while (pane && !(/(auto|scroll)/.test(getComputedStyle(pane).overflowY) && pane.scrollHeight > pane.clientHeight)) pane = pane.parentElement;
      pane = pane || document.scrollingElement;
      pane.scrollTop = 0;
      ta.focus(); ta.select();
      document.execCommand('insertText', false, text);
      return true;
    }, LONGER);
    if (!paste) throw new Error('the AI instructions editor is not on the page');
    await wait(900);
    const m = await page.evaluate(() => {
      const ta = document.getElementById('overview-instructions-editor');
      let pane = ta.parentElement;
      while (pane && !(/(auto|scroll)/.test(getComputedStyle(pane).overflowY) && pane.scrollHeight > pane.clientHeight)) pane = pane.parentElement;
      pane = pane || document.scrollingElement;
      const r = ta.getBoundingClientRect(); const q = pane.getBoundingClientRect();
      const line = parseFloat(getComputedStyle(ta).lineHeight) || 20;
      return { below: Math.round(r.bottom - q.bottom), line: Math.round(line), scrolled: Math.round(pane.scrollTop) };
    });
    const ok = m.below <= m.line;
    report(ok, 'a paste leaves the caret where the person can see it', ok
      ? `the end of the pasted text sits inside the pane (${m.below} px past its edge, one line is ${m.line}); the pane followed it by ${m.scrolled} px`
      : `the caret is ${m.below} px below the pane's lower edge with the pane at ${m.scrolled}`);
  } finally { await page.close(); }
}

// 4 and 5. The Properties description: the same box in the Properties grid, which has its own copy of the fit.
{
  const page = await openScene('propgrid-long', [420, 700]);
  try {
    const bad = cutOff(boxesOrThrow(await boxesBelow(page, 'textarea[data-commit-enter]'), 'Properties description'));
    report(!bad.length, 'Properties description, forced scrollbar on', bad.length ? `${bad[0].hidden} px of the text is below the box's edge` : 'the box holds all of its text');
    // The panel narrowed with NO window resize: a text-size change and a dragged panel edge both look like this,
    // and only the box's own ResizeObserver can see either of them.
    const before = boxesOrThrow(await boxesBelow(page, 'textarea[data-commit-enter]'), 'Properties description')[0];
    await page.evaluate(() => { document.body.style.width = '300px'; });
    await wait(600);
    const after = boxesOrThrow(await boxesBelow(page, 'textarea[data-commit-enter]'), 'Properties description')[0];
    const grew = after.height > before.height;
    const ok = grew && !cutOff([after]).length;
    report(ok, 'Properties description re-fits when the panel narrows with no window resize',
      `${before.height} px at the full width, ${after.height} px at 300 px${cutOff([after]).length ? `, with ${after.hidden} px of the text below its edge` : ''}`);
  } finally { await page.close(); }
}

await browser.close(); server.close();
if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
