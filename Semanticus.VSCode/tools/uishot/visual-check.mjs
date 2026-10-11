// The One Visual Language scoreboard: renders all 22 Studio pages on the mock engine, scores each with the
// screen check (gap, margin, padding, grid, nesting, and text contrast in Dark Modern, Light Modern and Dark High
// Contrast) and prints where every page stands against its baseline. Each page is also shot in the three themes.
//
//   TMPDIR=... OUTDIR=... SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/visual-check.mjs
//   ONLY=plan-start,pro node tools/uishot/visual-check.mjs        just those pages
//   SEMANTICUS_VISUAL_SEED=1 node tools/uishot/visual-check.mjs   lower the baseline where pages got better
//   SPACE=only node tools/uishot/visual-check.mjs                 only the space family; SPACE=0 skips it
//
// THE SPACE FAMILY (Kane's space rule, 27 September 2026) scores SPACE_SCENES at 2000 x 1200, 1440 x 900 and
// 900 x 900 in Dark Modern, with full-length fixtures, and ratchets the counts in visual-baseline.json's `space`
// block. Its own self-test (fixtures/visual-space.html) must score zero when clean and catch a planted boxed
// scroll, a cut-off Save and a page cap before any page is scored.
//
// It first proves the check on a page of its own (fixtures/visual-grid.html): built from the shared primitives
// it must score zero in every kind and every theme, and with a planted 13 px gap it must fail. If either goes the wrong way nothing else is
// scored, because a check that cannot see a 13 px gap proves nothing about 22 pages.
//
// Exit code 1 when a page is worse than its baseline, a page could not be measured, or the self-test failed.
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { SCENES, SPACE_SCENES, openScene, start, wait, webRoot } from './visual-scenes.mjs';
import { KINDS, SPACE_KINDS, judge } from './screen-judge.mjs';
import { checkScreen, checkSpace, measureScreen, readScreenBaseline, screenFailures, spaceFailures } from './screencheck.mjs';
import { THEMES, applyTheme } from './themes.mjs';

const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'visual-check');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
const { server, browser, origin, base } = await start();
const problems = [];

// ---- self-test -----------------------------------------------------------------------------------------
async function fixture(gap) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`${origin}/tools/uishot/fixtures/visual-grid.html?gap=${gap}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(200);
  const v = judge(await measureScreen(page));
  for (const t of Object.keys(THEMES)) {
    await applyTheme(page, t); await wait(300);
    await page.screenshot({ path: `${OUT}/selftest-gap-${gap}-${t}.png` });
  }
  await page.close();
  return v;
}
const SPACE_MODE = process.env.SPACE ?? '1';
async function spaceFixture(bad) {
  const page = await browser.newPage();
  await page.goto(`${origin}/tools/uishot/fixtures/visual-space.html?bad=${bad}`, { waitUntil: 'networkidle0', timeout: 30000 });
  const r = await checkSpace(page, `selftest-space-${bad}`, { settle: 200, ratchet: false });
  await page.close();
  return r;
}
const clean = await fixture(12);
const planted = await fixture(13);
console.log(`self-test: primitives page ${KINDS.map((k) => `${k} ${clean.counts[k]}`).join('  ')}`);
console.log(`self-test: planted 13px    ${KINDS.map((k) => `${k} ${planted.counts[k]}`).join('  ')}`);
if (clean.total !== 0) problems.push(`self-test: the page built from the shared primitives should score zero and scored ${clean.total}: ${clean.details.join('; ')}`);
if (planted.counts.gap === 0) problems.push('self-test: the planted 13 px gap was not caught');
if (SPACE_MODE !== '0') {
  const ok = await spaceFixture(0); const box = await spaceFixture(1); const cap = await spaceFixture(2);
  if (!ok.counts || ok.total !== 0) problems.push(`self-test: the clean space page should score zero and scored ${ok.total}: ${ok.details.join('; ')}`);
  if (!box.counts?.['boxed-scroll']) problems.push('self-test: the planted boxed scroll was not caught');
  if (!box.counts?.['hidden-action']) problems.push('self-test: the planted cut-off Save was not caught');
  if (!cap.counts?.['page-cap']) problems.push('self-test: the planted page cap was not caught');
}
if (problems.length) {
  console.error(problems.join('\n'));
  await browser.close(); server.close();
  process.exit(1);
}

// ---- the 22 pages --------------------------------------------------------------------------------------
const baseline = readScreenBaseline().pages;
const rows = [];
for (const s of SPACE_MODE === 'only' ? [] : SCENES) {
  if (only && !only.includes(s.id)) continue;
  let page;
  try {
    const opened = await openScene(browser, base, s);
    page = opened.page;
    const r = await checkScreen(page, s.id, { root: s.root ?? null, prepare: s.prepare ?? null });
    for (const t of Object.keys(THEMES)) {
      await applyTheme(page, t); await wait(300);
      await page.screenshot({ path: `${OUT}/${s.id}-${t}.png` });
    }
    await applyTheme(page, null);
    if (opened.errors.length) problems.push(`${s.id}: page errors: ${opened.errors.slice(0, 2).join(' | ')}`);
    rows.push({ s, r });
  } catch (e) {
    problems.push(`${s.id}: ${String(e.message ?? e).slice(0, 200)}`);
  } finally {
    if (page) await page.close();
  }
}
// ---- the space family -------------------------------------------------------------------------------------
const spaceRows = [];
for (const s of SPACE_MODE === '0' ? [] : SPACE_SCENES) {
  if (only && !only.includes(s.id)) continue;
  let page;
  try {
    const opened = await openScene(browser, base, s);
    page = opened.page;
    await applyTheme(page, 'dark');
    const r = await checkSpace(page, s.id, { root: s.root ?? null, prepare: s.prepare ?? null,
      shoot: (p, z) => p.screenshot({ path: `${OUT}/space-${s.id}-${z.width}x${z.height}.png` }) });
    if (process.env.SPACE_LIGHT) { await applyTheme(page, 'light'); await wait(300); await page.screenshot({ path: `${OUT}/space-${s.id}-light.png` }); }
    // The same as the screen loop above: a page that threw is a page whose layout means nothing, so it is reported
    // and never scored as a pass. This loop used to read the counts and not the errors.
    if (opened.errors.length) problems.push(`${s.id}: space: page errors: ${opened.errors.slice(0, 2).join(' | ')}`);
    spaceRows.push({ s, r });
  } catch (e) {
    problems.push(`${s.id}: space: ${String(e.message ?? e).slice(0, 200)}`);
  } finally {
    if (page) await page.close();
  }
}
await browser.close(); server.close();
if (spaceRows.length) {
  const sb = readScreenBaseline().space ?? {};
  // A scene that leaves the list takes its fault with it and nothing says so: the baseline keeps the key, the
  // scoreboard is one row shorter, and the run still exits 0. A full run scores every scene the baseline knows.
  if (!only && SPACE_MODE !== '0') {
    const scored = new Set(spaceRows.map(({ s }) => s.id));
    const missing = Object.keys(sb).filter((k) => !scored.has(k));
    if (missing.length) problems.push(`the space baseline has ${Object.keys(sb).length} scenes and this run scored ${scored.size}; nothing scored ${missing.join(', ')}`);
  }
  console.log(`\nSpace scoreboard, 2000 x 1200 / 1440 x 900 / 900 x 900, Dark Modern, full-length fixtures.\n`);
  console.log(`${'page'.padEnd(40)}${SPACE_KINDS.map((k) => k.padStart(15)).join('')}   base`);
  for (const { s, r } of spaceRows) {
    const b = sb[s.id]; const bt = b ? SPACE_KINDS.reduce((a, k) => a + (b[k] ?? 0), 0) : '-';
    console.log(`${s.label.slice(0, 39).padEnd(40)}${SPACE_KINDS.map((k) => String(r.counts ? r.counts[k] : '-').padStart(15)).join('')}   ${bt}`);
  }
  writeFileSync(`${OUT}/space-check.json`, JSON.stringify(spaceRows.map(({ s, r }) => ({ id: s.id, label: s.label, counts: r.counts, details: r.details, failures: r.failures })), null, 1));
}

// ---- scoreboard ----------------------------------------------------------------------------------------
const uniq = (xs) => [...new Set(xs.map((x) => Math.round(x)))].sort((a, b) => a - b);
console.log('\nOne Visual Language scoreboard, 1440 x 900, mock engine. Wanted: gaps 12, margin 24, padding 16 (inset 12), edges on the grid, no card in a card,');
console.log('and no text under 4.5:1 in Dark Modern (c-dark), Light Modern (c-light) or Dark High Contrast (c-hc).\n');
console.log(`${'page'.padEnd(26)}${'gaps'.padEnd(18)}${'margin'.padEnd(8)}${'padding'.padEnd(16)}${KINDS.map((k) => k.replace('contrast-', 'c-').padStart(8)).join('')}   total  base  verdict`);
let sum = 0; let sumBase = 0; let passing = 0;
for (const { s, r } of rows) {
  const m = r.measurement;
  const gaps = m ? uniq([...m.vgaps, ...m.hgaps].map((g) => g.gap)) : [];
  const top = m ? m.cards.filter((c) => c.depth === 0) : [];
  const margin = m ? (top.length ? Math.round(Math.min(...top.map((c) => c.x)) - m.root.contentLeft) : (m.textLeft == null ? '-' : Math.round(m.textLeft))) : '-';
  const pads = m ? uniq(m.cards.filter((c) => c.depth <= 1 && c.w >= 200 && c.h >= 48 && c.insetLeft != null).map((c) => c.insetLeft)) : [];
  const b = baseline[s.id];
  const baseTotal = b ? KINDS.reduce((a, k) => a + (b[k] ?? 0), 0) : '-';
  const total = r.counts ? r.total : '-';
  if (typeof total === 'number') sum += total;
  if (typeof baseTotal === 'number') sumBase += baseTotal;
  if (total === 0) passing++;
  const verdict = r.failures.length ? 'WORSE' : total === 0 ? 'PASS' : r.lowerable?.length ? 'better, lower the baseline' : 'at baseline';
  console.log(`${s.label.slice(0, 25).padEnd(26)}${JSON.stringify(gaps).slice(0, 17).padEnd(18)}${String(margin).padEnd(8)}${JSON.stringify(pads).slice(0, 15).padEnd(16)}${KINDS.map((k) => String(r.counts ? r.counts[k] : '-').padStart(8)).join('')}   ${String(total).padStart(5)}  ${String(baseTotal).padStart(4)}  ${verdict}`);
}
console.log(`\n${passing} of ${rows.length} pages pass. ${sum} screen problems in total (baseline ${sumBase}).`);
writeFileSync(`${OUT}/visual-check.json`, JSON.stringify(rows.map(({ s, r }) => ({ id: s.id, label: s.label, counts: r.counts, details: r.details, failures: r.failures })), null, 1));

const failed = [...problems, ...screenFailures(), ...spaceFailures()];
if (failed.length) {
  console.error(`\nvisual check: ${failed.length} problem(s)`);
  for (const f of failed) console.error(`  - ${f}`);
  process.exit(1);
}
