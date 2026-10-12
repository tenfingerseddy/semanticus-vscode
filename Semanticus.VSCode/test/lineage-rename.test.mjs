// N11 (hero16b): Claude renames the field the Impact page shows. The page follows the rename, and when the field is
// gone it says so in plain words and falls back to the list. It never prints "column:Store/Store City not found."
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

const require = createRequire(new URL('../webview/package.json', import.meta.url));
const ts = require('typescript');
const read = (p) => readFileSync(new URL(p, import.meta.url), 'utf8');
const { outputText } = ts.transpileModule(read('../webview/src/lineage-model.ts'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } });
const mod = { exports: {} };
new Function('require', 'module', 'exports', outputText)(require, mod, mod.exports);
const { followRename, impactErrorLine, GONE_FROM_IMPACT } = mod.exports;

const rename = (ref, before) => ({ kind: 'update', ref, props: ['Name'], before, after: ref.split('/').pop() });

test('a renamed column keeps the page on the same column', () => {
  assert.equal(followRename('column:Store/Store City', [rename('column:Store/Shop City', 'Store City')]), 'column:Store/Shop City');
});

test('a batch of renames follows only the shown field', () => {
  const deltas = [rename('column:Store/Store Code', 'Code'), rename('column:Store/Shop City', 'Store City'), rename('measure:Sales/Net', 'Net Sales')];
  assert.equal(followRename('column:Store/Store City', deltas), 'column:Store/Shop City');
});

test('a renamed table moves the field under it', () => {
  assert.equal(followRename('column:Store/Store City', [rename('table:Shop', 'Store')]), 'column:Shop/Store City');
  assert.equal(followRename('table:Store', [rename('table:Shop', 'Store')]), 'table:Shop');
});

test('other edits leave the ref alone', () => {
  assert.equal(followRename('column:Store/Store City', [{ kind: 'update', ref: 'column:Store/Store City', props: ['Description'], before: 'x' }]), 'column:Store/Store City');
  assert.equal(followRename('column:Store/Store City', undefined), 'column:Store/Store City');
});

test('a not-found error becomes a plain line with no ref in it', () => {
  const line = impactErrorLine('column:Store/Store City not found.');
  assert.equal(line, GONE_FROM_IMPACT);
  assert.doesNotMatch(line, /column:|not found/);
  assert.match(line, /renamed or removed/);
  assert.equal(impactErrorLine('The engine is busy.'), 'The engine is busy.');
});

test('the page follows the rename and drops to the list when the field is gone', () => {
  const src = read('../webview/src/lineage.tsx');
  assert.match(src, /M\.followRename\(rootRef\.current, n\.deltas\)/);
  assert.match(src, /M\.impactErrorLine\(message\)/);
  assert.match(src, /setRoot\(null\); rootRef\.current = null;/);
});
