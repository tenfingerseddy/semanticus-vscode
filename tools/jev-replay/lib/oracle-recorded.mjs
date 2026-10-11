// The recorded oracle: the only oracle the default path and the tests ever use.
//
// It is keyed on the ORACLE KEY from digest.mjs, which covers the complete evidence and the
// candidate set. It is never keyed on the question text or a question hash, because two models
// can be asked the same words about different objects and a question-hash key would hand back
// the other model's answer.
//
// A MISS IS AN ERROR. Not a fallback answer, not a skip, not a null. A miss means the fixture
// and the store have drifted apart, and the only safe thing a gate can do is stop and say which
// key it could not find. A fallback answer here would turn a broken fixture into a green run.

import { oracleKey } from './digest.mjs';

export class OracleMissError extends Error {
  constructor(message, key) {
    super(message);
    this.name = 'OracleMissError';
    this.key = key;
  }
}

export class RecordedOracle {
  /** @param {Array<{oracleKey: string, response: unknown}>} entries */
  constructor(entries) {
    this.kind = 'recorded';
    this.byKey = new Map();
    this.hits = 0;
    this.misses = 0;
    for (const entry of entries) {
      if (typeof entry?.oracleKey !== 'string') {
        throw new Error('a recorded oracle entry needs an oracleKey string');
      }
      if (!('response' in entry)) {
        throw new Error(`recorded oracle entry ${entry.oracleKey} carries no response`);
      }
      this.byKey.set(entry.oracleKey, entry.response);
    }
  }

  get size() {
    return this.byKey.size;
  }

  /** Returns the recorded typed response, or throws naming the key it could not find. */
  answer(record) {
    const key = oracleKey(record);
    if (!this.byKey.has(key)) {
      this.misses += 1;
      throw new OracleMissError(
        `recorded oracle has no answer for key ${key} (record "${record.recordId}", point `
        + `"${record.point.id}"). A miss is an error: the harness does not fall back to a guess `
        + 'and does not skip the case.', key);
    }
    this.hits += 1;
    return this.byKey.get(key);
  }
}

export function readOracleEntries(text) {
  return text.split('\n').filter((line) => line.trim() !== '').map((line) => JSON.parse(line));
}

export function createRecordedOracle(entries) {
  return new RecordedOracle(entries);
}
