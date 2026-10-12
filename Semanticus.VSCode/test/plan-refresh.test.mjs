// Refresh plan, and findings that are already on the plan (Kane, 11 Oct 2026: "Are AI readiness and BPA fixes automatically
// added to the plan? ... we need to add a refresh the plan button that clears out done items and checks for any new items").
//
// What is executed here, one line each:
//   AI readiness and Model quality findings join the Plan by themselves, so neither page offers to add one a plan row
//     already carries: Give the rest leaves those out, and a Model quality finding on a row says On the plan (it said
//     Give to your assistant, which read as "this is not on the plan", and pressing it put the work on the plan twice)
//   the Plan page has one Refresh plan button in its toolbar; it calls refreshPlan, says Refreshing while it runs, and the
//     engine's one sentence lands in the page's status line
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = join(webview, 'src');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const STUBS = {
  './bridge': {
    rpc: async () => null, onDidChange: () => () => {}, onProgress: () => () => {}, onReconnect: () => () => {},
    onPlanChange: () => () => {}, copyText: async () => {}, runHostCommand: () => {}, navigateInStudio: () => {},
  },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => {
    if (STUBS[spec]) return STUBS[spec];
    if (spec.startsWith('.')) return spec.endsWith('.css') ? {} : load(resolve(dirname(full), spec));
    return req(spec);
  };
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  cache.set(full, module.exports);
  return module.exports;
}
const read = (name) => readFileSync(join(src, name), 'utf8');

// ---- findings a plan row carries are not offered again ----------------------------------------------------------
const S = load(join(src, 'sendtask.tsx'));
assert.equal(typeof S.notOnPlan, 'function', 'sendtask.tsx must export notOnPlan(items, rows)');
const rows = [
  { id: 'words/model', stateWord: 'waiting', rules: ['DESC-MEASURE', 'MEASURES_SHOULD_HAVE_A_FORMAT_STRING'], objects: ['measure:Sales/Total Sales'] },
  { id: 'bpafix/model', stateWord: 'ready', rules: ['AVOID_FLOATING_POINT_DATA_TYPES'], objects: [] },
  { id: 'safefix/Naming', stateWord: 'done', status: 'done', rules: ['NAME-TRIM'], objects: [] },
];
const findings = [
  { ruleId: 'DESC-MEASURE', objectRef: 'measure:Sales/Total Sales' },              // on the words row
  { ruleId: 'MEASURES_SHOULD_HAVE_A_FORMAT_STRING', objectRef: 'measure:Sales/Total Sales' },
  { ruleId: 'avoid_floating_point_data_types', objectRef: 'column:Sales/Amount' },  // on the best-practice row, any case
  { ruleId: 'DESC-MEASURE', objectRef: 'measure:Sales/Margin' },                   // the row lists objects, and not this one
  { ruleId: 'NAME-TRIM', objectRef: 'column:Sales/ Amount' },                      // its row is done: not on the plan now
  { ruleId: 'REL-AMBIGUOUS', objectRef: 'table:Sales' },                           // no row at all
];
assert.deepEqual(S.notOnPlan(findings, rows).map((f) => f.ruleId + '|' + f.objectRef), [
  'DESC-MEASURE|measure:Sales/Margin', 'NAME-TRIM|column:Sales/ Amount', 'REL-AMBIGUOUS|table:Sales',
]);
assert.deepEqual(S.notOnPlan(findings, []), findings, 'with no plan read, every finding can still be given');

const bpa = read('bpa.tsx');
assert.doesNotMatch(bpa, /<GiveToAssistant /, 'Model quality must not offer Give to your assistant for a finding a plan row carries');
assert.match(bpa, /<GiveOrOnPlan ruleId=\{v\.ruleId\} objectRef=\{v\.objectRef\}/, 'a Model quality finding on a plan row must say On the plan');
const findingsSrc = read('findings.tsx');
assert.match(findingsSrc, /notOnPlan\(items\)/, 'Give the rest on Model quality must leave out what a plan row already carries');
const app = read('App.tsx');
assert.match(app, /const rest = notOnPlan\(restOfFindings\(card\)\)/, 'Give the rest on AI readiness must leave out what a plan row already carries');

// ---- the Refresh plan button ---------------------------------------------------------------------------------
const F = load(join(src, 'plan-refresh.tsx'));
let pressed = 0;
const idle = renderToStaticMarkup(React.createElement(F.RefreshPlanButton, { busy: null, onRefresh: () => { pressed++; } }));
assert.match(idle, /<button type="button" class="sem-btn sem-btn-sm"[^>]*data-testid="plan-refresh"[^>]*>Refresh plan<\/button>/);
assert.doesNotMatch(idle, /disabled/, 'Refresh plan is pressable while nothing else runs');
const running = renderToStaticMarkup(React.createElement(F.RefreshPlanButton, { busy: 'refresh', onRefresh: () => {} }));
assert.match(running, /disabled=""[^>]*>Refreshing\.\.\.<\/button>/, 'the button says it is working while the scan runs');
const other = renderToStaticMarkup(React.createElement(F.RefreshPlanButton, { busy: 'run', onRefresh: () => {} }));
assert.match(other, /disabled=""[^>]*>Refresh plan<\/button>/, 'one press at a time: another control running holds the button');
for (const copy of [idle, running]) assert.doesNotMatch(copy, /—|one click/i, 'copy rules: no em dash, never "one click"');

const plan = read('plan.tsx');
assert.match(plan, /<RefreshPlanButton busy=\{busy\} onRefresh=\{\(\) => void refresh\(\)\} \/>/, 'the Plan toolbar draws Refresh plan');
assert.match(plan, /run\('refresh', 'refreshPlan'\)/, 'Refresh plan calls the engine\'s refreshPlan, the same call as refresh_plan');
assert.match(plan, /if \(r\.ok\) setJustDone\(new Set\(\)\)/, 'the rows that finished while the page was open leave the list too');
// The engine's sentence ("2 new items, 3 done items cleared." or "Nothing new.") is the answer's sentence, which run() puts in plan-said.
assert.match(plan, /if \(said && !\('items' in \(value as P\.Plan\)\)\) setSentence\(said\)/);
assert.match(plan, /data-testid="plan-said">\{sentence\}/);

console.log('plan-refresh: ok');
