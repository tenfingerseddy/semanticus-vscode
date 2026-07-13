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

assert.match(tests, /type SubTab = 'measures' \| 'relationships' \| 'security' \| 'history';/, 'Tests must keep exactly the four welded grade facets');
assert.doesNotMatch(tests, /type SubTab = [^;]*'evidence'/, 'Evidence must not split the welded Tests grade facets');
assert.match(app, /\{ id: 'prove'[\s\S]*id: 'tests', label: 'Tests'[\s\S]*id: 'evidence', label: 'Evidence'/, 'Prove must expose only Tests and Evidence as peers');
assert.match(tests, /export function EvidenceView[\s\S]*rpc<EvidenceLibraryW>\('listEvidence'\)/, 'Evidence must browse the engine-owned model library through its peer view');
assert.match(tests, /rpc<EvidenceArtifactW>\('getEvidence', openEvidence\.id\)/, 'saved artifacts must reopen through the shared engine door');
assert.match(tests, /rpc<EvidenceSaveResultW>\('saveEvidence', 'tests'/, 'Test reports must save through the shared engine operation');
assert.match(workflows, /rpc<EvidenceSaveResultW>\('saveEvidence', 'workflow'/, 'Workflow reports must save through the shared engine operation');
assert.match(dialog, /Save with model/, 'the shared report dialog must name the durable action plainly');
assert.match(dialog, /export alone writes nothing/, 'the dialog must distinguish a local export from model-scoped evidence');
assert.match(app, /tab === 'evidence'[\s\S]*<EvidenceView/, 'the Evidence peer must render the shared model library');

console.log('evidence library UI contract tests passed');
