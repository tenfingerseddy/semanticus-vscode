// C4.2: a save is whole, honest, and does not clobber. Host copy and the DAX
// rename path must not leave a second native prompt or a stale unsaved marker.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
// Edit History (history.tsx) retired in slice 11, lane R2: saving and restoring a version are the Plan History's.
const history = readFileSync(resolve(root, 'webview', 'src', 'plan-history.tsx'), 'utf8');
const historyModel = readFileSync(resolve(root, 'webview', 'src', 'plan-history-model.ts'), 'utf8');
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
// Kane 8 Oct: a save is quiet. One word in the status bar for 4 seconds, no toast; the Publish chip tooltip does not lecture.
assert.doesNotMatch(save, /showInformationMessage/, 'a save raises no toast');
assert.match(save, /setStatusBarMessage\([^)]*4000\)/, 'a save says "Saved" in the status bar for 4 seconds');
assert.match(extension, /publishStatus\.tooltip = `Publish changes to \$\{dest\}\.`;/, 'the Publish chip tooltip names the destination and nothing else');
assert.doesNotMatch(save, /save_model|overwrite=true/, 'Save Model copy must not name engine operations');

assert.match(extension, /lastWritten/, 'the DAX editor must remember the bytes it just saved');
assert.match(extension, /rememberWritten/, 'a header rename save must store the buffer so VS Code does not re-dirty it');
assert.match(extension, /forgetWritten/, 're-homing a renamed DAX tab must drop the old buffer');
assert.match(sliceFn('reopenRenamedDaxNow'), /forgetWritten/, 'closing the old DAX tab after a rename must not leave a dirty buffer behind');

assert.match(bar, /\(s\.hasUnsavedEdits \?\? s\.hasUnsavedChanges\) && <span/, 'the Studio footer unsaved marker follows the person\'s unsaved edits (not the consent marker)');
assert.match(mcode, /window\.confirm\(\`This M query has unsaved typing/, 'switching an M target must ask before discarding typed text');
assert.match(mcode, /const selectTarget[\s\S]{0,180}confirmDiscard/, 'the query selector must use the unsaved-typing guard');
assert.match(mcode, /const selectTable[\s\S]{0,180}confirmDiscard/, 'the table selector must use the unsaved-typing guard');
assert.match(mcode, /cancelled \|\| policyDraftDirtyRef\.current\) return/, 'a policy refresh must not replace a dirty form with server values');
// Save a version is one step on the Plan History (the mockup's "Save a version"), so there is no preview left to
// claim anything about files; the engine saves the open model first and the row that appears is the answer.
assert.match(history, /rpc<HistoryCheckpointResult>\('createHistoryCheckpoint', saving\?\.trim\(\) \|\| null, true, 'human'\)/,
  'Save a version names the version and commits in one call');
// Restore asks the engine for its dry run BEFORE the one question, and trusts only the restored flag after it.
assert.match(history, /rpc<HistoryRestoreResult>\('restoreHistoryCheckpoint', a\.hash, false, 'human'\)/,
  'a version that cannot be restored says so before the question is asked');
assert.match(history, /M\.restoreOutcome\(r\)/, 'the page decides a restore through restoreOutcome');
assert.match(historyModel, /return \{ settled: false, msg: r\.error \|\| r\.note \|\| 'Nothing was restored\.', tone: 'bad' \};/, 'restore copy must follow the restored flag, not only the error field');
assert.doesNotMatch(history, /\u2014/, 'the History copy must not use an em dash');

console.log('save honesty UI tests passed');
