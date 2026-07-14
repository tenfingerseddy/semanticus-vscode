#!/usr/bin/env node
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const RELEASE_TARGETS = Object.freeze({
  'win32-x64': { platform: 'win32', arch: 'x64', runner: 'windows-latest' },
  'win32-arm64': { platform: 'win32', arch: 'arm64', runner: 'windows-11-arm' },
  'linux-x64': { platform: 'linux', arch: 'x64', runner: 'ubuntu-latest' },
  'darwin-x64': { platform: 'darwin', arch: 'x64', runner: 'macos-15-intel' },
  'darwin-arm64': { platform: 'darwin', arch: 'arm64', runner: 'macos-15' },
});

const sha256File = (file) => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const evidencePathFor = (vsixPath) => vsixPath.replace(/\.vsix$/i, '.evidence.json');
const releaseVersion = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version;
const actionsRunUrl = (env) => env.GITHUB_SERVER_URL && env.GITHUB_REPOSITORY && env.GITHUB_RUN_ID
  ? `${env.GITHUB_SERVER_URL}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`
  : null;

export function writeVsixEvidence(vsixPath, { target, rid, version, verification, env = process.env }) {
  const expected = RELEASE_TARGETS[target];
  if (!expected) throw new Error(`Unknown release target ${target}`);
  if (!verification.hostExecuted) throw new Error(`Refusing evidence for ${target}: extracted engine was not executed`);
  if (process.platform !== expected.platform || process.arch !== expected.arch) {
    throw new Error(`Refusing evidence for ${target}: host is ${process.platform}-${process.arch}`);
  }
  const runUrl = actionsRunUrl(env);
  const evidence = {
    schemaVersion: 1,
    version,
    target,
    rid,
    package: path.basename(vsixPath),
    sha256: sha256File(vsixPath),
    sizeBytes: fs.statSync(vsixPath).size,
    engineExtractedAndExecuted: true,
    host: { platform: process.platform, architecture: process.arch },
    runner: { label: env.SEMANTICUS_RUNNER_LABEL ?? null, os: env.RUNNER_OS ?? null, architecture: env.RUNNER_ARCH ?? null },
    commit: env.GITHUB_SHA ?? null,
    runUrl,
  };
  const output = evidencePathFor(vsixPath);
  fs.writeFileSync(output, `${JSON.stringify(evidence, null, 2)}\n`, 'utf8');
  return { output, evidence };
}

export function verifyVsixMatrix(directory, {
  expectedCommit = process.env.GITHUB_SHA ?? null,
  expectedVersion = releaseVersion,
  expectedRunUrl = actionsRunUrl(process.env),
  requireCiEvidence = process.env.GITHUB_ACTIONS === 'true',
} = {}) {
  const root = path.resolve(directory);
  const files = fs.readdirSync(root);
  const vsixes = files.filter((name) => name.endsWith('.vsix'));
  const evidenceFiles = files.filter((name) => name.endsWith('.evidence.json'));
  const targets = Object.keys(RELEASE_TARGETS);
  if (vsixes.length !== targets.length || evidenceFiles.length !== targets.length) {
    throw new Error(`Expected ${targets.length} VSIX files and evidence records, found ${vsixes.length} and ${evidenceFiles.length}`);
  }

  const verified = [];
  for (const target of targets) {
    const matches = evidenceFiles.filter((name) => name.startsWith(`semanticus-${target}-`));
    if (matches.length !== 1) throw new Error(`Expected exactly one evidence record for ${target}, found ${matches.length}`);
    const evidence = JSON.parse(fs.readFileSync(path.join(root, matches[0]), 'utf8'));
    const expected = RELEASE_TARGETS[target];
    if (evidence.schemaVersion !== 1 || evidence.target !== target || evidence.engineExtractedAndExecuted !== true) {
      throw new Error(`Execution evidence is invalid for ${target}`);
    }
    if (evidence.version !== expectedVersion) throw new Error(`Package version does not match ${target}`);
    if (evidence.host?.platform !== expected.platform || evidence.host?.architecture !== expected.arch) {
      throw new Error(`Execution host does not match ${target}`);
    }
    if (evidence.runner?.label !== expected.runner) throw new Error(`Runner label does not match ${target}`);
    if (expectedCommit && evidence.commit !== expectedCommit) throw new Error(`Commit does not match ${target}`);
    if (requireCiEvidence && (!expectedRunUrl || evidence.runUrl !== expectedRunUrl || !evidence.commit)) {
      throw new Error(`CI provenance is missing or belongs to another run for ${target}`);
    }
    const expectedPackage = `semanticus-${target}-${evidence.version}.vsix`;
    if (evidence.package !== expectedPackage || !vsixes.includes(expectedPackage)) {
      throw new Error(`Package name does not match ${target}`);
    }
    const vsixPath = path.join(root, expectedPackage);
    if (evidence.sizeBytes !== fs.statSync(vsixPath).size || evidence.sha256 !== sha256File(vsixPath)) {
      throw new Error(`Package digest does not match ${target}`);
    }
    verified.push(evidence);
  }
  return verified;
}

const invokedDirectly = process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;
if (invokedDirectly) {
  const directory = process.argv[2];
  if (!directory) {
    console.error('Usage: node scripts/verify-vsix-matrix.mjs <artifact-directory>');
    process.exit(2);
  }
  try {
    for (const item of verifyVsixMatrix(directory)) {
      console.log(`${item.target} sha256=${item.sha256} engine-extracted-and-executed=true runner=${item.runner.label} run=${item.runUrl}`);
    }
  } catch (error) {
    console.error(`VSIX matrix verification failed: ${error.message}`);
    process.exit(1);
  }
}
