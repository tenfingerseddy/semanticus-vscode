// The suite gate is only worth something while CI actually calls it. Nothing auto-discovers a file under
// tools/, and a bare `dotnet test` line reads as a perfectly normal CI step, so the way this gate dies is
// quietly: someone simplifies the workflow back to `run: dotnet test ...` and nobody notices until the next
// aborted run is reported as green. This file is the thing that notices.
//
// Same arrangement as findings-register.test.mjs does for tools/findings-fixed-commits.test.mjs.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const SELF_TEST = 'node tools/suite-gate.test.mjs';
const GATED = 'node tools/suite-gate.mjs --project Semanticus.Tests/Semanticus.Tests.csproj';
// The lean suite (Kane 8 Oct): the default run is the tier without [Trait("Cost", "sweep")], inside a 900 s budget and a
// 10 s per-test limit. The budget is enforced on the hosted Windows leg and only reported on the self-hosted Linux leg.
const DEFAULT_FILTER = '--filter "Cost!=sweep"';
const SWEEP_FILTER = '--filter "Cost=sweep"';
const BUDGET_FLAGS = ['--max-seconds 900', '--max-test-seconds 10'];
// 1.3.0: lanes A and C cut after release. True while the budget only reports on every leg.
const RELEASE_REPORT_ONLY = true;
const REPORT_ONLY_ON_LINUX = "${{ matrix.os == 'ubuntu-latest' && '--budget-report-only' || '' }}";

const escapeRe = (text) => text.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');

/** The step whose `run:` value is `command`: the problems with it (empty means it runs and can fail). */
function stepProblems(lines, command, where) {
  const problems = [];
  const runAt = lines.findIndex((line) => new RegExp(`^\\s*(- )?run: ${escapeRe(command)}(\\s[^#]*)?$`).test(line));
  if (runAt < 0) {
    problems.push(`no step's run line in ${where} is exactly "run: ${command} ..." (with no comment after it), so the step that holds `
      + 'the text may not run it');
    return problems;
  }
  let first = runAt;
  while (first > 0 && !/^\s*- /.test(lines[first])) first -= 1;
  const dashIndent = lines[first].search(/\S/);
  let last = runAt;
  while (last + 1 < lines.length && (lines[last + 1].trim() === '' || lines[last + 1].search(/\S/) > dashIndent)) last += 1;
  const step = lines.slice(first, last + 1);
  const gate = step.filter((line) => /^\s*(- )?(if|continue-on-error):/.test(line));
  if (gate.length > 0) {
    problems.push(`the step in ${where} that runs "${command}" can be skipped or cannot fail the job: ${gate.map((l) => l.trim()).join(' | ')}`);
  }
  return problems;
}

/** Every way the nightly sweep workflow fails to run the sweep tier through the gate. Empty means it is wired. */
function sweepProblems(raw) {
  const problems = [];
  const sweep = raw.split(/\r?\n/).filter((line) => !/^\s*#/.test(line)).join('\n');
  const lines = sweep.split(/\r?\n/);
  problems.push(...stepProblems(lines, GATED, 'sweep.yml'));
  const run = lines.find((line) => new RegExp(`^\\s*(- )?run: ${escapeRe(GATED)}(\\s[^#]*)?$`).test(line)) ?? '';
  if (!run.includes(SWEEP_FILTER)) problems.push(`sweep.yml's gated step does not carry ${SWEEP_FILTER}: ${run.trim()}`);
  if (run.includes(DEFAULT_FILTER)) problems.push('sweep.yml runs the DEFAULT tier, which is what ci.yml runs');
  if (run.includes('--no-count-check')) problems.push('sweep.yml passes --no-count-check, so an uncounted sweep would read as a pass');
  if (/^\s{2}schedule:/m.test(sweep) === false) problems.push('sweep.yml has no schedule: trigger, so nothing runs the sweep nightly');
  if (/^\s{2}workflow_dispatch:/m.test(sweep) === false) problems.push('sweep.yml has no workflow_dispatch: trigger, so the sweep cannot be run on demand');
  if (!sweep.includes('if: github.event.repository.private == true')) {
    problems.push('sweep.yml does not skip itself on the public mirror (github.event.repository.private == true), where no runner of ours is registered');
  }
  if (!sweep.includes('fromJSON(\'["self-hosted","linux","semanticus"]\')')) problems.push('sweep.yml does not run on the self-hosted Linux runner');
  return problems;
}

/** Every way the workflow text fails to run the gate. Empty means it is wired. */
function wiringProblems(raw) {
  const problems = [];
  // Comment lines are dropped first: a step commented out still leaves its text in the file, and must not satisfy
  // the checks below.
  const ci = raw.split(/\r?\n/).filter((line) => !/^\s*#/.test(line)).join('\n');

  if (!ci.includes(SELF_TEST)) {
    problems.push('ci.yml does not run tools/suite-gate.test.mjs, so the gate could stop being able to fail and CI would not say so');
  }
  if (!ci.includes(GATED)) {
    problems.push('ci.yml does not run the engine suite through tools/suite-gate.mjs, so an aborted run would report as green again');
  }

  // The text being in the file is not the step running. Each command must BE its step's `run:` value, on the run line
  // itself (not behind `echo ... #`, not in a folded or literal block where a flag can sit on the next line), and the
  // step must be one that runs and can fail: no `if:` and no `continue-on-error:`.
  const lines = ci.split(/\r?\n/);
  for (const command of [SELF_TEST, GATED]) problems.push(...stepProblems(lines, command, 'ci.yml'));

  // The default tier, with its budget. Without the filter the run would include the sweep tier (a 16 minute gate and every
  // matrix) on every pull request; without a budget flag nothing stops the suite from growing back to hours.
  const gateRun = lines.find((line) => new RegExp(`^\\s*(- )?run: ${escapeRe(GATED)}(\\s[^#]*)?$`).test(line)) ?? '';
  for (const flag of [DEFAULT_FILTER, ...BUDGET_FLAGS]) {
    if (!gateRun.includes(flag)) problems.push(`the gated engine-suite step does not carry ${flag}, so the default tier or its budget is gone: ${gateRun.trim()}`);
  }
  if (gateRun.includes(SWEEP_FILTER)) problems.push('the gated engine-suite step in ci.yml runs the SWEEP tier; that belongs in sweep.yml');
  // Report-only is allowed on the Linux leg alone. The Windows leg bills, has a quiet machine, and is where the budget is enforced.
  // 1.3.0: lanes A and C cut after release. They were never cut, so the budget only reports on every leg for this release.
  // When they land, set RELEASE_REPORT_ONLY to false and the Linux-only switch is required again.
  if (RELEASE_REPORT_ONLY) {
    if (!gateRun.includes('--budget-report-only')) problems.push(`the gated step lacks --budget-report-only, so a suite that is over its budget would fail the release: ${gateRun.trim()}`);
  } else {
    const reportOnly = gateRun.replace(REPORT_ONLY_ON_LINUX, '');
    if (!gateRun.includes(REPORT_ONLY_ON_LINUX)) {
      problems.push(`the gated step lacks the Linux-only report-only switch ${REPORT_ONLY_ON_LINUX}, so the budget would be enforced on a runner whose load is not controlled`);
    }
    if (reportOnly.includes('--budget-report-only')) {
      problems.push(`--budget-report-only is passed on every leg, so the Windows leg would never fail on speed: ${gateRun.trim()}`);
    }
  }
  // The two tiers must be proved a partition by counts, on the real assembly.
  if (!ci.includes('do not partition the suite')) {
    problems.push('ci.yml has no step proving that Cost!=sweep and Cost=sweep partition the suite');
  }

  // And the step it replaced must not creep back: a bare whole-suite line beside the gated one means the gated
  // one can be deleted without anything going red. Shell continuations are joined first, because the commutation
  // step spells its --filter on the next line and it is a narrowed run, which the gate itself allows.
  const statements = ci.replace(/\\\r?\n\s*/g, ' ').split(/\r?\n/);
  // A --list-tests line runs no test (the partition proof counts with it), so it is not a whole-suite run.
  const bare = statements.filter((line) => line.includes('dotnet test Semanticus.Tests/Semanticus.Tests.csproj')
    && !line.includes('--filter') && !line.includes('--list-tests'));
  if (bare.length > 0) {
    problems.push('ci.yml still runs the WHOLE engine suite with a bare `dotnet test`; that path cannot see an aborted run. '
      + `Run it through node tools/suite-gate.mjs instead. Offending line(s): ${bare.join(' | ')}`);
  }

  // The gate has one way past its count check, --no-count-check, and it exists for a person at a terminal. A CI step
  // that passed it would print a pass for a run nobody counted, which is the exact hole the waiver's loud verdict
  // is there to keep visible. The flag may never appear on a line that runs the gate here.
  const waivers = statements.filter((line) => line.includes('suite-gate.mjs') && line.includes('--no-count-check'));
  if (waivers.length > 0) {
    problems.push('ci.yml runs tools/suite-gate.mjs with --no-count-check, so CI would pass a run whose count was never measured. '
      + `Offending line(s): ${waivers.join(' | ')}`);
  }
  return problems;
}

// The real workflow is wired.
const real = readFileSync(resolve(repo, '.github/workflows/ci.yml'), 'utf8');
assert.deepEqual(wiringProblems(real), [], 'ci.yml does not run the suite gate the way CI needs it to');

// And each way a step can stop running the gate while its text stays in the file is seen (round 3 of the base-fixes
// verification found these four passing). Each mutation is made to the real workflow, on both gated steps.
for (const command of [GATED, SELF_TEST]) {
  const at = real.indexOf(`run: ${command}`);
  assert.ok(at > 0, `the real workflow has the step "run: ${command}"`);
  const lineEnd = real.indexOf('\n', at);
  const runLine = real.slice(at, lineEnd === -1 ? undefined : lineEnd).replace(/\r$/, '');
  const indent = real.slice(real.lastIndexOf('\n', at) + 1, at);
  const mutations = [
    ['an inline comment carries the command', runLine.replace(`run: ${command}`, `run: echo skipped # ${command}`)],
    ['the step is switched off with if: false', `if: false\n${indent}${runLine}`],
    ['the step may fail without failing the job', `continue-on-error: true\n${indent}${runLine}`],
    ['the command sits in a folded scalar with the waiver on its next line',
      `run: >\n${indent}  ${runLine.slice('run: '.length)}\n${indent}  --no-count-check`],
  ];
  for (const [name, replacement] of mutations) {
    const mutated = real.replace(runLine, replacement);
    assert.notEqual(mutated, real, `${name}: the mutation changed nothing`);
    assert.ok(wiringProblems(mutated).length > 0, `${name} (${command}): the wiring test did not notice`);
  }
}

// The lean-suite wiring. Each mutation is made to the real ci.yml and must turn the wiring red.
{
  const gateAt = real.indexOf(`run: ${GATED}`);
  const gateLine = real.slice(gateAt, real.indexOf('\n', gateAt)).replace(/\r$/, '');
  const leanMutations = [
    ['the default filter is dropped (the sweep tier would run on every pull request)', gateLine.replace(` ${DEFAULT_FILTER}`, '')],
    ['the filter runs the sweep tier instead', gateLine.replace(DEFAULT_FILTER, SWEEP_FILTER)],
    ['the run budget is dropped', gateLine.replace(` ${BUDGET_FLAGS[0]}`, '')],
    ['the per-test limit is dropped', gateLine.replace(` ${BUDGET_FLAGS[1]}`, '')],
    ...(RELEASE_REPORT_ONLY
      ? [['report-only is gone (the budget would fail the release)', gateLine.replace('--budget-report-only', '')]]
      : [
        ['report-only is passed on every leg (Windows would never fail on speed)', gateLine.replace(REPORT_ONLY_ON_LINUX, '--budget-report-only')],
        ['report-only is gone from the Linux leg (the budget would be enforced where the load is not controlled)', gateLine.replace(REPORT_ONLY_ON_LINUX, '')],
      ]),
    ['the partition proof is removed', null],
  ];
  for (const [name, replacement] of leanMutations) {
    const mutated = replacement === null
      ? real.replaceAll('do not partition the suite', 'do not match')
      : real.replace(gateLine, replacement);
    assert.notEqual(mutated, real, `${name}: the mutation changed nothing`);
    assert.ok(wiringProblems(mutated).length > 0, `${name}: the wiring test did not notice`);
  }
}

// The nightly sweep workflow runs the other tier, through the same gate.
const sweepRaw = readFileSync(resolve(repo, '.github/workflows/sweep.yml'), 'utf8');
assert.deepEqual(sweepProblems(sweepRaw), [], 'sweep.yml does not run the sweep tier the way the lean suite needs it to');
{
  const at = sweepRaw.indexOf(`run: ${GATED}`);
  assert.ok(at > 0, 'sweep.yml has the gated step');
  const runLine = sweepRaw.slice(at, sweepRaw.indexOf('\n', at)).replace(/\r$/, '');
  const indent = sweepRaw.slice(sweepRaw.lastIndexOf('\n', at) + 1, at);
  const sweepMutations = [
    ['the step is switched off with if: false', sweepRaw.replace(runLine, `if: false\n${indent}${runLine}`)],
    ['the step may fail without failing the job', sweepRaw.replace(runLine, `continue-on-error: true\n${indent}${runLine}`)],
    ['an inline comment carries the command', sweepRaw.replace(runLine, runLine.replace(`run: ${GATED}`, `run: echo skipped # ${GATED}`))],
    ['the sweep filter is dropped', sweepRaw.replace(runLine, runLine.replace(` ${SWEEP_FILTER}`, ''))],
    ['the sweep runs the default tier', sweepRaw.replace(runLine, runLine.replace(SWEEP_FILTER, DEFAULT_FILTER))],
    ['the count check is waived', sweepRaw.replace(runLine, `${runLine} --no-count-check`)],
    ['the schedule is removed', sweepRaw.replace(/^ {2}schedule:/m, '  # schedule:')],
    ['the manual trigger is removed', sweepRaw.replace(/^ {2}workflow_dispatch:/m, '  # workflow_dispatch:')],
    ['the public mirror guard is removed', sweepRaw.replace('if: github.event.repository.private == true', 'if: true')],
  ];
  for (const [name, mutated] of sweepMutations) {
    assert.notEqual(mutated, sweepRaw, `${name}: the mutation changed nothing`);
    assert.ok(sweepProblems(mutated).length > 0, `sweep.yml: ${name}: the wiring test did not notice`);
  }
}

console.log('suite gate wiring test passed (lean suite: 7 ci.yml mutations, 9 sweep.yml mutations, on top of the original 4 checks and 8 mutations)');
