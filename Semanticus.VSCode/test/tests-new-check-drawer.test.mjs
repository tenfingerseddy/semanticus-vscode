// The New check drawer after the approved redesign. Kind first, then the thing and the answer you trust.
// Everything a person MUST decide is visible; only tuning is behind Advanced. The required/optional split
// is the engine's own ReconcileContract (lane cp/tests-engine-b), not a guess made here.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const authoring = read('webview/src/tests-authoring.tsx');
const testsSource = read('webview/src/tests.tsx');

// ---------------------------------------------------------------------------------------------------
// 7. The kind chooser comes first, and names what you are doing.
// ---------------------------------------------------------------------------------------------------
assert.match(authoring, /function KindChooser\(/, 'the drawer opens on a kind chooser');
assert.match(authoring, /Trusted answer/, 'kind one');
assert.match(authoring, /A number you know is right/, 'and what it means');
assert.match(authoring, /Compare with source/, 'kind two');
assert.match(authoring, /The same number from SQL/, 'and what it means');
assert.match(authoring, /Table row count/, 'kind three');
assert.match(authoring, /Model rows against source rows/, 'and what it means');
assert.doesNotMatch(authoring, /Expected total/, 'the old engine-flavoured name is gone');
assert.doesNotMatch(authoring, /Match source SQL/, 'and so is the other one');
assert.doesNotMatch(authoring, /Interview question/, 'New check no longer offers an interview question');

// ---------------------------------------------------------------------------------------------------
// 7. Trusted answer: no grouping, no timing budget.
// ---------------------------------------------------------------------------------------------------
assert.doesNotMatch(authoring, /Timing budget/, 'the engine never applies a timing budget to a trusted answer');
assert.doesNotMatch(authoring, /budgetMs: budget/, 'and the drawer must stop saving one');
assert.match(authoring, /Which calculation/, 'the trusted-answer form starts with the calculation');
assert.match(authoring, /The number you trust/, 'then the answer you trust');
assert.match(authoring, /Only where \(optional\)/, 'then the optional narrowing');
assert.match(authoring, /Where this number came from \(optional\)/, 'then where it came from');

// Advanced holds tolerance and the complete DAX filter, and nothing a person must decide.
assert.match(authoring, /function Advanced\(/, 'there is one Advanced disclosure');
assert.match(authoring, /tolerance and a complete DAX filter/, 'Advanced says what is inside it');

// The DAX Lab handoff choice is untouched.
assert.match(authoring, /Which formula is this check about\?/, 'the DAX Lab saved-versus-edited choice stays exactly as today');
assert.match(authoring, /Test the measure as saved/, 'both options stay');
assert.match(authoring, /Test this expression/, 'both options stay');

// ---------------------------------------------------------------------------------------------------
// 7. Compare with source shows every required decision, visible, not hidden.
// ---------------------------------------------------------------------------------------------------
assert.match(authoring, /SQL source/, 'the SQL source picker is a first-class field');
assert.match(authoring, /Add a SQL source…/, 'and the list ends with a way to add one');
assert.match(authoring, /listSqlSources/, 'the picker reads the saved sources from the engine');
assert.match(authoring, /sqlSourceId/, 'the check references its source by stable id, never by name');
assert.match(authoring, /A blank in the model means/, 'the blank meaning stays a visible choice with no default');
assert.match(authoring, /Which groups to compare/, 'grouping is visible: without it a check can never do better than could not check');
assert.match(authoring, /Your SQL must use the same filter as the check/, 'the one line that stops two different questions being compared');
assert.match(authoring, /limitedEvidenceNote\(/, 'a total-only comparison is called limited evidence');
assert.doesNotMatch(authoring, /Source SQL endpoint saved with this check/, 'endpoints are no longer typed per check');
assert.doesNotMatch(authoring, /Use the detected endpoint/, 'the per-check endpoint override is replaced by a named source');
assert.doesNotMatch(authoring, /reviewReconcileMapping/, 'the drawer no longer detects an endpoint it cannot save anywhere shared');

// ---------------------------------------------------------------------------------------------------
// 7. Table row count shows the table and its mapping fields, and is the SAME thing as Map source.
// ---------------------------------------------------------------------------------------------------
assert.match(authoring, /function TableRowCountForm\(/, 'the third kind has its own form');
assert.match(authoring, /Which table/, 'it starts with the model table');
assert.match(authoring, /setTableSourceMapping/, 'saving it writes a table mapping, not an unsupported saved-test kind');
assert.doesNotMatch(authoring, /kind: 'tableRowCount'/, 'the engine refuses a tableRowCount saved-test kind by name');
assert.match(authoring, /Schema/, 'the mapping names the schema');
assert.match(authoring, /Table in the source/, 'and the source table');

// ---------------------------------------------------------------------------------------------------
// 7. Pro or storage requirements are stated before the last click.
// ---------------------------------------------------------------------------------------------------
assert.match(authoring, /function SaveRequirement\(/, 'the requirement is a named block');
// Not "saving needs Pro" any more: Tests is one WHOLE Pro feature from 2026-09-15, so a person on the free
// plan never reaches this drawer at all (App renders the Tests preview instead). What is left to state
// before the last click is the storage fact, which is true on every plan.
assert.doesNotMatch(authoring, /Saving a check needs Semanticus Pro/,
  'Tests is Pro as a whole; a per-action Pro line here would contradict the feature line');
assert.match(authoring, /kept in a small file beside this model/, 'and the storage fact is still stated plainly');
assert.match(authoring, /<SaveRequirement[\s\S]{0,600}<Footer/, 'it sits above the Save button, never after it');

// ---------------------------------------------------------------------------------------------------
// 10d. Escape from a row action closes the drawer.
// ---------------------------------------------------------------------------------------------------
assert.doesNotMatch(testsSource, /onKeyDown=\{\(event\) => event\.stopPropagation\(\)\}/,
  'a row action must not swallow the keydown the drawer listens for');
assert.match(testsSource, /stopRowToggle/, 'a row action stops the row toggling without deafening the window');

// ---------------------------------------------------------------------------------------------------
// Add a SQL source: the hub opens over the drawer, the drawer stays exactly as it was, and the picker
// adopts the new record only when there is no doubt about which one it is.
// ---------------------------------------------------------------------------------------------------
// CHANGED 2026-09-15 (Astra, finding 1, P1): the drawer compared the incoming list against its OWN initial
// empty state, so its first load looked like a source appearing while the hub was open and one saved source
// replaced an existing check's binding. The comparison is now against a baseline captured when Add is
// INVOKED. sourceAddedSinceAdd and sqlSourceBinding are run, not matched, in tests-fixes.test.mjs.
assert.match(authoring, /sourceAddedSinceAdd\(baseline, latestSources\.map/, 'the drawer decides whether a new source is unambiguous');
assert.match(authoring, /addBaseline\.current = sources\.map\(\(s\) => s\.id\)/, 'and only against the list as it was when Add was chosen');
assert.match(authoring, /if \(adopt\) setPicked\(adopt\)/, 'an unambiguous one is selected for the person');
assert.doesNotMatch(authoring, /onAddSqlSource\?\.\(\);[\s\S]{0,60}onClose/, 'adding a source must never close the drawer');

console.log('New check drawer contract passed');
