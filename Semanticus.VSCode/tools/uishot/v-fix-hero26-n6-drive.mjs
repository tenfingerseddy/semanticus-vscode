// Verifier v-fix-hero26 (N6, N8, N10) on screen in the real Query page: a folded row with a kept number opens without asking again and
// Run asks again (N10); a row timed with a repeat time only says so on its Speed tab (N6); a hand query run again keeps its "$" and a
// two-column hand query names its number without brackets (N8).
//   OUTDIR=<dir> node tools/uishot/v-fix-hero26-n6-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || '/tmp/vfh26-n6';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh26n6-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
const count = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).length, m);
const msgs = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).map((x) => x.params ?? x.args ?? []), m);
const openRow = async (page, id) => { const sel = `[data-testid="notebook-row"][data-id="${id}"] .qn-line-text`; await page.waitForSelector(sel, { timeout: 8000 }); await page.click(sel); await wait(2200); };
const reload = async (page) => {
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(800);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2000);
};
const fig = (page) => page.evaluate(() => ({ big: (document.querySelector('[data-testid="figure"] .qn-figure-n')?.textContent || '').trim(), name: (document.querySelector('[data-testid="figure-name"]')?.textContent || '').trim() }));
const SAID = 'EVALUATE ROW("Net Sales June 2025", 3028989.86)';
const PAIR = 'EVALUATE ROW("Year", 2025, "Total Cost 2025", 29871303.48)';
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro&nb=seed`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  const ids = await page.evaluate((said, pair) => {
    const m = window.__askDataMock;
    const a = m.recordRun([said, 1, 40, '$3,028,989.86', null]);
    const b = m.recordRun([pair, 1, 40, null, null]);
    const seed05 = m.notebook('list').questions.find((x) => x.id === 'dq-seed05'); seed05.coldMs = null; seed05.warmMs = 65;
    const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();
    const inner = window.__uishotHarness.response('runDax', []);
    window.__runDaxBefore = (p) => (sq(p[0]) === sq(said) ? { columns: [{ name: '[Net Sales June 2025]', type: 'Decimal' }], rows: [[3028989.86]], rowCount: 1, truncated: false, elapsedMs: 30, query: p[0] } : sq(p[0]) === sq(pair) ? { columns: [{ name: '[Year]', type: 'Int64' }, { name: '[Total Cost 2025]', type: 'Decimal' }], rows: [[2025, 29871303.48]], rowCount: 1, truncated: false, elapsedMs: 30, query: p[0] } : inner);
    window.__uishotHarness.setResponse('runDax', (p) => sq(p[0]) === sq(said) ? { columns: [{ name: '[Net Sales June 2025]', type: 'Decimal' }], rows: [[3028989.86]], rowCount: 1, truncated: false, elapsedMs: 30, query: p[0] }
      : sq(p[0]) === sq(pair) ? { columns: [{ name: '[Year]', type: 'Int64' }, { name: '[Total Cost 2025]', type: 'Decimal' }], rows: [[2025, 29871303.48]], rowCount: 1, truncated: false, elapsedMs: 30, query: p[0] } : inner);
    return { said: a, pair: b };
  }, SAID, PAIR);
  await reload(page);
  // N10
  const asks0 = await count(page, 'askData'), runs0 = await count(page, 'runDax');
  await openRow(page, 'dq-seed02');
  const f10 = await fig(page);
  const asks1 = await count(page, 'askData'), runs1 = await count(page, 'runDax');
  note(`N10: opened folded "total sales" (kept $2.65M): figure "${f10.big}", asks +${asks1 - asks0}, runs +${runs1 - runs0}`);
  check(asks1 - asks0 === 0 && runs1 - runs0 === 0, 'N10: opening a folded row with a kept number asks nothing');
  check(/2\.65M/.test(f10.big) || f10.big === '', `N10: it shows its kept number ("${f10.big}")`);
  await page.screenshot({ path: join(OUT, 'n10-open.png') });
  const runBtn = await page.$('[data-testid="row-run"], [data-testid="qn-run"], button[aria-label="Run"]');
  if (runBtn) { await runBtn.click(); await wait(2200); }
  else { await page.click(t('view-dax')); await wait(500); await page.click(t('dax-run')); await wait(2200); }
  const asks2 = await count(page, 'askData'), runs2 = await count(page, 'runDax');
  note(`N10: after Run: asks +${asks2 - asks1}, runs +${runs2 - runs1} (button ${runBtn ? 'row Run' : 'DAX Run'})`);
  check(asks2 - asks1 + runs2 - runs1 >= 1, 'N10: Run asks again');
  // N6
  await openRow(page, 'dq-seed05');
  await page.click(t('view-speed')); await wait(800);
  const speed = await page.evaluate(() => (document.querySelector('[data-testid="row-timed"]')?.textContent || document.querySelector('[data-testid="queries-speed-none"]')?.textContent || '').trim());
  note(`N6: Speed tab of a row with a repeat time only: "${speed}"`);
  check(/repeat runs 65 ms/.test(speed) && !/Not measured yet/.test(speed), 'N6: a repeat time alone is shown, not "Not measured yet"');
  await page.screenshot({ path: join(OUT, 'n6-speed.png') });
  // N8 "$" kept on a run again
  await openRow(page, ids.said);
  await page.click(t('view-dax')); await wait(500);
  const notes0 = (await msgs(page, 'noteQueryRow')).length;
  await page.click(t('dax-run')); await wait(2200);
  await page.click(t('view-answer')).catch(() => {}); await wait(600);
  const f8 = await fig(page);
  const n8 = (await msgs(page, 'noteQueryRow')).slice(notes0).filter((n) => n[0] === ids.said);
  const line8 = await page.evaluate((id) => (document.querySelector(`[data-testid="notebook-row"][data-id="${id}"] .qn-line-n`)?.textContent || '').trim(), ids.said);
  note(`N8: run again: figure "${f8.big}", row line "${line8}", notes ${JSON.stringify(n8)}`);
  note(`N8: calls in order after Run: ${JSON.stringify(await page.evaluate((n) => (window.__uishotHarness?.messages || []).slice(-40).filter((m) => /recordQueryRun|noteQueryRow|runDax|queryNotebook/.test(m.method || '')).map((m) => m.method + (m.method === 'recordQueryRun' ? JSON.stringify(m.params) : m.method === 'noteQueryRow' ? JSON.stringify(m.params) : '')), 0))}`);
  check(f8.big === '$3,028,989.86', `N8: the open figure keeps "$" ("${f8.big}")`);
  check(n8.every((n) => !n[1]?.headline || n[1].headline.startsWith('$')), 'N8: the kept headline keeps "$"');
  await page.screenshot({ path: join(OUT, 'n8-dollar.png') });
  // N8 attack: the "$" row's DAX edited by hand to a COUNT (the same row is replaced): the count must not be shown or kept as dollars.
  const COUNT = 'EVALUATE ROW("Orders 2025", 13301)';
  await page.evaluate((count) => {
    const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();
    const prev = window.__uishotHarness.response('runDax', []);
    const inner = typeof prev === 'function' ? prev : null;
    const was = window.__lastRunDax || null;
    window.__uishotHarness.setResponse('runDax', (p) => sq(p[0]) === sq(count) ? { columns: [{ name: '[Orders 2025]', type: 'Int64' }], rows: [[13301]], rowCount: 1, truncated: false, elapsedMs: 20, query: p[0] } : window.__runDaxBefore(p));
  }, COUNT).catch(() => {});
  if (!(await page.evaluate((id) => document.querySelector(`[data-testid="notebook-row"][data-id="${id}"]`)?.getAttribute('data-open') === 'yes', ids.said))) await openRow(page, ids.said);
  await page.click(t('view-dax')); await wait(500);
  await page.click('[data-testid="dax-card"] .cm-content'); await wait(200);
  await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control'); await page.keyboard.press('Backspace');
  await page.keyboard.type(COUNT); await wait(300);
  const notesC0 = (await msgs(page, 'noteQueryRow')).length;
  await page.click(t('dax-run')); await wait(2500);
  await page.click(t('view-answer')).catch(() => {}); await wait(600);
  const fc = await fig(page);
  const nc = (await msgs(page, 'noteQueryRow')).slice(notesC0);
  const sentC = (await msgs(page, 'runDax')).slice(-1)[0];
  const rowsC = await page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => (r.querySelector('.qn-line-text')?.textContent || '').trim().slice(0, 40) + ' = ' + (r.querySelector('.qn-line-n')?.textContent || '').trim()));
  note(`N8 attack: edited to a count: sent ${JSON.stringify(sentC?.[0])}; figure "${fc.big}"; notes ${JSON.stringify(nc)}; rows ${rowsC.join(' | ')}`);
  check(!/\$/.test(fc.big), `N8 attack: a count run in a row that said "$" is not shown as dollars ("${fc.big}")`);
  check(nc.every((n) => !/\$/.test(n[1]?.headline || '')), `N8 attack: the count is not kept as dollars (${JSON.stringify(nc)})`);
  await page.screenshot({ path: join(OUT, 'n8-edited-count.png') });
  // N8 a two-column hand query names its number without brackets
  await openRow(page, ids.pair);
  await page.click(t('view-dax')); await wait(500);
  await page.click(t('dax-run')); await wait(2200);
  await page.click(t('view-answer')).catch(() => {}); await wait(600);
  const fp = await fig(page);
  const linep = await page.evaluate((id) => (document.querySelector(`[data-testid="notebook-row"][data-id="${id}"] .qn-line-n`)?.textContent || '').trim(), ids.pair);
  note(`N8: two-column hand query: figure "${fp.big}" named "${fp.name}"; row line "${linep}"`);
  check(fp.name === 'Total Cost 2025', `N8: the figure is named without brackets ("${fp.name}")`);
  await page.screenshot({ path: join(OUT, 'n8-name.png') });
  check(errors.length === 0, `no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
