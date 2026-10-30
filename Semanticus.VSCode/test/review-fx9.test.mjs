import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

const load = makeLoader(), model = load('./marks-model.ts');
const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
const mark = (name, state) => ({ name, state, sentence: 'Contoso check.' });
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const find = (view, id) => elements(view.result).find(e => e.props?.['data-testid'] === id);
const button = (view, label) => elements(view.result).find(e => e.type === 'button' && e.props.children === label);

// d1 (fx23) 4.1: the row is the name and one glyph (no "–" before a run, no "Checked", no Description on the row).
test('gap 3: every calm measure has one mark before its Description, even before cache arrival', () => {
  for (const [name, marks, glyph] of [
    ['Total Quantity', [], ''],
    ['Total Cost', model.MARK_ORDER.map(n => mark(n, 'passed')), '✓'],
    ['Gross Profit', [mark('Filters', 'info')], '▲'],
  ]) {
    const row = model.treeRowItem(name, marks, { oneLine: 'Contoso sales.' });
    assert.equal(row.description, glyph);
    assert.ok(row.mark, 'the row names the card to open');
  }
  const row = model.treeRowItem('Total Quantity', [mark('Filters', 'passed'), mark('Contexts', 'passed')]);
  assert.equal(row.description, '✓', 'the marks that apply passed');
  const tree = ext.slice(ext.indexOf('class ModelTreeProvider'), ext.indexOf('async getChildren(', ext.indexOf('class ModelTreeProvider')));
  assert.doesNotMatch(tree, /if \(data\) \{ const row = treeRowItem/, 'a missing cache cannot hide the mark');
});

test('gap 3: pressing a tree row sends its primary card to Properties', async () => {
  const ts = req('typescript');
  const source = ext.slice(ext.indexOf('async function selectTreeObject('), ext.indexOf('async function askFormulaAssistant('));
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  const shown = [], focused = [];
  const select = new Function('propGrid', 'DAX_KINDS', 'openDax', js + ';return selectTreeObject;')(
    { showObjects: async (...args) => shown.push(args), showMark: (...args) => focused.push(args) }, new Set(), async () => {});
  const node = { ref: 'measure:Sales/Total Quantity', name: 'Total Quantity', kind: 'measure' };
  await select(node, 'Totals');
  assert.equal(shown[0][0][0], node);
  assert.deepEqual(focused, [[node.ref, 'Totals']]);
});

// d1 (fx23) 4.4: the detail has no Close; its mark button opens and closes it.
test('gap 3: Properties opens a requested card once; a new press reopens it after Close', async () => {
  const runtime = makeHookRuntime(), listeners = new Set();
  const totals = { ...mark('Totals', 'passed'), lastRan: new Date().toISOString(), total: { rule: 'sum_of_rows', type: 'sum', confidence: .9, expected: false } };
  const host = { rpc: async method => method === 'getMeasureMarks' ? { marks: [totals], revision: 1 } : null, onChange: fn => { listeners.add(fn); return () => listeners.delete(fn); } };
  const { MeasureMarksPanel } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let request = { name: 'Totals', id: 1 };
  const view = runtime.renderHook(() => MeasureMarksPanel({ reference: 'measure:Sales/Gross Profit', requestedMark: request }));
  const face = () => elements(view.result).find(e => typeof e.props?.onMark === 'function');
  const card = () => face()?.props.detail?.props?.mark?.name === 'Totals' ? face().props.detail : undefined;
  try {
    await settle(); assert.ok(card());
    face().props.onMark('Totals'); assert.equal(card(), undefined);
    listeners.forEach(fn => fn()); await settle(); assert.equal(card(), undefined);
    request = { name: 'Totals', id: 2 }; view.rerender(); assert.ok(card());
  } finally { runtime.unmount(); }
});

test('gap 8a: an oldest-check reason uses local words and keeps non-date reasons', () => {
  const oldTZ = process.env.TZ;
  try {
    process.env.TZ = 'Australia/Brisbane';
    assert.equal(model.runReason('Oldest check: 2026-10-27 01:02:04Z.', new Date('2026-10-27T02:00:00Z')),
      'Last checked today at 11:02.');
    assert.equal(model.runReason('Never checked.'), 'Never checked.');
    assert.equal(model.runReason('Oldest check: invalid.'), 'Not checked yet.');
  } finally { if (oldTZ === undefined) delete process.env.TZ; else process.env.TZ = oldTZ; }
});

test('gap 8b: Checks has one planned Run and no header Run checks', () => {
  const runtime = makeHookRuntime();
  const { ChecksPage } = makeLoader({
    './bridge': { rpc: async () => [], onDidChange: () => () => {}, onCheckState: () => () => {} },
    './marks-host': { ...load('./marks-host.ts'), MarksHostContext: { Provider: 'provider' } },
    './marks-panel': {}, './run-plan': { RunPlan: 'run-plan' },
  }, runtime.React)('./checks-page.tsx');
  const page = { sessionId: 'Contoso', summary: { total: 0 }, measures: [], numbers: { definitions: [] }, results: {}, tables: { rows: [] } };
  const view = runtime.renderHook(() => ChecksPage({ page }));
  assert.equal(elements(view.result).filter(e => e.type === 'run-plan').length, 1);
  assert.equal(button(view, 'Run checks'), undefined);
  runtime.unmount();
});

function descriptionHarness() {
  const runtime = makeHookRuntime(), listeners = new Set();
  let data = { ref: 'measure:Sales/Total Quantity', description: '', provenance: 'person', proposal: null };
  const host = {
    rpc: async method => {
      if (method === 'getMeasureDescription') return structuredClone(data);
      if (method === 'draftMeasureDescription') return data.proposal = { id: 'Contoso-draft', state: 'waiting', message: 'Waiting for your assistant. The wait ends after 60 seconds.' };
      if (method === 'keepMeasureDescription') { data.description = data.proposal.text; data.proposal = null; }
    },
    onChange: fn => { listeners.add(fn); return () => listeners.delete(fn); },
  };
  const { DescriptionBlock } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => DescriptionBlock({ reference: data.ref }));
  return { runtime, view, change: async patch => { Object.assign(data, patch); listeners.forEach(fn => fn()); await settle(); },
    ready: async () => { await button(view, 'Draft a Description').props.onClick(); await settle();
      data.proposal = { ...data.proposal, state: 'ready', text: 'Adds Contoso quantities.', message: 'The draft is ready.' };
      listeners.forEach(fn => fn()); await settle(); } };
}

test('gap 8c: a waiting notice ends when the assistant draft arrives', async () => {
  const h = descriptionHarness();
  try {
    await settle(); await h.ready();
    assert.ok(find(h.view, 'mk-proposal'));
    assert.doesNotMatch(find(h.view, 'mk-desc-note')?.props.children ?? '', /Waiting|60 seconds/);
  } finally { h.runtime.unmount(); }
});

test('gap 8d: Undo removes the kept notice and a cleared proposal stays cleared', async () => {
  const h = descriptionHarness();
  try {
    await settle(); await h.ready();
    await button(h.view, 'Keep this one').props.onClick(); await settle();
    assert.equal(find(h.view, 'mk-desc-note').props.children, 'Kept. Undo takes it back.');
    await h.change({ description: '', proposal: null });
    assert.equal(find(h.view, 'mk-desc-text'), undefined);
    assert.equal(find(h.view, 'mk-desc-note'), undefined);
    assert.equal(find(h.view, 'mk-proposal'), undefined);
  } finally { h.runtime.unmount(); }
});

test('gap 8f: a lazy restored Studio tab closes before auto-open creates one, even with simultaneous opens', async () => {
  const ts = req('typescript');
  const source = ext.slice(ext.indexOf('function openStudio('), ext.indexOf('// Studio opens by itself,', ext.indexOf('function openStudio(')));
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  class TabInputWebview { constructor(viewType) { this.viewType = viewType; } }
  for (const type of ['semanticusStudio', 'mainThreadWebview-semanticusStudio']) {
    const tabs = [{ input: new TabInputWebview(type) }, { input: new TabInputWebview('other') }];
    let created = 0;
    const vscode = { TabInputWebview, ViewColumn: { Active: 1 }, Uri: { joinPath: () => '' }, window: {
      tabGroups: { all: [{ tabs }], close: async old => { await settle(); for (const tab of old) tabs.splice(tabs.indexOf(tab), 1); return true; } },
      createWebviewPanel: () => { created++; tabs.push({ input: new TabInputWebview('semanticusStudio') }); return {
        webview: { onDidReceiveMessage() {} }, onDidDispose() {}, onDidChangeViewState() {}, reveal() {},
      }; },
    } };
    const open = new Function('vscode', 'pageScoped', 'studioRelayHandler', 'studioHtml', 'noteStudioPersonInput',
      'let studioPanel, studioOpening, studioReady; ' + js + ';return openStudio;')(vscode, x => x, () => {}, () => '', () => {});
    await Promise.all([open({}), open({})]);
    assert.equal(created, 1);
    assert.equal(tabs.filter(t => t.input.viewType.includes('semanticusStudio')).length, 1);
    assert.ok(tabs.some(t => t.input.viewType === 'other'));
  }
});

// d1 (fx23): scoring that is not served is hidden on Totals and the Description (section 5 cuts 1 to 3).
test('gap 8g: unreleased scoring reads as not available yet, across Totals and Description', () => {
  const reason = 'The scoring service is unavailable right now.';
  const totals = { name: 'Totals', state: 'not run', sentence: 'Not scored yet. ' + reason };
  const score = model.descriptionScore({ score: null, level: 'not scored yet', reason });
  assert.equal(model.verdictOf(totals, true), null);
  assert.equal(score.label, '');
  assert.equal(score.band, null);
});

test('gap 8f: reloading with two existing Studio tabs keeps the adopted active tab', async () => {
  const ts = req('typescript');
  const source = ext.slice(ext.indexOf('function openStudio('), ext.indexOf('// Studio opens by itself,', ext.indexOf('function openStudio(')));
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  class TabInputWebview { constructor(viewType) { this.viewType = viewType; } }
  const active = { input: new TabInputWebview('semanticusStudio') };
  const unrelated = { input: new TabInputWebview('other') };
  const tabs = [active, { input: new TabInputWebview('mainThreadWebview-semanticusStudio') }, unrelated];
  const vscode = { TabInputWebview, Uri: { joinPath: () => '' }, window: { tabGroups: {
    all: [{ viewColumn: 1, activeTab: active, tabs }],
    close: async old => { for (const tab of old) tabs.splice(tabs.indexOf(tab), 1); return true; },
  } } };
  const adopt = new Function('vscode', 'pageScoped', 'studioRelayHandler', 'studioHtml', 'noteStudioPersonInput',
    'let studioPanel, studioOpening, studioReady; ' + js + ';return adoptStudioPanel;')(vscode, x => x, () => {}, () => '', () => {});
  adopt({ visible: true, viewColumn: 1, webview: { onDidReceiveMessage() {} }, onDidDispose() {}, onDidChangeViewState() {} }, {});
  await settle();
  assert.deepEqual(tabs, [active, unrelated]);
});
