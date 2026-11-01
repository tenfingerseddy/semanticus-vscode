import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

// d1: the Checks feature of the 1.7 release build to the minimal design spec (docs of the d1 pass, sections 2 to 6).
// One verdict vocabulary, marks that do not apply are not shown, and every surface counts its words at rest.
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const panel = load('./marks-panel.tsx');
const plan = load('./run-plan.tsx');
const page = load('./checks-page.tsx');
const hostModule = load('./marks-host.ts');
const host = { rpc: async () => null, onChange: () => () => {}, open() {}, undo() {} };
const inHost = node => React.createElement(hostModule.MarksHostContext.Provider, { value: host }, node);
const html = node => renderToStaticMarkup(inHost(node));

const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const ran = new Date().toISOString();
const ctx = n => Array.from({ length: n }, (_, i) => ({ id: 'c' + i, shape: 'Single', reason: 'A place.' }));
const sumMarks = () => [
  mark('Totals', 'passed', 'Total adds up as a sum, as expected', { lastRan: ran, total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: false, source: '' }, cells: [{ context: 'Grand total', after: '1,256,740' }] }),
  mark('Filters', 'passed', 'No ignored filter in 157 places checked.', { lastRan: ran, chosenContexts: ctx(5), cells: [{ context: 'City: Aachen', after: '122' }] }),
  mark('SQL', 'not run', 'No SQL check is saved for this measure.'),
  mark('Trusted number', 'not run', 'No trusted number is saved for this measure.'),
  mark('Equivalence', 'not run', 'No edit yet.'),
  mark('Contexts', 'passed', 'The sampled contexts have values without errors.', { lastRan: ran, chosenContexts: ctx(5) }),
  mark('Alignment', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.'),
  mark('Performance', 'not run', 'not timed'),
];
const set = (marks, name, patch) => marks.map(x => x.name === name ? { ...x, ...patch } : x);
const dateMarks = () => set(sumMarks(), 'Filters', { state: 'info', chosenContexts: ctx(4),
  sentence: 'This formula ignores Date filters: every place shows 3 Jan 2018. If that is not meant, check the ALLEXCEPT.',
  cells: [{ context: 'Grand total', after: '3 Jan 2018' }, { context: 'Year: 2019', after: '3 Jan 2018' }] });
const editMarks = () => set(sumMarks(), 'Equivalence', { state: 'suspect', sentence: 'This change moved these numbers.', lastRan: ran,
  cells: ['Grand total', 'City: Aachen', 'City: Aalen', 'City: Aalen Waldhausen', 'City: Wasseralfingen', 'City: Unterkochen'].map((context, i) => ({ context, before: String(10 + i), after: String(20 + i) })) });
const match = (score, level, label) => ({ score, level, label });
const description = 'Total units sold. Adds up Sales[Quantity] over every sales row in the current filters, so it follows every slicer.';
const GOOD = match(94, 'matches', 'Matches 94%'), IMPROVE = match(64, 'could be improved', 'Could be improved');

const WORD = /[A-Za-z0-9][A-Za-z0-9%:.,'’()\-]*/g;
// The words a person reads at rest: tags are removed, a measure name counts as one word, glyphs and punctuation are not words.
const words = (markup, names = []) => {
  let text = markup.replace(/<[^>]*>/g, ' ').replace(/&amp;/g, '&').replace(/&#x27;/g, "'").replace(/&quot;/g, '"');
  for (const name of names) text = text.split(name).join(' name ');
  return (text.match(WORD) ?? []).length;
};
const cardAtRest = (marks, matchRow, { name = '', open = null } = {}) => {
  const verdict = m.cardVerdict(marks, matchRow);
  const shown = m.shownMarks(marks);
  const face = React.createElement(panel.CardFace, { verdict, actionable: null, marks: shown.map(x => ({ name: x.mark.name, state: x.state })), open,
    checked: m.checkedAt(ran), running: false, onMark() {}, onVerdict() {}, onCheck() {},
    description: React.createElement(panel.DescriptionLine, { text: description, match: matchRow }),
    add: React.createElement(panel.AddCheck, { reference: 'measure:Sales/' + name, marks, onRecheck() {} }) });
  return html(face);
};
const scenarios = () => [['simple SUM', sumMarks(), GOOD], ['Date filters finding', dateMarks(), IMPROVE], ['after an edit', editMarks(), IMPROVE]];

test('D1 one vocabulary: five states, each with a glyph, a word and a rank (section 2)', () => {
  assert.deepEqual(m.STATE_ORDER, ['broken', 'review', 'unknown', 'overridden', 'verified']);
  assert.deepEqual(m.STATE_ORDER.map(s => [m.GLYPH[s], m.WORD[s]]), [['✗', 'Broken'], ['▲', 'Needs review'], ['○', 'Unknown'], ['⊘', 'Overridden'], ['✓', 'Verified']]);
  assert.equal(new Set(Object.values(m.GLYPH)).size, 5, 'the glyph carries the state, so no two share one');
  assert.deepEqual([...m.MARK_ORDER], ['Totals', 'Filters', 'SQL', 'Trusted number', 'Equivalence', 'Contexts', 'Performance']);
  for (const old of ['Passed', 'Suspect', 'Moved', 'Not run', 'Checked']) assert.ok(!Object.values(m.WORD).includes(old), old + ' is gone from the vocabulary');
});

test('D1 mapping from the engine (section 2): the state of one mark', () => {
  const v = (name, state, sentence, extra) => m.verdictOf(mark(name, state, sentence, extra));
  assert.equal(v('Filters', 'passed', 'No ignored filter in 5 places checked.'), 'verified');
  assert.equal(v('Totals', 'passed', 'Total adds up as a sum', { total: { type: 'sum', expected: true } }), 'overridden');
  assert.equal(v('Totals', 'info', 'Checked as a sum. This is expected', { total: { type: 'sum', expected: true }, lastRan: ran }), 'overridden', 'the engine sends an accepted total as info with expected set');
  assert.equal(v('SQL', 'suspect', 'The measure differs.'), 'broken');
  assert.equal(v('Trusted number', 'suspect', 'The measure differs from a number you trust.'), 'broken');
  for (const name of ['Filters', 'Contexts', 'Totals', 'Performance', 'Equivalence']) assert.equal(v(name, 'suspect', 'A reason.'), 'review', name);
  assert.equal(v('Filters', 'info', 'This formula ignores Date filters.'), 'review');
  // ra1 item 7: the engine sends applies, a reason code and the verdict; the UI reads no sentence to decide.
  const failed = { reasonCode: 'could_not_run', applies: true, verdict: 'unknown' };
  assert.equal(v('Contexts', 'not run', 'Could not check this place.', { lastRan: ran, ...failed }), 'unknown');
  assert.equal(v('Filters', 'not run', 'The filters could not be probed.', failed), 'unknown', 'a failure is not hidden (R8)');
  assert.equal(v('Contexts', 'not run', 'Connect a model to check its contexts.', { reasonCode: 'not_connected', applies: false, verdict: null }), null, 'does not apply: hidden');
  assert.equal(v('Filters', 'not run', 'This check is waiting to run.', { reasonCode: 'waiting', applies: true, pending: true, verdict: null }), null, 'a mark being produced has no verdict');
  assert.equal(v('Filters', 'suspect', 'A filter does not change it.', { reasonCode: 'no_sql', applies: false, verdict: null }), null, 'applies false always hides, whatever the words');
  assert.equal(m.verdictOf(mark('Filters', 'not run', 'The filters could not be probed.'), true), null, 'a mark the engine did not finish gets no verdict from its words');
  assert.equal(v('Totals', 'unknown', 'Not clearly right.', { reasonCode: 'totals_unclear', applies: true, verdict: 'unknown' }), 'unknown', 'between the two Totals cuts: Unknown, never Verified (item 1)');
  assert.equal(v('Filters', 'info', 'Ignores Date filters.', { reasonCode: 'filters_on_purpose', applies: true, verdict: 'overridden' }), 'overridden', 'It is on purpose (item 2)');
});

test('D1 marks hidden when not applying: all seven marks (section 3)', () => {
  const hidden = [
    mark('Totals', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.'),
    mark('Totals', 'not run', 'Semanticus has not measured this judge yet'),
    mark('Totals', 'not run', 'Not scored yet.'),
    mark('Filters', 'not run', 'The filter check could not compare the source rows.'),
    mark('Filters', 'not run', 'This formula can use different sets of rows, so one row count cannot check its filters.'),
    mark('SQL', 'not run', 'No SQL check is saved for this measure.'),
    mark('Trusted number', 'not run', 'No trusted number is saved for this measure.'),
    mark('Equivalence', 'not run', 'No edit yet.'),
    mark('Performance', 'not run', 'Not timed.'),
    mark('Performance', 'not run', 'not timed'),
    mark('Performance', 'not run', 'Include performance is off for this measure and run.'),
    mark('Contexts', 'not run', 'Connect a model to check its contexts.'),
  ];
  const hiddenNames = new Set();
  for (const h of hidden) {
    assert.equal(m.verdictOf(h, h.name !== 'Contexts'), null, `${h.name} "${h.sentence}" is hidden`);
    assert.deepEqual(m.shownMarks([h]), [], h.name);
    hiddenNames.add(h.name);
  }
  assert.deepEqual([...hiddenNames].sort(), [...m.MARK_ORDER].sort(), 'each of the seven marks has a case that hides it');
  // f173: a distinct count has no verdict but is shown with its type, so it is not in the hidden list.
  const distinct = mark('Totals', 'info', 'Checked as a distinct count. Totals for this type are shown, not judged', { total: { rule: 'distinct_count', type: 'distinct count', confidence: .9, expected: false, source: '' } });
  assert.equal(m.verdictOf(distinct, true), null);
  assert.deepEqual(m.cardMarks([distinct]).map(x => [x.mark.name, x.state]), [['Totals', 'info']]);
  // A mark that applies and ran is shown, and a mark that applies but could not run is Unknown.
  assert.deepEqual(m.shownMarks(sumMarks()).map(x => x.mark.name), ['Totals', 'Filters', 'Contexts'], 'a SUM with no saved SQL or number and no edit shows three');
  assert.deepEqual(m.shownMarks(editMarks()).map(x => x.mark.name), ['Totals', 'Filters', 'Equivalence', 'Contexts']);
  assert.deepEqual(m.shownMarks(set(sumMarks(), 'SQL', { state: 'passed', sentence: 'Same.', lastRan: ran })).map(x => x.mark.name), ['Totals', 'Filters', 'SQL', 'Contexts']);
  assert.equal(m.shownMarks(set(sumMarks(), 'Totals', { state: 'not run', sentence: 'Not scored yet. Scoring needs a Pro plan.' })).some(x => x.mark.name === 'Totals'), false, 'no licence: no Totals mark');
  const alignment = m.shownMarks([mark('Alignment', 'passed', 'Matches.', { lastRan: ran })]);
  assert.deepEqual(alignment, [], 'Alignment is the Description score, never a mark');
});

test('D1 the card verdict line, case by case (section 4.4)', () => {
  const line = (marks, matchRow) => m.cardVerdict(marks, matchRow).line;
  assert.equal(line(sumMarks(), GOOD), '✓ Verified by 3 checks');
  assert.equal(line([sumMarks()[0]], GOOD), '✓ Verified by 1 check');
  assert.equal(line(set(sumMarks(), 'Equivalence', { state: 'passed', sentence: 'Nothing moved on the sampled grid.', lastRan: ran }), GOOD), '✓ Verified by 4 checks. Nothing moved.');
  assert.equal(line(editMarks(), GOOD), '▲ Needs review: your edit moved 6 or more numbers');
  assert.equal(line(set(sumMarks(), 'Equivalence', { state: 'suspect', sentence: 'This change moved these numbers.', lastRan: ran, cells: [{ context: 'A', before: '1', after: '2' }, { context: 'B', before: '1', after: '2' }] }), GOOD), '▲ Needs review: your edit moved 2 numbers');
  assert.equal(line(dateMarks(), GOOD), '▲ Needs review: ignores Date filters');
  assert.equal(line(set(sumMarks(), 'Filters', { state: 'suspect', sentence: 'The filter changes the source rows, but this formula still uses all of them.' }), GOOD), '▲ Needs review: a filter does not change it');
  assert.equal(line(set(sumMarks(), 'Filters', { state: 'suspect', sentence: 'Some fact rows have no related key, so their filters can lose these rows.' }), GOOD), '▲ Needs review: rows with no matching key');
  assert.equal(line(set(sumMarks(), 'Contexts', { state: 'suspect', sentence: 'It is blank in some places only.' }), GOOD), '▲ Needs review: blank or error in some places');
  assert.equal(line(set(sumMarks(), 'Totals', { state: 'suspect', sentence: 'The total does not fit a sum: rows 10, 20; total 99' }), GOOD), '▲ Needs review: the total does not fit a sum');
  assert.equal(line(set(sumMarks(), 'Performance', { state: 'suspect', sentence: 'Slow.', lastRan: ran, timings: [{ context: 'City', medianMs: 3200, minMs: 3000, maxMs: 3400 }] }), GOOD), '▲ Needs review: slow, 3.2 s at City');
  assert.equal(line(set(sumMarks(), 'Performance', { state: 'suspect', sentence: 'Slower.', lastRan: ran, timings: [{ context: 'City', medianMs: 300, minMs: 290, maxMs: 310, beforeMedianMs: 100, regression: true }] }), GOOD), '▲ Needs review: slower after your edit');
  assert.equal(line(sumMarks(), match(41, 'does not match', 'Does not match')), '▲ Needs review: the Description does not match the formula');
  assert.equal(line(set(sumMarks(), 'SQL', { state: 'suspect', sentence: 'The measure differs.', lastRan: ran, cells: [{ context: 'Grand total', before: '100', after: '95' }] }), GOOD), '✗ Broken: differs from SQL by 5', 'the spec: differs from SQL by <gap>');
  assert.equal(line(set(sumMarks(), 'Trusted number', { state: 'suspect', sentence: 'The measure differs from a number you trust.', lastRan: ran, cells: [{ context: 'Grand total', before: '100', after: '95' }] }), GOOD), '✗ Broken: 5 lower than your number');
  const failed = { reasonCode: 'could_not_run', applies: true, verdict: 'unknown' };
  assert.equal(line(set(sumMarks(), 'Contexts', { state: 'not run', sentence: 'Could not check this place.', ...failed }), GOOD), '○ Unknown: Contexts could not run');
  assert.equal(line(set(sumMarks(), 'Totals', { state: 'not run', sentence: 'The scoring service failed.', lastRan: ran, ...failed }), GOOD), '○ Unknown: Totals could not be scored');
  assert.equal(line(set(sumMarks(), 'Totals', { state: 'unknown', sentence: 'Not clearly right.', reasonCode: 'totals_unclear', applies: true, verdict: 'unknown' }), GOOD), '○ Unknown: the total is not clearly right for a sum');
  assert.equal(line(set(sumMarks(), 'Filters', { state: 'info', sentence: 'This formula ignores Date filters.', reasonCode: 'filters_on_purpose', applies: true, verdict: 'overridden' }), GOOD), '⊘ Overridden: ignores filters on purpose');
  assert.equal(line(set(sumMarks(), 'Totals', { state: 'info', sentence: 'Checked as a sum. This is expected', total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: true, source: '' } }), GOOD), '⊘ Overridden: total marked as expected');
  const two = set(set(sumMarks(), 'Contexts', { state: 'suspect', sentence: 'Blank.' }), 'SQL', { state: 'suspect', sentence: 'Differs.', lastRan: ran, cells: [{ before: '10', after: '12' }] });
  assert.equal(line(two, GOOD), '✗ Broken: differs from SQL by 2 (+1 more)', 'the worst line, then the others');
  assert.equal(line(set(sumMarks(), 'Contexts', { state: 'suspect', sentence: 'Blank.' }).map(x => x.name === 'Filters' ? { ...x, state: 'info', sentence: 'This formula ignores Date filters.' } : x), GOOD), '▲ Needs review: ignores Date filters (+1 more)');
  // Gap 2 (d1 tester): a measure with no verdict says why.
  assert.equal(line([], null), 'Not checked yet: no run has included it');
  assert.equal(line(sumMarks().map(x => ({ ...x, lastRan: undefined, state: 'not run' })), GOOD), 'Not checked yet: no run has included it');
  assert.equal(m.cardVerdict(sumMarks(), GOOD, { running: true }).line, 'Checking now');
});

test('D1 Keep: the engine holds the Keep, so Equivalence stops applying until the next edit (section 7.5, ra1 item 6)', () => {
  assert.equal(m.cardVerdict(editMarks(), GOOD).state, 'review');
  // The engine sends a kept Equivalence as not applying (reason kept): every surface and the assistant read the same.
  const kept = set(editMarks(), 'Equivalence', { reasonCode: 'kept', applies: false, verdict: null });
  assert.equal(m.cardVerdict(kept, GOOD).line, '✓ Verified by 3 checks');
  assert.equal(m.treeRowItem('Total Quantity', kept, { text: description, match: GOOD }).description, '✓');
  // The engine's rule (a Keep holds by the inputs the edit was compared on) is held in CheckedVRc28Tests; no TS copy of it.
  assert.equal(typeof m.isKept, 'undefined', 'the old TS Keep rule is gone');
  const sources = ['../src/extension.ts', '../webview/src/bridge.ts', '../webview/src/checked-grid.tsx', '../webview/src/marks-host.ts']
    .map(f => readFileSync(new URL(f, import.meta.url), 'utf8')).join('\n');
  assert.doesNotMatch(sources, /keptEdits|workspaceState\.get<[^>]*>\(KEPT/, 'no Keep store in the extension');
  assert.match(readFileSync(new URL('../webview/src/marks-panel.tsx', import.meta.url), 'utf8'), /rpc<CheckCache>\('keepMeasureEdit', reference\)/, 'Keep is an engine call');
});

test('D1 tree row: the name and one glyph, nothing else (section 4.1)', () => {
  for (const [what, marks, row, glyph] of [['SUM', sumMarks(), GOOD, '✓'], ['Date', dateMarks(), IMPROVE, '▲'], ['edit', editMarks(), IMPROVE, '▲']]) {
    const item = m.treeRowItem('Total Quantity', marks, { text: description, match: row });
    assert.equal(item.description, glyph, what);
    assert.equal(words(item.description), 0, what + ' has no words on the row');
    assert.doesNotMatch(item.description, /Checked|Moved|Filters|–|\+/, what);
  }
  assert.equal(m.treeRowItem('Gross Profit', [], { text: 'Contoso sales minus cost.' }).description, '', 'no glyph until a mark that applies has run');
  assert.equal(m.treeRowItem('Gross Profit', sumMarks().map(x => ({ ...x, state: 'not run', lastRan: undefined })), {}).description, '');
  const info = m.treeRowItem('Customers', [mark('Totals', 'info', 'Checked as a distinct count.', { total: { rule: 'distinct_count', type: 'distinct count', confidence: .9, expected: false, source: '' } })], {});
  assert.equal(info.description, '', 'a distinct count is not a Checked row');
});

test('D1 tree hover: three lines, 25 words or fewer, starts with the verdict line (section 4.1)', () => {
  const item = m.treeRowItem('Date New Customer', dateMarks(), { text: "The date of a customer's first order. It is the first date.", match: IMPROVE });
  const lines = item.tooltip.split('\n');
  assert.equal(lines[0], '▲ Needs review: ignores Date filters');
  assert.equal(lines[1], '✓ Totals: sum   ▲ Filters   ✓ Contexts');
  assert.equal(lines[2], "The date of a customer's first order.");
  assert.ok(words(item.tooltip) <= 25, `${words(item.tooltip)} words`);
  const long = m.treeRowItem('Total Quantity', editMarks(), { text: description + ' ' + description, match: IMPROVE });
  assert.ok(words(long.tooltip) <= 25, `the longest case is ${words(long.tooltip)} words`);
  assert.ok(long.tooltip.startsWith(m.cardVerdict(editMarks(), IMPROVE).line), 'the first line is the card verdict line');
  assert.equal(m.treeRowItem('Gross Profit', [], {}).tooltip, 'Not checked yet', 'spec 4.1 (wr1 S3)');
  for (const gone of ['Select the measure to open', 'Description:', 'Not scored yet', 'Jev', 'not run']) assert.ok(!item.tooltip.includes(gone), gone);
  assert.equal(item.mark, 'Filters', 'a click opens the worst mark in Properties');
});

test('D1 the Checks page groups by verdict, worst first, and says it in one line (section 4.2)', () => {
  const rows = [
    { ref: 'a', name: 'Total Cost', marks: sumMarks(), match: GOOD },
    { ref: 'b', name: 'Date New Customer', marks: dateMarks(), match: IMPROVE },
    { ref: 'c', name: 'Gross Profit', marks: [] },
    { ref: 'd', name: 'Margin', marks: set(sumMarks(), 'SQL', { state: 'suspect', sentence: 'Differs.', lastRan: ran }), match: GOOD },
    { ref: 'e', name: 'Orders', marks: sumMarks() },
    { ref: 'f', name: 'Accepted', marks: set(sumMarks(), 'Totals', { state: 'info', sentence: 'Checked as a sum. This is expected', total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: true, source: '' } }) },
  ];
  const groups = m.groupByState(rows);
  assert.deepEqual(groups.map(g => g.group), ['broken', 'review', 'verified', 'unchecked']);
  assert.deepEqual(groups.map(g => g.rows.map(r => r.name)), [['Margin'], ['Date New Customer'], ['Accepted', 'Orders', 'Total Cost'], ['Gross Profit']]);
  assert.deepEqual(groups.map(g => m.GROUP_WORDS[g.group]), ['Broken', 'Needs review', 'Verified', 'Not checked yet']);
  assert.equal(m.checksVerdictLine(groups, rows.length), '1 broken · 1 needs review · 3 verified · 1 not checked yet');
  assert.equal(m.checksVerdictLine([{ group: 'review', rows: [1, 2] }, { group: 'verified', rows: [1] }], 3), '2 need review · 1 verified');
  assert.equal(m.checksVerdictLine(m.groupByState([{ ref: 'a', name: 'A', marks: [] }, { ref: 'b', name: 'B', marks: [] }]), 2), '2 measures. Not checked yet.');
  assert.equal(m.checksVerdictLine(m.groupByState([{ ref: 'a', name: 'A', marks: [] }]), 1), '1 measure. Not checked yet.');
});

test('D1 word budget: tree row, 1 glyph and no words per row (12 rows)', () => {
  const rows = ['# Customers', '# Orders', '# Sales', 'Date Lost Customer', 'Date New Customer', 'Gross Profit', 'Quantity PY', 'Sales Amount', 'Sales PY', 'ShowValueForDates', 'Total Cost', 'Total Quantity'];
  for (const [i, name] of rows.entries()) {
    const item = m.treeRowItem(name, i === 4 ? dateMarks() : i === 5 || i === 9 ? [] : sumMarks(), { text: description, match: GOOD });
    assert.ok(item.description.length <= 1 && words(item.description) === 0, `${name}: ${item.description}`);
  }
});

test('D1 word budget: Checks page header 12 words or fewer, rows name and a glyph, headings 3 words and a count', () => {
  const rows = Array.from({ length: 12 }, (_, i) => ({ ref: 'r' + i, name: 'Measure ' + i, marks: i === 0 ? dateMarks() : i < 10 ? sumMarks() : [], match: GOOD }));
  const groups = m.groupByState(rows);
  const verdict = m.checksVerdictLine(groups, rows.length);
  const header = html(React.createElement(page.ChecksHeader, { verdict, problems: '', actionable: null, tools: React.createElement('button', { 'aria-label': 'More' }, '⋯') }));
  assert.ok(words(header) <= 12, `the header is ${words(header)} words: ${verdict}`);
  const open = html(React.createElement(page.CheckGroups, { groups, open: null, onOpen() {}, detail: () => null, collapsed: true, onToggle() {} }));
  const names = rows.map(r => r.name);
  const rowMarkup = [...open.matchAll(/<div class="cp-row[^"]*" data-testid="checks-row-[^"]+">(.*?)<\/button><\/div>/g)].map(x => x[1]);
  assert.equal(rowMarkup.length, 9 + 1, 'the collapsed group hides its rows');
  for (const r of rowMarkup) assert.ok(words(r, names) <= 1, `a row is ${words(r, names)} words: ${r}`);
  for (const [, heading] of open.matchAll(/<(?:h2|button)[^>]*class="cp-ghead"[^>]*>(.*?)<\/(?:h2|button)>/g))
    assert.ok(words(heading.replace(/<span class="cp-count">.*?<\/span>/, '')) <= 3, heading);
  for (const gone of ['Not scored yet', 'Jev', 'not run', 'Last checked', 'Suspect:', 'The seven marks']) assert.ok(!open.includes(gone) && !header.includes(gone), gone);
  const alone = html(React.createElement(page.CheckGroups, { groups: m.groupByState([{ ref: 'a', name: 'A', marks: [] }]), open: null, onOpen() {}, detail: () => null, collapsed: true, onToggle() {} }));
  assert.ok(alone.includes('checks-row-a') || alone.includes('cp-rowwrap'), 'Not checked yet is open when it is the only group');
  assert.ok(open.includes('aria-expanded="false"'), 'Not checked yet is collapsed after Run');
  const nothing = m.checksVerdictLine(m.groupByState(rows.map(r => ({ ...r, marks: [] }))), 12);
  assert.ok(words(nothing) + 1 <= 12);
});

test('D1 word budget: run plan 15 words or fewer, Choose 60 words or fewer, no notes', () => {
  const picks = [...Array(4)].map((_, i) => ({ measureRef: 'measure:Sales/C' + i, group: 'core', reason: 'Used by 4 other measures.' }))
    .concat([...Array(5)].map((_, i) => ({ measureRef: 'measure:Sales/R' + i, group: 'risky', reason: 'Formula uses filter changes.' })))
    .concat([...Array(3)].map((_, i) => ({ measureRef: 'measure:Sales/O' + i, group: 'oldest', reason: 'Never checked.' })));
  const p = { id: 'p', totalMeasures: 12, batchSize: 40, estimatedSeconds: 80, sentence: '', picks, firstRun: true };
  const summary = m.planSummary(p, false);
  assert.equal(summary.sentence, 'Checking 12 of 12: 4 core, 5 risky, 3 oldest. About 2 minutes.');
  assert.doesNotMatch(summary.sentence, /first run|estimate improves/);
  const rest = html(React.createElement(plan.PlanLine, { sentence: summary.sentence, running: false, canRun: true, onChoose() {}, onRun() {} }));
  assert.ok(words(rest) <= 15, `the run plan is ${words(rest)} words`);
  assert.match(rest, />Choose</);
  assert.match(rest, />Run</);
  for (const gone of ['Run plan', 'before it runs', 'Include performance', 'Show the reasons', 'Choose instead', 'sweep']) assert.ok(!rest.includes(gone), gone);
  const on = m.planSummary(p, true);
  assert.notEqual(on.sentence, summary.sentence, 'the estimate changes in place');
  assert.doesNotMatch(on.sentence, /performance/i, 'and no note comes with it');
  const names = new Map(picks.map(x => [x.measureRef, x.measureRef.split('/')[1]]));
  const choose = html(React.createElement(plan.ChooseBody, { plan: p, include: false, added: 'about 4 minutes', name: ref => names.get(ref), more: new Set(), onInclude() {}, onMore() {}, onPick() {} }));
  assert.match(choose, /Include performance/);
  assert.match(choose, /adds about 4 minutes/);
  assert.match(choose, /Pick your own/);
  assert.match(choose, /Core \(4\)/);
  const withoutNames = choose.replace(/<b>.*?<\/b>/g, ' ');
  assert.ok(words(withoutNames) <= 60, `Choose is ${words(withoutNames)} words`);
  const running = html(React.createElement(plan.PlanLine, { sentence: summary.sentence, running: true, done: 3, total: 12, canRun: true, onChoose() {}, onRun() {} }));
  assert.match(running, /Checking 3 of 12\./);
  assert.match(running, /<progress/);
  assert.match(running, /<button[^>]*disabled=""[^>]*>Run</, 'Run is disabled while it runs');
});

test('D1 word budget: the measure card at rest, three cases, 30 words or fewer (and Properties, the same card)', () => {
  for (const [what, marks, row] of scenarios()) {
    const markup = cardAtRest(marks, row);
    assert.ok(words(markup) <= 30, `${what}: the card is ${words(markup)} words`);
    const text = markup.replace(/<[^>]*>/g, ' ');
    for (const gone of ['Not run', 'Not scored yet', 'Jev', 'Passed', 'Marks', 'Suspect', 'Moved', 'Source not recorded', 'Last checked', 'Check it now']) assert.ok(!text.includes(gone), `${what}: ${gone}`);
    assert.match(markup, /Check now/);
    assert.match(markup, /Add a check/);
    assert.match(markup, /Checked \d\d:\d\d/);
    assert.ok(words(markup) + 2 <= 32, `${what}: Properties adds the name`);
  }
  const sum = cardAtRest(sumMarks(), GOOD);
  assert.match(sum, /Verified<\/b> by 3 checks/);
  assert.deepEqual([...sum.matchAll(/data-testid="mk-row-([^"]+)"/g)].map(x => x[1]), ['Totals', 'Filters', 'Contexts'], 'Totals, Filters and Contexts on a SUM');
  const edit = cardAtRest(editMarks(), IMPROVE);
  assert.match(edit, /Needs review<\/b>: your edit moved 6 or more numbers/);
  assert.ok(edit.includes('mk-row-Equivalence'), 'Equivalence is shown after an edit');
  assert.match(cardAtRest(dateMarks(), IMPROVE), /Needs review<\/b>: ignores Date filters/);
  assert.match(sum, /Matches 94%/, 'Kane\'s band (spec 4.4)');
  assert.match(sum, /title="How well the Description matches the formula"/, 'gap 5: the hover says what the score compares');
  assert.match(edit, /Could be improved, 64%/);
  assert.match(sum, /class="mk-dot mk-dot-good"/);
  assert.match(edit, /class="mk-dot mk-dot-warn"/);
  assert.match(sum, /mk-one/, 'the Description is one line with an ellipsis');
});

test('D1 word budget: a card with no run reads Not checked yet and Check now is the primary button', () => {
  const markup = cardAtRest([], null);
  assert.match(markup, /Not checked yet/);
  assert.match(markup, /sem-btn-primary[^>]*>Check now</);
  assert.ok(words(markup) <= 30);
});

test('D1 mark detail: one sentence of prose, a table of six rows or fewer, one primary action (section 4.4)', () => {
  const detail = (mk, state, extra = {}) => html(React.createElement(panel.MarkCard, { reference: 'measure:Sales/Total Quantity', mark: mk, state, others: [], onRecheck() {}, onKeep() {}, onPropose() {}, ...extra }));
  const filters = detail({ ...dateMarks()[1], reasonCode: 'filters_lost', actionCodes: ['fix_formula', 'on_purpose'] }, 'review');
  assert.match(filters, /Every place shows 3 Jan 2018\. The Description does not say so\. If that is not meant, check the ALLEXCEPT\./);
  // ra1 item 3: two actions of equal weight; rewriting the Description to fit the formula is behind the full Description.
  assert.match(filters, />Fix the formula</);
  assert.match(filters, />It is on purpose</);
  assert.doesNotMatch(filters, /Propose a better Description|sem-btn-primary/);
  assert.match(detail({ ...dateMarks()[1], reasonCode: 'filters_on_purpose', actionCodes: ['not_on_purpose'] }, 'overridden'), /Ignores Date filters on purpose, as you said\.[\s\S]*>It is not on purpose</);
  assert.match(filters, />Where it looked</, 'no second count beside the places sentence (wr1 F9)');
  assert.ok(!filters.includes('data-testid="mk-contexts"'), 'where it looked is one more click');
  assert.ok(words(filters.replace(/<table.*<\/table>/, ' ')) <= 25 + 8, 'about 25 words of prose');
  const edit = detail(editMarks()[4], 'review');
  assert.match(edit, /Your edit moved numbers in 6 or more places\./);
  assert.match(edit, /sem-btn-primary[^>]*>Keep</);
  assert.match(edit, /Undo my edit/);
  assert.equal((edit.match(/<tbody>.*<\/tbody>/)[0].match(/<tr>/g) ?? []).length, 6, 'a table of six rows');
  assert.match(edit, /<th>Before<\/th>/);
  const totals = detail(sumMarks()[0], 'verified');
  assert.match(totals, /Checked as a sum\. The total fits its rows\./);
  assert.ok(!totals.includes('Jev read') && !totals.includes('Confidence'), 'the confidence and the source are cut');
  assert.match(detail(sumMarks()[1], 'verified'), /No ignored filter in 157 places\./);
  // rc27 tester gap 6: "places" means the cells checked, on Contexts as on Filters, not the 5 contexts picked.
  assert.match(detail({ ...sumMarks()[5], sentence: 'Values in all 163 places checked.' }, 'verified'), /Values in all 163 places\./);
  assert.match(detail(mark('Performance', 'passed', 'Fast.', { lastRan: ran, timings: [{ context: 'City', medianMs: 149, minMs: 140, maxMs: 160, coldMs: 300 }] }), 'verified', { limitMs: 3000 }), /Slowest: City, 149 ms\. Limit 3 s\./);
  assert.match(detail(mark('Performance', 'suspect', 'Slow.', { lastRan: ran, timings: [{ context: 'City', medianMs: 3200, minMs: 3000, maxMs: 3300 }] }), 'review', { limitMs: 3000 }), /Slowest: City, 3\.2 s\. Over the 3 s limit\./);
  assert.match(detail(mark('Contexts', 'not run', 'Could not check this place.', { lastRan: ran }), 'unknown'), /Could not check this place\./);
  assert.match(detail(mark('Filters', 'passed', 'Passed. Ignores Date filters on purpose, as the Description says.', { lastRan: ran }), 'verified'), /Ignores Date filters on purpose, as the Description says\./);
  const overridden = detail(mark('Totals', 'info', 'Checked as a sum. This is expected', { lastRan: ran, total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: true, source: '' } }), 'overridden');
  assert.match(overridden, /You marked this total as expected\./);
  assert.match(detail(mark('Totals', 'suspect', 'The total does not fit a sum: rows 1, 2; total 9', { lastRan: ran, total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: false, source: '' } }), 'review'), /This is expected</);
  assert.match(detail(mark('Equivalence', 'passed', 'Nothing moved on the sampled grid.', { lastRan: ran }), 'verified'), /Nothing moved\./);
  for (const gone of ['Close', 'Passed:', 'Contexts tested', 'If you meant this change', 'It runs by itself']) assert.ok(!(edit + filters + totals).includes(gone), gone);
});

test('D1 a scoring cause the person can act on shows one line on the card and the Checks page, never on a row', () => {
  const pro = m.actionableReason('Not scored yet. Scoring needs a Pro plan.');
  assert.equal(pro, 'Scoring needs a Pro plan.');
  assert.equal(m.actionableReason('Scoring is off for this model.'), 'Scoring is off for this model.');
  assert.equal(m.actionableReason('The scoring allowance is used up for now.'), 'The scoring allowance is used up for now.');
  assert.equal(m.actionableReason('Not scored yet. This score turns on when Jev scoring is released.'), null, 'a service-side cause shows nothing');
  const marks = set(sumMarks(), 'Totals', { state: 'not run', sentence: 'Not scored yet. Scoring needs a Pro plan.' });
  const tree = m.treeRowItem('Total Quantity', marks, { text: description, match: GOOD });
  assert.ok(!tree.tooltip.includes('Pro plan') && !tree.description.includes('Pro'), 'never on the row or the hover');
  const rowMarkup = html(React.createElement(page.CheckRow, { row: { id: 'a', name: 'Total Quantity', state: m.rowVerdict(marks, GOOD), detail: null }, open: false, onOpen() {} }));
  assert.ok(!rowMarkup.includes('Pro plan'));
  const header = html(React.createElement(page.ChecksHeader, { verdict: '1 verified', actionable: pro }));
  assert.match(header, /Scoring needs a Pro plan\./);
  const face = html(React.createElement(panel.CardFace, { verdict: m.cardVerdict(marks, GOOD), actionable: pro, marks: [], open: null, checked: '', onMark() {}, onVerdict() {}, onCheck() {}, description: null }));
  assert.match(face, /Scoring needs a Pro plan\./);
});

test('D1 verdicts do not change: every mark that was Passed is Verified, every Suspect is Broken or Needs review (R3, no engine change)', () => {
  const cases = [['Filters', 'passed', 'verified'], ['Contexts', 'passed', 'verified'], ['Totals', 'passed', 'verified'], ['SQL', 'passed', 'verified'], ['Trusted number', 'passed', 'verified'], ['Equivalence', 'passed', 'verified'], ['Performance', 'passed', 'verified'],
    ['Filters', 'suspect', 'review'], ['Contexts', 'suspect', 'review'], ['Totals', 'suspect', 'review'], ['Performance', 'suspect', 'review'], ['Equivalence', 'suspect', 'review'], ['SQL', 'suspect', 'broken'], ['Trusted number', 'suspect', 'broken'], ['Filters', 'info', 'review']];
  for (const [name, state, want] of cases) assert.equal(m.verdictOf(mark(name, state, 'A result.', { lastRan: ran })), want, `${name} ${state}`);
  const descriptionOnly = m.cardVerdict([], match(41, 'does not match', 'Does not match'));
  assert.equal(descriptionOnly.state, 'review');
  assert.equal(m.cardVerdict([], match(70, 'matches', 'Matches')).state, null, 'a matching score does not change the verdict');
  assert.equal(m.cardVerdict(sumMarks(), IMPROVE).state, 'verified', 'could be improved is a score only');
  assert.equal(m.descriptionScore({ score: null, level: 'not scored yet', label: 'not scored yet' }).band, null, 'a score that is not served is hidden');
});

test('D1 every sentence section 5 cuts is out of the screens', () => {
  const read = file => readFileSync(new URL('../webview/src/' + file, import.meta.url), 'utf8') + (file === 'marks-panel.tsx' ? '' : '');
  const screens = ['marks-panel.tsx', 'run-plan.tsx', 'checks-page.tsx'].map(read).join('\n') + readFileSync(new URL('../src/marksModel.ts', import.meta.url), 'utf8').replace(/const NOT_APPLYING[\s\S]*?\n};/, '');
  const visible = screens.replace(/aria-label="Run plan"/g, '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/ \/\/ .*$/gm, '');
  const cut = ['This score turns on when Jev scoring is released', 'Not run yet.', 'Not timed. Timing is off unless', 'More places are available', 'Blank results have no rows to calculate',
    'passed, ', 'Source not recorded', 'How likely the Description statements', 'If you meant this change', 'It runs by itself after your next edit', 'This change moved these numbers',
    'For example ', 'Contexts tested', 'Jev read:', 'It may come from a lakehouse', 'The current value is filled in', 'Within the 3000 ms threshold', 'Request a full sweep',
    'Checking this measure. Results show here', 'Reading the marks.', 'Preparing five warm runs', 'Select the measure to open its marks', 'The seven marks for each measure', 'Your own saved checks',
    'Keys that do not match, and how many rows', 'Run plan', 'before it runs', 'first run. The estimate improves', 'Times each measure: five warm runs', 'The sweep checks the rest',
    'they keep their marks', 'Nothing suspect', 'Performance is on. The estimate includes timing', 'Performance is off.', 'Tick measures, a folder or a table', 'Choose instead', 'Show the reasons', 'Check it now', 'Show the full logic', 'Not scored yet'];
  for (const sentence of cut) assert.ok(!visible.includes(sentence), `"${sentence}" is cut`);
  assert.doesNotMatch(screens, /—/, 'no em dash');
});

test('D1 one vocabulary everywhere: the tree, the Checks page, the card, the detail and Properties read the same model', () => {
  for (const file of ['marks-panel.tsx', 'checks-page.tsx', 'run-plan.tsx', 'checked-grid.tsx']) {
    const source = readFileSync(new URL('../webview/src/' + file, import.meta.url), 'utf8');
    assert.doesNotMatch(source.replace(/\(Fail\|Suspect\)/g, ''), /\b(Suspect|Moved|Passed)\b/, file + ' has no old state word');
  }
  const grid = readFileSync(new URL('../webview/src/checked-grid.tsx', import.meta.url), 'utf8');
  assert.match(grid, /MeasureMarksPanel/, 'Properties shows the same card');
  assert.match(grid, /RunPlan/, 'and the same run plan');
  const pageSource = readFileSync(new URL('../webview/src/checks-page.tsx', import.meta.url), 'utf8');
  assert.match(pageSource, /MeasureMarksPanel/);
  assert.match(pageSource, /groupByState/);
  assert.doesNotMatch(pageSource, /worstFirst|treeRow\(/);
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(ext, /treeRowItem\(n\.name, stateMarks\(state\)/, 'the tree renders the engine check state');
  assert.doesNotMatch(ext, /msg\?\.type === 'keptEdits'/, 'no Keep store in the extension (ra1 item 6)');
  assert.match(ext, /'keepMeasureEdit'/, 'Keep is an engine call the Properties card may make');
  assert.match(ext, /onNotification\('checks\/didChange'/, 'the engine pushes the check state');
});

// The six gaps the d1 mockup tester found (verdict 2026-10-28-checks-mockup-1), built into fx23.
const detailOf = (mk, state, others = [], extra = {}) => html(React.createElement(panel.MarkCard, { reference: 'measure:Sales/Total Quantity', mark: mk, state, others, onRecheck() {}, onKeep() {}, onPropose() {}, ...extra }));
const timeMarks = () => set(set(sumMarks().map(x => ({ ...x, lastRan: undefined, state: 'not run', cells: [] })), 'Filters',
  { sentence: 'This formula moves the date window, so the row count test does not apply. Add a number you trust to check it.', reasonCode: 'filters_no_rows', applies: false, verdict: null }),
  'Totals', { sentence: 'Not scored yet. This score turns on when Jev scoring is released.', reasonCode: 'totals_off', applies: false, verdict: null })
  .map(x => x.name === 'Contexts' ? { ...x, sentence: 'Not run yet.' } : x);

test('D1 gap 1: a Broken measure is red, says why, and says what to fix', () => {
  const broken = set(sumMarks(), 'Trusted number', { state: 'suspect', sentence: 'The measure differs from a number you trust.', lastRan: ran, cells: [{ context: 'Grand total', before: '628,370', after: '628,358' }] });
  const verdict = m.cardVerdict(broken, GOOD);
  assert.equal(verdict.line, '✗ Broken: 12 lower than your number');
  const face = cardAtRest(broken, GOOD);
  assert.match(face, /mk-v-broken/, 'the verdict carries the red token');
  assert.match(face, /mk-s-broken/, 'and so does the mark');
  assert.ok(words(face) <= 30, `${words(face)} words`);
  const detail = detailOf(broken[3], 'broken', broken);
  assert.match(detail, /12 lower than your number\. Fix the formula, or the number it is compared with, then check again\./);
  assert.match(detail, /<th>You trust<\/th><th>Measure<\/th>/);
  assert.match(detail, />Check again</);
  assert.match(detailOf(set(sumMarks(), 'SQL', { state: 'suspect', sentence: 'Differs.', lastRan: ran, cells: [{ context: 'Grand total', before: '100', after: '95' }] })[2], 'broken'), /Differs from SQL by 5\. Fix the formula/);
  assert.equal(m.treeRowItem('Total Quantity', broken, { match: GOOD }).description, '✗');
});

test('D1 gap 2: a measure with no verdict says why; one that ran with no check that applies is Unknown, not "not checked yet"', () => {
  // The engine says a row count cannot check this formula (reason filters_no_rows): the mark does not apply, whatever its words.
  assert.equal(m.applies(timeMarks()[1]), false);
  assert.equal(m.applies(mark('Filters', 'not run', 'Any reworded sentence.', { reasonCode: 'filters_no_rows', applies: false })), false);
  const never = m.cardVerdict(timeMarks(), null);
  assert.equal(never.state, null);
  assert.equal(never.line, 'Not checked yet: no run has included it');
  const after = m.cardVerdict(timeMarks(), null, { checked: true });
  assert.equal(after.state, 'unknown', 'it was in the run: nobody knows if it is right (R8)');
  assert.equal(after.line, '○ Unknown: this formula moves the date window');
  const tree = m.treeRowItem('Quantity PY', timeMarks(), { checked: true, text: 'Units sold in the same period last year.' });
  assert.equal(tree.description, '○', 'the tree row has a glyph');
  assert.match(tree.tooltip, /^○ Unknown: this formula moves the date window/, 'and its hover says why');
  assert.ok(words(tree.tooltip) <= 25);
  // The header no longer reads "Checking 12 of 12" beside "2 not checked yet" after a run of all twelve.
  const rows = Array.from({ length: 12 }, (_, i) => ({ ref: 'r' + i, name: 'M' + i, marks: i < 10 ? sumMarks() : timeMarks(), match: GOOD, checked: true }));
  const groups = m.groupByState(rows);
  assert.equal(m.checksVerdictLine(groups, 12), '2 unknown · 10 verified');
  assert.ok(!groups.some(g => g.group === 'unchecked'));
  // "Not checked yet" opens when pressed.
  const unchecked = m.groupByState([{ ref: 'a', name: 'A', marks: sumMarks() }, { ref: 'b', name: 'B', marks: [] }]);
  const shut = html(React.createElement(page.CheckGroups, { groups: unchecked, open: null, onOpen() {}, detail: () => null, collapsed: true, onToggle() {} }));
  const opened = html(React.createElement(page.CheckGroups, { groups: unchecked, open: null, onOpen() {}, detail: () => null, collapsed: false, onToggle() {} }));
  assert.ok(!shut.includes('checks-row-b') && opened.includes('checks-row-b'), 'the group opens');
  assert.match(opened, /aria-expanded="true"/);
});

test('D1 gap 4: one value per place; Totals and Filters never show two grand totals for one measure', () => {
  const marks = set(set(dateMarks(), 'Totals', { cells: [{ context: 'Grand total', after: '1 Jan 2018' }] }), 'Filters', { cells: [{ context: 'Grand total', after: '3 Jan 2018' }, { context: 'Year: 2019', after: '3 Jan 2018' }] });
  const values = m.placeValues(marks);
  assert.equal(values.get('grand total'), '3 Jan 2018');
  const totals = detailOf(marks[0], 'verified', marks), filters = detailOf(marks[1], 'review', marks);
  const grand = markup => /<td>Grand total<\/td><td[^>]*>([^<]+)<\/td>/.exec(markup)?.[1];
  assert.equal(grand(totals), grand(filters));
  assert.equal(grand(totals), '3 Jan 2018');
  // A number in two formats reads the model's format on both.
  const sum = set(sumMarks(), 'Totals', { cells: [{ context: 'Grand total', after: '1256740' }] });
  const both = set(sum, 'Filters', { cells: [{ context: 'Grand total', after: '1,256,740' }] });
  assert.equal(grand(detailOf(both[0], 'verified', both)), '1,256,740');
  // Two-sided cells (SQL, a number you trust, an edit) keep both of their values.
  assert.match(detailOf(editMarks()[4], 'review', editMarks()), /<td[^>]*>10<\/td><td[^>]*>20<\/td>/);
});

test('D1 Kane\'s bands: the Description score reads Matches, Could be improved or Does not match (spec 4.4, Kane 27 Oct 05:49)', () => {
  assert.equal(m.descriptionScore(match(94, 'matches', 'Matches')).label, 'Matches 94%');
  assert.equal(m.descriptionScore(match(64, 'could be improved', 'Could be improved')).label, 'Could be improved, 64%');
  assert.equal(m.descriptionScore(match(41, 'does not match', 'Does not match')).label, 'Does not match, 41%');
  assert.equal(m.descriptionScore(match(94, 'matches', 'Matches')).words, 'Matches, 94%', 'the full Description reads the same words');
  assert.match(m.SCORE_TITLE, /Description matches the formula/, 'gap 5: the hover says what the score compares');
});

test('D1 gap 6: after an edit, the Equivalence detail shows the edit', () => {
  const edited = { ...editMarks()[4], beforeFormula: 'SUM ( Sales[Quantity] )', afterFormula: 'SUM ( Sales[Quantity] ) * 2' };
  const detail = detailOf(edited, 'review', editMarks());
  assert.match(detail, /data-testid="mk-edit-diff"/);
  assert.match(detail, /SUM \( Sales\[Quantity\] \)<\/code>/);
  assert.match(detail, /SUM \( Sales\[Quantity\] \) \* 2<\/code>/);
  assert.ok(!detailOf(editMarks()[4], 'review').includes('mk-edit-diff'), 'an older engine without the formulas shows no empty box');
  // At rest the card does not grow: the edit is behind the click.
  assert.ok(!cardAtRest(set(editMarks(), 'Equivalence', edited), IMPROVE).includes('mk-edit-diff'));
});

test('D1 gap 3: the running line and the finished line (the run plan)', () => {
  const running = html(React.createElement(plan.PlanLine, { sentence: 'Checking 12 of 12: 4 core. About 2 minutes.', running: true, done: 5, total: 12, canRun: true, onChoose() {}, onRun() {} }));
  assert.match(running, /role="status"[^>]*>Checking 5 of 12\.</);
  assert.ok(words(running) <= 15);
  assert.equal(m.cardVerdict(sumMarks(), GOOD, { running: true }).line, 'Checking now', 'a card shows Checking now while its measure runs');
});

test('fx24: the Checks header is 12 words or fewer in its worst state, and leads with what needs action (R1, R2, R8)', () => {
  const line = (verdict, problems) => html(React.createElement(page.ChecksHeader, { verdict, problems, actionable: null }));
  const worst = line('3 broken · 2 need review · 4 unknown · 1 verified · 2 not checked yet', '2 numbers differ · 1 key problem · 3 table counts differ');
  assert.ok(words(worst) <= 12, `the header is ${words(worst)} words: ${worst}`);
  assert.match(worst, /3 broken · 2 need review · 4 unknown/, 'the states that need action stay, Unknown included');
  assert.match(worst, /\(\+3 more\)/, 'the rest fold, as the card verdict line does');
  // fx29 gap 3 (spec 4.2, R8): a verdict shows its coverage, so Verified stays when it fits; over the budget it folds first.
  assert.match(line('1 needs review · 1 unknown · 10 verified', ''), />1 needs review · 1 unknown · 10 verified</);
  const broken = line('1 broken · 1 needs review · 2 unknown · 8 verified', '1 number differs');
  assert.ok(words(broken) <= 12, `the header is ${words(broken)} words: ${broken}`);
  assert.match(broken, /1 broken · 1 needs review · 2 unknown/);
  assert.match(broken, /1 number differs/, 'over the budget, the calm counts leave first and the problem stays');
  assert.doesNotMatch(broken, /verified|more/);
  assert.match(line('8 verified · 4 not checked yet', ''), /8 verified · 4 not checked yet/, 'with nothing to act on, the coverage stays');
});

test('fx24: while a measure is checked again, a mark not reached yet keeps its last known state with a checking cue', () => {
  // ra1 section 2: the engine keeps the last result in the record with pending set; the card renders it with the checking cue.
  const pending = { name: 'Trusted number', state: 'suspect', sentence: 'The measure is 5 lower than your number.', lastRan: ran, applies: true, verdict: 'broken', pending: true };
  const view = panel.cardState({ measureRef: 'measure:Sales/Total Cost', version: 9, running: true, lastChecked: ran, marks: [pending] },
    { measureRef: 'measure:Sales/Total Cost', version: 8, revision: 3, running: true, marks: [{ name: 'Trusted number', state: 'not run', sentence: 'This check is waiting to run.', reasonCode: 'waiting', applies: true, pending: true, verdict: null }] });
  assert.equal(view.running, true);
  assert.equal(m.verdictOf(view.marks[0]), 'broken', 'the newer record wins over the older answer');
  const older = panel.cardState({ measureRef: 'r', version: 2, marks: [pending] }, { measureRef: 'r', version: 5, revision: 1, running: false, marks: [{ ...pending, pending: false }] });
  assert.equal(older.marks[0].pending, false, 'a newer answer wins over an older record');
  const face = html(React.createElement(panel.CardFace, { verdict: m.cardVerdict(view.marks, null, { running: true }), description: null,
    marks: [{ name: 'Trusted number', state: 'broken', checking: true }], open: null, onMark() {}, onVerdict() {}, checked: '', running: true, onCheck() {} }));
  assert.match(face, /mk-checking/);
  assert.match(face, /title="Checking"/);
});


test('fx27: a sentence that prints a mark number reads in the measure format, as its table cell does (v-rc28 polish)', () => {
  const trusted = (before, after, beforeText, afterText, state) => ({ name: 'Trusted number', state, applies: true, verdict: state === 'passed' ? 'verified' : 'broken',
    sentence: 'The measure matches the saved numbers you trust.', lastRan: ran, cells: [{ context: 'Grand total', before, after, beforeText, afterText }] });
  assert.equal(m.detailSentence(trusted('628370', '628370', '$628,370.00', '$628,370.00', 'passed'), 'verified'), 'Same as your number: $628,370.00.');
  assert.match(m.detailSentence(trusted('628370', '628365', '$628,370.00', '$628,365.00', 'suspect'), 'broken'), /^\$5\.00 lower than your number\./);
  const sql = { name: 'SQL', state: 'suspect', applies: true, verdict: 'broken', sentence: 'Differs.', lastRan: ran,
    cells: [{ context: 'Grand total', before: '0.12', after: '0.17', beforeText: '12.0%', afterText: '17.0%' }] };
  assert.match(m.detailSentence(sql, 'broken'), /^Differs from SQL by 5\.0%\./);
  // Texts that round to the same words cannot show the gap: the raw gap says it.
  assert.match(m.detailSentence(trusted('100', '95', '100', '95', 'suspect'), 'broken'), /^5 lower than your number\./);
  assert.match(m.detailSentence(trusted('1000.4', '1000.2', '1,000', '1,000', 'suspect'), 'broken'), /^0\.2 lower than your number\./);
});

test('fx28: a gap reads in the measure format when the signs differ, and never reads 0 (v-fx27 polish a)', () => {
  const trusted = (cell, state = 'suspect') => ({ name: 'Trusted number', state, applies: true, verdict: 'broken',
    sentence: 'The measure differs from a number you trust.', lastRan: ran, cells: [{ context: 'Grand total', ...cell }] });
  // The engine writes the gap in the measure's format (MarkNumberFormat.Gap): the card prints it.
  assert.match(m.detailSentence(trusted({ before: '1000', after: '-5', beforeText: '$1,000.00', afterText: '-$5.00', gapText: '$1,005.00' }), 'broken'),
    /^\$1,005\.00 lower than your number\./);
  assert.match(m.detailSentence(trusted({ before: '628370', after: '628370.001', beforeText: '$628,370.00', afterText: '$628,370.00', gapText: '$0.001' }), 'broken'),
    /^\$0\.001 higher than your number\./);
  // A cell with no gap text (an older record on a door that does not fill it) still reads the format, with either sign.
  assert.match(m.detailSentence(trusted({ before: '1000', after: '-5', beforeText: '$1,000.00', afterText: '-$5.00' }), 'broken'), /^\$1,005\.00 lower than your number\./);
  assert.match(m.detailSentence(trusted({ before: '1000', after: '-5', beforeText: '$1,000.00', afterText: '($5.00)' }), 'broken'), /^\$1,005\.00 lower than your number\./);
  assert.match(m.detailSentence(trusted({ before: '-5', after: '1000', beforeText: '$-5.00', afterText: '$1,000.00' }), 'broken'), /^\$1,005\.00 higher than your number\./);
  // A gap under the format's rounding shows enough places to be seen.
  assert.match(m.detailSentence(trusted({ before: '628370', after: '628370.001', beforeText: '$628,370.00', afterText: '$628,370.00' }), 'broken'), /^0\.001 higher than your number\./);
  assert.match(m.detailSentence(trusted({ before: '5', after: '5.004' }), 'broken'), /^0\.004 higher than your number\./);
  const sql = { name: 'SQL', state: 'suspect', applies: true, verdict: 'broken', sentence: 'Differs.', lastRan: ran,
    cells: [{ context: 'Grand total', before: '1000', after: '-5', beforeText: '$1,000.00', afterText: '-$5.00', gapText: '$1,005.00' }] };
  assert.match(m.detailSentence(sql, 'broken'), /^Differs from SQL by \$1,005\.00\./);
  for (const mark of [trusted({ before: '628370', after: '628370.001' }), trusted({ before: '1', after: '1.0000001', beforeText: '1', afterText: '1' })])
    assert.doesNotMatch(m.detailSentence(mark, 'broken'), /^0 (higher|lower)/);
});
