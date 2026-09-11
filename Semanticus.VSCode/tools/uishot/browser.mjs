// THE UISHOT RUNTIME CONTRACT, in one place because it was in none.
//
// Two things were left implicit and both bite silently.
//
// 1. THE NODE FLOOR IS REAL AND IT IS 22.12. `puppeteer-core` 25 declares `engines.node >= 22.12.0`, and so
//    does `@puppeteer/browsers` underneath it. CI standardizes on Node 20 (`.github/workflows/ci.yml`, four
//    `node-version: '20'` steps). That is NOT a conflict today, measured rather than assumed: no CI job
//    installs or runs this directory. The only thing CI does with it is read its lockfile -- the extension's
//    `npm test` runs `test/dependency-audit.test.mjs`, which discovers all four `package-lock.json` roots
//    under `Semanticus.VSCode/` and runs `npm audit` in each. `npm audit` resolves advisories against the
//    lockfile and neither installs packages nor evaluates `engines`, so it is indifferent to the floor.
//    The floor therefore binds a DEVELOPER running the harness by hand, and nothing else. Left unstated,
//    it surfaces on Node 20 as an opaque syntax or module error from inside puppeteer, so state it.
//
//    WHY NOT JUST DOWNGRADE. Because no version does both, measured on the registry 2026-08-18: the newest
//    24.x (24.43.1) still pulls `extract-zip` and `ip-address` and `npm audit` reports 3 advisories against
//    it; the oldest published 25.x (25.0.2 -- 25.0.0 and 25.0.1 were never published) drops both and audits
//    clean, and every published version that drops them declares `>= 22.12.0`. Clean audit and Node 20
//    support are not simultaneously available, so the floor is the price of a 0-vulnerability audit and is
//    written down rather than paid silently.
//
// 2. PUPPETEER-CORE DOWNLOADS NO BROWSER. That is the whole difference between `puppeteer-core` and
//    `puppeteer`; `npm install` here fetches a library and nothing else. The README claimed otherwise. The
//    shell in `~/.cache/puppeteer` got there by some other route, and the picker used to take the FIRST
//    directory `readdirSync` returned, which is an arbitrary choice among however many shells are cached.
//    Arbitrary is the problem: two cached versions means screenshots that differ run to run with no visible
//    cause. The newest cached version for THIS platform now wins, and the choice is printed. Both halves of
//    that sentence matter; see the comment above `SHELL_PLATFORMS` for why the platform half was a defect.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { arch, homedir, platform, release } from 'node:os';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

// READ THE FLOOR FROM THE DEPENDENCY, never from a string typed here. A hand-copied floor is a comment that
// goes stale the first time the range moves, and a stale floor is worse than none: it names a version that
// is no longer the answer. If puppeteer-core is not installed yet there is no floor to check, and the
// import of it a moment later will say so far more clearly than this can.
function declaredNodeFloor() {
  const pkg = join(here, 'node_modules', 'puppeteer-core', 'package.json');
  if (!existsSync(pkg)) return null;
  try {
    const range = JSON.parse(readFileSync(pkg, 'utf8'))?.engines?.node;
    const m = /(\d+)\.(\d+)\.(\d+)/u.exec(range ?? '');
    return m ? { range, parts: [Number(m[1]), Number(m[2]), Number(m[3])] } : null;
  } catch { return null; }
}

const versionParts = (s) => (s.match(/\d+/gu) ?? []).map(Number);

function olderThan(a, b) {
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const l = a[i] ?? 0; const r = b[i] ?? 0;
    if (l !== r) return l < r;
  }
  return false;
}

// Called by every entry point in this directory before it imports anything that needs the floor.
export function requireSupportedNode() {
  const floor = declaredNodeFloor();
  if (!floor) return;
  if (!olderThan(versionParts(process.versions.node), floor.parts)) return;
  throw new Error(
    `uishot needs Node ${floor.parts.join('.')} or newer; this is Node ${process.versions.node}.\n`
    + `  The floor is not ours: the installed puppeteer-core declares engines.node "${floor.range}", and\n`
    + '  every puppeteer-core version that drops the vulnerable extract-zip / ip-address dependencies\n'
    + '  declares the same floor. Staying on an older puppeteer-core to reach Node 20 reintroduces 3 npm\n'
    + '  audit advisories, so the floor is deliberate.\n'
    + '  No CI job runs this harness, so this affects local screenshot runs only. Use a Node 22.12+ runtime.',
  );
}

// THE CACHE DIRECTORY NAME IS `<platform>-<buildId>`, AND BOTH HALVES CARRY DIGITS.
// `~/.cache/puppeteer/chrome-headless-shell/win64-131.0.6778.204/chrome-headless-shell-win64/…` is the real
// shape. "Newest cached version wins, compared numerically" used to scrape every digit out of that whole
// name, so the platform token became the leading version component: `win32-145.0.7632.1` parsed as
// [32, 145, …] and LOST to `win64-131.0.6778.204` at [64, 131, …]. The picker then announced Chrome 131 as
// the newest. Nothing else caught it either -- the only other test was the executable's BASENAME, which is
// identical on every platform -- so a cache carried across machines could hand back a binary this OS cannot
// execute, and the failure would surface from inside puppeteer rather than from here.
//
// So the platform is parsed first and filtered on, and only the build id is ever compared. An unrecognised
// directory name is skipped rather than guessed at: `chrome-headless-shell` shares its cache root with
// whatever else `@puppeteer/browsers` has put there.
const SHELL_PLATFORMS = ['linux_arm', 'linux', 'mac_arm', 'mac', 'win64', 'win32'];

// Mirrors `detectBrowserPlatform()` in @puppeteer/browsers (lib/detectPlatform.js), read at
// @puppeteer/browsers 2.x as vendored under node_modules here, because the cache is THEIRS and the naming
// is theirs. Returns null on a platform they do not name, and the caller then declines to use the cache at
// all rather than picking something arbitrary out of it.
export function currentShellPlatform(os = { platform: platform(), arch: arch(), release: release() }) {
  if (os.platform === 'darwin') return os.arch === 'arm64' ? 'mac_arm' : 'mac';
  if (os.platform === 'linux') return os.arch === 'arm64' ? 'linux_arm' : 'linux';
  if (os.platform === 'win32') {
    // Windows 11 on ARM runs x64 by emulation, which is why win64 is right there and win32 is right below.
    const parts = String(os.release ?? '').split('.').map(Number);
    const win11 = parts.length > 2 && (parts[0] > 10 || (parts[0] === 10 && (parts[1] > 0 || parts[2] >= 22000)));
    return os.arch === 'x64' || (os.arch === 'arm64' && win11) ? 'win64' : 'win32';
  }
  return null;
}

// `win64-131.0.6778.204` -> { platform: 'win64', build: [131, 0, 6778, 204] }, or null if the name is not
// that shape. PURE, so the ordering can be proved on folder-name fixtures instead of on whatever this disk
// happens to hold.
//
// THE BUILD ID IS A SHAPE, NOT "DIGITS AND DOTS". The old `\d[\d.]*` accepted `win64-145..1` and a
// trailing-dot `win64-145.` as well-formed names. Nothing then failed: `versionParts` runs `/\d+/g` over
// the string, so the empty segment simply disappears and `145..1` ranked as build [145, 1] -- a name
// @puppeteer/browsers never writes, silently competing with the real ones for "newest cached shell". A
// name this parser does not recognise must return null so the caller SKIPS it, which is what
// `pickShellDirs` is already written to do.
export function parseShellDirName(name) {
  const m = new RegExp(`^(${SHELL_PLATFORMS.join('|')})-(\\d+(?:\\.\\d+)*)$`, 'u').exec(name);
  return m ? { platform: m[1], build: versionParts(m[2]) } : null;
}

// The names this OS can run, newest build first. Anything for another platform is dropped, not ranked.
export function pickShellDirs(names, wantPlatform) {
  if (!wantPlatform) return [];
  return names
    .map((name) => ({ name, parsed: parseShellDirName(name) }))
    .filter((e) => e.parsed && e.parsed.platform === wantPlatform)
    .sort((a, b) => (olderThan(a.parsed.build, b.parsed.build) ? 1 : -1))
    .map((e) => e.name);
}

// The cached shells this machine can actually launch, newest first.
function cachedShells() {
  const cacheRoot = join(homedir(), '.cache', 'puppeteer', 'chrome-headless-shell');
  if (!existsSync(cacheRoot)) return [];
  const exeName = platform() === 'win32' ? 'chrome-headless-shell.exe' : 'chrome-headless-shell';
  const found = [];
  for (const v of pickShellDirs(readdirSync(cacheRoot), currentShellPlatform())) {
    const versionDir = join(cacheRoot, v);
    let inner = [];
    try { inner = readdirSync(versionDir); } catch { continue; }
    for (const d of inner) {
      const exe = join(versionDir, d, exeName);
      if (existsSync(exe)) { found.push({ version: v, exe }); break; }
    }
  }
  return found;
}

// SEMANTICUS_BROWSER wins, then the NEWEST cached shell, then a full Chrome/Edge install. The last group is
// a fallback and is announced as one: Edge in particular hands off to an already-running instance and then
// cannot be driven, which is the failure this harness exists to avoid.
export function findBrowser({ quiet = false } = {}) {
  if (process.env.SEMANTICUS_BROWSER && existsSync(process.env.SEMANTICUS_BROWSER)) {
    return process.env.SEMANTICUS_BROWSER;
  }
  const shells = cachedShells();
  if (shells.length > 0) {
    if (!quiet && shells.length > 1) {
      console.log(`uishot: ${shells.length} cached chrome-headless-shell versions; using the newest, ${shells[0].version}`);
    }
    return shells[0].exe;
  }
  for (const c of ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    '/usr/bin/google-chrome', '/usr/bin/chromium']) {
    if (existsSync(c)) {
      if (!quiet) console.log(`uishot: no cached chrome-headless-shell; falling back to ${c}. A full browser may hand off to a running instance and refuse to be driven.`);
      return c;
    }
  }
  throw new Error(
    'No Chromium found for uishot.\n'
    + '  `npm install` here does NOT download one: this package depends on puppeteer-core, which ships the\n'
    + '  library only. Install a shell with\n'
    + '    npx @puppeteer/browsers install chrome-headless-shell@stable\n'
    + '  (it lands in ~/.cache/puppeteer and the newest cached version is used), or point\n'
    + '  SEMANTICUS_BROWSER at a Chromium executable.',
  );
}
