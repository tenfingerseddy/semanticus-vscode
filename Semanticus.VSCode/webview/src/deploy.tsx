import { useEffect, useMemo, useRef, useState } from 'react';
import { usePersistedState } from './hooks';
import { rpc } from './bridge';
import { setPublishReach } from './publishReach';
import { type CompareSeed } from './compare';
import { PublishChanges, PageAction } from './publish-changes';
import { useConnection } from './connection';
import { openConnectionsOnView } from './connectionshub';
import { compareSeedFromSession } from './contextbar';
import { AccountPicker } from './accountpicker';
import { defaultSignInMode, isInteractiveSignIn, signInProblem, uiLabel } from './copy';
import { promoteLabel, publishStageName, restoreDetail, restoreLabel, rollbackResultLine, shortWhen } from './publishcopy';

// Wire shapes mirror Semanticus.Engine/Alm/AlmProtocol.cs (camelCased).
interface ConnectionRecord { id: string; kind: string; endpoint: string; database: string; modelName: string; label?: string | null; }
interface RestorePointRecord { id: string; endpoint: string; database: string; capturedUtc: string; bimPath?: string | null; bytes: number; op?: string; reason?: string; pushed: string[]; deleted: string[]; }
interface RollbackResult { applied: boolean; restored: number; removed: number; restoredRefs: string[]; removedRefs: string[]; failedRefs: string[]; restorePointId?: string; target?: string; note?: string; error?: string; }
interface DeploymentPipeline { id: string; displayName: string; description?: string; }
interface PipelineStage { id: string; order: number; displayName: string; description?: string; workspaceId?: string; workspaceName?: string; isPublic: boolean; }
interface DeployItemDiff { itemId: string; itemDisplayName: string; itemType: string; state: string; }
interface DeployStageReport { committed: boolean; sourceStageName?: string; targetStageName?: string; targetIsProd: boolean; itemCount: number; items: DeployItemDiff[]; newCount: number; updateCount: number; confirmToken?: string; status?: string; plan?: string; error?: string; }
interface DeploymentHistoryEntry { id: string; status: string; note?: { content?: string }; preDeploymentDiffInformation?: { newItemsCount: number; differentItemsCount: number; noDifferenceItemsCount: number }; performedBy?: { type?: string; displayName?: string }; executionEndTime?: string; }

type DeployMode = 'changes' | 'versions' | 'promote';
const MODE_LABEL: Record<DeployMode, string> = { changes: 'Changes', versions: 'Earlier versions', promote: 'Promote' };

// The Publish page: press one button to publish what is ticked, restore an earlier version, or promote between
// stages. Each tab has one button and it sits at the top right, on the line that says where it goes. Every check
// runs by itself; nothing sits between the press and the write except the button's own label.
export function DeployView({ seed, initialMode = 'changes', restoreTarget, onRestoreConsumed }: {
  seed?: CompareSeed | null; initialMode?: DeployMode;
  restoreTarget?: { id: string; endpoint: string; database: string; nonce: number } | null;
  onRestoreConsumed?: () => void;
}) {
  const { session, conn, context, openConnections } = useConnection();
  const [mode, setMode] = useState<DeployMode>(initialMode);
  // A restore deep-link is one-shot: adopt its target into local state, then App clears the prop so leaving
  // and re-opening Publish does not re-force Earlier versions on every remount.
  const [adopted, setAdopted] = useState<{ id: string; endpoint: string; database: string } | null>(null);
  const [connections, setConnections] = useState<ConnectionRecord[]>([]);
  const [slot, setSlot] = useState<HTMLElement | null>(null);   // where the tab's one button goes
  const [restorePoints, setRestorePoints] = useState<RestorePointRecord[]>([]);
  const [restoreId, setRestoreId] = useState('');
  const [restoreLoading, setRestoreLoading] = useState(false);
  const [restoreErr, setRestoreErr] = useState<string | null>(null);

  async function loadConnections() {
    try { setConnections((await rpc<ConnectionRecord[]>('listConnections')) ?? []); }
    catch { /* naming the destination is supporting context; the page still works without the registry */ }
  }
  useEffect(() => { void loadConnections(); }, []);

  const defaultSeed = useMemo<CompareSeed | null>(() => {
    if (!session?.sessionId) return null;
    const built = compareSeedFromSession(session, conn, context);
    let nonce = 17;
    const identity = `${session.sessionId}|${context?.publishing?.connectionId || ''}|${context?.publishing?.database || ''}`;
    for (const ch of identity) nonce = ((nonce * 31) + ch.charCodeAt(0)) | 0;
    return { ...built, nonce };
  }, [session?.sessionId, conn?.connected, conn?.kind, conn?.dataSource,
    context?.publishing?.connectionId, context?.publishing?.endpoint, context?.publishing?.database]);
  const reviewSeed = seed ?? defaultSeed;
  const endpoint = context?.publishing?.endpoint || session?.liveEndpoint || (conn?.kind === 'xmla' ? conn.dataSource : '');
  const database = context?.publishing?.database || session?.liveDatabase || '';
  const norm = (v?: string) => (v || '').trim().toLowerCase().replace(/[\\/]+$/, '');
  const visibleEndpoint = mode === 'versions' && adopted ? adopted.endpoint : endpoint;
  const visibleDatabase = mode === 'versions' && adopted ? adopted.database : database;
  const target = connections.find((c) => c.id === context?.publishing?.connectionId) ?? connections.find((c) => norm(c.endpoint) === norm(visibleEndpoint)
    && (!visibleDatabase || !c.database || norm(c.database) === norm(visibleDatabase)));
  const targetName = target?.modelName || target?.database || visibleDatabase || (visibleEndpoint ? visibleEndpoint.slice(visibleEndpoint.lastIndexOf('/') + 1) : 'the destination');
  // The stage shows only when somebody set one. No label means no badge, not a guess.
  const stage = target?.label ? publishStageName(target.label) : null;
  const liveBound = !!session?.liveBound;
  // A local working copy is not live-bound, but it can still publish when a destination resolves from the
  // connection registry (PublishConnectionId). Gate on the resolved destination, not on liveBound alone.
  const canPublish = liveBound || !!(context?.publishing?.available);
  const hasDestination = canPublish && reviewSeed?.right?.kind === 'workspace';
  const account = context?.publishing?.account || session?.currentAccount;
  const chooseDestination = () => { openConnectionsOnView('setup'); openConnections(); };
  // A new model, or a new destination, means we have not tried to read it yet: never carry a stale refusal over.
  useEffect(() => { setPublishReach(null); }, [session?.sessionId, context?.publishing?.connectionId]);

  // ── Earlier versions ───────────────────────────────────────────────────────────────────────
  async function loadRestorePoints() {
    setRestoreLoading(true); setRestoreErr(null);
    try {
      const scopeEndpoint = adopted?.endpoint || endpoint;
      const scopeDatabase = adopted?.database || database;
      const points = (await rpc<RestorePointRecord[]>('listRestorePoints', scopeEndpoint || null, scopeDatabase || null)) ?? [];
      setRestorePoints(points);
      setRestoreId((id) => points.some((p) => p.id === id) ? id : (points[0]?.id ?? ''));
    } catch (e) { setRestoreErr(String((e as Error).message ?? e)); }
    finally { setRestoreLoading(false); }
  }
  useEffect(() => {
    setRestoreErr(null);
    if (!restoreTarget && !adopted) void loadRestorePoints();
    // The selected publish identity is the restore-point scope. A target change must never leave the old target's
    // choices on screen.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [endpoint, database]);
  useEffect(() => {
    if (!restoreTarget) return;
    const t = restoreTarget;
    setAdopted({ id: t.id, endpoint: t.endpoint, database: t.database });
    setMode('versions'); setRestoreLoading(true); setRestoreErr(null);
    rpc<RestorePointRecord[]>('listRestorePoints', t.endpoint, t.database)
      .then((points) => {
        const found = points ?? [];
        setRestorePoints(found);
        setRestoreId(found.some((p) => p.id === t.id) ? t.id : (found[0]?.id ?? ''));
      })
      .catch((e) => setRestoreErr(String((e as Error).message ?? e)))
      .finally(() => setRestoreLoading(false));
    onRestoreConsumed?.();   // one-shot: App clears the prop so a later remount does not re-force Earlier versions
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [restoreTarget?.nonce]);

  return (
    <div className="h-full overflow-auto" style={{ color: 'var(--sem-fg)' }}>
      <div className="sem-page">
        <header className="sem-stack" data-testid="deploy-head">
          <div className="sem-inline flex-wrap">
            <h2 className="sem-page-title">Publish</h2>
            <div className="sem-seg sem-push" role="group" aria-label="Publish views">
              {(Object.keys(MODE_LABEL) as DeployMode[]).map((m) => (
                <button key={m} type="button" className="sem-seg-item" aria-pressed={mode === m} onClick={() => setMode(m)}>{MODE_LABEL[m]}</button>
              ))}
            </div>
          </div>
          <div className="sem-inline flex-wrap" data-testid="deploy-header">
            {/* Promote moves a model between release stages, so the publish destination is not what it is about. */}
            {mode === 'promote' ? null : hasDestination ? (
              <>
                <span>To <b>{targetName}</b></span>
                {stage && <span className="sem-chip sem-chip-outline">{stage}</span>}
                {account && <span className="sem-text-meta">as {account}</span>}
                <button type="button" onClick={chooseDestination} className="sem-btn sem-btn-quiet sem-btn-sm">Change</button>
              </>
            ) : <span className="sem-text-meta">No publish destination yet.</span>}
            <div ref={setSlot} className="sem-inline sem-push" />
          </div>
        </header>
        {mode === 'changes' && <PublishChanges key={session?.sessionId ?? 'none'} seed={hasDestination ? reviewSeed : { left: reviewSeed?.left ?? { kind: 'session' }, right: null, note: reviewSeed?.note, nonce: reviewSeed?.nonce ?? 0 }}
          destination={targetName} slot={slot} onWrote={() => void loadRestorePoints()} onChooseDestination={chooseDestination}
          onReach={(ok) => setPublishReach(ok ? null : { sessionId: session?.sessionId ?? '', connectionId: context?.publishing?.connectionId ?? null })} />}
        {mode === 'versions' && <VersionsPanel points={restorePoints} selectedId={restoreId} onSelect={setRestoreId} target={targetName}
          loading={restoreLoading} error={restoreErr} slot={slot} onRestored={loadRestorePoints} />}
        {mode === 'promote' && <PromotePanel slot={slot} />}
      </div>
    </div>
  );
}

// ── Earlier versions ─────────────────────────────────────────────────────────────────────────
// Picking a version runs the preview by itself. The one button restores it.
function VersionsPanel({ points, selectedId, onSelect, target, loading, error, slot, onRestored }: {
  points: RestorePointRecord[]; selectedId: string; onSelect: (id: string) => void; target: string;
  loading: boolean; error: string | null; slot: HTMLElement | null; onRestored: () => Promise<void>;
}) {
  const point = points.find((p) => p.id === selectedId);
  const [preview, setPreview] = useState<RollbackResult | null>(null);
  const [previewErr, setPreviewErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<'preview' | 'restore' | null>(null);
  const [note, setNote] = useState<{ tone: 'good' | 'bad'; text: string } | null>(null);
  const [nonce, setNonce] = useState(0);
  const seq = useRef(0);
  const when = point?.capturedUtc ? shortWhen(point.capturedUtc) : 'an earlier time';

  useEffect(() => {
    setPreview(null); setPreviewErr(null);
    if (!selectedId || point?.bimPath === null) return;
    const mine = ++seq.current;
    setBusy('preview');
    rpc<RollbackResult>('rollbackPush', selectedId, false, null, 'human')
      .then((r) => { if (mine !== seq.current) return; if (r.error) setPreviewErr(r.error); else setPreview(r); })
      .catch((e) => { if (mine === seq.current) setPreviewErr(String((e as Error).message ?? e)); })
      .finally(() => { if (mine === seq.current) setBusy(null); });
  }, [selectedId, nonce]);

  async function restore() {
    if (!selectedId || !preview) return;
    setBusy('restore'); setNote(null);
    const picked = when;
    try {
      const r = await rpc<RollbackResult>('rollbackPush', selectedId, true, null, 'human');
      setNote({ tone: r.error ? 'bad' : (r.failedRefs?.length ?? 0) > 0 ? 'bad' : 'good', text: rollbackResultLine(r, target, picked) });
      await onRestored();
    } catch (e) { setNote({ tone: 'bad', text: String((e as Error).message ?? e) }); }
    finally { setBusy(null); setNonce((n) => n + 1); }
  }

  // Picking another version drops the last result line; the preview then reruns by itself for the new pick.
  const pickVersion = (id: string) => { setNote(null); onSelect(id); };

  const nothing = !!preview && preview.restored <= 0 && preview.removed <= 0;
  const canPress = !!preview && !nothing && busy === null;
  return (
    <>
      {points.length > 0 && <PageAction slot={slot}>
        <button type="button" className="sem-btn sem-btn-primary" data-testid="restore-go" disabled={!canPress} onClick={() => void restore()}>
          {busy === 'restore' ? 'Restoring...' : restoreLabel(target, when)}
        </button>
      </PageAction>}
      {note && <div className="sem-inset"><StateLine tone={note.tone}>{note.text}</StateLine></div>}
      {(error || previewErr) && <div className="sem-inset"><StateLine tone="bad">{error || previewErr}</StateLine></div>}
      {points.length === 0 ? (
        <div className="sem-text-meta" data-testid="versions-empty">{loading ? 'Looking for earlier versions.' : 'No earlier versions yet. Each publish saves one first.'}</div>
      ) : (
        <section className="sem-card sem-stack" data-testid="versions">
          <div className="sem-inline flex-wrap">
            <span className="sem-text-meta">Version</span>
            <select value={selectedId} onChange={(e) => pickVersion(e.target.value)} className="sem-field" style={{ minWidth: 360 }} aria-label="Earlier version">
              {points.map((p) => <option key={p.id} value={p.id}>{shortWhen(p.capturedUtc)} · {p.reason || p.op || 'saved before a publish'}</option>)}
            </select>
          </div>
          {busy === 'preview' && <div className="sem-text-meta">Checking what would change.</div>}
          {preview && (
            <div className="sem-stack" data-testid="versions-detail">
              <div className="text-(length:--sem-text-body)">{restoreDetail(preview.restored, preview.removed, target)}</div>
              {preview.restoredRefs?.length > 0 && <Names label="Put back" refs={preview.restoredRefs} />}
              {preview.removedRefs?.length > 0 && <Names label="Removed" refs={preview.removedRefs} />}
            </div>
          )}
        </section>
      )}
    </>
  );
}
function Names({ label, refs }: { label: string; refs: string[] }) {
  return (
    <details className="sem-text-meta">
      <summary className="cursor-pointer">{label} ({refs.length})</summary>
      <div className="font-mono text-(length:--sem-text-label) mt-1 max-h-24 overflow-auto">{refs.map((r) => <div key={r}>{r}</div>)}</div>
    </details>
  );
}

// ── Promote ──────────────────────────────────────────────────────────────────────────────────
// Pick the two stages and the page checks by itself. The production token comes back with the check and goes out
// with the button, unseen.
function PromotePanel({ slot }: { slot: HTMLElement | null }) {
  const { context } = useConnection();
  // Pipelines are LAZY: listing them makes a live Entra-authed REST call, so we only fetch on demand (pressing the
  // button) rather than on every tab open, to avoid a surprise sign-in prompt.
  const [pipelines, setPipelines] = useState<DeploymentPipeline[] | null>(null);
  const [stages, setStages] = useState<Record<string, PipelineStage[]>>({});
  const [selPipe, setSelPipe] = useState<string | null>(null);
  const [pipeErr, setPipeErr] = useState<string | null>(null);
  // WHO signs in for the Fabric reads and the promote itself. An empty pick means "not chosen yet": the effective
  // mode then comes from the account the publish destination was really opened with, else a browser sign-in.
  const [pickedMode, setPickedMode] = usePersistedState<string>('promote.authMode', '');
  const [promoteTenant, setPromoteTenant] = usePersistedState<string>('promote.tenantId', '');
  const signInMode = pickedMode || defaultSignInMode(context?.publishing);
  const promoteTenantId = promoteTenant.trim() || null;
  const modePickerRef = useRef<HTMLSelectElement>(null);
  const [signingIn, setSigningIn] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [srcSel, setSrcSel] = useState('');
  const [tgtSel, setTgtSel] = useState('');
  const [report, setReport] = useState<DeployStageReport | null>(null);
  const [done, setDone] = useState<{ tone: 'good' | 'bad'; text: string } | null>(null);
  const [note, setNote] = useState('');
  const [history, setHistory] = useState<DeploymentHistoryEntry[] | null>(null);
  const seq = useRef(0);

  // Default the promote source and target to the last hop (e.g. Test to Production) whenever the pipeline's stages load.
  useEffect(() => {
    const s = selPipe ? stages[selPipe] : null;
    if (s && s.length >= 2) { setSrcSel(s[s.length - 2].id); setTgtSel(s[s.length - 1].id); }
    setReport(null); setHistory(null); setDone(null);
  }, [selPipe, stages]);

  async function loadPipelines(mode: string = signInMode) {
    setBusy('pipelines'); setPipeErr(null); setSigningIn(isInteractiveSignIn(mode));
    try {
      const ps = await rpc<DeploymentPipeline[]>('listDeploymentPipelines', mode, promoteTenantId);
      setPipelines(ps);
      if (ps.length) {   // surface the first pipeline's stages straight away
        setSelPipe(ps[0].id);
        const s = await rpc<PipelineStage[]>('getPipelineStages', ps[0].id, mode, promoteTenantId);
        setStages((m) => ({ ...m, [ps[0].id]: s }));
      }
    } catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); setSigningIn(false); }
  }
  // Pressing Sign in retries the read the browser way, which is the one way that works with nothing set up on the
  // computer. It also becomes the remembered choice, so the next press does not fail the same way.
  function signInAndRetry() { setPickedMode('interactive'); void loadPipelines('interactive'); }
  async function selectPipeline(id: string) {
    setSelPipe(id); setPipeErr(null);
    if (stages[id]) return;
    setBusy('stages');
    try { const s = await rpc<PipelineStage[]>('getPipelineStages', id, signInMode, promoteTenantId); setStages((m) => ({ ...m, [id]: s })); }
    catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  // The check: a dry-run deploy that deploys nothing. From the HUMAN door, so a production check returns the token.
  // It runs by itself whenever both stages are picked.
  useEffect(() => {
    setReport(null); setDone(null);
    if (!selPipe || !srcSel || !tgtSel || srcSel === tgtSel) return;
    const mine = ++seq.current;
    setBusy('check'); setPipeErr(null);
    rpc<DeployStageReport>('deployStage', selPipe, srcSel, tgtSel, null, null, false, null, false, signInMode, promoteTenantId, 'human')
      .then((r) => { if (mine !== seq.current) return; if (r.error) setPipeErr(r.error); else setReport(r); })
      .catch((e) => { if (mine === seq.current) setPipeErr(String((e as Error).message ?? e)); })
      .finally(() => { if (mine === seq.current) setBusy(null); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selPipe, srcSel, tgtSel]);

  const promote = async () => {
    if (!report || !selPipe) return;
    setBusy('promote'); setPipeErr(null); setDone(null);
    try {
      // deployStage's positional args: (pipeline, src, tgt, items, note, commit, confirmToken, forceOverride, authMode, tenantId, origin).
      // The stage move has no readiness gate since r1 (publish-p2): forceOverride is a placeholder the engine ignores,
      // overrideReason is not sent, and nothing here reads the gate on the report.
      const r = await rpc<DeployStageReport>('deployStage', selPipe, srcSel, tgtSel, null, note || null, true, report.confirmToken ?? null, false, signInMode, promoteTenantId, 'human');
      if (r.error) { setPipeErr(r.error); return; }
      setReport(r);
      setDone({ tone: r.committed ? 'good' : 'bad', text: r.committed ? `Promoted ${r.itemCount === 1 ? '1 item' : `${r.itemCount} items`} from ${r.sourceStageName} to ${r.targetStageName}.` : (r.plan || 'Nothing was promoted.') });
    } catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };
  const loadHistory = async () => {
    setBusy('history'); setPipeErr(null);
    try { setHistory(await rpc<DeploymentHistoryEntry[]>('deploymentHistory', selPipe, signInMode, promoteTenantId)); }
    catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };

  const hop = !!selPipe && (stages[selPipe]?.length ?? 0) >= 2;
  const canPromote = !!report && !report.committed && srcSel !== tgtSel && report.itemCount > 0 && (!report.targetIsProd || !!report.confirmToken) && busy === null;
  const label = busy === 'promote' ? 'Promoting...' : report ? promoteLabel(report.itemCount, report.sourceStageName ?? '', report.targetStageName ?? '') : 'Nothing to promote';
  return (
    <>
      {hop && <PageAction slot={slot}>
        <button type="button" className="sem-btn sem-btn-primary" data-testid="promote-go" disabled={!canPromote} onClick={() => void promote()}>{label}</button>
      </PageAction>}
      <section className="sem-card sem-stack" data-testid="promote">
        <div className="sem-text-meta">Copy a model from one stage to the next.</div>
        <div>
          <AccountPicker ref={modePickerRef} mode={signInMode} onMode={setPickedMode}
            tenantId={promoteTenant} onTenantId={setPromoteTenant}
            hint={signingIn ? 'Signing you in…' : undefined} />
        </div>
        <AccountProblem error={pipeErr} testId="promote" onSignIn={signInAndRetry} onChooseAccount={() => modePickerRef.current?.focus()} />
        {pipelines === null ? (
          <div><Btn onClick={() => loadPipelines()} busy={busy === 'pipelines'}>Find my pipelines</Btn></div>
        ) : pipelines.length === 0 ? (
          <div className="sem-text-meta max-w-[74ch]">No pipelines this account can see. Try a different account above, then <button className="underline" onClick={() => loadPipelines()}>look again</button>.</div>
        ) : (
          <>
            <div className="flex flex-wrap gap-2">
              {pipelines.map((p) => (
                <button key={p.id} onClick={() => selectPipeline(p.id)} title={p.description} aria-pressed={selPipe === p.id} className="sem-btn sem-btn-sm">
                  {p.displayName}
                </button>
              ))}
              <button className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => loadPipelines()} title="Look for pipelines again">↻</button>
            </div>
            {selPipe && (stages[selPipe] === undefined ? (
              <div className="sem-text-meta">Loading stages…</div>
            ) : stages[selPipe].length ? (
              <div className="flex items-center gap-2 flex-wrap">
                {stages[selPipe].map((s, i) => (
                  <span key={s.id} className="flex items-center gap-2">
                    {i > 0 && <Arrow />}
                    <Stage name={s.displayName} sub={s.workspaceName || (s.workspaceId ? '(no access)' : 'unassigned')} prod={/prod/i.test(s.displayName)} />
                  </span>
                ))}
              </div>
            ) : (
              <div className="sem-text-meta">This pipeline has no stages you can see.</div>
            ))}
            {hop && (
              <div className="sem-stack pt-3" style={{ borderTop: '1px solid var(--sem-rule)' }}>
                <div className="flex items-center gap-2 text-(length:--sem-text-meta) flex-wrap">
                  <span style={{ color: 'var(--sem-muted)' }}>Promote</span>
                  <StageSelect stages={stages[selPipe!]} value={srcSel} onChange={setSrcSel} />
                  <span style={{ color: 'var(--sem-muted)' }}>→</span>
                  <StageSelect stages={stages[selPipe!]} value={tgtSel} onChange={setTgtSel} />
                  <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="note (optional)" className="sem-field" style={{ width: 160 }} />
                  <Btn onClick={loadHistory} busy={busy === 'history'}>History</Btn>
                </div>
                {busy === 'check' && <div className="sem-text-meta">Checking what would move.</div>}
                {done && <StateLine tone={done.tone}>{done.text}</StateLine>}
                {report && !done && <DeployResult r={report} />}
                {history && (
                  <div className="text-(length:--sem-text-meta)" style={{ borderTop: '1px solid var(--sem-rule)', maxHeight: 150, overflow: 'auto' }}>
                    {history.length === 0 ? <div className="py-1" style={{ color: 'var(--sem-muted)' }}>No promotions yet.</div> : history.map((h) => (
                      <div key={h.id} className="flex items-center gap-2 py-1" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
                        <Badge color={h.status === 'Succeeded' ? 'var(--sem-good)' : h.status === 'Failed' ? 'var(--sem-bad)' : 'var(--sem-warn)'}>{uiLabel(h.status)}</Badge>
                        <span style={{ color: 'var(--sem-muted)' }}>{(h.executionEndTime || '').slice(0, 10)}</span>
                        {h.preDeploymentDiffInformation && <span style={{ color: 'var(--sem-muted)' }}>+{h.preDeploymentDiffInformation.newItemsCount} ~{h.preDeploymentDiffInformation.differentItemsCount}</span>}
                        {h.performedBy?.displayName && <span className="truncate">{h.performedBy.displayName}</span>}
                        {h.note?.content && <span className="truncate" style={{ color: 'var(--sem-muted)' }}>“{h.note.content}”</span>}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </section>
    </>
  );
}

// ---- styled atoms (match the other tabs' --sem-* + Tailwind) ----
function Btn({ children, onClick, primary, busy, disabled, title }: { children: React.ReactNode; onClick: () => void; primary?: boolean; busy?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={busy || disabled} title={title} className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {busy ? '…' : children}
    </button>
  );
}
// A failed Fabric call, in words, with the one thing that fixes it.
//
// It used to print whatever came back from the sign-in straight into a red line ("Please run 'az login' to set up
// account" on a laptop that has never run it). The plain line comes from the shared map in copy.ts; the raw text
// survives only for a failure the map does not recognise, because a person with an unknown failure still needs
// something to pass on.
function AccountProblem({ error, testId, onSignIn, onChooseAccount }: {
  error: string | null; testId: string; onSignIn: () => void; onChooseAccount: () => void;
}) {
  if (!error) return null;
  const problem = signInProblem(error);
  const act = problem.fix === 'signIn' ? onSignIn : problem.fix === 'chooseAccount' ? onChooseAccount : null;
  return (
    <div className="sem-inset text-(length:--sem-text-meta) max-w-[74ch]" data-testid={`${testId}-problem`}>
      <StateLine tone="bad">{problem.lead}</StateLine>
      {problem.detail && <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>{problem.detail}</div>}
      {act && problem.fixLabel && (
        <div className="mt-2" data-testid={`${testId}-signin-fix`}>
          <Btn onClick={act}>{problem.fixLabel}</Btn>
        </div>
      )}
    </div>
  );
}
// State is a word and a dot (Instrument): the dot carries the colour, the word stays readable text.
type Tone = 'bad' | 'good' | 'warn' | 'plain';
const TONE_CHIP: Record<Tone, string> = { bad: 'sem-chip sem-chip-bad', warn: 'sem-chip sem-chip-warn', good: 'sem-chip sem-chip-ok', plain: 'sem-chip sem-chip-neutral' };
function toneOf(color: string): Tone {
  return /bad/.test(color) ? 'bad' : /warn/.test(color) ? 'warn' : /good|accent/.test(color) ? 'good' : 'plain';
}
function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className={TONE_CHIP[toneOf(color)]}>{children}</span>;
}
function StateLine({ tone, children }: { tone: Tone; children: React.ReactNode }) {
  return (
    <div className="sem-inline text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)' }}>
      {tone !== 'plain' && <span className={TONE_CHIP[tone]} aria-hidden="true" />}
      <span className="min-w-0">{children}</span>
    </div>
  );
}
function StageSelect({ stages, value, onChange }: { stages: PipelineStage[]; value: string; onChange: (v: string) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="sem-field">
      {stages.map((s) => <option key={s.id} value={s.id}>{s.displayName}</option>)}
    </select>
  );
}

// What the check found: the counts and the items that would move.
function DeployResult({ r }: { r: DeployStageReport }) {
  return (
    <div className="sem-inset sem-stack text-(length:--sem-text-meta)">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="font-medium">{r.sourceStageName} → {r.targetStageName}</span>
        <span className="sem-num">+{r.newCount} new</span>
        <span className="sem-num">~{r.updateCount} update</span>
      </div>
      {r.items?.length > 0 && (
        <div style={{ borderTop: '1px solid var(--sem-rule)', maxHeight: 130, overflow: 'auto' }}>
          {r.items.map((it) => (
            <div key={it.itemId} className="flex items-center gap-2 py-1" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
              <Badge color={it.state === 'New' ? 'var(--sem-good)' : 'var(--sem-warn)'}>{uiLabel(it.state)}</Badge>
              <span style={{ color: 'var(--sem-muted)' }}>{it.itemType}</span>
              <span className="truncate">{it.itemDisplayName}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function Stage({ name, sub, prod }: { name: string; sub?: string; prod?: boolean }) {
  return (
    <div className="flex flex-col text-(length:--sem-text-meta)">
      <span className={prod ? 'sem-chip sem-chip-warn' : 'font-medium'} style={prod ? { color: 'var(--sem-fg)' } : undefined}>{name}{prod ? ' (prod)' : ''}</span>
      {sub && <span className="truncate" style={{ color: 'var(--sem-muted)', maxWidth: 160 }}>{sub}</span>}
    </div>
  );
}
function Arrow() { return <span style={{ color: 'var(--sem-muted)' }}>→</span>; }
