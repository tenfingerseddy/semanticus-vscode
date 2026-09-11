import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const rls = read('webview/src/advmodels.tsx');
const shot = read('tools/uishot/shot.mjs');
const harness = read('tools/uishot/harness.html');

assert.doesNotMatch(rls, /const deleteRole = \(name: string\) => void rpc\('deleteRole'/,
  'role delete must not fire from the button with no confirm step');
assert.match(rls, /Delete the role \{role\.name\}\?/,
  'role delete must ask before it removes the role');
assert.match(rls, /This also removes: ' \+ roleLossLines\(role\)\.join/,
  'the confirm step must name the filters and visibility settings that go with the role');
assert.match(rls, /You can undo this/,
  'the confirm step must say the delete can be undone');
assert.match(rls, /<MiniButton onClick=\{\(\) => setConfirmDel\(false\)\}>Cancel<\/MiniButton>/,
  'the confirm step must have a cancel control');

assert.match(rls, /That member is already on this role/,
  'adding a member that is already on the role must say so');
assert.match(rls, /That is not a user or group name/,
  'a junk member string must be refused in the panel');
assert.match(rls, /isRoleMemberIdentity/,
  'member identity must be checked before the add call');

assert.match(rls, /Visibility settings are not used on a calculation group/,
  'a calculation group must not be offered an OLS dropdown');
assert.match(rls, /\{table\.isCalculationGroup \?/,
  'OLS must branch on isCalculationGroup so the dropdown is not shown');
assert.match(rls, /open && !table\.isCalculationGroup/,
  'column visibility controls must also stay off a calculation group');

assert.match(rls, /lg:grid-cols-\[minmax\(15rem,18rem\)_1fr\]/,
  'the roles rail must be wide enough for the Add button to stay inside it');
assert.match(rls, /flex-1 min-w-0/,
  'the new-role name field must shrink so Add is not clipped');
assert.match(rls, /shrink-0"><MiniButton disabled=\{!newName\.trim\(\)\} onClick=\{createRole\}>Add/,
  'the Add button must not shrink under the neighbouring panel');

assert.match(rls, /const filterOk = valid && issues === 0/,
  'saving a row filter must refuse warnings as well as errors');
assert.match(rls, /disabled=\{!dirty \|\| !filterOk\}/,
  'Save filter must stay disabled while the row filter has issues');

assert.match(shot, /UISHOT_RLS && variant === 'Advanced Modelling'/,
  'RLS screenshots must land on the RLS / OLS area');
assert.match(harness, /isCalculationGroup: t\.name === 'UAT Detail'/,
  'the screenshot fixture must mark UAT Detail as a calculation group');

console.log('RLS / OLS UI contract tests passed');
