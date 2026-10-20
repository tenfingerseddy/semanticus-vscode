// Review > Validation C (1.6; Kane picked it from prototype round 2 on 15 Oct): the pure half is run as behaviour, the page
// is held to its feature list and its layout rules. The feature list is every feature of the Validation page before this
// change; each one has a home, and the report says where. A guard test without the happy path is not enough, so the model
// half is driven with the engine's real result shapes: a failing compare check, a trusted answer, relationships, counts,
// and eight recorded runs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const load = async (file) => {
  const out = ts.transpileModule(read(file), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(out, 'utf8').toString('base64')}`);
};
const M = await load('webview/src/validation-model.ts');
const page = read('webview/src/tests.tsx');
const css = read('webview/src/validation.css');

// ---------------------------------------------------------------------------------------------------
// The four questions, in order, and where each kind of check lives.
// ---------------------------------------------------------------------------------------------------
assert.deepEqual(M.GROUPS.map((g) => g.question), [
  'Do my numbers match their sources?', 'Did my changes move anything?', 'Do the trusted answers still hold?', 'Is the data whole?',
]);
assert.equal(M.groupOfKind('measureValue'), 'trusted');
assert.equal(M.groupOfKind('measureReconcile'), 'sources');

// ---------------------------------------------------------------------------------------------------
// Numbers: only what the engine sent, and a number is never invented from words.
// ---------------------------------------------------------------------------------------------------
assert.equal(M.parseNumber('2,646,905'), 2646905);
assert.equal(M.parseNumber('$1,204.50'), 1204.5);
assert.equal(M.parseNumber('41.8%'), 0.418, 'a percent becomes a share');
assert.equal(M.parseNumber('(1,234)'), -1234);
assert.equal(M.parseNumber('blank'), null, 'a word is not a number');
assert.equal(M.parseNumber(''), null);
assert.equal(M.parseNumber(null), null);
assert.equal(M.fmtFor([1, 2, 3]), 'int');
assert.equal(M.fmtFor([1, 2.5]), 'dec', 'a number that is not whole keeps its places');
assert.equal(M.fmtFor([0.4], true), 'pctx');

// ---------------------------------------------------------------------------------------------------
// A compare-with-source check: one pair per place, the differing ones first, the rules' pick among what fits.
// ---------------------------------------------------------------------------------------------------
const compare = {
  verdict: 'Fail', expected: '18,185,050.80', actual: '18,142,732.76', difference: -42318.04, rowsTotal: 48, mismatches: 3, matches: 45,
  rows: [
    { context: 'Australia', sql: 4821903.44, dax: 4821903.44, delta: 0, verdict: 'Pass' },
    { context: 'Grand total', sql: 18185050.8, dax: 18142732.76, delta: -42318.04, verdict: 'Fail', grandTotal: true },
    { context: 'Europe', sql: 5206771.2, dax: 5169048.82, delta: -37722.38, verdict: 'Fail' },
  ],
};
const cv = M.checkVisual(compare, { title: 'Net Revenue ties to finance extract', sourceLabel: 'the source', trusted: false });
assert.ok(cv.data, 'a failing compare check has a visual');
assert.deepEqual(cv.data.rows.map((r) => r.label), ['Grand total', 'Europe', 'Australia'], 'the differing comparisons come first');
assert.equal(cv.data.vLabel, 'The model');
assert.equal(cv.data.v2Label, 'The source', 'the source is named with a capital in a table head');
assert.equal(cv.rulesPick, 'dumbbell');
assert.ok(cv.fits.includes(cv.rulesPick), 'the rules pick is always one of the visuals that fit');
assert.ok(cv.fits.includes('table'), 'the table is always offered');
const manyRows = { ...compare, rows: Array.from({ length: 20 }, (_, i) => ({ context: 'Place ' + i, sql: 100, dax: 100 + i, verdict: i ? 'Fail' : 'Pass' })) };
assert.equal(M.checkVisual(manyRows, { title: 'T', sourceLabel: 'the source', trusted: false }).rulesPick, 'variance', 'twenty pairs read as gaps');
// A match the engine accepted is never drawn as a difference, and a mismatch is never drawn as a match: each row carries the
// engine's own verdict, and no allowance is worked out from the numbers here.
const absolute = M.checkVisual({ verdict: 'Pass', rows: [
  { context: 'A', sql: 1000, dax: 1000.4, verdict: 'Pass' }, { context: 'B', sql: 2000, dax: 2000, verdict: 'Pass' }] },
{ title: 'T', sourceLabel: 'the source', trusted: false });
assert.deepEqual(absolute.data.rows.map((r) => r.differs), [false, false], 'a passed row is not drawn red');
assert.deepEqual(cv.data.rows.map((r) => r.differs), [true, true, false], 'the engine\'s verdicts travel with the rows');
// Two counts that differ are never offered as Bars alone: Bars draw one number and hide the gap.
assert.ok(!cv.fits.includes('bars'), 'differing pairs are not offered Bars');
assert.ok(M.checkVisual(absolute.data ? { verdict: 'Pass', rows: [{ context: 'A', sql: 5, dax: 5, verdict: 'Pass' }, { context: 'B', sql: 6, dax: 6, verdict: 'Pass' }] } : null,
  { title: 'T', sourceLabel: 's', trusted: false }).fits.includes('bars'), 'equal pairs may still be Bars');
const diffCounts = M.countsVisual([{ table: 'Sales', model: 100, source: 90 }, { table: 'Date', model: 5, source: 5 }]);
assert.ok(!diffCounts.fits.includes('bars') && diffCounts.fits.includes('variance') && diffCounts.fits.includes('dumbbell'), 'counts that differ fit variance bars and the dumbbell');
// The kept comparisons are only the worst few: a run's share comes from its totals, and an older run without them has no share to claim.
assert.equal(M.howFarOff({ kind: 'savedCheck', name: 'N', verdict: 'Fail', contextsTotal: 500, mismatches: 30, contexts: Array.from({ length: 20 }, () => ({ verdict: 'Fail' })) }), 0.06);
assert.equal(M.howFarOff({ kind: 'savedCheck', name: 'N', verdict: 'Fail', contextsTotal: 500, contexts: Array.from({ length: 20 }, () => ({ verdict: 'Fail' })) }), null, 'an older run that kept only the worst rows has no honest share');
// A relationship with orphan rows is marked and says how many.
const orphans = M.relationshipVisual([{ name: 'r', manyTable: 'Sales', oneTable: 'Customer', manyRowCount: 2000000, refVerdict: 'Fail', refCount: 5 }]);
assert.equal(orphans.data.rows[0].differs, true);
assert.equal(orphans.data.rows[0].diffText, '5 not matched');
assert.ok(!orphans.fits.includes('bars'));

// Unknowns: a missing calculation, and a check that never ran, have nothing to draw. Nothing is made up.
assert.equal(M.checkVisual(null, { title: 'T', sourceLabel: 's', trusted: false }).data, null);
assert.equal(M.checkVisual({ missing: true }, { title: 'T', sourceLabel: 's', trusted: false }).data, null);
assert.equal(M.checkVisual({ verdict: 'NotVerifiable', expected: 'blank', actual: 'blank', rows: [] }, { title: 'T', sourceLabel: 's', trusted: true }).data, null, 'blank is not a number');

// A trusted answer: one number against the number the person trusts.
const trusted = M.checkVisual({ verdict: 'Fail', expected: '1,234', actual: '1,200', difference: -34 }, { title: 'Total Sales 2024', sourceLabel: 'x', trusted: true });
assert.equal(trusted.rulesPick, 'total');
assert.equal(trusted.data.total.value, 1200);
assert.equal(trusted.data.total.prior, 1234);
assert.equal(trusted.data.v2Label, 'The answer you trust', 'a trusted answer never borrows SQL words');
assert.deepEqual(trusted.fits, ['total', 'bullet', 'dumbbell', 'table']);

// ---------------------------------------------------------------------------------------------------
// Relationships and table row counts.
// ---------------------------------------------------------------------------------------------------
const rel = M.relationshipVisual([
  { name: 'a', manyTable: 'Sales', oneTable: 'Customer', manyRowCount: 10000, refVerdict: 'Fail', refCount: 1847 },
  { name: 'b', manyTable: 'Sales', oneTable: 'Date', refVerdict: 'Pass' },
  { name: 'c', manyTable: 'Budget', oneTable: 'Scenario', refVerdict: 'NotVerifiable' },
]);
assert.equal(rel.data.rows[0].v, 1 - 1847 / 10000, 'the share of rows that match comes from the engine\'s own count');
assert.equal(rel.data.rows[1].v, 1);
assert.equal(rel.data.rows[2].v, null, 'a relationship that could not be checked has no bar');
assert.equal(M.relationshipVisual([{ name: 'c', manyTable: 'B', oneTable: 'S', refVerdict: 'NotVerifiable' }]).data, null, 'all unknown draws nothing');
const counts = M.countsVisual([
  { table: 'Sales', model: 1236402, source: 1236402 }, { table: 'Returns', model: 88410, source: 88405 }, { table: 'Promotion', model: null, source: null },
]);
assert.equal(counts.data.rows.length, 2, 'only counted tables are drawn');
assert.equal(counts.rulesPick, 'variance', 'a count that differs is drawn as a gap');
assert.equal(M.countsVisual([{ table: 'Sales', model: 5, source: 5 }]).rulesPick, 'bars');

// ---------------------------------------------------------------------------------------------------
// Eight runs, small multiples; and Before and after.
// ---------------------------------------------------------------------------------------------------
const run = (i, net) => ({
  runId: 'r' + i, when: `2026-09-${String(i + 1).padStart(2, '0')}`,
  checks: [
    { kind: 'savedCheck', name: 'Net Revenue', verdict: net ? 'Fail' : 'Pass', contexts: Array.from({ length: 10 }, (_, k) => ({ verdict: k < net ? 'Fail' : 'Pass' })) },
    { kind: 'savedCheck', name: 'Total Sales', verdict: 'Pass' },
    { kind: 'relationship', name: 'S to C', check: 'ReferentialIntegrity', verdict: net > 2 ? 'Fail' : 'Pass' },
    { kind: 'relationship', name: 'S to D', check: 'ReferentialIntegrity', verdict: 'Pass' },
    { kind: 'tableRowCount', name: 'Returns', verdict: 'NotVerifiable' },
  ],
});
const eight = [7, 6, 5, 4, 3, 2, 1, 0].map((i) => run(i, i)).reverse().reverse();   // newest first: i=7 is the newest
const hist = M.historyVisual(eight);
assert.equal(hist.rulesPick, 'multiples');
const names = hist.data.rows.map((r) => r.label);
assert.ok(names.includes('Net Revenue') && names.includes('Total Sales') && names.includes('Relationships'), 'a relationship joins its family');
assert.ok(!names.includes('Table row counts'), 'a check that was only ever unknown has no series: an unknown is not a number');
const net = hist.data.rows.find((r) => r.label === 'Net Revenue');
assert.equal(net.series.length, 8);
assert.deepEqual(net.series, [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7], 'oldest to newest, the share of comparisons that differ');
assert.equal(hist.data.rows[0].label, 'Net Revenue', 'a check that differs now comes first');
assert.equal(net.note, 'Differs now');
assert.equal(M.historyVisual([run(0, 0)]).data, null, 'one run is not a history');
const ch = M.changesVisual(eight);
assert.equal(ch.rulesPick, 'dumbbell');
assert.equal(ch.data.rows.find((r) => r.label === 'Net Revenue').v, 0.7);
assert.equal(ch.data.rows.find((r) => r.label === 'Net Revenue').v2, 0.6);
assert.equal(M.movedCount(ch) >= 1, true);
assert.equal(M.changesVisual([run(0, 0)]).data, null, 'before and after needs two recorded runs');

// ---------------------------------------------------------------------------------------------------
// Jev's pick: one ask names every check with something to draw; Jev picks only among what fits.
// ---------------------------------------------------------------------------------------------------
const one = (id, fits, rulesPick) => ({ id, title: id, measure: 'm', kindWord: 'Check', status: 'differs', rows: 3, hasPrior: false, runs: 8, hasSplit: true, fits, rulesPick });
const ask = M.visualAsk([one('a', ['dumbbell', 'table'], 'dumbbell'), one('b', [], ''), one('c', ['total', 'table'], 'total')]);
assert.deepEqual(ask.checks.map((c) => c.id), ['a', 'c'], 'a check with nothing to draw is not asked about');
assert.equal(M.visualAsk([one('b', [], '')]), null, 'nothing to ask, no call');
assert.equal(M.visualAsk(Array.from({ length: 60 }, (_, i) => one('c' + i, ['table'], 'table'))).checks.length, M.MAX_VISUAL_CHECKS);
assert.ok(!JSON.stringify(ask).match(/\d{4,}/), 'the ask carries words and small counts, never a value');
const withFits = { data: {}, fits: ['dumbbell', 'variance', 'table'], rulesPick: 'dumbbell' };
let chosen = M.chosenVisual(withFits, { id: 'a', kind: 'variance', by: 'jev', reason: 'The gap is what matters here.' });
assert.deepEqual([chosen.kind, chosen.by], ['variance', 'jev'], 'a pick Jev made and that fits is drawn');
chosen = M.chosenVisual(withFits, { id: 'a', kind: 'waterfall', by: 'jev' });
assert.deepEqual([chosen.kind, chosen.by], ['dumbbell', 'rules'], 'a visual that does not fit is ignored and the rules\' pick is drawn');
chosen = M.chosenVisual(withFits, { id: 'a', kind: 'dumbbell', by: 'rules' });
assert.equal(chosen.by, 'rules', 'a pick the engine says is the rules\' stays the rules\'');
chosen = M.chosenVisual(withFits, undefined);
assert.deepEqual([chosen.kind, chosen.by, chosen.reason], ['dumbbell', 'rules', M.rulesReason('dumbbell')], 'with no answer the rules\' pick is shown, labelled as the rules\'');
assert.equal(M.chosenVisual({ data: null, fits: [], rulesPick: null }, undefined).kind, null);
for (const kind of M.VISUAL_KINDS) assert.ok(!/—/.test(M.rulesReason(kind)), 'no em dash in ' + kind);

// ---------------------------------------------------------------------------------------------------
// The one-line verdict of a group.
// ---------------------------------------------------------------------------------------------------
assert.equal(M.groupVerdict(['Differs', 'Pass', 'Pass', 'Pass']), '1 of 4 differ');
assert.equal(M.groupVerdict(['Pass', 'Pass']), 'All 2 pass');
assert.equal(M.groupVerdict(['Pass']), 'Pass');
assert.equal(M.groupVerdict(['Pass', 'Off']), '1 pass, 1 off');
assert.equal(M.groupVerdict(['Not run', 'Not run']), '2 not run yet');
assert.equal(M.groupVerdict(['Could not check', 'Pass']), '1 of 2 could not check');
assert.equal(M.groupVerdict([]), 'No checks yet');

// ---------------------------------------------------------------------------------------------------
// The page: every feature of the Validation page before the minimal redesign still has a home (Validation A, Kane 16 Oct).
// ---------------------------------------------------------------------------------------------------
const FEATURES = {
  'run everything on one button': /onClick=\{onRunAll\}>\{busy \? 'Running…' : 'Run checks'\}/,
  'the run menu lists the families': /runScopeGroups\(/,
  'choose checks to run (replaces the tick boxes)': /Choose checks…/,
  'run one family only (relationships, table counts)': /onRunSection=\{\(id\) => runSuite\(\{ sections: sectionsForGroup\(id\)/,
  'run one check': /onRunOne=\{runOne\}/,
  'new check and Add a SQL source': /<NewCheckChoices onPick=[\s\S]{0,260}onAddSqlSource=/,
  'the report of the latest run': /Report of this run/,
  'the report dialog': /<ReportExportDialog/,
  'kept reports are still the evidence route': /onOpenReports=\{\(\) => onPage\?\.\('reports'\)\}/,
  'run history and grade': /<HistoryPanel seen=\{\{ passed, differs, cannot, checked, accepted \}\} run=\{run\}/,
  'the grade cap sentence': /health\.gatedBy\.join\(' '\)/,
  'the checks that pass beside the grade, in the one unit the page counts (hero 26 N3)': /health \? `\$\{seen\.passed\.toLocaleString\(\)\} of \$\{seen\.checked\.toLocaleString\(\)\}`/,
  'record this run': /Record this run/,
  'a part run says what its letter covers': /partRunSummary\(/,
  'run progress': /<RunProgressLine progress=\{progress\} \/>/,
  'the assistant\'s own run and tool sentences': /data-assistant-run=/,
  'stale-model and stale-suite notices': /A saved check changed after this run/,
  'tick a check to run it on its own': /Tick \$\{r\.def\.title\} to run on its own/,
  'the checks that pass behind one link': /Show the \$\{passed\.toLocaleString\(\)\} that pass/,
  'edit a check': /<Item onClick=\{\(\) => \{ close\(\); props\.onEdit\(def\); \}\}>Edit<\/Item>/,
  'turn a check off or on': /\{def\.enabled \? 'Turn off' : 'Turn on'\}/,
  'delete a check with a confirmation and a visible error': /Could not delete this check/,
  'a failed turn off is said on the check': /Could not turn this check/,
  'open a check in Query': /'Open in Query'/,
  'the differing places, and all of them': /'All places'/,
  'the comparisons count and cap': /comparisonSummary\(differCount, matchCount/,
  'the sentence of what differs': /differenceSentence\(/,
  'the check\'s expected, actual, allowance and filter': /toleranceSentence\(value\?\.toleranceAbsolute/,
  'where a check was authored and where it ran': /<WhereItRan outcome=\{outcome\} \/>/,
  'the blank standoff and its setting': /action\('Choose what a blank means'/,
  'choose what a blank means from the check\'s menu': /<Item onClick=\{\(\) => \{ close\(\); props\.onEdit\(def, 'blank'\); \}\}>Choose what a blank means<\/Item>/,
  'a missing calculation': /The calculation this check was about no longer exists/,
  'connect a test model': /'Connect a test model'/,
  'relationships with details': /Show details/,
  'the values that do not match a relationship': /rpc<UnmatchedRowsW>\('getUnmatchedRows', rel\.name, 200\)/,
  'table row counts with Choose source': /Choose source/,
  'one setup for a table count mapping': /openTableMapping/,
  'the first-visit empty state: one line that starts the first check': /Start with a check: compare a number with your database, or save one you trust\./,
  'history: a dated list whose runs open to their evidence': /rpc<TestRunDetailW>\('getTestRun', runId\)/,
  'the eight-run view and before and after': /historyVisual\(recorded\)[\s\S]{0,800}changesVisual\(recorded\)|changesVisual\(recorded\)[\s\S]{0,800}historyVisual\(recorded\)/,
};
for (const [name, pattern] of Object.entries(FEATURES)) assert.match(page, pattern, `Validation A keeps: ${name}`);

// ---------------------------------------------------------------------------------------------------
// The layout rules Kane gave (16 Oct, option A): the verdict, one strip, then only what needs a look; the passes behind one link;
// one opened check at a time; the panels closed until the menu opens them; one column of whole grid columns.
// ---------------------------------------------------------------------------------------------------
assert.match(page, /<main className="sem-page vc-page" data-testid="validation-A">/, 'the page is the 12-column grid');
assert.match(page, /const \[open, setOpen\] = useState<string \| null>\(null\);/, 'no check starts open, and one opens at a time');
assert.match(page, /const \[showPass, setShowPass\] = useState\(false\);/, 'the passes start behind their link');
assert.match(page, /const \[panel, setPanel\] = useState<PanelId>\(null\);/, 'no panel starts open');
assert.match(page, /const NEEDS_A_LOOK: ResultChip\[\] = \['Differs', 'Could not check'\];/, 'the list holds what differs or could not be checked; checks not run fold into one line');
assert.match(page, /\{\(problems\.length > 0 \|\| notRunItems\.length > 0\) && <section className="sem-card va-list" data-testid="v-problems"/, 'only the problems are listed in the first view');
assert.match(page, /<span className="va-strip" data-testid="v-strip"/, 'one cell per check under the verdict');
assert.match(page, /ordered\.filter\(\(item\) => item\.chip !== 'Off' && item\.chip !== 'Not run'\)\.map\(\(item\) => <span key=\{item\.id\} className=\{`va-cell va-c-\$\{moodOf\(item\.chip\)\}`\}/, 'the strip is worst first, the same order as the list, and has a cell only for a check that has a result');
assert.match(page, /\{anyResult && <span className="va-strip" data-testid="v-strip"/, 'no strip, and no empty outline, before anything has run');
assert.match(css, /\.vc-page > \.va-col \{ grid-column: 3 \/ 11;/, 'the column is the middle eight');
assert.match(css, /@media \(max-width: 1200px\) \{ \.vc-page > \.va-col \{ grid-column: 2 \/ 12; \} \}/, 'and ten when the window is narrow');
assert.match(page, /relationships' as const/, 'relationships are one line like any other check');
assert.match(page, /id: 'rowcounts', name: 'Table row counts'/, 'and so are table row counts');
assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgb\(|hsl\(/, 'the page\'s CSS uses tokens only, no colour is typed');
for (const [name, source] of [['validation.css', css], ['validation-model.ts', read('webview/src/validation-model.ts')], ['validation-parts.tsx', read('webview/src/validation-parts.tsx')]])
  assert.doesNotMatch(source, /—/, `no em dash in ${name}`);
assert.doesNotMatch(page, /<main className="sem-page">/, 'the old page shell is gone');
assert.doesNotMatch(page, /\bMap\b/, 'never the word Map on this page');
// The first view must stay small. The number of controls and words a person meets before pressing anything is what option A is for.
assert.equal((page.match(/testid="v-more"/g) ?? []).length, 1, 'one menu of everything else');
assert.doesNotMatch(page, /Search checks and results|Show checks by result|function CardShell|function GroupTitle|function Fold\(/, 'the search box, the result filter, the group titles and the folds are gone from the first view');

// The short gap on a problem row is the engine's own difference and its count of places, never a share worked out here.
const T = await load('webview/src/tests-model.ts');
assert.equal(T.gapShort(-42318.04, 3), '42,318.04 lower · 3 places');
assert.equal(T.gapShort(34, 1), '34 higher');
assert.equal(T.gapShort(0, 2), '2 places');
assert.equal(T.gapShort(null, 1), '1 place differs');
assert.equal(T.gapShort(null, 0), 'Differs');
assert.equal(T.gapShort(0.001, 1), '0.001 higher', 'a tiny difference is never printed as 0.00');
assert.equal(T.gapShort(-0.00275, 1, undefined, '36.1%'), '0.27 points lower', 'a percent check states its gap in points (UAT round 2, N11)');

// Polish (16 Oct): the verdict counts only checks that ran; checks not run fold into one line; a long list is cut at ten; a long
// pass list gets a filter; and a gap that the grand total would hide names its places.
assert.match(page, /const tally = boardTally\(items\.map\(\(item\) => item\.chip\)\);/, 'one tally for the headline and the line under it, so the parts add up');
assert.match(page, /const headline = boardHeadline\(tally\);/, 'the big number comes from the tally');
assert.doesNotMatch(page, /of \{checked\.toLocaleString\(\)\} pass/, 'the headline never counts only the checks that ran');
assert.match(page, /const notRunItems = ordered\.filter\(\(item\) => item\.chip === 'Not run'\);/, 'not-run checks leave the problem rows');
assert.match(page, /onClick=\{runNotRun\} data-testid="v-notrun-run"/, 'one line with a Run button that runs those checks, or says why it cannot');
assert.match(page, /data-testid="v-notrun-arrow"/, 'the arrow on that line is a button that opens it');
assert.match(page, /if \(only\?\.family === 'rowcounts'\) onRunSection\('tableCounts'\);/, 'the Run on row counts runs row counts');
assert.match(page, /const PROBLEMS_SHOWN = 10;/);
assert.match(page, /problems\.slice\(0, PROBLEMS_SHOWN\)/, 'the first ten problems');
assert.match(page, /and \{\(problems\.length - shownProblems\.length\)\.toLocaleString\(\)\} more[\s\S]{0,40}/, 'and N more');
assert.match(page, /onClick=\{\(\) => setShowAllProblems\(true\)\} data-testid="v-problems-more"/, 'the line opens the rest in place');
assert.match(page, /const PASS_FILTER_FROM = 20;/);
assert.match(page, /const filterOn = showPass && quiet\.length > PASS_FILTER_FROM;/, 'a filter only for a pass list over twenty');
assert.match(page, /item\.name\.toLowerCase\(\)\.includes\(wanted\)/, 'the filter finds a check by name');
assert.match(page, /\{filterOn && <div className="va-filter">/, 'the filter sits at the top of the opened list');
const rows2 = [
  { delta: 500, verdict: 'Fail' }, { delta: -497, verdict: 'Fail' }, { delta: 3, verdict: 'Pass', grandTotal: true }];
assert.equal(T.gapShort(3, 2, rows2), '2 places differ, up to 500', 'opposite gaps are never hidden by their total');
assert.equal(T.gapShort(0, 2, [{ delta: 40, verdict: 'Fail' }, { delta: -40, verdict: 'Fail' }]), '2 places differ, up to 40');
assert.equal(T.gapShort(-42318.04, 3, [{ delta: -42318.04, verdict: 'Fail', grandTotal: true }, { delta: -37722.38, verdict: 'Fail' }]), '42,318.04 lower \u00b7 3 places', 'a total that is the biggest gap keeps the old line');
assert.equal(T.gapShort(34, 1, [{ delta: 34, verdict: 'Fail' }]), '34 higher', 'one place equal to the total keeps the old line');
assert.equal(T.gapShort(null, 1, [{ delta: 9, verdict: 'Fail' }, { delta: 0, verdict: 'Pass', grandTotal: true }]), '1 place differs, up to 9');

// ---------------------------------------------------------------------------------------------------
// Jev's pick on the page: ONE call per run, Pro only, rules when off or unsure, labelled as the rules' pick.
// ---------------------------------------------------------------------------------------------------
assert.match(page, /useFeature\('askData'\) === 'granted'/, 'only a granted plan asks Jev; Free draws the rules\' pick and sends nothing');
assert.equal((page.match(/rpc<VisualPicks>\('pickCheckVisuals', ask\)/g) ?? []).length, 1, 'one call site, one call for every check');
assert.match(page, /const askKey = ask \? `\$\{runKey\}\|/, 'the call is keyed to the run, so a redraw does not ask again');
assert.match(page, /\}, \[askOn, askKey\]\);/, 'and only a new run or a changed result asks again');
assert.match(page, /ask\.checks\.every\(\(c\) => asked\.current\.ids\.has\(c\.id\)\)\) return;/, 'Turn off and Turn on reuse the run\'s picks: no new call for a check this run already named');
assert.match(page, /by=\{chosen\.by\}/, 'the line under a visual says whether Jev or the rules chose it');
assert.match(read('webview/src/visuals/visuals.tsx'), /by === 'jev' \? <JevMark \/> : <span className="qv-jev qv-rules"/, 'the visual set labels a rules\' pick as the rules\'');

// ---------------------------------------------------------------------------------------------------
// UAT 1.5.104 (17 Oct): "2 of 3 pass" beside "1 could not check" and "1 not run yet" was four states for three checks.
// Every check is in exactly one state, and the parts add up to the number of checks.
// ---------------------------------------------------------------------------------------------------
{
  const t = M.boardTally(['Pass', 'Pass', 'Could not check', 'Not run', 'Off']);
  assert.equal(t.total, 4, 'a check that is turned off is not counted');
  assert.deepEqual([t.passed, t.differs, t.cannot, t.notRun], [2, 0, 1, 1]);
  assert.equal(t.passed + t.differs + t.cannot + t.notRun, t.total, 'the parts add up to the checks');
  assert.deepEqual(M.boardHeadline(t), { big: '2', rest: ' of 4 pass' }, 'the headline counts every check, not only the ones that ran');
  assert.deepEqual(M.boardParts(t).map((p) => p.text), ['1 could not check', '1 not run yet']);
  assert.equal(M.boardParts(t).reduce((n, p) => n + p.n, t.passed), t.total);
  const allPass = M.boardTally(['Pass', 'Pass', 'Pass']);
  assert.deepEqual(M.boardHeadline(allPass), { big: '3', rest: ' of 3 pass' });
  assert.deepEqual(M.boardParts(allPass).map((p) => p.text), ['Nothing needs a look']);
  const none = M.boardTally(['Not run', 'Not run']);
  assert.deepEqual(M.boardHeadline(none), { big: '2', rest: ' checks, not run yet' });
  assert.deepEqual(M.boardParts(none), [], 'nothing has run: the line under the headline says only Press Run checks');
  assert.deepEqual(M.boardHeadline(M.boardTally(['Not run'])), { big: '1', rest: ' check, not run yet' });
}
// The Run beside "not run yet" always runs: row counts are part of Run checks, so they are never refused for needing a source.
assert.equal(M.notRunRunWhy([{ family: 'rowcounts' }], true), null, 'row counts run with Run checks');
assert.match(M.notRunRunWhy([{ family: 'rowcounts' }], false), /no live test model/i);

// F54: a title the engine wrote with bare digits reads with separators, whatever is around it.
assert.equal(M.readableTitle('Net Sales totals 75000000'), 'Net Sales totals 75,000,000');
assert.equal(M.readableTitle('Net Sales totals 75249457.11 in 2024'), 'Net Sales totals 75,249,457.11 in 2024', 'a year is not a figure');
assert.equal(M.readableTitle('Orders 2024 holds'), 'Orders 2024 holds');
assert.equal(M.readableTitle('Units 1000 vs 12'), 'Units 1,000 vs 12');
assert.equal(M.readableTitle('Net Sales ties to source SQL'), 'Net Sales ties to source SQL');
// Round 2 N30: the first view of a check is its numbers and a next step; the allowed difference and where it was written wait behind Details.
assert.match(page, /<details className="va-details" data-testid="check-details">/, 'detail on demand');
assert.doesNotMatch(page.slice(page.indexOf('facts = <>'), page.indexOf('const meta =')), /toleranceSentence/, 'no allowed-difference line in the first view');
assert.doesNotMatch(page, /authoredSuffix/, 'no powerbi:// authored line in the first view');
// Round 2 N16: Run checks adds Relationships and Table row counts to the person's own checks; the page says so in one line.
assert.equal(M.quickChecksNote(true, true), 'Two quick model checks ran too: Relationships and Table row counts.');
assert.equal(M.quickChecksNote(true, false), 'A quick model check ran too: Relationships.');
assert.equal(M.quickChecksNote(false, true), 'A quick model check ran too: Table row counts.');
assert.equal(M.quickChecksNote(false, false), null);

// UAT 1.5.104 (K8, F47): the page said the engine's line ("Reconciled 'measure:Sales/Net Sales' vs SQL (Reconciled)") and then up to four more notes,
// two of them near copies. A note reads in plain words, never as an object reference, and the page shows one note at most.
assert.equal(M.plainToolNote("Reconciled 'measure:Sales/Net Sales' vs SQL (Reconciled)"), 'Compared Net Sales with its source: they match.');
assert.equal(M.plainToolNote("Reconciled 'measure:Sales/Net Sales' vs SQL (Mismatch)"), 'Compared Net Sales with its source: they differ.');
assert.equal(M.plainToolNote("Reconciled 'measure:Key Measures/# Sales' vs SQL (InsufficientCoverage)"), 'Compared # Sales with its source: could not check.');
assert.equal(M.plainToolNote("Saved check 'column:Product/Brand' for measure:Sales/Net Sales"), 'Saved check Product Brand for Net Sales');
assert.equal(M.plainToolNote('Your assistant is running your checks.'), 'Your assistant is running your checks.');
assert.doesNotMatch(M.plainToolNote("x measure:A/B y column:T/C z table:Sales"), /measure:|column:|table:/);
{
  const err = { key: 'err', tone: 'bad', text: 'It broke.' }, partial = { key: 'part', tone: 'warn', text: 'Only part ran.' }, assistant = { key: 'a', tone: 'ok', text: 'Done.' };
  assert.deepEqual(M.oneNotice([false, null, undefined]), null);
  assert.equal(M.oneNotice([null, partial, assistant]).key, 'part', 'the first note that has something to say wins, and only one is shown');
  assert.equal(M.oneNotice([err, partial, assistant]).key, 'err', 'a failure outranks the rest');
  assert.equal(M.oneNotice([{ key: 'x', tone: 'ok', text: '  ' }, assistant]).key, 'a', 'an empty note is no note');
}
const notices = page.slice(page.indexOf('const notices = '), page.indexOf('const notices = ') + 2600);
assert.equal((notices.match(/<Banner/g) ?? []).length, 1, 'the notices block draws one banner');
assert.doesNotMatch(page, /\{partRun && run\?\.scope && <div className="va-notice">/, 'the part-run sentence is a candidate for the one note, not a second banner');

console.log('validation view tests passed');

// Round 2 N16: the "quick checks ran too" line comes from THIS run's scope, never from chips kept from an older run.
{
  const quick = page.slice(page.indexOf('const quickNote'), page.indexOf('const quickNote') + 400);
  assert.match(quick, /!relSkipped/, 'Relationships counts only when this run ran it');
  assert.match(quick, /!countsSkipped/, 'Table row counts counts only when this run ran it');
}
