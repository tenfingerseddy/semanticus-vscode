// Option ordering and option neutrality.
//
// This file is an addition to the file list in the brief. Ordering is not digesting, and putting
// a shuffler inside digest.mjs would have mixed "what the oracle saw" with "how we key it".
//
// Two rules live here:
//
//  1. The option order is generated from the record's seed, deterministically. The same seed
//     reproduces the same order, so a replay is repeatable; a different seed gives a different
//     order, so a run cannot be tuned to one fixed layout. The oracle key CARRIES the order
//     (see digest.mjs), so a different seed is a different question that needs its own recorded
//     answer. Reshuffling is therefore a re-recording job, never a free replay trick.
//
//  2. The option text carries no hint of which system produced a candidate. If one option read
//     "Internet Sales (suggested by Jev)" the question would answer itself, and the score would
//     measure the label, not the judgment. Candidates that give the game away are refused by
//     name.

import { createHash } from 'node:crypto';
import { canonicalCandidateSet } from './digest.mjs';

export class OfferError extends Error {
  constructor(message) {
    super(message);
    this.name = 'OfferError';
  }
}

/** Whole tokens that name a producing system or a producing rule. */
export const DENIED_TOKENS = [
  'jev', 'claude', 'gpt', 'openai', 'anthropic', 'copilot', 'baseline', 'heuristic', 'oracle',
];

/** Run-together phrases that disclose provenance even when no single token does. */
export const DENIED_PHRASES = [
  'systema', 'systemb', 'suggestedby', 'proposedby', 'producedby', 'aisuggested', 'modelpicked',
  'rulepicked', 'ourpick', 'thenewone', 'theoldone',
];

function tokensOf(text) {
  return String(text).toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
}

function squashed(text) {
  return String(text).toLowerCase().replace(/[^a-z0-9]+/g, '');
}

/**
 * Refuse any candidate whose own text says where it came from.
 * Throws naming the candidate and the token, so the refusal is actionable.
 */
export function assertNeutralCandidates(candidates) {
  for (const candidate of candidates) {
    for (const token of tokensOf(candidate)) {
      if (DENIED_TOKENS.includes(token)) {
        throw new OfferError(
          `option "${candidate}" is not neutral: it contains the word "${token}", which names a `
          + 'producing system. Option text must not say which system produced a candidate.');
      }
    }
    const flat = squashed(candidate);
    for (const phrase of DENIED_PHRASES) {
      if (flat.includes(phrase)) {
        throw new OfferError(
          `option "${candidate}" is not neutral: it reads as "${phrase}", which discloses where `
          + 'the candidate came from.');
      }
    }
  }
  return true;
}

/** An endless stream of bytes derived from the seed. Re-hashed with a counter for more bytes. */
function* seedBytes(seed) {
  let block = 0;
  for (;;) {
    const digest = createHash('sha256').update(`jev-replay-offer:${String(seed)}:${block}`).digest();
    for (const byte of digest) yield byte;
    block += 1;
  }
}

/** Uniform integer in [0, bound) by rejection sampling, so no modulo bias creeps in. */
function nextBelow(stream, bound) {
  if (bound <= 1) return 0;
  const limit = Math.floor(0x100000000 / bound) * bound;
  for (;;) {
    const value = ((stream.next().value << 24) >>> 0)
      + (stream.next().value << 16) + (stream.next().value << 8) + stream.next().value;
    const unsigned = value >>> 0;
    if (unsigned < limit) return unsigned % bound;
  }
}

/**
 * The order this harness would offer the options in, from the seed.
 * Starts from the canonical SET, so the caller's incoming order cannot influence the result.
 */
export function offerOrder(candidates, seed) {
  if (seed === undefined || seed === null || seed === '') {
    throw new OfferError('offerOrder needs a seed; an unseeded order is not reproducible');
  }
  const items = canonicalCandidateSet(candidates);
  assertNeutralCandidates(items);
  const stream = seedBytes(seed);
  for (let i = items.length - 1; i > 0; i -= 1) {
    const j = nextBelow(stream, i + 1);
    const swap = items[i];
    items[i] = items[j];
    items[j] = swap;
  }
  return items;
}
