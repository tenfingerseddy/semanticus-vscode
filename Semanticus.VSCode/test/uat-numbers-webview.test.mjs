// Sol's hands-on test of the final M11 build (90716ced): "wrong number shown as right" items that live in the webview.
// Each block failed on 90716ced and passes with its fix.
//   N1 (H8)  a text YearMonth axis, and the matrix months, run in time order, never first-seen order
//   N4 (H11) the Workflows list shows a run's own verdict, and Author's sentence follows the checks switch
//   N5 (H12) the Reports library line counts and names every result state
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

// The motion folder (reached through the Reports page imports) reads the real bridge, which asks for a window and the VS Code API.
globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => undefined;
globalThis.removeEventListener ??= () => undefined;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
globalThis.document = { documentElement: { style: {} } };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });

const charts = [];
const visual = makeLoader({
  './echart': { EChart: (p) => { charts.push(p.option); return React.createElement('div', { 'data-testid': 'chart' }); }, tokenPx: () => 11 },
  echarts: {},
});
const { DaxVisual } = visual('daxvisual.tsx');

// ── N1: Product[Category] across Date[YearMonth], a TEXT column, the engine's rows in no set order ──────────
const col = (table, name) => ({ kind: 'column', ref: `'${table}'[${name}]`, name, table });
const cfg = (rows, cols) => ({ rows, cols, values: [{ kind: 'measure', ref: '[Margin %]', name: 'Margin %' }], filters: [] });
const frame = (names, rows) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1 });

// The seen order from the live run (UAT-F-S04a-004): 2024-03, 2025-08, 2024-09, 2024-02, ...
function acrossResult(months, cat = ['Bikes', 'Clothing']) {
  return frame(['Category', 'YearMonth', 'Margin %'], cat.flatMap((c, ci) => months.map((m, mi) => [c, m, ci * 100 + mi])));
}
const axisOf = (res, config, viz = 'line', across = true) => {
  charts.length = 0;
  renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz, height: 300, across }));
  return charts.at(-1).xAxis.data;
};
const headersOf = (res, config) => {
  const html = renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz: 'matrix', height: 300 }));
  return [...html.matchAll(/<th[^>]*>(.*?)<\/th>/g)].map((m) => readable(m[1]));
};
const seen = ['2024-03', '2025-08', '2024-09', '2024-02', '2025-01', '2024-12'];
const sorted = ['2024-02', '2024-03', '2024-09', '2024-12', '2025-01', '2025-08'];

test('N1 a text YearMonth axis runs in time order, whatever order the engine sent', () => {
  const res = acrossResult(seen);
  const config = cfg([col('Product', 'Category')], [col('Date', 'YearMonth')]);
  assert.deepEqual(axisOf(res, config), sorted, 'chart, date across the columns');
  // each line's numbers follow their month: Bikes' value at 2024-03 was index 0 in the engine's order
  const bikes = charts.at(-1).series.find((x) => x.name === 'Bikes').data;
  assert.deepEqual(bikes, sorted.map((m) => seen.indexOf(m)), 'a number stays under its own month');
});

test('N1 the matrix month columns run in time order too', () => {
  const res = acrossResult(seen);
  const config = cfg([col('Product', 'Category')], [col('Date', 'YearMonth')]);
  assert.deepEqual(headersOf(res, config).slice(1), sorted);
});

test('N1 a YearMonth in the rows (date down the side) orders the chart axis and the matrix rows', () => {
  const res = frame(['YearMonth', 'Margin %'], seen.map((m, i) => [m, i]));
  const config = cfg([col('Date', 'YearMonth')], []);
  assert.deepEqual(axisOf(res, config, 'line', false), sorted);
  const html = readable(renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz: 'matrix', height: 300 })));
  assert.deepEqual([...html.matchAll(/\d{4}-\d{2}/g)].map((m) => m[0]), sorted, 'matrix rows');
});

test('N1 sibling calendar labels order the same way: Mar 2024, 2024-Q3, a month name, a bare year, YYYYMM text', () => {
  const cases = [
    ['YearMonthName', ['Mar 2024', 'Aug 2025', 'Sep 2024', 'Feb 2024'], ['Feb 2024', 'Mar 2024', 'Sep 2024', 'Aug 2025']],
    ['Quarter', ['2025-Q1', '2024-Q3', '2024-Q1', '2024-Q4'], ['2024-Q1', '2024-Q3', '2024-Q4', '2025-Q1']],
    ['Month', ['March', 'January', 'December', 'February'], ['January', 'February', 'March', 'December']],
    ['Year', ['2025', '2023', '2024'], ['2023', '2024', '2025']],
    ['Period', ['202503', '202401', '202412', '202402'], ['202401', '202402', '202412', '202503']],
  ];
  for (const [name, input, want] of cases) {
    const res = frame([name, 'Margin %'], input.map((v, i) => [v, i]));
    const axis = axisOf(res, cfg([col('Date', name)], []), 'line', false);
    assert.deepEqual(axis.map((a) => String(a).replace(/^(\d{4})(\d{2})$/, '$1$2')), want, name);
  }
});

test('N1 a label set it cannot read keeps the engine order, and a plain category is never re-sorted', () => {
  const regions = ['West', 'Coast', 'North'];
  const res = frame(['Region', 'Margin %'], regions.map((v, i) => [v, i]));
  assert.deepEqual(axisOf(res, cfg([col('Store', 'Region')], []), 'bar', false), regions, 'a ranking stays ranked');
  // (FY25 P3 and its kind are read now: uat-numbers-order.test.mjs. A label with no time in it is still left alone.)
  const odd = ['Wave C', 'Wave A', 'Wave B'];
  const res2 = frame(['Fiscal Period', 'Margin %'], odd.map((v, i) => [v, i]));
  assert.deepEqual(axisOf(res2, cfg([col('Date', 'Fiscal Period')], []), 'line', false), odd, 'labels it cannot read are not put in an invented order');
  const mixed = ['2024-03', 'March', '2024-01'];
  const res3 = frame(['YearMonth', 'Margin %'], mixed.map((v, i) => [v, i]));
  assert.deepEqual(axisOf(res3, cfg([col('Date', 'YearMonth')], []), 'line', false), mixed, 'mixed shapes are not mixed into one order');
});

// ── N4: the list shows the report's own verdict; Author's sentence follows the effective strictness ─────────────
const wf = makeLoader({ './workflow-v3model': { runOnceKind: () => 'run' } })('workflows-model.ts');
const NOW = new Date('2026-10-02T12:00:00');
const todayRun = (report) => wf.lastRuns([{ kind: 'run', when: '2026-10-02T09:00:00', report: { workflow: 'w', ...report } }]).get('w');

test('N4 a run that finished with the report saying Unknown is listed as Unknown, never passed', () => {
  const line = wf.lastRunLine(todayRun({ status: 'completed', verdict: 'Unknown' }), NOW);
  assert.equal(line.text, 'Today · Unknown');
  assert.equal(line.ok, null);
});

test('N4 every verdict a report can carry reads as its own word in the list', () => {
  const want = { Verified: ['passed', true], Unknown: ['Unknown', null], NeedsReview: ['needs review', false], Broken: ['broken', false], Overridden: ['overridden', false] };
  for (const [verdict, [word, ok]] of Object.entries(want)) {
    const line = wf.lastRunLine(todayRun({ status: 'completed', verdict }), NOW);
    assert.equal(line.text, `Today · ${word}`, verdict);
    assert.equal(line.ok, ok, verdict);
  }
  assert.equal(wf.lastRunLine(todayRun({ status: 'aborted', verdict: 'Unknown' }), NOW).text, 'Today · stopped', 'a stopped run is stopped');
});

test('N4 a report with no verdict says finished, and reads the verdict out of its sealed evidence when that holds one', () => {
  assert.equal(wf.lastRunLine(todayRun({ status: 'completed' }), NOW).text, 'Today · finished', 'no verdict: never "passed"');
  assert.equal(wf.lastRunLine(todayRun({ status: 'done', evidence: JSON.stringify({ verdict: 'Broken' }) }), NOW).text, 'Today · broken');
  assert.equal(wf.lastRunLine(todayRun({ status: 'done', evidence: 'not json' }), NOW).text, 'Today · finished');
});

test('N4 the list row draws what the line says', () => {
  const noop = () => () => {};
  const bridge = new Proxy({ rpc: async () => { throw new Error('no calls'); }, copyText: async () => {} }, { get: (t, k) => (k in t ? t[k] : noop) });
  const W = makeLoader({ './bridge': bridge, '@xyflow/react/dist/style.css': {} })('workflows.tsx');
  const info = { name: 'w', title: 'W', source: 'user', stepCount: 3 };
  const row = (last) => readable(renderToStaticMarkup(React.createElement(W.WorkflowRow, { w: info, def: null, policy: null, isPro: true, last, now: NOW, onOpen: () => {}, onRun: () => {} })));
  assert.match(row(todayRun({ status: 'completed', verdict: 'Unknown' })), /Today · Unknown/);
  assert.doesNotMatch(row(todayRun({ status: 'completed', verdict: 'Unknown' })), /passed/);
  assert.match(row(todayRun({ status: 'completed', verdict: 'Verified' })), /Today · passed/);
});

const guided = { name: 'g', title: 'G', steps: [{ id: 's1', title: 'S', performer: 'assistant', gate: { inputs: [{ name: 'a' }], verify: [{ kind: 'bpa_clean' }] } }] };
const composed = { name: 'c', title: 'C', steps: [
  { id: 'a', title: 'A', performer: 'person', questions: [{ name: 'q', question: 'Q?', by: 'you' }] },
  { id: 'b', title: 'B', performer: 'semanticus', action: { op: 'create_measure' }, blanks: [{ name: 'formula', by: 'assistant' }], checks: [{ kind: 'dax_valid' }] },
] };

test('N4 Author never says Semanticus checks the work while the checks are off', () => {
  assert.equal(wf.summaryText(guided), 'Your assistant does 1 step, and Semanticus checks the work before a step counts as done.', 'switch unset: as before');
  assert.equal(wf.summaryText(guided, 'hard'), 'Your assistant does 1 step, and Semanticus checks the work before a step counts as done.');
  assert.equal(wf.summaryText(guided, 'off'), 'Your assistant does 1 step. Nothing is checked by Semanticus.');
  assert.match(wf.summaryText(guided, 'warn'), /warns when a check fails, but never holds a step back/);
  assert.doesNotMatch(wf.summaryText(composed, 'off'), /every check passes/);
  assert.match(wf.summaryText(composed, 'off'), /Then Semanticus makes the change\./);
  assert.match(wf.summaryText(composed), /only if every check passes/, 'switch unset: as before');
  assert.match(wf.summaryText(composed, 'warn'), /makes the change and warns when a check fails/);
});

test('N4 the workflow\'s own strictness and one step\'s own gate count too, and the model-wide switch outranks them', () => {
  const offByFile = { ...guided, strictness: 'off' };
  assert.equal(wf.summaryText(offByFile), 'Your assistant does 1 step. Nothing is checked by Semanticus.');
  assert.match(wf.summaryText(offByFile, 'hard'), /checks the work before a step counts as done/, 'the switch on wins over the file');
  const gateOff = { ...guided, steps: [{ ...guided.steps[0], gate: { ...guided.steps[0].gate, strictness: 'off' } }] };
  assert.equal(wf.summaryText(gateOff), 'Your assistant does 1 step. Nothing is checked by Semanticus.');
});

test('N4 Author hands the page\'s checks switch to the sentence', async () => {
  const { readFileSync } = await import('node:fs');
  const src = readFileSync(new URL('../webview/src/workflowdesign.tsx', import.meta.url), 'utf8');
  assert.match(src, /M\.summaryOf\([\s\S]*?, policy\?\.enforcement\)/, 'summaryOf gets policy.enforcement');
});

// ── N5: the Reports library line can never read cleaner than the results behind it ────────────────────────────
import { evidenceCountsLine } from '../webview/src/evidencesummary.mjs';

test('N5 Sol\'s report: 13 passed, 1 differs, 18 could not be checked reads all three', () => {
  const line = evidenceCountsLine({ kind: 'test-suite', verdict: 'Broken', verified: 13, total: 32, unknowns: 18,
    verdictCounts: { Verified: 13, NeedsReview: 0, Broken: 1, Unknown: 18, Overridden: 0 } });
  assert.equal(line, '13 of 32 passed · 1 differs · 18 could not be checked');
});

test('N5 every result state with something in it is named, in every combination', () => {
  const states = [['Broken', 'broke'], ['NeedsReview', 'review'], ['Overridden', 'overridden'], ['Unknown', 'could not be checked']];
  const words = { Broken: /\b2 broken\b/, NeedsReview: /\b2 need review\b/, Overridden: /\b2 overridden\b/, Unknown: /\b2 could not be checked\b/ };
  for (let mask = 0; mask < 16; mask++) {
    const counts = { Verified: 5, NeedsReview: 0, Broken: 0, Unknown: 0, Overridden: 0 };
    states.forEach(([k], i) => { if (mask & (1 << i)) counts[k] = 2; });
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    const verdict = counts.Broken ? 'Broken' : counts.NeedsReview ? 'NeedsReview' : counts.Overridden ? 'Overridden' : counts.Unknown ? 'Unknown' : 'Verified';
    const line = evidenceCountsLine({ kind: 'workflow-run', verdict, verified: 5, total, unknowns: counts.Unknown, verdictCounts: counts });
    assert.match(line, new RegExp(`^5 of ${total} passed`), `mask ${mask}: ${line}`);
    states.forEach(([k], i) => { if (mask & (1 << i)) assert.match(line, words[k], `mask ${mask} names ${k}: ${line}`); });
    assert.doesNotMatch(line, /not passed/, 'nothing is unaccounted for when the counts add up');
  }
});

test('N5 an older report with no per-state counts still names a verdict that is not clean, and what the counts leave out', () => {
  assert.equal(evidenceCountsLine({ verdict: 'Broken', verified: 13, total: 32, unknowns: 18 }), '13 of 32 passed · 18 could not be checked · 1 result not passed · Broken');
  assert.equal(evidenceCountsLine({ verdict: 'Verified', verified: 4, total: 4, unknowns: 0 }), '4 of 4 passed');
  assert.equal(evidenceCountsLine({ verdict: 'Unknown', verified: 0, total: 3, unknowns: 0 }), '0 of 3 passed · 3 results not passed · Not verified');
  assert.equal(evidenceCountsLine({ verdict: 'NeedsReview' }), 'Needs review');
  assert.equal(evidenceCountsLine({}), null, 'nothing to say, nothing said');
});

test('N5 the Reports library card itself shows the line, and never the old "of 32 checked" over a differing report', () => {
  const noop = () => () => {};
  const bridge = new Proxy({ rpc: async () => { throw new Error('no calls'); }, copyText: async () => {} }, { get: (t, k) => (k in t ? t[k] : noop) });
  const T = makeLoader({ './bridge': bridge, '@xyflow/react/dist/style.css': {} })('tests.tsx');
  const item = { id: 'r1', kind: 'test-suite', title: 'Sales test evidence', createdUtc: '2026-10-01T10:00:00Z', modelName: 'Working', valid: true,
    verdict: 'Broken', verified: 13, total: 32, unknowns: 18, verdictCounts: { Verified: 13, NeedsReview: 0, Broken: 1, Unknown: 18, Overridden: 0 } };
  const html = readable(renderToStaticMarkup(React.createElement(T.EvidenceLibraryView, { library: { items: [item], invalidCount: 0 }, onOpen: () => {} })));
  assert.match(html, /13 of 32 passed · 1 differs · 18 could not be checked/);
  assert.doesNotMatch(html, /13 of 32 checked/);
});
