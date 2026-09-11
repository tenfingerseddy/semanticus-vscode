// [T204] / F-017 / F-100. Puts the private mirror gate inside the enforced `npm test` suite.
//
// THE HOLE THIS CLOSES. tools/release holds the tenant detector's per-pattern controls and the per-pattern
// fail-red mutation matrix. All of it was built and accepted, and none of it ran in any gate: the extension
// runner discovers Semanticus.VSCode/test only, and the private gate deliberately cannot be named in
// .github/workflows/ci.yml because ci.yml is mirrored to the public repo while the gate is not. A public CI
// step invoking a file the manifest drops turns public CI red. So a weakened pattern would still publish clean.
//
// THE SHAPE. This file lives where the required suite already looks, and it is PUBLISHED, so it must behave
// in both trees. It tells them apart from the EXISTING representation of the curated copy: the public-mirror
// exclusion manifest, which self-excludes, so its presence is the private tree. Its absence means the private
// check is UNAVAILABLE in this copy, not that anything was proved, so that check skips rather than passes; a
// skip is reported as a skip and never counted as a pass. No second privacy scheme and no environment trick.
// The gate to run is read from the manifest's own excludeFiles, so the published copy of this file carries no
// private path.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const RELEASE_DIR = 'tools/release';
const MANIFEST_REL = `${RELEASE_DIR}/mirror-manifest.json`;

// The line the matrix prints once it has actually done the per-pattern mutation work. An entry point that
// exits 0 without it has proved nothing, and "exit 0" is exactly what a gutted gate looks like from out here.
const MATRIX_EVIDENCE = /fail-red mutations: (\d+) pattern/u;

function readManifest(root) {
  const path = join(root, MANIFEST_REL);
  return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null;
}

// The private-side gates, taken from the manifest rather than spelled out here, so the published copy of
// this file names no private path.
function privateGates(manifest) {
  return (manifest.excludeFiles ?? []).map((e) => e.path)
    .filter((p) => p.startsWith(`${RELEASE_DIR}/`) && p.endsWith('.test.mjs')).sort();
}

function runMirrorGate(root) {
  const declared = privateGates(readManifest(root));
  assert.ok(declared.length > 0, 'the exclusion manifest no longer names any private-side gate under '
    + `${RELEASE_DIR}, so this wrapper would run nothing and still report success`);

  const missing = declared.filter((rel) => !existsSync(join(root, rel)));
  assert.deepEqual(missing, [], 'private gate(s) the manifest names are not on disk, so the mutation matrix '
    + `did NOT run and this is a failure, not a skip: ${missing.join(', ')}`);

  let output = '';
  for (const rel of declared) {
    const result = spawnSync(process.execPath, [join(root, rel)], { cwd: root, encoding: 'utf8' });
    const text = `${result.stdout ?? ''}${result.stderr ?? ''}`;
    output += text;
    assert.ok(!result.error, `${rel} could not be launched, so it did not run: ${result.error?.message}`);
    assert.equal(result.status, 0, `${rel} FAILED (exit ${result.status}). The private mirror gate refused, `
      + `so the mirror is not clean:\n${text}`);
  }

  const hit = output.match(MATRIX_EVIDENCE);
  assert.ok(hit, 'the private mirror gate exited 0 without printing evidence of the per-pattern fail-red '
    + `mutation matrix, so nothing was proved:\n${output}`);
  assert.ok(Number(hit[1]) >= 1, `the fail-red matrix reported ${hit[1]} patterns, so it ran on an empty `
    + 'input set and refused nothing');
  return declared;
}

// Manifest absent means this copy has no private gate to run: unavailable, not clean.
const unavailable = readManifest(repoRoot) === null
  && 'no exclusion manifest; the private mirror check is unavailable in this copy';

test('the private mirror mutation matrix runs from this CI-discovered suite', { skip: unavailable }, () => {
  const ran = runMirrorGate(repoRoot);
  console.log(`release mirror gate: ran ${ran.length} private gate(s) and they passed:\n  ${ran.join('\n  ')}`);
});

test('a manifest-named private gate that is absent from disk fails', { skip: unavailable }, () => {
  // The regression T204 is about is the matrix quietly stopping. A manifest that still names the gate while
  // the gate is gone must be as loud as a real leak, not a quiet skip. One small manifest proves that; no
  // release tree is copied and no private error wording is depended on.
  const dir = mkdtempSync(join(tmpdir(), 't204-wrapper-'));
  try {
    mkdirSync(join(dir, RELEASE_DIR), { recursive: true });
    writeFileSync(join(dir, MANIFEST_REL),
      JSON.stringify({ excludeFiles: [{ path: `${RELEASE_DIR}/absent-gate.test.mjs` }] }));
    assert.throws(() => runMirrorGate(dir), /are not on disk, so the mutation matrix did NOT run/u,
      'a manifest-named gate that is absent from disk was treated as nothing to do');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
