import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const lineage = read('webview/src/lineage.tsx');
const app = read('webview/src/App.tsx');

for (const verb of ['Check blast radius', 'Safe rename', 'Stage removal', 'Create probes'])
  assert.match(lineage, new RegExp('>' + verb + '<'), `Lineage must expose the ${verb} action`);

assert.match(lineage, /rpc<ImpactAssessmentResult>\('impactAssessment'/, 'every action must consume the engine-owned assessment');
assert.match(lineage, /scope: 'modelAndReports'/, 'the UI default must include report coverage rather than imply model-only safety');
assert.match(lineage, /reportPaths: reportPaths \?\? \[\]/, 'local PBIR scope must flow into the engine assessment');
assert.doesNotMatch(lineage, /Safe to change \(model-only\)/, 'zero model dependencies must not be shown as globally safe');
assert.match(lineage, /rpc\('addPlanItem', root, 'delete'/, 'removal must stage through the shared Change Plan');
assert.match(lineage, /Nothing is removed until the proposed item is approved and applied/, 'staging must state the write boundary');
assert.match(app, /onOpenPlan=\{\(\) => goTab\('optimize'\)\}/, 'Lineage must route a staged item into the existing Change Plan');
assert.match(app, /onOpenTests=\{\(\) => goTab\('tests'\)\}/, 'probe creation must route to the existing Tests evidence home');
assert.match(lineage, /onOpenWorkflow\('governed-rename'\)/, 'Safe rename must route to its stable stock workflow');
assert.match(app, /<WorkflowsView navTarget=\{workflowTarget\}/, 'the workflow hand-off must select the exact playbook');
assert.doesNotMatch(app, /label: ['"](Blast radius|Safe rename|Probes)['"]/, 'Lineage verbs must not create top-level tabs');

console.log('lineage action UI contract tests passed');
