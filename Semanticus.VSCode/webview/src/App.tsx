import { Component, createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, lazy, Suspense, type ErrorInfo } from 'react';
import { publishStageName } from './publishcopy';
import { StudioLayer } from './studio/layer';
import { rpc, onDidChange, onAssistantCall, onPlanChange, onReconnect, onActivity, onNavigate, dispatchNavigate, type NavigateMessage, onTreeSelection, onOpenConnections, onWorkflowChange, onStudioZoom, onEngineState, onReconnectPending, postStudioZoom, signalReady, selectInProperties, focusSelectInProperties, focusModelTree, runHostCommand, loadState, saveState, type ChangeNotification, revealInTree } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';
import { ShortcutsOverlay, tabForKey, isTypingTarget, hostCommandForKey } from './shortcuts';
import { FirstRun } from './firstrun';
import { ActivityProvider, LiveActivity, ClaudeRanBanner, useClaudeReflection, KIND_TAB, type ActivityEvent } from './activity';
import { SaidLine, noteAssistantChange } from './studio/said-line';
import { useFixState } from './hooks';
import { useConnection, ConnectBar, type SessionInfo } from './connection';
import { ContextBar, compareSeedFromSession, QueryStalenessChip } from './contextbar';
import { FileRecoveryOffer } from './file-recovery';
import { ConnectionsHub, openConnectionsOnCreate, openConnectionsOnView, type HubView } from './connectionshub';
import type { ModelRef } from './compare';
import { adviceFor, biggestShare, shareAdvice, shareWords, verdictWords } from './storage-share';
import { useDriver } from './studio/driver-store';
import { VpaqComponentBars, VPAQ_COMPONENT_COLORS, VPAQ_UNATTRIBUTED_COLOR, Sparkline, type VpaqColumn, type VpaqTable, type VpaqBarItem } from './echart';
import { DiagramView } from './diagram';
import type { ModelGraph } from './diagram';
import { AdvancedModelsView } from './advmodels';
import { RowMenu } from './toolrow';
import { GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { useFeature, useLockedTools, ProBadge, PENDING_ACCESS } from './pro';
import { featureOfTool } from './features';
import { ProPreview } from './propreview';
import { ProChip, ProPage } from './propage';
import { TrialEndingLine } from './trialline';
import { DaxModelProvider } from './daxeditor';
import { DaxLabTabStateProvider, useDaxLabBusy } from './daxlab';
import { QueryView } from './query';
import { LineageTabStateProvider, LineageView, useLineageReportsBusy } from './lineage';
import { DataPreviewView } from './datapreview';
import { BpaView } from './bpa';
// Pending changes and the Model Spec are PARTS of the Plan page from 23 September, so the shell no
// longer mounts either of them: plan.tsx owns both, one as a section and one as a panel.
import { DocumentationView } from './documentation';
import { CopilotTestView } from './copilot-test';
import { DeployView } from './deploy';
import { usePublishReach } from './publishReach';
import { TestsView } from './tests';
import { HelpButton } from './help';
import { WorkflowsView, type WorkflowRunView } from './workflows';
import { emptyRuns, reduceRuns, liveRuns, mostRecentLive, runById, isRunNotFound, type RunMapState, type FoldOpts } from './workflowruns.mjs';
import { anchorOf, normalizeStorageMode, snapKey, scanMatchesAnchor, scanUsableDuringTransition, storageEvidenceLevel, relationshipAllowsStorageEdits, relationshipAllowsReverifiedDeletePlan, compareDecision, shouldStoreAsLast, resolvedClaimAllowed, introducedClaimAllowed, refIsAmbiguous, type StorageMode, type StorageEvidenceLevel } from './storagecalc.mjs';
import { busyAffordance } from './tabbusy.mjs';
import { linkWork } from './personality/link-state';
import { wirePersonality } from './personality/link';
import { onPlanRunProgress, onProgress, postToHost } from './bridge';
import { plainRuleName, plainCategory, plainGate } from './rule-words';

// The personality (1.5): The Link hears the real work, and the Personality and Sounds switches write through the host.
wirePersonality({ onAssistantCall, onActivity, onProgress, onPlanRunProgress, postToHost });
import { AiInstructionsPage, type InstructionsTab } from './instructions';
import { PermissionsView, permissionPresetDescription, publishPermissionDescription, type AgentPolicy } from './permissions';
import { CustomRulesPanel } from './rulesauthor';
import { GiveOrOnPlan, GiveToAssistant, clampWords, findingBuilder, notOnPlan, storageTask, usePlanRowsSeen, type PlanTaskRequest } from './sendtask';
import { PlanView } from './plan';
import { keepPlanThroughOutage, useAnyWordsDraft } from './startplan-drafts';
import { modelKeyOfSession, setDraftModel } from './drafts';
import { OtherModelWords } from './othermodels';
import { assistantChipCount, type Plan as PlanModel, type PlanDoer } from './plan-model';
import { AskBar, isAskKey } from './askbox';
import { draftRowId } from './composer-model';
import { ProDecisionsHost } from './decisions-settings';
import { band } from './readinessband.mjs';
import { SCAN_PAGE_OF_TOOL } from './studio/checks-run';
import { GradeFlip, BigWin } from './personality/motion';
import { RagBars } from './ragbars';
import { AREA_ALIASES, DESTINATION_OF_TOOL, PLAN_START_TARGET, SQL_DRAFT_TARGET, areaHomeTool, landingOf, objectLabel, objectToRef, refToObject, tableOfObject, type Area, type Route, type Seed, type ToolId } from './route';

// M Code is the heaviest tab (the @microsoft/powerquery-* parser/formatter/language-services + the 866-symbol
// M standard-library dataset — ~1.7 MB). Lazy-load it so that whole cluster lands in its own chunk and only
// downloads when the user opens the tab, keeping the rest of Studio's startup lean.

// ---- wire types (camelCase, from the engine) -------------------------------------------------
interface CategoryScore { category: string; score: number; weight: number; applicable: number; violations: number; waived?: number; hasRules: boolean; }
interface Finding { ruleId: string; ruleTitle: string; category: string; severity: string; fix: string; objectRef: string; objectName: string; message: string; displayMessage?: string; custom?: boolean; waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean; noLongerFires?: boolean; waivedAsTwin?: boolean; coveredBy?: string; coveredReason?: string; wholeRuleRow?: boolean; }
interface Scorecard {
  overall: number; rawOverall: number; grade: string; gatedBy: string[];
  categories: CategoryScore[]; coverage: Record<string, number>; findings: Finding[]; safeFixCount: number; waivedCount?: number;
  caveat?: string;   // set when the score may be incomplete (e.g. Direct Lake live cardinality is resident-only)
  ruleErrors?: string[];   // custom-rule problems surfaced loudly (unparseable annotation, an eval error)
}
interface ApprovalNotice { id: string; grantedUtc?: string | null; }
// SessionInfo now lives in connection.tsx (the single shared shape, mirroring Protocol.cs) — imported above.

const FIX_STYLE: Record<string, { label: string; color: string }> = {
  SafeFix: { label: 'Safe fix', color: 'var(--sem-good)' },
  AiContent: { label: 'AI', color: 'var(--sem-accent)' },
  Proposal: { label: 'Review', color: 'var(--sem-muted)' },
  None: { label: 'Info', color: 'var(--sem-muted)' },
};

type StudioTab = ToolId;

// Stable tool ids remain the compatibility API. The SIX buttons describe where a journey starts, not
// every capability reachable from there (Kane's tree, 22 September 2026, docs/redesign/ui-hierarchy.md).
// There were seven until slice 11 retired the Overview and the Edit History tab (Plan-heart lanes R1 and
// R2): the Plan is the first area and the start page, and History is a view inside it. Both old ids still
// land, through route.ts's landingOf.
// A utility destination hosts Settings without becoming an eighth area.
//
// These label strings are a CONTRACT, not decoration: the engine's ask box offers a place by
// the name the tab actually has, and test/ask-box.test.mjs asserts every engine label equals the strip
// label. A place that is a TAB inside another page is not in here at all: the engine flags it `isTab` and
// the box reads its name off the catalogue instead. Reports and the Notes tab are the two of those today.
// Keep the one-line id-then-label shape too: that test and Semanticus.Tests/AskRouteTests.cs both
// parse this literal as TEXT, so a tool entry split over two lines disappears from both of them. (This
// comment deliberately shows no example of the shape: a sample entry up here is indistinguishable from a
// real one to every reader that scans the file rather than the literal.)
const TAB_GROUPS: { id: Area; label: string; tabs: { id: StudioTab; label: string }[] }[] = [
  { id: 'plan', label: 'Plan', tabs: [
    { id: 'plan', label: 'Plan' },
  ] },
  { id: 'explore', label: 'Explore', tabs: [
    { id: 'data', label: 'Data' }, { id: 'query', label: 'Query' }, { id: 'diagram', label: 'Diagram' }, { id: 'lineage', label: 'Lineage' }, { id: 'stats', label: 'Size' },
  ] },
  { id: 'edit', label: 'Edit', tabs: [
    { id: 'instructions', label: 'AI instructions' }, { id: 'advmodels', label: 'Advanced modelling' }, { id: 'mcode', label: 'Power Query' },
  ] },
  { id: 'review', label: 'Review', tabs: [
    { id: 'bpa', label: 'Model quality (BPA)' }, { id: 'readiness', label: 'AI readiness' }, { id: 'copilottest', label: 'Copilot test' }, { id: 'tests', label: 'Validation' }, { id: 'docs', label: 'Docs' },
  ] },
  { id: 'publish', label: 'Publish', tabs: [
    { id: 'deploy', label: 'Published' },
  ] },
  { id: 'workflows', label: 'Workflows', tabs: [
    { id: 'workflows', label: 'Workflows' },
  ] },
];
// The Edit row is a plain strip: AI instructions and Advanced modelling (Queries moved to Explore > Query in 1.6). Advanced
// modelling is one tab, not a dropdown (Kane, 8 October 2026). The page it opens has its own row of buttons, and
// that one row (AREA_TABS in advmodels.tsx) lists Power Query together with the six builders, so there is a single list.
// Power Query keeps its own tool id (`mcode`: the ask box, the Plan and Edit M Code all land on it, and the engine
// names it), but it has no segment on the Edit row. It lights Advanced modelling while it is open.
// Model notes was a segment until 23 September; it is the Notes tab inside AI instructions now, so `knowledge` is
// a route with no segment of its own and the AI instructions segment stays lit on it.
const STRIP_HIDDEN_TABS: StudioTab[] = ['mcode'];
const TAB_TO_GROUP: Record<string, Area> = Object.fromEntries(TAB_GROUPS.flatMap((g) => g.tabs.map((t) => [t.id, g.id])));
TAB_TO_GROUP.compare = 'publish'; TAB_TO_GROUP.dataagent = 'publish';
// Pending changes and Model Spec left the strip on 23 September and became parts of the Plan page
// (Kane: "No point in three similar pages"). The IDS stay real routes, the way `evidence` does: every
// "Review these together" button, every saved page and every deep link still points at them.
TAB_TO_GROUP.optimize = 'plan'; TAB_TO_GROUP.spec = 'plan';
// Reports left the strip and became a tab inside Validation. The id stays a real route so an old link, a
// saved page and the Reports button on Validation all still work; it simply opens Validation on that tab.
TAB_TO_GROUP.evidence = 'review';
// And Model notes did the same on 23 September: it is the Notes tab inside AI instructions now. `knowledge`
// stays a real route for the deep link, the ask box, the Plan and every saved page that still names it.
TAB_TO_GROUP.knowledge = 'edit';
// Workflows retired for everyone (Kane, 17 Oct): the page and its code stay, but no strip, menu or list offers it. TAB_GROUPS
// keeps the entry because the route table, the quick pick and AskRouteTests still read it as text.
const RETIRED_AREAS: Area[] = ['workflows'];
const SHOWN_GROUPS = TAB_GROUPS.filter((g) => !RETIRED_AREAS.includes(g.id));
const CYCLE_TABS: StudioTab[] = SHOWN_GROUPS.flatMap((g) => g.tabs.map((t) => t.id));
// The Plan is where a person starts (Plan at the heart, Kane 24 Sep). Every older area word, the Overview's
// among them, resolves through route.ts's AREA_ALIASES.
const FALLBACK_TAB: StudioTab = 'plan';
// The exact sentence the host relay answers with when it holds no engine connection (extension.ts, the Studio and
// Connections message handlers). Matched, not guessed: it is the only evidence a panel that mounted while the engine
// was already down ever receives. engine-restart-recovery.test.mjs keeps the two literals identical.
const ENGINE_NOT_CONNECTED = 'Engine not connected.';
export function App() {
  const [session, setSession] = useState<SessionInfo | null>(null);
  // The open model's FILE is what a typed, unsaved draft is held under (drafts.ts), set before any editor below renders. It keeps the last model through an outage, so a draft is found again when the engine returns.
  if (session?.sessionId) setDraftModel(modelKeyOfSession(session), session.modelLabel, session.sessionId);
  const sessionRef = useRef<SessionInfo | null>(null);
  sessionRef.current = session;
  // No engine at all. Distinct from "no model open": with no engine the Open a model placeholder is a dead end,
  // because its button needs the engine too. That is what Kane hit on 2026-09-15 after a restart failed.
  const [engineDown, setEngineDown] = useState(false);
  // The remembered published model that is waiting for the person to sign in (host: setPendingReconnect). While it
  // waits the page names that model and offers Sign in to reconnect, instead of the generic Open a model page.
  const [reconnectName, setReconnectName] = useState<string | null>(null);
  const [reconnectOffer, setReconnectOffer] = useState(false);
  // Words typed into the start plan and not saved: while any are held, an engine outage leaves the Plan on screen (read-only, saying so) instead of swapping it for the not-connected page, which would unmount the editor.
  const anyWordsDraft = useAnyWordsDraft();
  // The live connection context is the single source of truth for the attached query engine (its session copy is
  // refreshed on attach/disconnect, which App's own `session` above is not) — the Compare seed reads BOTH from here.
  const { session: liveSession, conn, context: connectionContext, connectionsOpen, openConnections, closeConnections } = useConnection();
  const [card, setCard] = useState<Scorecard | null>(null);
  const [busy, setBusy] = useState(false);
  const [trend, setTrend] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [route, setRoute] = useState<Route>({ sessionId: '', destination: 'plan', tool: 'plan', nonce: 0 });
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
    const ALIAS: Record<string, string> = { dataagent: 'deploy', compare: 'deploy', spec: 'plan', optimize: 'plan' };
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
    // is folded in here because the Plan with an object selected is a different page from the bare Plan, and coming back
    // to the wrong one of those is the kind of near-miss that made the old Back feel broken.
    if (next.destination === 'utility' && current.destination !== 'utility') {
      returnTo.current = current.tool === 'plan' && !current.object && selectedObjectRef.current
        ? { ...current, object: selectedObjectRef.current }
        : current;
    }
    routeRef.current = next;
    setRoute(next);
  };
  // Open in Queries' in-memory half (see daxTarget below).
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
    if (!remembered) { goTab('plan'); return; }
    const sid = sessionRef.current?.sessionId ?? '';
    // The seed is dropped: it was an explicit one-use hand-off and has already been acknowledged, so replaying it
    // would re-run a request over work the person has since changed.
    goRoute({ ...remembered, sessionId: sid || remembered.sessionId, seed: undefined, nonce: ++navNonce.current });
  };
  const seedForTarget = (tool: StudioTab, target: string | undefined, object: Route['object'], approvalId?: string): Seed | undefined => {
    if (tool === 'query' && object?.kind === 'measure') return { kind: 'query', value: objectToRef(object) };
    if (tool === 'tests' && object?.kind === 'measure') return { kind: 'test', value: objectToRef(object) };
    if (tool === 'permissions' && approvalId) return { kind: 'approval', value: approvalId };
    if (tool === 'advmodels' && target) return { kind: 'advanced', value: target.startsWith('area:') ? target.slice(5) : target };
    if (tool === 'workflows' && target?.startsWith('draft:')) return { kind: 'draft', value: target.slice('draft:'.length) };
    // Open Query from the Plan's field carries the typed words (askbox.tsx ASK_WORDS_TARGET).
    if (tool === 'query' && target?.startsWith('words:')) return { kind: 'askWords', value: target.slice('words:'.length) };
    if (tool === 'spec' && target === SQL_DRAFT_TARGET) return { kind: 'specStart', value: 'sql' };
    if (tool === 'plan' && target === PLAN_START_TARGET) return { kind: 'planStart', value: 'start' };
    return undefined;
  };
  const goTab = (raw: string, approvalId?: string, target?: string, sessionId?: string, explicitSeed?: Seed) => {
    // Every id goes through the one landing table (route.ts). Compare is an alias for Published, and the two
    // retired pages land on the Plan: `modelhome` on the Plan itself, `history` on its History view, filtered to
    // the object the link named (contract 11.2). Data Agent remains a distinct route because its Advanced subview
    // is part of the destination, not a disposable alias detail.
    const land = landingOf(raw);
    if (!land) { console.warn(`[Studio] ignoring navigation to unknown tab id '${raw}' (host/webview version skew?)`); return; }
    const t = land.tool;
    if (land.view === 'history') setPlanHistory({ nonce: ++navNonce.current, entry: target?.startsWith('entry:') ? target.slice(6) : undefined });
    else if (t === 'plan') { setPlanHistory(null); if (target === 'work') setPlanHome(++navNonce.current); }
    setUnseen((s) => { if (!s.has(t)) return s; const n = new Set(s); n.delete(t); return n; });
    const object = refToObject(target);
    const seed = explicitSeed ?? seedForTarget(t, target, object, approvalId);
    goRoute({ sessionId: sessionId ?? sessionRef.current?.sessionId ?? routeRef.current.sessionId, destination: DESTINATION_OF_TOOL[t], tool: t, object, seed, nonce: ++navNonce.current });
  };
  useEffect(() => { if (route.destination !== 'utility' && !route.seed) lastInArea.current[route.destination] = route; }, [route]);
  const goGroup = (group: string) => {
    const area = AREA_ALIASES[group];
    if (!area) return;
    // The button for the area you are ALREADY in is an exit, so it goes to that area's home page. Area memory is
    // for coming BACK to an area, and using it here handed you the page you were already looking at: with Back
    // retired, that is how Kane had nothing at all to press on Data with a table in hand (2026-09-15).
    const remembered = routeRef.current.destination === area ? undefined : lastInArea.current[area];
    // The Plan's home is its Work view. History is page state inside the Plan, not a route, so the route alone
    // left you on History when you pressed Plan from it (journey m11 F2); this says "go home" to the page itself.
    if (area === 'plan') setPlanHome(++navNonce.current);
    if (remembered) goRoute({ ...remembered, sessionId: sessionRef.current?.sessionId ?? remembered.sessionId, seed: undefined, nonce: ++navNonce.current });
    else goTab(areaHomeTool(area));
  };
  // A route and every object hand-off belong to one model session. Reset them together on open, close and swap.
  const previousShellSession = useRef<string | undefined>(undefined);
  // A navigation stamped for a session Studio has not read yet is HELD and replayed when that session arrives. A
  // model just created or opened from Connections is known to the caller first (the create call answers with its
  // id), and to Studio only after the host's 'reconnected' and a fresh sessionInfo. Dropping it, as a stale move is
  // dropped, sent From a SQL source to the Plan (verifier H1). One stamped for a session already seen is stale.
  const heldNav = useRef<NavigateMessage | undefined>(undefined);
  const seenSessions = useRef(new Set<string>());
  useEffect(() => {
    const sid = session?.sessionId ?? '';
    if (previousShellSession.current === sid) return;
    const previousSid = previousShellSession.current;
    previousShellSession.current = sid;
    if (sid) seenSessions.current.add(sid);
    const coldStampedRoute = !!sid && routeRef.current.sessionId === sid;
    // A REAL session change is a different model, and the page you were on belongs to the model you left, so forget
    // it. The session merely BECOMING KNOWN is not that: on a cold open the page that sent you to Settings is
    // recorded before sessionInfo answers, and throwing it away there would make Done land on the Plan instead of
    // where you actually were. previousSid is undefined on mount and '' until a model arrives, so both are falsy.
    if (previousSid) returnTo.current = undefined;
    lastInArea.current = {};
    // The header's "your assistant ran something here" dots point at the old model's activity feed and reflections,
    // which the session key below restarts; a dot kept across the swap points at work done on a model no longer open.
    if (previousSid) setUnseen(new Set());
    const initialSelection = pendingTreeSelection.current?.sessionId === sid ? pendingTreeSelection.current.object : undefined;
    pendingTreeSelection.current = undefined;
    setSelectedObject(initialSelection); setCompareSeed(null); setSeedPending(false);
    setAgentPolicy(undefined); setModelChangeNonce(0);
    if (!coldStampedRoute) {
      const next: Route = { sessionId: sid, destination: 'plan', tool: 'plan', object: initialSelection, nonce: ++navNonce.current };
      routeRef.current = next; setRoute(next);
    }
    if (sid) rpc<AgentPolicy>('getAgentPolicy').then((p) => { if (sessionRef.current?.sessionId === sid) setAgentPolicy(p); })
      .catch(() => { if (sessionRef.current?.sessionId === sid) setAgentPolicy(null); });
    const held = heldNav.current;
    if (held && held.sessionId === sid) { heldNav.current = undefined; dispatchNavigate(held); }
  }, [session?.sessionId]);
  // Next/prev Studio tab (Ctrl+Alt+←/→ via the host keybinding, so it works with focus inside OR outside the webview).
  const cycleTab = (dir: 1 | -1) => {
    const i = CYCLE_TABS.indexOf(tabRef.current);
    goTab(CYCLE_TABS[((i < 0 ? 0 : i) + dir + CYCLE_TABS.length) % CYCLE_TABS.length]);
  };
  // The '?' keyboard-shortcuts cheat sheet (also opened by the "Semanticus: Keyboard Shortcuts" command).
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // "Review these together →" from AI Readiness / BPA: jump to Change Plan and seed it (the view auto-proposes only if the
  // plan is empty, so an in-progress plan is never clobbered). Turns Change Plan into the apply-mode of the findings.
  const [planSeed, setPlanSeed] = useState(0);
  // The "Your assistant" chip's count opens the Plan filtered to the assistant's track (contract 11.4). Page state
  // handed to the Plan, not a route seed: it is a filter, and a filter is not a place.
  const [planTrack, setPlanTrack] = useState<{ doer: PlanDoer; nonce: number } | null>(null);
  // A route to the old Edit History id (a saved link, the object line's History, Ctrl+Alt+H, an older engine's jump)
  // opens the Plan on its History view. Page state, bumped by its nonce like the track above.
  const [planHistory, setPlanHistory] = useState<{ nonce: number; entry?: string } | null>(null);
  // Bumped by the Plan area button (and More > Plan, and Ctrl+Shift+1): the Plan goes to its Work view.
  const [planHome, setPlanHome] = useState(0);
  // Ctrl J (lane U5; composer contract 10.2): the ask box under the header on any page. A task typed there drafts, and
  // the Plan opens with that draft's row open; planOpen carries the row, bumped by its nonce like the track above.
  const [askOpen, setAskOpen] = useState(false);
  // Words typed in a field that describes a task (the Plan's own field) go into the ask box when Ctrl J opens it,
  // rather than staying behind (journey m11 F7). The field says so with data-ask-carry. The nonce re-mounts the box
  // so a second Ctrl J starts from the words then in the field; askTaken empties that field once a draft is made.
  const [askSeed, setAskSeed] = useState<{ text: string; nonce: number } | null>(null);
  const [askTaken, setAskTaken] = useState(0);
  // Read by openAsk, which the keydown listener captured once, so it must not read askOpen through a stale closure.
  const askOpenRef = useRef(false);
  askOpenRef.current = askOpen;
  const openAsk = () => {
    const el = document.activeElement;
    // The Plan's field is a textarea since it keeps line breaks (N15), so both kinds of field carry.
    const text = (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) && el.dataset.askCarry === 'yes' ? el.value.trim() : '';
    // Ctrl J while the box is open never throws away words typed in it (UAT S12-2-008: every press re-mounted it
    // empty). It starts again only to carry new field words into an empty box; otherwise the cursor goes back in.
    const box = document.querySelector<HTMLInputElement>('[data-testid="ask-bar-input"]');
    if (askOpenRef.current && (!text || (box?.value ?? '').trim() !== '')) { box?.focus(); return; }
    setAskSeed({ text, nonce: ++navNonce.current });
    setAskOpen(true);
  };
  const [planOpen, setPlanOpen] = useState<{ id: string | null; nonce: number } | null>(null);
  // A workflow run is a Plan row too (lane W2, R3): Run on Workflows and the header's run chip open its row the same
  // way. A null id opens the Plan with no row opened.
  // A row asked for by id is the point of the trip, so no object filter may hide it: the tree's object is cleared as
  // Clear filter clears it (journey m11 F4). The route object is already dropped by goTab('plan').
  const openPlanRow = (id: string | null) => {
    if (id) setSelectedObject(undefined);
    setPlanOpen({ id, nonce: ++navNonce.current }); goTab('plan');
  };
  const assistantCount = useAssistantPlanCount(session?.sessionId, session?.revision, modelChangeNonce);
  const reviewAsPlan = () => { setPlanSeed((n) => n + 1); goTab('optimize'); };
  const openWorkflow = (name: string) => goTab('workflows', undefined, undefined, undefined, { kind: 'workflow', value: name });
  // Runs are Plan rows (contract 11.3, R3): the run chip opens the newest live run's row on the Plan.
  const openWorkflowRuns = () => { const live = mostRecentLive(runs); openPlanRow(live ? `run/${live.runId}` : null); };
  const openPublish = () => goTab('deploy');

  // The host navigates us here (e.g. a Model-tree "Preview data" right-click): switch tabs and, for Data, hand the
  // target table to the Data view (nonce so re-selecting the same table re-previews). signalReady lets the host
  // flush a navigation it queued while opening Studio cold.
  useEffect(() => {
    signalReady();
    return onNavigate((m) => {
      const currentSid = sessionRef.current?.sessionId;
      const messageSid = m.sessionId ?? currentSid ?? routeRef.current.sessionId;
      if (currentSid && messageSid !== currentSid) {
        if (m.sessionId && !seenSessions.current.has(m.sessionId)) heldNav.current = m;
        return;
      }
      // Keyboard/command pseudo-targets are session-stamped too. A delayed old-session shortcut must not move the
      // newly opened model any more than an old object target may.
      if (m.tab === 'shortcuts') { setShortcutsOpen(true); return; }
      if (m.tab === 'ask') { openAsk(); return; }
      // A row of the Plan by id, from inside Studio ("On the plan" on a finding it was given from).
      if (m.tab === 'planrow') { openPlanRow(m.target || null); return; }
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

  // Native selection is context only. It may update the Plan's route object, but never a DAX or Tests seed.
  // Session identity makes a queued event from model A harmless after model B has opened, even when names match.
  useEffect(() => onTreeSelection((selection) => {
    if (selection.by === 'assistant') return;
    const sid = sessionRef.current?.sessionId;
    if (sid && selection.sessionId && selection.sessionId !== sid) return;
    const object = refToObject(selection.ref);
    // The host can send its initial selection before sessionInfo returns. Keep its session stamp until the model
    // arrives, so opening Studio does not discard the row the person already selected in the native tree.
    if (!sid && selection.sessionId) { pendingTreeSelection.current = { sessionId: selection.sessionId, object }; return; }
    setSelectedObject(object);
    const current = routeRef.current;
    if (current.tool === 'plan' && (!selection.sessionId || current.sessionId === selection.sessionId)) {
      const next = { ...current, object };
      routeRef.current = next; setRoute(next);
    }
  }), []);
  // The native tree picker can name the hub section; use the same shared drawer as the footer and header.
  useEffect(() => onOpenConnections((section) => {
    if (section === 'open' || section === 'setup' || section === 'accounts' || section === 'history' || section === 'add' || section === 'work' || section === 'sqlsources' || section === 'createsql' || section === 'create') openConnectionsOnView(section);
    openConnections();
  }), [openConnections]);

  // The in-Studio half of the keyboard suite: gestures VS Code keybindings must NOT own (see shortcuts.tsx —
  // unmodified '?' would fire while typing, and Ctrl+Alt+letter package bindings collide with AltGr typing on
  // European layouts). Everything here checks the target is not editable, so typing is never hijacked.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing) return;
      // Ctrl J opens the ask box from any page, typing or not: it is not a character anyone types. The host keybinding
      // sends the same thing when VS Code sees the key first (package.json semanticus.studioAsk).
      if (isAskKey(e)) { e.preventDefault(); e.stopPropagation(); openAsk(); return; }
      const hostCmd = hostCommandForKey(e);
      if (hostCmd) { e.preventDefault(); e.stopPropagation(); runHostCommand(hostCmd); return; }
      if (!e.ctrlKey && !e.metaKey && !e.altKey && e.key === '?' && !isTypingTarget(e.target)) {
        e.preventDefault(); setShortcutsOpen((v) => !v); return;
      }
      if (!e.ctrlKey || !e.altKey || e.metaKey || isTypingTarget(e.target)) return;
      // The shared model timeline (both doors see it), through the same host command as Ctrl+Z, which says a refusal out
      // loud (verifier on m11/sol-fixes-1, D7); an empty timeline is a no-op there too.
      if (e.key.toLowerCase() === 'z') { e.preventDefault(); runHostCommand(e.shiftKey ? 'semanticus.redo' : 'semanticus.undo'); return; }
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
          // The trend is this model's score history: another model's scores in it draw a dip it never had.
          setCard(null); setError(null); setTrend([]);
          setPendingApprovals([]);
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
      noteAssistantChange(n);
      // There is no page-held list of edits any more. It fed Edit History, that tab's count and the Overview's
      // Changes tile, and all three retired in slice 11 (lanes R1, R2). History is the engine's ledger, read by the
      // Plan's History view (getPlanHistory), which survives a reload the way a list in this page never did.
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
    const offReconnectPending = onReconnectPending((name, offer) => { setReconnectName(name); setReconnectOffer(!!offer); });
    // The assistant's own AI readiness scan (show-checks, 12 Oct): scan again at once, so the page it opened on shows the result and not an older card.
    const offAssistantScan = onAssistantCall((call) => { if (SCAN_PAGE_OF_TOOL[call.tool] === 'readiness') void scan(); });
    return () => { off(); offReconnect(); offEngine(); offReconnectPending(); offAssistantScan(); window.clearTimeout(rescanTimer.current); };
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
  const keepPlan = keepPlanThroughOutage({ hasSession: !!session?.sessionId, hasDraft: anyWordsDraft, tab });
  const gatedFeature = featureOfTool(tab);
  const featureGrant = useFeature(gatedFeature);
  // Validation used to need a second grant of its own here: TestsView mounts for EVERY open model and is
  // merely hidden with a CSS class, so a gate on the visible branch alone would have left its listTests /
  // listTableMappings / listTestRuns calls running in the background on the free plan. Validation is free
  // from 22 September 2026, so there is nothing left to gate and the second grant is gone with it.
  const routeIsCurrent = !!session?.sessionId && route.sessionId === session.sessionId;
  const routeObjectRef = routeIsCurrent && route.object ? objectToRef(route.object) : undefined;
  const routeTable = routeIsCurrent ? tableOfObject(route.object) : undefined;
  const seed = routeIsCurrent ? route.seed : undefined;
  const dataTarget = tab === 'data' && routeTable ? { table: routeTable, nonce: route.nonce } : null;
  const diagramAdd = tab === 'diagram' && seed?.kind === 'addTables' && Array.isArray(seed.value) ? { tables: seed.value, nonce: route.nonce } : null;
  const diagramRelationship = tab === 'diagram' && route.object?.kind === 'relationship' ? { id: route.object.id, nonce: route.nonce } : null;
  const lineageTarget = tab === 'lineage' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce } : null;
  const testTarget = tab === 'tests' && seed?.kind === 'test' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce } : null;
  // Which of Validation's two tabs the route asks for. `evidence` is a real route id, not a page: it is how
  // an old link, a saved page and Validation's own Reports button all land on the same tab.
  const validationTab: 'checks' | 'reports' = tab === 'evidence' ? 'reports' : 'checks';
  // The same shape for AI instructions' two tabs. `knowledge` is a real route id and not a page of its own
  // any more: it is how a deep link, the ask box, the Plan and every saved page still reach the Notes tab.
  const instructionsTab: InstructionsTab = tab === 'knowledge' ? 'notes' : 'instructions';
  // A DAX text handed to the Query page (seed kind 'daxText') is adopted once and acknowledged.
  const daxTarget = tab === 'query' && seed?.kind === 'query' && routeObjectRef ? { ref: routeObjectRef, nonce: route.nonce }
    : tab === 'query' && seed?.kind === 'daxText' ? { ref: '', dax: String(seed.value), nonce: route.nonce } : null;
  // A draft handed to Author arrives as the seed { kind: 'draft', value: '<dr id>' } (Edit in Workflows and Make it
  // reusable on a draft, lane U5).
  const workflowTarget = tab === 'workflows' && (seed?.kind === 'workflow' || seed?.kind === 'workflowRuns' || seed?.kind === 'draft')
    ? { kind: seed.kind === 'workflow' ? 'workflow' as const : seed.kind === 'draft' ? 'draft' as const : 'section' as const, value: String(seed.value), nonce: route.nonce }
    : null;
  const pqTarget = tab === 'mcode' && routeTable ? { table: routeTable, partitionId: route.object?.kind === 'partition' ? routeObjectRef : undefined, nonce: route.nonce } : null;
  const advArea = tab === 'advmodels' && seed?.kind === 'advanced' ? { area: String(seed.value), nonce: route.nonce } : null;
  const permissionTarget = tab === 'permissions' && seed?.kind === 'approval' ? { id: String(seed.value), nonce: route.nonce } : null;
  const statsTarget = tab === 'stats' && routeTable ? { table: routeTable, nonce: route.nonce } : null;
  // The words typed in the Plan's field, handed to Ask your data's question box, adopted once (fix-askdata-r6).
  const askWordsTarget = tab === 'query' && seed?.kind === 'askWords' ? { words: String(seed.value), nonce: route.nonce } : null;
  // The new model wizard's SQL step (route.ts SQL_DRAFT_TARGET), as the route's nonce so each ask opens it once.
  const draftStartSql = tab === 'spec' && seed?.kind === 'specStart' ? route.nonce : 0;
  // The Plan's start view, asked for by the Studio's plan/start place (route.ts PLAN_START_TARGET), also once.
  const showPlanStart = tab === 'plan' && seed?.kind === 'planStart' ? route.nonce : 0;
  // What the Plan's "Showing <object>" line shows: an explicit route object wins, and with none the model tree's
  // current selection stands in, as it did on the Overview. So a table picked in the tree while another page is
  // open is still the Plan's object when the area button is pressed (coordinator, slice 11). Clear filter clears both.
  const planObject = tab === 'plan' && routeIsCurrent ? (route.object ?? selectedObject) : undefined;
  // Choosing where to go from the header while Connections is open is leaving the dialog. Before, the page changed
  // BEHIND it and nothing seemed to happen (journey m11 F18). The header's area buttons, More and Find a tool all go
  // through these two, so each closes the dialog first and the page it picked is in front.
  const leaveDialogFor = <A extends unknown[]>(go: (...args: A) => void) => (...args: A) => {
    if (connectionsOpen) closeConnections();
    go(...args);
  };
  const showConnections = (section: HubView = 'open') => { openConnectionsOnView(section); openConnections(); };
  const selectOnPlan = (object: Route['object']) => {
    setSelectedObject(object);
    const current = routeRef.current;
    if (current.tool !== 'plan') return;
    const next = { ...current, object };
    routeRef.current = next; setRoute(next);
  };

  return (
    <>
    <FirstRun />
    <DaxModelProvider key={session?.sessionId ?? 'no-model'}>
    {/* Lineage's report-analysis holder stays mounted while the conditional tab body below changes. The surrounding
        session key still drops model-scoped state atomically when a different model opens. */}
    <LineageTabStateProvider>
    <ActivityProvider>
    <DaxLabTabStateProvider>
    <StorageTabStateProvider active={tab === 'stats'}>
    <ShortcutsOverlay open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    <ProDecisionsHost />
    <ConnectionsHub open={connectionsOpen} onClose={closeConnections} />
    <Shell route={route} onTab={leaveDialogFor(goTab)} onGroup={leaveDialogFor(goGroup)} onDone={goDone} onShortcuts={() => setShortcutsOpen(true)} unseen={unseen}
      pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={pendingApprovals[0]?.id}
      onConnections={showConnections} activeRun={mostRecentLive(runs)} liveRunCount={liveRuns(runs).length}
      onWorkflowRuns={openWorkflowRuns} onPublish={openPublish} agentPolicy={agentPolicy}
      assistantCount={assistantCount}
      onAssistantCount={() => { setPlanTrack({ doer: 'assistant', nonce: ++navNonce.current }); goTab('plan'); }}
      askBar={askOpen ? <AskBar key={askSeed?.nonce ?? 0} initialText={askSeed?.text ?? ''}
        onClose={() => setAskOpen(false)}
        onDrafted={(draftId) => {
          if (askSeed?.text) setAskTaken(++navNonce.current);
          setPlanOpen({ id: draftRowId(draftId), nonce: ++navNonce.current }); goTab('plan');
        }} /> : null}>
      <ToolErrorBoundary resetKey={tab}>
      <>
      <TrialEndingLine hidden={tab === 'pro'} />
      {/* A test run can carry substantial cell evidence, so keep the view alive across Studio navigation instead
          of copying it into persisted webview state. The session key still discards it when a different model opens. */}
      {session?.sessionId && <div className={tab === 'tests' || tab === 'evidence' ? 'h-full' : 'hidden'}><TestsView key={session.sessionId} navTarget={testTarget} onNavConsumed={consumeRouteSeed}
        page={validationTab} onPage={(next) => goTab(next === 'reports' ? 'evidence' : 'tests')}
        onOpenDaxLab={(ref) => goTab('query', undefined, ref)} onOpenSavedReports={() => goTab('evidence')} /></div>}
      {tab === 'pro' ? (
        // The Pro page needs no model and no engine of its own: buying and a key both run in the extension host,
        // and a person with nothing open yet must still be able to get here from the header chip or Help.
        <ProPage />
      ) : engineDown && !keepPlan ? (
        <EngineDown />
      ) : !session?.sessionId ? (
        <Empty onOpen={() => showConnections('open')} reconnectName={reconnectName} offer={reconnectOffer} />
      ) : featureGrant === 'denied' ? (
        <ProPreview tool={tab} />
      ) : featureGrant === 'unknown' ? (
        <FeatureChecking />
      ) : tab === 'plan' || tab === 'optimize' || tab === 'spec' ? (
        // The living plan for this model, and the start page. It is recomputed from the model, so it takes
        // the change nonce: an edit this webview was told about is the one thing that can honestly move the
        // plan, and the page re-reads it rather than patching a list of its own. The Overview's jobs live
        // here since slice 11 (contract 11.1): the facts line with Raise compatibility level, and the object
        // line with the picked object's actions.
        //
        // Plan is ONE tool from 23 September (Kane: "Model spec should be replaced by plan and pending
        // changes should also be"). `optimize` and `spec` are still real route ids, because every
        // "Review these together" button, the palette, every saved page and every deep link points at
        // them. The Plan page HOSTS each as a panel over the plan and opens it only when a route asked
        // by id; the plan itself advertises neither. Kane, sixth Yoga build: the three had "just been
        // tied together rather than integrated", so the shared chrome went and the real merge is the
        // Fix milestone's.
        <PlanView sessionId={session.sessionId} revision={session.revision} changeNonce={modelChangeNonce}
          modelName={session.modelName} hostSession={session}
          focus={tab === 'optimize' ? 'changes' : tab === 'spec' ? 'draft' : undefined}
          focusNonce={route.nonce + planSeed}
          draftStartSql={draftStartSql} onDraftStartTaken={() => consumeRouteSeed(draftStartSql)}
          showStart={showPlanStart} onShowStartTaken={() => consumeRouteSeed(showPlanStart)}
          track={planTrack?.doer ?? null} trackNonce={planTrack?.nonce}
          openRow={planOpen?.id ?? null} openRowNonce={planOpen?.nonce} onRunUpdate={foldRun}
          focusObject={planObject && planObject.kind !== 'model'
            ? { ref: objectToRef(planObject), label: objectLabel(planObject) ?? objectToRef(planObject) } : null}
          onClearObject={() => selectOnPlan(undefined)}
          historyEntry={planHistory?.entry} historyNonce={planHistory?.nonce} homeNonce={planHome || undefined} fieldTakenNonce={askTaken || undefined}
          onGoObject={(tool, target, addTables) => goTab(tool, undefined, target, undefined,
            addTables?.length ? { kind: 'addTables', value: addTables } : undefined)}
          onGo={(tool, target) => {
            // An empty model's "New blank model" start opens the hub on its create form (ModelPlanView.Starts).
            if (tool === 'connections') { openConnectionsOnCreate(); openConnections(); return; }
            goTab(tool, undefined, target);
          }} />
      ) : tab === 'evidence' ? (
        // Reports is a TAB on Validation, not a page beside it, so the body is rendered by the always-mounted
        // TestsView above. Nothing here, for the same reason `tests` renders nothing here.
        null
      ) : tab === 'readiness' ? (
        <div className="sem-page">
          {error && <ReadyBanner tone="bad">{error}</ReadyBanner>}
          {card?.gatedBy?.length ? <ReadyBanner tone="bad">{card.gatedBy.map(plainGate).join(' · ')}</ReadyBanner> : null}
          {card?.caveat ? <ReadyBanner tone="warn">{card.caveat}</ReadyBanner> : null}
          {card?.ruleErrors?.length ? <ReadyBanner tone="warn">{card.ruleErrors.join(' · ')}</ReadyBanner> : null}
          {card ? <Hero card={card} trend={trend} busy={busy} onSafe={applySafeFixes} /> : <ReadyPanel><div className="sem-text-meta">Scanning model…</div></ReadyPanel>}
          {card && <Categories card={card} />}
          {/* No Model interview card here (Kane, 8 October 2026: the interview is hidden from the whole app; the
              engine and the assistant's tools keep it). docs/PLAN.md decisions log carries the exception. */}
          {card && <Findings card={card} onRescan={scan} />}
          {card && <CustomRulesPanel kind="readiness" onChanged={() => void scan()} />}
        </div>
      ) : tab === 'workflows' ? (
        <WorkflowsView navTarget={workflowTarget} onNavConsumed={consumeRouteSeed} runs={runs} onRunUpdate={foldRun}
          markOwnRun={(id) => ownRunIds.current.add(id)} isOwnRun={(id) => ownRunIds.current.has(id)} onOpenPlan={openPlanRow}
          onGo={(tool, target) => goTab(tool, undefined, target)} />
      ) : tab === 'instructions' || tab === 'knowledge' ? (
        <AiInstructionsPage sessionId={session.sessionId} revision={session.revision} changeNonce={modelChangeNonce}
          tab={instructionsTab} onTab={(next) => goTab(next === 'notes' ? 'knowledge' : 'instructions')}
          onOpenWorkflows={(name) => name ? openWorkflow(name) : goTab('workflows')} />
      ) : tab === 'bpa' ? (
        <BpaView onReviewAsPlan={reviewAsPlan} />
      ) : tab === 'diagram' ? (
        <DiagramView addTables={diagramAdd} focusRelationship={diagramRelationship} onNavConsumed={consumeRouteSeed} />
      ) : tab === 'advmodels' || tab === 'mcode' ? (
        // Power Query is a sub-page of Advanced modelling (Kane, 8 Oct): one row, one order. `mcode` stays a real route
        // (the ask box, the Plan and Edit M Code land on it) and opens this page on Power Query, at the table when given.
        <AdvancedModelsView navArea={tab === 'mcode' ? { area: 'mcode', nonce: route.nonce } : advArea} mcodeTarget={pqTarget} onNavConsumed={consumeRouteSeed} />
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
          onRename={(ref) => goTab('plan', undefined, ref)} onOpenWorkflow={openWorkflow} />
      ) : tab === 'copilottest' ? (
        <CopilotTestView />
      ) : tab === 'docs' ? (
        <DocumentationView />
      ) : tab === 'deploy' || tab === 'dataagent' ? (
        // The legacy dataagent route lands on Changes: the Data Agent screen is hidden for now (the assistant still reaches it).
        <DeployView key="deploy" seed={compareSeed} />
      ) : tab === 'tests' ? (
        null
      ) : tab === 'permissions' ? (
        <PermissionsView focusApproval={permissionTarget} onFocusConsumed={consumeRouteSeed} onPolicyChanged={setAgentPolicy} onApprovalChanged={() => { void refreshPendingApprovals(); }} />
      ) : tab === 'query' ? (
        <QueryView navTarget={daxTarget} navWords={askWordsTarget} onNavConsumed={consumeRouteSeed} onOpenTests={() => goTab('tests')} />
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
    </>
  );
}

// The Semanticus brand mark: logo A, the path S with a dot at each end (the website's assets/logo.svg drawing, at the
// 24 grid). The S follows the text colour and the middle bar and both dots take the jade accent, so the mark follows the
// theme like the rest of the header. Weight 1.8 and dots r 2.1 are a touch heavier than the brand file so the dots hold
// at the 20 px the header draws it.
function BrandMark() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-label="Semanticus" className="shrink-0" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M19 5.5H8.25a3.25 3.25 0 0 0 0 6.5h7.5a3.25 3.25 0 0 1 0 6.5H5" stroke="currentColor" />
      <path d="M8.25 12h7.5" style={{ stroke: 'var(--sem-accent)' }} />
      <circle cx="19" cy="5.5" r="2.1" style={{ fill: 'var(--sem-accent)' }} />
      <circle cx="5" cy="18.5" r="2.1" style={{ fill: 'var(--sem-accent)' }} />
    </svg>
  );
}

// The header's gear is gone (Kane, 2026-09-14). It sat beside Connections, named nothing in words, and was the
// only header way into assistant permissions. Permissions has a named row in the Your assistant menu now, beside
// the rest of the assistant's controls, so the glyph and its button went with it.

// Find a tool also lists the places that are VIEWS inside a page rather than buttons on the strip, under the page they
// live in. TAB_GROUPS cannot carry them (the ask box and AskRouteTests hold the strip to it as text), and without them
// the Model Spec and the SQL start were in no list at all (plan statement check, 28 Sep, defect 6). `target` is the
// route target the row passes, so "From a SQL source" lands on the wizard's SQL step (route.ts SQL_DRAFT_TARGET).
const FIND_A_TOOL_EXTRA: Partial<Record<Area, { id: string; tool: StudioTab; label: string; target?: string }[]>> = {
  plan: [
    { id: 'spec', tool: 'spec', label: 'Model Spec' },
    { id: 'spec-sql', tool: 'spec', label: 'From a SQL source', target: SQL_DRAFT_TARGET },
  ],
};

// A menu that opens from a button near an edge must still open on screen. Find a tool opens under a button in the
// header, or under its row inside More; anchored to that button's right edge at 590px wide, it ran off the LEFT edge
// of a 792px Studio (a 1440 window with the sidebar open, check item 9). Measured after it draws, then nudged in, so it
// needs no copy of the header's widths.
function keepMenuOnScreen(menu: HTMLElement | null) {
  if (!menu) return;
  menu.style.translate = '';
  const gutter = 8;
  const box = menu.getBoundingClientRect();
  const room = document.documentElement.clientWidth || window.innerWidth;
  const dx = box.left < gutter ? gutter - box.left : box.right > room - gutter ? Math.max(gutter - box.left, room - gutter - box.right) : 0;
  if (dx) menu.style.translate = `${Math.round(dx)}px 0`;
}

// The one list of every tool. Help > All tools opens it controlled (`open`, no button of its own); the More menu at a
// narrow width still carries its own copy with a button, so it keeps working when the header folds.
function ToolChooser({ tab, locked, onTab, open: controlled, onClose }: {
  tab: StudioTab; locked: Set<string>; onTab: (t: string, target?: string) => void; open?: boolean; onClose?: () => void;
}) {
  const [openOwn, setOpenOwn] = useState(false);
  const open = controlled ?? openOwn;
  const setOpen = (next: boolean | ((value: boolean) => boolean)) => {
    const value = typeof next === 'function' ? next(open) : next;
    if (controlled !== undefined) { if (!value) onClose?.(); } else setOpenOwn(value);
  };
  const wrap = useRef<HTMLDivElement | null>(null);
  const menu = useRef<HTMLDivElement | null>(null);
  useLayoutEffect(() => {
    if (!open) return;
    keepMenuOnScreen(menu.current);
    const again = () => keepMenuOnScreen(menu.current);
    window.addEventListener('resize', again);
    return () => window.removeEventListener('resize', again);
  }, [open]);
  useEffect(() => {
    if (!open) return;
    const closeOutside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const closeEscape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', closeOutside); document.addEventListener('keydown', closeEscape);
    return () => { document.removeEventListener('mousedown', closeOutside); document.removeEventListener('keydown', closeEscape); };
  }, [open]);
  return <div ref={wrap} className={controlled === undefined ? 'tool-chooser relative' : undefined}>
    {controlled === undefined && <button type="button" className="studio-header-action" aria-expanded={open} aria-haspopup="menu" onClick={() => setOpen((value) => !value)}>Find a tool</button>}
    {open && <div ref={menu} role="menu" aria-label={controlled === undefined ? 'Find a tool' : 'All tools'} className="tool-chooser-menu studio-header-pop absolute right-0 top-full mt-1">
      {SHOWN_GROUPS.map((group) => <section key={group.id} aria-label={group.label}>
        <div className="tool-chooser-heading">{group.label}</div>
        {/* The row carries a STABLE TOOL ID. It always did, and it now matters twice over: a Pro pill changes
            the row's text, so anything selecting these by their words breaks the moment a tool is locked. */}
        {group.tabs.map((tool) => <button key={tool.id} role="menuitem" type="button" data-tool={tool.id}
          aria-current={tool.id === tab || ((tab === 'compare' || tab === 'dataagent') && tool.id === 'deploy') || (tab === 'evidence' && tool.id === 'tests') || (tab === 'knowledge' && tool.id === 'instructions') ? 'page' : undefined}
          onClick={() => { onTab(tool.id); setOpen(false); }}>{tool.label}<ProBadge show={locked.has(tool.id)} /></button>)}
        {(FIND_A_TOOL_EXTRA[group.id] ?? []).map((extra) => <button key={extra.id} role="menuitem" type="button" data-tool={extra.id}
          aria-current={extra.tool === tab && !extra.target ? 'page' : undefined}
          onClick={() => { onTab(extra.tool, extra.target); setOpen(false); }}>{extra.label}<ProBadge show={locked.has(extra.tool)} /></button>)}
      </section>)}
    </div>}
  </div>;
}

function MoreStudioMenu({ activeArea, onGroup, tab, onTab, onConnections }: {
  activeArea: Area | null; onGroup: (gid: string) => void; tab: StudioTab;
  onTab: (tab: string, target?: string) => void; onConnections: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  // Kept inside the window like Find a tool's menu: at a narrow Studio it ran off the left edge (verifier L9).
  const menu = useRef<HTMLDivElement | null>(null);
  useLayoutEffect(() => {
    if (!open) return;
    keepMenuOnScreen(menu.current);
    const again = () => keepMenuOnScreen(menu.current);
    window.addEventListener('resize', again);
    return () => window.removeEventListener('resize', again);
  }, [open]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { setOpen(false); wrap.current?.querySelector('button')?.focus(); } };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  const go = (next: string, target?: string) => { onTab(next, target); setOpen(false); };
  // The overflow menu holds its own copy of Find a tool, so it marks the locked tools the same way.
  const lockedTools = useLockedTools();
  return <div ref={wrap} className="studio-more relative">
    <button type="button" aria-label="More Studio tools" aria-expanded={open} aria-haspopup="menu" onClick={() => setOpen((value) => !value)}
      className="studio-header-action">More</button>
    {open && <div ref={menu} role="menu" className="compact-menu studio-header-pop absolute right-0 top-full mt-1">
      <div className="compact-areas">{SHOWN_GROUPS.map((group) => <button key={group.id} role="menuitem" type="button" aria-current={activeArea === group.id ? 'page' : undefined}
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

// Folding is decided by FIT, level by level, and never by an estimate. The estimate this replaced summed each
// level's saving by hand and missed some of them (the actions' tighter gap, the nav's dropped margin), so after a
// resize it could fold the areas at a width where level 3 visibly fitted (journey m11 F1: 4 at 884px while 3
// fitted), and elsewhere it left the nav clipping a button. Trying each level in turn reads the real layout, so
// whatever styles.css does at a level is counted without a copy of its numbers here. It stays one task with no
// paint in between, so a person sees one state change, not a flicker through the levels. A level fits when
// nothing sticks out of the header and the areas nav, which shrinks before it folds, cuts into no button's text.
function measureHeaderFold(header: HTMLElement): number {
  delete header.dataset.brandFolded;
  if (header.clientWidth <= 0) return 0;
  // FOLD_AREAS_BELOW stays as the early shortcut: a header that narrow has no shallower fit worth finding.
  if (header.clientWidth < FOLD_AREAS_BELOW) return 4;
  const groups = header.querySelector('.studio-groups');
  const actions = header.querySelector('.studio-actions');
  // A level fits when nothing sticks out of the header and every area button's TEXT (its content box, so its side
  // padding may be the part the nav trims) ends left of the actions. Judged on the boxes a person
  // sees, not on the nav's scrollWidth: that counts trailing space and sub-pixels, and at level 3 it read 6px over
  // while Workflows ended 2px clear of the actions, which folded the six areas into More from the first frame at
  // 1440 wide with the sidebar open (journey m11-2).
  const fits = () => {
    if (header.scrollWidth > header.clientWidth) return false;
    if (!(groups instanceof HTMLElement)) return true;
    // The actions' left edge: the nav paints up to it (styles.css, the clip margin), so a button whose text ends there
    // is drawn whole. The nav's own box is 8px short of it, and judging by that box folded a header whose Workflows
    // ended 6px clear of More (journey m11-3, a real 792px Studio).
    const edge = actions instanceof HTMLElement ? actions.getBoundingClientRect().left : groups.getBoundingClientRect().right;
    for (const button of groups.children) {
      if (!(button instanceof HTMLElement) || button.getClientRects().length === 0) continue;
      const right = button.getBoundingClientRect().right - (parseFloat(getComputedStyle(button).paddingRight) || 0);
      if (right > edge + 0.5) return false;
    }
    return true;
  };
  for (let level = 0; level <= 3; level++) {
    header.dataset.fold = String(level);
    if (fits()) return level;
  }
  // Before the areas go, the brand word goes (the mark stays). The areas are how a person moves round the Studio,
  // and losing them to a few pixels, a status dot on the assistant chip, left a 792px Studio with no nav (m11 M4).
  // With the word gone, the shallowest level that fits is taken again, so nothing folds further than it must.
  header.dataset.brandFolded = '1';
  for (let level = 0; level <= 3; level++) {
    header.dataset.fold = String(level);
    if (fits()) return level;
  }
  delete header.dataset.brandFolded;
  return 4;
}

function useHeaderFold(ref: React.RefObject<HTMLElement | null>, signature: string) {
  useLayoutEffect(() => {
    const header = ref.current;
    if (!header) return;
    let frame = 0;
    let lastWidth = '';
    // The header's width, unrounded and in both units. clientWidth is a whole number of the header's own CSS px, so a
    // resize of less than a pixel (a real Free lane at 130%, 1051 to 1050, read 808 both times) left it unchanged and
    // the fold kept a level that no longer fit (verifier round 2). The rendered width is exact, but Studio zoom leaves
    // it where it was while the CSS width changes; clientWidth sees that. Either one moving is news.
    const widthOf = (el: HTMLElement) => `${el.clientWidth}:${el.getBoundingClientRect().width}`;
    const apply = () => {
      frame = 0;
      const el = ref.current;
      if (!el) return;
      lastWidth = widthOf(el);
      el.dataset.fold = String(measureHeaderFold(el));
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(apply); };
    apply();
    const observer = new ResizeObserver(() => {
      // Folding changes the header's HEIGHT, which fires this observer again. Only a width change is news, so
      // this cannot loop, and one rAF per resize frame keeps it to a single measure per frame.
      if (!ref.current || widthOf(ref.current) === lastWidth) return;
      schedule();
    });
    observer.observe(header);
    // Content that changes width without resizing the header: the assistant count landing after the first paint,
    // the Pro chip's words, an area button's state. The signature below covers the text the Shell knows about,
    // and this covers the children that fetch their own. Folding writes only data-fold on the header itself, which
    // is not watched, so a measure cannot trigger another one.
    const mutations = new MutationObserver(schedule);
    mutations.observe(header, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'style'] });
    return () => { if (frame) cancelAnimationFrame(frame); observer.disconnect(); mutations.disconnect(); };
  }, [ref, signature]);
}

/** The "Your assistant" chip's count: the assistant track's Ready plus On it, read off the engine's strip (contract
 *  11.4). Read again when the model moves and when the plan says it moved (a task, the switch, a hold). Null while
 *  nothing is known, so the chip draws no count rather than a zero nobody measured. */
function useAssistantPlanCount(sessionId: string | undefined, revision: number | undefined, nonce: number): number | null {
  const [count, setCount] = useState<number | null>(null);
  const [bump, setBump] = useState(0);
  useEffect(() => onPlanChange(() => setBump((n) => n + 1)), []);
  useEffect(() => {
    if (!sessionId) { setCount(null); return undefined; }
    let live = true;
    const timer = setTimeout(() => {
      rpc<PlanModel>('getModelPlan').then((plan) => { if (live) setCount(plan?.strip ? assistantChipCount(plan) : null); },
        () => { if (live) setCount(null); });
    }, 250);
    return () => { live = false; clearTimeout(timer); };
  }, [sessionId, revision, nonce, bump]);
  return count;
}

function Shell({ route, onTab, onGroup, onDone, onShortcuts, unseen, pendingApprovalCount, firstPendingApprovalId,
  onConnections, activeRun, liveRunCount, onWorkflowRuns, onPublish, agentPolicy, assistantCount = null,
  onAssistantCount, askBar, children }: {
  route: Route; onTab: (t: string, approvalId?: string, target?: string, sessionId?: string) => void; onGroup: (gid: string) => void;
  onDone: () => void; onShortcuts: () => void; unseen: Set<string>; pendingApprovalCount: number;
  firstPendingApprovalId?: string; onConnections: (section?: HubView) => void;
  activeRun: WorkflowRunView | null; liveRunCount: number; onWorkflowRuns: () => void; onPublish: () => void;
  agentPolicy: AgentPolicy | null | undefined; assistantCount?: number | null; onAssistantCount?: () => void;
  /** The ask box (Ctrl J, lane U5), drawn under the header on whatever page is open, or nothing. */
  askBar?: React.ReactNode; children: React.ReactNode;
}) {
  const tab = route.tool;
  const activeGroup = route.destination === 'utility' ? null : route.destination;
  const group = activeGroup ? TAB_GROUPS.find((item) => item.id === activeGroup) : undefined;
  const { session: shellSession, context, contextResolved, refresh } = useConnection();
  // Set by the Published page's own comparison against the destination. Null until something has actually tried.
  const unreachable = usePublishReach(shellSession?.sessionId, context?.publishing?.connectionId);
  // The tools this plan does not reach. The row keeps every one of them in its place and marks it; the
  // pill is a label, and the gate is the render boundary in App above.
  const lockedTools = useLockedTools();
  const lineageBusy = useLineageReportsBusy();
  const daxLabBusy = useDaxLabBusy();
  const storageBusy = useStorageBusy();
  const busy = useMemo(() => {
    const surfaces: string[] = [];
    if (lineageBusy) surfaces.push('lineage');
    if (daxLabBusy) surfaces.push('query');
    if (storageBusy) surfaces.push('stats');
    return busyAffordance(surfaces, tab, activeGroup, TAB_TO_GROUP);
  }, [lineageBusy, daxLabBusy, storageBusy, tab, activeGroup]);
  // The Link in the status line works while the person's own job runs (1.5).
  useEffect(() => { linkWork.setBusySurfaces([lineageBusy && 'lineage', daxLabBusy && 'query', storageBusy && 'stats'].filter(Boolean) as string[]); },
    [lineageBusy, daxLabBusy, storageBusy]);
  const source = shellSession?.source ? shellSession.source.replace(/[\\/]+$/, '').split(/[\\/]/).pop() : undefined;
  const publishing = context?.publishing;
  const publishTarget = publishing?.available ? (publishing.modelName || publishing.database || 'linked model') : null;
  const publishStageLabel = publishing?.effectiveLabel || publishing?.label;
  const publishStage = publishStageLabel ? publishStageName(publishStageLabel)
    : (publishing?.unlabelled ? 'development' : 'destination needed');
  const permissionLine = agentPolicy ? publishPermissionDescription(agentPolicy, publishing?.effectiveLabel || publishing?.label, !!publishTarget)
    : agentPolicy === null ? 'assistant permissions unavailable' : 'checking your assistant permissions';
  const publishLine = publishTarget ? `to ${publishTarget} · ${publishStage}` : 'no publish target · choose before publishing';
  const permissionDetail = agentPolicy ? `${agentPolicy.preset}: ${permissionPresetDescription(agentPolicy)}` : permissionLine;
  const toolLabel = tab === 'pro' ? 'Semanticus Pro' : tab === 'permissions' ? 'Assistant permissions' : tab === 'compare' || tab === 'dataagent' ? 'Published' : tab === 'evidence' ? 'Validation' : tab === 'knowledge' ? 'AI instructions'
    : tab === 'optimize' || tab === 'spec' ? 'Plan'
    : TAB_GROUPS.flatMap((item) => item.tabs).find((item) => item.id === tab)?.label ?? tab;
  const isSettings = route.destination === 'utility';
  // The Pro page names itself on the row, as the mockup draws it, rather than reading "Settings › Semanticus Pro".
  const areaLabel = isSettings ? (tab === 'pro' ? 'Semanticus Pro' : 'Settings') : group?.label ?? '';
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
  // An area with ONE tool whose tool has the area's own name said it twice: with seven areas the row read
  // "Overview › Overview" and "Workflows › Workflows", which teaches a person nothing and reads like a bug.
  // The area name alone is the honest row there, and the chevron goes with the thing it pointed at.
  const nameRepeats = !showStrip && toolLabel === areaLabel;
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
  const [toolsOpen, setToolsOpen] = useState(false);
  // Everything in the header whose text changes its width. A ResizeObserver only sees the header resize, so the
  // fold has to be re-measured when the content inside it changes too.
  const headerRef = useRef<HTMLElement | null>(null);
  const headerSignature = [shellSession?.modelName, source, publishLine, permissionLine,
    activeRun ? `${activeRun.title || activeRun.workflow}|${activeRun.stepIndex}|${activeRun.totalSteps}|${liveRunCount}` : '',
    pendingApprovalCount].join('|');
  useHeaderFold(headerRef, headerSignature);

  // CSS zoom on the app root scales every row, control and page together, so nothing has to be re-laid out for a
  // text size. It is applied here rather than on <body> because the harness and the extension host both own that.
  return <div className="studio-root h-full flex flex-col" style={{ zoom: zoom === ZOOM_DEFAULT ? undefined : `${zoom}%` }}>
    <header ref={headerRef} data-fold="0" className="studio-chrome flex items-center gap-2 px-3 border-b min-w-0" style={{ borderColor: 'var(--sem-border)' }}>
      <BrandMark />
      {/* Logo, then the model's name. The word "Semanticus" is the logo and the VS Code tab title; the file name moved
          into the tooltip so the header says one thing here. */}
      <div className="studio-identity min-w-0">
        <div className="font-semibold tracking-tight truncate" title={shellSession?.source || undefined}>{shellSession?.modelName || 'Semanticus'}</div>
      </div>
      <nav className="studio-groups flex items-center gap-1 ml-2 min-w-0" aria-label="Studio areas">
        {/* An area is marked Pro only when EVERY tool in it is locked: a mixed area would be lying about the
            free half of itself. Today that is Workflows and nothing else. */}
        {SHOWN_GROUPS.map((item, index) => <GroupTab key={item.id} active={item.id === activeGroup}
          title={`${item.label} (Ctrl+Shift+${index + 1})`} unseen={item.id !== activeGroup && item.tabs.some((tool) => unseen.has(tool.id))}
          busy={busy.groups.has(item.id)} locked={item.tabs.every((tool) => lockedTools.has(tool.id))}
          onClick={() => onGroup(item.id)}>{item.label}</GroupTab>)}
      </nav>
      <div className="studio-actions ml-auto flex items-center gap-2 shrink-0">
        <MoreStudioMenu activeArea={activeGroup} onGroup={onGroup} tab={tab} onTab={(t, target) => onTab(t, undefined, target)} onConnections={() => onConnections('open')} />
        {activeRun && <button type="button" className="studio-run-chip" onClick={onWorkflowRuns} title="Open its row on the Plan">
          {/* The label is its own element so a shortened chip can end in an ellipsis. Left as loose text it was an
              anonymous flex item, which centres and gets cut at BOTH ends ("d a measure · step"). */}
          <span /><span className="studio-run-chip-label">{activeRun.title || activeRun.workflow}, step {Math.min(activeRun.stepIndex + 1, activeRun.totalSteps)} of {activeRun.totalSteps}{liveRunCount > 1 ? `, ${liveRunCount} live` : ''}</span>
        </button>}
        <div className="studio-utilities">
          <button type="button" className="studio-header-action" onClick={() => onConnections('open')}>Connections</button>
        </div>
        {/* All tools lives in Help now (it was the header's Find a tool). The menu is the same ToolChooser; it opens
            under Help, so the two share one anchor. */}
        <div className="tool-chooser relative shrink-0">
          <HelpButton tab={tab} onGo={(next) => onTab(next as StudioTab)} onShortcuts={onShortcuts} onTextSize={stepZoom} zoom={zoom} onAllTools={() => setToolsOpen(true)} />
          {toolsOpen && <ToolChooser open tab={tab} locked={lockedTools} onTab={(t, target) => onTab(t, undefined, target)} onClose={() => setToolsOpen(false)} />}
        </div>
        {/* Get Pro, always in sight just left of Your assistant; a quiet Pro once you have it. Never folds. */}
        <ProChip here={tab === 'pro'} />
        <LiveActivity onOpen={onTab} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={firstPendingApprovalId}
          planCount={assistantCount} onPlanCount={onAssistantCount} />
        {/* One Publish button in sight. Here it opens the Publish page; on the Publish page it is hidden, because the
            page carries its own, on the line that says where the changes go. */}
        {tab !== 'deploy' && tab !== 'compare' && tab !== 'dataagent' && <div className="studio-publish-block">
          {/* Contract v1.5: the two publish facts are this tooltip and the bottom bar's Publish to slot. They used
              to hang under the row as a visible caption, which cost the header 19px on every page for a fact that
              never changes while you work. permissionDetail stays the deep version, one click away on the row. */}
          <button type="button" data-testid="publish-button" className="studio-publish" disabled={!contextResolved || !!unreachable}
            title={unreachable ? `Cannot reach ${publishTarget || 'the publish destination'}. The Publish page could not read it, so there is nothing to publish against.`
              : contextResolved ? `${publishLine}. ${permissionLine}. ${permissionDetail}` : 'Checking your connections'} onClick={onPublish}>Publish</button>
        </div>}
      </div>
    </header>
    <StudioLayer />
    {shellSession?.fileRecovery && shellSession.sessionId && <FileRecoveryOffer key={shellSession.sessionId} copy={shellSession.fileRecovery} sessionId={shellSession.sessionId}
      onResolved={refresh} />}
    {/* Row two, the same 40px on every page: the area name, then either the area's segment strip or the name of the
        page you are on, then the page notes behind ⓘ. This replaces the old breadcrumb row AND the always-open page
        guide, which together cost 93px above every tool.
        There is no Back here any more (Kane, 2026-09-14). Settings is the one page with no segment of its own, so it
        is the one page that gets a way out: Done, at the right-hand end, ahead of the ⓘ. */}
    {(showStrip || isSettings) && <div className="studio-arearow">
      {isSettings && <span className="studio-arearow-area">{areaLabel}{nameRepeats ? '' : ' ›'}</span>}
      {showStrip
        ? <AreaTabs group={group!} tab={tab} unseen={unseen} busyTabs={busy.tabs} locked={lockedTools} onTab={onTab} />
        : nameRepeats ? null : <strong className="studio-arearow-tool">{toolLabel}</strong>}
      {isSettings && <div className="ml-auto flex items-center gap-2">
        <button type="button" data-testid="settings-done" className="sem-btn sem-btn-sm" onClick={onDone}>Done</button>
      </div>}
    </div>}
    {/* pb-4: the scroll region ends exactly where the context bar starts, so without it the last control on a page sits
        flush against the bar at the end of the scroll. One rule here rather than per-page padding. */}
    {askBar}
    <SaidLine onHistory={(entry, sessionId) => onTab('history', undefined, entry ? `entry:${entry}` : undefined, sessionId)} />
    <main className="flex-1 overflow-auto min-h-0 pb-4">
      {/* M23, kept and strengthened: the segment strip used to sticky-pin to the top of this scroll region. It now
          sits in the chrome above it, so it cannot scroll away at all, on any page, at any scroll position. */}
      {children}
    </main>
    <OtherModelWords />
    <ContextBar onConnections={onConnections} />
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

function AreaTabs({ group, tab, unseen, busyTabs, locked, onTab }: {
  group: (typeof TAB_GROUPS)[number]; tab: StudioTab; unseen: Set<string>; busyTabs: Set<string>;
  locked: Set<string>; onTab: (tool: string) => void;
}) {
  // Ids that are not places of their own: the legacy Published routes, Reports (a tab inside Validation),
  // Notes (a tab inside AI instructions), Pending changes and Model Spec (views inside Plan), and Power Query (a
  // page of Advanced modelling). Each lights its host's segment.
  const visibleTab = tab === 'compare' || tab === 'dataagent' ? 'deploy' : tab === 'evidence' ? 'tests'
    : tab === 'knowledge' ? 'instructions' : tab === 'optimize' ? 'plan' : tab === 'spec' ? 'plan' : tab === 'mcode' ? 'advmodels' : tab;
  return <nav className="area-tabs" aria-label={`${group.label} tools`}>
    {group.tabs.filter((tool) => !STRIP_HIDDEN_TABS.includes(tool.id)).map((tool) => <Tab key={tool.id} id={tool.id} active={visibleTab === tool.id} unseen={unseen.has(tool.id) || (tool.id === 'advmodels' && unseen.has('mcode'))} busy={busyTabs.has(tool.id) || (tool.id === 'advmodels' && busyTabs.has('mcode'))} onClick={() => onTab(tool.id)}>
      {tool.label}
      <ProBadge show={locked.has(tool.id)} />
    </Tab>)}
  </nav>;
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
        <div className="rounded-(--sem-radius-card) border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)', maxWidth: 520 }}>
          <div className="text-sm font-semibold">This page hit a problem</div>
          <div className="text-xs mt-1" style={{ color: 'var(--sem-muted)' }}>
            Nothing was changed and your model is still open. Move to another page, or reload to start this one again.
          </div>
          <button className="sem-btn sem-btn-primary mt-4" onClick={() => location.reload()}>Reload page</button>
        </div>
      </div>
    );
  }
}

// Primary area button.
// `locked` is on the AREA button, not only on the segment, because an area can have one tool and therefore no
// segment strip to wear the pill: Workflows is exactly that, and it is the one paid tab in the product. Before
// this the pill had nowhere to render and the free plan showed no sign that Workflows was paid at all.
function GroupTab({ active, onClick, children, unseen, busy, locked, title }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean; locked?: boolean; title?: string }) {
  return (
    <button onClick={onClick} title={title} aria-current={active ? 'page' : undefined} className="sem-tab sem-tab-lg">
      {children}
      <ProBadge show={!!locked} />
      {busy && <TabBusyGlyph title="An operation is running in this group" />}
      {unseen && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="Your assistant ran something in this group" />}
    </button>
  );
}

function Tab({ id, active, onClick, children, unseen, busy }: { id?: string; active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean }) {
  return (
    // data-tab is the STABLE handle on a segment. A Pro pill changes the button's text, so a deep link or a
    // driver that matched on the words would stop finding a tool the moment that tool was locked.
    // aria-current is the state, and the look is drawn FROM it: .sem-tab[aria-current="page"] in styles.css
    // (Instrument: the text colour, the strong weight and a line under it). A screen reader cannot hear a
    // background, and this row marked the open tool with colour alone until 23 September 2026.
    <button onClick={onClick} data-tab={id} aria-current={active ? 'page' : undefined} className="sem-tab">
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

// The findings Semanticus cannot fix on its own, as ONE task for the assistant (Kane's Q4, 8 October 2026). The
// brief lists each as rule, object and a stable key, so the assistant asks the engine for that finding's own fix
// prompt instead of this page copying hundreds of them. The Plan task is done when the assistant says so: a
// model check cannot judge many findings at once.
const RESTS_FIX = new Set(['AiContent', 'Proposal']);
function restOfFindings(card: Scorecard): Finding[] {
  return card.findings.filter((f) => (!f.waived || f.waivedAsTwin) && RESTS_FIX.has(f.fix));
}
function restTask(list: Finding[]): PlanTaskRequest {
  const head = `Fix these ${list.length} AI readiness findings that Semanticus cannot fix by itself. For each line, ask Semanticus for the fix prompt for that rule and object, then make the change. Skip a finding you disagree with and say why.`;
  let brief = head;
  let listed = 0;
  for (const f of list) {
    const line = `\n- ${f.ruleTitle}: ${f.objectName} (${f.ruleId}|${f.objectRef})`;
    if ((brief + line).length > 3800) break;
    brief += line; listed += 1;
  }
  if (listed < list.length) brief += `\n- and ${list.length - listed} more: check AI readiness again for the rest.`;
  return { words: clampWords(`Fix ${list.length} AI readiness findings`), brief, sourcePage: 'readiness', objects: [], doneWhen: 'assistant_says', originLink: { kind: 'page', ref: 'readiness' } };
}

function Hero({ card, trend, busy, onSafe }: { card: Scorecard; trend: number[]; busy: boolean; onSafe: () => void }) {
  usePlanRowsSeen();
  // Only what no plan row carries yet: readiness findings join the Plan by themselves (Kane, 11 Oct).
  const rest = notOnPlan(restOfFindings(card));
  // The grade is a big letter (--sem-text-grade) and the score a number beside it, both in the band colour (Kane, 8 Oct):
  // one band function, readinessband.mjs, so the letter and every bar below it cannot disagree. The colour is never
  // alone, the number is always shown. The coverage figures are one line of text, not a row of pills.
  return (
    <ReadyPanel>
      <div className="flex items-center gap-6">
        <div className={`flex items-baseline gap-2 shrink-0 sem-rag-${band(card.overall)}`} title={`Grade ${card.grade}. This is the AI readiness score: how well this model explains itself to AI.`}>
          <span className="sem-rag-grade" data-testid="readiness-grade"><GradeFlip grade={card.grade} /></span>
          <span className="sem-num sem-rag-score">{card.overall.toFixed(0)}</span>
          <span className="sem-text-meta">of 100</span>
        </div>
        <div className="flex-1 min-w-0">
          <h2 className="sem-card-title">AI readiness</h2>
          <div className="sem-text-meta mt-1">
            <span className="sem-motion-anchor">{card.findings.filter((f) => !f.waived || f.waivedAsTwin).length} findings
              <BigWin kind={card.findings.length === 0 ? 'zero-findings' : undefined} eventKey={card.findings.length === 0} /></span>
          </div>
          {trend.length >= 2 && (
            <div className="mt-1" style={{ maxWidth: 320 }}><Sparkline values={trend} /></div>
          )}
          <div className="sem-text-meta mt-1">
            {Object.entries(card.coverage).map(([k, v], i) => (
              <span key={k}>{i > 0 ? ' · ' : ''}{prettyKey(k)} <span className="sem-num" style={{ color: 'var(--sem-fg)' }}>{v}%</span></span>
            ))}
          </div>
        </div>
        <div className="flex items-center gap-2 flex-wrap shrink-0">
          {(busy || card.safeFixCount > 0) && <button className="sem-btn sem-btn-primary" disabled={busy} onClick={onSafe}
            title={`Apply every safe fix in one undoable step and re-check the score.`}>
            {busy ? 'Applying…' : `Apply ${card.safeFixCount} safe fix${card.safeFixCount === 1 ? '' : 'es'}`}
          </button>}
          {rest.length > 0 && (
            <GiveToAssistant size="md" bare label="Give the rest to your assistant" sendKey="readiness|rest" build={() => restTask(rest)} />
          )}
        </div>
      </div>
    </ReadyPanel>
  );
}

function Categories({ card }: { card: Scorecard }) {
  const cats = card.categories.filter((c) => c.hasRules);
  return (
    <ReadyPanel>
      <h2 className="sem-card-title">Categories</h2>
      <RagBars rows={cats.map((c) => ({ name: plainCategory(c.category), score: c.score, detail: `${c.violations} of ${c.applicable}` }))} />
    </ReadyPanel>
  );
}

// Findings are few enough to read without a filter below this many.
const FILTER_ROW_FROM = 9;
const sevNum = (s: string) => (s === 'Critical' || s === 'High' ? 3 : s === 'Medium' ? 2 : 1);

function Findings({ card, onRescan }: { card: Scorecard; onRescan: () => void }) {
  const [filter, setFilter] = useState<'all' | 'SafeFix' | 'AiContent' | 'Proposal'>('all');
  const [waiveErr, setWaiveErr] = useState<string | null>(null);
  const { state: working, keyOf, fix } = useFixState('applyFix', 'getFixPrompt');

  // A finding another acceptance covers is left alone but is not accepted: it stays in the open list, with its own words.
  const accepted = (f: Finding) => !!f.waived && !f.waivedAsTwin;
  const activeFindings = card.findings.filter((f) => !accepted(f));
  const waivedFindings = card.findings.filter(accepted);
  const counts = activeFindings.reduce((m, f) => { m[f.fix] = (m[f.fix] ?? 0) + 1; return m; }, {} as Record<string, number>);
  const list = activeFindings.filter((f) => filter === 'all' || f.fix === filter);
  const byKey = new Map(card.findings.map((f) => [f.objectRef + '|' + f.ruleId, f]));
  const toRow = (f: Finding): FindingRow => ({
    // Provenance in the rule header: a finding from a model-embedded (user-authored) rule says so.
    ruleId: f.ruleId, ruleName: plainRuleName(f.ruleTitle) + (f.custom ? ' (custom rule)' : ''), category: f.category, severity: sevNum(f.severity),
    // Prefer the analyst rendering (agent-only op hint removed server-side); fall back to the raw message when absent
    // OR blank (defence in depth: a blank displayMessage must never render as an empty finding).
    objectRef: f.objectRef, objectName: f.objectName, message: f.displayMessage?.trim() ? f.displayMessage : f.message,
    waived: accepted(f), waiverReason: f.waiverReason, waiverRuleLevel: f.waiverRuleLevel, noLongerFires: f.noLongerFires, waivedAsTwin: f.waivedAsTwin, coveredBy: f.coveredBy, coveredReason: f.coveredReason, wholeRuleRow: f.wholeRuleRow,
    tag: { label: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).label, color: (FIX_STYLE[f.fix] ?? FIX_STYLE.None).color },
  });
  const rows: FindingRow[] = list.map(toRow);
  const waivedRows: FindingRow[] = waivedFindings.map(toRow);
  const run = (p: Promise<unknown>) => { setWaiveErr(null); void p.then(onRescan).catch((e) => setWaiveErr(String((e as Error).message ?? e))); };

  // true only when the engine took the acceptance: the box keeps the person's reason on anything else (R-B).
  const waiveWrite = async (ruleId: string, objectRef: string, reason: string): Promise<boolean> => {
    setWaiveErr(null);
    try { await rpc('waiveFinding', 'air', ruleId, objectRef, reason); } catch (e) { setWaiveErr(String((e as Error).message ?? e)); return false; }
    void Promise.resolve(onRescan()).catch((e) => setWaiveErr(String((e as Error).message ?? e)));
    return true;
  };
  const waive = (r: FindingRow, reason: string) => waiveWrite(r.ruleId, r.objectRef, reason);
  const waiveRule = (ruleId: string, reason: string) => waiveWrite(ruleId, '*', reason);
  // un-waive routes to the rule-level waiver ('*') when waived model-wide, else the single instance.
  const unwaive = (r: FindingRow) => run(rpc('unwaiveFinding', 'air', r.ruleId, r.waiverRuleLevel ? '*' : r.objectRef));

  const actions = (r: FindingRow) => {
    const f = byKey.get(r.objectRef + '|' + r.ruleId); if (!f) return null;
    const st = working[keyOf(f.ruleId, f.objectRef)];
    // Every finding gets a fix affordance: a deterministic Apply for SafeFix, and the free "Give to your assistant" for
    // everything else (AiContent AND Proposal/Review — get_fix_prompt's default branch builds a grounded
    // prompt for any rule). A finding with no button is a dead end the free-tier promise forbids.
    const fixBtn = f.fix === 'SafeFix'
      ? (st === 'done'
        ? <span className="sem-text-meta">Fixed</span>
        : <button className="sem-btn sem-btn-sm" disabled={st === 'fixing'} onClick={() => fix(f.ruleId, f.objectRef, onRescan)}>{st === 'fixing' ? '…' : 'Apply'}</button>)
      : <GiveOrOnPlan ruleId={f.ruleId} objectRef={f.objectRef} sendKey={`readiness|${f.ruleId}|${f.objectRef}`} build={findingBuilder('air', f)} />;
    return <span className="flex items-center gap-1">{fixBtn}<WaiveControl onWaive={(reason) => waive(r, reason)} onUnwaive={() => unwaive(r)} /></span>;
  };
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Accept for the whole model" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl subtle label="Accept for the whole model" title="Accept every instance of this rule across the model" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => run(rpc('unwaiveFinding', 'air', ruleId, '*'))} />;

  return (
    <>
      <ReadyPanel>
        <div className="flex items-center gap-2">
          <h2 className="sem-card-title">Findings <span className="sem-num font-normal" style={{ color: 'var(--sem-muted)' }}>{activeFindings.length}</span></h2>
        </div>
        {waiveErr && <div role="alert" className="mt-2 flex items-start gap-2"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{waiveErr}</div>}
        {/* The fix-type filter joins the level filter in ONE row, with no caption of its own, and a chip with nothing
            behind it is not drawn. Pressing the lit chip again clears it, so there is no "Any fix" chip. Under nine
            findings the row is not needed at all, the same rule findings.tsx uses for the level filter. */}
        <div className="mt-2"><GroupedFindings rows={rows} renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)}
          extraFilters={activeFindings.length < FILTER_ROW_FROM ? undefined : (
            <div className="sem-seg flex-wrap" role="group" aria-label="Filter findings by who fixes them">
              {([['SafeFix', 'Safe', 'Fixes this app can apply on its own'], ['AiContent', 'AI', 'Wording your assistant can write'], ['Proposal', 'Review', 'Changes that need your decision']] as const)
                .filter(([key]) => (counts[key] ?? 0) > 0 || filter === key)
                .map(([key, label, title]) => <button key={key} type="button" aria-pressed={filter === key} onClick={() => setFilter(filter === key ? 'all' : key)} className="sem-seg-item" title={title}>
                  <span>{label}</span><span className="sem-num ml-1">{counts[key] ?? 0}</span></button>)}
            </div>
          )} /></div>
      </ReadyPanel>
      <WaivedList rows={waivedRows} onUnwaive={unwaive} />
    </>
  );
}

// The readiness page's own card and notice. Local copies, because Panel and Banner below are shared with the
// Size by table page, which is drawn by another lane.
function ReadyPanel({ children }: { children: React.ReactNode }) {
  return <section className="sem-card">{children}</section>;
}
function ReadyBanner({ children, tone }: { children: React.ReactNode; tone: 'bad' | 'warn' }) {
  return <div role={tone === 'bad' ? 'alert' : undefined} className="sem-card flex items-start gap-2"><span className={'sem-chip shrink-0 sem-chip-' + tone} aria-hidden="true" />{children}</div>;
}


// ---- small primitives ------------------------------------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`sem-card ${className}`}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="sem-card-title">{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title} className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled, title }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title} className="sem-btn sem-btn-sm">
      {children}
    </button>
  );
}
// One item of a segmented switch: the pressed state is aria-pressed, so the look and the state cannot drift apart.
function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return <button type="button" onClick={onClick} aria-pressed={active} className="sem-seg-item">{children}</button>;
}
// A notice is a sentence with a dot in its colour, not a tinted box: the dot marks the state and the words stay
// in the text colour, so they read at full contrast in every theme (amber text on Light Modern was 2.7:1).
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="xe-msg st-notice" style={{ '--xe-dot': color } as React.CSSProperties}><div className="min-w-0 flex-1">{children}</div></div>;
}
// A legend key: the word with a dot in the series colour.
function Key({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="sem-chip sem-chip-dot" style={{ '--xe-dot': color } as React.CSSProperties}>{children}</span>;
}
function Loading() { return <Panel><div className="sem-text-meta">Scanning model…</div></Panel>; }

// The one frame between opening a Pro tool and the entitlement answering. Neither the real page nor the
// locked preview is honest here: loading the page would run a paid read on a plan that may not have it, and
// showing the preview would flash an upsell at a paying customer. So it says what it is doing.
function FeatureChecking() {
  return (
    // On the page margin like every page's first line, not floating in the middle of an empty page.
    <div className="h-full p-6" data-testid="feature-checking">
      <div className="sem-text-meta" role="status">
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
        <div className="text-sm font-semibold mb-1">Semanticus is not connected to its engine.</div>
        <div className="text-xs mb-3" style={{ color: 'var(--sem-muted)' }}>
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

function Empty({ onOpen, reconnectName, offer }: { onOpen: () => void; reconnectName?: string | null; offer?: boolean }) {
  // The model the person was on, waiting for a sign-in: say so and make the one button the first thing, rather than
  // sending them to a page of local files (Kane, K15).
  if (reconnectName) {
    return (
      <div className="h-full flex items-center justify-center p-8">
        <div className="model-empty-shell text-center max-w-sm" data-testid="reconnect-pending">
          <div className="text-sm font-semibold mb-1">{reconnectName}</div>
          <div className="text-xs mb-3" style={{ color: 'var(--sem-muted)' }}>{offer ? 'Your last model.' : 'Sign in again to open your last model.'}</div>
          <div className="flex items-center justify-center gap-2 flex-wrap">
            <button type="button" className="sem-primary" data-testid="reconnect-open" onClick={() => runHostCommand('semanticus.signInToReconnect')}>{offer ? 'Open it' : 'Sign in to reconnect'}</button>
            <button type="button" onClick={onOpen}>Open another model</button>
          </div>
        </div>
      </div>
    );
  }
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="model-empty-shell text-center max-w-sm">
        <div className="text-sm font-semibold mb-1">Open a model to begin</div>
        <div className="text-xs mb-3" style={{ color: 'var(--sem-muted)' }}>Choose local model files, a running Power BI Desktop model, or a published model.</div>
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

// ---- Opportunities: deterministic, evidence-backed findings the scan justifies -----------------------------------
type OppGroup = 'Can remove' | 'Worth reviewing' | 'Behavior cleanup';
const OPP_GROUP_ORDER: string[] = ['Can remove', 'Worth reviewing', 'Behavior cleanup'];
interface Opp {
  ruleId: string; ruleName: string; group: OppGroup; severity: number;
  ref: string; name: string; table: string; column: string; message: string;
  kind: 'unused' | 'dict' | 'hash' | 'summarize' | 'share';
  bytes: number;                              // storage weight — ranks within a group, never masquerades as severity
  encoding?: string; data?: number; dict?: number; hash?: number; total?: number; pctModel?: number; pctTable?: number;
  effect?: string;                            // the honest storage-effect label shown beside the actions
}

// The opportunities themselves are computed by the ENGINE (F-230) and arrive with the model facts snapshot
// (getModelFacts -> storageOpportunities), so both doors see the same findings. The engine port keeps the four
// rules, thresholds, evidence gates and copy this tab used to compute here (Semanticus.Engine/Facts/StorageOpportunities.cs).
interface StorageFacts { revision: number; storageOpportunities?: Opp[] }

function oppToRow(o: Opp, assistantConnected = true): FindingRow {
  return { ruleId: o.ruleId, ruleName: plainRuleName(o.ruleName), category: o.group, severity: o.severity, objectRef: o.ref, objectName: o.name, message: adviceFor(o.message, assistantConnected) };
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

// The grounded prompt "Give to your assistant" puts on the Plan (and Copy copies) for the user's assistant — the BPA get_fix_prompt pattern, but composed
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
  /** The last graph read, kept while a fresh one is read after a model change, so the overview (and its filter box) is not taken off the page (sweep 3, site 5). Selection still waits for `meta`. */
  overviewMeta: ModelGraph | null;
  stagedCols: Set<string> | null;
  setStagedCols: React.Dispatch<React.SetStateAction<Set<string> | null>>;
  colRows: ColumnMeta[] | null | undefined;
  setColRows: React.Dispatch<React.SetStateAction<ColumnMeta[] | null | undefined>>;
  unused: UnusedResult | null | undefined;
  setUnused: React.Dispatch<React.SetStateAction<UnusedResult | null | undefined>>;
  facts: StorageFacts | null | undefined;
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
  const [overviewMeta, setOverviewMeta] = useState<ModelGraph | null>(null);
  const [stagedCols, setStagedCols] = useState<Set<string> | null>(null);
  const [colRows, setColRows] = useState<ColumnMeta[] | null | undefined>(undefined);
  const [unused, setUnused] = useState<UnusedResult | null | undefined>(undefined);
  const [facts, setFacts] = useState<StorageFacts | null | undefined>(undefined);
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
    const graph = rpc<ModelGraph>('getModelGraph');
    graph
      .then((g) => { if (seq === loadSeq.current) setMeta(g); })
      .catch(() => { if (seq === loadSeq.current) setMeta(null); });
    // The overview follows the same read, but nothing nulls it while a read is on its way, so the filter box above its tables stays (sweep 3, site 5).
    graph.then((g) => { if (seq === loadSeq.current) setOverviewMeta(g); }, () => { if (seq === loadSeq.current) setOverviewMeta(null); });
    rpc<ColumnMeta[]>('listColumns')
      .then((cs) => { if (seq === loadSeq.current) { setColRows(cs); setStagedCols(new Set(cs.map((c) => colKey(c.table, c.name)))); } })
      .catch(() => { if (seq === loadSeq.current) { setColRows(null); setStagedCols(null); } });
    rpc<UnusedResult>('unusedObjects')
      .then((u) => { if (seq === loadSeq.current) setUnused(u); })
      .catch(() => { if (seq === loadSeq.current) setUnused(null); });
    rpc<StorageFacts>('getModelFacts')
      .then((f) => { if (seq === loadSeq.current) setFacts(f); })
      .catch(() => { if (seq === loadSeq.current) setFacts(null); });
  }, []);
  useEffect(() => {
    if (!activated) return;
    loadStaged();
    const reset = () => { setMeta(null); setStagedCols(null); setColRows(undefined); setUnused(undefined); setFacts(undefined); };
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
      scanState, setScanState, meta, setMeta, overviewMeta, stagedCols, setStagedCols, colRows, setColRows, unused, setUnused, facts,
      prevSnap, setPrevSnap, pinnedSnap, setPinnedSnap, compareMode, setCompareMode, barMode, setBarMode,
      explorerFilter, setExplorerFilter, busy, err, setErr, claudeEvent, setClaudeEvent,
      anchor, scanUsable, scanCurrent, scan, sessionPrev, persistedFor,
    }}>
      {children}
    </StorageTabStateContext.Provider>
  );
}

function StatsView({ onReviewAsPlan, navTarget }: { onReviewAsPlan?: () => void; navTarget?: { table: string; nonce: number } | null }) {
  const assistantConnected = useDriver().assistantConnected;
  const { conn, session, context } = useConnection();
  const {
    scanState, meta, overviewMeta, stagedCols, colRows, unused, facts, prevSnap, setPrevSnap, pinnedSnap, setPinnedSnap,
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
  const editingEvidenceAllowed = scanCurrent && relationshipAllowsStorageEdits(context?.relationship);
  const reverifiedPlanAllowed = scanCurrent && relationshipAllowsReverifiedDeletePlan(context?.relationship);
  const linkedCopyObservation = scanCurrent && storageEvidenceLevel(context?.relationship) === 'staleQueryCopy';
  // The engine computes the findings from its own storage read and the connection relationship (F-230); the tab
  // only shows them beside a scan, as it always did.
  const opps = useMemo<Opp[]>(() => report ? (facts?.storageOpportunities ?? []) : [], [report, facts]);
  const oppsRef = useRef<Opp[]>([]); oppsRef.current = opps;
  // The biggest cost is always named (UAT P16): a column at a clear share of the model is a finding even when the engine's byte floors let it pass.
  const shownOpps = useMemo<Opp[]>(() => {
    const f = report ? biggestShare(report.topColumns) : null;
    if (!f) return opps;
    const ref = colRef(f.table, f.column);
    if (opps.some((o) => o.ruleId === 'STORAGE-SHARE')) return opps;   // the engine already named it
    return [{ ruleId: 'STORAGE-SHARE', ruleName: shareWords(f), group: 'Worth reviewing', severity: 1, ref, name: `${f.table}[${f.column}]`, table: f.table, column: f.column,
      message: shareAdvice(f), kind: 'share', bytes: f.totalSize, total: f.totalSize, pctModel: f.pct }, ...opps];
  }, [report, opps]);

  // Is the finding evidence COMPLETE and SUCCESSFUL for THIS scan? Both enrichment reads must have SUCCEEDED —
  // distinguishing the three states each carries: undefined = still loading, null = the read FAILED, a value =
  // loaded (possibly empty, which is a legitimate complete result). While either is loading, `opps` is missing
  // its unused/summarize findings; on failure it is permanently missing them. A finding-delta or a persisted
  // baseline computed from that partial set would invent transient "resolved" claims and, once the read recovers,
  // report the recovered findings as "introduced". So the finding set is trustworthy only when BOTH succeeded
  // (`!= null` excludes undefined AND null); a failed enrichment yields no comparison and no persisted snapshot
  // rather than a poisoned one. Reachability (meta) is not part of the finding SET, so it is not gated here.
  const evidenceComplete = unused != null && colRows != null && facts != null;

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
    // A column name used in two tables would be two bars with the same name: those carry their table in the label.
    const seen = new Map<string, number>(); for (const c of report.topColumns) seen.set(c.column, (seen.get(c.column) ?? 0) + 1);
    return report.topColumns.map((c) => { const ts = tableSizeByName.get(c.table) ?? 0; return { ref: colRef(c.table, c.column), label: (seen.get(c.column) ?? 0) > 1 ? `${c.table} · ${c.column}` : c.column, sublabel: c.table, data: c.dataSize ?? 0, dict: c.dictionarySize ?? 0, hash: c.hashIndexSize ?? 0, total: c.totalSize, pctModel: c.pctOfModel, pctTable: ts > 0 ? Math.round((100 * c.totalSize) / ts) : 0 }; }).sort((a, b) => b.total - a.total);
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
  // The editing guard is the control's own disabled state (OppActions passes editingAllowed), so a task is never
  // built from a scan that is not proven to describe the open model.
  const giveTask = (o: Opp): PlanTaskRequest => storageTask(o, buildAskPrompt(o, storageMode, report?.caveat));
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

  // "Review these together" dispatches REAL plan items, kind 'delete_if_unused' and never bare 'delete': the Change
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
  const modeCaveat = report?.caveat ?? (storageMode === 'unknown'
    ? 'Storage mode could not be confirmed. Storage sizes and row counts may reflect only data currently in memory, so they are not treated as full model totals.'
    : null);
  const modelShareLabel = storageMode === 'directLake' ? '% resident' : storageMode === 'unknown' ? '% observed' : '% of model';
  const tableShareLabel = storageMode === 'directLake' ? '% resident table' : storageMode === 'unknown' ? '% observed table' : '% of table';
  const sizeLabel = 'Size';
  const removalPlanCandidates = useMemo(() => opps.filter((o) => o.kind === 'unused' && !refIsAmbiguous(o.table, o.column)), [opps]);
  // Pin is armed only once the enrichment reads SUCCEEDED (evidenceComplete — findingKeys complete, not a partial
  // set from a loading or FAILED read) AND the scan's identity is confirmed (bracketed fingerprint). A baseline
  // pinned from a failed unusedObjects/listColumns read would freeze an incomplete finding set that later reads back
  // as findings "introduced"; a missing fingerprint could never honestly anchor a campaign.
  const pinReady = !!curSnap && evidenceComplete && !!curSnap.fingerprintKey && scanCurrent && storageMode !== 'unknown';

  // The model the sizes come from, named once (Z2). Sizes are the test model's, never the staged edits.
  const sizeSource = context?.querying?.modelName || context?.querying?.database || conn?.dataSource || 'the test model';
  const delta = compare && comparison ? comp.known - comparison.known : null;

  return (
    <div className="sem-page sem-evidence-page" data-testid="size-page">
      {/* One header line, no card (Z1): the total and what it covers, then Rescan and the menu that holds the scan
          and compare controls (Z4). The scan also runs by itself when a test model connects, so Rescan is quiet. */}
      {(report || overviewMeta || conn?.connected) && (
        <div className="sem-inline flex-wrap" data-testid="size-header">
          {report ? <>
            <span className="text-(length:--sem-text-page) leading-(--sem-lh-page) font-semibold tnum">{fmtMB(comp.known)}</span>
            <span className="sem-text-meta">
              {storageMode === 'directLake' ? 'resident, ' : storageMode === 'unknown' ? 'observed, ' : ''}in {report.tables.length} {report.tables.length === 1 ? 'table' : 'tables'}
              {storageMode === 'import' ? ` · ${fmtInt(totalRows)} rows` : storageMode === 'directLake' ? ' · rows resident only' : ' · row count unavailable until storage mode is confirmed'}
              {scanTime ? ` · scanned ${scanTime}` : ''}
            </span>
            {delta != null && delta !== 0 && <span className={'xe-dot sem-num sem-text-meta ' + (delta < 0 ? 'xe-dot-good' : 'xe-dot-warn')} title={`A measured ${delta < 0 ? 'decrease' : 'increase'} since the ${comparisonLabel}. A refresh or the cache can change it too.`}>{delta < 0 ? '−' : '+'}{fmtMB(Math.abs(delta))} since the {comparisonLabel}</span>}
          </> : overviewMeta ? <span className="sem-text-meta">
            {overviewMeta.tables.length} tables · {fmtInt(overviewMeta.tables.reduce((n, t) => n + t.columns, 0))} columns · {fmtInt(overviewMeta.tables.reduce((n, t) => n + t.measures, 0))} measures
          </span> : null}
          {conn?.connected && <div className="ml-auto flex items-center gap-2">
            <button type="button" className={'sem-btn' + (report ? ' sem-btn-quiet' : ' sem-btn-primary')} disabled={busy} onClick={scan}>{busy ? 'Scanning…' : report ? 'Rescan' : 'Scan storage'}</button>
            {report && <RowMenu label="⋯" align="end" title="Compare scans">
              <button type="button" className="sem-btn sem-btn-sm" aria-pressed={compareMode === 'previous'} disabled={!comparisonEnabled} onClick={() => setCompareMode('previous')}>Compare with the previous scan</button>
              {pinnedSnap && <button type="button" className="sem-btn sem-btn-sm" aria-pressed={compareMode === 'baseline'} disabled={!comparisonEnabled} onClick={() => setCompareMode('baseline')}>Compare with the pinned baseline</button>}
              <button type="button" className="sem-btn sem-btn-sm" onClick={pinBaseline} disabled={!pinReady}
                title={pinReady
                  ? 'Freeze this scan as the comparison point for a clean-up campaign'
                  : storageMode === 'unknown'
                    ? 'Pinning is unavailable until storage mode is confirmed'
                    : 'Pinning needs the scan to finish reading columns and usage, a confirmed model identity, and the current test model'}>
                Pin this scan as the baseline</button>
              {!comparisonEnabled && storageMode === 'unknown' && <div className="sem-text-meta px-2">Comparison waits until the storage mode is confirmed.</div>}
              {!comparisonEnabled && storageMode !== 'unknown' && !scanCurrent && <div className="sem-text-meta px-2">Comparison is off: this scan is no longer from the current test model.</div>}
              {comparison && !decision.available && decision.reason === 'fingerprint' && <div className="sem-text-meta px-2">Comparison is off: the identity of one of the scans could not be confirmed.</div>}
              {comparisonEnabled && !comparison && <div className="sem-text-meta px-2">Nothing to compare with yet.</div>}
            </RowMenu>}
          </div>}
        </div>
      )}
      {!conn?.connected && <ConnectBar hint="Sizes come from a running test model." />}
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}

      {/* One amber line, said once (Z2). It replaces three warnings and the unsaved-edits chip: the sizes are the test
          model's. The chip stays only where the test model IS the open model, because there unsaved edits are the news. */}
      {report && linkedCopyObservation
        ? <Banner color="var(--sem-warn)">Sizes come from {sizeSource}, not your edits. Use them as a guide.</Banner>
        : <QueryStalenessChip />}

      {claudeEvent && <ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} />}

      {report && storageMode !== 'import' && modeCaveat && <Banner color="var(--sem-warn)">{modeCaveat}</Banner>}
      {report && compare && storageMode === 'directLake' && (
        <Banner color="var(--sem-warn)">Direct Lake: both scans measured only what was in memory at the time, so a change can come from the cache and not from storage.</Banner>
      )}
      {report && compare?.structureChanged && (
        <Banner color="var(--sem-warn)">The model's structure changed since the {comparisonLabel}, often from the fix you applied. The change is still measured. Read it as before and after.</Banner>
      )}

      {/* Offline: the model's tables while no live scan exists yet (before connect, or pre-scan). */}
      {!report && overviewMeta && (
        <Panel>
          <SectionTitle>Tables <span className="sem-num" style={{ color: 'var(--sem-muted)' }}>{overviewMeta.tables.length}</span></SectionTitle>
          <div className="mt-1">
            <SortableTable
              rows={overviewMeta.tables}
              filterText={(t) => t.name}
              filterPlaceholder="Filter tables…"
              initialSort={{ key: 'columns', dir: -1 }}
              maxHeight={520}
              onRowClick={meta ? (t) => selectInProperties('table:' + t.name) : undefined}
              onRowFocus={meta ? (t) => focusSelectInProperties('table:' + t.name) : undefined}
              cols={[
                { key: 'name', label: 'Table', sortVal: (t) => t.name, render: (t) => <span className="font-medium" style={{ opacity: t.isHidden ? 0.55 : 1 }}>{t.name}</span> },
                { key: 'columns', label: 'Cols', align: 'right', sortVal: (t) => t.columns, render: (t) => <span className="tnum">{t.columns}</span> },
                { key: 'measures', label: 'Measures', align: 'right', sortVal: (t) => t.measures, render: (t) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{t.measures}</span> },
                { key: 'kind', label: 'Kind', sortVal: (t) => (t.isDateTable ? 'date' : t.isCalculated ? 'calc' : t.isHidden ? 'hidden' : ''), render: (t) => (
                  t.isDateTable ? <span className="sem-text-meta">date</span>
                  : t.isCalculated ? <span className="sem-text-meta">calculated</span>
                  : t.isHidden ? <span className="sem-text-meta">hidden</span> : <span /> ) },
                { key: 'reveal', label: '', align: 'right', sortVal: () => '', render: (t) => <RevealBtn objRef={'table:' + t.name} /> },
              ]}
            />
          </div>
        </Panel>
      )}

      {report && (
        <>
          {/* Ways to save space, first (Z5): the assistant is the main action on each row (D4) and the by-hand actions
              fold into the row's menu. One card, no side rail. Nothing to fix is one line, not a card. */}
          {shownOpps.length === 0
            ? <div className="xe-msg" style={{ '--xe-dot': 'var(--sem-good)' } as React.CSSProperties}>{verdictWords(facts === undefined || unused === undefined ? 'loading' : facts === null ? 'failed' : 'done', 0)}</div>
            : <Panel>
                <div ref={oppsPanelRef} className="scroll-mt-2" />
                <div className="flex items-center gap-2">
                  <SectionTitle>Ways to save space <span className="sem-num" style={{ color: 'var(--sem-muted)' }}>{shownOpps.length}</span></SectionTitle>
                  {removalPlanCandidates.length > 0 && onReviewAsPlan && (
                    <div className="ml-auto"><Button primary disabled={!reverifiedPlanAllowed} onClick={() => void reviewAsPlanItems(removalPlanCandidates)}
                      title={!reverifiedPlanAllowed ? planBlockedReason ?? undefined : `Adds ${removalPlanCandidates.length} ${removalPlanCandidates.length === 1 ? 'removal' : 'removals'} to your plan. Semanticus checks them again before anything changes. It does not promise how much space you save.`}>
                      Review these together →
                    </Button></div>
                  )}
                </div>
                {unused === null && <div className="mt-1 xe-msg xe-msg-warn">Usage could not be determined (the safe-to-remove check did not run), so removal actions are disabled.</div>}
                {editingBlockedReason && !linkedCopyObservation && <div className="mt-1 xe-msg xe-msg-warn">{editingBlockedReason}</div>}
                <div className="mt-2">
                  <GroupedFindings rows={shownOpps.map((o) => oppToRow(o, assistantConnected))} categoryOrder={OPP_GROUP_ORDER}
                    renderActions={(r) => { const o = shownOpps.find((x) => x.ruleId === r.ruleId && x.ref === r.objectRef); return o
                      ? <OppActions o={o} assistantConnected={assistantConnected} reachable={oppReachable(o)} usageKnown={unused !== null} ambiguous={refIsAmbiguous(o.table, o.column)}
                          editingAllowed={editingEvidenceAllowed} editingBlockedReason={editingBlockedReason ?? undefined}
                          planAllowed={reverifiedPlanAllowed} planBlockedReason={planBlockedReason ?? undefined}
                          onGiveTask={giveTask} onDoNotSummarize={doNotSummarize} onHide={hideColumn} onDelete={deleteColumn}
                          onChangeType={(ref) => selectInProperties(ref)} onReviewAsPlan={onReviewAsPlan ? () => void reviewAsPlanItems([o]) : undefined} />
                      : null; }} />
                </div>
                {changed && <ChangedSince changed={changed} label={comparisonLabel} />}
              </Panel>}

          {/* The bars and the one table under them (Z6 to Z9): one Columns | Tables switch drives both, the legend
              holds the component values, and the totals no longer have a card of their own. */}
          <Panel>
            <div className="flex items-center gap-2 flex-wrap">
              <SectionTitle>{barMode === 'tables' ? 'Largest tables' : 'Largest columns'}</SectionTitle>
              <div className="sem-seg ml-1">
                <Chip active={barMode === 'columns'} onClick={() => setBarMode('columns')}>Columns</Chip>
                <Chip active={barMode === 'tables'} onClick={() => setBarMode('tables')}>Tables</Chip>
              </div>
              <div className="ml-auto flex gap-3 flex-wrap">
                <Key color={VPAQ_COMPONENT_COLORS.data}>Data <span className="sem-num" title={compare?.componentsComparable && comparison ? `Before: ${fmtMB(comparison.data)}` : undefined}>{fmtMB(comp.data)}</span></Key>
                <Key color={VPAQ_COMPONENT_COLORS.dict}>Different values <span className="sem-num" title={compare?.componentsComparable && comparison ? `Before: ${fmtMB(comparison.dict)}` : undefined}>{fmtMB(comp.dict)}</span></Key>
                <Key color={VPAQ_COMPONENT_COLORS.hash}>Lookup help <span className="sem-num" title={compare?.componentsComparable && comparison ? `Before: ${fmtMB(comparison.hash)}` : undefined}>{fmtMB(comp.hash)}</span></Key>
                {barMode === 'tables' && barItems.some((b) => (b.unattributed ?? 0) > 0) && (
                  <span title={storageMode === 'unknown' ? 'Table values are observed storage only. Components come from the scanned columns and the rest is unattributed.' : 'Table totals are exact for this storage mode. Components are estimated from the scanned columns and the rest is unattributed.'}>
                    <Key color={VPAQ_UNATTRIBUTED_COLOR}>Unattributed</Key>
                  </span>
                )}
              </div>
            </div>
            <div className="mt-2"><VpaqComponentBars items={barItems} storageMode={storageMode} onSelect={onBarSelect} /></div>
            {comp.attributed !== comp.known && (
              <div className="mt-1 sem-text-meta">Bars cover the {report.topColumns.length} largest columns ({fmtMB(comp.attributed)} of {fmtMB(comp.known)}).</div>
            )}
            {compare && !compare.componentsComparable && (
              <div className="mt-1 sem-text-meta">The two scans cover different columns, so only the total can be compared.</div>
            )}
            <div className="mt-3 pt-3 border-t" style={{ borderColor: 'var(--sem-rule)' }}>
              {barMode === 'tables' ? (
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
              ) : (
                <>
                  {explorerFilter && <div className="mb-2"><button onClick={() => setExplorerFilter('')} className="sem-btn sem-btn-sm">Filtered to {explorerFilter} ✕</button></div>}
                  <SortableTable
                    rows={explorerFilter ? report.topColumns.filter((c) => c.table === explorerFilter) : report.topColumns}
                    filterText={(c) => c.table + ' ' + c.column}
                    filterPlaceholder="Filter columns…"
                    initialSort={{ key: 'totalSize', dir: -1 }}
                    maxHeight={460}
                    onRowClick={(c) => columnReachable(c.table, c.column) && selectInProperties('column:' + c.table + '/' + c.column)}
                    onRowFocus={(c) => columnReachable(c.table, c.column) && focusSelectInProperties('column:' + c.table + '/' + c.column)}
                    cols={[
                      { key: 'column', label: 'Column', sortVal: (c) => c.column, render: (c) => <span><span className="font-medium">{c.column}</span> <span className="text-xs" style={{ color: 'var(--sem-muted)' }}>· {c.table}</span></span> },
                      { key: 'encoding', label: 'Encoding', sortVal: (c) => c.encoding, render: (c) => <span className="sem-text-meta">{c.encoding}</span> },
                      { key: 'totalSize', label: sizeLabel, align: 'right', sortVal: (c) => c.totalSize, render: (c) => <SizeCell bytes={c.totalSize} max={maxColSize} /> },
                      { key: 'dataSize', label: 'Data', align: 'right', sortVal: (c) => c.dataSize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dataSize != null ? fmtMB(c.dataSize) : 'Not available'}</span> },
                      { key: 'dictionarySize', label: 'Values', align: 'right', sortVal: (c) => c.dictionarySize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.dictionarySize != null ? fmtMB(c.dictionarySize) : 'Not available'}</span> },
                      { key: 'hashIndexSize', label: 'Lookup', align: 'right', sortVal: (c) => c.hashIndexSize ?? 0, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.hashIndexSize != null ? fmtMB(c.hashIndexSize) : 'Not available'}</span> },
                      { key: 'pctOfModel', label: modelShareLabel, align: 'right', sortVal: (c) => c.pctOfModel, render: (c) => <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{c.pctOfModel}%</span> },
                      { key: 'pctTable', label: tableShareLabel, align: 'right', sortVal: (c) => (tableSizeByName.get(c.table) ? c.totalSize / (tableSizeByName.get(c.table) as number) : 0), render: (c) => { const ts = tableSizeByName.get(c.table) ?? 0; return <span className="tnum" style={{ color: 'var(--sem-muted)' }}>{ts > 0 ? Math.round((100 * c.totalSize) / ts) + '%' : '-'}</span>; } },
                      { key: 'reveal', label: '', align: 'right', sortVal: () => '', render: (c) => columnReachable(c.table, c.column) ? <RevealBtn objRef={'column:' + c.table + '/' + c.column} /> : null },
                    ]}
                  />
                </>
              )}
            </div>
          </Panel>
        </>
      )}
    </div>
  );
}

// The action set + effect label for one opportunity. Every mutating action carries an honest storage-effect label,
// and destructive removal is gated on a SAFE usage verdict (fail closed when usage is Unknown) AND an unambiguous
// name: '/' is legal inside table and column names but the ref grammar splits on the first slash, so a slash-bearing
// name could resolve to a DIFFERENT object than the one the finding measured — Delete and the plan hand-off both
// refuse rather than guess.
function OppActions({ o, assistantConnected, reachable, usageKnown, ambiguous, editingAllowed, editingBlockedReason, planAllowed, planBlockedReason, onGiveTask, onDoNotSummarize, onHide, onDelete, onChangeType, onReviewAsPlan }: {
  o: Opp; assistantConnected: boolean; reachable: boolean; usageKnown: boolean; ambiguous: boolean; editingAllowed: boolean; editingBlockedReason?: string; planAllowed: boolean; planBlockedReason?: string;
  onGiveTask: (o: Opp) => PlanTaskRequest; onDoNotSummarize: (o: Opp) => void; onHide: (o: Opp) => void; onDelete: (o: Opp) => void;
  onChangeType: (ref: string) => void; onReviewAsPlan?: () => void;
}) {
  const ambiguousTip = "This name contains '/', which is ambiguous for safe deletion. Rename it first, or delete it from the Model tree.";
  const blockedTip = editingBlockedReason ?? 'This action needs a scan that is proven to describe the open editing model.';
  const unavailableTip = !editingAllowed ? blockedTip : !reachable ? 'The scanned object could not be confirmed in the open editing model.' : undefined;
  // The assistant is the main action (D4). What a person does by hand folds into the row's menu, every action kept.
  const hasMenu = o.kind === 'unused' || o.kind === 'dict' || o.kind === 'summarize' || reachable;
  return (
    <div className="flex flex-col items-end gap-1">
      {o.effect && <span className="sem-label">{o.effect}</span>}
      <div className="flex items-center gap-1 flex-wrap justify-end">
        {!assistantConnected && o.kind === 'share' && <button type="button" className="sem-btn sem-btn-sm" data-testid="size-connect" onClick={() => runHostCommand('semanticus.connectClaudeCode')}>Connect your assistant</button>}
        <GiveToAssistant sendKey={`stats|${o.ruleId}|${o.ref}`} disabled={!editingAllowed} disabledTitle={blockedTip} build={() => onGiveTask(o)} />
        {hasMenu && <RowMenu label="⋯" align="end" title="Do it yourself">
          {o.kind === 'unused' && <DeleteConfirm disabled={!editingAllowed || !reachable || !usageKnown || ambiguous} disabledTitle={!editingAllowed ? blockedTip : ambiguous ? ambiguousTip : unavailableTip} onConfirm={() => onDelete(o)} />}
          {o.kind === 'unused' && onReviewAsPlan && <MiniButton disabled={!planAllowed || ambiguous} title={!planAllowed ? planBlockedReason : ambiguous ? ambiguousTip : undefined} onClick={onReviewAsPlan}>Review these together</MiniButton>}
          {o.kind === 'dict' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onChangeType(o.ref)}>Change data type…</MiniButton>}
          {o.kind === 'summarize' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onDoNotSummarize(o)}>Do not summarize</MiniButton>}
          {o.kind === 'summarize' && <MiniButton disabled={!editingAllowed || !reachable} title={unavailableTip} onClick={() => onHide(o)}>Hide from authors</MiniButton>}
          {reachable ? <MiniButton onClick={() => revealInTree(o.ref)} title="Reveal in Model tree">Show in model</MiniButton> : !editingAllowed ? <MiniButton disabled title={blockedTip}>Show in model</MiniButton> : null}
        </RowMenu>}
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
      <button onClick={() => { setArmed(false); onConfirm(); }} className="sem-btn sem-btn-sm xe-dot xe-dot-bad">Confirm delete</button>
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
    <div className="mt-2 sem-inset">
      <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-2 text-xs font-semibold w-full" style={{ color: 'var(--sem-muted)' }}>
        <span className="inline-block w-3 text-(length:--sem-text-label)" style={{ transform: open ? 'none' : 'rotate(-90deg)' }}>▾</span>
        Changed since {label}<span className="sem-num">{count}</span>
      </button>
      {open && (
        <div className="mt-1 flex flex-col gap-1 pl-4">
          {changed.introduced.map((o) => <div key={'i' + o.ruleId + o.ref} className="text-xs"><span className="xe-dot xe-dot-warn">Introduced</span> · {o.name} · {plainRuleName(o.ruleName)}</div>)}
          {changed.newlyObserved.map((o) => <div key={'n' + o.ruleId + o.ref} className="text-xs"><span style={{ color: 'var(--sem-muted)' }}>Newly observed</span> · {o.name} · {plainRuleName(o.ruleName)}</div>)}
          {changed.newlyObserved.length > 0 && (
            <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>
              The previous scan covered only its top columns, so these cannot be confirmed as introduced since; a full-coverage scan on both sides would tell them apart.
            </div>
          )}
          {changed.resolved.map((k) => <div key={'r' + k} className="text-xs"><span className="xe-dot xe-dot-good">Resolved</span> · {refName(k)}</div>)}
          {changed.resolvedSuppressed && (
            <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>
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
        <div className="h-full rounded" style={{ width: Math.max(2, Math.round((100 * bytes) / max)) + '%', background: 'var(--sem-muted)' }} />
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
        <div className="flex items-center gap-2 pb-2">
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder={filterPlaceholder || 'Filter…'} spellCheck={false}
            className="sem-field sem-field-sm w-56" />
          <span className="text-xs tnum" style={{ color: 'var(--sem-muted)' }}>{view.length === rows.length ? `${rows.length}` : `${view.length} of ${rows.length}`}</span>
        </div>
      )}
      <div className="overflow-auto" style={maxHeight ? { maxHeight } : undefined}>
        <table className="w-full border-collapse">
          <thead className="sticky top-0 z-10" style={{ background: 'var(--sem-ground)' }}>
            <tr>
              {cols.map((c) => {
                const active = sort.key === c.key;
                return (
                  <th key={c.key} onClick={() => onHeader(c.key)}
                    className={'text-xs font-semibold px-2 py-1 cursor-pointer select-none ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ color: active ? 'var(--sem-fg)' : 'var(--sem-muted)', borderBottom: '1px solid var(--sem-rule)', whiteSpace: 'nowrap' }}>
                    {c.label}{active && <span className="text-(length:--sem-text-label) ml-1">{sort.dir === 1 ? '▲' : '▼'}</span>}
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
                className={(onRowClick || onRowFocus) ? 'cursor-pointer hover:bg-[var(--sem-hover)]' : ''}>
                {cols.map((c) => (
                  <td key={c.key} className={'text-xs px-2 py-2 ' + (c.align === 'right' ? 'text-right' : 'text-left')}
                    style={{ borderBottom: '1px solid var(--sem-rule)', color: 'var(--sem-fg)' }}>
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
