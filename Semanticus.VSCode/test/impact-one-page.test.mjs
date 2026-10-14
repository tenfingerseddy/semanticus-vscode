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
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') || file.endsWith('.js') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,   // an .mjs is ESM to the compiler; as .ts it becomes CommonJS
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
  'This adds a change to Changes > Pending changes. The field stays until you apply it.');

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
// A visible dimension column nothing in the model uses is the engine's own verdict, shown in its own group with the
// engine's reason, and it cannot be ticked HERE: a sweep never takes one (dogfood feedback, 27 Sep). The row must not
// go further and deny a removal the card beside it offers, because a person who opens the field and presses Propose
// removal does get it removed (dogfood round 2, D3). The reason text below is the engine's, verbatim.
const attribute = cand('City', 'column', 'visibleAttribute', 'Visible attribute, no model reference. People filter and group by columns like this in reports, Q&A and Copilot, so Semanticus never removes it on its own. Ask the people who use the model, or hide it first. If you decide it should go, propose its removal and approve that change.');
assert.doesNotMatch(attribute.reason, /not offered for removal|cannot be removed/i,
  'a row shown beside an enabled Propose removal must not say removal is not offered');
const withAttribute = [...candidates, attribute];
const cga = M.cleanupGroups(withAttribute);
assert.deepEqual(cga.map((g) => g.label), ['No use found in these checks', 'Still used', 'Needs checking', 'Visible attribute, no model reference']);
assert.deepEqual(cga[3].items.map((i) => i.name), ['City']);
assert.equal(M.cleanupTickable(attribute), false);
assert.ok(candidates.every((c) => M.cleanupTickable(c)), 'every other verdict stays tickable');
assert.equal(M.cleanupHeadline(withAttribute, oneOfThree),
  '5 candidates: 2 with no use found, 1 still used, 1 needing review, 1 visible attribute kept · checked against the model and 1 of 3 listed reports');
assert.equal(M.cleanupHeadline([], oneOfThree), 'No cleanup candidates · checked against the model and 1 of 3 listed reports');
assert.equal(M.proposeSelectedLabel(2), 'Propose removing 2 selected');
assert.equal(M.proposeSelectedLabel(1), 'Propose removing 1 selected');
assert.equal(M.CLEANUP_CAVEAT,
  'A report that was not checked could still use one of these. The change is rechecked against the same reports when you apply it.');

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
const { ImpactCard, CleanupList, ChooseReportsDrawer, CheckedStrip } = load(join(webview, 'src', 'lineage-impact.tsx'));

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

// Minimalism pass (8 Oct): the strip reads "1 of 3 reports checked, 9:41 AM" with the one door beside it. "Checked: model" led
// it, but the model is always read, so the words said nothing. Staleness and "no reports checked" stand as the model wrote them.
{
  const stripHtml = (sc) => readable(renderToStaticMarkup(React.createElement(CheckedStrip, { scope: sc, onChoose: () => {}, timeFormat: time })));
  const one = stripHtml(oneOfThree);
  assert.match(one, /1 of 3 reports checked,\s*9:41 AM/, 'the count and the time read as one phrase');
  assert.match(one, /Choose reports/);
  assert.doesNotMatch(one, /Checked: model|listed reports|\(9:41 AM\)/, 'no model lead, no "listed", no parentheses');
  assert.match(stripHtml(scope()), /no reports checked/, 'none checked stands as written');
  assert.match(stripHtml(stale), /1 listed report needs checking again/, 'a stale reading still says so');
}

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
// 7a. The 1.2.0 hotfix. Kane drove the released build against a real Fabric workspace and hit three
//     things: a discovered report's box would not tick, Check appeared to do nothing, and there was no
//     visible way off the screen. All three needed REAL ids to show up, so all three are checked here
//     with GUID workspace and report ids and display names that have spaces in them.
// ===================================================================================================
const FX_WS = '7c1e4f26-9a3b-4d55-8f10-2b6d4e9a1c88';
const FX_REPORT = 'd94a1f70-3c2e-4a81-9b55-6e0f3a71c204';
const FX_OTHER = 'f3517a9c-21b8-4e06-b7da-4c8e5d30916b';

// The key is the ENGINE's key. LocalEngine.ReportScope.cs builds "published:" + workspaceId + "/" + reportId,
// and if this drifts the two lists in the drawer go back to disagreeing about the same report.
assert.equal(M.publishedChoiceId(FX_WS, FX_REPORT), `published:${FX_WS}/${FX_REPORT}`);
assert.equal(M.publishedChoiceId('  ws-a  ', 'r-ops'), 'published:ws-a/r-ops', 'a padded workspace id still keys the same choice');

const fabricScope = scope({ reports: [
  { id: M.publishedChoiceId(FX_WS, FX_REPORT), kind: 'published', name: 'Sales Overview', source: 'Contoso Fabric Demo',
    workspaceId: FX_WS, reportId: FX_REPORT, state: 'notChecked' },
] });
const fabricReports = [
  { id: FX_REPORT, name: 'Sales Overview', reportType: 'PowerBIReport' },
  { id: FX_OTHER, name: 'Regional Demo 2', reportType: 'PowerBIReport' },
];
const fabricHtml = renderToStaticMarkup(React.createElement(ChooseReportsDrawer, {
  scope: fabricScope, selected: new Set([M.publishedChoiceId(FX_WS, FX_REPORT)]), onClose: () => {},
  timeFormat: time, workspaceId: FX_WS, reports: fabricReports,
}));
// The bug, as one assertion. The box beside a chosen report in the DISCOVERED list was drawn from the raw
// Fabric report id while the chosen list used the choice key, so with GUID ids it was always empty.
const boxFor = (html, label) => {
  const m = html.match(new RegExp(`<input[^>]*aria-label="${label}"[^>]*>`));
  assert.ok(m, `no checkbox labelled "${label}" in the drawer`);
  return m[0];
};
assert.match(boxFor(fabricHtml, 'Choose Sales Overview'), /checked=""/,
  'a report already on the model list shows ticked in the discovered list too');
assert.ok(!/checked=""/.test(boxFor(fabricHtml, 'Choose Regional Demo 2')), 'a report nobody chose shows unticked');
assert.match(boxFor(fabricHtml, 'Check Sales Overview'), /checked=""/,
  'and the chosen list says the same thing about the same report');
assert.match(readable(fabricHtml), /Chosen\. Untick to take it off the list\./,
  'the row says in words what the tick means, and that it has an inverse');

// The way out and the button that does the work both live OUTSIDE the scrolling middle. On a real workspace
// the list is long enough that a single scrolling column put both of them off the screen, which is why Kane
// pressed Check, saw nothing, and could find no way to move on.
const scrollStart = fabricHtml.indexOf('overflow-auto');
const doneAt = fabricHtml.indexOf('data-testid="choose-reports-done"');
const checkAt = fabricHtml.indexOf('data-testid="choose-reports-check"');
assert.ok(scrollStart > 0, 'the drawer has one scrolling region');
assert.equal(fabricHtml.split('overflow-auto').length - 1, 1, 'exactly one part of the drawer scrolls');
assert.ok(doneAt > 0 && doneAt < scrollStart, 'Done sits above the scrolling middle, so it never scrolls away');
assert.ok(checkAt > scrollStart, 'Check sits below the scrolling middle, so it never scrolls away');

// An error is written where the person is looking, which is beside Done, not at the top of a column they
// have already scrolled past.
const erroredHtml = renderToStaticMarkup(React.createElement(ChooseReportsDrawer, {
  scope: fabricScope, selected: new Set([M.publishedChoiceId(FX_WS, FX_REPORT)]), onClose: () => {},
  timeFormat: time, workspaceId: FX_WS, reports: fabricReports,
  error: 'Say yes to the sign-in permission first.', result: 'Read 2 reports.',
}));
const errAt = erroredHtml.indexOf('data-testid="choose-reports-error"');
assert.ok(errAt > 0 && errAt < erroredHtml.indexOf('overflow-auto'), 'the error is in the part that does not scroll');
assert.ok(!/choose-reports-result/.test(erroredHtml), 'an error replaces a stale result rather than sitting beside it');

// A check that comes back says what came back. Silence after a press is what Kane read as nothing happening.
const resultHtml = renderToStaticMarkup(React.createElement(ChooseReportsDrawer, {
  scope: fabricScope, selected: new Set([M.publishedChoiceId(FX_WS, FX_REPORT)]), onClose: () => {},
  timeFormat: time, workspaceId: FX_WS, reports: fabricReports, result: 'Read 2 reports.',
}));
assert.match(readable(resultHtml), /Read 2 reports\./, 'the drawer says what the check came back with');

const read2 = scope({ reports: [
  { id: 'a', kind: 'published', name: 'Sales Overview', state: 'checked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
  { id: 'b', kind: 'published', name: 'Regional Demo 2', state: 'checked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
] });
assert.equal(M.checkOutcomeSentence(read2, ['a', 'b']), 'Read 2 reports.');
const mixedRead = scope({ reports: [
  { id: 'a', kind: 'published', name: 'Sales Overview', state: 'checked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
  { id: 'b', kind: 'published', name: 'Regional Demo 2', state: 'couldNotBeFullyChecked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
] });
assert.equal(M.checkOutcomeSentence(mixedRead, ['a', 'b']), 'Read 1 report. Regional Demo 2 could not be fully read.');
const untouched = scope({ reports: [{ id: 'a', kind: 'published', name: 'Sales Overview', state: 'notChecked' }] });
assert.equal(M.checkOutcomeSentence(untouched, ['a']), 'Sales Overview was not read.');
assert.equal(M.checkOutcomeSentence(untouched, ['gone']),
  `Nothing was read. None of those reports are on this model's list any more.`,
  'asking for a report that is no longer chosen is said out loud, not swallowed');
// The engine knows things the page does not, so its own sentence wins over anything assembled here.
assert.equal(M.checkOutcomeSentence({ ...read2, note: 'The report selection changed while these were being read.' }, ['a', 'b']),
  'The report selection changed while these were being read.');

// The host side of the same repair, read from the source: a published check refuses locally when the
// permission box is untouched, and it names the box rather than paraphrasing a permission scope.
const lineageSrc = srcOf('lineage.tsx');
assert.match(lineageSrc, /Say yes to the sign-in permission first\. Tick "Yes, sign in with that permission"/,
  'the refusal names the box that has to be ticked');
assert.match(lineageSrc, /async function removePublished/, 'unticking a discovered report has an inverse to run');
assert.ok(!/const key = `published:\$\{wsId\}\/\$\{found\.id\}`/.test(lineageSrc),
  'the choice key is spelled in one place only, and that place is lineage-model.ts');

console.log('choose reports hotfix tests passed');

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

// ===================================================================================================
// 7b. IN REPORTS opens. Kane, 16 September, on his own workspace: "there is no way to view which reports
//     are impacted and by what. The IN REPORTS still shows choose reports and there's no expand to view."
//     His exact screen had ZERO hits, and the old expansion only existed when there was at least one, so
//     there was nothing to press at all. Every assertion here is about a report list that opens whether or
//     not anything was found.
// ===================================================================================================
const listSource = 'Finance and Operations';
const listRep = (name, state, extra = {}) => ({
  id: `published:7c1e4f26-9a3b-4d55-8f10-2b6d4e9a1c88/${name}`, kind: 'published', name, source: listSource, state,
  checkedWhenUtc: state === 'checked' || state === 'couldNotBeFullyChecked' ? '2026-09-15T09:41:00Z' : undefined,
  ...extra,
});
const listScope = scope({
  reports: [
    listRep('Sales Overview', 'checked'),
    listRep('Regional Demo 2', 'checked'),
    listRep('Executive Summary', 'couldNotBeFullyChecked', { note: 'Part of this report could not be read, so its use of this model is not fully known.' }),
    listRep('Quarterly Pack', 'notChecked'),
  ],
});
const listChoiceId = (name) => `published:7c1e4f26-9a3b-4d55-8f10-2b6d4e9a1c88/${name}`;
const listHits = [{
  path: 'Sales Overview', name: 'Sales Overview', choiceId: listChoiceId('Sales Overview'),
  visuals: 3, usedRefs: ['measure:Sales/Gross Margin %'],
  details: [
    { page: 'Margin', visual: 'Margin by month', visualKind: 'Bar chart', visualKindId: 'barChart', usedRefs: ['measure:Sales/Gross Margin %'], viaDependent: false },
    { page: 'Margin', visual: 'Margin headline', visualKind: 'Card', visualKindId: 'card', usedRefs: ['measure:Sales/Gross Margin %'], viaDependent: false },
    { page: 'Regions', visual: null, visualKind: 'Table', visualKindId: 'tableEx', usedRefs: ['measure:Sales/Margin Rank'], viaDependent: true },
  ],
}];

// The label counts LISTED reports, not checked ones: it says what pressing it gets you.
assert.equal(M.showAllReportsLabel(listScope), 'Show all 4 reports');
assert.equal(M.showAllReportsLabel(scope({ reports: [listRep('Only one', 'checked')] })), 'Show all 1 report');

const listRows = M.reportRows(listScope, listHits, 'Gross Margin %', { time });
assert.equal(listRows.length, 4, 'every chosen report gets a row, checked or not');
assert.deepEqual(listRows.map((r) => r.stateWords),
  ['Checked at 9:41 AM', 'Checked at 9:41 AM', 'Could not be fully checked', 'Not checked'],
  'each row says its state in plain words, and a check carries its time');
assert.deepEqual(listRows.map((r) => r.source), Array(4).fill(listSource), 'a row names where the report lives');

// The report with hits shows them, and says nothing about "no use".
assert.equal(listRows[0].details.length, 3);
assert.equal(listRows[0].finding, null, 'a report with hits does not also claim nothing was found');
assert.deepEqual(listRows[0].details.map((d) => d.page), ['Margin', 'Margin', 'Regions']);
assert.equal(listRows[0].details[2].viaDependent, true, 'use through a dependent measure is carried per visual');

// The checked report with nothing found says so, about THIS field, on its own row.
assert.equal(listRows[1].finding, 'No use of Gross Margin % found in this report.');
assert.equal(listRows[1].cause, null);

// The one that could not be fully checked carries the ENGINE's cause and one thing to do, and is NEVER
// given a "no use found" line: nobody established that.
assert.equal(listRows[2].finding, null, 'an incomplete reading must not read as a clean "nothing found"');
assert.match(listRows[2].cause, /Part of this report could not be read/);
assert.match(listRows[2].next, /Check it again/);

// The unread one says what it is and what to do, and claims nothing about the field.
assert.equal(listRows[3].finding, null);
assert.equal(listRows[3].cause, null, 'nothing went wrong with a report nobody has read yet');
assert.match(listRows[3].next, /Check it to find out whether it uses this field/);

// A cause the engine did not record is said as that, never smoothed over.
const noCause = M.reportRows(scope({ reports: [listRep('Silent failure', 'couldNotBeFullyChecked')] }), [], 'X', { time });
assert.equal(noCause[0].cause, M.CAUSE_NOT_RECORDED);
assert.match(M.reportRows(scope({ reports: [listRep('Moved on', 'needsChecking')] }), [], 'X', { time })[0].cause,
  /The model changed after this report was read\./);

// A hit about a report the list does not name is kept, not dropped to keep the join tidy.
const orphan = M.reportRows(scope({ reports: [listRep('Sales Overview', 'checked')] }),
  [...listHits, { path: '/home/k/Ad hoc.Report', name: 'Ad hoc', choiceId: 'local:/home/k/Ad hoc.Report',
    visuals: 1, details: [{ page: 'One', visual: 'V', visualKind: 'Card' }] }],
  'Gross Margin %', { time });
assert.equal(orphan.length, 2, 'a hit with no matching row still gets a row');
assert.equal(orphan[1].name, 'Ad hoc');

// The filter narrows the list the same way the model list's does.
assert.equal(M.filterReportRows(listRows, 'sales').length, 1);
assert.equal(M.filterReportRows(listRows, 'sales')[0].name, 'Sales Overview');
assert.equal(M.filterReportRows(listRows, 'Finance and Operations').length, 4, 'the workspace is searchable too');
assert.equal(M.filterReportRows(listRows, '').length, 4, 'an empty filter hides nothing');
assert.equal(M.filterReportRows(listRows, 'nothing here').length, 0);

// Every visual stays reachable on a report with a lot of them: paged, never capped with a number you
// cannot open. Same rule Kane set for the model list.
const busy = M.reportRows(scope({ reports: [listRep('Busy', 'checked')] }), [{
  path: 'Busy', name: 'Busy', choiceId: listChoiceId('Busy'), visuals: 14,
  details: Array.from({ length: 14 }, (_, i) => ({ page: 'P' + i, visual: 'V' + i, visualKind: 'Card' })),
}], 'X', { time });
assert.equal(M.pageOf(busy[0].details, 0, M.VISUAL_PAGE_SIZE).length, 10);
assert.equal(M.remaining(busy[0].details, 0, M.VISUAL_PAGE_SIZE), 4);
assert.equal(M.remaining(busy[0].details, 1, M.VISUAL_PAGE_SIZE), 0);

// A hit whose reference lives in a report-level part - a bookmark, a report filter, a page-level setting -
// has NO page-stamped visual behind it. The row used to fall through to "No use found in this report",
// which contradicted the summary directly above it: the summary said the report uses the field.
const reportLevel = M.reportRows(scope({ reports: [listRep('Bookmark only', 'checked')] }),
  [{ path: 'Bookmark only', name: 'Bookmark only', choiceId: listChoiceId('Bookmark only'), visuals: 0, usedRefs: ['measure:Sales/Gross Margin %'], details: [] }],
  'Gross Margin %', { time });
assert.equal(reportLevel.length, 1);
assert.notEqual(reportLevel[0].finding, 'No use of Gross Margin % found in this report.',
  'a report the answer says uses the field must never be given a no-use line');
assert.match(reportLevel[0].finding, /Gross Margin %/, 'the row still names the field it is talking about');
assert.match(reportLevel[0].finding, /bookmark|filter|setting/i,
  'say in plain words where a use with no visual behind it lives');
assert.equal(reportLevel[0].cause, null, 'nothing went wrong: the reading was clean');

// Two chosen reports in different workspaces share a display name, and only the SECOND one uses the field.
// Joined by name, the first row took the hit and the second was told nothing was found in it. Both facts
// were then wrong, about two different reports.
const twinScope = scope({ reports: [
  { id: 'published:ws-a/r-1', kind: 'published', name: 'Monthly report', source: 'Workspace A', workspaceId: 'ws-a', reportId: 'r-1', state: 'checked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
  { id: 'published:ws-b/r-2', kind: 'published', name: 'Monthly report', source: 'Workspace B', workspaceId: 'ws-b', reportId: 'r-2', state: 'checked', checkedWhenUtc: '2026-09-15T09:41:00Z' },
] });
const twinRows = M.reportRows(twinScope, [{
  path: 'Monthly report', name: 'Monthly report', choiceId: 'published:ws-b/r-2', visuals: 1,
  usedRefs: ['measure:Sales/Gross Margin %'],
  details: [{ page: 'Margin', visual: 'Margin by month', visualKind: 'Bar chart', visualKindId: 'barChart', usedRefs: ['measure:Sales/Gross Margin %'], viaDependent: false }],
}], 'Gross Margin %', { time });
assert.equal(twinRows.length, 2, 'two chosen reports are two rows, however they are named');
assert.deepEqual(twinRows.map((r) => r.source), ['Workspace A', 'Workspace B']);
assert.equal(twinRows[0].details.length, 0, 'the report that does not use it shows no visual');
assert.equal(twinRows[0].finding, 'No use of Gross Margin % found in this report.');
assert.equal(twinRows[1].details.length, 1, 'the hit belongs to the report it actually came from');
assert.equal(twinRows[1].details[0].visual, 'Margin by month');
assert.equal(twinRows[1].finding, null);

// A report past the engine's detail cap says how many matches it left out, and the row carries the count
// rather than the list quietly stopping at a number nobody can open.
const cappedHit = {
  path: 'Huge', name: 'Huge', choiceId: listChoiceId('Huge'), visuals: 15, usedRefs: ['measure:Sales/Gross Margin %'],
  details: Array.from({ length: 10 }, (_, i) => ({ page: 'P' + i, visual: 'V' + i, visualKind: 'Card' })),
  detailsOmitted: 5,
};
const capped = M.reportRows(scope({ reports: [listRep('Huge', 'checked')] }), [cappedHit], 'Gross Margin %', { time });
assert.equal(capped[0].detailsOmitted, 5, 'the omitted count reaches the row');
assert.equal(M.remaining(capped[0].details, 0, M.VISUAL_PAGE_SIZE), 0, 'the listed details all fit on one page');
assert.equal(M.omittedLine(0), null, 'nothing omitted means no line at all');
assert.equal(M.omittedLine(1), 'and 1 more match is not listed');
assert.equal(M.omittedLine(5), 'and 5 more matches are not listed');

const cappedHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact: { ...impact, rootName: 'Gross Margin %' }, scope: scope({ reports: [listRep('Huge', 'checked')] }),
  reportHits: [cappedHit], savedChecks: [], summary: 'x', reportSummary: 'y', timeFormat: time, expandedRow: 'reports',
}));
assert.match(cappedHtml, /and 5 more matches are not listed/,
  'the last page of a capped list says the rest exist');

// And the row for a report-level use draws its sentence.
const reportLevelHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact: { ...impact, rootName: 'Gross Margin %' }, scope: scope({ reports: [listRep('Bookmark only', 'checked')] }),
  reportHits: [{ path: 'Bookmark only', name: 'Bookmark only', choiceId: listChoiceId('Bookmark only'), visuals: 0, usedRefs: ['measure:Sales/Gross Margin %'], details: [] }],
  savedChecks: [], summary: 'x', reportSummary: 'y', timeFormat: time, expandedRow: 'reports',
}));
assert.ok(!reportLevelHtml.includes('No use of Gross Margin % found in this report.'),
  'the card must not say nothing was found in a report the answer says uses it');

// The save shape. Every path that writes the list back - untick, exclude, add, folder - goes through this
// one function, so a published report cannot be saved without the workspace and report id that say which
// report it is. A row that arrives without them has them recovered from its own choice id.
assert.deepEqual(M.scopeChoice({ id: 'published:ws-a/r-1', kind: 'published', name: 'Monthly report', source: 'Workspace A', workspaceId: 'ws-a', reportId: 'r-1', state: 'checked' }),
  { id: 'published:ws-a/r-1', kind: 'published', name: 'Monthly report', workspaceId: 'ws-a', workspaceName: 'Workspace A', reportId: 'r-1' });
assert.deepEqual(M.scopeChoice({ id: 'published:ws-b/r-2', kind: 'published', name: 'Monthly report', source: 'Workspace B', state: 'checked' }),
  { id: 'published:ws-b/r-2', kind: 'published', name: 'Monthly report', workspaceId: 'ws-b', workspaceName: 'Workspace B', reportId: 'r-2' },
  'a row with no coordinates on it has them read back out of its own id');
assert.deepEqual(M.scopeChoice({ id: 'local:/home/k/Sales.Report', kind: 'local', name: 'Sales', source: '/home/k/Sales.Report', state: 'notChecked' }),
  { id: 'local:/home/k/Sales.Report', kind: 'local', name: 'Sales', path: '/home/k/Sales.Report' });

// And the source file proves the drawer has no second way to build a choice.
const drawerSrc = srcOf('lineage.tsx');
assert.match(drawerSrc, /M\.scopeChoice/, 'the drawer saves through the shared choice builder');
assert.doesNotMatch(drawerSrc, /kind: 'published', name: r\.name/,
  'a hand-rolled published choice in the drawer is how the coordinates got dropped');

// And the card draws it. This is the state Kane was looking at: a real workspace, nothing found, and a
// list he could open anyway.
const listHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact: { ...impact, rootName: 'Gross Margin %' }, scope: listScope, reportHits: listHits, savedChecks: [],
  summary: 'x', reportSummary: 'y', timeFormat: time, expandedRow: 'reports',
}));
for (const name of ['Sales Overview', 'Regional Demo 2', 'Executive Summary', 'Quarterly Pack', listSource])
  assert.match(listHtml, new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')), `the list names ${name}`);
assert.match(listHtml, /No use of Gross Margin % found in this report\./);
assert.match(listHtml, /Margin by month/); assert.match(listHtml, /Bar chart/);
assert.match(listHtml, /through a measure that uses it/);
assert.match(listHtml, /Part of this report could not be read/);
assert.match(listHtml, /Check it to find out whether it uses this field/);
assert.match(listHtml, /Choose reports/, 'the Choose reports link stays beside the list');
assert.ok(!readable(listHtml).includes('tableEx'), 'the raw visual token stays a detail');

// Closed, the control is still offered with nothing found at all - which is the whole bug.
const emptyHitsHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact: { ...impact, rootName: 'Gross Margin %' }, scope: listScope, reportHits: [], savedChecks: [],
  summary: 'x', reportSummary: 'y', timeFormat: time,
}));
assert.match(emptyHitsHtml, /Show all 4 reports/, 'the way in exists even when no report uses the field');
assert.match(emptyHitsHtml, /data-testid="impact-reports-show-all"/);

// With nothing chosen there is no list to open, and the row says so rather than offering an empty box.
const noScopeHtml = renderToStaticMarkup(React.createElement(ImpactCard, {
  impact, scope: scope(), reportHits: [], savedChecks: [], summary: 'x', timeFormat: time,
}));
assert.ok(!/impact-reports-show-all/.test(noScopeHtml), 'nothing chosen means nothing to show all of');
assert.match(noScopeHtml, /Choose the reports this model should be checked against\./);

console.log('impact reports list contract tests passed');

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
