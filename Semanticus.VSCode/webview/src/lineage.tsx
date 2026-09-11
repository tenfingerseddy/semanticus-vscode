import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, onProgress, revealInTree, pickReportPaths, type OperationProgress } from './bridge';
import { useConnection } from './connection';
import { KIND_GLYPH, KIND_COLOR, KIND_LABEL, useFillHeight, type LineageResult } from './lineagetypes';
import { LineageGraphView } from './lineagegraph';
import { LineageTreeView } from './lineagetree';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';

// The UI never speaks engine: map wire verdicts + check-kind identifiers to plain analyst labels.
const VERDICT_LABEL: Record<string, string> = { Verified: 'Verified', NeedsReview: 'Needs review', Broken: 'Broken', Unknown: 'Unknown', Overridden: 'Overridden' };
const prettyKind = (k: string) => (k || '').replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());

// ---- wire types (camelCase, from Semanticus.Engine/Lineage/LineageProtocol.cs) ---------------
// LineageNode/LineageEdge/LineageResult + KIND_GLYPH/KIND_COLOR now live in ./lineagetypes (shared with the graph view).
interface ImpactNode { ref: string; name: string; kind: string; depth: number; via: string }
interface ImpactResult { root: string; rootName: string; rootKind: string; impacted: ImpactNode[]; measures: number; columns: number; tables: number; relationships: number; other: number; caveat?: string }
interface UnusedItem { ref: string; name: string; kind: string; table?: string; isHidden?: boolean; verdict: string; refCount: number; blockedBy: string[]; reason: string }
interface UnusedResult { items: UnusedItem[]; safeCount: number; usedByUnusedOnlyCount: number; cautionCount: number; caveat?: string }
// remove_safe_objects result (the sweep's act half) — mirrors RemoveSafeReport in LineageProtocol.cs (camelCased).
interface RemovedObject { ref: string; name: string; kind: string; table?: string }
interface SkippedObject { ref: string; reason: string }
interface RemoveSafeReport { revision: number; removed: RemovedObject[]; skipped: SkippedObject[]; count: number; verification?: string; caveat?: string; note?: string }
// Cloud report layer (Phase 3) — mirrors Semanticus.Engine/Lineage/LineageProtocol.cs (camelCased).
interface CloudReport { id: string; name: string; datasetId?: string; reportType?: string; webUrl?: string }
// Workspace list (list_workspaces) — mirrors Semanticus.Engine/Alm/AlmProtocol.cs FabricWorkspace (camelCased).
interface FabricWorkspace { id: string; displayName: string; description?: string; type?: string; capacityId?: string }
interface ReportVisualUsage { page: string; visual?: string; visualType?: string; usedRefs: string[] }
interface ReportUsage { path: string; name: string; read: boolean; fieldCount: number; unresolved: number; usedRefs: string[]; extensionMeasures: string[]; visuals?: ReportVisualUsage[]; error?: string }
interface ReportAnalysisResult { reports: ReportUsage[]; reportsRead: number; reportsUnreadable: number; modelFieldsUsed: string[]; unused: UnusedResult; caveat?: string }
interface ImpactCoverageArea { area: string; status: string; checked: number; unknown: number; detail: string }
interface ImpactReportHit { path: string; name: string; visuals: number; usedRefs: string[] }
interface ImpactReplayCheck { id: string; kind: string; title: string; targetRef?: string; reason: string }
interface ImpactNextAction { op: string; args: string; reason: string }
interface ImpactAssessmentResult {
  objectRef: string; objectName: string; objectKind: string; modelName: string; intent: string; scope: string;
  verdict: 'Verified' | 'NeedsReview' | 'Broken' | 'Unknown'; modelImpact: ImpactResult;
  reportImpact: ImpactReportHit[]; reportsImpacted: number; visualsImpacted: number;
  replayChecks: ImpactReplayCheck[]; replayChecksOmitted: number; coverage: ImpactCoverageArea[];
  unknowns: string[]; summary: string; suggestedNextAction: ImpactNextAction;
}
type AuthMode = 'azcli' | 'interactive' | 'devicecode' | 'serviceprincipal';
type LineageMode = 'graph' | 'tree' | 'impact' | 'unused' | 'reports';
type ReportsBusy = 'load' | 'analyze' | 'analyzeLocal' | 'workspaces' | 'browse' | null;

interface LineageTabState {
  mode: LineageMode;
  setMode: React.Dispatch<React.SetStateAction<LineageMode>>;
  analysis: ReportAnalysisResult | null;
  localReportPaths: string[] | null;
  reportsStale: boolean;
  // Report-analysis lifecycle. Safety does NOT depend on the analysis being perfectly fresh: report-used fields are
  // excluded from "safe" (report-aware list), and EVERY safe removal re-verifies at apply (removeSafeObjects skips
  // anything that gained a dependency). This lifecycle is only about showing the LATEST verdicts: each analysis claims
  // a token at start; a model edit or a newer analysis bumps the generation, so a slow result whose token is stale is
  // DISCARDED rather than overwriting a newer view. A model edit sets a soft `reportsStale` hint (prompt to
  // re-analyze), never a block.
  beginAnalyze: (localPaths?: string[] | null) => number;
  applyAnalysis: (analysis: ReportAnalysisResult | null, localPaths: string[] | null, token: number) => void;
  abandonAnalyze: (token: number) => void;
  clearAnalysis: () => void;
  ws: string;
  setWs: React.Dispatch<React.SetStateAction<string>>;
  tenant: string;
  setTenant: React.Dispatch<React.SetStateAction<string>>;
  authMode: AuthMode;
  setAuthMode: React.Dispatch<React.SetStateAction<AuthMode>>;
  workspaces: FabricWorkspace[] | null;
  setWorkspaces: React.Dispatch<React.SetStateAction<FabricWorkspace[] | null>>;
  wsError: string | null;
  setWsError: React.Dispatch<React.SetStateAction<string | null>>;
  manualWs: boolean;
  setManualWs: React.Dispatch<React.SetStateAction<boolean>>;
  reports: CloudReport[] | null;
  setReports: React.Dispatch<React.SetStateAction<CloudReport[] | null>>;
  loadedWs: string;
  setLoadedWs: React.Dispatch<React.SetStateAction<string>>;
  sel: Set<string>;
  setSel: React.Dispatch<React.SetStateAction<Set<string>>>;
  consent: boolean;
  setConsent: React.Dispatch<React.SetStateAction<boolean>>;
  pbirPath: string;
  setPbirPath: React.Dispatch<React.SetStateAction<string>>;
  pickedPaths: string[];
  setPickedPaths: React.Dispatch<React.SetStateAction<string[]>>;
  busy: ReportsBusy;
  setBusy: React.Dispatch<React.SetStateAction<ReportsBusy>>;
  cloudProgress: OperationProgress | null;
  setCloudProgress: React.Dispatch<React.SetStateAction<OperationProgress | null>>;
  error: string | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  gates: {
    cloudGen: React.MutableRefObject<number>;
    localGen: React.MutableRefObject<number>;
    activeCloudRunId: React.MutableRefObject<string | null>;
    tenantOwner: React.MutableRefObject<'auto' | 'user' | null>;
  };
}

const LineageTabStateContext = createContext<LineageTabState | null>(null);

function useLineageTabState(): LineageTabState {
  const state = useContext(LineageTabStateContext);
  if (!state) throw new Error('LineageView must be rendered inside LineageTabStateProvider');
  return state;
}

// Null-tolerant selector for the Studio tab bar: true while any report-discovery/analysis op (cloud list, cloud or
// local analyze, workspace list, browse) is in flight. Read outside LineageView so cloud analysis that keeps running
// while Lineage is hidden still shows a busy affordance on the tab bar.
export function useLineageReportsBusy(): boolean {
  return useContext(LineageTabStateContext)?.busy != null;
}

// This holder is mounted by App above the conditional Studio tab body. Report discovery, an in-flight analysis,
// run-correlated progress, and its eventual result therefore outlive LineageView's mount without becoming persisted
// webview data. App's session-keyed boundary still destroys the holder on a model swap.
export function LineageTabStateProvider({ children }: { children: React.ReactNode }) {
  const { session } = useConnection();
  const [mode, setMode] = useState<LineageMode>('tree');
  const [analysis, setAnalysis] = useState<ReportAnalysisResult | null>(null);
  const [localReportPaths, setLocalReportPaths] = useState<string[] | null>(null);
  const [ws, setWs] = useState('');
  const [tenant, setTenant] = useState('');
  const [authMode, setAuthMode] = useState<AuthMode>('azcli');
  const [workspaces, setWorkspaces] = useState<FabricWorkspace[] | null>(null);
  const [wsError, setWsError] = useState<string | null>(null);
  const [manualWs, setManualWs] = useState(false);
  const [reports, setReports] = useState<CloudReport[] | null>(null);
  const [loadedWs, setLoadedWs] = useState('');
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [consent, setConsent] = useState(false);
  const [pbirPath, setPbirPath] = useState('');
  const [pickedPaths, setPickedPaths] = useState<string[]>([]);
  const [busy, setBusy] = useState<ReportsBusy>(null);
  const [cloudProgress, setCloudProgress] = useState<OperationProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  // A model edit landed after the report analysis: a soft "re-analyze" hint (removal stays safe, it is re-checked at apply).
  const [reportsStale, setReportsStale] = useState(false);

  // These gates belong to the holder too: recreating them with ReportsPane would sever runId progress correlation and
  // let an old async completion bypass an invalidation after the user left the tab.
  const cloudGen = useRef(0);
  const localGen = useRef(0);
  const activeCloudRunId = useRef<string | null>(null);
  const prevIdentity = useRef<string | undefined>(undefined);
  const tenantOwner = useRef<'auto' | 'user' | null>(null);
  // Monotonic generation so a slow in-flight (re-)analysis can't clobber a newer analysis (or un-stale a fresh edit):
  // bumped on every model edit AND every manual analyze; a result applies only if its claimed token is still current.
  // Distinct from cloudGen/localGen on purpose — those own flow identity (busy/error/runId); this owns result freshness.
  const reanalyzeGen = useRef(0);
  // Mirror of the analysis state for the stable onDidChange closure below: a model edit invalidates a loaded analysis.
  const analysisRef = useRef<{ has: boolean }>({ has: false });
  analysisRef.current = { has: !!analysis };
  // The LOCAL paths of the NEWEST analyze request that has not failed (null for a cloud request). The model-edit
  // re-run below targets THIS, not the displayed analysis's paths — so a replacement request interrupted mid-flight
  // still lands (including a first-ever one with nothing displayed yet), instead of silently resurrecting the old scope.
  const requestedLocalPaths = useRef<string[] | null>(null);
  const staleTimer = useRef<number | undefined>(undefined);

  const beginAnalyze = (localPaths: string[] | null = null) => { requestedLocalPaths.current = localPaths; return ++reanalyzeGen.current; };
  const applyAnalysis = (next: ReportAnalysisResult | null, localPaths: string[] | null, token: number) => {
    if (token !== reanalyzeGen.current) return;
    setAnalysis(next); setLocalReportPaths(localPaths); setReportsStale(false);
  };
  // The owning request errored: drop its pending intent so a failed path is not silently retried on the next model
  // edit. Token-guarded — a request already superseded (by a newer analyze OR a model edit) must not clear the
  // newer intent (for an edit-orphaned request, the kept intent is exactly what the debounced re-run delivers).
  const abandonAnalyze = (token: number) => { if (token === reanalyzeGen.current) requestedLocalPaths.current = null; };
  const clearAnalysis = () => { reanalyzeGen.current++; requestedLocalPaths.current = null; setAnalysis(null); setLocalReportPaths(null); setReportsStale(false); };

  // A model edit invalidates ANY in-flight analysis (including a first-ever one, when has=false), so bump the
  // generation UNCONDITIONALLY — a slow result whose token is no longer current is discarded (keeps the view on the
  // latest verdicts). A loaded analysis gets the soft `stale` hint; a LOCAL request is then re-run in place (offline,
  // cheap, debounced), while a CLOUD one stays stale until the user re-runs it (a fresh one needs a sign-in). This
  // lives in the holder, not LineageView, so an edit made while Lineage is hidden still invalidates correctly.
  useEffect(() => {
    const off = onDidChange(() => {
      reanalyzeGen.current++;
      if (analysisRef.current.has) setReportsStale(true);
      window.clearTimeout(staleTimer.current);
      staleTimer.current = window.setTimeout(() => {
        const req = requestedLocalPaths.current;
        if (req && req.length) {
          const token = ++reanalyzeGen.current;
          rpc<ReportAnalysisResult>('analyzeReports', req)
            .then((r) => applyAnalysis(r, req, token))
            .catch(() => { /* leave it stale (already set); the next Analyze refreshes it */ });
        }
      }, 350);
    });
    return () => { off(); window.clearTimeout(staleTimer.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Keep the progress listener alive while Lineage is not the selected Studio tab. Correlation remains exact: only
  // analyze_cloud_reports events carrying the holder's current runId can advance the visible progress snapshot.
  // Monotone per run: the engine publishes per COMPLETED report from a parallel fan-out, so two simultaneous
  // completions can deliver done=2 before done=1 — a lower count for the same run is dropped, never shown.
  useEffect(() => onProgress((p) => {
    if (p.opKey === 'analyze_cloud_reports' && p.runId === activeCloudRunId.current)
      setCloudProgress((cur) => (cur && cur.runId === p.runId && p.done < cur.done ? cur : p));
  }), []);

  // Preserve ReportsPane's original model-identity invalidation above the tab switch. A same-session endpoint/database
  // rebind still bumps both generations and clears every model-scoped discovery/result; a different session remounts
  // this whole provider through App's key. Tenant ownership remains unchanged: user input is never overwritten here.
  useEffect(() => {
    if (!session) return;
    const ident = sessionIdentityKey(session);
    const modelSwitched = prevIdentity.current !== undefined && prevIdentity.current !== ident;
    prevIdentity.current = ident;
    if (modelSwitched) {
      cloudGen.current++; localGen.current++;
      activeCloudRunId.current = null;
      setBusy(null);
      setCloudProgress(null);
      setWorkspaces(null); setWs(''); setWsError(null); setLoadedWs('');
      setReports(null); setSel(new Set()); setConsent(false); setError(null); clearAnalysis();
    }
    const next = nextTenantValue(tenant, tenantOwner.current, session.currentTenant || '', modelSwitched);
    tenantOwner.current = next.owner;
    if (next.value !== tenant) setTenant(next.value);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session, session?.currentTenant]);

  return (
    <LineageTabStateContext.Provider value={{
      mode, setMode, analysis, localReportPaths, reportsStale, beginAnalyze, applyAnalysis, abandonAnalyze, clearAnalysis,
      ws, setWs, tenant, setTenant, authMode, setAuthMode, workspaces, setWorkspaces, wsError, setWsError,
      manualWs, setManualWs, reports, setReports, loadedWs, setLoadedWs, sel, setSel, consent, setConsent,
      pbirPath, setPbirPath, pickedPaths, setPickedPaths,
      busy, setBusy, cloudProgress, setCloudProgress, error, setError,
      gates: { cloudGen, localGen, activeCloudRunId, tenantOwner },
    }}>
      {children}
    </LineageTabStateContext.Provider>
  );
}

// Merge published-report usage into the MODEL lineage graph so the chain extends downstream into where each field is
// actually shown: field → visual → page → report (a visual belongs to exactly one page/report, so this stays precise).
// Edges are 'reportUsage' (from=dependant, to=dependency) — matching buildDeps so downstream(measure) = its visuals,
// then each visual's page, then its report. Only links to fields that exist in the model graph (never dangling); a
// visual with no model field is skipped. Returns the base graph unchanged when nothing connects (no clutter).
function augmentGraphWithReports(base: LineageResult | null, analysis: ReportAnalysisResult | null): LineageResult | null {
  if (!base || !analysis?.reports?.length) return base;
  const modelRefs = new Set(base.nodes.map((n) => n.ref));
  const nodes = base.nodes.slice();
  const edges = base.edges.slice();
  const seen = new Set(modelRefs);
  const addNode = (ref: string, name: string, kind: string, table?: string) => { if (!seen.has(ref)) { seen.add(ref); nodes.push({ ref, name, kind, table }); } };
  let linked = 0;
  // Key the ephemeral graph refs on the report's ARRAY INDEX, not its name: `r.name` is only the leaf folder name, so
  // two same-named reports (two "Sales.Report" folders, or two cloud reports named "Sales") would otherwise share a
  // ref namespace and addNode-dedup would merge them — attaching one report's fields to the other's visual. The index
  // is unique per report; display names (rname / page / visual) are still the friendly strings.
  analysis.reports.forEach((r, ri) => {
    if (!r.visuals?.length) return;
    const rname = r.name || r.path || `Report ${ri + 1}`;
    const reportRef = `report:${ri}:${rname}`;
    let reportAdded = false;
    const pageSeen = new Set<string>();
    r.visuals.forEach((v, vi) => {
      const fields = (v.usedRefs ?? []).filter((f) => modelRefs.has(f));
      if (fields.length === 0) return;   // a visual must touch a model field to be worth showing
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
  return { nodes, edges, caveat: undefined };   // report usage is now included → drop the model-only caveat
}

export function LineageView({ navTarget, onOpenPlan, onOpenTests, onOpenWorkflow }: {
  navTarget?: { ref: string; nonce: number } | null;
  onOpenPlan?: () => void;
  onOpenTests?: () => void;
  onOpenWorkflow?: (name: string) => void;
} = {}) {
  const { mode, setMode, analysis: reportAnalysis, localReportPaths, reportsStale } = useLineageTabState();
  // Open on the TREE: it's bounded (fan-out capped) so it renders instantly, whereas the whole-model force graph is the
  // heavy view. Users switch to Graph when they want the free-form explore.
  const [graph, setGraph] = useState<LineageResult | null>(null);
  // Report analysis (cloud or offline PBIR), lifted here so it persists across mode switches and augments the Graph/Tree
  // with the report layer (field → visual → page → report). The "Published reports" pane reports its result up.
  const [root, setRoot] = useState<string | null>(null);
  const [impact, setImpact] = useState<ImpactResult | null>(null);
  const [unused, setUnused] = useState<UnusedResult | null>(null);
  // Per-pane error slots so a success in one loader can't erase another's error (the loaders run concurrently).
  const [graphErr, setGraphErr] = useState<string | null>(null);
  const [unusedErr, setUnusedErr] = useState<string | null>(null);
  const [impactErr, setImpactErr] = useState<string | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const rootRef = useRef<string | null>(null);   // mirror of `root` read inside the (stable) onDidChange closure

  async function loadGraph() {
    try { setGraph(await rpc<LineageResult>('getLineage')); setGraphErr(null); }
    catch (e) { setGraphErr(String((e as Error).message ?? e)); }
  }
  async function loadUnused() {
    try { setUnused(await rpc<UnusedResult>('unusedObjects')); setUnusedErr(null); }
    catch (e) { setUnusedErr(String((e as Error).message ?? e)); }
  }
  async function loadImpact(ref: string) {
    setRoot(ref); rootRef.current = ref;
    try { setImpact(await rpc<ImpactResult>('impactOf', ref)); setImpactErr(null); }
    catch (e) { setImpactErr(String((e as Error).message ?? e)); setImpact(null); }
  }

  // Initial load + live refresh: the lineage graph is engine-owned model state, so re-read it (debounced) whenever
  // the model changes on EITHER door. The handler reads rootRef (NOT the `root` state) so a selection made AFTER
  // this effect ran still re-fetches its impact — the closure captured `root` once, at mount, when it was null.
  useEffect(() => {
    void loadGraph(); void loadUnused();
    const off = onDidChange(() => {
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => { void loadGraph(); void loadUnused(); if (rootRef.current) void loadImpact(rootRef.current); }, 350);
    });
    return () => { off(); window.clearTimeout(timer.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A Model-tree "Show Lineage & Impact" jump focuses that object — switch to Impact and load its dependents.
  // Nonce makes a repeat jump to the same object re-fire.
  useEffect(() => { if (navTarget?.ref) { setMode('impact'); void loadImpact(navTarget.ref); } }, [navTarget?.nonce]);  // eslint-disable-line react-hooks/exhaustive-deps

  // Graph/Tree render the model graph AUGMENTED with the report layer (when report analysis is loaded); the other panes
  // stay model-only (impactOf / unused are engine model-only).
  const fullGraph = useMemo(() => augmentGraphWithReports(graph, reportAnalysis), [graph, reportAnalysis]);
  const reportsMerged = fullGraph !== graph;
  // The "Safe to remove" pane is REPORT-AWARE only when a loaded analysis actually READ a report (reportsRead>0 — an
  // all-unreadable result is model-only data the engine returns as a courtesy, and must NOT be labelled protective).
  // When such an analysis exists we show ITS list (report-used fields excluded from "safe"), never a silent revert to
  // a model-only list where a report-used field reappears as "safe". A model edit marks the list stale — a SOFT
  // re-analyze hint, not a block: every "safe" delete re-verifies at apply (removeSafeObjects), so a stale view can
  // prompt, never endanger. Only with no usable analysis do we show model-only.
  const hasUsableAnalysis = !!reportAnalysis && reportAnalysis.reportsRead > 0;
  const unusedView = hasUsableAnalysis ? reportAnalysis!.unused : unused;
  const unusedScope: 'model-only' | 'report-aware' = hasUsableAnalysis ? 'report-aware' : 'model-only';
  const unusedStale = hasUsableAnalysis && reportsStale;
  const err = mode === 'reports' ? null : (mode === 'graph' || mode === 'tree') ? graphErr : (graphErr ?? (mode === 'impact' ? impactErr : unusedErr));
  // Caveat = the honesty note for the list actually shown. Graph/Tree carry the merged-graph note; the unused pane
  // carries the caveat of whichever list (model-only or report-aware) it is rendering.
  const caveat = mode === 'reports' ? null
    : (mode === 'graph' || mode === 'tree') ? fullGraph?.caveat
    : mode === 'impact' ? (graph?.caveat ?? impact?.caveat)
    : unusedView?.caveat;

  return (
    <div className="flex flex-col gap-4 p-4">
      <Panel>
        <div className="flex items-center gap-4">
          <div className="flex flex-col items-center justify-center w-20 h-20 rounded-2xl shrink-0" style={{ background: 'var(--sem-surface-2)', boxShadow: 'inset 0 0 0 2px var(--sem-accent)' }}>
            <div className="text-2xl">⇄</div>
            <div className="text-[10px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>lineage</div>
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-[15px] font-semibold">Lineage &amp; Impact</div>
            <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
              Trace where a field comes from and what depends on it, then find what's safe to remove.
            </div>
            <div className="flex gap-1.5 mt-2">
              <Seg active={mode === 'graph'} onClick={() => setMode('graph')}>Graph</Seg>
              <Seg active={mode === 'tree'} onClick={() => setMode('tree')}>Tree</Seg>
              <Seg active={mode === 'impact'} onClick={() => setMode('impact')}>Impact</Seg>
              <Seg active={mode === 'unused'} onClick={() => setMode('unused')}>Safe to remove</Seg>
              <Seg active={mode === 'reports'} onClick={() => setMode('reports')}>Published reports</Seg>
            </div>
          </div>
          <div className="text-right text-[11px] shrink-0" style={{ color: 'var(--sem-muted)' }}>
            {fullGraph && <div className="tnum">{fullGraph.nodes.length} nodes · {fullGraph.edges.length} edges{reportsMerged ? ' · +reports' : ''}</div>}
            {mode === 'unused' && unusedView && <div className="tnum mt-0.5">{unusedView.safeCount} safe · {unusedView.usedByUnusedOnlyCount} dead-only{unusedView.cautionCount ? ` · ${unusedView.cautionCount} caution` : ''}</div>}
          </div>
        </div>
      </Panel>

      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {caveat && <Banner color="var(--sem-warn)">{caveat}</Banner>}

      {mode === 'graph'
        ? <LineageGraphView graph={fullGraph} onOpenImpact={(r) => { setMode('impact'); void loadImpact(r); }} />
        : mode === 'tree'
        // The tree's safe-to-remove dots carry the SAME verdict basis as the Safe-to-remove pane (report-aware when a
        // usable analysis is loaded) — a report-only field must not wear a green "safe" dot in one view and not the other.
        ? <LineageTreeView graph={fullGraph} unusedItems={unusedView?.items} onOpenImpact={(r) => { setMode('impact'); void loadImpact(r); }} />
        : mode === 'impact'
          ? <ImpactPane graph={graph} root={root} impact={impact} onPick={loadImpact} reportPaths={localReportPaths ?? undefined}
              onOpenPlan={onOpenPlan} onOpenTests={onOpenTests} onOpenWorkflow={onOpenWorkflow} />
          : mode === 'unused'
            ? <UnusedPane unused={unusedView}
                scope={unusedScope}
                stale={unusedStale}
                reportPaths={hasUsableAnalysis ? (localReportPaths ?? undefined) : undefined}
                onImpact={(r) => { setMode('impact'); void loadImpact(r); }} />
            : <ReportsPane onImpact={(r) => { setMode('impact'); void loadImpact(r); }} />}
    </div>
  );
}

// ---- Impact mode ------------------------------------------------------------------------------
// Kinds impactOf can meaningfully re-root on (IDaxObject / Column). Structural leaves (relationship/hierarchy/level)
// would resolve but yield an empty, misleading "nothing depends on this" — reveal those in the tree instead.
const RE_ROOTABLE = new Set(['measure', 'column', 'calcColumn', 'calcTable', 'calcGroup', 'calcitem', 'table']);

function ImpactPane({ graph, root, impact, onPick, reportPaths, onOpenPlan, onOpenTests, onOpenWorkflow }: {
  graph: LineageResult | null; root: string | null; impact: ImpactResult | null; onPick: (ref: string) => void;
  reportPaths?: string[]; onOpenPlan?: () => void; onOpenTests?: () => void; onOpenWorkflow?: (name: string) => void;
}) {
  const [q, setQ] = useState('');
  const [assessment, setAssessment] = useState<ImpactAssessmentResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [staged, setStaged] = useState(false);
  const [probeMode, setProbeMode] = useState(false);
  // Pickable roots: measures + columns (the things you'd ask "what breaks if I change this?" about).
  const pickable = useMemo(() => (graph?.nodes ?? []).filter((n) => n.kind === 'measure' || n.kind === 'column' || n.kind === 'calcColumn'), [graph]);
  const view = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const list = needle ? pickable.filter((n) => (n.table + ' ' + n.name).toLowerCase().includes(needle)) : pickable;
    return list.slice(0, 400);
  }, [pickable, q]);
  // Fill the viewport height like the Graph/Tree canvases do, so the picker + impact list use the whole tab instead of
  // a short 460px box with dead space below. `lg:` only: at the narrow (stacked) breakpoint the panels stay natural
  // height (the `--sem-pane-h` var drives `h-full` solely on wide, where the two panels share one row).
  const [fillRef, paneH] = useFillHeight(440);

  // An assessment is a point-in-time proof. A model refresh or a different local report scope invalidates it just as
  // surely as selecting another object; never leave a stale verdict visible after either input changes.
  useEffect(() => { setAssessment(null); setActionError(null); setStaged(false); setProbeMode(false); }, [root, impact, reportPaths]);

  async function assess(intent: 'change' | 'rename' | 'remove', probes = false) {
    if (!root) return;
    setBusy(intent); setActionError(null); setStaged(false); setProbeMode(probes);
    try {
      setAssessment(await rpc<ImpactAssessmentResult>('impactAssessment', {
        objectRef: root, intent, scope: 'modelAndReports', reportPaths: reportPaths ?? [],
      }));
    } catch (e) { setActionError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  async function stageRemoval() {
    if (!root || !assessment || assessment.intent !== 'remove' || assessment.verdict === 'Broken') return;
    setBusy('stage'); setActionError(null);
    try {
      await rpc('addPlanItem', root, 'delete', null, `Remove ${assessment.objectName}`, null, null, 'human');
      setStaged(true);
    } catch (e) { setActionError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }

  const actionStyle = { background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' };
  const verdictColor = assessment?.verdict === 'Verified' ? 'var(--sem-good)'
    : assessment?.verdict === 'Broken' ? 'var(--sem-bad)'
    : assessment?.verdict === 'NeedsReview' ? 'var(--sem-warn)' : 'var(--sem-muted)';

  return (
    <div ref={fillRef} className="grid grid-cols-1 lg:grid-cols-[320px_1fr] gap-4 lg:h-[var(--sem-pane-h)]" style={{ '--sem-pane-h': `${paneH}px` } as React.CSSProperties}>
      <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full">
        <SectionTitle>Pick an object</SectionTitle>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter measures & columns…" spellCheck={false}
          className="mt-2 text-[12px] px-2 py-1 rounded-md outline-none w-full shrink-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <div className="mt-2 overflow-auto flex-1 min-h-0 max-h-[460px] lg:max-h-none">
          {view.map((n) => (
            <Row key={n.ref} active={n.ref === root} onClick={() => onPick(n.ref)} testId="lineage-impact-object">
              <Glyph kind={n.kind} />
              <span className="truncate" style={{ opacity: n.isHidden ? 0.55 : 1 }}>{n.name}</span>
              <span className="ml-auto text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 110 }}>{n.table}</span>
            </Row>
          ))}
          {view.length === 0 && <Empty>No matching objects.</Empty>}
        </div>
      </Panel>

      <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full">
        {!impact ? (
          <Empty>Select a measure or column to see everything that depends on it.</Empty>
        ) : (
          <>
            <div className="flex items-center gap-2 flex-wrap shrink-0">
              <Glyph kind={impact.rootKind} />
              <span className="text-[14px] font-semibold">{impact.rootName}</span>
              <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{impact.rootKind}</span>
            </div>
            <div className="flex gap-2 mt-3 flex-wrap shrink-0">
              <button onClick={() => void assess('change')} disabled={busy != null} className="text-[11px] px-2.5 py-1.5 rounded-md font-medium" style={actionStyle}>Check blast radius</button>
              <button onClick={() => void assess('rename')} disabled={busy != null} className="text-[11px] px-2.5 py-1.5 rounded-md font-medium" style={actionStyle}>Safe rename</button>
              <button onClick={() => void assess('remove')} disabled={busy != null} className="text-[11px] px-2.5 py-1.5 rounded-md font-medium" style={actionStyle}>Stage removal</button>
              <button onClick={() => void assess('change', true)} disabled={busy != null} className="text-[11px] px-2.5 py-1.5 rounded-md font-medium" style={actionStyle}>Create probes</button>
              {busy && <span className="text-[11px] self-center" style={{ color: 'var(--sem-muted)' }}>Assessing…</span>}
            </div>
            {actionError && <div className="text-[11px] mt-2 rounded-md px-2.5 py-2" style={{ color: 'var(--sem-bad)', border: '1px solid var(--sem-bad)' }}>{actionError}</div>}

            {assessment && (
              <div className="mt-3 rounded-lg border p-3 shrink-0" style={{ borderColor: verdictColor, background: 'var(--sem-bg)' }}>
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-[11px] font-bold px-2 py-0.5 rounded" style={{ color: verdictColor, border: `1px solid ${verdictColor}` }}>{VERDICT_LABEL[assessment.verdict] ?? prettyKind(assessment.verdict)}</span>
                  <span className="text-[12px] font-medium">{assessment.summary}</span>
                  <span className="ml-auto text-[10px]" style={{ color: 'var(--sem-muted)' }}>{assessment.scope === 'modelAndReports' ? 'model + supplied reports' : 'model only'}</span>
                </div>
                <div className="grid grid-cols-2 xl:grid-cols-4 gap-1.5 mt-2">
                  {assessment.coverage.map((c) => (
                    <div key={c.area} className="rounded px-2 py-1" title={c.detail} style={{ background: 'var(--sem-surface-2)' }}>
                      <div className="text-[9px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{c.area}</div>
                      <div className="text-[11px] font-medium">{prettyKind(c.status)}{c.checked ? ` · ${c.checked} checked` : ''}{c.unknown ? ` · ${c.unknown} unknown` : ''}</div>
                    </div>
                  ))}
                </div>
                {assessment.unknowns.length > 0 && (
                  <div className="mt-2 rounded-md px-2.5 py-2" style={{ border: '1px solid var(--sem-warn)', color: 'var(--sem-warn)' }}>
                    <div className="text-[10px] font-semibold uppercase tracking-wide">Coverage gaps</div>
                    {assessment.unknowns.map((u, i) => <div key={i} className="text-[11px] mt-0.5">• {u}</div>)}
                  </div>
                )}
                {(assessment.reportImpact.length > 0 || assessment.replayChecks.length > 0) && (
                  <div className="grid grid-cols-1 xl:grid-cols-2 gap-2 mt-2">
                    <div className="rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)' }}>
                      <div className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Known report impact</div>
                      {assessment.reportImpact.length === 0
                        ? <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>No supplied report uses the affected refs.</div>
                        : assessment.reportImpact.map((r) => <div key={r.path} className="text-[11px] mt-1"><b>{r.name}</b> · {r.visuals} visual{r.visuals === 1 ? '' : 's'}</div>)}
                    </div>
                    <div className="rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)' }}>
                      <div className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Checks to replay</div>
                      {assessment.replayChecks.map((c) => <div key={c.kind + ':' + c.id} className="text-[11px] mt-1"><b>{c.title}</b> <span style={{ color: 'var(--sem-muted)' }}>· {prettyKind(c.kind)}</span></div>)}
                      {assessment.replayChecksOmitted > 0 && <div className="text-[10px] mt-1" style={{ color: 'var(--sem-warn)' }}>+{assessment.replayChecksOmitted} more checks omitted from this view</div>}
                    </div>
                  </div>
                )}
                <div className="flex items-start gap-2 mt-2 text-[11px]">
                  <span className="font-semibold shrink-0">Next:</span>
                  <div style={{ color: 'var(--sem-muted)' }}>{assessment.suggestedNextAction.reason}</div>
                </div>
                {assessment.intent === 'remove' && (
                  <div className="flex items-center gap-2 mt-2">
                    <button onClick={() => void stageRemoval()} disabled={busy != null || assessment.verdict === 'Broken' || staged}
                      className="text-[11px] px-2.5 py-1 rounded-md font-medium" style={{ background: assessment.verdict === 'Broken' ? 'var(--sem-surface-2)' : 'var(--sem-accent)', color: assessment.verdict === 'Broken' ? 'var(--sem-muted)' : 'var(--sem-on-accent)', opacity: staged ? 0.6 : 1 }}>
                      {staged ? 'Staged for review' : assessment.verdict === 'Broken' ? 'Resolve known dependants first' : 'Stage in Change Plan'}
                    </button>
                    {staged && onOpenPlan && <button onClick={onOpenPlan} className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>Open Change Plan</button>}
                    <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Nothing is removed until the proposed item is approved and applied.</span>
                  </div>
                )}
                {assessment.intent === 'rename' && (
                  <div className="flex items-center gap-2 mt-2">
                    {onOpenWorkflow && <button onClick={() => onOpenWorkflow('governed-rename')} className="text-[11px] px-2.5 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Open Safe rename workflow</button>}
                    <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Stage the rename, review external bindings, apply FormulaFixup, replay proof and save one certificate.</span>
                  </div>
                )}
                {probeMode && (
                  <div className="flex items-center gap-2 mt-2">
                    {onOpenTests && <button onClick={onOpenTests} className="text-[11px] px-2.5 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Open Tests</button>}
                    <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{assessment.replayChecks.length} existing checks are scheduled. Ask AI Assistant to draft missing probes against this object, then save accepted tests.</span>
                  </div>
                )}
              </div>
            )}
            <div className="flex gap-1.5 mt-2 flex-wrap shrink-0">
              <Stat n={impact.impacted.length} label="impacted" strong />
              <Stat n={impact.measures} label="measures" />
              <Stat n={impact.columns} label="columns" />
              <Stat n={impact.relationships} label="relationships" />
              {impact.other > 0 && <Stat n={impact.other} label="other" />}
            </div>
            <div className="mt-3 flex-1 min-h-0 overflow-auto">
              {impact.impacted.length === 0 ? (
                <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No model dependency was found. Run an action above before treating that as safe.</div>
              ) : (
                <div className="flex flex-col">
                  {impact.impacted.map((n) => (
                    <Row key={n.ref} onClick={() => RE_ROOTABLE.has(n.kind) ? onPick(n.ref) : revealInTree(n.ref)}>
                      <span style={{ width: Math.min(n.depth - 1, 6) * 14 }} className="shrink-0" />
                      <Glyph kind={n.kind} />
                      <span className="truncate">{n.name}</span>
                      <ViaBadge via={n.via} />
                      <span className="ml-auto text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>depth {n.depth}</span>
                      <button className="text-[10px] shrink-0 ml-1" title="Reveal in Model tree" onClick={(e) => { e.stopPropagation(); revealInTree(n.ref); }} style={{ color: 'var(--sem-muted)' }}>⤢</button>
                    </Row>
                  ))}
                </div>
              )}
            </div>
          </>
        )}
      </Panel>
    </div>
  );
}

// ---- Safe-to-remove mode ----------------------------------------------------------------------
// Detect stays free; every row also gets a free per-item Delete (one at a time, undoable). The header's
// "Remove all N safe to remove" is the Pro sweep: the engine recomputes and re-verifies each item at apply
// time, deletes the still-safe set as one undoable step, and reports what it removed vs skipped (and why).
// `reportPaths` (local PBIR analysis only) makes the engine's at-apply re-check report-aware too. `scope` labels which
// verdict basis the list carries (model-only vs report-aware). `stale` = a model edit landed since the analysis, so the
// shown verdicts may be out of date: a SOFT hint prompting a re-analyze, NOT a block — removal stays safe because every
// "safe" delete re-verifies at apply (removeSafeObjects). A delete/sweep refresh is owned by the PARENT holder
// (model/didChange re-runs a local analysis in place / marks a cloud one stale).
function UnusedPane({ unused, onImpact, reportPaths, scope, stale }: { unused: UnusedResult | null; onImpact: (ref: string) => void; reportPaths?: string[]; scope?: 'model-only' | 'report-aware'; stale?: boolean }) {
  const [q, setQ] = useState('');
  const [kind, setKind] = useState('all');   // item-type facet: scope the candidate list to one kind (measures, columns, …)
  const tier = useTier();
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);
  const [sweep, setSweep] = useState<RemoveSafeReport | null>(null);
  const [confirmSweep, setConfirmSweep] = useState(false);
  const [confirmItem, setConfirmItem] = useState<UnusedItem | null>(null);
  // If a text search empties the selected kind, drop the stored selection back to All so it does not silently
  // re-apply when the search is cleared. effKind (below) already keeps the CURRENT render correct; this resets the
  // remembered intent to match. Runs before the early return to keep hook order stable.
  useEffect(() => {
    if (kind === 'all' || !unused) return;
    const n = q.trim().toLowerCase();
    const present = unused.items.some((i) => i.kind === kind && (!n || (i.table + ' ' + i.name).toLowerCase().includes(n)));
    if (!present) setKind('all');
  }, [kind, q, unused]);
  if (!unused) return <Panel><Empty>Scanning the model…</Empty></Panel>;

  // Two independent filters over the same list: a free-text needle and an item-KIND facet. Facet counts are taken
  // over the text-filtered set (each chip shows how many match the current search), and a selected kind that the
  // text search has emptied falls back to "all" so the view never strands on an invisible facet.
  const needle = q.trim().toLowerCase();
  const textItems = needle ? unused.items.filter((i) => (i.table + ' ' + i.name).toLowerCase().includes(needle)) : unused.items;
  const kindCounts = new Map<string, number>();
  for (const i of textItems) kindCounts.set(i.kind, (kindCounts.get(i.kind) ?? 0) + 1);
  const kinds = [...kindCounts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  const effKind = kind !== 'all' && kindCounts.has(kind) ? kind : 'all';
  const items = effKind === 'all' ? textItems : textItems.filter((i) => i.kind === effKind);
  const safe = items.filter((i) => i.verdict === 'safe');
  const dead = items.filter((i) => i.verdict === 'usedByUnusedOnly');
  const caution = items.filter((i) => i.verdict === 'caution');
  const allSafe = unused.items.filter((i) => i.verdict === 'safe');   // the sweep targets the FULL safe set, not the filtered view

  async function removeAll() {
    const refs = allSafe.map((i) => i.ref);
    if (refs.length === 0) return;
    if (!confirmSweep) { setConfirmSweep(true); setConfirmItem(null); return; }
    setBusy(true); setErr(null); setUpsell(null); setSweep(null); setConfirmSweep(false);
    try {
      const r = await rpc<RemoveSafeReport>('removeSafeObjects', refs, reportPaths ?? null);
      setSweep(r);
    } catch (e) {
      // A free click on the bulk sweep gets the plain invitation, not a raw exception in a red banner.
      if (isEntitlementError(e)) setUpsell(`Each item can be deleted one at a time free, right here on each row. Pro removes all ${refs.length} verified-safe items in one undoable step.`);
      else setErr(String((e as Error).message ?? e));
    } finally { setBusy(false); }
  }

  async function deleteOne(item: UnusedItem) {
    if (confirmItem?.ref !== item.ref) { setConfirmItem(item); setConfirmSweep(false); return; }
    setConfirmItem(null);
    if (item.verdict === 'safe') {
      // A "safe" delete goes through the single-item sweep (free): the engine RE-VERIFIES at apply — model + local
      // reports — and SKIPS the item if a new dependent appeared since the scan, instead of a raw unchecked delete.
      // This closes the scan-to-click window (a fresh model referencer, or a stale-ish local snapshot).
      setErr(null); setSweep(null);
      try { setSweep(await rpc<RemoveSafeReport>('removeSafeObjects', [item.ref], reportPaths ?? null)); setUpsell(null); }
      catch (e) { setErr(String((e as Error).message ?? e)); }
      return;
    }
    setErr(null); setSweep(null);
    // A successful free delete also clears a lingering upsell — the user took the free path it was pointing at.
    try { await rpc('deleteObject', item.ref); setUpsell(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  const confirmWhy = confirmItem == null ? ''
    : confirmItem.verdict === 'safe' ? 'Nothing in the model depends on it. It is re-checked at apply; if something now uses it, it is skipped, not deleted. You can undo this.'
    : confirmItem.verdict === 'usedByUnusedOnly' ? 'Only unused objects reference it; their formulas will error after this. You can undo this.'
    : 'Something references it that could not be checked offline. You can undo this.';

  return (
    <Panel>
      <div className="flex items-center gap-2 flex-wrap">
        <SectionTitle>Removal candidates <span style={{ color: 'var(--sem-muted)' }}>({unused.items.length})</span></SectionTitle>
        {scope && <ScopeBadge scope={scope} />}
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter…" spellCheck={false}
          className="ml-auto text-[12px] px-2 py-1 rounded-md outline-none w-56" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        {allSafe.length > 0 && (
          <button onClick={() => void removeAll()} disabled={busy}
            // The engine gate is >1 item, so a single-item sweep is free — only wear Pro messaging when the click is actually gated.
            title={tier === 'free' && allSafe.length > 1
              ? `Pro removes all ${allSafe.length} verified-safe items in one undoable step, re-checked at apply time. Each row's Delete stays free.`
              : `Removes ${allSafe.length === 1 ? 'the 1 verified-safe item' : `all ${allSafe.length} verified-safe items`} in one undoable step. Each is re-checked at apply time; anything no longer safe is skipped, not deleted.`}
            className="text-[12px] px-3 py-1 rounded-md font-medium shrink-0"
            style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: busy ? 0.6 : 1 }}>
            {busy ? 'Removing…' : `Remove all ${allSafe.length} safe to remove`}
            <ProBadge show={tier === 'free' && allSafe.length > 1} variant="onAccent" />
          </button>
        )}
      </div>

      {kinds.length > 1 && (
        <div className="mt-2 flex items-center gap-1 flex-wrap">
          <span className="text-[10px] mr-0.5" style={{ color: 'var(--sem-muted)' }}>Type</span>
          <FacetChip active={effKind === 'all'} onClick={() => setKind('all')} kind={null} count={textItems.length} />
          {kinds.map(([k, n]) => <FacetChip key={k} active={effKind === k} onClick={() => setKind(k)} kind={k} count={n} />)}
        </div>
      )}

      {stale && <div className="mt-2"><Banner color="var(--sem-warn)">The model changed since this analysis ran, so these verdicts may be out of date. Re-analyze the reports for an accurate view. Removals are re-checked at apply, so anything that gained a dependency is skipped, not deleted.</Banner></div>}
      {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
      {upsell && <div className="mt-2"><UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice></div>}
      {confirmSweep && (
        <div className="mt-2"><Banner color="var(--sem-warn)">
          <div>This removes {allSafe.length} item{allSafe.length === 1 ? '' : 's'} that nothing depends on. You can undo it.</div>
          <div className="mt-2 flex items-center gap-2">
            <button onClick={() => void removeAll()} disabled={busy} className="text-[11px] px-2 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busy ? 'Removing…' : 'Remove them'}</button>
            <button onClick={() => setConfirmSweep(false)} className="text-[11px] px-2 py-1 rounded-md" style={{ color: 'var(--sem-muted)' }}>Keep them</button>
          </div>
        </Banner></div>
      )}
      {confirmItem && (
        <div className="mt-2"><Banner color="var(--sem-warn)">
          <div>Delete '{confirmItem.name}'? {confirmWhy}</div>
          <div className="mt-2 flex items-center gap-2">
            <button onClick={() => void deleteOne(confirmItem)} className="text-[11px] px-2 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Delete</button>
            <button onClick={() => setConfirmItem(null)} className="text-[11px] px-2 py-1 rounded-md" style={{ color: 'var(--sem-muted)' }}>Keep it</button>
          </div>
        </Banner></div>
      )}
      {sweep && (
        <div className="mt-2">
          <Banner color={sweep.count > 0 ? 'var(--sem-good)' : 'var(--sem-warn)'}>
            <div>
              {sweep.count > 0
                // Only claim an undo when something was actually removed — a "Removed 0, you can undo it" reads as a lie.
                ? <>Removed {sweep.count} item{sweep.count === 1 ? '' : 's'} in one step. You can undo it.
                    {sweep.skipped.length > 0 && <> {sweep.skipped.length} not removed, each re-checked at apply time:</>}</>
                : <>Nothing was removed{sweep.skipped.length > 0 ? ':' : '.'}</>}
            </div>
            {sweep.skipped.length > 0 && (
              <div className="mt-1 flex flex-col gap-0.5 text-[11px]">
                {sweep.skipped.slice(0, 6).map((s) => (
                  <div key={s.ref} className="truncate" title={s.ref + ': ' + s.reason}>• {refLabel(s.ref).name}: {s.reason}</div>
                ))}
                {sweep.skipped.length > 6 && <div>…and {sweep.skipped.length - 6} more</div>}
              </div>
            )}
          </Banner>
        </div>
      )}

      {unused.items.length === 0 && <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-good)' }}>No model-orphaned measures or columns.</div>}

      {safe.length > 0 && (
        <div className="mt-3">
          <GroupLabel color="var(--sem-good)">Safe to remove ({safe.length})</GroupLabel>
          {safe.map((i) => <UnusedRow key={i.ref} item={i} onImpact={onImpact} onDelete={deleteOne} />)}
        </div>
      )}
      {dead.length > 0 && (
        <div className="mt-3">
          <GroupLabel color="var(--sem-warn)">Used only by an unused object ({dead.length})</GroupLabel>
          {dead.map((i) => <UnusedRow key={i.ref} item={i} onImpact={onImpact} onDelete={deleteOne} />)}
        </div>
      )}
      {caution.length > 0 && (
        <div className="mt-3">
          <GroupLabel color="var(--sem-bad)">Caution: referenced by an object we can't evaluate offline ({caution.length})</GroupLabel>
          {caution.map((i) => <UnusedRow key={i.ref} item={i} onImpact={onImpact} onDelete={deleteOne} />)}
        </div>
      )}
    </Panel>
  );
}

function UnusedRow({ item, onImpact, onDelete }: { item: UnusedItem; onImpact: (ref: string) => void; onDelete?: (item: UnusedItem) => void }) {
  return (
    <Row onClick={() => revealInTree(item.ref)}>
      <Glyph kind={item.kind} />
      <span className="truncate" style={{ opacity: item.isHidden ? 0.55 : 1 }}>{item.name}</span>
      <span className="text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 130 }}>{item.table}</span>
      {item.verdict !== 'safe' && item.blockedBy.length > 0 && (
        <span className="text-[10px] truncate" style={{ color: 'var(--sem-warn)', maxWidth: 220 }} title={'Referenced by: ' + item.blockedBy.join(', ')}>← {item.blockedBy.join(', ')}</span>
      )}
      <span className="ml-auto text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }}>{item.refCount} ref{item.refCount === 1 ? '' : 's'}</span>
      <button className="text-[10px] shrink-0 px-1.5 py-0.5 rounded" onClick={(e) => { e.stopPropagation(); onImpact(item.ref); }} title="Show impact" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>impact</button>
      {onDelete && (
        <button className="text-[10px] shrink-0 px-1.5 py-0.5 rounded ml-1" onClick={(e) => { e.stopPropagation(); void onDelete(item); }}
          title="Delete this object. You can undo it." style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-bad)' }}>delete</button>
      )}
    </Row>
  );
}

// A facet chip for the Removal-candidates type filter. Shares the graph view's kind vocabulary (KIND_GLYPH/KIND_COLOR/
// KIND_LABEL) so the two lineage surfaces read as one system; `kind=null` is the "All" chip. Shows the count under the
// current text search and highlights when it is the active scope. NOTE the interaction model differs from the graph's
// chips (single-select "show one" here vs the graph's multi-select "hide" toggles) - a focus gesture for a work list.
function FacetChip({ active, onClick, kind, count }: { active: boolean; onClick: () => void; kind: string | null; count: number }) {
  return (
    <button onClick={onClick} className="text-[11px] px-2 py-0.5 rounded-md font-medium inline-flex items-center gap-1 transition-[background-color] duration-100"
      style={{ background: active ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: active ? 'var(--sem-on-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {kind && <span style={{ color: active ? 'var(--sem-on-accent)' : (KIND_COLOR[kind] ?? 'var(--sem-muted)') }}>{KIND_GLYPH[kind] ?? '•'}</span>}
      {kind ? (KIND_LABEL[kind] ?? prettyKind(kind)) : 'All'} <span style={{ opacity: 0.65 }}>{count}</span>
    </button>
  );
}

// Labels the basis of the Removal-candidates verdicts: report-aware (a loaded report analysis excludes report-used
// fields from "safe") vs model-only (published-report usage is NOT considered — a report-only field can look unused).
function ScopeBadge({ scope }: { scope: 'model-only' | 'report-aware' }) {
  const reportAware = scope === 'report-aware';
  return (
    <span className="text-[10px] px-1.5 py-0.5 rounded shrink-0" title={reportAware
      ? 'Report usage is included: a field used only by an analyzed report is excluded from "safe".'
      : 'Model-only: published-report usage is NOT included. Analyze reports (Published reports tab) to include it.'}
      style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: reportAware ? 'var(--sem-good)' : 'var(--sem-muted)' }}>
      {reportAware ? 'Report-aware' : 'Model-only'}
    </span>
  );
}

// ---- Published-reports mode (cloud, Phase 3) --------------------------------------------------
// Discover the published reports in a Fabric/Power BI workspace (non-admin list, carries datasetId), then run the
// report-aware safe-to-remove over their cloud PBIR via getDefinition. LAZY (no call until a button) to avoid a
// surprise sign-in. getDefinition needs a WRITE-CAPABLE scope, so the Analyze button is gated behind explicit consent.
const PBIR_CAPABLE = (r: CloudReport) => (r.reportType ?? 'PowerBIReport') !== 'PaginatedReport';

// The live XMLA endpoint carries the workspace NAME, not its id (powerbi://api.powerbi.com/v1.0/myorg/<WorkspaceName>).
// Pull that trailing segment so, once workspaces are listed, we can pre-select the one whose DisplayName matches the
// model's own workspace — bridging name→id without asking. URL-decoded (a workspace name can contain spaces/%20).
// Anchored: only a TRAILING /myorg/<name> segment (optional trailing slash) is the workspace — a deeper path is not.
// Malformed percent-encoding returns null (no auto-select on a garbled hint), never the raw undecoded segment.
function workspaceNameFromEndpoint(endpoint?: string | null): string | null {
  if (!endpoint) return null;
  const m = /\/myorg\/([^/?#]+?)\/?$/i.exec(endpoint);
  if (!m) return null;
  try { return decodeURIComponent(m[1]); } catch { return null; }
}

// Merge the structured Browse picks with the free-text input's paths for analyze_reports. The picks stay a real
// string[] end to end — a legal Windows folder name can contain ';' (or edge whitespace), so a pick passes through
// EXACTLY as the OS dialog returned it; only the MANUAL free-text half uses the ';'-split + trim convention (its
// documented limitation). Order-preserving, deduped verbatim so a path both picked and typed is analyzed once.
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

// The open model's identity, as one comparable key. sessionId is the primary signal (a new open = a new session id,
// including two models in the SAME workspace, or local models where liveEndpoint is null for all of them); the
// endpoint + database ride along to catch a rebind that reuses a session. Any change = the discovery state on
// screen belongs to a different model and must be invalidated.
function sessionIdentityKey(s?: { sessionId?: string; liveEndpoint?: string; liveDatabase?: string } | null): string {
  return `${s?.sessionId ?? ''}|${s?.liveEndpoint ?? ''}|${s?.liveDatabase ?? ''}`;
}

// Decide the tenant field's next value when the open model's identity/tenant is observed. Ownership is explicit:
// only a value WE auto-filled ('auto') is ever replaced — or CLEARED when the new model has no known tenant (a
// stale prefill must not silently target the previous model's tenant). A user-typed value ('user') is theirs until
// they empty the field themselves (which returns ownership, so prefill works again).
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

function ReportsPane({ onImpact }: { onImpact: (ref: string) => void }) {
  const { session } = useConnection();   // the open model's live identity — used to default the workspace + tenant
  const {
    analysis, localReportPaths, reportsStale, beginAnalyze, applyAnalysis, abandonAnalyze, clearAnalysis,
    ws, setWs, tenant, setTenant, authMode, setAuthMode, workspaces, setWorkspaces,
    wsError, setWsError, manualWs, setManualWs, reports, setReports, loadedWs, setLoadedWs, sel, setSel,
    consent, setConsent, pbirPath, setPbirPath, pickedPaths, setPickedPaths,
    busy, setBusy, cloudProgress, setCloudProgress, error, setError, gates,
  } = useLineageTabState();
  const { cloudGen, localGen, activeCloudRunId, tenantOwner } = gates;
  // Generation tokens: every async CLOUD flow (list workspaces / list reports / cloud analyze) captures cloudGen at
  // start and commits its result only while still newest — so a slow old response (or one issued under a previous
  // auth/tenant/model identity) can never overwrite newer state or clobber a GUID typed mid-flight. Identity edits and
  // model switches bump the token to orphan whatever is in flight (and clear its busy honestly, since the orphaned
  // finally won't). localGen does the same for the offline analyze, which only a MODEL switch invalidates (auth/tenant
  // identity is irrelevant to a local file scan). Loaders bump on start too, so a second click orphans the first.
  const pbirCount = useMemo(() => (reports ?? []).filter(PBIR_CAPABLE).length, [reports]);
  const analyzeLabel = cloudProgress
    ? `Analyzing ${cloudProgress.done} of ${cloudProgress.total}${cloudProgress.note ? `: ${cloudProgress.note}` : ''}`
    : 'Analyzing…';
  // The model's own workspace name (from the live XMLA endpoint), used to pre-select it once workspaces list.
  const wsNameHint = useMemo(() => workspaceNameFromEndpoint(session?.liveEndpoint), [session?.liveEndpoint]);
  const clearCloudBusy = () => setBusy((b) => (b === 'workspaces' || b === 'load' || b === 'analyze' ? null : b));
  // Editing the workspace (or auth) after a Load must invalidate the on-screen reports — else Analyze would target a
  // DIFFERENT workspace with the previous one's report ids. Also drops a stale consent so each workspace is re-consented,
  // and orphans any in-flight cloud call (its commit is for the previous selection).
  function resetDiscovery() { cloudGen.current++; activeCloudRunId.current = null; clearCloudBusy(); setCloudProgress(null); setReports(null); setSel(new Set()); clearAnalysis(); setConsent(false); setError(null); }
  // Auth/tenant identity changed ⇒ any listed workspaces belong to the OLD identity. Drop them so the picker re-lists
  // for the new one rather than offering a wrong-tenant list. A MANUALLY typed GUID survives (the list is stale, the
  // GUID is not); a picker selection is cleared with the list it came from.
  function resetWorkspaces() { cloudGen.current++; activeCloudRunId.current = null; clearCloudBusy(); setCloudProgress(null); setWorkspaces(null); setWsError(null); if (!manualWs) setWs(''); }

  // List the workspaces for the current identity (a live Entra call — hence on demand, not on mount). On success,
  // auto-select the workspace whose DisplayName matches the model's own workspace name (case-insensitive exact) so
  // the common case ("analyze the model I'm editing") needs no typing. No match ⇒ leave unselected (never guess).
  async function loadWorkspaces() {
    const gen = ++cloudGen.current;
    setBusy('workspaces'); setWsError(null);
    try {
      const list = await rpc<FabricWorkspace[]>('listWorkspaces', authMode, tenant.trim() || null);
      if (gen !== cloudGen.current) return;   // identity/model/selection moved on — this list is for the old world
      const sorted = [...(list ?? [])].sort((a, b) => (a.displayName || '').localeCompare(b.displayName || ''));
      setWorkspaces(sorted);
      if (wsNameHint) {
        const match = sorted.find((w) => (w.displayName || '').toLowerCase() === wsNameHint.toLowerCase());
        // Functional read of the LATEST selection (never this closure's): a GUID typed mid-flight wins over the match.
        if (match) setWs((cur) => cur || match.id);
      }
    } catch (e) { if (gen === cloudGen.current) { setWsError(String((e as Error).message ?? e)); setWorkspaces([]); } }
    finally { if (gen === cloudGen.current) setBusy(null); }
  }

  async function loadReports() {
    const id = ws.trim();
    if (!id) { setError('Choose a workspace first.'); return; }
    const gen = ++cloudGen.current;
    setBusy('load'); setError(null); clearAnalysis(); setReports(null); setSel(new Set()); setConsent(false);
    try {
      const list = await rpc<CloudReport[]>('listReports', id, authMode, tenant.trim() || null);
      if (gen !== cloudGen.current) return;
      setReports(list); setLoadedWs(id);
    } catch (e) { if (gen === cloudGen.current) setError(String((e as Error).message ?? e)); }
    finally { if (gen === cloudGen.current) setBusy(null); }
  }
  async function analyze() {
    // Send the explicit PBIR id set so an empty selection ("analyze all PBIR") never pulls paginated/RDL reports, and
    // pass the live `consent` (not a literal) so the engine call is driven by the same flag the button is gated on.
    // Do NOT clear the current analysis first — that would drop the report-aware view mid-request; the freshness
    // token guards the new result in (and a model edit mid-flight orphans it).
    const ids = sel.size > 0 ? [...sel] : (reports ?? []).filter(PBIR_CAPABLE).map((r) => r.id);
    const gen = ++cloudGen.current;
    const token = beginAnalyze();
    const runId = crypto.randomUUID();
    activeCloudRunId.current = runId;
    setBusy('analyze'); setCloudProgress(null); setError(null);
    try {
      const res = await rpc<ReportAnalysisResult>('analyzeCloudReports', loadedWs, ids, consent, authMode, tenant.trim() || null, runId);
      if (gen !== cloudGen.current) return;
      applyAnalysis(res, null, token);
    } catch (e) { abandonAnalyze(token); if (gen === cloudGen.current) setError(String((e as Error).message ?? e)); }
    finally {
      if (gen === cloudGen.current) {
        if (activeCloudRunId.current === runId) activeCloudRunId.current = null;
        setBusy(null);
      }
    }
  }
  // Browse to report folder(s) via the host's native folder picker (multi-select). Picks land in the structured
  // chips list — never the free-text field (whose ';' convention would corrupt a path containing ';').
  async function browse() {
    if (busy) return;
    setBusy('browse');
    try {
      const picked = await pickReportPaths();
      if (picked && picked.length) setPickedPaths((cur) => mergeReportPaths([...cur, ...picked], ''));
    } catch (e) { setError(String((e as Error).message ?? e)); }   // the picker timed out/failed — say so, don't hang
    finally { setBusy((b) => (b === 'browse' ? null : b)); }
  }
  // Offline path: analyze local PBIR folder(s) (a .pbip's *.Report) — no sign-in. Feeds the SAME report layer.
  async function analyzeLocal() {
    const paths = mergeReportPaths(pickedPaths, pbirPath);
    if (!paths.length) { setError('Pick a local report folder (or enter a path) first.'); return; }
    const gen = ++localGen.current;
    const token = beginAnalyze(paths);
    setBusy('analyzeLocal'); setError(null);
    try {
      const res = await rpc<ReportAnalysisResult>('analyzeReports', paths);
      if (gen !== localGen.current) return;   // the model was switched mid-analysis — this snapshot is the old model's
      applyAnalysis(res, paths, token);
    } catch (e) { abandonAnalyze(token); if (gen === localGen.current) setError(String((e as Error).message ?? e)); }
    finally { if (gen === localGen.current) setBusy(null); }
  }
  // A delete/sweep from the pane below is refreshed by the PARENT holder: model/didChange re-runs a LOCAL analysis in
  // place and marks a CLOUD one stale (a soft re-analyze hint). No local re-run plumbing needed here.
  function toggle(id: string) { setSel((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; }); }

  return (
    <div className="flex flex-col gap-4">
      <Panel>
        <SectionTitle>Analyze local report files <span style={{ color: 'var(--sem-muted)' }}>(offline · no sign-in)</span></SectionTitle>
        <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
          Point at a local PBIR report folder (a <code>.pbip</code>'s <code>*.Report</code> folder). Merges the report
          layer (<b>field → visual → page → report</b>) into the Graph &amp; Tree, and makes report-only fields no
          longer “safe to remove”.
        </div>
        <div className="flex items-center gap-2 mt-3 flex-wrap">
          <input value={pbirPath} onChange={(e) => setPbirPath(e.target.value)} placeholder="C:\path\to\MyReport.Report   (or a .pbip file / project root; multiple: ; separated)" spellCheck={false}
            className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ minWidth: 380, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          <button onClick={() => void browse()} disabled={busy != null} title="Pick report folder(s) with the file browser"
            className="text-[12px] px-3 py-1 rounded-md" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)', opacity: busy ? 0.6 : 1 }}>
            {busy === 'browse' ? 'Opening…' : 'Browse…'}
          </button>
          <button onClick={() => void analyzeLocal()} disabled={busy != null}
            className="text-[12px] px-3 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: busy ? 0.6 : 1 }}>
            {busy === 'analyzeLocal' ? 'Analyzing…' : 'Analyze local PBIR'}
          </button>
        </div>
        {/* Browsed picks render as removable chips — visible, individually removable, and passed to analyze as a
            real string[] alongside whatever the free-text field holds. */}
        {pickedPaths.length > 0 && (
          <div className="flex items-center gap-1.5 mt-2 flex-wrap">
            {pickedPaths.map((p) => (
              <span key={p} className="inline-flex items-center gap-1.5 text-[11px] px-2 py-0.5 rounded-md" title={p}
                style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)', maxWidth: 420 }}>
                <span className="truncate">{p}</span>
                <button onClick={() => setPickedPaths((cur) => cur.filter((x) => x !== p))} aria-label={'Remove ' + p}
                  className="shrink-0 leading-none" style={{ color: 'var(--sem-muted)' }}>✕</button>
              </span>
            ))}
            <button onClick={() => setPickedPaths([])} className="text-[11px] underline" style={{ color: 'var(--sem-muted)' }}>clear all</button>
          </div>
        )}
      </Panel>

      <Panel>
        <SectionTitle>Discover published reports <span style={{ color: 'var(--sem-muted)' }}>(cloud workspace)</span></SectionTitle>
        <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
          List a workspace's reports (each carries the dataset it binds to), then check report usage against the open
          model. A field only a report displays is no longer flagged “safe to remove”.
        </div>
        {/* Identity: WHO signs in (az cli may be a different tenant than the model's own) + the tenant to target. */}
        <div className="flex items-center gap-2 mt-3 flex-wrap">
          {/* Identity/selection edits invalidate UNCONDITIONALLY (never `if (reports)`): while a load is in flight
              `reports` is null, and only the gen bump inside the resets orphans that request — a conditional reset
              would let it commit the PREVIOUS identity/workspace's reports under the new choice. */}
          <select value={authMode} onChange={(e) => { setAuthMode(e.target.value as AuthMode); resetWorkspaces(); resetDiscovery(); }}
            title="WHO signs in here; az cli may be logged into a different tenant than the model's own"
            className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            <option value="azcli">azcli</option>
            <option value="interactive">interactive</option>
            <option value="devicecode">devicecode</option>
            <option value="serviceprincipal">serviceprincipal</option>
          </select>
          {authMode !== 'azcli' && (
            <input value={tenant} onChange={(e) => {
                // Typing takes explicit ownership (the prefill machinery must never touch a user value); emptying
                // the field hands ownership back, so the next model observation may prefill again.
                tenantOwner.current = e.target.value.trim() === '' ? null : 'user';
                setTenant(e.target.value); resetWorkspaces(); resetDiscovery();
              }} placeholder="Tenant id / domain (optional)" spellCheck={false}
              className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ minWidth: 200, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          )}
          {!manualWs && (
            <button onClick={() => void loadWorkspaces()} disabled={busy != null} title="List the workspaces you can access, then pick one"
              className="text-[12px] px-3 py-1 rounded-md" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)', opacity: busy ? 0.6 : 1 }}>
              {busy === 'workspaces' ? 'Loading…' : workspaces ? 'Reload workspaces' : 'Load workspaces'}
            </button>
          )}
        </div>

        {/* Workspace selection: the picker (default) or a manual-id escape hatch for a workspace the list can't reach. */}
        <div className="flex items-center gap-2 mt-2 flex-wrap">
          {manualWs ? (
            <>
              <input value={ws} onChange={(e) => { setWs(e.target.value); resetDiscovery(); }} placeholder="Workspace (group) id…" spellCheck={false}
                className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ minWidth: 280, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
              <button onClick={() => {
                // Back to the picker: a typed GUID that IS in the loaded list (ids compare case-insensitively — a
                // GUID's casing is not identity) stays selected, normalized to the list's canonical id so the
                // <select> can render it; an unlisted one can't render in a select, so it clears with its
                // discovery state.
                setManualWs(false);
                const match = workspaces?.find((w) => w.id.toLowerCase() === ws.trim().toLowerCase());
                if (match) setWs(match.id);
                else { setWs(''); resetDiscovery(); }
              }} className="text-[11px] underline" style={{ color: 'var(--sem-muted)' }}>use workspace picker</button>
            </>
          ) : workspaces ? (
            <select value={ws} onChange={(e) => { setWs(e.target.value); resetDiscovery(); }} disabled={workspaces.length === 0}
              className="text-[12px] px-2 py-1 rounded-md outline-none" style={{ minWidth: 280, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
              <option value="">{workspaces.length === 0 ? 'No workspaces available' : 'Choose a workspace…'}</option>
              {workspaces.map((w) => <option key={w.id} value={w.id}>{w.displayName}</option>)}
            </select>
          ) : (
            <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Load workspaces to pick one, or</span>
          )}
          {/* Entering manual mode keeps the current id (the input opens pre-filled with it) — the selection did not
              change, so any loaded reports stay valid; editing the id afterwards resets discovery as usual. */}
          {!manualWs && <button onClick={() => setManualWs(true)} className="text-[11px] underline" style={{ color: 'var(--sem-muted)' }}>enter id manually</button>}
          <button onClick={() => void loadReports()} disabled={busy != null || !ws.trim()}
            className="text-[12px] px-3 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: (busy || !ws.trim()) ? 0.5 : 1 }}>
            {busy === 'load' ? 'Loading…' : 'Load reports'}
          </button>
        </div>
        {/* The selected workspace's id, shown dimly — the value behind the picker, and what a manual entry expects. */}
        {!manualWs && ws && <div className="text-[10px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>id {ws}</div>}
        {wsError && <div className="text-[11px] mt-2" style={{ color: 'var(--sem-bad)' }}>{wsError}</div>}
      </Panel>

      {error && <Banner color="var(--sem-bad)">{error}</Banner>}

      {reports && (
        <Panel>
          <div className="flex items-center gap-2">
            <SectionTitle>Reports <span style={{ color: 'var(--sem-muted)' }}>({reports.length} · {pbirCount} analyzable)</span></SectionTitle>
            <div className="ml-auto flex gap-1.5">
              <button className="text-[11px] px-2 py-0.5 rounded" onClick={() => setSel(new Set(reports.filter(PBIR_CAPABLE).map((r) => r.id)))}
                style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Select all PBIR</button>
              <button className="text-[11px] px-2 py-0.5 rounded" onClick={() => setSel(new Set())}
                style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Clear</button>
            </div>
          </div>
          {reports.length === 0 && <Empty>No reports in this workspace (or you lack access).</Empty>}
          <div className="mt-2 overflow-auto" style={{ maxHeight: 280 }}>
            {reports.map((r) => {
              const rdl = !PBIR_CAPABLE(r);
              return (
                <label key={r.id} className="flex items-center gap-2 px-2 py-1 rounded-md text-[12px]" style={{ opacity: rdl ? 0.5 : 1, cursor: rdl ? 'not-allowed' : 'pointer' }}>
                  <input type="checkbox" disabled={rdl} checked={sel.has(r.id)} onChange={() => toggle(r.id)} />
                  <span className="truncate">{r.name}</span>
                  {rdl && <span className="text-[9px] px-1 rounded shrink-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-warn)' }}>RDL</span>}
                  {r.datasetId && <span className="ml-auto text-[10px] truncate shrink-0" title={'dataset ' + r.datasetId} style={{ color: 'var(--sem-muted)', maxWidth: 180 }}>ds {r.datasetId.slice(0, 8)}…</span>}
                </label>
              );
            })}
          </div>

          <label className="flex items-start gap-2 mt-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            <input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} className="mt-0.5" />
            <span>Reading a report’s definition uses the Fabric <b>Get Report Definition</b> API, which requires a write-capable scope
              (<code>Item.ReadWrite.All</code> / <code>Report.ReadWrite.All</code>) + Contributor, even though Semanticus only <b>reads</b> it. I consent.</span>
          </label>
          <div className="flex items-center gap-2 mt-2">
            <button onClick={() => void analyze()} disabled={busy != null || !consent || pbirCount === 0}
              className="text-[12px] px-3 py-1 rounded-md font-medium" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: (busy || !consent || pbirCount === 0) ? 0.5 : 1 }}>
              {busy === 'analyze' ? analyzeLabel : sel.size > 0 ? `Analyze ${sel.size} selected` : 'Analyze all PBIR reports'}
            </button>
            {!consent && <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>consent required</span>}
          </div>
        </Panel>
      )}

      {analysis && (
        <>
          <Panel>
            <div className="flex items-center gap-2 flex-wrap">
              <SectionTitle>Report usage</SectionTitle>
              <Stat n={analysis.reportsRead} label="read" strong />
              {analysis.reportsUnreadable > 0 && <Stat n={analysis.reportsUnreadable} label="unreadable" />}
              <Stat n={analysis.modelFieldsUsed.length} label="model fields used" />
            </div>
            <div className="mt-2 overflow-auto" style={{ maxHeight: 360 }}>
              {analysis.reports.map((r, i) => <ReportUsageRow key={r.path + i} r={r} onImpact={onImpact} />)}
            </div>
          </Panel>
          {analysis.caveat && <Banner color="var(--sem-warn)">{analysis.caveat}</Banner>}
          <UnusedPane unused={analysis.unused} onImpact={onImpact}
            scope={analysis.reportsRead > 0 ? 'report-aware' : 'model-only'} stale={analysis.reportsRead > 0 && reportsStale}
            reportPaths={localReportPaths ?? undefined} />
        </>
      )}
    </div>
  );
}

// ---- Report usage row — expandable to the per-page/visual field drill -------------------------
// Parse an ObjectRefs ref ('measure:Sales/Total Sales') into a glyph + name + table for display, without needing
// the full model graph in this pane.
function refLabel(ref: string): { kind: string; name: string; table?: string } {
  const colon = ref.indexOf(':');
  const kind = colon > 0 ? ref.slice(0, colon) : 'unresolved';
  const rest = colon > 0 ? ref.slice(colon + 1) : ref;
  const slash = rest.lastIndexOf('/');
  return slash > 0 ? { kind, name: rest.slice(slash + 1), table: rest.slice(0, slash) } : { kind, name: rest };
}

function ReportUsageRow({ r, onImpact }: { r: ReportUsage; onImpact: (ref: string) => void }) {
  const [open, setOpen] = useState(false);
  const visuals = r.visuals ?? [];
  const canExpand = visuals.length > 0;
  // group visuals by page so the drill reads report → page → visual → field
  const byPage = useMemo(() => {
    const m = new Map<string, ReportVisualUsage[]>();
    for (const v of visuals) { const a = m.get(v.page); if (a) a.push(v); else m.set(v.page, [v]); }
    return [...m.entries()];
  }, [visuals]);

  return (
    <div className="border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 px-2 py-1 rounded-md text-[12px]" style={{ cursor: canExpand ? 'pointer' : 'default' }} onClick={() => canExpand && setOpen((o) => !o)}>
        {canExpand
          ? <span className="inline-block w-3 text-[10px]" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
          : <span className="inline-block w-3" />}
        <span className="shrink-0" style={{ color: r.read ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{r.read ? '✓' : '!'}</span>
        <span className="truncate">{r.name}</span>
        {r.error
          ? <span className="text-[10px] truncate" style={{ color: 'var(--sem-warn)', maxWidth: 320 }} title={r.error}>{r.error}</span>
          : <span className="ml-auto text-[10px] shrink-0" style={{ color: 'var(--sem-muted)' }}>
              {visuals.length > 0 ? `${visuals.length} visual${visuals.length === 1 ? '' : 's'} · ` : ''}{r.fieldCount} field{r.fieldCount === 1 ? '' : 's'}{r.unresolved > 0 ? ` · ${r.unresolved} unresolved` : ''}
            </span>}
      </div>

      {open && canExpand && (
        <div className="pl-6 pb-2 flex flex-col gap-1.5">
          {byPage.map(([page, vis]) => (
            <div key={page}>
              <div className="text-[10px] uppercase tracking-wide font-semibold mt-1" style={{ color: 'var(--sem-muted)' }}>▭ {page}</div>
              {vis.map((v, vi) => (
                <div key={(v.visual ?? 'page') + vi} className="ml-2 mt-1 rounded-md px-2 py-1" style={{ background: 'var(--sem-surface-2)' }}>
                  <div className="flex items-center gap-1.5 text-[11px]">
                    <span style={{ color: 'var(--sem-accent)' }}>{v.visual ? '▦' : '⛃'}</span>
                    <span className="font-medium">{v.visualType || (v.visual ? 'visual' : 'page filter')}</span>
                    {v.visual && <span style={{ color: 'var(--sem-muted)' }} className="truncate">{v.visual}</span>}
                    <span className="ml-auto text-[10px]" style={{ color: 'var(--sem-muted)' }}>{v.usedRefs.length} field{v.usedRefs.length === 1 ? '' : 's'}</span>
                  </div>
                  <div className="flex flex-wrap gap-1 mt-1">
                    {v.usedRefs.map((ref) => {
                      const l = refLabel(ref);
                      return (
                        <button key={ref} onClick={() => onImpact(ref)} title={`${l.table ? l.table + ' · ' : ''}impact of ${l.name}`}
                          className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-bg)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                          <span style={{ color: KIND_COLOR[l.kind] ?? 'var(--sem-muted)' }}>{KIND_GLYPH[l.kind] ?? '•'}</span>
                          {l.name}
                        </button>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ---- local primitives -------------------------------------------------------------------------
function Panel({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border p-4 ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color, border: `1px solid color-mix(in srgb,${color} 40%, transparent)` }}>{children}</div>;
}
function Seg({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  // Instant press feedback so switching sub-views never "feels like nothing happened" before the view swaps.
  return (
    <button onClick={onClick} className="text-[12px] px-3 py-1 rounded-md font-medium transition-[transform,filter,background-color] duration-100 active:scale-90 active:brightness-125 hover:brightness-110"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Row({ children, active, onClick, testId }: { children: React.ReactNode; active?: boolean; onClick?: () => void; testId?: string }) {
  return (
    <div onClick={onClick} data-testid={testId} className="flex items-center gap-2 px-2 py-1 rounded-md cursor-pointer text-[12px] hover:bg-[var(--sem-surface-2)]"
      style={active ? { background: 'var(--sem-accent-soft)' } : undefined}>
      {children}
    </div>
  );
}
function Glyph({ kind }: { kind: string }) {
  return <span className="shrink-0 text-[12px] w-4 text-center" style={{ color: KIND_COLOR[kind] ?? 'var(--sem-muted)' }} title={kind}>{KIND_GLYPH[kind] ?? '•'}</span>;
}
function ViaBadge({ via }: { via: string }) {
  if (!via || via === 'dax') return null;
  return <span className="text-[9px] px-1 rounded shrink-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-warn)' }}>{via}</span>;
}
function Stat({ n, label, strong }: { n: number; label: string; strong?: boolean }) {
  return (
    <span className="text-[11px] px-2 py-0.5 rounded-full tnum" style={{ background: 'var(--sem-surface-2)', color: strong ? 'var(--sem-fg)' : 'var(--sem-muted)', fontWeight: strong ? 600 : 400 }}>
      {n} {label}
    </span>
  );
}
function GroupLabel({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="text-[11px] font-semibold mb-1" style={{ color }}>{children}</div>;
}
function Empty({ children }: { children: React.ReactNode }) {
  return <div className="text-[12px] py-6 text-center" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
