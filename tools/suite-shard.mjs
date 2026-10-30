#!/usr/bin/env node
// Class shards for the full engine suite. Every real run uses suite-gate.mjs unchanged.
// Plan proves the discovery cover; run retains the gate's independent filtered count check;
// merge additionally requires the planned discovery count, no short run and a duration below 25 minutes.
// No .NET test runs during plan or merge. See docs/PLAN.md, "Sharded engine suite".

import { spawn, spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, linkSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { ABORT_PATTERNS, DISCOVERY_ERROR_PATTERNS, parseDuration, readRun } from './suite-gate.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const PROJECT = 'Semanticus.Tests/Semanticus.Tests.csproj';
const FILTER_LIMIT = 30000;
const MAX_SECONDS = 1500;
const filterFor = (classes) => classes.map((name) => `FullyQualifiedName~${name}.`).join('|');
const hash = (value) => createHash('sha256').update(JSON.stringify(value)).digest('hex');
const planHash = ({ planId, ...body }) => hash(body);
const methodOf = (name) => name.split('(')[0];
const positiveInt = (n) => Number.isSafeInteger(n) && n > 0;

function classOf(name) {
  const method = methodOf(name);
  const dot = method.lastIndexOf('.');
  const className = method.slice(0, dot);
  if (dot < 1 || !/^[\p{L}_][\p{L}\p{N}_+`]*(?:\.[\p{L}_][\p{L}\p{N}_+`]*)+$/u.test(className)) {
    throw new Error(`Cannot identify a fully qualified test class: ${JSON.stringify(name)}`);
  }
  return className;
}

/** Read either complete English vstest discovery output or one fully qualified display name per line. */
export function parseDiscovery(text) {
  const lines = text.split(/\r?\n/);
  const error = lines.filter((line) => !/^\s{4}\S/.test(line))
    .find((line) => [...ABORT_PATTERNS, ...DISCOVERY_ERROR_PATTERNS].some((p) => p.test(line)));
  if (error) throw new Error(`Incomplete discovery: ${error.trim()}`);
  const indented = lines.filter((line) => /^\s{4}\S/.test(line)).map((line) => line.trim());
  const tests = indented.length ? indented : lines.map((line) => line.trim()).filter(Boolean);
  if (!tests.length || (tests.length === 1 && tests[0].includes('The following Tests'))) {
    throw new Error('Discovery listed no tests');
  }
  tests.forEach(classOf);
  return tests;
}

const unescapeXml = (s) => s.replace(/&(?:lt|gt|quot|apos|amp);/g,
  (m) => ({ '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'", '&amp;': '&' })[m]);
function attributes(tag) {
  return Object.fromEntries([...tag.matchAll(/([\w:]+)="([^"]*)"/g)].map((m) => [m[1], unescapeXml(m[2])]));
}
function trxResults(text) {
  return [...text.matchAll(/<UnitTestResult\b((?:[^>"]|"[^"]*")*)\/?\s*>/g)].map((m) => attributes(m[1]));
}
function classTimings(text, classes) {
  const definitions = new Map();
  for (const m of text.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g)) {
    const method = /<TestMethod\b([^>]*)\/?\s*>/.exec(m[2]);
    if (method) definitions.set(attributes(m[1]).id, attributes(method[1]).className);
  }
  const seconds = new Map();
  for (const row of trxResults(text)) {
    let name = definitions.get(row.testId);
    if (!name) { try { name = classOf(row.testName ?? ''); } catch { continue; } }
    const duration = parseDuration(row.duration);
    if (classes.has(name) && duration !== null) seconds.set(name, (seconds.get(name) ?? 0) + duration);
  }
  if (!seconds.size) throw new Error('The timings TRX has no durations for the discovered classes');
  return seconds;
}

export function createPlan(tests, options = {}) {
  const shards = options.shards ?? 8;
  if (!positiveInt(shards)) throw new Error('--shards must be a positive whole number');
  if (!Array.isArray(tests) || !tests.length) throw new Error('Discovery listed no tests');
  const counts = new Map();
  for (const test of tests) { const name = classOf(test); counts.set(name, (counts.get(name) ?? 0) + 1); }
  const timings = options.timings === undefined ? null : classTimings(options.timings, counts);
  const timedCount = timings ? [...timings.keys()].reduce((n, name) => n + counts.get(name), 0) : 0;
  const average = timings ? [...timings.values()].reduce((a, b) => a + b, 0) / timedCount : 1;
  const classes = [...counts].map(([name, count]) => ({ name, count, weight: timings?.get(name) ?? count * average }));
  classes.sort((a, b) => b.weight - a.weight || b.count - a.count || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const buckets = Array.from({ length: Math.min(shards, classes.length) }, () => ({ classes: [], weight: 0, count: 0 }));
  for (const item of classes) {
    const bucket = buckets.reduce((a, b) => b.weight < a.weight || (b.weight === a.weight && b.count < a.count) ? b : a);
    bucket.classes.push(item.name); bucket.weight += item.weight; bucket.count += item.count;
  }
  const planned = [];
  for (const bucket of buckets) {
    let group = [];
    for (const name of bucket.classes.sort()) {
      if (filterFor([name]).length > FILTER_LIMIT) throw new Error(`One class exceeds the 30000 character filter limit: ${name}`);
      if (filterFor([...group, name]).length > FILTER_LIMIT) { planned.push(group); group = []; }
      group.push(name);
    }
    if (group.length) planned.push(group);
  }
  const plan = {
    schemaVersion: 1, project: options.project ?? PROJECT, configuration: 'Release', revision: options.revision ?? null,
    requestedShards: shards, balance: timings ? 'seconds' : 'test count', totalTests: tests.length, tests: [...tests].sort(),
    shards: planned.map((group, id) => ({
      id, classes: group, testCount: group.reduce((n, name) => n + counts.get(name), 0), filter: filterFor(group),
      estimatedSeconds: timings ? group.reduce((n, name) => n + (timings.get(name) ?? counts.get(name) * average), 0) : null,
    })),
  };
  plan.planId = planHash(plan);
  validatePlan(plan);
  return plan;
}

/** Check actual case-insensitive FQN substring semantics, including overlaps between namespaces. */
export function validatePlan(plan) {
  if (plan.schemaVersion !== 1 || !positiveInt(plan.totalTests) || plan.configuration !== 'Release'
      || !Array.isArray(plan.tests) || plan.tests.length !== plan.totalTests || !plan.shards?.length
      || plan.planId !== planHash(plan)) throw new Error('Invalid or edited shard plan identity/count');
  const discovered = new Map();
  for (const test of plan.tests) { const name = classOf(test); discovered.set(name, (discovered.get(name) ?? 0) + 1); }
  const owners = new Map();
  for (const [id, shard] of plan.shards.entries()) {
    if (shard.id !== id || !shard.classes?.length || !positiveInt(shard.testCount)
        || shard.filter !== filterFor(shard.classes) || shard.filter.length > FILTER_LIMIT) throw new Error(`Invalid shard ${id}`);
    let count = 0;
    for (const name of shard.classes) {
      if (!discovered.has(name) || owners.has(name)) throw new Error(`Class cover is not exactly once: ${name}`);
      owners.set(name, id); count += discovered.get(name);
    }
    if (count !== shard.testCount) throw new Error(`Shard ${id} count differs from discovery`);
  }
  if (owners.size !== discovered.size) throw new Error('Class cover omits discovered tests');
  const needles = plan.shards.map((s) => s.classes.map((name) => `${name.toLowerCase()}.`));
  for (const test of plan.tests) {
    const fqn = methodOf(test).toLowerCase();
    const matches = needles.flatMap((group, id) => group.some((needle) => fqn.includes(needle)) ? [id] : []);
    if (matches.length !== 1 || matches[0] !== owners.get(classOf(test))) {
      throw new Error(`Filter cover overlap: ${test} matches shards ${matches.join(', ')}; expected exactly one`);
    }
  }
  return { discovered: plan.totalTests, covered: plan.totalTests, uncovered: 0, duplicated: 0, classes: owners.size };
}

function resultProblems(plan, shard, result) {
  if (!result || result.status !== 'done') return [`missing or partial shard ${shard.id}`];
  const problems = [];
  if (result.planId !== plan.planId || result.shard !== shard.id || result.filter !== shard.filter) problems.push(`shard ${shard.id} belongs to another plan or filter`);
  if (result.ok !== true || result.gateExitCode !== 0 || result.timedOut === true) problems.push(`shard ${shard.id} failed its gate or runner`);
  const verdict = /^SUITE GATE: PASS \((\d+) executed of (\d+) discovered, (\d+) passed, (\d+) skipped, (\d+) failed\)\./.exec(result.gateVerdict ?? '');
  if (!verdict || Number(verdict[1]) !== result.executed || Number(verdict[2]) !== result.discovered
      || Number(verdict[3]) !== result.passed || Number(verdict[4]) !== result.skipped || Number(verdict[5]) !== result.failed) {
    problems.push(`shard ${shard.id} has no matching count-checked gate PASS`);
  }
  // Some theories discover one case and expand into several rows at runtime, as suite-gate allows.
  if (result.discovered !== shard.testCount || !Number.isSafeInteger(result.executed) || result.executed < shard.testCount) problems.push(`shard ${shard.id} counts differ from its plan (${shard.testCount})`);
  if (!Number.isSafeInteger(result.passed) || !Number.isSafeInteger(result.skipped) || result.passed < 0 || result.skipped < 0
      || result.failed !== 0 || result.passed + result.skipped !== result.executed || !Array.isArray(result.failedTests) || result.failedTests.length) {
    problems.push(`shard ${shard.id} has failed or inconsistent test counts`);
  }
  if (!Number.isFinite(result.seconds) || result.seconds < 0 || result.seconds >= MAX_SECONDS) problems.push(`shard ${shard.id} duration is unknown or at least 1500 seconds`);
  return problems;
}

export function mergeResults(plan, results) {
  const problems = [];
  try { validatePlan(plan); } catch (error) { problems.push(error.message); }
  const byId = new Map();
  const failedTests = new Set();
  for (const result of results) {
    if (!result || !Number.isSafeInteger(result.shard) || !plan.shards.some((s) => s.id === result.shard)) {
      problems.push('unknown or malformed shard result'); continue;
    }
    if (byId.has(result.shard)) problems.push(`duplicate shard ${result.shard}`);
    byId.set(result.shard, result);
    for (const name of result.failedTests ?? []) failedTests.add(name);
  }
  for (const shard of plan.shards) problems.push(...resultProblems(plan, shard, byId.get(shard.id)));
  const sum = (field) => [...byId.values()].reduce((n, r) => n + (Number.isSafeInteger(r[field]) && r[field] >= 0 ? r[field] : 0), 0);
  return { ok: problems.length === 0, planned: plan.totalTests, discovered: sum('discovered'), executed: sum('executed'),
    failedTests: [...failedTests].sort(), problems };
}

function atomicJson(path, value) {
  const temp = `${path}.${process.pid}.partial`;
  writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`);
  renameSync(temp, path);
}

/** Stop the whole child process group on timeout or interruption, including the gate's dotnet children. */
export async function executeGate({ argv, env, logPath }, io = {}) {
  const started = performance.now();
  writeFileSync(logPath, '');
  const child = spawn(process.execPath, [io.gatePath ?? join(HERE, 'suite-gate.mjs'), ...argv], { env, detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'] });
  let text = '';
  let timedOut = false;
  let forceTimer;
  // Leave ten seconds for teardown before the 25 minute ceiling. Discovery is inside this outer limit.
  const stop = () => {
    timedOut = true;
    const kill = (signal) => { try { process.kill(process.platform === 'win32' ? child.pid : -child.pid, signal); } catch (e) { if (e.code !== 'ESRCH') throw e; } };
    kill('SIGTERM');
    forceTimer ??= setTimeout(() => kill('SIGKILL'), 5000);
  };
  const timer = setTimeout(stop, io.timeoutMilliseconds ?? (MAX_SECONDS - 10) * 1000);
  const append = (chunk) => { const s = chunk.toString(); text += s; writeFileSync(logPath, s, { flag: 'a' }); process.stdout.write(s); };
  child.stdout.on('data', append); child.stderr.on('data', append);
  process.once('SIGTERM', stop); process.once('SIGINT', stop);
  try {
    const status = await new Promise((done, reject) => { child.once('error', reject); child.once('close', done); });
    return { text, status: timedOut ? null : status, seconds: (performance.now() - started) / 1000, timedOut };
  } finally {
    clearTimeout(timer); clearTimeout(forceTimer);
    process.removeListener('SIGTERM', stop); process.removeListener('SIGINT', stop);
  }
}

export async function runShard(plan, id, out, io = {}) {
  validatePlan(plan);
  const shard = plan.shards.find((s) => s.id === id);
  if (!shard) throw new Error(`Unknown shard ${id}`);
  mkdirSync(out, { recursive: true });
  const resultPath = join(out, `shard-${id}.result`);
  if (existsSync(resultPath)) {
    let previous;
    try { previous = JSON.parse(readFileSync(resultPath, 'utf8')); } catch { /* interrupted write: rerun */ }
    if (previous?.planId && previous.planId !== plan.planId) throw new Error(`Shard ${id} result belongs to another plan; use a separate output folder`);
    if (previous?.status === 'done') {
      if (previous.shard !== id || previous.filter !== shard.filter) throw new Error(`Shard ${id} result does not match the plan`);
      if (previous.ok === true && resultProblems(plan, shard, previous).length) throw new Error(`Shard ${id} has an incomplete or invalid done result`);
      return previous;
    }
  }
  const attempt = mkdtempSync(join(resolve(out), `shard-${id}-`));
  const logPath = join(attempt, 'gate.log');
  const argv = ['--project', plan.project, '--configuration', plan.configuration, '--filter', shard.filter,
    '--max-seconds', String(MAX_SECONDS), '--budget-report-only', '--slowest', '--log', join(attempt, 'tests.log'), '--', '-m:2', '-nodeReuse:false'];
  const base = { schemaVersion: 1, planId: plan.planId, shard: id, filter: shard.filter, log: logPath };
  atomicJson(resultPath, { ...base, status: 'running' });
  let run;
  try { run = await (io.executeGate ?? executeGate)({ argv, env: { ...process.env, TMPDIR: attempt, DOTNET_CLI_UI_LANGUAGE: 'en' }, logPath }); }
  catch (error) { run = { text: `Gate could not finish: ${error.message}`, status: null, seconds: null }; }
  if (io.executeGate) writeFileSync(logPath, run.text);
  const measured = [...run.text.matchAll(/^suite-gate: (\d+) tests discovered in /gm)];
  const discovered = measured.length === 1 ? Number(measured[0][1]) : null;
  const { summaries, aborted, started, noMatch } = readRun(run.text);
  const sum = (field) => summaries.length ? summaries.reduce((n, s) => n + s[field], 0) : null;
  const verdicts = run.text.match(/^SUITE GATE: (?:PASS[^\r\n]*|FAIL)\r?$/gm) ?? [];
  const failedTests = new Set([...run.text.matchAll(/^\s*Failed\s+(.+?)\s+\[[^\r\n]*\]\s*$/gm)].map((m) => m[1]));
  for (const m of run.text.matchAll(/^\[xUnit\.net[^\r\n]*?\]\s+(.+?)\s+\[FAIL\]\s*$/gm)) failedTests.add(m[1]);
  for (const folder of readdirSync(attempt, { withFileTypes: true })) {
    const path = join(attempt, folder.name, 'suite.trx');
    if (folder.isDirectory() && existsSync(path)) {
      for (const row of trxResults(readFileSync(path, 'utf8'))) if (row.outcome === 'Failed') failedTests.add(row.testName);
    }
  }
  const result = { ...base, status: 'done', ok: true, gateVerdict: verdicts.length === 1 ? verdicts[0] : null,
    gateExitCode: run.status, discovered, executed: sum('total'), passed: sum('passed'), skipped: sum('skipped'), failed: sum('failed'),
    failedTests: [...failedTests].sort(), seconds: run.seconds, timedOut: run.timedOut === true };
  result.problems = resultProblems(plan, shard, result);
  if (aborted.length || started !== summaries.length + noMatch || result.timedOut) result.problems.push('the gate aborted, stopped early or exceeded the outer time limit');
  result.ok = result.problems.length === 0;
  atomicJson(resultPath, result);
  return result;
}

function args(argv) {
  const options = { command: argv[0], out: 'suite-results' };
  for (let i = 1; i < argv.length; i += 2) {
    const key = argv[i]; const value = argv[i + 1];
    if (!['--out', '--list', '--shards', '--timings', '--project', '--shard'].includes(key) || !value || value.startsWith('--')) {
      throw new Error(`Unknown option or missing value: ${key}`);
    }
    options[key.slice(2)] = value;
  }
  return options;
}
function revision() {
  const run = spawnSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' });
  if (run.status !== 0) throw new Error('Cannot read the checkout revision');
  return run.stdout.trim();
}
function checkoutMatchesRevision(planned) {
  if (typeof planned !== 'string' || !/^[a-f0-9]{40,64}$/.test(planned)) return false;
  // A committed plan necessarily follows its recorded revision. Compare product files, including local edits,
  // instead of requiring the impossible equality between that revision and the commit holding the plan.
  const diff = spawnSync('git', ['diff', '--name-only', '--no-renames', '-z', planned, '--'], { encoding: 'utf8' });
  const untracked = spawnSync('git', ['ls-files', '--others', '--exclude-standard', '-z'], { encoding: 'utf8' });
  if (diff.status !== 0 || untracked.status !== 0) return false;
  return (diff.stdout + untracked.stdout).split('\0').filter(Boolean).every((path) => path.startsWith('cloud-plans/'));
}
function readPlan(path) { const plan = JSON.parse(readFileSync(path, 'utf8')); validatePlan(plan); return plan; }
function showPlan(plan) {
  const cover = validatePlan(plan);
  for (const shard of plan.shards) console.log(`shard ${shard.id}: ${shard.classes.length} classes, ${shard.testCount} tests${shard.estimatedSeconds === null ? '' : `, estimated ${shard.estimatedSeconds.toFixed(1)} s`}`);
  console.log(`PLAN COVER: PASS (${cover.covered} of ${cover.discovered} discovered tests; ${cover.classes} classes exactly once; no missing or duplicate cover)`);
}

export async function main(argv) {
  const options = args(argv);
  const out = resolve(options.out);
  const planPath = join(out, 'shards.json');
  if (options.command === 'plan') {
    mkdirSync(out, { recursive: true });
    if (existsSync(planPath)) {
      const existing = readPlan(planPath);
      if (!checkoutMatchesRevision(existing.revision)) throw new Error('Existing plan belongs to another revision; use a separate output folder');
      if ((options.shards !== undefined && Number(options.shards) !== existing.requestedShards)
          || (options.project !== undefined && options.project !== existing.project) || options.timings) {
        throw new Error('Existing plan has different planning inputs; use a separate output folder');
      }
      showPlan(existing); return 0;
    }
    let text;
    if (options.list) text = readFileSync(options.list, 'utf8');
    else {
      const listed = spawnSync('dotnet', ['test', options.project ?? PROJECT, '--list-tests', '--no-build', '-c', 'Release', '-m:2', '-nodeReuse:false'],
        { encoding: 'utf8', env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' }, maxBuffer: 512 * 1024 * 1024 });
      text = `${listed.stdout ?? ''}\n${listed.stderr ?? ''}`;
      writeFileSync(join(out, 'discovery.log'), text);
      if (listed.status !== 0 || listed.error) throw new Error(`Discovery failed: ${listed.error?.message ?? listed.status ?? listed.signal}`);
    }
    const plan = createPlan(parseDiscovery(text), { shards: options.shards === undefined ? 8 : Number(options.shards),
      project: options.project, revision: revision(), ...(options.timings ? { timings: readFileSync(options.timings, 'utf8') } : {}) });
    // Concurrent cloud sessions may discover together. Publish one complete immutable plan, never a partial JSON file.
    const candidate = join(out, `.shards-${process.pid}-${randomUUID()}.json`);
    writeFileSync(candidate, `${JSON.stringify(plan, null, 2)}\n`);
    try { linkSync(candidate, planPath); }
    catch (error) { if (error.code !== 'EEXIST') throw error; if (readPlan(planPath).planId !== plan.planId) throw new Error('Concurrent sessions produced different plans'); }
    finally { unlinkSync(candidate); }
    showPlan(plan); return 0;
  }
  if (!['run', 'merge'].includes(options.command)) throw new Error('Usage: suite-shard.mjs plan|run|merge --out <folder> [--list <file>] [--shards N] [--timings <trx>] [--shard i]');
  const plan = readPlan(planPath);
  if (options.command === 'run') {
    if (!checkoutMatchesRevision(plan.revision)) throw new Error('The checkout differs from the plan revision');
    if (!/^\d+$/.test(options.shard ?? '')) throw new Error('run requires --shard <zero-based index>');
    const result = await runShard(plan, Number(options.shard), out);
    console.log(`shard ${options.shard}: ${result.status}, ${result.ok ? 'PASS' : 'FAIL'}; ${result.executed ?? 'unknown'} executed of ${result.discovered ?? 'unknown'} discovered; ${result.seconds ?? 'unknown'} s`);
    return result.ok ? 0 : 1;
  }
  const results = [];
  for (const name of readdirSync(out).filter((name) => /^shard-\d+\.result$/.test(name))) {
    try {
      const result = JSON.parse(readFileSync(join(out, name), 'utf8'));
      if (name !== `shard-${result.shard}.result`) throw new Error('result filename and shard differ');
      results.push(result);
    } catch { results.push(null); }
  }
  const verdict = mergeResults(plan, results);
  console.log(`SHARDED SUITE: ${verdict.ok ? 'PASS' : 'FAIL'}`);
  console.log(`discovered ${verdict.discovered}; executed ${verdict.executed}; planned ${verdict.planned}`);
  for (const problem of verdict.problems) console.log(`  ${problem}`);
  for (const name of verdict.failedTests) console.log(`  FAILED: ${name}`);
  return verdict.ok ? 0 : 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.exitCode = await main(process.argv.slice(2)); }
  catch (error) { console.error(`suite-shard: ${error.message}`); if (process.argv[2] === 'merge') console.error('SHARDED SUITE: FAIL'); process.exitCode = 1; }
}
