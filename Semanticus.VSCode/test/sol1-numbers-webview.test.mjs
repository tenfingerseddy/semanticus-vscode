// Sol 6.1's review of the joined M11 build, area 3 (numbers), the four findings that live in the webview. Each block
// is Sol's own scenario, run against the real module, and each failed before its fix:
//   3.1 a percent answer keeps its format in Queries (Return Rate % 0.0068 reads 0.7%, never 0.01)
//   3.2 a stopped question does not leave the last question's rows or statement on screen
//   3.6 a card is one number: never two measures, or a column of rows, added into one figure
//   3.8 two queries that differ inside a text value are different queries
import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

globalThis.document = { documentElement: {} };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });

// ── 3.1 / 3.6: the answer's numbers, drawn ────────────────────────────────────────────────────────────────
const charts = [];
const visual = makeLoader({
  './echart': { EChart: (p) => { charts.push(p.option); return React.createElement('div', { 'data-testid': 'chart' }); }, tokenPx: () => 11 },
  echarts: {},
});
const M = visual('askdata-model.ts');
const { DaxVisual } = visual('daxvisual.tsx');

const returnRate = {
  outcome: 'answered', readBy: 'you',
  columns: [{ name: "'Store'[Store Region]" }, { name: 'Return Rate %' }], formats: [null, '0.0%'],
  rows: [['Coast', 0.0068], ['West', 0.0121]], rowCount: 2,
};
const returnParts = { measures: ['Return Rate %'], groupBy: ["'Store'[Store Region]"] };

test('3.1 the answer carries each measure column\'s format into the result Queries draws', () => {
  const rs = M.resultSetOf(returnRate);
  assert.equal(rs.columns[1].format, '0.0%', 'the measure column keeps the model\'s own format');
  assert.equal(rs.columns[0].format, undefined, 'a column that is not a measure has none');
});

test('3.1 a matrix, a chart\'s tooltip and its axis read 0.7%, never 0.01', () => {
  const rs = M.resultSetOf(returnRate);
  const config = M.pivotConfigOf(returnParts);
  const matrix = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res: rs, config, viz: 'matrix', height: 300 })));
  assert.match(matrix, /0\.7%/, 'the matrix shows the percent');
  assert.match(matrix, /1\.2%/);
  assert.doesNotMatch(matrix, /0\.01\b/, 'and never the rounded number under the percent measure\'s name');
  charts.length = 0;
  renderToStaticMarkup(React.createElement(DaxVisual, { res: rs, config, viz: 'bar', height: 300 }));
  const option = charts.at(-1);
  assert.equal(option.yAxis.axisLabel.formatter(0.0068), '0.7%', 'the value axis reads as percents');
  assert.match(option.tooltip.formatter({ dataIndex: 0, seriesIndex: 0 }), /0\.7%/, 'the tooltip reads as a percent');
});

test('3.1 a single percent on a card reads as a percent', () => {
  const one = { outcome: 'answered', columns: [{ name: 'Return Rate %' }], formats: ['0.0%'], rows: [[0.0068]], rowCount: 1 };
  const html = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res: M.resultSetOf(one), config: M.pivotConfigOf({ measures: ['Return Rate %'], groupBy: [] }), viz: 'card', height: 200 })));
  assert.match(html, /0\.7%/);
});

test('3.6 a card never adds two different measures into one number', () => {
  const two = { outcome: 'answered', columns: [{ name: 'Net Sales' }, { name: 'Units' }], rows: [[1000, 10]], rowCount: 1 };
  const config = M.pivotConfigOf({ measures: ['Net Sales', 'Units'], groupBy: [] });
  const html = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res: M.resultSetOf(two), config, viz: 'card', height: 200 })));
  assert.doesNotMatch(html, /1,010/, 'Net Sales plus Units is not a number anyone asked for');
});

test('3.6 a card never adds the rows of one measure into a total that measure does not have', () => {
  const html = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res: M.resultSetOf(returnRate), config: M.pivotConfigOf(returnParts), viz: 'card', height: 200 })));
  assert.doesNotMatch(html, /0\.02|1\.9%|1\.89/, 'two regions\' rates are not added into one');
  assert.match(html, /one number/i, 'the card says why it shows none');
});

test('3.6 the chart rules allow a card only for one number in one row', async () => {
  const { chartRulePick } = await import('../webview/src/chartpick.mjs');
  const fits = (s) => chartRulePick(s).fits.includes('card');
  assert.equal(fits({ dims: [], measures: 1, rows: 1 }), true, 'one number, one row');
  assert.equal(fits({ dims: [], measures: 2, rows: 1 }), false, 'two numbers');
  assert.equal(fits({ dims: [{ name: 'Region', isDate: false, distinct: 3 }], measures: 1, rows: 3 }), false, 'a split');
  assert.equal(fits({ dims: [], measures: 1, rows: 3 }), false, 'more than one row');
});

test('3.6 Ask your data offers the chart kinds the rules allow, not every kind', () => {
  const ask = makeLoader({
    './bridge': { rpc: async () => { throw new Error('no calls'); }, onDidChange: () => () => {}, onProgress: () => () => {} },
    './connection': { useConnection: () => ({ conn: { connected: true }, session: { sessionId: 't' }, context: null }), ConnectBar: () => null },
    './grid': { ResultGrid: () => React.createElement('div', { 'data-testid': 'grid' }) },
    './echart': { EChart: (p) => { charts.push(p.option); return null; }, tokenPx: () => 11 }, echarts: {},
    './daxeditor': { useDaxModelContext: () => ({ tables: [], measures: [], columns: [] }) },
    './tests-authoring': { AuthorDrawer: () => null }, './activity': { useClaudeReflection: () => {} }, './surface-escape': { useSurfaceEscape: () => {} },
  })('askdata.tsx');
  const answer = { outcome: 'answered', readBy: 'you', columns: [{ name: 'Net Sales' }, { name: 'Units' }], rows: [[1000, 10]], rowCount: 1 };
  const html = renderToStaticMarkup(React.createElement(ask.AnswerBody, {
    answer, parts: { measures: ['Net Sales', 'Units'], groupBy: [] }, connected: true, view: 'chart', viz: 'card', onViz: () => {}, showDax: false, onView: () => {}, onShowDax: () => {},
  }));
  assert.doesNotMatch(html, /aria-label="Card"/, 'there is no Card button for two numbers (K22: a button that does nothing is not shown)');
  assert.doesNotMatch(html, /<button[^>]*aria-label="[^"]*"[^>]*disabled/, 'and no chart button is shown switched off');
  assert.match(html, /aria-label="Scatter"/, 'while the Scatter that two numbers fit is');
  assert.doesNotMatch(readable(html), /1,010/, 'and a card asked for anyway is not drawn as their sum');
});

// ── 3.2: a stopped question and the last question's rows ──────────────────────────────────────────────────
function queriesAsk(answers) {
  const rt = makeHookRuntime();
  const rpcCalls = [];
  const Q = makeLoader({
    './bridge': { rpc: async (m, ...p) => { rpcCalls.push(m); if (m === 'askData') return answers.shift(); return null; } },
    './hooks': { usePersistedState: (_k, init) => rt.React.useState(init) },
    './consent': { useConsentCard: () => {}, useAskWhenAnswerNeedsPermission: () => {} },
    './daxeditor': { useDaxModelContext: () => ({ measures: [], columns: [] }) },
    './askdata': {
      ChipMenu: () => null, OtherReadings: () => null, AssumptionLine: () => null, JEV_READ_THIS: 'Jev read this',
      Line: (p) => React.createElement('div', null, p.text), announcement: (a) => String(a.outcome), stopLine: (_a, u) => u,
    },
  }, rt.React)('queriesask.tsx');
  const rows = [];
  const daxes = [];
  const host = rt.renderHook(() => Q.useQueriesAsk({
    persistKey: 'm', busyElsewhere: false, setBusy: () => {},
    onDax: (d) => daxes.push(d), onRows: (rs, dax) => rows.push({ rs, dax }),
  }));
  return { Q, host, rows, daxes, rpcCalls };
}

const sales2025 = {
  outcome: 'answered', readBy: 'jev', statement: 'This shows Net Sales, where Year is 2025.',
  parts: { measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2025'] }] },
  dax: 'EVALUATE ROW("v", CALCULATE([Net Sales], \'Date\'[Year] = 2025))', columns: [{ name: 'Net Sales' }], rows: [[5]], rowCount: 1,
};
const stopped2024 = {
  outcome: 'needs-you', readBy: 'jev', statement: 'This shows Net Sales, where Year is 2024.',
  parts: { measures: ['Net Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2024'] }] },
  unsure: ['The final check stopped this question.'], checks: [{ name: 'final', passed: false, line: 'The final check stopped this question.' }],
};

test('3.2 a stopped question clears the last question\'s rows and DAX, so nothing old sits under the new statement', async () => {
  const { host, rows } = queriesAsk([sales2025, stopped2024]);
  host.result.setWords('net sales 2025'); host.result.askWords(); await settle(); await settle();
  assert.equal(host.result.chipDax, sales2025.dax, 'the first answer is on screen');
  assert.ok(rows.at(-1).rs, 'with its rows');
  host.result.setWords('net sales 2024'); host.result.askWords(); await settle(); await settle();
  assert.equal(host.result.answer.statement, stopped2024.statement);
  assert.equal(host.result.chipDax, '', 'the DAX the chips wrote for 2025 is not the new question\'s');
  assert.equal(rows.at(-1).rs, null, 'and the 2025 rows are cleared');
});

test('3.2 the statement shows only while it still describes what is on screen', async () => {
  const { host, Q } = queriesAsk([sales2025]);
  host.result.setWords('net sales 2025'); host.result.askWords(); await settle(); await settle();
  const shown = (current) => readable(renderToStaticMarkup(React.createElement(Q.AskLines, { ask: host.result, current })));
  assert.match(shown(true), /Year is 2025/, 'own query: the statement reads');
  assert.doesNotMatch(shown(false), /Year is 2025/, 'a query changed by hand: the statement no longer describes it');
});

test('3.2 the Queries page tells the lines whether the statement still holds', async () => {
  const { readFileSync } = await import('node:fs');
  const lab = readFileSync(new URL('../webview/src/query.tsx', import.meta.url), 'utf8');
  assert.match(lab, /<AskLines ask=\{q\.askBox\} current=\{q\.askState\.answer\?\.dax \? q\.answerRows : !q\.res\}/, 'the page says whether the statement still describes the screen (rows owned by the answer, delta B3)');
});

// ── 3.8: whitespace inside a text value is a difference ───────────────────────────────────────────────
test('3.8 two queries that differ inside a text value are different queries', () => {
  const { squash } = makeLoader({}, React)('daxsquash.ts');
  assert.notEqual(squash('EVALUATE FILTER(T, T[Region] = "A  B")'), squash('EVALUATE FILTER(T, T[Region] = "A B")'));
  assert.notEqual(squash("EVALUATE 'A  B'[x]"), squash("EVALUATE 'A B'[x]"), 'a quoted table name too');
  assert.notEqual(squash('EVALUATE [A  B]'), squash('EVALUATE [A B]'), 'and a bracketed name');
  assert.equal(squash('EVALUATE\n    ROW("a",   1)\r\n'), squash('EVALUATE ROW("a", 1)'), 'layout outside a value still does not matter');
  assert.equal(squash('  x  =  "a  b" '), 'x = "a  b"');
  assert.equal(squash('"say ""hi  there"" now"  +  1'), '"say ""hi  there"" now" + 1', 'a doubled quote stays inside the value');
});

test('3.8 the page and the hook use the same quote-aware comparison', async () => {
  const { readFileSync } = await import('node:fs');
  const lab = readFileSync(new URL('../webview/src/daxlab.tsx', import.meta.url), 'utf8');
  const ask = readFileSync(new URL('../webview/src/queriesask.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(lab + ask, /replace\(\/\\s\+\/g, ' '\)/, 'no second, blind whitespace squash is left');
});

// ── 3.1 again: a hand-typed query in Queries carries no formats from the engine, so each measure's own is looked up by name ──
const MEASURES = [{ name: 'Return Rate %', table: 'Sales', formatString: '0.0%' }, { name: 'Net Sales', table: 'Sales', formatString: '#,0' }, { name: 'Plain', table: 'Sales' }];
const typed = (query, names, rows = [names.map(() => 0.0068)]) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1, query });
const formatsOf = (query, names) => {
  const { withMeasureFormats } = makeLoader({}, React)('resultformats.ts');
  return withMeasureFormats(typed(query, names), query, MEASURES).columns.map((c) => c.format);
};

test('3.1b a hand-typed query gets each plain measure column\'s format by name', () => {
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), ['0.0%']);
  assert.deepEqual(formatsOf("EVALUATE\n    SUMMARIZECOLUMNS ( 'Store'[Region], \"@Return Rate %\", [Return Rate %] )", ["'Store'[Region]", '[@Return Rate %]']), [undefined, '0.0%']);
  assert.deepEqual(formatsOf('EVALUATE ROW("Rate", [Return Rate %], "Sales", [Net Sales])', ['[Rate]', '[Sales]']), ['0.0%', '#,0'], 'the alias may differ from the measure');
});

test('3.1b a column that is not a plain measure reference keeps today\'s display', () => {
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", [Net Sales] * 100)', ['[Return Rate %]']), [undefined], 'a name that only looks like the measure');
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", CALCULATE ( [Net Sales], ALL ( Sales ) ))', ['[Return Rate %]']), [undefined]);
  assert.deepEqual(formatsOf('// ROW("Rate", [Return Rate %])\nEVALUATE ROW("Rate", [Net Sales])', ['[Rate]']), ['#,0'], 'a commented pair is not the query');
  assert.deepEqual(formatsOf('EVALUATE ROW("Rate", "x", [Return Rate %] + 1)', ['[Rate]']), [undefined]);
  assert.deepEqual(formatsOf('DEFINE MEASURE Sales[Return Rate %] = 1 EVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), [undefined], 'a measure the query defines is not the model\'s');
  assert.deepEqual(formatsOf('EVALUATE ROW("Plain", [Plain])', ['[Plain]']), [undefined], 'a measure with no format');
  assert.deepEqual(formatsOf('EVALUATE ROW("Rate", [Return Rate %], "Rate", [Net Sales])', ['[Rate]']), [undefined], 'one name for two measures says nothing');
});

test('3.1b a format the answer already carries is kept, and the grid and chart read the found one', () => {
  const { withMeasureFormats } = makeLoader({}, React)('resultformats.ts');
  const carried = { ...typed('EVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), columns: [{ name: '[Return Rate %]', format: '0%' }] };
  assert.equal(withMeasureFormats(carried, carried.query, MEASURES).columns[0].format, '0%');
  const q = 'EVALUATE ROW("Return Rate %", [Return Rate %])';
  const rs = withMeasureFormats(typed(q, ['[Return Rate %]']), q, MEASURES);
  const html = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res: rs, config: M.pivotConfigOf({ measures: ['Return Rate %'], groupBy: [] }), viz: 'card', height: 200 })));
  assert.match(html, /0\.7%/);
});

test('3.1b Queries looks the formats up from the model\'s measures', async () => {
  const { readFileSync } = await import('node:fs');
  const lab = readFileSync(new URL('../webview/src/daxlab.tsx', import.meta.url), 'utf8');
  const editor = readFileSync(new URL('../webview/src/daxeditor.tsx', import.meta.url), 'utf8');
  assert.match(lab, /withMeasureFormats\(res, resQuery \?\? res\.query, formatMeasures\)/, 'the result on screen is given its formats by name, from the model the numbers came from (delta B4)');
  assert.match(editor, /formatString: x\.formatString/, 'the model context carries each measure\'s format');
});
