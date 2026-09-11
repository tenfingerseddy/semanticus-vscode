// C1.6 Publish surface: one word, one confirm, Ctrl+S never publishes, Deploy header resolves,
// compare target resets on model switch, account is named, empty Fabric Git id is refused.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { shouldApplyDaxBuffer } from '../out/daxHeader.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const copy = await loadTypeScriptModule(resolve(root, 'webview/src/publishcopy.ts'));
const pkg = JSON.parse(read('package.json'));
const deploy = read('webview/src/deploy.tsx');
const compare = read('webview/src/compare.tsx');
const app = read('webview/src/App.tsx');
const extension = read('src/extension.ts');
const propgrid = read('media/propgrid/propgrid.js');
const help = read('webview/src/help.tsx');

let passed = 0;
const test = (name, fn) => { fn(); passed++; };

test('D-007 palette and tree use the word Publish', () => {
  const cmd = pkg.contributes.commands.find((c) => c.command === 'semanticus.saveToLive');
  assert.equal(cmd.title, copy.PUBLISH_COMMAND_TITLE);
  assert.match(cmd.title, /Publish/);
  assert.doesNotMatch(cmd.title, /Save to Live|deploy metadata/i);
});

test('D-007 Ship page has a Publish control and no Push changes mode button', () => {
  assert.match(deploy, />Publish</);
  assert.match(deploy, /data-testid="publish-button"/);
  assert.doesNotMatch(deploy, />Push changes</);
  assert.match(deploy, /Choose what to publish/);
});

test('D-007 chip, palette, and tree all open the Deploy confirm', () => {
  assert.match(extension, /navigateStudio\(extCtx, 'deploy', 'publish'\)/);
  assert.match(app, /m\.tab === 'deploy' && m\.target === 'publish'/);
  assert.match(extension, /publishStatus\.command = 'semanticus\.saveToLive'/);
  assert.match(extension, /\$\(cloud-upload\)/);
});

test('D-007 confirm card is the one write surface', () => {
  assert.match(deploy, /data-testid="publish-confirm"/);
  assert.match(deploy, /data-testid="publish-account"/);
  assert.match(deploy, /deployLive/);
  assert.doesNotMatch(extension, /showWarningMessage\(\s*`Deploy \$\{dry\.totalChanges\}/);
});

test('chip text hides when the model is not live and names Publish when it is', () => {
  assert.equal(copy.publishChipText({ liveBound: false }), null);
  assert.equal(copy.publishChipText({ liveBound: true }), 'Publish');
  assert.equal(copy.publishChipText({ liveBound: true, changeCount: 1 }), 'Publish · 1 change');
  assert.equal(copy.publishChipText({ liveBound: true, changeCount: 3 }), 'Publish · 3 changes');
  assert.equal(copy.publishChipText({ liveBound: true, changeCount: 0 }), 'Publish · up to date');
  assert.equal(copy.publishChipText({ liveBound: true, changeCount: 0, liveOnlyCount: 1 }), 'Publish · 1 to remove');
  assert.equal(copy.publishChipText({ liveBound: true, previewError: 'sign-in expired' }), 'Publish · could not check');
});

test('D-010 header resolves instead of hanging on loading', () => {
  const loading = copy.deployHeaderState({
    loading: true, liveBound: true, modelName: 'V2', targetName: 'V2', lastRestore: 'none',
  });
  assert.equal(loading.resolved, false);
  const idle = copy.deployHeaderState({
    loading: false, liveBound: true, modelName: 'V2', targetName: 'V2', lastRestore: '2h ago',
  });
  assert.equal(idle.resolved, true);
  assert.match(idle.line, /click Publish to review changes/);
  assert.doesNotMatch(idle.line, /working-copy state loading|working-copy changes not counted|drift: not checked/);
  const file = copy.deployHeaderState({
    loading: false, liveBound: false, modelName: 'Contoso', targetName: 'No live target', lastRestore: 'none',
  });
  assert.equal(file.resolved, true);
  assert.match(file.line, /not connected to a live model/);
  const counted = copy.deployHeaderState({
    loading: false, liveBound: true, modelName: 'V2', targetName: 'V2', changeCount: 1, lastRestore: 'none',
  });
  assert.match(counted.line, /1 change waiting/);
});

test('D-010 source no longer uses the unresolved git header sentences', () => {
  assert.doesNotMatch(deploy, /working-copy state loading/);
  assert.doesNotMatch(deploy, /working-copy changes not counted/);
  assert.doesNotMatch(deploy, /drift: not checked/);
});

test('D-008 compare remounts when the session changes', () => {
  assert.equal(copy.shouldResetCompareOnSessionChange('sess-a', 'sess-b'), true);
  assert.equal(copy.shouldResetCompareOnSessionChange('sess-a', 'sess-a'), false);
  assert.equal(copy.shouldResetCompareOnSessionChange(null, 'sess-b'), false);
  assert.match(deploy, /<CompareView key=\{session\?\.sessionId \?\? 'none'\}/);
});

test('D-012 confirm names the account, or says it is unknown', () => {
  assert.equal(copy.publishAccountLine('megan@contoso.com'), 'As megan@contoso.com');
  assert.equal(copy.publishAccountLine(''), copy.UNKNOWN_ACCOUNT);
  assert.equal(copy.publishAccountLine(null), copy.UNKNOWN_ACCOUNT);
  assert.match(deploy, /publishAccountLine\(/);
});

test('D-011 empty Fabric Git workspace id returns a visible message', () => {
  assert.equal(copy.fabricGitWorkspaceMessage(''), copy.FABRIC_GIT_EMPTY_ID);
  assert.equal(copy.fabricGitWorkspaceMessage('   '), copy.FABRIC_GIT_EMPTY_ID);
  assert.equal(copy.fabricGitWorkspaceMessage('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'), null);
  assert.match(deploy, /fabricGitWorkspaceMessage/);
  assert.doesNotMatch(deploy, /disabled=\{!fgWs\.trim\(\)\}/);
});

test('D-044 unmodified DAX buffer is a no-op', () => {
  assert.equal(shouldApplyDaxBuffer('SUM ( Sales[Amount] )', 'SUM ( Sales[Amount] )'), false);
  assert.equal(shouldApplyDaxBuffer('SUM ( Sales[Amount] )', 'SUM ( Sales[Amount] ) * 1.02'), true);
  assert.equal(shouldApplyDaxBuffer(undefined, 'SUM ( Sales[Amount] )'), true);
  assert.match(extension, /shouldApplyDaxBuffer/);
});

test('D-044 Ctrl+S in Properties applies the focused field and never publishes', () => {
  const bindings = pkg.contributes.keybindings.filter((k) => k.key === 'ctrl+s' || k.mac === 'cmd+s');
  assert.ok(bindings.some((k) => k.command === 'semanticus.save' && /semanticusStudio/.test(k.when)));
  assert.ok(bindings.some((k) => k.command === 'semanticus.save' && /semanticusModel/.test(k.when)));
  assert.ok(bindings.some((k) => k.command === 'semanticus.applyPropertyField' && /semanticusProperties/.test(k.when)));
  assert.ok(bindings.some((k) => k.command === 'semanticus.save' && /semanticusConnections/.test(k.when)));
  assert.ok(!bindings.some((k) => k.command === 'semanticus.saveToLive'));
  assert.match(propgrid, /m\.type === 'applyFocused'/);
  assert.match(extension, /semanticus\.applyPropertyField/);
});

test('confirm button names Publish and the destination', () => {
  assert.equal(copy.publishButtonLabel(1, 'V2'), 'Publish 1 change to V2');
  assert.equal(copy.publishButtonLabel(2, 'V2'), 'Publish 2 changes to V2');
  assert.equal(copy.publishButtonLabel(1, 'V2', 1), 'Publish 1 change and remove 1 from V2');
  assert.match(copy.nothingToPublishCopy('V2'), /Nothing to publish/);
  assert.match(copy.nothingToPublishCopy('V2', ['UAT Added']), /Nothing new to publish/);
  assert.match(copy.previewFailedCopy('V2', 'sign-in expired'), /Could not check V2/);
});

test('help and compare use the word Publish for the live write', () => {
  assert.doesNotMatch(help, /Save to Live Model/);
  assert.match(help, /Publish/);
  assert.doesNotMatch(compare, /embedded \? 'Push changes'/);
});

test('D-180 publish button label never invites a no-op write', () => {
  assert.equal(copy.publishButtonLabel(0, 'V2'), 'Nothing to publish');
  assert.doesNotMatch(copy.publishButtonLabel(0, 'V2'), /^Publish/);
  assert.equal(copy.publishButtonLabel(1, 'V2'), 'Publish 1 change to V2');
});

test('D-181 the confirm says it is still checking, not nothing to publish', () => {
  assert.match(copy.checkingCopy('Contoso'), /Checking Contoso/);
  assert.doesNotMatch(copy.checkingCopy('Contoso'), /Nothing to publish/);
  assert.match(deploy, /busy && !preview \? checkingCopy\(dest\)/);
});

test('D-181 a new check clears the old preview before it starts', () => {
  const loadPreview = deploy.slice(deploy.indexOf('async function loadPreview'), deploy.indexOf('function openPublish'));
  assert.ok(loadPreview.indexOf('setPreview(null)') >= 0);
  assert.ok(loadPreview.indexOf('setPreview(null)') < loadPreview.indexOf('setPreviewBusy(true)'));
});

test('D-179 a successful publish reloads restore points', () => {
  const confirmPublish = deploy.slice(deploy.indexOf('async function confirmPublish'), deploy.indexOf('async function loadRestorePoints'));
  assert.match(confirmPublish, /loadRestorePoints\(\)/);
});

test('D-178/D-226 publish is gated on the resolved destination, not liveBound alone', () => {
  assert.match(deploy, /const canPublish = liveBound \|\| !!/);
  assert.match(deploy, /context\?\.publishing\?\.available/);
  assert.match(deploy, /disabled=\{!canPublish \|\| previewBusy \|\| publishBusy\}/);
  assert.doesNotMatch(deploy, /disabled=\{!liveBound \|\| previewBusy \|\| publishBusy\}/);
});

console.log(`publish surface tests passed (${passed})`);

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}
