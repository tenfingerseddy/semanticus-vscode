import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { basename, dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const sourceRoots = [
  resolve(extensionRoot, 'src'),
  resolve(extensionRoot, 'webview/src'),
];
const sourceFiles = sourceRoots.flatMap(walk).concat(resolve(extensionRoot, 'media/propgrid/propgrid.js'));
const visiblePropertyNames = new Set([
  'a', 'blurb', 'body', 'description', 'detail', 'emptyText', 'label', 'lead', 'message',
  'placeholder', 'q', 'question', 'steps', 'subtitle', 'tip', 'title', 'tooltip',
]);
const visibleJsxAttributeNames = new Set(['aria-label', 'ariaLabel', 'placeholder', 'title']);
const failures = [];
// Every string the walk decided a person can read. The finish-line block at the bottom checks this set
// against the real MCP operation list, so it must stay large enough to mean something.
const userVisibleStrings = [];

for (const file of sourceFiles) {
  const source = readFileSync(file, 'utf8');
  const kind = extname(file) === '.tsx' ? ts.ScriptKind.TSX
    : extname(file) === '.js' ? ts.ScriptKind.JS
      : ts.ScriptKind.TS;
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, kind);
  visit(tree, tree, file);
}

const manifest = JSON.parse(readFileSync(resolve(extensionRoot, 'package.json'), 'utf8'));
checkText('package.json displayName', manifest.displayName);
checkText('package.json description', manifest.description);
for (const [index, keyword] of manifest.keywords.entries()) checkText(`package.json keywords[${index}]`, keyword, true);
for (const command of manifest.contributes?.commands ?? []) checkText(`package.json command ${command.command}`, command.title);
for (const submenu of manifest.contributes?.submenus ?? []) checkText(`package.json submenu ${submenu.id}`, submenu.label);
for (const views of Object.values(manifest.contributes?.views ?? {})) {
  for (const view of views) checkText(`package.json view ${view.id}`, view.name);
}
walkManifestConfiguration(manifest.contributes?.configuration, 'package.json configuration');
for (const root of ['Semanticus.Engine/workflows', 'Semanticus.Engine/workflow-templates']) {
  for (const file of walkFiles(resolve(repoRoot, root), new Set(['.md']))) {
    const lines = readFileSync(file, 'utf8').split(/\r?\n/);
    lines.forEach((line, index) => {
      const match = /^(title|description):\s*(.*)$/.exec(line);
      if (!match) return;
      const rendered = match[2].replace(/\{\{[^}]+\}\}/g, 'value');
      checkText(`${relative(repoRoot, file).replaceAll('\\', '/')}:${index + 1}`, rendered, false, true);
    });
  }
}

const copyModule = await loadTypeScriptModule(resolve(extensionRoot, 'webview/src/copy.ts'));
assert.equal(copyModule.uiLabel('impact_assessment'), 'Impact assessment');
assert.equal(copyModule.uiLabel('ConnectedAndInitialized'), 'Connected and initialized');
assert.equal(copyModule.uiLabel('DAXEquivalence'), 'DAX equivalence');
assert.equal(copyModule.uiLabel('answer-or-decline'), 'Answer or decline');
assert.equal(copyModule.uiLabel('', 'None'), 'None');

const advmodels = readFileSync(resolve(extensionRoot, 'webview/src/advmodels.tsx'), 'utf8');
assert.match(advmodels, /Created table \$\{n\} with \$\{picked\.length\} field/,
  'D-157: field-parameter success names the table, not an object ref');
assert.doesNotMatch(advmodels, /Created \$\{ref\} with/,
  'D-157: field-parameter success must not print table:Name');

const dataAgentUi = readFileSync(resolve(extensionRoot, 'webview/src/dataagent.tsx'), 'utf8');
assert.doesNotMatch(dataAgentUi, /verify-at-build/,
  'D-166: Data Agent UI must not print the build TODO');
assert.doesNotMatch(dataAgentUi, /\{list\?\.note &&/,
  'D-166: the agent rail must not reprint the engine note');
assert.doesNotMatch(dataAgentUi, /\{list\.note &&/,
  'D-166: the empty workspace must not reprint the engine note');

const hiddenPalette = new Set(
  (manifest.contributes?.menus?.commandPalette ?? [])
    .filter((entry) => entry.when === 'false')
    .map((entry) => entry.command),
);
for (const command of [
  'semanticus.editMCode', 'semanticus.showLineage', 'semanticus.addToDiagram',
  'semanticus.copyObject', 'semanticus.pasteObject',
  'semanticus.copyFromModel',
  'semanticus.studioGoTab',   // Go to Studio Area is registered but not listed (r1 min4, P3): Go to Studio Tab covers it
]) {
  assert.equal(hiddenPalette.has(command), false, `D-142: ${command} must appear in the command palette`);
}
const formatDax = (manifest.contributes?.menus?.commandPalette ?? [])
  .find((entry) => entry.command === 'semanticus.formatDaxOnline');
assert.ok(formatDax && formatDax.when !== 'false', 'D-142: format DAX must not be hidden with when false');

const extension = readFileSync(resolve(extensionRoot, 'src/extension.ts'), 'utf8');
assert.doesNotMatch(extension, /function \(needs compat level 1702\+\)/,
  'D-147: New Function must not name a compatibility level the Properties grid used to hide');

// ---- finish line: every user-visible string, against the real operation list -------------------------
// The operation list is the coverage oracle's own inventory of the MCP surface, so a newly added operation
// cannot slip into copy unnoticed. This is the walk the card asks for: an operation name, an environment
// variable or a dotfile in anything a person reads is a failure, not a judgement call.
const inventory = JSON.parse(readFileSync(resolve(repoRoot, 'docs/mcp-surface-inventory.json'), 'utf8'));
const operationNames = new Set((inventory.operations ?? []).map((entry) => entry.operation));
assert.ok(operationNames.size > 100, `the operation inventory looked empty (${operationNames.size} entries)`);
assert.ok(userVisibleStrings.length > 200, `the copy walk collected too little to prove anything (${userVisibleStrings.length} strings)`);
for (const { where, text } of userVisibleStrings) {
  for (const word of text.split(/[^A-Za-z0-9_]+/)) {
    if (operationNames.has(word)) failures.push(`${where}: operation name ${word}`);
  }
  if (/SEMANTICUS_[A-Z0-9_]+/.test(text)) failures.push(`${where}: environment variable`);
  if (/~\/\.semanticus/.test(text)) failures.push(`${where}: config or license dotfile`);
}

assert.deepEqual(failures, [], `User-facing copy violations:\n${failures.join('\n')}`);

// ---- honesty: copy that claimed Jev (or the engine) did something it does not do (lane m11/honesty) -----------
// BYTE-LEVEL, over the raw source of every place a person can read these from: the extension, the webview, the
// screenshot fixtures that stand in for the engine, and the engine's own C#. Raw bytes, not the TypeScript walk
// above, because two of these live in C# and one is split across a string concatenation the walk never joins.
// Each entry names why the old words were false, so a reviewer who meets a failure knows what to fix.
const JEV_DRAFTS_ON_PLAN = /internal const bool JevDraftsOnPlan = true;/.test(
  readFileSync(resolve(repoRoot, 'Semanticus.Engine', 'Composer', 'JevDraftBuilder.cs'), 'utf8'));
const HONESTY_BANNED = [
  { re: /Jev is not sure enough to choose/, why: 'the old line, said whether or not Jev was asked; DraftCopy.JevNotSure is the one line, said only when Jev was asked and sure of nothing' },
  { re: /Tell Jev more/, why: 'the button is Add more words (composer contract), and the words it adds can be drafted by the word match as well as by Jev' },
  // "Jev drafts the steps" (contract 10.7's approved Pro line) is true while Jev drafts on the Plan door, and false the
  // moment that committed switch is off, so the ban follows the switch rather than a comment (26 Sep).
  ...(JEV_DRAFTS_ON_PLAN ? [] : [{ re: /Jev drafts the steps/, why: 'JevDraftBuilder.JevDraftsOnPlan is off: Pro lists the steps that match your words, and Jev drafts nothing on the Plan' }]),
  { re: /Jev(?:\\)?'s drafts need Pro/, why: 'say what Pro gives (contract 10.7), not a line about whose drafts need what' },
  { re: /Jev is ready on the Plan/, why: 'the Pro page shows Jev\'s real state from the engine, never a hardcoded ready' },
  { re: /<dt>Jev<\/dt><dd>On<\/dd>/, why: 'the Pro page shows Jev\'s real state from the engine, never a hardcoded On' },
  { re: /approval step is\s*["']?\s*\+?\s*["']?\s*not built yet/, why: 'approve is built; point at it' },
  { re: /Custom instructions on a step are not built yet/, why: 'format 3 reads instructions; tell a version 2 file to upgrade' },
  { re: /Upgraded (?:the )?workflow[^"\n]*to version 2["']/, why: 'the receipt names the version the file was upgraded TO' },
  { re: /'your assistant' : 'a person'/, why: 'a saved report\'s producer is an op or workflow name, never a person' },
  { re: /Workflows, New, or compose_task[^"\n]*carries its own checks/, why: 'only a draft runs its own checks; New in Workflows opens steps in Author for you to save' },
  // m11/finish-2: since cf1b8539 the steps New in Workflows describes carry their templates' checks, and the file it
  // opens hands Semanticus's steps to Semanticus, so the old "no checks" and "cannot hand a step" lines are false.
  { re: /New turns the words into steps in Author, without checks/, why: 'described steps carry their templates\' checks since cf1b8539 (C7)' },
  { re: /cannot hand a step to Semanticus yet/, why: 'the described file is format 3 and gives such a step to Semanticus (N11)' },
  // The engine files every change line under the Plan item it serves (LocalEngine.Plan.cs), so a set is items today.
  { re: /a set will become items on the plan/, why: 'change sets are filed into Plan items now (N4)' },
];
const honestyRoots = [
  { dir: resolve(extensionRoot, 'src'), ext: new Set(['.ts', '.tsx']) },
  { dir: resolve(extensionRoot, 'webview/src'), ext: new Set(['.ts', '.tsx']) },
  { dir: resolve(extensionRoot, 'tools/uishot'), ext: new Set(['.mjs']), skip: /node_modules/ },
  { dir: resolve(repoRoot, 'Semanticus.Engine'), ext: new Set(['.cs']), skip: /[\\/](?:bin|obj)[\\/]/ },
];
const honestyFailures = [];
let honestyScanned = 0;
for (const root of honestyRoots) {
  for (const file of walkFiles(root.dir, root.ext)) {
    if (root.skip && root.skip.test(file)) continue;
    honestyScanned++;
    const lines = readFileSync(file, 'utf8').split(/\r?\n/);
    const text = lines.join('\n');
    for (const { re, why } of HONESTY_BANNED) {
      const m = re.exec(text);
      if (!m) continue;
      const line = text.slice(0, m.index).split('\n').length;
      honestyFailures.push(`${relative(repoRoot, file).replaceAll('\\', '/')}:${line}: "${m[0]}" (${why})`);
    }
  }
}
// The release notes are copy a person reads too (m11/finish-2, C7): the same scan over CHANGELOG.md, with its line
// wraps read as spaces so a phrase split across two lines is still found.
const CHANGELOG_BANNED = [
  { re: /steps described in Workflows carry none/, why: 'described steps carry their templates\' checks since cf1b8539 (C7)' },
  { re: /finds their steps, but not the checks each starter ran/, why: 'described steps carry the checks the parked starters ran (C7)' },
];
{
  const changelog = readFileSync(resolve(repoRoot, 'CHANGELOG.md'), 'utf8').replace(/\r?\n\s*/g, ' ');
  honestyScanned++;
  for (const { re, why } of [...HONESTY_BANNED, ...CHANGELOG_BANNED]) {
    const m = re.exec(changelog);
    if (m) honestyFailures.push(`CHANGELOG.md: "${m[0]}" (${why})`);
  }
}
assert.ok(honestyScanned > 200, `the honesty scan read only ${honestyScanned} files, so it proves nothing`);
assert.deepEqual(honestyFailures, [], `Copy that claims something the product does not do:\n${honestyFailures.join('\n')}`);

// ---- the audit trail's op name (D-209 / UX-02 / UX-06) -----------------------------------------------
// The Edits audit trail rows were rendering the engine's own operation id (apply_plan). Every operation id
// is snake_case, so one test over the whole real surface proves no id can reach a person.
assert.equal(typeof copyModule.opLabel, 'function', 'copy.ts must export the audit trail op labeller');
// "Plan" now names the living plan, so a set of edits applied together is never called one (Kane, 21 Sep).
assert.equal(copyModule.opLabel('apply_plan'), 'Changes applied');
assert.equal(copyModule.opLabel('blame_value'), 'What moved this number?');
assert.equal(copyModule.opLabel(''), 'An edit');
for (const name of operationNames) {
  const shown = copyModule.opLabel(name);
  assert.doesNotMatch(shown, /_/, `the audit trail would show an engine id: ${name} -> ${shown}`);
  assert.notEqual(shown, name, `the audit trail would show the raw id ${name}`);
}

// ---- the publish safety check, in plain words (Kane, 2026-09-12) -------------------------------------
// A record of a publish that went ahead past a red check keeps its destination in ONE place: the sentence the
// engine wrote into the record's summary. The record's machine-readable evidence carries the check result and
// never the endpoint, so that sentence has to be read back apart to say the same thing in plain words. Two
// generations of it exist on real models and both must be understood: the wording Kane read off his own audit
// trail in July is still stored there and cannot be rewritten after the fact.
assert.equal(typeof copyModule.readSafetyCheckOverride, 'function', 'copy.ts must export the red-check summary reader');
const julyRecord = copyModule.readSafetyCheckOverride(
  'gate RED (19 blocking BPA error(s)): override accepted to deploy to powerbi://api.powerbi.com/v1.0/myorg/ws/Sales');
assert.ok(julyRecord, 'a record already sitting in a real audit trail must still be understood');
assert.equal(julyRecord.kind, 'publish');
assert.equal(julyRecord.problems, '19 blocking problems.');
assert.equal(julyRecord.destination, 'powerbi://api.powerbi.com/v1.0/myorg/ws/Sales');
const todayRecord = copyModule.readSafetyCheckOverride(
  'Red safety check (2 errors to fix before this model can ship. Start with: X.). Published anyway with a written reason, to localhost:2/Blocked');
assert.ok(todayRecord, 'the wording written from now on must be understood too');
assert.equal(todayRecord.kind, 'publish');
assert.equal(todayRecord.destination, 'localhost:2/Blocked');
assert.equal(todayRecord.problems, '2 errors to fix before this model can ship. Start with: X.');
// A person publishes past a red check with no reason since 8 Oct (Kane D1, r1/publish-p2): the engine writes a third shape.
const warnedRecord = copyModule.readSafetyCheckOverride(
  'Published with warnings (2 errors to fix before this model can ship. Start with: X.), to localhost:2/Blocked');
assert.ok(warnedRecord, 'a publish with warnings and no reason must be understood as a red-check publish');
assert.equal(warnedRecord.kind, 'publish');
assert.equal(warnedRecord.destination, 'localhost:2/Blocked');
assert.equal(warnedRecord.problems, '2 errors to fix before this model can ship. Start with: X.');
const promoted = copyModule.readSafetyCheckOverride(
  'gate RED (19 blocking BPA error(s)): override accepted to promote Dev→Test');
assert.equal(promoted.kind, 'promote', 'a stage promotion past a red check is a different sentence');
assert.equal(promoted.destination, 'Dev→Test');
assert.equal(copyModule.readSafetyCheckOverride('Applied a 6-item change plan (2 renames).'), null,
  'an ordinary summary is not a red-check publish');
assert.equal(copyModule.readSafetyCheckOverride(undefined), null, 'a record with no summary is not a red-check publish');
// The gloss is display only. It replaces engine vocabulary a person cannot read; it never edits the record.
assert.equal(typeof copyModule.plainProblems, 'function', 'copy.ts must export the plain wording for what the check found');
assert.doesNotMatch(copyModule.plainProblems('19 blocking BPA error(s)'), /BPA/, 'the old engine wording must not reach a person');
assert.equal(copyModule.plainProblems('1 blocking BPA error(s)'), '1 blocking problem.');
assert.equal(copyModule.plainProblems('3 blocking BPA warning(s)'), '3 blocking problems.');
assert.equal(copyModule.plainProblems('2 errors and 41 warnings to fix before this model can ship.'),
  '2 errors and 41 warnings to fix before this model can ship.', 'wording that is already plain is left alone');

// ---- the cheat sheet's footer (D-211 / UX-04) --------------------------------------------------------
// The footer claimed every listed chord was a normal VS Code keybinding. Several are handled inside the
// panel itself, so the Keyboard Shortcuts editor cannot rebind them, and the footer has to say which.
assert.equal(typeof copyModule.PANEL_ONLY_CHORDS, 'string', 'copy.ts must export the panel-only chord list');
for (const chord of ['Ctrl+Alt+Z', 'Ctrl+Alt+Shift+Z', 'Ctrl+Alt+T', 'Shift+Alt+F']) {
  assert.ok(copyModule.PANEL_ONLY_CHORDS.includes(chord), `the footer must name ${chord} as panel-only`);
}
assert.match(copyModule.PANEL_ONLY_CHORDS, /Ctrl\+Alt\+letter/, 'the footer must cover the tab jumps');
const shortcutsSource = readFileSync(resolve(extensionRoot, 'webview/src/shortcuts.tsx'), 'utf8');
assert.doesNotMatch(shortcutsSource, /Every Ctrl-chord here is a normal VS Code keybinding/,
  'the cheat sheet footer still claims every chord is rebindable');
assert.match(shortcutsSource, /PANEL_ONLY_CHORDS/, 'the cheat sheet footer must use the shared panel-only list');

// ---- the Connect toast (D-210) -----------------------------------------------------------------------
// The command writes a connection file. Nothing is connected at that moment: the assistant picks it up on
// its next start. The toast may not say connected.
const extensionSource = readFileSync(resolve(extensionRoot, 'src/extension.ts'), 'utf8');
assert.doesNotMatch(extensionSource, /'AI Assistant connected\./,
  'the Connect toast claims a connection on the strength of a file write');
// Connect from the header now ends in the say card (N2, hero16), not a toast. The card is where the same honesty lives.
const firstRunCard = readFileSync(resolve(extensionRoot, 'webview/src/firstrun.tsx'), 'utf8');
assert.match(firstRunCard, /Claude Code reads new tools when it starts/, 'the say card must say the assistant picks the connection up when it starts');
assert.doesNotMatch(firstRunCard, /aria-label="Your assistant is connected"/, 'the say card may not claim a connection on the strength of a file write');
assert.doesNotMatch(extensionSource, /Wrote the connection file for your assistant\. Nothing is connected yet/, 'the second-press toast is gone');

// ---- remaining live-sync copy (D-202 / GOV-06) ------------------------------------------------------
// These four shipped lines describe what the VS Code view receives, but the old wording also implied that
// the AI Assistant received a live push. The agent learns the edit on its next call, so each line must say so.
// Edit History (history.tsx) retired in slice 11, lane R2. The Plan's History reads the engine ledger and claims no
// live push to anyone, so it has no timing sentence to keep honest; its rows are worded by plan-history-model.ts.
const specSource = readFileSync(resolve(extensionRoot, 'webview/src/spec.tsx'), 'utf8');
const helpSource = readFileSync(resolve(extensionRoot, 'webview/src/help.tsx'), 'utf8');
for (const [name, source] of [['spec', specSource], ['help', helpSource]]) {
  assert.doesNotMatch(source, /both update here live|both land here: live|syncs here live|additions appear live with an attribution chip/i,
    `${name} still claims the AI Assistant receives a live push`);
}
// The Model Spec's SQL start reads a Fabric SQL endpoint with a Microsoft (Entra) sign-in (autogenerate_spec_from_fabric),
// and the Plan's SQL start says so. The card and the glossary name that one thing the same way, never "a SQL server".
// m11 join part 2, item 10 (30 Sep): the card, the Plan's empty-model start (LocalEngine.PlanBestPractice.cs StartsFor)
// and the help page say the SAME words. Before, the card said "the table and column structure ... with your Microsoft
// sign-in. No source rows are copied.", the Plan "the tables and keys ... into a draft", and the help page's lead (which the
// router quotes) said "Nothing on the plan itself points at it" although the Plan's From a SQL source start opens it.
const SQL_START_WORDS = 'Read the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept.';
const cardDetail = /<WizardChoice title="Draft from a SQL source" detail="([^"]*)"/.exec(specSource);
assert.ok(cardDetail, 'the Model Spec has its Draft from a SQL source card');
assert.equal(cardDetail[1], SQL_START_WORDS, 'the Model Spec card says the SQL start words');
const bestPracticeSource = readFileSync(resolve(extensionRoot, '..', 'Semanticus.Engine', 'Plan', 'LocalEngine.PlanBestPractice.cs'), 'utf8');
const planStart = /Id = "sql", Label = "From a SQL source",\s*Line = "([^"]*)"/.exec(bestPracticeSource);
assert.ok(planStart, "the Plan's empty-model start From a SQL source is where it was");
assert.equal(planStart[1], `${SQL_START_WORDS} Check the draft, then build it in one step.`, "the Plan's SQL start says the card's words, then what happens next");
assert.ok(helpSource.includes('Drafting from a SQL source reads the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept.'),
  'the help page says the same words');
assert.doesNotMatch(helpSource, /Nothing on the plan itself points at it/, "the help page denies the Plan's From a SQL source start");
assert.match(helpSource, /the Plan's From a SQL source start opens it too/, "the help page names the Plan's way into the Model Spec");
for (const [name, source] of [['spec', specSource], ['help', helpSource]]) {
  assert.doesNotMatch(source, /\bSQL server\b/, `${name} calls the SQL endpoint "a SQL server"`);
}
// The copy layer's canonical term for the assistant is "your assistant" (T254). spec.tsx is
// owned by its feature lane and still says "AI Assistant", so the honesty check accepts either spelling; what
// it will not accept is a surface that drops the timing sentence entirely.
const copySource = readFileSync(resolve(extensionRoot, 'webview/src/copy.ts'), 'utf8');
const seesOnNextCall = /(?:AI Assistant|your assistant) sees.*next call/i;
assert.match(specSource, seesOnNextCall, 'spec must state when the assistant sees a change');
assert.match(helpSource, seesOnNextCall, 'help must state when the assistant sees a change');
assert.match(copySource, seesOnNextCall, 'the shared assistant-sync sentence must state when the assistant sees a change');
// The Help slide-over footer that quoted the shared sentence left on 8 Oct 2026 (it also said MCP); help keeps no copy of it.
assert.doesNotMatch(helpSource, /ASSISTANT_SYNC_COPY/, 'help must not carry its own copy of the assistant-sync sentence');
// The two files this lane owns say "your assistant". The only permitted survivals are the literal VS Code command
// titles from package.json, which a person has to read back off their own command palette.
for (const [name, source] of [['copy.ts', copySource], ['help.tsx', helpSource]]) {
  const withoutCommandTitles = source
    .replaceAll('Semanticus: Connect AI Assistant', '')
    .replaceAll('Install or Update Assistant Skills', '');
  assert.doesNotMatch(withoutCommandTitles, /\bAI Assistant\b/,
    `${name} must call the assistant "your assistant", not "AI Assistant"`);
}

// The MCP server's one-time instructions are also shipped copy. They must describe the real timing: the VS Code
// view updates live, while the AI Assistant learns about the edit on its next call (D-203 / GOV-06).
const programSource = readFileSync(resolve(repoRoot, 'Semanticus.Engine/Program.cs'), 'utf8');
assert.doesNotMatch(programSource, /every edit is undoable and broadcast to both/i,
  'server instructions still claim a live push to the AI Assistant');
assert.match(programSource, /VS Code view updates at once/i,
  'server instructions must say the VS Code view updates live');
assert.match(programSource, /the AI Assistant sees the change on its next call/i,
  'server instructions must state when the AI Assistant sees a change');

// ---- readiness wording (D-108 / AIR-06) -------------------------------------------------------------
// The readiness gate accepts a score that holds when findings do not increase, so these surfaces cannot promise
// that the grade will improve or move. They should describe the check's real result instead.
// The featured "Make the model AI-ready" card that carried this copy was cut with the scenario catalog (lane W2,
// contract 21.1 rows 16 and 17), so nothing on Workflows describes the readiness score now; the ban still holds.
const workflowSource = ['workflows.tsx', 'workflowdesign.tsx', 'workflowforms.tsx', 'workflow-run.tsx', 'workflow-rules.tsx', 'workflows-model.ts']
  .map((f) => readFileSync(resolve(extensionRoot, 'webview/src', f), 'utf8')).join('\n');
assert.doesNotMatch(workflowSource, /grade shows the improvement|show the grade moving/i,
  'AI-readiness copy promises a grade improvement the gate does not require');

// ---- the vocabulary merge (Kane, 21 September 2026) --------------------------------------------------
// One idea, one word. Each family below has a winner and the words it beat; the losers may not appear in
// anything a person reads, on either door. The audit that found them is docs/redesign/jev-simplification-
// vocabulary.md and the names are docs/redesign/ui-hierarchy.md. Wire ids, operation names, CSS classes and
// test ids are NOT copy and are never touched, so the walk below reads display strings only and the
// allow-list below carries the handful of places where a losing word is genuinely a different thing.
const MERGED_VOCABULARY = [
  { family: 'version', winner: 'version (qualified as "saved version" or "published version")', forbidden: [
    /\brestore[- ]points?\b/i, /\bcheckpoints?\b/i, /\bsave[- ]points?\b/i, /\bsnapshots?\b/i, /\banchors?\b/i] },
  { family: 'report', winner: 'report', forbidden: [
    /\bsaved reports?\b/i, /\bevidence\b/i, /\bartifacts?\b/i, /\bdigests?\b/i] },
  // "as a plan" is the phrase the absence list missed. Four buttons and a guide sentence still offered to
  // review a set of edits "as a plan" on 22 September, and every one of them opened Pending changes. A set
  // of edits is never a plan, so the PHRASE is forbidden and not only the bare noun.
  { family: 'changes', winner: 'changes (never "plan": Plan is the living plan now)', forbidden: [
    /\bproposed\b/i, /\bproposals?\b/i, /\bchange plans?\b/i, /\bstaged\b/i, /\bstage it now\b/i, /\bbatch(?:es)?\b/i,
    /\bas a plan\b/i] },
  { family: 'check', winner: 'check', forbidden: [
    /\bgates?\b/i, /\bsafety checks?\b/i, /\breadiness gate\b/i, /\btests?\b/i] },
  { family: 'check against SQL', winner: 'check against SQL', forbidden: [
    /\bcompare with source\b/i, /\bsource comparison\b/i, /\breconcil\w*\b/i] },
  { family: 'Model notes', winner: 'Model notes', forbidden: [/\bprimers?\b/i, /\bmodel orientation\b/i] },
  { family: 'Publish', winner: 'Publish', forbidden: [
    /\bdeploy\w*\b/i, /\bships?\b/i, /\bshipp\w+\b/i, /\bgo live\b/i, /\bpush(?:es|ed)?\b/i] },
  { family: 'AI readiness', winner: 'AI readiness', forbidden: [
    /\bAI understanding\b/i, /\bready for AI\b/i, /\bprep[- ]for[- ]AI\b/i, /\bAI-readiness\b/i] },
  { family: 'finding', winner: 'finding (under Model quality)', forbidden: [
    /\bviolations?\b/i, /\bbest practice analyzer\b/i] },
  { family: 'workflow', winner: 'workflow', forbidden: [
    /\bplaybooks?\b/i, /\bscenarios?\b/i, /\blearned workflows?\b/i] },
  { family: 'Profile', winner: 'account profile only ("team rules", "performance breakdown")', forbidden: [
    /\bproject profiles?\b/i, /\bworkflow profiles?\b/i, /\bDAX profile\b/i] },
];
// The only places a losing word survives, each because it names a DIFFERENT thing.
const VOCABULARY_ALLOWED = [
  // Sentences already written into a model's hash-chained audit trail. copy.ts reads them back; it may never
  // reword them, because the stored record cannot be rewritten after the fact.
  { text: 'gate RED (' }, { text: '): override accepted to deploy to ' },
  { text: '): override accepted to promote ' }, { text: 'Red safety check (' },
  { text: '). Published anyway with a written reason, to ' },
  { text: '). Moved to the next stage anyway with a written reason, from ' },
  // Git's own operation names on the Fabric Git panel. Sending a folder to git is not publishing a model.
  { text: 'Pull', files: ['deploy.tsx'] }, { text: 'Push', files: ['deploy.tsx'] },
  { text: 'confirm-gated push', files: ['deploy.tsx'] },
  { text: 'Commit pushes the workspace to git' }, { text: 'push failed' }, { text: 'pull failed' },
  // The engine writes this sentence on a refused live publish and deploy.tsx matches it to decide whether
  // to ask for a written reason (LocalEngine.LivePublish.cs:176). Rewording it here would break that match.
  { text: 'blocked by the deploy gate', files: ['deploy.tsx'] },
  // A point-in-time read of two tables, not a saved version. Kane approved this footer word for word
  // and the merge does not reach the word in this sense (tests-reading-rules.test.mjs pins it).
  { text: 'matching snapshots were not confirmed', files: ['tests-model.ts'] },
  // The connection role: which live model answers queries. That is not a saved check, and the Connections
  // copy is another lane's file set.
  { text: 'Tests', files: ['contextbar.tsx'] },
];
const VOCABULARY_ALLOWED_PATTERNS = [
  /\btests? (?:and queries|model|connection|contexts?)\b/gi,
  /\b(?:queries and tests|tests, and publishing|choose where tests run)\b/gi,
  /\blast tests?\b/gi, /\bthe test (?:finished|worked|failed|is true)\b/gi,
  /\b(?:to|and) test\b/gi, /\btest (?:it|and save|the measure|this expression)\b/gi, /\bexpression to test\b/gi,
  /\bdevelopment,? (?:then )?test,? (?:and |then )?production\b/gi,
  /\bdeployment pipelines?\b/gi,                      // Microsoft's own product name
  /\bPublish, then test\b/g,                         // the one-press label on the Copilot test page (Kane's brief, 17 Oct 2026)
  /\bCopilot tests?\b/gi,                            // Kane's name for the Review page that asks how Copilot answers (15 Oct 2026), not a saved check
  /\bcolumn profiles?\b|\bprofile (?:columns|failed|details|progress|needs)\b|\bto profile\b/gi,
];
const mergeRoot = resolve(extensionRoot, 'webview/src');
// pro.tsx, propreview.tsx, help.tsx and the plan pages belong to other lanes in this pass. mstdlib.ts is
// Microsoft's bundled M-language reference data, not our copy.
const MERGE_SKIP = new Set(['pro.tsx', 'propreview.tsx', 'help.tsx', 'mstdlib.ts']);
const mergeFiles = readdirSync(mergeRoot, { withFileTypes: true })
  .filter((entry) => entry.isFile() && ['.ts', '.tsx', '.mjs'].includes(extname(entry.name)))
  .filter((entry) => !entry.name.endsWith('.d.ts') && !entry.name.endsWith('.d.mts'))
  .filter((entry) => !MERGE_SKIP.has(entry.name) && !entry.name.startsWith('plan'))
  .map((entry) => join(mergeRoot, entry.name));
assert.ok(mergeFiles.length > 80, `the vocabulary walk found too few webview files (${mergeFiles.length})`);

const vocabularyFailures = [];
const mergeCopy = mergeFiles.flatMap((file) => collectDisplayCopy(file));
assert.ok(mergeCopy.length > 5000, `the vocabulary walk collected too little copy (${mergeCopy.length} strings)`);
// The repo's own skills (.claude/skills) are private and left out of the public source mirror. The private repo
// (marked by its mirror manifest) always reads them, so a missing skill still fails there.
const privateRepo = existsSync(resolve(repoRoot, 'tools/release/mirror-manifest.json')); // mirror-optional: marks the private repo
const privateSkills = privateRepo ? ['semanticus', 'optimize-dax', 'ai-ready', 'interview-model', 'model-pr', 'distill-workflow', 'curate-knowledge'] : [];
for (const skill of privateSkills) {
  const file = resolve(repoRoot, '.claude/skills', skill, 'SKILL.md');
  // Operation names live in code spans and stay exactly as the engine spells them; the prose around them
  // is what a person and their assistant read, and that is what the merge governs.
  const prose = readFileSync(file, 'utf8').replace(/```[\s\S]*?```/g, ' ').replace(/`[^`\n]*`/g, ' ');
  mergeCopy.push({ where: `.claude/skills/${skill}/SKILL.md`, base: `${skill}/SKILL.md`, text: prose });
}
for (const entry of mergeCopy) {
  const text = stripAllowedVocabulary(entry);
  for (const { family, winner, forbidden } of MERGED_VOCABULARY) {
    for (const pattern of forbidden) {
      const hit = pattern.exec(text);
      if (hit) vocabularyFailures.push(`${entry.where}: "${hit[0]}" was merged into ${family} -> say ${winner}`);
    }
  }
}
// BPA earns one appearance per surface, and only beside the plain name it abbreviates.
for (const file of mergeFiles) {
  const acronym = collectDisplayCopy(file).filter((entry) => /\bBPA\b/.test(entry.text));
  if (acronym.length === 0) continue;
  if (acronym.length > 1) vocabularyFailures.push(`${acronym[1].where}: BPA appears more than once on this surface`);
  if (!/Model quality \(BPA\)/.test(acronym[0].text)) {
    vocabularyFailures.push(`${acronym[0].where}: BPA may only appear as "Model quality (BPA)"`);
  }
}
assert.deepEqual(vocabularyFailures, [], `Merged vocabulary violations:\n${vocabularyFailures.join('\n')}`);

// ---- three names, and the two places the Plan replaced (Kane, 24 September 2026) --------------------
// Six names for one idea (workflow, playbook, job, team setup, template, profile) became three: a WORKFLOW
// is yours, in the list; a STARTER is a copy to start from; TEAM RULES are what a team requires (contract
// 21.7). The Overview and Edit History were retired into the Plan (contract 11.1 and 11.2), so no copy may
// send anyone to them. Engine identifiers keep their names (list_workflow_profiles, workflow-templates/),
// so code spans and single-token ids are not copy and are never read here.
//
// This walk is wider than the merge above, because these words were said on both doors: every webview
// file (help, the Plan pages and the Pro pages included), the extension host, the assistant pack's README,
// the seven packaged skills, and the MCP door's own words (tool descriptions and activity labels).
const THREE_NAMES = [
  { say: 'team rules', pattern: /\bteam setups?\b/i },
  { say: 'team rules', pattern: /\b(?:project|workflow|team) profiles?\b/i },
  { say: 'team rules', pattern: /\bgovernance\b/i },
  { say: 'workflow', pattern: /\bplaybooks?\b/i },
  // A model's jobs on Connections (Editing, Tests and queries, Publish to) are a different thing.
  { say: 'workflow (or the plain task it is)', pattern: /\bjobs?\b/i, except: /^connectionshub/ },
  { say: 'starter', pattern: /\bworkflow templates?\b|\btemplate shelf\b/i },
  // Calendars, rules and format strings have templates of their own; only Workflows' own files are held to it.
  { say: 'starter or team rules', pattern: /\btemplates?\b|\bprofiles?\b/i, only: /^workflow/ },
  { say: 'the Plan', pattern: /\bOverview\b/ },
  { say: "the Plan's History", pattern: /\bEdit History\b/i },
];
// Where a retired name is still the right words, each for a reason that will stay true.
const THREE_NAMES_ALLOWED = [
  // The What changed card tells an existing person where the old places went (contract 10); naming them
  // is the whole point of the card.
  { base: 'plan-guide.tsx', text: 'The Overview is gone.' },
  { base: 'plan-guide.tsx', text: 'Edit History moved into the Plan.' },
  // A heading inside the generated model documentation, and the first of Model notes' six fixed sections.
  { base: 'docrender.ts', text: '## Overview' },
  { base: 'McpTools.cs', text: 'Overview, Business context' },
];
const namesCopy = [];
for (const file of readdirSync(mergeRoot, { withFileTypes: true })) {
  if (!file.isFile() || !['.ts', '.tsx', '.mjs'].includes(extname(file.name))) continue;
  if (file.name.endsWith('.d.ts') || file.name === 'mstdlib.ts') continue;
  namesCopy.push(...collectDisplayCopy(join(mergeRoot, file.name)));
}
for (const file of walkFiles(resolve(extensionRoot, 'src'), new Set(['.ts']))) namesCopy.push(...collectDisplayCopy(file));
namesCopy.push(...collectDisplayCopy(resolve(extensionRoot, 'scripts/assistant-pack.mjs')));
// The shipped workflows and starters are read on both doors (list_workflows, the New panel), so their whole
// prose is copy: the front matter (title, description, whenToUse) and every step. Code spans and fenced
// blocks are identifiers and examples, as in the skills. The base is `stock/<file>` so the Workflows-only
// template and profile rule does not apply to a starter's own words about calendars or rules.
for (const root of ['Semanticus.Engine/workflows', 'Semanticus.Engine/workflow-templates']) {
  for (const file of walkFiles(resolve(repoRoot, root), new Set(['.md']))) {
    const prose = readFileSync(file, 'utf8').replace(/```[\s\S]*?```/g, ' ').replace(/`[^`\n]*`/g, ' ');
    namesCopy.push({ where: relative(repoRoot, file).replaceAll('\\', '/'), base: `stock/${basename(file)}`, text: prose });
  }
}
for (const skill of privateSkills) {
  const prose = readFileSync(resolve(repoRoot, '.claude/skills', skill, 'SKILL.md'), 'utf8')
    .replace(/```[\s\S]*?```/g, ' ').replace(/`[^`\n]*`/g, ' ');
  namesCopy.push({ where: `.claude/skills/${skill}/SKILL.md`, base: `${skill}/SKILL.md`, text: prose });
}
// The MCP door: every C# string literal with a space in it, in every McpTools file, outside comments.
// Those are tool and parameter descriptions, activity labels and refusals, all read by an assistant or shown
// in Live activity. Paths and ids in backticks are identifiers, as in the skills.
const engineRoot = resolve(repoRoot, 'Semanticus.Engine');
// LocalEngine.Workflows.cs holds the parked starters' teaching sentence, which start_workflow and get_workflow
// return on both doors, so its words are held to the same names.
const mcpFiles = readdirSync(engineRoot).filter((name) => /^McpTools.*\.cs$/.test(name) || name === 'LocalEngine.Workflows.cs');
assert.ok(mcpFiles.length > 5, `the MCP tool files could not be found (${mcpFiles.length})`);
let mcpLiterals = 0;
for (const name of mcpFiles) {
  readFileSync(join(engineRoot, name), 'utf8').split('\n').forEach((line, index) => {
    if (/^\s*\/\//.test(line)) return;
    for (const m of line.matchAll(/\$?"((?:[^"\\]|\\.)*)"/g)) {
      if (!/\S\s\S/.test(m[1])) continue;
      mcpLiterals += 1;
      namesCopy.push({ where: `Semanticus.Engine/${name}:${index + 1}`, base: name, text: m[1].replace(/`[^`]*`/g, ' ') });
    }
  });
}
assert.ok(mcpLiterals > 500, `the MCP door's words could not be read (${mcpLiterals} literals)`);
assert.ok(namesCopy.length > mergeCopy.length, `the three-names walk read less than the merge walk (${namesCopy.length})`);
const namesFailures = [];
for (const entry of namesCopy) {
  let text = entry.text;
  for (const allowed of THREE_NAMES_ALLOWED) if (allowed.base === entry.base) text = text.split(allowed.text).join(' ');
  for (const { say, pattern, except, only } of THREE_NAMES) {
    if (except?.test(entry.base) || (only && !only.test(entry.base))) continue;
    const hit = pattern.exec(text);
    if (hit) namesFailures.push(`${entry.where}: "${hit[0]}" -> say ${say}`);
  }
}
assert.deepEqual(namesFailures, [], `Three names and retired places:\n${namesFailures.join('\n')}`);

console.log(`copy rules passed across ${sourceFiles.length} UI source files, package metadata, and built-in workflow catalog copy`);
console.log(`merged vocabulary held across ${mergeFiles.length} owned webview files and 7 packaged skills (${mergeCopy.length} strings)`);
console.log(`three names held across ${namesCopy.length} strings on both doors (${mcpLiterals} from the MCP tools and the workflow engine)`);

function walk(directory) {
  return walkFiles(directory, new Set(['.ts', '.tsx', '.js']));
}

function walkFiles(directory, extensions) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return walkFiles(path, extensions);
    return extensions.has(extname(entry.name)) ? [path] : [];
  });
}

function visit(node, tree, file) {
  if (isTextNode(node)) {
    const text = node.text;
    const uiContext = isUiContext(node);
    if (uiContext) userVisibleStrings.push({ where: location(tree, file, node), text });
    checkText(location(tree, file, node), text, false, uiContext);
  }
  ts.forEachChild(node, (child) => visit(child, tree, file));
}

function isTextNode(node) {
  return ts.isStringLiteralLike(node)
    || ts.isTemplateHead(node)
    || ts.isTemplateMiddle(node)
    || ts.isTemplateTail(node)
    || ts.isJsxText(node);
}

function isUiContext(node) {
  if (ts.isJsxText(node)) return true;
  for (let current = node.parent; current; current = current.parent) {
    if (ts.isJsxAttribute(current)) return visibleJsxAttributeNames.has(current.name.text);
    if (ts.isPropertyAssignment(current) && visiblePropertyNames.has(propertyName(current.name))) return true;
    if (ts.isCallExpression(current) && current.arguments[0] && contains(current.arguments[0], node)) {
      const name = current.expression.getText();
      return /(?:showErrorMessage|showInformationMessage|showWarningMessage|showQuickPick)$/.test(name);
    }
    if (ts.isSourceFile(current) || ts.isFunctionLike(current)) break;
  }
  return false;
}

function contains(parent, child) {
  return child.pos >= parent.pos && child.end <= parent.end;
}

function propertyName(node) {
  return ts.isIdentifier(node) || ts.isStringLiteralLike(node) ? node.text : '';
}

function checkText(where, text, marketplaceKeyword = false, uiContext = false) {
  if (text.includes('\u2014')) failures.push(`${where}: em dash`);
  if (/\uFE0F/u.test(text) || /[\u{1F300}-\u{1FAFF}]/u.test(text)) failures.push(`${where}: colorful emoji`);
  // An assistant selector must name the actual client whose files will be installed.
  const assistantChoice = text === 'Claude Code' && where.startsWith('Semanticus.VSCode/src/extension.ts:');
  // The Studio's driver bar, note strip and cursor name the product "Claude", the name Kane chose (10 Oct 2026, 1.4 contract
  // section 0 rule 2 and section 6: "Added. Tell Claude go."). Everywhere else still says AI Assistant.
  // The driver bar folded into the Plan header in 1.5 (m15/calm-build), so its words now live in plan-calm-model.ts too.
  const claudeSurface = where.startsWith('Semanticus.VSCode/webview/src/studio/') || where.startsWith('Semanticus.VSCode/webview/src/plan-calm-model.ts:');
  // The first-run choice names the product it connects (Connect Claude Code, Open Claude Code in this folder): a nominative reference, as the command title is.
  const firstRunCard = where.startsWith('Semanticus.VSCode/webview/src/firstrun.tsx:');
  if (!assistantChoice && !claudeSurface && !firstRunCard && /\bclaude\b/i.test(text) && (marketplaceKeyword || /\s/.test(text))) failures.push(`${where}: use AI Assistant, not Claude`);
  if (/\bAI assistant\b/.test(text)) failures.push(`${where}: capitalize AI Assistant`);
  // Golden rule 2: the UI door is pushed a change live; the agent door is never sent anything. No string a
  // person reads may claim the two are fed the same way (D-202, D-210).
  if (/\bboth doors\b/i.test(text)) failures.push(`${where}: claims the change reaches both doors the same way`);
  if (/\bAI Assistant\s+live\b/i.test(text)) failures.push(`${where}: claims a live push to the AI Assistant`);
  if (uiContext) {
    const raw = text.match(/\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b/g) ?? [];
    for (const token of new Set(raw)) failures.push(`${where}: raw operation or enum ${token}`);
    if (/SEMANTICUS_[A-Z0-9_]+/.test(text)) failures.push(`${where}: environment variable`);
    if (/~\/\.semanticus/.test(text)) failures.push(`${where}: config or license dotfile`);
    if (/verify-at-build/.test(text)) failures.push(`${where}: build TODO`);
  }
}

function location(tree, file, node) {
  const { line, character } = tree.getLineAndCharacterOfPosition(node.getStart(tree));
  return `${relative(repoRoot, file).replaceAll('\\', '/')}:${line + 1}:${character + 1}`;
}

function walkManifestConfiguration(configuration, path) {
  if (!configuration) return;
  if (Array.isArray(configuration)) {
    configuration.forEach((item, index) => walkManifestConfiguration(item, `${path}[${index}]`));
    return;
  }
  if (typeof configuration !== 'object') return;
  for (const [key, value] of Object.entries(configuration)) {
    if (['title', 'description', 'markdownDescription'].includes(key) && typeof value === 'string') {
      checkText(`${path}.${key}`, value);
    }
    if (value && typeof value === 'object') walkManifestConfiguration(value, `${path}.${key}`);
  }
}

// Display copy in one file: JSX text, the visible properties and attributes the walk above already trusts,
// and any literal that reads as a sentence (an inner space). A single-token literal is almost always an id,
// a route or an enum, so it stays out; class lists and generated markup stay out for the same reason.
function collectDisplayCopy(file) {
  const source = readFileSync(file, 'utf8');
  const kind = extname(file) === '.tsx' ? ts.ScriptKind.TSX
    : extname(file) === '.mjs' || extname(file) === '.js' ? ts.ScriptKind.JS
      : ts.ScriptKind.TS;
  const tree = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, kind);
  const base = file.replaceAll('\\', '/').split('/').pop();
  const out = [];
  (function step(node) {
    if (isTextNode(node) && !isClassList(node)) {
      const text = node.text;
      const sentence = isUiContext(node) || /\S\s\S/.test(text);
      if (sentence && !/<[/a-zA-Z!]|="/.test(text)) out.push({ where: location(tree, file, node), base, text });
    }
    ts.forEachChild(node, step);
  })(tree);
  return out;
}

function isClassList(node) {
  for (let current = node.parent; current; current = current.parent) {
    if (ts.isJsxAttribute(current)) return current.name.text === 'className' || current.name.text === 'class';
    if (ts.isPropertyAssignment(current)) return propertyName(current.name) === 'className';
    if (ts.isSourceFile(current) || ts.isFunctionLike(current)) break;
  }
  return false;
}

/** The entry's text with every allowed survival of a merged word blanked out. */
function stripAllowedVocabulary(entry) {
  let text = entry.text;
  for (const allowed of VOCABULARY_ALLOWED) {
    if (allowed.files && !allowed.files.includes(entry.base)) continue;
    text = text.split(allowed.text).join(' ');
  }
  for (const pattern of VOCABULARY_ALLOWED_PATTERNS) text = text.replace(pattern, ' ');
  return text;
}

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}
