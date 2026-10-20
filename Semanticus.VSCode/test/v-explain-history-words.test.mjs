// Verifier red test (m16/v-fix-explain-dirty): a History row for a read-only analysis reads in plain words.
//
// fix-explain-dirty moved the explain_value record out of the model, and History still lists it as a record row.
// That row's bold line is opLabel(record.op) (plan-history-model.ts recordTitle). blame_value, compare_baseline and
// optimize_measure have hand-written labels in OP_LABEL, but explain_value has none, so it falls through to the
// generic humaniser and the person reads the op name itself: "Explain value". copy-rules only bans the underscore
// id, so it passes. This test asks for the same treatment the other analyses get.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const file = resolve(root, 'webview/src/copy.ts');
const out = ts.transpileModule(readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file,
}).outputText;
const copy = await import(`data:text/javascript;base64,${Buffer.from(out).toString('base64')}`);

for (const op of ['explain_value', 'blame_value', 'compare_baseline', 'optimize_measure']) {
  const shown = copy.opLabel(op);
  assert.notEqual(shown, copy.uiLabel(op), `History reads the op name for ${op}: "${shown}"`);
}
console.log('v-explain-history-words: ok');
