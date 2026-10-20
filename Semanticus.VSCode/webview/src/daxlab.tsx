import { isProbeDax, type SaidHeadline } from './query-notebook-model';
import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import { splitEngineNote, plainClearRefusal } from './enginewords';
import { usePersistedState, useResizable } from './hooks';
import { useConnection } from './connection';
import { useClaudeReflection, type ActivityEvent } from './activity';
import { DaxEditor, useDaxModelContext, type DaxEditorHandle } from './daxeditor';
import { ObjectBrowser, type BrowserNode } from './objectbrowser';
import type { ResultSet } from './wire';
import { timingAnnouncement } from './wire';
import { measuredBench } from './benchtiming.mjs';
import { PivotConfig, EMPTY_CONFIG, buildQuery, buildInstrumentedQuery, filterToDax, withSortBy } from './daxwells';
import { explainPayloadFromRow } from './daxvisual';
import type { Kind } from './visuals';
import { configOfResult, logRunUsable, queryShape, queryVisualFits, visualData } from './query-model';
import { checkFieldsOfDax } from './query-check-fields';
import { axisDims, foldSortColumns } from './axisorder';
import { useQueriesAsk, NO_ASK } from './queriesask';
import { squash } from './daxsquash';
import { withMeasureFormats, editingFormatsApply, measuresNotShadowed, queriesLinkedCopy, measureFormatsFromDmv, sentFormatsNow, MEASURE_FORMATS_DMV, type SentFormats } from './resultformats';
import { notesStore } from './studio/notes-runtime';
import { answerOwnsResult, producedByOf } from './resultownership';
import { answerFilterLines, cleanParts, outcomeOf, pivotConfigOf, seedForMeasureFocus, wellsOfQuestion, type CheckDraft, type DataAnswer } from './askdata-model';
import { calcCautionApplies, calcCautionLine, compareOriginal, differenceLine, plainCombination, rewriteFormat, shownNumber } from './compare-model';
import { isFast, waitMs } from './query-parts';
import { useLearn } from './studio/learn-state';
import { answerIsFor, useAssistantAnswer } from './studio/answer-store';
import { useFeature } from './pro';
import { engineShape } from './chartpick.mjs';
import { friendlyHeader } from './resultformat.mjs';
import { grainWords } from './sortgrain';
import type { ExplainPayload } from './explainpanel';
import { type AuthorShape, type TestDefinitionW } from './tests-authoring';

// Queries (the old DAX Lab), redesigned 27 Sep with diagnostics D1, and on 8 Oct the sentence gave way to labelled wells.
// One head row (Build or DAX, the Pro question box, Save measure check, Reset, Run), one fields row (the four wells
// Rows / Columns / Measures / Filters, or the question's parts), and ONE result that fills the rest: Result and Speed are its only two views. Calculation steps
// open from a number's right-click and the more menu; Compare a rewrite from a measure chip, the DAX view and the
// Save measure check menu, both as side sheets, so the result stays in view.
// Mockup: ~/Downloads/semanticus-queries-redesign-mockups.html (#C, #D1).

// wire types (camelCase, from Semanticus.Engine)
export interface EvalLogEntry { label?: string; expression?: string; rowCount: number; columns: string[]; rows: unknown[][]; raw?: string; }
export interface EvalLogResult { traceAvailable: boolean; entries: EvalLogEntry[]; resultColumns: { name: string; type?: string }[]; resultRows: unknown[][]; resultRowCount: number; elapsedMs: number; note?: string; error?: string; }
export interface BenchmarkResult { runs: number; firstMs: number; warmMinMs: number; warmMedianMs: number; runsMs: number[]; rowCount: number; note?: string; error?: string; }
export interface ScanEvent { line?: number; kind: string; subclass?: string; durationMs: number; cpuMs: number; rows: number; query?: string; }
export interface ServerTimings { totalMs: number; wallMs?: number; setupMs?: number; feMs: number; seMs: number; seCpuMs: number; seQueries: number; seCacheHits?: number; seParallelism: number; rowCount: number; scans: ScanEvent[]; traceAvailable: boolean; note?: string; error?: string; }
export interface EquivalenceMismatch { context: string; valueA: string; valueB: string; }
export interface EquivalenceResult { allMatch: boolean; rowsCompared: number; mismatchCount: number; truncated?: boolean; fidelity?: string | null; mismatches: EquivalenceMismatch[]; query?: string; error?: string; }

// Equivalence EVIDENCE with its provenance stamped at receipt time: the verdict is computed ONCE from the
// grid the comparison actually RAN with (not whatever grid is currently configured); the request key
// identifies the exact request and the context key the EXECUTION CONTEXT (query connection identity +
// editing session/model revision), so an expression/grid/filter edit, a connection swap to a different
// model, or another door editing the model (didChange bumps the session revision, which the
// ConnectionProvider refreshes into these signals) all mark the evidence STALE instead of leaving a green
// verdict up against a world it no longer describes. Reflected MCP results carry no REQUEST provenance
// (gridCount/requestKey null) but do get the context stamp, they executed on this same engine.
export interface EqEvidence { result: EquivalenceResult; gridCount: number | null; requestKey: string | null; contextKey: string | null; }

// The effective grid: trim + drop blanks (matches the engine's NormalizeGroupBy).
function normGrid(grid: string[]): string[] { return (grid ?? []).map((g) => (g ?? '').trim()).filter(Boolean); }

const VERIFY_MAX_ROWS = 100000;

// The DAX the person actually edited in the lab, or '' when they edited nothing worth saving as its own check.
// Query mode holds a whole query; the lab seeds a measure as EVALUATE ROW("Name", [Name]), and a query still in
// that shape, or one holding only the measure's own saved formula, is the measure as saved, not a new idea.
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

// Whether the calculation picked in the wells is what the editor holds. A query the lab wrote is its wells; a typed one is about the
// calculation only when it names it. Opening a check from "COUNTROWS(fact_sales)" with an unrelated picked measure and its wells' groups
// asked about something the person never wrote (UAT 18 Oct, v86), so such a check opens empty and the person picks.
export function labQueryIsAbout(mode: string, query: string, measureName: string, generatedQuery?: string): boolean {
  if (mode !== 'query') return true;
  const text = (query ?? '').trim();
  if (!text) return true;
  if (generatedQuery && squash(text) === squash(generatedQuery)) return true;
  return !!measureName && text.toLowerCase().includes(`[${measureName.replace(/]/g, ']]')}]`.toLowerCase());
}

// Request identity for staleness checks: the EXACT serialized request (collision-free by construction).
function requestKeyOf(exprA: string, exprB: string, grid: string[], filters: string[], maxRows: number): string {
  return JSON.stringify([exprA, exprB, grid, filters, maxRows]);
}

// Mirrors the engine's ONE evidence ladder (DaxBench.ClassifyEquivalenceEvidence): fidelity before mismatch
// (under a degraded comparison the surrogate itself can cause divergence, an observation, not a conviction),
// degraded above thin. Only 'proven' may render the green apply affordance; 'failed' is the red conviction;
// everything else is amber "not verified". 'noContext' = a clean match WITHOUT request provenance (a reflected
// MCP result): proven-vs-thin is unknowable, so it renders unproven, never green.
export type EqVerdict = 'proven' | 'failed' | 'degraded_mismatch' | 'degraded' | 'thin' | 'unverified' | 'noContext';
export function eqVerdict(ev: EqEvidence): EqVerdict {
  const eq = ev.result;
  if (eq.error) return 'unverified';
  // A calculation-group caution that neither formula can trigger is not a caution (UAT 18 Oct, N11).
  const cautious = !!eq.fidelity && calcCautionApplies(eq.fidelity, ev.requestKey);
  if (!eq.allMatch && cautious) return 'degraded_mismatch';
  if (!eq.allMatch) return 'failed';                 // grid-independent: a mismatch is classifiable without provenance
  if ((eq.rowsCompared ?? 0) <= 0) return 'unverified';
  if (eq.truncated) return 'unverified';
  if (cautious) return 'degraded';
  if (ev.gridCount == null) return 'noContext';      // clean match, no provenance, cannot claim per-context proof
  if (ev.gridCount === 0) return 'thin';
  return 'proven';
}
/** A clear the server would not do, with the place it was pressed: the Steps card or the Speed options. */
export interface ClearRefusal { text: string; where: 'speed' | 'steps'; }
export interface ClearCacheResult { cleared: boolean; local: boolean; dataSource?: string; database?: string; elapsedMs: number; note?: string; error?: string; }
export interface ColdWarmStats { n: number; avgMs: number; stdDevMs: number; minMs: number; maxMs: number; runsMs: number[]; }
export interface ColdWarmRun { index: number; cold: boolean; totalMs: number; seMs: number; seQueries: number; /** True for the traced run; the others were timed on the wall clock alone and have no storage figure. */ profiled?: boolean; }
export interface ColdWarmBenchmark { runs: number; cacheClearAvailable: boolean; traceAvailable: boolean; coldTotal: ColdWarmStats; warmTotal: ColdWarmStats; coldSe: ColdWarmStats; warmSe: ColdWarmStats; detail: ColdWarmRun[]; rowCount: number; note?: string; error?: string; }
export interface QueryPlanNode { level: number; operator: string; detail: string; records?: number; }
export interface QueryPlanResult { traceAvailable: boolean; logicalPlan?: string; physicalPlan?: string; logicalTree: QueryPlanNode[]; physicalTree: QueryPlanNode[]; totalMs: number; rowCount: number; note?: string; error?: string; }

export type Mode = 'visual' | 'query';
// Two views above the result (Kane, 27 Sep, diagnostics D1): the result, and its speed. Server steps fold inside
// Speed; Calculation steps and Compare a rewrite open as side sheets from the thing they are about.
type WbTab = 'result' | 'speed';
type Sheet = 'compare' | 'steps' | null;
export type DaxLabBusy = 'run' | 'ask' | 'measure' | 'debug' | 'profile' | 'plan' | 'quick' | 'coldwarm' | 'clear' | 'verify' | null;

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
  clearRefusal: ClearRefusal | null;
  setClearRefusal: React.Dispatch<React.SetStateAction<ClearRefusal | null>>;
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
  speedFor: (query: string | null | undefined, byAssistant?: boolean) => void;
  /** True while the numbers on Speed are ones an assistant measured on purpose, for a query that is not the result on screen. */
  speedByAssistant: boolean;
  /** The DAX an assistant last ran on this page. The page shows it, read-only, for as long as its rows are the result on screen. */
  assistantDax: string | null;
  setAssistantDax: React.Dispatch<React.SetStateAction<string | null>>;
  /** Counts every query the assistant ran, answers and checks alike, so the notebook reads its rows again for each. */
  ranSeq: number;
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
  const [clearRefusal, setClearRefusal] = useState<ClearRefusal | null>(null);
  const [eqEv, setEqEv] = useState<EqEvidence | null>(null);
  const [vErr, setVErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<DaxLabBusy>(null);
  const [err, setErr] = useState<string | null>(null);
  const [claudeEvent, setClaudeEvent] = useState<ActivityEvent | null>(null);
  const [assistantDax, setAssistantDax] = useState<string | null>(null);
  const [ranSeq, setRanSeq] = useState(0);
  // The query every number on Speed describes. Each speed tool stamps it before its numbers land, and numbers about a
  // different query are put away first, so one Speed view never shows two queries' numbers as one answer (a person's
  // cold and warm runs beside an assistant's profile of another query, verify-queries-redesign D7).
  const [speedQuery, setSpeedQuery] = useState<string | null>(null);
  const speedRef = useRef<string | null>(null);
  const [speedByAssistant, setSpeedByAssistant] = useState(false);
  const speedFor = (query: string | null | undefined, byAssistant = false) => {
    const q = query ?? null;
    if (squash(speedRef.current) !== squash(q)) { setCw(null); setBench(null); setTimings(null); setPlan(null); }
    speedRef.current = q; setSpeedQuery(q); setSpeedByAssistant(byAssistant);
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
    setRanSeq((n) => n + 1);
    // A query the assistant ran only to CHECK something never replaces what is on screen (UAT 17 Oct, P42b + F57): its one-row result took the
    // place of the answer it checked, and showed "Blank" under the person's eyes. It still becomes a row, folded away in the notebook.
    if (r && !e.error && !r.error && res && isProbeDax(r.query ?? e.query)) return;
    // A run that did not happen (held for the person's approval, refused, or failed) leaves the person's own result
    // where it is. The banner says what the assistant tried and why it did not run; the result area never shows an
    // empty result or an approval line written for the assistant, and Speed never times a query that did not run.
    if (!r || e.error || r.error || (r as { policyRefused?: boolean }).policyRefused || r.cancelled) { setClaudeEvent(e); return; }
    // The assistant's rows are the result now: Result is the view, whatever the person was looking at before (Kane, 13 Oct).
    setRes(r); setResQuery(r.query ?? e.query ?? null); setLogs(null); setErr(null); setClaudeEvent(e);
    setAssistantDax(r.query ?? e.query ?? null); setTab('result');
    // Numbers an assistant measured for some other query do not stay beside this result.
    if (squash(speedRef.current) !== squash(r.query ?? e.query ?? null)) setSpeedByAssistant(false);
  });
  useClaudeReflection('evaluate_and_log', (e) => {
    const r = e.result as EvalLogResult | undefined; const rows = r?.resultRows ?? [];
    setRes(r ? { columns: r.resultColumns, rows, rowCount: r.resultRowCount, truncated: (r.resultRowCount ?? rows.length) > rows.length, elapsedMs: r.elapsedMs, query: e.query } : null);
    setResQuery(e.query ?? null);
    setLogs(r?.entries ?? null); setLogNote(r?.note ?? null); setErr(e.error ?? null); setClaudeEvent(e); setSheet('steps');
  });
  useClaudeReflection('profile_dax', (e) => { if (e.result) { speedFor(e.query, true); setTimings(e.result as ServerTimings); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('benchmark_dax', (e) => { if (e.result) { const r = e.result as BenchmarkResult; speedFor(e.query, true); setBench(measuredBench(r)); setErr(r.error ?? e.error ?? null); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('benchmark_coldwarm', (e) => { if (e.result) { speedFor(e.query, true); setCw(e.result as ColdWarmBenchmark); setClaudeEvent(e); setTab('speed'); } });
  useClaudeReflection('capture_query_plan', (e) => { if (e.result) { speedFor(e.query, true); setPlan(e.result as QueryPlanResult); setClaudeEvent(e); setTab('speed'); setPlanOpen(true); } });
  useClaudeReflection('clear_cache', (e) => { const r = e.result as ClearCacheResult | undefined; if (r) { setClearMsg(r.cleared ? `Cleared the cache (${r.elapsedMs} ms)` : null); setClearRefusal(r.cleared ? null : { text: plainClearRefusal(r.error), where: 'speed' }); setClaudeEvent(e); } });
  useClaudeReflection('verify_equivalence', (e) => { if (e.result) { setEqEv({ result: e.result as EquivalenceResult, gridCount: null, requestKey: null, contextKey: execContextKey }); setVErr(e.error ?? null); setClaudeEvent(e); setSheet('compare'); } });

  return (
    <DaxLabTabStateContext.Provider value={{
      tab, setTab, sheet, setSheet, planOpen, setPlanOpen, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
      plan, setPlan, timings, setTimings, clearMsg, setClearMsg, clearRefusal, setClearRefusal, eqEv, setEqEv, vErr, setVErr,
      busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey, formatScopeKey, sentFormats, noteSent, speedQuery, speedFor, assistantDax, setAssistantDax, speedByAssistant, ranSeq,
    }}>
      {children}
    </DaxLabTabStateContext.Provider>
  );
}

export interface QueryPageProps { navTarget?: { ref: string; dax?: string; nonce: number } | null; navWords?: { words: string; nonce: number } | null; onNavConsumed?: (nonce: number) => void; onOpenTests?: () => void }
// The Query page's state and handlers (Ask your data and Queries became one page on 1.6). query.tsx draws it.
export function useQueryPage({ navTarget, navWords, onNavConsumed, onOpenTests }: QueryPageProps) {
  const { conn, session, context, connectXmla, busy: connBusy } = useConnection();
  const model = useDaxModelContext();
  // The measure and column names, so an error about a name that is not there can say which one was meant (UAT 18 Oct, N5).
  const fieldNames = useMemo(() => [...model.measures.map((m) => m.name), ...model.columns.map((c) => c.name)], [model]);
  const editorRef = useRef<DaxEditorHandle>(null);
  const runGen = useRef(0);
  const askGrant = useFeature('askData');

  const persistKey = session?.sessionId ?? 'no-model';
  const [mode, setMode] = usePersistedState<Mode>('lab.mode.' + persistKey, 'visual');
  const [query, setQuery] = usePersistedState('lab.bq.' + persistKey, '');
  // What the LAB last wrote into the editor. Persisted alongside the query itself so a reload cannot turn the
  // lab's own text into the person's, and seeded the same way (empty) for the same reason.
  const [generatedQuery, setGeneratedQuery] = usePersistedState('lab.bq.generated.' + persistKey, '');
  // H5: the person's own query, kept when the question box writes over it, so one action puts it back.
  const [putBack, setPutBack] = usePersistedState('lab.ask.putback.' + persistKey, '');
  // Why Fields could not show the open question as fields (pro UAT 18 Oct, N2). Cleared by the next press of the switch.
  const [fieldsNote, setFieldsNote] = useState<string | null>(null);
  const [testMeasureRef, setTestMeasureRef] = usePersistedState('lab.testMeasure.' + persistKey, '');
  // What an answer the assistant asked for holds on the page right now (set below, once the question's state is known): read by the measure focus.
  const heldAnswer = useRef<{ byAssistant: boolean; owns: boolean; measures: string[] | undefined; assistantRun?: boolean } | null>(null);
  // A deliberate measure action seeds a runnable query. Ordinary tree selection never reaches this effect, so it
  // cannot overwrite a Queries draft. The nonce makes a repeated action explicit.
  useEffect(() => {
    // Open in Queries from Ask your data: the engine's DAX text, taken as the query once, then acknowledged so
    // coming back through area memory cannot replay it over what the person has typed since.
    if (navTarget?.dax) {
      setMode('query'); setEntryRaw('dax');
      // With the page's answer handed over in memory, it lands as a question here: the chips, the editor's DAX and
      // the rows, with no second ask and no run (the pre-flight's hand-off; verify-queries-redesign D12). Without it
      // (a reload), the DAX text alone, as before.
      setQuery(navTarget.dax); setGeneratedQuery(navTarget.dax);
      onNavConsumed?.(navTarget.nonce);
      return;
    }
    if (!navTarget?.ref.startsWith('measure:')) return;
    const name = navTarget.ref.slice(navTarget.ref.indexOf('/') + 1);
    const seedText = seedForMeasureFocus(name, heldAnswer.current);
    if (seedText === null) {
      // The assistant's own answer is on the page and is about this measure: it keeps its query (filters and all). Only the selection moves.
      setTestMeasureRef(navTarget.ref);
      onNavConsumed?.(navTarget.nonce);
      return;
    }
    // The bar follows the page: with Build still chosen the wells and Read as would show the old fields beside a read-only ROW query.
    // A row is open: the seed is a new query, so it opens as the draft and the row keeps the DAX its DAX view shows (hero 25 H1).
    if (activeRow.current) onSeeded.current?.();
    setMode('query'); setEntryRaw('dax'); setQuery(seedText); setGeneratedQuery(seedText); setTestMeasureRef(navTarget.ref);
    onNavConsumed?.(navTarget.nonce);
  }, [navTarget?.nonce]);
  const [wells, setConfig] = usePersistedState<PivotConfig>('lab.config.' + persistKey, EMPTY_CONFIG);
  // The wells as the person set them, plus each field's sort-by column from the model (so the query can bring it and order
  // by it, UAT N1). Keyed by what the model says about THESE fields, so a model reload that changes nothing here is not a new config.
  const sortSig = [...wells.rows, ...wells.cols].map((f) => model.columns.find((c) => c.table === f.table && c.name === f.name)?.sortByColumn ?? '').join('|');
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const config = useMemo(() => withSortBy(wells, model), [wells, sortSig, model.columns.length > 0]);
  // Set when Fields shows an asked question's own fields: the run that follows refreshes that row and adds none (pro UAT 18 Oct, round 3, P7).
  const quietFieldsRun = useRef(false);
  const wellsTouched = useRef(false);
  // The notebook row the page is showing now, and what to tell the notebook when a run adds or changes one. The notebook (query-notebook.tsx)
  // owns both; the page only reads the one and calls the other, so a run made here lands in the row the person is looking at.
  const activeRow = useRef<string | null>(null);
  const onRecorded = useRef<((id: string) => void) | null>(null);
  const onSeeded = useRef<(() => void) | null>(null);
  // The last headline each row said. recordQueryRun clears a row's headline when it runs again, so the "$" it said is read from here (hero 26 N8).
  const saidHeadline = useRef<Record<string, SaidHeadline>>({});
  const measureRefOf = (field?: PivotConfig['values'][number]) => field?.table ? `measure:${field.table}/${field.name}` : '';
  const contextRefOf = (field: PivotConfig['rows'][number]) => field.table ? `column:${field.table}/${field.name}` : field.ref;
  function onWells(next: PivotConfig) { wellsTouched.current = true; setConfig(next); setTestMeasureRef(measureRefOf(next.values[0])); }
  // The person's own chart choice, or null for the rules' pick. It is forgotten when the result's shape changes.
  const [vizOverride, setVizOverride] = useState<{ kind: Kind; shape: string } | null>(null);
  const {
    tab, setTab, sheet, setSheet, planOpen, setPlanOpen, res, setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench, setBench, cw, setCw,
    plan, setPlan, timings, setTimings, clearMsg, setClearMsg, clearRefusal, setClearRefusal, eqEv, setEqEv, vErr, setVErr,
    busy, setBusy, err, setErr, claudeEvent, setClaudeEvent, execContextKey, formatScopeKey, sentFormats, noteSent, speedQuery, speedFor, assistantDax, setAssistantDax, speedByAssistant, ranSeq,
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
  // "Check saved." belongs to the row it was saved from (UAT 17 Oct, P22: it showed on every later answer). '__draft' is the draft row's id.
  const [measureStep, setMeasureStep] = useState<string | null>(null);
  const [checkSavedRow, setCheckSavedRow] = useState<string | null>(null);
  const checkSaved = checkSavedRow !== null;
  const setCheckSaved = (v: boolean) => setCheckSavedRow(v ? (activeRow.current ?? '__draft') : null);
  useEffect(() => { setCheckSavedRow(null); }, [res]);
  const [saving, setSaving] = useState<{ row: number; measure: number } | null>(null);
  const [answerDraft, setAnswerDraft] = useState<CheckDraft | null>(null);

  // compare: the Original formula is read from the measures the query shows now (compare-model.ts); the group-by + filters come from the wells' own split.
  // exprARaw is the person's own formula in the DAX view, where no measure is shown to read it from.
  const [exprARaw, setExprARaw] = usePersistedState('lab.exprA', '');
  const [pickedRef, setPickedRef] = useState<string | null>(null);
  const [typedA, setTypedA] = useState<{ forRef: string; text: string } | null>(null);
  const [exprB, setExprB] = usePersistedState('lab.exprB', '');
  const [customVerify, setCustomVerify] = usePersistedState('lab.verify.custom.' + persistKey, false);
  const [verifyFields, setVerifyFields] = usePersistedState<string[]>('lab.verify.fields.' + persistKey, []);
  // Compare belongs to the row it was made on (pro UAT 18 Oct, round 3, P5): Q5's Compare opened with Q1's rewrite and a stale "proven" for a
  // comparison Q5 never ran. Each row keeps its own; a row never opened here starts blank. A draft that is recorded as a row takes its Compare with it.
  type CompareSnap = { exprARaw: string; exprB: string; eqEv: EqEvidence | null; vErr: string | null; customVerify: boolean; verifyFields: string[]; pickedRef: string | null; typedA: { forRef: string; text: string } | null };
  const compareRows = useRef(new Map<string, CompareSnap>());
  const compareOwner = useRef('');
  function moveCompareTo(next: string | null, adopt = false) {
    const to = next ?? '';
    if (to === compareOwner.current) return;
    const from = compareOwner.current;
    compareOwner.current = to;
    if (adopt) { compareRows.current.delete(from); return; }
    compareRows.current.set(from, { exprARaw, exprB, eqEv, vErr, customVerify, verifyFields, pickedRef, typedA });
    const s = compareRows.current.get(to);
    setExprARaw(s?.exprARaw ?? ''); setExprB(s?.exprB ?? ''); setEqEv(s?.eqEv ?? null); setVErr(s?.vErr ?? null); setCustomVerify(s?.customVerify ?? false);
    setVerifyFields(s?.verifyFields ?? []); setPickedRef(s?.pickedRef ?? null); setTypedA(s?.typedA ?? null);
  }
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
    heldRow: () => activeRow.current,
  });
  const askOn = askGrant === 'granted';
  const chipsHere = askOn && !!ask.parts && (ask.parts.measures ?? []).length > 0 && !!ask.chipDax;
  // The chips own the query while the editor still holds exactly what they wrote (whitespace squashed).
  // An answer the engine read from the words alone (a count of customers by their orders) has a statement and a query but no chips: it still
  // owns the rows it made, so its statement and its assumption line show over them.
  const wordsOnly = askOn && !chipsHere && !!ask.answer && outcomeOf(ask.answer) === 'answered' && !!ask.answer.statement && !!ask.answer.dax && !!ask.chipDax;
  const askOwns = (chipsHere || wordsOnly) && squash(query) === squash(ask.chipDax);

  // The question's state, read-only, as its own object: nothing here sends anything, so the page may read it without a Pro gate
  // (test/free-sends-no-jev.test.mjs follows members, and `ask` itself holds the members that do ask).
  // What the page is handed to ask with: the real thing on a granted plan, an inert stand-in otherwise, so Free holds nothing that asks.
  const askBox = askOn ? ask : NO_ASK;
  const askState = { words: ask.words, setWords: ask.setWords, parts: ask.parts, answer: ask.answer, chipDax: ask.chipDax, asking: ask.asking, stopped: ask.stopped, announce: ask.announce };
  // ---- how the question is asked: Words, Build or DAX (the bar's switch). Words is Pro; Free sees its quiet line there.
  type Entry = 'words' | 'build' | 'dax';
  const [entryRaw, setEntryRaw] = usePersistedState<string>('lab.entry.' + persistKey, '');
  const entry: Entry = entryRaw === 'words' || entryRaw === 'build' || entryRaw === 'dax' ? entryRaw : (askOn ? 'words' : mode === 'query' ? 'dax' : 'build');
  // A refused question has no query of its own, but its parts stay as chips so the refused part can be fixed in place (A6).
  const chipsKept = askOn && !chipsHere && entry === 'words' && !!ask.answer && !ask.answer.dax && !!ask.parts && (ask.parts.measures ?? []).length > 0;
  // ---- An answer the person's assistant asked for (ask_data) lands here as an answer, labelled so, with no second ask.
  const fromAssistant = useAssistantAnswer();
  const takenAnswer = useRef<string | null>(null);
  const [byAssistant, setByAssistant] = useState<{ id: string; question: string | null } | null>(null);
  // The assistant's answer is the one on the page only while the page still holds that very answer: the person's own next question ends it.
  const adoptedAnswer = useRef<DataAnswer | null>(null);
  heldAnswer.current = { byAssistant: !!ask.answer && adoptedAnswer.current === ask.answer, owns: askOwns, measures: ask.parts?.measures ?? undefined, assistantRun: !!assistantDax && !!res && producedByOf(res, resQuery) === squash(assistantDax) };
  useEffect(() => {
    if (!fromAssistant || takenAnswer.current === fromAssistant.id || !answerIsFor(fromAssistant, session?.sessionId)) return;
    takenAnswer.current = fromAssistant.id;
    if (busy) return;
    const a = fromAssistant.answer as DataAnswer;
    adoptedAnswer.current = a;
    setByAssistant({ id: fromAssistant.id, question: fromAssistant.question ?? null });
    ask.adopt({ parts: a.parts ?? cleanParts(null), answer: a, words: fromAssistant.question ?? undefined });
    setEntryRaw('words');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fromAssistant, session?.sessionId]);
  // The Learn tour's recorded example answers: the demo model holds no data to ask, so the tour shows answers recorded on it.
  const learn = useLearn();

  const shared = conn?.connected && conn.kind !== 'local';
  const idle = busy !== null;
  // Build view runs the wells' query, unless a question's parts hold the query, which lives in the editor.
  // UAT v-batch1 (live 1.5.104): on a row's DAX tab, Run ran the wells' generated query instead of the DAX in the editor, so a row titled
  // EVALUATE ROW(...) showed another query's answer. Run runs exactly what the page shows: the wells' query only while the Fields are the way in.
  const personQuery = () => (entry === 'build' && !chipsHere && !wordsOnly ? buildQuery(config) : query);
  // While an assistant's rows are the result on screen, Run and Measure speed work on the query that made them.
  const currentQuery = () => (assistantShown && assistantDax ? assistantDax : personQuery());
  // The wells already hold everything a result needs, so the next step is Run, not "add a measure".
  const wellsReady = config.values.length > 0;
  // What Compare a rewrite tests across: the question's split and its own filters while a question holds the query,
  // otherwise the wells'. Comparing the hidden wells under a question proved something about a query nobody
  // could see (verify-queries-redesign D5).
  const qParts = chipsHere ? ask.parts : null;
  const groupByDerived = useMemo(() => customVerify ? verifyFields : qParts ? [...(qParts.groupBy ?? [])] : [...config.rows, ...config.cols].map((f) => f.ref), [config, customVerify, verifyFields, qParts]);
  const setVerifyGroupBy = (fields: string[]) => { setVerifyFields(fields); setCustomVerify(true); };
  const filterLinesDerived = useMemo(() => qParts ? answerFilterLines(ask.chipDax) : config.filters.map(filterToDax).filter((x): x is string => !!x), [config, qParts, ask.chipDax]);
  const compareSubject: 'wells' | 'question' | 'dax' = qParts ? 'question' : mode === 'visual' && !wordsOnly ? 'wells' : 'dax';
  // UAT 17 Oct, P27: Compare tests the measure the query shows NOW. A removed or stale one is never kept; with two or more shown the person picks one.
  const shownMeasures = qParts ? (qParts.measures ?? []).map((m) => `[${m.replace(/]/g, ']]')}]`) : config.values.map((v) => v.ref);
  const compareO = compareOriginal(shownMeasures, pickedRef, typedA);
  const exprA = compareSubject === 'dax' ? exprARaw : compareO.text;
  const setExprA = (t: string) => { if (compareSubject === 'dax') setExprARaw(t); else setTypedA({ forRef: compareO.chosen, text: t }); };
  const pickCompare = (ref: string) => { setPickedRef(ref); setTypedA(null); };
  // The formula Compare starts from: the question's measure while a question holds the query, else the one in use.
  const compareStart = () => (exprA || shownMeasures[0] || '');
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

  // The builder starts EMPTY and runs nothing until the person adds a field (UAT 17 Oct, P30 = F23): the old guess (City by JoinDate by Average
  // Order Value) ran as if it were a question, marked stale, and was where Compare's wrong measure came from.
  const seeded = useRef(true);
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
  // `record` false: the run only refreshes the open row's answer (Fields on a question), so it never adds a row of its own.
  async function runQuery(q: string, record = true) {
    if (!q.trim()) { setErr('Nothing to run yet. Choose a number in Fields, or write a query in DAX.'); return; }
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
        // A query run as written is a row of the notebook: the one the person is shaping if it is a query row, else a new one.
        if (record) void rpc<string | null>('recordQueryRun', rs.query ?? q, rs.rowCount, rs.elapsedMs, null, activeRow.current)
          .then((id) => { if (id && gen === runGen.current) onRecorded.current?.(id); }).catch(() => undefined);
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
  // Run is the person's own: over an assistant's result it brings their query back and runs it, as Back to my fields does.
  // Over an assistant's row it runs the DAX the DAX view shows (the assistant's own), never the person's query behind it.
  function run() { setTab('result'); void runQuery(assistantShown && assistantDax ? assistantDax : personQuery()); }
  async function debug(q?: string) {
    const gen = ++runGen.current;
    const ran = q ?? (mode === 'visual' && !chipsHere ? buildInstrumentedQuery(config) : currentQuery());
    const sent = formatsAtSend(execContextKey);
    setSheet('steps'); setBusy('debug'); setErr(null); setClaudeEvent(null);
    try {
      const r = await rpc<EvalLogResult>('evaluateAndLog', ran, 10000);
      if (gen !== runGen.current) return;
      // A log that failed or gave no steps changes nothing on screen: the result and the plan recorded beside it stay (UAT 18 Oct, N3).
      if (!logRunUsable(r)) { setLogNote(r.note ?? null); if (r.error) setErr(r.error); return; }
      {
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
    // Progress while it measures (UAT 17 Oct): the page said "Not measured yet" for the whole minute.
    setMeasureStep('Timing the query a few times');
    try {
      const c = await rpc<ColdWarmBenchmark>('benchmarkColdWarm', q, runs, clearOnRun && (!shared || confirmShared), confirmShared);
      if (gen !== runGen.current) return;
      if (c?.error) { setErr(c.error); setCw(null); } else if (c?.coldTotal && c?.warmTotal) setCw(c); else setCw(null);
      // A fast query has nothing to say about where its time went, so it is never traced: that cost seconds on a service model for nothing (UAT 17 Oct).
      if (c?.coldTotal && c?.warmTotal && isFast(c, null)) { setTimings(null); return; }
      setMeasureStep('Recording where the time went');
      const t = await rpc<ServerTimings>('profileDax', q);
      if (gen !== runGen.current) return;
      // A failed profile puts the old one away too: its split and scans describe another run, maybe another query.
      if (t?.error) { setErr(t.error); setTimings(null); } else setTimings(t);
    } catch (e) {
      if (gen === runGen.current) setErr(String((e as Error).message ?? e));
    } finally { if (gen === runGen.current) { setBusy(null); setMeasureStep(null); } }
  }
  // True when the server cleared its kept results. A refusal is shown where the button was pressed, in plain words, never as a success.
  async function clearCache(where: 'speed' | 'steps' = 'speed'): Promise<boolean> {
    setBusy('clear'); setErr(null); setClearMsg(null); setClearRefusal(null);
    try {
      const r = await rpc<ClearCacheResult>('clearCache', confirmShared);
      if (r.cleared) { setClearMsg(`Cleared the cache${r.database ? ` for "${r.database}"` : ''} (${r.elapsedMs} ms)`); return true; }
      setClearRefusal({ text: plainClearRefusal(r.error), where }); return false;
    }
    catch (e) { setErr(String((e as Error).message ?? e)); return false; } finally { setBusy(null); }
  }
  // Cold plan capture: clear the SE cache, then capture (a warm/cached query emits no plan). Nothing records until the clear has gone through.
  async function clearThenPlan() {
    const q = currentQuery();
    if (!(await clearCache('steps'))) return;
    speedFor(q); setTab('speed'); setPlanOpen(true);
    await call<QueryPlanResult>('captureQueryPlan', setPlan, 'plan', q);
  }
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
    if (mode !== 'visual' || chipsHere || wordsOnly || !conn?.connected || !wellsTouched.current) return;
    // No measure chosen: nothing to run. A placeholder query was run after every Remove (UAT F24); the old result goes instead.
    if (!config.values.length) { runGen.current++; setRes(null); setResQuery(null); setErr(null); return; }
    const q = buildQuery(config);
    const quiet = quietFieldsRun.current; quietFieldsRun.current = false;
    const t = setTimeout(() => { void runQuery(q, !quiet); }, 220);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config, mode, conn?.connected]);

  // A view switch REGENERATES the query from the same wells. It is the lab writing, not the person, so it is
  // recorded as such: a switch with no editing must not read as a changed formula. A question's DAX is already there.
  // With no fields chosen buildQuery is empty, so the editor starts empty with its placeholder (UAT 18 Oct, N4).
  function switchMode(m: Mode) { if (m === 'query' && mode === 'visual' && !chipsHere && !wordsOnly) { const q = buildQuery(config); if (query.trim() && squash(query) !== squash(generatedQuery)) setPutBack(query); setQuery(q); setGeneratedQuery(q); } setMode(m); }   // an edited query is put away first, never overwritten (R-B), exactly as the ask box does
  function compareValue(ref: string) { pickCompare(ref); setExprARaw(ref); setSheet('compare'); }
  function openMeasureCheck(shape: Extract<AuthorShape, 'measureValue' | 'measureReconcile'>) {
    // A typed query that is not about the picked calculation opens the check empty: the wells do not describe it.
    // An edited or opened query in the DAX editor is read for its own measure and groups, even when its measure matches the wells:
    // stale wells from another row must never describe it (v2-fix-final-r3, 18 Oct).
    const read = mode === 'query' && squash(query) !== squash(generatedQuery) ? checkFieldsOfDax(query, model) : null;
    const own = !read && !(entry === 'words' && !chipsHere) && labQueryIsAbout(mode, query, selectedMeasureName, generatedQuery);
    // An opened row puts its DAX in the editor and leaves the wells alone, so a query the field builder wrote is read for its own
    // measure and groups (UAT 18 Oct, round 3, R2). Any other typed query still opens empty.
    const targetRef = own ? testMeasureRef : read?.measureRef ?? '';
    const groupBy = own ? [...config.rows, ...config.cols].map(contextRefOf) : read?.groupBy ?? [];
    // The wells' FILTERS travel on both paths. Dropping them turned a calculation filtered to one year into a
    // check over the whole model, which is a wrong check that looks right. The engine records the connection.
    const filters = own ? filterLinesDerived.join(', ') : read?.filterDax ?? '';
    setCheckDraft({
      id: '', kind: shape, title: '', targetRef, enabled: true,
      paramsJson: JSON.stringify(shape === 'measureReconcile'
        ? { measureRef: targetRef, groupBy, filterDax: filters || undefined }
        : { measureRef: targetRef, filterDax: filters || undefined, expressionDax: (own && editedExpression) || undefined }),
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
  // New query and Reset: an empty builder that runs nothing until the person adds a field (UAT 17 Oct, P30 = F23 + F24, F37).
  function clearWells() {
    wellsTouched.current = true; ask.clear(); setSaving(null); setPutBack(''); setFieldsNote(null);
    setConfig(EMPTY_CONFIG); setTestMeasureRef(''); setQuery(''); setGeneratedQuery('');
    setPickedRef(null); setTypedA(null); setExprARaw(''); setExprB('');
    runGen.current++; setRes(null); setResQuery(null); setErr(null); setLogs(null); setClaudeEvent(null); setAssistantDax(null); setEqEv(null); setVErr(null);
  }
  const resetDefault = clearWells;

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
  // The page shows the assistant's query, not the person's wells, for exactly as long as its rows are the result on screen.
  const assistantShown = !!assistantDax && !!res && producedBy === squash(assistantDax);
  // With no query there is nothing to time or log: Speed and Steps say so instead of going quiet (pro UAT 18 Oct, N3).
  const emptyQuery = !currentQuery().trim();
  // The person's own query comes back as it was left: nothing here wrote to the wells or the editor.
  function backToMine() {
    setAssistantDax(null); setTab('result');
    if (conn?.connected && personQuery().trim() && (mode === 'query' || chipsHere || wellsReady)) void runQuery(personQuery());
    else { runGen.current++; setRes(null); setResQuery(null); }
  }
  // The question's statement holds only over rows its own query produced; the same comparison the chart below uses.
  const answerRows = answerOwnsResult({ askOwns, chipDax: ask.chipDax, res, resQuery });
  // The question's own fields are built ONCE per question: a new object each render made every card below think the result had changed,
  // and Explain asked the live model again on each one (hero 24 D1: about 3,500 queries in 13 minutes).
  const chipConfig = useMemo(() => (ask.parts ? pivotConfigOf(ask.parts, ask.chipDax) : null), [ask.parts, ask.chipDax]);
  const chartConfig: PivotConfig | null = !res ? null
    : chipsHere ? (askOwns && producedBy === squash(ask.chipDax) ? chipConfig : null)
      : config.values.length && (producedBy === squash(buildQuery(config)) || producedBy === squash(buildInstrumentedQuery(config))) ? config : null;
  // The sort-by columns a wells query brought leave the result here, so every page below counts columns by the wells' layout.
  const shownRes = useMemo(() => (formattedRes && chartConfig ? foldSortColumns(formattedRes, chartConfig) : formattedRes), [formattedRes, chartConfig]);
  // A date is drawn as time, and the rules say so, only over an order that was really set (axisDims, UAT N1).
  // A result nobody built from fields (a hand-written query) is drawn from its own shape, like any answer (hero 25 H11). Only drawing uses it.
  const drawConfig = useMemo(() => chartConfig ?? configOfResult(shownRes), [chartConfig, shownRes]);
  const shape = useMemo(() => {
    if (!shownRes || !drawConfig) return null;
    return queryShape(shownRes, drawConfig, axisDims(shownRes, drawConfig));
  }, [shownRes, drawConfig]);
  // The visuals that CAN draw this result: the shape rules narrow them, and one the rows cannot honestly fill (a member with a hole
  // in its trend) is not offered. The rules' pick is first. Jev picks among these or leaves the rules' pick (below).
  const rule = useMemo(() => {
    if (!shape || !shownRes || !drawConfig) return null;
    const base = queryVisualFits(shape);
    const fits = base.fits.filter((k) => k === 'table' || visualData(k, shownRes, drawConfig) !== null);
    return { ...base, fits, pick: fits.includes(base.pick) ? base.pick : 'table' as Kind };
  }, [shape, shownRes, drawConfig]);
  // Pro: Jev may pick the visual (decision point queries.chart_kind, docs/queries-chart-pick-bar.md). ONE call for each answer.
  // The engine answers "rules" while the point is off, below its bar (0.85), or unsure, and only a pick Jev made is labelled as
  // Jev's. It sends the question's words and the result's SHAPE (names of the splits, counts, yes or no facts), never a row or a
  // value. The page names the objects of the question in the words (memory jev-costs-nothing). Free never asks: the rules' pick.
  const [jevPick, setJevPick] = useState<{ key: string; kind: Kind; reason: string } | null>(null);
  const pickWords = chipsHere && ask.answer?.readBy === 'jev' ? ask.words.trim() : '';
  const pickKey = shape && rule ? JSON.stringify([shape.dims, shape.measures, shape.measureNames, shape.sameUnit, shape.negatives, rule.fits, rule.pick, pickWords]) : '';
  useEffect(() => {
    if (!askOn || !shape || !rule || rule.fits.length < 2) return;
    let alive = true;
    rpc<{ kind?: string; by?: string; reason?: string }>('pickChartKind', { words: pickWords || undefined, shape: engineShape(shape), fits: rule.fits, rulesPick: rule.pick })
      .then((p) => { if (alive) setJevPick(p?.by === 'jev' && p.kind && rule.fits.includes(p.kind as Kind) ? { key: pickKey, kind: p.kind as Kind, reason: p.reason ?? '' } : null); })
      .catch(() => { if (alive) setJevPick(null); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [askOn, pickKey]);
  const jevHere = !!jevPick && !!rule && jevPick.key === pickKey;
  const basePick: Kind | null = rule ? (jevHere ? jevPick!.kind : rule.pick) : null;
  const shapeKey = shape ? JSON.stringify(shape.dims.map((d) => [d.name, d.isDate, d.ordered])) + shape.measures : '';
  const kind: Kind = !rule ? 'table' : vizOverride && vizOverride.shape === shapeKey && rule.fits.includes(vizOverride.kind) ? vizOverride.kind : basePick!;
  const picked = !!rule && kind !== basePick;
  const chooseKind = (k: Kind) => setVizOverride(rule && k === basePick ? null : { kind: k, shape: shapeKey });
  const visual = useMemo(() => (shownRes && drawConfig && kind !== 'table' ? visualData(kind, shownRes, drawConfig) : null), [shownRes, drawConfig, kind]);
  const tableData = useMemo(() => (shownRes && drawConfig ? visualData('table', shownRes, drawConfig) : null), [shownRes, drawConfig]);

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
  const measuredMs = waitMs(cw, timings, bench);
  const speedMs = measuredMs != null && (speedIsShown || !res) ? measuredMs : res ? res.elapsedMs : null;
  const speedLabel = speedMs != null ? timingAnnouncement(speedMs) : null;
  // Numbers measured for another query are not shown at all: Speed names this result's own, or says there are none.
  const ownSpeed = speedIsShown || !res;
  // With nothing measured the Speed panel ends after its own content, not at the page bottom.
  const speedHasContent = ownSpeed && !!(timings || cw || plan || bench);
  const measureName = (exprA.match(/^\[(.+)\]$/)?.[1]) ?? config.values[0]?.name ?? 'this formula';
  // The fields the result on screen is built from, marked in the Fields list (option C's inventory).
  const fieldsInUse = useMemo(() => {
    const c = chartConfig ?? (chipsHere ? null : config);
    return new Set(c ? [...c.rows, ...c.cols, ...c.values, ...c.filters].map((f) => f.ref) : []);
  }, [chartConfig, chipsHere, config]);
  const canSaveAnswer = askOwns && outcomeOf(ask.answer) === 'answered' && (ask.answer?.rows ?? []).length > 0;
  const onExplainNumber = (p: ExplainPayload) => setCellMenu({ payload: p, x: lastPointer.current.x, y: lastPointer.current.y });
  // Explain this number is a card on the page: it explains the number the person pressed. An answer of one number is that number; an answer of many rows is
  // never explained by its first row on the engine's own choice: the card asks once which, with the first rows to press (explainChoices).
  const explainChoices = useMemo(() => {
    if (!shownRes || !chartConfig || !shownRes.rows.length || !chartConfig.values.length) return [];
    const ci = chartConfig.rows.length + chartConfig.cols.length;
    return shownRes.rows.slice(0, 8).map((r) => explainPayloadFromRow(chartConfig, r, ci, shownRes.columns[ci]?.format)).filter((p): p is NonNullable<typeof p> => !!p);
  }, [shownRes, chartConfig]);
  const explainRowCount = shownRes?.rows.length ?? 0;
  const defaultExplain = explainRowCount === 1 && explainChoices.length === 1 ? explainChoices[0] : null;
  useEffect(() => { setExplain(null); }, [res]);
  const explainShown = explain ?? defaultExplain;
  // The bar's switch. Words keeps the question's chips; Build goes back to the person's fields; DAX is the query text.
  const chooseEntry = (e: Entry) => {
    setFieldsNote(null);
    if (e !== 'build') setEntryRaw(e);
    if (e === 'dax') { if (mode !== 'query') switchMode('query'); }
    else if (e === 'build') {
      // Fields on a question shows the question's own fields, or says it cannot and leaves the question as it is. Before, it showed empty
      // wells, swapped the query for a placeholder row, and the Answer read "Blank" under the person's words (pro UAT 18 Oct, N1 N2).
      // Always the CURRENT question's own fields, whatever the wells held before: after a second question, the first one's wells are not its fields
      // (pro UAT 18 Oct, round 2: the Answer read "Split by Brand, Split by Year" over month rows, and Speed and Steps went silent).
      if (chipsHere || wordsOnly) {
        const shown = wellsOfQuestion(chipsHere ? ask.parts : null);
        if (!shown.config) { setFieldsNote(shown.why); if (entryRaw === 'build') setEntryRaw('words'); return; }   // the switch then shows DAX, where the question's query is
        wellsTouched.current = true; quietFieldsRun.current = true; setConfig(shown.config);
      }
      setEntryRaw(e);
      if (chipsHere) leaveQuestion(false);
      if (mode !== 'visual') switchMode('visual');
    }
  };

  return {
    onWells, proveLinkedFormats, runQuery, stopQuery, run, debug, call, measureSpeed,
    clearCache, clearThenPlan, runVerify, switchMode, compareValue, moveCompareTo, openMeasureCheck, leaveQuestion, resetDefault, clearWells,
    backToMine, model, fieldNames, fieldsNote, emptyQuery, editorRef, runGen, askGrant, persistKey, sortSig, config,
    wellsTouched, measureRefOf, contextRefOf, fieldsSpan, lastPointer, askOn, chipsHere,
    askOwns, wordsOnly, chipsKept, shared, idle, personQuery, currentQuery, wellsReady, qParts, groupByDerived,
    setVerifyGroupBy, filterLinesDerived, compareSubject, compareStart, selectedMeasureName, editedExpression, seeded, scopeNow,
    formatsAtSend, runProfile, runPlan, runQuick, eqStale, eqV, compareBadge, formatMeasures,
    formattedRes, producedBy, assistantShown, answerRows, chartConfig, shownRes, shape, rule,
    pickWords, pickKey, jevHere, basePick, shapeKey, picked,
    chooseKind, kind, visual, tableData, canvasRef, shownQuery, speedIsShown, measuredMs, speedMs, speedLabel,
    ownSpeed, speedHasContent, measureName, fieldsInUse, canSaveAnswer, onExplainNumber, mode,
    entry, chooseEntry, askBox, askState, activeRow, onRecorded, onSeeded, saidHeadline, byAssistant, setByAssistant, learn, explainShown, defaultExplain, explainChoices, explainRowCount,
    setMode, query, setQuery, generatedQuery, setGeneratedQuery, putBack, setPutBack, testMeasureRef,
    setTestMeasureRef, wells, setConfig, vizOverride, setVizOverride, fieldsOpen, setFieldsOpen, fieldsW,
    onFieldsDrag, editorH, onEditorDrag, setEditorH, runs, setRuns, clearOnRun, setClearOnRun,
    confirmShared, setConfirmShared, checkMenu, setCheckMenu, moreMenu, setMoreMenu, kindMenu, setKindMenu,
    checkShape, setCheckShape, checkDraft, setCheckDraft, checkSaved, checkSavedRow, setCheckSaved, saving, setSaving,
    measureStep, answerDraft, setAnswerDraft, exprA, setExprA, compareChoices: compareO.choices, compareChosen: compareO.chosen, pickCompare, exprB, setExprB, customVerify, setCustomVerify,
    verifyFields, setVerifyFields, explain, setExplain, cellMenu, setCellMenu, savedExpressions, setSavedExpressions,
    jevPick, setJevPick, canvasH, setCanvasH, conn, session, context, connectXmla,
    connBusy, tab, setTab, sheet, setSheet, planOpen, setPlanOpen, res,
    setRes, resQuery, setResQuery, logs, setLogs, logNote, setLogNote, bench,
    setBench, cw, setCw, plan, setPlan, timings, setTimings, clearMsg,
    setClearMsg, clearRefusal, setClearRefusal, eqEv, setEqEv, vErr, setVErr, busy, setBusy, err,
    setErr, claudeEvent, setClaudeEvent, execContextKey, formatScopeKey, sentFormats, noteSent, speedQuery,
    speedFor, assistantDax, setAssistantDax, speedByAssistant, ranSeq,
  };
}

export function CompareBody(p: any) {
  // `subject` is what the comparison is about: the wells on screen, a question's parts, or a query in the DAX view.
  // The body used to name the Values well in both views, and in the DAX view no wells are on screen (M25,
  // walkthrough 2026-09-14); under a question it named the hidden wells (verify-queries-redesign D5).
  const { exprA, setExprA, exprB, setExprB, groupByDerived, filterLinesDerived, filterWordsShown = [], conn, busy, runVerify, eqEv, eqStale, vErr, customVerify, setVerifyGroupBy, onUseVisual, subject, compareChoices = [], compareChosen = '', onPickCompare, measureFormats } = p;
  const model = useDaxModelContext();
  const own = subject === 'question' ? 'the question\'s split' : 'the split of your fields';
  const [choosingFields, setChoosingFields] = useState(false);
  const grain = grainWords(groupByDerived, useDaxModelContext().columns);   // the chips are the fields only: say so when one has a sort order (join 6, J4)
  const addFields = (nodes: BrowserNode[]) => setVerifyGroupBy([...new Set([...groupByDerived, ...nodes.map((n) => `'${n.table.split("'").join("''")}'[${n.name.split(']').join(']]')}]`)])]);
  return (
    <div className="flex flex-col gap-3" data-testid="queries-compare">
      {compareChoices.length > 1 && <div className="flex gap-2 items-center flex-wrap" role="group" aria-label="Which number to compare" data-testid="compare-pick">
        <span className="sem-text-meta">The query shows more than one number. Compare</span>
        {compareChoices.map((c: string) => <button key={c} type="button" className="sem-btn sem-btn-sm" aria-pressed={c === compareChosen} onClick={() => onPickCompare?.(c)}>{c.replace(/^\[|\]$/g, '').replace(/\]\]/g, ']')}</button>)}
      </div>}
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
        // The measure Compare tests (the chosen one, or the Original box when it is just a measure), so its numbers read in its own format.
        const comparedFormat = [...(measureFormats ?? []), ...model.measures].find((x) => x.formatString && `[${x.name.replace(/]/g, ']]')}]` === (subject === 'dax' ? (exprA ?? '').trim() : compareChosen))?.formatString;
        const formatOfMeasure = (name: string) => [...(measureFormats ?? []), ...model.measures].find((x) => x.formatString && x.name === name)?.formatString;
        const rewrittenFormat = rewriteFormat(exprB ?? '', comparedFormat, formatOfMeasure);
        const color = verdict === 'proven' ? 'var(--sem-good)' : verdict === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)';
        const text: Record<EqVerdict, string> = {
          proven: `Same numbers in all ${eq.rowsCompared} combinations.`,
          failed: differenceLine(eq.mismatchCount, eq.rowsCompared),
          degraded_mismatch: `A difference showed in ${eq.mismatchCount} combination(s), but the comparison ran with reduced fidelity, which can itself cause a difference. Not proven; do not apply on this comparison.`,
          degraded: /calculation[- ]group/i.test(eq.fidelity ?? '') ? calcCautionLine : `The numbers matched, but the comparison ran with reduced fidelity. Not proven.${eq.fidelity ? ' ' + eq.fidelity : ''}`,
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
                  <tr key={i}><td className="px-2 py-1" style={{ borderBottom: '1px solid var(--sem-border)' }}>{plainCombination(m.context)}</td><td className="px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-good)' }}>{shownNumber(m.valueA, comparedFormat)}</td><td className="px-2 py-1 tnum" style={{ borderBottom: '1px solid var(--sem-border)', color: 'var(--sem-bad)' }}>{shownNumber(m.valueB, rewrittenFormat)}</td></tr>
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
export function TraceNote({ note, kind }: { note: string; kind?: 'profileEmpty' | 'profileUnavailable' | 'scanDetailUnavailable' | 'debug' }) {
  const { text, technical } = splitEngineNote(note);
  // The trace line is for the log, never the page (UAT F30).
  useEffect(() => { if (technical) console.debug('[query trace]', technical); }, [technical]);
  const primary = kind === 'profileEmpty'
    ? 'No storage scan was captured. The answer may have come from cache or model metadata, so this profile only proves the elapsed time.'
    : kind === 'profileUnavailable'
      ? 'This endpoint returned elapsed time only. Storage work was not measured, so no storage-scan conclusion is available.'
    : kind === 'scanDetailUnavailable'
      ? 'Storage work was measured, but this endpoint did not return the individual scan details.'
    : kind === 'debug'
      ? 'The query returned a result, but it did not produce any logged calculation steps.'
      : text;
  return <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>{primary}</div>;
}

// A scan's kind in words. The trace reports a subclass name, or on some servers only its number ("176" on screen,
// verify-queries-redesign lower items), and neither is a word a person uses.
export function scanKind(subclass?: string): string {
  const k = subclass ?? '';
  if (/cache/i.test(k)) return 'From cache';
  if (/internal/i.test(k)) return 'Internal scan';
  if (/directquery|sql/i.test(k)) return 'Source query';
  return 'Scan';
}

export function LabeledMono({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  const model = useDaxModelContext();
  return (
    <div className="flex flex-col gap-1">
      <label className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{label}</label>
      <DaxEditor value={value} onChange={onChange} model={model} lineNumbers={false} minHeight={44} />
    </div>
  );
}
export function Empty({ children }: { children: React.ReactNode }) {
  return <div className="h-full grid place-items-center text-center sem-text-meta p-4"><div>{children}</div></div>;
}
export function Button({ children, onClick, primary, disabled, title, 'aria-expanded': expanded }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string; 'aria-expanded'?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title} aria-expanded={expanded} className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>{children}</button>
  );
}
// A message is a sentence with a coloured dot, not coloured text in a tinted box (Instrument).
export function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="xe-msg" style={{ '--xe-dot': color } as React.CSSProperties}>{children}</div>;
}
