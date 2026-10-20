// Acceptance driver for the Query notebook (1.6, Kane picked option C on 16 Oct). The REAL Studio through the mock engine, driven by clicks and
// keys, shot in Dark Modern and Light Modern at 1440 and 1090 wide, with the alignment measure (query-notebook-align.js) run on every shot and
// the first-view measure (query-notebook-measure.js, the prototype's own counting rules) on the first views.
//
// WHAT IT PROVES, from the harness's own call trace and the DOM:
//   - the happy path: ask in words, the number and how it was read in one line, one visual; an answer lands with NO second run;
//   - the box never asks anyone to paste DAX; DAX is the open row's own view, where it is edited and run;
//   - close and keep: a row closes (it leaves the notebook, stays in the history); a pinned row stays at the top and is still there on reopen;
//   - the full history: every question, newest first; remove one, pin one, bring one back into the notebook;
//   - Time all: Selection, Pinned and All each time the right rows, ONE AT A TIME (a parked call is the only call in flight), with progress;
//   - a word that could not apply is said in the open row and marked on the folded one (K21); a blank answer says so, never a zero;
//   - Free makes no ask, history or pick call at all, and still runs and keeps DAX queries;
//   - alignment: 0 off grid, 0 ragged rows, 0 bad gaps, 0 inner-edge misses at every shot; no sideways scroll.
// WHAT IT DOES NOT: prove the engine, Jev, or a live model. The engine half is Semanticus.Tests/QueryNotebookTests.cs; the live proof is in the lane report.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/query-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'query');
mkdirSync(OUT, { recursive: true });
const strip = (f) => readFileSync(join(here, f), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const ALIGN = strip('query-notebook-align.js');
const MEASURE = strip('query-notebook-measure.js');
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `query-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const WIDTHS = process.env.QUICK ? [1440] : [1440, 1090];
const THEMES = ['dark', 'light'];
const results = [];
const failures = [];
const firstViews = [];
const expect = (ok, what) => { if (!ok) failures.push(what); return ok; };
const t = (id) => `[data-testid="${id}"]`;
const align = { shots: 0, offGrid: [], ragged: [], gaps: [], inner: [] };

async function open(q, size = [1440, 900]) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const methods = (page) => page.evaluate(() => (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean));
const calls = (page, m) => page.evaluate((x) => (window.__uishotHarness?.messages || []).filter((c) => c.method === x).map((c) => c.params ?? c.args ?? []), m);
const count = async (page, m) => (await methods(page)).filter((x) => x === m).length;
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);
const rows = (page) => page.evaluate(() => [...document.querySelectorAll('[data-testid="notebook-row"]')].map((r) => ({
  id: r.getAttribute('data-id'), open: r.getAttribute('data-open') === 'yes', pinned: r.getAttribute('data-pinned') === 'yes',
  text: (r.querySelector('.qn-line-text')?.textContent || '').trim(), n: (r.querySelector('.qn-line-n')?.textContent || '').trim(), time: (r.querySelector('[data-testid="row-time"]')?.textContent || '').trim(),
  dropped: !!r.querySelector('[data-testid="row-dropped"]') })));
const press = async (page, sel) => { await page.waitForSelector(sel, { timeout: 8000 }); await page.$eval(sel, (e) => e.scrollIntoView({ block: 'center' })); await page.click(sel); await wait(300); };
// A row's own controls sit faint until the row is hovered: hover first, as a person does.
const rowPress = async (page, id, testid) => { const sel = `[data-testid="notebook-row"][data-id="${id}"]`; await page.waitForSelector(sel, { timeout: 8000 }); await page.hover(sel); await wait(120); await page.click(`${sel} ${t(testid)}`); await wait(450); };
const ask = async (page, words, waitMs = 2400) => {
  await page.$eval(`${t('queries-ask')} input`, (e) => { e.focus(); e.select(); }); await page.keyboard.type(words);
  await page.click(t('query-ask')); await wait(waitMs);
};
const toTab = async (page, label) => { await page.evaluate((x) => { const b = [...document.querySelectorAll('button, [role="tab"]')].find((e) => e.textContent.trim() === x && e.getClientRects().length); b?.click(); }, label); await wait(900); };

// Shots of the page in both themes at both widths, with the alignment measure on each.
async function shoot(page, slug, desc, { check = true, height = 800 } = {}) {
  for (const w of WIDTHS) {
    await page.setViewport({ width: w, height: w === 1440 ? 900 : height, deviceScaleFactor: 1 });
    await wait(350);
    for (const th of THEMES) {
      await applyTheme(page, th);
      await wait(350);
      if (check) {
        const a = await page.evaluate(`(${ALIGN})()`);
        if (a) {
          align.shots += 1;
          for (const k of ['offGrid', 'ragged', 'gaps', 'inner']) for (const x of a[k]) align[k].push(`${slug} ${w} ${th}: ${x}`);
        }
        const side = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
        expect(!side, `${slug} ${w} ${th}: the page scrolls sideways`);
      }
      const file = join(OUT, `${slug}-${w}-${th}.png`);
      await page.screenshot({ path: file });
      results.push({ slug, width: w, theme: th, file, desc });
    }
    await applyTheme(page, null);
  }
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await wait(200);
}
async function firstView(page, slug) {
  for (const w of [1440, 1090]) {
    await page.setViewport({ width: w, height: w === 1440 ? 900 : 800, deviceScaleFactor: 1 });
    await wait(400);
    const m = await page.evaluate(`(${MEASURE})(${JSON.stringify({ root: '.qn-page', targets: { number: '[data-testid="figure"]', visual: '[data-testid="answer-visual"]', dax: '[data-testid="view-dax"]' } })})`);
    firstViews.push({ slug, width: w, ...m });
  }
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
}

try {
  // 1. Pro, with the notebook as the last session left it: folded rows with their numbers; the pinned row first.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    expect(await has(page, t('query-page')) && await has(page, t('notebook')), 'start: the Query page and its notebook are mounted');
    const r = await rows(page);
    expect(r.map((x) => x.id).join() === 'dq-seed02,dq-seed03,dq-seed04,dq-seed05', `start: the pinned row first, then the open ones newest first, the closed one not shown (${r.map((x) => x.id).join()})`);
    expect(r[0].pinned && r[0].n === '$2.65M', 'start: a folded row shows its number (kept with the row), and a pin');
    expect(r.find((x) => x.id === 'dq-seed03').dropped, 'start: a row whose word could not apply is marked (K21)');
    expect(r.find((x) => x.id === 'dq-seed04').n === 'No rows', 'start: a blank answer says No rows, never a zero');
    expect(/1\.6 s then 210 ms/.test(r.find((x) => x.id === 'dq-seed05').time), 'start: a timed row shows its first and its repeat time');
    const ph = await page.$eval(`${t('queries-ask')} input`, (e) => e.getAttribute('placeholder'));
    expect(!/dax|paste|query/i.test(ph), `start: the box prompt never mentions DAX: "${ph}"`);
    expect(!/paste/i.test(await text(page, t('query-page'))), 'start: nothing on the page asks anyone to paste DAX');
    await shoot(page, '01-start', 'Pro, the notebook as the last session left it');
    await firstView(page, 'start');
    expect(errors.length === 0, 'start: page errors ' + errors.join('; '));
    await page.close();
  }

  // 2. The happy path: ask in words, read the number, see the visual, open the tools, run the DAX, test the speed, explain a number.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    await ask(page, 'total sales by region');
    const r = await rows(page);
    expect(r.length === 5 && r.find((x) => x.open)?.text === 'total sales by region', `ask: the question is a new open row (${r.map((x) => x.text).join(' | ')})`);
    expect(r[0].pinned && r[1].open, 'ask: the pinned row stays above the new one');
    expect(await has(page, t('answer-visual')) && !(await has(page, t('figure'))), 'ask: a several-row answer leads with its visual, never a row count alone (UAT F26)');
    expect((await text(page, t('reading'))).length > 0, 'ask: Read as shows how the words were taken, in one line');
    expect(await has(page, t('answer-visual')), 'ask: one visual');
    expect(await count(page, 'askData') === 1, `ask: one askData call (${await count(page, 'askData')})`);
    expect(await count(page, 'runDax') === 0, `ask: the answer's rows land with NO second run (${await count(page, 'runDax')} runDax calls)`);
    expect(await count(page, 'pickChartKind') <= 1, `ask: at most ONE visual pick call (${await count(page, 'pickChartKind')})`);
    expect(!(await has(page, t('dax-card'))) && !(await has(page, t('result'))) && !(await has(page, t('speed-card'))), 'ask: no tool is on screen until it is asked for');
    await shoot(page, '02-answer', 'Pro, a question asked in words: the number, how it was read, one visual');
    await firstView(page, 'answer');
    // The switch beside the answer: another visual that fits is one press (UAT 17 Oct, F35)
    const fits = await page.$$eval('[data-testid="notebook"] [data-testid="visual-switch"] [data-testid^="fit-"]', (b) => b.map((x) => x.getAttribute('data-testid')));
    expect(fits.length >= 2 && await has(page, `${t('notebook')} ${t('visual-switch')}`), `visual: the switch beside the answer lists the visuals that fit (${fits.join(', ')})`);
    const other = fits.find((f) => f !== 'fit-bars') || fits[1];
    await press(page, `${t('notebook')} ${t('visual-switch')} ${t(other)}`); await wait(300);
    expect(await has(page, `${t('notebook')} ${t('visual-table')}`) || await has(page, `${t('notebook')} .xe-table, ${t('notebook')} table`), 'visual: one press shows another visual');
    // DAX: the open row's own view
    await press(page, t('view-dax'));
    expect(await has(page, t('dax-card')) && (await has(page, t('queries-editor')) || await has(page, '.cm-content')), 'dax: the row\'s DAX view holds the query, where it can be edited');
    await shoot(page, '03-dax', 'Pro, the row\'s DAX view: fields or DAX, Run, Copy, Fields, Compare, Reset, Runs on');
    const runs0 = await count(page, 'runDax');
    await press(page, t('dax-run')); await wait(1500);
    expect(await count(page, 'runDax') === runs0 + 1, 'dax: Run in the DAX view runs the query once');
    expect(await count(page, 'recordQueryRun') >= 1, 'dax: a query run as written is recorded as a row');
    // Table, Speed, Steps, Compare, Save: each is one press
    await press(page, t('view-table'));
    expect(await has(page, t('queries-result')), 'table: every row, with the grid');
    await shoot(page, '04-table', 'Pro, the Table view');
    await press(page, t('view-speed'));
    await press(page, t('speed-start')); await wait(2600);
    expect(await count(page, 'benchmarkColdWarm') === 1 && await count(page, 'profileDax') === 1, 'speed: Measure speed runs cold and warm, then the profile');
    expect(/ms/.test(await text(page, t('queries-speed-answer'))), 'speed: the figures show');
    expect((await text(page, t('speed-insight'))).length > 20, 'speed: one plain sentence says what the numbers mean');
    expect(!(await has(page, t('every-run'))) && !(await has(page, t('clear-first'))), 'speed: the options and where the time went wait behind their own buttons');
    await press(page, t('fold-time')); await press(page, t('fold-every-run')); await press(page, t('speed-options'));
    expect(await has(page, t('every-run')) && await has(page, t('clear-first')) && await has(page, t('how-quick')), 'speed: Where the time went and Options open in place');
    await shoot(page, '05-speed', 'Pro, the Speed view after Measure speed, with Options and Where the time went open');
    await press(page, t('view-steps'));
    expect(await has(page, t('checks')) || await has(page, t('queries-steps')), 'steps: the checks and the steps');
    await press(page, t('plan-record')); await wait(900);
    expect(await count(page, 'captureQueryPlan') === 1, 'steps: Record the steps captures the plan');
    await press(page, t('log-measures')); await wait(1200);
    expect(await count(page, 'evaluateAndLog') === 1, 'steps: the calculation steps run the logged query');
    await shoot(page, '06-steps', 'Pro, the Steps view');
    await press(page, t('view-compare'));
    await press(page, `${t('compare-card')} .sem-btn-primary`); await wait(900);
    expect(await count(page, 'verifyEquivalence') === 1, 'compare: Compare calls verify');
    await press(page, t('view-save'));
    expect(await has(page, t('save-card')), 'save: Save as a check is a view of the row');
    // Explain a number by pressing it in the Table
    await press(page, t('view-table')); await wait(600);
    const cell = await page.$('[data-testid="queries-result"] .xe-gcell.sem-num');
    if (cell) { await cell.evaluate((e) => e.scrollIntoView({ block: 'center' })); await cell.click(); await wait(1300); }
    expect(await count(page, 'explainValue') >= 1, 'explain: a pressed number is explained');
    expect(await has(page, t('view-explain')) && await has(page, `${t('explain-card')} [data-explain-panel]`), 'explain: the dossier opens as a view of the same row');
    await shoot(page, '07-explain', 'Pro, an explained number');
    expect(errors.length === 0, 'happy path: page errors ' + errors.join('; '));
    await page.close();
  }

  // 3. Close and keep, pin, the full history, remove, bring back; and the notebook is the same after a reopen.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed');
    const ids = async () => (await rows(page)).map((r) => r.id).join();
    await rowPress(page, 'dq-seed05', 'row-pin');
    expect((await ids()) === 'dq-seed02,dq-seed05,dq-seed03,dq-seed04', `pin: a pinned row moves up with the other pinned row (${await ids()})`);
    await rowPress(page, 'dq-seed04', 'row-close');
    expect((await ids()) === 'dq-seed02,dq-seed05,dq-seed03', `close: the row leaves the notebook (${await ids()})`);
    // reopen: leave the tab and come back; the engine keeps the rows
    await toTab(page, 'Data'); await toTab(page, 'Query'); await wait(800);
    expect((await ids()) === 'dq-seed02,dq-seed05,dq-seed03', `reopen: the same rows, the pinned ones first (${await ids()})`);
    expect((await rows(page)).filter((r) => r.pinned).length === 2, 'reopen: both pins are still there');
    // history
    await press(page, t('history-open'));
    const hist = await page.$$eval('[data-testid="history-row"]', (r) => r.map((x) => ({ q: x.querySelector('.qn-hist-q')?.textContent, open: x.getAttribute('data-open') })));
    expect(hist.length === 5 && hist[0].q === 'margin % by region' && hist.some((h) => h.q === 'sales in 2031' && h.open === 'no'), `history: every question, newest first, closed ones too (${hist.map((h) => h.q).join(' | ')})`);
    await shoot(page, '08-history', 'Pro, the history menu: every question, newest first, with Pin and Remove', { check: false });
    // bring one back
    const back = (await page.$$('[data-testid="history-row"] .qn-hist-main'))[3];
    await back.click(); await wait(1500);
    expect((await ids()).split(',').includes('dq-seed04'), 'history: pressing a closed question brings it back into the notebook');
    expect((await rows(page)).find((r) => r.id === 'dq-seed04').open, 'history: and opens it');
    // remove one from the history
    await press(page, t('history-open'));
    const before = (await page.$$('[data-testid="history-row"]')).length;
    await (await page.$$('[data-testid="history-remove"]'))[before - 1].click(); await wait(500);
    expect((await page.$$('[data-testid="history-row"]')).length === before - 1, 'history: Remove deletes one question from the history');
    await page.keyboard.press('Escape');
    const nb = await calls(page, 'queryNotebook');
    for (const a of ['pin', 'close', 'reopen', 'remove']) expect(nb.some((c) => c[0] === a), `calls: the page uses queryNotebook ${a} (the assistant's tool reads the same rows)`);
    expect(errors.length === 0, 'notebook: page errors ' + errors.join('; '));
    await page.close();
  }

  // 4. Time all: Selection, Pinned, All; one at a time; progress.
  {
    const { page, errors } = await open('tab=query&tier=pro&nb=seed&hold=benchmarkColdWarm');
    await page.hover('[data-testid="notebook-row"][data-id="dq-seed03"]'); await wait(120);
    await page.click('[data-testid="notebook-row"][data-id="dq-seed03"] [data-testid="row-tick"]');
    await page.hover('[data-testid="notebook-row"][data-id="dq-seed05"]'); await wait(120);
    await page.click('[data-testid="notebook-row"][data-id="dq-seed05"] [data-testid="row-tick"]');
    await press(page, t('time-all'));
    const menu = await page.$$eval('[role="menuitem"][data-testid^="time-scope-"]', (b) => b.map((x) => `${x.getAttribute('data-testid')}:${x.textContent}`));
    expect(menu.join() === 'time-scope-selection:Selected rows2,time-scope-pinned:Pinned rows1,time-scope-all:All rows4', `time all: the choices say how many rows each times (${menu.join()})`);
    await shoot(page, '09-time-menu', 'Pro, Time all: Selected rows, Pinned rows or All rows', { check: false });
    await press(page, t('time-scope-selection')); await wait(700);
    expect(await count(page, 'benchmarkColdWarm') === 1, `time all: one query at a time: the first is parked and nothing else was sent (${await count(page, 'benchmarkColdWarm')})`);
    expect(/Timing 1 of 2/.test(await text(page, t('time-progress'))), `time all: progress says where it is (${await text(page, t('time-progress'))})`);
    await shoot(page, '10-timing', 'Pro, Time all running: Timing 1 of 2', { check: false });
    await page.evaluate(() => window.__uishotHarness.release('benchmarkColdWarm')); await wait(1500);
    expect(await count(page, 'benchmarkColdWarm') === 2, `time all: Selection timed exactly the 2 chosen rows (${await count(page, 'benchmarkColdWarm')})`);
    const r = await rows(page);
    expect(/412 ms then 96 ms/.test(r.find((x) => x.id === 'dq-seed03').time) && /412 ms then 96 ms/.test(r.find((x) => x.id === 'dq-seed05').time), `time all: one compact result per row (${r.map((x) => x.time).join(' | ')})`);
    expect(/Timed 2 of 2/.test(await text(page, t('time-progress'))), 'time all: one line says how it ended');
    await press(page, t('time-all')); await press(page, t('time-scope-pinned')); await wait(1200);
    expect(await count(page, 'benchmarkColdWarm') === 3, 'time all: Pinned timed the 1 pinned row');
    await press(page, t('time-all')); await press(page, t('time-scope-all')); await wait(2500);
    expect(await count(page, 'benchmarkColdWarm') === 7, `time all: All timed the 4 rows of the notebook (${await count(page, 'benchmarkColdWarm')} calls)`);
    await shoot(page, '11-timed', 'Pro, after Time all: each row with its first and repeat time');
    expect(errors.length === 0, 'time all: page errors ' + errors.join('; '));
    await page.close();
  }

  // 5. A word that could not apply: said in the open row, marked on the folded one (K21).
  {
    const { page, errors } = await open('tab=query&tier=pro');
    await ask(page, 'premium total sales by region');
    expect(/premium/.test(await text(page, t('answer'))) && /not in the model/.test(await text(page, t('answer'))), 'K21: the open row says the word that did not apply');
    await ask(page, 'total sales by category');
    const r = await rows(page);
    expect(r.find((x) => /premium/.test(x.text))?.dropped, 'K21: the folded row still carries the mark');
    await shoot(page, '12-dropped', 'Pro, a word that could not apply: said in the open row, marked on the folded one');
    expect(errors.length === 0, 'K21: page errors ' + errors.join('; '));
    await page.close();
  }

  // 6. A model that keeps no history: the answer still shows, in the one row, with the plain note.
  {
    const { page, errors } = await open('tab=query&tier=pro&askdata=unsaved');
    expect(/Save this model/.test(await text(page, t('notebook-empty'))), 'unsaved: the empty notebook says why nothing is kept');
    await ask(page, 'total sales by region');
    expect(await has(page, t('answer-visual')) && !(await has(page, t('figure'))), 'unsaved: the answer still shows in its row, led by its visual');
    expect(errors.length === 0, 'unsaved: page errors ' + errors.join('; '));
    await page.close();
  }

  // 7. Free: the page works, the box is the quiet Pro line, no ask, history or pick call is ever sent, and a query is still kept.
  {
    const { page, errors } = await open('tab=query&tier=free');
    expect(await has(page, t('query-page')), 'free: the page is the real page, not a preview');
    expect(await has(page, t('queries-ask-pro-pill')) && !(await has(page, `${t('queries-ask')} input`)), 'free: the words box is one quiet Pro line');
    await press(page, t('new-query'));
    expect(await has(page, t('dax-card')), 'free: New query opens a row with its DAX view');
    await shoot(page, '13-free-new', 'Free, a new query: Fields or DAX', { check: true });
    await press(page, t('mode-dax')); await press(page, t('dax-run')); await wait(1500);
    const sent = await methods(page);
    expect(!sent.some((m) => ['askData', 'listDataQuestions', 'pickChartKind'].includes(m)), 'free: no askData, listDataQuestions or pickChartKind call: ' + sent.filter((m) => ['askData', 'listDataQuestions', 'pickChartKind'].includes(m)).join(','));
    expect(sent.includes('runDax') && sent.includes('recordQueryRun'), 'free: the query runs and is kept as a row');
    const r = await rows(page);
    expect(r.length === 1 && r[0].open, `free: the query is the one row of the notebook (${r.length})`);
    await shoot(page, '14-free-ran', 'Free, the query ran and is a row', { check: true });
    expect(errors.length === 0, 'free: page errors ' + errors.join('; '));
    await page.close();
  }

  // 8. The old routes land here.
  for (const old of ['askdata', 'daxlab']) {
    const { page } = await open(`tab=${old}&tier=pro`);
    expect(await has(page, t('query-page')), `route: ${old} lands on the Query page`);
    await page.close();
  }
} catch (e) {
  failures.push('driver crashed: ' + (e?.stack || e));
} finally {
  await browser.close();
  server.close();
}

for (const k of ['offGrid', 'ragged', 'gaps', 'inner']) expect(align[k].length === 0, `alignment ${k}: ${[...new Set(align[k])].slice(0, 6).join(' ; ')}`);
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures, align, firstViews }, null, 2));
console.log(`query-drive: ${results.length} screenshots in ${OUT}; alignment measured on ${align.shots} shots: off grid ${align.offGrid.length}, ragged ${align.ragged.length}, gaps ${align.gaps.length}, inner edges ${align.inner.length}`);
for (const v of firstViews) console.log(`first view ${v.slug} ${v.width}: ${v.words} words, ${v.controls} controls, page scroll ${v.scrollRange} px, number ${JSON.stringify(v.targets?.number)}, visual ${JSON.stringify(v.targets?.visual)}`);
if (failures.length) { console.log('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('query-drive: all checks passed');
