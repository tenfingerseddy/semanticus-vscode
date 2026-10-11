// Verifier, lane m11/sol2-view (Sol delta B2, B3, B4, B6). Each block is a sibling of a finding the lane fixed, in the same
// class: a number is formatted only when proven to belong to that exact result, else the plain number. Left failing on
// purpose; each says what it shows.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

const load = makeLoader({}, req('react'));
const { withMeasureFormats } = load('resultformats.ts');
const { displayValue } = await import('../webview/src/resultformat.mjs');
const lab = readFileSync(new URL('../webview/src/daxlab.tsx', import.meta.url), 'utf8');

const M = [{ name: 'Return Rate %', table: 'Sales', formatString: '0.0%' }];
const formatOf = (q) => withMeasureFormats({ columns: [{ name: '[Return Rate %]' }], rows: [[0.68]], rowCount: 1, truncated: false, elapsedMs: 1, query: q }, q, M).columns[0].format;

// B4 sibling, a connection switched mid-run. runQuery awaits runDax and the holder stamps the rows with the context current
// when they ARRIVE. Run on a separate test model, switch the query connection to the editing model's own live instance while
// it runs: the test model's Margin 68 lands stamped "sameInstance" and reads 6,800.0%. The builder's own note 1.
test('B4 sibling: a result is stamped with the context its query was SENT in, or a context change orphans the run', () => {
  const run = lab.slice(lab.indexOf('async function runQuery('), lab.indexOf('function stopQuery('));
  const sentIn = /=\s*execContextKey[\s\S]*await rpc<ResultSet>\('runDax'/.test(run);
  const orphaned = /useEffect\(\(\) => \{[^}]*runGen\.current\+\+[^}]*\}, \[[^\]]*execContextKey[^\]]*\]\)/.test(lab);
  assert.ok(sentIn || orphaned, 'rows from the test model must never be stamped with the editing model\'s context');
});

// B2 sibling, another wrapper: Sol's query with the inner table moved into a variable. The lane refuses
// SELECTCOLUMNS(ROW(...)) because, by its own rule (resultformats.ts, measuresNotShadowed), a bare [Name] over a table reads
// that table's column before a measure. A variable or query table is a "plain table name" to the lookup, so the column
// holding 0.68 percentage points borrows the measure's 0.0% and reads 68.0%. PLAUSIBLE: rests on DAX name resolution, unproven live.
test('B2 sibling: a query variable or query table is not a model table, its columns are not the measure', () => {
  assert.equal(formatOf('DEFINE VAR t = ROW("Return Rate %", [Return Rate %] * 100) EVALUATE SELECTCOLUMNS(t, "Return Rate %", [Return Rate %])'), undefined, 'VAR');
  assert.equal(formatOf('DEFINE TABLE T2 = ROW("Return Rate %", [Return Rate %] * 100) EVALUATE SELECTCOLUMNS(T2, "Return Rate %", [Return Rate %])'), undefined, 'DEFINE TABLE');
});

// B2 class, pre-existing at b118c6d9: DAX names are case-blind, so a query measure spelled in another case REPLACES the
// model's measure, yet the "own" check is case-exact and the model's 0.0% lands on the redefined value (0.68 reads 68.0%).
test('B2 class: a measure the query redefines in another case is not the model\'s', () => {
  assert.equal(formatOf('DEFINE MEASURE Sales[return rate %] = DIVIDE([Returns], [Sales]) * 100 EVALUATE ROW("Return Rate %", [Return Rate %])'), undefined);
});

// Note 3, the plain fallback. After B4, every working-copy setup (the default, and the driver's scene 11c) shows a ratio
// measure through this path: 0.0068 under "Return rate %" reads 0.01, the exact display Sol 3.1 called a wrong number.
// The root rule says the PLAIN number; 0.01 is not 0.0068.
test('note 3: the plain number keeps a small ratio\'s value, it does not round 0.0068 to 0.01', () => {
  assert.notEqual(displayValue(0.0068119056469534), '0.01');
});
