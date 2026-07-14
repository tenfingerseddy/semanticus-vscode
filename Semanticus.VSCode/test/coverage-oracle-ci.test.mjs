import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');

assert.match(workflow, /^  coverage-oracle:\r?$/m,
  'CI must keep the generated coverage inventory as a dedicated visible job');
assert.match(workflow, /^    name: coverage oracle\r?$/m,
  'the coverage-oracle job must keep its stable required-check name');
assert.match(workflow, /^        run: \.\/tools\/coverage-oracle\.ps1 -Check\r?$/m,
  'CI must fail when the committed coverage inventory does not match tracked source');

console.log('coverage oracle CI contract passed');
