import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, onProgress, pickReportPaths, revealInTree, type OperationProgress } from './bridge';
import { useConnection } from './connection';
import { useFillHeight, KIND_GLYPH, KIND_COLOR, type LineageResult } from './lineagetypes';
import { LineageGraphView } from './lineagegraph';
import { LineageTreeView } from './lineagetree';
import { ToolRow } from './toolrow';
import * as M from './lineage-model';
import {
  CheckedStrip, ChooseReportsDrawer, CleanupList, ImpactCard, clockTime,
  type CloudReport, type FabricWorkspace, type ImpactAssessmentResult, type ImpactResult,
  type UnusedItem, type UnusedResult,
} from './lineage-impact';

// ---- wire types (camelCase, from Semanticus.Engine/Lineage/LineageProtocol.cs) ---------------
export interface ReportVisualUsage { page: string; visual?: string; visualType?: string; usedRefs: string[] }
export interface ReportUsage { path: string; name: string; read: boolean; fieldCount: number; unresolved: number; usedRefs: string[]; extensionMeasures: string[]; visuals?: ReportVisualUsage[]; error?: string }
export interface ReportAnalysisResult { reports: ReportUsage[]; reportsRead: number; reportsUnreadable: number; modelFieldsUsed: string[]; unused: UnusedResult; caveat?: string }
/** list_report_scope / set_report_scope / check_reports, from Semanticus.Engine/Lineage/ReportScope.cs. */
export interface ReportScopeResult extends M.ReportScope { usage?: ReportAnalysisResult }

type LineageMode = 'graph' | 'tree' | 'impact';
/** Safe to remove and Published reports were separate sub-tabs until Kane's option A. An old route or deep
 * link to either still has to land on something real: the cleanup view, and the Choose reports drawer. */
export const LEGACY_LINEAGE_MODES = ['unused', 'reports'] as const;
type DrawerBusy = 'workspaces' | 'reports' | 'check' | 'save' | 'browse' | null;

const EMPTY_SCOPE: M.ReportScope = {
  reports: [], listed: 0, checked: 0, couldNotBeFullyChecked: 0, notChecked: 0, needsChecking: 0, gaps: [],
};

interface LineageTabState {
  mode: LineageMode;
  setMode: React.Dispatch<React.SetStateAction<LineageMode>>;
  /** The cleanup view is a tick box on Impact, not a tab: same page, picker swapped for the candidate list. */
  cleanup: boolean;
  setCleanup: React.Dispatch<React.SetStateAction<boolean>>;
  drawerOpen: boolean;
  setDrawerOpen: React.Dispatch<React.SetStateAction<boolean>>;
  /** The engine owns which reports this model is checked against; the page only shows it. */
  scope: M.ReportScope;
  setScope: React.Dispatch<React.SetStateAction<M.ReportScope>>;
  usage: ReportAnalysisResult | null;
  setUsage: React.Dispatch<React.SetStateAction<ReportAnalysisResult | null>>;
  refreshScope: () => Promise<void>;
  busy: DrawerBusy;
  setBusy: React.Dispatch<React.SetStateAction<DrawerBusy>>;
  cloudProgress: OperationProgress | null;
  error: string | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  // Published-report discovery for the drawer.
  workspaces: FabricWorkspace[] | null; setWorkspaces: React.Dispatch<React.SetStateAction<FabricWorkspace[] | null>>;
  ws: string; setWs: React.Dispatch<React.SetStateAction<string>>;
  signInMode: string; setSignInMode: React.Dispatch<React.SetStateAction<string>>;
  tenant: string; setTenant: React.Dispatch<React.SetStateAction<string>>;
  reports: CloudReport[] | null; setReports: React.Dispatch<React.SetStateAction<CloudReport[] | null>>;
  sel: Set<string>; setSel: React.Dispatch<React.SetStateAction<Set<string>>>;
  consent: boolean; setConsent: React.Dispatch<React.SetStateAction<boolean>>;
  folderPath: string; setFolderPath: React.Dispatch<React.SetStateAction<string>>;
  gates: { cloudGen: React.MutableRefObject<number>; activeCloudRunId: React.MutableRefObject<string | null>; tenantOwner: React.MutableRefObject<'auto' | 'user' | null> };
}

const LineageTabStateContext = createContext<LineageTabState | null>(null);

function useLineageTabState(): LineageTabState {
  const state = useContext(LineageTabStateContext);
  if (!state) throw new Error('LineageView must be rendered inside LineageTabStateProvider');
  return state;
}

/** Null-tolerant selector for the Studio tab bar: true while any report discovery or check is in flight. */
export function useLineageReportsBusy(): boolean {
  return useContext(LineageTabStateContext)?.busy != null;
}

/**
 * This holder is mounted by App above the conditional Studio tab body, so report discovery and an in-flight
 * check outlive LineageView's mount without becoming persisted webview data. App's session-keyed boundary
 * still destroys the holder on a model swap.
 *
 * What it no longer holds is the ANSWER. The report reading lives in the engine now (list_report_scope /
 * check_reports), because keeping it here is exactly what let the page say reports had been checked while
 * the engine, asked fresh with no reports, said they had not.
 */
export function LineageTabStateProvider({ children }: { children: React.ReactNode }) {
  const { session } = useConnection();
  const [mode, setMode] = useState<LineageMode>('tree');
  const [cleanup, setCleanup] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [scope, setScope] = useState<M.ReportScope>(EMPTY_SCOPE);
  const [usage, setUsage] = useState<ReportAnalysisResult | null>(null);
  const [busy, setBusy] = useState<DrawerBusy>(null);
  const [cloudProgress, setCloudProgress] = useState<OperationProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [workspaces, setWorkspaces] = useState<FabricWorkspace[] | null>(null);
  const [ws, setWs] = useState('');
  const [signInMode, setSignInMode] = useState('azcli');
  const [tenant, setTenant] = useState('');
  const [reports, setReports] = useState<CloudReport[] | null>(null);
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [consent, setConsent] = useState(false);
  const [folderPath, setFolderPath] = useState('');

  const cloudGen = useRef(0);
  const activeCloudRunId = useRef<string | null>(null);
  const tenantOwner = useRef<'auto' | 'user' | null>(null);
  const prevIdentity = useRef<string | undefined>(undefined);
  const scopeTimer = useRef<number | undefined>(undefined);

  const refreshScope = async () => {
    try { setScope(await rpc<ReportScopeResult>('listReportScope')); }
    catch { /* a model with no scope yet is a valid answer, not an error to shout about */ }
  };

  // The engine decides when a reading has gone stale (it stamps the model revision it read at), so a model
  // edit only means "ask it again" here. No freshness bookkeeping of our own: two owners of one truth is
  // the bug this whole lane exists to fix.
  useEffect(() => {
    void refreshScope();
    const off = onDidChange(() => {
      window.clearTimeout(scopeTimer.current);
      scopeTimer.current = window.setTimeout(() => { void refreshScope(); }, 350);
    });
    return () => { off(); window.clearTimeout(scopeTimer.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => onProgress((p) => {
    if (p.opKey === 'analyze_cloud_reports' && p.runId === activeCloudRunId.current)
      setCloudProgress((cur) => (cur && cur.runId === p.runId && p.done < cur.done ? cur : p));
  }), []);

  // A same-session endpoint or database rebind is a different model: drop every model-scoped discovery.
  useEffect(() => {
    if (!session) return;
    const ident = sessionIdentityKey(session);
    const modelSwitched = prevIdentity.current !== undefined && prevIdentity.current !== ident;
    prevIdentity.current = ident;
    if (modelSwitched) {
      cloudGen.current++;
      activeCloudRunId.current = null;
      setBusy(null); setCloudProgress(null); setWorkspaces(null); setWs('');
      setReports(null); setSel(new Set()); setConsent(false); setError(null);
      setScope(EMPTY_SCOPE); setUsage(null); setCleanup(false); setDrawerOpen(false);
      void refreshScope();
    }
    const next = nextTenantValue(tenant, tenantOwner.current, session.currentTenant || '', modelSwitched);
    tenantOwner.current = next.owner;
    if (next.value !== tenant) setTenant(next.value);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session, session?.currentTenant]);

  return (
    <LineageTabStateContext.Provider value={{
      mode, setMode, cleanup, setCleanup, drawerOpen, setDrawerOpen, scope, setScope, usage, setUsage,
      refreshScope, busy, setBusy, cloudProgress, error, setError,
      workspaces, setWorkspaces, ws, setWs, signInMode, setSignInMode, tenant, setTenant,
      reports, setReports, sel, setSel, consent, setConsent, folderPath, setFolderPath,
      gates: { cloudGen, activeCloudRunId, tenantOwner },
    }}>
      {children}
    </LineageTabStateContext.Provider>
  );
}

// Merge published-report usage into the MODEL lineage graph so the chain extends downstream into where each
// field is actually shown: field -> visual -> page -> report. Edges are 'reportUsage' (from=dependant,
// to=dependency). Only links to fields that exist in the model graph; a visual with no model field is
// skipped. Returns the base graph unchanged when nothing connects.
function augmentGraphWithReports(base: LineageResult | null, analysis: ReportAnalysisResult | null): LineageResult | null {
  if (!base || !analysis?.reports?.length) return base;
  const modelRefs = new Set(base.nodes.map((n) => n.ref));
  const nodes = base.nodes.slice();
  const edges = base.edges.slice();
  const seen = new Set(modelRefs);
  const addNode = (ref: string, name: string, kind: string, table?: string) => { if (!seen.has(ref)) { seen.add(ref); nodes.push({ ref, name, kind, table }); } };
  let linked = 0;
  // Key the ephemeral graph refs on the report's ARRAY INDEX, not its name: two same-named reports would
  // otherwise share a ref namespace and dedup would attach one report's fields to the other's visual.
  analysis.reports.forEach((r, ri) => {
    if (!r.visuals?.length) return;
    const rname = r.name || r.path || `Report ${ri + 1}`;
    const reportRef = `report:${ri}:${rname}`;
    let reportAdded = false;
    const pageSeen = new Set<string>();
    r.visuals.forEach((v, vi) => {
      const fields = (v.usedRefs ?? []).filter((f) => modelRefs.has(f));
      if (fields.length === 0) return;
      if (!reportAdded) { addNode(reportRef, rname, 'report'); reportAdded = true; }
      const pageRef = `page:${ri}/${v.page}`;
      if (!pageSeen.has(pageRef)) { pageSeen.add(pageRef); addNode(pageRef, v.page, 'page', rname); edges.push({ from: reportRef, to: pageRef, kind: 'reportUsage' }); }
      const visualRef = `visual:${ri}/${v.page}#${vi}`;
      addNode(visualRef, v.visual || v.visualType || 'visual', 'visual', v.page);
      edges.push({ from: pageRef, to: visualRef, kind: 'reportUsage' });
      for (const f of fields) { edges.push({ from: visualRef, to: f, kind: 'reportUsage' }); linked++; }
    });
  });
  if (linked === 0) return base;
  return { nodes, edges, caveat: undefined };
}

export function LineageView({ navTarget, onOpenPlan, onOpenTests, onAddCheck, onOpenWorkflow, onRename }: {
  navTarget?: { ref: string; nonce: number } | null;
  onOpenPlan?: () => void;
  onOpenTests?: () => void;
  onAddCheck?: (ref: string) => void;
  onOpenWorkflow?: (name: string) => void;
  onRename?: (ref: string) => void;
} = {}) {
  const { mode, setMode, cleanup, setCleanup, drawerOpen, setDrawerOpen, scope, usage } = useLineageTabState();
  const [graph, setGraph] = useState<LineageResult | null>(null);
  const [root, setRoot] = useState<string | null>(null);
  const [impact, setImpact] = useState<ImpactResult | null>(null);
  const [unused, setUnused] = useState<UnusedResult | null>(null);
  const [graphErr, setGraphErr] = useState<string | null>(null);
  const [unusedErr, setUnusedErr] = useState<string | null>(null);
  // P1. The candidate list is measured AGAINST the scope, so a scope change makes it out of date, not merely
  // differently labelled. The first build reloaded it on mount and on a model edit only, so after a reading it
  // kept its old candidates under a new "checked against 1 of 3" line that nobody had recomputed.
  const [unusedStale, setUnusedStale] = useState(false);
  const [impactErr, setImpactErr] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const rootRef = useRef<string | null>(null);

  async function loadGraph() {
    try { setGraph(await rpc<LineageResult>('getLineage')); setGraphErr(null); }
    catch (e) { setGraphErr(String((e as Error).message ?? e)); }
  }
  async function loadUnused() {
    setUnusedStale(true);
    try { setUnused(await rpc<UnusedResult>('unusedObjects')); setUnusedErr(null); }
    catch (e) { setUnusedErr(String((e as Error).message ?? e)); }
    finally { setUnusedStale(false); }
  }
  async function loadImpact(ref: string) {
    setRoot(ref); rootRef.current = ref;
    try { setImpact(await rpc<ImpactResult>('impactOf', ref)); setImpactErr(null); }
    catch (e) { setImpactErr(String((e as Error).message ?? e)); setImpact(null); }
  }

  useEffect(() => {
    void loadGraph(); void loadUnused();
    const off = onDidChange(() => {
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => { void loadGraph(); void loadUnused(); if (rootRef.current) void loadImpact(rootRef.current); }, 350);
    });
    return () => { off(); window.clearTimeout(timer.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A Model-tree "Show Lineage & Impact" jump focuses that object. A LEGACY route (Safe to remove, Published
  // reports) lands on Impact too, with the cleanup view or the drawer already open, so an old deep link keeps
  // working instead of dropping someone on a tab that no longer exists.
  useEffect(() => {
    if (!navTarget?.ref) return;
    const legacy = (LEGACY_LINEAGE_MODES as readonly string[]).includes(navTarget.ref);
    if (legacy) {
      const landing = M.landingFor(navTarget.ref);
      setMode(landing.mode); setCleanup(!!landing.cleanup); setDrawerOpen(!!landing.drawer);
      return;
    }
    setMode('impact'); setCleanup(false); void loadImpact(navTarget.ref);
  }, [navTarget?.nonce]);   // eslint-disable-line react-hooks/exhaustive-deps

  const fullGraph = useMemo(() => augmentGraphWithReports(graph, usage), [graph, usage]);
  const reportsMerged = fullGraph !== graph;
  const err = (mode === 'graph' || mode === 'tree') ? graphErr : (graphErr ?? (cleanup ? unusedErr : impactErr));

  const modeSwitch = (
    <div className="sem-seg" role="group" aria-label="Lineage view">
      <button className="sem-seg-item" aria-pressed={mode === 'graph'} onClick={() => setMode('graph')} title="See the whole model as a web you can walk">Graph</button>
      <button className="sem-seg-item" aria-pressed={mode === 'tree'} onClick={() => setMode('tree')} title="Follow one field, one step at a time">Tree</button>
      <button className="sem-seg-item" aria-pressed={mode === 'impact'} onClick={() => setMode('impact')} title="See what uses a field, and what is safe to clean up">Impact</button>
    </div>
  );
  const counts = fullGraph ? `${fullGraph.nodes.length} things · ${fullGraph.edges.length} links${reportsMerged ? ' · reports included' : ''}` : null;
  const caveat = (mode === 'graph' || mode === 'tree') ? fullGraph?.caveat : null;

  if (mode === 'graph') return <LineageGraphView graph={fullGraph} modeSwitch={modeSwitch} counts={counts} caveat={caveat ?? null} error={err ?? null} onOpenImpact={(r) => { setMode('impact'); setCleanup(false); void loadImpact(r); }} />;
  if (mode === 'tree') return <LineageTreeView graph={fullGraph} unusedItems={unused?.items} modeSwitch={modeSwitch} counts={counts} caveat={caveat ?? null} error={err ?? null} onOpenImpact={(r) => { setMode('impact'); setCleanup(false); void loadImpact(r); }} />;

  return (
    <div className="flex flex-col">
      <ToolRow>
        {modeSwitch}
        <CheckedStrip scope={scope} onChoose={() => setDrawerOpen(true)} />
      </ToolRow>
      <div className="flex flex-col gap-4 p-4">
        {err && <Banner color="var(--sem-bad)">{err}</Banner>}
        <ImpactPane graph={graph} root={root} impact={impact} unused={unused} unusedStale={unusedStale} onPick={(r) => void loadImpact(r)}
          onOpenPlan={onOpenPlan} onOpenTests={onOpenTests} onAddCheck={onAddCheck} onOpenWorkflow={onOpenWorkflow} onRename={onRename} />
      </div>
      {drawerOpen && <DrawerHost onPick={(r) => void loadImpact(r)} returningTo={impact?.rootName ?? null}
        onScopeChanged={() => { void loadUnused(); if (rootRef.current) void loadImpact(rootRef.current); }} />}
    </div>
  );
}

export const LINEAGE_NOTES: string[] = [
  'Trace where a field comes from and what relies on it, then see what is safe to clean up.',
  'Graph shows the whole model as a web you can walk. Tree follows one field a step at a time. Impact says what uses a field, in the model and in the reports you chose, and lists what nothing uses.',
];

// ---- Impact: the one page -----------------------------------------------------------------------
const RE_ROOTABLE = new Set(['measure', 'column', 'calcColumn', 'calcTable', 'calcGroup', 'calcitem', 'table']);

function ImpactPane({ graph, root, impact, unused, unusedStale, onPick, onOpenPlan, onOpenTests, onAddCheck, onOpenWorkflow, onRename }: {
  graph: LineageResult | null; root: string | null; impact: ImpactResult | null; unused: UnusedResult | null; unusedStale?: boolean;
  onPick: (ref: string) => void; onOpenPlan?: () => void; onOpenTests?: () => void;
  onAddCheck?: (ref: string) => void; onOpenWorkflow?: (name: string) => void; onRename?: (ref: string) => void;
}) {
  const { cleanup, setCleanup, drawerOpen, setDrawerOpen, scope } = useLineageTabState();
  const [q, setQ] = useState('');
  const [cleanupFilter, setCleanupFilter] = useState('');
  const [assessment, setAssessment] = useState<ImpactAssessmentResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [proposed, setProposed] = useState(false);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [expandedRow, setExpandedRow] = useState<string | null>(null);
  const [fillRef, paneH] = useFillHeight(440);

  const pickable = useMemo(() => (graph?.nodes ?? []).filter((n) => n.kind === 'measure' || n.kind === 'column' || n.kind === 'calcColumn'), [graph]);
  const view = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (needle ? pickable.filter((n) => (n.table + ' ' + n.name).toLowerCase().includes(needle)) : pickable).slice(0, 400);
  }, [pickable, q]);

  // An assessment is a point-in-time answer. A different pick, or a fresh reading of the reports, replaces it.
  // The answer's own lifecycle belongs to ONE effect (the P4 one below), which clears it, asks, and only then
  // shows it. This one resets the things AROUND the answer. It used to clear the assessment too, and because
  // it also depends on `impact` - which resolves on its own schedule - it cleared a good answer a moment after
  // it arrived, leaving the card in a "ready" state with nothing in it. Two owners of one piece of state.
  useEffect(() => { setActionError(null); setProposed(false); setExpandedRow(null); }, [root, impact, scope]);

  // One Escape handler for the whole page, so only the topmost surface acts on it: the drawer first, then an
  // expanded list. Astra could not close the old drawer at all, because a control inside it swallowed the key.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (drawerOpen) { e.stopPropagation(); setDrawerOpen(false); return; }
      if (expandedRow) { e.stopPropagation(); setExpandedRow(null); }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [drawerOpen, expandedRow, setDrawerOpen]);

  // The card's own answer comes from the engine's assessment when it has one (it carries the report hits and
  // the checked scope), and from impact_of before that, so a pick paints at once instead of waiting.
  // P4. An absent assessment used to be read as zero report hits, so the card said "No use was found" before
  // the request had finished, and kept saying it after the request failed. The answer is shown only when a
  // request has come back FOR THIS field and THIS scope; until then the row says it is still checking.
  const [reportState, setReportState] = useState<'idle' | 'checking' | 'ready' | 'failed'>('idle');
  useEffect(() => {
    if (!root) { setReportState('idle'); return; }
    let live = true;
    setAssessment(null);
    setReportState('checking');
    (async () => {
      try {
        const r = await rpc<ImpactAssessmentResult>('impactAssessment', { objectRef: root, intent: 'change', scope: 'modelAndReports' });
        // The answer has to be about the field that is still picked, or it is somebody else's answer.
        if (!live) return;
        if (r?.objectRef && rootRef2.current && r.objectRef !== rootRef2.current) return;
        setAssessment(r); setReportState('ready');
      } catch (e) { if (live) { setReportState('failed'); setActionError(String((e as Error).message ?? e)); } }
    })();
    return () => { live = false; };
  }, [root, scope]);
  const rootRef2 = useRef<string | null>(null);
  rootRef2.current = root;

  async function proposeRemoval(refs: string[], label: string) {
    if (refs.length === 0) return;
    setBusy('propose'); setActionError(null);
    try {
      for (const r of refs) await rpc('addPlanItem', r, 'delete_if_unused', null, label, null, null, 'human');
      setProposed(true);
    } catch (e) { setActionError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  const candidates = unused?.items ?? [];
  // Both sentences are written HERE, from the same rules the engine uses for the assistant. The page needs
  // them the instant a field is picked, and it is the only side that knows the reader's clock, so the engine's
  // own wording stays for the MCP door rather than being rendered a beat late in place of this one.
  const summary = impact ? M.verdictSentence(impact) : '';
  // The model half is known the moment the field is picked. The report half is not, and must not borrow the
  // model half's certainty: it says what it is doing, or what went wrong, until a real answer arrives.
  const reportSummary = !impact ? undefined
    : reportState === 'checking' ? 'Checking report use...'
    : reportState === 'failed' ? 'Report use could not be checked. The reports were not read, so nothing here rules out report use.'
    : M.noUseSentence(impact.rootName, scope, (assessment?.reportImpact ?? []).map((h) => h.name), { time: clockTime });

  return (
    // Cleanup is a job with an overview, not a sidebar: with nothing picked it takes the whole width, so the
    // reasons are readable instead of wrapping four words to a line beside an empty card. Picking a row brings
    // the card back, with the list still wide enough to read.
    <div ref={fillRef}
      className={`grid grid-cols-1 gap-4 lg:h-[var(--sem-pane-h)] ${cleanup ? (impact ? 'lg:grid-cols-[460px_1fr]' : '') : 'lg:grid-cols-[320px_1fr]'}`}
      style={{ '--sem-pane-h': `${paneH}px` } as React.CSSProperties}>
      {cleanup ? (
        <CleanupList items={candidates} scope={scope} selected={ticked} active={root}
          filter={cleanupFilter} onFilter={setCleanupFilter} onCleanupOff={() => setCleanup(false)}
          onToggle={(r) => setTicked((s) => { const n = new Set(s); n.has(r) ? n.delete(r) : n.add(r); return n; })}
          onOpen={onPick} busy={busy != null} updating={unusedStale} caveat={unused?.caveat ?? null}
          onPropose={() => void proposeRemoval([...ticked], `Remove ${ticked.size} cleanup candidate${ticked.size === 1 ? '' : 's'}`)} />
      ) : (
        <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full">
          <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Measures and columns</div>
          <input value={q} onChange={(e) => setQ(e.target.value)} data-testid="impact-picker-filter"
            aria-label="Find a measure or column" placeholder="Find a measure or column" spellCheck={false}
            className="mt-2 text-[12px] px-2 py-1 rounded-md outline-none w-full shrink-0"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <label className="flex items-center gap-2 mt-2 text-[12px] shrink-0" data-testid="impact-cleanup-toggle-row">
            <input type="checkbox" checked={cleanup} data-testid="impact-cleanup-toggle" onChange={(e) => setCleanup(e.target.checked)} />
            <span>Show cleanup candidates</span>
            <span className="ml-auto text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>{candidates.length}</span>
          </label>
          <div className="mt-2 overflow-auto flex-1 min-h-0 max-h-[460px] lg:max-h-none">
            {view.map((n) => (
              <div key={n.ref} onClick={() => onPick(n.ref)} data-testid="lineage-impact-object"
                className="flex items-center gap-2 px-2 py-1 rounded-md cursor-pointer text-[12px] hover:bg-[var(--sem-surface-2)]"
                style={n.ref === root ? { background: 'var(--sem-accent-soft)' } : undefined}>
                <span className="shrink-0 text-[12px] w-4 text-center" style={{ color: KIND_COLOR[n.kind] ?? 'var(--sem-muted)' }}>{KIND_GLYPH[n.kind] ?? '•'}</span>
                <span className="truncate" style={{ opacity: n.isHidden ? 0.55 : 1 }}>{n.name}</span>
                <span className="ml-auto text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 110 }}>{n.table}</span>
              </div>
            ))}
            {view.length === 0 && <div className="text-[12px] py-6 text-center" style={{ color: 'var(--sem-muted)' }}>Nothing matches.</div>}
          </div>
        </Panel>
      )}

      {cleanup && !impact ? null : !impact ? (
        <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full">
          <div className="text-[12px] py-6 text-center" data-testid="impact-empty" style={{ color: 'var(--sem-muted)' }}>
            Pick anything on the left. The card says what uses it, in the model and in the reports that were checked,
            and which saved checks to run after changing it.
          </div>
        </Panel>
      ) : (
        <ImpactCard impact={impact} scope={scope} reportHits={assessment?.reportImpact ?? []}
          savedChecks={assessment?.replayChecks ?? []} summary={summary} reportSummary={reportSummary}
          onPick={(r) => (RE_ROOTABLE.has(r.split(':')[0]) ? onPick(r) : onPick(r))}
          onChooseReports={() => setDrawerOpen(true)}
          // The real rename journey, with the field carried through: the object is revealed and selected in
          // the model list, where its name is edited. The old button ran an assessment and then offered a
          // workflow link, which renamed nothing and left the person to find the field again themselves.
          onRename={() => { if (root) { revealInTree(root); onRename?.(root); } }}
          onProposeRemoval={() => void proposeRemoval([impact.root], `Remove ${impact.rootName}`)}
          onAddCheck={(r) => onAddCheck?.(r)} onOpenCheck={onOpenTests} onOpenPlan={onOpenPlan}
          busy={busy} proposed={proposed} error={actionError}
          expandedRow={expandedRow} onExpandRow={setExpandedRow} />
      )}
    </div>
  );
}

// ---- the Choose reports drawer, wired to the engine ----------------------------------------------
function DrawerHost({ onPick, returningTo, onScopeChanged }: {
  onPick: (ref: string) => void; returningTo: string | null; onScopeChanged?: () => void;
}) {
  const {
    scope, setScope, setUsage, setDrawerOpen, busy, setBusy, error, setError, refreshScope,
    workspaces, setWorkspaces, ws, setWs, signInMode, setSignInMode, tenant, setTenant,
    reports, setReports, sel, setSel, consent, setConsent, folderPath, setFolderPath, gates,
  } = useLineageTabState();
  const { cloudGen, tenantOwner } = gates;
  const { session } = useConnection();
  const wsNameHint = useMemo(() => workspaceNameFromEndpoint(session?.liveEndpoint), [session?.liveEndpoint]);

  // A tick in the drawer selects a report to CHECK. Everything already chosen for this model is pre-ticked,
  // so pressing Check reads the whole selection rather than silently only the newest addition.
  useEffect(() => { setSel(new Set(scope.reports.map((r) => r.id))); }, [scope.reports.length]);   // eslint-disable-line react-hooks/exhaustive-deps

  async function loadWorkspaces() {
    const gen = ++cloudGen.current;
    setBusy('workspaces'); setError(null);
    try {
      const list = await rpc<FabricWorkspace[]>('listWorkspaces', signInMode, tenant.trim() || null);
      if (gen !== cloudGen.current) return;
      const sorted = [...(list ?? [])].sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));
      setWorkspaces(sorted);
      if (wsNameHint) {
        const match = sorted.find((w) => (w.displayName || '').toLowerCase() === wsNameHint.toLowerCase());
        if (match) setWs((cur) => cur || match.id);
      }
    } catch (e) { if (gen === cloudGen.current) { setError(String((e as Error).message ?? e)); setWorkspaces([]); } }
    finally { if (gen === cloudGen.current) setBusy(null); }
  }

  async function loadReports() {
    if (!ws.trim()) { setError('Choose a workspace first.'); return; }
    const gen = ++cloudGen.current;
    setBusy('reports'); setError(null); setReports(null);
    try {
      const list = await rpc<CloudReport[]>('listReports', ws.trim(), signInMode, tenant.trim() || null);
      if (gen !== cloudGen.current) return;
      setReports(list);
    } catch (e) { if (gen === cloudGen.current) setError(String((e as Error).message ?? e)); }
    finally { if (gen === cloudGen.current) setBusy(null); }
  }

  // Choosing is a save; checking is a read. They are two calls on purpose, because the whole repair rests on
  // a choice never being mistaken for a reading.
  async function save(next: M.ScopeReport[]) {
    // P3. Every report carries its OWN workspace and report id, from the row itself. The first build rebuilt
    // them from whichever workspace the picker happened to be showing, so adding a report from workspace B
    // silently moved an existing report from workspace A into B, keeping its id and changing what it pointed at.
    const choices = next.map((r) => r.kind === 'published'
      ? { id: r.id, kind: 'published', name: r.name, workspaceId: r.workspaceId, workspaceName: r.source, reportId: r.reportId }
      : { id: r.id, kind: 'local', name: r.name, path: r.source });
    setBusy('save'); setError(null);
    try {
      setScope(await rpc<ReportScopeResult>('setReportScope', choices, 'human'));
      // A changed selection drops the reading it was made against, so nothing on the page may keep showing an
      // answer measured with the old one. P1: the cleanup list above all.
      setUsage(null);
      onScopeChanged?.();
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
    return next;
  }

  /**
   * P2. Ticking a report in the discovered list adds it to the model's selection. The first build called setSel
   * and then read the PREVIOUS sel inside the save, so the very first tick sent an empty list: the person saw a
   * tick and the engine was told nothing was chosen. One next selection is computed here and passed in.
   */
  async function addPublished(reportId: string) {
    const wsId = ws.trim();
    const wsName = workspaces?.find((w) => w.id === wsId)?.displayName;
    const found = (reports ?? []).find((r) => r.id === reportId);
    if (!found) return;
    const key = `published:${wsId}/${found.id}`;
    if (scope.reports.some((r) => r.id === key)) return;
    const next: M.ScopeReport[] = [...scope.reports, {
      id: key, kind: 'published', name: found.name, source: wsName ?? 'a workspace',
      workspaceId: wsId, reportId: found.id, state: 'notChecked',
    }];
    await save(next);
    // The chosen report is now selected for checking, once, and shows as not checked until it is read.
    setSel((prev) => new Set([...prev, key]));
  }

  async function addFolder() {
    const paths = mergeReportPaths([], folderPath);
    if (paths.length === 0) return;
    const next = [...scope.reports];
    for (const p of paths) {
      const key = `local:${p}`;
      if (next.some((r) => r.id === key)) continue;
      next.push({ id: key, kind: 'local', name: p.split(/[/\\]/).filter(Boolean).pop() ?? p, source: p, state: 'notChecked' });
    }
    const saved = await save(next);
    setSel(new Set((saved ?? next).map((r) => r.id)));
    setFolderPath('');
  }

  async function removeOne(id: string) {
    await save(scope.reports.filter((r) => r.id !== id));
  }

  async function check() {
    setBusy('check'); setError(null);
    try {
      const result = await rpc<ReportScopeResult>('checkReports', [...sel], consent, signInMode, tenant.trim() || null, null, 'human');
      setScope(result);
      if (result.usage) setUsage(result.usage);
      // P1. A reading changes every answer measured against it. The cleanup list is recomputed from the same
      // reading rather than keeping its old candidates under a new "checked against" label.
      onScopeChanged?.();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  // Browse can return several folders. They are added as a set, and a path typed in the box beside it is
  // added too, so nothing a person pointed at is silently dropped. Picks pass through verbatim; only the
  // typed half uses the ';' convention (a legal folder name can contain a semicolon).
  async function browse() {
    setBusy('browse');
    try {
      const picked = await pickReportPaths();
      const paths = mergeReportPaths(picked ?? [], folderPath);
      if (paths.length === 0) return;
      const next = [...scope.reports];
      for (const p of paths) {
        const key = `local:${p}`;
        if (next.some((r) => r.id === key)) continue;
        next.push({ id: key, kind: 'local', name: p.split(/[/\\]/).filter(Boolean).pop() ?? p, source: p, state: 'notChecked' });
      }
      await save(next);
      setFolderPath('');
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  return (
    <div className="fixed inset-0 z-40 flex" data-testid="choose-reports-scrim" style={{ background: 'rgba(0,0,0,.28)' }}
      onClick={(e) => { if (e.target === e.currentTarget) setDrawerOpen(false); }}>
      <div className="flex-1" />
      <ChooseReportsDrawer
        scope={scope} selected={sel} onClose={() => { setDrawerOpen(false); void refreshScope(); }}
        returningTo={returningTo}
        workspaces={workspaces} reports={reports} workspaceId={ws}
        signInMode={signInMode} tenantId={tenant}
        onWorkspaceId={setWs}
        onSignInMode={(v) => { setSignInMode(v); cloudGen.current++; setWorkspaces(null); setReports(null); }}
        onTenantId={(v) => { tenantOwner.current = v.trim() === '' ? null : 'user'; setTenant(v); cloudGen.current++; setWorkspaces(null); setReports(null); }}
        onLoadWorkspaces={() => void loadWorkspaces()} onLoadReports={() => void loadReports()}
        onToggle={(id) => {
          // A report in the DISCOVERED list is a choice being made: add it once, with its own coordinates, and
          // let the save decide the next selection. A report already on the model's list is only a tick.
          if ((reports ?? []).some((r) => r.id === id)) { void addPublished(id); return; }
          setSel((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
        }}
        onCheck={() => void check()}
        folderPath={folderPath} onFolderPath={setFolderPath} onBrowse={() => void browse()} onAddFolder={() => void addFolder()}
        onRemove={(id) => void removeOne(id)}
        busy={busy} error={error} consent={consent} onConsent={setConsent} />
    </div>
  );
}

// ---- helpers kept from the published-reports pane -------------------------------------------------
// The live XMLA endpoint carries the workspace NAME (powerbi://api.powerbi.com/v1.0/myorg/<WorkspaceName>).
// Pull that trailing segment so, once workspaces list, we can pre-select the one the model itself lives in.
// Anchored: only a TRAILING /myorg/<name> segment counts. Malformed percent-encoding returns null.
function workspaceNameFromEndpoint(endpoint?: string | null): string | null {
  if (!endpoint) return null;
  const m = /\/myorg\/([^/?#]+?)\/?$/i.exec(endpoint);
  if (!m) return null;
  try { return decodeURIComponent(m[1]); } catch { return null; }
}

/** Merge structured Browse picks with a free-text field's paths. Picks pass through VERBATIM (a legal folder
 * name can contain ';' or edge whitespace); only the free-text half uses the ';'-split + trim convention. */
function mergeReportPaths(picked: string[], freeText: string): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (const p of picked) { if (p && !seen.has(p)) { seen.add(p); out.push(p); } }
  for (const f of freeText.split(';')) {
    const t = f.trim();
    if (t && !seen.has(t)) { seen.add(t); out.push(t); }
  }
  return out;
}

/** The open model's identity as one comparable key. sessionId is the primary signal; the endpoint and
 * database ride along to catch a rebind that reuses a session. */
function sessionIdentityKey(s?: { sessionId?: string; liveEndpoint?: string; liveDatabase?: string } | null): string {
  return `${s?.sessionId ?? ''}|${s?.liveEndpoint ?? ''}|${s?.liveDatabase ?? ''}`;
}

/** Decide the tenant field's next value when the model's identity is observed. Only a value WE auto-filled
 * is ever replaced or cleared; a typed value is theirs until they empty the field themselves. */
function nextTenantValue(current: string, owner: 'auto' | 'user' | null, auto: string, modelSwitched: boolean):
  { value: string; owner: 'auto' | 'user' | null } {
  if (owner === 'user' && current !== '') return { value: current, owner };
  if (auto) {
    if (current === '' || (modelSwitched && owner === 'auto')) return { value: auto, owner: 'auto' };
    return { value: current, owner };
  }
  if (modelSwitched && owner === 'auto') return { value: '', owner: null };
  return { value: current, owner };
}

// ---- local primitives -------------------------------------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border p-4 ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color, border: `1px solid color-mix(in srgb,${color} 40%, transparent)` }}>{children}</div>;
}

export { mergeReportPaths, nextTenantValue, sessionIdentityKey, workspaceNameFromEndpoint };
export type { UnusedItem, UnusedResult, ImpactResult, ImpactAssessmentResult };
