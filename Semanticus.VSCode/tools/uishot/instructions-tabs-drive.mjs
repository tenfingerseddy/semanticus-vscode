// Acceptance driver for the jev/notes-fold lane: Model notes became the Notes tab of AI instructions.
//
// Kane, 23 September 2026 ("Model notes - agreed implement recommendation"). Every shot here is the REAL
// Studio driven through the mock engine, at the 1440 width the lane was asked to be read at.
//
// WHAT IS PROVEN HERE: that AI instructions really has two tabs; that each one shows its own pane and
// hides the other; that pressing Notes inside the page reaches the same place a deep link reaches; that
// the deep link `?tab=knowledge`, which is how the ask box, the Plan and every old saved page name this,
// still lands on the Notes tab; and that the notes brought all four of their sections with them and are
// still reading the engine (getPrimer), not drawing an empty shell.
//
// WHAT IS NOT: anything about the ENGINE's catalogue. The engine still lists `knowledge` as a strip tool
// and must come to flag it `isTab`; that half is another lane's, and test/ask-box.test.mjs holds it.
//
// EVERY SCENE STATES WHAT IT MUST SHOW and the run fails when one does not, so an exit of 0 is worth
// quoting. A scene with no `must` is refused before the browser starts.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');            // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'notes-fold');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
// Chromium's own scratch, on real disk. /tmp on this machine is a tmpfs other work fills, and a failed
// write there reads exactly like a component fault and is not one.
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `notes-fold-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);
const WIDTH = parseInt(process.env.UISHOT_W || '1440', 10);

const NOTES_SECTIONS = ['knowledge-insights', 'knowledge-learned', 'knowledge-recall', 'knowledge-purge'];
const missingSections = (seen) => NOTES_SECTIONS.filter((id) => !(seen.sections || []).includes(id));

const JOBS = [
  {
    slug: 'instructions-tab', q: 'tab=instructions', width: WIDTH,
    desc: 'AI instructions on its Instructions tab: the strip, the saved words, and the notes waiting behind the other tab',
    must: (s) => [
      s.tabs.join(',') !== 'Instructions,Notes' && `the strip should read Instructions, Notes; it read ${JSON.stringify(s.tabs)}`,
      s.pressed !== 'instructions' && `the Instructions tab is not the selected one, ${JSON.stringify(s.pressed)} is`,
      !s.instructionsPaneShown && 'the Instructions pane is not showing on the Instructions tab',
      s.notesPaneShown && 'the Notes pane is showing over the Instructions tab',
      !s.notesPaneMounted && 'the Notes pane was thrown away instead of hidden, so a half-written note would be lost',
      !s.calls.includes('getAiInstructions') && 'the page never read the saved instructions, so nothing here proves it opened',
      !s.foot.includes('Open the Notes tab') && `the link at the foot should send a person to the Notes tab; it read ${JSON.stringify(s.foot)}`,
    ],
  },
  {
    slug: 'notes-tab', q: 'tab=knowledge', width: WIDTH,
    desc: 'the deep link that every old link, the ask box and the Plan still use: it opens the Notes tab',
    must: (s) => [
      s.pressed !== 'notes' && `the deep link to knowledge did not open the Notes tab, ${JSON.stringify(s.pressed)} is selected`,
      !s.notesPaneShown && 'the Notes pane is not showing on the Notes tab',
      s.instructionsPaneShown && 'the Instructions pane is showing over the Notes tab',
      missingSections(s).length && `the notes lost ${missingSections(s).join(', ')} on the way into the tab`,
      !s.calls.includes('getPrimer') && 'the notes never read the engine, so this is an empty shell and not the page',
      s.editRow.join(' | ') !== 'Find and replace | Queries | AI instructions | Advanced modelling'
        && `the Edit row should read Find and replace, Queries, AI instructions, Advanced modelling; it read ${JSON.stringify(s.editRow)}`,
      s.editRowLit !== 'AI instructions' && `the Edit row should stay lit on AI instructions while its Notes tab is open; it lit ${JSON.stringify(s.editRowLit)}`,
    ],
  },
  {
    slug: 'switch-to-notes', q: 'tab=instructions', width: WIDTH, press: 'notes',
    desc: 'pressing Notes inside the page reaches the same place the deep link reaches',
    must: (s) => [
      s.pressed !== 'notes' && 'pressing Notes did not select the Notes tab',
      !s.notesPaneShown && 'pressing Notes did not show the notes',
      missingSections(s).length && `after the switch the notes are missing ${missingSections(s).join(', ')}`,
      !s.calls.includes('getPrimer') && 'the notes behind the tab never read the engine',
    ],
  },
  {
    slug: 'switch-back', q: 'tab=knowledge', width: WIDTH, press: 'instructions',
    desc: 'and back again: the notes stay mounted, so nothing half-written is thrown away',
    must: (s) => [
      s.pressed !== 'instructions' && 'pressing Instructions did not select the Instructions tab',
      !s.instructionsPaneShown && 'pressing Instructions did not show the saved words',
      !s.notesPaneMounted && 'going back to Instructions threw the notes away instead of hiding them',
      s.notesPaneShown && 'the Notes pane is still showing after switching away from it',
    ],
  },
];

const noPromise = JOBS.filter((j) => typeof j.must !== 'function').map((j) => j.slug);
if (noPromise.length) {
  console.error(`these scenes say nothing about what they must show, so they cannot prove anything: ${noPromise.join(', ')}`);
  await browser.close();
  server.close();
  process.exit(1);
}

const results = [];
for (const job of JOBS) {
  if (ONLY.length && !ONLY.includes(job.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: job.width, height: 1000, deviceScaleFactor: 2 });
  let note = '';
  const out = join(OUT, `${job.slug}-${LABEL}.png`);
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-page-tab="notes"]', { timeout: 20000 });
    await wait(2200);
    if (job.press) {
      const ok = await page.evaluate((which) => {
        const b = document.querySelector(`[data-page-tab="${which}"]`);
        if (b) b.click();
        return !!b;
      }, job.press);
      if (!ok) note += `no ${job.press} tab to press; `;
      await wait(1800);
    }
    const seen = await page.evaluate(() => {
      const shown = (el) => !!el && el.getClientRects().length > 0;
      const notes = document.querySelector('[data-testid="instructions-notes-pane"]');
      // Scoped to this page's own strip. Validation is mounted (hidden) on every tab and has a
      // data-page-tab strip of its own, so an unscoped query reads Checks and Reports instead.
      const tabs = [...document.querySelectorAll('[aria-label="AI instructions"] [data-page-tab]')];
      // Read off aria-current, which is the STATE. The wash is only how that state looks, and measuring
      // the colour would pass a row a screen reader cannot read (App's Tab carried no state until
      // 23 September 2026).
      const segments = [...document.querySelectorAll('.studio-arearow .area-tabs button')];
      const litSegment = segments.find((b) => b.getAttribute('aria-current') === 'page');
      return {
        tabs: tabs.map((b) => (b.textContent || '').trim()),
        pressed: tabs.find((b) => b.getAttribute('aria-pressed') === 'true')?.getAttribute('data-page-tab') ?? null,
        notesPaneMounted: !!notes,
        notesPaneShown: shown(notes),
        instructionsPaneShown: shown(document.querySelector('.sem-instructions-page')),
        sections: [...document.querySelectorAll('section[id^="knowledge-"]')].map((s) => s.id),
        foot: (document.querySelector('[data-testid="instructions-foot"]')?.textContent || '').trim(),
        editRow: segments.map((b) => (b.textContent || '').replace(/\u25be|\s+/g, ' ').trim()),
        editRowLit: (litSegment?.textContent || '').trim() || null,
        calls: (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean),
      };
    });
    await page.screenshot({ path: out });
    const failures = job.must(seen).filter(Boolean);
    if (errors.length) failures.push(`the page logged errors: ${errors.join(' | ')}`);
    results.push({ job: job.slug, desc: job.desc, width: job.width, seen, errors, note, failures, out });
  } catch (e) {
    results.push({ job: job.slug, desc: job.desc, errors, note, failures: ['FATAL: ' + e.message] });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-notes-fold-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results.map((r) => ({ job: r.job, out: r.out, failures: r.failures })), null, 2));

const broken = results.filter((r) => r.failures?.length);
if (broken.length) {
  console.error(`\n${broken.length} of ${results.length} scenes did not show what they promised:`);
  for (const r of broken) console.error(`  ${r.job}: ${r.failures.join(' | ')}`);
  process.exit(1);
}
console.log(`\nall ${results.length} scenes showed what they promised, at ${WIDTH} wide`);
