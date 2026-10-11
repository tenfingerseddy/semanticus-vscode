// K16 (Kane, 8 Oct): the Ask your data page answering "sales in 2025", "sales by year" and "sales by brand", BEFORE and
// AFTER the fix. The REAL Studio page renders the REAL engine's answers: each answer was recorded from the engine with the
// real Jev reading the words (AskDataTests.K16_Live_everyday_questions_are_answered_by_the_real_Jev, SEMANTICUS_K16_DUMP),
// and the harness's mock hands that recorded answer back for the typed question. Only the rows are stand-ins: the test rig's
// fake data connection returns placeholder rows, so the shots show plausible Contoso-style numbers under the real columns.
//
//   ANSWERS=<dir with answers-before and answers-after> OUTDIR=<dir> node tools/uishot/k16-askdata-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'k16');
const ANSWERS = process.env.ANSWERS;
if (!ANSWERS) throw new Error('ANSWERS must name the folder holding answers-before and answers-after');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `k16-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const results = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };

/** Stand-in rows under the real columns: the first column of a split names Contoso-style members, the numbers look real. */
function rowsFor(answer) {
  const cols = answer.columns ?? [];
  if (answer.outcome !== 'answered' || cols.length === 0) return answer;
  const split = cols.length - (answer.parts?.measures?.length ?? 1);
  const name = (cols[0]?.name ?? '').toLowerCase();
  const keys = split === 0 ? [null] : name.includes('year') ? [2023, 2024, 2025] : name.includes('brand') ? ['Contoso', 'Fabrikam', 'Litware'] : ['A', 'B', 'C'];
  const base = [9842310.25, 11207654.8, 12634210.5];
  const rows = keys.map((k, i) => (k === null ? [] : [k]).concat(cols.slice(split).map((_, j) => Math.round((split === 0 ? base[2] : base[i]) / (j + 1) * 100) / 100)));
  return { ...answer, rows, rowCount: rows.length, checks: (answer.checks ?? []).map((c) => (c.name === 'rows' ? { ...c, line: rows.length === 1 ? '1 row.' : `${rows.length} rows.` } : c)) };
}

const QUESTIONS = ['sales in 2025', 'sales by year', 'sales by brand'];
try {
  // SIDES=before runs against the before build of the page (media/studio built from the unfixed source), SIDES=after the fixed one.
  for (const side of (process.env.SIDES || 'before,after').split(',')) {
    for (const q of QUESTIONS) {
      const file = join(ANSWERS, `answers-${side}`, `${q.replace(/ /g, '-')}.json`);
      if (!expect(existsSync(file), `${side}: no recorded answer for "${q}"`)) continue;
      const answer = rowsFor(JSON.parse(readFileSync(file, 'utf8')).answer);
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', (e) => errors.push(e.message));
      await page.setViewport({ width: 1280, height: 720, deviceScaleFactor: 1 });
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&tier=pro`, { waitUntil: 'networkidle0', timeout: 45000 });
      await wait(2500);
      await page.evaluate((a) => { const m = window.__askDataMock; const own = m.answer.bind(m); m.answer = (ask) => (ask.question ? JSON.parse(JSON.stringify(a)) : own(ask)); }, answer);
      await page.focus('input[aria-label="Ask in your own words"]');
      await page.keyboard.type(q);
      await page.keyboard.press('Enter');
      await wait(1200);
      await applyTheme(page, 'dark');
      await wait(300);
      const shot = join(OUT, `${side}-${q.replace(/ /g, '-')}.png`);
      await page.screenshot({ path: shot });
      const outcome = await page.evaluate(() => document.querySelector('[data-testid="askdata-answer"]')?.getAttribute('data-outcome') ?? '');
      const assumption = await page.evaluate(() => (document.querySelector('[data-testid="askdata-assumption"]')?.textContent ?? '').replace(/\s+/g, ' ').trim());
      results.push({ side, question: q, outcome, assumption, statement: answer.statement ?? null, unsure: answer.unsure ?? [], shot });
      if (side === 'after') {
        expect(outcome === 'answered', `after: "${q}" was not answered (${outcome})`);
      }
      expect(errors.length === 0, `${side} "${q}": page errors ${errors.join('; ')}`);
      await page.close();
    }
  }
} finally {
  await browser.close(); server.close();
}
writeFileSync(join(OUT, `results-${(process.env.SIDES || 'all').replace(',', '-')}.json`), JSON.stringify({ results, failures }, null, 2));
console.log(`${results.length} shots in ${OUT}`);
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('k16-askdata-drive: every scene held');
