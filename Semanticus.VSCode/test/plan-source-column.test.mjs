#!/usr/bin/env node
// 1.5 visuals: the Plan's source column was 112px, so "Found by Semanticus" read "Found by Sem..." at about 1490px.
// The column must hold the longest source word, and the open row's detail grid must line up with it.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// A Windows checkout has CRLF line endings, and a source-text regex that names a newline must not care: read every source with \n only.
const source = (file) => readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
const css = source(process.env.STYLES_CSS || resolve(root, 'webview/src/styles.css'));
const model = source(resolve(root, 'webview/src/plan-model.ts'));
const firstColumn = (selector) => {
  const at = css.indexOf(`${selector} { display: grid; grid-template-columns: `);
  assert.ok(at >= 0, `${selector} has a grid`);
  return Number(/grid-template-columns: (\d+)px/.exec(css.slice(at, at + 160))[1]);
};

test('the source column fits the longest source word', () => {
  const words = /SOURCE_LABEL[^=]*=\s*\{([^}]*)\}/s.exec(model)?.[1] ?? '';
  const longest = Math.max(...[...words.matchAll(/'([^']+)'/g)].map((m) => m[1].length).filter((n) => n > 0), 0);
  assert.ok(longest >= 18, 'the longest source word is "Found by Semanticus"');
  // 12px mark + 6px gap + about 6.6px a letter at the meta size
  const needed = 12 + 6 + Math.ceil(longest * 6.6);
  assert.ok(firstColumn('.pw-row') >= needed, `the row's first column (${firstColumn('.pw-row')}px) holds ${needed}px`);
});

test('the open row detail and its changes line up with the row', () => {
  assert.equal(firstColumn('.pw-detail'), firstColumn('.pw-row'));
  assert.equal(firstColumn('.pw-changes'), firstColumn('.pw-row'));
});

// Hero run 21, N4: a row the assistant handed back shows its reason on the CLOSED row, not only inside the opened one.
test('a Needs you row shows the assistant\'s reason as its state note, cut short, with the whole reason on hover', async () => {
  const row = source(resolve(root, 'webview/src/plan-row.tsx'));
  assert.match(row, /handedBack/, 'the state cell has a handed-back branch');
  assert.match(row, /P\.reasonShort\(stateNote\)/, 'it shows the reason through reasonShort');
  assert.match(row, /title=\{closed \? [^}]*: item\.stateNote \?\? undefined\}/, 'the whole note stays on hover');
  const body = /export function reasonShort[\s\S]*?\n\}\n/.exec(model)?.[0];
  assert.ok(body, 'reasonShort exists in plan-model.ts');
  const { createRequire } = await import('node:module');
  const ts = createRequire(resolve(root, 'webview/package.json'))('typescript');
  const js = ts.transpileModule(body, { compilerOptions: { target: 'ES2020' } }).outputText.replace('export function', 'function');
  const reasonShort = new Function(`${js}; return reasonShort;`)();
  assert.equal(reasonShort('Needs a date column.'), 'Needs a date column.');
  assert.equal(reasonShort('Needs a date column: none exists in this model.'), 'Needs a date column: none...');
  assert.equal(reasonShort(''), null);
  assert.ok(!/—/.test(reasonShort('x '.repeat(40)) ?? ''), 'no em dash');
});
