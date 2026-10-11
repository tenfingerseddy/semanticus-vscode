// Typed request/response + notification bridge between the Studio webview and the extension host.
// The host is the SOLE engine client; the webview reaches the engine only through host-relayed RPC.

import { studioErrorText } from './engineupdated.mjs';
import { pageModelIdentity } from './pagemodel';

const vscode = acquireVsCodeApi();

/// The ONE place a message leaves this page for the host. Every message names the model the page shows (pagemodel.ts), because the host runs any message that can lead to a write
/// as the person's action on THAT model (extension.ts pageScoped) and the engine refuses it when another model is open by then (join 11 solui, U1: a Studio Undo posted as
/// runCommand named no model, so the host's newer one took its place and the Undo removed an edit from a model the person had not seen). A static test (test/solui-page-stamp.test.mjs)
/// allows this ONE vscode.postMessage call in the webview, so a message type added tomorrow cannot skip the stamp.
function postToHostStamped(message: { type: string } & Record<string, unknown>): void {
  vscode.postMessage({ ...message, modelIdentity: pageModelIdentity() });
}

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void; timer: number };
const pending = new Map<number, Pending>();
let seq = 0;

// Per-request timeout so a wedged or vanished host can't leave a promise (and its UI spinner) hanging forever. Most
// methods are fast local TOM/metadata ops → 30s. Everything that leaves the process for real work gets a generous
// 10-min ceiling so the guard never rejects a genuine in-flight op: Fabric/cloud round-trips, LIVE QUERIES against
// the model (previewTable / runDax / runInterview / evaluateAndLog / explainValue / vertiPaqScan / verifyEquivalence),
// whole-suite Tests runs (runTests can probe many model and source-SQL targets),
// interactive-auth connections (connectLocal/connectXmla — a browser sign-in can take minutes), git network ops,
// cross-model opens (compareModels/applyDiff), the daxLib network family, and whole-model builds (a start plan's start reads
// the source with its sign-in, and its build makes the whole model). Names verified
// against the actual bridge callers (grep of webview/src rpc sites); matched as a PREFIX of the exact method name.
// The host wire protocol is unchanged.
const RPC_DEFAULT_TIMEOUT_MS = 30_000;
const RPC_LONG_TIMEOUT_MS = 10 * 60_000;
const LONG_RPC = /^(deploy|cicd|analyze|listReports|listWorkspaces|listDataAgents|listDeploymentPipelines|getDataAgent|getPipelineStages|createDataAgent|updateDataAgent|publishDataAgent|deleteDataAgent|generateDataAgentConfig|autogenerateSpecFrom|buildModelFromSpec|previewDeploy|previewTable|runDax|runSql|runTests|runInterview|evaluateAndLog|explainValue|profileDax|benchmark|verifyEquivalence|refreshPartition|captureQueryPlan|optimizeMeasure|aiReadinessScanLive|vertiPaqScan|exportVpax|applySchemaUpdate|diffSchema|getSourceSchema|makeModelAiReady|proposePlan|applyPlanItems|getModelPlan|fabricGit|connectLocal|connectXmla|openLive|openLocal|prepareWorkingCopy|gitPush|gitPull|gitClone|compareModels|applyDiff|daxLib|startModelFromSql|buildStartPlan|publishNewModel)/;
function rpcTimeoutMs(method: string): number {
  return LONG_RPC.test(method) ? RPC_LONG_TIMEOUT_MS : RPC_DEFAULT_TIMEOUT_MS;
}

// Reject every in-flight request at once: the host connection went away (engine reconnect / model reopen) or the
// webview is being torn down, so their replies will never arrive. Clears timers so nothing double-fires afterward.
export function rejectAll(reason: string): void {
  const err = new Error(reason);
  for (const [, p] of pending) { window.clearTimeout(p.timer); try { p.reject(err); } catch { /* isolate a caller's handler */ } }
  pending.clear();
}

export type ChangeDelta = { kind: string; ref: string; props?: string[] };
export type ChangeNotification = {
  sessionId: string;
  revision: number;
  origin: string;
  label?: string;
  deltas?: ChangeDelta[];
  /** "undo" or "redo" when the commit walked the timeline (a redo of an add carries no delta for it). */
  replay?: string;
};

// Multiple views subscribe independently; each returns an unsubscribe fn.
const listeners = new Set<(n: ChangeNotification) => void>();
export function onDidChange(fn: (n: ChangeNotification) => void): () => void {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}

// The session's change plan broadcasts separately (plan/didChange) so the Optimize tab can watch it
// assemble in real time as the engine seeds it and the user's Claude fills the AI-content items.
const planListeners = new Set<(v: unknown) => void>();
export function onPlanChange(fn: (v: unknown) => void): () => void {
  planListeners.add(fn);
  return () => { planListeners.delete(fn); };
}

// A Run the plan press tells the page each step as it goes (plan/runProgress, round 1): which rows are being worked out,
// applied, checked, done or skipped, and how long the step took. Each row of the plan shows its own step from it. The
// agent door gets no push (golden rule 2); it reads the result of the same call.
const planRunListeners = new Set<(v: unknown) => void>();
export function onPlanRunProgress(fn: (v: unknown) => void): () => void {
  planRunListeners.add(fn);
  return () => { planRunListeners.delete(fn); };
}

// The start plan of a new model from a SQL source broadcasts on its own channel (startPlan/didChange, lane sf-studio): the
// engine says the plan moved (Jev decided something, the assistant wrote words, either door edited it) and the page reads it again.
const startPlanListeners = new Set<(v: unknown) => void>();
export function onStartPlanChange(fn: (v: unknown) => void): () => void {
  startPlanListeners.add(fn);
  return () => { startPlanListeners.delete(fn); };
}

// A composer draft broadcasts on its own channel (draft/didChange, lane L4) with the whole draft view, so an open draft
// follows its parts as the user's assistant claims and writes them on the other door.
const draftListeners = new Set<(v: unknown) => void>();
export function onDraftChange(fn: (v: unknown) => void): () => void {
  draftListeners.add(fn);
  return () => { draftListeners.delete(fn); };
}

export type OperationProgress = { opKey: string; runId: string; done: number; total: number; note?: string };
const progressListeners = new Set<(v: OperationProgress) => void>();
export function onProgress(fn: (v: OperationProgress) => void): () => void {
  progressListeners.add(fn);
  return () => { progressListeners.delete(fn); };
}

// The model spec broadcasts separately (spec/didChange) so the Spec tab watches it assemble live as the engine
// auto-generates it and the human OR the user's Claude refine it on the same session.
const specListeners = new Set<(v: unknown) => void>();
export function onSpecChange(fn: (v: unknown) => void): () => void {
  specListeners.add(fn);
  return () => { specListeners.delete(fn); };
}

// Diagram layout broadcasts separately (layout/didChange) so the Diagram canvas moves live when the OTHER door
// (the user's Claude, or a second client) saves positions on the same model. Layout is engine-owned sidecar
// state (.semanticus/layout.json, keyed by LineageTag), NOT a model edit — hence its own channel. Origin lets a
// client ignore its own echo. Wire types mirror Semanticus.Engine/Protocol.cs (LayoutNode / LayoutChange).
// keep: the Diagram leaves this table where it is when it makes room for the lines (a put-back, or save_layout's keep).
export type LayoutNode = { ref?: string; name: string; lineageTag?: string; x: number; y: number; width?: number; height?: number; keep?: boolean };
export type LayoutData = { tables: LayoutNode[] };
export type LayoutChange = { origin: string; tables: LayoutNode[] };
export type WorkflowPositions = Record<string, { x: number; y: number }>;
export type WorkflowLayout = { name: string; revision: string; positions: WorkflowPositions };
const workflowLayoutListeners = new Set<(layout: WorkflowLayout) => void>();
export function onWorkflowLayoutChange(fn: (layout: WorkflowLayout) => void): () => void {
  workflowLayoutListeners.add(fn);
  return () => { workflowLayoutListeners.delete(fn); };
}
const layoutListeners = new Set<(v: LayoutChange) => void>();
export function onLayoutChange(fn: (v: LayoutChange) => void): () => void {
  layoutListeners.add(fn);
  return () => { layoutListeners.delete(fn); };
}

// A workflow RUN transition broadcasts separately (workflow/didChange) so the Workflows tab tracks a live run
// as it advances — driven from EITHER door (the human in Studio, or the user's Claude over MCP), on one run.
// Payload = WorkflowRunView (camelCase, mirrors Semanticus.Engine/Workflow.cs).
const workflowRunListeners = new Set<(v: unknown) => void>();
export function onWorkflowChange(fn: (v: unknown) => void): () => void {
  workflowRunListeners.add(fn);
  return () => { workflowRunListeners.delete(fn); };
}

// The workflow LIBRARY broadcasts separately (workflow/libraryDidChange) so the Workflows rail live-updates when a
// workflow file is saved/deleted (by the human OR the user's Claude via save_workflow). Payload = WorkflowInfo[].
const workflowLibraryListeners = new Set<(v: unknown) => void>();
export function onWorkflowLibraryChange(fn: (v: unknown) => void): () => void {
  workflowLibraryListeners.add(fn);
  return () => { workflowLibraryListeners.delete(fn); };
}

// A NON-mutating run by the agent (model/activity) — run_dax / profile_dax / evaluate_and_log / … — so Studio
// reflects what Claude is doing live (the read counterpart of onDidChange). Carries a token-light result preview.
export type ActivityEvent = {
  seq: number; kind: string; origin: string; label?: string; query?: string; target?: string;
  ok: boolean; error?: string; approvalId?: string; rowCount?: number; elapsedMs?: number; result?: unknown;
};
const activityListeners = new Set<(e: ActivityEvent) => void>();
export function onActivity(fn: (e: ActivityEvent) => void): () => void {
  activityListeners.add(fn);
  return () => { activityListeners.delete(fn); };
}

// The host fires 'reconnected' when the engine reconnects OR a new model is opened (incl. a unified open from
// the VS Code source picker). Studio re-reads sessionInfo on this so the connection bar reflects the SAME model
// the tree now shows — never a stale/different one.
// The host fires 'entitlementChanged' when a licence change was swapped into the RUNNING engine (F-242): the
// connection is the same one, so nothing reconnects, and the tier cache would otherwise keep the old grant.
const entitlementListeners = new Set<() => void>();
export function onEntitlementChange(fn: () => void): () => void {
  entitlementListeners.add(fn);
  return () => { entitlementListeners.delete(fn); };
}
/** Whose swap a 'reconnected' is. 'otherDoor' is one the person did not make in this window (their assistant, or
 *  another window). 'restore' is the person's own History restore, which reopens the same model. Absent means the
 *  person's own open, a reload, or an engine restart. */
export type ReconnectBy = 'person' | 'otherDoor' | 'restore';
const reconnectListeners = new Set<(by: ReconnectBy) => void>();
export function onReconnect(fn: (by: ReconnectBy) => void): () => void {
  reconnectListeners.add(fn);
  return () => { reconnectListeners.delete(fn); };
}

// The host asks the webview to navigate to a tab (optionally selecting a target object) — e.g. a Model-tree
// right-click "Preview data" jumps to the Data tab and previews that table. `addTables` carries the tables a
// "Add to Studio Diagram" right-click should drop onto the Diagram canvas. Host→webview, not an engine RPC.
export type NavigateMessage = { tab: string; target?: string; addTables?: string[]; sessionId?: string };
const navigateListeners = new Set<(m: NavigateMessage) => void>();
export function onNavigate(fn: (m: NavigateMessage) => void): () => void {
  navigateListeners.add(fn);
  return () => { navigateListeners.delete(fn); };
}

// Navigate Studio from inside Studio, through the same listeners a host 'navigate' reaches. Every Unlock Pro uses
// it to open the Semanticus Pro page: the button lives deep in a page that has no route setter of its own, and a
// round trip through the host would add nothing but a hop. Unstamped, it is read as the current session's. A caller
// that has just opened or created a model passes the NEW session's id, because Studio learns of that session only
// after the host's 'reconnected' and a fresh sessionInfo, and an unstamped move made before then belongs to the
// model being left (verifier H1: Connections' From a SQL source landed on the Plan).
export function navigateInStudio(tab: string, target?: string, sessionId?: string): void {
  const m: NavigateMessage = { tab };
  if (target !== undefined) m.target = target;
  if (sessionId) m.sessionId = sessionId;
  dispatchNavigate(m);
}
/** Hand a navigation to Studio's listeners as if the host had sent it. Studio replays a held one through this. */
export function dispatchNavigate(m: NavigateMessage): void {
  navigateListeners.forEach((l) => { try { l(m); } catch { /* isolate a view's handler */ } });
}

// The Semanticus Pro page. The host runs every licence flow (checkout, the wait, the key, the portal) and sends
// the page where it stands as 'proState'; the page sends back only the person's clicks. No price id, no token
// storage and no payment address ever live in the webview. The shape is propage-model.ts's ProPageState.
export type ProPageStateMessage = import('./propage-model').ProPageState;
const proStateListeners = new Set<(state: ProPageStateMessage) => void>();
export function onProState(fn: (state: ProPageStateMessage) => void): () => void {
  proStateListeners.add(fn);
  return () => { proStateListeners.delete(fn); };
}
export type ProAction = 'hello' | 'buy' | 'reopen' | 'cancel' | 'activate' | 'manage' | 'remove';
export function postProAction(action: ProAction, extra?: { plan?: string; token?: string }): void {
  postToHostStamped({ type: 'pro', action, ...(extra ?? {}) });
}

// Native tree selection is context, not a navigation command. Model home may show actions for it while every
// mounted tool retains its draft and query state.
export type TreeSelectionMessage = { ref?: string; sessionId?: string };
const treeSelectionListeners = new Set<(selection: TreeSelectionMessage) => void>();
export function onTreeSelection(fn: (selection: TreeSelectionMessage) => void): () => void {
  treeSelectionListeners.add(fn);
  return () => { treeSelectionListeners.delete(fn); };
}

// The host asks the webview to open the Connections hub — e.g. the tree "Open Model" / "Manage Connections", or the
// quick pick's "Add published model". Host→webview, so both doors open the SAME component. An optional section names
// the hub view to land on (open / setup / accounts / history / add).
const openConnectionsListeners = new Set<(section?: string) => void>();
export function onOpenConnections(fn: (section?: string) => void): () => void {
  openConnectionsListeners.add(fn);
  return () => { openConnectionsListeners.delete(fn); };
}

// The host fires 'connectionChanged' when the CONNECTION STATE moves out-of-band from the webview's own RPCs: an
// MCP-door connect/disconnect (relayed from model/activity), or a reference-model set/clear the host just completed.
// Studio re-reads its connection context on this so ConnectBar + the Connections drawer never show a stale panel.
// Distinct from 'reconnected' (which also rejects in-flight requests on a real connection loss) — this is a light
// "re-read your connection state" nudge with no request-rejection side effect.
const connectionChangeListeners = new Set<() => void>();
export function onConnectionChange(fn: () => void): () => void {
  connectionChangeListeners.add(fn);
  return () => { connectionChangeListeners.delete(fn); };
}

// Whether the host has an ENGINE at all. A different question from "is a model open" and from "is a live query
// connection attached", and Studio could not tell them apart: with no engine every request fails, sessionInfo comes
// back empty, and the page fell through to the same "Open a model to begin" placeholder whose only button ALSO needs
// the engine. The host announces both transitions and answers this on 'studioReady', so a panel that mounts while
// the engine is down learns the truth instead of inferring it from a rejection.
const engineStateListeners = new Set<(connected: boolean) => void>();
export function onEngineState(fn: (connected: boolean) => void): () => void {
  engineStateListeners.add(fn);
  return () => { engineStateListeners.delete(fn); };
}

// A remembered published model that needs the person to sign in before it can open (a cold start). The host names it
// ("Contoso") or sends null once it is open or gone, and answers this on studioReady like engineState, so a page that
// mounts later still knows. Studio then shows that model with Sign in to reconnect instead of "Open a model to begin".
const reconnectPendingListeners = new Set<(name: string | null) => void>();
export function onReconnectPending(fn: (name: string | null) => void): () => void {
  reconnectPendingListeners.add(fn);
  return () => { reconnectPendingListeners.delete(fn); };
}

// Announce a registry change made through THIS webview's own door. The drawer's "Add a published model" runs
// connect_xmla, which the engine remembers but does NOT surface as model/activity — so the host's 'connectionChanged'
// relay never fires for it, and a mounted sibling (the Compare picker; the hub next) would go stale until a remount.
// Firing the SAME listeners locally keeps ONE signal every connection consumer already subscribes to, rather than a
// compare-local reload or a drawer-close proxy. Host-relayed changes (either door's remember/label/forget, which DO
// emit activity) still arrive via the 'connectionChanged' message handled below — the two paths never overlap.
export function announceConnectionChange(): void {
  connectionChangeListeners.forEach((l) => { try { l(); } catch { /* isolate a view's handler */ } });
}

// Studio text size. The level is a percentage the user sets with Ctrl+wheel, Ctrl+0 or Help > Text size, and the
// host keeps it in workspaceState so the same project opens at the same size next time. The host answers
// 'studioReady' with the stored level, which is why this is a subscription rather than an RPC: there is nothing to
// wait for, and a webview that never hears back simply stays at 100%.
const studioZoomListeners = new Set<(level: number) => void>();
export function onStudioZoom(fn: (level: number) => void): () => void {
  studioZoomListeners.add(fn);
  return () => { studioZoomListeners.delete(fn); };
}
export function postStudioZoom(level: number): void {
  postToHostStamped({ type: 'setStudioZoom', level });
}

// Unsaved Author work (workflowheld.mjs) is also kept by the host for the workspace, because closing the Studio tab drops
// this page's own state and a panel cannot ask before it closes (verifier on m11/sol-fixes-1 round 2). The page hands
// every change over; the host gives its copy to the next Studio page it opens, as window.__semanticusHeldWorkflows.
export function postToHost(message: { type: 'workflowHeld' | 'draftsHeld'; value: unknown }): void {
  postToHostStamped(message);
}
/** Every typed, unsaved draft of Studio (drafts.ts) is kept by the host the same way; a Studio page opened afresh gets it as window.__semanticusHeldDrafts. */
export function keptDraftsByTheExtension<T>(): T | null {
  const kept = (window as unknown as { __semanticusHeldDrafts?: unknown }).__semanticusHeldDrafts;
  return kept && typeof kept === 'object' ? kept as T : null;
}
export function keptByTheExtension<T>(): T | null {
  const kept = (window as unknown as { __semanticusHeldWorkflows?: unknown }).__semanticusHeldWorkflows;
  return kept && typeof kept === 'object' ? kept as T : null;
}

// The Plan's tour and What changed card remember that they have been seen in the host's context.globalState
// (Plan-heart contract 10), so a person who closed the tour in one project never gets it again in another. The
// Plan asks once when it mounts. An answer that never comes (a host older than this, a view closing) settles to
// null, and null means show nothing: guidance that cannot remember being dismissed must not appear at all.
export type GuideFlags = { planTour: boolean; whatChanged: boolean };
const guideFlagsPending = new Map<number, (flags: GuideFlags | null) => void>();
const GUIDE_FLAGS_WAIT_MS = 5_000;
export function requestGuideFlags(cardId: string): Promise<GuideFlags | null> {
  const id = ++seq;
  return new Promise((resolve) => {
    const timer = window.setTimeout(() => { if (guideFlagsPending.delete(id)) resolve(null); }, GUIDE_FLAGS_WAIT_MS);
    guideFlagsPending.set(id, (flags) => { window.clearTimeout(timer); resolve(flags); });
    postToHostStamped({ type: 'getGuideFlags', id, cardId });
  });
}
export function setGuideFlag(flag: 'planTour' | 'whatChanged', cardId?: string): void {
  postToHostStamped({ type: 'setGuideFlag', flag, cardId });
}

// Whether the Pro permission card may ask about model `scope` now. The host keeps that memory for the window's run
// (src/consentAsks.ts), because this page is rebuilt each time Studio is closed and reopened. An answer that never
// comes settles to no: a card that cannot be remembered as asked must not interrupt anyone.
const consentAskPending = new Map<number, (ask: boolean) => void>();
const CONSENT_ASK_WAIT_MS = 5_000;
export function claimConsentAsk(scope: string): Promise<boolean> {
  const id = ++seq;
  return new Promise((resolve) => {
    const timer = window.setTimeout(() => { if (consentAskPending.delete(id)) resolve(false); }, CONSENT_ASK_WAIT_MS);
    consentAskPending.set(id, (ask) => { window.clearTimeout(timer); resolve(ask); });
    postToHostStamped({ type: 'claimConsentAsk', id, scope });
  });
}

// Resolvers for in-flight requestDropTables() calls (host hands back the Model-tree drag stash on drop).
const dropPending = new Map<number, (tables: string[]) => void>();
const folderPending = new Map<number, (folder: string | null) => void>();
const specFilePending = new Map<number, (path: string | null) => void>();
const reportPathsPending = new Map<number, (paths: string[] | null) => void>();
const savePending = new Map<number, (saved: boolean) => void>();

/** Commit the Studio field the person is typing in, as clicking elsewhere would, and resolve once the writes that sets off have
 *  landed. A save asks for this first (Ctrl+S from the host): fields that commit on blur never saw the keystroke the save
 *  came from, so the model was saved with the word still only on screen. */
export async function commitFocusedStudioField(): Promise<void> {
  const el = document.activeElement as HTMLElement | null;
  if (!el || el === document.body || !el.matches?.('input, textarea, select, [contenteditable="true"]')) return;
  const before = new Set(pending.keys());
  el.blur();   // the field's own blur handler runs, so it commits exactly as it does when the person tabs away
  await new Promise<void>((done) => window.setTimeout(done, 0));   // a handler that awaits before it writes gets its turn
  const landed: Promise<void>[] = [];
  for (const [id, p] of pending) {
    if (before.has(id)) continue;
    landed.push(new Promise<void>((done) => {
      const { resolve, reject } = p;
      p.resolve = (v) => { resolve(v); done(); };
      p.reject = (err) => { reject(err); done(); };
    }));
  }
  await Promise.all(landed);
}

window.addEventListener('message', (e: MessageEvent) => {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;
  if (msg.type === 'commitFocused') {
    void commitFocusedStudioField().catch(() => undefined).then(() => postToHostStamped({ type: 'focusedCommitted', id: msg.id }));
    return;
  }
  if (msg.type === 'dropTables') {
    const r = dropPending.get(msg.id);
    if (r) { dropPending.delete(msg.id); r((msg.tables as string[]) ?? []); }
    return;
  }
  if (msg.type === 'workingCopyFolder') {
    const r = folderPending.get(msg.id);
    if (r) { folderPending.delete(msg.id); r(typeof msg.folder === 'string' ? msg.folder : null); }
    return;
  }
  if (msg.type === 'specFile') {
    const r = specFilePending.get(msg.id);
    if (r) { specFilePending.delete(msg.id); r(typeof msg.path === 'string' ? msg.path : null); }
    return;
  }
  if (msg.type === 'guideFlags') {
    const r = guideFlagsPending.get(msg.id);
    if (r) { guideFlagsPending.delete(msg.id); r(msg.unavailable ? null : { planTour: msg.planTour === true, whatChanged: msg.whatChanged === true }); }
    return;
  }
  if (msg.type === 'consentAsk') {
    const r = consentAskPending.get(msg.id);
    if (r) { consentAskPending.delete(msg.id); r(msg.ask === true); }
    return;
  }
  if (msg.type === 'saveModelResult') {
    const r = savePending.get(msg.id);
    if (r) { savePending.delete(msg.id); r(msg.saved === true); }
    return;
  }
  if (msg.type === 'reportPaths') {
    const r = reportPathsPending.get(msg.id);
    if (r) { reportPathsPending.delete(msg.id); r(Array.isArray(msg.paths) ? (msg.paths as string[]) : null); }
    return;
  }
  if (msg.type === 'rpcResult') {
    const p = pending.get(msg.id);
    if (p) {
      pending.delete(msg.id);
      window.clearTimeout(p.timer);   // reply arrived — cancel the hang-guard timeout
      // Never the engine's raw text for a missing method: an older host beside a newer page can still send it.
      if (msg.error) p.reject(new Error(studioErrorText(msg.error)));
      else p.resolve(msg.result);
    }
  } else if (msg.type === 'didChange') {
    const n = msg.payload as ChangeNotification;
    listeners.forEach((l) => { try { l(n); } catch { /* a view's handler threw; isolate it */ } });
  } else if (msg.type === 'planDidChange') {
    planListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'planRunProgress') {
    planRunListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'startPlanDidChange') {
    startPlanListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'draftDidChange') {
    draftListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'progressDidChange') {
    const v = msg.payload as OperationProgress;
    progressListeners.forEach((l) => { try { l(v); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'specDidChange') {
    specListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'layoutDidChange') {
    const v = msg.payload as LayoutChange;
    layoutListeners.forEach((l) => { try { l(v); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'workflowLayoutDidChange') {
    workflowLayoutListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'workflowDidChange') {
    workflowRunListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'workflowLibraryDidChange') {
    workflowLibraryListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'activity') {
    const e = msg.payload as ActivityEvent;
    activityListeners.forEach((l) => { try { l(e); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'navigate') {
    const m: NavigateMessage = { tab: msg.tab, target: msg.target, addTables: msg.addTables, sessionId: typeof msg.sessionId === 'string' ? msg.sessionId : undefined };
    navigateListeners.forEach((l) => { try { l(m); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'proState') {
    const state = msg.state as ProPageStateMessage;
    if (state && typeof state === 'object') proStateListeners.forEach((l) => { try { l(state); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'treeSelection') {
    const selection: TreeSelectionMessage = { ref: typeof msg.ref === 'string' ? msg.ref : undefined, sessionId: typeof msg.sessionId === 'string' ? msg.sessionId : undefined };
    treeSelectionListeners.forEach((l) => { try { l(selection); } catch { /* isolate a view */ } });
  } else if (msg.type === 'studioZoom') {
    const level = Number(msg.level);
    if (Number.isFinite(level)) studioZoomListeners.forEach((l) => { try { l(level); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'openConnections') {
    openConnectionsListeners.forEach((l) => { try { l(msg.section); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'connectionChanged') {
    connectionChangeListeners.forEach((l) => { try { l(); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'engineState') {
    const connected = !!msg.connected;
    engineStateListeners.forEach((l) => { try { l(connected); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'reconnectPending') {
    const name = typeof msg.name === 'string' && msg.name ? msg.name : null;
    reconnectPendingListeners.forEach((l) => { try { l(name); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'entitlementChanged') {
    entitlementListeners.forEach((l) => { try { l(); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'reconnected') {
    // The host swapped the engine connection (reconnect, or a new model opened). Requests in flight against the OLD
    // connection will never get an rpcResult, so abandon them, then let subscribers re-read. The rejection copy is
    // deliberately NOT "please retry": a rejected MUTATION may have already applied server-side before the connection
    // went away, so callers and users must verify before re-running (pairs with the engine's no-auto-retry rule).
    // NOTE: this also wires the previously-dead onReconnect() path — the host always posted 'reconnected', but the
    // webview never listened.
    rejectAll('Semanticus: the engine connection was lost while this request was in flight. The operation may still have completed. Verify its result before retrying.');
    const by: ReconnectBy = msg.by === 'otherDoor' || msg.by === 'restore' ? msg.by : 'person';
    reconnectListeners.forEach((l) => { try { l(by); } catch { /* isolate a view's handler */ } });
  }
});

// The webview is being torn down (panel closed or reloaded — it is retainContextWhenHidden, so this is a real close,
// not a tab-switch away): settle any in-flight promise so awaiting code unwinds cleanly instead of hanging.
window.addEventListener('pagehide', () => {
  rejectAll('Semanticus: the Studio view was closed.');
  for (const [, resolve] of folderPending) resolve(null);
  folderPending.clear();
  for (const [, resolve] of specFilePending) resolve(null);
  specFilePending.clear();
  for (const [, resolve] of reportPathsPending) resolve(null);
  reportPathsPending.clear();
  for (const [, resolve] of savePending) resolve(false);
  savePending.clear();
  for (const [, resolve] of guideFlagsPending) resolve(null);
  guideFlagsPending.clear();
  for (const [, resolve] of consentAskPending) resolve(false);
  consentAskPending.clear();
});

// Persisted webview state (survives hide/show + reloads — VS Code serializes it). A single state object is shared
// across the webview, so callers namespace by key. Used by the Diagram canvas to remember saved custom diagrams.
export function loadState<T>(key: string, fallback: T): T {
  try { const s = vscode.getState() as Record<string, unknown> | undefined; return (s && key in s ? (s[key] as T) : fallback); }
  catch { return fallback; }
}
export function saveState(key: string, value: unknown): void {
  try { const s = (vscode.getState() as Record<string, unknown>) ?? {}; vscode.setState({ ...s, [key]: value }); }
  catch { /* state unavailable — non-fatal */ }
}

// Ask the extension host to reveal + select an object in the Model tree (a webview→host command, not an
// engine RPC). The host resolves the ref and calls treeView.reveal.
export function revealInTree(ref: string): void {
  postToHostStamped({ type: 'revealInTree', ref });
}

// Selection bus, Studio → Properties: tell the host a Studio navigator focused/selected a model object so
// the Properties view repopulates for it exactly as a native Model-tree selection would. Never steals focus
// (unlike revealInTree): the host only re-pushes the grid; Studio keeps the keyboard. `ref` is the engine
// object ref the navigators already carry ('measure:Table/Name', 'column:Table/Name', 'table:Name', …).
//
// A mouse click on a row fires onFocus (focusSelectInProperties) AND then onClick (selectInProperties) for the
// SAME ref — one intent, two events. `pendingFocusRef` lets the click collapse that redundant second post. The
// token is one-shot AND self-expiring: a focus that is NOT followed by a click (keyboard focus, or the pointer
// moving on) must not leave the token set — otherwise it lingers across tab switches / model remounts and later
// swallows a legitimate click on the same ref. So the token clears on the click that consumes it, on a superseding
// focus, AND on a short timeout that outlasts a single mouse gesture (focus→click) but nothing longer.
let pendingFocusRef: string | null = null;
let pendingFocusTimer: number | null = null;
function clearPendingFocus(): void {
  pendingFocusRef = null;
  if (pendingFocusTimer !== null) { window.clearTimeout(pendingFocusTimer); pendingFocusTimer = null; }
}
export function selectInProperties(ref: string | null | undefined): void {
  if (!ref) return;
  const fromFocus = ref === pendingFocusRef;   // the mouse-focus that just fired already posted this exact ref
  clearPendingFocus();
  if (fromFocus) return;
  postToHostStamped({ type: 'selectObject', ref });
}

// Focus-driven selection: focusing an object row (keyboard tab, or the focus half of a mouse click) selects it
// in Properties WITHOUT running the row's primary action (insert DAX / preview / pick). This is the ratified
// "focusing an object row selects it" behavior — a keyboard user can walk rows inspecting each, and only
// Enter/Space triggers the primary action. ALWAYS posts (focus is a definite, never-stale intent to inspect);
// records the ref so the click that follows a mouse-focus doesn't double-post it.
export function focusSelectInProperties(ref: string | null | undefined): void {
  if (!ref) return;
  clearPendingFocus();          // supersede any prior, unconsumed focus token before arming a fresh one
  pendingFocusRef = ref;
  // Expire the token if no click follows THIS focus within one gesture: a keyboard walk (or a pointer that moved
  // on) leaves no click to consume it, and a stale token would later swallow a real click on the same ref.
  pendingFocusTimer = window.setTimeout(clearPendingFocus, 1000);
  postToHostStamped({ type: 'selectObject', ref });
}

// Move keyboard focus to the Model tree (Ctrl+Alt+T in Studio). Webview→host: only the host can move
// focus OUT of a webview (the workbench owns cross-surface focus), so this relays to semanticusModel.focus.
export function focusModelTree(): void {
  postToHostStamped({ type: 'focusModelTree' });
}

// Hand a workbench command to the host (D-149). Webview keydowns never reach VS Code's keybinding
// service on their own; the host allow-lists the few chords we forward (save, undo, redo, palette).
export function runHostCommand(command: string, ref?: string, sessionId?: string): void {
  postToHostStamped({ type: 'runCommand', command, ref, sessionId });
}

// Tell the host the Studio webview has mounted and its message listeners are live, so the host can flush a
// navigation it queued while opening the panel (e.g. a tree "Preview data" that opened Studio cold). Webview→host.
export function signalReady(): void {
  postToHostStamped({ type: 'studioReady' });
}

// Ask the host to save built documentation to a file (webview→host command, not an engine RPC). The content
// is fully rendered in the webview; the host just shows a Save dialog + writes it. Format picks the extension.
export function exportDoc(format: 'html' | 'markdown' | 'json', content: string, suggestedName: string,
  labels?: { saveLabel?: string; successLabel?: string }): void {
  postToHostStamped({ type: 'exportDoc', format, content, suggestedName, ...labels });
}

// Native folder selection is owned by the extension host. A local working copy is a real folder on disk, so the
// webview must never fake it with a text box or infer a path from the open model.
export function pickWorkingCopyFolder(): Promise<string | null> {
  const id = ++seq;
  return new Promise<string | null>((resolve) => {
    folderPending.set(id, resolve);
    postToHostStamped({ type: 'pickWorkingCopyFolder', id });
  });
}

// Spec files are durable project artifacts, so the extension host owns the native open/save picker while the
// engine remains the only reader/writer. This returns a path only; loadSpec/saveSpec still drive both doors.
export function pickSpecFile(mode: 'open' | 'save', suggestedName = 'model.spec.json'): Promise<string | null> {
  const id = ++seq;
  return new Promise<string | null>((resolve) => {
    specFilePending.set(id, resolve);
    postToHostStamped({ type: 'pickSpecFile', id, mode, suggestedName });
  });
}

// Save the open model the way Ctrl+S does: the host runs its save command (a model never saved asks where, as a
// folder or a file), and answers whether it saved. Webview to host, because only the host owns the save dialog.
export function saveModelFromPage(): Promise<boolean> {
  const id = ++seq;
  return new Promise<boolean>((resolve) => {
    savePending.set(id, resolve);
    postToHostStamped({ type: 'saveModel', id });   // the model this page shows: the host saves it as that person's action (join 11, J-D1)
  });
}

export function openLocalModel(): void {
  postToHostStamped({ type: 'openLocalModel' });
}

// The standalone Connections hub (a dedicated webview panel when Studio is closed) asks the host to dispose its own
// panel from the hub's close button. Webview→host: only the host owns the panel lifecycle. A no-op in the Studio
// overlay (that path calls closeConnections() instead), so the message is safe to post from either host.
export function closeConnectionsPanel(): void {
  postToHostStamped({ type: 'closePanel' });
}

// Native folder selection is owned by the extension host: local report analysis targets real folders on disk
// (a .pbip's *.Report folder, a definition folder, or a project root), so the webview must never fake the picker.
// Resolves the chosen folder paths (multi-select), or null if the user cancelled. The free-text input stays the
// power-user route (paste a .pbip file path or a ;-separated list) — this is the browse convenience beside it.
// Bounded: the host always answers (null on cancel/error), but if that reply is lost — host reload mid-dialog —
// the promise must not pin the caller's busy state forever, so a generous timeout rejects and cleans the slot
// (generous because the dialog legitimately sits open while the user hunts for the folder).
const PICK_REPORT_PATHS_TIMEOUT_MS = 120_000;
export function pickReportPaths(): Promise<string[] | null> {
  const id = ++seq;
  return new Promise<string[] | null>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      if (reportPathsPending.delete(id)) reject(new Error('Semanticus: the folder dialog did not respond. Close it if it is still open, then try again.'));
    }, PICK_REPORT_PATHS_TIMEOUT_MS);
    reportPathsPending.set(id, (paths) => { window.clearTimeout(timer); resolve(paths); });
    postToHostStamped({ type: 'pickReportPaths', id });
  });
}

// Point the Reference tree (the copy-from model) at a remembered published model — the drawer's "Use as reference"
// action. Webview→host: the host owns the reference model state (referenceRef) and the native Reference tree. Carries
// the tenant so a cross-tenant reference targets its own tenant, not the default one.
export function useAsReference(record: { endpoint: string; database?: string; authMode?: string; tenantId?: string }): void {
  postToHostStamped({ type: 'useAsReference', endpoint: record.endpoint, database: record.database, authMode: record.authMode, tenantId: record.tenantId });
}

// Print / save-as-PDF: window.print() is a no-op inside VS Code's webview host (Chromium suppresses the
// dialog), so the host writes the rendered HTML to a temp file and opens it in the SYSTEM browser — the
// user prints (or saves as PDF) there, with real page styling.
export function printDoc(content: string, suggestedName: string): void {
  postToHostStamped({ type: 'printDoc', content, suggestedName });
}

// Open an http(s) link in the user's browser. Links inside the sandboxed doc-preview iframe can't open
// popups (no allow-popups), so the parent routes them through the host (vscode.env.openExternal).
export function openExternal(url: string): void {
  postToHostStamped({ type: 'openExternal', url });
}

// Licensing always routes through the native command. The host reads the engine-owned account URL and applies
// the HTTPS guard; the sandboxed webview never owns or duplicates a checkout/customer-portal address.
export function manageLicense(): void {
  postToHostStamped({ type: 'manageLicense' });
}

export function showLicense(): void {
  postToHostStamped({ type: 'showLicense' });
}

// Ask the host which tables are being dragged from the Model tree (set at drag-start in handleDrag). A native tree
// drag's DataTransfer is not readable inside the webview iframe, so the diagram pulls the stash on drop. The host
// consumes (clears) it on read, so a second pull without a fresh drag returns []. Never rejects — resolves [] if no
// drag is in flight. (Bounded so a dropped reply can't leak a pending resolver forever.)
export function requestDropTables(): Promise<string[]> {
  const id = ++seq;
  return new Promise<string[]>((resolve) => {
    dropPending.set(id, resolve);
    postToHostStamped({ type: 'requestDropTables', id });
    window.setTimeout(() => { if (dropPending.delete(id)) resolve([]); }, 1500);
  });
}

export function rpc<T = unknown>(method: string, ...params: unknown[]): Promise<T> {
  const id = ++seq;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      if (pending.delete(id)) reject(new Error(`Semanticus: no response to "${method}" within ${Math.round(rpcTimeoutMs(method) / 1000)}s. The engine may be busy or disconnected. If this was a change it may still complete. Check the Semanticus output before retrying.`));
    }, rpcTimeoutMs(method));
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
    // Every request names the model this page is showing; the engine refuses a write whose page is not on the open model (pagemodel.ts). One place, so no editor can forget it.
    postToHostStamped({ type: 'rpc', id, method, params });
  });
}

/** Best-effort clipboard copy that works inside a VS Code webview (with a textarea fallback). */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok;
    } catch {
      return false;
    }
  }
}
