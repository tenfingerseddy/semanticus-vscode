// The Query page's contract (it began as the Queries redesign, Kane 27 Sep: diagnostics D1 and the Pro question box host contract H1
// to H9 from the Queries pre-flight; on 15 Oct 2026 Ask your data and Queries became the one page, Explore > Query, built as Kane picked
// option B). Read off the source, the way dax-lab-result reads its copy; the behaviour itself is driven end to end by
// tools/uishot/query-drive.mjs. test/query-view.test.mjs holds the parity list and the layout.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const hook = read('webview/src/daxlab.tsx');
const page = read('webview/src/query.tsx');
const lab = hook + '\n' + page;
const ask = read('webview/src/queriesask.tsx');
const wells = read('webview/src/querywells.tsx');

// D1 kept: every speed and diagnostic tool is still reachable, now as cards on the one page (no tabs, no side sheets).
for (const [what, re] of [
  ['Measure speed runs cold and warm, then the profile', /'benchmarkColdWarm', q, runs, clearOnRun && \(!shared \|\| confirmShared\), confirmShared\)[\s\S]{0,900}'profileDax', q\)/],
  ['Quick stays', /'benchmarkDax'/], ['Profile only stays', />Profile only</],
  ['Clear kept results stays', />Clear kept results</], ['runs box', /id="lab-runs"/], ['clear before each first run', /Clear kept results before each first run/],
  ['shared endpoint confirm', /Shared endpoint: clearing the cache affects all users/],
  ['server steps card', /data-testid="queries-server-steps"/], ['record the steps', /'Record the steps'/], ['clear then record', /Clear kept results, then record/],
  ['every run (behind Show every run, closed by default)', /Show every run/],
  ['calculation steps from a number', /label: 'Calculation steps', detail: 'Run again and log each step of the calculation'[\s\S]{0,200}act: \(\) => \{ nb\.setTab\('steps'\); void q\.debug\(\); \}/],
  ['explain this number is a card', /<ExplainPanel payload=\{sel\} inline \/>/],
  ['compare from the measure chip', /onCompare=\{q\.compareValue\}/], ['compare is a view', /data-testid="compare-card"/],
  ['steps is a view', /data-testid="steps-card"/],
  ['Stop', /stopQuery/], ['Reset is in the bar', /data-testid="query-reset"[^\n]*>Reset</],
  ['the assistant ran banner', /<ClaudeRanBanner/], ['staleness chip', /<QueryStalenessChip/], ['sign in again', /SIGN_IN_AGAIN/],
]) assert.match(lab, re, what);
assert.doesNotMatch(lab, /<SideSheet|WB_TABS|label: 'Performance'|label: 'Debug'|label: 'Verify'/, 'no side sheets and no tabs: every tool is a card');
// Reflections of the assistant's runs land where D1 puts each tool.
assert.match(hook, /useClaudeReflection\('capture_query_plan'[^\n]*setTab\('speed'\); setPlanOpen\(true\)/, 'an assistant plan opens the server steps');
assert.match(hook, /useClaudeReflection\('verify_equivalence'[^\n]*setSheet\('compare'\)/, 'an assistant comparison asks for the Compare card');
assert.match(hook, /useClaudeReflection\('evaluate_and_log'[\s\S]{0,700}setSheet\('steps'\)/, 'assistant calculation steps ask for the Steps card');
assert.match(page, /if \(q\.sheet === 'compare' \|\| q\.sheet === 'steps'\) \{[\s\S]*nb\.setTab\(q\.sheet\)/, 'the page opens the Compare or Steps view when it is asked for');

// The wells (Kane, 8 Oct): four labelled wells, each with its own add, on the same query builder.
assert.match(wells, /label: 'Rows'[\s\S]*label: 'Columns'[\s\S]*label: 'Measures'[\s\S]*label: 'Filters'/, 'Rows / Columns / Measures / Filters');
assert.match(wells, /data-testid=\{`well-add-\$\{w\.key\}`\}/, 'each well has its own add');
assert.match(wells, /data-testid="queries-wells"/, 'the wells carry their test id');
assert.match(wells, /data-well-chip=/, 'a chip says which well holds it');
assert.doesNotMatch(wells, /word: 'Show'|word: 'across'/, 'the sentence words are gone');
assert.match(page, /<QueryWells config=\{q\.config\} onConfig=\{q\.onWells\}/, 'the wells edit the same PivotConfig');
assert.doesNotMatch(lab, /QuerySentence/, 'the sentence is gone from the page');

// The space rule: the result is shown once, and the query is not repeated above it.
assert.match(page, /data-primary="result"/, 'the result region is tagged for the space check');

// Visuals: the rules narrow, Jev picks, one press changes it and one press puts it back. Jev's label only for a pick Jev made.
assert.match(hook, /queryVisualFits\(shape\)/, 'the visual rules pick the visual');
assert.match(page, /data-by=\{q\.jevHere \? 'jev' : 'rules'\}/, 'the pick line says Jev only when Jev picked');
assert.match(page, /\{q\.picked && <button[^\n]*data-testid="pick-back" onClick=\{\(\) => \{ q\.setVizOverride\(null\); setOn\(false\); \}\}/, 'one press back to the pick');
assert.match(hook, /setJevPick\(p\?\.by === 'jev' && p\.kind && rule\.fits\.includes\(p\.kind as Kind\)/, 'only an answer by Jev, inside the kinds that fit, counts');
assert.match(hook, /if \(!askOn \|\| !shape \|\| !rule \|\| rule\.fits\.length < 2\) return;/, 'Free never asks for a visual pick');
assert.equal((hook.match(/'pickChartKind'/g) || []).length, 1, 'one call for each answer, from one place');

// The question box contract.
assert.match(hook, /const askGrant = useFeature\('askData'\)/, 'the box gates on the askData feature');
assert.match(page, /\{askOn\s*\? <AskField ask=\{asking\}/, 'the field is only mounted on a granted plan');
assert.match(page, /askGrant === 'denied'\s*\? <><AskFreeLine \/>/, 'Free draws the quiet line; unknown draws nothing');
assert.match(ask, /Ask in plain words/, 'the Free line, promising no chips');
assert.doesNotMatch(ask, /useEffect\(\(\) => \{[^}]*rpc\(/, 'the hook asks nothing on mount');
assert.match(ask, /if \(!menu \|\| columns\) return;/, 'the chip pickers load on the first menu open');
assert.match(hook, /setQuery\(dax\); setGeneratedQuery\(dax\);/, 'H1: the DAX is the tool\'s own writing');
assert.match(hook, /onRows: \(rs, dax\) => \{ runGen\.current\+\+; setRes\(rs\)/, 'H2: the rows come from the answer, no second run');
assert.match(hook, /busyElsewhere: busy !== null && busy !== 'ask'/, 'H3: one busy state');
assert.match(hook, /if \(busy === 'ask'\) \{ ask\.stop\(\); return; \}/, 'H3: one Stop');
assert.match(ask, /'lab\.ask\.words\.' \+ persistKey[\s\S]{0,200}'lab\.ask\.parts\.' \+ persistKey[\s\S]{0,200}'lab\.ask\.dax\.' \+ persistKey/, 'H4: words, parts and DAX per model');
assert.doesNotMatch(ask, /usePersistedState[^\n]*answer/, 'H4: rows are never persisted');
assert.match(page, />Put back my query</, 'H5: the person\'s query can be put back');
assert.match(page, /q\.chipsHere\s*\? <button type="button" className="qv-saveopt" disabled=\{!canNumber\}[\s\S]{0,400}Save a number from this answer/, 'H6: only a number from the answer while the chips own the query');
assert.match(page, /handedFrom="queries"/, 'H6: the check is handed from the answer path, and says it came from Query');
assert.match(ask, /if \(\(e\.ctrlKey \|\| e\.metaKey\) && e\.key === 'Enter'\) \{ e\.preventDefault\(\); e\.stopPropagation\(\);[^\n]*ask\.askWords\(\); \}/, 'H7: the run key in the field asks');
assert.match(page, /closest\?\.\('\[data-ask-field\]'\)\) return;/, 'H7: the page run key ignores the field');
assert.match(hook, /entry === 'build' && !chipsHere && !wordsOnly \? buildQuery\(config\) : query/, 'H8: a question runs the editor\'s DAX, not the wells (and v-batch1: Run runs the DAX the DAX tab shows, whatever mode the wells are in)');
assert.equal((page.match(/<ConnectBar /g) || []).length, 3, 'H9: one connect prompt on screen at a time: the empty notebook, the open answer and a kept (not run yet) row each have one, and never two together');
assert.match(page, /nb\.shown\.length === 0 && <div className="qn-empty"/, 'H9: the empty notebook prompts only while no row is open');
assert.doesNotMatch(ask, /<ConnectBar/, 'H9: the box never draws its own connect prompt');
assert.match(ask, /maxRows: QUERIES_MAX_ROWS/, 'Query asks for depth');
assert.match(ask, /const QUERIES_MAX_ROWS = 10000;/, '10,000 rows');

// CI guards for the two contract breaks only the drivers used to see (verify-queries-redesign T: mutants M1 and M2
// both passed npm test). These read the source, so they are exact about the shapes that matter.
// M1, a Free leak: the question box, its chips and its lines exist only on a granted plan. 'unknown' is not granted.
assert.match(hook, /const askOn = askGrant === 'granted';/, 'M1: the box is on only when the plan grants askData');
assert.match(hook, /const chipsHere = askOn && /, 'M1: a question\'s chips count only on a granted plan');
assert.match(page, /\{askOn && <AskLines ask=\{q\.askBox\} current=\{q\.askState\.answer\?\.dax \? q\.answerRows : !q\.res\} quiet \/>\}/, 'M1: the answer lines only on a granted plan, told whether the statement still describes what is on screen');
assert.equal((page.match(/<AskField /g) || []).length, 1, 'M1: one question field, mounted in one place');
// M2, a doubled run: an answer's rows come from the answer. The one runDax call is inside runQuery, the question
// hook never runs a query, and runQuery is called from exactly these places, none of them an answer's landing.
assert.equal((lab.match(/'runDax'/g) || []).length, 1, 'M2: runDax is sent from one place (runQuery)');
assert.doesNotMatch(ask, /'runDax'|runQuery/, 'M2: the question hook never runs a query of its own');
const runSites = [...lab.matchAll(/^.*\brunQuery\(.*$/gm)].map((m) => m[0].trim());
const expected = [
  /^async function runQuery\(q: string, record = true\) \{$/,
  /^function run\(\) \{ setTab\('result'\); void runQuery\(assistantShown && assistantDax \? assistantDax : personQuery\(\)\); \}$/,
  /^const t = setTimeout\(\(\) => \{ void runQuery\(q, !quiet\); \}, 220\);$/,
  /^if \(runSentence && conn\?\.connected && config\.values\.length\) void runQuery\(buildQuery\(config\)\);$/,
  /^if \(conn\?\.connected && personQuery\(\)\.trim\(\) && \(mode === 'query' \|\| chipsHere \|\| wellsReady\)\) void runQuery\(personQuery\(\)\);$/,
  /^const signIn = .*void q\.runQuery\(q\.currentQuery\(\)\)/,
  /^void q\.runQuery\(text\);/,   // DAX typed in the question box runs as DAX, as its own row (v-batch1)
];
// Opening a folded query row runs it, once, and only with a connection (the notebook, 16 Oct): a press, never an answer's landing.
{ const nb = read('webview/src/query-notebook.tsx');
  assert.match(nb, /function open\(r: Rec\)[\s\S]*?runRow\(r\);/, 'M2: opening a row runs it from the press (open calls runRow)');
  assert.match(nb, /isDaxRow\(r\)\) \{ q\.chooseEntry\('dax'\); q\.setQuery\(r\.dax \?\? ''\); setLiveRow\(r\.id\); void q\.runQuery\(r\.dax \?\? ''\);/, 'M2: a DAX row runs as written and becomes the live row'); }
assert.equal(runSites.length, expected.length, `M2: runQuery is called from ${runSites.length} places, expected ${expected.length}:\n  ${runSites.join('\n  ')}`);
for (const re of expected) assert.ok(runSites.some((l) => re.test(l)), `M2: a known runQuery call site is missing or changed: ${re}`);
// Line ends are spelled \r?\n: a Windows checkout (.gitattributes `* text=auto`) gives these sources CRLF.
const onRows = /onRows: \(rs, dax\) => \{([^\r\n]*)\},\r?\n/.exec(hook)?.[1] ?? '';
assert.ok(onRows && !/runQuery|rpc\(/.test(onRows), 'M2: an answer\'s rows land without a run');
const onDax = /onDax: \(dax\) => \{([\s\S]*?)\r?\n    \},\r?\n/.exec(hook)?.[1] ?? '';
assert.ok(onDax && !/\brun\(|runQuery|rpc\(|debug\(|measureSpeed|setTimeout/.test(onDax), 'M2: the answer\'s DAX lands in the editor without a run');
// run() itself is started only by the person: the Run buttons and the run key.
assert.equal((hook.match(/\brun\(\)(?! \{)/g) || []).length, 0, 'M2: the hook never calls run()');
assert.equal((page.match(/\bq\.run\(\)/g) || []).length, 2, 'M2: run() is called from the run key and the DAX view\'s Run button only: a person\'s press');
assert.match(page, /data-testid="dax-run"/, 'M2: the one Run button is in the DAX view, beside the query it runs');
// The auto-run of the wells steps aside while a question holds the query.
assert.match(hook, /if \(mode !== 'visual' \|\| chipsHere \|\| wordsOnly \|\| !conn\?\.connected \|\| !wellsTouched\.current\) return;/, 'M2: the wells do not re-run under a question');

// Query show (Kane, 13 Oct): an assistant's run is the result at once (its DAX shown read-only in place of the person's wells), and
// Speed never shows numbers that belong to another query. Driven end to end by tools/uishot/query-show-drive.mjs.
assert.match(hook, /useClaudeReflection\('run_dax'[\s\S]{0,1600}setAssistantDax\([^\n]*setTab\('result'\)/, 'an assistant run selects the Result view');
assert.match(page, /data-testid="queries-assistant-dax"/, 'the assistant\'s DAX is shown read-only');
assert.match(page, /queryShownAbove=\{q\.assistantShown && q\.claudeEvent\.kind === 'run_dax'\}/, 'the strip does not repeat the DAX the box shows');
assert.doesNotMatch(lab, /queries-speed-stale|measured before, not the result on screen/, 'no stale-number warning: numbers of another query are not shown');
assert.match(page, /const own = q\.ownSpeed \|\| \(!!q\.speedQuery && !!r\.dax && squash\(q\.speedQuery\) === squash\(r\.dax\)\);\r?\n  const cw = own \? q\.cw : null, bench = own \? q\.bench : null, t = own \? q\.timings : null;/, 'Speed gets only this row\'s own numbers: the result on screen\'s, or those measured for the row\'s own query');

console.log('queries redesign passed');
