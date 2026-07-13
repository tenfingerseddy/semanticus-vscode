import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const interview = read('webview/src/interview.tsx');
const tests = read('webview/src/tests.tsx');
const app = read('webview/src/App.tsx');

assert.match(interview, /Behavioral contracts/, 'Tests must name saved interview questions as behavioral contracts');
assert.match(interview, /Running tests automatically re-checks saved number and paraphrase questions/, 'the automatic replay must be explicit');
assert.match(interview, /Safe-decline questions are checked in an AI chat/, 'chat-only contracts must not imply automatic model execution');
assert.match(interview, /never change its grade or coverage/, 'the evidence-only grading boundary must remain visible');
assert.match(tests, /<InterviewCard /, 'behavioral contracts must remain inside Tests');
assert.match(tests, /suiteEvidence=\{run\?\.interview\}/, 'fresh replay evidence must flow into the open behavioral-contract card');
assert.match(interview, /Replayed with this Tests run/, 'the latest replay must be distinguishable from persisted history');
assert.doesNotMatch(app, /label:\s*['"]Behavioral contracts['"]/, 'behavioral contracts must not create a top-level tab');

console.log('Tests behavioral contract UI contract tests passed');
