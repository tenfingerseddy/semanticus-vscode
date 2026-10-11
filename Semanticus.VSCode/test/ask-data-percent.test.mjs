// A percent measure reads as a percent in the Ask your data grid (round 6, L5). The verifier's live shot showed Return Rate %
// as 0.0068119056469534, which made the wrong number (a share of value, not of orders) harder to catch by eye. The engine sends
// each measure column's own format with the answer (DataAnswer.Formats); the grid shows a percent format as a percent and
// leaves every other cell exactly as it was. Sorting and filtering still see the number.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const ts = createRequire(join(webview, 'package.json'))('typescript');

const js = ts.transpileModule(src('gridformat.ts'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const mod = { exports: {} };
new Function('module', 'exports', js)(mod, mod.exports);
const { percentOf } = mod.exports;

test('a percent format shows the number as a percent, with the decimals the format asks for', () => {
  assert.equal(percentOf(0.0068119056469534, '0.0%;-0.0%;0.0%'), '0.7%');
  assert.equal(percentOf(0.0068119056469534, '0.00%'), '0.68%');
  assert.equal(percentOf(0.087, '0%'), '9%');
  assert.equal(percentOf(-0.125, '0.0%;-0.0%;0.0%'), '-12.5%');
  assert.equal(percentOf(1.236, '#,0.0%'), '123.6%');
});

test('any other format, or none, leaves the number alone', () => {
  assert.equal(percentOf(0.5, '$#,0.00;($#,0.00);$#,0.00'), null);
  assert.equal(percentOf(0.5, '#,0'), null);
  assert.equal(percentOf(0.5, undefined), null);
  assert.equal(percentOf(0.5, null), null);
  assert.equal(percentOf(0.5, ''), null);
});

test('the grid reads the format from the column, and the page hands it the answer\'s formats', () => {
  const grid = src('grid.tsx');
  assert.match(grid, /cell\(v, columns\[ci\]\?\.format\)/, 'the cell must show the column\'s format');
  assert.match(src('askdata.tsx'), /format: answer\.formats\?\.\[i\] \?\? undefined/, 'the page must pass each measure column\'s format to the grid');
  assert.match(src('askdata-model.ts'), /formats\?: \(string \| null\)\[\] \| null/);
});

// Hero run 4: a date came out as "2025-01-01T00:00:00" in the Ask answer and in Queries.
const { dateText } = mod.exports;

test('a date shows in the column\'s own format, or as a plain short date when it has none', () => {
  assert.equal(dateText('2025-01-01T00:00:00', 'dd/MM/yyyy'), '01/01/2025');
  assert.equal(dateText('2025-03-07T00:00:00', 'd MMM yyyy'), '7 Mar 2025');
  assert.equal(dateText('2025-03-07T00:00:00', 'MMMM yyyy'), 'March 2025');
  assert.equal(dateText('2025-03-07T00:00:00', 'dddd, d MMMM yyyy'), 'Friday, 7 March 2025');
  assert.equal(dateText('2025-03-07T00:00:00', 'yyyy-MM'), '2025-03');
  assert.equal(dateText('2025-01-01T00:00:00', null), '1 Jan 2025');
  assert.equal(dateText('2025-01-01T00:00:00', '#,0.00'), '1 Jan 2025', 'a number format on a date column is no date format');
  assert.equal(dateText('2025-01-01T00:00:00', 'General Date'), '1 Jan 2025');
  assert.equal(dateText('2025-01-01T00:00:00', 'Long Date'), 'Wednesday, 1 January 2025');
});

test('a time shows only when the value has one', () => {
  assert.equal(dateText('2025-01-01T14:05:00', undefined), '1 Jan 2025 14:05');
  assert.equal(dateText('2025-01-01T00:00:00', undefined), '1 Jan 2025');
  assert.equal(dateText('2025-01-01T14:05:09', 'dd/MM/yyyy HH:mm:ss'), '01/01/2025 14:05:09');
  assert.equal(dateText('2025-01-01T14:05:00', 'h:mm tt'), '2:05 PM');
});

test('anything that is not a date is left alone', () => {
  assert.equal(dateText('Coast', 'dd/MM/yyyy'), null);
  assert.equal(dateText(2025, 'dd/MM/yyyy'), null);
  assert.equal(dateText('2025', null), null);
  assert.equal(dateText(null, null), null);
});

test('the grid shows a date through dateText in the Ask answer and in Queries', () => {
  const grid = src('grid.tsx');
  assert.match(grid, /dateText\(v, format\)/, 'cell() must show a date through dateText');
  assert.match(grid, /dateText\(v, columns\[ci\]\?\.format\)/, 'the friendly grid must honour a date column\'s own format');
});
