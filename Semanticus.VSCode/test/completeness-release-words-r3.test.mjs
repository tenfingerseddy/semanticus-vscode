// verify: m11/completeness round 3 (28 Sep), failing on purpose at 01ec7063.
//
// Two things the round 3 brief asks of the release's words and of tools/release-claims.mjs, both this lane's:
// 1. The public statement about the Plan is settled only after a rerun on the joined build (the new lanes
//    m11/plan-harm, m11/plan-rows, m11/bpa-grade, m11/sql-start and m11/rule-clashes are still being built), so no
//    copy may claim "one click", a best-practice grade or a SQL start until then. Both READMEs, in the Free and Pro
//    paragraph this lane rewrote, still end "Build from a source and Fix in one click join Pro when they are ready."
//    (README.md:120-121, Semanticus.VSCode/README.md:119-120). The claim list has no row for any of the three, so
//    neither the strict run nor npm test sees it.
// 2. On this branch alone the claim list must name exactly the lanes not yet merged. Kane's two 28 Sep claims (the Jev
//    reader sends the model's own other names, its synonyms; a question read two ways names the other, "You may have
//    meant") arrive with m11/jev-query-tune (dispatch/b-jev-query-tune.md, Kane's decisions 1 and 2), which branches
//    off m11/query-split. The list names m11/query-split, whose head (19192631) has neither and is not the lane that
//    builds them, so merging it would leave both claims failing under a lane name that never brings them.
import assert from 'node:assert/strict';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { CLAIMS, surfaces } = await import(pathToFileURL(join(repo, 'tools', 'release-claims.mjs')).href);
const failures = [];

// 1a. No release surface makes the Plan statement's three claims while the statement is unsettled.
const PLAN_STATEMENT = /\bBuild from a source\b|\bFix in one click\b|\bbest[- ]practice grade\b|\bgrade A (?:on|for) best practice\b|\bone[- ](?:click|button|press) (?:build|best[- ]practice)\b/i;
for (const s of surfaces())
  for (const p of s.paragraphs)
    for (const sentence of p.sentences)
      if (PLAN_STATEMENT.test(sentence)) failures.push(`${s.name} says "${sentence}", a claim the Plan statement's rerun has not settled`);

// 1b. The claim list guards the rule: each of these sentences is caught by a row that fails (no lane to wait for).
for (const probe of ['Build from a source and Fix in one click join Pro when they are ready.',
  'Model quality shows a best-practice grade beside the count.',
  'Start a new model from a SQL source and press Run the plan.']) {
  const rows = CLAIMS.filter((c) => c.re.test(probe));
  if (!rows.some((c) => !c.jev && !c.arrivesWith))
    failures.push(`no claim row fails on "${probe}"${rows.length ? ` (it matches only ${rows.map((c) => `${c.id}${c.arrivesWith ? `, which waits for ${c.arrivesWith}` : ''}`).join('; ')})` : ''}`);
}

// 2. The lane named for Kane's two 28 Sep claims is the lane that builds them.
for (const probe of ['the other names your model gives things (its synonyms)', 'Showing Net Sales. You may have meant Gross Sales.']) {
  for (const c of CLAIMS.filter((x) => x.re.test(probe)))
    if (c.arrivesWith && !/\bm11\/jev-query-tune\b/.test(c.arrivesWith))
      failures.push(`${c.id} (made by "${probe}") says it arrives with ${c.arrivesWith}; its code comes with m11/jev-query-tune`);
}

for (const f of failures) console.log('RELEASE WORDS: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the release's words or the claim list break the round 3 brief (listed above)`);
console.log('completeness-release-words-r3: no Plan statement claim is made, the list fails on one, and Kane\'s two claims wait for m11/jev-query-tune');
