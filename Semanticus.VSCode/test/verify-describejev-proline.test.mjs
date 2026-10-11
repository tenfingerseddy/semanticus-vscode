// verify: the verifier's failing test for lane m11/describe-jev 31a92ca3 (Jev drafts on the Plan door, Pro). LEFT FAILING
// on purpose: it passes once the defect is fixed.
//
// DEFECT D8. The Plan's Pro line is what a Free person reads, on the Plan row and in Ctrl J, about what Pro does with
// their words. composer-model.ts says it is "the one string to change" "when real Jev drafting lands". 31a92ca3 switched
// Jev drafting on for the Plan door, but the line still says Pro only lists the steps "for you to pick", and the honesty
// scan in copy-rules.test.mjs still bans the approved copy of composer contract 10.7 ("Jev drafts the steps for a task
// from your words in Pro.") with the reason "Jev drafts nothing today", which is no longer true.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const builder = readFileSync(resolve(repo, 'Semanticus.Engine/Composer/JevDraftBuilder.cs'), 'utf8');
const planOn = /internal const bool JevDraftsOnPlan = true;/.test(builder);
assert.ok(planOn, 'this test is about the build where Jev drafts on the Plan door');

const model = readFileSync(resolve(repo, 'Semanticus.VSCode/webview/src/composer-model.ts'), 'utf8');
const proLine = /proLine:\s*'([^']*)'/.exec(model)?.[1];
assert.ok(proLine, 'found WORD.proLine');
assert.doesNotMatch(proLine, /for you to pick/,
  `the Plan's Pro line tells a Free person that Pro only lists steps for them to pick, but Jev drafts the steps on Pro now: "${proLine}"`);

const rules = readFileSync(resolve(here, 'copy-rules.test.mjs'), 'utf8');
assert.doesNotMatch(rules, /Jev drafts nothing today/,
  'copy-rules.test.mjs still bans the approved Pro line of composer contract 10.7 because "Jev drafts nothing today", which is false since 31a92ca3');
console.log('verify-describejev-proline: ok');
