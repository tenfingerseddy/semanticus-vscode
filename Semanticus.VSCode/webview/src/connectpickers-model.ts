// The rules behind the two Connections pickers (Power BI Desktop, and a workspace then a model), kept pure so
// test/connect-pickers.test.mjs runs them rather than reading them. The lists themselves are the engine's
// (listLocalInstances, listWorkspaces, listWorkspaceModels); nothing here decides what exists, only how it is
// named, ordered and turned into the connect the host already makes.

export interface LocalInstance { port: number; title?: string; dataSource?: string }
export interface FabricWorkspace { id: string; displayName: string; description?: string; type?: string; capacityId?: string }
export interface WorkspaceModel { id: string; displayName: string; description?: string; workspaceId?: string }

/** One list the engine was asked for: still coming, answered, or refused with the engine's own sentence. */
export type Load<T> = { kind: 'loading' } | { kind: 'ready'; items: T[] } | { kind: 'failed'; message: string };

/** What a Connect hands the host: the same arguments the pasted-address form fed connectXmla, with nothing typed. */
export interface WorkspaceChoice {
  endpoint: string; database: string; workspaceName: string; modelName: string;
  authMode: string; tenantId: string | null; label: string;
}

// The environment labels a workspace model can carry. The same values and words as the hub's list; "Local" is left
// out because a workspace model is never local. An unlabelled model counts as test, so it uses the Development settings.
export const ENV_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'No label (Development settings)' },
  { value: 'dev', label: 'Development' },
  { value: 'uat', label: 'UAT' },
  { value: 'prod', label: 'Production' },
  { value: 'local', label: 'Local' },
];
export const WORKSPACE_ENV_OPTIONS = ENV_OPTIONS.filter((o) => o.value !== 'local');

// The sign-in ways, in the words the hub already uses. Browser sign-in is the default and needs nothing set up.
export const SIGN_IN_WAYS: Array<{ value: string; label: string }> = [
  { value: 'interactive', label: 'Browser sign-in' },
  { value: 'azcli', label: 'Command-line sign-in' },
  { value: 'serviceprincipal', label: 'Service identity' },
];
export const isAdvancedSignIn = (authMode: string): boolean => authMode === 'azcli' || authMode === 'serviceprincipal';

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
  workspaceNone: 'This account cannot see any workspaces.',
  modelsLooking: 'Listing models...',
  pickWorkspace: 'Pick a workspace',
  noModels: (ws: string) => `${ws} has no models you can open.`,
  envLabel: 'Environment. It sets what your assistant may change.',
};
