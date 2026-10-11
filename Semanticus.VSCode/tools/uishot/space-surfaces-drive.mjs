// Verifier's proof for m11/space-rule: three surfaces the 27 September space audit named that the lane did not
// bring under the rule. Each case states what the audit's fix requires and measures it on the REAL page.
//
//   1. DaxLib, the fixture the audit measured (three packages), 2000 x 1200: "Let the feed and the preview run to
//      the page bottom." The audit measured 601 px empty below two short boxes. The lane set a max height, so a short
//      feed is still a short box: the page must not keep more than 80 px empty below its content.
//   2. DaxLib with thirty packages, 1440 x 900: the feed now runs to the page bottom from its own top and pushes
//      "Installed in this model" (and its Uninstall buttons) below the fold. That panel must still start on screen.
//   3. Perspectives, the fixture the audit measured (two perspectives), 2000 x 1200: "Give each perspective column a
//      share of the width." The tick grid must span at least 90% of its card.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-surfaces-drive.mjs
import { SPACE_SCENES, start, wait } from './visual-scenes.mjs';

const THIRTY = `window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { daxLibSearch: Array.from({ length: 30 }, (_, i) => ({ id: 'Contoso.Pkg' + i, version: '1.' + i + '.0', description: 'Helper functions for area ' + i + ' of the finance model.', authors: ['Contoso BI'], downloads: 1000 - i })) });`;
const CASES = [
  { label: 'DaxLib feed and preview reach the page bottom (3 packages)', id: 'adv-daxlib', size: [2000, 1200],
    measure: () => {
      const main = document.querySelector('main'); const mb = main.getBoundingClientRect().bottom;
      let low = 0; for (const el of main.querySelectorAll('.sem-card, [class*="rounded"]')) { const r = el.getBoundingClientRect(); if (r.height > 40 && r.width > 200) low = Math.max(low, r.bottom); }
      const empty = Math.round(mb - low); return { ok: empty <= 80, text: `${empty} px empty below the last card (audit measured 601; the rule allows 80)` };
    } },
  { label: 'DaxLib "Installed in this model" starts on screen (30 packages)', id: 'adv-daxlib', size: [1440, 900], init: THIRTY,
    measure: () => {
      const h = [...document.querySelectorAll('main *')].find((e) => e.children.length === 0 && e.textContent.trim() === 'Installed in this model');
      if (!h) return { ok: false, text: 'the Installed panel was not found' };
      const main = document.querySelector('main'); const r = h.getBoundingClientRect(), m = main.getBoundingClientRect();
      return { ok: r.bottom <= m.bottom, text: `its heading ends at y=${Math.round(r.bottom)}, the page region ends at y=${Math.round(m.bottom)}` };
    } },
  // The two perspectives are pinned here (round 2 verifier): the scene now seeds eight for the space check, and with
  // eight the old 240 px cap never bites, so restoring it passed this case. With the audit's two it cannot.
  { label: 'Perspectives tick grid spans its card (2 perspectives)', id: 'edit-advanced', size: [2000, 1200],
    init: "window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { perspectives: [{ ref: 'perspective:Finance', name: 'Finance', description: '', members: ['table:Sales', 'measure:Sales/Total Sales', 'measure:Sales/Margin', 'column:Sales/SalesAmount'] }, { ref: 'perspective:Sales Ops', name: 'Sales Ops', description: '', members: ['table:Customer', 'column:Customer/CustomerName'] }] });",
    measure: () => {
      const hdr = [...document.querySelectorAll('main .sticky')].find((e) => e.textContent.includes('Object'));
      if (!hdr) return { ok: false, text: 'the tick grid header was not found' };
      const cells = [...hdr.children]; const right = Math.max(...cells.map((c) => c.getBoundingClientRect().right));
      let card = hdr; while (card && !/sem-card|rounded/.test(String(card.className))) card = card.parentElement;
      const cr = (card || hdr.parentElement).getBoundingClientRect();
      const share = (right - cr.left) / cr.width;
      return { ok: share >= 0.9, text: `the grid's last column ends at ${Math.round(share * 100)}% of the card width` };
    } },
];

const { server, browser, base } = await start();
const failures = [];
for (const c of CASES) {
  const s = SPACE_SCENES.find((x) => x.id === c.id);
  const page = await browser.newPage();
  try {
    await page.setViewport({ width: c.size[0], height: c.size[1], deviceScaleFactor: 1 });
    if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
    if (c.init) await page.evaluateOnNewDocument(c.init);
    await page.goto(s.page ? base.replace('harness.html', s.page) : `${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    if (s.run) await s.run(page);
    await wait(900);
    const r = await page.evaluate(c.measure);
    console.log(`${r.ok ? 'PASS' : 'FAIL'}  ${c.label} at ${c.size.join('x')}: ${r.text}`);
    if (!r.ok) failures.push(`${c.label}: ${r.text}`);
  } catch (e) {
    failures.push(`${c.label}: could not be driven (${String(e.message ?? e).slice(0, 160)})`);
  } finally {
    await page.close();
  }
}
await browser.close(); server.close();
if (failures.length) { console.error(`\n${failures.length} of ${CASES.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log(`\nall ${CASES.length} pass`);
