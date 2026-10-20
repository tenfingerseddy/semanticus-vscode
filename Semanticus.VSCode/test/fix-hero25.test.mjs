// Hero 25 (the 1.5.101 test build): the defects H1 to H11, each pinned by a test that failed before its fix.
//   H1  an assistant's query is never drawn into, or saved as the figure of, a row the person owns; Run runs the DAX the DAX view shows.
//   H2  Time all counts a row as timed with any measured run, and says why when it cannot time one.
//   H3  turning Pro on writes nothing to the open model: the press that asked for it is worked out, then waits for approval.
//   H4  a hand query with several columns names the column it shows, and shows no chips of another question.
//   H5  the Copilot student is told to quote table names.
//   H6/H7/H8/H10/H11  stale text and wording, Validation, Explain, Speed, the Free split visual.
//   H9  the host tells the engine who has the wheel once, not once per key.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
globalThis.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return undefined; }, setState() {} });
globalThis.window ??= globalThis;
globalThis.addEventListener ??= () => {};
globalThis.removeEventListener ??= () => {};
const text = (f) => readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const web = (f) => text(join(webview, 'src', f));

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
const src = (f) => join(webview, 'src', f);
const N = load(src('query-notebook-model.ts'));
const V = load(src('validation-model.ts'));
const QM = load(src('query-model.ts'));
const AM = load(src('askdata-model.ts'));

let failures = 0;
const check = (name, fn) => { try { fn(); console.log('ok   ' + name); } catch (e) { failures++; console.error('FAIL ' + name + '\n' + (e && e.stack || e)); } };
const row = (id, extra = {}) => ({ id, askedAt: '2026-10-16T12:00:00Z', kind: 'words', question: `q ${id}`, dax: `EVALUATE ROW("v", 1)`, inNotebook: true, ...extra });

// ── H1 ──────────────────────────────────────────────────────────────────────────────────────────
check('H1: rows an assistant made are never a person\'s row figure', () => {
  const mine = row('mine', { dax: 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[Year] = 2025))' });
  assert.equal(N.rowOwnsResult(mine, 'EVALUATE ROW("Net Sales", [Net Sales])', false), false, 'rows of another query do not belong to the row');
  assert.equal(N.rowOwnsResult(mine, 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[Year] = 2025))', true), false, 'the assistant\'s rows never do, even with the same text');
  assert.equal(N.rowOwnsResult(mine, 'EVALUATE ROW("Net Sales", CALCULATE([Net Sales], \'Date\'[Year] = 2025))', false), true, 'its own run does');
  assert.equal(N.rowOwnsResult(mine, '', false), true, 'a result that names no query is not blocked (the happy path stays)');
  assert.equal(N.rowOwnsResult(row('nodax', { dax: null }), 'EVALUATE ROW("v", 9)', false), true, 'a row with no DAX yet is not blocked');
});
check('H1: the assistant\'s run is found as its own row', () => {
  const all = [row('p', { dax: 'EVALUATE ROW("v", 1)' }), row('a', { kind: 'dax', origin: 'agent', dax: 'EVALUATE   ROW("v",  2)', askedAt: '2026-10-16T12:05:00Z' }),
    row('a0', { kind: 'dax', origin: 'agent', dax: 'EVALUATE ROW("v", 2)', askedAt: '2026-10-16T11:00:00Z' })];
  assert.equal(N.assistantRowFor(all, 'EVALUATE ROW("v", 2)').id, 'a', 'the newest assistant row with that query, layout aside');
  assert.equal(N.assistantRowFor(all, 'EVALUATE ROW("v", 1)'), null, 'the person\'s row with the same text is not the assistant\'s');
  assert.equal(N.assistantRowFor(all, null), null);
});
check('H1: a measure focus never replaces the query of rows the assistant\'s run made', () => {
  assert.equal(AM.seedForMeasureFocus('Net Sales', { byAssistant: false, owns: false, measures: undefined, assistantRun: true }), null);
  assert.equal(AM.seedForMeasureFocus('Net Sales', { byAssistant: false, owns: false, measures: undefined, assistantRun: false }), 'EVALUATE ROW("Net Sales", [Net Sales])');
});
check('H1: the page is wired so (notebook, seed, Run)', () => {
  const nb = web('query-notebook.tsx'), lab = web('daxlab.tsx');
  assert.match(nb, /if \(!rowOwnsResult\(row, q\.producedBy, q\.assistantShown\)\) return;/, 'a figure is written only by the row\'s own run');
  assert.match(nb, /q\.claudeEvent\?\.kind === 'run_dax'[\s\S]{0,900}assistantRowFor\(list \?\? \[\], ran\)[\s\S]{0,900}setActive\(mine\.id\); setLiveRow\(mine\.id\)/, 'the assistant\'s run opens its own row (the notebook follows the run_dax event the Query page keeps; it is not a second consumer)');
  assert.match(nb, /q\.onSeeded\.current = \(\) => \{[^}]*setActive\(DRAFT\)/, 'a seed opens the draft, not the open row');
  assert.match(lab, /if \(activeRow\.current\) onSeeded\.current\?\.\(\);/, 'a seed with a row open leaves that row alone');
  assert.match(lab, /function run\(\) \{ setTab\('result'\); void runQuery\(assistantShown && assistantDax \? assistantDax : personQuery\(\)\); \}/, 'Run runs the DAX the DAX view shows');
  assert.match(lab, /assistantRun: !!assistantDax && !!res && producedByOf\(res, resQuery\) === squash\(assistantDax\)/);
});
check('H1: the engine keeps the assistant\'s run_dax as a row', () => {
  const tools = text(join(root, '..', 'Semanticus.Engine', 'McpTools.cs'));
  const at = tools.indexOf('Name = "run_dax"');
  assert.match(tools.slice(at, at + 1800), /KeepRunRowAsync\(engine, query, r\)/);
  const keep = tools.indexOf('static async Task<string> KeepRunRowAsync');
  assert.match(tools.slice(keep, keep + 900), /RecordQueryRunAsync\(query, r\.RowCount, r\.ElapsedMs, null, "agent"\)/);
});

// ── H4 ──────────────────────────────────────────────────────────────────────────────────────────
check('H4: a hand query of several columns names the column it shows', () => {
  const res = { columns: [{ name: 'Net Sales 2024' }, { name: 'Total Cost 2025' }], rows: [[36111492.86, 29871303.48]], rowCount: 1, truncated: false, elapsedMs: 5 };
  const f = N.figureOf(res, null);
  assert.equal(f.name, 'Total Cost 2025');
  assert.match(f.big, /29,871,303\.48|29871303\.48/);
  assert.equal(N.noteFor(row('x'), f, null, null).headline, 'Total Cost 2025: ' + f.big, 'the kept figure names its column too');
  const one = N.figureOf({ columns: [{ name: 'Net Sales' }], rows: [[5]], rowCount: 1, truncated: false, elapsedMs: 1 }, null);
  assert.equal(one.name, undefined, 'one column needs no name');
  const withFields = N.figureOf(res, { rows: [], cols: [], values: [{ kind: 'measure', ref: '[A]', name: 'A' }, { kind: 'measure', ref: '[B]', name: 'B' }], filters: [] });
  assert.equal(withFields.name, undefined, 'a question\'s own fields already name the number');
});
check('H4: the chips of another question are not drawn beside a hand query', () => {
  assert.match(web('query.tsx'), /const chipsOn = askOn && !q\.assistantShown && \(\(q\.chipsHere && q\.askOwns\) \|\| q\.chipsKept\);/);
  assert.match(web('query.tsx'), /fig\.name && <div className="sem-text-meta" data-testid="figure-name">/);
});

// ── H2 ──────────────────────────────────────────────────────────────────────────────────────────
check('H2: any measured run makes a row timed', () => {
  assert.deepEqual(N.measuredTimes({ coldTotal: { n: 0, avgMs: 0 }, warmTotal: { n: 3, avgMs: 67 } }), { warmMs: 67 }, 'warm runs alone are kept');
  assert.deepEqual(N.measuredTimes({ coldTotal: { n: 2, avgMs: 900 }, warmTotal: { n: 3, avgMs: 60 } }), { coldMs: 900, warmMs: 60 });
  assert.equal(N.measuredTimes({ coldTotal: { n: 0, avgMs: 0 }, warmTotal: { n: 0, avgMs: 0 } }), null);
  assert.equal(N.measuredTimes({ error: 'x', warmTotal: { n: 3, avgMs: 1 } }), null);
  assert.deepEqual(N.timeWords({ id: 'x', askedAt: '2026-10-16T12:00:00Z', warmMs: 67 }), { text: '67 ms repeat', slow: false, timed: true });
  assert.equal(N.timeWords({ id: 'x', askedAt: '2026-10-16T12:00:00Z', warmMs: 67 }).timed, true);
});
check('H2: the line says why, and what a shared endpoint skips, in one short line', () => {
  assert.equal(N.timingNote({ done: 2, failed: 0, total: 2, repeatOnly: 2, why: null }), 'Timed 2 of 2. First-run time is skipped on a shared server.');
  assert.equal(N.timingNote({ done: 1, failed: 1, total: 2, repeatOnly: 0, why: 'the query failed' }), 'Timed 1 of 2. 1 could not be timed: the query failed.');
  assert.match(N.timingNote({ done: 0, failed: 2, total: 2, repeatOnly: 0, why: 'no times came back' }), /2 could not be timed: no times came back\./);
  assert.equal(N.timingNote({ done: 3, failed: 0, total: 3, repeatOnly: 0, why: null }), 'Timed 3 of 3.');
  assert.ok(N.TIME_NOTE_MS > 0 && N.TIME_NOTE_MS <= 15000, 'the line leaves by itself');
  const nb = web('query-notebook.tsx');
  assert.match(nb, /const times = measuredTimes\(c\);/);
  assert.match(nb, /setTimeout\(\(\) => setTimeNote\(null\), TIME_NOTE_MS\)/);
});

// ── H3 ──────────────────────────────────────────────────────────────────────────────────────────
check('H3: turning Pro on writes nothing: the press that asked for it is only worked out', () => {
  const plan = web('plan.tsx');
  assert.doesNotMatch(plan, /upgradeToPro\(\{[^}]*run: onRun/, 'the unlock does not carry the write');
  assert.doesNotMatch(plan, /upgradeToPro\(\{ label: 'Run the plan', tab: 'plan', run: \(\) => \{ void runRowRef\.current\(item\)/, 'nor does a single row\'s');
  assert.match(plan, /run: afterUnlock \?\? \(\(\) => undefined\)/);
  assert.match(plan, /afterUnlock=\{\(\) => void proposeReadyRef\.current\(\)\}/);
  const at = plan.indexOf('const proposeReady = useCallback');
  const body = plan.slice(at, at + 900);
  assert.match(body, /'modelPlanAction', item\.id, 'propose'/);
  assert.doesNotMatch(body, /applyPlanItems|'approve'|runAllWaves/, 'nothing in it applies or approves');
  assert.doesNotMatch(web('propage.tsx'), /runs as soon as Pro is on/, 'the page no longer promises a run');
});

// ── H5 ──────────────────────────────────────────────────────────────────────────────────────────
check('H5: the Copilot student quotes table names', () => {
  // The .claude copy is internal and left out of the public source mirror; the private repo (marked by its mirror manifest) always checks it.
  const privateRepo = existsSync(join(root, '..', 'tools', 'release', 'mirror-manifest.json')); // mirror-optional: marks the private repo
  for (const f of [...(privateRepo ? [join(root, '..', '.claude', 'agents', 'copilot-student.md')] : []), join(root, 'assistant-pack', 'plugins', 'semanticus', 'agents', 'copilot-student.md')]) {
    const t = text(f);
    assert.match(t, /'Date'\[Year\]/, f);
    assert.match(t, /single quotes around a table name/i, f);
    assert.doesNotMatch(t, /Write columns as Table\[Column\]/, f);
  }
});

// ── H6 ──────────────────────────────────────────────────────────────────────────────────────────
check('H6: one word for a question with no number; one phrase for how it was read; no stale "Nothing asked yet"', () => {
  const ad = web('askdata.tsx');
  assert.match(ad, /refused: 'no number'/); assert.match(ad, /'needs-you': 'no number'/);
  assert.match(web('queriesask.tsx'), /const who = 'Read as';/);
  const q = web('query.tsx');
  assert.match(q, /!q\.res && !q\.err && !running && !a && r\.id === DRAFT \? <div className="sem-text-meta" data-testid="read-none">/);
  assert.match(q, /!q\.res && !q\.err && !running && !a && r\.id === DRAFT && conn\?\.connected && <div className="sem-text-meta">The answer appears here/);
});

// ── H7 ──────────────────────────────────────────────────────────────────────────────────────────
check('H7: figures read with separators, Run on rows that cannot run says why, history says how far off', () => {
  assert.equal(V.plainFigure('291894'), '291,894'); assert.equal(V.plainFigure('1000'), '1,000'); assert.equal(V.plainFigure('-1234.5'), '-1,234.5');
  assert.equal(V.plainFigure('$1.2M'), '$1.2M'); assert.equal(V.plainFigure(undefined), '');
  assert.equal(V.notRunRunWhy([{ family: 'rowcounts' }], true), null, 'row counts run with Run checks (UAT 1.5.104), so they are never refused for needing a source');
  assert.match(V.notRunRunWhy([{ row: {} }], false), /no live test model/i);
  assert.equal(V.notRunRunWhy([{ row: {} }], true), null, 'a saved check that can run just runs');
  assert.equal(V.notRunRunWhy([], true), null);
  assert.doesNotMatch(V.rulesReason('multiples'), /small multiples/);
  for (const k of ['total', 'bullet', 'dumbbell', 'variance', 'slope', 'sparktable', 'waterfall']) assert.doesNotMatch(V.rulesReason(k), /, so a /, k);
  const hist = V.historyVisual([{ runId: 'b', when: '2', checks: [{ name: 'r', kind: 'relationship', verdict: 'Pass' }] }, { runId: 'a', when: '1', checks: [{ name: 'r', kind: 'relationship', verdict: 'Pass' }] }]);
  assert.equal(hist.data.vLabel, 'Off now'); assert.equal(hist.data.v2Label, 'Off before');
  const t = web('tests.tsx');
  assert.match(t, /plainFigure\(outcome\.expected \?\? value\?\.expectedValue\)/);
  assert.match(t, /data-testid="v-notrun-why"/);
  assert.doesNotMatch(t, /<strong>\{outcome\.actual \?\? '·'\}<\/strong> from the model/);
});

// ── H8 ──────────────────────────────────────────────────────────────────────────────────────────
check('H8: Explain amounts and shares never share a column, nothing is cut, no engine words', () => {
  const ex = web('explainpanel.tsx');
  const bars = ex.slice(ex.indexOf('function ContributorBars'), ex.indexOf('// The copy-paste prompt'));
  assert.doesNotMatch(bars, /w-16/, 'the amount has no fixed narrow column');
  assert.doesNotMatch(bars, /truncate/, 'the name is not cut');
  // The no-wrap rule lives in the stylesheet now (the amount and the share each keep a grid column of their own).
  const css = text(join(root, 'webview', 'src', 'query.css'));
  assert.match(css, /\.ex-part-amt \{[^}]*white-space: nowrap/, 'the amount never wraps');
  assert.match(css, /\.ex-part-pct \{[^}]*white-space: nowrap/, 'the share never wraps');
  const E = load(src('explainpanel.tsx'));
  assert.equal(E.plainField("'Customer'[City]"), 'City'); assert.equal(E.plainField('Year'), 'Year');
  assert.equal(E.selectionWords([{ field: "'Customer'[City]", value: '= Perth' }]), 'City = Perth');
});

// ── H9 ──────────────────────────────────────────────────────────────────────────────────────────
check('H9: the engine hears who has the wheel once, not once per key', () => {
  const ext = text(join(root, 'src', 'extension.ts'));
  assert.match(ext, /function reportStudioDriver\(force = false\): void \{[\s\S]{0,700}if \(!force && key === lastReportedDriver\) return;/);
  assert.match(ext, /function noteStudioPersonInput\(\): void \{[\s\S]{0,500}reportStudioDriver\(\);\n\}/, 'a key press reports only a change');
  assert.match(ext, /studioLastAssistantCall = Math\.max\(studioLastAssistantCall \?\? -Infinity, started\);\n    reportStudioDriver\(true\);/, 'an assistant call still reports each time');
  assert.match(ext, /reportStudioDriver\(true\);   \/\/ a new engine starts/);
});

// ── H10 ─────────────────────────────────────────────────────────────────────────────────────────
check('H10: a row that is not being timed does not say Measuring', () => {
  const q = web('query.tsx');
  assert.match(q, /const measuring = q\.busy === 'measure' && !timingAll;/);
  assert.match(q, /<SpeedBody q=\{q\} r=\{r\} timingAll=\{!!nb\.timing\} \/>/);
  assert.doesNotMatch(q, /\{q\.busy === 'measure' \? 'Measuring…'/);
});

// ── H11 ─────────────────────────────────────────────────────────────────────────────────────────
check('H11: a hand query with a split is drawn from its own shape', () => {
  const res = { columns: [{ name: "'Store'[Region]" }, { name: 'Net Sales' }], rows: [['North', 22], ['South', 18], ['East', 9]], rowCount: 3, truncated: false, elapsedMs: 3 };
  const c = QM.configOfResult(res);
  assert.deepEqual(c.rows.map((f) => f.name), ['Region']); assert.deepEqual(c.values.map((f) => f.name), ['Net Sales']);
  assert.equal(QM.configOfResult({ ...res, rows: [['North', 22]], rowCount: 1 }), null, 'one row is a figure, not a split');
  assert.equal(QM.configOfResult({ columns: [{ name: 'a' }, { name: 'b' }], rows: [[1, 2], [3, 4]], rowCount: 2, truncated: false, elapsedMs: 1 }), null, 'numbers only: no split');
  assert.equal(QM.configOfResult({ columns: [{ name: 'a' }, { name: 'b' }], rows: [['x', 'y'], ['z', 'w']], rowCount: 2, truncated: false, elapsedMs: 1 }), null, 'text only: no number');
  const lab = web('daxlab.tsx');
  assert.match(lab, /const drawConfig = useMemo\(\(\) => chartConfig \?\? configOfResult\(shownRes\), \[chartConfig, shownRes\]\);/);
  assert.match(lab, /visualData\('table', shownRes, drawConfig\)/);
  assert.doesNotMatch(lab.slice(lab.indexOf('const defaultExplain')), /drawConfig/, 'Explain is never built from an inferred measure');
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('fix-hero25: all passed');
