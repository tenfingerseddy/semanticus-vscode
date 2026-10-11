// Workflows as one page (lane W2, Plan-heart contract 21; absorbs composer L7 and the Runs retirement R3).
//
// Rendered for real with react-dom/server over the page's own components, with the host seams stubbed. What a
// person can read is checked; the clicks and the calls they make are driven in a real browser by
// tools/uishot/workflows-drive.mjs, which records every call in results.json.
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

const noop = () => () => {};
const STUBS = {
  './bridge': new Proxy({
    rpc: async () => { throw new Error('the test loader answers no calls; drive the app for that'); },
    copyText: async () => {},
  }, { get: (t, k) => (k in t ? t[k] : noop) }),
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.endsWith('.css')) return {};
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&#x27;/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const render = (el) => readable(renderToStaticMarkup(el));

let passed = 0;
const test = (name, fn) => { try { fn(); passed++; } catch (e) { console.error(`not ok - ${name}`); throw e; } };

const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const repoFile = (f) => readFileSync(join(root, '..', f), 'utf8');
const M = load(join(webview, 'src', 'workflows-model.ts'));
const W = load(join(webview, 'src', 'workflows.tsx'));
const R = load(join(webview, 'src', 'workflow-rules.tsx'));
const D = load(join(webview, 'src', 'workflowdesign.tsx'));
const h = React.createElement;

// ---- 21.4: the false copy is a bug -------------------------------------------------------------------
// The switch only relaxes the checks inside runs. Actions a team rule requires still go through their workflow
// (LocalEngine.Workflows.cs, the enforcement note). The off state must never say bindings stop. This test ran red
// on the old banner ("Edits are not checked against required workflows until you turn them back on") before the
// copy was fixed; the banner itself then went with the rail, and the one off state left is Team rules' switch.
const TRUE_OFF = 'Checks inside runs are off. Actions a team rule requires still go through their workflow.';
const FALSE_CLAIM = /not checked against required workflows|required workflows are off|skips? the checks for new runs, so/i;

test('the off state says what is true: checks inside runs are off, required actions still go through', () => {
  const faces = [
    render(h(R.ChecksSwitch, { enforced: false, showOffLine: true, onSet: () => {} })),
    render(h(R.TeamRulesDrawer, { library: [], policy: { workflows: [], bindings: [], lints: [] }, enforced: false, profiles: [], isPro: true,
      onClose: () => {}, onSetEnforcement: () => {}, onApplyProfile: async () => '', onSetBinding: () => {}, onOpenWorkflow: () => {} })),
  ];
  for (const text of faces) {
    assert.doesNotMatch(text, FALSE_CLAIM, `the off state claims bindings stop: ${text}`);
    assert.ok(text.includes(TRUE_OFF), `the off state does not say what is true: ${text}`);
  }
  assert.equal(M.CHECKS_OFF_LINE, TRUE_OFF);
  assert.doesNotMatch(src('workflows.tsx') + src('workflow-rules.tsx') + src('help.tsx'), /not checked against required workflows/i);
});

// ---- Team rules: hidden until used (21.3) ---------------------------------------------------------------
const noRules = { workflows: [{ name: 'mine', requiredForOps: [] }], bindings: [], lints: [] };
const withRule = { workflows: [{ name: 'mine', requiredForOps: ['create_measure'] }], bindings: [{ op: 'create_measure', require: ['mine'], mode: 'hard', userDisablable: true }], lints: [] };

test('a person with no team rules meets no policy words; the off line only when a rule exists', () => {
  assert.equal(R.hasTeamRules(noRules, true), false);
  assert.equal(R.hasTeamRules(withRule, true), true);
  assert.equal(R.hasTeamRules(noRules, false), true, 'checks turned off is itself a rule');
  const drawer = (policy, enforced) => render(h(R.TeamRulesDrawer, { library: [{ name: 'mine', title: 'Mine', source: 'user', stepCount: 1 }], policy, enforced, profiles: [], isPro: true,
    onClose: () => {}, onSetEnforcement: () => {}, onApplyProfile: async () => '', onSetBinding: () => {}, onOpenWorkflow: () => {} }));
  const none = drawer(noRules, true);
  assert.match(none, /Required workflows None yet\./);
  assert.doesNotMatch(none, /Runs already underway keep the setting/, 'no off line without a rule');
  assert.match(drawer(withRule, true), /New measures go through Mine/);
  assert.equal(R.requiredWords('mine', withRule), 'Required for new measures');
  assert.equal(R.requiredWords('mine', noRules), null);
});

// ---- the list ---------------------------------------------------------------------------------------------
const info = (o) => ({ name: 'w', title: 'W', source: 'user', stepCount: 3, enabled: true, active: true, ...o });
const lib = [
  info({ name: 'mine', title: 'Mine' }),
  info({ name: 'verified-measure', title: 'Test a complex measure', source: 'stock', starter: true }),
  info({ name: 'optimize-dax', title: 'Fix a slow measure', source: 'stock', starter: true }),
  info({ name: 'req-stock', title: 'Required stock', source: 'stock', starter: true, required: true }),
  info({ name: 'off-one', title: 'Off one', source: 'stock', starter: true, enabled: false }),
  info({ name: 'hidden-one', title: 'Hidden one', source: 'user', active: false, activeReason: 'hidden by a project rule' }),
];

test('the list is yours, plus any shipped workflow a team rule requires; the filters search everything', () => {
  assert.deepEqual(M.listRows(lib).map((w) => w.name), ['mine', 'req-stock', 'hidden-one']);
  assert.deepEqual(M.filterRows(lib, '', 'off').map((w) => w.name), ['off-one'], 'Off finds a starter that is turned off');
  assert.deepEqual(M.filterRows(lib, '', 'hidden').map((w) => w.name), ['hidden-one']);
  assert.deepEqual(M.filterRows(lib, '', 'required').map((w) => w.name), ['req-stock']);
  assert.deepEqual(M.filterRows(lib, 'mine', null).map((w) => w.name), ['mine']);
  assert.equal(M.stateWord(lib[4]), 'Off');
  assert.equal(M.stateWord(lib[5]), 'Hidden');
  assert.equal(M.stateWord(lib[0]), null);
});

test('one Run per row, a Required mark only when a rule names it, and a broken file stays listed', () => {
  const row = (w, policy) => renderToStaticMarkup(h(W.WorkflowRow, { w, def: null, policy, isPro: true, onOpen: () => {}, onRun: () => {} }));
  const plain = row(info({ name: 'mine', title: 'Mine' }), noRules);
  assert.equal((plain.match(/>Run</g) || []).length, 1, 'one Run per row');
  assert.doesNotMatch(readable(plain), /Required/);
  assert.match(readable(row(info({ name: 'mine', title: 'Mine' }), withRule)), /Required for new measures/);
  assert.match(readable(row(info({ name: 'bad', error: 'YAML parse error' }), noRules)), /This file has an error\. Open it to read why\./);
  assert.match(plain, /disabled="" title="Runs as a row on the Plan"|title="Runs as a row on the Plan"/);
});

test('the step marks: one per step in the doer hue, and the line where the steps after the change begin', () => {
  const def = { name: 'x', title: 'X', steps: [
    { id: 'a', title: 'A', performer: 'person', questions: [{ name: 'q', question: 'Q?', by: 'you' }] },
    { id: 'b', title: 'B', performer: 'semanticus', action: { op: 'create_measure' }, blanks: [{ name: 'formula', by: 'assistant' }] },
    { id: 'c', title: 'C', performer: 'semanticus', action: { op: 'verify' } },
    { id: 'd', title: 'D', performer: 'semanticus', phase: 'after', action: { op: 'refresh_partition' } },
  ] };
  assert.deepEqual(M.stepMarks(def), { marks: ['you', 'assistant', 'semanticus', 'semanticus'], afterAt: 3 });
  assert.deepEqual(M.tagsOf(def.steps[1]), ['asst']);
  const guided = { name: 'g', title: 'G', steps: [{ id: 's1', title: 'S', performer: 'assistant', gate: { inputs: [{ name: 'a' }], verify: [{ kind: 'bpa_clean' }] } }] };
  assert.deepEqual(M.stepMarks(guided).marks, ['assistant'], 'a guided step is done by the assistant');
  assert.equal(M.summaryText(guided), 'Your assistant does 1 step, and Semanticus checks the work before a step counts as done.');
  // Lane author-gaps, B9: a person's stop now runs, so the sentence says where the run stops for you.
  assert.equal(M.summaryText(def), 'You answer 1 question and your assistant writes 1 part. Then Semanticus makes the change. It stops for you at step 1.');
  assert.equal(M.runKind({ ...def, schemaVersion: 3 }), 'draft');
  assert.equal(M.runKind({ ...guided, schemaVersion: 3 }), 'run');
  // N3 (lane l9-finish), found in lane e: Run on a saved all-assistant format-3 file with a Brief started it guided,
  // and the engine refused it. The rule is the engine's (WorkflowParser.IsComposed), the same one Run once uses.
  const briefed = { name: 'p', title: 'P', schemaVersion: 3, steps: [{ id: 's1', title: 'S', performer: 'assistant', brief: 'Time it cold and warm.' }] };
  assert.equal(M.runKind(briefed), 'draft');
  assert.equal(M.runKind({ ...briefed, schemaVersion: 2 }), 'run');
});

test('a format 3 check reads as the engine\'s own sentence (DraftCopy.CheckWords and PhaseWord)', () => {
  const copy = repoFile('Semanticus.Engine/Composer/DraftCopy.cs');
  for (const kind of ['valid_dax', 'dax_lint', 'max_chars', 'no_formula_text', 'valid_m', 'choice_member', 'name_free', 'key_unique', 'dates_within', 'source_matches', 'row_count']) {
    const m = new RegExp(`"${kind}" => "([^"]+)"`).exec(copy);
    assert.ok(m, `DraftCopy.cs has no sentence for ${kind}`);
    assert.equal(M.checkWords({ kind }), m[1], `${kind} differs from the engine`);
  }
  assert.equal(M.checkWords({ kind: 'refs_within', of: ['Returns', 'Sales'] }), 'Uses only Returns and Sales.');
  assert.equal(M.checkWords({ kind: 'result_range', min: '0', max: '1' }), 'The result is between 0% and 100%.');
  assert.match(copy, /phase == "before" \? "Checked" : phase == "written" \? "Checked once written" : "Checks after it runs"/);
  assert.equal(M.phaseWord('written'), 'Checked once written');
  assert.equal(M.phaseWord('after'), 'Checks after it runs');
});

// ---- New: starters, Describe it, Start blank ---------------------------------------------------------------
test('the six starters lead, every other shipped file stays under More starters, and a required one opens', () => {
  const shelf = M.starterShelf([
    info({ name: 'optimize-dax', title: 'Fix a slow measure', source: 'stock', starter: true }),
    info({ name: 'verified-measure', title: 'Test a complex measure', source: 'stock', starter: true, required: true }),
    info({ name: 'new-measure', title: 'Add a measure', source: 'stock', starter: true }),
    info({ name: 'mine', source: 'user' }),
  ], [{ name: 'month-end-close', title: 'Month-end close' }, { name: 'metric-certification', title: 'Certify a report' }]);
  assert.deepEqual(shelf.lead.map((s) => s.name), ['verified-measure', 'optimize-dax', 'month-end-close']);
  assert.deepEqual(shelf.more.map((s) => s.name), ['new-measure', 'metric-certification'], 'metric-certification has a UI door now (row 20)');
  const panel = render(h(W.NewPanel, { lead: shelf.lead, more: shelf.more, isPro: true, busy: null, onDescribe: () => {}, onUse: () => {}, onOpen: () => {}, onBlank: () => {}, onClose: null }));
  assert.ok(panel.indexOf('Describe it in words') < panel.indexOf('Or start from a starter'), 'Describe it comes first');
  const card = (name) => { const at = panel.indexOf(name); return panel.slice(at, at + 200); };
  assert.match(card('Test a complex measure'), /Open/, 'a required starter shows Open, not Use');
  assert.match(card('Fix a slow measure'), /Use/);
  assert.match(panel, /Start blank/);
  const free = render(h(W.NewPanel, { lead: [], more: [], isPro: false, busy: null, onDescribe: () => {}, onUse: () => {}, onOpen: () => {}, onBlank: () => {}, onClose: null }));
  assert.match(free, /New workflow and Describe it in words need Pro\. Nothing is drafted or counted here on Free\./);
  assert.equal(M.plainTemplateTitle('Month-end close for {{period_label}}'), 'Month-end close for your period');
  assert.equal(M.plainTemplateTitle('Author "{{measure_goal}}" and reconcile it'), 'Author "your measure goal" and reconcile it');
  assert.equal(M.copyName('optimize-dax', [info({ name: 'optimize-dax', source: 'user' })]), 'optimize-dax-2');
  assert.equal(M.copyName('optimize-dax', []), null);
});

test('Describe it is authoring: the steps open in Author and nothing is drafted onto the Plan (Kane, UAT 25 Sep)', () => {
  const page = src('workflows.tsx');
  assert.match(page, /rpc<M\.DescribedWorkflow>\('describeWorkflowSteps', words\)/);
  // The engine's format 3 file when it wrote one (C7 on L5: each step's action, who does it and its checks), else the steps.
  assert.match(page, /\? \{ title: M\.describedTitle\(words\), document: d\.document, note: M\.describedNote\(d\) \}/);
  assert.match(page, /: \{ title: M\.describedTitle\(words\), words, steps: d\.steps \?\? \[\], note: M\.describedNote\(d\) \}\);/,
    'the described steps open in Author');
  const describe = page.slice(page.indexOf('const describe = async'), page.indexOf('// ---- team rules'));
  assert.doesNotMatch(describe, /composeTask|onOpenPlan/, 'describing never drafts on the Plan or lands there');
  assert.match(M.DESCRIBE_LINE, /Nothing runs until you press Run\./);
  assert.equal(M.describedTitle('Every month, refresh Sales, run the saved checks and publish the model.'), 'Every month, refresh Sales, run the saved checks');
  assert.equal(M.describedTitle('Hide the key columns'), 'Hide the key columns');
  assert.match(M.describedNote({ sentence: '3 steps from your words; 1 to fill in.', when: 'Every month' }), /"Every month" is when it runs, not a step/);
  // A placeholder is a real step with a name and an ask, never a broken row.
  const author = src('workflowdesign.tsx');
  // A step with an op carries every op the engine chose (lane L5: Jev's ops, or the word match's one) and what it works
  // on; a step with none is a placeholder that says why.
  assert.match(author, /const ops = s\.ops\?\.length \? s\.ops : s\.op \? \[s\.op\] : \[\];/);
  assert.match(author, /const instructions = !ops\.length \? \(s\.reason \|\| PLACEHOLDER_STEP\) : s\.target \? `Works on \$\{s\.target\}\.` : '';/);
  // The composer's menu still stands in for Jev on a draft made from the Plan's own words (lane L5a).
  assert.match(src('composer-draft.tsx'), /v\.menu && !ran && <section className="cd-menu"/);
  assert.equal(M.nameFromTitle('Publish to the test workspace every Friday', []), 'publish-to-the-test-workspace-every-friday');
  assert.equal(M.nameFromTitle('Tidy', [info({ name: 'tidy' })]), 'tidy-2');
  assert.equal(M.nameFromTitle('!!!', []), null);
  // Verifier on m11/sol-fixes-1, D2: a new workflow's name is free among the kept NEW workflows too, which are kept by
  // file name, so a second "Month end check" or a second description with the same first sentence gets its own key.
  assert.equal(M.nameFromTitle('Month end check', [info({ name: 'tidy' }), { name: 'month-end-check' }]), 'month-end-check-2');
  assert.equal(M.freeName('every-month-refresh-sales', [{ name: 'every-month-refresh-sales' }, { name: 'every-month-refresh-sales-2' }]), 'every-month-refresh-sales-3');
  assert.equal(M.withWorkflowName('---\nschema_version: 3\nname: a\nsteps:\n  - name: keep\n', 'a-2'), '---\nschema_version: 3\nname: a-2\nsteps:\n  - name: keep\n');
  assert.match(author, /const taken = \[\.\.\.\(library \?\? \[\]\), \.\.\.heldDrafts\.list\(\)\.filter\(\(h\) => h\.creating\)\];/);
});

// ---- Author ------------------------------------------------------------------------------------------------
test('Save is one click: the preview shows only on a warning or a file conflict', () => {
  assert.equal(D.previewNeedsReading({ outcome: 'ready', issues: [] }), false);
  assert.equal(D.previewNeedsReading({ outcome: 'ready', issues: [{ severity: 'info' }] }), false);
  assert.equal(D.previewNeedsReading({ outcome: 'ready', issues: [{ severity: 'warning' }] }), true);
  assert.equal(D.previewNeedsReading({ outcome: 'conflict', issues: [] }), true);
  assert.equal(D.previewNeedsReading({ outcome: 'no_change', issues: [] }), false);
  const author = src('workflowdesign.tsx');
  assert.match(author, /if \(result && !previewNeedsReading\(result\)\) await write\(\);/, 'a clean preview writes at once');
  assert.match(author, /'editWorkflowDocument', held\.base\.name, held\.base\.byteHash/, 'the conflict guard (the saved hash) stays on the write');
});

test('the older-format notice is the live door to upgrade_workflow, preview first', () => {
  const author = src('workflowdesign.tsx');
  assert.match(author, /rpc<WorkflowUpgradeResult>\('upgradeWorkflow', name, true, document\.byteHash, document\.path, 'human'\)/);
  assert.match(author, /rpc<WorkflowUpgradeResult>\('upgradeWorkflow', name, false, preview\.document\.byteHash, preview\.document\.path, 'human'\)/);
  assert.equal(M.canUpgrade({ name: 'x', title: 'X', steps: [], schemaVersion: 1 }, true), true);
  assert.equal(M.canUpgrade({ name: 'x', title: 'X', steps: [], schemaVersion: 1 }, false), false, 'a starter is never upgraded in place');
  assert.equal(M.canUpgrade({ name: 'x', title: 'X', steps: [], schemaVersion: 2 }, true), false);
  assert.ok(!existsSync(join(webview, 'src', 'workflowdocument.tsx')), 'the dead editor is deleted');
});

test('Make it reusable copies the run\'s workflow with the run it came from, and nothing else changes', () => {
  const v1 = ['---', 'name: optimize-dax', 'title: Fix a slow measure', 'version: 1', '---', '', '## Step 1: Freeze', 'Body.'].join('\n');
  const out1 = M.reusableCopy(v1, 'optimize-dax-from-a-run', 'Fix a slow measure, from a run', 'wfr-7');
  assert.equal(out1, ['---', 'name: optimize-dax-from-a-run', 'title: "Fix a slow measure, from a run"', 'version: 1', 'derived_from: wfr-7', '---', '', '## Step 1: Freeze', 'Body.'].join('\n'));
  const v3 = ['---', 'schemaVersion: 3', 'name: a', 'title: A', 'provenance:', '  template: verified-measure', '  derived_from: wfr-1', '---', 'x'].join('\r\n');
  const out3 = M.reusableCopy(v3, 'a-from-a-run', 'A, from a run', 'wfr-9');
  assert.equal(out3, ['---', 'schemaVersion: 3', 'name: a-from-a-run', 'title: "A, from a run"', 'provenance:', '  template: verified-measure', '  derived_from: wfr-9', '---', 'x'].join('\r\n'), 'CRLF kept, one derived_from');
  assert.equal(M.reusableName('optimize-dax', [info({ name: 'optimize-dax-from-a-run' })]), 'optimize-dax-from-a-run-2');
  assert.equal(M.subLine(info({ stepCount: 4 }), { name: 'x', title: 'X', steps: [], provenance: { derived_from: 'wfr-7' } }), 'From a run · 4 steps');
  assert.match(src('workflow-run.tsx'), /rpc\('saveWorkflow', name, reusableCopy\(doc\.exactText, name, title, run\.runId\), 'human', true\)/, 'create-only, through save_workflow');
  assert.match(src('plan-history.tsx'), /row\.kind === 'run' && row\.report\?\.runId && <RunEntryActions/, 'the door is the run\'s History entry');
});

// ---- runs and drafts are Plan rows (R3, F-275) -----------------------------------------------------------
test('a run and a draft are Plan rows with their own detail; the Runs list and the live banner are gone', () => {
  const page = src('workflows.tsx');
  for (const gone of ['RunsSection', 'LiveBanner', 'EnforcementOffBanner', 'SectionSidebar', 'HomeSection', 'LibrarySection', 'GovernanceSection', 'PlaybookPage', 'AuthorSection', 'HOME_JOBS', 'GROUP_OF'])
    assert.doesNotMatch(page, new RegExp(`\\b${gone}\\b`), `${gone} is still in workflows.tsx`);
  const plan = src('plan.tsx');
  assert.match(plan, /item\.id\.startsWith\('run\/'\) && <div className="pw-detail pw-detail-own"><WorkflowRunDetail/);
  assert.match(plan, /P\.isDraftItem\(item\) && <ComposerDraft draftId=\{draftIdOf\(item\.id\)\}/, 'a draft row draws the draft (lane U5)');
  const app = src('App.tsx');
  assert.match(app, /const openWorkflowRuns = \(\) => \{ const live = mostRecentLive\(runs\); openPlanRow\(live \? `run\/\$\{live\.runId\}` : null\); \};/, 'the header run chip opens the run\'s Plan row');
  const run = src('workflow-run.tsx');
  assert.equal((run.match(/label="Abort"/g) || []).length, 1, 'one Abort, in the current step card');
  for (const call of ['submitWorkflowStep', 'skipWorkflowStep', 'abortWorkflow', 'exportWorkflowEvidence', 'getWorkflowRun'])
    assert.match(run, new RegExp(`'${call}'`), `the run row detail calls ${call}`);
  assert.match(run, /onWorkflowChange\(\(v\) =>/, 'the run row follows workflow\/didChange from either door');
});

test('Studio calls composeTask, getDraft, editDraft, runDraft and approveDraft, and follows draft/didChange (F-275)', () => {
  // The draft's own view is lane U5's (composer-draft.tsx, merged here); Workflows drafts from words and from a saved
  // workflow's Run, and opens a draft handed to it in Author with the same body.
  const all = src('composer-draft.tsx') + src('composer-model.ts') + src('workflows.tsx');
  for (const call of ['composeTask', 'getDraft', 'editDraft', 'runDraft', 'approveDraft']) assert.match(all, new RegExp(`'${call}'`), `no Studio caller of ${call}`);
  assert.match(src('composer-draft.tsx'), /onDraftChange\(\(payload\) =>/);
  assert.match(src('bridge.ts'), /msg\.type === 'draftDidChange'/);
  assert.equal((src('bridge.ts').match(/export function onDraftChange/g) || []).length, 1, 'one draft channel, not two');
  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.equal((ext.match(/onNotification\('draft\/didChange'/g) || []).length, 1, 'the host forwards draft/didChange once');
  const page = src('workflows.tsx');
  assert.match(page, /navTarget\.kind === 'draft' && navTarget\.value\) \{[^}]*setView\(\{ kind: 'draft', draftId: navTarget\.value \}\)/, 'a draft handed to Workflows opens in Author');
  assert.match(page, /<ComposerDraft draftId=\{draftId\} onGo=\{onGo\} \/>/, 'Author draws the Plan row\'s own draft body');
  assert.match(src('App.tsx'), /seed\.kind === 'draft' \? 'draft' as const/, 'the route seed reaches Workflows');
  assert.equal(W.draftWorkflowOf({ source: 'optimize-dax' }, [{ name: 'optimize-dax' }]), 'optimize-dax');
  assert.equal(W.draftWorkflowOf({ source: 'From your words' }, [{ name: 'optimize-dax' }]), null, 'a words draft has no file behind it');
  assert.match(src('plan.tsx'), /item\.id\.startsWith\('run\/'\) && <div className="pw-detail pw-detail-own"><WorkflowRunDetail/);
});

// ---- the 73 capabilities of contract 21.1, each with its new home proven ------------------------------------
// Each row names where the capability lives now and the proof: a source fact read here, or a driver scene in
// tools/uishot/workflows-drive.mjs that clicks it and measures what landed (results.json). The inventory and its
// numbering: /tmp/semanticus-joint-review/workflows-design/inventory.md, carried into contract 21.1.
const S = (file, re) => ({ file, re });
const scene = (name) => ({ scene: name });
const CHECKLIST = [
  [1, 'one page: the list, and Author', [S('workflows.tsx', /type View = \{ kind: 'list' \} \| \{ kind: 'author'/), scene('list')]],
  [2, 'Team rules link, Required mark', [S('workflows.tsx', /data-testid="wf-team-rules-link"/), scene('team-rules')]],
  [3, 'live run is its Plan row', [S('plan.tsx', /<WorkflowRunDetail/), scene('flow-2-run')]],
  [4, 'header run chip opens the Plan row', [S('App.tsx', /openPlanRow\(live \? `run\//)]],
  [5, 'shell run map feeds the Plan run rows', [S('App.tsx', /onRunUpdate=\{foldRun\}/), S('plan.tsx', /onRunUpdate=\{onRunUpdate\}/)]],
  [6, 'checks-off line in Team rules when a rule exists', [S('workflow-rules.tsx', /showOffLine=\{rules\}/), scene('team-rules-off')]],
  [7, 'deep links open Author; Edit rules opens Settings', [S('workflows.tsx', /navTarget\.kind === 'workflow' && navTarget\.value\) openAuthor/), S('workflows.tsx', /openAuthor\(n, \{ settings: true \}\)/)]],
  [8, 'free: the real list, read only, one Pro line', [S('propreview.tsx', /Search your workflows/), scene('free')]],
  [9, 'page title cut', [S('workflows.tsx', /^(?![\s\S]*Repeatable ways to do a job well)/)]],
  [10, 'last run is its History entry', [S('plan-history.tsx', /<RunEntryActions/), scene('flow-5-report')]],
  [11, 'start a job: starters under New', [S('workflows-model.ts', /SIX_STARTERS/), scene('empty')]],
  [12, 'row opens Author, one Run', [S('workflows.tsx', /data-testid=\{`wf-run-\$\{w\.name\}`\}/), scene('flow-2-run')]],
  [13, 'the list\'s one search', [S('workflows.tsx', /data-testid="wf-search"/)]],
  [14, 'All workflows button cut', [S('workflows.tsx', /^(?![\s\S]*All workflows \()/)]],
  [15, 'template shelf: starters under New', [S('workflows-model.ts', /for \(const t of templates \?\? \[\]\)/)]],
  [16, 'webview catalog cut; starters from the engine', [S('workflows.tsx', /rpc<typeof rawTemplates>\('listWorkflowTemplates'\)/)]],
  [17, 'featured workflows: starters', [S('workflows-model.ts', /'verified-measure', 'optimize-dax'/)]],
  [18, 'more ready-made: starters', [S('workflows.tsx', /More starters \(\$\{more\.length\}\)/)]],
  [19, 'template setup asked once, then Author', [S('workflows.tsx', /function UseDrawer/), scene('use-template')]],
  [20, 'metric-certification is a starter', [S('workflows-model.ts', /more = items\.filter/)]],
  [21, 'save or delete a starter: agent door, stated', [S('help.tsx', /Your assistant can save a starter of your own/)]],
  [22, 'library search: the list\'s search', [S('workflows-model.ts', /export function filterRows/)]],
  [23, 'one New: Describe it, starters, Start blank', [S('workflows.tsx', /data-testid="wf-start-blank"/), scene('describe-authors'), scene('describe-placeholder')]],
  [24, 'journey groups cut', [S('workflows.tsx', /^(?![\s\S]*GROUP_OF)/)]],
  [25, 'one Required mark, one state word', [S('workflows-model.ts', /export function stateWord/)]],
  [26, 'one Offered control in Rules', [S('workflow-rules.tsx', /export function OfferedControl/), scene('author-settings')]],
  [27, 'broken file stays listed; error opens in Author', [S('workflows.tsx', /This file has an error\. Open it to read why\./)]],
  [28, 'Author back link', [S('workflowdesign.tsx', /data-testid="wf-back"/)]],
  [29, 'header pills cut, Required mark stays', [S('workflowdesign.tsx', /data-testid="wf-required-mark"/)]],
  [30, 'version and meta in Settings details', [S('workflowdesign.tsx', /<span>Version<\/span>/)]],
  [31, 'Run in Author and on the row', [S('workflowdesign.tsx', /data-testid="wf-author-run"/)]],
  [32, 'one line in Author for off and hidden', [S('workflowdesign.tsx', /Hidden today: \{info\.activeReason\}/)]],
  [33, 'what it does: the Steps view', [S('workflowforms.tsx', /What it does/), scene('author')]],
  [34, 'when it applies: Rules in Settings', [S('workflowdesign.tsx', /<OfferedControl info=\{info\}/)]],
  [35, 'last completed run: History', [S('workflow-run.tsx', /export function RunEntryActions/)]],
  [36, 'Runs section: Plan rows', [S('plan.tsx', /item\.id\.startsWith\('run\/'\)/)]],
  [37, 'run header: the row\'s doer and state', [S('workflow-run.tsx', /Step <span className="sem-num">/)]],
  [38, 'report after completion: History detail', [S('workflow-run.tsx', /data-testid="ph-run-report"/), scene('flow-5-report')]],
  [39, 'one Abort in the row detail', [S('workflow-run.tsx', /label="Abort"/)]],
  [40, 'step rail and record in the row detail', [S('workflow-run.tsx', /function RunRail/)]],
  [41, 'loop and call frames in the row detail', [S('workflow-run.tsx', /function RunFrameCard/)]],
  [42, 'current step: Submit, Skip, Abort, same RPCs', [S('workflow-run.tsx', /'skipWorkflowStep'/), scene('flow-3-steps')]],
  [43, 'hand-off with checks off', [S('workflow-run.tsx', /Turn its checks on for this run/)]],
  [44, 'checks off for this step', [S('workflow-run.tsx', /Checks are turned off for this step/)]],
  [45, 'Plan item picker inside a gate', [S('workflow-run.tsx', /function PlanItemPicker/)]],
  [46, 'workflow report dialog from History', [S('workflow-run.tsx', /export function RunReportDialog/)]],
  [47, 'Checks in runs: on / off, true copy', [S('workflow-rules.tsx', /<b>Checks in runs<\/b>/), scene('team-rules-off')]],
  [48, 'Use a set of team rules: the sets as cards', [S('workflow-rules.tsx', /Start from a set of team rules/)]],
  [49, 'Governance table cut; filters', [S('workflows-model.ts', /\{ id: 'required', label: 'Required' \}/), scene('filter-off')]],
  [50, 'Required for in Rules, and the row mark', [S('workflowdesign.tsx', /<RequiredForControl name=\{info\.name\}/)]],
  [51, 'Offered: always, only when, off; six chips', [S('workflow-rules.tsx', /Readiness B or better/)]],
  [52, 'policy notes on the affected workflow', [S('workflow-rules.tsx', /lints\.map\(\(l, i\) =>/)]],
  [53, 'warn or block: agent door, stated', [S('help.tsx', /make a whole model's checks warn instead of stop/)]],
  [54, 'Author empty state cut', [S('workflows.tsx', /^(?![\s\S]*AuthorSection)/)]],
  [55, 'a title in words, the id derived; Describe it first', [S('workflowdesign.tsx', /M\.nameFromTitle\(title, taken\)/), scene('start-blank')]],
  [56, 'Author header', [S('workflowdesign.tsx', /data-testid="wf-saved"/), scene('author')]],
  [57, 'Steps the face, View switch', [S('workflowdesign.tsx', /data-testid=\{`wf-view-\$\{v\}`\}/), scene('author-menu')]],
  [58, 'Undo in the header; Redo, Discard, Delete in the menu', [S('workflowdesign.tsx', /data-testid="wf-undo"/), S('workflowdesign.tsx', /Discard changes/)]],
  [59, 'one-click Save; preview on warning or conflict', [S('workflowdesign.tsx', /export function previewNeedsReading/), scene('author-dirty-save')]],
  [60, 'Copy to this project gone; Use copies', [S('workflowdesign.tsx', /data-testid="wf-author-use"/), scene('flow-1-use')]],
  [61, 'tag icons, one + Add step', [S('workflowdesign.tsx', /<TagMark key=\{t\} tag=\{t\} \/>/)]],
  [62, 'Settings drawer: notes, inputs, rules, repeat', [S('workflowdesign.tsx', /Repeat on the Plan/), scene('author-settings')]],
  [63, 'step form, the step id in Source', [S('workflowforms.tsx', /^(?![\s\S]*Step ids stay fixed after saving)/)]],
  [64, 'the grouped picker only', [S('workflowforms.tsx', /^(?![\s\S]*list="sem-wf-op-list")/)]],
  [65, 'runs only when: plain, raw in Source', [S('workflowforms.tsx', /The condition itself is in Source/)]],
  [66, 'repeats for each item', [S('workflowforms.tsx', /Repeats for each item/)]],
  [67, 'calls another workflow, plain copy', [S('workflowforms.tsx', /Answers it hands back/)]],
  [68, 'You choose, Your assistant writes, checks', [S('workflowforms.tsx', /Brief for your assistant/), scene('author')]],
  [69, 'canvas and its layout behind View', [S('workflowdesign.tsx', /<WorkflowCanvas workflowName/), scene('author-canvas')]],
  [70, 'Source behind View', [S('workflowdesign.tsx', /<SourceEditor draft=\{draft\}/), scene('author-source')]],
  [71, 'dead editor deleted; the upgrade notice', [S('workflowdesign.tsx', /function UpgradeNotice/), scene('author-upgrade')]],
  [72, 'learned workflows in the list; Check in Author', [S('workflows-model.ts', /From a run · /), S('workflowdesign.tsx', /data-testid="wf-check"/), scene('author-check')]],
  [73, 'help copy in the three names', [S('help.tsx', /^(?![\s\S]*Workflows > Governance)/)]],
];

// ---- pre-handover journey polish, lane polish-b (F11 to F14) -------------------------------------------------
const UI = load(join(webview, 'src', 'workflow-ui.tsx'));
const RUN = load(join(webview, 'src', 'workflow-run.tsx'));
const markup = (el) => renderToStaticMarkup(el);

test('F11: a filled starter template is not offered an upgrade its own format refuses', () => {
  // The engine now writes a stock starter's copy in the current format (WorkflowStarterTests). A filled template
  // keeps its provenance as format 1 top-level keys, which format 2 refuses, so the notice would lead to a refusal.
  assert.equal(M.canUpgrade({ name: 'x', title: 'X', steps: [], schemaVersion: 1, provenance: { template: 'hard-measure', slot_values: '{}' } }, true), false);
  assert.equal(M.canUpgrade({ name: 'x', title: 'X', steps: [], schemaVersion: 1, provenance: {} }, true), true, 'your own format 1 file is still offered it');
});

test('F13: the skip box asks one plain question, never "Skip with reason reason"', () => {
  const open = (props) => markup(h(UI.ReasonButton, { label: 'Skip with reason', onConfirm: () => {}, defaultOpen: true, ...props }));
  assert.doesNotMatch(open({}), /reason reason/i);
  assert.match(open({ placeholder: 'Why skip this step?' }), /placeholder="Why skip this step\?"/);
  const run = src('workflow-run.tsx');
  assert.match(run, /label="Skip with reason"[^>]*placeholder="Why skip this step\?"/);
  assert.match(run, /label="Abort"[^>]*placeholder="Why stop this run\?"/);
});

const step = (status, n) => ({ stepId: `step-${n}`, title: `Step ${n}`, status, answers: {}, verifyResults: [] });
const finished = { runId: 'wfr-1', workflow: 'verified-measure', title: 'Test a complex measure', workflowVersion: 7, status: 'completed',
  startedUtc: '', stepIndex: 4, totalSteps: 4, steps: [step('done', 1), step('skipped', 2), step('skipped', 3), step('skipped', 4)] };

test('F14: a run counts done, skipped and passed steps the way its steps read them', () => {
  assert.equal(UI.runTally(finished), '1 done · 3 skipped');
  assert.equal(UI.runTally({ ...finished, status: 'active', stepIndex: 2, steps: [step('passed', 1), step('done', 2), step('pending', 3), step('pending', 4)] }), '1 passed · 1 done · 2 to go');
  assert.equal(UI.runTally({ ...finished, status: 'active', totalStepsProvisional: true, stepIndex: 1, steps: [step('passed', 1), step('pending', 2)], totalSteps: 2 }), '1 passed · at least 1 to go');
  const rail = render(h(RUN.RunRail, { run: finished, onSubmit: async () => {}, onSkip: async () => {}, onAbort: async () => {} }));
  assert.match(rail, /Run · 1 done · 3 skipped/);
  assert.doesNotMatch(rail, /0 of 4 steps passed/);
});

test('F12: a step never asks the person for machine input; the assistant fills what Semanticus reads as data', () => {
  const current = {
    stepId: 'step-2', title: 'Lock the expected values', instructions: 'Lock them.', ops: [], verifyKinds: ['anchor_coverage'], effectiveStrictness: 'hard',
    assistantFills: ['expectedValues', 'equivalenceGrid'],
    questions: [
      { name: 'expectedValues', question: 'The locked anchors as one fenced JSON array of {context, optional axis, expect} objects.', type: 'text', required: 'required' },
      { name: 'equivalenceGrid', question: "Machine field: the comma-separated qualified display columns, for example 'Product'[Category].", type: 'text', required: 'required' },
      { name: 'naiveForm', question: 'The tempting wrong formula, and where it gives a different answer.', type: 'text', required: 'required' },
    ],
  };
  const html = markup(h(RUN.CurrentStepCard, { current, onSubmit: async () => {}, onSkip: async () => {}, onAbort: async () => {} }));
  const text = readable(html);
  assert.doesNotMatch(text, /Machine field|fenced JSON|\{context/, 'a machine question is shown to the person');
  assert.match(text, /Your assistant fills these in/);
  assert.match(text, /Expected values/);
  assert.match(text, /Equivalence grid/);
  assert.match(text, /The tempting wrong formula, and where it gives a different answer\./, 'a question in words stays the person\'s');
  assert.equal((html.match(/<textarea/g) || []).length, 1, 'one box, for the question a person can answer');
});

// ---- r1 min4: the minimal pass (W4, W10 to W15, I4, I5) --------------------------------------------------
test('the Required / Off / Hidden filters show only once a workflow is in one of those states', () => {
  assert.deepEqual(M.visibleFilters([info({ name: 'a' }), info({ name: 'b' })], null), [], 'nothing required, off or hidden: no dead control');
  assert.deepEqual(M.visibleFilters(null, null), []);
  assert.deepEqual(M.visibleFilters([info({ name: 'a', required: true })], null).map((f) => f.id), ['required', 'off', 'hidden']);
  assert.equal(M.visibleFilters([info({ name: 'a', enabled: false })], null).length, 3);
  assert.equal(M.visibleFilters([info({ name: 'a', active: false })], null).length, 3);
  assert.equal(M.visibleFilters([info({ name: 'a' })], 'off').length, 3, 'a pressed filter stays so it can be pressed again');
});

test('Team rules says each thing once: one switch row, no second explainer, no FYI section', () => {
  const rules = src('workflow-rules.tsx');
  assert.doesNotMatch(rules, /Your assistant can also|Each set shows exactly what it changes|Someone working alone can ignore this/);
  assert.doesNotMatch(rules, /CHECKS_ON_LINE/, 'the heading and its On line were the same fact (W13)');
  const on = render(h(R.ChecksSwitch, { enforced: true, showOffLine: false, onSet: () => {} }));
  assert.equal(on, 'Checks in runs Off skips the checks inside runs. Required workflows stay required.');
});

test('the Author step header carries no pills, no outline footnote and no inset box (W10 to W12)', () => {
  const forms = src('workflowforms.tsx');
  const header = forms.slice(forms.indexOf('<div className="sem-wf-sf-h">'), forms.indexOf('<div className="sem-wf-what">'));
  assert.doesNotMatch(header, /TagMark/, 'the pills repeat the section heads 40px below');
  assert.doesNotMatch(src('workflowdesign.tsx'), /A step with no mark is done by Semanticus/);
  assert.match(src('styles.css'), /\.sem-wf-what \{ display: flex; flex-direction: column; gap: var\(--sem-space-1\); \}/, 'What it does is a label and a field, not a box');
});

test('the Model interview is not offered in the workflow pickers, and a workflow that already uses it still shows it (I4, I5)', () => {
  const forms = src('workflowforms.tsx');
  assert.match(forms, /catalog\.filter\(\(o\) => !isInterviewOp\(o\.name\) \|\| step\.ops\.includes\(o\.name\)\)/, 'the action picker hides interview actions unless the step has one');
  assert.match(forms, /model\.choices\.verifyKinds\.filter\(\(kind\) => !isInterviewCheck\(kind\) \|\| kind === verify\.kind\)/, 'the Check picker hides the replay unless this check is one');
  assert.match(forms, /const isInterviewCheck = \(kind: string\) => kind === 'interview_replay';/);
  assert.match(src('copy.ts'), /interview_replay: 'The saved questions were asked again'/, 'the title stays so a saved workflow still reads plainly');
  for (const op of ['add_interview_question', 'delete_interview_question', 'list_interview_questions', 'list_interview_seeds', 'run_interview'])
    assert.match(op, /interview/i, `${op} is caught by the picker filter`);
});

test('every one of the 73 capabilities has a new home, and each home is proven', () => {
  assert.equal(CHECKLIST.length, 73);
  assert.deepEqual(CHECKLIST.map((r) => r[0]), Array.from({ length: 73 }, (_, i) => i + 1));
  const driver = readFileSync(join(root, 'tools', 'uishot', 'workflows-drive.mjs'), 'utf8');
  for (const [n, home, proofs] of CHECKLIST) {
    assert.ok(proofs.length > 0, `row ${n} has no proof`);
    for (const p of proofs) {
      if (p.scene) assert.match(driver, new RegExp(`await scene\\('${p.scene}'`), `row ${n} (${home}): no driver scene ${p.scene}`);
      else assert.match(src(p.file), p.re, `row ${n} (${home}): ${p.file} does not show it`);
    }
  }
});

console.log(`workflows one page: ${passed} passed`);
