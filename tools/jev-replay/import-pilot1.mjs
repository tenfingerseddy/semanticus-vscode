// Imports pilot one into the harness fixture format.
//
// Reads docs/redesign/jev-pilot/verdict.json, which is the fixture of record, and writes into
// tools/jev-replay/.work/pilot1/:
//
//   records.jsonl       one harness record per scored answer from run one
//   records-run2.jsonl  the same for run two, needed only for stability_run1_vs_run2
//   oracle.jsonl        the recorded answers from run one, keyed on the oracle key
//   labels.jsonl        the labels, their author and their evidence, on their own
//
// Everything here is marked role "development" and can never be the held-out gate. Astra: keep
// all four pilots as development or regression fixtures. The public models the pilot used were
// already read while the questions were designed, so a number from them is a regression number,
// not evidence of generalisation.
//
// Two honest limits, both written onto every record rather than papered over:
//
//   1. verdict.json records the answers, not the request payload. calls.json, which held the
//      state sent to the service, is not committed. So request.allowed carries the request SHAPE
//      (the exact instruction text and criteria from prep.py, and the names of the state fields
//      that were sent) and says plainly that the state values are not included.
//   2. The .bim source models are not committed either, so source hashes are recorded as an
//      explicit "none:" sentinel with a reason, never as a digest of something else.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalCandidateSet, canonicalJson, oracleKey, offerDigest, sha256Hex } from './lib/digest.mjs';
import { offerOrder } from './lib/offer.mjs';
import { CASE_FAMILIES, RecordWriter, SCHEMA_VERSION } from './lib/record.mjs';
import { assignSplit } from './lib/split.mjs';
import { barFor, loadManifest } from './lib/manifest.mjs';

const HERE = resolve(fileURLToPath(new URL('.', import.meta.url)));
export const REPO_ROOT = resolve(HERE, '..', '..');
export const PILOT_DIR = join(REPO_ROOT, 'docs', 'redesign', 'jev-pilot');
export const VERDICT_PATH = join(PILOT_DIR, 'verdict.json');
export const PREP_PATH = join(PILOT_DIR, 'prep.py');
export const WORK_DIR = join(HERE, '.work', 'pilot1');
export const MANIFEST_PATH = join(HERE, 'manifest.json');

export const POINT_VERSION = '1.0.0';
export const CLARITY_QID = 'one_side_clarity';

// The instruction text, copied from prep.py. It is the ask, and it is part of the oracle key.
export const INSTRUCTIONS = {
  display_folder: 'Which display folder is this measure filed in?',
  format_family: 'How is the result of this measure displayed to a report reader?',
  data_category: 'What kind of real-world thing do the values in this column hold?',
  hidden: 'This column should be hidden from people building reports.',
  summarize_by:
    'When a report author drops this column onto a chart, how are its values combined by default?',
  sort_by_column:
    'When this column is placed on a chart axis, which column decides the order its values '
    + 'appear in?',
  date_table: "This table is the model's calendar or date table.",
  one_side:
    'In this relationship, which side is the one side, where the joined column holds one row '
    + 'per value?',
};

// The fixed criteria sets from prep.py. Exact.
export const FORMAT_CRITERIA = {
  currency: 'the result is displayed with a currency symbol',
  percent: 'the result is displayed as a percentage with a percent sign',
  whole_number: 'the result is displayed as a number with no decimal places',
  decimal: 'the result is displayed as a number with one or more decimal places',
  date_or_time: 'the result is displayed as a date or a time',
  text_or_none: 'the result is text rather than a number',
};

export const DATACAT_CRITERIA = {
  none: 'the values are none of the kinds listed below',
  Country: 'the column holds country or region names',
  StateOrProvince: 'the column holds state, province or region-within-country names',
  City: 'the column holds city or town names',
  PostalCode: 'the column holds postal codes or zip codes',
  Address: 'the column holds street addresses',
  Latitude: 'the column holds latitude coordinates',
  Longitude: 'the column holds longitude coordinates',
  ImageUrl: 'the column holds web addresses that point at image files',
  WebUrl: 'the column holds web addresses that point at web pages',
  Barcode: 'the column holds barcode values',
};

export const SUMMARIZE_CRITERIA = {
  sum: 'the values are added together',
  none: 'the values are listed as they are and are not aggregated',
  count: 'the number of rows is counted',
  average: 'the mean of the values is taken',
  min: 'the smallest value is taken',
  max: 'the largest value is taken',
};

export const HIDDEN_CRITERIA = {
  yes: 'the column does not belong in the field list a report author browses',
  no: 'the column belongs in the field list a report author browses',
};

export const DATE_TABLE_CRITERIA = {
  yes: "the table is the one marked as the model's date table",
  no: "the table is not marked as the model's date table",
};

// The state field names prep.py sent, by object kind. The values are not committed.
export const STATE_FIELDS = {
  measure: ['model_tables', 'measure', 'columns_in_that_table', 'measure_names_in_that_table'],
  column: ['model_tables', 'column', 'other_column_names_in_the_same_table',
    'this_column_is_used_in_a_relationship', 'measure_count_in_the_same_table'],
  table: ['model_tables', 'table', 'relationships'],
  relationship: ['model_tables', 'sides', 'note'],
};

// score.py's own exclusion sets, carried onto the records as countable case shape.
const BAD_DC = new Set([
  'Date[Calendar Year Quarter Number]',
  'Date[Fiscal Year Quarter Number]',
  'Customer[Customer Code]',
]);
const UNANSWERABLE_DC = new Set(['Customer[Continent]', 'Store[Continent]']);

// The author must not name the producing system. The first draft of this string carried the
// path docs/redesign/jev-pilot/prep.py, and the label-independence guard refused the whole
// import because that path contains the token "jev". The path lives in evidence.readBy instead,
// where it belongs, and the author names the labelling process only.
const LABEL_AUTHOR = 'stored-model-metadata:pilot-one-prep-step';

function labelEvidence(qid, object) {
  return {
    source: 'the property stored on the object in the published .bim file',
    readBy: 'docs/redesign/jev-pilot/prep.py',
    question: qid,
    object,
    independentHumanReview: false,
    caution:
      'These labels were not reviewed blind by a person. score.py itself names three of them as '
      + 'plainly wrong and two more as unanswerable, which is exactly why rule agreement is '
      + 'stored apart from correctness. A held-out gate needs reviewed labels, not these.',
  };
}

/** The criteria object for one question. Objects, never arrays, so no order reaches the key. */
export function criteriaFor(qid, options) {
  if (qid === 'format_family') return { ...FORMAT_CRITERIA };
  if (qid === 'data_category') return { ...DATACAT_CRITERIA };
  if (qid === 'summarize_by') return { ...SUMMARIZE_CRITERIA };
  if (qid === 'hidden') return { ...HIDDEN_CRITERIA };
  if (qid === 'date_table') return { ...DATE_TABLE_CRITERIA };
  if (qid === 'display_folder') {
    const out = {};
    for (const option of options) {
      out[option] = option === 'none'
        ? 'no folder; the measure sits at the top level of its table'
        : 'a folder named "' + option + '" in the field list of the table';
    }
    return out;
  }
  if (qid === 'sort_by_column') {
    const out = {};
    for (const option of options) {
      out[option] = option === 'none'
        ? "the column's own values decide the order"
        : 'the column named "' + option + '" in the same table';
    }
    return out;
  }
  if (qid === 'one_side') {
    // prep.py named the table and the joined column in each criterion. verdict.json does not
    // record which table was offered as which side, and deriving it from the label would put the
    // answer into the request. So the criteria stay neutral and the gap is stated.
    return {
      side_a: 'the side offered as side_a',
      side_b: 'the side offered as side_b',
      unrecorded:
        'prep.py named each side by its table and joined column; verdict.json does not record '
        + 'which table was offered as which side, so those names are absent here',
    };
  }
  throw new Error('no criteria known for question "' + qid + '"');
}

/** The candidate set and the order the options appear in the recorded response. */
export function candidatesFor(qid, answer) {
  if (answer.probabilities) {
    const offered = Object.keys(answer.probabilities);
    return { set: canonicalCandidateSet(offered), offeredOrder: offered };
  }
  if (answer.type === 'noul') {
    // prep.py wrote the yes/no criteria in that order.
    return { set: canonicalCandidateSet(['yes', 'no']), offeredOrder: ['yes', 'no'] };
  }
  throw new Error('cannot read the candidates for question "' + qid + '"');
}

function modelHashes(verdict) {
  const out = {};
  for (const [key, facts] of Object.entries(verdict.models)) {
    out[key] = sha256Hex(canonicalJson(facts));
  }
  return out;
}

export function buildRecords({ verdict, run, fixtureHash, promptVersion, manifest, splitPolicy }) {
  const calls = run === 1 ? verdict.raw_calls_run1 : verdict.raw_calls_run2;
  const hashes = modelHashes(verdict);
  const records = [];
  for (const call of calls) {
    for (const [qid, answer] of Object.entries(call.answers ?? {})) {
      if (qid === CLARITY_QID) continue;
      const pointId = 'pilot1.' + qid;
      const bar = barFor(manifest, pointId);
      const candidates = candidatesFor(qid, answer);
      const recordId = 'pilot1:run' + run + ':' + call.id + ':' + qid;
      const seed = 'pilot1:' + recordId;
      const isBadLabel = qid === 'data_category' && BAD_DC.has(call.object);
      const isUnanswerable = qid === 'data_category' && UNANSWERABLE_DC.has(call.object);
      const caseFamilies = {};
      for (const family of CASE_FAMILIES) caseFamilies[family] = false;
      // The only family pilot one genuinely carries: three stored categories that score.py's own
      // comment calls plainly wrong. Every other family stays false so it stays countable.
      caseFamilies.staleEvidence = isBadLabel;

      const record = {
        schemaVersion: SCHEMA_VERSION,
        recordId,
        fixture: { id: 'pilot1', hash: fixtureHash, role: 'development' },
        split: {
          membership: assignSplit({ modelId: call.model, family: qid }, splitPolicy),
          modelId: call.model,
          family: qid,
        },
        model: {
          id: call.model,
          hash: hashes[call.model],
          hashBasis:
            'sha256 over the canonical JSON of this model\'s facts block in verdict.json. The '
            + '.bim file itself is not committed to this repository, so this is not a hash of '
            + 'the model bytes.',
        },
        source: {
          hash: 'none:pilot-one-read-no-data-source',
          basis:
            'Pilot one asked about model metadata only. No data source was read, so there is no '
            + 'source digest to record. This is a sentinel, not a hash.',
        },
        point: { id: pointId, version: POINT_VERSION, kind: 'single' },
        prompt: { version: promptVersion },
        servedModel: { id: call.jev_version, version: call.jev_version },
        producer: { system: 'jev', version: call.jev_version },
        task: { kind: 'typed-micro-question', instructions: INSTRUCTIONS[qid] },
        context: {
          knowable: {
            callId: call.id,
            object: call.object,
            kind: call.kind,
            modelId: call.model,
            extra: call.extra ?? {},
          },
        },
        candidates: {
          set: candidates.set,
          offeredOrder: candidates.offeredOrder,
          offeredOrderBasis:
            'the order the options appear in the recorded response. The runner sent the criteria '
            + 'as a JSON object and did not log a wire order, so this is the recorded order and '
            + 'not proof of the order sent.',
          harnessOrder: offerOrder(candidates.set, seed),
        },
        request: {
          allowed: {
            questionType: answer.type,
            instructions: INSTRUCTIONS[qid],
            criteria: criteriaFor(qid, candidates.set),
            stateFieldNames: STATE_FIELDS[call.kind],
            stateValuesIncluded: false,
            provenance:
              'reconstructed from docs/redesign/jev-pilot/prep.py. The state payload went to '
              + 'calls.json, which is not committed, so this record carries the request shape '
              + 'and not its values.',
          },
        },
        response: { raw: answer },
        seed,
        thresholds: { confidence: bar.confidenceThreshold },
        fallback: bar.fallback,
        baseline: {
          version: 'pilot1-prep.py-baseline',
          output: call.baseline?.[qid] ?? null,
        },
        label: {
          value: call.truth?.[qid] ?? null,
          author: LABEL_AUTHOR,
          evidence: labelEvidence(qid, call.object),
        },
        // What today's rule says. For pilot one the trivial rule and the baseline are the same
        // function, recorded in both places on purpose so rule agreement and baseline accuracy
        // stay separable numbers.
        ruleAnswer: call.baseline?.[qid] ?? null,
        answerability: isUnanswerable ? 'unanswerable' : 'answerable',
        caseFamilies,
        outcome: {
          errors: call.error ? [call.error] : [],
          retryAttempts: Math.max(0, (call.attempts ?? 1) - 1),
          cacheHit: false,
          inputTokens: call.usage?.input_tokens ?? 0,
          outputTokens: call.usage?.output_tokens ?? 0,
          tokensScope: 'call',
          pricingBasis: 'USD 42 per billion input tokens, as run.ts and score.py priced it',
          elapsedMs: call.ms ?? 0,
        },
        provenance: {
          run,
          callId: call.id,
          sourceFile: 'docs/redesign/jev-pilot/verdict.json',
        },
      };
      if (qid === 'one_side' && call.answers[CLARITY_QID]) {
        record.response.secondary = call.answers[CLARITY_QID];
      }
      record.digests = { oracleKey: oracleKey(record), offerDigest: offerDigest(record) };
      records.push(record);
    }
  }
  return records;
}

export function importPilot1({ verdictPath = VERDICT_PATH, outDir = WORK_DIR,
  manifestPath = MANIFEST_PATH } = {}) {
  const verdictBytes = readFileSync(verdictPath);
  const verdict = JSON.parse(verdictBytes.toString('utf8'));
  const fixtureHash = sha256Hex(verdictBytes.toString('utf8'));
  const promptVersion = 'pilot1-prep.py:'
    + sha256Hex(readFileSync(PREP_PATH, 'utf8')).slice(0, 12);
  const manifest = loadManifest(manifestPath);
  const splitPolicy = manifest.splitPolicy;

  const run1 = buildRecords({ verdict, run: 1, fixtureHash, promptVersion, manifest, splitPolicy });
  const run2 = buildRecords({ verdict, run: 2, fixtureHash, promptVersion, manifest, splitPolicy });

  mkdirSync(outDir, { recursive: true });
  const recordsPath = join(outDir, 'records.jsonl');
  const run2Path = join(outDir, 'records-run2.jsonl');
  const oraclePath = join(outDir, 'oracle.jsonl');
  const labelsPath = join(outDir, 'labels.jsonl');

  // RecordWriter validates every record and refuses one that is missing a field.
  new RecordWriter(recordsPath).writeAll(run1);
  new RecordWriter(run2Path).writeAll(run2);

  // The recorded oracle holds run one only. Run two is the same evidence asked twice, so it
  // shares every oracle key; keeping both would put two answers under one key.
  const oracleLines = run1.map((r) => JSON.stringify({
    oracleKey: r.digests.oracleKey,
    recordId: r.recordId,
    pointId: r.point.id,
    response: r.response.raw,
  }));
  writeFileSync(oraclePath, oracleLines.join('\n') + '\n');

  const labelLines = run1.map((r) => JSON.stringify({
    recordId: r.recordId,
    pointId: r.point.id,
    value: r.label.value,
    author: r.label.author,
    evidence: r.label.evidence,
    ruleAnswer: r.ruleAnswer,
  }));
  writeFileSync(labelsPath, labelLines.join('\n') + '\n');

  return {
    outDir,
    fixtureHash,
    promptVersion,
    manifestDigest: manifest.manifestDigest,
    counts: { run1: run1.length, run2: run2.length, oracle: oracleLines.length,
      labels: labelLines.length },
    paths: { recordsPath, run2Path, oraclePath, labelsPath },
  };
}

function main() {
  const result = importPilot1();
  console.log('imported pilot one into ' + result.outDir);
  console.log('  fixture hash    ' + result.fixtureHash);
  console.log('  prompt version  ' + result.promptVersion);
  console.log('  manifest digest ' + result.manifestDigest);
  console.log('  run 1 records   ' + result.counts.run1);
  console.log('  run 2 records   ' + result.counts.run2);
  console.log('  oracle answers  ' + result.counts.oracle);
  console.log('  labels          ' + result.counts.labels);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  main();
}
