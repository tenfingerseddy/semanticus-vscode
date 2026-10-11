// The visual audit driver, moved into the repository from the 23 September 2026 audit that measured all 22
// Studio pages (visual-audit.json beside the One Visual Language proposal). It renders every page on the mock
// engine and writes two shots per page for a person to read: the page as it is, and the same page with every
// gap between cards drawn on it in its own colour (12 in green). It also runs the screen check on each page,
// so it fails when a page is worse than tools/uishot/visual-baseline.json. The scoreboard is visual-check.mjs.
//
//   TMPDIR=... OUTDIR=... SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/visual-audit-drive.mjs
//   ONLY=plan-start,pro node tools/uishot/visual-audit-drive.mjs
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { SCENES, openScene, start, wait, webRoot } from './visual-scenes.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';

const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'visual-audit');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
const { server, browser, base } = await start();
const results = {};
const problems = [];
for (const s of SCENES) {
  if (only && !only.includes(s.id)) continue;
  let page;
  try {
    const opened = await openScene(browser, base, s);
    page = opened.page;
    const r = await checkScreen(page, s.id, { root: s.root ?? null, prepare: s.prepare ?? null });
    await page.screenshot({ path: `${OUT}/before-${s.id}.png` });
    if (r.measurement) {
      await page.evaluate((m, t) => window.__semAnnotate(m, t), r.measurement, s.label);
      await wait(100);
      await page.screenshot({ path: `${OUT}/annotated-${s.id}.png` });
    }
    results[s.id] = { label: s.label, area: s.area, counts: r.counts, details: r.details, errors: opened.errors, measurement: r.measurement };
    if (opened.errors.length) problems.push(`${s.id}: page errors: ${opened.errors.slice(0, 2).join(' | ')}`);
  } catch (e) {
    results[s.id] = { label: s.label, area: s.area, error: String(e.message ?? e).slice(0, 300) };
    problems.push(`${s.id}: ${String(e.message ?? e).slice(0, 200)}`);
  } finally {
    if (page) await page.close();
  }
}
writeFileSync(`${OUT}/dom-audit.json`, JSON.stringify(results, null, 1));
await browser.close(); server.close();
const failed = [...problems, ...screenFailures()];
console.log(`\nshots and dom-audit.json in ${OUT}`);
if (failed.length) {
  console.error(`visual audit: ${failed.length} problem(s)`);
  for (const f of failed) console.error(`  - ${f}`);
  process.exit(1);
}
