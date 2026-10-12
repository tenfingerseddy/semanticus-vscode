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
import { measuredBench } from './benchtiming.mjs';
import { PivotConfig, EMPTY_CONFIG, buildQuery, buildInstrumentedQuery, seedConfig, filterToDax, parseRef, withSortBy } from './daxwells';
import { DaxVisual, explainPayloadFromRow, type VizType } from './daxvisual';
import { axisDims, foldSortColumns } from './axisorder';
import { QueryWells } from './querywells';
import { useQueriesAsk, AskField, AskChips, AskLines, AskFreeLine } from './queriesask';
import { squash } from './daxsquash';
import { withMeasureFormats, editingFormatsApply, measuresNotShadowed, queriesLinkedCopy, measureFormatsFromDmv, sentFormatsNow, MEASURE_FORMATS_DMV, type SentFormats } from './resultformats';
import { notesStore } from './studio/notes-runtime';
import { answerOwnsResult, producedByOf } from './resultownership';
import { SaveChooser } from './askdata';
import { answerFilterLines, checkDraftOf, filterWords, outcomeOf, pivotConfigOf, type CheckDraft, type QueriesHandoff } from './askdata-model';
import { useFeature } from './pro';
import { useSurfaceEscape } from './surface-escape';
import { MenuPop } from './menupop';
import { chartRulePick, engineShape, offeredKinds } from './chartpick.mjs';
import { friendlyHeader } from './resultformat.mjs';
import { grainWords } from './sortgrain';
import { ExplainPanel, type ExplainPayload } from './explainpanel';
import { plainDaxError } from './daxErrorText';
import { isSignInError, SIGN_IN_AGAIN } from './authcopy';
import { AuthorDrawer, type AuthorShape, type TestDefinitionW } from './tests-authoring';

// Queries (the old DAX Lab), redesigned 27 Sep with diagnostics D1, and on 8 Oct the sentence gave way to labelled wells.
// One head row (Build or DAX, the Pro question box, Save measure check, Reset, Run), one fields row (the four wells
// Rows / Columns / Measures / Filters, or the question's parts), and ONE result that fills the rest: Result and Speed are its only two views. Calculation steps
// open from a number's right-click and the more menu; Compare a rewrite from a measure chip, the DAX view and the
// Save measure check menu, both as side sheets, so the result stays in view.
// Mockup: ~/Downloads/semanticus-queries-redesign-mockups.html (#C, #D1).

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

// The DAX the person actually edited in the lab, or '' when they edited nothing worth saving as its own check.
// Query mode holds a whole query; the lab seeds a measure as EVALUATE ROW("Name", [Name]), and a query still in
// that shape — or one holding only the measure's own saved formula — is the measure as saved, not a new idea.
// Visual mode never carries one: its query is built from the wells, so there is nothing the person typed.
// generatedQuery is the text the LAB ITSELF last wrote into the editor (a Visual to Query switch, Reset, or a
// measure hand-off). Recognising the lab's own writing by shape was the bug: a Visual to Query switch pastes the
// wells' grouped query, which matches none of the known shapes, so an untouched view switch looked like an edit
// and offered a whole table query as the formula to check (Astra, 2026-09-14). What the lab wrote is not an edit
// however it is shaped, so the lab records it rather than guessing.
export function labExpressionEdit(mode: string, query: string, measureName: string, savedExpression?: string, generatedQuery?: string): string {
  if (mode !== 'query') return '';
  const text = (query ?? '').trim();
  if (!text || !measureName) return '';
  if (generatedQuery && squash(text) === squash(generatedQuery)) return '';
  const canonical = `EVALUATE ROW("${measureName.replace(/"/g, '""')}", [${measureName.replace(/]/g, ']]')}])`;
  if (squash(text) === squash(canonical)) return '';
  if (savedExpression && squash(text) === squash(savedExpression)) return '';
  return text;
}

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
// Two views above the result (Kane, 27 Sep, diagnostics D1): the result, and its speed. Server steps fold inside
// Speed; Calculation steps and Compare a rewrite open as side sheets from the thing they are about.
type WbTab = 'result' | 'speed';
type Sheet = 'compare' | 'steps' | null;
type DaxLabBusy = 'run' | 'ask' | 'measure' | 'debug' | 'profile' | 'plan' | 'quick' | 'coldwarm' | 'clear' | 'verify' | null;
const WB_TABS: { id: WbTab; label: string }[] = [{ id: 'result', label: 'Result' }, { id: 'speed', label: 'Speed' }];

interface DaxLabTabState {
  tab: WbTab;
  setTab: React.Dispatch<React.SetStateAction<WbTab>>;
  sheet: Sheet;
  setSheet: React.Dispatch<React.SetStateAction<Sheet>>;
  planOpen: boolean;
  setPlanOpen: React.Dispatch<React.SetStateAction<boolean>>;
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
  /** execContextKey without the model's revision: which connection and model the page is on, whatever has been edited since. */
  formatScopeKey: string;
  /** The formats the rows on screen were SENT with (null for rows no run of this page sent). Read with sentFormatsNow. */
  sentFormats: SentFormats | null;
  noteSent: (res: ResultSet, sent: SentFormats) => void;
  speedQuery: string | null;
  speedFor: (query: string | null | undefined) => void;
}

const DaxLabTabStateContext = createContext<DaxLabTabState | null>(null);

function useDaxLabTabState(): DaxLabTabState {
  const state = useContext(DaxLabTabStateContext);
  if (!state) throw new Error('DaxLabView must be rendered inside DaxLabTabStateProvider');
  return state;
}

// Null-tolerant selector for the Studio tab bar: true while any Queries op (human or reflected agent run) is in
// flight. Read outside DaxLabView so the tab bar can flag work that finished, or is still running, while hidden.
export function useDaxLabBusy(): boolean {
  return useContext(DaxLabTabStateContext)?.busy != null;
}

// Mounted by App above the conditional Studio tab body (and inside its model-session key). Human-started RPCs and
// reflected agent runs can therefore finish while Queries is hidden, while a different model still drops the entire
// result set exactly as the old component remount did.
export function DaxLabTabStateProvider({ children }: { children: React.ReactNode }) {
  const { conn, session } = useConnection();
  const [tab, setTab] = usePersistedState<WbTab>('lab.wbtab', 'result');
  const [sheet, setSheet] = useState<Sheet>(null);
  const [planOpen, setPlanOpen] = useState(false);
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
  // The query every number on Speed describes. Each speed tool stamps it before its numbers land, and numbers about a
  // different query are put away first, so one Speed view never shows two queries' numbers as one answer (a person's
  // cold and warm runs beside an assistant's profile of another query, verify-queries-redesign D7).
  const [speedQuery, setSpeedQuery] = useState<string | null>(null);
  const speedRef = useRef<string | null>(null);
  const speedFor = (query: string | null | undefined) => {
    const q = query ?? null;
    if (squash(speedRef.current) !== squash(q)) { setCw(null); setBench(null); setTimings(null); setPlan(null); }
    speedRef.current = q; setSpeedQuery(q);
  };

  // Keep the evidence context byte-for-byte equivalent to the old in-view calculation. Generic results retain the
  // existing QueryStalenessChip behavior; equivalence evidence continues to compare its receipt stamp to this key.
  const execContextKey = JSON.stringify([
    conn?.connected ?? null, conn?.kind ?? null, conn?.dataSource ?? null,
    conn?.database ?? null, conn?.connectionId ?? null, conn?.account ?? null,
    session?.currentTenant ?? null,
    session?.sessionId ?? null, session?.revision ?? null,
  ]);

  // Which connection and model the page is on, whatever has been edited since: the revision is the key's last item.
  const formatScopeKey = JSON.stringify(JSON.parse(execContextKey).slice(0, -1));

  // The formats a run's rows were sent with, noted by the holder that keeps the rows. They are fixed when the query goes out,
  // never read from whatever the page is on when the rows arrive, so a connection switched mid-run cannot hand another model's
  // numbers this model's formats (Sol delta B4). Rows no run of this page sent (an assistant's, a question's) carry none.
  const [sentFor, setSentFor] = useState<{ res: ResultSet; sent: SentFormats } | null>(null);
  const noteSent = (r: ResultSet, sent: SentFormats) => setSentFor({ res: r, sent });
  const sentFormats = res && sentFor?.res === res ? sentFor.sent : null;

  // Reflections must share the holder's lifetime too; otherwise an agent result emitted while another tab is visible
  // would still disappear even though human-started promises now survive. Each lands where D1 puts its tool: speed
  // work on Speed (server steps open their fold), calculation steps and a comparison in their side sheet.
  useClaudeReflection('run_dax', (e) => {
    const r = (e.result as ResultSet) ?? null;
    // A run that did not happen (held for the person's approval, refused, or failed) leaves the person's own result
    // where it is. The banner says what the assistant tried and why it did not run; the result area never shows an
    // empty result or an approval line written for the assistant, and Speed never times a query that did not run.
    if (!r || e.error || r.error || (r as { policyRefused?: boolean }).policyRefused || r.cancelled) { setClaudeEvent(e); return; }
    setRes(r); setResQuery(r.query ?? e.query ?? null); setLogs(null); setErr(null); setClaudeEvent(e);
  });
  useClaudeReflection('evaluate_and_log', (e) => {
    const r = e.result as EvalLogResult | undefined; const rows = r?.resultRows ?? [];
    setRes(r ? { columns: r.resultColumns, rows, rowCount: r.resultRowCount, truncated: (r.resultRowCount ?? rows.length) > rows.length, elapsedMs: r.elapsedMs, query: e.query } : null);
    setResQuery(e.query ?? null);
    setLogs(r?.entries ?? null); setLogNote(r?.note ?? null); setErr(e.error ?? null); setClaudeEvent(e); setSheet('steps');
  });
  useClaudeReflection('profile_dax', (e) => { if (e.result) { speedFor(e.query); setTimings(e.result as ServerTimings); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('benchmark_dax', (e) => { if (e.result) { const r = e.result as BenchmarkResult; speedFor(e.query); setBench(measuredBench(r)); setErr(r.error ?? e.error ?? null); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('benchmark_coldwarm', (e) => { if (e.result) { speedFor(e.query); setCw(e.result as ColdWarmBenchmark); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('capture_query_plan', (e) => { if (e.result) { speedFor(e.query); setPlan(e.result as QueryPlanResult); setClaudeEvent(e); setTab('speed'); setPlanOpen(true); } });
  useClaudeReflection('clear_cache', (e) => { const r = e.result as ClearCacheResult | undefined; if (r) { setClearMsg(r.cleared ? `Cleared the cache (${r.elapsedMs} ms)` : (r.error ?? 'Clear cache failed')); setClaudeEvent(e); } });
  useClaudeReflection('verify_equivalence', (e) => { if (e.result) { setEqEv({ result: e.result as EquivalenceResult, gridCount: null, requestKey: null, contextKey: execContextKey }); setVErr(e.error ?? null); setClaudeEvent(e); setSheet('compare'); } });

  return (
    <DaxLabTabStateContext.Provider value={{
      tab, setTab, sheet, setSheet, planOpen, setPlanOpen, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
      plan, setPlan, timings, setTimings, clearMsg, setClearMsg, eqEv, setEqEv, vErr, setVErr,
      busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey, formatScopeKey, sentFormats, noteSent, speedQuery, speedFor,
    }}>
      {children}
    </DaxLabTabStateContext.Provider>
  );
}

const CHART_TITLE: Record<VizType, string> = { matrix: 'Matrix', line: 'Line', bar: 'Bar', area: 'Area', table: 'Table', card: 'Card', scatter: 'Scatter' };
const MAIN_KINDS: VizType[] = ['matrix', 'line', 'bar', 'area', 'table'];

export function DaxLabView({ navTarget, onNavConsumed, onOpenTests }: { navTarget?: { ref: string; dax?: string; handoff?: QueriesHandoff; nonce: number } | null; onNavConsumed?: (nonce: number) => void; onOpenTests?: () => void }) {
  const { conn, session, context, connectXmla, busy: connBusy } = useConnection();
  const model = useDaxModelContext();
  const editorRef = useRef<DaxEditorHandle>(null);
  const runGen = useRef(0);
  const askGrant = useFeature('askData');

  const persistKey = session?.sessionId ?? 'no-model';
  const [mode, setMode] = usePersistedState<Mode>('lab.mode.' + persistKey, 'visual');
  const [query, setQuery] = usePersistedState('lab.bq.' + persistKey, "EVALUATE\n    TOPN(1000, SUMMARIZECOLUMNS('Date'[Date], \"v\", [Total Sales]))");
  // What the LAB last wrote into the editor. Persisted alongside the query itself so a reload cannot turn the
  // lab's own text into the person's, and seeded with the same starting query for the same reason.
  const [generatedQuery, setGeneratedQuery] = usePersistedState('lab.bq.generated.' + persistKey, "EVALUATE\n    TOPN(1000, SUMMARIZECOLUMNS('Date'[Date], \"v\", [Total Sales]))");
  // H5: the person's own query, kept when the question box writes over it, so one action puts it back.
  const [putBack, setPutBack] = usePersistedState('lab.ask.putback.' + persistKey, '');
  const [testMeasureRef, setTestMeasureRef] = usePersistedState('lab.testMeasure.' + persistKey, '');
  // A deliberate measure action seeds a runnable query. Ordinary tree selection never reaches this effect, so it
  // cannot overwrite a Queries draft. The nonce makes a repeated action explicit.
  useEffect(() => {
    // Open in Queries from Ask your data: the engine's DAX text, taken as the query once, then acknowledged so
    // coming back through area memory cannot replay it over what the person has typed since.
    if (navTarget?.dax) {
      setMode('query');
      // With the page's answer handed over in memory, it lands as a question here: the chips, the editor's DAX and
      // the rows, with no second ask and no run (the pre-flight's hand-off; verify-queries-redesign D12). Without it
      // (a reload), the DAX text alone, as before.
      if (navTarget.handoff?.answer?.dax === navTarget.dax) ask.adopt(navTarget.handoff);
      else { setQuery(navTarget.dax); setGeneratedQuery(navTarget.dax); }
      onNavConsumed?.(navTarget.nonce);
      return;
    }
    if (!navTarget?.ref.startsWith('measure:')) return;
    const name = navTarget.ref.slice(navTarget.ref.indexOf('/') + 1);
    const outputName = name.replace(/\"/g, '\"\"');
    const identifier = name.replace(/]/g, ']]');
    const seedText = `EVALUATE ROW(\"${outputName}\", [${identifier}])`;
    setMode('query'); setQuery(seedText); setGeneratedQuery(seedText); setTestMeasureRef(navTarget.ref);
    onNavConsumed?.(navTarget.nonce);
  }, [navTarget?.nonce]);
  const [wells, setConfig] = usePersistedState<PivotConfig>('lab.config.' + persistKey, EMPTY_CONFIG);
  // The wells as the person set them, plus each field's sort-by column from the model (so the query can bring it and order
  // by it, UAT N1). Keyed by what the model says about THESE fields, so a model reload that changes nothing here is not a new config.
  const sortSig = [...wells.rows, ...wells.cols].map((f) => model.columns.find((c) => c.table === f.table && c.name === f.name)?.sortByColumn ?? '').join('|');
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const config = useMemo(() => withSortBy(wells, model), [wells, sortSig, model.columns.length > 0]);
  const wellsTouched = useRef(false);
  const measureRefOf = (field?: PivotConfig['values'][number]) => field?.table ? `measure:${field.table}/${field.name}` : '';
  const contextRefOf = (field: PivotConfig['rows'][number]) => field.table ? `column:${field.table}/${field.name}` : field.ref;
  function onWells(next: PivotConfig) { wellsTouched.current = true; setConfig(next); setTestMeasureRef(measureRefOf(next.values[0])); }
  // The person's own chart choice, or null for the rules' pick. It is forgotten when the result's shape changes.
  const [vizOverride, setVizOverride] = useState<{ viz: VizType; shape: string } | null>(null);
  const {
    tab, setTab, sheet, setSheet, planOpen, setPlanOpen, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
    plan, setPlan, timings, setTimings, clearMsg, setClearMsg, eqEv, setEqEv, vErr, setVErr,
    busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey, formatScopeKey, sentFormats, noteSent, speedQuery, speedFor,
  } = useDaxLabTabState();
  const [fieldsOpen, setFieldsOpen] = usePersistedState('lab.fieldsOpen', false);
  useEffect(() => { if (res && !res.error) notesStore.setListOpen(false); }, [res]);
  // The Fields drawer's edge drags, as the old rail's did; it snaps to whole grid columns so both cards keep their
  // edges on the grid (verify-queries-redesign D11).
  const [fieldsW, onFieldsDrag] = useResizable('lab.fieldsW', 240, { axis: 'x', min: 170, max: 420 });
  const fieldsSpan = fieldsW < 200 ? 2 : fieldsW < 320 ? 3 : 4;
  const [editorH, onEditorDrag, setEditorH] = useResizable('lab.editorH', 180, { axis: 'y', min: 100, max: 520 });

  const [runs, setRuns] = usePersistedState('lab.runs', 5);
  const [clearOnRun, setClearOnRun] = usePersistedState('lab.clearOnRun', true);
  const [confirmShared, setConfirmShared] = useState(false);
  const [checkMenu, setCheckMenu] = useState(false);
  const [moreMenu, setMoreMenu] = useState(false);
  const [kindMenu, setKindMenu] = useState(false);
  const [checkShape, setCheckShape] = useState<AuthorShape | null>(null);
  const [checkDraft, setCheckDraft] = useState<TestDefinitionW | null>(null);
  const [checkSaved, setCheckSaved] = useState(false);
  const [saving, setSaving] = useState<{ row: number; measure: number } | null>(null);
  const [answerDraft, setAnswerDraft] = useState<CheckDraft | null>(null);

  // compare (exprA seeds from the first measure; the group-by + filters come from the wells' own split)
  const [exprA, setExprA] = usePersistedState('lab.exprA', '');
  const [exprB, setExprB] = usePersistedState('lab.exprB', '');
  const [customVerify, setCustomVerify] = usePersistedState('lab.verify.custom.' + persistKey, false);
  const [verifyFields, setVerifyFields] = usePersistedState<string[]>('lab.verify.fields.' + persistKey, []);
  // "Explain this number": right-click a number opens a small menu (Explain this number, Calculation steps).
  const [explain, setExplain] = useState<ExplainPayload | null>(null);
  const [cellMenu, setCellMenu] = useState<{ payload: ExplainPayload; x: number; y: number } | null>(null);
  const lastPointer = useRef({ x: 0, y: 0 });

  // The question box (Pro). The hook calls nothing by itself; only AskField, mounted on a granted plan, asks.
  const ask = useQueriesAsk({
    persistKey, busyElsewhere: busy !== null && busy !== 'ask',
    setBusy: (b) => setBusy(b),
    onDax: (dax) => {
      // H5: keep the person's own text before the box writes over it. The lab's own writing is not theirs.
      if (query.trim() && squash(query) !== squash(generatedQuery)) setPutBack(query);
      setQuery(dax); setGeneratedQuery(dax);
    },
    onRows: (rs, dax) => { runGen.current++; setRes(rs); setResQuery(rs ? dax : null); setErr(null); setLogs(null); setClaudeEvent(null); setTab('result'); },
  });
  const askOn = askGrant === 'granted';
  const chipsHere = askOn && !!ask.parts && (ask.parts.measures ?? []).length > 0 && !!ask.chipDax;
  // The chips own the query while the editor still holds exactly what they wrote (whitespace squashed).
  const askOwns = chipsHere && squash(query) === squash(ask.chipDax);

  const shared = conn?.connected && conn.kind !== 'local';
  const idle = busy !== null;
  // Build view runs the wells' query, unless a question's parts hold the query, which lives in the editor.
  const currentQuery = () => (mode === 'visual' && !chipsHere ? buildQuery(config) : query);
  // The wells already hold everything a result needs, so the next step is Run, not "add a measure".
  const wellsReady = config.values.length > 0;
  // What Compare a rewrite tests across: the question's split and its own filters while a question holds the query,
  // otherwise the wells'. Comparing the hidden wells under a question proved something about a query nobody
  // could see (verify-queries-redesign D5).
  const qParts = chipsHere ? ask.parts : null;
  const groupByDerived = useMemo(() => customVerify ? verifyFields : qParts ? [...(qParts.groupBy ?? [])] : [...config.rows, ...config.cols].map((f) => f.ref), [config, customVerify, verifyFields, qParts]);
  const setVerifyGroupBy = (fields: string[]) => { setVerifyFields(fields); setCustomVerify(true); };
  const filterLinesDerived = useMemo(() => qParts ? answerFilterLines(ask.chipDax) : config.filters.map(filterToDax).filter((x): x is string => !!x), [config, qParts, ask.chipDax]);
  const compareSubject: 'wells' | 'question' | 'dax' = qParts ? 'question' : mode === 'visual' ? 'wells' : 'dax';
  // The formula Compare starts from: the question's measure while a question holds the query, else the one in use.
  const compareStart = () => (qParts?.measures?.[0] ? `[${qParts.measures[0].replace(/]/g, ']]')}]` : exprA || config.values[0]?.ref || '');
  // The measures' saved formulas, so the handoff can tell an EDITED expression from the measure as it stands.
  const [savedExpressions, setSavedExpressions] = useState<Record<string, string>>({});
  useEffect(() => {
    let alive = true;
    rpc<{ ref: string; expression?: string }[]>('listMeasures')
      .then((rows) => { if (alive) setSavedExpressions(Object.fromEntries(rows.map((r) => [r.ref, r.expression ?? '']))); })
      .catch(() => undefined);
    return () => { alive = false; };
  }, [session?.revision]);
  const selectedMeasureName = testMeasureRef.slice(testMeasureRef.indexOf('/') + 1);
  const editedExpression = labExpressionEdit(mode, query, selectedMeasureName, savedExpressions[testMeasureRef], generatedQuery);
  // Heal a stale persisted tab id (the old five tabs: Performance, Server steps, Debug and Verify).
  useEffect(() => { if (!WB_TABS.some((t) => t.id === tab)) setTab((tab as string) === 'perf' || (tab as string) === 'plan' ? 'speed' : 'result'); }, [tab]);

  // Seed RUNNABLE defaults from the live model the first time (never the dead Contoso examples).
  const seeded = useRef(false);
  useEffect(() => {
    if (seeded.current) return;
    const empty = !config.rows.length && !config.cols.length && !config.values.length && !config.filters.length;
    if (empty && (model.measures.length || model.columns.length)) { setConfig(seedConfig(model)); seeded.current = true; }
    else if (!empty || (!model.measures.length && !model.columns.length && (config.values.length || config.rows.length))) seeded.current = true;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model]);
  // keep exprA pointed at the Show measure unless the user has typed their own
  useEffect(() => { if (!exprA && config.values[0]) setExprA(config.values[0].ref); /* eslint-disable-next-line */ }, [config.values]);
  useEffect(() => {
    if (!navTarget?.ref.startsWith('measure:') && mode === 'visual' && config.values[0]) setTestMeasureRef(measureRefOf(config.values[0]));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, config.values, navTarget?.nonce]);

  // The formats this page may put on rows it SENDS now: the editing model's own, only when the query connection IS the editing model
  // and nothing is unsaved (Sol delta B4). Fixed here, at send, and never re-read when the rows arrive.
  const scopeNow = useRef(formatScopeKey);
  scopeNow.current = formatScopeKey;
  const formatsAtSend = (sentIn: string): SentFormats => ({
    scope: JSON.stringify(JSON.parse(sentIn).slice(0, -1)),
    measures: editingFormatsApply(measuresNotShadowed(model.measures, model.columns), { relationship: context?.relationship, hasUnsavedChanges: session?.hasUnsavedChanges }),
  });
  // A linked copy (the default: edit the working copy, test on its runtime) is proven by asking the query model itself: its own
  // measure formats are the ones its numbers belong to, whether or not they match the copy being edited.
  async function proveLinkedFormats(rs: ResultSet, sent: SentFormats, gen: number) {
    try {
      const dmv = measureFormatsFromDmv(await rpc<ResultSet>('runDmv', MEASURE_FORMATS_DMV, 100000));
      if (!dmv || gen !== runGen.current || scopeNow.current !== sent.scope) return;
      noteSent(rs, { scope: sent.scope, measures: measuresNotShadowed(dmv, model.columns) });
    } catch { /* not proven: the numbers show plain */ }
  }
  async function runQuery(q: string) {
    const gen = ++runGen.current;
    // The context this query goes out in. The rows are formatted for THAT, however the connection changes while it runs.
    const sentIn = execContextKey;
    const sent = formatsAtSend(sentIn);
    const linked = queriesLinkedCopy(context?.relationship);
    setBusy('run'); setErr(null); setLogs(null); setClaudeEvent(null);
    try {
      const r = await rpc<ResultSet>('runDax', q, 50000);
      if (gen !== runGen.current) return;
      if (r.cancelled) { setErr(r.error || 'The query was stopped.'); return; }
      if (r.error) { setErr(r.error); setRes(null); setResQuery(null); }
      else {
        const rs = { ...r, query: r.query || q };
        setRes(rs); setResQuery(rs.query); noteSent(rs, sent);
        if (linked) void proveLinkedFormats(rs, sent, gen);
      }
    } catch (e) {
      if (gen !== runGen.current) return;
      setErr(String((e as Error).message ?? e));
    } finally { if (gen === runGen.current) setBusy(null); }
  }
  function stopQuery() {
    if (busy === 'ask') { ask.stop(); return; }
    runGen.current++;
    void rpc('cancelDax').catch(() => { /* the in-flight run still settles */ });
    setBusy(null);
    setErr('The query was stopped.');
  }
  function run() { setTab('result'); void runQuery(currentQuery()); }
  async function debug(q?: string) {
    const gen = ++runGen.current;
    const ran = q ?? (mode === 'visual' && !chipsHere ? buildInstrumentedQuery(config) : currentQuery());
    const sent = formatsAtSend(execContextKey);
    setSheet('steps'); setBusy('debug'); setErr(null); setClaudeEvent(null);
    try {
      const r = await rpc<EvalLogResult>('evaluateAndLog', ran, 10000);
      if (gen !== runGen.current) return;
      if (r.error) { setErr(r.error); setRes(null); setLogs(null); setResQuery(null); }
      else {
        const rs = { columns: r.resultColumns, rows: r.resultRows, rowCount: r.resultRowCount, truncated: false, elapsedMs: r.elapsedMs, query: ran };
        setRes(rs); setResQuery(ran); noteSent(rs, sent); setLogs(r.entries); setLogNote(r.note ?? null);
      }
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
  const runProfile = () => { const q = currentQuery(); speedFor(q); setTab('speed'); void call<ServerTimings>('profileDax', setTimings, 'profile', q); };
  const runPlan = () => { const q = currentQuery(); speedFor(q); setTab('speed'); setPlanOpen(true); void call<QueryPlanResult>('captureQueryPlan', setPlan, 'plan', q); };
  const runQuick = () => { const q = currentQuery(); speedFor(q); setTab('speed'); setCw(null); void call<BenchmarkResult>('benchmarkDax', (r) => setBench(r ? measuredBench(r) : null), 'quick', q, runs); };
  // Measure speed (D1): first and repeat runs, then where the time went, in one click. The two halves are the same
  // two engine calls Cold / Warm and Profile always made; only the button that starts them is new.
  async function measureSpeed() {
    const gen = ++runGen.current;
    const q = currentQuery();
    speedFor(q); setTab('speed'); setBench(null); setBusy('measure'); setErr(null); setClaudeEvent(null);
    try {
      const c = await rpc<ColdWarmBenchmark>('benchmarkColdWarm', q, runs, clearOnRun, confirmShared);
      if (gen !== runGen.current) return;
      if (c?.error) { setErr(c.error); setCw(null); } else if (c?.coldTotal && c?.warmTotal) setCw(c); else setCw(null);
      const t = await rpc<ServerTimings>('profileDax', q);
      if (gen !== runGen.current) return;
      // A failed profile puts the old one away too: its split and scans describe another run, maybe another query.
      if (t?.error) { setErr(t.error); setTimings(null); } else setTimings(t);
    } catch (e) {
      if (gen === runGen.current) setErr(String((e as Error).message ?? e));
    } finally { if (gen === runGen.current) setBusy(null); }
  }
  async function clearCache() {
    setBusy('clear'); setErr(null); setClearMsg(null);
    try { const r = await rpc<ClearCacheResult>('clearCache', confirmShared); setClearMsg(r.cleared ? `Cleared the cache${r.database ? ` for "${r.database}"` : ''} (${r.elapsedMs} ms)` : (r.error ?? 'Clear cache failed')); }
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(null); }
  }
  // Cold plan capture in one click: clear the SE cache, then capture (a warm/cached query emits no plan).
  async function clearThenPlan() { const q = currentQuery(); speedFor(q); setTab('speed'); setPlanOpen(true); await clearCache(); await call<QueryPlanResult>('captureQueryPlan', setPlan, 'plan', q); }
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
  // The compare word follows the STAMPED evidence: only a fresh 'proven' reads 'match', only a fresh 'failed' reads
  // 'diff'; stale evidence and every degraded/thin/unproven state carry the amber dot.
  const eqV = eqEv ? eqVerdict(eqEv) : null;
  const compareBadge = eqV ? {
    text: eqStale ? 'stale' : eqV === 'proven' ? 'match' : eqV === 'failed' ? 'diff' : 'unproven',
    color: eqStale ? 'var(--sem-warn)' : eqV === 'proven' ? 'var(--sem-good)' : eqV === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)',
  } : null;

  // Build view auto-runs on every wells change the user makes (like a real Power BI visual). Opening the tab,
  // switching models, or seeding defaults must not fire an unrequested query against the live endpoint.
  useEffect(() => {
    if (mode !== 'visual' || chipsHere || !conn?.connected || !wellsTouched.current) return;
    const q = buildQuery(config);
    const t = setTimeout(() => { void runQuery(q); }, 220);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, mode, conn?.connected]);

  // A view switch REGENERATES the query from the same wells. It is the lab writing, not the person, so it is
  // recorded as such: a switch with no editing must not read as a changed formula. A question's DAX is already there.
  function switchMode(m: Mode) { if (m === 'query' && mode === 'visual' && !chipsHere) { const q = buildQuery(config); if (query.trim() && squash(query) !== squash(generatedQuery)) setPutBack(query); setQuery(q); setGeneratedQuery(q); } setMode(m); }   // an edited query is put away first, never overwritten (R-B), exactly as the ask box does
  function compareValue(ref: string) { setExprA(ref); setSheet('compare'); }
  function openMeasureCheck(shape: Extract<AuthorShape, 'measureValue' | 'measureReconcile'>) {
    const groupBy = [...config.rows, ...config.cols].map(contextRefOf);
    // The wells' FILTERS travel on both paths. Dropping them turned a calculation filtered to one year into a
    // check over the whole model, which is a wrong check that looks right. The engine records the connection.
    const filters = filterLinesDerived.join(', ');
    setCheckDraft({
      id: '', kind: shape, title: '', targetRef: testMeasureRef, enabled: true,
      paramsJson: JSON.stringify(shape === 'measureReconcile'
        ? { measureRef: testMeasureRef, groupBy, filterDax: filters || undefined }
        : { measureRef: testMeasureRef, filterDax: filters || undefined, expressionDax: editedExpression || undefined }),
    });
    setCheckShape(shape); setCheckMenu(false); setCheckSaved(false);
  }
  // Reset the wells + editor back to the model's seeded defaults (the runnable starting point), and put away a
  // question's answer and parts.
  // Back to the person's own wells: the question's parts and answer are put away (its words stay in the field), and
  // the wells the person filled come back as they were and run. Before, the only way back was Reset, which threw the
  // wells away (verify-queries-redesign D8).
  function leaveQuestion(runSentence = true) {
    ask.clear(); setSaving(null); setMode('visual');
    if (runSentence && conn?.connected && config.values.length) void runQuery(buildQuery(config));
    else if (runSentence) { runGen.current++; setRes(null); setResQuery(null); }
  }
  function resetDefault() { wellsTouched.current = true; ask.clear(); setSaving(null); setPutBack(''); const c = seedConfig(model); setConfig(c); setTestMeasureRef(measureRefOf(c.values[0])); setQuery(buildQuery(c)); setGeneratedQuery(buildQuery(c)); setExprA(c.values[0]?.ref ?? ''); setExprB(''); }

  // The chart: what the result is split by decides which kinds fit, and the rules pick one (chartpick.mjs).
  // A result is drawn and explained under the fields of the query that PRODUCED it, never the fields on screen: an
  // assistant's run, or a hand-written query, is shown as its rows (verify-queries-redesign D13). A question's result
  // carries the question's own filters, so its tooltips and Explain this number describe the number shown (D1).
  // A result run from typed DAX carries no formats: each plain measure column gets its measure's own by name (Sol 3.1), from
  // the model the numbers came from. They are the formats the run was SENT with (formatsAtSend: the editing model's own when the
  // query connection IS the editing model, or a linked copy's own, read from it), good while the page is still on that connection
  // and whatever has been edited since; otherwise the numbers show plain (Sol delta B4).
  const formatMeasures = useMemo(() => sentFormatsNow(sentFormats, formatScopeKey), [sentFormats, formatScopeKey]);
  const formattedRes = useMemo(() => (res ? withMeasureFormats(res, resQuery ?? res.query, formatMeasures) : res), [res, resQuery, formatMeasures]);
  const producedBy = producedByOf(res, resQuery);
  // The question's statement holds only over rows its own query produced; the same comparison the chart below uses.
  const answerRows = answerOwnsResult({ askOwns, chipDax: ask.chipDax, res, resQuery });
  const chartConfig: PivotConfig | null = !res ? null
    : chipsHere ? (askOwns && producedBy === squash(ask.chipDax) ? pivotConfigOf(ask.parts!, ask.chipDax) : null)
      : producedBy === squash(buildQuery(config)) || producedBy === squash(buildInstrumentedQuery(config)) ? config : null;
  // The sort-by columns a wells query brought leave the result here, so every page below counts columns by the wells' layout.
  const shownRes = useMemo(() => (formattedRes && chartConfig ? foldSortColumns(formattedRes, chartConfig) : formattedRes), [formattedRes, chartConfig]);
  // A date is drawn as time, and the rules say so, only over an order that was really set (axisDims, UAT N1).
  const shape = useMemo(() => {
    if (!shownRes || !chartConfig) return null;
    return { dims: axisDims(shownRes, chartConfig), measures: chartConfig.values.length, rows: shownRes.rows.length };
  }, [shownRes, chartConfig]);
  const rule = shape ? chartRulePick(shape) : null;
  // Pro: Jev may pick the chart (decision point queries.chart_kind, docs/queries-chart-pick-bar.md). The engine answers
  // "rules" while the point is off, below its bar, or unsure, and only a pick Jev made is labelled as Jev's. It sends the
  // result's shape and a question's words, never its rows. Free never asks: the rules' pick is Free's pick.
  const [jevPick, setJevPick] = useState<{ key: string; kind: VizType; reason: string } | null>(null);
  const pickWords = chipsHere && ask.answer?.readBy === 'jev' ? ask.words.trim() : '';
  const pickKey = shape && rule ? JSON.stringify([shape.dims, shape.measures, rule.fits, rule.pick, pickWords]) : '';
  useEffect(() => {
    if (!askOn || !shape || !rule || rule.fits.length < 2) return;
    let alive = true;
    rpc<{ kind?: string; by?: string; reason?: string }>('pickChartKind', { words: pickWords || undefined, shape: engineShape(shape), fits: rule.fits, rulesPick: rule.pick })
      .then((p) => { if (alive) setJevPick(p?.by === 'jev' && p.kind && rule.fits.includes(p.kind as VizType) ? { key: pickKey, kind: p.kind as VizType, reason: p.reason ?? '' } : null); })
      .catch(() => { if (alive) setJevPick(null); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [askOn, pickKey]);
  const jevHere = !!jevPick && !!rule && jevPick.key === pickKey;
  const basePick: VizType | null = rule ? (jevHere ? jevPick!.kind : rule.pick) : null;
  // A date in the columns with categories in the rows: time runs along the axis and each row is a series.
  const dateAcross = !!shape && !!chartConfig && chartConfig.cols.length > 0 && shape.dims.slice(chartConfig.rows.length).some((d) => d.isDate && d.ordered !== false) && !shape.dims.slice(0, chartConfig.rows.length).some((d) => d.isDate && d.ordered !== false);
  const shapeKey = shape ? JSON.stringify(shape.dims.map((d) => [d.name, d.isDate, d.ordered])) + shape.measures : '';
  const viz: VizType = !rule ? 'table' : vizOverride && vizOverride.shape === shapeKey && rule.fits.includes(vizOverride.viz) ? vizOverride.viz : basePick!;
  const picked = !!rule && viz !== basePick;
  const moreKinds = offeredKinds(['card', 'scatter'], rule);
  const chooseViz = (v: VizType) => { setKindMenu(false); setVizOverride(rule && v === basePick ? null : { viz: v, shape: shapeKey }); };

  // The result area measures itself, so the chart or grid fills whatever height the page gives it.
  const [canvasH, setCanvasH] = useState(360);
  const canvasRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = canvasRef.current; if (!el) return;
    const ro = new ResizeObserver(() => setCanvasH(Math.max(160, Math.floor(el.clientHeight))));
    ro.observe(el);
    return () => ro.disconnect();
  });

  // The Speed tab names the speed of the result on screen: a measurement only while it measured that result's query,
  // otherwise the result's own run time. A measurement of an earlier query never labels a newer result (D7).
  const shownQuery = res ? (resQuery ?? res.query ?? null) : null;
  const speedIsShown = !!speedQuery && !!shownQuery && squash(speedQuery) === squash(shownQuery);
  const measuredMs = cw?.warmTotal?.n ? cw.warmTotal.avgMs : bench ? bench.warmMinMs : timings ? timings.totalMs : null;
  const speedMs = measuredMs != null && (speedIsShown || !res) ? measuredMs : res ? res.elapsedMs : null;
  const speedLabel = speedMs != null ? timingAnnouncement(speedMs) : null;
  const measureName = (exprA.match(/^\[(.+)\]$/)?.[1]) ?? config.values[0]?.name ?? 'this formula';
  // The fields the result on screen is built from, marked in the Fields list (option C's inventory).
  const fieldsInUse = useMemo(() => {
    const c = chartConfig ?? (chipsHere ? null : config);
    return new Set(c ? [...c.rows, ...c.cols, ...c.values, ...c.filters].map((f) => f.ref) : []);
  }, [chartConfig, chipsHere, config]);
  const canSaveAnswer = askOwns && outcomeOf(ask.answer) === 'answered' && (ask.answer?.rows ?? []).length > 0;
  const onExplainNumber = (p: ExplainPayload) => setCellMenu({ payload: p, x: lastPointer.current.x, y: lastPointer.current.y });

  // With no result: say what is happening. While a query runs it is running, and after an error the error above is
  // the whole message; "Press Run" under either told the person to do what they had just done.
  const resultBody = !res
    ? <Empty>{busy === 'run' || busy === 'ask' || busy === 'debug' ? 'Running the query.' : err ? '' : conn?.connected
      ? (mode === 'query' || chipsHere ? 'Press Run to see the rows here.' : wellsReady ? 'Press Run to build this result.' : 'Add a measure to build a result.')
      : 'Choose a test model below, then choose your fields or write a query.'}</Empty>
    : !chartConfig || viz === 'table'
      ? <ResultGrid columns={shownRes!.columns} rows={shownRes!.rows} height={canvasH} truncated={res.truncated} framed={false} friendly
          onCellMenu={chartConfig ? (row, ci) => { const p = explainPayloadFromRow(chartConfig, row, ci, shownRes!.columns[ci]?.format); if (p) { onExplainNumber(p); return true; } return false; } : undefined} />
      : <DaxVisual res={shownRes!} config={chartConfig} viz={viz} height={canvasH} onExplain={onExplainNumber} across={dateAcross} />;

  return (
    // The run key (Ctrl+Enter, Cmd+Enter on mac) anywhere in Queries = Run. Bubble-phase, so a CodeMirror keymap that
    // claims the key first still wins; the question field claims it too, to ask instead (H7).
    <div className="sem-tool-page h-full flex flex-col min-h-0 gap-3" data-testid="queries-page"
      onContextMenuCapture={(e) => { lastPointer.current = { x: e.clientX, y: e.clientY }; }}
      onKeyDown={(e) => {
        if ((e.target as HTMLElement | null)?.closest?.('[data-ask-field]')) return;
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key === 'Enter' && conn?.connected && !idle) {
          e.preventDefault(); e.stopPropagation(); run();
        }
      }}>
      {checkShape && <AuthorDrawer shape={checkShape} editing={checkDraft} onClose={() => { setCheckShape(null); setCheckDraft(null); }} onSaved={() => setCheckSaved(true)} />}
      {answerDraft && <AuthorDrawer shape="measureValue" handedFrom="queries" editing={answerDraft} onClose={() => setAnswerDraft(null)} onSaved={() => { setAnswerDraft(null); setCheckSaved(true); }} />}

      {/* head row: the view switch, the question (Pro) or its quiet line (Free), then the page's actions */}
      <div className="flex items-center gap-3 shrink-0">
        <Seg value={mode} onChange={switchMode} options={[['visual', 'Build'], ['query', 'DAX']]} />
        {askOn ? <AskField ask={ask} disabled={!conn?.connected || (idle && busy !== 'ask')} />
          : askGrant === 'denied' ? <AskFreeLine /> : <div className="flex-1" />}
        <div className="flex items-center gap-2 shrink-0">
          <div className="relative">
            <button type="button" aria-expanded={checkMenu} onClick={() => setCheckMenu((open) => !open)} className="sem-btn">Save measure check ▾</button>
            {checkMenu && <div className="absolute right-0 z-20 mt-1 w-[310px] rounded-(--sem-radius-card) border p-2" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
              {chipsHere ? <>
                {/* H6: while a question's parts hold the query, the only check is a number from this answer. The
                    wells' measure and filters are unrelated to it, so a check built from them would look right
                    and test the wrong thing. */}
                <CheckChoice title="Save a number from this answer" detail="Pick the number and the row it should stay at. The question's filters travel with it." onClick={() => { setCheckMenu(false); if (canSaveAnswer) setSaving({ row: 0, measure: 0 }); }} />
                {!canSaveAnswer && <div className="px-2 pb-1 sem-text-meta">{askOwns ? 'This answer has no rows to keep.' : 'You changed the query by hand. Build from the parts again to save a number from the answer.'}</div>}
              </> : <>
                <CheckChoice title="Check against SQL" detail="Compare this number with SQL you trust." onClick={() => openMeasureCheck('measureReconcile')} />
                <CheckChoice title="Check a trusted answer" detail="Save the number you expect." onClick={() => openMeasureCheck('measureValue')} />
                {/* Compare a rewrite is not a third door here: the number's compare mark and the ... menu already open it. */}
                {editedExpression && <div className="px-2 pb-1 pt-1 sem-text-meta">You have edited the formula, so the next screen asks whether to check the measure as saved or this expression.</div>}
              </>}
            </div>}
          </div>
          {idle
            ? <button onClick={stopQuery} title="Stop the running query" aria-label="Stop the running query" className="sem-btn">Stop</button>
            : <button disabled={!conn?.connected} onClick={run} title="Run the query" aria-label="Run the query" className="sem-btn sem-btn-primary">Run</button>}
        </div>
      </div>

      {/* the fields row: the fields drawer, then the question's parts or the person's own wells */}
      <div className="flex items-start gap-3 shrink-0 flex-wrap">
        <button type="button" className="sem-btn sem-btn-sm shrink-0" aria-expanded={fieldsOpen} onClick={() => setFieldsOpen(!fieldsOpen)}>{fieldsOpen ? '‹ Fields' : 'Fields ›'}</button>
        <div className="flex-1 min-w-0 flex flex-col gap-2">
          {chipsHere
            ? <AskChips ask={ask} owns={askOwns} disabled={idle} onRebuild={() => ask.askParts(ask.parts!)} onLeave={() => leaveQuestion()} />
            : mode === 'visual'
              ? <QueryWells config={config} onConfig={onWells} onCompare={compareValue} compareBadge={compareBadge} model={model} disabled={busy === 'ask'} />
              : <div className="flex items-center gap-3 flex-wrap" style={{ minHeight: 'var(--sem-control-h-sm)' }}>
                  <span className="sem-text-meta">Your own query. Autocomplete as you type; click or drag a field from Fields to insert it.</span>
                  {/* The last comparison's verdict, visible without opening the sheet, as a measure chip's ⇄ mark shows it. */}
                  {compareBadge && <span className="xe-dot sem-text-meta" style={{ '--xe-dot': compareBadge.color } as React.CSSProperties} data-testid="queries-compare-verdict">Last comparison: {compareBadge.text}</span>}
                </div>}
          {/* The statement is about the answer's own query: it holds while the chips own the query, and for a question stopped before any DAX was written only while no result is on screen. */}
          {askOn && <AskLines ask={ask} current={ask.answer?.dax ? answerRows : !res} />}
          {/* Only while the editor holds the question's own writing: over a newer query the person typed, Put back would
              throw that newer text away (verify-queries-redesign, lower items). The kept query is not dropped, so after
              Build from the parts again the offer is back (the pre-flight's finish line, lane 2 step 3). */}
          {putBack && squash(putBack) !== squash(query) && squash(query) === squash(generatedQuery) && (
            <div className="flex items-center gap-2" data-testid="queries-putback">
              <span className="sem-text-meta">The question replaced the query you wrote.</span>
              <button type="button" className="sem-btn sem-btn-sm" disabled={idle} onClick={() => { setQuery(putBack); setPutBack(''); setMode('query'); }}>Put back my query</button>
            </div>
          )}
          {saving && ask.answer && canSaveAnswer && (
            <SaveChooser answer={ask.answer} value={saving} onChange={setSaving} onCancel={() => setSaving(null)}
              onContinue={() => { const d = checkDraftOf(ask.answer!, saving.row, saving.measure, model.measures); if (d) { setAnswerDraft(d); setSaving(null); } }}
              ready={!!checkDraftOf(ask.answer, saving.row, saving.measure, model.measures)} />
          )}
        </div>
      </div>
      <div className="sr-only" aria-live="polite" data-testid="queries-ask-announce">{ask.announce}</div>

      {checkSaved && <div className="shrink-0"><Banner color="var(--sem-good)">Check saved.{onOpenTests && <button type="button" className="ml-2 xe-link" onClick={onOpenTests}>Open Validation</button>}</Banner></div>}
      {claudeEvent && <div className="shrink-0"><ClaudeRanBanner event={claudeEvent} onClear={() => setClaudeEvent(null)} /></div>}

      {/* body: the Fields drawer (when open) | the work. The result fills everything it is given. */}
      <div className="sem-grid flex-1 min-h-0" style={{ gridTemplateRows: 'minmax(0, 1fr)' }}>
        {fieldsOpen && (
          <div className="relative min-h-0 sem-card flex flex-col" style={{ gridColumn: `span ${fieldsSpan}` }} data-testid="queries-fields">
            <FieldsPanel model={model} inUse={fieldsInUse} onInsert={(t) => {
              if (mode === 'query') { editorRef.current?.insert(t); return; }
              const f = parseRef(t); if (!f) return;
              // A field clicked while a question holds the row goes to the person's own wells, and the wells
              // comes back: the click used to be dropped without a word (verify-queries-redesign D8).
              if (chipsHere) leaveQuestion(false);
              if (f.kind === 'measure' && !config.values.some((v) => v.ref === f.ref)) onWells({ ...config, values: [...config.values, f] });
              else if (f.kind === 'column' && !config.rows.some((v) => v.ref === f.ref)) onWells({ ...config, rows: [...config.rows, f] });
              else if (chipsHere) void runQuery(buildQuery(config));
            }} />
            <div onPointerDown={onFieldsDrag} title="Drag to resize" aria-hidden="true" className="absolute top-0" style={{ right: 'calc(-1 * var(--sem-gap))', width: 'var(--sem-gap)', height: '100%', cursor: 'col-resize', zIndex: 5 }} />
          </div>
        )}

        <div className="flex flex-col min-w-0 min-h-0" style={{ gridColumn: `span ${fieldsOpen ? 12 - fieldsSpan : 12}` }}>
          {mode === 'query' && <>
            <div className="sem-card shrink-0 flex flex-col min-h-0 overflow-hidden" style={{ height: editorH }} data-testid="queries-editor">
              <DaxEditor ref={editorRef} fill value={query} onChange={setQuery} model={model} minHeight={40} placeholder="EVALUATE TOPN(100, 'Sales')   ·   Ctrl+Space for suggestions" />
            </div>
            <div onPointerDown={onEditorDrag} onDoubleClick={() => setEditorH(editorH > 300 ? 180 : 420)} title="Drag to resize · double-click to change the size" className="shrink-0 relative xe-splitter" />
          </>}

          {/* the result card: Result and Speed, one row of views above one result shown once */}
          <div className="sem-card sem-card-flush flex-1 flex flex-col min-h-0" data-primary="result">
            <div className="sem-card-head shrink-0 flex items-center">
              <div className="flex items-center flex-wrap xe-tabs" role="group" aria-label="Result views">
                {WB_TABS.map((t) => {
                  const badge = t.id === 'result' ? (res ? (res.truncated ? `${res.rowCount}+` : String(res.rowCount)) : null) : speedLabel;
                  return (
                    <button key={t.id} type="button" aria-pressed={tab === t.id} onClick={() => setTab(t.id)} className="sem-tab">
                      <span>{t.label}</span>{badge && <span className="ml-2 sem-num" style={{ color: 'var(--sem-muted)' }}>{badge}</span>}
                    </button>
                  );
                })}
              </div>
              <div className="ml-auto relative pr-2">
                <button type="button" className="sem-btn sem-btn-sm" aria-label="More tools for this result" aria-haspopup="menu" aria-expanded={moreMenu} onClick={() => setMoreMenu(!moreMenu)}>⋯</button>
                {moreMenu && <MenuPop onClose={() => setMoreMenu(false)} align="right" items={[
                  { label: 'Calculation steps', detail: 'Run again and log each step of the calculation', disabled: !conn?.connected || idle, act: () => { void debug(); } },
                  { label: 'Compare a rewrite', detail: 'Prove a new formula gives the same numbers', act: () => compareValue(compareStart()) },
                  { label: 'Reset', detail: 'Back to the starting fields and query', disabled: idle, act: resetDefault },
                  { label: mode === 'query' ? 'Back to my fields' : 'Show the DAX', detail: mode === 'query' ? 'Build with the fields again' : 'See and edit the query this result ran', act: () => switchMode(mode === 'query' ? 'visual' : 'query') },
                ]} />}
              </div>
            </div>
            <div className="flex-1 min-h-0 flex flex-col">
              {err && (
                <div className="p-4 sem-stack shrink-0">
                  <Banner color="var(--sem-bad)">{plainDaxError(err)}</Banner>
                  {isSignInError(err) && session?.liveEndpoint && (
                    <button type="button" data-testid="signin-again" disabled={connBusy} className="sem-btn sem-btn-sm sem-btn-primary self-start"
                      onClick={() => {
                        void connectXmla(session.liveEndpoint!, session.liveDatabase ?? '', 'interactive', session.currentTenant ?? null)
                          .then((r) => { if (r.ok) void runQuery(currentQuery()); else if (r.message) setErr(r.message); });
                      }}>{SIGN_IN_AGAIN}</button>
                  )}
                </div>
              )}
              {tab === 'result' && <>
                {res && (
                  <div className="shrink-0 flex items-center gap-2 flex-wrap px-4 pt-3 pb-2">
                    <div className="flex items-center" role="group" aria-label="Chart kind">
                      {/* Only the kinds this result fits are shown: a button that does nothing is not on the row (K22). */}
                      {offeredKinds(MAIN_KINDS, rule).map((k) => (
                        <button key={k} type="button" aria-pressed={viz === k} onClick={() => chooseViz(k)} className="sem-seg-item" title={CHART_TITLE[k]}>
                          {CHART_TITLE[k]}{rule && k === rule.pick && <span className="ml-1 sem-doer sem-doer-engine text-(length:--sem-text-label)">rules</span>}
                        </button>
                      ))}
                      {moreKinds.length > 0 && (
                        <span className="relative">
                          <button type="button" className="sem-seg-item" aria-haspopup="menu" aria-expanded={kindMenu} onClick={() => setKindMenu(!kindMenu)}>More ▾</button>
                          {kindMenu && <MenuPop onClose={() => setKindMenu(false)} items={moreKinds.map((k) => ({ label: CHART_TITLE[k], act: () => chooseViz(k) }))} />}
                        </span>
                      )}
                    </div>
                    {rule && (
                      <span className="sem-text-meta flex items-center gap-2 min-w-0" data-testid="queries-chart-reason">
                        <span className={`sem-doer ${picked ? 'sem-doer-you' : jevHere ? 'sem-doer-agent' : 'sem-doer-engine'}`}>{picked ? 'Your pick.' : jevHere ? `Jev picked ${CHART_TITLE[jevPick!.kind]}:` : 'Picked by the chart rules:'}</span>
                        <span className="truncate">{picked ? (jevHere ? `Jev picks ${CHART_TITLE[jevPick!.kind]}. ${jevPick!.reason}` : `The rules pick ${CHART_TITLE[rule.pick]}. ${rule.reason}`)
                          : jevHere ? `${jevPick!.reason}${jevPick!.kind !== rule.pick ? ` The rules pick ${CHART_TITLE[rule.pick]}.` : ''}` : rule.reason}</span>
                        {picked && <button type="button" className="xe-link shrink-0" onClick={() => setVizOverride(null)}>{jevHere ? "Back to Jev's pick" : "Back to the rules' pick"}</button>}
                      </span>
                    )}
                    {/* Results run against the published model, not your staged edits: said in the result's own line,
                        where the mockup puts it, beside the rows it is about. */}
                    <span className="ml-auto flex items-center gap-3 min-w-0">
                      <QueryStalenessChip />
                      <span className="sem-text-meta sem-num shrink-0" title={queryIdentity(res.query || resQuery) ?? undefined}>
                        {rowAnnouncement(res.rowCount, res.truncated)} · {timingAnnouncement(res.elapsedMs)}
                      </span>
                    </span>
                  </div>
                )}
                <div ref={canvasRef} className="flex-1 min-h-0 px-4 pb-4" data-testid="queries-result">{resultBody}</div>
              </>}
              {tab === 'speed' && <SpeedView {...{ runs, setRuns, clearOnRun, setClearOnRun, confirmShared, setConfirmShared, shared: !!shared, connected: !!conn?.connected, idle, busy, clearCache, measureSpeed, runProfile, runQuick, runPlan, clearThenPlan, timings, cw, plan, bench, clearMsg, planOpen, setPlanOpen, stale: measuredMs != null && !!res && !speedIsShown }} />}
            </div>
          </div>
        </div>
      </div>

      {!res && <div className="shrink-0"><ConnectBar hint="Connect a model to run queries." /></div>}

      {cellMenu && <MenuPop at={{ x: cellMenu.x, y: cellMenu.y }} onClose={() => setCellMenu(null)} items={[
        { label: 'Explain this number', detail: `${cellMenu.payload.measureName} = ${cellMenu.payload.value}`, act: () => setExplain(cellMenu.payload) },
        { label: 'Calculation steps', detail: 'Run again and log each step of the calculation', disabled: !conn?.connected || idle, act: () => { void debug(); } },
      ]} />}
      {sheet === 'compare' && (
        <SideSheet title={`Compare a rewrite of ${measureName}`} onClose={() => setSheet(null)}>
          <CompareBody {...{ exprA, setExprA, exprB, setExprB, groupByDerived, filterLinesDerived, conn, busy, runVerify, eqEv, eqStale, vErr, customVerify, setVerifyGroupBy, subject: compareSubject, filterWordsShown: (qParts?.filters ?? []).map(filterWords) }} onUseVisual={() => setCustomVerify(false)} />
        </SideSheet>
      )}
      {sheet === 'steps' && (
        <SideSheet title="Calculation steps" onClose={() => setSheet(null)}>
          <StepsBody logs={logs} logNote={logNote} connected={!!conn?.connected} idle={idle} busy={busy} visual={mode === 'visual' && !chipsHere} onRun={() => debug()} />
        </SideSheet>
      )}
      {/* "Explain this number" slide-over (from the number's right-click menu) */}
      {explain && <ExplainPanel payload={explain} onClose={() => setExplain(null)} />}
    </div>
  );
}

// ---- Speed (D1): the answer first, then where the time went, then every run and the server steps as folds ----
// Laid out in three bands so no button ever sits inside a scroller (the space rule; verify-queries-redesign D9): the
// tools on top, the numbers in the one box that scrolls, and the Server steps fold pinned at the bottom with its own
// two buttons. Only rows of numbers and plan steps scroll.
function SpeedView(p: {
  runs: number; setRuns: (n: number) => void; clearOnRun: boolean; setClearOnRun: (b: boolean) => void; confirmShared: boolean; setConfirmShared: (b: boolean) => void;
  shared: boolean; connected: boolean; idle: boolean; busy: DaxLabBusy; clearCache: () => void; measureSpeed: () => void; runProfile: () => void; runQuick: () => void;
  runPlan: () => void; clearThenPlan: () => void; timings: ServerTimings | null; cw: ColdWarmBenchmark | null; plan: QueryPlanResult | null; bench: BenchmarkResult | null;
  clearMsg: string | null; planOpen: boolean; setPlanOpen: (b: boolean) => void; stale: boolean;
}) {
  const { runs, setRuns, clearOnRun, setClearOnRun, confirmShared, setConfirmShared, shared, connected, idle, busy, clearCache, measureSpeed, runProfile, runQuick, runPlan, clearThenPlan, timings, cw, plan, bench, clearMsg, planOpen, setPlanOpen, stale } = p;
  const off = !connected || idle;
  const [options, setOptions] = useState(false);
  const measured = !!(cw || bench || timings);
  const t = timings && timings.traceAvailable && timings.seQueries > 0 ? timings : null;
  const sePct = t && t.totalMs > 0 ? Math.round((t.seMs / t.totalMs) * 100) : 0;
  const head: [string, string][] = [];
  if (cw?.warmTotal?.n) head.push([timingAnnouncement(cw.warmTotal.avgMs), 'repeat runs']);
  if (cw?.coldTotal?.n) head.push([timingAnnouncement(cw.coldTotal.avgMs), 'first run, nothing kept']);
  // A quick timing shows whenever there is one: an assistant's benchmark used to hide behind an earlier cold and warm
  // measurement (D7). Both now describe the same query, because a measurement of another query is put away first.
  if (bench) { head.push([timingAnnouncement(bench.warmMinMs), cw ? 'quick, fastest repeat run' : 'repeat runs, fastest']); head.push([timingAnnouncement(bench.firstMs), cw ? 'quick, first run' : 'first run']); }
  if (t) { head.push([`${Math.max(0, 100 - sePct)}%`, 'formula engine']); head.push([`${sePct}%`, 'storage engine']); head.push([String(t.seQueries), `storage ${t.seQueries === 1 ? 'query' : 'queries'}${t.seCacheHits ? `, ${t.seCacheHits} from cache` : ''}`]); }
  return (
    <div className="flex-1 min-h-0 flex flex-col" data-testid="queries-speed">
      <div className="shrink-0 px-4 pt-4 pb-3 flex flex-col gap-2">
        <div className="flex items-center gap-2 flex-wrap">
          <Button primary disabled={off} onClick={measureSpeed} title="Run the query first with nothing kept, then again to see repeat speed, then record where the time went">{busy === 'measure' ? 'Measuring…' : measured ? 'Measure again' : 'Measure speed'}</Button>
          {/* One main action. The rest of the ways to time a query sit behind Options. */}
          <div className="relative">
            <Button onClick={() => setOptions(!options)} aria-expanded={options}>Options ▾</Button>
            {options && <OptionsPop onClose={() => setOptions(false)}>
              <Button disabled={off} onClick={() => { setOptions(false); runQuick(); }} title="Measure elapsed query time without clearing cached results">Quick</Button>
              <Button disabled={off} onClick={() => { setOptions(false); runProfile(); }} title="Only record where the time went: the formula and storage split and the heaviest scans">Profile only</Button>
              <Button disabled={off} onClick={() => { setOptions(false); clearCache(); }} title="Throw away results the server kept from earlier runs, so the next run starts cold. Needs a local Power BI Desktop or admin access.">Clear kept results</Button>
              <label className="sem-text-meta flex items-center gap-2" htmlFor="lab-runs">runs
                <input id="lab-runs" type="number" min={1} max={25} value={runs} onChange={(e) => setRuns(Math.max(1, Math.min(25, Number(e.target.value) || 1)))} className="sem-field sem-field-sm w-14 tnum" /></label>
              <label className="flex items-center gap-2 cursor-pointer sem-text-meta"><input type="checkbox" checked={clearOnRun} onChange={(e) => setClearOnRun(e.target.checked)} /> Clear kept results before each first run</label>
            </OptionsPop>}
          </div>
          {(busy === 'quick' || busy === 'profile' || busy === 'clear') && <span className="sem-text-meta" role="status">{busy === 'quick' ? 'Running…' : busy === 'profile' ? 'Profiling…' : 'Clearing…'}</span>}
        </div>
        {shared && <label className="flex items-center gap-2 cursor-pointer sem-text-meta" style={{ color: 'var(--sem-fg)' }}><span className="xe-dot xe-dot-warn" aria-hidden="true" /><input type="checkbox" checked={confirmShared} onChange={(e) => setConfirmShared(e.target.checked)} /> Shared endpoint: clearing the cache affects all users</label>}
        {clearMsg && <div className="text-(length:--sem-text-label) xe-dot xe-dot-good">{clearMsg}</div>}
      </div>
      <div className="flex-1 min-h-0 overflow-auto px-4 pb-3 flex flex-col gap-3" data-testid="queries-speed-numbers">
        {stale && <div className="xe-msg" style={{ '--xe-dot': 'var(--sem-warn)' } as React.CSSProperties} data-testid="queries-speed-stale">These numbers are for a query you measured before, not the result on screen. Measure again to time this result.</div>}
        {head.length > 0 && (
          <div className="flex items-baseline flex-wrap gap-x-6 gap-y-2" data-testid="queries-speed-answer">
            {head.map(([v, l]) => <span key={l} className="inline-flex items-baseline gap-2"><b className="text-(length:--sem-text-page) sem-num" style={{ fontWeight: 'var(--sem-weight-strong)' }}>{v}</b><span className="sem-text-meta">{l}</span></span>)}
          </div>
        )}
        {timings && <ServerTimingsView t={timings} />}
        {cw && <ColdWarmView cw={cw} />}
        {(cw?.detail.length || bench) ? (
          <details className="sem-inset">
            <summary className="cursor-pointer select-none flex items-center"><b className="text-xs">Every run</b><span className="ml-auto sem-text-meta">{[cw?.detail.length ? `${cw.detail.length} measured` : '', bench ? `${bench.runs} quick` : ''].filter(Boolean).join(' · ')} runs</span></summary>
            <div className="mt-2 flex flex-col gap-4">
              {cw && cw.detail.length > 0 && <RunDetail cw={cw} />}
              {bench && (
                <div className="grid grid-cols-1 md:grid-cols-[200px_1fr] gap-4 items-center">
                  <div className="flex flex-col gap-2">
                    <Metric label="first run" value={timingAnnouncement(bench.firstMs)} />
                    <Metric label="repeat runs, fastest" value={timingAnnouncement(bench.warmMinMs)} accent />
                    <Metric label="repeat runs, middle" value={timingAnnouncement(bench.warmMedianMs)} />
                    <Metric label="runs" value={String(bench.runs)} />
                  </div>
                  <BenchBars runsMs={bench.runsMs} />
                </div>
              )}
            </div>
          </details>
        ) : null}
      </div>
      <details className="sem-inset shrink-0 mx-4 mb-4" open={planOpen} onToggle={(e) => setPlanOpen((e.currentTarget as HTMLDetailsElement).open)} data-testid="queries-server-steps">
        <summary className="cursor-pointer select-none flex items-center"><b className="text-xs">Server steps</b><span className="ml-auto sem-text-meta">{plan ? `recorded ${plan.logicalTree.length + plan.physicalTree.length} steps` : ''}</span></summary>
        <div className="mt-2 flex flex-col gap-3">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="sem-text-meta">See the steps the server took to answer the query. Some servers do not provide this detail.</span>
            <div className="ml-auto flex gap-2">
              <Button disabled={off} onClick={runPlan}>{busy === 'plan' ? 'Recording…' : plan ? 'Record again' : 'Record the steps'}</Button>
              <Button disabled={off} onClick={clearThenPlan} title="Throw away results the server kept from earlier runs, then record the steps. The server may still return nothing.">{busy === 'clear' || busy === 'plan' ? '…' : 'Clear kept results, then record'}</Button>
            </div>
          </div>
          {plan ? <QueryPlanView plan={plan} maxH="min(360px, 24vh)" /> : <div className="sem-text-meta">No steps recorded yet.</div>}
          {shared && <div className="sem-text-meta">Note: query plans are often not emitted by the Power BI XMLA endpoint even for admins. <b>Measure speed</b> can still help when timing or trace data is available.</div>}
        </div>
      </details>
    </div>
  );
}

// The Speed options as a small panel under its button. Escape closes it first.
function OptionsPop({ children, onClose }: { children: React.ReactNode; onClose: () => void }) {
  useSurfaceEscape(onClose);
  return (
    <>
      <div className="fixed inset-0" style={{ zIndex: 29 }} onClick={onClose} />
      <div className="absolute left-0 flex flex-col gap-2 rounded-(--sem-radius-card) border p-3" style={{ top: '100%', marginTop: 'var(--sem-space-1)', zIndex: 30, width: 300, background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>
    </>
  );
}

function StepsBody({ logs, logNote, connected, idle, busy, visual, onRun }: { logs: EvalLogEntry[] | null; logNote: string | null; connected: boolean; idle: boolean; busy: string | null; visual: boolean; onRun: () => void }) {
  const acting = busy === 'debug';
  return (
    <div className="flex flex-col gap-3" data-testid="queries-steps">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="sem-text-meta">Logged calculation steps{logs ? ` · ${logs.length} step${logs.length === 1 ? '' : 's'}` : ''}. Running this replaces the result with the logged run.</span>
        <div className="ml-auto flex gap-2">
          <Button primary={!logs} disabled={!connected || idle} onClick={onRun}>{acting ? 'Running…' : visual ? 'Log each measure' : 'Capture calculation steps'}</Button>
        </div>
      </div>
      {acting && <div className="sem-text-meta">Running the query again and logging each step.</div>}
      {logNote && <TraceNote note={logNote} kind="debug" />}
      {(!logs || logs.length === 0) && !logNote && !acting && <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No calculation steps have been logged. Choose <b>Log each measure</b> in the Build view, or add <span className="font-mono">EVALUATEANDLOG(expr,&nbsp;"label")</span> around a part of your query.</div>}
      {logs && logs.map((e, i) => (
        <div key={i} className="sem-inset">
          <div className="flex items-baseline gap-2">
            <span className="text-xs font-semibold">{e.label || `log ${i + 1}`}</span>
            <span className="text-(length:--sem-text-label) font-mono truncate" style={{ color: 'var(--sem-muted)' }} title={e.expression}>{e.expression}</span>
            <span className="ml-auto text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }}>{e.rowCount} row{e.rowCount === 1 ? '' : 's'}</span>
          </div>
          {e.columns.length > 0 && <div className="mt-2"><ResultGrid columns={e.columns.map((c) => ({ name: c }))} rows={e.rows} height={Math.min(220, 34 + e.rows.length * 26)} filterable={false} friendly /></div>}
        </div>
      ))}
    </div>
  );
}

function CompareBody(p: any) {
  // `subject` is what the comparison is about: the wells on screen, a question's parts, or a query in the DAX view.
  // The body used to name the Values well in both views, and in the DAX view no wells are on screen (M25,
  // walkthrough 2026-09-14); under a question it named the hidden wells (verify-queries-redesign D5).
  const { exprA, setExprA, exprB, setExprB, groupByDerived, filterLinesDerived, filterWordsShown = [], conn, busy, runVerify, eqEv, eqStale, vErr, customVerify, setVerifyGroupBy, onUseVisual, subject } = p;
  const own = subject === 'question' ? 'the question\'s split' : 'the split of your fields';
  const [choosingFields, setChoosingFields] = useState(false);
  const grain = grainWords(groupByDerived, useDaxModelContext().columns);   // the chips are the fields only: say so when one has a sort order (join 6, J4)
  const addFields = (nodes: BrowserNode[]) => setVerifyGroupBy([...new Set([...groupByDerived, ...nodes.map((n) => `'${n.table.split("'").join("''")}'[${n.name.split(']').join(']]')}]`)])]);
  return (
    <div className="flex flex-col gap-3" data-testid="queries-compare">
      <LabeledMono label={subject === 'question' ? 'Original formula: the number the question shows' : subject === 'wells' ? 'Original formula: the measure you chose' : 'Original formula'} value={exprA} onChange={setExprA} />
      <LabeledMono label="Rewritten formula" value={exprB} onChange={setExprB} />
      <div className="text-(length:--sem-text-label) sem-inset" style={{ color: 'var(--sem-muted)' }}>
        <div className="flex gap-2 items-center flex-wrap">
          <b style={{ color: 'var(--sem-fg)' }}>{customVerify ? 'Tested across the fields you chose' : (subject !== 'dax' ? `Tested across ${own}` : 'Tested across the fields you choose')}</b>
          <Button onClick={() => setChoosingFields(!choosingFields)}>{choosingFields ? 'Done choosing' : 'Choose fields'}</Button>
          {customVerify && subject !== 'dax' && <Button onClick={onUseVisual}>Use the result's fields</Button>}
        </div>
        <div className="flex gap-2 flex-wrap mt-2">
          {groupByDerived.map((field: string) => <span key={field} className="sem-chip sem-chip-outline">{friendlyHeader(field)} <button aria-label={'Remove ' + field} onClick={() => setVerifyGroupBy(groupByDerived.filter((f: string) => f !== field))}>×</button></span>)}
          {!groupByDerived.length && <span>Grand total only. Choose fields for a useful comparison.</span>}
        </div>
        {grain && <div className="mt-1" data-testid="compare-grain">{grain}</div>}
        {subject === 'question' && filterWordsShown.length > 0 && <div className="mt-1">Filters from the question: {filterWordsShown.join(', ')}</div>}
        {subject === 'wells' && filterLinesDerived.length > 0 && <div className="mt-1">Filters from your fields: {filterLinesDerived.join(' && ')}</div>}
        {subject === 'dax' && filterLinesDerived.length > 0 && <div className="mt-1">Filters applied to every context: {filterLinesDerived.join(' && ')}</div>}
      </div>
      {choosingFields && <ObjectBrowser kinds={['column']} multiSelect dragEnabled={false} height={220} onPick={(n) => addFields([n])} onPickMany={addFields} />}
      <div className="flex"><div className="ml-auto"><Button primary disabled={!conn?.connected || busy === 'verify'} onClick={runVerify}>{busy === 'verify' ? 'Comparing…' : 'Compare'}</Button></div></div>
      {vErr && <Banner color="var(--sem-bad)">{vErr}</Banner>}
      {!eqEv && !vErr && <div className="sem-text-meta">If the rewrite differs, a table lists each combination with both numbers. Other results say plainly why nothing was proven, and the verdict turns stale the moment a formula, field, filter or connection changes.</div>}
      {eqEv && (() => {
        const eq: EquivalenceResult = eqEv.result;
        // The verdict comes from the evidence's OWN stamp (the grid it ran with), never the current config,
        // and evidence invalidated by any expression/grid/filter edit renders stale, never green.
        const verdict = eqVerdict(eqEv);
        if (eqStale) {
          return (
            <Banner color="var(--sem-warn)">
              {`This comparison is out of date: the formulas, fields, filters, connection, or model changed since it ran (its verdict was: ${verdict === 'noContext' ? 'unproven' : verdict}). Compare again for a result that matches what you see.`}
            </Banner>
          );
        }
        const color = verdict === 'proven' ? 'var(--sem-good)' : verdict === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)';
        const text: Record<EqVerdict, string> = {
          proven: `Same numbers in all ${eq.rowsCompared} combinations.`,
          failed: `Not the same: ${eq.mismatchCount} of ${eq.rowsCompared} combinations differ. Do not apply.`,
          degraded_mismatch: `A difference showed in ${eq.mismatchCount} combination(s), but the comparison ran with reduced fidelity, which can itself cause a difference. Not proven; do not apply on this comparison.`,
          degraded: `The numbers matched, but the comparison ran with reduced fidelity. Not proven.${eq.fidelity ? ' ' + eq.fidelity : ''}`,
          thin: `Matched at the grand total only. Not proven: choose fields for a comparison per combination.`,
          unverified: `Could not prove it: ${eq.truncated ? `the comparison hit the row limit (${eq.rowsCompared} rows), so it did not cover everything` : 'the comparison compared 0 rows, so nothing was proven'}.`,
          noContext: `Unproven: this result came from an assistant run without its fields, so a proof per combination cannot be claimed. Compare again here for a full result.`,
        };
        return (
        <>
          <Banner color={color}>{text[verdict]}</Banner>
          {eq.mismatches.length > 0 && (
            <div className="overflow-auto" style={{ maxHeight: 320 }}>
              <table className="text-xs border-collapse w-full">
                <thead><tr>{['Combination', 'Original', 'Rewrite'].map((h) => <th key={h} className="text-left px-2 py-1 sticky top-0" style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)' }}>{h}</th>)}</tr></thead>
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

// A side sheet over the right of Queries: the result stays in view beside it. Escape closes it (surface stack).
function SideSheet({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  const closeRef = useRef<HTMLButtonElement>(null);
  // The control that opened the sheet gets the focus back when it closes, by Escape or by its close button. Moving the
  // focus to the close button left it on the page body after closing (verify-queries-redesign D15).
  const opener = useRef<HTMLElement | null>(typeof document !== 'undefined' ? document.activeElement as HTMLElement | null : null);
  useSurfaceEscape(onClose);
  useEffect(() => {
    closeRef.current?.focus();
    return () => { const o = opener.current; if (o && o.isConnected && o !== document.body) o.focus(); };
  }, []);
  return (
    <div role="dialog" aria-label={title} data-testid="queries-sheet" className="fixed inset-y-0 right-0 flex flex-col" style={{
      width: 'min(560px, calc(100vw - 32px))', zIndex: 65, background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)',
      boxShadow: '-18px 0 44px -24px rgba(0,0,0,.7)',
    }}>
      <div className="flex items-center gap-2 px-4 shrink-0" style={{ height: 'var(--sem-row-2)', borderBottom: '1px solid var(--sem-border)' }}>
        <b className="text-(length:--sem-text-title) truncate">{title}</b>
        <button ref={closeRef} type="button" className="ml-auto sem-btn sem-btn-sm sem-btn-quiet" aria-label={`Close ${title}`} onClick={onClose}>✕</button>
      </div>
      <div className="flex-1 min-h-0 overflow-auto p-4">{children}</div>
    </div>
  );
}

// ---- server timings (FE/SE split) -----------------------------------------------------------
function TraceNote({ note, kind }: { note: string; kind?: 'profileEmpty' | 'profileUnavailable' | 'scanDetailUnavailable' | 'debug' }) {
  const diagnostic = note.indexOf('[trace:');
  const technical = diagnostic < 0 ? note : note.slice(diagnostic);
  const original = diagnostic < 0 ? note : note.slice(0, diagnostic).trim();
  const primary = kind === 'profileEmpty'
    ? 'No storage scan was captured. The answer may have come from cache or model metadata, so this profile only proves the elapsed time.'
    : kind === 'profileUnavailable'
      ? 'This endpoint returned elapsed time only. Storage work was not measured, so no storage-scan conclusion is available.'
    : kind === 'scanDetailUnavailable'
      ? 'Storage work was measured, but this endpoint did not return the individual scan details.'
    : kind === 'debug'
      ? 'The query returned a result, but it did not produce any logged calculation steps.'
      : original;
  return <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>
    {primary}
    {(kind || diagnostic >= 0) && <details className="mt-1"><summary className="cursor-pointer">Technical details</summary><code className="break-all">{technical}</code></details>}
  </div>;
}

/** One swatch plus the plain-words name of an engine. The bar hides a segment's own label once the segment
 *  gets narrow, so the two names have to live somewhere that does not depend on the split. */
function EngineKey({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="inline-flex items-center gap-2">
    <span aria-hidden="true" style={{ width: 9, height: 9, borderRadius: 'var(--sem-radius-chip)', background: color, flex: 'none' }} />
    {children}
  </span>;
}

// A scan's kind in words. The trace reports a subclass name, or on some servers only its number ("176" on screen,
// verify-queries-redesign lower items), and neither is a word a person uses.
function scanKind(subclass?: string): string {
  const k = subclass ?? '';
  if (/cache/i.test(k)) return 'From cache';
  if (/internal/i.test(k)) return 'Internal scan';
  if (/directquery|sql/i.test(k)) return 'Source query';
  return 'Scan';
}

function ServerTimingsView({ t }: { t: ServerTimings }) {
  const sePct = t.totalMs > 0 ? Math.round((t.seMs / t.totalMs) * 100) : 0;
  const fePct = Math.max(0, 100 - sePct);
  return (
    <div className="sem-inset">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Where the time went</span>
        {!t.traceAvailable && <span className="text-(length:--sem-text-label) xe-dot xe-dot-warn">Elapsed time only: {t.wallMs ?? t.totalMs} ms</span>}
      </div>
      {!t.traceAvailable && <TraceNote note={t.note || 'Storage timing data was unavailable.'} kind="profileUnavailable" />}
      {t.traceAvailable && (
        <>
          {t.seQueries > 0 && <>
            <div className="flex h-5 rounded overflow-hidden text-(length:--sem-text-label)" style={{ background: 'var(--sem-surface)' }} title={`Formula engine ${t.feMs} ms · storage engine ${t.seMs} ms`}>
              <div className="flex items-center justify-center" style={{ width: `${fePct}%`, background: 'var(--sem-engine-fe)', color: 'var(--sem-on-engine-fe)', minWidth: fePct > 0 ? 28 : 0 }}>{fePct > 14 ? `Formula ${fePct}%` : ''}</div>
              <div className="flex items-center justify-center" style={{ width: `${sePct}%`, background: 'var(--sem-engine-se)', color: 'var(--sem-on-engine-se)', minWidth: sePct > 0 ? 28 : 0 }}>{sePct > 14 ? `Storage ${sePct}%` : ''}</div>
            </div>
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
              <EngineKey color="var(--sem-engine-fe)">Formula engine</EngineKey>
              <EngineKey color="var(--sem-engine-se)">Storage engine</EngineKey>
            </div>
          </>}
          <div className="flex flex-wrap gap-x-4 gap-y-1 mt-2 text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }}>
            <span><b style={{ color: 'var(--sem-fg)' }}>{t.totalMs} ms</b> server query</span>
            {t.wallMs != null && <span>{t.wallMs} ms round trip</span>}
            {t.setupMs != null && t.setupMs >= 0 && <span>{t.setupMs} ms trace setup</span>}
            {t.seQueries > 0 && <><span><b style={{ color: 'var(--sem-engine-fe)' }}>{t.feMs} ms</b> formula engine, estimated</span>
            <span><b style={{ color: 'var(--sem-engine-se)' }}>{t.seMs} ms</b> storage engine</span></>}
            <span><b style={{ color: 'var(--sem-fg)' }}>{t.seQueries}</b> storage {t.seQueries === 1 ? 'query' : 'queries'}</span>
            {t.seCacheHits != null && <span><b style={{ color: 'var(--sem-engine-se)' }}>{t.seCacheHits}</b> from the storage cache</span>}
            <span>{t.seCpuMs} ms storage CPU</span>
            {t.seParallelism > 0 && <span>{t.seParallelism}× parallelism</span>}
          </div>
          {t.scans.length === 0 && <TraceNote note={t.note || 'No storage scan events were returned.'} kind={t.seQueries === 0 ? 'profileEmpty' : 'scanDetailUnavailable'} />}
          {t.scans.length > 0 && t.note && <TraceNote note={t.note} />}
          {t.scans.length > 0 && (
            <div className="mt-2">
              <div className="text-(length:--sem-text-label) mb-1" style={{ color: 'var(--sem-muted)' }}>Heaviest scans</div>
              <div className="overflow-auto" style={{ maxHeight: 220 }}>
                <table className="w-full text-(length:--sem-text-label) border-collapse">
                  <thead><tr>{['#', 'Kind', 'Duration', 'CPU', 'Rows', 'Storage query'].map((h, i) => (
                    <th key={h} className={(i >= 2 && i <= 4 ? 'text-right' : 'text-left') + ' px-2 py-1 sticky top-0'} style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)', color: 'var(--sem-muted)' }}>{h}</th>
                  ))}</tr></thead>
                  <tbody>
                    {t.scans.map((s, i) => (
                      <tr key={i}>
                        <td className="text-left px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{s.line ?? i + 1}</td>
                        <td className="text-left px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{scanKind(s.subclass)}</td>
                        <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-engine-se)' }}>{s.durationMs} ms</td>
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
  // The rows are coloured cold against warm, which is a different question from which engine did the work.
  // The optional swatch is what carries the engine identity, so the storage engine is the same colour here
  // as it is in the split bar above.
  const statTable = (title: string, cold: ColdWarmStats, warm: ColdWarmStats, engine?: string) => (
    <div>
      <div className="flex items-center gap-2 text-(length:--sem-text-label) font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>
        {engine && <span aria-hidden="true" style={{ width: 9, height: 9, borderRadius: 'var(--sem-radius-chip)', background: engine, flex: 'none' }} />}
        {title}
      </div>
      <table className="w-full text-xs border-collapse">
        <thead><tr>{['', 'Average', 'Spread', 'Fastest', 'Slowest'].map((h, i) => (
          <th key={h} className={(i === 0 ? 'text-left' : 'text-right') + ' px-2 py-1'} style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)', fontWeight: 600 }}>{h}</th>
        ))}</tr></thead>
        <tbody>
          {([['First run', cold, 'var(--sem-warn)'], ['Repeat runs', warm, 'var(--sem-good)']] as const).map(([label, s, c]) => (
            <tr key={label}>
              <td className="text-left px-2 py-1 font-medium" style={{ borderBottom: '1px solid var(--sem-rule)' }}><span className="xe-dot" style={{ '--xe-dot': c } as React.CSSProperties}>{label}</span></td>
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
      {cw.cacheClearAvailable && cw.note && <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{cw.note}</div>}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {statTable('Whole query', cw.coldTotal, cw.warmTotal)}
        {cw.traceAvailable && statTable('Storage engine', cw.coldSe, cw.warmSe, 'var(--sem-engine-se)')}
      </div>
    </div>
  );
}

function RunDetail({ cw }: { cw: ColdWarmBenchmark }) {
  return (
<div className="overflow-auto mt-1" style={{ maxHeight: 260 }}>
            <table className="w-full text-xs border-collapse">
              <thead><tr>{['Run', 'Kind', 'Whole query', 'Storage', 'Storage queries'].map((h, i) => (
                <th key={h} className={(i < 2 ? 'text-left' : 'text-right') + ' px-2 py-1 sticky top-0'} style={{ background: 'var(--sem-ground)', borderBottom: '1px solid var(--sem-rule)', color: 'var(--sem-muted)' }}>{h}</th>
              ))}</tr></thead>
              <tbody>
                {cw.detail.map((d, i) => (
                  <tr key={i}>
                    <td className="text-left px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)' }}>{d.index}</td>
                    <td className="text-left px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)', color: d.cold ? 'var(--sem-warn)' : 'var(--sem-good)' }}>{d.cold ? 'first' : 'repeat'}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)' }}>{timingAnnouncement(d.totalMs)}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{timingAnnouncement(d.seMs)}</td>
                    <td className="text-right px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-muted)' }}>{d.seQueries}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
  );
}

// ---- query plans (logical + physical) --------------------------------------------------------
function QueryPlanView({ plan, maxH = 360 }: { plan: QueryPlanResult; maxH?: number | string }) {
  const tree = (title: string, nodes: QueryPlanNode[]) => (
    <div className="flex-1 min-w-0">
      <div className="text-(length:--sem-text-label) font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>{title} <span className="opacity-70">({nodes.length})</span></div>
      <div className="overflow-auto text-(length:--sem-text-label)" style={{ maxHeight: maxH, background: 'var(--sem-surface)' }}>
        {nodes.length === 0
          ? <div className="px-2 py-2" style={{ color: 'var(--sem-muted)' }}>No result</div>
          : nodes.map((n, i) => (
            <div key={i} className="px-2 py-0 whitespace-nowrap flex items-center gap-2 font-mono" style={{ paddingLeft: 'var(--sem-space-2)' + Math.min(n.level, 24) * 8 }} title={n.detail}>
              <span style={{ color: 'var(--sem-fg)' }}>{n.operator}</span>
              {n.records != null && <span className="tnum xe-dot xe-dot-good">#{n.records.toLocaleString()}</span>}
            </div>
          ))}
      </div>
    </div>
  );
  return (
    <div className="sem-inset">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Query plan</span>
        {!plan.traceAvailable && <span className="text-(length:--sem-text-label) xe-dot xe-dot-warn">{plan.note || 'unavailable on this endpoint (needs local Power BI Desktop / admin XMLA)'}</span>}
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
function CheckChoice({ title, detail, onClick }: { title: string; detail: string; onClick: () => void }) {
  return <button type="button" onClick={onClick} className="mb-1 block w-full rounded-(--sem-radius-control) px-2 py-1 text-left hover:bg-[var(--sem-hover)]">
    <span className="block text-xs font-semibold">{title}</span>
    <span className="mt-1 block text-(length:--sem-text-label) leading-4" style={{ color: 'var(--sem-muted)' }}>{detail}</span>
  </button>;
}

function Seg<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <div className="sem-seg" role="group" aria-label="DAX Lab mode">
      {options.map(([v, label]) => (
        <button key={v} type="button" aria-pressed={value === v} onClick={() => onChange(v)} className="sem-seg-item">{label}</button>
      ))}
    </div>
  );
}
function LabeledMono({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  const model = useDaxModelContext();
  return (
    <div className="flex flex-col gap-1">
      <label className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{label}</label>
      <DaxEditor value={value} onChange={onChange} model={model} lineNumbers={false} minHeight={44} />
    </div>
  );
}
function Empty({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center text-center sem-text-meta p-4"><div>{children}</div></div>;
}
function Button({ children, onClick, primary, disabled, title, 'aria-expanded': expanded }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string; 'aria-expanded'?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title} aria-expanded={expanded} className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>{children}</button>
  );
}
// A message is a sentence with a coloured dot, not coloured text in a tinted box (Instrument).
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="xe-msg" style={{ '--xe-dot': color } as React.CSSProperties}>{children}</div>;
}
function Metric({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <span className="text-(length:--sem-text-body) font-semibold tnum" style={{ color: accent ? 'var(--sem-accent)' : 'var(--sem-fg)' }}>{value}</span>
    </div>
  );
}
