import { readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

// Every file under test/ runs, including the lockfile drift check. Nothing
// runs ahead of the loop as a gate: a pre-check that exits early hides the
// state of the other forty files behind one failure, which is how a broken
// lockfile check silently meant zero tests ran on Windows.
const tests = readdirSync(new URL('../test/', import.meta.url))
  .filter((name) => name.endsWith('.test.mjs'))
  .sort();

if (tests.length === 0) {
  console.error('No extension tests were discovered in Semanticus.VSCode/test.');
  process.exit(1);
}

console.log(`Running ${tests.length} extension test files.`);
const failed = [];
for (const test of tests) {
  const result = spawnSync(process.execPath, [`test/${test}`], {
    cwd: new URL('..', import.meta.url),
    stdio: 'inherit',
  });
  if (result.status !== 0) {
    failed.push(test);
  }
}

console.log(`\n${tests.length - failed.length}/${tests.length} extension test files passed.`);
if (failed.length > 0) {
  console.error(`Failed test files:\n  ${failed.join('\n  ')}`);
  process.exit(1);
}
