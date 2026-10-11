// Verifier round 2, lane m11/create-model-ui (bc6c9412): what a model swap still leaves behind from the OLD model.
//
// Round 1 found four places (session-swap-stale-state.test.mjs); the lane fixed them. Driving the fixed build in a
// real Extension Development Host (verify-create-model-ui-r2.md, shots B02 to B10) showed one more place a swap
// leaves the old model on screen, in the Studio header. It fails until fixed and names what a person saw.
//
// The header is App wiring with no seam to execute, so it is pinned in source the way the round 1 checks pin App's
// reconnect handler. A pin proves the wiring exists, not that it works: the fix still owes a lane run.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const app = readFileSync(join(root, 'webview', 'src', 'App.tsx'), 'utf8');

const failures = [];
function check(name, fn) {
  try { fn(); console.log('ok   ' + name); }
  catch (e) { failures.push(name); console.log('FAIL ' + name + '\n     ' + String(e?.message ?? e).split('\n').join('\n     ')); }
}
// The text of a block from `start` to the first `end` after it.
function slice(source, start, end, what) {
  const at = source.indexOf(start);
  assert.ok(at >= 0, `${what}: "${start}" not found`);
  const stop = source.indexOf(end, at + start.length);
  assert.ok(stop > at, `${what}: its end "${end}" not found`);
  return source.slice(at, stop);
}

// Shots B02 to B10. The assistant read Northstar's documentation model over MCP, so the header put its pulsing
// "Your assistant ran something in this group" dot on Review. The assistant then created an empty model: Studio
// rebound to it (tree, Properties, status bar, Plan all showed the new model), but the dot stayed on Review, and on
// the Docs tab once Review was opened, over a model the assistant had done nothing to. It survived two more swaps.
// The dot is App state above the session key (`unseen`), and neither App's session-change reset nor its reconnect
// handler clears it, while everything it points at (the activity feed, each tab's "Claude ran this") is under the
// key and restarts with the new model.
check('the header forgets where the assistant worked when a different model opens', () => {
  const reset = slice(app, 'const previousShellSession = useRef', '}, [session?.sessionId]);', 'App session-change reset');
  const onSwap = slice(app, 'const offReconnect = onReconnect(', 'const off = onDidChange(', 'App reconnect handler');
  const branch = slice(onSwap, 's.sessionId !== previousSessionId) {', '}', 'the different-session branch');
  assert.ok(/setUnseen\(/.test(reset) || /setUnseen\(/.test(branch),
    'a new session keeps the old model\'s "your assistant ran something here" dots in the header');
});

if (failures.length) {
  console.error(`session-swap-stale-state-r2: ${failures.length} of 1 failed`);
  process.exit(1);
}
console.log('session-swap-stale-state-r2: ok');
