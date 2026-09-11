import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src/extension.ts'), 'utf8');
const spec = readFileSync(resolve(root, 'webview/src/spec.tsx'), 'utf8');

// D-110: Print / PDF must not claim the documentation opened when openExternal says it did not.
const printFn = extension.slice(extension.indexOf('async function printDocInBrowser'), extension.indexOf('async function exportDocToFile'));
assert.match(printFn, /const opened = await vscode\.env\.openExternal/, 'Print / PDF must read whether the browser actually opened');
assert.match(printFn, /if \(opened\)/, 'the opened-in-browser status must be gated on that result');
assert.match(printFn, /showWarningMessage|showErrorMessage/, 'a failed open must surface an error, not a success status');
assert.doesNotMatch(
  printFn,
  /await vscode\.env\.openExternal\([\s\S]*\);\s*void vscode\.window\.setStatusBarMessage\('Semanticus: documentation opened in your browser/,
  'success must not be asserted before knowing the browser opened',
);

// D-111: a typed name that already ends in .json must not gain a second .json.
const pickSpec = extension.slice(extension.indexOf("if (msg?.type === 'pickSpecFile')"), extension.indexOf("if (msg?.type === 'exportDoc')"));
assert.match(pickSpec, /collapseDuplicateJsonExtension|json\.json/i, 'the save picker must collapse a doubled .json extension');
assert.match(extension, /function collapseDuplicateJsonExtension/, 'the name check must be a named helper so both the picker and tests share it');

// D-105: Build into model with unsaved spec changes must warn, not sit disabled with no message.
assert.match(spec, /if \(dirty\)/, 'Build must notice unsaved spec changes');
assert.match(spec, /setErr\([^)]*Save your spec changes first/, 'Build must tell the user to save first');
assert.doesNotMatch(spec, /disabled=\{!spec\.tables\?\.length \|\| dirty\}/, 'the build button must not swallow the click while the spec is dirty');
assert.match(spec, /setDirty\(true\);\s*setReport\(null\)/, 'an unsaved edit must not leave the previous build result looking fresh');

console.log('Spec save, print honesty, and unsaved-build tests passed');
