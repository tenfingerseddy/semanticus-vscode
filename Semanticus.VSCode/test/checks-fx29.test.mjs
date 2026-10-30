import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

// fx29: the rc28 release tester's gaps 1, 2, 3 and 8 on the Checks page, the card and the tree row.
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, onCheckState: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const panel = load('./marks-panel.tsx');
const page = load('./checks-page.tsx');
const hostModule = load('./marks-host.ts');
const host = { rpc: async () => null, onChange: () => () => {}, open() {}, undo() {} };
const html = node => renderToStaticMarkup(React.createElement(hostModule.MarksHostContext.Provider, { value: host }, node));
const ran = new Date().toISOString();
const ADVICE = 'Not scored yet. Pro advice is off for this model.';

test('gap 1: Pro advice off reads one plain line with Turn on, on the Checks page and the card', () => {
  assert.equal(m.actionableReason(ADVICE), 'Pro advice is off for this model.');
  assert.equal(m.actionableReason('Pro advice is off for this model.'), 'Pro advice is off for this model.', 'the Description score reason');
  assert.equal(m.actionableReason('Not scored yet. This score turns on when Jev scoring is released.'), null, 'a service fault shows nothing');
  const header = html(React.createElement(page.ChecksHeader, { verdict: '12 verified', actionable: m.actionableReason(ADVICE), onTurnOn() {} }));
  assert.match(header, /Pro advice is off for this model\./);
  assert.match(header, />Turn on</, 'the Checks page offers Turn on beside the line');
  const verdict = m.cardVerdict([{ name: 'Filters', state: 'passed', sentence: 'No ignored filter in 5 places checked.', lastRan: ran }]);
  const face = html(React.createElement(panel.CardFace, { verdict, actionable: m.actionableReason(ADVICE), onTurnOn() {}, marks: [], open: null, checked: '',
    onMark() {}, onVerdict() {}, onCheck() {}, description: null }));
  assert.match(face, /Pro advice is off for this model\./);
  assert.match(face, />Turn on</, 'the card offers Turn on beside the line');
  // The other actionable lines are not fixed by Turn on, so they carry no button.
  const pro = html(React.createElement(page.ChecksHeader, { verdict: '12 verified', actionable: 'Scoring needs a Pro plan.', onTurnOn() {} }));
  assert.doesNotMatch(pro, />Turn on</);
  // Never on the row or the hover (spec 4.1).
  const marks = [{ name: 'Totals', state: 'not run', sentence: ADVICE, reasonCode: 'totals_off', applies: false, verdict: null },
    { name: 'Filters', state: 'passed', sentence: 'No ignored filter in 5 places checked.', lastRan: ran, applies: true, verdict: 'verified' }];
  const row = m.treeRowItem('Total Quantity', marks, { checked: true });
  assert.ok(!/advice/i.test(row.tooltip + row.description + row.label));
});

// The engine's cheap pass writes Parse, Lint, Orphan keys and Description match (each with a time) for every measure before
// the run reaches it. None of them is one of the seven marks, so a measure that is only waiting is not checked yet.
const cheap = [
  { name: 'Parse', state: 'passed', sentence: 'The formula parses and its references resolve.', lastRan: ran },
  { name: 'Lint', state: 'passed', sentence: 'No lint findings.', lastRan: ran },
  { name: 'Orphan keys', state: 'not run', sentence: 'No relationship counts apply to this measure.' },
  { name: 'Description match', state: 'not run', sentence: 'Not scored yet. This score turns on when Jev scoring is released.', reasonCode: 'not_scored' },
];

test('gap 2: a measure only waiting in a run keeps "not checked yet" and no glyph, never Unknown', () => {
  const marks = m.stateMarks({ measureRef: 'measure:Sales/# Orders', version: 4, marks: [], cheap });
  const verdict = m.cardVerdict(marks, null, { checked: false });
  assert.equal(verdict.state, null, 'waiting is not a verdict: ' + verdict.line);
  assert.match(verdict.line, /^Not checked yet/);
  const row = m.treeRowItem('# Orders', marks, { checked: false });
  assert.equal(row.description, '', 'no glyph on the tree row');
  assert.doesNotMatch(row.label, /Unknown/);
  const groups = m.groupByState([{ ref: 'a', name: '# Orders', marks, checked: false }, { ref: 'b', name: 'Total Cost', checked: true,
    marks: [{ name: 'Filters', state: 'passed', sentence: 'No ignored filter in 5 places checked.', lastRan: ran }] }]);
  assert.equal(m.checksVerdictLine(groups, 2), '1 verified · 1 not checked yet');
  // A seven mark the run is still producing is waiting too (no result, no time).
  const waiting = [{ name: 'Filters', state: 'not run', sentence: 'This check is waiting to run.', reasonCode: 'waiting', applies: true, pending: true, verdict: null }];
  assert.equal(m.cardVerdict(waiting, null, { checked: false }).state, null);
  // A measure that was checked and has no mark that applies is still Unknown, with its reason (d1 tester gap 2).
  assert.equal(m.cardVerdict([{ name: 'Filters', state: 'not run', sentence: 'This formula moves the date window, so the row count test does not apply.', reasonCode: 'filters_no_rows', applies: false }], null, { checked: true }).state, 'unknown');
});

test('gap 3: the Checks header lists every non-zero part, verified included (spec 4.2)', () => {
  const header = verdict => html(React.createElement(page.ChecksHeader, { verdict, problems: '', actionable: null }));
  assert.match(header('1 needs review · 1 unknown · 10 verified'), />1 needs review · 1 unknown · 10 verified</);
  assert.match(header('1 broken · 2 need review · 9 verified'), />1 broken · 2 need review · 9 verified</);
  const parts = m.checksHeaderParts('1 needs review · 1 unknown · 10 verified', '1 number differs');
  assert.equal([parts.verdict, parts.problems].join(' · '), '1 needs review · 1 unknown · 10 verified · 1 number differs');
});

test('gap 8: a date the engine wrote in the measure\'s own format stays in that format on the card', () => {
  const filters = { name: 'Filters', state: 'info', sentence: 'This formula ignores Date filters. The grand total is 2016-09-12. If that is not meant, check the ALLEXCEPT.',
    lastRan: ran, reasonCode: 'filters_lost', applies: true, verdict: 'review',
    cells: [{ context: 'Grand total', after: '2016-09-12', afterText: '2016-09-12' }, { context: 'Year: 2017', after: '2016-09-28', afterText: '2016-09-28' }] };
  const card = html(React.createElement(panel.MarkCard, { reference: 'measure:Sales/Date New Customer', mark: filters, state: 'review', others: [filters], onRecheck() {} }));
  assert.match(card, /The grand total is 2016-09-12\./);
  assert.match(card, /<td[^>]*>2016-09-28<\/td>/);
  assert.doesNotMatch(card, /Sep 2016/);
  // A raw engine timestamp with no display text still reads as a date in words.
  const raw = { ...filters, sentence: 'Ignores Date filters.', cells: [{ context: 'Grand total', after: '2016-09-12T00:00:00' }] };
  assert.match(html(React.createElement(panel.MarkCard, { reference: 'r', mark: raw, state: 'review', others: [raw], onRecheck() {} })), /12 Sep 2016/);
});
