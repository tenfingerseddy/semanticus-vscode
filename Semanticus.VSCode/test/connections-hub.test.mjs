// The Connections HUB contract (TASKS.md [T160]/[T161]/[T164]; slice C lane C2, 24 Sep 2026). The hub REPLACED the
// drawer as the single manager surface, the simplification round (T164) made it ONE shared floating dialog for both
// doors, and slice C made it ONE page: one list of saved models, one account button, and the Now strip with its
// Change panel. These static assertions pin what still holds across all three: the shared floating frame, the
// one-verb row, the typed address kept to one form, the honest Phase 2 account wording (a real per-open choice and
// an explicit, counted make-default), the naming helpers, and the extension host. The one-page rules themselves are
// run in connections-one-page.test.mjs.
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
const harness = read('tools', 'uishot', 'harness.html');
const shot = read('tools', 'uishot', 'shot.mjs');
const manifest = JSON.parse(read('package.json'));
const count = (haystack, needle) => haystack.split(needle).length - 1;
const slice = (from, to) => hub.slice(hub.indexOf(from), hub.indexOf(to));

// ---- the drawer is gone; the hub is the ONE manager -----------------------------------------------------------
assert.ok(!existsSync(resolve(root, 'webview', 'src', 'connectionsdrawer.tsx')),
  'the drawer must be retired: the hub is the single manager surface, not a second manager');
assert.match(app, /<ConnectionsHub open=\{connectionsOpen\} onClose=\{closeConnections\} \/>/,
  'App must mount the hub on the shared connectionsOpen channel (so the footer, ConnectBar and every publish path open it)');
assert.doesNotMatch(app, /ConnectionsDrawer/, 'App must not retain the retired drawer');

// ---- one page: no section rail, and the views are called as functions (D-004) ----------------------------------
assert.doesNotMatch(hub, /navBtn\(|className="hub-nav"/, 'the five-section rail is gone: one page');
// Inner function components remount on every parent setState (D-004): typing in the address form kept only the
// first letter because <AddView/> was a new component type each keystroke. Views are called as functions so React
// reconciles the inputs instead of tearing them down.
for (const v of ['ListView', 'HistoryView', 'AddView', 'WorkView', 'JobsPanel', 'AccountMenu']) {
  assert.doesNotMatch(hub, new RegExp(`<${v}\\s*/>`), `${v} must not remount as an inner component`);
  assert.match(hub, new RegExp(`${v}\\(\\)`), `${v} is invoked as a function so typed input survives a re-render`);
}
// The three jobs keep their names. Reference left on 2026-09-14 and does not come back: the engine keeps its slot.
const hubModelSrc = read('webview', 'src', 'connectionshub-model.ts');
for (const label of ["'Editing'", "'Tests and queries'", "'Publish to'"]) {
  assert.ok(hubModelSrc.includes(`label: ${label}`), `the Change panel keeps the ${label} job`);
}
assert.doesNotMatch(hub, /Reference model/, 'no Reference job on the page');

// ---- ONE shared FLOATING frame for both doors (T164 P1) -------------------------------------------------------
// A single return renders the hub as a centered floating dialog in BOTH hosting modes; only the close callback differs.
// The old split (a `standalone ? region : overlay` fork with different shells) is gone.
assert.doesNotMatch(hub, /role="region" aria-label="Connections"/,
  'the standalone hub must NOT be a bare full-bleed region: it shares the floating dialog frame');
assert.match(hub, /inset: '36px 0 23px'/, "the floating backdrop must reclaim sol's inset geometry (36px 0 23px)");
assert.match(hub, /rgba\(8,10,13,\.66\)/, 'the backdrop must dim at the ratified opacity');
assert.match(hub, /backdropFilter: 'blur\(2px\)'/, 'the backdrop must blur');
// The space rule (Kane, 27 September 2026) replaced the fixed 1130 x 790 frame: it is sized from the window.
assert.match(hub, /width: 'min\(1760px, 92vw\)'/, 'the dialog grows with the window, up to a wide limit');
assert.match(hub, /height: 'calc\(100vh - 84px\)'/, 'the dialog takes the full height less a margin');
assert.match(hub, /if \(e\.target === e\.currentTarget\) onClose\(\)/, 'a backdrop click must dismiss');
// CHANGED 2026-09-15 (Astra, finding 2): Escape still dismisses the hub, but ONLY while the hub is the
// topmost surface. The hub opens over the Tests drawer, and both used to close on the same key, throwing
// away a half-finished check to save a SQL source for it. surface-stack.ts owns the rule; its behaviour is
// run in tests-fixes.test.mjs, and the two lines below pin that this file takes part in it.
assert.match(hub, /useSurfaceEscape\(\(\) => \{ if \(menuIdRef\.current\) setMenuId\(null\); else if \(panelOpenRef\.current\) \{ setPanel\('none'\); setJobPick\(null\); \} else onClose\(\); \}, shown\);/,
  'Escape must dismiss (an open row menu, then an open panel, then the dialog), and only while the hub is the surface on top');
assert.match(hub, /window\.addEventListener\('keydown', onKey\);\s*\n\s*return \(\) => \{ window\.removeEventListener\('keydown', onKey\); restoreRef\.current\?\.focus\(\); \};\s*\n\s*\}, \[shown, onClose\]\);/,
  'the focus-trap effect must run in BOTH modes (guarded on shown, not overlay-only)');

// ---- "Add a published model" is the ONLY place an endpoint is typed --------------------------------------------
assert.equal(count(hub, 'data-testid="hub-endpoint-input"'), 1,
  'there must be exactly one endpoint input in the whole hub');
assert.match(hub, />Workspace address/, 'the single address input says what it is in plain words');
assert.equal(count(hub, 'type="search"') + count(hub, 'placeholder="powerbi'), 2,
  'the only endpoint-shaped inputs are the search box and the single Add-view endpoint field');
assert.match(hub, /For a model the workspace list cannot show\./, 'the Add view says in one line what it is for');
assert.match(hub, />Connect and remember</, 'and the button still says the address is remembered');
assert.ok(hub.indexOf('function AddView') < hub.indexOf('data-testid="hub-endpoint-input"')
  && hub.indexOf('data-testid="hub-endpoint-input"') < hub.indexOf('Connect and remember'),
  'the endpoint input must live inside the Add view');

// ---- row density: ONE primary verb (Open) and the ⋯ menu that holds everything else (design, Kane 24 Sep) --------
assert.equal(count(hub, 'data-testid="hub-row-open"'), 1, 'a model row has exactly one primary Open verb');
assert.equal(count(hub, 'data-testid="hub-row-overflow"'), 1, 'a model row has exactly one overflow trigger');
assert.equal(count(hub, 'data-testid="hub-row-tests"'), 0, 'there is no second row verb');
assert.match(hub, /data-testid="hub-row-open"[\s\S]{0,280}>Open</,
  'the single primary verb is literally "Open" (never a stale-mutated "Sign in and open")');
const menu = slice('role="menu" data-hub-menu', '{envEditId === r.id');
for (const item of ['Use for tests and queries', 'Publish here', 'Work on a local copy', 'Add SQL endpoint', 'Open as another account', 'Environment label', 'History', 'Forget']) {
  assert.ok(menu.includes(item), `the row menu must carry "${item}"`);
}
assert.doesNotMatch(menu, /Use as reference/, 'the row menu does not set a reference model');
// The copy rules keep "tests" to its role sense, so the job reads "tests and queries" everywhere on the page.
assert.ok(!/>Use for tests</.test(hub) && !/'Use for tests'/.test(hub), 'no hub copy may say "Use for tests" on its own');
// A job the model cannot do stays in the menu, greyed, with the engine's reason.
assert.match(menu, /disabled=\{!!testsWhy\} why=\{testsWhy\}/, 'Use for tests and queries is greyed with the engine reason');
assert.match(menu, /disabled=\{!!publishWhy\} why=\{publishWhy\}/, 'Publish here is greyed with the engine reason');
// The environment label is set from the row menu, behind an explicit item, and only by a person.
assert.match(hub, /data-testid="hub-env-select"/, 'the env-label editor (a select) still exists, behind the Environment label item');

// ---- the page: three ways to add, one list, the not-linked sources -------------------------------------------------
const listView = slice('function ListView', 'function HistoryView');
assert.match(listView, /data-testid="hub-add-file" onClick=\{\(\) => openLocalModel\(\)\}/, 'Open file or folder uses the native picker');
assert.match(listView, /data-testid="hub-add-desktop"[\s\S]{0,120}setView\('desktop'\)/, 'Power BI Desktop opens the instance picker');
assert.match(listView, /data-testid="hub-add-workspace"[\s\S]{0,120}setView\('workspace'\)/, 'Workspace model opens the workspace then model picker');
assert.match(listView, /data-testid="hub-add-blank" onClick=\{\(\) => openCreate\(\)\}/, 'New blank model is a quiet link to the guarded create');
// Since the M11 wiring step From a SQL source opens the start plan's own form, with no blank-model name step first
// (test/startplan-wiring.test.mjs holds the rest).
assert.match(listView, /data-testid="hub-add-sql" onClick=\{\(\) => openStartFromSql\(\)\}/, 'From a SQL source opens the start form');
assert.match(listView, /records\.length >= SEARCH_FROM && <input ref=\{searchRef\}/, 'the search shows once the list is long enough to need it');
assert.match(listView, /Open your first model/, 'the first visit shows one sentence and the three ways in');
assert.doesNotMatch(listView, /max-h-\[336px\]/, 'the list is never capped at a fixed height');

// ---- honest identity + fail-closed governance -----------------------------------------------------------------
assert.match(hub, /as \$\{account\} \(was \$\{was\}\)/, 'per-row identity must show "as bob@ (was alice@)" provenance');
assert.match(hub, /account unknown/, 'an unknown next-open account must read honestly as "account unknown"');
assert.match(hub, /text: 'No label \(Development settings\)'/, 'an unlabelled cloud model must read "No label (Development settings)", never "Production"');
assert.match(hub, /if \(l\) return \{ text: 'Production', tone: 'warn' \}/, 'a hand-edited label the engine does not know ("Production", "staging") must read Production, because the engine applies prod to it');

// ---- honest friendly naming for local running models (T164 P5) ------------------------------------------------
assert.match(hub, /isGuidName/, 'the hub must detect a bare-GUID local database name');
assert.match(hub, /'Local running model'/, 'a local model with no friendly name falls back to "Local running model", never a GUID');
assert.match(extension, /const localDisplayName/, 'the quick pick must apply the same friendly local naming');
assert.match(extension, /isGuidName\(r\.database\)/, 'the friendly name must never surface a bare GUID');

// ---- Phase 2 accounts: a REAL per-open choice from saved profiles; make-default is explicit + quantified ---------
assert.match(hub, /data-testid="hub-account-dialog"/, 'the shared Phase 2 account dialog must render');
assert.match(hub, /data-purpose=\{purpose\}/, 'the account dialog must retain whether the user is opening or querying');
assert.match(hub, /For this \{purpose\} only\./,
  'the account dialog must retain the open/query purpose');
assert.doesNotMatch(hub, /data-testid="hub-use-for-open"/, 'no per-row Use button: a row is selected and the one footer button acts on it (C6)');
assert.match(hub, /`Use \$\{picked\.username\} for this \$\{purpose\}`/, 'the footer button names the chosen account and the purpose');
assert.match(hub, /data-testid="hub-sign-in-another"[\s\S]{0,800}>Sign in another account</,
  'the dialog must render "Sign in another account" (adds a saved profile via the real Microsoft picker)');
assert.match(hub, /data-testid="hub-signin-and-use"[\s\S]{0,260}>Sign in and use</,
  'a signed-out remembered identity must keep an explicit Sign in and use action');
assert.match(hub, /data-testid="hub-sign-in-again"[\s\S]{0,260}>Sign in again</,
  'an auth-failed profile must keep an explicit attempt-local sign-in action');
assert.match(hub, /markFailedAuthAttempt = \(r: ConnectionRecord, purpose: 'open' \| 'query', profileId\?: string, loginHint\?: string\)/,
  'a failed forced sign-in must retain the hinted profile identity when marking attempt-local recovery');
assert.match(hub, /p\.username\.toLowerCase\(\) === loginHint\.toLowerCase\(\)/,
  'attempt-local recovery matches a login hint to the saved profile without changing its global signed-in state');
assert.match(hub, /Which account should \{nameOf\(r\)\} use for \{role\}\?/, 'the account dialog title names the model and the job (one line, design)');
assert.match(hub, /const role = purpose === 'query' \? 'tests and queries' : 'opening'/,
  'the account dialog must distinguish the query role from editing');
assert.match(hub, /For this \{purpose\} only\./,
  'the dialog says the choice moves nothing else');
assert.match(hub, /data-testid="hub-account-recovery"/, 'a failed XMLA action must explain the saved-account recovery state');
assert.match(hubModelSrc, /will open with it from now on/,
  'make-default must quantify its blast radius (N saved models on the tenant will open with it) before it moves anything');
assert.match(hub, /data-testid="hub-make-default"[\s\S]{0,220}>Make default</,
  'the dialog must render an explicit "Make default" control, distinct from the per-open choice');
assert.match(hub, /\{panel !== 'account' && defaultConfirm\}/, 'a Make default pressed in the account choice shows the same counted confirmation');
// The per-open account choice is one row-menu item (design: "one menu item instead of three buttons").
assert.equal(count(hub, '>Open as another account<'), 1, 'the per-open account choice is one menu item');

// XMLA failures from either direct remembered action return to the existing account chooser. The test seam below
// drives the real hub, fails Query once, and checks the retry args + unchanged default/session in shot.mjs.
assert.match(hub, /const recoverAccountChoice = \(r: ConnectionRecord, purpose: 'open' \| 'query'\) =>/, 'recovery must retain target and purpose');
assert.match(hub, /if \(isSwitchableXmla\(r\) && isAuthRequiredFailure\(failure\)\) \{[\s\S]{0,180}recoverAccountChoice\(r, purpose\)/, 'failed remembered Open must return to account choice only for switchable XMLA auth failures');
assert.match(hub, /setError\(failure\);[\s\S]{0,180}recoverAccountChoice\(r, 'query'\)/, 'failed remembered Query must return to account choice');
assert.match(harness, /Authentication failed for all authenticators\. Technical Details: RootActivityId:/, 'the deterministic harness must use the real RPC auth-error message shape');
assert.match(harness, /window\.__uishotRecovery\.connectCalls\.push/, 'the harness must record RPC attempts');
assert.match(hub, /const isAuthRequiredFailure = \(message\?: string \| null\): boolean =>/, 'recovery must classify the RPC message instead of assuming an exception type');
assert.match(hub, /isSwitchableXmla\(r\) && isAuthRequiredFailure\(failure\)/, 'only a switchable XMLA auth failure may open account recovery');
assert.match(shot, /hub === 'recovery'/, 'the headless harness must exercise the real failure/retry UI');
assert.match(shot, /recovered\.connectCalls\[1\]\[6\], 'pf2'/, 'the recovery harness must prove the selected profile reaches the retry RPC');

// ---- the failure notice: one notice, a plain cause line, the engine text behind a disclosure (2026-09-14 review) ----
assert.match(hub, /function plainAuthCause\(message\?: string \| null, account\?: string\): string \| null/,
  'the notice must open with a plain-words cause built from the classified failure, not the raw engine string');
assert.match(hub, /return `The saved sign-in \$\{who\} no longer works\.`/,
  'the default classified auth cause reads as one plain sentence naming the account');
assert.match(hub, /if \(!isAuthRequiredFailure\(m\)\) return null/,
  'an unclassified failure gets no invented plain cause; it keeps the engine text as its own cause line');
assert.match(hub, /const plainCause = plainAuthCause\(error, causeAccount\)/,
  'the account dialog must build its cause line from the failure and the failed account');
assert.match(hub, /\{plainCause \|\| `The last attempt failed: \$\{error\}`\}/,
  'the cause line leads the notice and falls back to the engine text when nothing was classified');
assert.match(hub, /data-testid="hub-account-details"[\s\S]{0,220}>Show details</,
  'the raw engine text must sit behind a "Show details" disclosure');
// ONE notice: the cause, the instruction and the disclosure share a single hub-account-error container, so the
// recovery / retry instruction is no longer a second stacked block with its own border.
assert.ok(hub.indexOf('data-testid="hub-account-recovery"') > hub.indexOf('data-testid="hub-account-error"')
  && hub.indexOf('data-testid="hub-account-recovery"') < hub.indexOf('data-testid="hub-account-details"'),
  'the recovery instruction must sit INSIDE the one failure notice, between the cause line and the disclosure');
// Instrument (23 September 2026): the one notice is a flat inset, and its tone is the dot on the cause line
// (amber while a sign-in here can repair it, red when it cannot), not a coloured border.
assert.equal(count(hub, 'data-testid="hub-account-error" className="sem-inset'), 1,
  'the red cause notice and the amber instruction notice must be one notice');
assert.match(hub, /note-line \$\{authRequired \? 'note-warn' : 'note-bad'\}/, 'with one honest tone');
assert.match(hub, /sign-in needed for this connection/, 'the attempt-local row wording is kept');
assert.match(hub, /Sign in needed for this connection as \{failedProfile\.username\}/,
  'the instruction keeps the attempt-local sign-in-needed wording');

// ---- the primary button follows the SELECTED row and never silently retries an expired credential ---------------
assert.match(hub, /data-testid="hub-account-select"/, 'an account row must be selectable, since the primary follows it');
assert.match(hub, /const picked = list\.find\(\(p\) => p\.id === pickedProfileId\) \|\| failedProfile/,
  'the selection defaults to the row that just failed, so the primary names it');
assert.match(hub, /picked\.id === failedProfile\?\.id \? `Sign in again as \$\{picked\.username\}`/,
  'a selected sign-in-needed row makes the primary read "Sign in again as <account>"');
assert.match(hub, /: `Use \$\{picked\.username\} for this \$\{purpose\}`/,
  'a selected signed-in row makes the primary read "Use <account> for this open/query"');
assert.match(hub, /if \(needsSignIn\) \{ void applyAccount\(r, \{ forceReauth: true, loginHint: picked\.username \}\); return; \}/,
  'the primary repairs a sign-in-needed row with a forced, identity-pinned sign-in, never a silent retry');
assert.match(hub, /data-testid="hub-open-default" disabled=\{busy \|\| \(!picked && list\.length > 0\)\}/,
  'the primary is disabled when no usable account row is selected');
assert.match(hub, /onClick=\{runPrimary\}/, 'the primary action follows the selection, not a fixed default open');
assert.match(harness, /The local model at localhost:51000 is not ready\./, 'the harness must model an ordinary local failure');
assert.match(shot, /hub === 'local-failure'/, 'the headless harness must exercise the ordinary local failure UI');
assert.match(shot, /assert\.equal\(await page\.\$\('\[data-testid="hub-account-dialog"\]'\), null/, 'the local failure harness must prove no Microsoft account dialog appears');

// ---- every existing capability still reaches an existing engine op (dual-drive intact) -------------------------
for (const op of ['listConnections', 'probeConnectionAccounts', 'listConnectionHistory', 'prepareWorkingCopy',
  'setPublishDestination', 'labelConnection', 'forgetConnection', "'openLive'", "'openLocal'",
  'listAccountProfiles', 'setDefaultAccountProfile', 'listModelSqlEndpoints', 'setModelSqlEndpoint', 'disconnect']) {
  assert.ok(hub.includes(op), `the hub must drive the engine op ${op}`);
}
assert.doesNotMatch(hub, /useAsReference/, 'the hub offers no way to set a reference model any more');

// ---- the standalone door: ONE bundle, hosted full-page (as the shared frame) when Studio is closed --------------
assert.match(main, /window as unknown as \{ __semanticusInitialView\?: string; __semanticusInitialSection\?: string \}/, 'main must read the initial-view + initial-section flags');
assert.match(main, /initialView === 'connections' \? <ConnectionsStandalone initialSection=\{initialSection\} \/> : <App \/>/,
  'the same bundle must mount only the hub (with its initial section) when the host asks for the connections view');
assert.match(hub, /export function ConnectionsStandalone/, 'the hub must export the standalone host');
assert.match(hub, /<ConnectionsHub open standalone initialView=\{initialSection\}/, 'the standalone host must render the SAME hub component (with its section)');
// ---- Astra 4: the standalone door must be able to HONOUR the control it offers (F-207) -------------------------
//
// With Studio closed, Current setup still offered Turn on, and the card that press raises was mounted only
// inside App. The press moved a store nothing was listening to, so that door could not grant permission at
// all. A door that offers a control it cannot honour is worse than no door.
//
// WHAT THIS PROVES AND WHAT IT DOES NOT, stated rather than implied: this pins the MOUNT in the source, which
// is the half a browserless run can hold. The press itself is driven in a real browser by
// tools/uishot/consent-drive.mjs, scene conn-turn-on, which is not a CI suite. Same shape as the F-186 row:
// the order is pinned in the source, the race is not run.
assert.match(hub, /export function ConnectionsStandalone[\s\S]*<ConsentCard \/>/,
  'the standalone Connections host does not mount ConsentCard, so Turn on there raises nothing (F-207)');
assert.match(hub, /import \{ ConsentCard \} from '\.\/consent'/,
  'the standalone host cannot mount a permission card it never imported');

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
assert.match(extension, /panel\.webview\.onDidReceiveMessage\(pageScoped\(studioRelayHandler\(panel, true\)\)\)/, 'the standalone panel must register the shared host relay');
assert.match(extension, /if \(studioPanel\) \{[\s\S]*flushOpenConnections\(\);[\s\S]*return;[\s\S]*\}\s*openConnectionsPanel\(extCtx, section\);/,
  'openConnectionsManager must open the overlay when Studio is up and the standalone panel (with the section) when it is closed');
assert.match(extension, /msg\?\.type === 'closePanel'\) \{ panel\.dispose\(\)/, 'the relay must dispose a panel on closePanel');

// ---- T164 P4: the tree Open Model opens the hub; the native picker survives as Quick Open Model -----------------
assert.match(extension, /registerPersonCommand\('semanticus\.openModel', \(\) => openConnectionsManager\('open'\)\)/,
  'the tree primary Open Model must now open the shared floating hub on Open a model');
assert.match(extension, /registerPersonCommand\('semanticus\.quickOpenModel', \(\) => quickOpenModelCommand\(tree\)\)/,
  'the old native picker survives as the separately named Quick Open Model');
assert.match(extension, /registerPersonCommand\('semanticus\.manageConnections', \(\) => openConnectionsManager\('setup'\)\)/,
  'Manage Connections opens the hub on Current setup');
assert.ok(manifest.contributes.commands.some((c) => c.command === 'semanticus.quickOpenModel' && c.title === 'Semanticus: Open Recent Model…'),
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
assert.match(hub, /const openCreate = \(\) => \{ setCreateName\(''\)/, 'the card opens the guarded surface, it does not create directly');

// ---- the account menu lists the saved profiles (no dead-end); the unlabelled env option never truncates ----------
const accountMenu = slice('function AccountMenu', 'function AccountDialog');
assert.match(accountMenu, /profiles\.map\(\(p\) =>/, 'the account menu lists the saved profiles, signed in or not');
assert.match(accountMenu, /No saved accounts yet\. Pick a workspace model to sign in\./, 'an empty menu says where a sign-in happens');
assert.doesNotMatch(accountMenu, /Kept on this device/, 'the account menu does not repeat the History line (C10)');
assert.match(hub, /Models and sign-ins, kept on this device/, 'the one "kept on this device" note lives in History');
assert.doesNotMatch(hub, /No switchable live model/, 'the "No switchable live model" dead-end must be retired');
assert.match(read('webview', 'src', 'connectpickers-model.ts'), /No label \(Development settings\)/, 'the unlabelled env option must be short enough not to truncate');

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

// (6a) the row's line has EXACTLY two raw <button> elements: the one Open verb and the overflow. Any second row
// action verb would push this above two; everything else about a model sits in its menu.
const modelRow = slice('function ModelRow', 'function EndpointForm');
const rowLine = modelRow.slice(modelRow.indexOf('<div className="sem-list-row sem-list-row-2">'), modelRow.indexOf('{menuId === r.id &&'));
assert.equal(count(rowLine, '<button'), 2, 'a model row line is exactly two buttons: Open + overflow');

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
assert.match(hub, /\[data-hub-menu\] \[role="menuitem"\]:not\(:disabled\)'\)\?\.focus\(\)/, 'opening the overflow menu moves focus into its first usable item');
// CHANGED 2026-09-15 (Astra, finding 2): Escape moved out of the focus-trap effect and onto the shared
// surface hook, so the hub acts on it only while it is on top. Menu before dialog is unchanged.
assert.match(hub, /if \(menuIdRef\.current\) setMenuId\(null\); else if \(panelOpenRef\.current\)/,
  'Escape closes an open row menu first, then an open panel, then the dialog');
assert.match(modelRow, /e\.key === 'ArrowDown'[\s\S]{0,400}e\.key === 'End'/, 'the overflow menu handles Arrow/Home/End');

// ================================================================================================================
// T164 closure round (sol) - the three remaining seams
// ================================================================================================================

// (8a) no add button attaches anything by itself: Power BI Desktop and Workspace model open pickers that list, and
// every attach goes through a helper that owns the close contract. The old card took the FIRST running copy silently.
assert.ok(!/\bconnectXmla\(|\bconnectLocal\(|rpc\('connectLocal'/.test(slice('<div className="hub-adds">', '{notice &&')),
  'no add button attaches a model directly');
assert.doesNotMatch(hub, /connectRunningLocal|connectLocal\(null\)/, 'nothing takes the first running copy any more');
assert.match(slice('const useDesktopForTests', 'const openDesktop'), /const res = await connectLocal\(dataSource\);\s*\n\s*await load\(\); await reloadHistory\(\);[\s\S]{0,160}onClose\(\);/,
  'the picker attach closes only AFTER the attach + refresh complete (no race)');

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

// (B1) Use for tests and queries carries the SAME signed-out guard as Open, and says what changed when it worked.
// HIGH (sol): the account picker preserves the initiating verb; applyAccount routes by that EXPLICIT intent, never by
// re-inferring the pre-op role (a stale model is neither the editing nor the query connection, which used to fall to open).
assert.match(hub, /if \(stale\) openAccountChoice\(r, 'query'\); else void attachTests\(r\)/, 'a signed-out model goes to the account picker carrying QUERY intent');
assert.match(hub, /const openAccountChoice = \(r: ConnectionRecord, purpose: 'open' \| 'query' \| null = null\) => \{ setError\(null\);[^\n]*setSwitchPurpose\(purpose\); setSwitchTarget\(r\); \}/, 'a stale Open or tests-and-queries carries its purpose into the account choice');
assert.match(hub, /const asQuery = switchPurpose \? switchPurpose === 'query' : isQueryRole\(r\)/, 'applyAccount routes by explicit intent when set, else by role');
assert.match(hub, /if \(stale\) openAccountChoice\(r, 'open'\); else void openModel\(r\)/, 'a stale Open carries OPEN intent');
assert.match(hub, /setNotice\(`Tests and queries now run on \$\{nameOf\(r\)\}\.`\)/, 'a changed tests-and-queries model is said out loud');

// (B4 + error surfacing) the hook returns the engine's own message; the hub shows it instead of a generic line.
assert.match(connection, /export interface ConnectResult \{ ok: boolean; message\?: string \}/, 'connect returns a result carrying the engine message');
assert.match(connection, /async function connectXmla\([\s\S]{0,220}Promise<ConnectResult>/, 'connectXmla returns a ConnectResult');
assert.match(connection, /async function connectLocal\([\s\S]{0,120}Promise<ConnectResult>/, 'connectLocal returns a ConnectResult');
assert.match(hub, /res\.message \|\| 'The connection did not complete/, 'the Add flow shows the engine message (e.g. the service-principal env-var help), not just a generic line');
assert.match(hub, /res\.message \|\| 'That model could not be connected for tests and queries/, 'Use for tests and queries surfaces the engine message');

// (B2) the account menu is never a dead end: Sign in another account routes to where a sign-in happens (the live
// model's account choice, else the workspace picker), and Other ways to sign in opens the address form.
assert.match(hub, /const signedInAccount = currentAccount/, 'the hub draws one signed-in identity from the live session or the saved profiles, never a second guess');
assert.match(hub, /data-testid="hub-sign-in-another-top"[\s\S]{0,140}if \(switchable\) signInAnother\(switchable\); else setView\('workspace'\)/, 'Sign in another account always leads to a sign-in');
assert.match(hub, /data-testid="hub-other-sign-in" onClick=\{\(\) => openAddView\(\)\}/, 'Other ways to sign in opens the address form with its setup check');
// openAddView resets the method + clears any stale tenant, so a browser Sign in never lands on a prior advanced method (MED, sol).
assert.match(hub, /const openAddView = \(mode: string = 'interactive'\) => \{ setAuthMode\(mode\); setNewTenant\(''\);[\s\S]{0,40}setView\('add'\); \}/, 'openAddView resets the sign-in method and clears the tenant');
assert.match(hub, /data-testid="hub-profile-resignin"/, 'a signed-out saved profile has a Sign in recovery route (not a dead badge)');
// (MED, sol) signed-out is checked BEFORE default in the account dialog, so a DEFAULT-but-signed-out profile still gets
// a targeted re-auth (forceReauth + loginHint), never just the badge + a default open that could sign in someone else.
assert.match(hub, /: !p\.signedIn\s*\n\s*\? <button className=\{BTN_QUIET\} data-testid="hub-signin-and-use"[\s\S]{0,180}forceReauth: true, loginHint: p\.username/, 'a signed-out profile (default or not) gets a targeted "Sign in and use", ordered before the default badge');

// (service identity preflight) the advanced modes expose a tenant field + a live prerequisites preflight that reads
// PRESENCE + NAMES only, states the secret is never stored, and that Key Vault is not resolved in-app.
assert.match(hub, /data-testid="hub-auth-preflight"/, 'an advanced-auth prerequisites preflight renders');
assert.match(hub, /rpc<AuthPrerequisites>\('probeAuthPrerequisites', authMode, newTenant\.trim\(\) \|\| null\)/, 'the preflight probes the dual-drive engine op for what it can see');
assert.match(hub, /data-testid="hub-tenant-input"/, 'advanced modes expose a tenant field (fixes the CLI tenantId=null bug)');
assert.match(hub, /const advanced = authMode === 'azcli' \|\| authMode === 'serviceprincipal';\s*\n\s*const res = await connectXmla\(endpoint\.trim\(\), database\.trim\(\), authMode, advanced \? \(newTenant\.trim\(\) \|\| null\) : null\)/, 'the add threads the tenant ONLY for advanced modes (a browser connect never carries a hidden stale tenant)');
assert.match(hub, /Key Vault references are not supported here/, 'the preflight states plainly that Key Vault is not resolved in-app');
assert.match(hub, /never stores the secret/, 'the preflight states the secret is not stored by Semanticus');
assert.match(hub, /data-testid="hub-auth-names"[\s\S]{0,160}Show the variable names/, 'the environment variable names sit behind one disclosure, not in the copy (C4)');
assert.doesNotMatch(hub.slice(hub.indexOf('function AuthPreflight'), hub.indexOf('function AddView')), /sem-inset/, 'the setup check is not a box inside the form card');
assert.ok(!hub.includes('<article className="rounded-lg border p-3"'), 'the static (unclickable) auth-method article cards are gone (now buttons)');
// (MED, sol) the dialog min-height is responsive so a short viewport can't clip the frame now that panes scroll internally.
assert.doesNotMatch(hub, /minHeight: 'min\(590px/, 'the full-height frame needs no separate min-height (no short-viewport clipping)');

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
assert.match(hub, /if \(r\.kind === 'localDesktop' \|\| r\.kind === 'file' \|\| l === 'local'\)/,
  'a local file row reads as Local, never as a published model');
assert.match(extension, /record\.kind === 'file'/, 'the native picker can reopen a remembered local file');

// D-144: the open file or folder is named on screen, not only as a hover title.
assert.match(hub, /if \(r\.kind === 'file'\) return endpointText\(r\.endpoint\);/, 'Connections names the open file or folder on its row');
// r1 min4 (V1): the status bar names the model only; the open file or folder, in full, is the item's tooltip.
assert.match(readFileSync(resolve(root, 'src', 'statusBarText.ts'), 'utf8'), /if \(info\.source\) lines\.push\(sourceTail\(info\.source, 200\)\)/, 'the status bar tooltip carries the source path');
assert.match(extension, /status\.tooltip = item\.tooltip/, 'and the model item shows that tooltip');

// D-145, retired 23 September 2026 (Kane): "remove the file path box, not required because there are options above
// it to open a file or folder". Open a model no longer carries a typed-path field; Local file or project opens the
// native picker, which takes a file or a folder. The host's own typed-path prompt is a separate door and keeps
// its rejection wording.
assert.doesNotMatch(hub, /data-testid="hub-local-path"/, 'Open a model has no typed-path field any more');
assert.doesNotMatch(hub, /openTypedLocal|localPathRef|Or type a path/, 'and nothing of the typed path is left behind it');
assert.match(slice('function ListView', 'function HistoryView'), />Open file or folder</,
  'the way to a file or folder is the Open file or folder action above the list');
assert.match(extension, /validateInput: \(v\) =>[\s\S]{0,320}Please enter a path that exists\./,
  'the host typed-path prompt stays open and focused with that rejection');

// D-162: Semanticus: Open Model always opens the hub. The picker is a different command whose title does not
// contain "Open Model", so the palette cannot land the keyboard route on a remembered live entry.
assert.match(extension, /registerPersonCommand\('semanticus\.openModel', \(\) => openConnectionsManager\('open'\)\)/,
  'Open Model always opens the Connections hub');
assert.equal(count(extension, "registerPersonCommand('semanticus.openModel'"), 1, 'Open Model is registered once');
assert.ok(!manifest.contributes.commands.some((c) => c.command === 'semanticus.openModel' && /quick/i.test(c.title)),
  'Open Model is not the quick picker');
const openModelTitle = manifest.contributes.commands.find((c) => c.command === 'semanticus.openModel')?.title || '';
const findTitle = manifest.contributes.commands.find((c) => c.command === 'semanticus.quickOpenModel')?.title || '';
assert.match(openModelTitle, /Open Model/);
assert.ok(!/Open Model/i.test(findTitle), 'the remembered-model picker title must not contain Open Model');

// D-143 follow-through: a remembered local file cannot answer queries, so Work locally must not offer it
// as a test-and-query target (and must never label it 'published').
const workView = slice('function WorkView', 'const body =');
assert.match(workView, /records\.filter\(\(r\) => !r\.testsRefusal\)/,
  'Work on a local copy offers only models the engine did not refuse for tests and queries (a file is refused)');

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
assert.match(deploySrc, /const chooseDestination = \(\) => \{ openConnectionsOnView\('setup'\); openConnections\(\); \};/,
  'Change (the destination) must request the Change panel, then open the hub');
assert.match(deploySrc, /onClick=\{chooseDestination\}/, 'and the header Change link uses it');
assert.match(hubModelSrc, /case 'setup': return \{ view: 'open', panel: 'change' \};/, "'setup' lands on the Change panel, where Publish to is chosen");

// ================================================================================================================
// A remembered connection reads as WORDS, never as an address (Kane, live test on the Yoga, 2026-09-15). His list
// titled a published model "Contoso%20Fabric%20Monitoring" (Contoso stands in for the real tenant name, which the
// public-mirror gate refuses to carry): a WORKSPACE-level record with no dataset, so the old
// nameOf fell through to short(r.endpoint) — the encoded last path segment — in the row, the detail title, the
// Model row and the Open now panel, with the endpoint printed raw underneath. Behavioral, per the
// bridge-timeouts / lineage-actions extract-and-execute pattern: the display helpers stay top-level pure
// functions in connectionshub.tsx so they remain statically extractable AND runnable here.
// ================================================================================================================
const { default: tsc } = await import('typescript');
const HELPERS = ['short', 'isGuidName', 'decodeSegment', 'workspaceNameFromEndpoint', 'isWorkspaceOnly', 'nameOf', 'whereOf', 'endpointText'];
const helperSource = HELPERS.map((name) => {
  const m = hub.match(new RegExp(`\\nfunction ${name}\\([\\s\\S]*?\\n\\}`));
  assert.ok(m, `${name} must stay a statically extractable top-level function in connectionshub.tsx`);
  return m[0];
}).join('\n');
const helperJs = tsc.transpileModule(helperSource, { compilerOptions: { target: tsc.ScriptTarget.ES2020 } }).outputText;
const H = Function(`"use strict"; ${helperJs}; return { ${HELPERS.join(', ')} };`)();

// The workspace helper mirrors the ENGINE's ConnectionRegistry.WorkspaceNameFromEndpoint rule for rule, so one
// endpoint is one string on both doors: only a TRAILING /myorg/<name> counts, and malformed percent-encoding
// yields nothing rather than the raw segment (showing "Bad%ZZ" as a name is the defect, not a milder version).
assert.equal(H.workspaceNameFromEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Fabric%20Monitoring'), 'Contoso Fabric Monitoring');
assert.equal(H.workspaceNameFromEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Contoso%20%5BTest%5D'), 'Contoso [Test]');
assert.equal(H.workspaceNameFromEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Sales/'), 'Sales', 'a trailing slash is still the trailing segment');
assert.equal(H.workspaceNameFromEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Sales/extra'), null, 'a non-trailing /myorg/<name> is not the workspace');
assert.equal(H.workspaceNameFromEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Bad%ZZ'), null, 'malformed encoding must yield no name, never the raw segment');
assert.equal(H.workspaceNameFromEndpoint('localhost:51234'), null, 'a local data source has no workspace');
assert.equal(H.workspaceNameFromEndpoint(undefined), null);

// decodeSegment is the softer half: it turns escapes into words for text we have already decided to SHOW, and
// hands back exactly what it was given when the escapes are malformed, because dropping the text would be worse.
assert.equal(H.decodeSegment('Contoso%20Fabric%20Monitoring'), 'Contoso Fabric Monitoring');
assert.equal(H.decodeSegment('Sales%20%26%20Marketing'), 'Sales & Marketing');
assert.equal(H.decodeSegment('Bad%ZZ'), 'Bad%ZZ', 'malformed escapes fall back to the text as stored');
assert.equal(H.decodeSegment(undefined), '');

// The defect itself: a workspace-only record now titles as the decoded workspace.
const workspaceOnly = { id: 'w', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Fabric%20Monitoring' };
assert.equal(H.nameOf(workspaceOnly), 'Contoso Fabric Monitoring');
assert.ok(H.isWorkspaceOnly(workspaceOnly), 'an xmla record with no dataset is a workspace, not a model');
assert.equal(H.whereOf(workspaceOnly), 'Workspace, you pick the model when you open it');

// Once a model has been opened from that workspace the engine records the dataset, and the row flips: the model
// name is the title and the workspace becomes the subtitle, alongside who the next open signs in as.
const opened = { ...workspaceOnly, id: 'm', database: 'Fabric Monitoring', modelName: 'Fabric Monitoring' };
assert.equal(H.nameOf(opened), 'Fabric Monitoring');
assert.ok(!H.isWorkspaceOnly(opened));
assert.equal(H.whereOf(opened), 'In Contoso Fabric Monitoring');

// Where a model lives stays ONE line for every kind, and the two local kinds say what they are, decoded.
assert.equal(H.whereOf({ id: 'f', kind: 'file', endpoint: 'C:\\Models\\B%20Edit.SemanticModel' }), 'C:\\Models\\B Edit.SemanticModel');
assert.equal(H.whereOf({ id: 'g', kind: 'localDesktop', endpoint: 'localhost:52100' }), 'Power BI Desktop, localhost:52100');
for (const r of [workspaceOnly, opened, { id: 'f', kind: 'file', endpoint: 'C:\\M\\a.bim' }]) {
  assert.ok(!H.whereOf(r).includes('\n'), 'where a model lives must stay one line');
}

// Encoded text never survives into a name, whichever field it came from, and the non-cloud kinds keep the
// behaviour they already had (a bare GUID is still not a name; a local file still names itself).
assert.equal(H.nameOf({ id: 'e', kind: 'xmla', endpoint: 'asazure://x/y/Sales%20Ops' }), 'Sales Ops', 'a non-myorg endpoint still decodes its last segment');
assert.equal(H.nameOf({ id: 'g', kind: 'localDesktop', endpoint: 'localhost:52100', database: '493c64f4-b0f5-4a1e-9c2d-77a1e6b0f3aa' }), 'Local running model');
assert.equal(H.nameOf({ id: 'f', kind: 'file', endpoint: 'C:\\Models\\B Edit.SemanticModel' }), 'B Edit.SemanticModel');
assert.equal(H.nameOf({ id: 'n', kind: 'xmla', endpoint: '' }), 'Model');

// An endpoint shown as DETAIL is shown decoded too — it is text for a person to read, not a string to copy.
assert.equal(H.endpointText('powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Fabric%20Monitoring'),
  'powerbi://api.powerbi.com/v1.0/myorg/Contoso Fabric Monitoring');

// ---- and the surfaces must actually USE them, so a name is one string everywhere ----
const rowBlock = slice('function ModelRow', 'function EndpointForm');
assert.match(rowBlock, /\{nameOf\(r\)\}/, 'the row title is the shared name');
assert.match(rowBlock, /const where = \[whereOf\(r\), who\]/, 'the row second line starts with where the model lives');
assert.match(rowBlock, /rowSecondLine\(where, jobs, ep\?\.name \|\| null\)/, 'then its jobs and its SQL endpoint');
assert.doesNotMatch(rowBlock, /short\(/, 'no surface may show a raw endpoint segment as text');
assert.doesNotMatch(rowBlock, /\{r\.endpoint\}/, 'the row must not print a raw endpoint anywhere, tooltip included');
assert.match(hub, /return side\?\.available \? \(side\.modelName \|\| side\.database \|\| workspaceNameFromEndpoint\(side\.source\) \|\| decodeSegment\(short\(side\.source\)\) \|\| fallback\) : fallback;/,
  'the Now strip falls back through the shared helpers, never a raw segment');

// The harness must carry the shape Kane hit, or the screenshots prove nothing.
assert.match(harness, /powerbi:\/\/api\.powerbi\.com\/v1\.0\/myorg\/Contoso%20Fabric%20Monitoring/,
  'the uishot harness needs a remembered WORKSPACE-only record with an encoded endpoint');

// The context bar names the same targets from the same connection context, and its own last-resort helper had the
// same defect: it took the endpoint's final path segment verbatim. The engine now fills modelName for a workspace
// target so this fallback is rarely reached, but "rarely reached" is not "harmless" — an endpoint with no /myorg/
// segment still lands here. Only the NAME text changes; the header rows belong to another lane.
const contextbar = read('webview', 'src', 'contextbar.tsx');
const epSrc = contextbar.match(/\nfunction shortEndpoint\([\s\S]*?\n\}/);
assert.ok(epSrc, 'shortEndpoint must stay a statically extractable top-level function in contextbar.tsx');
const shortEndpoint = Function(`"use strict"; ${tsc.transpileModule(epSrc[0], { compilerOptions: { target: tsc.ScriptTarget.ES2020 } }).outputText}; return shortEndpoint;`)();
assert.equal(shortEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Fabric%20Monitoring'), 'Contoso Fabric Monitoring',
  'the context bar must not name a target with an encoded segment either');
assert.equal(shortEndpoint('asazure://x.asazure.windows.net/Sales%20Ops'), 'Sales Ops');
assert.equal(shortEndpoint('powerbi://api.powerbi.com/v1.0/myorg/Bad%ZZ'), 'Bad%ZZ',
  'a malformed escape keeps the text as stored rather than losing the only hint there is');
assert.equal(shortEndpoint('powerbi://api.powerbi.com'), 'api.powerbi.com', 'a dotted trailing segment still falls back to the host');
assert.equal(shortEndpoint('localhost:51000'), 'localhost:51000', 'a bare host:port is unchanged (there is nothing to decode or shorten)');
assert.equal(shortEndpoint(''), '');


// ================================================================================================================
// SQL endpoints: one per model, set from its row (contract 19.3, slice C). The SQL sources section is gone; the saved
// record underneath is the same record, shared when several models use it, and it still holds NO credential.
// ================================================================================================================
const endpointForm = slice('function EndpointForm', 'const defaultConfirm');
assert.doesNotMatch(hub, /function SqlSourcesView|>SQL sources</, 'no screen shows a separate SQL sources section');
assert.match(hub, /export type HubView = [^\n]*'sqlsources'/, "HubView keeps 'sqlsources' so the Tests page's way in still lands");
assert.match(endpointForm, /SQL endpoint for \{nameOf\(r\)\}/, 'the form opens under its row and names the model');
assert.match(endpointForm, /Checks compare this model's numbers against it\. One per model, optional\./);
assert.match(endpointForm, /Found in this model's sources:/, "the engine's suggestion is offered first");
for (const op of ['listSqlSources', 'saveSqlSource', 'deleteSqlSource', 'testSqlSource', 'listModelSqlEndpoints']) {
  assert.match(hub, new RegExp(`rpc<[^>]*>\\('${op}'`), `the hub must call ${op} on the engine, not keep its own store`);
}
for (const gone of ['listTests', 'listTableMappings', 'listSqlSourceUsage']) {
  assert.doesNotMatch(hub, new RegExp(`'${gone}'`), `${gone} is not needed to count what uses a source; the endpoint listing carries it`);
}
// A source holds NO credential: the form never offers a password or a token box.
assert.doesNotMatch(endpointForm, /type="password"/, 'a SQL endpoint never takes a password');
assert.doesNotMatch(endpointForm, /placeholder="[^"]*token/i, 'a SQL endpoint never takes a token');
// It reuses the ONE shared account picker rather than growing a second sign-in control.
assert.match(hub, /import \{ AccountPicker \} from '\.\/accountpicker'/, 'the form must reuse the shared account picker');
assert.match(endpointForm, /<AccountPicker/, 'the endpoint form signs in through the shared picker');
const pickerSrc = read('webview', 'src', 'accountpicker.tsx');
assert.match(pickerSrc, /tenantLabel\?: string/, 'the shared picker must let a page name what the tenant box is for');
assert.match(pickerSrc, /tenantLabel \?\? 'where the model lives \(optional\)'/, 'the default words are unchanged, so no other page moves');
assert.match(endpointForm, /tenantLabel="where the database lives \(optional\)"/, 'a SQL endpoint asks where the DATABASE lives');

// ---- the direct-open route the Tests page uses -------------------------------------------------------------------
assert.match(hub, /export function openSqlSources\(/, 'the Tests page needs a named way in');
assert.match(hub, /export function listSqlSources\(/, "a check's source picker needs the saved sources");
assert.match(hub, /openConnectionsOnView\('sqlsources'\)/, "the way in asks for 'sqlsources'");
assert.match(hubModelSrc, /case 'sqlsources': return \{ view: 'open', panel: 'sql' \};/, "which lands on the open model's SQL endpoint");
assert.match(hub, /if \(r\) openEndpointForm\(r\);\s*\n\s*else setNotice\('Open a model first\./, 'with no open model it says where the endpoint is set');
assert.match(hub, /export interface SqlSourceRecord/, 'the record shape is shared with the Tests lane');

// ---- behavioural: the pure helpers, extracted and RUN --------------------------------------------------------
const SQL_HELPERS = ['sqlSourceFormError', 'sqlSourceTestLine', 'sqlSourceUsedByNames'];
const sqlHelperSource = SQL_HELPERS.map((name) => {
  const m = hub.match(new RegExp(`\\nfunction ${name}\\([\\s\\S]*?\\n\\}`));
  assert.ok(m, `${name} must stay a statically extractable top-level function in connectionshub.tsx`);
  return m[0];
}).join('\n');
const S = Function(`"use strict"; ${tsc.transpileModule(sqlHelperSource, { compilerOptions: { target: tsc.ScriptTarget.ES2020 } }).outputText}; return { ${SQL_HELPERS.join(', ')} };`)();
const full = { id: null, name: 'Contoso warehouse', server: 'contoso-sql.database.windows.net', database: 'Warehouse', authMode: 'interactive', tenantId: '' };
assert.equal(S.sqlSourceFormError(full), null, 'a complete form has nothing to complain about');
assert.match(S.sqlSourceFormError({ ...full, name: '   ' }), /name/i, 'a missing name is refused in the form');
assert.match(S.sqlSourceFormError({ ...full, server: '' }), /server address/i, 'a missing server is refused in the form');
assert.match(S.sqlSourceFormError({ ...full, database: '' }), /database/i, 'a missing database is refused in the form');
// FAIL CLOSED on the only shape a pasted password arrives in, the same rule the engine applies at the boundary.
assert.match(S.sqlSourceFormError({ ...full, server: 'Server=x;User Id=sa;Password=hunter2' }), /connection string/i,
  'a pasted connection string is refused in the form, never sent to the engine');
assert.match(S.sqlSourceFormError({ ...full, database: 'Initial Catalog=Warehouse;' }), /connection string/i);
// "Never tested" is a THIRD state, distinct from "the last test failed". Rounding it into "ok" would be a lie.
assert.equal(S.sqlSourceTestLine(undefined, '15 Sep, 04:12 PM'), 'Not tested yet');
assert.equal(S.sqlSourceTestLine(true, '15 Sep, 04:12 PM'), 'Last test worked on 15 Sep, 04:12 PM');
assert.equal(S.sqlSourceTestLine(false, '15 Sep, 04:12 PM'), 'Last test failed on 15 Sep, 04:12 PM');
// The refusal names what still points at the source, and says how many it could not name.
assert.equal(S.sqlSourceUsedByNames(['Sales total vs finance'], 3), 'Sales total vs finance, and 2 more');
assert.equal(S.sqlSourceUsedByNames([], 2), '2 not named here');
assert.equal(S.sqlSourceUsedByNames(['  ', 'Sales'], 2), 'Sales, and 1 more', 'a blank title is not a name');

// ---- a refused Remove of a not-linked source renders the engine's names --------------------------------------------
const listViewSrc = slice('function ListView', 'function HistoryView');
assert.ok(listViewSrc.includes('data-testid="hub-sql-refused"'), 'a refused Remove needs its own block, not a toast');
assert.ok(listViewSrc.includes('nlRefused.checkTitles') && listViewSrc.includes('nlRefused.mappedTables'), "the refusal renders the engine's own names");
assert.ok(/checksUsing \?\? 0\) === 0 && \(nlRefused\.tableMappingsUsing \?\? 0\) === 0 && !\(nlRefused\.linkedModels \?\? \[\]\)\.length && nlRefused\.note/.test(listViewSrc),
  "the engine's note is a fallback for a refusal that named nothing, never a second copy of the same sentence (D-166)");

// ---- the harness must carry the fixtures, or the screenshots prove nothing ---------------------------------------
assert.match(harness, /listSqlSources:/, 'the uishot harness must answer listSqlSources');
assert.match(harness, /testSqlSource:/, 'the uishot harness must answer testSqlSource');
assert.match(harness, /deleteSqlSource:/, 'the uishot harness must answer deleteSqlSource');
assert.match(harness, /saveSqlSource:/, 'the uishot harness must answer saveSqlSource');
assert.match(harness, /lastTestOk: false/, 'the fixtures must include a source whose last test failed');

console.log('Connections hub contract passed');
