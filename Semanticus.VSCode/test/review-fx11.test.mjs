import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

const model = makeLoader()('./marks-model.ts');
const calm = 'Not scored yet. This score turns on when Jev scoring is released.';
const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const text = node => typeof node === 'string' || typeof node === 'number' ? String(node) : Array.isArray(node) ? node.map(text).join('') : node && typeof node === 'object' ? text(node.props?.children) : '';

test('gap 3: cached Totals and Description failures show one release sentence', () => {
  for (const reason of ['The scoring service could not be reached.', 'The scoring service does not have this check yet.',
    'The scoring service is unavailable right now.']) {
    // d1 (fx23): a service-side cause hides the Totals mark and the score; no release sentence is shown (section 5 cuts 1 and 2).
    const mark = { name: 'Totals', state: 'not run', sentence: 'Not scored yet. ' + reason };
    assert.equal(model.verdictOf(mark, true), null);
    const match = { score: null, level: 'not scored yet', reason };
    assert.equal(model.descriptionScore(match).label, '');
    assert.equal(model.actionableReason(reason), null, 'a service-side cause shows nothing');
    assert.ok(!model.treeRowItem('Contoso', [mark], { match }).tooltip.includes(calm));
  }
});

test('a cause the person can act on keeps its own line on Totals and the Description score', () => {
  for (const reason of ['Scoring is off for this model.', 'Scoring needs a Pro plan.', 'The scoring allowance is used up for now.']) {
    // d1 (fx23) section 3: the cause keeps its one line, under the Checks verdict line and on the card, never on a row.
    assert.equal(model.actionableReason('Not scored yet. ' + reason), reason);
    assert.equal(model.actionableReason(reason), reason);
  }
});

test('gap 5: the webview keeps the first-run disclosure', () => {
  const summary = model.planSummary({ picks: [{ measureRef: 'measure:Contoso/Amount', group: 'selected' }],
    totalMeasures: 12, estimatedSeconds: 4, firstRun: true }, false);
  // d1 (fx23) 4.3: the first-run tail is cut (section 5 cut 42); the rough time stays.
  assert.equal(summary.sentence, 'Checking 1 of 12: 1 ticked. About 4 seconds.');
});

test('gap 7a: the context reason appears once when it equals the expected behaviour', () => {
  const runtime = makeHookRuntime();
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => ({ rpc: async () => null }) } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => MarkCard({ reference: 'measure:Contoso/Amount', onClose() {}, mark: {
    name: 'Contexts', state: 'passed', sentence: 'Contoso checked.', chosenContexts: [
      { id: 'contoso', column: { table: 'Contoso', name: 'Category' }, reason: 'Each Contoso category.', expectedBehaviour: 'Each Contoso category.' },
    ],
  } }));
  // d1 (fx23) 4.4: the places sit behind "Where it looked (n)"; opened, each reason reads once.
  try {
    assert.equal(text(view.result).split('Each Contoso category.').length - 1, 0, 'behind a click');
    elements(view.result).find(e => e.props?.looked).props.looked.toggle();
    assert.equal(text(view.result).split('Each Contoso category.').length - 1, 1);
  }
  finally { runtime.unmount(); }
});

test('gap 7c: choosing two measures updates the visible plan and result count', async () => {
  const runtime = makeHookRuntime();
  const measures = Array.from({ length: 12 }, (_, i) => ({ ref: 'measure:Contoso/Amount ' + i, name: 'Amount ' + i, table: 'Contoso' }));
  const plans = new Map();
  const host = { onChange: () => () => {}, rpc: async (method, arg) => {
    if (method === 'planMeasureRun') {
      const refs = arg?.selection?.measures ?? measures.map(m => m.ref);
      const p = { id: 'contoso-' + refs.length, totalMeasures: 12, estimatedSeconds: 4 * refs.length,
        picks: refs.map(measureRef => ({ measureRef, group: 'selected', reason: 'Selected.' })) };
      plans.set(p.id, p); return p;
    }
    if (method === 'runMeasurePlan') return { results: plans.get(arg).picks.map(p => ({ measureRef: p.measureRef, marks: [] })) };
  } };
  const { RunPlan } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./run-plan.tsx');
  const view = runtime.renderHook(() => RunPlan({ measures }));
  const button = label => elements(view.result).find(e => e.type === 'button' && text(e) === label);
  const find = id => elements(view.result).find(e => e.props?.['data-testid'] === id);
  try {
    // d1 (fx23) 4.3: Choose holds Pick your own; a finished run adds no note (the Checks verdict line changes).
    const line = () => elements(view.result).find(e => typeof e.props?.onChoose === 'function');
    await settle(); line().props.onChoose();
    elements(view.result).find(e => typeof e.props?.onPick === 'function').props.onPick();
    elements(view.result).find(e => e.props?.['aria-label'] === 'Open Contoso').props.onClick();
    for (const m of measures.slice(0, 2)) find('tick-' + m.ref).props.onChange();
    await settle(); button('Check selected (2)').props.onClick(); await settle();
    assert.match(line().props.sentence, /Checking 2 of 12/);
    assert.equal(find('plan-note'), undefined);
  } finally { runtime.unmount(); }
});

test('gap 7d: opening DAX uses the VS Code preview slot', async () => {
  const source = ext.slice(ext.indexOf('async function openDaxAt('), ext.indexOf('async function selectTreeObject('));
  const ts = req('typescript');
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  const shown = [];
  const vscode = { window: { tabGroups: { all: [] }, showTextDocument: async (doc, options) => shown.push(options) },
    workspace: { openTextDocument: async uri => ({ uri }) }, languages: { setTextDocumentLanguage: async () => {} } };
  const open = new Function('vscode', 'resolveDaxEditorUri', 'headerUriModelKey', 'pickOpenDaxHref', 'DAX_SCHEME', 'validateDaxDoc', js + '; return openDaxAt;')(
    vscode, async uri => uri, () => 'Contoso', () => null, 'semanticus', () => {});
  await open({ path: '/Contoso/Amount.dax' }, 'measure:Contoso/Amount');
  await open({ path: '/Contoso/Cost.dax' }, 'measure:Contoso/Cost');
  assert.deepEqual(shown, [{ preview: true }, { preview: true }]);
});

test('gap 6: the tree publishes restored Descriptions while its mark cache is still loading', async () => {
  const source = ext.slice(ext.indexOf('    /** The checks/didChange push. */'), ext.indexOf('    /// Tell the tree what the open model is.'));
  const ts = req('typescript');
  const js = ts.transpileModule('class Tree {' + source + '}', { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  const Tree = new Function('acceptStates', js + '; return Tree;')(model.acceptStates);
  const tree = new Tree();
  let release;
  const cache = new Promise(resolve => { release = resolve; });
  const calls = [];
  tree.marksRead = 0; tree.checkStates = new Map(); tree.descriptions = new Map(); tree._emitter = { fire() {} };
  tree.connection = { sendRequest: method => { calls.push(method); return method === 'getMeasureCheckCache' ? cache : Promise.resolve({
    measures: [{ ref: 'measure:Contoso/Amount', oneLine: 'Contoso restored.' }],
  }); } };
  const pending = tree.readMarks(true);
  try {
    await settle();
    assert.equal(tree.descriptions.get('measure:Contoso/Amount')?.oneLine, 'Contoso restored.');
    assert.equal(calls[0], 'getChecksPage');
  } finally { release([]); await pending; }
});
