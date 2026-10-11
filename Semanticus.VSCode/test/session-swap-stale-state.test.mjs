// Verifier, lane m11/create-model-ui (b30e68b9): what a model swap leaves behind from the OLD model.
//
// The lane makes an assistant's swap reach the window (session/didChange), and the window then runs the same wiring
// its own opens run. Driving that in a real Extension Development Host (verify-create-model-ui.md, shots 03 to 19)
// showed four places where the old model, or a swap that is not announced, still reaches the person. Each check
// below is one of them. They fail until fixed; each names what a person saw.
//
// Three kinds of check: the Plan's guidance decision is a pure function and is executed; the rest is window and
// Studio wiring with no seam to execute, so it is pinned in source the way session-swap-notify.test.mjs pins the
// handler itself. A pin proves the wiring exists, not that it works: the fix still owes a lane run.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8').replace(/\r\n/g, '\n');
const app = readFileSync(join(webview, 'src', 'App.tsx'), 'utf8').replace(/\r\n/g, '\n');

const failures = [];
function check(name, fn) {
  try { fn(); console.log('ok   ' + name); }
  catch (e) { failures.push(name); console.log('FAIL ' + name + '\n     ' + String(e?.message ?? e).split('\n').join('\n     ')); }
}
// The text of a block from `start` to the first `end` after it.
function slice(source, start, end, what) {
  const at = source.indexOf(start);
  assert.ok(at >= 0, `${what}: "${start}" not found`);
  const stop = source.indexOf(end, at + start.length);
  assert.ok(stop > at, `${what}: its end "${end}" not found`);
  return source.slice(at, stop);
}

// Load plan-guide.tsx the way plan-guide.test.mjs does: transpiled, with only the VS Code bridge and CSS stubbed.
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const STUBS = {
  './bridge': { rpc: async () => ({ rows: [] }), requestGuideFlags: async () => null, setGuideFlag: () => {}, runHostCommand: () => {} },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {} },
  './plan-guide.css': {},
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const module = { exports: {} };
  cache.set(full, module.exports);
  const out = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const localRequire = (name) => STUBS[name] ?? (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', out)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const G = load(join(webview, 'src', 'plan-guide.tsx'));

// 1. Shots 02 to 04b. The first open showed this person the What changed card (history not empty), which sets the
//    card's flag. The assistant then made an empty model; Studio remounted and the six-step tour appeared at "1 of 6".
//    With the tour open at step 2, a second assistant model started it again at "1 of 6". Contract 10 decides the
//    guidance "on first open of the Plan after this build" and "nothing shows twice": which guide a person gets is a
//    fact about the person, not about whichever model is open, and it is not the assistant's to trigger.
check('the tour does not appear for a person who was already shown this build\'s guidance, on a new empty model', () => {
  const alreadyShown = { planTour: false, whatChanged: true };   // shownFlags() sets whatChanged when EITHER guide shows
  assert.equal(G.decideGuide(alreadyShown, true, G.WHAT_CHANGED_CARD_ID), 'none',
    'showing the card (or the tour) once already set the card flag; a later empty model must not start the tour');
});

// 2. Shots 05 and 07. The readiness page draws the score history as a sparkline. The list is never cleared when a
//    different model opens, so the assistant's empty model (25) showed a line falling from TomWrapperTest's 56, and
//    TomWrapperTest, reopened, showed 56, 25, 56: a dip it never had.
check('the readiness trend restarts when a different model opens', () => {
  const onSwap = slice(app, 'const offReconnect = onReconnect(() => {', 'const off = onDidChange(', 'App reconnect handler');
  const branch = slice(onSwap, 's.sessionId !== previousSessionId) {', '}', 'the different-session branch');
  assert.match(branch, /setTrend\(/, 'a new session keeps the old model\'s scores in the trend');
});

// 3. Shots 13 to 15. The person restored a saved version from the Plan's History. The engine reopened the model as a
//    new session and, by this lane's rule, did not tell the connection that asked. The relay does not count a restore
//    as a swap, so nothing re-read: Studio kept the old session, ignored every later change (App drops a didChange
//    for another session), and its Work view stayed at "Latest 04:48" and "Saved" after a 04:50 edit, while the
//    footer said "unsaved". An assistant's restore (shot 16) does reach Studio.
check('a restore the person starts in Studio rebinds the window, like an open does', () => {
  const swapped = slice(extension, 'const modelSwapped =', ';', 'the relay\'s model-swap test');
  assert.match(swapped, /restoreHistoryCheckpoint/, 'a History restore is a model swap the relay must refresh after');
  assert.match(swapped, /restored/, 'counted only when the restore actually restored');
});

// 4. Shots 17 to 19. "Undo: ran 1 step of 'Describe total sales'" stayed in the status bar after the assistant
//    opened a different model, and survived that model's next edit (the chip compares revisions, and a new session
//    starts at 0). Pressing it undid the assistant's edit to the OTHER model, not the draft it names.
check('the draft undo chip is cleared when a different model opens', () => {
  const status = slice(extension, 'function setStatusFromInfo(', '\n}\n', 'setStatusFromInfo');
  const bind = slice(extension, 'async function bindGridAndTree(', '\n}\n', 'bindGridAndTree');
  assert.ok(/renderDraftUndoChip\(null\)/.test(status) || /renderDraftUndoChip\(null\)/.test(bind),
    'nothing clears the chip on a session change, so it undoes the wrong model');
});

// 5. Hero journey 5 (8 Oct, B2). A new model started from a SQL source REPLACES the open one (the engine creates and saves an
//    empty model), but the relay did not count that as a swap. The tree kept naming the old model, Studio's own session stayed
//    on it, and every request the page sent named the OLD model, so "OK, let Pro see these" was refused ("This page was
//    showing another model than the one open now"). The start owns its target model: it is a swap, once the engine says it started.
check('a start from a SQL source rebinds the window, like an open does', () => {
  const swapped = slice(extension, 'const modelSwapped =', ';', 'the relay\'s model-swap test');
  assert.match(swapped, /startModelFromSql/, 'a start from a SQL source replaces the open model, so the relay must refresh after it');
  assert.match(swapped, /exists/, 'counted only when the engine answered with a plan (a refused start changes nothing)');
});

if (failures.length) {
  console.error(`session-swap-stale-state: ${failures.length} of 5 failed`);
  process.exit(1);
}
console.log('session-swap-stale-state: ok');
