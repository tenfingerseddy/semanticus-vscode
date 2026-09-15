import { Component, createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, lazy, Suspense, type ErrorInfo } from 'react';
import { createPortal } from 'react-dom';
import { anchorUnder } from './toolrow';
import { publishStageName } from './publishcopy';
import { rpc, onDidChange, onReconnect, onActivity, onNavigate, onPlanChange, onTreeSelection, onOpenConnections, onWorkflowChange, onStudioZoom, onEngineState, postStudioZoom, signalReady, selectInProperties, focusSelectInProperties, focusModelTree, runHostCommand, copyText, loadState, saveState, type ChangeNotification } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';
import { ShortcutsOverlay, tabForKey, isTypingTarget, hostCommandForKey } from './shortcuts';
import { ActivityProvider, LiveActivity, ClaudeRanBanner, useClaudeReflection, KIND_TAB, type ActivityEvent } from './activity';
import { useFixState } from './hooks';
import { useConnection, ConnectBar, type SessionInfo } from './connection';
import { ContextBar, compareSeedFromSession, QueryStalenessChip } from './contextbar';
import { ConnectionsHub, openConnectionsOnView, type HubView } from './connectionshub';
import type { ModelRef } from './compare';
import { VpaqComponentBars, VPAQ_COMPONENT_COLORS, VPAQ_UNATTRIBUTED_COLOR, Sparkline, type VpaqColumn, type VpaqTable, type VpaqBarItem } from './echart';
import { DiagramView } from './diagram';
import type { ModelGraph } from './diagram';
import { AdvancedModelsView } from './advmodels';
import { GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { useFeature, useLockedTools, ProBadge, PENDING_ACCESS } from './pro';
import { featureOfTool } from './features';
import { ProPreview } from './propreview';
import { DaxModelProvider } from './daxeditor';
import { DaxLabTabStateProvider, DaxLabView, useDaxLabBusy } from './daxlab';
import { LineageTabStateProvider, LineageView, useLineageReportsBusy } from './lineage';
import { SearchView } from './search';
import { DataPreviewView } from './datapreview';
import { BpaView } from './bpa';
import { OptimizeView } from './optimize';
import { SpecView } from './spec';
import { DocumentationView } from './documentation';
import { DeployView, usePublishReach } from './deploy';
import { EvidenceView, TestsView } from './tests';
import { DataAgentView } from './dataagent';
import { HelpButton, PageNotesButton } from './help';
import { HistoryView, type EditEntry } from './history';
import { WorkflowsView, type WorkflowRunView } from './workflows';
import { emptyRuns, reduceRuns, liveRuns, mostRecentLive, runById, isRunNotFound, type RunMapState, type FoldOpts } from './workflowruns.mjs';
import { anchorOf, normalizeStorageMode, snapKey, scanMatchesAnchor, scanUsableDuringTransition, storageEvidenceLevel, relationshipAllowsStorageEdits, relationshipAllowsReverifiedDeletePlan, compareDecision, shouldStoreAsLast, resolvedClaimAllowed, introducedClaimAllowed, refIsAmbiguous, identifierLike, type StorageMode, type StorageEvidenceLevel } from './storagecalc.mjs';
import { busyAffordance } from './tabbusy.mjs';
import { KnowledgeView } from './knowledge';
import { PermissionsView, permissionPresetDescription, publishPermissionDescription, type AgentPolicy } from './permissions';
import { InterviewCard, InterviewSummaryChip } from './interview';
import { CustomRulesPanel } from './rulesauthor';
import { ModelHome } from './modelhome';
import { DESTINATION_OF_TOOL, areaHomeTool, objectLabel, objectToRef, refToObject, tableOfObject, type Area, type Route, type Seed, type ToolId } from './route';

// M Code is the heaviest tab (the @microsoft/powerquery-* parser/formatter/language-services + the 866-symbol
// M standard-library dataset — ~1.7 MB). Lazy-load it so that whole cluster lands in its own chunk and only
// downloads when the user opens the tab, keeping the rest of Studio's startup lean.
const MCodeView = lazy(() => import('./mcode').then((m) => ({ default: m.MCodeView })));

// ---- wire types (camelCase, from the engine) -------------------------------------------------
interface CategoryScore { category: string; score: number; weight: number; applicable: number; violations: number; waived?: number; hasRules: boolean; }
interface Finding { ruleId: string; ruleTitle: string; category: string; severity: string; fix: string; objectRef: string; objectName: string; message: string; displayMessage?: string; custom?: boolean; waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean; }
interface Scorecard {
  overall: number; rawOverall: number; grade: string; gatedBy: string[];
  categories: CategoryScore[]; coverage: Record<string, number>; findings: Finding[]; safeFixCount: number; waivedCount?: number;
  caveat?: string;   // set when the score may be incomplete (e.g. Direct Lake live cardinality is resident-only)
  ruleErrors?: string[];   // custom-rule problems surfaced loudly (unparseable annotation, an eval error)
}
interface ApprovalNotice { id: string; grantedUtc?: string | null; }
// SessionInfo now lives in connection.tsx (the single shared shape, mirroring Protocol.cs) — imported above.

const GRADE_COLOR: Record<string, string> = { A: 'var(--sem-good)', B: 'var(--sem-good)', C: 'var(--sem-warn)', D: 'var(--sem-warn)', F: 'var(--sem-bad)' };
const FIX_STYLE: Record<string, { label: string; color: string }> = {
  SafeFix: { label: 'Safe fix', color: 'var(--sem-good)' },
  AiContent: { label: 'AI', color: 'var(--sem-accent)' },
  Proposal: { label: 'Review', color: 'var(--sem-muted)' },
  None: { label: 'Info', color: 'var(--sem-muted)' },
};

type StudioTab = ToolId;

// Stable tool ids remain the compatibility API. The five buttons describe where people start, not every
// capability reachable from there. A utility destination hosts Settings without becoming a sixth area.
const TAB_GROUPS: { id: Area; label: string; tabs: { id: StudioTab; label: string }[] }[] = [
  { id: 'model', label: 'Model', tabs: [
    { id: 'modelhome', label: 'Overview' }, { id: 'diagram', label: 'Diagram' }, { id: 'lineage', label: 'Lineage' },
    { id: 'search', label: 'Find and replace' }, { id: 'data', label: 'Data' }, { id: 'stats', label: 'Size by table' },
    { id: 'spec', label: 'Model Spec' }, { id: 'advmodels', label: 'Advanced Modelling' }, { id: 'mcode', label: 'Power Query' },
    { id: 'docs', label: 'Docs' }, { id: 'knowledge', label: 'Model notes' },
  ] },
  { id: 'calc', label: 'Calculations', tabs: [{ id: 'daxlab', label: 'DAX Lab' }] },
  { id: 'checks', label: 'Checks', tabs: [{ id: 'tests', label: 'Tests' }, { id: 'bpa', label: 'Model quality' }, { id: 'readiness', label: 'AI understanding' }, { id: 'evidence', label: 'Saved reports' }] },
  { id: 'changes', label: 'Changes', tabs: [{ id: 'optimize', label: 'Proposed' }, { id: 'history', label: 'History' }, { id: 'deploy', label: 'Published' }] },
  { id: 'workflows', label: 'Workflows', tabs: [{ id: 'workflows', label: 'Workflows' }] },
];
// The Model area's segmented row shows only these six as their own segment, and folds the five authoring tools
// into one "Create" dropdown segment, so eleven equal buttons don't crowd the header. TAB_GROUPS itself stays the
// full eleven-tool list: Find a tool, keyboard cycling and the tab→area map all still need every id. Size by table
// joined the primary six because it was in no segment and no menu: a table row or Find a tool were the only ways
// in, and it then appeared as a segment that had not existed a moment earlier. A Model tool in neither list still
// gets a segment of its own while it is the open tool, so the row is never left with nothing marked.
const MODEL_PRIMARY_TAB_IDS: StudioTab[] = ['modelhome', 'diagram', 'lineage', 'search', 'data', 'stats'];
const MODEL_CREATE_TAB_IDS: StudioTab[] = ['spec', 'advmodels', 'mcode', 'docs', 'knowledge'];
const TAB_TO_GROUP: Record<string, Area> = Object.fromEntries(TAB_GROUPS.flatMap((g) => g.tabs.map((t) => [t.id, g.id])));
TAB_TO_GROUP.compare = 'changes'; TAB_TO_GROUP.dataagent = 'changes';
const LEGACY_TABS: StudioTab[] = ['compare', 'dataagent'];
const VALID_TABS = new Set<string>([...TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id)), ...LEGACY_TABS, 'permissions']);
const CYCLE_TABS: StudioTab[] = TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id));
const FALLBACK_TAB: StudioTab = 'modelhome';
const AREA_ALIASES: Record<string, Area> = { understand: 'model', change: 'changes', improve: 'checks', prove: 'checks', ship: 'changes', model: 'model', calc: 'calc', checks: 'checks', changes: 'changes', workflows: 'workflows' };
// The exact sentence the host relay answers with when it holds no engine connection (extension.ts, the Studio and
// Connections message handlers). Matched, not guessed: it is the only evidence a panel that mounted while the engine
// was already down ever receives. engine-restart-recovery.test.mjs keeps the two literals identical.
const ENGINE_NOT_CONNECTED = 'Engine not connected.';
export function App() {
  const [session, setSession] = useState<SessionInfo | null>(null);
  const sessionRef = useRef<SessionInfo | null>(null);
  sessionRef.current = session;
  // No engine at all. Distinct from "no model open": with no engine the Open a model placeholder is a dead end,
  // because its button needs the engine too. That is what Kane hit on 2026-09-15 after a restart failed.
  const [engineDown, setEngineDown] = useState(false);
  // The live connection context is the single source of truth for the attached query engine (its session copy is
  // refreshed on attach/disconnect, which App's own `session` above is not) — the Compare seed reads BOTH from here.
  const { session: liveSession, conn, context: connectionContext, connectionsOpen, openConnections, closeConnections } = useConnection();
  const [card, setCard] = useState<Scorecard | null>(null);
  const [busy, setBusy] = useState(false);
  const [activity, setActivity] = useState<EditEntry[]>([]);
  // How many of the NEWEST timeline entries are currently undone (the redo branch). Driven by undo/redo broadcasts,
  // so it stays correct no matter which door (you or the AI) drove the step. items[0..undoneCount-1] are undone.
  const [undoneCount, setUndoneCount] = useState(0);
  const undoneCountRef = useRef(0); undoneCountRef.current = undoneCount;
  const activityLenRef = useRef(0); activityLenRef.current = activity.length;
  const [trend, setTrend] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [route, setRoute] = useState<Route>({ sessionId: '', destination: 'model', tool: 'modelhome', nonce: 0 });
  const routeRef = useRef(route); routeRef.current = route;
  // THERE IS NO BACK STACK. Kane retired the Back button on 2026-09-14 after hitting it himself: "The back button
  // doesnt do anything." A stack of places you have been is a browser idea, and Studio is not a browser — the areas
  // and their segment strips are always on screen, so every page is one click away without it. What the stack did
  // buy was a way out of Settings, which is the one page with no segment of its own. That is all this ref is: ONE
  // page to come back to, recorded on the way into Settings.
  const returnTo = useRef<Route | undefined>(undefined);
  const lastInArea = useRef<Partial<Record<Area, Route>>>({});
  // Whether a change plan is open. It is the only thing that decides where the Changes area button lands, so the
  // shell has to know it even while the Proposed tab is unmounted.
  const [hasPlan, setHasPlan] = useState(false);
  const hasPlanRef = useRef(false); hasPlanRef.current = hasPlan;
  const tab = route.tool;
  const [selectedObject, setSelectedObject] = useState<Route['object']>();
  const selectedObjectRef = useRef(selectedObject); selectedObjectRef.current = selectedObject;
  const pendingTreeSelection = useRef<{ sessionId: string; object: Route['object'] } | undefined>(undefined);
  const [agentPolicy, setAgentPolicy] = useState<AgentPolicy | null | undefined>(undefined);
  const [modelChangeNonce, setModelChangeNonce] = useState(0);
  // Workflow RUNS live at the SHELL, above the conditionally-mounted Workflows tab (BLOCKER: a run started by the
  // your assistant while the human is on another tab must not be missed — an unmounted tab has no listener). One
  // always-alive subscription folds every workflow/didChange broadcast into a run-map keyed by runId, and a
  // getWorkflowRun seed catches a run that started before we subscribed. Ownership (you vs your assistant) is
  // tracked by the ids we start ourselves.
  const [runs, setRuns] = useState<RunMapState>(emptyRuns());
  const ownRunIds = useRef<Set<string>>(new Set());
  const runsRef = useRef(runs); runsRef.current = runs;   // latest run map for the reconnect reconciliation pass (below)
  const foldRun = useCallback((r: WorkflowRunView | null | undefined, opts?: FoldOpts) => { if (r) setRuns((s) => reduceRuns(s, r, opts)); }, []);
  // After a pipe RECONNECT on the SAME session (engine alive, sessionId unchanged), the run-tracking effect below
  // does NOT re-run — and broadcasts that fired while disconnected are NOT replayed. A run shown live could have
  // completed unseen, stranding a ghost banner. Reconcile against the engine: read the authoritative live-run set
  // from orientation, refetch every locally-tracked ACTIVE run by id, and fold the results as RECONCILE folds
  // (rank-checked, so a fresh live broadcast that already advanced past the refetch is never rewound). A ghost
  // whose refetch is terminal lands as terminal; one the snapshot omits AND whose refetch is not-found is evicted.
  const reconcileRuns = useCallback((sid: string) => {
    const activeIds = liveRuns(runsRef.current).map((r) => r.runId);
    rpc<{ activeWork?: { workflows?: { runId?: string }[] } }>('getOrientation').then((o) => {
      if (sessionRef.current?.sessionId !== sid) return;   // a model swap raced the reconcile — its effect reseeds
      const liveIds = new Set((o?.activeWork?.workflows ?? []).map((w) => w?.runId).filter((id): id is string => !!id));
      // Evict a ghost ONLY on an authoritative run-not-found AND only when orientation's live set also omits it.
      const evictGhost = (id: string) => { if (!liveIds.has(id)) setRuns((s) => reduceRuns(s, { runId: id } as WorkflowRunView, { reconcile: true, notFound: true, sessionId: sid })); };
      for (const id of activeIds) {
        // Classify each refetch outcome (isRunNotFound handles both shapes, like the evidence door): a real run view
        // folds (rank-checked); an authoritative not-found — a rejection OR a resolved not-found note — evicts. A
        // TRANSPORT failure (pipe drop, timeout) during the refetch of a run that completed while the pipe was down
        // must NOT be read as absence: it would lose the terminal receipt and its Evidence path forever. We leave
        // such an entry untouched for a later reconciliation pass.
        rpc<WorkflowRunView>('getWorkflowRun', id).then(
          (v) => { if (isRunNotFound(v)) evictGhost(id); else foldRun(v, { reconcile: true, sessionId: sid }); },
          (e) => { if (isRunNotFound(e)) evictGhost(id); },
        );
      }
      // A run started DURING the outage that is still live: orientation lists it but we never tracked it — seed it.
      for (const id of liveIds) if (!runById(runsRef.current, id)) rpc<WorkflowRunView>('getWorkflowRun', id).then((v) => foldRun(v, { reconcile: true, sessionId: sid })).catch(() => undefined);
    }).catch(() => undefined);
  }, [foldRun]);
  const [deployRestoreTarget, setDeployRestoreTarget] = useState<{ id: string; endpoint: string; database: string; nonce: number } | null>(null);
  // Context-bar → Compare: seed the differ with what you're EDITING vs what you're QUERYING so a click lands on the
  // exact diff. Nonce so a repeat click re-fires; Compare only adopts a seed it hasn't consumed and never clobbers a
  // comparison the user set up by hand.
  const [compareSeed, setCompareSeed] = useState<{ left: ModelRef; right: ModelRef | null; note?: string; nonce: number } | null>(null);
  const navNonce = useRef(0);
  // Seed the Compare from LIVE connection state (the query engine + the fresh session copy), not App's lagging session,
  // so the click targets the same model the footer names. `right` may be null when the attached dataset can't be named
  // (the seed carries a note the Compare tab surfaces instead of diffing an arbitrary dataset).
  // Fresh copies for the seed path: a COLD status-bar open flushes navigation through onNavigate's first-render
  // closure (stale liveSession/session/conn), and the session may not have loaded yet at all. Read the latest via
  // refs, and if the session isn't ready, DEFER (never a timeout) — the resolver effect below seeds once it lands.
  const sessionForSeedRef = useRef<SessionInfo | null>(null);
  sessionForSeedRef.current = liveSession ?? session;
  const connRef = useRef(conn);
  connRef.current = conn;
  const connectionContextRef = useRef(connectionContext);
  connectionContextRef.current = connectionContext;
  const [seedPending, setSeedPending] = useState(false);
  const jumpToCompare = () => {
    goTab('deploy');
    const s = sessionForSeedRef.current;
    if (s?.sessionId) { const seed = compareSeedFromSession(s, connRef.current, connectionContextRef.current); setCompareSeed({ ...seed, nonce: ++navNonce.current }); setSeedPending(false); }
    else setSeedPending(true);   // session not ready — the resolver effect applies the seed when it arrives
  };
  // Apply a DEFERRED Compare seed once the session finally lands (the cold status-bar-open race). We wait for a
  // definite session (either the live copy or App's), never a timer. If it resolves with NO model open we still
  // can't seed — surface that in the Compare tab (a note) rather than drop the seed silently.
  useEffect(() => {
    if (!seedPending) return;
    const s = liveSession ?? session;
    if (!s) return;   // still loading — re-run when a session copy arrives
    setSeedPending(false);
    if (s.sessionId) { const seed = compareSeedFromSession(s, connRef.current, connectionContextRef.current); setCompareSeed({ ...seed, nonce: ++navNonce.current }); }
    else setCompareSeed({ left: { kind: 'session', label: 'working copy' }, right: null, nonce: ++navNonce.current,
      note: 'No model is open, so there is nothing to compare against what you are querying. Open a model, then try again.' });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seedPending, liveSession, session]);
  const [unseen, setUnseen] = useState<Set<string>>(new Set());   // tabs with new Claude activity (badge)
  const [pendingApprovals, setPendingApprovals] = useState<ApprovalNotice[]>([]);
  const pendingApprovalCount = pendingApprovals.length;
  const tabRef = useRef(tab); tabRef.current = tab;
  const rescanTimer = useRef<number | undefined>(undefined);

  const refreshPendingApprovals = useCallback(() => rpc<ApprovalNotice[]>('listPendingApprovals')
    .then((items) => setPendingApprovals((items ?? []).filter((x) => !x.grantedUtc))), []);

  // Approval requests can arrive through the MCP door while the human is anywhere in Studio. Poll at the shell
  // level so the callout is not trapped inside the Permissions tab that the human does not yet know to open.
  // With no model session there can be no actionable request; standing the poll down also avoids reconnect noise.
  useEffect(() => {
    if (!session?.sessionId) { setPendingApprovals([]); return; }
    let cancelled = false;
    const pull = () => rpc<ApprovalNotice[]>('listPendingApprovals')
      .then((items) => { if (!cancelled) setPendingApprovals((items ?? []).filter((x) => !x.grantedUtc)); })
      .catch(() => undefined);   // preserve the last known count through a transient engine reconnect
    void pull();
    const iv = window.setInterval(() => { void pull(); }, 5000);
    return () => { cancelled = true; window.clearInterval(iv); };
  }, [session?.sessionId]);

  // Shell-level workflow-run tracking (BLOCKER 1): keep ONE subscription alive across tab navigation so an
  // agent-driven run advancing while the human is on Diagram (or anywhere) is captured, and seed the map with the
  // current run so opening Workflows never shows "No active run" for a run that started before we subscribed. A
  // model swap resets the map + ownership. getWorkflowRun throws when no run exists — that is the empty case.
  useEffect(() => {
    // A model swap (sessionId change) must not carry the prior model's run map or ownership into the new session —
    // reset BOTH on ANY change, not only when the session goes empty (HIGH 3: an A→B swap kept A's banner/receipts).
    // The map is stamped with THIS session so any in-flight seed/broadcast from a prior model is dropped by the
    // reducer's generation guard even if its response lands after the swap (finding 4).
    const sid = session?.sessionId ?? null;
    setRuns(emptyRuns(sid)); ownRunIds.current = new Set();
    if (!sid) return;
    let alive = true;
    // Seed EVERY live run, not just the most recent: the orientation primer enumerates all active run ids, so an
    // agent with several runs already in flight is fully captured before the first broadcast arrives (HIGH 4). A
    // seed only FILLS a gap — it never overwrites a live broadcast that raced ahead of its snapshot (HIGH 1). Fall
    // back to the single most-recent run if orientation is unavailable. getWorkflowRun throws when a run is gone.
    const seed = (r: WorkflowRunView | null | undefined) => { if (alive) foldRun(r, { seed: true, sessionId: sid }); };
    rpc<{ activeWork?: { workflows?: { runId?: string }[] } }>('getOrientation').then((o) => {
      if (!alive) return;
      const ids = (o?.activeWork?.workflows ?? []).map((w) => w?.runId).filter((id): id is string => !!id);
      if (ids.length === 0) { rpc<WorkflowRunView>('getWorkflowRun').then(seed).catch(() => undefined); return; }
      for (const id of ids) rpc<WorkflowRunView>('getWorkflowRun', id).then(seed).catch(() => undefined);
    }).catch(() => { rpc<WorkflowRunView>('getWorkflowRun').then(seed).catch(() => undefined); });
    // Live broadcasts are ordering-safe folds stamped with this session (never seeds — they carry the freshest view).
    const off = onWorkflowChange((v) => foldRun(v as WorkflowRunView, { sessionId: sid }));
    return () => { alive = false; off(); };
  }, [session?.sessionId, foldRun]);

  // Badge a tab when Claude runs something on it while you're elsewhere; switching to a tab clears its badge.
  useEffect(() => onActivity((e) => {
    // Legacy ids badge their VISIBLE hosts: compare/dataagent render inside Deploy, spec inside
    // Change Plan. Without the remap the badge lands on a tab that no longer exists in the nav.
    const ALIAS: Record<string, string> = { dataagent: 'deploy', compare: 'deploy', spec: 'optimize' };
    const raw = KIND_TAB[e.kind];
    const t = raw ? (ALIAS[raw] ?? raw) : raw;
    const visibleTab = ALIAS[tabRef.current] ?? tabRef.current;
    if (t && t !== visibleTab) setUnseen((s) => { const n = new Set(s); n.add(t); return n; });
  }), []);
  const goRoute = (next: Route) => {
    const sid = sessionRef.current?.sessionId ?? '';
    // A cold host hand-off may arrive just before sessionInfo. Accept its stamped route, but once a current session is
    // known reject every delayed route from another model.
    if (sid && next.sessionId !== sid) return;
    const current = routeRef.current;
    // Settings is a detour, not a place in the app, so record the page that sent you there — and only on the way IN.
    // A second Settings route must not overwrite it, or Done would return you to Settings. The ambient tree selection
    // is folded in here because Overview with a row selected is a different page from bare Overview, and coming back
    // to the wrong one of those is the kind of near-miss that made the old Back feel broken.
    if (next.destination === 'utility' && current.destination !== 'utility') {
      returnTo.current = current.tool === 'modelhome' && !current.object && selectedObjectRef.current
        ? { ...current, object: selectedObjectRef.current }
        : current;
    }
    routeRef.current = next;
    setRoute(next);
  };
  const consumeRouteSeed = useCallback((nonce: number) => {
    setRoute((current) => {
      if (current.nonce !== nonce || !current.seed) return current;
      const next = { ...current, seed: undefined };
      routeRef.current = next;
      return next;
    });
  }, []);
  // Done, the one way out of Settings. It must ALWAYS land somewhere, which is the whole lesson of the button it
  // replaces: the old goBack popped its entry before goRoute could reject it, so a rejected route was consumed and
  // the click did nothing. Two things stop that here. The remembered page is RESTAMPED with the session that is live
  // now, so goRoute's session guard can never drop it — a page recorded before sessionInfo answered carries an empty
  // stamp, and that alone would have been enough to make Done inert. And there is a real fallback when nothing is
  // remembered, rather than silently doing nothing.
  const goDone = () => {
    const remembered = returnTo.current;
    returnTo.current = undefined;
    if (!remembered) { goTab('modelhome'); return; }
    const sid = sessionRef.current?.sessionId ?? '';
    // The seed is dropped: it was an explicit one-use hand-off and has already been acknowledged, so replaying it
    // would re-run a request over work the person has since changed.
    goRoute({ ...remembered, sessionId: sid || remembered.sessionId, seed: undefined, nonce: ++navNonce.current });
  };
  const seedForTarget = (tool: StudioTab, target: string | undefined, object: Route['object'], approvalId?: string): Seed | undefined => {
    if (tool === 'daxlab' && object?.kind === 'measure') return { kind: 'query', value: objectToRef(object) };
    if (tool === 'tests' && object?.kind === 'measure') return { kind: 'test', value: objectToRef(object) };
    if (tool === 'search' && target != null) return { kind: 'search', value: object?.kind === 'table' ? '' : (objectLabel(object) ?? target) };
    if (tool === 'deploy' && target === 'publish') return { kind: 'publish', value: 'publish' };
    if (tool === 'permissions' && approvalId) return { kind: 'approval', value: approvalId };
    if (tool === 'advmodels' && target) return { kind: 'advanced', value: target.startsWith('area:') ? target.slice(5) : target };
    return undefined;
  };
  const goTab = (raw: string, approvalId?: string, target?: string, sessionId?: string, explicitSeed?: Seed) => {
    if (!VALID_TABS.has(raw)) { console.warn(`[Studio] ignoring navigation to unknown tab id '${raw}' (host/webview version skew?)`); return; }
    // Compare is a compatibility alias for Deploy. Data Agent remains a distinct route because its Advanced subview
    // is part of the destination, not a disposable alias detail.
    const t = raw === 'compare' ? 'deploy' : raw as StudioTab;
    setUnseen((s) => { if (!s.has(t)) return s; const n = new Set(s); n.delete(t); return n; });
    const object = refToObject(target);
    const seed = explicitSeed ?? seedForTarget(t, target, object, approvalId);
    goRoute({ sessionId: sessionId ?? sessionRef.current?.sessionId ?? routeRef.current.sessionId, destination: DESTINATION_OF_TOOL[t], tool: t, object, seed, nonce: ++navNonce.current });
  };
  useEffect(() => { if (route.destination !== 'utility' && !route.seed) lastInArea.current[route.destination] = route; }, [route]);
  // A plan can be created or cleared from either door, so read it once per session and then follow the broadcast.
  useEffect(() => {
    const sid = session?.sessionId;
    if (!sid) { setHasPlan(false); return; }
    let live = true;
    rpc<{ planId?: string }>('getPlan').then((p) => { if (live) setHasPlan(!!p?.planId); }).catch(() => { /* no plan is a valid answer */ });
    const off = onPlanChange((v) => setHasPlan(!!(v as { planId?: string } | null)?.planId));
    return () => { live = false; off(); };
  }, [session?.sessionId]);
  const goGroup = (group: string) => {
    const area = AREA_ALIASES[group];
    if (!area) return;
    // The button for the area you are ALREADY in is an exit, so it goes to that area's home page. Area memory is
    // for coming BACK to an area, and using it here handed you the page you were already looking at: with Back
    // retired, that is how Kane had nothing at all to press on Data with a table in hand (2026-09-15).
    const remembered = routeRef.current.destination === area ? undefined : lastInArea.current[area];
    if (remembered) goRoute({ ...remembered, sessionId: sessionRef.current?.sessionId ?? remembered.sessionId, seed: undefined, nonce: ++navNonce.current });
    else goTab(areaHomeTool(area, hasPlanRef.current));
  };
  // A route and every object hand-off belong to one model session. Reset them together on open, close and swap.
  const previousShellSession = useRef<string | undefined>(undefined);
  useEffect(() => {
    const sid = session?.sessionId ?? '';
    if (previousShellSession.current === sid) return;
    const previousSid = previousShellSession.current;
    previousShellSession.current = sid;
    const coldStampedRoute = !!sid && routeRef.current.sessionId === sid;
    // A REAL session change is a different model, and the page you were on belongs to the model you left, so forget
    // it. The session merely BECOMING KNOWN is not that: on a cold open the page that sent you to Settings is
    // recorded before sessionInfo answers, and throwing it away there would make Done land on Overview instead of
    // where you actually were. previousSid is undefined on mount and '' until a model arrives, so both are falsy.
    if (previousSid) returnTo.current = undefined;
    lastInArea.current = {};
    const initialSelection = pendingTreeSelection.current?.sessionId === sid ? pendingTreeSelection.current.object : undefined;
    pendingTreeSelection.current = undefined;
    setSelectedObject(initialSelection); setCompareSeed(null); setDeployRestoreTarget(null); setSeedPending(false);
    setAgentPolicy(undefined); setModelChangeNonce(0);
    if (!coldStampedRoute) {
      const next: Route = { sessionId: sid, destination: 'model', tool: 'modelhome', object: initialSelection, nonce: ++navNonce.current };
      routeRef.current = next; setRoute(next);
    }
    if (sid) rpc<AgentPolicy>('getAgentPolicy').then((p) => { if (sessionRef.current?.sessionId === sid) setAgentPolicy(p); })
      .catch(() => { if (sessionRef.current?.sessionId === sid) setAgentPolicy(null); });
  }, [session?.sessionId]);
  // Next/prev Studio tab (Ctrl+Alt+←/→ via the host keybinding, so it works with focus inside OR outside the webview).
  const cycleTab = (dir: 1 | -1) => {
    const i = CYCLE_TABS.indexOf(tabRef.current);
    goTab(CYCLE_TABS[((i < 0 ? 0 : i) + dir + CYCLE_TABS.length) % CYCLE_TABS.length]);
  };
  // The '?' keyboard-shortcuts cheat sheet (also opened by the "Semanticus: Keyboard Shortcuts" command).
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // "Review as a plan →" from AI Readiness / BPA: jump to Change Plan and seed it (the view auto-proposes only if the
  // plan is empty, so an in-progress plan is never clobbered). Turns Change Plan into the apply-mode of the findings.
  const [planSeed, setPlanSeed] = useState(0);
  const reviewAsPlan = () => { setPlanSeed((n) => n + 1); goTab('optimize'); };
  const openWorkflow = (name: string) => goTab('workflows', undefined, undefined, undefined, { kind: 'workflow', value: name });
  const openWorkflowRuns = () => goTab('workflows', undefined, undefined, undefined, { kind: 'workflowRuns', value: 'runs' });
  const openPublish = () => goTab('deploy', undefined, 'publish');

  // The host navigates us here (e.g. a Model-tree "Preview data" right-click): switch tabs and, for Data, hand the
  // target table to the Data view (nonce so re-selecting the same table re-previews). signalReady lets the host
  // flush a navigation it queued while opening Studio cold.
  useEffect(() => {
    signalReady();
    return onNavigate((m) => {
      const currentSid = sessionRef.current?.sessionId;
      const messageSid = m.sessionId ?? currentSid ?? routeRef.current.sessionId;
      if (currentSid && messageSid !== currentSid) return;
      // Keyboard/command pseudo-targets are session-stamped too. A delayed old-session shortcut must not move the
      // newly opened model any more than an old object target may.
      if (m.tab === 'shortcuts') { setShortcutsOpen(true); return; }
      if (m.tab === 'cycle:next') { cycleTab(1); return; }
      if (m.tab === 'cycle:prev') { cycleTab(-1); return; }
      if (m.tab?.startsWith('group:')) { goGroup(m.tab.slice('group:'.length)); return; }
      if (m.tab === 'readiness' && m.target === 'rescan') { void scan(); }
      // The native sync status-bar item opens the existing seeded Compare path. All other host messages become one
      // session-stamped route, including Add tables, which is no longer held in a second replayable state slot.
      if (m.tab === 'compare' && m.target === 'seed') { jumpToCompare(); return; }
      const explicitSeed = m.tab === 'diagram' && m.addTables?.length
        ? { kind: 'addTables' as const, value: m.addTables }
        : undefined;
      if (m.tab) goTab(m.tab, undefined, m.target, messageSid, explicitSeed);

    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Native selection is context only. It may update Model home's route object, but never a DAX, Search or Tests seed.
  // Session identity makes a queued event from model A harmless after model B has opened, even when names match.
  useEffect(() => onTreeSelection((selection) => {
    const sid = sessionRef.current?.sessionId;
    if (sid && selection.sessionId && selection.sessionId !== sid) return;
    const object = refToObject(selection.ref);
    // The host can send its initial selection before sessionInfo returns. Keep its session stamp until the model
    // arrives, so opening Studio does not discard the row the person already selected in the native tree.
    if (!sid && selection.sessionId) { pendingTreeSelection.current = { sessionId: selection.sessionId, object }; return; }
    setSelectedObject(object);
    const current = routeRef.current;
    if (current.tool === 'modelhome' && (!selection.sessionId || current.sessionId === selection.sessionId)) {
      const next = { ...current, object };
      routeRef.current = next; setRoute(next);
    }
  }), []);
  // The native tree picker can name the hub section; use the same shared drawer as the footer and header.
  useEffect(() => onOpenConnections((section) => {
    if (section === 'open' || section === 'setup' || section === 'accounts' || section === 'history' || section === 'add' || section === 'work' || section === 'sqlsources') openConnectionsOnView(section);
    openConnections();
  }), [openConnections]);

  // The in-Studio half of the keyboard suite: gestures VS Code keybindings must NOT own (see shortcuts.tsx —
  // unmodified '?' would fire while typing, and Ctrl+Alt+letter package bindings collide with AltGr typing on
  // European layouts). Everything here checks the target is not editable, so typing is never hijacked.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing) return;
      const hostCmd = hostCommandForKey(e);
      if (hostCmd) { e.preventDefault(); e.stopPropagation(); runHostCommand(hostCmd); return; }
      if (!e.ctrlKey && !e.metaKey && !e.altKey && e.key === '?' && !isTypingTarget(e.target)) {
        e.preventDefault(); setShortcutsOpen((v) => !v); return;
      }
      if (!e.ctrlKey || !e.altKey || e.metaKey || isTypingTarget(e.target)) return;
      if (e.key.toLowerCase() === 'z') { e.preventDefault(); void rpc(e.shiftKey ? 'redo' : 'undo').catch(() => undefined); return; }   // the shared model timeline (both doors see it); empty-stack pops are no-ops
      if (e.shiftKey) return;
      if (e.key.toLowerCase() === 't') { e.preventDefault(); focusModelTree(); return; }
      const t = tabForKey(e);
      if (t) { e.preventDefault(); if (t === 'readiness') void scan(); goTab(t); }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function scan() {
    try {
      const c = await rpc<Scorecard>('aiReadinessScan');
      setCard(c);
      setTrend((t) => (t.length && t[t.length - 1] === c.overall ? t : [...t, c.overall].slice(-40)));
      setError(null);
    } catch (e) { setError(String((e as Error).message ?? e)); }
  }
  async function refreshSession() {
    try {
      const s = await rpc<SessionInfo>('sessionInfo');
      setSession(s);
      setEngineDown(false);
      return s;
    } catch (e) {
      // The host's own answer when it holds no engine connection. A panel that mounted while the engine was
      // already down never saw an engineState transition, so this is the second way it can find out.
      if (String((e as Error)?.message ?? e) === ENGINE_NOT_CONNECTED) setEngineDown(true);
      return null;
    }
  }

  useEffect(() => {
    (async () => { const s = await refreshSession(); if (s?.sessionId) await scan(); })();
    // Opening a file/project, local running model, or XMLA model swaps the engine session without emitting
    // model/didChange. Refresh App's authoritative copy as well as ConnectionProvider's copy, then reset the
    // model-scoped shell state. The session key below remounts every tab so a view cannot retain model A's graph,
    // tests, forms, or async state while the native tree already shows model B.
    const offReconnect = onReconnect(() => {
      const previousSessionId = sessionRef.current?.sessionId;
      void (async () => {
        const s = await refreshSession();
        // A failed refresh is not evidence of a model swap. Preserve the current shell until the host
        // confirms a replacement session, otherwise a transient RPC failure would erase local history.
        if (s?.sessionId && s.sessionId !== previousSessionId) {
          setCard(null); setError(null);
          setActivity([]); setUndoneCount(0); setPendingApprovals([]);
        }
        // Same-session reconnect: the run-tracking effect (keyed on sessionId) will NOT re-run, so reconcile the
        // run map against the engine to clear any ghost live run whose completion broadcast we missed while down.
        // A changed session instead remounts + reseeds via that effect, so no reconcile is needed there.
        if (s?.sessionId && s.sessionId === previousSessionId) reconcileRuns(s.sessionId);
        if (s?.sessionId) await scan();
      })();
    });
    const off = onDidChange((n: ChangeNotification) => {
      if (sessionRef.current?.sessionId && n.sessionId !== sessionRef.current.sessionId) return;
      setModelChangeNonce((value) => value + 1);
      // Undo/redo broadcast as their own didChange (label 'undo'/'redo'). Interpret them as moving the rollback
      // boundary rather than appending a node — so the timeline reads like a stack-state view (undone entries dim,
      // redo restores them). A fresh edit clears the redo branch (TE2's UndoManager drops the RedoStack), so the
      // undone entries can never come back — drop them and start the new entry on top.
      // The engine broadcasts undo/redo even on an EMPTY stack (a no-op pop still fires the event), reachable via the
      // MCP undo door or a second client — so clamp to [0, edits]. Without the upper cap an extra undo would inflate
      // undoneCount past the entry count and desync the timeline (negative "edits" tile, off-by-one redo).
      if (n.label === 'undo') { setUndoneCount((c) => Math.min(c + 1, activityLenRef.current)); }
      else if (n.label === 'redo') { setUndoneCount((c) => Math.max(0, c - 1)); }
      else {
        setActivity((a) => [{ revision: n.revision, sessionId: n.sessionId, origin: n.origin, label: n.label, count: n.deltas?.length ?? 0, refs: (n.deltas ?? []).map((d) => d.ref).filter(Boolean).slice(0, 12), ts: Date.now() }, ...a.slice(undoneCountRef.current)].slice(0, 200));
        setUndoneCount(0);
      }
      window.clearTimeout(rescanTimer.current);
      rescanTimer.current = window.setTimeout(() => { void scan(); void refreshSession(); }, 350);
    });
    // The host announces the engine going away and coming back, and answers this on studioReady. Studio must not
    // have to guess it from a failed request: a rejected request could be one wedged call, while this is the host
    // saying it has no engine at all.
    const offEngine = onEngineState((connected) => {
      setEngineDown(!connected);
      if (connected) void refreshSession();
    });
    return () => { off(); offReconnect(); offEngine(); window.clearTimeout(rescanTimer.current); };
  }, []);

  async function applySafeFixes() {
    setBusy(true); setError(null);
    try { const r = await rpc<{ scorecard: Scorecard }>('applySafeFixes'); if (r?.scorecard) setCard(r.scorecard); await refreshSession(); }
    // Applying every safe fix in one step is FREE from 2026-09-15 (Kane's feature line). There is no
    // entitlement refusal left to soften here, so a failure is a real failure and reads as one.
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  // ---- the one feature-aware render boundary --------------------------------------------------------
  // Every way into a tool ends here, because every one of them sets route.tool and nothing else decides
  // what renders: the tab row, a restored route, the legacy `dataagent` id, Find a tool, keyboard cycling
  // (cycleTab), host navigation (onNavigate -> goTab) and a deep link all move the same value. So the gate
  // is on the OPEN TOOL, once, above the tab switch. A pill on a navigation button is not a gate.
  const gatedFeature = featureOfTool(tab);
  const featureGrant = useFeature(gatedFeature);
  // Tests is the trap. TestsView mounts for EVERY open model and is merely hidden with a CSS class, so a
  // gate on the visible branch alone would leave its listTests / listTableMappings / listTestRuns calls
  // running in the background on the free plan. It needs its own grant because it is not the open tool,
  // and not rendering it at all is also what clears its paid state: an entitlement change unmounts the
  // component, and its run evidence goes with it.
  const testsGrant = useFeature('tests');
  const routeIsCurrent = !!session?.sessionId && route.sessionId === session.sessionId;
  const routeObjectRef = routeIsCurrent && route.object ? objectToRef(route.object) : undefined;
  const routeTable = routeIsCurrent ? tableOfObject(route.object) : undefined;
  const seed = routeIsCurrent ? route.seed : undefined;
  const dataTarget = tab === 'data' && routeTable ? { table: routeTable, nonce: route.nonce } : null;
  const diagramAdd = tab === 'diagram' && seed?.kind === 'addTables' && Array.isArray(seed.value) ? { tables: seed.value, nonce: route.nonce } : null;
  const diagramRelationship = tab === 'diagram' && route.object?.kind === 'relationship' ? { id: route.object.id, nonce: route.nonce } : null;
  const lineageTarget = tab === 'lineage' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce } : null;
  const testTarget = tab === 'tests' && seed?.kind === 'test' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce } : null;
  const daxTarget = tab === 'daxlab' && seed?.kind === 'query' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce } : null;
  const workflowTarget = tab === 'workflows' && (seed?.kind === 'workflow' || seed?.kind === 'workflowRuns')
    ? { kind: seed.kind === 'workflow' ? 'workflow' as const : 'section' as const, value: String(seed.value), nonce: route.nonce }
    : null;
  const pqTarget = tab === 'mcode' && routeTable ? { table: routeTable, partitionId: route.object?.kind === 'partition' ? routeObjectRef : undefined, nonce: route.nonce } : null;
  const advArea = tab === 'advmodels' && seed?.kind === 'advanced' ? { area: String(seed.value), nonce: route.nonce } : null;
  const searchTarget = tab === 'search' && seed?.kind === 'search'
    ? { query: String(seed.value), scope: route.object?.kind === 'table' ? routeObjectRef : undefined, nonce: route.nonce }
    : null;
  const permissionTarget = tab === 'permissions' && seed?.kind === 'approval' ? { id: String(seed.value), nonce: route.nonce } : null;
  const publishNonce = tab === 'deploy' && seed?.kind === 'publish' ? route.nonce : 0;
  const statsTarget = tab === 'stats' && routeTable ? { table: routeTable, nonce: route.nonce } : null;
  const historyTarget = tab === 'history' ? routeObjectRef : undefined;
  const showConnections = (section: HubView = 'open') => { openConnectionsOnView(section); openConnections(); };
  const selectOnModelHome = (object: Route['object']) => {
    setSelectedObject(object);
    const current = routeRef.current;
    if (current.tool !== 'modelhome') return;
    const next = { ...current, object };
    routeRef.current = next; setRoute(next);
  };

  return (
    <DaxModelProvider key={session?.sessionId ?? 'no-model'}>
    {/* Lineage's report-analysis holder stays mounted while the conditional tab body below changes. The surrounding
        session key still drops model-scoped state atomically when a different model opens. */}
    <LineageTabStateProvider>
    <ActivityProvider>
    <DaxLabTabStateProvider>
    <StorageTabStateProvider active={tab === 'stats'}>
    <ShortcutsOverlay open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    <ConnectionsHub open={connectionsOpen} onClose={closeConnections} />
    <Shell route={route} onTab={goTab} onGroup={goGroup} onDone={goDone} onShortcuts={() => setShortcutsOpen(true)} unseen={unseen}
      historyCount={activity.length - undoneCount} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={pendingApprovals[0]?.id}
      onConnections={showConnections} onJumpToCompare={jumpToCompare} activeRun={mostRecentLive(runs)} liveRunCount={liveRuns(runs).length}
      onWorkflowRuns={openWorkflowRuns} onPublish={openPublish} agentPolicy={agentPolicy} hasPlan={hasPlan}>
      <ToolErrorBoundary resetKey={tab}>
      <>
      {/* A test run can carry substantial cell evidence, so keep the view alive across Studio navigation instead
          of copying it into persisted webview state. The session key still discards it when a different model opens. */}
      {session?.sessionId && testsGrant === 'granted' && <div className={tab === 'tests' ? 'h-full' : 'hidden'}><TestsView key={session.sessionId} navTarget={testTarget} onNavConsumed={consumeRouteSeed}
        onOpenDaxLab={(ref) => goTab('daxlab', undefined, ref)} onOpenSavedReports={() => goTab('evidence')} /></div>}
      {engineDown ? (
        <EngineDown />
      ) : !session?.sessionId ? (
        <Empty onOpen={() => showConnections('open')} />
      ) : featureGrant === 'denied' ? (
        <ProPreview tool={tab} />
      ) : featureGrant === 'unknown' ? (
        <FeatureChecking />
      ) : tab === 'modelhome' ? (
        // Overview shows the Size by table scan the storage holder already owns, so landing on a model shows the
        // last real measurement without starting a DMV sweep of its own. The holder sits ABOVE this switch, so
        // App's own body cannot read it; the consumer keeps that read here at the mount instead.
        // The session id is re-tested inside the render prop because a property narrowing does not survive into a
        // closure; the outer branch has already proved it, so the null arm is unreachable.
        // sessionEdits is the changes THIS WEBVIEW has been notified of since the model opened, capped at 200 by
        // the activity list itself. It is not a destination comparison, so it travels under a name that says so.
        <StorageTabStateContext.Consumer>{(storage) => !session?.sessionId ? null :
          <ModelHome sessionId={session.sessionId} revision={session.revision} changeNonce={modelChangeNonce} modelName={session.modelName}
            compatibilityLevel={session.compatibilityLevel}
            selected={routeIsCurrent ? (route.object ?? selectedObject) : undefined} onSelect={selectOnModelHome} onConnections={() => showConnections('setup')}
            sessionEdits={activity.length - undoneCount} agentPolicy={agentPolicy} storage={storage}
            onGo={(tool, target, addTables) => goTab(tool, undefined, target, undefined,
              addTables?.length ? { kind: 'addTables', value: addTables } : undefined)} />}
        </StorageTabStateContext.Consumer>
      ) : tab === 'evidence' ? (
        <EvidenceView key={session.sessionId} />
      ) : tab === 'readiness' ? (
        <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
          {error && <Banner color="var(--sem-bad)">{error}</Banner>}
          {card?.gatedBy?.length ? <Banner color="var(--sem-bad)">{card.gatedBy.join(' · ')}</Banner> : null}
          {card?.caveat ? <Banner color="var(--sem-warn)">{card.caveat}</Banner> : null}
          {card?.ruleErrors?.length ? <Banner color="var(--sem-warn)">{card.ruleErrors.join(' · ')}</Banner> : null}
          {card ? <Hero card={card} trend={trend} busy={busy} onSafe={applySafeFixes} onRescan={scan} onReviewAsPlan={reviewAsPlan} /> : <Loading />}
          {card && <Categories card={card} />}
          {/* The latest behavioral signal, as a summary. Interview evidence left the Tests page in 1.2.0. */}
          {card && <InterviewSummaryChip />}
          {/* And the saved questions themselves, which the summary chip cannot manage. Taking the interview
              out of Tests was the decision; taking it out of the APP was not, and for a while the only way
              to see a saved question was MCP (Astra, question 4). This is a temporary home, unchanged. */}
          {card && <InterviewCard />}
          {card && <Findings card={card} onRescan={scan} />}
          {card && <CustomRulesPanel kind="readiness" onChanged={() => void scan()} />}
        </div>
      ) : tab === 'history' ? (
        <HistoryView items={activity} undoneCount={undoneCount} sessionId={session?.sessionId} objectFilter={historyTarget}
          onOpenRollback={(point) => { setDeployRestoreTarget({ id: point.id, endpoint: point.endpoint, database: point.database, nonce: ++navNonce.current }); goTab('deploy'); }} />
      ) : tab === 'workflows' ? (
        <WorkflowsView navTarget={workflowTarget} onNavConsumed={consumeRouteSeed} runs={runs} onRunUpdate={foldRun}
          markOwnRun={(id) => ownRunIds.current.add(id)} isOwnRun={(id) => ownRunIds.current.has(id)} />
      ) : tab === 'knowledge' ? (
        <KnowledgeView onOpenWorkflows={() => goTab('workflows')} />
      ) : tab === 'optimize' ? (
        <OptimizeView seedNonce={planSeed} />
      ) : tab === 'spec' ? (
        <SpecView session={session} />
      ) : tab === 'bpa' ? (
        <BpaView onReviewAsPlan={reviewAsPlan} />
      ) : tab === 'diagram' ? (
        <DiagramView addTables={diagramAdd} focusRelationship={diagramRelationship} onNavConsumed={consumeRouteSeed} />
      ) : tab === 'advmodels' ? (
        <AdvancedModelsView navArea={advArea} onNavConsumed={consumeRouteSeed} />
      ) : tab === 'mcode' ? (
        <Suspense fallback={<Loading />}>
          <MCodeView navTarget={pqTarget} />
        </Suspense>
      ) : tab === 'stats' ? (
        <StatsView onReviewAsPlan={reviewAsPlan} navTarget={statsTarget} />
      ) : tab === 'data' ? (
        <DataPreviewView target={dataTarget} />
      ) : tab === 'lineage' ? (
        // Add a check is a REAL hand-off, not a link to a page: a measure goes straight into the New check
        // drawer on the Tests page with its measure already filled in, through the same test seed the model
        // tree uses. Anything that is not a measure lands on Tests with its context intact, because a check
        // runs one measure and a column has to be asked about rather than guessed at.
        <LineageView navTarget={lineageTarget} onOpenPlan={() => goTab('optimize')} onOpenTests={() => goTab('tests')}
          // The seed is explicit because a TABLE ref (a column with no measure to pick, offering its table's row
          // count instead) is not one seedForTarget mints on its own, and without it the hand-off reached the
          // Tests page with the context dropped. One line, on the line this hand-off already owned.
          onAddCheck={(ref) => goTab('tests', undefined, ref, undefined, { kind: 'test', value: ref })}
          onRename={(ref) => goTab('modelhome', undefined, ref)} onOpenWorkflow={openWorkflow} />
      ) : tab === 'search' ? (
        <SearchView sessionId={session.sessionId} navQuery={searchTarget} onNavConsumed={consumeRouteSeed} onOpenPlan={() => goTab('optimize')} />
      ) : tab === 'docs' ? (
        <DocumentationView />
      ) : tab === 'deploy' ? (
        <DeployView key="deploy" seed={compareSeed} dataAgent={<DataAgentView />} restoreTarget={deployRestoreTarget} onRestoreConsumed={() => setDeployRestoreTarget(null)} publishNonce={publishNonce} onPublishConsumed={consumeRouteSeed} />
      ) : tab === 'tests' ? (
        null
      ) : tab === 'permissions' ? (
        <PermissionsView focusApproval={permissionTarget} onFocusConsumed={consumeRouteSeed} onPolicyChanged={setAgentPolicy} onApprovalChanged={() => { void refreshPendingApprovals(); }} />
      ) : tab === 'dataagent' ? (
        // Distinct key + no seed: the legacy route must land ON the Data Agent. A shared fiber would
        // ignore initialMode on re-render, and a lingering compare seed would flip the mode to push.
        <DeployView key="dataagent" dataAgent={<DataAgentView />} initialMode="advanced" initialAdvanced="dataagent" />
      ) : tab === 'daxlab' ? (
        <DaxLabView navTarget={daxTarget} onNavConsumed={consumeRouteSeed} onOpenTests={() => goTab('tests')} />
      ) : (
        // Explicit, safe fallback: every valid tab is enumerated above and goTab rejects unknown ids, so this is
        // unreachable in practice — but if it ever fires it lands on the first-of-lifecycle tab, never a random
        // mid-list one (the old `: <DaxLabView/>` catch-all was itself the "lands on DAX Lab" skew bug).
        FALLBACK_TAB === 'diagram' ? <DiagramView addTables={diagramAdd} focusRelationship={diagramRelationship} onNavConsumed={consumeRouteSeed} /> : null
      )}
      </>
      </ToolErrorBoundary>
    </Shell>
    </StorageTabStateProvider>
    </DaxLabTabStateProvider>
    </ActivityProvider>
    </LineageTabStateProvider>
    </DaxModelProvider>
  );
}

// The Semanticus brand mark — a compact abacus. An Ink rounded tile with three rods and nine beads;
// the three Signal-green beads trace a diagonal (matches the brand pack mark). Idle beads are muted grey.
function BrandMark() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-label="Semanticus" className="shrink-0">
      <rect x="1" y="1" width="22" height="22" rx="6" fill="#0D1117" />
      <rect x="1.5" y="1.5" width="21" height="21" rx="5.5" stroke="#EAEEF2" strokeOpacity="0.1" />
      <g stroke="#39424E" strokeWidth="1.2" strokeLinecap="round">
        <line x1="5" y1="7.5" x2="19" y2="7.5" /><line x1="5" y1="12" x2="19" y2="12" /><line x1="5" y1="16.5" x2="19" y2="16.5" />
      </g>
      <g fill="#5A6675">
        <circle cx="11" cy="7.5" r="2" /><circle cx="15" cy="7.5" r="2" />
        <circle cx="7" cy="12" r="2" /><circle cx="15" cy="12" r="2" />
        <circle cx="7" cy="16.5" r="2" /><circle cx="11" cy="16.5" r="2" />
      </g>
      <g fill="#2ED47A">
        <circle cx="7" cy="7.5" r="2" /><circle cx="11" cy="12" r="2" /><circle cx="15" cy="16.5" r="2" />
      </g>
    </svg>
  );
}

// The header's gear is gone (Kane, 2026-09-14). It sat beside Connections, named nothing in words, and was the
// only header way into assistant permissions. Permissions has a named row in the Your assistant menu now, beside
// the rest of the assistant's controls, so the glyph and its button went with it.

function ToolChooser({ tab, locked, onTab }: { tab: StudioTab; locked: Set<string>; onTab: (t: string) => void }) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (!open) return;
    const closeOutside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const closeEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', closeOutside); document.addEventListener('keydown', closeEscape);
    return () => { document.removeEventListener('mousedown', closeOutside); document.removeEventListener('keydown', closeEscape); };
  }, [open]);
  return <div ref={wrap} className="tool-chooser relative">
    <button type="button" className="studio-header-action" aria-expanded={open} aria-haspopup="menu" onClick={() => setOpen((value) => !value)}>Find a tool</button>
    {open && <div role="menu" aria-label="Find a tool" className="tool-chooser-menu absolute right-0 top-full z-50 mt-1">
      {TAB_GROUPS.map((group) => <section key={group.id} aria-label={group.label}>
        <div className="tool-chooser-heading">{group.label}</div>
        {/* The row carries a STABLE TOOL ID. It always did, and it now matters twice over: a Pro pill changes
            the row's text, so anything selecting these by their words breaks the moment a tool is locked. */}
        {group.tabs.map((tool) => <button key={tool.id} role="menuitem" type="button" data-tool={tool.id}
          aria-current={tool.id === tab || ((tab === 'compare' || tab === 'dataagent') && tool.id === 'deploy') ? 'page' : undefined}
          onClick={() => { onTab(tool.id); setOpen(false); }}>{tool.label}<ProBadge show={locked.has(tool.id)} /></button>)}
      </section>)}
    </div>}
  </div>;
}

function MoreStudioMenu({ activeArea, onGroup, tab, onTab, onConnections }: {
  activeArea: Area | null; onGroup: (gid: string) => void; tab: StudioTab;
  onTab: (tab: string) => void; onConnections: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { setOpen(false); wrap.current?.querySelector('button')?.focus(); } };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  const go = (next: string) => { onTab(next); setOpen(false); };
  // The overflow menu holds its own copy of Find a tool, so it marks the locked tools the same way.
  const lockedTools = useLockedTools();
  return <div ref={wrap} className="studio-more relative">
    <button type="button" aria-label="More Studio tools" aria-expanded={open} aria-haspopup="menu" onClick={() => setOpen((value) => !value)}
      className="studio-header-action">More</button>
    {open && <div role="menu" className="compact-menu absolute right-0 top-full z-50 mt-1">
      <div className="compact-areas">{TAB_GROUPS.map((group) => <button key={group.id} role="menuitem" type="button" aria-current={activeArea === group.id ? 'page' : undefined}
        onClick={() => { onGroup(group.id); setOpen(false); }}>{group.label}</button>)}</div>
      <ToolChooser tab={tab} locked={lockedTools} onTab={go} />
      {/* No Settings row: this menu is where the header utilities fold, and the gear is not one of them any more.
          Assistant permissions live in the Your assistant menu, which is on the header at every width. */}
      <button role="menuitem" type="button" onClick={() => { onConnections(); setOpen(false); }}>Connections</button>
    </div>}
  </div>;
}

// Header folding is MEASURED, not guessed. The old rule folded the utilities at a fixed 1500px container width,
// so on a 1440 window Find a tool and Connections were hidden behind More while the header still had
// room to spare. A ResizeObserver works out what actually fails to fit and writes data-fold on the header;
// styles.css keys the cumulative levels off that attribute. Order is contract v1.3 section 2: 1 = the model
// identity detail, 2 = the utilities into More, 3 = the run chip shortens, 4 = the five areas into the
// compact menu, and 4 is reachable only on a genuinely narrow header. Publish and Help never fold.
// The stage as a person writes it. The registry stores canonical ids, which read like a typo in a sentence
// ("to Contoso Sales · uat"). A label outside the standard four is somebody's own word, so it prints as typed.
// This is display only: the permission sentence is still keyed off the raw label.

const FOLD_AREAS_BELOW = 720;   // the five areas fold only below this header width
const FOLD_CHIP_WIDTH = 95;     // must match the level-3 .studio-run-chip max-width in styles.css

// One measuring pass, taken while data-fold="measure" has every part visible. Returns the shallowest level whose
// cumulative saving covers the overflow, so the header never folds more than it has to.
function measureHeaderFold(header: HTMLElement): number {
  const style = getComputedStyle(header);
  const gap = parseFloat(style.columnGap) || 0;
  const padding = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
  const available = header.clientWidth - padding;
  if (available <= 0) return 0;
  const width = (el: Element | null | undefined) => (el instanceof HTMLElement ? el.offsetWidth : 0);
  const find = (selector: string) => header.querySelector(selector);
  const brand = header.firstElementChild;
  const identity = find('.studio-identity');
  const groups = find('.studio-groups');
  const actions = find('.studio-actions');
  const utilities = find('.studio-utilities');
  const more = find('.studio-more');
  const chip = find('.studio-run-chip');
  const identityLine = identity?.firstElementChild;
  const identityWidth = width(identity);
  const moreWidth = width(more);
  // What the header needs with nothing folded. More is measured here but is not shown at level 0, so take it
  // and its gap back out of the actions row.
  // The areas nav carries its own left margin (ml-2); leaving it out under-counted the row by 8px, which the
  // shrinkable nav then absorbed, so Workflows touched the run chip at 1280.
  const groupsMargin = groups instanceof HTMLElement ? parseFloat(getComputedStyle(groups).marginLeft) || 0 : 0;
  const needed = width(brand) + identityWidth + groupsMargin + (groups ? groups.scrollWidth : 0)
    + Math.max(0, (actions ? actions.scrollWidth : 0) - (moreWidth ? moreWidth + gap : 0)) + gap * 3;
  const overflow = needed - available;
  if (overflow <= 0) return 0;

  // Cumulative savings per level, in the contract's fold order.
  const identityFolded = identity
    ? Math.min(identityWidth, Math.max(0, (identityLine ? identityLine.scrollWidth : 0) - width(find('.studio-model-identity'))))
    : 0;
  const saving: number[] = [0];
  saving[1] = identityWidth - identityFolded;
  saving[2] = saving[1] + Math.max(0, width(utilities) - moreWidth);
  saving[3] = saving[2] + (chip ? Math.max(0, width(chip) - FOLD_CHIP_WIDTH) : 0);
  saving[4] = saving[3] + (groups ? width(groups) + gap : 0) + (identityFolded ? identityFolded + gap : 0);
  // The last fold is decided by FIT, not by a number. The old rule allowed level 4 only under FOLD_AREAS_BELOW,
  // so a header 769 CSS px wide stopped at level 3 while level 3's saving still did not cover the overflow. The
  // nav then went on painting its buttons outside its own shrunken box: measured 2026-09-14 at 1000px and 130%
  // Studio zoom (which IS a 769px header), Workflows spanned x=482.7 to 581.8 while the actions started at
  // x=479.6, so clicking the middle of Workflows opened More. FOLD_AREAS_BELOW stays as the early shortcut: a
  // header that narrow, and already overflowing, has no shallower fit to find.
  if (header.clientWidth < FOLD_AREAS_BELOW) return 4;
  for (let level = 1; level <= 3; level++) if (saving[level] >= overflow) return level;
  return 4;
}

function useHeaderFold(ref: React.RefObject<HTMLElement | null>, signature: string) {
  useLayoutEffect(() => {
    const header = ref.current;
    if (!header) return;
    let frame = 0;
    let lastWidth = -1;
    const apply = () => {
      frame = 0;
      const el = ref.current;
      if (!el) return;
      lastWidth = el.clientWidth;
      // Measure and apply inside one task: nothing paints in between, so this reads as a single state change
      // rather than a flash of the unfolded header.
      el.dataset.fold = 'measure';
      el.dataset.fold = String(measureHeaderFold(el));
    };
    apply();
    const observer = new ResizeObserver(() => {
      // Folding changes the header's HEIGHT, which fires this observer again. Only a width change is news, so
      // this cannot loop, and one rAF per resize frame keeps it to a single measure per frame.
      if (!ref.current || ref.current.clientWidth === lastWidth || frame) return;
      frame = requestAnimationFrame(apply);
    });
    observer.observe(header);
    return () => { if (frame) cancelAnimationFrame(frame); observer.disconnect(); };
  }, [ref, signature]);
}

function Shell({ route, onTab, onGroup, onDone, onShortcuts, unseen, historyCount, pendingApprovalCount, firstPendingApprovalId,
  onConnections, onJumpToCompare, activeRun, liveRunCount, onWorkflowRuns, onPublish, agentPolicy, hasPlan, children }: {
  route: Route; onTab: (t: string, approvalId?: string, target?: string) => void; onGroup: (gid: string) => void;
  onDone: () => void; onShortcuts: () => void; unseen: Set<string>; historyCount: number; pendingApprovalCount: number;
  firstPendingApprovalId?: string; onConnections: (section?: HubView) => void; onJumpToCompare: () => void;
  activeRun: WorkflowRunView | null; liveRunCount: number; onWorkflowRuns: () => void; onPublish: () => void;
  agentPolicy: AgentPolicy | null | undefined; hasPlan: boolean; children: React.ReactNode;
}) {
  const tab = route.tool;
  const activeGroup = route.destination === 'utility' ? null : route.destination;
  const group = activeGroup ? TAB_GROUPS.find((item) => item.id === activeGroup) : undefined;
  const { session: shellSession, context, contextResolved } = useConnection();
  // Set by the Published page's own comparison against the destination. Null until something has actually tried.
  const unreachable = usePublishReach();
  // The tools this plan does not reach. The row keeps every one of them in its place and marks it; the
  // pill is a label, and the gate is the render boundary in App above.
  const lockedTools = useLockedTools();
  const lineageBusy = useLineageReportsBusy();
  const daxLabBusy = useDaxLabBusy();
  const storageBusy = useStorageBusy();
  const busy = useMemo(() => {
    const surfaces: string[] = [];
    if (lineageBusy) surfaces.push('lineage');
    if (daxLabBusy) surfaces.push('daxlab');
    if (storageBusy) surfaces.push('stats');
    return busyAffordance(surfaces, tab, activeGroup, TAB_TO_GROUP);
  }, [lineageBusy, daxLabBusy, storageBusy, tab, activeGroup]);
  const source = shellSession?.source ? shellSession.source.replace(/[\\/]+$/, '').split(/[\\/]/).pop() : undefined;
  const publishing = context?.publishing;
  const publishTarget = publishing?.available ? (publishing.modelName || publishing.database || 'linked model') : null;
  const publishStageLabel = publishing?.effectiveLabel || publishing?.label;
  const publishStage = publishStageLabel ? publishStageName(publishStageLabel)
    : (publishing?.unlabelled ? 'production' : 'destination needed');
  const permissionLine = agentPolicy ? publishPermissionDescription(agentPolicy, publishing?.effectiveLabel || publishing?.label, !!publishTarget)
    : agentPolicy === null ? 'assistant permissions unavailable' : 'checking your assistant permissions';
  const publishLine = publishTarget ? `to ${publishTarget} · ${publishStage}` : 'no publish target · choose before publishing';
  const permissionDetail = agentPolicy ? `${agentPolicy.preset}: ${permissionPresetDescription(agentPolicy)}` : permissionLine;
  const toolLabel = tab === 'permissions' ? 'Assistant permissions' : tab === 'dataagent' ? 'Data Agent' : tab === 'compare' ? 'Published' : tab === 'modelhome' ? 'Overview'
    : TAB_GROUPS.flatMap((item) => item.tabs).find((item) => item.id === tab)?.label ?? tab;
  const isSettings = route.destination === 'utility';
  const areaLabel = isSettings ? 'Settings' : group?.label ?? '';
  // The second row is the SAME row on every page. It shows the area's segment strip when the open tool is one of
  // that area's segments, and the tool's own name when it is not: a utility page (Assistant permissions) or an
  // area with a single tool (Calculations, Workflows).
  //
  // It used to drop the strip the moment a route carried an object, and that is what stranded Kane on the Yoga on
  // 2026-09-15: "went to data preview from overview and no way back, it hides all navigation buttons under model".
  // Overview > Preview data lands on Data WITH the table in hand, so the row read "Model › Access Assignment ›
  // Data" with no segments at all. Back had been retired the day before, and the Model button restored the
  // remembered page in that area, which was the very page he was on. An object does not change WHICH tools an area
  // has, so it gets no say in whether the strip is shown.
  const showStrip = !!group && group.tabs.length > 1;
  // The object itself is NOT on this row at all, in any form (Kane, 2026-09-15). The page already names what it
  // was opened with — Data heads itself "Preview · Promotion", DAX Lab shows the measure in its wells — so a copy
  // of that on the row is the same fact twice. A chip with a way to clear it was tried here and dropped: the row
  // can only clear the route's object, while the page keeps its own selection, so the control would have said it
  // did something it had not done. The row says where you are; the page says what you are looking at.
  // Escape is the keyboard Done, and only on a Settings page. Two guards, both deliberate. Typing targets are left
  // alone so Escape never eats a field's own cancel. And if ANY menu or dialog is mounted — the assistant menu, the
  // page notes, the Connections hub, the shortcuts sheet — Escape belongs to that thing, not to the page behind it;
  // this listener is on the bubble phase and checks defaultPrevented, so a handler that claims the key still wins.
  const doneRef = useRef(onDone); doneRef.current = onDone;
  useEffect(() => {
    if (!isSettings) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return;
      if (isTypingTarget(event.target) || isTypingTarget(document.activeElement)) return;
      if (document.querySelector('[role="menu"], [role="dialog"]')) return;
      event.preventDefault();
      doneRef.current();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isSettings]);
  const { zoom, stepZoom, toast } = useStudioZoom();
  // Everything in the header whose text changes its width. A ResizeObserver only sees the header resize, so the
  // fold has to be re-measured when the content inside it changes too.
  const headerRef = useRef<HTMLElement | null>(null);
  const headerSignature = [shellSession?.modelName, source, publishLine, permissionLine,
    activeRun ? `${activeRun.title || activeRun.workflow}|${activeRun.stepIndex}|${activeRun.totalSteps}|${liveRunCount}` : '',
    pendingApprovalCount, historyCount].join('|');
  useHeaderFold(headerRef, headerSignature);

  // CSS zoom on the app root scales every row, control and page together, so nothing has to be re-laid out for a
  // text size. It is applied here rather than on <body> because the harness and the extension host both own that.
  return <div className="studio-root h-full flex flex-col" style={{ zoom: zoom === ZOOM_DEFAULT ? undefined : `${zoom}%` }}>
    <header ref={headerRef} data-fold="0" className="studio-chrome flex items-center gap-2 px-3 border-b min-w-0" style={{ borderColor: 'var(--sem-border)' }}>
      <BrandMark />
      <div className="studio-identity min-w-0">
        <div className="font-semibold tracking-tight">Semanticus<span className="studio-model-identity">{shellSession?.modelName ? ` · ${shellSession.modelName}` : ''}</span></div>
        {source && <div className="truncate" title={shellSession?.source}>{source}</div>}
      </div>
      <nav className="studio-groups flex items-center gap-0.5 ml-2 min-w-0" aria-label="Studio areas">
        {TAB_GROUPS.map((item, index) => <GroupTab key={item.id} active={item.id === activeGroup}
          title={`${item.label} (Ctrl+Shift+${index + 1})`} unseen={item.id !== activeGroup && item.tabs.some((tool) => unseen.has(tool.id))}
          busy={busy.groups.has(item.id)} onClick={() => onGroup(item.id)}>{item.label}</GroupTab>)}
      </nav>
      <div className="studio-actions ml-auto flex items-center gap-2 shrink-0">
        <MoreStudioMenu activeArea={activeGroup} onGroup={onGroup} tab={tab} onTab={onTab} onConnections={() => onConnections('open')} />
        {activeRun && <button type="button" className="studio-run-chip" onClick={onWorkflowRuns} title="Continue in Workflows Runs">
          {/* The label is its own element so a shortened chip can end in an ellipsis. Left as loose text it was an
              anonymous flex item, which centres and gets cut at BOTH ends ("d a measure · step"). */}
          <span /><span className="studio-run-chip-label">{activeRun.title || activeRun.workflow} · step {Math.min(activeRun.stepIndex + 1, activeRun.totalSteps)} of {activeRun.totalSteps}{liveRunCount > 1 ? ` · ${liveRunCount} live` : ''}</span>
        </button>}
        <div className="studio-utilities">
          <ToolChooser tab={tab} locked={lockedTools} onTab={onTab} />
          <button type="button" className="studio-header-action" onClick={() => onConnections('open')}>Connections</button>
        </div>
        <HelpButton tab={tab} onGo={(next) => onTab(next as StudioTab)} onShortcuts={onShortcuts} onTextSize={stepZoom} />
        <LiveActivity onOpen={onTab} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={firstPendingApprovalId} />
        <div className="studio-publish-block">
          {/* The one publish action in the app. The Published page no longer carries its own button; pressing this
              while you are already on Published opens the review in place through the publish route seed. */}
          {/* Contract v1.5: the two publish facts are this tooltip and the bottom bar's Publish to slot. They used
              to hang under the row as a visible caption, which cost the header 19px on every page for a fact that
              never changes while you work. permissionDetail stays the deep version, one click away on the row. */}
          <button type="button" data-testid="publish-button" className="studio-publish" disabled={!contextResolved || !!unreachable}
            title={unreachable ? `Cannot reach ${publishTarget || 'the publish destination'}. The comparison on Published could not read it, so there is nothing to publish against.`
              : contextResolved ? `${publishLine}. ${permissionLine}. ${permissionDetail}` : 'Checking your connections'} onClick={onPublish}>Publish…</button>
        </div>
      </div>
    </header>
    {/* Row two, the same 40px on every page: the area name, then either the area's segment strip or the name of the
        page you are on, then the page notes behind ⓘ. This replaces the old breadcrumb row AND the always-open page
        guide, which together cost 93px above every tool.
        There is no Back here any more (Kane, 2026-09-14). Settings is the one page with no segment of its own, so it
        is the one page that gets a way out: Done, at the right-hand end, ahead of the ⓘ. */}
    <div className="studio-arearow">
      <span className="studio-arearow-area">{areaLabel} ›</span>
      {showStrip
        ? <AreaTabs group={group!} tab={tab} unseen={unseen} busyTabs={busy.tabs} locked={lockedTools} historyCount={historyCount} onTab={onTab} />
        : <strong className="studio-arearow-tool">{toolLabel}</strong>}
      {/* The right-hand end of the row, as ONE group. PageNotesButton carries its own margin-left:auto, so a second
          auto margin on Done would split the free space between them and leave Done stranded in mid-row, which is
          what it did on the first build. Inside a group sized to its content there is no free space left for the
          notes button to claim, so the pair sits flush right with Done ahead of the ⓘ. */}
      <div className="ml-auto flex items-center gap-2">
        {isSettings && <button type="button" data-testid="settings-done" className="sem-btn sem-btn-sm" onClick={onDone}>Done</button>}
        <PageNotesButton tab={tab} />
      </div>
    </div>
    {/* pb-4: the scroll region ends exactly where the context bar starts, so without it the last control on a page sits
        flush against the bar at the end of the scroll. One rule here rather than per-page padding. */}
    <main className="flex-1 overflow-auto min-h-0 pb-4">
      {/* M23, kept and strengthened: the segment strip used to sticky-pin to the top of this scroll region. It now
          sits in the chrome above it, so it cannot scroll away at all, on any page, at any scroll position. */}
      {children}
    </main>
    <ContextBar onConnections={onConnections} onReview={onJumpToCompare} />
    {toast && <div className="studio-zoom-toast" role="status">{toast}</div>}
  </div>;
}

// ---- Studio text size ---------------------------------------------------------------------------
// Ctrl (or Cmd) and the wheel anywhere in Studio, Ctrl+0 to reset, and Help > Text size, all driving one level.
// The level is a percentage applied as CSS zoom on the app root, so a bigger Studio is a bigger EVERYTHING and no
// page has to re-lay itself out. The host remembers it per workspace.
const ZOOM_MIN = 80;
const ZOOM_MAX = 130;
const ZOOM_STEP = 10;
const ZOOM_DEFAULT = 100;
const clampZoom = (value: number) => Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(value / ZOOM_STEP) * ZOOM_STEP));

function useStudioZoom() {
  const [zoom, setZoomState] = useState(ZOOM_DEFAULT);
  const [toast, setToast] = useState<string | null>(null);
  const level = useRef(ZOOM_DEFAULT);
  const timer = useRef(0);
  const setZoom = useCallback((value: number, announce = true) => {
    const next = clampZoom(value);
    // Nothing changed, so nothing is announced. Ctrl+0 at 100% must not flash a toast saying so.
    if (next === level.current) return;
    level.current = next;
    setZoomState(next);
    postStudioZoom(next);
    if (!announce) return;
    setToast(`Studio at ${next}%`);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setToast(null), 1000);
  }, []);
  const stepZoom = useCallback((direction: 'smaller' | 'larger' | 'reset') => {
    if (direction === 'reset') setZoom(ZOOM_DEFAULT);
    else setZoom(level.current + (direction === 'larger' ? ZOOM_STEP : -ZOOM_STEP));
  }, [setZoom]);
  useEffect(() => onStudioZoom((value) => { const next = clampZoom(value); level.current = next; setZoomState(next); }), []);
  useEffect(() => {
    const onWheel = (e: WheelEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      // A canvas zooms ITSELF on Ctrl+wheel and always has: React Flow's own handler, or anything that marks
      // itself data-own-zoom. Leaving the event alone there is the whole reason this listener is not on <body>
      // with a blanket preventDefault.
      // `canvas` is the FLOOR, added 2026-09-14. The ECharts lineage graph carried neither marker, so a
      // Ctrl+wheel on the part of its canvas that ZRender did not consume resized the whole of Studio: measured
      // at 1000x768, x=70,y=450 took it to 110% while x=700,y=400 on the same canvas did not. Marking that
      // container is the fix; matching the element means the next chart cannot lose its own gesture by being
      // forgotten here. The cost is that Ctrl+wheel over a chart that does NOT zoom itself now does nothing
      // rather than resizing Studio, which is the safer of the two surprises.
      const target = e.target as Element | null;
      if (target?.closest?.('.react-flow, [data-own-zoom], canvas')) return;
      e.preventDefault();
      setZoom(level.current + (e.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP));
    };
    // passive: false, or the browser refuses the preventDefault and the page zooms underneath Studio instead.
    window.addEventListener('wheel', onWheel, { passive: false });
    return () => window.removeEventListener('wheel', onWheel);
  }, [setZoom]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      if (e.key === '0') { e.preventDefault(); setZoom(ZOOM_DEFAULT); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [setZoom]);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  return { zoom, setZoom, stepZoom, toast };
}

function AreaTabs({ group, tab, unseen, busyTabs, locked, historyCount, onTab }: {
  group: (typeof TAB_GROUPS)[number]; tab: StudioTab; unseen: Set<string>; busyTabs: Set<string>;
  locked: Set<string>; historyCount: number; onTab: (tool: string) => void;
}) {
  const visibleTab = tab === 'compare' || tab === 'dataagent' ? 'deploy' : tab;
  if (group.id === 'model') {
    const primary = group.tabs.filter((t) => MODEL_PRIMARY_TAB_IDS.includes(t.id));
    const create = group.tabs.filter((t) => MODEL_CREATE_TAB_IDS.includes(t.id));
    const listed = (id: StudioTab) => MODEL_PRIMARY_TAB_IDS.includes(id) || MODEL_CREATE_TAB_IDS.includes(id);
    const openElsewhere = group.tabs.find((t) => t.id === visibleTab && !listed(t.id));
    return <nav className="area-tabs" aria-label={`${group.label} tools`}>
      {primary.map((tool) => <Tab key={tool.id} id={tool.id} active={visibleTab === tool.id} unseen={unseen.has(tool.id)} busy={busyTabs.has(tool.id)} onClick={() => onTab(tool.id)}>
        {tool.label}
      </Tab>)}
      <CreateTab items={create} activeTool={visibleTab} unseen={unseen} busyTabs={busyTabs} locked={locked} onTab={onTab} />
      {openElsewhere && <Tab key={openElsewhere.id} id={openElsewhere.id} active unseen={false} busy={busyTabs.has(openElsewhere.id)} onClick={() => onTab(openElsewhere.id)}>
        {openElsewhere.label}<ProBadge show={locked.has(openElsewhere.id)} />
      </Tab>}
    </nav>;
  }
  return <nav className="area-tabs" aria-label={`${group.label} tools`}>
    {group.tabs.map((tool) => <Tab key={tool.id} id={tool.id} active={visibleTab === tool.id} unseen={unseen.has(tool.id)} busy={busyTabs.has(tool.id)} onClick={() => onTab(tool.id)}>
      {tool.label}
      <ProBadge show={locked.has(tool.id)} />
      {tool.id === 'history' && historyCount > 0 && ' '}
      {tool.id === 'history' && historyCount > 0 && (
        <span className="ml-1.5 text-[9.5px] tnum font-semibold px-1.5 rounded-full" title={`${historyCount} edit${historyCount === 1 ? '' : 's'} on this model`}
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{historyCount}</span>
      )}
    </Tab>)}
  </nav>;
}

// The Model area's "Create" segment: a dropdown standing in for five modelling tools (Model Spec, Advanced
// Modelling, Power Query, Docs, Model notes). While one of them is the open tool, the segment itself reads as
// active and renames to "Create: <tool>" so the breadcrumb and this segment always agree on where you are.
// Keyboard: Enter/Space (or ArrowDown) opens onto the current tool, arrow keys move the highlight, Enter/Space
// picks it, Escape closes and returns focus to the segment button — the same contract as the header's menus.
//
// THE MENU IS RENDERED OUTSIDE THE STRIP. .area-tabs is overflow-x: auto, because the segment strip sits on a
// fixed-height row and must shrink rather than wrap. An absolutely positioned menu INSIDE that scroll container
// cost two things at once: it made the strip scrollable vertically as well as sideways, and focusing the
// highlighted item made the browser scroll the strip to reveal it. Kane hit both on his first click on the
// Yoga: the row was left showing one scrolled "Docs" segment with a scrollbar on two sides. So the menu is
// portalled to the Studio root and placed by the same zoom-corrected helper the tool row menu uses, and every
// focus() inside it passes preventScroll, so no container can move under a focused item ever again.
function CreateTab({ items, activeTool, unseen, busyTabs, locked, onTab }: {
  items: { id: StudioTab; label: string }[]; activeTool: StudioTab; unseen: Set<string>; busyTabs: Set<string>;
  locked: Set<string>; onTab: (tool: string) => void;
}) {
  const activeItem = items.find((it) => it.id === activeTool);
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(0);
  const wrap = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const itemRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const menuRef = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState<{ left: number; top: number } | null>(null);
  // The host is resolved at open time rather than at module load: the Studio root exists by then, and a portal
  // target that is missing would silently put the menu on the body in a different coordinate space.
  const host = open ? (buttonRef.current?.closest('.studio-root') as HTMLElement | null) ?? document.body : null;
  useEffect(() => {
    if (!open) return;
    // The menu is no longer inside `wrap`, so a click on one of its rows would read as "outside" and close it
    // before the click landed. Both boxes count as inside now.
    const outside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (wrap.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', outside);
    return () => document.removeEventListener('mousedown', outside);
  }, [open]);
  useLayoutEffect(() => {
    if (!open) { setBox(null); return; }
    const placed = anchorUnder(buttonRef.current, menuRef.current, 'start');
    if (placed) setBox(placed);
  }, [open, items.length]);
  useEffect(() => {
    if (!open) return;
    // -1 when no Create tool is open. The menu used to fall back to 0, so the FIRST item was drawn
    // highlighted the moment the menu opened and read as "you are here" when nothing was.
    setHighlight(items.findIndex((it) => it.id === activeTool));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  useEffect(() => {
    if (!open) return;
    // preventScroll on every one of these. Even out of the strip, a focus() that a browser decides to "reveal"
    // can scroll ANY ancestor that scrolls, and the row this menu belongs to is one.
    if (highlight >= 0) itemRefs.current[highlight]?.focus({ preventScroll: true });
    else menuRef.current?.focus({ preventScroll: true });   // nothing highlighted: the menu itself takes the keys
  }, [open, highlight]);
  // Escape is a CANCEL: focus goes back to the segment you opened, ring and all, so the keyboard keeps its place.
  const closeAndReturnFocus = () => { setOpen(false); buttonRef.current?.focus(); };
  // Choosing a tool NAVIGATES. Returning the ring to the segment after that left "Create" wearing the accent
  // outline while Overview wore the accent fill, so two segments read as current at once (M5). A keyboard pick
  // still lands focus on the segment; a pointer pick drops it, exactly as a plain button click would.
  const closeAfterPick = (fromKeyboard: boolean) => {
    setOpen(false);
    if (fromKeyboard) buttonRef.current?.focus(); else buttonRef.current?.blur();
  };
  const isActive = !!activeItem;
  // The open tool shows its own state on the page itself, so the segment only advertises the other four — same
  // rule the plain Tab uses when it hides its dot and spinner on the active tab.
  const isUnseen = items.some((it) => unseen.has(it.id) && it.id !== activeTool);
  const isBusy = items.some((it) => busyTabs.has(it.id) && it.id !== activeTool);
  return <div ref={wrap} className="area-tabs-create relative inline-flex">
    <button ref={buttonRef} type="button" aria-haspopup="menu" aria-expanded={open}
      className="sem-tab"
      style={isActive ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}
      onClick={() => setOpen((v) => !v)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setOpen(true); }
        else if (e.key === 'Escape' && open) { e.preventDefault(); setOpen(false); }
      }}>
      {activeItem ? `Create: ${activeItem.label}` : 'Create'}
      {/* All five Create tools are one feature, so the segment wears one pill when the plan does not reach them. */}
      <ProBadge show={items.every((it) => locked.has(it.id)) && items.length > 0} />
      <span aria-hidden="true" className="ml-1 opacity-60">▾</span>
      {isBusy && <TabBusyGlyph title="An operation is running in Create" />}
      {isUnseen && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="Your assistant ran something here" />}
    </button>
    {open && host && createPortal(
      // Placed, not flowed: left/top come from anchorUnder, in the zoomed root's own pixels. Until the first
      // measuring pass has a box it is parked off-screen, the same way the tool row menu does it, so nothing
      // flashes at 0,0.
      <div ref={menuRef} tabIndex={-1} role="menu" aria-label="Create" className="area-tabs-create-menu"
        style={box ? { left: box.left, top: box.top } : { left: -9999, top: -9999 }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); }
          else if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight((h) => (h + 1 + items.length) % items.length); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight((h) => (h <= 0 ? items.length : h) - 1); }
          else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); const it = items[highlight]; if (it) { onTab(it.id); closeAfterPick(true); } }
          else if (e.key === 'Tab') { setOpen(false); }
        }}>
        {items.map((it, i) => <button key={it.id} ref={(el) => { itemRefs.current[i] = el; }} role="menuitem" type="button" data-tab={it.id}
          aria-current={it.id === activeTool ? 'page' : undefined} className={i === highlight ? 'is-highlighted' : undefined}
          onMouseEnter={() => setHighlight(i)} onClick={() => { onTab(it.id); closeAfterPick(false); }}>
          {it.label}<ProBadge show={locked.has(it.id)} />{unseen.has(it.id) && it.id !== activeTool ? ' •' : ''}
        </button>)}
      </div>, host)}
  </div>;
}

// One tool view's render error is that tool's problem. Before this, an absent number in the apply report
// unmounted the whole Studio behind "Studio could not finish loading" and took the person's place with it
// (B1, walkthrough 2026-09-14). The header, the area strip and every other tool keep working; only the broken
// panel is replaced. The top-level boundary in main.tsx stays as the last resort for a shell-level failure.
class ToolErrorBoundary extends Component<{ resetKey: string; children: React.ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error: Error, info: ErrorInfo) { console.error('[Studio] tool view failed', error, info.componentStack); }
  // Navigating to another tool clears it WITHOUT remounting the children, so views that must stay alive across
  // navigation (Tests keeps its run evidence) are not thrown away by a neighbour's crash.
  componentDidUpdate(prev: { resetKey: string }) { if (prev.resetKey !== this.props.resetKey && this.state.failed) this.setState({ failed: false }); }
  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="sem-evidence-page">
        <div className="rounded-xl border p-5" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)', maxWidth: 520 }}>
          <div className="text-[15px] font-semibold">This page hit a problem</div>
          <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
            Nothing was changed and your model is still open. Move to another page, or reload to start this one again.
          </div>
          <button className="sem-btn sem-btn-primary mt-4" onClick={() => location.reload()}>Reload page</button>
        </div>
      </div>
    );
  }
}

// Primary area button.
function GroupTab({ active, onClick, children, unseen, busy, title }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean; title?: string }) {
  return (
    <button onClick={onClick} title={title} aria-current={active ? 'page' : undefined} className="sem-tab sem-tab-lg"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {busy && <TabBusyGlyph title="An operation is running in this group" />}
      {unseen && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="Your assistant ran something in this group" />}
    </button>
  );
}

function Tab({ id, active, onClick, children, unseen, busy }: { id?: string; active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean }) {
  return (
    // data-tab is the STABLE handle on a segment. A Pro pill changes the button's text, so a deep link or a
    // driver that matched on the words would stop finding a tool the moment that tool was locked.
    <button onClick={onClick} data-tab={id} className="sem-tab"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {busy && !active && <TabBusyGlyph title="An operation is running on this tab" />}
      {unseen && !active && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="Your assistant ran something here" />}
    </button>
  );
}

// A subtle monochrome spinning ring (currentColor → inherits the tab's ink, so it never introduces a colour). Placed
// inline after the label rather than as a corner dot, so it does not collide with the unseen dot and reads as "working".
function TabBusyGlyph({ title }: { title: string }) {
  return (
    <span className="ml-1 inline-flex sem-spin" role="status" aria-label="Working" title={title}>
      <svg width="10" height="10" viewBox="0 0 24 24" fill="none" aria-hidden="true">
        <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="3" strokeOpacity="0.3" />
        <path d="M21 12a9 9 0 0 0-9-9" stroke="currentColor" strokeWidth="3" strokeLinecap="round" />
      </svg>
    </span>
  );
}

function Hero({ card, trend, busy, onSafe, onRescan, onReviewAsPlan }: { card: Scorecard; trend: number[]; busy: boolean; onSafe: () => void; onRescan: () => void; onReviewAsPlan?: () => void }) {
  const color = GRADE_COLOR[card.grade] ?? 'var(--sem-muted)';
  return (
    <Panel>
      <div className="flex items-center gap-5">
        <div className="flex flex-col items-center justify-center w-24 h-24 rounded-2xl shrink-0" title={`Grade ${card.grade}. This is the AI readiness score: how well this model explains itself to AI.`} style={{ background: 'var(--sem-surface-2)', boxShadow: `inset 0 0 0 2px ${color}` }}>
          <div className="text-5xl font-bold leading-none" style={{ color }}>{card.grade}</div>
          <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>{card.overall.toFixed(0)}/100</div>
        </div>
        <div className="flex-1 min-w-0">
          <div className="text-[15px] font-semibold">AI understanding</div>
          <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
            {card.findings.filter((f) => !f.waived).length} findings{card.waivedCount ? ` · ${card.waivedCount} accepted` : ''} · {card.safeFixCount} safe fixes available
          </div>
          {trend.length >= 2 && (
            <div className="mt-1 -mb-1" style={{ maxWidth: 320 }}><Sparkline values={trend} /></div>
          )}
          <div className="flex flex-wrap gap-1.5 mt-2">
            {Object.entries(card.coverage).map(([k, v]) => (
              <span key={k} className="text-[11px] px-2 py-0.5 rounded-full tnum" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
                {prettyKey(k)} {v}%
              </span>
            ))}
          </div>
        </div>
        <div className="flex items-center gap-2 flex-wrap shrink-0">
          <Button primary disabled={busy || card.safeFixCount === 0} onClick={onSafe}
            title={`Apply every safe fix in one undoable step and re-check the score.`}>
            {busy ? 'Applying…' : `Apply ${card.safeFixCount} safe fix${card.safeFixCount === 1 ? '' : 'es'}`}
          </Button>
          {onReviewAsPlan && card.findings.length > 0 && (
            <Button onClick={onReviewAsPlan} title="Review every fix as one change plan, then apply in bulk">Review as a plan →</Button>
          )}
          <Button onClick={onRescan}>Re-scan</Button>
        </div>
      </div>
    </Panel>
  );
}

function Categories({ card }: { card: Scorecard }) {
  const cats = card.categories.filter((c) => c.hasRules);
  return (
    <Panel>
      <SectionTitle>Categories</SectionTitle>
      <div className="flex items-center gap-3 mt-2 text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
        <div className="w-36 shrink-0">What is checked</div>
        <div className="flex-1">How well it scores</div>
        <div className="w-12 text-right">Score</div>
        <div className="w-28 text-right">Things to fix</div>
      </div>
      <div className="flex flex-col gap-2.5 mt-1.5">
        {cats.map((c) => {
          const col = c.score >= 90 ? 'var(--sem-good)' : c.score >= 70 ? 'var(--sem-warn)' : 'var(--sem-bad)';
          return (
            <div key={c.category} className="flex items-center gap-3">
              <div className="w-36 text-[12px] shrink-0">{c.category}</div>
              <div className="flex-1 h-2 rounded-full overflow-hidden" style={{ background: 'var(--sem-surface-2)' }}>
                <div className="h-full rounded-full transition-all" style={{ width: `${c.score}%`, background: col }} />
              </div>
              <div className="w-12 text-right text-[12px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.score.toFixed(0)}</div>
              <div className="w-28 text-right text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.violations}/{c.applicable}{c.waived ? ` · ${c.waived} accepted` : ''}</div>
            </div>
          );
        })}
      </div>
    </Panel>
  );
}

const sevNum = (s: string) => (s === 'Critical' || s === 'High' ? 3 : s === 'Medium' ? 2 : 1);

function Findings({ card, onRescan }: { card: Scorecard; onRescan: () => void }) {
  const [filter, setFilter] = useState<'all' | 'SafeFix' | 'AiContent' | 'Proposal'>('all');
  const [waiveErr, setWaiveErr] = useState<string | null>(null);
  const [waivedOpen, setWaivedOpen] = useState(false);
  const { state: working, keyOf, fix, ask } = useFixState('applyFix', 'getFixPrompt');

  const activeFindings = card.findings.filter((f) => !f.waived);
  const waivedFindings = card.findings.filter((f) => f.waived);
  const counts = activeFindings.reduce((m, f) => { m[f.fix] = (m[f.fix] ?? 0) + 1; return m; }, {} as Record<string, number>);
  const list = activeFindings.filter((f) => filter === 'all' || f.fix === filter);
  const byKey = new Map(card.findings.map((f) => [f.objectRef + '|' + f.ruleId, f]));
  const toRow = (f: Finding): FindingRow => ({
    // Provenance in the rule header: a finding from a model-embedded (user-authored) rule says so.
    ruleId: f.ruleId, ruleName: f.ruleTitle + (f.custom ? ' (custom rule)' : ''), category: f.category, severity: sevNum(f.severity),
    // Prefer the analyst rendering (agent-only op hint removed server-side); fall back to the raw message when absent
    // OR blank (defence in depth: a blank displayMessage must never render as an empty finding).
    objectRef: f.objectRef, objectName: f.objectName, message: f.displayMessage?.trim() ? f.displayMessage : f.message,
    waived: f.waived, waiverReason: f.waiverReason, waiverRuleLevel: f.waiverRuleLevel,
    tag: { label: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).label, color: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).color },
  });
  const rows: FindingRow[] = list.map(toRow);
  const waivedRows: FindingRow[] = waivedFindings.map(toRow);
  const run = (p: Promise<unknown>) => { setWaiveErr(null); void p.then(onRescan).catch((e) => setWaiveErr(String((e as Error).message ?? e))); };

  const waive = (r: FindingRow, reason: string) => run(rpc('waiveFinding', 'air', r.ruleId, r.objectRef, reason));
  const waiveRule = (ruleId: string, reason: string) => run(rpc('waiveFinding', 'air', ruleId, '*', reason));
  // un-waive routes to the rule-level waiver ('*') when waived model-wide, else the single instance.
  const unwaive = (r: FindingRow) => run(rpc('unwaiveFinding', 'air', r.ruleId, r.waiverRuleLevel ? '*' : r.objectRef));

  const actions = (r: FindingRow) => {
    const f = byKey.get(r.objectRef + '|' + r.ruleId); if (!f) return null;
    const st = working[keyOf(f.ruleId, f.objectRef)];
    // Every finding gets a fix affordance: a deterministic Apply for SafeFix, and the free "Ask AI" for
    // everything else (AiContent AND Proposal/Review — get_fix_prompt's default branch builds a grounded
    // prompt for any rule). A finding with no button is a dead end the free-tier promise forbids.
    const fixBtn = f.fix === 'SafeFix'
      ? (st === 'done'
        ? <span className="text-[10px]" style={{ color: 'var(--sem-good)' }}>fixed ✓</span>
        : <MiniButton disabled={st === 'fixing'} onClick={() => fix(f.ruleId, f.objectRef, onRescan)}>{st === 'fixing' ? '…' : 'Apply'}</MiniButton>)
      : <MiniButton onClick={() => ask(f.ruleId, f.objectRef)}>{st === 'copied' ? 'Copied ✓' : 'Ask AI'}</MiniButton>;
    return <span className="flex items-center gap-1">{fixBtn}<WaiveControl onWaive={(reason) => waive(r, reason)} onUnwaive={() => unwaive(r)} /></span>;
  };
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Accept for the whole model" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl subtle label="Accept for the whole model" title="Accept every instance of this rule across the model" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => run(rpc('unwaiveFinding', 'air', ruleId, '*'))} />;

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-2">
          <SectionTitle>Findings <span style={{ color: 'var(--sem-muted)' }}>({activeFindings.length}{card.waivedCount ? <span> · <button type="button" onClick={() => { setWaivedOpen(true); document.getElementById('waived-findings')?.scrollIntoView({ block: 'nearest' }); }} title="Show the accepted findings" className="underline-offset-2 hover:underline" style={{ color: 'var(--sem-warn)' }}>{card.waivedCount} accepted</button></span> : ''})</span></SectionTitle>
        </div>
        {waiveErr && <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{waiveErr}</div>}
        {/* The fix-type filter joins the level filter in ONE row. Stacked, the two rows each began with an
            "All" and read as the same control shown twice. Its "All" is "Any fix" for the same reason. */}
        <div className="mt-2"><GroupedFindings rows={rows} renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)}
          extraFilters={
            <>
              <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>By fix</span>
              <div className="sem-seg flex-wrap" role="group" aria-label="Filter findings by who fixes them">
                <button type="button" aria-pressed={filter === 'all'} onClick={() => setFilter('all')} className="sem-seg-item" title="Show every finding">Any fix</button>
                <button type="button" aria-pressed={filter === 'SafeFix'} onClick={() => setFilter('SafeFix')} className="sem-seg-item" title="Fixes this app can apply on its own"><span>Safe</span><span className="opacity-70 tnum ml-1">{counts.SafeFix ?? 0}</span></button>
                <button type="button" aria-pressed={filter === 'AiContent'} onClick={() => setFilter('AiContent')} className="sem-seg-item" title="Wording your assistant can write"><span>AI</span><span className="opacity-70 tnum ml-1">{counts.AiContent ?? 0}</span></button>
                <button type="button" aria-pressed={filter === 'Proposal'} onClick={() => setFilter('Proposal')} className="sem-seg-item" title="Changes that need your decision"><span>Review</span><span className="opacity-70 tnum ml-1">{counts.Proposal ?? 0}</span></button>
              </div>
            </>
          } /></div>
      </Panel>
      <WaivedList rows={waivedRows} onUnwaive={unwaive} open={waivedOpen} onOpenChange={setWaivedOpen} />
    </div>
  );
}

// ---- small primitives ------------------------------------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border p-4 ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled, title }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-[10px] px-1.5 py-0.5 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color, border: `1px solid color-mix(in srgb,${color} 40%, transparent)` }}>{children}</div>;
}
function Loading() { return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Scanning model…</div></Panel>; }

// The one frame between opening a Pro tool and the entitlement answering. Neither the real page nor the
// locked preview is honest here: loading the page would run a paid read on a plan that may not have it, and
// showing the preview would flash an upsell at a paying customer. So it says what it is doing.
function FeatureChecking() {
  return (
    <div className="h-full flex items-center justify-center p-8" data-testid="feature-checking">
      <div className="text-[12px]" role="status" style={{ color: 'var(--sem-muted)' }}>
        {/* One sentence across all three surfaces that can be pending: this one, the Overview Checks card
            and the Advanced body. It said "your plan" here and something else there, which is two answers
            to the same question. */}
        <span className="sem-spin" /> {PENDING_ACCESS}
      </div>
    </div>
  );
}
// The page when there is no engine. It replaces the body inside the Shell, so the header and the areas stay where
// they were. Every button is a HOST command: nothing here can reach the engine, because there is no engine to reach.
// Kane, 2026-09-15: "Tried restart engine and it broke everything. Engine not connected now and no way back."
function EngineDown() {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="model-empty-shell text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">Semanticus is not connected to its engine.</div>
        <div className="text-[12px] mb-3" style={{ color: 'var(--sem-muted)' }}>
          The engine is the part that reads and edits your model. Start it again to carry on.
        </div>
        <div className="flex items-center justify-center gap-2 flex-wrap">
          <button type="button" className="sem-primary" onClick={() => runHostCommand('semanticus.restartEngine')}>Restart engine</button>
          <button type="button" onClick={() => runHostCommand('semanticus.openModel')}>Open a model</button>
          <button type="button" onClick={() => runHostCommand('semanticus.showOutput')}>Show details</button>
        </div>
      </div>
    </div>
  );
}

function Empty({ onOpen }: { onOpen: () => void }) {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="model-empty-shell text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">Open a model to begin</div>
        <div className="text-[12px] mb-3" style={{ color: 'var(--sem-muted)' }}>Choose local model files, a running Power BI Desktop model, or a published model.</div>
        <button type="button" className="sem-primary" onClick={onOpen}>Open model</button>
      </div>
    </div>
  );
}

// ---- Storage (VertiPaq scan) -----------------------------------------------------------------
interface VpaqReport { queryIdentity?: string | null; modelSize: number; columnCount: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; storageMode?: StorageMode; caveat?: string; error?: string; }

// The staged "does this column exist" key. '/' is a legal character in BOTH a table AND a column name, so a
// plain table + '/' + column join collides (table "Sales/EU" col "Amount" == table "Sales" col "EU/Amount").
function colKey(table: string, column: string) { return JSON.stringify([table, column]); }
function fmtMB(bytes: number) { const mb = bytes / 1024 / 1024; return mb >= 1 ? mb.toFixed(1) + ' MB' : (bytes / 1024).toFixed(0) + ' KB'; }
function fmtInt(n: number) { return (n ?? 0).toLocaleString(); }
const ENC_COLOR: Record<string, string> = { Hash: '#7C8BA5', Value: '#36c98b', RLE: '#e0b341' };
function colRef(table: string, column: string) { return 'column:' + table + '/' + column; }

// Wire types (camelCase, from the engine door) the Storage tab joins onto a scan.
interface ColumnMeta { ref: string; name: string; table: string; dataType: string; summarizeBy: string; isKey: boolean; isHidden: boolean; isCalculated: boolean; }
interface UnusedItem { ref: string; name: string; kind: string; table: string; verdict: string; }
interface UnusedResult { items: UnusedItem[]; safeCount: number; caveat?: string; }
interface ModelFingerprint { fingerprintKey?: string }

// A compact, persisted storage snapshot — enough to show composition deltas + which findings changed since. Only the
// last scan + a pinned baseline are kept, keyed by the stable QUERY-TARGET identity anchor + storage mode
// (storagecalc.mjs): the attached connection's endpoint|database (a database name alone repeats across endpoints),
// the source path on disk, and NO storage key at all when neither exists (the chain then lives in memory for the
// session — an anonymous model must never share a bucket with another). The anchor keys on the scanned (query-target)
// model, NOT the editing session, so swapping two query models onto one file session never shares a comparison chain.
// Never the shape fingerprint (deleting a column — the very fix this loop
// recommends — changes the fingerprint and would orphan the baseline). Each snapshot instead CARRIES its fingerprint
// + coverage metadata, so compare-time decides the LABEL (clean delta vs "structure changed" caveat) and whether the
// component split is even comparable (same coverage basis) — see compareDecision/coverageBasis in storagecalc.mjs.
interface StorageSnap {
  at: number; known: number; data: number; dict: number; hash: number; findingKeys: string[];
  fingerprintKey: string | null;   // the shape fingerprint captured WITH the scan (null = could not be confirmed)
  scannedColumns: number;          // how many columns the component split covers (topColumns.length)
  columnCount: number;             // the model's total column count at scan time
  attributed: number;              // data+dict+hash summed over the scanned columns (== known only at full coverage)
}
interface SnapStore { last: StorageSnap | null; pinned: StorageSnap | null; }
const EMPTY_STORE: SnapStore = { last: null, pinned: null };
// The scan + the fingerprint that identifies WHAT it measured, bound atomically in one state object — a fingerprint
// fetched separately could describe a different model than the scan did (the race this shape kills).
interface ScanState {
  report: VpaqReport; at: number; fingerprintKey: string | null;
  identity: string | null;   // engine-reported identity from the same live instance that produced the report
}
interface RemoveSafeReport { revision: number; removed: { ref: string; name: string }[]; skipped: { ref: string; reason: string }[]; count: number; note?: string; }
const MB = 1048576;
const MATERIAL = 1 * MB;   // a column is "materially large" once it costs at least ~1 MB of storage

// ---- Opportunities: deterministic, evidence-backed findings the scan justifies -----------------------------------
type OppGroup = 'Can remove' | 'Worth reviewing' | 'Behavior cleanup';
const OPP_GROUP_ORDER: string[] = ['Can remove', 'Worth reviewing', 'Behavior cleanup'];
interface Opp {
  ruleId: string; ruleName: string; group: OppGroup; severity: number;
  ref: string; name: string; table: string; column: string; message: string;
  kind: 'unused' | 'dict' | 'hash' | 'summarize';
  bytes: number;                              // storage weight — ranks within a group, never masquerades as severity
  encoding?: string; data?: number; dict?: number; hash?: number; total?: number; pctModel?: number; pctTable?: number;
  effect?: string;                            // the honest storage-effect label shown beside the actions
}

const NUMERIC_TYPES = new Set(['int64', 'decimal', 'double', 'currency']);
function isNumericType(t: string) { return NUMERIC_TYPES.has((t || '').toLowerCase()); }
function pctOfTable(bytes: number, tableSize: number) { return tableSize > 0 ? Math.round((100 * bytes) / tableSize) : 0; }

// Every rule is deterministic and justified by the scan; each carries an honest effect label. Thresholds:
//  • Dictionary-dominated — the dictionary is the LARGEST component AND ≥50% of a materially large (≥1 MB) column.
//  • Hash-index-heavy — the hash index is ≥25% of a materially large column (the finding is "Hash-index-heavy",
//    never "string"; a hash index is not evidence the column is text).
//  • Unused large column — a safe-to-remove verdict (from unused_objects) that IS in the scan and materially large.
//  • Identifier summarized by default — a materially large numeric identifier column still defaulting to Sum
//    (identifier = the key flag, or an identifier suffix on a real token boundary — storagecalc.identifierLike).
// Direct Lake: every size the scan reports is RESIDENT-ONLY, so all finding copy says so — sizes read "N MB
// resident", shares read "% of resident storage", and the unused finding's reduction claim flips from an upper
// bound ("up to") to the only thing resident sizes support: a lower bound ("at least N MB currently resident").
function computeOpportunities(
  report: VpaqReport,
  unused: UnusedResult | null,
  colByRef: Map<string, ColumnMeta>,
  scannedByRef: Map<string, VpaqColumn>,
  tableSizeByName: Map<string, number>,
  evidenceLevel: StorageEvidenceLevel,
): Opp[] {
  const out: Opp[] = [];
  const tsize = (t: string) => tableSizeByName.get(t) ?? 0;
  const mode = normalizeStorageMode(report.storageMode);
  const staleQueryCopy = evidenceLevel === 'staleQueryCopy';
  const sz = (b: number) => mode === 'directLake' ? `${fmtMB(b)} resident` : mode === 'unknown' ? `${fmtMB(b)} observed` : fmtMB(b);
  const share = mode === 'directLake' ? 'of resident storage' : mode === 'unknown' ? 'of observed storage' : 'of the model';
  const observationPrefix = staleQueryCopy ? 'Linked query-copy observation. ' : '';
  const observationEffect = staleQueryCopy ? 'Read-only query-copy observation' : undefined;

  for (const c of report.topColumns) {
    if (c.totalSize < MATERIAL) continue;
    const ref = colRef(c.table, c.column);
    const data = c.dataSize ?? 0, dict = c.dictionarySize ?? 0, hash = c.hashIndexSize ?? 0, total = c.totalSize;
    const base = { ref, name: `${c.table}[${c.column}]`, table: c.table, column: c.column, encoding: c.encoding, data, dict, hash, total, pctModel: c.pctOfModel, pctTable: pctOfTable(total, tsize(c.table)) };
    const largest = Math.max(data, dict, hash);
    if (dict === largest && dict >= 0.5 * total) {
      out.push({ ...base, kind: 'dict', ruleId: 'STORAGE-DICT', ruleName: 'Dictionary-dominated column', group: 'Worth reviewing', severity: 1, bytes: total,
        message: `${observationPrefix}Dictionary is the largest component: ${sz(dict)} of ${sz(total)} (${Math.round((100 * dict) / total)}%). Often indicates a high-cardinality or wide text column.`,
        effect: observationEffect ?? 'No estimate before refresh and rescan' });
    }
    if (hash >= 0.25 * total) {
      out.push({ ...base, kind: 'hash', ruleId: 'STORAGE-HASH', ruleName: 'Hash-index-heavy column', group: 'Worth reviewing', severity: 1, bytes: total,
        message: `${observationPrefix}Hash index is ${Math.round((100 * hash) / total)}% of this column (${sz(hash)} of ${sz(total)}).`,
        effect: observationEffect ?? 'No estimate. Grouping-index controls are not yet available; review with Ask AI.' });
    }
  }

  // These rules join query-engine rows to EDITING-session metadata by textual ref. Only sameInstance proves revision
  // and object equivalence. A linked copy proves provenance but either copy can be stale or structurally different,
  // so its bytes must never become measurements or reduction estimates for same-named editing objects.
  if (evidenceLevel === 'currentEditingModel') {
    // Summarize-by cleanup: metadata + scan join. Storage effect is none; it fixes wrong totals, not size.
    for (const [ref, cr] of colByRef) {
      if (!isNumericType(cr.dataType) || cr.summarizeBy !== 'Sum' || !identifierLike(cr.name, cr.isKey)) continue;
      const sc = scannedByRef.get(ref); if (!sc || sc.totalSize < MATERIAL) continue;
      out.push({ ref, name: `${cr.table}[${cr.name}]`, table: cr.table, column: cr.name, kind: 'summarize',
        ruleId: 'STORAGE-SUMMARIZE', ruleName: 'Identifier summarized by default', group: 'Behavior cleanup', severity: 1, bytes: sc.totalSize,
        total: sc.totalSize, pctModel: sc.pctOfModel, pctTable: pctOfTable(sc.totalSize, tsize(cr.table)),
        message: 'Numeric identifier defaulting to Sum. Aggregating a key produces meaningless totals for report authors and AI.',
        effect: 'Storage effect: none' });
    }

    // Unused large column: only a SAFE verdict, present in the related scan and materially large.
    for (const it of unused?.items ?? []) {
      if (it.verdict !== 'safe' || !it.ref.startsWith('column:')) continue;
      const sc = scannedByRef.get(it.ref); if (!sc || sc.totalSize < MATERIAL) continue;
      out.push({ ref: it.ref, name: `${sc.table}[${sc.column}]`, table: sc.table, column: sc.column, kind: 'unused',
        ruleId: 'STORAGE-UNUSED', ruleName: 'Unused large column', group: 'Can remove', severity: 2, bytes: sc.totalSize,
        total: sc.totalSize, pctModel: sc.pctOfModel, pctTable: pctOfTable(sc.totalSize, tsize(sc.table)),
        message: `Not referenced by any measure, relationship, hierarchy or sort-by (model-only; report usage not checked here). ${sz(sc.totalSize)}, ${sc.pctOfModel}% ${share}.`,
        effect: mode === 'directLake'
          ? `Reduction estimate: at least ${fmtMB(sc.totalSize)} currently resident`
          : mode === 'unknown'
            ? 'Reduction estimate unavailable until storage mode is confirmed'
            : `Reduction estimate: up to ${fmtMB(sc.totalSize)} after refresh` });
    }
  }

  if (staleQueryCopy) {
    // Keep only the safe editing-model verdict and a same-named query-copy observation as read-only context. Do not
    // use the query-copy byte threshold, totals, shares, or reduction estimate. The only enabled mutation is the
    // delete_if_unused plan path, whose apply step re-verifies referential safety in the editing model.
    for (const it of unused?.items ?? []) {
      if (it.verdict !== 'safe' || !it.ref.startsWith('column:') || !scannedByRef.has(it.ref)) continue;
      out.push({ ref: it.ref, name: `${it.table}[${it.name}]`, table: it.table, column: it.name, kind: 'unused',
        ruleId: 'STORAGE-UNUSED', ruleName: 'Unused column with linked query-copy observation', group: 'Can remove', severity: 2, bytes: 0,
        message: 'No editing-model reference was found. The linked query copy has a same-named column, but its storage observation may be stale and is not a measurement of this editing object.',
        effect: 'Reduction estimate unavailable for linked copies' });
    }
  }

  return out;
}

function oppToRow(o: Opp): FindingRow {
  return { ruleId: o.ruleId, ruleName: o.ruleName, category: o.group, severity: o.severity, objectRef: o.ref, objectName: o.name, message: o.message };
}

function snapOf(scan: ScanState, comp: { known: number; data: number; dict: number; hash: number; attributed: number }, opps: Opp[]): StorageSnap {
  return {
    at: scan.at, known: comp.known, data: comp.data, dict: comp.dict, hash: comp.hash,
    findingKeys: opps.map((o) => o.ruleId + '|' + o.ref),
    fingerprintKey: scan.fingerprintKey,
    scannedColumns: scan.report.topColumns.length,
    columnCount: scan.report.columnCount,
    attributed: comp.attributed,
  };
}

// The grounded prompt the "Ask AI" action copies for the user's Claude — the BPA get_fix_prompt pattern, but composed
// from the scan evidence (there is no engine rule behind a house finding). Never claims a reduction without a rescan.
// On Direct Lake the evidence lines use resident-only language and the scan's caveat banner rides along verbatim, so
// the assistant reasons about partial residency instead of treating the bytes as model totals.
function buildAskPrompt(o: Opp, storageMode: StorageMode, caveat?: string | null): string {
  const lines = [
    'A column is flagged in the Semanticus Storage view. Please investigate and, if appropriate, propose a change.',
    '',
    `Finding: ${o.ruleName}`,
    `Object: ${o.name}  (${o.ref})`,
  ];
  if (o.encoding) lines.push(`Encoding: ${o.encoding}`);
  if (o.total != null) lines.push(storageMode === 'directLake'
    ? `Storage: ${fmtMB(o.total)} resident · ${o.pctModel ?? 0}% of resident storage, ${o.pctTable ?? 0}% of its table's resident storage`
    : storageMode === 'unknown'
      ? `Observed storage: ${fmtMB(o.total)} · ${o.pctModel ?? 0}% of observed storage, ${o.pctTable ?? 0}% of its table's observed storage`
      : `Storage: ${fmtMB(o.total)} · ${o.pctModel ?? 0}% of the model, ${o.pctTable ?? 0}% of its table`);
  if (o.data != null && o.dict != null && o.hash != null) lines.push(`Breakdown: data ${fmtMB(o.data)}, dictionary ${fmtMB(o.dict)}, hash index ${fmtMB(o.hash)}${storageMode === 'directLake' ? ' (resident-only)' : storageMode === 'unknown' ? ' (storage mode unconfirmed)' : ''}`);
  lines.push('', o.message);
  if (caveat) lines.push('', `Caveat: ${caveat}`);
  lines.push('',
    'Constraint: do not claim a storage reduction. Storage change is only knowable after a refresh and a rescan, so describe the trade-off and measure it afterwards rather than promising bytes saved.');
  return lines.join('\n');
}

interface StorageTabState {
  scanState: ScanState | null;
  setScanState: React.Dispatch<React.SetStateAction<ScanState | null>>;
  meta: ModelGraph | null;
  setMeta: React.Dispatch<React.SetStateAction<ModelGraph | null>>;
  stagedCols: Set<string> | null;
  setStagedCols: React.Dispatch<React.SetStateAction<Set<string> | null>>;
  colRows: ColumnMeta[] | null | undefined;
  setColRows: React.Dispatch<React.SetStateAction<ColumnMeta[] | null | undefined>>;
  unused: UnusedResult | null | undefined;
  setUnused: React.Dispatch<React.SetStateAction<UnusedResult | null | undefined>>;
  prevSnap: StorageSnap | null;
  setPrevSnap: React.Dispatch<React.SetStateAction<StorageSnap | null>>;
  pinnedSnap: StorageSnap | null;
  setPinnedSnap: React.Dispatch<React.SetStateAction<StorageSnap | null>>;
  compareMode: 'previous' | 'baseline';
  setCompareMode: React.Dispatch<React.SetStateAction<'previous' | 'baseline'>>;
  barMode: 'columns' | 'tables';
  setBarMode: React.Dispatch<React.SetStateAction<'columns' | 'tables'>>;
  explorerFilter: string;
  setExplorerFilter: React.Dispatch<React.SetStateAction<string>>;
  busy: boolean;
  err: string | null;
  setErr: React.Dispatch<React.SetStateAction<string | null>>;
  claudeEvent: ActivityEvent | null;
  setClaudeEvent: React.Dispatch<React.SetStateAction<ActivityEvent | null>>;
  anchor: string | null;
  scanUsable: boolean;
  scanCurrent: boolean;
  scan: () => Promise<void>;
  sessionPrev: React.MutableRefObject<StorageSnap | null>;
  persistedFor: React.MutableRefObject<{ scan: ScanState; anchor: string | null } | null>;
}

const StorageTabStateContext = createContext<StorageTabState | null>(null);

function useStorageTabState(): StorageTabState {
  const state = useContext(StorageTabStateContext);
  if (!state) throw new Error('StatsView must be rendered inside StorageTabStateProvider');
  return state;
}

// Null-tolerant selector for the Studio tab bar: true while a storage scan (human or reflected agent run) is in
// flight, so the tab bar can flag a scan that keeps running while the Storage tab is hidden.
function useStorageBusy(): boolean {
  return useContext(StorageTabStateContext)?.busy ?? false;
}

// Storage activates lazily on the first visit so lifting it does not introduce an eager DMV scan at Studio startup.
// Once activated, this holder remains above the tab switch: in-flight scans finish while hidden and all identity,
// fingerprint, enrichment, and comparison gates keep observing model/connection changes.
function StorageTabStateProvider({ active, children }: { active: boolean; children: React.ReactNode }) {
  const { conn, session, context } = useConnection();
  const [activated, setActivated] = useState(active);
  const activatedRef = useRef(active); activatedRef.current = activated || active;
  useEffect(() => { if (active) setActivated(true); }, [active]);

  const [scanState, setScanState] = useState<ScanState | null>(null);
  const [meta, setMeta] = useState<ModelGraph | null>(null);
  const [stagedCols, setStagedCols] = useState<Set<string> | null>(null);
  const [colRows, setColRows] = useState<ColumnMeta[] | null | undefined>(undefined);
  const [unused, setUnused] = useState<UnusedResult | null | undefined>(undefined);
  const [prevSnap, setPrevSnap] = useState<StorageSnap | null>(null);
  const [pinnedSnap, setPinnedSnap] = useState<StorageSnap | null>(null);
  const [compareMode, setCompareMode] = useState<'previous' | 'baseline'>('previous');
  const [barMode, setBarMode] = useState<'columns' | 'tables'>('columns');
  const [explorerFilter, setExplorerFilter] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [claudeEvent, setClaudeEvent] = useState<ActivityEvent | null>(null);

  const lastObservedFp = useRef<string | null>(null);
  const sessionPrev = useRef<StorageSnap | null>(null);
  const persistedFor = useRef<{ scan: ScanState; anchor: string | null } | null>(null);
  const scanGen = useRef(0);
  const loadSeq = useRef(0);
  const prevQueryTargetKey = useRef<string | null>(null);
  const prevQueryAnchor = useRef<string | null>(null);

  const clearScan = useCallback(() => {
    scanGen.current++;
    setScanState(null); setPrevSnap(null); setPinnedSnap(null); setCompareMode('previous'); setExplorerFilter('');
    lastObservedFp.current = null; sessionPrev.current = null; persistedFor.current = null;
  }, []);
  useEffect(() => { clearScan(); }, [session?.sessionId, clearScan]);

  const queryTargetKey = conn?.connected ? (conn.connectionId || `${conn.dataSource ?? ''}|${conn.database ?? ''}`) : null;
  const anchor = anchorOf(conn?.dataSource, conn?.database, session?.source);
  const scanUsable = !!scanState && scanUsableDuringTransition(scanState.identity, prevQueryAnchor.current, anchor);
  const scanCurrent = !!scanState && scanUsable && scanMatchesAnchor(scanState.identity, anchor);
  useEffect(() => {
    const establishedTargetChanged = prevQueryTargetKey.current != null && prevQueryTargetKey.current !== queryTargetKey;
    const existingScanDoesNotMatch = scanState != null && !scanUsableDuringTransition(scanState.identity, prevQueryAnchor.current, anchor);
    if (establishedTargetChanged || existingScanDoesNotMatch) clearScan();
    prevQueryTargetKey.current = queryTargetKey;
    prevQueryAnchor.current = anchor;
  }, [queryTargetKey, anchor, scanState, clearScan]);

  const loadStaged = useCallback(() => {
    const seq = ++loadSeq.current;
    rpc<ModelGraph>('getModelGraph')
      .then((g) => { if (seq === loadSeq.current) setMeta(g); })
      .catch(() => { if (seq === loadSeq.current) setMeta(null); });
    rpc<ColumnMeta[]>('listColumns')
      .then((cs) => { if (seq === loadSeq.current) { setColRows(cs); setStagedCols(new Set(cs.map((c) => colKey(c.table, c.name)))); } })
      .catch(() => { if (seq === loadSeq.current) { setColRows(null); setStagedCols(null); } });
    rpc<UnusedResult>('unusedObjects')
      .then((u) => { if (seq === loadSeq.current) setUnused(u); })
      .catch(() => { if (seq === loadSeq.current) setUnused(null); });
  }, []);
  useEffect(() => {
    if (!activated) return;
    loadStaged();
    const reset = () => { setMeta(null); setStagedCols(null); setColRows(undefined); setUnused(undefined); };
    const off = onDidChange(() => { reset(); loadStaged(); });
    const offReconnect = onReconnect(() => { reset(); clearScan(); loadStaged(); });
    return () => { off(); offReconnect(); };
  }, [activated, loadStaged, clearScan]);

  const scanIdentityConfirmed = context?.relationship === 'sameInstance';
  const fingerprintNow = useCallback(() =>
    scanIdentityConfirmed
      ? rpc<ModelFingerprint>('getModelFingerprint').then((f) => f?.fingerprintKey ?? null).catch(() => null)
      : Promise.resolve<string | null>(null), [scanIdentityConfirmed]);

  useClaudeReflection('vpaq_scan', (e) => {
    if (!activatedRef.current || !e.result) return;
    setErr(e.error ?? null); setClaudeEvent(e);
    const r = e.result as VpaqReport;
    const preFp = lastObservedFp.current;
    const gen = scanGen.current;
    void fingerprintNow().then((fp) => {
      if (gen !== scanGen.current) return;
      if (fp != null) lastObservedFp.current = fp;
      setScanState({ report: r, at: Date.now(), fingerprintKey: fp != null && fp === preFp ? fp : null, identity: r.queryIdentity ?? null });
    });
  });

  const scan = useCallback(async () => {
    setBusy(true); setErr(null); setClaudeEvent(null);
    const gen = scanGen.current;
    try {
      const fpBefore = await fingerprintNow();
      const r = await rpc<VpaqReport>('vertiPaqScan', 2000);
      const fpAfter = await fingerprintNow();
      if (gen !== scanGen.current) return;
      if (fpAfter != null) lastObservedFp.current = fpAfter;
      if (r.error) { setErr(r.error); return; }
      setScanState({ report: r, at: Date.now(), fingerprintKey: fpBefore != null && fpBefore === fpAfter ? fpAfter : null, identity: r.queryIdentity ?? null });
    } catch (e) { if (gen === scanGen.current) setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }, [fingerprintNow]);

  const report = scanState?.report ?? null;
  useEffect(() => { if (activated && conn?.connected && !report && !busy && !err) void scan();
  }, [activated, conn?.connected, report, busy, err, scan]);

  return (
    <StorageTabStateContext.Provider value={{
      scanState, setScanState, meta, setMeta, stagedCols, setStagedCols, colRows, setColRows, unused, setUnused,
      prevSnap, setPrevSnap, pinnedSnap, setPinnedSnap, compareMode, setCompareMode, barMode, setBarMode,
      explorerFilter, setExplorerFilter, busy, err, setErr, claudeEvent, setClaudeEvent,
      anchor, scanUsable, scanCurrent, scan, sessionPrev, persistedFor,
    }}>
      {children}
    </StorageTabStateContext.Provider>
  );
}

function StatsView({ onReviewAsPlan, navTarget }: { onReviewAsPlan?: () => void; navTarget?: { table: string; nonce: number } | null }) {
  const { conn, session, context } = useConnection();
  const {
    scanState, meta, stagedCols, colRows, unused, prevSnap, setPrevSnap, pinnedSnap, setPinnedSnap,
    compareMode, setCompareMode, barMode, setBarMode, explorerFilter, setExplorerFilter,
    busy, err, setErr, claudeEvent, setClaudeEvent, anchor, scanUsable, scanCurrent, scan, sessionPrev, persistedFor,
  } = useStorageTabState();
  const oppsPanelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => { if (navTarget?.table) setExplorerFilter(navTarget.table); }, [navTarget?.nonce]);
  const report = scanState?.report ?? null;
  const storageMode = normalizeStorageMode(report?.storageMode);

  // Derived analytics (from what the DMV gives us): total rows, the largest table, the storage composition, and
  // the deterministic opportunities the scan justifies.
  const totalRows = report ? report.tables.reduce((n, t) => n + (t.rows || 0), 0) : 0;
  const largest = report?.tables[0];
  const maxTableSize = report ? Math.max(1, ...report.tables.map((t) => t.size)) : 1;
  const maxColSize = report ? Math.max(1, ...report.topColumns.map((c) => c.totalSize)) : 1;

  const tableSizeByName = useMemo(() => new Map((report?.tables ?? []).map((t) => [t.name, t.size] as const)), [report]);
  const scannedByRef = useMemo(() => new Map((report?.topColumns ?? []).map((c) => [colRef(c.table, c.column), c] as const)), [report]);
  const colByRef = useMemo(() => new Map((Array.isArray(colRows) ? colRows : []).map((c) => [c.ref, c] as const)), [colRows]);

  // Composition: the three components summed over the scanned columns + the authoritative "Known column storage"
  // total (ModelSize = the sum of ALL column totals). When the scan covers every column these are equal; the
  // coverage percent flags any shortfall honestly rather than pretending the split is the whole story.
  const comp = useMemo(() => {
    const cols = report?.topColumns ?? [];
    const data = cols.reduce((s, c) => s + (c.dataSize ?? 0), 0);
    const dict = cols.reduce((s, c) => s + (c.dictionarySize ?? 0), 0);
    const hash = cols.reduce((s, c) => s + (c.hashIndexSize ?? 0), 0);
    const known = report?.modelSize ?? 0;
    const attributed = data + dict + hash;
    return { data, dict, hash, known, attributed };
  }, [report]);

  // Query-copy rows become byte evidence for editing objects only on the same live instance. Linked-copy provenance
  // is useful read-only context, but either revision may be stale, so its bytes stay query-copy observations.
  const evidenceLevel = scanCurrent ? storageEvidenceLevel(context?.relationship) : 'none';
  const editingEvidenceAllowed = scanCurrent && relationshipAllowsStorageEdits(context?.relationship);
  const linkedCopyObservation = evidenceLevel === 'staleQueryCopy';
  const reverifiedPlanAllowed = scanCurrent && relationshipAllowsReverifiedDeletePlan(context?.relationship);
  const opps = useMemo<Opp[]>(
    () => report ? computeOpportunities(report, unused ?? null, colByRef, scannedByRef, tableSizeByName, evidenceLevel) : [],
    [report, unused, colByRef, scannedByRef, tableSizeByName, evidenceLevel]);
  const oppsRef = useRef<Opp[]>([]); oppsRef.current = opps;
  const topOpps = useMemo(() => [...opps].sort((a, b) => b.bytes - a.bytes).slice(0, 3), [opps]);

  // Is the finding evidence COMPLETE and SUCCESSFUL for THIS scan? Both enrichment reads must have SUCCEEDED —
  // distinguishing the three states each carries: undefined = still loading, null = the read FAILED, a value =
  // loaded (possibly empty, which is a legitimate complete result). While either is loading, `opps` is missing
  // its unused/summarize findings; on failure it is permanently missing them. A finding-delta or a persisted
  // baseline computed from that partial set would invent transient "resolved" claims and, once the read recovers,
  // report the recovered findings as "introduced". So the finding set is trustworthy only when BOTH succeeded
  // (`!= null` excludes undefined AND null); a failed enrichment yields no comparison and no persisted snapshot
  // rather than a poisoned one. Reachability (meta) is not part of the finding SET, so it is not gated here.
  const evidenceComplete = unused != null && colRows != null;

  // Ranked bars: the top storage consumers, each split into components. Columns mode is EXACT (each bar is one
  // scanned column's own split). Tables mode sums the components of the SCANNED columns only, while the table
  // total (t.size) is exact — so the gap renders as an explicit "unattributed" remainder segment rather than the
  // bar silently pretending the partial split is the whole table.
  const barItems = useMemo<VpaqBarItem[]>(() => {
    if (!report) return [];
    if (barMode === 'tables') {
      const byTable = new Map<string, { data: number; dict: number; hash: number }>();
      for (const c of report.topColumns) { const a = byTable.get(c.table) ?? { data: 0, dict: 0, hash: 0 }; a.data += c.dataSize ?? 0; a.dict += c.dictionarySize ?? 0; a.hash += c.hashIndexSize ?? 0; byTable.set(c.table, a); }
      return report.tables.map((t) => {
        const b = byTable.get(t.name) ?? { data: 0, dict: 0, hash: 0 };
        const unattributed = Math.max(0, t.size - (b.data + b.dict + b.hash));
        return { ref: 'table:' + t.name, label: t.name, data: b.data, dict: b.dict, hash: b.hash, unattributed, total: t.size, pctModel: t.pctOfModel, pctTable: 100 };
      }).sort((a, b) => b.total - a.total);
    }
    return report.topColumns.map((c) => { const ts = tableSizeByName.get(c.table) ?? 0; return { ref: colRef(c.table, c.column), label: c.column, sublabel: c.table, data: c.dataSize ?? 0, dict: c.dictionarySize ?? 0, hash: c.hashIndexSize ?? 0, total: c.totalSize, pctModel: c.pctOfModel, pctTable: ts > 0 ? Math.round((100 * c.totalSize) / ts) : 0 }; }).sort((a, b) => b.total - a.total);
  }, [report, barMode, tableSizeByName]);

  // The current scan as a snapshot (memoized so identity is stable for the decision), the comparison target, and
  // what the comparison may honestly claim (storagecalc.compareDecision): the TOTAL delta is always valid (the
  // engine sums the known total over all columns before the top-N cut), the COMPONENT + FINDING deltas only when
  // both snapshots share a coverage basis, and a fingerprint mismatch labels the comparison "structure changed"
  // instead of hiding it — the did-it-help loop must survive the structural fixes it recommends.
  const curSnap = useMemo(() => (scanState && !scanState.report.error ? snapOf(scanState, comp, opps) : null), [scanState, comp, opps]);
  // Unknown mode never enters a comparison chain. Its bytes may be totals or resident observations, so even two
  // unknown scans are not a defensible before/after pair. A stale ScanState is likewise never compared.
  const comparisonEnabled = scanCurrent && storageMode !== 'unknown';
  const comparison = comparisonEnabled ? (compareMode === 'baseline' && pinnedSnap ? pinnedSnap : prevSnap) : null;
  const comparisonLabel = compareMode === 'baseline' && pinnedSnap ? 'baseline' : 'previous scan';
  const decision = useMemo(() => compareDecision(curSnap, comparison), [curSnap, comparison]);
  const compare = decision.available ? decision : null;
  const changed = useMemo(() => {
    if (!comparison || !compare?.componentsComparable || !curSnap) return null;   // finding deltas need the same coverage basis
    if (!evidenceComplete) return null;   // a finding delta over a loading/failed (partial) finding set is not evidence — never claim from it
    const cur = new Set(opps.map((o) => o.ruleId + '|' + o.ref));
    const prev = new Set(comparison.findingKeys);
    const surfaced = opps.filter((o) => !prev.has(o.ruleId + '|' + o.ref));
    // "Introduced" (appeared SINCE) is a CLAIM the evidence must support: under a top-N PRIOR scan a finding now
    // visible may have been present-but-out-of-window before — a rank shift, not a new problem. Only a FULL-coverage
    // prior scan proves it is genuinely new (storagecalc.introducedClaimAllowed); otherwise it is "newly observed"
    // — honestly present now, not provably new. Symmetric with the resolved gate below.
    const canClaimIntroduced = introducedClaimAllowed(comparison);
    const introduced = canClaimIntroduced ? surfaced : [];
    const newlyObserved = canClaimIntroduced ? [] : surfaced;
    // "Resolved" is the mirror claim: between two top-N snapshots a finding can leave the visible population by rank
    // shift alone — absent is not fixed. Only two FULL-coverage scans prove it (storagecalc.resolvedClaimAllowed);
    // under partial coverage the group shows an honest note that resolved claims need a full-coverage scan.
    const canClaimResolved = resolvedClaimAllowed(curSnap, comparison);
    const gone = [...prev].filter((k) => !cur.has(k));
    const resolved = canClaimResolved ? gone : [];
    const resolvedSuppressed = !canClaimResolved && gone.length > 0;
    return introduced.length || newlyObserved.length || resolved.length || resolvedSuppressed
      ? { introduced, newlyObserved, resolved, resolvedSuppressed } : null;
  }, [comparison, compare, curSnap, opps, evidenceComplete]);

  // Persist a compact snapshot once per scan (only after the enrichment reads SUCCEED, so findingKeys are complete
  // and trustworthy — a failed read must never persist a partial finding set that later reads back as findings
  // "introduced"), keyed by the stable QUERY-TARGET identity anchor + storage mode (storagecalc.anchorOf/snapKey):
  // the attached connection's endpoint|database, the source path on disk, and NO storage key when neither exists —
  // the chain then lives in sessionPrev (memory) so an anonymous model never shares a bucket. The anchor MUST come
  // from the query target (what vpaq_scan scans), NOT the editing session: two query models swapped onto one file
  // session would otherwise share a key and compare cleanly though unrelated. The shape fingerprint travels INSIDE
  // the snapshot instead of keying it, deciding the comparison label at read time. The guard is keyed on the scan
  // bundle AND the anchor captured INSIDE it. If a connection swap commits before clearScan renders, the effect sees
  // old ScanState plus the new anchor and refuses the write before it can migrate a snapshot or pin. The stored "last"
  // is ALWAYS the newest identity-confirmed scan, whatever its
  // coverage (storagecalc.shouldStoreAsLast) — whether two scans are component-comparable is decided at COMPARE time.
  useEffect(() => {
    if (!scanState || scanState.report.error) return;
    if (!scanCurrent) return;   // connection-swap commit: even a same-render new report stays unusable until the swap clear lands
    const scanMode = normalizeStorageMode(scanState.report.storageMode);
    if (scanMode === 'unknown') return;   // unknown bytes never seed a mode-dependent comparison chain
    if (!evidenceComplete) return;   // wait for SUCCESSFUL enrichment; a loading (undefined) or failed (null) read is not a complete baseline
    const isNewScan = persistedFor.current?.scan !== scanState;
    if (!isNewScan && persistedFor.current?.anchor === anchor) return;
    persistedFor.current = { scan: scanState, anchor };
    const snap = snapOf(scanState, comp, oppsRef.current);
    if (anchor == null) {
      // No stable identity → never a storage key. Compare against the in-memory chain for this session only.
      if (isNewScan) {
        setPrevSnap(sessionPrev.current);
        if (shouldStoreAsLast(snap)) sessionPrev.current = snap;
      }
      return;
    }
    const key = snapKey(anchor, scanMode);
    const stored = loadState<SnapStore>(key, EMPTY_STORE);
    setPrevSnap(stored.last);
    // Migrate an in-memory pin made while the anchor was still null (a baseline pinned before the identity
    // resolved): the stored pin is empty, so blindly replacing state with it would DROP the user's pin. Prefer the
    // existing in-memory pin and persist it under the now-known key; otherwise adopt the stored one.
    const pinToKeep = pinnedSnap ?? stored.pinned;
    setPinnedSnap(pinToKeep);
    saveState(key, { last: shouldStoreAsLast(snap) ? snap : stored.last, pinned: pinToKeep });
  }, [scanState, scanCurrent, evidenceComplete, comp, anchor, pinnedSnap]);

  const pinBaseline = useCallback(() => {
    if (!curSnap || !scanState) return;
    if (!scanCurrent || storageMode === 'unknown') return;
    if (anchor != null) {
      const key = snapKey(anchor, storageMode);
      const stored = loadState<SnapStore>(key, EMPTY_STORE);
      saveState(key, { last: stored.last ?? curSnap, pinned: curSnap });
    }
    // No identity → the pin lives in state for this session only (same rule as the scan chain above).
    setPinnedSnap(curSnap); setCompareMode('baseline');
  }, [curSnap, scanState, scanCurrent, anchor, storageMode]);

  // The VertiPaq report names come from the PUBLISHED query engine; Properties + the Model tree resolve against
  // the STAGED editing model. After an undeployed rename the two diverge, so a report row's name-based ref would
  // resolve to nothing or (on a name collision) the WRONG object. Verify each report row against the staged model
  // before wiring select/reveal, checking the FULL ref (table AND column name, not just the table — a renamed
  // column on a still-present table would otherwise stay reachable). Fail CLOSED while the staged shape is
  // unloaded/loading (meta or stagedCols null): an honest no-affordance beats selecting a stale or wrong object.
  // Gate each row on ONLY the data it needs: a TABLE row is reachable once the graph (meta) has loaded — an
  // unrelated listColumns failure must not needlessly kill verifiable table rows. A COLUMN row needs BOTH the
  // graph AND the staged column-name set. Both fail CLOSED while their data is null (unloaded / loading / failed).
  const stagedTables = useMemo(() => new Set((meta?.tables ?? []).map((t) => t.name)), [meta]);
  const tableReachable = useCallback((table: string) => editingEvidenceAllowed && meta != null && stagedTables.has(table), [editingEvidenceAllowed, meta, stagedTables]);
  const columnReachable = useCallback((table: string, column: string) => editingEvidenceAllowed && meta != null && stagedCols != null && stagedCols.has(colKey(table, column)), [editingEvidenceAllowed, meta, stagedCols]);
  const oppReachable = useCallback((o: Opp) => o.ref.startsWith('column:') ? columnReachable(o.table, o.column) : tableReachable(o.table), [columnReachable, tableReachable]);
  const editingBlockedReason = !scanCurrent
    ? 'Editing actions are disabled because this scan belongs to a different query target.'
    : linkedCopyObservation
      ? 'Direct editing actions are disabled because linked query-copy rows do not prove revision or object equivalence.'
    : !relationshipAllowsStorageEdits(context?.relationship)
      ? 'Editing actions are disabled because the query model is not proven to describe the open editing model.'
      : null;
  const planBlockedReason = !scanCurrent
    ? 'Removal plans are disabled because this scan belongs to a different query target.'
    : !relationshipAllowsReverifiedDeletePlan(context?.relationship)
      ? 'Removal plans are disabled because the query model is not linked to the open editing model.'
      : null;

  // Opportunity actions. Each mutating action routes through an existing dual-drive op; the effect labels beside them
  // stay honest (a storage reduction is only claimable AFTER a refresh + rescan measures it).
  const fail = (e: unknown) => setErr(String((e as Error).message ?? e));
  const guardEditingAction = () => {
    if (editingEvidenceAllowed) return true;
    setErr(editingBlockedReason ?? 'Editing actions are disabled until the scan identity is confirmed.');
    return false;
  };
  const askAi = (o: Opp) => { if (guardEditingAction()) copyText(buildAskPrompt(o, storageMode, report?.caveat)); };
  const doNotSummarize = (o: Opp) => { if (guardEditingAction()) void rpc('setColumnMetadata', o.ref, null, 'None', null, null).then(() => void scan()).catch(fail); };
  const hideColumn = (o: Opp) => { if (guardEditingAction()) void rpc('setColumnMetadata', o.ref, true, null, null, null).then(() => void scan()).catch(fail); };
  // Delete routes through remove_safe_objects, which RE-VERIFIES the safe verdict server-side at apply time — a
  // dependency added between our unused_objects read and the click downgrades to a skip with the reason, never a
  // stale delete. An empty Removed[] is surfaced honestly instead of pretending success.
  const deleteColumn = (o: Opp) => {
    if (!guardEditingAction()) return;
    void rpc<RemoveSafeReport>('removeSafeObjects', [o.ref], null)
      .then((r) => {
        if (!r.removed?.length) setErr(r.skipped?.[0]?.reason ? `Not removed: ${r.skipped[0].reason}` : (r.note || 'Not removed: the safe verdict no longer holds.'));
      })
      .catch(fail);
  };

  // "Review as a plan" dispatches REAL plan items — kind 'delete_if_unused', never bare 'delete': the Change
  // Plan's apply pipeline RE-VERIFIES the unused verdict server-side at apply time and skips (with the reason)
  // any object that gained a referencer between this click and the apply, matching the safety the direct Delete
  // button gets from remove_safe_objects. Byte evidence rides in the title only for sameInstance; linked-copy plans
  // carry no size or reduction claim because query-copy revision and object equivalence are unproven.
  // Slash-ambiguous refs are excluded
  // (fail closed, same rule as the Delete button); duplicate dispatch is the ENGINE's job now — add_plan_item
  // dedupes identical pending items, so a second click (from either door) is a no-op there, not here.
  const planEligible = useCallback((o: Opp) => reverifiedPlanAllowed && o.kind === 'unused' && !refIsAmbiguous(o.table, o.column), [reverifiedPlanAllowed]);
  const reviewAsPlanItems = useCallback(async (list: Opp[]) => {
    try {
      const eligible = list.filter(planEligible);
      if (eligible.length === 0) {
        setErr(planBlockedReason ?? 'No removal item has a safe, unambiguous editing target.');
        return;
      }
      for (const o of eligible) {
        const evidence = linkedCopyObservation
          ? 'no storage reduction estimate because linked query-copy bytes may be stale; editing-model references re-verified when applied'
          : storageMode === 'directLake'
            ? `at least ${fmtMB(o.bytes)} currently resident; no model-side references`
            : storageMode === 'unknown'
              ? 'storage reduction not estimated because storage mode is unknown; no model-side references'
              : `up to ${fmtMB(o.bytes)} after refresh; no model-side references`;
        await rpc('addPlanItem', o.ref, 'delete_if_unused', null, `Remove unused column ${o.name} (${evidence})`, null, null, 'human');
      }
      onReviewAsPlan?.();
    } catch (e) { fail(e); }
  }, [planEligible, storageMode, linkedCopyObservation, planBlockedReason, onReviewAsPlan]);

  // A bar click SELECTS the object (Properties) and FILTERS the columns explorer to its table. Reveal in Properties /
  // the Model tree stays an explicit affordance, never a hidden click side effect.
  const onBarSelect = useCallback((ref: string) => {
    if (ref.startsWith('column:')) {
      const c = report?.topColumns.find((x) => colRef(x.table, x.column) === ref);
      if (c) { if (columnReachable(c.table, c.column)) selectInProperties(ref); setExplorerFilter(c.table); }
    } else {
      const t = ref.replace(/^table:/, '');
      if (tableReachable(t)) selectInProperties(ref);
      setExplorerFilter(t);
    }
  }, [report, columnReachable, tableReachable]);

  const scanTime = scanState ? new Date(scanState.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : null;
  const knownLabel = linkedCopyObservation ? 'Query-copy storage observation' : storageMode === 'directLake' ? 'Resident storage estimate' : storageMode === 'unknown' ? 'Observed column storage' : 'Known column storage';
  const modeCaveat = report?.caveat ?? (storageMode === 'unknown'
    ? 'Storage mode could not be confirmed. Storage sizes and row counts may reflect only data currently in memory, so they are not treated as full model totals.'
    : null);
  const modelShareLabel = linkedCopyObservation ? '% query copy' : storageMode === 'directLake' ? '% resident' : storageMode === 'unknown' ? '% observed' : '% model';
  const tableShareLabel = linkedCopyObservation ? '% query table' : storageMode === 'directLake' ? '% resident table' : storageMode === 'unknown' ? '% observed table' : '% table';
  const sizeLabel = linkedCopyObservation ? 'Query-copy bytes' : 'Size';
  const removalPlanCandidates = useMemo(() => opps.filter((o) => o.kind === 'unused' && !refIsAmbiguous(o.table, o.column)), [opps]);
  // Pin is armed only once the enrichment reads SUCCEEDED (evidenceComplete — findingKeys complete, not a partial
  // set from a loading or FAILED read) AND the scan's identity is confirmed (bracketed fingerprint). A baseline
  // pinned from a failed unusedObjects/listColumns read would freeze an incomplete finding set that later reads back
  // as findings "introduced"; a missing fingerprint could never honestly anchor a campaign.
  const pinReady = !!curSnap && evidenceComplete && !!curSnap.fingerprintKey && scanCurrent && storageMode !== 'unknown';

  return (
    <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
      <Panel>
        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0">
            <div className="text-[13px] font-semibold">Storage</div>
            <div className="mt-1"><ConnectBar hint="Storage analysis reads live storage statistics from the model's query engine." /></div>
          </div>
          {conn?.connected && <Button primary disabled={busy} onClick={scan}>{busy ? 'Scanning…' : report ? 'Rescan' : 'Scan storage'}</Button>}
        </div>
        {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
      </Panel>

      {/* Storage figures come from the published model's query engine, not your staged edits — flag any divergence. */}
      <QueryStalenessChip />

      {claudeEvent && <ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} />}

      {report && storageMode !== 'import' && modeCaveat && <Banner color="var(--sem-warn)">{modeCaveat}</Banner>}
      {report && linkedCopyObservation && (
        <Banner color="var(--sem-warn)">
          <div>These sizes are measured on the model you are querying, not the copy you are editing.</div>
          <div className="mt-1">The two can differ if you have edits that are not published yet, so treat a size here as a good guide rather than an exact figure for your edits.</div>
          <div className="mt-1">A removal plan made from this page does not promise how much space you will save. It re-checks the model you are editing before it changes anything.</div>
        </Banner>
      )}

      {/* Offline overview — model metadata while no live storage scan exists yet (before connect, or pre-scan). */}
      {!report && meta && (
        <>
          <Panel>
            <div className="flex items-baseline gap-6 flex-wrap">
              <div><div className="text-3xl font-bold tnum">{meta.tables.length}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>tables</div></div>
              <div><div className="text-xl font-semibold tnum">{fmtInt(meta.tables.reduce((n, t) => n + t.columns, 0))}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>columns</div></div>
              <div><div className="text-xl font-semibold tnum">{fmtInt(meta.tables.reduce((n, t) => n + t.measures, 0))}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>measures</div></div>
              <div><div className="text-xl font-semibold tnum">{meta.relationships.length}</div><div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>relationships</div></div>
              <div className="flex-1 min-w-[160px] text-[11px]" style={{ color: 'var(--sem-muted)' }}>Choose a test model above for storage size, row counts and encoding.</div>
            </div>
          </Panel>
          <Panel>
            <SectionTitle>Tables <span style={{ color: 'var(--sem-muted)' }}>({meta.tables.length})</span></SectionTitle>
            <div className="mt-1">
              <SortableTable
                rows={meta.tables}
                filterText={(t) => t.name}
                filterPlaceholder="Filter tables…"
                initialSort={{ key: 'columns', dir: -1 }}
                maxHeight={520}
                onRowClick={(t) => selectInProperties('table:' + t.name)}
                onRowFocus={(t) => focusSelectInProperties('table:' + t.name)}
                cols={[
                  { key: 'name', label: 'Table', sortVal: (t) => t.name, render: (t) => <span className="font-medium" style={{ opacity: t.isHidden ? 0.55 : 1 }}>{t.name}</span> },
                  { key: 'columns', label: 'Cols', align: 'right', sortVal: (t) => t.columns, render: (t) => <span className="tnum">{t.columns}</span> },
                  { key: 'measures', label: 'Measures', align: 'right', sortVal: (t) => t.measures, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.measures}</span> },
                  { key: 'kind', label: 'Kind', sortVal: (t) => (t.isDateTable ? 'date' : t.isCalculated ? 'calc' : t.isHidden ? 'hidden' : ''), render: (t) => (
                    t.isDateTable ? <span className="text-[10px] px-1 rounded" style={{ background: 'var(--sem-warn)', color: '#000' }}>DATE</span>
                    : t.isCalculated ? <span className="text-[10px] px-1 rounded" style={{ background: 'var(--sem-good)', color: '#000' }}>CALC</span>
                    : t.isHidden ? <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>hidden</span> : <span /> ) },
                  { key: 'reveal', label: '', align: 'right', sortVal: () => '', render: (t) => <RevealBtn objRef={'table:' + t.name} /> },
                ]}
              />
            </div>
          </Panel>
        </>
      )}

      {report && (
        <>
          {/* Scan status + comparison selector + pin. No deep-scan control ships in this phase. */}
          <Panel>
            <div className="flex items-center gap-3 flex-wrap text-[12px]">
              <span className="px-2 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>Standard scan</span>
              {scanTime && <span style={{ color: 'var(--sem-muted)' }}>Scanned {scanTime}</span>}
              <div className="flex items-center gap-1.5">
                <span style={{ color: 'var(--sem-muted)' }}>Compare</span>
                <select value={compareMode} disabled={!comparisonEnabled} onChange={(e) => setCompareMode(e.target.value as 'previous' | 'baseline')}
                  className="text-[12px] px-1.5 py-0.5 rounded-md outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
                  <option value="previous">Previous scan</option>
                  {pinnedSnap && <option value="baseline">Pinned baseline</option>}
                </select>
                {!comparison && comparisonEnabled && <span style={{ color: 'var(--sem-muted)' }}>· no comparison yet</span>}
                {!comparisonEnabled && storageMode === 'unknown' && <span style={{ color: 'var(--sem-muted)' }}>· comparison unavailable until storage mode is confirmed</span>}
                {!comparisonEnabled && !scanCurrent && <span style={{ color: 'var(--sem-muted)' }}>· comparison unavailable because this scan is no longer the current query target</span>}
                {comparison && !decision.available && decision.reason === 'fingerprint' && (
                  <span style={{ color: 'var(--sem-muted)' }}>· comparison unavailable: the model identity for one of the scans could not be confirmed</span>
                )}
                {compare?.structureChanged && (
                  <span className="text-[11px] px-1.5 py-0.5 rounded-md" style={{ background: 'color-mix(in srgb, var(--sem-warn) 14%, transparent)', color: 'var(--sem-warn)' }}
                    title={`The model's structure changed between the ${comparisonLabel} and this scan (often the very fix you applied). The storage change is still measured; read it as before-vs-after the structural change.`}>
                    Model structure changed since this scan
                  </span>
                )}
              </div>
              {/* Pin waits for BOTH: the enrichment reads (a snapshot pinned before listColumns/unusedObjects
                  settle would freeze an incomplete finding set) and a confirmed identity (an unconfirmed
                  fingerprint can never anchor a comparison — compareDecision suppresses on null). */}
              <div className="ml-auto"><Button onClick={pinBaseline} disabled={!pinReady}
                title={pinReady
                  ? 'Freeze this scan as the comparison point for a clean-up campaign'
                  : storageMode === 'unknown'
                    ? 'Pinning is unavailable until storage mode is confirmed'
                    : 'Pinning needs the scan enrichment (columns and usage) to finish, a confirmed model identity, and the current query target'}>
                Pin current as baseline</Button></div>
            </div>
          </Panel>

          {/* Known column storage + composition (data / dictionary / hash indexes). The TOTAL delta is always
              valid; component deltas render ONLY when both scans share a coverage basis (compareDecision), else
              they would compare different column populations and the "change" would be an artifact. */}
          <Panel>
            <div className="flex items-start gap-8 flex-wrap">
              <div className="min-w-[150px]">
                <div className="text-3xl font-bold tnum">{fmtMB(comp.known)}</div>
                <div className="text-[11px] uppercase tracking-wide mt-0.5" style={{ color: 'var(--sem-muted)' }}>{knownLabel}</div>
                {compare && <KnownDelta cur={comp.known} prev={comparison?.known ?? null} label={comparisonLabel} />}
              </div>
              <CompCard label="Data" color={VPAQ_COMPONENT_COLORS.data} cur={comp.data} prev={compare?.componentsComparable ? comparison?.data ?? null : null} />
              <CompCard label="Dictionary" color={VPAQ_COMPONENT_COLORS.dict} cur={comp.dict} prev={compare?.componentsComparable ? comparison?.dict ?? null : null} />
              <CompCard label="Hash indexes" color={VPAQ_COMPONENT_COLORS.hash} cur={comp.hash} prev={compare?.componentsComparable ? comparison?.hash ?? null : null} />
            </div>
            {compare && !compare.componentsComparable && (
              <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Coverage differs between the two scans, so the component change is not comparable; only the total is.</div>
            )}
            {compare && storageMode === 'directLake' && (
              <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-warn)' }}>Direct Lake: both scans measured only what was resident in memory at the time, so a change can reflect cache residency rather than storage.</div>
            )}
            <div className="mt-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
              {report.tables.length} tables · {report.columnCount} columns · {storageMode === 'import' ? fmtInt(totalRows) + ' rows' : storageMode === 'directLake' ? 'rows resident-only' : 'row count unavailable until storage mode is confirmed'}
              {largest ? ` · largest: ${largest.name} (${largest.pctOfModel}%)` : ''}
              {comp.attributed !== comp.known && <> · composition covers {fmtMB(comp.attributed)} of {fmtMB(comp.known)} (the {report.topColumns.length} largest columns)</>}
            </div>
          </Panel>

          {/* Ranked component bars + the first-screen top-three opportunities. */}
          <div className="flex gap-4 items-stretch flex-wrap xl:flex-nowrap">
            <Panel className="flex-1 min-w-[320px]">
              <div className="flex items-center gap-2 flex-wrap">
                <SectionTitle>{linkedCopyObservation ? 'Top query-copy observations' : 'Top storage consumers'}</SectionTitle>
                <div className="flex items-center gap-1 ml-1">
                  <Chip active={barMode === 'columns'} onClick={() => setBarMode('columns')}>Columns</Chip>
                  <Chip active={barMode === 'tables'} onClick={() => setBarMode('tables')}>Tables</Chip>
                </div>
                <div className="ml-auto text-[10px] flex gap-2.5" style={{ color: 'var(--sem-muted)' }}>
                  <span><span style={{ color: VPAQ_COMPONENT_COLORS.data }}>■</span> Data</span>
                  <span><span style={{ color: VPAQ_COMPONENT_COLORS.dict }}>■</span> Dictionary</span>
                  <span><span style={{ color: VPAQ_COMPONENT_COLORS.hash }}>■</span> Hash indexes</span>
                  {barMode === 'tables' && barItems.some((b) => (b.unattributed ?? 0) > 0) && (
                    <span><span style={{ color: VPAQ_UNATTRIBUTED_COLOR }}>■</span> Unattributed</span>
                  )}
                </div>
              </div>
              {barMode === 'tables' && (
                <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{storageMode === 'unknown' ? 'Table values are observed storage only; components come from the scanned columns and the rest is unattributed.' : 'Table totals are exact for this storage mode; components are estimated from the scanned columns and the rest is unattributed.'}</div>
              )}
              <div className="mt-2"><VpaqComponentBars items={barItems} storageMode={storageMode} onSelect={onBarSelect} /></div>
            </Panel>
            <Panel className="w-full xl:w-[264px] shrink-0">
              <SectionTitle>Top opportunities</SectionTitle>
              {topOpps.length === 0
                ? <div className="text-[12px] mt-2" style={{ color: 'var(--sem-good)' }}>Nothing flagged. ✓</div>
                : <div className="mt-2 flex flex-col gap-2">
                    {topOpps.map((o, i) => (
                      <button key={o.ruleId + o.ref} onClick={() => oppsPanelRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
                        className="text-left rounded-lg p-2 hover:bg-[var(--sem-surface-2)]" style={{ border: '1px solid var(--sem-border)' }}>
                        <div className="text-[12px] font-medium truncate">{i + 1}. {o.name}</div>
                        <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{o.ruleName}{o.bytes > 0 ? ` · ${linkedCopyObservation ? `${fmtMB(o.bytes)} query-copy observation` : storageMode === 'directLake' ? `${fmtMB(o.bytes)} resident` : storageMode === 'unknown' ? `${fmtMB(o.bytes)} observed` : fmtMB(o.bytes)}` : ''}</div>
                      </button>
                    ))}
                  </div>}
              <button onClick={() => oppsPanelRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
                className="mt-2 text-[11px]" style={{ color: 'var(--sem-accent)' }}>View all opportunities →</button>
            </Panel>
          </div>

          {/* The full grouped opportunities on the house findings renderer. */}
          <Panel>
            <div ref={oppsPanelRef} className="scroll-mt-2" />
            <div className="flex items-center gap-2">
              <SectionTitle>Opportunities <span style={{ color: 'var(--sem-muted)' }}>({opps.length})</span></SectionTitle>
              {removalPlanCandidates.length > 0 && onReviewAsPlan && (
                <Button disabled={!reverifiedPlanAllowed} onClick={() => void reviewAsPlanItems(removalPlanCandidates)}
                  title={!reverifiedPlanAllowed ? planBlockedReason ?? undefined : linkedCopyObservation || storageMode === 'unknown'
                    ? `Adds ${removalPlanCandidates.length} removal item(s) without a storage reduction estimate, then opens the plan for review`
                    : `Adds ${removalPlanCandidates.length} removal item(s) with their byte evidence to the change plan, then opens it for review`}>
                  Review as a plan →
                </Button>
              )}
            </div>
            {unused === null && <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-warn)' }}>Usage could not be determined (the safe-to-remove check did not run), so removal actions are disabled.</div>}
            {editingBlockedReason && <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-warn)' }}>{editingBlockedReason} Query-copy bytes are not joined to editing objects.</div>}
            <div className="mt-2">
              {opps.length === 0
                ? <div className="text-[12px]" style={{ color: 'var(--sem-good)' }}>Nothing the scan justifies acting on. ✓</div>
                : <GroupedFindings rows={opps.map(oppToRow)} categoryOrder={OPP_GROUP_ORDER}
                    renderActions={(r) => { const o = opps.find((x) => x.ruleId === r.ruleId && x.ref === r.objectRef); return o
                      ? <OppActions o={o} reachable={oppReachable(o)} usageKnown={unused !== null} ambiguous={refIsAmbiguous(o.table, o.column)}
                          editingAllowed={editingEvidenceAllowed} editingBlockedReason={editingBlockedReason ?? undefined}
                          planAllowed={reverifiedPlanAllowed} planBlockedReason={planBlockedReason ?? undefined}
                          onAskAi={askAi} onDoNotSummarize={doNotSummarize} onHide={hideColumn} onDelete={deleteColumn}
                          onChangeType={(ref) => selectInProperties(ref)} onReviewAsPlan={onReviewAsPlan ? () => void reviewAsPlanItems([o]) : undefined} />
                      : null; }} />}
            </div>
            {changed && <ChangedSince changed={changed} label={comparisonLabel} />}
          </Panel>

          {/* Explorer: the full sortable tables + columns lists for pro users. */}
          <Panel>
            <SectionTitle>Tables <span style={{ color: 'var(--sem-muted)' }}>({report.tables.length})</span></SectionTitle>
            <div className="mt-1">
              <SortableTable
                rows={report.tables}
                filterText={(t) => t.name}
                filterPlaceholder="Filter tables…"
                initialSort={{ key: 'size', dir: -1 }}
                onRowClick={(t) => tableReachable(t.name) && selectInProperties('table:' + t.name)}
                onRowFocus={(t) => tableReachable(t.name) && focusSelectInProperties('table:' + t.name)}
                cols={[
                  { key: 'name', label: 'Table', sortVal: (t) => t.name, render: (t) => <span className="font-medium">{t.name}</span> },
                  { key: 'rows', label: 'Rows', align: 'right', sortVal: (t) => t.rows ?? -1, render: (t) => <span className="tnum">{t.rows == null ? 'Not available' : fmtInt(t.rows)}</span> },
                  { key: 'columns', label: 'Cols', align: 'right', sortVal: (t) => t.columns ?? 0, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.columns ?? 'Not available'}</span> },
                  { key: 'size', label: sizeLabel, align: 'right', sortVal: (t) => t.size, render: (t) => <SizeCell bytes={t.size} max={maxTableSize} /> },
                  { key: 'pctOfModel', label: modelShareLabel, align: 'right', sortVal: (t) => t.pctOfModel, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.pctOfModel}%</span> },
                  { key: 'reveal', label: '', align: 'right', sortVal: () => '', render: (t) => tableReachable(t.name) ? <RevealBtn objRef={'table:' + t.name} /> : null },
                ]}
              />
            </div>
          </Panel>
          <Panel>
            <div className="flex items-center gap-2">
              <SectionTitle>Columns <span style={{ color: 'var(--sem-muted)' }}>({report.topColumns.length})</span></SectionTitle>
              {explorerFilter && <button onClick={() => setExplorerFilter('')} className="text-[11px] px-1.5 py-0.5 rounded-md" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' }}>Filtered to {explorerFilter} ✕</button>}
            </div>
            <div className="mt-1">
              <SortableTable
                rows={explorerFilter ? report.topColumns.filter((c) => c.table === explorerFilter) : report.topColumns}
                filterText={(c) => c.table + ' ' + c.column}
                filterPlaceholder="Filter columns…"
                initialSort={{ key: 'totalSize', dir: -1 }}
                maxHeight={460}
                onRowClick={(c) => columnReachable(c.table, c.column) && selectInProperties('column:' + c.table + '/' + c.column)}
                onRowFocus={(c) => columnReachable(c.table, c.column) && focusSelectInProperties('column:' + c.table + '/' + c.column)}
                cols={[
                  { key: 'column', label: 'Column', sortVal: (c) => c.column, render: (c) => <span><span className="font-medium">{c.column}</span> <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>· {c.table}</span></span> },
                  { key: 'encoding', label: 'Encoding', sortVal: (c) => c.encoding, render: (c) => <span className="text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: ENC_COLOR[c.encoding] || 'var(--sem-muted)' }}>{c.encoding}</span> },
                  { key: 'totalSize', label: linkedCopyObservation ? 'Query-copy bytes' : 'Total', align: 'right', sortVal: (c) => c.totalSize, render: (c) => <SizeCell bytes={c.totalSize} max={maxColSize} /> },
                  { key: 'dataSize', label: 'Data', align: 'right', sortVal: (c) => c.dataSize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dataSize != null ? fmtMB(c.dataSize) : 'Not available'}</span> },
                  { key: 'dictionarySize', label: 'Dict', align: 'right', sortVal: (c) => c.dictionarySize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dictionarySize != null ? fmtMB(c.dictionarySize) : 'Not available'}</span> },
                  { key: 'hashIndexSize', label: 'Index', align: 'right', sortVal: (c) => c.hashIndexSize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.hashIndexSize != null ? fmtMB(c.hashIndexSize) : 'Not available'}</span> },
                  { key: 'pctOfModel', label: modelShareLabel, align: 'right', sortVal: (c) => c.pctOfModel, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.pctOfModel}%</span> },
                  { key: 'pctTable', label: tableShareLabel, align: 'right', sortVal: (c) => (tableSizeByName.get(c.table) ? c.totalSize / (tableSizeByName.get(c.table) as number) : 0), render: (c) => { const ts = tableSizeByName.get(c.table) ?? 0; return <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{ts > 0 ? Math.round((100 * c.totalSize) / ts) + '%' : '-'}</span>; } },
                  { key: 'reveal', label: '', align: 'right', sortVal: () => '', render: (c) => columnReachable(c.table, c.column) ? <RevealBtn objRef={'column:' + c.table + '/' + c.column} /> : null },
                ]}
              />
            </div>
          </Panel>
        </>
      )}
    </div>
  );
}

// The "Known column storage" measured-change caption. Causal-honest: a smaller scan is a MEASURED decrease, never
// "recovered" or "saved" — a refresh, cache residency, or another writer could equally explain it.
function KnownDelta({ cur, prev, label }: { cur: number; prev: number | null; label: string }) {
  if (prev == null) return null;
  const d = cur - prev;
  if (d === 0) return <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>no measured change since {label}</div>;
  const dec = d < 0;
  return <div className="text-[11px] mt-0.5" style={{ color: dec ? 'var(--sem-good)' : 'var(--sem-warn)' }}>{fmtMB(Math.abs(d))} measured {dec ? 'decrease' : 'increase'} since {label}</div>;
}

// One composition card (Data / Dictionary / Hash indexes) with a compact signed delta vs the comparison.
function CompCard({ label, color, cur, prev }: { label: string; color: string; cur: number; prev: number | null }) {
  const d = prev == null ? null : cur - prev;
  return (
    <div className="min-w-[110px]">
      <div className="text-[11px] uppercase tracking-wide flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
        <span style={{ color }}>■</span>{label}
      </div>
      <div className="text-xl font-semibold tnum mt-0.5">{fmtMB(cur)}</div>
      {d != null && d !== 0 && <div className="text-[11px] tnum" style={{ color: d < 0 ? 'var(--sem-good)' : 'var(--sem-warn)' }} title={`measured ${d < 0 ? 'decrease' : 'increase'} since the comparison`}>{d < 0 ? '−' : '+'}{fmtMB(Math.abs(d))}</div>}
      {d === 0 && <div className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>no change</div>}
    </div>
  );
}

// The action set + effect label for one opportunity. Every mutating action carries an honest storage-effect label,
// and destructive removal is gated on a SAFE usage verdict (fail closed when usage is Unknown) AND an unambiguous
// name: '/' is legal inside table and column names but the ref grammar splits on the first slash, so a slash-bearing
// name could resolve to a DIFFERENT object than the one the finding measured — Delete and the plan hand-off both
// refuse rather than guess.
function OppActions({ o, reachable, usageKnown, ambiguous, editingAllowed, editingBlockedReason, planAllowed, planBlockedReason, onAskAi, onDoNotSummarize, onHide, onDelete, onChangeType, onReviewAsPlan }: {
  o: Opp; reachable: boolean; usageKnown: boolean; ambiguous: boolean; editingAllowed: boolean; editingBlockedReason?: string; planAllowed: boolean; planBlockedReason?: string;
  onAskAi: (o: Opp) => void; onDoNotSummarize: (o: Opp) => void; onHide: (o: Opp) => void; onDelete: (o: Opp) => void;
  onChangeType: (ref: string) => void; onReviewAsPlan?: () => void;
}) {
  const [asked, setAsked] = useState(false);
  const ask = () => { onAskAi(o); setAsked(true); window.setTimeout(() => setAsked(false), 1600); };
  const ambiguousTip = "This name contains '/', which is ambiguous for safe deletion. Rename it first, or delete it from the Model tree.";
  const blockedTip = editingBlockedReason ?? 'This action needs a scan that is proven to describe the open editing model.';
  const unavailableTip = !editingAllowed ? blockedTip : !reachable ? 'The scanned object could not be confirmed in the open editing model.' : undefined;
  return (
    <div className="flex flex-col items-end gap-1">
      {o.effect && <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{o.effect}</span>}
      <div className="flex items-center gap-1 flex-wrap justify-end">
        {o.kind === 'unused' && <DeleteConfirm disabled={!editingAllowed || !reachable || !usageKnown || ambiguous} disabledTitle={!editingAllowed ? blockedTip : ambiguous ? ambiguousTip : unavailableTip} onConfirm={() => onDelete(o)} />}
        {o.kind === 'unused' && onReviewAsPlan && <MiniButton disabled={!planAllowed || ambiguous} title={!planAllowed ? planBlockedReason : ambiguous ? ambiguousTip : undefined} onClick={onReviewAsPlan}>Review as a plan</MiniButton>}
        {o.kind === 'dict' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onChangeType(o.ref)}>Change data type…</MiniButton>}
        {o.kind === 'summarize' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onDoNotSummarize(o)}>Do not summarize</MiniButton>}
        {o.kind === 'summarize' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onHide(o)}>Hide from authors</MiniButton>}
        <MiniButton disabled={!editingAllowed} title={!editingAllowed ? blockedTip : undefined} onClick={ask}>{asked ? 'Copied ✓' : 'Ask AI'}</MiniButton>
        {reachable ? <RevealBtn objRef={o.ref} /> : !editingAllowed ? <MiniButton disabled title={blockedTip}>Show in model</MiniButton> : null}
      </div>
    </div>
  );
}

// A destructive delete needs a deliberate second click (no native confirm dialog in a webview). Disabled when the
// object was renamed out of the staged model, usage is Unknown, or the name is slash-ambiguous.
function DeleteConfirm({ disabled, disabledTitle, onConfirm }: { disabled?: boolean; disabledTitle?: string; onConfirm: () => void }) {
  const [armed, setArmed] = useState(false);
  if (disabled) return <MiniButton disabled title={disabledTitle ?? 'Removal needs a confirmed safe-to-remove verdict on a resolvable object'}>Delete…</MiniButton>;
  if (!armed) return <MiniButton onClick={() => setArmed(true)}>Delete…</MiniButton>;
  return (
    <span className="flex items-center gap-1">
      <button onClick={() => { setArmed(false); onConfirm(); }} className="text-[11px] px-2 py-0.5 rounded-md font-medium" style={{ background: 'var(--sem-bad)', color: '#fff' }}>Confirm delete</button>
      <MiniButton onClick={() => setArmed(false)}>Cancel</MiniButton>
    </span>
  );
}

// The collapsed "Changed since comparison" group — which findings appeared or resolved against the comparison point.
// "Introduced" (appeared since) and "Resolved" rows exist only when the relevant side had FULL coverage; under a
// partial prior scan a finding now visible is labeled "Newly observed" (present now, not provably new — it may have
// been out of the earlier top-N window), and a finding that left the population needs full coverage on both sides to
// count as resolved. So the group states what the evidence supports, never a rank shift dressed up as a change.
function ChangedSince({ changed, label }: { changed: { introduced: Opp[]; newlyObserved: Opp[]; resolved: string[]; resolvedSuppressed: boolean }; label: string }) {
  const [open, setOpen] = useState(false);
  const refName = (k: string) => { const ref = k.split('|')[1] ?? ''; const m = /^column:(.+)\/([^/]+)$/.exec(ref); return m ? `${m[1]}[${m[2]}]` : ref.replace(/^\w+:/, ''); };
  const count = changed.introduced.length + changed.newlyObserved.length + changed.resolved.length;
  return (
    <div className="mt-2 rounded-lg border p-2" style={{ borderColor: 'var(--sem-border)' }}>
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 text-[11px] uppercase tracking-wide font-semibold w-full" style={{ color: 'var(--sem-muted)' }}>
        <span className="inline-block w-3 text-[10px]" style={{ transform: open ? 'none' : 'rotate(-90deg)' }}>▾</span>
        Changed since {label}<span className="opacity-70">({count})</span>
      </button>
      {open && (
        <div className="mt-1 flex flex-col gap-1 pl-4">
          {changed.introduced.map((o) => <div key={'i' + o.ruleId + o.ref} className="text-[11px]"><span style={{ color: 'var(--sem-warn)' }}>Introduced</span> · {o.name} · {o.ruleName}</div>)}
          {changed.newlyObserved.map((o) => <div key={'n' + o.ruleId + o.ref} className="text-[11px]"><span style={{ color: 'var(--sem-muted)' }}>Newly observed</span> · {o.name} · {o.ruleName}</div>)}
          {changed.newlyObserved.length > 0 && (
            <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
              The previous scan covered only its top columns, so these cannot be confirmed as introduced since; a full-coverage scan on both sides would tell them apart.
            </div>
          )}
          {changed.resolved.map((k) => <div key={'r' + k} className="text-[11px]"><span style={{ color: 'var(--sem-good)' }}>Resolved</span> · {refName(k)}</div>)}
          {changed.resolvedSuppressed && (
            <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
              Some findings left the scanned population, but resolved findings need a full-coverage scan on both sides: a finding can drop out of a top-N scan without being fixed.
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// A small relative-size cell: the value + a proportional bar (max = the largest in the set).
function SizeCell({ bytes, max }: { bytes: number; max: number }) {
  return (
    <div className="flex items-center gap-2 justify-end">
      <span className="tnum">{fmtMB(bytes)}</span>
      <div className="w-14 h-1.5 rounded overflow-hidden shrink-0" style={{ background: 'var(--sem-surface-2)' }}>
        <div className="h-full rounded" style={{ width: Math.max(2, Math.round((100 * bytes) / max)) + '%', background: 'var(--sem-accent)' }} />
      </div>
    </div>
  );
}

// Type-aware comparator (numbers numeric; text natural). Nulls last.
function cmpVal(a: number | string, b: number | string): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
}

interface SortCol<T> { key: string; label: string; align?: 'left' | 'right'; sortVal: (r: T) => number | string; render: (r: T) => React.ReactNode; }
// A compact sortable + filterable table (non-virtualized — for the bounded stats lists). Click a header to sort
// (asc → desc), type the filter to narrow, click a row to show the object in the Properties view (the selection
// bus — no focus steal); each row's ⤢ jumps to the Model tree.
function SortableTable<T>({ rows, cols, initialSort, filterText, filterPlaceholder, onRowClick, onRowFocus, maxHeight }: {
  rows: T[]; cols: SortCol<T>[]; initialSort: { key: string; dir: 1 | -1 };
  filterText?: (r: T) => string; filterPlaceholder?: string; onRowClick?: (r: T) => void; onRowFocus?: (r: T) => void; maxHeight?: number;
}) {
  const [sort, setSort] = useState(initialSort);
  const [q, setQ] = useState('');
  const view = useMemo(() => {
    let r = rows;
    const needle = q.trim().toLowerCase();
    if (needle && filterText) r = r.filter((x) => filterText(x).toLowerCase().includes(needle));
    const c = cols.find((c) => c.key === sort.key);
    if (c) r = [...r].sort((a, b) => cmpVal(c.sortVal(a), c.sortVal(b)) * sort.dir);
    return r;
  }, [rows, q, sort, cols, filterText]);
  const onHeader = (key: string) => setSort((s) => (s.key === key ? { key, dir: (s.dir === 1 ? -1 : 1) as 1 | -1 } : { key, dir: -1 }));

  return (
    <div>
      {filterText && (
        <div className="flex items-center gap-2 pb-1.5">
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={filterPlaceholder || 'Filter…'} spellCheck={false}
            className="text-[12px] px-2 py-1 rounded-md outline-none w-56" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{view.length === rows.length ? `${rows.length}` : `${view.length} of ${rows.length}`}</span>
        </div>
      )}
      <div className="overflow-auto" style={maxHeight ? { maxHeight } : undefined}>
        <table className="w-full border-collapse">
          <thead className="sticky top-0 z-10" style={{ background: 'var(--sem-surface)' }}>
            <tr>
              {cols.map((c) => {
                const active = sort.key === c.key;
                return (
                  <th key={c.key} onClick={() => onHeader(c.key)}
                    className={'text-[11px] font-semibold px-2 py-1 cursor-pointer select-none ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ color: active ? 'var(--sem-accent)' : 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)', whiteSpace: 'nowrap' }}>
                    {c.label}{active && <span className="text-[9px] ml-1">{sort.dir === 1 ? '▲' : '▼'}</span>}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {view.map((r, i) => (
              // Click ACTIVATES (deduped bus post); focus SELECTS via the always-posts focus helper (onRowFocus) so a
              // mouse focus doesn't double-post and a keyboard walk still feeds Properties. onRowFocus falls back to
              // onRowClick for any table that wires only a click.
              <tr key={i} onClick={onRowClick ? () => onRowClick(r) : undefined}
                {...((onRowClick || onRowFocus) ? rowKeyProps(() => onRowClick?.(r), () => (onRowFocus ?? onRowClick)?.(r)) : {})}
                className={(onRowClick || onRowFocus) ? 'cursor-pointer hover:bg-[var(--sem-surface-2)]' : ''}>
                {cols.map((c) => (
                  <td key={c.key} className={'text-[12px] px-2 py-1.5 ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                    {c.render(r)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function prettyKey(k: string) {
  return k.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase()).replace('With', 'with').trim();
}
function shortRef(r: string) { const c = r.indexOf(':'); return c >= 0 ? r.slice(c + 1) : r; }
