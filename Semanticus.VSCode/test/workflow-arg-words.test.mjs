#!/usr/bin/env node
// 1.5 visuals: the step editor showed the engine's own words (tableRef, expression, {{inputs.table}}). A setting now
// reads in plain words ("Table", "Formula") and a value that comes from a question or a part is a chip, not {{...}} text.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'webview', 'src');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const cache = new Map();
function load(full) {
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true, allowJs: true },
    fileName: full.endsWith('.mjs') ? full.replace(/\.mjs$/, '.js') : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => spec.startsWith('.')
    ? load(resolve(dirname(full), spec).replace(/$/, existsSync(resolve(dirname(full), spec) + '.ts') ? '.ts' : existsSync(resolve(dirname(full), spec) + '.mjs') ? '.mjs' : ''))
    : req(spec);
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  cache.set(full, module.exports);
  return module.exports;
}
const V = load(join(src, 'workflow-v3model.ts'));

test('a setting reads in plain words', () => {
  assert.equal(V.argWords('tableRef'), 'Table');
  assert.equal(V.argWords('name'), 'Name');
  assert.equal(V.argWords('expression'), 'Formula');
  assert.equal(V.argWords('displayFolder'), 'Folder');
  assert.equal(V.argWords('someNewSetting'), 'Some new setting', 'an unknown setting still reads as words, never as the raw id');
  assert.equal(V.argWords('columnRef'), 'Column', 'a trailing Ref is engine talk');
});

test('a value from a question or a part is a chip with plain words', () => {
  const ctx = { inputs: [{ name: 'table', question: 'Which table?' }], questions: [{ name: 'compares', question: 'Return rate compares' }], blanks: [{ name: 'formula' }] };
  assert.deepEqual(V.argSource('{{inputs.table}}', ctx), { kind: 'answer', text: 'Your answer: Table' });
  assert.deepEqual(V.argSource('{{questions.compares}}', ctx), { kind: 'answer', text: 'Your answer: Compares' });
  assert.deepEqual(V.argSource('{{blanks.formula}}', ctx), { kind: 'part', text: 'Your assistant writes: Formula' });
  assert.equal(V.argSource('Returns', ctx), null, 'a typed value stays a field');
  assert.equal(V.argSource('Rate ({{inputs.unit}})', ctx), null, 'a value that only holds a token is not the whole chip');
  assert.equal(V.argSource('{{loop.table}}', ctx), null, 'a token this editor does not know stays as it is');
});

test('the editor shows words and chips, not ids and braces', () => {
  const fields = readFileSync(join(src, 'workflow-v3fields.tsx'), 'utf8');
  assert.match(fields, /argWords\(a\.name\)/, 'the row label is the plain word');
  assert.match(fields, /data-testid="wf-arg-chip"/, 'a value from a question or part is a chip');
  assert.doesNotMatch(fields, /<code>\{a\.name\}<\/code>/, 'the raw setting id is not shown');
  assert.doesNotMatch(fields, /written like \{'\{\{questions\.name\}\}'\}/, 'the helper line does not teach brace syntax');
});
