import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const text = node => typeof node === 'string' || typeof node === 'number' ? String(node) : Array.isArray(node) ? node.map(text).join('') : node && typeof node === 'object' ? text(node.props?.children) : '';
const find = (view, id) => elements(view.result).find(e => e.props?.['data-testid'] === id);

for (const [state, message] of [
  ['timedOut', 'Your assistant did not answer in time. No Description was changed. Request it again when your assistant is ready.'],
  ['declined', 'Your assistant declined this request. No Description was changed.'],
  ['unavailable', 'No assistant is connected to this session. Connect your assistant, then request a Description.'],
]) test(`gap 2: Properties shows the engine sentence when the Description is ${state}`, async () => {
  const runtime = makeHookRuntime();
  let changed;
  let proposal = null;
  const host = { onChange: fn => { changed = fn; return () => {}; }, rpc: async method => {
    if (method === 'draftMeasureDescription') return proposal = { id: 'contoso', state: 'waiting', message: 'Waiting for your assistant.' };
    if (method === 'getMeasureDescription') return { ref: 'measure:Contoso/Amount', description: '', proposal };
    throw new Error('Unexpected request: ' + method);
  } };
  const { DescriptionBlock } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  const view = runtime.renderHook(() => DescriptionBlock({ reference: 'measure:Contoso/Amount' }));
  try {
    await settle();
    elements(view.result).find(e => e.type === 'button' && text(e) === 'Draft a Description').props.onClick();
    await settle();
    assert.match(text(find(view, 'mk-desc-note')), /Waiting/);
    proposal = { ...proposal, state, message }; changed(); await settle();
    assert.equal(text(find(view, 'mk-desc-note')), message);
    assert.equal(elements(view.result).find(e => e.type === 'button' && text(e) === 'Draft a Description').props.disabled, false);
  } finally { runtime.unmount(); }
});

test('gap 5a: the first plan shows its rough time and keeps the first-run disclosure', () => {
  const { planSummary } = makeLoader()('./marks-model.ts');
  const plan = { totalMeasures: 12, estimatedSeconds: 228, firstRun: true,
    picks: Array.from({ length: 12 }, (_, i) => ({ measureRef: 'measure:Contoso/Amount ' + i, group: 'selected' })) };
  // d1 (fx23) 4.3: the first-run tail is cut (section 5 cut 42); the rough time stays.
  assert.equal(planSummary(plan, false).sentence, 'Checking 12 of 12: 12 ticked. About 4 minutes.');
  assert.match(planSummary({ ...plan, firstRun: false, estimatedSeconds: 120 }, false).sentence, /About 2 minutes/);
});

test('gap 4: a late cache reply cannot replace the newer single-measure result', async () => {
  const runtime = makeHookRuntime();
  let pushed;
  const reads = [];
  const { ChecksPage } = makeLoader({ './bridge': {
    rpc: () => new Promise(resolve => reads.push(resolve)), onDidChange: () => () => {}, runHostCommand() {},
    onCheckState: fn => { pushed = fn; return () => {}; },
  } }, { ...req('react'), ...runtime.React })('./checks-page.tsx');
  const page = { sessionId: 'contoso', measures: [{ ref: 'measure:Contoso/Amount', name: 'Amount' }],
    numbers: { definitions: [] }, results: {}, tables: { rows: [] }, summary: { total: 1 } };
  const view = runtime.renderHook(() => ChecksPage({ page, onRun() {}, onAdd() {}, onConnect() {}, onChanged() {} }));
  // d1 (fx23): the row is a name and a glyph inside its verdict group ("2 passed, 5 not run" is cut, section 5 cut 14).
  const groupOf = () => elements(view.result).find(e => Array.isArray(e.props?.groups))?.props.groups.find(g => g.rows.some(r => r.ref === 'measure:Contoso/Amount'))?.group;
  try {
    // The engine pushes the newer state (version 5) before the first read answers with the older one (version 3).
    pushed({ version: 5, states: [{ measureRef: 'measure:Contoso/Amount', version: 5, lastChecked: new Date().toISOString(),
      marks: [{ name: 'Filters', state: 'passed' }, { name: 'Contexts', state: 'passed' }] }] });
    await settle(); assert.equal(groupOf(), 'verified');
    reads[0]([{ measureRef: 'measure:Contoso/Amount', version: 3, marks: [] }]); await settle();
    assert.equal(groupOf(), 'verified', 'the late reply is ignored');
  } finally { runtime.unmount(); }
});

test('gap 5b: a new Equivalence result clears the previous Undo note', () => {
  const runtime = makeHookRuntime();
  const host = { undo() {}, rpc: async () => null };
  const { MarkCard } = makeLoader({ './marks-host': { useMarksHost: () => host } }, runtime.React)('./marks-panel.tsx');
  let mark = { name: 'Equivalence', state: 'suspect', sentence: 'This change moved these numbers.', lastRan: '2026-10-27T05:20:00Z' };
  const action = () => elements(MarkCard({ reference: 'measure:Contoso/Amount', mark, others: [], onClose() {}, onRecheck() {} }))
    .find(e => typeof e.type === 'function' && e.type.name === 'MarkAction');
  const view = runtime.renderHook(() => { const e = action(); return e.type(e.props); });
  try {
    elements(view.result).find(e => e.type === 'button' && text(e) === 'Undo my edit').props.onClick();
    assert.match(text(view.result), /Undo sent/);
    mark = { ...mark, state: 'not run', sentence: 'This check is waiting to run.' }; view.rerender();
    assert.doesNotMatch(text(view.result), /Undo sent/);
    mark = { ...mark, state: 'suspect', lastRan: '2026-10-27T05:21:00Z' }; view.rerender();
    assert.doesNotMatch(text(view.result), /Undo sent/);
  } finally { runtime.unmount(); }
});

test('gap 5d: each Checks row has a sign in its leading mark slot, including an unchecked helper', async () => {
  // d1 (fx23) 4.2: every row keeps its leading sign slot; a measure that is not checked yet has an empty slot (R6), and the
  // row is the slot, the name and the chevron (the reason column is cut, section 5 cut 38).
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { ChecksPage } = makeLoader({ './bridge': { rpc: async () => [], onDidChange: () => () => {}, runHostCommand() {} } })('./checks-page.tsx');
  const page = { sessionId: 'contoso', measures: [{ ref: 'measure:Contoso/ShowValueForDates', name: 'ShowValueForDates' }],
    numbers: { definitions: [{ id: 'contoso', title: 'Contoso number', enabled: true }] }, results: {}, tables: { rows: [] }, summary: { total: 1, numbersDiffer: 0, keyProblems: 0, tableProblems: 0 } };
  const html = renderToStaticMarkup(React.createElement(ChecksPage, { page, onRun() {}, onAdd() {}, onConnect() {}, onChanged() {} }));
  const rows = [...html.matchAll(/<button type="button" class="cp-open"[^>]*>(.*?)<\/button>/g)].map(x => x[1]);
  assert.equal(rows.length, 4);
  for (const row of rows) {
    const cells = [...row.matchAll(/<span class="([^"]+)"[^>]*>(.*?)<\/span>/g)];
    assert.match(cells[0][1], /^cp-glyph /, 'the sign slot is the first column of every row');
    assert.match(cells[0][2], /^[✓✗▲○⊘]?$/);
    assert.equal(cells.length, 3, 'The row uses three columns.');
  }
  assert.match(rows[0], /<span class="cp-glyph cp-s-none" aria-hidden="true"><\/span><span class="cp-name">ShowValueForDates/, 'not checked yet: no glyph');
});
