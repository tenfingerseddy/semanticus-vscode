// The Query lane from two real-user UATs of 1.5.104 (17 Oct 2026). The rules that decide what the page shows, run for real on the page's own
// modules, and the source facts that cannot run without a host. The engine half (the reading of "bought twice", a pinned row never rewritten,
// the check's flag) is Semanticus.Tests/AskDataTests.UatQuery.cs and QueryNotebookTests.cs.
//   P21   a reading the engine's check flags has no figure on the page.
//   P27   Compare tests the measure the query shows NOW.
//   F57   a one-row query of several columns leads with its first number, never "Blank".
//   F37   New query is an empty builder that runs nothing (so the next change can never land on a kept row).
//   P42b  the assistant's checks fold into one line; P29 + F58 a kept row says when it ran.
//   F26   an open answer does not lead with a row count; P23 Matrix is not Grid; P20a a trend over two dates is a line.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
globalThis.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return undefined; }, setState() {} });
globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => {};
globalThis.removeEventListener ??= () => {};
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const S = (f) => join(webview, 'src', f);
const A = load(S('askdata-model.ts'));
const C = load(S('compare-model.ts'));
const N = load(S('query-notebook-model.ts'));
const Q = load(S('query-model.ts'));
const W = load(S('wire.ts'));

// ── P21: a flagged reading has no headline figure ───────────────────────────────────────────────────
const answer = (extra = {}) => ({ outcome: 'answered', columns: [{ name: 'Order Count' }], rows: [[25669]], rowCount: 1, ...extra });
assert.equal(A.shownRowsOf(answer({ flagged: 'The question narrows the numbers in a way this reading does not show.' })), null, 'a flagged reading shows no rows, so no figure (25,669 was shown for "bought twice")');
assert.equal(A.shownRowsOf(answer())?.rows[0][0], 25669, 'a reading the check cleared still shows its rows (the happy path)');
assert.equal(A.shownRowsOf(answer({ outcome: 'refused' })), null, 'a refusal shows nothing');
assert.equal(A.unreadLine(answer({ flagged: 'The question narrows the numbers in a way this reading does not show.' })), 'The question narrows the numbers in a way this reading does not show. So no figure is shown.', 'one plain line says what it could not read');
assert.equal(A.unreadLine(answer()), null, 'no line when nothing was flagged');
const ask = src('queriesask.tsx');
assert.match(ask, /const rows = shownRowsOf\(a\);\s+onRows\(rows \? \{ \.\.\.rows, query: a\.dax \} : null, a\.dax\);/, 'the page gives the grid no rows for a flagged reading');
assert.match(ask, /unreadLine\(a\) && /, 'and says the line');
// the "2 or more" press asks again by its words (a question-only part survives cleanParts)
assert.deepEqual(A.cleanParts({ question: 'how many customers bought 2 or more times' }).question, 'how many customers bought 2 or more times', 'a reading made from words alone is asked again by its words');
assert.equal(A.cleanParts({ question: 'x', measures: ['Net Sales'] }).question, undefined, 'a question with parts is its parts');

// ── P27: Compare tests the measure the query shows now ──────────────────────────────────────────────
// The exact history: the page guessed [Average Order Value]; the person removed it and added Net Sales (also Brand and Year).
assert.equal(C.compareOriginal(['[Average Order Value]'], null, null).text, '[Average Order Value]', 'the first query shows the guess');
const afterRemove = C.compareOriginal(['[Net Sales]'], null, null);
assert.equal(afterRemove.text, '[Net Sales]', 'after Remove and Add the original is Net Sales, never the removed measure');
assert.equal(C.compareOriginal(['[Net Sales]'], '[Average Order Value]', { forRef: '[Average Order Value]', text: '[Average Order Value] * 1' }).text, '[Net Sales]', 'a pick or text kept for a removed measure is dropped');
assert.equal(C.compareOriginal(['[Net Sales]'], null, { forRef: '[Net Sales]', text: 'SUM ( Sales[NetSalesAmount] )' }).text, 'SUM ( Sales[NetSalesAmount] )', 'the person\'s own formula for the measure still shown is kept');
const two = C.compareOriginal(['[Net Sales]', '[Units]'], null, null);
assert.deepEqual(two.choices, ['[Net Sales]', '[Units]'], 'with two measures the person picks one');
assert.equal(C.compareOriginal(['[Net Sales]', '[Units]'], '[Units]', null).text, '[Units]', 'and the pick is the one compared');
assert.equal(C.compareOriginal([], null, null).text, '', 'no measure, nothing to compare');
const lab = src('daxlab.tsx');
assert.match(lab, /const exprA = compareSubject === 'dax' \? exprARaw : compareO\.text;/, 'Compare reads its original from the query as it is now');
assert.doesNotMatch(lab, /keep exprA pointed at the Show measure/, 'the old "set once, keep forever" effect is gone');
assert.match(src('query.tsx'), /compareChoices: q\.compareChoices, compareChosen: q\.compareChosen, onPickCompare: q\.pickCompare/, 'the Compare tab offers the pick');

// an answer the engine read from words alone (no chips) still owns its rows, so its statement and assumption show
assert.match(lab, /const wordsOnly = askOn && !chipsHere && !!ask\.answer && outcomeOf\(ask\.answer\) === 'answered' && !!ask\.answer\.statement/, 'a words-only answer owns the rows it made');
assert.match(src('query.tsx'), /\(q\.chipsHere \|\| q\.wordsOnly\) && q\.answerRows/, 'and the open answer says its statement');

// ── F57: the assistant's one-row check leads with its first number ──────────────────────────────────
const probe = { columns: ['Total 2024', 'All years', 'Blank brand 2024', 'Rows no date match', 'Rows no product match'].map((name) => ({ name })), rows: [[36111492.86, 75249457.11, null, null, null]], rowCount: 1, truncated: false, elapsedMs: 71 };
const fig = N.figureOf(probe, null);
assert.equal(fig.blank, false, 'a row whose first value is 36,111,492.86 is not "Blank"');
assert.equal(fig.big, '36,111,492.86', 'its first number leads');
assert.equal(fig.name, 'Total 2024', 'and says which number it is');
assert.deepEqual(N.figureOf({ ...probe, rows: [[null, null, null, null, null]] }, null).blank, true, 'a row with nothing in it says blank');
assert.equal(N.figureOf({ columns: [{ name: 'Net Sales' }], rows: [[5]], rowCount: 1 }, null).big, '5', 'one column, one value: unchanged');

// ── P42b: the assistant's checks fold ───────────────────────────────────────────────────────────────
const row = (id, dax, extra = {}) => ({ id, askedAt: '2026-10-17T10:00:00Z', kind: 'dax', origin: 'agent', dax, inNotebook: true, ...extra });
const answerRow = row('a', "EVALUATE SUMMARIZECOLUMNS('Product'[Brand], \"Net Sales\", [Net Sales])");
const checks = [row('c1', 'DEFINE MEASURE Sales[GM Check] = 1 EVALUATE ROW("x", [GM Check])'), row('c2', 'EVALUATE ROW("sa", SUM(Sales[Amount]), "sb", SUMX(VALUES(Sales[Brand]), SUM(Sales[Amount])))'), row('c3', "EVALUATE UNION(SELECTCOLUMNS(VALUES('Product'[Category]),\"k\",\"Category\"))")];
const person = row('p', 'EVALUATE ROW("v", 1)', { origin: 'human' });
assert.deepEqual(checks.map(N.isProbe), [true, true, true], 'a scratch measure, a cross-check of two values and a values list are checks');
assert.equal(N.isProbe(answerRow), false, 'a query that answers a question is not');
assert.equal(N.isProbe(row('one', 'EVALUATE ROW("Total2025", CALCULATE([Net Sales], \'Date\'[Year] = 2025))')), false, 'the assistant\'s one-number answer opens as an answer card (UAT 18 Oct)');
assert.equal(N.isProbe(person), false, 'a query the person wrote is never folded, whatever it looks like');
assert.equal(N.isProbe({ ...checks[0], pinned: true }), false, 'a pinned row is never folded');
const split = N.splitChecks([person, checks[0], answerRow, checks[1], checks[2]], null);
assert.deepEqual(split.rows.map((r) => r.id), ['p', 'a'], 'the answers stay in view');
assert.deepEqual(split.checks.map((r) => r.id), ['c1', 'c2', 'c3'], 'the checks fold into one line');
assert.deepEqual(N.splitChecks([checks[0], answerRow], 'c1').rows.map((r) => r.id), ['c1', 'a'], 'a check the person opened stays where it is');
assert.equal(N.checksLine(3), 'Your assistant checked 3 things');
assert.equal(N.checksLine(1), 'Your assistant checked 1 thing');
assert.equal(N.isProbeDax('EVALUATE TOPN(10, Sales)'), false);

// ── P29 + F58: a kept row says when it ran ──────────────────────────────────────────────────────────
const now = Date.parse('2026-10-17T10:09:00Z');
assert.deepEqual(N.keptLine({ id: 'k', askedAt: '2026-10-17T10:00:00Z', headline: '24 rows' }, now), { ran: true, text: 'Last run 9 min ago.' }, 'a row that ran keeps its number and says when');
assert.equal(N.keptLine({ id: 'k', askedAt: '2026-10-17T10:00:00Z', headline: '$1M' }, now).text.includes('Not run'), false, 'it never says "Not run yet"');
assert.deepEqual(N.keptLine({ id: 'k', askedAt: '2026-10-17T10:00:00Z' }, now), { ran: false, text: 'Not run yet.' }, 'only a row that never ran says so');
const page = src('query.tsx');
assert.doesNotMatch(page, /This is the number from the last run/, 'the old line is gone');

// ── F26 / P23 / P20a ────────────────────────────────────────────────────────────────────────────────
assert.equal(N.figureOf({ columns: [{ name: 'Brand' }, { name: 'Net Sales' }], rows: [['A', 1], ['B', 2]], rowCount: 2 }, null).count, true, 'a result of several rows says its count only as the folded row\'s line');
assert.match(page, /fig\.big && !fig\.count && <div className="qn-figure"/, 'the open answer leads with its chart, not a row count');
const cfg = (rows) => ({ rows, cols: [], values: [{ kind: 'measure', ref: '[Net Sales]', name: 'Net Sales' }], filters: [] });
const f = (name) => ({ kind: 'column', ref: `'T'[${name}]`, name });
assert.equal(Q.matrixConfigOf(cfg([f('Year')])), null, 'one split: Matrix would look like Grid, so it is not offered');
const mx = Q.matrixConfigOf(cfg([f('Year'), f('Month')]));
assert.deepEqual([mx.rows.map((x) => x.name), mx.cols.map((x) => x.name)], [['Year'], ['Month']], 'the last split goes across the top');
assert.equal(Q.matrixConfigOf({ ...cfg([f('Year'), f('Month')]), values: [{ kind: 'measure', ref: '[A]', name: 'A' }, { kind: 'measure', ref: '[B]', name: 'B' }] }), null, 'two numbers: the grid shows both, so no Matrix');
const dim = (name, isDate, distinct) => ({ name, isDate, ordered: true, distinct });
assert.equal(Q.queryVisualFits({ dims: [dim('Year', true, 2), dim('Month', true, 12)], measures: 1, rows: 24, sameUnit: true, negatives: false, measureNames: ['Net Sales'] }).pick, 'line', 'a number over a year and its months is a line, not 24 rows');
assert.deepEqual(Q.queryVisualFits({ dims: [dim('Year', true, 2), dim('Month', true, 12)], measures: 1, rows: 24, sameUnit: true, negatives: false, measureNames: ['Net Sales'] }).fits, ['line', 'bars', 'table'], 'and the switch offers the others');
assert.match(page, /data-testid="visual-switch"/, 'the visual switch sits beside the answer, not only under More');
assert.doesNotMatch(page.slice(page.indexOf('function MoreMenu'), page.indexOf('function VisualSwitch')), /Draw as/, 'More keeps the reason and Explain; the switch is beside the answer');

// ── F37 / P30 / F24: New query and Reset are an empty builder that runs nothing ─────────────────────
const nbSrc = src('query-notebook.tsx');
assert.match(nbSrc, /q\.askBox\.clear\(\); q\.askState\.setWords\(''\); q\.clearWells\(\); q\.chooseEntry\('build'\); beginDraft\(\);/, 'New query clears the fields before it opens its row');
assert.match(lab, /const resetDefault = clearWells;/, 'Reset is the same empty builder');
assert.doesNotMatch(lab, /setConfig\(seedConfig\(model\)\)/, 'the builder is never filled with a guess');
assert.match(lab, /if \(!config\.values\.length\) \{ runGen\.current\+\+; setRes\(null\)/, 'a Remove that leaves no number runs nothing');

// ── the page opens a builder when it is empty; the Free press goes to the Pro page ──────────────────
assert.match(page, /askGrant === 'denied' && nb\.ready && nb\.shown\.length === 0 && !nb\.activeId && !nb\.note\) nb\.newQuery\(\)/, 'the empty Query page opens a new query, so the fields and the DAX box are there');
assert.match(ask, /export function AskFreeLine\(\) \{[\s\S]*onClick=\{\(\) => upgradeToPro\(/, 'the Free "Ask in plain words" opens the Pro page like every other Pro press');

// ── v-batch1: Run runs exactly the DAX shown; DAX in the box runs as DAX ────────────────────────────
assert.match(lab, /const personQuery = \(\) => \(entry === 'build' && !chipsHere && !wordsOnly \? buildQuery\(config\) : query\);/, 'Run runs the editor\'s DAX unless the Fields are the way in');
assert.equal(Q.looksLikeDax('EVALUATE ROW("v", 1)'), true);
assert.equal(Q.looksLikeDax('  // my query\n  EVALUATE TOPN(5, Sales)'), true);
assert.equal(Q.looksLikeDax('DEFINE MEASURE Sales[x] = 1 EVALUATE ROW("x", [x])'), true);
assert.equal(Q.looksLikeDax('net sales by brand'), false);
assert.equal(Q.looksLikeDax('how many orders did we evaluate'), false);
assert.match(page, /if \(looksLikeDax\(text\)\) \{[\s\S]*?void q\.runQuery\(text\);\s+return;/, 'DAX typed in the question box is run as DAX, as its own row');

// ── F25 / F41 ───────────────────────────────────────────────────────────────────────────────────────
assert.equal(Q.fieldMatch('sales', 'Total net amount', 'The net sales amount after discounts.'), 1, 'the finder reads descriptions');
assert.equal(Q.fieldMatch('net', 'Total net amount', 'x'), 2, 'a name match ranks first');
assert.equal(Q.fieldMatch('cats', 'Total net amount', ''), 0);
assert.match(src('querywells.tsx'), /e\.key === 'Enter'[\s\S]*onPick\(top\)/, 'Enter in the finder adds the top match');
assert.match(src('fieldspanel.tsx'), /e\.key !== 'Enter'/, 'and in the fields list');

// ── speed: proportionate, with progress; a fast query is never traced ───────────────────────────────
assert.match(lab, /if \(c\?\.coldTotal && c\?\.warmTotal && isFast\(c, null\)\) \{ setTimings\(null\); return; \}/, 'a fast query skips the trace');
assert.match(lab, /setMeasureStep\('Timing the query a few times'\)[\s\S]*setMeasureStep\('Recording where the time went'\)/, 'the page says what it is doing while it measures');
assert.match(page, /data-testid="speed-progress"[\s\S]*\{seconds\} s/, 'and for how long');
assert.match(page, /\{!q\.emptyQuery && !measuring && !measured && r\.coldMs == null && r\.warmMs == null && <div className="sem-text-meta" data-testid="queries-speed-none">/, '"Not measured yet" is never said while it measures');
const nbModel2 = src('query-notebook.tsx');
assert.match(nbModel2, /rowOwnsResult\(row, q\.producedBy, q\.assistantShown\)\) setLiveRow\(row\.id\)/, 'a row that comes back open claims the rows it made, so its chart shows at once');

// ── round 2 (18 Oct): a row count has no number to open on, so a kept row runs and draws its chart; the chip finder takes Enter ──────────
assert.equal(N.isRowCount('24 rows'), true);
assert.equal(N.isRowCount('1,204 rows'), true);
assert.equal(N.isRowCount('$1.2M'), false);
assert.equal(N.isRowCount('Blank'), false);
assert.match(src('query-notebook.tsx'), /const keepsNumber = !!r\.headline && r\.outcome === 'answered' && !isRowCount\(r\.headline\);[\s\S]*if \(keepsNumber\) return;\n    runRow\(r\);/, 'a kept row of "24 rows" runs once on opening, so its chart is there');
assert.match(src('askdata.tsx'), /e\.key === 'Enter'[\s\S]*\[data-finder-list\] > button:not\(\[disabled\]\)'\)\?\.click\(\)/, 'Enter in the "+ Add" finder takes the top match');
assert.match(src('askdata.tsx'), /data-finder-list>\{items\}/, 'and only a match counts as the top one');

// ── Time all and wording ────────────────────────────────────────────────────────────────────────────
assert.equal(N.timingNote({ done: 3, failed: 0, total: 3, repeatOnly: 0, why: null, slowest: { text: 'sales trnd by month', ms: 128 } }), 'Timed 3 of 3. Slowest: sales trnd by month, 128 ms.', 'Time all ends with one summary line');
assert.equal(N.timingNote({ done: 1, failed: 0, total: 1, repeatOnly: 0, why: null }), 'Timed 1 of 1.');
assert.equal(W.timingAnnouncement(6374.2), '6.4 s', 'seconds are written as seconds, like the row header');
assert.equal(W.timingAnnouncement(79), '79 ms');
const all = page + nbSrc + ask + src('compare-model.ts') + N && (page + nbSrc + ask + lab);
assert.doesNotMatch(all, /—/, 'no em dash in the copy');

// ── round 2 of 1.5.105 (18 Oct): the lane fix-r2-query ──────────────────────────────────────────────
// N6 wrong-number risk: "year" + Enter took fiscal_year; the exact name "year" was 4th. Exact first, then starts-with, then the rest.
{
  const names = ['fiscal_year', 'Fiscal Year Quarter', 'Year Month', 'Yearly Target', 'year', 'Calendar Year'].map((name) => ({ name }));
  assert.deepEqual(Q.rankFields('year', names).map((x) => x.name), ['year', 'Year Month', 'Yearly Target', 'fiscal_year', 'Fiscal Year Quarter', 'Calendar Year'], 'exact name first, then starts-with, then the rest in model order');
  assert.equal(Q.rankFields(' YEAR ', names)[0].name, 'year', 'case and spaces are folded');
  assert.equal(Q.rankFields('year', [{ name: 'Fiscal Year' }, { name: 'Year' }])[0].name, 'Year', 'a name with a space folds too');
  // N7: one letter off finds a name of 4 or more letters; nothing shorter guesses.
  assert.equal(Q.rankFields('brnd', [{ name: 'Brand' }, { name: 'Region' }]).map((x) => x.name).join(), 'Brand', '"brnd" finds Brand');
  assert.equal(Q.rankFields('yea', [{ name: 'Brand' }]).length, 0, 'three letters never guess');
  assert.equal(Q.rankFields('qty', [{ name: 'Qyy' }]).length, 0, 'a name under 4 letters is not fuzzy matched');
  assert.equal(Q.fieldMatch('year', 'year', null), 2, 'fieldMatch still says a name match is 2');
}
const fp = src('fieldspanel.tsx');
assert.match(fp, /rankFields\(/, 'the fields list ranks its matches');
assert.doesNotMatch(fp, /const g = tables\[0\]; if \(!g\) return;\n\s+if \(g\.measures\[0\]\)/, 'Enter no longer takes the first measure of the first table');
assert.match(src('querywells.tsx'), /rankFields\(/, 'the add menu ranks too');

// N3 lost work: a failed or unsupported per-measure log never replaces the result, so the recorded plan stays; the button is not offered where it cannot work.
assert.equal(Q.logRunUsable({ error: 'boom', entries: [] }), false, 'a failed log is not used');
assert.equal(Q.logRunUsable({ entries: [] , note: 'unsupported' }), false, 'a log with no steps is not used');
assert.equal(Q.logRunUsable({ entries: [{ label: 'a' }] }), true, 'a log with steps is used (the happy path)');
assert.match(src('daxlab.tsx'), /if \(!logRunUsable\(r\)\)[\s\S]*?return;/, 'debug() leaves the result and the plan alone when the log gave no steps');
assert.match(src('query.tsx'), /\{!q\.shared && <button[^\n]*data-testid="log-measures"/, 'the log button is not offered on a server that cannot send steps');

// Worst 4 (F26 still open): a measure split by two fields leads with a chart.
{
  const col = (name, format) => ({ name, format });
  const res = { columns: [col('Product[Brand]'), col('Date[Year]'), col('[Net Sales]')], rows: [], rowCount: 0, elapsedMs: 5 };
  for (const b of ['Contoso', 'Fabrikam', 'Litware', 'Adventure']) for (const y of [2022, 2023, 2024]) res.rows.push([b, y, 1000 + b.length * 10 + (y - 2020)]);
  res.rowCount = res.rows.length;
  const cfg = { rows: [{ kind: 'column', ref: "'Product'[Brand]", name: 'Brand' }, { kind: 'column', ref: "'Date'[Year]", name: 'Year' }], cols: [], values: [{ kind: 'measure', ref: '[Net Sales]', name: 'Net Sales' }], filters: [] };
  const shape = { dims: [{ name: 'Brand', isDate: false, distinct: 4 }, { name: 'Year', isDate: false, distinct: 3 }], measures: 1, rows: 12, sameUnit: true };
  const fits = Q.queryVisualFits(shape);
  assert.equal(fits.pick, 'groups', 'brand by year leads with grouped bars');
  assert.deepEqual(fits.fits, ['groups', 'table'], 'and the table is one press away');
  const d = Q.visualData('groups', res, cfg);
  assert.ok(d, 'the grouped bars have data');
  assert.equal(d.rows.length, 4, 'one cluster for each brand');
  assert.deepEqual(d.periods, ['2022', '2023', '2024'], 'one series for each year');
  assert.equal(d.rows[0].series.length, 3);
  // The same numbers with the year across the top (a pivot) draw the same.
  const pivotCfg = { ...cfg, rows: [cfg.rows[0]], cols: [cfg.rows[1]] };
  const pd = Q.visualData('groups', res, pivotCfg);
  assert.ok(pd && pd.rows.length === 4 && pd.periods.length === 3, 'a pivot draws the same bars');
  assert.ok(Q.visualData('table', res, pivotCfg), 'a pivot has a table of its own: the Answer never says "shown as rows in the Table view" with nothing under it');
  // Too many series to tell apart: no chart claims them.
  const wide = { dims: [{ name: 'Brand', isDate: false, distinct: 15 }, { name: 'Colour', isDate: false, distinct: 20 }], measures: 1, rows: 80 };
  assert.equal(Q.queryVisualFits(wide).fits.includes('groups'), false, 'fifteen series are not drawn as bars');
}
assert.match(src('query.tsx'), /This result is shown as rows in the Table view/, 'the old line may remain only as the last resort');

// N11: Compare says the numbers agree when the only caution is a calculation group neither formula touches.
{
  const note = 'The model contains calculation groups: the candidates run under query-scoped names, so identity-sensitive functions (ISSELECTEDMEASURE / SELECTEDMEASURENAME) do not see the deployed measure\'s name.';
  const key = (a, b) => JSON.stringify([a, b, ['Brand'], [], 5000]);
  assert.equal(C.calcCautionApplies(note, key('SUM ( Sales[NetSalesAmount] )', '[Net Sales]')), false, 'neither formula uses a calculation-group function: no caution');
  assert.equal(C.calcCautionApplies(note, key('SELECTEDMEASURENAME()', '[Net Sales]')), true, 'a formula that reads the measure name keeps the caution');
  assert.equal(C.calcCautionApplies(note, null), true, 'with no formulas to read, the caution stays');
  assert.equal(C.calcCautionApplies('Evaluated INLINE, not as a deployed measure: ...', key('SUM ( Sales[Amt] )', '[Net Sales]')), true, 'a different caution is not removed');
  assert.match(C.calcCautionLine, /^[^\n]{10,140}$/, 'the caution is one plain line');
}
assert.match(src('daxlab.tsx'), /calcCautionApplies\(eq\.fidelity, ev\.requestKey\)/, 'the verdict asks whether the caution applies');

// N4 + N5: the DAX editor starts empty and a DAX error is one plain line.
{
  const E = load(S('daxErrorText.ts'));
  const tech = ' Technical Details: RootActivityId: 19c02fcb-bad6-49d0-b002-f535685bb163 Date (UTC): 10/18/2026 12:50:29 AM';
  const typo = E.plainQueryError("Query (1, 101) The value for 'Gross Proft' cannot be determined. Either the column doesn't exist, or there is no current row for this column." + tech, ['Net Sales', 'Gross Profit', 'Order Count']);
  assert.equal(typo, 'The model has nothing called "Gross Proft". Did you mean [Gross Profit]?', 'a near-miss name gets the suggestion');
  assert.doesNotMatch(typo, /RootActivityId|Technical Details|Query \(1/, 'no server text');
  assert.equal(E.plainQueryError("Query (1, 101) The value for 'Zzz' cannot be determined. Either the column doesn't exist." + tech, ['Net Sales']), 'The model has nothing called "Zzz".', 'no guess when nothing is close');
  const syn = E.plainQueryError("Query (2, 45) The syntax for 'top' is incorrect. (EVALUATE ROW(\"(drag fields into the wells)\", BLANK())top 10 prodcts by margin 2024)." + tech);
  assert.equal(syn, 'DAX does not understand "top" here.', 'a syntax error names the word and nothing else');
  assert.equal(E.plainQueryError('A plain message with <oii>tags</oii>.'), 'A plain message with tags.', 'the old tag strip stays');
  assert.match(E.plainDaxError('x RootActivityId: 1 <oii>y</oii>'), /RootActivityId: 1 y/, 'the Ask page keeps the raw text for its details view');
  assert.equal(E.plainQueryError(null), '');
}
assert.doesNotMatch(src('daxlab.tsx'), /drag fields into the wells/, 'the editor is never filled with a placeholder query');
assert.match(src('daxlab.tsx'), /'lab\.bq\.' \+ persistKey, ''\)/, 'a new editor starts empty');
assert.match(src('query.tsx'), /plainQueryError\(q\.err, q\.fieldNames\)/, 'the page gives the error the names in the model');

// Pro round 2 (18 Oct), N1 N2 N3: "Fields" on an asked question must show that question's fields, or say it cannot and leave the query alone.
// It used to show empty wells and swap the query for a placeholder row, so the Answer read "Blank. 1 row" and Speed and Steps went silent.
{
  const ok = A.wellsOfQuestion({ groupBy: ["'Product'[Brand]", "'Date'[Year]"], measures: ['Net Sales'] });
  assert.ok(ok.config, 'a question of plain fields shows as fields');
  assert.deepEqual(ok.config.rows.map((f) => f.name), ['Brand', 'Year']);
  assert.deepEqual(ok.config.values.map((f) => f.name), ['Net Sales']);
  const filtered = A.wellsOfQuestion({ groupBy: ["'Product'[Brand]"], measures: ['Net Sales'], filters: [{ column: "'Date'[Year]", op: '=', values: ['2024'] }] });
  assert.equal(filtered.config, null, 'a filter the wells cannot hold stays a question: showing it as fields would drop the filter and change the number');
  assert.match(filtered.why, /filter/, 'and says why in plain words');
  assert.equal(A.wellsOfQuestion({ groupBy: ["'Product'[Brand]"], measures: ['Net Sales'], top: 10 }).config, null, 'a top 10 stays a question too');
  assert.match(A.wellsOfQuestion({ groupBy: [], measures: ['Net Sales'], top: 10 }).why, /top/);
  const D = load(S('daxwells.tsx'));
  assert.equal(D.buildQuery({ rows: [], cols: [], values: [], filters: [] }), '', 'nothing chosen is no query, never a placeholder row');
  assert.equal(D.buildInstrumentedQuery({ rows: [], cols: [], values: [], filters: [] }), '');
}
{
  const lab = src('daxlab.tsx'), page = src('query.tsx');
  assert.match(lab, /wellsOfQuestion\(/, 'chooseEntry asks whether the question can be shown as fields');
  assert.match(lab, /setFieldsNote\(/, 'and says so when it cannot');
  assert.match(lab, /const emptyQuery = !currentQuery\(\)\.trim\(\)/, 'the page knows when there is nothing to measure');
  assert.match(page, /q\.emptyQuery[\s\S]{0,300}data-testid="speed-nothing"/, 'Speed says why there is nothing to measure');
  assert.match(page, /q\.emptyQuery[\s\S]{0,300}data-testid="steps-nothing"/, 'Steps says it too');
  assert.match(page, /data-testid="fields-note"/, 'the note sits beside the switch');
  assert.match(lab, /Nothing to run yet/, 'Run on an empty editor says so instead of sending an empty query');
}

// Pro N6: the yellow dot's words say what happened. "Counting customers with 2 or more orders" was applied, so it is never "Not applied".
assert.equal(N.markLabel('Counting customers with 2 or more orders.'), 'Assumed: Counting customers with 2 or more orders.');
assert.equal(N.markLabel('Split by ProductName.'), 'Assumed: Split by ProductName.');
assert.equal(N.markLabel('"premium" is not in the model, so it was not used.'), 'Not applied: "premium" is not in the model, so it was not used.');
assert.equal(N.markLabel('Year is not filtered.'), 'Not applied: Year is not filtered.');
assert.match(src('query-notebook-parts.tsx'), /aria-label=\{markLabel\(r\.dropped\)\}/, 'the mark reads its label from markLabel');

// Pro N5: a real difference says so plainly, with numbers a person can read.
{
  assert.equal(C.differenceLine(50, 51), 'Different in 50 of 51 combinations. Do not apply.');
  assert.equal(C.differenceLine(1, 1), 'Different in the only combination. Do not apply.');
  assert.equal(C.differenceLine(44, 30), 'Different in 44 combinations. Do not apply.', 'never "44 of 30" (live, 1.5.106)');
  assert.equal(C.shownNumber('0.4254108545201077'), '0.4254', 'sixteen decimals are rounded for reading');
  assert.equal(C.shownNumber('12345678.5'), '12,345,678.5');
  assert.equal(C.shownNumber('Trail Tent'), 'Trail Tent', 'a word is left as it is');
  // R10 (UAT 18 Oct, round 3): a percent measure reads as a percent in the Compare table, with the places its format has.
  assert.equal(C.shownNumber('0.3583', '0.0%'), '35.8%');
  assert.equal(C.shownNumber('0.239', '0.0%'), '23.9%');
  assert.equal(C.shownNumber('0.239', '0%'), '24%');
  assert.equal(C.shownNumber('1234.5', '#,0'), '1,234.5', 'a format with no percent changes nothing');
  const fmts = { 'Margin %': '0.0%;-0.0%;0.0%' };
  assert.equal(C.rewriteFormat('DIVIDE([Gross Profit], [Net Sales])', '0.0%', (n) => fmts[n]), '0.0%', 'a ratio rewrite reads as the original does (0.239 is 23.9%)');
  assert.equal(C.rewriteFormat('SUM(Sales[NetSalesAmount])', '0.0%', (n) => fmts[n]), undefined, 'an amount is never read as a percent');
  assert.equal(C.rewriteFormat('[Margin %]', '#,0', (n) => fmts[n]), '0.0%;-0.0%;0.0%', 'a bare measure reads in its own format');
  assert.equal(C.rewriteFormat('[Net Sales]', '0.0%', (n) => fmts[n]), undefined);
  assert.equal(C.plainCombination('Product[ProductName]=Trail Tent'), 'Product name: Trail Tent');
  assert.equal(C.plainCombination("'Date'[Year]=2024, Product[Brand]=Contoso"), 'Year: 2024, Brand: Contoso');
  assert.equal(C.plainCombination('(grand total)'), '(grand total)');
}
assert.match(src('daxlab.tsx'), /differenceLine\(eq\.mismatchCount, eq\.rowsCompared\)/, 'the verdict uses the plain line');
assert.match(src('query-notebook.tsx'), /noteFor\(row, figureOf\(q\.shownRes \?\? q\.res, q\.chartConfig\), dropped, q\.speedMs \?\? null/, 'the row header keeps the one time the Speed verdict uses (live, 1.5.106)');
assert.match(src('daxlab.tsx'), /shownNumber\(m\.valueB, rewrittenFormat\)/, 'and the rewrite reads in the format that fits it');
assert.match(src('daxlab.tsx'), /shownNumber\(m\.valueA, comparedFormat\)/, 'and the table shows readable numbers in the measure\'s own format');
assert.match(src('daxlab.tsx'), /\.\.\.\(measureFormats \?\? \[\]\), \.\.\.model\.measures\][\s\S]*subject === 'dax' \? \(exprA \?\? ''\)\.trim\(\) : compareChosen/, 'R10: an Original box that is just a measure still finds its format, from the formats the run was proven with first');
assert.match(src('query.tsx'), /measureFormats: q\.formatMeasures/, 'R10: the page hands those formats to Compare');

// N12: one time number everywhere (the one the verdict uses), a verdict that stays, and plain words in the detail.
{
  const P = load(S('query-parts.tsx'));
  const EW = load(S('enginewords.ts'));
  const st = (runsMs, avgMs) => ({ n: runsMs.length, avgMs, stdDevMs: 5, minMs: Math.min(...runsMs), maxMs: Math.max(...runsMs), runsMs });
  // the UAT run: repeat runs 62 to 150 ms, median 68 ms, average 94 ms; the first run 74 ms
  const cw = { runs: 5, cacheClearAvailable: false, traceAvailable: false, coldTotal: st([], 0), warmTotal: st([62, 64, 68, 70, 150], 82.8), coldSe: st([], 0), warmSe: st([], 0), detail: [], rowCount: 6 };
  assert.equal(P.waitMs(cw, null, null), 68, 'the one number is the middle repeat run, the one the big figure shows');
  assert.equal(P.speedInsight(cw, null, null), 'Fast: 68 ms. Nothing to fix.', 'the verdict quotes that same number');
  const slow = { ...cw, warmTotal: st([240, 250, 260], 250) };
  assert.match(P.speedInsight(slow, { totalMs: 0, feMs: 0, seMs: 0, seCpuMs: 0, seQueries: 0, seParallelism: 1, rowCount: 1, scans: [], traceAvailable: true }, null), /^[^\n]+$/, 'a slow query with no trace still gets a verdict line');
  assert.match(P.speedInsight(slow, null, null), /260 ms|250 ms/, 'and it names the time');
  assert.doesNotMatch(EW.plainEngineNote('Cold runs skipped: needs confirm=true. Showing warm-only. Storage engine 0 ms means the server reported no storage work (cached or the trace missed it), not a measured round trip.'), /storage engine|trace missed|formula engine/i, 'no engine words in the note');
  assert.match(EW.plainEngineNote('Cold runs skipped: needs confirm=true.'), /Cold runs were skipped/, 'the cold runs line stays');
}
{
  const page = src('query.tsx');
  assert.doesNotMatch(page, /storage engine|formula engine/i, 'the page says reading data and calculating');
  assert.doesNotMatch(src('query-parts.tsx'), /\(storage engine\)|\(formula engine\)/, 'and so do the bars');
  assert.match(src('daxlab.tsx'), /const measuredMs = waitMs\(/, 'the header time is the same number');
}

// Free round 2, wording and friction (N19, N20, N21) and pro N7, N8, N10.
assert.equal(N.rowText({ id: 'a', kind: 'dax', dax: "EVALUATE\nSUMMARIZECOLUMNS(\n    'Product'[Brand],\n    'Date'[Year],\n    \"Net Sales\", [Net Sales]\n)" }), 'Net Sales by Brand and Year', 'a row built from fields is named from them');
assert.equal(N.rowText({ id: 'a', kind: 'dax', dax: "EVALUATE\nCALCULATETABLE(\n    SUMMARIZECOLUMNS(\n        'Date'[Year],\n        \"Net Sales\", [Net Sales],\n        \"Units\", [Units],\n        \"Cost\", [Cost]\n    ),\n    'Date'[Year] = 2024\n)" }), 'Net Sales, Units and 1 more by Year', 'several measures, a filter around it');
assert.equal(N.rowText({ id: 'a', kind: 'dax', dax: 'EVALUATE ROW("x", 1)' }), 'EVALUATE ROW("x", 1)', 'a hand-written query keeps its own text');
assert.equal(N.rowText({ id: 'a', kind: 'dax', dax: "EVALUATE SUMMARIZECOLUMNS('Date'[Year])" }), "EVALUATE SUMMARIZECOLUMNS('Date'[Year])", 'no measure, no name');
{
  const page = src('query.tsx');
  assert.doesNotMatch(page, /Open DAX to choose them/, 'N20: the empty Answer points to where the fields are');
  assert.match(page, /No fields chosen yet\. Choose them under DAX, in Fields\./);
  assert.match(src('query.css'), /\.qv-grid-wrap \{[^}]*--qv-top/, 'N21: the table uses the page height');
  assert.match(page, /setProperty\('--qv-top'/);
  assert.doesNotMatch(page, /Capture calculation steps/, 'N10: one name for the steps control');
  assert.match(page, /useEffect\(\(\) => \(\) => q\.setClearRefusal\(null\)/, 'N7: a refusal from another row is not carried over');
  assert.match(page, /checked=\{q\.clearOnRun && \(!q\.shared \|\| q\.confirmShared\)\}/, 'N8: nothing is ticked that the service refuses');
  assert.doesNotMatch(src('query-notebook-model.ts'), /Shared endpoint ticked/, 'the Time all line has no jargon');
}

// Item 65: a line's axis labels get the room their own text needs.
{
  const V = load(S('visuals/visuals.tsx'));
  const labels = [];
  for (const y of [2024, 2025]) for (const m of ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']) labels.push(`${y} / ${m}`);
  const d = { rows: labels.map((label, i) => ({ label, v: 100 + i })), vLabel: 'Net Sales', fmt: 'int' };
  const { renderToStaticMarkup } = req('react-dom/server');
  const x = (el) => renderToStaticMarkup(el);
  const shown = (text) => (text.match(/\d{4} \/ [A-Za-z]+/g) || []).length;
  assert.ok(shown(x(V.drawVisual('line', d, 960))) < 24, '24 long month labels are not all drawn at 960 px');
  assert.ok(shown(x(V.drawVisual('line', d, 960))) <= Math.floor((960 - 60) / (14 + 6.2 * 14)) + 1, 'and the ones drawn fit their own width');
}

// Pro N9: the Table tab reads months in time order.
{
  const T = load(S('tabulate.ts'));
  const months = ['February', 'June', 'August', 'November', 'September', 'January', 'March'];
  const res = { columns: [{ name: 'Date[MonthName]' }, { name: '[Net Sales]' }], rows: months.map((m, i) => [m, 100 + i]), rowCount: months.length, elapsedMs: 3 };
  const cfg = { rows: [{ kind: 'column', ref: "'Date'[MonthName]", name: 'MonthName' }], cols: [], values: [{ kind: 'measure', ref: '[Net Sales]', name: 'Net Sales' }], filters: [] };
  assert.deepEqual(T.gridRowsInTimeOrder(res, cfg).map((r) => r[0]), ['January', 'February', 'March', 'June', 'August', 'September', 'November'], 'months run January to December');
  assert.equal(T.gridRowsInTimeOrder(res, null), res.rows, 'with no fields the rows stay as they came');
}
assert.match(src('query.tsx'), /gridRowsInTimeOrder\(/, 'the Table tab uses it');
// Fix round (18 Oct, round 2): the wells, the Answer's labels, the DAX tab, Speed and Steps follow the CURRENT row's own query.
{
  const lab = src('daxlab.tsx'), page = src('query.tsx');
  assert.match(lab, /if \(chipsHere \|\| wordsOnly\) \{\n\s+const shown = wellsOfQuestion\(/, 'Fields reads the question on screen, not only when the wells are empty');
  assert.match(lab, /entry === 'build' && !chipsHere && !wordsOnly \? buildQuery\(config\)/, 'a question worked out from words runs its own query, not the wells\'');
  assert.match(lab, /function switchMode\(m: Mode\) \{ if \(m === 'query' && mode === 'visual' && !chipsHere && !wordsOnly\)/, 'the DAX switch never swaps in the wells\' query over a question');
  assert.match(page, /const wellsShown = q\.entry === 'build' && !q\.assistantShown && !holds;/, 'the wells are hidden while a question holds the query');
  assert.match(page, /q\.entry === 'build' && !q\.wordsOnly \? readAsOfWells\(q\.config\)/, 'the Answer never reads another row\'s wells out under a question');
  assert.match(A.wellsOfQuestion(null).why, /worked out from its words/, 'a question the fields cannot show says so in one line');
}
{
  const E = load(S('enginewords.ts'));
  const warm = 'Warm-only: the cache was not cleared (cold runs skipped). Storage engine 0 ms means the server reported no storage work (cached or the trace missed it), not a measured round trip.';
  assert.equal(E.plainEngineNote(warm), 'The cache was not cleared, so these are repeat runs only.', 'an all-jargon note becomes one plain line, never the raw note');
  assert.equal(E.plainEngineNote('Storage engine time was not reported.'), 'The server gave no more detail about this run.');
  assert.equal(E.plainEngineNote('The server is busy.'), 'The server is busy.', 'a plain note is left as the engine wrote it');
}
// Pro UAT 18 Oct, round 3. P7: Fields on an asked question refreshes that row and adds none. P5: Compare belongs to the row it was made on.
{
  const lab = src('daxlab.tsx'), nb = src('query-notebook.tsx');
  assert.match(lab, /quietFieldsRun\.current = true; setConfig\(shown\.config\)/, 'P7: Fields on a question marks the run that follows as quiet');
  assert.match(lab, /const quiet = quietFieldsRun\.current; quietFieldsRun\.current = false;[\s\S]*runQuery\(q, !quiet\)/, 'P7: and the wells run that follows is told not to record');
  assert.match(lab, /async function runQuery\(q: string, record = true\)/);
  assert.match(lab, /if \(record\) void rpc<string \| null>\('recordQueryRun'/, 'P7: a quiet run records no row');
  assert.match(lab, /function moveCompareTo\(next: string \| null, adopt = false\)/, 'P5: Compare moves with the open row');
  assert.match(lab, /setExprARaw\(s\?\.exprARaw \?\? ''\); setExprB\(s\?\.exprB \?\? ''\); setEqEv\(s\?\.eqEv \?\? null\)/, 'P5: a row never opened here starts blank, with no rewrite and no verdict');
  assert.match(nb, /useEffect\(\(\) => \{ q\.moveCompareTo\(activeId && activeId !== DRAFT \? activeId : null\); \}, \[activeId\]\)/, 'P5: the notebook moves it when the open row changes');
  assert.match(nb, /if \(!q\.activeRow\.current\) q\.moveCompareTo\(id, true\)/, 'P5: a draft recorded as a row takes its Compare with it');
}
console.log('uat-query: the flagged reading, Compare, the assistant\'s checks, the kept row, the empty builder and the finder passed');
