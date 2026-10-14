// Chart colours too similar (Kane, 13 Oct 2026, Ask your data, "sales by month and year", clustered bars, 12 series).
// Three things close it, each with its own block below:
//   (1) the field with MORE values goes on the axis and the one with FEWER becomes the series (Month along the bottom, three
//       years as three bars), unless the person put the fields in the wells themselves;
//   (2) a chart that still needs more than six series never repeats a colour: a ramp across green, blue and purple;
//   (3) in the light and the dark theme each neighbour is clearly different (OKLab distance) and every bar stands out from the page.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { seriesRamp, minDistance, contrastRatio } from '../webview/src/chartramp.mjs';
import { twoSplitAxes } from '../webview/src/chartpick.mjs';

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

// The six steps and the page as the headless harness measured them (Dark Modern, Light Modern, Dark High Contrast).
const THEMES = {
  dark: { bg: [31, 31, 31], steps: [[60, 192, 143], [123, 176, 235], [185, 151, 212], [124, 248, 196], [177, 231, 255], [240, 205, 255]] },
  light: { bg: [255, 255, 255], steps: [[10, 122, 85], [27, 81, 143], [82, 51, 106], [0, 79, 45], [0, 39, 97], [43, 11, 63]] },
  hc: { bg: [0, 0, 0], steps: [[137, 209, 133], [89, 164, 249], [177, 128, 215], [192, 255, 187], [143, 219, 255], [232, 181, 255]] },
};
const MIN_NEIGHBOUR = 0.035;   // OKLab distance; about 0.02 is the least an eye can tell apart
const MIN_CONTRAST = 3;        // WCAG's bar for a graphic on its page

// ── (2) and (3): the ramp ───────────────────────────────────────────────────────────────────────────────────────
for (const [name, t] of Object.entries(THEMES)) {
  test(`12 ordered series (${name}): twelve colours, each neighbour clearly different, every bar stands out from the page`, () => {
    const r = seriesRamp(t.steps, t.bg, 12, true);
    assert.equal(new Set(r.map((c) => c.join())).size, 12, 'no colour twice');
    assert.ok(minDistance(r) >= MIN_NEIGHBOUR, `neighbours: ${minDistance(r).toFixed(3)}`);
    assert.ok(Math.min(...r.map((c) => contrastRatio(c, t.bg))) >= MIN_CONTRAST, 'every bar holds 3:1 against the page');
  });
  test(`12 unordered series (${name}): the six steps first, then a ramp; no two colours alike`, () => {
    const r = seriesRamp(t.steps, t.bg, 12, false);
    assert.deepEqual(r.slice(0, 6), t.steps, 'the six steps come first');
    assert.ok(minDistance(r, true) >= MIN_NEIGHBOUR, `any two: ${minDistance(r, true).toFixed(3)}`);
    assert.ok(Math.min(...r.map((c) => contrastRatio(c, t.bg))) >= MIN_CONTRAST, 'every bar holds 3:1 against the page');
  });
  test(`6 series or fewer keep the palette steps (${name})`, () => {
    assert.deepEqual(seriesRamp(t.steps, t.bg, 5, false), t.steps.slice(0, 5));
  });
}

const frame = (names, rows) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1 });
const measure = { kind: 'measure', ref: '[Sales]', name: 'Sales' };
const col = (name, sortBy) => ({ kind: 'column', ref: `'Date'[${name}]`, name, table: 'Date', ...(sortBy ? { sortBy: { ref: `'Date'[${sortBy}]`, name: sortBy } } : {}) });
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const YEARS = [2023, 2024, 2025];
// Ask's answer for "sales by month and year": the engine groups by the asked fields only, and the model sorts Month by MonthNo.
const monthYear = frame(['Date[Month]', 'Date[Year]', 'Sales'], MONTHS.flatMap((m, mi) => YEARS.map((y, yi) => [m, y, 1000 + mi * 10 + yi])));
const askConfig = { rows: [col('Month', 'MonthNo'), col('Year')], cols: [], values: [measure], filters: [] };
const yearMonth = frame(['Date[Year]', 'Date[Month]', 'Sales'], monthYear.rows.map(([m, y, v]) => [y, m, v]));
const draw = (res, config, viz = 'bar') => { charts.length = 0; renderToStaticMarkup(React.createElement(DaxVisual, { res, config, viz, height: 320 })); return charts.at(-1); };
const rgb = (s) => s.match(/\d+/g).map(Number);

// ── (1) the split ───────────────────────────────────────────────────────────────────────────────────────────────
test('month by year as bars: the 12 months run along the axis and the 3 years are the 3 series', () => {
  const o = draw(monthYear, askConfig);
  assert.equal(o.xAxis.data.length, 12, 'twelve months along the axis');
  assert.deepEqual(o.series.map((s) => s.name), ['2023', '2024', '2025'], 'a bar series for each year');
});
test('year first, month second gives the same chart', () => {
  const o = draw(yearMonth, { ...askConfig, rows: [askConfig.rows[1], askConfig.rows[0]] });
  assert.equal(o.xAxis.data.length, 12);
  assert.equal(o.series.length, 3);
});
test('a person who put Year in Rows and Month in Columns keeps their choice', () => {
  const o = draw(yearMonth, { rows: [col('Year')], cols: [col('Month', 'MonthNo')], values: [measure], filters: [] });
  // the wells' own order: the rows (Year) are the categories, the columns (Month) the series. Reads the pivot as built.
  assert.equal(o.series.length, 12);
});
test('two categories as bars: the field with more values is the axis', () => {
  const d = (distinct) => ({ isDate: false, distinct });
  assert.deepEqual(twoSplitAxes([d(3), d(9)], 1, 'bar'), { axis: 1, series: 0 });
  assert.deepEqual(twoSplitAxes([d(9), d(3)], 1, 'bar'), { axis: 0, series: 1 });
  assert.deepEqual(twoSplitAxes([d(4), d(4)], 1, 'bar'), { axis: 0, series: 1 }, 'a tie keeps the order given');
});
test('a line still follows the date, whatever the counts', () => {
  const dt = (distinct) => ({ isDate: true, ordered: true, distinct });
  assert.deepEqual(twoSplitAxes([{ isDate: false, distinct: 9 }, dt(3)], 1, 'line'), { axis: 1, series: 0 });
});

// ── (2) on the chart ────────────────────────────────────────────────────────────────────────────────────────────
test('12 months as 12 series (a person put Month in Columns): twelve different colours, in order, none repeated', () => {
  const o = draw(yearMonth, { rows: [col('Year')], cols: [col('Month', 'MonthNo')], values: [measure], filters: [] });
  const colours = o.series.map((s) => s.itemStyle.color);
  assert.equal(colours.length, 12);
  assert.equal(new Set(colours).size, 12, `no colour twice: ${colours.join(' ')}`);
  assert.ok(minDistance(colours.map(rgb), true) >= 0.02, 'no two alike');
});
test('9 unordered series: the six steps and three more, none repeated', () => {
  const brands = Array.from({ length: 9 }, (_, i) => `Brand ${i + 1}`);
  const res = frame(['Date[Year]', 'Product[Brand]', 'Sales'], YEARS.flatMap((y) => brands.map((b, i) => [y, b, 100 + i])));
  const o = draw(res, { rows: [{ ...col('Year') }], cols: [{ kind: 'column', ref: "'Product'[Brand]", name: 'Brand', table: 'Product' }], values: [measure], filters: [] });
  const colours = o.series.map((s) => s.itemStyle.color);
  assert.equal(colours.length, 9);
  assert.equal(new Set(colours).size, 9, 'no colour twice');
});
