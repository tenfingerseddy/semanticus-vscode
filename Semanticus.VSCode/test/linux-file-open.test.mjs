// D-146: Electron on Linux cannot offer files AND folders in one picker and degrades to
// folder-only, so a .bim or flat .tmdl file never appears. The host must pick files and
// folders in two steps. D-035: a lone object RPC argument must go by position, or the
// engine cannot bind listReferenceTree.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

function sliceFn(name) {
  const start = extension.indexOf(`async function ${name}`);
  assert.notEqual(start, -1, `${name} must exist`);
  const next = extension.indexOf('\nasync function ', start + 1);
  return extension.slice(start, next === -1 ? undefined : next);
}

const openFromFile = sliceFn('openFromFile');
assert.doesNotMatch(
  openFromFile,
  /canSelectFiles:\s*true,\s*canSelectFolders:\s*true/,
  'D-146: one dialog cannot select both files and folders on Linux; Electron then offers folders only',
);
assert.match(
  openFromFile,
  /canSelectFiles:\s*true[\s\S]*canSelectFolders:\s*false/,
  'D-146: the first chooser must be a file picker so a .bim / .tmdl can be chosen',
);
assert.match(
  openFromFile,
  /canSelectFiles:\s*false[\s\S]*canSelectFolders:\s*true/,
  'D-146: a second chooser must still open a TMDL / TE folder / PBIP project folder',
);
assert.match(
  openFromFile,
  /filters:\s*\{\s*'Semantic models':\s*\['bim',\s*'tmdl'\]/,
  'the file chooser must list BIM and TMDL files',
);
assert.match(
  openFromFile,
  /Open a file/,
  'the file step must say it opens a file, not a folder',
);
assert.match(
  openFromFile,
  /Open a folder/,
  'the folder step must say it opens a folder',
);

const loadReference = sliceFn('loadReferenceModel');
assert.match(
  loadReference,
  /ParameterStructures\.byPosition/,
  'D-035: listReferenceTree must send the ModelRef as a positional argument, not named params',
);
assert.match(
  loadReference,
  /sendRequest<TreeNode\[]>\('listReferenceTree',\s*ParameterStructures\.byPosition,\s*referenceRef\)/,
  'D-035: the reference tree call must force byPosition so StreamJsonRpc binds ModelRef',
);

const saveCommand = sliceFn('saveCommand');
assert.match(
  saveCommand,
  /sessionInfo/,
  'D-032: Save Model must read the open path before choosing a format',
);
assert.match(
  saveCommand,
  /\.bim/i,
  'D-032: Save Model must keep BIM format when the open path is a .bim file',
);
