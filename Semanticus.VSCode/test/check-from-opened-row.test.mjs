// v2-fix-final-r3 (verifier): R2 on an OPENED row whose wells are stale. Runs the shipped openMeasureCheck from daxlab.tsx
// with the state the live lane had: wells = Net Sales by Category (row B, built last), editor = row A's own DAX
// (Net Sales by Brand and Year). Live, the drawer carried Product Category, guessed SQL by Category, and Try it now said
// "All 6 places compared match." on the Brand and Year row. Expected: the check carries Brand and Year.
// usage: node test/check-from-opened-row.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), '..'));
const ts = createRequire(join(root, 'webview', 'package.json'))('typescript');
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8').replace(/\r\n/g, '\n');
const cjs = (code) => ts.transpileModule(code, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
const mod = (code) => { const exports = {}; Function('exports', code)(exports); return exports; };
const { squash } = mod(cjs(src('daxsquash.ts')));
const { checkFieldsOfDax } = mod(cjs(src('query-check-fields.ts')));
const lab = src('daxlab.tsx');
const fn = (name) => { const m = lab.match(new RegExp(`(export )?function ${name}\\([\\s\\S]*?\\n\\}\\n`)); assert.ok(m, name + ' found'); return cjs(m[0].replace(/^export /, '')); };
const labQueryIsAbout = Function('squash', fn('labQueryIsAbout') + '; return labQueryIsAbout;')(squash);
const labExpressionEdit = Function('squash', fn('labExpressionEdit') + '; return labExpressionEdit;')(squash);

const model = {
  measures: [{ name: 'Net Sales', table: 'Sales' }, { name: 'Margin %', table: 'Sales' }],
  columns: [{ table: 'Product', name: 'Brand' }, { table: 'Date', name: 'Year' }, { table: 'Product', name: 'Category', sortByColumn: 'CategorySortOrder' }, { table: 'Product', name: 'CategorySortOrder' }],
};
const rowA = "EVALUATE\nSUMMARIZECOLUMNS(\n    'Product'[Brand],\n    'Date'[Year],\n    \"Net Sales\", [Net Sales]\n)";
const wellsB = "EVALUATE\nSUMMARIZECOLUMNS(\n    'Product'[Category],\n    'Product'[CategorySortOrder],\n    \"Net Sales\", [Net Sales]\n)\nORDER BY 'Product'[CategorySortOrder]";

// The shipped function body, run with the page state the lane had.
const body = lab.match(/  function openMeasureCheck\([\s\S]*?\n  \}\n/)[0];
let draft = null;
const ctx = {
  entry: 'dax', chipsHere: false, mode: 'query', query: rowA, generatedQuery: wellsB,
  selectedMeasureName: 'Net Sales', testMeasureRef: 'measure:Sales/Net Sales', model,
  config: { rows: [{ table: 'Product', name: 'Category' }], cols: [] }, contextRefOf: (f) => `column:${f.table}/${f.name}`,
  filterLinesDerived: [], editedExpression: labExpressionEdit('query', rowA, 'Net Sales', undefined, wellsB),
  labQueryIsAbout, checkFieldsOfDax, squash, setCheckDraft: (d) => { draft = d; }, setCheckShape() {}, setCheckMenu() {}, setCheckSaved() {},
};
const run = Function(...Object.keys(ctx), cjs(body) + '; openMeasureCheck("measureReconcile");');
run(...Object.values(ctx));
const p = JSON.parse(draft.paramsJson);
assert.equal(draft.targetRef, 'measure:Sales/Net Sales');
assert.deepEqual(p.groupBy, ['column:Product/Brand', 'column:Date/Year'], 'R2: the opened Brand and Year row carries Brand and Year, not the stale wells (live: Product Category)');
console.log('r2-stale-wells: ok');
