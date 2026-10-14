// The Queries redesign's pure pieces: the chart rules' pick and the friendly headers and dates.
import assert from 'node:assert/strict';
import { chartRulePick, offeredKinds, twoSplitAxes, scatterPoints } from '../webview/src/chartpick.mjs';
import { friendlyHeader, displayValue, isDateColumn, columnFormatter } from '../webview/src/resultformat.mjs';

const d = (name, distinct, isDate = false) => ({ name, distinct, isDate });

// Doc 03 decision table, first is default.
assert.equal(chartRulePick({ dims: [d('Month', 12, true), d('Store region', 4)], measures: 1, rows: 48 }).pick, 'line', 'a date axis by 4 categories is a line');
assert.equal(chartRulePick({ dims: [d('Month', 12, true), d('Store', 30)], measures: 1, rows: 360 }).pick, 'matrix', 'more than 5 series is a grid');
assert.equal(chartRulePick({ dims: [d('Store region', 6)], measures: 1, rows: 6 }).pick, 'bar', 'one category is bars');
assert.equal(chartRulePick({ dims: [d('Product', 400)], measures: 1, rows: 400 }).pick, 'table', 'over 50 categories is rows');
assert.equal(chartRulePick({ dims: [], measures: 1, rows: 1 }).pick, 'card', 'a scalar is a card');
assert.equal(chartRulePick({ dims: [], measures: 3, rows: 1 }).pick, 'table', 'several scalars are rows');
assert.equal(chartRulePick({ dims: [d('Region', 4), d('Channel', 3)], measures: 1, rows: 12 }).pick, 'matrix', 'two categories are a grid');
assert.equal(chartRulePick({ dims: [d('Month', 12, true)], measures: 1, rows: 12 }).pick, 'line', 'a date axis is a line');
assert.equal(chartRulePick({ dims: [], measures: 0, rows: 5 }).pick, 'table', 'no number means rows');

// Hard rules: what does not fit is listed with its reason, and the pick is always among the fits.
const p = chartRulePick({ dims: [d('Store region', 6)], measures: 1, rows: 6 });
assert.ok(p.misfits.some((x) => x.viz === 'line' && /no date/.test(x.reason)), 'line without a date is a misfit');
assert.ok(p.misfits.some((x) => x.viz === 'card'), 'a card cannot show a split');
assert.ok(p.misfits.some((x) => x.viz === 'scatter'), 'a scatter needs two numbers');
for (const s of [{ dims: [d('M', 12, true), d('R', 4)], measures: 1, rows: 48 }, { dims: [], measures: 1, rows: 1 }, { dims: [d('P', 3)], measures: 2, rows: 3 }]) {
  const r = chartRulePick(s);
  assert.ok(r.fits.includes(r.pick), 'the pick fits');
  assert.ok(r.reason.length > 10 && !/[–—]/.test(r.reason), 'the reason is a plain sentence with no dashes');
}

// Friendly headers.
assert.equal(friendlyHeader("'Store'[Store Region]"), 'Store region');
assert.equal(friendlyHeader('Store[Store Region]'), 'Store region');
assert.equal(friendlyHeader('[Net Sales]'), 'Net sales');
assert.equal(friendlyHeader('@Net Sales'), 'Net sales');
assert.equal(friendlyHeader('Sales YTD'), 'Sales YTD');
assert.equal(friendlyHeader('OrderDate'), 'Order date');

// Dates and numbers.
assert.equal(displayValue('2025-01-03T00:00:00'), '3 Jan 2025');
assert.equal(displayValue('2025-01-03T14:05:00'), '3 Jan 2025 14:05');
assert.equal(displayValue('2025-12-01'), '1 Dec 2025');
assert.equal(displayValue('North'), 'North');
assert.equal(displayValue(null), '');
assert.equal(displayValue(true), 'Yes');
assert.ok(/^1.?234.?56$/.test(displayValue(1234.5612)), 'numbers keep two decimals');
assert.equal(isDateColumn([['2025-01-01T00:00:00'], [null], ['2025-02-01T00:00:00']], 0), true);
assert.equal(isDateColumn([['2025-01-01'], ['North']], 0), false);

const monthly = columnFormatter([['2025-01-01T00:00:00'], ['2025-02-01T00:00:00']], 0);
assert.equal(monthly('2025-02-01T00:00:00'), 'Feb 2025', 'a month column reads Jan 2025');
assert.equal(columnFormatter([['2025-01-03T00:00:00'], ['2025-02-01T00:00:00']], 0)('2025-01-03T00:00:00'), '3 Jan 2025', 'a day column keeps the day');
assert.equal(columnFormatter([['North'], ['South']], 0)('North'), 'North', 'text is shown as it came');

assert.equal(columnFormatter([[2023], [2024]], 0, 'OrderDateKey')(2024), '2024', 'a year or key is a label, not an amount');
assert.notEqual(columnFormatter([[2023000], [2024]], 0, 'Net Sales')(2023000), '2023000', 'an amount keeps its separators');

// K22 (Kane, 9 Oct): "only the first three chart buttons seem to work". A button the result cannot use is not shown, and every
// button that is shown draws a chart that fits the result.
const ALL = ['matrix', 'table', 'bar', 'line', 'area', 'card', 'scatter'];
const offered = (shape) => offeredKinds(ALL, chartRulePick(shape));
assert.deepEqual(offered({ dims: [], measures: 1, rows: 1 }), ['table', 'card'], 'one number: the rows and a card, nothing to split');
assert.deepEqual(offered({ dims: [d('Brand', 4)], measures: 1, rows: 4 }), ['matrix', 'table', 'bar'], 'one measure by brands: no line, area, card or scatter');
assert.deepEqual(offered({ dims: [d('Month', 12, true)], measures: 1, rows: 12 }), ['matrix', 'table', 'bar', 'line', 'area'], 'one measure by months');
assert.deepEqual(offered({ dims: [d('Brand', 4)], measures: 2, rows: 4 }), ['matrix', 'table', 'bar', 'scatter'], 'two measures by brands: a scatter plots one against the other');
assert.deepEqual(offeredKinds(ALL, null), ['table'], 'a result with no chart rules offers the rows only');
assert.ok(!offered({ dims: [d('Brand', 4), d('Month', 6, true)], measures: 2, rows: 24 }).includes('line'), 'two measures by two splits: a line would join unrelated points');
assert.ok(offered({ dims: [d('Brand', 3), d('Month', 6, true)], measures: 1, rows: 18 }).includes('line'), 'one measure by brand and month: a line for each brand');
// One measure by two columns is drawn as one series per value of one column, along the other, never as joined "Brand / Month" labels.
assert.deepEqual(twoSplitAxes([d('Brand', 3), d('Month', 6, true)], 1, 'line'), { axis: 1, series: 0 }, 'the date runs along the axis and each brand is a line');
assert.deepEqual(twoSplitAxes([d('Month', 6, true), d('Brand', 3)], 1, 'line'), { axis: 0, series: 1 }, 'the date is first: it stays the axis');
assert.deepEqual(twoSplitAxes([d('Year', 3, true), d('Month', 12, true)], 1, 'line'), { axis: 1, series: 0 }, 'two dates: months along the axis, a line for each year');
assert.deepEqual(twoSplitAxes([d('Brand', 3), d('Region', 4)], 1, 'bar'), { axis: 1, series: 0 }, 'two categories: clustered bars, the field with more values along the axis');
assert.deepEqual(twoSplitAxes([d('Brand', 3), d('Region', 4)], 1, 'bar', true), { axis: 0, series: 1 }, 'two categories placed by the person: their order stays');
assert.deepEqual(twoSplitAxes([d('Brand', 3), d('Store', 40)], 1, 'bar'), { axis: 1, series: 0 }, 'the 40 stores run along the axis, so only 3 series');
assert.equal(twoSplitAxes([d('Brand', 20), d('Store', 40)], 1, 'bar'), null, 'too many series either way: the joined labels stay');
assert.equal(twoSplitAxes([d('Brand', 3), d('Region', 4)], 2, 'bar'), null, 'two measures: the measures are the series');
assert.equal(twoSplitAxes([d('Brand', 3)], 1, 'bar'), null, 'one split: nothing to fold');
// A scatter plots one measure against the other, one point per row.
assert.deepEqual(scatterPoints(['A', 'B', 'C'], [[1, 10], [2, null], [3, 30]]), [{ name: 'A', x: 1, y: 10 }, { name: 'C', x: 3, y: 30 }], 'a scatter point needs both numbers');

console.log('queries-result-format: ok');
