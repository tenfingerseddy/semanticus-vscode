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
assert.match(scenarios, /lock the expected values[\s\S]*author one candidate[\s\S]*independent raw-row witness/i,
  'the featured card must describe the requirement-anchored witness workflow, not the old oracle flow');
for (const [name, text] of [['featured template', template], ['executable workflow', canonical]]) {
  assert.match(text, /## Step 1: Fix the specification[\s\S]*## Step 2: Lock the expected values/,
    `${name} must use the requirement-first, test-first spine`);
  assert.match(text, /contextLedger[\s\S]*expectedValues[\s\S]*witnessDax[\s\S]*certificate/,
    `${name} must require a context ledger, locked expected values, a locked witness, and an honest certificate`);
  assert.match(text, /## Step 6: HARD gate[\s\S]*probe: witnessDax[\s\S]*openShapesFrom: openShapes/,
    `${name} must hard-gate candidate equality against the locked witness on the pinned partition`);
  assert.doesNotMatch(text, /control_total|control total|raw-row oracle/i,
    `${name} must not fall back to the superseded control-total or v4 oracle framing`);
  assert.doesNotMatch(text, /[—–]/, `${name} must carry no em or en dashes (product copy rule)`);
  assert.doesNotMatch(text, /\b(?:Power BI|Microsoft Fabric|Microsoft Excel)\b/i,
    `${name} must not put Microsoft product names in user-facing copy`);
}

assert.match(template, /version: 5/, 'the separately versioned featured template must remain on its v5 contract');
assert.match(template, /## Step 7: Performance when attested, then finalize for production/,
  'the featured template must retain its attested-timing performance pass');

assert.match(canonical, /version: 6/, 'the executable stock workflow must carry the v51 semantic contract as version 6');
assert.match(canonical, /OUTSIDE DAX[\s\S]*small GROUPED row extract/i,
  'the stock workflow must derive anchors from small grouped extracts with arithmetic outside DAX');
assert.match(canonical, /kind: expected_values[\s\S]*anchors: expectedValues/,
  'the stock workflow must enforce its locked expected values through the structured anchor gate');
assert.match(canonical, /## Step 3: Author ONE canonical candidate[\s\S]*name: expectedValues[\s\S]*REQUIRED RECEIPT[\s\S]*required: optional[\s\S]*kind: expected_values[\s\S]*anchors: expectedValues/,
  'Step 3 must expose an optional receipted anchor revision that shadows Step 2 only when answered');
assert.match(canonical, /Every changed anchor object must contain originalExpect[\s\S]*correctedExpect[\s\S]*row-returning extractQuery/,
  'anchor revisions must name the engine-enforced receipt fields and live row-returning query');
assert.match(canonical, /without changing its contexts[\s\S]*scalar-constant extracts are refused/,
  'anchor revisions must preserve locked contexts and reject non-extract receipts');
assert.match(canonical, /## Step 4: Lock the witness\. Independent AND efficient[\s\S]*SARGable[\s\S]*witnessTiming/,
  'the stock workflow must require an independent, efficient, timed witness');
assert.match(canonical, /no bare FILTER over ALL|Do NOT wrap a[\s\S]*bare FILTER over ALL/i,
  'the stock workflow must forbid a bare FILTER over ALL of the large fact table');
assert.match(canonical, /## Step 6: HARD gate[\s\S]*Restate the Step-5 witness verbatim[\s\S]*required: required[\s\S]*probe: witnessDax/,
  'the hard equality gate must keep the repair window without allowing a decline to shadow the witness');
assert.match(canonical, /## Step 7: Performance against the model floor[\s\S]*candidate-vs-model-floor/i,
  'the final performance pass must grade against the base-aggregation model floor');
assert.match(canonical, /## Step 7: Performance against the model floor[\s\S]*name: expectedValues[\s\S]*REQUIRED RECEIPT[\s\S]*required: optional[\s\S]*verify:\s*\n\s*- kind: expected_values\s*\n\s*anchors: expectedValues/,
  'Step 7 must inherit or explicitly revise anchors and always enforce the latest set');
assert.doesNotMatch(canonical, /verified-measure-v51|prowfv|probench|23M-row|v1 record|v5 witness/i,
  'benchmark arm names and benchmark-specific wording must not leak into the stock workflow');

assert.match(designer, /evidenceQuickAdd/, 'workflow steps must expose evidence as a first-class addable action');
assert.match(designer, /\+ evidence report/, 'the action must use an analyst-facing label');
assert.match(designer, /Evidence exports only after the run is completed or aborted/, 'the designer must explain when evidence becomes final');
assert.match(workflows, /'check-blast-radius': 'Quality'/, 'Check blast radius must live in the Quality rail, not Custom');
assert.match(workflows, /'governed-rename': 'Quality'/, 'Safe rename must live beside the blast-radius workflow in Quality');
assert.match(workflows, /data-workflow=\{w\.name\}/, 'the workflow rail must remain directly targetable by the visual harness');

console.log('workflow hero and evidence-action tests passed');
