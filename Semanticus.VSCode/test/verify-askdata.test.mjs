// verify: defects the verifier found in the COMBINED Ask your data build (26 September 2026): m11/jev-query 2a3fa67c and
// m11/askdata-page 154208a3 merged onto m11/integration 568435d3, driven live on Northstar Sales Working in UAT lane h
// (Pro, Jev on). Every check in this file FAILS on that combination and is left failing on purpose: each one is a
// defect for the builders to fix, with the live evidence in its message. Nothing here changes product code.
//
//   1. Ctrl J drafts a Plan task from the preflight's own data question ("total sales by region") instead of sending
//      the person to Ask your data. The Plan's own field routes the same words to the page, so the two doors disagree.
//   2. A question refused because no model with data is connected says "Connect a copy of this model that holds
//      data..." and offers no way to connect; only the "built" outcome draws the connect bar.
//   3. The consent card a person agrees to on this build grants query.summarize's fields (the words of data
//      questions), yet the card never names data questions.
//
// Round 2 (verify-askdata-r2, m11/askdata 74c214b5): checks 1 to 3 above pass there. Sections 4 to 7 at the bottom
// FAIL there and are left failing on purpose.
import assert from 'node:assert/strict';
import { readFileSync, existsSync, mkdtempSync, mkdirSync, readdirSync, writeFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
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

// The same loader ask-data-page.test.mjs uses: TypeScript transpiled in memory, the host-facing modules stubbed.
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
  },
  './connection': {
    useConnection: () => ({ conn: { connected: false }, session: { sessionId: 'test' }, context: null }),
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

const failures = [];
let checks = 0;
function check(what, fn) {
  checks++;
  try { fn(); console.log(`  [PASS] ${what}`); }
  catch (e) { failures.push(what); console.log(`  [FAIL] ${what}\n         ${String(e.message).split('\n').join('\n         ')}`); }
}

// ── 1. Ctrl J and a data question ─────────────────────────────────────────────────────────────────────
// askbox.tsx AskBar.submit routes only what isQuestion calls a question; everything else becomes compose_task, a
// Plan draft. The preflight (Kane chose "redesign + page now", 26 Sep): "Ctrl J only NAVIGATES data questions to the
// page" and "Route words so 'total sales by region' lands here". Live in lane h: Ctrl J + "total sales by region" +
// Enter made the Plan row "total sales by region, Pick the steps" and never asked where to go; "what are total sales
// by region" offered "Open Ask your data". The engine's own routing test (AskRouteTests.A_data_question_lands_on_
// ask_your_data) names these four; Ctrl J must not turn them into tasks. If the fix moves the decision out of
// isQuestion (for example, route first and draft only when nothing matches), move this check with it.
// RETIRED by fix-askdata-r4 (Kane, 27 Sep: Ctrl J's question routing is out of this release). Ctrl J now drafts
// every line it is given, so there is no routing rule left to send these four anywhere; a data question is asked in
// the Plan's field or on Ask your data itself. What stands in: Ctrl J calls compose_task with the words and never
// route_ask, read off askbox.tsx AskBar (the same fact sections 4 and 8 now check). The four phrases stay listed.
const askBarSource = (() => {
  const s = readFileSync(join(webview, 'src', 'askbox.tsx'), 'utf8').replace(/\r\n/g, '\n');
  const i = s.indexOf('export function AskBar');
  return i < 0 ? '' : s.slice(i, s.indexOf('\n}\n', i));
})();
const ctrlJDraftsTheWords = () => {
  assert.match(askBarSource, /rpc<[^>]*>\('composeTask', words,/, 'Ctrl J drafts the words it is given');
  assert.doesNotMatch(askBarSource, /runAsk|routeAsk|isQuestion/, 'Ctrl J routes nothing, so no line it is given can be routed and lost');
};
for (const ask of ['total sales by region', 'net sales by store region for 2025', 'top 10 products by revenue', 'ask your data']) {
  check(`(retired: Ctrl J routing is out of this release) Ctrl J drafts "${ask}"`, ctrlJDraftsTheWords);
}

// ── 2. Refused because no data is connected: the person needs a way to connect ────────────────────────
// Live in lane h after an engine restart (the live connection was gone): Run again on "Units by Channel and Category,
// Channel is not Online, Year from 2024" came back refused with the parts check "Connect a copy of this model that
// holds data. The words are matched against its values, and the query runs on it." The page showed that sentence and
// nothing to act on; the connect bar appears only for the "built" outcome (askdata.tsx AnswerBody). Every question
// with a filter is refused this way when no model with data is connected.
const page = load(join(webview, 'src', 'askdata.tsx'));
const NO_DATA = 'Connect a copy of this model that holds data. The words are matched against its values, and the query runs on it.';
check('a question refused because no model with data is connected offers the way to connect', () => {
  const html = renderToStaticMarkup(React.createElement(page.AnswerBody, {
    answer: { outcome: 'refused', readBy: 'you', checks: [{ name: 'parts', passed: false, line: NO_DATA }], unsure: [] },
    parts: { measures: ['Units'], groupBy: ["'Store'[Channel]"], filters: [{ column: "'Store'[Channel]", op: 'is_not', values: ['Online'] }] },
    connected: false, view: 'table', showDax: false, onView: () => {}, onShowDax: () => {},
  }));
  assert.ok(html.includes(NO_DATA), 'the engine line is shown');
  assert.ok(html.includes('data-testid="connect-bar"'),
    'the page tells the person to connect a model that holds data and draws no connect bar to do it with');
});

// ── 3. The consent card and query.summarize ───────────────────────────────────────────────────────────
// consent.tsx grants fieldsInForce, which is every field DecisionRegistry declares. On this build that includes
// query.summarize:question_words (live, lane h: get_consent after "OK for this model" listed query.summarize:
// model_fields, question_words and value_matches as approved). The card's "What goes" names the ask box's questions
// and the words that describe a task, and nothing about data questions. DecisionRegistryTests' comment says "a version
// 4 permission does not cover the new point anyway"; a permission given on this build's card does. It was latent
// only while JevQueryReader.JevReads was false; it is true again (Kane, 28 Sep), so the card must name the words of data questions, or they go under a
// card that never named them. The jev-query lane left the card line and the version bump to the integrator.
const registry = readFileSync(join(repoRoot, 'Semanticus.Core', 'Decisions', 'DecisionRegistry.cs'), 'utf8');
const card = src('consent.tsx');
check('the consent card names the words of data questions, since the registry declares them', () => {
  assert.ok(/"question_words"/.test(registry), 'the registry declares query.summarize:question_words');
  const goes = /goes:\s*\[([\s\S]*?)\]/.exec(card);
  assert.ok(goes, 'the card has a "What goes" list');
  assert.ok(/data question/i.test(goes[1]),
    'the card a person agrees to grants query.summarize:question_words, and its "What goes" never names data questions');
});

// ════ Round 2 (verify-askdata-r2, 26 September 2026): m11/askdata 74c214b5 ════════════════════════════════════════
// The six checks above pass on 74c214b5. The checks below FAIL there and are left failing on purpose.

// ── 4. The defect 1 fix for Ctrl J drafts no longer drafts some tasks ─────────────────────────────────
// composer-model.ts isDataQuestion routes any words shaped "X by Y" or "X per Y" that do not start with a verb on its
// list, so a task typed into Ctrl J that says "by" is routed instead of drafted. Composer contract 10.2: "A task
// typed there calls compose_task and opens the Plan with the draft; a question keeps route_ask's behaviour", and the
// contract's question is "words that end in a question mark or start with a question word". The compose replay's
// own labelled tasks (tools/jev-replay/compose, every one a task Jev drafts) are the fairest sample of what people
// type: on 50ed634d every one of them that is not phrased as a question drafts; on 74c214b5 "call the Qty column
// Quantity, stop it summing by default" (labels-fresh f-29: rename_object, then set_summarize_by) is routed instead.
// Live in lane i, Ctrl J answered "Nothing here matches those words yet" and drafted nothing. If the fix moves the
// decision out of isQuestion (for example, route first and draft when nothing matches), move these checks with it.
// RETIRED by fix-askdata-r4 (Kane, 27 Sep): Ctrl J no longer routes anything, so a task typed there cannot be routed
// instead of drafted. The labelled compose tasks and the three task phrasings are all drafted by the one path left.
check('(retired: Ctrl J routing is out of this release) Ctrl J drafts every labelled compose task and every task that says "by"', () => {
  const labelledTasks = ['labels.json', 'labels-fresh.json'].flatMap((f) =>
    JSON.parse(readFileSync(join(repoRoot, 'tools', 'jev-replay', 'compose', f), 'utf8')).items);
  assert.ok(labelledTasks.length >= 70, `the compose replay labels are where they were (${labelledTasks.length} read)`);
  ctrlJDraftsTheWords();
});

// ── 5. The consent card does not name the descriptions Jev now reads ─────────────────────────────────────
// Whenever query.summarize is asked at all, its first call sends every listed measure's and column's description with
// the words (JevQueryReader.cs Snapshot, withDescriptions: true; replayed on the 160 held-out questions, 96 of the
// requests carried descriptions such as Net Sales' "Invoiced sales after line discounts. Returns are negative lines,
// so they reduce net sales."). JevQueryReader.JevReads is true on this build (Kane kept Jev on, 28 Sep), so the card
// must be right now. No other decision point sends a
// description.
// The card's "What goes" names names, types, formats, folders, counts, relationships and typed words, and never
// descriptions, which are free text the model's authors wrote. The brief: nothing Jev reads may be left out of what
// the person agreed to.
const reader = readFileSync(join(repoRoot, 'Semanticus.Engine', 'Query', 'JevQueryReader.cs'), 'utf8');
check('the consent card names the descriptions that Jev reads with a data question', () => {
  assert.ok(/d\["description"\]\s*=/.test(reader), 'query.summarize sends measure and column descriptions (JevQueryReader.cs Snapshot)');
  const goes = /goes:\s*\[([\s\S]*?)\]/.exec(card);
  assert.ok(goes, 'the card has a "What goes" list');
  assert.ok(/description/i.test(goes[1]),
    'the card a person agrees to grants query.summarize:model_fields, which carries the descriptions of the measures and columns, and its "What goes" never names descriptions');
});

// ── 6. consent-card.test.mjs fails on a Windows checkout ────────────────────────────────────────────────
// CI runs npm test on windows-latest too (ci.yml build-and-smoke, matrix os), and .gitattributes is "* text=auto",
// so that checkout hands the sources CRLF (the crlf-fix incident, 14 Sep). The check 74c214b5 added to
// consent-card.test.mjs finds the grant with /const declared = ([\s\S]*?);\n/, and in consent.tsx with CRLF endings
// no ";" is followed by "\n", so it fails with "the card no longer builds the list it grants in one place the test can
// read" (measured: the whole tree converted to CRLF, that one file fails; 50ed634d's consent-card.test.mjs passes on
// the same CRLF tree). Here the test runs against a copy of exactly the files it reads, with Windows line endings.
check('consent-card.test.mjs passes on a checkout with Windows line endings, as the windows-latest CI leg has', () => {
  const scratch = mkdtempSync(join(tmpdir(), 'verify-askdata-crlf-'));
  try {
    const crlf = (from, to) => {
      mkdirSync(dirname(to), { recursive: true });
      writeFileSync(to, readFileSync(from, 'utf8').replace(/\r\n/g, '\n').replace(/\n/g, '\r\n'));
    };
    const vs = join(scratch, 'Semanticus.VSCode');
    crlf(join(root, 'test', 'consent-card.test.mjs'), join(vs, 'test', 'consent-card.test.mjs'));
    for (const f of readdirSync(join(webview, 'src'))) {
      if (/\.(tsx?|css)$/.test(f)) crlf(join(webview, 'src', f), join(vs, 'webview', 'src', f));
    }
    crlf(join(root, 'tools', 'uishot', 'consent-drive.mjs'), join(vs, 'tools', 'uishot', 'consent-drive.mjs'));
    crlf(join(repoRoot, 'Semanticus.Core', 'Decisions', 'DecisionRegistry.cs'), join(scratch, 'Semanticus.Core', 'Decisions', 'DecisionRegistry.cs'));
    const run = spawnSync(process.execPath, [join(vs, 'test', 'consent-card.test.mjs')], { encoding: 'utf8' });
    assert.equal(run.status, 0,
      'consent-card.test.mjs fails once its sources have Windows line endings, so npm test goes red on the windows-latest leg:\n'
      + ((run.stderr || '') + (run.stdout || '')).split('\n').filter((l) => /Assertion|ok|card/.test(l)).slice(0, 3).join('\n'));
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

// ── 7. The switched-off line sends the person to a place the UI does not have ─────────────────────────────
// With query.summarize switched off for a model, a typed question answers LocalEngine.Query.cs SwitchedOffNote: "Switch
// it on in the suggestion settings", and 74c214b5's help for the page says reading "can be switched off for a model in
// the suggestion settings". The Studio has no place of that name: the switch is in the panel "What Pro decides",
// opened from Your assistant > "Settings: what Pro decides" (decisions-settings.tsx TITLE, activity.tsx), and its row
// is labelled with the first sentence of the point's purpose. A person told to find "the suggestion settings" cannot.
const engineQuery = readFileSync(join(repoRoot, 'Semanticus.Engine', 'LocalEngine.Query.cs'), 'utf8');
const uiText = readdirSync(join(webview, 'src')).filter((f) => /\.tsx?$/.test(f) && f !== 'help.tsx').map((f) => src(f)).join('\n');
check('the switched-off line and the page help name the settings place the Studio really has', () => {
  const off = /SwitchedOffNote = "([^"]*)"/.exec(engineQuery);
  assert.ok(off, 'the engine has its switched-off line');
  const help = /Reading typed questions can be switched off[^"]*/.exec(src('help.tsx'))?.[0] ?? '';
  for (const [where, line] of [['the engine\'s switched-off line', off[1]], ['the page help', help]]) {
    const named = /in the ([a-z ]*settings)/i.exec(line)?.[1];
    if (!named) continue;
    assert.ok(new RegExp(named.replace(/^the /i, ''), 'i').test(uiText),
      `${where} sends the person to "${named}", and no place in the Studio is called that (the switch lives under "What Pro decides")`);
  }
});

// ════ Round 3 (verify-askdata-r3, 27 September 2026): m11/askdata fa4fb162 ════════════════════════════════════════
// Every check above passes on fa4fb162. The checks below FAIL there and are left failing on purpose.

// ── 8. Ctrl J still routes tasks that say "by" or "per", so they are lost ─────────────────────────────────────────
// The round 3 fix narrows isDataQuestion (a listed verb first, no I/we/it/please, no model-part word, "by default" is
// not a split), and both labelled sets pass. But a task needs none of those words to say "by" or "per". The verifier
// wrote 40 phrasings of its own and labelled them BEFORE any run (sha256 5376073525e45b36...): fa4fb162 routes 11 of
// its 20 tasks, 74c214b5 routed 18, and 50ed634d routed none. A routed task is lost: askbox.tsx AskBar.submit calls
// runAsk, and the reply offers only an Open button, never a draft, so Enter again routes again. The rule's own comment
// says "When in doubt the words draft: ... a routed task is lost". Each check below is one family of those tasks;
// every phrase in them drafts on 50ed634d. If the fix moves the decision out of isQuestion (for example, a routed
// reply that also offers to draft), move these checks with it.
// RETIRED by fix-askdata-r4 (Kane, 27 Sep: Ctrl J's question routing is out of this release). Ctrl J drafts every
// line now, so none of these families can be routed and lost; each check stands on the one draft path.
for (const [family, tasks] of [
  ['a column that should sort by another, said without a verb first', [
    'Month Name should sort by Month Number', 'Month name sorted by month number']],
  ['a change to a measure whose own name says "per" or "by"', [
    'Net Sales per Customer needs two decimal places', 'Profit per Unit is wrong when filtered by year',
    'Average Price per Unit ignores discounts, fix the formula', 'Speed up Net Sales by Region']],
  ['a listed verb after a word like Now, Next or Quick fix', [
    'Now sort the months by month number', 'Quick fix: rename Qty to Quantity by hand in Sales',
    'Next, translate the captions by language for the French report']],
  ['a model change whose verb is not on the list', [
    'divide gross profit by net sales and call that Margin %', 'restrict each sales manager to their own region by email',
    'Only managers should see cost by store']],
]) {
  check(`(retired: Ctrl J routing is out of this release) Ctrl J drafts ${family} (${tasks.length} phrasings)`, ctrlJDraftsTheWords);
}

// ════ Round 4 (verify-askdata-r4, targeted, 27 September 2026): m11/askdata f2f9996a ══════════════════════════════
// Every check above passes on f2f9996a. The checks below FAIL there and are left failing on purpose.

// ── 9. Ctrl J's command still says it asks about the model ────────────────────────────────────────────────────────
// Kane, 27 Sep: Ctrl J's question routing is out of this release. The box's placeholder became "Describe a task" so it
// no longer promises to answer a question (composer-contract.md 10.2, amended). But the command Ctrl J runs is still
// titled "Ask About This Model, or Describe a Task (Studio)" in package.json, and that title is what a person reads in
// the Command Palette and beside Ctrl J in Keyboard Shortcuts. The box it opens routes nothing and answers nothing.
check('Ctrl J\'s command title says what the box does now, and no longer offers to ask about the model', () => {
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
  const cmd = pkg.contributes.commands.find((c) => c.command === 'semanticus.studioAsk');
  assert.ok(cmd, 'the Ctrl J command is contributed');
  assert.doesNotMatch(cmd.title, /\bask\b/i,
    `the Command Palette and Keyboard Shortcuts show Ctrl J as "${cmd.title}", but the box it opens only drafts a task (placeholder "Describe a task")`);
});

// ── 10. A measure whose name holds a slash is titled with the wrong name and a false "edited formula" ────────────────
// checkTitle (askdata-model.ts) takes the measure's name as the LAST piece of its ref after a slash. A ref is
// measure:<table>/<name>, and the engine splits it at the FIRST slash (ObjectRefs.Split), because a measure's name may
// hold one ("Sales/Unit", "Units / Order"). So a check handed straight from an answer, with nothing edited, is titled
// "Unit with an edited formula is 12.5": the wrong name, and a formula nobody edited. On fa4fb162 the same draft was
// titled "Sales/Unit for Region Coast is 12.5".
check('a check handed from an answer on a measure whose name holds a slash keeps the whole name and claims no edit', () => {
  const M = load(join(webview, 'src', 'askdata-model.ts'));
  const a = {
    parts: { measures: ['Sales/Unit'], groupBy: ["'Store'[Region]"], filters: [] },
    columns: [{ name: 'Region', type: 'String' }, { name: 'Sales/Unit', type: 'Double' }],
    rows: [['Coast', 12.5]], dax: 'EVALUATE __Result', reading: 'Sales/Unit by Region',
  };
  const draft = M.checkDraftOf(a, 0, 0, [{ name: 'Sales/Unit', table: 'Sales' }]);
  assert.ok(draft, 'the row can be pinned, so there is a draft');
  assert.equal(JSON.parse(draft.paramsJson).expressionDax, 'CALCULATE ( [Sales/Unit], TREATAS ( { "Coast" }, \'Store\'[Region] ) )',
    'the check asks one row of Sales/Unit, exactly as handed');
  assert.doesNotMatch(draft.title, /edited formula/, `the unedited check is titled "${draft.title}"`);
  assert.ok(draft.title.startsWith('Sales/Unit '), `the title "${draft.title}" does not name the measure Sales/Unit`);
});

console.log(`\nverify-askdata: ${failures.length} of ${checks} checks failed`);
if (failures.length) process.exit(1);
