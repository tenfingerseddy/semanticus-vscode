import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';

// L1 (ra1 section 2): the Checks rows render only the engine's check state, newest version per measure. Item 5 (v-g1 "risk,
// not proven"): after a run stops on an edit, a row could keep its pre-edit marks because the read the edit started was dropped.
// Here the run's stopped results, the first read and the pushes arrive in every order; the row always ends on the newest state.
const elements = node => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(elements)];
const ref = 'measure:Contoso/Total Quantity';
const passed = name => ({ name, state: 'passed', sentence: 'Fine.', applies: true, verdict: 'verified', lastRan: '2026-10-28T06:44:00Z' });
const state = (version, extra = {}) => ({ measureRef: ref, version, lastChecked: '2026-10-28T06:44:00Z', running: false, marks: [passed('Filters'), passed('Contexts')], ...extra });
const before = state(3);                                                                   // the stopped run's last record: pre-edit ✓
const comparing = state(4, { running: true, marks: [passed('Filters'), passed('Contexts'),
  { name: 'Equivalence', state: 'not run', sentence: 'This check is waiting to run.', reasonCode: 'waiting', applies: true, pending: true, verdict: null }] });
const moved = state(5, { marks: [passed('Filters'), passed('Contexts'),
  { name: 'Equivalence', state: 'suspect', sentence: 'This change moved these numbers.', applies: true, verdict: 'review', lastRan: '2026-10-28T06:50:00Z',
    cells: [{ context: 'Grand total', before: '628370', after: '1256740' }] }] });

function mount() {
  const runtime = makeHookRuntime();
  let pushed; const reads = [];
  const { ChecksPage } = makeLoader({ './bridge': {
    rpc: method => method === 'getMeasureCheckCache' ? new Promise(resolve => reads.push(resolve)) : Promise.resolve(null),
    onDidChange: () => () => {}, runHostCommand() {}, onCheckState: fn => { pushed = fn; return () => {}; },
  } }, { ...req('react'), ...runtime.React })('./checks-page.tsx');
  const page = { sessionId: 'contoso', measures: [{ ref, name: 'Total Quantity' }], numbers: { definitions: [] }, results: {}, tables: { rows: [] }, summary: { total: 1 } };
  const view = runtime.renderHook(() => ChecksPage({ page, onRun() {}, onAdd() {}, onConnect() {}, onChanged() {} }));
  const group = () => elements(view.result).find(e => Array.isArray(e.props?.groups))?.props.groups.find(g => g.rows.some(r => r.ref === ref))?.group;
  const ran = results => elements(view.result).find(e => typeof e.props?.onRan === 'function').props.onRan(results);
  return { runtime, push: change => pushed(change), read: i => reads[i], ran, group };
}

for (const [label, steps] of [
  ['the stopped run answers after the edit started its compare, before it lands', ['first', 'comparing', 'stopped', 'moved']],
  ['the stopped run answers after the compare landed', ['first', 'comparing', 'moved', 'stopped']],
  ['the first read answers last, older than everything', ['comparing', 'moved', 'stopped', 'first']],
]) test(`item 5: a Checks row after a run stopped on an edit shows the edit's result: ${label}`, async () => {
  const page = mount();
  try {
    for (const step of steps) {
      if (step === 'first') page.read(0)([before]);
      if (step === 'comparing') page.push({ version: 4, states: [comparing] });
      if (step === 'stopped') page.ran([before]);
      if (step === 'moved') page.push({ version: 5, states: [moved] });
      await settle();
    }
    assert.equal(page.group(), 'review', 'the row reads ▲ Needs review: your edit moved numbers, never the pre-edit ✓');
  } finally { page.runtime.unmount(); }
});

test('the run line counts a measure done from the engine push and shows Stop while it runs', () => {
  const React = req('react');
  const { renderToStaticMarkup } = req('react-dom/server');
  const { PlanLine } = makeLoader({ './marks-host': { useMarksHost: () => ({}) } })('./run-plan.tsx');
  const running = renderToStaticMarkup(React.createElement(PlanLine, { sentence: '', running: true, done: 3, total: 12, onChoose() {}, onRun() {}, onStop() {} }));
  assert.match(running, /Checking 3 of 12\./);
  assert.match(running, /data-testid="plan-stop"[^>]*>Stop</);
  const rest = renderToStaticMarkup(React.createElement(PlanLine, { sentence: 'Checking 12 of 12: 4 core. About 4 minutes.', running: false, canRun: true, onChoose() {}, onRun() {}, onStop() {} }));
  assert.doesNotMatch(rest, />Stop</, 'Stop shows only while a run runs');
});

test('a tree row that is not checked yet is named for a screen reader by the measure first (rc27 tester gap 4)', () => {
  const m = makeLoader()('./marks-model.ts');
  assert.equal(m.treeRowItem('Total Quantity', []).label, 'Total Quantity, Not checked yet');
  assert.equal(m.treeRowItem('Total Quantity', [passed('Filters')]).label, 'Total Quantity, Verified by 1 check');
});

test('a number in a mark table never wraps; only the place cell does (rc27 tester gap 2, ra1 items 12 and 19)', async () => {
  const { readFileSync } = await import('node:fs');
  const css = readFileSync(new URL('../webview/src/marks.css', import.meta.url), 'utf8');
  assert.match(css, /\.mk-cells td:not\(:first-child\), \.mk-cells th:not\(:first-child\) \{ white-space: nowrap; overflow-wrap: normal; \}/);
  // fx29 (rc28 tester gap 4): the place breaks between words, never inside one, so "anywhere" is gone (test/fx29-card.test.mjs).
  assert.match(css, /\.mk-cells td:first-child, \.mk-cells th:first-child \{ width: 100%; overflow-wrap: break-word; word-break: normal; hyphens: none; \}/);
  assert.doesNotMatch(css, /\.mk-cells td, \.mk-cells th \{ overflow-wrap: anywhere; \}/, 'no rule lets a number break');
});

test('an Undo of a kept Description reaches the Properties card before the slower property reload (rc27 tester gap 5, G1 a)', async () => {
  const { readFileSync } = await import('node:fs');
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(ext, /async refresh\(\): Promise<void> \{ if \(this\.refs\.length\) \{ void this\.view\?\.webview\.postMessage\(\{ type: 'didChange' \}\); await this\.push\(\); \} \}/,
    'Properties is told of the change first');
  const grid = readFileSync(new URL('../webview/src/checked-grid.tsx', import.meta.url), 'utf8');
  assert.match(grid, /m\?\.type === 'didChange'\) \{[\s\S]*?changed\.forEach/, 'and the card reads its Description again at once');
});
