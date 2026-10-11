// The replay record: one JSON object per decision-point evaluation, written as JSONL.
//
// The field list is Astra's, verbatim in intent: fixture / model / source hashes, split
// membership, the exact candidate list and the order offered, the task and the knowable context,
// the full allowed request and the raw typed response, point / schema / prompt / served-model
// versions, seed, thresholds, fallback, the baseline's version and output, the label with its
// author and evidence, errors, retries, cache hits, tokens, pricing basis and elapsed time, and
// for sequence points the selected action, the executed action, the engine result, the accepted
// diff and the final outcome.
//
// The writer VALIDATES and REFUSES. A record missing a required field is never written, and the
// refusal names the field. A half-populated record is worse than no record: it scores, it looks
// like evidence, and nobody can tell later what was not captured.
//
// The READER validates too, naming the line. Validating only on the way out protected records
// this harness wrote and nothing else, and a record file can be edited by hand, carried over
// from another branch, or left half-written by a crashed run. The scorer validates again on its
// own entry, because it can be handed records that never went through this file at all.

import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

export const SCHEMA_VERSION = '1.0.0';

/** Every field that must be present on every record. Dotted paths. */
export const REQUIRED_PATHS = [
  'schemaVersion',
  'recordId',
  // fixture, model and source hashes plus split membership
  'fixture.id',
  'fixture.hash',
  'fixture.role',
  'split.membership',
  'split.modelId',
  'split.family',
  'model.id',
  'model.hash',
  'source.hash',
  // point, schema, prompt and served-model versions
  'point.id',
  'point.version',
  'point.kind',
  'prompt.version',
  'servedModel.id',
  'servedModel.version',
  'producer.system',
  // the task and the knowable context
  'task.kind',
  'task.instructions',
  'context.knowable',
  // the exact candidate list and the order offered
  'candidates.set',
  'candidates.offeredOrder',
  // the full allowed request and the raw typed response
  'request.allowed',
  // the shape that was asked for. The scorer reads a reply against it exactly as the engine does
  // (DecisionAnswerReader.cs:105), so a record that does not say what was asked cannot be scored.
  'request.allowed.questionType',
  'response.raw',
  // seed, thresholds, fallback
  'seed',
  'thresholds.confidence',
  'fallback',
  // the baseline's version and its output
  'baseline.version',
  'baseline.output',
  // the label, its author and its evidence, kept apart from the rule's answer
  'label.value',
  'label.author',
  'label.evidence',
  'ruleAnswer',
  // countable case shape
  'answerability',
  'caseFamilies',
  // cost and behaviour of the call itself
  'outcome.errors',
  'outcome.retryAttempts',
  'outcome.cacheHit',
  'outcome.inputTokens',
  'outcome.outputTokens',
  'outcome.pricingBasis',
  'outcome.elapsedMs',
];

/** Extra fields a sequence point must carry. */
export const SEQUENCE_REQUIRED_PATHS = [
  'sequence.selectedAction',
  'sequence.executedAction',
  'sequence.engineResult',
  'sequence.acceptedDiff',
  'sequence.finalOutcome',
];

/**
 * Case families the report must be able to count even when a fixture holds none of them.
 * Zero is a finding. A missing key is a gap nobody notices.
 */
export const CASE_FAMILIES = [
  'schemaDrift',
  'ambiguousNames',
  'maliciousDescriptions',
  'wrongAccount',
  'disconnectedSource',
  'staleEvidence',
];

export const ANSWERABILITY = ['answerable', 'unanswerable', 'unsupported'];

export const POINT_KINDS = ['single', 'sequence'];

export const FIXTURE_ROLES = ['development', 'held-out'];

export class RecordSchemaError extends Error {
  constructor(message, field) {
    super(message);
    this.name = 'RecordSchemaError';
    this.field = field;
  }
}

export class LabelIndependenceError extends Error {
  constructor(message) {
    super(message);
    this.name = 'LabelIndependenceError';
  }
}

function readPath(object, path) {
  let node = object;
  for (const part of path.split('.')) {
    if (node === null || typeof node !== 'object' || !(part in node)) return { found: false };
    node = node[part];
  }
  return { found: node !== undefined, value: node };
}

/**
 * Refuse a record whose label was written by the same identity that produced the answer.
 * A system that marks its own homework produces a number that means nothing. Astra: reviewers
 * label blind to the producing system.
 */
export function assertLabelIndependent(record) {
  const producer = String(record?.producer?.system ?? '').toLowerCase().trim();
  const served = String(record?.servedModel?.id ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '');
  const author = String(record?.label?.author ?? '');
  const authorTokens = author.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
  const authorFlat = author.toLowerCase().replace(/[^a-z0-9]+/g, '');
  if (producer && authorTokens.includes(producer)) {
    throw new LabelIndependenceError(
      `label.author "${author}" names the producing system "${record.producer.system}". A label `
      + 'must be written by a reviewer who is not the system being scored.');
  }
  if (served && authorFlat === served) {
    throw new LabelIndependenceError(
      `label.author "${author}" is the served model "${record.servedModel.id}". A label must be `
      + 'written by a reviewer who is not the system being scored.');
  }
  return true;
}

/** Throws RecordSchemaError naming the first missing or malformed field. */
export function validateRecord(record) {
  if (record === null || typeof record !== 'object' || Array.isArray(record)) {
    throw new RecordSchemaError('a replay record must be a JSON object', null);
  }
  const required = record.point?.kind === 'sequence'
    ? [...REQUIRED_PATHS, ...SEQUENCE_REQUIRED_PATHS]
    : REQUIRED_PATHS;
  for (const path of required) {
    const hit = readPath(record, path);
    if (!hit.found) {
      throw new RecordSchemaError(`replay record is missing the required field "${path}"`, path);
    }
  }
  if (record.schemaVersion !== SCHEMA_VERSION) {
    throw new RecordSchemaError(
      `replay record has schemaVersion "${record.schemaVersion}", this harness writes `
      + `"${SCHEMA_VERSION}"`, 'schemaVersion');
  }
  if (!FIXTURE_ROLES.includes(record.fixture.role)) {
    throw new RecordSchemaError(
      `fixture.role must be one of ${FIXTURE_ROLES.join(', ')}, got "${record.fixture.role}"`,
      'fixture.role');
  }
  if (!POINT_KINDS.includes(record.point.kind)) {
    throw new RecordSchemaError(
      `point.kind must be one of ${POINT_KINDS.join(', ')}, got "${record.point.kind}"`,
      'point.kind');
  }
  if (!ANSWERABILITY.includes(record.answerability)) {
    throw new RecordSchemaError(
      `answerability must be one of ${ANSWERABILITY.join(', ')}, got "${record.answerability}"`,
      'answerability');
  }
  if (!Array.isArray(record.candidates.set)) {
    throw new RecordSchemaError('candidates.set must be an array', 'candidates.set');
  }
  if (!Array.isArray(record.candidates.offeredOrder)) {
    throw new RecordSchemaError(
      'candidates.offeredOrder must be an array', 'candidates.offeredOrder');
  }
  // The offered order is in the oracle key now, so it has to be the same options as the set.
  // Two lists that can disagree would put one list in the key and show a reviewer the other.
  const setKey = JSON.stringify([...new Set(record.candidates.set.map(String))].sort());
  const offeredKey = JSON.stringify(
    [...new Set(record.candidates.offeredOrder.map(String))].sort());
  if (setKey !== offeredKey) {
    throw new RecordSchemaError(
      'candidates.offeredOrder must hold the same options as candidates.set, in the order they '
      + 'were offered. The set is [' + record.candidates.set.join(', ') + '] and the order is ['
      + record.candidates.offeredOrder.join(', ') + '].', 'candidates.offeredOrder');
  }
  if (!Array.isArray(record.outcome.errors)) {
    throw new RecordSchemaError('outcome.errors must be an array', 'outcome.errors');
  }
  if (record.caseFamilies === null || typeof record.caseFamilies !== 'object'
      || Array.isArray(record.caseFamilies)) {
    throw new RecordSchemaError(
      'caseFamilies must be an object carrying every known family', 'caseFamilies');
  }
  for (const family of CASE_FAMILIES) {
    if (!(family in record.caseFamilies)) {
      throw new RecordSchemaError(
        `caseFamilies is missing the family "${family}". Every family is carried on every record, `
        + 'set to false when it does not apply, so a fixture with none of a family is countable.',
        `caseFamilies.${family}`);
    }
  }
  assertLabelIndependent(record);
  return true;
}

/** Appends validated records to a JSONL file. Refuses before it writes, never after. */
export class RecordWriter {
  constructor(path) {
    this.path = path;
    this.count = 0;
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, '');
  }

  write(record) {
    validateRecord(record);
    appendFileSync(this.path, `${JSON.stringify(record)}\n`);
    this.count += 1;
    return this.count;
  }

  writeAll(records) {
    for (const record of records) this.write(record);
    return this.count;
  }
}

/**
 * Reads a JSONL record file and VALIDATES every record in it, naming the line that failed.
 *
 * Reading used to parse and hand back whatever was on disk, which meant the writer's refusals
 * only ever protected records this harness wrote itself. A record file can be edited, copied
 * from another branch or half-written by a crashed run, so the same refusals run on the way in.
 * Pass { validate: false } only to inspect a file you already know is broken.
 */
export function readRecords(text, { validate = true } = {}) {
  const lines = text.split('\n');
  const records = [];
  for (let i = 0; i < lines.length; i += 1) {
    if (lines[i].trim() === '') continue;
    let record;
    try {
      record = JSON.parse(lines[i]);
    } catch (err) {
      throw new RecordSchemaError(
        `line ${i + 1} of the record file is not JSON: ${err.message}`, null);
    }
    if (validate) {
      try {
        validateRecord(record);
      } catch (err) {
        err.message = `line ${i + 1} of the record file was refused: ${err.message}`;
        throw err;
      }
    }
    records.push(record);
  }
  return records;
}

/** Python's str() for the handful of types the pilot scorer stringifies. */
export function pyStr(value) {
  if (value === true) return 'True';
  if (value === false) return 'False';
  if (value === null || value === undefined) return 'None';
  return String(value);
}
