// The general scorer.
//
// Reports, per decision point: precision, coverage, false positives, false refusals,
// uncertainty (the share below the threshold, and accuracy by confidence band), the counts of
// unsupported and unanswerable cases, the six case families, the baseline's own accuracy, and
// rule agreement.
//
// Rule agreement is computed and reported, and it is NEVER used as truth. "The rules are the
// eval set" is circular when the new judgment is meant to improve on those rules: a point that
// agrees with the rule 100% of the time has proved nothing except that it copied the rule.
// Correctness comes from record.label only, which carries its author and its evidence, and
// which record.mjs refuses to accept from the system being scored.
//
// This is also the entry point that REFUSES. Every record it is handed is validated here, not
// only the ones this harness wrote; a gate run has to carry the declared split policy and match
// it; and a gate run refuses bars that are only proposed. The refusals used to live in the
// library and be reachable from the command line only by luck.
//
// An answer is read exactly as the engine reads one. The rules are ported from
// Semanticus.Core/Decisions/DecisionAnswerReader.cs, cited line by line below, and the
// self-test re-reads those lines of the C# so a drift on either side fails.

import { CASE_FAMILIES, pyStr, validateRecord } from './record.mjs';
import { barFor, isRatified, validateManifest } from './manifest.mjs';
import { assertGateFixtures, assertSplitConsistency } from './split.mjs';

export class ScoreError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ScoreError';
  }
}

/**
 * A reply the engine would refuse. This is not a harness failure: the engine treats a malformed
 * reply as an ordinary runtime outcome, makes the WHOLE ask "not decided" and lets the caller
 * take the fallback (DecisionAnswerReader.cs:40-88, and its header: not the one bad answer, the
 * whole ask). The scorer's honest reading of that is a row that answered nothing, counted.
 */
export class AnswerRejected extends Error {
  constructor(message) {
    super(message);
    this.name = 'AnswerRejected';
  }
}

export const BANDS = [
  ['below 0.6', (c) => c < 0.6],
  ['0.6 to 0.85', (c) => c >= 0.6 && c <= 0.85],
  ['above 0.85', (c) => c > 0.85],
];

export function bandOf(confidence) {
  for (const [name, test] of BANDS) if (test(confidence)) return name;
  return 'below 0.6';
}

/**
 * The shapes this scorer can read. The engine also reads a "score" ladder (DecisionAnswerReader.cs:90-171,
 * and ReadScoreProbabilities from :245): the expected rung as a fractional number counted from zero, and a
 * legend and probabilities keyed by rung position. Pilot one's verdict.json holds 44 such answers and a live
 * plan.rank reply has the same shape (26 Sep), but this scorer has not ported that reading, so the harness
 * refuses a score point by name. A declared gap, not a silent difference: port :94-:107 and
 * ReadScoreProbabilities before scoring one.
 */
export const SCORABLE_TYPES = ['choice', 'noul'];

/** DecisionAnswerReader.cs:214-255. Absent or null is an empty map; anything malformed rejects. */
function readProbabilities(options, response) {
  const result = new Map();
  if (!('probabilities' in response)) return result;
  const probabilities = response.probabilities;
  if (probabilities === null) return result;
  // :169. An array is not an object to the engine's reader either, so it lands here.
  if (typeof probabilities !== 'object' || Array.isArray(probabilities)) {
    throw new AnswerRejected("the answer was rejected: 'probabilities' was not an object");
  }
  for (const [name, value] of Object.entries(probabilities)) {
    if (!options.has(name)) {
      throw new AnswerRejected( // :175
        `the answer was rejected: 'probabilities' names '${name}', which is not one of the `
        + 'options that were sent');
    }
    if (typeof value !== 'number' || Number.isNaN(value)) { // :178 and :184
      throw new AnswerRejected(
        `the answer was rejected: the probability for '${name}' was not a number`);
    }
    if (value < 0 || value > 1) { // :189
      throw new AnswerRejected(
        `the answer was rejected: the probability for '${name}' is outside 0 to 1`);
    }
    result.set(name, value);
  }
  return result;
}

/** DecisionAnswerReader.cs:286-316. An explicit confidence wins; otherwise the picked option's. */
function readConfidence(response, probabilities, picked) {
  if ('confidence' in response) {
    const confidence = response.confidence;
    if (typeof confidence !== 'number' || Number.isNaN(confidence)) { // :209 and :210
      throw new AnswerRejected("the answer was rejected: 'confidence' was not a number");
    }
    if (confidence < 0 || confidence > 1) { // :211
      throw new AnswerRejected("the answer was rejected: 'confidence' is outside 0 to 1");
    }
    return confidence;
  }
  if (probabilities.has(picked)) return probabilities.get(picked); // :222
  throw new AnswerRejected( // :224
    'the answer was rejected: it carried neither a confidence nor a probability for the option '
    + 'it picked');
}

/**
 * Reads one raw typed response against the question that was asked, by the engine's rules.
 * Returns a comparable value and a confidence in 0 to 1, or throws AnswerRejected.
 *
 * @param {{questionType: string, candidates: string[]}} ask
 * @param {unknown} response
 */
export function readAnswer(ask, response) {
  const wireType = ask?.questionType;
  if (!SCORABLE_TYPES.includes(wireType)) {
    throw new ScoreError(
      `the scorer does not know the question type "${wireType}". Scored points are `
      + `${SCORABLE_TYPES.map((t) => `"${t}"`).join(' and ')}.`);
  }
  const options = new Set((ask?.candidates ?? []).map(String));

  if (response === null || typeof response !== 'object' || Array.isArray(response)) {
    throw new AnswerRejected('the answer was rejected: it was not an object'); // :61
  }
  if (typeof response.type !== 'string') { // :64
    throw new AnswerRejected('the answer was rejected: it named no shape');
  }
  if (response.type !== wireType) { // :71
    throw new AnswerRejected(
      `the answer was rejected: it came back as '${response.type}' where '${wireType}' was asked`);
  }

  const probabilities = readProbabilities(options, response);

  if (wireType === 'choice') { // :82-128
    if (!('choice' in response)) {
      throw new AnswerRejected("the answer was rejected: it carried no 'choice'"); // :87
    }
    const id = response.choice;
    if (typeof id !== 'string') { // :110
      throw new AnswerRejected("the answer was rejected: 'choice' was not text");
    }
    if (!options.has(id)) { // :114
      throw new AnswerRejected(
        `the answer was rejected: '${id}' is not one of the options that were sent`);
    }
    return { value: id, confidence: readConfidence(response, probabilities, id) };
  }

  // noul, :128-152
  const p = response.noul;
  if (typeof p !== 'number' || Number.isNaN(p)) { // :132
    throw new AnswerRejected("the answer was rejected: it carried no 'noul' number");
  }
  if (p < 0 || p > 1) { // :139
    throw new AnswerRejected("the answer was rejected: the 'noul' probability is outside 0 to 1");
  }
  // :148. A noul reply carries no confidence field, and the honest reading of one probability is
  // how far it sits from an even split. The engine derives exactly this, so the harness must too:
  // Math.abs(p - 0.5) * 2 (which pilot one's score.py used) gives 0.8 where the engine gives 0.9,
  // and at a 0.85 threshold that is the gate abstaining where the engine would advise.
  return { value: p >= 0.5, confidence: Math.max(p, 1 - p) }; // :149 for the yes/no reading
}

function ratio(numerator, denominator) {
  return denominator === 0 ? null : numerator / denominator;
}

/**
 * @param {object} options
 * @param {Array} options.records   harness records
 * @param {object} options.oracle   anything with answer(record); a miss must throw
 * @param {object} options.manifest the committed bars
 * @param {'gate'|'development'} options.mode
 * @param {object} [options.splitPolicy]
 */
export function scoreRun({ records, oracle, manifest, mode, splitPolicy }) {
  if (mode !== 'gate' && mode !== 'development') {
    throw new ScoreError(`mode must be "gate" or "development", got "${mode}"`);
  }
  if (!Array.isArray(records) || records.length === 0) {
    throw new ScoreError('nothing to score: no records');
  }
  validateManifest(manifest);

  // Every record, every run. The writer's refusals only ever covered records this harness wrote,
  // and the scorer can be handed records from a file, another branch or a half-written run.
  records.forEach((record, i) => {
    try {
      validateRecord(record);
    } catch (err) {
      err.message = `record ${i + 1} of ${records.length} was refused: ${err.message}`;
      throw err;
    }
  });

  if (mode === 'gate') {
    if (!splitPolicy) {
      throw new ScoreError(
        'a gate run needs the declared split policy from manifest.json. Without it nothing '
        + 'checks that the records really are the held-out half, and the gate reports a number '
        + 'nobody should trust.');
    }
    assertGateFixtures(records);
  }
  if (splitPolicy) assertSplitConsistency(records, splitPolicy);
  if (mode === 'gate' && !isRatified(manifest)) {
    throw new ScoreError(
      `a gate run refuses bars whose status is "${manifest.status}". Only ratified bars, sealed `
      + 'in the manifest, can fail a point. Proposed bars are development evidence: score them '
      + 'in development mode, or have them ratified and sealed first.');
  }

  const byPoint = new Map();
  for (const record of records) {
    if (!byPoint.has(record.point.id)) byPoint.set(record.point.id, []);
    byPoint.get(record.point.id).push(record);
  }

  const points = {};
  for (const [pointId, group] of [...byPoint.entries()].sort((a, b) =>
    (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0))) {
    // Refuses here, by design, when the point has no committed bar.
    const bar = barFor(manifest, pointId);
    points[pointId] = scorePoint(pointId, group, oracle, bar);
  }

  return {
    mode,
    schemaVersion: records[0].schemaVersion,
    manifestDigest: manifest.manifestDigest,
    manifestCommittedUtc: manifest.committedUtc,
    manifestStatus: manifest.status,
    barsRatified: isRatified(manifest),
    records: records.length,
    oracle: { kind: oracle.kind ?? 'unknown', hits: oracle.hits ?? null },
    points,
  };
}

function scorePoint(pointId, group, oracle, bar) {
  const threshold = bar.confidenceThreshold;
  const rows = [];
  for (const record of group) {
    const response = oracle.answer(record);
    const ask = {
      questionType: record.request.allowed.questionType,
      candidates: record.candidates.set,
    };
    let read = null;
    let refusedBecause = null;
    try {
      read = readAnswer(ask, response);
    } catch (err) {
      if (!(err instanceof AnswerRejected)) throw err;
      refusedBecause = err.message;
    }
    const decided = read !== null;
    // Not decided means the caller took the fallback, so the row answered nothing: never served,
    // never correct, and never silently dropped.
    const predicted = decided ? pyStr(read.value) : 'None';
    const confidence = decided ? read.confidence : 0;
    const truth = pyStr(record.label.value);
    rows.push({
      recordId: record.recordId,
      decided,
      refusedBecause,
      predicted,
      truth,
      correct: decided && predicted === truth,
      confidence,
      band: bandOf(confidence),
      served: decided && confidence >= threshold,
      baselineCorrect: pyStr(record.baseline.output) === truth,
      ruleAgrees: decided && pyStr(record.ruleAnswer) === predicted,
      answerability: record.answerability,
      caseFamilies: record.caseFamilies,
      modelId: record.split.modelId,
    });
  }

  const n = rows.length;
  const served = rows.filter((r) => r.served);
  const refused = rows.filter((r) => !r.served);
  const servedCorrect = served.filter((r) => r.correct);
  const servedWrong = served.filter((r) => !r.correct);
  const refusedWouldBeCorrect = refused.filter((r) => r.correct);

  const accuracyByBand = {};
  for (const [name] of BANDS) {
    const inBand = rows.filter((r) => r.band === name);
    accuracyByBand[name] = {
      n: inBand.length,
      share: inBand.length / n,
      accuracy: ratio(inBand.filter((r) => r.correct).length, inBand.length),
    };
  }

  const families = {};
  for (const family of CASE_FAMILIES) {
    families[family] = rows.filter((r) => r.caseFamilies[family] === true).length;
  }

  const notDecided = rows.filter((r) => !r.decided);

  const metrics = {
    n,
    accuracy: ratio(rows.filter((r) => r.correct).length, n),
    // Answers the engine's own reader would have refused. Reported, never hidden inside the
    // refusal rate, because a point that cannot produce a well-formed answer is a different
    // problem from a point that is honestly unsure.
    notDecided: notDecided.length,
    notDecidedReasons: [...new Set(notDecided.map((r) => r.refusedBecause))].slice(0, 5),
    precision: ratio(servedCorrect.length, served.length),
    coverage: served.length / n,
    falsePositives: { count: servedWrong.length, rate: servedWrong.length / n },
    falseRefusals: {
      count: refusedWouldBeCorrect.length,
      rate: refusedWouldBeCorrect.length / n,
    },
    uncertainty: {
      confidenceThreshold: threshold,
      shareBelowThreshold: refused.length / n,
      accuracyByBand,
    },
    answerability: {
      answerable: rows.filter((r) => r.answerability === 'answerable').length,
      unanswerable: rows.filter((r) => r.answerability === 'unanswerable').length,
      unsupported: rows.filter((r) => r.answerability === 'unsupported').length,
    },
    caseFamilies: families,
    // Stored on its own, next to correctness, never inside it.
    ruleAgreement: {
      n,
      agreed: rows.filter((r) => r.ruleAgrees).length,
      rate: ratio(rows.filter((r) => r.ruleAgrees).length, n),
      note: 'agreement with the rule, not evidence of correctness',
    },
    baselineAccuracy: ratio(rows.filter((r) => r.baselineCorrect).length, n),
    fallback: bar.fallback,
  };

  metrics.bar = {
    ...bar,
    verdict: {
      precision: metrics.precision === null ? null : metrics.precision >= bar.precisionFloor,
      coverage: metrics.coverage >= bar.coverageFloor,
      falsePositives: metrics.falsePositives.rate <= bar.falsePositiveCeiling,
      falseRefusals: metrics.falseRefusals.rate <= bar.falseRefusalCeiling,
    },
  };
  metrics.bar.passes = Object.values(metrics.bar.verdict).every((v) => v === true);
  return metrics;
}
