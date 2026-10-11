import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const tests = read('webview/src/tests.tsx');
const app = read('webview/src/App.tsx');

// 8 Oct 2026 (Kane, D2): the Model interview is hidden from the whole app UI. The engine part stays (the MCP tools,
// the RPC, the interview_replay check and the assistant skill), so this file now pins the ABSENCE of the screen and
// is the guard against a later review restoring the card as a golden-rule-2 repair. The exception is logged in
// docs/PLAN.md, Resolved decisions.
assert.ok(!existsSync(resolve(root, 'webview/src/interview.tsx')), 'the Model interview card file is gone');
assert.doesNotMatch(app, /InterviewCard|from '\.\/interview'/, 'AI readiness does not mount the interview card');
assert.doesNotMatch(app, /Business questions and expected answers/, 'and no page names saved business questions');
// 1.2.0: the Model interview left the Tests page, and a Tests run no longer replays saved questions.
assert.doesNotMatch(tests, /<InterviewCard /, 'behavioral contracts must not live inside Tests either');
assert.doesNotMatch(tests, /suiteEvidence/, 'a Tests run no longer carries interview evidence');
assert.doesNotMatch(app, /label:\s*['"]Behavioral contracts['"]/, 'behavioral contracts must not create a top-level tab');
// The Publish advisory (publish lane) and the workflow pickers (workflows lane) are pinned where they live.
const deploy = read('webview/src/deploy.tsx');
assert.doesNotMatch(deploy, /Saved questions re-checked/, 'the Publish page shows no Model interview advisory (D2)');

console.log('Behavioral contract UI contract tests passed');
