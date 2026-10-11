// Verifier check of m11/drafts-sol (Sol's draft findings F1-F15), 5 Oct. The two rules across ALL editors, not only the ones the lane fixed:
//   R-A  a write a draft produces is refused when another model is open (the builder's join item 3: the click-only editors carry no identity).
//   R-B  a draft ends only when the engine accepted exactly that text.
// The editor here is AI instructions, one of the useEditBase editors the lane left as it was. Its Save is two calls (readNow, then the write), and its
// textarea stays editable while the Save is on its way. The fake engine mirrors LocalEngine: setAiInstructions has no identity to fence on, so it writes
// into whatever model is open (DraftsSolVerifyTests.cs proves that half against the real engine).
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, makeHookRuntime, settle, ambientReact } from './helpers/webview-load.mjs';

function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
const area = (tree) => find(tree, (el) => el.type === 'textarea');
const button = (tree, label) => find(tree, (el) => el.type === 'button' && [].concat(el.props?.children).join('') === label);
const type = (el, value) => el.props.onChange({ target: { value } });
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
globalThis.window ??= globalThis;

function instructionsEngine() {
  const e = { current: 'file:/m/A.bim', text: { 'file:/m/A.bim': '', 'file:/m/B.bim': '' }, readHold: null, writeHold: null, calls: [] };
  e.rpc = async (method, ...a) => {
    e.calls.push([method, ...a]);
    if (method === 'getAiInstructions') { if (e.readHold) await e.readHold.promise; return { present: true, instructions: e.text[e.current], limit: 2000 }; }
    if (method === 'setAiInstructions') {
      if (e.writeHold) await e.writeHold.promise;
      if (a[3] != null && a[3] !== e.text[e.current]) throw new Error('The instructions changed in the model after you opened them, so your version was not saved. Nothing was written.');
      e.text[e.current] = a[0]; return { changed: true };
    }
    if (method === 'listPrimerSuggestions') return { suggestions: [] };
    return undefined;
  };
  return e;
}

function world(e) {
  const persisted = {};
  const load = makeLoader({
    './bridge': { rpc: e.rpc, onDidChange: () => () => undefined, loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); } },
    './hooks': { messageOf: (x) => String(x?.message ?? x), useAutoGrow: () => ({ current: null }), useRemote: () => [{ status: 'idle' }, () => undefined] },
    './knowledge': { KnowledgeView: () => null },
  }, ambientReact);
  const drafts = load('drafts');
  drafts.setDraftModel('file:/m/A.bim');
  const card = load('instructions.tsx').AiInstructionsCard;
  const rt = makeHookRuntime();
  const state = { status: 'ready', value: { present: true, instructions: e.text['file:/m/A.bim'], limit: 2000 } };
  const h = rt.renderHook(() => card({ state, onReload: () => undefined, onNotes: () => undefined, suggestions: { status: 'ready', value: { suggestions: [] } }, onSuggestionsChanged: () => undefined }));
  return { drafts, tree: () => h.result, unmount: () => rt.unmount() };
}

async function openEditor(w) {
  button(w.tree(), 'Edit').props.onClick(); await settle();
  assert.ok(area(w.tree()), 'the editor is open');
}

test('R-A (join item 3): words typed for A and saved while the model switches to B are not written into B', async () => {
  const e = instructionsEngine();
  const w = world(e);
  await openEditor(w);
  type(area(w.tree()), 'Words typed for A'); await settle();
  e.readHold = deferred();                         // the engine is busy: the Save's first call (its read) waits
  button(w.tree(), 'Save').props.onClick(); await settle();
  // B is opened while that Save is on its way (the assistant over MCP, or the person from the tree): the engine swaps sessions, the page follows.
  e.current = 'file:/m/B.bim'; w.drafts.setDraftModel('file:/m/B.bim');
  e.readHold.resolve(); e.readHold = null; await settle(); await settle(); await settle();
  assert.equal(e.text['file:/m/B.bim'], '', 'B keeps its own (empty) instructions: words typed for A were never written into B');
  const write = e.calls.find((c) => c[0] === 'setAiInstructions');
  assert.ok(!write || write.includes('file:/m/A.bim'), 'a write, if any, names the model the words were typed for, so the engine can refuse it');
});

test('R-B: words typed while a Save is on its way are not deleted when that Save completes', async () => {
  const e = instructionsEngine();
  const w = world(e);
  await openEditor(w);
  type(area(w.tree()), 'First sentence.'); await settle();
  e.writeHold = deferred();                        // the engine is busy: the write waits
  button(w.tree(), 'Save').props.onClick(); await settle(); await settle();
  const ta = area(w.tree());
  assert.ok(ta && !ta.props.disabled && !ta.props.readOnly, 'the text stays editable while the Save is on its way');
  type(ta, 'First sentence. Second sentence.'); await settle();
  e.writeHold.resolve(); e.writeHold = null; await settle(); await settle(); await settle();
  assert.equal(e.text['file:/m/A.bim'], 'First sentence.', 'the engine took only the words the Save carried');
  const shown = area(w.tree())?.props.value;
  const held = w.drafts.draftStore.get('file:/m/A.bim', 'instructions|text', '')?.value;
  assert.ok(shown === 'First sentence. Second sentence.' || held === 'First sentence. Second sentence.',
    `the second sentence is still somewhere the person can find it (editor shows ${JSON.stringify(shown)}, store holds ${JSON.stringify(held)})`);
});

// ---- join item 6 / F14: words kept after "Build without them" ----------------------------------------------------------------------------------------
// The built-plan effect no longer clears them (startplan.tsx:780). But the plan read clears every start-plan draft of the model whenever the engine says there is
// no plan (startplan.tsx:708). So when the assistant discards the BUILT plan over MCP, the words the page promised to keep "until you discard them" go with no
// click from the person.
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';
test('F14 (join item 6): words kept after a build survive the assistant discarding the built plan', async () => {
  const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
  const MODEL = view.destination.modelPath;
  const persisted = {};
  const engine = { plan: { ...view, built: true } };
  const load = makeLoader({
    './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
    './bridge': {
      rpc: async (m) => (m === 'getStartPlan' ? engine.plan : undefined), onEngineState: () => () => {}, pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
      copyText: async () => true, onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true, revealInTree: () => {},
      loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
    },
    './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
    './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
    './connection': { useConnection: () => ({ openConnections: () => {} }) },
  }, ambientReact);
  load('drafts').setDraftModel(MODEL);
  const words = load('startplan-drafts').draftStore;
  words.set(MODEL, 'some-line', 'description', { text: 'Words the person typed and did not save.', base: 'Old words.' });   // kept after Build without them
  const sp = load('startplan.tsx');
  const rt = makeHookRuntime();
  let nonce = 0;
  const h = rt.renderHook(() => sp.useStartPlan('s1', 1, nonce));
  await settle(); await settle();
  assert.equal(words.get(MODEL, 'some-line', 'description')?.text, 'Words the person typed and did not save.', 'precondition: the built plan keeps them');
  engine.plan = { exists: false };          // the assistant discards the built plan over MCP; the page reads the plan again
  nonce = 1; h.rerender(); await settle(); await settle();
  assert.equal(words.get(MODEL, 'some-line', 'description')?.text, 'Words the person typed and did not save.', 'the kept words are still there: only the person\'s Discard ends them');
  rt.unmount();
});
