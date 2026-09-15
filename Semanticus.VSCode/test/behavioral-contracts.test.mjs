import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const interview = read('webview/src/interview.tsx');
const tests = read('webview/src/tests.tsx');
const app = read('webview/src/App.tsx');

assert.match(interview, /Business questions and expected answers/, 'saved interview questions must be explained in plain language');
assert.match(interview, /Safe-decline questions are checked in an AI chat/, 'chat-only contracts must not imply automatic model execution');
assert.match(interview, /never change a test grade or coverage/, 'the evidence-only grading boundary must remain visible');
// 1.2.0: the Model interview left the Tests page, and a Tests run no longer replays saved questions.
// The card keeps its own Ask, so the evidence is still reachable; nothing runs it behind the person's back.
assert.doesNotMatch(tests, /<InterviewCard /, 'behavioral contracts must not live inside Tests any more');
assert.doesNotMatch(tests, /suiteEvidence/, 'a Tests run no longer carries interview evidence');
assert.doesNotMatch(interview, /Replayed with this Tests run/, 'there is no automatic Tests replay to distinguish');
assert.doesNotMatch(interview, /Running tests automatically re-checks/, 'the automatic replay claim must go with the replay');
assert.match(interview, /Ask a question again here to re-check it/, 'asking again is now an explicit action');
assert.doesNotMatch(app, /label:\s*['"]Behavioral contracts['"]/, 'behavioral contracts must not create a top-level tab');

console.log('Behavioral contract UI contract tests passed');
