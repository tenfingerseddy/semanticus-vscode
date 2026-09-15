import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const history = read('webview/src/history.tsx');
const copy = read('webview/src/copy.ts');
const help = read('webview/src/help.tsx');
const app = read('webview/src/App.tsx');
const deploy = read('webview/src/deploy.tsx');

assert.match(history, /Saved on this computer/, 'Edit History must name the local checkpoint boundary');
assert.match(history, /Published model · restore points/, 'Edit History must name the published restore boundary');
assert.match(history, /createHistoryCheckpoint[^\n]+false/, 'checkpoint creation must preview before commit');
assert.match(history, /restoreHistoryCheckpoint[^\n]+false/, 'local restore must preview before confirmation');
assert.match(history, /Nothing leaves this computer/, 'a local checkpoint must not imply remote publication');
assert.match(history, /Roll back published model/, 'the restore-point action must say which model it rolls back');
assert.doesNotMatch(history, /AI Assistant/, 'History speaks of your assistant, never an AI Assistant brand');
assert.match(history, /Rolling back changes the published model, not the copy on this computer/, 'published rollback must not imply a local-file restore');
assert.match(app, /restoreTarget=\{deployRestoreTarget\}/, 'the exact published restore point must route into Deploy');
assert.match(deploy, /const t = restoreTarget;/, 'Deploy must capture the directed restore target as a null-narrowed local');
assert.match(deploy, /setRestoreId\(found\.some\(\(p\) => p\.id === t\.id\)/, 'Deploy must focus the directed restore point');
assert.doesNotMatch(app, /label: ['"](Checkpoint|Recovery|Restore points)['"]/, 'R7 recovery must not create a top-level tab');

// ---- the red safety check, in plain words (Kane, 2026-09-12: "what does all this gate stuff mean?") ----
// Changes > History showed a stack of full-width red slabs under a small grey "PUBLISHED WITH A REASON",
// each one nothing but "Reason: <sentence someone typed months ago>". No date, no name, and nowhere on the
// tab did anything say what the check was or what red meant. The block now names the check, explains it
// once, and gives each publish one quiet row.
assert.match(copy, /Publishes that went ahead with a red safety check/,
  'the shared copy must carry the plain heading for the red-check block');
assert.match(copy, /Before a publish, Semanticus checks the model\./, 'the explainer must say when the check runs');
assert.match(copy, /Green means no blocking problem was found\./, 'the explainer must say what green means');
assert.match(copy, /Red means blocking problems remained/, 'the explainer must say what red means');
assert.match(history, /SAFETY_CHECK_COPY\.heading/, 'the block heading must come from the shared safety-check copy');
assert.match(history, /SAFETY_CHECK_COPY\.explainer/, 'the block must explain the check, not just stack up reasons');
assert.doesNotMatch(history, /Published with a reason</, 'the old unexplained heading must be gone');
// One record is one quiet row: a red-check marker, the date and who, then the person's own sentence quoted.
assert.match(history, /shortWhen\(r\.when, now\)/, 'a red-check row must show when the publish happened');
assert.match(history, /actorLabel\(r\.origin\)/, 'a red-check row must show who published');
assert.match(history, /SAFETY_CHECK_COPY\.pill/, 'a red-check row must carry the red-check marker');
// Two rows, the rest behind one control, so seven publishes are not seven red slabs.
assert.match(history, /const RED_CHECK_PREVIEW = 2;/, 'only the two most recent red-check publishes may show');
assert.match(history, /Show \$\{older\} older/, 'the rest must sit behind a Show N older control');
// The block is about the publish safety check, so an override recorded against something else does not
// belong under that heading. Those records keep their reason on the audit trail below.
assert.match(history, /r\.op === 'deploy_live' \|\| r\.op === 'deploy_stage'/,
  'the red-check block must list publishes, not every recorded override');
// The audit trail row for an overridden publish reads in plain words, with the destination on its own line.
assert.match(history, /readSafetyCheckOverride\(r\.summary\)/,
  'an overridden publish row must be rewritten from the recorded summary');
assert.match(copy, /Published with a red safety check/, 'the audit row needs a plain title');
assert.match(copy, /The publish went ahead with a written reason\./, 'the audit row must say the publish still happened');
assert.match(history, /SAFETY_CHECK_COPY\.destinationLabel/, 'the destination must get its own line');
assert.doesNotMatch(history, /<span className="font-semibold">Override:<\/span>/,
  'an audit row must not label a person\'s own sentence "Override"');

// Nothing a person reads on this tab may be engine vocabulary. Walk the real string and JSX text nodes so a
// comment explaining the engine wording is not mistaken for copy.
// copy.ts is swept only over SAFETY_CHECK_COPY itself: the same file also holds the retired engine sentences
// as PATTERNS, because a record written in July still says "gate RED (…)" and the only way that record ever
// reads plainly is for the reader to recognise it. Those patterns are machinery, not copy.
const historyText = textNodes(history, 'history.tsx').join('\n');
const safetyCopyText = textNodes(copy, 'copy.ts', 'SAFETY_CHECK_COPY').join('\n');
assert.ok(safetyCopyText.length > 200, 'the safety-check copy could not be found to sweep');
for (const [name, text] of [['Edit History', historyText], ['the shared safety-check copy', safetyCopyText]]) {
  assert.doesNotMatch(text, /gate RED/i, `${name} must not print the engine word for a failed safety check`);
  assert.doesNotMatch(text, /\bBPA\b/, `${name} must not print the engine name of the model quality rules`);
  assert.doesNotMatch(text, /—/, `${name} copy must carry no em dash`);
}

// Help answers the question the tab raised, in the same words.
assert.match(copy, /What is the safety check\?/, 'the help must be able to ask what the safety check is');
assert.match(help, /SAFETY_CHECK_COPY\.helpQuestion/, 'the Changes help must carry the safety-check question');
assert.match(help, /SAFETY_CHECK_COPY\.explainer/, 'the help answer must reuse the shared explainer, not a second copy of it');
assert.match(copy, /A red check does not stop you\./, 'the help answer must say what a red check means for the person');

console.log('Edit History durable recovery UI contract tests passed');

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
