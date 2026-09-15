import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const app = readFileSync(resolve(root, 'webview', 'src', 'App.tsx'), 'utf8');
const deploy = readFileSync(resolve(root, 'webview', 'src', 'deploy.tsx'), 'utf8');
const context = readFileSync(resolve(root, 'webview', 'src', 'contextbar.tsx'), 'utf8');
// The Connections manager is now the hub (connectionshub.tsx), which REPLACED the drawer; the capability copy below
// must survive the move (drawer → hub), so this reads the hub as the single manager surface.
const connections = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');
const connection = readFileSync(resolve(root, 'webview', 'src', 'connection.tsx'), 'utf8');
const activity = readFileSync(resolve(root, 'webview', 'src', 'activity.tsx'), 'utf8');
const help = readFileSync(resolve(root, 'webview', 'src', 'help.tsx'), 'utf8');
const dataAgent = readFileSync(resolve(root, 'webview', 'src', 'dataagent.tsx'), 'utf8');
const spec = readFileSync(resolve(root, 'webview', 'src', 'spec.tsx'), 'utf8');
const bridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const tests = readFileSync(resolve(root, 'webview', 'src', 'tests.tsx'), 'utf8');
const diffview = readFileSync(resolve(root, 'webview', 'src', 'diffview.tsx'), 'utf8');
const interview = readFileSync(resolve(root, 'webview', 'src', 'interview.tsx'), 'utf8');
const permissions = readFileSync(resolve(root, 'webview', 'src', 'permissions.tsx'), 'utf8');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const pkg = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));

const groups = [...app.matchAll(/\{ id: '(model|calc|checks|changes|workflows)', label: '([^']+)', tabs:/g)]
  .map((m) => [m[1], m[2]]);
assert.deepEqual(groups, [
  ['model', 'Model'], ['calc', 'Calculations'], ['checks', 'Checks'], ['changes', 'Changes'], ['workflows', 'Workflows'],
]);
assert.match(app, /type StudioTab = ToolId/, 'the shell must use the typed route tool ids');
assert.match(app, /DESTINATION_OF_TOOL/, 'every stable tool id must map to an area');
assert.match(app, /const \[route, setRoute\] = useState<Route>/, 'navigation must be held as one typed route');
// There is no Back history to cap any more: Kane retired the button on 2026-09-14 ("The back button doesnt do
// anything") and the stack went with it. What replaced it is one recorded page, for Settings only.
assert.doesNotMatch(app, /backStack|backDepth|canGoBack|const goBack/, 'the Back history stack is gone, not merely hidden');
assert.match(app, /const returnTo = useRef<Route \| undefined>/, 'Settings remembers ONE page to return to, not a stack');
// The compact top added setBackDepth(0) to this line and Kane's removal of Back took it away again. What a model
// swap must clear is now the area memory and the ONE Settings return page, and nothing else: the cold hand-off
// (session becoming known, previousSid falsy) has to keep the return page, or Done lands on Overview instead of
// where the person actually was.
// \r?\n, not \n: the Windows CI checkout has CRLF line endings (*.ts text), and this pattern failed there first.
assert.match(app, /if \(previousSid\) returnTo\.current = undefined;\r?\n\s*lastInArea\.current = \{\};/, 'a model swap must clear route memory');
assert.match(app, /<ModelHome sessionId=\{session\.sessionId\}/, 'Model must open on a session-fenced real model home');
assert.match(app, /onTreeSelection\(\(selection\)/, 'tree selection must feed context without navigation');
assert.match(app, /selection\.sessionId !== sid/, 'late tree selection from another model must be dropped');
assert.match(app, /selected=\{routeIsCurrent \? \(route\.object \?\? selectedObject\) : undefined\}/, 'Model home must not show a prior session object');
assert.doesNotMatch(app, /← Back|studio-back/, 'no page in Studio carries a Back button any more');
// Settings is the one page with no segment strip of its own, so it is the one page with a way-out control.
assert.match(app, /\{isSettings && <button[^>]*data-testid="settings-done"[^>]*>Done<\/button>\}/, 'Settings carries one Done');
assert.match(app, /const goDone = \(\) => \{/, 'and Done is a real handler, not a prop wired to nothing');
assert.match(app, /Find a tool/, 'the shell must expose Find a tool');
assert.match(app, />Connections</, 'the shell must label Connections');
assert.match(app, />Publish…</, 'the shell must expose Publish');
assert.match(app, /raw === 'compare' \? 'deploy' : raw/, 'legacy Compare must resolve to Deploy');
assert.doesNotMatch(app, /raw === 'compare' \|\| raw === 'dataagent'/, 'Data Agent must retain its own route and Advanced subview');
assert.match(app, /<DeployView key="dataagent"[\s\S]*initialAdvanced="dataagent"/, 'the Data Agent route must land on its real subview');
assert.match(app, /consumeRouteSeed/, 'explicit seeds must be acknowledged once instead of replaying on return');
assert.match(context, />Editing</);
assert.match(context, />Tests</);
assert.match(context, />Publish to</);
assert.doesNotMatch(context, />Reference</, 'the bottom bar no longer carries a Reference slot');
assert.match(bridge, /treeSelectionListeners/, 'native selection context requires a dedicated bridge event');
assert.match(extension, /semanticus\.tryCalculation[\s\S]*navigateStudio\(context, 'daxlab'/, 'tree actions must open calculation with the selected measure');
assert.match(extension, /semanticus\.addTestForMeasure[\s\S]*navigateStudio\(context, 'tests'/, 'tree actions must open tests with the selected measure');
assert.match(extension, /type: 'treeSelection'[\s\S]*sessionId: currentSessionId/, 'tree selection must be stamped without navigating away from a draft');
assert.match(extension, /targetSessionId !== currentSessionId[\s\S]*editDaxCmd\(node\)/, 'Model home Edit formula must relay its exact session-scoped object to the native editor');
assert.match(app, /data-testid="publish-button"[^>]*>Publish…</, 'the header must carry the one Publish control');
assert.doesNotMatch(deploy, /data-testid="publish-button"/, 'the Published page must not repeat the header Publish button');
assert.doesNotMatch(tests, /<InterviewCard /, '1.2.0: the Model Interview evidence section left the Tests page');
assert.match(connection, /Choose test model/);

const bindings = pkg.contributes.keybindings
  .filter((x) => x.command === 'semanticus.studioGoGroup')
  .map((x) => [x.args, x.key]);
assert.deepEqual(bindings, [
  ['model', 'ctrl+shift+1'], ['calc', 'ctrl+shift+2'], ['checks', 'ctrl+shift+3'],
  ['changes', 'ctrl+shift+4'], ['workflows', 'ctrl+shift+5'],
]);

// --- the Model row is compact: six segments plus one Create dropdown, not eleven equal buttons ----------
const modelGroupSource = (app.match(/\{ id: 'model', label: 'Model', tabs: \[[\s\S]*?\n  \] \}/) || [''])[0];
const modelTabIds = [...modelGroupSource.matchAll(/\{ id: '([a-z]+)', label:/g)].map((m) => m[1]).slice(1);
assert.deepEqual(modelTabIds, [
  'modelhome', 'diagram', 'lineage', 'search', 'data', 'stats', 'spec', 'advmodels', 'mcode', 'docs', 'knowledge',
], 'TAB_GROUPS keeps all eleven Model tools, so Find a tool and direct routing still reach every one');
const listed = (name) => [...((app.match(new RegExp(name + ": StudioTab\\[\\] = \\[([^\\]]*)\\]")) || ['', ''])[1])
  .matchAll(/'([a-z]+)'/g)].map((m) => m[1]);
// M19: Size by table was in no segment and in no menu — a table row or Find a tool were the only ways in, and it
// then appeared as a segment that had never existed before you got there. It is a primary segment now.
assert.deepEqual(listed('MODEL_PRIMARY_TAB_IDS'), ['modelhome', 'diagram', 'lineage', 'search', 'data', 'stats'],
  'the Model row shows exactly these six as their own segment');
assert.deepEqual(listed('MODEL_CREATE_TAB_IDS'), ['spec', 'advmodels', 'mcode', 'docs', 'knowledge'],
  'the five authoring tools live behind the one Create dropdown segment');
assert.match(app, /Create: \$\{activeItem\.label\}/,
  'an open Create tool renames the segment so it agrees with the breadcrumb');
assert.match(app, /openElsewhere/,
  'a Model tool in neither list still gets an active segment while it is open, so the row is never left unmarked');

// Model home must not repeat the segments sitting directly above it; the two genuine extras stay.
const modelHome = readFileSync(resolve(root, 'webview', 'src', 'modelhome.tsx'), 'utf8');
assert.doesNotMatch(modelHome, /pagehead-actions/,
  'Model home no longer offers a second route to Diagram, Lineage and Find and replace');
// The two extras moved out of a bare link row into the Overview's Published card (cp/overview-glance), where
// they sit beside the destination they act on. What must not change is that both stay reachable from the
// landing page without going hunting for them.
assert.doesNotMatch(modelHome, /model-home-links/,
  'the old Model home link row is gone, not merely hidden');
assert.match(modelHome, /Connections and accounts[\s\S]{0,240}Dependencies/,
  'Model home keeps the two routes that are not segments, now on the Published card');

// =====================================================================================================
// Walkthrough behaviour defects (cp/walk-behaviour). Each assertion below is the contract the quality
// walkthrough proved was missing, written before the fix.
// =====================================================================================================
const route = readFileSync(resolve(root, 'webview', 'src', 'route.ts'), 'utf8');
const styles = readFileSync(resolve(root, 'webview', 'src', 'styles.css'), 'utf8');
const optimize = readFileSync(resolve(root, 'webview', 'src', 'optimize.tsx'), 'utf8');

// --- M4: the Changes area opens on the work in front of you, not always on the second tab --------------
assert.match(route, /export function areaHomeTool\(/,
  'the area home must be a function, because the Changes home depends on whether a plan exists');
assert.match(route, /areaHomeTool[\s\S]{0,300}'changes'[\s\S]{0,80}'optimize'[\s\S]{0,80}AREA_HOME\[area\]/,
  'Changes must open on Proposed when a plan exists and fall back to History when none does');
assert.match(app, /areaHomeTool\(area, /, 'goGroup must use the plan-aware area home');
assert.match(app, /onPlanChange\(/, 'the shell must track whether a plan exists to place the Changes home');

// --- M4: the sameSpot rule existed only to keep junk out of the Back stack. Both are gone. What replaces it is
// narrower and cannot have the same failure: Settings records the page you came from on the way IN and only then,
// so pressing Settings twice cannot make Done return you to Settings.
assert.doesNotMatch(app, /sameSpot/, 'the sameSpot push rule went with the stack it protected');
assert.match(app, /if \(next\.destination === 'utility' && current\.destination !== 'utility'\)/,
  'the return page is recorded on the way into Settings, never on the way from one Settings route to another');

// --- M5: only the current tool may read as active. A pointer pick must not leave the Create segment ringed
assert.match(app, /closeAfterPick/,
  'choosing a Create tool with the pointer must not leave the focus ring on the segment');
assert.match(app, /buttonRef\.current\?\.blur\(\)/, 'the pointer path blurs the segment it navigated away from');
assert.match(app, /closeAndReturnFocus/, 'Escape still returns focus to the Create segment');

// --- B1: one panel's render error is a card in that panel, never the whole Studio -----------------------
assert.match(app, /class ToolErrorBoundary extends Component/, 'tool views need their own error boundary');
assert.match(app, /This page hit a problem/, 'the in-place card must say what happened in plain words');
assert.match(app, /Reload page/, 'the in-place card must offer a way out');
assert.match(app, /<ToolErrorBoundary resetKey=\{tab\}>/,
  'the boundary must clear itself when you navigate to another tool');
assert.doesNotMatch(optimize, /report\.overall(Before|After)\.toFixed/,
  'an absent score must not be dereferenced: it crashed the whole Studio on every Apply');
assert.match(optimize, /function scoreText\(/, 'the score must go through one guard');

// --- M23: the segmented control stays under the header while the page scrolls --------------------------
// The contract is unchanged and is now met more strongly. The strip used to be a position:sticky bar INSIDE the
// scrolling <main>, which held it under the header at every scroll position. Under the compact top it is not in
// the scroll region at all: it sits in the chrome, on the area row, above <main>. A sticky bar can still be
// escaped by a nested scroller or a transformed ancestor; a sibling of <main> cannot scroll away at all. So what
// this pins now is the STRUCTURE that makes it true, plus the absence of the old bar.
assert.doesNotMatch(app, /area-tabs-bar/, 'the sticky bar inside main is replaced, not merely bypassed');
assert.doesNotMatch(styles, /\.area-tabs-bar/, 'and its rules go with it');
assert.match(app, /<div className="studio-arearow">[\s\S]*?<AreaTabs group=\{group!\}[\s\S]*?<\/div>\s*\{\/\*[\s\S]*?<main /,
  'the segment strip must sit on the area row, ABOVE the scrolling main, not inside it');
assert.match(styles, /\.studio-arearow \{[^}]*min-height: 40px/, 'the area row is one fixed-height row');
assert.match(styles, /\.area-tabs \{[^}]*flex-wrap: nowrap/,
  'the strip may never wrap onto a second line: that would make the row a different height on one page');

// --- P1: an object in the route may never take the navigation away ------------------------------------
// Kane, on the Yoga, 2026-09-15: "went to data preview from overview and no way back, it hides all navigation
// buttons under model". Overview > Preview data hands Data the table as the route object, and the strip rule
// hid itself whenever a route carried one. The row read "Model › Access Assignment › Data" with no segments,
// Back had already been retired, and the Model button restored the remembered page, which was that same page.
// An object never changes WHICH tools an area has, so it never has a say in whether the strip is shown.
assert.match(app, /const showStrip = !!group && group\.tabs\.length > 1;/,
  'the segment strip shows on every tool of a multi-tool area, object in hand or not');
assert.doesNotMatch(app, /const showStrip =[^;]*route\.object/,
  'and the strip may never again be hidden because the route carries an object');
// The object leaves this row altogether. It was a breadcrumb crumb in front of the tool name; a chip with a way
// to clear it was tried in its place and dropped (Kane, 2026-09-15), because the page already names what it was
// opened with and the row could only clear the ROUTE's object while the page kept its own selection. The row
// says where you are, the page says what you are looking at, and neither says it twice.
assert.doesNotMatch(app, /\{objectLabel\(route\.object\) && <><strong/,
  'the object is not a crumb in front of the tool name');
assert.doesNotMatch(app, /studio-arearow-object|arearow-clear-object/,
  'and it is not a chip on the row either: nothing on this row names the route object');
assert.doesNotMatch(styles, /\.studio-arearow-object/, 'and the chip rules go with it');
assert.match(app, /<span className="studio-arearow-area">\{areaLabel\} ›<\/span>\s*\{showStrip\s*\? <AreaTabs[\s\S]{0,200}: <strong className="studio-arearow-tool">\{toolLabel\}<\/strong>\}/,
  'the row is the area name and then exactly one of the strip or the tool name');
// The second exit, and the one that made the first trap inescapable: the area you are ALREADY in is a way out,
// so it goes to that area's home page rather than restoring the page you are looking at.
assert.match(app, /routeRef\.current\.destination === area \? undefined : lastInArea\.current\[area\]/,
  'clicking the area you are in must go home, not restore the page you are already on');

// --- M18: the table you picked is visibly the one you picked -------------------------------------------
// The Overview rebuild renamed the row class (model-table -> overview-row) and folded the measures note into
// the hint that stands where the action strip will appear. The contract is unchanged: the picked row is marked
// for eye and screen reader alike, and the page says where a measure is picked instead of leaving you hunting.
assert.match(modelHome, /overview-row is-selected/, 'the selected table row must be marked in the list');
assert.match(styles, /\.overview-row\.is-selected/, 'the selected table row must be painted');
assert.match(modelHome, /aria-current=\{isSelected \? 'true' : undefined\}/,
  'the selection must be announced, not only painted');
assert.match(modelHome, /measure in the model tree/,
  'the page lists tables only, so say plainly where measures are chosen');

// --- the header finishes: the gear goes, your assistant owns its own menu, Help owns the repairs --------
// Kane, 2026-09-14: "I dont like the settings icon next to connections". Assistant permissions were behind a
// gear that named nothing; they belong under the chip that already speaks for the assistant. Restart engine
// was palette-only, so a stuck engine had no visible way out of Studio.
assert.doesNotMatch(app, /SettingsGear/, 'the gear leaves the header entirely, glyph and all');
assert.doesNotMatch(app, /studio-settings/, 'no gear button is left in the header utilities');
assert.doesNotMatch(app, /aria-label="Settings"/, 'nothing in the header is labelled only "Settings"');

assert.match(activity, /role="menu" aria-label="Your assistant"/, 'the chip opens a real menu');
assert.match(activity, /approval\$\{pendingApprovalCount === 1 \? '' : 's'\} waiting/,
  'a waiting approval is the first thing the menu offers, and it counts in plain English');
assert.match(activity, /if \(pendingApprovalCount > 0\) items\.push\(\{/,
  'the approvals item is absent at zero, not greyed');
assert.match(activity, /label: 'Live activity'/, 'the live feed keeps its home, one level in');
assert.match(activity, /label: 'Permissions'/, 'assistant permissions are named in words, where the gear used to be');
assert.match(activity, /label: 'Connect your assistant'/, 'wiring the assistant is reachable from the chip');
assert.doesNotMatch(activity, /label: '[^']*Claude/, 'and it says it the way the product says it, never a model name');
assert.match(activity, /runHostCommand\('semanticus\.connectClaudeCode'\)/,
  'Connect Claude Code runs the existing host command, it does not reimplement it');
assert.match(activity, /assistantState/, 'the connect item carries its own state as right-hand text');
assert.match(activity, /feed\.length > 0 \|\| pendingApprovalCount > 0/,
  'the state is read from proof the assistant used this session, never guessed');
assert.doesNotMatch(activity, /if \(feed\.length === 0 && pendingApprovalCount === 0\) return null;/,
  'the chip now owns permissions and connect, so it must not disappear before the first run');

assert.match(help, /role="menu" aria-label="Help"/, 'Help is a menu now, not one button with one destination');
assert.match(help, /label: 'Help for this page'/, 'the slide-over keeps its place as the first item');
assert.match(help, /label: 'Keyboard shortcuts'/, 'the shortcuts sheet is reachable without opening the slide-over');
assert.match(help, /Something wrong\?/, 'the repairs sit in their own named section');
assert.match(help, /label: 'Restart engine'/, 'a stuck engine has a visible way out of Studio');
assert.match(help, /runHostCommand\('semanticus\.restartEngine'\)/,
  'Restart engine runs the existing host command');

assert.doesNotMatch(context, /connections-reference/, 'the Reference button leaves the bottom bar');
assert.doesNotMatch(context, /referenceMain|referenceSub/, 'and its copy goes with it');
assert.match(styles, /\.studio-contextbar \{[^}]*grid-template-columns: repeat\(3, minmax\(0, 1fr\)\) auto auto/,
  'the bottom bar is three roles plus the pill and Review changes');
assert.doesNotMatch(connections, /Reference model/, 'the Connections hub drops the Reference role card too');
assert.doesNotMatch(connections, /useAsReference/, 'and the hub no longer sets one');

// The ids the webview may hand the host, and nothing else. This set is a security boundary: anything listed
// here can be fired by webview content, so it is written out in full and changing it is a deliberate act.
// openModel and showOutput were added on 2026-09-15 for the Studio recovery panel, which IS the whole page when
// there is no engine; openModel opens the Connections hub and showOutput reveals the output channel, so like the
// other four neither writes anything on its own.
const allowlist = (extension.match(/const WEBVIEW_HOST_COMMANDS = new Set\(\[([^\]]*)\]\)/) || [])[1];
assert.ok(allowlist, 'the host command allowlist must still be one readable literal');
assert.deepEqual([...allowlist.matchAll(/'([^']+)'/g)].map((m) => m[1]).sort(), [
  'semanticus.connectClaudeCode', 'semanticus.restartEngine', 'semanticus.save', 'semanticus.undo',
  'semanticus.redo', 'workbench.action.showCommands', 'semanticus.openModel', 'semanticus.showOutput',
].sort(), 'the allowlist is exactly these eight ids');

console.log('intent navigation tests passed');
