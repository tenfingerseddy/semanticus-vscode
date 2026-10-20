// An assistant's model swap updates the window quietly (coordinator decision, 27 Sep, on the verifier's A3 for lane
// m11/create-model-ui). The swap reaches the tree, Properties and the status bar, and an open Studio follows it. It
// does NOT reopen a Studio the person closed. It DOES raise the "Let Pro decide for this model?" card for a model nobody has
// decided about (hero 20 D10, 13 Oct: the old 26 Sep rule kept Jev silent until the person found Pro > Turn on). The card only
// asks; the person's press is the only thing that turns permission on.
// A closed Studio shows the new model the next time the person opens it.
//
// Three kinds of check. The permission card's rules (the webview's card and the host's memory behind it, verifier r2
// O1 and O2) and the bridge's "who swapped" word are executed, through a loader that stubs only the VS Code bridge
// and the licence hook. The window side (the Studio auto-open) has no seam
// to execute, so it is pinned in source the way session-swap-notify.test.mjs pins the handler; a pin proves the
// wiring exists, and the lane run proves it works.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

function load(file, stubs) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  const module = { exports: {} };
  const out = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
  const localRequire = (name) => stubs[name] ?? (name.startsWith('.') ? load(resolve(dirname(full), name), stubs) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', out)(localRequire, module, module.exports);
  return module.exports;
}
function slice(source, start, end, what) {
  const at = source.indexOf(start);
  assert.ok(at >= 0, `${what}: "${start}" not found`);
  const stop = source.indexOf(end, at + start.length);
  assert.ok(stop > at, `${what}: its end "${end}" not found`);
  return source.slice(at, stop);
}

// ---- 1. The permission card, executed, with the host's memory behind it ---------------------------------
// The card's memory lives in the extension host (src/consentAsks.ts), because the Studio webview is rebuilt each time
// the person closes and reopens it (verifier r2, O2). Each load of consent.tsx below is one Studio webview; the one
// ConsentAsks instance is the window. `swap` does what the extension does: tell the host first, then the panels.
{
  const { ConsentAsks } = load(join(root, 'src', 'consentAsks.ts'), {});
  const host = new ConsentAsks();
  const didChange = new Set();
  const reconnect = new Set();
  const studio = () => load(join(webview, 'src', 'consent.tsx'), {
    './bridge': {
      rpc: async () => null,
      claimConsentAsk: async (scope) => host.claim(scope),
      onDidChange: (fn) => { didChange.add(fn); return () => didChange.delete(fn); },
      onReconnect: (fn) => { reconnect.add(fn); return () => reconnect.delete(fn); },
    },
    './pro': { useTier: () => 'pro' },
  });
  const edit = (sessionId) => didChange.forEach((fn) => fn({ sessionId, revision: 1, deltas: [] }));
  const swap = (by) => { reconnect.forEach((fn) => fn(by)); };
  const needs = (model) => ({ granted: false, current: false, appliesTo: model });
  let C = studio();

  // D10 (hero 20): the assistant opening a model asks like any other open, once for that model.
  swap('otherDoor');
  edit('s1');
  assert.equal(await C.raiseConsentCardFor(needs('A')), true, 'the assistant opening A brings up the card, once');
  assert.equal(await C.raiseConsentCardFor(needs('A')), false, 'and not again for A');
  swap('otherDoor');
  edit('s2');
  assert.equal(await C.raiseConsentCardFor(needs('B')), true, 'the assistant opening B asks about B');

  // The person's own open of a model already asked about does not ask again.
  swap('person');
  edit('s3');
  assert.equal(await C.raiseConsentCardFor(needs('A')), false, 'the person\'s own open of A does not ask again');

  // O2: "Not now" holds for the window's run. Run B shot B24: the person closed Studio and opened it again, and the
  // fresh Studio asked about the model they had just said Not now to.
  C = studio();
  assert.equal(await C.raiseConsentCardFor(needs('A')), false, 'a Studio closed and reopened does not ask about A again');

  // The assistant restores A (verifier r1 shot 16). The engine sends the new session's first model change BEFORE the
  // swap itself, so a rule keyed on notification order would forget A was asked about and ask again.
  edit('s4');
  swap('otherDoor');
  assert.equal(await C.raiseConsentCardFor(needs('A')), false, 'the assistant\'s restore of A does not bring the card back');
  // The person's own History restore reopens the same model: not a fresh occasion, and not their own open either.
  swap('restore');
  edit('s4b');
  assert.equal(await C.raiseConsentCardFor(needs('C')), true, 'a model not yet asked about asks, whoever opened it');

  // The person's own open of a model not yet asked about asks; reopening A still does not, in this window's run.
  swap('person');
  edit('s5');
  assert.equal(await C.raiseConsentCardFor(needs('A')), false, 'the person reopening A does not undo their Not now');
  assert.equal(await C.raiseConsentCardFor(needs('B')), false, 'B was asked about when the assistant opened it');
  assert.equal(await C.raiseConsentCardFor({ current: true, appliesTo: 'D' }), false, 'permission already current never asks');
  assert.equal(await C.raiseConsentCardFor({ current: false, appliesTo: '' }), false, 'a never-saved model never asks');
  assert.equal(host.claim(undefined), false, 'the host answers no to a message with no model named');
  assert.equal(host.claim('E'), true, 'and yes, once, to one that names a model not yet asked about');
}

// ---- 1b. The model open now, asked about at once (hero 20 D10) ------------------------------------------
// Nobody has decided about Jev for the model the assistant opened, and no page is waiting on a Pro answer: the card comes
// anyway. Someone who turned permission off has decided, so they are not asked. Nothing is turned on by the check itself.
{
  const { ConsentAsks } = load(join(root, 'src', 'consentAsks.ts'), {});
  const host = new ConsentAsks();
  let reply = null;
  const calls = [];
  const C = load(join(webview, 'src', 'consent.tsx'), {
    './bridge': {
      rpc: async (name) => { calls.push(name); return reply; },
      claimConsentAsk: async (scope) => host.claim(scope),
      onDidChange: () => () => {},
      onReconnect: () => () => {},
    },
    './pro': { useTier: () => 'pro' },
  });
  reply = { granted: false, current: false, appliesTo: 'M1' };
  assert.equal(await C.raiseConsentCardForOpenModel(), true, 'an undecided model the assistant opened brings up the card');
  assert.equal(await C.raiseConsentCardForOpenModel(), false, 'once');
  reply = { granted: false, current: false, appliesTo: 'M2', withdrawnUtc: '2026-10-01T00:00:00Z' };
  assert.equal(await C.raiseConsentCardForOpenModel(), false, 'permission the person turned off is a decision: no card');
  reply = { granted: true, current: true, appliesTo: 'M3' };
  assert.equal(await C.raiseConsentCardForOpenModel(), false, 'permission already given: no card');
  reply = { granted: false, current: false, appliesTo: '' };
  assert.equal(await C.raiseConsentCardForOpenModel(), false, 'a model with no saved place has nothing to keep permission under');
  assert.ok(!calls.some((c) => /^setConsent$/.test(c)), 'the check never gives permission itself');
}

// ---- 2. The bridge carries who swapped, executed ------------------------------------------------------
{
  const win = new EventTarget();
  win.setTimeout = setTimeout; win.clearTimeout = clearTimeout;
  const prev = { window: globalThis.window, acquire: globalThis.acquireVsCodeApi };
  globalThis.window = win;
  globalThis.acquireVsCodeApi = () => ({ postMessage: () => {}, getState: () => ({}), setState: () => {} });
  try {
    const B = load(join(webview, 'src', 'bridge.ts'), {});
    const heard = [];
    B.onReconnect((by) => heard.push(by));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected', by: 'otherDoor' } }));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } }));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected', by: 'person' } }));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected', by: 'restore' } }));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected', by: 'something else' } }));
    assert.deepEqual(heard, ['otherDoor', 'person', 'person', 'restore', 'person'], 'a reconnect with no word, or an unknown one, is the person\'s own');
  } finally { globalThis.window = prev.window; globalThis.acquireVsCodeApi = prev.acquire; }
}
// The card asks the host, and only the host's plain yes raises it. The answer is matched by id.
{
  const win = new EventTarget();
  win.setTimeout = setTimeout; win.clearTimeout = clearTimeout;
  const prev = { window: globalThis.window, acquire: globalThis.acquireVsCodeApi };
  const posted = [];
  globalThis.window = win;
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState: () => {} });
  try {
    const B = load(join(webview, 'src', 'bridge.ts'), {});
    const yes = B.claimConsentAsk('A');
    const no = B.claimConsentAsk('B');
    const [askA, askB] = posted.filter((m) => m.type === 'claimConsentAsk');
    assert.equal(askA.scope, 'A');
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'consentAsk', id: askB.id, ask: 'yes' } }));
    win.dispatchEvent(new MessageEvent('message', { data: { type: 'consentAsk', id: askA.id, ask: true } }));
    assert.equal(await yes, true, 'the host\'s yes for A raises the card for A');
    assert.equal(await no, false, 'anything but a plain yes is a no');
    const closing = B.claimConsentAsk('C');
    win.dispatchEvent(new Event('pagehide'));
    assert.equal(await closing, false, 'a Studio closing before the host answers does not ask');
  } finally { globalThis.window = prev.window; globalThis.acquireVsCodeApi = prev.acquire; }
}

// ---- 3. The window: another door's swap never opens Studio, pinned ------------------------------------
{
  const handler = slice(extension, "connected.onNotification('session/didChange'", '});', 'the session/didChange handler');
  const mark = handler.indexOf('autoOpenedSessionId = n.sessionId');
  const bind = handler.indexOf('bindGridAndTree(');
  assert.ok(mark >= 0, 'the handler must mark the swapped-to session as already auto-opened, or maybeAutoOpenStudio reopens a closed Studio');
  assert.ok(bind > mark, 'and mark it BEFORE bindGridAndTree, whose status refresh is what runs the auto-open');
  assert.match(handler.slice(bind), /bindGridAndTree\([^)]*'otherDoor'\)/, 'the handler tells Studio the swap came from another door');

  const auto = slice(extension, 'function maybeAutoOpenStudio(', '\n}\n', 'maybeAutoOpenStudio');
  assert.match(auto, /if \(sessionId === autoOpenedSessionId\) return;/, 'the auto-open still skips a session already marked');

  const bindBody = slice(extension, 'async function bindGridAndTree(', '\n}\n', 'bindGridAndTree');
  assert.match(bindBody, /postToPanels\(\{ type: 'reconnected', by \}\)/, 'bindGridAndTree passes who swapped on to Studio');
  // The relay says a History restore is a restore, so the person's own restore does not re-arm the card.
  assert.match(extension, /const swapBy = msg\.method === 'restoreHistoryCheckpoint' \? 'restore' : 'person';/,
    'the relay tells Studio a restore is a restore');
  assert.match(extension, /postToPanels\(\{ type: 'reconnected', by: swapBy \}\)/, 'and passes that word on to both panels');

  // The card's memory is the host's, for the window's run, and asked by every panel through one message. A swap is not told to it:
  // the assistant's open asks like any other.
  assert.doesNotMatch(extension, /noteSwap/, 'no swap is told to the card\'s memory any more: it never stays silent after the assistant\'s open');
  assert.match(extension, /msg\?\.type === 'claimConsentAsk'[\s\S]{0,600}?type: 'consentAsk', id: msg\.id, ask: !demoOpen && consentAsks\.claim\(msg\.scope\)/,
    'the panels ask the host whether the card may ask');
  // The tour's throwaway demo copy is never asked about, and not being asked is not recorded as asked (1.6, v2-fix-r2-start).
  // The read sits in scratchCopyOpen, outside the relay, whose engine calls all go through its rpc branch (join11-relay-scope).
  assert.match(extension, /msg\?\.type === 'claimConsentAsk'[\s\S]{0,400}?const demoOpen = await scratchCopyOpen\(\)/, 'a demo copy that is open does not raise the card');
  assert.match(extension, /async function scratchCopyOpen\(\)[\s\S]{0,300}?return isScratchCopyPath\(info\?\.source\)/, 'the demo-copy read checks the open model\'s source');
  assert.equal((extension.match(/new ConsentAsks\(\)/g) ?? []).length, 1, 'one memory for the window, shared by every panel');
  // The person's own opens keep the old default, so they still open Studio and re-arm the card.
  assert.match(bindBody, /by: 'person' \| 'otherDoor' = 'person'/, 'a caller that says nothing is the person');
}

console.log('assistant-swap-quiet: ok');

// ---- hero 24 D5: an answer the assistant ran says Jev was not asked for want of permission -------------------------------------------
// The open-model ask has its own rules (above). The page that shows such an answer asks the same question, through the same rules, so a card
// that was missed at the open is still offered once, and the person who decided is not asked.
{
  const consent = readFileSync(join(webview, 'src', 'consent.tsx'), 'utf8');
  const ask = readFileSync(join(webview, 'src', 'queriesask.tsx'), 'utf8');
  assert.match(consent, /export function useAskWhenAnswerNeedsPermission\(needs: boolean\)[\s\S]{0,400}raiseConsentCardForOpenModel\(\)/,
    'the answer-driven ask goes through the open-model ask, which skips a decided model');
  assert.match(ask, /useAskWhenAnswerNeedsPermission\(!fromWords && !!answer && answer\.needsPermission === true\)/,
    'the Query page asks when an answer it did not ask for says permission is missing');
}

// ---- UAT round 2: Not now is remembered for that model, across a reload ----------------------------------------------------------------------
// The memory lived only in the window's run, so Reload Window asked again about a model the person had put off. It is kept in the editor's
// own store (globalState) too, and a fresh window reads it back.
{
  const { ConsentAsks } = load(join(root, 'src', 'consentAsks.ts'), {});
  const store = new Map();
  const memento = { get: (k, d) => (store.has(k) ? store.get(k) : d), update: async (k, v) => { store.set(k, v); } };
  const first = new ConsentAsks(); first.remember(memento);
  assert.equal(first.claim('M1'), true, 'a model not asked about is asked once');
  const reloaded = new ConsentAsks(); reloaded.remember(memento);
  assert.equal(reloaded.claim('M1'), false, 'after a reload the same model is not asked again');
  assert.equal(reloaded.claim('M2'), true, 'another model still is');
  assert.match(extension, /consentAsks\.remember\(context\.globalState\)/, 'the window gives the memory its store');
}
