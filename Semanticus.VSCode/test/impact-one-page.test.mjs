// Model > Lineage > Impact, Option A: one page that answers "what uses this?", "what can I clean up?"
// and "which reports did we check?" from one card and one report selection.
//
// Two kinds of check live here, and the difference matters when reading a pass:
//   1. The SENTENCES and the GROUPING are real logic, executed. lineage-model.ts is a pure module on
//      purpose, so every honesty rule in Astra's copy tables is a function this file runs.
//   2. The COMPONENTS are rendered for real with react-dom/server, through a loader that stubs only the
//      VS Code bridge. A static render proves what a state draws; it does not prove a click. The clicks
//      (Show all, the filter, Escape, ticking, Propose removal) are driven in a real browser by
//      tools/uishot/cp-impact-drive.mjs, which is the check that actually proves them.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

// ---- a tiny CommonJS loader for the webview's own TS/TSX, with the host seams stubbed ---------------
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls; drive the app for that'); },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {}, pickReportPaths: async () => [],
  },
  './connection': { useConnection: () => ({ session: { sessionId: 'test', liveEndpoint: null, liveDatabase: null } }) },
  './pro': { useTier: () => 'pro', isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null },
};
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const srcOf = (f) => readFileSync(join(webview, 'src', f), 'utf8');

// What a person can actually read or hear: the text, plus the title and aria-label a tooltip or a screen
// reader speaks. Machine plumbing (a select's value, a class, a test id) is not copy, and the shared
// account picker's option values are engine mode ids by design - the LABEL is what is on screen.
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|checked|disabled|href|width|maxWidth)="[^"]*"/g, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&')
  .replace(/\s+/g, ' ');

const M = load(join(webview, 'src', 'lineage-model.ts'));
const at = (h, m) => `${h}:${String(m).padStart(2, '0')} AM`;
const time = () => at(9, 41);   // deterministic clock: the sentences are the subject, not the locale

// ===================================================================================================
// 1. The reports row. Astra's five states, verbatim in meaning: never invent a total, never call a
//    selection a reading, never let a stale timestamp stand in for a fresh check.
// ===================================================================================================
const scope = (over = {}) => {
  const reports = over.reports ?? [];
  const count = (s) => reports.filter((r) => r.state === s).length;
  return {
    modelName: 'Contoso', reports, listed: reports.length,
    checked: count('checked'), couldNotBeFullyChecked: count('couldNotBeFullyChecked'),
    notChecked: count('notChecked'), needsChecking: count('needsChecking'),
    checkedWhenUtc: reports.find((r) => r.checkedWhenUtc)?.checkedWhenUtc, gaps: [], ...over,
  };
};
const rep = (name, state, extra = {}) => ({
  id: name, kind: 'published', name, source: 'Fabric Reporting', state,
  checkedWhenUtc: state === 'checked' || state === 'couldNotBeFullyChecked' || state === 'needsChecking' ? '2026-09-15T09:41:00Z' : undefined,
  ...extra,
});

assert.equal(M.reportsRowLine(scope(), { time }).headline, 'Reports have not been checked.',
  'nothing chosen: say so, and never invent a total');
assert.equal(M.reportsRowLine(scope(), { time }).action, 'Choose reports');
assert.doesNotMatch(M.reportsRowLine(scope(), { time }).headline, /\d/, 'a count nobody established must not appear');

const oneOfThree = scope({ reports: [rep('Warehouse ops', 'checked'), rep('Contoso', 'notChecked'), rep('sm_expense_claims', 'notChecked')] });
assert.equal(M.reportsRowLine(oneOfThree, { time }).headline, '1 report checked; 2 listed reports not checked.');
assert.match(M.reportsRowLine(oneOfThree, { time }).detail, /Warehouse ops/, 'the checked report is named');
assert.match(M.reportsRowLine(oneOfThree, { time }).detail, /9:41 AM/, 'the check carries its time');
assert.match(M.reportsRowLine(oneOfThree, { time }).detail, /Contoso and sm_expense_claims were not checked/);
assert.equal(M.reportsRowCount(oneOfThree), '1 of 3 listed checked');

const allThree = scope({ reports: [rep('Warehouse ops', 'checked'), rep('Contoso', 'checked'), rep('sm_expense_claims', 'checked')] });
assert.equal(M.reportsRowLine(allThree, { time }).headline, 'All 3 listed reports checked in Fabric Reporting.');
assert.match(M.reportsRowLine(allThree, { time }).detail, /says nothing about other workspaces or report files/,
  'a full check of the list is still not a claim about every report');
assert.equal(M.reportsRowCount(allThree), 'all 3 listed checked');

const partly = scope({ reports: [rep('Warehouse ops', 'checked'), rep('Contoso', 'couldNotBeFullyChecked', { note: 'Part of this report could not be read.' })] });
assert.equal(M.reportsRowLine(partly, { time }).headline, '1 checked; 1 could not be fully checked.');

const stale = scope({ reports: [rep('Warehouse ops', 'needsChecking')] });
assert.equal(M.reportsRowLine(stale, { time }).headline, 'Needs checking again.');
assert.match(M.reportsRowLine(stale, { time }).detail, /model changed/i, 'say why it is stale');
assert.doesNotMatch(M.reportsRowLine(stale, { time }).headline, /checked in/, 'a stale check must not read as a completed one');

// The verdict sentence for the field's own report use never claims "no use" from an unfinished reading.
assert.match(M.noUseSentence('Total Sales', oneOfThree, [], { time }), /No use of Total Sales/);
assert.match(M.noUseSentence('Total Sales', oneOfThree, [], { time }), /2 listed reports were not checked/);
assert.equal(M.noUseSentence('Total Sales', scope(), [], { time }), 'Report use is unknown. No reports have been chosen for this model yet.');
assert.match(M.noUseSentence('Total Sales', partly, [], { time }), /could not be fully checked/,
  'an unreadable part must stay visible in the field’s own answer');

// The verdict sentence. The engine writes the same sentence for the assistant (ImpactAssessmentBuilder.Summary,
// pinned verbatim in Semanticus.Tests/ImpactAssessmentTests.cs), and the page writes it here because it must be
// on screen the instant a field is picked, before the assessment comes back. Driving the built app showed the
// cost of not having this: the card's first paint read "3 measures", a fragment, and only became a sentence a
// moment later. Same rule, same words, both doors.
assert.equal(M.verdictSentence({ rootName: 'Assess Sentence', rootKind: 'measure', impacted: [1], measures: 1, columns: 0, tables: 0, relationships: 0, other: 0 }),
  'Assess Sentence is used by 1 measure.');
assert.equal(M.verdictSentence({ rootName: 'Assess Sentence', rootKind: 'measure', impacted: [1], measures: 1, columns: 0, tables: 0, relationships: 0, other: 0 }, 'remove'),
  'Deleting Assess Sentence would break 1 measure.');
assert.equal(M.verdictSentence({ rootName: 'Assess Sentence Orphan', rootKind: 'measure', impacted: [], measures: 0, columns: 0, tables: 0, relationships: 0, other: 0 }),
  'Nothing in the model uses Assess Sentence Orphan.');

// The report sentence must not say "checked" twice about one reading.
assert.doesNotMatch(M.noUseSentence('Total Sales', oneOfThree, [], { time }), /checked \(checked/,
  'the reading and its time are one clause, not two');
// The time lives in the reports ROW, which says it once. Repeating it in the field's own sentence is what
// made the two doors impossible to compare (Astra, ruling 2).
assert.match(M.noUseSentence('Total Sales', oneOfThree, [], { time }), /\(Warehouse ops\)/);
assert.doesNotMatch(M.noUseSentence('Total Sales', oneOfThree, [], { time }), /9:41 AM/);

// ===================================================================================================
// 2. The strip. "Checked: model - N of M listed reports (time) - Choose reports" replaces the yellow
//    model-only banner, and says what the answer covers instead of what it does not.
// ===================================================================================================
const strip = M.checkedStrip(oneOfThree, { time });
assert.equal(strip.model, 'Checked: model');
assert.equal(strip.reports, '1 of 3 listed reports');
assert.equal(strip.when, '(9:41 AM)');
assert.equal(strip.action, 'Choose reports');
assert.equal(M.checkedStrip(scope(), { time }).reports, 'no reports checked');
assert.equal(M.checkedStrip(scope(), { time }).when, '');
// Driving the app showed the gap: a stale reading made the strip read "0 of 1 listed reports", which is a
// count and not a warning. The short form has to carry the staleness too, or the one line most people read
// says nothing about the thing that matters.
assert.equal(M.checkedStrip(stale, { time }).reports, '1 listed report needs checking again');
assert.equal(M.checkedStrip(stale, { time }).when, '', 'a stale check must not wear a fresh-looking time');
// And the field's own answer must say the reading went stale, not that it never happened.
assert.match(M.noUseSentence('Total Sales', stale, [], { time }), /needs checking again/i);
assert.match(M.noUseSentence('Total Sales', stale, [], { time }), /model changed/i);
assert.doesNotMatch(M.noUseSentence('Total Sales', stale, [], { time }), /has not been checked/);

// ===================================================================================================
// 3. What uses it: counts, a few names, and the full list grouped by kind and sorted by directness.
// ===================================================================================================
const node = (name, kind, over = {}) => ({ ref: `${kind}:Sales/${name}`, name, kind, table: 'Sales', depth: 1, via: 'dax', ...over });
const impact = {
  root: 'column:Date/Date', rootName: 'Date', rootKind: 'column',
  impacted: [
    node('Margin', 'measure', { depth: 2, viaName: 'Total Sales' }),
    node('Total Sales', 'measure'),
    node('Year', 'calcColumn', { table: 'Date' }),
    node('Sales[OrderDate] to Date[Date]', 'relationship', { via: 'relationship' }),
    node('Margin %', 'measure', { depth: 3, viaName: 'Margin' }),
  ],
  measures: 3, columns: 1, tables: 0, relationships: 1, other: 0,
};
assert.equal(M.modelRowCounts(impact), '3 measures · 1 column · 1 relationship');
assert.equal(M.modelRowCounts({ ...impact, columns: 0, relationships: 0, measures: 3 }), '3 measures');
assert.equal(M.modelRowCounts({ impacted: [], measures: 0, columns: 0, tables: 0, relationships: 0, other: 0 }), 'nothing');
assert.equal(M.verdictSentence(impact), 'Date is used by 3 measures, 1 column and 1 relationship.');
assert.doesNotMatch(M.verdictSentence(impact), /^\d/, 'the sentence names the field first, never opens on a bare count');

const groups = M.dependantGroups(impact);
assert.deepEqual(groups.map((g) => g.label), ['Measures · 3', 'Columns · 1', 'Relationships · 1']);
assert.deepEqual(groups[0].items.map((i) => i.name), ['Total Sales', 'Margin', 'Margin %'],
  'sorted by how directly each thing uses it, never by depth number on screen');
assert.equal(M.directness(groups[0].items[0]), 'directly');
assert.equal(M.directness(groups[0].items[1]), 'via Total Sales');
assert.equal(M.directness(groups[1].items[0]), 'calculated from it');
assert.equal(M.directness(groups[2].items[0]), 'relationship');
for (const g of groups) for (const i of g.items) {
  assert.doesNotMatch(M.directness(i), /depth|refs/i, 'engine words must not reach the list');
}
// The preview shows the MOST DIRECT things first, whatever kind they are: a relationship that uses the
// column directly matters more to a person than a measure three steps down the chain.
assert.deepEqual(M.previewNames(impact, 3).map((i) => i.name), ['Total Sales', 'Year', 'Sales[OrderDate] to Date[Date]']);
assert.deepEqual(M.previewNames(impact, 5).map((i) => i.name).slice(3), ['Margin', 'Margin %']);
assert.equal(M.showAllLabel(impact), 'Show all 5');

// ===================================================================================================
// 4. Removal: blocked with the reason while anything uses it, and the proposal says where it goes.
// ===================================================================================================
assert.equal(M.removalBlock(impact), 'These 5 things need this field. Update or remove them before deleting it.');
assert.equal(M.removalBlock({ ...impact, impacted: impact.impacted.slice(1, 2), measures: 1, columns: 0, relationships: 0, tables: 0, other: 0 }),
  '1 measure needs this field. Update or remove it before deleting it.');
assert.equal(M.removalBlock({ impacted: [], measures: 0, columns: 0, tables: 0, relationships: 0, other: 0 }), null,
  'nothing uses it: the action is not blocked');
assert.equal(M.PROPOSE_NOTE,
  'This adds a proposal to Changes > Proposed. The field stays until you apply the proposal.');

// ===================================================================================================
// 5. Cleanup: three groups, reasons, and a headline that counts what was actually established.
// ===================================================================================================
const cand = (name, kind, verdict, reason) => ({ ref: `${kind}:Sales/${name}`, name, kind, table: 'Sales', verdict, reason, blockedBy: [], refCount: 0 });
const candidates = [
  cand('_Base Rate', 'measure', 'safe', 'Not used by any measure, column or relationship, nor by the 1 report checked.'),
  cand('MiddleName', 'column', 'safe', 'Not used by any measure, column or relationship, nor by the 1 report checked.'),
  cand('Legacy Cost', 'measure', 'usedByUnusedOnly', 'Used by _Base Rate. No use of _Base Rate was found in these checks. Recheck after removing it.'),
  cand('CoverageKey', 'column', 'caution', 'Used by a data-loading step. Open that step to check whether it needs this column.'),
];
const cg = M.cleanupGroups(candidates);
assert.deepEqual(cg.map((g) => g.label), ['No use found in these checks', 'Still used', 'Needs checking']);
assert.deepEqual(cg.map((g) => g.items.length), [2, 1, 1]);
for (const g of cg) assert.doesNotMatch(g.label, /safe/i, 'nothing on this page is called safe');
assert.equal(M.cleanupHeadline(candidates, oneOfThree),
  '4 candidates: 2 with no use found, 1 still used, 1 needing review · checked against the model and 1 of 3 listed reports');
assert.equal(M.cleanupHeadline([], oneOfThree), 'No cleanup candidates · checked against the model and 1 of 3 listed reports');
assert.equal(M.proposeSelectedLabel(2), 'Propose removing 2 selected');
assert.equal(M.proposeSelectedLabel(1), 'Propose removing 1 selected');
assert.equal(M.CLEANUP_CAVEAT,
  'A report that was not checked could still use one of these. The proposal is rechecked against the same reports when you apply it.');

// ===================================================================================================
// 6. Add a check. A measure has a real handoff. A column does not, so it asks which measure instead of
//    being pushed into a measure-only form or losing its context.
// ===================================================================================================
assert.deepEqual(M.addCheckPlan({ root: 'measure:Sales/Total Sales', rootName: 'Total Sales', rootKind: 'measure', impacted: [], measures: 0, columns: 0, tables: 0, relationships: 0, other: 0 }),
  { kind: 'measure', ref: 'measure:Sales/Total Sales', name: 'Total Sales' });
const columnPlan = M.addCheckPlan(impact);
assert.equal(columnPlan.kind, 'choose');
assert.deepEqual(columnPlan.options.map((o) => o.name), ['Total Sales', 'Margin', 'Margin %']);
assert.equal(columnPlan.tableRowCount, 'Date');
assert.equal(columnPlan.prompt, 'A check runs one measure. Which measure should this check use?');
const barePlan = M.addCheckPlan({ root: 'column:Date/Date', rootName: 'Date', rootKind: 'column', impacted: [], measures: 0, columns: 0, tables: 0, relationships: 0, other: 0 });
assert.equal(barePlan.kind, 'choose');
assert.deepEqual(barePlan.options, []);
assert.equal(barePlan.tableRowCount, 'Date', 'a column with no measures can still be counted by its table');

// ===================================================================================================
// 7. The components, rendered. Not a source grep: the real React tree for each state.
// ===================================================================================================
const { ImpactCard, CleanupList, ChooseReportsDrawer } = load(join(webview, 'src', 'lineage-impact.tsx'));

const cardHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact, scope: oneOfThree, reportHits: [], savedChecks: [], summary: 'Date is used by 3 measures, 1 column and 1 relationship.',
  reportSummary: M.noUseSentence('Date', oneOfThree, 0, { time }), timeFormat: time,
}));
assert.match(cardHtml, /Date is used by 3 measures, 1 column and 1 relationship\./, 'the verdict sentence leads the card');
assert.match(cardHtml, /In the model/);
assert.match(cardHtml, /In reports/);
assert.match(cardHtml, /Saved checks/);
assert.match(cardHtml, /Show all 5/);
assert.match(cardHtml, /Rename/);
assert.match(cardHtml, /Propose removal/);
assert.match(cardHtml, /Add a check/);
assert.match(cardHtml, /These 5 things need this field/, 'removal is blocked with the reason, on the card');
for (const banned of ['Broken', 'Check blast radius', 'Create probes', 'Stage removal', 'Interview', 'Coverage gaps', 'reportPaths', 'refs', 'depth ', 'PBIR', 'Report-aware', 'Model-only', 'azcli', 'barChart'])
  assert.ok(!readable(cardHtml).includes(banned), `the card must not say "${banned}"`);

const cleanupHtml = renderToStaticMarkup(React.createElement(CleanupList, {
  items: candidates, scope: oneOfThree, selected: new Set([candidates[0].ref, candidates[1].ref]), timeFormat: time,
}));
assert.match(cleanupHtml, /No use found in these checks/);
assert.match(cleanupHtml, /Still used/);
assert.match(cleanupHtml, /Needs checking/);
assert.match(cleanupHtml, /Propose removing 2 selected/);
assert.match(cleanupHtml, /4 candidates: 2 with no use found, 1 still used, 1 needing review/);
assert.match(cleanupHtml, /Open that step to check whether it needs this column/, 'every row says why it is here');
assert.ok(!/safe/i.test(readable(cleanupHtml)), 'nothing on the cleanup list is called safe');

const drawerHtml = renderToStaticMarkup(React.createElement(ChooseReportsDrawer, {
  scope: oneOfThree, selected: new Set(['Warehouse ops']), onClose: () => {}, timeFormat: time, returningTo: 'Total Sales',
}));
assert.match(drawerHtml, /Sign-in asks for permission that can also edit reports\. Semanticus uses it only to read\./,
  'the permission sentence stands before sign-in');
assert.match(drawerHtml, /A Power BI project \(\.pbip\) or its \.Report folder\. PBIX files are not supported\./);
// The button saves a choice; the Check button reads. Astra's callback probe showed it emitted no checkReports
// call at all, so the label was a promise the code did not keep.
assert.match(drawerHtml, /Add this report folder/);
assert.ok(!readable(drawerHtml).includes('Check this report folder'), 'a button that only chooses must not say it checks');
// And Done/Escape says which of the two states the person is actually in.
assert.match(drawerHtml, /Your changed selection needs checking\. Done and Escape keep the selection and bring you back to Total Sales/,
  'a save clears the reading, so "keeps the last completed check" is false while a choice is unread');
// With everything read, the original promise is true again and is the one that shows.
const settledDrawer = renderToStaticMarkup(React.createElement(ChooseReportsDrawer, {
  scope: allThree, selected: new Set(['Warehouse ops']), onClose: () => {}, timeFormat: time, returningTo: 'Total Sales',
}));
assert.match(settledDrawer, /Done and Escape keep the last completed check and bring you back to Total Sales\./);
assert.ok(!readable(drawerHtml).includes('the reports are checked again when you reopen it'),
  'reopening does no work on its own: that line has to be an instruction');
assert.match(drawerHtml, /Check 1 selected report/);

assert.ok(!readable(drawerHtml).includes('azcli'), 'the raw sign-in mode must never reach the drawer');
assert.ok(!readable(drawerHtml).includes('serviceprincipal'), 'nor the raw service principal token');
assert.match(drawerHtml, /Use the Azure command line/, 'the sign-in choice is named in words');
assert.ok(!/Safe to remove|Report-aware|Model-only|PBIR|reportPaths/.test(readable(drawerHtml)), 'no engine words in the drawer');

// ===================================================================================================
// 7b. Astra's rejection of the integrated build. Each of these was a claim on screen the code did not
//     keep, or a fact the screen refused to show.
// ===================================================================================================
const fixture = JSON.parse(readFileSync(resolve(root, '..', 'docs', 'report-scope-sentences.json'), 'utf8'));
// The shared meaning fixture, read here and by Semanticus.Tests/ReportScopeRejectionTests.cs. Neither suite
// builds the other; the clock is supplied already formatted, because a time is the reader's own and is not
// what drifted. What drifted was meaning.
for (const c of fixture.cases) {
  const sc = {
    modelName: c.scope.model, reports: c.scope.reports.map((r) => ({ ...r, kind: 'published' })),
    listed: c.scope.reports.length,
    checked: c.scope.reports.filter((r) => r.state === 'checked').length,
    couldNotBeFullyChecked: c.scope.reports.filter((r) => r.state === 'couldNotBeFullyChecked').length,
    notChecked: c.scope.reports.filter((r) => r.state === 'notChecked').length,
    needsChecking: c.scope.reports.filter((r) => r.state === 'needsChecking').length,
    gaps: [],
  };
  assert.equal(M.noUseSentence(c.field, sc, c.hits, { time }), c.reportSentence,
    `the page and the engine must mean the same thing for "${c.name}"`);
}

// The hit sentence names the reports that ACTUALLY contain the field, and says what the others established.
const twoChecked = scope({ reports: [rep('Warehouse ops', 'checked'), rep('sm_expense_claims', 'checked')] });
assert.match(M.noUseSentence('Total Sales', twoChecked, ['Warehouse ops'], { time }), /\(Warehouse ops\)/);
assert.doesNotMatch(M.noUseSentence('Total Sales', twoChecked, ['Warehouse ops'], { time }), /sm_expense_claims/,
  'a report that does not use the field must not be named as one that does');
assert.match(M.noUseSentence('Total Sales', twoChecked, ['Warehouse ops'], { time }), /The other checked report does not use it\./);

// The duplicate: the completion fact is said once, not twice in the same row.
const cardOnce = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact, scope: allThree, reportHits: [], savedChecks: [], summary: 'Date is used by 3 measures.',
  reportSummary: 'x', timeFormat: time,
}));
assert.match(cardOnce, /All 3 listed reports checked in Fabric Reporting\./);
assert.ok(!readable(cardOnce).includes('all 3 listed checked'), 'the same completion fact must not be repeated beside itself');

// The full list: Kane's amendment says every dependant is reachable. No silent cap, no "and N more".
const many = {
  root: 'column:Date/Date', rootName: 'Date', rootKind: 'column',
  impacted: Array.from({ length: 260 }, (_, i) => ({ ref: `measure:Sales/M${i}`, name: `M${i}`, kind: 'measure', table: 'Sales', depth: 1, via: 'dax' })),
  measures: 260, columns: 0, tables: 0, relationships: 0, other: 0,
};
assert.equal(M.pageOf(many.impacted, 0).length, 100, 'the list pages rather than truncating');
assert.equal(M.pageOf(many.impacted, 1).length, 200, 'each Show more adds a page, it does not replace one');
assert.equal(M.pageOf(many.impacted, 2).length, 260, 'the last page ends at the real total, with nothing dropped');
assert.equal(M.remaining(many.impacted, 1), 60, 'a real Show more knows what is left');
assert.equal(M.remaining(many.impacted, 2), 0);
const manyHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact: many, scope: allThree, reportHits: [], savedChecks: [], summary: 'x', reportSummary: 'y',
  timeFormat: time, expandedRow: 'model',
}));
assert.ok(!/and \d+ more/.test(readable(manyHtml)), 'a silent cap with "and N more" is not a reachable list');
assert.match(manyHtml, /Show 160 more/, 'the control says how many pressing it gets you');

// Report page and visual details come back, with the visual kind in words and the raw token only in detail.
const hitHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact, scope: allThree, summary: 'x', reportSummary: 'y', timeFormat: time, savedChecks: [], expandedRow: 'reports',
  reportHits: [{
    path: 'Warehouse ops', name: 'Warehouse ops', visuals: 2, usedRefs: ['column:Date/Date'],
    details: [
      { page: 'Overview', visual: 'Sales by month', visualKind: 'Bar chart', visualKindId: 'barChart', usedRefs: ['column:Date/Date'], viaDependent: false },
      { page: 'Detail', visual: null, visualKind: 'Card', visualKindId: 'card', usedRefs: ['measure:Sales/Total Sales'], viaDependent: true },
    ],
  }],
}));
assert.match(hitHtml, /Overview/); assert.match(hitHtml, /Sales by month/); assert.match(hitHtml, /Bar chart/);
assert.match(hitHtml, /through a measure that uses it/, 'use THROUGH a dependent measure is the point of the detail');
assert.ok(!readable(hitHtml).includes('barChart'), 'the raw visual token is a detail, never a headline');

// Saved checks: an honest label, the whole relevant set, and no invented run status.
const checksHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact, scope: allThree, reportHits: [], summary: 'x', reportSummary: 'y', timeFormat: time,
  onOpenCheck: () => {},
  savedChecks: [
    { id: 'a', kind: 'ambient-suite', title: 'Relationships and table integrity', reason: 'r' },
    ...Array.from({ length: 5 }, (_, i) => ({ id: 'c' + i, kind: 'saved-test', title: 'Check ' + i, targetRef: 'measure:Sales/Total Sales', reason: 'r' })),
  ],
}));
assert.match(checksHtml, /Open saved checks/);
assert.ok(!readable(checksHtml).includes('Run it'), 'a link that only opens a page must not say Run');
assert.ok(!readable(checksHtml).includes('not run since the last change'), 'run status must be measured, not assumed');
for (let i = 0; i < 5; i++) assert.match(checksHtml, new RegExp('Check ' + i), 'every relevant check is reachable, not the first two');

// The cleanup list shows the ENGINE's caveat when it has one. A constant printed over it is the same fault as
// the polished fixture reasons: the page saying something steadier than the engine did.
const unreadCleanup = renderToStaticMarkup(React.createElement(CleanupList, {
  items: candidates, scope: scope({ reports: [rep('Warehouse ops', 'notChecked')] }), selected: new Set(),
  timeFormat: time,
  caveat: '1 chosen report still needs checking (Warehouse ops), so nothing here can be called unused yet. Check them, then look again.',
}));
assert.match(unreadCleanup, /1 chosen report still needs checking \(Warehouse ops\)/);
assert.ok(!readable(unreadCleanup).includes(M.CLEANUP_CAVEAT), 'the engine caveat replaces the standing one, it does not sit beside it');
const readCleanup = renderToStaticMarkup(React.createElement(CleanupList, {
  items: candidates, scope: oneOfThree, selected: new Set(), timeFormat: time,
}));
assert.match(readCleanup, /A report that was not checked could still use one of these/, 'with no engine caveat the standing sentence stays');

// P4's answer has exactly ONE owner. A second effect also cleared it, and because that one depends on the
// model impact (which resolves on its own schedule) it wiped a good answer a moment after it arrived, leaving
// the card "ready" with nothing in it. Driving the app is what showed it; this keeps it shown.
const pageSrc = srcOf('lineage.tsx');
const resetEffect = pageSrc.match(/useEffect\(\(\) => \{ setActionError\(null\)[^\n]*\}, \[root, impact, scope\]\);/);
assert.ok(resetEffect, 'the around-the-answer reset effect must still exist');
assert.ok(!resetEffect[0].includes('setAssessment'), 'only the request effect may clear the answer');
assert.equal((pageSrc.match(/setAssessment\(null\)/g) ?? []).length, 1, 'exactly one place clears the answer');

console.log('impact rejection repairs passed');

// ===================================================================================================
// 8. The sub-tabs, and what an old deep link does now.
// ===================================================================================================
const lineage = srcOf('lineage.tsx');
const app = srcOf('App.tsx');
assert.match(lineage, />Graph</); assert.match(lineage, />Tree</); assert.match(lineage, />Impact</);
assert.ok(!/>Safe to remove</.test(lineage), 'Safe to remove is gone as a sub-tab');
assert.ok(!/>Published reports</.test(lineage), 'Published reports is gone as a sub-tab');
assert.match(lineage, /LEGACY_LINEAGE_MODES/, 'an old route must land somewhere real, not on a blank tab');
assert.equal(M.landingFor('unused').mode, 'impact');
assert.equal(M.landingFor('unused').cleanup, true, 'a Safe-to-remove link lands on the cleanup view');
assert.equal(M.landingFor('reports').mode, 'impact');
assert.equal(M.landingFor('reports').drawer, true, 'a Published-reports link opens the Choose reports drawer');
assert.equal(M.landingFor('graph').mode, 'graph');
assert.match(app, /onAddCheck=\{\(ref\) => goTab\('tests', undefined, ref, undefined, \{ kind: 'test', value: ref \}\)\}/,
  'a table ref needs an explicit seed, or the hand-off arrives with its context dropped');
const testsSrc = srcOf('tests.tsx');
assert.match(testsSrc, /if \(navTarget\.ref\.startsWith\('table:'\)\) \{[\s\S]{0,200}setMappingTable\(navTarget\.ref\.slice\('table:'\.length\)\);/,
  'the Tests page must honour a table hand-off by opening the row-count setup for that table');

console.log('impact one-page contract tests passed');
