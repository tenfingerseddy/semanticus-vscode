import { useEffect, useState, useRef } from 'react';
import { openLocalModel, pickWorkingCopyFolder, rpc, useAsReference, onConnectionChange } from './bridge';
import { useConnection, type ConnectionContextModel } from './connection';

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
}

// One target's silently-probed account (mirrors Engine ConnectionAccountProbe). `account` is who the NEXT open signs in
// as — ONLY a live sign-in record, undefined when unknown; `previousAccount` is provenance ("last opened as <x>").
interface AccountProbe { id: string; account?: string; previousAccount?: string; tenantId?: string; }

// One device-local connection-timeline event (mirrors Engine ConnectionHistoryEvent). Holds no credential.
interface HistoryEvent { id?: string; kind: string; account?: string; endpoint?: string; database?: string; tenantId?: string; ok: boolean; detail?: string; whenUtc?: string; }
// A plain-language label for a timeline event's kind — the drawer never shows the raw engine kind token.
const historyKindLabel = (e: HistoryEvent): string => {
  switch (e.kind) {
    case 'connect': return e.ok ? 'Connected for tests' : 'Connect failed';
    case 'open': return e.ok ? 'Opened' : 'Open failed';
    case 'switch': return e.ok ? 'Switched account' : 'Account switch failed';
    case 'signin': return e.ok ? 'Signed in' : 'Sign-in failed';
    case 'role': return e.detail ? e.detail.charAt(0).toUpperCase() + e.detail.slice(1) : 'Role changed';
    default: return e.ok ? e.kind : `${e.kind} failed`;
  }
};
// Short local time for a timeline row from an ISO-8601 UTC stamp; falls back to the raw value if it won't parse.
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

const BTN = 'rounded-md border px-2.5 py-1.5 text-[11px] font-semibold disabled:opacity-40';
const inputStyle = { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' } as const;
const short = (value?: string) => {
  const v = (value || '').replace(/[\\/]+$/, '');
  if (!v) return '';
  const i = Math.max(v.lastIndexOf('/'), v.lastIndexOf('\\'));
  return i >= 0 ? v.slice(i + 1) : v;
};
const nameOf = (r: ConnectionRecord) => r.modelName || r.database || short(r.endpoint) || 'Model';
// Up to two initials for the identity avatar, from a UPN (kane@contoso.com -> K) or a "first.last" local part.
const initials = (account?: string): string => {
  const local = (account || '').split('@')[0];
  const parts = local.split(/[.\-_ ]+/).filter(Boolean);
  if (!parts.length) return '?';
  return (parts[0][0] + (parts[1]?.[0] ?? '')).toUpperCase();
};

function Role({ label, value, detail, missing }: { label: string; value: string; detail: string; missing?: boolean }) {
  return (
    <div className="grid grid-cols-[92px_minmax(0,1fr)] gap-3 px-3 py-2 border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="uppercase text-[9px] font-semibold tracking-[0.07em] pt-0.5" style={{ color: 'var(--sem-muted)' }}>{label}</div>
      <div className="min-w-0">
        <div className="text-[12px] font-semibold truncate" style={{ color: missing ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{value}</div>
        <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={detail}>{detail}</div>
      </div>
    </div>
  );
}

function roleValue(side: ConnectionContextModel | undefined, fallback: string): string {
  return side?.available ? (side.modelName || side.database || short(side.source) || fallback) : fallback;
}

export function ConnectionsDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { context, session, busy: connectionBusy, connectLocal, connectXmla, refresh } = useConnection();
  const [records, setRecords] = useState<ConnectionRecord[]>([]);
  const [probes, setProbes] = useState<AccountProbe[]>([]);
  const [loading, setLoading] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [work, setWork] = useState<WorkSetup | null>(null);
  const [switchTarget, setSwitchTarget] = useState<ConnectionRecord | null>(null);   // the record a tenant-wide account switch is being confirmed for
  const [addOpen, setAddOpen] = useState(false);
  const [endpoint, setEndpoint] = useState('');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState('interactive');
  const [history, setHistory] = useState<HistoryEvent[]>([]);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [historyFilter, setHistoryFilter] = useState('');   // '' = all connections; else a connection id

  const load = async () => {
    setLoading(true);
    try {
      // Probe alongside the list: the probe is the truth of who the NEXT open signs in as (the tenant-wide record),
      // so a target whose sibling switched identities shows the CURRENT account here, not its own stale last-used one.
      const [recs, prb] = await Promise.all([
        rpc<ConnectionRecord[]>('listConnections'),
        rpc<AccountProbe[]>('probeConnectionAccounts').catch(() => [] as AccountProbe[]),
      ]);
      setRecords(recs ?? []); setProbes(prb ?? []); setError(null);
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setLoading(false); }
  };
  // The connection timeline, optionally filtered to one connection id (per-connection filter). Read-only + credential-free.
  const loadHistory = async (filter: string) => {
    try { setHistory((await rpc<HistoryEvent[]>('listConnectionHistory', filter || null)) ?? []); }
    catch { setHistory([]); }
  };
  // Reload the timeline when it's visible. Shared so EVERY path that appends an event refreshes it — including a FAILED
  // open (the catch below) and a role change (setPublish), both of which append but reloaded nothing before (MED 5).
  const reloadHistory = async () => { if (historyOpen) await loadHistory(historyFilter); };
  // Who the NEXT open of this target signs in as — ONLY a live sign-in record (probe.account); undefined when unknown.
  // The per-target lastAccount is never the prediction, only provenance ("last opened as <x>") (HIGH 3).
  const probeOf = (r: ConnectionRecord): AccountProbe | undefined => probes.find((p) => p.id === r.id);
  const accountOf = (r: ConnectionRecord): string | undefined => probeOf(r)?.account;
  useEffect(() => { if (open) { void load(); void refresh(); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  // Load the timeline whenever the History panel opens or its per-connection filter changes.
  useEffect(() => { if (open && historyOpen) void loadHistory(historyFilter); }, [open, historyOpen, historyFilter]); // eslint-disable-line react-hooks/exhaustive-deps
  // A connection-state change from OUTSIDE this drawer's own RPCs — an MCP-door connect/disconnect (relayed from
  // model/activity) or a reference set/clear the host just completed — must reload every panel, so none goes stale
  // (MED 5 / MED 7). The timeline reloads too when it's visible, since a switch/open appends to it.
  useEffect(() => {
    if (!open) return;
    return onConnectionChange(() => { void load(); void refresh(); if (historyOpen) void loadHistory(historyFilter); });
  }, [open, historyOpen, historyFilter]); // eslint-disable-line react-hooks/exhaustive-deps
  // aria-modal promises an inert background, so deliver one: focus moves INTO the drawer on open,
  // Tab cycles inside it, and closing returns focus to whatever opened it.
  const panelRef = useRef<HTMLElement | null>(null);
  const restoreRef = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (!open) return;
    restoreRef.current = document.activeElement as HTMLElement | null;
    panelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { onClose(); return; }
      if (e.key !== 'Tab' || !panelRef.current) return;
      const els = panelRef.current.querySelectorAll<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
      if (!els.length) return;
      const first = els[0], last = els[els.length - 1];
      const inside = panelRef.current.contains(document.activeElement);
      if (e.shiftKey && (!inside || document.activeElement === first)) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && (!inside || document.activeElement === last)) { e.preventDefault(); first.focus(); }
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); restoreRef.current?.focus(); };
  }, [open, onClose]);
  if (!open) return null;

  const queryWith = async (r: ConnectionRecord) => {
    setBusyId('query:' + r.id); setError(null);
    const ok = r.kind === 'localDesktop'
      ? await connectLocal(r.endpoint)
      : await connectXmla(r.endpoint, r.database || '', r.authMode || 'interactive', r.tenantId || null);
    if (!ok) setError('That model could not be connected for tests and queries.');
    // A connect appends a timeline event — reload it too when the History panel is open (MED 7).
    await load(); await reloadHistory(); setBusyId(null);
  };

  const openLive = async (r: ConnectionRecord, forceReauth = false) => {
    setBusyId('open:' + r.id); setError(null);
    try {
      if (r.kind === 'localDesktop') await rpc('openLocal', r.endpoint, r.database || null);
      else await rpc('openLive', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null, forceReauth);
      // An open/switch/sign-in appends a timeline event — reload it too when the History panel is open (MED 7).
      await refresh(); await load(); await reloadHistory();
    } catch (e) {
      setError(String((e as Error).message ?? e));
      // A FAILED open ALSO appends a timeline event (a cancelled sign-in / superseded open) — reload so the timeline
      // shows it instead of going stale (MED 5).
      await reloadHistory();
    }
    finally { setBusyId(null); }
  };

  // Phase 1 account switch is tenant-wide (the sign-in cache holds one slot per tenant AND credential family), so we
  // quantify the blast radius before proceeding: only records that share BOTH the tenant and the sign-in family the
  // switch acts on are re-pointed — an interactive switch never touches device-code / service-principal / azcli rows.
  const blastRadius = (r: ConnectionRecord): string => {
    const tenant = (r.tenantId || '').toLowerCase();
    const family = credentialFamily(r.authMode);
    // The cache holds one slot per (tenant, credential family). Count from the SAME slot the selector actually uses:
    // records with the same tenant string (including the empty one) and the same family.
    const affected = records.filter((o) => o.kind === 'xmla' && o.id !== r.id
      && (o.tenantId || '').toLowerCase() === tenant && credentialFamily(o.authMode) === family);
    // A record with NO tenant recorded shares the empty-tenant slot with EVERY other tenantless record of the same
    // family — WHATEVER tenant each actually belongs to — so the honest scope is "models with no tenant recorded",
    // never "on the same tenant" (which would falsely imply they are all the same tenant) (HIGH 3).
    if (!tenant) {
      return affected.length
        ? `${affected.length} other remembered model${affected.length === 1 ? '' : 's'} with no tenant recorded also share this sign-in and will open with the new account, whatever tenant they belong to.`
        : 'No other remembered models without a tenant recorded are affected.';
    }
    return affected.length
      ? `${affected.length} other remembered model${affected.length === 1 ? '' : 's'} signed in the same way on this tenant will also open with the new account from now on.`
      : 'No other remembered models on this tenant are affected.';
  };
  const confirmSwitch = async () => {
    const r = switchTarget;
    if (!r) return;
    setSwitchTarget(null);
    await openLive(r, true);   // forceReauth: shows the account picker, then persists the newly chosen identity
  };

  const setPublish = async (r: ConnectionRecord) => {
    setBusyId('publish:' + r.id); setError(null);
    try {
      await rpc('setPublishDestination', r.id, 'human');
      // Setting a publish destination appends a role event — reload the timeline too when it's open (MED 5).
      await refresh(); await load(); await reloadHistory();
    } catch (e) { setError(String((e as Error).message ?? e)); }
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
    const parent = source.workingFolder ? null : await pickWorkingCopyFolder();
    if (!source.workingFolder && !parent) return;
    const queryId = source.id;
    const publishId = source.kind === 'xmla' ? source.id : (source.publishConnectionId || '');
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
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const addAndConnect = async () => {
    if (!endpoint.trim()) return;
    setBusyId('add'); setError(null);
    try {
      const ok = await connectXmla(endpoint.trim(), database.trim(), authMode);
      if (!ok) setError('The connection did not complete. Check the endpoint, model name, and sign-in.');
      else { setAddOpen(false); setEndpoint(''); setDatabase(''); await load(); }
    } finally { setBusyId(null); }
  };

  const editing = context?.editing;
  const querying = context?.querying;
  const publishing = context?.publishing;
  const reference = context?.reference;
  const xmlaRecords = records.filter((r) => r.kind === 'xmla');

  // Identity is always shown first: which account the live model is signed in as, and its tenant. This must be the
  // ACTIVE credential the engine reports (session.currentAccount) — never a per-target last-used hint, which can name a
  // stale account after a tenant-wide switch (e.g. an SP attach to a target Alice once used must read "account unknown",
  // not "Alice"). Null reads honestly as unknown.
  const currentAccount = session?.currentAccount;
  // Trust the SessionInfo (account, tenant) as ONE unit — the identity the live model is actually signed in as. Never
  // recombine it with a tenant hint from a DIFFERENT connection (editing/querying rows can name another target's
  // tenant): that would show "Bob beside Contoso" when Bob's real tenant is unknown. Null reads as "Tenant unknown".
  const currentTenant = session?.currentTenant;
  // "Switch account" acts on the live model currently in play (editing side, else querying). Only meaningful for a
  // published (xmla) target signed in via a switchable family — a local running model uses integrated Windows auth, and
  // azcli / service-principal have no account picker, so offering "Switch account" on them would be a false promise.
  const liveConnId = (editing?.live && editing?.kind !== 'localDesktop' ? editing?.connectionId : null) || querying?.connectionId;
  const switchable = records.find((r) => r.kind === 'xmla' && r.id === liveConnId && credentialFamily(r.authMode) != null);

  return (
    <div className="fixed inset-0 z-[80] flex justify-end" role="dialog" aria-modal="true" aria-label="Connections">
      <button className="absolute inset-0" style={{ background: 'color-mix(in srgb, var(--sem-bg) 68%, transparent)' }} onClick={onClose} aria-label="Close connections" />
      <aside ref={panelRef} tabIndex={-1} className="relative h-full w-[500px] max-w-[92vw] flex flex-col border-l shadow-2xl outline-none" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' }}>
        <div className="flex items-start gap-3 px-4 py-3 border-b" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="min-w-0">
            <h2 className="text-[15px] font-semibold">Connections</h2>
            <p className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>Keep editing, testing, and publishing separate. Nothing is published by changing a connection.</p>
          </div>
          <button onClick={onClose} className="ml-auto text-[18px] leading-none" style={{ color: 'var(--sem-muted)' }} aria-label="Close">×</button>
        </div>

        <div className="overflow-auto flex-1 min-h-0">
          <div className="mx-4 mt-4 flex items-center gap-3 rounded-lg border px-3 py-2.5 text-[12px]" style={{ borderColor: 'color-mix(in srgb, var(--sem-accent) 30%, var(--sem-border))', background: 'var(--sem-accent-soft)' }}>
            <span className="flex h-6 w-6 flex-none items-center justify-center rounded-full text-[10px] font-bold" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }} aria-hidden>{initials(currentAccount)}</span>
            <div className="min-w-0">
              <div className="font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{currentAccount || 'Account unknown'}</div>
              <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }}>{currentTenant ? currentTenant : (currentAccount ? 'Tenant unknown' : 'No live model is signed in')}</div>
            </div>
            {switchable && <button className={`${BTN} ml-auto flex-none`} disabled={busyId != null} onClick={() => setSwitchTarget(switchable)} style={inputStyle}>Switch account</button>}
          </div>

          {switchTarget && (
            <div className="mx-4 mt-3 rounded-lg border p-3" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
              <div className="text-[12px] font-semibold">Open {nameOf(switchTarget)} as a different account?</div>
              <div className="mt-1 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>The saved endpoint does not change, whichever account you pick. {blastRadius(switchTarget)} Every switch is recorded in the connection history.</div>
              <div className="mt-3 flex justify-end gap-2">
                <button className={BTN} onClick={() => setSwitchTarget(null)} style={inputStyle}>Cancel</button>
                <button className={BTN} disabled={busyId != null} onClick={() => void confirmSwitch()} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Choose account and open</button>
              </div>
            </div>
          )}

          <section className="m-4 rounded-lg border overflow-hidden" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
            <Role label="Editing" value={roleValue(editing, 'No model open')}
              detail={editing?.sourceControlled ? `Local files in source control · ${editing.repositoryRoot}` : (editing?.source || 'Open a local project or live model')} missing={!editing?.available} />
            <Role label="Tests and queries" value={roleValue(querying, 'Not connected')}
              detail={querying?.available ? (querying.kind === 'localDesktop' || querying.kind === 'local' ? 'Local running model' : 'Published model') : 'Choose which live model answers tests and queries'} missing={!querying?.available} />
            <Role label="Publish to" value={roleValue(publishing, 'Not linked')}
              detail={publishing?.available ? (publishing.endpoint || 'XMLA destination') : 'Choose an XMLA destination when you are ready to publish'} missing={!publishing?.available} />
            <Role label="Reference model" value={roleValue(reference, 'Not set')}
              detail={reference?.available ? 'Copy objects from it' : 'Browse a second model to copy objects from'} missing={!reference?.available} />
          </section>
          {context?.summary && <p className="mx-4 mb-4 text-[11px] leading-5" style={{ color: 'var(--sem-muted)' }}>{context.summary}</p>}

          <section className="mx-4 mb-4 rounded-lg border p-3 text-[10px] leading-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
            <div><b style={{ color: 'var(--sem-fg)' }}>Use for tests and queries</b> changes only where results come from. Your editable model stays open.</div>
            <div className="mt-1"><b style={{ color: 'var(--sem-fg)' }}>Open live</b> uses this one live model for both editing and queries. It does not create local files.</div>
            <div className="mt-1"><b style={{ color: 'var(--sem-fg)' }}>Work locally</b> creates or opens safe local files, then keeps testing and final publishing as separate choices.</div>
          </section>

          {work && (
            <section className="mx-4 mb-4 rounded-lg border p-3" style={{ borderColor: 'var(--sem-accent)', background: 'var(--sem-surface)' }}>
              <div className="flex items-start gap-2">
                <div><div className="text-[12px] font-semibold">Work locally from {nameOf(work.source)}</div>
                  <div className="text-[10px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>Local files are the editable source. A running model is still required to execute tests and queries.</div></div>
                <button className="ml-auto" onClick={() => setWork(null)} aria-label="Close local setup">×</button>
              </div>
              <div className="grid grid-cols-2 gap-2 mt-3">
                <label className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Tests and queries
                  <select value={work.queryId} onChange={(e) => updateWork(e.target.value, work.publishId)} className="mt-1 w-full rounded border px-2 py-1.5 text-[11px]" style={inputStyle}>
                    {records.map((r) => <option key={r.id} value={r.id}>{nameOf(r)} · {r.kind === 'localDesktop' ? 'local running' : 'published'}</option>)}
                  </select>
                </label>
                <label className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Final publish destination
                  <select value={work.publishId} onChange={(e) => updateWork(work.queryId, e.target.value)} className="mt-1 w-full rounded border px-2 py-1.5 text-[11px]" style={inputStyle}>
                    <option value="">Choose later</option>
                    {xmlaRecords.map((r) => <option key={r.id} value={r.id}>{nameOf(r)}</option>)}
                  </select>
                </label>
              </div>
              {work.plan ? <>
                <div className="mt-3 rounded-md p-2 text-[11px] leading-5" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>{work.plan.summary}</div>
                <div className="mt-2 text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={work.plan.targetFolder}><b style={{ color: 'var(--sem-fg)' }}>Local folder:</b> {work.plan.targetFolder}</div>
                <ul className="mt-2 space-y-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>{work.plan.benefits?.map((b) => <li key={b}>✓ {b}</li>)}</ul>
                {work.plan.conflicts?.map((c) => <div key={c} className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{c}</div>)}
                {work.plan.error && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{work.plan.error}</div>}
                {work.plan.opened && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-good)' }}>Local working copy opened. {work.plan.queryConnected ? 'The selected test model is connected.' : 'Connect a test model when ready.'}</div>}
                {!work.plan.opened && <div className="flex justify-end gap-2 mt-3"><button className={BTN} onClick={() => setWork(null)} style={inputStyle}>Cancel</button>
                  <button className={BTN} disabled={!work.plan.canCommit || busyId != null} onClick={confirmWork} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busyId ? 'Preparing…' : work.plan.action === 'open' ? 'Open local copy' : 'Create local copy'}</button></div>}
              </> : <div className="mt-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Updating preview…</div>}
            </section>
          )}

          <div className="flex items-center px-4 mb-2"><h3 className="text-[11px] uppercase tracking-[0.07em] font-semibold" style={{ color: 'var(--sem-muted)' }}>Known live models</h3>
            <button className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }} onClick={() => setAddOpen((v) => !v)}>+ Add a published model</button></div>

          {addOpen && <div className="mx-4 mb-3 rounded-lg border p-3 grid gap-2" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
            <input value={endpoint} onChange={(e) => setEndpoint(e.target.value)} placeholder="XMLA endpoint" className="rounded border px-2 py-1.5 text-[11px]" style={inputStyle} />
            <div className="grid grid-cols-2 gap-2"><input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder="Model name (optional)" className="rounded border px-2 py-1.5 text-[11px]" style={inputStyle} />
              <select value={authMode} onChange={(e) => setAuthMode(e.target.value)} className="rounded border px-2 py-1.5 text-[11px]" style={inputStyle}><option value="interactive">Browser sign-in</option><option value="azcli">Command-line sign-in</option><option value="serviceprincipal">Service identity</option></select></div>
            <button className={BTN} disabled={!endpoint.trim() || busyId != null} onClick={addAndConnect} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Connect and remember</button>
          </div>}

          <div className="px-4 pb-4 space-y-2">
            {loading && <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Loading connections…</div>}
            {!loading && records.map((r) => {
              const activeQuery = context?.querying?.connectionId === r.id;
              const activePublish = context?.publishing?.connectionId === r.id;
              const activeReference = context?.reference?.connectionId === r.id;
              const account = accountOf(r);   // who the NEXT open signs in as — only a live sign-in record; undefined = unknown
              const was = probeOf(r)?.previousAccount;   // provenance only: the last-used account
              const family = credentialFamily(r.authMode);   // null = azcli / service principal — no switchable account
              const identity = account
                ? (was && was !== account ? `as ${account} (was ${was})` : `as ${account}`)
                : (was ? `last opened as ${was}` : 'account unknown');
              return <div key={r.id} className="rounded-lg border p-3" style={{ borderColor: activeQuery || activePublish ? 'var(--sem-accent)' : 'var(--sem-border)', background: 'var(--sem-surface)' }}>
                <div className="flex items-start gap-2"><div className="min-w-0"><div className="text-[12px] font-semibold truncate">{nameOf(r)}</div>
                  <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={r.endpoint}>{r.kind === 'localDesktop' ? 'Local running model' : 'Published XMLA model'} · {r.database || short(r.endpoint)}{r.kind === 'xmla' ? ` · ${identity}` : ''}</div></div>
                  <div className="ml-auto flex flex-wrap justify-end gap-1">
                    {r.kind === 'xmla' && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{r.label || 'Production safeguards'}</span>}
                    {activeQuery && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }}>testing</span>}{activePublish && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>publish</span>}{activeReference && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>reference</span>}</div></div>
                {r.workingFolder && <div className="mt-1 text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={r.workingFolder}>Local copy: {r.workingFolder}</div>}
                <div className="flex flex-wrap gap-1.5 mt-2"><button className={BTN} disabled={connectionBusy || busyId != null || activeQuery} onClick={() => void queryWith(r)} style={inputStyle}>{activeQuery ? 'Used for tests' : 'Use for tests and queries'}</button>
                  <button className={BTN} disabled={busyId != null} onClick={() => void openLive(r)} style={inputStyle}>{r.kind === 'xmla' && !account && family ? 'Sign in and open' : 'Open live'}</button>
                  {r.kind === 'xmla' && family && account && <button className={BTN} disabled={busyId != null} onClick={() => setSwitchTarget(r)} style={inputStyle}>Open as…</button>}
                  {r.kind === 'xmla' && <button className={BTN} disabled={busyId != null || activeReference} onClick={() => useAsReference(r)} style={activeReference ? { background: 'var(--sem-accent-soft)', borderColor: 'var(--sem-accent)', color: 'var(--sem-accent)' } : inputStyle}>{activeReference ? 'Reference' : 'Use as reference'}</button>}
                  {r.kind === 'xmla' && <button className={BTN} disabled={busyId != null || activePublish} onClick={() => void setPublish(r)} style={inputStyle}>{activePublish ? 'Publish destination' : 'Set as publish destination'}</button>}
                  <button className={BTN} disabled={busyId != null} onClick={() => void beginWork(r)} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{r.workingFolder ? 'Open local copy' : 'Work locally'}</button></div>
              </div>;
            })}
            {!loading && records.length === 0 && <div className="rounded-lg border p-3 text-[11px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>No live models are remembered yet. Add an XMLA connection or open a running local model.</div>}
          </div>

          <div className="px-4 pb-6">
            <div className="flex items-center">
              <h3 className="text-[11px] uppercase tracking-[0.07em] font-semibold" style={{ color: 'var(--sem-muted)' }}>History</h3>
              <button className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }} onClick={() => setHistoryOpen((v) => !v)}>{historyOpen ? 'Hide' : 'Show'}</button>
            </div>
            {historyOpen && <div className="mt-2 rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
              <div className="flex items-center gap-2 px-3 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
                <label className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Filter</label>
                <select value={historyFilter} onChange={(e) => setHistoryFilter(e.target.value)} className="flex-1 rounded border px-2 py-1 text-[11px]" style={inputStyle}>
                  <option value="">All connections</option>
                  {records.map((r) => <option key={r.id} value={r.id}>{nameOf(r)}</option>)}
                </select>
              </div>
              <div className="max-h-64 overflow-auto">
                {history.length === 0 && <div className="px-3 py-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No connection history yet.</div>}
                {history.map((e, i) => (
                  <div key={i} className="flex items-start gap-2 px-3 py-2 border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
                    <span className="mt-0.5 text-[11px]" aria-hidden style={{ color: e.ok ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{e.ok ? '●' : '○'}</span>
                    <div className="min-w-0 flex-1">
                      <div className="text-[11px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{historyKindLabel(e)}{e.account ? ` · ${e.account}` : ''}</div>
                      <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={e.endpoint}>{[e.database || short(e.endpoint), shortWhen(e.whenUtc)].filter(Boolean).join(' · ')}</div>
                    </div>
                  </div>
                ))}
              </div>
            </div>}
          </div>
        </div>
        <div className="flex items-center gap-2 p-3 border-t" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <button className={BTN} onClick={openLocalModel} style={inputStyle}>Open local file or project</button>
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Existing files remain user-owned, including files already in source control.</span>
        </div>
        {error && <div className="absolute bottom-14 left-3 right-3 rounded-md border px-3 py-2 text-[11px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-bad)', color: 'var(--sem-bad)' }}>{error}</div>}
      </aside>
    </div>
  );
}
