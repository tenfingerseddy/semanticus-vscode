// Verifier's proofs for m11/space-rule, round 2 (67d0aca2). Each case is a way the lane still breaks Kane's space
// rule, or the coordinator's rulings for this round, that nothing in the lane catches.
//
// 1. Review, Docs: the narrative column still hides its own Save, Clear, Copy and Give to your assistant inside a
//    column that scrolls while the page does not. The fix in this round made the LEFT column's Include list scroll;
//    the narrative column (NarrativePane in documentation.tsx) is still `overflow-auto` as a whole. Picked on the
//    table the space scene itself seeds with its six-paragraph narrative, the section's Save is 0% visible at
//    1440 x 900 and at 2000 x 1200. This is round 1's D6, on the real page. Case 1.
// 2. The space check cannot see it. The list-row exemption (isListRow in screencheck.mjs) exempts a control when an
//    ancestor below its scroller repeats three times; each narrative section is its own wrapper (div.pt-3), so every
//    narrative control reads as a list row. The planted `?case=docs` page lays the controls out flat and so is still
//    counted; `?case=docs-nested` is the real markup, and scores 0. Case 2.
// 3. The review-docs scene never shows its long narrative in a writing box. LONG_NARRATIVE is seeded on tables and
//    measures, but the narrative column opens on the model, whose narrative is the harness's one sentence and two
//    bullets. D7 asked for a full-length Docs fixture; the writing boxes the audit's Docs fix is about stay short,
//    so the scene cannot catch that fix being undone. Case 3.
// 4. AI instructions: the card now spans six of twelve columns, and between 901 and about 1300 px of Studio width
//    that is narrower than its own 80ch reading column. The coordinator's ruling was to keep the 80ch column; at
//    1000 x 900 it is 444 px, at 1092 x 876 (the real VS Code window of the round 1 lane journey) 490 px, at
//    1200 x 900 536 px, each with 500 to 600 px of empty page beside the card. Case 4.
// 5. A scene whose click misses is scored as a pass. seg() ignores clickText's result, so a renamed or hidden switch
//    re-measures the base tab and reports it clean. The ratified design: a scene that cannot be measured "is
//    reported as not measured, never as a pass". Driven through the real visual-check.mjs. Case 5.
// 6. A space scene whose page throws is scored as a pass: the space loop in visual-check.mjs never reads
//    opened.errors (the screen loop does). Driven through the real visual-check.mjs. Case 6.
// 7. Row actions no longer line up. Capping the row's reading line (.sem-row-line) put a label of varying width
//    between the line and the button, so Connections' Open buttons zigzag across about 97 px at 2000 x 1200 and
//    Pending changes' reject buttons sit at two different x. On 0cf7a400 every one of them was in a single column.
//    Case 7.
// 8. The pages sized to the window (DaxLib, Field parameters, through useFillHeight) ignore the Studio text size: at
//    90% they stop 95 to 111 px short of the page bottom, at 80% 157 to 184 px. Field parameters has done this since
//    round 1; DaxLib joined it in round 2. Case 8.
//
// NOT HERE, because this harness cannot show it: in real VS Code (UAT lane k, Electron 42) a Studio text size of 80%
// leaves the AI instructions editor 60 px and a long Docs narrative 800 px shorter than their text, overflow hidden,
// no scrollbar. fitToText sets height:auto, the box collapses, the column around it stops overflowing and loses its
// scrollbar (19 to 21 px there), so the text is measured wider than it ends up. Headless Chromium gives scrollbars
// no layout width (measured: 2 px, the border, with and without --hide-scrollbars), so the width never changes here.
//
// Exit 1 when any case fails. Serves THIS worktree's built bundle (cd webview && npm run build first).
//   node tools/uishot/space-r2-drive.mjs
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { LONG_NARRATIVE, SPACE_SCENES, planSeed, start, wait, webRoot } from './visual-scenes.mjs';
import { checkSpace } from './screencheck.mjs';
import { applyTheme } from './themes.mjs';

const { server, browser, origin, base } = await start();
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };
async function scene(id, [w, h]) {
  const s = SPACE_SCENES.find((x) => x.id === id);
  const page = await browser.newPage();
  await page.setViewport({ width: w, height: h, deviceScaleFactor: 1 });
  if (s.plan) await page.evaluateOnNewDocument(planSeed());
  if (s.init) await page.evaluateOnNewDocument(s.init[0], s.init[1]);
  await page.goto(`${base}?${s.q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2600);
  if (s.run) await s.run(page);
  await wait(500);
  await applyTheme(page, 'dark');
  return page;
}
// The narrative column's controls: how much of each shows inside the column, and whether the page itself scrolls.
const narrativeReach = (page) => page.evaluate(() => {
  const asides = [...document.querySelectorAll('aside.sem-card')];
  const col = asides[asides.length - 1];
  const cr = col.getBoundingClientRect();
  const main = document.querySelector('main');
  const hidden = [...col.querySelectorAll('button, textarea, select')].filter((b) => b.getClientRects().length).map((b) => {
    const r = b.getBoundingClientRect();
    return { t: (b.innerText || b.tagName).trim().slice(0, 24), v: Math.max(0, Math.min(r.bottom, cr.bottom) - Math.max(r.top, cr.top)) / (r.height || 1) };
  }).filter((x) => x.v < 0.75);
  return { colScrolls: col.scrollHeight > col.clientHeight + 4, pageScrolls: main.scrollHeight > main.clientHeight + 4, hidden };
});

// 1. The real Docs page, on the table whose long narrative the scene seeds.
for (const size of [[1440, 900], [2000, 1200]]) {
  const page = await scene('review-docs', size);
  try {
    const picked = await page.evaluate(() => { const asides = [...document.querySelectorAll('aside.sem-card')]; const sel = asides[asides.length - 1].querySelector('select'); const o = [...sel.options].find((x) => /Sales/.test(x.textContent)); if (!o) return null; sel.value = o.value; sel.dispatchEvent(new Event('change', { bubbles: true })); return o.textContent; });
    await wait(1200);
    const r = await narrativeReach(page);
    const bad = !r.pageScrolls && r.colScrolls && r.hidden.length > 0;
    report(!bad, `Docs ${size.join('x')}, narrative on ${picked}`, bad ? `${r.hidden.length} controls under 75% visible inside the scrolling narrative column while the page does not scroll: ${r.hidden.slice(0, 5).map((x) => `"${x.t}" ${Math.round(x.v * 100)}%`).join(', ')}` : 'every narrative control is reachable without scrolling a box');
  } finally { await page.close(); }
}

// 2. The planted real markup: the check must count the hidden narrative Save.
{
  const page = await browser.newPage();
  try {
    await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
    await page.goto(`${origin}/tools/uishot/fixtures/space-misses.html?case=docs-nested`, { waitUntil: 'networkidle0', timeout: 30000 });
    await wait(300);
    const planted = await page.evaluate(() => { const col = [...document.querySelectorAll('aside.sem-card')].pop(); const cr = col.getBoundingClientRect(); return [...col.querySelectorAll('button')].filter((b) => b.textContent.trim() === 'Save').some((b) => { const r = b.getBoundingClientRect(); return Math.max(0, Math.min(r.bottom, cr.bottom) - Math.max(r.top, cr.top)) / r.height < 0.75; }); });
    const r = await checkSpace(page, 'r2:docs-nested', { ratchet: false, settle: 200 });
    const n = r.counts?.['hidden-action'] ?? 0;
    report(planted && n > 0, 'planted: the real Docs narrative markup, a section Save below the fold', planted ? `hidden-action ${n}` : 'the fixture planted nothing (no Save below the fold at 1440 x 900)');
  } finally { await page.close(); }
}

// 3. The review-docs scene's writing boxes must carry the long narrative the scene seeds.
{
  const page = await scene('review-docs', [1440, 900]);
  try {
    const lens = await page.evaluate(() => [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].map((t) => t.value.length));
    const most = Math.max(0, ...lens);
    report(most >= LONG_NARRATIVE.length, 'review-docs scene shows its full-length narrative in a writing box', `longest writing box ${most} characters (the scene seeds ${LONG_NARRATIVE.length}); boxes ${JSON.stringify(lens)}`);
  } finally { await page.close(); }
}

// 4. AI instructions keeps its 80ch reading column whenever the page has room for it.
for (const [id, size] of [['edit-instructions', [1000, 900]], ['edit-instructions', [1092, 876]], ['edit-instructions', [1200, 900]], ['edit-instructions-editing', [1092, 876]]]) {
  const page = await scene(id, size);
  try {
    const m = await page.evaluate(() => {
      const main = document.querySelector('main');
      const col = document.querySelector('[data-testid=overview-instructions] .xe-instr-text, [data-testid=overview-instructions] .xe-instr-editor');
      const probe = document.createElement('span'); probe.style.cssText = 'display:inline-block;width:80ch;position:absolute;visibility:hidden';
      col.parentElement.appendChild(probe); const ch80 = probe.getBoundingClientRect().width; probe.remove();
      const card = document.querySelector('[data-testid=overview-instructions]').getBoundingClientRect();
      return { col: col.getBoundingClientRect().width, ch80, room: main.clientWidth - 2 * 24 - 2 * 17, empty: main.getBoundingClientRect().right - card.right };
    });
    const ok = m.col >= m.ch80 - 2 || m.room < m.ch80;
    report(ok, `AI instructions ${id} ${size.join('x')}`, `reading column ${Math.round(m.col)} px of its ${Math.round(m.ch80)} px (80ch), with ${Math.round(m.empty)} px of empty page beside the card`);
  } finally { await page.close(); }
}

// 7. A list's row actions still line up in one column. The reading line is capped now, and a label of its own width
//    (the environment on Connections, the state on Pending changes) sits between that line and the action, so each
//    row's button lands at a different x. Round 1 had every Open at x=1759 and every reject at x=1936 (2000 wide).
for (const [id, label] of [['connections', 'Open'], ['plan-pending', '✕']]) {
  for (const size of [[2000, 1200], [1440, 900]]) {
    const page = await scene(id, size);
    try {
      const xs = await page.evaluate((l) => [...document.querySelectorAll('button')].filter((b) => b.innerText.trim() === l && b.getClientRects().length).map((b) => Math.round(b.getBoundingClientRect().left)), label);
      const spread = xs.length ? Math.max(...xs) - Math.min(...xs) : 0;
      report(xs.length > 1 && spread <= 2, `${id} ${size.join('x')}, the rows' "${label}" buttons in one column`, `${xs.length} buttons, x from ${Math.min(...xs)} to ${Math.max(...xs)} (${spread} px apart)`);
    } finally { await page.close(); }
  }
}
// 8. The pages sized to the window by useFillHeight (DaxLib, Field parameters) ignore the Studio text size. The hook
//    measures with getBoundingClientRect, which is in screen pixels, and writes a CSS height, which the Studio's
//    zoom then scales, so with the text smaller (more room) the cards stop well short of the page bottom. (Larger
//    text is not asserted: there the pages' own minimum heights can force the page to scroll, which the rule allows.)
for (const id of ['adv-daxlib', 'adv-fieldparameters']) {
  for (const [label, steps] of [['90%', [100]], ['80%', [100, 100]]]) {
    const page = await scene(id, [1440, 900]);
    try {
      for (const d of steps) { await page.evaluate((dy) => window.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, cancelable: true })), d); await wait(400); }
      await wait(900);
      const m = await page.evaluate(() => {
        const main = document.querySelector('main');
        const low = Math.max(...[...main.querySelectorAll('.sem-card')].filter((c) => c.getClientRects().length).map((c) => c.getBoundingClientRect().bottom));
        return { zoom: document.querySelector('.studio-root')?.style.zoom || '100%', empty: Math.round(main.getBoundingClientRect().bottom - low), scrolls: main.scrollHeight > main.clientHeight + 4 };
      });
      const ok = !m.scrolls && m.empty >= 0 && m.empty <= 80;
      report(ok, `${id} 1440x900 at Studio ${m.zoom}, the cards reach the page bottom`, m.scrolls ? `the cards run ${-m.empty} px past the page bottom and the page scrolls` : `${m.empty} px of the page empty below the cards (the rule allows 80)`);
    } finally { await page.close(); }
  }
}
await browser.close(); server.close();

// 5 and 6. The real scoreboard, with one planted fault in the scene it scores. It must not report a pass.
for (const [label, plantMode, only] of [
  ['a space scene whose click misses (the Notes switch hidden)', 'hide-notes', 'instructions-notes'],
  ['a space scene whose page throws', 'page-error', 'edit-instructions-editing'],
]) {
  const out = mkdtempSync(join(tmpdir(), 'space-r2-'));
  const r = spawnSync(process.execPath, ['--require', join(webRoot, 'tools', 'uishot', 'fixtures', 'space-plant.cjs'), join(webRoot, 'tools', 'uishot', 'visual-check.mjs')], {
    cwd: webRoot, env: { ...process.env, PLANT: plantMode, ONLY: only, SPACE: 'only', OUTDIR: out, SEMANTICUS_VISUAL_SEED: '' }, encoding: 'utf8', timeout: 900000,
  });
  rmSync(out, { recursive: true, force: true });
  const text = `${r.stdout ?? ''}\n${r.stderr ?? ''}`;
  const selftest = /self-test: .*(not caught|should score zero)/.test(text);
  // visual-check lists every problem as "  - <text>" after "visual check: N problem(s)"; any of them naming the scene
  // counts, whatever its wording.
  const named = text.split('\n').some((l) => l.startsWith('  - ') && l.includes(only));
  report(!selftest && r.status !== 0 && named, `visual-check with ${label}`, selftest ? 'the self-test failed, so the plant proved nothing' : r.status === 0 ? `exit 0: ${only} was scored as a pass` : `exit ${r.status}${named ? '' : `, but no problem names ${only}`}`);
}
if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
