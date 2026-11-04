// cx4 (the 1.7.4-rc2 tester, 3 Nov): after a live open, the window mixed two models for 5 to 8 s.
//
//   The dialog closed at about 9 s and the tree named pilot2, but the status bar said "8 measures" (pilot2 has 12), the Plan said
//   "AI ready C 73.3" (pilot2 is F 56.9) with the old model's tasks, Properties said "Checking 6 of 8" and the footer named the old
//   model. The right numbers came at 15 to 23 s, 3 of 3 opens. A wrong number shown as right.
//
//   Cause: the open answers the page and posts 'reconnected', but every per-model figure stays on screen until that surface's own
//   read of the new model comes back, and those reads waited behind the measure checks' planner on the engine's model thread (the
//   engine half is Semanticus.Tests/CheckedPlannerThreadTests.cs).
//
//   Rule now: from the moment the open answers until a surface has read the new model, it shows the new model's value or one short
//   line, "Reading <model>...", never the old model's value.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader } from './helpers/webview-load.mjs';
import { makePage, find, settleAll } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.window.addEventListener ??= () => undefined;
globalThis.window.removeEventListener ??= () => undefined;
globalThis.document ??= { activeElement: null, querySelector: () => null };
globalThis.matchMedia ??= () => ({ matches: false, addEventListener: () => undefined, removeEventListener: () => undefined });
globalThis.window.matchMedia ??= globalThis.matchMedia;
globalThis.acquireVsCodeApi ??= () => ({ postMessage: () => undefined, getState: () => ({}), setState: () => undefined });

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// Source pins must read the same text on LF and Windows CRLF checkouts.
const src = (...p) => readFileSync(resolve(root, ...p), 'utf8').replaceAll('\r\n', '\n');
const allText = (el) => {
  if (el == null || typeof el === 'boolean') return '';
  if (typeof el === 'string' || typeof el === 'number') return String(el);
  if (Array.isArray(el)) return el.map(allText).join(' ');
  if (typeof el.type === 'function' && el.props) { try { return allText(el.type(el.props)); } catch { return allText(el.props.children); } }
  return allText(el.props?.children);
};

const A = { sessionId: 's-a', modelName: 'Contoso Local', measures: 8 };
const B = { sessionId: 's-b', name: 'Contoso Published' };

// ---- the host: what the open hands the window --------------------------------------------------------------------------------------------------------------

test('the host names the new model from the open\'s answer, and the reading line is short and plain', () => {
  const M = makeLoader()('../../src/swapReading.ts');
  assert.deepEqual(M.swapModelOf({ sessionId: 's-b', modelName: 'Contoso Published', measures: 12 }), { sessionId: 's-b', name: 'Contoso Published' });
  assert.deepEqual(M.swapModelOf({ sessionId: 's-b', context: { editing: { modelName: 'Contoso Edit' } }, modelName: 'x' }), { sessionId: 's-b', name: 'Contoso Edit' },
    'the name the Properties step uses');
  assert.equal(M.swapModelOf({ restored: true }), undefined, 'an answer with no session names no swap: the window keeps today\'s order');
  assert.equal(M.swapModelOf(undefined), undefined);
  assert.equal(M.swapReadingLine('Contoso Published'), 'Reading Contoso Published...');
  const chip = M.swapReadingChip('Contoso Published');
  assert.match(chip.text, /Reading Contoso Published\.\.\./);
  assert.doesNotMatch(chip.text, /measure/, 'the old count is never on the chip while the new model is read');
});

test('the swap block shows the reading state on every surface BEFORE the window waits on any read of the new model', () => {
  const ext = src('src', 'extension.ts');
  const start = ext.indexOf('if (modelSwapped) {');
  assert.ok(start > 0, 'the swap block');
  const block = ext.slice(start, ext.indexOf("if (!answered) panel.webview.postMessage({ type: 'rpcResult'", start));
  const firstAwait = block.indexOf('await ');
  assert.ok(firstAwait > 0);
  const before = block.slice(0, firstAwait);
  assert.match(before, /const swap = swapModelOf\(result\)/, 'the new model is named from the answer');
  // cx5: the open watch paints it (src/openWatch.ts), unless a change that named the new session already did.
  assert.match(before, /openWatch\.seen\(swap\.sessionId, swap\.name\)/, 'the native status bar and the tree count say Reading before any await');
  assert.match(ext, /reading: \(swap\) => \{ showSwapReading\(swap\); propGrid\?\.reading\(swap\.name\); \}/, 'the watch\'s reading step is the cx4 one');
  assert.match(before, /postToPanels\(\{ type: 'reconnected', by: swapBy, model: swap \}\)/, 'Studio hears which model is coming, before any await');
  assert.ok(before.indexOf('openWatch.seen(swap.sessionId') < before.indexOf("postToPanels({ type: 'reconnected'"), 'the native bar changes no later than Studio');
  assert.match(before, /propGrid\.reading\(/, 'Properties drops the old card before it waits on the read');
  // The native chip: the old count goes at once, and the new count fills in when the new session is read (setStatusFromInfo).
  const show = ext.slice(ext.indexOf('function showSwapReading('), ext.indexOf('function showSwapReading(') + 1200);
  assert.match(show, /checksStatus\.text = /);
  assert.match(show, /swapReadingChip\(/);
  assert.match(show, /tree\?\.setChecksCount\(undefined\)/);
  assert.match(show, /checksCountRead\+\+|\+\+checksCountRead/, 'a count read for the old model that lands later is dropped');
  // The tree's model row (seen in the shots after the first fix): the new name and "Reading...", none of the old row's file or live state.
  assert.match(show, /tree\?\.setIdentity\(\{ name: swap\.name, source: '', sourceFull: '', live: false, liveBound: false, unsaved: false, reading: true(, opening: undefined)? \}\)/);
  assert.match(ext, /unsaved: !!\(info\.hasUnsavedEdits \?\? info\.hasUnsavedChanges\),\n\s+reading: false,/, 'the read of the new session ends it');
  assert.match(ext, /if \(id\?\.reading\) \{ row\.description = 'Reading\.\.\.';/);
});

test('Properties: the reading message drops the old object and its check card, and names the new model', () => {
  const js = src('media', 'propgrid', 'propgrid.js');
  const line = js.split('\n').find((l) => l.includes("m.type === 'reading'"));
  assert.ok(line, 'propgrid.js handles the reading message');
  assert.match(line, /state = null/);
  assert.match(line, /esc\(/, 'the model name is escaped');
  assert.match(line, /semanticus-marks-load/, 'the check card under the grid is redrawn for no object');
  const ext = src('src', 'extension.ts');
  const reading = ext.slice(ext.indexOf('    reading(name: string'), ext.indexOf('    reading(name: string') + 500);
  assert.match(reading, /\+\+this\._pushSeq/, 'a push of the old model still in flight is dropped');
  assert.match(reading, /type: 'reading'/);
});

// ---- Studio: the page, the header and the footer ----------------------------------------------------------------------------------------------------------

function swapWorld() {
  const listeners = new Set();
  return {
    listeners,
    bridge: { onReconnect: (fn) => { listeners.add(fn); return () => listeners.delete(fn); } },
    reconnected: (by, model) => { for (const fn of [...listeners]) fn(by, model); },
  };
}

test('Studio: from the swap until the page has read the new model it says "Reading <model>...", then it is gone', async () => {
  const w = swapWorld();
  const pg = makePage({ './bridge': w.bridge });
  const { useSwapReading } = pg.load('swap-reading.ts');
  const shown = { id: A.sessionId };
  const h = pg.mount(() => useSwapReading(shown.id));
  await settleAll();
  assert.equal(h.result, null, 'no swap, no reading line');
  w.reconnected('person', B);
  await settleAll();
  assert.deepEqual(h.result, { name: 'Contoso Published', line: 'Reading Contoso Published...' }, 'the page still shows A, and says it is reading B');
  shown.id = B.sessionId; h.rerender(); await settleAll();
  assert.equal(h.result, null, 'B is read: the line goes');
  // A newer open while B is shown, then the page reads a third model (another door opened it first): the line never sticks.
  w.reconnected('person', { sessionId: 's-c', name: 'Contoso Third' });
  await settleAll();
  assert.equal(h.result?.name, 'Contoso Third');
  shown.id = 's-d'; h.rerender(); await settleAll();
  assert.equal(h.result, null, 'any newer model the page reads ends the line');
  // A swap the host could not name (a restore, an engine restart) keeps today's behaviour.
  w.reconnected('restore', undefined);
  await settleAll();
  assert.equal(h.result, null);
  // A reconnect to the model already shown is not a swap.
  w.reconnected('person', { sessionId: 's-d', name: 'Contoso Fourth' });
  await settleAll();
  assert.equal(h.result, null);
  h.unmount();
});

test('Studio footer: while reading, it names the new model and never the old one', async () => {
  const pg = makePage({
    './connection': { useConnection: () => ({ session: { ...A }, conn: { connected: true }, reading: { name: B.name, line: 'Reading Contoso Published...' },
      context: { editing: { modelName: A.modelName, available: true, live: false }, querying: { available: true, modelName: A.modelName, live: true }, publishing: { available: false, live: false } } }) },
    './personality/link': { LinkStatus: () => null },
    './plan-notes': { PlanNotes: () => null },
  });
  const { ContextBar } = pg.load('contextbar.tsx');
  const h = pg.mount(() => ContextBar({ onConnections: () => undefined }));
  await settleAll();
  const said = allText(h.result);
  assert.match(said, /Reading Contoso Published\.\.\./);
  assert.doesNotMatch(said, /Contoso Local/, 'the old model is not named under the new one');
  h.unmount();
});

test('Studio page: the reading branch comes before every per-model page, and the always-mounted Checks view waits too', () => {
  const app = src('webview', 'src', 'App.tsx');
  assert.match(app, /const swapReading = useSwapReading\(session\?\.sessionId\)/);
  const body = app.slice(app.indexOf("{tab === 'pro' ? ("), app.indexOf('<PlanView sessionId={session.sessionId}'));
  assert.match(body, /\) : swapReading \? \(/, 'the page body shows the reading line');
  assert.ok(body.indexOf('swapReading ?') < body.indexOf("!session?.sessionId ? ("), 'before the empty page and before the Plan (grade, tasks)');
  assert.match(app, /\{session\?\.sessionId && !swapReading && <div className=\{\(tab === 'tests'/, 'the Checks view (its counts) is not shown for the old model');
  // The header names the model being read, not the old one.
  assert.match(app, /reading\?\.name \?\? \(?shellSession\?\.modelName/);
  // The provider hands the same state to the header and the footer.
  const conn = src('webview', 'src', 'connection.tsx');
  assert.match(conn, /const reading = useSwapReading\(session\?\.sessionId\)/);
});

test('the bridge passes the model the host names on \'reconnected\' to every listener', () => {
  const posted = [];
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState: () => undefined });
  const handlers = [];
  const add = globalThis.window.addEventListener;
  globalThis.window.addEventListener = (type, fn) => { if (type === 'message') handlers.push(fn); };
  try {
    const bridge = makeLoader({}, null)('bridge.ts');
    const seen = [];
    bridge.onReconnect((by, model) => seen.push([by, model]));
    for (const fn of handlers) fn({ data: { type: 'reconnected', by: 'person', model: { sessionId: 's-b', name: 'Contoso Published' } } });
    for (const fn of handlers) fn({ data: { type: 'reconnected', by: 'person' } });
    for (const fn of handlers) fn({ data: { type: 'reconnected', by: 'person', model: { sessionId: 7 } } });
    assert.deepEqual(seen, [['person', { sessionId: 's-b', name: 'Contoso Published' }], ['person', undefined], ['person', undefined]]);
  } finally { globalThis.window.addEventListener = add; }
});
