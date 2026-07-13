import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const app = readFileSync(resolve(root, 'webview', 'src', 'App.tsx'), 'utf8');
const deploy = readFileSync(resolve(root, 'webview', 'src', 'deploy.tsx'), 'utf8');
const context = readFileSync(resolve(root, 'webview', 'src', 'contextbar.tsx'), 'utf8');
const connections = readFileSync(resolve(root, 'webview', 'src', 'connectionsdrawer.tsx'), 'utf8');
const connection = readFileSync(resolve(root, 'webview', 'src', 'connection.tsx'), 'utf8');
const dataAgent = readFileSync(resolve(root, 'webview', 'src', 'dataagent.tsx'), 'utf8');
const spec = readFileSync(resolve(root, 'webview', 'src', 'spec.tsx'), 'utf8');
const bridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const tests = readFileSync(resolve(root, 'webview', 'src', 'tests.tsx'), 'utf8');
const diffview = readFileSync(resolve(root, 'webview', 'src', 'diffview.tsx'), 'utf8');
const interview = readFileSync(resolve(root, 'webview', 'src', 'interview.tsx'), 'utf8');
const permissions = readFileSync(resolve(root, 'webview', 'src', 'permissions.tsx'), 'utf8');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const pkg = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));

const groups = [...app.matchAll(/\{ id: '(understand|change|improve|prove|ship)', label: '([^']+)', tabs:/g)]
  .map((m) => [m[1], m[2]]);
assert.deepEqual(groups, [
  ['understand', 'Understand'], ['change', 'Change'], ['improve', 'Improve'], ['prove', 'Prove'], ['ship', 'Ship'],
]);

const groupBlock = app.slice(app.indexOf('const TAB_GROUPS'), app.indexOf('const TAB_TO_GROUP'));
const understandBlock = groupBlock.slice(groupBlock.indexOf("{ id: 'understand'"), groupBlock.indexOf("{ id: 'change'"));
const shipBlock = groupBlock.slice(groupBlock.indexOf("{ id: 'ship'"));
assert.doesNotMatch(understandBlock, /id: 'docs'/, 'Docs is a generated deliverable, not an Understand tool');
assert.match(shipBlock, /id: 'docs', label: 'Docs'/, 'Docs must remain in Ship');
assert.doesNotMatch(groupBlock, /id: 'compare'/, 'Compare must not return as a primary tab');
const changeBlock = groupBlock.slice(groupBlock.indexOf("{ id: 'change'"), groupBlock.indexOf("{ id: 'improve'"));
assert.match(changeBlock, /id: 'spec', label: 'Model Spec'/, 'Model Spec uniquely owns model creation and must remain under Change');
assert.doesNotMatch(app, /if \(t === 'spec'\) t = 'optimize'/, 'Spec routes must not be redirected to Change Plan');
assert.match(app, /<SpecView session=\{session\} \/>/, 'the restored Model Spec destination must render its real workspace');
assert.doesNotMatch(groupBlock, /id: 'dataagent'/, 'Data Agent must remain an advanced Ship capability');
assert.match(app, /TAB_TO_GROUP\.compare = 'ship'/, 'legacy Compare routes must still resolve inside Ship');
assert.match(app, /if \(t === 'compare'\) t = 'deploy'/, 'legacy Compare routes must redirect into Deploy');
assert.match(app, /TAB_TO_GROUP\.dataagent = 'ship'/, 'legacy Data Agent routes must still resolve inside Ship');
assert.doesNotMatch(app, /<CompareView seed=/, 'App must not retain a second top-level comparison surface');
assert.match(deploy, /<CompareView seed=\{reviewSeed\} embedded \/>/, 'Deploy must own the shared review and write path');
assert.match(deploy, /Unknown state stays explicit/, 'the state header must disclose unknowns');
assert.match(deploy, /rpc<ConnectionRecord\[]>\('listConnections'\)/, 'Deploy must read the shared target registry');
assert.match(deploy, /rpc<RestorePointRecord\[]>\('listRestorePoints'/, 'rollback must read engine-owned restore points');
assert.match(deploy, /rpc<RollbackResult>\('rollbackPush', restoreId, false/, 'rollback must preview before writing');
assert.match(deploy, /rpc<RollbackResult>\('rollbackPush', restoreId, true/, 'rollback confirmation must use the same engine op');
assert.match(deploy, /Anything listed under Will remove is deleted/, 'rollback must state the destructive consequence beside Confirm');
assert.doesNotMatch(diffview, /label: 'Map'|function MapMode/, 'Push Changes must not expose the retired Map mode');
assert.match(diffview, /label: 'Review'[\s\S]*label: 'Side by side'/, 'the two direct diff views must remain available');
assert.match(deploy, /mode === 'promote'.*<Panel title="Promote"/s, 'Promote must be a first-class Deploy mode');
assert.match(deploy, /advancedView === 'dataagent'.*dataAgent/s, 'Data Agent must live behind Deploy Advanced');
assert.match(deploy, /Nothing is promoted until the separate Deploy confirmation/, 'Promote must explain its two-step write boundary');
assert.match(context, /Review changes →/, 'the ambient context link must use the unified Deploy language');
assert.match(context, />Editing</);
assert.match(context, />Tests</);
assert.match(context, />Publish to</);
assert.match(connections, /Use for tests and queries[\s\S]*changes only where results come from/);
assert.match(connections, /Open live[\s\S]*does not create local files/);
assert.match(connections, /Work locally[\s\S]*keeps testing and final publishing as separate choices/);
assert.match(connections, /Existing files remain user-owned, including files already in source control/);
assert.match(connections, /setPublishDestination[\s\S]*Set as publish destination/, 'existing local and repository models must be able to link an explicit publish target');
assert.match(extension, /sendRequest<ModelConnectionRecord\[]>\('listConnections'\)/, 'the native picker must use the same engine registry as Studio');
assert.match(extension, /Create a new model[\s\S]*sendRequest<OpenResult>\('createModel'[\s\S]*navigateStudio\(extCtx, 'spec'\)/, 'the new-model entry must create one session then hand off to Model Spec');
assert.match(extension, /pickSpecFile[\s\S]*showSaveDialog[\s\S]*showOpenDialog/, 'saved specs must have native open and save paths');
assert.match(spec, /New model wizard[\s\S]*Start from scratch[\s\S]*Draft from a SQL source[\s\S]*Open a saved Model Spec/, 'the wizard must explain every supported starting point');
assert.match(spec, /setSpec[\s\S]*loadSpec[\s\S]*saveSpec/, 'wizard and file actions must converge on the shared engine spec');
assert.match(spec, /Build into model/, 'the reviewed spec must expose its build action');
assert.match(spec, /It does not publish/, 'the build boundary must remain explicit');
assert.match(bridge, /pickSpecFile[\s\S]*type: 'pickSpecFile'/, 'Model Spec file selection must use the native host picker');
assert.doesNotMatch(app, /<InterviewCard \/>/, 'AI Readiness must not retain the full interview evidence surface');
assert.match(app, /<InterviewSummaryChip onOpen=\{\(\) => goTab\('tests'\)\} \/>/, 'AI Readiness must route its latest-outcome chip to Tests');
assert.match(tests, /<InterviewCard /, 'Tests must own the full Model Interview evidence section');
assert.match(interview, /Evidence only[\s\S]*never change its grade or coverage/, 'the interview section must state its evidence-only boundary');
assert.doesNotMatch(extension, /function (getRecentXmla|rememberXmla|forgetXmla|pickRecentXmla)/, 'the extension must not retain a second XMLA history');
assert.match(extension, /migrateLegacyXmlaHistory[\s\S]*rememberXmlaConnection[\s\S]*globalState\.update\(LEGACY_RECENT_XMLA_KEY, undefined\)/, 'legacy history must migrate once into the engine registry, then be removed');
assert.match(connection, /Choose test model/);
assert.doesNotMatch(connection, /Attach XMLA|Attach local Power BI/, 'query tabs must use the shared Connections drawer, not a second attachment form');
assert.match(context, /publishing\?\.available[\s\S]*kind: 'workspace'/, 'review must prefer the selected publish destination');
assert.match(deploy, /context\?\.publishing\?\.endpoint \|\| session\?\.liveEndpoint/, 'Deploy must inherit the selected publish destination');
assert.match(dataAgent, /Build the source from editable[\s\S]*Change publish destination/, 'Data Agent must distinguish editable metadata from its live published source');
assert.match(app, /if \(!session\?\.sessionId\) \{ setPendingApprovals\(\[\]\); return; \}/,
  'the shell must not poll an approval queue without a connected model session');
assert.match(app, /onApprovalChanged=\{\(\) => \{ void refreshPendingApprovals\(\); \}\}/,
  'an approval decision must refresh the shell badge immediately');
assert.match(permissions, /onApprovalChanged\?\.\(\)[\s\S]*await pull\(\)/,
  'approve and deny must nudge the shell before refreshing their local queue');
assert.match(permissions, /That request has already been approved[\s\S]*That request is no longer waiting/,
  'a stale approval deep link must explain why no waiting card exists');
assert.match(permissions, /setDirectedId\(focusApproval\.id\)[\s\S]*setTimeout[\s\S]*setDirectedId/,
  'the directed-here ring must clear after the card has been shown');

const bindings = pkg.contributes.keybindings
  .filter((x) => x.command === 'semanticus.studioGoGroup')
  .map((x) => [x.args, x.key]);
assert.deepEqual(bindings, [
  ['understand', 'ctrl+shift+1'], ['change', 'ctrl+shift+2'], ['improve', 'ctrl+shift+3'],
  ['prove', 'ctrl+shift+4'], ['ship', 'ctrl+shift+5'],
]);

console.log('intent navigation tests passed');
