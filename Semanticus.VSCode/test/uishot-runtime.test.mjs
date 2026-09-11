#!/usr/bin/env node
// The two uishot runtime invariants that were asserted in comments and enforced nowhere.
//
// 1. THE CACHED-SHELL PICKER MUST FILTER ON PLATFORM BEFORE IT COMPARES BUILDS. The cache directory is
//    named `<platform>-<buildId>` (`win64-131.0.6778.204`), so "compare the numbers in the name" folded the
//    platform token into the version: `win32-145.0.7632.1` read as [32, 145, …] and lost to
//    `win64-131.0.6778.204` at [64, 131, …]. The picker then printed 131 as the newest. And because the
//    only other test was the executable's BASENAME -- identical on every platform -- a cache carried from
//    another machine could hand back a binary this OS cannot run.
//
// 2. THE NODE FLOOR MUST BE CHECKED BEFORE PUPPETEER IS LOADED. The entry points used a static
//    `import puppeteer from 'puppeteer-core'`, which the runtime evaluates before the first statement of
//    the file, so `requireSupportedNode()` could never fire on the Node versions it exists for. The guard
//    was unreachable exactly when it was needed.
//
// This file runs on the extension's Node (20 in CI), not on uishot's Node 22.12 floor. That is safe and
// deliberate: it imports `browser.mjs`, which pulls in nothing but node builtins, and it never loads
// puppeteer. The order proof below spawns its own child processes against a FAKE puppeteer-core.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { currentShellPlatform, parseShellDirName, pickShellDirs } from '../tools/uishot/browser.mjs';

const uishotDir = fileURLToPath(new URL('../tools/uishot/', import.meta.url));

// ── 1. The cached-shell picker ────────────────────────────────────────────────────────────────────

// Real names, and one that is not a shell directory at all. `~/.cache/puppeteer` is shared with whatever
// else @puppeteer/browsers has installed, so an unrecognised name must be skipped, never ranked.
const CACHE_NAMES = [
  'win64-131.0.6778.204',
  'win32-145.0.7632.1',
  'win64-140.0.7339.16',
  'mac_arm-140.0.7000.0',
  'linux-139.0.6900.0',
  'linux_arm-138.0.6800.0',
  'chrome',
];

// The comparator as it was: every digit in the whole folder name, newest first. Kept here so the fix is
// proved against the defect rather than against nothing.
const oldOrder = (names) => [...names].sort((a, b) => {
  const pa = (a.match(/\d+/gu) ?? []).map(Number);
  const pb = (b.match(/\d+/gu) ?? []).map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const l = pa[i] ?? 0; const r = pb[i] ?? 0;
    if (l !== r) return r - l;
  }
  return 0;
});

test('RED: the old all-digits comparator ranked the platform token as the version', () => {
  assert.equal(
    oldOrder(['win32-145.0.7632.1', 'win64-131.0.6778.204'])[0],
    'win64-131.0.6778.204',
    'this fixture no longer reproduces the bug, so the assertions below prove nothing. The old comparator '
    + 'read win32-145… as [32,145,…] and win64-131… as [64,131,…], and 64 > 32 decided it.',
  );
  // And it ranked other platforms at all, which is the second half of the defect. On this fixture the old
  // comparator hands a WINDOWS host a mac_arm directory: `mac_arm-140…` has no digits before the build, so
  // it sorts on 140 while every `win*` name sorts on 32 or 64 first. Only the executable's basename stood
  // between that and a launch, and the basename is the same on every platform.
  assert.equal(oldOrder(CACHE_NAMES)[0], 'mac_arm-140.0.7000.0');
});

test('GREEN: the picker keeps this platform only, then compares the build id alone', () => {
  assert.deepEqual(pickShellDirs(CACHE_NAMES, 'win64'),
    ['win64-140.0.7339.16', 'win64-131.0.6778.204'],
    'newest build first, and nothing from another platform');
  assert.deepEqual(pickShellDirs(CACHE_NAMES, 'win32'), ['win32-145.0.7632.1'],
    'a 32-bit host must not be handed the win64 shell just because 64 is a bigger number');
  assert.deepEqual(pickShellDirs(CACHE_NAMES, 'linux'), ['linux-139.0.6900.0'],
    'linux must not swallow linux_arm; the token is matched whole');
  assert.deepEqual(pickShellDirs(CACHE_NAMES, 'linux_arm'), ['linux_arm-138.0.6800.0']);
  assert.deepEqual(pickShellDirs(CACHE_NAMES, 'mac'), [],
    'mac must not match mac_arm');
  assert.deepEqual(pickShellDirs(CACHE_NAMES, null), [],
    'an unrecognised host platform declines the cache entirely rather than picking arbitrarily from it');
});

test('a name that is not <platform>-<build> is skipped, not guessed at', () => {
  assert.equal(parseShellDirName('chrome'), null);
  assert.equal(parseShellDirName('win64'), null);
  assert.equal(parseShellDirName('win64-'), null);
  assert.equal(parseShellDirName('freebsd-140.0.0.0'), null);
  assert.deepEqual(parseShellDirName('win64-131.0.6778.204'),
    { platform: 'win64', build: [131, 0, 6778, 204] });

  // RED: the old `\d[\d.]*` shape accepted these. It did not throw and it did not return null -- the empty
  // segment vanished under `/\d+/g`, so a name @puppeteer/browsers never writes ranked as a real build and
  // could win the "newest cached shell" comparison. Both must now be skipped.
  assert.equal(parseShellDirName('win64-145..1'), null,
    'a doubled dot is not a build id; the old pattern read it as build [145, 1]');
  assert.equal(parseShellDirName('win64-145.'), null,
    'a trailing dot is not a build id; the old pattern read it as build [145]');
  assert.equal(parseShellDirName('win64-.145'), null, 'a leading dot is not a build id');
  // And the shapes that ARE legal keep working, so the tightening is not a blanket refusal.
  assert.deepEqual(parseShellDirName('win64-145'), { platform: 'win64', build: [145] });
  assert.deepEqual(parseShellDirName('linux_arm-138.0.6800.0'),
    { platform: 'linux_arm', build: [138, 0, 6800, 0] });
});

test('a malformed cache directory name is dropped by the picker, not ranked against real ones', () => {
  // The whole point of returning null: `pickShellDirs` must not hand this back as the newest shell.
  assert.deepEqual(pickShellDirs(['win64-145..1', 'win64-131.0.6778.204'], 'win64'),
    ['win64-131.0.6778.204'],
    'a malformed name must be skipped, leaving only the shells this OS can actually launch');
  assert.deepEqual(pickShellDirs(['win64-145.'], 'win64'), [],
    'if every candidate is malformed the cache is declined entirely, not guessed at');
});

test('the host platform token matches what @puppeteer/browsers names the cache directory', () => {
  // These are detectBrowserPlatform()'s own cases, restated here because the cache naming is theirs.
  assert.equal(currentShellPlatform({ platform: 'darwin', arch: 'arm64' }), 'mac_arm');
  assert.equal(currentShellPlatform({ platform: 'darwin', arch: 'x64' }), 'mac');
  assert.equal(currentShellPlatform({ platform: 'linux', arch: 'arm64' }), 'linux_arm');
  assert.equal(currentShellPlatform({ platform: 'linux', arch: 'x64' }), 'linux');
  assert.equal(currentShellPlatform({ platform: 'win32', arch: 'x64', release: '10.0.26200' }), 'win64');
  assert.equal(currentShellPlatform({ platform: 'win32', arch: 'ia32', release: '10.0.26200' }), 'win32');
  // Windows 11 on ARM emulates x64; Windows 10 on ARM does not.
  assert.equal(currentShellPlatform({ platform: 'win32', arch: 'arm64', release: '10.0.26200' }), 'win64');
  assert.equal(currentShellPlatform({ platform: 'win32', arch: 'arm64', release: '10.0.19045' }), 'win32');
  assert.equal(currentShellPlatform({ platform: 'aix', arch: 'ppc64' }), null);
});

// ── 2. The floor check runs before puppeteer loads ────────────────────────────────────────────────

const ENTRY_POINTS = ['shot.mjs', 'docshot.mjs', 'pageshot.mjs', 'rasterize.mjs', 'render-icon.mjs',
  'lineage-interact.mjs'];

test('no uishot entry point imports puppeteer statically', () => {
  for (const name of ENTRY_POINTS) {
    const text = readFileSync(join(uishotDir, name), 'utf8');
    assert.doesNotMatch(text, /^\s*import\s[^\n]*from\s*['"]puppeteer-core['"]/mu,
      `${name} imports puppeteer-core statically. A static import is evaluated before the first statement `
      + 'of the file, so the Node floor guard below it can never fire. Use '
      + "`const puppeteer = (await import('puppeteer-core')).default;` after requireSupportedNode().");
    assert.match(text, /requireSupportedNode\(\);[\s\S]{0,200}await import\('puppeteer-core'\)/u,
      `${name} must call requireSupportedNode() BEFORE it dynamically imports puppeteer-core`);
  }
});

// A throwaway package whose puppeteer-core declares an unreachable floor AND explodes on load. The two
// together are what make the order observable: whichever of the two messages comes out says which ran
// first. Nothing here touches the real node_modules.
function fakedFloorPackage() {
  const dir = mkdtempSync(join(tmpdir(), 'uishot-floor-'));
  const pkgDir = join(dir, 'node_modules', 'puppeteer-core');
  mkdirSync(pkgDir, { recursive: true });
  writeFileSync(join(pkgDir, 'package.json'), JSON.stringify({
    name: 'puppeteer-core', version: '99.0.0', type: 'module', main: 'index.js',
    engines: { node: '>=999.0.0' },
  }));
  // It exports a default so that LINKING succeeds and the failure is an EVALUATION failure. A link error
  // would also come out first, but it would prove the wrong thing: linking happens before evaluation for
  // the dynamic import too, so only a throw on evaluation separates "loaded" from "merely resolved".
  writeFileSync(join(pkgDir, 'index.js'),
    "export default {};\nthrow new Error('PUPPETEER_WAS_LOADED');\n");
  copyFileSync(join(uishotDir, 'browser.mjs'), join(dir, 'browser.mjs'));
  return dir;
}

const runEntry = (dir, file) =>
  spawnSync(process.execPath, [join(dir, file)], { cwd: dir, encoding: 'utf8' });

test('the floor refusal fires without puppeteer being loaded, and the old shape could not', () => {
  const dir = fakedFloorPackage();
  try {
    const fixed = readFileSync(join(uishotDir, 'docshot.mjs'), 'utf8');
    writeFileSync(join(dir, 'fixed.mjs'), fixed);

    // RED: the shape this repository shipped until now, reconstructed from the fixed file so the control
    // cannot drift away from it -- static import, guard afterwards.
    const old = fixed
      .replace(/const puppeteer = \(await import\('puppeteer-core'\)\)\.default;\r?\n/u, '')
      .replace(/^(import \{ findBrowser[^\n]*\r?\n)/mu, "$1import puppeteer from 'puppeteer-core';\n");
    assert.match(old, /^import puppeteer from 'puppeteer-core';$/mu, 'the RED control was not built');
    writeFileSync(join(dir, 'old.mjs'), old);

    const red = runEntry(dir, 'old.mjs');
    assert.match(red.stderr, /PUPPETEER_WAS_LOADED/u,
      'the static-import control must load puppeteer before the guard runs; if it no longer does, this '
      + 'test has stopped proving the ordering fix');
    assert.doesNotMatch(red.stderr, /uishot needs Node/u,
      'the guard must be unreachable in the control, or the control is not the defect');

    const green = runEntry(dir, 'fixed.mjs');
    assert.match(green.stderr, /uishot needs Node 999\.0\.0 or newer/u,
      'the shipped entry point must refuse on the declared floor');
    assert.doesNotMatch(green.stderr, /PUPPETEER_WAS_LOADED/u,
      'puppeteer must not be loaded at all when the floor refuses');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
