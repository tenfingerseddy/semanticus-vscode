// verify: m11/completeness round 1 (27 Sep) wrote this file to hold the lane's copy to THIS build, and at d0452195 it
// listed 43 sentences that promised what the build did not do. The coordinator then ruled (D1, round 2): the lane
// writes the RELEASE's words and merges last, so the words may name what a feature lane brings, as long as a
// machine-checkable list says which code each claim needs and fails when that code is missing. That list is
// tools/release-claims.mjs. This file is its npm-test side:
//
//   - a claim whose code is here must hold (six areas, Connections, the consent marker, 'live', and the rest);
//   - a claim with no code and no lane fails: the date table is off for the release, so its words may not appear;
//   - what goes to Jev (D2) and the README's line on inference (D7) are checked in full;
//   - a claim whose code arrives with a named lane is printed as waiting, not failed. `node tools/release-claims.mjs`
//     is the strict run the integrator makes after the last merge, and there a waiting claim fails.
//
// On m11/completeness alone the waiting list names m11/askdata, m11/queries-redesign, m11/jev-query-tune (the Jev
// reader, Kane's 28 Sep decisions), m11/m4-columns-fair, m11/m4-aischema, m11/m4-relationships and m11/sol-fixes-1
// (which carries m11/describe-jev). That is expected; the file says so in its output.
import assert from 'node:assert/strict';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { check, CLAIMS } = await import(pathToFileURL(join(repo, 'tools', 'release-claims.mjs')).href);

const r = check({ strict: false });
for (const [, line] of r.waiting) console.log('WAITS FOR ITS LANE: ' + line);
for (const f of r.failures) console.log('COPY AHEAD OF THE RELEASE: ' + f);
assert.equal(r.failures.length, 0, `${r.failures.length} claim(s) have no code behind them and no lane that brings it (listed above)`);
// The list must keep seeing the copy: if the card or Help moved and nothing was read, every claim would pass unseen.
for (const id of ['six-areas', 'connections', 'ask-your-data', 'base-measures', 'what-copilot-sees', 'consent-first', 'what-pro-decides'])
  assert.ok(r.made.has(id), `the claim ${id} is made somewhere in the release's words, so the surfaces are still being read`);
assert.ok(!r.made.has('date-table'), 'no surface promises the date table: it is off for the release');
console.log(`completeness-copy-truth: ${CLAIMS.length} claims listed, ${r.made.size} made on ${r.surfaces} surfaces, `
  + `${r.waiting.size} waiting for their lane${r.waiting.size ? ` (${[...new Set([...r.waiting.values()].map((l) => /arrives with ([\w/-]+)/.exec(l)[1]))].join(', ')})` : ''}; `
  + 'run node tools/release-claims.mjs after the last merge');
