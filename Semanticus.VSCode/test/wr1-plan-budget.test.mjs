import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

// wr1 F8: the run plan line keeps its 15-word budget (spec 4.3, finish line D1) when four reason groups show. Live on pilot2
// after an edit it read "Checking 12 of 12: 2 changed, 3 core, 4 risky, 3 oldest. About 3 minutes." with Choose and Run: 17.
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({ './bridge': { onDidChange: () => () => {}, rpc: async () => null, runHostCommand() {} } });
const m = load('./marks-model.ts');
const plan = load('./run-plan.tsx');
const hostModule = load('./marks-host.ts');
const host = { rpc: async () => null, onChange: () => () => {}, open() {}, undo() {} };
const html = node => renderToStaticMarkup(React.createElement(hostModule.MarksHostContext.Provider, { value: host }, node));
const WORD = /[A-Za-z0-9][A-Za-z0-9%:.,'’()\-]*/g;
const words = markup => (markup.replace(/<[^>]*>/g, ' ').match(WORD) ?? []).length;

test('wr1 F8: the run plan line is 15 words or fewer with four reason groups', () => {
  const group = (g, n) => [...Array(n)].map((_, i) => ({ measureRef: `measure:Sales/${g}${i}`, group: g, reason: 'x' }));
  const picks = [...group('changed', 2), ...group('core', 3), ...group('risky', 4), ...group('oldest', 3)];
  const p = { id: 'p', totalMeasures: 12, batchSize: 30, estimatedSeconds: 132, sentence: '', picks, firstRun: false };
  const summary = m.planSummary(p, false);
  const rest = html(React.createElement(plan.PlanLine, { sentence: summary.sentence, running: false, canRun: true, onChoose() {}, onRun() {} }));
  assert.ok(words(rest) <= 15, `the run plan is ${words(rest)} words: ${summary.sentence} Choose Run`);
});
