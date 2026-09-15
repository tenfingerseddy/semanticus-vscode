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
const dataagent = read('webview/src/dataagent.tsx');
const picker = read('webview/src/accountpicker.tsx');
const sharedCopy = await loadTypeScriptModule(resolve(root, 'webview/src/copy.ts'));

let passed = 0;
const test = (name, fn) => { fn(); passed++; };

test('D-007 palette and tree use the word Publish', () => {
  const cmd = pkg.contributes.commands.find((c) => c.command === 'semanticus.saveToLive');
  assert.equal(cmd.title, copy.PUBLISH_COMMAND_TITLE);
  assert.match(cmd.title, /Publish/);
  assert.doesNotMatch(cmd.title, /Save to Live|deploy metadata/i);
});

test('D-007 the header carries the one Publish control and the Ship page has no second one', () => {
  // The app has ONE publish action and it lives in the header. The Published page used to repeat it in its card
  // head, which left two accent buttons saying nearly the same thing, so these assertions moved to App.tsx.
  assert.match(app, />Publish…</);
  assert.match(app, /data-testid="publish-button"/);
  assert.doesNotMatch(deploy, /data-testid="publish-button"/);
  assert.doesNotMatch(deploy, />Publish</);
  assert.doesNotMatch(deploy, />Push changes</);
  assert.match(deploy, />What to publish</);
  assert.doesNotMatch(deploy, /Choose what to publish/);
  assert.doesNotMatch(help, /Choose what to publish/);
  // The review still opens above whatever mode you were in, now seeded by the header button's publish route.
  assert.match(deploy, /\{entry === 'review' && <PublishConfirm/);
  assert.match(app, /const openPublish = \(\) => goTab\('deploy', undefined, 'publish'\)/);
});

test('D-007 chip, palette, and tree all open the Deploy confirm', () => {
  assert.match(extension, /navigateStudio\(extCtx, 'deploy', 'publish'\)/);
  assert.match(app, /tool === 'deploy' && target === 'publish'/);
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
  assert.match(file.line, /no publish destination yet/);
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
  // The gate no longer hides a button on this page; it decides what the page shows when the header's Publish…
  // seeds a review. Without a destination the entry state is choose-destination and no confirm ever opens.
  assert.match(deploy, /const entry = publishEntryState\(\{ canPublish, publishOpen \}\)/);
  assert.match(deploy, /if \(!canPublish\) \{ setPublishOpen\(false\); return; \}/);
  assert.doesNotMatch(deploy, /disabled=\{!liveBound \|\| previewBusy \|\| publishBusy\}/);
});

test('UX12 the publish entry state decides what the card shows, and no destination outranks the rest', () => {
  assert.equal(copy.publishEntryState({ canPublish: false, publishOpen: false }), 'choose-destination');
  assert.equal(copy.publishEntryState({ canPublish: false, publishOpen: true }), 'choose-destination');
  assert.equal(copy.publishEntryState({ canPublish: true, publishOpen: true }), 'review');
  assert.equal(copy.publishEntryState({ canPublish: true, publishOpen: false }), 'compare');
});

test('UX12 the header names a missing destination instead of claiming no live model', () => {
  const none = copy.deployHeaderState({
    loading: false, liveBound: false, modelName: 'Contoso', targetName: 'No live target', lastRestore: 'none',
  });
  assert.match(none.line, /no publish destination yet/);
  assert.doesNotMatch(none.line, /not connected to a live model/);
});

test('UX12 the confirm renders only under the review state, and a destination can always be chosen', () => {
  assert.match(deploy, /entry === 'review' && <PublishConfirm/);
  assert.match(deploy, /entry === 'choose-destination'/);
  assert.match(deploy, /No publish destination yet/);
  assert.match(deploy, />Choose destination</);
  assert.doesNotMatch(deploy, /publishOpen && <PublishConfirm/);
});

test('UX12 rollback wording follows applied and failedRefs, never error absence', () => {
  assert.match(copy.rollbackResultLine({ applied: false, note: 'Contoso Sales already matches this restore point. Nothing to roll back.' }, 'Contoso Sales', '2h ago'), /already matches/);
  assert.doesNotMatch(copy.rollbackResultLine({ applied: false }, 'Contoso Sales', '2h ago'), /^Restored/);
  assert.match(copy.rollbackResultLine({ applied: true, failedRefs: ['measure:Sales/Margin %'] }, 'Contoso Sales', '2h ago'), /except 1 object that could not be restored: measure:Sales\/Margin %/);
  assert.match(copy.rollbackResultLine({ applied: true, failedRefs: [] }, 'Contoso Sales', '2h ago'), /^Restored Contoso Sales to the point from 2h ago\. Your local model edits are unchanged/);
  assert.equal(copy.rollbackResultLine({ applied: true, error: 'target gone' }, 'X', 'now'), 'target gone');
  assert.match(deploy, /setRestoreNote\(rollbackResultLine\(r, restored, when\)\)/);
  assert.doesNotMatch(deploy, /if \(!r\.error\) setRestoreNote/);
});

test('UX12 the remainder sentence needs a fresh comparison; a failed refresh keeps the write summary and says the list may be stale', () => {
  assert.equal(copy.publishedSubsetLine({ count: 2, target: 'Contoso Sales', failed: 0, refreshed: true }), 'Published 2 changes to Contoso Sales. The differences still shown are not yet published.');
  const stale = copy.publishedSubsetLine({ count: 1, target: 'Contoso Sales', failed: 1, refreshed: false });
  assert.match(stale, /^Published 1 change to Contoso Sales\. 1 could not be published\./);
  assert.match(stale, /could not be refreshed/);
  assert.doesNotMatch(stale, /not yet published/);
  assert.match(compare, /setRefreshStale\(!refreshed\)/);
  assert.match(compare, /publishedSubsetLine\(\{ count: result\.count/);
});

test('UX12 publish and restore results say the local copy is untouched', () => {
  assert.match(deploy, /Your local model edits are unchanged\./);
  assert.match(deploy, /Your local model edits stay unchanged\./);
  assert.doesNotMatch(deploy, /never changes your local files or your history/);
});

// M17: a publish destination that cannot be read must not be offered as a publish you can press, and the
// comparison must say it never reached the destination instead of showing a diff that looks complete.
test('M17 an unreachable destination disables Publish and says the comparison could not read it', () => {
  assert.match(compare, /onTargetRead\?:/, 'the comparison must report whether it reached the target');
  assert.match(compare, /onTargetRead\?\.\(\{ ok: false/, 'a failed compare reports the target as unread');
  assert.match(compare, /onTargetRead\?\.\(\{ ok: true/, 'a successful compare clears the unread state');
  assert.match(compare, /Could not read the destination/,
    'the embedded comparison must say plainly that it never read the destination');
  assert.match(deploy, /export function usePublishReach\(/, 'the header needs the destination reachability');
  assert.match(deploy, /onTargetRead=\{/, 'the Published page must listen to its own destination comparison');
  assert.match(app, /usePublishReach\(\)/, 'the header Publish must read the reachability');
  assert.match(app, /Cannot reach \$\{publishTarget \|\| 'the publish destination'\}/,
    'the header names the destination itself, so the button and the page never print different names for it');
  assert.doesNotMatch(deploy, /destination: targetName/,
    'the reachability flag must not carry a name the registry has not finished resolving');
  assert.match(app, /disabled=\{!contextResolved \|\| !!unreachable\}/,
    'Publish is disabled while the destination cannot be reached');
  const styles = read('webview/src/styles.css');
  assert.match(styles, /\.studio-publish:disabled \{[^}]*opacity/,
    'a Publish that cannot be pressed must not be painted as if it can');
});

// ---- Promote: WHO signs in (Kane, Yoga, 2026-09-14: "promote asks you to run az login") ---------------
// Promote hard-coded the Azure command line as the way to sign in and printed the raw credential failure
// above its own explanation. Kane's laptop has never run that command line, so the only control on the
// panel produced a red line he could do nothing with. The page now asks WHO should sign in, the same way
// the Data agent page already does, and says what a failed sign-in means in words.

test('T??? the account picker is one shared control, and both pages use it', () => {
  assert.match(picker, /export function AccountPicker\(/, 'the shared picker must be a component both pages import');
  assert.match(deploy, /from '\.\/accountpicker'/, 'Promote must import the shared picker');
  assert.match(dataagent, /from '\.\/accountpicker'/, 'the Data agent header must import the shared picker');
  assert.doesNotMatch(dataagent, /<option value="azcli">/, 'the Data agent header must not keep its own copy of the choices');
});

test('T??? the picker offers exactly the sign-in modes the engine can build a credential for', () => {
  assert.deepEqual(sharedCopy.SIGN_IN_MODES.map((m) => m.value),
    ['interactive', 'devicecode', 'azcli', 'serviceprincipal'],
    'the values are the engine mode ids from EntraToken.BuildCredentialWith, minus token (the caller supplies that one)');
  for (const mode of sharedCopy.SIGN_IN_MODES) {
    assert.match(mode.label, /^[A-Z]/, `${mode.value} needs a label that reads as a sentence`);
    assert.doesNotMatch(mode.label, /Entra|azcli|cli\b|principal/i, `${mode.value} label must say what the person does, not name the machinery`);
  }
  // There is no VS Code session credential in the engine, so no label may promise one.
  assert.doesNotMatch(picker + sharedCopy.SIGN_IN_MODES.map((m) => m.label).join(' '), /account VS Code signed in with/i);
});

test('T??? Promote no longer names a sign-in mode for the person', () => {
  // Every Promote call used to carry the literal. The picked mode reaches them instead.
  for (const call of ['listDeploymentPipelines', 'getPipelineStages', 'deployStage', 'deploymentHistory']) {
    const line = deploy.split('\n').find((l) => l.includes(`'${call}'`) && l.includes("'azcli'"));
    assert.equal(line, undefined, `${call} still passes the Azure command line as a literal: ${line}`);
  }
  assert.match(deploy, /rpc<DeploymentPipeline\[\]>\('listDeploymentPipelines', mode/, 'the pipeline read takes the picked mode');
});

test('T??? the starting mode prefers the account the publish destination was opened with', () => {
  assert.equal(sharedCopy.defaultSignInMode({ authMode: 'devicecode' }), 'devicecode', 'a known destination account wins');
  assert.equal(sharedCopy.defaultSignInMode({ authMode: 'ENTRAMFA' }), 'interactive', 'the engine aliases fold onto the mode the picker offers');
  assert.equal(sharedCopy.defaultSignInMode({ authMode: 'token' }), 'interactive', 'a raw-token connection is not a mode a person can pick');
  assert.equal(sharedCopy.defaultSignInMode({ authMode: 'azcli' }), 'azcli', 'a destination really opened with the command line keeps it');
  assert.equal(sharedCopy.defaultSignInMode(null), 'interactive', 'with nothing known, sign in in a browser, never the command line');
  assert.equal(sharedCopy.defaultSignInMode({}), 'interactive');
  assert.match(deploy, /defaultSignInMode\(context\?\.publishing\)/,
    'Promote must derive its starting mode from the publish destination the engine reports');
});

test('T??? a failed sign-in is explained in words, with the action that fixes it', () => {
  const azcli = sharedCopy.signInProblem("AzureCliCredential authentication failed: ERROR: Please run 'az login' to setup account.");
  assert.equal(azcli.fix, 'signIn');
  assert.equal(azcli.fixLabel, 'Sign in');
  assert.doesNotMatch(azcli.lead, /az login/i, 'the lead line must not repeat the command');
  assert.match(azcli.lead, /signed in/i);
  const tenant = sharedCopy.signInProblem('AADSTS50020: User account from identity provider does not exist in tenant');
  assert.equal(tenant.fix, 'chooseAccount');
  assert.equal(tenant.fixLabel, 'Choose a different account');
  const consent = sharedCopy.signInProblem('AADSTS65001: The user or administrator has not consented to use the application.');
  assert.equal(consent.fix, 'askAdmin');
  assert.equal(consent.fixLabel, null, 'nobody can press their way out of needing an administrator');
  const role = sharedCopy.signInProblem('Fabric REST 403 InsufficientPrivileges  [Authenticated, but you lack the role: deployment-pipeline reads need an Admin pipeline role (stages) and Contributor on the stage workspace (items). A service principal also needs the tenant\'s setting.]');
  assert.equal(role.fix, 'askAdmin');
  assert.match(role.lead, /not allowed/i);
  assert.equal(role.detail, null, 'a known problem shows the plain line only');
  const unknown = sharedCopy.signInProblem('Fabric REST 500: the service fell over');
  assert.equal(unknown.fix, 'none');
  assert.equal(unknown.detail, 'Fabric REST 500: the service fell over', 'an unknown failure keeps its own text');
  assert.ok(unknown.lead.length > 0, 'and still gets a plain lead line above it');
  for (const p of [azcli, tenant, consent, role, unknown]) {
    assert.doesNotMatch(p.lead, /\u2014/, 'no em dashes in shipped copy');
    assert.doesNotMatch(p.lead, /tenant|credential|authMode|Entra/i, `engine words reached a person: ${p.lead}`);
  }
});

test('T??? Promote renders the plain line, its action, and a quiet line while a sign-in is running', () => {
  // Renamed from PromoteProblem when Fabric Git and CI/CD publish started using the same banner: a
  // component called Promote-anything rendering on two other panels is a lie in the source.
  assert.match(deploy, /<AccountProblem error=\{pipeErr\}/, 'the Promote failure must go through the plain-words component');
  assert.match(deploy, /const problem = signInProblem\(error\);/, 'and that component must use the shared map, not words of its own');
  assert.doesNotMatch(deploy, />\{pipeErr\}</, 'the raw engine text must not be printed straight into the panel again');
  // The banner names its test ids from the panel key now that three panels share it; promote- is still the
  // rendered id, which the acceptance driver reads.
  assert.match(deploy, /<AccountProblem error=\{pipeErr\} testId="promote"/, 'the fix has to be a control, not a sentence to read');
  assert.match(deploy, /Signing you in…/, 'an interactive sign-in has a step the person waits through');
  assert.equal(sharedCopy.isInteractiveSignIn('interactive'), true);
  assert.equal(sharedCopy.isInteractiveSignIn('devicecode'), true);
  assert.equal(sharedCopy.isInteractiveSignIn('azcli'), false, 'the command line never prompts, so it never shows a sign-in step');
  assert.equal(sharedCopy.isInteractiveSignIn('serviceprincipal'), false);
  assert.match(deploy, />Find my pipelines</, 'the button keeps the words Kane already knows');
});

// ---- Fabric Git and CI/CD publish: WHO signs in (Kane, Yoga, 2026-09-15: "same treatment here for login")
// The defect Promote had survived in two more panels of the same page. Both hard-coded the Azure command
// line and printed the raw credential failure, so on Kane's laptop Status, Commit, Update and Publish could
// only ever end in a red line about a command line he has never run, with nothing to press.

test('T??? no panel on the Published page names a sign-in mode for the person', () => {
  const stray = deploy.split('\n')
    .map((line, i) => `${i + 1}: ${line.trim()}`)
    .filter((line) => line.includes("'azcli'"));
  assert.deepEqual(stray, [],
    `deploy.tsx must hold no 'azcli' literal at all; the choices live in copy.ts and the picker:\n${stray.join('\n')}`);
});

test('T??? every Fabric Git call carries the picked account, not a literal', () => {
  for (const call of ['fabricGitConnection', 'fabricGitStatus', 'fabricGitCommit', 'fabricGitUpdate']) {
    const lines = deploy.split('\n').filter((l) => l.includes(`'${call}'`));
    assert.ok(lines.length > 0, `${call} is not called from the panel at all`);
    for (const line of lines) {
      // fgMode is the argument form: the Status read takes the mode as a parameter so Sign in can retry in
      // the same tick it switches, exactly the way Promote's loadPipelines does.
      assert.match(line, /\bfgSignInMode\b|\bfgMode\b/, `${call} must pass the picked mode: ${line.trim()}`);
      assert.ok(line.includes('fgTenantId'), `${call} must pass the place the person named: ${line.trim()}`);
    }
  }
});

test('T??? every CI/CD publish call carries the picked account, and the scaffold writer asks for none', () => {
  const lines = deploy.split('\n').filter((l) => l.includes("'cicdPublish'"));
  assert.equal(lines.length, 2, 'the plan and the confirm are the two publish calls');
  for (const line of lines) {
    assert.match(line, /\bpubSignInMode\b|\bpubMode\b/, `the publish must pass the picked mode: ${line.trim()}`);
    assert.ok(line.includes('pubTenantId'), `the publish must pass the place the person named: ${line.trim()}`);
  }
  // cicd_generate takes NO sign-in mode: EngineRpcTarget.cs cicdGenerate(target, workspaceId, environment,
  // write) makes no Fabric call, it only writes files. It must not grow a fake one for symmetry.
  const gen = deploy.split('\n').find((l) => l.includes("'cicdGenerate'"));
  assert.ok(gen, 'the scaffold generator is still called');
  assert.doesNotMatch(gen, /SignInMode|TenantId/,
    'the scaffold writer makes no Fabric call, so it must not pretend to sign anyone in');
});

test('T??? each panel that signs in shows the shared picker once, with its own remembered choice', () => {
  const pickers = deploy.match(/<AccountPicker/g) || [];
  assert.equal(pickers.length, 3, 'Promote, Fabric Git and CI/CD publish each show the picker exactly once');
  for (const key of ['promote.authMode', 'promote.tenantId', 'fabricgit.authMode', 'fabricgit.tenantId',
    'cicdpublish.authMode', 'cicdpublish.tenantId']) {
    assert.ok(deploy.includes(`'${key}'`), `${key} must be its own remembered choice, not shared with another panel`);
  }
  // All three start from the way the publish destination was really opened, then fall back to a browser.
  assert.equal((deploy.match(/defaultSignInMode\(context\?\.publishing\)/g) || []).length, 3,
    'every panel derives its starting choice from the destination the engine reports');
  // The Fabric Git explanation has to still make sense with a picker sitting above it.
  assert.match(deploy, /signs you in the way you picked above/,
    'the Fabric Git explanation must account for the control now above it');
});

test('T??? a failed Fabric Git or publish sign-in is explained in words, with the fix', () => {
  assert.match(deploy, /<AccountProblem error=\{fgErr\}/, 'the Fabric Git failure goes through the plain-words component');
  assert.match(deploy, /<AccountProblem error=\{pubErr\}/, 'and so does the publish failure');
  assert.doesNotMatch(deploy, /\{fgErr\}<\/div>/, 'the raw engine text must not be printed straight into the Fabric Git panel');
  // The two halves of the CI/CD panel keep separate lines. Generate writes files and can never fail on a
  // sign-in, so its plain line stays where it is; only the publish half goes through the account map.
  const pubPreviewBody = deploy.slice(deploy.indexOf('const pubPreview ='), deploy.indexOf('function pubSignInAndRetry'));
  assert.doesNotMatch(pubPreviewBody, /setCicdErr/, 'a publish failure must not land in the scaffold line');
  const genBody = deploy.slice(deploy.indexOf('const cicdGenerate ='), deploy.indexOf('const pubPreview ='));
  assert.doesNotMatch(genBody, /setPubErr/, 'a scaffold failure must not land in the publish line');
  // The banner names its own test ids from the panel key, so a driver can find each panel's fix separately.
  assert.match(deploy, /data-testid=\{`\$\{testId\}-signin-fix`\}/, 'the fix has to be a control, not a sentence');
  for (const id of ['promote', 'fabricgit', 'cicd']) {
    assert.ok(deploy.includes(`testId="${id}"`), `the ${id} panel must name itself to the banner`);
  }
  // The failure Kane read under the workspace id box, mapped.
  const status = sharedCopy.signInProblem("AzureCliCredential authentication failed: ERROR: Please run 'az login' to set up account.");
  assert.equal(status.fix, 'signIn');
  assert.equal(status.fixLabel, 'Sign in');
  assert.equal(status.detail, null, 'a known problem shows the plain line only');
  // One map now serves three panels, so a line that named pipelines would be wrong on two of them.
  const role = sharedCopy.signInProblem('Fabric REST 403 InsufficientPrivileges [Authenticated, but you lack the role]');
  assert.equal(role.fix, 'askAdmin');
  assert.doesNotMatch(role.lead, /pipeline/i,
    'the shared 403 line is read on Fabric Git and on publish too, so it cannot claim to be about pipelines');
  assert.match(role.lead, /not allowed/i);
});

test('T??? Sign in repeats a read, and never runs a live write for the person', () => {
  // Just the function's own body, so a neighbouring declaration cannot make this pass or fail by accident.
  const bodyOf = (decl) => {
    const start = deploy.indexOf(decl);
    assert.ok(start >= 0, `${decl} must exist`);
    const end = deploy.indexOf('\n  }', start);
    return deploy.slice(start, end);
  };
  const fgRetry = bodyOf('function fgSignInAndRetry');
  assert.match(fgRetry, /fgReadStatus\('interactive'\)/, 'signing in re-runs the Status read');
  assert.doesNotMatch(fgRetry, /fgConfirm|fgPreview/, 'signing in must never commit or update a workspace');
  const pubRetry = bodyOf('function pubSignInAndRetry');
  assert.match(pubRetry, /pubPreview\('interactive'\)/, 'signing in re-runs the dry-run plan, which writes nothing');
  assert.doesNotMatch(pubRetry, /pubConfirm/, 'signing in must never publish over a model');
  // A write that failed on the sign-in keeps its Confirm step, so the person can press it again after
  // signing in rather than starting the two-step over.
  const fgConfirmBody = deploy.slice(deploy.indexOf('const fgConfirm ='), deploy.indexOf('const cicdGenerate ='));
  assert.ok(fgConfirmBody.indexOf('if (r.error) { setFgErr(r.error); return; }') >= 0,
    'a failed Fabric Git write must leave the pending Confirm in place');
  const pubConfirmBody = deploy.slice(deploy.indexOf('const pubConfirm ='), deploy.indexOf('const canDeploy ='));
  assert.ok(pubConfirmBody.indexOf('if (r.error) { setPubErr(r.error); return; }') >= 0,
    'a failed publish must leave the pending Confirm in place');
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
