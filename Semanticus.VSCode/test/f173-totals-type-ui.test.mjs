// f173 (Kane, 1 Nov): "The requested behaviour for totals was to have Jev choose then show the type used". A Totals that Jev
// typed but did not judge (a distinct count, a type Jev is not sure of) is shown on the card, the Checks row's card and the
// tree hover, with the type and one short reason, in a calm look. It never changes the measure's verdict or the Checks counts.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, makeHookRuntime, req } from './helpers/webview-load.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const ran = '2026-10-30T08:00:00Z';
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];

// As the rc3 engine sends them: applies, no verdict, the type Jev chose, and the engine's sentence.
const distinct = { name: 'Totals', state: 'info', applies: true, verdict: null, reasonCode: 'totals_distinct', lastRan: ran,
  sentence: 'Checked as a distinct count. Distinct counts do not add up, so the total is shown, not judged.',
  total: { rule: 'distinct_count', type: 'distinct count', confidence: .99, expected: false, source: '' },
  cells: [{ context: 'Bikes', after: '10' }, { context: 'Grand total', after: '45' }] };
const unclear = { name: 'Totals', state: 'info', applies: true, verdict: null, reasonCode: 'totals_type_unclear', lastRan: ran,
  sentence: 'Checked as a recalculation. Jev is not sure of the type, so the total is shown, not judged.',
  total: { rule: 'recomputed_at_total', type: 'recalc (recomputed at the total)', confidence: .4, expected: false, source: '' } };
const filters = { name: 'Filters', state: 'passed', applies: true, verdict: 'verified', lastRan: ran, sentence: 'No ignored filter in 4 places.', cells: [] };

test('f173: a Totals Jev typed but did not judge is shown with its type, in the calm info look', () => {
  for (const totals of [distinct, unclear]) {
    const shown = m.cardMarks([totals, filters]);
    assert.deepEqual(shown.map(x => [x.mark.name, x.state]), [['Totals', 'info'], ['Filters', 'verified']], totals.reasonCode);
    assert.ok(m.MARK_GLYPH.info && !Object.values(m.GLYPH).includes(m.MARK_GLYPH.info), 'info has its own glyph, not a verdict glyph');
  }
  // Rc2's hidden Totals is gone, but a Totals with no result stays hidden.
  assert.deepEqual(m.cardMarks([{ name: 'Totals', state: 'not run', applies: false, verdict: null, reasonCode: 'totals_off', sentence: 'Not scored yet.' }]), []);
  assert.deepEqual(m.cardMarks([{ name: 'Totals', state: 'not run', applies: false, verdict: null, reasonCode: 'not_scored', sentence: 'Not scored yet.' }]), []);
});

test('f173: the detail names the type once, with one short reason', () => {
  assert.equal(m.detailSentence(distinct, 'info'), 'Checked as a distinct count. Distinct counts do not add up, so the total is shown, not judged.');
  assert.equal(m.detailSentence(unclear, 'info'), 'Checked as a recalculation. Jev is not sure of the type, so the total is shown, not judged.');
  // An older engine wrote its own type words first: the card still says the type once.
  assert.equal(m.detailSentence({ ...unclear, sentence: 'Checked as a recalc (recomputed at the total). Jev is not sure of the type, so the total is shown, not judged.' }, 'info'),
    'Checked as a recalculation. Jev is not sure of the type, so the total is shown, not judged.');
  for (const t of [distinct, unclear]) {
    const said = m.detailSentence(t, 'info');
    assert.equal(said.match(/Checked as/g).length, 1, said);
    assert.doesNotMatch(said, /—|recomputed_at_total|distinct_count|\(/);
  }
});

test('f173: the measure verdict and the Checks counts do not move', () => {
  for (const totals of [distinct, unclear]) {
    assert.equal(m.cardVerdict([totals, filters]).line, m.cardVerdict([filters]).line, 'Verified by 1 check, with or without the shown Totals');
    assert.equal(m.rowVerdict([totals, filters]), 'verified');
    assert.equal(m.rowVerdict([totals], null, true), m.rowVerdict([], null, true), 'a Totals that is only shown is no verdict');
    const rows = [{ ref: 'measure:Sales/A', name: 'A', marks: [totals, filters], checked: true }, { ref: 'measure:Sales/B', name: 'B', marks: [totals], checked: true }];
    const plain = rows.map(r => ({ ...r, marks: r.marks.filter(x => x.name !== 'Totals') }));
    assert.equal(m.checksVerdictLine(m.groupByState(rows), 2), m.checksVerdictLine(m.groupByState(plain), 2));
  }
});

test('f173: the tree hover lists the shown Totals beside the marks with a verdict', () => {
  const row = m.treeRowItem('Contoso customers', [distinct, filters], { checked: true });
  assert.equal(row.state, 'verified');
  assert.match(row.tooltip, new RegExp(`${m.MARK_GLYPH.info} Totals`));
  assert.match(row.tooltip, /✓ Filters/);
});

test('rc723 finding 1: the tree hover names the type Jev chose on Totals, judged or only shown', () => {
  const sum = { name: 'Totals', state: 'passed', applies: true, verdict: 'verified', lastRan: ran, sentence: 'The total fits its rows.',
    total: { rule: 'sum', type: 'sum', confidence: .95, expected: true, source: '' }, cells: [] };
  for (const [totals, type] of [[distinct, 'distinct count'], [unclear, 'recalculation'], [sum, 'sum']]) {
    const row = m.treeRowItem('Contoso customers', [totals, filters], { checked: true });
    const line = row.tooltip.split('\n')[1];
    assert.match(line, new RegExp(`Totals: ${type}(\\s|$)`), `${totals.reasonCode ?? 'judged sum'}: ${line}`);
    assert.equal(line.split(type).length, 2, 'the type is said once');
    assert.match(line, /✓ Filters/);
  }
});

test('rc724 finding 1: a Totals that needs review names its type once in the hover', () => {
  for (const [rule, type, raw] of [['sum', 'sum', 'sum'], ['recomputed_at_total', 'recalculation', 'recalc (recomputed at the total)']]) {
    const review = { name: 'Totals', state: 'failed', applies: true, verdict: 'review', reasonCode: 'totals_mismatch', lastRan: ran,
      sentence: 'The total does not fit its rows.', total: { rule, type: raw, confidence: .95, expected: false, source: '' }, cells: [] };
    const row = m.treeRowItem('Contoso quantity', [review, filters], { checked: true });
    assert.equal(row.state, 'review');
    assert.equal(row.tooltip.split(type).length, 2, `${type} is said once: ${row.tooltip}`);
    assert.match(row.tooltip.split('\n')[1], /Totals {3}✓ Filters/);
  }
});

test('f173: the card draws the shown Totals and opens its detail', async () => {
  const runtime = makeHookRuntime();
  const reference = 'measure:Sales/Contoso customers';
  const state = { measureRef: reference, version: 3, running: false, lastChecked: ran, marks: [distinct, filters] };
  const host = { rpc: async method => method === 'getMeasureMarks' ? state : method === 'getMeasureCheckCache' ? [state] : null,
    onChange: () => () => {}, onCheckState: () => () => {} };
  const { MeasureMarksPanel, CardFace, MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference }));
  const face = () => elements(view.result).find(e => e.type === CardFace).props;
  try {
    for (let i = 0; i < 20 && !face().marks.length; i++) await new Promise(r => setTimeout(r, 5));
    assert.deepEqual(face().marks.map(x => [x.name, x.state]), [['Totals', 'info'], ['Filters', 'verified']]);
    assert.match(face().verdict.line, /^✓ Verified by 1 check/);
    face().onMark('Totals');
    const detail = face().detail;
    assert.ok(detail, 'Totals opens');
    const html = renderToStaticMarkup(React.createElement(MarkCard, detail.props));
    assert.match(html, /mk-card-info/);
    assert.match(html, /Checked as a distinct count\. Distinct counts do not add up/);
  } finally { runtime.unmount(); }
  const faceHtml = renderToStaticMarkup(React.createElement(load('./marks-panel.tsx').CardFace, {
    verdict: m.cardVerdict([distinct, filters]), description: null, marks: [{ name: 'Totals', state: 'info' }, { name: 'Filters', state: 'verified' }],
    open: null, onMark() {}, onVerdict() {}, checked: '', onCheck() {} }));
  assert.match(faceHtml, /mk-s-info/);
  assert.match(faceHtml, new RegExp(m.MARK_GLYPH.info));
  const css = readFileSync(new URL('../webview/src/marks.css', import.meta.url), 'utf8');
  assert.match(css, /\.mk-s-info \.mk-glyph[^}]*var\(--sem-muted\)/, 'the info look is muted: not a warning, not a pass');
});
