import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
// Edit History (history.tsx) retired in slice 11, lane R2 (Plan-heart contract 11.2). Saving and restoring a version
// on this computer are the Plan's History now; published versions and their rollback are Publish > Published, which
// already listed restore points and rolled back. These are the same boundaries, checked at their new homes.
const history = read('webview/src/plan-history.tsx');
const historyModel = read('webview/src/plan-history-model.ts');
const copy = read('webview/src/copy.ts');
const help = read('webview/src/help.tsx');
const app = read('webview/src/App.tsx');
const deploy = read('webview/src/deploy.tsx');
assert.match(history, /Save a version/, 'the local version boundary is one plain action on History');
assert.match(history, /\{supported && <button[^>]*data-testid="ph-save"/, 'Save a version shows only when the model can keep versions');
assert.doesNotMatch(history + historyModel, /NO_GIT_SENTENCE|ph-nogit|needs the model in a git folder/,
  'a model that is not in a git folder says nothing and shows no Save a version: undo still works and nobody asked for versions');
assert.match(history, /restoreHistoryCheckpoint', a\.hash, false, 'human'\)/, 'local restore must preview before confirmation');
assert.match(historyModel, /The model goes back to how it was then\./, 'the one restore question says what happens to the model');
assert.match(history, /Published versions are in Publish/, 'History names where the published boundary lives');
assert.match(history, /onGo\?\.\('deploy'\)/, 'and takes you there');
assert.doesNotMatch(history + historyModel, /AI Assistant/, 'History speaks of your assistant, never an AI Assistant brand');
assert.match(deploy, /listRestorePoints/, 'Publish > Published lists the published restore points');
assert.match(deploy, /rollbackPush|rollback_push|'rollbackPush'/, 'and rolls back');
assert.doesNotMatch(app, /label: ['"](Checkpoint|Recovery|Restore points)['"]/, 'R7 recovery must not create a top-level tab');
assert.doesNotMatch(app, /label: 'Edit History'/, 'and Edit History is not a tab of its own any more');

// ---- the red check, in plain words (Kane, 2026-09-12: "what does all this gate stuff mean?") ----
// Changes > History showed a stack of full-width red slabs under a small grey "PUBLISHED WITH A REASON",
// each one nothing but "Reason: <sentence someone typed months ago>". No date, no name, and nowhere on the
// tab did anything say what the check was or what red meant. The block now names the check, explains it
// once, and gives each publish one quiet row.
assert.match(copy, /Publishes that went ahead with a red check/,
  'the shared copy must carry the plain heading for the red-check block');
assert.match(copy, /Before a publish, Semanticus checks the model\./, 'the explainer must say when the check runs');
assert.match(copy, /Green means no blocking problem was found\./, 'the explainer must say what green means');
assert.match(copy, /Red means blocking problems remained/, 'the explainer must say what red means');
// Since slice 11 the block is one row per publish in the Plan's History (the mockup's Simplify column: "one row, the
// reason word for word"), titled from the shared copy and rewritten from the recorded summary.
assert.match(historyModel, /SAFETY_CHECK_COPY\.publishTitle/, 'a red-check publish row takes its title from the shared copy');
assert.match(historyModel, /readSafetyCheckOverride\(record\.summary\)/, 'an overridden publish row must be rewritten from the recorded summary');
assert.match(historyModel, /SAFETY_CHECK_COPY\.destinationLabel/, 'the destination must get its own line');
assert.match(historyModel, /return r\.overrideReason \? '"' \+ r\.overrideReason \+ '"' : SAFETY_CHECK_COPY\.noReason;/, 'the reason is quoted word for word, and a publish with no reason says so');
assert.match(historyModel, /isRedCheck = \(r: VerifiedEditRecord\) => \(!!r\.overrideReason \|\| r\.verdict === 'overridden'\) && PUBLISHES\.has\(r\.op\)/,
  'the red-check row lists publishes, not every recorded override');
assert.match(copy, /Published with a red check/, 'the audit row needs a plain title');
assert.match(copy, /The publish went ahead\./, 'the audit row must say the publish still happened');

// Nothing a person reads on this tab may be engine vocabulary. Walk the real string and JSX text nodes so a
// comment explaining the engine wording is not mistaken for copy.
// copy.ts is swept only over SAFETY_CHECK_COPY itself: the same file also holds the retired engine sentences
// as PATTERNS, because a record written in July still says "gate RED (…)" and the only way that record ever
// reads plainly is for the reader to recognise it. Those patterns are machinery, not copy.
const historyText = [textNodes(history, 'plan-history.tsx'), textNodes(historyModel, 'plan-history-model.ts')].flat().join('\n');
const safetyCopyText = textNodes(copy, 'copy.ts', 'SAFETY_CHECK_COPY').join('\n');
assert.ok(safetyCopyText.length > 200, 'the safety-check copy could not be found to sweep');
for (const [name, text] of [['History', historyText], ['the shared safety-check copy', safetyCopyText]]) {
  assert.doesNotMatch(text, /gate RED/i, `${name} must not print the engine word for a failed safety check`);
  assert.doesNotMatch(text, /\bBPA\b/, `${name} must not print the engine name of the model quality rules`);
  assert.doesNotMatch(text, /—/, `${name} copy must carry no em dash`);
}

// Help answers the question the tab raised, in the same words.
assert.match(copy, /What is the check before a publish\?/, 'the help must be able to ask what the check is');
assert.match(help, /SAFETY_CHECK_COPY\.helpQuestion/, 'the Changes help must carry the safety-check question');
assert.match(help, /SAFETY_CHECK_COPY\.explainer/, 'the help answer must reuse the shared explainer, not a second copy of it');
assert.match(copy, /A red check does not stop you\./, 'the help answer must say what a red check means for the person');

console.log('History durable recovery UI contract tests passed');

/**
 * Every string literal and every piece of JSX text in a source file: everything a person can read. Pass a
 * declaration name to sweep only that one const, when the rest of the file is machinery.
 */
function textNodes(source, name, only) {
  const tree = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true,
    name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  const found = [];
  (function walk(node, inside) {
    const here = inside
      || !only
      || (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.name.text === only);
    if (here && (ts.isStringLiteralLike(node) || ts.isTemplateHead(node) || ts.isTemplateMiddle(node)
      || ts.isTemplateTail(node) || ts.isJsxText(node))) found.push(node.text);
    ts.forEachChild(node, (child) => walk(child, here));
  })(tree, false);
  return found;
}

// ---- Colour level B (contract section 20): the who column carries the doer hue -----------------------------
// Who made a change is a doer mark: You in the You hue, your assistant in the assistant hue, the system in the
// Semanticus hue. The word stays; the colour only repeats it. Executed in plan-history.test.mjs; the mapping is here.
assert.match(historyModel, /return origin === 'agent' \? 'assistant' : origin === 'system' \? 'semanticus' : 'you';/,
  'agent is the assistant, system is Semanticus, anything else is you');
assert.match(history, /<span className=\{M\.doerClass\(who\)\}>\{M\.WHO_WORD\[who\]\}<\/span>/,
  'a row names its maker with the doer mark that matches the word it says');
{
  const styles = read('webview/src/styles.css');
  for (const who of ['engine', 'you', 'agent']) {
    assert.match(styles, new RegExp(`\\.sem-doer-${who}\\s*\\{[^}]*color:\\s*var\\(--sem-doer-${who}\\)`), `.sem-doer-${who} is --sem-doer-${who}`);
  }
}
