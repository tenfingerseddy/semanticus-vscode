// The rules behind the two Connections pickers (Power BI Desktop, and a workspace then a model), kept pure so
// test/connect-pickers.test.mjs runs them rather than reading them. The lists themselves are the engine's
// (listLocalInstances, listWorkspaces, listWorkspaceModels); nothing here decides what exists, only how it is
// named, ordered and turned into the connect the host already makes.

export interface LocalInstance { port: number; title?: string; dataSource?: string }
export interface FabricWorkspace { id: string; displayName: string; description?: string; type?: string; capacityId?: string }
export interface WorkspaceModel { id: string; displayName: string; description?: string; workspaceId?: string }

/** One list the engine was asked for: still coming, answered, or refused with the engine's own sentence. */
export type Load<T> = { kind: 'loading' } | { kind: 'ready'; items: T[] } | { kind: 'failed'; message: string };

/** The host's own answer when it holds no engine connection (src/extension.ts, and App.tsx's copy). It waits for a start
 *  that is running before it says this, so seeing it means the engine is still coming up or could not start. */
export const ENGINE_NOT_CONNECTED = 'Engine not connected.';
export const isEngineAway = (e: unknown): boolean =>
  (e instanceof Error ? e.message : typeof e === 'string' ? e : String((e as { message?: unknown })?.message ?? e ?? '')).replace(/^\s*Error:\s*/, '').trim() === ENGINE_NOT_CONNECTED;

/** Where the engine is, as a picker draws it. 'ok' draws the lists; 'starting' is a calm wait; 'failed' says why and offers the way on. */
export type EngineView = { state: 'ok' } | { state: 'starting' } | { state: 'failed'; reason?: string };

/** How long a browser sign-in may wait on the page. The engine itself waits two minutes for a window the person has closed, and
 *  nothing tells the page it was closed. A sign-in that finishes later is kept by the engine, so Try again then needs no window. */
export const SIGN_IN_PATIENCE_MS = 60_000;

/** The engine's own words for a sign-in that was closed, cancelled or timed out (XmlaAuthHint). They are the same page as our timeout. */
const SIGN_IN_STOPPED = /sign-?in (?:was )?(?:cancell?ed|timed out|did not finish)/i;

/** The engine's own stop words, as text: a sign-in that was closed, cancelled or timed out. */
export const signInStopped = (text: string): boolean => SIGN_IN_STOPPED.test(text);

/** The transport's words when the engine goes away under a request (bridge.ts). They are about plumbing, not about the person's sign-in. */
const ENGINE_LOST = /engine connection was lost/i;
export const SIGN_IN_LOST = 'Semanticus restarted while you signed in. Try again.';

/** The sentence for a sign-in that failed: a lost engine in plain words, anything else as the engine wrote it. */
export function signInSentence(e: unknown): string {
  const text = engineSentence(e);
  return ENGINE_LOST.test(text) ? SIGN_IN_LOST : text;
}

/** A sign-in read that ends: it answers, the person cancels, or the patience time passes. Closing the window answers nothing, so
 *  "unfinished" is the end of all three of those stops. Any other refusal rejects with the engine's own sentence. */
export function waitForSignIn<T>(work: Promise<T>, ms: number = SIGN_IN_PATIENCE_MS): { done: Promise<{ kind: 'done'; value: T } | { kind: 'unfinished' }>; cancel: () => void } {
  let stop: () => void = () => {};
  const done = new Promise<{ kind: 'done'; value: T } | { kind: 'unfinished' }>((resolve, reject) => {
    const timer = setTimeout(() => resolve({ kind: 'unfinished' }), ms);
    stop = () => { clearTimeout(timer); resolve({ kind: 'unfinished' }); };
    work.then(
      (value) => { clearTimeout(timer); resolve({ kind: 'done', value }); },
      (e) => { clearTimeout(timer); if (SIGN_IN_STOPPED.test(engineSentence(e))) resolve({ kind: 'unfinished' }); else reject(e); },
    );
  });
  return { done, cancel: () => stop() };
}

/** Restart engine stops whatever engine this folder names, and a sentence that says another window holds it already tells the person what
 *  to do. Two instructions that disagree are one too many, so that page offers Try again only. */
export const restartFits = (reason?: string): boolean => !/^Another VS Code window/i.test((reason ?? '').trim());

/** What a Connect hands the host: the same arguments the pasted-address form fed connectXmla, with nothing typed. */
export interface WorkspaceChoice {
  endpoint: string; database: string; workspaceName: string; modelName: string;
  authMode: string; tenantId: string | null; label: string;
}

// The environment labels a workspace model can carry. The same values and words as the hub's list; "Local" is left
// out because a workspace model is never local. An unlabelled model counts as test, so it uses the Development settings.
export const ENV_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Not set (treated as Development)' },
  { value: 'dev', label: 'Development' },
  { value: 'uat', label: 'UAT' },
  { value: 'prod', label: 'Production' },
  { value: 'local', label: 'Local' },
];
export const WORKSPACE_ENV_OPTIONS = ENV_OPTIONS.filter((o) => o.value !== 'local');

// A native dropdown opens as a light list in some themes, and an option draws in the page's text colour, so options that are not
// the picked one looked grey and disabled (UAT F2). Every option says its own colours, so one that works draws as enabled.
export const OPTION_STYLE = { color: 'var(--sem-fg)', background: 'var(--sem-surface)' } as const;

// The sign-in ways, in the words the hub already uses. Browser sign-in is the default and needs nothing set up.
export const SIGN_IN_WAYS: Array<{ value: string; label: string }> = [
  { value: 'interactive', label: 'Browser sign-in' },
  { value: 'azcli', label: 'Azure command line' },
  { value: 'serviceprincipal', label: 'Service identity' },
];
export const isAdvancedSignIn = (authMode: string): boolean => authMode === 'azcli' || authMode === 'serviceprincipal';

/**
 * The sign-in the next workspace pick starts on. One that already opened a model is reused, so a person who got in with a
 * service identity is not sent to a browser window the next time (round 2 UAT N13). The model in play wins; else the
 * published model opened most recently. A way the picker does not offer, or nothing known, starts at the browser.
 */
export function signInToReuse(
  records: ReadonlyArray<{ id: string; kind: string; authMode?: string | null; tenantId?: string | null; lastUsedUtc?: string | null }>,
  liveConnId?: string | null,
): { authMode: string; tenantId: string } {
  const published = records.filter((r) => r.kind === 'xmla');
  const live = liveConnId ? published.find((r) => r.id === liveConnId) : undefined;
  const recent = [...published].sort((x, y) => String(y.lastUsedUtc ?? '').localeCompare(String(x.lastUsedUtc ?? '')))[0];
  const pick = live ?? recent;
  const mode = String(pick?.authMode ?? '').trim().toLowerCase();
  const offered = SIGN_IN_WAYS.some((w) => w.value === mode);
  return { authMode: offered ? mode : 'interactive', tenantId: offered && isAdvancedSignIn(mode) ? (pick?.tenantId ?? '') : '' };
}

// Power BI Desktop's own folder for a running copy is named AnalysisServicesWorkspace_<id>. That is what the engine
// can read today, and it tells a person nothing, so a copy is named by its report only when the title is one.
const DESKTOP_FOLDER = /^AnalysisServicesWorkspace/i;
export function instanceName(inst: LocalInstance): string {
  const t = (inst.title || '').trim();
  return t && !DESKTOP_FOLDER.test(t) ? t : 'Power BI Desktop report';
}
export function instanceDetail(inst: LocalInstance): string {
  return inst.dataSource || `localhost:${inst.port}`;
}

const PERSONAL = (w: FabricWorkspace) => (w.type || '').toLowerCase() === 'personal';
export function workspaceName(w: FabricWorkspace): string {
  return PERSONAL(w) ? 'My workspace' : (w.displayName || w.id);
}
// The address a picked workspace becomes. It is the /myorg/<name> shape the engine's WorkspaceNameFromEndpoint and
// the hub's workspaceNameFromEndpoint read back, so the saved row is named after the workspace on both doors. Only
// the characters that would end the name early are escaped; spaces stay, as Power BI writes them.
const WORKSPACE_ROOT = 'powerbi://api.powerbi.com/v1.0/myorg/';
export function workspaceEndpoint(w: FabricWorkspace): string {
  if (PERSONAL(w)) return WORKSPACE_ROOT + 'me';
  return WORKSPACE_ROOT + (w.displayName || '').replace(/[%/?#]/g, (c) => encodeURIComponent(c));
}

const byName = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'base' });
export function sortWorkspaces(list: FabricWorkspace[]): FabricWorkspace[] {
  return [...list].sort((a, b) => (PERSONAL(a) === PERSONAL(b) ? byName(workspaceName(a), workspaceName(b)) : PERSONAL(a) ? -1 : 1));
}
export function sortModels(list: WorkspaceModel[]): WorkspaceModel[] {
  return [...list].sort((a, b) => byName(a.displayName || a.id, b.displayName || b.id));
}

/** The engine's sentence as the engine wrote it. Only the transport's "Error: " prefix is removed. */
export function engineSentence(e: unknown): string {
  const raw = e instanceof Error ? e.message : typeof e === 'string' ? e : String((e as { message?: unknown })?.message ?? e ?? '');
  const text = raw.replace(/^\s*Error:\s*/, '').trim();
  return text || 'It did not work, and no reason came back.';
}

export function workspaceChoice(w: FabricWorkspace, m: WorkspaceModel, label: string, authMode: string, tenant: string): WorkspaceChoice {
  return {
    endpoint: workspaceEndpoint(w),
    database: m.displayName,
    workspaceName: workspaceName(w),
    modelName: m.displayName,
    authMode,
    // A browser sign-in picks its tenant itself; a tenant typed for an advanced attempt must not ride along hidden.
    tenantId: isAdvancedSignIn(authMode) ? (tenant.trim() || null) : null,
    label,
  };
}

// The words, in one place so the test and the page agree.
export const COPY = {
  desktopTitle: 'Power BI Desktop',
  desktopLooking: 'Looking for Power BI Desktop...',
  desktopNone: 'No report is open in Power BI Desktop. Open one, then look again.',
  desktopIntro: 'Reports open in Power BI Desktop on this computer. Pick one to open it here, or to run tests and queries on it.',
  desktopAddress: 'Address',
  desktopAddressHint: 'For a copy this list cannot see, like localhost:51000.',
  workspaceTitle: 'Workspace model',
  workspaceLooking: 'Listing your workspaces...',
  signInWaiting: 'Waiting for the sign-in window...',
  signInUnfinished: 'Sign-in did not finish.',
  engineStarting: 'Starting Semanticus...',
  engineFailedFallback: 'Semanticus could not start. Try again, or restart the engine.',
  workspaceNone: 'This account cannot see any workspaces.',
  modelsLooking: 'Listing models...',
  pickWorkspace: 'Pick a workspace',
  noModels: (ws: string) => `${ws} has no models you can open.`,
  envLabel: 'Environment. It sets what your assistant may change.',
};
