// Verifier v-fix-hero27: the REAL Studio webview build, fed the notebook rows exactly as the real engine wrote them over the pipe
// (VFixHero27PipeTests writes them to wire-rows.json). The harness's own recordQueryRun mock files a run differently from the engine,
// so this drive swaps in the engine's rule (LocalEngine.Notebook.cs RecordQueryRunAsync, transcribed below); the engine rule itself is
// proven over the pipe by VFixHero27PipeTests.
//   P1  every kind of row (the person's words row, the person's DAX row, the assistant's ask_data row, the assistant's run_dax row),
//       folded then opened, in the same session and after a full restart (persisted webview state kept, page reloaded): the DAX tab
//       shows THAT row's DAX, Run there runs it and is filed under that row, and nothing is asked
//   P2  figures from 1 to 999,999,999,999.99, negatives, percentages, long labels: one line, whole, clear of the time column
//   4   pin, close, Time all (one at a time) still work on Query
//   WIRE=<wire-rows.json> OUTDIR=<dir> node tools/uishot/v-fix-hero27-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/vfh27-shots';
mkdirSync(OUT, { recursive: true });
const WIRE = JSON.parse(readFileSync(process.env.WIRE, 'utf8'));
const ONLY = process.env.ONLY || '';
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh27-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = []; const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);
const sq = (s) => String(s || '').replace(/\s+/g, ' ').trim();

// The fake live model: one number per query, the same values the pipe test's fake gives.
const VALUE = (q) => /Units 2024/.test(q) ? 291894 : /Total Cost/.test(q) ? 29871303.48 : /\[Units\]/.test(q) ? 13301 : 39137964.25;

// The engine's filing rule for a run the person makes (RecordQueryRunAsync, origin human), over the mock's list, newest first.
const engineRule = () => {
  const m = window.__askDataMock;
  const squash = (s) => String(s || '').split(/\s+/).filter(Boolean).join(' ');
  const keyOf = (r) => { const x = r.kind === 'dax' ? r.dax : (r.reading || r.question || r.dax); return x ? squash(x).toLowerCase() : r.id; };
  m.recordRun = function (p) {
    const list = m.notebook('list').questions;   // the same objects, newest first
    const dax = squash(p[0]) ? p[0].trim() : null; if (!dax) return null;
    const at = new Date().toISOString(); const replaces = p[4];
    const own = list.find((r) => (r.inNotebook || r.pinned) && r.kind !== 'dax' && squash(r.dax) && squash(r.dax) === squash(dax));
    if (own) { Object.assign(own, { askedAt: at, rowCount: p[1], tookMs: p[2], headline: p[3] || null, coldMs: null, warmMs: null }); return own.id; }
    const probe = { kind: 'dax', dax, id: 'x' };
    const same = list.find((r) => r.kind === 'dax' && keyOf(r) === keyOf(probe)) || (replaces && list.find((r) => r.id === replaces && r.kind === 'dax'));
    if (same) { Object.assign(same, { dax, askedAt: at, rowCount: p[1], tookMs: p[2], headline: p[3] || null, dropped: null, coldMs: null, warmMs: null, inNotebook: true }); return same.id; }
    window.__vfhAppended = (window.__vfhAppended || 0) + 1;
    return window.__vfhOrig(p);
  };
};

async function seed(page, rows) {
  await page.evaluate(engineRule);
  await page.evaluate((rows) => {
    const m = window.__askDataMock;
    // The harness's starting row is not part of this scene.
    m.notebook('remove', 'dq-agent01');
    for (const r of [...rows].reverse()) {   // oldest first: the list is newest first
      const tmp = window.__vfhOrig([r.dax + ' /*seed*/', 1, 1, null, null]);
      const rec = m.notebook('list').questions.find((x) => x.id === tmp);
      Object.keys(rec).forEach((x) => delete rec[x]);
      Object.assign(rec, JSON.parse(JSON.stringify(r)));
    }
  }, rows);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'data' } }))); await wait(800);
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'query' } }))); await wait(2200);
}

async function launch(size, theme, state) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  const st = state ? `&state=${encodeURIComponent(JSON.stringify(state))}` : '';
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tab=query&tier=pro${st}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await applyTheme(page, theme);
  await wait(2500);
  // Keep the harness's original recordRun for seeding, then answer runDax with the fake model's number for THAT query.
  await page.evaluate(() => {
    const m = window.__askDataMock; window.__vfhOrig = m.recordRun.bind(m);
  });
  await page.evaluate((src) => {
    const VALUE = new Function('q', src);
    window.__uishotHarness.setResponse('runDax', (p) => {
      const q = p[0]; const v = VALUE(q);
      return { columns: [{ name: '[v]', type: 'Decimal', format: Number.isInteger(v) ? '#,0' : '\\$#,0.00' }], rows: [[v]], rowCount: 1, truncated: false, elapsedMs: 31, query: q };
    });
  }, `return /Units 2024/.test(q) ? 291894 : /Total Cost/.test(q) ? 29871303.48 : /\\[Units\\]/.test(q) ? 13301 : 39137964.25;`);
  return { page, errors };
}
const msgs = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).map((x) => x.params ?? x.args ?? []), m);
const rowSel = (id) => `[data-testid="notebook-row"][data-id="${id}"]`;
const isOpen = (page, id) => page.evaluate((s) => document.querySelector(s)?.getAttribute('data-open') === 'yes', rowSel(id));
const openId = (page) => page.evaluate(() => document.querySelector('[data-testid="notebook-row"][data-open="yes"]')?.getAttribute('data-id') ?? null);
const rowCount = (page) => page.evaluate(() => document.querySelectorAll('[data-testid="notebook-row"]').length);
const clickRow = async (page, id) => { await page.waitForSelector(`${rowSel(id)} .qn-line-text`, { timeout: 8000 }); await page.click(`${rowSel(id)} .qn-line-text`); await wait(1500); };
async function foldAll(page) { const o = await openId(page); if (o) await clickRow(page, o); }
const daxShown = async (page) => {
  await page.click(t('view-dax')); await wait(700);
  return sq(await page.evaluate(() => { const c = document.querySelector('[data-testid="dax-card"]'); return c ? (c.querySelector('.cm-content')?.innerText || c.querySelector('textarea')?.value || c.querySelector('pre')?.textContent || '') : ''; }));
};
const bodyText = (page) => page.evaluate(() => document.body.innerText);
const KIND = (r) => `${r.origin === 'agent' ? "assistant's" : "person's"} ${r.kind === 'dax' ? (r.origin === 'agent' ? 'run_dax' : 'DAX') : (r.origin === 'agent' ? 'ask_data' : 'words')} row`;

// One row, folded then opened: its own DAX, no ask, no run on opening; Run in the DAX tab runs THAT DAX and is filed under it.
async function proveRow(page, r, tag, when) {
  await foldAll(page);
  const n0 = await rowCount(page);
  const ask0 = (await msgs(page, 'askData')).length, run0 = (await msgs(page, 'runDax')).length, rec0 = (await msgs(page, 'recordQueryRun')).length;
  await clickRow(page, r.id);
  check(await isOpen(page, r.id), `${tag} ${when} P1 ${KIND(r)}: opens`);
  const askOpen = (await msgs(page, 'askData')).slice(ask0), runOpen = (await msgs(page, 'runDax')).slice(run0);
  if (r.headline) {
    check(askOpen.length === 0 && runOpen.length === 0, `${tag} ${when} P1 ${KIND(r)} with a kept number: opening asks nothing and runs nothing (asks ${askOpen.length}, runs ${runOpen.length})`);
  } else {
    // No kept number (the engine keeps the assistant's rows without one, P4): N10 runs it once on opening. Said, not judged here.
    note(`${tag} ${when} P1 ${KIND(r)} with NO kept number: opening sent ${askOpen.length} askData ${askOpen.map((p) => JSON.stringify(p[0]).slice(0, 90)).join(' ')} and ${runOpen.length} runDax ${runOpen.map((p) => sq(p[0]).slice(0, 50)).join(' ')}`);
    await wait(1500);
  }
  const shown = await daxShown(page);
  check(shown === sq(r.dax), `${tag} ${when} P1 ${KIND(r)}: the DAX tab shows its own DAX ("${shown.slice(0, 70)}")`);
  const txt = await bodyText(page);
  check(!/replaced the query you wrote|You changed the query by hand/.test(txt), `${tag} ${when} P1 ${KIND(r)}: no note that another query replaced this one`);
  await page.screenshot({ path: join(OUT, `p1-${when.replace(/\s+/g, '-')}-${r.origin}-${r.kind}-${tag}.png`) });
  const ask1 = (await msgs(page, 'askData')).length, run1 = (await msgs(page, 'runDax')).length, rec1 = (await msgs(page, 'recordQueryRun')).length;
  await page.click(t('dax-run')); await wait(2200);
  const runs = (await msgs(page, 'runDax')).slice(run1), recs = (await msgs(page, 'recordQueryRun')).slice(rec1);
  check(runs.length === 1 && sq(runs[0][0]) === sq(r.dax), `${tag} ${when} P1 ${KIND(r)}: Run in the DAX tab ran its own DAX, once (${runs.map((p) => sq(p[0]).slice(0, 50)).join(' | ')})`);
  check(recs.length === 1 && sq(recs[0][0]) === sq(r.dax) && recs[0][4] === r.id, `${tag} ${when} P1 ${KIND(r)}: the run is recorded against this row (replaces ${recs[0]?.[4]})`);
  check((await msgs(page, 'askData')).length === ask1, `${tag} ${when} P1 ${KIND(r)}: Run in the DAX tab asked nothing`);
  check(await openId(page) === r.id && await rowCount(page) === n0, `${tag} ${when} P1 ${KIND(r)}: the result is filed under this row (open ${await openId(page)}, rows ${n0} -> ${await rowCount(page)})`);
  const fig = sq(await page.evaluate(() => document.querySelector('[data-testid="answer"] .qn-figure-n, [data-testid="answer"] [data-testid="figure"]')?.textContent || ''));
  const want = VALUE(r.dax).toLocaleString('en-US', { minimumFractionDigits: Number.isInteger(VALUE(r.dax)) ? 0 : 2 });
  check(fig.includes(want), `${tag} ${when} P1 ${KIND(r)}: the Answer shows this row's own number ${want} ("${fig}")`);
  const lab = (await page.evaluate(() => (document.querySelector('[data-testid="query-by-assistant"]')?.textContent || '').trim()));
  note(`${tag} ${when} P1 ${KIND(r)}: label after Run "${lab}"`);
  if (r.origin === 'human') check(lab === '', `${tag} ${when} P3 ${KIND(r)}: never "Asked by your assistant" ("${lab}")`);
  await page.screenshot({ path: join(OUT, `p1-${when.replace(/\s+/g, '-')}-${r.origin}-${r.kind}-ran-${tag}.png`) });
  void rec0; void ask0;
}

const assistantRunDax = (page, dax) => page.evaluate((dax) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
  seq: 700 + Math.floor(Math.random() * 200), kind: 'run_dax', origin: 'agent', label: 'Ran DAX query', ok: true, rowCount: 1, elapsedMs: 310, query: dax,
  result: { columns: [{ name: '[Units 2024]', type: 'Int64' }], rows: [[291894]], rowCount: 1, truncated: false, elapsedMs: 310, query: dax } } } })), dax);

async function sceneP1(size, theme, withNumbers) {
  const tag = `${size[0]}-${theme}${withNumbers ? '' : '-nokept'}`;
  const rows = JSON.parse(JSON.stringify(WIRE.rows));
  // As the Studio leaves them once it has shown them (P4: the assistant's rows get their number when opened once).
  if (withNumbers) for (const r of rows) if (!r.headline) r.headline = VALUE(r.dax).toLocaleString('en-US');
  const byId = Object.fromEntries(rows.map((r) => [r.id, r]));
  const order = [WIRE.personWords, WIRE.agentWords, WIRE.personDax, WIRE.agentDax];
  let { page, errors } = await launch(size, theme, null);
  await seed(page, rows);
  // The assistant's run_dax is the last query of the session (the hero's case), then the person works the rows.
  await assistantRunDax(page, byId[WIRE.agentDax].dax); await wait(1500);
  for (const id of order) await proveRow(page, byId[id], tag, 'same session');
  // The person's words row again, after the others (the cross case: 2024 under 2025).
  await proveRow(page, byId[WIRE.personWords], tag, 'same session again');
  // A full restart: the webview's persisted state is kept (the editor's last query), the page reloads, the rows come back with their ids.
  await assistantRunDax(page, byId[WIRE.agentDax].dax); await wait(1200);
  const state = await page.evaluate(() => acquireVsCodeApi().getState());
  const kept = await page.evaluate(() => JSON.parse(JSON.stringify(window.__askDataMock.notebook('list').questions)));
  note(`${tag} restart: persisted editor query "${sq(Object.entries(state || {}).find(([k]) => k.startsWith('input:lab.bq.') && !k.includes('generated'))?.[1] || '').slice(0, 70)}"`);
  check(errors.length === 0, `${tag} before restart: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
  ({ page, errors } = await launch(size, theme, state));
  await seed(page, kept);
  for (const id of [WIRE.personWords, WIRE.personDax, WIRE.agentWords, WIRE.agentDax]) await proveRow(page, kept.find((r) => r.id === id), tag, 'after restart');
  check(errors.length === 0, `${tag} after restart: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
}

// P2: folded figures of every size and sign, at the real column, with the time column beside them.
const P2_ROWS = [
  ['1', 'words', 'one'], ['$1', 'words', 'one dollar'], ['999,999,999,999.99', 'dax', null], ['$999,999,999,999.99', 'words', 'net sales all time'],
  ['-$999,999,999,999.99', 'words', 'net loss all time'], ['-1,234,567.89', 'dax', null], ['12.5%', 'words', 'margin'], ['-3.4%', 'words', 'growth'],
  ['100.00%', 'words', 'share'], ['Total Cost 2025: 29,871,303.48', 'dax', null], ['Change vs 2024: -$3,025,471.39', 'dax', null],
  ['Average Net Sales Per Active Customer In North: $999,999,999,999.99', 'dax', null], ['Margin %: -12.34%', 'dax', null], ['6 rows', 'words', 'net sales by region'],
  ['1,234,567 rows', 'dax', null], ['No rows', 'words', 'net sales in 2019'],
];
async function sceneP2(size, theme, extra) {
  const tag = `${size[0]}-${theme}`;
  const { page, errors } = await launch(size, theme, null);
  const all = process.env.P2_ONLY_EXTRA && extra ? extra : [...P2_ROWS, ...(extra || [])];
  const rows = all.map(([h, kind, q], i) => ({ id: `dq-p2-${String(i).padStart(2, '0')}`, kind, question: q, reading: q, dax: `EVALUATE ROW("v${i}", ${i})`, headline: h, outcome: 'answered', origin: 'human', inNotebook: true, pinned: false, rowCount: 1, tookMs: [12, 1380, 45000, 640][i % 4], askedAt: new Date(Date.now() - (i + 1) * 3600e3).toISOString() }));
  await seed(page, rows);
  await page.screenshot({ path: join(OUT, `p2-folded-${tag}.png`), fullPage: true });
  const got = await page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => {
    const n = r.querySelector('.qn-line-n'); const v = r.querySelector('.qn-line-val'); const l = r.querySelector('.qn-line-lab'); const tm = r.querySelector('.qn-line-t'); const tx = r.querySelector('.qn-line-text');
    if (!n || !v) return { id: r.getAttribute('data-id'), none: true, text: n?.textContent };
    const vr = v.getBoundingClientRect(); const nr = n.getBoundingClientRect(); const tr = tm.getBoundingClientRect(); const xr = tx.getBoundingClientRect();
    const lh = parseFloat(getComputedStyle(v).lineHeight) || 18;
    const range = document.createRange(); range.selectNodeContents(v); const lines = range.getClientRects().length;
    return { id: r.getAttribute('data-id'), val: v.textContent, lab: l?.textContent || '', lines, h: Math.round(vr.height), lh,
      inside: vr.left >= nr.left - 0.5 && vr.right <= nr.right + 0.5, clearOfTime: vr.right <= tr.left + 0.5 || !tm.textContent, clearOfText: vr.left >= xr.right - 0.5,
      cut: v.scrollWidth > v.clientWidth + 1, labCut: l ? l.scrollWidth > l.clientWidth : false, labW: l ? Math.round(l.getBoundingClientRect().width) : 0, time: tm.textContent, title: n.getAttribute('title') };
  }));
  for (const g of got) {
    const want = rows.find((r) => r.id === g.id);
    if (g.none) { check(!want?.headline || want.headline === '', `${tag} P2: row ${g.id} draws its figure (${g.text})`); continue; }
    const whole = want.headline.slice(want.headline.lastIndexOf(': ') < 0 ? 0 : want.headline.lastIndexOf(': ') + 2);
    check(g.lines === 1 && g.h <= g.lh * 1.3 && g.val === whole && !g.cut && g.inside && g.clearOfTime && g.clearOfText,
      `${tag} P2: "${want.headline}" -> "${g.lab}${g.lab ? ' ' : ''}${g.val}" one line ${g.lines === 1}, whole ${g.val === whole && !g.cut}, in its column ${g.inside}, clear of time "${g.time}" ${g.clearOfTime}, clear of the question ${g.clearOfText}${g.labCut ? `, label shortened to ${g.labW}px` : ''}`);
  }
  check(errors.length === 0, `${tag} P2: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
}

// 4: pin, close, Time all (one query at a time) on the same Query, after the fix.
async function sceneRegress(size, theme) {
  const tag = `${size[0]}-${theme}`;
  const { page, errors } = await launch(size, theme, null);
  const rows = JSON.parse(JSON.stringify(WIRE.rows)); for (const r of rows) if (!r.headline) r.headline = VALUE(r.dax).toLocaleString('en-US');
  await seed(page, rows);
  const pin = WIRE.personWords, close = WIRE.personDax;
  await page.click(`${rowSel(pin)} [data-testid="row-pin"], ${rowSel(pin)} button[aria-label*="Pin"]`).catch(() => {}); await wait(1200);
  const first = await page.evaluate(() => document.querySelector('[data-testid="notebook-row"]')?.getAttribute('data-id'));
  check(first === pin && await page.evaluate((s) => document.querySelector(s)?.getAttribute('data-pinned'), rowSel(pin)) === 'yes', `${tag} 4: Pin puts the row on top, pinned (${first})`);
  await page.click(`${rowSel(close)} [data-testid="row-close"], ${rowSel(close)} button[aria-label*="Close"]`).catch(() => {}); await wait(1200);
  check(!(await page.$(rowSel(close))), `${tag} 4: Close takes the row out of the notebook`);
  const b0 = (await msgs(page, 'benchmarkColdWarm')).length;
  await page.evaluate(() => { window.__vfhBench = { now: 0, max: 0 }; const orig = window.__uishotHarness.response; window.__uishotHarness.setResponse('benchmarkColdWarm', () => ({ runs: 3, coldTotal: null, warmTotal: { n: 3, avgMs: 74 } })); void orig; });
  await page.click(t('time-all')); await wait(400); await page.click(t('time-scope-all')); await wait(5000);
  const bench = (await msgs(page, 'benchmarkColdWarm')).slice(b0);
  const n = await rowCount(page);
  check(bench.length === n && new Set(bench.map((p) => sq(p[0]))).size === n, `${tag} 4: Time all All timed each row once (${bench.length} calls for ${n} rows)`);
  const txt = await bodyText(page);
  const line = (txt.match(/Timed[^\n]*/) || [''])[0];
  check(line.startsWith(`Timed ${n} of ${n}`), `${tag} 4: Time all says "${line}"`);
  await page.screenshot({ path: join(OUT, `regress-pin-close-time-${tag}.png`) });
  check(errors.length === 0, `${tag} 4: no page errors (${errors.join(' | ').slice(0, 200)})`);
  await page.close();
}

try {
  for (const size of [[1090, 900], [1440, 1000]]) for (const theme of ['dark', 'light']) {
    if (!ONLY || ONLY.includes('p2')) await sceneP2(size, theme, process.env.P2_EXTRA ? JSON.parse(process.env.P2_EXTRA) : null);
    if (!ONLY || ONLY.includes('p1')) await sceneP1(size, theme, true);
    if (!ONLY || ONLY.includes('reg')) await sceneRegress(size, theme);
  }
  if (!ONLY || ONLY.includes('p1')) await sceneP1([1090, 900], 'dark', false);
} catch (e) { fails.push('drive crashed: ' + (e?.stack || e)); }
await browser.close(); server.close();
writeFileSync(join(OUT, 'drive.txt'), lines.concat(fails.length ? ['', 'FAILURES:', ...fails] : ['', 'no failures']).join('\n') + '\n');
console.log(lines.join('\n')); console.log(fails.length ? `\n${fails.length} FAILURES` : '\nno failures');
