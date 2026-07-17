import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Selection bus + F2 rename contract (the "Option C" tree program): Studio navigators feed the Properties
// view without stealing focus, every navigator row that names a model object carries the ONE universal
// "Reveal in Model tree" affordance, and F2 renames in the Properties Name row (InputBox kept as fallback).

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

const bridge = read('webview/src/bridge.ts');
const actions = read('webview/src/objectactions.tsx');
const ext = read('src/extension.ts');
const propgrid = read('media/propgrid/propgrid.js');
const pkg = JSON.parse(read('package.json'));

// --- 1. the bus: one shared webview helper, one host handler, no focus steal ---------------------------
assert.match(bridge, /export function selectInProperties\(/, 'bridge must export the shared selection-bus helper');
assert.match(bridge, /postMessage\(\{ type: 'selectObject', ref \}\)/, 'the bus must post the selectObject message');

assert.match(ext, /msg\?\.type === 'selectObject'/, 'the Studio message relay must handle selectObject');
assert.match(ext, /function selectRefInProperties\(/, 'the host must route selectObject through one function');
const busFn = ext.slice(ext.indexOf('function selectRefInProperties'), ext.indexOf('async function revealRefInTree'));
assert.match(busFn, /propGrid\.showObject\(/, 'the bus must reuse the SAME Properties path a tree selection uses');
assert.doesNotMatch(busFn, /semanticusProperties\.focus/, 'the bus must NOT steal focus (Properties only repopulates)');

// 1b. Focus-driven selection: focusing an object row selects it in Properties WITHOUT the primary action. The
// focus half ALWAYS posts (a definite, never-stale intent); the click that follows a mouse-focus is deduped by a
// one-shot token. The token SELF-EXPIRES — a short timeout that outlasts one mouse gesture (focus→click) but
// nothing longer, plus a supersede on the next focus and a clear on the click that consumes it — so a focus with
// NO trailing click (keyboard walk, pointer moved on) can't leave it set to later swallow a legitimate click on
// the same ref across a tab switch or model remount.
assert.match(bridge, /export function focusSelectInProperties\(/, 'bridge must export the focus-driven selection helper');
const focusSel = bridge.slice(bridge.indexOf('export function focusSelectInProperties'));
assert.match(focusSel, /pendingFocusRef = ref;/, 'focusSelectInProperties must record the ref');
assert.match(focusSel, /vscode\.postMessage\(\{ type: 'selectObject', ref \}\)/, 'focusSelectInProperties must ALWAYS post');
assert.match(focusSel, /setTimeout\(clearPendingFocus, \d+\)/, 'focusSelectInProperties must arm a timeout that expires the one-shot token (no lingering across gestures)');
assert.match(bridge, /function clearPendingFocus\(\)/, 'a shared clear must reset the token AND cancel its timer');
assert.match(bridge, /window\.clearTimeout\(pendingFocusTimer\)/, 'clearPendingFocus must cancel the pending timer so it cannot fire late against a fresh token');
const clickSel = bridge.slice(bridge.indexOf('export function selectInProperties'), bridge.indexOf('export function focusSelectInProperties'));
assert.match(clickSel, /const fromFocus = ref === pendingFocusRef/, 'the click path must collapse the redundant post from a preceding same-ref focus');
assert.match(clickSel, /clearPendingFocus\(\)/, 'the click path must clear the token after consuming it (one-shot)');

// --- 2. the universal Reveal affordance + per-navigator wiring -----------------------------------------
assert.match(actions, /Reveal in Model tree/, 'the shared RevealBtn must carry the canonical label');
assert.match(actions, /stopPropagation/, 'RevealBtn must not trigger the row action it sits on');
assert.match(actions, /export function rowKeyProps\(/, 'the shared keyboard-activation helper must exist');
for (const nav of ['objectbrowser.tsx', 'fieldspanel.tsx', 'search.tsx', 'datapreview.tsx', 'App.tsx']) {
  const src = read('webview/src/' + nav);
  assert.match(src, /selectInProperties\(/, `${nav} must feed the selection bus on row focus`);
  assert.match(src, /RevealBtn/, `${nav} must expose the universal Reveal in Model tree affordance`);
  // Keyboard a11y: div/tr rows are not natively focusable/activatable, so every navigator that made rows
  // clickable must also wire the shared keyboard-activation helper (role=button + tabIndex + Enter/Space).
  assert.match(src, /rowKeyProps\(/, `${nav} must give its rows keyboard activation (rowKeyProps)`);
  // Focus alone must SELECT (feed Properties) without the primary action — every navigator passes a focus-select
  // to rowKeyProps' second arg. The div-navigators route it through focusSelectInProperties; the SortableTable
  // (App.tsx) whose primary action IS the select passes its onRowClick. Either way, rowKeyProps gets a 2nd arg.
  assert.match(src, /rowKeyProps\([^\n]*,\s*\(\) =>/, `${nav} must wire a focus-select (rowKeyProps' 2nd arg) so focusing a row selects it`);
}
// The div-navigators use the dedicated always-posts focus helper (not the deduped click path) on focus.
for (const nav of ['objectbrowser.tsx', 'fieldspanel.tsx', 'search.tsx', 'datapreview.tsx']) {
  assert.match(read('webview/src/' + nav), /focusSelectInProperties\(/, `${nav} must select on focus via the always-posts focus helper`);
}
// The Reveal affordance must appear on keyboard focus, not hover alone — for the navigators that hide it until
// hover. (The Statistics grids in App.tsx show ⤢ permanently, so it is already keyboard-reachable there.)
for (const nav of ['objectbrowser.tsx', 'fieldspanel.tsx', 'search.tsx', 'datapreview.tsx']) {
  assert.match(read('webview/src/' + nav), /group-focus-within:/, `${nav} must reveal its hover affordance on keyboard focus-within`);
}
// The shared helper actually implements the keyboard contract.
assert.match(actions, /role: 'button'/, 'rowKeyProps must expose the row as a button role');
assert.match(actions, /tabIndex: 0/, 'rowKeyProps must make the row tab-focusable');
assert.match(actions, /'Enter' \|\| e\.key === ' '/, 'rowKeyProps must activate on Enter/Space');
// Focus-select: rowKeyProps wires onFocus to the optional focusSelect, guarded to the control itself so a focus
// bubbling up from a sibling control doesn't re-select. Enter/Space still run the PRIMARY action (activate).
assert.match(actions, /onFocus: focusSelect \?/, 'rowKeyProps must wire onFocus to the focus-select when provided');
assert.match(actions, /if \(e\.target === e\.currentTarget\) focusSelect\(\)/, 'onFocus must act only for the control itself (ignore a bubbled child focus)');
// The stats grids must not steal focus on a plain row click any more (reveal is the explicit ⤢ action).
assert.doesNotMatch(read('webview/src/App.tsx'), /onRowClick=\{\([a-z]\) => revealInTree/, 'stats row click must select, not reveal');
// Statistics (the generic SortableTable) must feed FOCUS through the always-posts helper, NOT the deduped click
// path — else a mouse focus double-posts (focus posts, then the click posts again with no token set) and a
// keyboard walk relying on the click path could be swallowed by a stale token. It takes a DISTINCT focus handler.
assert.match(read('webview/src/App.tsx'), /focusSelectInProperties\(/, 'the Statistics grids must select on FOCUS via the always-posts helper (not the deduped click path)');
assert.match(read('webview/src/App.tsx'), /onRowFocus\?: \(r: T\) => void/, 'SortableTable must accept a distinct focus-select handler (separate from onRowClick)');
assert.match(read('webview/src/App.tsx'), /rowKeyProps\(\(\) => onRowClick\?\.\(r\), \(\) => \(onRowFocus \?\? onRowClick\)\?\.\(r\)\)/, 'SortableTable must route focus through onRowFocus (fallback onRowClick), distinct from the click activate');

// --- 2b. untrusted-input allowlists: only resolvable ref kinds reach the engine ------------------------
assert.match(ext, /const SELECTABLE_REF_KINDS = new Set\(/, 'the host must allowlist the ref kinds it will resolve');
assert.match(busFn, /SELECTABLE_REF_KINDS\.has\(/, 'selectRefInProperties must reject kinds outside the allowlist');
const search = read('webview/src/search.tsx');
assert.match(search, /const NAVIGABLE_KINDS = new Set\(/, 'Search must allowlist the kinds it renders select/reveal for');
assert.doesNotMatch(search, /NAVIGABLE_KINDS = new Set\(\[[^\]]*namedexpression/, 'namedexpr is NOT navigable (grid + tree cannot resolve it)');
assert.match(search, /navigable \? rowKeyProps/, 'Search must only wire keyboard activation for navigable hits');

// --- 2c. live-stats rows verify against the STAGED model before acting ---------------------------------
const app = read('webview/src/App.tsx');
// Verify the FULL ref: a table row checks its table, a column row checks table AND column — a renamed column on
// a still-present table must not stay reachable (published name → nothing, or a same-named wrong object).
assert.match(app, /tableReachable\(/, 'live VertiPaq table rows must verify their table against the staged model');
assert.match(app, /columnReachable\(/, 'live VertiPaq column rows must verify the FULL ref (table AND column), not just the table');
// Gate each row on ONLY the data it needs: a table row needs meta; a column row needs meta AND stagedCols — an
// unrelated listColumns failure must NOT disable verifiable table rows. Both fail CLOSED while their data is null.
assert.match(app, /const tableReachable = useCallback\(\(table: string\) => editingEvidenceAllowed && meta != null && stagedTables\.has\(table\)/, 'table rows require a proven edit/query relationship and meta (a listColumns failure still must not kill them)');
assert.match(app, /const columnReachable = useCallback\(\(table: string, column: string\) => editingEvidenceAllowed && meta != null && stagedCols != null/, 'column rows require a proven edit/query relationship plus meta and stagedCols, failing closed while any gate is unavailable');
assert.match(app, /relationshipAllowsStorageEdits\(context\?\.relationship\)/, 'storage reachability and mutations must fail closed unless the protocol proves the query scan describes the editing model');
assert.doesNotMatch(app, /const metaReady = /, 'the combined metaReady gate is gone — table and column rows now gate independently');
assert.match(app, /stagedCols\.has\(colKey\(table, column\)\)/, 'the column check must resolve against the staged column key set');
assert.match(app, /listColumns/, 'the staged column names must be loaded (getModelGraph carries counts only, not names)');
// The staged column key must NOT be a '/'-join: '/' is legal in BOTH a table and a column name, so table+'/'+column
// collides ("Sales/EU"+"Amount" == "Sales"+"EU/Amount"). Join on the unit-separator control char instead.
assert.match(app, /const KEY_SEP = '\\u001f'/, 'the staged column key must join on an unambiguous separator, not a real ref char');
assert.doesNotMatch(app, /stagedCols\.has\(table \+ '\/' \+ column\)/, "the column key must NOT '/'-join (collides on slash-in-name)");
// Re-verify on model/didChange, and IMMEDIATELY distrust the loaded shape (clear state) BEFORE the refetch lands —
// otherwise the old refs stay actionable during the in-flight reload and a just-renamed object is still reachable.
// The reset helper clears the whole staged shape (meta + column names + the summarize/unused joins) and BOTH the
// model-change and reconnect handlers call it BEFORE refetching — so no stale ref stays actionable during the reload.
assert.match(app, /const reset = \(\) => \{ setMeta\(null\); setStagedCols\(null\); setColRows\(undefined\); setUnused\(undefined\); \};/, 'a model change must clear the whole staged shape (meta + column names + joins)');
assert.match(app, /onDidChange\(\(\) => \{ reset\(\); loadStaged\(\); \}\)/, 'a model change must clear the staged shape BEFORE refetching (no actionable stale refs during the reload)');
// Reconnect additionally clears the SCAN + comparison snapshots (clearScan) — a new connection/model must never
// keep rendering the previous model's storage numbers or persist under its comparison chain.
assert.match(app, /onReconnect\(\(\) => \{ reset\(\); clearScan\(\); loadStaged\(\); \}\)/, 'a model swap must distrust the old staged shape AND drop the prior scan (fail closed)');
// Versioned loads: a bumped seq tags each load and a stale in-flight response is dropped, so overlapping reloads
// can never restore an OLDER shape over a newer one; a failed read nulls its state (never keeps a stale shape).
assert.match(app, /const loadSeq = useRef\(0\)/, 'staged loads must be versioned with a seq counter');
assert.match(app, /const seq = \+\+loadSeq\.current/, 'each load must claim the next seq');
assert.match(app, /if \(seq === loadSeq\.current\) setMeta\(g\)/, 'a stale getModelGraph response must be dropped (only the newest load applies)');
assert.match(app, /catch\(\(\) => \{ if \(seq === loadSeq\.current\) setMeta\(null\); \}\)/, 'a failed getModelGraph must fail CLOSED (null meta), never keep a stale shape');
assert.match(app, /if \(seq === loadSeq\.current\) \{ setColRows\(null\); setStagedCols\(null\); \}/, 'a failed listColumns must fail CLOSED (null stagedCols)');

// --- 2d. the Data preview table list refreshes on model/didChange (no stale, obsolete-ref rows) --------
// Loaded once on mount, a renamed/added/dropped table would leave a stale row whose ref is obsolete — activating
// it feeds the bus (and the preview query) a name the model no longer has. Reload on didChange, versioned so a
// slower earlier response can't restore an older list, failing to empty (never keeping stale, obsolete-ref rows).
const dp = read('webview/src/datapreview.tsx');
assert.match(dp, /const off = onDidChange\(\(\) => loadTables\(\)\)/, 'the Data table list must reload on model/didChange');
assert.match(dp, /const tablesSeq = useRef\(0\)/, 'the table-list reload must be versioned');
assert.match(dp, /if \(seq === tablesSeq\.current\) setTables\(g\.tables\)/, 'a stale table-list response must be dropped (only the newest reload applies)');
assert.match(dp, /catch\(\(\) => \{ if \(seq === tablesSeq\.current\) setTables\(\[\]\); \}\)/, 'a failed table-list read must fail CLOSED (empty), never keep stale obsolete-ref rows');
// Fail closed DURING the reload too: clear the rows BEFORE the refetch settles, so a renamed/dropped table's
// obsolete-ref row can't be activated mid-reload (same discipline as the Statistics staged-shape clear).
const loadTablesFn = dp.slice(dp.indexOf('const loadTables ='), dp.indexOf('useEffect(() => {'));
assert.ok(loadTablesFn.includes('setTables([])'), 'loadTables must clear the rows on each reload (fail closed)');
assert.ok(loadTablesFn.indexOf('setTables([])') < loadTablesFn.indexOf("rpc<ModelGraph>('getModelGraph')"),
  'the clear must precede the getModelGraph refetch (no actionable obsolete-ref rows during the in-flight reload)');

// --- 3. F2 rename → the Properties Name row -------------------------------------------------------------
assert.match(ext, /registerCommand\('semanticus\.renameObject', \(n: TreeNode, ns\?: TreeNode\[\]\) => renameInPropertiesCmd\(n, ns\)\)/,
  'F2/Rename must route to the Properties grid (forwarding the selection for multi-select handling)');
assert.match(ext, /registerCommand\('semanticus\.renameObjectInputBox', \(n: TreeNode\) => renameCmd\(n\)\)/,
  'the InputBox rename must survive as its own command');
const renameFn = ext.slice(ext.indexOf('async function renameInPropertiesCmd'), ext.indexOf('async function renameCmd'));
assert.match(renameFn, /semanticusProperties\.focus/, 'rename must focus the Properties view');
// The focus key is captured from the F2 TARGET at the command site (before the show/focus awaits), then passed
// through — NOT read from live provider state when the grid finally focuses (that would rename a wrong object).
assert.match(renameFn, /const targetRef = n\.ref/, 'the F2 target ref must be captured at the command site, before the awaits');
assert.match(renameFn, /focusNameRow\(targetRef\)/, 'the captured target ref must be threaded through to focusNameRow');
assert.match(renameFn, /renameFolderCmd\(n\)/, 'F2 on a display folder must keep the prefix-rewrite prompt');
assert.match(renameFn, /Select a single object to rename/, 'multi-select rename must ask the user to narrow, not silently pick one');
assert.match(renameFn, /RENAMEABLE_KINDS\.has\(/, 'rename must refuse structural nodes instead of opening an empty grid');
assert.match(ext, /const RENAMEABLE_KINDS = new Set\(/, 'the renameable-kind allowlist must exist');
assert.match(ext, /focusNameRow\(targetRef: string\): void/, 'focusNameRow must take the F2 target ref, not read the live selection');
assert.match(ext, /pendingFocusKey/, 'a focus request against a still-loading view must be parked, not dropped');
// Discard-on-mismatch: if the selection moved off the F2 target before focus lands, DROP the intent (never focus).
const focusFn = ext.slice(ext.indexOf('focusNameRow(targetRef: string)'), ext.indexOf('private async push()'));
assert.match(focusFn, /this\.refs\.length !== 1 \|\| this\.refs\[0\] !== targetRef/, 'focusNameRow must discard when the selection has moved off the F2 target');
assert.match(ext, /key === JSON\.stringify\(this\.refs\)/, 'a parked focus intent must only flush if the selection is STILL that object');

// The F2 keybinding must be scoped to renameable viewItems so it never focuses an empty grid on a structural node.
const f2 = pkg.contributes.keybindings.find((k) => k.key === 'f2');
assert.equal(f2?.command, 'semanticus.renameObject', 'F2 must stay bound to the (rerouted) rename command');
assert.match(f2?.when ?? '', /viewItem =~/, 'the F2 keybinding must be scoped to renameable tree items');

// --- 3b. the renamed object is re-keyed so the didChange refresh does not blank the grid ---------------
assert.match(ext, /adopt the renamed ref/, 'a successful Name write must adopt the renamed (new) ref');
assert.match(ext, /msg\.name === 'Name' && refs\.length === 1/, 'the re-key path must fire on a single-object Name write');
// '/' is BOTH the ref separator AND a legal name character (a measure can be named "Gross/Net"), so the object-name
// boundary is NOT recoverable from the string — the re-key must STRIP the KNOWN old name (captured pre-write) and
// append the new name, never splice at a slash (which would corrupt a slash-in-name ref and blank the grid).
const rekeyBlock = ext.slice(ext.indexOf("msg.name === 'Name' && refs.length === 1"), ext.indexOf('// On success the mutation'));
assert.match(ext, /const names = this\.names\.slice\(\)/, 'the pre-write object names must be captured alongside refs for the re-key');
assert.match(rekeyBlock, /refs\[0\]\.endsWith\(oldName\)/, 'the re-key must verify the ref ends with the KNOWN old name before stripping it');
assert.match(rekeyBlock, /refs\[0\]\.length - oldName\.length/, 'the new ref = (ref minus the old-name suffix) + the new name');
assert.doesNotMatch(rekeyBlock, /lastIndexOf\('\/'\)/, "the re-key must NOT splice at a slash — '/' is a legal name char, so a slash-in-name would corrupt");
// Pin the mechanism on the reported bug shapes, INCLUDING a slash-in-name (measure "Gross/Net" on table Sales).
const rekey = (ref, oldName, newName) =>
  (typeof oldName === 'string' && oldName && ref.endsWith(oldName)) ? ref.slice(0, ref.length - oldName.length) + newName : ref;
assert.equal(rekey('measure:Sales/Amount', 'Amount', 'Total'), 'measure:Sales/Total', 'a two-part re-key replaces only the trailing name');
assert.equal(rekey('level:Sales/Geography/Country', 'Country', 'Region'), 'level:Sales/Geography/Region', 'a level re-key must keep the hierarchy segment');
assert.equal(rekey('measure:Sales/Gross/Net', 'Gross/Net', 'Margin'), 'measure:Sales/Margin', 'a slash-IN-name re-key strips the WHOLE known name, never splices at a slash');
assert.equal(rekey('table:Sales', 'Sales', 'Revenue'), 'table:Revenue', 'a colon-only ref re-keys after the colon');

// --- 3e. selection-bus objects re-key on the CANONICAL name, never a refParts guess -------------------
// refParts treats the FIRST '/' as the table/child separator, so a TOP-LEVEL object whose NAME contains '/'
// (a table "Sales/EU" → ref "table:Sales/EU", or a top-level function/role/perspective) parses to the WRONG name
// ("EU"). If the grid re-keyed off that guess, renaming to "Revenue" would build "table:Sales/Revenue" AND the
// endsWith guard would still PASS — a silent corruption. The fix: push() adopts the CANONICAL Name from the load
// response, overwriting the placeholder, so the re-key always strips the TRUE whole name.
assert.match(ext, /all\[i\]\?\.find\(\(p\) => p\.name === 'Name'\)\?\.value \|\| names\[i\]/, 'push() must adopt the canonical Name from the load response, overwriting the ref-parse placeholder');
const canonBlock = ext.slice(ext.indexOf('const canonical ='), ext.indexOf('const title ='));
assert.match(canonBlock, /this\.names = canonical/, 'the canonical names must replace this.names so the re-key holds the TRUE old name on every path');
// Executable: the FULL host flow for a slash-in-name TOP-LEVEL object arriving via the bus, then a rename.
const refPartsName = (ref) => { const rest = ref.slice(ref.indexOf(':') + 1); const s = rest.indexOf('/'); return s < 0 ? rest : rest.slice(s + 1); };
const rekeyGuarded = (ref, oldName, newName) => (typeof oldName === 'string' && oldName && ref.endsWith(oldName)) ? ref.slice(0, ref.length - oldName.length) + newName : ref;
// The bus seeds a placeholder from refParts; push()'s load response then OVERWRITES it with the canonical Name.
const busRename = (ref, canonicalName, newName) => { let stored = refPartsName(ref); stored = canonicalName; return rekeyGuarded(ref, stored, newName); };
assert.equal(refPartsName('table:Sales/EU'), 'EU', 'refParts misattributes a slash-in-name top-level table (the latent bug source)');
assert.equal(busRename('table:Sales/EU', 'Sales/EU', 'Revenue'), 'table:Revenue', 'the bus path re-keys off the CANONICAL name → correct ref (not table:Sales/Revenue)');
assert.equal(busRename('function:My/Fn', 'My/Fn', 'Renamed'), 'function:Renamed', 'a slash-in-name top-level function re-keys whole');
assert.equal(busRename('role:A/B', 'A/B', 'C'), 'role:C', 'a slash-in-name role re-keys whole');
assert.equal(busRename('perspective:P/Q', 'P/Q', 'R'), 'perspective:R', 'a slash-in-name perspective re-keys whole');
// Proof the endsWith guard alone is INSUFFICIENT: re-keying off the refParts GUESS passes the guard yet corrupts
// the ref — so the canonical name adopted in push() is load-bearing, not belt-and-suspenders.
assert.equal(rekeyGuarded('table:Sales/EU', refPartsName('table:Sales/EU'), 'Revenue'), 'table:Sales/Revenue',
  'the OLD behavior (re-key off the guess) silently corrupts — endsWith does NOT catch it');

// --- 3d. the shared keyboard handler acts ONLY for its row, never a bubbled nested control -------------
assert.match(actions, /e\.target !== e\.currentTarget/, 'rowKeyProps must ignore Enter/Space bubbled from a nested control (checkbox / Reveal) — only the row itself activates');
// Structure: the div-row navigators put role=button on a LEAF (the name/label span) so no role=button element
// contains a nested button (checkbox / Reveal stay siblings, with their own native tab stops). Only the generic
// SortableTable <tr> (cells are caller-rendered — no leaf available) keeps row-level rowKeyProps, which is what
// the target===currentTarget guard exists for.
for (const nav of ['objectbrowser.tsx', 'fieldspanel.tsx', 'datapreview.tsx']) {
  const src = read('webview/src/' + nav);
  assert.match(src, /<span[^\n]*\{\.\.\.rowKeyProps\(/, `${nav} must spread rowKeyProps on a leaf span, not the row that nests buttons`);
  assert.doesNotMatch(src, /<(div|tr)[^\n]*\{\.\.\.rowKeyProps\(/, `${nav} must NOT make a row that nests buttons a role=button`);
}

// --- 3c. the grid focus intent is KEYED to its object (no cross-object focus steal) --------------------
assert.match(propgrid, /m\.type === 'focusName'/, 'the grid must accept the focusName message');
assert.match(propgrid, /pendingFocusKey/, 'the focus intent must be stored KEYED to its object, not as a bare boolean');
assert.match(propgrid, /pendingFocusKey !== true && pendingFocusKey !== cur/, 'a load for a different object must DROP the intent (no focus steal)');
assert.match(ext, /postMessage\(\{ type: 'focusName', key \}\)/, 'the host must send the object key with focusName so the grid can bind the intent');
assert.match(propgrid, /data-prop="Name"/, 'the grid must target the Name row input');
assert.match(propgrid, /\.select\(\)/, 'the Name text must be selected (type-over ready)');

// --- contributions ---------------------------------------------------------------------------------------
const cmds = pkg.contributes.commands.map((c) => c.command);
assert.ok(cmds.includes('semanticus.renameObjectInputBox'), 'package.json must contribute the fallback command');
const ctx = pkg.contributes.menus['view/item/context'];
assert.ok(ctx.some((m) => m.command === 'semanticus.renameObjectInputBox'), 'the fallback must be reachable from the tree context menu');

console.log('selection bus + F2 rename contract tests passed');
