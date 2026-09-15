import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const tests = read('webview/src/tests.tsx');
const workflows = read('webview/src/workflows.tsx');
const dialog = read('webview/src/artifactdialog.tsx');
const app = read('webview/src/App.tsx');

// 1.2.0: the four welded sub-tabs became one page. Saved reports stays a PEER of Tests, not a sub-tab,
// because a saved report is a different thing from the run you are looking at (Astra, journey D).
assert.doesNotMatch(tests, /type SubTab/, 'Tests is one page now, not a set of sub-tabs');
assert.match(app, /\{ id: 'checks'[\s\S]*id: 'tests', label: 'Tests'[\s\S]*id: 'evidence', label: 'Saved reports'/, 'Checks must keep Tests and Saved reports together');
assert.match(tests, /export function EvidenceView[\s\S]*rpc<EvidenceLibraryW>\('listEvidence'\)/, 'Evidence must browse the engine-owned model library through its peer view');
assert.match(tests, /rpc<EvidenceArtifactW>\('getEvidence', openEvidence\.id\)/, 'saved artifacts must reopen through the shared engine door');
assert.match(tests, /rpc<EvidenceSaveResultW>\('saveEvidence', 'tests'/, 'Test reports must save through the shared engine operation');
assert.match(workflows, /rpc<EvidenceSaveResultW>\('saveEvidence', 'workflow'/, 'Workflow reports must save through the shared engine operation');
assert.match(tests, /<h1 className="m-0 text-\[15px\] font-semibold">Saved reports<\/h1>/, 'the peer view names itself Saved reports');
assert.match(dialog, /Save with model/, 'the shared report dialog must name the durable action plainly');
assert.match(dialog, /Save the report and its data beside this model\. Export saves a separate copy/, 'the dialog must distinguish a local export from model-scoped evidence');
assert.match(app, /tab === 'evidence'[\s\S]*<EvidenceView/, 'the Evidence peer must render the shared model library');

console.log('evidence library UI contract tests passed');
