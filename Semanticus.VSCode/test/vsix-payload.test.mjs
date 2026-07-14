import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { validatePayloadInventory } from '../scripts/verify-vsix.mjs';

const target = 'win32-x64';
const required = [
  '[Content_Types].xml',
  'extension.vsixmanifest',
  'extension/LICENSE.txt',
  'extension/NOTICE',
  'extension/package.json',
  'extension/readme.md',
  'extension/out/extension.js',
  'extension/media/studio/studio.js',
  'extension/media/studio/studio.css',
  'extension/node_modules/vscode-jsonrpc/node.js',
  'extension/engine/Semanticus.Engine.exe',
  'extension/engine/Semanticus.Engine.dll',
  'extension/engine/Semanticus.Core.dll',
  'extension/engine/Semanticus.Analysis.dll',
  'extension/engine/Semanticus.Engine.deps.json',
  'extension/engine/Semanticus.Engine.runtimeconfig.json',
  'extension/engine/System.Private.CoreLib.dll',
  'extension/engine/workflows/verified-measure.md',
  ...Array.from({ length: 100 }, (_, i) => `extension/engine/runtime-${i}.dll`),
];

const entries = required.map((fileName) => ({ fileName, uncompressedSize: 2_000_000, externalFileAttributes: 0 }));
const manifest = '<Identity TargetPlatform="win32-x64" />';
const packageJson = JSON.stringify({ name: 'semanticus-vscode', main: './out/extension.js' });
const contentScan = { scannedEntries: entries.length };
const scanned = (count) => ({ scannedEntries: count });

const valid = validatePayloadInventory(entries, target, manifest, packageJson, contentScan);
assert.equal(valid.executable, 'Semanticus.Engine.exe');
assert.ok(valid.engineFiles >= 100);
assert.equal(valid.scannedEntries, entries.length);

for (const [unixTarget, measuredBytes, rejectedBytes] of [
  ['linux-x64', 72_568, 69_999],
  ['darwin-x64', 82_040, 79_999],
  ['darwin-arm64', 105_744, 99_999],
]) {
  const unixEntries = entries.map((entry) => entry.fileName === 'extension/engine/Semanticus.Engine.exe'
    ? { ...entry, fileName: 'extension/engine/Semanticus.Engine', uncompressedSize: measuredBytes }
    : entry);
  const unixManifest = `<Identity TargetPlatform="${unixTarget}" />`;
  assert.equal(
    validatePayloadInventory(unixEntries, unixTarget, unixManifest, packageJson, scanned(unixEntries.length)).executable,
    'Semanticus.Engine',
  );
  assert.throws(
    () => validatePayloadInventory(
      unixEntries.map((entry) => entry.fileName === 'extension/engine/Semanticus.Engine'
        ? { ...entry, uncompressedSize: rejectedBytes }
        : entry),
      unixTarget,
      unixManifest,
      packageJson,
      scanned(unixEntries.length),
    ),
    /implausibly small/i,
    unixTarget,
  );
}

for (const forbidden of [
  'extension/test/regression.test.mjs',
  'extension/src/extension.ts',
  'extension/engine/Semanticus.Engine.pdb',
  'extension/private/signing.key',
  'extension/media/.npmrc',
  'extension/engine/NuGet.Config',
  'extension/engine/createdump.exe',
  'extension/media/.aws/credentials',
  'extension/media/.azure/accessTokens.json',
  'extension/media/.docker/config.json',
  'extension/media/.kube/config',
  'extension/media/.ssh/id_ed25519',
  'extension/unexpected.txt',
  '../outside.txt',
]) {
  assert.throws(
    () => validatePayloadInventory([...entries, { fileName: forbidden, uncompressedSize: 1, externalFileAttributes: 0 }], target, manifest, packageJson, scanned(entries.length + 1)),
    /allow-list|Forbidden|traversal/i,
    forbidden,
  );
}

assert.throws(
  () => validatePayloadInventory([...entries, { ...entries[0], fileName: entries[0].fileName.toUpperCase() }], target, manifest, packageJson, scanned(entries.length + 1)),
  /case-colliding/i,
);
assert.throws(
  () => validatePayloadInventory([...entries, { fileName: 'extension/engine/runtime//foo.dll', uncompressedSize: 1, externalFileAttributes: 0 }], target, manifest, packageJson, scanned(entries.length + 1)),
  /empty path segment/i,
);
assert.throws(() => validatePayloadInventory(entries, target, '<Identity />', packageJson, contentScan), /TargetPlatform/);
assert.throws(
  () => validatePayloadInventory(entries.map((entry, index) => index === 0 ? { ...entry, uncompressedSize: 151 * 1024 * 1024 } : entry), target, manifest, packageJson, contentScan),
  /excessive uncompressed size/i,
);
assert.throws(
  () => validatePayloadInventory(entries, target, manifest, packageJson, scanned(entries.length - 1)),
  /content scan was incomplete/i,
);
const withDirectory = [...entries, {
  fileName: 'extension/media/', compressedSize: 0, uncompressedSize: 0, externalFileAttributes: 0,
}];
assert.equal(
  validatePayloadInventory(withDirectory, target, manifest, packageJson, { scannedEntries: withDirectory.length }).scannedEntries,
  withDirectory.length,
);
assert.throws(
  () => validatePayloadInventory(
    [...entries, { fileName: 'extension/media/', compressedSize: 0, uncompressedSize: 1, externalFileAttributes: 0 }],
    target,
    manifest,
    packageJson,
    { scannedEntries: entries.length + 1 },
  ),
  /directory entry has content/i,
);
assert.throws(
  () => validatePayloadInventory(
    [...entries, { fileName: 'extension/media/', compressedSize: 1, uncompressedSize: 0, externalFileAttributes: 0 }],
    target,
    manifest,
    packageJson,
    scanned(entries.length + 1),
  ),
  /directory entry has content/i,
);

const publishWorkflow = readFileSync(new URL('../../.github/workflows/publish.yml', import.meta.url), 'utf8');
assert.match(publishWorkflow, /\$expectedTargets = @\('win32-x64', 'win32-arm64', 'linux-x64', 'darwin-x64', 'darwin-arm64'\)/);
assert.match(publishWorkflow, /foreach \(\$target in \$expectedTargets\)/);
assert.match(publishWorkflow, /--packagePath "\$\(\$matching\[0\]\.FullName\)"/);
assert.match(publishWorkflow, /needs: package/);
assert.match(publishWorkflow, /SEMANTICUS_REQUIRE_HOST_EXECUTION: '1'/);
assert.match(publishWorkflow, /verify-vsix-matrix\.mjs Semanticus\.VSCode\/dist/);

const ciWorkflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
assert.match(ciWorkflow, /needs: vsix/);
assert.match(ciWorkflow, /SEMANTICUS_REQUIRE_HOST_EXECUTION: '1'/);
assert.match(ciWorkflow, /verify-vsix-matrix\.mjs Semanticus\.VSCode\/dist/);

for (const workflow of [ciWorkflow, publishWorkflow]) {
  for (const [targetName, runner] of [
    ['win32-x64', 'windows-latest'],
    ['win32-arm64', 'windows-11-arm'],
    ['linux-x64', 'ubuntu-latest'],
    ['darwin-x64', 'macos-15-intel'],
    ['darwin-arm64', 'macos-15'],
  ]) {
    assert.match(workflow, new RegExp(`target: ${targetName}\\s+runner: ${runner}`), `${targetName} must execute on ${runner}`);
  }
}

const packageScript = readFileSync(new URL('../scripts/package.mjs', import.meta.url), 'utf8');
for (const platform of ['win32-x64', 'win32-arm64', 'linux-x64', 'darwin-x64', 'darwin-arm64']) {
  assert.match(packageScript, new RegExp(`'${platform}'\\s*:`), platform);
}
assert.match(packageScript, /NON_PRODUCT_RUNTIME_BINARIES = \['createdump', 'createdump\.exe'\]/);
assert.match(packageScript, /pruneNonProductRuntimeBinaries\(\);/);
assert.match(packageScript, /SEMANTICUS_REQUIRE_HOST_EXECUTION/);
assert.match(packageScript, /writeVsixEvidence\(outFile/);

console.log('VSIX payload allow-list and runnable-engine inventory contracts passed');
