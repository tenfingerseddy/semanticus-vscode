import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const scenarios = read('webview/src/workflowscenarios.tsx');
const designer = read('webview/src/workflowdesign.tsx');
const workflows = read('webview/src/workflows.tsx');
const template = read('../Semanticus.Engine/workflow-templates/hard-measure.md');
const canonical = read('../Semanticus.Engine/workflows/verified-measure.md');

const aiReady = scenarios.indexOf("id: 'make-model-ai-ready'");
const hardMeasure = scenarios.indexOf("id: 'hard-measure'");
const monthEnd = scenarios.indexOf("id: 'month-end-close'");
assert.ok(aiReady >= 0 && hardMeasure > aiReady, 'AI-ready and hard-measure must be the first two catalog entries');
assert.ok(monthEnd > hardMeasure, 'Month-end close must remain available below the two hero jobs');
assert.match(scenarios, /SCENARIOS\.filter\(\(s\) => s\.hero\)/, 'the picker must render an explicit hero tier first');
assert.match(scenarios, /s\.kind === 'template' && !s\.hero/, 'hero templates must not be duplicated in the depth tier');
assert.match(scenarios, /author one candidate[\s\S]*independent raw-row oracle/i,
  'the featured card must describe the ProBench v2 oracle workflow, not the old control-total flow');
for (const [name, text] of [['featured template', template], ['executable workflow', canonical]]) {
  assert.match(text, /version: 4/, `${name} must carry the optimized v4 contract`);
  assert.match(text, /## Step 1: Pin the intent and edge policy[\s\S]*## Step 2: Author one candidate/,
    `${name} must use the v4 one-candidate spine`);
  assert.match(text, /oracleDax[\s\S]*divergenceProof[\s\S]*trapCheck/,
    `${name} must require an independent oracle, a discriminating context, and a trap check`);
  assert.match(text, /## Step 4: Hard gate, candidate equals oracle[\s\S]*probe: oracleDax/,
    `${name} must hard-gate candidate equality against the oracle`);
  assert.match(text, /## Step 5: Finalize, with an optional performance pass/,
    `${name} must keep performance work off the default correctness path`);
  assert.doesNotMatch(text, /control_total|control total/i,
    `${name} must not fall back to the superseded single-control-total proof`);
}

assert.match(designer, /evidenceQuickAdd/, 'workflow steps must expose evidence as a first-class addable action');
assert.match(designer, /\+ evidence report/, 'the action must use an analyst-facing label');
assert.match(designer, /Evidence exports only after the run is completed or aborted/, 'the designer must explain when evidence becomes final');
assert.match(workflows, /'check-blast-radius': 'Quality'/, 'Check blast radius must live in the Quality rail, not Custom');
assert.match(workflows, /'governed-rename': 'Quality'/, 'Safe rename must live beside the blast-radius workflow in Quality');
assert.match(workflows, /data-workflow=\{w\.name\}/, 'the workflow rail must remain directly targetable by the visual harness');

console.log('workflow hero and evidence-action tests passed');
