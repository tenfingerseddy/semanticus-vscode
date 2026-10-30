import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, makeHookRuntime, req, settle } from './helpers/webview-load.mjs';

const { renderToStaticMarkup } = req('react-dom/server');
const page = {
  measures: [{ ref: 'measure:Sales/Sales Amount', name: 'Sales Amount', description: 'Contoso sales after discounts.',
    checked: { verdict: 'checked', meaningWords: 'Old words must stay hidden.' } }],
  summary: { total: 1, numbersDiffer: 0, keyProblems: 0, tableProblems: 0 },
  numbers: { definitions: [] }, results: {}, tables: { rows: [] },
};
const text = node => node == null || typeof node === 'boolean' ? '' : typeof node !== 'object' ? String(node)
  : Array.isArray(node) ? node.map(text).join('') : text(node.props?.children);
// The marks lane put the marks panel and the run plan in the page. They have their own tests (marks-ui.test.mjs and
// tools/uishot/marks-drive.mjs); here they are stand-ins so this test reads only the page around them. The marks host
// is the real module (its Description drafts, NT1-03) with a plain Provider; the engine's check state push is quiet.
const marksStubs = (rpc) => ({
  './bridge': { rpc, onDidChange: () => () => {}, onCheckState: () => () => {}, runHostCommand() {} },
  './marks-host': { ...makeLoader()('./marks-host.ts'), MarksHostContext: { Provider: ({ children }) => children } },
  './marks-panel': { MeasureMarksPanel: ({ reference }) => `[marks for ${reference}]`, MarkGlyph: ({ glyph }) => glyph },
  './run-plan': { RunPlan: () => '[run plan]' },
});
// The d1 page (fx23, SD-218) draws its header, groups and rows as components. They hold no hooks, so the walk draws them in place.
const elements = node => !node || typeof node !== 'object' ? [] : Array.isArray(node) ? node.flatMap(elements)
  : typeof node.type === 'function' ? [node, ...elements(node.type(node.props))] : [node, ...elements(node.props?.children)];
// Studio's Tests tab puts the cleanup in the page's More menu (tests.tsx); this stand-in is that menu item, pressed the same way.
const more = cleanup => req('react').createElement('button', { type: 'button', disabled: cleanup.disabled, onClick: cleanup.run }, 'Remove old annotations');

test('the retained page opens a Description card without the old meaning verdict', () => {
  const runtime = makeHookRuntime();
  const { ChecksPage } = makeLoader(marksStubs(async method => { if (method === 'getMeasureCheckCache') return []; throw new Error('Unexpected request'); }), runtime.React)('./checks-page.tsx');
  const hook = runtime.renderHook(() => ChecksPage({ page, onRun() {}, onAdd() {}, onConnect() {}, onChanged() {} }));
  let markup = renderToStaticMarkup(hook.result);
  assert.match(markup, /Sales Amount/);
  assert.doesNotMatch(markup, /Old words|Write its meaning|>Checked<|Partly checked/);
  const measure = elements(hook.result).find(n => n.props?.['data-testid'] === 'checks-row-measure:Sales/Sales Amount');
  const row = elements(measure).find(n => n.type === 'button' && n.props['aria-expanded'] === false);
  row.props.onClick();
  markup = renderToStaticMarkup(hook.result);
  assert.match(markup, /\[marks for measure:Sales\/Sales Amount\]/, 'the card holds the Description and the seven marks');
  assert.match(markup, /Add a number you trust/);
});

test('one press cleans the annotations and shows the undo notice', async () => {
  const runtime = makeHookRuntime(), calls = [];
  let changed = 0;
  const { ChecksPage } = makeLoader(marksStubs(async (...args) => {
    if (args[0] === 'getMeasureCheckCache') return [];
    calls.push(args); return { note: 'Removed 2 old annotations. Undo restores them. Description stays the same.' };
  }), runtime.React)('./checks-page.tsx');
  const hook = runtime.renderHook(() => ChecksPage({ page, more, onRun() {}, onAdd() {}, onConnect() {}, onChanged() { changed++; } }));
  assert.match(readFileSync(new URL('../webview/src/tests.tsx', import.meta.url), 'utf8'),
    /more=\{cleanup => [\s\S]*?onClick=\{\(\) => \{ close\(\); cleanup\.run\(\); \}\}>Remove old annotations</, 'Studio\'s More menu presses the page\'s cleanup');
  elements(hook.result).find(n => n.type === 'button' && text(n) === 'Remove old annotations').props.onClick();
  await settle();
  assert.deepEqual(calls, [['cleanupMeaningAnnotations']]);
  assert.equal(changed, 1);
  assert.match(renderToStaticMarkup(hook.result), /Undo restores them/);
});

test('formula repairs keep the assistant task contract after removing the old card adapter', async () => {
  const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  const start = source.indexOf('async function askFormulaAssistant(');
  const end = source.indexOf('async function editDaxCmd(', start);
  const js = req('typescript').transpileModule(source.slice(start, end), { compilerOptions: { target: 9 } }).outputText;
  const calls = [];
  const ask = new Function('conn', 'refParts', js + '; return askFormulaAssistant;')(
    { sendRequest: async (...args) => calls.push(args) }, () => ({ name: 'Contoso Sales' }));
  await ask('Fix the Contoso formula.', 'measure:Sales/Contoso Sales', 'save-formula/7/fix');
  assert.deepEqual(calls, [['addPlanTask', {
    words: 'Fix Contoso Sales.', brief: 'Fix the Contoso formula.', objects: ['measure:Sales/Contoso Sales'],
    doer: 'assistant', sourcePage: 'plan', sendKey: 'formula/measure:Sales/Contoso Sales/save-formula/7/fix',
  }]]);
});
