// A publish to the live model took minutes and the page said only "deploy live". The engine now tells the page each step in plain
// words (progress/didChange, opKey deploy_live) and the page adds the seconds. This pins the state model as BEHAVIOUR (the module is
// transpiled and run) and the wiring as source, where the wiring is the thing being pinned.
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

const { foldDeployProgress, deployStepLine, elapsedClock, DEPLOY_PROGRESS_KEY } = await loadTypeScriptModule(resolve(root, 'webview/src/publish-progress.ts'));
const ev = (done, note, extra = {}) => ({ opKey: DEPLOY_PROGRESS_KEY, runId: 'r1', done, total: 0, note, ...extra });

assert.equal(DEPLOY_PROGRESS_KEY, 'deploy_live', 'the key is the one the engine publishes');

// ---- the steps arrive in order, each with its own clock ----
let step = null;
step = foldDeployProgress(step, ev(0, 'Connecting to the service'), 1000);
assert.equal(deployStepLine(step, 1000), 'Connecting to the service, 0:00');
step = foldDeployProgress(step, ev(1, 'Reading the live model'), 3000);
assert.equal(deployStepLine(step, 45000), 'Reading the live model, 0:42', 'the clock starts when the step starts');
assert.equal(foldDeployProgress(step, ev(1, 'Reading the live model'), 9000).since, 3000, 'the same step again does not restart its clock');
step = foldDeployProgress(step, ev(4, 'Sending 85 changes'), 50000);
assert.equal(deployStepLine(step, 51000), 'Sending 85 changes, 0:01');

// ---- late events, other keys and the end ----
assert.equal(foldDeployProgress(step, ev(2, 'Comparing with your copy'), 52000), step, 'a late event never moves the line back');
assert.equal(foldDeployProgress(step, { opKey: 'run_tests', runId: 'r1', done: 3, total: 9, note: 'x' }, 52000), step, 'another key is not this line');
assert.equal(foldDeployProgress(step, ev(5, 'Saving a restore point', { runId: 'r2' }), 60000).runId, 'r2', 'a new run replaces the old line');
assert.equal(foldDeployProgress(step, ev(5, null, { runId: 'r2' }), 52000), step, 'the end of another run leaves this line alone');
assert.equal(foldDeployProgress(step, ev(5, null), 52000), null, 'the end of this run clears the line');
assert.equal(foldDeployProgress(step, ev(5, '  '), 52000), null, 'empty words are the end');
assert.equal(deployStepLine(null, 1000), null);

// ---- the clock ----
assert.equal(elapsedClock(0), '0:00');
assert.equal(elapsedClock(42999), '0:42');
assert.equal(elapsedClock(65000), '1:05');
assert.equal(elapsedClock(754000), '12:34');
assert.equal(elapsedClock(-5), '0:00');

// ---- copy rules: no em dash, plain words ----
const src = readFileSync(resolve(root, 'webview/src/publish-progress.ts'), 'utf8');
assert.ok(!src.includes('—'), 'no em dash in the page copy');

// ---- the wiring ----
const page = readFileSync(resolve(root, 'webview/src/publish-changes.tsx'), 'utf8');
assert.match(page, /onProgress\(\(e\) => setStep\(\(cur\) => foldDeployProgress\(cur, e, Date\.now\(\)\)\)\)/, 'the Changes tab listens to the progress channel');
assert.match(page, /data-testid="publish-step"/, 'the step line has its own test id');
assert.match(page, /!stepLine && <div className="sem-text-meta" data-testid="publish-checking"/, 'the step line takes the spot of the checking line');
// hero20 D7: the new-model card shows its create steps with the seconds too, never a blank wait.
const card = page.slice(page.indexOf('function NewModel('));
assert.match(card, /onProgress\(\(e\) => setStep\(\(cur\) => foldDeployProgress\(cur, e, Date\.now\(\)\)\)\)/, 'the new-model card listens to the progress channel');
assert.match(card, /data-testid="publish-new-step"/, 'the new-model step line has its own test id');
console.log('publish-progress: ok');
