import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const bundle = readFileSync(resolve(root, 'media', 'studio', 'studio.js'), 'utf8');

const policy = source.match(/const RPC_DEFAULT_TIMEOUT_MS = ([^;]+);[\s\S]*?const RPC_LONG_TIMEOUT_MS = ([^;]+);[\s\S]*?const LONG_RPC = (\/\^\([\s\S]*?\)\/);/);
assert.ok(policy, 'bridge timeout policy should remain statically testable');

const normalizeNumber = (expression) => Number(Function(`"use strict"; return (${expression.replaceAll('_', '')});`)());
const defaultMs = normalizeNumber(policy[1]);
const longMs = normalizeNumber(policy[2]);
const longMethods = Function(`"use strict"; return ${policy[3]};`)();

assert.equal(defaultMs, 30_000);
assert.equal(longMs, 600_000);
assert.equal(longMethods.test('runTests'), true, 'whole-suite tests must never use the generic 30-second guard');
assert.equal(longMethods.test('prepareWorkingCopy'), true, 'creating a local copy can include live authentication and export');
assert.equal(longMethods.test('sessionInfo'), false, 'fast metadata calls should retain the bounded default guard');

// A source-only assertion can pass while a stale generated asset ships. Pin the compiled preview as well.
assert.match(bundle, /10\*6e4/);
assert.match(bundle, /runSql\|runTests\|runInterview/);
assert.match(bundle, /openLive\|openLocal\|prepareWorkingCopy/);

console.log('bridge timeout policy tests passed');
