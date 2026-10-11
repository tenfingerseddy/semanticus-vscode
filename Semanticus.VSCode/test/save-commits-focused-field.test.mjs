// Ctrl+S in the Properties grid saves what the person just typed (UAT-F-S07c-2-004, -S07c-3, -S07c-6-008).
//
// WHAT WENT WRONG. Type a description in Properties, press Ctrl+S: "Saved TMDL" showed, but the save ran before the
// description was applied, so the file on disk lacked it and the model stayed dirty. The grid's key handler posted
// "run the Save command" and nothing committed the field first.
//
// CLASS. A Save that does not first commit the field the person is in. The fix is in the Save command itself, so
// every way to reach it (the Ctrl+S in the grid, the palette, the Studio key) commits first.
//
// HOW THIS TESTS IT. The REAL pieces run, with only the editor and the engine faked:
//   - media/propgrid/propgrid.js, loaded into a small fake page, receives a real key press (Ctrl+S) in a focused
//     Description box;
//   - the compiled PropertyGridProvider receives what that page posts, in order;
//   - the compiled runWebviewHostCommand and saveCommand run the Save command the way VS Code would;
//   - the fake engine takes time to apply a property and records what a save would have written.
// The assertions are on the engine's own record: the save must see the description, and the "Saved" notice must
// come after it.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiledPath = join(root, 'out', 'extension.js');
assert.ok(existsSync(compiledPath), 'out/extension.js is missing. Run npm run compile first.');
const compiled = readFileSync(compiledPath, 'utf8');
const gridSource = readFileSync(join(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');

/** The text of one top-level declaration, up to the brace (or semicolon) that closes it. Quotes and comments do not count. */
function slice(source, opener) {
  const start = source.indexOf(opener);
  assert.ok(start >= 0, `out/extension.js no longer contains "${opener}". This test slices real code; find where it went.`);
  const open = { '{': 1, '(': 1, '[': 1 };
  const close = { '}': 1, ')': 1, ']': 1 };
  let depth = 0, body = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (c === '/' && source[i + 1] === '/') { i = source.indexOf('\n', i); if (i < 0) break; continue; }
    if (c === '/' && source[i + 1] === '*') { i = source.indexOf('*/', i) + 1; continue; }
    if (c === '\'' || c === '"' || c === '`') {
      for (i++; i < source.length; i++) { if (source[i] === '\\') { i++; continue; } if (source[i] === c) break; }
      continue;
    }
    if (open[c]) { depth++; if (c === '{') body = true; continue; }
    if (close[c]) { depth--; if (body && depth === 0) return source.slice(start, i + 1); continue; }
    if (c === ';' && depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`could not find the end of ${opener}`);
}

// ---- the fake page the grid's script runs in ------------------------------------------------------------
function makePage(typed) {
  const posted = [];             // everything the page sends to the host, in order
  const listeners = { window: {}, document: {} };
  const field = { tagName: 'TEXTAREA', type: 'textarea', value: typed, getAttribute: (n) => (n === 'data-prop' ? 'Description' : null), closest: () => field };
  const rootEl = {
    innerHTML: '', querySelector: () => null, querySelectorAll: () => [],
  };
  const document = {
    activeElement: field, body: {}, documentElement: {},
    getElementById: (id) => (id === 'root' ? rootEl : null),
    querySelectorAll: () => [],
    addEventListener: (type, fn) => { (listeners.document[type] ??= []).push(fn); },
  };
  const window = { addEventListener: (type, fn) => { (listeners.window[type] ??= []).push(fn); } };
  new Function('acquireVsCodeApi', 'document', 'window', 'navigator', 'getComputedStyle', gridSource)(
    () => ({ postMessage: (m) => posted.push(m) }), document, window, { platform: 'Linux' }, () => ({}));
  return {
    posted,
    /** A message from the host to the page. */
    send: (m) => listeners.window.message.forEach((fn) => fn({ data: m })),
    /** A real Ctrl+S, the way the grid's capture listener sees it. */
    pressCtrlS: () => listeners.document.keydown.forEach((fn) => fn({
      defaultPrevented: false, isComposing: false, ctrlKey: true, metaKey: false, altKey: false, shiftKey: false,
      key: 's', target: field, preventDefault() {}, stopPropagation() {},
    })),
  };
}

// ---- the compiled host code, with the editor and the engine faked ---------------------------------------
const log = [];                  // what happened, in order
const notices = [];
const statusLines = [];
const engine = { description: '', onDisk: null, refuse: false };
const hostPosts = [];            // every message the host sent to the page, in order
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const conn = {
  async sendRequest(method, ...args) {
    log.push(`start:${method}`);
    if (method === 'sessionInfo') return { source: '/m/Model.SemanticModel/definition', modelName: 'Model' };
    if (method === 'listSelectionProperties' || method === 'getObjectProperties' || method === 'listObjectProperties') return [];
    if (method === 'setObjectProperty') {
      await sleep(60);            // the engine takes a moment, as a real one does
      assert.equal(args[1], 'Description');
      if (engine.refuse) throw new Error('The engine refused this write.');
      engine.description = args[2];
      log.push('end:setObjectProperty');
      return {};
    }
    if (method === 'save') {
      engine.onDisk = engine.description;   // a save writes whatever the model holds at this moment
      log.push(`wrote:${JSON.stringify(engine.onDisk)}`);
      return { path: '/m/Model.SemanticModel', format: 'TMDL', fileCount: 3 };
    }
    return undefined;
  },
};
const vscode = {
  window: {
    showInformationMessage: (text) => { notices.push(text); log.push(`notice:${text}`); return Promise.resolve(); },
    // Kane 8 Oct: a save is quiet. The word "Saved" goes to the status bar for a few seconds, not into a toast.
    setStatusBarMessage: (text, ms) => { statusLines.push({ text, ms }); log.push(`status:${text}`); return { dispose() {} }; },
    showWarningMessage: () => Promise.resolve(), showErrorMessage: () => Promise.resolve(),
  },
  commands: { executeCommand: (name) => { log.push(`command:${name}`); return commandTable[name]?.(); } },
  Uri: { joinPath: (...p) => p.join('/') },
};
const commandTable = {};

const propSet_1 = (() => { const m = { exports: {} }; new Function('module', 'exports', readFileSync(join(root, 'out', 'propSet.js'), 'utf8'))(m, m.exports); return m.exports; })();   // the grid's pure half, compiled beside extension.js
const modelFence_1 = createRequire(import.meta.url)(join(root, 'out', 'modelFence.js'));   // the host's one place that names the model on a request (C1, drafts-sol round 2)
const factory = new Function('vscode', 'getConn', 'conn', 'rememberLastModel', 'watchModelDisk', 'pickFirstSaveDestination', 'commitStudioFocused', 'propSet_1', 'modelFence_1', `
  ${slice(compiled, 'class PropertyGridProvider')}
  ${slice(compiled, 'const WEBVIEW_HOST_COMMANDS')}
  ${slice(compiled, 'const WEBVIEW_CREATE_COMMANDS')}
  ${slice(compiled, 'function runWebviewHostCommand')}
  ${slice(compiled, 'function runPersonAction')}
  let hostModelIdentity;
  const { fenceIdentity, personAction, insideAnyAction } = modelFence_1;
  let propGrid;
  ${slice(compiled, 'async function saveCommand')}
  return {
    makeGrid: (uri) => { const g = new PropertyGridProvider(getConn, uri); g.html = () => ''; return (propGrid = g); },   // the page markup is not under test
    runWebviewHostCommand, saveCommand,
  };
`);
const host = factory(vscode, () => conn, conn, async () => undefined, () => undefined, async () => undefined, async () => undefined, propSet_1, modelFence_1);   // no Studio panel here

// ---- the scene -------------------------------------------------------------------------------------------
const TYPED = 'Northstar UAT description';
const page = makePage(TYPED);
let receive;
const grid = host.makeGrid('file:///ext');
grid.refs = ['measure:Sales/Average Order Value'];
grid.names = ['Average Order Value'];
grid.push = async () => undefined;   // the re-read of the canonical values is not what is under test
grid.resolveWebviewView({
  webview: {
    options: {}, html: '',
    asWebviewUri: (u) => u, cspSource: '',
    onDidReceiveMessage: (fn) => { receive = fn; },
    postMessage: (m) => {            // host -> page
      hostPosts.push(m);
      queueMicrotask(() => {
        page.send(m);
        // and whatever the page answers goes back to the host, in order, like the real channel
        while (relayed < page.posted.length) { const msg = page.posted[relayed++]; void receive(msg); }
      });
      return Promise.resolve(true);
    },
  },
});
let relayed = 0;
commandTable['semanticus.save'] = () => host.saveCommand();
// the page's 'ready' and its first message from the host
page.send({ type: 'load', key: JSON.stringify(['measure:Sales/Average Order Value']), name: 'Average Order Value', props: [
  { name: 'Description', displayName: 'Description', kind: 'string', category: 'Basic', value: '' },
] });
relayed = page.posted.length;

// The person has typed into the Description box (the fake field already holds the text) and presses Ctrl+S.
page.pressCtrlS();
// What the page posted on that key press goes to the host, like the real channel.
const keyPress = page.posted.slice(relayed);
assert.deepEqual(keyPress.map((m) => m.type), ['runCommand'],
  'Ctrl+S in the grid hands the Save command to the host');
relayed = page.posted.length;
await receive(keyPress[0]);   // runWebviewHostCommand starts the Save command
await sleep(400);             // let the whole save settle

const saveIndex = log.findIndex((e) => e === 'start:save');
const setEnd = log.indexOf('end:setObjectProperty');
assert.ok(log.includes('start:setObjectProperty'), `the typed description was never sent to the engine. What happened: ${log.join(' | ')}`);
assert.ok(saveIndex > -1, `Save never ran. What happened: ${log.join(' | ')}`);
assert.ok(setEnd > -1 && setEnd < saveIndex,
  `the save started before the description had landed. What happened: ${log.join(' | ')}`);
assert.equal(engine.onDisk, TYPED, 'the file on disk must hold the description the person typed before pressing Ctrl+S');
assert.equal(notices.length, 0, `a save raises no toast. Got: ${notices.join(' | ')}`);
assert.deepEqual(statusLines.map((l) => l.text), ['Saved'], `one quiet "Saved" in the status bar. Got: ${JSON.stringify(statusLines)}`);
assert.equal(statusLines[0].ms, 4000, 'the status bar word stays for 4 seconds');
assert.ok(log.indexOf('status:Saved') > log.findIndex((e) => e.startsWith('wrote:')), 'the Saved word follows the write');

// ---- siblings: the same Save, reached a second way, and a view that never answers ---------------------------
// A. Save from the palette (no key press in the grid): the focused field is still committed first.
engine.description = ''; engine.onDisk = null; log.length = 0; notices.length = 0;
await host.saveCommand();
await sleep(100);
assert.equal(engine.onDisk, TYPED, 'Save from the palette also commits the field the person was in');

// B. Nothing focused in the grid (the field equals what the engine holds): no write, and the save still happens.
engine.description = TYPED; engine.onDisk = null; log.length = 0;
page.send({ type: 'load', key: JSON.stringify(['measure:Sales/Average Order Value']), name: 'Average Order Value', props: [
  { name: 'Description', displayName: 'Description', kind: 'string', category: 'Basic', value: TYPED },
] });
await host.saveCommand();
assert.ok(!log.includes('start:setObjectProperty'), 'an untouched field is not written again');
assert.equal(engine.onDisk, TYPED, 'the save still runs');

// ---- round 3: the host says when the engine took a write (join 11 solui, setOk) ---------------------------------
// The grid ends its hold on the words of an accepted write only on a load of THAT object, which never comes for an object the person left (the view is not kept when hidden). So the host
// answers every accepted write with setOk, naming the model, the selection and the words, and answers a refused one with setError and no setOk.
const setOks = hostPosts.filter((m) => m.type === 'setOk');
assert.ok(setOks.length >= 1, `the host never told the page the engine took the write. It sent: ${hostPosts.map((m) => m.type).join(', ')}`);
assert.deepEqual([setOks[0].name, setOks[0].value, setOks[0].key], ['Description', TYPED, JSON.stringify(['measure:Sales/Average Order Value'])]);
assert.equal(typeof setOks[0].model, 'string', 'it names the model the words were typed on');
// D. A refused write: setError, never setOk.
engine.refuse = true; engine.description = ''; log.length = 0;
page.send({ type: 'load', key: JSON.stringify(['measure:Sales/Average Order Value']), name: 'Average Order Value', props: [
  { name: 'Description', displayName: 'Description', kind: 'string', category: 'Basic', value: '' },
] });
const before = hostPosts.length;
await host.saveCommand();
await sleep(100);
const after = hostPosts.slice(before).map((m) => m.type);
assert.ok(after.includes('setError'), `a refused write answers setError. It sent: ${after.join(', ')}`);
assert.ok(!after.includes('setOk'), 'a refused write never answers setOk');

// C. A grid that never answers must not hold a Save hostage.
const silent = host.makeGrid('file:///ext2');
silent.resolveWebviewView({ webview: { options: {}, html: '', asWebviewUri: (u) => u, cspSource: '', onDidReceiveMessage: () => undefined, postMessage: () => Promise.resolve(true) } });
const t0 = Date.now();
await silent.commitFocused();
assert.ok(Date.now() - t0 < 3000, 'a silent view is waited for a moment, never forever');

console.log('save commits the focused field: ok');
