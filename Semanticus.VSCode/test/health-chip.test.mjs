// The health chip after Undo and Redo (journey m11 F10).
//
// The chip says what the LAST change did to model health, scored on that commit alone. Undo to here is a commit
// too, so the walk back to a clean model was scored as a change of its own and the chip read "9 new issues" on a
// model that had just been put back exactly. A walk through the history (Undo, Redo, Undo to here, Redo to here)
// is not a change the person made: the chip clears, so it never paints the way back as a worse model.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ts = createRequire(join(root, 'webview', 'package.json'))('typescript');
const source = readFileSync(join(root, 'src', 'healthChip.ts'), 'utf8');
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText;
const module = { exports: {} };
new Function('module', 'exports', js)(module, module.exports);
const H = module.exports;

const worse = { findings: 9, impact: 3, warn: true };
// The engine says a commit only walked the history on the wire (ChangeNotification.Replay, "undo" or "redo"). The chip
// reads that flag, never the label's words: a label is the op's own text, and nothing stops an edit's reading "Undo to".
for (const replay of ['undo', 'redo']) {
  for (const label of ['undo', 'Undo to Hide the key columns in the Sales table', 'anything at all']) {
    assert.equal(H.isHistoryWalk({ label, replay }), true, `${replay} ${label} is a walk`);
    assert.equal(H.chipHealthFor({ label, replay, health: worse }), undefined, `${replay}: the chip clears rather than scoring the walk`);
  }
}
for (const label of ['Undo to the old words', 'Redo to Hide the key columns', 'undo', 'Hide the key columns in the Sales table', undefined, '']) {
  assert.equal(H.isHistoryWalk({ label }), false, `${JSON.stringify(label)} with no replay flag is an edit`);
  assert.deepEqual(H.chipHealthFor({ label, health: worse }), worse, `${JSON.stringify(label)}: an edit keeps its health`);
}
assert.doesNotMatch(source, /startsWith\('Undo to '\)/u, 'the label is not read to spot a walk');

// The extension draws the chip through this rule, not from the raw notification.
const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.match(ext, /renderHealthChip\(chipHealthFor\(n\)\)/, 'model/didChange draws the chip through chipHealthFor');
assert.doesNotMatch(ext, /renderHealthChip\(n\.health\)/, 'and never from the raw health');
console.log('health-chip: all checks passed');
