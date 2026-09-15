// The Checks > Tests page turns engine results into words. These are the WORDS, tested as behaviour:
// the module is transpiled and run, so a wrong sentence fails here rather than in a screenshot.
//
// Written against the approved redesign (tests-redesign.html), Astra's UAT and her ten-point critique.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output, 'utf8').toString('base64')}`);
}

const model = await loadTypeScriptModule(resolve(root, 'webview/src/tests-model.ts'));

// ---------------------------------------------------------------------------------------------------
// 3. The result chip. Five states, and an unknown is never dressed as a failure.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.resultChip(false, { verdict: 'Pass' }), 'Off', 'a turned-off check reads Off whatever its last verdict was');
assert.equal(model.resultChip(true, null), 'Not run', 'a check with no outcome has not run');
assert.equal(model.resultChip(true, undefined), 'Not run', 'an absent outcome is Not run');
assert.equal(model.resultChip(true, { verdict: 'Pass' }), 'Pass');
assert.equal(model.resultChip(true, { verdict: 'Fail' }), 'Differs');
assert.equal(model.resultChip(true, { verdict: 'Suspect' }), 'Differs', 'a suspect check did see a difference; its root cause is named in the row');
assert.equal(model.resultChip(true, { verdict: 'NotVerifiable' }), 'Could not check');
assert.equal(model.resultChip(true, { verdict: 'Pass', missing: true }), 'Could not check', 'a missing target cannot pass');

// ---------------------------------------------------------------------------------------------------
// 1. The Run scope menu lists exactly what "all" includes, and never offers a group with nothing in it.
// ---------------------------------------------------------------------------------------------------
{
  const all = model.runScopeGroups(5, 8, 6);
  assert.deepEqual(all.map((g) => g.id), ['checks', 'relationships', 'tableCounts']);
  assert.deepEqual(all.map((g) => g.label), ['Enabled saved checks', 'Relationships', 'Table row counts']);
  assert.deepEqual(all.map((g) => g.count), [5, 8, 6]);

  const noTables = model.runScopeGroups(5, 8, 0);
  assert.deepEqual(noTables.map((g) => g.id), ['checks', 'relationships'], 'a group with nothing to run is not offered');

  assert.deepEqual(model.runScopeGroups(0, 0, 0), [], 'nothing to run offers nothing');

  // Found by driving the built page: before the first run nobody has counted the relationships yet.
  // An UNKNOWN count is not zero, and hiding the group made "all" look like it left relationships out.
  const beforeAnyRun = model.runScopeGroups(5, null, 2);
  assert.deepEqual(beforeAnyRun.map((g) => g.id), ['checks', 'relationships', 'tableCounts'],
    'a group whose size is not known yet is still part of what "all" runs');
  assert.equal(beforeAnyRun[1].count, null, 'and it says so by having no number, never a zero');
}

assert.equal(model.tickedRunRefusal(0), 'Tick at least one check first. Nothing is ticked, so there is nothing to run.',
  'an empty tick list is refused in one plain line, never sent as "everything"');
assert.equal(model.tickedRunRefusal(1), null);
assert.equal(model.tickedRunRefusal(4), null);

// ---------------------------------------------------------------------------------------------------
// 3. An older row keeps its own date after a partial rerun. Only what ran is replaced.
// ---------------------------------------------------------------------------------------------------
{
  const before = {
    a: { outcome: { defId: 'a', verdict: 'Pass' }, whenIso: '2026-09-10T09:00:00Z' },
    b: { outcome: { defId: 'b', verdict: 'Fail' }, whenIso: '2026-09-10T09:00:00Z' },
  };
  const after = model.foldRunResults(before, [{ defId: 'b', verdict: 'Pass' }], '2026-09-15T11:30:00Z');
  assert.equal(after.a.whenIso, '2026-09-10T09:00:00Z', 'a check that did not run keeps its own older date');
  assert.equal(after.a.outcome.verdict, 'Pass', 'a check that did not run keeps its own older result');
  assert.equal(after.b.whenIso, '2026-09-15T11:30:00Z', 'a check that ran takes the new date');
  assert.equal(after.b.outcome.verdict, 'Pass', 'a check that ran takes the new result');
  assert.notEqual(after, before, 'the fold returns a new map rather than mutating the old one');
  assert.equal(before.b.outcome.verdict, 'Fail', 'the previous map is left alone');
}

// ---------------------------------------------------------------------------------------------------
// 6 (Astra). "N comparisons differ; M match", with any cap disclosed and never implied away.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.comparisonSummary(3, 45, 48, 48), '3 comparisons differ; 45 match.');
assert.equal(model.comparisonSummary(1, 45, 46, 46), '1 comparison differs; 45 match.');
assert.equal(model.comparisonSummary(0, 48, 48, 48), 'Every comparison matches; 48 of 48.');
{
  const capped = model.comparisonSummary(3, 17, 20, 48);
  assert.match(capped, /3 comparisons differ; 17 match\./);
  assert.match(capped, /20 of 48/, 'a capped set must say how many of the whole it is showing');
  assert.doesNotMatch(capped, /^3 comparisons differ; 17 match\.$/, 'a cap must never be left unsaid');
}

// ---------------------------------------------------------------------------------------------------
// 3 + 10a. The one sentence of what differs. It names a number, never a SQL column.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.differenceSentence('12,984,201', '12,941,883', -42318),
  'The model is 42,318 lower than the answer you trust.');
assert.equal(model.differenceSentence('12,941,883', '12,984,201', 42318, 'the warehouse'),
  'The model is 42,318 higher than the warehouse.');
assert.equal(model.differenceSentence('1,204,112', '1,204,112', 0),
  'The model matches the answer you trust.');
{
  const noNumber = model.differenceSentence('318.40', 'blank', null);
  assert.match(noNumber, /blank/, 'a blank answer is said as blank, not as an empty gap');
  assert.match(noNumber, /318\.40/, 'the answer you trust is still shown when there is no difference to compute');
  assert.match(noNumber, /cannot say by how much/, 'an uncomputable difference says so instead of showing zero');
  assert.doesNotMatch(noNumber, /SQL|NULL/, 'a trusted-answer check never speaks SQL');
}

// ---------------------------------------------------------------------------------------------------
// 2. A part run earns a real letter for WHAT IT RAN. The page must say what that letter covers, name
// what did not run, and never present it as the model's overall grade. The engine stopped saying
// "Partial" (cp/tests-engine-a (b)), so neither does the page.
// ---------------------------------------------------------------------------------------------------
{
  const one = model.partRunSummary('the 1 check you picked', ['relationships', 'table counts']);
  assert.match(one, /the 1 check you picked/, 'the letter must say exactly what it covers');
  assert.match(one, /not for the whole model/, 'and must never read as the model overall');
  assert.match(one, /Relationships and table counts did not run/,
    'what did not run is named in the engine words, and starts its sentence like a sentence');
  assert.match(one, /keep their own earlier result and date/, 'and the untouched rows are accounted for');
  assert.doesNotMatch(one, /Partial/i, 'the word Partial told a person nothing and is gone');

  const nothingSkipped = model.partRunSummary('relationships', []);
  assert.match(nothingSkipped, /relationships/);
  assert.doesNotMatch(nothingSkipped, /did not run/, 'nothing skipped means no second sentence');

  const noCover = model.partRunSummary(undefined, ['table counts']);
  assert.match(noCover, /only the part you ran/, 'an absent gradeCovers still gets an honest sentence');
}

// ---------------------------------------------------------------------------------------------------
// 4 + 6 (Astra). Relationships lead with meaning: attention, not a vague "1 differs".
// ---------------------------------------------------------------------------------------------------
assert.equal(model.relationshipAttention(1), '1 relationship needs attention');
assert.equal(model.relationshipAttention(3), '3 relationships need attention');
assert.equal(model.relationshipAttention(0), 'Nothing needs attention');

// ---------------------------------------------------------------------------------------------------
// 7 (Astra 3). A total-only comparison is limited evidence and says so before the last click.
// ---------------------------------------------------------------------------------------------------
{
  const note = model.limitedEvidenceNote(0);
  assert.ok(note, 'a total-only comparison must carry a note');
  assert.match(note, /limited evidence/);
  // Fable's wording, decided against the engine's ReconcileContract: groupBy is 'required for a
  // verdict', so the cost of leaving it empty is a VERDICT CEILING, not a vague weakness.
  assert.match(note, /A comparison with no groups can show a matching total but can never pass as verified\./);
  assert.equal(model.limitedEvidenceNote(2), null, 'a grouped comparison needs no such warning');
}

// ---------------------------------------------------------------------------------------------------
// 5. The table-count footer, word for word.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.TABLE_COUNT_FOOTER,
  '"Counts differ" means matching snapshots were not confirmed, so this alone does not prove missing rows.',
  'the footer is the exact approved wording; it must never imply timing explains the difference');

// No em dash anywhere in the reading rules.
assert.doesNotMatch(readFileSync(resolve(root, 'webview/src/tests-model.ts'), 'utf8'), /—/, 'no em dashes in user-facing copy');

console.log('Tests page reading rules passed');
