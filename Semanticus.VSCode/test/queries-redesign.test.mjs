// The Queries redesign (Kane, 27 Sep): option C (the sentence, replaced on 8 Oct by labelled wells), diagnostics D1, and the Pro question box host
// contract H1 to H9 from the Queries pre-flight. Read off the source, the way dax-lab-result reads its copy; the
// behaviour itself is driven end to end by tools/uishot/queries-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const lab = read('webview/src/daxlab.tsx');
const ask = read('webview/src/queriesask.tsx');
const wells = read('webview/src/querywells.tsx');

// D1: two views only, Result and Speed. The old five are gone as views, and every tool is still reachable.
assert.match(lab, /const WB_TABS: \{ id: WbTab; label: string \}\[\] = \[\{ id: 'result', label: 'Result' \}, \{ id: 'speed', label: 'Speed' \}\];/, 'two views: Result and Speed');
assert.doesNotMatch(lab, /label: 'Performance'|label: 'Debug'|label: 'Verify'/, 'the old tabs are not views any more');
for (const [what, re] of [
  ['Measure speed runs cold and warm, then the profile', /'benchmarkColdWarm', q, runs, clearOnRun, confirmShared\)[\s\S]{0,300}'profileDax', q\)/],
  ['Quick stays', /'benchmarkDax'/], ['Profile only stays', />Profile only</],
  ['Clear kept results stays', />Clear kept results<\/Button>/], ['runs box', /id="lab-runs"/], ['clear before each first run', /Clear kept results before each first run/],
  ['shared endpoint confirm', /Shared endpoint: clearing the cache affects all users/],
  ['server steps fold inside Speed', /data-testid="queries-server-steps"/], ['record the steps', /'Record the steps'/], ['clear then record', /Clear kept results, then record/],
  ['every run fold', /<b className="text-xs">Every run<\/b>/],
  ['calculation steps from a number', /label: 'Calculation steps', detail: 'Run again and log each step of the calculation'[\s\S]{0,200}act: \(\) => \{ void debug\(\); \}/],
  ['explain this number beside it', /label: 'Explain this number'/],
  ['compare from the measure chip', /onCompare=\{compareValue\}/], ['compare from the ... menu', /label: 'Compare a rewrite', detail: 'Prove a new formula gives the same numbers'/], ['compare is a side sheet', /<SideSheet title=\{`Compare a rewrite of \$\{measureName\}`\}/],
  ['calculation steps sheet', /<SideSheet title="Calculation steps"/],
  ['explain panel', /<ExplainPanel payload=\{explain\}/], ['Stop', /stopQuery/], ['Reset is in the ... menu', /label: 'Reset', detail: 'Back to the starting fields and query'[\s\S]{0,80}act: resetDefault/],
  ['the assistant ran banner', /<ClaudeRanBanner/], ['staleness chip', /<QueryStalenessChip/], ['sign in again', /SIGN_IN_AGAIN/],
]) assert.match(lab, re, what);
// Reflections of the assistant's runs land where D1 puts each tool.
assert.match(lab, /useClaudeReflection\('capture_query_plan'[^\n]*setTab\('speed'\); setPlanOpen\(true\)/, 'an assistant plan opens the server steps fold');
assert.match(lab, /useClaudeReflection\('verify_equivalence'[^\n]*setSheet\('compare'\)/, 'an assistant comparison opens the compare sheet');
assert.match(lab, /useClaudeReflection\('evaluate_and_log'[\s\S]{0,700}setSheet\('steps'\)/, 'assistant calculation steps open the steps sheet');

// The wells (Kane, 8 Oct, replacing option C's sentence): four labelled wells, each with its own add, on the same query builder.
assert.match(wells, /label: 'Rows'[\s\S]*label: 'Columns'[\s\S]*label: 'Measures'[\s\S]*label: 'Filters'/, 'Rows / Columns / Measures / Filters');
assert.match(wells, /data-testid=\{`well-add-\$\{w\.key\}`\}/, 'each well has its own add');
assert.match(wells, /data-testid="queries-wells"/, 'the wells carry their test id');
assert.match(wells, /data-well-chip=/, 'a chip says which well holds it');
assert.doesNotMatch(wells, /word: 'Show'|word: 'across'/, 'the sentence words are gone');
assert.match(lab, /<QueryWells config=\{config\} onConfig=\{onWells\}/, 'the wells edit the same PivotConfig');
assert.doesNotMatch(lab, /QuerySentence/, 'the sentence is gone from the page');

// The space rule: the result fills the page and the query is not repeated.
assert.match(lab, /data-primary="result"/, 'the result region is tagged for the space check');
assert.match(lab, /sem-card sem-card-flush flex-1 flex flex-col min-h-0" data-primary="result"/, 'the result card grows to fill');
assert.doesNotMatch(lab, /useResizable\('lab\.wbH'/, 'no fixed-height result box');

// Charts: the rules pick, labelled with the reason, one click to change, one click back.
assert.match(lab, /chartRulePick\(shape\)/, 'the rules pick the chart');
assert.match(lab, /'Picked by the chart rules:'/, 'the pick is labelled');
assert.match(lab, /Back to the rules' pick/, 'one click back to the rules');
// Jev's pick (queries.chart_kind, D6): the label names Jev only for a pick Jev made, on a granted plan.
assert.equal((lab.match(/Jev picked/g) || []).length, 1, 'one place says Jev picked the chart');
assert.match(lab, /jevHere \? `Jev picked \$\{CHART_TITLE\[jevPick!\.kind\]\}:` : 'Picked by the chart rules:'/, 'Jev picked only when jevHere');
assert.match(lab, /setJevPick\(p\?\.by === 'jev' && p\.kind && rule\.fits\.includes\(p\.kind as VizType\)/, 'only an answer by Jev, inside the kinds that fit, counts');
assert.match(lab, /if \(!askOn \|\| !shape \|\| !rule \|\| rule\.fits\.length < 2\) return;/, 'Free never asks for a chart pick');

// The question box contract.
assert.match(lab, /const askGrant = useFeature\('askData'\)/, 'the box gates on the askData feature');
assert.match(lab, /\{askOn \? <AskField ask=\{ask\}/, 'the field is only mounted on a granted plan');
assert.match(lab, /askGrant === 'denied' \? <AskFreeLine \/> : <div className="flex-1" \/>/, 'Free draws the quiet line; unknown draws nothing');
assert.match(ask, /Ask in plain words/, 'the Free line, promising no chips');
assert.doesNotMatch(ask, /useEffect\(\(\) => \{[^}]*rpc\(/, 'the hook asks nothing on mount');
assert.match(ask, /if \(!menu \|\| columns\) return;/, 'the chip pickers load on the first menu open');
assert.match(lab, /setQuery\(dax\); setGeneratedQuery\(dax\);/, 'H1: the DAX is the tool\'s own writing');
assert.match(lab, /onRows: \(rs, dax\) => \{ runGen\.current\+\+; setRes\(rs\)/, 'H2: the rows come from the answer, no second run');
assert.match(lab, /busyElsewhere: busy !== null && busy !== 'ask'/, 'H3: one busy state');
assert.match(lab, /if \(busy === 'ask'\) \{ ask\.stop\(\); return; \}/, 'H3: one Stop');
assert.match(ask, /'lab\.ask\.words\.' \+ persistKey[\s\S]{0,200}'lab\.ask\.parts\.' \+ persistKey[\s\S]{0,200}'lab\.ask\.dax\.' \+ persistKey/, 'H4: words, parts and DAX per model');
assert.doesNotMatch(ask, /usePersistedState[^\n]*answer/, 'H4: rows are never persisted');
assert.match(lab, />Put back my query</, 'H5: the person\'s query can be put back');
assert.match(lab, /\{chipsHere \? <>[\s\S]{0,600}title="Save a number from this answer"/, 'H6: only a number from the answer while the chips own the query');
assert.match(lab, /handedFrom="queries"/, 'H6: the check is handed from the answer path, and says it came from Queries');
assert.match(ask, /if \(\(e\.ctrlKey \|\| e\.metaKey\) && e\.key === 'Enter'\) \{ e\.preventDefault\(\); e\.stopPropagation\(\);[^\n]*ask\.askWords\(\); \}/, 'H7: the run key in the field asks');
assert.match(lab, /closest\?\.\('\[data-ask-field\]'\)\) return;/, 'H7: the page run key ignores the field');
assert.match(lab, /mode === 'visual' && !chipsHere \? buildQuery\(config\) : query/, 'H8: a question runs the editor\'s DAX, not the wells');
assert.equal((lab.match(/<ConnectBar /g) || []).length, 1, 'H9: one connect prompt on the surface');
assert.doesNotMatch(ask, /<ConnectBar/, 'H9: the box never draws its own connect prompt');
assert.match(ask, /maxRows: QUERIES_MAX_ROWS/, 'Queries asks for depth');
assert.match(ask, /const QUERIES_MAX_ROWS = 10000;/, '10,000 rows');

// CI guards for the two contract breaks only the drivers used to see (verify-queries-redesign T: mutants M1 and M2
// both passed npm test). These read the source, so they are exact about the shapes that matter.
// M1, a Free leak: the question box, its chips and its lines exist only on a granted plan. 'unknown' is not granted.
assert.match(lab, /const askOn = askGrant === 'granted';/, 'M1: the box is on only when the plan grants askData');
assert.match(lab, /const chipsHere = askOn && /, 'M1: a question\'s chips count only on a granted plan');
assert.match(lab, /\{askOn && <AskLines ask=\{ask\} current=\{ask\.answer\?\.dax \? answerRows : !res\} \/>\}/, 'M1: the answer lines only on a granted plan, told whether the statement still describes what is on screen');
assert.equal((lab.match(/<AskField /g) || []).length, 1, 'M1: one question field, mounted in one place');
// M2, a doubled run: an answer's rows come from the answer. The one runDax call is inside runQuery, the question
// hook never runs a query, and runQuery is called from exactly these places, none of them an answer's landing.
assert.equal((lab.match(/'runDax'/g) || []).length, 1, 'M2: runDax is sent from one place (runQuery)');
assert.doesNotMatch(ask, /'runDax'|runQuery/, 'M2: the question hook never runs a query of its own');
const runSites = [...lab.matchAll(/^.*\brunQuery\(.*$/gm)].map((m) => m[0].trim());
const expected = [
  /^async function runQuery\(q: string\) \{$/,
  /^function run\(\) \{ setTab\('result'\); void runQuery\(currentQuery\(\)\); \}$/,
  /^const t = setTimeout\(\(\) => \{ void runQuery\(q\); \}, 220\);$/,
  /^if \(runSentence && conn\?\.connected && config\.values\.length\) void runQuery\(buildQuery\(config\)\);$/,
  /^else if \(chipsHere\) void runQuery\(buildQuery\(config\)\);$/,
  /^\.then\(\(r\) => \{ if \(r\.ok\) void runQuery\(currentQuery\(\)\); else if \(r\.message\) setErr\(r\.message\); \}\);$/,
];
assert.equal(runSites.length, expected.length, `M2: runQuery is called from ${runSites.length} places, expected ${expected.length}:\n  ${runSites.join('\n  ')}`);
for (const re of expected) assert.ok(runSites.some((l) => re.test(l)), `M2: a known runQuery call site is missing or changed: ${re}`);
// Line ends are spelled \r?\n: a Windows checkout (.gitattributes `* text=auto`) gives these sources CRLF.
const onRows = /onRows: \(rs, dax\) => \{([^\r\n]*)\},\r?\n/.exec(lab)?.[1] ?? '';
assert.ok(onRows && !/runQuery|rpc\(/.test(onRows), 'M2: an answer\'s rows land without a run');
const onDax = /onDax: \(dax\) => \{([\s\S]*?)\r?\n    \},\r?\n/.exec(lab)?.[1] ?? '';
assert.ok(onDax && !/\brun\(|runQuery|rpc\(|debug\(|measureSpeed|setTimeout/.test(onDax), 'M2: the answer\'s DAX lands in the editor without a run');
// run() itself is started only by the person: the Run button and the run key.
assert.equal((lab.match(/\brun\(\)(?! \{)/g) || []).length, 1, 'M2: run() is called from the run key only');
assert.equal((lab.match(/onClick=\{run\}/g) || []).length, 1, 'M2: and from the Run button');
// The auto-run of the wells steps aside while a question holds the query.
assert.match(lab, /if \(mode !== 'visual' \|\| chipsHere \|\| !conn\?\.connected \|\| !wellsTouched\.current\) return;/, 'M2: the wells do not re-run under a question');

console.log('queries redesign passed');
