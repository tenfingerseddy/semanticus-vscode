import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const adv = read('webview/src/advmodels.tsx');
const ext = read('src/extension.ts');

assert.match(ext, /sendRequest\('deleteObjects', refs, 'human'\)/,
  'multi-object delete must be one undo step, not a loop of single deletes');
assert.match(ext, /if \(refs\.length === 1\) await conn\.sendRequest\('deleteObject', refs\[0\], 'human'\)/,
  'a single delete still uses the one-object path');
assert.doesNotMatch(ext, /for \(const t of targets\) await conn\.sendRequest\('deleteObject'/,
  'authorDelete must not delete selected objects one by one');

// The box follows the model when nothing is typed (keptwords.ts), so an undo shows; typed words are kept instead (solui-round2-u2.test.mjs proves both).
assert.match(adv, /useKeptText\(String\(group\.precedence\)[,)]/,
  'calc-group precedence input must follow the model after undo');

assert.match(adv, /\.then\(\(\) => loadPersp\(\)\)/,
  'ticking a perspective member must reload so cascaded fields tick and the count matches');
assert.match(adv, /onDidChange\(\(\) => \{/,
  'perspective undo and an agent edit must reload, never skip the change');
assert.doesNotMatch(adv, /pendingSelf/,
  'the matrix must not skip its own change echo; that left child ticks and the count stale');

console.log('Undo redraw UI contract tests passed');
