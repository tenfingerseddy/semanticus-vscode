import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react';
import { rpc, rpcPressed, onActivity, onAssistantCall, onDidChange, onProgress, onReconnect } from './bridge';
import { useClaudeReflection } from './activity';
import { NO_ASSISTANT_RUN, TOOL_NOTE_KINDS, personRan, runFinished, runLine, runStale, runStarted, toolNote, type AssistantRun, type ToolNote } from './studio/checks-run';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import { useConnection, useSessionIdentity } from './connection';
import { listSqlSources, type SqlSourceRecord } from './connectionshub';
import { PathDisclosure } from './ui';
import { useFeature } from './pro';
import { DataTable, PickLine, Visual, type Kind } from './visuals';
import { Icon, Item, Mark, Menu, moodOf } from './validation-parts';
import './validation.css';
import {
  NO_VISUAL, boardHeadline, boardParts, boardTally, notRunRunWhy, oneNotice, quickChecksNote, plainFigure, plainToolNote, readableTitle, changesVisual, checkVisual, chosenVisual, countsVisual, historyVisual, movedCount,
  relationshipVisual, statusOf, visualAsk,
  type CheckVisual, type RecordedRunLike, type VisualAskCheck, type VisualPick, type VisualPicks,
} from './validation-model';
import { resultText } from './testresulttext';
import { evidenceCountsLine } from './evidencesummary.mjs';
import {
  AuthorDrawer, NewCheckChoices, announceTestSuiteChanged, onTestSuiteChanged,
  type AuthorFocus, type AuthorShape, type MeasureValueParamsW, type ReconcileParamsW,
} from './tests-authoring';
import {
  TABLE_COUNT_FOOTER, comparisonSummary, countsSummaryLine, differenceSentence, groupedLeadSentence, plainCheckError, familyRan, foldFamilyResult, foldRunResults,
  partRunSummary, relationshipChip, relationshipSummary, relationshipTally, resultChip, runOnlyLabel,
  countsFamilyChip, countsFamilyFinding, effectiveLastCount, relationshipMenuCount, runScopeGroups, sectionsForGroup, skippedFamilyNote,
  runsAgainstLine, gapShort, acceptedByLine, makesResultsOld, tickedRunRefusal, toleranceSentence, shownFigure, unknownCellText, foldRunProgress, runProgressPercent, runProgressText,
  type RunProgress, type RelationshipTally, type ResultChip, type RetainedFamily, type RunGroup, type StoredResult,
} from './tests-model';

// ===================================================================================================
// Checks > Tests, the approved redesign (tests-redesign.html, Kane 2026-09-15).
//
// ONE list of saved checks with the latest result in the row. Relationships and Table row counts each
// have their own card beneath it. History is a dated list whose runs open to their own evidence.
// Security and the Model interview left this page entirely: the engine no longer runs either here, so
// nothing on this page can imply they still contribute.
//
// Three rules the layout has to keep honest:
//  H1  An unknown is never dressed as a failure. "Could not check" is its own chip and its own colour.
//  H2  A part run's letter is the grade of WHAT IT RAN, never the model's, and a row that did not run
//      keeps its OWN result and its OWN date.
//  H3  A trusted-answer check has no SQL side, so its detail never borrows SQL vocabulary. Astra's UAT
//      found "SQL result NULL" under a SQL header on a check that never had any SQL at all.
//
// Wire shapes mirror Semanticus.Engine/LocalEngine.TestSuite.cs, Testing/* and Connect/* (camelCased),
// reconciled against the cp/tests-engine-a and cp/tests-engine-b lane reports.
// ===================================================================================================

interface CheckResultW { check: string; verdict: string; message?: string; rootCause?: string; count?: number }
interface RelationshipResultW {
  name: string; manyTable: string; manyColumn: string; oneTable: string; oneColumn: string;
  cardinality?: string; isActive: boolean; blankForeignKeys?: number; blankKeys?: number;
  manyRowCount?: number;
  dataTypeMatch: CheckResultW; keyUniqueness: CheckResultW; referentialIntegrity: CheckResultW;
}
interface RelationshipSuiteSummaryW { relationships: number; checked: number; passed: number; failed: number; suspect: number; notVerifiable: number; coveragePct: number }
interface RelationshipReportW { relationships: RelationshipResultW[]; summary: RelationshipSuiteSummaryW }
interface CompareRowW { context: string; sql?: number | null; dax?: number | null; delta?: number | null; verdict: string; explanation?: string; grandTotal?: boolean }
interface ReconcileOutcomeW {
  defId: string; title: string; targetRef?: string; verdict: string; message?: string; detail?: string; missing?: boolean;
  // cp/tests-engine-a: EVERY outcome, of either kind, now says what it was looking for, what it got, and
  // by how much. `difference` is ABSENT (never 0) when either side is not a number.
  expected?: string; actual?: string; difference?: number;
  sql?: string; dax?: string; rows?: CompareRowW[]; rowsTotal?: number;
  matches?: number; mismatches?: number; unverifiable?: number;
  durationMs?: number; sqlDurationMs?: number;
  createdBy?: string; createdWhen?: string; toleranceNote?: string;
  authoredAgainst?: string; authoredAgainstLabel?: string; ranAgainstLabel?: string;
  // Where the check's SQL goes. `unsavedServer` is set when the check names a server the person has not saved: nothing was sent to it.
  server?: string; database?: string; unsavedServer?: string;
  // Waive: the person accepted this exact difference. The verdict stays a difference; the check reads "Accepted". `waiverLapsed`: one was
  // accepted before, but the difference is another one now, so the check differs again.
  waiver?: CheckWaiverW | null; waiverLapsed?: boolean;
}
interface CheckWaiverW { by?: string; when?: string; note?: string | null }
interface WaiverW { system: string; ruleId: string; by?: string; when?: string; reason?: string | null; inForce?: boolean | null }
interface CategoryHealthW { category: string; weight: number; hasChecks: boolean; checked: number; passed: number; failed: number; suspect: number; notVerifiable: number; score: number }
interface TestHealthW {
  overall: number; grade: string; gatedBy: string[]; coveragePct: number; checked: number;
  passed: number; failed: number; suspect: number; notVerifiable: number; missing: number; rootFailures: number; accepted?: number;
  categories: CategoryHealthW[];
}
// cp/tests-engine-a, decision 1: a refused scope returns health null, scope.mode 'refused' and the reason
// in `note`, with no `error`. A refusal is an ANSWER to what was asked, so the page reads `note`.
interface RunScopeW {
  mode?: string; sections?: string[]; only?: string[];
  // cp/tests-engine-a: the result names its own scope in the page's own vocabulary ("saved checks",
  // "relationships", "table counts"), so nothing here has to infer what ran from what was asked.
  ran?: string[]; skipped?: string[]; gradeCovers?: string;
}
interface TestRunW {
  runId?: string; when?: string; modelName?: string; live?: boolean; health?: TestHealthW;
  relationships?: RelationshipReportW; reconciles?: ReconcileOutcomeW[];
  definitionCount?: number; persisted?: boolean; note?: string; error?: string;
  durationMs?: number; environment?: string; cacheCleared?: boolean; scope?: RunScopeW;
}
interface TestDefinitionW { id: string; kind: string; title: string; targetTag?: string; targetIdentity?: string; targetRef?: string; paramsJson?: string; enabled: boolean; createdBy?: string; createdWhen?: string; bindingWarning?: string; authoredAgainst?: string; authoredAgainstLabel?: string }
interface TestSuiteInfoW { definitions: TestDefinitionW[]; unreadableLines: number; note?: string; sqlSourceUse?: string }
// cp/tests-engine-a item 3. `outcomes` is absent on a run recorded before 1.2.0, which is also before
// access rules left the grade, so that run's letter was worked out a different way and the row says so.
// The engine records each context's numbers as decimal? (LocalEngine.TestSuite.cs RecordedContext), so they arrive as numbers, not the
// formatted strings the check's own totals use.
interface RecordedContextW { context: string; expected?: number | null; actual?: number | null; difference?: number | null; verdict: string; note?: string; grandTotal?: boolean }
interface RecordedCheckW {
  kind: string; defId?: string; name: string; check?: string; verdict: string;
  expected?: string; actual?: string; difference?: number; note?: string; accepted?: CheckWaiverW | null;
  contexts?: RecordedContextW[]; contextsTotal?: number; mismatches?: number;
  // The settings the run was executed with, kept with the run. Never re-read from today's definition.
  query?: string; filter?: string; tolerance?: string; source?: string;
}
interface TestRunOutcomesW { checks: RecordedCheckW[]; contextCap?: number; note?: string }
interface TestRunRecordW {
  runId: string; when: string; live: boolean; health?: TestHealthW; outcomes?: TestRunOutcomesW;
  modelName?: string; environment?: string; definitionCount?: number; scope?: RunScopeW; durationMs?: number;
}
// `gradedBeforeSecurityLeftTests` comes back on the DETAIL, never on the list: listTestRuns strips
// outcomes from every row, so a list row's missing outcomes proves nothing about how it was scored.
// A recorded check read back as the outcome the board draws: its verdict, what it expected and got, and the rows it kept.
function recordedOutcome(c: RecordedCheckW): ReconcileOutcomeW {
  return {
    defId: c.defId ?? '', title: c.name, verdict: c.verdict, message: c.note, expected: c.expected, actual: c.actual, difference: c.difference,
    mismatches: c.mismatches, rowsTotal: c.contextsTotal, waiver: c.accepted,
    rows: c.contexts?.map((x) => ({ context: x.context, sql: x.expected, dax: x.actual, delta: x.difference, verdict: x.verdict, explanation: x.note, grandTotal: x.grandTotal })),
  };
}
interface TestRunDetailW { run?: TestRunRecordW; note?: string; gradedBeforeSecurityLeftTests?: boolean }
interface TestRunRecordResultW { recorded: boolean; runId?: string; note?: string }
interface UnmatchedRowsW {
  relationship?: string; manyTable?: string; manyColumn?: string; oneTable?: string; oneColumn?: string;
  query?: string; columns?: string[]; rows?: (string | number | null)[][];
  rowsReturned?: number; cap?: number; truncated?: boolean; note?: string; error?: string;
}
interface TestHistoryW { runs: TestRunRecordW[]; note?: string }
interface TestReportResultW { markdown?: string; html?: string; json?: string; contentHash?: string; note?: string; error?: string }
interface EvidenceItemW {
  id: string; kind?: string; title?: string; createdUtc?: string; producer?: string; modelName?: string;
  verdict?: string; verified?: number; total?: number; unknowns?: number; verdictCounts?: Record<string, number>; contentHash?: string;
  valid: boolean; note?: string; jsonPath?: string; htmlPath?: string; updatedUtc?: string;
}
interface EvidenceLibraryW { modelName?: string; directoryPath?: string; items: EvidenceItemW[]; invalidCount: number; note?: string }
// cp/tests-engine-b, list_table_mappings. One row per model table, mapped or not, carrying the last counts
// from this session. Absent last* fields mean "not counted yet", which is honest rather than a zero.
interface TableMappingRowW {
  modelTable: string; source: string; sqlSourceId?: string; sqlSourceName?: string;
  server?: string; database?: string; schema?: string; entity?: string;
  canCount: boolean; reason?: string | null; editable: boolean; overridesDetected?: boolean; setWhenUtc?: string;
  lastVerdict?: string; lastMessage?: string; lastModelCount?: number; lastSourceCount?: number; lastRunUtc?: string;
}
interface TableMappingListW { rows: TableMappingRowW[]; note?: string }

type VerdictKey = 'Pass' | 'Fail' | 'Suspect' | 'NotVerifiable';
type CountChip = 'Match' | 'Counts differ' | 'Not checked';

const CHIP_COLOR: Record<ResultChip, string> = {
  Pass: 'var(--sem-good)',
  Differs: 'var(--sem-bad)',
  Accepted: 'var(--sem-good)',
  'Could not check': 'var(--sem-nv)',
  Off: 'var(--sem-muted)',
  'Not run': 'var(--sem-muted)',
};
const COUNT_CHIP_COLOR: Record<CountChip, string> = {
  Match: 'var(--sem-good)', 'Counts differ': 'var(--sem-warn)', 'Not checked': 'var(--sem-nv)',
};
const GRADE_COLOR: Record<string, string> = {
  A: 'var(--sem-good)', B: 'var(--sem-good)', C: 'var(--sem-warn)', D: 'var(--sem-warn)', F: 'var(--sem-bad)',
};
const verdictKey = (value?: string, missing = false): VerdictKey => {
  if (missing) return 'NotVerifiable';
  if (value === 'Pass' || value === 'Fail' || value === 'Suspect' || value === 'NotVerifiable') return value;
  if (value === 'Not verifiable') return 'NotVerifiable';
  return 'NotVerifiable';
};
const comparisonVerdict = (value: string): VerdictKey => value === 'Match' ? 'Pass' : value === 'Mismatch' ? 'Fail' : verdictKey(value);
/** The waiver store's name for a Validation check (WaiverStore system 'test'): the same door as waiving a finding. */
const CHECK_WAIVER_SYSTEM = 'test';
const KIND_LABEL: Record<string, string> = { measureValue: 'Trusted answer', measureReconcile: 'Check against SQL' };
const kindLabel = (kind?: string) => (kind && KIND_LABEL[kind]) || 'Check';
const formatDate = (value?: string, dateOnly = false) => {
  if (!value) return '·';
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return '·';
  return dateOnly ? date.toLocaleDateString() : date.toLocaleString();
};
/** "11 Jul, 7:30 PM": the one-line form of a run's time. The opened pieces keep the long form. */
const whenShort = (value?: string) => {
  const date = value ? new Date(value) : null;
  return !date || Number.isNaN(date.valueOf()) ? '·' : date.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' });
};
const countText = (value?: number) => value == null ? '·' : value.toLocaleString();

// A card on the page's grid. A card whose body is rows (overflow-hidden) is flush: the rows carry the padding.
function Card({ children, className = '', style }: { children: ReactNode; className?: string; style?: CSSProperties }) {
  const flush = /\boverflow-hidden\b/.test(className);
  return <div className={`sem-card ${flush ? 'sem-card-flush ' : ''}${className}`} style={style}>{children}</div>;
}

function Banner({ children, color }: { children: ReactNode; color: string }) {
  // A notice is a line of plain text with a state dot, not a coloured box.
  const dot = color === 'var(--sem-bad)' ? 'sem-chip-bad' : color === 'var(--sem-warn)' ? 'sem-chip-warn' : 'sem-chip-ok';
  return <div className={`sem-chip ${dot}`} style={{ color: 'var(--sem-fg)', whiteSpace: 'normal', height: 'auto' }}>{children}</div>;
}

// A result is a word and a dot (Instrument): the dot carries the state's colour, the word stays readable text.
// Off and Not run are quiet words with no dot; Could not check keeps its own colourless slate dot.
function StateWord({ word, color, title }: { word: string; color: string; title?: string }) {
  const quiet = color === 'var(--sem-muted)';
  return <span className="sem-chip w-max" title={title} style={quiet ? undefined : { color: 'var(--sem-fg)' }}>
    {!quiet && <StateDot color={color} />}{word}</span>;
}
// The state dot in a colour of the state's own. .sem-chip-ok/-warn/-bad fix the dot to three colours; the
// not-checked slate needs its own, so the dot is drawn here at the primitive's size.
function StateDot({ color }: { color: string }) {
  return <span aria-hidden className="inline-block size-1 shrink-0 rounded-full" style={{ background: color }} />;
}
function Chip({ chip, title }: { chip: ResultChip; title?: string }) {
  return <StateWord word={chip} color={CHIP_COLOR[chip]} title={title} />;
}

function CountPill({ chip, title }: { chip: CountChip; title?: string }) {
  return <StateWord word={chip} color={COUNT_CHIP_COLOR[chip]} title={title} />;
}

// A control inside a clickable row stops the ROW toggling, and nothing else. The WINDOW keydown the drawer
// listens on must still hear Escape: swallowing it is exactly why Astra could not close the drawer from a
// row action (journey D). This helper takes only the click, never the keydown.
const stopRowToggle = (event: { stopPropagation: () => void }) => event.stopPropagation();

export function TestsView({ navTarget, onNavConsumed, onOpenDaxLab, page = 'checks', onPage }: {
  navTarget?: { ref: string; nonce: number } | null;
  onNavConsumed?: (nonce: number) => void;
  onOpenDaxLab?: (measureRef: string) => void;
  onOpenSavedReports?: () => void;
  /** Which of Validation's two tabs is open. Reports used to be a page of its own in the Checks area; Kane
   *  made it a tab on this page on 22 September 2026. It is still a ROUTE (`evidence`), so the shell owns
   *  which tab is open and an old link still lands on it. */
  page?: 'checks' | 'reports';
  onPage?: (page: 'checks' | 'reports') => void;
}) {
  const { conn, openConnections, connectionsOpen } = useConnection();
  const [run, setRun] = useState<TestRunW | null>(null);
  const [suite, setSuite] = useState<TestSuiteInfoW | null>(null);
  const [sources, setSources] = useState<SqlSourceRecord[] | null>(null);
  const [mappings, setMappings] = useState<TableMappingListW | null>(null);
  const [history, setHistory] = useState<TestHistoryW | null>(null);
  // The eight newest recorded runs, each opened once: what the history card and Before and after draw from.
  const [runDetails, setRunDetails] = useState<Record<string, TestRunDetailW>>({});
  const [busy, setBusy] = useState(false);
  // How far the run this page started has got. The page names its run, so only its own progress moves the count.
  const [progress, setProgress] = useState<RunProgress | null>(null);
  const ownRunId = useRef<string | null>(null);
  const [loadingSuite, setLoadingSuite] = useState(true);
  const [reportOpen, setReportOpen] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [staleReason, setStaleReason] = useState<'model' | 'suite' | null>(null);
  // The assistant's own run of checks (show-checks, 12 Oct): its call shows as running, its result becomes the run on screen.
  const [assistantRun, setAssistantRun] = useState<AssistantRun>(NO_ASSISTANT_RUN);
  const [recordNote, setRecordNote] = useState<string | null>(null);
  // One sentence for the other tools the assistant ran here (a single check tried, a run recorded, ...), and the page reads again what they changed.
  const [assistantNote, setAssistantNote] = useState<ToolNote | null>(null);
  const evidenceRevision = useRef(0);
  const [author, setAuthor] = useState<AuthorShape | null>(null);
  const [editing, setEditing] = useState<TestDefinitionW | null>(null);
  const [authorFocus, setAuthorFocus] = useState<AuthorFocus>(undefined);
  // The host a check named that is not saved: the add-a-source form opens with it filled in.
  const [authorHost, setAuthorHost] = useState<string | undefined>(undefined);
  const [authorAddSource, setAuthorAddSource] = useState(false);
  const [mappingTable, setMappingTable] = useState<string | null>(null);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  // H2. The latest result PER CHECK, each with the date of the run that produced it. A part rerun folds
  // only what it ran into this map, so an untouched row keeps its own older result and its own older date.
  const [latest, setLatest] = useState<Record<string, StoredResult<ReconcileOutcomeW>>>({});
  // H2 again, for the AUTOMATIC families. A run that skipped relationships used to render its own empty
  // report, which the card read as "this model has no relationships to check" (Astra, finding 4). The last
  // real result is kept with the date it was taken and the model it was taken for, and only a run that
  // actually checked them replaces it.
  const [relationships, setRelationships] = useState<RetainedFamily<RelationshipReportW> | null>(null);

  useEffect(() => {
    if (!navTarget?.ref) return;
    // Impact's "Add a check" hands over a measure, or, when the field is a column with no measure to pick,
    // that column's TABLE for a row count. The table half used to arrive and be ignored, so the person landed
    // on the Tests page with nothing open and their context dropped (Astra).
    if (navTarget.ref.startsWith('table:')) {
      setMappingTable(navTarget.ref.slice('table:'.length));
      onNavConsumed?.(navTarget.nonce);
      return;
    }
    if (!navTarget.ref.startsWith('measure:')) return;
    setEditing({ id: '', kind: 'measureValue', title: '', targetRef: navTarget.ref, enabled: true } as TestDefinitionW);
    setAuthor('measureValue');
    onNavConsumed?.(navTarget.nonce);
  }, [navTarget?.nonce]);
  // Read the stored waivers again and lay them over what the page shows. The engine says which of them is in force (the check shows the very
  // difference that was accepted), so the page works out nothing itself. Called after Waive and Undo waive, and when the assistant did one.
  const syncWaivers = useCallback(() => rpc<WaiverW[]>('listWaivers').then((list) => {
    const mine: Record<string, WaiverW> = Object.fromEntries((list ?? []).filter((w) => w.system === CHECK_WAIVER_SYSTEM).map((w) => [w.ruleId, w]));
    const apply = (o: ReconcileOutcomeW): ReconcileOutcomeW => {
      if (o.verdict !== 'Fail' && o.verdict !== 'Suspect') return o;
      const w = mine[o.defId];
      const waiver = w?.inForce ? { by: w.by, when: w.when, note: w.reason } : null;
      return { ...o, waiver, waiverLapsed: !!w && !w.inForce };
    };
    setLatest((current) => Object.fromEntries(Object.entries(current).map(([id, stored]) => [id, { ...stored, outcome: apply(stored.outcome) }])));
    setRun((current) => current ? { ...current, reconciles: (current.reconciles ?? []).map(apply) } : current);
  }).catch(() => undefined), []);
  // A model edit makes the results out of date. Accepting a difference (or taking it back) is not one: the engine keeps it with the checks, not in the
  // model, and says so with a check_waiver activity, which also covers the assistant's waive_finding.
  useEffect(() => onDidChange((n) => { if (!makesResultsOld(n)) return; evidenceRevision.current++; setStaleReason('model'); }), []);
  useEffect(() => onActivity((event) => { if (event.kind === 'check_waiver') void syncWaivers(); }), [syncWaivers]);
  useEffect(() => onProgress((event) => setProgress((current) => foldRunProgress(current, event, ownRunId.current))), []);

  // Hero 26 N5: after a full restart the page can mount before the engine has the model back, so the first listTests fails or comes back empty.
  // A failed read is tried again a few times, and the whole page reads again when the open model arrives or changes.
  const suiteTries = useRef(0);
  const loadSuite = useCallback((): Promise<unknown> => rpc<TestSuiteInfoW>('listTests')
    .then((info) => { suiteTries.current = 0; setSuite(info); setLoadingSuite(false); })
    .catch(() => {
      if (suiteTries.current++ < 4) { setTimeout(() => { void loadSuite(); }, 1500); return; }
      setLoadingSuite(false);
    }), []);
  const loadSources = useCallback(() => listSqlSources().then(setSources).catch(() => setSources([])), []);
  const loadMappings = useCallback(() => rpc<TableMappingListW>('listTableMappings').then(setMappings).catch(() => setMappings({ rows: [] })), []);
  const loadHistory = useCallback(() => rpc<TestHistoryW>('listTestRuns', 20).then(setHistory).catch(() => setHistory({ runs: [] })), []);
  const { id: sessionId } = useSessionIdentity();
  useEffect(() => {
    suiteTries.current = 0;
    void loadSuite(); void loadSources(); void loadMappings(); void loadHistory();
  }, [loadSuite, loadSources, loadMappings, loadHistory, sessionId]);
  // Coming back to the window (a tab switch, a restored window) reads the engine's saved checks again, so a stale page never stays.
  useEffect(() => {
    const again = () => { if (document.visibilityState === 'visible') { void loadSuite(); void loadHistory(); } };
    document.addEventListener('visibilitychange', again);
    window.addEventListener('focus', again);
    return () => { document.removeEventListener('visibilitychange', again); window.removeEventListener('focus', again); };
  }, [loadSuite, loadHistory]);
  // Hero 26 N5: the model arriving late in the SAME session (the host says 'reconnected') reads the saved checks at once, with no focus needed.
  useEffect(() => onReconnect(() => { suiteTries.current = 0; void loadSuite(); void loadHistory(); }), [loadSuite, loadHistory]);
  // The newest recorded run is the last result of every saved check it ran, so a restarted window shows them as they were, dated, not "not run yet".
  // It only fills an empty page: a run made in this window always wins.
  useEffect(() => {
    if (run) return;
    const newest = [...(history?.runs ?? [])].sort((a, b) => new Date(b.when).valueOf() - new Date(a.when).valueOf())[0];
    const checks = newest ? runDetails[newest.runId]?.run?.outcomes?.checks : null;
    if (!newest || !checks?.length) return;
    setLatest((current) => Object.keys(current).length > 0 ? current : foldRunResults(current, checks.filter((c) => !!c.defId).map(recordedOutcome), newest.when));
  }, [history, runDetails, run]);
  useEffect(() => {
    const newest = [...(history?.runs ?? [])].sort((a, b) => new Date(b.when).valueOf() - new Date(a.when).valueOf()).slice(0, 8);
    const missing = newest.filter((record) => !runDetails[record.runId]);
    if (missing.length === 0) return;
    let alive = true;
    void Promise.all(missing.map((record) => rpc<TestRunDetailW>('getTestRun', record.runId).then((detail) => [record.runId, detail] as const).catch(() => null)))
      .then((pairs) => { if (alive) setRunDetails((current) => ({ ...current, ...Object.fromEntries(pairs.filter((pair): pair is readonly [string, TestRunDetailW] => !!pair)) })); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [history]);
  const recorded = useMemo<RecordedRunLike[]>(() => [...(history?.runs ?? [])]
    .sort((a, b) => new Date(b.when).valueOf() - new Date(a.when).valueOf()).slice(0, 8)
    .map((record) => ({
      runId: record.runId, when: record.when,
      checks: (runDetails[record.runId]?.run?.outcomes?.checks ?? []).map((check) => ({
        kind: check.kind, name: check.name, check: check.check, verdict: check.verdict,
        contexts: check.contexts?.map((context) => ({ verdict: context.verdict })), contextsTotal: check.contextsTotal, mismatches: check.mismatches,
      })),
    })), [history, runDetails]);
  // The hub is the only place a SQL source can be added, so the list is re-read the moment it closes.
  // The drawer stays mounted throughout: nothing here closes it, and nothing reopens it.
  const hubWasOpen = useRef(false);
  useEffect(() => {
    if (hubWasOpen.current && !connectionsOpen) void loadSources();
    hubWasOpen.current = connectionsOpen;
  }, [connectionsOpen, loadSources]);
  useEffect(() => onTestSuiteChanged(() => {
    evidenceRevision.current++; setStaleReason('suite'); void loadSuite(); void loadSources(); void loadMappings();
  }), [loadSuite, loadSources, loadMappings]);

  const definitions = suite?.definitions ?? [];
  const enabledCount = definitions.filter((d) => d.enabled).length;
  // From what was RETAINED, never from the newest run: a saved-check-only run reports an empty relationship
  // report, which read as zero and took "Run relationships only" off the menu, so running the saved checks
  // removed the way back to running the relationships (Astra, round two). Null is "not measured yet".
  const relationshipCount = relationshipMenuCount(relationships);
  const countableTables = (mappings?.rows ?? []).filter((row) => row.canCount).length;
  const groups = runScopeGroups(enabledCount, relationshipCount, countableTables);

  // The engine's runTests(persist, origin, only, sections): the origin is the second argument. Leaving it out sent the
  // ticked ids into the origin string, so Run this one failed (UAT-F-S06b-2-012); rpc-argument-parity.test.mjs holds the shape.
  // ONE run path. `only` is null for everything; a named subset is the ticked ids, which the page has
  // already refused if empty. `sections` only ever carries a family the engine recognises, never a tab name.
  // What a finished run does to the page, whoever ran it: the person's Run button or the assistant's run_tests.
  const adoptRun = (next: TestRunW, startedAt: number, persisted: boolean) => {
    setRun(next);
    if (next.reconciles?.length) {
      setLatest((current) => foldRunResults(current, next.reconciles ?? [], next.when ?? new Date().toISOString()));
    }
    const modelKey = next.modelName || next.environment || 'this model';
    setRelationships((current) => foldFamilyResult(current, {
      ran: familyRan(next.scope, 'relationships'),
      value: next.relationships,
      whenIso: next.when ?? new Date().toISOString(),
      modelKey,
    }));
    setStaleReason((reason) => evidenceRevision.current === startedAt ? null : reason);
    if (next.error) setErr(next.error);
    // A run re-reads the counts, and a recorded one changes the history it just wrote to. Astra found
    // History still showing July after a September recorded run because nothing invalidated it.
    void loadMappings();
    if (persisted) void loadHistory();
  };
  // The assistant's run. Its call opens the page on "running", its result is the run on screen (the same fold as the person's own),
  // and a run that did not happen says why. A person pressing Run takes the page back.
  useEffect(() => onAssistantCall((call) => setAssistantRun((view) => runStarted(view, call, Date.now()))), []);
  useEffect(() => {
    if (assistantRun.phase !== 'running') return;
    const timer = window.setInterval(() => setAssistantRun((view) => runStale(view, Date.now())), 15_000);
    return () => window.clearInterval(timer);
  }, [assistantRun.phase]);
  useClaudeReflection('run_tests', (event) => {
    const next = runFinished(assistantRun, event);
    setAssistantRun(next);
    if (next.phase === 'done' && next.run) { setErr(null); setRecordNote(null); adoptRun(next.run as TestRunW, evidenceRevision.current, !!next.run.persisted); }
  });

  const takeToolEvent = (event: { seq?: number; kind?: string; ok?: boolean; label?: string; error?: string | null }) => {
    const taken = toolNote(event);
    if (!taken) return;
    setAssistantNote(taken.note);
    if (taken.reload.includes('suite')) announceTestSuiteChanged();
    if (taken.reload.includes('history')) void loadHistory();
    if (taken.reload.includes('sources')) void loadSources();
    if (taken.reload.includes('mappings')) void loadMappings();
  };
  // TOOL_NOTE_KINDS is a fixed list, so the hooks run in the same order on every render.
  // eslint-disable-next-line react-hooks/rules-of-hooks
  for (const kind of TOOL_NOTE_KINDS) useClaudeReflection(kind, takeToolEvent);

  const runSuite = (options: { only?: string[] | null; sections?: string[] | null; persist?: boolean }) => {
    const startedAt = evidenceRevision.current;
    const runId = `page-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    ownRunId.current = runId;
    setProgress(null);
    setBusy(true);
    setErr(null);
    setRecordNote(null);
    setAssistantRun(personRan);
    setAssistantNote(null);
    rpcPressed<TestRunW>('runTests', options.persist ?? false, 'human', options.only ?? null, options.sections ?? null, runId)
      .then((next) => adoptRun(next, startedAt, !!options.persist))
      .catch((error: unknown) => setErr(error instanceof Error ? error.message : String(error)))
      .finally(() => { ownRunId.current = null; setProgress(null); setBusy(false); });
  };
  const runEverything = (persist = false) => runSuite({ persist });
  const runTicked = () => {
    const refusal = tickedRunRefusal(ticked.size);
    if (refusal) { setErr(refusal); return; }
    runSuite({ only: [...ticked] });
  };
  const runOne = (defId: string) => runSuite({ only: [defId] });
  const runGroup = (group: RunGroup) => runSuite({ sections: sectionsForGroup(group.id) });

  // Record keeps the run ON SCREEN. Re-executing it would record numbers nobody has looked at, against
  // data that may have moved since (Astra point 2). A refusal comes back as recorded:false with a note.
  const recordThisRun = () => {
    if (!run?.runId) { setRecordNote('This run has no id to record. Run the checks again, then record it.'); return; }
    setBusy(true); setRecordNote(null);
    rpc<TestRunRecordResultW>('recordTestRun', run.runId)
      .then((result) => {
        setRecordNote(result.note ?? null);
        if (result.recorded) { setRun((current) => current ? { ...current, persisted: true } : current); void loadHistory(); }
      })
      .catch((e: unknown) => setRecordNote(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  const refused = run?.scope?.mode === 'refused';
  const partRun = !!run && !refused && (run.scope?.skipped?.length ?? 0) > 0;
  const testModelLabel = run?.environment || (conn?.connected ? (conn.database || conn.dataSource || 'the connected model') : null);

  const openAuthor = (shape: AuthorShape, definition: TestDefinitionW | null, focus?: AuthorFocus, addSource = false, host?: string) => {
    setEditing(definition); setAuthorFocus(focus); setAuthorAddSource(addSource || !!host); setAuthorHost(host); setAuthor(shape);
  };
  const closeAuthor = () => { setAuthor(null); setEditing(null); setAuthorFocus(undefined); setAuthorAddSource(false); setAuthorHost(undefined); };
  // 5. One identity. Choose source on a table row and New check > Table row count open the SAME setup,
  // because for a table count the MAPPING is the check (the engine refuses a tableRowCount test kind).
  const openTableMapping = (table?: string) => setMappingTable(table ?? '');
  // Adding a source happens in the check's own drawer (no trip to Connections): New check > Add a SQL source opens a check against SQL with the form showing.
  const addSqlSource = () => openAuthor('measureReconcile', null, undefined, true);

  const running = busy || assistantRun.phase === 'running';
  // One note at most, said once, under the verdict, in the first of these that has something to say: a failure, a refused run, a change
  // since the run, a part run, the assistant's own words. Up to five notes stacked here, two of them near copies, read as noise (UAT 1.5.104).
  const staleText = run && staleReason ? (staleReason === 'suite'
    ? 'A saved check changed after this run. The results below still show the older check, so you can compare. Run again for current results.'
    : 'The model changed. These results may be out of date. Run again to check your latest changes.') : '';
  const note = oneNotice([
    err ? { key: 'err', tone: 'bad', text: err } : null,
    refused ? { key: 'refused', tone: 'warn', text: run?.note ?? 'That run asked for nothing, so nothing ran.' } : null,
    assistantRun.phase === 'failed' ? { key: 'assistant-failed', tone: 'warn', text: plainToolNote(runLine(assistantRun)) } : null,
    staleText ? { key: 'stale', tone: 'warn', text: staleText } : null,
    recordNote ? { key: 'record', tone: 'warn', text: recordNote } : null,
    partRun && run?.scope && run.health ? { key: 'part-run', tone: 'warn', text: partRunSummary(run.scope.gradeCovers, run.scope.skipped ?? []) } : null,
    assistantNote ? { key: 'assistant-note', tone: assistantNote.ok ? 'ok' : 'warn', text: plainToolNote(assistantNote.text) } : null,
    runLine(assistantRun) ? { key: 'assistant-run', tone: 'ok', text: plainToolNote(runLine(assistantRun)) } : null,
    run?.note && !refused && !partRun ? { key: 'run-note', tone: 'warn', text: run.note } : null,
  ]);
  const notices = note && <div className="va-notice" data-note={note.key} data-assistant-run={note.key.startsWith('assistant') ? assistantRun.phase : undefined}
    data-assistant-note={note.key === 'assistant-note' ? assistantNote?.kind : undefined}><Banner color={note.tone === 'bad' ? 'var(--sem-bad)' : note.tone === 'ok' ? 'var(--sem-ok)' : 'var(--sem-warn)'}>{note.text}</Banner></div>;

  return (
    <div className="h-full overflow-auto">
      {author && <AuthorDrawer shape={author} editing={editing} focus={authorFocus} latestSources={sources} onClose={closeAuthor}
        onSaved={() => { void loadSuite(); void loadMappings(); }} addSourceOnOpen={authorAddSource} addSourceHost={authorHost} onSourcesChanged={() => { void loadSources(); }} />}
      {mappingTable != null && <AuthorDrawer shape="tableRowCount" table={mappingTable} latestSources={sources} onClose={() => setMappingTable(null)}
        onSaved={() => { void loadMappings(); setMappingTable(null); }} onSourcesChanged={() => { void loadSources(); }} />}
      {reportOpen && <ReportExportDialog modelName={run?.modelName} onClose={() => setReportOpen(false)} />}
      <main className="sem-page vc-page" data-testid="validation-A">
        <h1 className="sr-only">Validation</h1>
        {/* Checks and Reports are still two real routes, so the area strip, a deep link and Kept reports in the "..." menu all agree
            about which one is open. Reports is not a tab any more: it is reached from the menu and says how to come back. */}
        {page === 'reports'
          ? <>
            <div className="va-reports-head" data-testid="v-head">
              <h2 className="sem-section-title">Kept reports</h2>
              <button type="button" className="sem-btn" data-page-tab="checks" onClick={() => onPage?.('checks')}>Back to checks</button>
            </div>
            <EvidenceBody />
          </>
          : <div className="va-col">
            <ValidationBoard
              definitions={definitions} latest={latest} loading={loadingSuite} liveModel={!!testModelLabel}
              ticked={ticked} onTick={setTicked}
              onEdit={(def, focus, host) => openAuthor(def.kind === 'measureValue' ? 'measureValue' : 'measureReconcile', def, focus, false, host)}
              onNew={() => openAuthor('measureValue', null)}
              onRunOne={runOne} onChanged={() => { void loadSuite(); void loadMappings(); }}
              onConnect={() => openConnections()} onOpenDaxLab={onOpenDaxLab}
              retained={relationships} relSkipped={!!run && !familyRan(run.scope, 'relationships')} hasRun={!!run}
              mappings={mappings} countsSkipped={!!run && !familyRan(run.scope, 'table counts')} onMapSource={openTableMapping}
              onRunSection={(id) => runSuite({ sections: sectionsForGroup(id) })} busy={busy}
              history={history} recorded={recorded} runKey={run?.runId ?? ''}
              run={run} running={running} progress={progress} partRun={partRun} notices={notices}
              groups={groups} onRunAll={() => runEverything(false)} onRunTicked={runTicked} onRunGroup={runGroup} onRecord={recordThisRun}
              onNewPick={(shape) => shape === 'tableRowCount' ? openTableMapping() : openAuthor(shape, null)} onAddSqlSource={addSqlSource}
              onOpenReport={() => setReportOpen(true)} onOpenReports={() => onPage?.('reports')} onWaiverChanged={() => { void syncWaivers(); }} />
          </div>}
      </main>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// 1. Run, and the scope menu that says exactly what "all" includes.
// ---------------------------------------------------------------------------------------------------
function RunMenu({ busy, groups, onRunAll, onRunGroup, onChoose }: {
  busy: boolean; groups: RunGroup[]; onRunAll: () => void; onRunGroup: (group: RunGroup) => void; onChoose: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLSpanElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key !== 'Escape') return; setOpen(false); buttonRef.current?.focus(); };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  const pick = (fn: () => void) => { setOpen(false); fn(); };
  return (
    <span ref={wrap} className="va-rel va-split">
      <button type="button" disabled={busy} className="sem-btn sem-btn-primary" data-testid="v-run" onClick={onRunAll}>{busy ? 'Running…' : 'Run checks'}</button>
      <button ref={buttonRef} type="button" aria-label="What to run" aria-haspopup="menu" aria-expanded={open} disabled={busy}
        className="sem-btn sem-btn-primary" onClick={() => setOpen((v) => !v)}>{Icon.down}</button>
      {open && (
        <div role="menu" aria-label="What to run" className="va-pop va-pop-right va-pop-wide">
          {groups.length === 0
            ? <div className="va-pop-note">Nothing to run yet. Add a check, or map a table to a SQL source.</div>
            : groups.map((group) => (
              // Each row names its own family, so no two rows read the same (Astra, finding 3).
              <button key={group.id} type="button" role="menuitem" aria-label={runOnlyLabel(group)} className="va-item va-item-count"
                onClick={() => pick(() => onRunGroup(group))}>
                <span>{group.label}</span>
                <span className="va-faint va-num">{group.count ?? 'every one'}</span>
              </button>
            ))}
          <button type="button" role="menuitem" className="va-item" onClick={() => pick(onChoose)}>Choose checks…</button>
        </div>
      )}
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------
// 2. One status line.
// ---------------------------------------------------------------------------------------------------
/** While a run goes: the count in words and a thin bar. Before the first count arrives it is just the words. */
function RunProgressLine({ progress }: { progress: RunProgress | null }) {
  const percent = runProgressPercent(progress);
  return (
    <div className="sem-tests-progress basis-full" role="status" aria-live="polite">
      <span className="sem-num">{runProgressText(progress)}</span>
      {percent !== null && (
        <div role="progressbar" aria-label="Checks run" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
          style={{ marginTop: 'var(--sem-space-2)', height: 'var(--sem-space-1)', borderRadius: 'var(--sem-radius-chip)', background: 'var(--sem-border)', overflow: 'hidden' }}>
          <div style={{ width: `${percent}%`, height: '100%', background: 'var(--sem-accent)', transition: 'width 200ms ease-out' }} />
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// 3. ONE list of saved checks.
// ---------------------------------------------------------------------------------------------------
function parseValueParams(def?: TestDefinitionW): MeasureValueParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as MeasureValueParamsW; } catch { return {}; }
}
function parseReconcileParams(def?: TestDefinitionW): ReconcileParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as ReconcileParamsW; } catch { return {}; }
}
function shortRef(value?: string): string {
  if (!value) return '·';
  const engine = /^(?:measure|column|table):([^/]+)(?:\/(.+))?$/.exec(value);
  if (engine) return engine[1];
  const dax = /^'((?:''|[^'])+)'\[(.+)\]$/.exec(value);
  return dax ? dax[1].replace(/''/g, "'") : value;
}
// Where a check was AGREED is part of reading it later: the same SQL against a different model is a
// different claim. It is a note, not a pin, so the wording never suggests the run is tied to it.
// A trusted-answer check can ask the measure as the model holds it, or a formula the person edited in DAX
// Lab. Those are different claims, so the row says which one before anyone reads the verdict.
function asksSummary(def: TestDefinitionW): string {
  if (def.kind !== 'measureValue') return '';
  const asks = parseValueParams(def).expressionDax ? 'an edited formula' : 'the measure as saved';
  return ` \u00b7 asks ${asks}`;
}

function filterSummary(def: TestDefinitionW): string {
  if (def.kind === 'measureValue') {
    const p = parseValueParams(def);
    return p.filterDax || (p.filterColumn ? `${p.filterColumn} = ${p.filterValue ?? ''}` : 'all rows');
  }
  return parseReconcileParams(def).filterDax || 'all rows';
}
// ---------------------------------------------------------------------------------------------------
// Validation A (Kane picked the minimal redesign on 16 Oct: "one health line, then only what failed; the passes behind one link").
//
// The page answers one question in one glance: "N of M pass", a strip with one cell per check, one short line (how many differ,
// how many could not be checked, when it ran), then ONLY the checks that need a look, one line each with its gap. Pressing a
// line opens that check's differences and its visual in place, one at a time. The checks that pass sit behind one link.
// Everything else has one home: Run checks and its arrow (what to run), and the "..." menu (New check, the report of this run,
// kept reports, run history and grade, record this run). A check's own "..." holds Edit, Draw as, Choose what a blank means,
// Turn off and Delete. Relationships and Table row counts are one line each, like any other check.
// The numbers are unchanged: the engine's verdict per row, its own difference, nothing worked out here (validation-model.ts).
// ---------------------------------------------------------------------------------------------------

/** The visual Jev chose (or the rules did), its reason line, and the other visuals that fit; one press redraws. */
function VisualBlock({ cv, pick }: { cv: CheckVisual; pick?: VisualPick }) {
  const chosen = chosenVisual(cv, pick);
  const [override, setOverride] = useState<Kind | null>(null);
  if (!cv.data || !chosen.kind) return null;
  const kind = override && cv.fits.includes(override) ? override : chosen.kind;
  return <div className="va-visual">
    <Visual kind={kind} data={cv.data} compact />
    <PickLine pick={chosen.kind} fits={cv.fits} reason={chosen.reason} current={kind} onPick={setOverride} by={chosen.by} />
  </div>;
}

/** Differs, then could not check: the order a person needs to look in. Checks that have not run fold into one line of their own. */
const NEEDS_A_LOOK: ResultChip[] = ['Differs', 'Could not check'];
/** More problems than this and the first view shows this many, then one line that opens the rest. */
const PROBLEMS_SHOWN = 10;
/** An opened pass list longer than this gets a box that finds a check by name. */
const PASS_FILTER_FROM = 20;

type BoardRow = { def: TestDefinitionW; outcome?: ReconcileOutcomeW; whenIso?: string; chip: ResultChip };
interface BoardItem { id: string; name: string; chip: ResultChip; finding: string; row?: BoardRow; family?: 'relationships' | 'rowcounts' }
type PanelId = 'history' | 'new' | 'choose' | null;

/** The gap, in a few words, for a row: the engine's own difference, or why there is none. */
function findingOf(row: BoardRow): string {
  const { def, outcome, chip } = row;
  if (chip === 'Off') return 'Turned off';
  if (chip === 'Not run') return 'Not run yet';
  // An accepted check keeps showing the difference it accepted: it stays visible, it is just no longer counted as a failure.
  if (chip === 'Differs' || chip === 'Accepted') return gapShort(outcome?.difference, outcome?.mismatches, outcome?.rows, outcome?.expected);
  if (chip === 'Could not check') {
    const reason = unknownCellText(outcome);
    // The row says the reason in a few words; the opened check says the rest.
    return !reason || /Open this row/.test(reason) ? 'Could not check' : reason.split('. ')[0].replace(/\.$/, '');
  }
  return def.kind === 'measureValue' ? 'Holds' : (outcome?.actual != null ? plainFigure(outcome.actual) : 'Matches');
}

function ValidationBoard({
  definitions, latest, loading, liveModel, ticked, onTick, onEdit, onNew, onRunOne, onChanged, onConnect, onOpenDaxLab,
  retained, relSkipped, hasRun, mappings, countsSkipped, onMapSource, onRunSection, busy, history, recorded, runKey,
  run, running, progress, partRun, notices, groups, onRunAll, onRunTicked, onRunGroup, onRecord, onNewPick, onAddSqlSource, onOpenReport, onOpenReports, onWaiverChanged,
}: {
  definitions: TestDefinitionW[]; latest: Record<string, StoredResult<ReconcileOutcomeW>>; loading: boolean; liveModel: boolean;
  ticked: Set<string>; onTick: (next: Set<string>) => void;
  onEdit: (def: TestDefinitionW, focus?: AuthorFocus, sourceHost?: string) => void; onNew: () => void;
  onRunOne: (defId: string) => void; onChanged: () => void; onConnect: () => void; onOpenDaxLab?: (measureRef: string) => void;
  retained: RetainedFamily<RelationshipReportW> | null; relSkipped: boolean; hasRun: boolean;
  mappings: TableMappingListW | null; countsSkipped: boolean; onMapSource: (table: string) => void;
  onRunSection: (id: RunGroup['id']) => void; busy: boolean;
  history: TestHistoryW | null; recorded: RecordedRunLike[]; runKey: string;
  run: TestRunW | null; running: boolean; progress: RunProgress | null; partRun: boolean; notices: ReactNode;
  groups: RunGroup[]; onRunAll: () => void; onRunTicked: () => void; onRunGroup: (group: RunGroup) => void; onRecord: () => void;
  onNewPick: (shape: AuthorShape) => void; onAddSqlSource: () => void; onOpenReport: () => void; onOpenReports: () => void;
  /** A check was waived or taken back: the page reads the stored waivers again. */
  onWaiverChanged: () => void;
}) {
  const askOn = useFeature('askData') === 'granted';
  const [open, setOpen] = useState<string | null>(null);
  const [showPass, setShowPass] = useState(false);
  const [showAllProblems, setShowAllProblems] = useState(false);
  const [showNotRun, setShowNotRun] = useState(false);
  const [notRunWhy, setNotRunWhy] = useState<string | null>(null);
  const [passFilter, setPassFilter] = useState('');
  const [panel, setPanel] = useState<PanelId>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const [picks, setPicks] = useState<Record<string, VisualPick>>({});
  // 10b. A failed row action writes its reason into ITS OWN row. Astra turned a check off with a save
  // error injected: the menu closed, the check stayed on, and nothing at all was said.
  const [rowError, setRowError] = useState<Record<string, string>>({});
  const setRowErrorFor = (id: string, message: string | null) => setRowError((current) => {
    const next = { ...current };
    if (message) next[id] = message; else delete next[id];
    return next;
  });

  const rows = useMemo<BoardRow[]>(() => definitions.map((def) => {
    const stored = latest[def.id];
    return { def, outcome: stored?.outcome, whenIso: stored?.whenIso, chip: resultChip(def.enabled, stored?.outcome) };
  }), [definitions, latest]);

  const toggleTick = (id: string) => { const next = new Set(ticked); if (next.has(id)) next.delete(id); else next.add(id); onTick(next); };
  const toggleEnabled = (def: TestDefinitionW) => {
    setRowErrorFor(def.id, null);
    // The switch changes one flag, so it flips it on the check as it stands NOW: the row may be older than an edit
    // the assistant just saved (save_test), and writing the row's copy back would undo that edit.
    rpc<TestSuiteInfoW>('listTests')
      .then((suite) => { const live = (suite.definitions ?? []).find((d) => d.id === def.id) ?? def; return rpc('saveTest', { ...live, enabled: !def.enabled }); })
      .then(() => { announceTestSuiteChanged(); onChanged(); })
      .catch((e: unknown) => setRowErrorFor(def.id, `Could not turn this check ${def.enabled ? 'off' : 'on'}: ${e instanceof Error ? e.message : String(e)}`));
  };
  // Waive: accept the difference a check shows. One press, the note is optional, and Undo waive takes it back in one press. The engine writes
  // it to the same store the findings' waivers use, so the assistant's waive_finding (system 'test') is the same act.
  const waive = (def: TestDefinitionW, note: string) => {
    setRowErrorFor(def.id, null);
    rpc('waiveFinding', CHECK_WAIVER_SYSTEM, def.id, null, note.trim() || null, 'human').then(() => onWaiverChanged())
      .catch((e: unknown) => setRowErrorFor(def.id, `Could not accept this difference: ${plainCheckError(e instanceof Error ? e.message : String(e))}`));
  };
  const unwaive = (def: TestDefinitionW) => {
    setRowErrorFor(def.id, null);
    rpc('unwaiveFinding', CHECK_WAIVER_SYSTEM, def.id, null, 'human').then(() => onWaiverChanged())
      .catch((e: unknown) => setRowErrorFor(def.id, `Could not take this back: ${plainCheckError(e instanceof Error ? e.message : String(e))}`));
  };
  const remove = (def: TestDefinitionW) => {
    setRowErrorFor(def.id, null);
    rpc<boolean>('deleteTest', def.id).then(() => { setConfirmId(null); announceTestSuiteChanged(); onChanged(); })
      .catch((e: unknown) => { setConfirmId(null); setRowErrorFor(def.id, `Could not delete this check: ${e instanceof Error ? e.message : String(e)}`); });
  };

  // ---- the parts that are not saved checks: relationships, table counts, and the two recorded-run views
  const rels = retained?.value.relationships ?? [];
  const relTally: RelationshipTally = relationshipTally(rels.map(relationshipChecks));
  const relChip: ResultChip | null = rels.length > 0 ? relationshipChip(relTally) : null;
  const relVisual = useMemo(() => relationshipVisual(rels.map((r) => ({
    name: r.name, manyTable: r.manyTable, oneTable: r.oneTable, manyRowCount: r.manyRowCount,
    refVerdict: verdictKey(r.referentialIntegrity.verdict), refCount: r.referentialIntegrity.count,
  }))), [rels]);
  const countRows = mappings?.rows ?? [];
  const countsVis = useMemo(() => countsVisual(countRows.map((r) => ({ table: r.modelTable, model: r.lastModelCount, source: r.lastSourceCount }))), [countRows]);
  // A calculated table has nothing to count against, and a row nobody made is not a check: see countsFamilyChip.
  const countsChip: ResultChip | null = countsFamilyChip(countRows);
  const changes = useMemo(() => changesVisual(recorded), [recorded]);
  const histVisual = useMemo(() => historyVisual(recorded), [recorded]);

  // ---- Jev picks every check's visual in ONE call per run: words and counts only (checks.visual_kind, Pro, off until
  // its bar is cleared). The rules' pick is drawn and labelled as the rules' pick whenever Jev is off, unsure or not asked.
  const visuals = useMemo(() => {
    const out: Record<string, CheckVisual> = {};
    for (const { def, outcome } of rows) {
      if (!def.enabled || !outcome) continue;
      const params = def.kind === 'measureValue' ? parseValueParams(def) : parseReconcileParams(def);
      out[def.id] = checkVisual(outcome, {
        title: def.title, sourceLabel: (params as ReconcileParamsW).sqlSourceName || 'the source', trusted: def.kind === 'measureValue',
        toleranceRelative: (params as { toleranceRelative?: number }).toleranceRelative, expectedText: (params as MeasureValueParamsW).expectedValue,
      });
    }
    if (relVisual.data) out.relationships = relVisual;
    if (countsVis.data) out.rowcounts = countsVis;
    return out;
  }, [rows, relVisual, countsVis]);
  const ask = useMemo(() => visualAsk([
    ...rows.filter(({ def }) => visuals[def.id]).map<VisualAskCheck>(({ def, outcome, chip }) => ({
      id: def.id, title: def.title, measure: shortRef(def.targetRef), kindWord: kindLabel(def.kind), status: statusOf(chip),
      rows: outcome?.rows?.length ?? 1, hasPrior: false, runs: recorded.length, hasSplit: (outcome?.rows?.length ?? 0) > 1,
      fits: visuals[def.id].fits, rulesPick: visuals[def.id].rulesPick ?? '',
    })),
    ...(visuals.relationships ? [{ id: 'relationships', title: 'Relationships', measure: 'relationships', kindWord: 'Relationships', status: statusOf(relChip ?? 'Not run'),
      rows: rels.length, hasPrior: false, runs: recorded.length, hasSplit: rels.length > 1, fits: visuals.relationships.fits, rulesPick: visuals.relationships.rulesPick ?? '' }] : []),
    ...(visuals.rowcounts ? [{ id: 'rowcounts', title: 'Table row counts', measure: 'table counts', kindWord: 'Table row counts', status: statusOf(countsChip ?? 'Not run'),
      rows: countRows.length, hasPrior: false, runs: recorded.length, hasSplit: countRows.length > 1, fits: visuals.rowcounts.fits, rulesPick: visuals.rowcounts.rulesPick ?? '' }] : []),
  ]), [rows, visuals, rels.length, relChip, countRows.length, countsChip, recorded.length]);
  const askKey = ask ? `${runKey}|${JSON.stringify(ask.checks.map((c) => [c.id, c.status, c.rows, c.rulesPick]))}` : '';
  // Turning a check off or on takes it out of or back into the ask but changes nothing about the run, so it reuses the run's picks:
  // a new call is made only for a new run, or for a check this run's call did not name.
  const asked = useRef<{ run: string; ids: Set<string> }>({ run: '', ids: new Set() });
  useEffect(() => {
    if (!askOn || !ask || !runKey) return;
    if (asked.current.run === runKey && ask.checks.every((c) => asked.current.ids.has(c.id))) return;
    asked.current = { run: runKey, ids: new Set([...(asked.current.run === runKey ? asked.current.ids : []), ...ask.checks.map((c) => c.id)]) };
    // A pick is kept while its run is still the newest, even if the effect ran again meanwhile (a check turned off mid-call).
    const run = runKey;
    rpc<VisualPicks>('pickCheckVisuals', ask)
      .then((result) => { if (asked.current.run === run) setPicks((current) => ({ ...current, ...Object.fromEntries((result?.picks ?? []).map((p) => [p.id, p])) })); })
      .catch(() => { if (asked.current.run === run) asked.current = { run: '', ids: new Set() }; });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [askOn, askKey]);

  // ---- one line per check, the two families included
  const items: BoardItem[] = [
    ...rows.map<BoardItem>((row) => ({ id: row.def.id, name: readableTitle(row.def.title), chip: row.chip, finding: findingOf(row), row })),
    ...(relChip ? [{
      id: 'relationships', name: 'Relationships', chip: relChip, family: 'relationships' as const,
      finding: relTally.attention > 0 ? `${relTally.attention.toLocaleString()} ${relTally.attention === 1 ? 'needs' : 'need'} attention`
        : relTally.unknown > 0 ? `${relTally.unknown.toLocaleString()} could not be checked` : `All ${rels.length.toLocaleString()} match`,
    }] : []),
    ...(countsChip ? [{
      id: 'rowcounts', name: 'Table row counts', chip: countsChip, family: 'rowcounts' as const,
      finding: countsFamilyFinding(countRows),
    }] : []),
  ];
  const rank = (chip: ResultChip) => (chip === 'Differs' ? 0 : chip === 'Could not check' ? 1 : chip === 'Accepted' ? 1.5 : chip === 'Not run' ? 2 : chip === 'Pass' ? 3 : 4);
  const ordered = [...items].sort((a, b) => rank(a.chip) - rank(b.chip) || a.name.localeCompare(b.name));
  const problems = ordered.filter((item) => NEEDS_A_LOOK.includes(item.chip));
  const notRunItems = ordered.filter((item) => item.chip === 'Not run');
  // Accepted differences are not buried with the passes: they stay in view in a list of their own.
  const acceptedItems = ordered.filter((item) => item.chip === 'Accepted');
  const quiet = ordered.filter((item) => !NEEDS_A_LOOK.includes(item.chip) && item.chip !== 'Not run' && item.chip !== 'Accepted');
  const shownProblems = showAllProblems || problems.length <= PROBLEMS_SHOWN ? problems : problems.slice(0, PROBLEMS_SHOWN);
  const filterOn = showPass && quiet.length > PASS_FILTER_FROM;
  const wanted = passFilter.trim().toLowerCase();
  const shownQuiet = filterOn && wanted ? quiet.filter((item) => item.name.toLowerCase().includes(wanted)) : quiet;
  const live = items.filter((item) => item.chip !== 'Off');
  const tally = boardTally(items.map((item) => item.chip));
  const { passed, differs, cannot, accepted } = tally;
  const off = items.length - live.length;
  const anyResult = passed + differs + cannot + accepted > 0;
  // The grade panel talks about the checks that ran, so it keeps its own count.
  const checked = passed + differs + cannot + accepted;
  const headline = boardHeadline(tally);
  // Relationships and Table row counts are added to every run, so the count of checks is never a surprise (round 2 N16).
  // Only this run's own results count: a chip kept from an older run says nothing about what this run added.
  const quickNote = quickChecksNote(!!relChip && relChip !== 'Not run' && !relSkipped, !!countsChip && countsChip !== 'Not run' && !countsSkipped);
  // Every check is in one state, so the line under the headline adds up to the number in it.
  const counts = boardParts(tally).map((part) => <span key={part.kind} className={part.kind === 'differs' ? 'va-bad va-num' : part.kind === 'cannot' ? 'va-warn va-num' : part.kind === 'notRun' ? 'va-num' : undefined}>{part.text}</span>);

  // ---- the head: the verdict first, a strip with one cell per check, one line under it
  const head = <div className="va-head">
    <div className="va-verdict" data-testid="v-verdict">
      {items.length === 0 && !loading
        ? null
        : anyResult
          ? <div className="va-figure" aria-live="polite"><span className="va-n va-num">{headline.big}</span><span className="va-of">{headline.rest}</span></div>
          : <div className="va-figure"><span className="va-n va-num va-faint">{headline.big}</span><span className="va-of">{headline.rest}</span></div>}
      {/* One cell for each check that has a result, drawn only once something has run: a check with no result has nothing to show, and an empty outline means nothing to a person. */}
      {anyResult && <span className="va-strip" data-testid="v-strip" role="img"
        aria-label={boardParts(tally).length ? `${passed} pass, ${boardParts(tally).map((part) => part.text).join(', ')}` : `${passed} pass`}>
        {ordered.filter((item) => item.chip !== 'Off' && item.chip !== 'Not run').map((item) => <span key={item.id} className={`va-cell va-c-${moodOf(item.chip)}`} title={`${item.name}: ${item.chip}`} />)}</span>}
      {running
        ? <RunProgressLine progress={progress} />
        : <div className="va-sub" data-testid="v-sub">
          {counts.map((node, i) => <Fragment key={i}>{i > 0 && <span className="va-sep">·</span>}{node}</Fragment>)}
          {run?.when
            ? <><span className="va-sep">·</span><button type="button" className="va-link" onClick={() => setPanel(panel === 'history' ? null : 'history')}
              title="Run history and grade" data-testid="v-when">Ran {whenShort(run.when)}</button></>
            : items.length > 0 && !anyResult && <span>Press Run checks.</span>}
        </div>}
      {!running && anyResult && quickNote && <p className="va-line va-faint" data-testid="v-quick">{quickNote}</p>}
    </div>
    <div className="va-tools" data-testid="v-head">
      <Menu label="More" trigger={Icon.more} className="va-icon-btn" testid="v-more">{(close) => <>
        <Item onClick={() => { close(); setPanel('new'); }}>New check</Item>
        <Item disabled={!run} title={run ? undefined : 'Run the checks first.'} onClick={() => { close(); onOpenReport(); }}>Report of this run</Item>
        <Item onClick={() => { close(); onOpenReports(); }}>Kept reports</Item>
        <Item onClick={() => { close(); setPanel('history'); }}>Run history and grade</Item>
        <Item disabled={!run || !!run.persisted || running} title={!run ? 'Run the checks first.' : run.persisted ? 'This run is already recorded.' : undefined}
          onClick={() => { close(); onRecord(); }}>Record this run</Item>
      </>}</Menu>
      <RunMenu busy={running} groups={groups} onRunAll={onRunAll} onRunGroup={onRunGroup} onChoose={() => setPanel('choose')} />
    </div>
  </div>;

  // ---- one line, and its detail on demand
  const detailFor = (item: BoardItem) => {
    if (item.row) {
      const row = item.row;
      return <CheckDetail def={row.def} outcome={row.outcome} whenIso={row.whenIso} chip={row.chip} visual={visuals[row.def.id] ?? NO_VISUAL} pick={picks[row.def.id]}
        liveModel={liveModel} error={rowError[row.def.id]} onDismissError={() => setRowErrorFor(row.def.id, null)}
        confirming={confirmId === row.def.id} onConfirm={() => setConfirmId(row.def.id)} onCancelConfirm={() => setConfirmId(null)}
        onDelete={() => remove(row.def)} onToggleEnabled={() => toggleEnabled(row.def)}
        onEdit={onEdit} onRunOne={() => onRunOne(row.def.id)} onConnect={onConnect} onOpenDaxLab={onOpenDaxLab}
        onWaive={(note) => waive(row.def, note)} onUnwaive={() => unwaive(row.def)} />;
    }
    return item.family === 'relationships'
      ? <RelationshipsBody retained={retained} skipped={relSkipped} cv={relVisual} pick={picks.relationships} busy={busy} onRun={() => onRunSection('relationships')} />
      : <RowCountsBody list={mappings} skipped={countsSkipped} onMapSource={onMapSource} cv={countsVis} pick={picks.rowcounts} busy={busy} onRun={() => onRunSection('tableCounts')} />;
  };
  const line = (item: BoardItem) => {
    const isOpen = open === item.id;
    const mood = moodOf(item.chip);
    return <div key={item.id} className="va-rowwrap" data-testid={`card-${item.id}`}>
      <button type="button" className={`va-row ${isOpen ? 'va-row-on' : ''}`} aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : item.id)} data-testid={`va-row-${item.id}`}>
        <Mark mood={mood} word={item.chip} />
        <span className="va-name" title={item.name}>{item.name}</span>
        <span className={`va-find va-num ${mood === 'differs' ? 'va-bad' : mood === 'cannot' && item.chip !== 'Not run' ? 'va-warn' : 'va-faint'}`}>{item.finding}</span>
        <span className="va-chev">{isOpen ? Icon.down : Icon.right}</span>
      </button>
      {isOpen && <div className="va-detail" data-testid={`va-detail-${item.id}`}>{detailFor(item)}</div>}
    </div>;
  };

  const emptyCard = (text: ReactNode, id: string) => <section className="sem-card va-empty" data-testid={`empty-${id}`}>{text}</section>;
  const empty = loading && items.length === 0 ? emptyCard('Loading your saved checks…', 'sources')
    : definitions.length === 0 && items.length === 0 ? emptyCard(<div className="flex flex-wrap items-center gap-2">
      <span>Start with a check: compare a number with your database, or save one you trust.</span>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onNew}>New check</button>
    </div>, 'sources') : null;

  // The Run on the "not run yet" row runs those checks: one family alone, one check alone, or everything when they are mixed.
  const runNotRun = () => {
    const why = notRunRunWhy(notRunItems, liveModel);
    setNotRunWhy(why);
    if (why) { setShowNotRun(true); return; }
    const only = notRunItems.length === 1 ? notRunItems[0] : null;
    if (only?.family === 'rowcounts') onRunSection('tableCounts');
    else if (only?.family === 'relationships') onRunSection('relationships');
    else if (only?.row) onRunOne(only.id);
    else onRunAll();
  };

  const closePanel = () => setPanel(null);
  const panelBody = panel === 'history'
    ? <HistoryPanel seen={{ passed, differs, cannot, checked, accepted }} run={run} partRun={partRun} history={history} histVisual={histVisual} changes={changes} picks={picks} recorded={recorded} busy={running} onRecord={onRecord} onClose={closePanel} />
    : panel === 'new'
      ? <Panel title="New check" onClose={closePanel}><div className="va-choices"><NewCheckChoices onPick={(shape) => { closePanel(); onNewPick(shape); }} onAddSqlSource={() => { closePanel(); onAddSqlSource(); }} /></div></Panel>
      : panel === 'choose'
        ? <Panel title="Choose checks to run" onClose={closePanel}>
          {rows.filter((r) => r.def.enabled).length === 0 ? <p className="va-line va-faint">No check is turned on.</p>
            : <div className="va-pick" role="group" aria-label="Checks to run">{rows.filter((r) => r.def.enabled).map((r) => <label key={r.def.id} className="va-pickrow">
              <input type="checkbox" checked={ticked.has(r.def.id)} onChange={() => toggleTick(r.def.id)} aria-label={`Tick ${r.def.title} to run on its own`} />
              <span className="va-name">{readableTitle(r.def.title)}</span></label>)}</div>}
          <div className="va-actions-row">
            <button type="button" className="sem-btn sem-btn-primary sem-btn-sm" disabled={running || ticked.size === 0} title={tickedRunRefusal(ticked.size) ?? undefined}
              onClick={() => { closePanel(); onRunTicked(); }}>{ticked.size > 0 ? `Run ${ticked.size.toLocaleString()} chosen` : 'Run the chosen'}</button>
            {ticked.size > 0 && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => onTick(new Set())}>Clear</button>}
          </div></Panel>
        : null;

  return <>
    {head}
    {notices}
    {panelBody}
    {empty}
    {(problems.length > 0 || notRunItems.length > 0) && <section className="sem-card va-list" data-testid="v-problems" aria-label="Checks that need a look">
      {shownProblems.map(line)}
      {shownProblems.length < problems.length && <div className="va-rowwrap"><button type="button" className="va-row va-moreline" onClick={() => setShowAllProblems(true)} data-testid="v-problems-more">
        <span /><span className="va-name">and {(problems.length - shownProblems.length).toLocaleString()} more</span><span /><span className="va-chev">{Icon.right}</span></button></div>}
      {notRunItems.length > 0 && <div className="va-rowwrap" data-testid="v-notrun">
        <div className="va-row va-notrun">
          <Mark mood="off" word="Not run" />
          <button type="button" className="va-link va-name va-notrun-name" aria-expanded={showNotRun} onClick={() => setShowNotRun(!showNotRun)} data-testid="v-notrun-toggle">
            {notRunItems.length === 1 ? notRunItems[0].name : `${notRunItems.length.toLocaleString()} not run yet`}</button>
          <button type="button" className="sem-btn sem-btn-sm" disabled={running} onClick={runNotRun} data-testid="v-notrun-run">Run</button>
          <button type="button" className="va-chev va-chev-btn" aria-label={showNotRun ? 'Hide the checks not run yet' : 'Show the checks not run yet'} aria-expanded={showNotRun}
            onClick={() => setShowNotRun(!showNotRun)} data-testid="v-notrun-arrow">{showNotRun ? Icon.down : Icon.right}</button>
        </div>
        {notRunWhy && <p className="va-note" role="status" data-testid="v-notrun-why">{notRunWhy}</p>}
        {showNotRun && <div className="va-notrun-list">{notRunItems.map(line)}</div>}
      </div>}
    </section>}
    {hasRun && !liveModel && rels.length === 0 && !relSkipped && <p className="va-line va-faint" data-testid="v-norels">
      Relationships need a live test model to count rows. <button type="button" className="va-link" onClick={onConnect}>Connect a test model</button> and run again.</p>}
    {acceptedItems.length > 0 && <section className="sem-card va-list" data-testid="v-accepted" aria-label="Accepted differences">
      {acceptedItems.map(line)}
    </section>}
    {quiet.length > 0 && <button type="button" className="va-link va-passlink" aria-expanded={showPass} onClick={() => setShowPass(!showPass)} data-testid="v-show-pass">
      {showPass ? 'Hide them' : passed === 0 ? `Show the ${off.toLocaleString()} turned off`
        : `Show the ${passed.toLocaleString()} that pass${off ? ` and ${off.toLocaleString()} turned off` : ''}`}</button>}
    {showPass && quiet.length > 0 && <section className="sem-card va-list" data-testid="v-passing" aria-label="Checks that pass">
      {filterOn && <div className="va-filter"><input type="search" className="va-filter-input" value={passFilter} onChange={(e) => setPassFilter(e.target.value)}
        placeholder="Find a check by name" aria-label="Find a check by name" data-testid="v-pass-filter" /></div>}
      {shownQuiet.map(line)}
      {shownQuiet.length === 0 && <p className="va-line va-faint va-nomatch">No check has that name.</p>}
    </section>}
  </>;
}

function Panel({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return <section className="sem-card va-panel" data-testid={`panel-${title.toLowerCase().replace(/[^a-z]+/g, '-')}`}>
    <div className="va-panel-head"><h2 className="va-panel-title">{title}</h2>
      <button type="button" className="va-icon" aria-label={`Close ${title}`} onClick={onClose}>{Icon.close}</button></div>
    {children}
  </section>;
}

/** Run history and grade: the engine's grade and coverage with the sentences that explain them, the eight-run view, before and after, the dated runs. */
function HistoryPanel({ seen, run, partRun, history, histVisual, changes, picks, recorded, busy, onRecord, onClose }: {
  seen: { passed: number; differs: number; cannot: number; checked: number; accepted: number };
  run: TestRunW | null; partRun: boolean; history: TestHistoryW | null; histVisual: CheckVisual; changes: CheckVisual;
  picks: Record<string, VisualPick>; recorded: RecordedRunLike[]; busy: boolean; onRecord: () => void; onClose: () => void;
}) {
  const health = run?.health;
  const moved = movedCount(changes);
  return <Panel title="Run history and grade" onClose={onClose}>
    <div className="va-pair">
      <div><div className="va-mid va-num" title={health ? `Grade ${health.grade}` : undefined}>{health?.grade ?? '·'}</div><div className="va-label">Grade</div></div>
      <div><div className="va-mid va-num">{health ? `${seen.passed.toLocaleString()} of ${seen.checked.toLocaleString()}` : '·'}</div><div className="va-label">{seen.checked === 1 ? 'Check passes' : 'Checks pass'}</div></div>
    </div>
    {health
      ? <p className="va-line va-faint">{seen.passed.toLocaleString()} of {seen.checked.toLocaleString()} {seen.checked === 1 ? 'check passes' : 'checks pass'}: {seen.accepted > 0 && <>{seen.accepted.toLocaleString()} accepted, </>}{seen.differs.toLocaleString()} {seen.differs === 1 ? 'differs' : 'differ'}, {seen.cannot.toLocaleString()} could not check.</p>
      : <p className="va-line va-faint">{run ? 'No grade for this run.' : 'Run the checks to get a grade.'}</p>}
    {/* The engine's own sentences. The page used to prefix "Grade capped:" and join with commas, which
        read as a fragment beside a sentence that already says the grade cannot rise. */}
    {health && health.gatedBy.length > 0 && <p className="va-line">{health.gatedBy.join(' ')}</p>}
    {partRun && health && run?.scope?.gradeCovers && <p className="va-line">{partRunSummary(run.scope.gradeCovers, run.scope.skipped ?? [])}</p>}
    <div className="va-actions-row">
      {run?.persisted
        ? <span className="va-faint">This run is recorded.</span>
        : <button type="button" className="sem-btn sem-btn-sm" disabled={!run || busy} onClick={onRecord}>Record this run</button>}
    </div>
    <h3 className="va-sub-title">How each check moved over the last 8 runs</h3>
    {histVisual.data ? <VisualBlock cv={histVisual} pick={picks.history} /> : <p className="va-line va-faint">Record two runs to see how each check moved.</p>}
    {changes.data && <>
      <h3 className="va-sub-title">Before and after</h3>
      <p className="va-line va-faint">{moved ? `${moved} of ${changes.data.rows.length} moved between the last two recorded runs (${formatDate(recorded[0]?.when, true)} beside ${formatDate(recorded[1]?.when, true)}).`
        : `Nothing moved between the last two recorded runs (${formatDate(recorded[0]?.when, true)} beside ${formatDate(recorded[1]?.when, true)}).`}</p>
      <VisualBlock cv={changes} pick={picks.changes} />
    </>}
    <h3 className="va-sub-title">Recorded runs</h3>
    <RunsList history={history} />
  </Panel>;
}

/** The kinds are the engine's; the page says them in the words a person uses. */
function CheckDetail(props: {
  def: TestDefinitionW; outcome?: ReconcileOutcomeW; whenIso?: string; chip: ResultChip; visual: CheckVisual; pick?: VisualPick;
  liveModel: boolean; error?: string; onDismissError: () => void;
  confirming: boolean; onConfirm: () => void; onCancelConfirm: () => void; onDelete: () => void; onToggleEnabled: () => void;
  onEdit: (def: TestDefinitionW, focus?: AuthorFocus, sourceHost?: string) => void; onRunOne: () => void; onConnect: () => void;
  onOpenDaxLab?: (measureRef: string) => void;
  onWaive: (note: string) => void; onUnwaive: () => void;
}) {
  const { def, outcome, whenIso, chip, visual, liveModel } = props;
  const [allPlaces, setAllPlaces] = useState(false);
  const [waiveNote, setWaiveNote] = useState('');
  const isTrustedAnswer = def.kind === 'measureValue';
  const reconcile = isTrustedAnswer ? undefined : parseReconcileParams(def);
  const value = isTrustedAnswer ? parseValueParams(def) : undefined;
  const sourceLabel = isTrustedAnswer ? 'the answer you trust' : (reconcile?.sqlSourceName || 'the source');
  const rowsKept = outcome?.rows ?? [];
  const differing = rowsKept.filter((row) => comparisonVerdict(row.verdict) === 'Fail');
  // FOUND BY DRIVING IT: the engine keeps only the worst rows, so counting the kept ones said
  // "3 differ; 2 match" about a comparison where 45 actually matched. The counts come from the
  // outcome's own totals whenever it has them; the kept rows are only a fallback.
  const differCount = outcome?.mismatches ?? differing.length;
  const matchCount = outcome?.matches ?? (rowsKept.length - differing.length);
  const reason = chip === 'Could not check' ? unknownCellText(outcome) : null;
  // A grouped check has no single figure to show: its answer is the places it compared.
  const grouped = !isTrustedAnswer && !!outcome && !outcome.missing && !outcome.expected && !outcome.actual && outcome.difference == null && rowsKept.length > 0;
  // A check that returned no figures at all (a grouped comparison, or one that could not run) has nothing to put beside "Expected" and "Actual".
  const showFigures = !grouped && !!outcome && !!(outcome.expected ?? value?.expectedValue ?? outcome.actual);
  const noNumber = chip === 'Could not check' && !!outcome && !outcome.missing && !outcome.actual && outcome.difference == null;

  let why: ReactNode;
  let facts: ReactNode = null;
  let table: ReactNode = null;
  const blankStandoff = !!outcome && !outcome.missing && isBlankStandoff(outcome.message);
  const actions: ReactNode[] = [];
  const action = (label: string, onClick: () => void, key = label, quietButton = false) =>
    <button key={key} type="button" className={`sem-btn sem-btn-sm ${quietButton ? 'sem-btn-quiet' : ''}`} onClick={onClick}>{label}</button>;
  const runLabel = outcome && !outcome.missing ? 'Run again' : 'Run this one';
  if (!def.enabled) {
    why = <p className="va-line">This check is turned off, so it did not run.</p>;
    actions.push(action('Turn on', props.onToggleEnabled));
  } else if (!outcome) {
    why = <p className="va-line">This check has not run yet.{!liveModel && ' There is no live test model to ask.'}</p>;
    actions.push(action('Run this one', props.onRunOne));
    if (!liveModel) actions.push(action('Connect a test model', props.onConnect, 'connect', true));
  } else if (outcome.missing) {
    why = <p className="va-line"><span className="sem-chip"><StateDot color="var(--sem-nv)" />Not checked</span>{' '}
      The calculation this check was about no longer exists, so there was nothing to ask.</p>;
    actions.push(action('Edit this check', () => props.onEdit(def)), action('Delete this check', props.onConfirm, 'delete', true));
  } else if (outcome.unsavedServer) {
    // Nothing was sent: the check names a server the person has not saved. One press opens the source form with that host filled in.
    why = <p className="va-line" data-testid="check-unsaved-server">{outcome.message}</p>;
    actions.push(action('Choose a source', () => props.onEdit(def, undefined, outcome.unsavedServer), 'choose-source'), action('Edit this check', () => props.onEdit(def), 'edit', true));
  } else if (blankStandoff) {
    why = <p className="va-line">{BLANK_STANDOFF}</p>;
    actions.push(action('Choose what a blank means', () => props.onEdit(def, 'blank')), action('Run this one', props.onRunOne, 'run', true));
  } else {
    // The model gave no number to compare (an error, a refusal): one plain line, never "answered nothing" beside a raw engine error (hero 26 N2).
    why = noNumber
      ? <p className="va-line" data-testid="check-error-line">{plainCheckError(outcome.message)}</p>
      : <>
        {chip === 'Accepted' && <p className="va-line" data-testid="accepted-line"><strong>Accepted difference.</strong> <span className="va-faint">{acceptedByLine(outcome.waiver)} It stays accepted until you press Undo waive.</span></p>}
        {chip === 'Differs' && outcome.waiverLapsed && <p className="va-note" data-testid="waiver-lapsed">You accepted a different difference before. This one is new, so the check differs again.</p>}
        <p className={`va-line ${chip === 'Differs' ? 'va-bad' : ''}`}>{grouped
          ? groupedLeadSentence(differCount, matchCount, sourceLabel)
          : differenceSentence(outcome.expected ?? value?.expectedValue, outcome.actual, outcome.difference ?? null, sourceLabel)}</p>
        {outcome.difference == null && outcome.message && !(grouped && chip === 'Pass') && <p className="va-note">{outcome.message}</p>}
      </>;
    facts = <>
      <div className="va-facts">
        {showFigures && <span>Expected <strong>{plainFigure(outcome.expected ?? value?.expectedValue) || '·'}</strong></span>}
        {showFigures && <span>Actual <strong>{shownFigure(outcome.actual, outcome.expected ?? value?.expectedValue) || '·'}</strong></span>}
        {filterSummary(def) !== 'all rows' && <span>Filter <strong>{filterSummary(def)}</strong></span>}
      </div>
      {!isTrustedAnswer && rowsKept.length > 0 && <p className="va-note">{comparisonSummary(differCount, matchCount, rowsKept.length, outcome.rowsTotal ?? rowsKept.length)}</p>}
      <WhereItRan outcome={outcome} />
      {!liveModel && <p className="va-note">This ran without a live test model. {action('Connect a test model', props.onConnect, 'connect', true)}</p>}
    </>;
    // The differences: the comparisons that differ (every kept comparison on request). One value against its answer needs no table.
    if (visual.data && visual.data.rows.length > 1) {
      const d = visual.data;
      const shownRows = !allPlaces && differing.length > 0
        ? { ...d, rows: d.rows.filter((r) => differing.some((x) => x.context === r.label)) }
        : d;
      table = <DataTable d={shownRows} />;
    }
    if (chip === 'Differs') {
      // One press accepts it. The note is there if the person wants one, and never required.
      actions.push(<input key="waive-note" type="text" className="va-waive-note" value={waiveNote} onChange={(e) => setWaiveNote(e.target.value)}
        placeholder="Note (optional)" aria-label="Note for the accepted difference (optional)" data-testid="waive-note" />);
      actions.push(action('Waive', () => { props.onWaive(waiveNote); setWaiveNote(''); }, 'waive'));
    }
    if (chip === 'Accepted') actions.push(action('Undo waive', props.onUnwaive, 'unwaive'));
    actions.push(action(runLabel, props.onRunOne, 'run', chip === 'Differs'));
    if (def.targetRef && props.onOpenDaxLab) actions.push(action('Open in Query', () => props.onOpenDaxLab?.(def.targetRef as string), 'query', true));
    if (!isTrustedAnswer && differing.length > 0 && visual.data && visual.data.rows.length > 1) actions.push(action(allPlaces ? 'Only the differing' : 'All places', () => setAllPlaces((v) => !v), 'places', true));
  }
  if (reason && chip === 'Could not check' && outcome && !outcome.missing && !blankStandoff && !noNumber) {
    why = <>{why}<p className="va-note" title={outcome.message}>{reason}</p></>;
  }
  const runsAgainst = isTrustedAnswer ? null : runsAgainstLine(outcome?.server ?? reconcile?.server, outcome?.database ?? reconcile?.database);
  const meta = chip === 'Could not check' && !liveModel ? 'No live connection' : whenIso ? formatDate(whenIso) : '';
  return <>
    {why}
    {facts}
    {visual.data && <VisualBlock cv={visual} pick={props.pick} />}
    {table}
    {props.error && <p className="va-note va-bad">{props.error} <button type="button" className="va-link" onClick={props.onDismissError}>Hide this</button></p>}
    {props.confirming && (
      <div className="va-note">
        Delete the check &apos;{readableTitle(def.title)}&apos;? Recorded runs keep their record. This cannot be undone.
        <span className="va-actions-row">
          <button type="button" className="sem-btn sem-btn-sm" onClick={props.onDelete}>Delete</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={props.onCancelConfirm}>Keep it</button>
        </span>
      </div>
    )}
    <div className="va-actions-row">
      {actions}
      <span className="va-push" />
      <Menu label={`More for ${def.title}`} trigger={Icon.more} className="va-icon-btn sem-btn-quiet">{(close) => <>
        <Item onClick={() => { close(); props.onEdit(def); }}>Edit</Item>
        {!blankStandoff && outcome && !outcome.missing && differing.some((x) => /^\(?blank\)?$/i.test(x.context)) && <Item onClick={() => { close(); props.onEdit(def, 'blank'); }}>Choose what a blank means</Item>}
        {blankStandoff && <Item onClick={() => { close(); props.onEdit(def, 'blank'); }}>Choose what a blank means</Item>}
        <Item onClick={() => { close(); props.onToggleEnabled(); }}>{def.enabled ? 'Turn off' : 'Turn on'}</Item>
        <Item danger onClick={() => { close(); props.onConfirm(); }}>Delete</Item>
      </>}</Menu>
    </div>
    {runsAgainst && <p className="va-meta" data-testid="check-runs-against">{runsAgainst}</p>}
    <p className="va-meta">{kindLabel(def.kind)}{asksSummary(def)}{meta ? ` · ${meta}` : ''}{shortRef(def.targetRef) !== '·' ? ` · ${shortRef(def.targetRef)}` : ''}</p>
    {/* The allowed difference and where the check was written are detail on demand (round 2 N30): the first view is the two numbers and the next step. */}
    {outcome && !outcome.missing && <details className="va-details" data-testid="check-details">
      <summary>Details</summary>
      <p className="va-note">{isTrustedAnswer
        ? toleranceSentence(value?.toleranceAbsolute, value?.toleranceRelative, value?.expectedValue)
        : toleranceSentence(reconcile?.toleranceAbsolute, reconcile?.toleranceRelative)}</p>
      {def.authoredAgainstLabel && <p className="va-note">Written against {def.authoredAgainstLabel}.</p>}
      {outcome.detail && <p className="va-note" data-testid="check-raw-detail">The server said: {outcome.detail}</p>}
    </details>}
  </>;
}

function RowCountsBody({ list, skipped, onMapSource, cv, pick, busy, onRun }: {
  list: TableMappingListW | null; skipped: boolean; onMapSource: (table: string) => void; cv: CheckVisual; pick?: VisualPick; busy: boolean; onRun: () => void;
}) {
  // A calculated table has no source table to count against, so it is never listed: the line above and the list below say the same thing.
  const rows = (list?.rows ?? []).filter((row) => row.canCount || row.editable);
  // The engine keeps the last counts for this model, so a run that skipped them leaves the numbers below
  // standing. The block says they are older rather than letting them read as this run's.
  const lastCounted = rows.map((row) => row.lastRunUtc).filter(Boolean).sort().pop();
  const summary = useMemo(() => {
    const counts: Record<CountChip, number> = { Match: 0, 'Counts differ': 0, 'Not checked': 0 };
    rows.forEach((row) => { counts[countChip(row)] += 1; });
    return counts;
  }, [rows]);
  const columns = 'minmax(0, 1.1fr) minmax(0, 1fr) minmax(0, 1.2fr) 116px 106px';
  return <>
    {rows.length > 0 && <p className="va-line">{countsSummaryLine(rows.map((row) => ({ ...row, ...effectiveLastCount(row) })))}</p>}
    {skipped && <div className="va-notice"><Banner color="var(--sem-warn)">{skippedFamilyNote('table counts', lastCounted)}</Banner></div>}
    {list == null && <p className="va-note">Loading the table sources…</p>}
    {list != null && rows.length === 0 && <p className="va-note">{list.note ?? 'This model has no tables to count.'}</p>}
    {summary['Counts differ'] > 0 && <p className="va-note">{TABLE_COUNT_FOOTER}</p>}
    {cv.data && <VisualBlock cv={cv} pick={pick} />}
    {rows.length > 0 && <div className="vc-rows">
      <div className="vc-rowhead sem-label" style={{ gridTemplateColumns: columns }}>
        <span>Table</span><span>Model</span><span>Source</span><span>Result</span><span />
      </div>
      {rows.map((row) => (
        <div key={row.modelTable} className="vc-row grid items-center border-b px-2 py-2 text-(length:--sem-text-meta)"
          style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-rule)' }}>
          <span className="truncate">{row.modelTable}</span>
          <span className="tnum">{countText(effectiveLastCount(row).lastModelCount ?? undefined)}</span>
          <span className="tnum truncate" title={effectiveLastCount(row).lastMessage ?? row.reason ?? undefined}>
            {effectiveLastCount(row).lastSourceCount != null
              ? `${effectiveLastCount(row).lastSourceCount!.toLocaleString()}${row.sqlSourceName ? ` · ${row.sqlSourceName}` : ''}`
              : row.canCount ? (row.sqlSourceName ?? 'Source chosen, not counted yet')
                : row.editable ? 'No source chosen' : 'No source table to count against'}
          </span>
          <span><CountPill chip={countChip(row)} title={effectiveLastCount(row).lastMessage ?? row.reason ?? undefined} /></span>
          <span>{row.editable
            ? <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet"
              onClick={() => onMapSource(row.modelTable)}>{row.sqlSourceId ? 'Change source' : 'Choose source'}</button>
            : null}</span>
        </div>
      ))}
    </div>}
    <div className="va-actions-row"><button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={onRun}>Run again</button></div>
  </>;
}

function RelationshipsBody({ retained, skipped, cv, pick, busy, onRun }: {
  retained: RetainedFamily<RelationshipReportW> | null; skipped: boolean; cv: CheckVisual; pick?: VisualPick; busy: boolean; onRun: () => void;
}) {
  const rels = retained?.value.relationships ?? [];
  const [open, setOpen] = useState<string | null>(null);
  // An unknown relationship is NOT fine. "3 fine" used to include one whose checks were all unknown, and a
  // card where every check was unknown showed a Pass header (Astra, finding 5; seen live on Kane's Fabric
  // model, where every relationship read Could not check under a PASS header).
  const tally: RelationshipTally = relationshipTally(rels.map(relationshipChecks));
  const columns = 'minmax(0, 1.2fr) minmax(0, 1.1fr) minmax(0, 0.9fr) 108px 96px';
  return <>
    {rels.length > 0 && <p className="va-line">{relationshipSummary(tally)}</p>}
    {/* A run that SKIPPED this family says so, and the rows below it keep their own earlier date. The old
        page rendered the skipped run's empty report and concluded the model has no relationships. */}
    {skipped && <div className="va-notice"><Banner color="var(--sem-warn)">{skippedFamilyNote('relationships', retained?.whenIso)}</Banner></div>}
    {cv.data && <VisualBlock cv={cv} pick={pick} />}
    {rels.length > 0 && <div className="vc-rows">
      <div className="vc-rowhead sem-label" style={{ gridTemplateColumns: columns }}>
        <span>Relationship</span><span>Rows that match</span><span title="Lookup values unique">Lookup values unique</span><span>Result</span><span />
      </div>
      {rels.map((rel) => <RelationshipRow key={rel.name} rel={rel} open={open === rel.name} onToggle={() => setOpen(open === rel.name ? null : rel.name)} />)}
    </div>}
    <div className="va-actions-row"><button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={onRun}>Run again</button></div>
  </>;
}

function WhereItRan({ outcome }: { outcome: ReconcileOutcomeW }) {
  const authored = outcome.authoredAgainstLabel;
  const ran = outcome.ranAgainstLabel;
  if (!authored && !ran) return null;
  // The meta line under the title already says what the check was authored against; this adds a line only when the run was somewhere else (hero 26 N8: the endpoint was shown twice).
  if (authored && (!ran || authored === ran)) return null;
  const text = authored && ran && authored !== ran
    ? `Authored against ${authored}, ran against ${ran}. Those are different models, so read this result as a statement about ${ran}.`
    : authored
      ? `Authored against ${authored}${ran ? ', and ran against it' : ''}.`
      : `Ran against ${ran}. No authoring connection was recorded for this check.`;
  return <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{text}</div>;
}

// ---------------------------------------------------------------------------------------------------
// 4. Relationships, in plain words. Every specialist term the UAT flagged is gone from what a person
//    reads: no referential integrity, no orphans, no cardinality, no hidden blank row.
// ---------------------------------------------------------------------------------------------------
const BLANK_STANDOFF = 'Every cell is blank in the model and NULL in the source. This check does not treat those as equal.';
function isBlankStandoff(message?: string): boolean {
  return !!message && /^Nothing verifiable:/i.test(message) && /BLANK vs NULL/i.test(message);
}

// The three checks of one relationship, as the shared tally reads them. An unknown is its own state here
// exactly as it is everywhere else on this page.
const relationshipChecks = (rel: RelationshipResultW): string[] =>
  [rel.dataTypeMatch, rel.keyUniqueness, rel.referentialIntegrity].map((c) => verdictKey(c.verdict));
const relationshipNeedsAttention = (rel: RelationshipResultW) =>
  relationshipChecks(rel).some((v) => v === 'Fail' || v === 'Suspect');

function matchText(rel: RelationshipResultW): string {
  const key = verdictKey(rel.referentialIntegrity.verdict);
  if (key === 'Pass') return 'All match';
  if (key === 'NotVerifiable') return 'Could not check';
  const count = rel.referentialIntegrity.count;
  if (count == null) return 'Some do not match';
  return `All but ${count.toLocaleString()} match`;
}

function RelationshipRow({ rel, open, onToggle }: { rel: RelationshipResultW; open: boolean; onToggle: () => void }) {
  const [unmatched, setUnmatched] = useState<UnmatchedRowsW | null>(null);
  const [loadingRows, setLoadingRows] = useState(false);
  const showRows = () => {
    setLoadingRows(true);
    rpc<UnmatchedRowsW>('getUnmatchedRows', rel.name, 200)
      .then(setUnmatched)
      .catch((e: unknown) => setUnmatched({ error: e instanceof Error ? e.message : String(e) }))
      .finally(() => setLoadingRows(false));
  };
  const attention = relationshipNeedsAttention(rel);
  const unique = verdictKey(rel.keyUniqueness.verdict);
  const types = verdictKey(rel.dataTypeMatch.verdict);
  // One rule for the row and the header: Pass means every check was decided and passed.
  const chip: ResultChip = relationshipChip(relationshipTally([relationshipChecks(rel)]));
  return <>
    <div className="vc-row grid items-center border-b px-2 py-2 text-(length:--sem-text-meta)" style={{ gridTemplateColumns: 'minmax(0, 1.2fr) minmax(0, 1.1fr) minmax(0, 0.9fr) 108px 96px', borderColor: 'var(--sem-rule)' }}>
      <span className="truncate" title={`${rel.manyTable} → ${rel.oneTable}`}>{rel.manyTable} → {rel.oneTable}</span>
      <span className="tnum truncate" title={matchText(rel)}>{matchText(rel)}</span>
      <span className="truncate">{unique === 'Pass' ? 'Yes' : unique === 'NotVerifiable' ? 'Could not check' : 'No'}</span>
      <span><Chip chip={chip} /></span>
      <span><button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onToggle}>{open ? 'Hide details' : 'Show details'}</button></span>
    </div>
    {open && (
      <div className="border-b px-2 py-3 text-(length:--sem-text-meta)" style={{ background: 'var(--sem-ground)', borderColor: 'var(--sem-rule)' }}>
        {/* There is no engine operation that returns the unmatched rows themselves, so this shows what the
            check actually COUNTED rather than promising records it cannot fetch (Astra point 7). */}
        <div style={{ color: 'var(--sem-muted)' }}>This is what the check counted.</div>
        <ul className="mt-2 list-none p-0">
          <li className="mb-1"><strong>Rows that match:</strong> {rel.referentialIntegrity.message ?? matchText(rel)}</li>
          <li className="mb-1"><strong>Lookup values unique:</strong> {rel.keyUniqueness.message ?? (unique === 'Pass' ? 'Every lookup value occurs once.' : 'This was not checked.')}</li>
          <li><strong>Column types compatible:</strong> {rel.dataTypeMatch.message ?? (types === 'Pass' ? 'The joined columns use types that work together.' : 'This was not checked.')}</li>
        </ul>
        {rel.referentialIntegrity.rootCause && <div className="mt-2 sem-chip sem-chip-warn" style={{ color: 'var(--sem-fg)', whiteSpace: 'normal', height: 'auto' }}>This result depends on another problem first: {rel.referentialIntegrity.rootCause}</div>}
        {attention && !unmatched && <button type="button" className="sem-btn sem-btn-sm mt-2" disabled={loadingRows}
          onClick={showRows}>{loadingRows ? 'Reading…' : 'Show the values that do not match'}</button>}
        {unmatched && <UnmatchedRows result={unmatched} oneTable={rel.oneTable} />}
      </div>
    )}
  </>;
}

// Each row is one lookup value the other table does not have, with how much data hangs off it, biggest
// first. Two hundred rows of the same value tell a person nothing; WHICH values are missing is the answer.
function UnmatchedRows({ result, oneTable }: { result: UnmatchedRowsW; oneTable: string }) {
  if (result.error) return <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-bad)' }}>{result.error}</div>;
  const rows = result.rows ?? [];
  if (rows.length === 0) return <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{result.note ?? 'No values came back.'}</div>;
  return <div className="mt-2">
    <div className="overflow-hidden rounded border" style={{ borderColor: 'var(--sem-rule)' }}>
      <div className="grid border-b px-3 py-1 sem-label"
        style={{ gridTemplateColumns: 'minmax(0,1fr) 140px', background: 'var(--sem-surface-2)', borderColor: 'var(--sem-rule)', color: 'var(--sem-muted)' }}>
        <span>Value with no {oneTable}</span><span>Rows carrying it</span>
      </div>
      {rows.slice(0, 20).map((row, index) => (
        <div key={index} className="grid border-b px-3 py-1 text-(length:--sem-text-label) last:border-0"
          style={{ gridTemplateColumns: 'minmax(0,1fr) 140px', borderColor: 'var(--sem-rule)' }}>
          <span className="truncate">{row[0] == null ? 'blank' : String(row[0])}</span>
          <span className="tnum">{row[1] == null ? '·' : Number(row[1]).toLocaleString()}</span>
        </div>
      ))}
    </div>
    <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
      {rows.length > 20 && `Showing 20 of ${rows.length.toLocaleString()}${result.truncated ? ' read' : ''}. `}
      {result.note ?? 'This reads the data as it is now, so it can differ from what the check counted when it ran.'}
    </div>
  </div>;
}

// ---------------------------------------------------------------------------------------------------
// 5. Table row counts. One home, one identity: Choose source here and New check > Table row count open the
//    SAME setup, because for a table count the mapping IS the check.
// ---------------------------------------------------------------------------------------------------
function countChip(row: TableMappingRowW): CountChip {
  if (!row.canCount) return 'Not checked';
  const last = effectiveLastCount(row);
  if (last.lastModelCount == null || last.lastSourceCount == null) return 'Not checked';
  if (last.lastVerdict === 'Pass' || last.lastModelCount === last.lastSourceCount) return 'Match';
  return 'Counts differ';
}

// ---------------------------------------------------------------------------------------------------
// 6. History: a dated list whose runs open to their own evidence.
// ---------------------------------------------------------------------------------------------------
function RunsList({ history }: { history: TestHistoryW | null }) {
  const [openRun, setOpenRun] = useState<TestRunDetailW | null>(null);
  const [loadingId, setLoadingId] = useState<string | null>(null);
  const runs = useMemo(() => [...(history?.runs ?? [])].sort((a, b) => new Date(b.when).valueOf() - new Date(a.when).valueOf()), [history]);
  const columns = '180px 40px minmax(0,1fr) 106px';
  const openOne = (runId: string) => {
    setLoadingId(runId);
    rpc<TestRunDetailW>('getTestRun', runId).then(setOpenRun).catch(() => undefined).finally(() => setLoadingId(null));
  };
  // A recorded run keeps the grade and its coverage, not the checks the page counts, so a past run says those and no count that disagrees with the page.
  const counts = (health?: TestHealthW) => health
    ? `${Math.round(health.coveragePct)}% of the checks gave an answer`
    : 'No grade was recorded.';
  return (
    <div>
      {history == null ? <div className="vc-note">Loading the recorded runs…</div>
        : runs.length === 0 ? <div className="vc-note">{history.note ?? 'No recorded runs yet. Record a run to keep it here.'}</div>
          : runs.map((record) => (
            <div key={record.runId} className="grid items-center border-b px-2 py-2 text-(length:--sem-text-meta)"
              style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-rule)' }}>
              <span>{formatDate(record.when)}</span>
              <span className="font-semibold sem-num">{record.health?.grade ?? '·'}</span>
              {/* No old-grade label here on purpose: listTestRuns strips outcomes from EVERY row, so a
                  missing outcomes field proves nothing about how that row was scored. The label lives in
                  the opened run, where the engine actually sends gradedBeforeSecurityLeftTests. */}
              <span className="truncate" style={{ color: 'var(--sem-muted)' }}>{counts(record.health)}</span>
              <span><button type="button" className="sem-btn sem-btn-sm"
                aria-label={`Open the run recorded on ${formatDate(record.when, true)}`}
                onClick={() => openOne(record.runId)}>{loadingId === record.runId ? 'Opening…' : 'Open'}</button></span>
            </div>
          ))}
      {openRun && <RecordedRun detail={openRun} onClose={() => setOpenRun(null)} />}
    </div>
  );
}

function RecordedRun({ detail, onClose }: { detail: TestRunDetailW; onClose: () => void }) {
  const record = detail.run;
  const checks = record?.outcomes?.checks ?? [];
  const columns = 'minmax(0,1.4fr) 128px minmax(0,1fr) 168px';
  const kindWord = (kind: string) => kind === 'savedCheck' ? 'Saved check' : kind === 'relationship' ? 'Relationship' : 'Table row count';
  return (
    <div className="border-t px-4 py-3" style={{ background: 'var(--sem-ground)', borderColor: 'var(--sem-rule)' }}>
      <div className="flex items-center gap-3">
        <strong className="text-(length:--sem-text-meta)">{record ? formatDate(record.when) : 'That run'}</strong>
        <button type="button" className="ml-auto text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }} onClick={onClose}>Close</button>
      </div>
      {detail.gradedBeforeSecurityLeftTests && <div className="mt-2 sem-chip sem-chip-warn" style={{ color: 'var(--sem-fg)', whiteSpace: 'normal', height: 'auto' }}>
        This run was scored the old way, before access rules left this page. Read its letter on its own, not against a newer one.
      </div>}
      {detail.note && <div className="mt-2 sem-chip sem-chip-warn" style={{ color: 'var(--sem-fg)', whiteSpace: 'normal', height: 'auto' }}>{detail.note}</div>}
      {record?.scope?.gradeCovers && <div className="mt-2 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>
        This grade is for {record.scope.gradeCovers}.
      </div>}
      {checks.length === 0
        ? <div className="mt-2 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>{record?.outcomes?.note ?? 'This run kept its grade and totals but not what each check found.'}</div>
        : <>
          <div className="mt-2 grid items-center border-b py-1 sem-label"
            style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-rule)', color: 'var(--sem-muted)' }}>
            <span>Check</span><span>Kind</span><span>Expected · actual</span><span>Result</span>
          </div>
          {/* Every row opens to what the run actually kept: the comparisons it made, and the settings it was
              run with. Before this, an opened run showed four numbers and nothing else, and the engine's own
              snapshot did not even hold the query (Astra, finding 6). */}
          {checks.map((check, index) => (
            <RecordedCheckRow key={`${check.name}-${check.check ?? ''}-${index}`} check={check} columns={columns} kindWord={kindWord} />
          ))}
          {record?.outcomes?.note && <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{record.outcomes.note}</div>}
        </>}
    </div>
  );
}

// One recorded check, opening to the evidence the run KEPT: the comparisons it made, with their own
// numbers, and the settings it was executed with. Nothing here is read from today's definition, so a check
// edited since the run cannot change what the run is shown to have asked.
const RELATIONSHIP_CHECK_WORD: Record<string, string> = {
  ReferentialIntegrity: 'rows that match',
  KeyUniqueness: 'lookup values unique',
  DataTypeMatch: 'column types compatible',
};
function RecordedCheckRow({ check, columns, kindWord }: {
  check: RecordedCheckW; columns: string; kindWord: (kind: string) => string;
}) {
  const [open, setOpen] = useState(false);
  const contexts = check.contexts ?? [];
  const settings: [string, string][] = [];
  if (check.query) settings.push([check.kind === 'tableRowCount' ? 'Table in the source' : 'The query it asked', check.query]);
  if (check.filter) settings.push(['Only where', check.filter]);
  if (check.tolerance) settings.push(['Tolerance', check.tolerance]);
  if (check.source) settings.push(['SQL source', check.source]);
  const hasDetail = contexts.length > 0 || settings.length > 0 || !!check.note;
  // A relationship row names WHICH of its checks this is. The card used to print the relationship name
  // three times over with no way to tell the rows apart.
  const name = check.check ? `${check.name} · ${RELATIONSHIP_CHECK_WORD[check.check] ?? check.check}` : check.name;
  return <>
    <div className="grid items-center border-b py-2 text-(length:--sem-text-meta)"
      style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-rule)' }} title={check.note}>
      <span className="truncate">{name}</span>
      <span style={{ color: 'var(--sem-muted)' }}>{kindWord(check.kind)}</span>
      <span className="tnum truncate">{check.expected ?? '\u00b7'} · <strong>{check.actual ?? '\u00b7'}</strong></span>
      <span className="flex items-center gap-2">
        <Chip chip={resultChip(true, check)} />
        {hasDetail && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet"
          aria-label={`${open ? 'Hide' : 'Open'} what ${name} found`}
          onClick={() => setOpen((v) => !v)}>{open ? 'Hide' : 'Open'}</button>}
      </span>
    </div>
    {open && (
      <div className="border-b px-3 py-2 text-(length:--sem-text-label)" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-rule)' }}>
        {check.note && <div className="mb-2" style={{ color: 'var(--sem-muted)' }}>{check.note}</div>}
        {settings.length > 0 && (
          <dl className="m-0 mb-2 grid gap-x-4 gap-y-1" style={{ gridTemplateColumns: 'max-content minmax(0,1fr)' }}>
            {settings.map(([label, value]) => <Fragment key={label}>
              <dt style={{ color: 'var(--sem-muted)' }}>{label}</dt>
              <dd className="m-0 break-words font-mono text-(length:--sem-text-label)">{value}</dd>
            </Fragment>)}
          </dl>
        )}
        {contexts.length > 0 && <>
          <div className="mb-1" style={{ color: 'var(--sem-muted)' }}>What it compared, as it was saved with this run.</div>
          <div className="overflow-hidden rounded border" role="table" aria-label={`Comparisons kept with ${name}`}
            style={{ borderColor: 'var(--sem-rule)' }}>
            <div role="row" className="grid border-b px-2 py-1 sem-label"
              style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', borderColor: 'var(--sem-rule)', color: 'var(--sem-muted)' }}>
              <span role="columnheader">Where</span><span role="columnheader">Expected</span>
              <span role="columnheader">The model</span><span role="columnheader">Difference</span>
            </div>
            {contexts.map((context, index) => (
              <div role="row" key={`${context.context}-${index}`} className="grid border-b px-2 py-1 last:border-0"
                title={context.note} style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', borderColor: 'var(--sem-rule)' }}>
                <span role="cell" className={context.grandTotal ? 'truncate font-semibold' : 'truncate'}>{context.context}</span>
                <span role="cell" className="tnum">{resultText(context.expected) ?? '\u00b7'}</span>
                <span role="cell" className="tnum">{resultText(context.actual) ?? '\u00b7'}</span>
                <span role="cell" className="tnum">{resultText(context.difference) ?? '\u00b7'}</span>
              </div>
            ))}
          </div>
          {(check.contextsTotal ?? 0) > contexts.length && <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>
            {contexts.length.toLocaleString()} of {(check.contextsTotal ?? 0).toLocaleString()} comparisons were kept with this run.
          </div>}
        </>}
      </div>
    )}
  </>;
}

// ---------------------------------------------------------------------------------------------------
// The Saved reports library. A peer view of Tests, not a sub-tab of it.
// ---------------------------------------------------------------------------------------------------
/** The Reports tab of Validation: every report kept with this model. It was a page of its own in the Checks
 *  area until 22 September 2026; it is a tab now, so it renders INSIDE Validation's page shell and brings no
 *  page padding of its own. `evidence` is still its route id, which is how an old link still reaches it. */
export function EvidenceBody() {
  const [library, setLibrary] = useState<EvidenceLibraryW | null>(null);
  const [openEvidence, setOpenEvidence] = useState<EvidenceItemW | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const loadEvidence = useCallback(() => {
    rpc<EvidenceLibraryW>('listEvidence').then((next) => { setLibrary(next); setErr(null); })
      .catch((error: unknown) => setErr(error instanceof Error ? error.message : String(error)));
  }, []);
  useEffect(() => {
    loadEvidence();
    return onActivity((event) => { if (event.kind === 'save_evidence') loadEvidence(); });
  }, [loadEvidence]);

  return <>
    {openEvidence && <EvidenceArtifactDialog
      title={openEvidence.title || 'Report'}
      subtitle={`${openEvidence.kind === 'workflow-run' ? 'Workflow' : 'Validation'} · ${formatDate(openEvidence.createdUtc)} · saved with ${openEvidence.modelName || 'the current model'}`}
      baseName={`${(openEvidence.modelName || 'semanticus').replace(/[^\w.-]+/g, '_')}-${openEvidence.id}`}
      stateKey="tests.evidence.saved.format"
      load={() => rpc<EvidenceArtifactW>('getEvidence', openEvidence.id)}
      onClose={() => setOpenEvidence(null)} />}
    {err && <div><Banner color="var(--sem-bad)">{err}</Banner></div>}
    <EvidenceLibraryView library={library} onOpen={setOpenEvidence} />
  </>;
}

function ReportExportDialog({ modelName, onClose }: { modelName?: string; onClose: () => void }) {
  const baseName = `${(modelName ?? 'semanticus').replace(/[^\w.-]+/g, '_')}-test-report`;
  return <EvidenceArtifactDialog
    title="Check report"
    subtitle={`Latest run for ${modelName ?? 'the current model'} · HTML and JSON are one sealed report; Markdown is the portable reading copy.`}
    baseName={baseName}
    stateKey="tests.report.format"
    load={() => rpc<TestReportResultW>('exportTestReport')}
    save={() => rpc<EvidenceSaveResultW>('saveEvidence', 'tests', null, 'human')}
    includeMarkdown
    onClose={onClose}
  />;
}

/** Why a report cannot be opened, in plain words. The engine's note is a verification detail (a signature check), so a
 *  mismatch reads as a change after saving and every other cause reads as unreadable. */
export function unreadableNote(note?: string | null): string {
  return note && /signature|hash|does not match|changed/i.test(note) ? 'Changed after it was saved, so it cannot be opened.' : 'This report could not be read.';
}

export function EvidenceLibraryView({ library, onOpen }: { library: EvidenceLibraryW | null; onOpen: (item: EvidenceItemW) => void }) {
  const items = library?.items ?? [];
  if (!library) return <Card className="text-(length:--sem-text-meta)"><span style={{ color: 'var(--sem-muted)' }}>Loading the reports…</span></Card>;
  if (items.length === 0) return <Card className="text-(length:--sem-text-meta)">
    <strong>No reports yet.</strong>
    <p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>{library.note ?? 'Run the checks in Validation, then use Report to keep one with this model.'}</p>
    {library.directoryPath && <div className="mt-2"><PathDisclosure label="Where these are kept" path={library.directoryPath} /></div>}
  </Card>;
  return <>
    {/* Every report is one card on the page's own columns: four across (span 3). */}
    {items.map((item) => { const countsLine = evidenceCountsLine(item); return (
        <Card key={item.id} className="sem-span-3 flex flex-col gap-2 text-(length:--sem-text-meta)">
          <strong className="text-(length:--sem-text-meta)">{item.title || 'Report'}</strong>
          <span style={{ color: 'var(--sem-muted)' }}>{item.kind === 'workflow-run' ? 'Workflow run' : 'Validation run'} · {formatDate(item.createdUtc)}</span>
          {/* No "by" here: the record's producer is the op or workflow that made it, never who saved it. */}
          {/* Said only when the report describes another model than the one that is open. */}
          {item.modelName && library.modelName && item.modelName.toLocaleLowerCase() !== library.modelName.toLocaleLowerCase()
            && <span style={{ color: 'var(--sem-muted)' }}>Saved with {item.modelName}</span>}
          {countsLine && <span style={{ color: 'var(--sem-muted)' }} data-testid="evidence-counts">{countsLine}</span>}
          <span className="mt-1 flex min-w-0 flex-wrap items-center gap-2">
            <button type="button" disabled={!item.valid} className="sem-btn sem-btn-sm" onClick={() => onOpen(item)}>Open</button>
            {!item.valid && <span className="sem-chip sem-chip-warn" style={{ color: 'var(--sem-fg)', whiteSpace: 'normal', height: 'auto' }}>{unreadableNote(item.note)}</span>}
          </span>
        </Card>
    ); })}
    {library.directoryPath && <div><PathDisclosure label="Where these are kept" path={library.directoryPath} /></div>}
  </>;
}
