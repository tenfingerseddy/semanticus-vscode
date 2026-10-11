// M11 join part 5, the number-display items from the verifier's second round on m11/sol2-view (v-sol2-view-2.md items 1 and 3).
//   J2  a column the query defines itself (DEFINE COLUMN) never borrows a measure's format
//   J3  the Tests page never rounds a plain number away (0.0068 read 0.01), and no other result value is shown at fixed decimals
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

const React = req('react');
const load = makeLoader({}, React);
const { withMeasureFormats } = load('resultformats.ts');
const { resultText } = load('testresulttext.ts');

const MEASURES = [{ name: 'Return Rate %', table: 'Sales', formatString: '0.0%' }];
const typed = (query, names) => ({ columns: names.map((name) => ({ name })), rows: [names.map(() => 0.68)], rowCount: 1, truncated: false, elapsedMs: 1, query });
const formatsOf = (query, names) => withMeasureFormats(typed(query, names), query, MEASURES).columns.map((c) => c.format);

test('J2 a DEFINE COLUMN of a measure\'s name is the query\'s own column: it borrows no format (the verifier\'s query)', () => {
  const q = 'DEFINE COLUMN Customer[Return Rate %] = [Return Rate %] * 100\nEVALUATE SELECTCOLUMNS(Customer, "Return Rate %", [Return Rate %])';
  assert.deepEqual(formatsOf(q, ['[Return Rate %]']), [undefined], '0.68 must not read 68.0%');
});

test('J2 the same, quoted table, lower case, and any case of the name', () => {
  assert.deepEqual(formatsOf('define column \'Customer\'[RETURN RATE %] = 1\nEVALUATE SELECTCOLUMNS(Customer, "Return Rate %", [return rate %])', ['[Return Rate %]']), [undefined]);
  assert.deepEqual(formatsOf('DEFINE COLUMN Customer[Return Rate %] = 1 EVALUATE ADDCOLUMNS(Customer, "Return Rate %", [Return Rate %])', ['[Return Rate %]']), [undefined]);
});

test('J2 control: a DEFINE COLUMN of another name leaves the measure\'s format alone, and no DEFINE COLUMN still formats', () => {
  assert.deepEqual(formatsOf('DEFINE COLUMN Customer[Other] = 1 EVALUATE SELECTCOLUMNS(Customer, "Return Rate %", [Return Rate %])', ['[Return Rate %]']), ['0.0%']);
  assert.deepEqual(formatsOf('EVALUATE SUMMARIZECOLUMNS("Return Rate %", [Return Rate %])', ['[Return Rate %]']), ['0.0%']);
});

test('J3 the Tests page shows a plain number the way every other surface does: 0.0068 is never 0.01', () => {
  assert.equal(resultText(0.0068119), '0.006812');
  assert.equal(resultText(0.0068), '0.0068');
  assert.equal(resultText(1.0068), '1.007');
  assert.equal(resultText(-0.0068), '-0.0068');
  assert.equal(resultText(42), '42');
  assert.equal(resultText(0), '0');
  assert.equal(resultText(null), null);
  assert.equal(resultText(undefined), null);
});

test('J3 a fractional amount of money keeps its cents, and a delta is not rounded away either', () => {
  assert.equal(resultText(1234.5), '1,234.50');
  assert.equal(resultText(12.5, true), '12.50');
  assert.equal(resultText(1234.5678), '1,234.57');
  assert.equal(resultText(0.001, true), '0.001');
  assert.equal(resultText(1e-9, true), '0.000000001');
});

test('J3 the Tests page reads its numbers through resultText, and no webview result display rounds to fixed decimals', () => {
  const dir = new URL('../webview/src/', import.meta.url);
  const page = readFileSync(new URL('tests.tsx', dir), 'utf8');
  assert.match(page, /from '\.\/testresulttext'/, 'tests.tsx must use the shared reading');
  assert.doesNotMatch(page, /minimumFractionDigits:\s*2,\s*maximumFractionDigits:\s*2/, 'no fixed two decimals left on the page');
  // The pages that show a query's or a check's result value (Queries' own toFixed is a timing in milliseconds). Every other toFixed in the webview is a size, a score, a position or a duration.
  for (const f of ['daxvisual.tsx', 'explainpanel.tsx', 'askdata.tsx', 'grid.tsx', 'tests.tsx']) {
    assert.doesNotMatch(readFileSync(new URL(f, dir), 'utf8'), /\.toFixed\(/, f + ' shows a result value with toFixed');
  }
});
