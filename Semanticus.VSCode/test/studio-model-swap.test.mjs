import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const app = readFileSync(resolve(root, 'webview/src/App.tsx'), 'utf8');
const main = readFileSync(resolve(root, 'webview/src/main.tsx'), 'utf8');

assert.match(app, /onReconnect\(\(\) => \{[\s\S]*refreshSession\(\)[\s\S]*s\?\.sessionId && s\.sessionId !== previousSessionId[\s\S]*scan\(\)/,
  'a confirmed host model swap must refresh App session state and the model score without clearing state on RPC failure');
assert.match(app, /DaxModelProvider key=\{session\?\.sessionId \?\? 'no-model'\}/,
  'every model-scoped Studio view must remount when the engine session changes');
assert.match(app, /setActivity\(\[\]\); setUndoneCount\(0\); setPendingApprovals\(\[\]\)/,
  'a model swap must not retain the previous model timeline or approvals');
assert.match(main, /class StudioErrorBoundary[\s\S]*getDerivedStateFromError[\s\S]*Reload Studio/,
  'an unexpected view failure must render a recoverable Studio state instead of a blank panel');

console.log('Studio model-swap lifecycle contract passed');
