import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { makeLoader, req, makeHookRuntime, settle } from './helpers/webview-load.mjs';

const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const source = (path) => readFileSync(new URL('../' + path, import.meta.url), 'utf8');

test('H5 consent waits for first run and the tour before claiming an ask', async () => {
  let closeFirst;
  let tour = true;
  let claimed = 0;
  const rt = makeHookRuntime();
  const load = makeLoader({
    './bridge': { onDidChange: () => () => {}, rpc: async () => ({ current: false, appliesTo: 'sales' }), claimConsentAsk: async () => { claimed++; return true; } },
    './pro': { useTier: () => 'pro' },
    './firstrun-gate': { whenFirstRunClosed: () => new Promise((r) => { closeFirst = r; }) },
    './studio/learn-state': { useLearn: () => ({ runtime: { phase: tour ? 'running' : 'idle' } }) },
  }, rt.React);
  const C = load('consent');
  const hook = rt.renderHook(() => C.useConsentCard(true));
  await settle();
  assert.equal(claimed, 0, 'first-run choice must remain visible');
  closeFirst?.(); await settle();
  assert.equal(claimed, 0, 'tour must remain visible');
  tour = false; hook.rerender(); await settle(); closeFirst?.(); await settle();
  assert.equal(claimed, 1, 'the consent card can ask after both close');
  rt.unmount();
});

test('H5 driver names the tour even without a connected assistant', () => {
  const load = makeLoader({ './studio/learn-state': { useLearn: () => ({ runtime: { phase: 'running' } }) } });
  const { DriverBarView } = load('studio/driver-bar');
  const html = renderToStaticMarkup(React.createElement(DriverBarView, {
    driver: { assistantConnected: false, mode: 'claudeDrives', state: 'claudeHasWheel' }, touring: true, onMode() {}, onResume() {},
  }));
  assert.match(html, /The tour is playing/);
  assert.doesNotMatch(html, /is working/);
});

test('H5 leaving a tour explicitly opens Plan Work', () => {
  assert.match(source('src/learnHost.ts'), /type: 'navigate', tab: 'plan', target: 'work'/);
});

test('H9 a result closes What I did and hides the connection prompt', () => {
  const s = source('webview/src/daxlab.tsx');
  assert.match(s, /if \(res && !res.error\) notesStore.setListOpen\(false\)/);
  assert.match(s, /!res && <div className="shrink-0"><ConnectBar/);
});

test('H9 ratios only receive the measure format when it exists', () => {
  const { withMeasureFormats } = makeLoader()('resultformats');
  const r = { columns: [{ name: '[Share]', dataType: 'Double' }], rows: [[0.06409]] };
  const q = 'EVALUATE ROW("Share", [Share])';
  assert.equal(withMeasureFormats(r, q, [{ name: 'Share' }]), r);
  assert.equal(withMeasureFormats(r, q, [{ name: 'Share', formatString: '0.00%' }]).columns[0].format, '0.00%');
});

test('H9 finishing queued notes cannot reopen the panel over a new result', () => {
  const { createNotesStore } = makeLoader()('studio/notes-store');
  let timer;
  const store = createNotesStore({
    driver: { canMoveScreen: () => true, snapshot: () => ({ resumeVersion: 0 }), subscribe: () => () => {} },
    moveTo() {}, setTimer: (fn) => { timer = fn; return 1; }, clearTimer() {},
  });
  for (let i = 0; i < 3; i++) store.push({ id: String(i), say: 'A step', place: 'explore/queries', by: 'assistant', sessionId: 's' });
  store.setListOpen(false);
  for (let i = 0; i < 3; i++) timer();
  assert.equal(store.getSnapshot().listOpen, false);
});

test('H10 approval says who approved on both notes', () => {
  const M = makeLoader()('startplan-model');
  const v = { exists: true, digest: 'd', approval: { digest: 'd', by: 'agent' } };
  assert.match(M.approvalOf(v).text, /Your assistant approved this plan/);
  assert.match(M.footOf(v, { pro: true }).lead, /Your assistant approved this plan/);
});

test('H10 the plan strip does not give a second waiting count', () => {
  assert.doesNotMatch(source('webview/src/startplan.tsx'), /wait on you/);
});

test('H11 programmatic tree selections cannot filter the Plan', () => {
  assert.match(source('src/extension.ts'), /type: 'treeSelection', ref, sessionId: currentSessionId, by: lastTreeSelectionBy/);
  // N10: the tree's own redraw is not the person either, so `by` comes from the guarded decision, not from the reveal depth alone.
  assert.match(source('src/extension.ts'), /const byPerson = treeSelectionGuard\.classify\(\[\.\.\.e\.selection\]\.map\(\(n\) => n\.ref\)\) === 'person' && studioTreeRevealDepth === 0/);
  assert.match(source('webview/src/App.tsx'), /if \(selection.by === 'assistant'\) return/);
  assert.match(source('webview/src/bridge.ts'), /by: msg.by === 'assistant' \? 'assistant' : 'human'/);
});

test('H11 a plan note can select Properties without filtering Plan tasks', () => {
  const moved = [], selected = [];
  const { moveScreenTo } = makeLoader({
    '../bridge': { dispatchNavigate: (m) => moved.push(m), revealInTree: (ref) => selected.push(ref) },
    './places.json': { places: [{ id: 'plan', tool: 'plan' }] },
  })('studio/notes-nav');
  moveScreenTo({ place: 'plan', focus: 'table:Sales', by: 'assistant', sessionId: 's' });
  assert.equal(moved[0].target, undefined);
  assert.deepEqual(selected, ['table:Sales']);
});

test('H15 transient session read failures retain the model header', () => {
  assert.match(source('webview/src/connection.tsx'), /if \(s\) setSession\(s\)/);
});

test('H15 an owner keeps dirty work so no unsaved tab is needed', () => {
  const program = readFileSync(new URL('../../Semanticus.Engine/Program.cs', import.meta.url), 'utf8');
  assert.doesNotMatch(program, /holdOpen: assistantOwner \?/);
  assert.match(source('src/statusBarText.ts'), /!info.keepsUnsavedWork/);
});
