// Pure fixtures and an injected gate only. This file never starts dotnet.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { chmodSync, copyFileSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPlan, parseDiscovery, validatePlan, mergeResults, runShard, executeGate } from './suite-shard.mjs';

const names = ['N.FooTests.One', 'N.FooTests.Two(x: "a.b")', 'N.FooTestsMore.One', 'N.BarTests.One'];
const make = (options = {}) => createPlan(names, { shards: 2, ...options });
const output = () => mkdtempSync(join(tmpdir(), 'suite-shard-test-'));
const gateText = (count, failed = 0) => [
  `suite-gate: ${count} tests discovered in fixture (Release).`,
  'Test run for fixture.dll',
  'Starting test execution, please wait...',
  `${failed ? 'Failed!' : 'Passed!'} - Failed: ${failed}, Passed: ${count - failed}, Skipped: 0, Total: ${count}`,
  'suite-gate: used 2 of 1500 s (0%).',
  failed ? '  Failed N.FooTests.One [1 ms]' : '',
  failed ? 'SUITE GATE: FAIL' : `SUITE GATE: PASS (${count} executed of ${count} discovered, ${count} passed, 0 skipped, 0 failed). Log: fixture`,
].join('\n');
const execute = (count, failed = 0) => async () => ({ text: gateText(count, failed), status: failed ? 1 : 0, seconds: 2 });
async function completed(plan = make()) {
  const out = output();
  const results = [];
  for (const shard of plan.shards) {
    results.push(await runShard(plan, shard.id, out, { executeGate: execute(shard.testCount) }));
  }
  return { plan, out, results };
}

test('discovery reads indented tests and rejects a successful exit with an aborted list', () => {
  const list = 'The following Tests are available:\n    N.C.One\n    N.C.Two\n';
  assert.deepEqual(parseDiscovery(list), ['N.C.One', 'N.C.Two']);
  assert.throws(() => parseDiscovery(`${list}The active test discovery was aborted.`), /discovery/i);
  assert.throws(() => parseDiscovery('The following Tests are available:'), /no tests/i);
});

test('a saved plain list works; parameters do not become a class', () => {
  assert.deepEqual(parseDiscovery(names.join('\n')), names);
  const plan = make();
  assert.ok(plan.shards.some((s) => s.classes.includes('N.FooTests')));
  assert.equal(plan.totalTests, 4);
});

test('filters have a trailing dot and do not match a longer class prefix', () => {
  const plan = createPlan(['N.FooTests.One', 'N.FooTestsMore.One'], { shards: 2 });
  assert.equal(plan.shards[0].filter, 'FullyQualifiedName~N.FooTests.');
  assert.equal(plan.shards[1].filter, 'FullyQualifiedName~N.FooTestsMore.');
  assert.ok(!'N.FooTestsMore.One'.includes('N.FooTests.'));
});

test('all tests and all classes belong to exactly one shard, with deterministic count balancing', () => {
  const plan = make();
  assert.deepEqual(plan, make());
  assert.equal(validatePlan(plan).covered, names.length);
  assert.deepEqual(plan.shards.map((s) => s.testCount), [2, 2]);
  assert.equal(new Set(plan.shards.flatMap((s) => s.classes)).size, 3);
});

test('a filter substring that overlaps another namespace cannot silently double-run a test', () => {
  assert.throws(() => createPlan(['N.C.One', 'Other.N.C.Two'], { shards: 2 }), /cover|matches|overlap/i);
});

test('long filters split a shard at 30,000 characters without losing classes', () => {
  const tests = Array.from({ length: 500 }, (_, i) => `N.${'A'.repeat(80)}${i}.One`);
  const plan = createPlan(tests, { shards: 1 });
  assert.ok(plan.shards.length > 1);
  assert.ok(plan.shards.every((s) => s.filter.length <= 30000));
  assert.equal(validatePlan(plan).covered, 500);
});

test('an unsplittable class and invalid shard counts fail', () => {
  assert.throws(() => createPlan([`N.${'A'.repeat(30000)}.One`]), /30000|30,000|long/i);
  for (const shards of [0, -1, 1.5, NaN]) assert.throws(() => make({ shards }), /shards/i);
  assert.throws(() => createPlan([]), /no tests/i);
  assert.throws(() => createPlan(['not-a-test']), /class|name/i);
});

test('duplicate display names remain separate discovered rows', () => {
  const plan = createPlan(['N.C.Theory', 'N.C.Theory'], { shards: 8 });
  assert.equal(plan.totalTests, 2);
  assert.equal(plan.shards.length, 1);
  assert.equal(validatePlan(plan).covered, 2);
});

test('TRX class durations balance classes; definitions support short display names', () => {
  const tests = ['N.A.One', 'N.B.One', 'N.C.One', 'N.D.One'];
  const timings = '<TestRun><TestDefinitions><UnitTest id="a"><TestMethod className="N.A" name="One"/></UnitTest></TestDefinitions>'
    + '<Results><UnitTestResult testId="a" testName="One" duration="00:01:40"/>'
    + '<UnitTestResult testName="N.B.One" duration="00:01:30"/>'
    + '<UnitTestResult testName="N.C.One" duration="00:00:20"/>'
    + '<UnitTestResult testName="N.D.One" duration="00:00:10"/></Results></TestRun>';
  const plan = createPlan(tests, { shards: 2, timings });
  assert.deepEqual(plan.shards.map((s) => s.estimatedSeconds), [110, 110]);
  assert.throws(() => createPlan(tests, { timings: '<TestRun/>' }), /timings/i);
});

test('the cover check rejects edited counts, filters, classes and plan identity', () => {
  for (const mutate of [
    (p) => { p.totalTests += 1; },
    (p) => { p.shards[0].testCount -= 1; },
    (p) => { p.shards[0].filter = 'FullyQualifiedName~N.FooTests'; },
    (p) => { p.shards[1].classes.push(p.shards[0].classes[0]); },
    (p) => { p.planId = 'stale'; },
  ]) {
    const plan = make();
    mutate(plan);
    assert.throws(() => validatePlan(plan));
  }
});

test('run calls the count-checking gate with the required filter, budgets and timings', async () => {
  const plan = make();
  let calls = 0;
  const result = await runShard(plan, 0, output(), { executeGate: async ({ argv }) => {
    calls += 1;
    for (const flag of ['--filter', '--max-seconds', '--budget-report-only', '--slowest']) assert.ok(argv.includes(flag));
    assert.equal(argv[argv.indexOf('--filter') + 1], plan.shards[0].filter);
    assert.equal(argv[argv.indexOf('--max-seconds') + 1], '1500');
    assert.ok(!argv.includes('--no-count-check'));
    return execute(plan.shards[0].testCount)();
  } });
  assert.equal(calls, 1);
  assert.equal(result.status, 'done');
  assert.equal(result.ok, true);
});

test('a rerun skips a done shard without calling the gate again', async () => {
  const { plan, out, results } = await completed();
  const result = await runShard(plan, 0, out, { executeGate: async () => assert.fail('a done shard ran twice') });
  assert.deepEqual(result, results[0]);
});

test('a partial shard resumes, while a completed result from another plan is refused', async () => {
  const plan = make();
  const out = output();
  writeFileSync(join(out, 'shard-0.result'), JSON.stringify({ status: 'running' }));
  assert.equal((await runShard(plan, 0, out, { executeGate: execute(2) })).ok, true);
  const stale = JSON.parse(readFileSync(join(out, 'shard-0.result'), 'utf8'));
  stale.planId = 'old';
  writeFileSync(join(out, 'shard-0.result'), JSON.stringify(stale));
  await assert.rejects(runShard(plan, 0, out, { executeGate: async () => assert.fail('stale result was overwritten') }), /plan/i);
});

test('a complete passing cover merges and totals all measured counts', async () => {
  const { plan, results } = await completed();
  const verdict = mergeResults(plan, results);
  assert.equal(verdict.ok, true);
  assert.equal(verdict.discovered, 4);
  assert.equal(verdict.executed, 4);
  assert.equal(verdict.planned, 4);
});

test('runtime theory rows above the planned count pass run, resume and merge', async () => {
  const plan = make();
  const out = output();
  const results = [];
  for (const shard of plan.shards) {
    const discovered = shard.testCount;
    const executed = discovered + 3;
    const text = [
      `suite-gate: ${discovered} tests discovered in fixture (Release).`,
      'Test run for fixture.dll',
      'Starting test execution, please wait...',
      `Passed! - Failed: 0, Passed: ${executed - 1}, Skipped: 1, Total: ${executed}`,
      `SUITE GATE: PASS (${executed} executed of ${discovered} discovered, ${executed - 1} passed, 1 skipped, 0 failed). Log: fixture`,
    ].join('\n');
    const result = await runShard(plan, shard.id, out, { executeGate: async () => ({ text, status: 0, seconds: 2 }) });
    assert.equal(result.ok, true, result.problems.join('; '));
    assert.deepEqual(await runShard(plan, shard.id, out, { executeGate: async () => assert.fail('completed theory shard ran twice') }), result);
    results.push(result);
  }
  const merged = mergeResults(plan, results);
  assert.equal(merged.ok, true, merged.problems.join('; '));
  assert.equal(merged.executed, plan.totalTests + 6);
  const short = { ...results[0], executed: 1, passed: 1, skipped: 0,
    gateVerdict: `SUITE GATE: PASS (1 executed of ${plan.shards[0].testCount} discovered, 1 passed, 0 skipped, 0 failed). Log: fixture` };
  assert.equal(mergeResults(plan, [short, results[1]]).ok, false, 'a matching PASS cannot excuse a short count');
});

test('a committed cloud plan can run and resume planning, while other checkout differences fail', async () => {
  const folder = output();
  const repo = join(folder, 'repo');
  const out = join(folder, 'results');
  mkdirSync(repo);
  const git = (...argv) => {
    const result = spawnSync('git', argv, { cwd: repo, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git('init', '-q');
  git('config', 'user.name', 'Contoso');
  git('config', 'user.email', 'contoso@example.com');
  writeFileSync(join(repo, 'product.cs'), 'original');
  git('add', '.');
  git('commit', '-qm', 'fixture product');
  const plan = make({ revision: git('rev-parse', 'HEAD') });
  mkdirSync(out);
  writeFileSync(join(out, 'shards.json'), JSON.stringify(plan));
  for (const shard of plan.shards) await runShard(plan, shard.id, out, { executeGate: execute(shard.testCount) });
  mkdirSync(join(repo, 'cloud-plans'));
  writeFileSync(join(repo, 'cloud-plans', 'fixture.json'), JSON.stringify(plan));
  git('add', '.');
  git('commit', '-qm', 'fixture cloud plan');
  const script = fileURLToPath(new URL('./suite-shard.mjs', import.meta.url));
  const cli = (...argv) => spawnSync(process.execPath, [script, ...argv, '--out', out], { cwd: repo, encoding: 'utf8' });
  assert.equal(cli('plan').status, 0, 'the plan-only commit must remain usable');
  assert.equal(cli('run', '--shard', '0').status, 0, 'the committed plan must run against the same product');
  writeFileSync(join(repo, 'cloud-plans', 'fixture.json'), '{}');
  assert.equal(cli('run', '--shard', '0').status, 0, 'changes under cloud-plans remain allowed');
  writeFileSync(join(repo, 'product.cs'), 'changed');
  assert.equal(cli('run', '--shard', '0').status, 1, 'an unstaged product change must fail');
  git('add', 'product.cs');
  assert.equal(cli('plan').status, 1, 'a staged product change must fail');
  git('commit', '-qm', 'fixture changed product');
  assert.equal(cli('run', '--shard', '0').status, 1, 'a committed product change must fail');
  git('checkout', plan.revision, '--', 'product.cs');
  git('commit', '-qm', 'fixture restored product');
  writeFileSync(join(repo, 'new-product.cs'), 'new');
  assert.equal(cli('run', '--shard', '0').status, 1, 'an untracked product file must fail');
});

test('missing, partial, malformed and duplicate results cannot pass', async () => {
  const { plan, results } = await completed();
  for (const bad of [[], [results[0]], [results[0], { status: 'running', shard: 1 }],
    [results[0], { status: 'done', shard: 1 }], [results[0], results[0], results[1]]]) {
    assert.equal(mergeResults(plan, bad).ok, false);
  }
});

test('gate failures retain every failed test name and fail the merge', async () => {
  const plan = make();
  const result = await runShard(plan, 0, output(), { executeGate: execute(2, 1) });
  assert.equal(result.ok, false);
  assert.deepEqual(result.failedTests, ['N.FooTests.One']);
  assert.ok(mergeResults(plan, [result]).failedTests.includes('N.FooTests.One'));
});

test('a clean-looking short run, discovery drift, timeout, missing verdict or crash cannot pass', async () => {
  const plan = make();
  for (const fake of [
    { text: gateText(1), status: 0, seconds: 2 },
    { text: gateText(3), status: 0, seconds: 2 },
    { text: gateText(2), status: 0, seconds: 1500 },
    { text: gateText(2).replace(/SUITE GATE: PASS[^\n]*/, ''), status: 0, seconds: 2 },
    { text: gateText(2), status: null, seconds: 2 },
  ]) {
    const result = await runShard(plan, 0, output(), { executeGate: async () => fake });
    assert.equal(result.ok, false);
    assert.equal(mergeResults(plan, [result]).ok, false);
  }
});

test('a result cannot use another filter or stale plan to pass merge', async () => {
  const { plan, results } = await completed();
  for (const change of [{ planId: 'old' }, { filter: '' }, { executed: 0 }, { seconds: null }, { timedOut: true }, { gateVerdict: 'SUITE GATE: PASS WITHOUT THE COUNT CHECK' }]) {
    assert.equal(mergeResults(plan, [{ ...results[0], ...change }, results[1]]).ok, false);
  }
});

test('a forged done marker cannot print a resumed PASS without measured gate counts', async () => {
  const plan = make();
  const out = output();
  writeFileSync(join(out, 'shard-0.result'), JSON.stringify({
    status: 'done', ok: true, shard: 0, planId: plan.planId, filter: plan.shards[0].filter,
  }));
  await assert.rejects(runShard(plan, 0, out, { executeGate: async () => assert.fail('invalid done result must be reported') }), /incomplete|invalid|count/i);
});

test('the CLI plans saved discovery, merges completed runs and rejects missing or truncated results', async () => {
  const out = output();
  // Keep revision checks independent of the developer's tracked and untracked changes.
  const repo = join(out, 'repo');
  mkdirSync(repo);
  for (const argv of [['init', '-q'], ['config', 'user.name', 'Contoso'], ['config', 'user.email', 'contoso@example.com'],
    ['-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-qm', 'fixture']]) {
    const r = spawnSync('git', argv, { cwd: repo, encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr);
  }
  const list = join(out, 'list.txt');
  writeFileSync(list, names.join('\n'));
  const script = fileURLToPath(new URL('./suite-shard.mjs', import.meta.url));
  const cli = (...argv) => spawnSync(process.execPath, [script, ...argv, '--out', out], { cwd: repo, encoding: 'utf8' });
  const planned = cli('plan', '--list', list, '--shards', '2');
  assert.equal(planned.status, 0, planned.stderr);
  assert.match(planned.stdout, /PLAN COVER: PASS \(4 of 4/);
  assert.match(planned.stdout, /shard 0:.*2 tests/);
  assert.equal(cli('plan', '--list', list).status, 0, 'the existing immutable plan remains usable');
  assert.equal(cli('plan', '--shards', '3').status, 1, 'an existing plan cannot silently ignore an explicit shard count');
  const missing = cli('merge');
  assert.equal(missing.status, 1);
  assert.match(missing.stdout, /SHARDED SUITE: FAIL/);
  assert.match(missing.stdout, /missing or partial shard 0/);
  writeFileSync(join(out, 'shard-0.result'), '{"status":');
  assert.equal(cli('merge').status, 1);
  const plan = JSON.parse(readFileSync(join(out, 'shards.json'), 'utf8'));
  for (const shard of plan.shards) await runShard(plan, shard.id, out, { executeGate: execute(shard.testCount) });
  const passed = cli('merge');
  assert.equal(passed.status, 0, passed.stderr);
  assert.match(passed.stdout, /SHARDED SUITE: PASS\ndiscovered 4; executed 4; planned 4/);
});

test('the default output folder does not invalidate its own plan revision', async () => {
  const folder = output();
  const repo = join(folder, 'repo');
  mkdirSync(repo);
  copyFileSync(new URL('../.gitignore', import.meta.url), join(repo, '.gitignore'));
  for (const argv of [['init', '-q'], ['config', 'user.name', 'Contoso'], ['config', 'user.email', 'contoso@example.com'],
    ['add', '.gitignore'], ['-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture']]) {
    const r = spawnSync('git', argv, { cwd: repo, encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr);
  }
  const list = join(folder, 'list.txt');
  writeFileSync(list, names.join('\n'));
  const script = fileURLToPath(new URL('./suite-shard.mjs', import.meta.url));
  const cli = (...argv) => spawnSync(process.execPath, [script, ...argv], { cwd: repo, encoding: 'utf8' });
  assert.equal(cli('plan', '--list', list, '--shards', '2').status, 0);
  const out = join(repo, 'suite-results');
  const plan = JSON.parse(readFileSync(join(out, 'shards.json'), 'utf8'));
  for (const shard of plan.shards) await runShard(plan, shard.id, out, { executeGate: execute(shard.testCount) });
  assert.equal(cli('plan').status, 0, 'the generated default output must not block plan reuse');
  assert.equal(cli('run', '--shard', '0').status, 0, 'the generated default output must not block a completed run');
});

test('the process runner captures both streams and stops an unfinished gate', async () => {
  const out = output();
  const fixture = join(out, 'fixture.mjs');
  writeFileSync(fixture, 'process.stdout.write("fixture stdout\\n"); process.stderr.write("fixture stderr\\n");');
  const logPath = join(out, 'gate.log');
  const clean = await executeGate({ argv: [], env: process.env, logPath }, { gatePath: fixture });
  assert.equal(clean.status, 0);
  assert.match(readFileSync(logPath, 'utf8'), /fixture stdout/);
  assert.match(readFileSync(logPath, 'utf8'), /fixture stderr/);
  writeFileSync(fixture, 'setInterval(() => {}, 1000);');
  const stopped = await executeGate({ argv: [], env: process.env, logPath }, { gatePath: fixture, timeoutMilliseconds: 150 });
  assert.equal(stopped.status, null);
  assert.equal(stopped.timedOut, true);
  assert.ok(stopped.seconds < 5);
});

test('cloud orchestration supports all shards, separate sessions and restart without a real SDK', () => {
  if (process.platform === 'win32') return;
  const folder = output();
  const repo = join(folder, 'repo');
  const bin = join(folder, 'bin');
  const out = join(folder, 'results');
  mkdirSync(join(repo, 'tools'), { recursive: true });
  mkdirSync(bin);
  copyFileSync(new URL('./cloud-suite.sh', import.meta.url), join(repo, 'tools/cloud-suite.sh'));
  // The lock shim intercepts every build and suite command. No real dotnet command can start.
  const shim = `#!${process.execPath}
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
const out = process.env.SHARD_FIXTURE_OUT;
const name = process.argv[1].split('/').pop();
const args = process.argv.slice(2);
if (process.env.JAVA_TOOL_OPTIONS) throw Error('JAVA_TOOL_OPTIONS was not unset');
appendFileSync(join(out, 'calls.jsonl'), JSON.stringify({ name, args, processors: process.env.DOTNET_PROCESSOR_COUNT }) + '\\n');
if (name === 'git') process.exit(0);
if (name === 'semlock') {
  if (args.slice(3)[0] === 'dotnet') {
    if (!args.includes('-m:2') || !args.includes('Release')) throw Error('build flags missing');
    process.exit(0);
  }
  const command = args[5];
  if (command === 'plan') writeFileSync(join(out, 'shards.json'), JSON.stringify({ shards: [{ id: 0 }, { id: 1 }] }));
  else if (command === 'run') {
    const id = args[args.indexOf('--shard') + 1];
    const path = join(out, 'shard-' + id + '.result');
    if (!existsSync(path)) { writeFileSync(path, 'done'); appendFileSync(join(out, 'executed'), id + '\\n'); }
  } else throw Error('unexpected locked command ' + args.join(' '));
  process.exit(0);
}
if (args[0] === '--input-type=module') {
  const { spawnSync } = await import('node:child_process');
  process.exit(spawnSync(process.execPath, args, { stdio: 'inherit' }).status);
}
if (args[1] === 'merge') {
  const complete = [0, 1].every((id) => existsSync(join(out, 'shard-' + id + '.result')));
  console.log('SHARDED SUITE: ' + (complete ? 'PASS' : 'FAIL'));
  process.exit(complete ? 0 : 1);
}
throw Error('unexpected node command ' + args.join(' '));
`;
  for (const name of ['git', 'node', 'semlock']) { const path = join(bin, name); writeFileSync(path, shim); chmodSync(path, 0o755); }
  mkdirSync(out);
  const env = { ...process.env, PATH: `${bin}:${process.env.PATH}`, SHARD_FIXTURE_OUT: out, JAVA_TOOL_OPTIONS: 'broken-options' };
  const cloud = (selection, results = out) => spawnSync('bash', [join(repo, 'tools/cloud-suite.sh'), 'fixture-branch', selection, results], {
    env: { ...env, SHARD_FIXTURE_OUT: results }, encoding: 'utf8',
  });
  const first = cloud('0');
  assert.equal(first.status, 1, first.stderr);
  assert.match(first.stdout, /SHARDED SUITE: FAIL/);
  const second = cloud('1');
  assert.equal(second.status, 0, second.stderr);
  assert.match(second.stdout, /SHARDED SUITE: PASS/);
  assert.equal(cloud('all').status, 0);
  assert.equal(readFileSync(join(out, 'executed'), 'utf8'), '0\n1\n');
  const calls = readFileSync(join(out, 'calls.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
  assert.equal(calls.filter((c) => c.name === 'semlock' && c.args.includes('plan')).length, 1);
  assert.ok(calls.filter((c) => c.name === 'semlock').every((c) => c.processors === '2'));
  assert.equal(cloud('0,0').status, 2);
  const all = join(folder, 'all');
  const together = cloud('all', all);
  assert.equal(together.status, 0, together.stderr);
  assert.equal(readFileSync(join(all, 'executed'), 'utf8'), '0\n1\n');
});
