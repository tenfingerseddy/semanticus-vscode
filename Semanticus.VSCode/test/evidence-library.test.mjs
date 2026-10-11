import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const tests = read('webview/src/tests.tsx');
// A finished run's report opens from its Plan row and its History entry now (lane W2, R3): workflow-run.tsx.
const workflows = read('webview/src/workflow-run.tsx');
const dialog = read('webview/src/artifactdialog.tsx');
const app = read('webview/src/App.tsx');

// 1.2.0 welded four sub-tabs into one page and made Reports a PEER of Validation in the Checks strip.
// Kane reversed the second half on 22 September 2026: Reports is a TAB ON Validation, and the Checks area
// it was a peer in no longer exists. What has not changed is that it is one page and not four sub-tabs,
// and that Reports keeps its own route id so a link to it still opens it.
assert.doesNotMatch(tests, /type SubTab/, 'Tests is one page now, not a set of sub-tabs');
assert.doesNotMatch(app, /id: 'evidence', label:/, 'Reports left the area strip; it is a tab on Validation');
assert.match(app, /\{ id: 'review', label: 'Review'[\s\S]*id: 'tests', label: 'Validation'/, 'Validation lives under Review');
assert.match(app, /TAB_TO_GROUP\.evidence = 'review';/, "and Reports' route still resolves to Validation's area");
assert.match(tests, /page\?: 'checks' \| 'reports'/, 'Validation is told which of its two tabs is open');
assert.match(tests, /data-page-tab="reports"/, 'and draws a tab to reach Reports');
assert.match(tests, /\{page === 'reports' \? <EvidenceBody \/> : <>/, 'the Reports tab renders the report library in place');
assert.match(tests, /export function EvidenceBody[\s\S]*rpc<EvidenceLibraryW>\('listEvidence'\)/, 'Reports must browse the engine-owned model library');
assert.match(tests, /rpc<EvidenceArtifactW>\('getEvidence', openEvidence\.id\)/, 'saved artifacts must reopen through the shared engine door');
assert.match(tests, /rpc<EvidenceSaveResultW>\('saveEvidence', 'tests'/, 'Test reports must save through the shared engine operation');
assert.match(workflows, /rpc<EvidenceSaveResultW>\('saveEvidence', 'workflow'/, 'Workflow reports must save through the shared engine operation');
// Minimalism pass (8 Oct): the tab says Reports on its own switch, so the page repeats no title or lead line.
assert.doesNotMatch(tests, /<h1 className="[^"]*">Reports<\/h1>/, 'the tab does not name itself a second time');
assert.match(dialog, /Save with model/, 'the shared report dialog must name the durable action plainly');
assert.match(dialog, /Save the report and its data beside this model\. Export saves a separate copy/, 'the dialog must distinguish a local export from model-scoped evidence');
// The `evidence` route renders NOTHING of its own: Validation is mounted for both of its tabs, above the
// tab switch, so the report library keeps its state when you move between Checks and Reports.
assert.match(app, /tab === 'tests' \|\| tab === 'evidence' \? 'h-full' : 'hidden'/, 'Validation mounts for both of its tabs');
assert.match(app, /page=\{validationTab\} onPage=\{\(next\) => goTab\(next === 'reports' \? 'evidence' : 'tests'\)\}/,
  'and the two tabs are real routes, so a deep link and the strip agree');
assert.match(app, /const validationTab: 'checks' \| 'reports' = tab === 'evidence' \? 'reports' : 'checks';/,
  "a link to `evidence` opens Validation on its Reports tab");

console.log('evidence library UI contract tests passed');
