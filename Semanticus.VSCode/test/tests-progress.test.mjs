// A full Validation run takes about a minute on a live model, and the page said only "Running". The engine now
// reports how far it has got ("312 of 691 checks"), and the page shows it. This pins the state model as BEHAVIOUR
// (the module is transpiled and run) and the wiring as source, where the wiring is the thing being pinned.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

async function loadTypeScriptModule(file) {
  const output = ts.transpileModule(readFileSync(file, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }, fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output, 'utf8').toString('base64')}`);
}

const { foldRunProgress, runProgressText, runProgressPercent, RUN_PROGRESS_KEY } = await loadTypeScriptModule(resolve(root, 'webview/src/tests-model.ts'));

const ev = (done, total, extra = {}) => ({ opKey: RUN_PROGRESS_KEY, runId: 'page-1', done, total, ...extra });

// ---- the state model ------------------------------------------------------------------------------------
assert.equal(RUN_PROGRESS_KEY, 'run_tests', 'the key is the one the engine publishes');

let state = null;
state = foldRunProgress(state, ev(0, 691), 'page-1');
assert.deepEqual(state, { runId: 'page-1', done: 0, total: 691 });
state = foldRunProgress(state, ev(312, 691), 'page-1');
assert.equal(runProgressText(state), '312 of 691 checks');
assert.equal(runProgressPercent(state), 45);

assert.equal(foldRunProgress(state, ev(100, 691), 'page-1'), state, 'a late event never moves the count backwards');
assert.equal(foldRunProgress(state, { ...ev(500, 691), runId: 'assistant-9' }, 'page-1'), state, 'another run does not move this page\'s count');
assert.equal(foldRunProgress(state, { ...ev(500, 691), opKey: 'analyze_cloud_reports' }, 'page-1'), state, 'another kind of work does not either');
assert.equal(foldRunProgress(null, ev(5, 10), null), null, 'with no run of its own, the page shows nothing');
assert.equal(foldRunProgress(null, ev(0, 0), 'page-1'), null, 'an event with no total says nothing');

const over = foldRunProgress(null, ev(900, 691), 'page-1');
assert.equal(over.done, 691, 'the count is held to the total');
assert.equal(foldRunProgress(null, ev(-4, 10), 'page-1').done, 0, 'and to zero');

// ---- the words ------------------------------------------------------------------------------------------
assert.equal(runProgressText(null), 'Running the checks…', 'before the first count');
assert.equal(runProgressText({ runId: 'a', done: 1, total: 1 }), '1 of 1 check run, grading them…');
assert.equal(runProgressText({ runId: 'a', done: 691, total: 691 }), '691 of 691 checks run, grading them…', 'every check has run; the grade is still being worked out');
assert.equal(runProgressText({ runId: 'a', done: 1200, total: 2400 }), `${(1200).toLocaleString()} of ${(2400).toLocaleString()} checks`);
assert.equal(runProgressPercent(null), null);
for (const text of [runProgressText(null), runProgressText(state)]) assert.ok(!/[—–]/.test(text), 'no dashes in the words');

// ---- the wiring -----------------------------------------------------------------------------------------
const page = readFileSync(resolve(root, 'webview/src/tests.tsx'), 'utf8');
assert.match(page, /rpcPressed<TestRunW>\('runTests', options\.persist \?\? false, 'human', options\.only \?\? null, options\.sections \?\? null, runId\)/, 'the page names its own run');
assert.match(page, /onProgress\(\(event\) => setProgress\(\(current\) => foldRunProgress\(current, event, ownRunId\.current\)\)\)/, 'progress events fold through the model');
assert.match(page, /ownRunId\.current = null; setProgress\(null\); setBusy\(false\)/, 'a finished run clears the count');
assert.match(page, /const running = busy \|\| assistantRun\.phase === 'running';/, 'the page counts the assistant\'s run as busy too');
assert.match(page, /run=\{run\} running=\{running\} progress=\{progress\}/, 'the board shows the count in place of the verdict line');
assert.match(page, /running\s*\? <RunProgressLine progress=\{progress\} \/>/, 'the verdict line shows it while a run goes');
console.log('tests-progress: ok');
