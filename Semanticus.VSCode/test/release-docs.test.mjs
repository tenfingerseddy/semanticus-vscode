import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const rootReadme = readFileSync(resolve(repoRoot, 'README.md'), 'utf8');
const marketplaceReadme = readFileSync(resolve(extensionRoot, 'README.md'), 'utf8');
const support = readFileSync(resolve(repoRoot, 'docs/supported-platforms.md'), 'utf8');
const changelog = readFileSync(resolve(repoRoot, 'CHANGELOG.md'), 'utf8');
const checklist = readFileSync(resolve(repoRoot, 'RELEASE-CHECKLIST.md'), 'utf8');
const packageJson = JSON.parse(readFileSync(resolve(extensionRoot, 'package.json'), 'utf8'));
const packageLock = JSON.parse(readFileSync(resolve(extensionRoot, 'package-lock.json'), 'utf8'));

const publicDocs = [
  ['README.md', rootReadme],
  ['Semanticus.VSCode/README.md', marketplaceReadme],
  ['docs/supported-platforms.md', support],
];

for (const [name, text] of publicDocs) {
  assert.doesNotMatch(text, /\u2014/u, `${name} contains an em dash`);
  assert.doesNotMatch(text, /\b(?:Claude|Anthropic)\b/iu, `${name} names a provider instead of AI Assistant`);
  assert.doesNotMatch(text, /(?:Not supported in 1\.0|Source and CI coverage only)/iu,
    `${name} retains a pre-1.0.1 platform claim`);
}

assert.match(rootReadme, /Semanticus 1\.0\.1 corrects the Marketplace listing/u);
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
assert.equal(packageJson.version, '1.0.1', 'release package version is not 1.0.1');
assert.match(checklist, new RegExp(`package\\.json.*${packageJson.publisher}`, 'su'),
  'release checklist does not name the package publisher awaiting human ownership verification');
assert.doesNotMatch(checklist, /replace `?"publisher": "kane"`?/u,
  'release checklist still asks for an obsolete publisher replacement');

console.log('release documentation contract tests passed');
