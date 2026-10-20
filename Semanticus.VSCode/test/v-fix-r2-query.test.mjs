// verify: m16/fix-r2-query at 0d97b5b2 (18 Oct), failing on purpose. Two defects seen live on 1.5.106 in a clean VS Code:
//
//   1. Speed on a shared service, after Measure again (Northstar Sales Working, 120 ms row): the detail still reads
//      "Warm-only: the cache was not cleared (cold runs skipped). Storage engine 0 ms means the server reported no storage
//      work (cached or the trace missed it), not a measured round trip." (shot v027). plainEngineNote drops every jargon
//      sentence, finds nothing left, and then falls back to the raw note (`noJargon(t) || t`). The lane's own test only
//      covered the "needs confirm=true" form, and the lane's N8 change makes the "Warm-only" form the one a shared server
//      always sends.
//   2. Fields on an asked question shows that question's fields only when the wells are empty. Ask "sales by brnad and yaer",
//      press Fields (wells fill: Brand, Year, Net Sales). Ask "net sales trend by month in 2025" or "top 10 prodcts by net
//      sales", press Fields: the wells show Brand, Year, Net Sales again, not the question's fields and not the "has a filter /
//      a top 10 ... stays as it is" line (shots v041, v046). Under the question the Answer then reads "Split by Brand, Split by
//      Year" over month or product rows (v042, v047), and Measure speed and Record the steps end with no result and no word
//      (v049 to v053). A question asked after fields were built shows the wells' query in its DAX tab (v108).
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}

let failed = 0;
const check = (name, fn) => { try { fn(); console.log('ok  ', name); } catch (e) { failed++; console.log('FAIL', name); console.log(e.message); } };

const EW = load(join(webview, 'src', 'enginewords.ts'));
const warmOnly = 'Warm-only: the cache was not cleared (cold runs skipped). Storage engine 0 ms means the server reported no storage work (cached or the trace missed it), not a measured round trip.';

check('1: the shared-server speed note never reaches a person in engine words', () => {
  assert.doesNotMatch(EW.plainEngineNote(warmOnly), /storage engine|trace missed|warm-only/i,
    'plainEngineNote falls back to the raw note when every sentence is jargon (enginewords.ts noJargon(t) || t)');
});

check('2: Fields on an asked question shows that question, whatever the wells held before', () => {
  const lab = src('daxlab.tsx');
  assert.doesNotMatch(lab, /\(chipsHere \|\| wordsOnly\) && wellsEmpty/,
    'chooseEntry only reads the question when the wells are empty, so a second question shows the first one\'s fields');
});

if (failed) { console.log(`${failed} failing`); process.exit(1); }
