import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// D-001 / D-002: Save to Live must tell the truth about live-only objects, and Push changes must be
// able to apply a reviewed selection (including an opt-in Delete) to a published model.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const extension = read('src/extension.ts');
const compare = read('webview/src/compare.tsx');
const deploy = read('webview/src/deploy.tsx');

assert.doesNotMatch(extension, /The live model already matches the session/,
  'D-002: Save to Live must not claim the live model already matches when it only counted adds and edits');
assert.match(extension, /liveOnly/,
  'D-002: Save to Live must look at objects that are on the live model but not in the copy');
assert.match(extension, /Nothing is removed unless you tick/,
  'D-001: Save to Live must say that extra live objects stay unless the person ticks them');
assert.match(extension, /canPickMany:\s*true/,
  'D-001: extra live objects must be offered as an unticked multi-select');
assert.match(deploy, /deleteRefs/,
  'D-001: Ship Publish must pass ticked live-only refs to deployLive');
assert.match(deploy, /Nothing is removed unless you tick/,
  'D-001: Ship Publish must say extra live objects stay unless ticked');
assert.match(deploy, /type="checkbox"/,
  'D-001: Ship Publish must offer extra live objects as unticked boxes');

assert.match(compare, /targetIsWorkspace/,
  'D-001: Push changes must recognise a published model as a target that can apply');
assert.match(compare, /targetIsFile \|\| targetIsSession \|\| targetIsWorkspace/,
  'D-001: Validate selection must be enabled when the Target is a published model');
assert.doesNotMatch(compare, /To apply, set the Target to a <b>file<\/b> or the <b>working copy<\/b>/,
  'D-001: the published-model dead-end copy must be gone');
assert.match(compare, /kind === 'workspace'[\s\S]{0,120}action !== 'Delete'/,
  'D-001: Delete rows against a published model must start unticked');

console.log('honest live-diff copy and apply-path tests passed');
