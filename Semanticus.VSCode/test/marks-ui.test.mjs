import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

// The marks screens (Properties, the mark cards, the run plan and the Checks page) read their words from one model.
const load = makeLoader();
const m = load('./marks-model.ts');
const mark = (name, state, sentence, extra = {}) => ({ name, state, sentence, cells: [], action: '', chosenContexts: [], timings: [], ...extra });
const engineMarks = [
  mark('Filters', 'suspect', 'The same number appears for different fact members, so the grouped filter may be ignored.'),
  mark('Contexts', 'passed', 'The sampled contexts have values without errors.'),
  mark('SQL', 'not run', 'No SQL check is saved for this measure.'),
  mark('Trusted number', 'not run', 'No trusted number is saved for this measure.'),
  mark('Equivalence', 'not run', 'No edit yet.'),
  mark('Totals', 'not run', 'Semanticus has not measured this judge yet'),
  mark('Alignment', 'not run', 'Semanticus has not measured this judge yet'),
  mark('Performance', 'not run', 'not timed'),
];

// FX17 and FX19 at the rc28 join: the engine formats every cell once (MarkNumberFormat, MarkNumberFormatTests); the card prints
// that text as it is and never formats a number of its own.
test('FX17: Equivalence prints the engine text of each side, in its own format', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const html = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Contoso share', others: [], onClose() {}, onRecheck() {},
    mark: mark('Equivalence', 'suspect', 'This change moved these numbers.', {
      beforeFormatString: '#,0', afterFormatString: '0.####%',
      cells: [{ context: 'City: Contoso', before: '628370', after: '9.707656317137992E-05', beforeText: '628,370', afterText: '0.0097%' }],
    }),
  }));
  assert.match(html, /<td[^>]*>628,370<\/td>/);
  assert.match(html, /<td[^>]*>0\.0097%<\/td>/);
  assert.doesNotMatch(html, /E-05|628370/);
});

test('FX19: the card never formats a number itself', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  for (const [format, before, after, shownBefore, shownAfter] of [
    ['#,0', '1000.1', '1000.2', '1,000.1', '1,000.2'],
    ['$#,0', '1000.1', '1000.2', '$1,000.1', '$1,000.2'],
    ['#,0;(#,0)', '-1000.1', '-1000.2', '(1,000.1)', '(1,000.2)'],
    [null, '909.09090909', '1234567890.123456', '909.09090909', '1,234,567,890.12346'],
  ]) {
    const html = renderToStaticMarkup(React.createElement(MarkCard, {
      reference: 'measure:Sales/Amount', others: [], onClose() {}, onRecheck() {},
      mark: mark('Equivalence', 'suspect', 'This change moved these numbers.', {
        beforeFormatString: format, afterFormatString: format, cells: [{ before, after, beforeText: shownBefore, afterText: shownAfter }],
      }),
    }));
    assert.ok(html.includes(`>${shownBefore}</td>`), `${format}: ${html}`);
    assert.ok(html.includes(`>${shownAfter}</td>`), `${format}: ${html}`);
  }
  // A one-sided mark prints the engine text of its value too.
  const filters = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Amount', others: [], onClose() {}, onRecheck() {},
    mark: mark('Filters', 'passed', 'The filters work.', { cells: [{ context: 'Grand total', after: '628370', afterText: '$628,370.00' }] }),
  }));
  assert.match(filters, /<td[^>]*>\$628,370\.00<\/td>/);
  assert.ok(!filters.includes('628370<'), filters);
});

test('FX17: information marks use an icon on the card and the native tree row', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const info = mark('Filters', 'info', 'This formula ignores Product filters.');
  const html = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Contoso quantity', others: [], onClose() {}, onRecheck() {}, mark: info,
  }));
  assert.match(html, /mk-card-review/, 'the card carries the Needs review state');
  assert.doesNotMatch(html, /ⓘ/);
  const row = m.treeRowItem('Contoso quantity', [info]);
  assert.equal(row.description, '▲');
  assert.doesNotMatch(row.description, /Checked/);
  assert.doesNotMatch(row.tooltip + row.description, /ⓘ/);
});

test('FX17: timing progress stays visible and does not offer a second timing run', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { AddCheck } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  // The engine sends a timing in progress as a pending mark with its progress line (reason code timing).
  const timing = mark('Performance', 'not run', 'Timing this measure: 3 of 7 runs done. About 8 seconds remaining.',
    { reasonCode: 'timing', applies: true, pending: true, verdict: null, progress: 'Timing this measure: 3 of 7 runs done. About 8 seconds remaining.' });
  assert.equal(m.verdictOf(timing, true), null, 'a timing in progress is no verdict yet');
  const panel = readFileSync(new URL('../webview/src/marks-panel.tsx', import.meta.url), 'utf8');
  // fx29 (rc28 tester gap 5): it stays visible in the spec's words, "Timing. About <t>." (timingLine), not the run counts.
  assert.match(panel, /role="status" data-testid="mk-timing">\{timingLine\(timing\)\}/, 'the progress line stays visible');
  assert.match(panel, /m\.name === 'Performance' && m\.progress/, 'it is the engine\'s progress line, not a sentence pattern');
  const html = renderToStaticMarkup(React.createElement(AddCheck, { reference: 'measure:Sales/Contoso quantity', marks: [timing], onRecheck() {} }));
  assert.doesNotMatch(html, /Time this measure/, 'no second timing run is offered');
});

test('FX12: every mark card shows dates in words and reserves Before and After for edits', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  for (const name of ['Totals', 'Filters', 'Contexts', 'SQL', 'Trusted number', 'Equivalence', 'Performance']) {
    const html = renderToStaticMarkup(React.createElement(MarkCard, {
      reference: 'measure:Sales/Contoso date', others: [], onClose() {}, onRecheck() {},
      mark: mark(name, 'passed', 'Contoso date.', { cells: [{ context: 'Date: 2016-09-28T00:00:00.0000000',
        before: '2016-09-28T00:00:00.0000000', after: '2017-09-28T00:00:00.0000000' }] }),
    }));
    assert.match(html, /28 Sep 2016/, name);
    assert.match(html, /28 Sep 2017/, name);
    assert.doesNotMatch(html, /T00:00:00/, name);
    if (name === 'Equivalence') {
      assert.match(html, /<th>Before<\/th>/);
      assert.match(html, /<th>After<\/th>/);
    } else assert.doesNotMatch(html, /<th>(Before|After)<\/th>/, name);
  }
});

test('FX12: a mark with one value has no edit headings', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const html = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Contoso date', others: [], onClose() {}, onRecheck() {},
    mark: mark('Filters', 'passed', 'Contoso date.', { cells: [{ after: '2016-09-28T00:00:00.0000000' }] }),
  }));
  assert.match(html, /<th>Value<\/th>/);
  assert.doesNotMatch(html, /<th>(Before|After)<\/th>/);
  assert.match(html, /28 Sep 2016/);
});

test('FX12: filter row counts name the source rows and the rows used', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const html = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Contoso amount', others: [], onClose() {}, onRecheck() {},
    mark: mark('Filters', 'suspect', 'Contoso rows.', { cells: [{ before: '1', after: '3' }] }),
  }));
  assert.match(html, /<th>Source rows<\/th>/);
  assert.match(html, /<th>Rows used<\/th>/);
  assert.doesNotMatch(html, /<th>(Before|After)<\/th>/);
});

// d1 (fx23) section 3: this old sentence means Filters does not apply, so it is hidden; the next step is Add a check.
test('FX12: an old Filters result also gives a reason and a next step', () => {
  const old = mark('Filters', 'not run', 'The filter check could not compare the source rows.');
  assert.equal(m.applies(old), false);
  assert.equal(m.verdictOf(old, true), null);
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { AddCheck } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const html = renderToStaticMarkup(React.createElement(AddCheck, { reference: 'measure:Sales/Contoso amount', marks: [old], onRecheck() {} }));
  assert.match(html, /Add a check/);
});

test('FX12: a timed date context also reads as a date', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./marks-panel.tsx');
  const html = renderToStaticMarkup(React.createElement(MarkCard, {
    reference: 'measure:Sales/Contoso amount', others: [], onClose() {}, onRecheck() {},
    mark: mark('Performance', 'passed', 'Contoso time.', { timings: [{ context: 'Date: 2016-09-28T00:00:00.0000000',
      medianMs: 10, minMs: 9, maxMs: 11, coldMs: 20 }] }),
  }));
  assert.match(html, /28 Sep 2016/);
  assert.doesNotMatch(html, /T00:00:00/);
});

// d1 (fx23): scoring that is not served is hidden, not "Not scored yet" (spec 4.4, section 5 cuts 1 to 3).
test('unavailable scoring uses the same plain words on cards, the Checks page and the tree row', () => {
  for (const reason of ['Not scored yet. The scoring service does not have this check yet.',
    'Not scored yet. The scoring service could not be reached.']) {
    const totals = mark('Totals', 'not run', reason);
    assert.equal(m.verdictOf(totals, true), null, 'Totals is hidden');
    const row = m.treeRowItem('Total Quantity', [totals], {
      oneLine: 'Adds Contoso quantities.', match: { score: null, level: 'not scored yet', reason: reason.split('. ').slice(1).join('. ') },
    });
    assert.equal(row.description, '');
    assert.doesNotMatch(row.tooltip, /Not scored yet|Description:|judge|Check this measure again|engine|relay/);
    assert.equal(row.state, null);
    assert.equal(m.descriptionScore({ score: null, level: 'not scored yet', reason }).band, null);
  }
});

test('old cached scoring reasons cannot show internal words or ask for a repeat check', () => {
  for (const reason of ['Semanticus has not measured this judge yet',
    'The total type is unavailable. Check this measure again.', 'Unknown question id: description.match']) {
    assert.equal(m.applies(mark('Totals', 'not run', reason)), false, reason);
    const score = m.descriptionScore({ score: null, level: 'not scored yet', reason });
    assert.equal(score.label, '');
    const line = m.cardVerdict([mark('Totals', 'not run', reason)], { score: null, level: 'not scored yet', reason }).line;
    assert.doesNotMatch(line + score.label, /judge|Check this measure again|question id|description.match/);
  }
});

test('Totals information names the checked type without counting as a pass or a warning', () => {
  const distinct = mark('Totals', 'info', 'Checked as a distinct count. Totals for this type are shown, not judged', {
    total: { rule: 'distinct_count', type: 'distinct count', confidence: .92, expected: false },
  });
  assert.equal(m.verdictOf(distinct, true), null, 'a distinct count is not judged, so its mark is hidden (27 Oct cut)');
  assert.equal(m.rowVerdict([distinct]), null);
  const sum = mark('Totals', 'passed', 'Total adds up as a sum, as expected', { total: { rule: 'sum_of_rows', type: 'sum', confidence: .92, expected: false } });
  assert.match(m.detailSentence(sum, 'verified'), /^Checked as a sum\./, 'the detail names the checked type');
});

test('a measured Totals warning stays suspect and low confidence stays information', () => {
  assert.equal(m.verdictOf(mark('Totals', 'suspect', 'The total does not fit a sum: rows 10, 20; total 40')), 'review');
  assert.equal(m.verdictOf(mark('Totals', 'info', 'Type unclear')), null, 'low confidence is no verdict');
});

test('the seven marks come in a fixed order and the alignment judge is the Description score, not a mark', () => {
  assert.deepEqual(m.orderMarks(engineMarks).map(x => x.name), ['Totals', 'Filters', 'SQL', 'Trusted number', 'Equivalence', 'Contexts', 'Performance']);
  assert.deepEqual(m.shownMarks([]), [], 'nothing has run: no mark is shown, none reads Not run');
});

test('each mark is passed, suspect or not run, and never by colour alone', () => {
  const shown = m.shownMarks(engineMarks);
  assert.deepEqual(shown.map(x => [x.mark.name, x.state]), [['Filters', 'review'], ['Contexts', 'verified']], 'marks that do not apply are hidden');
  for (const { state } of shown) { assert.ok(m.GLYPH[state].length > 0); assert.ok(m.WORD[state].length > 0); }
  assert.equal(new Set(Object.values(m.GLYPH)).size, 5, 'each state has its own glyph');
  assert.equal(m.cardVerdict(engineMarks).line, '▲ Needs review: a filter does not change it');
});

test('old Totals cache and an unavailable Description score both stay unscored', () => {
  assert.equal(m.applies(m.orderMarks(engineMarks)[0]), false);
  const score = m.descriptionScore({ score: null, level: 'not scored yet', label: 'not scored yet' });
  assert.equal(score.label, '');
  assert.equal(score.review, false);
});

test('Description score shows Kane\'s words at all four band boundaries', () => {
  // Kane's bands, 27 Oct 05:49 (d1 spec 4.4): "Matches 94%", "Could be improved, 64%", "Does not match, 41%".
  for (const [score, level, label] of [[70, 'matches', 'Matches'], [69, 'could be improved', 'Could be improved,'],
    [50, 'could be improved', 'Could be improved,'], [49, 'does not match', 'Does not match,']]) {
    const view = m.descriptionScore({ score, level, label: label.replace(',', '') });
    assert.equal(view.label, `${label} ${score}%`);
    assert.equal(view.review, score < 50);
  }
});

test('a person\'s own edit that moves numbers reads moved, not wrong; nothing moved reads nothing moved', () => {
  const moved = mark('Equivalence', 'suspect', 'This change moved these numbers.', { cells: [
    { context: 'Year: 2025', before: '31,560,400', after: '16,204,770' }, { context: 'Mar 2025', before: '9,412,880', after: '4,118,020' }] });
  const line = m.cardVerdict([moved]).line;
  assert.equal(line, '▲ Needs review: your edit moved 2 numbers');
  assert.doesNotMatch(line, /wrong|suspect|Broken/i);
  assert.equal(m.cardVerdict([mark('Equivalence', 'passed', 'Nothing moved on the sampled grid.')]).line, '✓ Verified by 1 check. Nothing moved.');
  assert.equal(m.verdictOf(mark('Equivalence', 'not run', 'No edit yet.'), true), null);
});

test('a contradiction in the Description counts as suspect, and a row with mostly unrun checks gets no plain tick', () => {
  const wrong = m.cardVerdict(engineMarks.map(x => x.name === 'Filters' ? { ...x, state: 'passed' } : x), { score: 41, level: 'does not match', label: 'Does not match' });
  assert.equal(wrong.state, 'review');
  assert.equal(wrong.worst, 'Description');
  // d1: marks that do not apply are not "not run". Two checks that apply and passed read as Verified by 2 checks.
  const two = m.cardVerdict(engineMarks.map(x => ({ ...x, state: x.name === 'Contexts' || x.name === 'Filters' ? 'passed' : 'not run' })), { score: 92, level: 'matches', label: 'Matches' });
  assert.equal(two.line, '✓ Verified by 2 checks');
  assert.doesNotMatch(two.line, /not run/);
  assert.equal(m.rowVerdict(engineMarks, { score: 93, level: 'matches', label: 'Matches' }), 'review');
});

test('the run plan sentence and estimate change with Include performance', () => {
  const picks = [...Array(6)].map((_, i) => ({ measureRef: 'measure:Sales/C' + i, group: 'changed', reason: 'Changed since the last run, including its dependencies.' }))
    .concat([...Array(10)].map((_, i) => ({ measureRef: 'measure:Sales/K' + i, group: 'core', reason: 'A base measure with 4 dependents.' })))
    .concat([...Array(16)].map((_, i) => ({ measureRef: 'measure:Sales/R' + i, group: 'risky', reason: 'Formula uses LASTNONBLANK.' })));
  const plan = { id: 'p', totalMeasures: 500, batchSize: 40, estimatedSeconds: 110, sentence: '', picks };
  const off = m.planSummary(plan, false), on = m.planSummary(plan, true);
  assert.equal(off.sentence, 'Checking 32 of 500: 6 changed, 10 core, 16 risky. About 2 minutes.');
  assert.ok(on.seconds > off.seconds);
  assert.notEqual(on.sentence, off.sentence);
  // d1 4.3: the estimate changes in place and no note comes with it.
  assert.match(on.sentence, /^Checking 32 of 500: 6 changed, 10 core, 16 risky\. About \d+ minutes\.$/);
  assert.deepEqual(m.planGroups(plan).map(g => [g.label, g.picks.length]), [['Changed', 6], ['Core', 10], ['Risky', 16]]);
});

test('a ticked set becomes exactly that selection', () => {
  assert.deepEqual(m.selectionRequest({ kind: 'measures', measures: ['measure:Sales/Sales YTD', 'measure:Sales/Gross Margin %'] }, false),
    { selection: { kind: 'measures', measures: ['measure:Sales/Sales YTD', 'measure:Sales/Gross Margin %'] }, includePerformance: false, cap: 40 });
  assert.deepEqual(m.selectionRequest({ kind: 'folder', table: 'Sales', folder: 'Sales\\Brands' }, true).selection, { kind: 'folder', measures: [], table: 'Sales', folder: 'Sales\\Brands' });
  assert.deepEqual(m.selectionRequest({ kind: 'table', table: 'Sales' }, false).selection, { kind: 'table', measures: [], table: 'Sales' });
  assert.deepEqual(m.selectionRequest({ kind: 'changed' }, false).selection, { kind: 'changed', measures: [] });
});

test('last checked names the overnight sweep as last night', () => {
  const monday = new Date(2026, 9, 26, 9, 30);
  assert.equal(m.lastChecked(new Date(2026, 9, 26, 2, 10).toISOString(), monday), 'Last checked last night at 02:10');
  assert.equal(m.lastChecked(new Date(2026, 9, 26, 8, 5).toISOString(), monday), 'Last checked today at 08:05');
  assert.equal(m.lastChecked(new Date(2026, 9, 25, 14, 0).toISOString(), monday), 'Last checked yesterday at 14:00');
  assert.equal(m.lastChecked(null, monday), 'Not checked yet');
});

// d1 (fx23) 7.1: worst first is now a grouping by verdict, worst group first.
test('the Checks page lists measures worst first', () => {
  const rows = [
    { ref: 'a', name: 'Total Cost', marks: engineMarks.map(x => ({ ...x, state: x.name === 'Filters' ? 'passed' : x.state })) },
    { ref: 'b', name: 'Sales Tailspin Toys', marks: engineMarks },
    { ref: 'c', name: 'Gross Margin %', marks: engineMarks.map(x => ({ ...x, state: x.name === 'Filters' ? 'passed' : x.state })), match: { score: 41, level: 'does not match', label: 'Does not match' } },
    { ref: 'd', name: 'Sales YTD', marks: engineMarks.map(x => ({ ...x, state: x.name === 'Equivalence' ? 'suspect' : x.name === 'Filters' ? 'passed' : x.state })) },
  ];
  assert.deepEqual(m.groupByState(rows).flatMap(g => g.rows.map(r => r.name)), ['Gross Margin %', 'Sales Tailspin Toys', 'Sales YTD', 'Total Cost']);
});

test('no em dash and no engine words in the marks copy', () => {
  for (const file of ['marks-model.ts', 'marks-panel.tsx', 'run-plan.tsx', 'checks-page.tsx']) {
    const source = readFileSync(new URL('../webview/src/' + file, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /—/, file + ' has no em dash');
    // An operation name may appear only as the method a call names, never in words on the screen.
    const copy = source.replace(/rpc(<[^>]*>)?\('[A-Za-z]+'/g, '');
    assert.doesNotMatch(copy, /\b(getMeasureMarks|planMeasureRun|runMeasurePlan|probe_measure|reconcile_measure)\b/, file + ' shows no operation names');
  }
});

test('the Properties grid reaches only the marks doors, and the Checks page shows marks and the run plan', () => {
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  const allow = ext.match(/const MARKS_GRID_METHODS = new Set\(\[([^\]]*)\]\)/);
  assert.ok(allow, 'the grid has one allow-list of engine methods');
  for (const method of ['getMeasureMarks', 'getMeasureDescription', 'setDescription', 'draftMeasureDescription', 'proposeMeasureDescription',
    'keepMeasureDescription', 'planMeasureRun', 'runMeasurePlan', 'getMeasureCheckCache', 'setMeasurePerformance', 'listSqlSources', 'saveTest'])
    assert.match(allow[1], new RegExp(`'${method}'`), method + ' is allowed');
  assert.doesNotMatch(allow[1], /'(deploy|runDax|setProperty|deleteObject)'/);
  const page = readFileSync(new URL('../webview/src/checks-page.tsx', import.meta.url), 'utf8');
  assert.match(page, /MeasureMarksPanel/);
  assert.match(page, /RunPlan/);
  assert.match(page, /groupByState/);
  // d1 4.2: "Remove old annotations" moved into the page's ⋯ menu.
  assert.match(readFileSync(new URL('../webview/src/tests.tsx', import.meta.url), 'utf8'), />Remove old annotations<\/Item>/);
});

// d1 (fx23) 4.1: the row is the name and one glyph; the Description moved to the hover (Kane's call, spec section 8).
test('tree row A: a suspect mark or a contradiction makes the one mark suspect, and it comes before the Description', () => {
  const tailspin = m.treeRowItem('Sales Tailspin Toys', engineMarks, { oneLine: 'Sales amount for Tailspin Toys products only.', match: { score: 93, level: 'matches', label: 'Matches 93%' } });
  assert.equal(tailspin.description, '▲');
  const passedAll = engineMarks.map(x => ({ ...x, state: 'passed' }));
  const margin = m.treeRowItem('Gross Margin %', passedAll, { oneLine: 'Gross profit as a share of sales.', match: { score: 41, level: 'does not match', label: 'Does not match' } });
  assert.equal(margin.state, 'review');
  assert.equal(margin.description, '▲');
  assert.doesNotMatch(margin.description, /✓/, 'a contradiction is never a tick');
  const two = m.treeRowItem('Sales YTD', engineMarks, { oneLine: 'Sales Amount from 1 January.', match: { score: 38, level: 'does not match', label: 'Does not match' } });
  assert.equal(two.description, '▲', 'one glyph, no "+1"');
  assert.match(two.tooltip, /\(\+1 more\)/, 'the hover counts the rest');
});

test('tree row A: calm rows have a quiet glyph, and mostly unrun checks never read as a tick', () => {
  const quiet = engineMarks.map(x => ({ ...x, state: x.name === 'Contexts' || x.name === 'Filters' ? 'passed' : 'not run' }));
  const row = m.treeRowItem('Total Quantity', quiet, { oneLine: 'Number of units sold.', match: { score: 92, level: 'matches', label: 'Matches 92%' } });
  assert.equal(row.description, '✓', 'the marks that apply all passed');
  assert.equal(m.treeRowItem('Total Cost', [], { oneLine: 'Cost of the goods sold.' }).description, '', 'nothing ran: no glyph, never a tick');
});

test('tree row A: the hover lists the seven in their fixed order with one line each', () => {
  const row = m.treeRowItem('Sales Tailspin Toys', engineMarks, { oneLine: 'Sales amount for Tailspin Toys products only.' });
  const lines = row.tooltip.split('\n');
  assert.equal(lines[0], '▲ Needs review: a filter does not change it');
  assert.equal(lines[1], '▲ Filters   ✓ Contexts', 'the marks that apply and ran, in the fixed order');
  assert.equal(lines[2], 'Sales amount for Tailspin Toys products only.');
  assert.doesNotMatch(row.tooltip, /Select the measure to open its marks in Properties|—/);
});

test('tree row A: the Model tree calls the one row function for measures', () => {
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  const start = ext.indexOf('class ModelTreeProvider'), end = ext.indexOf('async getChildren(', start);
  const body = ext.slice(start, end);
  assert.match(body, /treeRowItem\(/);
  assert.match(body, /n\.kind === 'measure'/);
  assert.match(ext, /from '\.\/marksModel'/);
});