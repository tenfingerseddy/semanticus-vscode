import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

assert.match(source,
  /connected\.onClose\(\(\) => \{[\s\S]*?if \(conn !== connected\) return;[\s\S]*?conn = undefined;[\s\S]*?connectionEpoch\+\+;[\s\S]*?scheduleEngineReconnect\(context\);[\s\S]*?\}\);/,
  'closing the active transport must invalidate it before scheduling one replacement');
assert.match(source,
  /function scheduleEngineReconnect[\s\S]*?if \(deactivating \|\| reconnectTimer\) return;[\s\S]*?void connectEngine\(context, true\);/,
  'background recovery must be quiet and single-flight at the timer boundary');
assert.match(source,
  /async function connectEngine[\s\S]*?connectInFlight\?\.epoch === epoch[\s\S]*?connectEngineAttempt/,
  'concurrent requests in one connection generation must share the same attempt');
assert.match(source,
  /async function restartEngineCmd[\s\S]*?connectionEpoch\+\+;[\s\S]*?const previous = conn;[\s\S]*?conn = undefined;[\s\S]*?previous\?\.dispose\(\)/,
  'explicit restart must invalidate and clear the transport before dispose fires onClose');
assert.match(source,
  /export function deactivate[\s\S]*?deactivating = true;[\s\S]*?cancelScheduledReconnect\(\);[\s\S]*?conn = undefined;[\s\S]*?previous\?\.dispose\(\)/,
  'shutdown must cancel recovery and clear the transport before dispose');

console.log('engine reconnect lifecycle tests passed');
