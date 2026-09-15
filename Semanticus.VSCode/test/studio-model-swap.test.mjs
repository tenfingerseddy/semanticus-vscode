import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const app = readFileSync(resolve(root, 'webview/src/App.tsx'), 'utf8');
const main = readFileSync(resolve(root, 'webview/src/main.tsx'), 'utf8');
const optimize = readFileSync(resolve(root, 'webview/src/optimize.tsx'), 'utf8');
const ext = readFileSync(resolve(root, 'src/extension.ts'), 'utf8');

assert.match(app, /onReconnect\(\(\) => \{[\s\S]*refreshSession\(\)[\s\S]*s\?\.sessionId && s\.sessionId !== previousSessionId[\s\S]*scan\(\)/,
  'a confirmed host model swap must refresh App session state and the model score without clearing state on RPC failure');
assert.match(app, /DaxModelProvider key=\{session\?\.sessionId \?\? 'no-model'\}/,
  'every model-scoped Studio view must remount when the engine session changes');
assert.match(app, /setActivity\(\[\]\); setUndoneCount\(0\); setPendingApprovals\(\[\]\)/,
  'a model swap must not retain the previous model timeline or approvals');
assert.match(main, /class StudioErrorBoundary[\s\S]*getDerivedStateFromError[\s\S]*Reload Studio/,
  'an unexpected view failure must render a recoverable Studio state instead of a blank panel');

assert.match(optimize, /it\.source === 'ai' \? RISK\.ai/,
  'the AI badge follows whether the change was AI-authored, not the risk of the kind');
assert.match(optimize, /onApply=\{\(\) => void apply\(\[it\.id\], 'apply'\)\}/,
  'each approved row has Apply, the free one-at-a-time path the Pro refusal names');
// The per-row Apply stays, and is asserted above. The sentence that used to point at it was a Pro refusal,
// and applying the whole approved set in one step is FREE from 2026-09-15, so there is nothing to point at.
assert.doesNotMatch(optimize, /Apply one change at a time with Apply on each row/,
  'the plan-shaped refusal is gone with the gate that caused it');
assert.match(optimize, /if \(\(next\.summary\?\.applied \?\? 0\) === 0\) setReport\(null\)/,
  'undo that restores plan items must clear the leftover applied result bar');
assert.match(ext, /semanticus\.selectTreeObject/,
  'every tree object click updates Properties, including a re-click after a model reopen');

console.log('Studio model-swap lifecycle contract passed');
