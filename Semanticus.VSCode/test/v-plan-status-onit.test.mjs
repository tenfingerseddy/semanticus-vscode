// Verifier (v-plan-status): "On it" from assistantAtWork must end about 45 s after the assistant's last edit, even when nothing else changes.
// The engine computes the flag at read time (LocalEngine.Plan.cs MarkRowsTheAssistantIsWorking), so the page must read the plan again when the
// window ends. plan.tsx re-reads only on sessionId/revision/changeNonce/retry and plan/didChange; no timer looks at assistantAtWork.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
// fileURLToPath, not .pathname: on Windows a URL pathname is /D:/..., which becomes D:\D:\... once joined.
const root = process.argv[2] ?? fileURLToPath(new URL('..', import.meta.url));
const plan = readFileSync(`${root}/webview/src/plan.tsx`, 'utf8');
const model = readFileSync(`${root}/webview/src/plan-model.ts`, 'utf8');
const anyTimer = /assistantAtWork[\s\S]{0,400}setTimeout|setTimeout[\s\S]{0,400}assistantAtWork/.test(plan + model);
const windowEnd = /assistantAtWork(Until|Ends|At)\b/.test(model);
assert.ok(anyTimer || windowEnd, 'a row flagged assistantAtWork stays On it (spinning, assistant colour, doer menu and Up hidden) until some other change re-reads the plan');
console.log('v-plan-status-onit: passed');
