// The draft store's orphans and its honest words (join 11, additions A1 to A5 from the uatj8 re-check).
//
//   A1  a draft of ANOTHER model must never keep the Plan on screen through an outage: the open model's drafts only, and the
//       recovery (Restart engine) is always on the page;
//   A2  "Semanticus is restarting" is said only when it is true: the engine is not answering, and the page offers Restart engine;
//   A3  Approve and build does not leave typed words behind unasked: the rows that hold unsaved words are named first;
//   A4  a draft ends when its plan is built, discarded by any door, or its line is gone, and is never shown in another line;
//   A5  a refusal names things as the page shows them, never as a raw ref.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const persisted = {};
let rpcImpl = async () => { throw new Error('no engine here'); };
const engineListeners = new Set(), reconnectListeners = new Set();
const stubs = {
  './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
  './bridge': {
    rpc: (...a) => rpcImpl(...a), onEngineState: (f) => { engineListeners.add(f); return () => engineListeners.delete(f); }, pickWorkingCopyFolder: async () => null,
    onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true, onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {},
    onReconnect: (f) => { reconnectListeners.add(f); return () => reconnectListeners.delete(f); }, navigateInStudio: () => {}, claimConsentAsk: async () => true, revealInTree: () => {},
    loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
};
const load = makeLoader(stubs);
const M = load('startplan-model');
const P = load('startplan');
const D = load('startplan-drafts');
const Dr = load('drafts');
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
const MODEL = '/models/Contoso.SemanticModel';
Dr.setDraftModel(MODEL);
const body = (v = view, extra = {}) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view: v, pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {}, ...extra }));
const lineId = 'w-desc-staff';
const hold = (model = MODEL, id = lineId) => D.draftStore.set(model, id, 'description', { text: 'Typed.', base: 'People who work at the firm.' });
const clean = () => { for (const m of [MODEL, '/models/Other.SemanticModel', view.destination.modelPath]) D.draftStore.clearModel(m); };

// ---- A1 ----------------------------------------------------------------------------------------------------------
test('A1: only the open model\'s drafts keep the Plan on screen through an outage', () => {
  clean();
  hold('/models/Other.SemanticModel');
  assert.equal(D.draftStore.anyFor(MODEL), false, 'a draft of another model counts for nothing');
  assert.equal(D.keepPlanThroughOutage({ hasSession: true, hasDraft: D.draftStore.anyFor(MODEL), tab: 'plan' }), false);
  hold();
  assert.equal(D.draftStore.anyFor(MODEL), true);
  assert.equal(D.keepPlanThroughOutage({ hasSession: true, hasDraft: true, tab: 'plan' }), true);
  assert.equal(D.keepPlanThroughOutage({ hasSession: false, hasDraft: true, tab: 'plan' }), false, 'with no session there is no plan to keep: the recovery page shows');
  assert.equal(D.keepPlanThroughOutage({ hasSession: true, hasDraft: true, tab: 'data' }), false, 'only the Plan tabs');
  clean();
});
test('A1: the app asks the open model, and the stale banner carries Restart engine', () => {
  const app = src('App.tsx');
  assert.match(app, /keepPlanThroughOutage\(\{ hasSession: !!session\?\.sessionId/, 'the app decides through the one rule');
  assert.match(src('startplan-drafts.ts'), /useAnyWordsDraft = \(\) => useAnyDraft\(SCOPE, draftModel\(\)\)/, 'and counts the open model only');
  const html = body(view, { stale: 'down' });
  assert.match(html, /data-testid="sp-stale-restart"/, 'the banner offers Restart engine');
});

// ---- A2 ----------------------------------------------------------------------------------------------------------
test('A2: the page does not say a restart is under way when it is not', () => {
  for (const k of ['engineRestarting', 'engineRestartingNoDraft']) {
    assert.ok(!/restarting/i.test(M.COPY[k]), `${k} must not claim a restart: "${M.COPY[k]}"`);
    assert.match(M.COPY[k], /not answering/, `${k} says what is true`);
  }
  hold();
  assert.match(readable(body(view, { stale: 'down' })), /Semanticus is not answering; your typed words are kept\./);
  clean();
  assert.match(readable(body(view, { stale: 'down' })), /Semanticus is not answering\. The plan shows as it was/);
  assert.match(readable(body(view, { stale: 'down' })), /Restart engine/);
});

// ---- A3 ----------------------------------------------------------------------------------------------------------
test('A3: rows that hold unsaved words are named, as the page shows them', () => {
  clean(); hold(MODEL, 'w-desc-staff');
  const col = (view.sections ?? []).flatMap((s) => s.groups ?? []).find((g) => (g.columns ?? []).length);
  D.draftStore.set(MODEL, `col:${col.columns[0].ref}`, 'rename', { text: 'Better', base: col.columns[0].name });
  const names = M.unsavedRowNames(view, D.draftStore.held(MODEL));
  assert.equal(names.length, 2);
  assert.ok(names.every((n) => !/^(table|column|measure):/.test(n)), `no raw ref: ${JSON.stringify(names)}`);
  assert.ok(names.some((n) => n.includes(col.columns[0].name)), `the column is named: ${JSON.stringify(names)}`);
  clean();
});
test('A3: the ask before a build says which rows hold words and offers both ways', () => {
  const html = renderToStaticMarkup(React.createElement(P.UnsavedWordsAsk, { names: ['Staff description', 'Rate'], busy: false, onSaveFirst() {}, onBuildWithout() {} }));
  const text = readable(html);
  assert.match(text, /Staff description/); assert.match(text, /Rate/);
  assert.match(text, /not saved/i);
  assert.match(html, /data-testid="sp-unsaved-save-first"/); assert.match(html, /data-testid="sp-unsaved-build-without"/);
  const s = src('startplan.tsx');
  const bodySrc = s.slice(s.indexOf('export function StartPlanBody'), s.indexOf('// ---------------------------------------------------------------- the container'));
  assert.match(bodySrc, /askBeforeBuild\(/, 'the Approve and build and Build buttons go through the ask');
  assert.ok(!/onClick=\{p\.onApproveAndBuild\}/.test(bodySrc) && !/onClick=\{p\.onBuild\}/.test(bodySrc), 'no build button calls the build straight');
});

// ---- A4 ----------------------------------------------------------------------------------------------------------
// W3 (drafts fix round 2) amends A4: a line is known by its name, so an assistant's rename looks like a removal. The words are no longer dropped on that guess: see test/drafts-orphan-ask.test.mjs.
test('A4 (amended by W3): a draft whose line is gone is KEPT and asked about, and one whose line is there is not an orphan', async () => {
  clean(); hold(MODEL, 'w-desc-staff'); hold(MODEL, 'w-gone-line');
  const { orphanScopes } = load('orphandrafts');
  const orphans = orphanScopes('startplan|', M.rowNames(view).keys());
  assert.deepEqual(orphans.map((o) => o.scope), ['startplan|w-gone-line'], 'only the line that is gone is asked about');
  assert.ok(D.draftStore.get(MODEL, 'w-gone-line', 'description'), 'its words are still held: nothing drops them on a guess');
  assert.ok(D.draftStore.get(MODEL, 'w-desc-staff', 'description'));
  clean();
});
test('A4 (amended by D3, drafts-sol round 2): a plan discarded by any door but the person\'s own Discard keeps its words, and so does a plan that is only unreachable', async () => {
  clean(); hold();
  const rt = makeHookRuntime();
  const hookReact = { ...rt.React, useCallback(f, deps) { const r = rt.React.useRef({ deps: null, f: null }).current; if (!r.f || !deps || !r.deps || deps.some((d, i) => !Object.is(d, r.deps[i]))) { r.f = f; r.deps = deps; } return r.f; } };
  // One draft store for the hook and for this test: the modules that hold it are the ones already loaded.
  const HP = makeLoader({ ...stubs, './startplan-drafts': D, './drafts': Dr }, hookReact)('startplan');
  const plan = JSON.parse(JSON.stringify(view));
  let answer = async () => plan;
  rpcImpl = async () => answer();
  const h = rt.renderHook(() => HP.useStartPlan('s1', 1, 0));
  await settle();
  assert.equal(h.result.view?.digest, plan.digest);
  assert.ok(D.draftStore.get(MODEL, lineId, 'description'), 'a read keeps the draft');
  answer = async () => { throw new Error('Engine not connected.'); };
  await h.result.read(); await settle();
  assert.ok(D.draftStore.get(MODEL, lineId, 'description'), 'an unreachable engine keeps it');
  answer = async () => ({ exists: false });   // discarded by the assistant, or by another window
  await h.result.read(); await settle();
  assert.ok(D.draftStore.get(MODEL, lineId, 'description'), 'a plan the assistant discarded does NOT take the words the page promised to keep: the Plan lists them with Copy and Discard (WordsKeptAfterBuild, why gone)');
  rt.unmount();
  clean();
});
test('A4: the panel clears the model\'s drafts when the plan is built, and the key is the open model\'s', () => {
  const s = src('startplan.tsx');
  assert.match(s, /planModelKey = \(view[^)]*\)[^=]*=> draftModel\(\) \|\| M\.modelKeyOf\(view\)/, 'one key for the plan\'s drafts: the open model\'s file');
  const panel = s.slice(s.indexOf('export function StartPlanPanel'), s.indexOf('// ---------------------------------------------------------------- the start form'));
  assert.match(panel, /view\.built[^;]*clearModel|clearModel[^;]*built|if \(view\.built\)/, 'a built plan ends its drafts');
  assert.doesNotMatch(panel, /pruneStartDrafts\(/, 'a line that is gone does NOT end its draft on a guess (it may have been renamed): the page asks instead (W3)');
});

// ---- A5 ----------------------------------------------------------------------------------------------------------
test('A5: a refusal names the thing as the page shows it', () => {
  const col = (view.sections ?? []).flatMap((s) => s.groups ?? []).find((g) => (g.columns ?? []).length);
  const c = col.columns[0];
  const raw = `${c.ref}: It contains formula text.`;
  const said = M.wordsSaveResult({ written: 0, refused: [raw] }, view).said;
  assert.ok(!said.includes(c.ref), `no raw ref in: ${said}`);
  assert.match(said, new RegExp(c.name)); assert.match(said, /It contains formula text\./);
  const unknown = M.wordsSaveResult({ written: 0, refused: ['column:dim_client/IND_CD: It contains formula text.'] }, view).said;
  assert.ok(!/column:dim_client\/IND_CD/.test(unknown), `an unknown ref is still not shown raw: ${unknown}`);
  assert.match(unknown, /IND_CD/); assert.match(unknown, /dim_client/);
  assert.equal(M.wordsSaveResult({ written: 0, refused: ['Total: A rename would clash with another name.'] }, view).said, 'Total: A rename would clash with another name.', 'words with no ref are left alone');
});

// ---- A4 for every editor: an object that is gone takes its draft ------------------------------------------------------
test('A4: pruneGone drops the open model\'s drafts of objects that are no longer listed, and only those', () => {
  Dr.setDraftModel(MODEL);
  const { draftStore } = Dr;
  for (const id of ['i1', 'i2']) draftStore.set(MODEL, `insight|${id}`, '', { value: { text: 'x', keys: '' }, base: { text: 'y', keys: '' } });
  draftStore.set('/models/Other.SemanticModel', 'insight|i2', '', { value: 'x', base: 'y' });
  draftStore.set(MODEL, 'rules|bpa|new', '', { value: { id: 'A' }, base: { id: '' } });
  assert.equal(Dr.pruneGone('insight|', null), 0, 'a list that failed to read drops nothing');
  assert.equal(Dr.pruneGone('insight|', ['i1']), 1, 'the lesson that is gone takes its draft');
  assert.ok(draftStore.get(MODEL, 'insight|i1', ''));
  assert.equal(draftStore.get(MODEL, 'insight|i2', ''), null);
  assert.ok(draftStore.get('/models/Other.SemanticModel', 'insight|i2', ''), 'another model\'s draft is not this page\'s to drop');
  assert.equal(Dr.pruneGone('rules|bpa|', [], ['new']), 0, 'a new rule\'s draft stays');
  draftStore.clearModel(MODEL); draftStore.clearModel('/models/Other.SemanticModel');
});
test('A4: the lists whose objects keep a stable id prune on every fresh read; a calculation item (known by its name) asks instead (W3)', () => {
  assert.match(src('knowledge.tsx'), /pruneGone\('insight\|', list\.insights\.map/);
  assert.doesNotMatch(src('advmodels.tsx'), /pruneGone\(/, 'a calculation item has no id that survives a rename');
  assert.match(src('advmodels.tsx'), /<OrphanDrafts prefix="calcitem\|"/);
  assert.match(src('rulesauthor.tsx'), /pruneGone\(`rules\|\$\{kind\}\|`/);
});
