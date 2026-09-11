#!/usr/bin/env node
// A fresh npm install must leave each committed lockfile byte-identical. EACH means each: the roots are
// discovered from `git ls-files`, not listed here. See "EVERY COMMITTED LOCKFILE" below, including what
// this proves and does not prove for the Node-22.12 tools/uishot package.
//
// WHY: an incomplete lockfile makes npm rewrite the file on every install even
// when no dependency moved, so `npm ci` and a plain `npm install` disagree and
// every branch carries lockfile noise. The committed lockfiles are therefore
// whatever npm itself produces, hoisted optional entries and peer flags
// included, and this test holds them there.
//
// THE WRITER IS PINNED, AND THE PIN IS PART OF THE TEST. The canonical bytes
// are whatever npm writes, so they are a property of NPM'S VERSION and not of
// the dependency graph. The committed lockfiles were written by npm 11.6.2 on
// node 24.11.1. `actions/setup-node` pins node and leaves npm to whatever the
// distribution bundles, so CI could have rewritten these files differently and
// failed this test on a tree with no drift in it at all. The CI job that runs
// `npm test` therefore installs npm@11.6.2 explicitly, and the test below reads
// .github/workflows/ci.yml and asserts that exact pin, so the two cannot part
// company quietly. Change one, change the other, and re-record the lockfiles.
//
// Two Windows-specific traps this file has to avoid, both measured on
// npm 11.6.2 / node 24.11.1:
//
//  1. `spawnSync('npm', ...)` fails with ENOENT. On Windows npm is npm.cmd,
//     and node refuses to run a .cmd without a shell since the CVE-2024-27980
//     fix. Building a shell string instead is a quoting hazard here, because
//     the checkout path contains parentheses. So this file resolves npm's own
//     cli.js and runs it under process.execPath: no shell, no quoting.
//  2. npm copies package.json's newline style into the lockfile it writes. A
//     CRLF package.json therefore yields a CRLF lockfile that can never match
//     the LF blob git stores. `.gitattributes` pins both manifests to eol=lf
//     for that reason; if that pin is removed this test starts failing.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const vscodeRoot = fileURLToPath(new URL('..', import.meta.url));
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));

// The npm that wrote the committed lockfiles. See the header.
const PINNED_NPM = '11.6.2';
const CI_WORKFLOW = join(repoRoot, '.github', 'workflows', 'ci.yml');

// ── EVERY COMMITTED LOCKFILE, DISCOVERED, NOT LISTED ──────────────────────────────────────────────
// The header above says "each committed lockfile" and this used to be a hand list of two while four are
// committed: `tools/uishot` and `uitest` had no drift gate at all, and a fifth root would have arrived the
// same way -- silently. So the population is asked of git. `git ls-files` is the right question because
// the claim is about COMMITTED files: an untracked lockfile in someone's working tree is not something
// this repository promises anything about.
//
// WHAT THIS PROVES FOR tools/uishot, AND WHAT IT DOES NOT. That package declares `engines.node >= 22.12`
// (puppeteer-core 25's floor) and this test may run on Node 20 in CI. Everything below is lockfile-only:
// `npm install --package-lock-only --offline --ignore-scripts` resolves the tree from the lockfile and
// writes a lockfile. It installs no package, runs no lifecycle script, and evaluates no `engines` field,
// so it neither needs nor exercises Node 22.12. A pass here therefore says the uishot lockfile is complete
// and byte-stable. It says NOTHING about whether uishot RUNS on this Node -- that is browser.mjs's
// `requireSupportedNode()` guard, proved in test/uishot-runtime.test.mjs, and nothing in this file touches
// uishot code.
function committedLockfiles() {
  const r = spawnSync('git', ['ls-files', '-z', '--', '*package-lock.json'], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  if (r.status !== 0) {
    // Reported as its own failing test below rather than swallowed: a discovery step that quietly finds
    // nothing turns this whole file into a green run that checked no lockfile at all.
    return { error: `git ls-files exited ${r.status}: ${r.error?.message ?? ''}${r.stderr ?? ''}` };
  }
  const paths = (r.stdout ?? '').split('\0')
    .filter((p) => p.endsWith('package-lock.json'))
    .filter((p) => !p.split('/').includes('node_modules'))
    .sort();
  return { paths };
}

const discovered = committedLockfiles();
// The label is the containing directory, or "<repo root>" for a lockfile at the top.
const lockfilePackages = (discovered.paths ?? []).map((p) => {
  const dir = p.split('/').slice(0, -1).join('/');
  return [dir === '' ? '<repo root>' : dir, join(repoRoot, ...dir.split('/').filter(Boolean))];
});

// Find npm's JavaScript entry point, so we can run it as a node script.
// Returns null when nothing plausible is on PATH; the caller reports that as
// its own failure rather than as lockfile drift.
function resolveNpmCli() {
  const fromEnv = process.env.npm_execpath;
  if (fromEnv && fromEnv.endsWith('.js') && existsSync(fromEnv)) {
    return fromEnv;
  }
  const names = process.platform === 'win32' ? ['npm.cmd', 'npm.exe', 'npm'] : ['npm'];
  for (const dir of (process.env.PATH || '').split(delimiter).filter(Boolean)) {
    for (const name of names) {
      const launcher = join(dir, name);
      if (!existsSync(launcher)) {
        continue;
      }
      // Windows: C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js
      // Linux:   /usr/lib/node_modules/npm/bin/npm-cli.js beside /usr/bin/npm
      const candidates = [
        join(dir, 'node_modules', 'npm', 'bin', 'npm-cli.js'),
        join(dir, '..', 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js'),
      ];
      for (const candidate of candidates) {
        if (existsSync(candidate)) {
          return candidate;
        }
      }
      // Some installs symlink the launcher straight at the cli script.
      try {
        const real = realpathSync(launcher);
        if (real.endsWith('.js')) {
          return real;
        }
      } catch {
        // Unreadable link; keep looking.
      }
    }
  }
  return null;
}

const npmCli = resolveNpmCli();

function runNpmLockOnly(cwd, cache, offline) {
  return spawnSync(
    process.execPath,
    [
      npmCli,
      'install',
      '--package-lock-only',
      ...(offline ? ['--offline'] : []),
      '--ignore-scripts',
      '--no-audit',
      '--no-fund',
      '--no-progress',
    ],
    {
      cwd,
      encoding: 'utf8',
      env: {
        ...process.env,
        npm_config_cache: cache,
        npm_config_fund: 'false',
        npm_config_audit: 'false',
        npm_config_update_notifier: 'false',
      },
    },
  );
}

// One rehearsal of the install in a throwaway copy of the package.
function attempt(pkgDir, before, offline) {
  const tmp = mkdtempSync(join(tmpdir(), 'semanticus-lock-'));
  try {
    copyFileSync(join(pkgDir, 'package.json'), join(tmp, 'package.json'));
    writeFileSync(join(tmp, 'package-lock.json'), before);
    const result = runNpmLockOnly(tmp, join(tmp, 'cache'), offline);
    if (result.status !== 0) {
      return {
        ok: false,
        why:
          `npm install --package-lock-only${offline ? ' --offline' : ''} exited ${result.status}\n` +
          `${result.error ? `spawn error: ${result.error.message}\n` : ''}` +
          `${result.stdout ?? ''}\n${result.stderr ?? ''}`,
      };
    }
    return { ok: true, after: readFileSync(join(tmp, 'package-lock.json')) };
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}

// npm's own vocabulary for "the lockfile did not resolve this, I would have had
// to go to the registry". Seeing one of these in an --offline failure is the
// lockfile being incomplete, which is a REAL defect and the thing this file
// exists to catch. Anything else is the environment.
const INCOMPLETE_LOCKFILE_SIGNS = [
  'ENOTCACHED',
  'request to',
  'No matching version',
  'not found in the cache',
  'network',
];

function checkOne(pkgDir) {
  const lockPath = join(pkgDir, 'package-lock.json');
  const before = readFileSync(lockPath);
  // --offline against a throwaway cache is the ONLY run. It can read nothing
  // but what the lockfile already resolves, so a pass means the lockfile is
  // complete. Measured working here with an empty cache.
  //
  // THERE IS NO ONLINE RETRY, AND THAT IS THE FIX. This used to fall back to a
  // networked install whenever the offline one failed, which conflated the two
  // things it most needed to keep apart. An offline failure is usually the
  // lockfile being INCOMPLETE, which is a real defect; retrying with the
  // network resolved the missing entry from the registry and reported the run
  // as clean. The same fallback also meant a green run could silently have
  // required registry access, so "this proves the lockfile is complete" was not
  // something a pass could be read as. Now a failure is always a failure, and
  // the message says which of the two it is rather than calling either one
  // drift.
  const outcome = attempt(pkgDir, before, true);
  if (!outcome.ok) {
    const incomplete = INCOMPLETE_LOCKFILE_SIGNS.some((s) => outcome.why.includes(s));
    throw new Error(
      incomplete
        ? `THE LOCKFILE IS INCOMPLETE, not drifted. npm could not rehearse the ` +
          `install for ${pkgDir} from ${lockPath} alone: it wanted the registry ` +
          `for something the lockfile does not resolve. Run npm install in that ` +
          `folder with a warm network and commit the lockfile it produces. This ` +
          `is not reported as drift and is not retried online, because a retry ` +
          `would resolve the missing entry and hide the hole.\n${outcome.why}`
        : `THE ENVIRONMENT FAILED, not the lockfile. npm exited non-zero for ` +
          `${pkgDir} without asking for the network, so this says nothing about ` +
          `drift either way. Check that npm ${PINNED_NPM} is installed and that ` +
          `the temp directory is writable, then run it again.\n${outcome.why}`,
    );
  }
  assert.ok(
    before.equals(outcome.after),
    `npm install rewrote ${lockPath} (${before.length} -> ${outcome.after.length} bytes) ` +
      `under npm ${npmVersion() ?? 'unknown'}. The canonical bytes are the ones npm ` +
      `${PINNED_NPM} writes. Run npm install in that folder and commit the lockfile npm produces.`,
  );
}

// The version of the npm this run is actually using, or null if it cannot be
// read. Reported in failures so a byte mismatch caused by the writer is
// diagnosable from the message rather than from a hunch.
function npmVersion() {
  if (!npmCli) return null;
  const r = spawnSync(process.execPath, [npmCli, '--version'], { encoding: 'utf8' });
  return r.status === 0 ? (r.stdout ?? '').trim() : null;
}

test('npm cli script is resolvable without a shell', () => {
  assert.ok(
    npmCli,
    'Could not find npm-cli.js on PATH. The lockfile check runs npm under ' +
      'process.execPath and cannot fall back to a shell.',
  );
});

test('build:webview uses npm ci', () => {
  const pkg = JSON.parse(readFileSync(join(vscodeRoot, 'package.json'), 'utf8'));
  assert.match(
    pkg.scripts['build:webview'],
    /\bnpm ci\b/,
    'build:webview must use npm ci so a webview build cannot rewrite the lockfile',
  );
});

test('both manifests are pinned to LF on checkout', () => {
  const attributes = readFileSync(join(vscodeRoot, '.gitattributes'), 'utf8');
  for (const pattern of [
    'package.json text eol=lf',
    '**/package.json text eol=lf',
    'package-lock.json text eol=lf',
    '**/package-lock.json text eol=lf',
  ]) {
    assert.ok(
      attributes.includes(pattern),
      `Semanticus.VSCode/.gitattributes must pin "${pattern}". npm copies ` +
        `package.json's newline into the lockfile, so a CRLF checkout makes ` +
        `byte identity impossible on Windows.`,
    );
  }
});

// WHY THIS READS ONE JOB AND NOT THE WHOLE FILE. The assertion used to scan every `npm@x.y.z` string
// anywhere in the workflow, which is both too loose and too tight. Too loose: a pin in some unrelated job,
// or in a comment, satisfied it while the job that actually runs `npm test` stayed unpinned, which is the
// only place the pin does any work. Too tight: any other job that legitimately needs a different npm would
// fail this gate for a reason that has nothing to do with lockfile drift. What matters is one thing, so
// that is what is checked: the step that runs `npm test` is preceded, in its own job, by the global
// install of the npm these lockfiles were recorded under.
const NPM_TEST_JOB = 'build-and-smoke';

// CRLF in the working tree, LF in the index, so the line anchors below are matched on normalized text.
function workflowJob(rawText, name) {
  const text = rawText.replace(/\r\n/gu, '\n');
  const start = text.indexOf(`\n  ${name}:\n`);
  assert.notEqual(start, -1, `${CI_WORKFLOW} has no job named "${name}"`);
  const rest = text.slice(start + 1);
  const next = rest.slice(1).search(/\n {2}[a-z][a-z0-9-]*:\n/u);
  return next === -1 ? rest : rest.slice(0, next + 1);
}

test('the CI job that runs npm test pins the npm the lockfiles were recorded under', () => {
  const job = workflowJob(readFileSync(CI_WORKFLOW, 'utf8'), NPM_TEST_JOB);

  const testStep = job.search(/^\s*run: npm test\s*$/mu);
  assert.notEqual(
    testStep,
    -1,
    `${CI_WORKFLOW} job "${NPM_TEST_JOB}" no longer runs "npm test". This gate names the job that runs ` +
      `the extension test suite; if that moved, move NPM_TEST_JOB with it rather than widening the scan.`,
  );

  const pinStep = job.search(/^\s*run: npm install --global npm@\d+\.\d+\.\d+\s*$/mu);
  assert.notEqual(
    pinStep,
    -1,
    `${CI_WORKFLOW} job "${NPM_TEST_JOB}" does not install a pinned npm. The committed lockfiles are ` +
      `byte-for-byte whatever npm wrote them, so this job can fail on a tree with no drift in it. Add ` +
      `"npm install --global npm@${PINNED_NPM}" as a step before "npm test".`,
  );
  assert.ok(
    pinStep < testStep,
    `${CI_WORKFLOW} job "${NPM_TEST_JOB}" pins npm AFTER it runs npm test, so the test still runs under ` +
      `whatever npm the runner image happened to ship.`,
  );

  const pins = new Set([...job.matchAll(/run: npm install --global npm@(\d+\.\d+\.\d+)/gu)].map((m) => m[1]));
  for (const pin of pins) {
    assert.equal(
      pin,
      PINNED_NPM,
      `${CI_WORKFLOW} job "${NPM_TEST_JOB}" pins npm@${pin} but this test's canonical bytes were recorded ` +
        `under npm@${PINNED_NPM}. Moving the CI pin without re-recording the lockfiles ` +
        `is how this gate starts failing for a reason that is not drift.`,
    );
  }
});

test('this run uses the npm the lockfiles were recorded under', () => {
  const actual = npmVersion();
  assert.ok(actual, 'could not read a version out of the resolved npm cli');
  assert.equal(
    actual,
    PINNED_NPM,
    `this run is using npm ${actual}, but the committed lockfiles were written by ` +
      `npm ${PINNED_NPM}. npm's lockfile formatting is a property of npm's version, so ` +
      `a byte comparison under a different npm proves nothing either way. This fails ` +
      `rather than skipping, because a skipped gate is not a pass. Install npm@${PINNED_NPM}, ` +
      `or move the pin here and in the CI workflow together and re-record both lockfiles.`,
  );
});

test('every committed lockfile was discovered', () => {
  assert.ok(!discovered.error, `could not ask git for the committed lockfiles: ${discovered.error}`);
  assert.ok(lockfilePackages.length > 0,
    'git ls-files found no committed package-lock.json. That is a discovery failure, not a clean tree, '
    + 'and it would make every check below vacuous.');
  console.log(`lockfile roots under check: ${lockfilePackages.map(([l]) => l).join(', ')}`);
});

for (const [label, dir] of lockfilePackages) {
  test(`${label} lockfile is unchanged by npm install`, () => {
    checkOne(dir);
  });
}
