// Join part 6, the webview's own fixes (each block failed on the merged tree and passes with its fix).
//   J4a  a saved check's groups and the Compare chips say the grain plainly when a grouped field has a sort-by column in the model
//   J4b  the chart's legend does not draw over the last axis label (grid.bottom leaves room for the labels and the legend)
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, req, webview } from './helpers/webview-load.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
globalThis.document = { documentElement: { style: {} } };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });

const load = makeLoader({});
const { grainWords, sortOrderedFields, columnOfRef } = load('sortgrain.ts');

const columns = [
  { table: 'Date', name: 'Fiscal Month', sortByColumn: 'Fiscal Month Number' },
  { table: 'Date', name: 'Fiscal Month Number' },
  { table: 'Date', name: 'Year', sortByColumn: 'Year' },        // sorted by itself: no separate order
  { table: 'Product', name: 'Category' },
  { table: "O'Brien", name: 'Region', sortByColumn: 'Region Order' },
];

test('J4a both reference spellings are read', () => {
  assert.deepEqual(columnOfRef("'Date'[Fiscal Month]"), { table: 'Date', name: 'Fiscal Month' });
  assert.deepEqual(columnOfRef('column:Date/Fiscal Month'), { table: 'Date', name: 'Fiscal Month' });
  assert.deepEqual(columnOfRef("'O''Brien'[Region]"), { table: "O'Brien", name: 'Region' });
  assert.equal(columnOfRef('[Margin %]'), null);
});

test('J4a a field the model sorts by another column is named, in plain words', () => {
  const words = grainWords(["'Date'[Fiscal Month]", "'Product'[Category]"], columns);
  assert.match(words, /^Compared per Fiscal Month, without its sort order\./);
  assert.match(words, /ALL on the field/);
  assert.equal(grainWords(['column:Date/Fiscal Month'], columns).startsWith('Compared per Fiscal Month, without its sort order.'), true, 'engine refs read the same');
  assert.match(grainWords(["'Date'[Fiscal Month]", "'O''Brien'[Region]"], columns), /^Compared per Fiscal Month and Region, without their sort order\./);
  assert.ok(!/—/.test(words), 'no em dash');
});

test('J4a nothing is said when no grouped field has its own sort column, or the model has not loaded', () => {
  assert.equal(grainWords(["'Product'[Category]", "'Date'[Year]"], columns), null);
  assert.equal(grainWords(["'Date'[Fiscal Month]"], []), null);
  assert.equal(grainWords([], columns), null);
  assert.deepEqual(sortOrderedFields(["'DATE'[fiscal month]"], columns), ['Fiscal Month'], 'case does not hide it');
});

test('J4a the saved check form and the Compare card show that sentence', () => {
  const authoring = readFileSync(join(webview, 'src', 'tests-authoring.tsx'), 'utf8');
  assert.match(authoring, /const grain = grainWords\(groupBy, model\.columns\)/);
  assert.match(authoring, /data-testid="check-grain">\{grain\}/);
  const lab = readFileSync(join(webview, 'src', 'daxlab.tsx'), 'utf8');
  assert.match(lab, /const grain = grainWords\(groupByDerived, useDaxModelContext\(\)\.columns\)/);
  assert.match(lab, /data-testid="compare-grain">\{grain\}/);
});

test('J4b the legend has room under the axis labels', () => {
  const visual = readFileSync(join(webview, 'src', 'daxvisual.tsx'), 'utf8');
  const room = Number(/export const LEGEND_ROOM = (\d+);/.exec(visual)?.[1]);
  // labels hang about 23px under the plot and a scroll legend is about 25px tall: anything under 48 draws one over the other
  assert.ok(room >= 52, `LEGEND_ROOM is ${room}`);
  assert.match(visual, /bottom: showLegend \? LEGEND_ROOM : 24/);
  assert.doesNotMatch(visual, /bottom: showLegend \? 38 : 24/);
});
