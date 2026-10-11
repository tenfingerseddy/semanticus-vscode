// UAT N1 round 2 (Sol H8, "an order shown as time order that nobody established"). The verifier's finding on 6626af92:
// the Queries caption "Dates run left to right" came from a field's NAME, not from the order the axis was drawn in.
// Three things close it, and each has its own block below:
//   (a) a field with a sort-by column in the model brings that column in the query, orders by it, and the drawn axis
//       follows it (a fiscal "Fiscal Month" runs Jul to Jun, not Jan to Dec)
//   (b) with no sort-by, labels that say what time they are (Mar-24, Jan 24, 03/2024, FY25 P3) are read
//   (c) the time caption, and the line it goes with, show only when (a) or (b) really set the order
// Each block failed on d05fd4cc and passes with the fix.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { periodKey, periodKeys } from '../webview/src/periodorder.mjs';
import { chartRulePick } from '../webview/src/chartpick.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
globalThis.document = { documentElement: { style: {} } };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });

const charts = [];
const load = makeLoader({
  './echart': { EChart: (p) => { charts.push(p.option); return React.createElement('div'); }, tokenPx: () => 11 },
  echarts: {},
});
const { DaxVisual } = load('daxvisual.tsx');
const { foldSortColumns, axisDims } = load('axisorder.ts');
const { buildQuery, buildInstrumentedQuery, withSortBy } = load('daxwells.tsx');

const frame = (names, rows) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1 });
const measure = { kind: 'measure', ref: '[Sales]', name: 'Sales' };
const col = (table, name, sortBy) => ({ kind: 'column', ref: `'${table}'[${name}]`, name, table, ...(sortBy ? { sortBy: { ref: `'${table}'[${sortBy}]`, name: sortBy } } : {}) });
const wells = (rows, cols = []) => ({ rows, cols, values: [measure], filters: [] });
const axisOf = (res, config, viz = 'line', across = false) => {
  charts.length = 0;
  renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz, height: 300, across }));
  return charts.at(-1)?.xAxis?.data ?? null;
};
const headersOf = (res, config) => [...renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz: 'matrix', height: 300 })).matchAll(/<th[^>]*>(.*?)<\/th>/g)].map((m) => m[1].replace(/<[^>]+>/g, ''));

const FISCAL = ['Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'];
// the engine's rows in an order of its own, each month with its FiscalMonthNo (Jul = 1 ... Jun = 12)
const scrambled = ['Mar', 'Jan', 'Jul', 'Oct', 'Jun', 'Aug', 'Dec', 'Feb', 'Sep', 'Nov', 'Apr', 'May'];
const fiscalRows = scrambled.map((m) => [m, FISCAL.indexOf(m) + 1, 100 + FISCAL.indexOf(m)]);

// ── (a) the model's sort-by column ───────────────────────────────────────────────────────────────────────────────
test('N1a the built query brings the sort-by column and orders by it, and only for a field that has one', () => {
  const q = buildQuery(wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]));
  assert.match(q, /SUMMARIZECOLUMNS\(\s+'Date'\[Fiscal Month\],\s+'Date'\[FiscalMonthNo\],\s+"Sales", \[Sales\]\s+\)/, q);
  assert.match(q, /\nORDER BY 'Date'\[FiscalMonthNo\]$/, q);
  assert.equal(buildQuery(wells([col('Date', 'Fiscal Month')])), 'EVALUATE\nSUMMARIZECOLUMNS(\n    \'Date\'[Fiscal Month],\n    "Sales", [Sales]\n)', 'no sort-by: the query is what it was');
  const filtered = { ...wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]), filters: [{ ref: "'Date'[Year]", name: 'Year', family: 'number', filter: { kind: 'number', op: 'eq', a: 2025 } }] };
  assert.match(buildQuery(filtered), /^EVALUATE\nCALCULATETABLE\(/);
  assert.match(buildQuery(filtered), /\)\nORDER BY 'Date'\[FiscalMonthNo\]$/, 'the order sits outside the filter wrapper');
  assert.match(buildInstrumentedQuery(wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')])), /ORDER BY 'Date'\[FiscalMonthNo\]$/, 'the logged run orders the same way');
});

test('N1a a sort-by column that is already a well field is not added twice', () => {
  const q = buildQuery(wells([col('Date', 'Fiscal Month', 'FiscalMonthNo'), col('Date', 'FiscalMonthNo')]));
  assert.equal((q.match(/'Date'\[FiscalMonthNo\]/g) ?? []).length, 2, q);   // once as the field, once in ORDER BY
});

test('N1a withSortBy reads each field\'s sort-by from the model, and leaves a field alone when the model does not say', () => {
  const model = { tables: ['Date'], measures: [], functions: [], columns: [{ table: 'Date', name: 'Fiscal Month', sortByColumn: 'FiscalMonthNo' }, { table: 'Date', name: 'FiscalMonthNo' }, { table: 'Date', name: 'Year' }] };
  const out = withSortBy(wells([col('Date', 'Fiscal Month'), col('Date', 'Year')]), model);
  assert.deepEqual(out.rows[0].sortBy, { ref: "'Date'[FiscalMonthNo]", name: 'FiscalMonthNo' });
  assert.equal(out.rows[1].sortBy, undefined);
  assert.equal(withSortBy(wells([col('Date', 'Fiscal Month')]), { tables: [], measures: [], columns: [], functions: [] }).rows[0].sortBy, undefined, 'a model not loaded yet says nothing');
});

test('N1a the result folds back to the wells\' own layout: the sort-by column leaves, its order stays', () => {
  const config = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const res = frame(['Date[Fiscal Month]', 'Date[FiscalMonthNo]', 'Sales'], fiscalRows);
  const f = foldSortColumns(res, config);
  assert.deepEqual(f.columns.map((c) => c.name), ['Date[Fiscal Month]', 'Sales']);
  assert.deepEqual(f.rows[0], ['Mar', 108]);
  assert.equal(foldSortColumns(frame(['Fiscal Month', 'Sales'], [['Jul', 1]]), config).columns.length, 2, 'a result without the column is left as it is');
});

test('N1a a fiscal month field runs Jul to Jun on the chart and in the matrix, not Jan to Dec', () => {
  const config = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const res = foldSortColumns(frame(['Fiscal Month', 'FiscalMonthNo', 'Sales'], fiscalRows), config);
  assert.deepEqual(axisOf(res, config), FISCAL);
  const across = wells([col('Product', 'Category')], [col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const acrossRes = foldSortColumns(frame(['Category', 'Fiscal Month', 'FiscalMonthNo', 'Sales'],
    ['Bikes', 'Clothing'].flatMap((c, ci) => scrambled.slice(ci * 3).concat(scrambled.slice(0, ci * 3)).map((m) => [c, m, FISCAL.indexOf(m) + 1, 5]))), across);
  assert.deepEqual(headersOf(acrossRes, across).slice(1), FISCAL, 'the matrix columns follow the sort-by even where the first row met them in another order');
  assert.deepEqual(axisDims(res, config).map((d) => d.ordered), [true], 'and the model\'s order counts as established');
});

test('N1a a sort-by the result does not carry is never replaced by reading the labels', () => {
  // the person's own typed query, or an assistant's, returns the months without their number: Jul..Jun must not become Jan..Dec
  const config = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const res = frame(['Fiscal Month', 'Sales'], scrambled.map((m, i) => [m, i]));
  assert.deepEqual(axisOf(res, config), scrambled, 'the engine\'s order, no invented one');
  assert.deepEqual(axisDims(res, config).map((d) => d.ordered), [false], 'and nothing claims it is time order');
});

// ── (b) labels that say what time they are ───────────────────────────────────────────────────────────────────────
test('N1b the common month and fiscal label shapes read as time', () => {
  const key = (s) => periodKey(s, false);
  assert.ok(key('Feb-24') < key('Mar-24') && key('Mar-24') < key('Sep-24') && key('Sep-24') < key('Aug-25'), 'Mar-24');
  assert.equal(key('Mar-24'), key('Mar 2024'), 'a two-digit year reads as that year');
  assert.ok(key('Jan 24') < key('Feb 24') && key('Dec 24') < key('Jan 25'), 'Jan 24');
  assert.ok(key("Dec'24") < key("Jan'25"), "Dec'24");
  assert.ok(key('12/2023') < key('01/2024') && key('01/2024') < key('03/2024') && key('03/2024') < key('11/2024'), '03/2024');
  assert.ok(key('3/2024') < key('12/2024'), '3/2024 without a zero');
  assert.ok(key('FY24 P12') < key('FY25 P1') && key('FY25 P1') < key('FY25 P3') && key('FY25 P3') < key('FY25 P10'), 'FY25 P3 (P10 after P3, not before)');
  assert.equal(key('FY25 P3'), key('FY2025 P03'));
  assert.ok(key('FY25 Q2') < key('FY25 Q3') && key('FY24 Q4') < key('FY25 Q1'), 'FY25 Q3');
  assert.ok(key('FY24') < key('FY25'), 'FY25');
  assert.equal(key('Mar-24'.toUpperCase()), key('Mar-24'), 'case does not matter');
});

test('N1b labels that only look like these are not read', () => {
  for (const s of ['Mar-99999', '13/2024', '00/2024', 'FY25 P14', 'FY25 P0', 'Mar-2', 'Alpha-24', 'Period A', 'Wk 3', 'Q5-24']) assert.equal(periodKey(s, false), null, s);
});

test('N1b a column mixing shapes, or holding one unreadable label, is not read at all', () => {
  assert.equal(periodKeys(['Mar-24', 'Apr-24', 'Period A'], false), null);
  assert.equal(periodKeys(['Mar-24', 'FY25 P1'], false), null);
  assert.ok(periodKeys(['Mar-24', 'Mar 2024'], false), 'two spellings of one shape are fine');
});

test('N1b bare month names are not calendar-read in a fiscal field (a fiscal year starts when the company says)', () => {
  assert.ok(periodKeys(['March', 'January'], true), 'a calendar field still reads');
  assert.equal(periodKeys(['March', 'January'], true, { calendarMonths: false }), null);
});

// ── (c) the caption claims only an order that was set ───────────────────────────────────────────────────────────
const captionFor = (names, res, config) => {
  const dims = axisDims(res, config);
  return { dims, rule: chartRulePick({ dims, measures: 1, rows: res.rows.length }) };
};

test('N1c a readable label set is drawn in time order under the time caption', () => {
  const config = wells([col('Date', 'Month Year')]);
  const res = frame(['Month Year', 'Sales'], ['Mar-24', 'Aug-25', 'Sep-24', 'Feb-24'].map((m, i) => [m, i]));
  const { dims, rule } = captionFor(['Month Year'], res, config);
  assert.equal(dims[0].ordered, true);
  assert.match(rule.reason, /Dates run left to right/);
  assert.equal(rule.pick, 'line');
  assert.deepEqual(axisOf(res, config), ['Feb-24', 'Mar-24', 'Sep-24', 'Aug-25']);
});

test('N1c a date-named field whose order nobody set shows no time claim and no line', () => {
  const config = wells([col('Date', 'Period Name')]);
  const res = frame(['Period Name', 'Sales'], ['Period C', 'Period A', 'Period B'].map((m, i) => [m, i]));
  const { dims, rule } = captionFor(['Period Name'], res, config);
  assert.equal(dims[0].ordered, false);
  assert.doesNotMatch(rule.reason, /Dates run left to right|left to right/i, rule.reason);
  assert.match(rule.reason, /not a time order/, 'it says plainly that the order is not one');
  assert.equal(rule.pick, 'bar');
  assert.ok(!rule.fits.includes('line') && !rule.fits.includes('area'), 'a line would draw a sequence nobody set');
  assert.deepEqual(axisOf(res, config, 'bar'), ['Period C', 'Period A', 'Period B'], 'the engine\'s order, unchanged');
});

test('N1c dates the engine sends as dates stay established, and a field not named like a date is untouched', () => {
  const iso = frame(['Day', 'Sales'], [['2024-03-02', 1], ['2024-01-05', 2]]);
  assert.equal(axisDims(iso, wells([col('Date', 'Day')]))[0].ordered, true);
  const cat = axisDims(frame(['Region', 'Sales'], [['East', 1]]), wells([col('Geo', 'Region')]))[0];
  assert.equal(cat.isDate, false);
  assert.equal(cat.ordered, undefined);
});

test('N1c a shape that says nothing about order keeps the rules it had (the caption still reads for a set order)', () => {
  const r = chartRulePick({ dims: [{ name: 'Month', isDate: true, distinct: 12 }], measures: 1, rows: 12 });
  assert.match(r.reason, /Dates run left to right/);
  assert.equal(r.pick, 'line');
});

// ── round 3: a cell is recomputed in the context the drawn query used ─────────────────────────────────────────────
// buildQuery groups by the sort-by column (it is Power BI's own query), and a grouped column is in every cell's filter
// context. So whatever recomputes a cell from the wells must carry it: Explain's group-by, and the calculation the
// hover shows for the cell. verify-uat-numbers-explain.test.mjs pins the flat-row Explain; these pin the matrix paths.
const { tabulate, cellContext, explainPayloadFromMatrix } = load('daxvisual.tsx');
const { queryGroupRefs } = load('daxwells.tsx');

test('N1 round 3 the query\'s group-by list is what Explain gets, and each field once', () => {
  const c = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo'), col('Date', 'Year')], [col('Product', 'Category')]);
  assert.deepEqual(queryGroupRefs(c), ["'Date'[Fiscal Month]", "'Date'[Year]", "'Product'[Category]", "'Date'[FiscalMonthNo]"]);
  assert.deepEqual(queryGroupRefs(wells([col('Date', 'Fiscal Month', 'FiscalMonthNo'), col('Date', 'FiscalMonthNo')])), ["'Date'[Fiscal Month]", "'Date'[FiscalMonthNo]"]);
  assert.deepEqual(queryGroupRefs(wells([col('Date', 'Month')])), ["'Date'[Month]"], 'no sort-by: the group-by is the wells');
});

test('N1 round 3 a matrix cell on a sort-by field: Explain groups by the sort-by column and the calculation pins its value', () => {
  const config = wells([col('Product', 'Category')], [col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const flat = frame(['Category', 'Fiscal Month', 'FiscalMonthNo', 'Sales'], [['Bikes', 'Jul', 1, 5], ['Bikes', 'Aug', 2, 6]]);
  const res = foldSortColumns(flat, config);
  const m = tabulate(res, config);
  const ci = m.colKeys.indexOf('Aug');
  const p = explainPayloadFromMatrix(config, m, 0, ci);
  assert.deepEqual(p.groupBy, ["'Product'[Category]", "'Date'[Fiscal Month]", "'Date'[FiscalMonthNo]"]);
  const calc = cellContext(config, m, 0, ci).calc;
  assert.match(calc, /'Date'\[Fiscal Month\] = "Aug"/);
  assert.match(calc, /'Date'\[FiscalMonthNo\] = 2/, calc);
});

test('N1 round 3 rows on a sort-by field: the hover calculation pins the sort value of that row', () => {
  const config = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const m = tabulate(foldSortColumns(frame(['Fiscal Month', 'FiscalMonthNo', 'Sales'], [['Jan', 7, 1], ['Jul', 1, 2]]), config), config);
  const jan = m.rows.findIndex((r) => r.keys[0] === 'Jan');
  assert.match(cellContext(config, m, jan, 0).calc, /'Date'\[FiscalMonthNo\] = 7/);
  assert.deepEqual(explainPayloadFromMatrix(config, m, jan, 0).groupBy, ["'Date'[Fiscal Month]", "'Date'[FiscalMonthNo]"]);
});

test('N1 round 3 a field with no sort-by, or a result that did not carry it, gets no extra pin', () => {
  const plain = wells([col('Date', 'Month')]);
  const m = tabulate(frame(['Month', 'Sales'], [['Jan', 1]]), plain);
  assert.doesNotMatch(cellContext(plain, m, 0, 0).calc, /FiscalMonthNo/);
  const withBy = wells([col('Date', 'Fiscal Month', 'FiscalMonthNo')]);
  const m2 = tabulate(frame(['Fiscal Month', 'Sales'], [['Jan', 1]]), withBy);
  assert.equal(cellContext(withBy, m2, 0, 0).calc, "CALCULATE( [Sales],\n  'Date'[Fiscal Month] = \"Jan\" )", 'nothing to pin when the result has no sort values');
});

// ── Ask data: the engine's query does not group by the sort-by, so a model-ordered field claims no time order ─────
test('N1 round 3 Ask data: a field the model orders by a fiscal number is not read as Jan..Dec by its label', () => {
  const stubs = {
    './bridge': { rpc: async () => { throw new Error('no calls'); }, onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {}, focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {} },
    './connection': { useConnection: () => ({ conn: { connected: true }, session: { sessionId: 't' }, context: null }), ConnectBar: () => React.createElement('div') },
    './grid': { ResultGrid: () => React.createElement('div') },
    './tests-authoring': { AuthorDrawer: () => React.createElement('aside') },
    './activity': { useClaudeReflection: () => {} },
    './surface-escape': { useSurfaceEscape: () => {} },
    './echart': { EChart: (p) => { charts.push(p.option); return React.createElement('div'); }, tokenPx: () => 11 },
    echarts: {},
  };
  const answer = { outcome: 'answered', readBy: 'you', reading: 'Sales by Month', checks: [], unsure: [], columns: [{ name: 'Month' }, { name: 'Sales' }],
    rows: [['Mar', 3], ['Jan', 1], ['Feb', 2]], rowCount: 3, truncated: false, elapsedMs: 1 };
  const parts = { groupBy: ["'Date'[Month]"], measures: ['Sales'], filters: [] };
  const draw = (columns) => {
    const L = makeLoader({ ...stubs, './daxeditor': { useDaxModelContext: () => ({ tables: [], measures: [], functions: [], columns }) } });
    charts.length = 0;
    const html = renderToStaticMarkup(React.createElement(L('askdata.tsx').AnswerBody, { answer, parts, connected: true, view: 'chart', viz: 'line', showDax: false, onView: () => {}, onShowDax: () => {} }));
    return { axis: charts.at(-1)?.xAxis?.data ?? null, html };
  };
  assert.deepEqual(draw([{ table: 'Date', name: 'Month' }]).axis, ['Jan', 'Feb', 'Mar'], 'control: with no sort-by the labels are read as months');
  const fiscal = draw([{ table: 'Date', name: 'Month', sortByColumn: 'FiscalMonthNo' }]);
  assert.deepEqual(fiscal.axis, ['Mar', 'Jan', 'Feb'], 'a model-ordered field the result cannot show is left as the engine sent it');
  assert.doesNotMatch(fiscal.html, /Dates run left to right/);
});
