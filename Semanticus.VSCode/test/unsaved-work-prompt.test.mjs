// C4.1: unsaved work always gets a prompt. Native open, restart, first save,
// disk divergence, and a window reload must not throw work away in silence.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const hub = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');
const deploy = readFileSync(resolve(root, 'webview', 'src', 'deploy.tsx'), 'utf8');

function sliceFn(name) {
  const start = extension.indexOf(`async function ${name}`);
  assert.notEqual(start, -1, `${name} must exist`);
  const next = extension.indexOf('\nasync function ', start + 1);
  return extension.slice(start, next === -1 ? undefined : next);
}

const confirm = sliceFn('confirmUnsavedWork');
assert.match(confirm, /This model has unsaved changes/, 'the shared prompt must say the model has unsaved changes');
assert.match(confirm, /showWarningMessage/, 'the shared prompt is a warning the person has to answer');
assert.match(confirm, /modal:\s*true/, 'the shared prompt must block the action until the person answers');
assert.match(confirm, /'Save'/, 'the shared prompt must offer Save');
assert.match(confirm, /'Discard'/, 'the shared prompt must offer Discard');
assert.doesNotMatch(confirm, /save_model|open_model|discardUnsaved/, 'the prompt must not name engine operations');
assert.doesNotMatch(confirm, /\u2014/, 'the prompt must not use an em dash');

for (const name of ['openFromFile', 'openFromTypedPath', 'openFromLocal', 'openFromRemembered']) {
  const body = sliceFn(name);
  assert.match(body, /confirmUnsavedWork|confirmAndSendOpen/, `${name} must ask before replacing a dirty session`);
}

const restart = sliceFn('restartEngineCmd');
assert.match(restart, /confirmUnsavedWork/, 'Restart engine must ask before killing a dirty session');
assert.match(extension, /semanticus.lastModelPath/, 'the open model path is remembered across restart');

const save = sliceFn('saveCommand');
assert.match(save, /never been saved|pickFirstSaveDestination/, 'Save Model must offer a destination when the model has no path');
const firstSave = sliceFn('pickFirstSaveDestination');
assert.match(firstSave, /showSaveDialog|showOpenDialog/, 'the first save must open a destination picker');
assert.match(firstSave, /canSelectFiles:\s*true[\s\S]*canSelectFolders:\s*false|filters:[\s\S]*bim/, 'the first save must be able to pick a .bim file');
assert.match(firstSave, /canSelectFolders:\s*true/, 'the first save must be able to pick a TMDL folder');

const deactivate = extension.slice(extension.indexOf('export function deactivate'), extension.indexOf('// ---- engine lifecycle'));
assert.doesNotMatch(deactivate, /spawned\.kill/, 'a window reload must not kill the engine that still holds the session');
assert.doesNotMatch(deactivate, /killEngine/, 'a window reload must not kill the engine that still holds the session');

assert.match(extension, /diskDiverged/, 'the host must read when files on disk no longer match the loaded model');
assert.match(extension, /Reload from disk/, 'a disk change must offer to reload the model from disk');
assert.match(extension, /createFileSystemWatcher|watchModelDisk/, 'the host must watch the loaded model files for an external checkout');

assert.match(hub, /hasUnsavedChanges/, 'the Connections hub must see the dirty flag');
assert.match(hub, /confirmUnsaved|openConfirm|replaceConfirm|hasUnsavedChanges && !/, 'opening from the hub must ask when the open model is dirty');
assert.match(hub, /Opening another model throws those changes away unless you save first|replaces the open model, which has unsaved changes/, 'the hub must say the open would throw unsaved work away');

assert.match(deploy, /modelReloadNeeded/, 'a git pull or checkout that changes disk must surface the reload flag');
assert.match(deploy, /files on disk changed|Reload from disk|modelReloadNeeded/, 'Deploy must not ignore a checkout that left the session behind disk');

console.log('unsaved work prompt tests passed');
