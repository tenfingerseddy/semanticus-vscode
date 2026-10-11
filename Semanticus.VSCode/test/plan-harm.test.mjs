// Anyone who follows the Plan must not be hurt (Kane, 28 Sep 2026; lane m11/plan-harm): the Studio half.
//
// What is executed here, one line each:
//   the Plan's "Save the changes you have made" row offers Save, and Save is a save (it was Choose, which opened
//     History and saved nothing)
//   the page's Save asks the host with the message the host's Studio relay answers
//   the optional cleanup row says "Optional" on its collapsed line while it waits (the waits line replaced it)
//   the permission card's check runs again after a new model's first save (the behaviour is proven by
//     tools/uishot/consent-drive.mjs, scenes card-after-first-save and line-after-first-save; this pins the wiring)
process.env.TZ = 'UTC';
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
    rpc: async () => null, onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
    onPlanChange: () => () => {}, onSpecChange: () => () => {}, copyText: async () => {}, pickSpecFile: async () => null,
    saveModelFromPage: async () => true,
  },
  './connection': { useConnection: () => ({ session: { sessionId: 'test' }, context: null }) },
  './pro': {
    useTier: () => 'pro', useFeature: () => 'granted', isEntitlementError: () => false, ProBadge: () => null,
    UpsellNotice: () => null, PENDING_ACCESS: 'Checking your Pro access', upgradeToPro: () => {},
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

const P = load(join(src, 'plan-model.ts'));
const R = load(join(src, 'plan-row.tsx'));

// ---- the Save row saves -------------------------------------------------------------------------
const saveRow = {
  id: 'ship/unsaved', section: 'Ship', title: 'Save the changes you have made', sub: 'they are only in this session',
  who: 'you', status: 'ready', why: ['This model has changes that are not saved yet.'], diff: [],
  jump: { tool: 'history', label: 'History' }, source: 'found', doer: 'you', stateWord: 'needs_you', state: 'gap',
};
assert.deepEqual(P.mainAction(saveRow, false), { id: 'save', label: 'Save', primary: true },
  'the Save row must offer Save as its one main button, not Choose (which opened History)');
const html = renderToStaticMarkup(React.createElement(R.PlanRow, {
  item: saveRow, items: [saveRow], open: false, busy: null, onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {},
}));
assert.match(html, /data-testid="plan-act-save"[^>]*>Save</, 'the Save row does not draw a Save button');
assert.doesNotMatch(html, /plan-act-choose/, 'the Save row still draws Choose');

// Every other derived choice has no main button (Choose is gone: the page link on the row opens its page): the Save is the Save row's alone.
const other = { ...saveRow, id: 'datetable/model', title: 'Mark the date table', jump: { tool: 'model', label: 'Model' } };
assert.equal(P.mainAction(other, false), null);

// ---- the optional row says Optional on its collapsed line, even while it waits (verify, 28 Sep, L1) ----
const cleanupRow = {
  id: 'cleanup/model', section: 'Structure', title: 'Remove 2 unused things', sub: 'Optional. Waits for: Fix 10 names and 16 more',
  optional: true, who: 'you', status: 'ready', why: ['Optional. No report has been read.'], diff: [], source: 'found', doer: 'you',
  stateWord: 'waiting', state: 'gap', waitsFor: [{ id: 'safefix/Naming', title: 'Fix 10 names' }, { id: 'describe/model', title: 'Describe 71 things' }],
};
const cleanupHtml = renderToStaticMarkup(React.createElement(R.PlanRow, {
  item: cleanupRow, items: [cleanupRow], open: false, busy: null, onToggle: () => {}, onAction: () => {}, onDoer: () => {}, onGo: () => {},
}));
assert.match(cleanupHtml, /class="pw-sub[^"]*"[^>]*>Optional · Waits for: <b>Fix 10 names and 1 more<\/b>/,
  'the optional row\'s collapsed line drops the word Optional while it waits');
const plainWaiting = { ...cleanupRow, id: 'describe/model', optional: false, sub: 'Waits for: Fix 10 names and 1 more' };
assert.doesNotMatch(renderToStaticMarkup(React.createElement(R.PlanRow, {
  item: plainWaiting, items: [plainWaiting], open: false, busy: null, onToggle: () => {}, onAction: () => {}, onGo: () => {},
})), /Optional/, 'a row that is not optional must not say Optional');

// ---- the page's Save reaches the host's save --------------------------------------------------------
const bridge = readFileSync(join(src, 'bridge.ts'), 'utf8');
const plan = readFileSync(join(src, 'plan.tsx'), 'utf8');
const host = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.match(bridge, /export function saveModelFromPage\(\)/, 'the bridge has no way to ask the host to save');
assert.match(bridge, /postToHostStamped\(\{ type: 'saveModel', id \}\)/, 'the bridge does not post saveModel');
assert.match(bridge, /msg\.type === 'saveModelResult'/, 'the bridge never reads the host\'s answer to a save');
assert.match(host, /msg\?\.type === 'saveModel'[\s\S]{0,200}saveCommand\(\)[\s\S]{0,200}type: 'saveModelResult'/,
  'the Studio relay no longer saves on saveModel and answers saveModelResult');
assert.match(plan, /case 'save':[^\n]*\n?[^\n]*saveModelFromPage\(\)/, 'the Plan page does not save on the Save row');

// ---- the permission card is asked again after a first save -----------------------------------------
const consent = readFileSync(join(src, 'consent.tsx'), 'utf8');
const hook = consent.slice(consent.indexOf('export function useConsentCard'), consent.indexOf('export function ConsentCard'));
assert.match(hook, /onDidChange\(/, 'useConsentCard does not listen for the save that gives a new model an identity');
assert.match(hook, /\}, \[missing, tier, again\]\);/, 'useConsentCard does not run its check again after a first save');
const line = consent.slice(consent.indexOf('export function ProAdviceLine'));
assert.match(line, /onDidChange\(\(\) => read\(\)\)/, 'the permission line does not read again after a first save');

// ---- the screenshot fixtures say only what the product says and offer only what it offers (verify, 28 Sep, L2, L3) ----
// A fixture is what a reviewer holds up against the product, so a fixture that says an old name or offers a removal the
// engine never makes teaches the wrong product. The grade is the AI readiness grade (lane m11/plan-harm), and the
// cleanup never offers any column of a date table while no report has been read (LocalEngine.Plan.cs, the cleanup).
const uishot = join(root, 'tools', 'uishot');
const fixtureFiles = [join(uishot, 'consent-drive.mjs'), ...['plan-fixture.mjs'].map((f) => join(uishot, 'fixtures', f))];
for (const file of fixtureFiles) {
  const text = readFileSync(file, 'utf8');
  assert.doesNotMatch(text, /model quality grade/i, `${file} still calls it the model quality grade, not the AI readiness grade`);
}
const consentDrive = readFileSync(join(uishot, 'consent-drive.mjs'), 'utf8');
const savePlan = consentDrive.slice(consentDrive.indexOf('const SAVE_PLAN'), consentDrive.indexOf('const RANKED_PLAN'));
assert.ok(savePlan.includes("'cleanup/model'"), 'the consent driver no longer has the optional cleanup this check reads');
assert.doesNotMatch(savePlan, /dim_date\[|FISCAL_|_DATE\b|\bDate\]/i,
  'the consent driver\'s optional cleanup offers a column of the date table, which the engine never offers');

// ---- copy rules ---------------------------------------------------------------------------------
for (const [name, text] of [['plan-model.ts', readFileSync(join(src, 'plan-model.ts'), 'utf8')], ['plan.tsx', plan], ['consent.tsx', consent]])
  assert.ok(!text.includes('—'), `${name} carries an em dash`);

console.log('plan-harm: ok');
