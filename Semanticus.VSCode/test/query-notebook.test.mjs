// The Query notebook (1.6, Kane 16 Oct, option C): the rules that decide what the notebook shows, run for real, and the row drawn.
//
//   1. THE ORDER. Pinned rows first, then the open rows, each group newest first; a closed row is in the history only.
//   2. TIME ALL. The scope (Selection, Pinned, All) picks the rows, in the notebook's order, and leaves out a row with no query.
//   3. THE FIGURE. One value is the number; a split is how many rows (never added into a total); a blank says so and is never zero.
//   4. THE ROW. A pinned row is marked and offers Unpin; a part that could not apply is marked (K21); a timed row shows both times.
//   5. THE WORDS. The box never asks anyone to paste DAX; the page has no em dash, no "Map", no "one click".
// The engine half (pin, close, reopen, remove, the history file, the assistant's tool) is Semanticus.Tests/QueryNotebookTests.cs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

// The page's modules talk to the host through the bridge; there is no host here, so the bridge gets a quiet stand-in.
globalThis.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return undefined; }, setState() {} });
globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => {};
globalThis.removeEventListener ??= () => {};

const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const N = load(join(webview, 'src', 'query-notebook-model.ts'));

const at = (min) => new Date(Date.UTC(2026, 9, 16, 12, 0, 0) - min * 60000).toISOString();
const rec = (id, min, extra = {}) => ({ id, askedAt: at(min), kind: 'words', question: `question ${id}`, dax: `EVALUATE ROW("v", ${id.length})`, inNotebook: true, ...extra });

// ── 1. The order ───────────────────────────────────────────────────────────────────────────────────
const all = [rec('new', 1), rec('mid', 30, { pinned: true }), rec('old', 120), rec('closed', 5, { inNotebook: false }), rec('oldpin', 600, { pinned: true, inNotebook: false })];
assert.deepEqual(N.notebookRows(all).map((r) => r.id), ['mid', 'oldpin', 'new', 'old'], 'pinned first (a pinned row is in the notebook whatever else is set), then the open ones, each newest first');
assert.deepEqual(N.historyRows(all).map((r) => r.id), ['new', 'closed', 'mid', 'old', 'oldpin'], 'the history is every row, newest first, closed ones too');
assert.ok(!N.notebookRows(all).some((r) => r.id === 'closed'), 'a closed row leaves the notebook and stays in the history');

// ── 2. Time all ────────────────────────────────────────────────────────────────────────────────────
const shown = N.notebookRows(all);
assert.deepEqual(N.timeScope('all', shown, new Set()).map((r) => r.id), ['mid', 'oldpin', 'new', 'old'], 'All: every row of the notebook, in its order');
assert.deepEqual(N.timeScope('pinned', shown, new Set()).map((r) => r.id), ['mid', 'oldpin'], 'Pinned: the pinned rows');
assert.deepEqual(N.timeScope('selection', shown, new Set(['old', 'new', 'closed'])).map((r) => r.id), ['new', 'old'], 'Selection: the ticked rows that are in the notebook, in its order');
assert.deepEqual(N.timeScope('selection', shown, new Set()), [], 'nothing ticked, nothing to time');
const noQuery = [rec('a', 1, { dax: null, outcome: 'refused' }), rec('b', 2)];
assert.deepEqual(N.timeScope('all', noQuery, new Set()).map((r) => r.id), ['b'], 'a question that never got as far as a query has nothing to time');
assert.deepEqual(N.scopeCounts(shown, new Set(['new'])), { selection: 1, pinned: 2, all: 4 }, 'the menu says how many each choice would time');

// ── 3. The figure ──────────────────────────────────────────────────────────────────────────────────
const cfg = (rows, values) => ({ rows, cols: [], values, filters: [] });
const f = (name, kind = 'column') => ({ kind, ref: kind === 'measure' ? `[${name}]` : `'T'[${name}]`, name, table: 'T' });
const money = '\\$#,0';
const one = { columns: [{ name: 'Net Sales', format: money }], rows: [[2646905]], rowCount: 1, truncated: false, elapsedMs: 3 };
assert.deepEqual(N.figureOf(one, cfg([], [f('Net Sales', 'measure')])), { big: '$2,646,905', blank: false }, 'one value is the number, in the measure\'s own format');
const split = { columns: [{ name: 'Region' }, { name: 'Net Sales', format: money }], rows: [['Coast', 1], ['West', 2], ['North', 3]], rowCount: 3, truncated: false, elapsedMs: 3 };
assert.deepEqual(N.figureOf(split, cfg([f('Region')], [f('Net Sales', 'measure')])), { big: '3 rows', blank: false, count: true }, 'a split is never added into a number: a ratio added up is a wrong number shown as right');
assert.deepEqual(N.figureOf({ ...one, rows: [], rowCount: 0 }, cfg([], [f('Net Sales', 'measure')])), { big: 'No rows', blank: true }, 'a filter that matched nothing says so, never a zero (K21)');
assert.deepEqual(N.figureOf({ ...one, rows: [[null]] }, cfg([], [f('Net Sales', 'measure')])), { big: 'Blank', blank: true }, 'a blank number says blank');
assert.deepEqual(N.figureOf(one, null), { big: '$2,646,905', blank: false }, 'a query written by hand: its last column of one row');
assert.deepEqual(N.figureOf(null, null), { big: '', blank: false }, 'nothing run yet, nothing said');
assert.deepEqual(N.figureOf(one, null, true), { big: '', blank: false }, 'while it runs, no old number is shown');
const note = N.noteFor({ id: 'x', askedAt: at(1), headline: '3 rows', dropped: '', tookMs: 38 }, { big: '3 rows', blank: false }, '', 38.2);
assert.equal(note, null, 'nothing changed, nothing is sent again');
assert.deepEqual(N.noteFor({ id: 'x', askedAt: at(1) }, { big: '$1.2M', blank: false }, 'premium is not in the model.', 41), { headline: '$1.2M', dropped: 'premium is not in the model.', tookMs: 41 }, 'a new number, line and time are sent once');

// ── 4. The row ─────────────────────────────────────────────────────────────────────────────────────
assert.equal(N.timeWords({ id: 'x', askedAt: at(1), tookMs: 38 }).text, '38 ms', 'the time is the run time until Time all has run');
assert.deepEqual(N.timeWords({ id: 'x', askedAt: at(1), tookMs: 38, coldMs: 1640, warmMs: 210 }), { text: '1.6 s then 210 ms', slow: true, timed: true }, 'after Time all: the first run then the repeat runs, amber when the first run is slow');
assert.equal(N.timeWords({ id: 'x', askedAt: at(1), coldMs: 412, warmMs: 38 }).slow, false, 'a fast first run is not flagged');
assert.equal(N.ago(at(0), Date.UTC(2026, 9, 16, 12, 0, 0)), 'just now');
assert.equal(N.ago(at(9), Date.UTC(2026, 9, 16, 12, 0, 0)), '9 min ago');
assert.equal(N.ago(at(26 * 60), Date.UTC(2026, 9, 16, 12, 0, 0)), 'yesterday');
assert.equal(N.rowText({ id: 'x', askedAt: at(1), kind: 'dax', dax: 'EVALUATE\n   ROW("v", 1)' }), 'EVALUATE ROW("v", 1)', 'a query row is its DAX on one line');
assert.equal(N.rowText({ id: 'x', askedAt: at(1), question: 'sales in 2031', reading: 'Total Sales' }), 'sales in 2031', 'a question row is the words asked');
assert.equal(N.rowText({ id: 'x', askedAt: at(1), parts: { measures: ['Units'], groupBy: ["'Product'[Category]"] }, outcome: 'refused' }), 'Units by Category', 'a row with no words is named from its parts');

const NB = load(join(webview, 'src', 'query-notebook-parts.tsx'));
const nb = (over = {}) => ({ selected: new Set(), toggleSelected() {}, open() {}, act: async () => {}, ...over });
const html = (r, on = false, extra = {}) => renderToStaticMarkup(React.createElement(NB.RowLine, { r, on, nb: nb(extra) }));
const text = (h) => h.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/\s+/g, ' ').trim();
const pinned = html(rec('p', 1, { pinned: true, headline: '$2.65M', tookMs: 38 }));
assert.match(pinned, /data-pinned="yes"/, 'a pinned row says so');
assert.match(pinned, /aria-label="Unpin: question p"/, 'and offers Unpin, not Pin');
assert.match(pinned, /aria-label="Close: question p"/, 'every row can be closed: it stays in the history');
assert.match(text(pinned), /\$2\.65M.*38 ms/, 'a folded row shows its number and its time');
const plain = html(rec('q', 1, { headline: '4 rows' }));
assert.match(plain, /aria-label="Pin: question q"/, 'an unpinned row offers Pin');
const dropped = html(rec('d', 1, { headline: '4 rows', dropped: '"premium" is not in the model, so this is all customers.' }));
assert.match(dropped, /data-testid="row-dropped"/, 'a part that could not apply is marked on the folded row (K21)');
assert.match(dropped, /aria-label="Not applied: &quot;premium&quot; is not in the model/, 'the mark says what was not applied');
assert.doesNotMatch(plain, /data-testid="row-dropped"/, 'and no mark when nothing was dropped');
assert.match(html(rec('o', 1, { headline: '4 rows' }), true), /aria-expanded="true"/, 'the open row says it is open');
assert.doesNotMatch(text(html(rec('o', 1, { headline: '4 rows' }), true)), /4 rows/, 'the open row shows its number below, not on its line');
assert.match(html(rec('t', 1), false, { selected: new Set(['t']) }), /type="checkbox"[^>]*checked=""|checked=""[^>]*type="checkbox"/, 'a ticked row is ticked');
assert.match(html({ ...rec('x', 1, { kind: 'dax', dax: 'EVALUATE ROW("v", 1)' }) }), /qn-mono/, 'a query row reads in the code font');

// ── 5. The words ───────────────────────────────────────────────────────────────────────────────────
const page = src('query.tsx'), nbSrc = src('query-notebook.tsx'), ask = src('queriesask.tsx'), css = src('query.css'), model = src('query-notebook-model.ts');
assert.doesNotMatch(page + nbSrc + ask + model, /paste\s+DAX|or DAX|type DAX|write DAX in/i, 'the box never asks anyone to paste DAX; DAX is the row\'s own view');
const wordsOnPage = page + nbSrc + src('query-notebook-parts.tsx') + model;
assert.doesNotMatch(wordsOnPage + css, /—/, 'no em dash');
assert.doesNotMatch(wordsOnPage, /one click/i, 'no "one click"');
assert.doesNotMatch(wordsOnPage.replace(/\.map\(/g, ''), /\bMap\b(?!\()/, 'never "Map" in the copy');
assert.match(page, /data-testid="mode-dax"[\s\S]*data-testid="dax-run"/, 'DAX stays reachable: the row\'s DAX view, where it can be edited and run');
assert.match(nbSrc, /'benchmarkColdWarm', r\.dax, q\.runs, q\.clearOnRun && \(!q\.shared \|\| q\.confirmShared\), q\.confirmShared/, 'Time all uses the existing speed call (cold and warm)');
assert.match(nbSrc, /for \(let i = 0; i < todo\.length; i\+\+\) \{\n\s+if \(stop\.current\) break;[\s\S]*?await rpc<ColdWarmBenchmark>\('benchmarkColdWarm'/, 'Time all asks the server for ONE row at a time, and each is awaited before the next');
assert.match(nbSrc, /q\.setBusy\('measure'\)[\s\S]*q\.setBusy\(null\)/, 'while Time all runs the page is busy, so nothing else is sent');
console.log('query notebook: the order, Time all, the figure, the row and the words passed');
