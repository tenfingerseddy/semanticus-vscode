// The Ask your data PAGE (Kane, 26 September 2026: "redesign the Jev reading and build the page now").
//
// The page sits under Explore, after Data, and the whole feature is Pro. Reading a question from typed words
// stays off until the Jev reading passes its retest (Semanticus.Engine/Query/JevQueryReader.cs), so the page
// has to work fully on the PARTS path today: a person picks a number and the columns to split it by, and every
// change to a chip is sent back as parts, read by "you", with no Jev call at all.
//
// What each section proves:
//   1. THE PLACE. The tool id, its area, its strip label, its Pro feature and the help entry, read off the
//      real source files so a rename on one side fails here.
//   2. THE CALL. `runAsk` is the whole send, executed with a fake send: one method, the parts as given.
//   3. THE EDITS. Every chip edit is a pure function over the parts; each is executed.
//   4. THE STATES. The answer card is rendered for real with react-dom/server, once per outcome the engine
//      returns, and what a person reads is read back out of the markup.
//   5. SAVE AS A CHECK and OPEN IN QUERIES: the hand-offs, executed.
//   6. THE WIRING: read off App.tsx, route.ts and daxlab.tsx.
// There is no DOM here; the real clicks are driven by tools/uishot/askdata-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(root, '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
  },
  './connection': {
    useConnection: () => ({ conn: CONN.now, session: { sessionId: 'test' }, context: null }),
    ConnectBar: () => React.createElement('div', { 'data-testid': 'connect-bar' }, 'Choose test model'),
  },
  './grid': { ResultGrid: (p) => React.createElement('div', { 'data-testid': 'result-grid' }, `grid ${p.rows.length} rows`) },
  './daxvisual': {
    DaxVisual: () => React.createElement('div', { 'data-testid': 'chart' }, 'chart'),
    VizSwitcher: () => React.createElement('div', null, 'kinds'),
  },
  './daxeditor': { useDaxModelContext: () => ({ tables: [], measures: [], columns: [] }) },
  './tests-authoring': { AuthorDrawer: () => React.createElement('aside', null, 'New check') },
  './activity': { useClaudeReflection: () => {} },
  './surface-escape': { useSurfaceEscape: () => {} },
};
const CONN = { now: { connected: true } };
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ').trim();

// ── 1. The place ────────────────────────────────────────────────────────────────────────────────────
const route = src('route.ts');
assert.match(route, /\| 'askdata'/, 'askdata is a tool id');
assert.match(route, /data: 'explore', askdata: 'explore'/, 'Ask your data sits in Explore');
const app = src('App.tsx');
assert.match(app, /\{ id: 'data', label: 'Data' \}, \{ id: 'askdata', label: 'Ask your data' \},/,
  'Ask your data is the Explore segment right after Data');
const features = src('features.ts');
assert.match(features, /export type ProFeature = 'workflows' \| 'askData';/, 'the wire id matches the engine');
assert.match(features, /askdata: 'askData',/, 'the page is gated on its own feature');
assert.match(src('askbox.tsx'), /askdata: 'Ask your data',/, 'the ask box names the page as the strip does');
assert.match(src('help.tsx'), /"askdata": \{/, 'the page has a help entry, which the router copies its words from');
assert.match(readFileSync(join(repoRoot, 'Semanticus.Engine', 'Entitlement', 'ProFeature.cs'), 'utf8'),
  /ProFeature\.AskData => "askData",/, 'the engine reports the same wire id');
assert.match(src('propreview.tsx'), /'askdata': \{/, 'free sees a preview of the page, which calls nothing');

const M = load(join(webview, 'src', 'askdata-model.ts'));

// ── 2. The call ─────────────────────────────────────────────────────────────────────────────────────
const calls = [];
const send = async (method, ...params) => { calls.push({ method, params }); return { outcome: 'answered', readBy: 'you' }; };
const parts = {
  measures: ['Net Sales'], groupBy: ["'Store'[Store Region]"],
  filters: [{ column: "'Date'[Year]", op: 'is', values: ['2025'] }], top: null, topBy: null, lowest: false, sortBy: null, descending: false,
};
await M.runAsk(send, parts);
assert.equal(calls.length, 1);
assert.equal(calls[0].method, 'askData', 'the page calls the Studio half of ask_data');
assert.deepEqual(calls[0].params[0].measures, ['Net Sales']);
assert.equal(calls[0].params[0].question, undefined, 'a chip ask carries no words, so Jev is never asked');
calls.length = 0;
await M.runWords(send, 'total sales by region');
assert.deepEqual(calls[0].params[0], { question: 'total sales by region' }, 'words go as words, and only words');
const broke = await M.runAsk(async () => { throw new Error('The engine did not answer.'); }, parts);
assert.equal(broke.outcome, 'error');
assert.equal(broke.error, 'The engine did not answer.', 'a thrown call is an error answer a person reads');

// ── 3. The edits ────────────────────────────────────────────────────────────────────────────────────
assert.equal(M.columnName("'Store'[Store Region]"), 'Store Region');
assert.equal(M.columnName('Date[Year]'), 'Year');
assert.deepEqual(M.swapMeasure(parts, 0, 'Units').measures, ['Units']);
assert.deepEqual(M.removeMeasure(parts, 0).measures, ['Net Sales'], 'the last measure cannot be removed');
assert.deepEqual(M.removeMeasure(M.addMeasure(parts, 'Units'), 0).measures, ['Units']);
assert.deepEqual(M.swapGroup(parts, 0, "'Product'[Category]").groupBy, ["'Product'[Category]"]);
assert.deepEqual(M.removeGroup(parts, 0).groupBy, []);
assert.deepEqual(M.addGroup(parts, "'Product'[Category]").groupBy, ["'Store'[Store Region]", "'Product'[Category]"]);
assert.deepEqual(M.addGroup(parts, "'Store'[Store Region]").groupBy, ["'Store'[Store Region]"], 'no column twice');
assert.deepEqual(M.addFilter(parts, "'Store'[Store Region]", 'Coast, West').filters[1],
  { column: "'Store'[Store Region]", op: 'is', values: ['Coast', 'West'] }, 'typed words split on commas');
assert.deepEqual(M.removeFilter(parts, 0).filters, []);
assert.deepEqual(M.setFilterValues(parts, 0, '2024').filters[0].values, ['2024']);
const topped = M.setTop(parts, 10);
assert.equal(topped.top, 10); assert.equal(topped.topBy, 'Net Sales', 'a top ranks by the first number by default');
assert.equal(M.removeTop(topped).top, null);
assert.equal(M.setTop(topped, 5, 'Net Sales', true).lowest, true, 'a bottom N');
const sorted = M.setSort(parts, 'Net Sales', true);
assert.equal(sorted.sortBy, 'Net Sales'); assert.equal(sorted.descending, true);
assert.equal(M.removeSort(sorted).sortBy, null);
// Removing a measure the top ranks by moves the ranking to the measure that is left.
assert.equal(M.removeMeasure({ ...M.addMeasure(topped, 'Units') }, 0).topBy, 'Units');
// The chips, as a person hears them: each one is a named button.
const chips = M.chipsOf(topped);
assert.deepEqual(chips.map((c) => c.label), ['Net Sales', 'Store Region', 'Year = 2025', 'Top 10 by Net Sales']);
assert.ok(chips.every((c) => /^Change /.test(c.name)), 'every chip says what pressing it does');
assert.equal(M.filterWords({ column: "'Date'[Year]", op: 'from', values: ['2024'] }), 'Year from 2024');
assert.equal(M.filterWords({ column: "'Store'[Store Region]", op: 'is_not', values: ['Coast', 'West'] }), 'Store Region is not Coast or West');
// A column of days is offered as a filter that keeps a range (the engine filters one, and the assistant can send one);
// true/false columns are not, because the engine cannot filter them yet.
assert.deepEqual(M.filterableColumns([
  { table: 'Date', name: 'Date', dataType: 'DateTime' }, { table: 'Date', name: 'Year', dataType: 'Int64' },
  { table: 'Store', name: 'Store Region', dataType: 'String' }, { table: 'Store', name: 'Open', dataType: 'Boolean' },
  { table: 'Sales', name: 'Amount', dataType: 'Decimal' }, { table: 'Sales', name: 'Rate', dataType: 'Double' },
]).map((c) => c.name), ['Date', 'Year', 'Store Region', 'Amount', 'Rate']);
// A date chip offers only the four range words, the ones a column of days takes.
assert.equal(M.filterKind('DateTime'), 'date');
assert.deepEqual(M.filterOps('date').map(([op]) => op), ['after', 'from', 'before', 'up_to']);
assert.deepEqual(M.filterOps('number').map(([op]) => op), ['is', 'is_not', 'after', 'from', 'before', 'up_to']);
assert.deepEqual(M.filterOps('text').map(([op]) => op), ['is', 'is_not']);
// Moving from a date column to a text one (Back, then another column) never keeps a range word the text column
// refuses (Sol, 29 Sep): the operator falls back to the first the new kind takes.
assert.equal(M.opFor('text', 'from'), 'is');
assert.equal(M.opFor('date', 'is'), 'from');
assert.equal(M.opFor('number', 'after'), 'after');
assert.equal(M.opFor('date', 'up_to'), 'up_to');

// ── 4. The states ───────────────────────────────────────────────────────────────────────────────────
const answered = {
  outcome: 'answered', readBy: 'you', reading: 'Net Sales by Store Region, Year = 2025', parts,
  dax: "DEFINE\n    VAR __Filter1 =\n        TREATAS ( { 2025 }, 'Date'[Year] )\n    VAR __Result =\n        SUMMARIZECOLUMNS (\n            'Store'[Store Region],\n            __Filter1,\n            \"@Net Sales\", [Net Sales]\n        )\nEVALUATE\n    __Result\nORDER BY\n    'Store'[Store Region] ASC\n",
  checks: [{ name: 'reach', passed: true, line: 'Every column reaches Net Sales.' }, { name: 'rows', passed: true, line: '2 rows.' }],
  unsure: [], columns: [{ name: 'Store Region', type: 'String' }, { name: 'Net Sales', type: 'Decimal' }],
  rows: [['Coast', 1200.5], ['Inland', 800]], rowCount: 2, truncated: false, elapsedMs: 42,
};
assert.equal(M.outcomeOf(answered), 'answered');
const card = (answer, extra = {}) => readable(renderToStaticMarkup(React.createElement(load(join(webview, 'src', 'askdata.tsx')).AnswerBody,
  { answer, parts: M.partsAfter(parts, answer), connected: true, view: 'table', showDax: false, onView: () => {}, onShowDax: () => {}, ...extra })));
const A = card(answered);
assert.match(A, /grid 2 rows/, 'an answer shows its rows in the shared grid');
assert.match(A, /2 rows/);
assert.doesNotMatch(A, /DEFINE|SUMMARIZECOLUMNS/, 'no DAX words outside the DAX view');
assert.match(card(answered, { showDax: true }), /SUMMARIZECOLUMNS/, 'the DAX view shows the query the engine built');
assert.match(card(answered, { view: 'chart' }), /chart/, 'the chart is the shared visual');

// Two good readings (Kane, 28 Sep): the engine's line and one button that asks the other reading's parts. The page's
// own ask sends parts, so the switch never calls Jev; with no switch handed in, nothing is drawn.
const torn = { ...answered, readBy: 'jev', others: [{ line: 'Showing Net Sales. You may have meant Units.', instead: 'Units',
  reading: 'Units by Store Region, Year = 2025', parts: { ...parts, measures: ['Units'] } }] };
const T = card(torn, { onSwitch: () => {} });
assert.match(T, /Showing Net Sales\. You may have meant Units\./, 'the other reading is named in the engine\'s words');
assert.match(T, /Show Units instead/, 'with one button to switch to it');
assert.doesNotMatch(card(torn), /You may have meant/, 'a surface that cannot switch does not offer it');
assert.doesNotMatch(card(answered, { onSwitch: () => {} }), /You may have meant|instead/, 'a sure reading names no other');
// K16 (Kane, 8 Oct): an answer that assumed something says so in ONE line, right under the statement, and each other reading
// is a button on that same line: its own line is not repeated.
{
  const assumed = { ...answered, readBy: 'jev', statement: 'This shows Net Sales for each Year.', assumption: 'Metric Category is not filtered.',
    others: [{ line: 'Metric Category is not filtered.', instead: 'only Metric Category Sales', reading: 'Net Sales by Year, Metric Category = Sales',
      parts: { ...parts, filters: [{ column: "'Metric Category'[Metric Category]", op: 'is', values: ['Sales'] }] } }] };
  const K = card(assumed, { onSwitch: () => {} });
  assert.match(K, /This shows Net Sales for each Year\.\s*Metric Category is not filtered\.\s*Show only Metric Category Sales instead/, 'the statement, then the one assumption line with its button');
  assert.equal(K.match(/Metric Category is not filtered\./g).length, 1, 'the assumption is said once');
  assert.match(card(assumed), /Metric Category is not filtered\./, 'a surface that cannot switch still says what was assumed');
  assert.doesNotMatch(card(assumed), /instead/, 'without a switch there is no button');
}
{
  const src = readFileSync(join(webview, 'src', 'askdata.tsx'), 'utf8');
  assert.match(src, /onSwitch=\{\(p\) => void ask\(p\)\}/, 'the page switches by asking the other parts, which are read by you, not Jev');
}

// Stopped at the cap.
const capped = { ...answered, truncated: true, rowCount: 500, checks: [{ name: 'rows', passed: true, line: 'Stopped at 500 rows. There are more; ask for a top few to see the ones that matter.' }] };
assert.match(card(capped), /Stopped at 500 rows/, 'a capped answer says so');

// Refused: the failed checks' lines, verbatim, and the parts the page sent are KEPT when the answer has none.
const refused = { outcome: 'refused', parts: null, checks: [{ name: 'values', passed: false, line: "No value in Store Region matches 'Atlantis'." }], unsure: [], columns: [], rows: [] };
assert.equal(M.outcomeOf(refused), 'refused');
assert.deepEqual(M.partsAfter(parts, refused), parts, 'a refusal never wipes the chips');
const R = card(refused);
assert.match(R, /No value in Store Region matches 'Atlantis'\./, 'the refusal line, word for word');
assert.doesNotMatch(R, /grid/, 'nothing ran, so no grid');
// A refusal with no check line but an error (the agent policy gate) still says why.
assert.match(card({ outcome: 'refused', error: 'Approve the data read first.', checks: [], unsure: [] }), /Approve the data read first\./);

// Built with no data connection: the DAX and the connect bar, no grid.
const built = { ...answered, outcome: 'built', rows: [], columns: [], rowCount: 0, note: 'Connect a copy of this model that holds data. The words are matched against its values, and the query runs on it.' };
const B = card(built, { connected: false });
assert.match(B, /Choose test model/, 'built with no data shows the connect bar');
assert.match(B, /Connect a copy of this model that holds data/, 'and the engine note');
assert.doesNotMatch(B, /grid/, 'and no grid');

// Error: ONE plain notice, the server's own words behind Show details and never printed twice, and the sign-in
// line where it is a sign-in stop (verify-askdata-r5, r5-062: the raw text was shown twice, under a green line
// saying the query checked out).
const rawError = 'Column <oii>Region</oii> is missing. Technical Details: RootActivityId 1234';
const failedRun = {
  outcome: 'error', error: rawError, unsure: [],
  checks: [{ name: 'rows', passed: false, line: 'The query did not run on the model it was sent to.' }],
};
const E = card(failedRun);
assert.match(E, /The query did not run on the model it was sent to\./, 'one plain notice');
assert.doesNotMatch(E, /RootActivityId/, 'and the server raw text is not on screen until it is asked for');
assert.doesNotMatch(E, /checks out/, 'and nothing says the query checked out');
assert.match(E, /Show details/, 'the raw text is one press away');
const EO = card(failedRun, { showError: true });
assert.match(EO, /Column Region is missing\./, 'opened, it reads plainly, with markup tags dropped');
assert.equal((EO.match(/RootActivityId/g) || []).length, 1, 'said once, not twice');
// fix-askdata-final (verify-askdata-r6 L6): what the server said wraps instead of scrolling sideways, and (L5) the plain
// notice names no button, because the assistant door reads the same line and has none.
const EOraw = renderToStaticMarkup(React.createElement(load(join(webview, 'src', 'askdata.tsx')).AnswerBody,
  { answer: failedRun, parts: M.partsAfter(parts, failedRun), connected: true, view: 'table', showDax: false, onView: () => {}, onShowDax: () => {}, showError: true }));
assert.match(EOraw, /<pre class="m-0 whitespace-pre-wrap break-words/, 'what the server said wraps');
assert.doesNotMatch(EOraw, /overflow-x-auto/, 'and never scrolls sideways');
assert.equal((E.match(/Show details/g) || []).length, 1, 'Show details is said once, on the button, and never inside the notice');
// A failure the engine sent no check line for still says why, in the same words.
assert.match(card({ outcome: 'error', error: rawError, checks: [], unsure: [] }),
  /The query did not run on the model it was sent to\./, 'the page stands in with the same plain notice');
// The sign-in button, for real: it needs an onSignIn. Until fix-askdata-r6 this line passed on the raw error text
// happening to contain the same words, which proved nothing about the button.
assert.match(card({ outcome: 'error', error: 'Live sign-in expired. Sign in again.', checks: [], unsure: [] },
  { onSignIn: () => {} }), /Sign in again/);

// Needs you: the unsure lines, as the engine wrote them. A build where Jev does not read words answers this way.
const notYet = 'Reading a question from your words is not switched on in this version yet. Choose the numbers and the columns instead, and Semanticus builds the query.';
const N = card({ outcome: 'needs-you', note: notYet, unsure: [notYet], checks: [] });
assert.match(N, /not switched on in this version yet/, 'the words path says plainly it is off');
assert.equal((N.match(/not switched on/g) || []).length, 1, 'said once, not twice');
assert.match(card({ outcome: 'needs-you', unsure: ['Type a question, or choose a number to see.'], checks: [] }), /choose a number to see/);

// Words Jev read (query.summarize, on for Pro since 26 Sep): the reading is the chips, and the answer says Jev read it.
const J = card({ ...answered, readBy: 'jev' });
assert.match(J, /Jev read this\. Semanticus built and checked the query\./, 'a question Jev read says so');
assert.doesNotMatch(J, /Built from your choices/, 'and does not claim the person chose the parts');
assert.doesNotMatch(J, /not switched on/, 'and no not-yet line when Jev read it');
// Round 5 (29 Sep): every answer says what it shows, first; a stopped reading offers "Show it anyway"; "Jev read this" is
// only over an answer that ran.
{
  const statement = 'This shows Net Sales for each Store Region, where Year is 2025.';
  const S = card({ ...answered, readBy: 'jev', statement });
  assert.ok(S.indexOf(statement) >= 0 && S.indexOf(statement) < S.indexOf('Jev read this'), 'the statement is the first line of an answer');
  assert.match(S, /askdata-statement|This shows Net Sales/, 'the statement is drawn');
  assert.doesNotMatch(card({ ...answered }), /This shows/, 'no statement, none drawn');
  // A stopped reading: the stop line is said once (the statement is not repeated inside it), nothing ran, and the offer is one button.
  const stop = { outcome: 'needs-you', readBy: 'jev', statement, checks: [], unsure: [statement + ' The question asks how the numbers changed, and Semanticus does not work out a change yet. Nothing was run.'],
    others: [{ line: 'Run this reading as it is.', instead: 'it anyway', anyway: true, reading: 'Net Sales by Store Region, Year = 2025', parts }] };
  const X = card(stop, { onSwitch: () => {} });
  assert.equal((X.match(/This shows Net Sales for each Store Region/g) || []).length, 1, 'the statement is said once, not again inside the stop line');
  assert.match(X, /does not work out a change yet\. Nothing was run\./);
  assert.match(X, /Show it anyway/, 'the reading is one click away');
  assert.doesNotMatch(X, /Run this reading as it is\.(?!.*Show it anyway)|Show it anyway instead|instead/, 'the offer is worded as an offer, not as another reading');
  assert.doesNotMatch(X, /Jev read this/, 'a reading that did not run is not "Jev read this"');
  assert.doesNotMatch(card({ ...stop, outcome: 'built', unsure: [] }), /Jev read this/, 'nor is one that was only built');
}
// The Queries question box (the M11 join, 30 Sep): the same statement first, the stop line without it, and the same
// "Show it anyway", which asks the reading's parts (read by you, so no Jev call).
{
  const statement = 'This shows Net Sales for each Store Region, where Year is 2025 (1 January to 31 December 2025).';
  const { AskLines } = load(join(webview, 'src', 'queriesask.tsx'));
  const asked = [];
  const box = (answer) => readable(renderToStaticMarkup(React.createElement(AskLines, { ask: { answer, asking: false, stopped: false, words: '', announce: '', askParts: (p) => asked.push(p) }, current: true })));
  const Q = box({ ...answered, readBy: 'jev', statement });
  assert.ok(Q.indexOf(statement) >= 0 && Q.indexOf(statement) < Q.indexOf('Every column reaches'), 'the Queries box says the statement first');
  const stop = { outcome: 'needs-you', readBy: 'jev', statement, checks: [], unsure: [statement + ' The question asks how the numbers changed, and Semanticus does not work out a change yet. Nothing was run.'],
    others: [{ line: 'Run this reading as it is.', instead: 'it anyway', anyway: true, reading: 'Net Sales by Store Region, Year = 2025', parts }] };
  const X = box(stop);
  assert.equal((X.match(/This shows Net Sales for each Store Region/g) || []).length, 1, 'the Queries box says the statement once, not again inside the stop line');
  assert.match(X, /does not work out a change yet\. Nothing was run\./);
  assert.match(X, /Show it anyway/, 'the Queries box offers the stopped reading one click away');
  assert.doesNotMatch(X, /Show it anyway instead/);
  assert.match(src('queriesask.tsx'), /<OtherReadings answer=\{a\} onSwitch=\{ask\.askParts\} \/>/, 'the offer asks the parts, which are read by you, not Jev');
}
// K19 (Kane, 8 Oct): where Jev was not asked for want of permission the person can give, the note carries ONE Allow button, on
// both pages. It is drawn only when the engine says the person can allow it, and it never presses itself.
{
  const noPermission = "Jev was not asked: you have not given permission to send this model's details for advice.";
  const stopped = { outcome: 'needs-you', note: noPermission, unsure: [noPermission], checks: [], needsPermission: true };
  const pageHtml = renderToStaticMarkup(React.createElement(load(join(webview, 'src', 'askdata.tsx')).AnswerBody,
    { answer: stopped, parts, connected: true, view: 'table', showDax: false, onView() {}, onShowDax() {}, onAllowed() {} }));
  assert.equal((pageHtml.match(/>Allow</g) || []).length, 1, 'Ask your data: one Allow button on the note');
  const { AskLines } = load(join(webview, 'src', 'queriesask.tsx'));
  const queriesHtml = renderToStaticMarkup(React.createElement(AskLines, { ask: { answer: stopped, asking: false, stopped: false, words: '', announce: '', askParts() {}, askWords() {} }, current: true }));
  assert.equal((queriesHtml.match(/>Allow</g) || []).length, 1, 'Queries: one Allow button on the note');
  const plain = { ...stopped, needsPermission: false };
  assert.doesNotMatch(renderToStaticMarkup(React.createElement(AskLines, { ask: { answer: plain, asking: false, stopped: false, words: '', announce: '', askParts() {}, askWords() {} }, current: true })), />Allow</, 'no button where the person cannot allow it');
  assert.match(src('consent.tsx'), /rpc<ConsentInfoW>\('setConsent', info\.fieldsInForce, info\.schemaVersionInForce, 'human', info\.appliesTo/, 'the button grants as the person, for the list in force');
}
// The switch off for this model: the engine's line, as written.
const off = 'Jev reading your words is switched off for this model. Switch it on under What Pro decides, on the Pro page, or choose the numbers and the columns instead.';
assert.match(card({ outcome: 'needs-you', note: off, unsure: [off], checks: [] }), /switched off for this model/);

// ── 5. Save as a check, and Open in Queries ─────────────────────────────────────────────────────────
const measuresInModel = [{ name: 'Net Sales', table: 'Sales' }];
const draft = M.checkDraftOf(answered, 0, 0, measuresInModel);
assert.equal(draft.targetRef, 'measure:Sales/Net Sales');
const p = JSON.parse(draft.paramsJson);
assert.equal(p.expectedValue, '1200.5', 'the expected value is the value shown');
// A trusted-answer check asks a single value, never a DEFINE query (measured live on Northstar, 26 Sep: the
// engine answers NotVerifiable for a DEFINE). So the engine's filter lines are inlined into one CALCULATE.
assert.equal(p.expressionDax,
  "CALCULATE ( [Net Sales], TREATAS ( { 2025 }, 'Date'[Year] ), TREATAS ( { \"Coast\" }, 'Store'[Store Region] ) )",
  'the question\'s own filters and the row it came from, as one value');
assert.doesNotMatch(p.expressionDax, /DEFINE|EVALUATE/, 'a single value, not a query');
assert.equal(M.checkDraftOf({ ...answered, columns: [{ name: 'Day', type: 'DateTime' }, answered.columns[1]], rows: [['2025-01-01', 3]] }, 0, 0, measuresInModel), null,
  'a row split by a date cannot be pinned yet');
assert.equal(M.checkDraftOf({ ...answered, rows: [[null, 3]] }, 0, 0, measuresInModel), null, 'nor a blank row');
// verify-askdata-r2 R6: a finder that matches nothing says so, in the person's words, so a typo reads differently
// from an empty list; every finder in the chip menu draws its list through this one line.
assert.equal(M.emptyFinderLine('column', ' zzqx '), 'No column matches "zzqx". Check the spelling, or clear the words to see them all.');
assert.equal(M.emptyFinderLine('number', 'marg'), 'No number matches "marg". Check the spelling, or clear the words to see them all.');
assert.equal(M.emptyFinderLine('number', ''), 'This model has no measures to show.');
assert.equal(M.emptyFinderLine('column', ''), 'There are no columns to show here.');
{
  const page = src('askdata.tsx');
  assert.equal((page.match(/\{finder\('Find a (number|column)'\)\}/g) || []).length, 5, 'five finders in the chip menu');
  assert.equal((page.match(/\{found\(/g) || []).length, 5, 'and each draws its list through found(), which says when nothing matches');
}
// verify-askdata-r2 R2 and verify-askdata-r3 R2: the title says everything the check asks, read off the check itself
// (checkTitle): the question's filter and the row, then the number. Only a check that asks nothing narrower than the
// measure names a total.
assert.equal(draft.title, 'Net Sales for Year = 2025, Store Region = Coast is 1200.5', 'a one-row check names its row and the question\'s filter');
{
  const totalParts = { ...parts, groupBy: [] };
  const total = M.checkDraftOf({ ...answered, parts: totalParts, columns: [answered.columns[1]], rows: [[2000.5]] }, 0, 0, measuresInModel);
  assert.equal(total.title, 'Net Sales for Year = 2025 is 2000.5', 'a check with no split names its filter, and claims no plain total');
  assert.equal(JSON.parse(total.paramsJson).expressionDax, "CALCULATE ( [Net Sales], TREATAS ( { 2025 }, 'Date'[Year] ) )");
  const bare = M.checkDraftOf({ ...answered, parts: { ...totalParts, filters: [] }, dax: 'EVALUATE ROW ( "@Net Sales", [Net Sales] )', columns: [answered.columns[1]], rows: [[9]] }, 0, 0, measuresInModel);
  assert.equal(bare.title, 'Net Sales in total is 9');
}
// verify-askdata-r3 R2: the title is worked out from the check whatever field changes, so no field can bring a false
// total back. The drawer's fields: the number typed, an Only where filter, the row edited in the formula, a written
// filter, and a formula this cannot read.
{
  const x = "CALCULATE ( [Total Sales], TREATAS ( { 2025 }, 'Date'[Year] ), TREATAS ( { \"Coast\" }, 'Customer'[Region] ) )";
  const ref = 'measure:Sales/Total Sales';
  assert.equal(M.checkTitle(ref, '1356', { expressionDax: x }), 'Total Sales for Year = 2025, Region = Coast is 1356', 'the number typed');
  assert.equal(M.checkTitle(ref, '1356.1', { expressionDax: x, filterColumn: 'Channel', filterValue: 'Online' }),
    'Total Sales for Year = 2025, Region = Coast, Channel = Online is 1356.1', 'Only where added');
  assert.equal(M.checkTitle(ref, '1356.1', { expressionDax: x.replace('"Coast"', '"North"') }),
    'Total Sales for Year = 2025, Region = North is 1356.1', 'the row edited in the formula');
  assert.equal(M.checkTitle(ref, '7', { filterDax: `'Product'[Category] = "Bikes", 'Date'[Year] = 2026` }),
    'Total Sales for Category = Bikes, Year = 2026 is 7', 'a written filter, read condition by condition');
  assert.equal(M.checkTitle(ref, '7', { filterDax: `KEEPFILTERS ( 'Product'[Color] )` }), 'Total Sales for a written filter is 7');
  assert.equal(M.checkTitle(ref, '7', { expressionDax: '[Total Sales] * 2' }), 'Total Sales with an edited formula is 7', 'a formula it cannot read');
  assert.equal(M.checkTitle(ref, '7', { expressionDax: "CALCULATE ( [Other], TREATAS ( { 1 }, 'T'[C] ) )" }), 'Total Sales with an edited formula is 7',
    'a formula over another measure is not this measure\'s check');
  assert.equal(M.checkTitle(ref, '1', { expressionDax: "CALCULATE ( [Total Sales], FILTER ( KEEPFILTERS ( VALUES ( 'Store'[Channel] ) ), NOT ( 'Store'[Channel] IN { \"Online\", \"Phone\" } ) ), FILTER ( KEEPFILTERS ( VALUES ( 'Date'[Year] ) ), 'Date'[Year] >= 2024 ) )" }),
    'Total Sales for Channel is not Online or Phone, Year from 2024 is 1', 'every shape the query template writes');
  // Sol on round 4: the engine asks the written filter INSTEAD of Only where when both are set (LocalEngine.TestSuite.cs
  // measure-value filter), so the title names the one it asks and not the one it drops.
  assert.equal(M.checkTitle(ref, '100', { filterColumn: "'Store'[Region]", filterValue: 'Coast', filterDax: `'Store'[Region] = "North"` }),
    'Total Sales for Region = North is 100', 'the written filter wins over Only where, as the engine asks it');
  assert.equal(M.checkTitle(ref, '', {}), 'Total Sales in total', 'no number yet');
  assert.equal(M.checkTitle(ref, '9', { expressionDax: '[Total Sales]' }), 'Total Sales in total is 9', 'the bare measure is its total');
  for (const t of [M.checkTitle(ref, '1356', { expressionDax: x }), M.checkTitle(ref, '1', { expressionDax: x, filterColumn: 'Channel', filterValue: 'Online' })])
    assert.doesNotMatch(t, /\btotals?\b/, `a one-row check never claims a total ("${t}")`);
}

// The question's filters are read back out of the engine's own query text (answerFilterLines), so the two sides must
// agree on its layout. The engine pins that layout byte for byte in AskDataTests.The_canonical_shape_is_exactly_the_template;
// this reads that expected text, so a change to the template fails here too, not only in C#, instead of Explain this
// number and a saved check silently losing the filters (verify-queries-redesign round 2, lower item).
{
  const cs = readFileSync(join(repoRoot, 'Semanticus.Tests', 'AskDataTests.cs'), 'utf8').replace(/\r\n/g, '\n');
  const body = /The_canonical_shape_is_exactly_the_template\(\)[\s\S]*?Assert\.Equal\(\n([\s\S]*?)\n\s*QueryTemplate\.Build\(s\)\);/.exec(cs)?.[1] ?? '';
  const canonical = [...body.matchAll(/^\s*"((?:[^"\\]|\\.)*)"/gm)].map((m) => JSON.parse(`"${m[1]}"`)).join('');
  assert.match(canonical, /^DEFINE\n[\s\S]*VAR __Filter1 =[\s\S]*\nEVALUATE\n/, 'the engine test\'s canonical query was read');
  const year = ["TREATAS ( { 2025 }, 'Date'[Year] )"];
  assert.deepEqual(M.answerFilterLines(canonical), year, 'the filter is read out of the query exactly as the engine writes it');
  assert.deepEqual(M.answerFilterLines(canonical.replace(/\n/g, '\r\n')), year, 'and out of the same query with Windows line ends');
}

// The New check drawer, rendered for real: handed a check from this page, it must not say the formula was
// changed in DAX Lab (coordinator, 26 Sep). The DAX Lab wording stays for a DAX Lab hand-off.
{
  const realAuthoring = (() => { const saved = STUBS['./tests-authoring']; delete STUBS['./tests-authoring'];
    const m = load(join(webview, 'src', 'tests-authoring.tsx')); STUBS['./tests-authoring'] = saved; return m; })();
  const drawer = (from) => readable(renderToStaticMarkup(React.createElement(realAuthoring.AuthorDrawer,
    { shape: 'measureValue', editing: draft, handedFrom: from, onClose: () => {}, onSaved: () => {} })));
  const fromPage = drawer('askdata');
  assert.doesNotMatch(fromPage, /DAX Lab/, 'a check from Ask your data does not mention DAX Lab');
  assert.match(fromPage, /Ask your data/, 'it says where the number came from');
  assert.match(drawer(undefined), /You changed the formula in DAX Lab/, 'the DAX Lab hand-off keeps its words');
}
assert.match(src('askdata.tsx'), /handedFrom="askdata"/, 'the page tells the drawer where the check came from');

// Who asked, in the history: the assistant's asks carry the marker.
assert.equal(M.whoAsked({ origin: 'agent' }), 'Your assistant');
assert.equal(M.whoAsked({ origin: 'human' }), 'You');

// ── 6. The wiring ───────────────────────────────────────────────────────────────────────────────────
assert.match(route, /\| 'daxText'/, 'Open in Queries hands the DAX over as a one-use seed');
assert.match(app, /tab === 'askdata' \? \(/, 'App mounts the page');
assert.match(app, /seed\?\.kind === 'daxText'/, 'App hands the DAX seed to Queries');
const lab = src('daxlab.tsx');
assert.match(lab, /navTarget\?\.dax/, 'Queries adopts a handed DAX text');
assert.match(lab, /onNavConsumed\?\.\(navTarget\.nonce\)/, 'and acknowledges it once adopted');
const page = src('askdata.tsx');
assert.match(page, /rpc\('cancelDax'\)/, 'Stop reuses the query cancel');
assert.match(page, /aria-live="polite"/, 'results are announced');
assert.match(page, /useClaudeReflection\('ask_data', \(\) => loadHistory\(\)\)/, 'an assistant ask, pushed live as an activity, refreshes the list');
assert.match(page, /usePersistedState<DataAsk>\('askdata\.parts\.' \+ persistKey/, 'the question survives leaving the page and coming back (Studio unmounts a tab)');
assert.doesNotMatch(page + src('askdata-model.ts'), /—/, 'no em dashes in the page');

// ── 7. The verifier's defects on the combined build (26 Sep) ────────────────────────────────────────────
// A refused question's history row is named from the parts as asked, so it reads as a question and Run again has
// something to send (defect 1; the engine keeps the parts, AskDataVerifyTests).
assert.equal(M.questionLine({ measures: ['Net Sales'], groupBy: ["'Store'[Region]"], filters: [{ column: "'Store'[Region]", op: 'is', values: ['Atlantis'] }] }),
  'Net Sales by Region, Region = Atlantis');
assert.equal(M.questionLine({ measures: ['Units', 'Net Sales'], groupBy: ["'Product'[Category]"], top: 3, topBy: 'Units' }), 'Units and Net Sales by Category, Top 3 by Units');
assert.equal(M.questionLine({ measures: [] }), '');
assert.equal(M.questionLine(null), '');
// Defect 1's page half: a history row refused before its parts were settled (a value not in the data, no model with
// data, the assistant's ask held for approval) has no reading and no words, so the page names it from its parts
// instead of "A question that was not run". The row's name is also its Run again button's name.
{
  const P = load(join(webview, 'src', 'askdata.tsx'));
  const refused = { id: 'q1', askedAt: '2026-09-26T10:00:00Z', outcome: 'refused', reading: null, question: null,
    parts: { measures: ['Net Sales'], groupBy: ["'Store'[Store Region]"], filters: [{ column: "'Store'[Store Region]", op: 'is', values: ['Atlantis'] }] } };
  assert.equal(P.rowName(refused), 'Net Sales by Store Region, Store Region = Atlantis', 'a refused row is named from its parts');
  const held = { id: 'q2', askedAt: '2026-09-26T10:01:00Z', outcome: 'needs-you', origin: 'agent', parts: { measures: ['Units'], groupBy: ["'Product'[Category]"] } };
  assert.equal(P.rowName(held), 'Units by Category', 'the assistant\'s ask held for approval is named from its parts');
  assert.equal(P.rowName({ ...refused, reading: 'Net Sales by Store Region' }), 'Net Sales by Store Region', 'the engine\'s reading comes first');
  assert.equal(P.rowName({ ...refused, question: 'sales in atlantis' }), 'sales in atlantis', 'then the words asked');
  assert.equal(P.rowName({ ...refused, parts: null }), 'A question that was not run', 'and only a row with nothing to name it by says so');
  assert.match(page, /\{rowName\(r\)\}/, 'the history draws each row\'s name with rowName');
  assert.match(page, /aria-label=\{`Run again: \$\{rowName\(r\)\}`\}/, 'and its Run again button is named the same');
}
// The pickers leave out a calculation group's columns, which the engine refuses every time (defect 6).
assert.deepEqual(M.askableColumns([{ table: 'Sales', name: 'Amount' }, { table: 'Time Intelligence', name: 'Time Calculation' }], ['Time Intelligence']),
  [{ table: 'Sales', name: 'Amount' }]);
// Every answer that waits on a model with data offers the way to connect, not only "built" (defect 5). The line is
// the engine's own, word for word.
const engineQuery = readFileSync(join(repoRoot, 'Semanticus.Engine', 'LocalEngine.Query.cs'), 'utf8');
assert.ok(engineQuery.includes(`NoDataNote = "${M.NO_DATA_LINE}"`), 'the page reads the engine\'s no-data line word for word');
assert.ok(M.needsData({ outcome: 'built' }));
assert.ok(M.needsData({ outcome: 'refused', checks: [{ name: 'parts', passed: false, line: `Say which values of Year to filter by. ${M.NO_DATA_LINE}` }] }));
assert.ok(M.needsData({ outcome: 'needs-you', note: M.NO_DATA_LINE, unsure: [M.NO_DATA_LINE] }));
assert.ok(!M.needsData({ outcome: 'refused', checks: [{ name: 'values', passed: false, line: "No value in Store Region matches 'Atlantis'." }] }));
assert.ok(!M.needsData({ outcome: 'answered', rows: [[1]] }));
// A typed question Jev read replaces the chips with the reading; one it did not read keeps the person's chips.
assert.match(page, /settle\(a\.readBy === 'jev' \? cleanParts\(null\) : parts, a\)/, 'the chips show how the words were read');
assert.match(page, /useConsentCard\(fromWords/, 'words Jev could not read for want of permission raise the consent card');
const engineQueryNotes = readFileSync(join(repoRoot, 'Semanticus.Engine', 'LocalEngine.Query.cs'), 'utf8');
assert.ok(engineQueryNotes.includes(`SwitchedOffNote = "${off}"`), 'the switched-off line above is the engine\'s, word for word');
// verify-askdata-r2 R5: the line and the page help sent the person to "the suggestion settings", which the Studio does
// not have. Both now name the real place, by the names the Studio shows: the panel's title and the menu it opens from.
{
  const panel = /const TITLE = '([^']+)'/.exec(src('decisions-settings.tsx'))?.[1];
  assert.equal(panel, 'What Pro decides', 'the switch lives in the section "What Pro decides"');
  assert.match(src('propage.tsx'), /<DecisionSettingsSection \/>/, 'the section is on the Pro page (r1 min4: it left the Your assistant menu)');
  const help = /Reading typed questions can be switched off[^"]*/.exec(src('help.tsx'))?.[0] ?? '';
  for (const [where, line] of [['the engine line', off], ['the page help', help]]) {
    assert.ok(line.includes(`under ${panel}, on the Pro page`), `${where} names the panel and the page: ${line}`);
    assert.doesNotMatch(line, /suggestion settings/i, `${where} names no place the Studio lacks`);
  }
}
// A model change puts away the last model's answer and list (defect 4; driven in tools/uishot/verify-askdata-drive.mjs).
assert.match(page, /\}, \[persistKey, loadHistory\]\);/, 'the page resets when the open model changes');
assert.match(src('hooks.ts'), /if \(held\.key !== key\)/, 'usePersistedState reads the new key\'s value when its key changes');

// ── 8. The minimalism pass (Kane, 8 Oct): the words box first, passed checks folded, repeats merged ─────────────────
{
  // Words first: the form comes before the chips, its Ask is the primary button, and the title line, the empty-state
  // line and Refresh are gone. The empty line keeps its test id on the quiet "or build it yourself" label.
  const at = (needle) => page.indexOf(needle);
  assert.ok(at('aria-label="Ask in your own words"') > 0 && at('aria-label="Ask in your own words"') < at('aria-label="The question, as parts"'), 'the words box comes before the chips');
  assert.match(page, /placeholder="Ask about your numbers, like net sales by region for 2025"/, 'the placeholder says what to type');
  assert.match(page, /className="sem-btn sem-btn-primary" disabled=\{busy \|\| !words\.trim\(\)\}>Ask</, 'Ask is the one jade button');
  assert.match(page, /or build it yourself:/, 'the chips are the second way');
  assert.doesNotMatch(page, />Refresh</, 'the list reloads on its own, so there is no Refresh');
  assert.doesNotMatch(page, /Choose a number and how to split it|Nothing runs until a number is chosen/, 'no page header line and no empty-state paragraph');
  assert.match(page, /<h1 className="sr-only">Ask your data<\/h1>/, 'the page keeps a heading for a screen reader');
  assert.match(page, /className=\{`group sem-stack/, 'a history row is one hover target');
  assert.match(page, /opacity-0 group-hover:opacity-100 focus:opacity-100/, 'Run again shows on hover or focus, and a keyboard reaches it');

  // The passed checks fold into one line. A failed check never folds.
  const C = card(answered);
  assert.match(C, /✓ Checked · 2 rows · 42 ms/, 'one line says checked, the rows and the time');
  assert.doesNotMatch(C, /Every column reaches Net Sales/, 'the passed lines are not on screen until asked for');
  assert.match(C, /Show checks/, 'one button brings them back');
  assert.doesNotMatch(C, /Built from your choices/, 'and nothing claims who built it');
  assert.match(card(capped), /Stopped at 500 rows/, 'a capped answer keeps its own words on the line');
  const bad = { ...answered, outcome: 'refused', checks: [{ name: 'reach', passed: false, line: 'Net Sales does not reach Store Region.' }, { name: 'rows', passed: true, line: '0 rows.' }] };
  assert.match(card(bad), /Net Sales does not reach Store Region\./, 'a failed check prints in full');
  // Few rows need no filter box and no frame.
  const gridCall = /<ResultGrid columns=\{rs\.columns[\s\S]{0,400}filterable=\{rs\.rows\.length > 20\} framed=\{false\}/.exec(page);
  assert.ok(gridCall, 'the grid has a filter only above 20 rows and draws no frame inside the card');

  // Data and the connect line (same pass): connected, the line is not drawn (the footer's Tests slot names the model and opens the
  // same picker); offline it is one short sentence and the same button. The grid carries the one count line.
  const conn = src('connection.tsx'), data = src('datapreview.tsx');
  assert.match(conn, /if \(conn\?\.connected\) return null;/, 'a connected ConnectBar draws nothing');
  assert.doesNotMatch(conn, /Tests and queries use|Local files remain editable|no running model is selected/, 'the long offline paragraph is gone');
  assert.match(conn, />Choose test model</, 'the button keeps its name');
  assert.match(data, /hint="Connect a model that holds data to see rows\."/, 'Data says one short line offline');
  assert.doesNotMatch(data, /Preview ·|Connect, then pick a table|\(truncated\)|cols ·/, 'the title is the table name and the counts are said once');
  assert.match(data, /truncated=\{res\.truncated\}/, 'the grid says when more rows exist');

  // Repeats merge: the newest ask stays, with how many times.
  const P = load(join(webview, 'src', 'askdata.tsx'));
  const ask = (id, who, when) => ({ id, askedAt: when, origin: who, reading: 'Total Sales by Category', outcome: 'answered', rowCount: 3 });
  const merged = P.mergeRepeats([ask('a', 'agent', '2026-10-08T05:00:00Z'), ask('b', 'human', '2026-10-08T04:00:00Z'), { ...ask('c', 'human', '2026-10-08T03:00:00Z'), reading: 'Units by Region' }]);
  assert.deepEqual(merged.map((m) => [m.r.id, m.times]), [['a', 2], ['c', 1]], 'two asks of one reading are one row, the newest, counted twice');
  const unnamed = P.mergeRepeats([{ id: 'x', askedAt: 'z', outcome: 'refused' }, { id: 'y', askedAt: 'z', outcome: 'refused' }]);
  assert.equal(unnamed.length, 2, 'two refused asks with no reading are not the same question');

  // K17 (Kane, 8 and 9 Oct): the history has no scroll bar of its own. The latest 8 questions show and "Show all" opens the
  // rest in place, so the page scrolls.
  assert.equal(P.HISTORY_SHOWN, 8, 'the list shows the latest 8 questions');
  const history = page.slice(page.indexOf('aria-label="Asked before"'), page.indexOf('aria-label="Your question"'));
  assert.doesNotMatch(history, /overflow-auto|overflow-y|inset-0|askdata-history-frame/, 'the history card holds no scroll area of its own');
  assert.match(history, /mergedHistory\.length > HISTORY_SHOWN/, 'the link shows only when there is more than the 8');
  assert.match(history, /showAllHistory \? 'Show fewer' : 'Show all'/, 'Show all opens the list in place, and Show fewer closes it');
  assert.match(page, /showAllHistory \? mergedHistory : mergedHistory\.slice\(0, HISTORY_SHOWN\)/, 'closed, the list is cut to the latest 8');
}

console.log('ask-data-page: all checks passed');
