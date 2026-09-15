// Kane's 1.2.0 P1 (Yoga, 2026-09-15): Help > Something wrong? > Restart engine while a live model was open
// ended in "Semanticus could not start its engine. ... Could not find a part of the path
// '/tmp/semanticus-live/a98e81bc/Contoso.bim'." and a Studio that was not connected, with no way back.
//
// Two defects compounded, and this file pins both shut.
//
//   1. The engine start and the model reopen shared ONE try/catch (connectEngineAttempt). The engine spawn
//      never carries a model path at all — ownerServeArgs is `serve --workspace <ws> --ui-challenge-stdin
//      --license-stdin` — so the engine started fine and it was the separate `open` request that threw. That
//      throw landed in the START catch, which disposed a healthy connection, said the engine could not start,
//      and handed the dead path straight back to scheduleEngineReconnect forever.
//   2. A model opened through the Studio / Connections hub webview never updated the remembered model, so the
//      remembered path stayed an EARLIER session's live working copy, which the engine's own snapshot sweep
//      had already deleted.
//
// The contract now: the engine always starts, a model that cannot be reopened is an OPEN failure reported
// after the engine is up, a live model is reopened through the live open rather than its temp working copy,
// and a webview that finds no engine offers a way back instead of an empty page.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const app = readFileSync(resolve(root, 'webview', 'src', 'App.tsx'), 'utf8');
const bridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const delivery = readFileSync(resolve(root, 'src', 'licenseDelivery.ts'), 'utf8');
const manifest = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));
const harness = readFileSync(resolve(root, 'tools', 'uishot', 'harness.html'), 'utf8');

function slice(text, startPattern, endPattern, what) {
  const start = text.search(startPattern);
  assert.notEqual(start, -1, `could not find the start of ${what}`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(endPattern);
  return end === -1 ? rest : rest.slice(0, end + 1);
}

// ---- (a) the proven cause: the engine spawn carries no model, so a model can never fail a start ----------

assert.match(delivery,
  /export function ownerServeArgs[\s\S]*?return \['serve', '--workspace', workspace, '--ui-challenge-stdin', '--license-stdin'\];/,
  'the engine is spawned with no model argument; if that ever changes, a missing model really could fail a start');

const attempt = slice(source, /async function connectEngineAttempt/, /\n(?:async )?function |\n\/\/\/ /, 'connectEngineAttempt');
assert.doesNotMatch(attempt, /sendRequest<OpenResult>\('open'/,
  'the model reopen must not sit inside the start try/catch: an open failure there disposes a healthy connection and lies that the engine could not start');
assert.doesNotMatch(attempt, /LAST_MODEL_PATH_KEY/,
  'connectEngineAttempt must not read the remembered path itself; the reopen owns it and runs after the engine is up');
assert.match(attempt, /await reopenLastModel\(/,
  'the reopen runs as its own step once the connection is established');

// The start catch is what says "could not start"; it must be reachable only from the connect block.
assert.match(attempt,
  /status\.text = '\$\(error\) Semanticus: engine error';[\s\S]{0,400}?could not start its engine/,
  'the start catch keeps its own honest message for a real start failure');

// ---- (b) the reopen is an open failure, never a start failure -------------------------------------------

const reopen = slice(source, /async function reopenLastModel/, /\n(?:async )?function |\n\/\/\/ /, 'reopenLastModel');
assert.doesNotMatch(reopen, /scheduleEngineReconnect/,
  'a model that will not reopen must NOT put the engine into a reconnect loop; the engine is already up');
assert.doesNotMatch(reopen, /conn = undefined/,
  'a model that will not reopen must NOT throw the live connection away');
assert.doesNotMatch(reopen, /could not start its engine/,
  'an open failure must never be reported as a start failure');

// ---- (c) a live model comes back through the live open, not its temp working copy ------------------------

assert.match(source,
  /interface LastModel \{[\s\S]*?kind: 'file' \| 'live' \| 'localDesktop';/,
  'the remembered model records WHAT it is, because a live model\'s source path is a temp working copy the engine sweeps');
assert.match(source,
  /function describeSession\([\s\S]*?info\.liveEndpoint[\s\S]*?info\.liveDatabase/,
  'the remembered descriptor carries the live endpoint and database off SessionInfo');
assert.match(source, /interface LastModel \{[\s\S]*?authMode\?: string;/,
  'and the sign-in method, so a reopen still knows how to sign in if the connection record is forgotten');
const liveOpen = slice(source, /async function openLiveTarget/, /\n(?:async )?function |\n\/\/\/ /, 'openLiveTarget');
assert.match(liveOpen, /sendRequest<OpenResult>\('openLive'/,
  'a remembered live model is reopened through the live open');
assert.match(liveOpen, /listConnections/,
  'the live reopen takes its sign-in details from the saved connection record, not from guesses');
assert.match(liveOpen, /sendRequest<OpenResult>\('openLocal'/,
  'a remembered running local model is reopened through the local open');
assert.match(reopen, /openLiveTarget\(connected, target, opts\.restarted\)/,
  'the discard permission IS whether this was a restart; it is never hardcoded');
// The permission is never invented anywhere else in the reopen.
assert.equal((reopen.match(/openLiveTarget\(/g) || []).length, 1,
  'exactly one place in the reopen may open a live target');

// ---- (d) a file that is gone clears the memory and says so in plain words --------------------------------

assert.match(reopen, /fs\.existsSync\(/,
  'a remembered file path is checked before it is handed to the engine');
assert.match(source, /function forgetLastModel\([\s\S]*?LAST_MODEL_PATH_KEY, undefined[\s\S]*?LAST_MODEL_KEY, undefined/,
  'a remembered model that is gone is forgotten, both the path and the descriptor, so the next start is clean');
assert.match(reopen, /forgetLastModel\(/,
  'the reopen forgets a model it proved is gone');

assert.match(source,
  /'The engine restarted, but the model you had open could not be reopened: '/,
  'the restart wording is the plain sentence Kane was promised');
assert.match(source,
  /'Semanticus started, but the model you had open could not be reopened: '/,
  'a cold start says "started", not "restarted"; the same failure, honestly named');
assert.match(source, /'\. Open a model to continue\.'/,
  'and the tail tells the person what to do');
assert.match(source, /\['Open a model', 'Show details'\]/,
  'the notice offers a way back and a way to see the detail');
// A published model we chose not to sign in to on our own gets a sign-in offered instead of being abandoned.
assert.match(source, /'\. Sign in to open it again, or open another model\.'/,
  'when the way back is a sign-in, the sentence says so');
assert.match(source, /\['Sign in', 'Open a model', 'Show details'\]/, 'and Sign in is the first action');
assert.match(source, /function reopenFailureReason[\s\S]*?that file is no longer on this computer/,
  'the reason is translated into plain words');

// The raw path belongs in the output channel, never in the first line the person reads.
const notice = slice(source, /async function reportReopenFailure/, /\n(?:async )?function |\n\/\/\/ /, 'reportReopenFailure');
// The raw text is what named '/tmp/semanticus-live/a98e81bc/Contoso.bim' in Kane's toast. It goes to the output
// channel and nowhere else, so the ONLY thing between `message =` and the call that shows it is plain words.
const shown = notice.slice(notice.indexOf('const lead ='), notice.indexOf('show' + 'WarningMessage'));
assert.ok(shown.length > 0, 'reportReopenFailure must build its message before it shows it');
assert.doesNotMatch(shown, /\berr\b/,
  'the sentence a person reads must not splice the raw engine error, which carries the full path');
assert.match(notice, /out\.appendLine\('Reopening the last model failed: '/, 'the raw detail goes to the output channel');
assert.match(notice, /out\.show\(true\)/, '"Show details" opens the output channel');

// ---- (e) activation obeys the same rule -----------------------------------------------------------------

assert.match(source, /async function connectEngineAttempt\([^)]*restarted = false\)/,
  'activation and the background reconnect are NOT restarts, so the flag defaults off and no sign-in opens by itself');
assert.match(attempt, /await reopenLastModel\(context, connected, \{ restarted \}\)/,
  'activation reopens through the SAME guarded path as a restart, so a stale remembered model can never block a start');
assert.match(source, /function scheduleEngineReconnect[\s\S]*?void connectEngine\(context, true\);/,
  'the background reconnect stays quiet and does not claim to be a restart');
const restart = slice(source, /async function restartEngineCmd/, /\n(?:async )?function |\n\/\/\/ /, 'restartEngineCmd');
assert.match(restart, /await connectEngine\(context, false, true\);/,
  'restart tells the reopen it WAS a restart, which is what lets a live model come back through the live open');

// ---- (f) a model opened through the webview is remembered ------------------------------------------------
// This is why Kane's remembered path pointed at a DIFFERENT model: the hub open never wrote it.

const relay = slice(source, /if \(modelSwapped\) \{/, /panel\.webview\.postMessage\(\{ type: 'rpcResult'/, 'the webview model-swap block');
assert.match(relay, /rememberLastModel\(/,
  'a model opened from Studio or the Connections hub must update the remembered model, or a restart reopens a stale one');
assert.match(relay, /watchModelDisk\(/,
  'and the disk watcher must follow the model the person actually opened');

// ---- (g) the webview offers a way back instead of a dead page --------------------------------------------

assert.match(source, /const WEBVIEW_HOST_COMMANDS = new Set\(\[[\s\S]*?'semanticus\.openModel'[\s\S]*?\]\);/,
  'the recovery panel\'s "Open a model" runs through the existing host command allowlist');
assert.match(source, /const WEBVIEW_HOST_COMMANDS = new Set\(\[[\s\S]*?'semanticus\.showOutput'[\s\S]*?\]\);/,
  'and so does "Show details"');
assert.ok(manifest.contributes.commands.some((c) => c.command === 'semanticus.showOutput'),
  'semanticus.showOutput is a real contributed command, not a webview-only id');
assert.match(source, /registerCommand\('semanticus\.showOutput'/, 'and it is registered');

assert.match(source, /postToPanels\(\{ type: 'engineState', connected: false \}\)/,
  'the host tells both panels when the engine is gone, so Studio does not have to guess from a failed request');
assert.match(source, /postToPanels\(\{ type: 'engineState', connected: true \}\)/,
  'and tells them when it is back');
assert.match(bridge, /export function onEngineState\(/,
  'the bridge relays the engine state to the views');
assert.match(bridge, /msg\.type === 'engineState'/, 'and handles the message');

assert.match(app, /Semanticus is not connected to its engine\./,
  'the disconnected Studio says what is wrong in one plain sentence');
assert.match(app, /function EngineDown\(/, 'the recovery panel is its own component');
const panel = slice(app, /function EngineDown\(/, /\nfunction /, 'EngineDown');
assert.match(panel, /Restart engine/, 'Restart engine is offered');
assert.match(panel, /Open a model/, 'Open a model is offered');
assert.match(panel, /Show details/, 'Show details is offered');
assert.match(panel, /runHostCommand\('semanticus\.restartEngine'\)/, 'Restart engine runs the real command');
assert.match(panel, /runHostCommand\('semanticus\.openModel'\)/, 'Open a model runs the real command');
assert.match(panel, /runHostCommand\('semanticus\.showOutput'\)/, 'Show details runs the real command');
assert.doesNotMatch(panel, /rpc\(/, 'the panel cannot reach the engine, so it must not try');

// The panel replaces the page content; the Shell (and its header) stays around it.
assert.match(app, /\{engineDown \? \(\s*<EngineDown \/>\s*\) : !session\?\.sessionId \?/,
  'the recovery panel replaces the page body inside the Shell, so the header stays');

// The webview must also be able to work it out on its own: a Studio that mounts while the engine is already
// down never receives the transition message, only a rejected request.
assert.match(app, /ENGINE_NOT_CONNECTED/, 'a Studio that mounts cold recognises the host\'s not-connected answer');
assert.match(app, /const ENGINE_NOT_CONNECTED = 'Engine not connected\.';/,
  'and it matches the host\'s literal exactly');
assert.match(source, /error: 'Engine not connected\.'/, 'which is the literal the host actually sends');

// ---- (h) the review harness can show the state --------------------------------------------------------

assert.match(harness, /engine=down|engineDown/,
  'the screenshot harness has a disconnected fixture, so this state is reviewable without a live engine');


// ---- (i) Astra spot-review-5, findings 1 and 2: BEHAVIOURAL ---------------------------------------------
// Source-shape assertions let both of these through, so these run the real functions instead. The bench pulls
// the actual declarations out of the syntax tree and drives them with mocked storage, RPC and file existence
// (test/reopen-behaviour.mjs). Astra's probe is the origin of the technique and of both failures.

const { makeBench, LIVE_TARGET, LOCAL_TARGET, liveSession, fileSessionFromSnapshot } = await import('./reopen-behaviour.mjs');

// The one writer. A bare path can never name a live model, so no caller may hand one in.
assert.doesNotMatch(source, /function rememberLastModelPath\(/,
  'rememberLastModelPath always wrote { kind: file } and its callers did not honour its comment; every call site derives from the session instead');
assert.match(source, /function describeSession\(/, 'the descriptor is derived from a full SessionInfo in one place');

// (1) A native Open of a live model must not record it as a file.
{
  const bench = makeBench();
  bench.sandbox.currentSession = liveSession();
  await bench.sandbox.bindGridAndTree('SM_NFM', { refresh: () => {} });
  const got = bench.remembered();
  assert.equal(got?.kind, 'live', 'a native open of a live model must stay live, not become its temp snapshot path');
  assert.equal(got?.endpoint, LIVE_TARGET.endpoint, 'and must keep the endpoint');
  assert.equal(got?.database, LIVE_TARGET.database, 'and the dataset');
}

// (2) Ctrl+S on a live model must not erase its live reopen target.
{
  const bench = makeBench();
  bench.sandbox.currentSession = liveSession();
  await bench.sandbox.saveCommand();
  const got = bench.remembered();
  assert.equal(got?.kind, 'live', 'an in-place save writes the snapshot back; it does not turn a live model into a file');
  assert.equal(got?.endpoint, LIVE_TARGET.endpoint, 'and the endpoint survives the save');
}

// (3) A cold start may open the cached bytes offline, but the TARGET stays live.
{
  const bench = makeBench({ pathExists: () => true, sessionAfterOpen: () => fileSessionFromSnapshot() });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  assert.deepEqual(bench.openCalls().map((c) => c.method), ['open'],
    'a cold start with the snapshot present opens it offline: no network, no sign-in');
  const got = bench.remembered();
  assert.equal(got?.kind, 'live', 'opening the cached bytes offline must not discard the live reopen target');
  assert.equal(got?.endpoint, LIVE_TARGET.endpoint, 'the endpoint is what the next restart needs');
}

// (4) A background reconnect must never be able to pop a sign-in window.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  // NOT "ask the engine nicely for a silent open" — make no credential-bearing call at all. A headless lane
  // on this branch showed a real Microsoft sign-in page open during a cold start even with the engine's
  // non-interactive origin requested, and I could not attribute it. An extension that must not prompt does
  // not get to depend on somebody else's boundary holding.
  assert.deepEqual(bench.openCalls(), [],
    'a background reopen must make NO live open call; it cannot pop a sign-in it never asks for');
  // Astra, recheck 6: these used to read a stub's bookkeeping. They now read the sentence and the buttons a
  // person would actually be shown, driven through the real reportReopenFailure.
  const notice = bench.notices.at(-1);
  assert.match(notice.message, /opening it again means signing in, and only you can do that/,
    'and it says plainly that the model is still there but needs a person');
  assert.deepEqual(notice.actions, ['Sign in', 'Open a model', 'Show details'],
    'offering Sign in as the explicit human action');
  assert.equal(bench.remembered()?.kind, 'live', 'while keeping the target, which has not gone anywhere');
}

// (5) A restart IS a human action, so it may sign in.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  const live = bench.openCalls().find((c) => c.method === 'openLive');
  assert.equal(live?.args[9], 'human', 'the person just asked for this restart, so a sign-in is allowed');
  assert.equal(live?.args[2], 'devicecode', 'the sign-in method comes from the saved connection record, not a guess');
}

// (6) A RESTART that the engine refuses keeps the record. (Astra, recheck 6: this case used to claim it
// exercised a refused silent reopen, which cold recovery no longer even attempts. It now names what it does.)
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  bench.sandbox.openThrows = new Error('AADSTS50076: the account needs a second factor.');
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  assert.equal(bench.remembered()?.kind, 'live',
    'a model that could not be signed in to still EXISTS; forgetting it would lose the person their place');
  const notice = bench.notices.at(-1);
  assert.match(notice.message, /could not be reopened/, 'and the person is told, in plain words');
}

// (7) A file that is genuinely gone is still forgotten, and offers no sign-in.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed({ kind: 'file', path: '/fixture/deleted.bim' });
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  assert.equal(bench.remembered(), null, 'a missing file is forgotten');
  assert.equal(bench.rememberedPath(), null, 'both keys');
  const notice = bench.notices.at(-1);
  assert.deepEqual(notice.actions, ['Open a model', 'Show details'],
    'a missing local file has nothing to sign in to');
}


// (8) THE HOLE THE OFFLINE-FIRST PATH OPENS, found by driving the real host.
// After a cold start opens a live model's cached snapshot, the engine session is a plain file session: it has
// no LiveOrigin, because those bytes were opened as a file. Any later re-derive therefore sees "not live" and
// would demote the target all over again. An in-place save of the SAME bytes must not do that.
{
  const bench = makeBench({ pathExists: () => true, sessionAfterOpen: () => fileSessionFromSnapshot() });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  assert.equal(bench.remembered()?.kind, 'live', 'precondition: the cold start kept the live target');
  await bench.sandbox.saveCommand();
  assert.equal(bench.remembered()?.kind, 'live',
    'saving the cached bytes back to the same path does not turn the live model into a file');
  assert.equal(bench.remembered()?.endpoint, LIVE_TARGET.endpoint, 'and the endpoint survives that save too');
}

// (9) A deliberate save to a DIFFERENT, durable destination is the one thing that does convert it.
{
  const bench = makeBench({ pathExists: () => true, sessionAfterOpen: () => fileSessionFromSnapshot() });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  bench.sandbox.currentSession = fileSessionFromSnapshot({ source: '/fixture/chosen/Model.bim' });
  await bench.sandbox.saveCommand();
  assert.equal(bench.remembered()?.kind, 'file',
    'a save that lands the model somewhere else IS a file from then on; only sameness of bytes preserves the live target');
  assert.equal(bench.remembered()?.path, '/fixture/chosen/Model.bim', 'and it points at where the person put it');
}


// ---- (j) Astra recheck 6 ---------------------------------------------------------------------------------
// Both of these are in code this lane added, and both were invisible to the sequential cases above because
// they need a SECOND thing to happen while the first is still in flight.

// (10) HER REPRODUCTION, FIRST. The Sign in notice stays actionable while the person carries on working. By
// the time it is answered, a DIFFERENT model may be open with unsaved edits. Answering must not throw that
// work away: openLiveTarget sent discardUnsaved: true unconditionally, and UnsavedWorkGuard.ThrowIfBlockedAsync
// (Semanticus.Engine/UnsavedWorkGuard.cs:18) returns immediately when that flag is true, so nothing downstream
// would have stopped it either.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  bench.sandbox.answerNotice = null;                       // the warning is left sitting there
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  const notice = bench.notices.at(-1);
  assert.ok(notice.actions.includes('Sign in'), 'precondition: Sign in was offered');

  // Meanwhile the person opens something else and edits it.
  bench.sandbox.currentSession = { sessionId: 'other-session', revision: 7, modelName: 'Newest',
    source: '/fixture/newest.bim', liveBound: false, hasUnsavedChanges: true };
  bench.sandbox.unsavedAnswer = undefined;                 // they press Cancel on the Save / Discard prompt
  await bench.sandbox.signInAndReopen(bench.connection, LIVE_TARGET);
  assert.deepEqual(bench.openCalls(), [],
    'Cancel on the unsaved-work prompt means the sign-in does not happen at all');

  // And when they do choose, the choice is what travels; a notice never carries restartic discard permission.
  bench.sandbox.unsavedAnswer = 'Discard';
  await bench.sandbox.signInAndReopen(bench.connection, LIVE_TARGET);
  const live = bench.openCalls().find((c) => c.method === 'openLive');
  assert.ok(live, 'Discard lets it through');
  assert.equal(live.args[10], true, 'and carries the discard the person just granted');
}

// (11) The same action, with unsaved work the person chooses to SAVE first.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  bench.sandbox.currentSession = { sessionId: 'other-session', revision: 7, modelName: 'Newest',
    source: '/fixture/newest.bim', liveBound: false, hasUnsavedChanges: true };
  bench.sandbox.unsavedAnswer = 'Save';
  await bench.sandbox.signInAndReopen(bench.connection, LIVE_TARGET);
  assert.ok(bench.calls.some((c) => c.method === 'save'), 'Save means the work is written before the swap');
  const live = bench.openCalls().find((c) => c.method === 'openLive');
  assert.equal(live?.args[10], false, 'and nothing is discarded, because nothing needed to be');
}

// (12) A clean session needs no prompt at all.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  bench.sandbox.currentSession = { sessionId: 's', revision: 1, modelName: 'Clean', source: '/fixture/clean.bim',
    liveBound: false, hasUnsavedChanges: false };
  await bench.sandbox.signInAndReopen(bench.connection, LIVE_TARGET);
  const live = bench.openCalls().find((c) => c.method === 'openLive');
  assert.equal(live?.args[10], false, 'no unsaved work, so no discard permission is invented');
}

// (13) MEDIUM: late enrichment must not overwrite a NEWER remembered model.
// rememberLastModel awaits the saved connection record for authMode before writing. The Studio relay does not
// await rememberLastModel, so two can overlap; without an ordering check the slower one wins on write.
{
  const bench = makeBench({ pathExists: () => false });
  let release;
  bench.sandbox.holdListConnections = new Promise((r) => { release = r; });
  const slow = bench.sandbox.rememberLastModel(liveSession());           // model A: live, needs enrichment
  await bench.sandbox.rememberLastModel({ sessionId: 'b', revision: 1, modelName: 'Newest',
    source: '/fixture/newest.bim', liveBound: false });                  // model B lands first: a plain file
  assert.equal(bench.remembered()?.path, '/fixture/newest.bim', 'precondition: B is what is remembered');
  release();
  await slow;
  assert.equal(bench.remembered()?.path, '/fixture/newest.bim',
    'a late reply for model A must not resurrect A over the newer B');
  assert.equal(bench.remembered()?.kind, 'file', 'nor restore its live endpoint');
}

// (14) And it must not resurrect a model that was FORGOTTEN while it was in flight.
{
  const bench = makeBench({ pathExists: () => false });
  let release;
  bench.sandbox.holdListConnections = new Promise((r) => { release = r; });
  const slow = bench.sandbox.rememberLastModel(liveSession());
  bench.sandbox.forgetLastModel();
  assert.equal(bench.remembered(), null, 'precondition: forgotten');
  release();
  await slow;
  assert.equal(bench.remembered(), null,
    'a model proved gone stays gone; a late enrichment reply cannot bring it back');
}


// ---- (k) Astra recheck 7 --------------------------------------------------------------------------------
// A regression I introduced in round 2. Threading the discard permission through openLiveTarget made the
// local-Desktop call carry it too, and the one caller hardcoded `true`. A running Desktop model is the one
// live kind an automatic recovery IS allowed to reopen, so that branch handed out discard permission nobody
// granted. Before round 2 the local call omitted the argument entirely and so used false.
//
// Paired on purpose: the counting assertion further up proves there is one call site, not which branches
// reach it, so the branches are exercised instead.

// (15) Cold local recovery: reopen, but discard NOTHING.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LOCAL_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  const local = bench.openCalls().find((c) => c.method === 'openLocal');
  assert.ok(local, 'a running Desktop model needs no credential, so recovery may still reopen it');
  assert.equal(local.args[2], false,
    'but an automatic reopen has asked nobody anything, so it must not carry discard permission');
  assert.deepEqual(bench.prompts, [], 'and it does not prompt either; it simply claims nothing');
}

// (16) The explicit restart control: the permission restartEngineCmd already settled does travel.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LOCAL_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  const local = bench.openCalls().find((c) => c.method === 'openLocal');
  assert.equal(local?.args[2], true, 'a restart settled unsaved work before it got here');
}

// (17) The same pairing on the published side, so neither kind can drift.
{
  const bench = makeBench({ pathExists: () => false });
  bench.seed(LIVE_TARGET);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: true });
  assert.equal(bench.openCalls().find((c) => c.method === 'openLive')?.args[10], true,
    'a restart of a published model carries the same settled permission');
}

// (18) Nothing an automatic reopen does may set the flag, whichever kind it is.
for (const target of [LOCAL_TARGET, LIVE_TARGET, { kind: 'file', path: '/fixture/some.bim' }]) {
  const bench = makeBench({ pathExists: () => true });
  bench.seed(target);
  await bench.sandbox.reopenLastModel(bench.context, bench.connection, { restarted: false });
  for (const call of bench.openCalls()) {
    const flag = call.method === 'openLive' ? call.args[10] : call.args[2];
    assert.notEqual(flag, true, `an automatic ${target.kind} reopen must never discard unsaved work`);
  }
}

console.log('engine restart recovery tests passed');
