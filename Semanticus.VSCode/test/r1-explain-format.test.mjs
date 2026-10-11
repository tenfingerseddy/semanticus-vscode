// Round 1, hero journey 6 (Kane, 8 Oct): a number is shown in the measure's own format everywhere (Ask grid, Explain this
// number), and the explanation reads in plain words ("Margin % is 35.8% for 2025."), never 0.3578 or an engine sentence.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req, webview } from './helpers/webview-load.mjs';
import { join } from 'node:path';

const React = req('react');
const stub = (name) => new Proxy({}, { get: (_, k) => (k === '__esModule' ? false : k === 'uiLabel' ? (x) => x : () => null) });
const load = makeLoader({ './bridge': { rpc: async () => ({}) }, './sendtask': { GiveToAssistant: () => null, explainTask: () => '' }, './copy': { uiLabel: (x) => x } }, React);
const { formatNumber, percentOf } = load('gridformat.ts');
const { fmtNum, leadSentence } = load('explainpanel.tsx');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');

const MONEY = '\\$#,0.00;(\\$#,0.00);\\$#,0.00';

test('a currency or thousands format shows the number the way the model says (36111492.86 as $36,111,492.86)', () => {
  assert.equal(formatNumber(36111492.86, MONEY), '$36,111,492.86');
  assert.equal(formatNumber(-1234.5, MONEY), '($1,234.50)');
  assert.equal(formatNumber(1234567, '#,0'), '1,234,567');
  assert.equal(formatNumber(0.3578, '0.0%'), '35.8%');
  assert.equal(formatNumber(12.3456, '0.00'), '12.35');
});

test('a format that is not fully understood, or no format, leaves the number alone', () => {
  assert.equal(formatNumber(5, undefined), null);
  assert.equal(formatNumber(5, ''), null);
  assert.equal(formatNumber(5, '0.0,,"M"'), null);
  assert.equal(formatNumber(5, 'yyyy-mm-dd'), null);
  // percentOf still means percent only.
  assert.equal(percentOf(36111492.86, MONEY), null);
});

test('the grid shows a measure column in its format', () => {
  const grid = src('grid.tsx');
  assert.match(grid, /formatNumber\(v, format\)/);
  assert.doesNotMatch(grid, /percentOf/);
});

test('Explain this number shows the value in the measure format and a plain lead sentence', () => {
  assert.equal(fmtNum('0.35775270636660383', '0.0%'), '35.8%');
  assert.equal(fmtNum('0.3578', undefined), '0.3578');
  const d = { valueEvaluated: true, isBlank: false, value: '0.35775270636660383', summary: '[Margin %] is 0.36 for this selection.' };
  assert.equal(leadSentence('Margin %', d, '0.0%', [{ field: 'Year', value: '2025', src: 'filter' }]), 'Margin % is 35.8% for 2025.');
  // A filter entry carries its operator in its value ("= 2025"): the sentence says the value only.
  assert.equal(leadSentence('Margin %', d, '0.0%', [{ field: 'Year', value: '= 2025', src: 'filter' }]), 'Margin % is 35.8% for 2025.');
  assert.equal(leadSentence('Margin %', d, '0.0%', []), 'Margin % is 35.8%.');
  // A blank keeps the engine's own sentence.
  assert.equal(leadSentence('Margin %', { valueEvaluated: true, isBlank: true, summary: 'shows no value' }, '0.0%', []), 'shows no value');
});

test('the panel and the Ask page show the explanation, and the Ask page shows it in place of the table', () => {
  const panel = src('explainpanel.tsx');
  assert.match(panel, /What it is built from/);
  assert.match(panel, /export function ExplainInline/);
  assert.match(panel, /How it differs by/);
  const ask = src('askdata.tsx');
  assert.match(ask, /answer\.why/);
  assert.match(ask, /hasRows && !why/);
});

test('K12 the selection reads "Year = 2025", never "Year = = 2025", and a button with a zero count is not shown', () => {
  const { plainValue, selectionWords } = load('explainpanel.tsx');
  assert.equal(plainValue('= 2025'), '2025');
  assert.equal(plainValue('2025'), '2025');
  assert.equal(selectionWords([{ field: 'Year', value: '= 2025' }, { field: 'City', value: 'Perth' }]), 'Year = 2025; City = Perth');
  assert.match(src('bpa.tsx'), /card\.autoFixable > 0\) && <Button/, 'Fix 0 is not shown');
  assert.match(src('App.tsx'), /card\.safeFixCount > 0\) && <button/, 'Apply 0 safe fixes is not shown');
});
