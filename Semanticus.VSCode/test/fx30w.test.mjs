import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

// fx30w: the screen-side faults of the wr1 list (F1, F9, S3). F8 is wr1-plan-budget.test.mjs; S2 is a scroll, so its proof is
// tools/uishot/fx30w-drive.mjs (no DOM in this suite).
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, onCheckState: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const panel = load('./marks-panel.tsx');
const hostModule = load('./marks-host.ts');
const host = { rpc: async () => null, onChange: () => () => {}, open() {}, undo() {} };
const html = node => renderToStaticMarkup(React.createElement(hostModule.MarksHostContext.Provider, { value: host }, node));
const ran = new Date().toISOString();
const ADVICE_OFF = 'Pro advice is off for this model.';
const NO_DESCRIPTION = { score: null, level: 'not scored yet', label: 'not scored yet', reason: 'There is no Description to score.' };

test('wr1 F1: at open, before any run, the scoring state names Pro advice off on the Checks page and the card', () => {
  // A fresh open: no measure has a check record, Total Quantity has no Description. Only the page read's scoring state says it.
  const rows = [{ ref: 'measure:Sales/Total Quantity', name: 'Total Quantity', marks: [], match: NO_DESCRIPTION, checked: false }];
  assert.equal(m.checksActionable(ADVICE_OFF, rows, false), ADVICE_OFF, 'the Checks page line');
  assert.equal(m.cardActionable(ADVICE_OFF, [], NO_DESCRIPTION), ADVICE_OFF, 'the card line');
  // Every other unscored case reads its own line from the same state; a service fault and "nothing stops it" read none.
  for (const line of ['Scoring needs a Pro plan.', 'Save the model or open it live to score it.', 'Scoring is off for this model.'])
    assert.equal(m.cardActionable(line, [], NO_DESCRIPTION), line);
  assert.equal(m.cardActionable(null, [], NO_DESCRIPTION), null);
  assert.equal(m.cardActionable('This score turns on when Jev scoring is released.', [], null), null);
  // Turn on said yes on the Checks page: the advice line goes at once, before the page is read again.
  assert.equal(m.checksActionable(ADVICE_OFF, rows, true), null);
  // A line a mark carries (consent changed) still shows when the state names nothing.
  const changed = [{ name: 'Totals', state: 'not run', sentence: 'Not scored yet. Pro advice is on now. Check again to score it.' }];
  assert.equal(m.cardActionable(null, changed, null), 'Pro advice is on now. Check again to score it.');
  const face = html(React.createElement(panel.CardFace, { verdict: m.cardVerdict([]), actionable: m.cardActionable(ADVICE_OFF, [], NO_DESCRIPTION), onTurnOn() {},
    marks: [], open: null, checked: '', onMark() {}, onVerdict() {}, onCheck() {}, description: null }));
  assert.match(face, /Pro advice is off for this model\. <button[^>]*>Turn on</);
});

test('wr1 F9: one count for "places": Where it looked shows no second number beside the places sentence', () => {
  const ctx = Array.from({ length: 5 }, (_, i) => ({ id: 'c' + i, shape: 'Single', reason: 'A place.' }));
  const filters = { name: 'Filters', state: 'passed', sentence: 'No ignored filter in 157 places checked.', lastRan: ran, chosenContexts: ctx, cells: [{ context: 'City: Aachen', after: '122' }] };
  const detail = html(React.createElement(panel.MarkCard, { reference: 'measure:Sales/Total Quantity', mark: filters, state: 'verified', others: [], onRecheck() {} }));
  assert.match(detail, /No ignored filter in 157 places\./);
  const button = /<button[^>]*>(Where it looked[^<]*)</.exec(detail)?.[1];
  assert.ok(button, 'Where it looked is still offered');
  const counts = [...detail.replace(/<[^>]*>/g, ' ').matchAll(/(\d[\d,]*)\s+places|\((\d+)\)/g)].map(x => x[1] ?? x[2]);
  assert.deepEqual([...new Set(counts)], ['157'], `one count: "${button}" beside "No ignored filter in 157 places."`);
});

test('wr1 S3: the tree hover of a measure not checked yet reads "Not checked yet" (spec 4.1)', () => {
  const item = m.treeRowItem('Gross Profit', [], {});
  assert.equal(item.tooltip, 'Not checked yet');
  assert.equal(item.label, 'Gross Profit, Not checked yet');
  // The card keeps its reason clause, within its 30 words.
  assert.equal(m.cardVerdict([]).line, 'Not checked yet: no run has included it');
});
