// Typed request/response + notification bridge between the Studio webview and the extension host.
// The host is the SOLE engine client; the webview reaches the engine only through host-relayed RPC.

const vscode = acquireVsCodeApi();

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void; timer: number };
const pending = new Map<number, Pending>();
let seq = 0;

// Per-request timeout so a wedged or vanished host can't leave a promise (and its UI spinner) hanging forever. Most
// methods are fast local TOM/metadata ops → 30s. Everything that leaves the process for real work gets a generous
// 10-min ceiling so the guard never rejects a genuine in-flight op: Fabric/cloud round-trips, LIVE QUERIES against
// the model (previewTable / runDax / runInterview / evaluateAndLog / explainValue / vertiPaqScan / verifyEquivalence),
// whole-suite Tests runs (runTests can probe many model and source-SQL targets),
// interactive-auth connections (connectLocal/connectXmla — a browser sign-in can take minutes), git network ops,
// cross-model opens (compareModels/applyDiff), the daxLib network family, and whole-model builds. Names verified
// against the actual bridge callers (grep of webview/src rpc sites); matched as a PREFIX of the exact method name.
// The host wire protocol is unchanged.
const RPC_DEFAULT_TIMEOUT_MS = 30_000;
const RPC_LONG_TIMEOUT_MS = 10 * 60_000;
const LONG_RPC = /^(deploy|cicd|analyze|listReports|listWorkspaces|listDataAgents|listDeploymentPipelines|getDataAgent|getPipelineStages|createDataAgent|updateDataAgent|publishDataAgent|deleteDataAgent|generateDataAgentConfig|autogenerateSpecFrom|buildModelFromSpec|previewDeploy|previewTable|runDax|runSql|runTests|runInterview|evaluateAndLog|explainValue|profileDax|benchmark|verifyEquivalence|refreshPartition|captureQueryPlan|optimizeMeasure|aiReadinessScanLive|vertiPaqScan|exportVpax|applySchemaUpdate|diffSchema|getSourceSchema|makeModelAiReady|proposePlan|fabricGit|connectLocal|connectXmla|openLive|openLocal|prepareWorkingCopy|gitPush|gitPull|gitClone|compareModels|applyDiff|daxLib)/;
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
export type LayoutNode = { ref?: string; name: string; lineageTag?: string; x: number; y: number; width?: number; height?: number };
export type LayoutData = { tables: LayoutNode[] };
export type LayoutChange = { origin: string; tables: LayoutNode[] };
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
const reconnectListeners = new Set<() => void>();
export function onReconnect(fn: () => void): () => void {
  reconnectListeners.add(fn);
  return () => { reconnectListeners.delete(fn); };
}

// The host asks the webview to navigate to a tab (optionally selecting a target object) — e.g. a Model-tree
// right-click "Preview data" jumps to the Data tab and previews that table. `addTables` carries the tables a
// "Add to Studio Diagram" right-click should drop onto the Diagram canvas. Host→webview, not an engine RPC.
export type NavigateMessage = { tab: string; target?: string; addTables?: string[] };
const navigateListeners = new Set<(m: NavigateMessage) => void>();
export function onNavigate(fn: (m: NavigateMessage) => void): () => void {
  navigateListeners.add(fn);
  return () => { navigateListeners.delete(fn); };
}

// Resolvers for in-flight requestDropTables() calls (host hands back the Model-tree drag stash on drop).
const dropPending = new Map<number, (tables: string[]) => void>();
const folderPending = new Map<number, (folder: string | null) => void>();
const specFilePending = new Map<number, (path: string | null) => void>();

window.addEventListener('message', (e: MessageEvent) => {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;
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
  if (msg.type === 'rpcResult') {
    const p = pending.get(msg.id);
    if (p) {
      pending.delete(msg.id);
      window.clearTimeout(p.timer);   // reply arrived — cancel the hang-guard timeout
      if (msg.error) p.reject(new Error(msg.error));
      else p.resolve(msg.result);
    }
  } else if (msg.type === 'didChange') {
    const n = msg.payload as ChangeNotification;
    listeners.forEach((l) => { try { l(n); } catch { /* a view's handler threw; isolate it */ } });
  } else if (msg.type === 'planDidChange') {
    planListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'specDidChange') {
    specListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'layoutDidChange') {
    const v = msg.payload as LayoutChange;
    layoutListeners.forEach((l) => { try { l(v); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'workflowDidChange') {
    workflowRunListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'workflowLibraryDidChange') {
    workflowLibraryListeners.forEach((l) => { try { l(msg.payload); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'activity') {
    const e = msg.payload as ActivityEvent;
    activityListeners.forEach((l) => { try { l(e); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'navigate') {
    const m: NavigateMessage = { tab: msg.tab, target: msg.target, addTables: msg.addTables };
    navigateListeners.forEach((l) => { try { l(m); } catch { /* isolate a view's handler */ } });
  } else if (msg.type === 'reconnected') {
    // The host swapped the engine connection (reconnect, or a new model opened). Requests in flight against the OLD
    // connection will never get an rpcResult, so abandon them, then let subscribers re-read. The rejection copy is
    // deliberately NOT "please retry": a rejected MUTATION may have already applied server-side before the connection
    // went away, so callers and users must verify before re-running (pairs with the engine's no-auto-retry rule).
    // NOTE: this also wires the previously-dead onReconnect() path — the host always posted 'reconnected', but the
    // webview never listened.
    rejectAll('Semanticus: the engine connection was lost while this request was in flight. The operation may still have completed. Verify its result before retrying.');
    reconnectListeners.forEach((l) => { try { l(); } catch { /* isolate a view's handler */ } });
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
  vscode.postMessage({ type: 'revealInTree', ref });
}

// Move keyboard focus to the Model tree (Ctrl+Alt+T in Studio). Webview→host: only the host can move
// focus OUT of a webview (the workbench owns cross-surface focus), so this relays to semanticusModel.focus.
export function focusModelTree(): void {
  vscode.postMessage({ type: 'focusModelTree' });
}

// Tell the host the Studio webview has mounted and its message listeners are live, so the host can flush a
// navigation it queued while opening the panel (e.g. a tree "Preview data" that opened Studio cold). Webview→host.
export function signalReady(): void {
  vscode.postMessage({ type: 'studioReady' });
}

// Ask the host to save built documentation to a file (webview→host command, not an engine RPC). The content
// is fully rendered in the webview; the host just shows a Save dialog + writes it. Format picks the extension.
export function exportDoc(format: 'html' | 'markdown' | 'json', content: string, suggestedName: string,
  labels?: { saveLabel?: string; successLabel?: string }): void {
  vscode.postMessage({ type: 'exportDoc', format, content, suggestedName, ...labels });
}

// Native folder selection is owned by the extension host. A local working copy is a real folder on disk, so the
// webview must never fake it with a text box or infer a path from the open model.
export function pickWorkingCopyFolder(): Promise<string | null> {
  const id = ++seq;
  return new Promise<string | null>((resolve) => {
    folderPending.set(id, resolve);
    vscode.postMessage({ type: 'pickWorkingCopyFolder', id });
  });
}

// Spec files are durable project artifacts, so the extension host owns the native open/save picker while the
// engine remains the only reader/writer. This returns a path only; loadSpec/saveSpec still drive both doors.
export function pickSpecFile(mode: 'open' | 'save', suggestedName = 'model.spec.json'): Promise<string | null> {
  const id = ++seq;
  return new Promise<string | null>((resolve) => {
    specFilePending.set(id, resolve);
    vscode.postMessage({ type: 'pickSpecFile', id, mode, suggestedName });
  });
}

export function openLocalModel(): void {
  vscode.postMessage({ type: 'openLocalModel' });
}

// Print / save-as-PDF: window.print() is a no-op inside VS Code's webview host (Chromium suppresses the
// dialog), so the host writes the rendered HTML to a temp file and opens it in the SYSTEM browser — the
// user prints (or saves as PDF) there, with real page styling.
export function printDoc(content: string, suggestedName: string): void {
  vscode.postMessage({ type: 'printDoc', content, suggestedName });
}

// Open an http(s) link in the user's browser. Links inside the sandboxed doc-preview iframe can't open
// popups (no allow-popups), so the parent routes them through the host (vscode.env.openExternal).
export function openExternal(url: string): void {
  vscode.postMessage({ type: 'openExternal', url });
}

// Licensing always routes through the native command. The host reads the engine-owned account URL and applies
// the HTTPS guard; the sandboxed webview never owns or duplicates a checkout/customer-portal address.
export function manageLicense(): void {
  vscode.postMessage({ type: 'manageLicense' });
}

// Ask the host which tables are being dragged from the Model tree (set at drag-start in handleDrag). A native tree
// drag's DataTransfer is not readable inside the webview iframe, so the diagram pulls the stash on drop. The host
// consumes (clears) it on read, so a second pull without a fresh drag returns []. Never rejects — resolves [] if no
// drag is in flight. (Bounded so a dropped reply can't leak a pending resolver forever.)
export function requestDropTables(): Promise<string[]> {
  const id = ++seq;
  return new Promise<string[]>((resolve) => {
    dropPending.set(id, resolve);
    vscode.postMessage({ type: 'requestDropTables', id });
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
    vscode.postMessage({ type: 'rpc', id, method, params });
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
