// verify: v-fix-uat-validation (18 Oct), FAILS on purpose at b686f546.
// Run from Semanticus.VSCode: node <this file> <path to Semanticus.VSCode>
//
// Live repro (Northstar Sales Working, real VS Code, real clicks): a grouped Check against SQL, Net Sales by Product Brand,
// all 3 brands equal to the cent. The row says Pass ("Matches"). Opened, its FIRST line reads
//   "The model answered nothing, and Northstar Source says nothing. One of these is not a number, so we cannot say by how
//    much they differ."
// over a table of three matching rows, and the facts read "Expected ·  Actual ·". Shot: v39-brand-open.png.
// Cause: CheckDetail (tests.tsx:1053) leads every outcome that is not an error with
//   differenceSentence(outcome.expected ?? value?.expectedValue, outcome.actual, outcome.difference ?? null, sourceLabel)
// and a grouped outcome has no single expected, actual or difference, so the "not a number" branch fires on a Pass.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createRequire } from 'node:module';

const ext = resolve(process.argv[2] ?? '.');
const ts = createRequire(resolve(ext, 'package.json'))('typescript');
async function load(file) {
  const out = ts.transpileModule(readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(out, 'utf8').toString('base64')}`);
}
const model = await load(resolve(ext, 'webview/src/tests-model.ts'));

// The outcome the engine returned for the grouped check (what the page showed): Pass, no single figure, no difference.
const outcome = { verdict: 'Pass', expected: undefined, actual: undefined, difference: null };
assert.equal(model.resultChip(true, outcome), 'Pass', 'the grouped check passed');

// The page's own call for the lead sentence (tests.tsx:1053), with the same arguments.
const lead = model.differenceSentence(outcome.expected, outcome.actual, outcome.difference ?? null, 'Northstar Source');
assert.doesNotMatch(lead, /answered nothing|says nothing|cannot say/,
  `a check that passed must not lead with "${lead}"`);
console.log('grouped-pass-lead-sentence: ok');
