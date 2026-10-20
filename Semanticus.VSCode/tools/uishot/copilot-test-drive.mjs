// Browser-driven proof of Review > Copilot test (Kane, 15 Oct 2026, option A). The real Studio on the mocked engine, with four rounds of a Contoso bank
// of 140 questions (28 held back), the misses of round 3, and the six changes tried in round 4. It checks the page against the design lens: the work is visible,
// nothing is stale, progress shows, every part sits on the 12-column grid (copilot-test-align.js, from tools/query-proto/align.js), nothing spills sideways, and
// every control works. The engine is mocked: the round states and the numbers are typed in below, not computed.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium [THEMES=dark,light] [WIDTHS=1440,1090] node tools/uishot/copilot-test-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'copilot-test');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark').split(',');
const WIDTHS = (process.env.WIDTHS || '1440,1090').split(',').map(Number);
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('Build the webview first.'); process.exit(1); }
const FIXTURE_SRC = readFileSync(join(here, 'fixtures', 'plan-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const ALIGN = readFileSync(join(here, 'copilot-test-align.js'), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- the fixture: what the engine would answer (typed in; the numbers are not computed) ------------------------------------------------
const seed = (state) => `
${FIXTURE_SRC}
const __plan = makePlan({});
window.__calls = [];
const KINDS = ['totals', 'filters', 'time', 'ranking', 'words', 'names', 'cannot'];
const SIZE = { totals: 24, filters: 28, time: 26, ranking: 18, words: 16, names: 20, cannot: 8 };
const RIGHT = [
  { totals: 22, filters: 17, time: 13, ranking: 12, words: 6, names: 9, cannot: 6 },
  { totals: 23, filters: 21, time: 18, ranking: 14, words: 9, names: 14, cannot: 7 },
  { totals: 24, filters: 23, time: 21, ranking: 15, words: 11, names: 17, cannot: 7 },
  { totals: 24, filters: 26, time: 23, ranking: 16, words: 14, names: 19, cannot: 7 },
];
const HELD = [16, 20, 22, 24];     // right out of 28 held back, per round
const sum = (o) => KINDS.reduce((s, k) => s + o[k], 0);
const score = (right, total) => ({ right, total, couldNotCheck: 0, share: total ? Math.round((right / total) * 10000) / 10000 : null });
const missCounts = { wrong_field: 7, wrong_filter: 5, wrong_number: 3, said_no: 4, should_say_no: 1, dax_failed: 2 };
const missesOf3 = [
  { n: 61, question: 'What were sales in Canada?', kind: 'words', miss: 'wrong_field', missWords: 'Wrong field', studentDax: 'EVALUATE ROW("v", CALCULATE([Units], Store[Country] = "Canada"))', studentAnswer: '41,907', referenceDax: 'EVALUATE ROW("v", CALCULATE([Total Sales], Store[Country] = "Canada"))', referenceAnswer: '3402118', detail: '', cause: 'A similar field was used in place of the right one: Units, Total Sales' },
  { n: 62, question: 'What was revenue in 2025?', kind: 'names', miss: 'wrong_field', missWords: 'Wrong field', studentDax: 'EVALUATE ROW("v", CALCULATE([Net Price], \\'Date\\'[Year] = 2025))', studentAnswer: '1204.88', referenceDax: 'EVALUATE ROW("v", CALCULATE([Total Sales], \\'Date\\'[Fiscal Year] = "FY2025"))', referenceAnswer: '22890412', detail: '', cause: 'A word in the question is not a name the model knows: Total Sales' },
  { n: 63, question: 'Sales this year so far', kind: 'time', miss: 'wrong_filter', missWords: 'Wrong filter', studentDax: 'EVALUATE ROW("v", CALCULATE([Total Sales], DATESYTD(\\'Date\\'[Date])))', studentAnswer: '11020551', referenceDax: 'EVALUATE ROW("v", CALCULATE([Total Sales], DATESYTD(\\'Date\\'[Date], "6-30")))', referenceAnswer: '19644130', detail: '', cause: 'The calendar year was used, and the model runs on another year: Date[Date], year end' },
  { n: 64, question: 'Margin last year', kind: 'time', miss: 'wrong_filter', missWords: 'Wrong filter', studentDax: 'EVALUATE ROW("v", CALCULATE([Margin], \\'Date\\'[Year] = 2025))', studentAnswer: '11876002', referenceDax: 'EVALUATE ROW("v", CALCULATE([Margin], \\'Date\\'[Fiscal Year] = "FY2025"))', referenceAnswer: '12310448', detail: '', cause: 'The calendar year was used, and the model runs on another year: Date[Year], Date[Fiscal Year]' },
  { n: 65, question: 'What is our margin?', kind: 'words', miss: 'wrong_number', missWords: 'Wrong number', studentDax: 'EVALUATE ROW("v", ROUND([Margin], -6))', studentAnswer: '18000000', referenceDax: 'EVALUATE ROW("v", [Margin])', referenceAnswer: '18433770', detail: '', cause: 'Another cause' },
  { n: 66, question: 'Turnover by region', kind: 'names', miss: 'said_no', missWords: 'Said no, could answer', studentDax: null, studentAnswer: 'said it cannot answer', referenceDax: 'EVALUATE SUMMARIZECOLUMNS(Store[Territory], "v", [Total Sales])', referenceAnswer: '6 rows', detail: '', cause: 'A word in the question is not a name the model knows: Store[Territory], Total Sales' },
  { n: 67, question: 'Are we ahead of target this quarter?', kind: 'cannot', miss: 'should_say_no', missWords: 'Answered, should say no', studentDax: 'EVALUATE ROW("v", DIVIDE([Total Sales], [Sales LY]) - 1)', studentAnswer: '0.061', referenceDax: null, referenceAnswer: 'Say no', detail: '', cause: 'The model has no data for this, and the student answered anyway' },
  { n: 68, question: 'Bottom 3 stores by margin', kind: 'ranking', miss: 'dax_failed', missWords: 'DAX failed', studentDax: 'EVALUATE TOPN(3, Store, [Margin], ASCENDING)', studentAnswer: null, referenceDax: 'EVALUATE TOPN(3, VALUES(Store[Store]), [Margin], ASC)', referenceAnswer: '3 rows', detail: '', cause: 'A mistake in the DAX that the model cannot fix' },
];
const groups = [
  { cause: 'sibling_measure', fix: 'description', label: 'A similar field was used in place of the right one: Units, Total Sales', fixWords: 'A description', count: 1, numbers: [61], objects: ['Units'], by: 'jev' },
  { cause: 'calendar_vs_fiscal', fix: 'instruction', label: 'The calendar year was used, and the model runs on another year: Date[Year], year end', fixWords: 'AI instructions', count: 2, numbers: [63, 64], objects: [], by: 'jev' },
  { cause: 'unknown_word', fix: 'other_name', label: 'A word in the question is not a name the model knows: Total Sales, Store[Territory]', fixWords: 'Other names', count: 2, numbers: [62, 66], objects: [], by: 'jev' },
  { cause: 'no_data', fix: 'instruction', label: 'The model has no data for this, and the student answered anyway', fixWords: 'AI instructions', count: 1, numbers: [67], objects: [], by: 'jev' },
  { cause: 'other', fix: 'instruction', label: 'Another cause', fixWords: 'AI instructions', count: 1, numbers: [65], objects: [], by: 'rules' },
  { cause: 'dax_slip', fix: 'none', label: 'A mistake in the DAX that the model cannot fix', fixWords: 'No change', count: 1, numbers: [68], objects: [], by: 'jev' },
];
const changes = [
  { title: 'Say that "sales" means Total Sales, in money', where: 'AI instructions', on: 'Model', before: '(nothing about sales)', after: 'When someone says "sales", use Total Sales. It is money, not units. Use Units only when they say units or items.', forMisses: [61, 5, 7, 9, 11] },
  { title: 'Add "revenue" and "turnover" as other names for Total Sales', where: 'Other names', on: 'Total Sales', before: 'sales amount', after: 'sales amount, revenue, turnover', forMisses: [62, 13, 15] },
  { title: 'Describe Margin as money, and point to Margin % for the percent', where: 'Description', on: 'Margin', before: '', after: 'Total Sales minus Total Cost, in money. For the percent, use Margin %.', forMisses: [65, 17] },
  { title: 'Say that the year starts on 1 July', where: 'AI instructions', on: 'Model', before: '(nothing about the year)', after: 'The year runs 1 July to 30 June. "This year" and "last year" mean the fiscal year in Date[Fiscal Year], unless someone names a calendar year.', forMisses: [63, 64, 21, 22] },
  { title: 'Add "region" and "area" as other names for Territory', where: 'Other names', on: 'Store[Territory]', before: '', after: 'region, area', forMisses: [66, 31] },
  { title: 'Say there is no budget or target data', where: 'AI instructions', on: 'Model', before: '(nothing)', after: 'This model has no budget, target or forecast. If asked, say so and offer last year instead.', forMisses: [67] },
];
const FIXED = [4, 3, 1, 3, 1, 0];
function round(i, complete) {
  const right = complete ? sum(RIGHT[i]) : 74;
  const total = complete ? 140 : 80;
  const heldRight = complete ? HELD[i] : 16, heldTotal = complete ? 28 : 18;
  const when = ['2026-10-12T04:14:00Z', '2026-10-13T06:40:00Z', '2026-10-14T23:02:00Z', '2026-10-15T01:20:00Z'][i];
  return {
    id: 'rd-00000' + (i + 1), number: i + 1, when, student: 'Haiku 5.5', total: 140, answered: complete ? 140 : 86, complete,
    practice: score(right - heldRight, total - heldTotal), heldBack: score(heldRight, heldTotal), all: score(right, total), couldNotCheck: 0,
    byKind: KINDS.map((k) => ({ kind: k, right: RIGHT[i][k], total: SIZE[k] })),
    missCounts: complete ? { wrong_field: 2, wrong_filter: 2, wrong_number: 1, said_no: 1, should_say_no: 1, dax_failed: 1 } : { wrong_field: 1, wrong_filter: 1, wrong_number: 0, said_no: 1, should_say_no: 0, dax_failed: 1 },
    newMisses: i === 3 ? 1 : 0,
    changes: i === 3 ? changes.map((c, j) => ({ ...c, fixed: FIXED[j] })) : [],
    groups: complete ? groups : [], misses: [],
  };
}
const roundsDone = [0, 1, 2, 3].map((i) => round(i, true));
const roundsRunning = [0, 1, 2].map((i) => round(i, true)).concat([round(3, false)]);
const rounds = ${JSON.stringify(state)} === 'running' ? roundsRunning : ${JSON.stringify(state)} === 'empty' ? [] : roundsDone;
const EMPTY = ${JSON.stringify(state)} === 'empty';
const bankRowsOf = KINDS.map((k) => ({ kind: k, count: EMPTY ? 0 : SIZE[k] }));
const Q = [
  ['totals', 'What were total sales?', '48213904', 'EVALUATE ROW("v", [Total Sales])'], ['totals', 'How many orders do we have?', '96512', 'EVALUATE ROW("v", [Order Count])'],
  ['totals', 'What is our average order value?', '499.56', 'EVALUATE ROW("v", [Avg Order Value])'], ['filters', 'Sales of bikes in Germany', '2914360', 'EVALUATE ROW("v", CALCULATE([Total Sales], Product[Category] = "Bikes", Store[Country] = "Germany"))'],
  ['filters', 'Margin for corporate customers', '6102775', 'EVALUATE ROW("v", CALCULATE([Margin], Customer[Segment] = "Corporate"))'], ['filters', 'Orders from online stores', '31208', 'EVALUATE ROW("v", CALCULATE([Order Count], Store[Channel] = "Online"))'],
  ['time', 'Sales this year compared with last year', '0.084', 'EVALUATE ROW("v", [Sales YoY %])'], ['time', 'Sales in March 2026', '4180227', 'EVALUATE ROW("v", CALCULATE([Total Sales], \\'Date\\'[Month] = "Mar 2026"))'],
  ['time', 'Year to date margin', '9844010', 'EVALUATE ROW("v", CALCULATE([Margin], DATESYTD(\\'Date\\'[Date], "6-30")))'], ['ranking', 'Top 5 products by sales', null, 'EVALUATE TOPN(5, VALUES(Product[Product]), [Total Sales])'],
  ['ranking', 'Which store sold the least last year?', null, 'EVALUATE TOPN(1, VALUES(Store[Store]), [Sales LY], ASC)'], ['words', 'What were sales in Canada?', '3402118', 'EVALUATE ROW("v", CALCULATE([Total Sales], Store[Country] = "Canada"))'],
  ['words', 'Who is our best customer?', null, 'EVALUATE TOPN(1, VALUES(Customer[Customer]), [Total Sales])'], ['names', 'What was revenue in 2025?', '22890412', 'EVALUATE ROW("v", CALCULATE([Total Sales], \\'Date\\'[Fiscal Year] = "FY2025"))'],
  ['names', 'Turnover by region', null, 'EVALUATE SUMMARIZECOLUMNS(Store[Territory], "v", [Total Sales])'], ['cannot', 'Are we ahead of budget?', null, null], ['cannot', 'How many people visited the stores?', null, null],
].map(([kind, question, value, query], i) => ({ id: 'iq-' + i, question, kind, tier: query ? 'value' : 'refusal', query, expectedValue: value, expectedMatrix: value || !query ? null : [['a', '1'], ['b', '2'], ['c', '3'], ['d', '4'], ['e', '5']], heldBack: i % 5 === 4, expectRefusal: !query }));
const view = { text: 'THE MODEL ...', tables: 6, columns: 48, measures: 31, measuresDescribed: 27, otherNames: 38, instructionWords: 412, leftOut: 12, sees: [
  { what: 'Tables, columns and measures', count: '6 tables, 48 columns, 31 measures', on: true }, { what: 'AI instructions', count: '412 words', on: true },
  { what: 'Descriptions', count: '27 of 31 measures', on: true }, { what: 'Other names', count: '38 names', on: true },
  { what: 'Hidden tables and columns', count: '12 left out', on: false }, { what: 'Your data and the right answers', count: 'never shown', on: false } ] };
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v; v.setResponse('getModelPlan', () => __plan);
  v.setResponse('getPlanHistory', () => ({ sessionId: 's', rows: [], more: false, kept: true }));
  v.setResponse('interviewRounds', (params) => {
    window.__calls.push(['interviewRounds', params[0] ?? null]);
    const r = params[0];
    return { rounds: rounds.map((x) => (r != null && String(x.number) === String(r) ? { ...x, misses: missesOf3 } : x)), bankSize: EMPTY ? 0 : 140, bankByKind: bankRowsOf, bankKey: 'abc', heldBack: EMPTY ? 0 : 28 };
  });
  v.setResponse('listInterviewQuestions', () => ({ questions: EMPTY ? [] : Q }));
  v.setResponse('getCopilotView', () => view);
  v.setResponse('addPlanTask', (params) => { window.__calls.push(['addPlanTask', params[0]]); return { itemId: 'task-' + window.__calls.length }; });
} });
`;

// ---- the drive ----------------------------------------------------------------------------------------------------------------------------
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };
const count = (a) => a ? Object.fromEntries(['offGrid', 'ragged', 'titleLines', 'gaps', 'inner'].map((k) => [k, a[k].length])) : null;
const sideways = (p) => p.evaluate(() => {
  const pg = document.querySelector('.ct-page'); let e = pg, bad = document.documentElement.scrollWidth > innerWidth + 1;
  while (e && e !== document.body) { if (e.scrollWidth > e.clientWidth + 1 && /(auto|scroll)/.test(getComputedStyle(e).overflowX)) bad = true; e = e.parentElement; }
  for (const c of document.querySelectorAll('.ct-page > .sem-card')) {
    if (c.scrollWidth > c.clientWidth + 1) bad = true;
    const r = c.getBoundingClientRect();
    for (const t of c.querySelectorAll('table, svg, pre, .ct-change-line')) { const q = t.getBoundingClientRect(); if (q.width && q.right > r.right + 1) bad = true; }
  }
  return bad;
});
const fullHeight = (p) => p.evaluate(() => { let e = document.querySelector('.ct-page'); while (e && e !== document.body) { const s = getComputedStyle(e); if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight) return e.scrollHeight + (innerHeight - e.clientHeight); e = e.parentElement; } return document.documentElement.scrollHeight; });
const results = { runs: [] };
for (const theme of THEMES) for (const width of WIDTHS) for (const state of ['done', 'running', 'empty']) {
  console.log(`== ${theme} ${width} ${state}`);
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => { failed += 1; errors.push(String(e)); console.error('pageerror: ' + e.message); });
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(state));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=copilottest`, { waitUntil: 'networkidle0', timeout: 30000 });
  await applyTheme(page, theme);
  await wait(900);
  const dir = join(OUT, `${theme}-${width}-${state}`); mkdirSync(dir, { recursive: true });
  let n = 0; const run = { theme, width, state, align: [], sideways: [], missing: [], shots: [] };
  const measure = async (step) => { const a = await page.evaluate(`(${ALIGN})()`); const c = count(a); run.align.push({ step, ...c }); if (c && Object.values(c).some((v) => v)) run.align[run.align.length - 1].detail = a; if (await sideways(page)) run.sideways.push(step); return a; };
  const shot = async (name, full = false) => { n += 1; const f = join(dir, `${String(n).padStart(2, '0')}-${name}.png`); if (full) { await page.setViewport({ width, height: Math.min(9000, await fullHeight(page)), deviceScaleFactor: 1 }); await wait(500); } await page.screenshot({ path: f }); if (full) { await page.setViewport({ width, height: 900, deviceScaleFactor: 1 }); await wait(300); } run.shots.push(f); };
  const text = () => page.evaluate(() => document.querySelector('.ct-page')?.innerText ?? '');

  check(!!(await page.$('.ct-page')), 'the Copilot test page is on screen');
  await measure('load'); await shot('load', true);
  const body = await text();
  check(/Copilot test/.test(body) && /Score over rounds/.test(body) && /Question bank/.test(body) && /Who answers/.test(body), 'the page has its parts: score, round, changes, misses, heat, kinds, bank, who answers');
  if (state === 'empty') {
    check(/No round has run yet/.test(body) && /There is no question bank yet/.test(body), 'an empty page says so, and offers the assistant the first job');
    check(!!(await page.$('[data-send-key="copilot-test:bank"]')), 'the bank card has Write the questions');
  }
  check(!body.includes('—') && !/Q&A|Map\b/.test(body), 'no em dash, no "Q&A", no "Map" in the words on the page');
  if (state === 'done') {
    check(/92%/.test(await page.$eval('[data-testid=score-now]', (e) => e.textContent)), 'the big number is the score of round 4 (92%)');
    check(/Round 4, up/.test(body) && /Above your aim of 90%/.test(body), 'the line under it says how far it moved and that the aim is reached');
    check(/Fixed 4 of 5/.test(body), 'a change says what it fixed: "Fixed 4 of 5"');
    check(/1 question that was right is now a miss/.test(body), 'a new miss is said, not hidden');
    check(/\b24 of 24\b|\b24 of 28\b/.test(await page.$eval('[data-testid=score-legend]', (e) => e.textContent)) || /held back: 86%/.test(body), 'the held-back score sits beside the score');
  } else if (state === 'running') {
    check(/so far/.test(body), 'a running round says "so far"');
    check(await page.$eval('[data-testid=round-state]', (e) => e.textContent) === 'Running', 'the round card says Running');
    const w = await page.$eval('.ct-prog-graded', (e) => e.style.width);
    check(parseFloat(w) > 55 && parseFloat(w) < 70, 'the progress bar shows 86 of 140 (' + w + ')');
  }
  if (state !== 'empty') check(!!(await page.$('[data-testid=card-bank] [data-testid=kind-totals]')), 'the bank card lists its kinds');
  // The student DAX sits beside the right DAX at 1090 and 1440.
  const sides = await page.evaluate(() => { const m = document.querySelector('[data-testid="miss-61"]'); if (!m) return null; const [a, b] = [...m.querySelectorAll('.ct-side')].map((e) => e.getBoundingClientRect()); return { sameRow: Math.abs(a.top - b.top) < 2, ordered: a.right <= b.left + 1 }; });
  if (state !== 'empty') check(!!sides && sides.sameRow && sides.ordered, 'the student DAX sits beside the right DAX (same row)');
  // Press every control.
  const ids = await page.evaluate(() => [...document.querySelectorAll('.ct-page [data-testid]')].filter((e) => e.matches('button, input')).map((e) => e.getAttribute('data-testid')));
  for (const id of [...new Set(ids)].filter((x) => x !== 'open-history')) {   // open-history leaves the page, so it is pressed last
    const el = await page.$(`[data-testid="${id}"]`); if (!el) { run.missing.push(id); continue; }
    await el.evaluate((e) => e.scrollIntoView({ block: 'center' })); await el.click(); await wait(250);
    await measure(id); if (!/^fold-/.test(id)) await shot(id);
    if (/^fold-/.test(id) || /^miss-only-/.test(id) || id === 'miss-more') { const e2 = await page.$(`[data-testid="${id}"]`); if (e2) { await e2.click(); await wait(200); } }
  }
  // The send buttons give the assistant a task through the person's add-task control.
  const sent = await page.evaluate(() => window.__calls.filter((c) => c[0] === 'addPlanTask').map((c) => c[1]));
  check(sent.every((t) => t.sourcePage === 'copilottest' && t.doneWhen === 'assistant_says' && /interview-model skill/.test(t.brief)), 'a press of Run round or New question bank adds a task for the assistant that names the skill');
  await measure('end'); await shot('end', true);
  if (state === 'done') { const h = await page.$('[data-testid=open-history]'); check(!!h, 'the changes card offers History'); }
  check(errors.length === 0, `no page or console error (${errors.length})`);
  check(run.missing.length === 0, 'no control went missing (' + run.missing.join(', ') + ')');
  check(run.sideways.length === 0, 'nothing spills sideways (' + run.sideways.join(', ') + ')');
  const bad = run.align.filter((a) => Object.entries(a).some(([k, v]) => k !== 'step' && k !== 'detail' && v));
  check(bad.length === 0, `every step sits on the grid (bad steps ${bad.length} of ${run.align.length})`);
  if (bad.length) console.error('  first bad: ' + JSON.stringify(bad[0]).slice(0, 700));
  run.errors = errors; results.runs.push(run);
  await ctx.close();
}
writeFileSync(join(OUT, 'drive.json'), JSON.stringify(results, null, 1));
await browser.close(); server.close();
console.log(failed ? `\n${failed} FAILED` : '\nall ok');
process.exit(failed ? 1 : 0);
