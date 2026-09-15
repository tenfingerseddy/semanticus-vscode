import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
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
  'semanticus.studioGoGroup', 'semanticus.studioGoTab',
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

// ---- the audit trail's op name (D-209 / UX-02 / UX-06) -----------------------------------------------
// The Edits audit trail rows were rendering the engine's own operation id (apply_plan). Every operation id
// is snake_case, so one test over the whole real surface proves no id can reach a person.
assert.equal(typeof copyModule.opLabel, 'function', 'copy.ts must export the audit trail op labeller');
assert.equal(copyModule.opLabel('apply_plan'), 'Plan applied');
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
assert.match(extensionSource, /Wrote the connection file/, 'the Connect toast must say a file was written');
assert.match(extensionSource, /Nothing is connected yet/, 'the Connect toast must say nothing is connected yet');

// ---- remaining live-sync copy (D-202 / GOV-06) ------------------------------------------------------
// These four shipped lines describe what the VS Code view receives, but the old wording also implied that
// the AI Assistant received a live push. The agent learns the edit on its next call, so each line must say so.
const historySource = readFileSync(resolve(extensionRoot, 'webview/src/history.tsx'), 'utf8');
const specSource = readFileSync(resolve(extensionRoot, 'webview/src/spec.tsx'), 'utf8');
const helpSource = readFileSync(resolve(extensionRoot, 'webview/src/help.tsx'), 'utf8');
assert.match(historySource, /it\.label \? opLabel\(it\.label\) : 'edited the model'/,
  'the live Edit History row must humanize its operation label');
for (const [name, source] of [['history', historySource], ['spec', specSource], ['help', helpSource]]) {
  assert.doesNotMatch(source, /both update here live|both land here: live|syncs here live|additions appear live with an attribution chip/i,
    `${name} still claims the AI Assistant receives a live push`);
}
// The copy layer's canonical term for the assistant is "your assistant" (T254). history.tsx and spec.tsx are
// owned by their feature lanes and still say "AI Assistant", so the honesty check accepts either spelling; what
// it will not accept is a surface that drops the timing sentence entirely.
const copySource = readFileSync(resolve(extensionRoot, 'webview/src/copy.ts'), 'utf8');
const seesOnNextCall = /(?:AI Assistant|your assistant) sees.*next call/i;
assert.match(historySource, seesOnNextCall, 'history must state when the assistant sees a change');
assert.match(specSource, seesOnNextCall, 'spec must state when the assistant sees a change');
assert.match(helpSource, seesOnNextCall, 'help must state when the assistant sees a change');
assert.match(copySource, seesOnNextCall, 'the shared assistant-sync sentence must state when the assistant sees a change');
assert.match(helpSource, /ASSISTANT_SYNC_COPY/, 'help must use the shared assistant-sync sentence, not its own copy of it');
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
const scenarioSource = readFileSync(resolve(extensionRoot, 'webview/src/workflowscenarios.tsx'), 'utf8');
const workflowSource = readFileSync(resolve(extensionRoot, 'webview/src/workflows.tsx'), 'utf8');
assert.doesNotMatch(`${scenarioSource}\n${workflowSource}`, /grade shows the improvement|show the grade moving/i,
  'AI-readiness copy promises a grade improvement the gate does not require');
assert.match(`${scenarioSource}\n${workflowSource}`, /readiness (?:score|result).*(?:held|improv)/i,
  'AI-readiness copy must describe the score as held or improved');

console.log(`copy rules passed across ${sourceFiles.length} UI source files, package metadata, and built-in workflow catalog copy`);

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
  if (!assistantChoice && /\bclaude\b/i.test(text) && (marketplaceKeyword || /\s/.test(text))) failures.push(`${where}: use AI Assistant, not Claude`);
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

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}
