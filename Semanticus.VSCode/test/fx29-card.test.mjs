import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req } from './helpers/webview-load.mjs';

// fx29: the measure card gaps of the rc28 release tester (verdict 2026-10-28-rc28-release-1, gaps 4, 5, 6 and 9; spec 4.4).
// The layout half of gaps 4 and 6 and the timing of gap 9 are proved in the browser by tools/uishot/fx29-card-drive.mjs.
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const panel = load('./marks-panel.tsx');
const hostModule = load('./marks-host.ts');
const host = { rpc: async () => null, onChange: () => () => {}, open() {}, undo() {} };
const html = node => renderToStaticMarkup(React.createElement(hostModule.MarksHostContext.Provider, { value: host }, node));
const detail = (mark, state) => html(React.createElement(panel.MarkCard, { reference: 'measure:Sales/Total Cost', mark, state, others: [], onRecheck() {}, onKeep() {}, onPropose() {} }));
const source = file => readFileSync(new URL('../webview/src/' + file, import.meta.url), 'utf8');

const timings = [['City', 104, 96, 131, 160], ['Grand total', 100, 92, 118, 210], ['Brand', 98, 90, 125, 150]]
  .map(([context, medianMs, minMs, maxMs, coldMs]) => ({ context, medianMs, minMs, maxMs, coldMs }));
const performance = { name: 'Performance', state: 'passed', sentence: 'Slowest: City, median 104 ms.', lastRan: new Date().toISOString(),
  applies: true, verdict: 'verified', timings, cells: timings.map(t => ({ context: t.context, after: t.medianMs + ' ms' })), chosenContexts: [] };

test('fx29 gap 5: the timing line reads "Timing. About <t>." with no run counts and no Cold or Warm words', () => {
  assert.equal(typeof m.timingLine, 'function', 'the card has one place that words the timing line');
  for (const [progress, line] of [
    ['Timing this measure: 6 of 21 runs done. Cold: Grand total. About 2 seconds remaining.', 'Timing. About 2 seconds.'],
    ['Timing this measure. Preparing five warm runs and one cold run. About 30 seconds.', 'Timing. About 30 seconds.'],
    ['Timing this measure: Waiting for 2 other checks first.', 'Timing. About 30 seconds.'],
    ['Timing this measure: 1 of 21 runs done. About 1 minute remaining.', 'Timing. About 1 minute.'],
    ['Timing this measure: 20 of 21 runs done. About 1 second remaining.', 'Timing. About 1 second.'],
  ]) {
    const said = m.timingLine(progress);
    assert.equal(said, line, progress);
    assert.doesNotMatch(said, /\bof \d+|runs|cold|warm|remaining/i, said);
  }
  // The card shows the line through the model, never the engine's progress words.
  const card = source('marks-panel.tsx');
  assert.match(card, /timingLine\(/, 'the card words the timing line with timingLine');
  assert.doesNotMatch(card, /data-testid="mk-timing">\{timing\}/, 'the card prints the engine progress as it came');
});

test('fx29 gap 6: the Performance detail is one table: Where, Median, Spread, Cold', () => {
  const markup = detail(performance, 'verified');
  const tables = markup.match(/<table/g) ?? [];
  assert.equal(tables.length, 1, 'one table, not a Where and Value table beside the timings: ' + markup);
  const heads = [...markup.matchAll(/<th[^>]*>([^<]*)<\/th>/g)].map(x => x[1]);
  assert.deepEqual(heads, ['Where', 'Median', 'Spread', 'Cold']);
  assert.match(markup, /104 ms/);
  // Another mark keeps its own table of places.
  const filters = detail({ name: 'Filters', state: 'passed', sentence: 'No ignored filter in 157 places checked.', applies: true, verdict: 'verified',
    cells: [{ context: 'City: Aachen', after: '122', afterText: '122' }], chosenContexts: [], timings: [] }, 'verified');
  assert.equal((filters.match(/<table/g) ?? []).length, 1);
});

test('fx29 gap 4: a table with two or more number columns stacks its place over the numbers in a narrow card', () => {
  const trusted = detail({ name: 'Trusted number', state: 'suspect', sentence: 'The measure differs from a number you trust.', applies: true, verdict: 'broken',
    cells: [{ context: 'Total Cost: All', before: '1000000', after: '89766267.02', beforeText: '$1,000,000.00', afterText: '$89,766,267.02' }], chosenContexts: [], timings: [] }, 'broken');
  assert.match(trusted, /class="[^"]*mk-stack[^"]*"[^>]*style="--mk-n:2"/, 'two number columns: ' + trusted);
  assert.match(detail(performance, 'verified'), /class="[^"]*mk-stack[^"]*"[^>]*style="--mk-n:3"/, 'three number columns');
  const css = source('marks.css');
  // The place cell never breaks inside a word: "anywhere" let the table shrink the column to one letter (tester 14a, "Whe / re").
  const place = /\.mk-cells td:first-child, \.mk-cells th:first-child \{([^}]*)\}/.exec(css)?.[1] ?? '';
  assert.doesNotMatch(place, /overflow-wrap:\s*anywhere|word-break:\s*break-all/, place);
  assert.match(css, /@container[^{]*\{[^@]*\.mk-stack/, 'a narrow card stacks the place over its numbers');
});

test('fx29 gap 9: Keep shows the kept Description at once, before the engine read returns', () => {
  const card = source('marks-panel.tsx');
  const keep = /const keep = async \(\) => \{([\s\S]*?)\n  \};/.exec(card)?.[1] ?? '';
  const kept = keep.indexOf('keepMeasureDescription'), shown = keep.search(/\bshown\(kept|setView\(/), read = keep.indexOf('await read()');
  assert.ok(kept >= 0 && shown > kept && (read < 0 || shown < read), 'Keep sets the kept text on the card before it reads the Description again: ' + keep);
  // shown() puts the text on the card in place of the read's old view.
  assert.match(card, /const shown = \(text: string, provenance: string\) => setView\(v => v && \{ \.\.\.v, description: text/);
});
