import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import zlib from 'node:zlib';
import { validatePayloadInventory } from '../scripts/verify-vsix.mjs';

const target = 'win32-x64';
const skillManifest = JSON.parse(readFileSync(new URL('../assistant-pack/manifest.json', import.meta.url), 'utf8'));
const required = [
  '[Content_Types].xml',
  'extension.vsixmanifest',
  'extension/LICENSE.txt',
  'extension/NOTICE',
  'extension/THIRD-PARTY-NOTICES.md',
  'extension/package.json',
  'extension/readme.md',
  'extension/assistant-pack/manifest.json',
  'extension/assistant-pack/README.md',
  'extension/assistant-pack/LICENSE',
  'extension/assistant-pack/.agents/plugins/marketplace.json',
  'extension/assistant-pack/.claude-plugin/marketplace.json',
  'extension/assistant-pack/plugins/semanticus/.claude-plugin/plugin.json',
  'extension/assistant-pack/plugins/semanticus/.codex-plugin/plugin.json',
  ...skillManifest.skills.map(skill => `extension/assistant-pack/${skill.path}`),
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

const entries = required.map((fileName) => ({ fileName, uncompressedSize: fileName.startsWith('extension/assistant-pack/') ? 1_000 : 2_000_000, externalFileAttributes: 0 }));
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

// THE NOTICES ENTRY, PROVED RED. The corrected licence texts reach a recipient only because the packager
// stages THIRD-PARTY-NOTICES.md into the extension folder. A check that merely allows the file would stay
// green the day the staging step is deleted, so drop the entry from the payload and the verifier must
// refuse the package by name. Both licence documents are held this way, so neither can leave alone.
for (const noticeFile of ['extension/NOTICE', 'extension/THIRD-PARTY-NOTICES.md', ...skillManifest.skills.map(skill => `extension/assistant-pack/${skill.path}`)]) {
  const without = entries.filter((entry) => entry.fileName !== noticeFile);
  assert.equal(without.length, entries.length - 1, `${noticeFile} is not in the payload fixture`);
  assert.throws(
    () => validatePayloadInventory(without, target, manifest, packageJson, scanned(without.length)),
    new RegExp(`missing required production files[\\s\\S]*${noticeFile.replace('.', '\\.')}`),
    noticeFile,
  );
}

// And the packager must actually produce it: staged from the repository root before vsce runs, removed
// afterwards so the working tree is left clean.
const packagerSource = readFileSync(new URL('../scripts/package.mjs', import.meta.url), 'utf8');
assert.match(packagerSource, /copyFileSync\(path\.join\(repoRoot, 'THIRD-PARTY-NOTICES\.md'\), stagedNotices\)/);
assert.match(packagerSource, /stageThirdPartyNotices\(\);/);
assert.match(packagerSource, /cleanThirdPartyNotices\(\);/);

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
assert.match(publishWorkflow, /verify:\s+needs: package/);
assert.match(publishWorkflow, /SEMANTICUS_REQUIRE_HOST_EXECUTION: '1'/);
assert.match(publishWorkflow, /verify-vsix-matrix\.mjs Semanticus\.VSCode\/dist/);
assert.doesNotMatch(publishWorkflow, /VSCE_PAT/);
assert.doesNotMatch(publishWorkflow, /vsce publish|--packagePath/);
assert.doesNotMatch(publishWorkflow, /Detect the publisher secret|Publish the verified VSIX/);

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

// The Marketplace and Extensions view icon: package.json names it, and it is a 256 by 256 PNG (the size the
// Marketplace asks for at least 128 of). Read off the file's own PNG header, not assumed from the name.
const extensionPackage = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
assert.equal(extensionPackage.icon, 'media/icon.png', 'package.json must point its icon at media/icon.png');
const icon = readFileSync(new URL('../media/icon.png', import.meta.url));
assert.deepEqual([...icon.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 'media/icon.png is not a PNG');
assert.equal(icon.toString('latin1', 12, 16), 'IHDR', 'media/icon.png has no IHDR chunk first');
assert.deepEqual([icon.readUInt32BE(16), icon.readUInt32BE(20)], [256, 256], 'media/icon.png must be 256 by 256');

// Logo A (the path S with end dots; Kane, 28 Sep, and again 8 Oct when the Extensions view still showed the old
// abacus). Three surfaces carry it: the activity bar icon (a monochrome currentColor SVG, which VS Code themes), the
// 256 px tile PNG, and the Studio header mark. Each is checked by what it draws, not by its name.
const LOGO_A_PATH = 'M19 5.5H8.25a3.25 3.25 0 0 0 0 6.5h7.5a3.25 3.25 0 0 1 0 6.5H5';
const activityIcon = readFileSync(new URL('../media/icon.svg', import.meta.url), 'utf8');
assert.ok(activityIcon.includes(LOGO_A_PATH), 'media/icon.svg must draw the logo A path');
assert.equal((activityIcon.match(/<circle/g) || []).length, 2, 'media/icon.svg must draw exactly the two end dots');
assert.ok(!/<rect|<line/.test(activityIcon), 'media/icon.svg still draws the old abacus frame or rods');
assert.ok(/currentColor/.test(activityIcon) && !/#[0-9a-fA-F]{3,6}\b/.test(activityIcon), 'media/icon.svg must be monochrome currentColor so VS Code can theme it');
assert.equal(extensionPackage.contributes.viewsContainers.activitybar[0].icon, 'media/icon.svg');
const brandSvg = readFileSync(new URL('../media/icon-brand.svg', import.meta.url), 'utf8');
assert.ok(brandSvg.includes(LOGO_A_PATH) && !/<line|<rect[^>]*stroke-width="3\.5"/.test(brandSvg), 'media/icon-brand.svg must be logo A, not the abacus');
const appSource = readFileSync(new URL('../webview/src/App.tsx', import.meta.url), 'utf8');
const brandMark = appSource.slice(appSource.indexOf('function BrandMark()'), appSource.indexOf('function BrandMark()') + 900);
assert.ok(brandMark.includes(LOGO_A_PATH) && !/abacus/i.test(brandMark), 'the Studio header BrandMark must draw logo A');

// The PNG is checked by pixels: logo A's two end dots are Signal jade at known points of the 256 px tile (the dots sit at
// (19,5.5) and (5,18.5) of a 24 grid placed at 16 + 4x on a 128 tile, doubled for 256 px). The abacus had no jade there.
function decodePng(buf) {
  const width = buf.readUInt32BE(16), height = buf.readUInt32BE(20);
  assert.equal(buf[24], 8, 'icon.png must be 8 bit'); assert.equal(buf[25], 6, 'icon.png must be RGBA'); assert.equal(buf[28], 0, 'icon.png must not be interlaced');
  const idat = []; let at = 8;
  while (at < buf.length) { const len = buf.readUInt32BE(at); if (buf.toString('latin1', at + 4, at + 8) === 'IDAT') idat.push(buf.subarray(at + 8, at + 8 + len)); at += 12 + len; }
  const raw = zlib.inflateSync(Buffer.concat(idat)), stride = width * 4, out = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const f = raw[y * (stride + 1)], line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const a = x >= 4 ? out[y * stride + x - 4] : 0, b = y ? out[(y - 1) * stride + x] : 0, c = x >= 4 && y ? out[(y - 1) * stride + x - 4] : 0;
      const p = f === 0 ? 0 : f === 1 ? a : f === 2 ? b : f === 3 ? (a + b) >> 1 : (() => { const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; })();
      out[y * stride + x] = (line[x] + p) & 255;
    }
  }
  return { width, height, px: (x, y) => [...out.subarray(y * stride + x * 4, y * stride + x * 4 + 4)] };
}
const png = decodePng(icon);
for (const [x, y, what] of [[184, 76, 'top right'], [72, 180, 'bottom left']]) {
  const [r, g, b, a] = png.px(x, y);
  assert.ok(a === 255 && Math.abs(r - 0x3c) < 24 && Math.abs(g - 0xc0) < 24 && Math.abs(b - 0x8f) < 24, `icon.png ${what} end dot at (${x},${y}) must be jade #3CC08F, got rgba(${r},${g},${b},${a})`);
}
const [cr, cg, cb, ca] = png.px(4, 4);
assert.equal(ca, 0, 'icon.png corner must be transparent (rounded tile)');

// The Marketplace page ships inside the package, so it must not go stale. A link to one release tag or one versioned
// download (1.2.1 was never published, so those links gave a 404) breaks the day the next version ships: point at
// releases/latest instead. Kane: never "one click" in copy.
const marketplaceReadme = readFileSync(new URL('../README.md', import.meta.url), 'utf8');
assert.doesNotMatch(marketplaceReadme, /releases\/tag\/v?\d/u, 'README links to a release tag; use releases/latest');
assert.doesNotMatch(marketplaceReadme, /releases\/download\/v?\d/u, 'README links to a versioned download; use releases/latest');
assert.doesNotMatch(marketplaceReadme, /one[- ]click/iu, 'README says one click');
assert.doesNotMatch(extensionPackage.description, /one[- ]click/iu, 'package.json description says one click');
for (const image of ['https://semanticus.com.au/assets/shots/diagram.png', 'https://semanticus.com.au/assets/shots/workflows.png'])
  assert.ok(marketplaceReadme.includes(image), 'README lost its image ' + image);
// The repository front page (the public mirror's README) has the same links and the same rule.
const frontPage = readFileSync(new URL('../../README.md', import.meta.url), 'utf8');
assert.doesNotMatch(frontPage, /releases\/tag\/v?\d/u, 'the front page links to a release tag; use releases/latest');
assert.doesNotMatch(frontPage, /releases\/download\/v?\d/u, 'the front page links to a versioned download; use releases/latest');
assert.doesNotMatch(frontPage, /one[- ]click/iu, 'the front page says one click');
// F-285: a platform table whose rows all open the same release page picks nothing. The Marketplace is the install
// path, and a manual download gets one link to releases/latest that names the five files there.
assert.doesNotMatch(frontPage, /^\|.*releases\/latest.*\|\s*$/mu, 'no install table row points a platform at the shared release page');
for (const image of ['https://semanticus.com.au/assets/shots/diagram.png', 'https://semanticus.com.au/assets/shots/workflows.png'])
  assert.ok(frontPage.includes(image), 'the front page lost its image ' + image);

console.log('VSIX payload allow-list and runnable-engine inventory contracts passed');
