#!/usr/bin/env node
// Release consistency oracle: proves the repo and the world agree on what is released.
// Checks package.json == package-lock == newest versioned CHANGELOG section == pushed v-tag,
// and with --marketplace, the live VS Marketplace version. Exists because a checkout on a stale
// branch (or an untagged release) made "is 1.1 shipped?" unanswerable from the repo (2026-07-21).
// Usage: node tools/release/verify-release.mjs [--marketplace]

import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const wantMarketplace = process.argv.includes('--marketplace');
const failures = [];
const check = (name, ok, detail) => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}: ${detail}`);
  if (!ok) failures.push(name);
};

const pkg = JSON.parse(readFileSync(resolve(repoRoot, 'Semanticus.VSCode/package.json'), 'utf8'));
const lock = JSON.parse(readFileSync(resolve(repoRoot, 'Semanticus.VSCode/package-lock.json'), 'utf8'));
const version = pkg.version;
console.log(`Declared version: ${version}\n`);

check('package-lock', lock.version === version && lock.packages['']?.version === version,
  `package-lock has ${lock.version} / root entry ${lock.packages['']?.version}`);

// Newest *versioned* section; an [Unreleased] section above it is the normal between-releases state.
const changelog = readFileSync(resolve(repoRoot, 'CHANGELOG.md'), 'utf8');
const newest = changelog.match(/^## \[(\d+\.\d+\.\d+)\]/mu)?.[1];
check('CHANGELOG', newest === version, `newest versioned section is [${newest ?? 'none'}]`);

const git = (...args) => execFileSync('git', ['-C', repoRoot, ...args], { encoding: 'utf8' }).trim();
const tag = `v${version}`;
check('local tag', git('tag', '-l', tag) === tag, `${tag} ${git('tag', '-l', tag) ? 'exists' : 'missing'}`);
let remoteTag = '';
try { remoteTag = git('ls-remote', '--tags', 'origin', `refs/tags/${tag}`); } catch { /* offline: reported below */ }
check('origin tag', remoteTag.includes(`refs/tags/${tag}`), remoteTag ? `${tag} on origin` : `${tag} not on origin (or offline)`);

if (wantMarketplace) {
  const extensionId = `${pkg.publisher}.${pkg.name}`;
  const res = await fetch('https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery?api-version=3.0-preview.1', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ filters: [{ criteria: [{ filterType: 7, value: extensionId }] }], flags: 914 }),
  });
  if (!res.ok) {
    check('marketplace', false, `gallery query failed: HTTP ${res.status}`);
  } else {
    const ext = (await res.json()).results?.[0]?.extensions?.[0];
    // Platform packages repeat the version once per target; entry 0 is the latest.
    const live = ext?.versions?.[0]?.version;
    check('marketplace', live === version, ext ? `latest published is ${live}` : `${extensionId} not found`);
  }
}

console.log(failures.length
  ? `\nRELEASE INCONSISTENT: ${failures.join(', ')}`
  : `\nRelease ${version} is consistent${wantMarketplace ? ' (including Marketplace)' : ''}.`);
// exitCode, not process.exit(): a hard exit while undici's sockets drain trips a libuv assert on Windows.
process.exitCode = failures.length ? 1 : 0;
