// Verifier's proof for m11/space-rule: breaks of Kane's space rule that the new space check scores as clean.
//
// 1. A card stretched to the page bottom hides the empty space from the check. It measures "room" as the empty
//    band below the lowest boxed element, and a stretched card's own border is that element, so a short box that
//    scrolls inside the card, with Save cut off inside it, is never "a box that scrolls while the page has room".
//    AI instructions is built this way since this lane (.sem-instructions-page, styles.css), so the audit's big
//    fault can come back on that page and the ratchet will not see it. Cases 1 and 3.
// 2. The hidden-action rule was narrowed to pages with 80 px of empty room. The ratified rule 2 is "a button, link or
//    input less than 75% visible inside a scrolling ancestor, while the page root does not scroll". A page that fills
//    the window with a side panel that scrolls, hiding that panel's own Save, is scored clean. Docs does this at
//    900 x 900 (Save, Copy, Give to your assistant) and 1440 x 900 (Reset to defaults). Cases 2 and 4.
//
// Every case must be caught (a count above zero in the named kind). Exit 1 when the check misses any.
//   node tools/uishot/space-check-misses-drive.mjs      (after cd webview && npm run build)
import { SPACE_SCENES, start, wait } from './visual-scenes.mjs';
import { checkSpace } from './screencheck.mjs';
import { applyTheme } from './themes.mjs';

const SHORT_BOX = "document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = '.xe-instr-text{max-height:200px !important;overflow-y:auto !important}'; document.head.appendChild(st); });";
const CASES = [
  { label: 'planted: text and Save in a 200 px scroller inside a stretched card', fixture: 'stretched', want: ['boxed-scroll', 'hidden-action'] },
  { label: 'planted: Save and Copy hidden in a scrolling side panel, page does not scroll', fixture: 'sidepanel', want: ['hidden-action'] },
  { label: 'AI instructions: the text back in a 200 px scrolling box', scene: 'edit-instructions', init: SHORT_BOX, want: ['boxed-scroll'] },
  // Was the real Review, Docs page. That page is fixed (the long Include list is what scrolls now, and the column's
  // actions sit outside the scroller), so there is no fault left on it to catch. The shape it had is planted here,
  // where a fix to the product cannot quietly retire the case: three cards at the window's height, each scrolling on
  // its own so the page never does, and the narrative column's Save below its fold.
  { label: 'planted: the Docs shape, a narrative Save below the fold in a scrolling column', fixture: 'docs', want: ['hidden-action'] },
];

const { server, browser, origin, base } = await start();
const failures = [];
for (const c of CASES) {
  const page = await browser.newPage();
  try {
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    let opts = { ratchet: false };
    if (c.fixture) {
      await page.goto(`${origin}/tools/uishot/fixtures/space-misses.html?case=${c.fixture}`, { waitUntil: 'networkidle0', timeout: 30000 });
      opts = { ...opts, settle: 200 };
    } else {
      const s = SPACE_SCENES.find((x) => x.id === c.scene);
      if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
      if (c.init) await page.evaluateOnNewDocument(c.init);
      await page.goto(`${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
      await wait(2600);
      if (s.run) await s.run(page);
      await wait(400);
      await applyTheme(page, 'dark');
      opts = { ...opts, root: s.root ?? null, prepare: s.prepare ?? null };
    }
    const r = await checkSpace(page, `misses:${c.fixture ?? c.scene}`, opts);
    const missed = c.want.filter((k) => !(r.counts?.[k] > 0));
    console.log(`${missed.length ? 'FAIL' : 'PASS'}  ${c.label}: ${c.want.map((k) => `${k} ${r.counts?.[k] ?? '-'}`).join(', ')}`);
    if (missed.length) failures.push(`${c.label}: the space check scored ${missed.join(' and ')} 0`);
  } catch (e) {
    failures.push(`${c.label}: could not be driven (${String(e.message ?? e).slice(0, 160)})`);
  } finally {
    await page.close();
  }
}
await browser.close(); server.close();
if (failures.length) { console.error(`\n${failures.length} of ${CASES.length} missed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log(`\nall ${CASES.length} caught`);
