import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const rootReadme = readFileSync(resolve(repoRoot, 'README.md'), 'utf8');
const marketplaceReadme = readFileSync(resolve(extensionRoot, 'README.md'), 'utf8');
const supportPath = resolve(repoRoot, 'docs/supported-platforms.md');
const checklistPath = resolve(repoRoot, 'RELEASE-CHECKLIST.md');
const support = existsSync(supportPath) ? readFileSync(supportPath, 'utf8') : null;
const checklist = existsSync(checklistPath) ? readFileSync(checklistPath, 'utf8') : null;
const packageJson = JSON.parse(readFileSync(resolve(extensionRoot, 'package.json'), 'utf8'));
const packageLock = JSON.parse(readFileSync(resolve(extensionRoot, 'package-lock.json'), 'utf8'));

const publicDocs = [
  ['README.md', rootReadme],
  ['Semanticus.VSCode/README.md', marketplaceReadme],
  ...(support ? [['docs/supported-platforms.md', support]] : []),
];

for (const [name, text] of publicDocs) {
  assert.doesNotMatch(text, /\u2014/u, `${name} contains an em dash`);
  assert.doesNotMatch(text, /\b(?:Claude|Anthropic)\b/iu, `${name} names a provider instead of AI Assistant`);
  assert.doesNotMatch(text, /(?:Windows\s*[,/]\s*macOS\s*(?:and|[/,])\s*Linux|Windows, macOS and Linux)/iu,
    `${name} makes an unfrozen three-platform support claim`);
}

assert.match(rootReadme, /Semanticus 1\.0\.0 is the first public Windows 11 x64 release/u);
assert.match(marketplaceReadme, /Windows 11 x64[\s\S]*Supported release platform/u);
assert.match(marketplaceReadme, /Ubuntu 24\.04 x64[\s\S]*Source and CI coverage only/u);
assert.match(marketplaceReadme, /macOS[\s\S]*Not supported in 1\.0/u);
if (support) {
  assert.match(support, /CI build success alone is not an installation or\s+live-environment claim/u);
  assert.match(support, /Fabric deployment-pipeline, Fabric Git and CI\/CD publication writes preview as a dry run/u);
}

assert.notEqual(packageJson.publisher, 'kane', 'package publisher still uses the obsolete placeholder');
assert.equal(packageJson.version, packageLock.version, 'package.json and package-lock.json versions differ');
assert.equal(packageJson.version, packageLock.packages[''].version, 'root package-lock entry has a different version');
if (checklist) {
  assert.match(checklist, new RegExp(`package\\.json.*${packageJson.publisher}`, 'su'),
    'release checklist does not name the package publisher awaiting human ownership verification');
  assert.doesNotMatch(checklist, /replace `?"publisher": "kane"`?/u,
    'release checklist still asks for an obsolete publisher replacement');
}

console.log('release documentation contract tests passed');
