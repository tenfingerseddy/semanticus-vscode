// fix-hero27: the REAL Studio on the notebook with folded rows that kept big numbers. Proves, at 1090 and 1440, dark and light:
//   P2  a folded figure shows whole on one line ("$39,137,964.25", "Total Cost 2025: 29,871,303.48")
//   P1  a folded words row that kept its number opens on ITS OWN DAX in the DAX tab, after the assistant ran a query too, and Run runs it
//   P3  "Asked by your assistant" follows the open row's origin, not the last ask
//   OUTDIR=<dir> node tools/uishot/fix-hero27-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/fh27-shots';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `fh27-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = []; const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();

const DAX_2025 = 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[Year] = 2025))';
const DAX_2024 = 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[Year] = 2024))';
const DAX_COST = 'EVALUATE ROW("Total Cost 2025", CALCULATE([Total Cost], \'Date\'[Year] = 2025))';
const DAX_UNITS = 'EVALUATE ROW("Units 2024", CALCULATE([Units], \'Date\'[Year] = 2024))';
const DAX_JUNE = 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[MonthName] = "June", \'Date\'[Year] = 2025))';
const ROWS = {
  a: { kind: 'words', question: 'net sales in 2025', reading: 'Net Sales, Year = 2025', readBy: 'jev', dax: DAX_2025, headline: '$39,137,964.25', rowCount: 1, tookMs: 71, origin: 'human', parts: { measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2025'] }] } },
  b: { kind: 'words', question: 'net sales in 2024', reading: 'Net Sales, Year = 2024', readBy: 'jev', dax: DAX_2024, headline: '$36,111,492.86', rowCount: 1, tookMs: 64, origin: 'human', parts: { measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2024'] }] } },
  c: { kind: 'dax', question: null, dax: DAX_COST, headline: 'Total Cost 2025: 29,871,303.48', rowCount: 1, tookMs: 283, origin: 'human' },
  d: { kind: 'dax', question: null, dax: DAX_UNITS, headline: '291,894', rowCount: 1, tookMs: 40, origin: 'agent' },
  e: { kind: 'words', question: 'net sales in june 2025', reading: 'Net Sales, June 2025', readBy: 'jev', dax: DAX_JUNE, headline: '$3,028,989.86', rowCount: 1, tookMs: 50, origin: 'agent', parts: { measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[MonthName]", op: 'is', values: ['June'] }] } },
};
const ids = {};

async function seed(page) {
  await page.evaluate((rows, day) => {
    const m = window.__askDataMock;
    const out = {};
    let n = 0;
    for (const k of ['e', 'd', 'c', 'b', 'a']) {   // oldest first: the list is newest first
      const id = m.recordRun([rows[k].dax + ' /*' + k + '*/', 1, 1, null, null]);
      const rec = m.notebook('list').questions.find((x) => x.id === id);
      Object.keys(rec).forEach((x) => delete rec[x]);
      Object.assign(rec, JSON.parse(JSON.stringify(rows[k])), { id, askedAt: new Date(Date.now() - (5 - n++) * 60000).toISOString(), outcome: 'answered', inNotebook: true, pinned: false });
      out[k] = id;
    }
    return out;
  }, ROWS).then((o) => Object.assign(ids, o));
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(800);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2200);
}

async function open(size, theme) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro`, { waitUntil: 'networkidle0', timeout: 60000 });
  await applyTheme(page, theme);
  await wait(2500);
  await seed(page);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(800);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2200);
  return { page, errors };
}
const msgs = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).map((x) => x.params ?? x.args ?? []), m);
const openRow = async (page, id) => { const sel = `[data-testid="notebook-row"][data-id="${id}"] .qn-line-text`; await page.waitForSelector(sel, { timeout: 8000 }); await page.click(sel); await wait(1800); };
const daxText = async (page) => {
  if (await page.$(t('view-dax'))) { await page.click(t('view-dax')); await wait(700); }
  return sq(await page.evaluate(() => { const c = document.querySelector('[data-testid="dax-card"]'); return c ? (c.querySelector('.cm-content')?.textContent || c.querySelector('pre')?.textContent || '') : ''; }));
};
const assistantRun = (page, dax, rows) => page.evaluate((dax, rows) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
  seq: 910 + Math.floor(Math.random() * 80), kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true, rowCount: rows.length, elapsedMs: 310, query: dax,
  result: { columns: [{ name: '[Units]', type: 'Whole' }], rows, rowCount: rows.length, truncated: false, elapsedMs: 310, query: dax } } } })), dax, rows);
// Every folded figure on one line and whole: the number's own box is one line high and its text is not cut.
const figures = (page) => page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => {
  const n = r.querySelector('.qn-line-n'); const v = r.querySelector('.qn-line-val'); const l = r.querySelector('.qn-line-lab');
  if (!n || !v) return { id: r.getAttribute('data-id'), none: true };
  const vr = v.getBoundingClientRect(); const nr = n.getBoundingClientRect();
  const lh = parseFloat(getComputedStyle(v).lineHeight) || vr.height;
  return { id: r.getAttribute('data-id'), val: v.textContent, lab: l?.textContent || '', oneLine: v.getClientRects().length === 1 && vr.height <= lh * 1.3, whole: vr.right <= nr.right + 1 && vr.left >= nr.left - 1, labCut: l ? l.scrollWidth > l.clientWidth : false, labW: l ? Math.round(l.getBoundingClientRect().width) : 0 };
}));

async function scene(size, theme) {
  const tag = `${size[0]}-${theme}`;
  const { page, errors } = await open(size, theme);
  // P2
  await page.screenshot({ path: join(OUT, `p2-folded-${tag}.png`) });
  const f = await figures(page);
  for (const fig of f.filter((x) => !x.none)) check(fig.oneLine && fig.whole, `${tag} P2: "${fig.lab} ${fig.val}" shows whole on one line (label ${fig.labW}px${fig.labCut ? ', shortened' : ''})`);
  const a = f.find((x) => x.id === ids.a); const c = f.find((x) => x.id === ids.c);
  check(a && a.val === '$39,137,964.25', `${tag} P2: the 2025 figure reads $39,137,964.25 (${a?.val})`);
  check(c && c.val === '29,871,303.48' && /Total Cost 2025/.test(c.lab + '') , `${tag} P2: the hand query keeps its number and a label piece ("${c?.lab}" ${c?.val})`);
  // P1: assistant ran a query (Units), then the person opens a words row that kept its number.
  await assistantRun(page, DAX_UNITS, [[291894]]); await wait(1200);
  await openRow(page, ids.a);
  const runs0 = (await msgs(page, 'runDax')).length;
  const askParams0 = (await msgs(page, 'askData')).length;
  const dax = await daxText(page);
  check(dax.includes(sq(DAX_2025).slice(0, 40)) && !dax.includes('Units'), `${tag} P1: the 2025 row's DAX tab shows its own DAX after the assistant's run (${dax.slice(0, 80)})`);
  await page.screenshot({ path: join(OUT, `p1-dax-2025-${tag}.png`) });
  // another person's row next: its own DAX, never row a's
  await page.click(t('view-answer')).catch(() => {}); await wait(300);
  await openRow(page, ids.b);
  const dax2 = await daxText(page);
  check(dax2.includes(sq(DAX_2024).slice(0, 40)) && !dax2.includes('2025'), `${tag} P1: the 2024 row's DAX tab shows 2024's DAX (${dax2.slice(0, 80)})`);
  check(!/replaced the query you wrote/.test(await page.evaluate(() => document.body.innerText)), `${tag} P1: no "question replaced the query you wrote" note`);
  check(!/You changed the query by hand/.test(await page.evaluate(() => document.body.innerText)), `${tag} P1: no "changed the query by hand" note`);
  // Run from the DAX tab runs that row's DAX
  if (await page.$(t('dax-run'))) { await page.click(t('dax-run')); await wait(2000); }
  const runs = (await msgs(page, 'runDax')).slice(runs0);
  check(runs.length >= 1 && sq(runs[runs.length - 1][0]).includes(sq(DAX_2024).slice(0, 60)), `${tag} P1: Run in the DAX tab ran the 2024 row's DAX (${runs.map((r) => sq(r[0]).slice(0, 50)).join(' | ')})`);
  check((await msgs(page, 'askData')).length === askParams0, `${tag} P1: opening and running the rows asked nothing in words`);
  // after a full restart: reload the page, the rows come back; open without any run first
  await page.reload({ waitUntil: 'networkidle0' }); await applyTheme(page, theme); await wait(2500); await seed(page);
  // P3: the assistant asks (ask_data), then the person opens their own row and presses Run: the label is the open row's, never the last ask's
  const ag = await page.evaluate(() => window.__askDataMock.agentAsk({ measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[MonthName]", op: 'is', values: ['June'] }] }));
  await page.evaluate((a) => { const send = (d) => window.dispatchEvent(new MessageEvent('message', { data: d }));
    send({ type: 'activity', payload: { seq: 931, kind: 'ask_data', origin: 'agent', label: 'Asked', ok: true, result: a } });
    send({ type: 'studioAnswer', payload: { id: 'sa-' + Math.random().toString(16).slice(2, 14), sessionId: 'uishot', at: new Date().toISOString(), question: 'net sales in june 2025', answer: a } }); }, ag);
  await wait(1500);
  await openRow(page, ids.a);
  await page.evaluate(() => { const b = [...document.querySelectorAll('[data-testid="row-body"] button, [data-testid="answer"] button')].find((x) => (x.textContent || '').trim() === 'Run'); if (b) b.click(); });
  await wait(2500);
  const src0 = await page.evaluate(() => (document.querySelector('[data-testid="query-by-assistant"]')?.textContent || '').trim());
  note(`${tag} P3: person's row after Run (assistant asked last): label "${src0}"`);
  check(src0 === '', `${tag} P3: Run on the person's row after the assistant's ask shows no "Asked by your assistant" ("${src0}")`);
  await page.screenshot({ path: join(OUT, `p3-person-run-${tag}.png`) });
  // P3: the person's row after the assistant's row was open
  await openRow(page, ids.e);
  await page.click(t('view-answer')).catch(() => {}); await wait(500);
  const src1 = await page.evaluate(() => (document.querySelector('[data-testid="query-by-assistant"]')?.textContent || '').trim());
  note(`${tag} P3: the assistant's ask_data row open: label "${src1}"`);
  await page.screenshot({ path: join(OUT, `p3-agent-row-${tag}.png`) });
  await openRow(page, ids.a);
  await page.click(t('view-answer')).catch(() => {}); await wait(500);
  const src2 = await page.evaluate(() => (document.querySelector('[data-testid="query-by-assistant"]')?.textContent || '').trim());
  check(src2 === '', `${tag} P3: the person's own row never says "Asked by your assistant" ("${src2}")`);
  await page.screenshot({ path: join(OUT, `p3-person-row-${tag}.png`) });
  check(errors.length === 0, `${tag}: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
}
try {
  for (const size of [[1090, 900], [1440, 1000]]) for (const theme of ['dark', 'light']) await scene(size, theme);
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
