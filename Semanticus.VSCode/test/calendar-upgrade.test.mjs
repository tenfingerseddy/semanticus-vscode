import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const calendars = read('webview/src/advmodels.tsx');
const harness = read('tools/uishot/harness.html');
const shot = read('tools/uishot/shot.mjs');

assert.doesNotMatch(calendars, /window\.confirm\([^\n]*compatibility level/,
  'calendar upgrades must not depend on a browser-native confirmation dialog');
assert.match(calendars, /Confirm the one-way upgrade/,
  'the irreversible action must use an in-panel confirmation');
assert.match(calendars, /await rpc\('setCompatibilityLevel', 1701\);[\s\S]*await load\(\)/,
  'the confirmed action must call the typed mutation and reload engine-owned state');
assert.match(calendars, /disabled=\{upgrading\}[\s\S]*Upgrading…/,
  'the destructive action must expose and guard its in-flight state');
assert.match(harness, /RESPONSES\.setCompatibilityLevel = function[\s\S]*calendarState = \{ calendars: \[\], compatibilityLevel: 1701, calendarsSupported: true \}/,
  'the below-1701 fixture must transition after the upgrade RPC');
assert.match(shot, /Could not find button matching UISHOT_CLICK=/,
  'maintained interaction screenshots must fail when their requested action is absent');
assert.match(shot, /process\.env\.UISHOT_EXPECT[\s\S]*page\.waitForFunction/,
  'maintained interaction screenshots must be able to assert the resulting state');

console.log('calendar upgrade UI contract tests passed');
