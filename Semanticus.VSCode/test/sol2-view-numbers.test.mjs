// Sol 6.1's review of the M11 delta (d141ce01..b118c6d9), the four findings about which format and which statement a shown
// number gets. Each block is Sol's own scenario, run against the real module, and each failed before its fix. The rule they
// share: a number is formatted, and a statement is shown over it, ONLY when it is proven to belong to that exact result;
// otherwise the plain number shows (no invented percent, no borrowed statement).
//   B2 an inner query column lends its percent format to a transformed outer column
//   B3 the assistant's run_dax result shows under the previous question's statement (second strike of 3.2)
//   B4 Queries applies the editing model's formats to a different connection's numbers
//   B6 a quoted literal percent sign scales the value by 100
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

const React = req('react');
const load = makeLoader({}, React);
const { withMeasureFormats, editingFormatsApply, measuresNotShadowed, sentFormatsNow, queriesLinkedCopy, measureFormatsFromDmv } = load('resultformats.ts');
const { plainNumber, displayValue } = await import('../webview/src/resultformat.mjs');
const { percentOf } = load('gridformat.ts');
const { answerOwnsResult } = load('resultownership.ts');
const src = (f) => readFileSync(new URL('../webview/src/' + f, import.meta.url), 'utf8');

const MEASURES = [
  { name: 'Return Rate %', table: 'Sales', formatString: '0.0%' },
  { name: 'Net Sales', table: 'Sales', formatString: '#,0' },
  { name: 'A', table: 'Sales', formatString: '0.0%' },
];
const typed = (query, names, rows = [names.map(() => 0.0068)]) => ({ columns: names.map((name) => ({ name })), rows, rowCount: rows.length, truncated: false, elapsedMs: 1, query });
const formatsOf = (query, names, measures = MEASURES) => withMeasureFormats(typed(query, names), query, measures).columns.map((c) => c.format);

// ── B2 ────────────────────────────────────────────────────────────────────────────────────────────────────
test('B2 an inner column\'s format is not lent to a transformed outer column of the same name (Sol\'s query)', () => {
  const q = 'EVALUATE SELECTCOLUMNS(\n  ROW("Return Rate %", [Return Rate %]),\n  "Return Rate %", [Return Rate %] * 100\n)';
  assert.deepEqual(formatsOf(q, ['[Return Rate %]']), [undefined], '0.68 percentage points must not read 68.0%');
});

test('B2 only the outermost table\'s own columns are looked up: a pair inside a nested table says nothing about the output', () => {
  // The outer column "B" is the inner row's column A (a column, not the measure [A]), and the inner pair is not the output.
  assert.deepEqual(formatsOf('EVALUATE SELECTCOLUMNS(ROW("A", [Net Sales]), "B", [A])', ['[B]']), [undefined], 'a column of the inner row that shares a measure\'s name');
  assert.deepEqual(formatsOf('EVALUATE SELECTCOLUMNS(ROW("A", [Return Rate %]), "A", [A] / 2)', ['[A]']), [undefined], 'projection with arithmetic');
  assert.deepEqual(formatsOf('EVALUATE ADDCOLUMNS(ROW("Return Rate %", [Return Rate %]), "x", 1)', ['[Return Rate %]', '[x]']), [undefined, undefined], 'a column passed through another table function is not proven');
  assert.deepEqual(formatsOf('EVALUATE ROW("v", COALESCE(ROW("Return Rate %", [Return Rate %]), 0))', ['[Return Rate %]']), [undefined], 'a pair buried in an argument');
  assert.deepEqual(formatsOf('EVALUATE UNION(ROW("Return Rate %", [Return Rate %]), ROW("Return Rate %", [Return Rate %] * 100))', ['[Return Rate %]']), [undefined], 'a function the lookup does not know');
});

test('B2 a measure whose name a column of the model also carries is not looked up (a bare [Name] reads the column in a row)', () => {
  const kept = measuresNotShadowed(MEASURES, [{ name: 'a' }, { name: 'Region' }]);
  assert.deepEqual(kept.map((m) => m.name), ['Return Rate %', 'Net Sales'], 'A is also a column, whatever its case');
  assert.match(src('daxlab.tsx'), /editingFormatsApply\(measuresNotShadowed\(model\.measures, model\.columns\)/, 'the page applies it');
});

test('B2 one alias twice, once bare and once transformed, is not a bare reference', () => {
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", [Return Rate %], "Return Rate %", [Return Rate %] * 100)', ['[Return Rate %]']), [undefined]);
});

test('B2 two statements in one run prove nothing about which one produced the rows', () => {
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", [Return Rate %] * 100)\nEVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), [undefined]);
});

test('B2 the plain cases the earlier fix covered still read their format, through the wrappers the lookup knows', () => {
  assert.deepEqual(formatsOf('EVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), ['0.0%']);
  assert.deepEqual(formatsOf("EVALUATE\n    SUMMARIZECOLUMNS ( 'Store'[Region], \"@Return Rate %\", [Return Rate %] )\nORDER BY 'Store'[Region]", ["'Store'[Region]", '[@Return Rate %]']), [undefined, '0.0%']);
  assert.deepEqual(formatsOf("EVALUATE TOPN(5, SUMMARIZECOLUMNS('Store'[Region], \"Rate\", [Return Rate %]), [Rate], DESC)", ["'Store'[Region]", '[Rate]']), [undefined, '0.0%'], 'TOPN keeps its table\'s columns');
  assert.deepEqual(formatsOf('EVALUATE SELECTCOLUMNS(Sales, "Rate", [Return Rate %])', ['[Rate]']), ['0.0%'], 'SELECTCOLUMNS with a bare reference');
  assert.deepEqual(formatsOf('DEFINE MEASURE Sales[Return Rate %] = 1 EVALUATE ROW("Return Rate %", [Return Rate %])', ['[Return Rate %]']), [undefined], 'a measure the query defines is not the model\'s');
});

// ── B3 ────────────────────────────────────────────────────────────────────────────────────────────────────
const Q2025 = 'EVALUATE ROW("v", CALCULATE([Net Sales], \'Date\'[Year] = 2025))';
const Q2024 = 'EVALUATE ROW("v", CALCULATE([Net Sales], \'Date\'[Year] = 2024))';

test('B3 the question\'s statement holds only while the rows on screen were produced by the answer\'s own query', () => {
  const base = { askOwns: true, chipDax: Q2025 };
  assert.equal(answerOwnsResult({ ...base, res: { query: Q2025 }, resQuery: Q2025 }), true, 'its own rows');
  assert.equal(answerOwnsResult({ ...base, res: { query: Q2024 }, resQuery: Q2024 }), false, 'the assistant ran 2024 and the editor still holds 2025 (Sol\'s scenario)');
  assert.equal(answerOwnsResult({ ...base, res: { query: Q2024 }, resQuery: null }), false, 'the result\'s own query is read when the page has none');
  assert.equal(answerOwnsResult({ ...base, res: { query: Q2025.replace('EVALUATE ROW', 'EVALUATE\n    ROW') }, resQuery: null }), true, 'layout is not a difference');
  assert.equal(answerOwnsResult({ ...base, res: null, resQuery: null }), true, 'no rows on screen: the statement describes the query in the editor');
  assert.equal(answerOwnsResult({ askOwns: false, chipDax: Q2025, res: { query: Q2025 }, resQuery: Q2025 }), false, 'a query changed by hand');
  assert.equal(answerOwnsResult({ askOwns: true, chipDax: Q2025, res: { rows: [] }, resQuery: undefined }), false, 'rows that name no query cannot be vouched for');
});

test('B3 the Queries page shows the statement through that ownership, not through the editor alone', () => {
  const lab = src('daxlab.tsx') + src('query.tsx');
  assert.match(lab, /answerOwnsResult\(/, 'the page asks whether the rows are the answer\'s');
  assert.doesNotMatch(lab, /current=\{ask\.answer\?\.dax \? askOwns : !res\}/, 'the statement is no longer bound to editor ownership alone');
  assert.match(lab, /<AskLines ask=\{q\.askBox\} current=\{q\.askState\.answer\?\.dax \? q\.answerRows : !q\.res\}/);
});

// ── B4 ────────────────────────────────────────────────────────────────────────────────────────────────────
test('B4 the editing model\'s formats are put on a run only when the query connection IS the editing model', () => {
  const ok = { relationship: 'sameInstance', hasUnsavedChanges: false };
  const mine = [{ name: 'Margin', formatString: '0.0%' }];
  assert.deepEqual(editingFormatsApply(mine, ok), mine, 'same live model, nothing unsaved');
  for (const relationship of ['twoModels', 'workingCopyAndPublished', 'workingCopyAndLocalRuntime', 'editingOnly', 'queryingOnly', 'none', undefined])
    assert.deepEqual(editingFormatsApply(mine, { ...ok, relationship }), [], 'relationship ' + relationship + ': another model\'s numbers');
  assert.deepEqual(editingFormatsApply(mine, { ...ok, hasUnsavedChanges: true }), [], 'unsaved edits may have changed a format the connection does not have');
  assert.deepEqual(editingFormatsApply(mine, null), []);
});

test('B4 Sol\'s scenario: a ratio measure in the editing model must not turn another model\'s 68 into 6,800.0%', () => {
  const q = 'EVALUATE ROW("Margin", [Margin])';
  const editing = [{ name: 'Margin', formatString: '0.0%' }];
  const rows = typed(q, ['[Margin]'], [[68]]);
  const apply = (ctx) => withMeasureFormats(rows, q, editingFormatsApply(editing, ctx)).columns[0].format;
  assert.equal(apply({ relationship: 'twoModels', hasUnsavedChanges: false }), undefined, 'a separate query connection shows the plain number');
  assert.equal(apply({ relationship: 'sameInstance', hasUnsavedChanges: false }), '0.0%', 'the editing model\'s own live connection still reads its format');
});

test('B4 the formats a run was sent with hold while the page is on that connection, and a model edit does not drop them', () => {
  const sent = { scope: '["conn","A"]', measures: [{ name: 'Margin', formatString: '0.0%' }] };
  assert.deepEqual(sentFormatsNow(sent, '["conn","A"]'), sent.measures, 'same connection (an edit changes the revision, which is not in the scope)');
  assert.deepEqual(sentFormatsNow(sent, '["conn","B"]'), [], 'the connection changed');
  assert.deepEqual(sentFormatsNow(null, '["conn","A"]'), [], 'rows no run of this page sent');
});

test('B4 a linked copy is proven by the query model\'s own measure formats, read from it', () => {
  assert.equal(queriesLinkedCopy('workingCopyAndLocalRuntime'), true);
  assert.equal(queriesLinkedCopy('workingCopyAndPublished'), true);
  for (const r of ['sameInstance', 'twoModels', 'editingOnly', 'queryingOnly', 'none', undefined]) assert.equal(queriesLinkedCopy(r), false, String(r));
  const dmv = { columns: [{ name: '[Name]' }, { name: '[FormatString]' }], rows: [['Return Rate %', '0.0%'], ['Net Sales', null]], rowCount: 2, truncated: false, elapsedMs: 1 };
  assert.deepEqual(measureFormatsFromDmv(dmv), [{ name: 'Return Rate %', formatString: '0.0%' }, { name: 'Net Sales', formatString: null }]);
  assert.equal(measureFormatsFromDmv({ ...dmv, error: 'no' }), null, 'a failed read proves nothing');
  assert.equal(measureFormatsFromDmv({ ...dmv, columns: [{ name: '[x]' }] }), null, 'not that table');
  assert.equal(measureFormatsFromDmv(null), null);
  const q = 'EVALUATE ROW("Return Rate %", [Return Rate %])';
  assert.deepEqual(withMeasureFormats(typed(q, ['[Return Rate %]']), q, measureFormatsFromDmv(dmv)).columns.map((c) => c.format), ['0.0%'], 'the default setup reads 0.7% again');
});

test('B4 the page notes what a run was SENT with at the moment it goes out, never at arrival', () => {
  const lab = src('daxlab.tsx');
  assert.match(lab, /editingFormatsApply\(measuresNotShadowed\(model\.measures, model\.columns\), \{/, 'the page filters the editing model\'s formats through the check');
  assert.doesNotMatch(lab, /withMeasureFormats\(res, resQuery \?\? res\.query, model\.measures\)/, 'the editing model\'s formats are never handed over unchecked');
  const run = lab.slice(lab.indexOf('async function runQuery('), lab.indexOf('function stopQuery('));
  assert.ok(run.indexOf('formatsAtSend(sentIn)') > 0 && run.indexOf('formatsAtSend(sentIn)') < run.indexOf("await rpc<ResultSet>('runDax'"), 'runQuery fixes the formats before the query goes out');
  assert.match(lab, /sentFormatsNow\(sentFormats, formatScopeKey\)/, 'the page reads only the formats the rows were sent with');
});

test('B2 names the query defines are never a model table, and every name is compared without case', () => {
  const m = [{ name: 'Return Rate %', formatString: '0.0%' }];
  const f = (q) => withMeasureFormats(typed(q, ['[Return Rate %]']), q, m).columns[0].format;
  assert.equal(f('DEFINE VAR t = ROW("Return Rate %", [Return Rate %] * 100) EVALUATE SELECTCOLUMNS(t, "Return Rate %", [Return Rate %])'), undefined, 'VAR');
  assert.equal(f('DEFINE VAR T = ROW("Return Rate %", [Return Rate %] * 100) EVALUATE SELECTCOLUMNS(t, "Return Rate %", [Return Rate %])'), undefined, 'VAR in another case');
  assert.equal(f("DEFINE TABLE 'My T' = ROW(\"Return Rate %\", [Return Rate %] * 100) EVALUATE SELECTCOLUMNS('My T', \"Return Rate %\", [Return Rate %])"), undefined, 'a quoted DEFINE TABLE');
  assert.equal(f('EVALUATE SELECTCOLUMNS(Sales, "Return Rate %", [Return Rate %])'), '0.0%', 'a model table still reads');
  assert.equal(f('DEFINE MEASURE Sales[RETURN RATE %] = 1 EVALUATE ROW("Return Rate %", [Return Rate %])'), undefined, 'a measure redefined in another case');
  assert.equal(f('EVALUATE ROW("return rate %", [return rate %])'), '0.0%', 'a model measure named in another case');
});

test('a plain number is never rounded away: 0.0068 reads 0.0068, a large figure keeps two decimals', () => {
  assert.equal(plainNumber(0.0068119056469534), '0.006812');
  assert.equal(plainNumber(0.00068), '0.00068');
  assert.equal(plainNumber(1.0068), '1.007');
  assert.equal(plainNumber(12.3456), '12.35');
  assert.equal(plainNumber(1234.5678), '1,234.57');
  assert.equal(plainNumber(0), '0');
  assert.equal(plainNumber(-0.0068119), '-0.006812');
  assert.equal(displayValue(0.0068119056469534), '0.006812', 'the grid\'s plain path');
});

// ── B6 ────────────────────────────────────────────────────────────────────────────────────────────────────
test('B6 a quoted or escaped percent sign is text, not a scaling operator (Sol\'s 12.3)', () => {
  assert.equal(percentOf(12.3, '0.0"%"'), '12.3%', 'the model\'s intended display');
  assert.equal(percentOf(12.3, '0.0\\%'), '12.3%', 'an escaped sign is text too');
  assert.equal(percentOf(12.3, '0.0"%";-0.0"%"'), '12.3%');
  assert.equal(percentOf(-12.3, '0.0"%";-0.0"%"'), '-12.3%');
  assert.equal(percentOf(0.123, '0.0%'), '12.3%', 'a real operator still scales');
});

test('B6 the section that applies to the value is the one honoured', () => {
  // Positive, negative, zero: each reads its own section. A negative section without a percent operator is not scaled.
  assert.equal(percentOf(0.5, '0.0%;0.0'), '50.0%');
  assert.equal(percentOf(-0.5, '0.0%;0.0'), null, 'the negative section is a plain number: show the number as it is');
  assert.equal(percentOf(-12.3, '0.0%;0.0"%"'), '12.3%', 'a literal sign in the negative section is not scaled');
  assert.equal(percentOf(-0.125, '0.0%;-0.0%;0.0%'), '-12.5%');
  assert.equal(percentOf(-0.125, '0.0%'), '-12.5%', 'one section: the sign is added');
  assert.equal(percentOf(0, '0.0%;-0.0%;"none"'), null, 'zero reads the third section, which has no percent');
  assert.equal(percentOf(0, '0.0%;-0.0%'), '0.0%', 'two sections: zero reads the first');
});

test('B6 a format the reader does not fully understand shows the raw number', () => {
  for (const f of ['[Red]0.0%', '0.0%E+00', '[>=1]0%;0.0', '0.0%_)', 'General%', '0,.0%', '0.0 "x" 0.0%', '', '0.0"%', '0.0\\'])
    assert.equal(percentOf(0.5, f), null, JSON.stringify(f));
  assert.equal(percentOf(0.5, '#,0'), null);
  assert.equal(percentOf(0.5, '$#,0.00'), null);
});

test('B6 the formats the product shows today still read the same', () => {
  assert.equal(percentOf(0.0068119056469534, '0.0%;-0.0%;0.0%'), '0.7%');
  assert.equal(percentOf(0.0068119056469534, '0.00%'), '0.68%');
  assert.equal(percentOf(0.087, '0%'), '9%');
  assert.equal(percentOf(1.236, '#,0.0%'), '123.6%');
  assert.equal(percentOf(12.36, '#,0.0%'), '1,236.0%');
  assert.equal(percentOf(0.5, '0.0%;(0.0%)'), '50.0%');
  assert.equal(percentOf(-0.5, '0.0%;(0.0%)'), '(50.0%)');
});

test('B6 a locale or currency code in front of a percent format is read, not shown raw', () => {
  assert.equal(percentOf(0.5, '[$-en-US]0.00%'), '50.00%');
  assert.equal(percentOf(0.5, '[$-409]0%'), '50%');
  assert.equal(percentOf(0.5, '[$€-407]0.0%'), '€50.0%');
  assert.equal(percentOf(0.5, '[Red]0.0%'), null, 'a colour is still not read');
  assert.equal(percentOf(0.5, '[$-en-US0.0%'), null, 'an unclosed bracket is not read');
});
