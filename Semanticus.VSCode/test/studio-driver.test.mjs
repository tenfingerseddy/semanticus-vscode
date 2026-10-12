import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import ts from 'typescript';

const require = createRequire(new URL('../webview/package.json', import.meta.url));
function load(name) {
  const source = readFileSync(new URL(`../webview/src/studio/${name}`, import.meta.url), 'utf8');
  const { outputText } = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, jsx: ts.JsxEmit.ReactJSX,
  } });
  const module = { exports: {} };
  new Function('require', 'module', 'exports', outputText)(
    (id) => id === './driver-store' ? storeModule : id === './learn-state' ? { useLearn: () => ({ runtime: { phase: 'idle' } }) }
      : id === './assistant-sign' ? load('assistant-sign.tsx') : id === './assistant-working' ? load('assistant-working.ts') : id.endsWith('.css') ? {} : require(id), module, module.exports);
  return module.exports;
}
const storeModule = load('driver-store.ts');
const { createDriverStore, assistantIsWorking, QUIET_GAP_MS } = storeModule;
const driving = () => createDriverStore('claudeDrives');

test('show arrives: idle starts; driving queues; person and I drive do not move', () => {
  const s = driving();
  assert.equal(s.getSnapshot().state, 'idle');
  s.noteShow();
  assert.equal(s.getSnapshot().state, 'claudeDriving');
  s.noteShow();
  assert.equal(s.getSnapshot().pendingNotes, 2);
  s.notePersonInput(1000);
  s.noteShow();
  assert.equal(s.getSnapshot().state, 'personHasWheel');
  assert.equal(s.canMoveScreen(), false);
  s.setMode('iDrive');
  s.noteShow();
  assert.equal(s.getSnapshot().state, 'idle');
  assert.equal(s.canMoveScreen(), false);
});

test('person input: every Claude state yields, repeated input resets the clock', () => {
  for (const start of ['idle', 'claudeDriving', 'personHasWheel']) {
    const s = driving();
    if (start === 'claudeDriving') s.noteShow();
    if (start === 'personHasWheel') s.notePersonInput(500);
    s.notePersonInput(1000);
    assert.equal(s.getSnapshot().state, 'personHasWheel');
    s.notePersonInput(2000);
    assert.equal(s.getSnapshot().lastPersonInput, 2000);
    assert.equal(s.canMoveScreen(), false);
  }
  const s = createDriverStore('iDrive');
  s.notePersonInput(1000);
  assert.equal(s.getSnapshot().state, 'idle');
});

const at = (ms) => new Date(ms).toISOString();

test('next prompt: the wheel returns on the first call after a quiet gap of 8 s, not before', () => {
  assert.equal(QUIET_GAP_MS, 8000);
  const s = driving();
  s.notePersonInput(1000);
  s.noteShow(); s.noteShow(); s.noteShow();
  s.noteAssistantCall(at(8999));                       // 7999 ms after the click: Claude is still on its task
  assert.equal(s.getSnapshot().state, 'personHasWheel');
  assert.equal(s.getSnapshot().pendingNotes, 3);
  s.noteAssistantCall(at(16998));                      // 7999 ms after the last call
  assert.equal(s.getSnapshot().state, 'personHasWheel');
  s.noteAssistantCall(at(24998));                      // exactly 8000 ms after the last call: a new prompt started
  assert.equal(s.getSnapshot().state, 'claudeDriving');
  assert.equal(s.getSnapshot().pendingNotes, 1);
  assert.equal(s.getSnapshot().resumeVersion, 1);
  assert.equal(s.canMoveScreen(), true);
});

test('next prompt: within one busy task the wheel stays with the person, however long it runs', () => {
  const s = driving();
  s.notePersonInput(1000);
  for (let t = 3000; t <= 120000; t += 5000) {          // a call every 5 s for two minutes (D6: the old rule gave the wheel back at 15 s)
    s.noteAssistantCall(at(t));
    assert.equal(s.getSnapshot().state, 'personHasWheel', `still the person's at ${t}`);
  }
  assert.equal(s.canMoveScreen(), false);
});

test('next prompt: Claude stops and the person types, so the first call 8 s or more after the last call or the click resumes', () => {
  const idle = driving();                               // the person clicks while Claude is idle, then types a prompt
  idle.noteAssistantCall(at(100));
  idle.notePersonInput(20000);
  idle.noteAssistantCall(at(27999));
  assert.equal(idle.getSnapshot().state, 'personHasWheel');
  idle.noteAssistantCall(at(35999));
  assert.equal(idle.getSnapshot().state, 'claudeDriving');
  const busy = driving();                               // the click lands mid task; calls go on every 4 s; the task ends; the prompt comes later
  busy.notePersonInput(20000);
  busy.noteAssistantCall(at(24000)); busy.noteAssistantCall(at(28000)); busy.noteAssistantCall(at(32000));
  assert.equal(busy.getSnapshot().state, 'personHasWheel');
  busy.noteAssistantCall(at(70000));                   // 38 s of quiet, then a call
  assert.equal(busy.getSnapshot().state, 'claudeDriving');
});

test('next prompt: a call that began before the click never resumes; the latest click wins; I drive stays off', () => {
  const s = driving();
  s.noteAssistantCall(at(0));
  s.notePersonInput(30000);
  s.noteAssistantCall(at(29000));                      // began before the click, only reported now
  s.noteAssistantCall(at(30000));                      // same instant
  assert.equal(s.getSnapshot().state, 'personHasWheel');
  s.notePersonInput(40000);
  s.noteAssistantCall(at(47999));
  assert.equal(s.getSnapshot().state, 'personHasWheel');
  for (const bad of ['bad time', '']) { s.noteAssistantCall(bad); assert.equal(s.getSnapshot().state, 'personHasWheel'); }
  s.setMode('iDrive'); s.noteAssistantCall(at(90000));
  assert.equal(s.getSnapshot().state, 'idle');
  assert.equal(s.canMoveScreen(), false);
  assert.equal(s.getSnapshot().assistantConnected, true);
});

test('Let Claude drive resumes the newest waiting note, or idle with an empty queue', () => {
  for (const pending of [0, 3]) {
    const s = driving(); s.notePersonInput(1000);
    for (let n = 0; n < pending; n++) s.noteShow();
    s.letClaudeDrive();
    assert.equal(s.getSnapshot().state, pending ? 'claudeDriving' : 'idle');
    assert.equal(s.getSnapshot().pendingNotes, pending ? 1 : 0);
    assert.equal(s.getSnapshot().resumeVersion, 1);
  }
  const s = createDriverStore('iDrive'); s.letClaudeDrive();
  assert.equal(s.canMoveScreen(), false);
});

test('mode switch stops moves and resumes at idle; reading the queue returns to idle', () => {
  const s = driving(); s.noteShow();
  s.setMode('iDrive');
  assert.equal(s.getSnapshot().state, 'idle');
  assert.equal(s.canMoveScreen(), false);
  s.setMode('claudeDrives');
  assert.equal(s.getSnapshot().state, 'idle');
  assert.equal(s.canMoveScreen(), true);
  s.noteShow(); s.noteQueueSize(0);
  assert.equal(s.getSnapshot().state, 'idle');
  s.notePersonInput(1000); s.noteQueueSize(0);
  assert.equal(s.getSnapshot().state, 'personHasWheel');
});

test('subscriptions publish changes synchronously and unsubscribe', () => {
  const s = driving(); const states = [];
  const off = s.subscribe(() => states.push(s.getSnapshot().state));
  s.noteShow(); s.notePersonInput(1000); off(); s.setMode('iDrive');
  assert.deepEqual(states, ['claudeDriving', 'personHasWheel']);
});

test('next prompt starts driving with no waiting notes and reveals the bar', () => {
  const s = driving(); s.notePersonInput(0);
  s.noteAssistantCall(at(8000));
  assert.equal(s.getSnapshot().state, 'claudeDriving');
  assert.equal(s.getSnapshot().assistantConnected, true);
});

test('bar renders the three states, exact copy, token colours, and the selected mode', () => {
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { DriverBarView } = load('driver-bar.tsx');
  const s = driving();
  const draw = (extra = {}) => renderToStaticMarkup(React.createElement(DriverBarView, {
    driver: s.getSnapshot(), onMode() {}, onResume() {}, ...extra,
  }));
  assert.equal(draw(), '');
  s.setConnected(true);
  let html = draw();
  // N9 (hero16): the connection file exists but no assistant has started. The bar says so plainly and does not claim Claude is driving.
  assert.ok(html.includes('Waiting for your assistant. Open Claude Code in this folder to begin.'), html);
  assert.ok(!html.includes('Claude is driving'), 'no driving claim before an assistant has called');
  s.noteAssistantCall(at(0));
  html = draw({ working: true });
  // Nobody has told the engine a name yet: the bar says Your assistant, and never claims it is Claude.
  assert.ok(html.includes('Your assistant is working. Click anything to take the wheel.'), html);
  assert.ok(!html.includes('Claude is driving'));
  assert.ok(html.includes('var(--sem-doer-agent)'));
  assert.ok(html.includes('aria-pressed="true"'));
  assert.ok(!html.includes('Let Claude drive') && !html.includes('Let your assistant drive'));
  assert.ok(html.includes('Your assistant drives'), 'no name yet: the button says so, and never Claude');
  s.notePersonInput(1000); html = draw();
  assert.ok(html.includes('You have the wheel. Your next message to Your assistant gives it back.'));
  assert.ok(html.includes('var(--sem-doer-you)'));
  assert.ok(html.includes('Let your assistant drive'));
  s.setMode('iDrive'); html = draw();
  assert.ok(html.includes('I drive. Your assistant works but does not move your screen.'));
  assert.ok(html.includes('var(--sem-muted)'));
  assert.ok(!html.includes('Let your assistant drive'));
});

function loadHost() {
  const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('extension.ts', source, ts.ScriptTarget.Latest, true);
  const names = ['isStudioDriveMode', 'isStudioDriverState', 'reportStudioDriver',
    'noteStudioPersonInput', 'noteStudioAssistantCall', 'studioRelayHandler'];
  const functions = ast.statements.filter((node) => ts.isFunctionDeclaration(node) && names.includes(node.name?.text))
    .map((node) => node.getText(ast)).join(String.fromCharCode(10));
  const js = ts.transpileModule(functions, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText;
  const calls = []; const saved = []; const posted = [];
  const panel = { webview: { postMessage(message) { posted.push(message); } } };
  const make = new Function('panel', 'calls', 'saved', `
    let studioDriveMode = 'claudeDrives', studioDriverState = 'idle', studioLastPersonInput = null, studioLastAssistantCall = null;
    let studioPanel = panel;
    let studioDriveModeLoaded = true;
    const STUDIO_DRIVE_MODE_KEY = 'semanticus.studio.driveMode';
    const conn = { sendRequest(...args) { calls.push(args); return Promise.resolve(); } };
    const out = { appendLine() {} };
    const extCtx = { workspaceState: { update(...args) { saved.push(args); } } };
    ${js}
    return { relay: studioRelayHandler(panel, false), standalone: studioRelayHandler(panel, true),
      person: noteStudioPersonInput, assistant: noteStudioAssistantCall,
      setPanel(value) { studioPanel = value; },
      snapshot() { return { mode: studioDriveMode, state: studioDriverState, at: studioLastPersonInput }; } };
  `);
  return { ...make(panel, calls, saved), calls, saved, posted };
}

test('host mode messages persist the workspace setting and retain the wheel for the selected mode', async () => {
  const h = loadHost(); h.person();
  await h.relay({ type: 'setDriveMode', mode: 'claudeDrives' });
  assert.equal(h.snapshot().state, 'personHasWheel');
  await h.relay({ type: 'setDriveMode', mode: 'iDrive' });
  assert.equal(h.snapshot().state, 'idle');
  assert.deepEqual(h.saved.at(-1), ['semanticus.studio.driveMode', 'iDrive']);
  assert.deepEqual(h.calls.at(-1), ['setStudioDriver', 'iDrive', 'idle']);
  const count = h.saved.length;
  await h.standalone({ type: 'setDriveMode', mode: 'claudeDrives' });
  await h.relay({ type: 'setDriveMode', mode: 'other' });
  assert.equal(h.saved.length, count);
});

test('host reports person input and accepts store reports; a closed panel resumes after a quiet gap of 8 s', async () => {
  const h = loadHost(); h.person();
  assert.equal(h.posted.at(-1).type, 'personInput');
  assert.deepEqual(h.calls.at(-1), ['setStudioDriver', 'claudeDrives', 'personHasWheel']);
  await h.relay({ type: 'driverState', mode: 'claudeDrives', state: 'personHasWheel', lastPersonInput: 1000 });
  h.setPanel(undefined);
  h.assistant(at(8999));                               // 7999 ms after the click
  assert.equal(h.snapshot().state, 'personHasWheel');
  h.assistant(at(16998));                              // a call every 8 s less a millisecond: one busy task
  assert.equal(h.snapshot().state, 'personHasWheel');
  h.assistant(at(24998));
  assert.equal(h.snapshot().state, 'claudeDriving');
  await h.relay({ type: 'driverState', mode: 'iDrive', state: 'personHasWheel' });
  assert.equal(h.snapshot().state, 'idle');
});

test('D5: the host tells the engine who has the wheel again on every assistant call, so a lost report heals', async () => {
  const h = loadHost(); h.person();
  const before = h.calls.length;
  h.assistant(at(Date.now()));
  assert.equal(h.calls.length, before + 1, 'one more report on the call');
  assert.deepEqual(h.calls.at(-1), ['setStudioDriver', 'claudeDrives', 'personHasWheel']);
});

test('D5: a new engine connection starts from the engine defaults, so the host reports to it as soon as it connects', () => {
  const source = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  const idx = source.indexOf('        conn = connected;');
  assert.ok(idx > 0, 'the connect step is where it was');
  const after = source.slice(idx, idx + 900);
  assert.match(after, /reportStudioDriver\(\)/, 'the driver state is sent right after the connection is adopted');
});

// H3 (hero15, D5 again): a new model gives the Studio a new session, and App remounts everything under the session-keyed
// provider, the driver layer included. The host says the drive mode once per page load (studioReady), so a remounted layer
// must still report the person taking the wheel, or the engine keeps answering "moved".
function loadLayer(store, bridge, posts) {
  const source = readFileSync(new URL('../webview/src/studio/layer.tsx', import.meta.url), 'utf8');
  const { outputText } = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, jsx: ts.JsxEmit.ReactJSX,
  } });
  const cleanups = [];
  const stubs = {
    react: { useLayoutEffect: (fn) => { const off = fn(); if (off) cleanups.push(off); } },
    './driver-store': store,
    '../bridge': bridge,
    './driver-wire': { postDriverMessage: (m) => posts.push(m), onPersonInput: () => () => {} },
    './driver-bar': { DriverBar: () => null }, './note-strip': { NoteStrip: () => null }, './cursor': { StudioCursor: () => null }, './drive-edge': { DriveEdge: () => null },
    './answer-store': { setAssistantAnswer: () => {} },
  };
  const module = { exports: {} };
  new Function('require', 'module', 'exports', outputText)((id) => (id in stubs ? stubs[id] : require(id)), module, module.exports);
  return { mount: () => module.exports.StudioLayer(), unmount: () => cleanups.splice(0).forEach((off) => off()) };
}

test('H3: after a new model remounts the Studio, taking the wheel is still reported to the host', () => {
  const store = load('driver-store.ts');
  const modes = new Set();
  const listen = (set) => (fn) => { set.add(fn); return () => set.delete(fn); };
  const bridge = { onDriveMode: listen(modes), onAssistantCall: () => () => {}, onStudioShow: () => () => {}, onStudioAnswer: () => () => {},
    onReconnect: () => () => {}, rpc: async () => ({}) };
  const posts = [];
  const saved = globalThis.document;
  globalThis.document = { addEventListener() {}, removeEventListener() {} };
  try {
    const layer = loadLayer(store, bridge, posts);
    layer.mount();
    modes.forEach((fn) => fn('claudeDrives', true));        // studioReady: the host says the mode, once per page load
    assert.equal(posts.at(-1).state, 'idle');
    layer.unmount(); layer.mount();                          // a new model: a new session key remounts the page
    posts.length = 0;
    store.driverStore.notePersonInput(5000);
    const report = posts.filter((m) => m.type === 'driverState').at(-1);
    assert.ok(report, 'the click reached the host');
    assert.equal(report.mode, 'claudeDrives');
    assert.equal(report.state, 'personHasWheel');
    layer.unmount();
  } finally { globalThis.document = saved; }
});

// Kane, 11 Oct: "I drive" did not light up when it was on; only "Claude drives" changed colour. Each mode shows which is on.
test('the selected mode button stands out in every state, I drive included', () => {
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { DriverBarView } = load('driver-bar.tsx');
  const s = driving(); s.setConnected(true);
  const styles = () => {
    const html = renderToStaticMarkup(React.createElement(DriverBarView, { driver: s.getSnapshot(), onMode() {}, onResume() {} }));
    return Object.fromEntries([...html.matchAll(/<button[^>]*data-testid="driver-mode-(\w+)"[^>]*aria-pressed="(\w+)"[^>]*style="([^"]*)"/g)]
      .map(([, mode, pressed, style]) => [mode, { pressed: pressed === 'true', style }]));
  };
  for (const [label, act] of [['Claude drives', () => {}], ['the person has the wheel', () => s.notePersonInput(1000)], ['I drive', () => s.setMode('iDrive')]]) {
    act();
    const b = styles();
    const on = Object.values(b).find((x) => x.pressed); const off = Object.values(b).find((x) => !x.pressed);
    assert.ok(on && off, label);
    assert.ok(!/color:\s*var\(--sem-muted\)/.test(on.style), `${label}: the selected button is not grey (${on.style})`);
    assert.match(on.style, /box-shadow:\s*inset 0 0 0 1px currentColor/, `${label}: the selected button has a ring`);
    assert.match(off.style, /color:\s*var\(--sem-muted\)/, `${label}: the other button is quiet`);
    assert.doesNotMatch(off.style, /box-shadow/, `${label}: and has no ring`);
  }
});

// ---- The assistant sign (Kane, 12 Oct: option A, "from the mockup A for both options") ----

const claudeCode = { key: 'claude', name: 'Claude Code', initials: 'CC', version: '2.1.0' };
const drawBar = (React, snapshot, extra = {}) => require('react-dom/server').renderToStaticMarkup(
  React.createElement(load('driver-bar.tsx').DriverBarView, { driver: snapshot, onMode() {}, onResume() {}, ...extra }));

test('the store keeps who the assistant is: the call carries it, the same answer does not redraw, a missing one keeps the last', () => {
  const s = driving(); s.setConnected(true);
  let draws = 0; s.subscribe(() => { draws++; });
  s.noteAssistantCall(at(1000), claudeCode);
  assert.deepEqual({ ...s.getSnapshot().assistant, version: undefined }, { ...claudeCode, version: undefined });
  const before = draws;
  s.noteAssistantCall(at(1000), { ...claudeCode });   // a new object with the same answer on every call
  assert.equal(draws, before, 'the bar is not redrawn for a name it already has');
  s.noteAssistantCall(at(2000));                        // a client that sent no name
  assert.equal(s.getSnapshot().assistant.name, 'Claude Code');
  s.setAssistant({ key: 'codex', name: 'Codex', initials: 'CX' });
  assert.equal(s.getSnapshot().assistant.key, 'codex');
  s.setAssistant({ nonsense: true }); s.setAssistant(null);
  assert.equal(s.getSnapshot().assistant.key, 'codex', 'garbage from an older engine changes nothing');
  assert.equal(createDriverStore('iDrive').getSnapshot().assistant, null, 'no assistant, no name');
});

test('working means a note is showing or the last call began under one quiet gap ago', () => {
  const s = driving(); s.setConnected(true);
  assert.equal(assistantIsWorking(s.getSnapshot(), 5000), false, 'a connection file is not an assistant at work');
  s.noteAssistantCall(at(1000), claudeCode);
  assert.equal(assistantIsWorking(s.getSnapshot(), 1000 + QUIET_GAP_MS - 1), true);
  assert.equal(assistantIsWorking(s.getSnapshot(), 1000 + QUIET_GAP_MS), false, 'idle after the quiet gap');
  s.noteShow();
  assert.equal(assistantIsWorking(s.getSnapshot(), 1000 + QUIET_GAP_MS * 5), true, 'a note still playing is work');
});

test('the sign: the assistant\'s own tile pulses while it works, sits still when connected and idle, and is absent with no assistant', () => {
  const React = require('react');
  const s = driving();
  assert.equal(drawBar(React, s.getSnapshot()), '', 'no assistant, no bar, no sign');
  s.setConnected(true);
  assert.ok(!drawBar(React, s.getSnapshot()).includes('assistant-sign'), 'waiting for an assistant shows no sign');
  s.noteAssistantCall(at(1000), claudeCode);
  let html = drawBar(React, s.getSnapshot(), { working: true });
  assert.match(html, /data-testid="assistant-sign" data-state="working" data-assistant="claude"/);
  assert.ok(html.includes('>CC</b>'), 'the tile carries the assistant\'s initials');
  assert.ok(html.includes('Claude Code is working. Click anything to take the wheel.'), html);
  html = drawBar(React, s.getSnapshot(), { working: false });
  assert.match(html, /data-state="idle"/);
  assert.ok(html.includes('Claude Code is connected.'), html);
  assert.ok(!html.includes('Click anything'), 'idle does not tell the person to take a wheel it is not turning');
  // The person has the wheel and I drive keep the sign and name the assistant in the words.
  s.notePersonInput(2000);
  assert.ok(drawBar(React, s.getSnapshot(), { working: true }).includes('Your next message to Claude Code gives it back.'));
  s.setMode('iDrive');
  assert.ok(drawBar(React, s.getSnapshot(), { working: true }).includes('I drive. Claude Code works but does not move your screen.'));
});

test('the driver bar buttons name who drives: Claude for Claude Code and Claude Desktop, Codex for Codex, Your assistant when unknown', () => {
  const React = require('react');
  const s = driving(); s.setConnected(true);
  const buttons = () => drawBar(React, s.getSnapshot(), { working: true });
  s.noteAssistantCall(at(1000), claudeCode);
  assert.ok(buttons().includes('>Claude drives<'), 'Claude Code: Claude drives');
  s.setAssistant({ key: 'claude', name: 'Claude', initials: 'CL' });
  assert.ok(buttons().includes('>Claude drives<') && buttons().includes('Claude is working.'), 'Claude Desktop: Claude drives, and the words say Claude, not Claude Code');
  s.setAssistant({ key: 'codex', name: 'Codex', initials: 'CX' });
  assert.ok(buttons().includes('>Codex drives<') && !buttons().includes('Claude'), 'Codex: Codex drives');
  s.setAssistant({ key: 'unknown', name: 'Your assistant', initials: null });
  assert.ok(buttons().includes('>Your assistant drives<'));
  s.notePersonInput(2000);
  assert.ok(buttons().includes('Let your assistant drive'));
  s.setAssistant({ key: 'codex', name: 'Codex', initials: 'CX' });
  assert.ok(buttons().includes('Let Codex drive'));
  s.setAssistant({ key: 'claude', name: 'Claude', initials: 'CL' });
  assert.ok(buttons().includes('Let Claude drive'));
});

test('the sign: an assistant with no name gets three dots, and its own plain name is shown as sent', () => {
  const React = require('react');
  const s = driving(); s.setConnected(true);
  s.noteAssistantCall(at(1000), { key: 'unknown', name: 'Your assistant', initials: null });
  let html = drawBar(React, s.getSnapshot(), { working: true });
  assert.ok(html.includes('<circle'), 'three dots, not letters');
  assert.ok(html.includes('Your assistant is working.'));
  s.setAssistant({ key: 'other', name: 'Zed Agent', initials: 'ZA' });
  html = drawBar(React, s.getSnapshot(), { working: false });
  assert.ok(html.includes('>ZA</b>') && html.includes('Zed Agent is connected.'), html);
});

test('the sign moves by transform and opacity only, stands still for reduced motion, and a hidden Studio sets no timer', () => {
  const css = readFileSync(new URL('../webview/src/studio/assistant-sign.css', import.meta.url), 'utf8');
  const keyframes = [...css.matchAll(/@keyframes [\w-]+ \{((?:[^{}]|\{[^}]*\})*)\}/g)].map((m) => m[1]);
  assert.equal(keyframes.length, 2);
  for (const frames of keyframes) {
    const props = new Set([...frames.matchAll(/([\w-]+)\s*:/g)].map((m) => m[1]));
    for (const p of props) assert.ok(p === 'transform' || p === 'opacity', `animated property ${p}`);
  }
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)[^{]*\{[^}]*animation: none/);
  assert.match(css, /body\.vscode-reduce-motion[^{]*\{[^}]*animation: none/);
  assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgb\(/, 'tokens only, no colours of its own');
  const bar = readFileSync(new URL('../webview/src/studio/assistant-working.ts', import.meta.url), 'utf8');
  assert.match(bar, /document\.hidden\) return;/, 'the working timer is not armed while the page is hidden');
  assert.match(bar, /visibilitychange/);
  const sign = readFileSync(new URL('../webview/src/studio/assistant-sign.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(sign, /<img|\.svg|href=|url\(/i, 'a tile Semanticus draws, never another company\'s logo');
});

test('the Studio reads who is attached from the entitlement answer, on mount and after a reconnect', async () => {
  const store = load('driver-store.ts');
  const calls = [];
  const reconnects = new Set();
  const bridge = { onDriveMode: () => () => {}, onAssistantCall: () => () => {}, onStudioShow: () => () => {}, onStudioAnswer: () => () => {},
    onReconnect: (fn) => { reconnects.add(fn); return () => reconnects.delete(fn); },
    rpc: async (method) => { calls.push(method); return { tier: 'free', assistant: { key: 'codex', name: 'Codex', initials: 'CX' } }; } };
  const saved = globalThis.document;
  globalThis.document = { addEventListener() {}, removeEventListener() {} };
  try {
    const layer = loadLayer(store, bridge, []);
    layer.mount();
    await new Promise((done) => setTimeout(done, 5));
    assert.deepEqual(calls, ['getEntitlement']);
    assert.equal(store.driverStore.getSnapshot().assistant.name, 'Codex');
    reconnects.forEach((fn) => fn('engine'));
    await new Promise((done) => setTimeout(done, 5));
    assert.equal(calls.length, 2, 'asked again after a reconnect');
    layer.unmount();
  } finally { globalThis.document = saved; }
});
