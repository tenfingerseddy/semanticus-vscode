// Publish surface: one word, one button, Ctrl+S never publishes, the label is the guard, the destination is named once,
// the Changes tab resets on model switch, the account is named, and the Publish page has no second step.
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
const changes = read('webview/src/publish-changes.tsx');
const sharedPublishCopy = read('webview/src/publishcopy.ts');
const app = read('webview/src/App.tsx');
let reach = '';
try { reach = read('webview/src/publishReach.ts'); } catch { /* the O5 test says what is missing */ }
const extension = read('src/extension.ts');
const propgrid = read('media/propgrid/propgrid.js');
const help = read('webview/src/help.tsx');
const dataagent = read('webview/src/dataagent.tsx');
const picker = read('webview/src/accountpicker.tsx');
const sharedCopy = await loadTypeScriptModule(resolve(root, 'webview/src/copy.ts'));

let passed = 0;
const test = (name, fn) => { fn(); passed++; };

test('H1 the page publishes through one operation, and the destination travels with every call', () => {
  // The host relays the engine's set_publish_destination activity, so Studio re-reads without opening Connections.
  assert.match(extension, /kind === 'set_publish_destination'/, 'the host must relay set_publish_destination as a connectionChanged nudge');
  // The page rehearses once and writes once, both through applyDiff with the destination it compared. No deployLive.
  const calls = changes.match(/rpc<ApplyDiffResult>\('applyDiff'[^\n]*/g) ?? [];
  assert.equal(calls.length, 2, 'the Publish page makes one rehearsal call and one write call');
  for (const c of calls) assert.match(c, /left, right,/, 'every applyDiff call names the two sides it compared: ' + c);
  assert.match(calls[0], /, false, 'human'\)/, 'the rehearsal writes nothing');
  assert.match(calls[1], /\[\.\.\.effective\], true, 'human'\)/, 'the write sends the ticked rows, from the person, with no token and no reason');
  assert.doesNotMatch(deploy + changes, /deployLive/, 'the page has no second publish operation');
});

test('J10-3 the extension host sends no deployLive of its own: a null target would publish to a destination the person was not shown', () => {
  assert.doesNotMatch(extension, /sendRequest<[^>]*>\('deployLive'/, 'no host code may call deployLive; the Publish page is the one door and it carries the shown destination');
  assert.doesNotMatch(extension, /overrideDeployToLive/, 'the unused override helper is gone');
});

test('B2 the Data agent page signs in only when the person asks', () => {
  // Picking "Sign in in a browser" (or a device code) used to list workspaces at once, which opens the sign-in window.
  assert.equal(sharedCopy.mayStartSignIn('interactive', '', 'interactive|'), false);
  assert.equal(sharedCopy.mayStartSignIn('devicecode', '', 'devicecode|'), false);
  assert.equal(sharedCopy.mayStartSignIn('interactive', 'interactive|', 'interactive|'), true, 'pressed for this choice');
  assert.equal(sharedCopy.mayStartSignIn('interactive', 'interactive|', 'interactive|tenant-b'), false, 'a new tenant is a new sign-in');
  assert.equal(sharedCopy.mayStartSignIn('azcli', '', 'azcli|'), true, 'the command line never opens a window');
  assert.equal(sharedCopy.mayStartSignIn('serviceprincipal', '', 'serviceprincipal|'), true);
  const effect = dataagent.slice(dataagent.indexOf("rpcPressed<FabricWorkspace[]>('listWorkspaces'") - 400, dataagent.indexOf("rpcPressed<FabricWorkspace[]>('listWorkspaces'"));
  assert.match(effect, /mayStartSignIn\(/, 'the workspace listing must wait for the person when the sign-in opens a window');
  assert.match(dataagent, /data-testid="dataagent-sign-in"/, 'the page must offer a Sign in button for those modes');
});

test('D-007 palette and tree use the word Publish', () => {
  const cmd = pkg.contributes.commands.find((c) => c.command === 'semanticus.saveToLive');
  assert.equal(cmd.title, copy.PUBLISH_COMMAND_TITLE);
  assert.match(cmd.title, /Publish/);
  assert.doesNotMatch(cmd.title, /Save to Live|deploy metadata/i);
});

test('D-007 the header Publish opens the Publish page and is hidden on it', () => {
  // One Publish button in sight: the header one on every other page, the page's own on the Publish page.
  assert.match(app, />Publish</);
  assert.match(app, /data-testid="publish-button"/);
  assert.match(app, /tab !== 'deploy' && tab !== 'compare' && tab !== 'dataagent' && <div className="studio-publish-block">/);
  assert.match(app, /const openPublish = \(\) => goTab\('deploy'\)/);
  assert.doesNotMatch(app, />Publish…</, 'the header button no longer opens a review, so it carries no ellipsis');
  assert.doesNotMatch(deploy, /data-testid="publish-button"/);
  assert.doesNotMatch(deploy, /PublishConfirm|publish-confirm|publishOpen|confirmPublish|publishNeedReason|publishOverride/, 'the review card and its reason box are gone');
  assert.doesNotMatch(deploy, />Advanced</, 'the Advanced tab is gone');
  assert.match(deploy, /Changes/);
  assert.doesNotMatch(help, /Choose what to publish/);
});

test('D-007 chip, palette, and tree all open the Publish page', () => {
  assert.match(extension, /navigateStudio\(extCtx, 'deploy', 'publish'\)/);
  assert.doesNotMatch(app, /tool === 'deploy' && target === 'publish'/, 'the publish route has no seed: the page itself is the review');
  assert.match(extension, /publishStatus\.command = 'semanticus\.saveToLive'/);
  assert.match(extension, /\$\(cloud-upload\)/);
});

test('D-007 the Publish page has one write button, named by its label', () => {
  assert.match(changes, /data-testid="publish-go"/);
  assert.match(changes, /publishLabel\(\{/);
  assert.match(deploy, /data-testid="restore-go"/);
  assert.match(deploy, /data-testid="promote-go"/);
  assert.doesNotMatch(deploy + changes, /Publish anyway|Why publish anyway|Validate selection|Confirm rollback|Preview rollback/);
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

test('the label is the guard: it names the count and the place, and says so when something is removed', () => {
  const L = (o) => copy.publishLabel({ target: 'Contoso Sales', ...o });
  assert.equal(L({ changes: 5, removals: 0 }), 'Publish 5 changes to Contoso Sales');
  assert.equal(L({ changes: 1, removals: 0 }), 'Publish 1 change to Contoso Sales');
  assert.equal(L({ changes: 5, removals: 1 }), 'Publish 5 changes and remove 1 from Contoso Sales');
  assert.equal(L({ changes: 0, removals: 2 }), 'Remove 2 from Contoso Sales');
  assert.equal(L({ changes: 0, removals: 0 }), 'Nothing ticked');
  assert.equal(L({ changes: 3, removals: 0, busy: true }), 'Publishing...');
  assert.equal(L({ changes: 0, removals: 0, empty: true }), 'Nothing to publish');
  assert.doesNotMatch(L({ changes: 0, removals: 0 }), /^Publish/, 'no label invites a write of nothing');
});

test('ticks change the count: removals count apart, and a row that needs an unticked table is left out', () => {
  const table = { ref: 'table:Returns', objectType: 'Table', name: 'Returns', table: null, action: 'Create' };
  const rate = { ref: 'measure:Returns/Return Rate', objectType: 'Measure', name: 'Return Rate', table: 'Returns', action: 'Create' };
  const margin = { ref: 'measure:Sales/Margin %', objectType: 'Measure', name: 'Margin %', table: 'Sales', action: 'Update' };
  const legacy = { ref: 'measure:Sales/Legacy', objectType: 'Measure', name: 'Legacy', table: 'Sales', action: 'Delete' };
  const rel = { ref: 'relationship:Returns->Date', objectType: 'Relationship', name: 'Returns to Date', table: null, action: 'Create', leftText: JSON.stringify({ fromTable: 'Returns', toTable: 'Date' }) };
  const rows = [table, rate, margin, legacy, rel];
  // the parent-needed rule
  assert.deepEqual(copy.neededParent(rate, rows, new Set([rate.ref, margin.ref])), { name: 'Returns', ref: table.ref }, 'a child of a new table that is not ticked needs it');
  assert.equal(copy.neededParent(rate, rows, new Set([rate.ref, table.ref])), null, 'ticking the table clears it');
  assert.deepEqual(copy.neededParent(rel, rows, new Set([rel.ref]))?.name, 'Returns', 'a relationship that points at a new table needs it too');
  assert.equal(copy.neededParent(margin, rows, new Set()), null, 'a change to an existing table needs nothing');
  assert.equal(copy.neededParent(legacy, rows, new Set()), null, 'a removal needs nothing');
  // the counts
  assert.deepEqual(copy.publishCounts(rows, new Set([margin.ref, table.ref])), { changes: 2, removals: 0 });
  assert.deepEqual(copy.publishCounts(rows, new Set([margin.ref, legacy.ref])), { changes: 1, removals: 1 });
  // the row notes
  assert.equal(copy.rowNote(table, 'Contoso Sales'), 'new table');
  assert.equal(copy.rowNote({ ...margin, leftText: '{"expression":"a","name":"x"}', rightText: '{"expression":"b","name":"x"}' }, 'X'), 'formula changed');
  assert.equal(copy.rowNote({ ...margin, leftText: '{"formatString":"a"}', rightText: '{"formatString":"b"}' }, 'X'), 'measure changed');
  assert.match(copy.rowNote(legacy, 'Contoso Sales'), /^only on Contoso Sales\. Tick to remove it there$/);
  // the engine's failed list, in plain words, matched by ref and never taken apart
  const reasons = copy.failureReasons([table.ref, `${margin.ref} (merged locally but the live deploy did not carry it: a metadata deploy doesn't push this object type)`], rows);
  assert.match(reasons.get(table.ref), /different object with this name/);
  assert.match(reasons.get(margin.ref), /does not take this kind of change/);
  // Kane, 13 Oct: no engine words reach the person, and the compatibility level is one plain row
  const engineWords = copy.failureReasons([`table:Date (table 'Date' carries authored shell metadata this apply path cannot copy yet; deploy it through TMDL/XMLA instead)`], [{ ref: 'table:Date', objectType: 'Table', name: 'Date', action: 'Update' }]).get('table:Date');
  assert.doesNotMatch(engineWords, /TMDL|XMLA|shell|metadata|apply path/);
  assert.equal(engineWords, 'This change stays in your copy for now. Semanticus cannot send it to Power BI yet.');
  const level = { ref: 'compatibility', objectType: 'CompatibilityLevel', name: 'Compatibility level', table: null, action: 'Update', leftText: '1701', rightText: '1600' };
  assert.equal(copy.rowNote(level, 'Contoso'), 'raise it from 1600 to 1701. Power BI cannot lower it again');
  assert.deepEqual(copy.groupRows([margin, level]).map((g) => g.label), ['Sales', 'Model'], 'the level sits in the Model group, after the tables');
  // the line that names what stayed in the person's copy: up to two names, then a count, one plain sentence
  assert.equal(copy.couldNotGoLine(new Map([[margin.ref, 'x']]), rows), '1 change could not go (Margin %).');
  assert.equal(copy.couldNotGoLine(reasons, rows), '2 changes could not go (Returns, Margin %).');
  assert.equal(copy.couldNotGoLine(new Map([[table.ref, 'x'], [rate.ref, 'x'], [margin.ref, 'x']]), rows), '3 changes could not go (Returns, Return Rate and 1 more).');
  assert.match(changes, /couldNotGoLine\(failed, items\)/, 'the page says it after a publish that left something behind');
  // the page wires it
  assert.match(changes, /neededParent\(it, items, wish\)/);
  assert.match(changes, /failureReasons\(dry\.failedRefs, list\)/);
  assert.match(changes, /Needs the \{parent\.name\} table, which is not ticked\./);
  assert.match(changes, /Tick \{parent\.name\} too/);
});

test('D-008 the Changes tab remounts when the session changes', () => {
  assert.match(deploy, /<PublishChanges key=\{session\?\.sessionId \?\? 'none'\}/);
});

test('D-012 the header names the destination and the account once, and the stage only when one was set', () => {
  assert.match(deploy, /<span>To <b>\{targetName\}<\/b><\/span>/);
  assert.match(deploy, /\{stage && <span className="sem-chip sem-chip-outline">\{stage\}<\/span>\}/);
  assert.match(deploy, /\{account && <span className="sem-text-meta">as \{account\}<\/span>\}/);
  assert.doesNotMatch(deploy, /treated as production/, 'no label means no badge, not a guess');
  assert.doesNotMatch(deploy, /Publishing writes your reviewed changes|It never publishes\./, 'the caveat sentences are gone');
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

test('the one button says what it will do in the words of the person', () => {
  assert.doesNotMatch(copy.nothingToPublishCopy('V2'), /Nothing to publish/, 'the button says it; the line says why');
  assert.match(copy.nothingToPublishCopy('V2'), /V2 already has everything in your copy/);
  assert.match(copy.unreachableCopy('V2'), /^Could not reach V2\./);
  assert.equal(copy.publishedLine(5, 'Contoso Sales'), 'Published 5 changes to Contoso Sales. Your copy did not change.');
  assert.equal(copy.publishedLine(1, 'Contoso Sales'), 'Published 1 change to Contoso Sales. Your copy did not change.');
  assert.equal(copy.undoneLine('Contoso Sales', '09:42'), 'Contoso Sales is back to how it was before 09:42.');
  assert.equal(copy.restoreLabel('Contoso Sales', '2 Oct 14:15'), 'Restore Contoso Sales to 2 Oct 14:15');
  assert.equal(copy.promoteLabel(3, 'Test', 'Production'), 'Promote 3 items from Test to Production');
  assert.equal(copy.shortWhen(new Date(2026, 9, 2, 14, 15)), '2 Oct 14:15', 'a version is named by its day and time on the person\'s own clock');
  for (const line of [copy.publishedLine(2, 'X'), copy.undoneLine('X', '09:42'), copy.restoreDetail(3, 1, 'X')]) assert.doesNotMatch(line, /\u2014/, 'no em dashes in shipped copy');
});

test('help and compare use the word Publish for the live write', () => {
  assert.doesNotMatch(help, /Save to Live Model/);
  assert.match(help, /Publish/);
});

test('D-180 the button never invites a no-op write', () => {
  assert.equal(copy.publishLabel({ changes: 0, removals: 0, target: 'V2' }), 'Nothing ticked');
  assert.match(changes, /disabled=\{!canPress\}/, 'nothing ticked leaves the button disabled');
  assert.match(changes, /const canPress = !!diff && !busy && effective\.size > 0/);
});

test('D-181 the page says it is still checking, not nothing to publish', () => {
  assert.match(copy.checkingCopy('Contoso'), /Checking Contoso/);
  assert.doesNotMatch(copy.checkingCopy('Contoso'), /Nothing to publish/);
  assert.equal(copy.publishLabel({ changes: 0, removals: 0, target: 'Contoso', checking: true }), 'Checking...');
  assert.match(changes, /const checking = loading && !diff;/);
});

test('D-179 a successful publish and an undo both reload the restore points', () => {
  assert.match(changes, /onWrote\?\.\(\);/);
  assert.match(deploy, /onWrote=\{\(\) => void loadRestorePoints\(\)\}/);
});

test('D-178/D-226 publish is gated on the resolved destination, not liveBound alone', () => {
  assert.match(deploy, /const canPublish = liveBound \|\| !!/);
  assert.match(deploy, /context\?\.publishing\?\.available/);
  assert.match(deploy, /const hasDestination = canPublish && reviewSeed\?\.right\?\.kind === 'workspace'/);
});

test('UX12 with no destination the page offers a new model and Choose destination, and no list', () => {
  assert.match(changes, /if \(!right \|\| !left\) return <NewModel /);
  assert.match(changes, /data-testid="publish-choose" onClick=\{onChooseDestination\}>Choose destination</);
  const newModel = changes.slice(changes.indexOf('function NewModel('));
  assert.doesNotMatch(newModel, /publish-go"|publish-list/, 'the no-destination view carries no change list');
});

test('UX12 the header names a missing destination instead of claiming no live model', () => {
  assert.match(deploy, /No publish destination yet\./);
  assert.doesNotMatch(deploy, /not connected to a live model/);
});

test('UX12 an unreachable destination shows one line and Sign in, with no list and no button', () => {
  assert.match(changes, /data-testid="publish-unreachable"/);
  assert.match(changes, /unreachableCopy\(destination\)/);
  assert.match(changes, /data-testid="publish-signin"/);
  assert.match(changes, /\{diff && <PageAction slot=\{slot\}>/, 'the write button exists only once a list was read');
  assert.match(changes, /\{diff && !empty && \(\s*<section/, 'and so does the list');
});

test('UX12 rollback wording follows applied and failedRefs, never error absence', () => {
  assert.match(copy.rollbackResultLine({ applied: false, note: 'Contoso Sales already matches this saved version.' }, 'Contoso Sales', '2 Oct 14:15'), /already matches/);
  assert.doesNotMatch(copy.rollbackResultLine({ applied: false }, 'Contoso Sales', '2 Oct 14:15'), /^Restored/);
  assert.match(copy.rollbackResultLine({ applied: true, failedRefs: ['measure:Sales/Margin %'] }, 'Contoso Sales', '2 Oct 14:15'), /except 1 object that could not be restored: measure:Sales\/Margin %/);
  assert.match(copy.rollbackResultLine({ applied: true, failedRefs: [] }, 'Contoso Sales', '2 Oct 14:15'), /^Restored Contoso Sales to the version from 2 Oct 14:15\. Your copy did not change\./);
  assert.equal(copy.rollbackResultLine({ applied: true, error: 'target gone' }, 'X', 'now'), 'target gone');
  assert.match(deploy, /rollbackResultLine\(r, target, picked\)/);
});

test('Earlier versions: picking a version runs the preview by itself, and one button restores it', () => {
  assert.match(deploy, /rpc<RollbackResult>\('rollbackPush', selectedId, false, null, 'human'\)/, 'the preview runs when a version is picked');
  assert.match(deploy, /rpc<RollbackResult>\('rollbackPush', selectedId, true, null, 'human'\)/);
  assert.match(deploy, /\}, \[selectedId, nonce\]\);/, 'the preview is keyed on the picked version');
  assert.doesNotMatch(deploy, /Preview rollback|Confirm rollback|Will remove|deleted from the live target/, 'no Preview step, no Confirm step, no red warning line');
  assert.equal(copy.restoreDetail(0, 0, 'Contoso Sales'), 'Contoso Sales already matches this version.');
  assert.equal(copy.restoreDetail(3, 1, 'Contoso Sales'), 'This puts back 3 objects and removes 1 object that came after this version.');
});

test('Promote: picking both stages runs the check by itself, and one button sends the token unseen', () => {
  assert.match(deploy, /\}, \[selPipe, srcSel, tgtSel\]\);/, 'the check runs when the stages are picked');
  assert.match(deploy, /rpc<DeployStageReport>\('deployStage', selPipe, srcSel, tgtSel, null, null, false, null, false, signInMode, promoteTenantId, 'human'\)/);
  // r1 publish-p2: the stage move has no readiness gate, so no override reason is sent and the page never reads report.gate.
  assert.doesNotMatch(deploy, /'human', null\)|report\.gate/, 'no overrideReason on deployStage, and no gate read');
  assert.match(deploy, /true, report\.confirmToken \?\? null, false, signInMode/, 'the token from the check goes out with the button');
  assert.doesNotMatch(deploy, /promote anyway|override the failing check|reason \(required|will be sent with Promote|confirm token/i, 'no tick box, no reason field, no token line');
  assert.match(deploy, /Copy a model from one stage to the next\./);
  assert.doesNotMatch(deploy, /A pipeline is a set of stages|Promotion <i>rules<\/i>/, 'the explainer paragraphs are gone');
});

test('UX12 a refused write compares again by itself and shows the engine\'s plain sentence, never a reason box', () => {
  assert.match(changes, /if \(r\.error\) setLine\(\{ tone: 'bad', text: plainPublishError\(r\.error\) \}\)/, 'the engine\'s sentence goes through the plain-words filter');
  assert.match(changes, /await read\(\);   \/\/ a refused write usually means the destination moved/, 'the list is read again after every press');
  assert.doesNotMatch(changes, /overrideReason|Why publish/, 'no reason is ever typed');
  assert.match(changes, /data-testid="publish-undo"/);
  assert.match(changes, /rpc<RollbackWire>\('rollbackPush', point\.id, true, null, 'human'\)/, 'Undo publish restores the point the publish just saved, in one call');
});

test('UX12 publish and restore results say the local copy is untouched', () => {
  assert.match(sharedPublishCopy, /Your copy did not change\./);
  assert.doesNotMatch(deploy, /never changes your local files or your history/);
});

// Verifier r2 O5 (lane m11/create-model-ui): the "cannot reach" flag is module state, read by the header on every
// page, and only the Published page reset it, so a model swapped in while another page was open kept the old
// model's refusal: Publish greyed out and "Cannot reach" over a model nobody had tried. The flag belongs to the model
// session and the destination it was read for.
test('O5 a model swap never carries the old model\'s "cannot reach" onto the new one', () => {
  const out = ts.transpileModule(reach, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', out)((name) => (name === 'react' ? { useEffect() {}, useState: (v) => [v, () => {}] } : {}), module, module.exports);
  const { reachFor } = module.exports;
  const refused = { sessionId: 's1', connectionId: 'c1' };
  assert.deepEqual(reachFor(refused, 's1', 'c1'), { unreachable: true }, 'the model and destination that refused show it');
  assert.equal(reachFor(refused, 's2', 'c1'), null, 'a different model opened since has not been tried');
  assert.equal(reachFor(refused, 's1', 'c2'), null, 'nor has a different destination');
  assert.equal(reachFor(refused, undefined, 'c1'), null, 'no model open, nothing refused');
  assert.deepEqual(reachFor({ sessionId: 's1', connectionId: null }, 's1', undefined), { unreachable: true }, 'no destination id reads as the same no destination id');
  assert.equal(reachFor(null, 's1', 'c1'), null, 'nothing tried, nothing refused');
  assert.match(deploy, /setPublishReach\(ok \? null : \{ sessionId: session\?\.sessionId \?\? '', connectionId: context\?\.publishing\?\.connectionId \?\? null \}\)/,
    'the Published page records which model and destination refused');
});

// M17: a publish destination that cannot be read must not be offered as a publish you can press, and the
// page must say it never reached the destination instead of showing a list that looks complete.
test('M17 an unreachable destination disables the header Publish and says the page could not read it', () => {
  assert.match(changes, /onReach\?\.\(false\)/, 'a failed read reports the destination as unread');
  assert.match(changes, /onReach\?\.\(true\)/, 'a successful read clears the unread state');
  assert.match(changes, /Could not reach|unreachableCopy/, 'the page says plainly that it never read the destination');
  assert.match(reach, /export function usePublishReach\(/, 'the header needs the destination reachability');
  assert.match(deploy, /onReach=\{/, 'the Publish page must report whether it reached its destination');
  assert.match(app, /usePublishReach\(shellSession\?\.sessionId, context\?\.publishing\?\.connectionId\)/, 'the header Publish must read the reachability of the model and destination it shows');
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
  assert.match(deploy, /rpcPressed<DeploymentPipeline\[\]>\('listDeploymentPipelines', mode/, 'the pipeline read takes the picked mode');
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

test('T??? no panel on the Published page names a sign-in mode for the person', () => {
  const stray = deploy.split('\n')
    .map((line, i) => `${i + 1}: ${line.trim()}`)
    .filter((line) => line.includes("'azcli'"));
  assert.deepEqual(stray, [],
    `deploy.tsx must hold no 'azcli' literal at all; the choices live in copy.ts and the picker:\n${stray.join('\n')}`);
});

test('T??? the Promote panel shows the shared picker once, with its own remembered choice', () => {
  const pickers = deploy.match(/<AccountPicker/g) || [];
  assert.equal(pickers.length, 1, 'Promote is the one panel on the page that signs in, so it shows the picker exactly once');
  for (const key of ['promote.authMode', 'promote.tenantId']) {
    assert.ok(deploy.includes(`'${key}'`), `${key} must be its own remembered choice`);
  }
  assert.equal((deploy.match(/defaultSignInMode\(context\?\.publishing\)/g) || []).length, 1,
    'the starting choice comes from the destination the engine reports');
});

test('T??? the hidden delivery screens are gone from the page, and the assistant still reaches them', () => {
  for (const gone of ['fabricGitConnection', 'fabricGitStatus', 'fabricGitCommit', 'fabricGitUpdate', 'cicdPublish', 'cicdGenerate', 'gitStatus', 'gitCommit', 'gitPush', 'gitPull', 'deployGate', 'DataAgentView']) {
    assert.ok(!deploy.includes(gone), `${gone} must not be reachable from the Publish page`);
  }
  assert.doesNotMatch(app, /DataAgentView/, 'the Data Agent screen is not mounted');
  assert.match(app, /tab === 'deploy' \|\| tab === 'dataagent' \?/, 'the legacy dataagent route lands on Changes');
});

test('T??? Sign in repeats a read, and never runs a live write for the person', () => {
  const start = deploy.indexOf('function signInAndRetry');
  assert.ok(start >= 0, 'signInAndRetry must exist');
  const body = deploy.slice(start, deploy.indexOf('\n', start));
  assert.match(body, /loadPipelines\('interactive'\)/, 'signing in re-runs the pipeline read');
  assert.doesNotMatch(body, /promote\(|deployStage/, 'signing in must never promote');
  const changeStart = changes.indexOf('async function signInAndRetry');
  const changeBody = changes.slice(changeStart, changes.indexOf('\n  }', changeStart));
  assert.match(changeBody, /void read\(\)/, 'signing in on the Changes tab only reads again');
  assert.doesNotMatch(changeBody, /publish\(|applyDiff/, 'signing in must never publish');
});

test('J3 a change that differs only by an annotation the publish never carries is not a publishable change', () => {
  const row = (action, l, r, extra = {}) => ({ ref: 'measure:Sales/Margin', objectType: 'Measure', name: 'Margin', table: 'Sales', action,
    leftText: JSON.stringify(l), rightText: r === null ? null : JSON.stringify(r), ...extra });
  const base = { name: 'Margin', expression: '1' };
  const mark = { annotations: [{ name: 'Semanticus_AddedOnPurpose', value: 'the measure for X' }] };
  assert.equal(copy.isUnpublishable(row('Update', { ...base, ...mark }, base)), true, 'a foreign annotation alone');
  assert.equal(copy.isUnpublishable(row('Update', { ...base, annotations: [{ name: 'Semanticus_ConsentId', value: 'x' }] }, base)), true, 'the permission marker on the model');
  assert.equal(copy.isUnpublishable(row('Update', { ...base, annotations: [{ name: 'Semanticus_Waivers', value: '[]' }] }, base)), false, 'an owned annotation does go');
  assert.equal(copy.isUnpublishable(row('Update', { ...base, expression: '2', ...mark }, base)), false, 'a real change next to the annotation');
  assert.equal(copy.isUnpublishable(row('Create', { ...base, ...mark }, null)), false, 'a new object');
  const rows = [row('Update', { ...base, ...mark }, base)];
  assert.deepEqual(copy.publishCounts(copy.publishableRows(rows), new Set(rows.map((r) => r.ref))), { changes: 0, removals: 0 });
});

test('hero21 N8: right after a create no lone Model row that cannot go, and a new date table is named with its calendar and hierarchy', () => {
  const model = (action, l, r) => ({ ref: 'model', objectType: 'Model', name: 'Hero', table: null, action, leftText: JSON.stringify(l), rightText: JSON.stringify(r) });
  assert.equal(copy.isUnpublishable(model('Update', { name: 'Hero', defaultPowerBIDataSourceVersion: 'powerBI_V3' }, { name: 'Hero' })), true, 'the default data source version alone never goes from here');
  for (const [what, l, r] of [['a description', { name: 'Hero', description: 'Sales' }, { name: 'Hero' }], ['a culture', { name: 'Hero', culture: 'en-US' }, { name: 'Hero', culture: 'en-AU' }], ['implicit measures', { name: 'Hero', discourageImplicitMeasures: true }, { name: 'Hero' }]]) {
    assert.equal(copy.isUnpublishable(model('Update', { ...l, defaultPowerBIDataSourceVersion: 'powerBI_V3' }, r)), false, what + ' changed on the model stays listed');
  }
  assert.equal(copy.isUnpublishable(model('Update', { name: 'Hero', annotations: [{ name: 'Semanticus_Waivers', value: '[]' }] }, { name: 'Hero' })), false, 'a carried annotation does go');
  const table = (action, l, r) => ({ ref: 'table:Date', objectType: 'Table', name: 'Date', table: null, action, leftText: JSON.stringify(l), rightText: r ? JSON.stringify(r) : null });
  assert.equal(copy.rowNote(table('Create', { name: 'Date', calendars: [{ name: 'Gregorian' }], hierarchies: [{ name: 'Calendar' }] }, null), 'Hero'), 'new table, with a calendar and a hierarchy');
  assert.equal(copy.rowNote(table('Create', { name: 'Date', hierarchies: [{ name: 'Calendar' }] }, null), 'Hero'), 'new table, with a hierarchy');
  assert.equal(copy.rowNote(table('Create', { name: 'Date' }, null), 'Hero'), 'new table');
  assert.equal(copy.rowNote(table('Update', { name: 'Date', calendars: [{ name: 'Gregorian' }] }, { name: 'Date' }), 'Hero'), 'calendar changed');
});

test('role-diff: a role member is shown in words, and a role change that cannot go is named before the press', () => {
  const guid = '22222222-2222-2222-2222-222222222222';
  const group = { memberName: `obj:${guid}@contoso`, identityProvider: 'AzureAD', memberType: 'group' };
  const person = { memberName: 'ana@contoso.com', identityProvider: 'AzureAD', memberType: 'user' };
  const role = (members, extra = {}) => JSON.stringify({ name: 'Readers', modelPermission: 'read', members, ...extra });
  const row = (action, l, r) => ({ ref: 'role:Readers', objectType: 'Role', name: 'Readers', table: null, action, leftText: l, rightText: r });

  // Members in words: a name when there is one, else the kind of Microsoft Entra object. Never a guid, never raw JSON.
  assert.equal(copy.memberWords(group), 'a Microsoft Entra group');
  assert.equal(copy.memberWords(person), 'ana@contoso.com');
  assert.equal(copy.memberWords({ ...group, memberType: 'user' }), 'a Microsoft Entra user');
  const lines = copy.roleMemberLines(role([person, group]), role([group]));
  assert.deepEqual(lines, { added: ['ana@contoso.com'], removed: [] });
  assert.deepEqual(copy.roleMemberLines(role([group]), role([group, person])), { added: [], removed: ['ana@contoso.com'] });
  assert.deepEqual(copy.roleMemberLines(role([group, group]), role([group])), { added: ['a Microsoft Entra group'], removed: [] }, 'a second unnamed group still counts');
  assert.deepEqual(copy.roleMemberLines(role([{ ...group, memberId: guid }]), role([group])), { added: [], removed: [] }, 'the server memberId is never a change');
  assert.equal(copy.roleMemberLines('{"name":"x"}', '{"name":"x"}'), null, 'a row that is not a role has no member lines');
  assert.deepEqual(copy.roleMemberLines(role([person]), null), { added: ['ana@contoso.com'], removed: [] }, 'a new role adds all its members');
  assert.deepEqual(copy.roleMemberLines(null, role([person])), { added: [], removed: ['ana@contoso.com'] }, 'a role only on the destination removes all of them');
  for (const text of [...lines.added, ...lines.removed]) assert.doesNotMatch(text, /[{}\[\]"]|obj:|[0-9a-f]{8}-[0-9a-f]{4}/, 'words only');

  // The row says what it is, and the engine's reason reads as a plain line. Neither mentions JSON.
  assert.equal(copy.rowNote(row('Update', role([person, group]), role([group])), 'Contoso Sales'), 'role members changed');
  assert.equal(copy.rowNote(row('Create', role([group]), null), 'Contoso Sales'), 'new role');
  assert.equal(copy.rowNote(row('Delete', null, role([group])), 'Contoso Sales'), 'only on Contoso Sales. Tick to remove it there', 'a role removal still goes');
  assert.equal(copy.heldReason, undefined, 'no role row is held back any more: a role goes like any other change');
});

test('newmodel: with no destination, the Publish page publishes the model as a new model in a workspace', () => {
  assert.deepEqual(copy.newModelSignIn([null, { authMode: '' }, { authMode: 'serviceprincipal', tenantId: 't1' }]), { authMode: 'serviceprincipal', tenantId: 't1' });
  assert.deepEqual(copy.newModelSignIn([]), { authMode: 'interactive', tenantId: null }, 'a person with no saved sign-in signs in in a browser');
  assert.equal(copy.newModelLabel({ busy: false, workspace: 'Semanticus Demo' }), 'Publish to Semanticus Demo');
  assert.equal(copy.newModelLabel({ busy: true, workspace: 'Semanticus Demo' }), 'Publishing...');
  assert.equal(copy.newModelLabel({ busy: false, workspace: null }), 'Publish');
  // The card offers a free name when the workspace already holds the model's own name; it never offers one that replaces something.
  assert.equal(copy.freeNewModelName('Gold', ['Contoso Sales', 'Gold']), 'Gold 2');
  assert.equal(copy.freeNewModelName('gold', ['Gold', 'Gold 2']), 'gold 3', 'the next free number, whatever the case');
  assert.equal(copy.freeNewModelName('Gold', ['Contoso Sales']), 'Gold', 'a free name is left alone');
  assert.match(changes, /data-testid="publish-new-model"/, 'the no-destination state offers the new model card');
  assert.match(changes, /rpc<NewModelResult>\('publishNewModel'/, 'one press calls the engine once');
  assert.match(changes, /tone: r\.loaded \|\| r\.signInNeeded \? 'good' : 'bad', text: r\.note/, 'an import model that needs its source sign-in shows the engine line as a next step, not an error');
  assert.match(read('webview/src/bridge.ts'), /const LONG_RPC = [^\n]*\|publishNewModel\)/, 'creating and loading a model takes longer than the default wait');
  // Tests and queries attach to the new model in the engine, so the assistant's publish_new_model attaches them too.
  assert.doesNotMatch(changes, /connectXmla\(r\.endpoint/, 'the page does not attach Tests itself');
  assert.match(read('../Semanticus.Engine/LocalEngine.NewModel.cs'), /if \(outcome\.Loaded\)\s+try \{ await ConnectXmlaAsync\(endpoint, name, authMode/, 'the engine attaches Tests to a loaded new model, for both doors');
});

// hero17 P9: an import model has no data until its source sign-in is set, so the new-model card must not promise one.
test('newmodel: the card does not promise that the data loads', () => {
  assert.doesNotMatch(changes, /Publishes the whole model and loads its data/, 'the card says what the press does, not what the data does');
  assert.match(changes, /Publishes the whole model\. Later publishes update it\./);
});

// hero17 P7: the assistant's compare publish gets the same Undo publish as the person's press, and Earlier versions reads the new restore point.
test('publish: the assistant\'s compare publish offers Undo publish and refreshes Earlier versions', () => {
  const done = { kind: 'apply_model_diff', origin: 'agent', result: { applied: true, count: 2, restorePointId: 'rp-1' } };
  assert.deepEqual(copy.assistantPublishUndo(done), { id: 'rp-1', count: 2 });
  assert.equal(copy.assistantPublishUndo({ ...done, origin: 'human' }), null, 'the person\'s own press already shows its Undo');
  assert.equal(copy.assistantPublishUndo({ ...done, result: { ...done.result, applied: false } }), null, 'nothing was written');
  assert.equal(copy.assistantPublishUndo({ ...done, result: { applied: true, count: 2 } }), null, 'no restore point, no Undo');
  assert.equal(copy.assistantPublishUndo({ ...done, kind: 'deploy_gate' }), null);
  assert.match(changes, /assistantPublishUndo\(e\)/, 'the Changes tab listens for it');
  assert.match(changes, /onWrote\?\.\(\)/, 'and asks the page to read the restore points again');
});

// D6: while the assistant publishes, the new-model card shows where it publishes, from the engine's start and end.
test('newmodel: the card shows the target the assistant is publishing to, and lets go at the end', () => {
  const start = { kind: 'publish_target', origin: 'agent', result: { phase: 'start', workspace: 'Semanticus Demo', name: 'launch-video-test' } };
  assert.deepEqual(copy.assistantPublish(start), { phase: 'start', workspace: 'Semanticus Demo', name: 'launch-video-test' });
  assert.equal(copy.assistantPublish({ ...start, result: { ...start.result, phase: 'end' } }).phase, 'end');
  assert.equal(copy.assistantPublish({ ...start, kind: 'publish_new_model' }), null, 'only the target event counts');
  assert.equal(copy.assistantPublish({ ...start, origin: 'human' }), null, 'the person own press already holds its target in the form');
  assert.equal(copy.assistantPublish({ ...start, result: { phase: 'start' } }), null, 'no workspace, no target');
  assert.equal(copy.assistantPublishLine({ workspace: 'Semanticus Demo', name: 'launch-video-test' }), 'Your assistant is publishing launch-video-test to Semanticus Demo.');
  assert.match(changes, /onActivity\(\(e\) => \{\s*const t = assistantPublish\(e\)/, 'the card listens for the target');
  assert.match(changes, /data-testid="publish-assistant-target"/, 'and says it on the page');
  assert.match(changes, /disabled=\{busy \|\| !!target\}/, 'the name is held while the assistant publishes');
  assert.match(changes, /target \? <span className="sem-field" data-testid="publish-new-ws-target">\{target\.workspace\}<\/span>/, 'and the workspace shows the assistant\'s target');
  assert.match(read('webview/src/personality/link-state.ts'), /e\.kind === 'publish_target'\) return/, 'the target line never ends the Link call');
});

// hero19 D13: after a good publish the list and the button clear at once, before the page compares again.
test('what a publish sent leaves the list at once; a refused row stays', () => {
  const rows = [{ ref: 'measure:Sales/A' }, { ref: 'measure:Sales/B' }, { ref: 'measure:Sales/C' }];
  assert.deepEqual(copy.leftAfterPublish(rows, ['measure:Sales/A', 'measure:Sales/B'], []).map((r) => r.ref), ['measure:Sales/C']);
  assert.deepEqual(copy.leftAfterPublish(rows, ['measure:Sales/A', 'measure:Sales/B'], ['measure:Sales/B (merged locally but the live deploy did not carry it)']).map((r) => r.ref), ['measure:Sales/B', 'measure:Sales/C']);
  assert.deepEqual(copy.leftAfterPublish(rows, null, null), rows, 'nothing named, nothing removed');
  assert.ok(changes.includes('leftAfterPublish(d.items, r.appliedRefs, r.failedRefs)'), 'the page clears the list when the write lands');
});

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}

test('a level raise that landed says so, and a refused raise shows no engine words', () => {
  assert.equal(copy.plainPublishError('The compatibility level is now 1701. Nothing else was published. The XMLA request failed.'),
    'The compatibility level is now 1701. Nothing else was published. This change stays in your copy for now. Semanticus cannot send it to Power BI yet.');
  assert.equal(copy.plainPublishError('The compatibility level is now 1701. Nothing else was published. The service is busy.'),
    'The compatibility level is now 1701. Nothing else was published. The service is busy.');
  const refused = copy.plainPublishError('Power BI would not raise the compatibility level from 1600 to 1701, so nothing was published: Cannot change metadata via XMLA. Technical Details: RootActivityId: abc');
  assert.doesNotMatch(refused, /xmla|metadata|Technical Details|RootActivityId/i);
  assert.match(refused, /^Power BI would not raise the compatibility level from 1600 to 1701, so nothing was published\./);
  assert.equal(copy.plainPublishError('Something plain.'), 'Something plain.');
});

test('a refused level raise shows the person\'s sentence and only the service\'s first sentence, with no code, activity id or technical details', () => {
  const head = 'Power BI would not raise the compatibility level from 1600 to 1701, so nothing was published.';
  // the verifier probe (v-publish-shell, item 1): a plain first sentence stays, and nothing after it
  const probe = copy.plainPublishError('Power BI would not raise the compatibility level from 1600 to 1701, so nothing was published: The database compatibility level 1701 is not supported on this capacity. Error code: 0xC1120001. RootActivityId: 4f2c-11');
  assert.equal(probe, head + ' The database compatibility level 1701 is not supported on this capacity.');
  // technical details and everything after them go, even on the first sentence's line
  assert.equal(copy.plainPublishError(head + ' This capacity cannot do that. Technical Details: RootActivityId: abc; Date (UTC): 2026-10-13'),
    head + ' This capacity cannot do that.');
  // a first sentence that holds an error code or an engine word is not shown at all
  assert.equal(copy.plainPublishError(head + ' Failed with 0xC1120001 on the service.'), head);
  assert.equal(copy.plainPublishError(head + ' Cannot change metadata via XMLA.'), head);
  // nothing after the head: the head alone
  assert.equal(copy.plainPublishError(head), head);
  assert.equal(copy.plainPublishError('Power BI would not raise the compatibility level from 1600 to 1701, so nothing was published:'), head);
});

console.log(`publish surface tests passed (${passed})`);
