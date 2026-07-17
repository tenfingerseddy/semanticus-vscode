import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as net from 'net';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import {
    createMessageConnection,
    MessageConnection,
    ParameterStructures,
    StreamMessageReader,
    StreamMessageWriter,
} from 'vscode-jsonrpc/node';
import { analyzeDax, collectVars, extractDaxSymbols, varDefinition, type DaxIndex } from './daxLint';
import { formatDax } from './daxFormat';
import { buildDaxHeader, splitDaxHeader, reKeyRef, uniqueName, checkDaxHeader, decideDaxSave, decideRenameRecovery, guardModelMatch, identityToken, MODEL_SCOPED_KINDS, type PendingRenameRecord } from './daxHeader';
import {
    decideEngineOwner,
    resolveEngineCandidate,
    shouldAutoHealMcpEntry,
    type McpServerEntry,
    type ResolvedEngine,
} from './engineResolution';
import { normFolder, folderParts, folderRef, parentFolderPath, leafFolderName, groupFolderLevel } from './folders';
import { migrateLegacyXmlaEntries, type LegacyRecentXmla } from './legacyXmlaMigration';
import { getUiChallenge, RpcHandshakeRejectedError, rpcRolePreamble, waitForRpcHandshake } from './rpcAuth';

// ---- wire DTOs (camelCase, matching Semanticus.Engine/Protocol.cs) ----------------------------
// displayFolder: the member's DisplayFolder path ("Parent\Child") — the engine fan stays FLAT; this
// extension synthesizes the folder nodes (kind 'dfolder') client-side so agents' list_objects is unchanged.
interface TreeNode { ref: string; name: string; kind: string; hasChildren: boolean; displayFolder?: string; }
interface ModelRef { kind: string; path?: string; gitRef?: string; endpoint?: string; database?: string; authMode?: string; tenantId?: string; label?: string; }
interface ChangeDelta { kind: string; ref: string; props?: string[]; }
interface UndoStateDto { canUndo: boolean; canRedo: boolean; atCheckpoint: boolean; }
// Mirrors Semanticus.Engine HealthDelta (feature #4): the health movement one commit caused. Pro-only and
// threshold-suppressed — absent on free, on health-neutral edits, and from pre-feature engines (additive field).
interface HealthDelta { grade?: string; new?: string[]; findings?: number; impact?: number; warn?: boolean; }
interface ChangeNotification { sessionId: string; revision: number; origin: string; label?: string; deltas?: ChangeDelta[]; undo?: UndoStateDto; health?: HealthDelta; }
interface OpenResult { sessionId: string; revision: number; modelName: string; tables: number; measures: number; source: string; liveConnected?: boolean; account?: string; }
interface SessionInfo { sessionId?: string; revision: number; modelName?: string; source?: string; hasUnsavedChanges?: boolean; tables?: number; measures?: number; liveBound?: boolean; liveEndpoint?: string; liveDatabase?: string; liveConnected?: boolean; liveKind?: string; liveDataSource?: string; currentAccount?: string; currentTenant?: string; }
interface ModelConnectionRecord { id: string; kind: string; endpoint: string; database?: string; modelName?: string; tenantId?: string; authMode?: string; label?: string; workingFolder?: string; publishConnectionId?: string; lastUsedUtc?: string; lastAccount?: string; }
// One target's silently-probed account (mirrors Engine ConnectionAccountProbe). `account` is who the NEXT open signs in
// as (the tenant-wide record wins); `previousAccount` names the target's last-used account when it differs ("was <x>").
interface ConnectionAccountProbe { id: string; account?: string; previousAccount?: string; tenantId?: string; }
// The credential family a tenant-wide account switch acts on: only interactive / device-code sign-ins keep a switchable
// record. azcli / serviceprincipal / token have no account picker, so an "Open as…" on them would be a false promise.
function credentialFamily(authMode?: string): 'interactive' | 'devicecode' | null {
    const m = (authMode || 'interactive').trim().toLowerCase();
    if (m === 'interactive' || m === 'entra' || m === 'entramfa' || m === 'mfa') return 'interactive';
    if (m === 'devicecode') return 'devicecode';
    return null;   // azcli / serviceprincipal / token — no interactive record to switch
}
interface LocalInstance { port: number; title: string; dataSource: string; }
// Mirrors Entitlement.EntitlementInfo (camelCased over the RPC): the tier the engine actually computed, plus a
// teaching `reason` (why free, OR the grace advisory while still Pro). expiry is unix seconds (0 = perpetual).
interface EntitlementInfo { tier: string; licensedTo?: string; expiry?: number; reason?: string; manageUrl?: string; }
interface SetResult { revision: number; changed: boolean; }
// Mirrors Semanticus.Engine RenameResult (camelCased over the RPC). newRef is the AUTHORITATIVE post-rename ref the
// engine computed (never a string-spliced guess) — the header-driven save re-keys to it, falling back to reKeyRef only
// if a build ever omits it.
interface RenameResult { revision: number; changed: boolean; newRef?: string; warning?: string; }
interface SaveResult { revision: number; path: string; format: string; fileCount: number; }
interface DeployReport { endpoint?: string; database?: string; committed?: boolean; totalChanges?: number; added?: number; changes?: string[]; unmatched?: string[]; liveOnly?: string[]; conflicts?: string[]; error?: string; }
interface EngineInfo { pipeName: string; pid: number; startedUtc: string; workspace: string; exePath?: string; }
interface SearchHit { ref: string; kind: string; name: string; table?: string; where: string; snippet?: string; }
interface SearchResult { query: string; hits: SearchHit[]; total: number; truncated: boolean; }

const DAX_SCHEME = 'semanticus';
const MODEL_REF = 'model:';
const out = vscode.window.createOutputChannel('Semanticus');

let conn: MessageConnection | undefined;
let connectionEpoch = 0;                              // invalidates a connect attempt when restart/shutdown overtakes it
let connectInFlight: { epoch: number; promise: Promise<void> } | undefined;
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
let reconnectAttempt = 0;
let deactivating = false;
let spawned: cp.ChildProcess | undefined;
let studioPanel: vscode.WebviewPanel | undefined;   // at most one Studio panel (openStudio reveals the existing one)
let studioReady = false;                            // the Studio webview has posted 'studioReady' (its listeners are live)
let pendingNav: { tab: string; target?: string; addTables?: string[] } | undefined;  // a navigation queued while the panel was still mounting
let pendingOpenConnections = false;   // "Manage connections" queued while the Studio panel was still mounting
let dragTablesStash: string[] = [];                 // tables being dragged from the Model tree — handed to the Studio diagram on drop (the webview iframe can't read a native drag's DataTransfer, so it pulls them via the 'requestDropTables' relay)
let tree: ModelTreeProvider;                          // module-scoped so every context-menu handler can refresh the tree
let treeView: vscode.TreeView<TreeNode>;              // module-scoped so handlers can read the multi-selection
let propGrid: PropertyGridProvider;                   // module-scoped so the "Properties" menu can target a node
let daxFs: DaxFileSystem;                             // module-scoped so connectEngine() can re-attach it on restart
let status: vscode.StatusBarItem;                     // module-scoped so connectEngine()/restart can update it
let healthStatus: vscode.StatusBarItem;               // the health chip (feature #4): what the LAST change did to model health — plain English, never a popup
let syncStatus: vscode.StatusBarItem;                 // the sync chip: what you're EDITING vs QUERYING (mirrors the Studio footer, but global — visible while editing a TMDL file / browsing the tree, outside the webview). Click → seeded Compare.
let aiConnectStatus: vscode.StatusBarItem;            // call-to-action shown ONLY while this workspace has no `semanticus` MCP entry — clicking it runs Connect AI Assistant. Hidden once connected, so it's a disappearing nudge, not clutter.
// Connection-mutating RPCs the webview relays through the host: after one lands, re-read the sync chip so a
// webview-initiated attach/disconnect (which fires no model/didChange) still updates the native chip immediately.
const SYNC_REFRESH_METHODS = new Set(['connectLocal', 'connectXmla', 'disconnect', 'openLocal', 'openLive', 'prepareWorkingCopy']);
let healthSessionId: string | undefined;               // which session the chip reflects — a session swap hides it (no didChange fires on open)
let lastHealthWorsened = false;                          // did the last change WORSEN health? — decides the chip click: offer one-click Undo vs just open the scan
let referenceTree: ReferenceTreeProvider;            // the "Reference Model" tree — a second model browsed to copy FROM
let referenceView: vscode.TreeView<TreeNode>;
let referenceRef: ModelRef | undefined;              // which model the Reference view is browsing
let copyStash: { source: ModelRef; refs: string[] } | undefined;   // Ctrl+C in the Reference tree → Ctrl+V into the Model tree
let treeCopyStash: { ref: string; kind: string; name: string; table?: string } | undefined;   // Ctrl+C in the MODEL tree → Ctrl+V duplicates via the engine (a reference, not content)
let lastCopyFrom: 'model' | 'reference' | undefined;   // when both clipboards hold something, the most recent copy wins (real clipboard semantics)
let extCtx: vscode.ExtensionContext;                  // module-scoped so secure storage and one-time migrations share the activation context

export async function activate(context: vscode.ExtensionContext) {
    deactivating = false;
    reconnectAttempt = 0;
    cancelScheduledReconnect();
    connectionEpoch++;
    extCtx = context;
    // CRITICAL 2: drop any pending-rename record (incl. a tombstone) whose editor is no longer open — e.g. the user closed
    // the tab while the extension was inactive, so the clean-close handler never fired. Deferred so restored tabs/docs are
    // present first: a record whose dirty doc IS restored must be KEPT (its editor can still save), so the sweep only
    // clears records with NO open tab AND no open document — the fail-closed direction is to keep, never to over-prune.
    setTimeout(() => { void sweepClosedPendingRenames(); }, 5000);
    tree = new ModelTreeProvider();
    propGrid = new PropertyGridProvider(() => conn, context.extensionUri);
    daxFs = new DaxFileSystem(() => conn);
    status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    status.text = '$(database) Semanticus: connecting…';
    status.command = 'semanticusModel.focus';
    status.show();

    // Health chip (feature #4): sits right of the rev text; shown only when the engine reports the last change
    // moved model health (Pro + over threshold) — so free tier and quiet edits keep the bar exactly as before.
    // Ambient presence, never a popup. Clicking a WORSENING chip offers a one-click Undo of the change that
    // caused it (the change is already on the undo timeline); otherwise it opens the AI Readiness tab. That
    // decision lives in semanticus.healthChipAction (it reads lastHealthWorsened set by renderHealthChip).
    healthStatus = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
    healthStatus.command = 'semanticus.healthChipAction';

    // Sync chip (mirrors the Studio footer's load-bearing sync verdict, but NATIVE so "what am I connected to"
    // is visible everywhere — editing a TMDL file, browsing the tree — not only inside the Studio webview. Shown
    // only once a model is open; clicking opens a seeded Compare (editing vs querying), exactly like the footer.
    syncStatus = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 98);
    syncStatus.command = 'semanticus.openCompareSeeded';

    // "Connect AI Assistant" call-to-action: wiring Claude Code is a one-command step users kept missing (it was
    // Command-Palette-only). This chip makes it a visible one click — shown ONLY while this folder has no semanticus
    // MCP entry, and it disappears the moment it's connected. Paired with the Model view-title plug button.
    aiConnectStatus = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 97);
    aiConnectStatus.command = 'semanticus.connectClaudeCode';
    aiConnectStatus.text = '$(plug) Connect AI Assistant';
    aiConnectStatus.tooltip = 'Wire your AI Assistant to this Semanticus session. Writes .mcp.json in this folder.';
    context.subscriptions.push(aiConnectStatus, vscode.workspace.onDidChangeWorkspaceFolders(() => refreshAiConnectStatus()));
    refreshAiConnectStatus();
    void maybeOfferConnectAiAssistant(context);

    // canSelectMany: the explorer supports multi-select so "Script ▸", Delete, Hide/Show and Create-hierarchy
    // can operate on a whole selection in one undoable gesture (the agent door already batches; now the UI does too).
    treeView = vscode.window.createTreeView('semanticusModel', { treeDataProvider: tree, canSelectMany: true, dragAndDropController: new ModelTreeDnd() });
    // Folder nodes carry no properties of their own. An empty/objectless selection means the model itself — model
    // settings are always reachable without adding a synthetic root node to the native tree.
    treeView.onDidChangeSelection((e) => { void propGrid.showObjects([...e.selection].filter((n) => n.kind !== 'dfolder')); });

    // The Reference Model tree: point it at a second model (file / git ref / published workspace) to browse its
    // measures/columns/calc-items and copy them into the open model (right-click, or Ctrl+C here → Ctrl+V in Model).
    referenceTree = new ReferenceTreeProvider();
    referenceView = vscode.window.createTreeView('semanticusReference', { treeDataProvider: referenceTree, canSelectMany: true });

    context.subscriptions.push(
        out,
        status,
        healthStatus,
        syncStatus,
        treeView,
        vscode.window.registerWebviewViewProvider('semanticusProperties', propGrid),
        vscode.workspace.registerFileSystemProvider(DAX_SCHEME, daxFs, { isCaseSensitive: true }),
        vscode.languages.registerCompletionItemProvider('dax', daxCompletionProvider, '[', "'", '(', '.'),
        vscode.languages.registerSignatureHelpProvider('dax', daxSignatureProvider, '(', ','),
        vscode.languages.registerDocumentDropEditProvider('dax', daxDropProvider),
        vscode.languages.registerHoverProvider('dax', daxHoverProvider),
        vscode.languages.registerDefinitionProvider('dax', daxDefinitionProvider),
        vscode.languages.registerDocumentSymbolProvider('dax', daxDocumentSymbolProvider),
        vscode.languages.registerDocumentFormattingEditProvider('dax', daxFormatProvider),
        vscode.languages.registerDocumentRangeFormattingEditProvider('dax', daxRangeFormatProvider),
        vscode.commands.registerCommand('semanticus.formatDaxOnline', () => formatDaxOnlineCmd(context)),
        daxDiagnostics,
        vscode.workspace.onDidOpenTextDocument((d) => validateDaxDoc(d)),
        vscode.workspace.onDidChangeTextDocument((e) => scheduleDaxValidate(e.document)),
        // HIGH 3 / CRITICAL 2: closing a doc always prunes its IN-MEMORY caches (diagnostics + the header-uri set). The
        // PERSISTED pending-rename record is cleared only on a CLEAN close (!isDirty): a window reload / hot-exit restores
        // the doc STILL DIRTY (isDirty stays true) — the lifecycle the record must survive — whereas a clean close means the
        // user discarded the unsaved DAX (or it was already saved), so no dirty editor can still resume/save against a
        // recreated same-name object; safe to drop the record, INCLUDING a CRITICAL 2 tombstone. A record whose doc is
        // closed while the extension is inactive is dropped instead by the activation sweep (sweepClosedPendingRenames).
        vscode.workspace.onDidCloseTextDocument((d) => {
            daxDiagnostics.delete(d.uri);
            daxHeaderDocs.delete(d.uri.toString());
            if (!d.isDirty) void clearPendingRename(d.uri.toString());
        }),
        vscode.commands.registerCommand('semanticus.refresh', () => tree.refresh()),
        vscode.commands.registerCommand('semanticus.findInModel', () => findInModelCmd(context)),
        vscode.commands.registerCommand('semanticus.editDax', (n: TreeNode) => openDax(n)),
        vscode.commands.registerCommand('semanticus.openModel', () => openModelCommand(tree)),
        vscode.commands.registerCommand('semanticus.openStudio', () => openStudio(context)),
        // The native sync chip's click: open Studio → Compare, seeded with what you're editing vs querying (the
        // webview computes the seed from its live connection state — 'seed' is the pseudo-target it recognises).
        vscode.commands.registerCommand('semanticus.openCompareSeeded', () => navigateStudio(context, 'compare', 'seed')),
        vscode.commands.registerCommand('semanticus.save', () => saveCommand()),
        vscode.commands.registerCommand('semanticus.saveToLive', () => saveToLiveCommand()),
        vscode.commands.registerCommand('semanticus.restartEngine', () => restartEngineCmd(context)),
        vscode.commands.registerCommand('semanticus.activateLicense', () => activateLicenseCmd(context)),
        vscode.commands.registerCommand('semanticus.showLicense', () => showLicenseCmd()),
        vscode.commands.registerCommand('semanticus.undo', async () => { await conn?.sendRequest('undo'); tree.refresh(); }),
        vscode.commands.registerCommand('semanticus.redo', async () => { await conn?.sendRequest('redo'); tree.refresh(); }),
        // Health chip click (feature #4): a WORSENING chip offers a one-click Undo of the change that moved health
        // (routing to the SAME undo op as Semanticus: Undo, so it genuinely reverts) alongside opening the scan;
        // any other chip just opens the AI Readiness tab (its prior behaviour). Plain-English, never a raw op name.
        vscode.commands.registerCommand('semanticus.healthChipAction', () => healthChipActionCmd()),
        // Object authoring + editing from the tree — each calls a typed engine op (origin=human) so the human UI and
        // the user's Claude drive the SAME ops on one live session (every change broadcasts model/didChange both ways).
        // Create (root + per-table). Data-column create is intentionally absent: a bare DataColumn with no source has
        // no meaning in an import model — columns come from the source/M or as a *calculated* column.
        vscode.commands.registerCommand('semanticus.newTable', () => authorNewTable()),
        vscode.commands.registerCommand('semanticus.newCalcTable', () => authorNewCalcTable()),
        vscode.commands.registerCommand('semanticus.newCalcGroup', () => authorNewCalcGroup()),
        vscode.commands.registerCommand('semanticus.newRole', () => authorNewRole()),
        vscode.commands.registerCommand('semanticus.newFunction', () => authorNewFunction()),
        vscode.commands.registerCommand('semanticus.newMeasure', (n: TreeNode) => authorNewMeasure(n)),
        // Display folders: New Folder... (table/calc group/folder), Rename Folder... (folder), Move to Folder...
        // (measure/column/hierarchy) — all through the engine's atomic display-folder ops (one gesture = one undo).
        vscode.commands.registerCommand('semanticus.newFolder', (n: TreeNode) => newFolderCmd(n)),
        vscode.commands.registerCommand('semanticus.renameFolder', (n: TreeNode) => renameFolderCmd(n)),
        vscode.commands.registerCommand('semanticus.moveToFolder', (n: TreeNode, ns?: TreeNode[]) => moveToFolderCmd(n, ns)),
        vscode.commands.registerCommand('semanticus.newCalcColumn', (n: TreeNode) => authorNewCalcColumn(n)),
        vscode.commands.registerCommand('semanticus.newHierarchy', (n: TreeNode) => authorNewHierarchy(n)),
        vscode.commands.registerCommand('semanticus.newCalcItem', (n: TreeNode) => authorNewCalcItem(n)),
        vscode.commands.registerCommand('semanticus.hierarchyFromColumns', (n: TreeNode, ns?: TreeNode[]) => hierarchyFromColumnsCmd(n, ns)),
        vscode.commands.registerCommand('semanticus.newRelationship', (n: TreeNode) => authorNewRelationship(n)),
        // Universal edit/navigation (single object). DAX editing reuses semanticus.editDax (opens Monaco).
        // F2 / Rename routes to the Properties grid's Name row (the grid IS the rename surface); the old
        // InputBox prompt stays available as its own command so keyboard-only rename is never lost.
        vscode.commands.registerCommand('semanticus.renameObject', (n: TreeNode, ns?: TreeNode[]) => renameInPropertiesCmd(n, ns)),
        vscode.commands.registerCommand('semanticus.renameObjectInputBox', (n: TreeNode) => renameCmd(n)),
        vscode.commands.registerCommand('semanticus.duplicateObject', (n: TreeNode) => authorDuplicate(n)),
        // Model-tree copy/paste: Copy stashes a reference (kind + ref), Paste duplicates via the engine's
        // duplicate_object — same-container beside the original, a measure onto another table lands there.
        vscode.commands.registerCommand('semanticus.copyObject', (n: TreeNode) => copyObjectCmd(n)),
        vscode.commands.registerCommand('semanticus.pasteObject', (n: TreeNode) => pasteObjectCmd(n)),
        vscode.commands.registerCommand('semanticus.showDependents', (n: TreeNode) => showDependentsCmd(n)),
        vscode.commands.registerCommand('semanticus.editProperties', (n: TreeNode) => revealProperties(n)),
        // Script ▸ / Hide / Show / Delete — selection-aware (operate on the whole multi-selection).
        vscode.commands.registerCommand('semanticus.scriptDax', (n: TreeNode, ns?: TreeNode[]) => scriptObjectsCmd('dax', n, ns)),
        vscode.commands.registerCommand('semanticus.scriptTmdl', (n: TreeNode, ns?: TreeNode[]) => scriptObjectsCmd('tmdl', n, ns)),
        vscode.commands.registerCommand('semanticus.scriptTmsl', (n: TreeNode, ns?: TreeNode[]) => scriptObjectsCmd('tmsl', n, ns)),
        vscode.commands.registerCommand('semanticus.applyDaxScript', () => applyDaxScriptCmd()),
        vscode.commands.registerCommand('semanticus.applyTmdlScript', () => applyTmdlScriptCmd()),
        vscode.commands.registerCommand('semanticus.hideObject', (n: TreeNode, ns?: TreeNode[]) => setHiddenCmd(true, n, ns)),
        vscode.commands.registerCommand('semanticus.showObject', (n: TreeNode, ns?: TreeNode[]) => setHiddenCmd(false, n, ns)),
        vscode.commands.registerCommand('semanticus.deleteObject', (n: TreeNode, ns?: TreeNode[]) => authorDelete(n, ns)),
        // Measure-specific.
        vscode.commands.registerCommand('semanticus.setFormat', (n: TreeNode) => setFormatCmd(n)),
        vscode.commands.registerCommand('semanticus.timeIntelligence', (n: TreeNode) => timeIntelCmd(n)),
        // Column-specific.
        vscode.commands.registerCommand('semanticus.summarizeBy', (n: TreeNode) => summarizeByCmd(n)),
        vscode.commands.registerCommand('semanticus.dataCategory', (n: TreeNode) => dataCategoryCmd(n)),
        // Table-specific.
        vscode.commands.registerCommand('semanticus.markDateTable', (n: TreeNode) => markDateTableCmd(n)),
        vscode.commands.registerCommand('semanticus.previewTableData', (n: TreeNode) => previewTableDataCmd(context, n)),
        vscode.commands.registerCommand('semanticus.updateSchema', (n?: TreeNode) => updateSchemaCmd(context, n)),
        vscode.commands.registerCommand('semanticus.addToDiagram', (n: TreeNode, ns?: TreeNode[]) => addToDiagramCmd(context, n, ns)),
        // Studio jumps: right-click a tree object and land on the Studio tab that owns it (a key discoverability win —
        // the tree is the index, Studio is the workbench). All route through navigateStudio (opens + flushes nav).
        vscode.commands.registerCommand('semanticus.showLineage', (n: TreeNode) => { if (n?.ref) navigateStudio(context, 'lineage', n.ref); }),
        vscode.commands.registerCommand('semanticus.editMCode', (n: TreeNode) => {
            try {
                // A partition ref ('partition:<Table>/<Name>') flows through whole — the M lane's target ids use the
                // same format, so the jump lands on the CLICKED partition instead of the table's first one.
                if (n?.ref && refParts(n.ref).kind === 'partition') { navigateStudio(context, 'mcode', n.ref); return; }
                const t = tableOfNode(n);
                if (t) navigateStudio(context, 'mcode', `table:${t}`);
                else vscode.window.showInformationMessage('Right-click a table or partition to edit its M code.');
            } catch (e: any) { vscode.window.showErrorMessage('Edit M Code failed: ' + (e?.message ?? e)); }   // never silent
        }),
        // ---- Keyboard-shortcuts suite (docs/keyboard-shortcuts.md). These are thin navigation commands so every
        // gesture has a REMAPPABLE command behind it: the package.json keybindings (scoped by `when` so Semanticus
        // never grabs a key outside its own surfaces) and the in-Studio handler both land here or in the webview's
        // own goTab. Studio-bound commands ride navigateStudio, so they open Studio cold and queue until it mounts.
        vscode.commands.registerCommand('semanticus.studioSearch', () => navigateStudio(context, 'search', '')),          // '' = "focus the find box" (no query hand-off)
        vscode.commands.registerCommand('semanticus.studioNextTab', () => navigateStudio(context, 'cycle:next')),
        vscode.commands.registerCommand('semanticus.studioPrevTab', () => navigateStudio(context, 'cycle:prev')),
        vscode.commands.registerCommand('semanticus.studioGoGroup', (g: string) => navigateStudio(context, 'group:' + (g || 'understand'))),
        vscode.commands.registerCommand('semanticus.studioGoTab', (t: string) => { if (t) navigateStudio(context, t); }),  // power users: bind your own key to any tab id
        vscode.commands.registerCommand('semanticus.scanReadiness', () => navigateStudio(context, 'readiness', 'rescan')),
        vscode.commands.registerCommand('semanticus.scanBpa', () => navigateStudio(context, 'bpa')),                       // the BPA tab scans on open
        vscode.commands.registerCommand('semanticus.keyboardShortcuts', () => navigateStudio(context, 'shortcuts')),       // the '?' cheat sheet
        vscode.commands.registerCommand('semanticus.amFieldParameter', () => navigateStudio(context, 'advmodels', 'area:fieldparams')),
        vscode.commands.registerCommand('semanticus.amCalcGroup', () => navigateStudio(context, 'advmodels', 'area:calcgroups')),
        vscode.commands.registerCommand('semanticus.amCalendar', () => navigateStudio(context, 'advmodels', 'area:calendars')),
        vscode.commands.registerCommand('semanticus.amPerspectives', () => navigateStudio(context, 'advmodels', 'area:perspectives')),
        vscode.commands.registerCommand('semanticus.amRlsOls', () => navigateStudio(context, 'advmodels', 'area:rlsols')),
        vscode.commands.registerCommand('semanticus.amDaxLib', () => navigateStudio(context, 'advmodels', 'area:daxlib')),
        // Ship-gater: wire the user's own Claude Code (a separate product that reads only its OWN .mcp.json) to this engine.
        vscode.commands.registerCommand('semanticus.connectClaudeCode', () => connectClaudeCodeCmd()),
        vscode.commands.registerCommand('semanticus.setReferenceModel', () => setReferenceModelCmd()),
        vscode.commands.registerCommand('semanticus.refreshReferenceModel', () => loadReferenceModel()),
        vscode.commands.registerCommand('semanticus.clearReferenceModel', () => clearReferenceModel()),
        vscode.commands.registerCommand('semanticus.copyRefToModel', (n: TreeNode, ns?: TreeNode[]) => copyRefIntoModel(ns?.length ? ns : [n])),
        vscode.commands.registerCommand('semanticus.copyRefObject', () => copyRefStash()),
        vscode.commands.registerCommand('semanticus.pasteIntoModel', () => pasteIntoModelCmd()),
        // Partition-specific.
        vscode.commands.registerCommand('semanticus.refreshPartition', (n: TreeNode) => refreshPartitionCmd(n)),
        // Relationship-specific.
        vscode.commands.registerCommand('semanticus.relCrossFilter', (n: TreeNode) => relCrossFilterCmd(n)),
        vscode.commands.registerCommand('semanticus.relToggleActive', (n: TreeNode) => relToggleActiveCmd(n)),
        // Role-specific.
        vscode.commands.registerCommand('semanticus.roleModelPermission', (n: TreeNode) => roleModelPermCmd(n)),
        vscode.commands.registerCommand('semanticus.roleAddRls', (n: TreeNode) => roleAddRlsCmd(n)),
        vscode.commands.registerCommand('semanticus.roleAddMember', (n: TreeNode) => roleAddMemberCmd(n)),
    );
    context.subscriptions.push(vscode.commands.registerCommand('semanticus.manageLicense', () => manageLicenseCmd()));

    await autoHealSemanticusMcpEntry(context);
    await connectEngine(context);
}

export function deactivate() {
    deactivating = true;
    cancelScheduledReconnect();
    connectionEpoch++;
    const previous = conn;
    conn = undefined;   // clear first: its onClose callback must not schedule a replacement during shutdown
    try { previous?.dispose(); } catch { /* ignore */ }
    // Only kill the engine if WE spawned it.
    if (spawned && spawned.pid) { try { spawned.kill(); } catch { /* ignore */ } }
}

// ---- engine lifecycle ------------------------------------------------------------------------

function workspaceDir(): string {
    // Prefer the open folder; else the engine repo (derived from the DLL path) — both are writable. Never
    // process.cwd(): for an Extension Development Host that can be a non-writable dir, so the engine can't
    // publish .semanticus/engine.json there and the connection silently times out.
    const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    return folder ?? repoFromDll() ?? process.cwd();
}

const LICENSE_SECRET_KEY = 'semanticus.licenseToken';   // the token's home now: VS Code SecretStorage (OS keychain), NOT settings.json
const DEFAULT_LICENSE_MANAGE_URL = 'https://semanticus.com.au/pro'; // fallback for an older engine that predates manageUrl
let licenseMigrationNoticeShown = false;                 // one-shot guard so the "moved to secure storage" toast fires at most once per session

/// Guard for the conflict prompt, keyed on the exact (legacy, secret) pair being shown — a NEW pair prompts again
/// in the same session (a session-wide boolean would suppress a genuinely different conflict), and a prompt that
/// settles STALE or DISMISSED clears the key so the follow-up reconciliation it deferred to isn't lost until
/// restart. An applied (or keychain-failed) prompt keeps its key: re-asking a question the user already answered
/// for the same pair would nag.
let licenseConflictPromptedKey: string | undefined;

/// Monotonic revision of license state, bumped INSIDE the locked work of every mutation that actually COMMITS a
/// change (verified secret store, verified secret delete/rollback, settings token write, settings scope clear).
/// Every flow that spans a prompt captures it when it reads the state it will act on; its locked apply-work checks
/// it FIRST and aborts honestly when anything committed in between. This is what makes stale INTENT detectable:
/// the mutex serializes writes but cannot invalidate a choice captured before someone else committed — and value
/// equality is NOT identity (a second activation can store the SAME token; only the revision tells the histories apart).
let licenseMutationRevision = 0;

/// The attempt EPOCH: bumped BEFORE every potentially mutating SecretStorage call (store AND delete), even when the
/// call later fails or its verification read fails. The commit revision above cannot see a HALF-store (stored but
/// unverified, so no revision bump), and a parked flow comparing only the revision could act over someone else's
/// failed attempt that left the same VALUE in the slot. Prompt-spanning flows capture the epoch alongside the
/// revision and their applies check BOTH; a flow's own store captures the epoch AFTER its own bump, so its own
/// rollback stays eligible while anyone else's attempt (successful or not) invalidates it.
let licenseMutationAttemptEpoch = 0;

/// Single-flight guard for INTERACTIVE license flows (Activate/Clear license): held for the ENTIRE interactive
/// lifespan — command entry, the input box, any parked consent modal — until the flow fully settles. A second
/// interactive invocation while one is in flight is refused outright, and the background conflict prompt refuses to
/// OPEN while this is held (it defers without setting its pair guard, so a later read re-offers against fresh
/// state). Opening an interactive flow also settles any already-open conflict prompt by releasing its pair guard —
/// that prompt's parked apply aborts via its own revision/epoch/value checks if this flow changes anything. With
/// this, at most ONE prompt-spanning license mutation flow exists at a time; the only concurrent actors left are
/// the background migration (revision+epoch guarded) and writers outside this extension (value-guarded).
let licenseInteractiveFlowActive = false;

/// EVERY mutation of license state (SecretStorage store/delete and settings.json token writes/clears) runs through
/// this single promise chain, so check-then-act sequences cannot interleave: a parked conflict handler, a rollback
/// waiting behind a cancelled modal, and a fresh activation all serialize, and each re-reads current state UNDER
/// the lock to re-check its own precondition before touching anything.
/// Rules: acquire AFTER user prompts resolve (never hold the chain across a prompt), and never nest — work passed
/// here must not itself call withLicenseMutation (the chain is not reentrant; nesting deadlocks it).
let licenseMutationChain: Promise<unknown> = Promise.resolve();
function withLicenseMutation<T>(work: () => Promise<T>): Promise<T> {
    const run = licenseMutationChain.then(work);
    licenseMutationChain = run.then(() => undefined, () => undefined);   // a failed mutation never breaks the chain
    return run;
}

/// The Pro license token, read securely with a clear preference order and a one-time migration OFF plaintext settings:
///   1) SecretStorage (context.secrets — the OS keychain), the authoritative home going forward;
///   2) the legacy `semanticus.licenseToken` SETTING (plaintext in settings.json) — if found, MIGRATE it into secrets
///      (VERIFIED read-back first) and offer (non-modally, once) to remove the plaintext copy; never a silent delete;
///   3) neither → '' (free tier). Setting a token is the explicit "Activate License" command, never an auto-prompt here.
/// Single source of truth for BOTH the engine-spawn --license arg and the .mcp.json writer, so both doors agree on tier.
/// opts.reconcile=false makes this a PURE read: no migration into secrets, no cleanup offer, no conflict prompt.
/// The Activate License prefill uses it — a fire-and-forget prompt launched from the prefill could sit open while
/// the user activates a DIFFERENT token, then apply its stale answer over the new one.
async function getLicenseToken(context: vscode.ExtensionContext = extCtx, opts?: { reconcile?: boolean }): Promise<string> {
    const reconcile = opts?.reconcile !== false;
    let secret = '';
    try { secret = (await context.secrets.get(LICENSE_SECRET_KEY))?.trim() || ''; }
    catch { /* keychain unavailable — fall through to the legacy setting */ }

    const legacy = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();

    if (secret) {
        // A secret exists (the authoritative home). Do NOT resolve silently while a plaintext legacy setting also
        // exists — it could be a leftover copy, OR a NEWER token the user pasted into settings.json that the stored
        // secret would otherwise silently shadow. Handle both cases (non-blocking; startup never waits on the answer):
        if (reconcile && legacy && legacy === secret) {
            // Same token in both places — just the leftover plaintext copy. Offer the one-time cleanup consent
            // flow, scoped to THIS token's value: scopes holding a different token are never part of the offer.
            void clearLegacyLicenseSetting(true, secret);
        } else if (reconcile && legacy && legacy !== secret) {
            // The two tokens differ, so one is out of date. Surface it and let the user pick which wins.
            reconcileLicenseConflict(context, legacy, secret);
        }
        return secret;
    }

    if (!legacy) return '';

    // No secret yet — migrate the plaintext token into secure storage, but only OFFER to remove the settings copy
    // once the secret provably reads back. On a failed keychain write the setting stays untouched (still the only
    // usable token) and the migration quietly retries on a later read. Non-blocking: startup never waits on the answer.
    if (reconcile) {
        // Under the mutation lock, re-read BOTH preconditions before migrating: secure storage must STILL be empty
        // (an activation that stored a new token while this read awaited the lock must never be overwritten), AND
        // the setting must still hold the value we captured (a changed setting means this migration's input is
        // stale — a later read retries against the world as it is then).
        const migrated = await withLicenseMutation(async () => {
            try { if (((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim()) return false; } catch { return false; }
            const legacyNow = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();
            if (legacyNow !== legacy) return false;
            return storeLicenseSecretVerified(context, legacy);
        });
        if (migrated) void clearLegacyLicenseSetting(true, legacy);
    }
    return legacy;
}

/// The keychain and the plaintext setting hold DIFFERENT Pro tokens, so one is out of date. Surface the conflict
/// (non-modal, once per distinct pair — see licenseConflictPromptedKey) and let the user decide which wins, rather
/// than letting the stored token silently shadow a token the user may have just pasted into settings.json. Either
/// choice ends with the plaintext copies OF THAT VALUE cleaned up (value-guarded: scopes holding some third token
/// are kept and reported, never deleted).
/// STALENESS GUARD: this notice can sit open indefinitely while the user activates a different token. The whole
/// apply-phase runs as ONE unit under the license-mutation lock; INSIDE it, the revision captured at prompt time is
/// checked first (any commit in between invalidates the choice, even one that re-stored identical values), then both
/// copies are re-read and compared to the captured pair (out-of-band writers never bump the revision). On any
/// mismatch it abandons without altering anything and releases the prompt guard so the new state can be re-offered.
function reconcileLicenseConflict(context: vscode.ExtensionContext, legacyAtPrompt: string, secretAtPrompt: string): void {
    // Defer while an interactive license flow is in flight: that flow is about to change the very state this
    // prompt would reconcile. The pair guard is NOT set, so a read AFTER the flow settles re-offers fresh.
    if (licenseInteractiveFlowActive) return;
    const promptKey = `${legacyAtPrompt}\u0000${secretAtPrompt}`;
    if (licenseConflictPromptedKey === promptKey) return;   // this exact pair is already prompted (or answered)
    licenseConflictPromptedKey = promptKey;
    const revisionAtPrompt = licenseMutationRevision;
    const epochAtPrompt = licenseMutationAttemptEpoch;
    const releaseGuard = () => { if (licenseConflictPromptedKey === promptKey) licenseConflictPromptedKey = undefined; };
    void vscode.window.showWarningMessage(
        'Semanticus found two different Pro license tokens: one in secure storage and one in settings.json (plaintext). They do not match, so one is out of date. Which should be used?',
        'Use settings value', 'Keep stored license',
    ).then(async (pick) => {
        if (pick !== 'Use settings value' && pick !== 'Keep stored license') {
            // Dismissed (Escape): leave both tokens as-is, but release the guard so the reconciliation this prompt
            // deferred can be offered again without a restart.
            releaseGuard();
            return;
        }

        // Acquire the lock only now (after the choice resolved), then validate and apply atomically. Stale and
        // failure outcomes carry the EVIDENCE that produced them, so the copy never overclaims: "a newer license
        // change was made" is reserved for a revision bump or a value difference; an epoch-only trip only proves
        // another operation was ATTEMPTED; a failed read proves nothing at all.
        type ConflictOutcome =
            | 'stale-committed' | 'stale-attempted' | 'stale-unverified'
            | 'store-failed-original' | 'store-failed-unverified' | 'store-failed-unreadable'
            | 'applied-cleared' | 'applied-partial';
        const outcome = await withLicenseMutation(async (): Promise<ConflictOutcome> => {
            // Stale-intent check FIRST: any COMMITTED mutation (revision) or even ATTEMPTED secret write (epoch)
            // since this prompt was shown invalidates the choice — value equality is not identity, and a half-store
            // that failed verification leaves no revision trace but must still void this parked decision.
            if (licenseMutationRevision !== revisionAtPrompt) return 'stale-committed';
            if (licenseMutationAttemptEpoch !== epochAtPrompt) return 'stale-attempted';
            // Value re-checks as defense in depth against writers OUTSIDE this extension's mutex (Settings Sync,
            // another window, a hand edit) — those never bump the revision or the epoch.
            let secretNow = '';
            try { secretNow = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim(); } catch { return 'stale-unverified'; }
            const legacyNow = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();
            if (secretNow !== secretAtPrompt || legacyNow !== legacyAtPrompt) return 'stale-committed';

            if (pick === 'Use settings value' && !(await storeLicenseSecretVerified(context, legacyAtPrompt))) {
                // The store can MUTATE secure storage and then fail its verification read, so "nothing changed"
                // must be PROVEN, never assumed. Proof-read, still under the lock, and classify.
                try {
                    const now = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim();
                    return now === secretAtPrompt ? 'store-failed-original' : 'store-failed-unverified';
                } catch { return 'store-failed-unreadable'; }
            }
            // Both choices retire the plaintext copies of the SETTINGS value (it either just moved into secure
            // storage, or the user explicitly chose to discard it in favour of the stored license).
            const res = await clearLegacyScopes(legacyAtPrompt);
            return expectedFullyCleared(res) && res.expectedCleared.length > 0 ? 'applied-cleared' : 'applied-partial';
        });

        if (outcome === 'stale-committed' || outcome === 'stale-attempted' || outcome === 'stale-unverified') {
            // Release the guard: the world moved on, and the conflict that NOW exists (if any) deserves its own prompt.
            releaseGuard();
            vscode.window.showInformationMessage(
                outcome === 'stale-committed'
                    ? 'Semanticus: a newer license change was made since this notice was shown, so nothing was altered. Run "Semanticus: Show license" to see what is active.'
                    : outcome === 'stale-attempted'
                        ? 'Semanticus: another license operation was attempted since this notice was shown, so this decision was not applied. Run "Semanticus: Show license" to see what is active.'
                        : 'Semanticus: the license state could not be re-checked, so this decision was not applied. Run "Semanticus: Show license" to see what is active.');
        } else if (outcome === 'store-failed-original') {
            // The pair is UNRESOLVED — keeping the guard would hide a live conflict until reload, so release it
            // for a later re-offer. The proof-read showed the original secret, so "nothing changed" is truthful.
            releaseGuard();
            vscode.window.showWarningMessage('Semanticus: could not move the settings.json token into secure storage, and secure storage still holds the previously stored license, so nothing changed. The previously stored license remains authoritative and the settings.json token is still in plaintext. This will be offered again; you can also retry after your keychain is available.');
        } else if (outcome === 'store-failed-unverified') {
            releaseGuard();
            vscode.window.showWarningMessage('Semanticus: the move to secure storage could not be verified, so either token may now be in secure storage. Nothing was removed from settings.json. Run "Semanticus: Show license" to see what is active, and activate the license you want once secure storage works. This will be offered again.');
        } else if (outcome === 'store-failed-unreadable') {
            releaseGuard();
            vscode.window.showWarningMessage('Semanticus: the move to secure storage failed and secure storage could not be read back, so its current state is unknown. Nothing was removed from settings.json. Reload the window, run "Semanticus: Show license" to see what is active, then activate the license you want. This will be offered again.');
        } else if (pick === 'Use settings value') {
            vscode.window.showInformationMessage(
                'Semanticus: the settings.json token is now stored in secure storage.' +
                (outcome === 'applied-cleared' ? ' The plaintext copy was removed.' : '') +
                ' Restart the engine, or reload the window, to activate it.');
        } else if (outcome === 'applied-cleared') {
            vscode.window.showInformationMessage('Semanticus: kept the license in secure storage and removed the plaintext copy from settings.json.');
            // On a partial clear, clearLegacyScopes already reported exactly which scopes survived or were kept.
        }
    });
}

/// Store the token in SecretStorage and VERIFY it reads back. Callers must not touch the legacy plaintext setting
/// unless this returns true — the keychain can be missing or locked (headless/portable setups), and a store() that
/// silently failed followed by a settings delete would strand the user with no usable token anywhere.
/// Always called under the license-mutation lock. The attempt EPOCH bumps BEFORE the store (even a failed or
/// half store is an attempt other parked flows must see); a VERIFIED store additionally bumps
/// licenseMutationRevision (it commits). A failed/unverified store deliberately does NOT bump the revision — the
/// failing attempt's own parked rollback must still pass its revision+epoch check (it captures the epoch after
/// this bump) to clean up the half-store, while every OTHER parked flow is invalidated by the epoch alone.
async function storeLicenseSecretVerified(context: vscode.ExtensionContext, token: string): Promise<boolean> {
    licenseMutationAttemptEpoch++;
    try {
        await context.secrets.store(LICENSE_SECRET_KEY, token);
        const ok = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim() === token;
        if (ok) licenseMutationRevision++;
        return ok;
    } catch { return false; }
}

/// One config scope that DEFINES the plaintext token: the configuration object to update THROUGH, the target to
/// clear at, the trimmed VALUE it holds, the folder URI (folder scopes only, for fresh re-reads), and a
/// human-readable label for prompts and survivor reports. The config object matters: in a multi-root workspace an
/// UNSCOPED inspect() cannot see a folder-level value, and an unscoped update(..., WorkspaceFolder) cannot target a
/// folder — every folder scope must be probed AND cleared through a configuration scoped to that folder's URI. The
/// VALUE matters just as much: different scopes can hold DIFFERENT tokens, and a value-blind clear would destroy a
/// credential that was never migrated or shown as a conflict.
interface LegacyTokenScope { cfg: vscode.WorkspaceConfiguration; target: vscode.ConfigurationTarget; label: string; value: string; uri?: vscode.Uri; }

/// What a value-guarded clear actually did: which scopes holding the expected token are provably gone, and which
/// scopes were deliberately or unavoidably left, each with WHY (a different token / changed mid-clear / failed).
interface ClearScopesResult { expectedCleared: string[]; keptOther: { scope: string; why: string }[]; }

/// True when the expected token no longer survives anywhere a clear could see: nothing failed to clear. (Scopes
/// kept for holding a DIFFERENT token never held the expected one, so they don't count against this.)
function expectedFullyCleared(res: ClearScopesResult): boolean {
    return !res.keptOther.some((k) => k.why === 'could not be removed');
}

/// A plaintext `semanticus.licenseToken` can be defined at more than one config scope: Global, Workspace, and any
/// workspace FOLDER's .vscode/settings.json (possibly git-tracked). Return every scope that actually defines it —
/// global/workspace via the unscoped config, folder values via a per-folder URI-scoped config — with each scope's
/// own value, so the clear path can hit each at its OWN target through its OWN config, and only when the value is
/// actually the token being cleaned up.
function definedLegacyScopes(): LegacyTokenScope[] {
    const scopes: LegacyTokenScope[] = [];
    const base = vscode.workspace.getConfiguration('semanticus');
    const info = base.inspect<string>('licenseToken');
    const globalValue = (info?.globalValue ?? '').trim();
    if (globalValue) scopes.push({ cfg: base, target: vscode.ConfigurationTarget.Global, label: 'user settings', value: globalValue });
    const workspaceValue = (info?.workspaceValue ?? '').trim();
    if (workspaceValue) scopes.push({ cfg: base, target: vscode.ConfigurationTarget.Workspace, label: 'workspace settings', value: workspaceValue });
    // Folder scopes exist as DISTINCT settings files only in a multi-root workspace (a .code-workspace is open).
    // In a single-folder window the folder's .vscode/settings.json IS the workspace settings — already captured as
    // workspaceValue above; probing the folder again would double-report (and double-clear) the same file.
    if (vscode.workspace.workspaceFile !== undefined) {
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            const fcfg = vscode.workspace.getConfiguration('semanticus', folder.uri);
            const folderValue = (fcfg.inspect<string>('licenseToken')?.workspaceFolderValue ?? '').trim();
            if (folderValue) scopes.push({ cfg: fcfg, target: vscode.ConfigurationTarget.WorkspaceFolder, label: `workspace folder '${folder.name}'`, value: folderValue, uri: folder.uri });
        }
    }
    return scopes;
}

/// The CURRENT value at one specific scope, read through a brand-new configuration object (a captured config can
/// serve stale values after an external write). Used to narrow the check-to-update window per target.
function freshValueAt(scope: LegacyTokenScope): string {
    const info = vscode.workspace.getConfiguration('semanticus', scope.uri).inspect<string>('licenseToken');
    const v = scope.target === vscode.ConfigurationTarget.Global ? info?.globalValue
        : scope.target === vscode.ConfigurationTarget.Workspace ? info?.workspaceValue
        : info?.workspaceFolderValue;
    return (v ?? '').trim();
}

/// Tell the user which plaintext scopes were deliberately LEFT ALONE because they hold a token other than the one
/// being cleaned up — never a silent keep, never a silent delete. No-op on an empty list. (No "activate to replace"
/// advice here: activation deliberately preserves different-valued scopes, so it would not replace these.)
function reportKeptScopes(kept: LegacyTokenScope[]): void {
    if (kept.length === 0) return;
    const labels = kept.map((s) => s.label).join(', ');
    vscode.window.showWarningMessage(
        `Semanticus kept ${labels}: ${kept.length === 1 ? 'it holds' : 'they hold'} a different license token than the one being cleaned up. If that token is stale, open that settings.json and delete the "semanticus.licenseToken" entry yourself.`,
    );
}

/// Clear the plaintext token from every scope whose CURRENT value equals `expected` — each through ITS OWN config
/// object, re-inspecting THAT target immediately before its update (Settings Sync or another extension can rewrite
/// a scope between snapshot and update; the mutex cannot cover writers outside this extension). VALUE-GUARDED: a
/// scope holding a DIFFERENT token (or one that changed mid-clear) is kept and REPORTED, never deleted. Returns the
/// structured outcome; failures to clear warn loudly by name. Bumps licenseMutationRevision when anything committed.
/// Call ONLY under the license-mutation lock: the fresh reads here ARE the under-lock precondition re-check.
async function clearLegacyScopes(expected: string): Promise<ClearScopesResult> {
    const result: ClearScopesResult = { expectedCleared: [], keptOther: [] };
    if (!expected) return result;   // never treat "clear anything" as a valid request
    const scopes = definedLegacyScopes();
    const attempted: LegacyTokenScope[] = [];
    for (const scope of scopes) {
        if (scope.value !== expected) {
            result.keptOther.push({ scope: scope.label, why: 'holds a different license token' });
            continue;
        }
        // Last-instant re-inspect of THIS target: skip (and report) if its value moved since the snapshot.
        if (freshValueAt(scope) !== expected) {
            result.keptOther.push({ scope: scope.label, why: 'its value changed while it was being cleared' });
            continue;
        }
        attempted.push(scope);
        try { await scope.cfg.update('licenseToken', undefined, scope.target); }
        catch { /* a throw here just means this scope may survive — detected by the fresh re-inspect below */ }
    }
    // Fresh re-inspect: classify what actually happened at each attempted target, THREE ways — empty is the only
    // outcome that counts as cleared. A scope now holding some OTHER non-empty value was rewritten underneath us
    // (Settings Sync, another window); claiming it "cleared" would hide that a token is still there.
    for (const scope of attempted) {
        const after = freshValueAt(scope);
        if (!after) result.expectedCleared.push(scope.label);
        else if (after === expected) result.keptOther.push({ scope: scope.label, why: 'could not be removed' });
        else result.keptOther.push({ scope: scope.label, why: 'its value changed while it was being cleared' });
    }
    if (result.expectedCleared.length > 0) licenseMutationRevision++;   // something committed
    // Reporting lives here so every caller behaves identically: kept scopes by reason, failures loud.
    const keptDifferent = result.keptOther.filter((k) => k.why !== 'could not be removed');
    if (keptDifferent.length > 0) {
        vscode.window.showWarningMessage(
            `Semanticus kept ${keptDifferent.map((k) => `${k.scope} (${k.why})`).join(', ')}. If that license token is stale, open that settings.json and delete the "semanticus.licenseToken" entry yourself.`,
        );
    }
    const failed = result.keptOther.filter((k) => k.why === 'could not be removed').map((k) => k.scope);
    if (failed.length > 0) {
        vscode.window.showWarningMessage(
            `Semanticus could not remove the plaintext license from ${failed.join(', ')}. Open that settings.json, delete the "semanticus.licenseToken" entry yourself, then run "Semanticus: Show license" to re-verify.`,
        );
    }
    return result;
}

/// The ONE code path that removes the legacy plaintext token from settings.json — both flows share it so the consent
/// semantics stay identical:
///   ask=true  — the silent read-path migration: a one-time non-modal toast ASKS before touching the user's config;
///   ask=false — the explicit Activate/Clear License commands: the user just acted on the token, so the copy is
///               removed without a question, but the CALLER must disclose the removal.
/// VALUE-GUARDED: only scopes holding `expected` (the verified secret, or the value the user explicitly discarded)
/// are offered/cleared; scopes holding a different token are kept and reported (on the explicit path even when
/// nothing matched, so nothing is ever silently kept). Returns true only when something was cleared AND `expected`
/// is provably gone from all scopes.
/// Acquires the license-mutation lock itself — never call while already holding it (use clearLegacyScopes inside).
async function clearLegacyLicenseSetting(ask: boolean, expected: string): Promise<boolean> {
    if (!expected) return false;
    const scopes = definedLegacyScopes();
    const matching = scopes.filter((s) => s.value === expected);
    if (matching.length === 0) {
        // Nothing holds the token being cleaned up. An explicit command still owes the user the truth about other
        // tokens deliberately left in plaintext; a background read stays quiet (the conflict flow owns that case).
        if (!ask) reportKeptScopes(scopes);
        return false;
    }
    if (ask) {
        if (!licenseMigrationNoticeShown) {
            licenseMigrationNoticeShown = true;
            const scopeList = matching.map((s) => s.label).join(', ');
            // This consent can sit parked while an activation replaces the license. Capture the world it was
            // asked about; the locked accept re-checks all of it and aborts honestly instead of clearing the
            // plaintext copy of a token that is no longer the one in secure storage.
            const revisionAtPrompt = licenseMutationRevision;
            const epochAtPrompt = licenseMutationAttemptEpoch;
            void vscode.window.showInformationMessage(
                `Semanticus moved your Pro license into secure storage (the OS keychain). Remove the plaintext copy from settings.json (${scopeList})?`,
                'Remove from settings', 'Keep',
            ).then((pick) => {
                if (pick !== 'Remove from settings') return;
                void withLicenseMutation(async () => {
                    // Classify staleness by its EVIDENCE so the copy never overclaims: a revision bump or a value
                    // difference proves a change; an epoch-only trip proves only an attempt; a failed read proves nothing.
                    const staleness = async (): Promise<'fresh' | 'committed' | 'attempted' | 'unverified'> => {
                        if (licenseMutationRevision !== revisionAtPrompt) return 'committed';
                        if (licenseMutationAttemptEpoch !== epochAtPrompt) return 'attempted';
                        try { return ((await extCtx.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim() !== expected ? 'committed' : 'fresh'; }
                        catch { return 'unverified'; }   // unreadable: cannot prove the keychain still holds this token
                    };
                    const verdict = await staleness();
                    if (verdict !== 'fresh') {
                        // Allow the offer to come back for the CURRENT state on a later read instead of staying
                        // suppressed until restart with its promise unkept.
                        licenseMigrationNoticeShown = false;
                        vscode.window.showInformationMessage(
                            verdict === 'committed'
                                ? 'Semanticus: the license changed after this notice was shown, so the plaintext copy was not removed. The cleanup will be offered again when the license is next read.'
                                : verdict === 'attempted'
                                    ? 'Semanticus: another license operation was attempted after this notice was shown, so the plaintext copy was not removed. The cleanup will be offered again when the license is next read.'
                                    : 'Semanticus: the license could not be re-checked, so the plaintext copy was not removed. The cleanup will be offered again when the license is next read.');
                        return;
                    }
                    await clearLegacyScopes(expected);
                });
            });
        }
        return false;
    }
    return withLicenseMutation(async () => {
        const res = await clearLegacyScopes(expected);
        return res.expectedCleared.length > 0 && expectedFullyCleared(res);
    });
}

/// Mask a bearer secret for display: keep only the last 4 chars so a shared screenshot/paste of the (user-visible)
/// output channel can identify WHICH token is active without leaking it. Short/empty values collapse to '***'.
function maskSecret(s: string): string {
    return !s || s.length <= 4 ? '***' : '***' + s.slice(-4);
}

/// Redact secrets in an args array before it hits the output channel. Returns a copy; the real args handed to
/// spawn()/writeFile() are never touched.
function redactArgs(args: readonly string[]): string[] {
    const out = [...args];
    for (let i = 0; i < out.length - 1; i++) {
        if (out[i] === '--license') out[i + 1] = maskSecret(out[i + 1]);
    }
    return out;
}

/// <repo>/Semanticus.Engine/bin/Debug/net8.0/Semanticus.Engine.dll -> <repo> (4 levels up).
function repoFromDll(): string | undefined {
    if (!extCtx || extCtx.extensionMode !== vscode.ExtensionMode.Development) return undefined;
    const dll = vscode.workspace.getConfiguration('semanticus').get<string>('engineDll') || '';
    if (dll && fs.existsSync(dll)) return path.resolve(path.dirname(dll), '..', '..', '..', '..');
    return undefined;
}

/// Resolve dotnet by absolute path first — a GUI-launched VS Code may have a stripped PATH that omits
/// dotnet even though the integrated terminal finds it. Falls back to bare 'dotnet' (PATH lookup).
function resolveDotnet(): string {
    const cands = [
        process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, 'dotnet.exe') : '',
        process.env.ProgramFiles ? path.join(process.env.ProgramFiles, 'dotnet', 'dotnet.exe') : '',
        'C:\\Program Files\\dotnet\\dotnet.exe',
    ].filter(Boolean) as string[];
    for (const c of cands) { try { if (fs.existsSync(c)) return c; } catch { /* ignore */ } }
    return 'dotnet';
}

/// The engine-resolution ladder (shared by the spawn path and the Connect-Claude-Code writer, so both
/// doors launch the SAME binary):
///   (a) in an Extension Development Host only, semanticus.engineDll explicitly set and present → 'dll';
///   (b) the self-contained engine bundled in the .vsix at <ext>/engine/Semanticus.Engine[.exe] → 'exe'
///       (run directly — that's the point of a per-platform self-contained publish, no .NET runtime needed);
///   (c) neither → a teaching error naming BOTH fixes.
/// Installed extensions never honor the F5 override. Otherwise their visible Marketplace version can silently
/// drive an unrelated checkout's older engine, which defeats artifact verification and can revive fixed defects.
/// The bundled name differs by OS: Windows appends .exe, other platforms don't.
function resolveEngine(): ResolvedEngine {
    const dll = vscode.workspace.getConfiguration('semanticus').get<string>('engineDll') || '';
    const exeName = process.platform === 'win32' ? 'Semanticus.Engine.exe' : 'Semanticus.Engine';
    const exe = path.join(extCtx.extensionPath, 'engine', exeName);
    const mode = extCtx.extensionMode === vscode.ExtensionMode.Development ? 'development' : 'production';
    if (mode === 'production' && dll)
        out.appendLine('Ignoring the F5-only semanticus.engineDll setting in the installed extension.');
    return resolveEngineCandidate({ mode, overrideDll: dll, bundledExecutable: exe, exists: fs.existsSync });
}

function engineInfoPath(ws: string): string {
    return path.join(ws, '.semanticus', 'engine.json');
}

function readEngineInfo(ws: string): EngineInfo | undefined {
    try {
        const raw = fs.readFileSync(engineInfoPath(ws), 'utf8');
        const o = JSON.parse(raw) as Record<string, any>;
        // The engine writes engine.json with PascalCase keys (Newtonsoft default: PipeName/Pid/…), but the rest of
        // the wire protocol is camelCase. Tolerate BOTH so a live engine is never read as { pipeName: undefined }.
        return {
            pipeName: o.pipeName ?? o.PipeName,
            pid: o.pid ?? o.Pid,
            startedUtc: o.startedUtc ?? o.StartedUtc,
            workspace: o.workspace ?? o.Workspace,
            exePath: o.exePath ?? o.ExePath,
        };
    } catch { return undefined; }
}

function pidAlive(pid: number): boolean {
    try { process.kill(pid, 0); return true; }
    catch (e: any) { return e?.code === 'EPERM'; }
}

const delay = (ms: number) => new Promise(r => setTimeout(r, ms));

/// The OS path to the engine's named pipe. Windows uses the Win32 pipe namespace (\\.\pipe\<name>); on Unix, .NET's
/// NamedPipeServerStream is a Unix domain socket at Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name), and
/// CoreFX resolves GetTempPath on Unix as $TMPDIR else /tmp — NOT node's os.tmpdir(), which differs in its fallback
/// ordering (TMP/TEMP) and trailing-slash trimming, so we read the env var explicitly to mirror .NET exactly.
/// Provably INERT on win32: that branch returns the byte-identical previous literal. (POSIX untested on this box.)
function pipePathFor(pipeName: string): string {
    if (process.platform === 'win32') return `\\\\.\\pipe\\${pipeName}`;
    const tmp = process.env.TMPDIR || '/tmp';
    const p = `${tmp.endsWith('/') ? tmp : tmp + '/'}CoreFxPipe_${pipeName}`;   // Path.Combine semantics for an absolute base
    // sun_path is a fixed buffer INCLUDING the terminating NUL: 104 bytes on macOS (103 usable), 108 on Linux
    // (107 usable). Use the platform's REAL ceiling so valid Linux paths (104-107 bytes) are not wrongly rejected by
    // the tighter macOS limit before we even try to connect. Our pipe names are short hashes so this should never
    // fire, but assert it rather than let connect() fail with an inscrutable EINVAL.
    const limit = process.platform === 'darwin' ? 103 : 107;
    const bytes = Buffer.byteLength(p, 'utf8');
    if (bytes > limit) throw new Error(`Engine socket path is too long for a Unix domain socket (${bytes} bytes, limit ${limit} on ${process.platform}): ${p}. Set TMPDIR to a shorter path.`);
    return p;
}

/// Liveness test that actually matters: can we open the engine's pipe? Replaces process.kill(pid,0), which is
/// an unreliable existence check on Windows (it reported a live, listening engine as dead, blocking every attach).
/// Returns null on success, else the failure reason (error code / 'timeout') so callers can LOG why a connect failed.
function tryConnectPipe(pipeName: string): Promise<string | null> {
    return new Promise((resolve) => {
        let settled = false;
        let socket: net.Socket;
        const finish = (reason: string | null) => { if (settled) return; settled = true; try { socket?.destroy(); } catch { /* ignore */ } resolve(reason); };
        socket = net.connect(pipePathFor(pipeName));
        socket.once('connect', () => finish(null));
        socket.once('error', (e: any) => finish(e?.code ?? e?.message ?? 'error'));
        setTimeout(() => finish('timeout-3s'), 3000);
    });
}

async function ensureEngine(ws: string, context: vscode.ExtensionContext, uiChallenge: string): Promise<string> {
    const existing = readEngineInfo(ws);
    const liveOwner = !!existing && (await tryConnectPipe(existing.pipeName)) === null;
    let engine: ResolvedEngine | undefined;
    let resolutionError: unknown;
    try { engine = resolveEngine(); }
    catch (e) { resolutionError = e; }

    if (liveOwner && existing) {
        const mode = context.extensionMode === vscode.ExtensionMode.Development ? 'development' : 'production';
        const decision = decideEngineOwner(engine, existing.exePath, mode, process.platform);
        if (decision === 'mismatch') {
            out.appendLine(`Refusing engine owner mismatch. Expected "${engine!.path}"; running owner reports "${existing.exePath}".`);
            throw new Error('A different Semanticus engine is already running for this folder. Run Semanticus: Restart Engine, then reconnect the AI Assistant.');
        }
        if (decision === 'unresolved') throw resolutionError;
        if (decision === 'legacy') await warnLegacyEngineOwnerOnce(ws, existing, context);
        if (decision === 'development-fallback') {
            out.appendLine(`Development attach fallback: using live engine pid ${existing.pid} because no local launch candidate resolved (${String((resolutionError as any)?.message ?? resolutionError)}).`);
        }
        out.appendLine(`Attaching to running engine (pid ${existing.pid}, pipe ${existing.pipeName}).`);
        return existing.pipeName;
    }

    if (!engine) throw resolutionError;

    // Resolve which binary to launch (dev DLL via dotnet, or the bundled self-contained exe directly). Throws a
    // teaching error naming both fixes when neither is present.
    const serveArgs = ['serve', '--workspace', ws, '--ui-challenge-stdin'];
    // Deliver the Pro license to the OWNER engine we spawn here — entitlement follows the owner, and get_entitlement
    // (plus every Pro chokepoint) reads from THIS process. Previously only the .mcp.json writer passed --license, so a
    // VS Code-spawned engine stayed Free even with a token set; pass it here too (the engine's `serve` accepts it).
    const licenseToken = await getLicenseToken(context);
    if (licenseToken) serveArgs.push('--license', licenseToken);
    let spawnCmd: string;
    let spawnArgs: string[];
    if (engine.kind === 'dll') {
        spawnCmd = resolveDotnet();
        spawnArgs = [engine.path, ...serveArgs];
    } else {
        // Self-contained exe: spawn it DIRECTLY — no dotnet (that's the whole point of the per-platform publish).
        // Zip/vsix extraction can strip the executable bit on non-Windows, so best-effort re-add it before launch.
        if (process.platform !== 'win32') { try { fs.chmodSync(engine.path, 0o755); } catch { /* best effort */ } }
        spawnCmd = engine.path;
        spawnArgs = serveArgs;
    }
    // Redact the Pro token (the value after --license) from the shareable output channel; spawn() below still gets the real args.
    out.appendLine(`Spawning engine (${engine.kind}): "${spawnCmd}" ${redactArgs(spawnArgs).map(a => `"${a}"`).join(' ')}`);
    // Pipe the engine's own stdout/stderr into this channel so a startup failure is VISIBLE here instead of
    // surfacing only as a 10s timeout. ('error' = spawn couldn't start; 'exit' = engine quit early.)
    spawned = cp.spawn(spawnCmd, spawnArgs, { windowsHide: true });
    spawned.stdin?.end(uiChallenge + '\n');
    spawned.stdout?.on('data', (d) => out.appendLine('  [engine] ' + d.toString().replace(/\s+$/, '')));
    spawned.stderr?.on('data', (d) => out.appendLine('  [engine] ' + d.toString().replace(/\s+$/, '')));
    spawned.on('error', (e) => out.appendLine('Engine spawn error: ' + ((e as Error)?.message ?? e) + '. Is the .NET 8 runtime installed and is dotnet on PATH?'));
    spawned.on('exit', (code) => out.appendLine(`Engine process exited (code ${code}).`));

    // A cold .NET + TOM start under a full window reload (AV scanning the just-built DLL + every other extension
    // activating) can take far longer than a standalone run (~300ms). Wait generously — 40s — before giving up.
    let lastReason = 'engine.json not published yet';
    for (let i = 0; i < 200; i++) {
        await delay(200);
        const info = readEngineInfo(ws);
        if (!info) { if (i % 25 === 0) out.appendLine(`  waiting… ${lastReason}`); continue; }
        const reason = await tryConnectPipe(info.pipeName);
        if (reason === null) return info.pipeName;
        lastReason = `connect to ${pipePathFor(info.pipeName)} failed: ${reason}`;
        if (i % 10 === 0) out.appendLine(`  ${lastReason}`);
    }
    throw new Error(`Engine pipe never accepted a connection within 40s. Last reason: ${lastReason}. See the Semanticus output channel.`);
}

async function warnLegacyEngineOwnerOnce(ws: string, info: EngineInfo, context: vscode.ExtensionContext): Promise<void> {
    const key = `legacyEngineOwnerWarned:${path.resolve(ws)}:${info.pid}:${info.startedUtc || ''}`;
    out.appendLine(`Attaching to legacy engine pid ${info.pid}; executable provenance is unavailable.`);
    if (context.globalState.get<boolean>(key)) return;
    await context.globalState.update(key, true);
    void vscode.window.showWarningMessage(
        'This running Semanticus engine predates executable verification. It was attached for this session. Restart the engine to record its source.',
    );
}

async function connectPipe(pipeName: string, uiChallenge: string, attempts = 25): Promise<MessageConnection> {
    const fullPath = pipePathFor(pipeName);
    // The engine publishes engine.json a moment before its pipe server starts accepting, so the first connect
    // can race and fail with ENOENT — retry briefly instead of dropping straight to "engine error".
    let lastErr: unknown;
    for (let i = 0; i < attempts; i++) {
        try {
            return await new Promise<MessageConnection>((resolve, reject) => {
                const socket = net.connect(fullPath);
                let settled = false;
                const fail = (error: unknown) => {
                    if (settled) return;
                    settled = true;
                    try { socket.destroy(); } catch { }
                    reject(error);
                };
                socket.once('connect', async () => {
                    try {
                        const response = waitForRpcHandshake(socket);
                        socket.write(rpcRolePreamble('human', uiChallenge), 'utf8');
                        await response;
                        const c = createMessageConnection(new StreamMessageReader(socket), new StreamMessageWriter(socket));
                        c.listen();
                        socket.resume();
                        out.appendLine(`Connected to authenticated engine pipe ${fullPath}.`);
                        settled = true;
                        resolve(c);
                    } catch (error) { fail(error); }
                });
                socket.once('error', fail);
            });
        } catch (e) {
            if (e instanceof RpcHandshakeRejectedError) throw e;
            lastErr = e;
            await delay(200);
        }
    }
    throw lastErr ?? new Error(`Could not connect to engine pipe ${fullPath}.`);
}

/// The health chip (feature #4): reflect what the LAST change did to model health. PLAIN ENGLISH ONLY — the
/// engine's vocabulary (findings / blast radius / delta) never reaches the UI ("issues", "things"). Absent
/// health (free tier, quiet edit, old engine) hides the chip — the bar reads exactly as before the feature.
/// Ambient, never a popup; surfaces, never applies (the change is already undoable via Semanticus: Undo).
function renderHealthChip(h: HealthDelta | undefined): void {
    if (!healthStatus) { return; }
    if (!h) { lastHealthWorsened = false; healthStatus.hide(); return; }
    const grade = h.grade && h.grade.includes('->') ? h.grade.split('->') : undefined;
    const issues = h.findings ?? (h.new?.length ?? 0);
    const things = h.impact ?? 0;

    // ONE coherent story in text + tooltip: lead with the grade move when there is one, then what the change
    // added/affects — so "grade moved AND issues appeared" never reads as two different messages.
    const parts: string[] = [];
    if (issues > 0) { parts.push(`added ${issues} issue${issues === 1 ? '' : 's'}`); }
    if (things > 0) { parts.push(`affects ${things} thing${things === 1 ? '' : 's'}`); }
    const story = parts.length > 0 ? `This change ${parts.join(' and ')}.` : '';

    healthStatus.text = grade
        ? `$(pulse) Model health: ${grade[0]} -> ${grade[1]}`
        : issues > 0
            ? `$(pulse) Model health: ${issues} new issue${issues === 1 ? '' : 's'}`
            : `$(pulse) This change affects ${things} thing${things === 1 ? '' : 's'}`;

    const lead = grade ? `Model health: ${grade[0]} -> ${grade[1]}. ` : '';
    const sentence = `${lead}${story}`.trim() || `This change moved model health.`;

    // Warning tint (the ambient "spell-check squiggle") ONLY when the change genuinely worsened health: the
    // grade LETTER dropped, or the grade didn't move and a net-new WARNING+ issue appeared (engine-flagged via
    // `warn`; older engines omit it — fall back to any new issue). An IMPROVED grade is never painted red —
    // even when individual findings moved (the old `|| issues > 0` did exactly that).
    const gradeWorsened = grade ? grade[1] > grade[0] : undefined;   // letters order A<B<C<D<F lexicographically
    const worsened = grade ? gradeWorsened === true : (h.warn ?? issues > 0);
    // A worsening chip offers a one-click Undo on click (the click routes through semanticus.healthChipAction,
    // which reads this flag) — so the tooltip promises undo-in-place, not a detour to the command palette.
    lastHealthWorsened = worsened;
    healthStatus.tooltip = worsened
        ? `${sentence} Click to undo this change, or review it.`
        : `${sentence} Click to review.`;
    healthStatus.backgroundColor = worsened ? new vscode.ThemeColor('statusBarItem.warningBackground') : undefined;
    healthStatus.show();
}

/// The health chip's click (feature #4). A change that WORSENED health offers a one-click Undo — the same undo
/// op as "Semanticus: Undo", so it genuinely reverts the change that just moved the grade — alongside opening the
/// readiness scan. Any other (improved / neutral-but-surfaced) chip keeps its prior behaviour: open the AI
/// Readiness tab. Plain-English throughout; the engine's vocabulary never reaches the picker.
async function healthChipActionCmd(): Promise<void> {
    if (!lastHealthWorsened) { await vscode.commands.executeCommand('semanticus.scanReadiness'); return; }
    const undoItem: vscode.QuickPickItem = { label: '$(discard) Undo this change', detail: 'Revert the change that just worsened model health' };
    const scanItem: vscode.QuickPickItem = { label: '$(pulse) Open readiness scan', detail: 'See the full picture in the AI Readiness tab' };
    const pick = await vscode.window.showQuickPick([undoItem, scanItem], { placeHolder: 'This change worsened model health. What would you like to do?' });
    if (pick === undoItem) { await vscode.commands.executeCommand('semanticus.undo'); }
    else if (pick === scanItem) { await vscode.commands.executeCommand('semanticus.scanReadiness'); }
}

/// Connect to (or spawn) the engine and wire the live session: notifications, model open, tree + IntelliSense.
/// Reused by activate() and the Restart Engine command, so a restart re-establishes everything in place.
function cancelScheduledReconnect(): void {
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = undefined;
}

function scheduleEngineReconnect(context: vscode.ExtensionContext): void {
    if (deactivating || reconnectTimer) return;
    const waitMs = Math.min(750 * Math.pow(2, reconnectAttempt++), 10_000);
    reconnectTimer = setTimeout(() => {
        reconnectTimer = undefined;
        if (deactivating) return;
        status.text = '$(database) Semanticus: reconnecting…';
        void connectEngine(context, true);
    }, waitMs);
}

async function connectEngine(context: vscode.ExtensionContext, quiet = false): Promise<void> {
    const epoch = connectionEpoch;
    if (connectInFlight?.epoch === epoch) return connectInFlight.promise;
    const promise = connectEngineAttempt(context, quiet, epoch);
    connectInFlight = { epoch, promise };
    try { await promise; }
    finally { if (connectInFlight?.promise === promise) connectInFlight = undefined; }
}

async function connectEngineAttempt(context: vscode.ExtensionContext, quiet: boolean, epoch: number): Promise<void> {
    let connected: MessageConnection | undefined;
    try {
        const ws = workspaceDir();
        const uiChallenge = await getUiChallenge(context.secrets, ws);
        let pipeName = await ensureEngine(ws, context, uiChallenge);
        try {
            connected = await connectPipe(pipeName, uiChallenge);
        } catch (error) {
            if (!(error instanceof RpcHandshakeRejectedError)) throw error;
            // A healthy engine that rejects the SecretStorage proof is an MCP-owned or stale-challenge owner.
            // The human door is authoritative: stop it, start an owner with this proof, then authenticate again.
            await reclaimUiOwnership(ws, pipeName);
            pipeName = await ensureEngine(ws, context, uiChallenge);
            connected = await connectPipe(pipeName, uiChallenge);
        }
        if (deactivating || epoch !== connectionEpoch) { connected.dispose(); return; }
        conn = connected;
        tree.attach(connected);
        daxFs.attach(connected);
        await migrateLegacyXmlaHistory();

        connected.onNotification('model/didChange', (n: ChangeNotification) => {
            out.appendLine(`didChange rev=${n.revision} origin=${n.origin} label=${n.label ?? ''}`);
            tree.refresh();
            scheduleDaxSymbolRebuild();   // keep DAX IntelliSense in sync with renames/creates/deletes
            void propGrid.refresh();   // live-update the property grid when the selected object changes (incl. agent edits)
            for (const d of n.deltas ?? []) { daxFs.signalChanged(d.ref); }
            status.text = `$(database) Semanticus rev ${n.revision}${n.origin === 'agent' ? ' $(hubot)' : ''}`;
            healthSessionId = n.sessionId;   // the chip now reflects THIS session's last change
            renderHealthChip(n.health);
            void refreshSyncChip();   // an edit can flip hasUnsavedChanges → repaint the editing↔querying verdict
            try { studioPanel?.webview.postMessage({ type: 'didChange', payload: n }); } catch { /* disposing */ }
        });
        // The change plan broadcasts separately so the Studio Optimize tab updates live as the plan
        // is proposed/filled/applied (by the human OR the user's Claude on the same session).
        connected.onNotification('plan/didChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'planDidChange', payload: v }); } catch { /* disposing */ }
        });
        connected.onNotification('progress/didChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'progressDidChange', payload: v }); } catch { /* disposing */ }
        });
        // The model spec broadcasts separately so the Studio Spec tab updates live as it's auto-generated / built /
        // refined (by the human OR the user's Claude on the same session).
        connected.onNotification('spec/didChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'specDidChange', payload: v }); } catch { /* disposing */ }
        });
        // Diagram layout broadcasts separately (engine-owned sidecar, not a model edit) so the Diagram canvas moves
        // live when the user's Claude (or another client) saves positions on the same model.
        connected.onNotification('layout/didChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'layoutDidChange', payload: v }); } catch { /* disposing */ }
        });
        // A workflow RUN transition broadcasts separately so the Studio Workflows tab tracks a live run as it
        // advances — driven from EITHER door (the human in Studio, or the user's Claude over MCP) on one run.
        connected.onNotification('workflow/didChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'workflowDidChange', payload: v }); } catch { /* disposing */ }
        });
        // The workflow LIBRARY broadcasts separately so the Workflows rail live-updates when a workflow file is
        // saved/deleted (by the human OR the user's Claude via save_workflow) on the same session.
        connected.onNotification('workflow/libraryDidChange', (v: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'workflowLibraryDidChange', payload: v }); } catch { /* disposing */ }
        });
        // A NON-mutating run by the user's Claude (run_dax / profile_dax / debug / …) broadcasts so Studio
        // reflects what the agent is doing live — the read counterpart of model/didChange. Relay only (no
        // tree/grid side effects); the webview routes it to the matching tab + the live activity feed.
        connected.onNotification('model/activity', (e: unknown) => {
            try { studioPanel?.webview.postMessage({ type: 'activity', payload: e }); } catch { /* disposing */ }
            // A connection-mutating op by the agent (over MCP) fires only model/activity — no model/didChange — so the
            // native identity/sync chips would otherwise go stale on the UI door. Repaint them when the activity is a
            // connect/disconnect (HIGH 7). Other activity kinds (run_dax/…) don't move the connection state — skip them.
            const kind = (e as { kind?: string } | undefined)?.kind;
            if (kind === 'connect_xmla' || kind === 'connect_local' || kind === 'disconnect') {
                void refreshStatus();
                // The webview's ConnectBar + Connections drawer subscribe to reconnect + model edits only, so an
                // MCP-door connect/disconnect (no model/didChange) would leave them stale. Relay a light connection-
                // state nudge so both re-read their context (MED 5).
                try { studioPanel?.webview.postMessage({ type: 'connectionChanged' }); } catch { /* disposing */ }
            }
        });
        connected.onClose(() => {
            if (conn !== connected) return;   // an explicit restart already replaced/invalidated this connection
            conn = undefined;
            connectionEpoch++;
            status.text = '$(database) Semanticus: disconnected';
            healthStatus.hide();
            syncStatus.hide();
            scheduleEngineReconnect(context);
        });

        // Open a model if one is already loaded, else if configured, else prompt.
        let info: SessionInfo | undefined = await connected.sendRequest<SessionInfo>('sessionInfo');
        if (info?.sessionId) {
            setStatusFromInfo(info);
        } else {
            const configured = vscode.workspace.getConfiguration('semanticus').get<string>('modelPath');
            if (configured) {
                await connected.sendRequest<OpenResult>('open', configured);
                info = await refreshStatus();
            } else {
                status.text = '$(database) Semanticus: no model. Run "Open Model…"';
            }
        }
        if (info?.sessionId) await propGrid.showModelIfEmpty(info.modelName);
        else await propGrid.clear();
        tree.refresh();
        try { studioPanel?.webview.postMessage({ type: 'reconnected' }); } catch { /* no panel */ }
        void rebuildDaxSymbols();   // seed DAX IntelliSense for the just-opened model
        reconnectAttempt = 0;
    } catch (err: any) {
        if (deactivating || epoch !== connectionEpoch) return;
        if (conn === connected) conn = undefined;
        try { connected?.dispose(); } catch { /* already closed */ }
        status.text = '$(error) Semanticus: engine error';
        out.appendLine('Engine connection failed: ' + (err?.stack ?? err));
        if (!quiet) vscode.window.showErrorMessage('Semanticus could not start its engine. See the Semanticus output channel. ' + (err?.message ?? err));
        scheduleEngineReconnect(context);
    }
}

/// One-click dev loop: drop the connection, kill the running engine (freeing the locked DLL), optionally
/// rebuild it (Debug), then reconnect a fresh engine — no full window reload. Picks up engine code changes.
async function restartEngineCmd(context: vscode.ExtensionContext): Promise<void> {
    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Semanticus: restarting engine' },
        async (progress) => {
            cancelScheduledReconnect();
            reconnectAttempt = 0;
            connectionEpoch++;
            const previous = conn;
            conn = undefined;
            try { previous?.dispose(); } catch { /* already gone */ }
            const ws = workspaceDir();
            progress.report({ message: 'stopping…' });
            killEngine(ws);
            await delay(700);   // let the OS release the file lock before we rebuild

            const rebuild = context.extensionMode === vscode.ExtensionMode.Development
                && vscode.workspace.getConfiguration('semanticus').get<boolean>('rebuildEngineOnRestart', true);
            if (rebuild) {
                progress.report({ message: 'building engine (Debug)…' });
                const ok = await buildEngine();
                if (!ok) vscode.window.showWarningMessage('Semanticus: engine rebuild failed; restarting the previous build. See the Semanticus output.');
            }

            progress.report({ message: 'starting…' });
            status.text = '$(database) Semanticus: connecting…';
            await connectEngine(context);
        });
}

/// "Semanticus: Activate license" — paste a Pro token, persist it, and re-verify HONESTLY via the engine. We never
/// re-implement ECDSA in TS; we hand the token to the engine over --license and read back its get_entitlement verdict.
/// SINGLE-FLIGHT: at most one interactive license flow may exist at a time — a second invocation is refused while
/// one is in flight, and entering the flow settles any open conflict prompt (releases its pair guard; its parked
/// apply aborts through its own revision/epoch/value checks if this flow changes anything).
async function activateLicenseCmd(context: vscode.ExtensionContext): Promise<void> {
    if (licenseInteractiveFlowActive) {
        vscode.window.showInformationMessage('Semanticus: a license operation is already in progress. Finish or dismiss it first.');
        return;
    }
    licenseInteractiveFlowActive = true;
    licenseConflictPromptedKey = undefined;   // settle any open conflict prompt: it re-offers after this flow ends
    try { await activateLicenseFlow(context); }
    finally { licenseInteractiveFlowActive = false; }
}

/// The interactive body of Activate License. Runs entirely under the single-flight guard (see activateLicenseCmd);
/// every apply below still re-checks revision+epoch+values under the mutation lock — the guard removes interactive
/// races, the checks keep the invariant against the background migration and writers outside this extension.
async function activateLicenseFlow(context: vscode.ExtensionContext): Promise<void> {
    // Capture the state this flow will act on COHERENTLY (one locked read): the secret, the effective legacy
    // setting, the commit revision and the attempt epoch. Every decision made in the input box below is based on
    // this snapshot, and every apply re-checks it under the lock before touching anything. The prefill derived
    // from it is ALSO the value the clear flow treats as "the token being discarded" (the value-guarded clear).
    const opened = await withLicenseMutation(async () => {
        let secretAtOpen: string | null = '';
        try { secretAtOpen = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim(); } catch { secretAtOpen = null; }
        const legacyAtOpen = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();
        return { secretAtOpen, legacyAtOpen, revision: licenseMutationRevision, epoch: licenseMutationAttemptEpoch };
    });
    const prefill = opened.secretAtOpen || opened.legacyAtOpen;
    const token = await vscode.window.showInputBox({
        title: 'Activate Semanticus Pro',
        prompt: 'Paste your Semanticus Pro license token.',
        placeHolder: 'Emailed to you after checkout (a long code with a dot in the middle). Leave empty to clear.',
        password: true,           // the token is a bearer credential — never echo it on screen
        ignoreFocusOut: true,     // pasting can steal focus; don't discard the box
        value: prefill,
    });
    if (token === undefined) return;   // user cancelled — leave the setting untouched
    const trimmed = token.trim();

    // Persist in SecretStorage (the OS keychain), NOT settings.json — the token is a bearer credential. Both doors
    // (the spawned engine + the .mcp.json writer) read it back through getLicenseToken(). The legacy plaintext copy is
    // removed ONLY after the secret provably reads back, and only where it holds the SAME token (value-guarded);
    // the explicit command discloses the removal instead of asking.
    if (!trimmed) {
        // Empty submit means "clear the license I was shown" (the prefill), NOT "delete whatever secret exists
        // now": a newer activation can commit while this box sits open, and deleting ITS token would destroy it.
        // One locked unit: revision check first, then the secret must still equal the captured prefill before it
        // is deleted (proved gone by read-back), then the plaintext copies OF THE DISCARDED TOKEN are cleared.
        type ClearOutcome =
            | { kind: 'superseded-committed' } | { kind: 'superseded-attempted' }
            | { kind: 'done'; secretGone: boolean; deleteError: string; plaintextRemoved: boolean; remaining: string[] };
        const res = await withLicenseMutation(async (): Promise<ClearOutcome> => {
            // Revision AND epoch: a half-store that failed verification bumps only the epoch, yet can have left
            // the SAME value in the slot — deleting over it would erase evidence a rollback still needs. The two
            // trips carry different EVIDENCE (a commit happened vs an attempt happened) and word their abort differently.
            if (licenseMutationRevision !== opened.revision) return { kind: 'superseded-committed' };
            if (licenseMutationAttemptEpoch !== opened.epoch) return { kind: 'superseded-attempted' };
            let current: string | null = null;
            try { current = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim(); } catch { current = null; }
            let secretGone: boolean;
            let deleteError = '';
            if (current === null) {
                secretGone = false;
                deleteError = 'secure storage could not be read';
            } else if (current && current !== prefill) {
                // An out-of-band writer (never bumps our counters) put a DIFFERENT token in the slot — not ours to
                // delete. The value difference IS evidence of a change, so the committed wording is truthful.
                return { kind: 'superseded-committed' };
            } else if (!current) {
                secretGone = true;   // nothing to delete
            } else {
                try {
                    licenseMutationAttemptEpoch++;   // attempting a delete — other parked flows must see it
                    await context.secrets.delete(LICENSE_SECRET_KEY);
                    secretGone = !((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim();
                    if (secretGone) licenseMutationRevision++;   // the delete committed
                } catch (e: any) { secretGone = false; deleteError = e?.message ?? String(e); }
            }
            let plaintextRemoved = false;
            if (prefill) {
                const cleared = await clearLegacyScopes(prefill);
                plaintextRemoved = cleared.expectedCleared.length > 0;
            } else {
                reportKeptScopes(definedLegacyScopes());   // nothing was active, but plaintext tokens exist — say so
            }
            // Whatever is STILL defined at any scope is read on the next engine start — free tier can only be
            // promised when nothing remains anywhere.
            const remaining = definedLegacyScopes().map((s) => s.label);
            return { kind: 'done', secretGone, deleteError, plaintextRemoved, remaining };
        });
        if (res.kind === 'superseded-committed') {
            vscode.window.showInformationMessage('Semanticus: a newer license change was made since this prompt was shown, so nothing was cleared. Run "Semanticus: Show license" to see what is active.');
        } else if (res.kind === 'superseded-attempted') {
            vscode.window.showInformationMessage('Semanticus: another license operation was attempted since this prompt was shown, so nothing was cleared. Run "Semanticus: Show license" to see what is active.');
        } else if (res.secretGone && res.remaining.length === 0) {
            vscode.window.showInformationMessage('Semanticus: license cleared. The engine will run on the free tier after the next restart.' + (res.plaintextRemoved ? ' The plaintext copy was also removed from settings.json.' : ''));
        } else if (res.secretGone) {
            // NEVER claim the free tier while a plaintext token survives: the engine reads the setting on its next
            // start, so the kept token remains effective and Pro may activate again.
            vscode.window.showWarningMessage(
                'Semanticus: the license was cleared from secure storage' + (res.plaintextRemoved ? ' and its plaintext copy was removed' : '') +
                `, but a license token remains in ${res.remaining.join(', ')}. That setting is still read on the next engine start, so Pro may still activate. To fully clear it, open that settings.json and delete the "semanticus.licenseToken" entry.`);
        } else {
            vscode.window.showWarningMessage(
                'Semanticus: the license could not be fully cleared from secure storage' +
                (res.deleteError ? ` (${res.deleteError})` : '') +
                ', so Pro may still activate after a restart. Try clearing it again, or reload the window and clear it once more.' +
                (res.plaintextRemoved ? ' The plaintext copy in settings.json was removed.' : ''));
        }
        return;
    }
    // Store under the mutation lock, snapshotting the prior secret INSIDE the same unit so the snapshot and the
    // store attempt are one atomic step: storeLicenseSecretVerified can report failure with the NEW token actually
    // stored (store() succeeded, the verifying read failed), and the cancel path below needs a coherent prior
    // state to roll back to — otherwise "nothing was saved" could be false while the half-stored token has
    // silently replaced an earlier one. The unit's FIRST statements re-check the open-time revision/epoch and
    // re-read the (secret, legacy) pair: with single-flight interactive flows a mismatch here should only be
    // reachable via the background migration or an external writer, but the invariant is cheap — keep it checked.
    // Revision and epoch are then captured AFTER our own store bumped them, so the parked modal's rollback and
    // plaintext write below stay eligible for OUR attempt while anyone else's attempt invalidates them.
    type StoreAttempt =
        | { superseded: 'committed' | 'attempted' | 'unverified' }
        | { superseded: false; stored: boolean; priorSecret: string; priorSecretKnown: boolean; revision: number; epoch: number };
    const attempt = await withLicenseMutation(async (): Promise<StoreAttempt> => {
        // The abort wording follows the EVIDENCE: revision or value difference = a change happened; epoch only =
        // an attempt happened; a read that cannot be compared proves nothing.
        if (licenseMutationRevision !== opened.revision) return { superseded: 'committed' };
        if (licenseMutationAttemptEpoch !== opened.epoch) return { superseded: 'attempted' };
        let secretNow: string | null = '';
        try { secretNow = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim(); } catch { secretNow = null; }
        if (secretNow === null || opened.secretAtOpen === null) {
            // One side unreadable: equality cannot be proven either way. Proceed only when BOTH reads failed the
            // same way (a consistently unavailable keychain is the normal broken-keychain path to the fallback).
            if (secretNow !== opened.secretAtOpen) return { superseded: 'unverified' };
        } else if (secretNow !== opened.secretAtOpen) { return { superseded: 'committed' }; }
        const legacyNow = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();
        if (legacyNow !== opened.legacyAtOpen) return { superseded: 'committed' };
        let priorSecret = '';
        let priorSecretKnown = true;
        try { priorSecret = (await context.secrets.get(LICENSE_SECRET_KEY)) ?? ''; }
        catch { priorSecretKnown = false; }
        const stored = await storeLicenseSecretVerified(context, trimmed);
        return { superseded: false, stored, priorSecret, priorSecretKnown, revision: licenseMutationRevision, epoch: licenseMutationAttemptEpoch };
    });
    if (attempt.superseded) {
        vscode.window.showInformationMessage(
            attempt.superseded === 'committed'
                ? 'Semanticus: a newer license change was made since this prompt was shown, so nothing was stored. Run "Semanticus: Show license" to see what is active, then activate again if needed.'
                : attempt.superseded === 'attempted'
                    ? 'Semanticus: another license operation was attempted since this prompt was shown, so nothing was stored. Run "Semanticus: Show license" to see what is active, then activate again if needed.'
                    : 'Semanticus: the license state could not be re-checked, so nothing was stored. Run "Semanticus: Show license" to see what is active, then activate again if needed.');
        return;
    }

    if (attempt.stored) {
        if (await clearLegacyLicenseSetting(false, trimmed)) {
            vscode.window.showInformationMessage('Semanticus: license saved to secure storage. The plaintext copy was removed from settings.json.');
        }
    } else {
        // Secure storage would not take (or return) the secret. The only remaining place to persist the token is
        // settings.json, in PLAINTEXT. That is a bearer credential written to an unencrypted file that Settings Sync
        // can propagate to your other machines, so get explicit, informed consent BEFORE any write instead of writing
        // first and warning afterwards.
        const choice = await vscode.window.showWarningMessage(
            'Semanticus could not save your license to secure storage (the OS keychain is unavailable on this machine).',
            {
                modal: true,
                detail: 'The only remaining place to store it is settings.json, in plaintext. That file is not encrypted, and if you use Settings Sync it will be propagated to your other machines. Store the license token in plaintext anyway?',
            },
            'Store in plaintext',
        );
        if (choice !== 'Store in plaintext') {
            // Cancelled (or dismissed): persist nothing. "Not saved" must be PROVABLY true — the failed store may
            // have half-succeeded (stored, but the verifying read failed). Under the lock, the revision+epoch check
            // comes FIRST: value equality is NOT identity — another actor can have ATTEMPTED or stored the SAME
            // token while this modal sat open (a successful same-token activation bumps the revision; even a failed
            // half-store bumps the epoch), and "rolling back" over either would destroy state that is not ours.
            // Only the counters distinguish those histories. The value checks after them guard against out-of-band
            // writers that bump neither.
            type RollbackOutcome = 'clean' | 'superseded-gone-committed' | 'superseded-gone-attempted'
                | 'superseded-present-committed' | 'superseded-present-attempted' | 'uncertain';
            const rb = await withLicenseMutation(async (): Promise<RollbackOutcome> => {
                const revTripped = licenseMutationRevision !== attempt.revision;
                if (revTripped || licenseMutationAttemptEpoch !== attempt.epoch) {
                    // Someone acted after our failed store: do NOT restore over their state. But "was not saved"
                    // still needs PROOF — re-read and only claim absence when the attempted token is provably
                    // gone. The wording follows the evidence: a revision trip proves a change COMMITTED; an
                    // epoch-only trip proves only that another operation was attempted.
                    try {
                        const now = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim();
                        if (now !== trimmed) return revTripped ? 'superseded-gone-committed' : 'superseded-gone-attempted';
                        return revTripped ? 'superseded-present-committed' : 'superseded-present-attempted';
                    } catch { return revTripped ? 'superseded-present-committed' : 'superseded-present-attempted'; }   // unreadable — absence cannot be proven
                }
                if (!attempt.priorSecretKnown) return 'uncertain';   // no coherent snapshot — never guess at a restore
                let current: string | null = null;
                try { current = (await context.secrets.get(LICENSE_SECRET_KEY)) ?? ''; } catch { current = null; }
                if (current === null) return 'uncertain';
                if (current === attempt.priorSecret) return 'clean';   // prior state intact — nothing to undo
                if (current.trim() !== trimmed) {
                    // An out-of-band writer changed it (a VALUE difference — evidence of a real change) — not
                    // ours to undo; the attempted token is provably absent.
                    return 'superseded-gone-committed';
                }
                try {
                    licenseMutationAttemptEpoch++;   // our restore is itself an attempt others must see
                    if (attempt.priorSecret) await context.secrets.store(LICENSE_SECRET_KEY, attempt.priorSecret);
                    else await context.secrets.delete(LICENSE_SECRET_KEY);
                    const now = (await context.secrets.get(LICENSE_SECRET_KEY)) ?? '';
                    const restored = attempt.priorSecret ? now === attempt.priorSecret : !now.trim();
                    if (restored) licenseMutationRevision++;   // the rollback itself is a commit
                    return restored ? 'clean' : 'uncertain';
                } catch { return 'uncertain'; }
            });
            if (rb === 'clean') {
                vscode.window.showInformationMessage('Semanticus: the license was not saved. Secure storage still holds what it held before, and nothing was written to settings.json. Activate the license again once secure storage is available (for example, after unlocking your keychain or reloading the window).');
            } else if (rb === 'superseded-gone-committed') {
                vscode.window.showInformationMessage('Semanticus: this license was not saved, and nothing was written to settings.json. A newer license change was made after this attempt started, and that newer state was kept.');
            } else if (rb === 'superseded-gone-attempted') {
                vscode.window.showInformationMessage('Semanticus: this license was not saved, and nothing was written to settings.json. Another license operation was attempted after this attempt started, so secure storage was left as it is. Run "Semanticus: Show license" to see what is active.');
            } else if (rb === 'superseded-present-committed') {
                vscode.window.showWarningMessage('Semanticus: nothing was written to settings.json, but Semanticus could not confirm that this attempt\'s token is out of secure storage, and a newer license change was made afterwards. Reload the window, run "Semanticus: Show license" to see what is active, then activate the license you want.');
            } else if (rb === 'superseded-present-attempted') {
                vscode.window.showWarningMessage('Semanticus: nothing was written to settings.json, but Semanticus could not confirm that this attempt\'s token is out of secure storage, and another license operation was attempted afterwards. Reload the window, run "Semanticus: Show license" to see what is active, then activate the license you want.');
            } else {
                vscode.window.showWarningMessage('Semanticus: the license was not written to settings.json, but secure storage is in an uncertain state: the save failed partway and Semanticus could not confirm restoring the previous state. Reload the window, run "Semanticus: Show license" to see what is active, then activate again.');
            }
            return;
        }
        // Consent was given for the state as of THIS attempt — if anything was committed or even attempted while
        // the modal sat open, honor the newer state and write nothing. Then re-read the VALUES too: writers outside
        // this extension (another window's keychain, Settings Sync) bump neither counter. The abort wording follows
        // the evidence: revision or value difference = a change; epoch only = an attempt.
        const wrote = await withLicenseMutation(async (): Promise<'written' | 'committed' | 'attempted'> => {
            if (licenseMutationRevision !== attempt.revision) return 'committed';
            if (licenseMutationAttemptEpoch !== attempt.epoch) return 'attempted';
            let secretNow: string | null = null;
            try { secretNow = ((await context.secrets.get(LICENSE_SECRET_KEY)) ?? '').trim(); } catch { secretNow = null; }
            // The secret may hold the prior value or our own half-store; any OTHER readable value means someone
            // else acted. (Unreadable is expected here — a broken keychain is exactly why this fallback exists.)
            if (secretNow !== null && secretNow !== attempt.priorSecret.trim() && secretNow !== trimmed) return 'committed';
            const legacyNow = (vscode.workspace.getConfiguration('semanticus').get<string>('licenseToken') || '').trim();
            if (legacyNow !== opened.legacyAtOpen) return 'committed';
            await vscode.workspace.getConfiguration('semanticus').update('licenseToken', trimmed, vscode.ConfigurationTarget.Global);
            licenseMutationRevision++;   // the plaintext write is a commit
            return 'written';
        });
        if (wrote !== 'written') {
            vscode.window.showInformationMessage(
                wrote === 'committed'
                    ? 'Semanticus: a newer license change was made since this prompt was shown, so nothing was written to settings.json. The newer state was kept. Run "Semanticus: Show license" to see what is active.'
                    : 'Semanticus: another license operation was attempted since this prompt was shown, so nothing was written to settings.json. Run "Semanticus: Show license" to see what is active.');
            return;
        }
        vscode.window.showWarningMessage('Semanticus: license saved to settings.json in plaintext because secure storage was unavailable. Consider excluding settings.json from Settings Sync until secure storage works, then re-activate to move it into the keychain.');
    }

    // Re-verify: restart the engine WE spawned so it re-reads --license, then report what the engine computed. If we
    // merely ATTACHED to an engine we don't own, we must not kill it — prompt for a window reload to re-own with the token.
    if (spawned) {
        await restartEngineCmd(context);   // respawns with --license (getLicenseToken now returns the new token)
        await showLicenseCmd();            // surface the engine's real verdict (tier + expiry/grace, or the teaching failure Reason)
    } else {
        const pick = await vscode.window.showInformationMessage(
            'License saved. Reload the window to activate it (Semanticus can\'t finish here on its own).',
            'Reload Window');
        if (pick === 'Reload Window') await vscode.commands.executeCommand('workbench.action.reloadWindow');
    }
}

/// Open the one web-owned account door. Checkout, renewal and cancellation stay outside the extension; the engine
/// supplies the authoritative URL so the Studio, command palette and expiry/grace copy cannot drift apart.
async function manageLicenseCmd(known?: EntitlementInfo): Promise<void> {
    let info = known;
    if (!info && conn) {
        try { info = await conn.sendRequest('getEntitlement') as EntitlementInfo; }
        catch { /* an older/disconnected engine still gets the launch fallback */ }
    }
    const url = (info?.manageUrl || DEFAULT_LICENSE_MANAGE_URL).trim();
    if (!/^https:\/\//i.test(url)) {
        vscode.window.showErrorMessage('Semanticus: the Pro account link is not a valid secure web address.');
        return;
    }
    const opened = await vscode.env.openExternal(vscode.Uri.parse(url));
    if (!opened) vscode.window.showWarningMessage('Semanticus: your browser did not open. Try “Semanticus: Pro Plans and Support” again.');
}

/// "Semanticus: Show license" — report the current entitlement WITHOUT changing anything. Reads the engine's verdict
/// (the source of truth) so what's shown is exactly what gates the Pro chokepoints, then offers the matching web path.
async function showLicenseCmd(): Promise<void> {
    if (!conn) { vscode.window.showWarningMessage('Semanticus: not connected yet. Wait for the status bar to show your model, then try again.'); return; }
    let info: EntitlementInfo;
    try { info = await conn.sendRequest('getEntitlement') as EntitlementInfo; }
    catch (e: any) { vscode.window.showErrorMessage(`Semanticus: could not read the entitlement: ${e?.message ?? e}`); return; }

    if ((info?.tier ?? 'free').toLowerCase() === 'pro') {
        const expiry = info.expiry ? new Date(info.expiry * 1000).toISOString().slice(0, 10) : null;
        // A grace advisory rides in `reason` while still Pro; otherwise show the plain licensed-to / expiry line.
        const detail = info.reason ? info.reason
            : expiry ? `Licensed to ${info.licensedTo || 'you'}, expires ${expiry}.`
            : `Licensed to ${info.licensedTo || 'you'}, perpetual.`;
        const pick = await vscode.window.showInformationMessage(`Semanticus Pro is active. ${detail}`, 'Pro options');
        if (pick === 'Pro options') await manageLicenseCmd(info);
    } else {
        // Free — surface the engine's Reason VERBATIM (it teaches: no license / invalid / expired-past-grace + renew URL).
        const pick = await vscode.window.showInformationMessage(
            `Semanticus is on the free tier. ${info?.reason || 'No Pro license is active.'}`,
            'Upgrade to Pro');
        if (pick === 'Upgrade to Pro') await manageLicenseCmd(info);
    }
}

/// Kill whatever engine we know about (the one we spawned, and/or the one in engine.json) and clear its lock files.
function killEngine(ws: string): void {
    try { if (spawned?.pid) process.kill(spawned.pid); } catch { /* already exited */ }
    spawned = undefined;
    const info = readEngineInfo(ws);
    if (info?.pid) { try { process.kill(info.pid); } catch { /* not ours / already gone */ } }
    for (const f of ['engine.json', 'engine.lock']) {
        try { fs.rmSync(path.join(ws, '.semanticus', f)); } catch { /* may not exist */ }
    }
}

async function reclaimUiOwnership(ws: string, pipeName: string): Promise<void> {
    const existing = readEngineInfo(ws);
    out.appendLine(`The live engine rejected the UI proof. Reclaiming ownership for VS Code (pid ${existing?.pid ?? 'unknown'}).`);
    killEngine(ws);
    const deadline = Date.now() + 5000;
    while (Date.now() < deadline) {
        const processStopped = !existing?.pid || !pidAlive(existing.pid);
        const pipeStopped = (await tryConnectPipe(pipeName)) !== null;
        if (processStopped && pipeStopped) return;
        await delay(100);
    }
    throw new Error('Semanticus could not stop the previous engine owner. Reconnect the AI Assistant or restart the engine, then try again.');
}

function engineCsproj(): string | undefined {
    const repo = repoFromDll();
    if (!repo) return undefined;
    const p = path.join(repo, 'Semanticus.Engine', 'Semanticus.Engine.csproj');
    return fs.existsSync(p) ? p : undefined;
}

/// dotnet build the engine (Debug) so a restart picks up code changes. Resolves true on a clean build.
function buildEngine(): Promise<boolean> {
    return new Promise((resolve) => {
        const csproj = engineCsproj();
        if (!csproj) { out.appendLine('restart: cannot locate Semanticus.Engine.csproj to build (set semanticus.engineDll).'); resolve(false); return; }
        const dotnet = resolveDotnet();
        out.appendLine(`Rebuilding engine: "${dotnet}" build "${csproj}" -c Debug`);
        const proc = cp.spawn(dotnet, ['build', csproj, '-c', 'Debug', '--nologo', '-v', 'q'], { windowsHide: true });
        proc.stdout?.on('data', (d) => out.appendLine('  [build] ' + d.toString().replace(/\s+$/, '')));
        proc.stderr?.on('data', (d) => out.appendLine('  [build] ' + d.toString().replace(/\s+$/, '')));
        proc.on('error', (e) => { out.appendLine('build spawn error: ' + ((e as Error)?.message ?? e)); resolve(false); });
        proc.on('exit', (code) => { out.appendLine(`engine build exited (code ${code}).`); resolve(code === 0); });
    });
}

// ---- model tree ------------------------------------------------------------------------------

class ModelTreeProvider implements vscode.TreeDataProvider<TreeNode> {
    private readonly _emitter = new vscode.EventEmitter<TreeNode | undefined | void>();
    readonly onDidChangeTreeData = this._emitter.event;
    private connection: MessageConnection | undefined;
    // The FLAT engine fan per table ref — folder nodes are synthesized from each member's displayFolder, and
    // the folder fans + getParent + the folder pickers all read this cache. Cleared on every refresh (didChange).
    private readonly flat = new Map<string, TreeNode[]>();

    attach(c: MessageConnection) { this.connection = c; }
    refresh() { this.flat.clear(); this._emitter.fire(); }

    getTreeItem(n: TreeNode): vscode.TreeItem {
        const item = new vscode.TreeItem(
            n.name,
            n.hasChildren ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
        item.id = n.ref;          // stable identity (= ref) so reveal-by-ref matches the rendered node
        item.contextValue = n.kind;
        item.iconPath = iconFor(n.kind);
        // Single-click opens the full Monaco DAX editor for anything with a DAX body (no more tiny input boxes).
        if (DAX_KINDS.has(n.kind)) {
            item.command = { command: 'semanticus.editDax', title: 'Edit DAX', arguments: [n] };
            item.tooltip = n.kind === 'function' ? 'Edit function (DAX UDF)' : 'Edit DAX';
        }
        if (n.kind === 'dfolder') item.tooltip = 'Display folder: a label on its members, not a container (it lives in each member’s Display Folder property)';
        return item;
    }

    async getChildren(n?: TreeNode): Promise<TreeNode[]> {
        if (!this.connection) return [];
        try {
            if (!n) return await this.connection.sendRequest<TreeNode[]>('listTree', null);
            // A table/calc-group fans through the folder grouper (TE2-style); a folder node fans its own level.
            if (n.kind === 'table' || n.kind === 'calcgroup') {
                const kids = await this.connection.sendRequest<TreeNode[]>('listTree', n.ref);
                this.flat.set(n.ref, kids);
                return groupFolderLevel(kids, refParts(n.ref).name, '');
            }
            if (n.kind === 'dfolder') {
                const { table, path } = folderParts(n.ref);
                return groupFolderLevel(await this.tableChildren('table:' + table), table, path);
            }
            return await this.connection.sendRequest<TreeNode[]>('listTree', n.ref);
        } catch (e: any) {
            out.appendLine('listTree failed: ' + (e?.message ?? e));
            return [];
        }
    }

    /// The flat (ungrouped) children of a table/calc-group, cache-first — folder fans and the folder pickers
    /// share it so they always agree with what the tree renders.
    async tableChildren(tableRef: string): Promise<TreeNode[]> {
        const hit = this.flat.get(tableRef);
        if (hit) return hit;
        if (!this.connection) return [];
        const kids = await this.connection.sendRequest<TreeNode[]>('listTree', tableRef);
        this.flat.set(tableRef, kids);
        return kids;
    }

    // The parent node of a ref — derived from the ref scheme (so reveal can walk to root). Constructed nodes
    // match the real tree nodes by id (= ref). Returns undefined for root-level kinds. ASYNC because a foldered
    // member must resolve its folder from the render fan even on a COLD tree (see the member case).
    async getParent(n: TreeNode): Promise<TreeNode | undefined> {
        const mk = (ref: string, name: string, kind: string): TreeNode => ({ ref, name, kind, hasChildren: true });
        if (n.kind === 'dfolder') {
            const { table, path } = folderParts(n.ref);
            const parent = parentFolderPath(path);
            return parent ? mk(folderRef(table, parent), leafFolderName(parent), 'dfolder') : mk('table:' + table, table, 'table');
        }
        const p = refParts(n.ref);
        switch (p.kind) {
            case 'measure': case 'column': case 'calcColumn': case 'hierarchy': {
                if (!p.table) return undefined;
                // Resolve the member's folder from the SAME fan the render path uses (tableChildren — cache-first,
                // else a fresh engine read), NOT from the passed node. Reveal-by-ref hands us a synthetic node with
                // no displayFolder, and refresh() clears the cache, so relying on the node/cache regressed reveal
                // for any foldered object in a cold tree (it fell back to the table, which no longer lists the
                // member directly — its rendered children are folder nodes). One resolution, always correct.
                let df = '';
                try { df = normFolder((await this.tableChildren('table:' + p.table)).find((x) => x.ref === n.ref)?.displayFolder); }
                catch { /* engine hiccup — fall back to the table below */ }
                if (df) return mk(folderRef(p.table, df), leafFolderName(df), 'dfolder');
                return mk('table:' + p.table, p.table, 'table');
            }
            case 'partition':
                return p.table ? mk('table:' + p.table, p.table, 'table') : undefined;
            case 'calcitem':
                return p.table ? mk('table:' + p.table, p.table, 'calcgroup') : undefined;
            case 'level': {
                const hier = (p.name || '').split('/')[0];
                return p.table && hier ? mk('hierarchy:' + p.table + '/' + hier, hier, 'hierarchy') : undefined;
            }
            case 'relationship': return mk('folder:relationships', 'Relationships', 'folderRelationships');
            case 'role': return mk('folder:roles', 'Roles', 'folderRoles');
            case 'perspective': return mk('folder:perspectives', 'Perspectives', 'folderPerspectives');
            default: return undefined;   // table / calcgroup / function / folder* → root
        }
    }
}

// ---- display folders (TE2-style) ---------------------------------------------------------------
// A display folder has no existence of its own in the model — it is just the DisplayFolder string on each
// measure/column/hierarchy ("Parent\Child" for nesting), so an empty folder cannot persist. The tree groups
// the engine's FLAT fan into 'dfolder:' nodes client-side; every folder gesture writes through the engine's
// batch ops (setDisplayFolder / renameDisplayFolder) so one gesture is ONE undo step. The pure folder math
// (normFolder / folderParts / groupFolderLevel / ancestry) lives in ./folders — vscode-free + unit-tested.

// The Reference Model tree — a SECOND model (file / git ref / published workspace) browsed read-only to copy
// objects FROM into the open model. The engine returns the copyable objects as a flat list (one resolve); this
// provider builds the table → measure/column/calc-item hierarchy from the refs.
class ReferenceTreeProvider implements vscode.TreeDataProvider<TreeNode> {
    private readonly _emitter = new vscode.EventEmitter<TreeNode | undefined | void>();
    readonly onDidChangeTreeData = this._emitter.event;
    private nodes: TreeNode[] = [];
    setNodes(nodes: TreeNode[]) { this.nodes = nodes; this._emitter.fire(); }
    clear() { this.nodes = []; this._emitter.fire(); }
    getTreeItem(n: TreeNode): vscode.TreeItem {
        const item = new vscode.TreeItem(n.name, n.hasChildren ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
        item.id = n.ref;
        item.contextValue = 'ref:' + n.kind;   // 'ref:measure' / 'ref:calcColumn' / 'ref:calcitem' → the copy menu shows only on copyable leaves
        item.iconPath = iconFor(n.kind);
        if (n.ref.includes('/')) item.tooltip = 'Copy into the open model (right-click, or Ctrl+C → Ctrl+V in the Model tree)';
        return item;
    }
    getChildren(n?: TreeNode): TreeNode[] {
        if (!n) return this.nodes.filter((x) => !x.ref.includes('/'));   // top level = tables (no '/')
        const tbl = n.ref.slice(n.ref.indexOf(':') + 1);
        return this.nodes.filter((x) => { const rest = x.ref.slice(x.ref.indexOf(':') + 1); const s = rest.indexOf('/'); return s >= 0 && rest.slice(0, s) === tbl; });
    }
}

// Point the Reference tree at a model to copy FROM (file / git ref / published workspace).
async function setReferenceModelCmd(): Promise<void> {
    if (!conn) { void vscode.window.showWarningMessage('Open a model first, then set a reference model to copy from.'); return; }
    const pick = await vscode.window.showQuickPick(
        [{ label: '$(file) File…', detail: 'A .bim / .pbip / TMDL folder on disk', refKind: 'file' },
         { label: '$(git-branch) Git ref…', detail: "A commit / branch / tag of the open model's repo", refKind: 'gitref' },
         { label: '$(cloud) Workspace (XMLA)…', detail: 'A published Power BI / Fabric / AAS model', refKind: 'workspace' }],
        { placeHolder: 'Browse a model to copy objects FROM' });
    if (!pick) return;
    let ref: ModelRef | undefined;
    if (pick.refKind === 'file') {
        const path = await vscode.window.showInputBox({ prompt: 'Path to a .bim / .pbip / TMDL folder', ignoreFocusOut: true });
        if (path) ref = { kind: 'file', path };
    } else if (pick.refKind === 'gitref') {
        const gitRef = await vscode.window.showInputBox({ prompt: 'Git ref (commit / branch / tag)', value: 'HEAD', ignoreFocusOut: true });
        if (gitRef) ref = { kind: 'gitref', gitRef };
    } else {
        // Read the shared connections registry instead of a bespoke endpoint entry: a remembered model carries its
        // endpoint, dataset, auth mode and tenant, so a reference no longer re-types (or guesses azcli for) any of them.
        const known = ((await conn.sendRequest<ModelConnectionRecord[]>('listConnections').catch(() => [])) ?? []).filter((r) => r.kind === 'xmla');
        type RefPick = vscode.QuickPickItem & { record?: ModelConnectionRecord; addNew?: boolean };
        const items: RefPick[] = known.map((r): RefPick => ({
            record: r, label: `$(cloud) ${r.modelName || r.database || r.endpoint}`, description: r.label || 'Production safeguards',
            detail: `${r.endpoint}${r.database ? ` · ${r.database}` : ''}${r.lastAccount ? ` · as ${r.lastAccount}` : ''}`,
        }));
        if (items.length) items.push({ label: '', kind: vscode.QuickPickItemKind.Separator });
        items.push({ addNew: true, label: '$(add) Another published model…', detail: 'Enter an endpoint the product has not connected to yet' });
        const chosen = await vscode.window.showQuickPick(items, { placeHolder: 'Choose a remembered model to copy objects FROM', matchOnDetail: true });
        if (!chosen) return;
        if (chosen.record) {
            ref = { kind: 'workspace', endpoint: chosen.record.endpoint, database: chosen.record.database || undefined, authMode: chosen.record.authMode || 'azcli', tenantId: chosen.record.tenantId || undefined };
        } else {
            const endpoint = await vscode.window.showInputBox({ prompt: 'XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/Workspace', ignoreFocusOut: true });
            if (endpoint) { const database = await vscode.window.showInputBox({ prompt: 'Dataset name (optional)', ignoreFocusOut: true }); ref = { kind: 'workspace', endpoint, database: database || undefined, authMode: 'azcli' }; }
        }
    }
    if (!ref) return;
    referenceRef = ref;
    await loadReferenceModel();
}

async function loadReferenceModel(): Promise<void> {
    if (!conn || !referenceRef) return;
    const label = referenceRef.path ?? referenceRef.gitRef ?? referenceRef.database ?? referenceRef.endpoint ?? 'reference';
    try {
        const nodes = await conn.sendRequest<TreeNode[]>('listReferenceTree', referenceRef);
        referenceTree.setNodes(nodes);
        referenceView.title = 'Reference · ' + label;
        await vscode.commands.executeCommand('setContext', 'semanticus.hasReference', true);
        await vscode.commands.executeCommand('semanticusReference.focus');
    } catch (e: any) {
        void vscode.window.showErrorMessage('Could not load the reference model: ' + (e?.message ?? e));
    }
}

function clearReferenceModel(): void {
    referenceRef = undefined; copyStash = undefined; referenceTree.clear();
    referenceView.title = undefined;
    // Drop the engine-owned reference binding too, so the Connections drawer's Reference card clears in lockstep (MED 8).
    // Nudge the drawer to re-read ONCE the engine has actually cleared the binding (round-trip, not fire-and-forget) so
    // its Reference card + "Use as reference" state update instead of lagging a step behind (MED 7).
    void conn?.sendRequest('clearReferenceBinding')
        .catch(() => { /* best-effort — the tree is already cleared */ })
        .finally(() => { try { studioPanel?.webview.postMessage({ type: 'connectionChanged' }); } catch { /* no panel */ } });
    void vscode.commands.executeCommand('setContext', 'semanticus.hasReference', false);
    // The reference clipboard died with the reference model — fall back to the Model tree's own clipboard
    // (if any) so the Paste menu and the most-recent-copy routing stay coherent.
    if (lastCopyFrom === 'reference') lastCopyFrom = treeCopyStash ? 'model' : undefined;
    void vscode.commands.executeCommand('setContext', 'semanticus.treeClipboard', treeCopyStash?.kind);
}

// Ctrl+C in the Reference tree: stash the selected copyable objects to paste into the Model tree.
function copyRefStash(): void {
    if (!referenceRef) return;
    const sel = referenceView.selection.filter((x) => x.ref.includes('/'));   // leaf objects only (measures / columns / items)
    if (sel.length === 0) { void vscode.window.showInformationMessage('Select a measure / column / calculation item in the Reference Model tree, then Ctrl+C.'); return; }
    copyStash = { source: referenceRef, refs: sel.map((x) => x.ref) };
    lastCopyFrom = 'reference';
    // Light the Model tree's Paste menu for the reference clipboard too (the dedicated 'reference' value has
    // its own menu rule in package.json) — Ctrl+V and right-click Paste must agree on what's pasteable.
    void vscode.commands.executeCommand('setContext', 'semanticus.treeClipboard', 'reference');
    void vscode.window.setStatusBarMessage(`$(clippy) Copied ${sel.length} object(s). Ctrl+V in the Model tree to paste`, 5000);
}

// Ctrl+V in the Model tree: copy the stashed reference objects into the open model.
async function pasteIntoModelCmd(): Promise<void> {
    if (!copyStash) { void vscode.window.showInformationMessage('Nothing copied. Select objects in the Reference Model tree and press Ctrl+C first.'); return; }
    await cherryPickInto(copyStash.source, copyStash.refs);
}

async function copyRefIntoModel(nodes: TreeNode[]): Promise<void> {
    if (!referenceRef) return;
    const refs = nodes.filter((n) => n?.ref?.includes('/')).map((n) => n.ref);
    if (refs.length === 0) { void vscode.window.showInformationMessage('Pick a measure / column / calculation item to copy.'); return; }
    await cherryPickInto(referenceRef, refs);
}

async function cherryPickInto(source: ModelRef, refs: string[]): Promise<void> {
    if (!conn) return;
    try {
        const r = await conn.sendRequest<{ applied: boolean; count: number; failedRefs?: string[]; note?: string; error?: string }>('cherryPick', source, refs, true, true, 'human');
        if (r?.error) { void vscode.window.showErrorMessage(r.error); return; }
        tree.refresh();
        const failed = r?.failedRefs?.length ? `  ·  ${r.failedRefs.length} could not: ${r.failedRefs.join('; ')}` : '';
        void vscode.window.showInformationMessage((r?.note ?? `Copied ${r?.count ?? refs.length} object(s) into the open model.`) + failed);
    } catch (e: any) {
        void vscode.window.showErrorMessage('Copy failed: ' + (e?.message ?? e));
    }
}

// The ref kinds the Properties grid can actually resolve (mirrors the engine's ObjectRefs.Resolve cases). A
// webview message is untrusted input — an unsupported or bogus kind (e.g. 'namedexpr', which Resolve has no
// case for, or anything a compromised/renamed webview could post) is rejected at the door rather than being
// handed to the engine op to blank the grid. Kept in sync with Search's NAVIGABLE_KINDS.
const SELECTABLE_REF_KINDS = new Set(['table', 'measure', 'column', 'hierarchy', 'calcitem', 'partition', 'function', 'relationship', 'role', 'perspective', 'level']);

// Selection bus, Studio → Properties: repopulate the Properties view for an object a Studio navigator
// focused/selected — exactly the update a native-tree selection triggers (the same showObject path), but
// WITHOUT focusing the view: Studio keeps the keyboard, Properties just follows.
function selectRefInProperties(ref: unknown): void {
    if (typeof ref !== 'string' || !ref.includes(':')) return;
    const p = refParts(ref);
    if (!p.kind || !p.name || !SELECTABLE_REF_KINDS.has(p.kind)) return;   // untrusted input: only known-resolvable kinds
    void propGrid.showObject({ ref, name: p.name, kind: p.kind, hasChildren: false });
}

// Reveal + select an object in the Model tree by its ref (driven from the Studio webview's right-click).
async function revealRefInTree(ref: string): Promise<void> {
    if (!treeView) return;
    const p = refParts(ref);
    const node: TreeNode = { ref, name: p.name, kind: p.kind, hasChildren: false };
    try {
        await vscode.commands.executeCommand('semanticusModel.focus');   // make sure the view is visible
        await treeView.reveal(node, { select: true, focus: true, expand: false });
    } catch (e: any) {
        out.appendLine('revealInTree failed for ' + ref + ': ' + (e?.message ?? e));
        const parent = await tree.getParent(node);                        // fallback: at least surface the parent (getParent is async — resolves the folder chain)
        if (parent) { try { await treeView.reveal(parent, { select: false, focus: true, expand: true }); } catch { /* give up */ } }
    }
}

// Kinds whose objects carry an editable DAX expression (single-click → Monaco).
const DAX_KINDS = new Set(['measure', 'function', 'calcColumn', 'calcitem']);

function iconFor(kind: string): vscode.ThemeIcon {
    switch (kind) {
        case 'table': return new vscode.ThemeIcon('table');
        case 'calcgroup': return new vscode.ThemeIcon('symbol-namespace');
        case 'measure': return new vscode.ThemeIcon('symbol-operator');
        case 'column': return new vscode.ThemeIcon('symbol-field');
        case 'calcColumn': return new vscode.ThemeIcon('symbol-constant');
        case 'hierarchy': return new vscode.ThemeIcon('list-tree');
        case 'level': return new vscode.ThemeIcon('symbol-field');
        case 'partition': return new vscode.ThemeIcon('database');
        case 'calcitem': return new vscode.ThemeIcon('symbol-operator');
        case 'relationship': return new vscode.ThemeIcon('references');
        case 'role': return new vscode.ThemeIcon('shield');
        case 'perspective': return new vscode.ThemeIcon('eye');
        case 'function': return new vscode.ThemeIcon('symbol-function');
        case 'dfolder':            // a display folder (member grouping) — same glyph as the collection folders
        case 'folderRelationships':
        case 'folderRoles':
        case 'folderPerspectives': return new vscode.ThemeIcon('folder');
        default: return new vscode.ThemeIcon('symbol-misc');
    }
}

// ---- editable DAX as virtual files (Monaco) --------------------------------------------------

function uriForRef(ref: string): vscode.Uri {
    return vscode.Uri.parse(`${DAX_SCHEME}:/${encodeURIComponent(ref)}.dax`);
}
// The header-doc uri for a create-then-edit ref (CRITICAL 2): the SAME path as uriForRef (so getDax/setDax resolve the
// ref from the path unchanged) PLUS a query that STAMPS the identity onto the uri — hdr=1 marks it a header doc and mk
// carries the owning model's key. Uris persist across window reloads, so this identity survives a reload where the
// in-memory registry is gone, and mk makes a cross-model dirty buffer detectable at Save (decideDaxSave rejects it).
function uriForHeaderRef(ref: string): vscode.Uri {
    const mk = daxHeaderModelKey ? '&mk=' + identityToken(daxHeaderModelKey) : '';
    return vscode.Uri.parse(`${DAX_SCHEME}:/${encodeURIComponent(ref)}.dax?hdr=1${mk}`);
}
function isHeaderUri(uri: vscode.Uri): boolean {
    return new URLSearchParams(uri.query).get('hdr') === '1';
}
function headerUriModelKey(uri: vscode.Uri): string | undefined {
    const mk = new URLSearchParams(uri.query).get('mk');
    return mk === null ? undefined : mk;
}
function refFromUri(uri: vscode.Uri): string {
    // uri.path is ALREADY percent-decoded by VS Code's Uri (it reverses what encodeURIComponent did in uriForRef —
    // %3A→':', %2F→'/', %25→'%'), so it is the exact original ref. Do NOT decode a second time: a stray
    // decodeURIComponent throws "URIError: URI malformed" on any name with a literal '%' (e.g. a measure "Margin %"),
    // which is what broke opening %-suffixed DAX in readFile/writeFile.
    let p = uri.path.startsWith('/') ? uri.path.slice(1) : uri.path;
    if (p.endsWith('.dax')) p = p.slice(0, -4);
    return p;
}

// Zero-dialog authoring: the uris (of objects CREATED this session via create-then-edit) whose DAX document carries
// a name-header line 1. Registered at create time; transferred to the new uri when the header is used to rename;
// cleared on model swap. A registered doc ALWAYS shows the header (readFile adds it, writeFile strips it) for the
// life of the registration -- so a repeat save never mistakes the header for DAX. Objects opened from the tree are
// NOT registered (their editor is body-only, exactly as before); those rename via F2 / the Properties Name row.
// A fast in-session CACHE of header-doc uris; the URI query (hdr=1) is the TRUTH (isDaxHeaderDoc consults both, so a
// reload-restored header uri is recognized even though the in-memory set is empty). Registered at create time, pruned
// on close, wiped on model swap.
const daxHeaderDocs = new Set<string>();
// A header-doc whose rename COMMITTED but whose body write FAILED: the editor stays open + dirty with the user's DAX on
// the OLD uri. We remember the object's new ref + the session it was renamed in (model token + session id + revision) so
// the NEXT save on that uri can, as an IMMEDIATE retry (same session, nothing mutated since), write the body to the
// renamed object and re-home the (clean) editor -- preserving the unsaved DAX in place (HIGH 3). PERSISTED in
// workspaceState (not just memory): a window reload after the partial failure would otherwise leave a dirty editor
// blindly retrying the DEAD old ref with an empty in-memory map. CRITICAL 1 (r8): across a reload the session changes, so
// the record can no longer RESUME (name-based refs can't prove the object's identity across sessions) — it then serves to
// REFUSE that save safely and point the user back to the tree. Keyed by the model TOKEN so a record can never be resumed
// into a different model; the session+revision fence is validated against the live session at Save (see decideRenameRecovery).
const DAX_PENDING_RENAME_KEY = 'semanticus.daxPendingRename';
function pendingRenames(): Record<string, PendingRenameRecord> {
    return extCtx?.workspaceState.get<Record<string, PendingRenameRecord>>(DAX_PENDING_RENAME_KEY) ?? {};
}
function getPendingRename(uriKey: string): PendingRenameRecord | undefined { return pendingRenames()[uriKey]; }
async function setPendingRename(uriKey: string, rec: PendingRenameRecord): Promise<void> {
    await extCtx?.workspaceState.update(DAX_PENDING_RENAME_KEY, { ...pendingRenames(), [uriKey]: rec });
}
async function clearPendingRename(uriKey: string): Promise<void> {
    const all = pendingRenames();
    if (uriKey in all) { const next = { ...all }; delete next[uriKey]; await extCtx?.workspaceState.update(DAX_PENDING_RENAME_KEY, next); }
}
// CRITICAL 2: a pending-rename record (including a fail-closed tombstone) is dangerous only while its editor is still open
// and could save. Once the doc is gone, the record is safe to drop. onDidCloseTextDocument handles a live clean close, but
// a doc closed while the extension was inactive leaves an orphan record — so at activation we sweep records whose URI has
// NO open tab AND no open text document. A restored dirty doc (window reload) is present in the tabs/docs, so its record is
// KEPT; we only clear the provably-orphaned ones. Fail-closed: when unsure, keep.
async function sweepClosedPendingRenames(): Promise<void> {
    const all = pendingRenames();
    const keys = Object.keys(all);
    if (keys.length === 0) return;
    const open = new Set<string>();
    for (const d of vscode.workspace.textDocuments) open.add(d.uri.toString());
    for (const g of vscode.window.tabGroups.all) {
        for (const tab of g.tabs) { if (tab.input instanceof vscode.TabInputText) open.add(tab.input.uri.toString()); }
    }
    for (const key of keys) { if (!open.has(key)) await clearPendingRename(key); }
}
function isDaxHeaderDoc(uri: vscode.Uri): boolean { return isHeaderUri(uri) || daxHeaderDocs.has(uri.toString()); }
// The session id + the CURRENT model's key. The session id keys the cache wipe (any swap clears the in-memory set); the
// model key (source path when saved, else the session id) is the STABLE, reload-surviving identity stamped onto header
// uris — a per-process session counter would false-reject a legitimately restored dirty buffer after a window reload.
let daxHeaderSessionId: string | undefined;
let daxHeaderModelKey: string | undefined;
// The canonical model-identity string for a session: the source path (stable across a window reload) when the model is
// saved, else the ephemeral session id. identityToken() turns this into the url-safe token stamped on header uris. One
// helper so setStatusFromInfo, createThenEdit and writeFile all key off the SAME notion of "which model is this".
function modelKeyOf(info?: SessionInfo): string | undefined { return info?.source || info?.sessionId; }
function clearDaxHeaderDocsOnSwap(sessionId?: string): void {
    // Wipe only the in-memory header CACHE (the uri is the truth, so this is just a fast-path reset). Pending renames are
    // deliberately NOT cleared here (HIGH 3): they are persisted to survive a window reload -- which itself looks like a
    // session swap -- and a stale record can never be misapplied because decideRenameRecovery re-checks the model token
    // and authoritative engine state before resuming.
    if (sessionId !== daxHeaderSessionId) { daxHeaderSessionId = sessionId; daxHeaderDocs.clear(); }
}

// After a header-driven rename the object's ref (its identity is its name) changed, so the old uri is now stale.
// Close its tab and reopen the renamed object under the new ref (re-registered, so its header follows the new name).
// The registry transfer happens FIRST (synchronously) so a save racing the tab swap already sees the new uri.
async function reopenRenamedDaxNow(oldUri: vscode.Uri, newRef: string): Promise<void> {
    // Only reached on the CLEAN path (rename + body both committed), so the old tab is no longer dirty and closing it
    // loses nothing. (A partial rename never re-homes -- it keeps the dirty editor in place; see writeFile / HIGH 3.)
    try {
        const newUri = uriForHeaderRef(newRef);
        daxHeaderDocs.delete(oldUri.toString());
        void clearPendingRename(oldUri.toString());
        daxHeaderDocs.add(newUri.toString());
        for (const g of vscode.window.tabGroups.all) {
            for (const tab of g.tabs) {
                if (tab.input instanceof vscode.TabInputText && tab.input.uri.toString() === oldUri.toString()) {
                    try { await vscode.window.tabGroups.close(tab); } catch { /* already closed */ }
                }
            }
        }
        await openDaxAt(newUri, newRef);
    } catch (e: any) {
        // Re-home is best-effort, but a failure must NOT be silent (HIGH 3): the tree already reflects the rename, yet
        // the editor may still show the old name. Tell the user so they can reopen it from the tree.
        vscode.window.showWarningMessage(`Renamed, but couldn't reopen the editor on the new name (${e?.message ?? e}). Reopen it from the Model tree.`);
    }
}

// The clean-path re-home: defer past the in-flight save (writeFile is mid-return), then swap the tab.
function reopenRenamedDax(oldUri: vscode.Uri, newRef: string): void {
    setTimeout(() => { void reopenRenamedDaxNow(oldUri, newRef); }, 0);
}

// A measure / calc object stored as a SINGLE line (agent- or import-authored — e.g. a VAR/RETURN measure with no
// line breaks) is unreadable in the editor and especially in Peek Definition. Pretty-print it for display via the
// offline formatter (semantics-preserving + idempotent). DAX the user already wrote across multiple lines is shown
// verbatim (we only touch the single-line case); any formatter error falls back to the raw expression. On save the
// displayed text is what's written, so a one-line measure becomes nicely formatted after its first save, then stays.
function displayDax(raw: string): string {
    if (!raw || raw.includes('\n')) return raw;
    try {
        const maxLine = vscode.workspace.getConfiguration('semanticus').get<number>('dax.maxLineLength', 60);
        return formatDax(raw, { indent: '    ', maxLine });
    } catch { return raw; }
}

async function openDaxAt(uri: vscode.Uri, ref: string) {
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.languages.setTextDocumentLanguage(doc, 'dax');
    await vscode.window.showTextDocument(doc, { preview: false });
    validateDaxDoc(doc);   // language is set after open, so onDidOpen may have missed it — lint now
    void ref;              // ref is implied by the uri path; kept in the signature for call-site clarity
}
async function openDax(n: TreeNode) {
    return openDaxAt(uriForRef(n.ref), n.ref);   // tree-opened: the PLAIN uri (body-only editor, no name header)
}

// Find in Model — a live quick-pick over the engine's read-only search_model (names / descriptions / DAX).
// Picking a DAX-bearing object opens its DAX editor; anything else opens in the Properties grid.
async function findInModelCmd(context: vscode.ExtensionContext) {
    if (!conn) { vscode.window.showWarningMessage('Connect to a model first.'); return; }
    const qp = vscode.window.createQuickPick<vscode.QuickPickItem & { hit?: SearchHit }>();
    qp.placeholder = 'Find in model: names, descriptions, DAX expressions…';
    qp.matchOnDescription = true;
    qp.matchOnDetail = true;
    // The quick-pick is the fast launcher; the Studio "Search" tab is the full find/replace surface. This button
    // hands the current query off to it (the panel has replace, facets, regex/whole-word, and the reference-safe rename).
    const toSearchTab: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('replace-all'), tooltip: 'Open in Search & Replace' };
    qp.buttons = [toSearchTab];
    qp.onDidTriggerButton((b) => { if (b === toSearchTab) { const q = qp.value.trim(); qp.hide(); navigateStudio(context, 'search', q || undefined); } });
    let seq = 0;
    qp.onDidChangeValue(async (val) => {
        const q = val.trim();
        if (q.length < 2) { qp.items = []; qp.title = undefined; return; }
        const mine = ++seq;
        qp.busy = true;
        try {
            const res = await conn!.sendRequest<SearchResult>('searchModel', q, 200);
            if (mine !== seq) return;   // a newer keystroke superseded this result
            qp.items = res.hits.map((h) => ({
                label: `$(${iconForKind(h.kind)}) ${h.name}`,
                description: `${h.kind}${h.table ? ' · ' + h.table : ''} · ${h.where.toLowerCase()}`,
                detail: h.where !== 'Name' ? h.snippet : undefined,
                hit: h,
            }));
            qp.title = res.total === 0 ? 'No matches' : res.truncated ? `${res.total} matches (showing ${res.hits.length})` : `${res.total} match${res.total === 1 ? '' : 'es'}`;
        } catch { /* transient (model closing) — ignore */ } finally { if (mine === seq) qp.busy = false; }
    });
    qp.onDidAccept(async () => {
        const hit = qp.selectedItems[0]?.hit;
        qp.hide();
        if (!hit) return;
        const node: TreeNode = { ref: hit.ref, name: hit.name, kind: hit.kind, hasChildren: false };
        if (DAX_KINDS.has(hit.kind)) await openDax(node);
        else { await propGrid.showObjects([node]); await vscode.commands.executeCommand('semanticusProperties.focus'); }
    });
    qp.onDidHide(() => qp.dispose());
    qp.show();
}

function iconForKind(kind: string): string {
    switch (kind) {
        case 'measure': return 'symbol-operator';
        case 'column': case 'calcColumn': return 'symbol-field';
        case 'table': case 'calcgroup': return 'table';
        case 'hierarchy': return 'list-tree';
        case 'calcitem': return 'symbol-enum-member';
        case 'function': return 'symbol-function';
        default: return 'symbol-misc';
    }
}

class DaxFileSystem implements vscode.FileSystemProvider {
    private readonly _emitter = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this._emitter.event;
    private readonly mtimes = new Map<string, number>();
    private connection: MessageConnection | undefined;

    constructor(private readonly getConn: () => MessageConnection | undefined) {}
    attach(c: MessageConnection) { this.connection = c; }
    private conn() { return this.connection ?? this.getConn(); }

    signalChanged(ref: string) {
        // Fire for the plain (tree-opened) uri AND any registered header-doc uri of the same ref (they share the path
        // but differ by query), so a live edit to a freshly-created object refreshes its header editor too.
        const plain = uriForRef(ref);
        const uris = [plain];
        for (const key of daxHeaderDocs) {
            try { const u = vscode.Uri.parse(key); if (u.toString() !== plain.toString() && refFromUri(u) === ref) uris.push(u); } catch { /* not a uri */ }
        }
        for (const uri of uris) {
            this.mtimes.set(uri.toString(), Date.now());
            this._emitter.fire([{ type: vscode.FileChangeType.Changed, uri }]);
        }
    }

    async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
        const mtime = this.mtimes.get(uri.toString()) ?? Date.now();
        let size = 0;
        try {
            const dax = await this.conn()?.sendRequest<string>('getDax', refFromUri(uri));
            size = Buffer.byteLength(this.withHeader(uri, displayDax(dax ?? '')), 'utf8');   // match readFile's (possibly header + formatted) content
        } catch { /* object may not exist yet */ }
        return { type: vscode.FileType.File, ctime: 0, mtime, size, permissions: undefined };
    }

    async readFile(uri: vscode.Uri): Promise<Uint8Array> {
        const dax = await this.conn()?.sendRequest<string>('getDax', refFromUri(uri));
        // Pretty-print a single-line measure so it opens/peeks readably (multi-line DAX is shown as authored).
        return Buffer.from(this.withHeader(uri, displayDax(dax ?? '')), 'utf8');
    }

    // Prepend the create-then-edit name header for a header doc (URI truth, cache fallback); body-only otherwise.
    private withHeader(uri: vscode.Uri, body: string): string {
        if (!isDaxHeaderDoc(uri)) return body;
        const header = buildDaxHeader(refFromUri(uri));
        return header ? header + '\n' + body : body;
    }

    // The live model's identity, fetched fresh from the engine in ONE sessionInfo read (so the token, session id and
    // revision can never describe two different reads): the url-safe TOKEN (a durable, reload-surviving key — source path
    // when saved, else the session id — compared against the header uri's owning-model key for OWNERSHIP), and the live
    // SESSION ID. CRITICAL 1: the session id is passed to the mutation RPCs as `expectedSession` so the engine can refuse
    // the write atomically — inside its single-writer dispatch — if the model swapped before it lands, closing the
    // residual sub-RPC-window race the extension alone could not. Fencing on the session id (not the source path) fences
    // an UNSAVED model too and catches a same-file REOPEN (same source, brand-new session). A session id always exists
    // when connected, so the header-save path never passes null (see writeFile — it fails closed if it is ever missing).
    // `revision` (round 7/8): the same read also carries the live session revision — a monotonic per-mutation counter. It
    // fences the header-save VERIFIED-RESUMPTION writes as expectedRevision: the rename→body pair fences the body on the
    // rename's returned revision, and a partial-rename RECOVERY resumes ONLY when this live revision still equals the one
    // the rename recorded (CRITICAL 1, round 8) — proving nothing mutated since — then fences the write on it, so a
    // same-session delete+recreate at the resumed name is refused inside the engine's dispatch.
    private async liveModelIdentity(conn: MessageConnection): Promise<{ token?: string; sessionId?: string; revision?: number }> {
        const info = await conn.sendRequest<SessionInfo>('sessionInfo').catch(() => undefined);
        const k = modelKeyOf(info);
        return { token: k ? identityToken(k) : undefined, sessionId: info?.sessionId ?? undefined, revision: info?.revision };
    }

    // Just the identity TOKEN (a thin wrapper over liveModelIdentity for callers that only compare the uri's owner).
    private async liveModelToken(conn: MessageConnection): Promise<string | undefined> {
        return (await this.liveModelIdentity(conn)).token;
    }

    // CRITICAL 1 (b): a belt-and-suspenders backstop. The engine now fences every header-save write on the session id
    // (saved AND unsaved), so a mismatched write is refused outright; this re-reads identity AFTER the writes land and
    // surfaces any confirmed drift honestly, pointing at the undo/Edit-History trail, in case a token moved for a reason
    // the id fence didn't cover. Only warns on a CONFIRMED drift (both tokens known and different).
    private async warnIfModelDrifted(conn: MessageConnection, uriModelKey: string | undefined): Promise<void> {
        const live = await this.liveModelToken(conn);
        if (uriModelKey !== undefined && live !== undefined && uriModelKey !== live) {
            void vscode.window.showWarningMessage('The model changed while saving. Check Edit History and undo if this landed on the wrong model.');
        }
    }

    async writeFile(uri: vscode.Uri, content: Uint8Array): Promise<void> {
        const raw = Buffer.from(content).toString('utf8');
        const conn = this.conn();
        const key = uri.toString();
        const oldRef = refFromUri(uri);
        const { header, body } = splitDaxHeader(raw);
        const isHeaderDoc = isDaxHeaderDoc(uri);
        // Identity is URI-borne, not content-based (CRITICAL 2), and the LIVE model key is AUTHORITATIVE (CRITICAL 1):
        // fetch it from the engine (sessionInfo RPC) at Save time rather than trusting the cached daxHeaderModelKey
        // global, which an MCP-door model swap could have left stale (saving A's dirty header would then pass an
        // A-vs-A(cached) check while the RPCs target current model B). decideDaxSave folds isHeaderUri, the
        // missing/cross-model identity checks, line-1 validity and the duplicate-header guard into one honest verdict.
        const uriModelKey = headerUriModelKey(uri);
        let liveModelKey: string | undefined;
        let liveSession: string | undefined;   // CRITICAL 1: passed to the mutation RPCs as expectedSession (engine-side swap fence)
        let liveRevision: number | undefined;   // CRITICAL (r7/r8): the live session revision (same one sessionInfo read) — the recovery-resume proof + write fence
        if (isHeaderDoc && conn) { const id = await this.liveModelIdentity(conn); liveModelKey = id.token; liveSession = id.sessionId; liveRevision = id.revision; }
        const decision = decideDaxSave(oldRef, header, body, {
            isHeaderUri: isHeaderDoc,
            uriModelKey,
            modelKey: liveModelKey,
        });

        if (decision.kind === 'reject') {
            // A header doc that can't be saved as-is (missing / wrong model identity, or a deleted / malformed /
            // wrong-kind / duplicated header): REFUSE before any RPC so no DAX is lost and no wrong object is touched.
            // Throwing keeps the document DIRTY and intact for the analyst to fix.
            throw new Error(decision.reason);
        }

        if (decision.kind === 'header') {
            daxHeaderDocs.add(key);   // keep the cache warm (URI is truth; this just speeds later reads)
            if (!conn) throw new Error('Not connected to the engine yet, so this cannot be saved.');
            // CRITICAL 1: the header-save path must ALWAYS fence on the live session id — it must never fall open to an
            // unfenced (null) write. decideDaxSave already rejected a missing model token above, and liveSession shares
            // that one sessionInfo read, so this is a fail-closed backstop (and narrows liveSession to a string).
            if (!liveSession) throw new Error("Couldn't confirm the live model session, so nothing was saved. Reopen the object from the Model tree, then Save.");
            // HIGH 3 / CRITICAL 1 (round 8): a PRIOR save may have committed the rename but FAILED the body write, leaving
            // this dirty editor on the now-stale oldRef and a PERSISTED record of the ref it was renamed to (+ the session
            // and revision the rename committed at). Refs are NAME-based, so an object now living at the new name is NOT
            // provably the one we renamed — a delete+recreate could have put an impostor there, and no liveness probe can
            // tell them apart. So the ONLY safe resume is the IMMEDIATE retry: the SAME session with the revision STILL the
            // rename's (nothing mutated since). decideRenameRecovery makes that call from the live session + revision we
            // sampled at Save time; any drift refuses (a moved-revision impostor risk tombstones, a cross-session reload
            // just refuses). The one resume then CAS-fences the write on record.revision, so an interleaving mutation is
            // refused atomically inside the engine's dispatch. (This dropped the old liveness probes — a probe can't vouch
            // for identity — so refExists / the getDax absent-marker are gone.)
            const pending = getPendingRename(key);
            let baseRef = oldRef;
            // CRITICAL 1 (r8): the revision the recovery write is CAS-fenced on. undefined on the normal (no-pending) path —
            // the rename→body pair fences its BODY write on the rename's returned revision instead, and a genuine plain save
            // stays unfenced (SCOPE RULING below). On a partial-rename resume it is the rename's recorded revision (which the
            // recovery just proved still equals the live one) — the engine refuses the write if anything mutated since.
            let recoveryRevision: number | undefined;
            if (pending) {
                const recovery = decideRenameRecovery(pending, oldRef, liveModelKey, liveSession, liveRevision);
                if (recovery.kind === 'reject') {
                    // The rename can't be safely resumed, but the editor is still open + dirty. NEVER prune the record here —
                    // a pruned guard would let a LATER save hit an object recreated at the new name. When the verdict is
                    // TOMBSTONE (same session, but the revision moved — a possible impostor), persist state:'dead' so this and
                    // every future save is permanently refused; a cross-session reject leaves the record untouched (HIGH 2).
                    if (recovery.tombstone && pending.state !== 'dead') await setPendingRename(key, { ...pending, state: 'dead' });
                    throw new Error(recovery.reason);
                }
                baseRef = recovery.baseRef;      // the ONE safe resume: the object's renamed ref (never the possibly-recreated old name)
                recoveryRevision = pending.revision;   // the immediate-retry CAS fence: the engine refuses if anything mutated since the rename
            }
            const oldName = refParts(baseRef).name;
            const newName = decision.name;
            if (newName !== oldName) {
                // Two tracked changes, sequenced honestly: attempt the RENAME FIRST. If it is refused (e.g. a
                // duplicate name), NOTHING is written and the doc stays dirty — the old object is never silently
                // overwritten with the body under a name the user was trying to leave.
                let renamed: RenameResult;
                try {
                    // CRITICAL 1: expectedSession lets the engine refuse this rename atomically (inside its single-writer
                    // dispatch) if the model swapped since liveSession was sampled — no wrong-model write can slip through.
                    // CRITICAL (r7/r8): on the RECOVERY path (recoveryRevision set) also fence the rename on the rename's
                    // recorded revision, which the recovery just proved still live, so any mutation since then refuses it;
                    // undefined on the normal first-save (the rename is the first op — nothing to fence against; its returned
                    // revision fences the body write below instead).
                    renamed = await conn.sendRequest<RenameResult>('renameObject', baseRef, newName, 'human', liveSession, recoveryRevision);
                } catch (e: any) {
                    throw new Error(`Could not rename to "${newName}": ${e?.message ?? e} Nothing was saved. Pick a free name in line 1, then Save.`);
                }
                // Re-key to the ref the ENGINE returned (authoritative — never a slash-spliced guess); fall back to
                // the known-old-name suffix strip only if a build ever omits newRef.
                const newRef = renamed?.newRef ? renamed.newRef : reKeyRef(baseRef, oldName, newName);
                try {
                    // CRITICAL 1: same engine-side session fence on the body write (a swap between the rename and here is refused).
                    // CRITICAL (r7): fence the body write on the RENAME'S returned revision — the verified-resumption claim of the
                    // pair. A delete+recreate of the just-renamed object (at its new name) between the rename commit and here bumps
                    // the revision, so the engine refuses the body rather than land it on the unrelated same-named impostor.
                    await conn.sendRequest<SetResult>('setDax', newRef, body, 'human', liveSession, renamed.revision);
                } catch (e: any) {
                    // Partial state: the rename COMMITTED but the body write failed. Do NOT close the editor — keep it open +
                    // dirty so the user's DAX is PRESERVED IN PLACE (HIGH 3). PERSIST the new ref + the SESSION it committed in
                    // (model token + session id) + the rename's revision. CRITICAL 1 (r8): session id + revision are the
                    // resume fence, not mere provenance — the NEXT save resumes ONLY as an immediate retry (same session, that
                    // exact revision still live), and CAS-fences the body write on it; any drift refuses (see decideRenameRecovery).
                    await setPendingRename(key, { newRef, modelKey: liveModelKey ?? '', sessionId: liveSession, revision: renamed.revision });
                    throw new Error(`Renamed to "${newName}", but saving the DAX failed: ${e?.message ?? e} Your DAX is kept here. Fix it and Save again.`);
                }
                await clearPendingRename(key);
                this.mtimes.set(key, Date.now());
                await this.warnIfModelDrifted(conn, uriModelKey);   // CRITICAL 1 (b): flag a swap that slipped the residual race
                reopenRenamedDax(uri, newRef);   // the ref changed (rename, or a resume off a renamed base) — re-home the editor
                void vscode.window.setStatusBarMessage(`$(check) Renamed to "${newName}" and saved. Any DAX references and Q&A synonyms follow the rename.`, 5000);
                return;
            }
            // Name unchanged (relative to the object's real current ref): strip the header, save the body only.
            // SCOPE RULING (r7/r8): the revision fence (recoveryRevision) applies ONLY where the object was renamed under us —
            // i.e. a RECOVERY resume (baseRef !== oldRef): the object lives at a ref we never named it to, so the write is
            // CAS-fenced on the rename's revision the recovery just proved still live, and any same-session mutation after
            // that proof (a delete+recreate impostor) refuses. A GENUINE plain save (no pending; baseRef === oldRef,
            // recoveryRevision undefined) stays name-addressed and UNFENCED — that is the product-wide set-by-ref semantic
            // every door (MCP + UI) uses; fencing a plain save on the GLOBAL revision would make it refuse constantly under
            // concurrent agent activity, for no safety gain the other doors have. The fence is reserved for this feature's
            // VERIFIED-RESUMPTION writes (the rename→body pair and recovery), not every save.
            try {
                // CRITICAL 1: the engine refuses this write if the model swapped since liveSession was sampled.
                await conn.sendRequest<SetResult>('setDax', baseRef, body, 'human', liveSession, recoveryRevision);
            } catch (e: any) {
                throw new Error(`Saving the DAX failed: ${e?.message ?? e} Your DAX is kept here. Fix it and Save again.`);
            }
            this.mtimes.set(key, Date.now());
            await this.warnIfModelDrifted(conn, uriModelKey);   // CRITICAL 1 (b): flag a swap that slipped the residual race
            if (baseRef !== oldRef) {
                // A prior partial rename is now fully resolved (the body reached the renamed object) — clear the
                // pending state and finally re-home the (now clean) editor onto the renamed ref.
                await clearPendingRename(key);
                reopenRenamedDax(uri, baseRef);
            }
            return;
        }

        // decision.kind === 'body': a plain (non-header) DAX editor — save the whole document as the expression.
        await conn?.sendRequest<SetResult>('setDax', oldRef, raw, 'human');
        this.mtimes.set(key, Date.now());
    }

    watch(): vscode.Disposable { return new vscode.Disposable(() => { /* no-op */ }); }
    readDirectory(): [string, vscode.FileType][] { return []; }
    createDirectory(): void { /* no-op */ }
    delete(): void { throw vscode.FileSystemError.NoPermissions('delete not supported'); }
    rename(): void { throw vscode.FileSystemError.NoPermissions('rename not supported'); }
}

// ---- DAX language service (IntelliSense) -----------------------------------------------------
// Model-aware completions: the DAX functions below + the LIVE model's tables / measures / columns,
// rebuilt from the tree on connect and on every model/didChange (debounced). After 'Table'[ we narrow
// to that table's columns. Syntax highlighting comes from language/dax.tmLanguage.json (the grammar).

interface DaxMeasureSym { name: string; ref: string; }
interface DaxColumnSym { table: string; name: string; ref: string; kind: 'column' | 'calcColumn' }
interface DaxSymbols { tables: string[]; measures: DaxMeasureSym[]; columns: DaxColumnSym[]; functions: DaxMeasureSym[]; }
let daxSymbols: DaxSymbols = { tables: [], measures: [], columns: [], functions: [] };
let daxIndex: DaxIndex | null = null;          // lowercased lookup index for the linter (null until a model loads)
let daxSymbolTimer: ReturnType<typeof setTimeout> | undefined;

function scheduleDaxSymbolRebuild() {
    if (daxSymbolTimer) clearTimeout(daxSymbolTimer);
    daxSymbolTimer = setTimeout(() => { void rebuildDaxSymbols(); }, 400);
}

async function rebuildDaxSymbols() {
    if (!conn) return;
    try {
        const roots = await conn.sendRequest<TreeNode[]>('listTree', null);
        const tnodes = roots.filter((t) => t.kind === 'table' || t.kind === 'calcgroup');
        const syms: DaxSymbols = {
            tables: tnodes.map((t) => t.name), measures: [], columns: [],
            functions: roots.filter((r) => r.kind === 'function').map((f) => ({ name: f.name, ref: f.ref })),   // model UDFs
        };
        for (const t of tnodes) {
            const kids = await conn.sendRequest<TreeNode[]>('listTree', t.ref);
            for (const k of kids) {
                if (k.kind === 'measure') syms.measures.push({ name: k.name, ref: k.ref });
                else if (k.kind === 'column' || k.kind === 'calcColumn') syms.columns.push({ table: t.name, name: k.name, ref: k.ref, kind: k.kind });
            }
        }
        daxSymbols = syms;
        daxIndex = buildDaxIndex(syms);
        revalidateOpenDaxDocs();               // model changed → refresh squiggles on every open DAX editor
    } catch { /* model may be closed */ }
}

function buildDaxIndex(s: DaxSymbols): DaxIndex {
    const columnsByTable = new Map<string, Set<string>>();
    const allColumns = new Set<string>();
    for (const c of s.columns) {
        const t = c.table.toLowerCase();
        (columnsByTable.get(t) ?? columnsByTable.set(t, new Set()).get(t)!).add(c.name.toLowerCase());
        allColumns.add(c.name.toLowerCase());
    }
    return {
        tables: new Set(s.tables.map((t) => t.toLowerCase())),
        columnsByTable,
        measures: new Set(s.measures.map((m) => m.name.toLowerCase())),
        allColumns,
    };
}

const daxCompletionProvider: vscode.CompletionItemProvider = {
    provideCompletionItems(doc, pos) {
        const line = doc.lineAt(pos).text.slice(0, pos.character);
        const items: vscode.CompletionItem[] = [];
        // After 'Table'[ (or Table[) narrow to that table's columns, by bare name.
        const tableCtx = /'([^']+)'\[[^\]]*$|(\b[A-Za-z_][\w ]*?)\[[^\]]*$/.exec(line);
        if (tableCtx) {
            const tbl = (tableCtx[1] ?? tableCtx[2] ?? '').trim();
            for (const c of daxSymbols.columns.filter((c) => c.table === tbl)) {
                const it = new vscode.CompletionItem(c.name, vscode.CompletionItemKind.Field);
                it.detail = `column · ${c.table}`;
                items.push(it);
            }
            if (items.length) return items;
        }
        // VAR names declared above the cursor (so `RETURN <var>` / reuse completes).
        for (const v of collectVars(doc.getText(new vscode.Range(new vscode.Position(0, 0), pos)))) {
            const it = new vscode.CompletionItem(v, vscode.CompletionItemKind.Variable);
            it.detail = 'variable'; it.sortText = '0_' + v;   // surface vars above functions
            items.push(it);
        }
        for (const kw of DAX_KEYWORDS) {
            const it = new vscode.CompletionItem(kw, vscode.CompletionItemKind.Keyword);
            it.insertText = kw + ' ';
            it.detail = 'keyword';
            items.push(it);
        }
        for (const fn of DAX_FUNCTIONS) {
            const it = new vscode.CompletionItem(fn, vscode.CompletionItemKind.Function);
            it.insertText = new vscode.SnippetString(`${fn}($0)`);
            it.detail = 'DAX function';
            const sig = DAX_SIGNATURES[fn];
            if (sig) { const md = new vscode.MarkdownString(); md.appendCodeblock(`${fn}(${sig.params.join(', ')})`, 'dax'); md.appendMarkdown('\n' + sig.doc); it.documentation = md; }
            items.push(it);
        }
        for (const f of daxSymbols.functions) {   // model-defined functions (UDFs)
            const it = new vscode.CompletionItem(f.name, vscode.CompletionItemKind.Function);
            it.insertText = new vscode.SnippetString(`${f.name}($0)`);
            it.detail = 'model function';
            it.sortText = '1_' + f.name;          // surface model functions just under the built-ins
            items.push(it);
        }
        for (const t of daxSymbols.tables) {
            const it = new vscode.CompletionItem(`'${t}'`, vscode.CompletionItemKind.Class);
            it.detail = 'table';
            items.push(it);
        }
        for (const m of daxSymbols.measures) {
            const it = new vscode.CompletionItem(`[${m.name}]`, vscode.CompletionItemKind.Field);
            it.detail = 'measure';
            items.push(it);
        }
        for (const c of daxSymbols.columns) {
            const it = new vscode.CompletionItem(`'${c.table}'[${c.name}]`, vscode.CompletionItemKind.Variable);
            it.detail = `column · ${c.table}`;
            items.push(it);
        }
        return items;
    },
};

// DAX keywords (not functions — no parens). VAR/RETURN drive the formatter's block layout too.
const DAX_KEYWORDS = ['VAR', 'RETURN', 'NOT', 'IN', 'DEFINE', 'EVALUATE', 'MEASURE', 'ORDER BY', 'START AT', 'ASC', 'DESC'];

// The full DAX function library (~270 functions, all categories). VS Code highlighting is grammar-based (any
// NAME( ), so this list drives COMPLETION. KEEP IN SYNC with webview/src/dax.ts.
const DAX_FUNCTIONS = [
    // Aggregation
    'APPROXIMATEDISTINCTCOUNT', 'AVERAGE', 'AVERAGEA', 'AVERAGEX', 'COUNT', 'COUNTA', 'COUNTAX', 'COUNTBLANK', 'COUNTROWS', 'COUNTX', 'DISTINCTCOUNT', 'DISTINCTCOUNTNOBLANK', 'MAX', 'MAXA', 'MAXX', 'MIN', 'MINA', 'MINX', 'PRODUCT', 'PRODUCTX', 'SUM', 'SUMX',
    // Date & time
    'CALENDAR', 'CALENDARAUTO', 'DATE', 'DATEDIFF', 'DATEVALUE', 'DAY', 'EDATE', 'EOMONTH', 'HOUR', 'MINUTE', 'MONTH', 'NETWORKDAYS', 'NOW', 'QUARTER', 'SECOND', 'TIME', 'TIMEVALUE', 'TODAY', 'UTCNOW', 'UTCTODAY', 'WEEKDAY', 'WEEKNUM', 'YEAR', 'YEARFRAC',
    // Time intelligence
    'CLOSINGBALANCEMONTH', 'CLOSINGBALANCEQUARTER', 'CLOSINGBALANCEYEAR', 'DATEADD', 'DATESBETWEEN', 'DATESINPERIOD', 'DATESMTD', 'DATESQTD', 'DATESYTD', 'ENDOFMONTH', 'ENDOFQUARTER', 'ENDOFYEAR', 'FIRSTDATE', 'FIRSTNONBLANK', 'LASTDATE', 'LASTNONBLANK', 'NEXTDAY', 'NEXTMONTH', 'NEXTQUARTER', 'NEXTYEAR', 'OPENINGBALANCEMONTH', 'OPENINGBALANCEQUARTER', 'OPENINGBALANCEYEAR', 'PARALLELPERIOD', 'PREVIOUSDAY', 'PREVIOUSMONTH', 'PREVIOUSQUARTER', 'PREVIOUSYEAR', 'SAMEPERIODLASTYEAR', 'STARTOFMONTH', 'STARTOFQUARTER', 'STARTOFYEAR', 'TOTALMTD', 'TOTALQTD', 'TOTALYTD',
    // Filter
    'ALL', 'ALLCROSSFILTERED', 'ALLEXCEPT', 'ALLNOBLANKROW', 'ALLSELECTED', 'CALCULATE', 'CALCULATETABLE', 'EARLIER', 'EARLIEST', 'FILTER', 'KEEPFILTERS', 'LOOKUPVALUE', 'REMOVEFILTERS', 'SELECTEDVALUE',
    // Financial
    'ACCRINT', 'ACCRINTM', 'AMORDEGRC', 'AMORLINC', 'COUPDAYBS', 'COUPDAYS', 'COUPDAYSNC', 'COUPNCD', 'COUPNUM', 'COUPPCD', 'CUMIPMT', 'CUMPRINC', 'DB', 'DDB', 'DISC', 'DOLLARDE', 'DOLLARFR', 'DURATION', 'EFFECT', 'FV', 'INTRATE', 'IPMT', 'ISPMT', 'MDURATION', 'NOMINAL', 'NPER', 'ODDFPRICE', 'ODDFYIELD', 'ODDLPRICE', 'ODDLYIELD', 'PDURATION', 'PMT', 'PPMT', 'PRICE', 'PRICEDISC', 'PRICEMAT', 'PV', 'RATE', 'RECEIVED', 'RRI', 'SLN', 'SYD', 'TBILLEQ', 'TBILLPRICE', 'TBILLYIELD', 'VDB', 'XIRR', 'XNPV', 'YIELD', 'YIELDDISC', 'YIELDMAT',
    // Information
    'COLUMNSTATISTICS', 'CONTAINS', 'CONTAINSROW', 'CONTAINSSTRING', 'CONTAINSSTRINGEXACT', 'CUSTOMDATA', 'HASONEFILTER', 'HASONEVALUE', 'ISAFTER', 'ISBLANK', 'ISCROSSFILTERED', 'ISEMPTY', 'ISERROR', 'ISEVEN', 'ISFILTERED', 'ISINSCOPE', 'ISLOGICAL', 'ISNONTEXT', 'ISNUMBER', 'ISODD', 'ISONORAFTER', 'ISSELECTEDMEASURE', 'ISSUBTOTAL', 'ISTEXT', 'SELECTEDMEASURE', 'SELECTEDMEASUREFORMATSTRING', 'SELECTEDMEASURENAME', 'USERCULTURE', 'USERNAME', 'USEROBJECTID', 'USERPRINCIPALNAME', 'INFO.VIEW.COLUMNS', 'INFO.VIEW.MEASURES', 'INFO.VIEW.RELATIONSHIPS', 'INFO.VIEW.TABLES',
    // Logical
    'AND', 'BITAND', 'BITLSHIFT', 'BITOR', 'BITRSHIFT', 'BITXOR', 'COALESCE', 'FALSE', 'IF', 'IF.EAGER', 'IFERROR', 'NOT', 'OR', 'SWITCH', 'TRUE',
    // Math & trig
    'ABS', 'ACOS', 'ACOSH', 'ACOT', 'ACOTH', 'ASIN', 'ASINH', 'ATAN', 'ATANH', 'CEILING', 'CONVERT', 'COS', 'COSH', 'COT', 'COTH', 'CURRENCY', 'DEGREES', 'DIVIDE', 'EVEN', 'EXP', 'FACT', 'FLOOR', 'GCD', 'INT', 'ISO.CEILING', 'LCM', 'LN', 'LOG', 'LOG10', 'MOD', 'MROUND', 'ODD', 'PI', 'POWER', 'QUOTIENT', 'RADIANS', 'RAND', 'RANDBETWEEN', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'SIGN', 'SIN', 'SINH', 'SQRT', 'SQRTPI', 'TAN', 'TANH', 'TRUNC',
    // Parent & child
    'PATH', 'PATHCONTAINS', 'PATHITEM', 'PATHITEMREVERSE', 'PATHLENGTH',
    // Relationship
    'CROSSFILTER', 'RELATED', 'RELATEDTABLE', 'USERELATIONSHIP',
    // Statistical
    'BETA.DIST', 'BETA.INV', 'CHISQ.DIST', 'CHISQ.DIST.RT', 'CHISQ.INV', 'CHISQ.INV.RT', 'COMBIN', 'COMBINA', 'CONFIDENCE.NORM', 'CONFIDENCE.T', 'EXPON.DIST', 'GEOMEAN', 'GEOMEANX', 'LINEST', 'LINESTX', 'MEDIAN', 'MEDIANX', 'NORM.DIST', 'NORM.INV', 'NORM.S.DIST', 'NORM.S.INV', 'PERCENTILE.EXC', 'PERCENTILE.INC', 'PERCENTILEX.EXC', 'PERCENTILEX.INC', 'POISSON.DIST', 'RANK.EQ', 'RANKX', 'SAMPLE', 'STDEV.P', 'STDEV.S', 'STDEVX.P', 'STDEVX.S', 'T.DIST', 'T.DIST.2T', 'T.DIST.RT', 'T.INV', 'T.INV.2T', 'VAR.P', 'VAR.S', 'VARX.P', 'VARX.S',
    // Table manipulation
    'ADDCOLUMNS', 'ADDMISSINGITEMS', 'CROSSJOIN', 'CURRENTGROUP', 'DATATABLE', 'DETAILROWS', 'DISTINCT', 'EXCEPT', 'FILTERS', 'GENERATE', 'GENERATEALL', 'GENERATESERIES', 'GROUPBY', 'IGNORE', 'INTERSECT', 'NATURALINNERJOIN', 'NATURALLEFTOUTERJOIN', 'ROLLUP', 'ROLLUPADDISSUBTOTAL', 'ROLLUPGROUP', 'ROLLUPISSUBTOTAL', 'ROW', 'SELECTCOLUMNS', 'SUMMARIZE', 'SUMMARIZECOLUMNS', 'TOPN', 'TREATAS', 'UNION', 'VALUES',
    // Text
    'COMBINEVALUES', 'CONCATENATE', 'CONCATENATEX', 'EXACT', 'FIND', 'FIXED', 'FORMAT', 'LEFT', 'LEN', 'LOWER', 'MID', 'REPLACE', 'REPT', 'RIGHT', 'SEARCH', 'SUBSTITUTE', 'TRIM', 'UNICHAR', 'UNICODE', 'UPPER', 'VALUE',
    // Window
    'INDEX', 'MATCHBY', 'OFFSET', 'ORDERBY', 'PARTITIONBY', 'RANK', 'ROWNUMBER', 'WINDOW',
    // Other
    'BLANK', 'ERROR', 'EVALUATEANDLOG', 'TOCSV', 'TOJSON',
];

// The DAX reference text for a tree node — what gets inserted when you drag it into an editor.
function daxRefText(n: TreeNode): string {
    const p = refParts(n.ref);
    switch (n.kind) {
        case 'measure': return `[${p.name}]`;
        case 'column':
        case 'calcColumn': return `'${p.table}'[${p.name}]`;
        case 'table':
        case 'calcgroup': return `'${p.name}'`;
        default: return n.name;
    }
}

// Drag a measure/column/table from the Model tree into ANY editor → inserts its DAX reference. We expose
// the reference as text/plain so VS Code's drop-into-editor (and our drop provider below) inserts it verbatim.
class ModelTreeDnd implements vscode.TreeDragAndDropController<TreeNode> {
    readonly dropMimeTypes: string[] = [];
    readonly dragMimeTypes = ['text/plain'];
    handleDrag(source: readonly TreeNode[], dt: vscode.DataTransfer): void {
        const text = source.map(daxRefText).filter(Boolean).join(' ');
        if (text) dt.set('text/plain', new vscode.DataTransferItem(text));
        // Also stash the TABLE(s) implied by the dragged nodes so the Studio diagram can add them on drop. A native
        // tree drag's DataTransfer is NOT readable inside the webview iframe, so the webview pulls this stash via a
        // postMessage round-trip ('requestDropTables') when a drop lands on the canvas. Consumed once (cleared on read).
        dragTablesStash = [...new Set(source.map(tableOfNode).filter((t): t is string => !!t))];
    }
    handleDrop(): void { /* the tree isn't a drop target */ }
}

// The table a tree node belongs to (for the diagram drop): a table/calc-group node IS the table; a column/measure/
// hierarchy/partition/calc-item/level node carries its owning table in the ref. Folders/roles/etc. → none.
function tableOfNode(n: TreeNode): string | undefined {
    const p = refParts(n.ref);
    return (p.kind === 'table' || p.kind === 'calcgroup') ? p.name : p.table;
}

// Explicit drop handler for DAX docs so a tree drag inserts exactly the reference text (not a file link etc.).
const daxDropProvider: vscode.DocumentDropEditProvider = {
    async provideDocumentDropEdits(_doc, _pos, dt) {
        const item = dt.get('text/plain');
        const text = item ? await item.asString() : '';
        return text ? new vscode.DocumentDropEdit(text) : undefined;
    },
};

// Signature help (calltips) for a curated set of the most-used DAX functions.
interface DaxSig { params: string[]; doc: string; }
const DAX_SIGNATURES: Record<string, DaxSig> = {
    CALCULATE: { params: ['Expression', '[Filter1]', '[Filter2]', '…'], doc: 'Evaluates an expression in a context modified by the given filters.' },
    CALCULATETABLE: { params: ['Table', '[Filter1]', '…'], doc: 'Evaluates a table expression in a modified filter context.' },
    FILTER: { params: ['Table', 'FilterExpression'], doc: 'Returns the rows of Table where FilterExpression is true.' },
    SUMX: { params: ['Table', 'Expression'], doc: 'Sums Expression evaluated for each row of Table.' },
    AVERAGEX: { params: ['Table', 'Expression'], doc: 'Averages Expression over each row of Table.' },
    MINX: { params: ['Table', 'Expression'], doc: 'Minimum of Expression over each row of Table.' },
    MAXX: { params: ['Table', 'Expression'], doc: 'Maximum of Expression over each row of Table.' },
    COUNTX: { params: ['Table', 'Expression'], doc: 'Counts non-blank Expression values over Table.' },
    PRODUCTX: { params: ['Table', 'Expression'], doc: 'Product of Expression over each row of Table.' },
    CONCATENATEX: { params: ['Table', 'Expression', '[Delimiter]', '[OrderBy]'], doc: 'Concatenates Expression over Table, joined by Delimiter.' },
    RANKX: { params: ['Table', 'Expression', '[Value]', '[Order]', '[Ties]'], doc: 'Ranks Expression across Table.' },
    TOPN: { params: ['N', 'Table', '[OrderBy]', '[Order]'], doc: 'Returns the top N rows of Table.' },
    SUM: { params: ['ColumnName'], doc: 'Adds all the numbers in a column.' },
    AVERAGE: { params: ['ColumnName'], doc: 'Averages all the numbers in a column.' },
    MIN: { params: ['ColumnNameOrScalar', '[Scalar2]'], doc: 'Smallest value in a column or between two scalars.' },
    MAX: { params: ['ColumnNameOrScalar', '[Scalar2]'], doc: 'Largest value in a column or between two scalars.' },
    COUNT: { params: ['ColumnName'], doc: 'Counts non-blank numeric/date values in a column.' },
    COUNTROWS: { params: ['[Table]'], doc: 'Counts the rows of Table (or the current context).' },
    DISTINCTCOUNT: { params: ['ColumnName'], doc: 'Counts distinct values in a column.' },
    DIVIDE: { params: ['Numerator', 'Denominator', '[AlternateResult]'], doc: 'Safe division; returns AlternateResult (default BLANK) on divide-by-zero.' },
    IF: { params: ['LogicalTest', 'ResultIfTrue', '[ResultIfFalse]'], doc: 'Returns one value if the test is true, another if false.' },
    SWITCH: { params: ['Expression', 'Value1', 'Result1', '…', '[Else]'], doc: 'Matches Expression against values, returning the matching result.' },
    COALESCE: { params: ['Expression1', 'Expression2', '…'], doc: 'Returns the first non-blank expression.' },
    IFERROR: { params: ['Value', 'ValueIfError'], doc: 'Returns ValueIfError if Value errors, else Value.' },
    RELATED: { params: ['ColumnName'], doc: 'Returns a related value from another table (many→one).' },
    RELATEDTABLE: { params: ['Table'], doc: 'Returns the related rows of Table (one→many).' },
    USERELATIONSHIP: { params: ['Column1', 'Column2'], doc: 'Activates an inactive relationship for the calculation.' },
    ALL: { params: ['[TableOrColumn]', '…'], doc: 'Removes filters from a table/columns.' },
    ALLEXCEPT: { params: ['Table', 'Column1', '…'], doc: 'Removes filters from Table except the listed columns.' },
    ALLSELECTED: { params: ['[TableOrColumn]'], doc: 'Filter context from outside the current visual selection.' },
    REMOVEFILTERS: { params: ['[TableOrColumn]', '…'], doc: 'Clears filters from the given tables/columns.' },
    KEEPFILTERS: { params: ['Expression'], doc: 'Adds filters without overriding existing ones.' },
    VALUES: { params: ['TableOrColumn'], doc: 'Distinct values of a column (incl. a blank row if any).' },
    DISTINCT: { params: ['TableOrColumn'], doc: 'Distinct values of a column (no blank row).' },
    SELECTEDVALUE: { params: ['ColumnName', '[AlternateResult]'], doc: 'The single value in scope, else AlternateResult.' },
    HASONEVALUE: { params: ['ColumnName'], doc: 'TRUE when exactly one value is in scope.' },
    LOOKUPVALUE: { params: ['ResultColumn', 'SearchColumn', 'SearchValue', '…'], doc: 'Returns the result value for matching search columns.' },
    TREATAS: { params: ['Table', 'Column1', '…'], doc: 'Applies a table expression as filters on the given columns.' },
    SUMMARIZE: { params: ['Table', 'GroupBy1', '…', '[Name]', '[Expr]'], doc: 'Groups Table by columns, optionally adding aggregates.' },
    SUMMARIZECOLUMNS: { params: ['GroupBy1', '…', '[Filter]', '[Name]', '[Expr]'], doc: 'Groups and aggregates across columns and filters.' },
    ADDCOLUMNS: { params: ['Table', 'Name1', 'Expr1', '…'], doc: 'Adds calculated columns to Table.' },
    SELECTCOLUMNS: { params: ['Table', 'Name1', 'Expr1', '…'], doc: 'Projects named expressions from Table.' },
    DATESYTD: { params: ['Dates', '[YearEndDate]'], doc: 'Year-to-date set of dates.' },
    TOTALYTD: { params: ['Expression', 'Dates', '[Filter]', '[YearEndDate]'], doc: 'Year-to-date evaluation of Expression.' },
    SAMEPERIODLASTYEAR: { params: ['Dates'], doc: 'Shifts the date column back one year.' },
    DATEADD: { params: ['Dates', 'NumberOfIntervals', 'Interval'], doc: 'Shifts dates by a number of day/month/quarter/year intervals.' },
    DATESINPERIOD: { params: ['Dates', 'StartDate', 'NumberOfIntervals', 'Interval'], doc: 'A set of dates over a rolling period.' },
    DATESBETWEEN: { params: ['Dates', 'StartDate', 'EndDate'], doc: 'Dates between two bounds.' },
    FORMAT: { params: ['Value', 'FormatString'], doc: 'Formats a value as text using a format string.' },
};

function findCallContext(doc: vscode.TextDocument, pos: vscode.Position): { fn: string; argIndex: number } | undefined {
    const text = doc.getText(new vscode.Range(new vscode.Position(0, 0), pos));
    let depth = 0, argIndex = 0, i = text.length - 1;
    for (; i >= 0; i--) {
        const ch = text[i];
        if (ch === ')') depth++;
        else if (ch === '(') { if (depth === 0) break; depth--; }
        else if (ch === ',' && depth === 0) argIndex++;
    }
    if (i < 0) return undefined;
    const m = /([A-Za-z_][A-Za-z0-9_.]*)\s*$/.exec(text.slice(0, i));
    return m ? { fn: m[1], argIndex } : undefined;
}

const daxSignatureProvider: vscode.SignatureHelpProvider = {
    provideSignatureHelp(doc, pos) {
        const ctx = findCallContext(doc, pos);
        if (!ctx) return;
        const sig = DAX_SIGNATURES[ctx.fn.toUpperCase()];
        if (!sig) return;
        const info = new vscode.SignatureInformation(`${ctx.fn}(${sig.params.join(', ')})`, sig.doc);
        info.parameters = sig.params.map((p) => new vscode.ParameterInformation(p));
        const help = new vscode.SignatureHelp();
        help.signatures = [info];
        help.activeSignature = 0;
        help.activeParameter = Math.min(ctx.argIndex, sig.params.length - 1);
        return help;
    },
};

// ---- DAX diagnostics (squiggles) -------------------------------------------------------------
// Validate DAX editors against the live model: unbalanced () [] (error) + unknown table/column/measure
// references (warning), via the pure analyzer in daxLint.ts. Conservative by design — see that file.

const daxDiagnostics = vscode.languages.createDiagnosticCollection('dax');

function isDaxDoc(doc: vscode.TextDocument): boolean { return doc.languageId === 'dax'; }

function validateDaxDoc(doc: vscode.TextDocument): void {
    if (!isDaxDoc(doc)) return;
    // A create-then-edit doc has a name header on line 1 that is NOT DAX body — analyze only the body and shift the
    // diagnostic offsets back past the header, so the header never produces a spurious "unknown column" squiggle.
    let text = doc.getText();
    let base = 0;
    const extra: vscode.Diagnostic[] = [];
    // Header docs (URI truth, cache fallback): lint only the BODY, offsetting diagnostics past the header — but when
    // line 1 no longer validates as this doc's header, surface an explicit ERROR on line 1 (Save would reject it too),
    // so a broken / deleted / wrong-kind header is SEEN, not silently linted as DAX (HIGH 4).
    if (isDaxHeaderDoc(doc.uri)) {
        const s = splitDaxHeader(text);
        const chk = checkDaxHeader(refFromUri(doc.uri), s.header);
        if (!chk.ok) {
            const end = new vscode.Position(0, Math.max(1, s.header.length));
            const dg = new vscode.Diagnostic(new vscode.Range(new vscode.Position(0, 0), end), chk.reason, vscode.DiagnosticSeverity.Error);
            dg.source = 'DAX';
            extra.push(dg);
        }
        text = s.body; base = s.headerLen;   // lint the body either way; the header is covered by the explicit diag
    }
    const raw = analyzeDax(text, daxIndex);
    const diags = raw.map((d) => {
        const dg = new vscode.Diagnostic(
            new vscode.Range(doc.positionAt(d.start + base), doc.positionAt(d.end + base)),
            d.message,
            d.severity === 'error' ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
        dg.source = 'DAX';
        return dg;
    });
    daxDiagnostics.set(doc.uri, [...extra, ...diags]);
}

function revalidateOpenDaxDocs(): void { for (const d of vscode.workspace.textDocuments) validateDaxDoc(d); }

const daxValidateTimers = new Map<string, ReturnType<typeof setTimeout>>();
function scheduleDaxValidate(doc: vscode.TextDocument): void {
    if (!isDaxDoc(doc)) return;
    const key = doc.uri.toString();
    const t = daxValidateTimers.get(key);
    if (t) clearTimeout(t);
    daxValidateTimers.set(key, setTimeout(() => { daxValidateTimers.delete(key); validateDaxDoc(doc); }, 300));
}

// ---- DAX hover + go-to-definition ------------------------------------------------------------
// Resolve the reference token under the cursor: a function (signature/doc), a [Measure] (its DAX), or a
// column 'Table'[Col] / Table[Col] (its table/kind). Definition jumps to a measure/calc-column's DAX editor.

// Match a DAX reference token: 'Table'[Col] | 'Table' | Table[Col] | [Name]  (used with getWordRangeAtPosition).
const DAX_REF_RE = /'(?:[^']|'')*'\s*\[[^\]]*\]|'(?:[^']|'')*'|[A-Za-z_]\w*\[[^\]]*\]|\[[^\]]*\]/;

interface DaxRef { kind: 'measure' | 'column' | 'table'; table?: string; name: string; }
function parseDaxRef(token: string): DaxRef | undefined {
    let m = /^'((?:[^']|'')*)'\s*\[([^\]]*)\]$/.exec(token);                         // 'Table'[Col]
    if (m) return { kind: 'column', table: m[1].replace(/''/g, "'"), name: m[2] };
    m = /^([A-Za-z_]\w*)\[([^\]]*)\]$/.exec(token);                                  // Table[Col]
    if (m) return { kind: 'column', table: m[1], name: m[2] };
    m = /^'((?:[^']|'')*)'$/.exec(token);                                            // 'Table'
    if (m) return { kind: 'table', name: m[1].replace(/''/g, "'") };
    m = /^\[([^\]]*)\]$/.exec(token);                                                // [Name]
    if (m) return { kind: 'measure', name: m[1] };
    return undefined;
}
const eqi = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

const daxHoverProvider: vscode.HoverProvider = {
    async provideHover(doc, pos) {
        const refRange = doc.getWordRangeAtPosition(pos, DAX_REF_RE);
        if (refRange) {
            const ref = parseDaxRef(doc.getText(refRange));
            if (ref?.kind === 'measure') {
                // a bare [Name] is a measure first, else a column
                const meas = daxSymbols.measures.find((x) => eqi(x.name, ref.name));
                if (meas) {
                    const md = new vscode.MarkdownString(`**${meas.name}** · measure\n`);
                    try { const dax = await conn?.sendRequest<string>('getDax', meas.ref); if (dax) md.appendCodeblock(dax.trim(), 'dax'); } catch { /* offline */ }
                    return new vscode.Hover(md, refRange);
                }
                const col = daxSymbols.columns.find((x) => eqi(x.name, ref.name));
                if (col) return new vscode.Hover(new vscode.MarkdownString(`**${col.name}** · column · \`${col.table}\``), refRange);
            } else if (ref?.kind === 'column') {
                const col = daxSymbols.columns.find((x) => eqi(x.name, ref.name) && (!ref.table || eqi(x.table, ref.table!)));
                if (col) {
                    const md = new vscode.MarkdownString(`**${col.name}** · ${col.kind === 'calcColumn' ? 'calculated column' : 'column'} · \`${col.table}\`\n`);
                    try {                       // enrich with the column's data type + display folder (isolated per-hover read)
                        const props = await conn?.sendRequest<{ name: string; value: string }[]>('getObjectProperties', col.ref);
                        const dt = props?.find((p) => p.name === 'DataType')?.value;
                        const df = props?.find((p) => p.name === 'DisplayFolder')?.value;
                        if (dt) md.appendMarkdown(`\nType: \`${dt}\``);
                        if (df) md.appendMarkdown(` · Folder: \`${df}\``);
                    } catch { /* offline */ }
                    if (col.kind === 'calcColumn') { try { const dax = await conn?.sendRequest<string>('getDax', col.ref); if (dax) md.appendCodeblock(dax.trim(), 'dax'); } catch { /* offline */ } }
                    return new vscode.Hover(md, refRange);
                }
            } else if (ref?.kind === 'table') {
                if (daxSymbols.tables.some((t) => eqi(t, ref.name))) return new vscode.Hover(new vscode.MarkdownString(`**${ref.name}** · table`), refRange);
            }
        }
        // a VAR reference → its definition; else a function name
        const wordRange = doc.getWordRangeAtPosition(pos, /[A-Za-z_][\w.]*/);
        if (wordRange) {
            const bare = doc.getText(wordRange);
            const def = varDefinition(doc.getText(), bare);
            if (def !== null) {
                const md = new vscode.MarkdownString(`**${bare}** · variable\n`);
                md.appendCodeblock(def, 'dax');
                return new vscode.Hover(md, wordRange);
            }
            const word = bare.toUpperCase();
            const sig = DAX_SIGNATURES[word];
            if (sig) {
                const md = new vscode.MarkdownString();
                md.appendCodeblock(`${word}(${sig.params.join(', ')})`, 'dax');
                md.appendMarkdown('\n' + sig.doc);
                return new vscode.Hover(md, wordRange);
            }
            if (DAX_FUNCTIONS.includes(word)) return new vscode.Hover(new vscode.MarkdownString(`\`${word}\` · DAX function`), wordRange);
        }
        return undefined;
    },
};

// Go-to-Definition / Peek (F12) inside a DAX editor. Two cases:
//  (1) an object reference [Measure] / 'Table'[Col] / Table[Col] → jump to / peek that measure's or calc-column's
//      own DAX editor. We try the cached symbol table first, then fall back to a LIVE engine lookup by exact name,
//      so it works even when the symbol cache is cold/stale (e.g. right after opening a model, before the debounced
//      rebuild lands) — which is why it could silently do nothing before.
//  (2) a bare identifier that names a VAR declared in THIS expression → jump to that VAR's declaration line.
const daxDefinitionProvider: vscode.DefinitionProvider = {
    async provideDefinition(doc, pos) {
        const refRange = doc.getWordRangeAtPosition(pos, DAX_REF_RE);
        if (refRange) {
            const ref = parseDaxRef(doc.getText(refRange));
            const loc = ref ? await resolveDaxDefinition(ref) : undefined;
            if (loc) return loc;
        }
        // A VAR reference (a bare identifier matching a `VAR <name> =` in the same document) → its declaration.
        const wordRange = doc.getWordRangeAtPosition(pos);
        if (wordRange) {
            const word = doc.getText(wordRange);
            const decl = extractDaxSymbols(doc.getText()).find((s) => s.kind === 'var' && eqi(s.name, word));
            if (decl) return new vscode.Location(doc.uri, new vscode.Range(doc.positionAt(decl.nameStart), doc.positionAt(decl.nameEnd)));
        }
        return undefined;
    },
};

// Resolve a measure / calculated-column reference to its DAX-editor location. Cache first (instant), then a live
// engine search by exact name as a fallback so the cold/stale-cache case still resolves.
async function resolveDaxDefinition(ref: DaxRef): Promise<vscode.Location | undefined> {
    const top = new vscode.Position(0, 0);
    if (ref.kind === 'measure') {
        const meas = daxSymbols.measures.find((x) => eqi(x.name, ref.name));
        if (meas) return new vscode.Location(uriForRef(meas.ref), top);
        const cc = daxSymbols.columns.find((x) => eqi(x.name, ref.name) && x.kind === 'calcColumn');
        if (cc) return new vscode.Location(uriForRef(cc.ref), top);
    } else if (ref.kind === 'column') {
        const cc = daxSymbols.columns.find((x) => x.kind === 'calcColumn' && eqi(x.name, ref.name) && (!ref.table || eqi(x.table, ref.table!)));
        if (cc) return new vscode.Location(uriForRef(cc.ref), top);
    } else {
        return undefined;   // a bare 'Table' reference has no DAX body to open
    }
    // Live fallback: the symbol cache missed → ask the engine to resolve the exact name to a DAX-bearing object.
    if (conn && ref.name) {
        try {
            const res = await conn.sendRequest<SearchResult>('searchModel', ref.name, 50);
            const hit = res.hits.find((h) => (h.kind === 'measure' || h.kind === 'calcColumn') && eqi(h.name, ref.name)
                && (ref.kind !== 'column' || !ref.table || eqi(h.table ?? '', ref.table!)));
            if (hit) return new vscode.Location(uriForRef(hit.ref), top);
        } catch { /* engine busy / closed — fall through to no definition */ }
    }
    return undefined;
}

// Outline (Ctrl+Shift+O / breadcrumbs): the VAR declarations + RETURN clause of a measure expression.
const daxDocumentSymbolProvider: vscode.DocumentSymbolProvider = {
    provideDocumentSymbols(doc) {
        return extractDaxSymbols(doc.getText()).map((s) => new vscode.DocumentSymbol(
            s.name,
            s.kind === 'var' ? 'variable' : '',
            s.kind === 'var' ? vscode.SymbolKind.Variable : vscode.SymbolKind.Key,
            new vscode.Range(doc.positionAt(s.fullStart), doc.positionAt(s.fullEnd)),
            new vscode.Range(doc.positionAt(s.nameStart), doc.positionAt(s.nameEnd))));
    },
};

// ---- DAX formatting --------------------------------------------------------------------------
// Offline (default): a local pretty-printer (daxFormat.ts) wired as Format Document (Shift+Alt+F) +
// editor.formatOnSave. Online (opt-in): the daxformatter.com service (SQLBI) — exactly what TE2/DAX Studio
// use — behind a one-time consent prompt, since it sends the expression off-machine.

function fullRange(doc: vscode.TextDocument): vscode.Range {
    return new vscode.Range(doc.positionAt(0), doc.positionAt(doc.getText().length));
}

function daxFormatOpts(options: vscode.FormattingOptions) {
    return { indent: options.insertSpaces ? ' '.repeat(options.tabSize) : '\t', maxLine: vscode.workspace.getConfiguration('semanticus').get<number>('dax.maxLineLength', 60) };
}

const daxFormatProvider: vscode.DocumentFormattingEditProvider = {
    provideDocumentFormattingEdits(doc, options) {
        // Create-then-edit doc: format only the DAX body, never the header line (formatDax would mangle it) — but
        // only when line 1 still validates as this doc's header. A broken / edited-away header is left untouched
        // (formatting a non-header line 1 would rewrite the user's mistake, hiding it); Save's reject path is the fix.
        if (isDaxHeaderDoc(doc.uri)) {
            const { header, body } = splitDaxHeader(doc.getText());
            if (checkDaxHeader(refFromUri(doc.uri), header).ok) {
                const formattedBody = formatDax(body, daxFormatOpts(options));
                if (formattedBody === body) return [];
                const bodyRange = new vscode.Range(new vscode.Position(1, 0), fullRange(doc).end);
                return [vscode.TextEdit.replace(bodyRange, formattedBody)];
            }
            return [];   // broken header: no destructive edits (the lint diagnostic + Save's reject are the fix)
        }
        const formatted = formatDax(doc.getText(), daxFormatOpts(options));
        return formatted === doc.getText() ? [] : [vscode.TextEdit.replace(fullRange(doc), formatted)];
    },
};

const daxRangeFormatProvider: vscode.DocumentRangeFormattingEditProvider = {
    provideDocumentRangeFormattingEdits(doc, range, options) {
        const text = doc.getText(range);
        const formatted = formatDax(text, daxFormatOpts(options));
        return formatted === text ? [] : [vscode.TextEdit.replace(range, formatted)];
    },
};

interface DaxFormatterError { line: number; column: number; message: string; }
interface DaxFormatterResult { formatted?: string; errors?: DaxFormatterError[]; }

// POST the expression to daxformatter.com (priming the redirect like TE2's DaxFormatter does).
async function callDaxFormatter(dax: string): Promise<DaxFormatterResult> {
    const base = 'https://www.daxformatter.com/api/daxformatter/daxtextformat';
    let url = base;
    try {
        const probe = await fetch(base, { method: 'GET', redirect: 'manual' });
        const loc = probe.headers.get('location');
        if (loc) url = loc;
    } catch { /* no redirect / offline — fall back to the base URL */ }
    const body = JSON.stringify({ Dax: dax, MaxLineLenght: 0, ListSeparator: ',', DecimalSeparator: '.', CallerApp: 'Semanticus', CallerVersion: '0.1.0' });
    const resp = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json; charset=UTF-8', 'Accept': 'application/json' }, body });
    if (!resp.ok) throw new Error(`daxformatter.com returned HTTP ${resp.status}`);
    const text = (await resp.text()).trim();
    let formatted: string | undefined; let errors: DaxFormatterError[] | undefined;
    if (text.startsWith('{')) { const o = JSON.parse(text) as DaxFormatterResult; formatted = o.formatted; errors = o.errors; }
    else if (text.startsWith('"')) formatted = JSON.parse(text) as string;
    return { formatted: formatted?.replace(/\r\n/g, '\n').trim(), errors };
}

async function formatDaxOnlineCmd(context: vscode.ExtensionContext) {
    const ed = vscode.window.activeTextEditor;
    if (!ed || ed.document.languageId !== 'dax') { vscode.window.showWarningMessage('Open a DAX expression to format.'); return; }
    if (!context.globalState.get<boolean>('semanticus.daxOnlineConsent')) {
        const pick = await vscode.window.showWarningMessage(
            'Format with DAX Formatter sends this expression to the online service daxformatter.com (SQLBI). The expression leaves your machine. Continue?',
            { modal: true }, 'Use once', 'Always use');
        if (!pick) return;
        if (pick === 'Always use') await context.globalState.update('semanticus.daxOnlineConsent', true);
    }
    const text = ed.document.getText();
    try {
        const r = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Formatting DAX via daxformatter.com…' },
            () => callDaxFormatter(text));
        if (r.errors && r.errors.length) { vscode.window.showErrorMessage('DAX Formatter: ' + r.errors.map((e) => `(${e.line},${e.column}) ${e.message}`).join('; ')); return; }
        if (!r.formatted) { vscode.window.showErrorMessage('DAX Formatter returned no result.'); return; }
        await ed.edit((b) => b.replace(fullRange(ed.document), r.formatted!));
    } catch (e: any) { vscode.window.showErrorMessage('DAX Formatter failed: ' + (e?.message ?? e)); }
}

// ---- commands --------------------------------------------------------------------------------

// Set the status bar from a SessionInfo. ONE model identity: name + measure count + a live indicator when a
// query engine is attached (a unified open of Power BI Desktop / XMLA, or an attach to a running instance).
function setStatusFromInfo(info?: SessionInfo): void {
    // Gate the "Save to Live Model" toolbar button on a live-bound session (opened via Power BI Desktop / XMLA).
    void vscode.commands.executeCommand('setContext', 'semanticus.liveBound', !!info?.liveBound);
    // Health chip hygiene: opening a DIFFERENT model broadcasts no didChange, so a stale "health moved" chip
    // from the previous session would linger over the new model — hide it whenever the session identity changes.
    if (info?.sessionId !== healthSessionId) { healthSessionId = info?.sessionId; healthStatus?.hide(); }
    // Opening a DIFFERENT model invalidates the create-then-edit header registry (its refs are for the old model) —
    // wipe it on the same session-identity change the health chip keys off (HIGH: a reloaded window must not carry
    // stale header registrations onto a new model).
    clearDaxHeaderDocsOnSwap(info?.sessionId);
    // The model key stamped onto new header uris: the source path (stable across a window reload) when the model is
    // saved, else the ephemeral session id. Used ONLY to reject a header doc minted for a DIFFERENT model at Save.
    daxHeaderModelKey = modelKeyOf(info);
    renderSyncChip(info);
    if (!info?.sessionId) { status.text = '$(database) Semanticus: no model. Run "Open Model…"'; return; }
    const live = info.liveConnected ? ' · $(broadcast) live' : '';
    // Show which account the live model is signed in as, so identity is visible even with the drawer closed. Only when
    // a live connection is in play and the account is known (azcli/local report no named account — honestly omitted).
    const acct = (info.liveConnected || info.liveBound) && info.currentAccount ? ` · $(account) ${info.currentAccount}` : '';
    status.text = `$(database) Semanticus: ${info.modelName} (${info.measures} measures)${live}${acct}`;
    status.tooltip = info.currentAccount
        ? `Signed in as ${info.currentAccount}${info.currentTenant ? ` · ${info.currentTenant}` : ''}`
        : undefined;
}

// The native sync chip: EDITING → QUERYING, tinted only when we can PROVE a divergence (a live query connection
// with unsaved edits — the exact case where query results omit your staged work). This mirrors the Studio footer's
// syncVerdict, reading the SAME sessionInfo the footer uses (liveBound/liveEndpoint/liveDatabase = editing;
// liveConnected/liveDataSource = querying; hasUnsavedChanges = why they can silently disagree) — no second source
// of truth. It never blocks; it discloses. Hidden until a model is open.
function renderSyncChip(info?: SessionInfo): void {
    if (!syncStatus) return;
    if (!info?.sessionId) { syncStatus.hide(); return; }
    const norm = (x?: string) => (x || '').trim().toLowerCase().replace(/[\\/]+$/, '');
    const short = (p?: string) => { const t = (p || '').replace(/[\\/]+$/, ''); const i = Math.max(t.lastIndexOf('/'), t.lastIndexOf('\\')); return i >= 0 ? t.slice(i + 1) : t; };
    const editing = info.liveBound && info.liveDatabase ? info.liveDatabase : (info.modelName || 'model');
    const connected = !!info.liveConnected;
    // Same live instance? Only claim it when the attached query source provably matches the edited endpoint/source.
    const q = norm(info.liveDataSource);
    const same = connected && !!q && (q === norm(info.liveEndpoint) || q === norm(info.source));
    const querying = !connected ? 'offline'
        : (info.liveBound && info.liveDatabase ? info.liveDatabase : (short(info.liveDataSource) || 'live model'));

    let warn = false, detail: string;
    if (!connected) detail = 'No live connection, so queries are unavailable.';
    else if (info.hasUnsavedChanges) { warn = true; detail = "Unsaved edits are not in the model you're querying."; }
    else if (same) detail = 'Editing and querying the same instance.';
    // A local attach (Power BI Desktop, liveKind 'local') is NOT a published model — don't tell the user their
    // queries "reflect the last deploy" when they're hitting a live local engine. The published-model disclosure
    // is load-bearing but belongs only to the XMLA case.
    else if (info.liveKind === 'local') detail = 'Querying an attached local engine (Power BI Desktop).';
    else detail = 'Querying the published model. Reflects the last deploy.';

    syncStatus.text = `${warn ? '$(warning)' : '$(git-compare)'} ${editing} $(arrow-right) ${querying}`;
    syncStatus.tooltip = `Editing: ${editing}\nQuerying: ${connected ? querying : 'not connected'}\n${detail}\nClick to compare what you're editing against what you're querying.`;
    syncStatus.backgroundColor = warn ? new vscode.ThemeColor('statusBarItem.warningBackground') : undefined;
    syncStatus.show();
}
// Re-read sessionInfo and repaint the sync chip. Used where we don't already hold a fresh SessionInfo — a
// model/didChange (flips hasUnsavedChanges) or a webview-initiated attach/disconnect relayed through the host.
async function refreshSyncChip(): Promise<void> {
    if (!conn) { syncStatus?.hide(); return; }
    try { renderSyncChip(await conn.sendRequest<SessionInfo>('sessionInfo')); } catch { /* leave the chip as-is */ }
}
async function refreshStatus(): Promise<SessionInfo | undefined> {
    if (!conn) return undefined;
    try { const info = await conn.sendRequest<SessionInfo>('sessionInfo'); setStatusFromInfo(info); return info; }
    catch { return undefined; }
}

// Native counterpart of the Connections drawer: a local project, a remembered local runtime, or a remembered XMLA
// model can be the source. The drawer separately controls tests/queries and the final publish destination.
// The model tree keeps its fast native picker (keyboard-first, no webview spin-up), but every remembered row now shows
// the identity a connect will use, and carries inline account actions. It launches; it never manages — management
// lives in the Connections drawer. Built on createQuickPick so each row can carry per-row buttons.
async function openModelCommand(tree: ModelTreeProvider) {
    if (!conn) { vscode.window.showWarningMessage('Semanticus engine not connected.'); return; }
    const connection = conn;
    type OpenPick = vscode.QuickPickItem & { id?: 'newModel' | 'file' | 'discoverLocal' | 'newXmla' | 'manage'; record?: ModelConnectionRecord };
    const known = (await connection.sendRequest<ModelConnectionRecord[]>('listConnections').catch(() => [])) ?? [];
    // One cheap silent probe (a local MSAL-record read on the engine) fills in the account for rows connected before we
    // began capturing it — so "as <account>" is honest even for older targets, and absent means "account unknown".
    const probes = (await connection.sendRequest<ConnectionAccountProbe[]>('probeConnectionAccounts').catch(() => [])) ?? [];
    // The probe is the truth of who the NEXT open signs in as (the tenant-wide record); the per-target lastAccount is a
    // fallback only when nothing was probed. Preferring the probe is what keeps a tenant-mate from showing a stale
    // account after a sibling model switched identities.
    const probeOf = (r: ModelConnectionRecord) => probes.find((p) => p.id === r.id);
    // The PREDICTED next-open account is ONLY a live sign-in record (probe.account); it is null when unknown. We never
    // fall back to the per-target lastAccount as the prediction — that appears only as "last opened as" provenance (HIGH 3).
    const accountOf = (r: ModelConnectionRecord) => probeOf(r)?.account;
    const local = known.filter((r) => r.kind === 'localDesktop');
    const xmla = known.filter((r) => r.kind === 'xmla');

    // Per-row buttons (icon + tooltip — the native inline-action mechanism). The tooltip carries the exact wording.
    const openAsBtn: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('account'), tooltip: 'Open as…' };
    const signInBtn: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('sign-in'), tooltip: 'Sign in and open' };
    const manageBtn: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('gear'), tooltip: 'Manage connections' };
    const xmlaRow = (r: ModelConnectionRecord): OpenPick => {
        const account = accountOf(r);
        const was = probeOf(r)?.previousAccount;
        // An unlabelled cloud target reads "Production safeguards", not "prod": fail-closed governance is a different
        // fact from a declared production label (the label is null; the safeguards still apply).
        const description = r.label || 'Production safeguards';
        // Who the next open will actually sign in as. When that's unknown (no live sign-in record) we say so — and only
        // name the last-used account as provenance ("last opened as <x>"), never as the prediction (HIGH 3).
        const identity = account
            ? (was && was !== account ? `as ${account} (was ${was})` : `as ${account}`)
            : (was ? `last opened as ${was}` : 'account unknown');
        // Account actions only make sense for a switchable sign-in family (interactive / device-code). For azcli /
        // service principal there is no account picker, so offer NO account button — the row just opens on Enter,
        // rather than promising an "Open as…" that would silently reuse the non-interactive identity.
        const switchable = credentialFamily(r.authMode) != null;
        const buttons = !switchable ? [manageBtn] : account ? [openAsBtn, manageBtn] : [signInBtn, manageBtn];
        return {
            record: r, label: `$(cloud) ${r.modelName || r.database || r.endpoint}`, description,
            detail: `${r.endpoint}${r.database ? ` · ${r.database}` : ''} · ${identity}`,
            buttons,
        };
    };
    const items: OpenPick[] = [
        { id: 'newModel', label: '$(new-file) Create a new model…', detail: 'Start with a guided draft, review it with AI Assistant, then build' },
        { id: 'file', label: '$(file-directory) Open local file or project…', detail: 'Edit user-owned .bim or TMDL files, including projects already in source control' },
        { id: 'discoverLocal', label: '$(vm) Find a running local model…', detail: 'Use one running model for editing and queries' },
    ];
    if (local.length) {
        items.push({ label: 'Running local models', kind: vscode.QuickPickItemKind.Separator });
        items.push(...local.map((r): OpenPick => ({ record: r, label: `$(vm) ${r.modelName || r.database || r.endpoint}`, description: r.label || 'local', detail: `${r.endpoint}${r.database ? ` · ${r.database}` : ''}` })));
    }
    if (xmla.length) {
        items.push({ label: 'Published models', kind: vscode.QuickPickItemKind.Separator });
        items.push(...xmla.map(xmlaRow));
    }
    items.push({ label: '', kind: vscode.QuickPickItemKind.Separator },
        { id: 'newXmla', label: '$(add) Add a published model…', detail: 'Enter the endpoint once, then pick the account; it is remembered across the product' },
        { id: 'manage', label: '$(gear) Manage connections', detail: 'Accounts, environments, history' });

    const qp = vscode.window.createQuickPick<OpenPick>();
    qp.items = items;
    qp.placeholder = 'Search models, or paste an XMLA endpoint or file path';
    qp.matchOnDetail = true; qp.matchOnDescription = true;
    const pickOne = () => new Promise<{ item?: OpenPick; button?: vscode.QuickInputButton; record?: ModelConnectionRecord; value?: string }>((resolve) => {
        let resolved = false;
        const done = (v: { item?: OpenPick; button?: vscode.QuickInputButton; record?: ModelConnectionRecord; value?: string }) => { if (!resolved) { resolved = true; resolve(v); } };
        qp.onDidTriggerItemButton((e) => { qp.hide(); done({ button: e.button, record: (e.item as OpenPick).record }); });
        // Enter accepts the highlighted row — OR, when the typed text matched nothing, the raw value the user pasted
        // (the placeholder promises "paste an XMLA endpoint or file path"), so a paste is opened, not silently dropped.
        qp.onDidAccept(() => { const item = qp.selectedItems[0]; const value = qp.value; qp.hide(); done({ item, value }); });
        qp.onDidHide(() => done({}));
        qp.show();
    });
    const choice = await pickOne();
    qp.dispose();

    try {
        // An inline row button was pressed: switch account, sign in and open, or jump to the manager.
        if (choice.button) {
            if (choice.button === manageBtn) { await openConnectionsManager(); return; }
            if (!choice.record) return;
            const r = choice.button === signInBtn
                ? await signInAndOpen(choice.record, known)
                : await switchAccountAndOpen(choice.record, known);
            await finishOpen(r, tree);
            return;
        }
        const pick = choice.item;
        if (!pick) {
            // No row matched, but the user typed/pasted something and pressed Enter — route it to the same open flow
            // the placeholder promises, instead of cancelling. An XMLA endpoint opens live; anything else is a path.
            const pasted = (choice.value || '').trim();
            if (pasted) { await finishOpen(await openFromPasted(pasted), tree); }
            return;
        }
        if (pick.id === 'manage') { await openConnectionsManager(); return; }
        if (pick.id === 'newModel') {
            const name = await vscode.window.showInputBox({
                title: 'Create a new model', prompt: 'Model name', placeHolder: 'Sales analytics', ignoreFocusOut: true,
                validateInput: (v) => v.trim() ? undefined : 'Enter a model name.',
            });
            if (!name) return;
            const current = await connection.sendRequest<SessionInfo>('sessionInfo').catch(() => undefined);
            if (current?.sessionId && current.hasUnsavedChanges) {
                const confirm = await vscode.window.showWarningMessage(
                    `Creating ${name.trim()} replaces the open model, which has unsaved changes.`,
                    { modal: true }, 'Create new model');
                if (confirm !== 'Create new model') return;
            }
            const created = await connection.sendRequest<OpenResult>('createModel', name.trim(), 1604);
            await propGrid.showModel(created.modelName);
            tree.refresh(); await refreshStatus(); void rebuildDaxSymbols();
            try { studioPanel?.webview.postMessage({ type: 'reconnected' }); } catch { }
            navigateStudio(extCtx, 'spec');
            return;
        }
        const r = pick.record ? await openFromRemembered(pick.record)
            : pick.id === 'file' ? await openFromFile()
            : pick.id === 'discoverLocal' ? await openFromLocal()
            : await openFromXmla();
        await finishOpen(r, tree);
    } catch (e: any) {
        vscode.window.showErrorMessage('Open failed: ' + (e?.message ?? e));
    }
}

// Shared post-open wiring: announce, retarget the property grid, refresh the tree/status/IntelliSense, wake Studio.
async function finishOpen(r: OpenResult | undefined, tree: ModelTreeProvider): Promise<void> {
    if (!r) return;
    const account = r.account ? ` · as ${r.account}` : '';
    vscode.window.showInformationMessage(`Opened ${r.modelName}: ${r.tables} tables, ${r.measures} measures${r.liveConnected ? ' · live' : ''}${account}.`);
    await propGrid.showModel(r.modelName);   // a model swap never inherits an old object's property target
    tree.refresh();
    await refreshStatus();
    void rebuildDaxSymbols();   // re-seed DAX IntelliSense (completion/hover/go-to-definition) for the just-opened model
    try { studioPanel?.webview.postMessage({ type: 'reconnected' }); } catch { /* no panel */ }
}

// Open the shared Connections manager. Studio hosts it as the Connections drawer; opening Studio and posting the
// message opens the same component from the native door. Queued (like navigation) so it survives a cold panel mount.
async function openConnectionsManager(): Promise<void> {
    openStudio(extCtx);
    pendingOpenConnections = true;
    flushOpenConnections();
}
function flushOpenConnections() {
    if (studioPanel && studioReady && pendingOpenConnections) {
        try { studioPanel.webview.postMessage({ type: 'openConnections' }); } catch { /* disposing */ }
        pendingOpenConnections = false;
    }
}

async function openFromRemembered(record: ModelConnectionRecord): Promise<OpenResult> {
    return record.kind === 'localDesktop'
        ? conn!.sendRequest<OpenResult>('openLocal', record.endpoint, record.database || null)
        : vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Semanticus: opening ${record.modelName || record.database || 'published model'}…` },
            () => conn!.sendRequest<OpenResult>('openLive', record.endpoint, record.database || null, record.authMode || 'interactive', null, record.tenantId || null, false),
        );
}

// The ONE shared account dialog, reached from the picker's "Open as…", the drawer's Switch account, and
// "Sign in and open". Phase 1 is honest: the sign-in cache holds one slot per tenant, so choosing a different
// account is a SWITCH with tenant-wide consequences, never a fake per-open override. We quantify the blast radius —
// the remembered models on the same tenant this switch will also re-point — BEFORE anything happens.
function switchBlastRadius(record: ModelConnectionRecord, known: ModelConnectionRecord[]): string {
    const tenant = (record.tenantId || '').toLowerCase();
    const family = credentialFamily(record.authMode);
    // A sign-in is remembered per (tenant, credential family), so a switch only re-points other records that share BOTH.
    // Count from the SAME slot the switch actually acts on: same tenant string (including the empty one) and same family.
    const affected = known.filter((r) => r.kind === 'xmla' && r.id !== record.id
        && (r.tenantId || '').toLowerCase() === tenant && credentialFamily(r.authMode) === family);
    // A record with NO tenant recorded shares the empty-tenant slot with EVERY other tenantless record of the same
    // family — whatever tenant each actually belongs to — so the honest scope is "models with no tenant recorded",
    // never "on the same tenant" (which would falsely imply they are all the same tenant) (HIGH 3).
    if (!tenant) {
        return affected.length
            ? `${affected.length} other remembered model${affected.length === 1 ? '' : 's'} with no tenant recorded also share this sign-in and will open with the new account, whatever tenant they belong to.`
            : 'No other remembered models without a tenant recorded are affected.';
    }
    return affected.length
        ? `${affected.length} other remembered model${affected.length === 1 ? '' : 's'} signed in the same way on this tenant will also open with the new account from now on.`
        : 'No other remembered models on this tenant are affected.';
}

async function switchAccountAndOpen(record: ModelConnectionRecord, known: ModelConnectionRecord[]): Promise<OpenResult | undefined> {
    if (!conn) return undefined;
    const name = record.modelName || record.database || 'this model';
    const choice = await vscode.window.showWarningMessage(
        `Open ${name} as a different account?`,
        { modal: true, detail: `The saved endpoint does not change, whichever account you pick. ${switchBlastRadius(record, known)} Every switch is recorded in the connection history.` },
        'Choose account and open');
    if (choice !== 'Choose account and open') return undefined;
    return vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Semanticus: switching account for ${name}…` },
        () => conn!.sendRequest<OpenResult>('openLive', record.endpoint, record.database || null, record.authMode || 'interactive', null, record.tenantId || null, true),
    );
}

// A stale (signed-out / account-unknown) row: sign in and open in one step. Forces the account picker (forceReauth),
// so a failed authorization returns to the account choice, never to an endpoint form.
async function signInAndOpen(record: ModelConnectionRecord, known: ModelConnectionRecord[]): Promise<OpenResult | undefined> {
    if (record.kind === 'localDesktop') return openFromRemembered(record);
    return switchAccountAndOpen(record, known);
}

// A value pasted into the Open picker: an XMLA endpoint (a URI scheme like powerbi:// or asazure://, or a
// "Data Source=" connection form) opens live with a browser sign-in (the safe default; the account can be switched
// afterwards). Anything else is treated as a .bim/.tmdl file or a PBIP/TMDL folder path. Mirrors the two open flows
// the picker's rows already offer, so a paste is never dropped.
async function openFromPasted(value: string): Promise<OpenResult | undefined> {
    const v = value.trim();
    if (!v || !conn) return undefined;
    const looksXmla = /^[a-z][a-z0-9+.-]*:\/\//i.test(v) || /^data source=/i.test(v);
    if (looksXmla) {
        return vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Semanticus: connecting to XMLA…' },
            () => conn!.sendRequest<OpenResult>('openLive', v, null, 'interactive', null, null, false));
    }
    return conn.sendRequest<OpenResult>('open', v);
}

async function openFromFile(): Promise<OpenResult | undefined> {
    const picked = await vscode.window.showOpenDialog({
        canSelectFiles: true, canSelectFolders: true, canSelectMany: false,
        openLabel: 'Open semantic model', filters: { 'Semantic models': ['bim', 'tmdl'] },
    });
    if (!picked || picked.length === 0) return undefined;
    return conn!.sendRequest<OpenResult>('open', picked[0].fsPath);
}

async function openFromLocal(): Promise<OpenResult | undefined> {
    const instances = (await conn!.sendRequest<LocalInstance[]>('listLocalInstances').catch(() => [] as LocalInstance[])) ?? [];
    if (instances.length === 0) {
        vscode.window.showWarningMessage('No running local model was found. Open one in the desktop authoring app, then try again.');
        return undefined;
    }
    let ds: string | null;
    if (instances.length === 1) {
        ds = instances[0].dataSource;
    } else {
        const p = await vscode.window.showQuickPick(
            instances.map((i) => ({ label: i.title || i.dataSource, detail: i.dataSource, ds: i.dataSource })),
            { placeHolder: 'Pick a running local model' },
        );
        if (!p) return undefined;
        ds = p.ds;
    }
    return vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Semanticus: opening local model…' },
        () => conn!.sendRequest<OpenResult>('openLocal', ds, null),
    );
}

// T13 migration: the engine registry is now the only connection history. Import the extension's former globalState
// entries once, then remove that key. Retaining the key on any failure makes the migration retry safely; Remember is
// idempotent, and neither path stores tokens or secrets.
const LEGACY_RECENT_XMLA_KEY = 'semanticus.recentXmla';
async function migrateLegacyXmlaHistory(): Promise<void> {
    const legacy = extCtx?.globalState.get<LegacyRecentXmla[]>(LEGACY_RECENT_XMLA_KEY) ?? [];
    if (!legacy.length || !conn || !extCtx) return;
    const connection = conn;
    const context = extCtx;
    try {
        const migrated = await migrateLegacyXmlaEntries(legacy,
            (r, mode) => connection.sendRequest('rememberXmlaConnection', r.endpoint, r.database || null, r.modelName || null, mode, 'human'),
            () => context.globalState.update(LEGACY_RECENT_XMLA_KEY, undefined));
        out.appendLine(`Migrated ${migrated} legacy XMLA connection${migrated === 1 ? '' : 's'} into the engine registry.`);
    } catch (e: any) {
        out.appendLine('Legacy XMLA connection migration will retry: ' + (e?.message ?? e));
    }
}
// forceReauth: the interactive families cache ONE saved sign-in per (client, tenant). "Use a different account…"
// ignores that saved sign-in and shows the Entra account picker, then persists the newly chosen identity — the
// recovery path when the cache is stuck on the wrong account (e.g. a different tenant's workspace shows "not found").
interface AuthMode { mode: string; label: string; detail: string; forceReauth?: boolean; }
const AUTH_MODES: AuthMode[] = [
    { mode: 'interactive', label: 'Microsoft Entra (interactive)', detail: 'Browser sign-in / MFA; reuses your saved sign-in' },
    { mode: 'interactive', label: 'Microsoft Entra: use a different account…', detail: 'Forces the account picker; ignores the saved sign-in (switch tenant/identity)', forceReauth: true },
    { mode: 'serviceprincipal', label: 'Service principal', detail: 'Reads AZURE_CLIENT_ID/SECRET/TENANT (or FABRIC_*) from the engine env; reliable for Fabric' },
    { mode: 'azcli', label: 'Azure CLI', detail: 'Uses your current `az login` session' },
];
async function openFromXmla(): Promise<OpenResult | undefined> {
    const endpoint = await vscode.window.showInputBox({
        prompt: 'XMLA endpoint', ignoreFocusOut: true,
        placeHolder: 'powerbi://api.powerbi.com/v1.0/myorg/<Workspace>',
    });
    if (!endpoint) return undefined;
    const database = await vscode.window.showInputBox({
        prompt: 'Model / database (optional, leave empty for the endpoint\'s only/first model)', ignoreFocusOut: true,
    });
    if (database === undefined) return undefined;
    const authPick = await vscode.window.showQuickPick(AUTH_MODES, { placeHolder: 'Authentication' });
    if (!authPick) return undefined;

    const ep = endpoint, db = database || undefined, am = authPick.mode;
    const r = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Semanticus: connecting to XMLA…' },
        () => conn!.sendRequest<OpenResult>('openLive', ep, db || null, am, null, null, authPick.forceReauth ?? false),
    );
    return r;
}

async function saveCommand() {
    if (!conn) { vscode.window.showWarningMessage('Semanticus engine not connected.'); return; }
    try {
        const r = await conn.sendRequest<SaveResult>('save', null, 'TMDL');
        vscode.window.showInformationMessage(`Saved ${r.format} → ${r.path} (${r.fileCount} files).`);
    } catch (e: any) {
        vscode.window.showErrorMessage('Save failed: ' + (e?.message ?? e));
    }
}

// Save to the LIVE model the session was opened from (open_local / open_live). A live WRITE — so it always runs a
// DRY RUN first, shows the exact change set, and only writes after an explicit modal confirm. Local Power BI Desktop
// writes back with integrated auth (no prompt); a cloud XMLA write asks for the write principal (service principal
// is the reliable one for Fabric). Metadata only — no data refresh, no object add/delete (engine-enforced).
async function saveToLiveCommand() {
    if (!conn) { warnNoEngine(); return; }
    const info = await conn.sendRequest<SessionInfo>('sessionInfo');
    if (!info?.liveBound || !info.liveEndpoint) {
        vscode.window.showWarningMessage('This model wasn’t opened from a live source. Use Open Model… → Power BI Desktop or XMLA, then deploy changes back.');
        return;
    }
    // Auth is REUSED from how the model was opened — no re-prompt, like Tabular Editor. Passing authMode=null makes
    // the engine reuse the live binding's mode (local Power BI Desktop = integrated/no token; cloud = the cached
    // open token via MSAL). 1) DRY RUN — compute the change set, write nothing.
    let dry: DeployReport;
    try {
        dry = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Semanticus: computing changes (dry run)…' },
            () => conn!.sendRequest<DeployReport>('deployLive', null, null, null, null, null, false),
        );
    } catch (e: any) { vscode.window.showErrorMessage('Save to live (dry run) failed: ' + (e?.message ?? e)); return; }
    if (dry.error) { vscode.window.showErrorMessage('Save to live (dry run): ' + dry.error); return; }
    if (!dry.totalChanges) { vscode.window.showInformationMessage('No metadata changes to deploy. The live model already matches the session.'); return; }
    // 2) CONFIRM — modal, with the exact change list.
    const list = (dry.changes ?? []).slice(0, 12).join('\n');
    const more = (dry.totalChanges ?? 0) > 12 ? `\n…and ${(dry.totalChanges ?? 0) - 12} more` : '';
    const extra = [
        dry.added ? `\n+ ${dry.added} new object(s) (measures / calculated columns) will be created.` : '',
        dry.conflicts?.length ? `\nWarning: ${dry.conflicts.length} rename conflict(s) will be skipped.` : '',
        dry.unmatched?.length ? `\nNote: ${dry.unmatched.length} object(s) can’t be deployed here (new tables / data columns, left out).` : '',
    ].join('');
    const go = await vscode.window.showWarningMessage(
        `Deploy ${dry.totalChanges} metadata change(s) to the live model “${dry.database}”?`,
        { modal: true, detail: `Endpoint: ${dry.endpoint}\n\n${list}${more}${extra}\n\nMetadata only (incl. new measures / calc columns). No data refresh, no deletes. This writes to the LIVE model.` },
        'Deploy',
    );
    if (go !== 'Deploy') return;
    // 3) COMMIT.
    try {
        const res = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Semanticus: deploying to the live model…' },
            () => conn!.sendRequest<DeployReport>('deployLive', null, null, null, null, null, true),
        );
        if (res.error) vscode.window.showErrorMessage('Deploy failed; nothing was committed: ' + res.error);
        else if (res.committed) vscode.window.showInformationMessage(`Deployed ${res.totalChanges} change(s) to “${res.database}”.`);
        else vscode.window.showWarningMessage(`Deploy made no changes (${res.totalChanges ?? 0} computed).`);
    } catch (e: any) {
        // The Verified-Edits deploy gate is an ACCOUNTABLE PAUSE, never a hard wall (golden rule #2 — the agent door
        // can override, so the human door must too). A RED gate throws "…blocked by the deploy gate — <blockers>…";
        // catch just that, show the blockers, and offer an "Override & Deploy…" that captures a required reason and
        // retries with it (mirroring the engine, which rejects an empty/whitespace override reason).
        const emsg = String(e?.message ?? e);
        if (emsg.includes('blocked by the deploy gate')) { await overrideDeployToLive(emsg); return; }
        vscode.window.showErrorMessage('Deploy failed: ' + emsg);
    }
}

// The accountable-override path for Save to Live when the deploy gate blocks. Surfaces the blockers, requires a typed
// reason (engine-enforced too), then retries deployLive with origin='human' + the reason as the 8th/9th params.
async function overrideDeployToLive(gateMsg: string) {
    if (!conn) { warnNoEngine(); return; }
    const pick = await vscode.window.showWarningMessage(gateMsg, { modal: true, detail: 'The readiness gate blocked this deploy. You can override with a written reason; it is recorded, permanently, in the Verified Edits trail.' }, 'Override & Deploy…');
    if (pick !== 'Override & Deploy…') return;
    const reason = await vscode.window.showInputBox({
        prompt: 'Reason for overriding the deploy gate (recorded in the Verified Edits trail)',
        placeHolder: 'Why is it acceptable to deploy despite the blockers?',
        ignoreFocusOut: true,
        validateInput: (v) => (v.trim() ? null : 'A reason is required to override the gate.'),
    });
    if (!reason || !reason.trim()) return;
    try {
        const res = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Semanticus: deploying to the live model (override)…' },
            () => conn!.sendRequest<DeployReport>('deployLive', null, null, null, null, null, true, 'human', reason.trim()),
        );
        if (res.error) vscode.window.showErrorMessage('Deploy failed; nothing was committed: ' + res.error);
        else if (res.committed) vscode.window.showWarningMessage(`Deployed ${res.totalChanges} change(s) to “${res.database}” (gate overridden).`);
        else vscode.window.showWarningMessage(`Deploy made no changes (${res.totalChanges ?? 0} computed).`);
    } catch (e: any) { vscode.window.showErrorMessage('Deploy failed: ' + (e?.message ?? e)); }
}

// ---- Connect Claude Code (ship-gater: MCP install logistics) ---------------------------------
// Claude Code is a SEPARATE product that discovers MCP servers ONLY from its own workspace .mcp.json — the
// VS Code MCP API wires servers to Copilot, not Claude Code, so it can't help here. Hand-authoring the entry
// (absolute DLL path, a matching --workspace, no env block) is exactly what users get wrong, so this writes it.

// The MCP server entry Claude Code launches. Full absolute paths because Claude Code starts with a MINIMAL
// environment (no inherited cwd/PATH). Two shapes, matching resolveEngine():
//   • bundled 'exe' → command = the self-contained engine exe, args = ["mcp","--workspace",ws] (no dotnet needed);
//   • dev 'dll'     → command = the resolved dotnet, args = [dll,"mcp","--workspace",ws].
// The engine is attach-or-own (ONE server model): this `mcp` process ATTACHES to a running owner engine (the
// one VS Code Studio spawned) when present, else owns the model itself. Entitlement follows the OWNER engine,
// so no env block here (env blocks in .mcp.json are historically unreliable). A Pro licence, when the user
// has one, is delivered via the RELIABLE --license flag (appended only when semanticus.licenseToken is set).
function semanticusMcpEntry(engine: ResolvedEngine, ws: string, licenseToken: string): McpServerEntry {
    const command = engine.kind === 'exe' ? engine.path : resolveDotnet();
    const args = engine.kind === 'exe' ? ['mcp', '--workspace', ws] : [engine.path, 'mcp', '--workspace', ws];
    if (licenseToken) args.push('--license', licenseToken);
    return { command, args };
}

// Deep-preserve an existing .mcp.json and set ONLY mcpServers.semanticus. Returns the merged object plus whether
// a PRE-EXISTING semanticus entry differs from ours — so the caller can confirm before clobbering someone's edit.
function mergeMcpConfig(existing: unknown, entry: McpServerEntry): { merged: Record<string, any>; conflict: boolean } {
    const isObj = (v: unknown): v is Record<string, any> => !!v && typeof v === 'object' && !Array.isArray(v);
    const merged: Record<string, any> = isObj(existing) ? { ...existing } : {};
    const servers: Record<string, any> = isObj(merged.mcpServers) ? { ...merged.mcpServers } : {};
    const prior = servers.semanticus;
    const conflict = prior !== undefined && JSON.stringify(prior) !== JSON.stringify(entry);
    servers.semanticus = entry;
    merged.mcpServers = servers;
    return { merged, conflict };
}

/// A Marketplace update installs the bundled apphost under a versioned extension directory. Repair only a prior
/// Semanticus-generated bundled entry for this same workspace; custom and development entries still flow through
/// the explicit Connect AI Assistant confirmation path.
async function autoHealSemanticusMcpEntry(context: vscode.ExtensionContext): Promise<void> {
    if (context.extensionMode === vscode.ExtensionMode.Development) return;
    const ws = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!ws) return;
    const target = path.join(ws, '.mcp.json');
    if (!fs.existsSync(target)) return;

    let engine: ResolvedEngine;
    try { engine = resolveEngine(); }
    catch (e: any) { out.appendLine(`AI Assistant connection auto-heal skipped: ${e?.message ?? e}`); return; }
    if (engine.kind !== 'exe') return;

    let parsed: unknown;
    try { parsed = JSON.parse(fs.readFileSync(target, 'utf8')); }
    catch { out.appendLine(`AI Assistant connection auto-heal skipped: ${target} is not valid JSON.`); return; }
    const prior = parsed && typeof parsed === 'object' && !Array.isArray(parsed)
        ? (parsed as any).mcpServers?.semanticus
        : undefined;
    const licenseToken = await getLicenseToken(context, { reconcile: false });
    const entry = semanticusMcpEntry(engine, path.resolve(ws), licenseToken);
    if (!shouldAutoHealMcpEntry(prior, entry, process.platform)) return;

    const merged = mergeMcpConfig(parsed, entry).merged;
    try { fs.writeFileSync(target, JSON.stringify(merged, null, 2) + '\n', 'utf8'); }
    catch (e: any) { out.appendLine(`AI Assistant connection auto-heal failed: ${e?.message ?? e}`); return; }
    out.appendLine(`Updated the Semanticus AI Assistant entry to bundled engine ${engine.path}.`);
    if (licenseToken) await warnIfMcpJsonNotIgnored(ws);

    const noticeKey = `mcpAutoHealNotice:${target}:${engine.path}`;
    if (!context.globalState.get<boolean>(noticeKey)) {
        await context.globalState.update(noticeKey, true);
        void vscode.window.showInformationMessage(
            'The AI Assistant connection was updated for this Semanticus version. Reconnect the AI Assistant to load it.',
        );
    }
}

/// Whether <ws>/.mcp.json is git-ignored: true = ignored, false = NOT ignored, null = undeterminable (no git / not a
/// repo). Cheap probe — `git check-ignore -q .mcp.json` exits 0 (ignored), 1 (not ignored), or 128/ENOENT (no repo).
function mcpJsonIgnored(ws: string): Promise<boolean | null> {
    return new Promise((resolve) => {
        cp.execFile('git', ['check-ignore', '-q', '.mcp.json'], { cwd: ws }, (err: any) => {
            if (!err) return resolve(true);              // exit 0 — ignored
            if (err.code === 1) return resolve(false);   // exit 1 — tracked/untracked but NOT ignored
            resolve(null);                                // 128 / ENOENT — not a repo, or git unavailable
        });
    });
}

/// Whether <ws>/.mcp.json is TRACKED (already committed/staged): `git ls-files --error-unmatch` exits 0 for a tracked
/// path, 1 for untracked. Tracked is the dangerous case — a .gitignore line does NOTHING for an already-tracked file.
function mcpJsonTracked(ws: string): Promise<boolean | null> {
    return new Promise((resolve) => {
        cp.execFile('git', ['ls-files', '--error-unmatch', '.mcp.json'], { cwd: ws }, (err: any) => {
            if (!err) return resolve(true);
            if (err.code === 1) return resolve(false);
            resolve(null);                                // not a repo, or git unavailable
        });
    });
}

// The remediation for an already-tracked .mcp.json. We NEVER run destructive git on the user's repo ourselves —
// we surface the exact commands for the user to review and run.
const MCP_UNTRACK_COMMANDS = [
    '# Stop tracking .mcp.json (it holds your Semanticus Pro license token):',
    'echo .mcp.json >> .gitignore',
    'git rm --cached .mcp.json',
    'git add .gitignore',
    'git commit -m "Stop tracking .mcp.json"',
    '# The token is still in PAST commits. To remove it from history, use a rewrite tool',
    '# such as git filter-repo: https://github.com/newren/git-filter-repo',
    '',
].join('\n');

/// One-time-per-workspace nudge: the .mcp.json we just wrote carries the Pro token (a bearer credential) and isn't
/// git-ignored. Three cases, all non-modal, warned at most once per workspace:
///   • already TRACKED — a .gitignore line cannot help and the token is in repo history; say exactly that and offer
///     the remediation commands (copied to the clipboard), never running destructive git ourselves;
///   • untracked — offer the one-click .gitignore add;
///   • tracking status UNKNOWN (git failed on the ls-files probe) — say so honestly and do NOT guess "untracked"
///     (that would offer a .gitignore line that cannot help an already-committed token), leaving the flag unset so
///     the check re-runs on the next Connect.
/// The warned flag is set ONLY on an explicit outcome (a remediation action SUCCEEDED, or the user chose "Not now"),
/// NOT on a failed action and NOT on a dismissed (Escape) prompt — either of those must re-offer on the next Connect
/// instead of being suppressed forever.
async function warnIfMcpJsonNotIgnored(ws: string): Promise<void> {
    const key = `mcpGitignoreWarned:${ws}`;
    if (extCtx.globalState.get<boolean>(key)) return;    // already handled for this workspace — never nag again
    if ((await mcpJsonIgnored(ws)) !== false) return;    // ignored, or can't tell (no repo/git) — nothing to warn about
    const markWarned = () => extCtx.globalState.update(key, true);

    const tracked = await mcpJsonTracked(ws);
    if (tracked === null) {
        // Git could not establish whether .mcp.json is tracked (the ls-files probe did not complete — git
        // unavailable or a transient error). Do NOT fall through to the untracked path and offer a .gitignore line
        // that would leave an already-committed token in place. Say so, and leave the flag UNSET so we re-check next time.
        vscode.window.showWarningMessage(
            'Semanticus wrote your Pro license token into .mcp.json but could not determine whether git is tracking that file (the git check did not complete). Before committing, verify that .mcp.json is git-ignored, or remove the token from it.',
        );
        return;   // flag intentionally left unset — re-offered on the next Connect Claude Code
    }

    if (tracked === true) {
        const pick = await vscode.window.showWarningMessage(
            '.mcp.json holds your Semanticus Pro license token and is already committed to this git repository, ' +
            'so the token is in your repo history. Adding it to .gitignore will not remove it. ' +
            'Use the git commands to stop tracking it and to clean the history.',
            'Copy git commands', 'Not now',
        );
        if (pick === 'Copy git commands') {
            try {
                await vscode.env.clipboard.writeText(MCP_UNTRACK_COMMANDS);
                vscode.window.showInformationMessage('Copied. Review and run the commands in this repository. Cleaning past commits needs git filter-repo (see the copied note).');
                void markWarned();
            } catch (e: any) { vscode.window.showErrorMessage(`Could not copy the commands: ${e?.message ?? e}`); }   // flag NOT set — re-offered next Connect
        } else if (pick === 'Not now') {
            void markWarned();   // explicit decline — don't nag again for this workspace
        }
        // pick === undefined (dismissed/Escape): leave the flag unset so the warning re-offers next Connect.
        return;
    }

    // tracked === false — untracked: offer the one-click .gitignore add.
    const pick = await vscode.window.showWarningMessage(
        '.mcp.json now contains your Pro license token and is not git-ignored, so it could be committed to your repo. Add it to .gitignore?',
        'Add to .gitignore', 'Not now',
    );
    if (pick === 'Add to .gitignore') {
        const gi = path.join(ws, '.gitignore');
        try {
            let body = ''; try { body = fs.readFileSync(gi, 'utf8'); } catch { /* new .gitignore */ }
            if (!body.split(/\r?\n/).some((l) => l.trim() === '.mcp.json')) {
                fs.writeFileSync(gi, body + (body && !body.endsWith('\n') ? '\n' : '') + '.mcp.json\n', 'utf8');
            }
            vscode.window.showInformationMessage('Added .mcp.json to .gitignore.');
            void markWarned();
        } catch (e: any) { vscode.window.showErrorMessage(`Could not update .gitignore: ${e?.message ?? e}`); }   // flag NOT set — re-offered next Connect
    } else if (pick === 'Not now') {
        void markWarned();   // explicit decline
    }
    // pick === undefined (dismissed/Escape): flag left unset so the warning re-offers next Connect.
}

/// True when this workspace's .mcp.json already wires the semanticus MCP server. Cheap best-effort read — any
/// parse/IO failure counts as "not connected" so the call-to-action stays visible rather than hiding on a bad file.
function hasSemanticusMcpEntry(ws: string | undefined): boolean {
    if (!ws) return false;
    try { return !!JSON.parse(fs.readFileSync(path.join(ws, '.mcp.json'), 'utf8'))?.mcpServers?.semanticus; }
    catch { return false; }
}

/// Show the "Connect AI Assistant" status-bar call-to-action only while the open folder is not yet wired.
function refreshAiConnectStatus(): void {
    if (!aiConnectStatus) return;
    const ws = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (ws && !hasSemanticusMcpEntry(ws)) aiConnectStatus.show(); else aiConnectStatus.hide();
}

/// First-run nudge: the one time Semanticus is used in a folder that isn't wired yet, offer to connect the AI
/// Assistant. Once handled it never re-prompts for that folder (the always-on view-title button + status chip
/// carry it from there), so this is a gentle offer, not a nag.
async function maybeOfferConnectAiAssistant(context: vscode.ExtensionContext): Promise<void> {
    const ws = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!ws || hasSemanticusMcpEntry(ws) || context.workspaceState.get('semanticus.connectPromptDone')) return;
    const pick = await vscode.window.showInformationMessage(
        'Connect your AI Assistant to this Semanticus session? This writes .mcp.json so it drives the same live model as the editor.',
        'Connect', 'Not now', "Don't ask again",
    );
    if (pick === 'Connect') await connectClaudeCodeCmd();
    if (pick) await context.workspaceState.update('semanticus.connectPromptDone', true);
}

async function connectClaudeCodeCmd(): Promise<void> {
    const ws = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!ws) { vscode.window.showErrorMessage('Open a folder first. Semanticus writes the AI Assistant connection into your open folder.'); return; }

    // Resolve the engine the SAME way ensureEngine() launches it (dev DLL → dotnet, else the bundled exe).
    let engine: ResolvedEngine;
    try { engine = resolveEngine(); }
    catch (e: any) { vscode.window.showErrorMessage(e?.message ?? String(e)); return; }

    // A Pro licence token, if the user has set one, rides along via the reliable --license flag.
    const licenseToken = await getLicenseToken();
    const entry = semanticusMcpEntry(engine, path.resolve(ws), licenseToken);
    const target = path.join(ws, '.mcp.json');

    let merged: Record<string, any>;
    if (fs.existsSync(target)) {
        let raw: string;
        try { raw = fs.readFileSync(target, 'utf8'); }
        catch (e: any) { vscode.window.showErrorMessage(`Could not read ${target}: ${e?.message ?? e}`); return; }
        let parsed: unknown;
        try { parsed = JSON.parse(raw); }
        catch {
            // Fail LOUD, write NOTHING: overwriting an unparseable .mcp.json would silently destroy whatever
            // (possibly hand-edited) servers it holds. Offer to open it so the user can fix the syntax.
            const pick = await vscode.window.showErrorMessage(`${target} is not valid JSON. Fix it, then run Connect AI Assistant again. Nothing was written.`, 'Open .mcp.json');
            if (pick === 'Open .mcp.json') void vscode.commands.executeCommand('vscode.open', vscode.Uri.file(target));
            return;
        }
        const res = mergeMcpConfig(parsed, entry);
        if (res.conflict) {
            const pick = await vscode.window.showWarningMessage(
                `${target} already has a different "semanticus" MCP server entry. Replace it?`,
                { modal: true, detail: 'The existing entry points elsewhere. Replace it to connect your AI Assistant to this Semanticus session and workspace, or keep it unchanged.' },
                'Replace', 'Keep existing',
            );
            if (pick !== 'Replace') return;
        }
        merged = res.merged;
    } else {
        merged = mergeMcpConfig(undefined, entry).merged;
    }

    // 2-space indent + trailing newline (matches how a human/formatter would leave the file).
    try { fs.writeFileSync(target, JSON.stringify(merged, null, 2) + '\n', 'utf8'); }
    catch (e: any) { vscode.window.showErrorMessage(`Could not write ${target}: ${e?.message ?? e}`); return; }
    refreshAiConnectStatus();   // the folder is wired now — drop the call-to-action chip

    // The token in .mcp.json args is the DOCUMENTED delivery channel for the user's own Claude Code (kept intact) —
    // but if the file now holds a token and isn't git-ignored, nudge once so it doesn't get committed to a shared repo.
    if (licenseToken) await warnIfMcpJsonNotIgnored(ws);

    // The restart is the step users miss: an .mcp.json change is only picked up on the AI Assistant's next start,
    // so lead with it (a fresh assistant process discovers the server; a running one needs a tools refresh).
    const pick = await vscode.window.showInformationMessage(
        'AI Assistant connected. Last step: restart it in this folder (or refresh its tools) so it loads them. Keep VS Code open so Studio holds the shared live session.',
        'Open .mcp.json',
    );
    if (pick === 'Open .mcp.json') void vscode.commands.executeCommand('vscode.open', vscode.Uri.file(target));
}

// ---- object authoring / editing from the tree ------------------------------------------------
// Every action invokes a typed engine op (origin='human'), so the human UI and the user's Claude
// drive the SAME ops on one live session — and each change broadcasts model/didChange both ways.

function warnNoEngine() { vscode.window.showWarningMessage('Semanticus engine not connected.'); }

/// The objects an action should operate on, multi-select-aware: the live selection when several are
/// selected, else the right-clicked node (VS Code passes the clicked item + the selection array).
function selectionRefs(node?: TreeNode, nodes?: TreeNode[]): TreeNode[] {
    const sel = (nodes && nodes.length) ? nodes : node ? [node] : treeView ? [...treeView.selection] : [];
    // Display-folder nodes are labels, not model objects — selection-wide ops (hide/delete/script/…)
    // operate on their members via the folder's own menu, never on the folder ref itself.
    return sel.filter((n) => n.kind !== 'dfolder');
}

/// 'column:Sales/Amount' -> { table: 'Sales', name: 'Amount' }. Works for measure/column/calcitem/hierarchy refs.
function refParts(ref: string): { kind: string; table?: string; name: string } {
    const colon = ref.indexOf(':');
    const kind = ref.slice(0, colon);
    const rest = ref.slice(colon + 1);
    // Model-scoped kinds (table:, function:, ...) carry no container, so the whole remainder is the name even when it
    // contains a '/' (a calc table named "A/B"). Only container-scoped kinds split on the first slash. Matches
    // daxHeader.refPartsOf — a shared invariant so a header re-key never lands on the wrong object.
    if (MODEL_SCOPED_KINDS.has(kind)) return { kind, name: rest };
    const slash = rest.indexOf('/');
    if (slash < 0) return { kind, name: rest };
    return { kind, table: rest.slice(0, slash), name: rest.slice(slash + 1) };
}

async function createAndRefresh(method: string, params: unknown[], label: string, openInEditor = false, afterOrigin: unknown[] = []): Promise<string | undefined> {
    if (!conn) { warnNoEngine(); return; }
    try {
        // afterOrigin: additive wire params that ride AFTER origin (e.g. createMeasure's displayFolder).
        const ref = await conn.sendRequest<string>(method, ...params, 'human', ...afterOrigin);
        tree.refresh();
        // Land DAX authoring in the full Monaco editor (the semanticus: virtual file), never a tiny input box.
        if (openInEditor && ref) await openDax({ ref, name: refParts(ref).name, kind: refParts(ref).kind, hasChildren: false });
        return ref;
    } catch (e: any) {
        vscode.window.showErrorMessage(`Could not create ${label}: ` + (e?.message ?? e));
    }
}

// ---- Zero-dialog authoring ------------------------------------------------------------------
// Create-then-edit: the FIVE DAX-bearing kinds (measure, calculated column, calculated table, calculation item,
// function) are created IMMEDIATELY with a generated, collision-checked name — no name InputBox — then opened in
// the full Monaco editor with the name as line 1 (a script-style header). Renaming happens in the header on Save;
// the object appears selected in the Model tree and the Properties view follows via the existing selection path.

/// Existing names in the container a new object would join (case-insensitive), so uniqueName can pick a free
/// "New X" / "New X 2". Table-scoped kinds read the container's children; model-scoped kinds read the roots.
async function existingNames(scope: 'container' | 'table' | 'function', containerRef?: string): Promise<Set<string>> {
    const names = new Set<string>();
    if (!conn) return names;
    try {
        if (scope === 'container' && containerRef) {
            for (const k of await conn.sendRequest<TreeNode[]>('listTree', containerRef)) names.add(k.name.toLowerCase());
        } else {
            const roots = await conn.sendRequest<TreeNode[]>('listTree', null);
            const want = scope === 'function' ? (r: TreeNode) => r.kind === 'function' : (r: TreeNode) => r.kind === 'table' || r.kind === 'calcgroup';
            for (const r of roots.filter(want)) names.add(r.name.toLowerCase());
        }
    } catch { /* engine hiccup — fall back to the base name (a duplicate would be caught by the create op) */ }
    return names;
}

/// Create an object, reveal + select it in the Model tree, then open its DAX editor WITH the name header. The
/// object exists the instant the command runs (one undo removes it); no dialog precedes the first keystroke.
async function createThenEdit(method: string, params: unknown[], label: string, where: string, afterOrigin: unknown[] = []): Promise<string | undefined> {
    if (!conn) { warnNoEngine(); return; }
    // CRITICAL 1: sample the AUTHORITATIVE model identity (sessionInfo RPC) BEFORE the create and verify it UNCHANGED
    // after. An MCP-door model swap racing the create would otherwise let us stamp (and open a rename-capable header
    // editor) against the WRONG model. The residual sub-RPC-window race — a swap landing between this sample and the
    // create committing — stays open until the engine takes an expected-session parameter on the create RPC.
    const beforeKey = modelKeyOf(await refreshStatus());
    // Split the create from the open: a create that SUCCEEDED but whose editor failed to open must NOT be reported as
    // "Could not create" (the object exists — one undo removes it). Report the two outcomes separately and honestly.
    let ref: string | undefined;
    try {
        ref = await conn.sendRequest<string>(method, ...params, 'human', ...afterOrigin);
    } catch (e: any) {
        vscode.window.showErrorMessage(`Could not create ${label}: ` + (e?.message ?? e));
        return;
    }
    tree.refresh();
    if (!ref) return;
    // CRITICAL 1 / MEDIUM 4: re-read identity and verify it didn't swap under the create. guardModelMatch requires BOTH
    // samples present AND equal — so two MISSING samples (no session / a failed sessionInfo) no longer pass an
    // undefined===undefined check and stamp a header editor from the stale cached global; that ambiguous case now warns
    // and points at the tree. On a mismatch this object may belong to a DIFFERENT model, so do NOT stamp/open a header
    // editor on it. (The create RPC takes no expectedSession fence — the five create ops are left engine-unchanged — so
    // this before/after sample is the create path's guard; the residual swap-during-create window is warned, not silently
    // mis-stamped.)
    const afterKey = modelKeyOf(await refreshStatus());
    if (!guardModelMatch(beforeKey, afterKey).ok) {
        vscode.window.showWarningMessage(`The model may have changed while creating ${label} "${refParts(ref).name}". It was created in the model that was open at the time. Find it in that model's tree.`);
        return ref;
    }
    try {
        await revealRefInTree(ref).catch(() => { /* cold tree — the refresh still shows it */ });
        const hUri = uriForHeaderRef(ref);              // a header uri: identity (hdr=1 + owning model) lives on the uri
        daxHeaderDocs.add(hUri.toString());             // warm the cache BEFORE open so readFile emits the header
        await openDaxAt(hUri, ref);
        void vscode.window.setStatusBarMessage(`$(add) Created ${label} "${refParts(ref).name}"${where}. Rename it in line 1 or edit the DAX below, then Save.`, 6000);
    } catch (e: any) {
        // The object was created (and is in the Model tree); only opening its editor failed. Say exactly that.
        vscode.window.showWarningMessage(`Created ${label} "${refParts(ref).name}"${where}, but its editor didn't open (${e?.message ?? e}). Select it in the Model tree to edit its DAX.`);
    }
    return ref;
}

// ---- Create (root) ----
async function authorNewTable() {
    const name = await vscode.window.showInputBox({ prompt: 'New table name' });
    if (!name) return;
    const ref = await createAndRefresh('createTable', [name], 'table');
    if (ref) vscode.window.showInformationMessage(`Created ${ref}. Add columns/measures via right-click; it needs a partition before deploy/refresh.`);
}

async function authorNewCalcTable() {
    if (!conn) { warnNoEngine(); return; }
    // Zero dialog: seed a unique name + a VALID placeholder expression (createCalculatedTable rejects an empty one),
    // then land the name + DAX in the Monaco editor. Replacing the placeholder is the first thing the analyst does.
    const name = uniqueName('New Calculated Table', await existingNames('table'));
    await createThenEdit('createCalculatedTable', [name, 'ROW("Value", BLANK())'], 'calculated table', '');
}

async function authorNewCalcGroup() {
    const name = await vscode.window.showInputBox({ prompt: 'New calculation group name' });
    if (!name) return;
    await createAndRefresh('createCalculationGroup', [name], 'calculation group');
}

async function authorNewRole() {
    const name = await vscode.window.showInputBox({ prompt: 'New role name' });
    if (!name || !conn) return;
    try { await conn.sendRequest('createRole', name, null, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Create role failed: ' + (e?.message ?? e)); }
}

async function authorNewFunction() {
    if (!conn) { warnNoEngine(); return; }
    // Zero dialog: UDF names forbid spaces, so the generated name (and its collision suffix) is space-free.
    const name = uniqueName('NewFunction', await existingNames('function'), '');
    await createThenEdit('createFunction', [name, '(x: INT64) => x'], 'function (needs compat level 1702+)', '');
}

// ---- Create (per parent node) ----
async function authorNewMeasure(n?: TreeNode) {
    // The Ctrl+Alt+N keybinding passes no node — resolve the owning table from the tree selection (any child
    // works: a selected measure/column means its table), else offer a table picker so the gesture always lands.
    if (!n) {
        const sel = treeView?.selection[0];
        if (sel && (sel.kind === 'table' || sel.kind === 'calcgroup' || sel.kind === 'dfolder')) n = sel;
        else {
            const t = sel?.ref ? refParts(sel.ref).table : undefined;
            if (t) n = { ref: 'table:' + t, name: t, kind: 'table', hasChildren: true };
        }
        if (!n) {
            if (!conn) { warnNoEngine(); return; }
            let tables: TreeNode[] = [];
            try { tables = (await conn.sendRequest<TreeNode[]>('listTree', null)).filter((x) => x.kind === 'table' || x.kind === 'calcgroup'); }
            catch { /* engine hiccup — the empty-list message below covers it */ }
            if (!tables.length) { vscode.window.showInformationMessage('Open a model (with at least one table) to add a measure.'); return; }
            const pick = await vscode.window.showQuickPick(tables.map((x) => ({ label: x.name, node: x })), { title: 'New Measure', placeHolder: 'Which table should hold the new measure?' });
            if (!pick) return;
            n = pick.node;
        }
    }
    // Invoked on a FOLDER node: the measure is born in that folder, on the folder's table — create + folder
    // ride ONE engine call (createMeasure's additive displayFolder param), so it is one undo step.
    let folder = '';
    if (n.kind === 'dfolder') {
        const fp = folderParts(n.ref);
        folder = fp.path;
        n = { ref: 'table:' + fp.table, name: fp.table, kind: 'table', hasChildren: true };
    }
    // Zero dialog: generate a unique measure name on the target table, create it, and open the editor with the
    // name as line 1. (The folder, if any, rides createMeasure's additive displayFolder param — one engine call.)
    const name = uniqueName('New Measure', await existingNames('container', n.ref));
    await createThenEdit('createMeasure', [n.ref, name, '0'], 'measure', ` on ${n.name}${folder ? ` in ${folder}` : ''}`, folder ? [folder] : []);
}

// ---- Display-folder gestures --------------------------------------------------------------------

/// "New Folder..." on a table / calculation group / folder node. An empty folder cannot exist in the model (a
/// folder IS its members' Display Folder paths), so ONE InputBox (the folder name — a name is the only input) then
/// the folder's first measure is created in it via create-then-edit, opening the editor. Existing measures move in
/// via each measure's own "Move to Folder" gesture.
async function newFolderCmd(n?: TreeNode) {
    if (!conn) { warnNoEngine(); return; }
    n ??= treeView?.selection[0];
    if (!n?.ref) { vscode.window.showInformationMessage('Right-click a table (or a folder on one) to add a folder.'); return; }
    const base = n.kind === 'dfolder' ? folderParts(n.ref) : { table: refParts(n.ref).name, path: '' };
    const name = await vscode.window.showInputBox({
        prompt: `New folder on ${base.table}${base.path ? ` inside ${base.path}` : ''}`,
        placeHolder: 'e.g. KPIs; use \\ to nest (KPIs\\Growth)',
        validateInput: (v) => normFolder(v) ? undefined : 'Type a folder name.',
    });
    if (!name) return;
    const path = (base.path ? base.path + '\\' : '') + normFolder(name);
    const mName = uniqueName('New Measure', await existingNames('container', 'table:' + base.table));
    await createThenEdit('createMeasure', ['table:' + base.table, mName, '0'], 'measure', ` in ${path}`, [path]);
}

/// "Move to Folder..." on measures/columns/hierarchies — quick-pick of the folders in use on the involved
/// tables (from the model, never hardcoded) + "New folder…" + "(no folder)". One batched, undoable move.
async function moveToFolderCmd(node?: TreeNode, nodes?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    const movable = selectionRefs(node, nodes).filter((t) => t.kind === 'measure' || t.kind === 'column' || t.kind === 'calcColumn' || t.kind === 'hierarchy');
    if (!movable.length) { vscode.window.showInformationMessage('Select a measure, column, or hierarchy to move to a folder.'); return; }
    const tables = [...new Set(movable.map((t) => refParts(t.ref).table).filter((t): t is string => !!t))];

    // Folders in use on the involved tables — every ancestor level is a real target too ('A\B' implies 'A').
    const folders = new Map<string, string>();
    for (const t of tables) {
        let kids: TreeNode[] = [];
        try { kids = await tree.tableChildren('table:' + t); } catch { /* engine hiccup — New folder… still offered */ }
        for (const k of kids) {
            let p = normFolder(k.displayFolder);
            while (p) {
                if (!folders.has(p.toLowerCase())) folders.set(p.toLowerCase(), p);
                const cut = p.lastIndexOf('\\');
                p = cut >= 0 ? p.slice(0, cut) : '';
            }
        }
    }
    type Item = vscode.QuickPickItem & { act: 'folder' | 'new' | 'clear'; path?: string };
    const items: Item[] = [
        ...[...folders.values()].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })).map((f): Item => ({ label: '$(folder) ' + f, act: 'folder', path: f })),
        { label: '$(new-folder) New folder…', detail: 'Type a new folder path (use \\ to nest)', act: 'new' },
        { label: '$(close) (no folder)', detail: 'Back to the table root', act: 'clear' },
    ];
    const what = movable.length === 1 ? `"${movable[0].name}"` : `${movable.length} objects`;
    const pick = await vscode.window.showQuickPick(items, {
        title: `Move ${what} to folder`,
        placeHolder: folders.size ? 'Pick a folder, or make a new one' : 'No folders on this table yet. Make the first one',
    });
    if (!pick) return;
    let target = '';
    if (pick.act === 'folder') target = pick.path!;
    else if (pick.act === 'new') {
        const name = await vscode.window.showInputBox({ prompt: 'New folder path', placeHolder: 'e.g. KPIs, or KPIs\\Growth to nest' });
        if (!name || !normFolder(name)) return;
        target = normFolder(name);
    }
    try {
        await conn.sendRequest('setDisplayFolder', movable.map((t) => t.ref), target, 'human');
        tree.refresh();
        void vscode.window.setStatusBarMessage(
            target ? `$(folder) Moved ${what} into ${target}. One undo restores ${movable.length === 1 ? 'it' : 'them'}` : `$(folder) ${what} moved out of ${movable.length === 1 ? 'its folder' : 'their folders'}`, 5000);
    } catch (e: any) { vscode.window.showErrorMessage('Move to folder failed: ' + (e?.message ?? e)); }
}

/// "Rename Folder..." — ONE engine op rewrites the Display Folder prefix on every member (nested subfolders
/// included), so a single undo restores the whole rename. Renaming to an existing sibling's name MERGES the
/// two folders (that is what equal paths mean) — the status message reports how many members moved.
async function renameFolderCmd(n?: TreeNode) {
    if (!conn) { warnNoEngine(); return; }
    n ??= treeView?.selection[0];
    if (!n || n.kind !== 'dfolder') { vscode.window.showInformationMessage('Right-click a folder in the Model tree to rename it.'); return; }
    const { table, path } = folderParts(n.ref);
    const cut = path.lastIndexOf('\\');
    const parent = cut >= 0 ? path.slice(0, cut) : '';
    const newName = await vscode.window.showInputBox({
        prompt: `Rename folder "${path}" on ${table}. Everything inside moves with it, in one undoable change`,
        value: n.name,
        validateInput: (v) => normFolder(v) ? undefined : 'Type a folder name.',
    });
    if (!newName || normFolder(newName) === n.name) return;
    const to = (parent ? parent + '\\' : '') + normFolder(newName);
    try {
        const r = await conn.sendRequest<{ members: number; to: string }>('renameDisplayFolder', 'table:' + table, path, to, 'human');
        tree.refresh();
        void vscode.window.setStatusBarMessage(`$(folder) Renamed to ${to}. ${r.members} item${r.members === 1 ? '' : 's'} moved, one undo restores them`, 5000);
    } catch (e: any) { vscode.window.showErrorMessage('Rename folder failed: ' + (e?.message ?? e)); }
}

/// Resolve the container a per-parent create needs. From a tree right-click the node is in hand (no prompt); from
/// the command palette (no node) we infer it from the selection, and only if that fails show ONE container picker.
async function resolveContainer(n: TreeNode | undefined, want: 'table' | 'calcgroup', title: string): Promise<TreeNode | undefined> {
    if (n?.kind === want) return n;
    // A calculation ITEM identifies its group (the group is the item's container — `calcitem:Group/Item`), so a
    // right-click on a calc item, or a selected calc item, needs NO picker. The group node uses the `table:` ref
    // prefix (a calc group is a table in TOM), matching what createCalculationItem / existingNames expect.
    const groupFromItem = (x?: TreeNode): TreeNode | undefined => {
        if (want !== 'calcgroup' || x?.kind !== 'calcitem') return undefined;
        const g = refParts(x.ref).table;
        return g ? { ref: 'table:' + g, name: g, kind: 'calcgroup', hasChildren: true } : undefined;
    };
    const fromN = groupFromItem(n);
    if (fromN) return fromN;
    const sel = treeView?.selection[0];
    if (sel?.kind === want) return sel;
    const fromSel = groupFromItem(sel);
    if (fromSel) return fromSel;
    if (want === 'table') { const t = sel?.ref ? refParts(sel.ref).table : undefined; if (t) return { ref: 'table:' + t, name: t, kind: 'table', hasChildren: true }; }
    if (!conn) { warnNoEngine(); return; }
    let roots: TreeNode[] = [];
    try { roots = (await conn.sendRequest<TreeNode[]>('listTree', null)).filter((x) => x.kind === want); }
    catch { /* engine hiccup — the empty-list message below covers it */ }
    if (!roots.length) { vscode.window.showInformationMessage(want === 'calcgroup' ? 'Create a calculation group first (right-click the model, New Calculation Group).' : 'Open a model with at least one table first.'); return; }
    const pick = await vscode.window.showQuickPick(roots.map((x) => ({ label: x.name, node: x })), { title, placeHolder: want === 'calcgroup' ? 'Which calculation group?' : 'Which table?' });
    return pick?.node;
}

async function authorNewCalcColumn(n?: TreeNode) {
    const t = await resolveContainer(n, 'table', 'New Calculated Column');
    if (!t) return;
    const name = uniqueName('New Column', await existingNames('container', t.ref));
    await createThenEdit('createCalculatedColumn', [t.ref, name, 'BLANK()'], 'calculated column', ` on ${t.name}`);
}

async function authorNewHierarchy(n: TreeNode) {
    const name = await vscode.window.showInputBox({ prompt: `New hierarchy on ${n.name}` });
    if (!name) return;
    const colsInput = await vscode.window.showInputBox({ prompt: 'Level columns (comma-separated, top level first)', placeHolder: 'Year, Quarter, Month' });
    if (colsInput === undefined) return;
    const levels = colsInput.split(',').map((s) => s.trim()).filter(Boolean);
    await createAndRefresh('createHierarchy', [n.ref, name, levels], 'hierarchy');
}

async function authorNewCalcItem(n?: TreeNode) {
    // From a right-click the calc-group node is in hand; from the palette resolve it (selection, else one picker).
    const g = await resolveContainer(n, 'calcgroup', 'New Calculation Item');
    if (!g) return;
    const name = uniqueName('New Calculation Item', await existingNames('container', g.ref));
    await createThenEdit('createCalculationItem', [g.ref, name, 'SELECTEDMEASURE()'], 'calculation item', ` in ${g.name}`);
}

/// Build a hierarchy from the multi-selected columns of one table (top level = first selected).
async function hierarchyFromColumnsCmd(node?: TreeNode, nodes?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    const cols = selectionRefs(node, nodes).filter((c) => c.kind === 'column' || c.kind === 'calcColumn');
    if (!cols.length) { vscode.window.showWarningMessage('Select one or more columns of a single table first.'); return; }
    const tables = new Set(cols.map((c) => refParts(c.ref).table));
    if (tables.size !== 1) { vscode.window.showWarningMessage('All columns must belong to the same table.'); return; }
    const table = refParts(cols[0].ref).table!;
    const names = cols.map((c) => refParts(c.ref).name);
    const hname = await vscode.window.showInputBox({ prompt: `New hierarchy (levels: ${names.join(' › ')})`, value: 'Hierarchy' });
    if (!hname) return;
    await createAndRefresh('createHierarchy', [`table:${table}`, hname, names], 'hierarchy');
}

async function authorNewRelationship(n: TreeNode) {
    if (!conn) { warnNoEngine(); return; }
    // ONE picker (was a free-text ref InputBox): the candidate lookup columns on OTHER tables — the "one" side this
    // many-side column points to. The from-column is the clicked node, so a single choice completes the relationship.
    const fromTable = refParts(n.ref).table;
    const items: (vscode.QuickPickItem & { ref: string })[] = [];
    try {
        const roots = await conn.sendRequest<TreeNode[]>('listTree', null);
        for (const t of roots.filter((r) => r.kind === 'table')) {
            if (t.name === fromTable) continue;
            for (const k of await conn.sendRequest<TreeNode[]>('listTree', t.ref)) {
                if (k.kind === 'column' || k.kind === 'calcColumn') items.push({ label: `${t.name}[${k.name}]`, ref: k.ref });
            }
        }
    } catch { /* engine hiccup — handled by the empty check below */ }
    if (!items.length) { vscode.window.showInformationMessage('No columns on other tables to relate to. Add a lookup table first.'); return; }
    const pick = await vscode.window.showQuickPick(items, {
        title: `New relationship from ${n.name}`, matchOnDescription: true,
        placeHolder: 'Pick the lookup column this column points to (many-to-one)',
    });
    if (!pick) return;
    await createAndRefresh('createRelationship', [n.ref, pick.ref, null, null], 'relationship');
}

// ---- Universal edit / navigation ----
async function authorDuplicate(n: TreeNode) {
    const newName = await vscode.window.showInputBox({ prompt: `Duplicate ${n.name} as…`, value: `${n.name} copy` });
    if (newName === undefined) return;
    // The legacy 3-arg positional shape (objRef, newName, origin): targetRef is appended AFTER origin on
    // the wire, so this in-place duplicate — and any older bundle attached to a newer engine — stays intact.
    await createAndRefresh('duplicateObject', [n.ref, newName || null], 'duplicate');
}

// ---- Model-tree copy/paste (Ctrl+C / Ctrl+V) --------------------------------------------------
// Copy stashes a REFERENCE (kind + ref) in extension state, never object content: Paste asks the engine to
// deep-clone the LIVE object (duplicate_object), so it rides the normal tracked-change chokepoint — undoable,
// broadcast on model/didChange, visible in Edit History. The `semanticus.treeClipboard` context key gates the
// Paste menu entries per clipboard kind, so an incompatible target simply never offers Paste.

/** Copyable tree kinds and their plain-English names (the UI never speaks engine). */
const COPYABLE_KINDS: Record<string, string> = {
    measure: 'measure', column: 'column', calcColumn: 'calculated column', hierarchy: 'hierarchy',
    table: 'table', calcgroup: 'calculation group', calcitem: 'calculation item',
};

// Node kinds F2 / Rename can act on — the SAME set the context-menu when-clause encodes. The F2 keybinding is
// scoped to these viewItems in package.json, but the command palette can still invoke it with an arbitrary
// tree selection, so the command re-checks: a structural/grouping node (Relationships, collection folders) has
// no Name row and must NOT open an empty grid. Display folders reroute to the prefix-rewrite prompt beforehand.
const RENAMEABLE_KINDS = new Set(['table', 'calcgroup', 'measure', 'column', 'calcColumn', 'hierarchy', 'calcitem', 'function', 'perspective']);

/** Valid paste-target node kinds per clipboard kind — the SAME matrix the package.json menu when-clauses
 *  encode. The Ctrl+V keybinding bypasses menu gating, so the command re-checks it and teaches (instead of
 *  silently duplicating in place) when the selection can't receive the clipboard. Keep in sync with the
 *  `semanticus.pasteObject` entries in package.json `menus.view/item/context`. (The 'reference' clipboard
 *  value has its own menu rule and routes to the Reference cherry-pick BEFORE this matrix is consulted —
 *  that paste ignores the clicked node.) */
const PASTE_TARGETS: Record<string, { kinds: string[]; where: string }> = {
    // A folder node accepts a measure paste like its table does — and the copy is FILED into that folder
    // (that is what pasting "into" a folder means). Other clipboards keep folders out: columns/hierarchies
    // are table-bound and a folder is not a model-scope target.
    measure: { kinds: ['table', 'calcgroup', 'measure', 'dfolder'], where: 'a table, a calculation group, a folder, or another measure' },
    column: { kinds: ['table', 'column', 'calcColumn'], where: 'its own table, or a column on it' },
    calcColumn: { kinds: ['table', 'column', 'calcColumn'], where: 'its own table, or a column on it' },
    hierarchy: { kinds: ['table', 'hierarchy'], where: 'its own table, or a hierarchy on it' },
    calcitem: { kinds: ['calcgroup', 'calcitem'], where: 'a calculation group, or a calculation item in one' },
    table: { kinds: ['table', 'calcgroup'], where: 'a table or calculation group (the copy is created at model scope)' },
    calcgroup: { kinds: ['table', 'calcgroup'], where: 'a table or calculation group (the copy is created at model scope)' },
};

function copyObjectCmd(n?: TreeNode) {
    if (!n) {
        // The Ctrl+C keybinding passes no node — copy the tree selection (one object in v1).
        const sel = treeView ? [...treeView.selection] : [];
        if (sel.length > 1) { void vscode.window.showInformationMessage('Copy works on one object at a time. Select a single object and copy again.'); return; }
        n = sel[0];
    }
    const label = n ? COPYABLE_KINDS[n.kind] : undefined;
    if (!n || !label) {
        void vscode.window.showInformationMessage('Select a measure, column, hierarchy, table, calculation group, or calculation item to copy.');
        return;
    }
    treeCopyStash = { ref: n.ref, kind: n.kind, name: n.name, table: refParts(n.ref).table };
    lastCopyFrom = 'model';
    void vscode.commands.executeCommand('setContext', 'semanticus.treeClipboard', n.kind);
    const hint = n.kind === 'measure' ? 'paste onto any table, or Ctrl+V for a copy beside it' : 'Ctrl+V (or right-click, Paste) to make a copy';
    void vscode.window.setStatusBarMessage(`$(clippy) Copied ${label} "${n.name}": ${hint}`, 5000);
}

async function pasteObjectCmd(n?: TreeNode) {
    // Two clipboards can hold something (this tree's copy, and the Reference Model tree's Ctrl+C) —
    // the most recent copy wins, like a real clipboard.
    if (copyStash && (lastCopyFrom === 'reference' || !treeCopyStash)) { await pasteIntoModelCmd(); return; }
    if (!treeCopyStash) {
        void vscode.window.showInformationMessage('Nothing copied yet. Copy an object in the Model tree (or the Reference Model tree) first.');
        return;
    }
    if (!conn) { warnNoEngine(); return; }
    const clip = treeCopyStash;
    const target = n ?? treeView?.selection[0];

    // The keybinding path bypasses the menu's when-clause gating, so re-check the same target matrix here:
    // an unsupported (or empty) selection teaches, never a silent in-place duplicate somewhere else.
    const rule = PASTE_TARGETS[clip.kind];
    if (!target || !rule || !rule.kinds.includes(target.kind)) {
        void vscode.window.showInformationMessage(`Paste a ${COPYABLE_KINDS[clip.kind]} onto ${rule?.where ?? 'a supported object'}. Select one and paste again.`);
        return;
    }

    // Where does this paste land? Same container = a copy beside the original. A measure pasted onto ANOTHER
    // table (or a calculation item onto another group) is cloned there. Columns and hierarchies only exist on
    // their own table, so a foreign table teaches instead of surprising. A folder target = its table, plus the
    // copy is filed into that folder afterwards.
    let targetRef: string | null = null;
    const tKind = target.kind;
    const tTable = tKind === 'table' || tKind === 'calcgroup' ? refParts(target.ref).name
        : tKind === 'dfolder' ? folderParts(target.ref).table
        : refParts(target.ref).table;
    const intoFolder = tKind === 'dfolder' ? folderParts(target.ref).path : '';
    if (clip.kind === 'measure' || clip.kind === 'calcitem') {
        if (tTable && tTable !== clip.table) targetRef = 'table:' + tTable;
    } else if (clip.kind === 'column' || clip.kind === 'calcColumn' || clip.kind === 'hierarchy') {
        if (tTable && tTable !== clip.table) {
            const why = clip.kind === 'hierarchy'
                ? `its levels are built from that table's columns`
                : `its data lives in that table`;
            void vscode.window.showInformationMessage(`A ${COPYABLE_KINDS[clip.kind]} can only be pasted onto its own table ("${clip.table}"): ${why}. Measures can be pasted onto any table.`);
            return;
        }
    }
    // table / calcgroup clipboards duplicate at model scope: any valid paste target works, no targetRef.

    try {
        // Wire shape: (objRef, newName, origin, targetRef) — targetRef rides AFTER origin (legacy 3-arg compat).
        const newRef = await conn.sendRequest<string>('duplicateObject', clip.ref, null, 'human', targetRef);
        // Pasting INTO a folder files the copy there. duplicate_object cannot file, so this is a second tracked
        // change — the Edit History shows both entries, and fully reverting the paste takes two undos. Honest
        // over clever: the copy exists even if the filing fails (the catch leaves it at its inherited folder).
        if (intoFolder && newRef) {
            try { await conn.sendRequest('setDisplayFolder', [newRef], intoFolder, 'human'); }
            catch (e: any) { void vscode.window.showInformationMessage(`Pasted, but could not file the copy into ${intoFolder}: ${e?.message ?? e}`); }
        }
        tree.refresh();
        const p = refParts(newRef);
        void vscode.window.setStatusBarMessage(`$(clippy) Pasted "${p.name}"${p.table ? ` on ${p.table}` : ''}${intoFolder ? ` in ${intoFolder}` : ''}`, 5000);
    } catch (e: any) {
        const msg: string = e?.message ?? String(e);
        if (/not found/i.test(msg)) {
            void vscode.window.showInformationMessage(`"${clip.name}" is no longer in the model (it may have been renamed or deleted since you copied it). Copy it again.`);
        } else if (msg.includes("Can't paste") || msg.includes('duplicate at model scope') || msg.includes('Duplicate is not supported')) {
            // The engine's teaching refusals ARE the explanation (what can be pasted where) — surface them
            // as information, without an alarming "failed" prefix. Real errors keep the error channel below.
            void vscode.window.showInformationMessage(msg);
        } else {
            vscode.window.showErrorMessage('Paste failed: ' + msg);
        }
    }
}

// F2 / right-click Rename: show the object in the Properties view and put the caret in its Name row
// (text selected, ready to type over). The grid's Name write goes through the same engine property path
// (DAX references auto-rewritten), so nothing is lost vs the old InputBox — which remains available as
// "Rename with Input Box" (semanticus.renameObjectInputBox) for anyone who prefers the prompt.
async function renameInPropertiesCmd(n?: TreeNode, ns?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    // Rename acts on exactly ONE object. A right-click passes the clicked node (n); the F2 keybinding / palette
    // pass nothing → fall back to the tree selection, but only when it's unambiguous. Several selected + no
    // clicked node ⇒ ask the user to narrow rather than silently pick one.
    const sel = (ns && ns.length) ? ns : treeView ? [...treeView.selection] : [];
    n ??= sel.length === 1 ? sel[0] : undefined;
    if (!n) {
        vscode.window.showInformationMessage(sel.length > 1 ? 'Select a single object to rename.' : 'Select an object in the Model tree to rename.');
        return;
    }
    if (!n.ref) { vscode.window.showInformationMessage('Select an object in the Model tree to rename.'); return; }
    if (n.kind === 'dfolder') { await renameFolderCmd(n); return; }   // a display folder is a label, not an object — no Name row; keep the prefix-rewrite prompt
    // Structural / grouping nodes (and anything without a Name row) have nothing to rename — say so instead of
    // opening an empty grid. (F2's when-clause already blocks these; this covers the palette path.)
    if (!RENAMEABLE_KINDS.has(n.kind)) { vscode.window.setStatusBarMessage(`"${n.name}" can't be renamed.`, 4000); return; }
    // Capture the F2 TARGET's ref NOW: showObject + the focus command both await, and a tree selection landing
    // during those awaits would move the grid to another object. Passing the target through keys the caret to
    // THIS object, not to whatever the grid happens to show when focusNameRow runs — otherwise the rename lands
    // on the wrong object.
    const targetRef = n.ref;
    await propGrid.showObject(n);
    await vscode.commands.executeCommand('semanticusProperties.focus');
    propGrid.focusNameRow(targetRef);
}

async function renameCmd(n?: TreeNode) {
    if (!conn) { warnNoEngine(); return; }
    n ??= treeView?.selection[0];   // no node passed — rename the tree selection
    if (!n?.ref) { vscode.window.showInformationMessage('Select an object in the Model tree to rename.'); return; }
    if (n.kind === 'dfolder') { await renameFolderCmd(n); return; }   // a folder rename is the prefix rewrite, not renameObject
    const newName = await vscode.window.showInputBox({ prompt: `Rename this ${COPYABLE_KINDS[n.kind] ?? n.kind} "${n.name}". DAX references are auto-rewritten.`, value: n.name });
    if (!newName || newName === n.name) return;
    try { await conn.sendRequest('renameObject', n.ref, newName, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Rename failed: ' + (e?.message ?? e)); }
}

async function authorDelete(node?: TreeNode, nodes?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    const targets = selectionRefs(node, nodes);
    if (!targets.length) return;
    const label = targets.length === 1 ? `${COPYABLE_KINDS[targets[0].kind] ?? targets[0].kind} "${targets[0].name}"` : `${targets.length} objects`;
    const ok = await vscode.window.showWarningMessage(`Delete ${label}? DAX references are NOT auto-rewritten, so check dependents first.`, { modal: true }, 'Delete');
    if (ok !== 'Delete') return;
    try {
        for (const t of targets) await conn.sendRequest('deleteObject', t.ref, 'human');
        tree.refresh();
    } catch (e: any) { vscode.window.showErrorMessage('Delete failed: ' + (e?.message ?? e)); }
}

/// Read-only Script ▸ (DAX / TMDL / TMSL) of the whole selection → opens the script in a scratch Monaco doc.
async function scriptObjectsCmd(format: 'dax' | 'tmdl' | 'tmsl', node?: TreeNode, nodes?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    let refs = selectionRefs(node, nodes).map((n) => n.ref).filter(Boolean);
    if (!refs.length) return;
    // Measures have a safe partial-TMDL round-trip in the engine. Other table children still use their containing
    // table until their own identity/apply contract exists. Deduped, order-preserving for mixed multi-selection.
    if (format === 'tmdl') refs = tmdlEditableRefs(refs);
    const lang = format === 'dax' ? 'dax' : format === 'tmsl' ? 'json' : 'tmdl';
    try {
        const text = await conn.sendRequest<string>('scriptObjects', refs, format);
        const doc = await vscode.workspace.openTextDocument({ content: text, language: lang });
        await vscode.window.showTextDocument(doc, { preview: false });
    } catch (e: any) { vscode.window.showErrorMessage('Script failed: ' + (e?.message ?? e)); }
}

// Measures remain individually editable; map every other table-child ref to its top-level table until that child
// kind has the same fail-closed partial-apply contract. Keep already-top-level refs as-is.
function tmdlEditableRefs(refs: string[]): string[] {
    const out: string[] = [];
    for (const r of refs) {
        const colon = r.indexOf(':');
        const rest = colon >= 0 ? r.slice(colon + 1) : r;
        const slash = rest.indexOf('/');
        const mapped = r.startsWith('measure:') || slash < 0 ? r : 'table:' + rest.slice(0, slash);
        if (!out.includes(mapped)) out.push(mapped);
    }
    return out;
}

/// Apply the edited TMDL script in the active editor back to the model (the round-trip for Script ▸ TMDL).
/// Reuses the engine's apply_tmdl: top-level documents and sentinel-owned measure blocks are applied in place via
/// TMDL's MetadataSerializationContext in one undoable batch; structural changes surface and the whole gesture undoes.
async function applyTmdlScriptCmd() {
    if (!conn) { warnNoEngine(); return; }
    const ed = vscode.window.activeTextEditor;
    if (!ed) { vscode.window.showWarningMessage('Open a TMDL script (right-click ▸ Script ▸ TMDL) to apply.'); return; }
    try {
        const r = await conn.sendRequest<{ applied: string[]; skipped: string[] }>('applyTmdlScript', ed.document.getText(), 'human');
        tree.refresh();
        const a = r.applied?.length ?? 0, s = r.skipped?.length ?? 0;
        if (s) vscode.window.showWarningMessage(`Applied ${a} TMDL document(s); skipped ${s}: ${r.skipped.join('; ')}`);
        else vscode.window.showInformationMessage(`Applied ${a} TMDL document(s) to the model.`);
    } catch (e: any) { vscode.window.showErrorMessage('Apply TMDL failed: ' + (e?.message ?? e)); }
}

/// Apply the edited DAX script in the active editor back to the model (the round-trip for Script ▸ DAX).
/// Reuses the engine's apply_dax_script: each "// @object <ref>" block's expression is set in one undoable batch.
async function applyDaxScriptCmd() {
    if (!conn) { warnNoEngine(); return; }
    const ed = vscode.window.activeTextEditor;
    if (!ed) { vscode.window.showWarningMessage('Open a DAX script (right-click ▸ Script ▸ DAX) to apply.'); return; }
    try {
        const r = await conn.sendRequest<{ applied: string[]; skipped: string[] }>('applyDaxScript', ed.document.getText(), 'human');
        tree.refresh();
        const a = r.applied?.length ?? 0, s = r.skipped?.length ?? 0;
        if (s) vscode.window.showWarningMessage(`Applied ${a} expression(s); skipped ${s} (not found / not DAX): ${r.skipped.join(', ')}`);
        else vscode.window.showInformationMessage(`Applied ${a} DAX expression(s) to the model.`);
    } catch (e: any) { vscode.window.showErrorMessage('Apply DAX script failed: ' + (e?.message ?? e)); }
}

/// Set IsHidden on the whole selection (skips objects that can't be hidden).
async function setHiddenCmd(hidden: boolean, node?: TreeNode, nodes?: TreeNode[]) {
    if (!conn) { warnNoEngine(); return; }
    for (const t of selectionRefs(node, nodes)) {
        try { await conn.sendRequest('setObjectProperty', t.ref, 'IsHidden', String(hidden), 'human'); } catch { /* not hideable */ }
    }
    tree.refresh();
}

async function showDependentsCmd(n: TreeNode) {
    if (!conn || !n) return;
    try {
        const deps = await conn.sendRequest<{ ref: string; name: string; kind: string }[]>('getDependents', n.ref);
        if (!deps.length) { vscode.window.showInformationMessage(`Nothing references ${n.name}.`); return; }
        const pick = await vscode.window.showQuickPick(
            deps.map((d) => ({ label: d.name, description: d.kind, detail: d.ref })),
            { title: `${deps.length} dependent(s) of ${n.name}. Pick to open its DAX`, matchOnDescription: true });
        if (pick?.detail) {
            try { await openDax({ ref: pick.detail, name: pick.label, kind: refParts(pick.detail).kind, hasChildren: false }); }
            catch { vscode.window.showInformationMessage(pick.detail); }
        }
    } catch (e: any) { vscode.window.showErrorMessage('Show dependents failed: ' + (e?.message ?? e)); }
}

async function revealProperties(n: TreeNode) {
    if (n) await propGrid.showObject(n);
    await vscode.commands.executeCommand('semanticusProperties.focus');
}

// ---- Measure-specific ----
interface FormatTemplate {
    id: string; category: string; name: string; description: string; kind: 'static' | 'dynamic';
    formatString: string; sampleInput: string; exampleOutput: string; common: boolean;
    note?: string; credit?: string; pack?: string;
}

// Format-string picker backed by the engine's curated template library (list_format_templates) — the SAME catalog
// the agent sees over MCP (the agent door is the analyst door). A STATIC pick writes the literal via setMeasureFormat;
// a DYNAMIC (DAX) pick writes the measure's dynamic format string via setMeasureFormatExpression (Power BI's
// "Format = Dynamic"). Falls back to the built-in presets if the catalog can't be fetched, and always offers Custom….
async function setFormatCmd(n: TreeNode) {
    if (!conn || !n) return;
    let templates: FormatTemplate[] = [];
    try { templates = await conn.sendRequest<FormatTemplate[]>('listFormatTemplates', null, null); } catch { /* fall back below */ }

    type Item = vscode.QuickPickItem & { tpl?: FormatTemplate; custom?: boolean };
    const toItem = (t: FormatTemplate): Item => ({
        label: t.name,
        description: t.kind === 'dynamic' ? 'DAX (dynamic)' : t.formatString,
        detail: `${t.exampleOutput}${t.note ? '  ·  ' + t.note : ''}`,
        tpl: t,
    });

    const items: Item[] = [];
    if (templates.length) {
        const common = templates.filter((t) => t.common);
        if (common.length) {
            items.push({ label: 'Common', kind: vscode.QuickPickItemKind.Separator });
            for (const t of common) items.push(toItem(t));
        }
        let cat = '';
        for (const t of templates) {
            if (t.category !== cat) { cat = t.category; items.push({ label: cat, kind: vscode.QuickPickItemKind.Separator }); }
            items.push(toItem(t));
        }
    } else {
        for (const p of ['#,0', '#,0.00', '0.0%', '0.00%', '\\$#,0', '\\$#,0.00', '#,0;(#,0)', '0']) items.push({ label: p });
    }
    items.push({ label: 'Other', kind: vscode.QuickPickItemKind.Separator });
    items.push({ label: '$(pencil) Custom…', detail: 'Type a format string by hand', custom: true });

    const pick = await vscode.window.showQuickPick(items, {
        title: `Format string for ${n.name}`, matchOnDescription: true, matchOnDetail: true,
        placeHolder: 'Pick a template (preview shows a sample), or Custom…',
    });
    if (!pick) return;
    try {
        if (pick.custom) {
            const c = await vscode.window.showInputBox({ prompt: 'Format string', value: '#,0' });
            if (!c) return;
            await conn.sendRequest('setMeasureFormat', n.ref, c, 'human');
        } else if (pick.tpl && pick.tpl.kind === 'dynamic') {
            await conn.sendRequest('setMeasureFormatExpression', n.ref, pick.tpl.formatString, 'human');
        } else {
            await conn.sendRequest('setMeasureFormat', n.ref, pick.tpl ? pick.tpl.formatString : pick.label, 'human');
        }
        tree.refresh();
    } catch (e: any) { vscode.window.showErrorMessage('Set format failed: ' + (e?.message ?? e)); }
}

async function timeIntelCmd(n: TreeNode) {
    if (!conn || !n) return;
    const dateCol = await vscode.window.showInputBox({ prompt: `Date column for time-intelligence on ${n.name}`, placeHolder: "column:Date/Date or 'Date'[Date]" });
    if (!dateCol) return;
    try {
        await conn.sendRequest('generateTimeIntelligence', n.ref, dateCol, null, null, 'human');
        tree.refresh();
        vscode.window.showInformationMessage(`Generated YTD/QTD/MTD/PY/YoY measures for ${n.name}.`);
    } catch (e: any) { vscode.window.showErrorMessage('Time-intelligence failed: ' + (e?.message ?? e)); }
}

// ---- Column-specific ----
async function summarizeByCmd(n: TreeNode) {
    if (!conn || !n) return;
    const pick = await vscode.window.showQuickPick(['None', 'Sum', 'Average', 'Min', 'Max', 'Count', 'DistinctCount'], { title: `Default summarization for ${n.name}` });
    if (!pick) return;
    try { await conn.sendRequest('setColumnMetadata', n.ref, null, pick, null, null, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Set summarize-by failed: ' + (e?.message ?? e)); }
}

async function dataCategoryCmd(n: TreeNode) {
    if (!conn || !n) return;
    const cats = ['Address', 'City', 'Continent', 'Country', 'County', 'Latitude', 'Longitude', 'PostalCode', 'StateOrProvince', 'Place', 'WebUrl', 'ImageUrl', 'Barcode'];
    const pick = await vscode.window.showQuickPick(cats, { title: `Data category for ${n.name}` });
    if (!pick) return;
    try { await conn.sendRequest('setColumnMetadata', n.ref, null, null, pick, null, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Set data category failed: ' + (e?.message ?? e)); }
}

// ---- Table-specific ----
async function markDateTableCmd(n: TreeNode) {
    if (!conn || !n) return;
    const dateCol = await vscode.window.showInputBox({ prompt: `Date/key column of ${n.name} (optional)`, placeHolder: 'Date' });
    if (dateCol === undefined) return;
    try { await conn.sendRequest('markDateTable', n.ref, dateCol || null, 'human'); tree.refresh(); vscode.window.showInformationMessage(`Marked ${n.name} as the date table.`); }
    catch (e: any) { vscode.window.showErrorMessage('Mark date table failed: ' + (e?.message ?? e)); }
}

// ---- Partition-specific ----
// Refresh ("process") a single partition. The QuickPick lists every partition-level option WITH its explanation;
// then a dry-run previews the live write and a modal confirms before commit (a real refresh executes on the engine).
async function refreshPartitionCmd(n: TreeNode) {
    if (!conn || !n || !n.ref?.startsWith('partition:')) return;
    let types: any[];
    try { types = await conn.sendRequest('listRefreshTypes'); }
    catch (e: any) { vscode.window.showErrorMessage('Could not load refresh types: ' + (e?.message ?? e)); return; }
    const pick = await vscode.window.showQuickPick(
        (types || []).filter(t => t.partitionLevel).map(t => ({
            label: t.name + (t.recommended ? '  $(star-full)' : ''),
            detail: t.explanation,
            type: t.name as string,
        })),
        { title: `Refresh partition: ${n.name}`, placeHolder: 'Choose how to refresh (each option is explained below)', matchOnDetail: true });
    if (!pick) return;

    // Dry run first — preview what would happen, and surface any refusal (e.g. not connected to a live model).
    let plan: any;
    try { plan = await conn.sendRequest('refreshPartition', n.ref, pick.type, null, null, null, null, null, false); }
    catch (e: any) { vscode.window.showErrorMessage('Refresh not available: ' + (e?.message ?? e)); return; }

    const confirmLabel = pick.type === 'ClearValues' ? 'Clear values (destructive)' : `Run ${pick.type} refresh`;
    const ok = await vscode.window.showWarningMessage(
        plan?.plan ?? `Refresh ${n.name} with ${pick.type}?`,
        { modal: true, detail: `LIVE data write against ${plan?.endpoint ?? 'the live model'}${plan?.database ? ' / ' + plan.database : ''}.` },
        confirmLabel);
    if (ok !== confirmLabel) return;

    // Commit — the actual live refresh.
    try {
        const res: any = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `Refreshing ${n.name} (${pick.type})…`, cancellable: false },
            () => conn!.sendRequest('refreshPartition', n.ref, pick.type, null, null, null, null, null, true));
        if (res?.committed) vscode.window.showInformationMessage(`Refreshed ${n.name} (${pick.type}).`);
        else vscode.window.showErrorMessage(`Refresh failed; nothing changed: ${res?.error ?? 'unknown error'}`);
    } catch (e: any) { vscode.window.showErrorMessage('Refresh failed: ' + (e?.message ?? e)); }
}

// ---- Relationship-specific ----
async function relCrossFilterCmd(n: TreeNode) {
    if (!conn || !n) return;
    const pick = await vscode.window.showQuickPick(['OneDirection', 'BothDirections'], { title: 'Cross-filter direction (prefer OneDirection in a star schema)' });
    if (!pick) return;
    try { await conn.sendRequest('setRelationship', refParts(n.ref).name, pick, null, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Set cross-filter failed: ' + (e?.message ?? e)); }
}

async function relToggleActiveCmd(n: TreeNode) {
    if (!conn || !n) return;
    const makeActive = n.name.includes('(inactive)');   // the tree labels inactive relationships
    try { await conn.sendRequest('setRelationship', refParts(n.ref).name, null, makeActive, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Toggle active failed: ' + (e?.message ?? e)); }
}

// ---- Role-specific ----
async function roleModelPermCmd(n: TreeNode) {
    if (!conn || !n) return;
    const pick = await vscode.window.showQuickPick(['None', 'Read', 'ReadRefresh', 'Refresh', 'Administrator'], { title: `Model permission for ${n.name}` });
    if (!pick) return;
    try { await conn.sendRequest('setRolePermission', n.name, pick, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Set permission failed: ' + (e?.message ?? e)); }
}

async function roleAddRlsCmd(n: TreeNode) {
    if (!conn || !n) return;
    const table = await vscode.window.showInputBox({ prompt: `RLS: table to filter for role ${n.name}`, placeHolder: 'table:Sales' });
    if (!table) return;
    const filter = await vscode.window.showInputBox({ prompt: `Row-filter DAX for ${table} (empty = deny all)`, placeHolder: '[Region] = "West"' });
    if (filter === undefined) return;
    try { await conn.sendRequest('setTablePermission', n.name, table, filter, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Set RLS failed: ' + (e?.message ?? e)); }
}

async function roleAddMemberCmd(n: TreeNode) {
    if (!conn || !n) return;
    const member = await vscode.window.showInputBox({ prompt: `Add member to role ${n.name} (UPN or group name)`, placeHolder: 'user@contoso.com' });
    if (!member) return;
    try { await conn.sendRequest('setRoleMember', n.name, member, true, 'human'); tree.refresh(); }
    catch (e: any) { vscode.window.showErrorMessage('Add member failed: ' + (e?.message ?? e)); }
}

// ---- Properties view (the property grid, docked under the Model tree) ------------------------
// Reflects the selected object's TOM properties (via get_properties) into a live, editable grid.
// Edits call set_property (origin='human'); a model/didChange refreshes it — so you watch the grid
// update under your cursor when your Claude edits the same object. No WinForms PropertyGrid port.

interface PropDesc { name: string; displayName: string; category: string; description: string; kind: string; value: string; options: string[]; readOnly: boolean; varies?: boolean; suggestions?: string[]; hint?: string; }

/// Merge per-object property descriptors for a (possibly multi-) selection: a single object passes through; a
/// multi-selection keeps only properties present on EVERY object (by name+kind), marking a property `varies` when
/// the objects disagree on its value (so the editor shows a "multiple values" placeholder; a set applies to all).
function mergePropDescs(all: PropDesc[][]): PropDesc[] {
    if (all.length === 0) return [];
    if (all.length === 1) return all[0];
    const first = all[0];
    const out: PropDesc[] = [];
    for (const p of first) {
        let onAll = true;
        let varies = false;
        for (let i = 1; i < all.length; i++) {
            const q = all[i].find((x) => x.name === p.name && x.kind === p.kind);
            if (!q) { onAll = false; break; }
            if (q.value !== p.value) varies = true;
            if (q.readOnly) p.readOnly = true;   // read-only on any → read-only for the batch
            if (q.suggestions?.length) p.suggestions = [...new Set([...(p.suggestions ?? []), ...q.suggestions])];   // union prefills
        }
        if (onAll) out.push({ ...p, varies, value: varies ? '' : p.value });
    }
    return out;
}

class PropertyGridProvider implements vscode.WebviewViewProvider {
    private view?: vscode.WebviewView;
    private refs: string[] = [];
    private names: string[] = [];
    private _pushSeq = 0;   // monotonic request id: a newer push() (selection changed) bumps this so an older, slower fetch drops its result
    private templates?: FormatTemplate[];   // format-template catalog — static engine data, fetched ONCE per session
    private templatesPosted = false;        // whether THIS webview instance has the catalog (reset when the view is recreated)
    private modelName = 'Model';
    private pendingFocusKey: string | null = null;   // an F2 rename asked for the Name row before the webview was live — flushed on 'ready', keyed to its object

    constructor(private readonly getConn: () => MessageConnection | undefined, private readonly extensionUri: vscode.Uri) {}

    resolveWebviewView(view: vscode.WebviewView) {
        this.view = view;
        this.templatesPosted = false;
        view.webview.options = { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(this.extensionUri, 'media')] };
        view.webview.html = this.html(view.webview);
        view.webview.onDidReceiveMessage(async (msg: any) => {
            const conn = this.getConn();
            if (msg?.type === 'ready') {
                void this.push(); void this.pushTemplates();
                // Flush a rename intent that arrived while the view was (re)creating. Ordering vs push's 'load'
                // doesn't matter: the grid parks 'focusName' (keyed to its object) until that object's render.
                if (this.pendingFocusKey != null) {
                    const key = this.pendingFocusKey; this.pendingFocusKey = null;
                    // Only flush if the selection is STILL that object — a selection change while the view was
                    // (re)creating means the F2 target is gone; discard rather than focus the wrong row.
                    if (key === JSON.stringify(this.refs)) void view.webview.postMessage({ type: 'focusName', key });
                }
                return;
            }
            if (msg?.type === 'set' && conn && this.refs.length) {
                // The multi-select property write is ONE atomic, all-or-nothing, undoable change (#140): a single
                // grid gesture must be a single undo entry, not N. Route the generic property through the batched
                // engine op (set_properties) — a mid-batch failure rolls the WHOLE batch back and names the object.
                const refs = this.refs.slice();
                const names = this.names.slice();   // parallel to refs — the CURRENT (pre-write) object names, for the re-key below
                let error: string | null = null;
                try {
                    // Two rows have dedicated single-object engine ops with typed refusals (the format-expression and
                    // column-data-type paths); no batch variant, so they stay per-object. Everything else — the vast
                    // majority of grid rows — is the generic set_property path this issue makes atomic.
                    if (msg.name === 'FormatStringExpression') {
                        for (const ref of refs) await conn.sendRequest('setMeasureFormatExpression', ref, msg.value, 'human');
                    } else if (msg.name === 'DataType') {
                        // Column data type is a typed op (side effects on format/summarize); columns route there, any
                        // non-column ref falls back to the atomic generic write.
                        const cols = refs.filter((r) => r.startsWith('column:'));
                        const rest = refs.filter((r) => !r.startsWith('column:'));
                        for (const ref of cols) await conn.sendRequest('setColumnDataType', ref, msg.value, 'human');
                        if (rest.length) await conn.sendRequest('setObjectProperties', rest, msg.name, msg.value, 'human');
                    } else if (refs.length === 1) {
                        // Single object → the unchanged single-object path (no batch audit record).
                        await conn.sendRequest('setObjectProperty', refs[0], msg.name, msg.value, 'human');
                    } else {
                        await conn.sendRequest('setObjectProperties', refs, msg.name, msg.value, 'human');
                    }
                }
                catch (e: any) { error = String(e?.message ?? e); }
                // Re-push FIRST (reverts the field to the canonical value), THEN surface the error on it — order
                // matters because 'load' clears the error state in the webview.
                if (error) { await this.push(); this.view?.webview.postMessage({ type: 'setError', name: msg.name, error }); }
                // A successful Name write RENAMES the object, so our stored (name-based) ref is now stale: the
                // model/didChange refresh — and every later edit — would target the OLD ref and blank the grid.
                // We hold the truth here (old ref + the new name we just wrote), so adopt the renamed ref and
                // re-push. The delta payload carries only the NEW ref (no old→new map), so re-keying can't be
                // done generally from didChange; it must happen on the write path that knows both — here.
                else if (msg.name === 'Name' && refs.length === 1 && typeof msg.value === 'string' && msg.value
                         && this.refs.length === 1 && this.refs[0] === refs[0]) {   // guard: no newer selection landed
                    // Rebuild the ref by stripping the KNOWN old name and appending the new one. '/' is BOTH the ref
                    // separator AND a legal name character (a measure can be named "Gross/Net" → measure:Sales/Gross/Net),
                    // so the object-name boundary is NOT recoverable from the string alone — splicing at a slash would land
                    // inside the name and corrupt the ref. We hold the old name here (names[0], captured pre-write); if the
                    // ref ends with it, the prefix is everything before that suffix, and the new ref is prefix + newName.
                    // This is exact for every shape — 2-part (measure/column) AND 3-part (level:Table/Hierarchy/Name) —
                    // because the name, slashes and all, is always the literal tail of the ref. If the ref does NOT end
                    // with the old name (should never happen), skip the re-key rather than guess and corrupt it.
                    const oldName = names[0];
                    if (typeof oldName === 'string' && oldName && refs[0].endsWith(oldName)) {
                        const newRef = refs[0].slice(0, refs[0].length - oldName.length) + msg.value;
                        if (newRef !== refs[0]) { this.refs = [newRef]; this.names = [msg.value]; await this.push(); }
                    }
                }
                // On success the mutation broadcasts model/didChange → refresh() re-pushes the canonical values.
            }
        });
    }

    /// Push the format-template catalog (the same curated library the agent reads over MCP) so the Format String
    /// combo + the Format expression template picker have real entries. Fetched lazily once per session (it's
    /// immutable engine data); posted once per webview instance — retried from push() so a view that resolved
    /// BEFORE the engine connected still gets the catalog on the first selection.
    private async pushTemplates(): Promise<void> {
        if (this.templatesPosted) return;
        const conn = this.getConn();
        if (!conn || !this.view) return;
        if (!this.templates) {
            try { this.templates = await conn.sendRequest<FormatTemplate[]>('listFormatTemplates', null, null); }
            catch { return; }   // no catalog → the grid degrades to plain text inputs (no combo affordance)
        }
        // Slim to what the grid renders — name/example per entry, grouped by category.
        const slim = this.templates.map((t) => ({
            name: t.name, category: t.category, kind: t.kind, formatString: t.formatString,
            exampleOutput: t.exampleOutput, common: t.common, note: t.note ?? '',
        }));
        this.view.webview.postMessage({ type: 'formatTemplates', templates: slim });
        this.templatesPosted = true;
    }

    /// Show one OR many tree nodes (the Properties grid follows the tree's multi-selection).
    async showObjects(nodes: TreeNode[]): Promise<void> {
        if (nodes.length === 0) { await this.showModel(); return; }
        this.refs = nodes.map((n) => n.ref).filter(Boolean);
        this.names = nodes.map((n) => n.name);
        await this.push();
    }
    async showObject(node?: TreeNode): Promise<void> { await this.showObjects(node ? [node] : []); }
    async showModel(name?: string): Promise<void> {
        if (name) this.modelName = name;
        this.refs = [MODEL_REF];
        this.names = [this.modelName];
        await this.push();
    }
    async showModelIfEmpty(name?: string): Promise<void> {
        if (name) this.modelName = name;
        if (this.refs.length === 0) await this.showModel();
    }
    async clear(): Promise<void> { this.refs = []; this.names = []; await this.push(); }
    async refresh(): Promise<void> { if (this.refs.length) await this.push(); }

    /// F2 rename lands here: ask the grid to put the caret in its Name row (text selected, type-over ready).
    /// If the webview isn't live yet (the focus command may be resolving it right now), the intent is parked
    /// and flushed on the view's 'ready' handshake instead of being dropped.
    focusNameRow(targetRef: string): void {
        // The key is captured from the F2 TARGET (passed in), NOT read from this.refs here: between the keypress
        // and this call the selection can move (a tree click during the show/focus awaits), and keying off the
        // live this.refs would aim the caret at the NEW object → a rename of the wrong object. If the selection
        // has already moved off the target, DISCARD — never focus a Name row the user didn't F2.
        if (this.refs.length !== 1 || this.refs[0] !== targetRef) return;
        const key = JSON.stringify([targetRef]);
        if (!this.view) { this.pendingFocusKey = key; return; }
        // postMessage resolves false when the webview can't receive yet (still loading) — park the intent then too.
        try { this.view.webview.postMessage({ type: 'focusName', key }).then((ok) => { if (!ok) this.pendingFocusKey = key; }, () => { this.pendingFocusKey = key; }); }
        catch { this.pendingFocusKey = key; }   // the view is being torn down / recreated — 'ready' will flush
    }

    private async push(): Promise<void> {
        if (!this.view) return;
        const conn = this.getConn();
        // Snapshot the selection + claim a sequence number BEFORE any await. A selection change calls push() again,
        // bumping _pushSeq; this (now stale) call then bails instead of posting the OLD object's props under the NEW
        // selection's key/title. Read ONLY the snapshot after the await — never this.refs/this.names, which have moved on.
        const seq = ++this._pushSeq;
        const refs = this.refs.slice();
        const names = this.names.slice();
        if (!conn || !refs.length) { this.view.webview.postMessage({ type: 'empty' }); return; }
        void this.pushTemplates();   // no-op once this webview has the catalog
        try {
            const all = await Promise.all(refs.map((r) => conn.sendRequest<PropDesc[]>('getObjectProperties', r).catch(() => [] as PropDesc[])));
            if (seq !== this._pushSeq || !this.view) return;   // a newer selection superseded this fetch (or the view is gone) — drop it
            const props = mergePropDescs(all);
            // Adopt the CANONICAL object names from the load response, overwriting whatever placeholder seeded
            // this.names. The selection-bus path derives its name from refParts(ref), which misattributes a '/'
            // INSIDE a top-level name (table:Sales/EU → "EU"): the rename re-key strips the old name off the tail
            // of the ref, so it MUST hold the true whole name or it silently corrupts the ref (and endsWith still
            // passes). The Name property is the ground truth. seq===_pushSeq means this.refs hasn't moved, so all[]
            // is still index-aligned with this.names; where a fetch failed or has no Name, keep the placeholder.
            const canonical = refs.map((_, i) => all[i]?.find((p) => p.name === 'Name')?.value || names[i]);
            this.names = canonical;
            const title = refs.length === 1 ? (canonical[0] ?? refs[0]) : `${refs.length} objects selected`;
            // key = the selection's refs, NOT the header name: two same-named objects on different tables must
            // still read as a selection change in the webview (it resets in-progress editors on a new key).
            this.view.webview.postMessage({ type: 'load', key: JSON.stringify(refs), name: title, multi: refs.length > 1, props });
        } catch { if (seq === this._pushSeq && this.view) this.view.webview.postMessage({ type: 'empty' }); }
    }

    private html(webview: vscode.Webview): string {
        const nonce = getNonce();
        const base = vscode.Uri.joinPath(this.extensionUri, 'media', 'propgrid');
        const cssUri = webview.asWebviewUri(vscode.Uri.joinPath(base, 'propgrid.css'));
        const jsUri = webview.asWebviewUri(vscode.Uri.joinPath(base, 'propgrid.js'));
        const csp = `default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';`;
        // The grid's CSS + JS live in media/propgrid/* (single source of truth, shared with the uishot
        // screenshot harness). Edit them there, not here.
        return `<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<link rel="stylesheet" href="${cssUri}">
</head>
<body><div id="root"></div>
<script nonce="${nonce}" src="${jsUri}"></script></body></html>`;
    }
}

// ---- Studio webview (full-tab React dashboard) -----------------------------------------------

function openStudio(context: vscode.ExtensionContext) {
    if (studioPanel) { studioPanel.reveal(vscode.ViewColumn.Active); return; }

    const panel = vscode.window.createWebviewPanel(
        'semanticusStudio',
        'Semanticus Studio',
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
    studioPanel = panel;
    panel.onDidDispose(() => { if (studioPanel === panel) { studioPanel = undefined; studioReady = false; } });

    // Relay: webview RPC requests -> the single engine connection -> result back to the webview.
    panel.webview.onDidReceiveMessage(async (msg: any) => {
        if (msg?.type === 'revealInTree') { void revealRefInTree(msg.ref); return; }   // Studio "Reveal in Model tree"
        if (msg?.type === 'selectObject') { selectRefInProperties(msg.ref); return; }  // selection bus: Properties follows a Studio row focus (no focus steal)
        if (msg?.type === 'focusModelTree') { void vscode.commands.executeCommand('semanticusModel.focus'); return; }   // Ctrl+Alt+T in Studio — only the host can move focus out of a webview
        if (msg?.type === 'openLocalModel') {
            try {
                const opened = await openFromFile();
                if (opened) {
                    await propGrid.showModel(opened.modelName);
                    tree.refresh(); await refreshStatus(); void rebuildDaxSymbols();
                    panel.webview.postMessage({ type: 'reconnected' });
                }
            } catch (e: any) { vscode.window.showErrorMessage('Open failed: ' + (e?.message ?? e)); }
            return;
        }
        if (msg?.type === 'pickWorkingCopyFolder') {
            const picked = await vscode.window.showOpenDialog({ canSelectFiles: false, canSelectFolders: true, canSelectMany: false, openLabel: 'Choose local model folder' });
            panel.webview.postMessage({ type: 'workingCopyFolder', id: msg.id, folder: picked?.[0]?.fsPath ?? null });
            return;
        }
        if (msg?.type === 'pickReportPaths') {
            // Local report analysis targets on-disk report folders (a .pbip's *.Report folder, a definition folder,
            // or a project root). Folders are the common case, so this is a folder multi-select; power users paste a
            // .pbip file path (or a ;-separated list) into the free-text input the browse button sits beside.
            // ALWAYS answers — a dialog failure resolves null so the webview's pending slot never hangs.
            try {
                const picked = await vscode.window.showOpenDialog({ canSelectFiles: false, canSelectFolders: true, canSelectMany: true, openLabel: 'Select report folder(s)' });
                panel.webview.postMessage({ type: 'reportPaths', id: msg.id, paths: picked?.map((u) => u.fsPath) ?? null });
            } catch { try { panel.webview.postMessage({ type: 'reportPaths', id: msg.id, paths: null }); } catch { /* disposing */ } }
            return;
        }
        if (msg?.type === 'pickSpecFile') {
            const safeName = String(msg.suggestedName ?? 'model.spec.json').replace(/[^\w.-]+/g, '_').replace(/^[.]+/, '') || 'model.spec.json';
            const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
            let selected: vscode.Uri | undefined;
            if (msg.mode === 'save') {
                selected = await vscode.window.showSaveDialog({
                    defaultUri: folder ? vscode.Uri.joinPath(folder, safeName) : vscode.Uri.file(safeName),
                    saveLabel: 'Save Model Spec', filters: { 'Model Spec': ['json'] },
                });
            } else {
                selected = (await vscode.window.showOpenDialog({
                    defaultUri: folder, canSelectFiles: true, canSelectFolders: false, canSelectMany: false,
                    openLabel: 'Open Model Spec', filters: { 'Model Spec': ['json'] },
                }))?.[0];
            }
            panel.webview.postMessage({ type: 'specFile', id: msg.id, path: selected?.fsPath ?? null });
            return;
        }
        if (msg?.type === 'exportDoc') { void exportDocToFile(msg); return; }            // Documentation tab "Export…"
        if (msg?.type === 'printDoc') { void printDocInBrowser(msg); return; }           // Documentation tab "Print / PDF" (system browser)
        if (msg?.type === 'manageLicense') { void vscode.commands.executeCommand('semanticus.manageLicense'); return; }
        if (msg?.type === 'useAsReference') {                                            // Connections drawer "Use as reference" → point the Reference tree at a remembered model
            // A remembered model carries its endpoint, dataset, auth mode AND tenant — so a cross-tenant reference
            // targets its own tenant, never the default one az login is home to (the same fix as the native picker).
            referenceRef = { kind: 'workspace', endpoint: String(msg.endpoint ?? ''), database: msg.database || undefined, authMode: msg.authMode || 'azcli', tenantId: msg.tenantId || undefined };
            // Round-trip the binding: only AFTER the host has actually bound the reference (and the engine context
            // reflects it) do we nudge the drawer to re-read — a fire-and-forget refresh observed the OLD binding (MED 7).
            void loadReferenceModel().finally(() => { try { panel.webview.postMessage({ type: 'connectionChanged' }); } catch { /* disposing */ } });
            return;
        }
        if (msg?.type === 'openExternal') {                                              // sandboxed-iframe links → the user's browser
            const url = String(msg.url ?? '');
            if (/^https?:\/\//i.test(url)) void vscode.env.openExternal(vscode.Uri.parse(url));
            return;
        }
        if (msg?.type === 'studioReady') { studioReady = true; flushNav(); flushOpenConnections(); return; }     // webview mounted → flush queued nav / drawer-open
        // Diagram drop: hand over (and consume) the tables stashed when the user started dragging from the Model tree.
        if (msg?.type === 'requestDropTables') { panel.webview.postMessage({ type: 'dropTables', id: msg.id, tables: dragTablesStash }); dragTablesStash = []; return; }
        if (msg?.type !== 'rpc') return;
        if (!conn) { panel.webview.postMessage({ type: 'rpcResult', id: msg.id, error: 'Engine not connected.' }); return; }
        try {
            // byPosition: vscode-jsonrpc sends a lone object argument as JSON-RPC NAMED params, which the engine's
            // single-complex-parameter methods (searchModelEx(SearchOptions), …) cannot bind ("supplies 6").
            const params = msg.params ?? [];
            const result: any = params.length
                ? await conn.sendRequest(msg.method, ParameterStructures.byPosition, ...params)
                : await conn.sendRequest(msg.method);
            const modelSwapped = msg.method === 'open' || msg.method === 'openLocal' || msg.method === 'openLive' || msg.method === 'createModel'
                || (msg.method === 'prepareWorkingCopy' && !!result?.opened);
            if (modelSwapped) {
                try { await propGrid.showModel(result?.context?.editing?.modelName ?? result?.modelName ?? 'Model'); } catch { }
                tree.refresh();
                await refreshStatus();
                void rebuildDaxSymbols();
            }
            panel.webview.postMessage({ type: 'rpcResult', id: msg.id, result });
            // Resolve the caller BEFORE announcing the model swap. onReconnect deliberately rejects requests still
            // in flight against the old session; reversing these two messages would make a successful drawer open
            // report "connection lost" and discard its real result.
            if (modelSwapped) try { panel.webview.postMessage({ type: 'reconnected' }); } catch { }
            // A webview attach/disconnect changes the QUERYING side (and the account in play) but fires no
            // model/didChange — repaint the native status chip AND the sync chip so neither lags behind the footer the
            // user just acted through. refreshStatus() repaints BOTH (it calls setStatusFromInfo → renderSyncChip);
            // refreshSyncChip alone left the account chip stale on a webview connect/disconnect (HIGH 7).
            if (SYNC_REFRESH_METHODS.has(msg.method)) void refreshStatus();
        } catch (e: any) {
            panel.webview.postMessage({ type: 'rpcResult', id: msg.id, error: String(e?.message ?? e) });
        }
    });

    panel.webview.html = studioHtml(panel.webview, context);
}

// Open Studio (revealing an existing panel) and navigate it to a tab, optionally selecting a target object (e.g.
// a Model-tree "Preview data" → the Data tab + that table). If the webview is still mounting, the navigation is
// queued and flushed once it posts 'studioReady', so a cold open never drops the message.
function navigateStudio(context: vscode.ExtensionContext, tab: string, target?: string, addTables?: string[]) {
    pendingNav = { tab, target, addTables };
    openStudio(context);
    flushNav();
}
function flushNav() {
    if (studioPanel && studioReady && pendingNav) {
        try { studioPanel.webview.postMessage({ type: 'navigate', tab: pendingNav.tab, target: pendingNav.target, addTables: pendingNav.addTables }); } catch { /* disposing */ }
        pendingNav = undefined;
    }
}

// Model-tree "Add to Studio Diagram" → open Studio's Diagram tab and drop the selected table(s) onto the canvas.
// Native tree→webview-panel DRAG is blocked by VS Code (pointer-events:none on the iframe during a drag), so this
// command is the reliable tree-origin route (the in-webview "Add tables" palette is the reliable drag route).
function addToDiagramCmd(context: vscode.ExtensionContext, node: TreeNode, nodes?: TreeNode[]) {
    const sel = (nodes && nodes.length ? nodes : (node ? [node] : [...(treeView?.selection ?? [])]));
    const tables = [...new Set(sel.map(tableOfNode).filter((t): t is string => !!t))];
    if (!tables.length) { vscode.window.showInformationMessage('Right-click a table (or a column/measure/hierarchy) to add it to the diagram.'); return; }
    navigateStudio(context, 'diagram', undefined, tables);
}

// Model-tree "Preview data" → jump to the Data tab and preview that table's contents. n.ref is 'table:<name>'.
function previewTableDataCmd(context: vscode.ExtensionContext, n: TreeNode) {
    if (!n?.ref) return;
    navigateStudio(context, 'data', n.ref);
}

// ---- Update Schema (Tabular-Editor-style) ----------------------------------------------------
// Right-click a table (or the model view's ⋯ menu) → re-read the table's SOURCE columns, DIFF them against the
// model, and apply the accepted SUBSET (per-item checkboxes). Invoked with a table node from the context menu, or
// with none from the view title (then we prompt for a table). The diff + apply are the same dual-drive engine ops
// the agent uses (diffSchema / applySchemaUpdate); the source-read degrades gracefully when there's no reachable
// SQL/Fabric endpoint (an offline snapshot / non-SQL source), so the panel still renders with a clear banner.
const schemaDiffPanels = new Map<string, vscode.WebviewPanel>();

async function updateSchemaCmd(context: vscode.ExtensionContext, node?: TreeNode): Promise<void> {
    if (!conn) { void vscode.window.showWarningMessage('Open a model first.'); return; }
    let tableRef = node && node.kind === 'table' ? node.ref : undefined;
    if (!tableRef) {
        // No table node (invoked from the model view title) — pick one.
        let tables: TreeNode[] = [];
        try { tables = (await conn.sendRequest<TreeNode[]>('listTree', null)).filter((t) => t.kind === 'table'); }
        catch (e: any) { void vscode.window.showErrorMessage('Could not list tables: ' + (e?.message ?? e)); return; }
        if (!tables.length) { void vscode.window.showInformationMessage('No tables to update.'); return; }
        const pick = await vscode.window.showQuickPick(
            tables.map((t) => ({ label: t.name, ref: t.ref })),
            { title: 'Update Schema', placeHolder: 'Pick a table to re-read its source schema' });
        if (!pick) return;
        tableRef = pick.ref;
    }
    const tableName = tableRef.slice(tableRef.indexOf(':') + 1);
    openSchemaDiffPanel(context, tableRef, tableName);
}

function openSchemaDiffPanel(context: vscode.ExtensionContext, tableRef: string, tableName: string): void {
    const existing = schemaDiffPanels.get(tableRef);
    if (existing) { existing.reveal(vscode.ViewColumn.Active); void loadSchemaDiff(existing, tableRef); return; }

    const panel = vscode.window.createWebviewPanel(
        'semanticusSchemaDiff', `Update Schema: ${tableName}`, vscode.ViewColumn.Active,
        { enableScripts: true, retainContextWhenHidden: true });
    schemaDiffPanels.set(tableRef, panel);
    panel.onDidDispose(() => { if (schemaDiffPanels.get(tableRef) === panel) schemaDiffPanels.delete(tableRef); });

    panel.webview.onDidReceiveMessage(async (msg: any) => {
        if (!conn) { panel.webview.postMessage({ type: 'error', error: 'Engine not connected.' }); return; }
        if (msg?.type === 'ready' || msg?.type === 'refresh') { void loadSchemaDiff(panel, tableRef); return; }
        if (msg?.type === 'apply') {
            const items = (msg.items ?? []).map((i: any) => ({
                change: i.change,
                column: i.column,
                dataType: i.change === 'Removed' ? undefined : i.sourceType,   // Added/TypeChanged target type
                sourceColumn: i.change === 'Added' ? i.column : undefined,
            }));
            if (!items.length) { panel.webview.postMessage({ type: 'applied', result: { changed: false, added: 0, removed: 0, retyped: 0, skipped: [] } }); return; }
            try {
                const result = await conn.sendRequest('applySchemaUpdate', tableRef, items, 'human');
                panel.webview.postMessage({ type: 'applied', result });
                tree.refresh();
                void loadSchemaDiff(panel, tableRef);   // re-diff so the applied items drop out
            } catch (e: any) {
                panel.webview.postMessage({ type: 'error', error: String(e?.message ?? e) });
            }
        }
    });

    panel.webview.html = schemaDiffHtml(panel.webview, tableName);
}

async function loadSchemaDiff(panel: vscode.WebviewPanel, tableRef: string): Promise<void> {
    if (!conn) return;
    try {
        // Live source probe (sourceColumns=null). Fails soft: an unreachable source returns reachable=false + a
        // message, which the webview renders as a banner rather than an error dialog.
        const diff = await conn.sendRequest('diffSchema', tableRef, null, 'azcli', null);
        panel.webview.postMessage({ type: 'diff', diff });
    } catch (e: any) {
        panel.webview.postMessage({ type: 'error', error: String(e?.message ?? e) });
    }
}

function schemaDiffHtml(webview: vscode.Webview, tableName: string): string {
    const nonce = getNonce();
    const csp = `default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';`;
    const title = tableName.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c] as string));
    // Self-contained panel (inline theme-aware CSS + a nonce'd script). The diff is pushed via postMessage so the
    // panel can re-diff after an apply without regenerating the HTML.
    return `<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 12px 16px; }
  h2 { margin: 0 0 2px; font-size: 1.15em; }
  .sub { color: var(--vscode-descriptionForeground); margin-bottom: 12px; font-size: .9em; }
  .banner { padding: 8px 10px; border-radius: 4px; margin: 8px 0 14px;
            background: var(--vscode-inputValidation-warningBackground); border: 1px solid var(--vscode-inputValidation-warningBorder); }
  .group { margin: 14px 0 4px; }
  .group h3 { font-size: .95em; margin: 0 0 4px; display: inline-block; }
  .group .sel { font-size: .8em; margin-left: 10px; }
  a.sel { color: var(--vscode-textLink-foreground); cursor: pointer; text-decoration: none; }
  table { border-collapse: collapse; width: 100%; }
  td, th { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--vscode-widget-border, rgba(128,128,128,.25)); font-size: .9em; }
  th { color: var(--vscode-descriptionForeground); font-weight: 600; }
  .col { font-family: var(--vscode-editor-font-family, monospace); }
  .badge { display: inline-block; padding: 1px 7px; border-radius: 10px; font-size: .78em; font-weight: 600; }
  .b-add { background: rgba(64,160,64,.22); color: var(--vscode-testing-iconPassed, #3fb950); }
  .b-rem { background: rgba(200,64,64,.22); color: var(--vscode-testing-iconFailed, #f85149); }
  .b-type { background: rgba(200,150,40,.22); color: var(--vscode-charts-yellow, #d29922); }
  .type-arrow { color: var(--vscode-descriptionForeground); }
  .actions { margin-top: 18px; display: flex; gap: 10px; align-items: center; }
  button { font-family: inherit; padding: 5px 14px; border: none; border-radius: 3px; cursor: pointer;
           background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
  button:hover { background: var(--vscode-button-hoverBackground); }
  button.secondary { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  button:disabled { opacity: .5; cursor: default; }
  .result { margin-top: 12px; font-size: .9em; }
  .muted { color: var(--vscode-descriptionForeground); }
  .empty { color: var(--vscode-descriptionForeground); margin-top: 20px; }
</style></head>
<body>
  <h2>Update Schema: ${title}</h2>
  <div class="sub" id="sub">Reading the source schema…</div>
  <div id="banner"></div>
  <div id="content"></div>
  <div class="actions" id="actions" style="display:none">
    <button id="apply">Apply selected</button>
    <button id="refresh" class="secondary">Re-read source</button>
    <span class="muted" id="count"></span>
  </div>
  <div class="result" id="result"></div>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  const $ = (id) => document.getElementById(id);
  let items = [];
  function esc(s){ return String(s==null?'':s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
  const badge = { Added:'<span class="badge b-add">ADDED</span>', Removed:'<span class="badge b-rem">REMOVED</span>', TypeChanged:'<span class="badge b-type">TYPE</span>' };

  function render(diff){
    items = diff.items || [];
    const src = diff.source === 'fabric-sql' ? 'live source' : (diff.source || 'source');
    $('sub').textContent = diff.reachable
      ? ((items.length ? (diff.added+' added · '+diff.typeChanged+' type-changed · '+diff.removed+' removed') : 'No differences') + '  (from the '+src+')')
      : 'Could not read the live source.';
    $('banner').innerHTML = diff.reachable ? '' : '<div class="banner">'+esc(diff.error || 'Source unreachable.')+'</div>';
    if (!items.length){
      $('content').innerHTML = diff.reachable ? '<div class="empty">The model is in sync with the source. Nothing to update.</div>' : '';
      $('actions').style.display = diff.reachable ? 'none' : 'flex';
      $('apply').style.display = 'none';
      renderCount(); return;
    }
    const groups = [['Added','New columns at the source'],['TypeChanged','Changed data types'],['Removed','Columns no longer at the source']];
    let html = '';
    for (const [kind, label] of groups){
      const rows = items.filter(i => i.change === kind);
      if (!rows.length) continue;
      html += '<div class="group"><h3>'+badge[kind]+' '+esc(label)+'</h3>'
        + '<span class="sel"><a class="sel" data-all="'+kind+'">all</a> · <a class="sel" data-none="'+kind+'">none</a></span>'
        + '<table><thead><tr><th style="width:24px"></th><th>Column</th><th>'+(kind==='Removed'?'Model type':(kind==='TypeChanged'?'Model → source':'Source type'))+'</th></tr></thead><tbody>';
      for (const i of rows){
        const detail = kind==='TypeChanged' ? (esc(i.modelType)+' <span class="type-arrow">→</span> '+esc(i.sourceType))
                     : kind==='Removed' ? esc(i.modelType) : esc(i.sourceType)+(i.sqlType?' <span class="muted">('+esc(i.sqlType)+')</span>':'');
        // Removals are destructive → opt-in (unchecked by default); additions/retypes default checked.
        const checked = kind === 'Removed' ? '' : 'checked';
        html += '<tr><td><input type="checkbox" data-id="'+esc(i.id)+'" '+checked+'></td><td class="col">'+esc(i.column)+'</td><td>'+detail+'</td></tr>';
      }
      html += '</tbody></table></div>';
    }
    $('content').innerHTML = html;
    $('actions').style.display = 'flex';
    $('apply').style.display = '';
    document.querySelectorAll('a[data-all]').forEach(a => a.onclick = () => toggleGroup(a.getAttribute('data-all'), true));
    document.querySelectorAll('a[data-none]').forEach(a => a.onclick = () => toggleGroup(a.getAttribute('data-none'), false));
    document.querySelectorAll('input[type=checkbox]').forEach(c => c.onchange = renderCount);
    renderCount();
  }
  function toggleGroup(kind, on){
    const ids = new Set(items.filter(i => i.change === kind).map(i => i.id));
    document.querySelectorAll('input[type=checkbox]').forEach(c => { if (ids.has(c.getAttribute('data-id'))) c.checked = on; });
    renderCount();
  }
  function selectedItems(){
    const on = new Set(Array.from(document.querySelectorAll('input[type=checkbox]:checked')).map(c => c.getAttribute('data-id')));
    return items.filter(i => on.has(i.id));
  }
  function renderCount(){ const n = selectedItems().length; $('count').textContent = n ? (n+' selected') : 'nothing selected'; $('apply').disabled = !n; }

  $('apply').onclick = () => { $('apply').disabled = true; $('result').textContent = 'Applying…'; vscode.postMessage({ type:'apply', items: selectedItems() }); };
  $('refresh').onclick = () => { $('result').textContent=''; $('sub').textContent='Reading the source schema…'; vscode.postMessage({ type:'refresh' }); };

  window.addEventListener('message', (e) => {
    const m = e.data;
    if (m.type === 'diff') render(m.diff);
    else if (m.type === 'applied'){
      const r = m.result || {};
      const parts = [];
      if (r.added) parts.push(r.added+' added'); if (r.retyped) parts.push(r.retyped+' retyped'); if (r.removed) parts.push(r.removed+' removed');
      let t = r.changed ? ('Applied: '+parts.join(' · ')) : 'No changes applied.';
      if (r.skipped && r.skipped.length) t += ' (skipped: '+r.skipped.map(esc).join('; ')+')';
      $('result').innerHTML = esc(t);
    }
    else if (m.type === 'error'){ $('result').innerHTML = '<span class="b-rem">'+esc(m.error)+'</span>'; $('apply').disabled = false; }
  });
  vscode.postMessage({ type:'ready' });
</script>
</body></html>`;
}

// "Print / PDF": window.print() inside a VS Code webview (and doubly so inside the doc-preview's sandboxed
// iframe) is suppressed by the host — it silently does nothing. The reliable path on every OS: write the
// rendered HTML to a temp file and open it in the system browser, where Ctrl+P / "Save as PDF" just work.
async function printDocInBrowser(msg: any): Promise<void> {
    try {
        const content = typeof msg?.content === 'string' ? msg.content : '';
        if (!content) { void vscode.window.showWarningMessage('Nothing to print yet. The documentation preview is still rendering.'); return; }
        const safeName = String(msg?.suggestedName ?? 'documentation').replace(/[^\w.-]+/g, '_').replace(/^[.]+/, '') || 'documentation';
        const file = path.join(os.tmpdir(), `semanticus-doc-${safeName}-${Date.now().toString(36)}.html`);
        await fs.promises.writeFile(file, content, 'utf8');
        await vscode.env.openExternal(vscode.Uri.file(file));
        void vscode.window.setStatusBarMessage('Semanticus: documentation opened in your browser. Print or save as PDF there.', 6000);
    } catch (e: any) {
        void vscode.window.showErrorMessage('Print / PDF failed: ' + String(e?.message ?? e));
    }
}

// Save the Documentation tab's built output to a file. The content is fully rendered (and escaped) in the webview;
// the host just picks a path and writes it. The suggested name is sanitised; the user always confirms the final path.
async function exportDocToFile(msg: any): Promise<void> {
    try {
        const format = msg?.format === 'markdown' || msg?.format === 'json' ? msg.format : 'html';
        const ext = format === 'markdown' ? 'md' : format === 'json' ? 'json' : 'html';
        const content = typeof msg?.content === 'string' ? msg.content : '';
        const saveLabel = typeof msg?.saveLabel === 'string' && msg.saveLabel.trim() ? msg.saveLabel.trim() : 'Export documentation';
        const successLabel = typeof msg?.successLabel === 'string' && msg.successLabel.trim() ? msg.successLabel.trim() : 'Documentation';
        const safeName = String(msg?.suggestedName ?? 'documentation').replace(/[^\w.-]+/g, '_').replace(/^[.]+/, '') || 'documentation';
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
        const defaultUri = folder ? vscode.Uri.joinPath(folder, `${safeName}.${ext}`) : vscode.Uri.file(`${safeName}.${ext}`);
        const target = await vscode.window.showSaveDialog({
            defaultUri,
            saveLabel,
            filters: format === 'markdown' ? { Markdown: ['md'] } : format === 'json' ? { JSON: ['json'] } : { HTML: ['html', 'htm'] },
        });
        if (!target) return;   // user cancelled
        await vscode.workspace.fs.writeFile(target, Buffer.from(content, 'utf8'));
        const pick = await vscode.window.showInformationMessage(`${successLabel} exported to ${target.fsPath}`, 'Open', 'Reveal');
        if (pick === 'Open') void vscode.commands.executeCommand('vscode.open', target);
        else if (pick === 'Reveal') void vscode.commands.executeCommand('revealFileInOS', target);
    } catch (e: any) {
        void vscode.window.showErrorMessage('Export failed: ' + String(e?.message ?? e));
    }
}

function studioHtml(webview: vscode.Webview, context: vscode.ExtensionContext): string {
    const base = vscode.Uri.joinPath(context.extensionUri, 'media', 'studio');
    const jsUri = webview.asWebviewUri(vscode.Uri.joinPath(base, 'studio.js'));
    const cssUri = webview.asWebviewUri(vscode.Uri.joinPath(base, 'studio.css'));
    const nonce = getNonce();
    const csp = [
        `default-src 'none'`,
        `img-src ${webview.cspSource} https: data:`,
        `style-src ${webview.cspSource} 'unsafe-inline'`,
        // 'strict-dynamic': studio.js is an ES module (see vite.config.ts) that dynamic-import()s code-split
        // chunks (the lazy M-code cluster). A bare nonce only covers the entry <script>; 'strict-dynamic'
        // propagates that trust to the chunks it imports, so they load without listing each hashed filename.
        `script-src 'nonce-${nonce}' 'strict-dynamic'`,
        `font-src ${webview.cspSource}`,
        // The Documentation tab previews built docs in a sandboxed <iframe srcdoc>; 'self' permits that frame.
        // The doc's own inline <script> (search) has no nonce, so it stays inert in the preview by design.
        `frame-src 'self'`,
    ].join('; ');
    return `<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link rel="stylesheet" href="${cssUri}">
<title>Semanticus Studio</title></head>
<body><div id="root"></div><script type="module" nonce="${nonce}" src="${jsUri}"></script></body></html>`;
}

function getNonce(): string {
    let t = '';
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    for (let i = 0; i < 24; i++) t += chars.charAt(Math.floor(Math.random() * chars.length));
    return t;
}
