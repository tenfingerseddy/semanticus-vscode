import { useEffect, useState, useRef, type ReactNode } from 'react';
import { openLocalModel, pickWorkingCopyFolder, rpc, onConnectionChange, onOpenConnections, announceConnectionChange, closeConnectionsPanel } from './bridge';
import { useConnection, type ConnectionContextModel, type SessionInfo } from './connection';
import { AccountPicker } from './accountpicker';
import { defaultSignInMode, SIGN_IN_MODES, SQL_SOURCE_COPY } from './copy';
import { useSurfaceEscape } from './surface-escape';

// The Connections HUB — the ratified end-state (TASKS.md [T160]/[T161]). It REPLACES the drawer as the single manager
// surface: a full-viewport overlay inside Studio, and the SAME component full-page in a dedicated webview when Studio is
// closed (standalone). Both doors host one component so the picker launches while the manager manages. All state moves
// through the existing engine ops (listConnections / connectionContext / probeConnectionAccounts / listConnectionHistory
// / prepareWorkingCopy / setPublishDestination / labelConnection / forgetConnection / connectXmla / createModel), so
// MCP-door parity, undo and broadcast semantics are untouched. Brand: the Ink + Signal-green token layer (--sem-*).
// Sizing follows sol's refinement spec: explicit control heights (30px standard / 24px compact / 24x24 overflow).

interface ConnectionRecord {
  id: string;
  kind: 'xmla' | 'localDesktop' | string;
  endpoint: string;
  database?: string;
  modelName?: string;
  tenantId?: string;
  authMode?: string;
  label?: string;
  workingFolder?: string;
  publishConnectionId?: string;
  lastUsedUtc?: string;
  lastAccount?: string;
  useCount?: number;
}

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
interface AuthPrereqRequirement { label: string; envNames: string[]; present: boolean; }
interface AuthPrerequisites { mode: string; ready: boolean; requirements: AuthPrereqRequirement[]; cliAccount?: string; cliTenant?: string; detail?: string; keyVaultSupported: boolean; }

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
// Sign-in-family events (identity), vs model events (connect / open / role). Drives the History type filter chips.
const isSigninEvent = (e: HistoryEvent) => e.kind === 'signin' || e.kind === 'switch';
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

export type HubView = 'open' | 'setup' | 'accounts' | 'history' | 'add' | 'work' | 'sqlsources';

// A caller inside Studio (e.g. Deploy's "Change publish destination") can ask the hub to open on a specific view
// next. The hub is mounted once in App with no initialView prop, so the request is a module-level flag set just
// before openConnections() flips `open`; the hub consumes and clears it on the open transition below.
let nextView: HubView | null = null;
export function openConnectionsOnView(view: HubView): void {
  nextView = view;
}

// ================================================================================================================
// THE FOURTH ROLE: named SQL sources.
//
// A check used to carry its own server, database and sign-in, so the same warehouse was typed again for every check
// (Astra's UAT, 2026-09-14), and a check authored with no sign-in mode reached the SQL token helper, which reads an
// absent mode as the Azure command line. A saved, named, tested record fixes both: an address is typed once here,
// and a check or a table mapping points at it by id, so a rename or a re-point never breaks a reference.
//
// These records hold NO credential. `authMode` is the NAME of a way to sign in, exactly like a model connection;
// there is no password field on this form and there never will be. The engine refuses a connection-string shape at
// its boundary and this form refuses one before the call, so a pasted password never leaves the box it was typed in.
//
// Engine ops (cp/tests-engine-b): listSqlSources / saveSqlSource / deleteSqlSource / testSqlSource, plus
// listTableSourceMappings for the table half. All four are the SAME engine path the agent door calls.
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
interface SqlSourceDeleteResult { deleted: boolean; note?: string; checksUsing?: number; tableMappingsUsing?: number; checkTitles?: string[]; mappedTables?: string[]; }
/** What uses one saved SQL source. The FREE projection behind list_sql_source_usage, and the whole of it:
 *  usage only, so no check definitions, no expected values, no saved verdicts and no row counts leak out of
 *  the now-Pro Tests feature. Connections is the only caller and this is all Connections needs. */
interface SqlSourceUsage { id: string; name: string; checksUsing: number; checkTitles: string[]; tableMappingsUsing: number; mappedTables: string[]; }
/** The Add / Edit form's own state. `id` null = a new source; an id = an edit of that record. */
interface SqlSourceForm { id: string | null; name: string; server: string; database: string; authMode: string; tenantId: string; }

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
  if (!name) return 'Give this source a name, so you can pick it by name in a check later.';
  if (!server) return 'Type the server address, for example contoso-sql.database.windows.net.';
  if (!database) return 'Type the database name.';
  if (/[;=]/.test(server) || /[;=]/.test(database))
    return 'Type just the server address and the database name, not a connection string. Semanticus signs you in and never keeps a password.';
  return null;
}

/**
 * The row's last-test line. "Never tested" is a THIRD state and stays one: a source nobody has tested has not
 * passed, and saying so is the difference between evidence and a guess. `whenText` is already-formatted words, so
 * this stays pure and the contract test can run it.
 */
function sqlSourceTestLine(lastTestOk: boolean | null | undefined, whenText: string): string {
  if (lastTestOk === null || lastTestOk === undefined) return 'Not tested yet';
  if (lastTestOk) return whenText ? `Last test worked on ${whenText}` : 'Last test worked';
  return whenText ? `Last test failed on ${whenText}` : 'Last test failed';
}

/**
 * "Used by 3 checks and 4 table mappings" is only said when it was really counted. Use is per MODEL, so with no
 * model open there is nothing to count; and when the table half could not be read, it is reported as uncounted
 * rather than rounded down to zero, which would read as "nothing points at this" and invite a removal.
 */
function sqlSourceUsageLine(checks: number | null, mappings: number | null, usageRead = true): string {
  // Three different nulls, and they must not print the same sentence. No usage reading at all means there is
  // no model to ask. A reading that came back WITHOUT this source, or without one of its counts, is a
  // partial answer: it is not zero use, and it must never be allowed to read as "nothing points at this",
  // because that is the sentence a person deletes a source on.
  if (!usageRead) return 'Open a model to see what uses this source.';
  if (checks === null && mappings === null) return 'What uses this source was not counted.';
  if (checks === null) return `Checks were not counted. Used by ${mappings} ${mappings === 1 ? 'table mapping' : 'table mappings'}.`;
  if (checks === 0 && mappings === 0) return 'Nothing in this model uses it yet.';
  const parts = [`${checks} ${checks === 1 ? 'check' : 'checks'}`];
  if (mappings !== null) parts.push(`${mappings} ${mappings === 1 ? 'table mapping' : 'table mappings'}`);
  const line = `Used by ${parts.join(' · ')}`;
  return mappings === null ? `${line}. Table mappings were not counted.` : line;
}

/**
 * The names behind a refusal's count. The engine counts what still points at a source; the names come from the
 * saved checks and the table mappings the hub already holds. Where the hub knows fewer names than the engine
 * counted, it says how many are missing rather than showing a short list as though it were the whole list.
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
 * Takes the openConnections from useConnection() so this module stays free of the connection context, exactly the
 * shape Deploy's "Change publish destination" already uses.
 */
export function openSqlSources(openConnections: () => void): void {
  openConnectionsOnView('sqlsources');
  openConnections();
}

/** The saved SQL sources, for a check's source picker on the Tests page. One engine op, no second store. */
export function listSqlSources(): Promise<SqlSourceRecord[]> {
  return rpc<SqlSourceRecord[]>('listSqlSources');
}

const inputStyle = { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' } as const;
// Button scale system (sol's refinement): explicit heights remove inherited line-height / host-scaling inflation, the
// root cause of oversized buttons. Standard 30px, compact 24px, quiet 24px link, overflow 24x24.
const BTN = 'inline-flex h-[30px] items-center justify-center gap-1.5 rounded-[5px] border px-2.5 text-[11px] leading-none font-semibold disabled:opacity-40';
const BTN_COMPACT = 'inline-flex h-6 min-w-0 items-center justify-center gap-1 rounded border px-2 text-[10px] leading-none font-semibold disabled:opacity-40';
const BTN_QUIET = 'inline-flex h-6 items-center justify-center gap-1 rounded px-1.5 text-[10px] leading-none font-semibold disabled:opacity-40';
const BTN_OVERFLOW = 'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded border p-0 text-[16px] leading-none font-semibold disabled:opacity-40';
// ---- naming a remembered connection ---------------------------------------------------------------------------
// A remembered connection reads as WORDS, never as an address. Kane's list titled a published model
// "Contoso%20Fabric%20Monitoring" (live test, 2026-09-15; Contoso stands in for the real tenant name, which the
// public-mirror gate refuses to carry): a WORKSPACE-level record with no dataset, so the name fell
// through to the encoded last path segment, in the row, the detail title, the Model row and the Open now panel alike.
// These are the ONE place a name is decided, and they mirror the engine's ConnectionRegistry.WorkspaceNameFromEndpoint
// rule for rule so a target is one string on both doors. Kept as top-level pure functions so the contract test can
// extract and actually RUN them, rather than only matching the source text.
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
// The row's SECOND line. A workspace says what it is, because Open there means picking a model first; a real model
// names the workspace it lives in and then who the next open signs in as, so the ratified identity line (HIGH 3) is
// still on every row you can actually open a model from. ONE line for every kind, deliberately: the row height is what
// the nine-models-without-a-scrollbar check measures, so a second line would move a release gate sideways.
function subtitleOf(r: ConnectionRecord, identity: string): string {
  if (r.kind === 'file') return endpointText(r.endpoint);
  if (r.kind === 'localDesktop') return decodeSegment(short(r.endpoint)) || 'Local running model';
  if (isWorkspaceOnly(r)) return 'Workspace · you pick the model when you open it';
  const workspace = workspaceNameFromEndpoint(r.endpoint);
  return workspace ? `In ${workspace} · ${identity}` : identity;
}
// The detail card's Model row. A workspace has no model chosen yet, and saying so is better than repeating the
// workspace name in the Model slot as if one had been.
function modelRowText(r: ConnectionRecord): string {
  if (isWorkspaceOnly(r)) return 'You pick the model when you open this workspace';
  if (r.database && !isGuidName(r.database)) return r.database;
  return nameOf(r);
}
// An endpoint shown as DETAIL is text for a person to read, not a string to copy, so it is decoded too.
function endpointText(endpoint?: string | null): string {
  return decodeSegment(endpoint || '');
}
// Up to two initials for the identity avatar, from a UPN (megan@contoso.com -> K) or a "first.last" local part.
const initials = (account?: string): string => {
  const local = (account || '').split('@')[0];
  const parts = local.split(/[.\-_ ]+/).filter(Boolean);
  if (!parts.length) return '?';
  return (parts[0][0] + (parts[1]?.[0] ?? '')).toUpperCase();
};

// Environment reads a fail-closed label. An UNLABELLED cloud model reads "Production safeguards", never "prod": a
// fail-closed governance posture is a different fact from a declared Production label (ratified, sol).
function environment(r: ConnectionRecord): { text: string; tone: 'accent' | 'warn' | 'muted' } {
  if (r.kind === 'localDesktop' || r.kind === 'file' || r.label === 'local') return { text: 'Local', tone: 'muted' };
  const l = (r.label || '').toLowerCase();
  if (l === 'dev') return { text: 'Development', tone: 'accent' };
  if (l === 'uat') return { text: 'UAT', tone: 'accent' };
  if (l === 'prod') return { text: 'Production', tone: 'warn' };
  return { text: 'Production safeguards', tone: 'warn' };
}
function envBadgeStyle(tone: 'accent' | 'warn' | 'muted'): React.CSSProperties {
  if (tone === 'accent') return { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' };
  if (tone === 'warn') return { background: 'color-mix(in srgb, var(--sem-warn) 16%, transparent)', color: 'var(--sem-warn)', borderColor: 'color-mix(in srgb, var(--sem-warn) 40%, transparent)' };
  return { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' };
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

// The environment label options. The unlabelled value is named "Not labelled (Production safeguards)", short enough that
// the compact detail-pane select never truncates the selected value (L3).
const ENV_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Not labelled (Production safeguards)' },
  { value: 'dev', label: 'Development' },
  { value: 'uat', label: 'UAT' },
  { value: 'prod', label: 'Production' },
  { value: 'local', label: 'Local' },
];

function EnvBadge({ r }: { r: ConnectionRecord }) {
  const env = environment(r);
  return <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] leading-none font-semibold uppercase tracking-[0.05em]" style={envBadgeStyle(env.tone)}>{env.text}</span>;
}

// One role card in the Current setup grid (Editing / Tests and queries / Publish to). A card either offers a
// "Change ..." action (routing to Open a model) OR an inline control (the publish picker, which owns that role
// since publish left the Open view entirely).
function RoleCard({ n, label, value, detail, missing, action, onAction, control }:
  { n: number; label: string; value: string; detail: string; missing?: boolean; action?: string; onAction?: () => void; control?: ReactNode }) {
  return (
    <article className="rounded-md border p-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="flex items-center gap-2">
        <span className="flex h-5 w-5 items-center justify-center rounded-full border text-[9px] font-bold" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 55%, var(--sem-border))', color: 'var(--sem-accent)' }}>{n}</span>
        <h3 className="text-[10px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>{label}</h3>
      </div>
      <div className="mt-2.5 text-[13px] font-semibold truncate" style={{ color: missing ? 'var(--sem-muted)' : 'var(--sem-fg)' }} title={value}>{value}</div>
      <div className="mt-1 min-h-6 text-[9px] leading-[14px]" style={{ color: 'var(--sem-muted)' }}>{detail}</div>
      {control ? <div className="mt-1.5">{control}</div> : action && <button className={`${BTN} mt-1.5`} style={inputStyle} onClick={onAction}>{action}</button>}
    </article>
  );
}

// A scroll region that SHOWS it has more below it. The hub is a fixed-height dialog, so the detail column really does
// scroll — but this platform paints an OVERLAY scrollbar (measured: offsetWidth - clientWidth === 0, and nothing is
// drawn at rest), so a card cut at the pane's bottom edge read as clipped rather than as scrollable. The fade is drawn
// only while content remains below, so it never claims there is more when there is not. Display only: no pointer
// events, no change to what the pane contains.
function ScrollCue({ className, children }: { className?: string; children: ReactNode }) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [more, setMore] = useState(false);
  useEffect(() => {
    const el = ref.current; if (!el) return;
    const read = () => setMore(el.scrollHeight - el.clientHeight - el.scrollTop > 1);
    read();
    el.addEventListener('scroll', read, { passive: true });
    const ro = new ResizeObserver(read);
    ro.observe(el);
    for (const child of Array.from(el.children)) ro.observe(child);
    return () => { el.removeEventListener('scroll', read); ro.disconnect(); };
  });
  return (
    <div className="relative flex flex-col min-[900px]:min-h-0 min-[900px]:flex-1">
      <div ref={ref} className={className}>{children}</div>
      <div aria-hidden className="pointer-events-none absolute inset-x-0 bottom-0 h-6 transition-opacity duration-150"
        style={{ opacity: more ? 1 : 0, background: 'linear-gradient(to top, var(--sem-surface), transparent)' }} />
    </div>
  );
}

export function ConnectionsHub({ open, onClose, standalone = false, initialView }: { open: boolean; onClose: () => void; standalone?: boolean; initialView?: HubView }) {
  const { context, session, connectLocal, connectXmla, refresh } = useConnection();
  const [view, setView] = useState<HubView>(initialView ?? 'open');
  const [records, setRecords] = useState<ConnectionRecord[]>([]);
  const [probes, setProbes] = useState<AccountProbe[]>([]);
  const [profiles, setProfiles] = useState<AccountProfile[]>([]);   // the saved Microsoft identities on this device (Phase 2)
  const [loading, setLoading] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [menuId, setMenuId] = useState<string | null>(null);           // the row whose overflow menu is open
  const [query, setQuery] = useState('');
  const [work, setWork] = useState<WorkSetup | null>(null);
  const [switchTarget, setSwitchTarget] = useState<ConnectionRecord | null>(null);   // the model whose per-open account choice dialog is open (Phase 2)
  const [switchPurpose, setSwitchPurpose] = useState<'open' | 'query' | null>(null);   // HIGH (sol): the EXPLICIT intent behind a stale-model account choice (Open vs Query). A stale model is neither the editing nor the query connection, so applyAccount cannot infer intent from its role - it MUST honour the verb the user clicked. null = a generic "choose account" (role-inferred).
  const [failedAuthAttempt, setFailedAuthAttempt] = useState<FailedAuthAttempt | null>(null); // attempt-local; never marks a saved profile globally signed out
  const [pendingDefault, setPendingDefault] = useState<AccountProfile | null>(null);  // a make-default awaiting its blast-radius confirmation
  // The account row the chooser's PRIMARY button acts on. The primary must never contradict the list (a row that needs a
  // sign-in cannot be answered by a silent retry of the same expired credential), so it follows this selection. null =
  // no explicit pick yet, and the dialog falls back to the failed row, then the signed-in default.
  const [pickedProfileId, setPickedProfileId] = useState<string | null>(null);
  const [endpoint, setEndpoint] = useState('');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState('interactive');
  const [newLabel, setNewLabel] = useState('');
  const [newTenant, setNewTenant] = useState('');   // optional tenant for command-line / service-identity adds (threads to the query tenant so az's home tenant can't silently decide)
  const [prereq, setPrereq] = useState<AuthPrerequisites | null>(null);   // the live auth-prerequisite probe for the selected advanced sign-in method (names/presence only, never secret values)
  const [prereqLoading, setPrereqLoading] = useState(false);
  const [history, setHistory] = useState<HistoryEvent[]>([]);
  const [historyKind, setHistoryKind] = useState<'all' | 'model' | 'signin'>('all');
  const [historyConn, setHistoryConn] = useState<string | null>(null);   // an optional per-connection filter (set from a row's "View history")
  const [createOpen, setCreateOpen] = useState(false);   // the guarded "Create new model" surface
  const [createName, setCreateName] = useState('');
  const [createConfirm, setCreateConfirm] = useState(false);   // second-step consent when the open model has unsaved changes
  const [openConfirm, setOpenConfirm] = useState(false);       // second-step consent when opening another model over unsaved work
  const [pendingOpen, setPendingOpen] = useState<ConnectionRecord | 'path' | null>(null);
  const [editEnv, setEditEnv] = useState(false);   // the detail pane's environment-label editor is behind an explicit control
  const [localPath, setLocalPath] = useState('');
  const [localPathError, setLocalPathError] = useState<string | null>(null);
  // ---- the fourth role: named SQL sources ------------------------------------------------------------------------
  const [sqlSources, setSqlSources] = useState<SqlSourceRecord[]>([]);
  const [sqlLoading, setSqlLoading] = useState(false);
  const [sqlError, setSqlError] = useState<string | null>(null);          // the LIST could not be read (not a form error)
  const [sqlUsage, setSqlUsage] = useState<SqlSourceUsage[] | null>(null); // per-source usage; null = not counted (no model open, or the read failed)
  const [sqlForm, setSqlForm] = useState<SqlSourceForm | null>(null);     // the Add / Edit form, or null when closed
  const [sqlFormError, setSqlFormError] = useState<string | null>(null);  // what the form itself refused, or what the engine said
  const [sqlBusy, setSqlBusy] = useState<string | null>(null);            // 'save' | 'test' | 'remove' | a source id being tested
  const [sqlTest, setSqlTest] = useState<SqlSourceTestResult | null>(null);   // the last Test connection outcome
  const [sqlRemoving, setSqlRemoving] = useState<SqlSourceRecord | null>(null);  // a Remove awaiting its confirmation
  const [sqlRefused, setSqlRefused] = useState<SqlSourceDeleteResult | null>(null); // a Remove the engine refused, with names
  const [sqlOpenId, setSqlOpenId] = useState<string | null>(null);        // the row whose test result is showing

  const shown = open || standalone;
  // Focus the Open-view search when the hub opens on Open a model (the tree "Open Model" lands here with search ready).
  // A ref flag the search input's callback ref consumes once, so it never steals focus on an ordinary re-render.
  const searchWanted = useRef(true);
  // A monotonically-bumped token for host "open" requests that arrive while the hub is ALREADY shown. The focus effect
  // below keys on [shown, onClose] and will NOT re-run in that case, so searchWanted alone never re-focuses search; a
  // dedicated effect keyed on this token does. It is left at 0 on the initial mount (owned by the opener-capture effect).
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
      setSelectedId((cur) => cur && (recs ?? []).some((r) => r.id === cur) ? cur : ((recs ?? [])[0]?.id ?? null));
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setLoading(false); }
  };
  const loadHistory = async (conn: string | null) => {
    try { setHistory((await rpc<HistoryEvent[]>('listConnectionHistory', conn || null)) ?? []); }
    catch { setHistory([]); }
  };
  const reloadHistory = async () => { if (view === 'history') await loadHistory(historyConn); };

  // ---- named SQL sources: load, save, test, remove ----------------------------------------------------------------
  // The list itself is machine-local and always readable. What USES a source is per model, so both use-counts come
  // from the open model and stay NULL when there is no model to ask: a failed count must never render as a zero,
  // because "nothing points at this" is exactly the sentence that invites a removal.
  const loadSqlSources = async () => {
    setSqlLoading(true);
    try {
      const recs = await rpc<SqlSourceRecord[]>('listSqlSources');
      setSqlSources(recs ?? []);
      setSqlError(null);
    }
    catch (e) { setSqlError(String((e as Error).message ?? e)); setSqlSources([]); }
    finally { setSqlLoading(false); }
  };
  // ONE free read, not two Pro ones. This used to call listTests for the per-check use and listTableMappings
  // for the per-table mappings. Both belong to the Tests feature now, and both return far more than a use
  // count: whole check definitions with their expected values, and saved verdicts with row counts. Neither
  // may become free to keep a Connections label working, so the engine offers the usage projection instead.
  // It still fails on its own: a usage read that cannot answer leaves the counts NULL and the list standing,
  // because a failed count rendered as a zero is the exact sentence that invites a removal.
  // The engine door returns a bare array (EngineRpcTarget.listSqlSourceUsage). Asking for a `{ sources }`
  // wrapper read `undefined` off every successful answer, so the page showed "not counted" beside sources it
  // had just been told the real counts for. An answer in a shape this page does not recognise is not counted
  // either: it falls to null, never to zero.
  const loadSqlUse = async () => {
    try {
      const rows = await rpc<SqlSourceUsage[]>('listSqlSourceUsage');
      setSqlUsage(Array.isArray(rows) ? rows : null);
    }
    catch { setSqlUsage(null); }
  };
  // A source the projection did not mention, or a count it did not carry, is NOT zero use. Zero is the one
  // sentence that invites a removal, and inventing it from a partial answer is how a source that three saved
  // checks depend on gets deleted. Both fall back to null, which the row renders as "not counted".
  const usageFor = (id: string): SqlSourceUsage | undefined => (sqlUsage ?? []).find((u) => u.id === id);
  const countOf = (id: string, pick: (u: SqlSourceUsage) => number | undefined): number | null => {
    if (sqlUsage === null) return null;            // the read failed outright
    const row = usageFor(id);
    if (!row) return null;                          // a successful but PARTIAL answer: this source was absent
    const value = pick(row);
    return typeof value === 'number' ? value : null; // present, but this count was not carried
  };
  const checksUsing = (id: string): number | null => countOf(id, (u) => u.checksUsing);
  const mapsUsing = (id: string): number | null => countOf(id, (u) => u.tableMappingsUsing);

  const openSqlForm = (r?: SqlSourceRecord) => {
    setSqlFormError(null); setSqlTest(null); setSqlRefused(null); setSqlRemoving(null);
    setSqlForm(r
      ? { id: r.id, name: r.name || '', server: r.server || '', database: r.database || '', authMode: defaultSignInMode(r), tenantId: r.tenantId || '' }
      : { id: null, name: '', server: '', database: '', authMode: 'interactive', tenantId: '' });
  };
  const closeSqlForm = () => { setSqlForm(null); setSqlFormError(null); setSqlTest(null); };

  // Save validates the obvious FIRST, so a missing name never becomes a round trip, and a pasted connection string
  // never leaves this webview at all. The engine's own refusals are shown exactly as it worded them.
  const saveSqlSource = async () => {
    if (!sqlForm) return;
    const refusal = sqlSourceFormError(sqlForm);
    if (refusal) { setSqlFormError(refusal); return; }
    setSqlBusy('save'); setSqlFormError(null);
    try {
      const saved = await rpc<SqlSourceRecord>('saveSqlSource', sqlForm.id || null, sqlForm.name.trim(), sqlForm.server.trim(),
        sqlForm.database.trim(), sqlForm.authMode, sqlForm.tenantId.trim() || null, 'human');
      setSqlForm(null); setSqlTest(null);
      if (saved?.id) setSqlOpenId(saved.id);   // keep the saved row in view, so a Save does not lose the person's place
      await loadSqlSources();
    }
    catch (e) { setSqlFormError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); }
  };

  // Test connection asks the engine to sign in with the SOURCE's own mode and ask for one constant. A failure is a
  // RESULT here, not an exception: the note is the plain-words reason, and the date moves either way.
  const testSqlSource = async (r: SqlSourceRecord) => {
    setSqlBusy(r.id); setSqlTest(null); setSqlRefused(null);
    try {
      const result = await rpc<SqlSourceTestResult>('testSqlSource', r.id, 'human');
      setSqlTest(result ?? { ok: false, note: 'The test finished without saying anything.' });
    }
    catch (e) { setSqlTest({ id: r.id, name: r.name, ok: false, note: String((e as Error).message ?? e) }); }
    finally { setSqlBusy(null); await loadSqlSources(); }
  };

  // Testing the form's values before the record exists would need an unsaved probe the engine does not offer, so the
  // form saves first and then tests the saved record. Said out loud on the button rather than implied.
  const saveAndTestSqlSource = async () => {
    if (!sqlForm) return;
    const refusal = sqlSourceFormError(sqlForm);
    if (refusal) { setSqlFormError(refusal); return; }
    setSqlBusy('test'); setSqlFormError(null);
    try {
      const saved = await rpc<SqlSourceRecord>('saveSqlSource', sqlForm.id || null, sqlForm.name.trim(), sqlForm.server.trim(),
        sqlForm.database.trim(), sqlForm.authMode, sqlForm.tenantId.trim() || null, 'human');
      setSqlForm((f) => f ? { ...f, id: saved?.id ?? f.id } : f);
      const result = await rpc<SqlSourceTestResult>('testSqlSource', saved.id, 'human');
      setSqlTest(result ?? { ok: false, note: 'The test finished without saying anything.' });
    }
    catch (e) { setSqlFormError(String((e as Error).message ?? e)); }
    finally { setSqlBusy(null); await loadSqlSources(); }
  };

  // Remove is refused while anything still points at the source, and the refusal NAMES what does. The engine returns
  // both the counts and the sorted names; we render the names, and say how many were not named when the two disagree.
  const removeSqlSource = async (r: SqlSourceRecord) => {
    setSqlBusy(r.id); setSqlRefused(null);
    try {
      const result = await rpc<SqlSourceDeleteResult>('deleteSqlSource', r.id, 'human');
      if (result?.deleted) { setSqlRemoving(null); setSqlRefused(null); }
      else { setSqlRemoving(r); setSqlRefused(result ?? { deleted: false }); }   // the record stays, so the refusal can name it
    }
    catch (e) { setSqlRefused({ deleted: false, note: String((e as Error).message ?? e) }); }
    finally { setSqlBusy(null); await loadSqlSources(); }
  };

  const probeOf = (r: ConnectionRecord): AccountProbe | undefined => probes.find((p) => p.id === r.id);
  const accountOf = (r: ConnectionRecord): string | undefined => probeOf(r)?.account;

  useEffect(() => { if (shown) { void load(); void refresh(); } }, [shown]); // eslint-disable-line react-hooks/exhaustive-deps
  // A pending "open on this view" request (openConnectionsOnView) lands the hub on the requested section when it opens.
  useEffect(() => {
    if (!shown || !nextView) return;
    setView(nextView);
    nextView = null;
  }, [shown]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (shown && view === 'history') void loadHistory(historyConn); }, [shown, view, historyConn]); // eslint-disable-line react-hooks/exhaustive-deps
  // The list is a local file read and the Current setup role card states its count, so it loads with the hub. What
  // USES a source is per model and costs two more calls, so that is read only when the section itself opens.
  useEffect(() => { if (shown) void loadSqlSources(); }, [shown]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (shown && view === 'sqlsources') void loadSqlUse(); }, [shown, view]); // eslint-disable-line react-hooks/exhaustive-deps
  // A connection-state change from OUTSIDE this hub's own RPCs (an MCP-door connect/disconnect relayed from
  // model/activity, or a reference set/clear the host just completed) reloads every panel so none goes stale.
  useEffect(() => {
    if (!shown) return;
    return onConnectionChange(() => { void load(); void refresh(); if (view === 'history') void loadHistory(historyConn); });
  }, [shown, view, historyConn]); // eslint-disable-line react-hooks/exhaustive-deps
  // The host can ask the hub to jump to a section (the tree "Manage Connections" -> Current setup, "Add published
  // model" from the quick pick -> Add). Both doors receive the SAME host->webview message, so overlay and standalone
  // converge on one behaviour; an "open" landing re-arms the search focus.
  useEffect(() => onOpenConnections((section) => {
    if (section === 'open' || section === 'setup' || section === 'accounts' || section === 'history' || section === 'add' || section === 'sqlsources') {
      // A host-driven Add (the native "Add published model") must land on a FRESH form, not whatever advanced method +
      // tenant a prior visit left behind (MED, sol) - the same reset openAddView applies to the in-webview CTAs.
      if (section === 'add') { setAuthMode('interactive'); setNewTenant(''); }
      setView(section);
      if (section === 'open') { searchWanted.current = true; setOpenToken((t) => t + 1); }
    }
  }), []); // eslint-disable-line react-hooks/exhaustive-deps

  // aria-modal promises an inert background, so deliver one: focus moves INTO the hub on open, Tab cycles inside it,
  // Escape dismisses, and closing returns focus to whatever opened it. The shared floating frame gives both doors the
  // same dialog, so the trap and Escape run in BOTH modes (no standalone guard). ORDER MATTERS: the opener is captured
  // BEFORE any hub focus moves, so restore never targets an element inside the closing hub; only then do we focus the
  // search box (search-first on Open a model, via a plain ref — never a focus-in-ref-callback) or the panel. onClose is
  // a stable identity (useCallback / module import), so this effect runs only on open/close, not on every re-render.
  const panelRef = useRef<HTMLDivElement | null>(null);
  const restoreRef = useRef<HTMLElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  const localPathRef = useRef<HTMLInputElement | null>(null);
  // A ref mirror of the open overflow menu, read by the modal Escape handler so Escape closes an OPEN row menu first
  // and only then the whole dialog (a ref, not a dep, so the focus effect never re-runs when a menu opens/closes).
  const menuIdRef = useRef<string | null>(null);
  useEffect(() => { menuIdRef.current = menuId; }, [menuId]);
  // A new account chooser (or a new purpose) starts with no explicit row pick, so the primary button falls back to
  // the row that failed, then the signed-in default. A pick can never leak from a previous target.
  useEffect(() => { setPickedProfileId(null); }, [switchTarget?.id, switchPurpose]);
  // The trigger that opened the current overflow menu, captured on open so focus can return to it on close (Escape or a
  // selection unmounts the focused menuitem — without this, focus would orphan on <body>).
  const menuTriggerRef = useRef<HTMLButtonElement | null>(null);
  // When a row's overflow menu opens, move focus into it (ARIA menu semantics; Arrow/Home/End are handled on it). When
  // it closes, return focus to the trigger that opened it, never orphaning focus on <body>.
  useEffect(() => {
    if (menuId) { document.querySelector<HTMLButtonElement>('[data-hub-menu] [role="menuitem"]')?.focus(); return; }
    if (menuTriggerRef.current) { menuTriggerRef.current.focus(); menuTriggerRef.current = null; }
  }, [menuId]);
  // Escape belongs to whatever is on TOP. The hub opens OVER the Tests drawer, and both used to close on
  // the same key, so adding a SQL source from inside a half-finished check threw the check away (Astra,
  // 2026-09-15, P1). The hub opens last, so it is the top surface and takes the key; the drawer gets it
  // back the moment the hub closes. An open row menu is still dismissed first: that is inside this surface.
  useSurfaceEscape(() => { if (menuIdRef.current) setMenuId(null); else onClose(); }, shown);
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
  // Re-focus search when an "open" request arrives while the hub is already shown (the token bumped above). Runs only
  // for token>0 so the initial mount stays owned by the opener-capture effect (which captures the opener BEFORE any hub
  // focus — reintroducing focus there would be the old HIGH bug). Targets searchRef only when the Open view is showing.
  useEffect(() => {
    if (openToken === 0 || view !== 'open') return;
    searchRef.current?.focus();
  }, [openToken]); // eslint-disable-line react-hooks/exhaustive-deps
  // Live auth prerequisites for an ADVANCED sign-in method (command-line / service identity): probe the engine for what
  // it can actually see — the detected `az login` account, or which service-principal inputs are present on the host —
  // so choosing the method previews whether it will work instead of only failing at Connect. Names/presence only, never
  // a secret value; debounced on the tenant field; a probe failure falls back to static guidance (never a dead form).
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

  const editing = context?.editing;
  const querying = context?.querying;
  const publishing = context?.publishing;
  const xmlaRecords = records.filter((r) => r.kind === 'xmla');
  const currentAccount = session?.currentAccount;
  const currentTenant = session?.currentTenant;
  // ONE signed-in identity for the hub: the live session account when present, else the saved default (or any
  // signed-in) profile the engine already served. Connections, the chooser and the footer chip all read this so a
  // remembered sign-in is never shown as unknown after a restart that has not yet reattached a live model.
  const signedInAccount = currentAccount
    || profiles.find((p) => p.isDefault && p.signedIn)?.username
    || profiles.find((p) => p.signedIn)?.username;
  const signedInTenant = currentTenant
    || profiles.find((p) => p.username === signedInAccount)?.tenantId;
  const selected = records.find((r) => r.id === selectedId) || records[0];
  // "Switch account" acts on the live model in play (editing side, else querying), and only when it is a published
  // target signed in via a switchable family (a local model uses Windows auth; azcli / service principal have no picker).
  const liveConnId = (editing?.live && editing?.kind !== 'localDesktop' ? editing?.connectionId : null) || querying?.connectionId;
  const switchable = records.find((r) => r.kind === 'xmla' && r.id === liveConnId && credentialFamily(r.authMode) != null);

  // ---- mutations (every one goes through an existing engine op) -------------------------------------------------
  // CLOSE CONTRACT (Kane's simplify guidance, ONE behavior everywhere): a SUCCESSFUL terminal action - open live,
  // query attach, working-copy open, create - closes the dialog in BOTH modes (overlay close / standalone panel
  // dispose, via the shared onClose). Actions that keep you in the manager - label change, forget, make-default,
  // add-and-remember, section nav - never close.
  // opts carries the Phase 2 per-open account choice: accountProfileId pins a SAVED account for this open only (never
  // repoints the tenant default); forceReauth adds a NEW account via the Microsoft picker; makeDefault repoints the
  // tenant default (an explicit human choice with a stated blast radius). A failed authorization stays on the account
  // choice (the dialog reopens), never an endpoint form.
  // Keep the target and the user's purpose when a remembered XMLA operation cannot authorize. A saved profile is
  // identity metadata only; it may still need a fresh sign-in. Returning to this chooser preserves the endpoint,
  // the open/query distinction, the tenant default, and the editing session while giving the user an explicit retry.
  const recoverAccountChoice = (r: ConnectionRecord, purpose: 'open' | 'query') => {
    setSwitchPurpose(purpose);
    setSwitchTarget(r);
    setPickedProfileId(null);   // a fresh failure re-selects the row that failed, so the primary button names it
  };
  const openModel = async (r: ConnectionRecord, opts?: { forceReauth?: boolean; accountProfileId?: string; makeDefault?: boolean; loginHint?: string }, purpose: 'open' | 'query' = 'open') => {
    setBusyId('open:' + r.id); setError(null); setMenuId(null);
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
      setError(failure);
      await reloadHistory();   // a FAILED open also appends a timeline event (a cancelled sign-in / superseded open)
      if (isSwitchableXmla(r) && isAuthRequiredFailure(failure)) {
        markFailedAuthAttempt(r, purpose, opts?.accountProfileId, opts?.loginHint);
        recoverAccountChoice(r, purpose);   // only auth failures enter account recovery
      }
    }
    finally { setBusyId(null); }
  };
  // Route an account choice by the ROLE the target plays (item 8). A QUERY-only live model switches ONLY its query
  // connection (connectXmla) and NEVER replaces the editing session; every other target opens/reopens for editing
  // (openLive). Routing on the target's role is the safe default: switching the query account can never clobber the
  // editing session, and reopening the editing model as a different account is still an edit-open.
  const isQueryRole = (r: ConnectionRecord): boolean =>
    querying?.connectionId === r.id && !(editing?.live && editing?.connectionId === r.id && editing?.kind !== 'localDesktop');
  // Open the shared per-open account dialog with an EXPLICIT intent (HIGH, sol): a stale-model Open/Query knows what it
  // wants, so applyAccount honours the verb the user clicked rather than re-inferring from the pre-op role (which, for a
  // not-yet-connected model, is neither editing nor querying and would silently fall through to an edit-open). null =
  // a generic "choose account" that stays role-inferred (a live query model query-switches; an editing model reopens).
  const openAccountChoice = (r: ConnectionRecord, purpose: 'open' | 'query' | null = null) => { setError(null); setFailedAuthAttempt(null); setSwitchPurpose(purpose); setSwitchTarget(r); };
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
      onClose();
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

  const queryWith = async (r: ConnectionRecord) => {
    if (r.kind === 'file') {
      setError('A local file cannot answer queries by itself. Choose a running local model or a published model.');
      return;
    }
    setBusyId('query:' + r.id); setError(null); setMenuId(null);
    const res = r.kind === 'localDesktop'
      ? await connectLocal(r.endpoint)
      : await connectXmla(r.endpoint, r.database || '', r.authMode || 'interactive', r.tenantId || null);
    const failure = !res.ok ? (res.message || 'That model could not be connected for queries.') : null;
    await load(); await reloadHistory(); setBusyId(null);
    if (res.ok) onClose();   // query attach is a terminal action — close in both modes (the contract above)
    else {
      setError(failure);
      if (isSwitchableXmla(r) && isAuthRequiredFailure(failure)) {
        markFailedAuthAttempt(r, 'query');
        recoverAccountChoice(r, 'query');
      } else setFailedAuthAttempt(null);
    }
  };
  // The "Running desktop model" quick card discovers and query-attaches a running local model. It routes through this
  // helper (never a bare rpc) so a SUCCESSFUL attach honours the close contract: await the attach + refresh, THEN
  // onClose — the SAME completion-then-close order as queryWith, so the close can never race the refresh.
  const connectRunningLocal = async () => {
    setBusyId('query:local'); setError(null); setMenuId(null);
    const res = await connectLocal(null);
    if (!res.ok) setError(res.message || 'No running local model could be connected for queries.');
    await load(); await reloadHistory(); setBusyId(null);
    if (res.ok) onClose();   // discover + query-attach is a terminal action — close in both modes (the contract above)
  };

  const setPublish = async (r: ConnectionRecord) => {
    setBusyId('publish:' + r.id); setError(null); setMenuId(null);
    try { await rpc('setPublishDestination', r.id, 'human'); await refresh(); await load(); await reloadHistory(); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const relabel = async (r: ConnectionRecord, label: string) => {
    setBusyId('label:' + r.id); setError(null);
    try { await rpc('labelConnection', r.id, label || null); await load(); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const forget = async (r: ConnectionRecord) => {
    setBusyId('forget:' + r.id); setError(null); setMenuId(null);
    try {
      await rpc('forgetConnection', r.id);
      await load(); await refresh(); await reloadHistory();
      setSelectedId((cur) => (cur === r.id ? null : cur));
    }
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
      if (!res.ok) { setError(res.message || 'The connection did not complete. Check the endpoint, model name, and sign-in.'); return; }   // show the engine's actual reason (e.g. the service-principal env-var help)
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

  // B1: "Create new model" is guarded. The engine swaps the open model UNCONDITIONALLY, so the hub must (a) take a
  // name (never a hardcoded default) and (b) re-read the live session at submit time and require an explicit second
  // consent when the open model has unsaved changes — the same guard the native picker applies, in BOTH hosting modes.
  const submitCreate = async () => {
    const name = createName.trim();
    if (!name) return;
    setBusyId('create'); setError(null);
    try {
      const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
      if (s?.hasUnsavedChanges && !createConfirm) { setCreateConfirm(true); return; }
      await rpc('createModel', name, 1604, !!s?.hasUnsavedChanges);
      await refresh();
      setCreateOpen(false); setCreateName(''); setCreateConfirm(false);
      onClose();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };
  const openCreate = () => { setCreateName(''); setCreateConfirm(false); setError(null); setCreateOpen(true); };
  const rejectLocalPath = () => {
    setLocalPathError('Please enter a path that exists.');
    localPathRef.current?.focus();
  };
  const openTypedLocal = async () => {
    const p = localPath.trim();
    if (!p) { rejectLocalPath(); return; }
    setBusyId('open:path'); setError(null); setLocalPathError(null);
    try {
      const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
      if (s?.hasUnsavedChanges && !openConfirm) { setPendingOpen('path'); setOpenConfirm(true); setBusyId(null); return; }
      await rpc('open', p, !!s?.hasUnsavedChanges);
      await refresh(); await load();
      setLocalPath('');
      setOpenConfirm(false); setPendingOpen(null);
      onClose();
    } catch (e) {
      const msg = String((e as Error).message ?? e);
      if (/not found|does not exist|could not find|no such file|cannot find/i.test(msg)) rejectLocalPath();
      else { setLocalPathError(msg); localPathRef.current?.focus(); }
    } finally { setBusyId(null); }
  };
  // Navigate FRESH to the Add view with a known sign-in method (MED, sol): a browser "Sign in" CTA must land on Browser
  // sign-in, not whatever advanced method was last selected, and must never carry a hidden stale tenant into the connect.
  // The method cards pass their own mode; a plain Sign in / Add resets to interactive.
  const openAddView = (mode: string = 'interactive') => { setAuthMode(mode); setNewTenant(''); setError(null); setView('add'); };
  const createDirty = !!session?.hasUnsavedChanges || createConfirm;

  // How many remembered models a make-default would repoint: the set on the SAME tenant + sign-in family, which is the
  // slot the tenant default governs (an interactive default never touches device-code / service-principal / azcli rows).
  // Counts the whole matching set (including this model) — the honest "N would open with it" the design quantifies.
  const defaultBlastRadius = (r: ConnectionRecord): string => {
    const tenant = (r.tenantId || '').toLowerCase();
    const family = credentialFamily(r.authMode);
    const affected = records.filter((o) => o.kind === 'xmla'
      && (o.tenantId || '').toLowerCase() === tenant && credentialFamily(o.authMode) === family);
    const n = affected.length;   // honest zero when nothing matches — never a fabricated floor of 1
    const where = r.tenantId ? `the ${r.tenantId} tenant` : 'this sign-in with no tenant recorded';
    return `${n} remembered model${n === 1 ? '' : 's'} on ${where} would open with it from now on.`;
  };
  // Blast radius stated from a PROFILE (the Accounts view has no target model) — models on the profile's tenant + family.
  const profileDefaultBlast = (p: AccountProfile): string => {
    const tenant = (p.tenantId || '').toLowerCase();
    const family = credentialFamily(p.family);
    const affected = records.filter((o) => o.kind === 'xmla'
      && (o.tenantId || '').toLowerCase() === tenant && credentialFamily(o.authMode) === family);
    const n = affected.length;   // honest zero when nothing matches — never a fabricated floor of 1
    const where = p.tenantId ? `the ${p.tenantId} tenant` : 'this sign-in with no tenant recorded';
    return `${n} remembered model${n === 1 ? '' : 's'} on ${where} would open with it from now on.`;
  };
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
  // Use a SAVED account for THIS open only (silent; never repoints the tenant default), routed by role. Keep the dialog
  // target set so a failed authorization returns to the account choice.
  const useForThisOpen = (r: ConnectionRecord, p: AccountProfile) => { void applyAccount(r, { accountProfileId: p.id }); };
  // Add a NEW account via the Microsoft sign-in picker (does not repoint the default unless it is the first account), routed by role.
  const signInAnother = (r: ConnectionRecord) => { void applyAccount(r, { forceReauth: true }); };
  // Make a saved account the tenant DEFAULT (an explicit, quantified choice). Repoints every remembered model on the
  // tenant, so it is recorded in History and broadcast so both doors refresh.
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

  const viewHistoryFor = (r: ConnectionRecord) => { setMenuId(null); setHistoryConn(r.id); setHistoryKind('all'); setView('history'); };

  // ---- views ---------------------------------------------------------------------------------------------------
  const visibleRecords = records.filter((r) => {
    const q = query.trim().toLowerCase();
    if (!q) return true;
    return [nameOf(r), r.endpoint, r.database, environment(r).text].filter(Boolean).join(' ').toLowerCase().includes(q);
  });

  // One row: model info + ONE primary verb (Open) + the overflow. Nothing else (ratified: ONE verb). Open runs the
  // sensible default (published -> Open live; local -> open the running model); a signed-out published row routes Open
  // into the shared account dialog instead of mutating its label, with the signed-out state shown as a detail status.
  // Publish/work-locally and per-open account choices moved off the row (detail pane, Current setup, overflow).
  function ModelRow({ r }: { r: ConnectionRecord }) {
    const account = accountOf(r);
    const was = probeOf(r)?.previousAccount;
    const family = credentialFamily(r.authMode);
    const stale = r.kind === 'xmla' && !account && !!family;   // a switchable published row with no live sign-in
    const identity = r.kind === 'xmla' ? rowIdentity(account, was) : 'Local Windows session';
    const isSel = r.id === selectedId;
    return (
      <div className="relative border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)', background: isSel ? 'var(--sem-accent-soft)' : 'transparent', boxShadow: isSel ? 'inset 2px 0 var(--sem-accent)' : undefined }}>
        <div className="flex items-center gap-2 px-2.5" style={{ minHeight: '56px' }}>
          <button type="button" data-testid="hub-model-row" onClick={() => { setSelectedId(r.id); setMenuId(null); setEditEnv(false); }} className="flex min-w-0 flex-1 items-center gap-2 py-2 text-left">
            <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded border text-[13px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }} aria-hidden>{r.kind === 'localDesktop' ? '▣' : '◇'}</span>
            <span className="min-w-0">
              <span className="block truncate text-[12px] leading-4 font-semibold" style={{ color: 'var(--sem-fg)' }}>{nameOf(r)}</span>
              <span className="mt-0.5 block truncate text-[9px] leading-3" style={{ color: 'var(--sem-muted)' }} title={endpointText(r.endpoint)}>{subtitleOf(r, identity)}</span>
            </span>
          </button>
          <EnvBadge r={r} />
          <button className={`${BTN_COMPACT} whitespace-nowrap`} data-testid="hub-row-open" disabled={busyId != null} onClick={() => { if (stale) openAccountChoice(r, 'open'); else void openModel(r); }}
            style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Open</button>
          <button className={BTN_OVERFLOW} data-testid="hub-row-overflow" aria-haspopup="menu" aria-expanded={menuId === r.id} onClick={(e) => { const opening = menuId !== r.id; if (opening) menuTriggerRef.current = e.currentTarget; setMenuId(opening ? r.id : null); }} style={{ ...inputStyle, borderColor: 'transparent', color: 'var(--sem-muted)' }} title="More actions">{'⋯'}</button>
        </div>
        {menuId === r.id && (
          <div role="menu" data-hub-menu onKeyDown={(e) => {
            const items = Array.from(e.currentTarget.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'));
            const i = items.indexOf(document.activeElement as HTMLButtonElement);
            if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1) % items.length]?.focus(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length]?.focus(); }
            else if (e.key === 'Home') { e.preventDefault(); items[0]?.focus(); }
            else if (e.key === 'End') { e.preventDefault(); items[items.length - 1]?.focus(); }
          }} className="absolute right-2.5 top-full z-20 -mt-1 w-56 rounded-md border py-1 shadow-2xl" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
            {r.kind === 'xmla' && family && <MenuItem onClick={() => { setMenuId(null); openAccountChoice(r); }}>Choose account for this open</MenuItem>}
            <MenuItem onClick={() => viewHistoryFor(r)}>View connection history</MenuItem>
            <div className="my-1 border-t" style={{ borderColor: 'var(--sem-border)' }} />
            <MenuItem danger onClick={() => void forget(r)}>Forget remembered model</MenuItem>
          </div>
        )}
      </div>
    );
  }

  function DetailPane() {
    if (!selected) return <div className="p-3.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Select a model to see its details and open options.</div>;
    const r = selected;
    const isCloud = r.kind === 'xmla';
    const account = accountOf(r);
    const was = probeOf(r)?.previousAccount;
    const family = credentialFamily(r.authMode);
    const stale = isCloud && !account && !!family;
    const activeQuery = querying?.connectionId === r.id;
    const activeEditing = editing?.connectionId === r.id;
    const env = environment(r);
    // Statuses are TEXT, never a mutated button label: the honest state of THIS model right now.
    const statuses: string[] = [];
    if (activeEditing) statuses.push('Currently open');
    if (activeQuery) statuses.push('Queries run here');
    if (r.workingFolder) statuses.push(`Local copy exists · ${r.workingFolder}`);
    if (stale) statuses.push('Sign-in required');
    return (
      <div className="px-3.5 py-2.5">
        <div className="flex items-start gap-2.5">
          <span className="flex h-8 w-8 items-center justify-center rounded border text-[14px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }} aria-hidden>{r.kind === 'localDesktop' ? '▣' : '◇'}</span>
          <div className="min-w-0 flex-1">
            <h3 className="truncate text-[13px] leading-5 font-semibold" style={{ color: 'var(--sem-fg)' }} title={nameOf(r)}>{nameOf(r)}</h3>
            <div className="text-[10px] leading-4 break-words" style={{ color: 'var(--sem-muted)' }}>{r.kind === 'file' ? 'Local file or project' : r.kind === 'localDesktop' ? 'Local running model' : isWorkspaceOnly(r) ? 'Published workspace' : 'Published model'}<br />{endpointText(r.endpoint)}</div>
          </div>
          <EnvBadge r={r} />
        </div>

        {statuses.length > 0 && (
          <div className="mt-1.5 flex flex-wrap gap-1.5">
            {statuses.map((s) => <span key={s} className="inline-flex items-center rounded-full border px-2 py-0.5 text-[9px] leading-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{s}</span>)}
          </div>
        )}

        <div className="mt-1.5 grid grid-cols-[96px_minmax(0,1fr)] gap-x-2 gap-y-1.5 border-y py-1.5 text-[10px] leading-4" style={{ borderColor: 'var(--sem-border)' }}>
          <div style={{ color: 'var(--sem-muted)' }}>Model</div><div style={{ color: 'var(--sem-fg)' }}>{modelRowText(r)}</div>
          <div style={{ color: 'var(--sem-muted)' }}>Environment</div>
          {/* The badge stays visible; editing the label is behind an explicit control (a label change moves your assistant's
              permissions, so it is a deliberate act, not an always-open dropdown). */}
          <div>
            {editEnv && isCloud ? (
              <>
                <select data-testid="hub-env-select" disabled={busyId != null} value={(r.label || '')} onChange={(e) => { void relabel(r, e.target.value); setEditEnv(false); }} className="h-6 max-w-[220px] rounded border px-1.5 text-[10px]" style={inputStyle}>
                  {ENV_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
                <div className="mt-1 text-[9px] leading-[13px]" style={{ color: 'var(--sem-muted)' }}>This label controls your assistant's permissions. Only a person can change it.</div>
              </>
            ) : (
              <div className="flex flex-wrap items-center gap-2">
                <span style={{ color: 'var(--sem-fg)' }}>{env.text}</span>
                {isCloud && <button className={BTN_QUIET} data-testid="hub-change-env" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => setEditEnv(true)}>Change environment label</button>}
              </div>
            )}
          </div>
          <div style={{ color: 'var(--sem-muted)' }}>Connection history</div><div style={{ color: 'var(--sem-fg)' }}>{typeof r.useCount === 'number' ? `Used ${r.useCount} time${r.useCount === 1 ? '' : 's'}` : 'Remembered'}{r.lastUsedUtc ? ` · Last used ${shortWhen(r.lastUsedUtc)}` : ''}</div>
        </div>

        {isCloud ? (
          <div className="mt-1.5 rounded-md border p-2" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 30%, var(--sem-border))', background: 'var(--sem-accent-soft)' }}>
            <div className="flex items-center gap-1.5">
              <span className="flex h-5 w-5 items-center justify-center rounded-full text-[9px] font-bold" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} aria-hidden>{initials(account || signedInAccount)}</span>
              <div className="min-w-0 text-[10px]" style={{ color: 'var(--sem-fg)' }}>{stale ? 'Signed out. Sign in and choose an account for this model.' : `This open will use ${account || signedInAccount || 'the current account'}.`}</div>
              {family && <button className={`${BTN_QUIET} ml-auto`} style={{ color: 'var(--sem-accent)' }} onClick={() => openAccountChoice(r)}>Choose account for this open</button>}
            </div>
            {was && was !== account && <div className="mt-1 text-[9px]" style={{ color: 'var(--sem-muted)' }}>Last opened as {was}. The saved endpoint stays the same whichever account you pick.</div>}
          </div>
        ) : (
          <div className="mt-1.5 rounded-md border p-2 text-[10px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{r.kind === 'file' ? 'This is a file on this computer. No sign-in is required.' : 'No Microsoft sign-in is required. This local desktop model uses your Windows session.'}</div>
        )}

        {/* The three outcomes, each explaining itself at the decision point (Kane's direct ask). Publish is NOT
            here: it is a Current-setup role, not a way to open.
            They STICK to the bottom of the pane: the detail above them is taller than the pane, so the third way to
            open used to sit below the fold, half-faded by the scroll cue, and the decision was cut off mid-sentence. */}
        <div className="sticky bottom-0 -mx-3.5 -mb-2.5 px-3.5 pb-2.5 pt-1.5" style={{ background: 'var(--sem-surface)', borderTop: '1px solid var(--sem-border)' }}>
        <div className="text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>Open this model</div>
        <div className="mt-1.5 grid gap-1.5">
          <OutcomeButton primary label={isCloud ? 'Open live' : 'Open'} disabled={busyId != null} busy={busyId === 'open:' + r.id}
            micro={isCloud ? 'Edit and query the published model directly. No local files are created.' : r.kind === 'file' ? 'Open this local file or project for editing.' : 'Open this running local model for editing and queries.'}
            onClick={() => { if (stale) openAccountChoice(r, 'open'); else void openModel(r); }} />
          {/* Query carries the SAME signed-out guard as Open live (routes to the account picker), an explicit
              "already answers queries" state instead of a silent grey, and visible progress — the three ways it used
              to read as "nothing happens." */}
          <OutcomeButton label={activeQuery ? 'Already used for queries' : 'Query this model'} disabled={busyId != null || activeQuery || r.kind === 'file'} busy={busyId === 'query:' + r.id}
            micro={activeQuery ? 'This model already answers your tests and queries.' : 'Run queries and tests against this model. The model you are editing stays open.'}
            onClick={() => { if (stale) openAccountChoice(r, 'query'); else void queryWith(r); }} />
          <OutcomeButton label="Work locally" disabled={busyId != null} busy={busyId === 'work:' + r.id}
            micro="Work on a saved copy on this computer. Publish separately when you want to update the live model."
            onClick={() => void beginWork(r)} />
        </div>
        </div>
      </div>
    );
  }

  // The ONE shared account dialog (Phase 2), reached from a row's overflow "Choose account for this open", the detail's
  // account note, and the Accounts "Switch account". Saved profiles are selectable per open (Use for this open) without repointing the
  // tenant default; Sign in another account adds a profile via the real Microsoft picker; make-default is explicit and
  // quantified. A failed authorization returns HERE (openModel re-sets the target), never to an endpoint form.
  function AccountDialog({ r }: { r: ConnectionRecord }) {
    const list = profilesFor(r);
    const def = defaultProfileFor(r);
    const defName = def?.username || accountOf(r) || signedInAccount;
    const purpose = switchPurpose === 'query' ? 'query' : 'open';
    const role = purpose === 'query' ? 'Tests and queries' : 'Open for editing';
    const authRequired = isAuthRequiredFailure(error || undefined);
    const signedInCount = list.filter((p) => p.signedIn).length;
    const signedOutCount = list.length - signedInCount;
    const failedProfile = failedAuthAttempt?.targetId === r.id && failedAuthAttempt.purpose === purpose
      ? list.find((p) => p.id === failedAuthAttempt.profileId || p.username === failedAuthAttempt.username)
      : undefined;
    // ONE notice, not two: the plain cause line, then the instruction, then the engine's raw text behind "Show details".
    // The tone stays honest - amber while a sign-in can repair this here, red for a failure this dialog cannot repair.
    const causeAccount = failedProfile?.username || failedAuthAttempt?.username || accountOf(r);
    const plainCause = plainAuthCause(error, causeAccount);
    const tone = authRequired ? 'var(--sem-warn)' : 'var(--sem-bad)';
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
      <div data-testid="hub-account-dialog" data-purpose={purpose} className="m-4 rounded-lg border p-3" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, var(--sem-border))', background: 'var(--sem-surface)' }}>
        <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Who should authorize {role} for {nameOf(r)}?</div>
        <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>Saved accounts are remembered identities. The connection currently authorized for this model {authRequired ? 'may have expired.' : 'is separate from these saved identities.'} This choice applies to this {purpose} only. The saved endpoint stays the same whichever account you pick.</div>
        {error && <div data-testid="hub-account-error" className="mt-2 rounded-md border px-2.5 py-2 text-[10px] leading-4 break-words" style={{ borderColor: tone, background: 'var(--sem-surface-2)' }}>
          <div className="text-[11px] font-semibold" style={{ color: tone }}>{plainCause || `The last attempt failed: ${error}`}</div>
          {authRequired
            ? <div data-testid="hub-account-recovery" className="mt-1" style={{ color: 'var(--sem-fg)' }}>
                {failedProfile
                  ? <>Sign in needed for this connection as {failedProfile.username}. Use “Sign in again” on that row, or the button below. {signedInCount > 1 && <>You can also choose another signed-in saved account. </>}Your current model and local edits stay in place.</>
                  : signedInCount > 0
                    ? <>Choose a signed-in saved account for this {purpose}. {signedOutCount > 0 && <>Signed-out profiles offer “Sign in and use”. </>}“Sign in another account” opens the sign-in picker. Your current model and local edits stay in place.</>
                    : <>No saved account is currently signed in. Use “Sign in another account” to authorize this {purpose}. Your current model and local edits stay in place.</>}
              </div>
            : <div data-testid="hub-account-retry" className="mt-1" style={{ color: 'var(--sem-fg)' }}>The target remains selected. Retry this {purpose} with a listed account or use “Sign in another account”. Your current model and local edits stay in place.</div>}
          {plainCause && <details data-testid="hub-account-details" className="mt-1.5">
            <summary className="cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>Show details</summary>
            <div className="mt-1 break-words" style={{ color: 'var(--sem-muted)' }}>{error}</div>
          </details>}
        </div>}
        <div className="mt-2.5 rounded-md border overflow-hidden" style={{ borderColor: 'var(--sem-border)' }}>
          {list.map((p) => (
            <div key={p.id} data-testid="hub-account-row" data-selected={picked?.id === p.id ? 'true' : undefined} className="flex items-center gap-2.5 border-b px-2.5 py-2 last:border-b-0" style={{ borderColor: 'var(--sem-border)', background: picked?.id === p.id ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)' }}>
              <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[10px] font-bold" style={{ background: p.isDefault ? 'var(--sem-accent)' : 'var(--sem-surface)', color: p.isDefault ? 'var(--sem-on-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} aria-hidden>{initials(p.username)}</span>
              {/* Selecting a row is what the primary button follows, so the identity itself is the selector. */}
              <button type="button" data-testid="hub-account-select" aria-pressed={picked?.id === p.id} className="min-w-0 flex-1 text-left" onClick={() => setPickedProfileId(p.id)}>
                <div className="truncate text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{p.username}</div>
                <div className="truncate text-[9px]" style={{ color: 'var(--sem-muted)' }}>{[p.tenantId ? `${p.tenantId} tenant` : 'tenant unknown', failedProfile?.id === p.id ? 'sign-in needed for this connection' : p.signedIn ? 'signed in' : 'signed out', p.lastUseUtc ? `last used ${shortWhen(p.lastUseUtc)}` : null].filter(Boolean).join(' · ')}</div>
              </button>
              <div className="flex shrink-0 items-center gap-1.5">
                {/* Signed-out is checked BEFORE default (MED, sol): a DEFAULT profile that is signed out must still get a
                    targeted "Sign in and use" (forceReauth + loginHint re-auths THIS identity) - the bottom "Open with
                    default" has neither, so it could sign in a different account. */}
                {failedProfile?.id === p.id
                  ? <button className={BTN_QUIET} data-testid="hub-sign-in-again" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => void applyAccount(r, { forceReauth: true, loginHint: p.username })}>Sign in again</button>
                  : !p.signedIn
                  ? <button className={BTN_QUIET} data-testid="hub-signin-and-use" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => void applyAccount(r, { forceReauth: true, loginHint: p.username })}>Sign in and use</button>
                  : p.isDefault
                    ? <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] font-semibold uppercase" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>default</span>
                    : <button className={BTN_QUIET} data-testid="hub-use-for-open" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => useForThisOpen(r, p)}>Use for this {purpose}</button>}
                {!p.isDefault && p.signedIn && <button className={BTN_QUIET} data-testid="hub-make-default" style={{ color: 'var(--sem-muted)' }} disabled={busyId != null} onClick={() => setPendingDefault(p)}>Make default</button>}
              </div>
            </div>
          ))}
          <button data-testid="hub-sign-in-another" className="flex w-full items-center gap-2.5 px-2.5 py-2 text-left" style={{ background: 'var(--sem-surface-2)' }} disabled={busyId != null} onClick={() => signInAnother(r)}>
            <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[13px] font-bold" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} aria-hidden>+</span>
            <span className="min-w-0"><span className="block text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Sign in another account</span>
              <span className="block text-[9px]" style={{ color: 'var(--sem-muted)' }}>opens the Microsoft sign-in picker; adds a saved profile</span></span>
          </button>
        </div>
        <div className="mt-2.5 border-t pt-2 text-[9px] leading-[14px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>Make an account the default instead: {defaultBlastRadius(r)} Every switch is recorded in the connection history.</div>
        <div className="mt-3 flex justify-end gap-2">
          <button className={BTN} onClick={() => { setSwitchTarget(null); setSwitchPurpose(null); setFailedAuthAttempt(null); setPendingDefault(null); setPickedProfileId(null); setError(null); }} style={inputStyle}>Cancel</button>
          <button className={BTN} data-testid="hub-open-default" disabled={busyId != null || (!picked && list.length > 0)} onClick={runPrimary} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{primaryLabel}</button>
        </div>
      </div>
    );
  }

  function OpenView() {
    return (
      // A fixed head (title, account banner, the "Open a new model" strip) above a flex-1 body where BOTH panes own
      // their own scroll at >=900px, so a tall detail pane can never push the dialog past its fixed height (the old
      // outer scrollbar + cut-off cards). Below 900px it flows naturally into the one outer scroll.
      <section className="flex flex-col px-5 py-4 min-[900px]:h-full">
        <div className="mb-3 flex items-start gap-3">
          <div className="min-w-0"><h2 className="text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>Open a model</h2>
            <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Open a saved model, connect to a published model or start a new one. Your recent connections are remembered.</p></div>
          <input ref={searchRef} value={query} onChange={(e) => setQuery(e.target.value)} type="search" placeholder="Search models" className="ml-auto h-8 w-[210px] shrink-0 rounded-[5px] border px-2.5 text-[11px]" style={inputStyle} />
        </div>

        <div className="mb-3 flex items-center gap-2 rounded-md border px-2.5 py-2 text-[10px]" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 30%, var(--sem-border))', background: 'var(--sem-accent-soft)' }}>
          <span className="flex h-5 w-5 items-center justify-center rounded-full text-[9px] font-bold" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} aria-hidden>{initials(signedInAccount)}</span>
          <div className="min-w-0"><div className="text-[10px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{signedInAccount ? `Signed in as ${signedInAccount}` : 'No account is signed in'}</div>
            <div className="text-[9px] truncate" style={{ color: 'var(--sem-muted)' }}>{signedInAccount ? `${signedInTenant || 'Tenant unknown'} · each model opens with its tenant's default; pick another per open` : 'Open a published model below to sign in'}</div></div>
          {/* Not-signed-in routes where a sign-in actually happens (adding a published model), NOT the Accounts view,
              which has nothing to act on with no account yet (the old dead end sol flagged). */}
          <button className={`${BTN_QUIET} ml-auto`} data-testid="hub-banner-account" style={{ color: 'var(--sem-accent)' }} onClick={() => { if (signedInAccount) setView('accounts'); else openAddView(); }}>{signedInAccount ? 'Switch account' : 'Sign in'}</button>
        </div>

        {/* "Open a new model" FIRST and full-width (Kane: the most frequent action; sol: placing it here also relieves
            the right-column overflow). "Create blank model" is named so it never reads as this section's own title. */}
        <section className="mb-3">
          <h3 className="mb-1.5 text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>Open a new model</h3>
          <div className="grid grid-cols-2 gap-1.5 min-[720px]:grid-cols-4">
            <QuickCard glyph={'▱'} title="Local file or project" body="Open a BIM, TMDL, or Power BI project from disk." onClick={() => openLocalModel()} />
            <QuickCard glyph="+" title="Published model" body="Connect to a published Power BI or Fabric model." onClick={() => openAddView()} />
            <QuickCard glyph={'▣'} title="Running desktop model" body="Discover a running local model and query it." onClick={() => void connectRunningLocal()} />
            <QuickCard glyph="+" title="Create blank model" body="Name a new model and build it with your assistant." onClick={openCreate} />
          </div>
          <form className="mt-1.5 flex flex-wrap items-center gap-1.5" onSubmit={(e) => { e.preventDefault(); void openTypedLocal(); }}>
            <input ref={localPathRef} data-testid="hub-local-path" value={localPath} onChange={(e) => { setLocalPath(e.target.value); setLocalPathError(null); }} placeholder="Or type a path to a .bim file, TMDL folder, or Power BI project" autoComplete="off" className="h-8 min-w-[220px] flex-1 rounded-[5px] border px-2.5 text-[11px]" style={inputStyle} />
            <button type="submit" className={BTN_COMPACT} disabled={busyId != null} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Open path</button>
          </form>
          {localPathError && <div data-testid="hub-local-path-error" className="mt-1 text-[10px]" style={{ color: 'var(--sem-bad)' }}>{localPathError}</div>}
        </section>

        <div className="grid grid-cols-1 gap-3 min-[900px]:flex-1 min-[900px]:min-h-0 min-[900px]:grid-cols-[minmax(0,1.6fr)_minmax(320px,1fr)]">
          <section className="flex min-h-0 flex-col overflow-hidden rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
            <div className="flex items-center gap-2 border-b px-2.5 py-2" style={{ borderColor: 'var(--sem-border)' }}>
              <h3 className="text-[10px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Recent and remembered</h3>
              <span className="inline-flex h-4 min-w-4 items-center justify-center rounded-full px-1.5 text-[9px] leading-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{visibleRecords.length}</span>
              <span className="ml-auto text-[9px]" style={{ color: 'var(--sem-muted)' }}>This device</span>
            </div>
            {/* The list owns its scroll at >=900px (min-height:0 + overflow); below 900px it flows into the one outer scroll.
                Same ScrollCue treatment as the detail pane: this platform paints an overlay scrollbar (no track drawn at
                rest), so a list cut at the pane's bottom edge read as the whole list rather than as scrollable. */}
            <ScrollCue className="min-[900px]:min-h-0 min-[900px]:flex-1 min-[900px]:overflow-auto">
              {loading && <div className="p-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Loading connections...</div>}
              {!loading && visibleRecords.map((r) => <ModelRow key={r.id} r={r} />)}
              {!loading && visibleRecords.length === 0 && <div className="p-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No remembered models match. Open a new model above.</div>}
            </ScrollCue>
          </section>

          {/* The detail pane now OWNS its scroll at >=900px, so a tall detail can never push the dialog past its fixed
              height — the fix for the outer scrollbar + cut-off cards. The new-model sources left this column entirely
              (they are the top strip now). */}
          <section className="flex min-h-0 flex-col overflow-hidden rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} aria-live="polite">
            {/* pb-7 clears the scroll-cue gradient, so the third way to open is never half-faded at the
                bottom of the pane the way it was. */}
            <ScrollCue className="min-[900px]:min-h-0 min-[900px]:flex-1 min-[900px]:overflow-auto"><DetailPane /></ScrollCue>
          </section>
        </div>
      </section>
    );
  }

  function SetupView() {
    return (
      <section className="px-5 py-4">
        <div className="flex items-start gap-3 mb-3">
          <div><h2 className="text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>Current setup</h2>
            <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Choose which model to edit, which one to test and where to publish. Changing these choices does not publish anything.</p></div>
          <button className={`${BTN} ml-auto`} onClick={() => setView('open')} style={inputStyle}>Choose a model</button>
        </div>
        <div className="grid grid-cols-2 gap-2 max-[760px]:grid-cols-1">
          <RoleCard n={1} label="Editing" value={roleValue(editing, 'No model open')}
            detail={editing?.sourceControlled ? `Local files in source control · ${editing.repositoryRoot}` : (editing?.source || 'Open a local project or live model')} missing={!editing?.available} action="Change source" onAction={() => setView('open')} />
          <RoleCard n={2} label="Tests and queries" value={roleValue(querying, 'Not connected')}
            detail={querying?.available ? (querying.kind === 'localDesktop' || querying.kind === 'local' ? 'A running model answers tests and queries' : 'A published model answers tests and queries') : 'Choose which live model answers tests and queries'} missing={!querying?.available} action="Change test model" onAction={() => setView('open')} />
          {/* Publish is a Current-setup role, not a way to open, so it owns its assignment here (it left the Open
              view entirely). Choosing a destination only links it; publishing stays a separate reviewed action.
              The Reference role card left this grid on 2026-09-14 with the footer's Reference slot: the engine
              still has the slot, and the way in becomes an action rather than a fourth thing to set up first. */}
          <RoleCard n={3} label="Publish to" value={roleValue(publishing, 'Not linked')}
            detail={publishing?.available ? 'Publishing requires a separate review and confirmation.' : 'Choose the live model you want to publish to'} missing={!publishing?.available}
            control={<label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Set as publish destination
              <select value={publishing?.connectionId || ''} disabled={busyId != null} onChange={(e) => { const t = xmlaRecords.find((x) => x.id === e.target.value); if (t) void setPublish(t); }} className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle}>
                <option value="">Choose a published destination</option>
                {xmlaRecords.map((x) => <option key={x.id} value={x.id}>{nameOf(x)}</option>)}
              </select></label>} />
          {/* The fourth role. It is not a model, so it carries no account probe and no Open verb: it is the place a
              check gets its independent number from, saved once and picked by name. The card states how many are
              saved and hands over to the section that manages them. */}
          <RoleCard n={4} label="SQL sources"
            value={sqlLoading ? 'Loading...' : sqlError ? SQL_SOURCE_COPY.loadFailed : sqlSources.length > 0
              ? `${sqlSources.length} ${sqlSources.length === 1 ? 'source saved' : 'sources saved'}`
              : SQL_SOURCE_COPY.roleEmpty}
            missing={!sqlLoading && sqlSources.length === 0}
            detail={SQL_SOURCE_COPY.roleDetail} action={SQL_SOURCE_COPY.roleAction} onAction={() => setView('sqlsources')} />
        </div>
        {context?.summary && <p className="mt-3 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{context.summary}</p>}
        {/* The three outcomes, in the ratified taxonomy, so editing / querying / working-locally never blur. */}
        <div className="mt-3 rounded-lg border p-3 text-[10px] leading-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
          <div><b style={{ color: 'var(--sem-fg)' }}>Open live</b> Edit and query the published model directly. No local files are created.</div>
          <div className="mt-1"><b style={{ color: 'var(--sem-fg)' }}>Query this model</b> Run queries and tests against this model. The model you are editing stays open.</div>
          <div className="mt-1"><b style={{ color: 'var(--sem-fg)' }}>Work locally</b> Work on a saved copy on this computer. Publish separately when you want to update the live model.</div>
        </div>
      </section>
    );
  }

  function AccountsView() {
    // The credential family a saved profile signs in with, in plain words.
    const methodLabel = (family?: string): string => credentialFamily(family) === 'devicecode' ? 'Device code sign-in' : 'Browser sign-in';
    return (
      <section className="px-5 py-4">
        <div className="mb-3"><h2 className="text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>Accounts</h2>
          <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>The saved Microsoft identities on this device. Each remembered model opens with the default account for its tenant and sign-in method (shown on each row); you can open one model as a different saved account for that open only, without changing what the others use.</p></div>

        <div className="rounded-md border p-2.5" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 30%, var(--sem-border))', background: 'var(--sem-accent-soft)' }}>
          <div className="flex items-center gap-2.5">
            <span className="flex h-8 w-8 items-center justify-center rounded-full text-[11px] font-bold" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} aria-hidden>{initials(signedInAccount)}</span>
            <div className="min-w-0"><div className="truncate text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{signedInAccount || 'No account is signed in'}</div>
              <div className="truncate text-[9px]" style={{ color: 'var(--sem-muted)' }}>{signedInTenant ? `${signedInTenant} · current` : (signedInAccount ? 'Tenant unknown' : 'Open a published model to sign in')}</div></div>
            {/* Never a dead end (B2): a live switchable model offers Switch account; otherwise, when nothing is signed
                in, a primary Sign in routes to where a sign-in actually happens (adding/opening a published model). */}
            {switchable
              ? <button className={`${BTN} ml-auto`} data-testid="hub-switch-account" disabled={busyId != null} onClick={() => openAccountChoice(switchable)} style={inputStyle}>Switch account</button>
              : !signedInAccount && <button className={`${BTN} ml-auto`} data-testid="hub-accounts-signin" disabled={busyId != null} onClick={() => openAddView()} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Sign in</button>}
          </div>
        </div>

        {/* The saved-account list exists regardless of live state (no dead-end): a device-local, credential-free store. */}
        <div className="mt-4 mb-1.5 flex items-center gap-2">
          <h3 className="text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>Saved accounts on this device</h3>
          <span className="inline-flex h-4 min-w-4 items-center justify-center rounded-full px-1.5 text-[9px] leading-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{profiles.length}</span>
        </div>
        <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          {profiles.length === 0 && <div className="px-3 py-3 text-[10px]" style={{ color: 'var(--sem-muted)' }}>No saved accounts yet. <button className="font-semibold underline-offset-2 hover:underline" data-testid="hub-accounts-empty-add" style={{ color: 'var(--sem-accent)' }} onClick={() => openAddView()}>Add a published model</button> and sign in with a browser, and the account is remembered here.</div>}
          {profiles.map((p) => (
            <div key={p.id} data-testid="hub-profile-row" className="flex items-center gap-2.5 border-b px-3 py-2 last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
              <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[10px] font-bold" style={{ background: p.isDefault ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: p.isDefault ? 'var(--sem-on-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} aria-hidden>{initials(p.username)}</span>
              <div className="min-w-0 flex-1">
                <div className="truncate text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{p.username}</div>
                <div className="truncate text-[9px]" style={{ color: 'var(--sem-muted)' }}>{[p.tenantId ? `${p.tenantId} tenant` : 'tenant unknown', methodLabel(p.family), p.lastUseUtc ? `last used ${shortWhen(p.lastUseUtc)}` : (p.lastSignInUtc ? `signed in ${shortWhen(p.lastSignInUtc)}` : null)].filter(Boolean).join(' · ')}</div>
              </div>
              <div className="flex shrink-0 items-center gap-1.5">
                {p.isDefault && <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] font-semibold uppercase" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>default</span>}
                {!p.signedIn && <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] font-semibold uppercase" style={{ background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)', borderColor: 'color-mix(in srgb, var(--sem-bad) 40%, transparent)' }}>signed out</span>}
                {/* A signed-out profile is not a dead end (MED, sol), and recovery must TARGET this profile: open the
                    account dialog of a remembered model on its tenant+family, where "Sign in and use" re-auths THIS
                    identity (forceReauth + loginHint). Only if no such model exists do we fall back to a generic Add. */}
                {!p.signedIn && <button className={BTN_QUIET} data-testid="hub-profile-resignin" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => { const m = records.find((rec) => rec.kind === 'xmla' && profilesFor(rec).some((pp) => pp.id === p.id)); if (m) openAccountChoice(m); else openAddView(); }}>Sign in</button>}
                {!p.isDefault && p.signedIn && <button className={BTN_QUIET} data-testid="hub-profile-make-default" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => setPendingDefault(p)}>Make default</button>}
              </div>
            </div>
          ))}
        </div>

        <div className="mt-4 mb-1.5 text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>Other authentication methods</div>
        {/* Real entry points, not static cards (sol: they read as clickable): each jumps to Add with the method
            preselected, where the live setup preflight shows what the engine can see. */}
        <div className="grid grid-cols-2 gap-2.5 max-[760px]:grid-cols-1">
          <button type="button" data-testid="hub-authmethod-azcli" className="rounded-lg border p-3 text-left" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} onClick={() => openAddView('azcli')}>
            <h3 className="text-[10px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Command-line sign-in</h3>
            <p className="mt-1 text-[9px] leading-4" style={{ color: 'var(--sem-muted)' }}>Use the account from your current Azure command-line session. Set it up when you add a model.</p>
          </button>
          <button type="button" data-testid="hub-authmethod-sp" className="rounded-lg border p-3 text-left" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} onClick={() => openAddView('serviceprincipal')}>
            <h3 className="text-[10px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Service identity</h3>
            <p className="mt-1 text-[9px] leading-4" style={{ color: 'var(--sem-muted)' }}>Use a configured service identity for automation. Check its setup when you add a model.</p>
          </button>
        </div>
        <p className="mt-4 text-[9px] leading-5" style={{ color: 'var(--sem-muted)' }}>Saved account names and tenant details help you choose the right identity. Credentials stay in the encrypted Microsoft sign-in cache on this device. Connection records contain no passwords or access tokens.</p>
      </section>
    );
  }

  function HistoryView() {
    const filtered = history.filter((e) => historyKind === 'all' ? true : historyKind === 'signin' ? isSigninEvent(e) : !isSigninEvent(e));
    const connName = historyConn ? nameOf(records.find((r) => r.id === historyConn) || { id: '', kind: '', endpoint: '' }) : null;
    const chip = 'inline-flex h-[26px] items-center rounded-full border px-2.5 text-[9px] leading-none font-semibold';
    return (
      <section className="px-5 py-4">
        <div className="flex items-start gap-3 mb-3">
          <div><h2 className="text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>History</h2>
            <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Model connections and Microsoft sign-ins together. History is local to this device.</p></div>
          <button className={`${BTN} ml-auto`} onClick={() => setView('accounts')} style={inputStyle}>Manage accounts</button>
        </div>
        <div className="mb-3 flex flex-wrap items-center gap-1.5">
          {(['all', 'model', 'signin'] as const).map((k) => (
            <button key={k} className={chip} onClick={() => setHistoryKind(k)}
              style={historyKind === k ? { borderColor: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' } : { borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
              {k === 'all' ? 'All activity' : k === 'model' ? 'Models' : 'Sign-ins'}
            </button>
          ))}
          {connName && <button className={`${chip} ml-1`} onClick={() => setHistoryConn(null)} style={{ borderColor: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }} title="Clear the model filter">{connName} ×</button>}
        </div>
        <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          {filtered.length === 0 && <div className="px-3 py-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No connection history yet.</div>}
          {filtered.map((e, i) => (
            <div key={i} className="flex items-start gap-2 px-3 py-2 border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
              <span className="mt-0.5 text-[11px]" aria-hidden style={{ color: e.ok ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{e.ok ? '●' : '○'}</span>
              <div className="min-w-0 flex-1">
                <div className="text-[11px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{historyKindLabel(e)}{e.account ? ` · ${e.account}` : ''}</div>
                <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={e.detail || e.endpoint}>{[e.detail || e.database || short(e.endpoint), shortWhen(e.whenUtc)].filter(Boolean).join(' · ')}</div>
              </div>
            </div>
          ))}
        </div>
      </section>
    );
  }

  // The advanced-auth PREFLIGHT panel (Kane's "what does config look like / how are secrets + key vaults managed?").
  // It shows what the engine can actually SEE — the detected az login session, or which service-principal inputs are
  // present on the host (names + presence only, never a value) — so choosing the method previews whether Connect will
  // work. It states plainly that secrets live in the environment and that Key Vault is not resolved in-app.
  function AuthPreflight() {
    const sp = authMode === 'serviceprincipal';
    return (
      <div data-testid="hub-auth-preflight" className="rounded-md border p-2.5 text-[10px] leading-4" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 30%, var(--sem-border))', background: 'var(--sem-surface-2)' }}>
        <div className="flex items-center gap-1.5">
          <span className="text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>{sp ? 'Service identity setup' : 'Command-line sign-in'}</span>
          {prereqLoading && <span className="text-[9px]" style={{ color: 'var(--sem-muted)' }}>Checking...</span>}
          {!prereqLoading && prereq && <span className="ml-auto text-[9px] font-semibold" style={{ color: prereq.ready ? 'var(--sem-good)' : 'var(--sem-warn)' }}>{prereq.ready ? 'Ready' : 'Not ready'}</span>}
        </div>
        {sp ? (
          <>
            {prereq && prereq.requirements.length > 0 ? (
              <ul className="mt-1.5 space-y-1">
                {prereq.requirements.map((req) => (
                  <li key={req.label} className="flex items-start gap-1.5">
                    <span aria-hidden style={{ color: req.present ? 'var(--sem-good)' : 'var(--sem-warn)' }}>{req.present ? '●' : '○'}</span>
                    <span style={{ color: 'var(--sem-fg)' }}>{req.label}<span style={{ color: 'var(--sem-muted)' }}> · {req.present ? 'found' : 'not set'} ({req.label === 'Tenant' && newTenant.trim() ? 'provided in the tenant field above' : req.envNames.join(' or ')})</span></span>
                  </li>
                ))}
              </ul>
            ) : (
              <div className="mt-1.5" style={{ color: 'var(--sem-muted)' }}>Set the client id, secret, and tenant as environment variables (AZURE_CLIENT_ID / AZURE_CLIENT_SECRET / AZURE_TENANT_ID, or the FABRIC_ / POWERBI_ aliases) before you connect.</div>
            )}
            <div className="mt-1.5" style={{ color: 'var(--sem-muted)' }}>The variables must be visible to the VS Code process; if you set them after launch, reload the window. Semanticus never stores the secret. <b style={{ color: 'var(--sem-fg)' }}>Direct Key Vault references are not supported</b> here; have your pipeline place the secret in the environment.</div>
          </>
        ) : (
          <>
            {prereq && prereq.cliAccount
              ? <div className="mt-1.5" style={{ color: 'var(--sem-fg)' }}>Signed in to Azure CLI as {prereq.cliAccount}{prereq.cliTenant ? ` · ${prereq.cliTenant}` : ''}.</div>
              : <div className="mt-1.5" style={{ color: 'var(--sem-warn)' }}>{prereq?.detail || 'Not signed in to the Azure CLI. Run az login in a terminal, then reload the window.'}</div>}
            <div className="mt-1.5" style={{ color: 'var(--sem-muted)' }}>Uses your current az login session; nothing is entered or stored here. Power BI can reject Azure CLI tokens on some tenants, where service identity or browser sign-in is more reliable.</div>
          </>
        )}
      </div>
    );
  }

  function AddView() {
    const advanced = authMode === 'azcli' || authMode === 'serviceprincipal';
    return (
      <section className="px-5 py-4">
        <button className={BTN_QUIET} style={{ color: 'var(--sem-accent)' }} onClick={() => setView('open')}>{'‹'} Back to models</button>
        <h2 className="mt-1 text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>Add a published model</h2>
        <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Enter the address of the workspace containing your model. Semanticus remembers it so you can reopen the model without entering the address again.</p>
        <form className="mt-3 max-w-[560px] rounded-lg border p-3 grid gap-2.5" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} onSubmit={(e) => { e.preventDefault(); void addAndConnect(); }}>
          <label className="grid gap-1 text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>XMLA endpoint
            <input data-testid="hub-endpoint-input" value={endpoint} onChange={(e) => setEndpoint(e.target.value)} placeholder="powerbi://api.powerbi.com/v1.0/myorg/Workspace" autoComplete="off" className="h-8 rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle} />
            <span className="text-[9px] font-normal" style={{ color: 'var(--sem-muted)' }}>Paste the workspace connection address (XMLA endpoint) from the workspace settings in Power BI or Fabric.</span></label>
          <label className="grid gap-1 text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>Model name
            <input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder="Optional if the workspace has one model" autoComplete="off" className="h-8 rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle} /></label>
          <label className="grid gap-1 text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>Sign-in
            <select data-testid="hub-authmode-select" value={authMode} onChange={(e) => { const m = e.target.value; setAuthMode(m); if (m !== 'azcli' && m !== 'serviceprincipal') setNewTenant(''); }} className="h-8 rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle}>
              <option value="interactive">Browser sign-in</option>
              <option value="azcli">Command-line sign-in</option>
              <option value="serviceprincipal">Service identity</option>
            </select></label>
          {/* Tenant targeting for the advanced modes: a service principal needs its tenant; az login otherwise silently
              uses its home tenant (the bug sol flagged where the add passed tenantId=null). */}
          {advanced && (
            <label className="grid gap-1 text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>{authMode === 'serviceprincipal' ? 'Tenant' : 'Tenant (optional)'}
              <input data-testid="hub-tenant-input" value={newTenant} onChange={(e) => setNewTenant(e.target.value)} placeholder="Tenant ID or domain (e.g. contoso.com)" autoComplete="off" className="h-8 rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle} />
              <span className="text-[9px] font-normal" style={{ color: 'var(--sem-muted)' }}>{authMode === 'serviceprincipal' ? 'The tenant the service principal belongs to (or set AZURE_TENANT_ID).' : 'Leave blank to use your az login home tenant, or name the tenant this model lives in.'}</span></label>
          )}
          <label className="grid gap-1 text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>Environment
            <select value={newLabel} onChange={(e) => setNewLabel(e.target.value)} className="h-8 rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle}>
              {ENV_OPTIONS.filter((o) => o.value !== 'local').map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
            <span className="text-[9px] font-normal" style={{ color: 'var(--sem-muted)' }}>Environment labels control your assistant's permissions. Only a person can change them.</span></label>
          {advanced && AuthPreflight()}
          <div className="flex justify-end gap-2 border-t pt-2.5" style={{ borderColor: 'var(--sem-border)' }}>
            <button type="button" className={BTN} onClick={() => setView('open')} style={inputStyle}>Cancel</button>
            <button type="submit" className={BTN} disabled={!endpoint.trim() || busyId != null} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Connect and remember</button>
          </div>
        </form>
      </section>
    );
  }

  function WorkView() {
    const src = work?.source || selected;
    return (
      <section className="px-5 py-4">
        <button className={BTN_QUIET} style={{ color: 'var(--sem-accent)' }} onClick={() => { setWork(null); setView('open'); }}>{'‹'} Back to models</button>
        <h2 className="mt-1 text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>Work locally from {src ? nameOf(src) : 'this model'}</h2>
        <p className="mt-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Edit the model files on this computer. To run queries and tests, also choose a live model with data.</p>
        {work && (
          <div className="mt-3 max-w-[640px] rounded-lg border p-3" style={{ borderColor: 'var(--sem-accent)', background: 'var(--sem-surface)' }}>
            <div className="grid grid-cols-2 gap-3 max-[600px]:grid-cols-1">
              <label className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Tests and queries
                <select value={work.queryId} onChange={(e) => updateWork(e.target.value, work.publishId)} className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px]" style={inputStyle}>
                  {records.filter((r) => r.kind !== 'file').map((r) => <option key={r.id} value={r.id}>{nameOf(r)} · {r.kind === 'localDesktop' ? 'local running' : 'published'}</option>)}
                </select>
              </label>
              <label className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Final publish destination
                <select value={work.publishId} onChange={(e) => updateWork(work.queryId, e.target.value)} className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px]" style={inputStyle}>
                  <option value="">Choose later</option>
                  {xmlaRecords.map((r) => <option key={r.id} value={r.id}>{nameOf(r)}</option>)}
                </select>
              </label>
            </div>
            {work.plan ? <>
              <div className="mt-3 rounded-md p-2.5 text-[11px] leading-5" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>{work.plan.summary}</div>
              <div className="mt-2 text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={work.plan.targetFolder}><b style={{ color: 'var(--sem-fg)' }}>Local folder:</b> {work.plan.targetFolder}</div>
              <ul className="mt-2 space-y-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>{work.plan.benefits?.map((b) => <li key={b}>{'✓'} {b}</li>)}</ul>
              {work.plan.conflicts?.map((c) => <div key={c} className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{c}</div>)}
              {work.plan.error && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{work.plan.error}</div>}
              {work.plan.opened && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-good)' }}>Local working copy opened. {work.plan.queryConnected ? 'The selected test model is connected.' : 'Connect a test model when ready.'}</div>}
              {!work.plan.opened && <div className="flex justify-end gap-2 mt-3"><button className={BTN} onClick={() => { setWork(null); setView('open'); }} style={inputStyle}>Cancel</button>
                <button className={BTN} disabled={!work.plan.canCommit || busyId != null} onClick={() => void confirmWork()} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busyId ? 'Preparing...' : work.plan.action === 'open' ? 'Open local copy' : 'Create local copy'}</button></div>}
            </> : <div className="mt-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Updating preview...</div>}
          </div>
        )}
      </section>
    );
  }

  // ================================================================================================================
  // The fourth role's own section: the saved SQL sources, their Add / Edit form, and the four states.
  // Called as a function, never mounted as a JSX element, so typing in the form survives a parent re-render
  // (D-004: an inner component is a NEW component type each keystroke, which tore the inputs down).
  // ================================================================================================================
  function SqlSourcesView() {
    const form = sqlForm;
    const modeLabel = (r: SqlSourceRecord): string => {
      const chosen = SIGN_IN_MODES.find((m) => m.value === defaultSignInMode(r));
      return chosen ? chosen.label : 'Sign in in a browser';
    };
    return (
      <section className="px-5 py-4">
        <div className="flex items-start gap-3 mb-3">
          <div><h2 className="text-[17px] leading-6 font-semibold" style={{ color: 'var(--sem-fg)' }}>{SQL_SOURCE_COPY.title}</h2>
            <p className="mt-0.5 max-w-[620px] text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.lede}</p></div>
          <button className={`${BTN} ml-auto`} data-testid="hub-sql-add" disabled={sqlBusy != null} onClick={() => openSqlForm()}
            style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Add SQL source</button>
        </div>

        {/* The Add / Edit form. One form for both, because the fields and the rules are identical; only the title,
            the primary verb and whether an id travels differ. */}
        {form && (
          <div className="mb-3 rounded-lg border p-3" data-testid="hub-sql-form" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 45%, var(--sem-border))', background: 'var(--sem-surface)' }}>
            <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{form.id ? SQL_SOURCE_COPY.editTitle : SQL_SOURCE_COPY.newTitle}</div>
            <div className="mt-2 grid grid-cols-3 gap-2 max-[760px]:grid-cols-1">
              <label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.nameLabel}
                <input data-testid="hub-sql-name" value={form.name} autoFocus onChange={(e) => { setSqlForm({ ...form, name: e.target.value }); setSqlFormError(null); }}
                  placeholder={SQL_SOURCE_COPY.namePlaceholder} autoComplete="off" className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle} /></label>
              <label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.serverLabel}
                <input data-testid="hub-sql-server" value={form.server} onChange={(e) => { setSqlForm({ ...form, server: e.target.value }); setSqlFormError(null); }}
                  placeholder={SQL_SOURCE_COPY.serverPlaceholder} autoComplete="off" className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle} /></label>
              <label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.databaseLabel}
                <input data-testid="hub-sql-database" value={form.database} onChange={(e) => { setSqlForm({ ...form, database: e.target.value }); setSqlFormError(null); }}
                  placeholder={SQL_SOURCE_COPY.databasePlaceholder} autoComplete="off" className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle} /></label>
            </div>
            {/* The ONE shared sign-in control, the same one the Data agent page and Promote use. A SQL source picks a
                way to sign in exactly the way a model connection does, so there is nothing new here to learn. */}
            <div className="mt-2.5">
              <AccountPicker mode={form.authMode} onMode={(m) => setSqlForm({ ...form, authMode: m })}
                tenantId={form.tenantId} onTenantId={(t) => setSqlForm({ ...form, tenantId: t })}
                tenantLabel="where the database lives (optional)" hint={SQL_SOURCE_COPY.signInHint} />
            </div>
            {/* Errors in plain words, in the form, never silent: the form's own refusal and the engine's own words
                land in the SAME place, so a person never has to look in two spots for the reason. */}
            {sqlFormError && <div data-testid="hub-sql-form-error" className="mt-2 rounded-[5px] border px-2 py-1.5 text-[11px] leading-5"
              style={{ borderColor: 'var(--sem-bad)', color: 'var(--sem-bad)' }}>{sqlFormError}</div>}
            {sqlTest && <div data-testid="hub-sql-form-test" className="mt-2 rounded-[5px] border px-2 py-1.5 text-[11px] leading-5"
              style={{ borderColor: sqlTest.ok ? 'var(--sem-good)' : 'var(--sem-bad)', color: sqlTest.ok ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{sqlTest.note}</div>}
            <p className="mt-2 text-[9px] leading-4" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.testExplains}</p>
            <div className="mt-2.5 flex justify-end gap-2">
              <button className={BTN} onClick={closeSqlForm} style={inputStyle}>Cancel</button>
              {/* An unsaved record cannot be probed, so this saves first and says so on the button rather than
                  pretending the typed values were tested where they stand. */}
              <button className={BTN} data-testid="hub-sql-form-test-btn" disabled={sqlBusy != null} onClick={() => void saveAndTestSqlSource()} style={inputStyle}>
                {sqlBusy === 'test' ? SQL_SOURCE_COPY.testing : 'Save and test connection'}</button>
              <button className={BTN} data-testid="hub-sql-save" disabled={sqlBusy != null} onClick={() => void saveSqlSource()}
                style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>
                {form.id ? SQL_SOURCE_COPY.saveChanges : SQL_SOURCE_COPY.save}</button>
            </div>
          </div>
        )}

        {/* A refused Remove gets its own block, not a toast: it carries a list a person has to act on. */}
        {sqlRefused && sqlRemoving && (
          <div className="mb-3 rounded-lg border p-3" data-testid="hub-sql-refused" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
            <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{sqlRemoving.name} was not removed</div>
            <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-warn)' }}>{SQL_SOURCE_COPY.refusedLead}</div>
            {(sqlRefused.checksUsing ?? 0) > 0 && (
              <div className="mt-2 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>
                <b style={{ color: 'var(--sem-fg)' }}>{SQL_SOURCE_COPY.refusedChecks}</b> {sqlSourceUsedByNames(sqlRefused.checkTitles ?? [], sqlRefused.checksUsing ?? 0)}</div>
            )}
            {(sqlRefused.tableMappingsUsing ?? 0) > 0 && (
              <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>
                <b style={{ color: 'var(--sem-fg)' }}>{SQL_SOURCE_COPY.refusedTables}</b> {sqlSourceUsedByNames(sqlRefused.mappedTables ?? [], sqlRefused.tableMappingsUsing ?? 0)}</div>
            )}
            {/* Nothing was counted and nothing was deleted: the engine said why in its own words, so show those. */}
            {(sqlRefused.checksUsing ?? 0) === 0 && (sqlRefused.tableMappingsUsing ?? 0) === 0 && sqlRefused.note &&
              <div className="mt-2 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{sqlRefused.note}</div>}
            <div className="mt-2 text-[11px] leading-5" style={{ color: 'var(--sem-fg)' }}>{SQL_SOURCE_COPY.refusedNext}</div>
            <div className="mt-2.5 flex justify-end"><button className={BTN} data-testid="hub-sql-refused-close" onClick={() => { setSqlRefused(null); setSqlRemoving(null); }} style={inputStyle}>Close</button></div>
          </div>
        )}

        {/* The confirmation an UNUSED source still gets: removing it is quick to do and slow to undo. */}
        {sqlRemoving && !sqlRefused && (
          <div className="mb-3 rounded-lg border p-3" data-testid="hub-sql-remove-confirm" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
            <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Remove {sqlRemoving.name}?</div>
            <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.removeConfirm}</div>
            <div className="mt-2.5 flex justify-end gap-2">
              <button className={BTN} onClick={() => setSqlRemoving(null)} style={inputStyle}>Cancel</button>
              <button className={BTN} data-testid="hub-sql-remove-confirm-btn" disabled={sqlBusy != null} onClick={() => void removeSqlSource(sqlRemoving)}
                style={{ background: 'var(--sem-bad)', borderColor: 'var(--sem-bad)', color: 'var(--sem-on-accent)' }}>Remove</button>
            </div>
          </div>
        )}

        <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          {sqlLoading && <div data-testid="hub-sql-loading" className="px-3 py-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.loading}</div>}
          {/* A list that could not be read says so. An empty list and a broken list are not the same thing, and
              showing the empty state for a failure would invite someone to type a source they already have. */}
          {!sqlLoading && sqlError && <div data-testid="hub-sql-error" className="px-3 py-3 text-[11px] leading-5" style={{ color: 'var(--sem-bad)' }}>
            {SQL_SOURCE_COPY.loadFailed} {sqlError}</div>}
          {!sqlLoading && !sqlError && sqlSources.length === 0 && (
            <div data-testid="hub-sql-empty" className="px-3 py-4 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.empty}</div>
          )}
          {!sqlLoading && !sqlError && sqlSources.map((r) => {
            const failed = r.lastTestOk === false;
            const showing = sqlOpenId === r.id && sqlTest && sqlTest.id === r.id;
            return (
              <div key={r.id} data-testid="hub-sql-row" className="border-b px-3 py-2.5 last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
                <div className="flex items-center gap-2">
                  <span className="truncate text-[12px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{r.name}</span>
                  <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] leading-none font-semibold uppercase tracking-[0.05em]"
                    style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>SQL source</span>
                  {/* A source whose last test failed says so in the row, not only when you open it. */}
                  {failed && <span data-testid="hub-sql-failed" className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] leading-none font-semibold uppercase tracking-[0.05em]"
                    style={{ background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)', borderColor: 'color-mix(in srgb, var(--sem-bad) 40%, transparent)' }}>last test failed</span>}
                  <div className="ml-auto flex shrink-0 items-center gap-1.5">
                    <button className={BTN_COMPACT} data-testid="hub-sql-test" disabled={sqlBusy != null} style={inputStyle}
                      onClick={() => { setSqlOpenId(r.id); void testSqlSource(r); }}>{sqlBusy === r.id ? SQL_SOURCE_COPY.testing : 'Test connection'}</button>
                    <button className={BTN_COMPACT} data-testid="hub-sql-edit" disabled={sqlBusy != null} style={inputStyle} onClick={() => openSqlForm(r)}>Edit</button>
                    <button className={BTN_COMPACT} data-testid="hub-sql-remove" disabled={sqlBusy != null} style={inputStyle}
                      onClick={() => { setSqlRefused(null); setSqlRemoving(r); }}>Remove</button>
                  </div>
                </div>
                <div className="mt-1.5 grid grid-cols-[92px_minmax(0,1fr)] gap-x-2 gap-y-0.5 text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>
                  <span>Server</span><b className="truncate font-semibold" style={{ color: 'var(--sem-fg)' }} title={r.server}>{r.server}</b>
                  <span>Database</span><b className="truncate font-semibold" style={{ color: 'var(--sem-fg)' }}>{r.database}</b>
                  <span>Sign in as</span><b className="truncate font-semibold" style={{ color: 'var(--sem-fg)' }}>{modeLabel(r)}{r.tenantId ? ` · ${r.tenantId}` : ''}</b>
                  <span>Last test</span><b className="truncate font-semibold" style={{ color: failed ? 'var(--sem-bad)' : r.lastTestOk ? 'var(--sem-good)' : 'var(--sem-muted)' }}>
                    {sqlSourceTestLine(r.lastTestOk, shortWhen(r.lastTestedUtc))}</b>
                  <span>Used by</span><b className="truncate font-semibold" style={{ color: 'var(--sem-fg)' }}>{sqlSourceUsageLine(checksUsing(r.id), mapsUsing(r.id), sqlUsage !== null)}</b>
                </div>
                {/* The result of THIS row's Test connection, in the row that was tested. */}
                {showing && <div data-testid="hub-sql-test-result" className="mt-1.5 rounded-[5px] border px-2 py-1 text-[10px] leading-4"
                  style={{ borderColor: sqlTest.ok ? 'var(--sem-good)' : 'var(--sem-bad)', color: sqlTest.ok ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{sqlTest.note}</div>}
              </div>
            );
          })}
        </div>
        <p className="mt-3 text-[9px] leading-5" style={{ color: 'var(--sem-muted)' }}>{SQL_SOURCE_COPY.footnote}</p>
      </section>
    );
  }

  const navBtn = (v: HubView, glyph: string, label: string) => (
    <button data-hubnav={v} onClick={() => setView(v)} className="flex h-[30px] w-full items-center gap-2 rounded-[5px] px-2 text-left text-[11px] leading-none" style={{ background: view === v ? 'var(--sem-accent-soft)' : 'transparent', color: view === v ? 'var(--sem-fg)' : 'var(--sem-muted)', fontWeight: view === v ? 600 : 400 }}>
      <span className="w-3.5 text-center text-[12px]" style={{ color: view === v ? 'var(--sem-accent)' : 'var(--sem-muted)' }} aria-hidden>{glyph}</span><span>{label}</span>
    </button>
  );

  const body = (
    <div ref={panelRef} tabIndex={-1} className="flex h-full min-h-0 w-full flex-col outline-none" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)' }}>
      <header className="flex min-h-[52px] items-center gap-2.5 border-b px-4 py-2" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="min-w-0"><h1 className="text-[15px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Connections</h1>
          <p className="text-[10px] leading-4" style={{ color: 'var(--sem-muted)' }}>Open a model, choose where tests run and manage your saved accounts.</p></div>
        <button className="ml-auto flex h-8 items-center gap-2 rounded-[5px] border px-1.5" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} onClick={() => setView('accounts')} title="Manage the current account">
          <span className="flex h-5 w-5 items-center justify-center rounded-full text-[9px] font-bold" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} aria-hidden>{initials(signedInAccount)}</span>
          <span className="min-w-0 text-left"><span className="block text-[10px] font-semibold truncate max-w-[140px]" style={{ color: 'var(--sem-fg)' }}>{signedInAccount || 'Account unknown'}</span>
            <span className="block text-[9px] truncate max-w-[140px]" style={{ color: 'var(--sem-muted)' }}>{signedInTenant || 'Manage accounts'}</span></span>
        </button>
        <button onClick={onClose} className="inline-flex h-7 w-7 items-center justify-center rounded text-[18px] leading-none" style={{ color: 'var(--sem-muted)' }} aria-label="Close Connections">{'×'}</button>
      </header>

      <div className="grid min-h-0 flex-1" style={{ gridTemplateColumns: '160px minmax(0, 1fr)' }}>
        <nav className="border-r p-2" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} aria-label="Connections sections">
          <div className="flex h-5 items-center px-2 text-[9px] font-semibold uppercase tracking-[0.1em]" style={{ color: 'var(--sem-muted)' }}>Models</div>
          {navBtn('open', '▣', 'Open a model')}
          {navBtn('setup', '⑂', 'Current setup')}
          <div className="mx-1.5 my-2 border-t" style={{ borderColor: 'var(--sem-border)' }} />
          {/* The fourth role. It sits in its own group because a SQL source is not a model: it never answers DAX,
              it answers the independent number a check compares against. */}
          <div className="flex h-5 items-center px-2 text-[9px] font-semibold uppercase tracking-[0.1em]" style={{ color: 'var(--sem-muted)' }}>Data</div>
          {navBtn('sqlsources', '▤', 'SQL sources')}
          <div className="mx-1.5 my-2 border-t" style={{ borderColor: 'var(--sem-border)' }} />
          <div className="flex h-5 items-center px-2 text-[9px] font-semibold uppercase tracking-[0.1em]" style={{ color: 'var(--sem-muted)' }}>Identity</div>
          {navBtn('accounts', '○', 'Accounts')}
          {navBtn('history', '↺', 'History')}
          <div className="mx-1 mt-2.5 rounded-md border p-2" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
            <div className="text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Open now</div>
            <div className="mt-1 text-[10px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{roleValue(editing, 'No model open')}</div>
            {editing?.source && <div className="text-[9px] leading-4 break-all" style={{ color: 'var(--sem-muted)' }} title={endpointText(editing.source)} data-testid="hub-open-now-path">{endpointText(editing.source)}</div>}
            <div className="text-[9px] leading-4" style={{ color: 'var(--sem-muted)' }}>Editing, tests, and publishing can point to different models.</div>
          </div>
        </nav>

        <div className="min-h-0 overflow-auto" style={{ background: 'var(--sem-bg)' }}>
          {openConfirm && pendingOpen && (
            <div className="m-4 rounded-lg border p-3" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }} data-testid="hub-open-unsaved">
              <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>This model has unsaved changes.</div>
              <div className="mt-2 text-[11px] leading-5" style={{ color: 'var(--sem-warn)' }}>Opening another model throws those changes away unless you save first.</div>
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => { setOpenConfirm(false); setPendingOpen(null); }} style={inputStyle}>Cancel</button>
                <button className={BTN} data-testid="hub-open-discard" disabled={busyId != null} onClick={() => { const p = pendingOpen; if (p === 'path') void openTypedLocal(); else if (p) void openModel(p); }} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Discard</button>
              </div>
            </div>
          )}
          {createOpen && (
            <div className="m-4 rounded-lg border p-3" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
              <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Create a new model</div>
              <label className="mt-2 block text-[10px] font-semibold" style={{ color: 'var(--sem-muted)' }}>Model name
                <input data-testid="hub-create-name" value={createName} onChange={(e) => setCreateName(e.target.value)} placeholder="Sales analytics" autoFocus className="mt-1 h-8 w-full rounded-[5px] border px-2.5 text-[11px] font-normal" style={inputStyle} />
              </label>
              {createDirty && <div className="mt-2 text-[11px] leading-5" style={{ color: 'var(--sem-warn)' }}>Creating {createName.trim() || 'a new model'} replaces the open model, which has unsaved changes.</div>}
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => { setCreateOpen(false); setCreateConfirm(false); }} style={inputStyle}>Cancel</button>
                <button className={BTN} data-testid="hub-create-submit" disabled={!createName.trim() || busyId != null} onClick={() => void submitCreate()} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{createDirty ? 'Create and replace open model' : 'Create model'}</button>
              </div>
            </div>
          )}
          {switchTarget && <AccountDialog r={switchTarget} />}
          {pendingDefault && (
            <div className="m-4 rounded-lg border p-3" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
              <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Make {pendingDefault.username} the default account?</div>
              <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{profileDefaultBlast(pendingDefault)} Every switch is recorded in the connection history.</div>
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => setPendingDefault(null)} style={inputStyle}>Cancel</button>
                <button className={BTN} data-testid="hub-make-default-confirm" disabled={busyId != null} onClick={() => void makeDefaultFor(pendingDefault)} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Make default</button>
              </div>
            </div>
          )}
          {view === 'open' && OpenView()}
          {view === 'setup' && SetupView()}
          {view === 'accounts' && AccountsView()}
          {view === 'history' && HistoryView()}
          {view === 'add' && AddView()}
          {view === 'work' && WorkView()}
          {view === 'sqlsources' && SqlSourcesView()}
        </div>
      </div>

      <footer className="flex items-center gap-2 border-t px-4 py-1 text-[9px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
        <span aria-hidden style={{ color: 'var(--sem-accent)' }}>{'◇'}</span>
        <span>Connections and sign-in history are stored on this device. Environment labels also protect your assistant's actions.</span>
        <button className={`${BTN_QUIET} ml-auto`} style={{ color: 'var(--sem-accent)' }} onClick={() => openLocalModel()}>Open local file or project</button>
        <span className="text-[9px]">Existing files remain user-owned, including files already in source control.</span>
      </footer>
      {error && !switchTarget && <div className="absolute bottom-14 left-4 right-4 rounded-md border px-3 py-2 text-[11px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-bad)', color: 'var(--sem-bad)' }}>{error}</div>}
    </div>
  );

  // ONE shared floating frame for BOTH doors (P1). The hub is a centered floating dialog whether it overlays Studio or
  // is hosted full-page in the standalone panel; only the close callback differs (the caller wires overlay-close vs
  // panel-dispose). The backdrop dims + blurs; a click on it, or Escape, dismisses. In the standalone panel the area
  // behind the backdrop is just the empty editor-tab background, which is expected.
  return (
    <div className="fixed z-[80] grid place-items-center" role="dialog" aria-modal="true" aria-label="Connections"
      style={{ inset: '36px 0 23px', padding: 18, background: 'rgba(8,10,13,.66)', backdropFilter: 'blur(2px)' }}
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="relative flex flex-col overflow-hidden rounded-[10px] border shadow-2xl"
        style={{ width: 'min(1130px, 96vw)', height: 'min(790px, calc(100vh - 84px))', minHeight: 'min(590px, calc(100vh - 84px))', borderColor: 'var(--sem-border)' }}>
        {body}
      </div>
    </div>
  );
}

function MenuItem({ children, onClick, disabled, danger }: { children: ReactNode; onClick: () => void; disabled?: boolean; danger?: boolean }) {
  return (
    <button role="menuitem" disabled={disabled} onClick={onClick} className="flex h-7 w-full items-center px-3 text-left text-[10px] leading-none disabled:opacity-40 hover:bg-[color:var(--sem-surface-2)]"
      style={{ color: danger ? 'var(--sem-bad)' : 'var(--sem-fg)' }}>{children}</button>
  );
}

function QuickCard({ glyph, title, body, onClick }: { glyph: string; title: string; body: string; onClick: () => void }) {
  return (
    <button onClick={onClick} className="grid min-h-[50px] grid-cols-[14px_minmax(0,1fr)] grid-rows-[auto_auto] content-center gap-x-2 rounded-md border px-2.5 py-1.5 text-left" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <span className="row-span-2 mt-0.5 text-[12px] leading-4" style={{ color: 'var(--sem-muted)' }} aria-hidden>{glyph}</span>
      <b className="block text-[10px] leading-4" style={{ color: 'var(--sem-fg)' }}>{title}</b>
      <span className="block text-[9px] leading-[13px]" style={{ color: 'var(--sem-muted)' }}>{body}</span>
    </button>
  );
}

// One opening outcome: the verb as the label, its one-line consequence as VISIBLE microcopy right under it, so the UI
// explains itself at the decision point (Kane's direct ask). The primary outcome carries the accent.
function OutcomeButton({ label, micro, onClick, disabled, primary, busy }: { label: string; micro: string; onClick: () => void; disabled?: boolean; primary?: boolean; busy?: boolean }) {
  return (
    <button type="button" disabled={disabled || busy} onClick={onClick} data-testid="hub-outcome"
      className="grid w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-2 rounded-md border px-3 py-1.5 text-left disabled:opacity-50"
      style={primary
        ? { background: 'var(--sem-accent-soft)', borderColor: 'color-mix(in srgb, var(--sem-accent) 45%, var(--sem-border))' }
        : { background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <span className="min-w-0">
        <span className="block text-[11px] font-semibold" style={{ color: primary ? 'var(--sem-accent)' : 'var(--sem-fg)' }}>{label}</span>
        {/* Progress is VISIBLE (Kane: Query "did nothing"): the same slot shows "Connecting..." while the connect runs. */}
        <span className="mt-0.5 block text-[9px] leading-[13px]" style={{ color: 'var(--sem-muted)' }}>{busy ? 'Connecting...' : micro}</span>
      </span>
      <span aria-hidden className="text-[13px]" style={{ color: 'var(--sem-muted)' }}>{busy ? '⋯' : '›'}</span>
    </button>
  );
}

// Standalone host: main.tsx mounts this (instead of <App/>) when the host page sets window.__semanticusInitialView =
// 'connections'. The hub fills the dedicated panel (as the same shared floating frame); its close button disposes the
// panel via the host. An optional initial section lets "Manage Connections" land on Current setup.
export function ConnectionsStandalone({ initialSection }: { initialSection?: HubView }) {
  // closeConnectionsPanel is a module-level import (stable identity), passed directly so the hub's focus/Escape effect
  // never retriggers on a re-render (a fresh arrow would). The hub disposes the panel via this on close.
  return <ConnectionsHub open standalone initialView={initialSection} onClose={closeConnectionsPanel} />;
}
