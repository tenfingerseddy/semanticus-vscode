#!/usr/bin/env node
// The Jev replay gate, command line entry.
//
//   node tools/jev-replay/replay.mjs import    --fixture pilot1
//   node tools/jev-replay/replay.mjs reproduce --fixture pilot1
//   node tools/jev-replay/replay.mjs score     --fixture pilot1 [--mode gate]
//   node tools/jev-replay/replay.mjs ci
//
// Recorded answers only. Nothing on this path constructs a live oracle, and nothing on this path
// opens a socket. lib/oracle-live.mjs is not imported from here, and replay.test.mjs checks that
// by reading the source of every file, because one stray import is all it would take.

import { readFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { readRecords } from './lib/record.mjs';
import { createRecordedOracle, readOracleEntries } from './lib/oracle-recorded.mjs';
import { loadManifest } from './lib/manifest.mjs';
import { scoreRun } from './lib/score.mjs';
import {
  flatten, sectionsOf, pooledCalibration, stability, q8Clarity, runBlock,
} from './lib/pilot1-score.mjs';
import { importPilot1, VERDICT_PATH, WORK_DIR, MANIFEST_PATH } from './import-pilot1.mjs';

const HERE = resolve(fileURLToPath(new URL('.', import.meta.url)));
const TEST_PATH = join(HERE, 'replay.test.mjs');

function parseArgs(argv) {
  const out = { command: argv[0] ?? 'help', flags: {} };
  for (let i = 1; i < argv.length; i += 1) {
    const token = argv[i];
    if (token.startsWith('--')) {
      const name = token.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) out.flags[name] = true;
      else {
        out.flags[name] = next;
        i += 1;
      }
    }
  }
  return out;
}

// ------------------------------------------------------------------ float-tolerant comparison

const TOLERANCE = 1e-12;

/**
 * Walks two values and collects every difference, tracking the worst numeric delta seen.
 * Numbers are compared on absolute delta, which is the strict reading for values in 0 to 1 and
 * also fine for the millisecond and token counts here.
 */
export function compareDeep(actual, expected, path, state) {
  const at = path === '' ? '(root)' : path;
  if (expected === null || actual === null) {
    if (actual !== expected) {
      state.diffs.push(at + ': expected ' + JSON.stringify(expected)
        + ', got ' + JSON.stringify(actual));
    }
    return;
  }
  if (typeof expected === 'number' && typeof actual === 'number') {
    const delta = Math.abs(actual - expected);
    if (delta > state.worst.delta) state.worst = { delta, path: at };
    if (!(delta <= TOLERANCE)) {
      state.diffs.push(at + ': expected ' + expected + ', got ' + actual
        + ' (delta ' + delta + ')');
    }
    return;
  }
  if (Array.isArray(expected) || Array.isArray(actual)) {
    if (!Array.isArray(expected) || !Array.isArray(actual)) {
      state.diffs.push(at + ': one side is an array and the other is not');
      return;
    }
    if (expected.length !== actual.length) {
      state.diffs.push(at + ': expected ' + expected.length + ' items, got ' + actual.length);
    }
    const n = Math.min(expected.length, actual.length);
    for (let i = 0; i < n; i += 1) compareDeep(actual[i], expected[i], at + '[' + i + ']', state);
    return;
  }
  if (typeof expected === 'object' && typeof actual === 'object') {
    const keys = new Set([...Object.keys(expected), ...Object.keys(actual)]);
    for (const key of [...keys].sort()) {
      if (!(key in expected)) {
        state.diffs.push(at + '.' + key + ': present in the harness output, absent in verdict.json');
        continue;
      }
      if (!(key in actual)) {
        state.diffs.push(at + '.' + key + ': present in verdict.json, absent in the harness output');
        continue;
      }
      compareDeep(actual[key], expected[key], at + '.' + key, state);
    }
    return;
  }
  if (actual !== expected) {
    state.diffs.push(at + ': expected ' + JSON.stringify(expected)
      + ', got ' + JSON.stringify(actual));
  }
}

function newState() {
  return { diffs: [], worst: { delta: 0, path: '(none)' } };
}

// ------------------------------------------------------------------ commands

function loadFixture(fixture) {
  const dir = join(WORK_DIR, '..', fixture);
  const recordsPath = join(dir, 'records.jsonl');
  if (!existsSync(recordsPath)) {
    throw new Error('fixture "' + fixture + '" is not imported yet. Run the import command first.');
  }
  const records = readRecords(readFileSync(recordsPath, 'utf8'));
  const run2Path = join(dir, 'records-run2.jsonl');
  const run2 = existsSync(run2Path) ? readRecords(readFileSync(run2Path, 'utf8')) : [];
  const oracle = createRecordedOracle(
    readOracleEntries(readFileSync(join(dir, 'oracle.jsonl'), 'utf8')));
  return { dir, records, run2, oracle };
}

function commandImport(flags) {
  const fixture = flags.fixture ?? 'pilot1';
  if (fixture !== 'pilot1') {
    throw new Error('the only fixture with an importer today is pilot1, got "' + fixture + '"');
  }
  const result = importPilot1();
  console.log('import pilot1: ' + result.counts.run1 + ' run-one records, '
    + result.counts.run2 + ' run-two records, ' + result.counts.oracle + ' recorded answers');
  console.log('  fixture hash ' + result.fixtureHash);
  console.log('  written to ' + result.outDir);
  return result;
}

function reproducePilot1() {
  const { records, run2, oracle } = loadFixture('pilot1');
  const verdict = JSON.parse(readFileSync(VERDICT_PATH, 'utf8'));

  const rows = flatten(records);
  const sections = sectionsOf(rows);
  const pooled = pooledCalibration(rows);
  const stab = stability(records, run2);

  const required = newState();
  compareDeep(rows, verdict.answers, 'answers', required);
  compareDeep(sections, verdict.sections, 'sections', required);
  compareDeep(pooled, verdict.pooled_calibration, 'pooled_calibration', required);
  compareDeep(stab, verdict.stability_run1_vs_run2, 'stability_run1_vs_run2', required);

  // Extra checks, beyond the required list. They exercise the same records and cost nothing.
  const extra = newState();
  compareDeep(q8Clarity(records), verdict.q8_clarity, 'q8_clarity', extra);
  const run = runBlock(records);
  const expectedRun = { ...verdict.run };
  delete expectedRun.input_cost_usd;
  delete expectedRun.jev_version;
  delete expectedRun.concurrency;
  compareDeep(run, expectedRun, 'run', extra);

  return {
    rows, sections, pooled, stab, oracle, records,
    required, extra,
    counts: {
      rows: rows.length,
      expectedRows: verdict.answers.length,
      sections: sections.length,
      expectedSections: verdict.sections.length,
    },
  };
}

function commandReproduce(flags) {
  const fixture = flags.fixture ?? 'pilot1';
  if (fixture !== 'pilot1') {
    throw new Error('reproduce knows only the pilot1 fixture today, got "' + fixture + '"');
  }
  const r = reproducePilot1();
  console.log('reproduce pilot1, from the imported records only');
  console.log('  answer rows   ' + r.counts.rows + ' of ' + r.counts.expectedRows
    + ' expected, compared field by field');
  console.log('  sections      ' + r.counts.sections + ' of ' + r.counts.expectedSections
    + ' expected, every n, accuracy, baseline accuracy, mean confidence and band');
  console.log('  pooled calibration and run-to-run stability compared in full');
  console.log('  float tolerance ' + TOLERANCE);
  console.log('  worst delta   ' + r.required.worst.delta + ' at ' + r.required.worst.path);
  console.log('  extra checks  q8_clarity and the run block, worst delta '
    + r.extra.worst.delta + ' at ' + r.extra.worst.path);

  let failed = false;
  if (r.counts.rows !== r.counts.expectedRows
      || r.counts.sections !== r.counts.expectedSections) {
    failed = true;
    console.error('REPRODUCTION FAILED: the row or section count does not match verdict.json');
  }
  if (r.required.diffs.length > 0) {
    failed = true;
    console.error('REPRODUCTION FAILED: ' + r.required.diffs.length
      + ' differences against verdict.json');
    for (const diff of r.required.diffs.slice(0, 40)) console.error('    ' + diff);
    if (r.required.diffs.length > 40) {
      console.error('    ... and ' + (r.required.diffs.length - 40) + ' more');
    }
  }
  if (r.extra.diffs.length > 0) {
    failed = true;
    console.error('REPRODUCTION FAILED on an extra check: ' + r.extra.diffs.length
      + ' differences');
    for (const diff of r.extra.diffs.slice(0, 20)) console.error('    ' + diff);
  }
  if (!failed) console.log('  pilot one reproduces exactly');

  // The harness's own general scorer, over the same records, through the recorded oracle.
  const manifest = loadManifest(MANIFEST_PATH);
  const report = scoreRun({
    records: r.records,
    oracle: r.oracle,
    manifest,
    mode: 'development',
    splitPolicy: manifest.splitPolicy,
  });
  printReport(report);
  return { failed, report, worst: r.required.worst };
}

function pct(x) {
  return x === null ? '    -' : (x * 100).toFixed(1).padStart(5, ' ') + '%';
}

export function printReport(report) {
  console.log('');
  console.log('general scorer, ' + report.mode + ' mode, recorded oracle');
  console.log('  manifest ' + report.manifestDigest + ' committed ' + report.manifestCommittedUtc);
  console.log(report.barsRatified
    ? '  bars sealed and ratified, so a gate run against them can fail a point'
    : '  bars "' + report.manifestStatus + '", not ratified: this reading is development '
      + 'evidence and not a ratified gate');
  console.log('  records ' + report.records + ', oracle hits ' + report.oracle.hits);
  console.log('');
  console.log('  point                        n   acc  prec   cov    FP   FRef  unc'
    + '  rule  base  unans  bar');
  for (const [pointId, m] of Object.entries(report.points)) {
    console.log('  ' + pointId.padEnd(24, ' ')
      + String(m.n).padStart(5, ' ')
      + pct(m.accuracy)
      + pct(m.precision)
      + pct(m.coverage)
      + pct(m.falsePositives.rate)
      + pct(m.falseRefusals.rate)
      + pct(m.uncertainty.shareBelowThreshold)
      + pct(m.ruleAgreement.rate)
      + pct(m.baselineAccuracy)
      + String(m.answerability.unanswerable).padStart(6, ' ')
      + '  ' + (m.bar.passes ? 'pass' : 'FAIL'));
  }
  console.log('');
  console.log('  acc is correctness against the record label. rule is agreement with the '
    + "rule and is never truth.");
  console.log('  unc is the share below the confidence threshold. unans counts cases no answer '
    + 'could have got right.');
  const families = {};
  for (const m of Object.values(report.points)) {
    for (const [family, count] of Object.entries(m.caseFamilies)) {
      families[family] = (families[family] ?? 0) + count;
    }
  }
  console.log('  case families: ' + Object.entries(families)
    .map(([k, v]) => k + ' ' + v).join(', '));
  const refused = Object.entries(report.points).filter(([, m]) => m.notDecided > 0);
  if (refused.length > 0) {
    console.log('  answers the engine would refuse, counted as not decided: '
      + refused.map(([id, m]) => id + ' ' + m.notDecided).join(', '));
    for (const [id, m] of refused) {
      for (const reason of m.notDecidedReasons) console.log('    ' + id + ': ' + reason);
    }
  }
}

function commandScore(flags) {
  const fixture = flags.fixture ?? 'pilot1';
  // A mistyped mode must never quietly become a development run. "--mode Gate" used to score
  // and print as a development run, which reads afterwards as a gate that passed.
  const mode = (flags.mode === undefined || flags.mode === true)
    ? 'development'
    : String(flags.mode);
  if (mode !== 'gate' && mode !== 'development') {
    throw new Error('--mode must be "gate" or "development", got "' + mode
      + '". An unrecognised mode is refused, never read as development.');
  }
  const { records, oracle } = loadFixture(fixture);
  const manifest = loadManifest(flags.manifest ? resolve(flags.manifest) : MANIFEST_PATH);
  const report = scoreRun({
    records, oracle, manifest, mode, splitPolicy: manifest.splitPolicy,
  });
  printReport(report);
  const failing = Object.entries(report.points).filter(([, m]) => !m.bar.passes);
  if (mode === 'gate' && failing.length > 0) {
    console.error('GATE FAILED on ' + failing.length + ' point(s): '
      + failing.map(([id]) => id).join(', '));
    return { failed: true, report };
  }
  return { failed: false, report };
}

function commandCi() {
  const started = Date.now();
  let failed = false;
  console.log('jev-replay ci: recorded answers only, no network, no toolchain beyond node');
  console.log('');
  try {
    commandImport({ fixture: 'pilot1' });
  } catch (err) {
    failed = true;
    console.error('IMPORT FAILED: ' + err.message);
  }
  console.log('');
  if (!failed) {
    try {
      const result = commandReproduce({ fixture: 'pilot1' });
      if (result.failed) failed = true;
    } catch (err) {
      failed = true;
      console.error('REPRODUCE FAILED: ' + err.message);
    }
  }
  console.log('');
  const test = spawnSync(process.execPath, ['--test', TEST_PATH], {
    stdio: 'inherit',
    env: { ...process.env, JEV_REPLAY_LIVE: '' },
  });
  if (test.status !== 0) {
    failed = true;
    console.error('SELF-TEST FAILED with exit code ' + test.status);
  }
  const elapsed = ((Date.now() - started) / 1000).toFixed(2);
  console.log('');
  console.log('jev-replay ci ' + (failed ? 'FAILED' : 'passed') + ' in ' + elapsed + ' s');
  return failed ? 1 : 0;
}

function commandHelp() {
  console.log([
    'jev-replay, the replay gate for Jev decision points.',
    '',
    '  import    --fixture pilot1    read the pilot fixture and write harness records',
    '  reproduce --fixture pilot1    recompute the pilot numbers and compare to verdict.json',
    '  score     --fixture pilot1    run the general scorer [--mode gate|development]',
    '  ci                            import, reproduce and the self-test, recorded answers only',
    '',
    'Recorded answers only. The live oracle is not reachable from any of these commands.',
  ].join('\n'));
  return 0;
}

function main() {
  const { command, flags } = parseArgs(process.argv.slice(2));
  try {
    if (command === 'import') {
      commandImport(flags);
      return 0;
    }
    if (command === 'reproduce') return commandReproduce(flags).failed ? 1 : 0;
    if (command === 'score') return commandScore(flags).failed ? 1 : 0;
    if (command === 'ci') return commandCi();
    if (command === 'help' || command === '--help' || command === '-h') return commandHelp();
    console.error('unknown command "' + command + '"');
    commandHelp();
    return 2;
  } catch (err) {
    console.error(err.name + ': ' + err.message);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  process.exitCode = main();
}
