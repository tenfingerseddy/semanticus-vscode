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

// And the pane must actually USE them: analyze passes the merged string[]; Browse picks never touch the free text.
assert.match(lineage, /mergeReportPaths\(pickedPaths, pbirPath\)/, 'local analyze must merge structured picks with the free-text paths');
assert.doesNotMatch(lineage, /setPbirPath\(picked/, 'Browse picks must never be joined into the free-text field');

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

// Async-invalidation contracts (the token mechanics live in React state, so pin the load-bearing source shapes):
// selection/identity edits must invalidate UNCONDITIONALLY — `if (reports)` skipped the bump while a load was in
// flight (reports === null), letting the old request commit the previous workspace's reports under the new choice.
assert.doesNotMatch(lineage, /if \(reports\) resetDiscovery/, 'workspace/identity edits must never gate invalidation on reports being present');
assert.match(lineage, /function resetDiscovery\(\) \{ cloudGen\.current\+\+/, 'a selection edit must orphan the in-flight cloud call (token bump)');
assert.match(lineage, /function resetWorkspaces\(\) \{ cloudGen\.current\+\+/, 'an identity edit must orphan the in-flight cloud call (token bump)');
assert.match(lineage, /sessionIdentityKey\(session\)/, 'the model-switch guard must key on the composite session identity, not the endpoint alone');
assert.match(lineage, /nextTenantValue\(tenant, tenantOwner\.current/, 'tenant prefill must flow through the ownership decision helper');

// Progress belongs to one caller-issued cloud-analysis invocation. Notifications are broadcast to every client, so
// the pane must pass its run id to the RPC request and reject same-op progress from MCP or concurrent RPC callers.
assert.match(lineage, /const runId = crypto\.randomUUID\(\)/, 'the webview caller must create a fresh id per cloud analysis');
assert.match(lineage, /'analyzeCloudReports',[\s\S]*tenant\.trim\(\) \|\| null, runId\)/, 'the caller-issued id must travel in the analyze RPC args');
assert.match(lineage, /p\.runId === activeCloudRunId\.current/, 'the progress listener must accept only the active invocation');
assert.match(lineage, /activeCloudRunId\.current = null/, 'invalidated and completed analyses must stop accepting late progress');

console.log('published-report selection helper tests passed');
