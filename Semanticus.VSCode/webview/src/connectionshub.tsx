import { Fragment, lazy, Suspense, useEffect, useState, useRef, type ReactNode } from 'react';
import { openLocalModel, pickWorkingCopyFolder, rpc, onConnectionChange, onOpenConnections, announceConnectionChange, closeConnectionsPanel, navigateInStudio, runHostCommand } from './bridge';
import { planRankingOf } from './features';
import { useTier } from './pro';
import { useConnection, type ConnectionContextModel, type SessionInfo } from './connection';
import { AccountPicker } from './accountpicker';
import { defaultSignInMode } from './copy';
import { useSurfaceEscape } from './surface-escape';
import { ConsentCard } from './consent';
import { DraftMark, EditMovedBanner, sameValue, useEditBase } from './editbase';
import { draftModel, draftStore, heldDraft, useHeldDrafts } from './drafts';
import { setPageModelIdentity } from './pagemodel';
import { DesktopPicker, WorkspacePicker, type WorkspaceChoice } from './connectpickers';
import { ENV_OPTIONS } from './connectpickers-model';
import {
  type HubRecord, type HubContext, type HubPanel, type Job, type ModelEndpointList, type NotLinkedSource, type SqlEndpointInfo,
  SEARCH_FROM, defaultMoveCount, defaultMoveSentence, endpointFor, jobOptions, kindWord, removeConfirmSentence, routeSection, rowJobs, rowSecondLine, sideId, startFormStaysOpen,
} from './connectionshub-model';
import { BTN, BTN_COMPACT, BTN_PRIMARY, BTN_QUIET, ChangePanel, DefaultConfirm, JobPicker, NotLinkedList } from './connectionshub-parts';

// The Connections HUB, one page (slice C, lane C2; contract 19.1; design connections-minimal.html, Kane 24 Sep).
// It is ONE list of saved models, ONE account button, and a Jobs button for which model does Editing, Tests and
// queries, and Publish to (the Now strip that named them left in r1 min4: each row names its jobs). The five sections, the side rail and the three-way detail panel it replaced repeated the
// same account, the same "what is open now" and the same Open button in three or four places each.
//
// It is still the single manager surface: a floating dialog inside Studio, and the SAME component full-page in a
// dedicated webview when Studio is closed (standalone). All state moves through existing engine ops (listConnections /
// connectionContext / probeConnectionAccounts / listConnectionHistory / prepareWorkingCopy / setPublishDestination /
// labelConnection / forgetConnection / connectXmla / createModel / disconnect / listModelSqlEndpoints /
// setModelSqlEndpoint), so the assistant reaches every capability here through its own tools on the same session.
//
// The page does not decide which model may do which job. The engine lists each saved model with the reason it
// cannot answer tests and queries or be a publish destination (the same sentence it throws when asked), and a model
// with a reason is struck out with it, never hidden. connectionshub-model.ts holds that rule and a node test runs it.

type ConnectionRecord = HubRecord;

// One target's silently-probed account (mirrors Engine ConnectionAccountProbe). `account` is who the NEXT open signs in
// as — ONLY a live sign-in record, undefined when unknown; `previousAccount` is provenance ("last opened as <x>").
interface AccountProbe { id: string; account?: string; previousAccount?: string; tenantId?: string; }

// A saved Microsoft identity on this device (mirrors Engine AccountProfile) — the Phase 2 multi-account store. Holds no
// credential: `family` is the sign-in method, `isDefault` marks the account an unqualified open of that tenant uses, and
// `signedIn` is whether a usable saved sign-in still exists (a signed-out profile needs an interactive re-sign).
interface AccountProfile { id: string; username: string; tenantId?: string; family?: string; lastSignInUtc?: string; lastUseUtc?: string; isDefault?: boolean; signedIn?: boolean; }
interface FailedAuthAttempt { targetId: string; purpose: 'open' | 'query'; profileId?: string; username?: string; }

// The live auth-prerequisite probe for an advanced sign-in method (mirrors Engine AuthPrerequisites). Presence + NAMES
// only, NEVER a secret value. For serviceprincipal, `requirements` lists each needed input (Client ID / Client secret /
// Tenant) and whether the engine host can see it; for azcli, `cliAccount`/`cliTenant` report the detected `az login`
// session. `keyVaultSupported` is false today, stated plainly so the UI never implies in-app Key Vault resolution.
interface AuthPreqRequirement { label: string; envNames: string[]; present: boolean; }
interface AuthPrerequisites { mode: string; ready: boolean; requirements: AuthPreqRequirement[]; cliAccount?: string; cliTenant?: string; detail?: string; keyVaultSupported: boolean; }

// One device-local connection-timeline event (mirrors Engine ConnectionHistoryEvent). Holds no credential.
interface HistoryEvent { id?: string; kind: string; account?: string; endpoint?: string; database?: string; tenantId?: string; ok: boolean; detail?: string; whenUtc?: string; }
const historyKindLabel = (e: HistoryEvent): string => {
  switch (e.kind) {
    case 'connect': return e.ok ? 'Connected for queries' : 'Connect failed';
    case 'open': return e.ok ? 'Opened' : 'Open failed';
    case 'switch': return e.ok ? 'Switched account' : 'Account switch failed';
    case 'signin': return e.ok ? 'Signed in' : 'Sign-in failed';
    case 'role': return e.detail ? e.detail.charAt(0).toUpperCase() + e.detail.slice(1) : 'Role changed';
    default: return e.ok ? e.kind : `${e.kind} failed`;
  }
};

/** The History row's second line: who, what it was about, when. The "what" is left out when it only repeats the title. */
const historySubLine = (e: HistoryEvent): string => {
  const kind = historyKindLabel(e).trim().toLowerCase();
  const about = e.detail || e.database || short(e.endpoint);
  return [e.account, about && about.trim().toLowerCase() !== kind ? about : null, shortWhen(e.whenUtc)].filter(Boolean).join(' · ');
};
const shortWhen = (iso?: string): string => {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
};

// The credential family a tenant-wide account switch acts on: only interactive / device-code sign-ins keep a switchable
// record. azcli / serviceprincipal / token have no account picker, so account actions on them would be a false promise.
function credentialFamily(authMode?: string): 'interactive' | 'devicecode' | null {
  const m = (authMode || 'interactive').trim().toLowerCase();
  if (m === 'interactive' || m === 'entra' || m === 'entramfa' || m === 'mfa') return 'interactive';
  if (m === 'devicecode') return 'devicecode';
  return null;
}

// The extension relay sends RPC failures as `error: String(e?.message ?? e)` (extension.ts), so the webview receives
// the engine's message text, not a typed exception envelope. Keep this classifier aligned with the engine's narrow
// XmlaAuthHint.LooksLikeAuthFailure markers and its fixed teaching messages. Generic network, dataset and local-model
// errors must stay ordinary errors; words like "unauthorized" alone are too broad and can be a model/query error.
const AUTH_REQUIRED_MARKERS = [
  'authentication failed for all authenticators',
  'aadsts',
  'credentialunavailable',
  'interactive authentication is not supported',
  'interactive authentication is needed',
  'az login',
  'no accounts were found in the cache',
  'not signed in to this workspace',
  'saved account is signed out on this device',
  'sign in again to use it',
  'silent sign-in could not renew this connection',
  'the live sign-in expired',
];
const isAuthRequiredFailure = (message?: string | null): boolean => {
  const m = (message || '').toLowerCase();
  return AUTH_REQUIRED_MARKERS.some((marker) => m.includes(marker));
};
// Saved-account recovery only exists for account-switchable XMLA modes. An auth-shaped service-principal/token error
// still needs its real cause and retry target, but it cannot be repaired by sending the person to a Microsoft account list.
const isSwitchableXmla = (r: ConnectionRecord): boolean => r.kind === 'xmla' && credentialFamily(r.authMode) !== null;
// The plain-words cause line the failure notice OPENS with. The raw engine text ("Authentication failed for all
// authenticators. Technical Details: RootActivityId: ...") is diagnostic, not a cause a person can read, so it moves
// behind "Show details" and this line leads. It is built from the SAME markers isAuthRequiredFailure classifies on, so
// the sentence can never claim more than the classification proves; an unclassified failure returns null and the notice
// keeps the engine's own text as its cause.
function plainAuthCause(message?: string | null, account?: string): string | null {
  const m = (message || '').toLowerCase();
  if (!isAuthRequiredFailure(m)) return null;
  const who = account ? `for ${account}` : 'for this connection';
  if (m.includes('no accounts were found in the cache') || m.includes('not signed in to this workspace'))
    return `There is no saved sign-in ${who} on this computer.`;
  if (m.includes('az login') || m.includes('credentialunavailable'))
    return `The command-line sign-in ${who} is not available on this computer.`;
  if (m.includes('interactive authentication'))
    return `The saved sign-in ${who} cannot be renewed quietly. It needs a fresh sign-in.`;
  return `The saved sign-in ${who} no longer works.`;
}

interface WorkingCopyResult {
  sourceConnectionId?: string;
  sourceModelName?: string;
  publishConnectionId?: string;
  publishModelName?: string;
  queryConnectionId?: string;
  queryModelName?: string;
  queryKind?: string;
  targetFolder: string;
  action: 'create' | 'open';
  canCommit: boolean;
  commitRequested: boolean;
  opened: boolean;
  queryConnected: boolean;
  twoCopiesInPlay: boolean;
  summary: string;
  benefits: string[];
  conflicts: string[];
  nextAction?: string;
  error?: string;
}

interface WorkSetup {
  source: ConnectionRecord;
  parentFolder: string | null;
  queryId: string;
  publishId: string;
  plan: WorkingCopyResult | null;
}

// The names other surfaces still ask the hub to open on. 'setup', 'accounts' and 'sqlsources' were sections of the
// old five-section hub; on the one page they open the Change panel, the account menu and the open model's SQL
// endpoint (routeSection in connectionshub-model.ts), so Deploy, the Tests page and the host commands keep working.
// 'createsql' is the list with the start plan's own form open (From a SQL source): the SQL start when no model is open yet
// (verifier L3). The form asks for the source, the model name and an empty folder, and the engine's startModelFromSql makes
// the model itself, so there is no blank-model name step before it (the M11 wiring step).
// 'create' is the list with the New blank model form open: the welcome in the empty Model view starts there.
export type HubView = 'open' | 'setup' | 'accounts' | 'history' | 'add' | 'work' | 'sqlsources' | 'desktop' | 'workspace' | 'createsql' | 'create';
type MainView = 'open' | 'history' | 'add' | 'work' | 'desktop' | 'workspace';

// A caller inside Studio (e.g. Deploy's "Change publish destination") can ask the hub to open on a specific view
// next. The hub is mounted once in App with no initialView prop, so the request is a module-level flag set just
// before openConnections() flips `open`; the hub consumes and clears it on the open transition below.
let nextView: HubView | null = null;
export function openConnectionsOnView(view: HubView): void {
  nextView = view;
}

// The start plan's form, the one the Plan page hosts (startplan.tsx StartFromSql). Loaded when it is first shown: startplan
// reads its saved endpoints from this file, so a static import both ways would make the two modules load each other.
const StartFromSqlForm = lazy(() => import('./startplan').then((m) => ({ default: m.StartFromSql })));

/** The next time the hub opens, it opens "New blank model" (an empty model's Plan offers it, ModelPlanView.Starts). */
let nextCreate = false;
export function openConnectionsOnCreate(): void {
  nextView = 'open';
  nextCreate = true;
}

// ================================================================================================================
// SQL endpoints. A SQL source used to be a fourth role with its own section; now each model has one optional SQL
// endpoint, set from its row (contract 19.3). The saved record underneath is unchanged: a name, a server, a database
// and the NAME of a way to sign in. It holds NO credential; there is no password field on this form and there never
// will be. The engine refuses a connection-string shape at its boundary and this form refuses one before the call,
// so a pasted password never leaves the box it was typed in. Several models may share one saved record.
// ================================================================================================================

/** One saved SQL source, as both doors see it (mirrors Engine SqlSourceRecord). Exported: a check's source picker
 *  on the Tests page renders this same record rather than inventing a second shape for it. */
export interface SqlSourceRecord {
  id: string;
  name: string;
  server: string;
  database: string;
  /** A way to sign in, by NAME (interactive | devicecode | azcli | serviceprincipal). Never a token. */
  authMode?: string;
  tenantId?: string;
  createdUtc?: string;
  /** Absent = never tested. Kept distinct from "the last test failed", which is lastTestOk === false. */
  lastTestedUtc?: string;
  lastTestOk?: boolean;
}
/** The outcome of one Test connection (mirrors Engine SqlSourceTestResult). `note` is always plain words. */
interface SqlSourceTestResult { id?: string; name?: string; server?: string; database?: string; ok: boolean; note?: string; elapsedMs?: number; testedUtc?: string; }
/** A refused Remove (mirrors Engine SqlSourceDeleteResult). The engine returns the counts AND the names, both sorted,
 *  so a refusal can say what needs repair rather than only how much of it there is. */
interface SqlSourceDeleteResult { deleted: boolean; note?: string; checksUsing?: number; tableMappingsUsing?: number; checkTitles?: string[]; mappedTables?: string[]; linkedModels?: string[]; usingModels?: string[]; }
/** The endpoint form's own state. `id` null = a new saved record; an id = the saved record this model points at. */
interface SqlSourceForm { id: string | null; name: string; server: string; database: string; authMode: string; tenantId: string; }

// What the person typed into an endpoint form and has not saved outlives the hub (join 11, the drafts class, drafts.ts): closing it, a tab
// switch, a restarted engine and a reload keep it. A draft is held per row and per source (a saved one by its id, or 'new'), with the
// saved source it was typed over (a new one: the form as it opened). The hub opens the form again on it; a draft ends on a save the engine
// took, Cancel, and Use the new one.
type SqlFields = Omit<SqlSourceForm, 'id'>;
const sqlFieldsOfForm = (f: SqlSourceForm): SqlFields => ({ name: f.name, server: f.server, database: f.database, authMode: f.authMode, tenantId: f.tenantId });
const SQL_DRAFTS = 'hub|sql|';
const sqlDraftScope = (row: string, source: string | null) => SQL_DRAFTS + JSON.stringify([row, source ?? 'new']);
const sqlRowPrefix = (row: string) => SQL_DRAFTS + '[' + JSON.stringify(row) + ',';
/** A held endpoint form of the open model: on `row` when given, preferring the source `prefer` (null = a new one), else the first held. */
function heldSqlForm(row?: string, prefer?: string | null): { row: string; form: SqlSourceForm; base: SqlFields } | null {
  const m = draftModel();
  if (!m) return null;
  const found: { row: string; source: string }[] = [];
  for (const id of draftStore.held(m, SQL_DRAFTS)) {
    try {
      const [scope, field] = JSON.parse(id) as [string, string];
      const parts = JSON.parse(scope.slice(SQL_DRAFTS.length)) as unknown;
      if (field || !Array.isArray(parts) || parts.length !== 2 || typeof parts[0] !== 'string' || typeof parts[1] !== 'string') continue;
      if (!row || parts[0] === row) found.push({ row: parts[0], source: parts[1] });
    } catch { /* not one of these */ }
  }
  const pick = (prefer !== undefined ? found.find((x) => x.source === (prefer ?? 'new')) : undefined) ?? found[0];
  const source = pick ? (pick.source === 'new' ? null : pick.source) : null;
  const h = pick ? heldDraft<SqlFields>(sqlDraftScope(pick.row, source)) : undefined;
  return pick && h ? { row: pick.row, form: { id: source, ...h.value }, base: h.base } : null;
}

/**
 * What Save refuses BEFORE the engine is called, in the words of the box that is wrong. One complaint at a time, in
 * field order, so the person fixes the first empty box rather than reading three at once.
 *
 * The connection-string rule FAILS CLOSED and mirrors the engine's own boundary check: a bare address has no ';' and
 * no '=', so anything that does is a pasted connection string. We refuse it instead of trying to strip a password
 * out of it, and we refuse it here so the paste never reaches the wire at all.
 */
function sqlSourceFormError(form: SqlSourceForm): string | null {
  const name = (form.name || '').trim();
  const server = (form.server || '').trim();
  const database = (form.database || '').trim();
  if (!name) return 'Give this endpoint a name, so you can recognise it later.';
  if (!server) return 'Type the server address, for example contoso-sql.database.windows.net.';
  if (!database) return 'Type the database name.';
  if (/[;=]/.test(server) || /[;=]/.test(database))
    return 'Type just the server address and the database name, not a connection string. Semanticus signs you in and never keeps a password.';
  return null;
}

/**
 * The last-test line. "Never tested" is a THIRD state and stays one: a source nobody has tested has not passed, and
 * saying so is the difference between evidence and a guess. `whenText` is already-formatted words, so this stays pure
 * and the contract test can run it.
 */
function sqlSourceTestLine(lastTestOk: boolean | null | undefined, whenText: string): string {
  if (lastTestOk === null || lastTestOk === undefined) return 'Not tested yet';
  if (lastTestOk) return whenText ? `Last test worked on ${whenText}` : 'Last test worked';
  return whenText ? `Last test failed on ${whenText}` : 'Last test failed';
}

/**
 * The names behind a refusal's count. Where the engine named fewer than it counted, say how many are missing rather
 * than showing a short list as though it were the whole list.
 */
function sqlSourceUsedByNames(names: string[], count: number): string {
  const listed = (names || []).map((n) => (n || '').trim()).filter((n) => n.length > 0);
  const extra = Math.max(0, count - listed.length);
  if (listed.length === 0) return extra > 0 ? `${extra} not named here` : '';
  if (extra === 0) return listed.join(', ');
  return `${listed.join(', ')}, and ${extra} more`;
}

/**
 * The Tests page's way in: "Add a SQL source..." at the end of a check's source list, and "Manage SQL sources".
 * They land on the open model's SQL endpoint, which is where a check's source now comes from.
 */
export function openSqlSources(openConnections: () => void): void {
  openConnectionsOnView('sqlsources');
  openConnections();
}

/** The saved SQL sources, for a check's source picker on the Tests page. One engine op, no second store. */
export function listSqlSources(): Promise<SqlSourceRecord[]> {
  return rpc<SqlSourceRecord[]>('listSqlSources');
}

// ---- naming a remembered connection ---------------------------------------------------------------------------
// A remembered connection reads as WORDS, never as an address. Kane's list titled a published model
// "Contoso%20Fabric%20Monitoring" (live test, 2026-09-15; Contoso stands in for the real tenant name, which the
// public-mirror gate refuses to carry): a WORKSPACE-level record with no dataset, so the name fell
// through to the encoded last path segment. These are the ONE place a name is decided, and they mirror the engine's
// ConnectionRegistry.WorkspaceNameFromEndpoint rule for rule so a target is one string on both doors. Kept as
// top-level pure functions so the contract test can extract and actually RUN them.
function short(value?: string): string {
  const v = (value || '').replace(/[\\/]+$/, '');
  if (!v) return '';
  const i = Math.max(v.lastIndexOf('/'), v.lastIndexOf('\\'));
  return i >= 0 ? v.slice(i + 1) : v;
}
// Turn stored escapes into words for text we have ALREADY decided to show. Malformed escapes hand back exactly what
// was stored: showing "Bad%ZZ" is poor, but dropping the text entirely is worse, and this half never names anything
// on its own — it only makes readable something the caller had already chosen to display.
function decodeSegment(value?: string | null): string {
  const v = value || '';
  try { return decodeURIComponent(v); } catch { return v; }
}
// The workspace an XMLA endpoint points at, in words. Anchored to a TRAILING /myorg/<name>, so a deeper path is never
// mistaken for the workspace, and malformed percent-encoding yields NOTHING rather than the raw segment: here the
// result becomes a name, and a garbled name is the defect itself rather than a milder version of it.
function workspaceNameFromEndpoint(endpoint?: string | null): string | null {
  if (!endpoint) return null;
  let trimmed = endpoint.trim();
  while (trimmed.endsWith('/')) trimmed = trimmed.slice(0, -1);
  const marker = '/myorg/';
  const at = trimmed.toLowerCase().lastIndexOf(marker);
  if (at < 0) return null;
  const segment = trimmed.slice(at + marker.length);
  if (!segment || segment.includes('/') || segment.includes('?') || segment.includes('#')) return null;
  try { return decodeURIComponent(segment); } catch { return null; }
}
// A published record with no dataset is a WORKSPACE, not a model: Open there means choosing a model first. The engine
// records the dataset name once a live open resolves one, so a record stops being workspace-only by itself.
function isWorkspaceOnly(r: ConnectionRecord): boolean {
  return r.kind === 'xmla' && !(r.database || '').trim();
}
// Power BI Desktop names a running local model's database with a bare GUID, which tells a person nothing. Detect it so
// a local row falls back to an honest friendly name (the Desktop model/file name, else "Local running model") and never
// shows the GUID as the display name (P5).
function isGuidName(s?: string): boolean {
  return !!s && /^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$/i.test(s.trim());
}
function nameOf(r: ConnectionRecord): string {
  if (r.kind === 'file') return r.modelName || decodeSegment(short(r.endpoint)) || 'Local model';
  if (r.kind === 'localDesktop') return r.modelName || (isGuidName(r.database) ? '' : (r.database || '')) || 'Local running model';
  return r.modelName || r.database || workspaceNameFromEndpoint(r.endpoint) || decodeSegment(short(r.endpoint)) || 'Model';
}
// Where a saved model lives, the first part of its row's second line. A workspace-level record says what it is,
// because Open there means picking a model first.
function whereOf(r: ConnectionRecord): string {
  if (r.kind === 'file') return endpointText(r.endpoint);
  if (r.kind === 'localDesktop') return `Power BI Desktop, ${decodeSegment(short(r.endpoint)) || 'running on this computer'}`;
  if (isWorkspaceOnly(r)) return 'Workspace, you pick the model when you open it';
  const workspace = workspaceNameFromEndpoint(r.endpoint);
  return workspace ? `In ${workspace}` : 'Workspace model';
}
// An endpoint shown as DETAIL is text for a person to read, not a string to copy, so it is decoded too.
function endpointText(endpoint?: string | null): string {
  return decodeSegment(endpoint || '');
}

// An UNLABELLED cloud model reads "No label (Development settings)": with no label it counts as test (Kane, 10 Oct 2026), and that is a
// different fact from a declared Development label. Permissions says it the same way, so the one fact has one wording. A label typed
// into the saved file by hand that is not one of the four ("Production", "staging") reads Production, because the engine applies prod to it.
function environment(r: ConnectionRecord): { text: string; tone: 'accent' | 'warn' | 'muted' } {
  const l = (r.label || '').trim().toLowerCase();   // the engine's own reading (EffectiveLabel): trimmed, any case
  if (r.kind === 'localDesktop' || r.kind === 'file' || l === 'local') return { text: 'Local', tone: 'muted' };
  if (l === 'dev') return { text: 'Development', tone: 'accent' };
  if (l === 'uat') return { text: 'UAT', tone: 'accent' };
  if (l) return { text: 'Production', tone: 'warn' };
  return { text: 'No label (Development settings)', tone: 'muted' };
}
// Who the NEXT open of a target signs in as — ONLY a live sign-in record (probe.account); undefined when unknown. The
// per-target lastAccount is never the prediction, only provenance ("last opened as <x>") (HIGH 3).
function rowIdentity(account?: string, was?: string): string {
  return account
    ? (was && was !== account ? `as ${account} (was ${was})` : `as ${account}`)
    : (was ? `last opened as ${was}` : 'account unknown');
}

function roleValue(side: ConnectionContextModel | undefined, fallback: string): string {
  return side?.available ? (side.modelName || side.database || workspaceNameFromEndpoint(side.source) || decodeSegment(short(side.source)) || fallback) : fallback;
}

function EnvBadge({ r }: { r: ConnectionRecord }) {
  const env = environment(r);
  // Instrument: the label is a word, with a dot in the warning colour while it carries the Production safeguards.
  return <span className={`sem-chip shrink-0${env.tone === 'warn' ? ' sem-chip-warn' : env.tone === 'accent' ? ' sem-chip-dot' : ''}`}>{env.text}</span>;
}

export function ConnectionsHub({ open, onClose, standalone = false, initialView }: { open: boolean; onClose: () => void; standalone?: boolean; initialView?: HubView }) {
  const { context, session, connectLocal, connectXmla, disconnect, refresh } = useConnection();
  const ctx = context as HubContext | null;
  const firstRoute = routeSection(initialView ?? 'open');
  const [view, setViewState] = useState<MainView>(firstRoute.view);
  const [panel, setPanel] = useState<HubPanel>(firstRoute.panel);   // the Change panel, the account menu, or none
  const [records, setRecords] = useState<ConnectionRecord[]>([]);
  const [probes, setProbes] = useState<AccountProbe[]>([]);
  const [profiles, setProfiles] = useState<AccountProfile[]>([]);   // the saved Microsoft identities on this device (Phase 2)
  const [loading, setLoading] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [rowError, setRowError] = useState<{ id: string; text: string; retry: () => void } | null>(null);   // said on the row that failed
  const [notice, setNotice] = useState<string | null>(null);   // what a job change did, in the engine's words where it gave some
  const [menuId, setMenuId] = useState<string | null>(null);   // the row whose ⋯ menu is open
  const [jobPick, setJobPick] = useState<Job | null>(null);    // the Change panel picker that is open
  const [query, setQuery] = useState('');
  const [work, setWork] = useState<WorkSetup | null>(null);
  const [switchTarget, setSwitchTarget] = useState<ConnectionRecord | null>(null);   // the model whose per-open account choice dialog is open (Phase 2)
  const [switchPurpose, setSwitchPurpose] = useState<'open' | 'query' | null>(null);   // HIGH (sol): the EXPLICIT intent behind a stale-model account choice (Open vs Query). null = a generic "choose account" (role-inferred).
  const [failedAuthAttempt, setFailedAuthAttempt] = useState<FailedAuthAttempt | null>(null); // attempt-local; never marks a saved profile globally signed out
  const [pendingDefault, setPendingDefault] = useState<AccountProfile | null>(null);  // a make-default awaiting its count-first confirmation
  // The account row the chooser's PRIMARY button acts on. The primary must never contradict the list (a row that needs a
  // sign-in cannot be answered by a silent retry of the same expired credential), so it follows this selection.
  const [pickedProfileId, setPickedProfileId] = useState<string | null>(null);
  const [endpoint, setEndpoint] = useState('');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState('interactive');
  const [newLabel, setNewLabel] = useState('');
  const [newTenant, setNewTenant] = useState('');   // optional tenant for command-line / service-identity adds
  const [prereq, setPrereq] = useState<AuthPrerequisites | null>(null);   // names/presence only, never secret values
  const [prereqLoading, setPrereqLoading] = useState(false);
  const [history, setHistory] = useState<HistoryEvent[]>([]);
  const [historyConn, setHistoryConn] = useState<string | null>(null);   // an optional per-model filter (set from a row's History)
  const [createOpen, setCreateOpen] = useState(initialView === 'create');   // the guarded "New blank model" surface; the SQL start opens its own form (startSqlOpen) and never this card
  const [createName, setCreateName] = useState('');
  const createNameNow = useRef(''); createNameNow.current = createName;   // what the box holds when the engine answers
  const [createConfirm, setCreateConfirm] = useState(false);   // second-step consent when the open model has unsaved changes
  // "From a SQL source" opens the start plan's own form here, with no name step first: it names the model, takes an empty
  // folder and reads the source in one call, and asks before it replaces an open model with unsaved changes.
  const [startSqlOpen, setStartSqlOpen] = useState(initialView === 'createsql');
  // A model that opens ends the start: the form, with the sign-in error of the start that failed before it, goes (plan statement check 3, defect 6).
  const openSessionId = session?.sessionId ?? null;
  const lastSessionId = useRef<string | null>(openSessionId);
  useEffect(() => {
    const was = lastSessionId.current;
    lastSessionId.current = openSessionId;
    setStartSqlOpen((on) => startFormStaysOpen(on, was, openSessionId));
  }, [openSessionId]);
  const pro = planRankingOf(useTier()) === 'granted';
  const [openConfirm, setOpenConfirm] = useState(false);       // second-step consent when opening another model over unsaved work
  const [pendingOpen, setPendingOpen] = useState<ConnectionRecord | null>(null);
  const [envEditId, setEnvEditId] = useState<string | null>(null);   // the row whose environment label is being changed
  // ---- SQL endpoints ---------------------------------------------------------------------------------------------
  const [endpoints, setEndpoints] = useState<ModelEndpointList | null>(null);   // null = not read (or the read failed)
  const [sqlSources, setSqlSources] = useState<SqlSourceRecord[]>([]);
  // A form held from before (the hub was closed, the page reloaded) opens again under its row.
  const heldSqlAtOpen = useRef<ReturnType<typeof heldSqlForm> | undefined>(undefined);
  if (heldSqlAtOpen.current === undefined) heldSqlAtOpen.current = heldSqlForm();
  const [sqlRow, setSqlRow] = useState<string | null>(heldSqlAtOpen.current?.row ?? null);              // the row whose endpoint form is open
  const [sqlForm, setSqlForm] = useState<SqlSourceForm | null>(heldSqlAtOpen.current?.form ?? null);
  const [sqlSourcesRead, setSqlSourcesRead] = useState(false);   // the saved sources were read: before that, a held form's source is not known yet
  // A NEW source's starting point (the form as it opened): its draft is held over this. Null while the form is on a saved source.
  const sqlNewSeed = useRef<SqlFields | null>(heldSqlAtOpen.current && !heldSqlAtOpen.current.form.id ? heldSqlAtOpen.current.base : null);
  const sqlNow = useRef({ row: sqlRow, form: sqlForm }); sqlNow.current = { row: sqlRow, form: sqlForm };
  const [sqlFormError, setSqlFormError] = useState<string | null>(null);
  const [sqlBusy, setSqlBusy] = useState<string | null>(null);
  // The saved source behind an open form can be changed from the other door (save_sql_source) while the form is open. The form
  // follows it while nothing is typed, and Save reads the source again and asks before writing an older copy over it.
  const sqlFields = (rec: SqlSourceRecord) => ({ name: rec.name || '', server: rec.server || '', database: rec.database || '', authMode: defaultSignInMode(rec), tenantId: rec.tenantId || '' });
  const sqlRaw = useRef<SqlSourceRecord | null>(null);   // the saved source the last Save read, as the engine sent it (the write names it as what it replaces)
  const sqlScopeNow = sqlRow && sqlForm ? sqlDraftScope(sqlRow, sqlForm.id) : '';
  const sqlGuard = useEditBase({
    current: sqlForm?.id ? (!sqlSourcesRead ? undefined : sqlSources.find((x) => x.id === sqlForm.id) ? sqlFields(sqlSources.find((x) => x.id === sqlForm.id)!) : null) : undefined,
    draft: sqlForm ? { name: sqlForm.name, server: sqlForm.server, database: sqlForm.database, authMode: sqlForm.authMode, tenantId: sqlForm.tenantId } : undefined,
    editing: sqlRow != null && !!sqlForm?.id, scope: sqlScopeNow,
    persist: sqlRow && sqlForm?.id ? { scope: sqlScopeNow } : undefined,
    reseed: (v) => setSqlForm((f) => f ? { ...f, ...v } : f),
    readNow: async () => { const rec = (await rpc<SqlSourceRecord[]>('listSqlSources')).find((x) => x.id === sqlForm?.id); sqlRaw.current = rec ?? null; return rec ? sqlFields(rec) : null; },
  });
  const sqlTypedSince = useRef(false);   // the last Save finished with newer words in the boxes than it carried: the form is not closed over them
  const heldSql = useHeldDrafts(SQL_DRAFTS);   // the scopes of this model's held endpoint forms: a row that holds one says so
  const endSqlDraft = (row: string | null, source: string | null) => { const m = draftModel(); if (m && row) draftStore.clear(m, sqlDraftScope(row, source)); };
  /** A change typed into the form. A NEW source's draft is held here (a saved one's is held by the guard above). */
  const typeSql = (next: SqlSourceForm) => {
    setSqlForm(next);
    const m = draftModel(), row = sqlNow.current.row;
    if (next.id || !m || !row) return;
    sqlNewSeed.current ??= { name: '', server: '', database: '', authMode: next.authMode, tenantId: next.tenantId };
    const v = sqlFieldsOfForm(next);
    if (sameValue(v, sqlNewSeed.current)) draftStore.clear(m, sqlDraftScope(row, null));
    else draftStore.set(m, sqlDraftScope(row, null), '', { value: v, base: sqlNewSeed.current });
  };
  const [sqlTest, setSqlTest] = useState<SqlSourceTestResult | null>(null);
  const [nlRemoving, setNlRemoving] = useState<NotLinkedSource | null>(null);          // a not-linked Remove awaiting its confirmation
  const [nlRefused, setNlRefused] = useState<SqlSourceDeleteResult | null>(null);      // a Remove the engine refused, with names

  const shown = open || standalone;
  // One way to move between the page and its few full views; the old section names land through routeSection.
  const setView = (v: MainView) => { setViewState(v); setMenuId(null); setJobPick(null); if (v !== 'open') setPanel('none'); };
  const goTo = (section: string) => { const r = routeSection(section); setViewState(r.view); setPanel(r.panel); setMenuId(null); setJobPick(null); };
  // Focus the search when the hub opens on the list (the tree "Open Model" lands here with search ready), once.
  const searchWanted = useRef(true);
  // A monotonically-bumped token for host "open" requests that arrive while the hub is ALREADY shown (the focus effect
  // below keys on [shown, onClose] and will not re-run then). Left at 0 on the initial mount.
  const [openToken, setOpenToken] = useState(0);

  const load = async () => {
    setLoading(true);
    try {
      // Probe alongside the list: the probe is the truth of who the NEXT open signs in as (the tenant-wide record),
      // so a target whose sibling switched identities shows the CURRENT account, not its own stale last-used one.
      const [recs, prb, profs] = await Promise.all([
        rpc<ConnectionRecord[]>('listConnections'),
        rpc<AccountProbe[]>('probeConnectionAccounts').catch(() => [] as AccountProbe[]),
        rpc<AccountProfile[]>('listAccountProfiles').catch(() => [] as AccountProfile[]),
      ]);
      setRecords(recs ?? []); setProbes(prb ?? []); setProfiles(profs ?? []); setError(null);
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setLoading(false); }
    void loadEndpoints();
  };
  // Each row's SQL endpoint and the sources no model is linked to, from the engine (C1). A failed read leaves the rows
  // without an endpoint line rather than claiming none is set.
  const loadEndpoints = async () => {
    const [list, sources] = await Promise.all([
      rpc<ModelEndpointList>('listModelSqlEndpoints').catch(() => null),
      rpc<SqlSourceRecord[]>('listSqlSources').catch(() => null),
    ]);
    setEndpoints(list); setSqlSources(sources ?? []);
    if (sources) setSqlSourcesRead(true);
  };
  const loadHistory = async (conn: string | null) => {
    try { setHistory((await rpc<HistoryEvent[]>('listConnectionHistory', conn || null)) ?? []); }
    catch { setHistory([]); }
  };
  const reloadHistory = async () => { if (view === 'history') await loadHistory(historyConn); };

  const probeOf = (r: ConnectionRecord): AccountProbe | undefined => probes.find((p) => p.id === r.id);
  const accountOf = (r: ConnectionRecord): string | undefined => probeOf(r)?.account;

  useEffect(() => { if (shown) { void load(); void refresh(); } }, [shown]); // eslint-disable-line react-hooks/exhaustive-deps
  // A pending "open on this view" request (openConnectionsOnView) lands the hub on the requested place when it opens.
  useEffect(() => {
    if (!shown || !nextView) return;
    goTo(nextView);
    if (nextView === 'createsql') openStartFromSql();
    if (nextView === 'create') openCreate();
    nextView = null;
    if (nextCreate) { nextCreate = false; openCreate(); }
  }, [shown]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (shown && view === 'history') void loadHistory(historyConn); }, [shown, view, historyConn]); // eslint-disable-line react-hooks/exhaustive-deps
  // A connection-state change from OUTSIDE this hub's own RPCs (an MCP-door connect/disconnect relayed from
  // model/activity, or a reference set/clear the host just completed) reloads everything so nothing goes stale.
  useEffect(() => {
    if (!shown) return;
    return onConnectionChange(() => { void load(); void refresh(); if (view === 'history') void loadHistory(historyConn); });
  }, [shown, view, historyConn]); // eslint-disable-line react-hooks/exhaustive-deps
  // The host can ask the hub to jump somewhere (the tree "Manage Connections" -> the Change panel, "Add published
  // model" from the quick pick -> the pasted-address form). Both doors receive the SAME host->webview message.
  useEffect(() => onOpenConnections((section) => {
    if (section && ['open', 'setup', 'accounts', 'history', 'add', 'work', 'sqlsources', 'desktop', 'workspace', 'createsql', 'create'].includes(section)) {
      // A host-driven Add must land on a FRESH form, not whatever advanced method + tenant a prior visit left behind.
      if (section === 'add') { setAuthMode('interactive'); setNewTenant(''); }
      goTo(section);
      if (section === 'createsql') openStartFromSql();
      if (section === 'create') openCreate();
      if (section === 'open') { searchWanted.current = true; setOpenToken((t) => t + 1); }
    }
  }), []); // eslint-disable-line react-hooks/exhaustive-deps
  // "Add a SQL source" from the Tests page lands here: the open model's SQL endpoint form, under its row.
  useEffect(() => {
    if (panel !== 'sql' || loading) return;
    const id = sideId(ctx?.editing);
    const r = id ? records.find((x) => x.id === id) : undefined;
    setPanel('none');
    if (r) openEndpointForm(r);
    else setNotice('Open a model first. Its SQL endpoint is set from its row in this list.');
  }, [panel, loading, records, ctx]); // eslint-disable-line react-hooks/exhaustive-deps

  // aria-modal promises an inert background, so deliver one: focus moves INTO the hub on open, Tab cycles inside it,
  // Escape dismisses, and closing returns focus to whatever opened it. ORDER MATTERS: the opener is captured BEFORE
  // any hub focus moves, so restore never targets an element inside the closing hub. onClose is a stable identity, so
  // this effect runs only on open/close, not on every re-render.
  const panelRef = useRef<HTMLDivElement | null>(null);
  const restoreRef = useRef<HTMLElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  // Ref mirrors of what Escape should close first: an open row menu, then the Change panel or the account menu.
  const menuIdRef = useRef<string | null>(null);
  useEffect(() => { menuIdRef.current = menuId; }, [menuId]);
  const panelOpenRef = useRef(false);
  useEffect(() => { panelOpenRef.current = panel === 'change' || panel === 'account'; }, [panel]);
  // A new account chooser (or a new purpose) starts with no explicit row pick. A pick can never leak from a previous target.
  useEffect(() => { setPickedProfileId(null); }, [switchTarget?.id, switchPurpose]);
  // The trigger that opened the current ⋯ menu, captured on open so focus can return to it on close.
  const menuTriggerRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    if (menuId) { document.querySelector<HTMLButtonElement>('[data-hub-menu] [role="menuitem"]:not(:disabled)')?.focus(); return; }
    if (menuTriggerRef.current) { menuTriggerRef.current.focus(); menuTriggerRef.current = null; }
  }, [menuId]);
  // Escape belongs to whatever is on TOP. The hub opens OVER the Tests drawer, and both used to close on the same key,
  // so adding a SQL source from inside a half-finished check threw the check away (Astra, 2026-09-15, P1). The hub
  // opens last, so it takes the key; inside it, an open row menu closes first, then an open panel, then the hub.
  useSurfaceEscape(() => { if (menuIdRef.current) setMenuId(null); else if (panelOpenRef.current) { setPanel('none'); setJobPick(null); } else onClose(); }, shown);
  useEffect(() => {
    if (!shown) return;
    restoreRef.current = document.activeElement as HTMLElement | null;   // capture the opener BEFORE moving focus in
    if (searchWanted.current && searchRef.current) { searchWanted.current = false; searchRef.current.focus(); }
    else panelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Tab' || !panelRef.current) return;
      const els = panelRef.current.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
      if (!els.length) return;
      const first = els[0], last = els[els.length - 1];
      const inside = panelRef.current.contains(document.activeElement);
      if (e.shiftKey && (!inside || document.activeElement === first)) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && (!inside || document.activeElement === last)) { e.preventDefault(); first.focus(); }
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); restoreRef.current?.focus(); };
  }, [shown, onClose]);
  // Re-focus search when an "open" request arrives while the hub is already shown (the token bumped above).
  useEffect(() => {
    if (openToken === 0 || view !== 'open') return;
    searchRef.current?.focus();
  }, [openToken]); // eslint-disable-line react-hooks/exhaustive-deps
  // Live auth prerequisites for an ADVANCED sign-in method on the pasted-address form: what the engine can actually
  // see (the detected `az login` account, or which service-principal inputs are present). Names/presence only.
  useEffect(() => {
    if (view !== 'add' || (authMode !== 'azcli' && authMode !== 'serviceprincipal')) { setPrereq(null); setPrereqLoading(false); return; }
    let cancelled = false;
    setPrereqLoading(true);
    const t = setTimeout(async () => {
      try { const p = await rpc<AuthPrerequisites>('probeAuthPrerequisites', authMode, newTenant.trim() || null); if (!cancelled) setPrereq(p); }
      catch { if (!cancelled) setPrereq(null); }
      finally { if (!cancelled) setPrereqLoading(false); }
    }, 250);
    return () => { cancelled = true; clearTimeout(t); };
  }, [view, authMode, newTenant]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!shown) return null;

  const editing = ctx?.editing;
  const querying = ctx?.querying;
  const publishing = ctx?.publishing;
  const currentAccount = session?.currentAccount;
  // ONE signed-in identity for the hub: the live session account when present, else the saved default (or any
  // signed-in) profile the engine already served, so a remembered sign-in is never shown as unknown after a restart.
  const signedInAccount = currentAccount
    || profiles.find((p) => p.isDefault && p.signedIn)?.username
    || profiles.find((p) => p.signedIn)?.username;
  // "Use another account" acts on the live model in play (editing side, else querying), and only when it is a published
  // target signed in via a switchable family (a local model uses Windows auth; azcli / service principal have no picker).
  const liveConnId = (editing?.live && editing?.kind !== 'localDesktop' ? editing?.connectionId : null) || querying?.connectionId;
  const switchable = records.find((r) => r.kind === 'xmla' && r.id === liveConnId && credentialFamily(r.authMode) != null);

  // ---- mutations (every one goes through an existing engine op) -------------------------------------------------
  // CLOSE CONTRACT: a SUCCESSFUL terminal action (open live, open a file, a working-copy open, create) closes the
  // dialog in BOTH modes. Changing which model answers tests and queries, or where this model publishes to, keeps you on the page and
  // says what changed (design "A model for each job"), as do label, forget, make-default and the SQL endpoint.
  const recoverAccountChoice = (r: ConnectionRecord, purpose: 'open' | 'query') => {
    setSwitchPurpose(purpose);
    setSwitchTarget(r);
    setPickedProfileId(null);   // a fresh failure re-selects the row that failed, so the primary button names it
  };
  const openModel = async (r: ConnectionRecord, opts?: { forceReauth?: boolean; accountProfileId?: string; makeDefault?: boolean; loginHint?: string }, purpose: 'open' | 'query' = 'open') => {
    setBusyId('open:' + r.id); setError(null); setRowError(null); setMenuId(null); setJobPick(null);
    try {
      const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
      if (s?.hasUnsavedChanges && !openConfirm) { setPendingOpen(r); setOpenConfirm(true); setBusyId(null); return; }
      const discard = !!s?.hasUnsavedChanges;
      if (r.kind === 'localDesktop') await rpc('openLocal', r.endpoint, r.database || null, discard);
      else if (r.kind === 'file') await rpc('open', r.endpoint, discard);
      else await rpc('openLive', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null,
        opts?.forceReauth ?? false, opts?.accountProfileId ?? null, opts?.makeDefault ?? false, opts?.loginHint ?? null, 'human', discard);
      setSwitchTarget(null); setSwitchPurpose(null); setFailedAuthAttempt(null); setPendingDefault(null); setPickedProfileId(null); setOpenConfirm(false); setPendingOpen(null);
      await refresh(); await load(); await reloadHistory();
      onClose();
    } catch (e) {
      const failure = String((e as Error).message ?? e);
      await reloadHistory();   // a FAILED open also appends a timeline event (a cancelled sign-in / superseded open)
      if (isSwitchableXmla(r) && isAuthRequiredFailure(failure)) {
        setError(failure);
        markFailedAuthAttempt(r, purpose, opts?.accountProfileId, opts?.loginHint);
        recoverAccountChoice(r, purpose);   // only auth failures enter account recovery
      } else if (records.some((x) => x.id === r.id)) {
        setRowError({ id: r.id, text: failure, retry: () => void openModel(r, opts, purpose) });   // said on the row that failed
      } else setError(failure);
    }
    finally { setBusyId(null); }
  };
  // Route an account choice by the ROLE the target plays. A QUERY-only live model switches ONLY its query connection
  // (connectXmla) and NEVER replaces the editing session; every other target opens/reopens for editing (openLive).
  const isQueryRole = (r: ConnectionRecord): boolean =>
    querying?.connectionId === r.id && !(editing?.live && editing?.connectionId === r.id && editing?.kind !== 'localDesktop');
  // Open the shared per-open account dialog with an EXPLICIT intent (HIGH, sol). null = a generic "choose account"
  // that stays role-inferred (a live query model query-switches; an editing model reopens).
  const openAccountChoice = (r: ConnectionRecord, purpose: 'open' | 'query' | null = null) => { setError(null); setMenuId(null); setPanel('none'); setFailedAuthAttempt(null); setSwitchPurpose(purpose); setSwitchTarget(r); };
  const applyAccount = async (r: ConnectionRecord, opts?: { forceReauth?: boolean; accountProfileId?: string; makeDefault?: boolean; loginHint?: string }) => {
    const asQuery = switchPurpose ? switchPurpose === 'query' : isQueryRole(r);   // honour explicit intent; else infer from role
    if (!asQuery) { await openModel(r, opts, 'open'); return; }
    setBusyId('open:' + r.id); setError(null); setMenuId(null);
    try {
      await rpc('connectXmla', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null,
        opts?.forceReauth ?? false, opts?.accountProfileId ?? null, opts?.makeDefault ?? false, opts?.loginHint ?? null);
      setSwitchTarget(null); setSwitchPurpose(null); setFailedAuthAttempt(null); setPendingDefault(null); setPickedProfileId(null);   // the query account switch succeeded — dismiss the dialog
      await refresh(); await load(); await reloadHistory();
      announceConnectionChange();
      setNotice(`Tests and queries now run on ${nameOf(r)}.`);
    } catch (e) {
      const failure = String((e as Error).message ?? e);
      setError(failure);
      await reloadHistory();
      if (isSwitchableXmla(r) && isAuthRequiredFailure(failure)) markFailedAuthAttempt(r, 'query', opts?.accountProfileId, opts?.loginHint);
      else setFailedAuthAttempt(null);
      recoverAccountChoice(r, 'query');   // the saved target + query purpose stay available for an explicit retry
    } finally { setBusyId(null); }
  };
  // The saved accounts that can open a given target: matched by sign-in family, and by tenant when the target records
  // one (a per-tenant default is what an unqualified open uses). Newest-used first (the store already sorts them).
  const profilesFor = (r: ConnectionRecord): AccountProfile[] => {
    const family = credentialFamily(r.authMode);
    const tenant = (r.tenantId || '').toLowerCase();
    return profiles.filter((p) => credentialFamily(p.family) === family && (!tenant || (p.tenantId || '').toLowerCase() === tenant));
  };

  // Tests and queries: the model that answers them. Whether a model CAN is the engine's call: the pickers strike out
  // what it listed as refused, and anything it refuses on the way comes back in its own words on the row.
  const attachTests = async (r: ConnectionRecord) => {
    setBusyId('query:' + r.id); setError(null); setRowError(null); setMenuId(null); setJobPick(null); setNotice(null);
    const res = r.kind === 'localDesktop'
      ? await connectLocal(r.endpoint)
      : await connectXmla(r.endpoint, r.database || '', r.authMode || 'interactive', r.tenantId || null);
    const failure = !res.ok ? (res.message || 'That model could not be connected for tests and queries.') : null;
    await load(); await reloadHistory(); setBusyId(null);
    if (res.ok) { setNotice(`Tests and queries now run on ${nameOf(r)}.`); return; }
    if (isSwitchableXmla(r) && isAuthRequiredFailure(failure)) {
      setError(failure);
      markFailedAuthAttempt(r, 'query');
      recoverAccountChoice(r, 'query');
    } else { setFailedAuthAttempt(null); setRowError({ id: r.id, text: failure || '', retry: () => void attachTests(r) }); }
  };
  // Disconnect tests and queries: the existing `disconnect`, which only other tabs offered before (contract 19.1).
  const disconnectTests = async () => {
    setBusyId('disconnect'); setError(null); setNotice(null); setJobPick(null);
    try { await disconnect(); await load(); await reloadHistory(); announceConnectionChange(); setNotice('Nothing answers tests and queries now. Your open model is unchanged.'); }
    finally { setBusyId(null); }
  };
  // The pickers (connectpickers.tsx) only list; these three do the work, so the unsaved-changes check, the close
  // contract and the label step stay the ones every other open uses. Each answers the engine's sentence on a
  // failure, which the picker shows on the row that failed, or null when it worked.
  const useDesktopForTests = async (dataSource: string): Promise<string | null> => {
    setError(null); setMenuId(null);
    const res = await connectLocal(dataSource);
    await load(); await reloadHistory();
    if (!res.ok) return res.message || 'That copy of Power BI Desktop could not be connected for tests and queries.';
    onClose();   // a test attach from the picker is a terminal action: close in both modes
    return null;
  };
  const openDesktop = async (dataSource: string): Promise<string | null> => {
    // openModel asks about unsaved changes first and shows its own failure line, exactly as a saved row's Open does.
    await openModel({ id: 'desktop:' + dataSource, kind: 'localDesktop', endpoint: dataSource });
    return null;
  };
  const connectPicked = async (c: WorkspaceChoice): Promise<string | null> => {
    setError(null);
    const res = await connectXmla(c.endpoint, c.database, c.authMode, c.tenantId);
    if (!res.ok) return res.message || 'The connection did not complete.';
    await load();
    if (c.label) {
      const recs = await rpc<ConnectionRecord[]>('listConnections').catch(() => [] as ConnectionRecord[]);
      const match = (recs ?? []).find((r) => r.endpoint === c.endpoint && (r.database || '') === c.database)
        ?? (recs ?? []).find((r) => r.endpoint === c.endpoint);
      if (match) { await rpc('labelConnection', match.id, c.label).catch(() => undefined); await load(); }
    }
    announceConnectionChange();   // the same in-webview announce the pasted-address add makes (see addAndConnect)
    setView('open');
    setNotice(`${c.database || 'The model'} is connected and saved.`);
    return null;
  };

  // Publish to: link the open model to a workspace model. Choosing only links it; publishing still shows its review.
  const setPublish = async (r: ConnectionRecord) => {
    setBusyId('publish:' + r.id); setError(null); setRowError(null); setMenuId(null); setJobPick(null); setNotice(null);
    try {
      await rpc('setPublishDestination', r.id, 'human'); await refresh(); await load(); await reloadHistory();
      setNotice(`This model now publishes to ${nameOf(r)}. Nothing was published.`);
    }
    catch (e) { setRowError({ id: r.id, text: String((e as Error).message ?? e), retry: () => void setPublish(r) }); }
    finally { setBusyId(null); }
  };
  // "Not set": the clear C1 added to set_publish_destination. The engine words the result, including the no-op.
  const clearPublish = async () => {
    setBusyId('publish:clear'); setError(null); setJobPick(null); setNotice(null);
    try {
      const after = await rpc<HubContext>('setPublishDestination', null, 'human', true);
      await refresh(); await load(); await reloadHistory();
      setNotice(after?.note || 'This model has no publish destination now.');
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const relabel = async (r: ConnectionRecord, label: string) => {
    setBusyId('label:' + r.id); setError(null);
    try { await rpc('labelConnection', r.id, label || null); await load(); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); setEnvEditId(null); }
  };

  const forget = async (r: ConnectionRecord) => {
    setBusyId('forget:' + r.id); setError(null); setMenuId(null);
    try { await rpc('forgetConnection', r.id); await load(); await refresh(); await reloadHistory(); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const previewWork = async (source: ConnectionRecord, parentFolder: string | null, queryId: string, publishId: string) => {
    setBusyId('work:' + source.id); setError(null);
    try {
      const plan = await rpc<WorkingCopyResult>('prepareWorkingCopy', source.id, parentFolder, false, queryId, publishId || null, 'human');
      setWork({ source, parentFolder, queryId, publishId, plan });
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };
  const beginWork = async (source: ConnectionRecord) => {
    setMenuId(null);
    const parent = source.workingFolder ? null : await pickWorkingCopyFolder();
    if (!source.workingFolder && !parent) return;
    const queryId = source.id;
    const publishId = source.kind === 'xmla' ? source.id : (source.publishConnectionId || '');
    setView('work');
    await previewWork(source, parent, queryId, publishId);
  };
  const updateWork = (queryId: string, publishId: string) => {
    if (!work) return;
    setWork({ ...work, queryId, publishId, plan: null });
    void previewWork(work.source, work.parentFolder, queryId, publishId);
  };
  const confirmWork = async () => {
    if (!work?.plan?.canCommit) return;
    setBusyId('work:' + work.source.id); setError(null);
    try {
      const plan = await rpc<WorkingCopyResult>('prepareWorkingCopy', work.source.id, work.parentFolder, true, work.queryId, work.publishId || null, 'human');
      setWork({ ...work, plan }); await refresh(); await load();
      if (plan.opened) onClose();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const addAndConnect = async () => {
    if (!endpoint.trim()) return;
    setBusyId('add'); setError(null);
    try {
      // Only the advanced modes expose (and use) the tenant field; a browser sign-in must NOT carry a hidden stale
      // tenant a prior advanced attempt left behind (MED, sol) - the picker targets the tenant itself.
      const advanced = authMode === 'azcli' || authMode === 'serviceprincipal';
      const res = await connectXmla(endpoint.trim(), database.trim(), authMode, advanced ? (newTenant.trim() || null) : null);
      if (!res.ok) { setError(res.message || 'The connection did not complete. Check the address, model name, and sign-in.'); return; }   // the engine's actual reason
      const added = endpoint.trim();
      setEndpoint(''); setDatabase(''); setNewLabel(''); setNewTenant('');
      await load();
      // A newly remembered endpoint carries the label the person set: apply it once, then land back on the model list.
      if (newLabel) {
        const recs = await rpc<ConnectionRecord[]>('listConnections').catch(() => [] as ConnectionRecord[]);
        const match = (recs ?? []).find((r) => r.endpoint === added);
        if (match) { await rpc('labelConnection', match.id, newLabel).catch(() => undefined); await load(); }
      }
      // connect_xmla runs through the UI door and emits no engine activity, so the host's 'connectionChanged' relay
      // never fires for it. Announce in-webview so a mounted sibling consumer (the Compare picker) re-reads the new
      // record without a remount — the product-wide contract; the hub itself already refreshed via its own load().
      announceConnectionChange();
      setView('open');
    } finally { setBusyId(null); }
  };

  // "New blank model" is guarded. The engine swaps the open model UNCONDITIONALLY, so the hub must (a) take a name
  // (never a hardcoded default) and (b) re-read the live session at submit time and require an explicit second
  // consent when the open model has unsaved changes — the same guard the native picker applies, in BOTH hosting modes.
  const submitCreate = async () => {
    const name = createName.trim();
    if (!name) return;
    setBusyId('create'); setError(null);
    try {
      const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
      if (s?.hasUnsavedChanges && !createConfirm) { setCreateConfirm(true); return; }
      await rpc<{ sessionId?: string }>('createModel', name, 1604, !!s?.hasUnsavedChanges);
      await refresh();
      setCreateConfirm(false);
      // The form and the hub end only when the box still holds the name that was sent; a name typed while the model was being made stays (the next Create sends it).
      if (createNameNow.current.trim() === name) { setCreateOpen(false); setCreateName(''); onClose(); }
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };
  const openCreate = () => { setCreateName(''); setCreateConfirm(false); setStartSqlOpen(false); setError(null); setCreateOpen(true); };
  const openStartFromSql = () => { setCreateOpen(false); setCreateConfirm(false); setError(null); setStartSqlOpen(true); };
  // The start made the model, opened it and planned it. The plan is the Plan page's, so the hub hands over to Studio:
  // standalone, the host opens Studio (asked BEFORE the panel closes: a message posted after 'closePanel' comes from a
  // disposed webview and never arrives, verifier H1); in Studio the move to the Plan is stamped with the NEW session,
  // which Studio holds until it has read that session (App.tsx heldNav).
  const startedFromSql = async () => {
    const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
    await refresh();
    setStartSqlOpen(false);
    if (standalone) runHostCommand('semanticus.openStudio');
    onClose();
    if (!standalone) navigateInStudio('plan', undefined, s?.sessionId);
  };
  // Navigate FRESH to the pasted-address form with a known sign-in method (MED, sol): a browser sign-in must land on
  // Browser sign-in, not whatever advanced method was last selected, and never carry a hidden stale tenant.
  const openAddView = (mode: string = 'interactive') => { setAuthMode(mode); setNewTenant(''); setError(null); setView('add'); };
  const createDirty = !!session?.hasUnsavedChanges || createConfirm;

  // The tenant default account for a target, from the saved profiles — who an UNQUALIFIED open uses.
  const defaultProfileFor = (r: ConnectionRecord): AccountProfile | undefined => profilesFor(r).find((p) => p.isDefault);
  // A raw auth failure does not prove a saved profile is globally signed out. Mark only the profile actually selected by
  // this attempt (or the known tenant default used by an unqualified attempt), and keep the marker local to this chooser.
  const markFailedAuthAttempt = (r: ConnectionRecord, purpose: 'open' | 'query', profileId?: string, loginHint?: string) => {
    const profile = profileId
      ? profiles.find((p) => p.id === profileId)
      : loginHint
        ? profilesFor(r).find((p) => p.username.toLowerCase() === loginHint.toLowerCase())
        : defaultProfileFor(r);
    setFailedAuthAttempt({ targetId: r.id, purpose, profileId: profile?.id, username: profile?.username || loginHint });
  };
  // Use a SAVED account for THIS open only (silent; never repoints the tenant default), routed by role.
  const useForThisOpen = (r: ConnectionRecord, p: AccountProfile) => { void applyAccount(r, { accountProfileId: p.id }); };
  // Add a NEW account via the Microsoft sign-in picker (does not repoint the default unless it is the first account).
  const signInAnother = (r: ConnectionRecord) => { void applyAccount(r, { forceReauth: true }); };
  // Make a saved account the tenant DEFAULT. It moves every saved model on that tenant, so the page says how many
  // BEFORE it moves them (DefaultConfirm), records it in History and broadcasts so both doors refresh.
  const makeDefaultFor = async (p: AccountProfile) => {
    setBusyId('default:' + p.id); setError(null);
    try {
      await rpc('setDefaultAccountProfile', p.id, 'human');
      setPendingDefault(null);
      await load(); await refresh(); await reloadHistory();
      announceConnectionChange();   // repointing the default is a connection-state change → refresh the pickers/siblings
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const viewHistoryFor = (r: ConnectionRecord | null) => { setMenuId(null); setPanel('none'); setHistoryConn(r?.id ?? null); setView('history'); };

  // ---- SQL endpoints: one per model, set from its row ------------------------------------------------------------
  const endpointOf = (r: ConnectionRecord): SqlEndpointInfo | null => endpointFor(endpoints, r.id);
  const openEndpointForm = (r: ConnectionRecord) => {
    setMenuId(null); setSqlFormError(null); setSqlTest(null); setSqlRow(r.id);
    const ep = endpointOf(r);
    const rec = ep ? sqlSources.find((s) => s.id === ep.sourceId) : undefined;
    const fresh: SqlSourceForm = rec
      ? { id: rec.id, name: rec.name || '', server: rec.server || '', database: rec.database || '', authMode: defaultSignInMode(rec), tenantId: rec.tenantId || '' }
      : { id: null, name: '', server: '', database: '', authMode: r.kind === 'xmla' ? (r.authMode || 'interactive') : 'interactive', tenantId: r.kind === 'xmla' ? (r.tenantId || '') : '' };
    // Words left on this row's form open again (the source it points at first, else any other the person typed for it).
    const held = heldSqlForm(r.id, fresh.id);
    sqlNewSeed.current = held ? (held.form.id ? null : held.base) : (fresh.id ? null : sqlFieldsOfForm(fresh));
    setSqlForm(held ? held.form : fresh);
  };
  // Cancel, and the end of a save or a remove: the form closes and what was typed in it is done with.
  const closeEndpointForm = () => {
    const { row, form } = sqlNow.current;
    if (form) endSqlDraft(row, form.id);
    setSqlRow(null); setSqlForm(null); setSqlFormError(null); setSqlTest(null);
  };
  const useSavedSource = (id: string) => {
    const rec = sqlSources.find((s) => s.id === id);
    const held = rec && sqlRow ? heldDraft<SqlFields>(sqlDraftScope(sqlRow, rec.id)) : undefined;
    if (rec) setSqlForm({ id: rec.id, ...(held?.value ?? { name: rec.name || '', server: rec.server || '', database: rec.database || '', authMode: defaultSignInMode(rec), tenantId: rec.tenantId || '' }) });
    setSqlFormError(null); setSqlTest(null);
  };
  const useNewSource = () => {
    const blank: SqlSourceForm = { id: null, name: '', server: '', database: '', authMode: 'interactive', tenantId: '' };
    const held = sqlRow ? heldDraft<SqlFields>(sqlDraftScope(sqlRow, null)) : undefined;
    sqlNewSeed.current = held?.base ?? sqlFieldsOfForm(blank);
    setSqlForm(held ? { id: null, ...held.value } : blank);
  };
  // Save the record (new, or the one this model already points at), then link it to the model. The engine's own
  // refusals land in the form, in its words.
  const saveRecord = async (): Promise<SqlSourceRecord | null> => {
    if (!sqlForm) return null;
    const refusal = sqlSourceFormError(sqlForm);
    if (refusal) { setSqlFormError(refusal); return null; }
    if (sqlForm.id && !(await sqlGuard.beforeSave())) return null;   // the saved source moved: the banner asks, nothing is written
    // An existing source is written naming what the Save read, and the engine compares it inside its own write.
    const was = sqlForm.id ? sqlRaw.current : null;
    const expected = was ? JSON.stringify({ name: was.name ?? null, server: was.server ?? null, database: was.database ?? null, authMode: was.authMode ?? null, tenantId: was.tenantId ?? null }) : null;
    let saved: SqlSourceRecord;
    try {
      saved = await rpc<SqlSourceRecord>('saveSqlSource', sqlForm.id || null, sqlForm.name.trim(), sqlForm.server.trim(),
        sqlForm.database.trim(), sqlForm.authMode, sqlForm.tenantId.trim() || null, 'human', expected);
    } catch (e) { if (await sqlGuard.refused(e)) return null; throw e; }
    const sent = { name: sqlForm.name, server: sqlForm.server, database: sqlForm.database, authMode: sqlForm.authMode, tenantId: sqlForm.tenantId };   // the words this Save carried (the form as it was when Save was pressed)
    // R-B: the typing is the saved source now, and its draft ends, only when the boxes still hold exactly the words the engine took. Words typed while the Save was on its way stay held, the form stays open on
    // them (sqlTypedSince, read by the callers that close it), and they are not reported as a change made by someone else.
    const now = sqlNow.current.form;
    sqlTypedSince.current = !!now && !sameValue({ name: now.name, server: now.server, database: now.database, authMode: now.authMode, tenantId: now.tenantId }, sent);
    if (!sqlTypedSince.current) {
      // The engine took it. The list learns of it at once, so the form on it has nothing to hold or ask.
      endSqlDraft(sqlRow, sqlForm.id);
      if (saved?.id) endSqlDraft(sqlRow, saved.id);
    } else if (sqlForm.id) sqlGuard.saved(sent, sqlFields(saved));
    if (saved?.id) setSqlSources((list) => [...list.filter((x) => x.id !== saved.id), saved]);
    setSqlForm((f) => f ? { ...f, id: saved?.id ?? f.id } : f);
    return saved;
  };
  const saveEndpoint = async (r: ConnectionRecord) => {
    setSqlBusy('save'); setSqlFormError(null);
    try {
      const saved = await saveRecord();
      if (!saved?.id) return;
      await rpc('setModelSqlEndpoint', saved.id, r.id, 'human');
      if (sqlTypedSince.current) { await loadEndpoints(); setNotice(`SQL endpoint saved for ${nameOf(r)}. You typed more while it was saving, so the form is still open on those words.`); return; }
      closeEndpointForm(); await loadEndpoints();
      setNotice(`SQL endpoint saved for ${nameOf(r)}. Checks for this model use it without asking again.`);
    }
    catch (e) { setSqlFormError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); }
  };
  // Test signs in with the record's own way and asks for one constant; an unsaved record cannot be probed, so the
  // form saves first and says so on the button.
  const testEndpoint = async () => {
    setSqlBusy('test'); setSqlFormError(null); setSqlTest(null);
    try {
      const saved = await saveRecord();
      if (!saved?.id) return;
      const result = await rpc<SqlSourceTestResult>('testSqlSource', saved.id, 'human');
      setSqlTest(result ?? { ok: false, note: 'The test finished without saying anything.' });
    }
    catch (e) { setSqlFormError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); await loadEndpoints(); }
  };
  const removeEndpoint = async (r: ConnectionRecord) => {
    setSqlBusy('remove'); setSqlFormError(null);
    try {
      await rpc('setModelSqlEndpoint', null, r.id, 'human');
      closeEndpointForm(); await loadEndpoints();
      setNotice(`${nameOf(r)} has no SQL endpoint now. The saved record is kept.`);
    }
    catch (e) { setSqlFormError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); }
  };
  const linkSource = async (sourceId: string, connectionId: string) => {
    setSqlBusy('link'); setError(null);
    try {
      await rpc('setModelSqlEndpoint', sourceId, connectionId, 'human');
      await loadEndpoints();
      const r = records.find((x) => x.id === connectionId);
      setNotice(`Linked to ${r ? nameOf(r) : 'the model'}.`);
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); }
  };
  // Remove is refused while anything still points at the source, and the refusal NAMES what does.
  const removeSource = async (s: NotLinkedSource) => {
    setSqlBusy('remove:' + s.id); setNlRefused(null);
    try {
      const result = await rpc<SqlSourceDeleteResult>('deleteSqlSource', s.id, 'human');
      if (result?.deleted) { setNlRemoving(null); setNlRefused(null); }
      else setNlRefused(result ?? { deleted: false });
    }
    catch (e) { setNlRefused({ deleted: false, note: String((e as Error).message ?? e) }); }
    finally { setSqlBusy(null); await loadEndpoints(); }
  };

  // ---- the page --------------------------------------------------------------------------------------------------
  const visibleRecords = records.filter((r) => {
    const q = query.trim().toLowerCase();
    if (!q) return true;
    return [nameOf(r), r.endpoint, r.database, environment(r).text].filter(Boolean).join(' ').toLowerCase().includes(q);
  });
  const busy = busyId != null;

  // One row: the model in words, its label, ONE verb (Open) and the ⋯ menu that holds everything else about it.
  function ModelRow({ r }: { r: ConnectionRecord }) {
    const account = accountOf(r);
    const was = probeOf(r)?.previousAccount;
    const family = credentialFamily(r.authMode);
    const stale = r.kind === 'xmla' && !account && !!family;   // a switchable published row with no live sign-in
    const jobs = rowJobs(r, ctx);
    const isEditing = jobs.includes('editing');
    const ep = endpointOf(r);
    // The account shows once, in the header. A row says who it opens as only when that is someone else.
    const who = r.kind === 'xmla' && (!account || account !== signedInAccount) ? rowIdentity(account, was) : null;
    const where = [whereOf(r), who].filter(Boolean).join(', ');
    const testsWhy = rowJobs(r, ctx).includes('tests and queries') ? null : (r.testsRefusal || null);
    const publishWhy = rowJobs(r, ctx).includes('publish target') ? null : (r.publishRefusal || null);
    return (
      <div className="hub-row relative" data-selected={isEditing ? 'true' : undefined}>
        <div className="sem-list-row sem-list-row-2">
          {/* The environment label sits INSIDE the row's reading line, not between it and Open. It is a word whose
              width changes from row to row (none, dev, uat, Production), and outside the line it pushed each row's
              Open to its own x, 97 px apart down the list. Inside it, every Open is in one column. */}
          <div className="flex min-w-0 flex-1 items-center gap-2 sem-row-line">
            <div data-testid="hub-model-row" className="flex min-w-0 flex-1 flex-col items-start py-2 text-left">
              <span className="hub-row-name block max-w-full truncate">{nameOf(r)}</span>
              <span className="sem-text-meta block max-w-full truncate" title={endpointText(r.endpoint)}>{rowSecondLine(where, jobs, ep?.name || null)}</span>
              {/* Words typed into this row's SQL endpoint form and not saved: kept, and found again when the form opens. */}
              <DraftMark show={sqlRow !== r.id && [...heldSql].some((id) => String(JSON.parse(id)[0]).startsWith(sqlRowPrefix(r.id)))} />
            </div>
            <EnvBadge r={r} />
          </div>
          <button className={`${BTN_COMPACT} whitespace-nowrap`} data-testid="hub-row-open" disabled={busy || isEditing} title={isEditing ? 'Open now' : undefined}
            onClick={() => { if (stale) openAccountChoice(r, 'open'); else void openModel(r); }}>Open</button>
          <button className="sem-btn sem-btn-sm hub-overflow" data-testid="hub-row-overflow" aria-haspopup="menu" aria-expanded={menuId === r.id} aria-label={`More for ${nameOf(r)}`}
            onClick={(e) => { const opening = menuId !== r.id; if (opening) menuTriggerRef.current = e.currentTarget; setMenuId(opening ? r.id : null); setPanel('none'); }} title="More">{'⋯'}</button>
        </div>
        {menuId === r.id && (
          <div role="menu" data-hub-menu onKeyDown={(e) => {
            const items = Array.from(e.currentTarget.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)'));
            const i = items.indexOf(document.activeElement as HTMLButtonElement);
            if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1) % items.length]?.focus(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length]?.focus(); }
            else if (e.key === 'Home') { e.preventDefault(); items[0]?.focus(); }
            else if (e.key === 'End') { e.preventDefault(); items[items.length - 1]?.focus(); }
          }} className="hub-menu absolute right-4 top-full z-20 -mt-1">
            {jobs.includes('tests and queries')
              // The one door for letting go of tests and queries (the connect bar's Disconnect and the Now strip's left):
              // it sits on the row that holds the job, and the Jobs panel offers the same as "None".
              ? <MenuItem onClick={() => { setMenuId(null); void disconnectTests(); }}>Turn tests and queries off</MenuItem>
              : <MenuItem disabled={!!testsWhy} why={testsWhy} onClick={() => { if (stale) openAccountChoice(r, 'query'); else void attachTests(r); }}>Use for tests and queries</MenuItem>}
            {jobs.includes('publish target')
              ? <MenuItem disabled onClick={() => undefined}>Publish target</MenuItem>
              : <MenuItem disabled={!!publishWhy} why={publishWhy} onClick={() => void setPublish(r)}>Publish here</MenuItem>}
            <MenuItem onClick={() => void beginWork(r)}>Work on a local copy</MenuItem>
            <div className="hub-menu-rule" />
            <MenuItem onClick={() => openEndpointForm(r)}>{ep ? `SQL endpoint: ${ep.name || ep.server || 'set'}` : 'Add SQL endpoint'}</MenuItem>
            {r.kind === 'xmla' && family && <MenuItem onClick={() => openAccountChoice(r)}>Open as another account</MenuItem>}
            {r.kind === 'xmla' && <MenuItem onClick={() => { setMenuId(null); setEnvEditId(r.id); }}>Environment label</MenuItem>}
            <MenuItem onClick={() => viewHistoryFor(r)}>History</MenuItem>
            <div className="hub-menu-rule" />
            <MenuItem danger onClick={() => void forget(r)}>Forget</MenuItem>
          </div>
        )}
        {envEditId === r.id && (
          <div className="hub-inline" data-testid="hub-env-edit">
            <label className="sem-field-label">Environment label
              <select data-testid="hub-env-select" disabled={busy} value={(r.label || '')} autoFocus onChange={(e) => void relabel(r, e.target.value)} className="sem-field sem-field-sm">
                {ENV_OPTIONS.filter((o) => o.value !== 'local').map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select></label>
            <span className="sem-text-meta">Sets what your assistant may do on this model.</span>
            <button className={`${BTN_QUIET} ml-auto`} onClick={() => setEnvEditId(null)}>Done</button>
          </div>
        )}
        {sqlRow === r.id && sqlForm && EndpointForm(r)}
        {rowError?.id === r.id && (
          <div className="hub-inline" data-testid="hub-row-error" role="alert">
            <span className="note-line note-bad min-w-0 flex-1 break-words">{rowError.text}</span>
            <button className={BTN_COMPACT} disabled={busy} onClick={() => rowError.retry()}>Try again</button>
            <button className={BTN_QUIET} onClick={() => setRowError(null)}>Dismiss</button>
          </div>
        )}
      </div>
    );
  }

  // The endpoint form opens under its row. Called as a function, never mounted as a JSX element, so typing survives a
  // parent re-render (D-004: an inner component is a NEW component type each keystroke, which tore the inputs down).
  function EndpointForm(r: ConnectionRecord) {
    const form = sqlForm!;
    const ep = endpointOf(r);
    const suggestion = sideId(editing) === r.id && !ep ? ctx?.sqlEndpointSuggestion : null;
    const sharedWith = form.id ? (endpoints?.rows ?? []).filter((x) => x.sqlEndpoint?.sourceId === form.id && x.connectionId !== r.id).length : 0;
    const saved = form.id ? sqlSources.find((s) => s.id === form.id) : undefined;
    return (
      <div className="hub-inline hub-sqlform" data-testid="hub-sql-form">
        <div className="hub-sqlform-head">
          <b className="sem-card-title">SQL endpoint for {nameOf(r)}</b>
          <span className="sem-text-meta">Checks compare this model's numbers against it. One per model, optional.</span>
        </div>
        {suggestion && (
          <div className="hub-found" data-testid="hub-sql-found">
            <span className="note-line note-ok min-w-0 flex-1">Found in this model's sources: <b>{suggestion.server}</b>, {suggestion.database}</span>
            <button className={BTN_COMPACT} onClick={() => {
              if (suggestion.matchingSourceId) useSavedSource(suggestion.matchingSourceId);
              else typeSql({ ...form, id: null, name: form.name || `${nameOf(r)} SQL endpoint`, server: suggestion.server, database: suggestion.database });
            }}>Use this</button>
          </div>
        )}
        {sqlSources.length > 0 && (
          <label className="sem-field-label">Use a saved one
            <select className="sem-field sem-field-sm" data-testid="hub-sql-saved" value={form.id || ''} onChange={(e) => { if (e.target.value) useSavedSource(e.target.value); else useNewSource(); }}>
              <option value="">A new one</option>
              {sqlSources.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select></label>
        )}
        <div className="hub-sqlform-grid">
          <label className="sem-field-label">Name
            <input data-testid="hub-sql-name" value={form.name} onChange={(e) => { typeSql({ ...form, name: e.target.value }); setSqlFormError(null); }}
              placeholder="Contoso warehouse" autoComplete="off" className="sem-field sem-field-sm" /></label>
          <label className="sem-field-label">Server
            <input data-testid="hub-sql-server" value={form.server} onChange={(e) => { typeSql({ ...form, server: e.target.value }); setSqlFormError(null); }}
              placeholder="contoso-sql.database.windows.net" autoComplete="off" className="sem-field sem-field-sm" /></label>
          <label className="sem-field-label">Database
            <input data-testid="hub-sql-database" value={form.database} onChange={(e) => { typeSql({ ...form, database: e.target.value }); setSqlFormError(null); }}
              placeholder="Warehouse" autoComplete="off" className="sem-field sem-field-sm" /></label>
        </div>
        {/* The ONE shared sign-in control, the same one the Data agent page and Promote use. */}
        <AccountPicker mode={form.authMode} onMode={(m) => typeSql({ ...form, authMode: m })}
          tenantId={form.tenantId} onTenantId={(t) => typeSql({ ...form, tenantId: t })}
          tenantLabel="where the database lives (optional)" hint="Every check on this model signs in this way." />
        {saved && <div className="sem-text-meta">{sqlSourceTestLine(saved.lastTestOk, shortWhen(saved.lastTestedUtc))}{sharedWith > 0 ? `. ${sharedWith} other saved model${sharedWith === 1 ? ' uses' : 's use'} this too, so a change here changes it for ${sharedWith === 1 ? 'it' : 'them'}.` : ''}</div>}
        {sqlGuard.moved && <EditMovedBanner what="SQL source" theirs={sqlGuard.theirs} onKeepMine={sqlGuard.keepMine} onUseNew={sqlGuard.useNew} disabled={sqlBusy != null} />}
        {sqlFormError && <div data-testid="hub-sql-form-error" className="note-line note-bad">{sqlFormError}</div>}
        {sqlTest && <div data-testid="hub-sql-form-test" className={`note-line ${sqlTest.ok ? 'note-ok' : 'note-bad'}`}>{sqlTest.note}</div>}
        <div className="hub-sqlform-btns">
          <DraftMark show={form.id ? sqlGuard.drafted : !!sqlNewSeed.current && !sameValue(sqlFieldsOfForm(form), sqlNewSeed.current)} />
          {ep && <button className={BTN_QUIET} data-testid="hub-sql-unlink" disabled={sqlBusy != null} onClick={() => void removeEndpoint(r)}>Remove endpoint</button>}
          <button className={`${BTN_COMPACT} ml-auto`} onClick={closeEndpointForm}>Cancel</button>
          <button className={BTN_COMPACT} data-testid="hub-sql-test" disabled={sqlBusy != null} onClick={() => void testEndpoint()}>{sqlBusy === 'test' ? 'Testing...' : 'Save and test'}</button>
          <button className={`${BTN_COMPACT} sem-btn-primary`} data-testid="hub-sql-save" disabled={sqlBusy != null} onClick={() => void saveEndpoint(r)}>Save</button>
        </div>
      </div>
    );
  }

  // The make-default confirmation says how many saved models move BEFORE anything moves. One element, drawn in the
  // account menu or above the account choice, whichever asked for it.
  const defaultConfirm = pendingDefault && (
    <DefaultConfirm username={pendingDefault.username} busy={busy}
      sentence={defaultMoveSentence(defaultMoveCount(records, pendingDefault.tenantId, credentialFamily(pendingDefault.family), credentialFamily), pendingDefault.tenantId)}
      onCancel={() => setPendingDefault(null)} onConfirm={() => void makeDefaultFor(pendingDefault)} />
  );

  // The account menu: the ONLY place the account shows. Saved accounts, which is the default, which are signed out,
  // Make default (count first), sign in another, the history, and where all of it is kept.
  function AccountMenu() {
    const methodLabel = (family?: string): string => credentialFamily(family) === 'devicecode' ? 'device code sign-in' : 'browser sign-in';
    return (
      <div className="hub-pop" role="group" aria-label="Accounts" data-testid="hub-account-menu">
        {profiles.length === 0 && <div className="sem-text-meta hub-pop-item">No saved accounts yet. Pick a workspace model to sign in.</div>}
        {profiles.map((p) => (
          <div key={p.id} className="hub-pop-item" data-testid="hub-profile-row">
            <span className={`note-dot ${p.signedIn ? 'note-dot-ok' : ''}`} aria-hidden />
            <span className="min-w-0 flex-1"><b className="block truncate">{p.username}</b>
              <span className="sem-text-meta block truncate">{[p.tenantId || 'tenant unknown', methodLabel(p.family)].join(' · ')}</span></span>
            {!p.signedIn
              ? <button className={BTN_QUIET} data-testid="hub-profile-resignin" disabled={busy} onClick={() => { const m = records.find((rec) => rec.kind === 'xmla' && profilesFor(rec).some((pp) => pp.id === p.id)); if (m) openAccountChoice(m); else setView('workspace'); }}>Sign in</button>
              : p.isDefault ? <span className="sem-chip sem-chip-dot">Default</span>
              : <button className={BTN_QUIET} data-testid="hub-profile-make-default" disabled={busy} onClick={() => setPendingDefault(p)}>Make default</button>}
          </div>
        ))}
        {defaultConfirm}
        <div className="hub-pop-rule" />
        {switchable && <button className={`${BTN_QUIET} hub-pop-link`} data-testid="hub-switch-account" disabled={busy} onClick={() => openAccountChoice(switchable)}>Use another account for {nameOf(switchable)}</button>}
        <button className={`${BTN_QUIET} hub-pop-link`} data-testid="hub-sign-in-another-top" disabled={busy} onClick={() => { if (switchable) signInAnother(switchable); else setView('workspace'); }}>Sign in another account</button>
        <button className={`${BTN_QUIET} hub-pop-link`} data-testid="hub-other-sign-in" onClick={() => openAddView()}>Other ways to sign in</button>
        <div className="hub-pop-item"><button className={BTN_QUIET} onClick={() => viewHistoryFor(null)}>History</button></div>
      </div>
    );
  }

  // The per-open account choice (Phase 2), reached from a row's "Open as another account", a stale row's Open, and the
  // account menu. Saved profiles are selectable per open without repointing the tenant default; Sign in another account
  // adds a profile via the real Microsoft picker; make-default is explicit and quantified. A failed authorization
  // returns HERE, never to an address form.
  function AccountDialog({ r }: { r: ConnectionRecord }) {
    const list = profilesFor(r);
    const def = defaultProfileFor(r);
    const defName = def?.username || accountOf(r) || signedInAccount;
    const purpose = switchPurpose === 'query' ? 'query' : 'open';
    const role = purpose === 'query' ? 'tests and queries' : 'opening';
    const authRequired = isAuthRequiredFailure(error || undefined);
    const signedInCount = list.filter((p) => p.signedIn).length;
    const signedOutCount = list.length - signedInCount;
    const failedProfile = failedAuthAttempt?.targetId === r.id && failedAuthAttempt.purpose === purpose
      ? list.find((p) => p.id === failedAuthAttempt.profileId || p.username === failedAuthAttempt.username)
      : undefined;
    // ONE notice, not two: the plain cause line, then the instruction, then the engine's raw text behind "Show details".
    const causeAccount = failedProfile?.username || failedAuthAttempt?.username || accountOf(r);
    const plainCause = plainAuthCause(error, causeAccount);
    // The row the PRIMARY button acts on: an explicit pick, else the row that just failed, else the signed-in default.
    const picked = list.find((p) => p.id === pickedProfileId) || failedProfile || (def?.signedIn ? def : undefined) || list.find((p) => p.signedIn);
    const needsSignIn = !!picked && (picked.id === failedProfile?.id || !picked.signedIn);
    const legacyPrimary = defName ? `${purpose === 'query' ? 'Query with' : 'Open with'} ${defName}` : `${purpose === 'query' ? 'Query with' : 'Open with'} the default account`;
    const primaryLabel = !picked
      ? (list.length === 0 ? legacyPrimary : 'Select an account above')
      : picked.id === failedProfile?.id ? `Sign in again as ${picked.username}`
      : !picked.signedIn ? `Sign in as ${picked.username}`
      : `Use ${picked.username} for this ${purpose}`;
    // A row that needs a sign-in is answered by a forced, identity-pinned sign-in - NEVER a silent retry of the
    // credential that just failed. A signed-in row is pinned for this purpose only and never repoints the default.
    const runPrimary = () => {
      if (!picked) { void applyAccount(r); return; }   // reachable only with no saved profiles: the tenant-default path
      if (needsSignIn) { void applyAccount(r, { forceReauth: true, loginHint: picked.username }); return; }
      useForThisOpen(r, picked);
    };
    return (
      <div data-testid="hub-account-dialog" data-purpose={purpose} className="sem-card mb-3">
        <div className="sem-card-title">Which account should {nameOf(r)} use for {role}?</div>
        <div className="sem-text-meta mt-1">For this {purpose} only.</div>
        {error && <div data-testid="hub-account-error" className="sem-inset mt-2 text-xs leading-4 break-words">
          <div className={`text-xs font-semibold note-line ${authRequired ? 'note-warn' : 'note-bad'}`}>{plainCause || `The last attempt failed: ${error}`}</div>
          {authRequired
            ? <div data-testid="hub-account-recovery" className="mt-1" style={{ color: 'var(--sem-fg)' }}>
                {failedProfile
                  ? <>Sign in needed for this connection as {failedProfile.username}. Use “Sign in again” on that row, or the button below. {signedInCount > 1 && <>You can also choose another signed-in saved account. </>}Your current model and local edits stay in place.</>
                  : signedInCount > 0
                    ? <>Choose a signed-in saved account for this {purpose}. {signedOutCount > 0 && <>Signed-out profiles offer “Sign in and use”. </>}“Sign in another account” opens the sign-in picker. Your current model and local edits stay in place.</>
                    : <>No saved account is currently signed in. Use “Sign in another account” to authorize this {purpose}. Your current model and local edits stay in place.</>}
              </div>
            : <div data-testid="hub-account-retry" className="mt-1" style={{ color: 'var(--sem-fg)' }}>The model stays selected. Retry this {purpose} with a listed account or use “Sign in another account”. Your current model and local edits stay in place.</div>}
          {plainCause && <details data-testid="hub-account-details" className="mt-2">
            <summary className="cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>Show details</summary>
            <div className="mt-1 break-words" style={{ color: 'var(--sem-muted)' }}>{error}</div>
          </details>}
        </div>}
        <div className="hub-account-list mt-3">
          {list.map((p) => (
            <div key={p.id} data-testid="hub-account-row" data-selected={picked?.id === p.id ? 'true' : undefined} className="hub-account-row flex items-center gap-3 border-b py-2 last:border-b-0">
              {/* Selecting a row is what the primary button follows, so the identity itself is the selector. */}
              <button type="button" data-testid="hub-account-select" aria-pressed={picked?.id === p.id} className="min-w-0 flex-1 text-left" onClick={() => setPickedProfileId(p.id)}>
                <div className="truncate text-xs font-semibold" style={{ color: 'var(--sem-fg)' }}>{p.username}</div>
                <div className="truncate text-xs" style={{ color: 'var(--sem-muted)' }}>{[p.tenantId ? `${p.tenantId} tenant` : 'tenant unknown', failedProfile?.id === p.id ? 'sign-in needed for this connection' : p.signedIn ? 'signed in' : 'signed out', p.lastUseUtc ? `last used ${shortWhen(p.lastUseUtc)}` : null].filter(Boolean).join(' · ')}</div>
              </button>
              <div className="flex shrink-0 items-center gap-2">
                {/* Signed-out is checked BEFORE default (MED, sol): a DEFAULT profile that is signed out must still get a
                    targeted "Sign in and use" (forceReauth + loginHint re-auths THIS identity). */}
                {failedProfile?.id === p.id
                  ? <button className={BTN_QUIET} data-testid="hub-sign-in-again" disabled={busy} onClick={() => void applyAccount(r, { forceReauth: true, loginHint: p.username })}>Sign in again</button>
                  : !p.signedIn
                  ? <button className={BTN_QUIET} data-testid="hub-signin-and-use" disabled={busy} onClick={() => void applyAccount(r, { forceReauth: true, loginHint: p.username })}>Sign in and use</button>
                  : p.isDefault
                    ? <span className="sem-chip sem-chip-dot">Default</span>
                    : null}
                {!p.isDefault && p.signedIn && <button className={BTN_QUIET} data-testid="hub-make-default" style={{ color: 'var(--sem-muted)' }} disabled={busy} onClick={() => setPendingDefault(p)}>Make default</button>}
              </div>
            </div>
          ))}
          <button data-testid="hub-sign-in-another" className="flex w-full items-center gap-3 px-3 py-2 text-left" style={{ background: 'var(--sem-surface-2)' }} disabled={busy} onClick={() => signInAnother(r)}>
            <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-(length:--sem-text-body) font-bold" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} aria-hidden>+</span>
            <span className="min-w-0"><span className="block text-xs font-semibold" style={{ color: 'var(--sem-fg)' }}>Sign in another account</span>
              </span>
          </button>
        </div>
        {panel !== 'account' && defaultConfirm}
        <div className="mt-3 flex justify-end gap-2">
          <button className={BTN} onClick={() => { setSwitchTarget(null); setSwitchPurpose(null); setFailedAuthAttempt(null); setPendingDefault(null); setPickedProfileId(null); setError(null); }}>Cancel</button>
          <button className={BTN_PRIMARY} data-testid="hub-open-default" disabled={busy || (!picked && list.length > 0)} onClick={runPrimary}>{primaryLabel}</button>
        </div>
      </div>
    );
  }

  // The Change panel behind the Jobs button: the three jobs, each a picker over the saved models, and a model that cannot do
  // a job is struck out with the engine's reason (jobOptions reads only what the engine listed).
  function JobsPanel() {
    const pick = (job: Job, current: string, id: string | null, onChoose: (r: ConnectionRecord) => void, extra?: ReactNode) => (
      <JobPicker job={job} current={current} busy={busy} open={jobPick === job}
        onToggle={() => setJobPick(jobPick === job ? null : job)}
        options={jobOptions(records, job, id, nameOf, kindWord)}
        onChoose={(cid) => { const r = records.find((x) => x.id === cid); if (r) onChoose(r); }} extra={extra} />
    );
    const addLine = <button type="button" className="hub-job-opt hub-job-add" onClick={() => { setJobPick(null); setPanel('none'); }}><span>Add Power BI Desktop or a workspace model</span></button>;
    const rows = [
      // Editing goes through openModel, so a model with unsaved edits asks first before it is replaced.
      { job: 'edit' as Job, current: roleValue(editing, 'None'), picker: pick('edit', roleValue(editing, 'None'), sideId(editing), (r) => void openModel(r), addLine) },
      { job: 'tests' as Job, current: roleValue(querying, 'None'), picker: pick('tests', roleValue(querying, 'None'), sideId(querying), (r) => void attachTests(r),
        <>{addLine}{querying?.available && <button type="button" className="hub-job-opt hub-job-add" data-testid="hub-job-tests-off" onClick={() => void disconnectTests()}><span>None: turn tests and queries off</span></button>}</>) },
      { job: 'publish' as Job, current: roleValue(publishing, 'Not set'), picker: pick('publish', roleValue(publishing, 'Not set'), sideId(publishing), (r) => void setPublish(r),
        <>{addLine}{publishing?.available && <button type="button" className="hub-job-opt hub-job-add" data-testid="hub-job-publish-clear" onClick={() => void clearPublish()}><span>Not set</span></button>}</>) },
    ];
    return <ChangePanel rows={rows} note={notice} onDone={() => { setPanel('none'); setJobPick(null); }} />;
  }

  function ListView() {
    const unlinked = endpoints?.notLinked ?? [];
    return (
      <section className="hub-page">
        <div className="hub-adds">
          <button className={records.length === 0 ? BTN_PRIMARY : BTN} data-testid="hub-add-file" onClick={() => openLocalModel()}>Open file or folder</button>
          <button className={BTN} data-testid="hub-add-desktop" onClick={() => { setError(null); setView('desktop'); }}>Power BI Desktop</button>
          <button className={BTN} data-testid="hub-add-workspace" onClick={() => { setError(null); setView('workspace'); }}>Workspace model</button>
          <button className={BTN_QUIET} data-testid="hub-add-blank" onClick={() => openCreate()}>New blank model</button>
          <button className={BTN_QUIET} data-testid="hub-add-sql" onClick={() => openStartFromSql()}>From a SQL source</button>
          {records.length >= SEARCH_FROM && <input ref={searchRef} value={query} onChange={(e) => setQuery(e.target.value)} type="search" placeholder="Search models" aria-label="Search models" className="sem-field ml-auto hub-search" />}
        </div>
        {notice && panel !== 'change' && <div className="sem-text-meta note-line note-ok" data-testid="hub-notice">{notice}</div>}
        {loading && records.length === 0 && <div className="sem-text-meta">Loading your models...</div>}
        {!loading && records.length === 0 && (
          <div className="hub-empty" data-testid="hub-empty">
            <b className="sem-card-title">Open your first model</b>
            <p className="sem-text-meta">A file or folder on this computer, a report open in Power BI Desktop, or a model in a Fabric or Power BI workspace. Models you open show up here next time.</p>
          </div>
        )}
        {records.length > 0 && (
          <div className="hub-list" data-testid="hub-list">
            {/* Called, never mounted as <ModelRow />: ModelRow is declared inside this component, so it is a NEW component type on every render. Mounted as an element it tore down the whole row, and the
                SQL form's inputs with it, on every keystroke (the form's state lives here): focus was lost after each key (F15, Sol's drafts walk). */}
            {visibleRecords.map((r) => <Fragment key={r.id}>{ModelRow({ r })}</Fragment>)}
            {visibleRecords.length === 0 && <div className="sem-text-meta hub-list-empty">No saved models match.</div>}
          </div>
        )}
        {nlRemoving && !nlRefused && removeConfirmSentence(nlRemoving) && (
          <div className="sem-card" data-testid="hub-sql-remove-confirm">
            <div className="sem-card-title">Remove {nlRemoving.name}?</div>
            <div className="sem-text-meta mt-1">{removeConfirmSentence(nlRemoving)}</div>
            <div className="mt-3 flex justify-end gap-2">
              <button className={BTN} onClick={() => setNlRemoving(null)}>Cancel</button>
              <button className={BTN} data-testid="hub-sql-remove-confirm-btn" disabled={sqlBusy != null} onClick={() => void removeSource(nlRemoving)} style={{ color: 'var(--sem-bad)' }}>Remove</button>
            </div>
          </div>
        )}
        {nlRefused && nlRemoving && (
          <div className="sem-card" data-testid="hub-sql-refused">
            <div className="sem-card-title">{nlRemoving.name} was not removed</div>
            <div className="mt-1 text-xs leading-5 note-line note-warn">It is still in use, so nothing was removed.</div>
            {(nlRefused.checksUsing ?? 0) > 0 && <div className="sem-text-meta mt-2"><b>Checks that use it</b> {sqlSourceUsedByNames(nlRefused.checkTitles ?? [], nlRefused.checksUsing ?? 0)}</div>}
            {(nlRefused.tableMappingsUsing ?? 0) > 0 && <div className="sem-text-meta mt-1"><b>Table mappings that use it</b> {sqlSourceUsedByNames(nlRefused.mappedTables ?? [], nlRefused.tableMappingsUsing ?? 0)}</div>}
            {(nlRefused.usingModels ?? []).length > 0 && <div className="sem-text-meta mt-1"><b>Saved models with those checks or mappings</b> {(nlRefused.usingModels ?? []).join(', ')}</div>}
            {(nlRefused.linkedModels ?? []).length > 0 && <div className="sem-text-meta mt-1"><b>Models that use it as their SQL endpoint</b> {(nlRefused.linkedModels ?? []).join(', ')}</div>}
            {/* The engine's own note only when it named nothing, so one refusal never reads as two (D-166). */}
            {(nlRefused.checksUsing ?? 0) === 0 && (nlRefused.tableMappingsUsing ?? 0) === 0 && !(nlRefused.linkedModels ?? []).length && nlRefused.note && <div className="sem-text-meta mt-2">{nlRefused.note}</div>}
            <div className="mt-3 flex justify-end"><button className={BTN} onClick={() => { setNlRefused(null); setNlRemoving(null); }}>Close</button></div>
          </div>
        )}
        <NotLinkedList items={unlinked} busy={sqlBusy != null || busy}
          linkTargets={records.map((r) => ({ id: r.id, name: nameOf(r) }))}
          onLink={(sid, cid) => void linkSource(sid, cid)} onRemove={(s) => { setNlRefused(null); setNlRemoving(s); }} />
      </section>
    );
  }

  function HistoryView() {
    const connName = historyConn ? nameOf(records.find((r) => r.id === historyConn) || { id: '', kind: '', endpoint: '' }) : null;
    return (
      <section className="hub-page">
        <div className="flex items-center gap-2">
          <button className={BTN_QUIET} onClick={() => setView('open')}>{'‹'} Back</button>
          <h2 className="sem-section-title">History</h2>
          {connName && <button type="button" className="sem-btn sem-btn-sm" onClick={() => setHistoryConn(null)} title="Show every model">{connName} {'×'}</button>}
          <span className="sem-text-meta ml-auto">Models and sign-ins, kept on this device</span>
        </div>
        <div className="sem-card sem-card-flush">
          {history.length === 0 && <div className="px-3 py-3 text-xs" style={{ color: 'var(--sem-muted)' }}>No connection history yet.</div>}
          {history.map((e, i) => (
            <div key={i} className="flex items-start gap-2 border-b px-4 py-2 last:border-b-0" style={{ borderColor: 'var(--sem-rule)' }}>
              <span className={`note-dot mt-2 ${e.ok ? 'note-dot-ok' : 'note-dot-bad'}`} aria-hidden />
              <div className="min-w-0 flex-1">
                <div className="text-xs font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{historyKindLabel(e)}</div>
                <div className="text-xs truncate" style={{ color: 'var(--sem-muted)' }} title={e.detail || e.endpoint}>{historySubLine(e)}</div>
              </div>
            </div>
          ))}
        </div>
      </section>
    );
  }

  // The advanced-auth PREFLIGHT panel: what the engine can actually SEE (the detected az login session, or which
  // service-principal inputs are present on the host, names + presence only, never a value), so choosing the method
  // previews whether Connect will work. It says plainly that secrets live in the environment and Key Vault is not read.
  function AuthPreflight() {
    const sp = authMode === 'serviceprincipal';
    const names = prereq?.requirements.map((req) => `${req.label}: ${req.envNames.join(' or ')}`) ?? [];
    return (
      <div data-testid="hub-auth-preflight" className="text-xs border-t pt-3" style={{ borderColor: 'var(--sem-rule)' }}>
        <div className="flex items-center gap-2">
          <span className="sem-label">{sp ? 'Service identity setup' : 'Command-line sign-in'}</span>
          {prereqLoading && <span className="text-xs" style={{ color: 'var(--sem-muted)' }}>Checking...</span>}
          {!prereqLoading && prereq && <span className={`sem-chip ml-auto ${prereq.ready ? 'sem-chip-ok' : 'sem-chip-warn'}`}>{prereq.ready ? 'Ready' : 'Not ready'}</span>}
        </div>
        {sp ? (
          <>
            {prereq && prereq.requirements.length > 0 ? (
              <ul className="mt-2 space-y-1">
                {prereq.requirements.map((req) => (
                  <li key={req.label} className="flex items-start gap-2">
                    <span className={`note-dot mt-2 ${req.present ? 'note-dot-ok' : 'note-dot-warn'}`} aria-hidden />
                    <span style={{ color: 'var(--sem-fg)' }}>{req.label}<span style={{ color: 'var(--sem-muted)' }}> · {req.present ? 'found' : 'not set'}{req.label === 'Tenant' && newTenant.trim() ? ', from the tenant field above' : ''}</span></span>
                  </li>
                ))}
              </ul>
            ) : (
              <div className="mt-2" style={{ color: 'var(--sem-muted)' }}>Set the client id, secret and tenant before you connect.</div>
            )}
            {/* The variable names are for the person who sets them, so they sit behind a disclosure, not in the copy. */}
            <details data-testid="hub-auth-names" className="mt-2">
              <summary className="cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>Show the variable names</summary>
              <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>
                {names.length > 0 ? names.join('. ') : 'AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID, or the FABRIC_ and POWERBI_ aliases'}.
                {' '}Set them before VS Code starts, or reload the window.
              </div>
            </details>
            <div className="mt-2" style={{ color: 'var(--sem-muted)' }}>Semanticus never stores the secret. <b style={{ color: 'var(--sem-fg)' }}>Key Vault references are not supported here;</b> have your pipeline place the secret in the environment.</div>
          </>
        ) : (
          <>
            {prereq && prereq.cliAccount
              ? <div className="mt-2" style={{ color: 'var(--sem-fg)' }}>Signed in to Azure CLI as {prereq.cliAccount}{prereq.cliTenant ? ` · ${prereq.cliTenant}` : ''}.</div>
              : <div className="mt-2 note-line note-warn">{prereq?.detail || 'Not signed in to the Azure CLI. Run az login in a terminal, then reload the window.'}</div>}
            <div className="mt-2" style={{ color: 'var(--sem-muted)' }}>Uses your current az login session; nothing is entered or stored here. Power BI can reject Azure CLI tokens on some tenants, where service identity or browser sign-in is more reliable.</div>
          </>
        )}
      </div>
    );
  }

  // "Other ways to sign in" and "Paste an address instead" land here: the address for endpoints the pickers cannot
  // list, the sign-in way, and the service identity setup check.
  function AddView() {
    const advanced = authMode === 'azcli' || authMode === 'serviceprincipal';
    return (
      <section className="hub-page">
        <div className="flex items-center gap-2"><button className={BTN_QUIET} onClick={() => setView('open')}>{'‹'} Back</button>
          <h2 className="sem-section-title">Add a model by its address</h2></div>
        <p className="sem-text-meta">For a model the workspace list cannot show.</p>
        <form className="sem-card max-w-[560px] grid gap-3" onSubmit={(e) => { e.preventDefault(); void addAndConnect(); }}>
          <label className="sem-field-label">Workspace address
            <input data-testid="hub-endpoint-input" value={endpoint} onChange={(e) => setEndpoint(e.target.value)} placeholder="powerbi://api.powerbi.com/v1.0/myorg/Workspace" autoComplete="off" className="sem-field " />
            <span className="sem-text-meta">Copy it from the workspace settings in Power BI or Fabric.</span></label>
          <label className="sem-field-label">Model name
            <input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder="Optional if the workspace has one model" autoComplete="off" className="sem-field " /></label>
          <label className="sem-field-label">Sign-in
            <select data-testid="hub-authmode-select" value={authMode} onChange={(e) => { const m = e.target.value; setAuthMode(m); if (m !== 'azcli' && m !== 'serviceprincipal') setNewTenant(''); }} className="sem-field ">
              <option value="interactive">Browser sign-in</option>
              <option value="azcli">Command-line sign-in</option>
              <option value="serviceprincipal">Service identity</option>
            </select></label>
          {/* Tenant targeting for the advanced modes: a service principal needs its tenant; az login otherwise silently
              uses its home tenant (the bug sol flagged where the add passed tenantId=null). */}
          {advanced && (
            <label className="sem-field-label">{authMode === 'serviceprincipal' ? 'Tenant' : 'Tenant (optional)'}
              <input data-testid="hub-tenant-input" value={newTenant} onChange={(e) => setNewTenant(e.target.value)} placeholder="Tenant ID or domain (e.g. contoso.com)" autoComplete="off" className="sem-field " />
              <span className="sem-text-meta">{authMode === 'serviceprincipal' ? 'Your organisation\'s tenant.' : 'Leave blank to use your az login home tenant, or name the tenant this model lives in.'}</span></label>
          )}
          <label className="sem-field-label">Environment
            <select value={newLabel} onChange={(e) => setNewLabel(e.target.value)} className="sem-field ">
              {ENV_OPTIONS.filter((o) => o.value !== 'local').map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
            <span className="sem-text-meta">Sets what your assistant may do on this model.</span></label>
          {advanced && AuthPreflight()}
          <div className="flex justify-end gap-2 border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
            <button type="button" className={BTN} onClick={() => setView('open')}>Cancel</button>
            <button type="submit" className={BTN_PRIMARY} disabled={!endpoint.trim() || busy}>Connect and remember</button>
          </div>
        </form>
      </section>
    );
  }

  // Work on a local copy keeps its own short form: where tests and queries run, where it publishes, and the folder.
  function WorkView() {
    const src = work?.source;
    return (
      <section className="hub-page">
        <div className="flex items-center gap-2"><button className={BTN_QUIET} onClick={() => { setWork(null); setView('open'); }}>{'‹'} Back</button>
          <h2 className="sem-section-title">Work on a local copy of {src ? nameOf(src) : 'this model'}</h2></div>
        <p className="sem-text-meta">Edit the model files on this computer. To run tests and queries, also choose a running model with data.</p>
        {work && (
          <div className="sem-card max-w-[640px]">
            <div className="grid grid-cols-2 gap-3 max-[600px]:grid-cols-1">
              <label className="sem-field-label">Tests and queries
                <select value={work.queryId} onChange={(e) => updateWork(e.target.value, work.publishId)} className="sem-field w-full ">
                  {records.filter((r) => !r.testsRefusal).map((r) => <option key={r.id} value={r.id}>{nameOf(r)} · {kindWord(r)}</option>)}
                </select>
              </label>
              <label className="sem-field-label">Publish to
                <select value={work.publishId} onChange={(e) => updateWork(work.queryId, e.target.value)} className="sem-field w-full ">
                  <option value="">Choose later</option>
                  {records.filter((r) => !r.publishRefusal && r.kind === 'xmla').map((r) => <option key={r.id} value={r.id}>{nameOf(r)}</option>)}
                </select>
              </label>
            </div>
            {work.plan ? <>
              <div className="mt-2 text-xs truncate" style={{ color: 'var(--sem-muted)' }} title={work.plan.targetFolder}><b style={{ color: 'var(--sem-fg)' }}>Local folder:</b> {work.plan.targetFolder}</div>
              {work.plan.conflicts?.map((c) => <div key={c} className="mt-2 text-xs note-line note-bad">{c}</div>)}
              {work.plan.error && <div className="mt-2 text-xs note-line note-bad">{work.plan.error}</div>}
              {work.plan.opened && <div className="mt-2 text-xs note-line note-ok">Local copy opened. {work.plan.queryConnected ? 'The chosen model answers tests and queries.' : 'Choose a model for tests and queries when ready.'}</div>}
              {!work.plan.opened && <div className="flex justify-end gap-2 mt-3"><button className={BTN} onClick={() => { setWork(null); setView('open'); }}>Cancel</button>
                <button className={BTN_PRIMARY} disabled={!work.plan.canCommit || busy} onClick={() => void confirmWork()}>{busyId ? 'Preparing...' : work.plan.action === 'open' ? 'Open local copy' : 'Create local copy'}</button></div>}
            </> : <div className="mt-3 text-xs" style={{ color: 'var(--sem-muted)' }}>Updating preview...</div>}
          </div>
        )}
      </section>
    );
  }

  // The frame: the header (title, Jobs, the one account button, close), then the page. It sits on the
  // 12-column grid (One Visual Language): every row spans all twelve columns.
  const body = (
    <div ref={panelRef} tabIndex={-1} className="hub-frame" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)' }}>
      <header className="hub-head">
        <h1 className="sem-page-title">Connections</h1>
        {/* Which model does which job. It replaces the Now strip: each row names its own job and the bar at the foot of
            Studio names them too. The id is the strip's old one, so the screenshot drivers still find it. */}
        <button type="button" className={`${BTN_QUIET} ml-auto`} data-testid="hub-now-change" aria-expanded={panel === 'change'} disabled={busy}
          onClick={() => { setMenuId(null); setJobPick(null); setView('open'); setPanel(panel === 'change' ? 'none' : 'change'); }}>Jobs</button>
        <div className="hub-acct">
          <button className="sem-btn max-w-[260px]" data-testid="hub-account-button" aria-haspopup="dialog" aria-expanded={panel === 'account'}
            onClick={() => { setMenuId(null); setPanel(panel === 'account' ? 'none' : 'account'); }} title="Your accounts">
            {signedInAccount && <span className="note-dot note-dot-ok" aria-hidden />}
            <span className="min-w-0 truncate">{signedInAccount || 'Sign in'}</span><span aria-hidden>{'▾'}</span>
          </button>
          {panel === 'account' && AccountMenu()}
        </div>
        <button onClick={onClose} className="sem-btn sem-btn-quiet hub-close" aria-label="Close Connections">{'×'}</button>
      </header>
      <div className="hub-main">
          {openConfirm && pendingOpen && (
            <div className="sem-card mb-3" data-testid="hub-open-unsaved">
              <div className="sem-card-title">This model has unsaved changes.</div>
              <div className="mt-2 text-xs leading-5 note-line note-warn">Opening another model throws those changes away unless you save first.</div>
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => { setOpenConfirm(false); setPendingOpen(null); }}>Cancel</button>
                <button className={BTN_PRIMARY} data-testid="hub-open-discard" disabled={busy} onClick={() => { const p = pendingOpen; if (p) void openModel(p); }}>Discard</button>
              </div>
            </div>
          )}
          {createOpen && (
            <div className="sem-card mb-3">
              <div className="sem-card-title">New blank model</div>
              <label className="sem-field-label mt-2">Model name
                <input data-testid="hub-create-name" value={createName} onChange={(e) => setCreateName(e.target.value)} placeholder="Sales analytics" autoFocus className="sem-field w-full" />
              </label>
              {createDirty && <div className="mt-2 text-xs leading-5 note-line note-warn">Creating {createName.trim() || 'a new model'} replaces the open model, which has unsaved changes.</div>}
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => { setCreateOpen(false); setCreateConfirm(false); }}>Cancel</button>
                <button className={BTN_PRIMARY} data-testid="hub-create-submit" disabled={!createName.trim() || busy} onClick={() => void submitCreate()}>{createDirty ? 'Create and replace open model' : 'Create model'}</button>
              </div>
            </div>
          )}
          {startSqlOpen && (
            <div className="sem-card mb-3 hub-start-sql" data-testid="hub-start-sql">
              <Suspense fallback={<div className="sem-text-meta" role="status">Opening the form...</div>}>
                <StartFromSqlForm pro={pro} onStarted={() => void startedFromSql()}
                  onAddOnConnections={() => { setStartSqlOpen(false); goTo('sqlsources'); }} />
              </Suspense>
              <div className="mt-3 flex justify-end">
                <button className={BTN} data-testid="hub-start-sql-cancel" onClick={() => setStartSqlOpen(false)}>Cancel</button>
              </div>
            </div>
          )}
          {switchTarget && <AccountDialog r={switchTarget} />}
          {!switchTarget && panel !== 'account' && defaultConfirm}
          {view === 'open' && panel === 'change' && JobsPanel()}
          {view === 'open' && panel !== 'change' && ListView()}
          {view === 'history' && HistoryView()}
          {view === 'add' && AddView()}
          {view === 'work' && WorkView()}
          {view === 'desktop' && <DesktopPicker onBack={() => setView('open')} onUseForTests={useDesktopForTests} onOpen={openDesktop} />}
          {view === 'workspace' && <WorkspacePicker account={signedInAccount} onBack={() => setView('open')}
            onPasteAddress={() => openAddView()} onConnect={connectPicked} />}
      </div>
      {error && !switchTarget && <div className="hub-error" role="alert">{error}</div>}
    </div>
  );

  // ONE shared floating frame for BOTH doors (P1). The hub is a centered floating dialog whether it overlays Studio or
  // is hosted full-page in the standalone panel; only the close callback differs (the caller wires overlay-close vs
  // panel-dispose). The backdrop dims + blurs; a click on it, or Escape, dismisses. The frame is sized from the window
  // (the space rule, 27 September 2026): it was a fixed 1130 x 790 at every size, so thirty saved models scrolled
  // inside it on a 2000 x 1200 screen with half the screen around it.
  return (
    <div className="fixed z-[80] grid place-items-center" role="dialog" aria-modal="true" aria-label="Connections"
      style={{ inset: '36px 0 23px', padding: 'var(--sem-space-4)', background: 'rgba(8,10,13,.66)', backdropFilter: 'blur(2px)' }}
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="relative flex flex-col overflow-hidden rounded-(--sem-radius-card) border"
        style={{ width: 'min(1760px, 92vw)', height: 'calc(100vh - 84px)', borderColor: 'var(--sem-border)' }}>
        {body}
      </div>
    </div>
  );
}

// One ⋯ menu item. A job this model cannot do stays in the menu, greyed, with the engine's reason under it.
function MenuItem({ children, onClick, disabled, danger, why }: { children: ReactNode; onClick: () => void; disabled?: boolean; danger?: boolean; why?: string | null }) {
  return (
    <button role="menuitem" disabled={disabled} onClick={onClick} className="hub-menu-item" data-danger={danger ? 'true' : undefined} data-refused={why ? 'true' : undefined}>
      <span>{children}</span>{why && <em className="hub-job-why">{why}</em>}
    </button>
  );
}

// Standalone host: main.tsx mounts this (instead of <App/>) when the host page sets window.__semanticusInitialView =
// 'connections'. The hub fills the dedicated panel (as the same shared floating frame); its close button disposes the
// panel via the host. An optional initial section lets "Manage Connections" land on the Change panel.
export function ConnectionsStandalone({ initialSection }: { initialSection?: HubView }) {
  // The Studio page names the model it shows on every request (App.tsx, drafts.ts setDraftModel), and the engine refuses a write from the extension that names none while a model is open
  // (drafts-sol round 3). This page runs without <App/>, so it names the model its own session read shows, the same way, or the hub's own writes (a SQL source, a label, a publish destination)
  // would all be refused whenever a model is open.
  const { session } = useConnection();
  setPageModelIdentity(session?.sessionId ? session.modelIdentity : undefined);
  // closeConnectionsPanel is a module-level import (stable identity), passed directly so the hub's focus/Escape effect
  // never retriggers on a re-render (a fresh arrow would). The hub disposes the panel via this on close.
  //
  // The permission card is mounted here as well as under App (Astra 4, 2026-09-22). With Studio closed, the hub can
  // still raise it, and a card mounted only inside App would leave the press moving a store nothing listens to.
  return (
    <>
      <ConsentCard />
      <ConnectionsHub open standalone initialView={initialSection} onClose={closeConnectionsPanel} />
    </>
  );
}
