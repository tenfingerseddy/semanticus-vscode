// F-003 / [T197] and F-024 / [T209]. The CI gate battery prepends a gh shim directory onto
// the child PATH. A hardcoded colon join is Unix-only: Windows PATH is semicolon-separated,
// so from PowerShell bash loses cat, wc and the shim while the same file still passes from
// Git Bash. path.delimiter is the portable join.
//
// The battery itself is the only executable proof of the cost classifier and the required
// gate verdict. A file nothing invokes is documentation (F-024), so this file also asserts
// the wiring. The cases live in the battery so this file does not duplicate them.
//
// WHY THE JOB IS TAKEN BY NAME, NOT BY SEARCHING FOR THE COMMAND. Finding "the job that
// contains `node tools/ci-gate/gate-battery.mjs`" is circular: moving that command into
// another job, or leaving `ci-gate-battery` as a no-op, still finds a job that looks
// right, while `gate` keeps judging the named job that no longer runs anything. Isolate
// the `ci-gate-battery` and `gate` mappings, then bind command, condition, needs and
// result to those mappings.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../tools/ci-gate/gate-battery.mjs', import.meta.url), 'utf8');
const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8')
  .replace(/\r\n/gu, '\n');

const BATTERY_RUN = /^ {8}run: node tools\/ci-gate\/gate-battery\.mjs[ \t]*$/m;
const BATTERY_IF = /^    if: needs\.decide\.outputs\.draft != 'true'[ \t]*$/m;
const GATE_NEEDS = /^    needs: \[[^\n]*\bci-gate-battery\b[^\n]*\][ \t]*$/m;
const R_CIGATE_BIND = /^          R_CIGATE: \$\{\{ needs\.ci-gate-battery\.result \}\}[ \t]*$/m;
const R_CIGATE_FAIL = /if \[ "\$R_CIGATE" != "success" \]/;

const COLON_JOIN = /PATH:\s*`\$\{[^`]+\}:\$\{process\.env\.PATH\}`/;
const DELIMITER_JOIN = /PATH:\s*`\$\{[^`]+\}?\$\{delimiter\}\$\{process\.env\.PATH\}`/;

function assertDecidePathUsesDelimiter(src) {
  assert.match(
    src,
    /import\s*\{[^}]*\bdelimiter\b[^}]*\}\s*from\s*'node:path'/,
    'gate-battery must import path.delimiter so the child PATH join is portable',
  );
  assert.match(
    src,
    DELIMITER_JOIN,
    'the decide child PATH must join the shim directory with path.delimiter, not a colon',
  );
  assert.doesNotMatch(
    src,
    COLON_JOIN,
    'the decide child PATH must not hard-code a colon join',
  );
}

test('the decide child PATH joins the shim directory with path.delimiter', () => {
  assertDecidePathUsesDelimiter(source);
});

test('a colon join is rejected', () => {
  const sabotaged = source.replace(DELIMITER_JOIN, (match) => match.replace('${delimiter}', ':'));
  assert.notEqual(sabotaged, source, 'the PATH-join positive control must rewrite the real join');
  assert.throws(
    () => assertDecidePathUsesDelimiter(sabotaged),
    /path\.delimiter|colon join/u,
    'the guard must reject a colon join',
  );
});

// Isolate one top-level job mapping. Same shape as lockfile-drift and the coverage-oracle
// DAX job: the name is the identity, so a command that moved to a neighbour cannot satisfy
// checks aimed at this job.
function yamlJob(text, name) {
  const normalized = text.replace(/\r\n/gu, '\n');
  const escapedName = name.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const header = new RegExp(`^ {2}${escapedName}:[ \\t]*$`, 'mu').exec(normalized);
  assert.notEqual(header, null, `ci.yml has no job named "${name}"`);
  const rest = normalized.slice(header.index + header[0].length);
  const next = rest.search(/^ {2}[A-Za-z_][A-Za-z0-9_-]*:[ \t]*$/mu);
  return next === -1 ? rest : rest.slice(0, next);
}

function assertCiGateBatteryWired(text) {
  const batteryJob = yamlJob(text, 'ci-gate-battery');
  const gateJob = yamlJob(text, 'gate');

  assert.match(
    batteryJob,
    BATTERY_RUN,
    'the ci-gate-battery job must run node tools/ci-gate/gate-battery.mjs by name (F-024)',
  );
  assert.match(
    batteryJob,
    BATTERY_IF,
    'ci-gate-battery must run on every non-draft ref, including a documents-only one',
  );
  assert.doesNotMatch(
    batteryJob,
    /continue-on-error:\s*true/,
    'a continue-on-error on the ci-gate-battery job would hide a child failure',
  );
  assert.doesNotMatch(
    batteryJob,
    /\|\|[ \t]*true|\|\|[ \t]*:/,
    'wrapping the battery invocation so a child failure is swallowed is the same as not running it',
  );

  assert.match(
    gateJob,
    GATE_NEEDS,
    'gate must list ci-gate-battery in its own needs, or a red battery cannot block a merge',
  );
  assert.match(
    gateJob,
    R_CIGATE_BIND,
    'gate must bind R_CIGATE to needs.ci-gate-battery.result, or the result is never judged',
  );
  assert.match(
    gateJob,
    R_CIGATE_FAIL,
    'gate must fail when R_CIGATE is not success, or the bound result is a no-op',
  );
}

test('ci-gate-battery and gate mappings bind the battery invocation', () => {
  assertCiGateBatteryWired(workflow);
});

test('moving the watched command out of ci-gate-battery is rejected', () => {
  const stripped = workflow.replace(BATTERY_RUN, '        run: echo "ci-gate-battery left as a no-op"');
  assert.notEqual(stripped, workflow, 'the move mutation must rewrite the watched command');
  const moved = stripped.replace(
    /^[ \t]+run: node tools\/findings-codex-coverage\.mjs[ \t]*$/m,
    '        run: node tools/ci-gate/gate-battery.mjs',
  );
  assert.notEqual(moved, stripped, 'the move mutation must plant the command in another job');
  assert.match(
    moved,
    BATTERY_RUN,
    'the move mutation must keep the command in the file, or it is just a deletion',
  );
  assert.match(
    yamlJob(moved, 'finding-queue'),
    BATTERY_RUN,
    'the planted command must land in finding-queue so a command-search would still pass',
  );
  assert.doesNotMatch(
    yamlJob(moved, 'ci-gate-battery'),
    BATTERY_RUN,
    'ci-gate-battery must no longer carry the watched command after the move',
  );
  assert.throws(
    () => assertCiGateBatteryWired(moved),
    /ci-gate-battery job must run node tools\/ci-gate\/gate-battery\.mjs/u,
    'a command that left the named job must not satisfy the wiring',
  );
});

test('leaving ci-gate-battery as a no-op is rejected', () => {
  const noop = workflow.replace(BATTERY_RUN, '        run: echo "ci-gate-battery no-op"');
  assert.notEqual(noop, workflow, 'the no-op mutation must rewrite the watched command');
  assert.doesNotMatch(noop, BATTERY_RUN, 'the no-op mutation must remove the watched command');
  assert.throws(
    () => assertCiGateBatteryWired(noop),
    /ci-gate-battery job must run node tools\/ci-gate\/gate-battery\.mjs/u,
    'a no-op ci-gate-battery job must not satisfy the wiring',
  );
});

test('an inert heredoc cannot pose as the battery step', () => {
  const inert = workflow.replace(
    BATTERY_RUN,
    [
      '        run: |',
      "          : <<'NOT_RUN'",
      '          run: node tools/ci-gate/gate-battery.mjs',
      '          NOT_RUN',
    ].join('\n'),
  );
  assert.notEqual(inert, workflow, 'the heredoc mutation must rewrite the real battery step');
  assert.match(
    yamlJob(inert, 'ci-gate-battery'),
    /run: node tools\/ci-gate\/gate-battery\.mjs/u,
    'the inert heredoc must keep the watched text inside the named job',
  );
  assert.throws(
    () => assertCiGateBatteryWired(inert),
    /ci-gate-battery job must run node tools\/ci-gate\/gate-battery\.mjs/u,
    'text inside an inert heredoc is not an executable battery step',
  );
});

test('an underscored neighbour cannot satisfy ci-gate-battery', () => {
  const stripped = workflow.replace(BATTERY_RUN, '        run: echo "ci-gate-battery left as a no-op"');
  assert.notEqual(stripped, workflow, 'the neighbour mutation must rewrite the real battery step');
  const renamed = stripped.replace('\n  coverage-oracle:\n', '\n  coverage_oracle: \t\n');
  assert.notEqual(renamed, stripped, 'the neighbour mutation must create a valid underscored job id');
  const moved = renamed.replace(
    '        run: ./tools/coverage-oracle.ps1 -Check',
    '        run: node tools/ci-gate/gate-battery.mjs',
  );
  assert.notEqual(moved, renamed, 'the neighbour mutation must move the command into coverage_oracle');
  assert.match(
    yamlJob(moved, 'coverage_oracle'),
    BATTERY_RUN,
    'the watched command must exist in the immediate underscored neighbour',
  );
  assert.doesNotMatch(
    yamlJob(moved, 'ci-gate-battery'),
    BATTERY_RUN,
    'the named battery job must remain a no-op',
  );
  assert.throws(
    () => assertCiGateBatteryWired(moved),
    /ci-gate-battery job must run node tools\/ci-gate\/gate-battery\.mjs/u,
    'a command in an underscored neighbour must not satisfy the named battery job',
  );
});
