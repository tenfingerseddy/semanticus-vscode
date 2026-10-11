// Verifier, lane m11/uat-numbers (N1, Sol H8). LEFT FAILING ON PURPOSE: it proves a defect, it does not fix one.
//
// H8 was a time axis drawn out of order under the rules' caption "Dates run left to right". The lane orders the labels
// it can read (2024-03, Mar 2024, 2024-Q3, a month name) and keeps the engine's order for any label set it cannot read.
// But the caption does not come from that reading: Queries (daxlab.tsx) calls a field a date when its NAME sounds like
// one (familyByName) and then says "Dates run left to right" whatever order the axis was drawn in. So a common month
// label such as FORMAT([Date], "MMM-YY") ("Mar-24"), or a fiscal period ("FY25 P3"), is still drawn in the order the
// engine happened to send, under a caption that says it is time order. That is H8 itself, for these label shapes.
//
// The invariant this holds, whichever way the join fixes it (read these labels, order by the model's sort-by column,
// or stop claiming time order when the order was not established): when the rules say "Dates run left to right",
// the axis drawn under them runs in time order.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { chartRulePick } from '../webview/src/chartpick.mjs';
import { isDateColumn } from '../webview/src/resultformat.mjs';

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
const { familyByName } = load('daxwells.tsx');

const frame = (names, rows) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1 });
const cfg = (name) => ({ rows: [{ kind: 'column', ref: `'Date'[${name}]`, name, table: 'Date' }], cols: [], values: [{ kind: 'measure', ref: '[Sales]', name: 'Sales' }], filters: [] });

// The caption exactly as Queries computes it (daxlab.tsx, the `shape` memo), and the axis exactly as it is drawn.
function captionAndAxis(name, engineOrder) {
  const res = frame([name, 'Sales'], engineOrder.map((v, i) => [v, i + 1]));
  const config = cfg(name);
  const shape = { dims: [{ name, isDate: familyByName(name) === 'date' || isDateColumn(res.rows, 0), distinct: engineOrder.length }], measures: 1, rows: res.rows.length };
  const rule = chartRulePick(shape);
  charts.length = 0;
  renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz: rule.pick, height: 300, across: false }));
  return { reason: rule.reason, pick: rule.pick, axis: charts.at(-1)?.xAxis?.data ?? null };
}

const cases = [
  // FORMAT([Date], "MMM-YY"): one of the most common month labels in a Power BI date table.
  ['Month Year', ['Mar-24', 'Aug-25', 'Sep-24', 'Feb-24'], ['Feb-24', 'Mar-24', 'Sep-24', 'Aug-25']],
  // A fiscal period label (the lane's own test pins this one to the engine's order on a line chart).
  ['Fiscal Period', ['FY25 P3', 'FY24 P12', 'FY25 P1'], ['FY24 P12', 'FY25 P1', 'FY25 P3']],
];

for (const [name, engineOrder, timeOrder] of cases) {
  test(`verify N1: "${name}" (${engineOrder.join(', ')}) is never drawn out of time order under "Dates run left to right"`, () => {
    const { reason, pick, axis } = captionAndAxis(name, engineOrder);
    if (!/Dates run left to right/.test(reason)) return;   // no time claim: any order is honest
    assert.equal(pick, 'line');
    assert.deepEqual(axis, timeOrder, `the caption says "${reason}" over an axis drawn ${JSON.stringify(axis)}`);
  });
}
