// m11/completeness round 4 (verifier round 3, F2; Sol on round 4): the claim list's plan-statement row fails on every
// way of making the Plan statement's three claims (one click, a best-practice grade, a SQL start) until the rerun on
// the joined build passes, and it does not fail on the true sentences that name what exists now.
import assert from 'node:assert/strict';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { CLAIMS } = await import(pathToFileURL(join(repo, 'tools', 'release-claims.mjs')).href);
const row = CLAIMS.find((c) => c.id === 'plan-statement');
assert.ok(row, 'the claim list has a plan-statement row');
assert.ok(!row.arrivesWith && !row.jev, 'the plan-statement row waits for no lane, so npm test fails on it too');
const failures = [];

for (const s of [
  'Build from a source and Fix in one click join Pro when they are ready.',
  'Model quality shows a best-practice grade beside the count.',
  'Start a new model from a SQL source and press Run the plan.',
  'The Plan fixes every issue in one click.',
  'Pro lets you start from SQL.',
  'One-click fixes for the whole plan are on Pro.',
  'Your model is graded against best practice.',
  'The Plan fixes everything in one click.',
  'Pro includes fix in one click.',
]) if (!row.re.test(s)) failures.push(`plan-statement does not catch "${s}"`);

for (const s of [
  'Apply a safe fix in one click.',
  'Apply a safe one-click fix for the selected issue.',
  'That is Model Spec, Model quality, AI readiness, every one-click fix, and Publish.',
  'The Model Spec wizard can draft from a SQL source.',
  '## Build from source',
  'A check against SQL also needs a SQL connection.',
  'Connections are on one page, with a SQL endpoint for each model.',
]) if (row.re.test(s)) failures.push(`plan-statement fails on the true sentence "${s}"`);

if (row.needs.every((n) => n.ok())) failures.push('plan-statement passes, but the rerun has not been recorded as passed');

for (const f of failures) console.log('PLAN STATEMENT: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the plan-statement row misreads a sentence (listed above)`);
console.log('completeness-release-claims-r4: the plan-statement row catches every form of the three claims and passes the true ones');
