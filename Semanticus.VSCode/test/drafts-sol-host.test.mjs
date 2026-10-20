// m11/drafts-sol, the extension-host half of F3 and R-A for the Properties grid (src/propSet.ts, pure so it runs without VS Code).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ts = createRequire(root + '/package.json')('typescript');
const propSet = (() => {
  const js = ts.transpileModule(readFileSync(resolve(root, 'src', 'propSet.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
  const m = { exports: {} };
  new Function('module', 'exports', js)(m, m.exports);
  return m.exports;
})();
const { mergePropDescs, gridSetCalls, fenceOf } = propSet;

const P = (name, value, extra = {}) => ({ name, displayName: name, category: 'Basic', description: '', kind: 'string', value, options: [], readOnly: false, ...extra });

test('a selection whose objects differ carries each object\'s own value, in the order of the selection', () => {
  const merged = mergePropDescs([[P('Description', 'First words'), P('Name', 'A')], [P('Description', 'Second words'), P('Name', 'A')]]);
  const d = merged.find((p) => p.name === 'Description'), n = merged.find((p) => p.name === 'Name');
  assert.equal(d.varies, true); assert.equal(d.value, ''); assert.deepEqual(d.values, ['First words', 'Second words']);
  assert.equal(n.varies, false); assert.equal(n.values, undefined, 'a row whose objects agree has one value and no list');
});

test('F3: a batch write names every object\'s own starting value and the model it was typed for', () => {
  const refs = ['measure:T/A', 'measure:T/B'];
  const [call, ...rest] = gridSetCalls(refs, { name: 'Description', value: 'Common', expected: null, expectedAll: ['First words', 'Second words'], model: 'file:/m/Sales.bim' });
  assert.equal(rest.length, 0);
  assert.equal(call.method, 'setObjectProperties');
  assert.deepEqual(call.args, [refs, 'Description', 'Common', 'human', null, ['First words', 'Second words'], 'file:/m/Sales.bim']);
});

test('F3: a list of starting values that does not fit the selection is not sent (the engine would have to guess)', () => {
  const [call] = gridSetCalls(['measure:T/A', 'measure:T/B'], { name: 'Description', value: 'Common', expected: null, expectedAll: ['only one'], model: 'file:/m/Sales.bim' });
  assert.equal(call.args[5], null);
});

test('R-A: every grid route names the model; a key that is not the engine\'s shape names none (an older engine would refuse everything)', () => {
  const one = gridSetCalls(['measure:T/A'], { name: 'Description', value: 'x', expected: 'was', model: 'live:powerbi://x|sales' });
  assert.deepEqual(one[0].args, ['measure:T/A', 'Description', 'x', 'human', 'was', 'live:powerbi://x|sales']);
  assert.equal(gridSetCalls(['measure:T/A'], { name: 'Description', value: 'x', expected: 'was', model: '/m/Sales.bim' })[0].args[5], null);
  assert.equal(fenceOf('desktop:sales|localhost:1|g'), 'desktop:sales|localhost:1|g');
  assert.equal(fenceOf('name:Sales'), null);
});

test('F3: the format expression and the column data type compare each object\'s own value too', () => {
  const fx = gridSetCalls(['measure:T/A', 'measure:T/B'], { name: 'FormatStringExpression', value: '"0"', expected: null, expectedAll: ['one', 'two'], model: 'file:/m/S.bim' });
  assert.deepEqual(fx.map((c) => [c.method, c.args[0], c.args[3]]), [['setMeasureFormatExpression', 'measure:T/A', 'one'], ['setMeasureFormatExpression', 'measure:T/B', 'two']]);
  const dt = gridSetCalls(['column:T/A', 'measure:T/B', 'column:T/C'], { name: 'DataType', value: 'Int64', expected: null, expectedAll: ['String', 'x', 'Double'], model: 'file:/m/S.bim' });
  assert.deepEqual(dt.map((c) => [c.method, c.args[0], c.args[3]]), [['setColumnDataType', 'column:T/A', 'String'], ['setColumnDataType', 'column:T/C', 'Double'], ['setObjectProperties', ['measure:T/B'], 'human']]);
  assert.deepEqual(dt[2].args[5], ['x']);
});

// UAT 18 Oct N20: after the assistant renamed NetSalesAmount, Properties still showed the old name with "No properties match" until another object was picked.
// The change carries the new ref, the old name (before) and the new name (after), so the selection follows the object to its new ref.
test('N20: an assistant rename moves the grid\'s selection to the renamed ref, and nothing else moves it', () => {
  const { followRename } = propSet;
  const rename = { kind: 'update', ref: 'column:Sales/Net Sales Amount', props: ['Name'], before: 'NetSalesAmount', after: 'Net Sales Amount' };
  assert.deepEqual(followRename(['column:Sales/NetSalesAmount'], [rename]), ['column:Sales/Net Sales Amount']);
  assert.deepEqual(followRename(['column:Sales/NetSalesAmount', 'measure:Sales/Total'], [rename]), ['column:Sales/Net Sales Amount', 'measure:Sales/Total'], 'a multi-selection keeps its other objects');
  assert.equal(followRename(['measure:Sales/Total'], [rename]), null, 'another object selected: nothing moves');
  assert.equal(followRename(['column:Sales/NetSalesAmount'], [{ ...rename, props: ['Description'] }]), null, 'a description change is not a rename');
  assert.equal(followRename(['column:Sales/NetSalesAmount'], [{ kind: 'update', ref: 'column:Sales/NetSalesAmount', props: ['Name'] }]), null, 'a Name change with no old and new name is left to the next read');
  assert.deepEqual(followRename(['measure:Sales/Gross/Net'], [{ kind: 'update', ref: 'measure:Sales/Gross-Net', props: ['Name'], before: 'Gross/Net', after: 'Gross-Net' }]), ['measure:Sales/Gross-Net'], 'a slash in the name is part of the name');
});
