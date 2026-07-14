import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { RELEASE_TARGETS, verifyVsixMatrix, writeVsixEvidence } from '../scripts/verify-vsix-matrix.mjs';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'semanticus-vsix-matrix-test-'));
const commit = '0123456789abcdef0123456789abcdef01234567';
const runUrl = 'https://github.com/tenfingerseddy/semanticus/actions/runs/123';
try {
  for (const [target, host] of Object.entries(RELEASE_TARGETS)) {
    const version = '1.0.1';
    const packageName = `semanticus-${target}-${version}.vsix`;
    const content = Buffer.from(`package:${target}`);
    fs.writeFileSync(path.join(root, packageName), content);
    fs.writeFileSync(path.join(root, `semanticus-${target}-${version}.evidence.json`), `${JSON.stringify({
      schemaVersion: 1,
      version,
      target,
      rid: target,
      package: packageName,
      sha256: createHash('sha256').update(content).digest('hex'),
      sizeBytes: content.length,
      engineExtractedAndExecuted: true,
      host: { platform: host.platform, architecture: host.arch },
      runner: { label: host.runner, os: host.platform, architecture: host.arch },
      commit,
      runUrl,
    }, null, 2)}\n`);
  }

  const ciGate = { expectedCommit: commit, expectedVersion: '1.0.1', expectedRunUrl: runUrl, requireCiEvidence: true };
  assert.equal(verifyVsixMatrix(root, ciGate).length, 5);
  const linuxPackage = path.join(root, 'semanticus-linux-x64-1.0.1.vsix');
  fs.appendFileSync(linuxPackage, 'tamper');
  assert.throws(
    () => verifyVsixMatrix(root, ciGate),
    /digest does not match linux-x64/,
  );
  fs.writeFileSync(linuxPackage, Buffer.from('package:linux-x64'));

  const macEvidence = path.join(root, 'semanticus-darwin-arm64-1.0.1.evidence.json');
  const invalid = JSON.parse(fs.readFileSync(macEvidence, 'utf8'));
  invalid.engineExtractedAndExecuted = false;
  fs.writeFileSync(macEvidence, JSON.stringify(invalid));
  assert.throws(
    () => verifyVsixMatrix(root, ciGate),
    /evidence is invalid for darwin-arm64/,
  );
  invalid.engineExtractedAndExecuted = true;
  fs.writeFileSync(macEvidence, JSON.stringify(invalid));

  const linuxEvidence = path.join(root, 'semanticus-linux-x64-1.0.1.evidence.json');
  const wrongVersion = JSON.parse(fs.readFileSync(linuxEvidence, 'utf8'));
  wrongVersion.version = '1.0.0';
  fs.writeFileSync(linuxEvidence, JSON.stringify(wrongVersion));
  assert.throws(() => verifyVsixMatrix(root, ciGate), /version does not match linux-x64/);
  wrongVersion.version = '1.0.1';
  wrongVersion.runUrl = 'https://github.com/tenfingerseddy/semanticus/actions/runs/999';
  fs.writeFileSync(linuxEvidence, JSON.stringify(wrongVersion));
  assert.throws(() => verifyVsixMatrix(root, ciGate), /belongs to another run for linux-x64/);

  const local = Object.entries(RELEASE_TARGETS).find(([, host]) => host.platform === process.platform && host.arch === process.arch);
  if (local) {
    const [target, host] = local;
    const vsix = path.join(root, `local-${target}.vsix`);
    fs.writeFileSync(vsix, 'local-package');
    const written = writeVsixEvidence(vsix, {
      target,
      rid: target,
      version: '1.0.1',
      verification: { hostExecuted: true },
      env: {
        GITHUB_SERVER_URL: 'https://github.com',
        GITHUB_REPOSITORY: 'tenfingerseddy/semanticus',
        GITHUB_RUN_ID: '456',
        GITHUB_SHA: commit,
        SEMANTICUS_RUNNER_LABEL: host.runner,
        RUNNER_OS: host.platform,
        RUNNER_ARCH: host.arch,
      },
    });
    assert.equal(written.evidence.engineExtractedAndExecuted, true);
    assert.equal(written.evidence.runUrl, 'https://github.com/tenfingerseddy/semanticus/actions/runs/456');
  }
} finally {
  fs.rmSync(root, { recursive: true, force: true });
}

console.log('five-target VSIX execution evidence tests passed');
