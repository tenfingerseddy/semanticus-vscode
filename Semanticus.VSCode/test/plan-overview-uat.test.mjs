// The Plan overview lane (1.6, two real-user UATs of 1.5.104). Each test is the press or the screen a person met.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const require = createRequire(new URL('../webview/package.json', import.meta.url));
const src = (name) => readFileSync(new URL(`../webview/src/${name}`, import.meta.url), 'utf8');
import { existsSync } from 'node:fs';
import { dirname, join, normalize } from 'node:path';
const root = fileURLToPath(new URL('../webview/src/', import.meta.url));
const cache = new Map();
/** Loads a webview source file; a relative import is loaded the same way (links override it), a package comes from the webview's node_modules. */
function load(name, link = {}) {
  const key = name + JSON.stringify(Object.keys(link));
  if (cache.has(key)) return cache.get(key);
  const { outputText } = ts.transpileModule(src(name), { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, jsx: ts.JsxEmit.ReactJSX,
  } });
  const module = { exports: {} };
  cache.set(key, module.exports);
  const here = dirname(name);
  const req = (id) => {
    if (link[id]) return link[id];
    if (id.endsWith('.css')) return {};
    if (!id.startsWith('.')) return require(id);
    const base = normalize(join(here, id));
    for (const ext of ['', '.ts', '.tsx', '/index.ts', '/index.tsx']) if (existsSync(join(root, base + ext)) && /\.tsx?$/u.test(base + ext)) return load(base + ext, link);
    throw new Error('cannot load ' + id + ' from ' + name);
  };
  new Function('require', 'module', 'exports', outputText)(req, module, module.exports);
  cache.set(key, module.exports);
  return module.exports;
}

// A tiny stand-in for the DOM: an Element with closest() over a chain of selectors it matches.
class FakeElement { constructor(marks, parent = null) { this.marks = marks; this.parent = parent; }
  closest(sel) { const want = sel.split(',').map((s) => s.trim());
    for (let e = this; e; e = e.parent) if (want.some((w) => e.marks.includes(w))) return e; return null; } }
globalThis.Element = FakeElement;

const noop = () => null;
const layer = load('studio/layer.tsx', {
  './driver-bar': {}, './driver-store': {}, '../bridge': {}, './answer-store': {}, './driver-wire': {}, './note-strip': {}, './cursor': {},
  './drive-edge': {}, '../plan-chrome': {},
});

test('K7: pressing a button that sends a step to the assistant does not take the wheel', () => {
  const studio = new FakeElement(['.studio-root']);
  const plain = new FakeElement(['button'], studio);
  assert.equal(layer.isPersonInput(plain), true, 'an ordinary click is still the person taking the wheel');
  const ask = new FakeElement(['button', '[data-keeps-wheel]'], studio);
  assert.equal(layer.isPersonInput(ask), false, 'Ask your assistant sends work to the assistant and leaves the switch alone');
});

const history = load('plan-history-model.ts');
test('P12: a record from before this person opened the model never says You', () => {
  assert.equal(history.whoOf('earlier'), 'earlier');
  assert.equal(history.WHO_WORD.earlier, 'Before you opened it');
  assert.notEqual(history.doerClass('earlier'), history.doerClass('you'));
  assert.equal(history.whoOf('you'), 'you');
  assert.ok(!history.WHO_FILTERS.some((f) => f.id === 'earlier'), 'it is not a filter');
});

const share = load('storage-share.ts');
test('P16: the biggest column is a finding at a clear share, and the page never says Nothing to fix while it is still looking', () => {
  // The Northstar shape from the UAT: AuditNote 4.0 MB, 54% of a 7.4 MB model.
  const cols = [{ table: 'Sales', column: 'AuditNote', totalSize: 4 * 1048576, pctOfModel: 53.99 }, { table: 'Sales', column: 'OrderNumber', totalSize: 924 * 1024, pctOfModel: 12.28 }];
  const f = share.biggestShare(cols);
  assert.deepEqual(f && [f.column, f.pct], ['AuditNote', 54]);
  assert.equal(share.shareWords(f), 'AuditNote is 54% of the model');
  assert.match(share.shareAdvice(f), /^It takes 4\.0 MB\. Ask your assistant/u);
  assert.equal(share.biggestShare([{ table: 'T', column: 'A', totalSize: 40 * 1024, pctOfModel: 80 }]), null, 'a trivial model has no cost worth naming');
  assert.equal(share.biggestShare([{ table: 'T', column: 'A', totalSize: 2 << 20, pctOfModel: 12 }]), null, 'a column under the share is not a finding');
  assert.equal(share.verdictWords('loading', 0), 'Looking for savings...');
  assert.equal(share.verdictWords('failed', 0), 'Could not check for savings.');
  assert.equal(share.verdictWords('done', 0), 'Nothing to fix.');
  assert.equal(share.verdictWords('done', 2), null);
});

const nextStoreMod = load('studio/next-store.ts');
test('F14: a press on one model is not drawn on another', () => {
  const store = nextStoreMod.createNextStore();
  store.setScope('northstar');
  store.set({ id: 'next:run', on: true, hidden: false, step: { ask: 'Ask your assistant to run the plan' } });
  store.press('next:run', 'Added. Tell Claude go.');
  assert.equal(store.getSnapshot().pressed?.text, 'Added. Tell Claude go.');
  store.setScope('northstar');
  assert.ok(store.getSnapshot().pressed, 'the same model keeps its press');
  store.setScope('contoso');
  assert.equal(store.getSnapshot().pressed, null, 'another model starts clean');
});

const calm = load('plan-calm-model.ts');
const model = load('plan-model.ts');
const slotIn = (over) => ({ press: null, receipt: null, work: null, hasNext: false,
  driver: { connected: false, mode: 'iDrive', wheel: false, assistantWorking: false, name: 'Your assistant' }, ...over });
test('F8: with no assistant the Plan speaks to the person, and rows never send them to an assistant that is not there', () => {
  // "Ask your assistant to run the plan" was the slot with nobody to ask. Now one quiet line offers the connection.
  assert.equal(calm.slotOf(slotIn({ hasNext: true, nextNeedsAssistant: true })).kind, 'connect');
  assert.equal(calm.slotOf(slotIn({})).kind, 'connect', 'a plan with no step offers it too');
  assert.equal(calm.slotOf(slotIn({ hasNext: true, nextNeedsAssistant: false })).kind, 'next', 'a step the person can do with Do it stays');
  assert.equal(calm.slotOf(slotIn({ hasNext: true, nextNeedsAssistant: true, driver: { connected: true, mode: 'claudeDrives', wheel: false, assistantWorking: false, name: 'Claude' } })).kind, 'next');
  // The nine rows said "Waiting, ask your assistant": plain words now, and none reads as a button.
  const note = 'Waiting for your assistant. Ask it to run the plan.';
  assert.equal(model.stateNoteShort(note, false), 'needs an assistant');
  assert.equal(model.stateNoteShort(note, true), 'for your assistant');
  assert.equal(model.stateNoteShort('Ask your assistant to run the plan.', false), 'needs an assistant');
  assert.equal(model.stateNoteShort("You paused your assistant's tasks. Ask your assistant to run the plan, or turn them back on.", true), 'paused by you');
  for (const n of [note, 'Ask your assistant to run the plan.']) for (const c of [true, false]) assert.doesNotMatch(model.stateNoteShort(n, c), /^ask/iu);
});

test('F19: each table has its own filter, and a capped grid says its filter searches the loaded rows', () => {
  const preview = src('datapreview.tsx');
  assert.match(preview, /<ResultGrid key=\{selected \?\? ''\}/u, 'a new table is a new grid, so the text typed on Customer is not still there on Sales');
  assert.match(preview, /truncated=\{res\.truncated\}/u, 'the grid is capped only when the preview got the one extra row it asked for');
  const grid = src('grid.tsx');
  assert.match(grid, /of \$\{rows\.length\.toLocaleString\(\)\}\$\{truncated \? ' loaded' : ''\}/u);
  assert.match(grid, /the filter searches only these/u);
});

test('F7: a record the model carried is not the person\'s history, so a first install gets the tour, not What changed', () => {
  assert.equal(history.historyEmptyForPerson([{ by: 'earlier' }, { by: 'earlier' }]), true);
  assert.equal(history.historyEmptyForPerson([{ by: 'earlier' }, { by: 'semanticus' }]), true, 'the plan\'s own first-time decision is not the person\'s history');
  assert.equal(history.historyEmptyForPerson([{ by: 'earlier' }, { by: 'you' }]), false);
  assert.equal(history.historyEmptyForPerson([{ by: 'assistant' }]), false);
  assert.equal(history.historyEmptyForPerson([]), true);
  assert.equal(history.historyEmptyForPerson(undefined), null, 'unknown shows nothing');
  assert.match(src('plan-guide.tsx'), /historyEmptyForPerson\(v\?\.rows\)/u, 'the guide asks the person\'s history, not the newest row');
});

test('P13 and F16: an opened row names three things and "and 7 more", and never names a tool', () => {
  const names = Array.from({ length: 10 }, (_, i) => `Sales[Col${i + 1}]`);
  assert.equal(model.firstNames(names), 'Sales[Col1], Sales[Col2], Sales[Col3] and 7 more');
  assert.equal(model.firstNames(['A', 'B']), 'A, B');
  assert.equal(model.objectsLine({ objects: names.map((n) => 'column:' + n.replace('Sales[', 'Sales/').replace(']', '')) }).split(' and ')[1], '7 more');
  const brief = 'Hide foreign keys. get_fix_prompt (system bpa) gives the exact steps for each object.';
  assert.equal(model.plainWhy([brief]), 'Hide foreign keys.');
  assert.equal(model.plainWhy(['Check what uses it first (get_lineage, unused_objects), then remove it with delete_objects.']), '');
  assert.equal(model.plainWhy(['One. Two. Three.']), 'One. Two.', 'a short why');
  for (const w of ['apply_fix (system bpa) makes it.', 'get_fix_prompt (system air) gives the steps for each object.']) assert.equal(model.plainWhy([w]), '');
  assert.match(src('plan-row.tsx'), /P\.plainWhy\(why\)/u);
  assert.doesNotMatch(src('plan-row.tsx'), /Finish these tasks first/u);
});

const fit = await import('../webview/src/diagramfit.mjs');
test('P14: the first frame holds readable text and never draws a card half cut; Fit never goes below the readable size', () => {
  // The Northstar shape from the UAT: a row of cards 190 wide on a 286 pitch with Sales on the left, in a 1090 px canvas.
  const xs = [-286, 0, 286, 572, 858, 1144, 1430];
  const cards = xs.map((x) => ({ x, w: 190 }));
  const bounds = { x: -286, y: 0, width: 1430 + 190 + 286, height: 400 };
  const v = fit.readableFirstView(bounds, 1090, 700, 0.55, 24, fit.FIRST_FIT_MIN_ZOOM, cards);
  assert.ok(v.zoom >= fit.FIRST_FIT_MIN_ZOOM, 'text is never smaller than readable');
  const screen = (c) => ({ left: c.x * v.zoom + v.x, right: (c.x + c.w) * v.zoom + v.x });
  for (const c of cards) {
    const s = screen(c);
    assert.ok(s.right <= 1090 || s.left >= 1090, `card at ${c.x} is whole or out of view, never half cut (${s.left.toFixed(0)} to ${s.right.toFixed(0)})`);
  }
  assert.ok(screen(cards[0]).left >= 0, 'the first card is in view');
  // A diagram that fits readably is kept as the plain fit.
  assert.equal(fit.readableFirstView({ x: 0, y: 0, width: 600, height: 300 }, 1090, 700, 1, 24, fit.FIRST_FIT_MIN_ZOOM, cards), null);
  // The Fit press frames through readableFit (UAT round 2, item 49): a whole fit while the names stay readable, else held at the readable floor.
  const diagram = src('diagram.tsx');
  assert.match(diagram, /showFitView=\{false\}/u, 'React Flow\'s own queued Fit is not drawn');
  assert.match(diagram, /aria-label="Fit View" onClick=\{\(\) => readableFit\(/u, 'Fit View never goes below the readable zoom, in every layout');
  assert.doesNotMatch(diagram, /aria-label="Fit View" onClick=\{\(\) => doFit\(/u, 'Fit View is not the plain full fit any more');
});

test('F3: zoom in and out keep a left-anchored frame on its left edge and a centred one on its middle', () => {
  const xs = [-286, 0, 286, 572, 858, 1144, 1430];
  const cards = xs.map((x) => ({ x, w: 190 }));
  const bounds = { x: -286, y: 0, width: 1430 + 190 + 286, height: 400 };
  const v = fit.readableFirstView(bounds, 1090, 700, 0.55, 24, fit.FIRST_FIT_MIN_ZOOM, cards);
  const left = (view) => bounds.x * view.zoom + view.x;
  for (const f of [1 / 1.2, 1.2, 1 / 1.2 / 1.2]) {
    const z = fit.zoomPinned(v, bounds, 1090, 700, f);
    assert.ok(Math.abs(left(z) - left(v)) < 0.01, `the left edge does not drift sideways (factor ${f.toFixed(2)})`);
    assert.ok(Math.abs(z.zoom - v.zoom * f) < 1e-9 || z.zoom === 0.15 || z.zoom === 2);
  }
  // A centred, fully visible fit zooms about its middle.
  const c = { x: 100, y: 80, zoom: 0.6 };
  const small = { x: 0, y: 0, width: 1000, height: 600 };
  const mid = (view) => ((small.x + small.width / 2) * view.zoom + view.x);
  const centred = { x: (1090 - 600) / 2, y: (700 - 360) / 2, zoom: 0.6 };
  const z2 = fit.zoomPinned(centred, small, 1090, 700, 1 / 1.2);
  assert.ok(Math.abs(mid(z2) - mid(centred)) < 0.01, 'a centred frame stays centred');
  assert.ok(c);
  // The zoom stays inside the canvas limits.
  const mY = (view) => (small.y + small.height / 2) * view.zoom + view.y;
  assert.ok(Math.abs(mY(z2) - mY(centred)) < 0.01, 'and stays on its vertical middle');
  assert.equal(fit.zoomPinned({ x: 0, y: 0, zoom: 0.16 }, small, 1090, 700, 0.5).zoom, 0.15);
  const diagram = src('diagram.tsx');
  assert.match(diagram, /zoomPinned\(/u, 'the zoom buttons use it');
});

test('F4: no Jev on the Pro page, Size advice offers Connect with no assistant, Data says More rows exist only when more do', () => {
  for (const f of ['propage-model.ts', 'propage.tsx']) {
    const lines = src(f).split('\n').filter((l) => !/^\s*(\/\/|\*|\/\*)/u.test(l));
    const shown = lines.filter((l) => /(['`]|>)[^'`<>]*\bJev\b[^'`<>]*(['`]|<)/u.test(l));
    assert.deepEqual(shown, [], `${f} shows the person no Jev`);
  }
  const adv = share.adviceFor('It takes 4.0 MB. Ask your assistant to shrink it, or check that the model needs all of it.', false);
  assert.equal(adv, 'It takes 4.0 MB. Check that the model needs all of it.');
  assert.match(share.adviceFor('It takes 4.0 MB. Ask your assistant to shrink it, or check that the model needs all of it.', true), /Ask your assistant to shrink it/u);
  assert.match(src('App.tsx'), /adviceFor\(/u);
  assert.match(src('App.tsx'), /data-testid="size-connect"/u, 'with no assistant the finding offers Connect');
  const data = src('datapreview.tsx');
  assert.match(data, /'previewTable', name, n \+ 1/u, 'ask for one row more than is shown');
  assert.match(data, /rows\.length > n \? \{ \.\.\.r, rows: r\.rows\.slice\(0, n\)[^}]*truncated: true/u);
  assert.doesNotMatch(data, /rows\.length >= topN/u, 'exactly topN rows is not More rows exist');
});

test('F8: a row offers Ask your assistant only when an assistant is connected', () => {
  assert.match(src('plan.tsx'), /kind === 'ask'\) return driver\.assistantConnected \? \{ kind, onPress/u);
});
