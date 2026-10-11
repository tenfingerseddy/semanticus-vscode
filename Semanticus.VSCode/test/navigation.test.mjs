import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
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
const permissions = readFileSync(resolve(root, 'webview', 'src', 'permissions.tsx'), 'utf8');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const pkg = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));

// The areas Kane picked on 22 September 2026 (docs/redesign/ui-hierarchy.md), in strip order. Seven until slice 11
// retired the Overview (Plan-heart lane R1): the Plan is the first area and the start page. The id and the word are
// read out of TAB_GROUPS itself, so a rename that forgets one of the other render sites fails here rather than
// shipping a strip that disagrees with itself.
// Scoped to the literal, never to the whole file: a comment elsewhere in App.tsx that happens to spell an
// entry out would otherwise join the strip, which is exactly what happened the first time this was written.
const groupsBlock = (app.match(/const TAB_GROUPS[\s\S]*?\n\];/) || [''])[0];
assert.ok(groupsBlock, 'TAB_GROUPS must be findable as one literal');
const groups = [...groupsBlock.matchAll(/\{ id: '([a-z]+)', label: '([^']+)', tabs:/g)].map((m) => [m[1], m[2]]);
assert.deepEqual(groups, [
  ['plan', 'Plan'], ['explore', 'Explore'], ['edit', 'Edit'],
  ['review', 'Review'], ['publish', 'Publish'], ['workflows', 'Workflows'],
]);
// Analyse was the word until Kane's pick; Review replaced it in the same decision.
assert.doesNotMatch(app, /label: 'Analyse'/, 'the fifth area is Review, not Analyse');

// Every tool, by the name the tab actually has. This list IS the contract the engine's ask box copies, so
// it is written out in full rather than counted.
//
// Pending changes and Model Spec left this list on 23 September. They are not gone: Kane folded all three
// of the Plan area's pages into one ("No point in three similar pages, it makes it very confusing for
// users"), so `optimize` and `spec` are routes that land on the Plan page with the matching part open,
// the way `evidence` lands on Validation. A route that is not a strip button is not a strip tool.
const stripTools = [...groupsBlock.matchAll(/\{ id: '([a-z]+)', label: '([^']+)' \}/g)].map((m) => [m[1], m[2]]);
assert.deepEqual(stripTools, [
  ['plan', 'Plan'],
  ['data', 'Data'], ['askdata', 'Ask your data'], ['diagram', 'Diagram'], ['lineage', 'Lineage'], ['stats', 'Size'],
  ['search', 'Find and replace'], ['daxlab', 'Queries'],
  ['instructions', 'AI instructions'], ['advmodels', 'Advanced modelling'], ['mcode', 'Power Query'],
  ['bpa', 'Model quality (BPA)'], ['readiness', 'AI readiness'], ['tests', 'Validation'], ['docs', 'Docs'],
  ['deploy', 'Published'],
  ['workflows', 'Workflows'],
], 'the strip is seventeen tools in six areas, each by the name its own tab has. Ask your data joined Explore on 26 September. Model notes became the Notes tab of AI instructions, and Pending changes and Model Spec folded into Plan, on 23 September; the Overview and Edit History retired into the Plan in slice 11.');
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
assert.match(app, /<PlanView sessionId=\{session\.sessionId\}/, 'a model opens on the session-fenced Plan, the start page since slice 11');
assert.match(app, /onTreeSelection\(\(selection\)/, 'tree selection must feed context without navigation');
assert.match(app, /selection\.sessionId !== sid/, 'late tree selection from another model must be dropped');
// The Plan's object is the route's, else the tree selection (as on the Overview); both are fenced to this session:
// the route by routeIsCurrent, the selection by the session effect that resets it on every model swap.
assert.match(app, /const planObject = tab === 'plan' && routeIsCurrent \? \(route\.object \?\? selectedObject\) : undefined;/, 'the Plan must not show a prior session object');
assert.match(app, /setSelectedObject\(initialSelection\);/, 'and a model swap replaces the tree selection');
assert.doesNotMatch(app, /← Back|studio-back/, 'no page in Studio carries a Back button any more');
// Settings is the one page with no segment strip of its own, so it is the one page with a way-out control.
assert.match(app, /\{isSettings && <div className="ml-auto flex items-center gap-2">\s*<button[^>]*data-testid="settings-done"[^>]*>Done<\/button>\s*<\/div>\}/, 'Settings carries one Done');
assert.match(app, /const goDone = \(\) => \{/, 'and Done is a real handler, not a prop wired to nothing');
assert.match(app, /Find a tool/, 'the More menu at a narrow width keeps its own copy of the tool list');
assert.match(help, /id: 'all-tools', label: 'All tools'/, 'Help > All tools opens the same list of every tool (the header Find a tool button left, Kane 8 Oct)');
assert.match(app, /<HelpButton[\s\S]{0,300}onAllTools=/, 'and the shell wires it to the ToolChooser menu');
assert.doesNotMatch(app, /<div className="studio-utilities">\s*<ToolChooser/, 'Find a tool is not a header button any more');
assert.match(app, />Connections</, 'the shell must label Connections');
assert.match(app, />Publish</, 'the shell must expose Publish');
const route = readFileSync(resolve(root, 'webview', 'src', 'route.ts'), 'utf8');
assert.match(route, /compare: \{ tool: 'deploy' \}/, 'legacy Compare must resolve to Deploy');
assert.doesNotMatch(route, /dataagent: \{ tool:/, 'Data Agent keeps its own route id');
assert.match(app, /tab === 'deploy' \|\| tab === 'dataagent' \?[\s\S]{0,260}<DeployView key="deploy" seed=\{compareSeed\} \/>/, 'the Data Agent route lands on the Publish page, Changes tab (the Data Agent screen is hidden for now)');
assert.match(app, /consumeRouteSeed/, 'explicit seeds must be acknowledged once instead of replaying on return');
assert.match(context, /data-testid="connections-editing"/);
assert.match(context, />Tests:</);
assert.match(context, />Publish:</);
assert.doesNotMatch(context, />Reference</, 'the bottom bar no longer carries a Reference slot');
assert.match(bridge, /treeSelectionListeners/, 'native selection context requires a dedicated bridge event');
assert.match(extension, /semanticus\.tryCalculation[\s\S]*navigateStudio\(context, 'daxlab'/, 'tree actions must open calculation with the selected measure');
assert.match(extension, /semanticus\.addTestForMeasure[\s\S]*navigateStudio\(context, 'tests'/, 'tree actions must open tests with the selected measure');
assert.match(extension, /type: 'treeSelection'[\s\S]*sessionId: currentSessionId/, 'tree selection must be stamped without navigating away from a draft');
assert.match(extension, /targetSessionId !== currentSessionId[\s\S]*editDaxCmd\(node\)/, 'Model home Edit formula must relay its exact session-scoped object to the native editor');
assert.match(app, /data-testid="publish-button"[^>]*>Publish</, 'the header must carry the one Publish control');
assert.doesNotMatch(deploy, /data-testid="publish-button"/, 'the Published page must not repeat the header Publish button');
assert.doesNotMatch(tests, /<InterviewCard /, '1.2.0: the Model Interview evidence section left the Tests page');
assert.match(connection, /Choose test model/);

const bindings = pkg.contributes.keybindings
  .filter((x) => x.command === 'semanticus.studioGoGroup')
  .map((x) => [x.args, x.key]);
assert.deepEqual(bindings, [
  ['plan', 'ctrl+shift+1'], ['explore', 'ctrl+shift+2'], ['edit', 'ctrl+shift+3'],
  ['review', 'ctrl+shift+4'], ['publish', 'ctrl+shift+5'], ['workflows', 'ctrl+shift+6'],
], 'one key per area, in strip order, and the host command names the same ids the webview does');
// The host's own area picker offers the same six, by the same ids: a person who opens the command
// instead of pressing the key must not be shown a list of areas that no longer exist.
const pickerAreas = [...(extension.match(/const groups: Array<vscode\.QuickPickItem & \{ key: string \}> = \[[\s\S]*?\];/) || [''])[0]
  .matchAll(/key: '([a-z]+)'/g)].map((m) => m[1]);
assert.deepEqual(pickerAreas, ['plan', 'explore', 'edit', 'review', 'publish', 'workflows'],
  'Go to Studio Area offers the six areas');

// --- the Edit row is a plain strip: Advanced modelling is one tab, and its page row lists every modelling page ----
// Kane, 8 Oct 2026: no dropdown. Power Query has no segment on the Edit row; it is a button on the Advanced modelling
// page's own row, next to the six builders, and it lights the Advanced modelling tab while it is open.
assert.match(app, /const STRIP_HIDDEN_TABS: StudioTab\[\] = \['mcode'\];/, 'Power Query is hidden from the Edit strip');
assert.doesNotMatch(app, /AdvancedFoldTab|EDIT_FOLD_TAB_IDS|EDIT_PRIMARY_TAB_IDS|ADVANCED_FOLD_ITEMS|area-tabs-create/, 'and the dropdown and both of its lists are gone');
assert.match(app, /tab === 'mcode' \? 'advmodels' : tab/, 'an open Power Query lights the Advanced modelling segment');
assert.match(app, /\{ id: 'advmodels', label: 'Advanced modelling' \}/, 'the Edit row keeps the label Advanced modelling as a plain tab');
// The page row lists seven things and every one has a STABLE id, because the Plan and the ask box jump straight
// to a sub-page. An id that quietly stopped matching would drop the person on whichever sub-page was open
// last, with nothing on screen to say it had ignored where they asked to go.
const advmodels = readFileSync(resolve(root, 'webview', 'src', 'advmodels.tsx'), 'utf8');
const foldSource = (advmodels.match(/const AREA_TABS: \[Area, string\]\[\] = \[[\s\S]*?\n\];/) || [''])[0];
const foldItems = [...foldSource.matchAll(/\['([a-z]+)', '([^']+)'\]/g)].map((m) => [m[1], m[2]]);
assert.deepEqual(foldItems, [
  ['mcode', 'Power Query'],
  ['calendars', 'Calendars'],
  ['calcgroups', 'Calculation groups'],
  ['fieldparams', 'Field parameters'],
  ['rls', 'RLS / OLS'],
  ['perspectives', 'Perspectives'],
  ['daxlib', 'DaxLib'],
], 'the modelling pages are Power Query and the six builders, in one order, each by its own stable id');
assert.match(advmodels, /useState<Area>\('mcode'\)/, 'the page lands on its first item, Power Query (coordinator, 8 Oct)');
assert.match(advmodels, /AREA_TABS\.map\(\(\[id, label\]\) => <Seg/, 'the page row is drawn from that one list');
assert.match(app, /tab === 'advmodels' \|\| tab === 'mcode' \? \(/, 'Power Query (mcode) opens the same page, so it carries the same row with Power Query lit');
assert.doesNotMatch(app, /MODELLING_PAGES|ModellingRow/, 'and App keeps no second copy of the list');
// rlsols was that sub-page's id until the fold needed one word for it. The old id still resolves, because
// askbox.tsx and the amRlsOls command both still spell it that way.
assert.match(advmodels, /AREA_ALIASES: Record<string, Area> = \{ rlsols: 'rls' \}/, 'the older sub-page id still lands');

// --- every retired area word still lands somewhere ----------------------------------------------------
// Saved state and old deep links outlive a rename, and an unknown word makes the area button do nothing at
// all. Eleven words have been retired from this shell; every one of them must resolve to a real area. The table
// lives in route.ts since slice 11, where test/retired-routes.test.mjs executes it rather than reading it.
const aliasSource = (route.match(/export const AREA_ALIASES: Record<string, Area> = \{[\s\S]*?\n\};/) || [''])[0];
const aliases = Object.fromEntries([...aliasSource.matchAll(/(\w+): '([a-z]+)'/g)].map((m) => [m[1], m[2]]));
const areaIds = new Set(groups.map(([id]) => id));
for (const retired of ['overview', 'model', 'calc', 'checks', 'changes', 'understand', 'change', 'improve', 'prove', 'ship']) {
  assert.ok(aliases[retired], `the retired area word "${retired}" must still resolve`);
  assert.ok(areaIds.has(aliases[retired]), `"${retired}" resolves to "${aliases[retired]}", which is not one of the six`);
}
for (const id of areaIds) assert.equal(aliases[id], id, `the area id "${id}" must resolve to itself`);

// Model home (the Overview) retired in slice 11. Its Published tile's two ways through are the Publish area and the
// header's Connections, both on every page; test/retired-routes.test.mjs proves the old ids still land.
assert.ok(!existsSync(resolve(root, 'webview', 'src', 'modelhome.tsx')), 'modelhome.tsx is gone, not merely unmounted');

// =====================================================================================================
// Walkthrough behaviour defects (cp/walk-behaviour). Each assertion below is the contract the quality
// walkthrough proved was missing, written before the fix.
// =====================================================================================================
const styles = readFileSync(resolve(root, 'webview', 'src', 'styles.css'), 'utf8');
const optimize = readFileSync(resolve(root, 'webview', 'src', 'optimize.tsx'), 'utf8');

// --- M4, and how it ended: an area opens on the FIRST segment of its own row --------------------------
// The Changes row read Pending changes / History / Published and its home was History, so pressing the area
// button always landed on the middle segment, which read as a bug; the home was made plan-aware to dodge it.
// The seven-area rows have no middle-tab home left (Plan opens on Plan, Publish opens on Published, both
// first in their own row), so the exception is GONE rather than moved, and so is the plan read the shell
// used to make on every session just to place one button.
assert.match(route, /export function areaHomeTool\(area: Area\): ToolId \{\s*\n\s*return AREA_HOME\[area\];/,
  'the area home is a plain lookup now: no area needs an exception');
assert.match(app, /else goTab\(areaHomeTool\(area\)\);/, 'goGroup lands on the area home');
assert.doesNotMatch(app, /hasPlanRef/,
  'and the shell no longer reads the plan on every session to decide where one button goes');
// The shell DOES read the plan once more since the Plan became the heart, for one thing only: the "Your assistant"
// chip's count (contract 11.4). It must never feed where an area button lands.
assert.doesNotMatch(app.slice(app.indexOf('const goGroup'), app.indexOf('const goGroup') + 900), /assistantCount|getModelPlan/,
  'the chip count never decides where an area button goes');
const homes = Object.fromEntries([...(route.match(/AREA_HOME: Record<Area, ToolId> = \{[\s\S]*?\n\};/) || [''])[0]
  .matchAll(/(\w+): '(\w+)'/g)].map((m) => [m[1], m[2]]));
assert.deepEqual(homes, {
  plan: 'plan', explore: 'data', edit: 'search',
  review: 'bpa', publish: 'deploy', workflows: 'workflows',
}, 'every area opens on the first segment of its own row');

// --- M4: the sameSpot rule existed only to keep junk out of the Back stack. Both are gone. What replaces it is
// narrower and cannot have the same failure: Settings records the page you came from on the way IN and only then,
// so pressing Settings twice cannot make Done return you to Settings.
assert.doesNotMatch(app, /sameSpot/, 'the sameSpot push rule went with the stack it protected');
assert.match(app, /if \(next\.destination === 'utility' && current\.destination !== 'utility'\)/,
  'the return page is recorded on the way into Settings, never on the way from one Settings route to another');

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
assert.match(app, /<div className="studio-arearow">[\s\S]*?<AreaTabs group=\{group!\}[\s\S]*?<\/div>\}?\s*\{\/\*[\s\S]*?<main /,
  'the segment strip must sit on the area row, ABOVE the scrolling main, not inside it');
// 40px is --sem-bar-h (tokens.css), One Visual Language.
assert.match(styles, /\.studio-arearow \{[^}]*min-height: (40px|var\(--sem-bar-h\))/, 'the area row is one fixed-height row');
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
assert.match(app, /\{\(showStrip \|\| isSettings\) && <div className="studio-arearow">\s*\{isSettings && <span className="studio-arearow-area">\{areaLabel\}\{nameRepeats \? '' : ' ›'\}<\/span>\}\s*\{showStrip\s*\? <AreaTabs[\s\S]{0,240}: nameRepeats \? null : <strong className="studio-arearow-tool">\{toolLabel\}<\/strong>\}/,
  'the row exists only for a strip or a Settings page; the area name shows on Settings alone (the lit area in the header says it elsewhere), then at most one of the strip or the tool name');
// Plan and Workflows are areas with one tool, and that tool carries the area's own name, so the row used
// to read "Overview › Overview" when the Overview was one of them. It says it once. The chevron goes with the half that was dropped, or it
// dangles off the end of a row pointing at nothing.
assert.match(app, /const nameRepeats = !showStrip && toolLabel === areaLabel;/,
  'the row knows when the area and the tool are the same word');
// The second exit, and the one that made the first trap inescapable: the area you are ALREADY in is a way out,
// so it goes to that area's home page rather than restoring the page you are looking at.
assert.match(app, /routeRef\.current\.destination === area \? undefined : lastInArea\.current\[area\]/,
  'clicking the area you are in must go home, not restore the page you are already on');

// --- M18: the object you picked is visibly the one you picked -------------------------------------------
// The Overview's table list marked the picked row. The Overview retired in slice 11 (lane R1), and the picked
// object now filters the Plan: the list says "Showing <object>" and the object's actions sit under that line,
// named for eye and screen reader alike (contract 11.1). The browser proof is cp-plan-drive.mjs, scene object-line.
const plan = readFileSync(resolve(root, 'webview', 'src', 'plan.tsx'), 'utf8');
assert.match(plan, /Showing <b>\{track \? P\.DOER_LABEL\[track\] : focusObject\?\.label\}<\/b>/, 'the Plan names the picked object');
assert.match(plan, /<section className="pw-object" data-testid="plan-object-actions" aria-label=\{`\$\{kind\} \$\{name\}`\}>/,
  'and its actions are announced with the object they act on');

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
assert.match(activity, /if \(!assistantSeen\) items\.push\(\{ id: 'connect'/, 'the connect item shows only while the assistant has not been seen, and carries no state word');
assert.doesNotMatch(activity, /Settings: what Pro decides/, 'What Pro decides is a section of the Pro page, not a menu item');
assert.match(activity, /feed\.length > 0 \|\| pendingApprovalCount > 0/,
  'the state is read from proof the assistant used this session, never guessed');
assert.doesNotMatch(activity, /if \(feed\.length === 0 && pendingApprovalCount === 0\) return null;/,
  'the chip now owns permissions and connect, so it must not disappear before the first run');

assert.match(help, /role="menu" aria-label="Help"/, 'Help is a menu now, not one button with one destination');
assert.match(help, /label: 'Help for this page'/, 'the slide-over keeps its place as the first item');
assert.match(help, /label: 'Keyboard shortcuts'/, 'the shortcuts sheet is reachable without opening the slide-over');
assert.match(help, /label: 'Restart engine'/, 'a stuck engine has a visible way out of Studio');
assert.match(help, /runHostCommand\('semanticus\.restartEngine'\)/,
  'Restart engine runs the existing host command');

assert.doesNotMatch(context, /connections-reference/, 'the Reference button leaves the bottom bar');
assert.doesNotMatch(context, /referenceMain|referenceSub/, 'and its copy goes with it');
assert.match(styles, /\.studio-contextbar \{[^}]*display: flex/,
  'the bottom bar is the model, Tests, Publish and the pill on one line (Kane kept it and shortened it, 8 Oct)');
assert.doesNotMatch(context, /Review changes|connection-hint/, 'it has no Review changes and no per-slot hints');
assert.doesNotMatch(connections, /Reference model/, 'the Connections hub drops the Reference role card too');
assert.doesNotMatch(connections, /useAsReference/, 'and the hub no longer sets one');

// The ids the webview may hand the host, and nothing else. This set is a security boundary: anything listed
// here can be fired by webview content, so it is written out in full and changing it is a deliberate act.
// openModel and showOutput were added on 2026-09-15 for the Studio recovery panel, which IS the whole page when
// there is no engine; openModel opens the Connections hub and showOutput reveals the output channel, so like the
// other four neither writes anything on its own.
// upgradeToPro and manageSubscription were added on 2026-09-21 for the Pro button and LEFT on 23 September:
// the Semanticus Pro page is a Studio route now, and its clicks reach the host as 'pro' messages through one
// handler that checks each message's shape (extension.ts handleProPageMessage), so no webview needs them.
// draftFromSqlSource was added on 2026-09-28 for From a SQL source on the standalone Connections panel, and LEFT on
// 2 October: that panel now shows the start plan's own form, whose start makes and opens the model, so it asks the host
// for openStudio, which only opens Studio on the open model and writes nothing.
// signInToReconnect was added on 8 October 2026 (r1/reopen, K15): Studio's empty page offers it for the model that waits
// for a sign-in, and it runs the same person-only sign-in the notice's button runs.
const allowlist = (extension.match(/const WEBVIEW_HOST_COMMANDS = new Set\(\[([^\]]*)\]\)/) || [])[1];
assert.ok(allowlist, 'the host command allowlist must still be one readable literal');
assert.deepEqual([...allowlist.matchAll(/'([^']+)'/g)].map((m) => m[1]).sort(), [
  'semanticus.connectClaudeCode', 'semanticus.restartEngine', 'semanticus.save', 'semanticus.undo',
  'semanticus.redo', 'workbench.action.showCommands', 'semanticus.openModel', 'semanticus.showOutput',
  'semanticus.openStudio', 'semanticus.signInToReconnect',
].sort(), 'the allowlist is exactly these ten ids');

// --- no list outside Studio may name a place that is not on the strip ------------------------------
//
// The defect this closes: a page folds into another page as a tab, the strip loses its button, and the
// lists OUTSIDE the webview keep offering the old name. Nothing went wrong when you picked it, because the
// route id still resolves; you were simply promised a page that does not exist any more. Model notes did
// exactly this on 23 September 2026 and was found by eye, in the host tool picker and in Ctrl+Alt+K.
//
// The rule: every entry in the host's Go to Studio Tool picker and in the shortcut overlay must name a
// place the strip knows. A place that is a TAB inside another page says so, "<the page>: <the tab>", so a
// person reads where they are going before they get there.
const shortcuts = readFileSync(resolve(root, 'webview', 'src', 'shortcuts.tsx'), 'utf8');
// The strip's own words, with any trailing note in brackets dropped: "Model quality (BPA)" is the place
// called Model quality, and the shortcut overlay is allowed to hang its own note off the same name.
const stripNames = new Set(stripTools.map(([, label]) => label.replace(/\s*\([^)]*\)\s*$/, '').trim()));
// Which page holds each id that is a tab rather than a page. Kept beside the shell's own answer below, so
// a third tab-inside-a-page cannot be added here without the shell agreeing.
const TAB_HOSTS = { evidence: 'tests', knowledge: 'instructions', optimize: 'plan', spec: 'plan' };
const visibleTabLine = [...app.matchAll(/const visibleTab = [^;]*;/gs)]
  .map((m) => m[0]).find((line) => line.includes("'deploy'")) ?? '';
for (const [id, host] of Object.entries(TAB_HOSTS)) {
  assert.ok(visibleTabLine.includes(`tab === '${id}' ? '${host}'`),
    `${id} is listed here as a tab inside ${host}, and App's segment strip does not agree`);
}
// History became a view inside the Plan in slice 11 (lane R2). It never lights a segment, because route.ts lands the
// id on the Plan itself, so its host is checked against that table instead of App's segment line.
assert.match(route, /history: \{ tool: 'plan', view: 'history' \}/, 'history lands on the Plan, on its History view');
TAB_HOSTS.history = 'plan';
const stripLabelOf = (id) => (stripTools.find(([tid]) => tid === id) || [])[1];
// Settings is a place of its own with no area and so no strip button: App gives it its own name in the
// area row. It is the ONLY thing allowed to name itself without appearing on the strip.
const OFF_STRIP_PLACES = { permissions: 'Assistant permissions' };

/** What is wrong with one entry of an outside list, or nothing. */
const namesARealPlace = (where, key, label) => {
  if (OFF_STRIP_PLACES[key]) {
    return OFF_STRIP_PLACES[key] === label ? null
      : `${where} calls ${key} ${JSON.stringify(label)}, and that page is called ${JSON.stringify(OFF_STRIP_PLACES[key])}`;
  }
  const head = label.split(':')[0].replace(/\s*\([^)]*\)\s*$/, '').trim();
  if (!stripNames.has(head)) {
    return `${where} offers ${JSON.stringify(label)}, and there is no place called ${JSON.stringify(head)} on the strip`;
  }
  if (TAB_HOSTS[key]) {
    const hostName = stripLabelOf(TAB_HOSTS[key]);
    if (!label.startsWith(`${hostName}: `)) {
      return `${where} offers ${JSON.stringify(label)} for ${key}, which is a tab inside ${hostName}: say so, as "${hostName}: <the tab>"`;
    }
    return null;
  }
  if (head !== (stripLabelOf(key) || '').replace(/\s*\([^)]*\)\s*$/, '').trim()) {
    return `${where} calls ${key} ${JSON.stringify(label)}, and the strip calls it ${JSON.stringify(stripLabelOf(key))}`;
  }
  return null;
};

const pickerBlock = (extension.match(/const tabs: Array<vscode\.QuickPickItem & \{ key: string \}> = \[[\s\S]*?\];/) || [''])[0];
assert.ok(pickerBlock, 'the host tool picker must still be one readable literal');
const pickerEntries = [...pickerBlock.matchAll(/\{ label: '([^']+)', key: '([a-z]+)' \}/g)].map((m) => [m[2], m[1]]);
assert.ok(pickerEntries.length >= 20, `the host picker should offer every place, found ${pickerEntries.length}`);
const shortcutEntries = [...((shortcuts.match(/const KEY_TABS[\s\S]*?\n\];/) || [''])[0])
  .matchAll(/\{ key: '[a-z]', tab: '([a-z]+)', label: '([^']+)' \}/g)].map((m) => [m[1], m[2]]);
assert.ok(shortcutEntries.length >= 8, `the shortcut overlay should list its tab keys, found ${shortcutEntries.length}`);

const misnamed = [
  ...pickerEntries.map(([key, label]) => namesARealPlace('the host tool picker', key, label)),
  ...shortcutEntries.map(([key, label]) => namesARealPlace('the shortcut overlay', key, label)),
].filter(Boolean);
assert.deepEqual(misnamed, [], 'a list outside Studio names a place the strip does not have');

// And every id those lists hand back must still be a route the shell accepts, or the pick does nothing.
// The routes the shell accepts are route.ts's landing table since slice 11: every tool DESTINATION_OF_TOOL names,
// plus the retired ids it still lands (test/retired-routes.test.mjs executes it; this reads the two lists off it).
const destinationBlock = (route.match(/export const DESTINATION_OF_TOOL[\s\S]*?\n\};/) || [''])[0];
const retiredBlock = (route.match(/const RETIRED_TOOLS[\s\S]*?\n\};/) || [''])[0];
const validTabs = new Set([...destinationBlock.matchAll(/(\w+): '/g), ...retiredBlock.matchAll(/^\s+(\w+): \{/gm)].map((m) => m[1]));
assert.ok(validTabs.has('deploy') && validTabs.has('history') && validTabs.has('permissions'), `the accepted ids were read (${[...validTabs]})`);
const dead = [...pickerEntries, ...shortcutEntries].map(([key]) => key).filter((key) => !validTabs.has(key));
assert.deepEqual([...new Set(dead)], [], 'an outside list hands the shell an id it does not accept');

console.log('intent navigation tests passed');
