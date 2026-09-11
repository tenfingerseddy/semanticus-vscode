import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const compare = readFileSync(resolve(root, 'webview', 'src', 'compare.tsx'), 'utf8');
const preview = readFileSync(resolve(root, 'webview', 'src', 'datapreview.tsx'), 'utf8');
const lab = readFileSync(resolve(root, 'webview', 'src', 'daxlab.tsx'), 'utf8');
const authcopy = readFileSync(resolve(root, 'webview', 'src', 'authcopy.ts'), 'utf8');

assert.match(extension, /function withOpenProgress/,
  'live open must go through one helper so cancel, timeout, and replace cannot drift');
assert.match(extension, /cancellable:\s*true/,
  'D-016: the opening notification must offer Cancel');
assert.match(extension, /Opening timed out/,
  'D-016: a stuck open must time out in words a person can read');
assert.match(extension, /openingByKey/,
  'D-016: a second open of the same model must replace the first, not stack');
assert.match(extension, /Opening cancelled/,
  'D-016: Cancel must settle the notification instead of leaving it spinning');
assert.match(extension, /OPEN_WAIT_MS\s*=\s*120_000/,
  'open wait must match the engine sign-in ceiling, not the 10 minute page guard');

assert.match(authcopy, /sign-in cancelled/i);
assert.match(authcopy, /sign-in timed out/i);
assert.match(authcopy, /live sign-in expired/i);
assert.match(compare, /isSignInError/,
  'Ship Review must recognise a cancelled sign-in so the page stops waiting');
assert.match(compare, /from '\.\/authcopy'/,
  'Ship Review must share the sign-in detector so cancelled copy cannot drift');
assert.match(preview, /signin-again/,
  'D-017: Data preview must offer Sign in again without reopening the model');
assert.match(lab, /signin-again/,
  'D-017: DAX Lab must offer Sign in again without reopening the model');

console.log('open progress and sign-in wait surface tests passed');
