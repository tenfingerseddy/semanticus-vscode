import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader } from './helpers/webview-load.mjs';

test('the native count includes every saved-check issue and opens Checks', () => {
  const load = makeLoader();
  const M = load('../../src/checksCount.ts');
  assert.deepEqual(M.checksCountView({ total: 6, numbersDiffer: 1, keyProblems: 1, tableProblems: 1 }), {
    text: '1 number differs · 1 key problem · 1 table problem · 6 measures',
    attention: true, command: 'semanticus.studioGoTab', args: ['tests'],
  });
  assert.equal(M.checksCountView({ total: 6 }).text, '6 measures');
});

test('the model row and status bar use the same count view and route', () => {
  const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.ok(source.includes('checksCountView(page.summary)'));
  assert.ok(source.includes('tree.setChecksCount(view)'));
  assert.ok(source.includes('checksStatus.text ='));
  assert.ok(source.includes("row.command = { command: 'semanticus.studioGoTab', title: 'Open Checks', arguments: ['tests'] }"));
});
