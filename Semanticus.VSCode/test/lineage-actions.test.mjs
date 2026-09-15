import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const lineage = read('webview/src/lineage.tsx');
const impact = read('webview/src/lineage-impact.tsx');
const app = read('webview/src/App.tsx');
const tree = read('webview/src/lineagetree.tsx');
const model = read('webview/src/lineage-model.ts');

// ---- the four buttons are gone (Astra's UAT, September 2026) --------------------------------------
// Check blast radius, Safe rename, Stage removal and Create probes sent the SAME request and drew the SAME
// card; one of them created nothing at all. Picking a thing is the question now, and each remaining action
// does what its verb says. The page-level contract for the replacement lives in impact-one-page.test.mjs.
for (const gone of ['Check blast radius', 'Safe rename', 'Stage removal', 'Create probes'])
  assert.ok(!new RegExp('>' + gone + '<').test(lineage + impact), `${gone} must not come back`);
assert.ok(!/Stage in Change Plan/.test(lineage + impact), 'a button that only adds a proposal must say Propose removal');

assert.match(lineage, /rpc<ImpactAssessmentResult>\('impactAssessment'/, 'the card must consume the engine-owned assessment');
assert.match(lineage, /scope: 'modelAndReports'/, 'the page must ask for report coverage rather than imply model-only safety');
assert.doesNotMatch(lineage, /reportPaths:/, 'the reports come from the engine scope now, not from a path list the page holds');
assert.match(lineage, /rpc\('addPlanItem', r, 'delete_if_unused'/, 'removal must be proposed with the self-re-verifying kind');
assert.match(model, /This adds a proposal to Changes > Proposed\. The field stays until you apply the proposal\./,
  'the proposal must say where it goes and that nothing left the model');
assert.match(app, /onOpenPlan=\{\(\) => goTab\('optimize'\)\}/, 'a proposal must route into the existing Changes view');
assert.match(app, /onAddCheck=\{\(ref\) => goTab\('tests', undefined, ref, undefined, \{ kind: 'test', value: ref \}\)\}/,
  'Add a check must hand the measure, or the table for a row count, to the Tests page with its context');
// Rename opens the REAL rename journey with the field carried through: the object is revealed and selected
// in the model list, where its name is edited. The old button ran an assessment and offered a workflow link
// that renamed nothing and left the person to find the field again.
assert.match(lineage, /onRename=\{\(\) => \{ if \(root\) \{ revealInTree\(root\); onRename\?\.\(root\); \} \}\}/,
  'Rename must carry the field into the place its name is edited');
assert.match(app, /<WorkflowsView navTarget=\{workflowTarget\}/, 'the workflow hand-off must still select the exact playbook');
assert.doesNotMatch(app, /label: ['"](Blast radius|Safe rename|Probes)['"]/, 'Lineage verbs must not create top-level tabs');

// ---- the report reading belongs to the engine ----------------------------------------------------
// This is the UAT's most serious finding: the page kept its own analysis, so Impact asked the engine fresh
// with no reports and said reports were not checked, while the page said they were, and a delete rechecked
// the model only. One owner, read through these three ops.
for (const op of ['listReportScope', 'setReportScope', 'checkReports'])
  assert.match(lineage, new RegExp("'" + op + "'"), `the page must read the engine-owned report scope through ${op}`);
assert.doesNotMatch(lineage, /analyzeCloudReports/, 'the page must not hold a cloud analysis of its own again');
assert.match(lineage, /LEGACY_LINEAGE_MODES/, 'an old Safe to remove or Published reports link must still land somewhere real');

console.log('lineage action UI contract tests passed');

// ---- Lineage Tree: the resize refit must be attached to the CANVAS, not to the component's first render ----------
// The canvas is absent from the Tree's first render (the lineage graph arrives async, so the first paint is the
// "Loading…" panel). A ResizeObserver attached from a mount effect therefore read a null ref, returned, and never
// ran again, so a FRESH first visit had no refit at all and a window resize left cards outside the canvas until Fit
// was pressed by hand. These are the source shapes of the fix; the behaviour itself is driven in a real browser by
// `node tools/uishot/lineage-interact.mjs resize`, which is the check that actually proves it.
assert.match(tree, /const canvasRef = useCallback\(\(el: HTMLDivElement \| null\) => \{/, 'the canvas must take a callback ref so the observer attaches when the element does');
assert.match(tree, /<div ref=\{canvasRef\}/, 'the React Flow wrapper must use that callback ref');
assert.match(tree, /ro\.observe\(el\)/, 'the ResizeObserver must observe the element the callback ref was handed');
assert.doesNotMatch(tree, /containerElRef/, 'the container RefObject the mount effect read is what failed; it must not come back');
assert.match(tree, /if \(!interactedSinceFit\.current\) fitLive\.current\(/, 'a resize must still skip the refit after a manual pan or zoom');
assert.match(tree, /const canvasRef = useCallback\([\s\S]*?\}, \[\]\);/, 'the callback ref must have empty deps — a changing identity re-attaches the observer on every render');
// The minimap is a fixed ~200x150 overlay that fitView cannot know about, so on a short pane it covers real cards.
assert.match(tree, /const showMiniMap = /, 'the minimap must be conditional on the pane having room for it');
assert.match(tree, /\{showMiniMap && <MiniMap/, 'the MiniMap element must be gated by that decision');


// ---- Published-reports selection helpers (behavioral, per the bridge-timeouts extract-and-execute pattern) -----
// The helpers stay top-level pure functions in lineage.tsx so they remain statically extractable and runnable.
const { default: ts } = await import('typescript');
const helper = (name) => {
  const m = lineage.match(new RegExp(`function ${name}\\([\\s\\S]*?\\n\\}`));
  assert.ok(m, `${name} should remain a statically extractable top-level helper`);
  const js = ts.transpileModule(m[0], { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText;
  return Function(`"use strict"; ${js}; return ${name};`)();
};

// workspaceNameFromEndpoint: only a TRAILING /myorg/<name> segment counts; decode failures mean "no hint".
const wsName = helper('workspaceNameFromEndpoint');
assert.equal(wsName('powerbi://api.powerbi.com/v1.0/myorg/Contoso%20%5BTest%5D'), 'Contoso [Test]', 'the encoded workspace name must decode');
assert.equal(wsName('powerbi://api.powerbi.com/v1.0/myorg/Sales/'), 'Sales', 'a trailing slash is still the trailing segment');
assert.equal(wsName('powerbi://api.powerbi.com/v1.0/myorg/Sales/extra'), null, 'a NON-trailing /myorg/<name> must not match (the workspace is the last segment)');
assert.equal(wsName('powerbi://api.powerbi.com/v1.0/myorg/Bad%ZZ'), null, 'malformed percent-encoding must yield no hint, never the raw segment');
assert.equal(wsName('localhost:12345'), null, 'a local endpoint has no workspace');
assert.equal(wsName(null), null);
assert.equal(wsName(undefined), null);

// mergeReportPaths: structured Browse picks survive VERBATIM (a legal ';' or edge whitespace in a folder name must
// not be altered); only the FREE-TEXT half uses the ';'-split + trim convention; order-preserving deduped.
const merge = helper('mergeReportPaths');
assert.deepEqual(merge(['C:\\Reports\\FY25;Final.Report'], ''), ['C:\\Reports\\FY25;Final.Report'], 'a picked path containing ";" must pass through intact');
assert.deepEqual(merge(['C:\\Reports\\Trailing.Report '], ''), ['C:\\Reports\\Trailing.Report '], 'picks pass through exactly as the dialog returned them (no trim)');
assert.deepEqual(merge(['C:\\a.Report'], 'C:\\b.Report; C:\\a.Report'), ['C:\\a.Report', 'C:\\b.Report'], 'free text splits on ";" and duplicates collapse');
assert.deepEqual(merge([], ' ; ;C:\\x '), ['C:\\x'], 'blank free-text fragments are dropped, free-text paths trimmed');
assert.deepEqual(merge([], ''), [], 'nothing picked or typed is an empty set');

// And the drawer must actually USE it: every folder Browse returned is added, together with a path typed in
// the box beside it, so nothing a person pointed at is silently dropped.
assert.match(lineage, /mergeReportPaths\(picked \?\? \[\], folderPath\)/, 'Browse must add every folder it returned, plus the typed one');
assert.match(lineage, /mergeReportPaths\(\[\], folderPath\)/, 'the typed path goes through the same merge convention');

// sessionIdentityKey: the model-switch guard's composite key. The endpoint alone cannot distinguish two models in
// the SAME workspace, nor any pair of local models (liveEndpoint null for all) — sessionId must.
const identKey = helper('sessionIdentityKey');
const sameWs = 'powerbi://api.powerbi.com/v1.0/myorg/Contoso';
assert.notEqual(identKey({ sessionId: 's1', liveEndpoint: sameWs, liveDatabase: 'A' }),
  identKey({ sessionId: 's2', liveEndpoint: sameWs, liveDatabase: 'B' }), 'two models in one workspace must have distinct identities');
assert.notEqual(identKey({ sessionId: 's1' }), identKey({ sessionId: 's2' }), 'two LOCAL models (no endpoint) must have distinct identities');
assert.equal(identKey({ sessionId: 's1', liveEndpoint: sameWs, liveDatabase: 'A' }),
  identKey({ sessionId: 's1', liveEndpoint: sameWs, liveDatabase: 'A' }), 'the same open model must be a stable identity');
assert.equal(identKey(null), identKey(undefined), 'no session observed is one (empty) identity');

// nextTenantValue: explicit ownership. Only OUR prefill is ever replaced or cleared; a typed value is untouchable
// until the user empties the field (which returns ownership so prefill works again).
const nextTenant = helper('nextTenantValue');
assert.deepEqual(nextTenant('', null, 'contoso.com', false), { value: 'contoso.com', owner: 'auto' }, 'a blank field prefills');
assert.deepEqual(nextTenant('old.com', 'auto', 'new.com', true), { value: 'new.com', owner: 'auto' }, 'auto-to-auto: a model switch replaces our own prefill');
assert.deepEqual(nextTenant('old.com', 'auto', '', true), { value: '', owner: null }, 'auto-to-empty: a switch to a model with NO known tenant clears our stale prefill');
assert.deepEqual(nextTenant('mine.com', 'user', 'new.com', true), { value: 'mine.com', owner: 'user' }, 'a user-typed tenant is never replaced');
assert.deepEqual(nextTenant('mine.com', 'user', '', true), { value: 'mine.com', owner: 'user' }, 'a user-typed tenant is never cleared');
assert.deepEqual(nextTenant('', null, 'auto.com', true), { value: 'auto.com', owner: 'auto' }, 'a user-emptied field regains prefill on the next observation');
assert.deepEqual(nextTenant('old.com', 'auto', '', false), { value: 'old.com', owner: 'auto' }, 'no switch means no clearing (the model did not change)');

// Async-invalidation contracts (the token mechanics live in React state, so pin the load-bearing source shapes).
// A sign-in or tenant edit must orphan whatever cloud call is in flight: its result belongs to the OLD identity,
// and letting it commit would list one tenant's workspaces under another's choice.
assert.match(lineage, /onSignInMode=\{\(v\) => \{ setSignInMode\(v\); cloudGen\.current\+\+;/, 'a sign-in edit must orphan the in-flight cloud call');
assert.match(lineage, /onTenantId=\{\(v\) => \{[\s\S]{0,140}cloudGen\.current\+\+;/, 'a tenant edit must orphan the in-flight cloud call');
assert.match(lineage, /const gen = \+\+cloudGen\.current;/, 'each cloud load must claim a token at the start');
assert.match(lineage, /if \(gen !== cloudGen\.current\) return;/, 'a superseded cloud result must be dropped, never committed');
assert.match(lineage, /sessionIdentityKey\(session\)/, 'the model-switch guard must key on the composite session identity, not the endpoint alone');
assert.match(lineage, /nextTenantValue\(tenant, tenantOwner\.current/, 'tenant prefill must flow through the ownership decision helper');

// Progress notifications are broadcast to every client, so the listener must accept only this holder's own run.
assert.match(lineage, /p\.runId === activeCloudRunId\.current/, 'the progress listener must accept only the active invocation');
assert.match(lineage, /activeCloudRunId\.current = null/, 'an invalidated run must stop accepting late progress');

console.log('published-report selection helper tests passed');

// ===================================================================================================
// The compact tool row on Lineage (Kane, 2026-09-14: "each tool keeps one row of controls; counts,
// legends and status lines become quiet chips inside the canvas, never a row of their own").
//
// Measured in the built app at 1366x768 before this change: between the shell's segment strip and the
// drawing surface, the Tree mode put SIX rows and 284px, and the Graph mode SEVEN rows and 316px. The tree
// canvas was left 219px tall and the graph canvas 200px, on a 768px window. These assertions pin the
// one-row shape. The behaviour itself is driven in a real browser by tools/uishot/lineage-interact.mjs.
// ===================================================================================================
const graphView = read('webview/src/lineagegraph.tsx');
const studioCss = read('webview/src/styles.css');

// ONE row per mode. The mode switcher leads the row the mode's own controls sit on, exactly as Canvas and
// Relationships lead the Diagram's row.
for (const [what, src] of [['the Lineage tree', tree], ['the Lineage graph', graphView]])
  assert.equal((src.match(/<ToolRow[\s>]/g) ?? []).length, 1, `${what} must render exactly one tool row`);
assert.match(tree, /modeSwitch/, 'the tree must render the mode switcher inside its own tool row');
assert.match(graphView, /modeSwitch/, 'the graph must render the mode switcher inside its own tool row');

// The hero panel is gone: its sentence becomes the exported page note, not 98px of page.
assert.match(lineage, /export const LINEAGE_NOTES\s*:/, 'lineage.tsx must export LINEAGE_NOTES for the page notes the shell owns');
assert.doesNotMatch(lineage, /w-20 h-20 rounded-2xl/, 'the lineage hero block must be gone, not merely restyled');

// Status, counts and the honesty caveat move inside the canvas as chips.
assert.match(graphView, /<CanvasChip[\s\S]{0,300}nodeCount/, 'the graph node count must render as an in-canvas chip');
assert.match(graphView, /<CanvasChip[^>]*at="end"/, 'the edge-colour key must render as an in-canvas chip');
assert.match(tree, /<CanvasChip[\s\S]{0,300}root/, 'the tree root status must render as an in-canvas chip');
for (const [what, src] of [['the tree', tree], ['the graph', graphView]])
  assert.match(src, /caveat/, `${what} must still show the model-only caveat, moved into the canvas rather than dropped`);

// The slicers and the Fit behaviour survive the compaction: every control stays reachable.
for (const [what, src, labels] of [
  ['the tree', tree, ['Upstream', 'Downstream', 'Horizontal', 'Vertical', 'Expand all', 'Collapse', 'Fit']],
  ['the graph', graphView, ['Whole model', 'Focused', 'Force', 'Circular', 'Fit', 'Reset']],
]) for (const label of labels)
  assert.match(src, new RegExp('>[^<>]*' + label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '<'), `${what} must keep its ${label} control`);
for (const [what, src] of [['the tree', tree], ['the graph', graphView]]) {
  assert.match(src, /<MultiSelect label="Tables"/, `${what} must keep its Tables slicer`);
  assert.match(src, /<MultiSelect label="Fields"/, `${what} must keep its Fields slicer`);
}

// The kind filters fold into one Show menu so a long list of kinds cannot push the row onto a second line.
for (const [what, src] of [['the tree', tree], ['the graph', graphView]])
  assert.match(src, /<RowMenu label="Show"/, `${what} must fold its kind filters into one Show menu`);
assert.match(studioCss, /\.sem-toolrow\s*\{[^}]*flex-wrap:\s*nowrap/, 'the tool row must refuse to wrap onto a second line');

// The scripted browser check reads the selection through a stable hook rather than a sentence on a row that
// no longer exists.
assert.match(graphView, /data-sem-selected=/, 'the graph selection chip must expose the selected name to the scripted check');

console.log('lineage compact tool row tests passed');
