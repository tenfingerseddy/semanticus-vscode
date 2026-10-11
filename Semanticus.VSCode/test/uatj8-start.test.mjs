// UAT of the join 8 build (Sol, 4 Oct), the page's half of the fixes. What only a browser sees (the editor staying open, a live update arriving while words are typed)
// is proven by tools/uishot/startplan-drive.mjs (scenes words-conflict-*, sample-control-*); this file reads the real page code and the model's own words.
//
//   U1  a words save that meets newer words keeps the editor open with the person's draft, shows the newer words beside it, and offers Keep mine / Use the new one.
//   U2  after the sample grant each included table carries the per-table sample choice, the same edit and the same words as the grant dialog.
//   U4  the folder help says what the start writes: an empty model, and no table, measure or link until the build.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, makeHookRuntime, settle, req } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
// What the stubbed engine answers, and who listens for the host's engine up/down announcement (the hook tests below change both).
let rpcImpl = async () => { throw new Error('the test loader answers no calls'); };
const engineListeners = new Set(), reconnectListeners = new Set();
const stubs = {
  './startplan.css': {},
  './plan-history.css': {},
  './sparkline': { ValueSparkline: () => null },
  './bridge': {
    rpc: (...a) => rpcImpl(...a), onEngineState: (f) => { engineListeners.add(f); return () => engineListeners.delete(f); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true,
    onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: (f) => { reconnectListeners.add(f); return () => reconnectListeners.delete(f); }, navigateInStudio: () => {}, claimConsentAsk: async () => true,
    revealInTree: () => {}, loadState: (_k, fallback) => fallback, saveState: () => undefined,
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
};
const load = makeLoader(stubs);
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const M = load('startplan-model');
const P = load('startplan');
// Each fix is judged on its own, so a red run shows every item that is still open and not only the first.
const results = [];
const section = (name, fn) => { try { fn(); results.push([name, 'ok']); } catch (e) { results.push([name, 'FAIL ' + String(e.message).split('\n')[0]]); } };

// ---------------------------------------------------------------- U1: a words save that meets newer words
const mine = { ref: 'measure:Sales/Total DiscountAmount', kind: 'description', current: 'A.' };   // the words the person started from
const newer = { ...mine, current: 'B.' };                                                           // what the assistant wrote since

section('U1 words editor keeps the draft', () => {
assert.deepEqual(M.wordsMove(mine, 'C, typed.', newer), { kind: 'ask', theirs: 'B.' }, 'typed words and newer words: ask, never replace');
assert.deepEqual(M.wordsMove(mine, 'A.', newer), { kind: 'follow', text: 'B.' }, 'nothing typed yet: the editor follows the newer words');
assert.deepEqual(M.wordsMove(mine, 'B.', newer), { kind: 'same' }, 'what is typed is what the line holds now: no conflict');
assert.deepEqual(M.wordsMove(mine, 'C, typed.', mine), { kind: 'none' }, 'the line did not move');
assert.deepEqual(M.wordsMove(mine, 'C, typed.', null), { kind: 'none' }, 'the line is not on the page for a moment: nothing to compare');

assert.deepEqual(M.wordsSaveResult({ written: 1, refused: [] }), { saved: true, moved: false, said: null });
const movedSentence = 'measure:Sales/Total DiscountAmount: These words changed while you were editing them, so yours were not saved. Look at the new words and edit again.';
assert.deepEqual(M.wordsSaveResult({ written: 0, refused: [movedSentence] }), { saved: false, moved: true, said: null }, 'the engine\'s refusal over newer words is a conflict, which the editor shows itself');
const other = M.wordsSaveResult({ written: 0, refused: ['Total: A rename would clash with another name.'] });
assert.equal(other.saved, false); assert.equal(other.moved, false); assert.match(other.said, /clash/, 'any other refusal is said in the editor, beside the draft it did not save');
assert.equal(M.wordsSaveResult(undefined).saved, false);

assert.equal(M.COPY.keepMine, 'Keep mine');
assert.equal(M.COPY.useNew, 'Use the new one');
for (const k of ['wordsMoved', 'yourWords', 'newWords']) assert.ok(M.COPY[k] && !/[–—]/.test(M.COPY[k]), `${k} has words and no dash`);
assert.match(M.COPY.wordsMoved, /Your words are still here, and nothing was saved yet\./);

{
  const s = src('startplan.tsx');
  const detail = s.slice(s.indexOf('export function LineDetail'), s.indexOf('export function ColumnList'));
  assert.match(detail, /<WordsEditor /, 'the words use their own editor, which keeps the draft');
  assert.ok(!/onWords\(wordsAtOpen, t\); done\(\)/.test(detail), 'a save no longer closes the editor before the engine has answered');
  const editor = s.slice(s.indexOf('function WordsEditor'), s.indexOf('export function LineDetail'));
  assert.match(editor, /M\.wordsMove\(/, 'the editor asks the model what a moved line means for the draft');
  assert.match(editor, /data-testid="sp-words-moved"/);
  assert.match(editor, /data-testid="sp-words-keepmine"/);
  assert.match(editor, /data-testid="sp-words-usenew"/);
  assert.match(editor, /data-testid="sp-words-theirs"/, 'the newer words are shown beside the draft');
  assert.match(s, /const onWords = useCallback\(async \(words: [^)]*\): Promise<M\.WordsSaveResult>/, 'the container answers the editor with what happened');
}
{
  // The editor itself, drawn as it stands when the newer words have arrived: the draft is in the box, the newer words are beside it, the choice is offered, Save waits.
  const html = renderToStaticMarkup(React.createElement(P.WordsEditor, { live: newer, start: mine, draft: 'C, typed.', busy: false, onSave: async () => true, onCancel() {} }));
  const text = readable(html);
  assert.match(html, /data-testid="sp-words-moved"/, 'the conflict is shown in the editor');
  assert.match(html, /C, typed\./, 'the draft is still in the box');
  assert.match(html, /data-testid="sp-words-theirs"[^>]*>[^<]*B\./, 'the newer words are beside it');
  assert.match(text, /Keep mine/); assert.match(text, /Use the new one/);
  assert.match(html, /data-testid="sp-words-save"[^>]*disabled/, 'Save waits for the choice');
  const calm = renderToStaticMarkup(React.createElement(P.WordsEditor, { live: mine, start: mine, draft: 'C, typed.', busy: false, onSave: async () => true, onCancel() {} }));
  assert.ok(!/sp-words-moved/.test(calm), 'no banner while the line has not moved');
  assert.ok(!/data-testid="sp-words-save"[^>]*disabled/.test(calm), 'and Save is open');
}

});

// ---------------------------------------------------------------- U2: the per-table sample choice on the plan after the grant
const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
const staff = view.sections.find((s) => s.id === 'tables').groups.find((g) => g.table === 'Staff');
const asPro = (v) => ({ ...v, sampleFacts: true, sample: { state: 'read' }, decide: { state: 'stable' }, jevDeclined: false, built: false });
section('U2 per-table sample choice', () => {
assert.deepEqual(M.sampleControlOf(asPro(view), staff, { pro: true }), { name: 'Staff', allowed: true }, 'after the grant a table with a sample carries the choice');
assert.deepEqual(M.sampleControlOf(asPro(view), { ...staff, sampleOff: true }, { pro: true }), { name: 'Staff', allowed: false }, 'a refused table reads as refused');
assert.equal(M.sampleControlOf({ ...asPro(view), decide: { state: 'waiting_grant' } }, staff, { pro: true }), null, 'before the grant the dialog has the choice, so the page does not');
assert.equal(M.sampleControlOf({ ...asPro(view), jevDeclined: true }, staff, { pro: true }), null, 'no grant to narrow after Not now');
assert.equal(M.sampleControlOf({ ...asPro(view), sampleFacts: false, sample: { state: 'none' } }, staff, { pro: true }), null, 'no sample, nothing to refuse');
assert.equal(M.sampleControlOf(asPro(view), staff, { pro: false }), null, 'Free has no Jev to refuse it to');
assert.equal(M.sampleControlOf({ ...asPro(view), built: true }, staff, { pro: true }), null, 'a built plan is no longer changed');
assert.equal(M.sampleControlOf(asPro(view), { table: null, lines: [] }, { pro: true }), null, 'a group that is not a table has none');
assert.equal(M.sampleControlOf(asPro(view), { ...staff, lines: staff.lines.map((l) => ({ ...l, kept: false })) }, { pro: true }), null, 'a table left out has none');
assert.deepEqual(M.tableSampleEdit('Staff', false), { op: 'set_table_sample', target: 'Staff', value: 'off' }, 'the same edit the assistant makes (edit_start_plan set_table_sample)');

const proBody = (v, extra = {}) => renderToStaticMarkup(React.createElement(P.StartPlanBody, {
  view: v, pro: true, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {}, ...extra,
}));
{
  const html = proBody(asPro(view));
  assert.ok((html.match(/data-testid="sp-sample"/g) ?? []).length >= 1, 'a table of the plan carries the sample choice');
  assert.match(readable(html), /Send counts and patterns from its sample/, 'in the words of the grant dialog');
  const off = JSON.parse(JSON.stringify(asPro(view)));
  off.sections.find((s) => s.id === 'tables').groups.find((g) => g.table === 'Staff').sampleOff = true;
  assert.match(proBody(off), /data-testid="sp-sample"[^>]*data-allowed="no"/, 'a refused table reads unchecked');
  assert.ok(!/data-testid="sp-sample"/.test(proBody({ ...asPro(view), decide: { state: 'waiting_grant' } })), 'no control before the grant');
  assert.ok(!/data-testid="sp-sample"/.test(renderToStaticMarkup(React.createElement(P.StartPlanBody, { view: asPro(view), pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {} }))), 'none on Free');
}
{
  const s = src('startplan.tsx');
  assert.match(s, /M\.tableSampleEdit\(/, 'the control sends the model\'s own edit');
  const consent = src('consent.tsx');
  assert.match(consent, /START_CARD\.tableSample\b/, 'the dialog keeps the words the page borrows');
}

});

// ---------------------------------------------------------------- U7: the band says what the build does with each kind of question
section('U7 the band does not say an unanswered pick stays out', () => {
  const pick = { id: 'unsure_pick:b1', kind: 'unsure_pick', text: 'Jev was not sure how to add up Rate in Staff, so the plan uses average. Is that right?', options: ['average', 'none'], recommended: 'average' };
  const line = { id: 'unsure_line:l1', kind: 'unsure_line', text: 'Format on Hours: Jev was not sure, so it is left out until you say.', options: ['keep', 'drop'], recommended: 'drop' };
  const grant = { id: 'grant', kind: 'grant', text: 'x', options: ['Allow'] };
  const both = M.needsLeadFor([pick, line, grant]);
  assert.match(M.needsLeadFor([line]), /Each stays out of the build until you answer/, 'a line left out says so');
  assert.ok(!/stays out|stay out|left out/.test(M.needsLeadFor([pick])), 'a pick the plan builds does not say it stays out');
  assert.match(M.needsLeadFor([pick]), /builds? the rule's pick/, 'it says the plan builds the pick');
  assert.match(M.needsLeadFor([pick]), /Answer to change it/);
  assert.match(both, /stays out of the build/, 'both kinds: the lines that are out are said to be out'); assert.match(both, /builds? the rule's pick/, 'and the pick that is built is said to be built');
  assert.equal(M.needsLeadFor([grant]), null, 'the grant is not a Jev question');
  assert.equal(M.needsLeadFor([]), null);
  assert.ok(!/[–—]/.test(both));
  assert.match(src('startplan.tsx'), /M\.needsLeadFor\(needs\)/, 'the band uses it');
});

// ---------------------------------------------------------------- U4: what the folder help says the start writes
section('U4 folder help says what is written', () => {
  const help = M.COPY.formFolderHint, note = M.COPY.emptyNote('/x/models');
  for (const t of [help, note]) {
    assert.ok(!/nothing else is written until you build/i.test(t), 'the words no longer say that nothing is written: the start saves an empty model');
    assert.ok(!/[–—]/.test(t));
  }
  assert.match(help, /empty model/i, 'the help says an empty model is created');
  assert.match(help, /\.SemanticModel/, 'in a folder named for the model');
  assert.match(help, /No table, measure or link is written until you approve the plan and build\./, 'and what waits for the build');
  assert.match(note, /No table, measure or link is written until you approve and build\./);
});


// ---------------------------------------------------------------- D1 (fix round 2): typed words outlive the editor
const D = load('startplan-drafts');
const staffWords = { ref: 'table:Staff', kind: 'description', current: 'People who work at the firm.' };
section('D1 the model key is the file, never the session', () => {
  assert.equal(M.modelKeyOf(view), view.destination.modelPath, 'the key is the plan\'s destination file');
  assert.equal(M.modelKeyOf({ ...view, destination: { folder: '/a', name: 'B' } }), '/a/B', 'without a model path: the folder and the name');
  assert.equal(M.modelKeyOf({ exists: true, id: 'sp-9', sections: [], needsYou: [] }), 'sp-9', 'last: the plan id');
  const k1 = D.draftKey('/m/A.SemanticModel', 'w-desc-staff', 'description'), k2 = D.draftKey('/m/B.SemanticModel', 'w-desc-staff', 'description');
  assert.notEqual(k1, k2, 'two models never share a draft');
  assert.notEqual(D.draftKey('/m', 'a|b', 'c'), D.draftKey('/m', 'a', 'b|c'), 'the parts of a key cannot run together');
});

section('D1 a draft survives a row switch, a remount and a restart', () => {
  const disk = { all: {} };
  const storage = { load: () => JSON.parse(JSON.stringify(disk.all)), save: (a) => { disk.all = JSON.parse(JSON.stringify(a)); } };
  const m = view.destination.modelPath;
  const one = D.createDraftStore(storage);
  one.set(m, 'w-desc-staff', 'description', { text: 'Staff, typed.', base: staffWords.current });
  one.set(m, 'w-desc-clients', 'description', { text: 'Clients, typed.', base: 'Old clients.' });
  // (a) another row opens: the editor of the first goes, its draft stays, and both rows are held
  assert.deepEqual(one.get(m, 'w-desc-staff', 'description'), { text: 'Staff, typed.', base: staffWords.current });
  assert.deepEqual([...one.held(m)].sort(), [D.heldId('w-desc-clients', 'description'), D.heldId('w-desc-staff', 'description')].sort(), 'both rows are held as drafts');
  // (b) a remount on a restarted engine: a NEW store over the same persisted state (the session id is not in the key)
  const two = D.createDraftStore(storage);
  assert.deepEqual(two.get(m, 'w-desc-staff', 'description'), { text: 'Staff, typed.', base: staffWords.current }, 'a remount finds the draft by file, line and kind');
  assert.equal(two.get(m, 'w-desc-staff', 'synonyms'), null, 'another kind of words on the same line is not that draft');
  assert.equal(two.get('/m/Other.SemanticModel', 'w-desc-staff', 'description'), null, 'another model is not that draft');
  // words that are what they started from hold nothing
  two.set(m, 'w-desc-staff', 'description', { text: ' People who work at the firm. ', base: staffWords.current });
  assert.equal(two.get(m, 'w-desc-staff', 'description'), null, 'typed back to the start: nothing to keep');
  // a save the engine took, or the person\'s own discard, ends a draft; nothing else does
  two.clear(m, 'w-desc-clients', 'description');
  assert.equal(two.any(), false);
  // the plan's discard clears the model\'s drafts and no other model\'s
  two.set(m, 'a', 'description', { text: 'x', base: 'y' }); two.set('/m/Other.SemanticModel', 'a', 'description', { text: 'x', base: 'y' });
  two.clearModel(m);
  assert.equal(two.get(m, 'a', 'description'), null); assert.notEqual(two.get('/m/Other.SemanticModel', 'a', 'description'), null);
  // the plan re-renders on the SIGNATURE of the held rows: typing into a row that already has a draft must not change it (the plan has hundreds of rows)
  const sigStore = D.createDraftStore({ load: () => ({}), save() {} });
  const sig0 = sigStore.heldSignature(m);
  sigStore.set(m, 'r1', 'description', { text: 'a', base: 'b' }); const sig1 = sigStore.heldSignature(m);
  sigStore.set(m, 'r1', 'description', { text: 'ab', base: 'b' }); const sig2 = sigStore.heldSignature(m);
  sigStore.clear(m, 'r1', 'description');
  assert.notEqual(sig0, sig1, 'a row gaining a draft changes the signature'); assert.equal(sig1, sig2, 'more typing in that row does not'); assert.equal(sigStore.heldSignature(m), sig0, 'losing it changes it back');
  // subscribers hear every change
  let heard = 0; const off = two.subscribe(() => { heard++; });
  two.set(m, 'b', 'description', { text: 'p', base: 'q' }); two.clear(m, 'b', 'description'); off(); two.set(m, 'c', 'description', { text: 'p', base: 'q' });
  assert.equal(heard, 2, 'a set and a clear are heard; an unsubscribed listener is not');
  // state that cannot be read or written is not a crash: the drafts live for the page
  const broken = D.createDraftStore({ load: () => { throw new Error('no state'); }, save: () => { throw new Error('no state'); } });
  broken.set(m, 'a', 'description', { text: 'x', base: 'y' });
  assert.equal(broken.get(m, 'a', 'description')?.text, 'x');
});

section('D1 the page opens a row with its draft and flags the row', () => {
  const m = view.destination.modelPath;
  D.draftStore.clearModel(m);
  const tier = { pro: false };
  const line = M.linesOf(view).find((l) => l.item?.id === 'w-desc-staff');
  const detail = (extra = {}) => renderToStaticMarkup(React.createElement(P.LineDetail, { line, view, tier, busy: false, onEdit() {}, onWords: async () => ({ saved: true, moved: false, said: null }), modelKey: m, ...extra }));
  assert.ok(!/data-testid="sp-words-editor"/.test(detail()), 'with no draft the row opens without an editor');
  D.draftStore.set(m, 'w-desc-staff', 'description', { text: 'People who work at the firm. And contractors.', base: staffWords.current });
  const opened = detail();
  assert.match(opened, /data-testid="sp-words-editor"/, 'a row with a held draft opens on its editor');
  assert.match(opened, /People who work at the firm\. And contractors\./, 'with the typed words in the box');
  assert.ok(!/data-testid="sp-words-moved"/.test(opened), 'the line has not moved under it');
  // the assistant wrote while the draft was away: the editor asks, as it does live (the draft was typed over the OLD words)
  const moved = JSON.parse(JSON.stringify(view));
  const ml = M.linesOf(moved).find((l) => l.item?.id === 'w-desc-staff'); ml.word = 'The assistant wrote newer words.'; ml.item.after = ml.word;
  const asked = renderToStaticMarkup(React.createElement(P.LineDetail, { line: ml, view: moved, tier, busy: false, onEdit() {}, onWords: async () => ({ saved: true, moved: false, said: null }), modelKey: m }));
  assert.match(asked, /data-testid="sp-words-moved"/, 'newer words that arrived while the draft was away are shown beside it');
  assert.match(asked, /And contractors\./); assert.match(asked, /The assistant wrote newer words\./);
  // the plan: the row says the draft is not saved, and another model's page does not
  const body = (v) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view: v, pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {} }));
  const flagged = body(view);
  assert.match(flagged, /data-testid="sp-row-w-desc-staff"[^>]*data-draft="yes"/, 'the row with a draft is marked');
  assert.match(readable(flagged), /Draft not saved/, 'in plain words');
  assert.equal((flagged.match(/data-testid="sp-draft-flag"/g) ?? []).length, 1, 'once, on that row only');
  assert.ok(!/data-draft="yes"/.test(body({ ...view, destination: { ...view.destination, modelPath: '/m/Other.SemanticModel' } })), 'a plan of another file shows no flag');
  D.draftStore.clearModel(m);
  assert.ok(!/sp-draft-flag/.test(body(view)), 'cleared: no flag');
});

section('D1 the editor keeps and clears the draft in the right places', () => {
  const s = src('startplan.tsx');
  const editor = s.slice(s.indexOf('export function WordsEditor'), s.indexOf('export function LineDetail'));
  assert.match(editor, /onDraft\??\.\(/, 'the editor reports every change of its draft upward, so the draft outlives it');
  assert.match(editor, /M\.COPY\.discardWords/, 'with words typed, Cancel says what it does: Discard my words');
  const save = editor.slice(editor.indexOf('const save = async'), editor.indexOf('const keys ='));
  assert.match(save, /if \(r\.saved\) \{\s*onTook\??\.\(sent\)/, 'a save the engine took ends the draft (R-B, drafts-sol F10: only the words it sent, through onTook)');
  const esc = editor.slice(editor.indexOf('const keys ='), editor.indexOf('const box ='));
  assert.ok(!/onDraft/.test(esc), 'Escape closes the editor but never throws the draft away');
  assert.match(editor, /data-testid="sp-words-cancel"[^>]*onClick=\{\(\) => \{[^}]*onDraft\??\.\(null\)/, 'Discard ends the draft');
  assert.match(editor, /sp-words-usenew[^]*?setText\(live!\.current\)/, 'Use the new one loads the new words, which leaves nothing typed, which holds no draft');
  const detail = s.slice(s.indexOf('export function LineDetail'), s.indexOf('export function ColumnList'));
  assert.match(detail, /draftStore\.get\(/, 'the row looks for a held draft when it opens');
  assert.match(s, /draftStore\.clearModel\(/, 'the plan\'s own discard clears its drafts');
});



// A failed read or a down engine keeps the last plan on screen. The hook is run for real over a stubbed engine.
const asyncSection = async (name, fn) => { try { await fn(); results.push([name, 'ok']); } catch (e) { results.push([name, 'FAIL ' + String(e.message).split('\n')[0]]); } };
await asyncSection('D1 a failed read or a down engine keeps the plan on screen', async () => {
  const rt = makeHookRuntime();
  // The hook runtime's useCallback returns a new function every render, which re-runs every effect that lists it; React keeps it by its dependencies, and so does this.
  const React = { ...rt.React, useCallback(f, deps) { const r = rt.React.useRef({ deps: null, f: null }).current; if (!r.f || !deps || !r.deps || deps.some((d, i) => !Object.is(d, r.deps[i]))) { r.f = f; r.deps = deps; } return r.f; } };
  const HP = makeLoader(stubs, React)('startplan');
  const plan = JSON.parse(JSON.stringify(view));
  let answer = async () => plan;
  rpcImpl = async (method) => { assert.equal(method, 'getStartPlan'); return answer(); };
  const h = rt.renderHook(() => HP.useStartPlan('s1', 1, 0));
  await settle();
  assert.equal(h.result.view?.digest, plan.digest, 'the plan is read'); assert.equal(h.result.stale, null, 'and is not stale');
  // the read fails: the engine is down
  answer = async () => { throw new Error('Engine not connected.'); };
  await h.result.read(); await settle();
  assert.equal(h.result.view?.digest, plan.digest, 'a failed read keeps the last plan');
  assert.equal(h.result.stale, 'down', 'and says the engine is not there');
  // any other failure: kept too, said as a failed read
  answer = async () => { throw new Error('Request timed out.'); };
  await h.result.read(); await settle();
  assert.equal(h.result.view?.digest, plan.digest); assert.equal(h.result.stale, 'failed');
  // the answer comes back: stale ends
  answer = async () => plan;
  await h.result.read(); await settle();
  assert.equal(h.result.stale, null, 'a good read ends it');
  // the host announces the engine is gone, before any read fails
  for (const f of engineListeners) f(false); await settle();
  assert.equal(h.result.stale, 'down', 'the host\'s announcement is enough');
  assert.equal(h.result.view?.digest, plan.digest);
  // ...and that it is back: the plan is read again
  let reads = 0; answer = async () => { reads++; return plan; };
  for (const f of engineListeners) f(true); await settle();
  assert.ok(reads >= 1, 'back: the page reads the plan again'); assert.equal(h.result.stale, null);
  // a reconnect cuts a read in flight short (bridge.ts rejectAll): the read is made again, and the plan stays on screen meanwhile
  answer = async () => { throw new Error('Semanticus: the engine connection was lost while this request was in flight.'); };
  await h.result.read(); await settle();
  assert.equal(h.result.stale, 'failed'); assert.equal(h.result.view?.digest, plan.digest);
  answer = async () => plan;
  for (const f of reconnectListeners) f('person'); await settle();
  assert.equal(h.result.stale, null, 'a reconnect reads the plan again');
  // a plan that is truly gone is still gone
  answer = async () => ({ exists: false });
  await h.result.read(); await settle();
  assert.equal(h.result.view, null, 'an answer of no plan clears the page');
  // nothing held and the read fails: there is no last plan to keep
  answer = async () => { throw new Error('Engine not connected.'); };
  await h.result.read(); await settle();
  assert.equal(h.result.view, null);
  rt.unmount();
});
section('D1 the page waits, in words, and Save waits for the engine', () => {
  const s = src('startplan.tsx');
  assert.match(s, /M\.COPY\.engineRestarting/, 'the page says it is not answering and the typed words are kept');
  const panel = s.slice(s.indexOf('export function StartPlanPanel'), s.indexOf('// ---------------------------------------------------------------- the start form'));
  assert.match(panel, /stale/, 'the panel takes the stale state');
  assert.match(panel, /isEngineAway\(e\) \? M\.COPY\.engineRestarting/, 'a Save refused because the engine is down is told in the plan\'s words, not the raw one');
  const app = src('App.tsx');
  assert.match(app, /hasWordsDraft|useAnyWordsDraft/, 'the app keeps the Plan on screen while the engine is down if words are typed');
  const body = s.slice(s.indexOf('export function StartPlanBody'), s.indexOf('// ---------------------------------------------------------------- the container'));
  assert.match(body, /sp-stale/, 'the notice is on the page');
  // The editor's Save is off while the engine is away, with the reason said, and the box stays typeable.
  const tier = { pro: false };
  const line = M.linesOf(view).find((l) => l.item?.id === 'w-desc-staff');
  const html = renderToStaticMarkup(React.createElement(P.WordsEditor, { live: staffWords, start: staffWords, draft: 'Typed.', busy: false, away: true, onSave: async () => true, onCancel() {} }));
  assert.match(html, /data-testid="sp-words-save"[^>]*disabled/, 'Save waits while the engine is away');
  assert.match(readable(html), /Save waits until Semanticus answers\. Your typed words are kept\./, 'and says why');
  assert.ok(!/<textarea[^>]*disabled/.test(html), 'the box stays open to type in');
  assert.match(html, /Discard my words/, 'a typed draft is discarded by name');
  void tier; void line;
});
for (const [n, r] of results) console.log(`${r === 'ok' ? 'ok  ' : 'RED '} ${n}${r === 'ok' ? '' : ': ' + r}`);
if (results.some(([, r]) => r !== 'ok')) process.exit(1);
console.log('uatj8-start: ok');
