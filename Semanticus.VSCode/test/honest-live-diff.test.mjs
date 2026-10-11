import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// D-001 / D-002: Save to Live must tell the truth about live-only objects, and Push changes must be
// able to apply a reviewed selection (including an opt-in Delete) to a published model.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const extension = read('src/extension.ts');
const changes = read('webview/src/publish-changes.tsx');
const sharedCopy = read('webview/src/publishcopy.ts');

assert.doesNotMatch(extension, /The live model already matches the session/,
  'D-002: Save to Live must not claim the live model already matches when it only counted adds and edits');
assert.match(extension, /liveOnly/,
  'D-002: Save to Live must look at objects that are on the live model but not in the copy');
assert.match(extension, /Nothing is removed unless you tick/,
  'D-001: Save to Live must say that extra live objects stay unless the person ticks them');
assert.match(extension, /canPickMany:\s*true/,
  'D-001: extra live objects must be offered as an unticked multi-select');
assert.match(changes, /it\.action !== 'Delete'/,
  'D-001: a removal from a published model must start unticked (the one tick that stays a choice)');
assert.match(changes, /Tick all[\s\S]{0,40}|const tickAll = \(\) => setWish\(new Set\(items\.filter\(\(i\) => !isRemoval\(i\)\)/,
  'D-001: Tick all must not tick removals');
assert.match(sharedCopy, /Tick to remove it there/,
  'D-001: Ship Publish must say a destination-only object stays unless ticked');
assert.match(changes, /type="checkbox"/,
  'D-001: the Publish page must offer destination-only objects as unticked boxes');
assert.match(changes, /rpc<ApplyDiffResult>\('applyDiff', left, right, \[\.\.\.effective\], true, 'human'\)/,
  'D-001: the Publish page must send the reviewed selection, removals included, through applyDiff');

console.log('honest live-diff copy and apply-path tests passed');
