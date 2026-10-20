// fix2-uat-validation: the grouped check leads with how the places came out, a row that could not be counted has one
// word everywhere, and a bare figure in a title reads with its separators in the visual too.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
async function load(file) {
  const out = ts.transpileModule(readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(out, 'utf8').toString('base64')}`);
}
const model = await load(resolve(root, 'webview/src/tests-model.ts'));
const vm = await load(resolve(root, 'webview/src/validation-model.ts'));

assert.equal(model.groupedLeadSentence(0, 3, 'Northstar Source'), 'The model matches Northstar Source in all 3 places it compared.');
assert.equal(model.groupedLeadSentence(2, 1, 'Northstar Source'), 'The model differs from Northstar Source in 2 of 3 places.');

const tried = { canCount: true, source: 'mapping', lastVerdict: 'NotVerifiable', lastModelCount: null, lastSourceCount: null };
const waiting = { canCount: true, source: 'mapping', lastVerdict: null, lastModelCount: null, lastSourceCount: null };
const ok = { canCount: true, source: 'mapping', lastVerdict: 'Pass', lastModelCount: 5, lastSourceCount: 5 };
assert.equal(model.countsSummaryLine([ok, tried]), '1 match, 0 differ, 1 could not be counted');
assert.equal(model.countsSummaryLine([ok, waiting]), '1 match, 0 differ, 1 not counted yet');
assert.equal(model.countsFamilyFinding([tried]), '1 could not be counted', 'the row finding uses the same words');

const outcome = { verdict: 'Pass', expected: '75000000', actual: '75000000', difference: 0, rows: [] };
const v = vm.checkVisual(outcome, { trusted: true, sourceLabel: 'x', title: 'Net Sales totals 75000000', expectedText: '75000000' });
assert.equal(v.data.rows[0].label, 'Net Sales totals 75,000,000');
assert.equal(v.data.total.label, 'Net Sales totals 75,000,000');
// Saving a second connection keeps the model that answers the checks.
const hub = await load(resolve(root, 'webview/src/connectionshub-model.ts'));
const northstar = { available: true, live: true, kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/FrameworkTesting', database: 'Northstar Sales Working', authMode: 'serviceprincipal', tenantId: 't1' };
assert.deepEqual(hub.testsTargetToKeep(northstar, { endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Semanticus Demo', database: 'Contoso Sales' }),
  { endpoint: northstar.endpoint, database: northstar.database, authMode: 'serviceprincipal', tenantId: 't1' });
assert.equal(hub.testsTargetToKeep(northstar, { endpoint: northstar.endpoint, database: 'northstar sales working' }), null, 'the model just added is the one answering');
assert.equal(hub.testsTargetToKeep({ ...northstar, kind: 'localDesktop' }, { endpoint: 'x', database: 'y' }), null, 'a copy of Power BI Desktop is not put back by address');
assert.equal(hub.testsTargetToKeep({ ...northstar, live: false }, { endpoint: 'x', database: 'y' }), null);
assert.equal(hub.testsTargetToKeep(null, { endpoint: 'x', database: 'y' }), null);
// Query > Save > Check against SQL: a typed query that is not about the picked calculation opens the check empty (v86).
const daxlab = readFileSync(resolve(root, 'webview/src/daxlab.tsx'), 'utf8');
const m = daxlab.match(/function labQueryIsAbout\([\s\S]*?\n\}/);
assert.ok(m, 'labQueryIsAbout stays a top-level helper');
const squashJs = ts.transpileModule(readFileSync(resolve(root, 'webview/src/daxsquash.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
const js = ts.transpileModule(m[0], { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText;
const about = Function(`"use strict"; const { squash } = (() => { const exports = {}; ${squashJs}; return exports; })(); ${js}; return labQueryIsAbout;`)();
assert.equal(about('query', 'EVALUATE ROW("Rows", COUNTROWS(fact_sales))', 'Average list price', 'EVALUATE ROW("Average list price", [Average list price])'), false,
  'COUNTROWS(fact_sales) is not about Average list price');
assert.equal(about('query', 'EVALUATE ROW("x", [Average list price] * 2)', 'Average list price', ''), true, 'a query that names the calculation is about it');
assert.equal(about('visual', 'anything', 'Average list price', ''), true, 'the wells are the query in Build');
assert.equal(about('query', 'EVALUATE ROW("a", [A])', 'A', 'EVALUATE  ROW("a", [A])'), true, 'the lab own writing is its wells');
// R2 (UAT 18 Oct, round 3): an opened Query row carries its own measure and groups into Check against SQL.
const qc = await load(resolve(root, 'webview/src/query-check-fields.ts'));
const nsModel = {
  measures: [{ name: 'Net Sales', table: 'Sales' }, { name: 'Margin %', table: 'Sales' }],
  columns: [{ table: 'Product', name: 'Brand' }, { table: 'Date', name: 'Year' }, { table: 'Product', name: 'Category', sortByColumn: 'CategorySortOrder' }, { table: 'Product', name: 'CategorySortOrder' }],
};
assert.deepEqual(qc.checkFieldsOfDax(`EVALUATE\nSUMMARIZECOLUMNS(\n    'Product'[Brand],\n    'Date'[Year],\n    "Net Sales", [Net Sales]\n)`, nsModel),
  { measureRef: 'measure:Sales/Net Sales', groupBy: ['column:Product/Brand', 'column:Date/Year'], filterDax: '' }, 'Net Sales by Brand and Year');
assert.deepEqual(qc.checkFieldsOfDax(`EVALUATE\nSUMMARIZECOLUMNS(\n    'Product'[Category],\n    'Product'[CategorySortOrder],\n    "Margin %", [Margin %]\n)\nORDER BY 'Product'[CategorySortOrder]`, nsModel),
  { measureRef: 'measure:Sales/Margin %', groupBy: ['column:Product/Category'], filterDax: '' }, 'a column added only to order another is not a group');
assert.deepEqual(qc.checkFieldsOfDax(`EVALUATE\nCALCULATETABLE(\n    SUMMARIZECOLUMNS(\n        Product[Brand],\n        "Net Sales", [Net Sales]\n    ),\n    'Date'[Year] = 2025\n)`, nsModel),
  { measureRef: 'measure:Sales/Net Sales', groupBy: ['column:Product/Brand'], filterDax: "'Date'[Year] = 2025" }, 'its filters travel');
assert.deepEqual(qc.checkFieldsOfDax('EVALUATE ROW("Net Sales", [Net Sales])', nsModel), { measureRef: 'measure:Sales/Net Sales', groupBy: [], filterDax: '' });
assert.equal(qc.checkFieldsOfDax('EVALUATE ROW("Rows", COUNTROWS(fact_sales))', nsModel), null, 'a hand-written query is not guessed at');
assert.equal(qc.checkFieldsOfDax('EVALUATE SUMMARIZECOLUMNS(Product[Nope], "Net Sales", [Net Sales])', nsModel), null, 'a field the model lacks is not guessed at');
assert.match(daxlab, /checkFieldsOfDax\(query, model\)/, 'Check against SQL reads the open row\'s query when the wells are not about it');
// R3: a tick belongs to the field, not to what the search shows now.
const pick = await load(resolve(root, 'webview/src/objectbrowser-pick.ts'));
const all = [{ ref: 'a/Brand' }, { ref: 'a/Year' }, { ref: 'a/Region' }];
assert.deepEqual(pick.tickedNodes(all, new Set(['a/Brand', 'a/Year'])).map((n) => n.ref), ['a/Brand', 'a/Year'], 'Brand stays ticked after a search for year');
assert.deepEqual(pick.tickedNodes(all, new Set(['a/Year', 'a/Brand']), new Set(['a/Year'])).map((n) => n.ref), ['a/Brand'], 'one already added is not offered again');
assert.match(readFileSync(resolve(root, 'webview/src/objectbrowser.tsx'), 'utf8'), /tickedNodes\(nodes, sel, pickedRefs\)/, 'the browser reads ticks from every field');
console.log('validation-fix2: ok');
