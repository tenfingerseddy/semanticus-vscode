// Tests for tools/suite-gate.mjs. Nothing auto-discovers this file, so CI runs it by name:
//   node tools/suite-gate.test.mjs
// (the same arrangement as tools/findings-fixed-commits.test.mjs, and ci.yml is checked for the line).
//
// The fixtures are not invented. The aborted one is the real tail of a Debug engine-suite run on
// m11/integration 568435d3 on 2026-09-28, which executed 2,312 of 5,320 tests and still printed `Passed!` as
// its summary. (Its exit code was never recorded and the log is gone, so no test here depends on one.) If this
// gate cannot see that, it is worth nothing.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { judge, judgeBudget, parseDuration, readRun, readTrx, runGate } from './suite-gate.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const gate = resolve(here, 'suite-gate.mjs');

// The real thing: an aborted Debug run that reported itself as a pass.
const ABORTED_RUN = [
  'Test run for /repo/Semanticus.Tests/bin/Debug/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
  'VSTest version 17.11.1 (x64)',
  '',
  'Starting test execution, please wait...',
  'A total of 1 test files matched the specified pattern.',
  'The active test run was aborted. Reason: Test host process crashed : [session] disposing the live credential'
    + ' during dispose failed (the fields are already cleared): credential dispose boom',
  '[engine] parent process 4242 exited; holding this session for 0s in case the editor is reloading.',
  '',
  'Passed!  - Failed:     0, Passed:  2311, Skipped:     1, Total:  2312, Duration: 17 m 53 s'
    + ' - Semanticus.Tests.dll (net8.0)',
].join('\n');

const CLEAN_RUN = [
  'Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
  'Starting test execution, please wait...',
  'Passed!  - Failed:     0, Passed:  5322, Skipped:     4, Total:  5326, Duration: 36 m 43 s'
    + ' - Semanticus.Tests.dll (net8.0)',
].join('\n');

const FAILED_RUN = CLEAN_RUN.replace(
  'Passed!  - Failed:     0, Passed:  5322',
  'Failed!  - Failed:     2, Passed:  5320');

// ---- 1. the abort itself ------------------------------------------------------------------------------------
{
  const { aborted, summary } = readRun(ABORTED_RUN);
  assert.ok(aborted.length > 0, 'the abort line must be seen');
  assert.equal(summary.verdict, 'Passed!', 'the fixture really does claim a pass, which is the whole problem');
  assert.equal(summary.total, 2312);

  const verdict = judge({ text: ABORTED_RUN, exitCode: 0, discovered: 5320 });
  assert.equal(verdict.ok, false, 'an aborted run is never a pass, whatever its exit code says');
  assert.ok(verdict.problems.some((p) => p.includes('ABORTED')), 'the failure must name the abort');
  assert.ok(verdict.problems.some((p) => p.includes('2312 of 5320')),
    'the failure must name how many tests never ran');
}

// ---- 2. a short run with no abort line is still a short run --------------------------------------------------
{
  const quietlyShort = CLEAN_RUN.replace('Total:  5326', 'Total:  4000');
  const verdict = judge({ text: quietlyShort, exitCode: 0, discovered: 5320 });
  assert.equal(verdict.ok, false, 'a suite that executed fewer tests than it discovered has not passed');
  assert.ok(verdict.problems.some((p) => p.includes('SHORT RUN')));
}

// ---- 3. no summary line at all -------------------------------------------------------------------------------
{
  const verdict = judge({ text: 'Starting test execution, please wait...', exitCode: 0, discovered: 5320 });
  assert.equal(verdict.ok, false, 'a run that printed no summary proved nothing');
  assert.ok(verdict.problems.some((p) => p.includes('no summary line')));
}

// ---- 4. a real failure, and a clean run ----------------------------------------------------------------------
{
  assert.equal(judge({ text: FAILED_RUN, exitCode: 1, discovered: 5320 }).ok, false);
  const clean = judge({ text: CLEAN_RUN, exitCode: 0, discovered: 5320 });
  assert.equal(clean.ok, true, `a clean full run must pass: ${clean.problems.join(' ')}`);
  assert.equal(clean.summary.total, 5326);
}

// ---- 5. a clean run is not excused when the exit code disagrees ----------------------------------------------
{
  const verdict = judge({ text: CLEAN_RUN, exitCode: 134, discovered: 5320 });
  assert.equal(verdict.ok, false, 'a nonzero exit code with clean-looking output is still a failure');
}

// ---- 6. no discovered count is a failure, unless it was waived out loud ---------------------------------------
// This group used to assert the opposite (no count, so nothing to compare, so a pass). That was the defect the
// verifier's group 8 found: a gate that cannot count cannot tell 12 tests from 5,324.
{
  const twelve = CLEAN_RUN.replace('Total:  5326', 'Total:  12');
  const unknown = judge({ text: twelve, exitCode: 0, discovered: null, whyUncounted: '--list-tests exited 1' });
  assert.equal(unknown.ok, false, 'with no discovered count the run cannot be shown complete, so it cannot pass');
  assert.ok(unknown.problems.some((p) => p.includes('UNKNOWN') && p.includes('--list-tests exited 1')),
    `the failure must say the count is unknown and why: ${unknown.problems.join(' ')}`);
  assert.equal(unknown.discovered, null, 'and must not report a discovered number it never had');

  for (const junk of [0, -1, NaN, 2.5, '5324']) {
    assert.equal(judge({ text: twelve, exitCode: 0, discovered: junk }).ok, false, `${junk} is not a count`);
  }

  const waived = judge({ text: twelve, exitCode: 0, discovered: null, countWaived: true });
  assert.equal(waived.ok, true, 'an explicit waiver lets a clean run through');
  assert.equal(judge({ text: ABORTED_RUN, exitCode: 0, discovered: null, countWaived: true }).ok, false,
    'and never excuses an abort');
}

// ---- 7. the script itself, end to end ------------------------------------------------------------------------
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-test-'));
  const abortedLog = join(dir, 'aborted.log');
  const cleanLog = join(dir, 'clean.log');
  writeFileSync(abortedLog, ABORTED_RUN);
  writeFileSync(cleanLog, CLEAN_RUN);

  const bad = spawnSync(process.execPath, [gate, '--verify-log', abortedLog, '--discovered', '5320'],
    { encoding: 'utf8' });
  assert.equal(bad.status, 1, 'the script must exit nonzero on the aborted log');
  assert.ok(bad.stderr.includes('SUITE GATE: FAIL'), 'and say so loudly on stderr');
  assert.ok(bad.stderr.includes('ABORTED'), `and name the abort: ${bad.stderr}`);

  const good = spawnSync(process.execPath, [gate, '--verify-log', cleanLog, '--discovered', '5320'],
    { encoding: 'utf8' });
  assert.equal(good.status, 0, `the script must pass a clean log: ${good.stderr}`);
  assert.ok(good.stdout.includes('SUITE GATE: PASS'));
}

// ---- 8. VERIFY (m11/base-fixes, 2026-09-28): a failed DISCOVERY must not be reported as a pass -------------
// When `dotnet test --list-tests` fails, countListed returns null, the short-run check switches off, and the
// success line still prints `SUITE GATE: PASS (12 executed of 12 discovered)`: a discovered count the gate
// never learned, filled in from the executed total by `discovered ?? s.total`. A run that executed 12 of
// 5,324 tests therefore reads as green and exits 0, which is the exact misreading this gate exists to stop.
// Discovery fails for ordinary reasons: the project is not built in that configuration, or the discovery host
// dies on a loaded machine, which is the very condition that produced the incident this gate was written for.
//
// This test FAILED at 0bd77393, before the fix. It is the proof of the defect, and now its guard.
// POSIX only: the stand-in is a bash script found through a colon-separated PATH, and on Windows the real
// dotnet.exe would be found first. Group 11 proves the same rules in process on every platform.
if (process.platform !== 'win32') {
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-nodiscovery-'));
  const shimScript = `#!/usr/bin/env bash
# Stands in for dotnet: DISCOVERY fails, then the run itself reports a short but clean pass.
for arg in "$@"; do
  if [ "$arg" = "--list-tests" ]; then
    echo "MSBUILD : error MSB1009: Project file does not exist." >&2
    exit 1
  fi
done
echo "Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)"
echo "Starting test execution, please wait..."
echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 2 s - Semanticus.Tests.dll (net8.0)"
exit 0
`;
  const shim = join(dir, 'dotnet');
  writeFileSync(shim, shimScript, { mode: 0o755 });

  const run = spawnSync(process.execPath,
    [gate, '--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
      '--log', join(dir, 'out.log')],
    { encoding: 'utf8', env: { ...process.env, PATH: dir + ':' + process.env.PATH } });

  assert.ok(!/executed of 12 discovered/.test(run.stdout),
    'the gate claimed a discovered count it never learned; discovery had failed, so it printed the executed '
    + 'total as though it were the discovered total: ' + run.stdout);
  assert.notEqual(run.status, 0,
    'discovery failed, so the short-run check could not run, and the gate still exited 0 with SUITE GATE: PASS. '
    + 'A 12-test run of a 5,324-test suite reads as green: ' + run.stdout);
}

// ---- 9. --verify-log with no --discovered cannot check the count, so it cannot pass ---------------------------
// Found by the same verifier: `--verify-log <log>` alone printed `SUITE GATE: PASS` with no caveat, so any short
// log passed silently in that mode.
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-nocount-'));
  const shortLog = join(dir, 'short.log');
  writeFileSync(shortLog, CLEAN_RUN.replace('Total:  5326', 'Total:    12'));

  const run = spawnSync(process.execPath, [gate, '--verify-log', shortLog], { encoding: 'utf8' });
  assert.notEqual(run.status, 0, `a log judged with no discovered count must not pass: ${run.stdout}`);
  assert.ok(!run.stdout.includes('SUITE GATE: PASS'), `and must not print a pass line: ${run.stdout}`);
  assert.ok(run.stderr.includes('SUITE GATE: FAIL'), `it must fail loudly: ${run.stderr}`);
  assert.ok(run.stderr.includes('--discovered'), `and say how to give the count: ${run.stderr}`);

  // A number that is not a count is no count either.
  const junk = spawnSync(process.execPath, [gate, '--verify-log', shortLog, '--discovered', 'lots'], { encoding: 'utf8' });
  assert.notEqual(junk.status, 0, `--discovered lots is not a measurement: ${junk.stdout}`);
}

// ---- 10. the only way past a missing count is an explicit waiver, and the pass line says what was waived -------
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-waived-'));
  const shortLog = join(dir, 'short.log');
  writeFileSync(shortLog, CLEAN_RUN.replace('Total:  5326', 'Total:    12'));

  const run = spawnSync(process.execPath, [gate, '--verify-log', shortLog, '--no-count-check'], { encoding: 'utf8' });
  assert.equal(run.status, 0, `an explicit waiver lets a clean log through: ${run.stderr}`);
  assert.ok(/PASS WITHOUT THE COUNT CHECK/.test(run.stdout), `and the verdict names the waiver: ${run.stdout}`);
  assert.ok(!/discovered,/.test(run.stdout) && !/of \d+ discovered/.test(run.stdout),
    `a waived run must not print a discovered number it never measured: ${run.stdout}`);

  // The waiver never excuses an abort.
  const abortedLog = join(dir, 'aborted.log');
  writeFileSync(abortedLog, ABORTED_RUN);
  const bad = spawnSync(process.execPath, [gate, '--verify-log', abortedLog, '--no-count-check'], { encoding: 'utf8' });
  assert.equal(bad.status, 1, 'waiving the count check must not waive the abort check');
}

// ---- 11. the whole run path, in process, on every platform ---------------------------------------------------
// Group 8 drives the real script through a bash stand-in for dotnet, which only works where bash and a
// colon-separated PATH exist. These drive runGate with a stand-in spawn, so the Windows leg of CI proves the
// same rules.
{
  const clean = (total) => CLEAN_RUN.replace('Total:  5326', `Total: ${String(total).padStart(6)}`);
  const listing = (n) => ['The following Tests are available:',
    ...Array.from({ length: n }, (_, i) => `    Semanticus.Tests.T${i}`)].join('\n');
  const fake = ({ list, run }) => {
    const calls = [];
    const spawn = (cmd, args) => {
      calls.push(args);
      return args.includes('--list-tests') ? list(args) : run(args);
    };
    return { spawn, calls };
  };
  const drive = (argv, spawn) => {
    const out = []; const err = [];
    const dir = mkdtempSync(join(tmpdir(), 'suite-gate-io-'));
    const code = runGate([...argv, '--log', join(dir, 'suite.log')], {
      spawn, out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
    });
    return { code, out: out.join('\n'), err: err.join('\n') };
  };
  const project = ['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release'];

  // a. discovery fails: FAIL, and no discovered number anywhere.
  {
    const { spawn } = fake({
      list: () => ({ status: 1, stdout: '', stderr: 'MSBUILD : error MSB1009: Project file does not exist.' }),
      run: () => ({ status: 0, stdout: clean(12), stderr: '' }),
    });
    const r = drive(project, spawn);
    assert.equal(r.code, 1, `discovery failed, so the gate must fail: ${r.out}`);
    assert.ok(!r.out.includes('SUITE GATE: PASS'), r.out);
    assert.ok(!/12 discovered|of 12/.test(r.out + r.err), `it must not invent a discovered count: ${r.out} ${r.err}`);
    assert.ok(r.err.includes('--list-tests exited 1'), `and must say why the count is missing: ${r.err}`);
  }

  // b. discovery lists nothing: the same.
  {
    const { spawn } = fake({
      list: () => ({ status: 0, stdout: 'No test is available in Semanticus.Tests.dll.', stderr: '' }),
      run: () => ({ status: 0, stdout: clean(12), stderr: '' }),
    });
    assert.equal(drive(project, spawn).code, 1, 'a discovery that lists nothing measured nothing');
  }

  // c. discovery works, the run is full: PASS with the measured number.
  {
    const { spawn } = fake({
      list: () => ({ status: 0, stdout: listing(12), stderr: '' }),
      run: () => ({ status: 0, stdout: clean(12), stderr: '' }),
    });
    const r = drive(project, spawn);
    assert.equal(r.code, 0, `a full clean run passes: ${r.err}`);
    assert.ok(r.out.includes('SUITE GATE: PASS (12 executed of 12 discovered'), r.out);
  }

  // d. discovery works, the run is short: FAIL.
  {
    const { spawn } = fake({
      list: () => ({ status: 0, stdout: listing(20), stderr: '' }),
      run: () => ({ status: 0, stdout: clean(12), stderr: '' }),
    });
    const r = drive(project, spawn);
    assert.equal(r.code, 1, 'a short run fails');
    assert.ok(r.err.includes('12 of 20'), r.err);
  }

  // e. the test host is killed by a signal: dotnet has no exit code at all, and that is not a pass.
  {
    const { spawn } = fake({
      list: () => ({ status: 0, stdout: listing(12), stderr: '' }),
      run: () => ({ status: null, signal: 'SIGKILL', stdout: clean(12), stderr: '' }),
    });
    const r = drive(project, spawn);
    assert.equal(r.code, 1, `a run ended by a signal is not a pass: ${r.out}`);
    assert.ok(r.err.includes('SIGKILL'), r.err);
  }

  // f. dotnet cannot be started at all.
  {
    const { spawn } = fake({
      list: () => ({ status: 0, stdout: listing(12), stderr: '' }),
      run: () => ({ status: null, error: new Error('spawn dotnet ENOENT'), stdout: '', stderr: '' }),
    });
    assert.equal(drive(project, spawn).code, 1, 'a run that never started is not a pass');
  }

  // g. a --filter: the count is measured through the same filter, so the check still runs.
  {
    const { spawn, calls } = fake({
      list: () => ({ status: 0, stdout: listing(5), stderr: '' }),
      run: () => ({ status: 0, stdout: clean(5), stderr: '' }),
    });
    const r = drive([...project, '--filter', 'FullyQualifiedName~Foo'], spawn);
    assert.equal(r.code, 0, `a filtered full run passes: ${r.err}`);
    assert.ok(r.out.includes('5 executed of 5 discovered'), r.out);
    const listCall = calls.find((a) => a.includes('--list-tests'));
    assert.ok(listCall && listCall.includes('--filter') && listCall.includes('FullyQualifiedName~Foo'),
      `discovery must be asked through the same filter, or the count compares two different sets: ${JSON.stringify(calls)}`);
  }

  // h. --no-count-check on a live run skips discovery, says so, and never prints a discovered number.
  {
    const { spawn, calls } = fake({
      list: () => { throw new Error('discovery must not run when the count check is waived'); },
      run: () => ({ status: 0, stdout: clean(12), stderr: '' }),
    });
    const r = drive([...project, '--no-count-check'], spawn);
    assert.equal(r.code, 0, r.err);
    assert.ok(r.out.includes('PASS WITHOUT THE COUNT CHECK'), r.out);
    assert.ok(!/discovered,|of \d+ discovered/.test(r.out), r.out);
    assert.equal(calls.length, 1);
  }
}

// ---- 12. VERIFY (m11/base-fixes round 2, 2026-09-29): an ABORTED discovery is not a count ------------------------
// Measured on this lane's head, SDK 8.0.424, VSTest 17.11.1: `dotnet test Semanticus.Tests/Semanticus.Tests.csproj
// --no-build -c Release --list-tests` with its discovery test host SIGKILLed 2.0 s and 2.4 s in EXITED 0, listed 2,000
// and 3,000 of the 5,341 tests, and ended with the line below. countListed reads only the exit code, so the gate takes
// the partial list as a measured count: it prints "3000 tests discovered", and a clean run of 3,500 prints
// `SUITE GATE: PASS (3500 executed of 3000 discovered ...)` and exits 0 with 1,841 tests never run. That is group 8's
// defect by another road: the gate could not count, and it printed a pass and a number it did not measure.
//
// This test FAILS today. It is the proof of the defect, not a guard over a fix. In process, so it runs everywhere.
{
  const abortedDiscovery = ['Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
    'VSTest version 17.11.1 (x64)', '', 'The following Tests are available:',
    ...Array.from({ length: 3000 }, (_, i) => `    Semanticus.Tests.T${i}`),
    'The active test discovery was aborted. Reason: Test host process crashed'].join('\n');
  for (const executed of [3500, 5347]) {
    const out = []; const err = [];
    const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
      '--log', join(mkdtempSync(join(tmpdir(), 'suite-gate-aborted-discovery-')), 'suite.log')], {
      spawn: (cmd, args) => (args.includes('--list-tests')
        ? { status: 0, stdout: abortedDiscovery, stderr: '' }
        : { status: 0, stdout: CLEAN_RUN.replace('Passed:  5322, Skipped:     4, Total:  5326',
          `Passed: ${String(executed - 4).padStart(5)}, Skipped:     4, Total: ${String(executed).padStart(5)}`), stderr: '' }),
      out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
    });
    const said = [...out, ...err].join('\n');
    assert.ok(!/3000 (tests )?discovered|of 3000/.test(said),
      `discovery was ABORTED, so 3,000 is not a measured count, and the gate printed it as one: ${said.slice(-400)}`);
    assert.notEqual(code, 0,
      `discovery was aborted, so the gate could not count, and it still exited 0 after ${executed} executed: ${said.slice(-400)}`);
  }
}
// ---- 13. VERIFY (m11/base-fixes round 2, 2026-09-29): a log's LAST run is the one that counts --------------------
// readRun takes the FIRST line that looks like a summary. A log holding two runs (a `tee -a`, a retry, Debug then
// Release into one file) is therefore judged by the earlier one: with --discovered 20, a log whose second run
// printed `Failed!  - Failed: 2` gets `SUITE GATE: PASS (20 executed of 20 discovered, 20 passed, ..., 0 failed)` and
// exit 0, and so does one whose second run stopped at 12. There is no exit code in a log to catch it.
//
// This test FAILS today. It is the proof of the defect, not a guard over a fix.
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-two-runs-'));
  const run = (verdict, failed, passed, total) => ['Test run for /repo/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
    `${verdict}  - Failed: ${String(failed).padStart(5)}, Passed: ${String(passed).padStart(5)}, Skipped:     0, `
    + `Total: ${String(total).padStart(5)}, Duration: 2 s - Semanticus.Tests.dll (net8.0)`].join('\n');
  for (const [name, last] of [['failed', run('Failed!', 2, 18, 20)], ['short', run('Passed!', 0, 12, 12)]]) {
    const log = join(dir, `${name}.log`);
    writeFileSync(log, `${run('Passed!', 0, 20, 20)}\n${last}\n`);
    const r = spawnSync(process.execPath, [gate, '--verify-log', log, '--discovered', '20'], { encoding: 'utf8' });
    assert.notEqual(r.status, 0,
      `the log's last run ${name === 'failed' ? 'failed 2 tests' : 'executed 12 of 20'}, and the gate passed it on the `
      + `earlier run's summary: ${r.stdout}`);
    assert.ok(!r.stdout.includes('SUITE GATE: PASS'), `and printed a pass line: ${r.stdout}`);
  }
}
// ---- 14. VERIFY (m11/base-fixes round 3, 2026-09-29): a discovery aborted for ANY reason is not a count ------------
// Group 12's fix reads the discovery text for ABORT_PATTERNS, and for discovery those match one abort REASON only:
// "Test host process crashed". vstest gives another when the host does not exit within about 10 s of losing its channel.
// Measured on this lane's head (SDK 8.0.424, VSTest 17.11.1): with the discovery test host's TCP channel cut (`ss -K` on
// its --port), frozen first or left running, `dotnet test Semanticus.Tests/Semanticus.Tests.csproj --no-build -c Release
// --list-tests` printed the stderr line below, EXITED 0, and listed 1,000, 2,601, 1,000 and 2,901 of the 5,341 tests.
// The gate printed "suite-gate: 1000 tests discovered" and `SUITE GATE: PASS (5347 executed of 1000 discovered, ...)`,
// exit 0. vstest's resource table (Microsoft.TestPlatform.CommunicationUtilities) also holds the bare
// "The active test discovery was aborted." with no reason at all.
//
// This test FAILS today. It is the proof of the defect, not a guard over a fix. In process, so it runs everywhere.
{
  const listing = ['Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
    'VSTest version 17.11.1 (x64)', '', 'The following Tests are available:',
    ...Array.from({ length: 1000 }, (_, i) => `    Semanticus.Tests.T${i}`)].join('\n');
  const channelLost = 'The active test discovery was aborted. Reason: Unable to communicate with test host process.';
  const out = []; const err = [];
  const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
    '--log', join(mkdtempSync(join(tmpdir(), 'suite-gate-channel-lost-')), 'suite.log')], {
    spawn: (cmd, args) => (args.includes('--list-tests')
      ? { status: 0, stdout: listing, stderr: channelLost }
      : { status: 0, stdout: CLEAN_RUN, stderr: '' }),
    out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
  });
  const said = [...out, ...err].join('\n');
  assert.ok(!/1000 (tests )?discovered|of 1000/.test(said),
    `discovery was ABORTED (the channel was lost), so 1,000 is not a measured count, and the gate printed it as one: ${said.slice(-400)}`);
  assert.notEqual(code, 0,
    `discovery was aborted, so the gate could not count, and it still exited 0: ${said.slice(-400)}`);
}
// ---- 15. VERIFY (m11/base-fixes round 3, 2026-09-29): a run that printed no summary still counts as a run ----------
// Group 13's fix judges every summary line in a log. A run stopped before its summary prints none, so in a log that
// also holds a clean run it is judged by nothing at all. Measured on this lane's head (SDK 8.0.424, VSTest 17.11.1) with
// a real 204-test slice: `dotnet test` ended 20 s in by SIGKILL, by SIGTERM (what `timeout` sends) and by Ctrl-C (SIGINT
// to its process group) printed the run header and then nothing (Ctrl-C adds "Attempting to cancel the build...").
// Alone, each of those logs FAILS ("no summary line"). Joined to a clean run of the same slice, first, middle or last,
// with --discovered 204, all fifteen joins printed `SUITE GATE: PASS (204 executed of 204 ...)` and exited 0.
//
// This test FAILS today. It is the proof of the defect, not a guard over a fix.
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-stopped-run-'));
  const header = ['Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
    'VSTest version 17.11.1 (x64)', '', 'Starting test execution, please wait...',
    'A total of 1 test files matched the specified pattern.'];
  const clean = [...header, '',
    'Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204, Duration: 1 m 36 s - Semanticus.Tests.dll (net8.0)',
  ].join('\n');
  const stopped = { killed: header.join('\n'), interrupted: [...header, 'Attempting to cancel the build...'].join('\n') };
  for (const [name, text] of Object.entries(stopped)) {
    for (const [where, runs] of [['last', [clean, text]], ['first', [text, clean]], ['middle', [clean, text, clean]]]) {
      const log = join(dir, `${name}-${where}.log`);
      writeFileSync(log, `${runs.join('\n')}\n`);
      const r = spawnSync(process.execPath, [gate, '--verify-log', log, '--discovered', '204'], { encoding: 'utf8' });
      assert.notEqual(r.status, 0,
        `the ${where} run in this log was ${name} before it printed a summary, and the gate passed the log on the other `
        + `run's summary: ${r.stdout}`);
      assert.ok(!r.stdout.includes('SUITE GATE: PASS'), `and printed a pass line: ${r.stdout}`);
    }
  }
}
// ---- 16. VERIFY (m11/base-fixes round 4, 2026-09-29): the two checks are general, not one pattern each -----------
// Groups 14 and 15 pin one shape each. A2: ANY aborted discovery is not a count, whatever reason vstest gives (an
// arbitrary exception message, or none at all), and so is any discovery that printed a test-host error without the
// abort line (the 100 ms and 300 ms kills print a TestPlatformException and still exit 0). B2: the gate counts the
// runs that STARTED ("Starting test execution") against the summaries printed, and fails when more started than
// summarised, saying how many, in any position and for any number of runs.
{
  const listing = ['Test run for /repo/Semanticus.Tests/bin/Release/net8.0/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)',
    'VSTest version 17.11.1 (x64)', '', 'The following Tests are available:',
    ...Array.from({ length: 1000 }, (_, i) => `    Semanticus.Tests.T${i}`)].join('\n');
  const reasons = [
    ['an exception message as the reason', 'The active test discovery was aborted. Reason: System.IO.IOException: broken pipe'],
    ['the bare form', 'The active test discovery was aborted.'],
    ['a different case and no full stop', 'the active test discovery was aborted'],
    ['a test host error with no abort line', 'Testhost process for source(s) \'/repo/Semanticus.Tests.dll\' exited with error: boom'],
    ['a protocol failure', 'Microsoft.VisualStudio.TestPlatform.ObjectModel.TestPlatformException: Failed to negotiate protocol, waiting for response timed out after 90 seconds'],
    ['the channel lost, worded without the abort line', 'Unable to communicate with test host process.'],
  ];
  for (const [name, line] of reasons) {
    for (const [where, stdout, stderr] of [['stderr', listing, line], ['stdout', `${listing}\n${line}`, '']]) {
      const out = []; const err = [];
      const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
        '--log', join(mkdtempSync(join(tmpdir(), 'suite-gate-any-abort-')), 'suite.log')], {
        spawn: (cmd, args) => (args.includes('--list-tests')
          ? { status: 0, stdout, stderr }
          : { status: 0, stdout: CLEAN_RUN, stderr: '' }),
        out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
      });
      const said = [...out, ...err].join('\n');
      assert.ok(!/1000 (tests )?discovered|of 1000/.test(said), `${name} (${where}): 1,000 is not a measured count: ${said.slice(-300)}`);
      assert.notEqual(code, 0, `${name} (${where}): the gate exited 0 after a discovery that went wrong: ${said.slice(-300)}`);
      assert.match(said, /aborted|test host|testhost|negotiate|communicate/i, `${name} (${where}): the reason must be named: ${said.slice(-300)}`);
    }
  }
  // a complete discovery that merely MENTIONS a host in a test name still counts
  {
    const named = `${listing}\n    Semanticus.Tests.TestHostProcessCrashedTests.Recovers\n`;
    const out = []; const err = [];
    const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
      '--log', join(mkdtempSync(join(tmpdir(), 'suite-gate-named-')), 'suite.log')], {
      spawn: (cmd, args) => (args.includes('--list-tests')
        ? { status: 0, stdout: named, stderr: '' }
        : { status: 0, stdout: CLEAN_RUN.replace('Total:  5326', 'Total:  1001').replace('Passed:  5322', 'Passed:  1001'), stderr: '' }),
      out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
    });
    assert.equal(code, 0, `a complete listing whose test names mention a test host must still count: ${[...out, ...err].join('\n').slice(-300)}`);
  }

  // B2: N runs started, fewer summaries
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-started-'));
  const start = ['Test run for /repo/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)', 'Starting test execution, please wait...'];
  const summary = 'Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204, Duration: 1 m 36 s - Semanticus.Tests.dll (net8.0)';
  const done = [...start, summary].join('\n');
  const cut = start.join('\n');
  const cases = [
    ['1 of 2', [done, cut], /1 of 2 runs|2 runs started.*1 .*summar|started 2.*1/i],
    ['2 of 3', [cut, done, cut], /2 of 3 runs|3 runs started.*1 .*summar|started 3.*1/i],
    ['1 of 3 (two cut)', [cut, cut, done], /1 of 3 runs|3 runs started.*1 .*summar|started 3.*1/i],
    ['all cut but one, a failed one first', [done.replace('Passed!  - Failed:     0, Passed:   204', 'Failed!  - Failed:     1, Passed:   203'), cut], /1 of 2 runs|2 runs started|started 2/i],
  ];
  for (const [name, runs, pattern] of cases) {
    const log = join(dir, `${name.replace(/\W+/g, '-')}.log`);
    writeFileSync(log, `${runs.join('\n')}\n`);
    const r = spawnSync(process.execPath, [gate, '--verify-log', log, '--discovered', '204'], { encoding: 'utf8' });
    assert.notEqual(r.status, 0, `${name}: more runs started than printed a summary, and the gate passed it: ${r.stdout}`);
    assert.ok(!r.stdout.includes('SUITE GATE: PASS'), `${name}: and printed a pass line: ${r.stdout}`);
    assert.match(r.stderr, pattern, `${name}: the failure must say how many runs started against how many summarised: ${r.stderr}`);
  }
  // every run started and finished still passes, and a log with a summary but no header still passes
  for (const [name, text] of [['three whole runs', [done, done, done].join('\n')], ['no header at all', summary]]) {
    const log = join(dir, `ok-${name.replace(/\W+/g, '-')}.log`);
    writeFileSync(log, `${text}\n`);
    const r = spawnSync(process.execPath, [gate, '--verify-log', log, '--discovered', '204'], { encoding: 'utf8' });
    assert.equal(r.status, 0, `${name} must pass: ${r.stderr}`);
  }
}
// ---- 17. JOIN (m11/join, 2026-09-30): the round-4 and round-3 low items ------------------------------------------
// Each part runs even when an earlier one fails, so one run names every part that is red.
{
  const dir = mkdtempSync(join(tmpdir(), 'suite-gate-lows-'));
  const failures = [];
  const part = (name, body) => { try { body(); } catch (e) { failures.push(`${name}: ${e.message}`); } };
  const start = ['Test run for /repo/Semanticus.Tests.dll (.NETCoreApp,Version=v8.0)', 'VSTest version 17.11.1 (x64)', '',
    'Starting test execution, please wait...', 'A total of 1 test files matched the specified pattern.'];
  const summary204 = 'Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204, Duration: 1 m 36 s - Semanticus.Tests.dll (net8.0)';
  const done = [...start, summary204].join('\n');
  const verify = (name, text, discovered = '204') => {
    const log = join(dir, `${name.replace(/\W+/g, '-')}.log`);
    writeFileSync(log, `${text}\n`);
    return spawnSync(process.execPath, [gate, '--verify-log', log, '--discovered', discovered], { encoding: 'utf8' });
  };

  // a. (round 4, low 1) The listed-line filter is what keeps a test NAMED after a gate phrase from being read as a
  // vstest error. A theory row's display name carries its arguments, spaces and all, so a row whose argument is a gate
  // phrase is listed with it. Deleting the filter must turn this red.
  part('a. theory rows named with gate phrases', () => {
    const rows = ['    Semanticus.Tests.GatePhraseTests.Named(reason: "Test host process crashed")',
      '    Semanticus.Tests.GatePhraseTests.Named(reason: "The active test discovery was aborted.")',
      '    Semanticus.Tests.GatePhraseTests.Named(reason: "Test Run Aborted.")',
      '    Semanticus.Tests.GatePhraseTests.Named(reason: "TestPlatformException: Failed to negotiate protocol")'];
    const listing = ['The following Tests are available:', ...rows].join('\n');
    const out = []; const err = [];
    const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
      '--log', join(dir, 'theory-rows.log')], {
      spawn: (cmd, args) => (args.includes('--list-tests')
        ? { status: 0, stdout: listing, stderr: '' }
        : { status: 0, stdout: CLEAN_RUN.replace('Total:  5326', 'Total:     4').replace('Passed:  5322', 'Passed:     4'), stderr: '' }),
      out: (s) => out.push(s), err: (s) => err.push(s), write: (s) => out.push(s),
    });
    const said = [...out, ...err].join('\n');
    assert.equal(code, 0, `a listed theory row whose argument is a gate phrase is a test, not a vstest error: ${said.slice(-400)}`);
    assert.ok(said.includes('4 tests discovered'), `and all four rows are counted: ${said.slice(-400)}`);
  });

  // b. (round 4, low 5) With --verify-log the count was GIVEN, so the short-run failure must not call it discovered.
  part('b. a given count is not called discovered', () => {
    const r = verify('short-given', done, '300');
    assert.notEqual(r.status, 0, `204 of 300 is short: ${r.stdout}`);
    assert.ok(!/discovered tests executed/.test(r.stderr), `a given count is not a discovered one: ${r.stderr}`);
    assert.match(r.stderr, /SHORT RUN: 204 of 300 tests executed \(300 is the count you gave with --discovered/,
      `the failure must say where the count came from: ${r.stderr}`);
  });

  // c. (round 4, low 3) A solution-wide filtered run: the project the filter does not match prints its start line and
  // "No test matches the given testcase filter", and no summary, because it ran nothing. That is a finished run, not a
  // stopped one. A stopped run beside it still fails, and a log in which nothing ran still fails.
  part('c. a project the filter matched nothing in did not stop', () => {
    const noMatch = [...start, 'No test matches the given testcase filter `FullyQualifiedName~Foo` in /repo/Other.Tests.dll'].join('\n');
    for (const [where, runs] of [['after', [done, noMatch]], ['before', [noMatch, done]]]) {
      const r = verify(`no-match-${where}`, runs.join('\n'));
      assert.equal(r.status, 0, `a project the filter matched nothing in (${where}) finished, it did not stop: ${r.stderr}`);
    }
    const r = verify('no-match-and-stopped', [noMatch, done, start.join('\n')].join('\n'));
    assert.notEqual(r.status, 0, `a stopped run is still a stopped run beside a no-match one: ${r.stdout}`);
    assert.match(r.stderr, /1 run\(s\) stopped/, r.stderr);
    const alone = verify('no-match-alone', noMatch);
    assert.notEqual(alone.status, 0, `a log where nothing ran proves nothing: ${alone.stdout}`);
  });

  // d. (round 4, low 2) A run can stop after vstest's "Test run for" line and before its start line. Counting both
  // headers narrows the window a run can hide in.
  part('d. a run that stopped before its start line', () => {
    const r = verify('stopped-before-start', [done, start[0]].join('\n'));
    assert.notEqual(r.status, 0, `a run that printed only "Test run for" did not finish: ${r.stdout}`);
    assert.match(r.stderr, /stopped before finishing/, r.stderr);
  });

  // e. (round 3, low 3) vstest's words are translated by the CLI's UI language, and a translated summary reads as no
  // summary at all. The gate asks dotnet for English on every call it makes, so it reads the words it knows.
  part('e. dotnet is asked for English', () => {
    const envs = [];
    const code = runGate(['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release',
      '--log', join(dir, 'language.log')], {
      spawn: (cmd, args, options) => {
        envs.push(options?.env ?? null);
        return args.includes('--list-tests')
          ? { status: 0, stdout: 'The following Tests are available:\n    Semanticus.Tests.T0', stderr: '' }
          : { status: 0, stdout: CLEAN_RUN.replace('Total:  5326', 'Total:     1').replace('Passed:  5322', 'Passed:     1'), stderr: '' };
      },
      out: () => {}, err: () => {}, write: () => {},
    });
    assert.equal(code, 0);
    assert.equal(envs.length, 2, 'discovery and the run');
    for (const env of envs) {
      assert.ok(env && env.DOTNET_CLI_UI_LANGUAGE === 'en', `dotnet must be asked for English: ${JSON.stringify(env?.DOTNET_CLI_UI_LANGUAGE)}`);
      // Windows spells it Path, and a spread of process.env keeps that spelling, so look the key up case-blind.
      const pathKey = Object.keys(env).find((k) => k.toUpperCase() === 'PATH');
      assert.equal(pathKey && env[pathKey], process.env.PATH, 'and the rest of the environment is passed through as it was');
    }
  });

  assert.deepEqual(failures, [], `group 17 failed:\n${failures.join('\n')}`);
}
// ---- 18. LEAN SUITE (8 Oct): the speed budget --------------------------------------------------------------------
// The default CI run must stay under 900 s and no default test may take 10 s. The gate measures the wall clock around the
// `dotnet test` spawn and reads each test's duration from a trx log it adds to the run. Nothing here waits: the clock is
// injected through io.now, and the trx text through io.readTrx.
{
  const failures = [];
  const part = (name, body) => { try { body(); } catch (e) { failures.push(`${name}: ${e.message}`); } };
  const CLEAN = CLEAN_RUN.replace('Total:  5326', 'Total:     3').replace('Passed:  5322', 'Passed:     3');
  const trxOf = (rows) => ['<?xml version="1.0" encoding="UTF-8"?>', '<TestRun><Results>',
    ...rows.map(([name, duration]) => `  <UnitTestResult executionId="e" testName="${name}" duration="${duration}" outcome="Passed" />`),
    '</Results></TestRun>'].join('\n');
  const FAST = trxOf([['Semanticus.Tests.A.One', '00:00:00.0123456'], ['Semanticus.Tests.A.Two', '00:00:01.5000000'], ['Semanticus.Tests.B.Three', '00:00:00.4000000']]);
  const SLOW = trxOf([['Semanticus.Tests.A.One', '00:00:00.0123456'], ['Semanticus.Tests.Slow.Matrix', '00:00:10.7000000']]);
  const project = ['--project', 'Semanticus.Tests/Semanticus.Tests.csproj', '--configuration', 'Release'];

  /** Drive runGate with a clock that says the run took `seconds`, and a trx that holds `trx` (null: the logger wrote nothing). */
  const drive = (argv, { seconds = 1, trx = FAST, run = CLEAN, listed = 3, calls = [] } = {}) => {
    const out = []; const err = [];
    const dir = mkdtempSync(join(tmpdir(), 'suite-gate-budget-'));
    let tick = 0;
    const code = runGate([...project, ...argv, '--log', join(dir, 'suite.log')], {
      spawn: (cmd, args) => {
        calls.push(args);
        if (args.includes('--list-tests')) {
          return { status: 0, stdout: ['The following Tests are available:', ...Array.from({ length: listed }, (_, i) => `    Semanticus.Tests.T${i}`)].join('\n'), stderr: '' };
        }
        return { status: 0, stdout: run, stderr: '' };
      },
      // The first reading is the start of the run, the second its end.
      now: () => (tick++ === 0 ? 0 : seconds * 1000),
      readTrx: () => trx,
      out: (x) => out.push(x), err: (x) => err.push(x), write: () => {},
    });
    return { code, out: out.join('\n'), err: err.join('\n') };
  };

  part('a. trx durations are read as seconds, with days, and a bad one is not a number', () => {
    assert.equal(parseDuration('00:00:01.5000000'), 1.5);
    assert.equal(parseDuration('00:01:02'), 62);
    assert.equal(parseDuration('1.02:03:04.5'), 86400 + 7200 + 180 + 4.5);
    assert.equal(parseDuration('soon'), null);
    assert.equal(parseDuration(undefined), null);
  });

  part('b. a trx is read by testName and duration; escapes, a quoted ">" and theory rows are kept', () => {
    const tests = readTrx(trxOf([['N.A(x: &quot;a &gt; b&quot;)', '00:00:02.0000000'], ['N.A(x: &quot;c&quot;)', '00:00:03.0000000'],
      ['N.B > raw', '00:00:01.0000000']]));
    assert.deepEqual(tests.map((t) => t.seconds), [2, 3, 1]);
    assert.equal(tests[0].name, 'N.A(x: "a > b")', 'XML escapes are undone');
    assert.equal(tests[2].name, 'N.B > raw', 'a raw ">" inside a quoted attribute does not end the tag');
    assert.equal(tests.length, 3, 'two rows of one theory are two tests');
    assert.equal(readTrx('<TestRun></TestRun>'), null, 'a trx with no result is "not measured", not "no slow tests"');
    assert.equal(readTrx(''), null);
  });

  part('c. judgeBudget on its own: under, at 85%, over, and one slow test', () => {
    const tests = readTrx(SLOW);
    const under = judgeBudget({ elapsed: 100, maxSeconds: 900, tests: readTrx(FAST) });
    assert.deepEqual([under.problems, under.warnings], [[], []]);
    assert.match(under.info[0], /used 100 of 900 s \(11%\)/);
    const near = judgeBudget({ elapsed: 800, maxSeconds: 900, tests: readTrx(FAST) });
    assert.equal(near.problems.length, 0);
    assert.equal(near.warnings.length, 1, 'inside the last 15% is a warning, not a failure');
    const over = judgeBudget({ elapsed: 901, maxSeconds: 900, tests: readTrx(FAST) });
    assert.match(over.problems.join(' '), /901 s and the budget is 900 s/);
    assert.equal(judgeBudget({ elapsed: 900, maxSeconds: 900, tests: readTrx(FAST) }).problems.length, 0, 'exactly the budget is not over it');
    const slow = judgeBudget({ elapsed: 5, maxTestSeconds: 10, tests });
    assert.match(slow.problems.join(' '), /Semanticus\.Tests\.Slow\.Matrix \(10\.7 s\)/);
    assert.equal(judgeBudget({ elapsed: 5, maxTestSeconds: 10, tests: readTrx(FAST) }).problems.length, 0);
    assert.equal(judgeBudget({ elapsed: 5, maxTestSeconds: 10, tests: null }).problems.length, 1, 'durations that were not measured cannot clear a per-test limit');
  });

  part('d. the 15 slowest are printed, slowest first', () => {
    const rows = Array.from({ length: 20 }, (_, i) => [`T.t${i}`, `00:00:0${i % 10}.${String(i).padStart(2, '0')}00000`]);
    const { info } = judgeBudget({ elapsed: 5, wantSlowest: true, tests: readTrx(trxOf(rows)) });
    const lines = info.filter((l) => /^\s+\d/.test(l));
    assert.equal(lines.length, 15);
    const secs = lines.map((l) => Number(l.trim().split(' ')[0]));
    assert.deepEqual(secs, [...secs].sort((a, b) => b - a));
  });

  part('e. a run over --max-seconds FAILS, with the slowest tests printed', () => {
    const r = drive(['--max-seconds', '900', '--max-test-seconds', '10'], { seconds: 901 });
    assert.equal(r.code, 1, `an over-budget run must fail: ${r.out} ${r.err}`);
    assert.match(r.err, /SUITE GATE: FAIL/);
    assert.match(r.err, /901 s and the budget is 900 s/);
    assert.match(r.out, /the 3 slowest of 3 tests/);
    assert.ok(!r.out.includes('SUITE GATE: PASS'), r.out);
  });

  part('f. a run inside the budget passes and says how much it used', () => {
    const r = drive(['--max-seconds', '900', '--max-test-seconds', '10'], { seconds: 412 });
    assert.equal(r.code, 0, `${r.out} ${r.err}`);
    assert.match(r.out, /used 412 of 900 s \(46%\)/);
    assert.match(r.out, /SUITE GATE: PASS/);
    assert.ok(!r.out.includes('::warning::'), r.out);
  });

  part('g. a run at 85% passes with a ::warning::', () => {
    const r = drive(['--max-seconds', '900'], { seconds: 800 });
    assert.equal(r.code, 0);
    assert.match(r.out, /::warning::suite gate budget: the run used 800 of 900 s/);
  });

  part('h. one slow test FAILS the run even when the clock is fine', () => {
    const r = drive(['--max-seconds', '900', '--max-test-seconds', '10'], { seconds: 30, trx: SLOW });
    assert.equal(r.code, 1, `${r.out} ${r.err}`);
    assert.match(r.err, /1 test\(s\) took more than 10 s each/);
    assert.match(r.err, /Semanticus\.Tests\.Slow\.Matrix/);
  });

  part('i. report-only (the self-hosted Linux leg) prints the same and never fails on a budget', () => {
    const r = drive(['--max-seconds', '900', '--max-test-seconds', '10', '--budget-report-only'], { seconds: 2000, trx: SLOW });
    assert.equal(r.code, 0, `${r.out} ${r.err}`);
    assert.match(r.out, /used 2000 of 900 s/);
    assert.match(r.out, /::warning::suite gate budget \(report only, not enforced here\): the run took 2000 s/);
    assert.match(r.out, /::warning::suite gate budget \(report only, not enforced here\): 1 test\(s\) took more than 10 s/);
    assert.match(r.out, /SUITE GATE: PASS/);
  });

  part('j. report-only never excuses a failed, aborted or short run', () => {
    const failed = drive(['--max-seconds', '900', '--budget-report-only'], { run: FAILED_RUN.replace('Total:  5326', 'Total:     3') });
    assert.equal(failed.code, 1, 'a failing test still fails');
    const short = drive(['--max-seconds', '900', '--budget-report-only'], { listed: 10 });
    assert.equal(short.code, 1, 'executed 3 of 10 discovered is still a short run');
    assert.match(short.err, /SHORT RUN/);
  });

  part('k. executed == discovered holds per filter: both calls take the filter, and a short run under it fails', () => {
    for (const filter of ['Cost!=sweep', 'Cost=sweep']) {
      const calls = [];
      const r = drive(['--filter', filter, '--max-seconds', '900'], { calls, listed: 5 });
      assert.equal(r.code, 1, `${filter}: 3 executed of 5 discovered must fail`);
      assert.match(r.err, /SHORT RUN: 3 of 5/);
      assert.equal(calls.length, 2);
      for (const args of calls) assert.equal(args[args.indexOf('--filter') + 1], filter, `${filter}: discovery and run take the same filter`);
    }
  });

  part('l. the trx logger is added only when a budget or --slowest asks for it', () => {
    const plain = []; drive([], { calls: plain });
    assert.ok(!plain.flat().includes('--logger'), 'no budget flag, no logger: a plain run is the run it always was');
    const budgeted = []; drive(['--max-seconds', '900'], { calls: budgeted });
    const run = budgeted.find((a) => !a.includes('--list-tests'));
    assert.ok(run.includes('--logger') && run.some((a) => String(a).startsWith('trx;LogFileName=')), 'the run writes a trx');
    assert.ok(!budgeted.find((a) => a.includes('--list-tests')).includes('--logger'), 'discovery does not');
    const slowest = []; const r = drive(['--slowest'], { calls: slowest });
    assert.equal(r.code, 0);
    assert.match(r.out, /the 3 slowest of 3 tests/);
    assert.ok(!r.out.includes('used '), 'with no budget there is no "used X of N"');
  });

  part('m. a budget that cannot be measured is not a pass when enforced, and a warning when report-only', () => {
    const noTrx = drive(['--max-test-seconds', '10'], { trx: null });
    assert.equal(noTrx.code, 1, `${noTrx.out} ${noTrx.err}`);
    assert.match(noTrx.err, /not a pass/);
    const reportOnly = drive(['--max-test-seconds', '10', '--budget-report-only'], { trx: null });
    assert.equal(reportOnly.code, 0);
    assert.match(reportOnly.out, /::warning::/);
    // The wall clock needs no trx: an over-budget run fails without one.
    assert.equal(drive(['--max-seconds', '10'], { trx: null, seconds: 11 }).code, 1);
  });

  part('n. a budget that is not a number is an error, never "no budget"', () => {
    for (const bad of [['--max-seconds', 'soon'], ['--max-seconds', '0'], ['--max-test-seconds', '-3'], ['--max-seconds']]) {
      const r = drive(bad);
      assert.equal(r.code, 2, `${bad.join(' ')} must not run the suite: ${r.out} ${r.err}`);
      assert.match(r.err, /needs a positive number of seconds/);
    }
  });

  part('o. the discovery wall time is not in the budget', () => {
    // 17 s of discovery at 12,000 tests must not eat the budget: only the run is timed.
    let clock = 0;
    const out = [];
    const dir = mkdtempSync(join(tmpdir(), 'suite-gate-clock-'));
    runGate([...project, '--max-seconds', '900', '--log', join(dir, 'suite.log')], {
      spawn: (cmd, args) => {
        if (args.includes('--list-tests')) { clock += 17_000; return { status: 0, stdout: 'The following Tests are available:\n    A.B\n    A.C\n    A.D', stderr: '' }; }
        clock += 10_000;
        return { status: 0, stdout: CLEAN, stderr: '' };
      },
      now: () => clock, readTrx: () => FAST,
      out: (x) => out.push(x), err: () => {}, write: () => {},
    });
    assert.match(out.join('\n'), /used 10 of 900 s/, out.join('\n'));
  });

  assert.deepEqual(failures, [], `group 18 failed:\n${failures.join('\n')}`);
}
console.log('suite-gate tests passed (18 groups)');
