// Hero 24 D2 (HARM): the assistant asked "net sales in June 2025" (ask_data) and pointed the Studio at the measure (show_in_studio). The Query page
// then showed `EVALUATE ROW("Net Sales", [Net Sales])`, no filter, under "Read as ... YearMonth = 2025-06" and said the person had changed the query
// by hand. One press of Run gave the number for every date under the June chip. The answer's own query must stay, filters and all.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const load = (file) => {
  const module = { exports: {} };
  const js = ts.transpileModule(readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
  new Function('module', 'exports', 'require', js)(module, module.exports, (n) => { throw new Error('unexpected import ' + n); });
  return module.exports;
};
// seedForMeasureFocus is pure; the file it lives in only imports types, so it loads on its own.
const { seedForMeasureFocus, answerFilterLines } = load(join(root, 'webview', 'src', 'askdata-model.ts'));

// The June case, as the engine writes it (QueryTemplate.Build): the filter is a VAR above the result.
const juneDax = [
  'DEFINE',
  '    VAR __Filter1 =',
  "        TREATAS({\"2025-06\"}, 'Date'[YearMonth])",
  '    VAR __Result =',
  '        CALCULATETABLE(ROW("Net Sales", [Net Sales]), __Filter1)',
  'EVALUATE __Result',
].join('\n');
assert.equal(answerFilterLines(juneDax).length, 1, 'the June query carries its filter');

// 1. The assistant's answer still holds the page and is about the focused measure: no seed, so the filtered query stays.
assert.equal(seedForMeasureFocus('Net Sales', { byAssistant: true, owns: true, measures: ['Net Sales'] }), null);
assert.equal(seedForMeasureFocus('net sales', { byAssistant: true, owns: true, measures: ['Net Sales'] }), null, 'names compare without case');

// 2. Every other case still seeds the one-measure query, as before.
const seed = 'EVALUATE ROW("Net Sales", [Net Sales])';
assert.equal(seedForMeasureFocus('Net Sales', null), seed, 'nothing held');
assert.equal(seedForMeasureFocus('Net Sales', { byAssistant: false, owns: true, measures: ['Net Sales'] }), seed, "the person's own answer is replaced by their deliberate action");
assert.equal(seedForMeasureFocus('Net Sales', { byAssistant: true, owns: false, measures: ['Net Sales'] }), seed, 'the query was changed since: it is not the answer any more');
assert.equal(seedForMeasureFocus('Units', { byAssistant: true, owns: true, measures: ['Net Sales'] }), 'EVALUATE ROW("Units", [Units])', 'another measure');
assert.equal(seedForMeasureFocus('A"B]', null), 'EVALUATE ROW("A""B]", [A"B]]])', 'quotes and brackets are escaped');

// 3. The page is wired: the focus effect asks the helper and keeps the query on null; the answer is the assistant's only while the page holds that very answer.
const lab = readFileSync(join(root, 'webview', 'src', 'daxlab.tsx'), 'utf8').replace(/\r\n/g, '\n');
assert.match(lab, /const seedText = seedForMeasureFocus\(name, heldAnswer\.current\);\n\s+if \(seedText === null\) \{[\s\S]{0,300}setTestMeasureRef\(navTarget\.ref\);[\s\S]{0,80}return;/);
assert.match(lab, /adoptedAnswer\.current = a;/);
assert.match(lab, /byAssistant: !!ask\.answer && adoptedAnswer\.current === ask\.answer/);
console.log('query-follow-assistant: ok');
