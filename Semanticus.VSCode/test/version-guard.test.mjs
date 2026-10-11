// Kane, 25 September, on the Yoga. He installed a new build over the same version, 1.2.1. The Studio page was new,
// but the engine behind it was the OLD process: it had started before the install and was never replaced. Workflows,
// "Describe it in words", then showed the engine's raw reply: "No method by the name 'describeWorkflowSteps' is
// found." Nobody can act on that, and it breaks the copy rules.
//
// The rule this file holds: a newer page and an older engine (or the reverse) never reach the person as a raw RPC
// error. A missing method is read as "the two halves are different builds" and becomes one plain sentence, and the
// window restarts the engine itself when it owns that engine and nothing unsaved is at stake.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ENGINE_UPDATED_TEXT, ENGINE_UPDATED_RESTART, ENGINE_RESTARTED_TEXT, isMethodNotFound, plainRpcError, decideOutdatedEngine,
  guardEngineRequests,
} from '../out/versionGuard.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// Line-ending proof: a Windows checkout (core.autocrlf) has CRLF, and every slice below ends at /\n}\n/.
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8').replace(/\r\n/g, '\n');

// The exact error Kane saw, as vscode-jsonrpc hands it over from the engine's StreamJsonRpc door.
const kanes = Object.assign(new Error("No method by the name 'describeWorkflowSteps' is found."), { code: -32601 });

// ---- (1) the copy ----------------------------------------------------------------------------------------------
assert.equal(ENGINE_UPDATED_TEXT, 'Semanticus was updated. Close VS Code and your assistant, then open them again to finish.');
assert.equal(ENGINE_UPDATED_RESTART, 'Restart');
for (const line of [ENGINE_UPDATED_TEXT, ENGINE_RESTARTED_TEXT]) {
  assert.ok(!/\u2014|\u2013/.test(line), 'no em or en dashes');
  assert.ok(!/method|rpc|engine|build/i.test(line), 'no engine jargon reaches the person');
  assert.match(line, /^Semanticus was updated/, 'both lines say what happened first');
}

// ---- (2) what counts as "a different build answered" -------------------------------------------------------------
assert.equal(isMethodNotFound(kanes), true, 'the JSON-RPC method-not-found code');
assert.equal(isMethodNotFound(new Error("No method by the name 'x' is found.")), true,
  'the engine sentence alone, when a hop dropped the code');
assert.equal(isMethodNotFound({ message: 'Unhandled method describeWorkflowSteps', code: -32601 }), true);
assert.equal(isMethodNotFound(new Error('The model has no table named Sales.')), false, 'an ordinary failure is not a build mismatch');
assert.equal(isMethodNotFound({ code: -32603, message: 'Internal error' }), false);
assert.equal(isMethodNotFound(undefined), false);
assert.equal(isMethodNotFound('No method by the name \'y\' is found.'), true, 'a bare string error too');

// ---- (3) the mapping ------------------------------------------------------------------------------------------
assert.equal(plainRpcError(kanes), ENGINE_UPDATED_TEXT, "Kane's error becomes the plain sentence");
assert.equal(plainRpcError(new Error('The model has no table named Sales.')), 'The model has no table named Sales.',
  'every other error passes through untouched');
assert.equal(plainRpcError('plain text'), 'plain text');
assert.equal(plainRpcError(kanes, 'restart'), ENGINE_RESTARTED_TEXT, 'when the window restarts the engine, the page says to try again');
assert.equal(plainRpcError(kanes, 'offer-restart'), ENGINE_UPDATED_TEXT);
assert.equal(plainRpcError(kanes, 'tell'), ENGINE_UPDATED_TEXT);
assert.equal(plainRpcError(new Error('other'), 'restart'), 'other', 'the restart line is only ever for a missing method');

// ---- (4) what the window does about it -----------------------------------------------------------------------
const OLD = 'build-old', NEW = 'build-new';
assert.equal(decideOutdatedEngine({ expected: NEW, reported: OLD, ownedByThisWindow: true, unsaved: false }), 'restart',
  'an engine this window owns, from another build, with nothing unsaved, is restarted without asking');
assert.equal(decideOutdatedEngine({ expected: NEW, reported: '', ownedByThisWindow: true, unsaved: false }), 'restart',
  'an engine too old to say its build is another build');
assert.equal(decideOutdatedEngine({ expected: NEW, reported: OLD, ownedByThisWindow: true, unsaved: true }), 'offer-restart',
  'unsaved work is never thrown away: the person gets the sentence and a Restart button that asks about saving');
assert.equal(decideOutdatedEngine({ expected: NEW, reported: OLD, ownedByThisWindow: true, unsaved: undefined }), 'offer-restart',
  'an engine that could not be asked is treated as holding unsaved work');
assert.equal(decideOutdatedEngine({ expected: NEW, reported: OLD, ownedByThisWindow: false, unsaved: false }), 'tell',
  'an engine another process owns is never restarted from here: the sentence only');
assert.equal(decideOutdatedEngine({ expected: NEW, reported: NEW, ownedByThisWindow: true, unsaved: false }), 'tell',
  'the engine matches the installed files, so this window is the stale half: restarting the engine would not help');
assert.equal(decideOutdatedEngine({ expected: undefined, reported: OLD, ownedByThisWindow: true, unsaved: false }), 'offer-restart',
  'a window that cannot read its own build still offers the Restart it can do');

// ---- (5) the guard on the connection ---------------------------------------------------------------------------
{
  const seen = [];
  const fake = {
    sendRequest: async (method) => {
      if (method === 'describeWorkflowSteps') throw kanes;
      if (method === 'broken') throw new Error('The model has no table named Sales.');
      return `ok:${method}`;
    },
  };
  const guarded = guardEngineRequests(fake, (method) => seen.push(method));
  assert.equal(guarded, fake, 'the guard patches the connection in place, so identity checks on it still hold');
  assert.equal(await guarded.sendRequest('sessionInfo'), 'ok:sessionInfo', 'a good call is untouched');
  await assert.rejects(guarded.sendRequest('broken'), /no table named Sales/, 'an ordinary error is untouched');
  await assert.rejects(guarded.sendRequest('describeWorkflowSteps', 'words'), (e) => {
    assert.equal(e.message, ENGINE_UPDATED_TEXT, 'the person reads the plain sentence');
    assert.equal(e.code, -32601, 'callers that test the code still can');
    return true;
  });
  assert.deepEqual(seen, ['describeWorkflowSteps'], 'the window hears about the mismatch once per failing call');
}

// ---- (6) the wiring in extension.ts ------------------------------------------------------------------------------
function slice(text, startPattern, endPattern, what) {
  const start = text.search(startPattern);
  assert.notEqual(start, -1, `could not find the start of ${what}`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(endPattern);
  return end === -1 ? rest : rest.slice(0, end + 1);
}
const attempt = slice(source, /async function connectEngineAttempt\(/, /\n}\n/, 'connectEngineAttempt');
const adoptAt = attempt.indexOf('await adoptEngine(connected)');
const guardAt = attempt.indexOf('guardEngineRequests(connected');
assert.ok(guardAt > adoptAt, 'every request on the live connection is guarded, installed after the build probes ran');
assert.ok(guardAt < attempt.indexOf('conn = connected'), 'the guard is on before the connection is handed out');

// The Studio bridge: every webview call's error goes through the mapping, never String(e.message) raw.
const bridge = slice(source, /if \(msg\?\.type !== 'rpc'\) return;/, /\n    };\n}/, 'the Studio rpc bridge');
assert.match(bridge, /error: await studioRpcError\(e, caller\)/, 'the Studio bridge maps a missing method to the plain sentence');
const studioError = slice(source, /async function studioRpcError\(/, /\n}\n/, 'studioRpcError');
assert.match(studioError, /plainRpcError\(e, await outdatedDecision\.action\)/, 'the page line follows the one decision made for that connection');
assert.doesNotMatch(bridge, /rpcResult', id: msg\.id, error: String\(e/, 'no raw engine error reaches the Studio page');

const handler = slice(source, /function handleOutdatedEngine\(/, /\n}\n/, 'handleOutdatedEngine');
assert.match(handler, /decideOutdatedEngine\(/, 'the window decides with the one rule tested above');
assert.match(handler, /expectedEngineBuild\(\)/, 'it compares against the files installed now');
assert.match(handler, /runningEngineBuild\(/, 'and against the build the engine says it is');
assert.match(handler, /restartEngineCmd\(/, 'a restart goes through the one restart path, which protects unsaved work');
assert.match(handler, /ENGINE_UPDATED_RESTART/, 'the Restart button is offered where the window can act');

// ---- (7) the Studio page's own net -------------------------------------------------------------------------------
// Kane's exact path can also be a NEW page beside an OLD extension host (files replaced, window not reloaded): that
// host forwards the raw engine text, so the page maps it too, with the same sentence.
{
  const page = await import('../webview/src/engineupdated.mjs');
  assert.equal(page.ENGINE_UPDATED_TEXT, ENGINE_UPDATED_TEXT, 'the page and the extension say the same sentence');
  assert.equal(page.studioErrorText("No method by the name 'describeWorkflowSteps' is found."), ENGINE_UPDATED_TEXT);
  assert.equal(page.studioErrorText('Unhandled method describeWorkflowSteps'), ENGINE_UPDATED_TEXT);
  assert.equal(page.studioErrorText('The model has no table named Sales.'), 'The model has no table named Sales.');
  const pageBridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8').replace(/\r\n/g, '\n');
  assert.match(pageBridge, /p\.reject\(new Error\(studioErrorText\(msg\.error\)\)\)/, 'every engine error the page receives goes through the map');
}

// ---- (8) the Properties grid re-reads after an engine switch (coordinator, 25 Sep) -------------------------------
// Lane o: Restart, Discard, new engine. The tree and the status bar said clean and the file on disk was clean, but the
// grid still showed the discarded Description, because the connect path only pushed the grid when it was EMPTY
// (showModelIfEmpty). Any connect can be a different engine holding a different model, so the grid re-reads.
{
  const tail = slice(source, /THE ENGINE IS UP\. Everything below is about a MODEL/, /\n}\n/, 'the model half of connectEngineAttempt');
  assert.match(tail, /propGrid\.reload\(info\?\.modelName\)/, 'every successful connect re-reads the grid from the engine it joined');
  assert.doesNotMatch(tail, /showModelIfEmpty\(/, 'an already-filled grid is never left showing the previous engine');
  const reload = slice(source, /async reload\(name\?: string\): Promise<void> \{/, /\n    }\n/, 'PropertyGridProvider.reload');
  assert.match(reload, /if \(!this\.refs\.length\) \{ await this\.showModel\(\); return; \}/, 'an empty grid shows the model, as before');
  assert.match(reload, /await this\.push\(\);/, 'a filled grid fetches its selection again from the new engine');
}

// ---- (9) the older-engine notice goes away once the switch is done (coordinator, 25 Sep) -------------------------
// It was a showWarningMessage, which the VS Code API cannot close, so it sat in the notification centre after the new
// engine was running, still asking the person to save. It is now a notification Semanticus owns and settles.
{
  const { SwitchNotices, withSaveLink, OLDER_BUILD_SAVE_COMMAND } = await import('../out/versionGuard.js');
  const notices = new SwitchNotices();
  let closed = 0;
  const a = notices.hold().then(() => closed++);
  const b = notices.hold().then(() => closed++);
  assert.equal(notices.open, 2);
  notices.settle();
  await Promise.all([a, b]);
  assert.equal(closed, 2, 'one completed switch closes every notice that was waiting for it');
  assert.equal(notices.open, 0);
  notices.settle();   // nothing waiting: harmless
  assert.equal(OLDER_BUILD_SAVE_COMMAND, 'semanticus.saveOlderEngine');
  const line = withSaveLink('Save them and Semanticus will switch to the new engine.');
  assert.equal(line, 'Save them and Semanticus will switch to the new engine. [Save](command:semanticus.saveOlderEngine)');
  assert.ok(!/\u2014|\u2013/.test(line));

  const offer = slice(source, /function offerSaveForOlderBuild\(/, /\n}\n/, 'offerSaveForOlderBuild');
  assert.doesNotMatch(offer, /showWarningMessage\(/, 'the notice is not one the API can never close');
  assert.match(offer, /ProgressLocation\.Notification[\s\S]*?switchNotices\.hold\(\)/, 'it stays up exactly until the switch is done');
  assert.match(offer, /withSaveLink\(/, 'and still offers Save');
  assert.match(source, /registerPersonCommand\(OLDER_BUILD_SAVE_COMMAND,/, 'the Save link has a command behind it');
  const tail = slice(source, /THE ENGINE IS UP\. Everything below is about a MODEL/, /\n}\n/, 'the model half of connectEngineAttempt');
  assert.match(tail, /if \(!keptOlderBuild\) switchNotices\.settle\(\);/, 'a connect that kept no older engine closes the notices');
  const deact = slice(source, /export function deactivate\(\)/, /\n}\n/, 'deactivate');
  assert.match(deact, /switchNotices\.settle\(\)/, 'a closing window leaves no notice behind');
}

console.log('version-guard: ok');
