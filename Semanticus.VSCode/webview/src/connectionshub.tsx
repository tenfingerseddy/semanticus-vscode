import { useEffect, useState, useRef, type ReactNode } from 'react';
import { openLocalModel, pickWorkingCopyFolder, rpc, useAsReference, onConnectionChange, onOpenConnections, announceConnectionChange, closeConnectionsPanel } from './bridge';
import { useConnection, type ConnectionContextModel, type SessionInfo } from './connection';

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

export type HubView = 'open' | 'setup' | 'accounts' | 'history' | 'add' | 'work';

// A caller inside Studio (e.g. Deploy's "Change publish destination") can ask the hub to open on a specific view
// next. The hub is mounted once in App with no initialView prop, so the request is a module-level flag set just
// before openConnections() flips `open`; the hub consumes and clears it on the open transition below.
let nextView: HubView | null = null;
export function openConnectionsOnView(view: HubView): void {
  nextView = view;
}

const inputStyle = { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' } as const;
// Button scale system (sol's refinement): explicit heights remove inherited line-height / host-scaling inflation, the
// root cause of oversized buttons. Standard 30px, compact 24px, quiet 24px link, overflow 24x24.
const BTN = 'inline-flex h-[30px] items-center justify-center gap-1.5 rounded-[5px] border px-2.5 text-[11px] leading-none font-semibold disabled:opacity-40';
const BTN_COMPACT = 'inline-flex h-6 min-w-0 items-center justify-center gap-1 rounded border px-2 text-[10px] leading-none font-semibold disabled:opacity-40';
const BTN_QUIET = 'inline-flex h-6 items-center justify-center gap-1 rounded px-1.5 text-[10px] leading-none font-semibold disabled:opacity-40';
const BTN_OVERFLOW = 'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded border p-0 text-[16px] leading-none font-semibold disabled:opacity-40';
const short = (value?: string) => {
  const v = (value || '').replace(/[\\/]+$/, '');
  if (!v) return '';
  const i = Math.max(v.lastIndexOf('/'), v.lastIndexOf('\\'));
  return i >= 0 ? v.slice(i + 1) : v;
};
// Power BI Desktop names a running local model's database with a bare GUID, which tells a person nothing. Detect it so
// a local row falls back to an honest friendly name (the Desktop model/file name, else "Local running model") and never
// shows the GUID as the display name (P5).
const isGuidName = (s?: string) => !!s && /^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$/i.test(s.trim());
const nameOf = (r: ConnectionRecord) => {
  if (r.kind === 'file') return r.modelName || short(r.endpoint) || 'Local model';
  if (r.kind === 'localDesktop') return r.modelName || (isGuidName(r.database) ? '' : (r.database || '')) || 'Local running model';
  return r.modelName || r.database || short(r.endpoint) || 'Model';
};
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
  return side?.available ? (side.modelName || side.database || short(side.source) || fallback) : fallback;
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

// One role card in the Current setup grid (Editing / Tests and queries / Publish to / Reference model). A card either
// offers a "Change ..." action (routing to Open a model) OR an inline control (the publish / reference pickers that
// now own those roles, since publish + reference left the Open view entirely).
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
  const [pendingDefault, setPendingDefault] = useState<AccountProfile | null>(null);  // a make-default awaiting its blast-radius confirmation
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
    if (section === 'open' || section === 'setup' || section === 'accounts' || section === 'history' || section === 'add') {
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
  // The trigger that opened the current overflow menu, captured on open so focus can return to it on close (Escape or a
  // selection unmounts the focused menuitem — without this, focus would orphan on <body>).
  const menuTriggerRef = useRef<HTMLButtonElement | null>(null);
  // When a row's overflow menu opens, move focus into it (ARIA menu semantics; Arrow/Home/End are handled on it). When
  // it closes, return focus to the trigger that opened it, never orphaning focus on <body>.
  useEffect(() => {
    if (menuId) { document.querySelector<HTMLButtonElement>('[data-hub-menu] [role="menuitem"]')?.focus(); return; }
    if (menuTriggerRef.current) { menuTriggerRef.current.focus(); menuTriggerRef.current = null; }
  }, [menuId]);
  useEffect(() => {
    if (!shown) return;
    restoreRef.current = document.activeElement as HTMLElement | null;   // capture the opener BEFORE moving focus in
    if (searchWanted.current && searchRef.current) { searchWanted.current = false; searchRef.current.focus(); }
    else panelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { if (menuIdRef.current) setMenuId(null); else onClose(); return; }
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
  const reference = context?.reference;
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
  const openModel = async (r: ConnectionRecord, opts?: { forceReauth?: boolean; accountProfileId?: string; makeDefault?: boolean; loginHint?: string }) => {
    setBusyId('open:' + r.id); setError(null); setMenuId(null);
    try {
      const s = await rpc<SessionInfo>('sessionInfo').catch(() => null);
      if (s?.hasUnsavedChanges && !openConfirm) { setPendingOpen(r); setOpenConfirm(true); setBusyId(null); return; }
      const discard = !!s?.hasUnsavedChanges;
      if (r.kind === 'localDesktop') await rpc('openLocal', r.endpoint, r.database || null, discard);
      else if (r.kind === 'file') await rpc('open', r.endpoint, discard);
      else await rpc('openLive', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null,
        opts?.forceReauth ?? false, opts?.accountProfileId ?? null, opts?.makeDefault ?? false, opts?.loginHint ?? null, 'human', discard);
      setSwitchTarget(null); setSwitchPurpose(null); setPendingDefault(null); setOpenConfirm(false); setPendingOpen(null);
      await refresh(); await load(); await reloadHistory();
      onClose();
    } catch (e) {
      setError(String((e as Error).message ?? e));
      await reloadHistory();   // a FAILED open also appends a timeline event (a cancelled sign-in / superseded open)
      if (switchTarget) setSwitchTarget(r);   // a failed authorization returns HERE (the account choice), never an endpoint form
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
  const openAccountChoice = (r: ConnectionRecord, purpose: 'open' | 'query' | null = null) => { setSwitchPurpose(purpose); setSwitchTarget(r); };
  const applyAccount = async (r: ConnectionRecord, opts?: { forceReauth?: boolean; accountProfileId?: string; makeDefault?: boolean; loginHint?: string }) => {
    const asQuery = switchPurpose ? switchPurpose === 'query' : isQueryRole(r);   // honour explicit intent; else infer from role
    if (!asQuery) { await openModel(r, opts); return; }
    setBusyId('open:' + r.id); setError(null); setMenuId(null);
    try {
      await rpc('connectXmla', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null,
        opts?.forceReauth ?? false, opts?.accountProfileId ?? null, opts?.makeDefault ?? false, opts?.loginHint ?? null);
      setSwitchTarget(null); setSwitchPurpose(null); setPendingDefault(null);   // the query account switch succeeded — dismiss the dialog
      await refresh(); await load(); await reloadHistory();
      announceConnectionChange();
      onClose();
    } catch (e) {
      setError(String((e as Error).message ?? e));
      await reloadHistory();
      if (switchTarget) setSwitchTarget(r);   // a failed authorization returns HERE (the account choice), never an endpoint form
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
    if (!res.ok) setError(res.message || 'That model could not be connected for queries.');   // the engine's own words, not a generic line
    await load(); await reloadHistory(); setBusyId(null);
    if (res.ok) onClose();   // query attach is a terminal action — close in both modes (the contract above)
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
  // Publish/reference/work-locally and per-open account choices moved off the row (detail pane, Current setup, overflow).
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
              <span className="mt-0.5 block truncate text-[9px] leading-3" style={{ color: 'var(--sem-muted)' }} title={r.endpoint}>{r.kind === 'file' ? r.endpoint : r.kind === 'localDesktop' ? (short(r.endpoint) || 'Local running model') : identity}</span>
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
            <div className="text-[10px] leading-4 break-words" style={{ color: 'var(--sem-muted)' }}>{r.kind === 'file' ? 'Local file or project' : r.kind === 'localDesktop' ? 'Local running model' : 'Published model'}<br />{r.endpoint}</div>
          </div>
          <EnvBadge r={r} />
        </div>

        {statuses.length > 0 && (
          <div className="mt-1.5 flex flex-wrap gap-1.5">
            {statuses.map((s) => <span key={s} className="inline-flex items-center rounded-full border px-2 py-0.5 text-[9px] leading-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{s}</span>)}
          </div>
        )}

        <div className="mt-1.5 grid grid-cols-[96px_minmax(0,1fr)] gap-x-2 gap-y-1.5 border-y py-1.5 text-[10px] leading-4" style={{ borderColor: 'var(--sem-border)' }}>
          <div style={{ color: 'var(--sem-muted)' }}>Model</div><div style={{ color: 'var(--sem-fg)' }}>{r.database && !isGuidName(r.database) ? r.database : short(r.endpoint)}</div>
          <div style={{ color: 'var(--sem-muted)' }}>Environment</div>
          {/* The badge stays visible; editing the label is behind an explicit control (a label change moves AI Assistant
              permissions, so it is a deliberate act, not an always-open dropdown). */}
          <div>
            {editEnv && isCloud ? (
              <>
                <select data-testid="hub-env-select" disabled={busyId != null} value={(r.label || '')} onChange={(e) => { void relabel(r, e.target.value); setEditEnv(false); }} className="h-6 max-w-[220px] rounded border px-1.5 text-[10px]" style={inputStyle}>
                  {ENV_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
                <div className="mt-1 text-[9px] leading-[13px]" style={{ color: 'var(--sem-muted)' }}>This label controls AI Assistant permissions. Only a person can change it.</div>
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

        {/* The three outcomes, each explaining itself at the decision point (Kane's direct ask). Publish and reference
            are NOT here: they are Current-setup roles, not ways to open. */}
        <div className="mt-1.5 text-[9px] font-semibold uppercase tracking-[0.08em]" style={{ color: 'var(--sem-muted)' }}>Open this model</div>
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
    return (
      <div data-testid="hub-account-dialog" className="m-4 rounded-lg border p-3" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, var(--sem-border))', background: 'var(--sem-surface)' }}>
        <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Who should open {nameOf(r)}?</div>
        <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>This choice applies to this open. The saved endpoint stays the same whichever account you pick.</div>
        <div className="mt-2.5 rounded-md border overflow-hidden" style={{ borderColor: 'var(--sem-border)' }}>
          {list.map((p) => (
            <div key={p.id} className="flex items-center gap-2.5 border-b px-2.5 py-2 last:border-b-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
              <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[10px] font-bold" style={{ background: p.isDefault ? 'var(--sem-accent)' : 'var(--sem-surface)', color: p.isDefault ? 'var(--sem-on-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} aria-hidden>{initials(p.username)}</span>
              <div className="min-w-0 flex-1">
                <div className="truncate text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{p.username}</div>
                <div className="truncate text-[9px]" style={{ color: 'var(--sem-muted)' }}>{[p.tenantId ? `${p.tenantId} tenant` : 'tenant unknown', p.signedIn ? 'signed in' : 'signed out', p.lastUseUtc ? `last used ${shortWhen(p.lastUseUtc)}` : null].filter(Boolean).join(' · ')}</div>
              </div>
              <div className="flex shrink-0 items-center gap-1.5">
                {/* Signed-out is checked BEFORE default (MED, sol): a DEFAULT profile that is signed out must still get a
                    targeted "Sign in and use" (forceReauth + loginHint re-auths THIS identity) - the bottom "Open with
                    default" has neither, so it could sign in a different account. */}
                {!p.signedIn
                  ? <button className={BTN_QUIET} data-testid="hub-signin-and-use" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => void applyAccount(r, { forceReauth: true, loginHint: p.username })}>Sign in and use</button>
                  : p.isDefault
                    ? <span className="inline-flex h-[18px] items-center rounded-full border px-1.5 text-[8px] font-semibold uppercase" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>default</span>
                    : <button className={BTN_QUIET} data-testid="hub-use-for-open" style={{ color: 'var(--sem-accent)' }} disabled={busyId != null} onClick={() => useForThisOpen(r, p)}>Use for this open</button>}
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
          <button className={BTN} onClick={() => { setSwitchTarget(null); setSwitchPurpose(null); setPendingDefault(null); }} style={inputStyle}>Cancel</button>
          <button className={BTN} data-testid="hub-open-default" disabled={busyId != null} onClick={() => void applyAccount(r)} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{defName ? `Open with ${defName}` : 'Open with the default account'}</button>
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
            <QuickCard glyph="+" title="Create blank model" body="Name a new model and build it with AI Assistant." onClick={openCreate} />
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
            {/* The list owns its scroll at >=900px (min-height:0 + overflow); below 900px it flows into the one outer scroll. */}
            <div className="min-[900px]:min-h-0 min-[900px]:flex-1 min-[900px]:overflow-auto">
              {loading && <div className="p-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Loading connections...</div>}
              {!loading && visibleRecords.map((r) => <ModelRow key={r.id} r={r} />)}
              {!loading && visibleRecords.length === 0 && <div className="p-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No remembered models match. Open a new model above.</div>}
            </div>
          </section>

          {/* The detail pane now OWNS its scroll at >=900px, so a tall detail can never push the dialog past its fixed
              height — the fix for the outer scrollbar + cut-off cards. The new-model sources left this column entirely
              (they are the top strip now). */}
          <section className="flex min-h-0 flex-col overflow-hidden rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} aria-live="polite">
            <div className="min-[900px]:min-h-0 min-[900px]:flex-1 min-[900px]:overflow-auto"><DetailPane /></div>
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
          {/* Publish + reference are Current-setup roles, not ways to open, so they own their assignment here (they left
              the Open view entirely). Choosing a destination only links it; publishing stays a separate reviewed action. */}
          <RoleCard n={3} label="Publish to" value={roleValue(publishing, 'Not linked')}
            detail={publishing?.available ? 'Publishing requires a separate review and confirmation.' : 'Choose the live model you want to publish to'} missing={!publishing?.available}
            control={<label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Set as publish destination
              <select value={publishing?.connectionId || ''} disabled={busyId != null} onChange={(e) => { const t = xmlaRecords.find((x) => x.id === e.target.value); if (t) void setPublish(t); }} className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle}>
                <option value="">Choose a published destination</option>
                {xmlaRecords.map((x) => <option key={x.id} value={x.id}>{nameOf(x)}</option>)}
              </select></label>} />
          <RoleCard n={4} label="Reference model" value={roleValue(reference, 'Not set')}
            detail={reference?.available ? 'Browse objects from this model and copy them into the open model.' : 'Browse a second model to copy objects from'} missing={!reference?.available}
            control={<label className="block text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Choose a reference model
              <select value={reference?.connectionId || ''} disabled={busyId != null} onChange={(e) => { const t = xmlaRecords.find((x) => x.id === e.target.value); if (t) useAsReference(t); }} className="mt-1 h-8 w-full rounded-[5px] border px-2 text-[11px] font-normal" style={inputStyle}>
                <option value="">Choose a reference model</option>
                {xmlaRecords.map((x) => <option key={x.id} value={x.id}>{nameOf(x)}</option>)}
              </select></label>} />
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
            <span className="text-[9px] font-normal" style={{ color: 'var(--sem-muted)' }}>Environment labels control AI Assistant permissions. Only a person can change them.</span></label>
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
          <div className="flex h-5 items-center px-2 text-[9px] font-semibold uppercase tracking-[0.1em]" style={{ color: 'var(--sem-muted)' }}>Identity</div>
          {navBtn('accounts', '○', 'Accounts')}
          {navBtn('history', '↺', 'History')}
          <div className="mx-1 mt-2.5 rounded-md border p-2" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
            <div className="text-[9px] font-semibold uppercase tracking-[0.06em]" style={{ color: 'var(--sem-muted)' }}>Open now</div>
            <div className="mt-1 text-[10px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{roleValue(editing, 'No model open')}</div>
            {editing?.source && <div className="text-[9px] leading-4 break-all" style={{ color: 'var(--sem-muted)' }} title={editing.source} data-testid="hub-open-now-path">{editing.source}</div>}
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
        </div>
      </div>

      <footer className="flex items-center gap-2 border-t px-4 py-1 text-[9px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)', color: 'var(--sem-muted)' }}>
        <span aria-hidden style={{ color: 'var(--sem-accent)' }}>{'◇'}</span>
        <span>Connections and sign-in history are stored on this device. Environment labels also protect AI Assistant actions.</span>
        <button className={`${BTN_QUIET} ml-auto`} style={{ color: 'var(--sem-accent)' }} onClick={() => openLocalModel()}>Open local file or project</button>
        <span className="text-[9px]">Existing files remain user-owned, including files already in source control.</span>
      </footer>
      {error && <div className="absolute bottom-14 left-4 right-4 rounded-md border px-3 py-2 text-[11px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-bad)', color: 'var(--sem-bad)' }}>{error}</div>}
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
