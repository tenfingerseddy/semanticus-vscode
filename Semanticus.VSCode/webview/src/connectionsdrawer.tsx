import { useEffect, useState, useRef } from 'react';
import { openLocalModel, pickWorkingCopyFolder, rpc } from './bridge';
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
  const { context, busy: connectionBusy, connectLocal, connectXmla, refresh } = useConnection();
  const [records, setRecords] = useState<ConnectionRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [work, setWork] = useState<WorkSetup | null>(null);
  const [addOpen, setAddOpen] = useState(false);
  const [endpoint, setEndpoint] = useState('');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState('interactive');

  const load = async () => {
    setLoading(true);
    try { setRecords((await rpc<ConnectionRecord[]>('listConnections')) ?? []); setError(null); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setLoading(false); }
  };
  useEffect(() => { if (open) { void load(); void refresh(); } }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
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
      : await connectXmla(r.endpoint, r.database || '', r.authMode || 'interactive');
    if (!ok) setError('That model could not be connected for tests and queries.');
    await load(); setBusyId(null);
  };

  const openLive = async (r: ConnectionRecord, forceReauth = false) => {
    setBusyId('open:' + r.id); setError(null);
    try {
      if (r.kind === 'localDesktop') await rpc('openLocal', r.endpoint, r.database || null);
      else await rpc('openLive', r.endpoint, r.database || null, r.authMode || 'interactive', null, r.tenantId || null, forceReauth);
      await refresh(); await load();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusyId(null); }
  };

  const setPublish = async (r: ConnectionRecord) => {
    setBusyId('publish:' + r.id); setError(null);
    try {
      await rpc('setPublishDestination', r.id, 'human');
      await refresh(); await load();
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
  const xmlaRecords = records.filter((r) => r.kind === 'xmla');

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
          <section className="m-4 rounded-lg border overflow-hidden" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
            <Role label="Editing" value={roleValue(editing, 'No model open')}
              detail={editing?.sourceControlled ? `Local files in source control · ${editing.repositoryRoot}` : (editing?.source || 'Open a local project or live model')} missing={!editing?.available} />
            <Role label="Tests and queries" value={roleValue(querying, 'Not connected')}
              detail={querying?.available ? (querying.kind === 'localDesktop' || querying.kind === 'local' ? 'Local running model' : 'Published model') : 'Choose which live model answers tests and queries'} missing={!querying?.available} />
            <Role label="Publish to" value={roleValue(publishing, 'Not linked')}
              detail={publishing?.available ? (publishing.endpoint || 'XMLA destination') : 'Choose an XMLA destination when you are ready to publish'} missing={!publishing?.available} />
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
            <button className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }} onClick={() => setAddOpen((v) => !v)}>+ Add XMLA connection</button></div>

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
              return <div key={r.id} className="rounded-lg border p-3" style={{ borderColor: activeQuery || activePublish ? 'var(--sem-accent)' : 'var(--sem-border)', background: 'var(--sem-surface)' }}>
                <div className="flex items-start gap-2"><div className="min-w-0"><div className="text-[12px] font-semibold truncate">{nameOf(r)}</div>
                  <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={r.endpoint}>{r.kind === 'localDesktop' ? 'Local running model' : 'Published XMLA model'} · {r.database || short(r.endpoint)}</div></div>
                  <div className="ml-auto flex gap-1">{activeQuery && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }}>testing</span>}{activePublish && <span className="rounded px-1.5 py-0.5 text-[9px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>publish</span>}</div></div>
                {r.workingFolder && <div className="mt-1 text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={r.workingFolder}>Local copy: {r.workingFolder}</div>}
                <div className="flex flex-wrap gap-1.5 mt-2"><button className={BTN} disabled={connectionBusy || busyId != null || activeQuery} onClick={() => void queryWith(r)} style={inputStyle}>{activeQuery ? 'Used for tests' : 'Use for tests and queries'}</button>
                  <button className={BTN} disabled={busyId != null} onClick={() => void openLive(r)} style={inputStyle}>Open live</button>
                  {r.kind === 'xmla' && r.authMode === 'interactive' && <button className={BTN} disabled={busyId != null} onClick={() => void openLive(r, true)} style={inputStyle}>Open with another account</button>}
                  {r.kind === 'xmla' && <button className={BTN} disabled={busyId != null || activePublish} onClick={() => void setPublish(r)} style={inputStyle}>{activePublish ? 'Publish destination' : 'Set as publish destination'}</button>}
                  <button className={BTN} disabled={busyId != null} onClick={() => void beginWork(r)} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{r.workingFolder ? 'Open local copy' : 'Work locally'}</button></div>
              </div>;
            })}
            {!loading && records.length === 0 && <div className="rounded-lg border p-3 text-[11px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>No live models are remembered yet. Add an XMLA connection or open a running local model.</div>}
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
