// A model the assistant creates or opens reaches this window at once (golden rule 2, the UI door is pushed changes).
//
// A swap is not an edit, so no model/didChange ever announced it: after the assistant called create_model the tree,
// status bar and Studio kept the old model until the new one was edited. The engine now broadcasts session/didChange
// to every client except the one that asked (Semanticus.Tests/SessionSwapBroadcastTests.cs proves that side on a real
// pipe). This file pins the window side: it listens, and it runs the SAME post-open wiring its own opens run.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');

const at = extension.indexOf("connected.onNotification('session/didChange'");
assert.ok(at > 0, 'the window must listen for session/didChange on the engine connection');
const handler = extension.slice(at, extension.indexOf('});', at));
// 'otherDoor': the same wiring, told the swap was not the person's own (assistant-swap-quiet.test.mjs).
assert.match(handler, /bindGridAndTree\(n\.modelName, tree, 'otherDoor'\)/,
  'a swap from another door must run the same wiring a local open runs (grid, tree, status, Studio)');
assert.match(handler, /\.catch\(/, 'a failed refresh must be logged, never an unhandled rejection');

// It is registered beside model/didChange, on the same connection, so an attach that wires one wires both.
const didChangeAt = extension.indexOf("connected.onNotification('model/didChange'");
assert.ok(didChangeAt > 0 && Math.abs(at - didChangeAt) < 4000, 'session/didChange is wired with the other engine notifications');

// The shared wiring re-reads the session and tells both panels the model was replaced.
const bindAt = extension.indexOf('async function bindGridAndTree(');
assert.ok(bindAt > 0, 'bindGridAndTree must exist');
const bind = extension.slice(bindAt, extension.indexOf('\n}\n', bindAt));
for (const [pattern, why] of [
  [/propGrid\.showModel\(modelName\)/, 'the property grid drops the old model\'s object'],
  [/tree\.refresh\(\)/, 'the Model tree reloads'],
  [/await refreshStatus\(\)/, 'the status bar and the tree identity row read the new session'],
  [/rememberLastModel\(info\)/, 'a restart brings back the model that is open now'],
  [/postToPanels\(\{ type: 'reconnected', by \}\)/, 'Studio and the Connections hub re-read the new session'],
]) assert.match(bind, pattern, why);

// The payload mirrors the engine's SessionChangeNotification.
assert.match(extension, /interface SessionChangeNotification \{ sessionId: string;[^}]*modelName\?: string;[^}]*reason: string; \}/,
  'the notification shape mirrors Semanticus.Engine SessionChangeNotification');

console.log('session-swap-notify: ok');
