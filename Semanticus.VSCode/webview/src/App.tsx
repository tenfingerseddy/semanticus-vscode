import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, lazy, Suspense } from 'react';
import { rpc, onDidChange, onReconnect, onActivity, onNavigate, onOpenConnections, onWorkflowChange, signalReady, selectInProperties, focusSelectInProperties, focusModelTree, copyText, loadState, saveState, type ChangeNotification } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';
import { ShortcutsOverlay, tabForKey, isTypingTarget } from './shortcuts';
import { ActivityProvider, LiveActivity, ClaudeRanBanner, useClaudeReflection, KIND_TAB, type ActivityEvent } from './activity';
import { useFixState } from './hooks';
import { useConnection, ConnectBar, type SessionInfo } from './connection';
import { ContextBar, compareSeedFromSession, QueryStalenessChip } from './contextbar';
import { ConnectionsDrawer } from './connectionsdrawer';
import type { ModelRef } from './compare';
import { VpaqComponentBars, VPAQ_COMPONENT_COLORS, VPAQ_UNATTRIBUTED_COLOR, Sparkline, type VpaqColumn, type VpaqTable, type VpaqBarItem } from './echart';
import { DiagramView } from './diagram';
import type { ModelGraph } from './diagram';
import { AdvancedModelsView } from './advmodels';
import { GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { useTier, isEntitlementError, LicenseButton, ProBadge, UpsellNotice } from './pro';
import { DaxModelProvider } from './daxeditor';
import { DaxLabTabStateProvider, DaxLabView, useDaxLabBusy } from './daxlab';
import { LineageTabStateProvider, LineageView, useLineageReportsBusy } from './lineage';
import { SearchView } from './search';
import { DataPreviewView } from './datapreview';
import { BpaView } from './bpa';
import { OptimizeView } from './optimize';
import { SpecView } from './spec';
import { DocumentationView } from './documentation';
import { DeployView } from './deploy';
import { EvidenceView, TestsView } from './tests';
import { DataAgentView } from './dataagent';
import { HelpButton } from './help';
import { HistoryView, type EditEntry } from './history';
import { WorkflowsView, type WorkflowRunView } from './workflows';
import { emptyRuns, reduceRuns, liveRuns, runById, isRunNotFound, type RunMapState, type FoldOpts } from './workflowruns.mjs';
import { anchorOf, normalizeStorageMode, snapKey, scanMatchesAnchor, scanUsableDuringTransition, storageEvidenceLevel, relationshipAllowsStorageEdits, relationshipAllowsReverifiedDeletePlan, compareDecision, shouldStoreAsLast, resolvedClaimAllowed, introducedClaimAllowed, refIsAmbiguous, identifierLike, type StorageMode, type StorageEvidenceLevel } from './storagecalc.mjs';
import { busyAffordance } from './tabbusy.mjs';
import { KnowledgeView } from './knowledge';
import { PermissionsView } from './permissions';
import { InterviewSummaryChip } from './interview';
import { CustomRulesPanel } from './rulesauthor';

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

type StudioTab = 'readiness' | 'optimize' | 'bpa' | 'diagram' | 'advmodels' | 'mcode' | 'stats' | 'data' | 'daxlab' | 'lineage' | 'compare' | 'docs' | 'spec' | 'deploy' | 'tests' | 'evidence' | 'dataagent' | 'permissions' | 'history' | 'workflows' | 'knowledge' | 'search';

// The primary row names the five analyst intents; the secondary row keeps direct tools reachable without exposing
// product-internal machinery as navigation. IDs remain stable for host commands and persisted state. Compare and Data
// Agent stay valid compatibility routes, but are no longer primary destinations: Compare opens as Deploy's review and
// Data Agent lives under Deploy's Advanced tools. Model Spec remains primary because it uniquely owns model creation.
const TAB_GROUPS: { id: string; label: string; tabs: { id: StudioTab; label: string }[] }[] = [
  { id: 'understand', label: 'Understand', tabs: [
    { id: 'diagram', label: 'Diagram' }, { id: 'search', label: 'Search' }, { id: 'lineage', label: 'Lineage' },
    // DAX Lab is filed by the QUESTION THAT STARTS THE JOURNEY — "what does this query return?" — not by the
    // fact a Lab query can also change the model. That start-of-journey intent is Understand (like Search and
    // Lineage), even though each can lead to a change.
    { id: 'daxlab', label: 'DAX Lab' },
    { id: 'data', label: 'Data' }, { id: 'stats', label: 'Storage' },
  ] },
  { id: 'change', label: 'Change', tabs: [
    { id: 'spec', label: 'Model Spec' }, { id: 'advmodels', label: 'Advanced Modelling' }, { id: 'mcode', label: 'M Code' },
    { id: 'optimize', label: 'Change Plan' },
  ] },
  { id: 'improve', label: 'Improve', tabs: [
    { id: 'readiness', label: 'AI Readiness' }, { id: 'bpa', label: 'BPA' },
  ] },
  { id: 'prove', label: 'Prove', tabs: [
    { id: 'tests', label: 'Tests' }, { id: 'evidence', label: 'Evidence' },
  ] },
  { id: 'ship', label: 'Ship', tabs: [
    // Docs sits in Ship by Kane's ruling (2026-07-12): generated documentation is a deliverable
    // you ship, not reference reading.
    { id: 'deploy', label: 'Deploy' }, { id: 'permissions', label: 'Permissions' }, { id: 'docs', label: 'Docs' },
  ] },
];
const TAB_TO_GROUP: Record<string, string> = Object.fromEntries(TAB_GROUPS.flatMap((g) => g.tabs.map((t) => [t.id, g.id])));
TAB_TO_GROUP.compare = 'ship';
TAB_TO_GROUP.dataagent = 'ship';
// The single source of truth for valid tab ids: every grouped tab + the standalone far-right surfaces. Used to
// VALIDATE ids arriving from the host's navigateStudio — a stale bundle receiving an id it renamed (e.g. PR #29's
// 'powerquery'→'mcode') must IGNORE the switch, not fall through the render ternary into an arbitrary tab. Derived
// from TAB_GROUPS so it can never drift from the render switch.
const STANDALONE_TABS: StudioTab[] = ['history', 'workflows', 'knowledge'];
const LEGACY_TABS: StudioTab[] = ['compare', 'dataagent'];
const VALID_TABS = new Set<string>([...TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id)), ...STANDALONE_TABS, ...LEGACY_TABS]);
// Keyboard next/prev cycles every tab in reading order: the grouped lifecycle tabs, then the standalone
// far-right surfaces in their on-screen order (Primer · Workflows · Edit History). Wraps at both ends.
const CYCLE_TABS: StudioTab[] = [...TAB_GROUPS.flatMap((g) => g.tabs.map((t) => t.id)), 'knowledge', 'workflows', 'history'];
const FALLBACK_TAB: StudioTab = 'diagram';   // must be a real, first-of-lifecycle tab — never a mid-list default
// A pending Data-tab target (from a Model-tree "Preview data" right-click). The nonce makes a repeat navigation to
// the SAME table re-fire the preview.
type DataTarget = { table: string; nonce: number };

export function App() {
  const [session, setSession] = useState<SessionInfo | null>(null);
  const sessionRef = useRef<SessionInfo | null>(null);
  sessionRef.current = session;
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
  const [upsell, setUpsell] = useState<string | null>(null);   // a free click on the bulk button teaches, never errors
  const tier = useTier();
  const [tab, setTab] = useState<StudioTab>('diagram');   // open on Understand → Diagram
  const [dataTarget, setDataTarget] = useState<DataTarget | null>(null);          // table to preview (tree → Data)
  const [diagramAdd, setDiagramAdd] = useState<{ tables: string[]; nonce: number } | null>(null);  // tables to drop on the Diagram (tree → "Add to Studio Diagram")
  // Tree → Studio "jump to tab" targets: a right-click in the Model tree opens the owning tab AND focuses the object
  // (lineage → impact of a ref · M Code → that table's M · Advanced Modelling → the right builder). Nonce so a repeat
  // jump to the same target re-fires.
  const [lineageTarget, setLineageTarget] = useState<{ ref: string; nonce: number } | null>(null);
  const [workflowTarget, setWorkflowTarget] = useState<{ name: string; nonce: number } | null>(null);
  // Workflow RUNS live at the SHELL, above the conditionally-mounted Workflows tab (BLOCKER: a run started by the
  // AI Assistant while the human is on another tab must not be missed — an unmounted tab has no listener). One
  // always-alive subscription folds every workflow/didChange broadcast into a run-map keyed by runId, and a
  // getWorkflowRun seed catches a run that started before we subscribed. Ownership (you vs the AI Assistant) is
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
  const [pqTarget, setPqTarget] = useState<{ table: string; partitionId?: string; nonce: number } | null>(null);
  const [advArea, setAdvArea] = useState<{ area: string; nonce: number } | null>(null);
  const [searchTarget, setSearchTarget] = useState<{ query: string; nonce: number } | null>(null);   // findInModel → "Open in Search & Replace"
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
  const [permissionTarget, setPermissionTarget] = useState<{ id: string; nonce: number } | null>(null);
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
  const goTab = (t: string, approvalId?: string) => {
    // Validate against the known tab set (single source of truth). An unknown id — e.g. a host on a newer/older
    // protocol sending a renamed tab a stale bundle doesn't have — is IGNORED (never a mystery landing on some
    // arbitrary tab), and logged so version skew is visible in devtools instead of silent.
    if (!VALID_TABS.has(t)) { console.warn(`[Studio] ignoring navigation to unknown tab id '${t}' (host/webview version skew?)`); return; }
    if (t === 'compare') t = 'deploy';   // compatibility alias: comparison is Deploy's Push changes review
    if (t === 'permissions' && approvalId) setPermissionTarget({ id: approvalId, nonce: ++navNonce.current });
    setUnseen((s) => { if (!s.has(t)) return s; const n = new Set(s); n.delete(t); return n; });
    setTab(t as StudioTab);
  };
  // Group memory lives HERE (not in Shell) so the keyboard group jumps (Ctrl+Shift+1–5) and the group
  // buttons share it: entering a group returns you to the tab you last used in it, not always its first.
  const lastInGroup = useRef<Record<string, StudioTab>>({});
  useEffect(() => {
    const visibleTab = tab === 'dataagent' ? 'deploy' : tab;
    const g = TAB_TO_GROUP[visibleTab]; if (g) lastInGroup.current[g] = visibleTab;
  }, [tab]);
  const goGroup = (gid: string) => { const g = TAB_GROUPS.find((x) => x.id === gid); if (g) goTab(lastInGroup.current[g.id] ?? g.tabs[0].id); };
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
  const openWorkflow = (name: string) => { setWorkflowTarget({ name, nonce: ++navNonce.current }); goTab('workflows'); };

  // The host navigates us here (e.g. a Model-tree "Preview data" right-click): switch tabs and, for Data, hand the
  // target table to the Data view (nonce so re-selecting the same table re-previews). signalReady lets the host
  // flush a navigation it queued while opening Studio cold.
  useEffect(() => {
    signalReady();
    return onNavigate((m) => {
      // Keyboard/command pseudo-targets first — these aren't tabs, so they must never reach goTab.
      if (m.tab === 'shortcuts') { setShortcutsOpen(true); return; }
      if (m.tab === 'cycle:next') { cycleTab(1); return; }
      if (m.tab === 'cycle:prev') { cycleTab(-1); return; }
      if (m.tab?.startsWith('group:')) { goGroup(m.tab.slice('group:'.length)); return; }
      if (m.tab === 'readiness' && m.target === 'rescan') { void scan(); }   // the palette/keyboard "run a scan" — then land on the tab
      if (m.tab === 'data' && m.target) {
        const table = m.target.startsWith('table:') ? m.target.slice('table:'.length) : m.target;
        setDataTarget({ table, nonce: ++navNonce.current });
      }
      if (m.tab === 'diagram' && m.addTables?.length) setDiagramAdd({ tables: m.addTables, nonce: ++navNonce.current });
      if (m.tab === 'lineage' && m.target) setLineageTarget({ ref: m.target, nonce: ++navNonce.current });
      if (m.tab === 'mcode' && m.target) {
        // 'partition:<Table>/<Name>' pinpoints WHICH partition was clicked; derive the table for the picker and
        // keep the full ref so the M lane can land on that exact partition. 'table:<T>' selects the table only.
        const rest = m.target.startsWith('partition:') ? m.target.slice('partition:'.length) : null;
        const slash = rest ? rest.indexOf('/') : -1;
        if (rest && slash > 0) setPqTarget({ table: rest.slice(0, slash), partitionId: m.target, nonce: ++navNonce.current });
        else setPqTarget({ table: m.target.startsWith('table:') ? m.target.slice('table:'.length) : m.target, nonce: ++navNonce.current });
      }
      if (m.tab === 'advmodels' && m.target) setAdvArea({ area: m.target.startsWith('area:') ? m.target.slice('area:'.length) : m.target, nonce: ++navNonce.current });
      // The native sync status-bar item → a seeded Compare (same as the footer click): compute the seed from
      // live connection state and land on Review. jumpToCompare already seeds + goTab('compare'), so return early.
      if (m.tab === 'compare' && m.target === 'seed') { jumpToCompare(); return; }
      // '' is a real target here: "just focus the find box" (Ctrl+F) — only undefined means no hand-off.
      if (m.tab === 'search' && m.target != null) setSearchTarget({ query: m.target, nonce: ++navNonce.current });
      if (m.tab) goTab(m.tab);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The native tree picker's "Manage connections" opens the shared Connections drawer here, so both doors host the
  // same component.
  useEffect(() => onOpenConnections(() => openConnections()), [openConnections]);

  // The in-Studio half of the keyboard suite: gestures VS Code keybindings must NOT own (see shortcuts.tsx —
  // unmodified '?' would fire while typing, and Ctrl+Alt+letter package bindings collide with AltGr typing on
  // European layouts). Everything here checks the target is not editable, so typing is never hijacked.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing) return;
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
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
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
    try { const s = await rpc<SessionInfo>('sessionInfo'); setSession(s); return s; } catch { return null; }
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
          setCard(null); setError(null); setUpsell(null);
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
    return () => { off(); offReconnect(); window.clearTimeout(rescanTimer.current); };
  }, []);

  async function applySafeFixes() {
    setBusy(true); setUpsell(null);
    try { const r = await rpc<{ scorecard: Scorecard }>('applySafeFixes'); if (r?.scorecard) setCard(r.scorecard); await refreshSession(); }
    catch (e) {
      // A free click on the bulk button gets the plain invitation, not a raw exception in a red banner.
      if (isEntitlementError(e)) setUpsell(`Each fix below is free. Apply them one at a time, as many as you like. Pro applies all ${card?.safeFixCount ?? 0} in one undoable step and re-checks your score for you.`);
      else setError(String((e as Error).message ?? e));
    }
    finally { setBusy(false); }
  }

  return (
    <DaxModelProvider key={session?.sessionId ?? 'no-model'}>
    {/* Lineage's report-analysis holder stays mounted while the conditional tab body below changes. The surrounding
        session key still drops model-scoped state atomically when a different model opens. */}
    <LineageTabStateProvider>
    <ActivityProvider>
    <DaxLabTabStateProvider>
    <StorageTabStateProvider active={tab === 'stats'}>
    <ShortcutsOverlay open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    <ConnectionsDrawer open={connectionsOpen} onClose={closeConnections} />
    <Shell tab={tab === 'dataagent' ? 'deploy' : tab} onTab={goTab} onGroup={goGroup} onShortcuts={() => setShortcutsOpen(true)} unseen={unseen} historyCount={activity.length - undoneCount} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={pendingApprovals[0]?.id} onConnections={openConnections} onJumpToCompare={jumpToCompare}>
      <>
      {/* A test run can carry substantial cell evidence, so keep the view alive across Studio navigation instead
          of copying it into persisted webview state. The session key still discards it when a different model opens. */}
      {session?.sessionId && <div className={tab === 'tests' ? 'h-full' : 'hidden'}><TestsView key={session.sessionId} /></div>}
      {!session?.sessionId ? (
        <Empty />
      ) : tab === 'evidence' ? (
        <EvidenceView key={session.sessionId} />
      ) : tab === 'readiness' ? (
        <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
          {error && <Banner color="var(--sem-bad)">{error}</Banner>}
          {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
          {card?.gatedBy?.length ? <Banner color="var(--sem-bad)">{card.gatedBy.join(' · ')}</Banner> : null}
          {card?.caveat ? <Banner color="var(--sem-warn)">{card.caveat}</Banner> : null}
          {card?.ruleErrors?.length ? <Banner color="var(--sem-warn)">{card.ruleErrors.join(' · ')}</Banner> : null}
          {card ? <Hero card={card} trend={trend} busy={busy} tier={tier} onSafe={applySafeFixes} onRescan={scan} onReviewAsPlan={reviewAsPlan} /> : <Loading />}
          {card && <Categories card={card} />}
          {/* Improve keeps the latest behavioral signal; Prove owns the complete interview evidence and actions. */}
          {card && <InterviewSummaryChip onOpen={() => goTab('tests')} />}
          {card && <Findings card={card} onRescan={scan} />}
          {card && <CustomRulesPanel kind="readiness" onChanged={() => void scan()} />}
        </div>
      ) : tab === 'history' ? (
        <HistoryView items={activity} undoneCount={undoneCount} sessionId={session?.sessionId}
          onOpenRollback={(point) => { setDeployRestoreTarget({ id: point.id, endpoint: point.endpoint, database: point.database, nonce: ++navNonce.current }); goTab('deploy'); }} />
      ) : tab === 'workflows' ? (
        <WorkflowsView navTarget={workflowTarget} runs={runs} onRunUpdate={foldRun}
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
        <DiagramView addTables={diagramAdd} />
      ) : tab === 'advmodels' ? (
        <AdvancedModelsView navArea={advArea} />
      ) : tab === 'mcode' ? (
        <Suspense fallback={<Loading />}>
          <MCodeView navTarget={pqTarget} />
        </Suspense>
      ) : tab === 'stats' ? (
        <StatsView onReviewAsPlan={reviewAsPlan} />
      ) : tab === 'data' ? (
        <DataPreviewView target={dataTarget} />
      ) : tab === 'lineage' ? (
        <LineageView navTarget={lineageTarget} onOpenPlan={() => goTab('optimize')} onOpenTests={() => goTab('tests')} onOpenWorkflow={openWorkflow} />
      ) : tab === 'search' ? (
        <SearchView navQuery={searchTarget} onOpenPlan={() => goTab('optimize')} />
      ) : tab === 'docs' ? (
        <DocumentationView />
      ) : tab === 'deploy' ? (
        <DeployView key="deploy" seed={compareSeed} dataAgent={<DataAgentView />} restoreTarget={deployRestoreTarget} onRestoreConsumed={() => setDeployRestoreTarget(null)} />
      ) : tab === 'tests' ? (
        null
      ) : tab === 'permissions' ? (
        <PermissionsView focusApproval={permissionTarget} onApprovalChanged={() => { void refreshPendingApprovals(); }} />
      ) : tab === 'dataagent' ? (
        // Distinct key + no seed: the legacy route must land ON the Data Agent. A shared fiber would
        // ignore initialMode on re-render, and a lingering compare seed would flip the mode to push.
        <DeployView key="dataagent" dataAgent={<DataAgentView />} initialMode="advanced" initialAdvanced="dataagent" />
      ) : tab === 'daxlab' ? (
        <DaxLabView />
      ) : (
        // Explicit, safe fallback: every valid tab is enumerated above and goTab rejects unknown ids, so this is
        // unreachable in practice — but if it ever fires it lands on the first-of-lifecycle tab, never a random
        // mid-list one (the old `: <DaxLabView/>` catch-all was itself the "lands on DAX Lab" skew bug).
        FALLBACK_TAB === 'diagram' ? <DiagramView addTables={diagramAdd} /> : null
      )}
      </>
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

function Shell({ tab, onTab, onGroup, onShortcuts, unseen, historyCount, pendingApprovalCount, firstPendingApprovalId, onConnections, onJumpToCompare, children }: { tab: StudioTab; onTab: (t: string, approvalId?: string) => void; onGroup: (gid: string) => void; onShortcuts: () => void; unseen: Set<string>; historyCount: number; pendingApprovalCount: number; firstPendingApprovalId?: string; onConnections: () => void; onJumpToCompare: () => void; children: React.ReactNode }) {
  // Edit History and Workflows are standalone (no intent group) — a different axis from the five task intents
  // (a session-wide record / a cross-cutting playbook library). When either is active NO group is highlighted and the
  // secondary tab-row is hidden — they're full-bleed surfaces, not sub-tabs of anything.
  // Group memory (return to the tab you last used in a group) lives in App, shared with the keyboard jumps.
  const isHistory = tab === 'history';
  const isStandalone = isHistory || tab === 'workflows' || tab === 'knowledge';
  const activeGroup = isStandalone ? null : (TAB_TO_GROUP[tab] ?? TAB_GROUPS[0].id);
  const group = TAB_GROUPS.find((g) => g.id === activeGroup) ?? TAB_GROUPS[0];

  // Cross-tab busy: the converted surfaces (stages 1-2) keep their ops running while hidden. Surface that on the tab
  // bar so the originating tab (or its group, when that group is closed) shows a subtle spinner. Hooks are called
  // unconditionally — Shell always renders inside all three holders. The mapping mirrors the `unseen` bubbling.
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

  return (
    <div className="h-full flex flex-col">
      {/* primary row — analyst intents (left) + the standalone cross-cutting affordances (right) */}
      <header className="flex items-center gap-3 px-4 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
        <BrandMark />
        <div className="font-semibold tracking-tight">Semanticus Studio</div>
        <nav className="flex items-center gap-1 ml-3">
          {TAB_GROUPS.map((g, i) => (
            <GroupTab key={g.id} active={g.id === activeGroup} title={`${g.label} (Ctrl+Shift+${i + 1})`}
              unseen={g.id !== activeGroup && g.tabs.some((t) => unseen.has(t.id))}
              busy={busy.groups.has(g.id)}
              onClick={() => onGroup(g.id)}>{g.label}</GroupTab>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-3">
          <KnowledgeTab active={tab === 'knowledge'} onClick={() => onTab('knowledge')} />
          <WorkflowsTab active={tab === 'workflows'} onClick={() => onTab('workflows')} />
          <HistoryTab active={isHistory} count={historyCount} onClick={() => onTab('history')} />
          <LicenseButton />
          <span className="w-px h-5" style={{ background: 'var(--sem-border)' }} />
          <HelpButton tab={tab} onGo={(t) => onTab(t as StudioTab)} onShortcuts={onShortcuts} />
          <LiveActivity onOpen={onTab} pendingApprovalCount={pendingApprovalCount} firstPendingApprovalId={firstPendingApprovalId} />
        </div>
      </header>
      {/* secondary row — the active group's tabs (hidden for the standalone Edit History / Workflows surfaces) */}
      {!isStandalone && (
        <div className="flex items-center gap-1 px-4 py-1.5 border-b" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          {group.tabs.map((t) => (
            <Tab key={t.id} active={tab === t.id} unseen={unseen.has(t.id)} busy={busy.tabs.has(t.id)} onClick={() => onTab(t.id)}>
              {t.label}
              {t.id === 'permissions' && pendingApprovalCount > 0 && (
                <span className="ml-1 inline-flex min-w-4 h-4 px-1 items-center justify-center rounded-full text-[9px] leading-none font-bold tnum"
                  style={{ background: 'var(--sem-warn)', color: 'var(--sem-on-warn)' }} title={`${pendingApprovalCount} permission${pendingApprovalCount === 1 ? '' : 's'} waiting`}>
                  {pendingApprovalCount > 99 ? '99+' : pendingApprovalCount}
                </span>
              )}
            </Tab>
          ))}
        </div>
      )}
      <main className="flex-1 overflow-auto min-h-0">{children}</main>
      {/* Context bar — a FOOTER pinned to the bottom of the Studio panel (VS Code convention: ambient state lives at the
          bottom). flex:none so long tab content scrolls inside <main> above and never pushes it off screen. It replaces
          the old flat "name · N measures · N tables" header line: it separates what you're EDITING from what you're
          TESTING and PUBLISHING and flags when staged edits are absent from the queried model. Identity segments open
          Connections; Review changes alone opens the seeded diff. */}
      <ContextBar onConnections={onConnections} onReview={onJumpToCompare} />
    </div>
  );
}

// Standalone, always-visible Edit-History affordance — pinned far-right, separate from the intent groups
// because the co-authoring timeline is a session-wide record (a different axis from "what you do").
// The live count badge makes accumulating edits an ambient signal of co-authoring without opening the tab.
function HistoryTab({ active, count, onClick }: { active: boolean; count: number; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Edit History: every change this session, yours and the AI Assistant's, on one undoable timeline"
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M3 3v5h5" /><path d="M3.05 13A9 9 0 1 0 6 5.3L3 8" /><path d="M12 7v5l3 2" />
      </svg>
      <span>Edits</span>
      {count > 0 && (
        <span className="text-[10px] tnum rounded-full px-1.5 text-center" style={{ minWidth: 16, lineHeight: '15px',
          ...(active ? { background: 'rgba(6,33,15,0.20)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }) }}>
          {count}
        </span>
      )}
    </button>
  );
}

// Standalone Workflows affordance — pinned far-right beside Edit History, separate from the Understand→Change→Improve→Prove→Ship
// lifecycle groups because a workflow library is a cross-cutting concern (playbooks that orchestrate the lifecycle),
// not a stage within it. Same button language as Edit History so the two standalone surfaces read as a pair.
function WorkflowsTab({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Workflows: playbooks that chain steps with verified gates. Run one here, or let your AI Assistant run it."
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M9 6h11" /><path d="M9 12h11" /><path d="M9 18h11" /><circle cx="4" cy="6" r="1.6" /><circle cx="4" cy="12" r="1.6" /><circle cx="4" cy="18" r="1.6" />
      </svg>
      <span>Workflows</span>
    </button>
  );
}

// The Primer is the human-facing orientation document. Learning machinery remains behind it as a supplier.
function KnowledgeTab({ active, onClick }: { active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} title="Primer: the open model's shared orientation document"
      className="relative flex items-center gap-1.5 text-[12.5px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={active
        ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
        : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M12 2a7 7 0 0 0-4 12.7V17a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1v-2.3A7 7 0 0 0 12 2Z" /><path d="M9 21h6" />
      </svg>
      <span>Primer</span>
    </button>
  );
}

// Primary lifecycle-group button (filled-accent when active). The secondary Tab below uses the lighter soft-accent style,
// giving a clear two-tier hierarchy.
function GroupTab({ active, onClick, children, unseen, busy, title }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean; title?: string }) {
  return (
    <button onClick={onClick} title={title} className="relative inline-flex items-center text-[13px] px-3 py-1 rounded-md font-semibold transition-colors"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {busy && <TabBusyGlyph title="An operation is running in this group" />}
      {unseen && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="The AI Assistant ran something in this group" />}
    </button>
  );
}

function Tab({ active, onClick, children, unseen, busy }: { active: boolean; onClick: () => void; children: React.ReactNode; unseen?: boolean; busy?: boolean }) {
  return (
    <button onClick={onClick} className="relative inline-flex items-center text-[12px] px-2.5 py-1 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}>
      {children}
      {busy && !active && <TabBusyGlyph title="An operation is running on this tab" />}
      {unseen && !active && <span className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: 'var(--sem-accent)' }} title="The AI Assistant ran something here" />}
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

function Hero({ card, trend, busy, tier, onSafe, onRescan, onReviewAsPlan }: { card: Scorecard; trend: number[]; busy: boolean; tier: string; onSafe: () => void; onRescan: () => void; onReviewAsPlan?: () => void }) {
  const color = GRADE_COLOR[card.grade] ?? 'var(--sem-muted)';
  return (
    <Panel>
      <div className="flex items-center gap-5">
        <div className="flex flex-col items-center justify-center w-24 h-24 rounded-2xl" style={{ background: 'var(--sem-surface-2)', boxShadow: `inset 0 0 0 2px ${color}` }}>
          <div className="text-5xl font-bold leading-none" style={{ color }}>{card.grade}</div>
          <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>{card.overall.toFixed(0)}/100</div>
        </div>
        <div className="flex-1 min-w-0">
          <div className="text-[15px] font-semibold">AI Readiness {card.overall.toFixed(1)}</div>
          <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
            {card.findings.length} findings · {card.safeFixCount} safe fixes available
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
        <div className="flex flex-col gap-2">
          <Button primary disabled={busy || card.safeFixCount === 0} onClick={onSafe}
            title={tier === 'free'
              ? `Pro applies all ${card.safeFixCount} in one undoable step and re-checks your score. Each fix below stays free, one at a time.`
              : `Apply every safe fix in one undoable step and re-check the score.`}>
            {busy ? 'Applying…' : `Apply ${card.safeFixCount} safe fix${card.safeFixCount === 1 ? '' : 'es'}`}
            <ProBadge show={tier === 'free'} variant="onAccent" />
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
      <div className="flex flex-col gap-2.5 mt-2">
        {cats.map((c) => {
          const col = c.score >= 90 ? 'var(--sem-good)' : c.score >= 70 ? 'var(--sem-warn)' : 'var(--sem-bad)';
          return (
            <div key={c.category} className="flex items-center gap-3">
              <div className="w-36 text-[12px] shrink-0">{c.category}</div>
              <div className="flex-1 h-2 rounded-full overflow-hidden" style={{ background: 'var(--sem-surface-2)' }}>
                <div className="h-full rounded-full transition-all" style={{ width: `${c.score}%`, background: col }} />
              </div>
              <div className="w-12 text-right text-[12px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.score.toFixed(0)}</div>
              <div className="w-20 text-right text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{c.violations}/{c.applicable}</div>
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
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Waive rule" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl label="Waive rule" title="Accept every instance of this rule, model-wide (Pro)" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => run(rpc('unwaiveFinding', 'air', ruleId, '*'))} />;

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-2">
          <SectionTitle>Findings <span style={{ color: 'var(--sem-muted)' }}>({activeFindings.length}{card.waivedCount ? ` · ${card.waivedCount} waived` : ''})</span></SectionTitle>
          <div className="ml-auto flex gap-1">
            <Chip active={filter === 'all'} onClick={() => setFilter('all')}>All</Chip>
            <Chip active={filter === 'SafeFix'} onClick={() => setFilter('SafeFix')}>Safe {counts.SafeFix ?? 0}</Chip>
            <Chip active={filter === 'AiContent'} onClick={() => setFilter('AiContent')}>AI {counts.AiContent ?? 0}</Chip>
            <Chip active={filter === 'Proposal'} onClick={() => setFilter('Proposal')}>Review {counts.Proposal ?? 0}</Chip>
          </div>
        </div>
        {waiveErr && <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{waiveErr}</div>}
        <div className="mt-2"><GroupedFindings rows={rows} renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)} /></div>
      </Panel>
      <WaivedList rows={waivedRows} onUnwaive={unwaive} />
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
function Empty() {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">No model open</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Open a model from the Semanticus tree (Open Model…), then this dashboard scores it for AI readiness and lets you and the AI Assistant fix it together.</div>
      </div>
    </div>
  );
}

// ---- Storage (VertiPaq scan) -----------------------------------------------------------------
interface VpaqReport { queryIdentity?: string | null; modelSize: number; columnCount: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; storageMode?: StorageMode; caveat?: string; error?: string; }

// The staged "does this column exist" key. '/' is a legal character in BOTH a table AND a column name, so a
// plain table + '/' + column join collides (table "Sales/EU" col "Amount" == table "Sales" col "EU/Amount").
// This key is internal (built and looked up only here), so join on the unit-separator control char — unambiguous
// and never a real name character — instead of concatenating a real ref.
const KEY_SEP = '\u001f';
function colKey(table: string, column: string) { return table + KEY_SEP + column; }
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

function StatsView({ onReviewAsPlan }: { onReviewAsPlan?: () => void }) {
  const { conn, session, context } = useConnection();
  const {
    scanState, meta, stagedCols, colRows, unused, prevSnap, setPrevSnap, pinnedSnap, setPinnedSnap,
    compareMode, setCompareMode, barMode, setBarMode, explorerFilter, setExplorerFilter,
    busy, err, setErr, claudeEvent, setClaudeEvent, anchor, scanUsable, scanCurrent, scan, sessionPrev, persistedFor,
  } = useStorageTabState();
  const oppsPanelRef = useRef<HTMLDivElement | null>(null);
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
        <Banner color="var(--sem-warn)">Storage bytes are read-only observations from the linked query copy. That copy may be stale or structurally different from the open editing copy, so its bytes are not measurements of same-named editing objects. Removal plans omit reduction estimates and re-verify editing-model references when applied.</Banner>
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
