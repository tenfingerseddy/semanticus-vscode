// fix-uat-first-start (two real-user UATs of 1.5.104, 17 Oct). Each block was red before its fix.
//   1. A browser sign-in window the person closed never answers, so the workspace list stayed on "Listing your workspaces..." for a
//      minute or more with no message and no cancel. A sign-in that is closed, cancelled or slow now ends the wait.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const read = (...p) => readFileSync(join(root, ...p), 'utf8');

const STUBS = { './firstrun.css': {}, './pro': { useTier: () => 'pro', upgradeToPro: () => {} }, './bridge': { setGuideFlag: () => {}, requestGuideFlags: async () => ({}), firstRunConnect: async () => ({ ok: true }), firstRunClose: () => {}, onFirstRunSay: () => () => {}, rpc: async () => { throw new Error('no calls here'); }, rpcPressed: async () => { throw new Error('no calls here'); }, runHostCommand: () => {}, onEngineState: () => () => {}, requestFirstRun: async () => ({ show: false, connected: false }) } };
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true }, fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (STUBS[name] ? STUBS[name] : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const testids = (html) => [...html.matchAll(/data-testid="([^"]+)"/g)].map((m) => m[1]);

const M = load(join(webview, 'src', 'connectpickers-model.ts'));
const V = load(join(webview, 'src', 'connectpickers.tsx'));
const h = React.createElement;
const noop = () => {};
const view = (extra = {}) => renderToStaticMarkup(h(V.WorkspacePickerView, {
  account: null, workspaces: { kind: 'loading' }, wsId: null, models: null, modelId: null, env: '', authMode: 'interactive', tenant: '',
  otherOpen: false, busy: false, connectError: null, onBack: noop, onRetry: noop, onPickWorkspace: noop, onPickModel: noop, onEnv: noop,
  onAuthMode: noop, onTenant: noop, onToggleOther: noop, onPasteAddress: noop, onConnect: noop, ...extra,
}));

// ---- 1. the sign-in wait ends ----------------------------------------------------------------------------------------------
{
  // A sign-in that never returns (the window was closed): the wait ends by itself, with no answer from the engine.
  const never = new Promise(() => {});
  const w = M.waitForSignIn(never, 20);
  const out = await w.done;
  assert.equal(out.kind, 'unfinished', 'a sign-in that never returns ends after the patience time');

  // Cancel ends it at once.
  const w2 = M.waitForSignIn(new Promise(() => {}), 60_000);
  w2.cancel();
  assert.equal((await w2.done).kind, 'unfinished', 'Cancel ends the wait at once');

  // A sign-in that finishes is a result, and the clock stops.
  const w3 = M.waitForSignIn(Promise.resolve([1, 2]), 20);
  const r3 = await w3.done;
  assert.deepEqual(r3, { kind: 'done', value: [1, 2] });

  // The engine's own closed or cancelled or timed out sentences are the same page as a timeout.
  for (const s of ['Sign-in cancelled. Support id: ab12cd34.', 'Sign-in timed out. Try again when you are ready. Support id: ab12cd34.', 'Sign-in did not finish. Support id: ab12cd34.']) {
    const x = await M.waitForSignIn(Promise.reject(new Error(s)), 1000).done;
    assert.equal(x.kind, 'unfinished', s);
  }
  // Any other refusal is still shown as the engine wrote it.
  await assert.rejects(M.waitForSignIn(Promise.reject(new Error('Fabric said 403.')), 1000).done, /Fabric said 403/);

  assert.ok(M.SIGN_IN_PATIENCE_MS >= 30_000 && M.SIGN_IN_PATIENCE_MS <= 90_000, 'a short wait, not the engine\'s two minutes');
}
{
  // The page: Cancel while it waits, and a plain page when it did not finish.
  const waiting = view({ signIn: 'waiting' });
  assert.ok(testids(waiting).includes('pick-signin-cancel'), 'a waiting sign-in shows Cancel');
  assert.match(readable(waiting), /sign-in window/i, 'and says what it waits for');
  assert.doesNotMatch(readable(waiting), /Listing your workspaces/, 'not the endless listing line');

  const ended = view({ signIn: 'unfinished' });
  assert.match(readable(ended), /Sign-in did not finish\./);
  assert.ok(testids(ended).includes('pick-ws-retry'), 'Try again');
  assert.ok(testids(ended).includes('pick-signin-other'), 'Other ways to sign in');
  assert.doesNotMatch(readable(ended), /Listing your workspaces/);
  assert.ok(!testids(ended).includes('pick-signin-cancel'), 'nothing left to cancel');

  // An ordinary listing (a saved sign-in) is unchanged.
  const plain = view({});
  assert.match(readable(plain), /Listing your workspaces/);
  assert.ok(!testids(plain).includes('pick-signin-cancel'));
}
{
  const src = read('webview', 'src', 'connectpickers.tsx');
  assert.match(src, /waitForSignIn\(/, 'the picker reads the list through the bounded wait');
}

// ---- 2. a folder Semanticus opens gets the open model; a folder opened by hand gets nothing ----------------------------------------
// The handoff is a marker file in THE TARGET folder's .semanticus/, written before the folder opens and taken once (Kane, 17 Oct:
// a window that Semanticus did not open must get nothing from Semanticus, so there is no startup event; the model rides in globalState keyed by the folder and the marker only wakes the extension).
{
  const { mkdtempSync, rmSync, existsSync: has, readdirSync, mkdirSync, writeFileSync } = await import('node:fs');
  const { tmpdir } = await import('node:os');
  const F = load(join(root, 'src', 'firstRun.ts'));
  const model = { kind: 'live', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Demo', database: 'Contoso Sales', modelName: 'Contoso Sales' };
  const dir = mkdtempSync(join(tmpdir(), 'hand-'));
  const other = mkdtempSync(join(tmpdir(), 'hand-'));
  try {
    assert.equal(F.OPEN_HANDOFF_MARKER, '.semanticus/open-handoff', 'one marker name, which package.json wakes on');
    const store = new Map();
    const state = { get: (k) => store.get(k), update: async (k, v) => { v === undefined ? store.delete(k) : store.set(k, v); } };
    const take = (folder, at) => F.takeOpenHandoff(folder, () => Date.parse(at), state, false);
    await F.writeOpenHandoff(state, dir, { model, studio: true, asked: ['Contoso Sales'] }, () => new Date('2026-10-17T10:00:00Z').toISOString(), false);
    assert.ok(has(join(dir, '.semanticus', 'open-handoff')), 'the marker is in the target folder before it opens');
    assert.equal(readFileSync(join(dir, '.semanticus', 'open-handoff'), 'utf8').includes('powerbi'), false, 'and it carries no model');
    assert.deepEqual(readdirSync(other), [], 'and nowhere else');
    const got = take(dir, '2026-10-17T10:00:30Z');
    assert.deepEqual(got.model, model, 'the folder gets the model that was open, from this machine\'s own record');
    assert.equal(got.studio, true, 'and the Studio');
    assert.deepEqual(got.asked, ['Contoso Sales'], 'and what the Pro card already asked about');
    await Promise.resolve();
    assert.equal(take(dir, '2026-10-17T10:00:31Z'), undefined, 'once');
    assert.ok(!has(join(dir, '.semanticus', 'open-handoff')), 'the marker is deleted when it is taken');
    assert.equal(store.has(F.OPEN_HANDOFF_KEY), false, 'and so is the record');

    await F.writeOpenHandoff(state, dir, { model, studio: false, asked: [] }, () => new Date('2026-10-17T10:00:00Z').toISOString(), false);
    assert.equal(take(dir, '2026-10-17T10:30:00Z'), undefined, 'an old record is never used');
    assert.ok(!has(join(dir, '.semanticus', 'open-handoff')), 'and its marker is removed');

    // A marker with no record on this machine (a cloned or shared folder) hands over nothing, and is still removed.
    const planted = mkdtempSync(join(tmpdir(), 'hand-'));
    try {
      mkdirSync(join(planted, '.semanticus'));
      writeFileSync(join(planted, '.semanticus', 'open-handoff'), JSON.stringify({ model, studio: true, asked: [], at: '2026-10-17T10:00:00Z' }), 'utf8');
      assert.equal(take(planted, '2026-10-17T10:00:30Z'), undefined, 'a marker with no record here opens nothing');
      assert.ok(!has(join(planted, '.semanticus', 'open-handoff')), 'and is removed');
      // A record for another folder is not used by this one, even with a marker present.
      await F.writeOpenHandoff(state, dir, { model, studio: true, asked: [] }, () => new Date('2026-10-17T10:00:00Z').toISOString(), false);
      mkdirSync(join(planted, '.semanticus'), { recursive: true });
      writeFileSync(join(planted, '.semanticus', 'open-handoff'), 'x', 'utf8');
      assert.equal(take(planted, '2026-10-17T10:00:30Z'), undefined, 'another folder takes nothing from this folder\'s record');
      assert.ok(store.has(F.OPEN_HANDOFF_KEY), 'and leaves it for its own folder');
      rmSync(join(dir, '.semanticus', 'open-handoff'));
      assert.equal(take(dir, '2026-10-17T10:00:30Z'), undefined, 'a record with no marker is not used either');
    } finally { rmSync(planted, { recursive: true, force: true }); }

    // A repo can ship the marker as a link to a file of the person's. Writing the marker replaces the link and never writes through it (tc2-open-handoff).
    const linked = mkdtempSync(join(tmpdir(), 'hand-'));
    const victim = join(other, 'notes.txt');
    try {
      const { symlinkSync, lstatSync } = await import('node:fs');
      writeFileSync(victim, 'mine\n', 'utf8');
      mkdirSync(join(linked, '.semanticus'));
      symlinkSync(victim, join(linked, '.semanticus', 'open-handoff'));
      await F.writeOpenHandoff(state, linked, { model, studio: true, asked: [] }, () => new Date('2026-10-17T10:00:00Z').toISOString(), false);
      assert.equal(readFileSync(victim, 'utf8'), 'mine\n', 'the file the link pointed to is untouched');
      assert.ok(lstatSync(join(linked, '.semanticus', 'open-handoff')).isFile(), 'and the marker is a plain file now');
      F.takeOpenHandoff(linked, () => Date.parse('2026-10-17T10:00:30Z'), state, false);
    } finally { rmSync(linked, { recursive: true, force: true }); rmSync(victim, { force: true }); }

    await F.writeOpenHandoff(state, dir, { model: undefined, studio: true, asked: [] }, undefined, false);
    assert.equal(F.takeOpenHandoff(dir, Date.now, state, false), undefined, 'no model open, nothing to hand over');
    assert.equal(F.takeOpenHandoff(other, Date.now, state, false), undefined, 'a folder with no marker takes nothing');
    assert.equal(F.takeOpenHandoff(undefined, Date.now, state, false), undefined, 'a window with no folder takes nothing');
  } finally { rmSync(dir, { recursive: true, force: true }); rmSync(other, { recursive: true, force: true }); }
}
{
  const pkg = JSON.parse(read('package.json'));
  assert.ok(!pkg.activationEvents.includes('onStartupFinished'), 'no window wakes Semanticus by itself: an unrelated folder gets no engine, file, lock or status item');
  assert.ok(!pkg.activationEvents.includes('*'), 'and no wildcard either');
  assert.ok(pkg.activationEvents.includes('workspaceContains:.semanticus/open-handoff'), 'a folder Semanticus opens wakes on its own marker');
  const ext = read('src', 'extension.ts');
  assert.doesNotMatch(ext, /writeFolderHandoff|takeFolderHandoff|syncFileStore|FOLDER_HANDOFF/, 'there is no global handoff left');
  assert.match(ext, /takeOpenHandoff\(/, 'the folder takes the marker');
  assert.match(ext, /await writeOpenHandoff\(extCtx.globalState,/, 'Semanticus writes it before it opens a folder');
  assert.match(ext, /registerPersonCommand\('semanticus\.openModelFolder'/, 'the press that opens the model folder exists');
  assert.ok(pkg.contributes.commands.some((c) => c.command === 'semanticus.openModelFolder' && /Open this model's folder/.test(c.title)), 'and it is in the palette with its plain name');
  const deact = ext.slice(ext.indexOf('export function deactivate()'), ext.indexOf('// ---- engine lifecycle'));
  assert.doesNotMatch(deact, /Handoff|writeFile|globalStorage/, 'a closing window writes nothing');
}

// ---- 2c. the Pro card does not ask again after a reload Semanticus caused -------------------------------------------------------------
{
  const C = load(join(root, 'src', 'consentAsks.ts'));
  const before = new C.ConsentAsks();
  assert.equal(before.claim('model:contoso'), true, 'the closing window asked once');
  const after = new C.ConsentAsks();
  after.seed(before.snapshot());
  assert.equal(after.claim('model:contoso'), false, 'the window after the reload does not ask the same question');
  assert.equal(after.claim('model:other'), true, 'a model it was not asked about is still asked');
  after.seed('not a list'); after.seed([7, '', 'x'.repeat(5000)]);
  const ext = read('src', 'extension.ts');
  assert.match(ext, /consentAsks\.seed\(rec\.asked\)/, 'a folder Semanticus opened seeds the card memory');
  assert.match(ext, /consentAsks\.seed\(pending\.asked\)/, 'and so does a Connect that opened a folder');
  assert.match(ext, /asked: consentAsks\.snapshot\(\)/, 'both carry it over');
}

// ---- 2d. the address form's browser sign-in has the picker's Cancel and bounded wait, and a lost engine is said plainly -----------------------
{
  const lost = new Error('Semanticus: the engine connection was lost while this request was in flight. The operation may still have completed. Verify its result before retrying.');
  assert.equal(M.signInSentence(lost), 'Semanticus restarted while you signed in. Try again.', 'a lost engine mid sign-in is said in plain words');
  assert.equal(M.signInSentence('Sign-in was cancelled.'), 'Sign-in was cancelled.', 'any other sentence stays the engine\'s own');
  assert.equal(M.signInStopped('Sign-in timed out after 2 minutes. Try again.'), true, 'the engine\'s stopped sign-in is the same end as our own timeout');
  assert.equal(M.signInStopped('The model was not found.'), false);
  const hub = read('webview', 'src', 'connectionshub.tsx');
  const add = hub.slice(hub.indexOf('const addAndConnect'), hub.indexOf('// "New blank model" is guarded'));
  assert.match(add, /waitForSignIn\(/, 'Connect and remember waits through the same bounded wait as the picker');
  assert.match(add, /signInSentence\(/, 'and says a lost engine plainly');
  assert.match(hub, /data-testid="hub-add-signin-cancel"/, 'the address form shows Cancel while it waits for the sign-in window');
  assert.match(hub, /data-testid="hub-add-signin-waiting"/, 'with the same waiting line');
  const pick = read('webview', 'src', 'connectpickers.tsx');
  assert.match(pick, /signInSentence\(e\)/, 'the picker says a lost engine plainly too');
}

// ---- 2e. a new blank model with unsaved work is remembered by name, so a folder opened by hand can offer it back ---------------------------
{
  const U = load(join(root, 'src', 'unsavedBlank.ts'));
  const t0 = Date.parse('2026-10-18T10:00:00Z');
  const blank = { modelName: 'Scratch Sales', source: '', hasUnsavedEdits: true };
  let list = U.noteUnsavedBlank([], blank, () => t0);
  assert.deepEqual(list.map((x) => x.name), ['Scratch Sales'], 'a pathless model with unsaved edits is remembered');
  assert.deepEqual(U.noteUnsavedBlank(list, { ...blank, modelName: 'Other', liveBound: true }, () => t0), list, 'a published model is not a new blank model');
  assert.deepEqual(U.noteUnsavedBlank(list, { modelName: 'Sales', source: '/x/Sales.bim', hasUnsavedEdits: true }, () => t0), list, 'a model with a file is the engine\'s file recovery, not this list');
  assert.deepEqual(U.noteUnsavedBlank(list, { ...blank, hasUnsavedEdits: false }, () => t0), [], 'all edits undone: nothing is kept');
  assert.deepEqual(U.noteUnsavedBlank(list, { modelName: 'Scratch Sales', source: '/x/Scratch Sales.bim', hasUnsavedEdits: false }, () => t0), [], 'saved to a file: settled');
  for (let i = 0; i < 8; i++) list = U.noteUnsavedBlank(list, { ...blank, modelName: 'M' + i }, () => t0 + i);
  assert.equal(list.length, 5, 'only the last five are kept');
  assert.deepEqual(U.freshUnsavedBlanks([{ name: 'Old', at: '2026-08-01T00:00:00Z' }, { name: 'New', at: '2026-10-17T00:00:00Z' }], () => t0).map((x) => x.name), ['New'], 'an old one is not offered');
  const ext = read('src', 'extension.ts');
  assert.match(ext, /noteUnsavedBlank\(/, 'the host notes the open model on every read of the session');
  assert.match(ext, /offerUnsavedBlank\(/, 'and offers a kept one when a window opens with no model');
  assert.match(ext, /id: 'unsavedBlank'/, 'and Open model lists it, one press away');
  assert.match(ext, /resolveFileRecovery', made\.sessionId, true\)/, 'one press brings the changes back, with no second card');
}

// ---- 3. the Your assistant pulse is seen: the Pro card waits for it -----------------------------------------------------------
{
  const G = load(join(webview, 'src', 'firstrun-gate.ts'));
  assert.ok(G.PULSE_HOLD_MS >= 2400, 'the hold lasts as long as the pulse plays');
  const t0 = Date.now();
  G.holdForPulse(60);
  await G.whenFirstRunClosed();
  assert.ok(Date.now() - t0 >= 55, 'the Pro card is held until the pulse has played');
  // Two asks made while the pulse plays (the open-model ask and an answer-needs-permission ask) both wait for it.
  G.holdForPulse(120);
  const t2 = Date.now();
  let secondAt = 0;
  await Promise.all([G.whenFirstRunClosed(), new Promise((r) => setTimeout(r, 5)).then(() => G.whenFirstRunClosed()).then(() => { secondAt = Date.now() - t2; })]);
  assert.ok(secondAt >= 100, `a second ask is held as long as the first (waited ${secondAt} ms)`);
  const t1 = Date.now();
  await G.whenFirstRunClosed();
  assert.ok(Date.now() - t1 < 60, 'and nothing waits once the pulse is over');
  const fr = read('webview', 'src', 'firstrun.tsx');
  assert.match(fr, /holdForPulse\(/, 'the card holds the Pro card as it pulses');
}

// ---- 4. the say card fits the open model --------------------------------------------------------------------------------------
{
  const R = load(join(webview, 'src', 'firstrun.tsx'));
  const props = { mode: 'assistant', onMode: noop, onConnect: noop, onClose: noop, onAround: noop, error: null, note: null, stage: 'done' };
  assert.notEqual(R.firstSay(true), R.firstSay(false), 'a model that is open changes the first sentence');
  assert.doesNotMatch(R.firstSay(true), /SQL endpoint|Build a model/i, 'it never says to build one when one is open');
  assert.match(readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, say: R.firstSay(true) }))), /top numbers/);
  assert.match(readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props }))), /Build a model from my SQL endpoint/, 'with nothing open it is unchanged');
}

// ---- 5. the tour is about the person's model, or a demo whose pages have something to show ------------------------------
{
  const T = load(join(webview, 'src', 'studio', 'tour-model.ts'));
  const ask = JSON.parse(read('assets', 'learn', 'ask.json'));
  const own = T.chapterFor(ask, true), demo = T.chapterFor(ask, false);
  assert.ok(own.steps.some((x) => x.place === 'edit/queries'), 'on the person\'s model every page is toured');
  assert.ok(!demo.steps.some((x) => x.place === 'edit/queries'), 'the demo has no data, so a page that is empty there is left out');
  assert.ok(own.steps.every((x) => x.example === undefined), 'recorded demo answers are never shown over the person\'s own model');
  assert.ok(!own.steps.some((x) => /rose each quarter/.test(x.say)), 'and no sentence about the demo\'s numbers is said about theirs');
  const plan = T.chapterFor(JSON.parse(read('assets', 'learn', 'plan.json')), true);
  assert.ok(!plan.steps.some((x) => /on purpose/.test(x.say)), 'nor "gaps on purpose"');
  const L = load(join(webview, 'src', 'studio', 'learn-state.ts'));
  const running = (own) => ({ runtime: { phase: 'running', chapterId: 'ask', message: null, own }, tour: {} });
  assert.equal(L.learnActive(running(false)), true);
  assert.equal(L.learnActive(running(true)), false, 'the Query page shows no recorded examples over the person\'s model');
}

// ---- 6. the empty Model view leads to the Studio ----------------------------------------------------------------------------
{
  const pkg = JSON.parse(read('package.json'));
  const empty = pkg.contributes.viewsWelcome.find((w) => w.view === 'semanticusModel' && w.when === '!semanticus.hasModel');
  assert.match(empty.contents, /\[Open Studio\]\(command:semanticus\.openStudio\)/, 'Open Studio is on the empty Model view');
  assert.match(empty.contents, /\[Open a model\]\(command:semanticus\.openModel\)/, 'and so is Open a model');
  assert.doesNotMatch(empty.contents, /\u2014/, 'no em dash');
}

// ---- 7, 8, 11. Connect opens the model; the row names its sign-in; the dropdown reads plainly ------------------------------------
{
  const hub = read('webview', 'src', 'connectionshub.tsx');
  assert.match(hub, /const connectPicked = [^\n]*openByAddress\(c\)/, 'Connect on a model picker opens that model');
  assert.match(hub, /openByAddress\(\{ endpoint: endpoint\.trim\(\)/, 'the address form opens the model it remembers');
  const pick = hub.slice(hub.indexOf('const openByAddress'), hub.indexOf('const connectPicked'));
  assert.doesNotMatch(pick, /connectXmla/, 'and neither makes the model the Tests target');
  assert.match(hub, /SIGN_IN_WAYS\.find\(\(w\) => w\.value === authMode\)\?\.label/, 'a row with no account reads its sign-in ("Service identity"), not "account unknown"');
  assert.match(M.SIGN_IN_WAYS.map((w) => w.label).join('|'), /Browser sign-in\|Azure command line\|Service identity/);
  assert.ok(M.ENV_OPTIONS.every((o) => !/No label|Command-line/.test(o.label)), 'plain names');
  assert.equal(M.OPTION_STYLE.color, 'var(--sem-fg)', 'every option says its own colour, so one that works is not drawn grey');
  for (const f of ['connectpickers.tsx', 'connectionshub.tsx']) assert.match(read('webview', 'src', f), /style=\{OPTION_STYLE\}/, f);
}

// ---- 9. Save and test links the source it saved --------------------------------------------------------------------------------
{
  const hub = read('webview', 'src', 'connectionshub.tsx');
  const t = hub.slice(hub.indexOf('const testEndpoint'), hub.indexOf('const removeEndpoint'));
  assert.ok(t.indexOf("'setModelSqlEndpoint'") > t.indexOf('saveRecord()') && t.indexOf("'setModelSqlEndpoint'") < t.indexOf("'testSqlSource'"), 'a source saved by Save and test is linked to the model before it is tested');
}

// ---- 10. Check reports uses the model's sign-in --------------------------------------------------------------------------------
{
  const lin = read('webview', 'src', 'lineage.tsx');
  assert.doesNotMatch(lin, /useState\('azcli'\)/, 'the Azure command line is no longer the default');
  assert.match(lin, /defaultSignInMode\(modelSignIn\)/, 'the default is the sign-in the open model already uses');
  assert.doesNotMatch(read('webview', 'src', 'lineage-impact.tsx'), /signInMode = 'azcli'/, 'and a drawer with nothing told defaults to the browser sign-in');
}

// ---- 11. the starting-engine page (verifier v-batch1 notes on fix-fresh-connect) ---------------------------------------------------
{
  const fail = view({ engine: { state: 'failed', reason: 'Semanticus could not start its engine. Restart the engine, or open Show details for the reason.' } });
  assert.ok(testids(fail).includes('pick-engine-details'), 'a sentence that says Show details has a Show details to press');
  assert.ok(testids(fail).includes('pick-engine-restart'), 'and Restart engine, when it fits');
  const other = view({ engine: { state: 'failed', reason: 'Another VS Code window is using this model with an older version of Semanticus. Close that window, then open this folder again.' } });
  assert.ok(!testids(other).includes('pick-engine-restart'), 'one instruction: Restart engine is not offered beside "Close that window"');
  assert.ok(testids(other).includes('pick-ws-retry'), 'Try again stays');
  assert.equal(M.restartFits('Another VS Code window is using this model.'), false);
  assert.equal(M.restartFits('Semanticus could not start its engine.'), true);
  // The engine coming back is not a press: it must never open a sign-in window.
  const pk = read('webview', 'src', 'connectpickers.tsx');
  const upIdx = pk.indexOf('if (connected && engineState.current');
  assert.match(pk.slice(upIdx, upIdx + 120), /loadWorkspaces\(false\)/, 'the engine coming up re-asks without a press');
  assert.doesNotMatch(pk, /loadWorkspaces\(lastPressed\.current\)/, 'and so does the retry while starting');
  // An engine that exits at once is said at once.
  const ext = read('src', 'extension.ts');
  assert.match(ext, /exitedEarly/, 'the host notices the engine process exiting');
  const loop = ext.slice(ext.indexOf('let lastReason = \'engine.json not published yet\''), ext.indexOf('Engine pipe never accepted'));
  assert.match(loop, /exitedEarly !== undefined[^\n]*throw/, 'and stops waiting for a pipe that will never come');
}

// ---- 12. no folder: the last model is offered in one press (round 2 UAT N25) -----------------------------------------------------
{
  const R = load(join(root, 'src', 'recentModel.ts'));
  const recs = [
    { id: 'a', kind: 'xmla', endpoint: 'powerbi://x/Sales', database: 'Sales', modelName: 'Sales', authMode: 'serviceprincipal', lastUsedUtc: '2026-10-18T01:00:00Z' },
    { id: 'b', kind: 'file', endpoint: '/m/Costs.bim', modelName: 'Costs', lastUsedUtc: '2026-10-18T03:00:00Z' },
    { id: 'c', kind: 'localDesktop', endpoint: 'localhost:51000', modelName: 'Desktop', lastUsedUtc: '2026-10-18T09:00:00Z' },
  ];
  assert.deepEqual(R.recentModelOffer(recs, false), { kind: 'file', path: '/m/Costs.bim', modelName: 'Costs' }, 'the model opened last, never a Desktop copy that may not be running');
  assert.equal(R.recentModelOffer([recs[0]], false).kind, 'live');
  assert.equal(R.recentModelOffer([recs[0]], false).authMode, 'serviceprincipal', 'it reopens the way it was opened');
  assert.equal(R.recentModelOffer(recs, true), undefined, 'a window with a folder keeps its own remembered model, not the machine-wide last one');
  assert.equal(R.recentModelOffer([], false), undefined);
}

// ---- 13. the tour, the card and the names (round 2 UAT N18, N17, N32) --------------------------------------------------------------
{
  // One counter: the Plan's own first-open guide stays out while the Learn tour is playing, and counts as given.
  const guide = read('webview', 'src', 'plan-guide.tsx');
  assert.match(guide, /if \(learnPhase\(\) !== 'idle'\) \{ write\(\['whatChanged', 'planTour'\]\); return; \}/, 'no "1 of 7" card beside the tour bar\'s "step 1 of 5"');
  // No sign-in error inside the tour: the demo does not read workspaces, and a sign-in refusal is a calm line elsewhere.
  const pub = read('webview', 'src', 'publish-changes.tsx');
  assert.match(pub, /if \(inTour\) \{ setWorkspaces\(\{ kind: 'ready', items: \[\] \}\); return; \}/, 'the tour never asks for a sign-in');
  assert.match(pub, /isSignInError\(workspaces\.message\) \? 'Sign in to see your workspaces\.'/, 'a refused read says Sign in, not the engine sentence');
  // The Query step shows its recorded answer: the page goes to the Answer tab when a step names one.
  assert.match(read('webview', 'src', 'query.tsx'), /if \(tourExample !== null\) nb\.setTab\('answer'\)/, 'the demo Query step is filled');
  // One name for the service sign-in.
  assert.doesNotMatch(read('webview', 'src', 'copy.ts'), /Use a service account/, 'Service identity everywhere');
  assert.match(read('webview', 'src', 'copy.ts'), /value: 'serviceprincipal', label: 'Service identity'/);
  // The host's answer reaches the card: the bridge keeps the folder fields (a live run showed the card without them).
  assert.match(read('webview', 'src', 'bridge.ts'), /folder: typeof msg\.folder === 'string' \? msg\.folder : undefined, folderPath: typeof msg\.folderPath === 'string'/, 'the bridge passes the folder on');
  // The Connect card names the folder, with a copy press.
  const F = load(join(root, 'src', 'firstRun.ts'));
  assert.equal(F.folderForDisplay('/home/kane/Semanticus/Model', '/home/kane'), '~/Semanticus/Model');
  assert.equal(F.folderForDisplay('/data/proj', '/home/kane'), resolve('/data/proj'), 'a folder outside home is shown whole');
  assert.equal(F.folderForDisplay('/home/kane', '/home/kane'), resolve('/home/kane'), 'the home folder itself is shown whole, never "~" alone');
  assert.equal(F.saveStartFolder('/home/kane', () => true), join('/home/kane', 'Semanticus'), 'with no folder open the Save dialog starts in ~/Semanticus');
  assert.equal(F.saveStartFolder('/home/kane', () => false), '/home/kane', 'and in the home folder when that folder is not there yet');
  const card = readable(renderToStaticMarkup(h(load(join(webview, 'src', 'firstrun.tsx')).FirstRunCard,
    { stage: 'done', mode: 'assistant', onMode() {}, onConnect() {}, onClose() {}, error: null, note: null, folder: { shown: '~/Semanticus/Model', path: '/home/kane/Semanticus/Model' } })));
  assert.match(card, /Open Claude Code in this folder: ~\/Semanticus\/Model Copy Then say:/, 'the path is on the card, with a copy press');
}

console.log('first-start ok');
