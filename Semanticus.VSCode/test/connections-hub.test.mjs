// The Connections HUB contract (TASKS.md [T160]/[T161]/[T164]). The hub REPLACED the drawer as the single manager
// surface, then the simplification round (T164) made it ONE shared floating dialog for both doors, a one-verb model
// row, and a self-explaining three-outcome detail. These static assertions pin that ratified structure so a regression
// is caught without a running VS Code: the hub sections, the only-typed-endpoint rule, the shared floating frame, the
// one-verb row with its slimmed overflow, the three-outcome detail with visible microcopy, and the honest Phase 2
// account wording (a real per-open choice + an explicit, quantified make-default).
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(root, ...p), 'utf8');
const hub = read('webview', 'src', 'connectionshub.tsx');
const app = read('webview', 'src', 'App.tsx');
const main = read('webview', 'src', 'main.tsx');
const bridge = read('webview', 'src', 'bridge.ts');
const connection = read('webview', 'src', 'connection.tsx');
const extension = read('src', 'extension.ts');
const manifest = JSON.parse(read('package.json'));
const count = (haystack, needle) => haystack.split(needle).length - 1;
const slice = (from, to) => hub.slice(hub.indexOf(from), hub.indexOf(to));

// ---- the drawer is gone; the hub is the ONE manager -----------------------------------------------------------
assert.ok(!existsSync(resolve(root, 'webview', 'src', 'connectionsdrawer.tsx')),
  'the drawer must be retired: the hub is the single manager surface, not a second manager');
assert.match(app, /<ConnectionsHub open=\{connectionsOpen\} onClose=\{closeConnections\} \/>/,
  'App must mount the hub on the shared connectionsOpen channel (so the footer, ConnectBar and every publish path open it)');
assert.doesNotMatch(app, /ConnectionsDrawer/, 'App must not retain the retired drawer');

// ---- the four hub sections (sol's left nav) --------------------------------------------------------------------
assert.match(hub, /navBtn\('open',[\s\S]*?'Open a model'\)/, 'nav must offer Open a model');
assert.match(hub, /navBtn\('setup',[\s\S]*?'Current setup'\)/, 'nav must offer Current setup');
assert.match(hub, /navBtn\('accounts',[\s\S]*?'Accounts'\)/, 'nav must offer Accounts');
assert.match(hub, /navBtn\('history',[\s\S]*?'History'\)/, 'nav must offer History');
// Inner function components remount on every parent setState (D-004): typing in Add a published model kept only
// the first letter because <AddView/> was a new component type each keystroke. Views are called as functions so
// React reconciles the inputs instead of tearing them down.
for (const v of ['OpenView', 'SetupView', 'AccountsView', 'HistoryView', 'AddView', 'WorkView']) {
  assert.doesNotMatch(hub, new RegExp(`<${v}\\s*/>`), `${v} must not remount as an inner component`);
  assert.match(hub, new RegExp(`${v}\\(\\)`), `${v} is invoked as a function so typed input survives a re-render`);
}
// Current setup keeps the four explicit role cards.
for (const label of ['"Editing"', '"Tests and queries"', '"Publish to"', '"Reference model"']) {
  assert.match(hub, new RegExp(`label=${label}`), `Current setup must keep the ${label} role card`);
}

// ---- ONE shared FLOATING frame for both doors (T164 P1) -------------------------------------------------------
// A single return renders the hub as a centered floating dialog in BOTH hosting modes; only the close callback differs.
// The old split (a `standalone ? region : overlay` fork with different shells) is gone.
assert.doesNotMatch(hub, /role="region" aria-label="Connections"/,
  'the standalone hub must NOT be a bare full-bleed region: it shares the floating dialog frame');
assert.match(hub, /inset: '36px 0 23px'/, "the floating backdrop must reclaim sol's inset geometry (36px 0 23px)");
assert.match(hub, /rgba\(8,10,13,\.66\)/, 'the backdrop must dim at the ratified opacity');
assert.match(hub, /backdropFilter: 'blur\(2px\)'/, 'the backdrop must blur');
assert.match(hub, /width: 'min\(1130px, 96vw\)'/, 'the dialog must reclaim the ratified width');
assert.match(hub, /height: 'min\(790px, calc\(100vh - 84px\)\)'/, 'the dialog must reclaim the ratified height');
assert.match(hub, /if \(e\.target === e\.currentTarget\) onClose\(\)/, 'a backdrop click must dismiss');
assert.match(hub, /if \(e\.key === 'Escape'\)[\s\S]{0,80}onClose\(\)/, 'Escape must dismiss (the dialog, after any open row menu)');
assert.match(hub, /window\.addEventListener\('keydown', onKey\);\s*\n\s*return \(\) => \{ window\.removeEventListener\('keydown', onKey\); restoreRef\.current\?\.focus\(\); \};\s*\n\s*\}, \[shown, onClose\]\);/,
  'the Escape / focus-trap effect must run in BOTH modes (guarded on shown, not overlay-only)');

// ---- "Add a published model" is the ONLY place an endpoint is typed --------------------------------------------
assert.equal(count(hub, 'data-testid="hub-endpoint-input"'), 1,
  'there must be exactly one endpoint input in the whole hub');
assert.match(hub, />XMLA endpoint/, 'the single endpoint input must carry the XMLA endpoint label');
assert.equal(count(hub, 'type="search"') + count(hub, 'placeholder="powerbi'), 2,
  'the only endpoint-shaped inputs are the search box and the single Add-view endpoint field');
assert.match(hub, /remembers it so you can reopen the model without entering the address again/, 'the Add view explains that the connection address is remembered');
assert.ok(hub.indexOf('function AddView') < hub.indexOf('data-testid="hub-endpoint-input"')
  && hub.indexOf('data-testid="hub-endpoint-input"') < hub.indexOf('Connect and remember'),
  'the endpoint input must live inside the Add view');

// ---- row density: ONE primary verb (Open) + a slimmed overflow (T164 P3, Kane ratified ONE) --------------------
assert.equal(count(hub, 'data-testid="hub-row-open"'), 1, 'a model row has exactly one primary Open verb');
assert.equal(count(hub, 'data-testid="hub-row-overflow"'), 1, 'a model row has exactly one overflow trigger');
assert.equal(count(hub, 'data-testid="hub-row-tests"'), 0, 'the second row verb ("Use for tests") is gone');
assert.match(hub, /data-testid="hub-row-open"[\s\S]{0,280}>Open</,
  'the single primary verb is literally "Open" (never a stale-mutated "Sign in and open")');
// The overflow carries ONLY: choose account, view history, forget. Publish/reference/work-locally left the row.
const menu = slice('data-testid="hub-row-overflow"', 'function DetailPane');
assert.match(menu, /role="menu"/, 'the overflow opens a menu');
for (const item of ['Choose account for this open', 'View connection history', 'Forget remembered model']) {
  assert.ok(menu.includes(item), `the overflow menu must carry "${item}"`);
}
for (const gone of ['Set as publish destination', 'Use as reference', 'Work locally', 'Open local copy']) {
  assert.ok(!menu.includes(gone), `the overflow menu must NOT carry "${gone}" (it left the Open view)`);
}
// No purged testing-role copy anywhere in the hub (rows, detail, quick cards, notes).
for (const gone of ['Use for tests', 'Used for tests', 'Use for tests and queries']) {
  assert.ok(!hub.includes(gone), `no hub copy may contain "${gone}"`);
}

// ---- the detail pane earns the decision: three self-explaining outcomes + text statuses (T164 P3) --------------
assert.equal(count(hub, '<OutcomeButton'), 3, 'the detail presents exactly three opening outcomes');
assert.match(hub, /data-testid="hub-outcome"/, 'each outcome is a self-explaining OutcomeButton with visible microcopy');
assert.match(hub, /Open live[\s\S]{0,160}Edit and query the published model directly\. No local files are created\./,
  'outcome: Open live carries its exact one-line explanation');
assert.match(hub, /Query this model[\s\S]{0,160}Run queries and tests against this model\. The model you are editing stays open\./,
  'outcome: Query this model carries its exact one-line explanation');
assert.match(hub, /Work locally[\s\S]{0,160}Work on a saved copy on this computer\. Publish separately when you want to update the live model\./,
  'outcome: Work locally carries its exact one-line explanation');
for (const status of ['Currently open', 'Queries run here', 'Local copy exists', 'Sign-in required']) {
  assert.ok(hub.includes(status), `statuses are TEXT: "${status}" must be a plain status, never a mutated button label`);
}
// The environment badge stays visible; editing the label is behind an explicit control.
assert.match(hub, /Change environment label/, 'the detail must gate the env-label editor behind an explicit control');
assert.match(hub, /data-testid="hub-env-select"/, 'the env-label editor (a select) must still exist behind that control');

// ---- publish + reference LEFT the Open view; they live on Current setup role cards (T164 P3) -------------------
const setup = slice('function SetupView', 'function AccountsView');
assert.match(setup, /Set as publish destination/, 'Publish is chosen on its Current setup role card');
assert.match(setup, /setPublish/, 'the publish role card drives setPublishDestination');
assert.match(setup, /useAsReference/, 'the reference role card assigns the reference model');
// The Setup verbs note is the ratified taxonomy, not the retired "Use for tests and queries" phrasing.
assert.match(setup, /Open live[\s\S]{0,120}No local files are created/, 'the Setup note uses the Open live taxonomy');
assert.match(setup, /Query this model[\s\S]{0,120}The model you are editing stays open/, 'the Setup note uses the Query this model taxonomy');

// ---- the Open view earns its height (T164 P2) -----------------------------------------------------------------
const openView = slice('function OpenView', 'function SetupView');
assert.ok(!openView.includes('max-h-[336px]'), 'the model list must no longer be capped at 336px');
assert.ok(!openView.includes('hub-add-open'), 'the duplicate header-level "+ Add published model" button is removed (the card is the single route)');
assert.match(openView, /min-\[900px\]:flex-1 min-\[900px\]:overflow-auto/,
  'the list fills the remaining height and only scrolls when the collection exceeds the column');
assert.match(openView, /grid-cols-\[minmax\(0,1\.6fr\)_minmax\(320px,1fr\)\]/, 'the wide grid is ~62% list / 38% detail');
// (T164 fix round) "Open a new model" is a full-width strip ABOVE the remembered list (Kane: opening a new model is
// the most frequent action; sol: a top strip also relieves the old right-column overflow that cut off the cards).
assert.match(openView, /Open a new model<\/h3>/, 'the new-model sources sit under an "Open a new model" heading');
assert.match(openView, /grid-cols-2 gap-1\.5 min-\[720px\]:grid-cols-4/, 'the strip is a full-width 2-up / 4-up card row, not a cramped 2x2 in the right column');
assert.ok(openView.indexOf('Open a new model</h3>') < openView.indexOf('Recent and remembered'), 'the new-model strip sits ABOVE the remembered list');
assert.match(openView, /title="Create blank model"/, 'the create card is "Create blank model" (distinct from the section title)');
assert.match(openView, /Discover a running local model and query it\./, 'the Running desktop model card uses taxonomy-consistent copy');
assert.ok(!openView.includes('use it for tests and queries'), 'the over-promising running-model copy is gone');
// (B3) BOTH the remembered list and the detail pane own their scroll at >=900px, so a tall detail can never push the
// dialog past its fixed height (the cut-off + outer scrollbar Kane saw). "Other ways to open" left the right column.
assert.equal(count(openView, 'min-[900px]:min-h-0 min-[900px]:flex-1 min-[900px]:overflow-auto'), 2,
  'the list AND the detail each own their scroll at >=900px (neither overflows the dialog)');
assert.ok(!openView.includes('Other ways to open'), 'the old "Other ways to open" right-column group is gone (it is the top strip now)');

// ---- honest identity + fail-closed governance -----------------------------------------------------------------
assert.match(hub, /as \$\{account\} \(was \$\{was\}\)/, 'per-row identity must show "as bob@ (was alice@)" provenance');
assert.match(hub, /account unknown/, 'an unknown next-open account must read honestly as "account unknown"');
assert.match(hub, /text: 'Production safeguards'/, 'an unlabelled cloud model must read "Production safeguards", never "prod"');

// ---- honest friendly naming for local running models (T164 P5) ------------------------------------------------
assert.match(hub, /isGuidName/, 'the hub must detect a bare-GUID local database name');
assert.match(hub, /'Local running model'/, 'a local model with no friendly name falls back to "Local running model", never a GUID');
assert.match(extension, /const localDisplayName/, 'the quick pick must apply the same friendly local naming');
assert.match(extension, /isGuidName\(r\.database\)/, 'the friendly name must never surface a bare GUID');

// ---- Phase 2 accounts: a REAL per-open choice from saved profiles; make-default is explicit + quantified ---------
assert.match(hub, /data-testid="hub-account-dialog"/, 'the shared Phase 2 account dialog must render');
assert.match(hub, />This choice applies to this open\./,
  'the account dialog must promise (in rendered text) the choice applies to THIS open only (per-open, not tenant-wide)');
assert.match(hub, /data-testid="hub-use-for-open"[\s\S]{0,300}>Use for this open</,
  'Phase 2 must render a real per-open "Use for this open" control on the saved profiles');
assert.match(hub, /data-testid="hub-sign-in-another"[\s\S]{0,800}>Sign in another account</,
  'the dialog must render "Sign in another account" (adds a saved profile via the real Microsoft picker)');
assert.match(hub, /The saved endpoint stays the same whichever account you pick/,
  'the dialog must promise the saved endpoint is unchanged whichever account is picked');
assert.match(hub, /would open with it from now on/,
  'make-default must quantify its blast radius (N remembered models on the tenant would open with it)');
assert.match(hub, /data-testid="hub-make-default"[\s\S]{0,220}>Make default</,
  'the dialog must render an explicit "Make default" control, distinct from the per-open choice');
assert.match(hub, /Make an account the default instead/,
  'the make-default affordance states its blast radius (distinct from the per-open choice)');
// The per-open account choice is reached from the row overflow + the detail account note.
assert.match(hub, /Choose account for this open/, 'the per-open account choice keeps its exact ratified label');

// ---- every existing capability still reaches an existing engine op (dual-drive intact) -------------------------
for (const op of ['listConnections', 'probeConnectionAccounts', 'listConnectionHistory', 'prepareWorkingCopy',
  'setPublishDestination', 'labelConnection', 'forgetConnection', "'openLive'", "'openLocal'",
  'listAccountProfiles', 'setDefaultAccountProfile']) {
  assert.ok(hub.includes(op), `the hub must drive the engine op ${op}`);
}
assert.match(hub, /useAsReference/, 'Use as reference must remain reachable');

// ---- the standalone door: ONE bundle, hosted full-page (as the shared frame) when Studio is closed --------------
assert.match(main, /window as unknown as \{ __semanticusInitialView\?: string; __semanticusInitialSection\?: string \}/, 'main must read the initial-view + initial-section flags');
assert.match(main, /initialView === 'connections' \? <ConnectionsStandalone initialSection=\{initialSection\} \/> : <App \/>/,
  'the same bundle must mount only the hub (with its initial section) when the host asks for the connections view');
assert.match(hub, /export function ConnectionsStandalone/, 'the hub must export the standalone host');
assert.match(hub, /<ConnectionsHub open standalone initialView=\{initialSection\}/, 'the standalone host must render the SAME hub component (with its section)');
assert.match(bridge, /export function closeConnectionsPanel[\s\S]*type: 'closePanel'/,
  'the standalone close button must ask the host to dispose the panel');
assert.match(bridge, /onOpenConnections\(fn: \(section\?: string\) => void\)/, 'the open-connections channel must carry an optional section');

// ---- extension host: reuse the studio bundle with the view + section flags; branch on whether Studio is open ----
assert.match(extension, /function studioHtml\(webview: vscode\.Webview, context: vscode\.ExtensionContext, initialView\?: string, section\?: string\)/,
  'studioHtml must accept an initial-view flag and an initial-section flag');
assert.match(extension, /window\.__semanticusInitialView=/, 'studioHtml must inject the initial-view flag');
assert.match(extension, /window\.__semanticusInitialSection=/, 'studioHtml must inject the initial-section flag');
assert.match(extension, /function openConnectionsPanel\(context: vscode\.ExtensionContext, section: string = 'open'\)/, 'the standalone panel host must accept a section');
assert.match(extension, /studioHtml\(panel\.webview, context, 'connections', section\)/, 'the standalone panel must reuse the studio bundle in connections mode with its section');
assert.match(extension, /panel\.webview\.onDidReceiveMessage\(studioRelayHandler\(panel, true\)\)/, 'the standalone panel must register the shared host relay');
assert.match(extension, /if \(studioPanel\) \{[\s\S]*flushOpenConnections\(\);[\s\S]*return;[\s\S]*\}\s*openConnectionsPanel\(extCtx, section\);/,
  'openConnectionsManager must open the overlay when Studio is up and the standalone panel (with the section) when it is closed');
assert.match(extension, /msg\?\.type === 'closePanel'\) \{ panel\.dispose\(\)/, 'the relay must dispose a panel on closePanel');

// ---- T164 P4: the tree Open Model opens the hub; the native picker survives as Quick Open Model -----------------
assert.match(extension, /registerCommand\('semanticus\.openModel', \(\) => openConnectionsManager\('open'\)\)/,
  'the tree primary Open Model must now open the shared floating hub on Open a model');
assert.match(extension, /registerCommand\('semanticus\.quickOpenModel', \(\) => quickOpenModelCommand\(tree\)\)/,
  'the old native picker survives as the separately named Quick Open Model');
assert.match(extension, /registerCommand\('semanticus\.manageConnections', \(\) => openConnectionsManager\('setup'\)\)/,
  'Manage Connections opens the hub on Current setup');
assert.ok(manifest.contributes.commands.some((c) => c.command === 'semanticus.quickOpenModel' && c.title === 'Semanticus: Find a remembered model'),
  'the remembered-model picker is a separately named palette command that does not collide with Open Model');
assert.ok(manifest.contributes.commands.some((c) => c.command === 'semanticus.manageConnections' && c.title === 'Semanticus: Manage Connections' && c.icon === '$(gear)'),
  'Manage Connections is a contributed palette command with a gear icon');
assert.ok(manifest.contributes.menus['view/title'].some((m) => m.command === 'semanticus.manageConnections' && m.when === 'view == semanticusModel'),
  'a gear opens the hub from the Semanticus tree view title bar');
// The two redundant activation events VS Code auto-generates are removed (Kane's Problems panel).
assert.ok(!(manifest.activationEvents ?? []).includes('onView:semanticusModel'), 'the redundant onView activation event is removed');
assert.ok(!(manifest.activationEvents ?? []).includes('onCommand:semanticus.openModel'), 'the redundant onCommand activation event is removed');

// ---- T164 P4: the Quick Open picker converges on taxonomy + drops the pasted-endpoint interpretation ------------
const picker = extension.slice(extension.indexOf('async function quickOpenModelCommand'), extension.indexOf('async function finishOpen'));
assert.match(picker, /tooltip: 'Query this model'/, 'the picker offers an inline "Query this model" action');
assert.match(picker, /tooltip: 'Choose account for this open'/, 'the picker offers an inline "Choose account for this open" action');
assert.match(picker, /placeholder = 'Search remembered models'/, 'the picker no longer invites pasting an endpoint or path');
assert.match(picker, /pick\.id === 'newXmla'\) \{ await openConnectionsManager\('add'\)/, '"Add published model" routes to the hub Add view');
assert.ok(!extension.includes('openFromPasted'), 'the pasted-endpoint interpretation is removed (endpoints live only in Add)');
assert.ok(!extension.includes('async function openFromXmla'), 'the native typed-endpoint form is retired (Add owns typed endpoints)');

// ---- B1: the "Create new model" quick card is GUARDED (name + unsaved-changes confirmation) --------------------
assert.doesNotMatch(hub, /createModel', 'New model'/, 'the create card must NOT hardcode a model name');
assert.match(hub, /data-testid="hub-create-name"/, 'the create surface must prompt for a model name');
assert.match(hub, /rpc<SessionInfo>\('sessionInfo'\)/, 'the guard must re-read the live session at submit time');
assert.match(hub, /hasUnsavedChanges && !createConfirm/, 'unsaved changes must require an explicit second consent');
assert.match(hub, /replaces the open model, which has unsaved changes/, 'the consequence must be stated plainly');
assert.match(hub, /Create and replace open model/, 'the confirm verb must name the destructive replace');
assert.match(hub, /openConfirm/, 'opening another model must share the unsaved-work confirm');
assert.match(hub, /Opening another model throws those changes away unless you save first/, 'the open confirm must say unsaved work would be thrown away');
assert.match(hub, /data-testid="hub-open-unsaved"/, 'the open confirm is a visible overlay');
assert.match(hub, /createModel', name, 1604, !!s\?\.hasUnsavedChanges/, 'create after confirm must tell the engine to discard unsaved work');
assert.match(hub, /const openCreate = \(\)/, 'the card opens the guarded surface, it does not create directly');

// ---- the Accounts view lists the saved profiles (no dead-end); the unlabelled env option never truncates --------
assert.match(hub, /Saved accounts on this device/, 'the Accounts view must list the saved profiles (no dead-end)');
assert.doesNotMatch(hub, /No switchable live model/, 'the "No switchable live model" dead-end must be retired');
assert.match(hub, /Not labelled \(Production safeguards\)/, 'the unlabelled env option must be short enough not to truncate');

// ---- connection-relevant notifications broadcast to BOTH panels (the standalone hub is not deaf) ----------------
assert.match(extension, /function postToPanels\(msg: unknown\)/, 'a multi-panel broadcast helper must exist');
assert.match(extension, /for \(const p of \[studioPanel, connectionsPanel\]\)/, 'it must fan out to the Studio AND the standalone Connections panel');
assert.match(extension, /postToPanels\(\{ type: 'didChange'/, 'model/didChange must reach both panels');
assert.match(extension, /postToPanels\(\{ type: 'connectionChanged' \}\)/, 'connectionChanged must reach both panels');
assert.match(extension, /postToPanels\(\{ type: 'reconnected' \}\)/, 'reconnected must reach both panels (settles in-flight requests too)');
assert.doesNotMatch(extension, /studioPanel\?\.webview\.postMessage\(\{ type: '(didChange|reconnected)'/, 'no connection-relevant notification may target only the Studio panel');

// ---- the drawer->hub comment sweep left no stale "drawer" reference --------------------------------------------
assert.doesNotMatch(extension, /drawer/i, 'extension comments must say hub/manager, not the retired drawer');

// ================================================================================================================
// T164 fix round (sol closure review) - strengthened pins
// ================================================================================================================

// (6a) the row has EXACTLY three raw <button> elements: the row selector, the one Open verb, and the overflow. Any
// second row action verb (or a resurrected "Use for tests") would push this above three.
const modelRow = slice('function ModelRow', 'function DetailPane');
assert.equal(count(modelRow, '<button'), 3, 'a model row is exactly three buttons: select + Open + overflow');

// (6b) ONE dialog shell for both doors, with NO standalone ternary inside the frame return.
assert.equal(count(hub, 'role="dialog"'), 1, 'there is exactly one dialog shell (both doors share it)');
const frame = hub.slice(hub.indexOf('<div className="fixed z-[80] grid place-items-center"'), hub.indexOf('function MenuItem'));
assert.ok(!frame.includes('standalone'), 'the shared frame return JSX must not branch on standalone (one shell everywhere)');
assert.ok(!frame.includes('fixed inset-0'), 'the retired full-bleed standalone shell is gone');

// (6d) the focus/Escape effect runs in BOTH modes (no standalone guard) and captures the opener BEFORE moving focus.
const focusEffect = hub.slice(hub.indexOf('const panelRef = useRef'), hub.indexOf('}, [shown, onClose]);') + 24);
assert.ok(!focusEffect.includes('standalone'), 'the focus/Escape effect must not guard on standalone');
assert.match(focusEffect, /restoreRef\.current = document\.activeElement[\s\S]{0,200}searchRef\.current\.focus\(\)[\s\S]{0,120}else panelRef\.current\?\.focus\(\)/,
  'the opener is captured BEFORE any hub focus moves (restore, then search-or-panel)');
assert.match(hub, /<input ref=\{searchRef\}/, 'the search box uses a plain ref (no focus-in-ref-callback)');
assert.ok(!hub.includes('searchWanted.current = false; el.focus()'), 'the focus-in-ref-callback antipattern is gone');
// onClose is a stable identity so the effect cannot retrigger on a ConnectionProvider re-render.
assert.match(connection, /const openConnections = useCallback\(\(\) => setConnectionsOpen\(true\), \[\]\)/, 'openConnections is a stable identity');
assert.match(connection, /const closeConnections = useCallback\(\(\) => setConnectionsOpen\(false\), \[\]\)/, 'closeConnections (= the hub onClose) is a stable identity');
assert.match(hub, /<ConnectionsHub open standalone initialView=\{initialSection\} onClose=\{closeConnectionsPanel\}/,
  'the standalone host passes a stable module-level onClose (no fresh arrow)');

// close contract: a successful terminal action closes in BOTH modes (the standalone-only stay-open branches are gone).
assert.ok(!hub.includes('!standalone) onClose'), 'no terminal action may stay open only in standalone (one behavior everywhere)');
assert.match(hub, /a SUCCESSFUL terminal action/, 'the close contract is stated in one comment');

// (extension) the hub open is engine-guarded, and an existing standalone panel always gets the requested section.
assert.match(extension, /async function openConnectionsManager[\s\S]{0,420}Semanticus engine not connected\./,
  'openConnectionsManager refuses when no engine is connected, before creating/revealing a panel');
assert.ok(!extension.includes("if (section !== 'open') { try { connectionsPanel.webview.postMessage"),
  'the existing-panel branch no longer drops an open request');
assert.ok(extension.includes("try { connectionsPanel.webview.postMessage({ type: 'openConnections', section }); }"),
  'an existing standalone panel always gets the requested section (including open)');

// (6c) the Quick Open picker selects solely from selectedItems[0]; a typed value is never interpreted (no qp.value).
// (picker is already sliced above in the P4 section.)
assert.ok(!picker.includes('qp.value'), 'the picker must not read qp.value (a typed endpoint/path is never interpreted)');
assert.match(picker, /const item = qp\.selectedItems\[0\]; qp\.hide\(\); done\(\{ item \}\)/, 'acceptance comes solely from the highlighted row');

// (7) the overflow menu supports keyboard navigation (focus-in on open + Arrow/Home/End); Escape closes the menu
// (menu-aware modal handler), then the dialog.
assert.match(hub, /\[data-hub-menu\] \[role="menuitem"\]'\)\?\.focus\(\)/, 'opening the overflow menu moves focus into it');
assert.match(hub, /if \(e\.key === 'Escape'\) \{ if \(menuIdRef\.current\) setMenuId\(null\); else onClose\(\); return; \}/,
  'Escape closes an open row menu first, then the dialog');
assert.match(modelRow, /e\.key === 'ArrowDown'[\s\S]{0,400}e\.key === 'End'/, 'the overflow menu handles Arrow/Home/End');

// ================================================================================================================
// T164 closure round (sol) - the three remaining seams
// ================================================================================================================

// (8a) the close contract holds for the quick cards too: a successful query-attach must route through the closing
// helper (connectRunningLocal), never a bare connect that skips onClose. Catch any direct connect in the quick cards.
const quickCards = slice('Open a new model', 'Recent and remembered');
assert.ok(!/rpc\('connectLocal'/.test(quickCards),
  'no quick card may attach via a bare rpc(connectLocal) - that skips the close contract');
assert.ok(!/\bconnectXmla\(/.test(quickCards) && !/\bconnectLocal\(/.test(quickCards),
  'a successful query-attach must route through the closing helper, never a direct connect call in the quick cards');
assert.match(quickCards, /title="Running desktop model"[\s\S]{0,180}onClick=\{\(\) => void connectRunningLocal\(\)\}/,
  'the Running desktop model card routes through the closing helper (connectRunningLocal), not a direct rpc');
// the helper mirrors queryWith: await the attach + refresh, THEN onClose (the close cannot race the refresh).
const runningLocalHelper = slice('const connectRunningLocal', 'const setPublish');
assert.match(runningLocalHelper, /const res = await connectLocal\(null\)/, 'the helper awaits the running-local attach (result carries the engine message)');
assert.match(runningLocalHelper, /await load\(\); await reloadHistory\(\); setBusyId\(null\);\s*\n\s*if \(res\.ok\) onClose\(\)/,
  'the helper closes only AFTER the attach + refresh complete (mirrors queryWith ordering; no race)');

// (8b) a host "open" request while ALREADY shown re-focuses search: the [shown, onClose] focus effect will not re-run,
// so an "open" request bumps a token and a dedicated effect keyed on it focuses searchRef when the Open view is up.
assert.match(hub, /if \(section === 'open'\) \{ searchWanted\.current = true; setOpenToken\(\(t\) => t \+ 1\); \}/,
  'a host open request bumps the open-focus token (so an already-shown hub re-focuses search)');
assert.match(hub, /if \(openToken === 0 \|\| view !== 'open'\) return;\s*\n\s*searchRef\.current\?\.focus\(\);\s*\n\s*\}, \[openToken\]\)/,
  'a dedicated effect keyed on the token focuses search when the Open view is showing (the already-shown re-request)');

// (8c) closing the overflow menu returns focus to the trigger (Escape or a selection unmounts the focused menuitem).
assert.match(hub, /const menuTriggerRef = useRef<HTMLButtonElement \| null>\(null\)/,
  'a ref remembers the overflow trigger that opened the menu');
assert.match(modelRow, /onClick=\{\(e\) => \{ const opening = menuId !== r\.id; if \(opening\) menuTriggerRef\.current = e\.currentTarget;/,
  'the overflow trigger is captured when its menu opens');
assert.match(hub, /if \(menuTriggerRef\.current\) \{ menuTriggerRef\.current\.focus\(\); menuTriggerRef\.current = null; \}/,
  'closing the overflow menu (Escape or a selection) returns focus to the trigger, never orphaning it on <body>');

// ================================================================================================================
// T164 fix round (Kane's F5 findings: Query dead, Accounts dead-end, cutoff, service-identity config) - sol reviewed
// ================================================================================================================

// (B1) Query this model carries the SAME signed-out guard as Open live, an explicit already-used state, and visible
// progress - the three ways it used to read as "nothing happens".
assert.match(hub, /function OutcomeButton\(\{ label, micro, onClick, disabled, primary, busy \}/, 'OutcomeButton supports a busy/progress state');
assert.match(hub, /\{busy \? 'Connecting\.\.\.' : micro\}/, 'a busy outcome shows visible "Connecting..." progress in the microcopy slot');
assert.match(hub, /label=\{activeQuery \? 'Already used for queries' : 'Query this model'\}/, 'Query shows an explicit "Already used for queries" state, not a silent grey');
assert.match(hub, /if \(stale\) openAccountChoice\(r, 'query'\); else void queryWith\(r\)/, 'Query routes a signed-out model to the account picker carrying QUERY intent');
assert.match(hub, /busy=\{busyId === 'query:' \+ r\.id\}/, 'Query shows progress while its connect runs');
// HIGH (sol): the account picker preserves the initiating verb; applyAccount routes by that EXPLICIT intent, never by
// re-inferring the pre-op role (a stale model is neither the editing nor the query connection, which used to fall to open).
assert.match(hub, /const openAccountChoice = \(r: ConnectionRecord, purpose: 'open' \| 'query' \| null = null\) => \{ setSwitchPurpose\(purpose\); setSwitchTarget\(r\); \}/, 'a stale account choice carries explicit Open/Query intent');
assert.match(hub, /const asQuery = switchPurpose \? switchPurpose === 'query' : isQueryRole\(r\)/, 'applyAccount routes by explicit intent when set, else by role');
assert.match(hub, /if \(stale\) openAccountChoice\(r, 'open'\); else void openModel\(r\)/, 'a stale Open (row + Open live outcome) carries OPEN intent');

// (B4 + error surfacing) the hook returns the engine's own message; the hub shows it instead of a generic line.
assert.match(connection, /export interface ConnectResult \{ ok: boolean; message\?: string \}/, 'connect returns a result carrying the engine message');
assert.match(connection, /async function connectXmla\([\s\S]{0,220}Promise<ConnectResult>/, 'connectXmla returns a ConnectResult');
assert.match(connection, /async function connectLocal\([\s\S]{0,120}Promise<ConnectResult>/, 'connectLocal returns a ConnectResult');
assert.match(hub, /res\.message \|\| 'The connection did not complete/, 'the Add flow shows the engine message (e.g. the service-principal env-var help), not just a generic line');
assert.match(hub, /res\.message \|\| 'That model could not be connected for queries/, 'Query surfaces the engine message');

// (B2) the Accounts view is never a dead end: a Sign in CTA when nothing is signed in, an actionable empty state, and
// the not-signed-in Open-view banner routes to Add (where a sign-in happens), never into the Accounts view.
assert.match(hub, /if \(signedInAccount\) setView\('accounts'\); else openAddView\(\)/, 'the not-signed-in banner routes to Add via openAddView, not the Accounts dead-end');
assert.match(hub, /const signedInAccount = currentAccount/, 'the hub draws one signed-in identity from the live session or the saved profiles, never a second guess');
assert.match(hub, /data-testid="hub-accounts-signin"[\s\S]{0,160}openAddView\(\)/, 'the Accounts view offers a primary Sign in CTA when nothing is signed in');
assert.match(hub, /data-testid="hub-accounts-empty-add"[\s\S]{0,120}openAddView\(\)/, 'the empty saved-accounts state is actionable (Add a published model)');
// the two "other authentication methods" are real buttons that open Add with the method preselected (not dead cards).
assert.match(hub, /data-testid="hub-authmethod-azcli"[\s\S]{0,200}openAddView\('azcli'\)/, 'the command-line method card opens Add with azcli preselected');
assert.match(hub, /data-testid="hub-authmethod-sp"[\s\S]{0,200}openAddView\('serviceprincipal'\)/, 'the service-identity method card opens Add with serviceprincipal preselected');
// openAddView resets the method + clears any stale tenant, so a browser Sign in never lands on a prior advanced method (MED, sol).
assert.match(hub, /const openAddView = \(mode: string = 'interactive'\) => \{ setAuthMode\(mode\); setNewTenant\(''\);[\s\S]{0,40}setView\('add'\); \}/, 'openAddView resets the sign-in method and clears the tenant');
assert.match(hub, /data-testid="hub-profile-resignin"/, 'a signed-out saved profile has a Sign in recovery route (not a dead badge)');
// (MED, sol) signed-out is checked BEFORE default in the account dialog, so a DEFAULT-but-signed-out profile still gets
// a targeted re-auth (forceReauth + loginHint), never just the badge + a default open that could sign in someone else.
assert.match(hub, /\{!p\.signedIn\s*\n\s*\? <button className=\{BTN_QUIET\} data-testid="hub-signin-and-use"[\s\S]{0,180}forceReauth: true, loginHint: p\.username/, 'a signed-out profile (default or not) gets a targeted "Sign in and use", ordered before the default badge');

// (service identity preflight) the advanced modes expose a tenant field + a live prerequisites preflight that reads
// PRESENCE + NAMES only, states the secret is never stored, and that Key Vault is not resolved in-app.
assert.match(hub, /data-testid="hub-auth-preflight"/, 'an advanced-auth prerequisites preflight renders');
assert.match(hub, /rpc<AuthPrerequisites>\('probeAuthPrerequisites', authMode, newTenant\.trim\(\) \|\| null\)/, 'the preflight probes the dual-drive engine op for what it can see');
assert.match(hub, /data-testid="hub-tenant-input"/, 'advanced modes expose a tenant field (fixes the CLI tenantId=null bug)');
assert.match(hub, /const advanced = authMode === 'azcli' \|\| authMode === 'serviceprincipal';\s*\n\s*const res = await connectXmla\(endpoint\.trim\(\), database\.trim\(\), authMode, advanced \? \(newTenant\.trim\(\) \|\| null\) : null\)/, 'the add threads the tenant ONLY for advanced modes (a browser connect never carries a hidden stale tenant)');
assert.match(hub, /Direct Key Vault references are not supported/, 'the preflight states plainly that Key Vault is not resolved in-app');
assert.match(hub, /never stores the secret/, 'the preflight states the secret is not stored by Semanticus');
assert.ok(!hub.includes('<article className="rounded-lg border p-3"'), 'the static (unclickable) auth-method article cards are gone (now buttons)');
// (MED, sol) the dialog min-height is responsive so a short viewport can't clip the frame now that panes scroll internally.
assert.match(hub, /minHeight: 'min\(590px, calc\(100vh - 84px\)\)'/, 'the dialog min-height is responsive (no short-viewport clipping)');

// ================================================================================================================
// C3.1 UAT: Add form keeps typing (D-004), local files in Recent (D-143), path on screen (D-144),
// focus after a rejected path (D-145), one Open Model command (D-162)
// ================================================================================================================

// D-004: the endpoint field is a controlled input whose parent does not remount it (pinned with the view-call
// loop above). Paste and typing both write through onChange to hub state, so a remount cannot empty the field.
assert.match(hub, /data-testid="hub-endpoint-input" value=\{endpoint\} onChange=\{\(e\) => setEndpoint\(e\.target\.value\)\}/,
  'the XMLA endpoint field is controlled by hub state, not by state that dies on remount');

// D-143: a locally opened file is a first-class remembered row. Opening it must call the file open, never a live endpoint.
assert.match(hub, /r\.kind === 'file'/, 'remembered local files have their own kind');
assert.match(hub, /else if \(r\.kind === 'file'\) await rpc\('open', r\.endpoint/,
  'opening a remembered local file uses the file open path');
assert.match(hub, /if \(r\.kind === 'localDesktop' \|\| r\.kind === 'file' \|\| r\.label === 'local'\)/,
  'a local file row reads as Local, never as a published model');
assert.match(extension, /record\.kind === 'file'/, 'the native picker can reopen a remembered local file');

// D-144: the open file or folder is named on screen, not only as a hover title.
assert.match(hub, /data-testid="hub-open-now-path"/, 'Connections names the open file or folder');
assert.match(extension, /sourceTail\(info\.source\)/, 'the status bar includes the open file or folder');
assert.match(extension, /if \(info\.source\) tip\.push\(info\.source\)[\s\S]{0,220}status\.tooltip/,
  'the status bar tooltip carries the full source path');
assert.match(connection, /session\?\.source/, 'the connect bar names the open file or folder');

// D-145: a rejected typed path keeps the dialog and returns keyboard focus to the path field.
assert.match(hub, /data-testid="hub-local-path"/, 'Open a model has a typed-path field');
assert.match(hub, /Please enter a path that exists\./, 'a missing path is rejected in those exact words');
assert.match(hub, /localPathRef\.current\?\.focus\(\)/, 'a rejected path returns focus to the path field');
assert.match(extension, /validateInput: \(v\) =>[\s\S]{0,320}Please enter a path that exists\./,
  'the host typed-path prompt stays open and focused with that rejection');

// D-162: Semanticus: Open Model always opens the hub. The picker is a different command whose title does not
// contain "Open Model", so the palette cannot land the keyboard route on a remembered live entry.
assert.match(extension, /registerCommand\('semanticus\.openModel', \(\) => openConnectionsManager\('open'\)\)/,
  'Open Model always opens the Connections hub');
assert.equal(count(extension, "registerCommand('semanticus.openModel'"), 1, 'Open Model is registered once');
assert.ok(!manifest.contributes.commands.some((c) => c.command === 'semanticus.openModel' && /quick/i.test(c.title)),
  'Open Model is not the quick picker');
const openModelTitle = manifest.contributes.commands.find((c) => c.command === 'semanticus.openModel')?.title || '';
const findTitle = manifest.contributes.commands.find((c) => c.command === 'semanticus.quickOpenModel')?.title || '';
assert.match(openModelTitle, /Open Model/);
assert.ok(!/Open Model/i.test(findTitle), 'the remembered-model picker title must not contain Open Model');

// D-143 follow-through: a remembered local file cannot answer queries, so Work locally must not offer it
// as a test-and-query target (and must never label it 'published').
const workView = slice('function WorkView', 'const navBtn');
assert.match(workView, /records\.filter\(\(r\) => r\.kind !== 'file'\)/,
  'Work locally does not offer a local file as a query target');

// Dual-drive: list_connections is the same remembered list, including local files.
const mcpTools = readFileSync(resolve(root, '..', 'Semanticus.Engine', 'McpTools.cs'), 'utf8');
const listConnDesc = mcpTools.slice(mcpTools.indexOf('Name = "list_connections"'), mcpTools.indexOf('public static async Task<ModelConnectionRecord[]> ListConnections'));
assert.match(listConnDesc, /local files or projects/, 'list_connections names remembered local files');
assert.doesNotMatch(listConnDesc, /they are not live-connection records/,
  'list_connections must not deny that local files are remembered');

// ================================================================================================================
// D-177: "Change publish destination" must land the hub on Current setup (publish is chosen there), never the
// default Open-a-model view where a row offers no publish action.
// ================================================================================================================
const deploySrc = read('webview', 'src', 'deploy.tsx');
assert.match(hub, /export function openConnectionsOnView/, 'the hub must export a request-open-on-view setter');
assert.match(hub, /if \(!shown \|\| !nextView\) return;/, 'the hub must consume the pending view only once shown');
assert.match(hub, /nextView = null;/, 'the hub must clear the pending view after consuming it');
assert.match(deploySrc, /onClick=\{\(\) => \{ openConnectionsOnView\('setup'\); openConnections\(\); \}\}/,
  'Change publish destination must request Current setup, then open the hub');

console.log('Connections hub contract passed');
