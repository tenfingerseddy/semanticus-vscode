#!/usr/bin/env node
// Runs a .NET test project and refuses to call a partial run green.
//
// Why this exists: on 2026-09-28 a Debug run of the engine suite logged `The active test run was aborted.
// Reason: Test host process crashed`, executed 2,312 of 5,320 tests, and then printed
// `Passed!  - Failed: 0, Passed: 2311` as its summary. A person or an agent reading the last line was told the
// suite passed. The cause of that crash is NOT known: it was never reproduced, and the one theory offered for
// it (the host running out of file descriptors) was disproved. Killing a test host from outside reproduces the
// same output shape. What that run's exit code was is also not known, because its log is gone; in three
// controlled kills since, `dotnet test` exited 1. So do not read this gate as a fix for an exit code. It exists
// because the printed text can claim a pass for a run that stopped part way, and a count is the only thing that
// tells the two apart.
//
// It asks four questions that `dotnet test` does not:
//   1. Did the run abort, or did a test host crash? Either one is a failure, whatever the exit code says.
//   2. Is there a summary line for every run that started? A run that dies before printing one is not a pass, even
//      when another run in the same output printed one. A run "started" when it printed vstest's "Test run for" line
//      or its "Starting test execution" line (whichever count is higher). A run that printed neither, such as one
//      whose build failed or that was stopped before vstest began, leaves nothing in the output to count, so a log
//      that joins it to a clean run cannot show it. A project that a --filter matched nothing in prints its start
//      line and "No test matches the given testcase filter" with no summary; that run finished, and is not counted
//      as stopped.
//   3. Did it execute every test it discovered? A short count is a failure, and the message names the shortfall.
//      Only SHORT counts fail. Executed can legitimately exceed discovered, because a theory whose rows xunit
//      cannot serialise is listed once by discovery and expanded into every row when it runs. The cost of that
//      slack is that a full run missing a few tests (as many as the over-count, six at the time of writing) still
//      passes: the count catches a run that stops part way, not one dropped test.
//      Discovery is read the same way as the run: when the discovery test host is killed, `dotnet test --list-tests`
//      still exits 0 and prints its own abort line ("The active test discovery was aborted") over a partial list, so
//      the abort text, not only the exit code, decides whether a count was measured.
//   4. Did it learn the discovered count at all? A gate that could not measure it cannot tell a short run from
//      a full one, so it FAILS and says why. It never prints a discovered number it did not measure.
//
// A fifth question is asked only when the caller sets a budget (lean suite, 8 Oct): was the run fast enough?
//   --max-seconds N         wall clock around the `dotnet test` spawn (discovery is not in it). Over N is a FAIL.
//   --max-test-seconds N    one test over N seconds is a FAIL. Read from a trx log the gate adds to the run.
//   --slowest               print the 15 slowest tests and "used X of N s" without setting a budget.
//   --budget-report-only    print everything, warn, never fail on a budget. For a runner whose load is not controlled
//                           (the self-hosted Linux leg); the hosted Windows leg enforces. A budget the gate could not
//                           measure (no trx) FAILS when enforced and warns when report-only: unknown is not a pass.
// Each run is counted through its own --filter, so `Cost!=sweep` and `Cost=sweep` each hold executed == discovered.
//
// The one way past question 4 is --no-count-check, which says so in the verdict itself
// (`SUITE GATE: PASS WITHOUT THE COUNT CHECK`). CI never passes it, and suite-gate-wiring.test.mjs holds that.
//
// Usage:
//   node tools/suite-gate.mjs --project Semanticus.Tests/Semanticus.Tests.csproj --configuration Release
//   node tools/suite-gate.mjs --project <csproj> --configuration Debug --log <path> -- --blame
//   node tools/suite-gate.mjs --project <csproj> --filter "FullyQualifiedName~Foo"   (counted through the filter)
//   node tools/suite-gate.mjs --verify-log <path> --discovered 5324        (judge a log that already exists)
//     (--verify-log judges every run that printed a header; a run whose build failed, or that stopped before vstest
//     printed anything, is invisible in a log, so judge a log of one run where you can. It has no exit code to read.)
//
// Every dotnet call is made with DOTNET_CLI_UI_LANGUAGE=en: vstest translates its summary and abort lines, and the
// gate reads the English ones, so a translated run would otherwise fail as "no summary line".
//
// Discovery runs `dotnet test --list-tests --no-build` with the same filter and pass-through arguments, so the
// configuration must already be built; a configuration that is not fails discovery, and the gate fails with it.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const ABORT_PATTERNS = [
  /The active test run was aborted/i,
  /Test host process crashed/i,
  /Test Run Aborted/i,
];

// What vstest prints when DISCOVERY goes wrong, whatever the reason. It words the abort two ways ("The active test
// discovery was aborted." and "... Reason: {0}", where the reason is a crash, a lost channel or an exception's own
// message), and a host that dies before it lists anything prints a TestPlatformException instead and still exits 0.
// So the check is on the kind of message, not on one reason.
export const DISCOVERY_ERROR_PATTERNS = [
  /test discovery was aborted/i,
  /test ?host process/i,
  /Failed to negotiate protocol/i,
  /TestPlatformException/i,
];

// The two headers vstest prints when a run begins, once per run (and per target framework, like the summary):
// "Test run for <dll> (<framework>)" first, then "Starting test execution". A run is counted by whichever appears
// more often, so a run stopped between the two is still seen.
const RUN_STARTED = /Starting test execution/g;
const RUN_FOR = /^Test run for /gm;
// A run that a --filter matched nothing in ends with this line and no summary. It ran nothing and it finished.
const NO_MATCH = /No test matches the given testcase filter/g;

const SUMMARY = /(Passed!|Failed!)\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)/g;

/**
 * Everything the gate can learn from the run's own output. Pure, so the tests can feed it captured logs.
 * `summaries` holds EVERY summary line: a log can hold several runs (a `tee -a`, a retry, Debug then Release), and
 * a multi-target project prints one per framework, so judging only the first would hide a later failure. `summary`
 * is the last one, for the pass line. `started` counts the runs that began, so a run stopped before its summary
 * cannot hide behind another run's, and `noMatch` counts the runs that a filter matched nothing in, which finish
 * with no summary.
 */
export function readRun(text) {
  const aborted = ABORT_PATTERNS.filter((p) => p.test(text)).map((p) => String(p));
  const summaries = [...text.matchAll(SUMMARY)].map((match) => ({
    verdict: match[1],
    failed: Number(match[2]),
    passed: Number(match[3]),
    skipped: Number(match[4]),
    total: Number(match[5]),
  }));
  const started = Math.max([...text.matchAll(RUN_STARTED)].length, [...text.matchAll(RUN_FOR)].length);
  const noMatch = [...text.matchAll(NO_MATCH)].length;
  return { aborted, summary: summaries.length === 0 ? null : summaries[summaries.length - 1], summaries, started, noMatch };
}

/** A discovered count is a positive whole number or it is not a count. */
function asCount(value) {
  return Number.isInteger(value) && value > 0 ? value : null;
}

/**
 * The verdict. `discovered` is null when the gate could not measure it, and then the run fails unless the
 * caller waived the check out loud (`countWaived`); `whyUncounted` says why it is missing. `exitCode` is
 * undefined when there is no exit code to read (a log judged after the fact); null means the process had none,
 * because a signal ended it or it never started, and that is a failure. `countGiven` says the count came from the
 * caller (--discovered), not from a discovery, so no message calls it discovered.
 */
export function judge({ text, exitCode, discovered, countWaived = false, whyUncounted = null, signal = null, countGiven = false }) {
  const { aborted, summary, summaries, started, noMatch } = readRun(text);
  const problems = [];
  const counted = asCount(discovered);

  if (aborted.length > 0) {
    problems.push(
      `the run ABORTED (matched ${aborted.join(', ')}). vstest can still print a Passed! line after this, `
      + 'which is exactly the misreading this gate exists to stop.');
  }
  if (summary === null) {
    problems.push('no summary line was printed at all, so nothing here says how many tests ran.');
  }
  const stopped = started - summaries.length - noMatch;
  if (stopped > 0) {
    problems.push(
      `${started} runs started ("Test run for" or "Starting test execution") but only ${summaries.length} printed a summary`
      + `${noMatch > 0 ? ` and ${noMatch} matched no test` : ''}, so `
      + `${stopped} run(s) stopped before finishing. A run that dies before printing one is not a pass, `
      + 'even when another run in the same output did.');
  }
  summaries.forEach((s, i) => {
    const at = summaries.length > 1 ? ` (summary ${i + 1} of ${summaries.length} in this output)` : '';
    if (s.verdict === 'Failed!' || s.failed > 0) problems.push(`${s.failed} test(s) failed${at}.`);
    if (counted !== null && s.total < counted) {
      problems.push(
        `SHORT RUN${at}: ${s.total} of ${counted} `
        + (countGiven ? `tests executed (${counted} is the count you gave with --discovered, not one measured here)` : 'discovered tests executed')
        + `, so ${counted - s.total} never ran. A suite that stops early is not a suite that passed.`);
    }
  });
  if (counted === null && !countWaived) {
    problems.push(
      `the discovered count is UNKNOWN (${whyUncounted ?? 'no count was given'}), so a short run cannot be told `
      + 'from a full one and this is not a pass. Build the configuration so discovery works, or give the count '
      + 'with --discovered <n> when judging a log. Only --no-count-check waives this, and it says so in the verdict.');
  }
  if (exitCode === null) {
    problems.push(signal
      ? `dotnet test was ended by ${signal} and has no exit code, so nothing it printed can be trusted as a verdict.`
      : 'dotnet test has no exit code: it never started, or something ended it from outside.');
  } else if (problems.length === 0 && exitCode !== 0 && exitCode !== undefined) {
    problems.push(`dotnet test exited ${exitCode} while its own output looked clean.`);
  }
  return { ok: problems.length === 0, problems, summary, discovered: counted };
}

// ---- the budget ----------------------------------------------------------------------------------------------------

/** How many of the slowest tests the gate prints. */
export const SLOWEST_SHOWN = 15;
/** A warning appears at this share of the budget, before the run is over it. */
export const WARN_AT = 0.85;

const UNESCAPES = { '&lt;': '<', '&gt;': '>', '&amp;': '&', '&quot;': '"', '&apos;': "'" };
const unescapeXml = (text) => text.replace(/&(?:lt|gt|amp|quot|apos);/g, (m) => UNESCAPES[m]);

/** "00:00:01.2345678" or "1.02:03:04.5" (a trx duration) in seconds, or null when it is not one. */
export function parseDuration(text) {
  const m = /^(?:(\d+)\.)?(\d+):(\d{2}):(\d{2}(?:\.\d+)?)$/.exec(text ?? '');
  if (m === null) return null;
  return Number(m[1] ?? 0) * 86400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]);
}

/**
 * Every executed test in a trx log, as { name, seconds }. A regex, not an XML parser: the trx is machine written, the
 * element is flat, and Node has no XML reader built in. A quoted `>` inside an attribute does not end the tag.
 * Returns null when the text holds no UnitTestResult at all (the logger did not run), which is not the same as zero tests.
 */
export function readTrx(text) {
  const tests = [];
  for (const tag of text.matchAll(/<UnitTestResult\b((?:[^>"]|"[^"]*")*)>/g)) {
    const attrs = {};
    for (const a of tag[1].matchAll(/([\w:]+)="([^"]*)"/g)) attrs[a[1]] = unescapeXml(a[2]);
    const seconds = parseDuration(attrs.duration);
    if (attrs.testName !== undefined && seconds !== null) tests.push({ name: attrs.testName, seconds });
  }
  return tests.length === 0 ? null : tests;
}

/**
 * The speed verdict. `elapsed` is the wall clock around dotnet test in seconds; `tests` is readTrx's result or null.
 * Pure, so the tests can plant an over-budget run and a slow test without waiting for either. Returns the lines to
 * print, the budget problems (the caller fails on them unless report-only), and warnings.
 */
export function judgeBudget({ elapsed, maxSeconds = null, maxTestSeconds = null, tests, wantSlowest = false }) {
  const problems = [];
  const warnings = [];
  const info = [];
  if (maxSeconds !== null) {
    const share = Math.round((elapsed / maxSeconds) * 100);
    info.push(`suite-gate: used ${elapsed.toFixed(0)} of ${maxSeconds} s (${share}%).`);
    if (elapsed > maxSeconds) {
      problems.push(`the run took ${elapsed.toFixed(0)} s and the budget is ${maxSeconds} s (--max-seconds). `
        + 'Cheapen the slowest tests below, or move a heavy one to the sweep with [Trait("Cost", "sweep")].');
    } else if (elapsed >= maxSeconds * WARN_AT) {
      warnings.push(`the run used ${elapsed.toFixed(0)} of ${maxSeconds} s (${share}%): within ${Math.round((1 - WARN_AT) * 100)}% of the budget.`);
    }
  } else if (wantSlowest) {
    info.push(`suite-gate: the run took ${elapsed.toFixed(0)} s.`);
  }
  const measureTests = maxTestSeconds !== null || wantSlowest || maxSeconds !== null;
  if (measureTests) {
    if (tests === null) {
      const why = 'no test durations were read (the trx log is missing or empty), so the slowest tests and the per-test limit '
        + 'could not be measured.';
      if (maxTestSeconds !== null) problems.push(`${why} A per-test limit that cannot be measured is not a pass.`);
      else warnings.push(why);
    } else {
      const slowest = [...tests].sort((a, b) => b.seconds - a.seconds).slice(0, SLOWEST_SHOWN);
      info.push(`suite-gate: the ${slowest.length} slowest of ${tests.length} tests:`);
      for (const t of slowest) info.push(`  ${t.seconds.toFixed(2).padStart(8)} s  ${t.name}`);
      if (maxTestSeconds !== null) {
        const over = tests.filter((t) => t.seconds > maxTestSeconds).sort((a, b) => b.seconds - a.seconds);
        if (over.length > 0) {
          problems.push(`${over.length} test(s) took more than ${maxTestSeconds} s each (--max-test-seconds): `
            + `${over.slice(0, SLOWEST_SHOWN).map((t) => `${t.name} (${t.seconds.toFixed(1)} s)`).join('; ')}`
            + `${over.length > SLOWEST_SHOWN ? `; and ${over.length - SLOWEST_SHOWN} more` : ''}. `
            + 'Cheapen it, or move it to the sweep with [Trait("Cost", "sweep")].');
        }
      }
    }
  }
  return { info, problems, warnings };
}

/** A budget is a positive number; anything else given with a budget flag is an error, never a silent "no budget". */
function asSeconds(value) {
  return /^\d+(\.\d+)?$/.test(value ?? '') && Number(value) > 0 ? Number(value) : null;
}

function parseArgs(argv) {
  const out = { passthrough: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--') { out.passthrough = argv.slice(i + 1); break; }
    if (!arg.startsWith('--')) continue;
    const key = arg.slice(2);
    const value = argv[i + 1];
    if (key === 'project' || key === 'configuration' || key === 'filter' || key === 'log' || key === 'verify-log') {
      out[key] = value; i += 1;
    } else if (key === 'discovered') {
      // Number('') is 0 and Number('12abc') is NaN; a count has to be written as one.
      out.discovered = /^\d+$/.test(value ?? '') ? Number(value) : null; i += 1;
    } else if (key === 'no-count-check') {
      out.noCountCheck = true;
    } else if (key === 'max-seconds' || key === 'max-test-seconds') {
      const seconds = asSeconds(value);
      if (seconds === null) out.badBudget = `--${key} needs a positive number of seconds (got ${JSON.stringify(value ?? null)})`;
      else out[key === 'max-seconds' ? 'maxSeconds' : 'maxTestSeconds'] = seconds;
      i += 1;
    } else if (key === 'slowest') {
      out.slowest = true;
    } else if (key === 'budget-report-only') {
      out.budgetReportOnly = true;
    }
  }
  return out;
}

function defaultSpawn(cmd, args, options = {}) {
  return spawnSync(cmd, args, { encoding: 'utf8', maxBuffer: 512 * 1024 * 1024, ...options });
}

// English output for every dotnet call (see the header): the patterns below are vstest's English words.
function dotnetOptions() {
  return { env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' } };
}

function countListed(spawn, project, configuration, filter, passthrough) {
  const args = ['test', project, '--no-build', '-c', configuration, '--list-tests'];
  if (filter) args.push('--filter', filter);
  args.push(...passthrough);
  const run = spawn('dotnet', args, dotnetOptions());
  if (run.error) return { count: null, why: `--list-tests could not start: ${run.error.message}` };
  // A newline between the streams: stdout can end mid-line, and a stderr line glued to a listed test would read as one.
  const text = `${run.stdout ?? ''}\n${run.stderr ?? ''}`;
  if (run.status !== 0) {
    return { count: null, why: `--list-tests exited ${run.status ?? `by ${run.signal ?? 'an unknown cause'}`}` };
  }
  // A discovery that goes wrong can still exit 0 and print a PARTIAL list (measured on SDK 8.0.424: after every kill of
  // the discovery test host, `dotnet test --list-tests` exited 0, and the list arrives in batches of 1,000). Whatever the reason vstest gives, that list is not a count.
  // Listed tests are indented, so only the other lines are read; a test named after a host cannot trip this.
  const problem = text.split(/\r?\n/).filter((line) => !/^\s{4}\S/.test(line))
    .find((line) => [...ABORT_PATTERNS, ...DISCOVERY_ERROR_PATTERNS].some((p) => p.test(line)));
  if (problem !== undefined) {
    return { count: null, why: `--list-tests went wrong and exited 0 with a partial or empty list; vstest said: "${problem.trim().slice(0, 240)}"` };
  }
  // vstest indents each discovered test under "The following Tests are available:".
  const lines = text.split(/\r?\n/).filter((line) => /^\s{4}\S/.test(line));
  return lines.length > 0 ? { count: lines.length, why: null } : { count: null, why: '--list-tests listed no tests' };
}

function shout(err, lines) {
  const bar = '='.repeat(96);
  err(`\n${bar}\nSUITE GATE: FAIL\n${bar}`);
  for (const line of lines) err(`  ${line}`);
  err(`${bar}\n`);
}

const WAIVED = 'SUITE GATE: PASS WITHOUT THE COUNT CHECK';

/** The whole gate, with its process and file effects passed in so the tests can drive it on any platform. */
export function runGate(argv, io = {}) {
  const spawn = io.spawn ?? defaultSpawn;
  const out = io.out ?? ((s) => console.log(s));
  const err = io.err ?? ((s) => console.error(s));
  const write = io.write ?? ((s) => process.stdout.write(s));
  const now = io.now ?? (() => Date.now());
  const opts = parseArgs(argv);
  if (opts.badBudget) { err(`suite-gate: ${opts.badBudget}.`); return 2; }
  const waived = opts.noCountCheck === true;

  if (opts['verify-log']) {
    const text = readFileSync(opts['verify-log'], 'utf8');
    const discovered = opts.discovered ?? null;
    const verdict = judge({
      text, exitCode: undefined, discovered, countWaived: waived, countGiven: true,
      whyUncounted: 'a log was given with no valid --discovered <n>',
    });
    if (!verdict.ok) { shout(err, [`log: ${opts['verify-log']}`, ...verdict.problems]); return 1; }
    const s = verdict.summary;
    out(verdict.discovered === null
      ? `${WAIVED} (${s.total} executed; the discovered count was never given, and --no-count-check waived that `
        + `check. The exit code is not in a log, so it was not checked either.) Log: ${opts['verify-log']}`
      : `SUITE GATE: PASS (${s.total} executed of ${verdict.discovered} (the count you gave with --discovered, not `
        + `measured here), ${s.passed} passed, ${s.skipped} skipped, ${s.failed} failed; the exit code is not in a `
        + `log, so it was not checked). `
        + `Log: ${opts['verify-log']}`);
    return 0;
  }

  if (!opts.project) {
    err('suite-gate: --project <csproj> is required (or --verify-log <path> --discovered <n>).');
    return 2;
  }
  const configuration = opts.configuration ?? 'Release';
  const logPath = opts.log ?? join(mkdtempSync(join(tmpdir(), 'suite-gate-')), 'suite.log');

  let discovered = null;
  let why = null;
  if (waived) {
    why = '--no-count-check was given, so discovery was not run';
    out(`suite-gate: --no-count-check was given, so the executed-vs-discovered count will NOT be checked and `
      + 'this run cannot prove it was complete. Abort, failure and exit-code checks still apply.');
  } else {
    const listed = countListed(spawn, opts.project, configuration, opts.filter, opts.passthrough);
    discovered = listed.count;
    why = listed.why;
    if (discovered === null) {
      out(`suite-gate: could not discover a test count (${why}). The run goes ahead so its output is seen, `
        + 'but without a count it cannot pass.');
    } else {
      out(`suite-gate: ${discovered} tests discovered in ${opts.project} (${configuration}`
        + `${opts.filter ? `, filter ${opts.filter}` : ''}).`);
    }
  }

  const args = ['test', opts.project, '--no-build', '-c', configuration];
  if (opts.filter) args.push('--filter', opts.filter);
  // A trx log is added only when a budget or --slowest asks for durations, so a plain run is exactly the run it was.
  const wantTimes = opts.maxSeconds !== undefined || opts.maxTestSeconds !== undefined || opts.slowest === true;
  let trxPath = null;
  if (wantTimes) {
    const trxDir = mkdtempSync(join(tmpdir(), 'suite-gate-trx-'));
    trxPath = join(trxDir, 'suite.trx');
    args.push('--results-directory', trxDir, '--logger', 'trx;LogFileName=suite.trx');
  }
  args.push(...opts.passthrough);
  const startedAt = now();
  const run = spawn('dotnet', args, dotnetOptions());
  const elapsed = (now() - startedAt) / 1000;
  const text = `${run.stdout ?? ''}${run.stderr ?? ''}`;
  writeFileSync(logPath, text);
  write(text);

  const verdict = judge({
    text, exitCode: run.error ? null : run.status, signal: run.signal ?? null,
    discovered, countWaived: waived, whyUncounted: why,
  });
  if (run.error) verdict.problems.unshift(`dotnet test could not start: ${run.error.message}`);

  let budget = { info: [], problems: [], warnings: [] };
  if (wantTimes) {
    const trx = trxPath !== null && (io.readTrx ?? ((path) => (existsSync(path) ? readFileSync(path, 'utf8') : null)))(trxPath);
    budget = judgeBudget({
      elapsed, maxSeconds: opts.maxSeconds ?? null, maxTestSeconds: opts.maxTestSeconds ?? null,
      tests: typeof trx === 'string' ? readTrx(trx) : null, wantSlowest: opts.slowest === true,
    });
    for (const line of budget.info) out(line);
    // A warning is a CI annotation; report-only turns a budget problem into one, so the Linux leg shows it without failing.
    for (const w of budget.warnings) out(`::warning::suite gate budget: ${w}`);
    if (opts.budgetReportOnly) {
      for (const p of budget.problems) out(`::warning::suite gate budget (report only, not enforced here): ${p}`);
    }
  }
  const budgetFails = opts.budgetReportOnly ? [] : budget.problems;
  if (!verdict.ok || budgetFails.length > 0) {
    shout(err, [`log: ${logPath}`, ...verdict.problems, ...budgetFails]);
    return 1;
  }

  const s = verdict.summary;
  out(verdict.discovered === null
    ? `${WAIVED} (${s.total} executed, ${s.passed} passed, ${s.skipped} skipped, ${s.failed} failed; `
      + `the discovered count was not measured, so nothing here shows the run was complete). Log: ${logPath}`
    : `SUITE GATE: PASS (${s.total} executed of ${verdict.discovered} discovered, `
      + `${s.passed} passed, ${s.skipped} skipped, ${s.failed} failed). Log: ${logPath}`);
  return 0;
}

if (process.argv[1] && process.argv[1].endsWith('suite-gate.mjs')) process.exit(runGate(process.argv.slice(2)));
