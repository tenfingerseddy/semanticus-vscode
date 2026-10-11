// Verifier's proof for m11/space-rule: the boxes that now grow with their text must not move the page while a person
// types, and must not hide their text after the Studio text size changes.
//
// The space rule made three text boxes grow with their text instead of scrolling inside a short box: the AI
// instructions editor and the Docs narrative (useAutoGrow in webview/src/hooks.ts) and the Properties description
// (autoGrow in media/propgrid/propgrid.js, now up to 60vh). Each one sets the box to height:auto and back on every
// change. While the box is momentarily short, the region that scrolls the page loses its scroll room and its
// position is clamped, so a person typing at the end of a long text sees the page jump back up on every key and
// Save leave the screen.
//
// WHAT IS PROVEN HERE: with the text scrolled to its end and the caret after the last character, typing five
// characters (no new line) leaves the region's scroll position where it was. A new line may scroll the page down
// by a line to keep the caret in view; nothing here types one.
//
// AND: the Studio text size (Ctrl + wheel) is CSS zoom on the app root, which fires no window resize. useAutoGrow
// re-fits only when the text or the window width changes, and the boxes are overflow hidden, so after a zoom the
// Docs narrative keeps its old height and the end of its text is cut off with no scrollbar. The last case zooms
// Studio to 120% and requires the box to still show all of its text.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any box moves the page.
//   node tools/uishot/space-autogrow-drive.mjs
import { SPACE_SCENES, start, wait } from './visual-scenes.mjs';

const CASES = [
  { id: 'edit-instructions-editing', size: [900, 900], box: '#overview-instructions-editor', label: 'AI instructions editor' },
  { id: 'edit-instructions-editing', size: [1440, 900], box: '#overview-instructions-editor', label: 'AI instructions editor' },
  { id: 'review-docs', size: [900, 900], box: 'textarea[placeholder^="Markdown"]', label: 'Docs narrative', long: true },
  { id: 'propgrid-long', size: [420, 700], box: 'textarea[data-commit-enter]', label: 'Properties description' },
  { id: 'review-docs', size: [2000, 1200], box: 'textarea[placeholder^="Markdown"]', label: 'Docs narrative after Studio text size 120%', long: true, zoom: 2 },
];
const NARRATIVE = Array.from({ length: 40 }, (_, i) => `Line ${i + 1}: revenue is recognised at ship date and returns are netted off in the month they are processed.`).join('\n');

const { server, browser, base } = await start();
const failures = [];
for (const c of CASES) {
  const s = SPACE_SCENES.find((x) => x.id === c.id);
  const page = await browser.newPage();
  try {
    await page.setViewport({ width: c.size[0], height: c.size[1], deviceScaleFactor: 1 });
    if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
    await page.goto(s.page ? base.replace('harness.html', s.page) : `${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    if (s.run) await s.run(page);
    await wait(900);
    // The Docs fixture's narratives are one sentence: give the box a long one, the way a person pasting one would.
    if (c.long) {
      await page.evaluate((q, text) => { const ta = [...document.querySelectorAll(q)].find((t) => t.getClientRects().length); ta.focus(); ta.select(); document.execCommand('insertText', false, text); }, c.box, NARRATIVE);
      await wait(500);
    }
    // The region that scrolls the box: its nearest scrolling ancestor, else the document.
    const read = () => page.evaluate((q) => {
      const ta = [...document.querySelectorAll(q)].find((t) => t.getClientRects().length);
      let sc = ta.parentElement;
      while (sc && !(/(auto|scroll)/.test(getComputedStyle(sc).overflowY) && sc.scrollHeight > sc.clientHeight)) sc = sc.parentElement;
      sc = sc || document.scrollingElement;
      return { top: Math.round(sc.scrollTop), room: sc.scrollHeight - sc.clientHeight, len: ta.value.length };
    }, c.box);
    if (c.zoom) {
      // Two Ctrl + wheel steps: Studio at 120%. Then the box must still show every line of its text.
      for (let i = 0; i < c.zoom; i++) { await page.evaluate(() => window.dispatchEvent(new WheelEvent('wheel', { deltaY: -100, ctrlKey: true, cancelable: true }))); await wait(300); }
      await wait(800);
      const z = await page.evaluate((q) => { const ta = [...document.querySelectorAll(q)].find((t) => t.getClientRects().length);
        return { zoom: document.querySelector('.studio-root')?.style.zoom || '100%', hidden: ta.scrollHeight - ta.clientHeight, overflow: getComputedStyle(ta).overflowY }; }, c.box);
      const ok = z.hidden <= 4 || z.overflow !== 'hidden';
      console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.label} at ${c.size.join('x')}: Studio at ${z.zoom}, ${z.hidden} px of the text below the box's edge, overflow ${z.overflow}`);
      if (!ok) failures.push(`${c.label}: ${z.hidden} px of the text is cut off with no scrollbar`);
      continue;
    }
    await page.evaluate((q) => {
      const ta = [...document.querySelectorAll(q)].find((t) => t.getClientRects().length);
      ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
      let sc = ta.parentElement;
      while (sc && !(/(auto|scroll)/.test(getComputedStyle(sc).overflowY) && sc.scrollHeight > sc.clientHeight)) sc = sc.parentElement;
      (sc || document.scrollingElement).scrollTop = 1e6;
    }, c.box);
    await wait(300);
    const before = await read();
    await page.keyboard.type(' more', { delay: 60 });
    await wait(400);
    const after = await read();
    const moved = before.top - after.top;
    const ok = after.len === before.len + 5 && moved <= 4;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${c.label} at ${c.size.join('x')}: scroll position ${before.top} -> ${after.top} (${moved > 0 ? 'moved up ' + moved + ' px' : 'held'}), text ${before.len} -> ${after.len} characters`);
    if (!ok) failures.push(`${c.label} at ${c.size.join('x')}: the page moved up ${moved} px while typing at the end of the text`);
  } catch (e) {
    failures.push(`${c.label} at ${c.size.join('x')}: could not be driven (${String(e.message ?? e).slice(0, 160)})`);
  } finally {
    await page.close();
  }
}
await browser.close(); server.close();
if (failures.length) { console.error(`\n${failures.length} of ${CASES.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log(`\nall ${CASES.length} held their place`);
