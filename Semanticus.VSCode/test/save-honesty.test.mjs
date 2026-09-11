// C4.2: a save is whole, honest, and does not clobber. Host copy and the DAX
// rename path must not leave a second native prompt or a stale unsaved marker.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const history = readFileSync(resolve(root, 'webview', 'src', 'history.tsx'), 'utf8');
const bar = readFileSync(resolve(root, 'webview', 'src', 'contextbar.tsx'), 'utf8');
const mcode = readFileSync(resolve(root, 'webview', 'src', 'mcode.tsx'), 'utf8');

function sliceFn(name) {
  const start = extension.indexOf(`async function ${name}`);
  assert.notEqual(start, -1, `${name} must exist`);
  const next = extension.indexOf('\nasync function ', start + 1);
  return extension.slice(start, next === -1 ? undefined : next);
}

const save = sliceFn('saveCommand');
assert.match(save, /files on disk changed/, 'Save Model must notice an external edit');
assert.match(save, /Replace files on disk/, 'Save Model must ask before replacing files that changed on disk');
assert.match(save, /overwrite/, 'Save Model must pass overwrite to the engine after the person confirms');
assert.doesNotMatch(save, /\u2014/, 'Save Model copy must not use an em dash');
assert.doesNotMatch(save, /save_model|overwrite=true/, 'Save Model copy must not name engine operations');

assert.match(extension, /lastWritten/, 'the DAX editor must remember the bytes it just saved');
assert.match(extension, /rememberWritten/, 'a header rename save must store the buffer so VS Code does not re-dirty it');
assert.match(extension, /forgetWritten/, 're-homing a renamed DAX tab must drop the old buffer');
assert.match(sliceFn('reopenRenamedDaxNow'), /forgetWritten/, 'closing the old DAX tab after a rename must not leave a dirty buffer behind');

assert.match(bar, /hasUnsavedChanges && <span/, 'the Studio footer unsaved marker follows session state');
assert.match(mcode, /window\.confirm\(\`This M query has unsaved typing/, 'switching an M target must ask before discarding typed text');
assert.match(mcode, /const selectTarget[\s\S]{0,180}confirmDiscard/, 'the query selector must use the unsaved-typing guard');
assert.match(mcode, /const selectTable[\s\S]{0,180}confirmDiscard/, 'the table selector must use the unsaved-typing guard');
assert.match(mcode, /cancelled \|\| policyDraftDirtyRef\.current\) return/, 'a policy refresh must not replace a dirty form with server values');
assert.match(history, /The open model will be saved first/, 'checkpoint preview must not claim no file delta when the model still needs saving');
assert.match(history, /No files have changed/, 'an empty checkpoint still explains that it marks the current files');
assert.doesNotMatch(history, /No file delta; this will mark the accepted state/, 'the old no-delta line is gone');
assert.match(history, /r\.restored/, 'restore copy must follow the restored flag, not only the error field');
assert.doesNotMatch(history, /The open model will be saved first[^']*\u2014/, 'the new checkpoint copy must not use an em dash');
assert.doesNotMatch(history, /The files were restored\.\u2014/, 'the restore success copy must not use an em dash');

console.log('save honesty UI tests passed');
