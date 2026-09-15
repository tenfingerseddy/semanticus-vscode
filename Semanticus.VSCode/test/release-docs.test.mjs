import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const rootReadme = readFileSync(resolve(repoRoot, 'README.md'), 'utf8');
const marketplaceReadme = readFileSync(resolve(extensionRoot, 'README.md'), 'utf8');
const support = readFileSync(resolve(repoRoot, 'docs/supported-platforms.md'), 'utf8');
const changelog = readFileSync(resolve(repoRoot, 'CHANGELOG.md'), 'utf8');
const checklist = readFileSync(resolve(repoRoot, 'RELEASE-CHECKLIST.md'), 'utf8');
const privateManifest = resolve(repoRoot, 'tools/release/mirror-manifest.json'); // mirror-optional: private acceptance procedure
const privateTree = existsSync(privateManifest);
const acceptance = privateTree
  ? readFileSync(resolve(repoRoot, 'docs/rc-acceptance.md'), 'utf8') // mirror-optional: required only in the private tree
  : null;
const packageJson = JSON.parse(readFileSync(resolve(extensionRoot, 'package.json'), 'utf8'));
const packageLock = JSON.parse(readFileSync(resolve(extensionRoot, 'package-lock.json'), 'utf8'));

const publicDocs = [
  ['README.md', rootReadme],
  ['Semanticus.VSCode/README.md', marketplaceReadme],
  ['docs/supported-platforms.md', support],
];

for (const [name, text] of publicDocs) {
  assert.doesNotMatch(text, /\u2014/u, `${name} contains an em dash`);
  assert.doesNotMatch(text, /(?:Claude|Anthropic)-powered/iu, `${name} implies Semanticus provides AI inference`);
  assert.doesNotMatch(text, /(?:Not supported in 1\.0|Source and CI coverage only)/iu,
    `${name} retains a pre-1.0.1 platform claim`);
}

assert.ok(rootReadme.includes('## Install ' + packageJson.version), 'README installation version must match the package');
for (const platform of ['Windows 11 x64', 'Windows 11 ARM64', 'Ubuntu 24.04 x64', 'macOS Intel', 'macOS Apple Silicon']) {
  assert.match(marketplaceReadme, new RegExp(`${platform}[\\s\\S]*Supported release package`, 'u'), platform);
  assert.match(support, new RegExp(`${platform}[\\s\\S]*Supported release package`, 'u'), platform);
}
assert.match(support, /bundled engine is\s+extracted and executed on a matching CI runner/u);
assert.match(support, /Fabric deployment-pipeline, Fabric Git and CI\/CD publication writes preview as a dry run/u);
assert.match(changelog, /## \[1\.0\.1\] - 2026-07-14/u);

assert.notEqual(packageJson.publisher, 'kane', 'package publisher still uses the obsolete placeholder');
assert.equal(packageJson.version, packageLock.version, 'package.json and package-lock.json versions differ');
assert.equal(packageJson.version, packageLock.packages[''].version, 'root package-lock entry has a different version');
assert.equal(packageJson.version, '1.2.0', 'release package version is not 1.2.0');
assert.match(checklist, new RegExp(`package\\.json.*${packageJson.publisher}`, 'su'),
  'release checklist does not name the package publisher awaiting human ownership verification');
assert.doesNotMatch(checklist, /replace `?"publisher": "kane"`?/u,
  'release checklist still asks for an obsolete publisher replacement');
assert.doesNotMatch(checklist, /VSCE_PAT/u,
  'the manual Marketplace route must not ask for an unused publication secret');
assert.match(checklist, /Marketplace portal upload[\s\S]*after the packages pass verification/iu,
  'manual publication must use the finished verified packages');
assert.match(checklist, /publish\.yml[\s\S]*packages and verifies[\s\S]*does not publish/iu,
  'the checklist must state that the tag workflow never publishes');

// The checklist and the RC acceptance procedure are both authoritative for a release owner, so a
// contract that only binds one of them lets the other keep describing the retired publishing route.
test('private Marketplace acceptance procedure', { skip: !privateTree && 'private procedure excluded from this source snapshot' }, () => {
assert.doesNotMatch(acceptance, /VSCE_PAT/u,
  'the RC acceptance procedure still requires a secret the manual portal route never uses');
assert.match(acceptance, /portal upload of the verified packages/iu,
  'the RC acceptance procedure must require the verified packages for manual publication');
assert.match(acceptance, /publish\.yml[\s\S]{0,200}?packages and verifies[\s\S]{0,200}?does not publish/iu,
  'the RC acceptance procedure must state that the tag workflow never publishes');
});

console.log('release documentation contract tests passed');
