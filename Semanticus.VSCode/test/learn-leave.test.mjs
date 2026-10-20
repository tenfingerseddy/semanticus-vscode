// Leaving the Learn tour must never throw away work in a model that is not the demo copy (1.4, lane tour; verifier FAIL, 10 Oct).
//   Leave the tour with the demo copy still open: the person's earlier model comes back (the happy path)
//   Leave the tour after another model opened: nothing is reopened, so nothing is discarded; the bar closes
//   Another model opening ends the tour at once (the host tells the Studio); the demo's own opens never do
// It loads the compiled out/learnHost.js with a stub vscode module, so run `npm run compile` first (npm test builds first).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const require = createRequire(import.meta.url);
const Module = require('node:module');
const realLoad = Module._load;
Module._load = function (request, ...rest) {
  if (request === 'vscode') return { window: { showWarningMessage: async () => undefined } };
  return realLoad.call(this, request, ...rest);
};
const { createLearn } = require(join(root, 'out', 'learnHost.js'));
const { isScratchCopyPath } = require(join(root, 'out', 'learn.js'));

// Nothing offers the demo's internal folder as a person's own model or folder (1.6, v2-fix-r2-start).
test('a path inside a tour demo copy is known as one, and nothing else is', () => {
  const store = join('home', 'k', 'globalStorage', 'semanticus-vscode.semanticus-vscode');
  assert.equal(isScratchCopyPath(join(store, 'learn', 'plan-20261018-063000')), true);
  assert.equal(isScratchCopyPath(join(store, 'learn', 'ask-20261018-063000-2', 'tables', 'Sales.tmdl')), true, 'a file inside it too');
  assert.equal(isScratchCopyPath('C:\\Users\\k\\globalStorage\\x\\learn\\edit-20261018-063000'), true, 'a Windows path');
  assert.equal(isScratchCopyPath(join('work', 'models', 'Contoso.bim')), false);
  assert.equal(isScratchCopyPath(join('work', 'learn', 'my-notes')), false, 'a folder called learn that the tour did not make');
  assert.equal(isScratchCopyPath(undefined), false);
});

// `modelOpen` false: the tour opens its demo, and Leave brings back what was remembered. True: the person's own model is open, and the
// tour is about THAT model (fix-uat-first-start, F6): the demo never opens and Leave leaves the model exactly as it was.
function rig(modelOpen = false) {
  const storageDir = mkdtempSync(join(tmpdir(), 'lrn-'));
  const state = { open: modelOpen ? 'sales' : undefined, restored: [], forgotten: 0, posted: [], opened: 0, readyMessages: [] };
  const learn = createLearn({
    assetsDir: join(root, 'assets'),
    storageDir,
    open: async () => { state.opened++; state.open = 'demo-1'; return { modelName: 'Contoso Shop', sessionId: 'demo-1' }; },
    currentModelName: () => (state.open === 'sales' ? 'Sales' : undefined),
    remembered: () => ({ kind: 'file', path: '/home/me/Sales.bim' }),
    restore: async (previous) => { state.restored.push({ previous, whileOpen: state.open }); state.open = 'sales'; },
    forget: () => { state.forgotten++; },
    currentSessionId: () => state.open,
    postToStudio: async (message) => { state.posted.push(message.type); if (message.type === 'learnReady') state.readyMessages.push(message); },
    log: () => {},
  });
  return { learn, state, done: () => rmSync(storageDir, { recursive: true, force: true }) };
}

test('Leave the tour with the demo copy open brings the earlier model back', async () => {
  const { learn, state, done } = rig();
  try {
    await learn.start('plan');
    await learn.end();
    assert.equal(state.restored.length, 1);
    assert.equal(state.restored[0].whileOpen, 'demo-1');
    assert.ok(state.posted.includes('learnEnded'));
  } finally { done(); }
});

test('Leave the tour after another model opened reopens nothing, so nothing is discarded', async () => {
  const { learn, state, done } = rig();
  try {
    await learn.start('plan');
    state.open = 'budget';   // the person (or their assistant) opened Budget and edited it
    await learn.end();
    assert.deepEqual(state.restored, [], 'restore (discard) ran while the demo copy was not open');
    assert.equal(state.forgotten, 0);
    assert.ok(state.posted.includes('learnEnded'));
  } finally { done(); }
});

test('another model opening ends the tour at once, and the later Leave does nothing', async () => {
  const { learn, state, done } = rig();
  try {
    await learn.start('plan');
    state.posted.length = 0;
    learn.modelChanged('demo-1');   // a refresh of the demo itself is not a new model
    assert.deepEqual(state.posted, []);
    state.open = 'budget';
    learn.modelChanged('budget');
    await new Promise((r) => setImmediate(r));
    assert.deepEqual(state.posted, ['learnEnded']);
    await learn.end();
    assert.deepEqual(state.restored, []);
    assert.deepEqual(state.posted, ['learnEnded']);
  } finally { done(); }
});

test('a model that opens while a chapter is starting does not end the tour', async () => {
  const { learn, state, done } = rig();
  try {
    await learn.start('plan');
    state.posted.length = 0;
    let release;
    const gate = new Promise((r) => { release = r; });
    const second = createLearn({
      assetsDir: join(root, 'assets'), storageDir: mkdtempSync(join(tmpdir(), 'lrn-')),
      open: async () => { second.modelChanged('demo-2'); await gate; return { modelName: 'Contoso Shop', sessionId: 'demo-2' }; },
      remembered: () => undefined, restore: async () => {}, forget: () => {}, currentSessionId: () => 'demo-2',
      postToStudio: async (m) => { state.posted.push(m.type); }, log: () => {},
    });
    const running = second.start('ask');
    release();
    await running;
    assert.deepEqual(state.posted, ['learnReady']);
  } finally { done(); }
});

test('with the person\'s model open the tour never opens the demo, and Leave leaves the model as it was', async () => {
  const { learn, state, done } = rig(true);
  try {
    await learn.start('plan');
    assert.equal(state.opened, 0, 'the demo was opened over the person\'s model');
    assert.equal(state.open, 'sales');
    assert.deepEqual(state.readyMessages.map((m) => [m.sessionId, m.own, m.modelName]), [['sales', true, 'Sales']], 'the tour is about the open model');
    await learn.start('ask');   // the next chapter keeps touring the same model
    assert.equal(state.opened, 0);
    await learn.end();
    assert.deepEqual(state.restored, [], 'Leave reopened (and so discarded) the person\'s model');
    assert.equal(state.forgotten, 0);
    assert.equal(state.open, 'sales');
    assert.ok(state.posted.includes('learnEnded'));
  } finally { done(); }
});
