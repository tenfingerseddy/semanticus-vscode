// Verifier v-fix-hero25 (H1): the REAL Studio through the mock engine. A person's row is open with its own number; then the assistant
// runs a query of its own (run_dax; run_dax + show_in_studio measure focus; ask_data). Proves or breaks: the person's row keeps its own
// number and DAX (on screen, in the engine's rows, after a tab reload); the assistant's run is its own row; Run runs the shown DAX;
// noteQueryRow never writes another query's number.
// ENGINE modes: 'gate' = what the real engine does today (the gated MCP door refuses RecordQueryRunAsync, so no assistant row exists);
//               'fixed' = what the lane meant (an assistant row with that DAX exists before the event).
//   OUTDIR=<dir> node tools/uishot/v-fix-hero25-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/vfh25-shots';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh25-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);

async function open(q) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const msgs = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).map((x) => x.params ?? x.args ?? []), m);
const send = (page, data) => page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);
const rows = (page) => page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => ({
  id: r.getAttribute('data-id'), open: r.getAttribute('data-open') === 'yes', text: (r.querySelector('.qn-line-text')?.textContent || '').trim(),
  n: (r.querySelector('.qn-line-n')?.textContent || '').trim() })));
const store = (page) => page.evaluate(() => window.__askDataMock.notebook('list').questions.map((r) => ({ id: r.id, origin: r.origin, kind: r.kind, dax: r.dax, headline: r.headline, inNotebook: r.inNotebook })));
const openRow = async (page, id) => { const sel = `[data-testid="notebook-row"][data-id="${id}"] .qn-line-text`; await page.waitForSelector(sel, { timeout: 8000 }); await page.click(sel); await wait(2500); };
const daxText = (page) => page.evaluate(() => { const c = document.querySelector('[data-testid="dax-card"]'); return c ? (c.querySelector('.cm-content')?.textContent || c.textContent || '') : ''; });
const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();

const JUNE = "EVALUATE ROW(\"Net Sales June 2025\", CALCULATE([Total Sales], 'Date'[Year] = 2025, 'Date'[Month] = 6))";
const JUNE_V = 3028989.86;
const UNITS = "EVALUATE ROW(\"Units 2024\", CALCULATE([Total Sales], 'Date'[Year] = 2024))";
const UNITS_V = 291894;
const runEvent = (seq, dax, v) => ({ type: 'activity', payload: { seq, kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true, rowCount: 1, elapsedMs: 310, query: dax,
  result: { columns: [{ name: '[v]', type: 'Decimal' }], rows: [[v]], rowCount: 1, truncated: false, elapsedMs: 310, query: dax } } });
const showEvent = (id) => ({ type: 'studioShow', payload: { id, say: 'Here is the number.', how: 'Open Explore, then Query.', place: 'explore/ask', focus: 'measure:Sales/Total Sales', at: new Date().toISOString(), by: 'assistant', auto: false, sessionId: 'uishot' } });
// What the fixed engine would do before the Studio hears of the run: keep an assistant row with that DAX.
const engineKeepsAgentRow = (page, dax, v) => page.evaluate((dax, v) => {
  const id = window.__askDataMock.recordRun([dax, 1, 310, null, null]);
  const r = window.__askDataMock.notebook('list').questions.find((x) => x.id === id); r.origin = 'agent'; return id;
}, dax, v);

async function scenario(name, { engine, path }) {
  const { page, errors } = await open('tab=query&tier=pro&nb=seed');
  // The person opens "total sales" (pinned): it runs, and its number is its own.
  await openRow(page, 'dq-seed02');
  const mineBefore = (await store(page)).find((r) => r.id === 'dq-seed02');
  const figMine = await text(page, t('figure'));
  note(`${name}: person's row open, figure "${figMine}", kept "${mineBefore.headline}", dax ${sq(mineBefore.dax)}`);
  const notes0 = (await msgs(page, 'noteQueryRow')).length;
  const runs0 = (await msgs(page, 'runDax')).length;
  let agentId = null;
  const msgCount0 = await page.evaluate(() => (window.__uishotHarness?.messages || []).length);
  if (path === 'run_dax' || path === 'run_dax+show') {
    if (engine === 'fixed') agentId = await engineKeepsAgentRow(page, JUNE, JUNE_V);
    await send(page, runEvent(910, JUNE, JUNE_V)); await wait(1800);
    if (path === 'run_dax+show') { await send(page, showEvent('sn-0000000show1')); await wait(2500); }
  } else if (path === 'ask_data') {
    const a = await page.evaluate(() => window.__askDataMock.agentAsk({ measures: ['Total Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2024'] }] }));
    agentId = a.questionId;
    await send(page, { type: 'activity', payload: { seq: 911, kind: 'ask_data', origin: 'agent', label: 'Asked', ok: true, result: a } });
    await send(page, { type: 'studioAnswer', payload: { id: 'sa-00000000001', sessionId: 'uishot', at: new Date().toISOString(), question: 'total sales in 2024', answer: a } });
    await wait(400); await send(page, showEvent('sn-0000000show2')); await wait(2500);
  }
  const after = await page.evaluate((n) => (window.__uishotHarness?.messages || []).slice(n).map((m) => m.method + (m.method === 'runDax' || m.method === 'askData' ? ':' + JSON.stringify(m.params ?? m.args ?? []).slice(0, 90) : '')).filter((x) => x && !/^undefined|listPending|setStudio/.test(x)), msgCount0);
  note(`${name}: calls after the assistant's event: ${after.join(', ')}`);
  note(`${name}: answer view after the assistant: "${(await text(page, t('row-body'))).slice(0, 160)}"`);
  const r1 = await rows(page);
  const openNow = r1.find((r) => r.open);
  note(`${name}: open row after the assistant: ${openNow ? `${openNow.id} "${openNow.text}"` : 'none (draft)'}; rows ${r1.map((r) => `${r.id}=${r.n}`).join(' ')}`);
  check(!openNow || openNow.id !== 'dq-seed02', `${name}: the person's row is not the row that holds the assistant's run`);
  if (engine === 'fixed' && path !== 'ask_data') check(openNow?.id === agentId, `${name}: the assistant's run opens its own row (${agentId})`);
  if (engine === 'fixed' && path !== 'ask_data') { const fig = await text(page, t('figure')); check(/3,028,989/.test(fig), `${name}: the assistant's row shows the assistant's number ("${fig}")`); }
  if (engine === 'gate' && path !== 'ask_data') {
    const s = await store(page);
    check(s.some((r) => r.origin === 'agent' && sq(r.dax) === sq(JUNE)), `${name}: the assistant's run is kept as a row of its own (the engine kept ${s.filter((r) => r.origin === 'agent').length} assistant rows with that DAX)`);
  }
  await page.screenshot({ path: join(OUT, `${name}-1-after-assistant.png`) });
  const notes1 = (await msgs(page, 'noteQueryRow')).slice(notes0);
  const wrong = notes1.filter((n) => n[0] === 'dq-seed02');
  check(wrong.length === 0, `${name}: no noteQueryRow on the person's row after the assistant's run (${JSON.stringify(notes1)})`);
  const mineAfter = (await store(page)).find((r) => r.id === 'dq-seed02');
  check(mineAfter.headline === mineBefore.headline && sq(mineAfter.dax) === sq(mineBefore.dax), `${name}: the person's row keeps its number and DAX in the engine ("${mineAfter.headline}")`);
  // Run on the open row: the shown DAX
  if (await has(page, t('view-dax'))) { await page.click(t('view-dax')); await wait(500); }
  const shown = sq(await daxText(page));
  note(`${name}: DAX view shows ${shown.slice(0, 140)}`);
  if (await has(page, t('dax-run'))) { await page.click(t('dax-run')); await wait(1800); }
  const runs = (await msgs(page, 'runDax')).slice(runs0);
  const last = runs[runs.length - 1];
  if (last) check(shown.includes(sq(last[0]).slice(0, 60)) || sq(last[0]).includes(shown.slice(0, 60)), `${name}: Run sends the DAX the DAX view shows (sent ${sq(last[0]).slice(0, 120)})`);
  else note(`${name}: Run made no runDax call`);
  await page.screenshot({ path: join(OUT, `${name}-2-after-run.png`) });
  const notes2 = (await msgs(page, 'noteQueryRow')).slice(notes0).filter((n) => n[0] === 'dq-seed02');
  check(notes2.length === 0, `${name}: Run on the open row writes nothing to the person's row (${JSON.stringify(notes2)})`);
  // The person goes back to their row; then a reload (leave the tab, come back)
  await openRow(page, 'dq-seed02');
  const back = await text(page, t('figure'));
  check(back === figMine, `${name}: the person's row shows its own number again ("${back}" vs "${figMine}")`);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(1200);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2000);
  const rr = (await rows(page)).find((r) => r.id === 'dq-seed02');
  const s2 = (await store(page)).find((r) => r.id === 'dq-seed02');
  check(s2.headline === mineBefore.headline && (!rr || rr.n === mineBefore.headline || rr.n === ''), `${name}: after the reload the person's row still holds its own number (row "${rr?.n}", kept "${s2.headline}")`);
  const allNotes = (await msgs(page, 'noteQueryRow'));
  const bad = allNotes.filter((n) => n[0] === 'dq-seed02' && n[1]?.headline && n[1].headline !== mineBefore.headline);
  check(bad.length === 0, `${name}: no noteQueryRow ever put another number on the person's row (${JSON.stringify(bad)})`);
  if (engine === 'fixed' && path !== 'ask_data') {
    const kept = (await store(page)).find((r) => r.id === agentId);
    const row = (await rows(page)).find((r) => r.id === agentId);
    note(`${name}: noteQueryRow on the assistant's row: ${JSON.stringify(allNotes.filter((n) => n[0] === agentId))}`);
    await openRow(page, agentId);
    note(`${name}: the assistant's row opened again after the reload shows "${await text(page, t('figure'))}"; body "${(await text(page, t('row-body'))).slice(0, 140)}"`);
    await page.screenshot({ path: join(OUT, `${name}-4-assistant-row-reopened.png`) });
    check(/3,028,989/.test(kept?.headline ?? '') , `${name}: the assistant's row keeps its own number in the engine (kept "${kept?.headline}", folded row "${row?.n}")`);
  }
  check(errors.length === 0, `${name}: no page errors (${errors.join(' | ').slice(0, 160)})`);
  await page.screenshot({ path: join(OUT, `${name}-3-reloaded.png`) });
  await page.close();
}

// The hero's second case: the assistant's check query lands while a DAX row (by region) is open.
async function regionCase(engine) {
  const name = `units-on-dax-row-${engine}`;
  const { page, errors } = await open('tab=query&tier=pro&nb=seed');
  await openRow(page, 'dq-seed05');
  const before = (await store(page)).find((r) => r.id === 'dq-seed05');
  if (engine === 'fixed') await engineKeepsAgentRow(page, UNITS, UNITS_V);
  await send(page, runEvent(920, UNITS, UNITS_V)); await wait(2500);
  const after = (await store(page)).find((r) => r.id === 'dq-seed05');
  check(after.headline === before.headline, `${name}: the open DAX row keeps its number ("${after.headline}" vs "${before.headline}")`);
  check(!(await msgs(page, 'noteQueryRow')).some((n) => n[0] === 'dq-seed05' && /291/.test(JSON.stringify(n[1]))), `${name}: 291,894 is never noted on it`);
  check(errors.length === 0, `${name}: no page errors`);
  await page.close();
}

try {
  for (const engine of ['gate', 'fixed']) {
    await scenario(`run_dax-${engine}`, { engine, path: 'run_dax' });
    await scenario(`run_dax+show-${engine}`, { engine, path: 'run_dax+show' });
    await regionCase(engine);
  }
  await scenario('ask_data', { engine: 'fixed', path: 'ask_data' });
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
