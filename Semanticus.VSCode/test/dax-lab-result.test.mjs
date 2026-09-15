import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const daxlab = read('webview/src/daxlab.tsx');
const grid = read('webview/src/grid.tsx');
const wire = read('webview/src/wire.ts');
const authoring = read('webview/src/tests-authoring.tsx');
const tests = read('webview/src/tests.tsx');

assert.match(wire, /export function rowAnnouncement/, 'both doors share one capped-row announcement');
assert.match(wire, /shown\. More rows exist\./, 'a capped set must say more rows exist');
assert.match(wire, /export function timingAnnouncement/, 'zero-ms times must be labelled, not shown as 0');
assert.match(wire, /under 1 ms/, 'a 0 ms figure is not a measured round trip');
assert.match(wire, /export function queryIdentity/, 'the result must be able to name the query that produced it');
assert.match(wire, /query\?: string/, 'the result carries the query text');
assert.match(wire, /cancelled\?: boolean/, 'a stopped query is a typed result, not a silent hang');

assert.match(daxlab, />Stop</, 'a running query must have a Stop control');
assert.match(daxlab, /stopQuery/, 'Stop must call a stop function, not a dead Running button');
assert.match(daxlab, /cancelDax/, 'Stop must ask the engine to stop the running query');
assert.match(daxlab, /The query was stopped\./, 'stopping must say so in plain words');
assert.match(daxlab, /disabled=\{idle\}/, 'Reset must not replace the editor while a query is running');
assert.match(daxlab, /rowAnnouncement\(res\.rowCount, res\.truncated\)/, 'the result pane must announce a capped set');
assert.match(daxlab, /Result of:/, 'the result pane must name the query that produced the result');
assert.match(daxlab, /timingAnnouncement/, 'zero-ms timings must go through the honest label');
assert.match(daxlab, /Save measure check/, 'a calculation must offer a direct saved-check handoff');
assert.match(daxlab, /The visual's filters travel with the check\./, 'the handoff must say the filters travel');

// UX11: a check saved from a filtered visual must carry what the person was actually looking at. Dropping the
// filters turned a calculation narrowed to one year into a check over the whole model, which reads as correct.
assert.match(daxlab, /groupBy, filterDax: filters \|\| undefined/, 'the SQL handoff must carry the visual filters, not only the grouping fields');
assert.match(daxlab, /filterDax: filters \|\| undefined, expressionDax: editedExpression \|\| undefined/, 'the trusted-answer handoff must carry the filters and any edited formula');
assert.match(daxlab, /export function labExpressionEdit/, 'an edited formula must be distinguishable from the measure as saved');
assert.match(daxlab, /savedExpression && squash\(text\) === squash\(savedExpression\)/, 'text equal to the measure as saved is not an edit');
assert.match(daxlab, /The connection you are on now is written down\./, 'the handoff must say the connection is recorded');
assert.match(daxlab, /still runs against whichever Tests and queries connection is current/, 'the recorded connection must not read as a pin');

// The drawer shows the carried filters prefilled and editable, and asks which formula the check is about.
assert.match(authoring, /open=\{!!initial\.filterDax\}/, 'carried filters must be visible, not hidden in a collapsed block');
assert.match(authoring, /Carried over from the filters on your visual/, 'the drawer must say where the prefilled filters came from');
assert.match(authoring, /Which formula is this check about\?/, 'an edited formula must force a deliberate choice');
assert.match(authoring, /Test the measure as saved/, 'the drawer must offer the measure as saved');
assert.match(authoring, /Test this expression/, 'the drawer must offer the edited expression');
assert.match(authoring, /expressionDax: testExpression \? \(expressionDax\.trim\(\) \|\| undefined\) : undefined/, 'choosing the saved measure must drop the expression, not keep it silently');
assert.match(authoring, /filterDax: filterDax\.trim\(\) \|\| undefined/, 'the SQL check must persist its own filter');
assert.match(authoring, /Your SQL must use the same filter as the check/, 'the SQL side is the author\'s own text and must be narrowed by them');
assert.match(authoring, /Semanticus never rewrites your query/, 'and the drawer must say the engine will not do it for them');
assert.match(authoring, /Authored against \{authored \|\| current\}/, 'the drawer must name the connection the check is authored against');

// Authoring is a record, never a pin: the saved list and the evidence both say so.
assert.match(tests, /authored against \$\{def\.authoredAgainstLabel\}/, 'the saved list must name the authoring connection');
assert.match(tests, /Authored against \$\{authored\}, ran against \$\{ran\}/, 'evidence must say when the check ran somewhere else');
assert.match(tests, /asks \$\{asks\}/, 'the saved list must say whether the check asks the measure or an edited formula');
assert.match(daxlab, /No storage scan was captured/, 'a cached or metadata-only profile must be explained in plain language');
assert.match(daxlab, /did not produce any logged calculation steps/, 'missing calculation logs must be explained without making trace jargon primary');
assert.match(authoring, /ObjectBrowser kinds=\{\['column'\]\}/, 'source checks must use the shared cross-table field picker');
assert.match(authoring, /Product category, Customer region, Date year/, 'the context picker must make the intended three-table path clear');
assert.match(authoring, /Tests and queries connection/, 'the DAX target must be distinguished from the saved SQL endpoint');
// 1.2.0: the endpoint is no longer typed into a check. A check names a SAVED SQL SOURCE by stable id,
// and the four inline endpoint fields only ride through untouched so older checks keep running.
// CHANGED 2026-09-15 (Astra, round two, F1): the id is still what a check persists, but it is now built by
// the SHARED reconcileSourceFields so Try and Save cannot send different source fields. That helper also
// keeps a missing named source ON the payload; dropping it is what let an old inline address win.
assert.match(authoring, /\.\.\.reconcileSourceFields\(sqlSourceId, initial\.sqlSourceId/,
  'the accepted SQL source must be persisted in the definition by id');
assert.match(authoring, /server: initial\.server, database: initial\.database/, 'an older inline endpoint must ride through untouched');
assert.match(tests, /onTestSuiteChanged/, 'mounted test results must hear sidecar definition changes');
assert.match(tests, /results below still show the older check/, 'definition changes must keep old evidence visible and mark it stale');
assert.match(tests, /Not checked/, 'unavailable execution must be presented as not checked');
// A Not checked row is neutral by contract: the missing-measure line never borrows the failure colour, and
// it carries the two actions that actually resolve it.
const missingRow = tests.match(/if \(outcome\.missing\) \{[\s\S]*?\n  \}/);
assert.ok(missingRow, 'the opened row must branch on a missing calculation');
assert.match(missingRow[0], /var\(--sem-nv\)/, 'a missing calculation must speak in the not-checked tone');
assert.doesNotMatch(missingRow[0], /var\(--sem-bad\)/, 'not checked must never use the failure colour');
assert.match(missingRow[0], /label="Edit this check"/, 'the row must offer editing the check');
assert.match(missingRow[0], /label="Delete this check"/, 'the row must offer deleting the check');

// The blank standoff is restated in the person's words and linked to the setting that decides it.
assert.match(tests, /Every cell is blank in the model and NULL in the source\. This test does not treat those as equal\./, 'the blank standoff must be said without engine words');
assert.match(tests, /label="Choose what a blank means"/, 'the blank standoff must link its own setting');
assert.match(tests, /to check it\./, 'the blank standoff must say what choosing achieves');
assert.match(tests, /comparisonSummary\(differCount, matchCount, rows\.length/, 'the comparisons must carry a count, not colour alone');
assert.match(tests, /Tick to run a subset/, 'the saved-checks header must say what ticking does');

// Tolerance boxes name their own unit, and the blank requirement stays neutral until Save is attempted.
assert.match(authoring, /Relative %/, 'the relative tolerance box must name its unit');
assert.match(authoring, /aria-label="Relative tolerance in percent"/, 'the relative tolerance box must be labelled in percent for a screen reader');
assert.match(authoring, /aria-label="Absolute tolerance"/, 'the absolute tolerance box must keep its label');
assert.match(authoring, /saveAttempted \? 'var\(--sem-bad\)' : 'var\(--sem-muted\)'/, 'a requirement is neutral until Save is attempted');
// CHANGED 2026-09-15 (Astra, round two, F1): the same rule, moved into the ONE validation Try and Save now
// share. A blank meaning that has not been chosen still stays neutral until a button is pressed; it is now
// either button, because trying a check the engine would refuse is no more honest than saving it.
assert.match(authoring, /if \(!blankPolicy\) \{ setSaveAttempted\(true\);/,
  'an attempted Save must be what turns the requirement red');

assert.match(grid, /More rows exist/, 'the grid must say when more rows exist');

// =====================================================================================================
// Walkthrough behaviour defects (cp/walk-behaviour): the canvas and Verify must describe the mode you are
// actually in. Written before the fix, against the state the walkthrough photographed.
// =====================================================================================================

// M15: the canvas told you to drag a measure into Values while Total Sales was already sitting in Values.
// The real next step is Run, so the hint has to be driven by what the wells hold.
assert.match(daxlab, /const wellsReady =/, 'the empty canvas must know whether the wells already hold a measure');
assert.match(daxlab, /Press Run to build this visual\./, 'ready wells get the real next step, not a drag instruction');
assert.match(daxlab, /Drag a measure into Values to build a visual\./, 'empty wells keep the drag instruction');
assert.match(daxlab, /wellsReady \?[\s\S]{0,120}Press Run to build this visual/,
  'the two hints must be chosen by the wells, not printed unconditionally');

// M25: Verify in Query mode still named the Values well and the visual, neither of which is on screen there.
assert.match(daxlab, /visual \? 'Original formula: the measure selected in Values' : 'Original formula'/,
  'the first Verify box must name the Query-mode input when you are in Query mode');
assert.match(daxlab, /visual \? 'Test contexts from the visual' : 'Test contexts from the fields you choose'/,
  'the contexts heading must describe where the contexts came from in this mode');
assert.match(daxlab, /visual && filterLinesDerived\.length > 0/,
  'the "Filters from the visual" line belongs to Visual mode only');
assert.match(daxlab, /VerifyTab \{\.\.\.\{[^}]*visual: mode === 'visual'/,
  'Verify must be told which mode it is rendering for');

// M20: the New check popover ignored Escape, carried no menu role, and opened away from its own button.
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1400}aria-haspopup="menu"/, 'New check opens a menu');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1400}aria-expanded=\{open\}/, 'its state must be announced');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1600}role="menu"/, 'the popover must BE a menu');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1600}aria-label="New check"/, 'the menu must be named');
assert.match(authoring, /function Pick\([\s\S]{0,300}role="menuitem"/, 'each choice must be a menu item');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1800}key !== 'Escape' \|\| !open/, 'Escape must close it');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1800}window\.addEventListener\('keydown', escape\)/,
  'Escape must close it wherever focus is, the same contract as the other menus');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1600}buttonRef\.current\?\.focus\(\)/,
  'Escape must put focus back on New check');
assert.match(authoring, /function NewCheckMenu[\s\S]{0,1600}className="absolute left-0/,
  'the popover must hang under its own button, not under Run');
assert.doesNotMatch(authoring, /function NewCheckMenu[\s\S]{0,1400}absolute right-0/,
  'right-anchoring is what pushed it away from New check');

// =====================================================================================================
// Astra's spot review, 2026-09-14: "Calculations > Query > Save measure check > Check a trusted answer.
// I clicked Query without editing anything, and the drawer said I had changed the formula and preselected
// 'Test this expression' with the whole grouped EVALUATE SUMMARIZECOLUMNS query." Switching views must not
// silently turn a measure check into a table-query check. Written before the fix, against that state.
// =====================================================================================================
const { default: tsc } = await import('typescript');
const extract = (source, name, label) => {
  const m = source.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`));
  assert.ok(m, `${name} should remain a statically extractable top-level helper in ${label}`);
  const js = tsc.transpileModule(m[0], { compilerOptions: { target: tsc.ScriptTarget.ES2020 } }).outputText;
  return Function(`"use strict"; ${js}; return ${name};`)();
};

// The query a Visual to Query switch writes into the editor, from the wells Astra was handed.
const wellQuery = "EVALUATE\n    SUMMARIZECOLUMNS(\n        'Customer'[CustomerName],\n        'Sales'[OrderDateKey],\n        \"Total Sales\", [Total Sales]\n    )";
const labEdit = extract(daxlab, 'labExpressionEdit', 'daxlab.tsx');
assert.equal(labEdit('visual', wellQuery, 'Total Sales', 'SUM(Sales[Amount])', wellQuery), '',
  'Visual mode never carries an edited expression');
assert.equal(labEdit('query', wellQuery, 'Total Sales', 'SUM(Sales[Amount])', wellQuery), '',
  'a Visual to Query switch only regenerates the query from the same wells, so it is not an edit');
assert.equal(labEdit('query', 'EVALUATE ROW("Total Sales", [Total Sales])', 'Total Sales', 'SUM(Sales[Amount])', wellQuery), '',
  'the seeded measure query is the measure as saved, not a new idea');
assert.equal(labEdit('query', wellQuery + '\nORDER BY [Total Sales] DESC', 'Total Sales', 'SUM(Sales[Amount])', wellQuery),
  wellQuery + '\nORDER BY [Total Sales] DESC', 'text the person actually changed is still an edit');
assert.equal(labEdit('query', "CALCULATE([Total Sales], 'Date'[Year] = 2024)", 'Total Sales', 'SUM(Sales[Amount])', wellQuery),
  "CALCULATE([Total Sales], 'Date'[Year] = 2024)", 'a typed scalar expression is still an edit');

// The lab must RECORD what it generated rather than guess from the text: any regeneration is the lab's own
// writing, and only text that differs from it belongs to the person.
assert.match(daxlab, /generatedQuery/, 'the lab must remember the query it generated itself');
assert.match(daxlab, /const q = buildQuery\(config\); setQuery\(q\); setGeneratedQuery\(q\);/,
  'a mode switch must record the query it just wrote');
assert.match(daxlab, /labExpressionEdit\(mode, query, selectedMeasureName, savedExpressions\[testMeasureRef\], generatedQuery\)/,
  "the edited-formula signal must be measured against the lab's own generated text");

// A measure check compares ONE number. The drawer must not hand a whole table query to it, and must say so.
const oneCell = extract(authoring, 'daxReturnsOneCell', 'tests-authoring.tsx');
assert.equal(oneCell(''), true, 'nothing typed is not a table query');
assert.equal(oneCell('[Total Sales]'), true, 'a measure reference is one number');
assert.equal(oneCell("CALCULATE([Total Sales], 'Date'[Year] = 2024)"), true, 'a scalar expression is one number');
assert.equal(oneCell('EVALUATE ROW("v", [Total Sales])'), true, 'a one-pair ROW query is one cell');
assert.equal(oneCell(wellQuery), false, 'a grouped SUMMARIZECOLUMNS query is a table, not one number');
assert.equal(oneCell('EVALUATE TOPN(1000, SUMMARIZECOLUMNS(\'Date\'[Date], "v", [Total Sales]))'), false,
  "the lab's default TOPN query is a table");
assert.equal(oneCell('EVALUATE ROW("a", [Total Sales], "b", [Cost])'), false, 'a two-pair ROW query is two columns');
assert.equal(oneCell('EVALUATE ROW("v", [Total Sales]) ORDER BY [v]'), false, 'a trailing clause is a query, not a cell');
assert.equal(oneCell('DEFINE MEASURE \'Sales\'[X] = 1 EVALUATE ROW("v", [X])'), true,
  'a DEFINE block before a one-pair ROW is still one cell');
assert.equal(oneCell("EVALUATE 'Sales'"), false, 'a whole table is a table');

assert.match(authoring, /useState\(!!handedExpression && handedIsOneCell\)/,
  'a table query must never be preselected as the expression a measure check asks');
assert.match(authoring, /A measure check compares one number/, 'the drawer must say plainly what a measure check needs');
assert.match(authoring, /returns a table, not one number/, 'the drawer must name what the query actually returns');

console.log('DAX Lab result pane copy and controls passed');

// =====================================================================================================
// Astra's re-check, 2026-09-14: she typed EVALUATE ROW("v", [Total Sales] + 1) in Query, opened Check a
// trusted answer, entered 'Date'[Year] = 2024 under "Use a complete DAX filter", and saved. The request
// carried both fields and nothing on screen said what would happen to the filter. The engine now scopes
// the query with CALCULATETABLE or refuses it; the drawer has to say which, before the person saves.
// Written against the pre-fix drawer, where measureFilterNote did not exist.
// =====================================================================================================
const filterNote = extract(authoring, 'measureFilterNote', 'tests-authoring.tsx');
const year = "'Date'[Year] = 2024";
assert.equal(filterNote('EVALUATE ROW("v", [Total Sales] + 1)', ''), null,
  'no filter typed means there is nothing to explain');
assert.equal(filterNote("CALCULATE([Total Sales], 'Date'[Year] = 2024)", year), null,
  'a scalar expression is filtered the way it always was, so the drawer stays quiet');
assert.equal(filterNote('', year), null, 'the saved measure is filtered the way it always was');

const scoped = filterNote('EVALUATE ROW("v", [Total Sales] + 1)', year);
assert.equal(scoped && scoped.tone, 'ok', 'a single EVALUATE can be scoped, so the note is not a warning');
assert.match(scoped.text, /Applied around your query/,
  'the drawer must say how the filter reaches a complete query');

const defined = filterNote('DEFINE MEASURE ' + "'Sales'" + '[X] = 1 EVALUATE ROW("v", [X])', year);
assert.equal(defined && defined.tone, 'warn', 'a DEFINE-led query cannot take a filter from outside');
assert.match(defined.text, /DEFINE/, 'the warning must name what in the query stops it');

const ordered = filterNote('EVALUATE ROW("v", [Total Sales]) ORDER BY [v]', year);
assert.equal(ordered && ordered.tone, 'warn', 'a trailing ORDER BY cannot be scoped from outside');
assert.match(ordered.text, /ORDER BY/, 'the warning must name the clause');

const twice = filterNote('EVALUATE ROW("v", 1) EVALUATE ROW("v", 2)', year);
assert.equal(twice && twice.tone, 'warn', 'two EVALUATE blocks are two answers, not one scopeable query');

const literal = filterNote('EVALUATE ROW("ORDER BY", [Total Sales])', year);
assert.equal(literal && literal.tone, 'ok',
  'a clause name sitting inside a string is text, not structure');

// The refusal must not be something the person only discovers after saving.
assert.match(authoring, /measureFilterNote\(testExpression \? expressionDax : '', filterDax \|\| /,
  'the drawer must work the note out from the expression this check actually asks');
assert.match(authoring, /Applied around your query/, 'the note text must live in the drawer itself');
assert.doesNotMatch(authoring, /already carries its own filter/,
  'the old claim that a complete query carries its own filter is what dropped the filter');

console.log('measure check filter scope note passed');

// =====================================================================================================
// Kane, on the live build 2026-09-14: "SE and FE in performance are the same colour. they should be
// different". They were literally the same hex: --sem-good was defined as the same green as --sem-accent
// in BOTH theme blocks, so the split bar painted one colour twice and the two legend figures matched it.
// The two engines now own two named tokens, and an engine keeps one colour everywhere Studio draws it.
// =====================================================================================================
const styles = read('webview/src/styles.css');

const darkBlock = styles.slice(styles.indexOf(':root {'), styles.indexOf('body.vscode-light'));
const lightBlock = styles.slice(styles.indexOf('body.vscode-light'), styles.indexOf('html, body, #root'));
assert.ok(darkBlock.length > 0 && lightBlock.length > 0, 'both theme blocks must be found');

const tokenValue = (block, name) => {
  const found = block.match(new RegExp('--' + name + ':\\s*([^;]+);'));
  return found ? found[1].trim() : null;
};

// Contrast is the whole point of a second colour, so the test measures it rather than trusting the hex.
const channel = (v) => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4));
const luminance = (hex) => {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  assert.ok(m, `a theme colour must be a plain 6-digit hex so contrast can be measured, got ${hex}`);
  const n = parseInt(m[1], 16);
  return 0.2126 * channel(((n >> 16) & 255) / 255)
    + 0.7152 * channel(((n >> 8) & 255) / 255)
    + 0.0722 * channel((n & 255) / 255);
};
const contrast = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};
const rgb = (hex) => {
  assert.match(hex, /^#[0-9a-f]{6}$/i, `a theme colour must be a plain 6-digit hex, got ${hex}`);
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
};
const channelGap = (a, b) => Math.max(...rgb(a).map((v, i) => Math.abs(v - rgb(b)[i])));

for (const [theme, block] of [['dark', darkBlock], ['light', lightBlock]]) {
  const fe = tokenValue(block, 'sem-engine-fe');
  const se = tokenValue(block, 'sem-engine-se');
  const onFe = tokenValue(block, 'sem-on-engine-fe');
  const onSe = tokenValue(block, 'sem-on-engine-se');
  assert.ok(fe, `the ${theme} theme must define --sem-engine-fe`);
  assert.ok(se, `the ${theme} theme must define --sem-engine-se`);
  assert.ok(onFe, `the ${theme} theme must define the ink for the formula-engine fill`);
  assert.ok(onSe, `the ${theme} theme must define the ink for the storage-engine fill`);
  assert.notEqual(fe, se, `the two engines must be two different colours in the ${theme} theme`);
  // Luminance alone would not catch two hues of the same brightness, so the separation is measured
  // channel-wise: the two engines must be far apart in colour, not merely a shade lighter or darker.
  assert.ok(channelGap(fe, se) >= 60,
    `the two engine colours must be told apart in the ${theme} theme, not two shades of one`);
  assert.ok(contrast(fe, onFe) >= 4.5,
    `the label on the formula-engine segment must be legible in the ${theme} theme`);
  assert.ok(contrast(se, onSe) >= 4.5,
    `the label on the storage-engine segment must be legible in the ${theme} theme`);
}

const timings = daxlab.slice(daxlab.indexOf('function ServerTimingsView'), daxlab.indexOf('function ColdWarmView'));
assert.ok(timings.length > 0, 'the server timings view must be found');
assert.match(timings, /background: 'var\(--sem-engine-fe\)'/, 'the FE segment must take the formula-engine colour');
assert.match(timings, /background: 'var\(--sem-engine-se\)'/, 'the SE segment must take the storage-engine colour');
assert.match(timings, /color: 'var\(--sem-on-engine-fe\)'/, 'the FE segment label must take its own ink');
assert.match(timings, /color: 'var\(--sem-on-engine-se\)'/, 'the SE segment label must take its own ink');
assert.match(timings, /color: 'var\(--sem-engine-fe\)' \}\}>\{t\.feMs\}/, 'the formula-engine figure must match its segment');
assert.match(timings, /color: 'var\(--sem-engine-se\)' \}\}>\{t\.seMs\}/, 'the storage-engine figure must match its segment');
assert.match(timings, /color: 'var\(--sem-engine-se\)' \}\}>\{t\.seCacheHits\}/, 'SE cache hits are storage-engine work');
assert.match(timings, /color: 'var\(--sem-engine-se\)' \}\}>\{s\.durationMs\}/, 'a storage scan is storage-engine work');
assert.doesNotMatch(timings, /var\(--sem-good\)/, 'no engine figure may keep the shared pass-green');
assert.doesNotMatch(timings, /var\(--sem-accent\)/, 'no engine figure may keep the brand accent');

// A segment narrower than about 8% hides its own label, so the two engines are also named underneath.
assert.match(timings, /FE formula engine/, 'the bar must say what FE stands for in plain words');
assert.match(timings, /SE storage engine/, 'the bar must say what SE stands for in plain words');

// The storage-engine stat table is the same engine, so it carries the same colour as the bar segment.
const coldwarm = daxlab.slice(daxlab.indexOf('function ColdWarmView'), daxlab.indexOf('function QueryPlanView'));
assert.match(coldwarm, /var\(--sem-engine-se\)/, 'the storage-engine table must be marked with the storage-engine colour');

console.log('engine colours passed');

