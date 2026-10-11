// The jev-replay self-test. Recorded answers only.
//
// This file never imports lib/oracle-live.mjs, and one of the tests below proves that by reading
// the source of every file in the harness. A default run must construct no live oracle and must
// make no network call, and "must" here means checked, not intended.
//
// Run:  node --test tools/jev-replay/replay.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

import {
  canonicalJson, oracleKey, offerDigest, sha256Hex, assertOrderFreeRequest, DigestError,
} from './lib/digest.mjs';
import { offerOrder, assertNeutralCandidates, OfferError } from './lib/offer.mjs';
import {
  CASE_FAMILIES, RecordSchemaError, LabelIndependenceError, RecordWriter, SCHEMA_VERSION,
  validateRecord, assertLabelIndependent, readRecords, pyStr,
} from './lib/record.mjs';
import { assignSplit, assertGateFixtures, SplitError } from './lib/split.mjs';
import {
  barFor, loadManifest, computeManifestDigest, ManifestError,
} from './lib/manifest.mjs';
import { scoreRun, readAnswer, AnswerRejected } from './lib/score.mjs';
import { createRecordedOracle, OracleMissError } from './lib/oracle-recorded.mjs';
import { buildRecords, importPilot1, MANIFEST_PATH, VERDICT_PATH } from './import-pilot1.mjs';
import {
  flatten, sectionsOf, pooledCalibration, stability, pyRound2,
} from './lib/pilot1-score.mjs';

const HERE = resolve(fileURLToPath(new URL('.', import.meta.url)));
const WORK = join(HERE, '.work', 'selftest');
mkdirSync(WORK, { recursive: true });

// Import the pilot fixture before any test reads it. Several tests read .work/pilot1 directly,
// and without this they read whatever the last run left on disk: after a change to the oracle
// key that is a stale file, and the suite then passes or fails depending on the order it was
// last run in. Seen on the first run after the key started carrying the option order.
importPilot1();

// ------------------------------------------------------------------ a valid record to mutate

function makeRecord(overrides = {}) {
  const base = {
    schemaVersion: SCHEMA_VERSION,
    recordId: 'test:1',
    fixture: { id: 'unit', hash: 'f'.repeat(64), role: 'development' },
    split: { membership: 'development', modelId: 'unit_model', family: 'display_folder' },
    model: { id: 'unit_model', hash: 'm'.repeat(64) },
    source: { hash: 'none:unit-test-has-no-source' },
    point: { id: 'pilot1.display_folder', version: '1.0.0', kind: 'single' },
    prompt: { version: 'unit-prompt-1' },
    servedModel: { id: 'jev-1.13.0', version: 'jev-1.13.0' },
    producer: { system: 'jev', version: 'jev-1.13.0' },
    task: { kind: 'typed-micro-question', instructions: 'Which display folder?' },
    context: { knowable: { object: 'Sales[Amount]', kind: 'measure', extra: {} } },
    candidates: { set: ['Finance', 'Sales', 'none'], offeredOrder: ['Sales', 'none', 'Finance'] },
    request: { allowed: { questionType: 'choice', criteria: { Sales: 'a', Finance: 'b', none: 'c' } } },
    response: { raw: { type: 'choice', choice: 'Sales', confidence: 0.9 } },
    seed: 'unit-seed',
    thresholds: { confidence: 0.85 },
    fallback: 'offer the full list',
    baseline: { version: 'unit-baseline', output: 'Sales' },
    label: { value: 'Sales', author: 'reviewer-a', evidence: { note: 'checked by hand' } },
    ruleAnswer: 'Finance',
    answerability: 'answerable',
    caseFamilies: Object.fromEntries(CASE_FAMILIES.map((f) => [f, false])),
    outcome: {
      errors: [], retryAttempts: 0, cacheHit: false, inputTokens: 100, outputTokens: 10,
      pricingBasis: 'unit', elapsedMs: 5,
    },
  };
  return { ...base, ...overrides };
}

// ------------------------------------------------------------------ 1. record schema

test('the record schema names every field Astra listed', () => {
  const record = makeRecord();
  assert.equal(validateRecord(record), true);
  // The field list is the contract, so spot-check that the load-bearing ones are really required.
  for (const path of [
    'fixture.hash', 'model.hash', 'source.hash', 'split.membership', 'split.family',
    'candidates.set', 'candidates.offeredOrder', 'task.instructions', 'context.knowable',
    'request.allowed', 'response.raw', 'point.version', 'schemaVersion', 'prompt.version',
    'servedModel.version', 'seed', 'thresholds.confidence', 'fallback', 'baseline.version',
    'baseline.output', 'label.value', 'label.author', 'label.evidence', 'outcome.errors',
    'outcome.retryAttempts', 'outcome.cacheHit', 'outcome.inputTokens', 'outcome.outputTokens',
    'outcome.pricingBasis', 'outcome.elapsedMs',
  ]) {
    const broken = JSON.parse(JSON.stringify(makeRecord()));
    const parts = path.split('.');
    let node = broken;
    for (const part of parts.slice(0, -1)) node = node[part];
    delete node[parts[parts.length - 1]];
    assert.throws(() => validateRecord(broken), (err) => {
      assert.ok(err instanceof RecordSchemaError, 'expected a RecordSchemaError for ' + path);
      assert.equal(err.field, path);
      assert.match(err.message, new RegExp(path.replace(/\./g, '\\.')));
      return true;
    }, 'removing ' + path + ' must be refused by name');
  }
});

test('the writer refuses a record missing a field and names it, and writes nothing', () => {
  const path = join(WORK, 'refused.jsonl');
  const writer = new RecordWriter(path);
  const broken = makeRecord();
  delete broken.seed;
  assert.throws(() => writer.write(broken), (err) => {
    assert.equal(err.field, 'seed');
    return true;
  });
  assert.equal(readFileSync(path, 'utf8'), '', 'a refused record must not reach the file');
  assert.equal(writer.write(makeRecord()), 1);
  assert.equal(readRecords(readFileSync(path, 'utf8')).length, 1);
});

test('a sequence point must carry the selected, executed, engine, diff and outcome fields', () => {
  const sequence = makeRecord({
    point: { id: 'pilot1.display_folder', version: '1.0.0', kind: 'sequence' },
  });
  assert.throws(() => validateRecord(sequence), (err) => {
    assert.equal(err.field, 'sequence.selectedAction');
    return true;
  });
  sequence.sequence = {
    selectedAction: 'set_display_folder',
    executedAction: 'set_display_folder',
    engineResult: { ok: true },
    acceptedDiff: { before: 'none', after: 'Sales' },
    finalOutcome: 'applied',
  };
  assert.equal(validateRecord(sequence), true);
});

test('every case family is carried on every record so a fixture with none is still countable', () => {
  const record = makeRecord();
  for (const family of CASE_FAMILIES) assert.ok(family in record.caseFamilies);
  const broken = makeRecord();
  delete broken.caseFamilies.maliciousDescriptions;
  assert.throws(() => validateRecord(broken), (err) => {
    assert.equal(err.field, 'caseFamilies.maliciousDescriptions');
    return true;
  });
});

// ------------------------------------------------------------------ 2. the two digests

test('the oracle key carries the order actually offered, as the engine does', () => {
  // Semanticus.Core/Decisions/DecisionDigest.cs:49 writes each question's options IN ORDER, and
  // its header rule 3 says a different option order is a different question. The harness has to
  // key the same way, or the two doors evaluate different things under one name.
  const a = makeRecord({
    recordId: 'a',
    candidates: { set: ['Finance', 'Sales', 'none'], offeredOrder: ['Sales', 'none', 'Finance'] },
  });
  const b = makeRecord({
    recordId: 'b',
    candidates: { set: ['none', 'Sales', 'Finance'], offeredOrder: ['none', 'Finance', 'Sales'] },
  });
  assert.notEqual(oracleKey(a), oracleKey(b),
    'the same options in a different order is a different question, so a different key');
  assert.notEqual(offerDigest(a), offerDigest(b), 'the offer digest must record the real order');
  assert.notEqual(oracleKey(a), offerDigest(a), 'the two digests are not the same number');

  // The same order twice is the same key, so a genuine replay still finds its recording.
  const again = makeRecord({
    recordId: 'a-again',
    candidates: { set: ['none', 'Sales', 'Finance'], offeredOrder: ['Sales', 'none', 'Finance'] },
  });
  assert.equal(oracleKey(a), oracleKey(again),
    'the same evidence and the same offered order must give the same key');
});

test('the oracle key changes when the evidence or a version changes', () => {
  const base = makeRecord();
  const key = oracleKey(base);
  const cases = [
    ['fixture hash', (r) => { r.fixture.hash = 'a'.repeat(64); }],
    ['model hash', (r) => { r.model.hash = 'b'.repeat(64); }],
    ['source hash', (r) => { r.source.hash = 'none:other'; }],
    ['knowable context', (r) => { r.context.knowable.object = 'Sales[Other]'; }],
    ['allowed request', (r) => { r.request.allowed.criteria.Sales = 'different'; }],
    ['candidate set', (r) => { r.candidates.set = ['Finance', 'Sales']; }],
    ['point version', (r) => { r.point.version = '2.0.0'; }],
    ['schema version', (r) => { r.schemaVersion = '9.9.9'; }],
    ['prompt version', (r) => { r.prompt.version = 'other'; }],
    ['served model version', (r) => { r.servedModel.version = 'jev-2.0.0'; }],
  ];
  for (const [name, mutate] of cases) {
    const record = JSON.parse(JSON.stringify(base));
    mutate(record);
    assert.notEqual(oracleKey(record), key, 'changing the ' + name + ' must change the key');
  }
});

test('the key is never the question text alone: same words, different object, different key', () => {
  const a = makeRecord({ recordId: 'a' });
  const b = JSON.parse(JSON.stringify(a));
  b.context.knowable.object = 'Costs[Amount]';
  assert.equal(a.task.instructions, b.task.instructions);
  assert.notEqual(oracleKey(a), oracleKey(b));
});

test('an allowed request that lists the candidates as an array is refused', () => {
  const record = makeRecord();
  record.request.allowed.options = ['none', 'Sales', 'Finance'];
  assert.throws(() => assertOrderFreeRequest(record), DigestError);
  assert.throws(() => oracleKey(record), DigestError);
});

test('canonical JSON sorts object keys and keeps array order', () => {
  assert.equal(canonicalJson({ b: 1, a: 2 }), '{"a":2,"b":1}');
  assert.equal(canonicalJson([3, 1, 2]), '[3,1,2]');
  assert.equal(sha256Hex('').length, 64);
});

// ------------------------------------------------------------------ 3. neutral, shuffled options

test('the same seed reproduces the same option order and a different seed does not', () => {
  const options = ['alpha', 'bravo', 'charlie', 'delta', 'echo', 'foxtrot', 'golf', 'hotel'];
  const first = offerOrder(options, 'seed-one');
  const again = offerOrder([...options].reverse(), 'seed-one');
  assert.deepEqual(again, first, 'the same seed must reproduce the same order');
  assert.notDeepEqual(offerOrder(options, 'seed-two'), first,
    'a different seed must give a different order');
  assert.deepEqual([...first].sort(), [...options].sort(), 'the order is a permutation');
  assert.throws(() => offerOrder(options, ''), OfferError, 'an unseeded order is refused');
});

test('option text that names the producing system is refused', () => {
  assert.equal(assertNeutralCandidates(['Sales', 'Finance', 'none']), true);
  for (const bad of ['Sales (suggested by Jev)', 'the Claude answer', 'baseline', 'system A']) {
    assert.throws(() => assertNeutralCandidates(['Sales', bad]), OfferError,
      'expected "' + bad + '" to be refused');
  }
  assert.throws(() => offerOrder(['Sales', 'jev pick'], 'seed'), OfferError);
});

// ------------------------------------------------------------------ 4. labels apart from rules

test('the scorer never uses ruleAnswer as truth', () => {
  // The label says Sales, the rule says Finance, the answer says Sales.
  // Correct against the label, disagreeing with the rule. Both numbers must show that.
  const record = makeRecord();
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(record), response: record.response.raw },
  ]);
  const manifest = loadManifest(MANIFEST_PATH);
  const report = scoreRun({ records: [record], oracle, manifest, mode: 'development' });
  const point = report.points['pilot1.display_folder'];
  assert.equal(point.accuracy, 1, 'the answer matches the label, so accuracy is 1');
  assert.equal(point.ruleAgreement.rate, 0, 'the answer differs from the rule, so agreement is 0');
  assert.notEqual(point.accuracy, point.ruleAgreement.rate);

  // Flip the rule to agree and the correctness number must not move.
  const agreeing = makeRecord({ recordId: 'agree', ruleAnswer: 'Sales' });
  const oracle2 = createRecordedOracle([
    { oracleKey: oracleKey(agreeing), response: agreeing.response.raw },
  ]);
  const report2 = scoreRun({ records: [agreeing], oracle: oracle2, manifest,
    mode: 'development' });
  assert.equal(report2.points['pilot1.display_folder'].accuracy, 1);
  assert.equal(report2.points['pilot1.display_folder'].ruleAgreement.rate, 1);
});

test('a label written by the producing system is refused at import', () => {
  const selfLabelled = makeRecord({
    label: { value: 'Sales', author: 'jev', evidence: {} },
  });
  assert.throws(() => assertLabelIndependent(selfLabelled), LabelIndependenceError);
  assert.throws(() => validateRecord(selfLabelled), LabelIndependenceError);
  const writer = new RecordWriter(join(WORK, 'self-labelled.jsonl'));
  assert.throws(() => writer.write(selfLabelled), LabelIndependenceError);
  assert.equal(readFileSync(join(WORK, 'self-labelled.jsonl'), 'utf8'), '');

  // The served model id counts as the same identity too.
  const servedAuthor = makeRecord({
    label: { value: 'Sales', author: 'jev-1.13.0', evidence: {} },
  });
  assert.throws(() => assertLabelIndependent(servedAuthor), LabelIndependenceError);

  // A path or a sentence that merely mentions the system is refused as well, on purpose.
  assert.throws(
    () => assertLabelIndependent(makeRecord({
      label: { value: 'Sales', author: 'read from the jev pilot folder', evidence: {} },
    })),
    LabelIndependenceError);
});

// ------------------------------------------------------------------ 5. splits

test('splits are assigned by model and by family, never by row', () => {
  const policy = { heldOutModels: ['northstar'], heldOutFamilies: ['one_side'] };
  assert.equal(assignSplit({ modelId: 'northstar', family: 'hidden' }, policy), 'held-out');
  assert.equal(assignSplit({ modelId: 'aw_packt', family: 'one_side' }, policy), 'held-out');
  assert.equal(assignSplit({ modelId: 'aw_packt', family: 'hidden' }, policy), 'development');
  // Two different rows of the same model and family always land together.
  assert.equal(
    assignSplit({ modelId: 'aw_packt', family: 'hidden' }, policy),
    assignSplit({ modelId: 'aw_packt', family: 'hidden' }, policy));
  assert.throws(() => assignSplit({ family: 'hidden' }, policy), SplitError);
});

test('a held-out gate run refuses a fixture marked development', () => {
  const development = makeRecord();
  assert.equal(development.fixture.role, 'development');
  assert.throws(() => assertGateFixtures([development]), (err) => {
    assert.ok(err instanceof SplitError);
    assert.match(err.message, /development/);
    return true;
  });
  const manifest = loadManifest(MANIFEST_PATH);
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(development), response: development.response.raw },
  ]);
  assert.throws(
    () => scoreRun({
      records: [development], oracle, manifest, mode: 'gate',
      splitPolicy: manifest.splitPolicy,
    }),
    SplitError, 'scoring a gate run on development evidence must be refused');
  // And with no split policy at all it is refused before it gets that far, for its own reason.
  assert.throws(
    () => scoreRun({ records: [development], oracle, manifest, mode: 'gate' }),
    /split policy/);
  // The same records are fine in development mode, which is what the pilot is for.
  assert.ok(scoreRun({
    records: [development], oracle, manifest, mode: 'development',
    splitPolicy: manifest.splitPolicy,
  }));
});

test('the pilot one import is marked development on every record', () => {
  const records = readRecords(
    readFileSync(join(HERE, '.work', 'pilot1', 'records.jsonl'), 'utf8'));
  assert.ok(records.length > 0);
  for (const record of records) {
    assert.equal(record.fixture.role, 'development');
    assert.equal(record.split.membership, 'development');
  }
  assert.throws(() => assertGateFixtures(records), SplitError);
});

// ------------------------------------------------------------------ 6. bars

test('a point with no bar in the manifest is refused, by name', () => {
  const manifest = loadManifest(MANIFEST_PATH);
  assert.ok(barFor(manifest, 'pilot1.display_folder'));
  assert.throws(() => barFor(manifest, 'pilot1.not_declared'), (err) => {
    assert.ok(err instanceof ManifestError);
    assert.match(err.message, /pilot1\.not_declared/);
    return true;
  });
  const record = makeRecord({
    point: { id: 'pilot1.not_declared', version: '1.0.0', kind: 'single' },
  });
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(record), response: record.response.raw },
  ]);
  assert.throws(
    () => scoreRun({ records: [record], oracle, manifest, mode: 'development' }), ManifestError);
});

test('every declared bar carries all five numbers and a fallback', () => {
  const manifest = loadManifest(MANIFEST_PATH);
  assert.ok(Object.keys(manifest.points).length >= 8);
  for (const [pointId, bar] of Object.entries(manifest.points)) {
    for (const field of ['precisionFloor', 'coverageFloor', 'falsePositiveCeiling',
      'falseRefusalCeiling', 'confidenceThreshold']) {
      assert.equal(typeof bar[field], 'number', pointId + ' needs a numeric ' + field);
    }
    assert.equal(typeof bar.fallback, 'string', pointId + ' needs a fallback');
  }
  assert.match(manifest.committedUtc, /^\d{4}-\d{2}-\d{2}T/);
});

test('a manifest edited after it was committed is refused, and a run records its digest', () => {
  const manifest = loadManifest(MANIFEST_PATH);
  assert.equal(computeManifestDigest(manifest), manifest.manifestDigest);

  const tampered = JSON.parse(readFileSync(MANIFEST_PATH, 'utf8'));
  tampered.points['pilot1.display_folder'].precisionFloor = 0.1;
  const tamperedPath = join(WORK, 'tampered-manifest.json');
  writeFileSync(tamperedPath, JSON.stringify(tampered, null, 2));
  assert.throws(() => loadManifest(tamperedPath), (err) => {
    assert.ok(err instanceof ManifestError);
    assert.match(err.message, /digest does not match/);
    return true;
  });

  const record = makeRecord();
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(record), response: record.response.raw },
  ]);
  const report = scoreRun({ records: [record], oracle, manifest, mode: 'development' });
  assert.equal(report.manifestDigest, manifest.manifestDigest,
    'a run must record the manifest digest it ran under');
  assert.equal(report.manifestCommittedUtc, manifest.committedUtc);
});

// ------------------------------------------------------------------ 7. the report

test('the report carries every number the gate has to publish', () => {
  const records = readRecords(
    readFileSync(join(HERE, '.work', 'pilot1', 'records.jsonl'), 'utf8'));
  const entries = readFileSync(join(HERE, '.work', 'pilot1', 'oracle.jsonl'), 'utf8')
    .split('\n').filter(Boolean).map((line) => JSON.parse(line));
  const oracle = createRecordedOracle(entries);
  const manifest = loadManifest(MANIFEST_PATH);
  const report = scoreRun({
    records, oracle, manifest, mode: 'development', splitPolicy: manifest.splitPolicy,
  });
  assert.equal(report.records, records.length);
  assert.equal(Object.keys(report.points).length, 8);
  for (const [pointId, m] of Object.entries(report.points)) {
    for (const key of ['n', 'accuracy', 'precision', 'coverage', 'falsePositives',
      'falseRefusals', 'uncertainty', 'answerability', 'caseFamilies', 'ruleAgreement',
      'baselineAccuracy', 'fallback', 'bar']) {
      assert.ok(key in m, pointId + ' must report ' + key);
    }
    assert.ok('shareBelowThreshold' in m.uncertainty);
    for (const band of ['below 0.6', '0.6 to 0.85', 'above 0.85']) {
      assert.ok(band in m.uncertainty.accuracyByBand, pointId + ' must report the band ' + band);
      assert.ok('accuracy' in m.uncertainty.accuracyByBand[band]);
      assert.ok('share' in m.uncertainty.accuracyByBand[band]);
    }
    for (const kind of ['answerable', 'unanswerable', 'unsupported']) {
      assert.equal(typeof m.answerability[kind], 'number',
        pointId + ' must count ' + kind + ' cases');
    }
    for (const family of CASE_FAMILIES) {
      assert.equal(typeof m.caseFamilies[family], 'number',
        pointId + ' must count the family ' + family + ' even when it holds none');
    }
    assert.notEqual(m.ruleAgreement, undefined);
    assert.equal(typeof m.baselineAccuracy, 'number');
  }
  // Pilot one really does carry unanswerable cases and stale evidence, so the counters are live.
  assert.equal(report.points['pilot1.data_category'].answerability.unanswerable, 2);
  assert.equal(report.points['pilot1.data_category'].caseFamilies.staleEvidence, 3);
  assert.equal(report.points['pilot1.display_folder'].caseFamilies.schemaDrift, 0);
});

// ------------------------------------------------------------------ 8. oracles

test('a recorded oracle miss is an error that names the key, never a fallback answer', () => {
  const record = makeRecord();
  const empty = createRecordedOracle([]);
  assert.throws(() => empty.answer(record), (err) => {
    assert.ok(err instanceof OracleMissError);
    assert.equal(err.key, oracleKey(record));
    assert.match(err.message, new RegExp(oracleKey(record)));
    assert.match(err.message, /A miss is an error/);
    return true;
  });
  const stocked = createRecordedOracle([
    { oracleKey: oracleKey(record), response: record.response.raw },
  ]);
  assert.deepEqual(stocked.answer(record), record.response.raw);
  // A shuffled offer is a DIFFERENT question, so it misses and says so. Reusing one recording
  // across orders would look like an order-bias test while testing nothing: a real order-bias
  // test needs an answer recorded for each order that was offered.
  const shuffled = makeRecord({
    recordId: 'shuffled',
    candidates: { set: ['none', 'Finance', 'Sales'], offeredOrder: ['Finance', 'Sales', 'none'] },
  });
  assert.throws(() => stocked.answer(shuffled), OracleMissError,
    'a recording made for one option order must not be handed back for another');
});

test('a default run constructs no live oracle and makes no network call', () => {
  const calls = [];
  const realFetch = globalThis.fetch;
  globalThis.fetch = (...args) => {
    calls.push(args);
    throw new Error('the default path must not call out');
  };
  try {
    const result = importPilot1();
    assert.equal(result.counts.run1, 413);
    const records = readRecords(
      readFileSync(join(HERE, '.work', 'pilot1', 'records.jsonl'), 'utf8'));
    const entries = readFileSync(join(HERE, '.work', 'pilot1', 'oracle.jsonl'), 'utf8')
      .split('\n').filter(Boolean).map((line) => JSON.parse(line));
    const oracle = createRecordedOracle(entries);
    const manifest = loadManifest(MANIFEST_PATH);
    const report = scoreRun({
      records, oracle, manifest, mode: 'development', splitPolicy: manifest.splitPolicy,
    });
    assert.equal(oracle.kind, 'recorded');
    assert.equal(report.oracle.kind, 'recorded');
    assert.equal(oracle.hits, records.length);
  } finally {
    globalThis.fetch = realFetch;
  }
  assert.deepEqual(calls, [], 'the default path made a network call');
});

test('nothing on the default path imports the live oracle or opens a socket', () => {
  const files = [];
  const collect = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === '.work' || entry.name === 'node_modules') continue;
      const full = join(dir, entry.name);
      if (entry.isDirectory()) collect(full);
      else if (entry.name.endsWith('.mjs')) files.push(full);
    }
  };
  collect(HERE);
  assert.ok(files.length >= 10, 'expected to find the harness sources');

  const liveImport = /from\s+['"][^'"]*oracle-live/;
  const network = /node:(https?|net|tls|dgram)\b|\bfetch\s*\(|XMLHttpRequest|WebSocket/;
  let sawLiveFile = false;
  for (const file of files) {
    const source = readFileSync(file, 'utf8');
    if (file.endsWith('oracle-live.mjs')) {
      sawLiveFile = true;
      // The live oracle is allowed to call out. Its guards are checked below by source, because
      // importing it here would make it reachable from the self-test, which it must not be.
      assert.match(source, /JEV_REPLAY_LIVE !== '1'/);
      assert.match(source, /JEV_REPLAY_KEY_FILE/);
      assert.match(source, /because it is inside the repository/);
      continue;
    }
    assert.doesNotMatch(source, liveImport, file + ' imports the live oracle');
    if (file.endsWith('replay.test.mjs')) continue;
    assert.doesNotMatch(source, network, file + ' reaches the network');
  }
  assert.ok(sawLiveFile, 'oracle-live.mjs should exist and be checked by source');
});

test('the harness carries no secret and reads no key of its own', () => {
  const files = readdirSync(join(HERE, 'lib')).map((n) => join(HERE, 'lib', n))
    .concat([join(HERE, 'replay.mjs'), join(HERE, 'import-pilot1.mjs'),
      join(HERE, 'manifest.json'), join(HERE, 'README.md')]);
  const secretish = /(sk-[A-Za-z0-9]{12,}|Bearer\s+[A-Za-z0-9._-]{16,})/;
  for (const file of files) {
    if (!existsSync(file)) continue;
    const source = readFileSync(file, 'utf8');
    assert.doesNotMatch(source, secretish, file + ' looks like it carries a secret');
  }
});

// ------------------------------------------------------------------ 9. pilot one reproduction

test('pilot one reproduces from the imported records alone', () => {
  const verdict = JSON.parse(readFileSync(VERDICT_PATH, 'utf8'));
  const records = readRecords(
    readFileSync(join(HERE, '.work', 'pilot1', 'records.jsonl'), 'utf8'));
  const run2 = readRecords(
    readFileSync(join(HERE, '.work', 'pilot1', 'records-run2.jsonl'), 'utf8'));

  const rows = flatten(records);
  assert.equal(rows.length, 413);
  assert.equal(rows.length, verdict.answers.length);

  let worst = 0;
  const near = (a, b, where) => {
    if (typeof a === 'number' && typeof b === 'number') {
      const delta = Math.abs(a - b);
      if (delta > worst) worst = delta;
      assert.ok(delta <= 1e-12, where + ': ' + a + ' vs ' + b);
    } else {
      assert.deepEqual(a, b, where);
    }
  };

  rows.forEach((row, i) => {
    const expected = verdict.answers[i];
    for (const key of Object.keys(expected)) near(row[key], expected[key], 'answers[' + i + '].' + key);
    assert.deepEqual(Object.keys(row).sort(), Object.keys(expected).sort());
  });

  const sections = sectionsOf(rows);
  assert.equal(sections.length, 14);
  assert.equal(sections.length, verdict.sections.length);
  sections.forEach((section, i) => {
    const expected = verdict.sections[i];
    assert.equal(section.name, expected.name);
    assert.equal(section.n, expected.n);
    near(section.accuracy, expected.accuracy, section.name + '.accuracy');
    near(section.baseline_accuracy, expected.baseline_accuracy, section.name + '.baseline');
    near(section.mean_confidence, expected.mean_confidence, section.name + '.mean_confidence');
    for (const band of Object.keys(expected.bands)) {
      assert.equal(section.bands[band].n, expected.bands[band].n);
      near(section.bands[band].share, expected.bands[band].share, section.name + ' ' + band);
      near(section.bands[band].accuracy, expected.bands[band].accuracy, section.name + ' ' + band);
    }
  });

  const pooled = pooledCalibration(rows);
  for (const band of Object.keys(verdict.pooled_calibration)) {
    assert.equal(pooled[band].n, verdict.pooled_calibration[band].n);
    near(pooled[band].share, verdict.pooled_calibration[band].share, 'pooled ' + band);
    near(pooled[band].accuracy, verdict.pooled_calibration[band].accuracy, 'pooled ' + band);
  }

  const stab = stability(records, run2);
  assert.deepEqual(Object.keys(stab).sort(),
    Object.keys(verdict.stability_run1_vs_run2).sort());
  for (const q of Object.keys(verdict.stability_run1_vs_run2)) {
    assert.equal(stab[q].n, verdict.stability_run1_vs_run2[q].n);
    assert.equal(stab[q].same_answer, verdict.stability_run1_vs_run2[q].same_answer);
    near(stab[q].rate, verdict.stability_run1_vs_run2[q].rate, 'stability ' + q);
  }

  assert.ok(worst <= 1e-12, 'worst delta ' + worst);
});

test('the pilot flatten keeps score.py behaviour that is easy to get wrong', () => {
  assert.equal(pyStr(true), 'True');
  assert.equal(pyStr(false), 'False');
  assert.equal(pyStr(null), 'None');
  assert.equal(pyRound2(0.625), 0.62, 'Python rounds half to even');
  assert.equal(pyRound2(0.375), 0.38);
  assert.equal(pyRound2(0.3799999999999999), 0.38);
  // The band edges are inclusive at both ends of the middle band.
  const rows = flatten(readRecords(
    readFileSync(join(HERE, '.work', 'pilot1', 'records.jsonl'), 'utf8')));
  const noul = rows.find((r) => r.raw_noul !== null);
  assert.equal(noul.conf, Math.abs(noul.raw_noul - 0.5) * 2);
  assert.ok(['True', 'False'].includes(noul.pred));
});

test('buildRecords refuses to build a point that has no committed bar', () => {
  const manifest = loadManifest(MANIFEST_PATH);
  const stripped = JSON.parse(JSON.stringify(manifest));
  delete stripped.points['pilot1.hidden'];
  const verdict = JSON.parse(readFileSync(VERDICT_PATH, 'utf8'));
  assert.throws(() => buildRecords({
    verdict,
    run: 1,
    fixtureHash: 'x'.repeat(64),
    promptVersion: 'test',
    manifest: stripped,
    splitPolicy: manifest.splitPolicy,
  }), (err) => {
    assert.ok(err instanceof ManifestError);
    assert.match(err.message, /pilot1\.hidden/);
    return true;
  });
});

// ------------------------------------------------------------------ 10. the command line refuses

// Astra B9: the refusals in the contract table were reachable from the library and not from the
// command a person or CI actually runs. These tests drive replay.mjs itself, with bad files on
// disk, and check the exit code and the reason. A refusal that only a unit test can trigger is
// not a refusal.

const SEALED_MANIFEST = join(WORK, 'sealed-manifest.json');

function sealedManifestPath() {
  const manifest = JSON.parse(readFileSync(MANIFEST_PATH, 'utf8'));
  manifest.status = 'sealed';
  manifest.note = 'a self-test copy, sealed so the gate path can be exercised';
  manifest.manifestDigest = computeManifestDigest(manifest);
  writeFileSync(SEALED_MANIFEST, JSON.stringify(manifest, null, 2) + '\n');
  return SEALED_MANIFEST;
}

/** Writes a fixture directory replay.mjs can load: records plus a matching recorded oracle. */
function writeFixture(name, records) {
  const dir = join(HERE, '.work', name);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'records.jsonl'),
    records.map((r) => JSON.stringify(r)).join('\n') + '\n');
  writeFileSync(join(dir, 'oracle.jsonl'),
    records.map((r) => JSON.stringify({ oracleKey: oracleKey(r), response: r.response.raw }))
      .join('\n') + '\n');
  return dir;
}

function runCli(args) {
  const result = spawnSync(process.execPath, [join(HERE, 'replay.mjs'), ...args], {
    encoding: 'utf8',
    env: { ...process.env, JEV_REPLAY_LIVE: '' },
  });
  return { status: result.status, out: (result.stdout ?? '') + (result.stderr ?? '') };
}

/** A record that is genuinely held out: the model is one the manifest reserves for the gate. */
function heldOutRecord(overrides = {}) {
  return makeRecord({
    recordId: 'heldout:1',
    fixture: { id: 'unit-heldout', hash: 'f'.repeat(64), role: 'held-out' },
    split: { membership: 'held-out', modelId: 'northstar', family: 'display_folder' },
    model: { id: 'northstar', hash: 'm'.repeat(64) },
    ...overrides,
  });
}

test('the score command refuses a record missing a required field, and names it', () => {
  const broken = makeRecord();
  delete broken.seed;
  writeFixture('cli-missing-field', [broken]);
  const run = runCli(['score', '--fixture', 'cli-missing-field']);
  assert.notEqual(run.status, 0, 'a record missing a field must not score');
  assert.match(run.out, /missing the required field "seed"/);
});

test('the score command refuses a label the producing system wrote', () => {
  const selfLabelled = makeRecord({
    label: { value: 'Sales', author: 'jev', evidence: { note: 'its own output' } },
  });
  writeFixture('cli-self-label', [selfLabelled]);
  const run = runCli(['score', '--fixture', 'cli-self-label']);
  assert.notEqual(run.status, 0, 'a self-authored label must not score');
  assert.match(run.out, /names the producing system/);
});

test('a gate run over a development fixture is refused on the command line', () => {
  writeFixture('cli-development', [makeRecord()]);
  const run = runCli(['score', '--fixture', 'cli-development', '--mode', 'gate',
    '--manifest', sealedManifestPath()]);
  assert.notEqual(run.status, 0, 'a gate run on development evidence must not score');
  assert.match(run.out, /role "development"/);
});

test('a gate run against bars that are only proposed is refused', () => {
  writeFixture('cli-heldout', [heldOutRecord()]);
  const run = runCli(['score', '--fixture', 'cli-heldout', '--mode', 'gate']);
  assert.notEqual(run.status, 0, 'proposed bars are not a gate');
  assert.match(run.out, /"proposed"/);
  assert.match(run.out, /ratified/);
});

test('a gate run against sealed bars over held-out evidence is allowed', () => {
  // Without this the refusal above would prove nothing: a gate that always refuses is not a gate.
  writeFixture('cli-heldout', [heldOutRecord()]);
  const run = runCli(['score', '--fixture', 'cli-heldout', '--mode', 'gate',
    '--manifest', sealedManifestPath()]);
  assert.equal(run.status, 0, run.out);
  assert.match(run.out, /gate mode/);
});

test('a record from a held-out model in the tuning split is refused', () => {
  const leaked = heldOutRecord({
    recordId: 'leaked:1',
    fixture: { id: 'unit-leak', hash: 'f'.repeat(64), role: 'development' },
    split: { membership: 'development', modelId: 'northstar', family: 'display_folder' },
  });
  writeFixture('cli-leaked-split', [leaked]);
  const run = runCli(['score', '--fixture', 'cli-leaked-split']);
  assert.notEqual(run.status, 0, 'a held-out model must not be scored in the tuning half');
  assert.match(run.out, /northstar/);
  assert.match(run.out, /held-out/);
});

test('an unknown mode is refused rather than quietly read as development', () => {
  writeFixture('cli-development', [makeRecord()]);
  const run = runCli(['score', '--fixture', 'cli-development', '--mode', 'Gate']);
  assert.notEqual(run.status, 0, 'a mistyped mode must not downgrade a gate run');
  assert.match(run.out, /mode/);
});

test('development mode says out loud that proposed bars are not a gate', () => {
  const run = runCli(['score', '--fixture', 'pilot1']);
  assert.equal(run.status, 0, run.out);
  assert.match(run.out, /proposed/);
  assert.match(run.out, /not a ratified gate/);
});

test('the scorer validates every record it is handed, not just the ones the writer wrote', () => {
  const broken = makeRecord();
  delete broken.label.evidence;
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(makeRecord()), response: makeRecord().response.raw },
  ]);
  const manifest = loadManifest(MANIFEST_PATH);
  assert.throws(
    () => scoreRun({ records: [broken], oracle, manifest, mode: 'development' }),
    (err) => {
      assert.ok(err instanceof RecordSchemaError);
      assert.equal(err.field, 'label.evidence');
      return true;
    });
});

test('a gate run needs the declared split policy', () => {
  const record = heldOutRecord();
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(record), response: record.response.raw },
  ]);
  const manifest = JSON.parse(readFileSync(sealedManifestPath(), 'utf8'));
  assert.throws(() => scoreRun({ records: [record], oracle, manifest, mode: 'gate' }),
    /split policy/);
  assert.ok(scoreRun({
    records: [record], oracle, manifest, mode: 'gate', splitPolicy: manifest.splitPolicy,
  }));
});

// ------------------------------------------------------------------ 11. same rules as the engine

// Astra B10: the replay and the engine have to evaluate the same answers under the same evidence.
// The table below is COPIED from Semanticus.Core/Decisions/DecisionAnswerReader.cs, with the line
// each case comes from. The engine cannot be run from here (this harness is node only, by design),
// so the guard against drift is two-sided: this table pins the behaviour, and the test after it
// re-reads those exact lines of the C# and fails if any of them has moved or changed.

const ENGINE_READER_CS = resolve(HERE, '..', '..', 'Semanticus.Core', 'Decisions',
  'DecisionAnswerReader.cs');
const ENGINE_DIGEST_CS = resolve(HERE, '..', '..', 'Semanticus.Core', 'Decisions',
  'DecisionDigest.cs');

const CHOICE = { questionType: 'choice', candidates: ['Finance', 'Sales', 'none'] };
const NOUL = { questionType: 'noul', candidates: ['no', 'yes'] };

const ENGINE_CASES = [
  // DecisionAnswerReader.cs:93 an answer that is not an object is rejected
  { cite: 'reader:93', ask: CHOICE, response: 'Sales', reject: /not an object/ },
  // :98 no 'type' string
  { cite: 'reader:98', ask: CHOICE, response: { choice: 'Sales', confidence: 0.9 },
    reject: /named no shape/ },
  // :105 the shape that came back is not the shape that was asked
  { cite: 'reader:105', ask: CHOICE, response: { type: 'noul', noul: 0.9 },
    reject: /came back as 'noul'/ },
  // :120 a choice answer with no 'choice'
  { cite: 'reader:120', ask: CHOICE, response: { type: 'choice', confidence: 0.9 },
    reject: /carried no 'choice'/ },
  // :147 'choice' was not text
  { cite: 'reader:147', ask: CHOICE, response: { type: 'choice', choice: 3, confidence: 0.9 },
    reject: /was not text/ },
  // :153 a choice that was never offered
  { cite: 'reader:153', ask: CHOICE,
    response: { type: 'choice', choice: 'Marketing', confidence: 0.9 },
    reject: /not one of the options that were sent/ },
  // :222 'probabilities' was not an object
  { cite: 'reader:222', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 0.9, probabilities: [0.9] },
    reject: /'probabilities' was not an object/ },
  // :230 'probabilities' names an option that was not sent
  { cite: 'reader:230', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 0.9, probabilities: { Other: 1 } },
    reject: /names 'Other'/ },
  // :235 a probability that is not a number
  { cite: 'reader:235', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 0.9, probabilities: { Sales: 'high' } },
    reject: /was not a number/ },
  // :246 a probability outside 0 to 1
  { cite: 'reader:246', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 0.9, probabilities: { Sales: 1.2 } },
    reject: /outside 0 to 1/ },
  // :295 'confidence' was not a number
  { cite: 'reader:295', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 'high' },
    reject: /'confidence' was not a number/ },
  // :299 'confidence' outside 0 to 1
  { cite: 'reader:299', ask: CHOICE, response: { type: 'choice', choice: 'Sales', confidence: 1.5 },
    reject: /'confidence' is outside 0 to 1/ },
  // :308 no confidence field falls back to the probability of the option it picked
  { cite: 'reader:308', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', probabilities: { Sales: 0.74, Finance: 0.26 } },
    value: 'Sales', confidence: 0.74 },
  // :310 neither a confidence nor a probability for the option it picked
  { cite: 'reader:310', ask: CHOICE, response: { type: 'choice', choice: 'Sales' },
    reject: /neither a confidence nor a probability/ },
  // :293 an explicit confidence is taken as sent, even when probabilities disagree
  { cite: 'reader:293', ask: CHOICE,
    response: { type: 'choice', choice: 'Sales', confidence: 0.62, probabilities: { Sales: 0.67 } },
    value: 'Sales', confidence: 0.62 },
  // :187 a noul answer with no 'noul' number
  { cite: 'reader:187', ask: NOUL, response: { type: 'noul' }, reject: /carried no 'noul' number/ },
  // :194 a noul probability outside 0 to 1
  { cite: 'reader:194', ask: NOUL, response: { type: 'noul', noul: 1.4 },
    reject: /outside 0 to 1/ },
  // :201 confidence is how far the one probability sits from an even split
  { cite: 'reader:201', ask: NOUL, response: { type: 'noul', noul: 0.9 },
    value: true, confidence: 0.9 },
  { cite: 'reader:201', ask: NOUL, response: { type: 'noul', noul: 0.02 },
    value: false, confidence: 0.98 },
  { cite: 'reader:201', ask: NOUL, response: { type: 'noul', noul: 0.5 },
    value: true, confidence: 0.5 },
  // :202 yes is p at or above an even split
  { cite: 'reader:202', ask: NOUL, response: { type: 'noul', noul: 0.52 },
    value: true, confidence: 0.52 },
  { cite: 'reader:202', ask: NOUL, response: { type: 'noul', noul: 0.49 },
    value: false, confidence: 0.51 },
];

test('the harness reads an answer exactly as the engine does', () => {
  for (const c of ENGINE_CASES) {
    const where = c.cite + ' ' + JSON.stringify(c.response);
    if (c.reject) {
      assert.throws(() => readAnswer(c.ask, c.response), (err) => {
        assert.ok(err instanceof AnswerRejected, where + ' should be refused');
        assert.match(err.message, c.reject, where);
        return true;
      }, where + ' should be refused');
      continue;
    }
    const read = readAnswer(c.ask, c.response);
    assert.equal(read.value, c.value, where + ' value');
    assert.ok(Math.abs(read.confidence - c.confidence) < 1e-12,
      where + ' confidence: expected ' + c.confidence + ', got ' + read.confidence);
  }
});

test('the engine rules this harness copied are still the engine rules', () => {
  // If one of these fails the engine moved and this harness has drifted. Re-read the C#, re-copy
  // the rule into the table above, and re-cite the line. Do not just update the line number.
  const cited = (path, lines) => {
    const source = readFileSync(path, 'utf8').split('\n');
    for (const [number, text] of Object.entries(lines)) {
      assert.equal(source[Number(number) - 1].trim(), text,
        path + ':' + number + ' no longer reads as the harness copied it');
    }
  };
  cited(ENGINE_READER_CS, {
    93: `if (value.ValueKind != JsonValueKind.Object) { why = "it was not an object"; return null; }`,
    96: `if (!value.TryGetProperty("type", out typeElement) || typeElement.ValueKind != JsonValueKind.String)`,
    103: `if (!string.Equals(type, question.WireType, StringComparison.Ordinal))`,
    120: `if (!value.TryGetProperty(field, out picked)) { why = $"it carried no '{field}'"; return null; }`,
    151: `if (!question.HasOption(id))`,
    185: `if (!value.TryGetProperty("noul", out noul) || noul.ValueKind != JsonValueKind.Number)`,
    // Re-copied, not just re-cited: the engine now reads every number through TryNumber, which
    // refuses one no double can hold (1e400 used to throw FormatException). This harness reads
    // recorded JSON in JavaScript, where such a number parses to Infinity and the same 0 to 1
    // range check below refuses it, so the two still agree on the outcome.
    192: `if (!TryNumber(noul, out p) || double.IsNaN(p) || p < 0 || p > 1)`,
    201: `var confidence = Math.Max(p, 1 - p);`,
    202: `var yes = p >= 0.5;`,
    222: `if (probabilities.ValueKind != JsonValueKind.Object) { why = "'probabilities' was not an object"; return result; }`,
    228: `if (!question.HasOption(option))`,
    233: `if (p.Value.ValueKind != JsonValueKind.Number)`,
    239: `if (!TryNumber(p.Value, out v))`,
    244: `if (double.IsNaN(v) || v < 0 || v > 1)`,
    295: `if (c.ValueKind != JsonValueKind.Number) { why = "'confidence' was not a number"; return false; }`,
    296: `if (!TryNumber(c, out confidence)) { why = "'confidence' was not a number this reader can hold"; return false; }`,
    297: `if (double.IsNaN(confidence) || confidence < 0 || confidence > 1)`,
    308: `if (probabilities.TryGetValue(picked, out p)) { confidence = p; return true; }`,
  });
  cited(ENGINE_DIGEST_CS, {
    49: `for (var j = 0; j < q.Options.Count; j++)`,
  });
});

test('an answer the engine would refuse is counted as not decided, never as correct', () => {
  // The engine makes the whole ask "not decided" and the caller takes the fallback. The scorer's
  // honest reading of that is a row that answered nothing: not served, not correct, and counted.
  const bad = makeRecord({
    recordId: 'bad-answer',
    response: { raw: { type: 'choice', choice: 'Marketing', confidence: 0.99 } },
  });
  const oracle = createRecordedOracle([
    { oracleKey: oracleKey(bad), response: bad.response.raw },
  ]);
  const manifest = loadManifest(MANIFEST_PATH);
  const report = scoreRun({ records: [bad], oracle, manifest, mode: 'development' });
  const point = report.points['pilot1.display_folder'];
  assert.equal(point.notDecided, 1, 'a refused answer must be counted');
  assert.equal(point.coverage, 0, 'a refused answer is never served');
  assert.equal(point.accuracy, 0, 'a refused answer is never correct');
  assert.equal(point.falsePositives.count, 0);
});
