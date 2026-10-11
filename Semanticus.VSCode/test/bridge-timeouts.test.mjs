import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const bundlePath = resolve(root, 'media', 'studio', 'studio.js');
assert.ok(existsSync(bundlePath), 'media/studio/studio.js is missing. Run npm run build:webview first.');
const bundle = readFileSync(bundlePath, 'utf8');

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
// Round 1 (K1): a Run the plan press measured 33 to 60 s a wave on the real engine, and the first plan read about 25 s. Under the generic
// 30-second guard the page reported "no response" for a press that was still working and then finished.
assert.equal(longMethods.test('applyPlanItems'), true, 'Run the plan lasts longer than 30 seconds');
assert.equal(longMethods.test('getModelPlan'), true, 'the first plan read takes about 25 seconds on a big model');
assert.equal(longMethods.test('setPlanItemDoer'), false, 'the person controls keep the bounded default guard');
assert.equal(longMethods.test('sessionInfo'), false, 'fast metadata calls should retain the bounded default guard');

// A source-only assertion can pass while a stale generated asset ships. Pin the compiled preview as well.
assert.match(bundle, /10\*6e4/);
assert.match(bundle, /runSql\|runTests\|runInterview/);
assert.match(bundle, /openLive\|openLocal\|prepareWorkingCopy/);

console.log('bridge timeout policy tests passed');
