import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import ts from 'typescript';

const require = createRequire(new URL('../webview/package.json', import.meta.url));
const studio = new URL('../webview/src/studio/', import.meta.url);
function load(name) {
  const source = readFileSync(new URL(name, studio), 'utf8');
  const { outputText } = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020,
    jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true,
  } });
  const module = { exports: {} };
  new Function('require', 'module', 'exports', outputText)((id) => {
    if (id.endsWith('.css')) return {};
    if (id === './presses.json') return JSON.parse(readFileSync(new URL('presses.json', studio)));
    if (id === './notes-runtime') return {};
    if (id === './notes-nav') return { moveScreenTo: () => {} };
    if (id === './driver-store') return load('driver-store.ts');
    return require(id);
  }, module, module.exports);
  return module.exports;
}
const cursor = load('cursor.tsx');

test('press lookup uses the explicit id before the tool', () => {
  assert.equal(cursor.pressFor({ press: 'run-plan', tool: 'save_model' }).id, 'run-plan');
  assert.equal(cursor.pressFor({ tool: 'apply_plan_items' }).id, 'run-plan');
  assert.equal(cursor.pressFor({ tool: 'impact_assessment' }).id, 'lineage');
  assert.equal(cursor.pressFor({ press: 'unknown' }), undefined);
  assert.equal(cursor.pressFor({ tool: 'unknown' }), undefined);
});

test('press map follows the contract and shares one engine resource', () => {
  const map = JSON.parse(readFileSync(new URL('presses.json', studio)));
  assert.equal(map.version, 1);
  const ids = ['read-source', 'approve-build', 'run-plan', 'undo', 'save', 'publish', 'publish-new', 'ask', 'explain', 'lineage'];
  assert.deepEqual(map.presses.map((p) => p.id), ids);
  const placesFile = new URL('places.json', studio);
  const places = existsSync(placesFile)
    ? JSON.parse(readFileSync(placesFile)).places.map((p) => p.id)
    : ['plan/start', 'plan', 'publish/changes', 'publish/new-model', 'explore/ask', 'edit/queries', 'explore/lineage'];
  const tools = new Set();
  for (const press of map.presses) {
    const keys = Object.keys(press).filter((k) => k !== 'say' && k !== 'previewSay').sort();
    assert.deepEqual(keys, ['id', 'label', 'place', 'selector', 'tools']);
    // A press on a real button names it in the note, in the button's own words (Kane, 11 Oct: "seeing the buttons clicked clearly").
    if (press.say !== undefined) assert.equal(press.say, `Your assistant pressed ${press.label}.`);
    // A dry run presses nothing (h17 P8): the publish presses say so in their own words.
    if (press.previewSay !== undefined) assert.match(press.previewSay, /^Your assistant previewed the /);
    assert.ok(places.includes(press.place), press.place);
    assert.ok(press.label.length && press.selector.length);
    assert.ok(Array.isArray(press.tools) && press.tools.length);
    for (const tool of press.tools) { assert.ok(!tools.has(tool)); tools.add(tool); }
  }
  const project = readFileSync(new URL('../../Semanticus.Engine/Semanticus.Engine.csproj', import.meta.url), 'utf8');
  assert.ok(project.includes('LogicalName="Semanticus.Engine.studio-presses.json"'));
});

// Kane, 11 Oct: "mouse movement isn't that important but seeing the buttons clicked clearly is." Kane, 12 Oct (option A): a ring on the
// real button for every press, no mouse pointer, and the Studio opens the button's page first when the button is elsewhere.
function fakeTimers() {
  let now = 0, seq = 0; const timers = new Map();
  return { set(fn, ms) { timers.set(++seq, { at: now + ms, fn }); return seq; }, clear(id) { timers.delete(id); },
    advance(ms) { now += ms; for (const [id, t] of [...timers]) if (t.at <= now) { timers.delete(id); t.fn(); } } };
}
function fakeControl() {
  const attrs = new Map();
  return { attrs, setAttribute: (k, v) => attrs.set(k, v), removeAttribute: (k) => attrs.delete(k) };
}

test('the control itself shows ringed at once, for a moment, then lets go', () => {
  const t = fakeTimers(); const button = fakeControl();
  cursor.pressControl(button, t);
  assert.equal(button.attrs.get('data-claude-press'), 'on', 'no glide to wait for');
  t.advance(cursor.PRESS_MS - 1);
  assert.equal(button.attrs.get('data-claude-press'), 'on', 'held long enough to see');
  t.advance(1);
  assert.equal(button.attrs.has('data-claude-press'), false);
});

test('stopping lets go at once', () => {
  const t = fakeTimers(); const button = fakeControl();
  const stop = cursor.pressControl(button, t);
  stop();
  assert.equal(button.attrs.has('data-claude-press'), false, 'the person took the wheel');
  t.advance(10000);
  assert.equal(button.attrs.has('data-claude-press'), false);
});

// The press, on a fake page: the port is the real page's four questions (is it drawn, open its page, ring it, may I move).
function page({ can = true } = {}) {
  let clock = 0, seq = 0; const frames = new Map();
  const state = { drawnAt: null, canMove: can, opened: 0, rung: [], letGo: 0, found: 0 };
  const port = {
    now: () => clock,
    frame: (fn) => { frames.set(++seq, fn); return seq; },
    cancelFrame: (id) => frames.delete(id),
    canMove: () => state.canMove,
    find: () => { state.found++; return state.drawnAt !== null && clock >= state.drawnAt ? { name: 'button' } : null; },
    openPage: () => { state.opened++; },
    show: (control) => { state.rung.push(control); return () => { state.letGo++; }; },
  };
  return { state, port, frames, tick(ms = 16) { clock += ms; const pending = [...frames.values()]; frames.clear(); pending.forEach((fn) => fn()); },
    run(ms) { for (let t = 0; t < ms; t += 16) this.tick(16); } };
}

test('a button on this page is ringed at once, and its page is not opened again', () => {
  const p = page(); p.state.drawnAt = 0;
  cursor.playPress(p.port, true);
  assert.equal(p.state.rung.length, 1);
  assert.equal(p.state.opened, 0);
  assert.equal(p.frames.size, 0, 'nothing keeps polling once it is ringed');
});

test('a button on another page: the page opens first, the press waits for it to draw, then rings it (the bug in Kane\'s session)', () => {
  const p = page(); p.state.drawnAt = 700;   // the page needs 700 ms to open and draw
  cursor.playPress(p.port, true);
  assert.equal(p.state.opened, 1, 'the page is opened');
  assert.equal(p.state.rung.length, 0, 'not rung before it is drawn');
  p.run(500);
  assert.equal(p.state.rung.length, 0);
  p.run(400);
  assert.equal(p.state.rung.length, 1, 'rung once it drew');
  assert.equal(p.state.opened, 1);
  assert.equal(p.frames.size, 0);
});

test('a page that never draws the button gives up quietly after the wait', () => {
  const p = page();
  cursor.playPress(p.port, true);
  p.run(cursor.PRESS_WAIT_MS + 100);
  assert.equal(p.state.rung.length, 0);
  assert.equal(p.frames.size, 0, 'no timer is left running');
});

test('the person taking the wheel stops the wait and lets go of a ring', () => {
  const p = page(); p.state.drawnAt = 500;
  const stop = cursor.playPress(p.port, true);
  p.run(160);
  p.state.canMove = false; p.tick();
  assert.equal(p.frames.size, 0, 'stopped waiting');
  p.state.drawnAt = 0; p.run(500);
  assert.equal(p.state.rung.length, 0, 'nothing is rung for a person who took the wheel');
  const q = page(); q.state.drawnAt = 0;
  const stopQ = cursor.playPress(q.port, false);
  stopQ();
  assert.equal(q.state.letGo, 1, 'a ring that is showing is let go');
  stop();
});

test('with the wheel already the person\'s, nothing opens and nothing is ringed: the press stays noted', () => {
  const p = page({ can: false }); p.state.drawnAt = 0;
  cursor.playPress(p.port, true);
  assert.equal(p.state.opened, 0);
  assert.equal(p.state.rung.length, 0);
  assert.equal(p.frames.size, 0);
});

test('there is no mouse pointer left: no glide, ripple, highlight or label in the cursor module or its styles', () => {
  const source = readFileSync(new URL('cursor.tsx', studio), 'utf8');
  const css = readFileSync(new URL('studio-cursor.css', studio), 'utf8');
  for (const gone of [/createCursorPlayer/, /GLIDE_MS/, /sc-pointer/, /sc-ripple/, /sc-highlight/, /studio-cursor-ripple/, /createPortal/])
    assert.doesNotMatch(source + css, gone);
});

test('every press on a real button has a sentence that names it', () => {
  const map = JSON.parse(readFileSync(new URL('presses.json', studio)));
  for (const id of ['read-source', 'approve-build', 'run-plan', 'undo', 'save', 'publish', 'publish-new', 'ask'])
    assert.ok(map.presses.find((p) => p.id === id).say, id);
  const css = readFileSync(new URL('studio-cursor.css', studio), 'utf8');
  assert.match(css, /\[data-claude-press\]/, 'the pressed look is styled');
});
