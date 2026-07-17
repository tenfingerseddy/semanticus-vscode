import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { rpc, onReconnect, onDidChange, onConnectionChange } from './bridge';
import type { ConnectionStatus } from './wire';

// Live-connection state, shared across every Studio query tab (Statistics / DAX Query / DAX Lab / Pivot / Data).
// Those tabs run DAX/DMV against a real Analysis Services engine. The UNIFIED open (Open Model… → Power BI Desktop
// or XMLA) makes the SAME model editable in the tree AND live here — so when the tree model is live, Studio just
// works with no separate connect. When the open model is a plain file (metadata only), this bar lets you ATTACH a
// running engine for queries (which doesn't change the tree model). Lifting the state here means one source of
// truth, and a refresh on 'reconnected' keeps Studio pointed at the SAME model the tree shows.

export interface LocalInstance { port: number; title: string; dataSource: string; }
export interface ConnectionContextModel {
  available: boolean;
  modelName?: string;
  source?: string;
  connectionId?: string;
  sourceConnectionId?: string;
  kind?: string;
  endpoint?: string;
  database?: string;
  authMode?: string;
  account?: string;            // the account (UPN) last signed in to this side, when known — the identity bar's "as <account>"
  tenantId?: string;
  label?: string;
  effectiveLabel?: string;
  unlabelled?: boolean;
  workingFolder?: string;
  live: boolean;
  sourceControlled?: boolean;
  repositoryRoot?: string;
}
export interface ModelConnectionContext {
  editing: ConnectionContextModel;
  querying: ConnectionContextModel;
  publishing: ConnectionContextModel;
  reference: ConnectionContextModel;   // the "copy FROM" model the Reference tree is browsing, when one is bound
  relationship: string;
  twoModelsInPlay: boolean;
  publishDestinationSeparateFromQuerying: boolean;
  summary: string;
}
// The ONE session-identity shape, mirroring Semanticus.Engine/Protocol.cs SessionInfo (camelCased). This used to be
// declared twice (a partial copy in App.tsx dropped half the fields the engine already sends); both sites now import
// this so the two can't drift. "Editing" vs "querying" is the whole point of the context bar: `source`/`liveBound`/
// `liveEndpoint`/`liveDatabase` describe the model you're EDITING; `liveConnected`/`liveKind`/`liveDataSource`
// describe the engine your queries actually RUN against; `hasUnsavedChanges` is why the two can silently disagree.
export interface SessionInfo {
  sessionId?: string;
  revision?: number;
  modelName?: string;
  source?: string;             // the edited model's origin: a local folder/file path, or an XMLA endpoint when live-bound
  hasUnsavedChanges?: boolean;
  tables?: number;
  measures?: number;
  liveBound?: boolean;         // opened FROM a live model (deploy can push edits back to it)
  liveEndpoint?: string;       // the bound XMLA endpoint (null unless live-bound)
  liveDatabase?: string;       // the bound dataset/database (null unless live-bound)
  liveConnected?: boolean;     // a live QUERY connection is attached (drives DAX/DMV/preview)
  liveKind?: string;           // "local" | "xmla" — the attached query engine's kind
  liveDataSource?: string;     // the attached query engine's data source
  currentAccount?: string;     // the account (UPN) the live-bound/queried model is signed in as — for the identity bar; null = unknown
  currentTenant?: string;      // the tenant that account/connection belongs to, when known
}

interface ConnCtx {
  conn: ConnectionStatus | null;     // the attached live QUERY engine (drives the tabs)
  session: SessionInfo | null;       // the OPEN model identity (what the tree shows)
  context: ModelConnectionContext | null; // engine-owned edit/query/publish identities
  busy: boolean;
  err: string | null;
  instances: LocalInstance[];
  connectLocal: (dataSource?: string | null) => Promise<boolean>;   // attach a local instance to the current session
  connectXmla: (endpoint: string, database: string, authMode: string, tenantId?: string | null) => Promise<boolean>;
  disconnect: () => Promise<void>;
  refresh: () => Promise<void>;
  connectionsOpen: boolean;
  openConnections: () => void;
  closeConnections: () => void;
}

const Ctx = createContext<ConnCtx | null>(null);

export function useConnection(): ConnCtx {
  const c = useContext(Ctx);
  if (!c) throw new Error('useConnection must be used within <ConnectionProvider>');
  return c;
}

export function ConnectionProvider({ children }: { children: ReactNode }) {
  const [conn, setConn] = useState<ConnectionStatus | null>(null);
  const [session, setSession] = useState<SessionInfo | null>(null);
  const [context, setContext] = useState<ModelConnectionContext | null>(null);
  const [instances, setInstances] = useState<LocalInstance[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [connectionsOpen, setConnectionsOpen] = useState(false);

  // The model identity + its live state is read from sessionInfo (set by a unified open) AND connectionStatus
  // (set by an explicit attach). They agree once a model is live; we read both so either path lights up the tabs.
  async function refresh(): Promise<void> {
    try {
      const [c, s, x] = await Promise.all([
        rpc<ConnectionStatus>('connectionStatus').catch(() => null),
        rpc<SessionInfo>('sessionInfo').catch(() => null),
        rpc<ModelConnectionContext>('connectionContext').catch(() => null),
      ]);
      setConn(c);
      setSession(s);
      setContext(x);
    } catch { /* leave prior state; the connect UI stays available */ }
  }

  async function connectLocal(dataSource: string | null = null): Promise<boolean> {
    setBusy(true); setErr(null);
    try {
      const c = await rpc<ConnectionStatus>('connectLocal', dataSource, null);
      setConn(c);
      if (!c.connected && c.message) setErr(c.message);
      else await refresh();
      return !!c.connected;
    } catch (e) { setErr(String((e as Error).message ?? e)); return false; }
    finally { setBusy(false); }
  }
  async function connectXmla(endpoint: string, database: string, authMode: string, tenantId: string | null = null): Promise<boolean> {
    setBusy(true); setErr(null);
    try {
      // Thread the tenant like the primary open — a remembered cross-tenant model must query its OWN tenant, not the
      // default one the RPC's old four-arg form left null (which let az login's home tenant silently decide).
      const c = await rpc<ConnectionStatus>('connectXmla', endpoint, database || null, authMode, null, tenantId || null);
      setConn(c);
      if (!c.connected && c.message) setErr(c.message);
      else await refresh();
      return !!c.connected;
    } catch (e) { setErr(String((e as Error).message ?? e)); return false; }
    finally { setBusy(false); }
  }
  async function disconnect() {
    setBusy(true);
    try { setConn(await rpc<ConnectionStatus>('disconnect')); await refresh(); } catch { /* ignore */ } finally { setBusy(false); }
  }

  // On mount: read the open model + its live state, and discover local instances (for the attach picker). We do
  // NOT silently auto-connect to a single running instance anymore — that could bind a DIFFERENT model than the
  // one open in the tree. The unified open (Open Model…) is the way to get an editable + live model together.
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      await refresh();
      const list = (await rpc<LocalInstance[]>('listLocalInstances').catch(() => [])) ?? [];
      if (!cancelled) setInstances(list);
    };
    void load();
    // The host fires 'reconnected' when the engine reconnects or a new model is opened (incl. the VS Code source
    // picker) — re-read so Studio always reflects the SAME model the tree now shows.
    const off = onReconnect(() => { void load(); });
    // Any edit (either door) flips hasUnsavedChanges — re-read sessionInfo so the query tabs' staleness chips light up
    // the moment a staged edit diverges from the queried model. Debounced so a burst of edits costs one round-trip.
    let t: ReturnType<typeof setTimeout> | undefined;
    const offChange = onDidChange(() => { clearTimeout(t); t = setTimeout(() => { void refresh(); }, 300); });
    // An MCP-door connect/disconnect (or a reference set/clear) changes the live connection WITHOUT a model edit or a
    // reconnect, so neither handler above fires — re-read the connection context so the ConnectBar never goes stale.
    const offConn = onConnectionChange(() => { void refresh(); });
    return () => { cancelled = true; off(); offChange(); offConn(); clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Ctx.Provider value={{ conn, session, context, busy, err, instances, connectLocal, connectXmla, disconnect, refresh,
      connectionsOpen, openConnections: () => setConnectionsOpen(true), closeConnections: () => setConnectionsOpen(false) }}>
      {children}
    </Ctx.Provider>
  );
}

/** Compact query-runtime status. Every tab opens the shared Connections drawer, so attach/open/publish choices never
 * fork into a second set of endpoint fields with different memory or wording. */
export function ConnectBar({ hint }: { hint?: string }) {
  const { conn, session, context, busy, err, disconnect, openConnections } = useConnection();
  const testing = context?.querying;

  if (conn?.connected) {
    const label = testing?.modelName || testing?.database || conn.dataSource;
    const kind = testing?.kind === 'localDesktop' || conn.kind === 'local' ? 'local running model' : 'published model';
    return (
      <div className="flex items-center gap-2 text-[12px] flex-wrap" style={{ color: 'var(--sem-muted)' }}>
        <span style={{ color: 'var(--sem-good)' }}>●</span>
        <span>Tests and queries use <b style={{ color: 'var(--sem-fg)' }}>{label}</b> ({kind}).</span>
        <button onClick={openConnections} className="underline">Change</button>
        <button onClick={disconnect} disabled={busy} className="underline disabled:opacity-40">Disconnect</button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>
        {hint ?? 'This tab runs live queries.'} {session?.modelName ? ` ${session.modelName} is open for editing, but no running model is selected for tests and queries.` : ' No model is open.'}
        {' '}Local files remain editable without a connection, but they cannot execute queries by themselves.
      </div>
      <div><button onClick={openConnections} disabled={busy}
        className="text-[12px] px-3 py-1.5 rounded-lg font-medium disabled:opacity-40"
        style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Choose test model</button></div>
      {err && <div className="text-[12px]" style={{ color: 'var(--sem-bad)' }}>{err}</div>}
    </div>
  );
}
