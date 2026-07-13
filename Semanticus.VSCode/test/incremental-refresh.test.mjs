import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const view = read('webview/src/mcode.tsx');
const transform = read('webview/src/mtransform.ts');

assert.match(view, /rangeStart \? 'updateNamedExpression' : 'createNamedExpression'/, 'RangeStart must be created or upgraded');
assert.match(view, /rangeEnd \? 'updateNamedExpression' : 'createNamedExpression'/, 'RangeEnd must be created or upgraded');
assert.match(view, /label: 'Add range filter'/, 'the missing partition filter must have a direct action');
assert.match(view, /setIncrementalRefreshPolicy[\s\S]*form\.mode,[\s\S]*null, true\)/, 'saving must opt into engine auto-wiring');
assert.match(transform, /incrementalRefreshFilter[\s\S]*Source = \$\{m\.trim\(\)\}/, 'a bare source must be wrapped explicitly');
assert.match(transform, /appendStep\(source, 'Filtered for incremental refresh'/, 'range wiring must use the non-clobbering appendStep kernel');
assert.match(transform, />= RangeStart and \$\{c\} < RangeEnd/, 'the generated filter must be half-open');

console.log('incremental refresh auto-wire UI contract tests passed');
