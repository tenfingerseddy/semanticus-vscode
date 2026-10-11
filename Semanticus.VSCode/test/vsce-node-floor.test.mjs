import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

// The packager (@vscode/vsce) says which Node it needs in its own lockfile entry. A CI job that runs `npm run package`
// on an older Node packs with a tool that does not support it, so the floor and the workflows must agree. The audit
// forced the bump from vsce 3 (Node 20) to vsce 4 (Node 22) on 2026-10-03: the braces advisory GHSA-vfj7-8cjw-p6xm has
// no fixed braces, and vsce 3 reached it through secretlint, globby and fast-glob.
const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
const engines = lock.packages['node_modules/@vscode/vsce']?.engines?.node;
assert.ok(engines, 'the lockfile has no engines.node for @vscode/vsce');
const floor = Number(/>=\s*(\d+)/.exec(engines)?.[1]);
assert.ok(Number.isInteger(floor), `cannot read a Node floor from "${engines}"`);

const dir = new URL('../../.github/workflows/', import.meta.url);
let packagers = 0;
for (const name of readdirSync(dir).filter(n => n.endsWith('.yml'))) {
  const text = readFileSync(new URL(name, dir), 'utf8');
  // jobs sit at two-space indent under `jobs:`; split on them
  const jobs = text.split(/^(?=  [A-Za-z0-9_-]+:\s*$)/m);
  for (const job of jobs) {
    if (!/npm run package\b/.test(job)) continue;
    packagers++;
    const versions = [...job.matchAll(/node-version:\s*'?(\d+)/g)].map(m => Number(m[1]));
    assert.ok(versions.length > 0, `${name}: a job that runs "npm run package" sets no node-version`);
    for (const v of versions)
      assert.ok(v >= floor, `${name}: a job that runs "npm run package" uses Node ${v}, and @vscode/vsce needs ${engines}`);
  }
}
assert.ok(packagers >= 2, `expected the CI and publish package jobs, found ${packagers}`);
console.log(`vsce node floor ${floor} holds for ${packagers} package jobs`);
