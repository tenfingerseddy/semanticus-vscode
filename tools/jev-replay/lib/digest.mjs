// Canonical JSON plus the two digests the replay gate runs on.
//
// Why two digests, and why the difference is load-bearing:
//
//   ORACLE KEY  keys the recorded answer store. It is computed over the complete evidence, the
//               candidate SET in a canonical sorted order, AND the order the options were
//               actually offered in. The order is in the key because the engine puts it in its
//               own digest: Semanticus.Core/Decisions/DecisionDigest.cs:49 writes each
//               question's options in order, and that file's rule 3 says plainly that a
//               different option order is a different question. If the harness dropped the
//               order the two would evaluate different things under one name, and a recording
//               made for one order would be handed back for another.
//
//               The honest consequence, written down because it was got wrong once: replaying
//               one recording after a shuffle does not test order bias. It just misses, and the
//               miss is an error. A real order-bias test needs an answer RECORDED FOR EACH
//               ORDER that was offered, which is a recording job, not a replay trick.
//
//   OFFER DIGEST is the same evidence with only the order actually offered. It is recorded,
//               never used for lookup. It is the short number a reviewer can compare across
//               records to see which of them were shown the same layout.
//
// Never key an oracle on the question text or a question hash alone. Two different models can
// be asked the same words about different objects, and a question-hash key would hand back the
// other model's answer. Astra called this out directly, so the key here always carries the
// fixture, model and source hashes, the task, the knowable context, the full allowed request,
// the candidate set, and the point, schema, prompt and served-model versions.

import { createHash } from 'node:crypto';

export class DigestError extends Error {
  constructor(message) {
    super(message);
    this.name = 'DigestError';
  }
}

/** Deterministic JSON: object keys sorted, array order preserved, no whitespace. */
export function canonicalJson(value) {
  if (value === undefined) throw new DigestError('canonicalJson refuses undefined');
  if (value === null) return 'null';
  const t = typeof value;
  if (t === 'number') {
    if (!Number.isFinite(value)) throw new DigestError(`canonicalJson refuses ${String(value)}`);
    return JSON.stringify(value);
  }
  if (t === 'boolean' || t === 'string') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (t === 'object') {
    const keys = Object.keys(value).filter((k) => value[k] !== undefined).sort(compareStrings);
    return `{${keys.map((k) => `${JSON.stringify(k)}:${canonicalJson(value[k])}`).join(',')}}`;
  }
  throw new DigestError(`canonicalJson refuses a value of type ${t}`);
}

/** Code-point order, not the locale order the default sort would use. */
export function compareStrings(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function sha256Hex(text) {
  return createHash('sha256').update(text, 'utf8').digest('hex');
}

/** The candidate SET in canonical order: unique, sorted. Order offered is discarded here. */
export function canonicalCandidateSet(candidates) {
  if (!Array.isArray(candidates)) throw new DigestError('candidate set must be an array');
  return [...new Set(candidates.map(String))].sort(compareStrings);
}

/**
 * The evidence the oracle is entitled to see, in one object.
 * Object keys are sorted by canonicalJson, so an object never leaks an ordering into the key.
 */
function keyBody(record, candidates) {
  return {
    candidates,
    evidence: {
      fixtureHash: record.fixture.hash,
      modelHash: record.model.hash,
      sourceHash: record.source.hash,
      task: record.task,
      knowableContext: record.context.knowable,
      allowedRequest: record.request.allowed,
    },
    versions: {
      pointId: record.point.id,
      pointVersion: record.point.version,
      schemaVersion: record.schemaVersion,
      promptVersion: record.prompt.version,
      servedModelId: record.servedModel.id,
      servedModelVersion: record.servedModel.version,
    },
  };
}

/**
 * The candidate list has exactly one home on a record: candidates.offeredOrder.
 * A second copy inside request.allowed is a second order that nothing keeps in step with the
 * first, so a reviewer reading the record later is shown two orders and cannot tell which one
 * the oracle actually saw. Criteria therefore travel as an object, keyed by option id.
 */
export function assertOrderFreeRequest(record) {
  const set = new Set(record.candidates.set.map(String));
  if (set.size === 0) return;
  const walk = (node, path) => {
    if (Array.isArray(node)) {
      const asStrings = node.map((x) => (typeof x === 'string' ? x : null));
      if (asStrings.every((x) => x !== null) && asStrings.length === set.size
          && asStrings.every((x) => set.has(x))) {
        throw new DigestError(
          `request.allowed${path} lists the candidates as an array, which is a second copy of `
          + 'the option order. The order offered lives on candidates.offeredOrder and nowhere '
          + 'else. Send the options as an object keyed by option id.');
      }
      node.forEach((child, i) => walk(child, `${path}[${i}]`));
      return;
    }
    if (node && typeof node === 'object') {
      for (const k of Object.keys(node)) walk(node[k], `${path}.${k}`);
    }
  };
  walk(record.request.allowed, '');
}

function offeredOrderOf(record) {
  if (!Array.isArray(record?.candidates?.offeredOrder)) {
    throw new DigestError('candidates.offeredOrder must be an array');
  }
  return record.candidates.offeredOrder.map(String);
}

/**
 * sha256 over the complete evidence, the candidate SET in canonical order, and the order the
 * options were actually offered in. The order is here because the engine's digest has it
 * (DecisionDigest.cs:49), so the same question asked twice in two layouts is two keys.
 */
export function oracleKey(record) {
  assertOrderFreeRequest(record);
  return sha256Hex(canonicalJson(keyBody(record, {
    set: canonicalCandidateSet(record.candidates.set),
    offered: offeredOrderOf(record),
  })));
}

/** sha256 over the same evidence with only the order actually offered. Recorded, never looked up. */
export function offerDigest(record) {
  return sha256Hex(canonicalJson(keyBody(record, { offered: offeredOrderOf(record) })));
}
