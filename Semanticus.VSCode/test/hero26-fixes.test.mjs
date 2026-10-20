// Hero 26 (test build 1.5.102): the page half of the fixes. The engine half is in Semanticus.Tests (QueryNotebookTests, TestReportRendererTests,
// CopilotTestTests, UatC63AuthoringTests, PlanStripTests).
//
//   N1  the assistant's own run never takes the ask box words, the chips or the answer of the row the person has open
//   N5  Validation reads its saved checks again when the open model arrives or the window comes back, and shows the last recorded run
//   N6  a row timed with a repeat time only is not "Not measured yet"
//   N7  a read of the pending changes does not pull the Studio off the Plan list
//   N8  words and layout: no engine brackets, "$" kept on a re-run, the bar number has room, one name for the driver, Free words, endpoint once
//   N10 a folded row that kept its number opens on it and does not ask again
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

// ── N8. A hand query names its number without the engine's brackets, and a run again keeps the "$" ────────────────────────────────────────
assert.equal(N.plainColumnName('[Total Cost 2025]'), 'Total Cost 2025');
assert.equal(N.plainColumnName("'Customer'[City]"), 'City');
assert.equal(N.plainColumnName('Sales[Amount]'), 'Amount');
assert.equal(N.plainColumnName('Plain name'), 'Plain name');
assert.equal(N.plainColumnName(null), undefined);
const two = { columns: [{ name: 'Year' }, { name: '[Total Cost 2025]' }], rows: [[2025, 29871303.48]], rowCount: 1 };
const fig = N.figureOf(two, null);
assert.equal(fig.name, 'Total Cost 2025', 'the figure is named without brackets');
assert.equal(N.noteFor({ id: 'x', askedAt: '2026-10-16T00:00:00Z', headline: '' }, fig, null, null).headline, 'Total Cost 2025: 29,871,303.48', 'the folded headline has no engine brackets');
assert.equal(N.keepSymbol('$3,028,989.86', '3,028,989.86'), '$3,028,989.86', 'a row that said "$" keeps it when run again');
assert.equal(N.keepSymbol('Total: $12.50', '13.75'), '$13.75', 'the symbol after a name stays');
assert.equal(N.keepSymbol('3,028,989.86', '3,100.00'), '3,100.00', 'a row with no symbol gets none');
assert.equal(N.keepSymbol('$10', '6 rows'), '6 rows', 'only a plain number takes a symbol');
assert.equal(N.keepSymbol('$10', '-5'), '-$5', 'a loss keeps its sign first');
// v-fix-hero26 r2: the "$" is carried only while the row's query is the same; an edited query (a count) is not dollars.
{ const said = { dax: 'EVALUATE ROW("Net Sales", [Net Sales])', headline: '$3,028,989.86' };
  assert.equal(N.saidFor(said, { dax: 'EVALUATE ROW("Net Sales", [Net Sales])' }), '$3,028,989.86', 'the same query keeps what it said');
  assert.equal(N.saidFor(said, { dax: 'EVALUATE ROW("Orders 2025", 13301)' }), null, 'an edited query says nothing old');
  assert.equal(N.keepSymbol(N.saidFor(said, { dax: 'EVALUATE ROW("Orders 2025", 13301)' }), '13,301'), '13,301', 'a count typed into a dollar row is never shown as dollars');
  assert.equal(N.saidFor(undefined, { dax: 'x' }), null, 'nothing said, nothing carried'); }
assert.equal(N.noteFor({ id: 'x', askedAt: '2026-10-16T00:00:00Z', headline: '$3,028,989.86' }, { big: '3,028,989.86', blank: false }, null, null), null, 'the same number run again says nothing new');

const css = src('query.css');
// Hero 27 P2 replaces the N8 two-line clamp: a folded figure is never broken inside a number (see the hero 27 section below).
assert.doesNotMatch(css, /\.qn-line-n \{[^}]*-webkit-line-clamp/, 'a folded headline is no longer cut to two lines');
const parts = src('visuals/visuals.tsx');
assert.match(parts, /const rh = compact \? 22 : 28, lw = LABEL_W\(w, rows\), vw = Math\.max\(76,/, 'the number at the end of a bar gets the room its text needs');

const explain = load(join(webview, 'src', 'explainpanel.tsx'));
assert.equal(explain.plainNote("The largest parts of this number, split by 'Customer'[City]. The parts add up to the total."), 'The largest parts of this number, split by City. The parts add up to the total.');
assert.equal(explain.plainNote(undefined), undefined);

const query = src('query.tsx');
assert.match(query, /Choose fields or write a query above\./, 'Free has no box, so its empty line does not send the person to one');
assert.match(query, /askOn \? 'Ask a question above\./, 'Pro keeps its line');

const bar = src('studio/driver-bar.tsx');
assert.match(bar, /const driverName = 'Your assistant';/, 'one name for the driver on the switch');
assert.match(bar, /const waiting = driver\.mode === 'claudeDrives' && !driver\.assistantWorking && !driver\.assistant;/, 'never back to waiting once the assistant has been seen');

const tests = src('tests.tsx');
assert.match(tests, /if \(authored && \(!ran \|\| authored === ran\)\) return null;/, 'the endpoint is said once');
assert.match(tests, /\{seen\.differs === 1 \? 'differs' : 'differ'\}/, '1 differs');
assert.doesNotMatch(tests, /Of checks that gave an answer/, 'Run history counts checks in the one unit the page counts');

// ── N1. The assistant's own run wears nothing of the open row ───────────────────────────────────────────────────────────────────────────
const nb = src('query-notebook.tsx');
assert.match(nb, /\(q\.assistantShown \? \{\n\s+id: DRAFT, askedAt: new Date\(\)\.toISOString\(\), kind: 'dax', question: null, dax: q\.currentQuery\(\)/, 'the draft holds the assistant\'s DAX and no ask-box words');
assert.match(query, /const chipsOn = askOn && !q\.assistantShown &&/, 'no chips over the assistant\'s query');
assert.match(query, /const a = q\.assistantShown \? null : q\.askState\.answer;/, 'no ask-box answer over the assistant\'s query');

// ── N10. A folded row that kept its number opens on it and does not ask again ─────────────────────────────────────────────────────────────
assert.match(nb, /if \(keepsNumber\) return;\n\s+runRow\(r\);/, 'open() shows the kept number; only a row with none (or only a row count) runs');
assert.match(nb, /canRun, runRow/, 'Run still asks again');
// Hero 27 P1 (fails on f2f3411e): a words row that opens on its kept number returns before runRow, and only a DAX row puts its query in the
// editor, so its DAX tab shows the session's last query (another row's: the 2024 ask under "net sales in 2025", or the assistant's June or
// Units DAX). The row must put its OWN DAX in the editor before it returns.
{
  const openFn = nb.slice(nb.indexOf('function open(r: Rec)'), nb.indexOf('function beginDraft'));
  const beforeReturn = openFn.split("if (r.headline && r.outcome === 'answered') return;")[0].replace(/if \(isDaxRow\(r\)\) \{[^}]*\}/g, '');
  assert.match(beforeReturn, /setQuery\(r\.dax/, 'a words row that opens on its kept number shows its own DAX in the DAX tab (hero27 P1)');
}

// ── N6. A repeat time alone is a timed row ────────────────────────────────────────────────────────────────────────────────────────────────
assert.match(query, /!measured && \(r\.coldMs != null \|\| r\.warmMs != null\) && <div className="sem-text-meta" data-testid="row-timed">/);
assert.match(query, /!measured && r\.coldMs == null && r\.warmMs == null && <div/);

// ── N5. Validation reads again ────────────────────────────────────────────────────────────────────────────────────────────────────────────
assert.match(tests, /const \{ id: sessionId \} = useSessionIdentity\(\);\n\s+useEffect\(\(\) => \{\n\s+suiteTries\.current = 0;\n\s+void loadSuite\(\); void loadSources\(\); void loadMappings\(\); void loadHistory\(\);\n\s+\}, \[loadSuite, loadSources, loadMappings, loadHistory, sessionId\]\);/, 'the page reads again when the open model arrives');
assert.match(tests, /suiteTries\.current\+\+ < 4/, 'a failed read is tried again');
assert.match(tests, /document\.addEventListener\('visibilitychange', again\)/, 'coming back to the window reads again');
assert.match(tests, /foldRunResults\(current, checks\.filter\(\(c\) => !!c\.defId\)\.map\(recordedOutcome\), newest\.when\)/, 'the newest recorded run fills an empty page');

// ── N7. A read of the pending changes stays where the screen is ──────────────────────────────────────────────────────────────────────────
const places = JSON.parse(src('studio/places.json'));
const changes = places.places.find((p) => p.id === 'plan/changes');
assert.ok(!changes.tools.includes('get_plan'), 'reading the pending changes does not move the Studio there');
assert.ok(places.staysWhereItIs.get_plan, 'it says why');
assert.equal(places.toolSays.get_plan, undefined);

// ── N2. A check that errored says one plain line ──────────────────────────────────────────────────────────────────────────────────────────
const TM = load(join(webview, 'src', 'tests-model.ts'));
assert.equal(TM.plainCheckError("The query could not be run. Technical Details: RootActivityId: 4f1c-77, Date (UTC): 2026-10-16"), 'The query could not be run.');
assert.equal(TM.plainCheckError('A <oii>Date</oii> column cannot be compared. RootActivityId: abc'), 'A Date column cannot be compared.');
assert.equal(TM.plainCheckError(''), 'The model could not answer this check.');
assert.equal(TM.plainCheckError(null), 'The model could not answer this check.');
assert.match(tests, /why = noNumber\n\s+\? <p className="va-line" data-testid="check-error-line">\{plainCheckError\(outcome\.message\)\}<\/p>/, 'the open check says the one line and not "answered nothing"');
assert.match(tests, /&& !blankStandoff && !noNumber\) \{/, 'no second "open this row for why" under it');

// ── fix2: N8 a re-run keeps "$" after the engine cleared the headline; N1 no fields under the assistant's number; N5 a late list in the same session ──
const noteAfterClear = N.noteFor({ id: 'x', askedAt: '2026-10-16T00:00:00Z', headline: null }, { big: '3,028,989.86', blank: false }, null, null, '$3,028,989.86');
assert.equal(noteAfterClear?.headline, '$3,028,989.86', 'the headline saved after a re-run keeps the "$" the row said before the engine cleared it');
assert.equal(N.noteFor({ id: 'x', askedAt: '2026-10-16T00:00:00Z', headline: null }, { big: '3,028,989.86', blank: false }, null, null)?.headline, '3,028,989.86', 'a row that never said "$" gets none');
const queryTsx = readFileSync(join(webview, 'src', 'query.tsx'), 'utf8');
assert.match(queryTsx, /const parts = q\.assistantShown \? \[\] : /, 'the person\'s fields are not read out under the assistant\'s number');
assert.match(queryTsx, /keepSymbol\(r\.headline \?\? saidFor\(q\.saidHeadline\.current\[r\.id\], r\)/, 'the open figure reads the headline the row said before it was cleared, only while its query is the same');
assert.match(tests, /onReconnect\(\(\) => \{ suiteTries\.current = 0; void loadSuite\(\); void loadHistory\(\); \}\)/, 'the saved checks are read again when the model arrives late in the same session');

// ── Hero 27 P2: a folded figure never breaks inside a number; the label shortens instead ────────────────────────────────────────────────────
{
  const nCss = (css.match(/\.qn-line-n \{[^}]*\}/) ?? [''])[0];
  assert.doesNotMatch(nCss, /overflow-wrap/, 'no break inside a figure');
  assert.match(css, /\.qn-line-val \{[^}]*white-space: nowrap/, 'the number is one piece');
  assert.match(css, /\.qn-line-lab \{[^}]*text-overflow: ellipsis/, 'the label gives way to the number');
  assert.deepEqual(N.splitFigure('Total Cost 2025: 29,871,303.48'), { label: 'Total Cost 2025:', value: '29,871,303.48' });
  assert.deepEqual(N.splitFigure('$39,137,964.25'), { label: '', value: '$39,137,964.25' });
  assert.deepEqual(N.splitFigure('6 rows'), { label: '', value: '6 rows' });
  assert.match(readFileSync(join(webview, 'src', 'query-notebook-parts.tsx'), 'utf8'), /qn-line-val/, 'the row line draws the number apart from its label');
}

// ── Hero 27 P3: "Asked by your assistant" follows the row's own origin, never the last ask ───────────────────────────────────────────────────
{
  const q = readFileSync(join(webview, 'src', 'query.tsx'), 'utf8');
  assert.match(q, /r\.origin === 'agent'/, 'the label reads the open row\'s origin');
  assert.doesNotMatch(q, /q\.byAssistant && q\.chipsHere/, 'the last ask no longer decides the label');
}

// ── Hero 27 P1 (more): the assistant's last query is not shown over a person's row that has not run ────────────────────────────────────────────
{
  const openFn = nb.slice(nb.indexOf('function open(r: Rec)'), nb.indexOf('function beginDraft'));
  assert.match(openFn, /setAssistantDax\(null\)/, 'opening another row ends the assistant\'s query on the DAX tab');
}

console.log('hero26-fixes: ok');
