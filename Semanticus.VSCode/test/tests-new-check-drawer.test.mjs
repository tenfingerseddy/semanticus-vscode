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
assert.match(authoring, /limitedEvidenceNote\(/, 'a total-only comparison says what it leaves out');
assert.doesNotMatch(authoring, /Source SQL endpoint saved with this check/, 'endpoints are no longer typed per check');
assert.doesNotMatch(authoring, /Use the detected endpoint/, 'the per-check endpoint override is replaced by a named source');
assert.doesNotMatch(authoring, /reviewReconcileMapping/, 'the drawer no longer detects an endpoint it cannot save anywhere shared');

// ---------------------------------------------------------------------------------------------------
// 7. Table row count shows the table and its mapping fields, and is the SAME thing as Choose source.
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
// CHANGED 17 Oct 2026 (UAT 1.5.104): the drawer owns adding a source. A small form inside it saves the source and the picker adopts
// exactly that one, so there is no baseline to compare and no trip to Connections. Query opens this drawer with no handler at all.
assert.match(authoring, /import \{ SqlSourceAddForm, type AddedSource \} from '\.\/tests-addsource';/, 'the add form is part of the drawer');
assert.match(authoring, /const sourceAdded = \(added: AddedSource, keepOpen: boolean, connected\?: string\) =>/, 'the drawer learns which source was added');
assert.match(authoring, /setAdopt\(added\.id\)/, 'and the picker chooses exactly that one for the person');
assert.match(authoring, /if \(adopt\) setPicked\(adopt\)/, 'the check form takes it');
assert.doesNotMatch(authoring, /onAddSqlSource\?\.\(\)/, 'no handler is needed from outside: a drawer opened from Query adds a source too');
assert.doesNotMatch(authoring, /<option value=\{ADD\}>|const ADD = /, 'adding is a button, never an item in the list, so moving over the list opens nothing');
assert.match(authoring, /data-testid="check-add-source-open"/, 'the button under the list');

// UAT 1.5.104 (17 Oct): the check form. Each line is a thing a person met: a result off screen, a form that moved while they typed, a
// blank rule with no default, a box cut off at the edge, a SQL box that started empty, a source nobody chose.
assert.match(authoring, /data-testid="author-result"/, 'the answer to Try it now sits under the buttons, outside the scrolling form');
assert.doesNotMatch(authoring, /\{err && <div className="mb-3 rounded border/, 'and no longer at the top of a long form where it was off screen');
assert.match(authoring, /data-testid="draft-row"/, 'the draft row exists even with no draft');
assert.doesNotMatch(authoring, /function draftRow\([^)]*\) \{\s*if \(!show\) return null;/, 'the draft row keeps its height, so the form never moves under the cursor');
assert.match(authoring, /blankPolicy: p\.blankPolicy \|\| 'zero'/, 'a blank counts as zero until the person says otherwise (a stated default)');
assert.doesNotMatch(authoring, /There is no sensible default/, 'no "pick one, there is no default" wall');
assert.match(authoring, /placeholder="Value" className="min-h-7 min-w-0 flex-1/, 'the Value box can shrink, so the form never scrolls sideways');
assert.match(authoring, /defaultSqlSourceId\(linkedSourceId, sources\)/, 'a new check starts on the model\'s own source');
assert.match(authoring, /guessSumSql\(measureExpression, mappings \?\? \[\], sqlSourceId, groupBy, links\)/, 'and a plain SUM starts the SQL as a guess');
assert.match(authoring, /const sqlSourceId = pickedSourceId \|\| \(modelTable \? defaultSqlSourceId\(linkedSourceId, sources\) : ''\);/, 'a table row count starts on the model\'s own source too, shown and not stored');
assert.match(authoring, /title: sqlTitleFor\(measureRef, groupBy\)/, 'a grouped check says its groups in its title, so the total and the one by Brand are not twins');
assert.match(authoring, /\$\{name\}\$\{groups\.length \? ' by ' \+ groups\.join\(' and '\) : ''\} ties to source SQL/, 'Net Sales by Brand ties to source SQL');
assert.match(authoring, /onSourcesChanged\?\.\(\);/, 'adding a source asks the page to read its sources again');
assert.doesNotMatch(authoring.slice(authoring.indexOf('const sourceAdded')), /^[\s\S]{0,700}announceTestSuiteChanged\(\)/, 'and never says a check changed, because none did');
assert.match(authoring, /className="sticky -bottom-3 z-10 -mx-4 -mb-3 mt-3 flex flex-wrap items-center gap-2 px-4 pb-3 pt-2"/, 'Try it now and Save stay in reach at the bottom of the form, and cover the form padding so no chip shows under them (round 2 N10)');
assert.match(authoring, /saveLabel="Save check"/, 'the row count form saves a check, not a source (round 2 N28)');
assert.doesNotMatch(authoring, /saveLabel="Save source"/, 'no Save source button');
assert.match(readFileSync(resolve(root, 'webview/src/tests-addsource.tsx'), 'utf8'), /connectedLine\(added\.name\)/, 'Save and check says it connected (round 2 N29)');
assert.match(authoring, /setTryNote\(connected \?\? null\)/, 'and the drawer shows that line after the form closes');
assert.match(authoring, /expectedShown = typedExpected \? expected : shownNumber\(expected, formatString\)/, 'a handed-over ratio is shown in the measure\'s format (round 2 N11)');
assert.match(authoring, /distinctTitle\(/, 'a new check takes a name no saved check has (round 2 N15)');
assert.match(authoring, /rpc<[^>]*>\('getModelGraph'\)/, 'the SQL guess reads the model links to join the groups');

console.log('New check drawer contract passed');
