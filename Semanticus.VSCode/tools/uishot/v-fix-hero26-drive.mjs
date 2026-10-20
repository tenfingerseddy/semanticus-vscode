// Verifier v-fix-hero26 (N1): the REAL Studio fed the engine shapes the pipe gives. WIRE is the JSON the verifier's C# test
// (VFixHero26PipeTests) captured over a real RpcServer: the notebook rows a Human connection reads (before and after the assistant's
// run_dax through the MCP door) and the model/activity event the Studio hears. Each scene opens a person's row, the engine keeps the
// assistant's row (the 'after' row, verbatim) and the activity event lands; or (not-kept) the row is missing, as when history is off.
//   WIRE=<wire-n1.json> OUTDIR=<dir> node tools/uishot/v-fix-hero26-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/vfh26-shots';
const WIRE = JSON.parse(readFileSync(process.env.WIRE, 'utf8'));
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh26-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();

const UNITS = WIRE.units;
const PERSON_DAX = WIRE.personDax;
const agentRow = WIRE.after.questions.find((r) => r.origin === 'agent');
const personRow = WIRE.before.questions.find((r) => r.id === WIRE.person);
const unitsResult = WIRE.activity.result;
const personResult = { columns: [{ name: '[Net Sales]', type: 'Decimal' }], rows: [[39137964.25]], rowCount: 1, truncated: false, elapsedMs: 29, query: PERSON_DAX };

async function open() {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  // The engine's rows, verbatim from the wire (the person's hand query with its kept number). The harness's own ask store holds them.
  await page.evaluate((row, unitsResult, personResult, units, personDax) => {
    const m = window.__askDataMock;
    const id = m.recordRun([row.dax, row.rowCount, row.tookMs, row.headline, null]);
    const rec = m.notebook('list').questions.find((x) => x.id === id);
    Object.keys(rec).forEach((k) => delete rec[k]); Object.assign(rec, JSON.parse(JSON.stringify(row)));
    const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();
    const inner = window.__uishotHarness.response('runDax', []);
    window.__uishotHarness.setResponse('runDax', (p) => sq(p[0]) === sq(units) ? { ...unitsResult } : sq(p[0]) === sq(personDax) ? { ...personResult } : inner);
  }, personRow, unitsResult, personResult, UNITS, PERSON_DAX);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(800);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2000);
  return { page, errors };
}
const msgs = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).map((x) => x.params ?? x.args ?? []), m);
const send = (page, data) => page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const rows = (page) => page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => ({
  id: r.getAttribute('data-id'), open: r.getAttribute('data-open') === 'yes', text: (r.querySelector('.qn-line-text')?.textContent || '').trim(),
  n: (r.querySelector('.qn-line-n')?.textContent || '').trim(), all: (r.textContent || '').replace(/\s+/g, ' ').trim() })));
const store = (page) => page.evaluate(() => window.__askDataMock.notebook('list').questions.map((r) => ({ id: r.id, origin: r.origin, kind: r.kind, dax: r.dax, headline: r.headline, question: r.question })));
const openRow = async (page, id) => { const sel = `[data-testid="notebook-row"][data-id="${id}"] .qn-line-text`; await page.waitForSelector(sel, { timeout: 8000 }); await page.click(sel); await wait(2200); };
const ask = async (page, words) => {
  const sel = '[data-testid="queries-ask"] input';
  await page.waitForSelector(sel, { timeout: 8000 });
  await page.focus(sel); await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control'); await page.keyboard.press('Backspace');
  await page.keyboard.type(words); await page.keyboard.press('Enter'); await wait(2500);
};
const typeOnly = async (page, words) => {
  const sel = '[data-testid="queries-ask"] input';
  await page.focus(sel); await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control'); await page.keyboard.press('Backspace');
  await page.keyboard.type(words); await wait(300);
};
// The engine keeps the assistant's row before the Studio hears of the run (McpTools.RunDax): the 'after' row from the wire, verbatim.
const engineKeeps = (page) => page.evaluate((row) => {
  const m = window.__askDataMock;
  const id = m.recordRun([row.dax, row.rowCount, row.tookMs, null, null]);
  const rec = m.notebook('list').questions.find((x) => x.id === id);
  Object.keys(rec).forEach((k) => delete rec[k]); Object.assign(rec, JSON.parse(JSON.stringify(row)));
  return rec.id;
}, agentRow);
const fire = (page, seq) => send(page, { type: 'activity', payload: { ...WIRE.activity, seq } });
// What a person sees of the open answer: the row body, the reading line and any chips.
const answerView = (page) => page.evaluate(() => {
  const body = document.querySelector('[data-testid="row-body"]');
  const reading = document.querySelector('[data-testid="reading"]');
  return { body: (body?.textContent || '').replace(/\s+/g, ' ').trim(), reading: (reading?.textContent || '').replace(/\s+/g, ' ').trim(),
    parts: [...(reading?.querySelectorAll('.qv-part') || [])].map((p) => p.textContent.trim()), chips: [...document.querySelectorAll('[data-testid="reading"] [data-chip]')].map((b) => b.textContent.trim()),
    figure: (document.querySelector('[data-testid="figure"] .qn-figure-n')?.textContent || '').trim(), source: (document.querySelector('[data-testid="answer-source"]')?.textContent || '').trim(),
    headline: (document.querySelector('[data-testid="headline"]')?.textContent || '').trim() };
});

async function scene(name, { setup, kept, words }) {
  const { page, errors } = await open();
  const forbidden = await setup(page);
  const before = await store(page);
  const openBefore = (await rows(page)).find((r) => r.open);
  note(`${name}: before the assistant: open ${openBefore ? `${openBefore.id} "${openBefore.text}" ${openBefore.n}` : 'none'}; rows ${(await rows(page)).map((r) => `${r.id}=${r.n}`).join(' ')}`);
  await page.screenshot({ path: join(OUT, `${name}-0-before.png`) });
  const n0 = (await msgs(page, 'noteQueryRow')).length;
  const agentId = kept ? await engineKeeps(page) : null;
  await fire(page, 900 + Math.floor(Math.random() * 90));
  await wait(2500);
  const v = await answerView(page);
  const r1 = await rows(page);
  const openNow = r1.find((r) => r.open);
  note(`${name}: after: open ${openNow ? `${openNow.id} "${openNow.text}"` : 'none'}; figure "${v.figure}"; source "${v.source}"; reading "${v.reading}"; parts ${JSON.stringify(v.parts)} chips ${JSON.stringify(v.chips)}`);
  await page.screenshot({ path: join(OUT, `${name}-1-after.png`) });
  if (kept) check(openNow?.id === agentId, `${name}: the assistant's own row (${agentId}) is the open row`);
  else check(!openNow || openNow.id === 'draft' || !before.some((b) => b.id === openNow.id), `${name}: no person's row holds the assistant's run`);
  check(/291,894/.test(v.figure) || /291894/.test(v.figure), `${name}: the assistant's number is shown ("${v.figure}")`);
  check(v.parts.length === 0 && v.chips.length === 0, `${name}: no Read as parts and no chips under the assistant's number (${JSON.stringify(v.parts)} ${JSON.stringify(v.chips)})`);
  for (const w of forbidden) check(!v.body.toLowerCase().includes(w.toLowerCase()) && !(openNow?.all || '').toLowerCase().includes(w.toLowerCase()), `${name}: the open row and answer never say "${w}"`);
  const other = r1.filter((r) => r.id !== openNow?.id && /291/.test(r.all));
  check(other.length === 0, `${name}: 291,894 is on no other row (${other.map((r) => r.id).join(',')})`);
  const notes = (await msgs(page, 'noteQueryRow')).slice(n0);
  const onPerson = notes.filter((n) => before.some((b) => b.id === n[0]));
  check(onPerson.length === 0, `${name}: no noteQueryRow on a person's row (${JSON.stringify(notes)})`);
  const after = await store(page);
  for (const b of before) {
    const a = after.find((x) => x.id === b.id);
    check(a && a.headline === b.headline && sq(a.dax) === sq(b.dax) && a.origin === b.origin && a.question === b.question, `${name}: ${b.id} (${b.origin}) keeps its number, DAX and words ("${a?.headline}")`);
  }
  // The DAX view shows the assistant's own DAX; Run sends it.
  if (await page.$(t('view-dax'))) { await page.click(t('view-dax')); await wait(600); }
  const daxShown = sq(await page.evaluate(() => { const c = document.querySelector('[data-testid="dax-card"]'); return c ? (c.querySelector('.cm-content')?.textContent || c.textContent || '') : ''; }));
  check(daxShown.includes(sq(UNITS).slice(0, 40)), `${name}: the DAX view shows the assistant's DAX (${daxShown.slice(0, 90)})`);
  const runs0 = (await msgs(page, 'runDax')).length;
  if (await page.$(t('dax-run'))) { await page.click(t('dax-run')); await wait(2000); }
  const runs = (await msgs(page, 'runDax')).slice(runs0);
  if (runs.length) check(sq(runs[runs.length - 1][0]) === sq(UNITS), `${name}: Run sends the assistant's DAX`);
  const v2 = await answerView(page);
  await page.click(`[data-testid="view-answer"]`).catch(() => {}); await wait(800);
  const v3 = await answerView(page);
  note(`${name}: after Run: figure "${v3.figure}" parts ${JSON.stringify(v3.parts)} chips ${JSON.stringify(v3.chips)} headline "${v3.headline}"`);
  check(v3.parts.length === 0 && v3.chips.length === 0, `${name}: after Run, still no parts or chips under the assistant's number`);
  for (const w of forbidden) check(!v3.body.toLowerCase().includes(w.toLowerCase()), `${name}: after Run, the answer never says "${w}"`);
  await page.screenshot({ path: join(OUT, `${name}-2-after-run.png`) });
  const notes2 = (await msgs(page, 'noteQueryRow')).slice(n0).filter((n) => before.some((b) => b.id === n[0]) && /291/.test(JSON.stringify(n[1])));
  check(notes2.length === 0, `${name}: 291,894 is never noted on a person's row (${JSON.stringify(notes2)})`);
  // Leave and come back: every row keeps its own number; the person's DAX row opens on its own number.
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(900);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2200);
  const r4 = await rows(page);
  note(`${name}: after a reload: ${r4.map((r) => `${r.id}${r.open ? '*' : ''}="${r.text.slice(0, 40)}" ${r.n}`).join(' | ')}`);
  const p4 = r4.find((r) => r.id === personRow.id);
  check(p4 && p4.n.includes('39,137,964.25'), `${name}: after a reload the person's DAX row shows its own number (${p4?.n})`);
  const bad4 = r4.filter((r) => r.id !== agentId && /291/.test(r.all));
  check(bad4.length === 0, `${name}: after a reload 291,894 is on no person's row`);
  await openRow(page, personRow.id);
  const v5 = await answerView(page);
  check(/39,137,964\.25/.test(v5.figure), `${name}: the person's row opens on its own number ("${v5.figure}")`);
  if (kept) {
    await openRow(page, agentId);
    const v6 = await answerView(page);
    note(`${name}: the assistant's row opened again: figure "${v6.figure}" parts ${JSON.stringify(v6.parts)} source "${v6.source}"`);
    check(!v6.figure || /291,?894/.test(v6.figure), `${name}: the assistant's row opened again shows only its own number ("${v6.figure}")`);
    check(v6.parts.length === 0 && v6.chips.length === 0, `${name}: the assistant's row opened again has no parts or chips`);
  }
  check(errors.length === 0, `${name}: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.screenshot({ path: join(OUT, `${name}-3-end.png`) });
  await page.close();
}

// The person's asked question open, with its chips, and new words typed in the ask box (the hero's "net sales in June 2025").
const askOpen = async (page) => { await ask(page, 'total sales by region'); await typeOnly(page, 'total sales in june 2025'); return ['total sales in june 2025', 'total sales by region', 'Region']; };
// The person's own hand query open (the hero's second case: a DAX row open).
const daxOpen = async (page) => { await openRow(page, personRow.id); await typeOnly(page, 'net sales in june 2025'); return ['net sales in june 2025', '39,137,964.25']; };
// The person building from fields (wells filled), no row open.
const buildOpen = async (page) => {
  await ask(page, 'total sales by region');
  await page.click(t('view-dax')); await wait(700);
  const b = await page.$(t('mode-build'));
  if (b) { await b.click(); await wait(1500); } else note('build: no Fields control found');
  await page.click(t('view-answer')).catch(() => {}); await wait(700);
  return ['total sales by region', 'Region'];
};

try {
  for (const kept of [true, false]) {
    await scene(`ask-open-${kept ? 'kept' : 'notkept'}`, { setup: askOpen, kept });
    await scene(`dax-open-${kept ? 'kept' : 'notkept'}`, { setup: daxOpen, kept });
    await scene(`build-${kept ? 'kept' : 'notkept'}`, { setup: buildOpen, kept });
  }
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
