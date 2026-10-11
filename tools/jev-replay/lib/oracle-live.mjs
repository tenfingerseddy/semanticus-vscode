// The live oracle. Nothing in this repository imports this file.
//
// It exists so a human can, by hand, re-record answers against the real service. It is not part
// of the default CLI path, it is not part of `replay.mjs ci`, and replay.test.mjs does not import
// it. The self-test checks that fact by reading the source of every other file, because the
// cheapest way for a secret-free harness to stop being secret-free is for one import to creep in.
//
// Four refusals, all of them deliberate:
//
//   1. It refuses unless JEV_REPLAY_LIVE is exactly "1". Not truthy, not "true", not "yes".
//      An opt-in that accepts several spellings gets switched on by accident.
//   2. It refuses unless the key file path arrives in JEV_REPLAY_KEY_FILE.
//   3. It refuses a key file that lives inside this repository. A key inside the repo is a key
//      one `git add -A` away from being published.
//   4. It never prints, returns, logs or stores the key. The key is read inside a closure, used
//      for one header, and never attached to any object a caller can reach.

import { existsSync, readFileSync, statSync } from 'node:fs';
import { resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { oracleKey } from './digest.mjs';

export class LiveOracleRefused extends Error {
  constructor(message) {
    super(message);
    this.name = 'LiveOracleRefused';
  }
}

/** The repository root, found from this file, so the containment check needs no configuration. */
export function repoRoot() {
  return resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..', '..');
}

export function assertLiveAllowed(env) {
  if (env.JEV_REPLAY_LIVE !== '1') {
    throw new LiveOracleRefused(
      'the live oracle refuses to run: JEV_REPLAY_LIVE must be exactly "1". The default path and '
      + 'the self-test use recorded answers only.');
  }
  const keyPath = env.JEV_REPLAY_KEY_FILE;
  if (typeof keyPath !== 'string' || keyPath.trim() === '') {
    throw new LiveOracleRefused(
      'the live oracle refuses to run: set JEV_REPLAY_KEY_FILE to a key file that lives outside '
      + 'this repository. The harness never carries a key of its own.');
  }
  const resolved = resolve(keyPath);
  const root = repoRoot();
  if (resolved === root || resolved.startsWith(root + sep)) {
    throw new LiveOracleRefused(
      `the live oracle refuses the key file at ${resolved} because it is inside the repository. `
      + 'Keys live outside the tree so they cannot be committed.');
  }
  if (!existsSync(resolved) || !statSync(resolved).isFile()) {
    throw new LiveOracleRefused(`the live oracle cannot read a key file at ${resolved}`);
  }
  return resolved;
}

/**
 * Builds a live oracle. Throws unless every gate above is satisfied.
 * The key never leaves this closure.
 */
export function createLiveOracle(env = process.env, options = {}) {
  const keyFile = assertLiveAllowed(env);
  const endpoint = options.endpoint ?? env.JEV_REPLAY_ENDPOINT;
  if (typeof endpoint !== 'string' || endpoint === '') {
    throw new LiveOracleRefused(
      'the live oracle refuses to run: no endpoint. Set JEV_REPLAY_ENDPOINT.');
  }
  const readKey = () => {
    const match = readFileSync(keyFile, 'utf8')
      .match(/^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*["']?([^"'\s]+)/m);
    const key = match ? match[1] : readFileSync(keyFile, 'utf8').trim();
    if (!key) throw new LiveOracleRefused(`the key file at ${keyFile} holds no key`);
    return key;
  };
  return {
    kind: 'live',
    keyFile,
    endpoint,
    async answer(record) {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: {
          authorization: `Bearer ${readKey()}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({ request: record.request.allowed, key: oracleKey(record) }),
        signal: AbortSignal.timeout(Number(env.JEV_REPLAY_TIMEOUT_MS ?? 20000)),
      });
      if (!response.ok) {
        // The body can echo a request; report the status only, never the body and never the key.
        throw new LiveOracleRefused(`live oracle call failed with HTTP ${response.status}`);
      }
      return (await response.json()).answers;
    },
  };
}
