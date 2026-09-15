// The shape of Checks > Tests after the approved redesign (tests-redesign.html, 2026-09-15).
// One tool row, one status line, ONE list of saved checks, a Relationships card, a Table row counts card
// and a History card. Security and the Model interview are gone from this page.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const tests = read('webview/src/tests.tsx');
const authoring = read('webview/src/tests-authoring.tsx');
const app = read('webview/src/App.tsx');
const interview = read('webview/src/interview.tsx');

// ---------------------------------------------------------------------------------------------------
// 8. What leaves the page.
// ---------------------------------------------------------------------------------------------------
assert.doesNotMatch(tests, /'security'/, 'the Security sub-tab must be gone from Tests');
assert.doesNotMatch(tests, /SecurityReportW|RoleFilterResultW|RoleOlsW/, 'the Security wire shapes must leave with the tab');
assert.doesNotMatch(tests, /function Security\(|function RoleCard\(/, 'the Security panel and role cards must be deleted, not hidden');
assert.doesNotMatch(tests, /<InterviewCard/, 'the Model interview block must leave the Tests page');
assert.doesNotMatch(tests, /SuiteInterviewEvidence|run\?\.interview|interviewNote/, 'a Tests run no longer carries interview evidence');
assert.doesNotMatch(authoring, /'interview'/, 'New check must not offer an Interview question');
assert.doesNotMatch(authoring, /addInterviewQuestion|runInterview/, 'the interview drawer leaves the Tests authoring file');
assert.doesNotMatch(interview, /View in Tests/, 'the AI understanding page must not link into Tests any more');
assert.doesNotMatch(tests, /Run \+ record/, 'the separate Run + record button is replaced by Record it');
assert.match(interview, /export function InterviewCard/, 'the interview component itself stays for the AI understanding page');

// ---------------------------------------------------------------------------------------------------
// 1. One 40px tool row.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /sem-tests-toolrow/, 'the page must have one named tool row');
assert.match(tests, /Run all enabled checks/, 'Run says exactly what it runs');
assert.match(tests, /New check/, 'the authoring entry is New check');
assert.match(tests, />Report/, 'Report is its own menu');
assert.match(tests, /Latest/, 'Report offers the latest run');
assert.match(tests, /Saved reports/, 'Report offers the saved library');
assert.match(tests, /Runs on /, 'the tool row names the test model it runs on');
assert.match(tests, /SQL sources: /, 'the tool row counts the saved SQL sources');
assert.match(tests, /Add SQL source/, 'the tool row offers to add one');
assert.match(tests, /openSqlSources\(openConnections\)/, 'Add SQL source uses the Connections lane own helper');
assert.doesNotMatch(tests, /'sqlsources'/, 'and never restates the section id this page does not own');
assert.match(tests, /listSqlSources\(\)/, 'the count comes from the engine, through the one shared reader');
// The hub opens OVER the drawer. Nothing here closes it and nothing reopens it, so a half-filled check
// is exactly as it was when the hub goes away. The list is re-read at the only moment it can change.
assert.doesNotMatch(tests, /addSqlSource[\s\S]{0,120}closeAuthor/, 'adding a source must not close the open check drawer');
assert.match(tests, /hubWasOpen\.current && !connectionsOpen/, 'the sources are re-read when the hub closes');
assert.match(tests, /latestSources=\{sources\}/, 'and the open drawer is handed the fresh list');

// ---------------------------------------------------------------------------------------------------
// 1. The scope menu lists what "all" includes and refuses an empty tick list.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /runScopeGroups\(/, 'the scope menu is built from the groups that actually have something to run');
assert.match(tests, /tickedRunRefusal\(/, 'ticking nothing is refused in the page, before the request is sent');
assert.doesNotMatch(tests, /sections=\{\[sub\]\}|sections: \[sub\]/, 'the open sub-tab name is no longer sent as a section');

// ---------------------------------------------------------------------------------------------------
// 2. One status line.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /sem-tests-status/, 'one status line, not a wall of tiles');
assert.doesNotMatch(tests, /function Kpi\(|function OverviewBand\(/, 'the six-tile overview band is replaced by the status line');
assert.match(tests, /pass · /, 'the status line reads N of M pass, then differ, then could not check');
assert.match(tests, /could not check/, 'unknowns stay distinct from failures on the status line');
assert.match(tests, /coveragePct/, 'coverage comes from the engine and sits beside the grade');
assert.match(tests, /not recorded/, 'an unrecorded run says so');
assert.match(tests, /Record it/, 'and offers to record itself');
assert.match(tests, /partRunSummary\(run\.scope\?\.gradeCovers, run\.scope\?\.skipped/,
  'a part run says what its letter covers and what did not run, in the engine own words');
assert.doesNotMatch(tests, /Partial/i, 'the engine stopped saying Partial, so the page must not say it either');
assert.match(tests, /scope\?\.gradeCovers/, 'a part run shows its real letter with what it covers beside it');

// ---------------------------------------------------------------------------------------------------
// 3. ONE list of saved checks.
// ---------------------------------------------------------------------------------------------------
assert.doesNotMatch(tests, /function SavedTestsList\(/, 'the separate saved-definition list is folded into the one list');
assert.doesNotMatch(tests, /function Measures\(/, 'there is no Measures section any more');
assert.match(tests, /function SavedChecks\(/, 'one list of saved checks');
assert.match(tests, /resultChip\(/, 'each row shows one of the five result chips');
assert.match(tests, /Search checks and results/, 'search acts on the one list');
assert.match(tests, /chipCounts/, 'the filter chips count the rows actually shown');
assert.match(tests, /Run this one/, 'a row can run just itself');
assert.match(tests, /Turn off/, 'a row can be turned off');
assert.match(tests, /Open in DAX Lab/, 'an opened row offers the DAX Lab handoff');
assert.match(tests, /Show the differing comparisons/, 'an opened row offers its differing comparisons');
assert.match(tests, /Edit this check/, 'an opened row offers Edit');
assert.match(tests, /differenceSentence\(/, 'an opened row leads with one sentence of what differs');
assert.match(tests, /comparisonSummary\(differCount, matchCount/, 'an opened row counts the comparisons and discloses any cap');
assert.match(tests, /outcome\?\.mismatches \?\? differing\.length/, 'the counts come from the whole comparison, not from the handful of rows kept with it');
assert.match(tests, /outcome\?\.matches \?\?/, 'the same for the matching side');

// 10b. Turn off and Delete show their error in the row. Never a swallowed catch.
assert.doesNotMatch(tests, /'saveTest', \{ \.\.\.def, enabled: !def\.enabled \}\)\.then\(\(\) => announceTestSuiteChanged\(\)\)\.catch\(\(\) => undefined\)/,
  'turning a check off must not swallow its save error');
assert.match(tests, /setRowError\(/, 'a failed row action writes its error into that row');
assert.match(tests, /rowError\[/, 'and the row renders it');

// 10a. A trusted-answer failure never shows SQL columns.
assert.match(tests, /kind === 'measureValue'/, 'the detail panel branches on the check kind');
assert.doesNotMatch(tests, /emptyLabel="NULL"/, 'the generic SQL compare table must not render a trusted-answer result');
assert.match(tests, /outcome\.expected/, 'a trusted-answer result reads the engine expected field');
assert.match(tests, /outcome\.actual/, 'and the engine actual field');
assert.match(tests, /outcome\.difference/, 'and the engine difference field');

// ---------------------------------------------------------------------------------------------------
// 4. Relationships in plain words.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /Rows that match/, 'relationships speak plain words, not referential integrity');
assert.match(tests, /Lookup values unique/, 'plain words for key uniqueness');
assert.match(tests, /Column types compatible/, 'plain words for data types');
assert.doesNotMatch(tests, /Referential integrity|orphan rows/, 'the specialist words leave the card');
// CHANGED 2026-09-15 (Astra, finding 5, confirmed live on Kane's Fabric model): the card said "N need
// attention, M fine", where "fine" was everything minus the failures, so relationships whose checks were
// all UNKNOWN were counted as fine and the header still read PASS. Unknowns are their own count now.
assert.match(tests, /relationshipSummary\(/, 'the card summarises attention, passes and unknowns separately');
assert.match(tests, /relationshipChip\(/, 'and its header chip cannot say Pass over an unchecked relationship');
assert.match(tests, /Show details/, 'each relationship can open its own detail');

// ---------------------------------------------------------------------------------------------------
// 5. Table row counts, with one identity for a mapping.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /function TableRowCounts\(/, 'table row counts have their own card');
assert.match(tests, /Map source/, 'an unmapped table offers Map source');
assert.match(tests, /Counts differ/, 'a difference is said as Counts differ, never as a proven fault');
assert.match(tests, /Not checked/, 'an unmapped table is not checked');
assert.match(tests, /TABLE_COUNT_FOOTER/, 'the card carries the exact approved footer');
assert.match(tests, /openTableMapping/, 'Map source and New check > Table row count open the same setup');
assert.match(authoring, /tableRowCount/, 'the drawer knows the table row count kind');

// ---------------------------------------------------------------------------------------------------
// 6. History opens a run's evidence.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /rpc<TestRunDetailW>\('getTestRun'/, 'History opens a recorded run through get_test_run');
assert.match(tests, /scored the old way, before access rules left this page/,
  'a run recorded before the change is labelled rather than shown as comparable');
assert.match(tests, /Saved reports \{|Saved reports/, 'the History card links to Saved reports');
assert.doesNotMatch(tests, /function HistoryChart\(/, 'the unopenable trend chart is replaced by a dated list');

// 10c + Astra point 2. Record keeps the run ON SCREEN. Recording by re-running would store numbers
// nobody looked at, against data that may have moved since.
assert.match(tests, /rpc<TestRunRecordResultW>\('recordTestRun', run\.runId\)/, 'Record saves the run being shown');
assert.doesNotMatch(tests, /onRecord=\{\(\) => runEverything\(true\)\}/, 'Record must not be a re-run in disguise');
assert.match(tests, /result\.recorded/, 'a refused recording is read from the result, not assumed');
assert.match(tests, /recordNote/, 'and its reason is shown');
assert.match(tests, /if \(result\.recorded\) \{[\s\S]{0,160}loadHistory\(\)/, 'a successful recording reloads the history it just changed');

// The old-grade label belongs to the opened run: listTestRuns strips outcomes from EVERY row, so a
// missing outcomes field on a list row proves nothing about how that row was scored.
assert.match(tests, /detail\.gradedBeforeSecurityLeftTests/, 'the old-grade label reads the engine flag');
assert.doesNotMatch(tests, /record\.outcomes == null &&/, 'a list row must not infer the old scoring from a stripped field');

// Astra point 7 is buildable now: the engine returns the unmatched values.
assert.match(tests, /rpc<UnmatchedRowsW>\('getUnmatchedRows', rel\.name, 200\)/, 'Show details reads the real unmatched values');
assert.match(tests, /This reads the data as it is now/, 'and discloses that it is reading current data');
assert.match(tests, /All but \$\{count\.toLocaleString\(\)\} match/, 'the Rows that match column must state the matching state, not the opposite one');

// The segment strip renames Results to Saved reports.
assert.match(app, /id: 'evidence', label: 'Saved reports'/, 'the Checks strip names the library Saved reports');
assert.doesNotMatch(app, /id: 'evidence', label: 'Results'/, 'Results was the name Astra found misleading');

// ---------------------------------------------------------------------------------------------------
// 9. The states.
// ---------------------------------------------------------------------------------------------------
assert.match(tests, /No saved checks yet/, 'the first visit says there are none');
// CHANGED 2026-09-15 (Astra, finding 3): "automatically on every run" was not true of a run that ticked
// saved checks, which skips both automatic families. The sentence now says when they actually run.
assert.match(tests, /Relationships and table row counts are checked when you run everything/, 'and that automatic checks still exist');
assert.doesNotMatch(tests, /checked automatically on every run|Checked on every run/, 'nothing promises they always run');
assert.match(tests, /Connect a test model/, 'a result that needs a live model offers to connect one');
assert.match(tests, /Loading/, 'there is a loading state');

console.log('Tests page surface contract passed');
