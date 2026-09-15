import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const scenarios = read('webview/src/workflowscenarios.tsx');
const designer = read('webview/src/workflowdesign.tsx');
const forms = read('webview/src/workflowforms.tsx');
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
assert.match(scenarios, /Define the business rule[\s\S]*trusted answers from source rows[\s\S]*test it against those answers/i,
  'the featured card explains that independent source answers are established before testing the calculation');
for (const [name, text] of [['featured template', template], ['executable workflow', canonical]]) {
  assert.match(text, /## Step 1: Fix the specification[\s\S]*## Step 2: Lock the expected values/,
    `${name} must use the requirement-first, test-first spine`);
  // The two files share the spine but not the step count: the featured template is still the seven-step v5
  // contract, while the shipped seed was cut to four person-facing steps at UX16. The ledger-and-witness
  // assertions that named v5's own input names now live with the template below, and the shipped seed's
  // equivalent guarantees are asserted step by step further down. Neither file lost a guard.
  assert.match(text, /CONTEXT LEDGER[\s\S]*PINNED[\s\S]*OPEN[\s\S]*expectedValues[\s\S]*witnessDax[\s\S]*certificate/,
    `${name} must require a pinned-or-open context ledger, locked expected values, a locked witness, and an honest certificate`);
  assert.doesNotMatch(text, /control_total|control total|raw-row oracle/i,
    `${name} must not fall back to the superseded control-total or v4 oracle framing`);
  assert.doesNotMatch(text, /[—–]/, `${name} must carry no em or en dashes (product copy rule)`);
  assert.doesNotMatch(text, /\b(?:Power BI|Microsoft Fabric|Microsoft Excel)\b/i,
    `${name} must not put Microsoft product names in user-facing copy`);
}

assert.match(template, /version: 5/, 'the separately versioned featured template must remain on its v5 contract');
assert.match(template, /## Step 7: Performance when attested, then finalize for production/,
  'the featured template must retain its attested-timing performance pass');
assert.match(template, /openShapesFrom: openShapes/,
  'the v5 template must keep its Step-6-declared open-shape partition');
assert.match(template, /## Step 6: HARD gate[\s\S]*probe: witnessDax[\s\S]*openShapesFrom: open/,
  'the featured template must hard-gate candidate equality against the locked witness on the pinned partition');

assert.match(canonical, /version: 7/, 'the executable stock workflow must carry the v7 shaped-anchor coverage contract as version 7');

// =====================================================================================================
// THE SHIPPED SEED IS FOUR PERSON-FACING STEPS, AND EACH ONE IS ASSERTED WHERE IT LIVES.
// UX16 cut the seven-step v7 file to four (requirement, expected values, candidate, reconcile-and-finish);
// the context ledger moved into the Step-1 instructions and the old Step 4-7 work was folded into Step 4.
// Every guarantee the old assertions made is still asserted below, but against a SLICE of the step that
// must carry it rather than against the whole file, so a guard can no longer be satisfied by wording that
// drifted into a different step. The featured v5 template is asserted separately above; it did not change.
// =====================================================================================================
const stepsOf = (text) => Object.fromEntries(text.split(/^(?=##\s*Step\s+\d+\s*:)/m)
  .filter((s) => /^##\s*Step\s+\d+\s*:/.test(s))
  .map((s) => [s.match(/^##\s*Step\s+(\d+)/)[1], s]));
const cs = stepsOf(canonical);
assert.deepEqual(Object.keys(cs), ['1', '2', '3', '4'],
  'the shipped verified-measure seed is four person-facing steps; a step added or lost invalidates every slice below');

// Step 1 fixes the requirement, and nothing is authored against anything else.
assert.match(cs['1'], /## Step 1: Fix the specification\. The requirement text is the only source/,
  'Step 1 must fix the specification from the requirement text');
assert.match(cs['1'], /CONTEXT LEDGER[\s\S]*- PINNED:[\s\S]*- OPEN:[\s\S]*name: requirement\s*\r?\n\s*question:[\s\S]*required: required/,
  'Step 1 must build the pinned-or-open context ledger and require the restated requirement');
assert.match(cs['1'], /Your own interpretation never pins a context/,
  'only the requirement or the user may pin a context, never the assistant');

// Step 2 locks the expected values BEFORE a candidate exists, and gates them with anchor_coverage.
assert.match(cs['2'], /## Step 2: Lock the expected values before any candidate exists/,
  'Step 2 must lock the expected values before authoring');
assert.match(cs['2'], /name: equivalenceGrid[\s\S]*name: openGrains[\s\S]*verify:\s*\r?\n\s*- kind: anchor_coverage\s*\r?\n\s*anchors: expectedValues/,
  'Step 2 must declare the proof lattice and requirement-silent grains and gate them through anchor_coverage');
assert.match(cs['2'], /This partition is declared before authoring and cannot later shrink/,
  'the open partition must be born at Step 2, never declared after evidence');
assert.match(cs['2'], /optional axis/i, 'the anchor recipe must offer the shaped (axis) anchor form');
assert.match(cs['2'], /OUTSIDE DAX[\s\S]*small GROUPED\s+row extract/i,
  'Step 2 must derive anchors from small grouped extracts with arithmetic outside DAX');

// Step 3 authors ONE candidate and enforces the locked anchors through the structured gate.
assert.match(cs['3'], /## Step 3: Author ONE canonical candidate[\s\S]*name: expectedValues[\s\S]*REQUIRED RECEIPT[\s\S]*required: optional[\s\S]*verify:\s*\r?\n\s*- kind: expected_values\s*\r?\n\s*anchors: expectedValues/,
  'Step 3 must expose an optional receipted anchor revision that shadows Step 2 only when answered, and always enforce the latest set');
assert.match(cs['3'], /Every changed anchor object must contain originalExpect[\s\S]*correctedExpect[\s\S]*row-returning extractQuery/,
  'anchor revisions must name the engine-enforced receipt fields and live row-returning query');
assert.match(cs['3'], /without changing its contexts[\s\S]*scalar-constant extracts are refused/,
  'anchor revisions must preserve locked contexts and reject non-extract receipts');

// Step 4 is the hard gate: an independent witness, proven equal over the locked grid, then finalized.
assert.match(cs['4'], /## Step 4: Reconcile against an independent witness, then finalize honestly/,
  'Step 4 must reconcile the candidate against an independent witness and finish honestly');
assert.match(cs['4'], /```yaml gate\s*\r?\n\s*strictness: hard/,
  'Step 4 must carry the hard gate; the shipped seed has exactly one and this is it');
assert.match(cs['4'], /verify:\s*\r?\n\s*- kind: dax_equivalence\s*\r?\n\s*probe: witnessDax\s*\r?\n\s*openShapesFrom: openGrains/,
  'the hard gate must prove candidate-against-witness equality over the Step-2 open-grain partition');
assert.match(cs['4'], /- kind: expected_values\s*\r?\n\s*anchors: expectedValues/,
  'the hard gate must also re-enforce the locked expected values, so equality alone cannot pass the step');
assert.match(cs['4'], /name: witnessDax[\s\S]*required: required\s*\r?\n\s*daxPurity: no-bare-measures/,
  'the witness must be required and engine-checked for bare measure references, so it cannot be declined or faked');
assert.match(cs['4'], /SARGable[\s\S]*grouped SUMMARIZECOLUMNS extracts/,
  'the witness must be independent AND efficient, not merely independent');
assert.match(cs['4'], /Never wrap a\s*\r?\n?\s*bare FILTER over ALL of a large fact table/,
  'the seed must forbid a bare FILTER over ALL of the large fact table');
assert.match(cs['4'], /MODEL FLOOR[\s\S]*base aggregation the metric sits on at the SAME grid/,
  'the performance pass must grade against the base-aggregation model floor, not a bare time threshold');
assert.match(cs['4'], /A skipped, offline or zero-coverage verify is not a pass[\s\S]*OVERRIDDEN/,
  'a skipped hard gate must downgrade the certificate rather than pass quietly');

assert.doesNotMatch(canonical, /verified-measure-v51|prowfv|probench|23M-row|v1 record|v5 witness/i,
  'benchmark arm names and benchmark-specific wording must not leak into the stock workflow');

assert.match(forms, /Add evidence report/, 'workflow steps must expose evidence as a first-class addable action');
assert.match(forms, /Evidence exports only after the run is completed or aborted/, 'the designer must explain when evidence becomes final');

// =====================================================================================================
// THE ADD-A-STEP CONTROLS. 1.2.0 shipped with the only add control inside a selected step's form
// (workflowforms.tsx "Add step after"). A new workflow has zero steps, so nothing could be selected and
// nothing could be added: Kane's "under author there is no add step button". Each assertion below names
// one control that must not disappear again. What they prove is that the source still declares them;
// that they render and work is Semanticus.VSCode/tools/drive-authoring.mjs, which clicks all four
// journeys in the built bundle, and the zero-step draft test in workflow-document.test.mjs.
// =====================================================================================================
const canvas = read('webview/src/workflowcanvas.tsx');
assert.match(forms, /export function WorkflowEmptySteps/,
  'the form area needs its own empty state, or a zero-step workflow is a dead end where the settings form used to sit');
assert.match(forms, /No steps yet[\s\S]{0,400}Add the first step/,
  'the empty state must say what a step is and carry the primary add action');
assert.match(designer, /Add the first step<\/button>/,
  'the Steps rail must carry an add action when the workflow has no steps at all');
assert.match(designer, /\+ Add step<\/button>/,
  'the Steps rail must keep a persistent add control at its end at every step count, the way the 1.1.3 outline did');
assert.match(designer, /aria-label=\{`Insert a step after step \$\{index \+ 1\}`\}/,
  'the rail must offer an insert control between steps, not only an append at the end');
assert.match(designer, /steps\.length === 0[\s\S]{0,200}addFirstStep/,
  'the zero-step case must be what decides the rail add control, not the selection');
// Canvas used to carry a third WorkflowEmptySteps card in its right-hand form column. That column is
// gone (the step editor is a drawer that only exists while a step is chosen, 2026-09-15), so the Canvas
// zero-step add action is the canvas's own centred empty state instead. Both paths are still asserted:
// two cards in Steps, and the canvas empty state below.
assert.match(designer, /<WorkflowEmptySteps[\s\S]{0,4000}<WorkflowEmptySteps/,
  'Steps (settings selected) and Steps (no step selected) must each offer the empty-state add action');
assert.match(canvas, /data-wf-canvas-empty="true"[\s\S]{0,600}Add the first step/,
  'Canvas must offer the same add action from its own empty state, since it no longer has a form column');
assert.match(designer, /onAddStep=\{steps\.length \? addStepAtEnd : addFirstStep\}/,
  'Canvas must get an add control of its own, because with zero steps no step can be selected to add from');
assert.match(canvas, /data-wf-canvas-empty/,
  'the canvas body must show its own empty state rather than an empty grid');
assert.match(canvas, /onAddStep && <Button primary disabled=\{addDisabled\}/,
  'the canvas empty state must use the shared Button, so a control restyle reaches this surface too');
assert.match(designer, /const STOCK_EDIT_NOTE = 'Copy to this project to edit';/,
  'a read-only built-in must explain how to get an editable copy');
assert.match(designer, /\{stock && <small className="sem-wf-restriction">\{STOCK_EDIT_NOTE\}<\/small>\}/,
  'the read-only note must sit beside the disabled rail controls, not replace them');
assert.match(forms, /\{readOnlyNote && <small className="sem-wf-restriction">\{readOnlyNote\}<\/small>\}/,
  'the read-only note must sit beside the disabled step actions, so a built-in shows controls and a reason, never an absence');
assert.match(forms, /focusKey !== step\.key \|\| !titleRef\.current/,
  'a step added from any control must land with the caret in its title');
assert.doesNotMatch(designer, /[\u2014\u2013]/, 'the designer must carry no em or en dashes (product copy rule)');
assert.doesNotMatch(forms, /[\u2014\u2013]/, 'the step forms must carry no em or en dashes (product copy rule)');
assert.match(workflows, /'check-blast-radius': 'Review a change'/, 'Check blast radius must live with change review');
assert.match(workflows, /'governed-rename': 'Review a change'/, 'Safe rename must live beside the blast-radius workflow');
assert.match(workflows, /data-workflow=\{w\.name\}/, 'the workflow rail must remain directly targetable by the visual harness');

// =====================================================================================================
// [T171] THE SCREENSHOT HARNESS FIXTURE IS THE SHIPPED LIBRARY, OR THE PICTURES ARE FICTION.
// Every Workflows-tab capture is drawn from the mocked WORKFLOWS list in tools/uishot/harness.html, and
// that list is what anyone judging the rail's copy, grouping and label fit is actually looking at. It had
// drifted to 24 stock rows, half of them parked or deleted, with three shipped seeds missing, and nothing
// noticed: four extension tests read harness.html and not one of them read the fixture. This does.
//
// It reads the seed directory, not a list written here, so a new stock workflow fails this immediately.
// What it does NOT prove: that the harness RENDERS the fixture faithfully (that is the screenshot), and
// it recomputes `gated` from the gate fences rather than through the engine's own parser, so it mirrors
// WorkflowRunner.BuildInfo only for the default case of no settings-level or global strictness override.
// =====================================================================================================
const harness = read('tools/uishot/harness.html');
const seedDir = resolve(root, '../Semanticus.Engine/workflows');

const unquote = (raw) => {
  const s = raw.trim();
  return s.length >= 2 && s[0] === s[s.length - 1] && (s[0] === '"' || s[0] === "'") ? s.slice(1, -1) : s;
};

const readSeed = (fileName) => {
  const text = readFileSync(resolve(seedDir, fileName), 'utf8');
  const split = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/);
  assert.ok(split, `${fileName} must open with a frontmatter block`);
  const front = {};
  for (const line of split[1].split(/\r?\n/)) {
    const pair = line.match(/^([A-Za-z][A-Za-z0-9_]*): ?(.*)$/);
    // A wrapped value would be read as a key-less continuation and quietly lost, so refuse it instead.
    assert.ok(pair, `${fileName} frontmatter line is not a single 'key: value' pair this guard can read: ${line}`);
    front[pair[1]] = pair[2].trimEnd();
  }
  const body = split[2];
  // The parser's own anchored heading shape (WorkflowParser.StepHeading), so an indented pretend-heading
  // is not counted here either.
  const steps = body.match(/^##\s*Step\s+\d+\s*:.*$/gm) || [];
  // HasEnforcedGate: any gate that is not switched off and asks for inputs or runs a verify.
  const gated = (body.match(/^```\s*yaml\s+gate\s*$\r?\n([\s\S]*?)^```\s*$/gm) || []).some((fence) =>
    !/^strictness:\s*['"]?off['"]?\s*$/m.test(fence) && /^(inputs|verify):/m.test(fence.replace(/^```.*$/gm, '')));
  return {
    name: front.name,
    title: unquote(front.title || ''),
    description: unquote(front.description || ''),
    version: Number(front.version ?? 1),
    stepCount: steps.length,
    gated,
    triggers: (front.triggers || '[]').trim().replace(/^\[|\]$/g, '').split(',').map((t) => unquote(t)).filter(Boolean),
  };
};

const seeds = readdirSync(seedDir).filter((f) => f.endsWith('.md')).sort().map(readSeed);
assert.ok(seeds.length > 0, 'the shipped workflow seed directory must not be empty');

const literal = harness.match(/const WORKFLOWS = cfg\.workflows2 \|\| (\[[\s\S]*?\n {4}\]);/);
assert.ok(literal, 'the harness must keep WORKFLOWS as one readable array literal, or this guard goes blind');
const wfInfo = (o) => ({
  name: '', title: '', description: '', version: 1, source: 'stock', stepCount: 0,
  gated: false, triggers: [], error: null, enabled: true, active: true, activeReason: null, ...o,
});
const fixture = new Function('wfInfo', `return ${literal[1]};`)(wfInfo);
const fixtureStock = fixture.filter((w) => w.source === 'stock');

assert.deepEqual(
  fixtureStock.map((w) => w.name).sort(),
  seeds.map((s) => s.name).sort(),
  'the harness stock rail must be exactly the shipped seeds: no parked entry, no deleted name, none missing');

for (const seed of seeds) {
  const shown = fixtureStock.find((w) => w.name === seed.name);
  for (const field of ['title', 'description', 'version', 'stepCount', 'gated']) {
    assert.deepEqual(shown[field], seed[field], `harness fixture '${seed.name}' has a stale ${field}`);
  }
  assert.deepEqual(shown.triggers, seed.triggers, `harness fixture '${seed.name}' has stale triggers`);
}

// The three deliberate non-catalog rows earn their place as STATES, not as library entries: a learned
// user workflow, a v2 file with loop/call provenance, and a file the parser refused (shown, never hidden).
assert.deepEqual(
  fixture.filter((w) => w.source !== 'stock').map((w) => w.name).sort(),
  ['broken-workflow', 'loop-handoff', 'month-end-close'],
  'the harness must keep exactly the three deliberate non-stock states');
assert.ok(fixture.find((w) => w.name === 'broken-workflow').error,
  'the parse-error row is only a state while it still carries its error');

// The rail is only half of it. Opening a row renders WORKFLOW_DEFS, which is where the playbook page gets
// its heading and its "Runs on" chips, so a stale def there is a wrong picture of the same library.
// Its hand-written steps are the mock and are not checked against the file; the metadata is.
// The region runs from the declaration to the last assignment that is followed by a blank line. It is
// deliberately not anchored on the comment that follows, because naming the op in that comment would
// credit this file as coverage evidence for operations it never exercises.
// THE REGION MATCH MUST NOT DEPEND ON THE CHECKOUT'S LINE ENDINGS. The closing `};` was anchored with a
// bare `\n`, which is `\r\n` wherever git checks the harness out with CRLF. The Windows runner does exactly
// that, so this guard returned null there while Linux stayed green: measured on PR #359, 2026-09-06, where
// the Windows leg failed 45/46 files at this line and every other assertion in this file passed. Note the
// WORKFLOWS literal above survives CRLF only because its pattern ends before the newline; being accidentally
// safe is not the same as being written to be safe, which is why every newline here is now `\r?\n`.
const DEFS_REGION = /const WORKFLOW_DEFS = cfg\.workflowDefs \|\| \{[\s\S]*?\r?\n {4}\};\r?\n(?=\r?\n)/;
const matchDefsRegion = (text, label) => {
  const region = text.match(DEFS_REGION);
  assert.ok(region, `the harness must keep the WORKFLOW_DEFS fixtures in one readable region (${label})`);
  return region[0];
};
const readDefs = (text, label) => new Function('cfg', 'gi', `${matchDefsRegion(text, label)}\nreturn WORKFLOW_DEFS;`)(
  {}, (name, question, type, required) => ({ name, question, type, required: required || 'answer-or-decline' }));
const defs = readDefs(harness, 'as checked out');

// BOTH LINE-ENDING FORMS, EXERCISED RATHER THAN ASSUMED. The defect was invisible everywhere the working
// copy happened to be LF, which is every Linux leg and every developer here. Converting the real harness
// both ways is what makes a CRLF checkout a case this suite actually runs, instead of one CI discovers.
// The comparison is on the NORMALISED region text, not on the parsed objects: a fixture value spanning
// lines legitimately carries the checkout's own newlines, so comparing parsed strings would fail for a
// reason that is not a defect.
{
  const asLf = harness.replace(/\r\n/g, '\n');
  const asCrlf = asLf.replace(/\n/g, '\r\n');
  assert.notEqual(asCrlf, asLf, 'the line-ending controls must actually differ, or they prove nothing');
  assert.equal(matchDefsRegion(asCrlf, 'CRLF checkout').replace(/\r\n/g, '\n'), matchDefsRegion(asLf, 'LF checkout'),
    'the WORKFLOW_DEFS region must read identically whichever line endings the checkout used');
  assert.deepEqual(Object.keys(readDefs(asCrlf, 'CRLF checkout')), Object.keys(readDefs(asLf, 'LF checkout')),
    'the same fixtures must be found in a CRLF checkout as in an LF one');
}
// A blank line dropped into the middle of that region would silently shorten it, and a guard that checks
// three of five fixtures is worse than none. Every assignment in the file must have made it into the object.
for (const [, assigned] of harness.matchAll(/WORKFLOW_DEFS\['([^']+)'\]\s*=/g)) {
  assert.ok(defs[assigned], `WORKFLOW_DEFS['${assigned}'] is defined outside the region this guard reads`);
}

for (const def of Object.values(defs)) {
  const seed = seeds.find((s) => s.name === def.name);
  if (!seed) {
    assert.notEqual(def.source, 'stock', `WORKFLOW_DEFS['${def.name}'] claims to be stock but ships from nowhere`);
    continue;
  }
  for (const field of ['title', 'description', 'version']) {
    assert.deepEqual(def[field], seed[field], `WORKFLOW_DEFS['${def.name}'] has a stale ${field}`);
  }
  assert.deepEqual(def.triggers, seed.triggers, `WORKFLOW_DEFS['${def.name}'] has stale triggers`);
  assert.equal(def.steps.length, seed.stepCount, `WORKFLOW_DEFS['${def.name}'] mocks the wrong number of steps`);
}

console.log('workflow hero and evidence-action tests passed');
