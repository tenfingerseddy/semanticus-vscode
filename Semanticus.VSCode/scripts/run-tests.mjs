import { readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const tests = readdirSync(new URL('../test/', import.meta.url))
  .filter((name) => name.endsWith('.test.mjs'))
  .sort();

if (tests.length === 0) {
  console.error('No extension tests were discovered in Semanticus.VSCode/test.');
  process.exit(1);
}

console.log(`Running ${tests.length} extension test files.`);
for (const test of tests) {
  const result = spawnSync(process.execPath, [`test/${test}`], {
    cwd: new URL('..', import.meta.url),
    stdio: 'inherit',
  });
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}
