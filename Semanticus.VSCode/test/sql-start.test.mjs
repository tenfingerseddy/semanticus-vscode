// Starting a model from a SQL source (plan statement check, 28 Sep 2026, defects 4, 6 and 9).
//
// Kane, the same day: "we already add a sql endpoint to connections. This is what would be used. SQL endpoint uses
// the same entra sign in, no passwords need to be stored." So the wizard's SQL step reads the SQL endpoints the
// person saved on Connections and offers them FIRST, and typing an endpoint stays as the fallback. The draft call is
// the same autogenerateSpecFromFabric it always was, fed from the saved record: no new sign-in, nothing stored.
//
// What each part proves, because they are not equally strong:
//   1. THE SQL STEP. Rendered for real with react-dom/server from spec.tsx, with a saved endpoint and without one,
//      and read back as the words a person sees. The draft's arguments come from one exported pure function that is
//      executed here, so the saved record's server, database, sign-in and tenant are what goes on the wire.
//   2. THE ASK BAR. isPlaceAsk is executed: the words that name the new model wizard route instead of drafting.
//   3. THE WAYS IN. Read off the real source: Connections has From a SQL source beside New blank model, Find a tool
//      lists the Model Spec and the SQL start, and the one entry point (route tool 'spec', target SQL_DRAFT_TARGET)
//      is what all three use. The real clicks are proved in a browser by tools/uishot/sql-start-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    onSpecChange: () => () => {}, pickSpecFile: async () => null, onDidChange: () => () => {},
    navigateInStudio: () => {}, runHostCommand: () => {},
  },
  './daxeditor': { DaxField: () => null },
  './dax': { EMPTY_MODEL: {} },
  './advmodels': { FORMAT_PRESETS: [] },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
};
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') || file.endsWith('.js') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ')
  .trim();

const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');

// ===================================================================================================
// 1. The SQL step offers the endpoints saved on Connections first.
// ===================================================================================================
const S = load(join(webview, 'src', 'spec.tsx'));
assert.equal(typeof S.SqlSourceStep, 'function', 'the SQL step is its own component, exported so it is rendered here');
assert.equal(typeof S.sqlDraftArgs, 'function', 'the draft arguments come from one pure function');

const saved = { id: 'sq-1', name: 'Contoso warehouse', server: 'contoso.datawarehouse.fabric.microsoft.com', database: 'Sales', authMode: 'interactive', tenantId: 'contoso.com' };
const noop = () => {};
const withSaved = readable(renderToStaticMarkup(React.createElement(S.SqlSourceStep,
  { sources: [saved], busy: false, onDraft: noop, onAddOnConnections: noop })));
assert.match(withSaved, /Contoso warehouse/, 'the saved endpoint is listed by the name the person gave it');
assert.match(withSaved, /contoso\.datawarehouse\.fabric\.microsoft\.com/, 'with its address, so two alike are told apart');
assert.match(withSaved, /Sales/, 'and its database');
assert.match(withSaved, /Type an endpoint instead/, 'typing stays, as the fallback');
assert.doesNotMatch(withSaved, /Tenant \(optional\)/, 'the typed fields are not the first thing shown when one is saved');
assert.match(withSaved, /No password/i, 'it says out loud that nothing secret is kept');

const noneSaved = readable(renderToStaticMarkup(React.createElement(S.SqlSourceStep,
  { sources: [], busy: false, onDraft: noop, onAddOnConnections: noop })));
assert.match(noneSaved, /SQL endpoint/, 'with none saved the typed form is shown');
assert.match(noneSaved, /Connections/, 'and it points at Connections, where an endpoint is saved once');

const loading = readable(renderToStaticMarkup(React.createElement(S.SqlSourceStep,
  { sources: null, busy: false, onDraft: noop, onAddOnConnections: noop })));
assert.match(loading, /Reading/i, 'while the list is read it says so, rather than flashing the typed form');

// The saved record is what goes on the wire, positionally, tenant last and only when set.
assert.deepEqual(S.sqlDraftArgs({ server: ' a.example.com ', database: ' DW ', authMode: 'interactive', tenantId: 'contoso.com' }, 'import'),
  ['a.example.com', 'DW', 'interactive', 'import', 'human', 'contoso.com']);
assert.deepEqual(S.sqlDraftArgs({ server: 'a', database: 'b', authMode: '', tenantId: '' }, 'directLake'),
  ['a', 'b', 'azcli', 'directLake', 'human'], 'no sign-in named means the default, and no tenant is not sent');

// The draft's own explanations reach the person: why a link is off, and which table is the date table.
const specSrc = src('spec.tsx');
assert.match(specSrc, /r\.note/, 'a relationship row shows the note that says why it is drafted off');
assert.match(specSrc, /report\.notes/, 'the build report shows what the build chose on purpose');
assert.match(specSrc, /dateColumn/, 'the date table chip says when a source table is marked instead of a calendar added');
assert.match(specSrc, /no date table marked/, 'the date table chip says when the draft marked none, and dateTable.note says why');
assert.match(specSrc, /dateTable\.note/, 'the draft\'s reason for marking no date table reaches the person');

// ===================================================================================================
// 2. The ask bar: words that name the wizard are a route, not a task.
// ===================================================================================================
const C = load(join(webview, 'src', 'composer-model.ts'));
for (const words of ['model spec', 'Model Spec', 'build a model from scratch', 'build the model from my SQL database', 'create a new model from a SQL source', 'open the new model wizard'])
  assert.equal(C.isPlaceAsk(words), true, `"${words}" names the new model wizard`);
for (const words of ['add a measure for total sales', 'describe every table', 'build a date table', 'rename Sales to Revenue'])
  assert.equal(C.isPlaceAsk(words), false, `"${words}" is a task and still drafts`);
// SKIPPED in the m11 join (part 2, 30 Sep): the lane pinned askbox.tsx routing a place ask and an unsure ask the way it
// routed a question. Kane's ruling of 27 Sep (Ctrl J drafts every line and routes nothing) is later, and
// test/ask-box.test.mjs and test/verify-askdata.test.mjs hold it; the Plan's field asks the engine, whose AskMakeIntent is
// this same rule (the list checks below).
console.log('SKIP - the Ctrl J bar routes a place ask: Ctrl J routes nothing (Kane, 27 Sep)');

// The rule for "is this ask a request to make a model from a source" is ONE rule in two places: the engine's AskMakeIntent
// (whose word lists live in AskMakeIntent.cs and, for the making verbs, in AskRouteCatalog.MakingWords) and this file's
// classifyMakeIntent. Every list must be the same, word for word, so a word one door knows and the other does not cannot
// split them (round 7).
const catalogSrc = readFileSync(join(root, '..', 'Semanticus.Engine', 'Plan', 'AskRouteCatalog.cs'), 'utf8');
const intentSrc = readFileSync(join(root, '..', 'Semanticus.Engine', 'Plan', 'AskMakeIntent.cs'), 'utf8');
const listOf = (source, name) => {
  const m = new RegExp(name + String.raw`\s*=\s*new\[\]\s*\{([^}]*)\}`).exec(source);
  assert.ok(m, `the engine names ${name}`);
  return [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
};
assert.deepEqual(listOf(catalogSrc, 'MakingWords'), C.MAKING_WORDS, 'the engine and the ask bar carry the same making words');
for (const [engineName, list] of [['RequestWords', C.REQUEST_WORDS], ['ConvertWords', C.CONVERT_WORDS], ['PreVerbs', C.PRE_VERBS], ['LeadIns', C.LEAD_INS],
  ['PrepositionWords', C.PREPOSITION_WORDS], ['JoinerWords', C.JOINER_WORDS], ['HeadWords', C.HEAD_WORDS], ['SourceWords', C.SOURCE_WORDS],
  ['TailWords', C.TAIL_WORDS], ['ActionWords', C.ACTION_WORDS], ['ExistingWords', C.EXISTING_WORDS], ['QuestionStarts', C.QUESTION_STARTS],
  ['ObjectFollowers', C.OBJECT_FOLLOWERS]])
  assert.deepEqual(listOf(intentSrc, engineName), list, `the engine's ${engineName} and the ask bar's list are the same`);
assert.ok(!C.MAKING_WORDS.includes('new'), '"new" says which model, not that anyone should make one, so it is not a making verb; a bare "new model ..." is its own case');
for (const w of C.MAKING_WORDS)
  assert.equal(C.isPlaceAsk(`${w} a model from my sql database`), true, `"${w}" makes a model from SQL a place ask`);
for (const t of ['database', 'warehouse', 'endpoint', 'server', 'source tables', 'schema', 'metadata', 'connection'])
  assert.equal(C.isPlaceAsk(`build a model from my sql ${t}`), true, `"${t}" may follow "sql"`);
for (const s of C.SOURCE_WORDS)
  assert.equal(C.isPlaceAsk(`build a model from my ${s}`), true, `"${s}" names a source`);
for (const words of ['create a semantic model from my SQL database', 'set up a model from our sql warehouse', 'generate the model from the fabric sql endpoint'])
  assert.equal(C.isPlaceAsk(words), true, `"${words}" names the new model wizard`);
for (const words of ['refresh the model from the sql endpoint', 'update my model from the sql database', 'check the model from the sql source'])
  assert.equal(C.isPlaceAsk(words), false, `"${words}" is not a new model`);

// Round 5 (R2-M2, L1, L2, L4): the SAME list of asks the engine's route test reads, run through this door. The engine
// half is SqlDraftBuildTests.One_list_of_asks_routes_to_the_model_spec_exactly_when_a_making_word_governs_the_sql, which
// reads the same file, so the two doors are proved equal by the asks and not by comparing word lists.
const asks = JSON.parse(readFileSync(join(root, 'test', 'model-from-sql-asks.json'), 'utf8')).asks;
assert.ok(asks.length >= 30 && asks.some((a) => a.place) && asks.some((a) => !a.place), 'the shared list has both kinds of ask');
for (const { ask, place, intent } of asks) {
  assert.equal(C.isPlaceAsk(ask), place, `"${ask}" ${place ? 'names the new model wizard' : 'is not a new model'}`);
  assert.equal(C.classifyMakeIntent(ask), intent, `"${ask}" reads as ${intent}`);
  assert.equal(C.isMakeUnsure(ask), intent === 'unsure', `"${ask}" ${intent === 'unsure' ? 'goes to the engine to be asked about' : 'is not one the service is asked about'}`);
}

// The m11 join, part 2, item 7 (30 Sep): the verifier's round 7 attack list, the same file the engine's
// AskMakeIntentFixR7Tests reads. At 0ad1c1be 15 of its 40 asks about a model that exists read as a request to make one.
{
  const r7 = JSON.parse(readFileSync(join(root, 'test', 'sql-start-r7-asks.json'), 'utf8')).asks;
  assert.equal(r7.length, 56, 'the round 7 list: 40 existing, 8 make, 8 guards');
  const wrong = r7.filter(({ ask, opens }) => (C.classifyMakeIntent(ask) === 'yes') !== opens || C.isPlaceAsk(ask) !== opens)
    .map(({ id, ask, opens }) => `${id} "${ask}" (want ${opens ? 'the Model Spec' : 'not the Model Spec'}, words read ${C.classifyMakeIntent(ask)})`);
  assert.deepEqual(wrong, [], `${wrong.length} wrong on the round 7 list`);
}

// ===================================================================================================
// 3. The ways in, and the one entry point they share.
// ===================================================================================================
const routeSrc = src('route.ts');
assert.match(routeSrc, /export const SQL_DRAFT_TARGET = 'start:sql'/, 'one named target opens the wizard on its SQL step');
assert.match(routeSrc, /'specStart'/, 'it travels as a one-use seed');

const appSrc = src('App.tsx');
assert.match(appSrc, /tool === 'spec' && target === SQL_DRAFT_TARGET/, 'the shell turns the target into the seed');
assert.match(appSrc, /FIND_A_TOOL_EXTRA/, 'Find a tool carries the places that are views inside the Plan');
assert.match(appSrc, /label: 'Model Spec'/, 'Find a tool lists the Model Spec');
assert.match(appSrc, /label: 'From a SQL source'/, 'and the SQL start');
assert.match(appSrc, /keepMenuOnScreen/, 'the Find a tool menu is kept inside the window (check item 9: cut off at 1440 wide)');

const planSrc = src('plan.tsx');
assert.match(planSrc, /startSql=/, 'the Plan hands the start to the draft panel');

const hubSrc = src('connectionshub.tsx');
assert.match(hubSrc, /data-testid="hub-add-sql"/, 'Connections has From a SQL source');
const adds = hubSrc.slice(hubSrc.indexOf('hub-add-blank'), hubSrc.indexOf('hub-add-blank') + 400);
assert.match(adds, /hub-add-sql/, 'right beside New blank model');
// Since the M11 wiring step it opens the start plan's own form there, with no blank-model name step first (the form
// names the model and the start makes it): test/startplan-wiring.test.mjs holds that, and sql-start-drive.mjs drives it.
assert.match(hubSrc, /<StartFromSqlForm\b/, 'and it shows the start plan\'s own form');

const extSrc = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.match(extSrc, /'semanticus\.draftFromSqlSource'/, 'the host has the command a panel without Studio uses');
assert.match(extSrc, /navigateStudio\(context, 'spec', 'start:sql'\)/, 'and it opens the same entry point');

// ===================================================================================================
// 4. Review-fix round 2 (verifier, 28 Sep).
// ===================================================================================================
// M3. A failed read of the saved endpoints is not "none saved". The read's mapping is executed here with the
// Connections call made to fail, and the step is rendered in that state.
STUBS['./connectionshub'].listSqlSources = async () => { throw new Error('the engine did not answer'); };
assert.equal(typeof S.readSavedSqlSources, 'function', 'the SQL step reads the saved endpoints through one exported function');
assert.equal(await S.readSavedSqlSources(), 'failed', 'a failed read is reported as failed, never as an empty list');
STUBS['./connectionshub'].listSqlSources = async () => [];
assert.deepEqual(await S.readSavedSqlSources(), [], 'an answer with none saved is still an empty list');
const failed = readable(renderToStaticMarkup(React.createElement(S.SqlSourceStep,
  { sources: 'failed', busy: false, onDraft: noop, onAddOnConnections: noop, onRetry: noop })));
assert.doesNotMatch(failed, /No SQL endpoint is saved/, 'a failed read does not say that none is saved');
assert.match(failed, /could not be read/, 'it says the list could not be read');
assert.match(failed, /try again/, 'and offers to read it again');
assert.match(failed, /SQL endpoint/, 'with the typed form still there');

// L2. One endpoint, one name for its sign-in: the step uses the words Connections shows (copy.ts SIGN_IN_MODES).
assert.match(withSaved, /Sign in in a browser/, 'a saved endpoint shows the sign-in by the name Connections gives it');
assert.doesNotMatch(withSaved, /Microsoft sign-in/, 'not a second name of its own');
const typedForm = renderToStaticMarkup(React.createElement(S.SqlSourceStep, { sources: [], busy: false, onDraft: noop, onAddOnConnections: noop }));
for (const label of ['Sign in in a browser', 'Use a code on another device', 'Use the Azure command line'])
  assert.ok(typedForm.includes(label), `the typed form offers "${label}"`);
assert.doesNotMatch(typedForm, /Interactive sign-in|>Device code</, 'and none of its old names');

// H1. From a SQL source on Connections creates a model, which is a NEW session. The move to the SQL step is stamped
// with that session, and Studio holds a move for a session it has not read yet instead of dropping it. The real
// session swap is driven in a browser by tools/uishot/sql-start-swap-drive.mjs and in VS Code by the lane's proof.
const bridgeSrc = src('bridge.ts');
assert.match(bridgeSrc, /export function navigateInStudio\(tab: string, target\?: string, sessionId\?: string\)/, 'a move inside Studio can name its session');
assert.match(appSrc, /heldNav\.current = m/, 'Studio holds a move stamped for a session it has not read yet');
assert.match(appSrc, /held\.sessionId === sid\) \{ heldNav\.current = undefined; dispatchNavigate\(held\)/, 'and replays it when that session arrives');
assert.match(hubSrc, /navigateInStudio\('plan', undefined, s\?\.sessionId\)/, 'the hub stamps the move to the Plan with the new session');
const started = hubSrc.slice(hubSrc.indexOf('const startedFromSql'), hubSrc.indexOf('const startedFromSql') + 900);
assert.ok(started.indexOf("runHostCommand('semanticus.openStudio')") > 0 && started.indexOf("runHostCommand('semanticus.openStudio')") < started.indexOf('onClose();'),
  'standalone, the host is asked before the panel closes: a message after closePanel never arrives');

// L3. The palette command with no model open starts on Connections' From a SQL source form, not "Open a model".
assert.match(extSrc, /currentSessionId\s*\?\s*navigateStudio\(context, 'spec', 'start:sql'\) : void openConnectionsManager\('createsql'\)/, 'no model open: Connections, on the SQL start');
assert.match(hubSrc, /if \(section === 'createsql'\) openStartFromSql\(\)/, 'and the hub opens that form when asked');
// Plan statement check 4, defect 6: the panel opened on From a SQL source showed the New blank model card above the start form, because the card's first state
// was open for 'createsql' as well as for 'create'. Only 'create' opens the card at mount.
assert.match(hubSrc, /const \[createOpen, setCreateOpen\] = useState\(initialView === 'create'\);/, 'the blank-model card opens at mount for the blank-model start only');
assert.doesNotMatch(hubSrc, /useState\(initialView === 'createsql' \|\| initialView === 'create'\)/, 'and not for the SQL start');
assert.match(src('main.tsx'), /'createsql', 'create'\] as const/, 'including when the standalone panel is opened on it');

console.log('sql-start: all checks passed');
