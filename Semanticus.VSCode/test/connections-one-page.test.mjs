// Connections, slice C, lane C2: the one-page hub (contract 19.1; design connections-minimal.html, Kane 24 Sep).
//
// This file proves the parts that are rules and words:
//   1. The page's rules in connectionshub-model.ts, executed. The one that matters most: a model that cannot do a
//      job is struck out with the ENGINE's reason, and the page never works a reason out for itself.
//   2. The drawn pieces in connectionshub-parts.tsx, rendered with react-dom/server and read as a person reads them:
//      the Now strip, the Change panel with a struck-out model, the make-default count said before anything moves.
//   3. The wiring, read off connectionshub.tsx: which engine calls the page makes, that the SQL sources section is
//      gone, and that every "cannot go" item of contract 19.1 is still on the page.
// The clicks are driven in a real browser by tools/uishot/c2-hub-drive.mjs.
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

const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|disabled|aria-[\w-]+|title|placeholder|autoComplete)="[^"]*"/gi, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ').trim();

const modelPath = join(webview, 'src', 'connectionshub-model.ts');
const partsPath = join(webview, 'src', 'connectionshub-parts.tsx');
assert.ok(existsSync(modelPath), 'the page rules live in connectionshub-model.ts so they can be run');
assert.ok(existsSync(partsPath), 'the drawn pieces live in connectionshub-parts.tsx so they can be rendered');
const M = load(modelPath);
const P = load(partsPath);
const hub = readFileSync(join(webview, 'src', 'connectionshub.tsx'), 'utf8');
const h = React.createElement;
const draw = (C, props) => renderToStaticMarkup(h(C, props));
const noop = () => {};

// ===================================================================================================
// 1. The rules, executed
// ===================================================================================================

// The engine's sentences, as list_connections carries them. The page must show exactly these.
const FILE_NO = 'A file on this computer cannot answer queries. Open it, then choose Power BI Desktop or a workspace model for tests and queries.';
const PUB_NO = 'Publish needs a workspace model. Power BI Desktop and files on this computer cannot be published to.';
const recs = [
  { id: 'r-file', kind: 'file', endpoint: 'C:\\Models\\Contoso Retail', modelName: 'Contoso Retail', testsRefusal: FILE_NO, publishRefusal: PUB_NO },
  { id: 'r-desk', kind: 'localDesktop', endpoint: 'localhost:51000', modelName: 'Contoso local test', publishRefusal: PUB_NO },
  { id: 'r-ws', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Sales', database: 'Contoso Sales', modelName: 'Contoso Sales' },
];

// The reason is the engine's, word for word, and Editing takes any model.
assert.equal(M.jobRefusal(recs[0], 'tests'), FILE_NO, 'a struck-out model shows the engine sentence it was listed with');
assert.equal(M.jobRefusal(recs[1], 'publish'), PUB_NO);
assert.equal(M.jobRefusal(recs[0], 'edit'), null, 'Editing takes any model');
assert.equal(M.jobRefusal(recs[2], 'publish'), null);
// The page does not re-decide the rule. A file record the engine listed WITHOUT a refusal is not struck out here,
// even though today's engine would refuse it: the rule is the engine's, so the page takes its word for it.
assert.equal(M.jobRefusal({ id: 'x', kind: 'file', endpoint: 'C:\\x' }, 'tests'), null,
  'the page never invents a reason: no engine refusal, no strike');
assert.equal(M.jobRefusal({ id: 'x', kind: 'localDesktop', endpoint: 'localhost:1' }, 'publish'), null,
  'the page never invents a publish rule either');
assert.equal(M.jobRefusal({ id: 'x', kind: 'xmla', endpoint: 'e', testsRefusal: '   ' }, 'tests'), null, 'a blank refusal is no refusal');

// Every model is listed in every picker; the refused ones carry the reason instead of the kind word.
const nameOf = (r) => r.modelName;
const testsOpts = M.jobOptions(recs, 'tests', 'r-desk', nameOf, M.kindWord);
assert.equal(testsOpts.length, 3, 'a model that cannot do a job is struck out, never hidden');
assert.deepEqual(testsOpts.map((o) => [o.id, o.refusal, o.current]),
  [['r-file', FILE_NO, false], ['r-desk', null, true], ['r-ws', null, false]]);
assert.equal(testsOpts[0].detail, FILE_NO);
assert.equal(testsOpts[1].detail, 'Power BI Desktop');
assert.equal(testsOpts[2].detail, 'workspace model');

// The Now strip left the hub (r1 min4, C1/C2): each row names its own jobs, and a Jobs button opens the picker panel.
const side = (id, name, extra = {}) => ({ available: true, connectionId: id, modelName: name, live: true, ...extra });
const nm = (s) => s.modelName;
assert.equal(M.nowLine, undefined, 'the strip\'s one-line rule left with the strip');

// A row names the jobs it holds, and its SQL endpoint.
const ctx = { editing: { available: true, sourceConnectionId: 'r-file', live: false }, querying: side('r-desk', 'x'), publishing: side('r-ws', 'y') };
assert.deepEqual(M.rowJobs(recs[0], ctx), ['editing']);
assert.deepEqual(M.rowJobs(recs[1], ctx), ['tests and queries']);
assert.deepEqual(M.rowJobs(recs[2], ctx), ['publish target']);
assert.equal(M.rowSecondLine('C:\\Models\\Contoso Retail', ['editing'], 'Contoso Retail warehouse'),
  'C:\\Models\\Contoso Retail · editing · SQL endpoint: Contoso Retail warehouse');
assert.equal(M.rowSecondLine('In Contoso Sales', [], null), 'In Contoso Sales');

// The endpoint comes from the engine's list, by saved-model id.
const list = { rows: [{ connectionId: 'r-file', sqlEndpoint: { sourceId: 's1', name: 'Contoso Retail warehouse' } }, { connectionId: 'r-ws', sqlEndpoint: null }] };
assert.equal(M.endpointFor(list, 'r-file').name, 'Contoso Retail warehouse');
assert.equal(M.endpointFor(list, 'r-ws'), null);
assert.equal(M.endpointFor(null, 'r-ws'), null);

// Remove on a not-linked source only when the engine COUNTED nothing across EVERY saved model (Codex on pull request
// 394, F-265). An uncounted number is not a zero, and neither is a zero that only covers the open model.
const ALL = 'savedModels';
assert.equal(M.notLinkedRemovable({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0, usageScope: ALL }), true);
assert.equal(M.notLinkedRemovable({ id: 's', name: 's', checksUsing: 1, tableMappingsUsing: 0, usageScope: ALL }), false);
assert.equal(M.notLinkedRemovable({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0 }), false,
  'a zero the engine did not say covers every saved model keeps Remove shut');
assert.equal(M.notLinkedRemovable({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0, usageScope: 'openModel' }), false);
assert.equal(M.notLinkedRemovable({ id: 's', name: 's', usageScope: ALL }), false, 'a count the engine did not carry keeps Remove shut');
// What the row says it counted, and what the confirmation claims, come from the same counts and the same scope.
assert.equal(M.notLinkedUses({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0, usageScope: ALL }),
  'No saved model uses it');
assert.equal(M.notLinkedUses({ id: 's', name: 's', checksUsing: 2, tableMappingsUsing: 1, usageScope: ALL, usingModels: ['Contoso Retail', 'Contoso Sales'] }),
  '2 checks and 1 table mapping in Contoso Retail, Contoso Sales use it');
assert.equal(M.notLinkedUses({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0 }), 'What uses it was not counted');
assert.equal(M.removeConfirmSentence({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0, usageScope: ALL }),
  'No saved model uses it as its SQL endpoint, and no check or table mapping in any saved model points at it. A check you point at it later would have to be set up again.');
assert.equal(M.removeConfirmSentence({ id: 's', name: 's', checksUsing: 0, tableMappingsUsing: 0 }), null,
  'no sentence claims nothing uses it unless every saved model was counted');
assert.equal(M.removeConfirmSentence({ id: 's', name: 's', checksUsing: 1, tableMappingsUsing: 0, usageScope: ALL }), null);

// Make default says how many models move, from the saved list, before anything moves.
const family = (m) => (!m || m === 'interactive' ? 'interactive' : m === 'devicecode' ? 'devicecode' : null);
const saved = [
  { id: 'a', kind: 'xmla', endpoint: 'e1', tenantId: 'contoso.com', authMode: 'interactive' },
  { id: 'b', kind: 'xmla', endpoint: 'e2', tenantId: 'Contoso.com', authMode: 'interactive' },
  { id: 'c', kind: 'xmla', endpoint: 'e3', tenantId: 'contoso.com', authMode: 'serviceprincipal' },
  { id: 'd', kind: 'xmla', endpoint: 'e4', tenantId: 'fabrikam.com', authMode: 'interactive' },
  { id: 'e', kind: 'file', endpoint: 'C:\\x' },
];
assert.equal(M.defaultMoveCount(saved, 'contoso.com', 'interactive', family), 2);
assert.equal(M.defaultMoveSentence(2, 'contoso.com'), '2 saved models on the contoso.com tenant will open with it from now on.');
assert.equal(M.defaultMoveSentence(1, undefined), '1 saved model on this sign-in with no tenant recorded will open with it from now on.');

// The old section names still land somewhere real on the one page.
assert.deepEqual(M.routeSection('setup'), { view: 'open', panel: 'change' }, "Deploy's Change publish destination opens the Change panel");
assert.deepEqual(M.routeSection('accounts'), { view: 'open', panel: 'account' });
assert.deepEqual(M.routeSection('sqlsources'), { view: 'open', panel: 'sql' }, "the Tests page's way in opens the open model's SQL endpoint");
assert.deepEqual(M.routeSection('history'), { view: 'history', panel: 'none' });
assert.deepEqual(M.routeSection('open'), { view: 'open', panel: 'none' });

// ===================================================================================================
// 2. The pieces, drawn
// ===================================================================================================

// The Now strip is gone from the page: no strip component, no Disconnect that only dropped tests and queries (C1, C2).
assert.equal(P.NowStrip, undefined, 'no Now strip component');

// The Tests picker, open: the file is there, struck out, cannot be chosen, and shows the engine's sentence.
{
  const opts = M.jobOptions(recs, 'tests', 'r-ws', nameOf, M.kindWord);
  const html = draw(P.JobPicker, { job: 'tests', current: 'Contoso Sales', options: opts, open: true, onToggle: noop, onChoose: noop });
  const options = [...html.matchAll(/<button[^>]*role="option"[^>]*>[\s\S]*?<\/button>/g)].map((m) => m[0]);
  assert.equal(options.length, 3, 'every saved model is in the picker');
  const file = options.find((o) => o.includes('Contoso Retail'));
  assert.match(file, /aria-disabled="true"/);
  assert.match(file, /disabled=""/, 'a struck-out model cannot be chosen');
  assert.match(file, /class="hub-job-struck"/, 'it is struck out');
  assert.ok(readable(file).includes(FILE_NO), "and the reason shown is the engine's own sentence");
  const ws = options.find((o) => o.includes('Contoso Sales'));
  assert.match(ws, /aria-selected="true"/);
  assert.doesNotMatch(ws, /aria-disabled="true"/);
}
// Closed, it shows only the current choice.
assert.doesNotMatch(draw(P.JobPicker, { job: 'publish', current: 'Not set', options: [], open: false, onToggle: noop, onChoose: noop }), /role="listbox"/);

// The Change panel: the three jobs, each with its one-line meaning, and the promise that nothing publishes.
{
  const html = draw(P.ChangePanel, { rows: M.JOBS.map((j) => ({ job: j.job, current: 'x', picker: h('span', null, 'picker') })), onDone: noop, note: 'Contoso Retail no longer has a publish destination. Nothing was deployed or deleted.' });
  const text = readable(html);
  assert.match(text, /Which model does which job Done/);
  for (const j of M.JOBS) assert.ok(text.includes(`${j.label} ${j.help}`), `the ${j.label} job says what it means`);
  assert.doesNotMatch(text, /Changing these never publishes anything/, 'the footer reassurance left: Publish to already says a review comes first (C11)');
  assert.match(text, /no longer has a publish destination/, "the engine's note after a change is shown");
}

// Make default: the count comes first, and the button that moves them is a second press.
{
  const html = draw(P.DefaultConfirm, { username: 'admin@contoso.com', sentence: M.defaultMoveSentence(3, 'contoso.com'), onCancel: noop, onConfirm: noop });
  assert.match(readable(html), /^Make admin@contoso\.com the default\? 3 saved models on the contoso\.com tenant will open with it from now on\. Each switch is kept in History\. Cancel Make default$/);
}

// Not linked to a model: Link and Remove, with Remove shut while something still uses the source.
{
  const html = draw(P.NotLinkedList, {
    items: [{ id: 's1', name: 'Old warehouse', server: 'old.database.windows.net', database: 'W', checksUsing: 0, tableMappingsUsing: 0, usageScope: 'savedModels' },
      { id: 's2', name: 'Busy warehouse', server: 'busy.database.windows.net', database: 'B', checksUsing: 2, tableMappingsUsing: 1, usageScope: 'savedModels', usingModels: ['Contoso Retail'] },
      { id: 's3', name: 'Uncounted warehouse', server: 'u.database.windows.net', database: 'U', checksUsing: 0, tableMappingsUsing: 0 }],
    linkTargets: [{ id: 'r-ws', name: 'Contoso Sales' }], onLink: noop, onRemove: noop });
  const text = readable(html);
  assert.match(text, /^Not linked to a model/);
  assert.match(text, /Old warehouse old\.database\.windows\.net · W · No saved model uses it/);
  assert.match(text, /Busy warehouse busy\.database\.windows\.net · B · 2 checks and 1 table mapping in Contoso Retail use it/);
  assert.match(text, /Uncounted warehouse u\.database\.windows\.net · U · What uses it was not counted/);
  const rows = html.split('data-testid="hub-notlinked-row"').slice(1);
  assert.doesNotMatch(rows[0].match(/<button[^>]*hub-notlinked-remove[^>]*>/)[0], /disabled/);
  assert.match(rows[1].match(/<button[^>]*hub-notlinked-remove[^>]*>/)[0], /disabled/, 'a source in use cannot be removed from here');
  assert.match(rows[2].match(/<button[^>]*hub-notlinked-remove[^>]*>/)[0], /disabled/, 'a zero that does not cover every saved model cannot be removed from here');
  assert.match(html, /<option value="r-ws">Contoso Sales<\/option>/, 'Link offers the saved models');
}
assert.equal(draw(P.NotLinkedList, { items: [], linkTargets: [], onLink: noop, onRemove: noop }), '', 'no section when every source is linked');

// ===================================================================================================
// 3. The wiring, and every "cannot go" item of contract 19.1
// ===================================================================================================
const has = (re, why) => assert.match(hub, re, why);
const hasNot = (re, why) => assert.doesNotMatch(hub, re, why);

// One page: no section rail, no SQL sources section, no Current setup or Accounts view.
hasNot(/className="hub-nav"/, 'the five-section rail is gone');
hasNot(/function SqlSourcesView|navBtn\(|function SetupView|function AccountsView|function DetailPane/, 'the old sections and the detail panel are gone');
hasNot(/>SQL sources</, 'no screen shows a separate SQL sources section');
hasNot(/<NowStrip\b/, 'the Now strip is not on the page');
has(/data-testid="hub-now-change"[\s\S]{0,260}>Jobs</, 'a Jobs button in the header opens the Change panel');
has(/data-testid="hub-job-tests-off"[\s\S]{0,160}None: turn tests and queries off/, 'turning tests and queries off lives in the Tests picker');
has(/<ChangePanel\b/, 'the Change panel is on the page');
has(/<NotLinkedList\b/, 'the not-linked sources are on the page');

// The page does not re-decide a job rule: the old in-page file rule is gone, and pickers read jobOptions.
hasNot(/A local file cannot answer queries/, 'the Tests rule is the engine\'s now, not a sentence in the page');
has(/options=\{jobOptions\(records, job, /, 'every job picker lists its options through jobOptions');
for (const job of ['edit', 'tests', 'publish']) has(new RegExp(`pick\\('${job}', `), `the Change panel has the ${job} picker`);

// The engine calls behind each job.
has(/rpc\('setPublishDestination', r\.id, 'human'\)/, 'Publish here sets the destination');
has(/rpc<[^>]+>\('setPublishDestination', null, 'human', true\)/, '"Not set" clears it through the new clear');
has(/const \{[^}]*\bdisconnect\b[^}]*\} = useConnection\(\)/, 'Disconnect uses the existing disconnect');
has(/onClick=\{\(\) => void disconnectTests\(\)\}[\s\S]{0,80}None: turn tests and queries off/, "the Tests picker still lets go of tests and queries");
has(/Turn tests and queries off<\/MenuItem>/, 'the row that holds tests and queries has the same off switch: Disconnect stays reachable from the row (min2 removed the connect bar one)');
hasNot(/>Used for tests and queries</, 'the disabled "Used for tests and queries" row is an action now');
has(/rpc<ModelEndpointList>\('listModelSqlEndpoints'\)/, 'each row reads its SQL endpoint from the engine');
has(/rpc\('setModelSqlEndpoint', [^)]*, 'human'\)/, 'the row menu sets a model\'s SQL endpoint');
has(/rpc\('setModelSqlEndpoint', null, /, 'and can clear it');

// The row menu: the jobs, a local copy, the SQL endpoint, another account, label, history, forget.
for (const item of ['Use for tests and queries', 'Publish here', 'Work on a local copy', 'Add SQL endpoint', 'Open as another account', 'Environment label', 'History', 'Forget']) {
  assert.ok(hub.includes(`>${item}<`) || hub.includes(`'${item}'`), `the row menu offers ${item}`);
}
hasNot(/>Use for tests</, 'the copy rules keep "tests" to its role sense: "Use for tests and queries"');

// Three ways to add a model, and the pickers from lane C3 mounted.
has(/>Open file or folder</); has(/>Power BI Desktop</); has(/>Workspace model</); has(/>New blank model</);
has(/<DesktopPicker /); has(/<WorkspacePicker /);

// ---- contract 19.1, "What cannot go", one line each ----
// (1) The environment label, person-only, in the row menu; and the add form asks for it once.
has(/data-testid="hub-env-select"/, 'the environment label is still set from the page');
has(/Sets what your assistant may do on this model\./, 'the label note says what the label does, once');
has(/function AddView[\s\S]*?<label className="sem-field-label">Environment/, 'the pasted-address add form asks for the label once');
// (2) Choosing an account for one open is one menu item.
assert.equal((hub.match(/>Open as another account</g) || []).length, 1, 'one menu item, not three buttons');
has(/function AccountDialog/, 'and it opens the per-open account choice');
// (3) Make default says how many models it moves before it moves them.
has(/setPendingDefault\(p\)/, 'Make default asks first');
assert.equal((hub.match(/makeDefaultFor\(/g) || []).length, 1, 'makeDefaultFor is called from one place only: the confirmation that says the count');
has(/<DefaultConfirm [\s\S]{0,600}?onConfirm=\{\(\) => void makeDefaultFor\(pendingDefault\)\}/);
has(/defaultMoveSentence\(defaultMoveCount\(records, /, 'the confirmation carries the count');
// (4) The unsaved-changes check before opening another model, from a row AND from the Change panel.
has(/if \(s\?\.hasUnsavedChanges && !openConfirm\) \{ setPendingOpen\(r\); setOpenConfirm\(true\);/);
has(/data-testid="hub-open-unsaved"/);
has(/job: 'edit'[\s\S]{0,400}?openModel\(/, 'choosing Editing in the Change panel goes through openModel, so it asks first');
// (5) Work on a local copy keeps its own short form.
has(/function WorkView/); has(/prepareWorkingCopy/);
// (6) The service identity setup check sits behind "Other ways to sign in".
has(/function AuthPreflight/); has(/probeAuthPrerequisites/);

// ---- drop 14: a plain local model is never said to be in source control, and no folder is guessed ----
hasNot(/source control/i, 'the page makes no source-control claim at all');
hasNot(/repositoryRoot/, 'and never shows the repository folder as the model folder');


// ===================================================================================================
// 4. Assistant permissions says each thing once (r1 min4, P1 to P11). Labels are set in Connections only.
// ===================================================================================================
{
  const src = join(webview, 'src');
  // Only the seams permissions.tsx touches are stubbed: the bridge, the connection hook and the shared panel pieces.
  const Passthrough = ({ children }) => h('div', null, children);
  cache.set(join(src, 'bridge.ts'), { rpc: async () => { throw new Error('no engine in this test'); }, onReconnect: () => () => {} });
  cache.set(join(src, 'connection.tsx'), { useConnection: () => ({ openConnections: noop }) });
  cache.set(join(src, 'workflows.tsx'), { Panel: Passthrough, Banner: Passthrough, SectionTitle: Passthrough, Pill: Passthrough });
  const Q = load(join(src, 'permissions.tsx'));
  const permsSrc = readFileSync(join(src, 'permissions.tsx'), 'utf8');

  const standard = { enabled: true, preset: 'standard', matrix: { EditLocal: { local: 'allow', dev: 'allow', uat: 'allow', prod: 'allow' }, DeployFile: { local: 'allow', dev: 'allow', uat: 'allow', prod: 'allow' } } };
  const locked = { enabled: true, preset: 'locked', matrix: { EditLocal: { local: 'ask', dev: 'ask', uat: 'ask', prod: 'ask' }, DeployFile: { local: 'ask', dev: 'ask', uat: 'ask', prod: 'ask' } } };
  assert.equal(Q.fixedLine(standard), 'Always allowed: read and check the model, edit the working model, write model files. Never: change these settings.');
  assert.match(Q.fixedLine(locked), /Editing the working model and writing model files ask first/, 'under Locked the local rows are not "always allowed", and the line does not say they are');
  assert.equal(Q.fixedLine({ ...standard, enabled: false }), Q.fixedLine(standard), 'with the guardrail off the fixed rules still stand');

  assert.equal(Q.presetLine({ name: 'Standard', desc: 'Asks before touching UAT or production.' }), 'Standard: asks before touching UAT or production.');

  // One word per label, the Connections words.
  assert.deepEqual(Q.LABEL_WORD, { local: 'Local', dev: 'Development', uat: 'UAT', prod: 'Production' });
  assert.equal(Q.unlabelledCount(null), null, 'unread is not zero');
  const conns = [{ kind: 'xmla', label: 'prod' }, { kind: 'xmla', label: null }, { kind: 'xmla' }, { kind: 'localDesktop' }, { kind: 'file', label: '' }, { kind: 'xmla', label: 'uat' }];
  assert.equal(Q.unlabelledCount(conns), 2, 'only a published model with no label counts: a local model is Local, as the Connections list says');
  assert.equal(Q.labelsLine(0), 'Labels are set on each model in Connections.');
  assert.equal(Q.labelsLine(1), 'Labels are set on each model in Connections. 1 model has no label, so it gets the Production settings.');
  assert.equal(Q.labelsLine(3), 'Labels are set on each model in Connections. 3 models have no label, so they get the Production settings.');

  // The page: no Targets list, no second disclaimer, no preset cards, no "always allowed" rows.
  assert.doesNotMatch(permsSrc, /TargetsPanel|LabelPicker|ConfirmButton|labelConnection|forgetConnection/, 'labels and Forget live in Connections only');
  assert.doesNotMatch(permsSrc, /Free · always available|A preset sets a sensible baseline|prevents accidents; it is not a security boundary|Columns are your target labels/);
  assert.equal((permsSrc.match(/not a security boundary/g) || []).length, 1, 'the honesty line is said once');
  assert.match(permsSrc, /This stops accidents\. It is not a security boundary: the sign-in you give your assistant is\./);
  assert.match(permsSrc, /Approving also allows other row previews on this target for/, 'the 15-minute scope stays, shorter');
  assert.doesNotMatch(permsSrc, /This approval has expired/, 'an expired approval has no action and is not listed');
  assert.match(permsSrc, /className="sem-seg self-start" role="group" aria-label="Preset"/, 'five presets are one segmented control');
  assert.doesNotMatch(permsSrc, /uppercase tracking-wide/, 'column heads are sentence case');
}

console.log('connections-one-page: rules, pieces and wiring hold.');
