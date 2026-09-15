import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { usePersistedState } from './hooks';
import { rpc, onDidChange, onActivity } from './bridge';
import { CompareView, type CompareSeed, type ModelDiff } from './compare';
import { useConnection } from './connection';
import { openConnectionsOnView } from './connectionshub';
import { compareSeedFromSession } from './contextbar';
import { AccountPicker } from './accountpicker';
import { ProBadge, useFeature, PENDING_ACCESS } from './pro';
import { ProPreview } from './propreview';
import { defaultSignInMode, isInteractiveSignIn, signInProblem, uiLabel } from './copy';
import {
  checkingCopy, deployHeaderState, fabricGitWorkspaceMessage, nothingToPublishCopy, previewFailedCopy,
  publishAccountLine, publishButtonLabel, publishEntryState, rollbackResultLine, publishStageName,
} from './publishcopy';

// Wire shapes mirror Semanticus.Engine/Alm/AlmProtocol.cs (camelCased). The semantic model diff (ModelDiff) +
// its drill-down render via the shared <CompareGrid> — the Deploy Source-Control panel is a preset of the
// Compare tab (Source=session, Target=gitref:HEAD).
interface GitFileChange { path: string; status: string; staged: boolean; worktree: boolean; }
interface GitStatus { isRepo: boolean; repoRoot?: string; workingDir?: string; branch?: string; detached?: boolean; upstream?: string; ahead: number; behind: number; files: GitFileChange[]; modelDirty: boolean; note?: string; }
interface InterviewAdvisory { questions: number; replayed: number; right: number; wrong: number; unverified: number; notReplayable: number; changes?: { question: string; before?: string; after: string }[]; note?: string; }
interface DeployGate { pass: boolean; grade?: string; bpaViolations: number; bpaBlocking: number; blockers: string[]; olderWarnings?: number; checkLine?: string; changes: number; interview?: InterviewAdvisory | null; note?: string; }
interface GitCommitResult { committed: boolean; hash?: string; error?: string; note?: string; savedModelFirst?: boolean; }
interface GitActionResult { ok: boolean; output?: string; error?: string; modelReloadNeeded?: boolean; }
interface ConnectionRecord { id: string; kind: string; endpoint: string; database: string; modelName: string; label?: string | null; }
interface RestorePointRecord { id: string; endpoint: string; database: string; capturedUtc: string; bimPath?: string | null; bytes: number; op?: string; reason?: string; pushed: string[]; deleted: string[]; }
interface RollbackResult { applied: boolean; restored: number; removed: number; restoredRefs: string[]; removedRefs: string[]; failedRefs: string[]; restorePointId?: string; target?: string; note?: string; error?: string; }
interface DeploymentPipeline { id: string; displayName: string; description?: string; }
interface PipelineStage { id: string; order: number; displayName: string; description?: string; workspaceId?: string; workspaceName?: string; isPublic: boolean; }
interface DeployItemDiff { itemId: string; itemDisplayName: string; itemType: string; state: string; }
interface DeployStageReport { committed: boolean; sourceStageName?: string; targetStageName?: string; targetIsProd: boolean; itemCount: number; items: DeployItemDiff[]; newCount: number; updateCount: number; gate?: DeployGate; confirmToken?: string; status?: string; plan?: string; error?: string; }
interface DeploymentHistoryEntry { id: string; status: string; note?: { content?: string }; preDeploymentDiffInformation?: { newItemsCount: number; differentItemsCount: number; noDifferenceItemsCount: number }; performedBy?: { type?: string; displayName?: string }; executionEndTime?: string; }
interface FabricGitConnection { state?: string; providerType?: string; organization?: string; repository?: string; branch?: string; directory?: string; head?: string; lastSyncTime?: string; error?: string; }
interface FabricGitChange { objectId?: string; itemType?: string; displayName?: string; workspaceChange?: string; remoteChange?: string; conflictType?: string; }
interface FabricGitStatus { workspaceHead?: string; remoteCommitHash?: string; changes: FabricGitChange[]; conflicts: boolean; error?: string; }
interface FabricGitResult { committed: boolean; action?: string; direction?: string; status?: string; changeCount: number; conflicts: boolean; plan?: string; error?: string; }
interface CicdFile { path: string; content: string; }
interface CicdScaffold { files: CicdFile[]; written: boolean; writtenPaths: string[]; skippedPaths: string[]; note?: string; error?: string; }
interface CicdPublishResult { committed: boolean; action?: string; workspaceId?: string; itemId?: string; modelPath?: string; partCount: number; sampleParts: string[]; status?: string; plan?: string; error?: string; }
interface LivePublishReport {
  committed: boolean; endpoint?: string; database?: string; totalChanges: number;
  changes?: string[]; added?: number; liveOnly?: string[]; unmatched?: string[]; conflicts?: string[];
  matchNote?: string; error?: string; confirmToken?: string;
}

type DeployMode = 'push' | 'rollback' | 'promote' | 'advanced';
type AdvancedView = 'delivery' | 'dataagent';

// One release decision surface: review and push the working copy, restore a guarded snapshot, promote between stages,
// or open the less-common delivery and Data Agent tools. The same engine operations remain available to both doors.
// ── Can we reach the publish destination? ──────────────────────────────────────────────────────────────
// A publish needs a DESTINATION, not a query connection, so the header keeps naming the destination even with
// no live model attached. But it used to offer an enabled Publish and a full-looking comparison while nothing
// had actually been read from that destination (M17, walkthrough 2026-09-14). The Published page's own
// comparison is the one thing that really talks to the destination, so it publishes what it found here and the
// header's single Publish button reads it. Unknown (null) means we have not tried: Publish stays enabled.
// A flag, not a name. The Published page resolves its destination label from the connection registry, which
// loads a beat after the first comparison can fail, so carrying a name here printed the half-resolved one
// ('Contoso') in a header that says 'Contoso Sales'. The header names the destination; this only says reachable.
type PublishReach = { unreachable: true } | null;
let reachState: PublishReach = null;
const reachListeners = new Set<(value: PublishReach) => void>();
function setPublishReach(next: PublishReach): void {
  if (!!reachState === !!next) { reachState = next; return; }
  reachState = next;
  reachListeners.forEach((listener) => { try { listener(next); } catch { /* isolate a view */ } });
}
export function usePublishReach(): PublishReach {
  const [value, setValue] = useState(reachState);
  useEffect(() => { reachListeners.add(setValue); setValue(reachState); return () => { reachListeners.delete(setValue); }; }, []);
  return value;
}

export function DeployView({ seed, dataAgent, initialMode = 'push', initialAdvanced = 'delivery', restoreTarget, onRestoreConsumed, publishNonce = 0, onPublishConsumed }: {
  seed?: CompareSeed | null; dataAgent?: ReactNode; initialMode?: DeployMode; initialAdvanced?: AdvancedView;
  restoreTarget?: { id: string; endpoint: string; database: string; nonce: number } | null;
  onRestoreConsumed?: () => void;
  publishNonce?: number; onPublishConsumed?: (nonce: number) => void;
}) {
  const { session, conn, context, openConnections } = useConnection();
  const [mode, setMode] = useState<DeployMode>(initialMode);
  // A rollback deep-link is one-shot: adopt its target into local state, then App clears the prop so leaving
  // and re-opening Deploy does not re-force stale Roll back on every remount.
  const [adopted, setAdopted] = useState<{ id: string; endpoint: string; database: string } | null>(null);
  const [advancedView, setAdvancedView] = useState<AdvancedView>(initialAdvanced);
  // ONLY the Advanced mode is gated. Publish, Promote, compare and restore points stay free, so this grant
  // touches nothing else on the page. The Advanced button keeps its place and wears the pill; pressing it
  // lands on the preview, which is where the feature explains itself.
  //
  // Three states, and the live body needs the third one: `=== 'granted'`, never `!== 'denied'`. With
  // getEntitlement in flight the first version mounted the real Advanced panels and a press on Data Agent
  // called listDataAgents (Astra, 2026-09-15). An unanswered plan is not an unlocked one.
  const advancedGrant = useFeature('publishedAdvanced');
  const advancedLocked = advancedGrant === 'denied';
  const advancedPending = advancedGrant === 'unknown';
  const advancedOpen = advancedGrant === 'granted';
  const [status, setStatus] = useState<GitStatus | null>(null);
  const [diff, setDiff] = useState<ModelDiff | null>(null);
  const [connections, setConnections] = useState<ConnectionRecord[]>([]);
  const [restorePoints, setRestorePoints] = useState<RestorePointRecord[]>([]);
  const [restoreId, setRestoreId] = useState('');
  const [restorePreview, setRestorePreview] = useState<RollbackResult | null>(null);
  const [restoreResult, setRestoreResult] = useState<RollbackResult | null>(null);
  const [restoreNote, setRestoreNote] = useState<string | null>(null);   // the plain result line for a confirmed restore
  const [restoreBusy, setRestoreBusy] = useState<'load' | 'preview' | 'confirm' | null>(null);
  const [restoreErr, setRestoreErr] = useState<string | null>(null);
  const [gate, setGate] = useState<DeployGate | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [msg, setMsg] = useState('');
  // Fabric pipelines are LAZY — listing them makes a live Entra-authed REST call, so we only fetch on demand
  // (clicking the button) rather than on every tab open, to avoid a surprise sign-in prompt.
  const [pipelines, setPipelines] = useState<DeploymentPipeline[] | null>(null);
  const [stages, setStages] = useState<Record<string, PipelineStage[]>>({});
  const [selPipe, setSelPipe] = useState<string | null>(null);
  const [pipeErr, setPipeErr] = useState<string | null>(null);
  // WHO signs in for the Fabric reads and the promote itself. Promote used to hard-code the Azure command
  // line, so a computer that has never run it could only fail (Kane, 2026-09-14). An empty pick means "not
  // chosen yet": the effective mode then comes from the account the publish destination was really opened
  // with, and falls back to a browser sign-in. Reading it this way, rather than seeding the persisted
  // value, means a destination that resolves AFTER this page mounts still decides the starting choice.
  const [pickedMode, setPickedMode] = usePersistedState<string>('promote.authMode', '');
  const [promoteTenant, setPromoteTenant] = usePersistedState<string>('promote.tenantId', '');
  const signInMode = pickedMode || defaultSignInMode(context?.publishing);
  const promoteTenantId = promoteTenant.trim() || null;
  const modePickerRef = useRef<HTMLSelectElement>(null);
  // Which panel is holding a person at a sign-in step, or null. A flag, not a boolean, because Fabric Git
  // and CI·CD·Publish render side by side under Advanced: one shared boolean would put "Signing you in…"
  // on both pickers at once while only one of them was really doing anything.
  const [signingIn, setSigningIn] = useState<string | null>(null);
  // Promote (preview + gated deploy) + history
  const [srcSel, setSrcSel] = useState('');
  const [tgtSel, setTgtSel] = useState('');
  const [report, setReport] = useState<DeployStageReport | null>(null);
  const [note, setNote] = useState('');
  const [override, setOverride] = useState(false);
  const [overrideReason, setOverrideReason] = useState('');   // required when override is ON — the engine refuses a blank one
  const [history, setHistory] = useState<DeploymentHistoryEntry[] | null>(null);
  // Fabric Git (workspace ⇄ git)
  // Its own choice of who signs in, remembered under its own key. Every Fabric Git call used to hard-code
  // the Azure command line, so on a computer that has never run it (Kane's laptop, 2026-09-15) Status could
  // only print a red line about a command he does not have. Same starting rule as Promote: the way the
  // publish destination was really opened, falling back to a browser sign-in.
  const [fgPicked, setFgPicked] = usePersistedState<string>('fabricgit.authMode', '');
  const [fgTenant, setFgTenant] = usePersistedState<string>('fabricgit.tenantId', '');
  const fgSignInMode = fgPicked || defaultSignInMode(context?.publishing);
  const fgTenantId = fgTenant.trim() || null;
  const fgPickerRef = useRef<HTMLSelectElement>(null);
  const [fgWs, setFgWs] = useState('');
  const [fgLoadedWs, setFgLoadedWs] = useState('');   // the id the loaded status/conn belong to — guards a wrong-target write
  const [fgConn, setFgConn] = useState<FabricGitConnection | null>(null);
  const [fgStatus, setFgStatus] = useState<FabricGitStatus | null>(null);
  const [fgResult, setFgResult] = useState<FabricGitResult | null>(null);
  const [fgErr, setFgErr] = useState<string | null>(null);   // panel-local error (status read / write failure)
  const [fgPending, setFgPending] = useState<{ action: 'commit' | 'update'; plan: string; conflicts: boolean } | null>(null);
  // CI·CD · Publish: emit a fabric-cicd scaffold (no gate) + publish the open model's definition (gated, two-step).
  const [cicdTarget, setCicdTarget] = useState<'github' | 'ado'>('github');
  const [cicdEnv, setCicdEnv] = useState('PROD');
  const [cicdWs, setCicdWs] = useState('');
  const [scaffold, setScaffold] = useState<CicdScaffold | null>(null);
  const [scaffoldOpen, setScaffoldOpen] = useState<string | null>(null);
  // The publish half of this panel is the only half that signs in: cicd_generate just writes files
  // (EngineRpcTarget.cs cicdGenerate takes no authMode at all), so the picker sits over the publish rows.
  const [pubPicked, setPubPicked] = usePersistedState<string>('cicdpublish.authMode', '');
  const [pubTenant, setPubTenant] = usePersistedState<string>('cicdpublish.tenantId', '');
  const pubSignInMode = pubPicked || defaultSignInMode(context?.publishing);
  const pubTenantId = pubTenant.trim() || null;
  const pubPickerRef = useRef<HTMLSelectElement>(null);
  const [pubWs, setPubWs] = useState('');
  const [pubItem, setPubItem] = useState('');
  const [pubResult, setPubResult] = useState<CicdPublishResult | null>(null);
  const [pubPending, setPubPending] = useState<CicdPublishResult | null>(null);   // dry-run preview awaiting Confirm
  const [cicdErr, setCicdErr] = useState<string | null>(null);   // the scaffold half: file problems, never sign-in
  const [pubErr, setPubErr] = useState<string | null>(null);     // the publish half, which does sign in
  const timer = useRef<number | undefined>(undefined);
  // Editing the workspace id invalidates a loaded preview — clear it so the gated buttons hide until Status reloads.
  const fgEditWs = (v: string) => { setFgWs(v); setFgStatus(null); setFgConn(null); setFgResult(null); setFgErr(null); setFgPending(null); };
  const [publishOpen, setPublishOpen] = useState(false);
  const [preview, setPreview] = useState<LivePublishReport | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewErr, setPreviewErr] = useState<string | null>(null);
  const [publishBusy, setPublishBusy] = useState(false);
  const [publishOverride, setPublishOverride] = useState('');
  const [publishNeedReason, setPublishNeedReason] = useState(false);
  const [publishGate, setPublishGate] = useState<DeployGate | null>(null);
  const [publishResult, setPublishResult] = useState<string | null>(null);
  const [deleteRefs, setDeleteRefs] = useState<string[]>([]);

  // Default the promote source→target to the last hop (e.g. Test→Prod) whenever the selected pipeline's stages load.
  useEffect(() => {
    const s = selPipe ? stages[selPipe] : null;
    if (s && s.length >= 2) { setSrcSel(s[s.length - 2].id); setTgtSel(s[s.length - 1].id); }
    setReport(null); setHistory(null); setOverride(false); setOverrideReason('');
  }, [selPipe, stages]);

  async function load() {
    try {
      const st = await rpc<GitStatus>('gitStatus');
      setStatus(st); setErr(null);
      if (st.isRepo) {
        try { setDiff(await rpc<ModelDiff>('compareModels', { kind: 'session' }, { kind: 'gitref', gitRef: 'HEAD' })); }
        catch { setDiff(null); }   // e.g. no commit yet
      } else setDiff(null);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    try { setConnections((await rpc<ConnectionRecord[]>('listConnections')) ?? []); }
    catch { /* target labelling is supporting context; preserve the deploy controls if the registry is unavailable */ }
  }
  useEffect(() => {
    void load();
    // A model/git change invalidates a previously-computed gate result — clear it (an explicit gate check goes
    // through run()/load(), which does NOT clear, so the user's result stays put until something actually changes).
    const reload = () => { setGate(null); window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void load(), 350); };
    const offC = onDidChange(reload);
    const offA = onActivity((e) => { if (e.kind && (e.kind.startsWith('git_') || e.kind === 'apply_model_diff')) reload(); });
    return () => { offC(); offA(); window.clearTimeout(timer.current); };
  }, []);
  useEffect(() => { if (seed) setMode('push'); }, [seed?.nonce]);
  async function loadPreview(ticked: string[] = []) {
    setPreview(null);
    setPreviewBusy(true); setPreviewErr(null); setPublishNeedReason(false); setPublishGate(null);
    try {
      const dry = await rpc<LivePublishReport>('deployLive', null, null, null, null, null, false, 'human', null, null, ticked);
      let g: DeployGate | null = null;
      try { g = await rpc<DeployGate>('deployGate', null); } catch { g = null; }
      if (g) {
        setPublishGate(g);
        if (!g.pass) setPublishNeedReason(true);
      }
      if (dry.error) { setPreviewErr(dry.error); setPreview(null); }
      else setPreview(dry);
    } catch (e) { setPreviewErr(String((e as Error).message ?? e)); setPreview(null); }
    finally { setPreviewBusy(false); }
  }
  function openPublish() {
    // No resolved destination means there is nothing to review against: stay on the choose-a-destination panel
    // rather than opening a confirm that would name "No live target" and offer to publish anyway.
    setPublishResult(null); setDeleteRefs([]);
    if (!canPublish) { setPublishOpen(false); return; }
    setPublishOpen(true);
    void loadPreview([]);
  }
  function toggleDelete(ref: string) {
    const next = deleteRefs.includes(ref) ? deleteRefs.filter((r) => r !== ref) : [...deleteRefs, ref];
    setDeleteRefs(next);
    void loadPreview(next);
  }
  async function confirmPublish(reason?: string) {
    if (!preview?.confirmToken) return;
    setPublishBusy(true); setPreviewErr(null);
    try {
      const res = await rpc<LivePublishReport>('deployLive', null, null, null, null, null, true, 'human', reason?.trim() || null, preview.confirmToken, deleteRefs);
      if (res.error) { setPreviewErr(res.error); return; }
      if (res.committed) {
        const dest = res.database || targetName;
        const n = res.totalChanges ?? 0;
        setPublishResult(`Published ${n} change${n === 1 ? '' : 's'} to ${dest}. Your local model edits are unchanged.`);
        setPublishOpen(false); setPreview(null); setPublishNeedReason(false); setPublishOverride(''); setDeleteRefs([]);
        await loadRestorePoints();   // the push just wrote a restore point — the header + snapshot counts must follow it now
      } else setPreviewErr(res.error || 'Publish made no changes.');
    } catch (e) {
      const emsg = String((e as Error).message ?? e);
      if (emsg.includes('blocked by the deploy gate')) { setPreviewErr(emsg); setPublishNeedReason(true); }
      else setPreviewErr(emsg);
    } finally { setPublishBusy(false); }
  }
  useEffect(() => {
    if (!publishNonce) return;
    openPublish(); onPublishConsumed?.(publishNonce);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [publishNonce]);

  async function loadRestorePoints() {
    setRestoreBusy('load'); setRestoreErr(null);
    try {
      const scopeEndpoint = adopted?.endpoint || endpoint;
      const scopeDatabase = adopted?.database || database;
      const points = (await rpc<RestorePointRecord[]>('listRestorePoints', scopeEndpoint || null, scopeDatabase || null)) ?? [];
      setRestorePoints(points);
      setRestoreId((id) => points.some((p) => p.id === id) ? id : (points[0]?.id ?? ''));
    } catch (e) { setRestoreErr(String((e as Error).message ?? e)); }
    finally { setRestoreBusy(null); }
  }
  async function previewRollback() {
    if (!restoreId) return;
    setRestoreBusy('preview'); setRestoreErr(null); setRestoreResult(null); setRestoreNote(null);
    try {
      const r = await rpc<RollbackResult>('rollbackPush', restoreId, false, null, 'human');
      setRestorePreview(r); if (r.error) setRestoreErr(r.error);
    } catch (e) { setRestoreErr(String((e as Error).message ?? e)); }
    finally { setRestoreBusy(null); }
  }
  async function confirmRollback() {
    if (!restoreId || !restorePreview) return;
    setRestoreBusy('confirm'); setRestoreErr(null);
    // Name the point BEFORE the write: loadRestorePoints() below replaces the list the label is read from.
    const point = restorePoints.find((p) => p.id === restoreId);
    const restored = point?.database || point?.endpoint || targetName;
    const when = point?.capturedUtc ? relativeTime(point.capturedUtc) : 'the saved point';
    try {
      const r = await rpc<RollbackResult>('rollbackPush', restoreId, true, null, 'human');
      setRestorePreview(null); setRestoreResult(r); if (r.error) setRestoreErr(r.error);
      setRestoreNote(rollbackResultLine(r, restored, when));
      await loadRestorePoints();
      // The published model just moved, so any comparison on screen is stale. Re-check it when the review card is
      // open, otherwise drop it so the header returns to "click Publish to review changes".
      if (publishOpen) await loadPreview(deleteRefs); else setPreview(null);
    } catch (e) { setRestoreErr(String((e as Error).message ?? e)); }
    finally { setRestoreBusy(null); }
  }

  async function run(label: string, fn: () => Promise<unknown>) {
    setBusy(label); setErr(null);
    try { await fn(); await load(); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }
  const commit = () => run('commit', async () => {
    if (!msg.trim()) { setErr('Enter a commit message.'); return; }
    const r = await rpc<GitCommitResult>('gitCommit', msg.trim(), null, true, 'human');
    if (r.error) setErr(r.error); else setMsg('');
  });
  const push = () => run('push', async () => { const r = await rpc<GitActionResult>('gitPush', null, null, true, 'human'); if (!r.ok) setErr(r.error || r.output || 'push failed'); });
  const pull = () => run('pull', async () => { const r = await rpc<GitActionResult>('gitPull', 'human'); if (!r.ok) setErr(r.error || 'pull failed'); else if (r.modelReloadNeeded) setErr('The files on disk changed. Reload the model to match the files.'); });
  const checkGate = () => run('gate', async () => setGate(await rpc<DeployGate>('deployGate', null)));

  // `mode` is passed in rather than read off state, so the Sign in action can retry with the browser
  // sign-in in the same tick it switches to it.
  async function loadPipelines(mode: string = signInMode) {
    setBusy('pipelines'); setPipeErr(null); setSigningIn(isInteractiveSignIn(mode) ? 'promote' : null);
    try {
      const ps = await rpc<DeploymentPipeline[]>('listDeploymentPipelines', mode, promoteTenantId);
      setPipelines(ps);
      if (ps.length) {   // surface the first pipeline's stages straight away
        setSelPipe(ps[0].id);
        const s = await rpc<PipelineStage[]>('getPipelineStages', ps[0].id, mode, promoteTenantId);
        setStages((m) => ({ ...m, [ps[0].id]: s }));
      }
    } catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); setSigningIn(null); }
  }
  // Pressing Sign in retries the read the browser way, which is the one way that works with nothing set up
  // on the computer. It also becomes the remembered choice, so the next press does not fail the same way.
  function signInAndRetry() {
    setPickedMode('interactive');
    void loadPipelines('interactive');
  }
  async function selectPipeline(id: string) {
    setSelPipe(id); setPipeErr(null);
    if (stages[id]) return;
    setBusy('stages');
    try { const s = await rpc<PipelineStage[]>('getPipelineStages', id, signInMode, promoteTenantId); setStages((m) => ({ ...m, [id]: s })); }
    catch (e) { setPipeErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }
  // Preview = a dry-run deploy (deploys nothing). From the HUMAN door, so a prod preview surfaces the confirmToken.
  const doPreview = () => run('preview', async () => { setPipeErr(null); setOverride(false); setOverrideReason(''); setReport(await rpc<DeployStageReport>('deployStage', selPipe, srcSel, tgtSel, null, note || null, false, null, false, signInMode, promoteTenantId, 'human', null)); });
  const doDeploy = () => run('deploy', async () => {
    // forceOverride now needs an overrideReason (the engine refuses a bare toggle). deployStage's positional args are
    // (pipeline, src, tgt, items, note, commit, confirmToken, forceOverride, authMode, tenantId, origin, overrideReason)
    // — so the reason is the 12th; pass authMode/tenantId/origin explicitly to reach it. null when the toggle is off.
    const reason = override ? overrideReason.trim() || null : null;
    const r = await rpc<DeployStageReport>('deployStage', selPipe, srcSel, tgtSel, null, note || null, true, report?.confirmToken ?? null, override, signInMode, promoteTenantId, 'human', reason);
    setReport(r); if (r.error) setPipeErr(r.error);
  });
  const loadHistory = () => run('history', async () => { setPipeErr(null); setHistory(await rpc<DeploymentHistoryEntry[]>('deploymentHistory', selPipe, signInMode, promoteTenantId)); });

  // Fabric Git: read the workspace's connection + status. Commit (workspace→git) / Update (git→workspace) are a
  // genuine TWO-STEP confirm — a first click runs the engine's commit=false dry-run (writes nothing) and shows the
  // plan; only an explicit "Confirm" click sends commit=true. The reads now report failures on the DTO .error
  // (the engine no longer throws across the door), so we surface that in the panel-local fgErr.
  // Every call here carries the account picked at the top of the panel. `fgMode` is an argument rather than
  // read off state, so Sign in can retry with the browser sign-in in the same tick it switches to it.
  const fgReadStatus = (fgMode: string = fgSignInMode) => run('fgload', async () => {
    setFgErr(null); setFgResult(null); setFgPending(null); setFgStatus(null); setFgConn(null);
    const missing = fabricGitWorkspaceMessage(fgWs);
    if (missing) { setFgErr(missing); return; }
    const ws = fgWs.trim();
    setSigningIn(isInteractiveSignIn(fgMode) ? 'fabricgit' : null);
    try {
      const conn = await rpc<FabricGitConnection>('fabricGitConnection', ws, fgMode, fgTenantId);
      const st = await rpc<FabricGitStatus>('fabricGitStatus', ws, fgMode, fgTenantId);
      if (conn.error || st.error) { setFgErr(conn.error || st.error || 'Fabric Git read failed.'); return; }
      setFgConn(conn); setFgStatus(st); setFgLoadedWs(ws);
    } finally { setSigningIn(null); }
  });
  // Sign in only ever repeats a READ. Nothing on this page commits to a workspace, and nothing overwrites a
  // model, because a button said Sign in: a write that failed on the sign-in keeps its own Confirm step, and
  // the person presses that themselves once they are in.
  function fgSignInAndRetry() {
    setFgPicked('interactive');
    void fgReadStatus('interactive');
  }
  // Step 1: dry-run preview (commit=false) — surfaces the pending change count/plan, mutates nothing.
  const fgPreview = (action: 'commit' | 'update') => run(action === 'commit' ? 'fgcommit' : 'fgupdate', async () => {
    setFgErr(null); setFgResult(null);
    const ws = fgWs.trim();
    const r = action === 'commit'
      ? await rpc<FabricGitResult>('fabricGitCommit', ws, null, null, false, fgSignInMode, fgTenantId)
      : await rpc<FabricGitResult>('fabricGitUpdate', ws, 'PreferRemote', false, false, fgSignInMode, fgTenantId);
    if (r.error) { setFgErr(r.error); return; }
    setFgPending({ action, plan: r.plan || '', conflicts: r.conflicts });
  });
  // Step 2: the confirmed live write (commit=true). A failure KEEPS the pending step: the two-step used to
  // be thrown away on any error, so a sign-in that failed here left the person with a red line and no way
  // back to the Confirm they had already earned.
  const fgConfirm = () => run('fgconfirm', async () => {
    if (!fgPending) return;
    setFgErr(null);
    const ws = fgWs.trim();
    const r = fgPending.action === 'commit'
      ? await rpc<FabricGitResult>('fabricGitCommit', ws, null, null, true, fgSignInMode, fgTenantId)
      : await rpc<FabricGitResult>('fabricGitUpdate', ws, 'PreferRemote', false, true, fgSignInMode, fgTenantId);
    if (r.error) { setFgErr(r.error); return; }
    setFgPending(null); setFgResult(r);
    const st = await rpc<FabricGitStatus>('fabricGitStatus', ws, fgSignInMode, fgTenantId);
    if (!st.error) setFgStatus(st);
  });

  // CI·CD — generate the fabric-cicd scaffold (write=false returns contents; write=true lands them in the repo).
  const cicdGenerate = (write: boolean) => run(write ? 'cicdwrite' : 'cicdgen', async () => {
    setCicdErr(null);
    const r = await rpc<CicdScaffold>('cicdGenerate', cicdTarget, cicdWs.trim() || null, cicdEnv.trim() || 'PROD', write);
    setScaffold(r); if (r.error) setCicdErr(r.error);
  });
  // Publish is a real two-step like Fabric Git: a Plan click dry-runs (enumerates parts, POSTs nothing) → Confirm publishes.
  const pubPreview = (pubMode: string = pubSignInMode) => run('pubprev', async () => {
    setPubErr(null); setPubResult(null);
    setSigningIn(isInteractiveSignIn(pubMode) ? 'cicd' : null);
    try {
      const r = await rpc<CicdPublishResult>('cicdPublish', pubWs.trim() || null, pubItem.trim() || null, false, pubMode, pubTenantId);
      if (r.error) { setPubErr(r.error); return; }
      setPubPending(r);
    } finally { setSigningIn(null); }
  });
  // Sign in repeats the PLAN, which is the engine's dry run and posts nothing. The confirmed publish is a
  // full overwrite of somebody's model, so it stays where it belongs: behind the person's own second click.
  function pubSignInAndRetry() {
    setPubPicked('interactive');
    void pubPreview('interactive');
  }
  const pubConfirm = () => run('pubconf', async () => {
    setPubErr(null);
    if (!pubWs.trim() || !pubItem.trim()) { setPubErr('Workspace id and item id are required to publish.'); return; }
    const r = await rpc<CicdPublishResult>('cicdPublish', pubWs.trim(), pubItem.trim(), true, pubSignInMode, pubTenantId);
    if (r.error) { setPubErr(r.error); return; }   // keep the pending plan, so Confirm survives a failed sign-in
    setPubPending(null); setPubResult(r);
  });
  // The Deploy button mirrors the engine's gate: needs a fresh preview, and for prod the confirmToken the preview
  // surfaced. When overriding a failing gate, the engine requires a typed reason — so gate that here too.
  const canDeploy = !!report && !report.committed && srcSel !== tgtSel && (!report.targetIsProd || !!report.confirmToken) && (!override || !!overrideReason.trim());

  const dirty = (status?.files?.length ?? 0) > 0 || status?.modelDirty;

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
  useEffect(() => {
    setRestorePreview(null); setRestoreResult(null); setRestoreErr(null);
    if (!restoreTarget && !adopted) void loadRestorePoints();
    // The selected publish identity is the restore-point scope. A target change must never leave the old target's
    // rollback choices on screen.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [endpoint, database]);
  useEffect(() => {
    if (!restoreTarget) return;
    const t = restoreTarget;
    setAdopted({ id: t.id, endpoint: t.endpoint, database: t.database });
    setMode('rollback'); setRestoreBusy('load'); setRestoreErr(null); setRestorePreview(null); setRestoreResult(null);
    rpc<RestorePointRecord[]>('listRestorePoints', t.endpoint, t.database)
      .then((points) => {
        const found = points ?? [];
        setRestorePoints(found);
        setRestoreId(found.some((p) => p.id === t.id) ? t.id : (found[0]?.id ?? ''));
      })
      .catch((e) => setRestoreErr(String((e as Error).message ?? e)))
      .finally(() => setRestoreBusy(null));
    onRestoreConsumed?.();   // one-shot: App clears the prop so a later remount does not re-force stale Roll back
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [restoreTarget?.nonce]);
  const norm = (v?: string) => (v || '').trim().toLowerCase().replace(/[\\/]+$/, '');
  const visibleEndpoint = mode === 'rollback' && adopted ? adopted.endpoint : endpoint;
  const visibleDatabase = mode === 'rollback' && adopted ? adopted.database : database;
  const target = connections.find((c) => c.id === context?.publishing?.connectionId) ?? connections.find((c) => norm(c.endpoint) === norm(visibleEndpoint)
    && (!visibleDatabase || !c.database || norm(c.database) === norm(visibleDatabase)));
  const targetName = target?.modelName || target?.database || visibleDatabase || (visibleEndpoint ? visibleEndpoint.slice(visibleEndpoint.lastIndexOf('/') + 1) : 'No live target');
  const targetLabel = target?.label ? publishStageName(target.label) : (endpoint ? 'treated as production' : null);
  const lastRestore = restorePoints[0]?.capturedUtc ? relativeTime(restorePoints[0].capturedUtc) : 'none';
  const liveBound = !!session?.liveBound;
  // A local working copy is not live-bound (the engine keeps LiveOrigin null for it by the open_live/open_local
  // write-authority pin), but it can still publish when a destination resolves from the connection registry
  // (PublishConnectionId). Gate Publish on the resolved destination, not on liveBound alone (D-178 / D-226).
  const canPublish = liveBound || !!(context?.publishing?.available);
  const header = deployHeaderState({
    loading: previewBusy && publishOpen && !preview && !previewErr,
    liveBound: canPublish,
    modelName: session?.modelName || 'the open model',
    targetName,
    changeCount: preview ? preview.totalChanges : null,
    previewError: previewErr,
    lastRestore,
  });
  // A new model, or a new destination, means we have not tried to read it yet: never carry a stale refusal over.
  useEffect(() => { setPublishReach(null); }, [session?.sessionId, context?.publishing?.connectionId]);
  const account = context?.publishing?.account || session?.currentAccount;
  const entry = publishEntryState({ canPublish, publishOpen });

  return (
    <div className="h-full overflow-auto" style={{ color: 'var(--sem-fg)' }}>
      <div className="sem-evidence-page pt-3 flex flex-col gap-3">
        <div className="rounded-lg p-4" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
          <div className="flex items-center gap-2 flex-wrap">
            <h2 className="text-[16px] font-semibold m-0">Publishing</h2>
            {/* No destination means no stage to label: the fallback endpoint would be the query connection, not a publish target. */}
            {canPublish && targetLabel && <Badge color={target?.label ? 'var(--sem-muted)' : 'var(--sem-warn)'}>{targetLabel}</Badge>}
            {/* No Publish button here. The header's Publish… is the app's ONE publish action; pressing it while you
                are on this page opens the review in place through the publish route seed, and with no destination it
                lands on the choose-a-destination card below. A second button here made people hunt for the right one. */}
          </div>
          <div className="text-[13px] mt-1" data-testid="deploy-header" style={{ color: 'var(--sem-fg)' }}>{header.line}</div>
          <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
            Publishing writes your reviewed changes to the destination. Your local model edits stay unchanged.
          </div>
          <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>
            Ctrl+S saves what is in front of you. It never publishes.
            <button type="button" onClick={() => { openConnectionsOnView('setup'); openConnections(); }} className="underline ml-1">Change publish destination</button>
          </div>
          <div className="flex items-center gap-2 mt-3 flex-wrap">
            <ModeBtn active={mode === 'push'} onClick={() => setMode('push')}>What to publish</ModeBtn>
            <ModeBtn active={mode === 'rollback'} onClick={() => setMode('rollback')}>Roll back</ModeBtn>
            <ModeBtn active={mode === 'promote'} onClick={() => setMode('promote')}>Promote</ModeBtn>
            <ModeBtn active={mode === 'advanced'} onClick={() => setMode('advanced')}>Advanced<ProBadge show={advancedLocked} /></ModeBtn>
          </div>
        </div>
        {publishResult && <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-good) 12%, transparent)', color: 'var(--sem-good)', border: '1px solid var(--sem-border)' }}>{publishResult}</div>}
        {mode === 'push' && entry === 'choose-destination' && (
          <div className="rounded-lg p-4" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
            <div className="text-[14px] font-semibold">No publish destination yet</div>
            <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>Choose where this model publishes, then review the changes that would go there.</div>
            {/* The one action on the page: the head button is hidden in this state so there is a single Choose destination. */}
            <button type="button" onClick={() => { openConnectionsOnView('setup'); openConnections(); }}
              className="mt-3 px-3 py-1.5 rounded-md text-[12px] font-semibold"
              style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', border: '1px solid var(--sem-accent)' }}>Choose destination</button>
          </div>
        )}
        {/* The review sits above the mode content, so pressing Publish from Roll back or Promote does not throw away
            where you were. */}
        {entry === 'review' && <PublishConfirm preview={preview} busy={previewBusy || publishBusy} error={previewErr}
          targetName={targetName} endpoint={endpoint || ''} account={account} needReason={publishNeedReason} reason={publishOverride}
          gate={publishGate} deleteRefs={deleteRefs} onToggleDelete={toggleDelete}
          onReason={setPublishOverride} onConfirm={() => void confirmPublish(publishNeedReason ? publishOverride : undefined)}
          onCancel={() => { setPublishOpen(false); setPublishNeedReason(false); setDeleteRefs([]); }} />}
        {mode === 'push' && entry === 'compare' && <CompareView key={session?.sessionId ?? 'none'} seed={reviewSeed} embedded
          destinationName={targetName}
          onTargetRead={(state) => setPublishReach(state.ok ? null : { unreachable: true })} />}
        {mode === 'rollback' && <RollbackPanel points={restorePoints} selectedId={restoreId}
          onSelect={(id) => { setRestoreId(id); setRestorePreview(null); setRestoreResult(null); setRestoreErr(null); setRestoreNote(null); }}
          preview={restorePreview} result={restoreResult} resultNote={restoreNote} error={restoreErr} busy={restoreBusy}
          onPreview={previewRollback} onConfirm={confirmRollback} onReload={loadRestorePoints} />}
        {mode === 'advanced' && advancedOpen && <div className="rounded-lg p-3" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
          <div className="text-[11px] mb-2" style={{ color: 'var(--sem-muted)' }}>Connect source control, sync a Fabric workspace, set up automated delivery or publish a Data Agent.</div>
          <div className="flex items-center gap-2 flex-wrap">
            <ModeBtn active={advancedView === 'delivery'} onClick={() => setAdvancedView('delivery')}>Delivery tools</ModeBtn>
            <ModeBtn active={advancedView === 'dataagent'} onClick={() => setAdvancedView('dataagent')}>Data Agent</ModeBtn>
          </div>
        </div>}
      </div>
      {err && <Banner tone="bad">{err}</Banner>}

      {/* Advanced, on a plan that does not reach it. Rendered in place, so Publish, Promote, compare and
          restore points are still one click away on the row above. Nothing below this line mounts. */}
      {mode === 'advanced' && advancedLocked && (
        <div className="sem-evidence-page"><ProPreview tool="deploy-advanced" embedded /></div>
      )}
      {/* And while the plan has not answered: neither the panels nor the upsell, because neither is true yet. */}
      {mode === 'advanced' && advancedPending && (
        <div className="sem-evidence-page pt-3 text-[12px]" role="status" data-testid="advanced-checking"
          style={{ color: 'var(--sem-muted)' }}><span className="sem-spin" /> {PENDING_ACCESS}</div>
      )}

      {mode === 'advanced' && advancedOpen && advancedView === 'dataagent' && dataAgent}

      {/* ── SOURCE CONTROL ───────────────────────────────────────────── */}
      {mode === 'advanced' && advancedOpen && advancedView === 'delivery' && <Panel title="Source Control" sub="local git + model versioning">
        {!status?.isRepo ? (
          <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{status?.note || 'Open a model that lives in a git repository.'}</div>
        ) : (
          <>
            <div className="flex items-center gap-3 text-[12px] flex-wrap mb-2">
              <span>⎇ <b>{status.branch || (status.detached ? 'detached' : '?')}</b></span>
              {status.upstream && <Chip>{status.upstream}</Chip>}
              {(status.ahead > 0 || status.behind > 0) && <Chip>↑{status.ahead} ↓{status.behind}</Chip>}
              {dirty ? <Badge color="var(--sem-warn)">{(status.files?.length ?? 0)} changed{status.modelDirty ? ' · unsaved edits' : ''}</Badge> : <Badge color="var(--sem-good)">clean</Badge>}
              <div className="ml-auto flex gap-1.5">
                <Btn onClick={pull} busy={busy === 'pull'}>Pull</Btn>
                <Btn onClick={push} busy={busy === 'push'} title="confirm-gated push">Push</Btn>
                <Btn onClick={checkGate} busy={busy === 'gate'}>Readiness gate</Btn>
              </div>
            </div>

            {gate && (
              <div className="mb-2 text-[12px] rounded px-3 py-1.5" style={{ background: 'color-mix(in srgb,var(--sem-' + (gate.pass ? 'good' : 'bad') + ') 12%, transparent)' }}>
                {gate.checkLine || ((gate.pass ? 'Checks on this change: nothing to fix.' : 'Checks on this change: ' + (gate.blockers || []).join(' ')) + (gate.olderWarnings ? ' The model has ' + gate.olderWarnings + ' older warning' + (gate.olderWarnings === 1 ? '' : 's') + ' this change did not cause. See them on AI Readiness.' : ''))}
              </div>
            )}
            {/* Interview advisory (present only when the model has a saved question pack). Informational by
                contract: it NEVER blocks the gate, so it renders as plain guidance, red text only when a
                question now comes back confidently wrong. */}
            {gate?.interview && (
              <div className="mb-2 text-[11px]" style={{ color: gate.interview.wrong > 0 ? 'var(--sem-bad)' : 'var(--sem-muted)' }}
                title={gate.interview.note}>
                Saved questions re-checked (informational, never blocks): {gate.interview.right} right
                {gate.interview.wrong > 0 ? `, ${gate.interview.wrong} confidently wrong` : ''}
                {gate.interview.unverified > 0 ? `, ${gate.interview.unverified} could not be checked` : ''}
                {(gate.interview.changes?.length ?? 0) > 0 ? ` · ${gate.interview.changes!.length} changed since last asked` : ''}
              </div>
            )}

            {/* Commit */}
            <div className="flex items-center gap-2">
              <input value={msg} onChange={(e) => setMsg(e.target.value)} placeholder="commit message…" className="flex-1"
                style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '4px 8px', fontSize: 12 }} />
              <Btn primary onClick={commit} busy={busy === 'commit'} disabled={!dirty}>Commit</Btn>
            </div>
            <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>Commit saves the open model's unsaved edits to disk first, then commits the changed model files.</div>
          </>
        )}
      </Panel>}

      {/* ── DEPLOYMENT PIPELINE (Fabric — read-only discovery this phase) ───────────── */}
      {mode === 'promote' && <Panel title="Promote" sub="Move a model between development, test and production stages.">
        <div className="mb-2">
          <AccountPicker ref={modePickerRef} mode={signInMode} onMode={setPickedMode}
            tenantId={promoteTenant} onTenantId={setPromoteTenant}
            hint={signingIn === 'promote' ? 'Signing you in…' : undefined} />
        </div>
        <AccountProblem error={pipeErr} testId="promote" onSignIn={signInAndRetry} onChooseAccount={() => modePickerRef.current?.focus()} />
        {pipelines === null ? (
          <>
            <div className="text-[12px] mb-2 max-w-[74ch]" style={{ color: 'var(--sem-muted)' }}>
              <p className="m-0">A pipeline is a set of stages a model moves through, usually development, then test, then production. Promoting copies the model from one stage to the next so the same work is tested before people rely on it.</p>
              <p className="m-0 mt-1.5">Nothing is listed yet because this page has not looked for your pipelines. It signs you in the way you picked above and only reads. Nothing is promoted until you confirm on the next page.</p>
              <p className="m-0 mt-1.5">If the list comes back empty, either you have no pipelines or your account cannot see them. Someone who administers your workspace can add you.</p>
            </div>
            <Btn onClick={() => loadPipelines()} busy={busy === 'pipelines'}>Find my pipelines</Btn>
          </>
        ) : pipelines.length === 0 ? (
          <div className="text-[12px] max-w-[74ch]" style={{ color: 'var(--sem-muted)' }}>No pipelines this account can see. Either none have been set up, or this account has not been added to them. Try a different account above, or ask whoever administers your workspace, then <button className="underline" onClick={() => loadPipelines()}>look again</button>.</div>
        ) : (
          <>
            <div className="flex flex-wrap gap-1.5 mb-2">
              {pipelines.map((p) => (
                <button key={p.id} onClick={() => selectPipeline(p.id)} title={p.description}
                  className="text-[12px] px-2 py-0.5 rounded"
                  style={{ background: selPipe === p.id ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: selPipe === p.id ? 'var(--sem-on-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
                  {p.displayName}
                </button>
              ))}
              <button className="text-[11px] px-2 py-0.5 rounded" onClick={() => loadPipelines()} title="Look for pipelines again" style={{ color: 'var(--sem-muted)' }}>↻</button>
            </div>
            {selPipe && (stages[selPipe] === undefined ? (
              <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading stages…</div>
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
              <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>This pipeline has no stages you can see.</div>
            ))}

            {/* Promote: preview (dry-run) + the gated Deploy */}
            {selPipe && (stages[selPipe]?.length ?? 0) >= 2 && (
              <div className="mt-3 pt-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
                <div className="flex items-center gap-2 text-[12px] flex-wrap">
                  <span style={{ color: 'var(--sem-muted)' }}>Promote</span>
                  <StageSelect stages={stages[selPipe]} value={srcSel} onChange={(v) => { setSrcSel(v); setReport(null); }} />
                  <span style={{ color: 'var(--sem-accent)' }}>→</span>
                  <StageSelect stages={stages[selPipe]} value={tgtSel} onChange={(v) => { setTgtSel(v); setReport(null); }} />
                  <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="note (optional)"
                    style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 6px', fontSize: 11, width: 130 }} />
                  <Btn onClick={doPreview} busy={busy === 'preview'} disabled={srcSel === tgtSel}>Preview</Btn>
                  <Btn onClick={doDeploy} busy={busy === 'deploy'} disabled={!canDeploy} title="confirm-gated live deploy">Deploy</Btn>
                  <Btn onClick={loadHistory} busy={busy === 'history'}>History</Btn>
                </div>
                {report && <DeployResult r={report} override={override} setOverride={setOverride} overrideReason={overrideReason} setOverrideReason={setOverrideReason} />}
                {history && (
                  <div className="mt-2 rounded text-[11px]" style={{ border: '1px solid var(--sem-border)', maxHeight: 150, overflow: 'auto' }}>
                    {history.length === 0 ? <div className="px-2 py-1" style={{ color: 'var(--sem-muted)' }}>No deployments yet.</div> : history.map((h) => (
                      <div key={h.id} className="flex items-center gap-2 px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)' }}>
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

            <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>
              Note: a <b>production</b> promotion needs a human confirm token (an agent can't do it); stage reads need an Admin pipeline role. Deployment <i>rules</i> are portal-only (no REST).
            </div>
          </>
        )}
      </Panel>}

      {/* ── FABRIC GIT (workspace ⇄ git) ───────────────────────────── */}
      {mode === 'advanced' && advancedOpen && advancedView === 'delivery' && <Panel title="Fabric Git" sub="sync a workspace ⇄ git (Azure DevOps / GitHub)">
        <div className="mb-2">
          <AccountPicker ref={fgPickerRef} mode={fgSignInMode} onMode={setFgPicked}
            tenantId={fgTenant} onTenantId={setFgTenant}
            hint={signingIn === 'fabricgit' ? 'Signing you in…' : undefined} />
        </div>
        <div className="flex items-center gap-2 mb-2 flex-wrap">
          <input value={fgWs} onChange={(e) => fgEditWs(e.target.value)} placeholder="workspace id"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 8px', fontSize: 12, width: 250 }} />
          <Btn onClick={() => fgReadStatus()} busy={busy === 'fgload'}>Status</Btn>
        </div>
        {/* The answer to Status belongs under Status, where the connection and the change list also land.
            Above the box would push the id a person just typed down the page every time a read failed. */}
        <AccountProblem error={fgErr} testId="fabricgit" onSignIn={fgSignInAndRetry} onChooseAccount={() => fgPickerRef.current?.focus()} />
        {fgConn && (
          <div className="text-[12px] mb-1 flex items-center gap-2 flex-wrap">
            <Badge color={fgConn.state === 'ConnectedAndInitialized' ? 'var(--sem-good)' : fgConn.state === 'Connected' ? 'var(--sem-warn)' : 'var(--sem-bad)'}>{uiLabel(fgConn.state, 'Unknown')}</Badge>
            {fgConn.providerType && <span style={{ color: 'var(--sem-muted)' }}>{fgConn.providerType} · {fgConn.repository}/{fgConn.branch}{fgConn.directory && fgConn.directory !== '/' ? fgConn.directory : ''}</span>}
            {fgConn.head && <span style={{ color: 'var(--sem-muted)' }}>@ {fgConn.head.slice(0, 8)}</span>}
          </div>
        )}
        {fgStatus && ((fgStatus.changes?.length ?? 0) > 0 ? (
          <div className="rounded text-[12px]" style={{ border: '1px solid var(--sem-border)', maxHeight: 160, overflow: 'auto' }}>
            {fgStatus.conflicts && <div className="px-2 py-1" style={{ color: 'var(--sem-bad)' }}>Conflicts present. Update resolves with PreferRemote by default.</div>}
            {fgStatus.changes.map((c, i) => (
              <div key={c.objectId || i} className="flex items-center gap-2 px-2 py-0.5" style={{ borderBottom: '1px solid var(--sem-border)' }}>
                {c.conflictType === 'Conflict' && <Badge color="var(--sem-bad)">conflict</Badge>}
                {c.workspaceChange && <Badge color="var(--sem-warn)">ws {c.workspaceChange}</Badge>}
                {c.remoteChange && <Badge color="var(--sem-accent)">git {c.remoteChange}</Badge>}
                <span style={{ color: 'var(--sem-muted)' }}>{c.itemType}</span>
                <span className="truncate">{c.displayName}</span>
              </div>
            ))}
          </div>
        ) : <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>In sync: no workspace/git differences.</div>)}

        {/* The destructive writes are a real two-step: a click previews (dry-run), then an explicit Confirm commits.
            Gated on the loaded status belonging to the CURRENT workspace id (editing the id clears it via fgEditWs). */}
        {fgStatus && fgLoadedWs === fgWs.trim() && !fgPending && (
          <div className="flex items-center gap-2 mt-1.5">
            <Btn onClick={() => fgPreview('commit')} busy={busy === 'fgcommit'} title="preview a commit (workspace → git)">Commit</Btn>
            <Btn onClick={() => fgPreview('update')} busy={busy === 'fgupdate'} title="preview an update (git → workspace; overwrites items)">Update</Btn>
          </div>
        )}
        {fgPending && (
          <div className="mt-1.5 rounded p-2 text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-warn)' }}>
            <div style={{ color: 'var(--sem-muted)' }}>{fgPending.plan}</div>
            {fgPending.conflicts && <div style={{ color: 'var(--sem-bad)' }}>Conflicts present. Update resolves with PreferRemote.</div>}
            <div className="flex items-center gap-2 mt-1.5">
              <Btn primary onClick={fgConfirm} busy={busy === 'fgconfirm'} title="run the live write">Confirm {fgPending.action === 'commit' ? 'Commit' : 'Update'}</Btn>
              <button className="text-[11px] underline" onClick={() => setFgPending(null)} style={{ color: 'var(--sem-muted)' }}>Cancel</button>
            </div>
          </div>
        )}
        {fgResult && !fgPending && <div className="text-[11px] mt-1" style={{ color: fgResult.error ? 'var(--sem-bad)' : 'var(--sem-good)' }}>{fgResult.error || fgResult.plan}</div>}
        <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>Commit pushes the workspace to git; Update overwrites workspace items with the git version. Each click previews first (dry-run); a second Confirm runs the live write. Every one of them signs you in the way you picked above.</div>
      </Panel>}

      {/* ── CI·CD · PUBLISH ─────────────────────────────────────────── */}
      {mode === 'advanced' && advancedOpen && advancedView === 'delivery' && <Panel title="CI·CD · Publish" sub="git source-of-truth → workspace (fabric-cicd)">
        {/* Scaffold problems only. They are never sign-in problems: cicd_generate writes files and makes no
            Fabric call, so it has no account to fail on. The publish half keeps its own line, below. */}
        {cicdErr && <div className="text-[12px] mb-2 rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)' }}>{cicdErr}</div>}

        {/* Generate the fabric-cicd CI scaffold (pure file authoring — no live write). */}
        <div className="text-[11px] font-semibold uppercase tracking-wide mb-1" style={{ color: 'var(--sem-muted)' }}>Generate CI</div>
        <div className="flex items-center gap-2 mb-2 flex-wrap text-[12px]">
          <select value={cicdTarget} onChange={(e) => { setCicdTarget(e.target.value as 'github' | 'ado'); setScaffold(null); }}
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 4px', fontSize: 11 }}>
            <option value="github">GitHub Actions</option>
            <option value="ado">Azure DevOps</option>
          </select>
          <input value={cicdWs} onChange={(e) => setCicdWs(e.target.value)} placeholder="workspace id (optional)"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 8px', fontSize: 11, width: 180 }} />
          <input value={cicdEnv} onChange={(e) => setCicdEnv(e.target.value)} placeholder="Environment" title="Replacement key in parameter.yml"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 8px', fontSize: 11, width: 70 }} />
          <Btn onClick={() => cicdGenerate(false)} busy={busy === 'cicdgen'}>Generate</Btn>
          {scaffold && !scaffold.error && <Btn onClick={() => cicdGenerate(true)} busy={busy === 'cicdwrite'} title="write the scaffold into the repo">Write to repo</Btn>}
        </div>
        {scaffold && !scaffold.error && (
          <div className="mb-1">
            {scaffold.written && <div className="text-[11px] mb-1" style={{ color: 'var(--sem-good)' }}>✓ wrote {scaffold.writtenPaths.length} file(s) into the repo.</div>}
            {scaffold.skippedPaths?.length > 0 && <div className="text-[11px] mb-1" style={{ color: 'var(--sem-warn)' }}>skipped {scaffold.skippedPaths.length} existing file(s) (not overwritten): {scaffold.skippedPaths.map((p) => p.split(/[\\/]/).pop()).join(', ')}.</div>}
            <div className="rounded text-[12px]" style={{ border: '1px solid var(--sem-border)' }}>
              {scaffold.files.map((f) => (
                <div key={f.path}>
                  <div className="flex items-center gap-2 px-2 py-1 cursor-pointer" onClick={() => setScaffoldOpen(scaffoldOpen === f.path ? null : f.path)}
                    style={{ borderBottom: '1px solid var(--sem-border)' }}>
                    <span style={{ color: 'var(--sem-accent)' }}>{scaffoldOpen === f.path ? '▾' : '▸'}</span>
                    <span className="font-mono">{f.path}</span>
                  </div>
                  {scaffoldOpen === f.path && (
                    <pre className="text-[11px] m-0 px-3 py-2 whitespace-pre-wrap" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)', maxHeight: 220, overflow: 'auto' }}>{f.content}</pre>
                  )}
                </div>
              ))}
            </div>
            {scaffold.note && <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>{scaffold.note}</div>}
          </div>
        )}

        {/* Publish the OPEN model's on-disk definition to a workspace — a gated, two-step live write. */}
        <div className="text-[11px] font-semibold uppercase tracking-wide mt-3 mb-1" style={{ color: 'var(--sem-muted)' }}>Publish (Items API · full overwrite)</div>
        {/* The picker sits over the publish rows, not over the whole panel: Generate CI above only writes
            files on this computer and never signs anyone in, so a control there would promise otherwise. */}
        <div className="mb-2">
          <AccountPicker ref={pubPickerRef} mode={pubSignInMode} onMode={setPubPicked}
            tenantId={pubTenant} onTenantId={setPubTenant}
            hint={signingIn === 'cicd' ? 'Signing you in…' : undefined} />
        </div>
        <div className="flex items-center gap-2 mb-1 flex-wrap">
          <input value={pubWs} onChange={(e) => { setPubWs(e.target.value); setPubPending(null); setPubResult(null); setPubErr(null); }} placeholder="workspace id"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 8px', fontSize: 12, width: 200 }} />
          <input value={pubItem} onChange={(e) => { setPubItem(e.target.value); setPubPending(null); setPubResult(null); setPubErr(null); }} placeholder="semantic-model item id"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 8px', fontSize: 12, width: 200 }} />
          <Btn onClick={() => pubPreview()} busy={busy === 'pubprev'}>Plan</Btn>
        </div>
        <AccountProblem error={pubErr} testId="cicd" onSignIn={pubSignInAndRetry} onChooseAccount={() => pubPickerRef.current?.focus()} />
        {pubPending && (
          <div className="mt-1 rounded p-2 text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-warn)' }}>
            <div style={{ color: 'var(--sem-muted)' }}>{pubPending.plan}</div>
            {pubPending.sampleParts?.length > 0 && (
              <div className="text-[11px] mt-1 font-mono" style={{ color: 'var(--sem-muted)' }}>{pubPending.partCount} part(s): {pubPending.sampleParts.join(', ')}{pubPending.partCount > pubPending.sampleParts.length ? ' …' : ''}</div>
            )}
            <div className="flex items-center gap-2 mt-1.5">
              <Btn primary onClick={pubConfirm} busy={busy === 'pubconf'} disabled={!pubWs.trim() || !pubItem.trim()} title="overwrite the target model's definition">Confirm Publish</Btn>
              <button className="text-[11px] underline" onClick={() => setPubPending(null)} style={{ color: 'var(--sem-muted)' }}>Cancel</button>
            </div>
          </div>
        )}
        {pubResult && !pubPending && <div className="text-[11px] mt-1" style={{ color: pubResult.error ? 'var(--sem-bad)' : 'var(--sem-good)' }}>{pubResult.error || pubResult.plan}</div>}
        <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>
          Publish reads the open model's on-disk PBIP/TMDL folder and overwrites the target model via the Fabric Items API. It is a confirm-gated live write: an agent can preview, but only a person publishes.
        </div>
      </Panel>}
    </div>
  );
}

// ---- styled atoms (match the other tabs' --sem-* + Tailwind) ----
function relativeTime(utc: string): string {
  const ms = Date.now() - Date.parse(utc);
  if (!Number.isFinite(ms)) return 'date unavailable';
  const mins = Math.max(0, Math.floor(ms / 60000));
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60); if (hours < 48) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}
function shortLiveOnlyName(ref: string): string {
  const cut = ref.indexOf(' (live-only');
  const r = cut >= 0 ? ref.slice(0, cut) : ref;
  const colon = r.indexOf(':');
  const rest = colon >= 0 ? r.slice(colon + 1) : r;
  const slash = rest.lastIndexOf('/');
  return slash >= 0 ? rest.slice(slash + 1) : rest;
}
function PublishConfirm({ preview, busy, error, targetName, endpoint, account, needReason, reason, onReason, onConfirm, onCancel, gate, deleteRefs, onToggleDelete }: {
  preview: LivePublishReport | null; busy: boolean; error: string | null; targetName: string; endpoint: string;
  account?: string | null; needReason: boolean; reason: string; onReason: (v: string) => void;
  onConfirm: () => void; onCancel: () => void; gate?: DeployGate | null;
  deleteRefs: string[]; onToggleDelete: (ref: string) => void;
}) {
  const dest = targetName || preview?.database || 'the live model';
  const changes = preview?.changes ?? [];
  const shown = changes.slice(0, 12);
  const more = Math.max(0, (preview?.totalChanges ?? 0) - shown.length);
  const liveOnly = preview?.liveOnly ?? [];
  const count = preview?.totalChanges ?? 0;
  const empty = !!preview && count === 0 && deleteRefs.length === 0;
  const what = error && !preview ? previewFailedCopy(dest, error)
    : busy && !preview ? checkingCopy(dest)
    : count === 0 ? nothingToPublishCopy(dest, liveOnly)
    : null;
  return (
    <div className="rounded-lg overflow-hidden" data-testid="publish-confirm" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-baseline gap-2 px-4 py-3" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <h3 className="text-[14px] font-semibold m-0">Publish to {dest}</h3>
        <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Nothing is written until you press the button.</span>
      </div>
      <ConfirmRow k="What">{what ? <div>{what}</div> : (
        <div><b>{count === 1 ? '1 change' : `${count} changes`} to publish</b>
          {busy && !preview ? <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Checking for changes…</div> : null}
          {shown.length > 0 && <ul className="mt-1 mb-0 pl-4">{shown.map((c) => <li key={c}>{c}</li>)}</ul>}
          {more > 0 && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>and {more} more</div>}
        </div>
      )}</ConfirmRow>
      {liveOnly.length > 0 && <ConfirmRow k="Extra on live">
        <div>
          <div className="text-[12px] mb-1" style={{ color: 'var(--sem-muted)' }}>Nothing is removed unless you tick it.</div>
          {liveOnly.map((ref) => (
            <label key={ref} className="flex items-center gap-2 text-[12px] py-0.5">
              <input type="checkbox" checked={deleteRefs.includes(ref)} onChange={() => onToggleDelete(ref)} />
              <span>{shortLiveOnlyName(ref)}</span>
            </label>
          ))}
        </div>
      </ConfirmRow>}
      <ConfirmRow k="Where"><div>{dest}{endpoint && <div className="text-[11px] font-mono" style={{ color: 'var(--sem-muted)' }}>{endpoint}</div>}</div></ConfirmRow>
      <ConfirmRow k="As"><div data-testid="publish-account">{publishAccountLine(account)}</div></ConfirmRow>
      <ConfirmRow k="Checks">
        <div data-testid="publish-checks" style={{ color: gate && !gate.pass ? 'var(--sem-bad)' : 'var(--sem-fg)' }}>
          {busy && !gate ? 'Checking this change…' : (gate?.checkLine || 'Checks on this change: nothing to fix.')}
        </div>
      </ConfirmRow>
      {error && <div className="px-4 py-2 text-[12px]" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb,var(--sem-bad) 10%, transparent)' }}>{error}</div>}
      {needReason && <div className="px-4 py-2">
        <input value={reason} onChange={(e) => onReason(e.target.value)} placeholder="Why publish anyway?" className="w-full"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '4px 8px', fontSize: 12 }} />
      </div>}
      <div className="flex items-center gap-3 px-4 pt-3 pb-1">
        <Btn primary onClick={onConfirm} busy={busy} disabled={empty || !preview?.confirmToken || (needReason && !reason.trim())}>
          {needReason ? 'Publish anyway with a reason' : publishButtonLabel(count, dest, deleteRefs.length)}
        </Btn>
        <button type="button" className="text-[12px] underline" style={{ color: 'var(--sem-muted)', background: 'none', border: 0 }} onClick={onCancel}>Cancel</button>
      </div>
      <div className="px-4 pb-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Publishing changes the model's design, not its data. No data refresh.</div>
    </div>
  );
}
function ConfirmRow({ k, children }: { k: string; children: ReactNode }) {
  return (
    <div className="grid gap-2 px-4 py-2" style={{ gridTemplateColumns: '120px 1fr', borderBottom: '1px solid var(--sem-border)' }}>
      <div className="text-[11px] uppercase tracking-wide pt-0.5" style={{ color: 'var(--sem-muted)' }}>{k}</div>
      <div className="text-[13px]">{children}</div>
    </div>
  );
}
// Segmented, not filled: the mode row only switches what you are looking at, so it must not compete with the one
// filled control that actually writes.
function ModeBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return <button type="button" onClick={onClick} aria-pressed={active} className="sem-btn">{children}</button>;
}
function RollbackPanel({ points, selectedId, onSelect, preview, result, resultNote, error, busy, onPreview, onConfirm, onReload }: {
  points: RestorePointRecord[]; selectedId: string; onSelect: (id: string) => void; preview: RollbackResult | null;
  result: RollbackResult | null; resultNote?: string | null; error: string | null; busy: 'load' | 'preview' | 'confirm' | null;
  onPreview: () => void; onConfirm: () => void; onReload: () => void;
}) {
  const point = points.find((p) => p.id === selectedId);
  const canConfirm = !!preview && !preview.error && (preview.restored > 0 || preview.removed > 0);
  return (
    <div className="rounded-lg p-3" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-baseline gap-2 mb-2">
        <span className="text-[13px] font-semibold">Roll back</span>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Restores the published model to a saved point. Your local copy does not change; the comparison updates afterwards.</span>
      </div>
      {error && <div className="text-[12px] mb-2 rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)' }}>{error}</div>}
      {points.length === 0 ? (
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>
          No restore points are saved for this destination yet. Publishing saves one before changing the live model design.
          <button onClick={onReload} className="underline ml-1" disabled={busy === 'load'}>Refresh</button>
        </div>
      ) : (
        <>
          <div className="flex items-center gap-2 flex-wrap">
            <select value={selectedId} onChange={(e) => onSelect(e.target.value)} style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '3px 6px', fontSize: 12, minWidth: 360 }}>
              {points.map((p) => <option key={p.id} value={p.id}>{new Date(p.capturedUtc).toLocaleString()} · {p.database || p.endpoint} · {p.reason || p.op || 'pre-push snapshot'}</option>)}
            </select>
            <Btn onClick={onPreview} busy={busy === 'preview'} disabled={!selectedId || point?.bimPath === null}>Preview rollback</Btn>
            <button onClick={onReload} className="text-[11px] underline" style={{ color: 'var(--sem-muted)' }} disabled={busy === 'load'}>Refresh</button>
          </div>
          {point && <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>Snapshot for {point.database || point.endpoint}: {point.pushed?.length ?? 0} pushed, {point.deleted?.length ?? 0} deleted by the guarded write.</div>}
        </>
      )}
      {preview && !preview.error && (
        <div className="mt-3 rounded p-3 text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-warn)' }}>
          <div className="font-medium">Preview: restore {preview.restored}, remove {preview.removed}, unresolved {preview.failedRefs?.length ?? 0}</div>
          {preview.removedRefs?.length > 0 && <RefList label="Will remove" refs={preview.removedRefs} color="var(--sem-bad)" />}
          {preview.restoredRefs?.length > 0 && <RefList label="Will restore" refs={preview.restoredRefs} color="var(--sem-good)" />}
          {preview.failedRefs?.length > 0 && <RefList label="Will not touch" refs={preview.failedRefs} color="var(--sem-warn)" />}
          {preview.note && <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>{preview.note}</div>}
          <div className="flex items-center gap-2 mt-2">
            <Btn primary onClick={onConfirm} busy={busy === 'confirm'} disabled={!canConfirm}>Confirm rollback</Btn>
            <span className="text-[11px]" style={{ color: 'var(--sem-bad)' }}>Anything listed under Will remove is deleted from the live target.</span>
          </div>
        </div>
      )}
      {result && !preview && <div className="mt-2 text-[12px] rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-' + (result.error ? 'bad' : !result.applied ? 'muted' : (result.failedRefs?.length ?? 0) > 0 ? 'warn' : 'good') + ') 12%, transparent)', color: result.error ? 'var(--sem-bad)' : !result.applied ? 'var(--sem-fg)' : (result.failedRefs?.length ?? 0) > 0 ? 'var(--sem-warn)' : 'var(--sem-good)' }}>{result.error || resultNote || result.note || `Restored ${result.restored}, removed ${result.removed}.`}</div>}
    </div>
  );
}
function RefList({ label, refs, color }: { label: string; refs: string[]; color: string }) {
  return <div className="mt-1"><span className="font-medium" style={{ color }}>{label} ({refs.length})</span><div className="font-mono text-[11px] mt-0.5 max-h-24 overflow-auto" style={{ color: 'var(--sem-muted)' }}>{refs.map((r) => <div key={r}>{r}</div>)}</div></div>;
}
function Panel({ title, sub, children }: { title: string; sub?: string; children: React.ReactNode }) {
  return (
    <div className="m-3 rounded-lg p-3" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-baseline gap-2 mb-2">
        <span className="text-[13px] font-semibold">{title}</span>
        {sub && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{sub}</span>}
      </div>
      {children}
    </div>
  );
}
function Btn({ children, onClick, primary, busy, disabled, title }: { children: React.ReactNode; onClick: () => void; primary?: boolean; busy?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={busy || disabled} title={title} className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {busy ? '…' : children}
    </button>
  );
}
// A failed Fabric call, in words, with the one thing that fixes it. Shared by Promote, Fabric Git and the
// CI·CD publish, which is why it is no longer called PromoteProblem.
//
// All three used to print whatever came back from the sign-in straight into a red line. What Kane read on
// his own laptop was "Please run 'az login' to set up account", once above the paragraph explaining what a
// pipeline is and again under the Fabric Git workspace box, with no control that could act on it. The plain
// line comes from the shared map in copy.ts; the raw text survives only for a failure the map does not
// recognise, because a person with an unknown failure still needs something to pass on.
function AccountProblem({ error, testId, onSignIn, onChooseAccount }: {
  error: string | null; testId: string; onSignIn: () => void; onChooseAccount: () => void;
}) {
  if (!error) return null;
  const problem = signInProblem(error);
  const act = problem.fix === 'signIn' ? onSignIn : problem.fix === 'chooseAccount' ? onChooseAccount : null;
  return (
    <div className="text-[12px] mb-2 rounded px-2 py-2 max-w-[74ch]" data-testid={`${testId}-problem`}
      style={{ background: 'color-mix(in srgb,var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)' }}>
      <div>{problem.lead}</div>
      {problem.detail && <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>{problem.detail}</div>}
      {act && problem.fixLabel && (
        <div className="mt-2" data-testid={`${testId}-signin-fix`}>
          <Btn onClick={act}>{problem.fixLabel}</Btn>
        </div>
      )}
    </div>
  );
}
function Chip({ children }: { children: React.ReactNode }) {
  return <span className="text-[11px] px-2 py-0.5 rounded-full" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{children}</span>;
}
function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="text-[9px] px-1 rounded align-middle" style={{ background: color, color: 'var(--sem-on-accent)' }}>{children}</span>;
}
function Banner({ tone, children }: { tone: 'bad' | 'good' | 'warn'; children: React.ReactNode }) {
  const c = tone === 'bad' ? 'var(--sem-bad)' : tone === 'warn' ? 'var(--sem-warn)' : 'var(--sem-good)';
  return <div className="mx-3 mt-3 rounded px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + c + ' 14%, transparent)', color: c }}>{children}</div>;
}
function StageSelect({ stages, value, onChange }: { stages: PipelineStage[]; value: string; onChange: (v: string) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)}
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 4px', fontSize: 11 }}>
      {stages.map((s) => <option key={s.id} value={s.id}>{s.displayName}</option>)}
    </select>
  );
}

// The preview / deploy result: New/Update counts, the readiness gate, the prod confirm token, the item diff, status.
function DeployResult({ r, override, setOverride, overrideReason, setOverrideReason }: { r: DeployStageReport; override: boolean; setOverride: (b: boolean) => void; overrideReason: string; setOverrideReason: (s: string) => void }) {
  const gateBad = r.gate && !r.gate.pass;
  return (
    <div className="mt-2 rounded p-2 text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="font-medium">{r.sourceStageName} → {r.targetStageName}{r.targetIsProd ? ' (prod)' : ''}</span>
        <span style={{ color: 'var(--sem-good)' }}>+{r.newCount} new</span>
        <span style={{ color: 'var(--sem-warn)' }}>~{r.updateCount} update</span>
        {r.gate && <Badge color={r.gate.pass ? 'var(--sem-good)' : 'var(--sem-bad)'}>gate {r.gate.pass ? 'pass' : 'blocked'}</Badge>}
        {r.committed && <Badge color="var(--sem-good)">deployed ✓</Badge>}
        {r.status && !r.committed && <Badge color="var(--sem-warn)">{uiLabel(r.status)}</Badge>}
      </div>
      {r.error && <div className="mt-1" style={{ color: 'var(--sem-bad)' }}>{r.error}</div>}
      {r.plan && !r.error && <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>{r.plan}</div>}
      {r.targetIsProd && r.confirmToken && !r.committed && (
        <div className="mt-1" style={{ color: 'var(--sem-warn)' }}>Production: confirm token <span className="font-mono">{r.confirmToken}</span> will be sent with Deploy.</div>
      )}
      {gateBad && !r.committed && (
        <div className="mt-1 flex items-center gap-2 flex-wrap">
          <label className="flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
            <input type="checkbox" checked={override} onChange={(e) => setOverride(e.target.checked)} /> deploy anyway (override the failing gate)
          </label>
          {/* The engine refuses a bare override — a written reason is required and recorded in the Verified Edits trail. */}
          {override && (
            <input value={overrideReason} onChange={(e) => setOverrideReason(e.target.value)} placeholder="reason (required, recorded)"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid ' + (overrideReason.trim() ? 'var(--sem-border)' : 'var(--sem-warn)'), borderRadius: 4, padding: '2px 6px', fontSize: 11, flex: 1, minWidth: 180 }} />
          )}
        </div>
      )}
      {r.items?.length > 0 && (
        <div className="mt-1.5 rounded" style={{ border: '1px solid var(--sem-border)', maxHeight: 130, overflow: 'auto' }}>
          {r.items.map((it) => (
            <div key={it.itemId} className="flex items-center gap-2 px-2 py-0.5" style={{ borderBottom: '1px solid var(--sem-border)' }}>
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
    <div className="rounded px-3 py-1.5 text-center" style={{ background: 'var(--sem-surface-2)', border: '1px solid ' + (prod ? 'var(--sem-warn)' : 'var(--sem-border)'), minWidth: 96 }}>
      <div className="text-[12px] font-medium">{name}{prod ? ' (prod)' : ''}</div>
      {sub && <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 120 }}>{sub}</div>}
    </div>
  );
}
function Arrow() { return <span style={{ color: 'var(--sem-accent)' }}>→</span>; }
