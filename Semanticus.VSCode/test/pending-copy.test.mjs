// hero19 D10 (Pending changes page) and D7 (the red-check line on Publish): the copy says one consistent thing, shows format strings the way
// people read them, and speaks no engine words. The helpers are pure and load without a host.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
async function load(file) {
  const out = ts.transpileModule(read(file), { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(out).toString('base64')}`);
}
const copy = await load('webview/src/pending-copy.ts');
const sum = (o = {}) => ({ total: 0, deterministic: 0, bpa: 0, ai: 0, jev: 0, renames: 0, needsContent: 0, approved: 0, rejected: 0, applied: 0, unverified: 0, ...o });
let passed = 0;
const test = (name, fn) => { fn(); passed++; };

test('P1 a format string reads the way people read it: no backslashes', () => {
  assert.equal(copy.readableFormat('\\$#,0.00;(\\$#,0.00);\\$#,0.00'), '$#,0.00;($#,0.00);$#,0.00');
  assert.equal(copy.readableFormat('0.0%'), '0.0%');
  assert.equal(copy.readableFormat(undefined), undefined);
  assert.equal(copy.readableFormat('Format as \\$#,0.00'), 'Format as $#,0.00');
});

test('P2 applied rows are not "0 approved": the summary counts what is applied', () => {
  const line = copy.pendingSummary(sum({ total: 37, jev: 36, deterministic: 1, applied: 37 }));
  assert.match(line, /37 applied/);
  assert.doesNotMatch(line, /0 approved/);
  assert.doesNotMatch(line, /deterministic|AI-authored/i, 'no engine words');
  assert.equal(copy.pendingSummary(sum({ total: 5, approved: 2, applied: 1, needsContent: 1 })), '1 applied · 2 approved · 1 needs content · 1 to review');
});

test('P3 the second sentence follows the state, and "Nothing is written" is only said while something is not applied', () => {
  assert.match(copy.pendingNote(sum({ total: 3, applied: 3 })), /All applied/);
  assert.doesNotMatch(copy.pendingNote(sum({ total: 3, applied: 3 })), /Nothing is written until you apply/);
  assert.match(copy.pendingNote(sum({ total: 3, approved: 3 })), /Nothing is written until you apply/);
  assert.match(copy.pendingNote(sum({ total: 3, approved: 1, applied: 2 })), /not written until you apply/i);
});

test('P4 a group says applied when its rows are applied, approved when they are only approved', () => {
  assert.equal(copy.groupState([{ status: 'applied' }, { status: 'applied' }]), null, 'applied is said once per row, not again on the header (hero20 D13)');
  assert.equal(copy.groupState([{ status: 'approved' }, { status: 'applied' }]), '1 approved');
  assert.equal(copy.groupState([{ status: 'proposed' }]), null);
});

test('P5 the page source no longer carries the old words, and Plan does not repeat the sentence above the page', () => {
  const opt = read('webview/src/optimize.tsx');
  assert.doesNotMatch(opt, /AI-authored/);
  assert.doesNotMatch(opt, />Deterministic </);
  assert.doesNotMatch(read('webview/src/plan.tsx'), /Nothing is written until you apply/);
  assert.match(opt, /readableFormat/, 'rows show format strings through the helper');
});

const progress = await load('webview/src/publish-progress.ts');
test('P6 the red-check line: shown when the assistant asks, cleared when the engine says so, other events change nothing', () => {
  const words = 'Your assistant is asking you about 1 red check: Avoid IFERROR (Return Rate)';
  const shown = progress.foldPublishAsk(null, { kind: 'publish_asking', ok: true, label: words });
  assert.equal(shown, words);
  assert.equal(progress.foldPublishAsk(shown, { kind: 'run_dax', ok: true, label: 'x' }), words, 'other events change nothing');
  assert.equal(progress.foldPublishAsk(shown, { kind: 'publish_asking', ok: false }), null, 'the clearing event removes it');
  assert.equal(progress.foldPublishAsk(null, { kind: 'publish_asking', ok: true, label: '  ' }), null, 'an empty line shows nothing');
  const page = read('webview/src/publish-changes.tsx');
  assert.match(page, /data-testid="publish-asking"/);
  assert.match(page, /clearPublishAsk\(\)/, 'a publish the person makes clears it');
  assert.doesNotMatch(words, /\u2014/);
});

// ---- copy21: one word once, rule names in plain words ----------------------------------------------------------------------------------------

const ruleWords = await load('webview/src/rule-words.ts');

test('C1 a pending row says "applied" once: the chip says it, the engine note does not say it again', () => {
  assert.equal(copy.rowNote('applied', 'applied'), null);
  assert.equal(copy.rowNote('applied', ' Applied. '), null);
  assert.equal(copy.rowNote('applied', 'applied (checked again first that nothing uses it)'), 'Checked again first that nothing uses it.');
  assert.equal(copy.rowNote('failed', 'The measure is gone.'), 'The measure is gone.');
  assert.equal(copy.rowNote('skipped', 'not applied: it needs a rename first'), 'not applied: it needs a rename first');
  assert.equal(copy.rowNote('applied', undefined), null);
  const page = read('webview/src/optimize.tsx');
  assert.match(page, /rowNote\(it\.status, it\.note\)/, 'the row reads its note through rowNote');
  assert.doesNotMatch(page, /\{it\.note && </, 'the raw engine note is not drawn');
});

test('C2 a rule name has no [Category] bracket on Review, in the findings and on a pending row', () => {
  assert.equal(ruleWords.plainRuleName('[Formatting] Do not summarize numeric columns'), 'Do not summarize numeric columns');
  assert.equal(ruleWords.plainRuleName('[Performance] Avoid the IFERROR function'), 'Avoid the IFERROR function');
  assert.equal(ruleWords.plainRuleName('Do not summarize numeric columns'), 'Do not summarize numeric columns');
  assert.equal(ruleWords.plainRuleName(ruleWords.plainRuleName('[Formatting] Hide keys')), 'Hide keys', 'it can run twice');
  assert.equal(ruleWords.plainRuleName('[Formatting]'), '[Formatting]', 'a name that is only a bracket is kept');
  // Only a built-in category is taken off (verifier v-copy21): a custom rule whose bracket is a field keeps its name.
  assert.equal(ruleWords.plainRuleName('[Sales Amount] needs a format string'), '[Sales Amount] needs a format string');
  assert.equal(ruleWords.plainRuleName('[Total Sales] must be hidden'), '[Total Sales] must be hidden');
  assert.equal(ruleWords.plainRuleName('[DAX Expressions] Avoid IFERROR'), 'Avoid IFERROR');
  assert.equal(ruleWords.plainRuleName('[Naming Conventions] Partitions should be named'), 'Partitions should be named');
  assert.equal(ruleWords.plainRuleName('[error prevention] Check keys'), 'Check keys', 'the tag is matched without case');
  assert.equal(ruleWords.plainRuleName("'Sales'[Net] is wrong"), "'Sales'[Net] is wrong", 'a bracket later in the name is a field, not a category');
  assert.equal(ruleWords.plainRuleName(undefined), undefined);
  for (const file of ['bpa.tsx', 'findings.tsx', 'optimize.tsx']) assert.match(read('webview/src/' + file), /plainRuleName\(/, file + ' draws rule names through plainRuleName');
  assert.match(read('webview/src/App.tsx'), /ruleName: plainRuleName\(f\.ruleTitle\)/, 'the findings the Review page gets are plain');
});

test('C3 the result line of Run the plan is read in full, never cut with an ellipsis', () => {
  const css = read('webview/src/plan-calm.css');
  const rule = css.split('\n').find((l) => l.startsWith('.pc-done {')) ?? '';
  assert.ok(rule, 'the .pc-done rule is there');
  assert.doesNotMatch(rule, /text-overflow|white-space: nowrap/);
  assert.match(rule, /overflow-wrap: anywhere/);
});

test('C4 the Claude Desktop card says Connected once', () => {
  const model = read('webview/src/connect-assistants-model.ts');
  assert.match(model, /status: 'On this computer', action: 'done', actionLabel: 'Connected'/, 'the line under a connected name does not say Connected again');
  assert.match(model, /r\.ok && r\.changed && r\.nextStep \? \[\] : \[r\.message\]/, 'a good press says what to do next, not Connected again');
});

// ---- UAT round 2 (18 Oct) surface words ----------------------------------------------------------------------
const words = await load('webview/src/rule-words.ts');
const share = await load('webview/src/storage-share.ts');

test('R1 the pending list hides a chip with nothing behind it and says "new value to come"', () => {
  const optimize = read('webview/src/optimize.tsx');
  assert.match(optimize, /\(s\?\.jev \?\? 0\) > 0 \|\| filter === 'jev'/, 'the Jev chip shows only with rows, or while it is the filter in force');
  assert.doesNotMatch(optimize, /awaiting authored value/, 'no engine words on a row with no value yet');
  assert.match(optimize, /new value to come/);
});

test('R2 the readiness card speaks plain words, not type names or "capped at D"', () => {
  assert.equal(words.plainCategory('CopilotLimits'), 'Copilot limits');
  assert.equal(words.plainCategory('DataAgentConfig'), 'Data agent setup');
  assert.equal(words.plainCategory('Naming'), 'Naming');
  const half = words.plainGate('>50% of visible measures undescribed; capped at D');
  assert.doesNotMatch(half, /capped|>50%/);
  assert.match(half, /More than half of your measures have no description/);
  assert.match(words.plainGate('2 objects hold a formula that refers to an object the model does not have; capped at D'), /^2 objects have a formula/);
  assert.equal(words.plainGate('Something new'), 'Something new', 'an unknown note passes through as it came');
});

test('R3 the Size page says plain words and names the table of a repeated column', () => {
  assert.equal(words.plainRuleName('Dictionary-dominated column'), 'Column with many different values');
  const msg = share.adviceFor('Dictionary is the largest component: 5.3 MB of 6 MB (88%). Often indicates a high-cardinality or wide text column.', true);
  assert.doesNotMatch(msg, /Dictionary|cardinality/);
  assert.match(share.adviceFor('Hash index is 40% of this column (2 MB of 5 MB).', true), /^Lookup help is 40%/);
  const app = read('webview/src/App.tsx');
  assert.doesNotMatch(app, /Hash indexes <span/, 'the legend does not say hash indexes');
  assert.match(app, /\$\{c\.table\} · \$\{c\.column\}/, 'two bars with one column name carry their table');
});

test('R4 Workflows is retired from the header, and one name drives the screen', () => {
  const app = read('webview/src/App.tsx');
  assert.match(app, /const SHOWN_GROUPS = TAB_GROUPS\.filter\(\(g\) => !RETIRED_AREAS\.includes\(g\.id\)\)/);
  assert.doesNotMatch(app, /\{TAB_GROUPS\.map\(\(item, index\) => <GroupTab/, 'the header strip draws the shown areas only');
  const head = read('webview/src/plan-head.tsx');
  assert.doesNotMatch(head, /'Claude'/, 'the Plan switch never names the app: it says Your assistant');
  assert.match(head, /Let your assistant drive/);
  const propage = read('webview/src/propage.tsx');
  assert.doesNotMatch(propage, /Workflows/, 'the Pro page does not sell Workflows');
  assert.doesNotMatch(propage, /waits for you to approve/, 'the Pro page describes asking, not approving');
});

test('R5 with no model open the Pro advice line says to open one', () => {
  const consent = read('webview/src/consent.tsx');
  assert.match(consent, /Open a model before you can let Pro decide for it/);
  assert.match(consent, /permissionNeedsLine\(modelOpen\)/);
});

console.log(`pending-copy: ${passed} tests passed`);
