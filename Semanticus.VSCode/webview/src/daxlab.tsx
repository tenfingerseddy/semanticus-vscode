import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import { usePersistedState, useResizable } from './hooks';
import { BenchBars } from './echart';
import { useConnection, ConnectBar } from './connection';
import { QueryStalenessChip } from './contextbar';
import { useClaudeReflection, ClaudeRanBanner, type ActivityEvent } from './activity';
import { ResultGrid } from './grid';
import { DaxEditor, useDaxModelContext, type DaxEditorHandle } from './daxeditor';
import { FieldsPanel } from './fieldspanel';
import { ObjectBrowser, type BrowserNode } from './objectbrowser';
import type { ResultSet } from './wire';
import { queryIdentity, rowAnnouncement, timingAnnouncement } from './wire';
import { FieldWells, PivotConfig, EMPTY_CONFIG, buildQuery, buildInstrumentedQuery, seedConfig, filterToDax } from './daxwells';
import { DaxVisual, VizSwitcher, explainPayloadFromRow, type VizType } from './daxvisual';
import { ExplainPanel, type ExplainPayload } from './explainpanel';
import { plainDaxError } from './daxErrorText';
import { isSignInError, SIGN_IN_AGAIN } from './authcopy';

// DAX Lab — Layout B. A left Fields rail feeds everything by drag; the centre is a Power-BI-style VISUAL (field
// wells → chart/matrix, with filter context on hover) OR the QUERY editor — both drive one shared query. A
// full-width bottom WORKBENCH holds Result · Performance · Plan · Debug · Verify · DAX, so the heavyweight tools
// get real space. The visual auto-runs on every well change; Run/Debug live in one split button.

// wire types (camelCase, from Semanticus.Engine)
interface EvalLogEntry { label?: string; expression?: string; rowCount: number; columns: string[]; rows: unknown[][]; raw?: string; }
interface EvalLogResult { traceAvailable: boolean; entries: EvalLogEntry[]; resultColumns: { name: string; type?: string }[]; resultRows: unknown[][]; resultRowCount: number; elapsedMs: number; note?: string; error?: string; }
interface BenchmarkResult { runs: number; firstMs: number; warmMinMs: number; warmMedianMs: number; runsMs: number[]; rowCount: number; note?: string; error?: string; }
interface ScanEvent { line?: number; kind: string; subclass?: string; durationMs: number; cpuMs: number; rows: number; query?: string; }
interface ServerTimings { totalMs: number; wallMs?: number; setupMs?: number; feMs: number; seMs: number; seCpuMs: number; seQueries: number; seCacheHits?: number; seParallelism: number; rowCount: number; scans: ScanEvent[]; traceAvailable: boolean; note?: string; error?: string; }
interface EquivalenceMismatch { context: string; valueA: string; valueB: string; }
interface EquivalenceResult { allMatch: boolean; rowsCompared: number; mismatchCount: number; truncated?: boolean; fidelity?: string | null; mismatches: EquivalenceMismatch[]; query?: string; error?: string; }

// Equivalence EVIDENCE with its provenance stamped at receipt time: the verdict is computed ONCE from the
// grid the comparison actually RAN with (not whatever grid is currently configured); the request key
// identifies the exact request and the context key the EXECUTION CONTEXT (query connection identity +
// editing session/model revision), so an expression/grid/filter edit, a connection swap to a different
// model, or another door editing the model (didChange bumps the session revision, which the
// ConnectionProvider refreshes into these signals) all mark the evidence STALE instead of leaving a green
// verdict up against a world it no longer describes. Reflected MCP results carry no REQUEST provenance
// (gridCount/requestKey null) but do get the context stamp — they executed on this same engine.
interface EqEvidence { result: EquivalenceResult; gridCount: number | null; requestKey: string | null; contextKey: string | null; }

// The effective grid: trim + drop blanks (matches the engine's NormalizeGroupBy).
function normGrid(grid: string[]): string[] { return (grid ?? []).map((g) => (g ?? '').trim()).filter(Boolean); }

const VERIFY_MAX_ROWS = 100000;

// Request identity for staleness checks: the EXACT serialized request (collision-free by construction).
function requestKeyOf(exprA: string, exprB: string, grid: string[], filters: string[], maxRows: number): string {
  return JSON.stringify([exprA, exprB, grid, filters, maxRows]);
}

// Mirrors the engine's ONE evidence ladder (DaxBench.ClassifyEquivalenceEvidence): fidelity before mismatch
// (under a degraded comparison the surrogate itself can cause divergence — an observation, not a conviction),
// degraded above thin. Only 'proven' may render the green apply affordance; 'failed' is the red conviction;
// everything else is amber "not verified". 'noContext' = a clean match WITHOUT request provenance (a reflected
// MCP result): proven-vs-thin is unknowable, so it renders unproven — never green.
type EqVerdict = 'proven' | 'failed' | 'degraded_mismatch' | 'degraded' | 'thin' | 'unverified' | 'noContext';
function eqVerdict(ev: EqEvidence): EqVerdict {
  const eq = ev.result;
  if (eq.error) return 'unverified';
  if (!eq.allMatch && eq.fidelity) return 'degraded_mismatch';
  if (!eq.allMatch) return 'failed';                 // grid-independent: a mismatch is classifiable without provenance
  if ((eq.rowsCompared ?? 0) <= 0) return 'unverified';
  if (eq.truncated) return 'unverified';
  if (eq.fidelity) return 'degraded';
  if (ev.gridCount == null) return 'noContext';      // clean match, no provenance — cannot claim per-context proof
  if (ev.gridCount === 0) return 'thin';
  return 'proven';
}
interface ClearCacheResult { cleared: boolean; local: boolean; dataSource?: string; database?: string; elapsedMs: number; note?: string; error?: string; }
interface ColdWarmStats { n: number; avgMs: number; stdDevMs: number; minMs: number; maxMs: number; runsMs: number[]; }
interface ColdWarmRun { index: number; cold: boolean; totalMs: number; seMs: number; seQueries: number; }
interface ColdWarmBenchmark { runs: number; cacheClearAvailable: boolean; traceAvailable: boolean; coldTotal: ColdWarmStats; warmTotal: ColdWarmStats; coldSe: ColdWarmStats; warmSe: ColdWarmStats; detail: ColdWarmRun[]; rowCount: number; note?: string; error?: string; }
interface QueryPlanNode { level: number; operator: string; detail: string; records?: number; }
interface QueryPlanResult { traceAvailable: boolean; logicalPlan?: string; physicalPlan?: string; logicalTree: QueryPlanNode[]; physicalTree: QueryPlanNode[]; totalMs: number; rowCount: number; note?: string; error?: string; }

type Mode = 'visual' | 'query';
type WbTab = 'result' | 'perf' | 'plan' | 'debug' | 'verify';
type DaxLabBusy = 'run' | 'debug' | 'profile' | 'plan' | 'quick' | 'coldwarm' | 'clear' | 'verify' | null;
const WB_TABS: { id: WbTab; label: string }[] = [
  { id: 'result', label: 'Result' }, { id: 'perf', label: 'Performance' }, { id: 'plan', label: 'Plan' },
  { id: 'debug', label: 'Debug' }, { id: 'verify', label: 'Verify' },
];

interface DaxLabTabState {
  tab: WbTab;
  setTab: React.Dispatch<React.SetStateAction<WbTab>>;
  res: ResultSet | null;
  setRes: React.Dispatch<React.SetStateAction<ResultSet | null>>;
  resQuery: string | null;
  setResQuery: React.Dispatch<React.SetStateAction<string | null>>;
  logs: EvalLogEntry[] | null;
  setLogs: React.Dispatch<React.SetStateAction<EvalLogEntry[] | null>>;
  logNote: string | null;
  setLogNote: React.Dispatch<React.SetStateAction<string | null>>;
  bench: BenchmarkResult | null;
  setBench: React.Dispatch<React.SetStateAction<BenchmarkResult | null>>;
  cw: ColdWarmBenchmark | null;
  setCw: React.Dispatch<React.SetStateAction<ColdWarmBenchmark | null>>;
  plan: QueryPlanResult | null;
  setPlan: React.Dispatch<React.SetStateAction<QueryPlanResult | null>>;
  timings: ServerTimings | null;
  setTimings: React.Dispatch<React.SetStateAction<ServerTimings | null>>;
  clearMsg: string | null;
  setClearMsg: React.Dispatch<React.SetStateAction<string | null>>;
  eqEv: EqEvidence | null;
  setEqEv: React.Dispatch<React.SetStateAction<EqEvidence | null>>;
  vErr: string | null;
  setVErr: React.Dispatch<React.SetStateAction<string | null>>;
  busy: DaxLabBusy;
  setBusy: React.Dispatch<React.SetStateAction<DaxLabBusy>>;
  err: string | null;
  setErr: React.Dispatch<React.SetStateAction<string | null>>;
  claudeEvent: ActivityEvent | null;
  setClaudeEvent: React.Dispatch<React.SetStateAction<ActivityEvent | null>>;
  execContextKey: string;
}

const DaxLabTabStateContext = createContext<DaxLabTabState | null>(null);

function useDaxLabTabState(): DaxLabTabState {
  const state = useContext(DaxLabTabStateContext);
  if (!state) throw new Error('DaxLabView must be rendered inside DaxLabTabStateProvider');
  return state;
}

// Null-tolerant selector for the Studio tab bar: true while any DAX Lab op (human or reflected agent run) is in
// flight. Read outside DaxLabView so the tab bar can flag work that finished — or is still running — while hidden.
export function useDaxLabBusy(): boolean {
  return useContext(DaxLabTabStateContext)?.busy != null;
}

// Mounted by App above the conditional Studio tab body (and inside its model-session key). Human-started RPCs and
// reflected agent runs can therefore finish while DAX Lab is hidden, while a different model still drops the entire
// result set exactly as the old component remount did.
export function DaxLabTabStateProvider({ children }: { children: React.ReactNode }) {
  const { conn, session } = useConnection();
  const [tab, setTab] = usePersistedState<WbTab>('lab.wbtab', 'result');
  const [res, setRes] = useState<ResultSet | null>(null);
  const [resQuery, setResQuery] = useState<string | null>(null);
  const [logs, setLogs] = useState<EvalLogEntry[] | null>(null);
  const [logNote, setLogNote] = useState<string | null>(null);
  const [bench, setBench] = useState<BenchmarkResult | null>(null);
  const [cw, setCw] = useState<ColdWarmBenchmark | null>(null);
  const [plan, setPlan] = useState<QueryPlanResult | null>(null);
  const [timings, setTimings] = useState<ServerTimings | null>(null);
  const [clearMsg, setClearMsg] = useState<string | null>(null);
  const [eqEv, setEqEv] = useState<EqEvidence | null>(null);
  const [vErr, setVErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<DaxLabBusy>(null);
  const [err, setErr] = useState<string | null>(null);
  const [claudeEvent, setClaudeEvent] = useState<ActivityEvent | null>(null);

  // Keep the evidence context byte-for-byte equivalent to the old in-view calculation. Generic results retain the
  // existing QueryStalenessChip behavior; equivalence evidence continues to compare its receipt stamp to this key.
  const execContextKey = JSON.stringify([
    conn?.connected ?? null, conn?.kind ?? null, conn?.dataSource ?? null,
    conn?.database ?? null, conn?.connectionId ?? null, conn?.account ?? null,
    session?.currentTenant ?? null,
    session?.sessionId ?? null, session?.revision ?? null,
  ]);

  // Reflections must share the holder's lifetime too; otherwise an agent result emitted while another tab is visible
  // would still disappear even though human-started promises now survive.
  useClaudeReflection('run_dax', (e) => {
    const r = (e.result as ResultSet) ?? null;
    setRes(r); setResQuery(r?.query ?? e.query ?? null); setLogs(null); setErr(e.error ?? null); setClaudeEvent(e);
  });
  useClaudeReflection('evaluate_and_log', (e) => {
    const r = e.result as EvalLogResult | undefined; const rows = r?.resultRows ?? [];
    setRes(r ? { columns: r.resultColumns, rows, rowCount: r.resultRowCount, truncated: (r.resultRowCount ?? rows.length) > rows.length, elapsedMs: r.elapsedMs, query: e.query } : null);
    setResQuery(e.query ?? null);
    setLogs(r?.entries ?? null); setLogNote(r?.note ?? null); setErr(e.error ?? null); setClaudeEvent(e); setTab('debug');
  });
  useClaudeReflection('profile_dax', (e) => { if (e.result) { setTimings(e.result as ServerTimings); setClaudeEvent(e); setTab('perf'); } });
  useClaudeReflection('benchmark_dax', (e) => { if (e.result) { setBench(e.result as BenchmarkResult); setClaudeEvent(e); setTab('perf'); } });
  useClaudeReflection('benchmark_coldwarm', (e) => { if (e.result) { setCw(e.result as ColdWarmBenchmark); setClaudeEvent(e); setTab('perf'); } });
  useClaudeReflection('capture_query_plan', (e) => { if (e.result) { setPlan(e.result as QueryPlanResult); setClaudeEvent(e); setTab('plan'); } });
  useClaudeReflection('clear_cache', (e) => { const r = e.result as ClearCacheResult | undefined; if (r) { setClearMsg(r.cleared ? `Cleared the cache (${r.elapsedMs} ms)` : (r.error ?? 'Clear cache failed')); setClaudeEvent(e); } });
  useClaudeReflection('verify_equivalence', (e) => { if (e.result) { setEqEv({ result: e.result as EquivalenceResult, gridCount: null, requestKey: null, contextKey: execContextKey }); setVErr(e.error ?? null); setClaudeEvent(e); setTab('verify'); } });

  return (
    <DaxLabTabStateContext.Provider value={{
      tab, setTab, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
      plan, setPlan, timings, setTimings, clearMsg, setClearMsg, eqEv, setEqEv, vErr, setVErr,
      busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey,
    }}>
      {children}
    </DaxLabTabStateContext.Provider>
  );
}

export function DaxLabView() {
  const { conn, session, connectXmla, busy: connBusy } = useConnection();
  const model = useDaxModelContext();
  const editorRef = useRef<DaxEditorHandle>(null);
  const runGen = useRef(0);

  const persistKey = session?.sessionId ?? 'no-model';
  const [mode, setMode] = usePersistedState<Mode>('lab.mode.' + persistKey, 'visual');
  const [query, setQuery] = usePersistedState('lab.bq.' + persistKey, "EVALUATE\n    TOPN(1000, SUMMARIZECOLUMNS('Date'[Date], \"v\", [Total Sales]))");
  const [config, setConfig] = usePersistedState<PivotConfig>('lab.config.' + persistKey, EMPTY_CONFIG);
  const wellsTouched = useRef(false);
  function onWells(next: PivotConfig) { wellsTouched.current = true; setConfig(next); }
  const [viz, setViz] = usePersistedState<VizType>('lab.viz', 'bar');
  const {
    tab, setTab, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
    plan, setPlan, timings, setTimings, clearMsg, setClearMsg, eqEv, setEqEv, vErr, setVErr,
    busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey,
  } = useDaxLabTabState();
  const [railW, onRailDrag] = useResizable('lab.railW', 212, { axis: 'x', min: 160, max: 380 });
  const [wbH, onWbDrag, setWbH] = useResizable('lab.wbH', 250, { axis: 'y', dir: -1, min: 130, max: 620 });

  const [runs, setRuns] = usePersistedState('lab.runs', 5);
  const [clearOnRun, setClearOnRun] = usePersistedState('lab.clearOnRun', true);
  const [confirmShared, setConfirmShared] = useState(false);

  // verify (exprA seeds from the Values measure; the group-by + filters come from the visual's own matrix)
  const [exprA, setExprA] = usePersistedState('lab.exprA', '');
  const [exprB, setExprB] = usePersistedState('lab.exprB', '');
  const [customVerify, setCustomVerify] = usePersistedState('lab.verify.custom.' + persistKey, false);
  const [verifyFields, setVerifyFields] = usePersistedState<string[]>('lab.verify.fields.' + persistKey, []);
  // "Explain this number" (feature #2): right-click a value cell → the engine's explain_value dossier.
  const [explain, setExplain] = useState<ExplainPayload | null>(null);

  const shared = conn?.connected && conn.kind !== 'local';
  const idle = busy !== null;
  const currentQuery = () => (mode === 'visual' ? buildQuery(config) : query);
  const groupByDerived = useMemo(() => customVerify ? verifyFields : [...config.rows, ...config.cols].map((f) => f.ref), [config, customVerify, verifyFields]);
  const setVerifyGroupBy = (fields: string[]) => { setVerifyFields(fields); setCustomVerify(true); };
  const filterLinesDerived = useMemo(() => config.filters.map(filterToDax).filter((x): x is string => !!x), [config]);
  // The execution context evidence is valid FOR: the QUERY TARGET identity (the attached database +
  // registry connection id + authenticated account + tenant — NOT the editing-origin liveDatabase, which
  // stays constant when you switch database A→B on the same endpoint or re-authenticate the same target
  // under a different account/RLS context) + the editing session/model revision. Every field rides RPC
  // payloads the ConnectionProvider re-reads on connection changes AND model/didChange; a payload that
  // omits them (disconnect, a failed refresh nulling conn) still shifts the key — fail stale, not fresh.
  // (No DAX tab — the generated query is visible/editable in Query mode.) Heal a stale persisted tab id.
  useEffect(() => { if (!WB_TABS.some((t) => t.id === tab)) setTab('result'); }, [tab]);

  // Seed RUNNABLE defaults from the live model the first time (never the dead Contoso examples).
  const seeded = useRef(false);
  useEffect(() => {
    if (seeded.current) return;
    const empty = !config.rows.length && !config.cols.length && !config.values.length && !config.filters.length;
    if (empty && (model.measures.length || model.columns.length)) { setConfig(seedConfig(model)); seeded.current = true; }
    else if (!empty || (!model.measures.length && !model.columns.length && (config.values.length || config.rows.length))) seeded.current = true;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model]);
  // keep exprA pointed at the Values measure unless the user has typed their own
  useEffect(() => { if (!exprA && config.values[0]) setExprA(config.values[0].ref); /* eslint-disable-next-line */ }, [config.values]);

  function reveal() { if (wbH < 200) setWbH(280); }

  async function runQuery(q: string) {
    const gen = ++runGen.current;
    setBusy('run'); setErr(null); setLogs(null); setClaudeEvent(null);
    try {
      const r = await rpc<ResultSet>('runDax', q, 50000);
      if (gen !== runGen.current) return;
      if (r.cancelled) { setErr(r.error || 'The query was stopped.'); return; }
      if (r.error) { setErr(r.error); setRes(null); setResQuery(null); }
      else { setRes({ ...r, query: r.query || q }); setResQuery(r.query || q); }
    } catch (e) {
      if (gen !== runGen.current) return;
      setErr(String((e as Error).message ?? e));
    } finally { if (gen === runGen.current) setBusy(null); }
  }
  function stopQuery() {
    runGen.current++;
    void rpc('cancelDax').catch(() => { /* the in-flight run still settles */ });
    setBusy(null);
    setErr('The query was stopped.');
  }
  function run() { setTab('result'); reveal(); void runQuery(currentQuery()); }
  async function debug(q?: string) {
    const gen = ++runGen.current;
    const ran = q ?? (mode === 'visual' ? buildInstrumentedQuery(config) : currentQuery());
    setTab('debug'); reveal(); setBusy('debug'); setErr(null); setClaudeEvent(null);
    try {
      const r = await rpc<EvalLogResult>('evaluateAndLog', ran, 10000);
      if (gen !== runGen.current) return;
      if (r.error) { setErr(r.error); setRes(null); setLogs(null); setResQuery(null); }
      else { setRes({ columns: r.resultColumns, rows: r.resultRows, rowCount: r.resultRowCount, truncated: false, elapsedMs: r.elapsedMs, query: ran }); setResQuery(ran); setLogs(r.entries); setLogNote(r.note ?? null); }
    } catch (e) {
      if (gen !== runGen.current) return;
      setErr(String((e as Error).message ?? e));
    } finally { if (gen === runGen.current) setBusy(null); }
  }
  async function call<T>(method: string, set: (v: T | null) => void, b: typeof busy, ...args: unknown[]) {
    setBusy(b); setErr(null); setClaudeEvent(null);
    try { const r = await rpc<T & { error?: string }>(method, ...args); if (r && (r as { error?: string }).error) { setErr((r as { error?: string }).error!); set(null); } else set(r); }
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(null); }
  }
  const runProfile = () => { setTab('perf'); reveal(); void call<ServerTimings>('profileDax', setTimings, 'profile', currentQuery()); };
  const runPlan = () => { setTab('plan'); reveal(); void call<QueryPlanResult>('captureQueryPlan', setPlan, 'plan', currentQuery()); };
  const runQuick = () => { setCw(null); void call<BenchmarkResult>('benchmarkDax', setBench, 'quick', currentQuery(), runs); };
  const runColdWarm = () => { setBench(null); void call<ColdWarmBenchmark>('benchmarkColdWarm', setCw, 'coldwarm', currentQuery(), runs, clearOnRun, confirmShared); };
  async function clearCache() {
    setBusy('clear'); setErr(null); setClearMsg(null);
    try { const r = await rpc<ClearCacheResult>('clearCache', confirmShared); setClearMsg(r.cleared ? `Cleared the cache${r.database ? ` for "${r.database}"` : ''} (${r.elapsedMs} ms)` : (r.error ?? 'Clear cache failed')); }
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(null); }
  }
  // Cold plan capture in one click: clear the SE cache, then capture (a warm/cached query emits no plan).
  async function clearThenPlan() { setTab('plan'); reveal(); await clearCache(); await call<QueryPlanResult>('captureQueryPlan', setPlan, 'plan', currentQuery()); }
  async function runVerify() {
    setBusy('verify'); setVErr(null); setClaudeEvent(null);
    // Stamp provenance from the values the request is actually MADE with (captured before the await).
    const requestKey = requestKeyOf(exprA, exprB, groupByDerived, filterLinesDerived, VERIFY_MAX_ROWS);
    const contextKey = execContextKey;
    try {
      const r = await rpc<EquivalenceResult>('verifyEquivalence', exprA, exprB, groupByDerived, filterLinesDerived, VERIFY_MAX_ROWS);
      if (r.error) {
        // An execution error under degraded evaluation carries the caveat too: the failure happened under the
        // surrogate, which may itself be the cause.
        setVErr(r.error + (r.fidelity ? ` (ran under degraded evaluation: ${r.fidelity})` : ''));
        setEqEv(null);
      } else {
        // Stamp the evidence with ITS OWN provenance at receipt: the effective grid this comparison ran with,
        // the exact request key, and the execution context. The verdict comes from this stamp, never from the
        // currently-configured grid or whatever is connected later.
        setEqEv({ result: r, gridCount: normGrid(groupByDerived).length, requestKey, contextKey });
      }
    }
    catch (e) { setVErr(String((e as Error).message ?? e)); } finally { setBusy(null); }
  }
  // Evidence is only as good as the request AND the world it answered: an expression/grid/filter edit, a
  // connection/model swap, or a model revision bump (another door's edit) all invalidate it.
  const eqStale = !!eqEv && (
    (eqEv.requestKey != null && eqEv.requestKey !== requestKeyOf(exprA, exprB, groupByDerived, filterLinesDerived, VERIFY_MAX_ROWS))
    || (eqEv.contextKey != null && eqEv.contextKey !== execContextKey)
  );

  // Visual mode auto-runs on every well change the user makes (like a real Power BI visual). Opening the tab,
  // switching models, or seeding defaults must not fire an unrequested query against the live endpoint.
  useEffect(() => {
    if (mode !== 'visual' || !conn?.connected || !wellsTouched.current) return;
    const q = buildQuery(config);
    const t = setTimeout(() => { void runQuery(q); }, 220);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, mode, conn?.connected]);

  function switchMode(m: Mode) { if (m === 'query' && mode === 'visual') setQuery(buildQuery(config)); setMode(m); }
  function compareValue(ref: string) { setExprA(ref); setTab('verify'); reveal(); }
  // Reset the wells + editor back to the model's seeded defaults (the runnable starting point).
  function resetDefault() { wellsTouched.current = true; const c = seedConfig(model); setConfig(c); setQuery(buildQuery(c)); setExprA(c.values[0]?.ref ?? ''); setExprB(''); }

  return (
    // Ctrl+Enter (Cmd+Enter on mac) anywhere in the lab = Run — the universal "run the query" gesture. Bubble-phase,
    // so a CodeMirror keymap that claims the key first still wins; preventDefault stops the editor inserting a line.
    <div className="h-full flex flex-col min-h-0" style={{ background: 'var(--sem-bg)' }}
      onKeyDown={(e) => {
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key === 'Enter' && conn?.connected && !idle) {
          e.preventDefault(); e.stopPropagation(); run();
        }
      }}>
      {/* top bar */}
      <div className="flex items-center gap-3 px-3 shrink-0" style={{ height: 46, borderBottom: '1px solid var(--sem-border)', background: 'color-mix(in srgb,var(--sem-bg) 88%, #fff 2%)' }}>
        <span className="font-semibold text-[14px]">DAX Lab</span>
        <Seg value={mode} onChange={switchMode} options={[['visual', 'Visual'], ['query', 'Query']]} />
        <div className="ml-auto flex items-center gap-2">
          <button onClick={resetDefault} disabled={idle} title="Reset the selected fields and query to their starting values" className="text-[12px] px-3 py-1.5 rounded-lg" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', cursor: idle ? 'default' : 'pointer', opacity: idle ? 0.5 : 1 }}>Reset</button>
          {idle
            ? <button onClick={stopQuery} title="Stop the running query" aria-label="Stop the running query" className="text-[12.5px] font-semibold px-5 py-1.5 rounded-lg" style={{ background: 'var(--sem-bad)', color: 'var(--sem-on-accent)', border: 'none', cursor: 'pointer' }}>Stop</button>
            : <button disabled={!conn?.connected} onClick={run} title="Run the query" aria-label="Run the query" className="text-[12.5px] font-semibold px-5 py-1.5 rounded-lg" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', border: 'none', cursor: 'pointer', opacity: conn?.connected ? 1 : 0.5 }}>Run</button>}
        </div>
      </div>
      <div className="px-3 py-1.5 shrink-0" style={{ borderBottom: '1px solid var(--sem-border)' }}><ConnectBar hint="Try formulas against a live model, measure their speed and compare their answers." /></div>

      {/* Results run against the published model, not your staged edits — say so when they diverge. */}
      <QueryStalenessChip className="px-3 pt-2 shrink-0" />

      {claudeEvent && <div className="px-3 pt-2 shrink-0"><ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} /></div>}

      {/* body: rail | main */}
      <div className="flex-1 min-h-0 grid" style={{ gridTemplateColumns: `${railW}px 1fr` }}>
        <div className="relative min-h-0" style={{ borderRight: '1px solid var(--sem-border)' }}>
          <div className="h-full p-2"><FieldsPanel model={model} onInsert={(t) => editorRef.current?.insert(t)} /></div>
          <div onPointerDown={onRailDrag} title="Drag to resize" className="absolute top-0" style={{ right: -3, width: 6, height: '100%', cursor: 'col-resize', zIndex: 5 }} />
        </div>

        <div className="flex flex-col min-w-0 min-h-0">
          {/* top zone */}
          <div className="flex-1 min-h-0 overflow-auto flex flex-col">
            {mode === 'visual' ? (
              <>
                <FieldWells config={config} onConfig={onWells} onCompare={compareValue} />
                <div className="flex items-center gap-2 px-3 py-2" style={{ borderTop: '1px solid var(--sem-border)', borderBottom: '1px solid var(--sem-border)' }}>
                  <VizSwitcher viz={viz} onViz={setViz} />
                  <span className="ml-auto text-[11.5px] tnum" style={{ color: 'var(--sem-muted)' }}>
                    {res ? `${rowAnnouncement(res.rowCount, res.truncated)} · ${timingAnnouncement(res.elapsedMs)} · hover a point for its filter context · right-click a value to explain it` : 'build a visual from the wells above'}
                  </span>
                </div>
                <div className="p-3" style={{ height: 380, flex: 'none' }}>
                  {res ? <DaxVisual res={res} config={config} viz={viz} height={356} onExplain={setExplain} />
                    : <div className="h-full grid place-items-center text-[12px]" style={{ color: 'var(--sem-muted)' }}>{conn?.connected ? 'Drag a measure into Values to build a visual.' : 'Choose a test model above, then add fields to Rows, Columns or Values.'}</div>}
                </div>
              </>
            ) : (
              <div className="p-3 flex-1 min-h-0 flex flex-col">
                <div className="text-[11px] mb-2" style={{ color: 'var(--sem-muted)' }}>Query · autocomplete as you type · click or drag a field from the rail to insert · paste long queries from Desktop here.</div>
                <div className="flex-1 min-h-0"><DaxEditor ref={editorRef} value={query} onChange={setQuery} model={model} minHeight={360} placeholder="EVALUATE TOPN(100, 'Sales')   ·   Ctrl+Space for suggestions" /></div>
              </div>
            )}
          </div>

          {/* splitter */}
          <div onPointerDown={onWbDrag} onDoubleClick={() => setWbH(wbH > 380 ? 250 : 440)} title="Drag to resize · double-click to expand"
            className="shrink-0 relative" style={{ height: 8, cursor: 'row-resize', background: 'var(--sem-surface-2)', borderTop: '1px solid var(--sem-border)', borderBottom: '1px solid var(--sem-border)' }}>
            <div style={{ position: 'absolute', left: '50%', top: '50%', transform: 'translate(-50%,-50%)', width: 42, height: 3, borderRadius: 2, background: 'var(--sem-border-strong, var(--sem-border))' }} />
          </div>

          {/* workbench */}
          <div className="shrink-0 flex flex-col min-h-0" style={{ height: wbH }}>
            <div className="flex items-center px-2 shrink-0" style={{ borderBottom: '1px solid var(--sem-border)', background: 'var(--sem-surface-2)' }}>
              {WB_TABS.map((t) => {
                // The verify badge follows the STAMPED evidence: only a fresh 'proven' is green 'match', only a
                // fresh 'failed' is red 'diff' — stale evidence and every degraded/thin/unproven state show amber.
                const eqV = eqEv ? eqVerdict(eqEv) : null;
                const badge = t.id === 'result' && res ? (res.truncated ? `${res.rowCount}+` : String(res.rowCount))
                  : t.id === 'verify' && eqV ? (eqStale ? 'stale' : eqV === 'proven' ? 'match' : eqV === 'failed' ? 'diff' : 'unproven') : null;
                const badgeColor = eqStale ? 'var(--sem-warn)' : eqV === 'proven' ? 'var(--sem-good)' : eqV === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)';
                return (
                  <button key={t.id} onClick={() => setTab(t.id)} className="text-[12px] px-3 py-2 flex items-center gap-1.5 whitespace-nowrap" style={{ color: tab === t.id ? 'var(--sem-fg)' : 'var(--sem-muted)', fontWeight: tab === t.id ? 600 : 400, borderBottom: `2px solid ${tab === t.id ? 'var(--sem-accent)' : 'transparent'}`, background: 'none', cursor: 'pointer' }}>
                    {t.label}{badge && <span className="text-[9.5px] rounded-full px-1.5" style={{ background: 'var(--sem-surface)', border: `1px solid ${t.id === 'verify' ? `color-mix(in srgb,${badgeColor} 40%,transparent)` : 'var(--sem-border)'}`, color: t.id === 'verify' ? badgeColor : 'var(--sem-muted)' }}>{badge}</span>}
                  </button>
                );
              })}
            </div>
            <div className="flex-1 min-h-0 overflow-auto">
              {err && (
                <div className="p-3">
                  <Banner color="var(--sem-bad)">{plainDaxError(err)}</Banner>
                  {isSignInError(err) && session?.liveEndpoint && (
                    <button type="button" data-testid="signin-again" disabled={connBusy} className="mt-2 text-[12px] px-2 py-1 rounded-md"
                      style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', border: 'none', cursor: connBusy ? 'default' : 'pointer' }}
                      onClick={() => {
                        void connectXmla(session.liveEndpoint!, session.liveDatabase ?? '', 'interactive', session.currentTenant ?? null)
                          .then((r) => { if (r.ok) void runQuery(currentQuery()); else if (r.message) setErr(r.message); });
                      }}>{SIGN_IN_AGAIN}</button>
                  )}
                </div>
              )}
              {tab === 'result' && (res
                ? <div className="h-full px-3 pt-2.5 pb-3 flex flex-col min-h-0">
                    <div className="text-[11px] tnum pb-1.5 shrink-0" style={{ color: 'var(--sem-muted)' }}>
                      {rowAnnouncement(res.rowCount, res.truncated)} · {timingAnnouncement(res.elapsedMs)}
                      {queryIdentity(res.query || resQuery) && (
                        <div className="font-mono truncate" title={res.query || resQuery || undefined}>Result of: {queryIdentity(res.query || resQuery)}</div>
                      )}
                    </div>
                    <ResultGrid columns={res.columns} rows={res.rows} height="100%" truncated={res.truncated}
                    onCellMenu={mode === 'visual' ? (row, ci) => { const p = explainPayloadFromRow(config, row, ci); if (p) { setExplain(p); return true; } return false; } : undefined} />
                  </div>
                : <Empty>{conn?.connected ? 'Run a query (or build a visual) to see rows here.' : 'Choose a test model above to run queries.'}</Empty>)}
              {tab === 'perf' && <PerfTab {...{ runs, setRuns, clearOnRun, setClearOnRun, confirmShared, setConfirmShared, shared, conn, idle, busy, clearCache, runProfile, runPlan, runQuick, runColdWarm, timings, cw, plan, bench, clearMsg }} />}
              {tab === 'plan' && <PlanTab plan={plan} shared={!!shared} connected={!!conn?.connected} idle={idle} busy={busy} clearMsg={clearMsg} onCapture={runPlan} onClearCapture={clearThenPlan} />}
              {tab === 'debug' && <DebugTab logs={logs} logNote={logNote} connected={!!conn?.connected} idle={idle} busy={busy} visual={mode === 'visual'} onRun={() => debug()} />}
              {tab === 'verify' && <VerifyTab {...{ exprA, setExprA, exprB, setExprB, groupByDerived, filterLinesDerived, conn, busy, runVerify, eqEv, eqStale, vErr, customVerify, setVerifyGroupBy }} onChooseFields={() => setWbH(Math.max(wbH, 520))} onUseVisual={() => setCustomVerify(false)} />}
            </div>
          </div>
        </div>
      </div>

      {/* "Explain this number" slide-over (right-click a value cell in the matrix / table / result grid) */}
      {explain && <ExplainPanel payload={explain} onClose={() => setExplain(null)} />}
    </div>
  );
}

// ---- workbench tab bodies --------------------------------------------------------------------
function PerfTab(p: any) {
  const { runs, setRuns, clearOnRun, setClearOnRun, confirmShared, setConfirmShared, shared, conn, idle, busy, clearCache, runProfile, runPlan, runQuick, runColdWarm, timings, cw, plan, bench, clearMsg } = p;
  return (
    <div className="p-3 flex flex-col gap-3">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Compare a fresh run with repeat runs that can reuse earlier work.</span>
        <label className="text-[11px] ml-1" style={{ color: 'var(--sem-muted)' }}>runs</label>
        <input type="number" min={1} max={25} value={runs} onChange={(e) => setRuns(Math.max(1, Math.min(25, Number(e.target.value) || 1)))} className="w-14 text-[12px] px-2 py-1 rounded-md tnum outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <div className="ml-auto flex items-center gap-2 flex-wrap">
          <Button disabled={!conn?.connected || idle} onClick={clearCache} title="Clear the Storage-Engine cache (needs local Power BI Desktop / admin XMLA)">{busy === 'clear' ? 'Clearing…' : 'Clear cache'}</Button>
          <Button disabled={!conn?.connected || idle} onClick={runProfile} title="Server timings: the formula-engine / storage-engine split + heaviest scans">{busy === 'profile' ? 'Profiling…' : 'Profile'}</Button>
          <Button disabled={!conn?.connected || idle} onClick={runPlan} title="Capture the logical + physical query plan">{busy === 'plan' ? 'Capturing…' : 'Plan'}</Button>
          <Button disabled={!conn?.connected || idle} onClick={runQuick} title="Measure elapsed query time without clearing cached results">{busy === 'quick' ? 'Running…' : 'Quick'}</Button>
          <Button primary disabled={!conn?.connected || idle} onClick={runColdWarm} title="Cold (cache cleared) vs warm benchmark">{busy === 'coldwarm' ? 'Benchmarking…' : 'Cold / Warm'}</Button>
        </div>
      </div>
      <div className="flex items-center gap-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
        <label className="flex items-center gap-1.5 cursor-pointer"><input type="checkbox" checked={clearOnRun} onChange={(e) => setClearOnRun(e.target.checked)} /> Clear cache before each cold run</label>
        {shared && <label className="flex items-center gap-1.5 cursor-pointer" style={{ color: 'var(--sem-warn)' }}><input type="checkbox" checked={confirmShared} onChange={(e) => setConfirmShared(e.target.checked)} /> Shared endpoint: clearing the cache affects all users</label>}
      </div>
      {clearMsg && <div className="text-[11px]" style={{ color: 'var(--sem-good)' }}>{clearMsg}</div>}
      {!timings && !cw && !plan && !bench && <Empty>Start with Quick to measure query time. Use Profile for more detail, or Cold / Warm to compare fresh and repeat runs.</Empty>}
      {timings && <ServerTimingsView t={timings} />}
      {cw && <ColdWarmView cw={cw} />}
      {bench && (
        <div className="grid grid-cols-1 md:grid-cols-[200px_1fr] gap-4 items-center">
          <div className="flex flex-col gap-1.5">
            <Metric label="first run (cold-ish)" value={timingAnnouncement(bench.firstMs)} />
            <Metric label="warm min (steady state)" value={timingAnnouncement(bench.warmMinMs)} accent />
            <Metric label="warm median" value={timingAnnouncement(bench.warmMedianMs)} />
            <Metric label="runs" value={String(bench.runs)} />
          </div>
          <BenchBars runsMs={bench.runsMs} />
        </div>
      )}
    </div>
  );
}

function PlanTab({ plan, shared, connected, idle, busy, clearMsg, onCapture, onClearCapture }: { plan: QueryPlanResult | null; shared: boolean; connected: boolean; idle: boolean; busy: string | null; clearMsg: string | null; onCapture: () => void; onClearCapture: () => void }) {
  return (
    <div className="p-3 flex flex-col gap-3">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>See the steps the server took to answer the query. Some servers do not provide this detail.</span>
        <div className="ml-auto flex gap-2">
          <Button disabled={!connected || idle} onClick={onCapture}>{busy === 'plan' ? 'Capturing…' : plan ? 'Re-capture' : 'Capture'}</Button>
          <Button primary disabled={!connected || idle} onClick={onClearCapture} title="Clear cached data before capturing the plan. The server may still return no plan.">{busy === 'clear' || busy === 'plan' ? '…' : 'Clear cache & capture'}</Button>
        </div>
      </div>
      {clearMsg && <div className="text-[11px]" style={{ color: 'var(--sem-good)' }}>{clearMsg}</div>}
      {plan ? <QueryPlanView plan={plan} /> : <Empty>Capture a plan to see the server execution steps. This is an advanced way to investigate a slow query.</Empty>}
      {shared && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Note: query plans are often not emitted by the Power BI XMLA endpoint even for admins. <b>Profile</b> (server timings) and <b>Cold/Warm</b> can still help when timing or trace data is available.</div>}
    </div>
  );
}

function DebugTab({ logs, logNote, connected, idle, busy, visual, onRun }: { logs: EvalLogEntry[] | null; logNote: string | null; connected: boolean; idle: boolean; busy: string | null; visual: boolean; onRun: () => void }) {
  const acting = busy === 'debug';
  return (
    <div className="p-3 flex flex-col gap-3">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>EVALUATEANDLOG traces{logs ? ` · ${logs.length} log${logs.length === 1 ? '' : 's'}` : ''}</span>
        <div className="ml-auto flex gap-2">
          <Button primary={!logs} disabled={!connected || idle} onClick={onRun}>{acting ? 'Running…' : visual ? 'Log each measure' : 'Capture query logs'}</Button>
        </div>
      </div>
      {logNote && <TraceNote note={logNote} />}
      {!logs && !logNote && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}><span className="font-mono">EVALUATEANDLOG(expr,&nbsp;"label")</span> logs a labelled snapshot of any sub-expression. Hit <b>Log each measure</b> to auto-wrap the Values measures, or add it yourself in Query mode.</div>}
      {logs && logs.map((e, i) => (
        <div key={i} className="rounded-lg p-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
          <div className="flex items-baseline gap-2">
            <span className="text-[12px] font-semibold" style={{ color: 'var(--sem-accent)' }}>{e.label || `log ${i + 1}`}</span>
            <span className="text-[11px] font-mono truncate" style={{ color: 'var(--sem-muted)' }} title={e.expression}>{e.expression}</span>
            <span className="ml-auto text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{e.rowCount} row{e.rowCount === 1 ? '' : 's'}</span>
          </div>
          {e.columns.length > 0 && <div className="mt-1.5"><ResultGrid columns={e.columns.map((c) => ({ name: c }))} rows={e.rows} height={Math.min(220, 34 + e.rows.length * 26)} filterable={false} /></div>}
        </div>
      ))}
    </div>
  );
}

function VerifyTab(p: any) {
  const { exprA, setExprA, exprB, setExprB, groupByDerived, filterLinesDerived, conn, busy, runVerify, eqEv, eqStale, vErr, customVerify, setVerifyGroupBy, onChooseFields, onUseVisual } = p;
  const [choosingFields, setChoosingFields] = useState(false);
  const addFields = (nodes: BrowserNode[]) => setVerifyGroupBy([...new Set([...groupByDerived, ...nodes.map((n) => `'${n.table.split("'").join("''")}'[${n.name.split(']').join(']]')}]`)])]);
  return (
    <div className="p-3 flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Compare expressions across the combinations of fields you choose, including fields from different tables.</span>
        <div className="ml-auto"><Button primary disabled={!conn?.connected || busy === 'verify'} onClick={runVerify}>{busy === 'verify' ? 'Verifying…' : 'Verify'}</Button></div>
      </div>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        <LabeledMono label="Original formula: the measure selected in Values" value={exprA} onChange={setExprA} />
        <LabeledMono label="Candidate rewrite (exprB)" value={exprB} onChange={setExprB} />
      </div>
      <div className="text-[11px] rounded-lg px-3 py-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>
        <div className="flex gap-2 items-center flex-wrap">
          <b style={{ color: 'var(--sem-fg)' }}>{customVerify ? 'Custom test contexts' : 'Test contexts from the visual'}</b>
          <Button onClick={() => { if (!choosingFields) onChooseFields(); setChoosingFields(!choosingFields); }}>{choosingFields ? 'Done choosing' : 'Choose fields'}</Button>
          {customVerify && <Button onClick={onUseVisual}>Use visual fields</Button>}
        </div>
        <div className="flex gap-1.5 flex-wrap mt-2">
          {groupByDerived.map((field: string) => <span key={field} className="rounded border px-2 py-1" style={{ borderColor: 'var(--sem-border)' }}>{field} <button aria-label={'Remove ' + field} onClick={() => setVerifyGroupBy(groupByDerived.filter((f: string) => f !== field))}>×</button></span>)}
          {!groupByDerived.length && <span>Grand total only. Choose fields for a useful comparison.</span>}
        </div>
        <div className="mt-2">Combine fields such as Product category, Customer segment, Region and Date. The comparison covers the returned combinations.</div>
        {filterLinesDerived.length > 0 && <div className="mt-1">Filters from the visual: {filterLinesDerived.join(' && ')}</div>}
      </div>
      {choosingFields && <ObjectBrowser kinds={['column']} multiSelect dragEnabled={false} height={220} onPick={(n) => addFields([n])} onPickMany={addFields} />}
      {vErr && <Banner color="var(--sem-bad)">{vErr}</Banner>}
      {eqEv && (() => {
        const eq: EquivalenceResult = eqEv.result;
        // The verdict comes from the evidence's OWN stamp (the grid it ran with), never the current config —
        // and evidence invalidated by any expression/grid/filter edit renders stale, never green.
        const verdict = eqVerdict(eqEv);
        if (eqStale) {
          return (
            <Banner color="var(--sem-warn)">
              {`△ Evidence is STALE: the expressions, grid, filters, connection, or model changed since this comparison ran (its verdict was: ${verdict === 'noContext' ? 'unproven' : verdict}). Re-run Verify for evidence that matches what you see.`}
            </Banner>
          );
        }
        const color = verdict === 'proven' ? 'var(--sem-good)' : verdict === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)';
        const text: Record<EqVerdict, string> = {
          proven: `✓ Equivalent across the ${eq.rowsCompared} tested contexts.`,
          failed: `✗ NOT equivalent: ${eq.mismatchCount} of ${eq.rowsCompared} contexts differ. Do not apply.`,
          degraded_mismatch: `△ Difference observed in ${eq.mismatchCount} context(s) under a DEGRADED comparison. Not authoritative: the reduced-fidelity comparison itself can cause divergence. Not verified; do not apply on this evidence.`,
          degraded: `△ Values matched, but the comparison ran with reduced fidelity. NOT verified.${eq.fidelity ? ' ' + eq.fidelity : ''}`,
          thin: `△ Matched at the grand total only. NOT verified: choose fields for a per-context comparison.`,
          unverified: `△ Could not verify: ${eq.truncated ? `the comparison hit the row cap (${eq.rowsCompared} rows), so coverage is incomplete` : 'the comparison compared 0 rows, so nothing was proven'}.`,
          noContext: `△ Unproven (no evidence context): this result was reflected from an assistant run without its request grid, so a per-context proof cannot be claimed. Re-run Verify here for stamped evidence.`,
        };
        return (
        <>
          <Banner color={color}>{text[verdict]}</Banner>
          {eq.mismatches.length > 0 && (
            <div className="overflow-auto rounded-lg" style={{ border: '1px solid var(--sem-border)', maxHeight: 220 }}>
              <table className="text-[12px] border-collapse w-full">
                <thead><tr>{['Context', 'Original', 'Candidate'].map((h) => <th key={h} className="text-left px-2 py-1 sticky top-0" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)' }}>{h}</th>)}</tr></thead>
                <tbody>{eq.mismatches.map((m: EquivalenceMismatch, i: number) => (
                  <tr key={i}><td className="px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)' }}>{m.context}</td><td className="px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-good)' }}>{m.valueA}</td><td className="px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-bad)' }}>{m.valueB}</td></tr>
                ))}</tbody>
              </table>
            </div>
          )}
        </>
        );
      })()}
    </div>
  );
}

// ---- server timings (FE/SE split) -----------------------------------------------------------
function TraceNote({ note }: { note: string }) {
  const diagnostic = note.indexOf('[trace:');
  return <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>
    {diagnostic < 0 ? note : note.slice(0, diagnostic).trim()}
    {diagnostic >= 0 && <details className="mt-1"><summary className="cursor-pointer">Technical details</summary><code className="break-all">{note.slice(diagnostic)}</code></details>}
  </div>;
}

function ServerTimingsView({ t }: { t: ServerTimings }) {
  const sePct = t.totalMs > 0 ? Math.round((t.seMs / t.totalMs) * 100) : 0;
  const fePct = Math.max(0, 100 - sePct);
  return (
    <div className="rounded-lg p-3" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-2">
        <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Server timings</span>
        {!t.traceAvailable && <span className="text-[10px]" style={{ color: 'var(--sem-warn)' }}>Elapsed time only: {t.wallMs ?? t.totalMs} ms</span>}
      </div>
      {!t.traceAvailable && t.note && <TraceNote note={t.note} />}
      {t.traceAvailable && (
        <>
          {t.seQueries > 0 && <div className="flex h-5 rounded overflow-hidden text-[10px]" style={{ background: 'var(--sem-surface)' }} title={`FE ${t.feMs}ms · SE ${t.seMs}ms`}>
            <div className="flex items-center justify-center" style={{ width: `${fePct}%`, background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', minWidth: fePct > 0 ? 28 : 0 }}>{fePct > 8 ? `FE ${fePct}%` : ''}</div>
            <div className="flex items-center justify-center" style={{ width: `${sePct}%`, background: 'var(--sem-good)', color: '#000', minWidth: sePct > 0 ? 28 : 0 }}>{sePct > 8 ? `SE ${sePct}%` : ''}</div>
          </div>}
          <div className="flex flex-wrap gap-x-5 gap-y-1 mt-2 text-[11px] tnum" style={{ color: 'var(--sem-muted)' }}>
            <span><b style={{ color: 'var(--sem-fg)' }}>{t.totalMs} ms</b> server query</span>
            {t.wallMs != null && <span>{t.wallMs} ms round trip</span>}
            {t.setupMs != null && t.setupMs >= 0 && <span>{t.setupMs} ms trace setup</span>}
            {t.seQueries > 0 && <><span><b style={{ color: 'var(--sem-accent)' }}>{t.feMs} ms</b> estimated formula engine</span>
            <span><b style={{ color: 'var(--sem-good)' }}>{t.seMs} ms</b> captured storage engine</span></>}
            <span><b style={{ color: 'var(--sem-fg)' }}>{t.seQueries}</b> SE queries</span>
            {t.seCacheHits != null && <span><b style={{ color: 'var(--sem-good)' }}>{t.seCacheHits}</b> SE cache hits</span>}
            <span>{t.seCpuMs} ms SE cpu</span>
            {t.seParallelism > 0 && <span>{t.seParallelism}× parallelism</span>}
          </div>
          {t.note && <TraceNote note={t.note} />}
          {t.scans.length > 0 && (
            <div className="mt-2">
              <div className="text-[10px] uppercase tracking-wide mb-1" style={{ color: 'var(--sem-muted)' }}>Heaviest scans</div>
              <div className="overflow-auto rounded" style={{ border: '1px solid var(--sem-border)', maxHeight: 220 }}>
                <table className="w-full text-[11px] border-collapse">
                  <thead><tr>{['#', 'Subclass', 'Duration', 'CPU', 'Rows', 'xmSQL'].map((h, i) => (
                    <th key={h} className={(i >= 2 && i <= 4 ? 'text-right' : 'text-left') + ' px-2 py-1 sticky top-0'} style={{ background: 'var(--sem-surface)', borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{h}</th>
                  ))}</tr></thead>
                  <tbody>
                    {t.scans.map((s, i) => (
                      <tr key={i}>
                        <td className="text-left px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{s.line ?? i + 1}</td>
                        <td className="text-left px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{s.subclass ?? 'Scan'}</td>
                        <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-good)' }}>{s.durationMs} ms</td>
                        <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{s.cpuMs}</td>
                        <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{s.rows.toLocaleString()}</td>
                        <td className="text-left px-2 py-1 font-mono truncate" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)', maxWidth: 320 }} title={s.query}>{s.query ?? ''}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ---- cold/warm benchmark ---------------------------------------------------------------------
function ColdWarmView({ cw }: { cw: ColdWarmBenchmark }) {
  const statTable = (title: string, cold: ColdWarmStats, warm: ColdWarmStats) => (
    <div>
      <div className="text-[11px] uppercase tracking-wide font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>{title}</div>
      <table className="w-full text-[12px] border-collapse">
        <thead><tr>{['', 'Avg', '± StdDev', 'Min', 'Max'].map((h, i) => (
          <th key={h} className={(i === 0 ? 'text-left' : 'text-right') + ' px-2 py-1'} style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)', fontWeight: 600 }}>{h}</th>
        ))}</tr></thead>
        <tbody>
          {([['Cold', cold, 'var(--sem-warn)'], ['Warm', warm, 'var(--sem-good)']] as const).map(([label, s, c]) => (
            <tr key={label}>
              <td className="text-left px-2 py-1 font-medium" style={{ color: c, borderBottom: '1px solid var(--sem-border)' }}>{label}</td>
              <td className="text-right px-2 py-1 tnum font-semibold" style={{ borderBottom: '1px solid var(--sem-border)' }}>{s.n ? timingAnnouncement(s.avgMs) : 'Not run'}</td>
              <td className="text-right px-2 py-1 tnum" style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>{s.n ? `±${s.stdDevMs.toFixed(1)}` : 'Not run'}</td>
              <td className="text-right px-2 py-1 tnum" style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>{s.n ? timingAnnouncement(s.minMs) : 'Not run'}</td>
              <td className="text-right px-2 py-1 tnum" style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>{s.n ? timingAnnouncement(s.maxMs) : 'Not run'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
  return (
    <div className="flex flex-col gap-3">
      {!cw.cacheClearAvailable && <Banner color="var(--sem-warn)">{cw.note || 'Cache could not be cleared, so cold ≈ warm.'}</Banner>}
      {cw.cacheClearAvailable && cw.note && <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{cw.note}</div>}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {statTable('Total query (ms)', cw.coldTotal, cw.warmTotal)}
        {cw.traceAvailable && statTable('Storage engine (ms)', cw.coldSe, cw.warmSe)}
      </div>
      {cw.detail.length > 0 && (
        <details>
          <summary className="text-[11px] cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>Per-run detail ({cw.detail.length} runs)</summary>
          <div className="overflow-auto rounded-lg mt-1" style={{ border: '1px solid var(--sem-border)', maxHeight: 260 }}>
            <table className="w-full text-[12px] border-collapse">
              <thead><tr>{['Run', 'Temp', 'Total ms', 'SE ms', 'SE q'].map((h, i) => (
                <th key={h} className={(i < 2 ? 'text-left' : 'text-right') + ' px-2 py-1 sticky top-0'} style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{h}</th>
              ))}</tr></thead>
              <tbody>
                {cw.detail.map((d, i) => (
                  <tr key={i}>
                    <td className="text-left px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)' }}>{d.index}</td>
                    <td className="text-left px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)', color: d.cold ? 'var(--sem-warn)' : 'var(--sem-good)' }}>{d.cold ? 'cold' : 'warm'}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)' }}>{timingAnnouncement(d.totalMs)}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{timingAnnouncement(d.seMs)}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{d.seQueries}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </div>
  );
}

// ---- query plans (logical + physical) --------------------------------------------------------
function QueryPlanView({ plan }: { plan: QueryPlanResult }) {
  const tree = (title: string, nodes: QueryPlanNode[]) => (
    <div className="flex-1 min-w-0">
      <div className="text-[11px] uppercase tracking-wide font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>{title} <span className="opacity-70">({nodes.length})</span></div>
      <div className="overflow-auto rounded-lg text-[11px]" style={{ border: '1px solid var(--sem-border)', maxHeight: 360, background: 'var(--sem-surface)' }}>
        {nodes.length === 0
          ? <div className="px-2 py-2" style={{ color: 'var(--sem-muted)' }}>No result</div>
          : nodes.map((n, i) => (
            <div key={i} className="px-2 py-0.5 whitespace-nowrap flex items-center gap-2 font-mono" style={{ paddingLeft: 8 + Math.min(n.level, 24) * 8 }} title={n.detail}>
              <span style={{ color: 'var(--sem-fg)' }}>{n.operator}</span>
              {n.records != null && <span className="tnum" style={{ color: 'var(--sem-good)' }}>#{n.records.toLocaleString()}</span>}
            </div>
          ))}
      </div>
    </div>
  );
  return (
    <div className="rounded-lg p-3" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-2">
        <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Query plan</span>
        {!plan.traceAvailable && <span className="text-[10px]" style={{ color: 'var(--sem-warn)' }}>{plan.note || 'unavailable on this endpoint (needs local Power BI Desktop / admin XMLA)'}</span>}
      </div>
      {plan.traceAvailable && (
        <div className="flex gap-4 flex-col md:flex-row">
          {tree('Logical plan', plan.logicalTree)}
          {tree('Physical plan', plan.physicalTree)}
        </div>
      )}
    </div>
  );
}

// ---- local primitives ------------------------------------------------------------------------
function Seg<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <div className="inline-flex rounded-lg p-0.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      {options.map(([v, label]) => (
        <button key={v} onClick={() => onChange(v)} className="text-[12px] px-3.5 py-1 rounded-md" style={value === v ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', fontWeight: 600, border: 'none', cursor: 'pointer' } : { background: 'none', color: 'var(--sem-muted)', border: 'none', cursor: 'pointer' }}>{label}</button>
      ))}
    </div>
  );
}
function LabeledMono({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  const model = useDaxModelContext();
  return (
    <div className="flex flex-col gap-1">
      <label className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{label}</label>
      <DaxEditor value={value} onChange={onChange} model={model} lineNumbers={false} minHeight={44} />
    </div>
  );
}
function Empty({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center text-center text-[12px] p-4" style={{ color: 'var(--sem-muted)' }}><div>{children}</div></div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title} className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', border: 'none', cursor: 'pointer' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', cursor: 'pointer' }}>{children}</button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color, border: `1px solid color-mix(in srgb,${color} 40%, transparent)` }}>{children}</div>;
}
function Metric({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <span className="text-[13px] font-semibold tnum" style={{ color: accent ? 'var(--sem-accent)' : 'var(--sem-fg)' }}>{value}</span>
    </div>
  );
}
