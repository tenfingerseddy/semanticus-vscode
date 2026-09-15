import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react';
import { rpc, onActivity, onDidChange } from './bridge';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import { useConnection } from './connection';
import { openSqlSources, listSqlSources, type SqlSourceRecord } from './connectionshub';
import { PathDisclosure } from './ui';
import {
  AuthorDrawer, NewCheckMenu, announceTestSuiteChanged, onTestSuiteChanged,
  type AuthorFocus, type AuthorShape, type MeasureValueParamsW, type ReconcileParamsW,
} from './tests-authoring';
import {
  TABLE_COUNT_FOOTER, comparisonSummary, differenceSentence, familyRan, foldFamilyResult, foldRunResults,
  partRunSummary, relationshipChip, relationshipSummary, relationshipTally, resultChip, runOnlyLabel,
  relationshipMenuCount, runScopeGroups, savedChecksHint, sectionsForGroup, skippedFamilyNote,
  tickedMenuNote, tickedRunRefusal,
  tickingIsPossible, toleranceSentence, unknownCellText,
  type RelationshipTally, type ResultChip, type RetainedFamily, type RunGroup, type StoredResult,
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
  defId: string; title: string; targetRef?: string; verdict: string; message?: string; missing?: boolean;
  // cp/tests-engine-a: EVERY outcome, of either kind, now says what it was looking for, what it got, and
  // by how much. `difference` is ABSENT (never 0) when either side is not a number.
  expected?: string; actual?: string; difference?: number;
  sql?: string; dax?: string; rows?: CompareRowW[]; rowsTotal?: number;
  matches?: number; mismatches?: number; unverifiable?: number;
  durationMs?: number; sqlDurationMs?: number;
  createdBy?: string; createdWhen?: string; toleranceNote?: string;
  authoredAgainst?: string; authoredAgainstLabel?: string; ranAgainstLabel?: string;
}
interface CategoryHealthW { category: string; weight: number; hasChecks: boolean; checked: number; passed: number; failed: number; suspect: number; notVerifiable: number; score: number }
interface TestHealthW {
  overall: number; grade: string; gatedBy: string[]; coveragePct: number; checked: number;
  passed: number; failed: number; suspect: number; notVerifiable: number; missing: number; rootFailures: number;
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
  expected?: string; actual?: string; difference?: number; note?: string;
  contexts?: RecordedContextW[]; contextsTotal?: number;
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
  verdict?: string; verified?: number; total?: number; unknowns?: number; contentHash?: string;
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
const plural = (n: number, singular: string, pluralForm = `${singular}s`) => `${n.toLocaleString()} ${n === 1 ? singular : pluralForm}`;
const KIND_LABEL: Record<string, string> = { measureValue: 'Trusted answer', measureReconcile: 'Compare with source' };
const kindLabel = (kind?: string) => (kind && KIND_LABEL[kind]) || 'Check';
const formatDate = (value?: string, dateOnly = false) => {
  if (!value) return '·';
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return '·';
  return dateOnly ? date.toLocaleDateString() : date.toLocaleString();
};
// Integers render plain; anything fractional keeps exactly two decimals so a column of money never drops
// its trailing cents. A tiny non-zero goes scientific rather than rounding to a lying 0.00 beside Differs.
const resultText = (value: number | null | undefined, delta = false) => {
  if (value == null) return null;
  if (value !== 0 && Math.abs(value) < 0.005) return value.toExponential(2);
  return value.toLocaleString(undefined, delta || !Number.isInteger(value) ? { minimumFractionDigits: 2, maximumFractionDigits: 2 } : undefined);
};
const countText = (value?: number) => value == null ? '·' : value.toLocaleString();

function Card({ children, className = '', style }: { children: ReactNode; className?: string; style?: CSSProperties }) {
  return <div className={`rounded-[9px] border ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)', ...style }}>{children}</div>;
}

function Eyebrow({ children }: { children: ReactNode }) {
  return <div className="text-[10px] font-semibold uppercase tracking-[0.07em]" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}

function Banner({ children, color }: { children: ReactNode; color: string }) {
  return <div className="rounded-md border px-3 py-2 text-[12px]" style={{ color, borderColor: color, background: 'var(--sem-surface)' }}>{children}</div>;
}

function Chip({ chip, title }: { chip: ResultChip; title?: string }) {
  const color = CHIP_COLOR[chip];
  const quiet = chip === 'Off' || chip === 'Not run';
  return <span className="inline-flex w-max items-center rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase leading-[18px] whitespace-nowrap"
    title={title} style={{ color, borderColor: quiet ? 'var(--sem-border)' : color }}>{chip}</span>;
}

function CountPill({ chip, title }: { chip: CountChip; title?: string }) {
  return <span className="inline-flex w-max items-center rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase leading-[18px] whitespace-nowrap"
    title={title} style={{ color: COUNT_CHIP_COLOR[chip], borderColor: chip === 'Not checked' ? 'var(--sem-border)' : COUNT_CHIP_COLOR[chip] }}>{chip}</span>;
}

// A control inside a clickable row stops the ROW toggling, and nothing else. The WINDOW keydown the drawer
// listens on must still hear Escape: swallowing it is exactly why Astra could not close the drawer from a
// row action (journey D). This helper takes only the click, never the keydown.
const stopRowToggle = (event: { stopPropagation: () => void }) => event.stopPropagation();

function RowAction({ label, onClick, tone }: { label: string; onClick: () => void; tone?: string }) {
  return <button type="button" className="rounded-md border px-2 py-1 text-[11px] font-semibold"
    style={{ background: 'var(--sem-surface-2)', borderColor: tone ?? 'var(--sem-border)', color: tone ?? 'var(--sem-fg)' }}
    onClick={(event) => { stopRowToggle(event); onClick(); }}>{label}</button>;
}

export function TestsView({ navTarget, onNavConsumed, onOpenDaxLab, onOpenSavedReports }: {
  navTarget?: { ref: string; nonce: number } | null;
  onNavConsumed?: (nonce: number) => void;
  onOpenDaxLab?: (measureRef: string) => void;
  onOpenSavedReports?: () => void;
}) {
  const { conn, openConnections, connectionsOpen } = useConnection();
  const [run, setRun] = useState<TestRunW | null>(null);
  const [suite, setSuite] = useState<TestSuiteInfoW | null>(null);
  const [sources, setSources] = useState<SqlSourceRecord[] | null>(null);
  const [mappings, setMappings] = useState<TableMappingListW | null>(null);
  const [history, setHistory] = useState<TestHistoryW | null>(null);
  const [savedReports, setSavedReports] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [loadingSuite, setLoadingSuite] = useState(true);
  const [reportOpen, setReportOpen] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [staleReason, setStaleReason] = useState<'model' | 'suite' | null>(null);
  const [recordNote, setRecordNote] = useState<string | null>(null);
  const evidenceRevision = useRef(0);
  const [author, setAuthor] = useState<AuthorShape | null>(null);
  const [editing, setEditing] = useState<TestDefinitionW | null>(null);
  const [authorFocus, setAuthorFocus] = useState<AuthorFocus>(undefined);
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
  useEffect(() => onDidChange(() => { evidenceRevision.current++; setStaleReason('model'); }), []);

  const loadSuite = useCallback(() => rpc<TestSuiteInfoW>('listTests')
    .then(setSuite).catch(() => undefined).finally(() => setLoadingSuite(false)), []);
  const loadSources = useCallback(() => listSqlSources().then(setSources).catch(() => setSources([])), []);
  const loadMappings = useCallback(() => rpc<TableMappingListW>('listTableMappings').then(setMappings).catch(() => setMappings({ rows: [] })), []);
  const loadHistory = useCallback(() => rpc<TestHistoryW>('listTestRuns', 20).then(setHistory).catch(() => setHistory({ runs: [] })), []);
  const loadReportCount = useCallback(() => rpc<EvidenceLibraryW>('listEvidence')
    .then((library) => setSavedReports(library.items.length)).catch(() => setSavedReports(null)), []);
  useEffect(() => {
    void loadSuite(); void loadSources(); void loadMappings(); void loadHistory(); void loadReportCount();
  }, [loadSuite, loadSources, loadMappings, loadHistory, loadReportCount]);
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

  // ONE run path. `only` is null for everything; a named subset is the ticked ids, which the page has
  // already refused if empty. `sections` only ever carries a family the engine recognises, never a tab name.
  const runSuite = (options: { only?: string[] | null; sections?: string[] | null; persist?: boolean }) => {
    const startedAt = evidenceRevision.current;
    setBusy(true);
    setErr(null);
    setRecordNote(null);
    rpc<TestRunW>('runTests', options.persist ?? false, options.only ?? null, options.sections ?? null)
      .then((next) => {
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
        if (options.persist) { void loadHistory(); void loadReportCount(); }
      })
      .catch((error: unknown) => setErr(error instanceof Error ? error.message : String(error)))
      .finally(() => setBusy(false));
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

  const openAuthor = (shape: AuthorShape, definition: TestDefinitionW | null, focus?: AuthorFocus) => {
    setEditing(definition); setAuthorFocus(focus); setAuthor(shape);
  };
  const closeAuthor = () => { setAuthor(null); setEditing(null); setAuthorFocus(undefined); };
  // 5. One identity. Map source on a table row and New check > Table row count open the SAME setup,
  // because for a table count the MAPPING is the check (the engine refuses a tableRowCount test kind).
  const openTableMapping = (table?: string) => setMappingTable(table ?? '');
  const addSqlSource = () => openSqlSources(openConnections);

  return (
    <div className="h-full overflow-auto">
      {author && <AuthorDrawer shape={author} editing={editing} focus={authorFocus} latestSources={sources} onClose={closeAuthor}
        onSaved={() => { void loadSuite(); void loadMappings(); }} onAddSqlSource={addSqlSource} />}
      {mappingTable != null && <AuthorDrawer shape="tableRowCount" table={mappingTable} latestSources={sources} onClose={() => setMappingTable(null)}
        onSaved={() => { void loadMappings(); setMappingTable(null); }} onAddSqlSource={addSqlSource} />}
      {reportOpen && <ReportExportDialog modelName={run?.modelName} onSaved={loadReportCount} onClose={() => setReportOpen(false)} />}
      <main className="sem-centered-page w-full min-w-0 px-7 pt-4 pb-12">

        {/* 1. One tool row. Everything that starts an action lives here and nowhere else. */}
        <div className="sem-tests-toolrow mb-3 flex h-10 flex-wrap items-center gap-1.5">
          <h1 className="m-0 mr-1.5 text-[13.5px] font-bold">Tests</h1>
          <RunMenu busy={busy} groups={groups} tickedCount={ticked.size} savedCheckCount={definitions.length}
            onRunAll={() => runEverything(false)} onRunAndRecord={() => runEverything(true)}
            onRunTicked={runTicked} onRunGroup={runGroup} />
          <NewCheckMenu onPick={(shape) => shape === 'tableRowCount' ? openTableMapping() : openAuthor(shape, null)} />
          <ReportMenu disabled={!run} count={savedReports} onLatest={() => setReportOpen(true)} onSaved={onOpenSavedReports} />
          <div className="ml-auto flex flex-wrap items-center gap-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            <span>Runs on <strong style={{ color: 'var(--sem-fg)' }}>{testModelLabel ?? 'no test model'}</strong></span>
            <span aria-hidden className="inline-block h-[18px] w-px" style={{ background: 'var(--sem-border)' }} />
            <span>SQL sources: <strong style={{ color: 'var(--sem-fg)' }}>{sources == null ? '·' : sources.length}</strong></span>
            <button type="button" className="font-semibold" style={{ color: 'var(--sem-accent)' }} onClick={addSqlSource}>Add SQL source</button>
          </div>
        </div>

        {err && <div className="mb-3"><Banner color="var(--sem-bad)">{err}</Banner></div>}
        {recordNote && <div className="mb-3"><Banner color="var(--sem-warn)">{recordNote}</Banner></div>}
        {refused && <div className="mb-3"><Banner color="var(--sem-warn)">{run?.note ?? 'That run asked for nothing, so nothing ran.'}</Banner></div>}
        {run?.note && !refused && <div className="mb-3"><Banner color="var(--sem-warn)">{run.note}</Banner></div>}
        {run && staleReason && <div className="mb-3"><Banner color="var(--sem-warn)">{staleReason === 'suite'
          ? 'A saved check changed after this run. The results below still show the older check, so you can compare. Run again for current results.'
          : 'The model changed. These results may be out of date. Run again to check your latest changes.'}</Banner></div>}

        {/* 2. One status line: grade, counts, coverage, when, and whether it was kept. */}
        <StatusLine run={run} busy={busy} partRun={partRun} onRecord={recordThisRun} />

        <div className="mt-3 grid gap-3">
          <SavedChecks
            definitions={definitions} latest={latest} loading={loadingSuite} liveModel={!!testModelLabel}
            ticked={ticked} onTick={setTicked}
            onEdit={(def, focus) => openAuthor(def.kind === 'measureValue' ? 'measureValue' : 'measureReconcile', def, focus)}
            onNew={() => openAuthor('measureValue', null)}
            onRunOne={runOne} onChanged={() => { void loadSuite(); void loadMappings(); }}
            onConnect={() => openConnections()} onOpenDaxLab={onOpenDaxLab} />

          <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit,minmax(420px,1fr))' }}>
            <Relationships retained={relationships} skipped={!!run && !familyRan(run.scope, 'relationships')}
              hasRun={!!run} liveModel={!!testModelLabel} onConnect={() => openConnections()} />
            <TableRowCounts list={mappings} skipped={!!run && !familyRan(run.scope, 'table counts')} onMapSource={openTableMapping} />
          </div>

          <History history={history} savedReports={savedReports} onOpenSavedReports={onOpenSavedReports}
            onRecord={recordThisRun} latestRun={run} busy={busy} />
        </div>
      </main>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// 1. Run, and the scope menu that says exactly what "all" includes.
// ---------------------------------------------------------------------------------------------------
function RunMenu({ busy, groups, tickedCount, savedCheckCount, onRunAll, onRunAndRecord, onRunTicked, onRunGroup }: {
  busy: boolean; groups: RunGroup[]; tickedCount: number; savedCheckCount: number;
  onRunAll: () => void; onRunAndRecord: () => void; onRunTicked: () => void; onRunGroup: (group: RunGroup) => void;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key !== 'Escape') return; setOpen(false); buttonRef.current?.focus(); };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  const pick = (fn: () => void) => { setOpen(false); fn(); };
  const refusal = tickedRunRefusal(tickedCount);
  const canTick = tickingIsPossible(savedCheckCount);
  return (
    <div ref={wrap} className="relative inline-flex">
      <button type="button" disabled={busy} className="inline-flex h-7 items-center rounded-l-md border px-3 text-[12px] font-semibold disabled:opacity-60"
        onClick={onRunAll} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>
        {busy ? 'Running…' : 'Run all enabled checks'}
      </button>
      <button ref={buttonRef} type="button" aria-label="What to run" aria-haspopup="menu" aria-expanded={open} disabled={busy}
        className="inline-flex h-7 items-center rounded-r-md border border-l-0 px-2 text-[12px] disabled:opacity-60" onClick={() => setOpen((v) => !v)}
        style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>▾</button>
      {open && (
        <div role="menu" aria-label="What to run" className="absolute left-0 top-9 z-20 w-[330px] rounded-md border p-2 text-[12px]"
          style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <Eyebrow>Run all enabled checks includes</Eyebrow>
          <ul className="mt-1.5 mb-2 list-none p-0">
            {groups.length === 0
              ? <li className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Nothing to run yet. Add a check, or map a table to a SQL source.</li>
              : groups.map((group) => (
                // Three buttons all reading "Only this" told nobody which one they were about to run, and
                // two of them sent the same family anyway (Astra, finding 3). Each row says its own name.
                <li key={group.id} className="mb-1">
                  <button type="button" role="menuitem" aria-label={runOnlyLabel(group)}
                    className="flex w-full items-center gap-2 rounded-md border px-2 py-1.5 text-left"
                    style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}
                    onClick={() => pick(() => onRunGroup(group))}>
                    <span className="flex-1">
                      <span className="block font-semibold">{group.label}</span>
                      <span className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{runOnlyLabel(group)}</span>
                    </span>
                    <span className="tnum text-[11px]" style={{ color: 'var(--sem-muted)' }}>{group.count ?? 'every one'}</span>
                  </button>
                </li>
              ))}
          </ul>
          {/* Offered only while there is something to tick. With no saved checks there are no tick boxes on
              the page at all, so this was a dead option (Kane, live on the Yoga, 2026-09-15). */}
          {canTick && <button type="button" role="menuitem" className="mb-1 block w-full rounded-md border px-2 py-1.5 text-left"
            style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }} onClick={() => pick(onRunTicked)}>
            <span className="block font-semibold">Only what I ticked</span>
            <span className="block text-[10px]" style={{ color: refusal ? 'var(--sem-bad)' : 'var(--sem-muted)' }}>
              {tickedMenuNote(tickedCount, savedCheckCount)}
            </span>
          </button>}
          <button type="button" role="menuitem" className="block w-full rounded-md border px-2 py-1.5 text-left"
            style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }} onClick={() => pick(onRunAndRecord)}>
            <span className="block font-semibold">Run everything and record this run</span>
            <span className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>Keeps the result in History. It needs room to store it.</span>
          </button>
        </div>
      )}
    </div>
  );
}

function ReportMenu({ disabled, count, onLatest, onSaved }: { disabled: boolean; count: number | null; onLatest: () => void; onSaved?: () => void }) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  return (
    <div ref={wrap} className="relative">
      <button type="button" aria-haspopup="menu" aria-expanded={open} className="inline-flex h-7 items-center rounded-md border px-3 text-[12px]"
        onClick={() => setOpen((v) => !v)} style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>Report ▾</button>
      {open && (
        <div role="menu" aria-label="Report" className="absolute left-0 top-9 z-20 w-[260px] rounded-md border p-1.5 text-[12px]"
          style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <button type="button" role="menuitem" disabled={disabled} className="mb-1 block w-full rounded-md px-2 py-1.5 text-left disabled:opacity-40"
            onClick={() => { setOpen(false); onLatest(); }}>
            <span className="block font-semibold">Latest</span>
            <span className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{disabled ? 'Run the checks first.' : 'Read, print or export the run above.'}</span>
          </button>
          <button type="button" role="menuitem" className="block w-full rounded-md px-2 py-1.5 text-left"
            onClick={() => { setOpen(false); onSaved?.(); }}>
            <span className="block font-semibold">Saved reports{count == null ? '' : ` ${count}`}</span>
            <span className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>Reports already kept with this model.</span>
          </button>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// 2. One status line.
// ---------------------------------------------------------------------------------------------------
function StatusLine({ run, busy, partRun, onRecord }: {
  run: TestRunW | null; busy: boolean; partRun: boolean; onRecord: () => void;
}) {
  if (!run) {
    return <Card className="sem-tests-status flex flex-wrap items-center gap-3 px-3 py-2 text-[12.5px]">
      <span style={{ color: 'var(--sem-muted)' }}>{busy ? 'Running the checks…' : 'No run yet. Run all enabled checks to see where this model stands.'}</span>
    </Card>;
  }
  const health = run.health;
  return (
    <Card className="sem-tests-status flex flex-wrap items-center gap-x-4 gap-y-1 px-3 py-2 text-[12.5px]">
      {/* H2. A part run's letter is REAL, and it is the grade of what ran. It is shown, and what it covers
          is said beside it, so it can never be read as the model's overall grade. */}
      {!health
        ? <span className="inline-flex h-[22px] min-w-[30px] items-center justify-center rounded-md px-1.5 text-[11px] font-extrabold"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>·</span>
        : <span className="inline-flex h-[22px] min-w-[30px] items-center justify-center rounded-md text-[13px] font-extrabold"
          style={{ background: partRun ? 'var(--sem-surface-2)' : 'var(--sem-accent-soft)', color: GRADE_COLOR[health.grade] ?? 'var(--sem-nv)' }}>{health.grade}</span>}
      {partRun && run.scope?.gradeCovers && <span className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>for {run.scope.gradeCovers}</span>}
      {health
        ? <span><strong>{health.passed.toLocaleString()} of {health.checked.toLocaleString()} pass</strong> · {health.failed.toLocaleString()} differ · {(health.notVerifiable + health.missing).toLocaleString()} could not check</span>
        : <span style={{ color: 'var(--sem-muted)' }}>No grade for this run.</span>}
      {health && <span style={{ color: 'var(--sem-muted)' }}>{health.coveragePct}% coverage</span>}
      {/* The engine's own sentences. The page used to prefix "Grade capped:" and join with commas, which
          read as a fragment beside a sentence that already says the grade cannot rise. */}
      {health && health.gatedBy.length > 0 ? <span style={{ color: 'var(--sem-bad)' }}>{health.gatedBy.join(' ')}</span> : null}
      <span className="ml-auto flex flex-wrap items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
        <span>Last run {formatDate(run.when)}</span>
        {run.persisted
          ? <span>· recorded</span>
          : <span className="flex items-center gap-2">· this run is not recorded
            <button type="button" disabled={busy} className="font-semibold disabled:opacity-50" style={{ color: 'var(--sem-accent)' }} onClick={onRecord}>Record it</button></span>}
      </span>
      {partRun && <span className="basis-full text-[11px]" style={{ color: 'var(--sem-warn)' }}>{partRunSummary(run.scope?.gradeCovers, run.scope?.skipped ?? [])}</span>}
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------
// 3. ONE list of saved checks.
// ---------------------------------------------------------------------------------------------------
const CHIP_ORDER: ResultChip[] = ['Differs', 'Pass', 'Could not check', 'Off', 'Not run'];
const chipRank: Record<ResultChip, number> = { Differs: 0, 'Could not check': 1, 'Not run': 2, Pass: 3, Off: 4 };

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
function authoredSuffix(def: TestDefinitionW): string {
  return def.authoredAgainstLabel ? ` \u00b7 authored against ${def.authoredAgainstLabel}` : '';
}

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

function SavedChecks({ definitions, latest, loading, liveModel, ticked, onTick, onEdit, onNew, onRunOne, onChanged, onConnect, onOpenDaxLab }: {
  definitions: TestDefinitionW[]; latest: Record<string, StoredResult<ReconcileOutcomeW>>; loading: boolean; liveModel: boolean;
  ticked: Set<string>; onTick: (next: Set<string>) => void;
  onEdit: (def: TestDefinitionW, focus?: AuthorFocus) => void; onNew: () => void;
  onRunOne: (defId: string) => void; onChanged: () => void; onConnect: () => void; onOpenDaxLab?: (measureRef: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<'All' | ResultChip>('All');
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [menu, setMenu] = useState<string | null>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  // 10b. A failed row action writes its reason into ITS OWN row. Astra turned a check off with a save
  // error injected: the menu closed, the check stayed on, and nothing at all was said.
  const [rowError, setRowError] = useState<Record<string, string>>({});
  const setRowErrorFor = (id: string, message: string | null) => setRowError((current) => {
    const next = { ...current };
    if (message) next[id] = message; else delete next[id];
    return next;
  });

  const rows = useMemo(() => definitions.map((def) => {
    const stored = latest[def.id];
    return { def, outcome: stored?.outcome, whenIso: stored?.whenIso, chip: resultChip(def.enabled, stored?.outcome) };
  }), [definitions, latest]);

  // The chips count the rows this list actually holds, so a chip can never promise rows the list is not
  // showing (Astra point 9: the drawing selected Differs while displaying Pass, Could not check and Off).
  const chipCounts = useMemo(() => {
    const counts: Record<ResultChip, number> = { Pass: 0, Differs: 0, 'Could not check': 0, Off: 0, 'Not run': 0 };
    rows.forEach((row) => { counts[row.chip] += 1; });
    return counts;
  }, [rows]);

  const shown = useMemo(() => rows
    .filter((row) => `${row.def.title} ${row.def.targetRef ?? ''} ${row.outcome?.message ?? ''}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()))
    .filter((row) => filter === 'All' || row.chip === filter)
    .sort((a, b) => chipRank[a.chip] - chipRank[b.chip] || a.def.title.localeCompare(b.def.title)), [rows, query, filter]);

  const toggleOpen = (id: string) => setOpen((current) => { const next = new Set(current); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  const toggleTick = (id: string) => { const next = new Set(ticked); if (next.has(id)) next.delete(id); else next.add(id); onTick(next); };
  const toggleEnabled = (def: TestDefinitionW) => {
    setRowErrorFor(def.id, null);
    rpc('saveTest', { ...def, enabled: !def.enabled }).then(() => { announceTestSuiteChanged(); onChanged(); })
      .catch((e: unknown) => setRowErrorFor(def.id, `Could not turn this check ${def.enabled ? 'off' : 'on'}: ${e instanceof Error ? e.message : String(e)}`));
  };
  const remove = (def: TestDefinitionW) => {
    setRowErrorFor(def.id, null);
    rpc<boolean>('deleteTest', def.id).then(() => { setConfirmId(null); announceTestSuiteChanged(); onChanged(); })
      .catch((e: unknown) => { setConfirmId(null); setRowErrorFor(def.id, `Could not delete this check: ${e instanceof Error ? e.message : String(e)}`); });
  };

  const columns = '40px minmax(190px,1.5fr) 132px minmax(140px,1.1fr) 128px 118px 30px';
  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-2 border-b px-3.5 py-2.5" style={{ borderColor: 'var(--sem-border)' }}>
        <h2 className="m-0 text-[13.5px] font-semibold">Saved checks</h2>
        {/* Tick boxes only exist on these rows, so with no saved checks "Tick to run a subset" described a
            control that was not on the page (Kane, live on the Yoga, 2026-09-15). */}
        <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Each one compares a number in your model with an answer you trust.{' '}
          {savedChecksHint(definitions.length)}</span>
      </div>
      <div className="flex flex-wrap items-center gap-1.5 border-b px-3.5 py-2" style={{ borderColor: 'var(--sem-border)' }}>
        <input aria-label="Search checks and results" value={query} onChange={(event) => setQuery(event.target.value)}
          placeholder="Search checks and results…" className="h-6 min-w-[220px] rounded-md border px-2 text-[12px] outline-none"
          style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' }} />
        {(['All', ...CHIP_ORDER] as const).map((key) => (
          <button key={key} type="button" onClick={() => setFilter(key)}
            className="rounded-full border px-2.5 py-1 text-[11px]"
            style={{
              background: filter === key ? 'var(--sem-accent-soft)' : 'var(--sem-surface)',
              borderColor: filter === key ? 'var(--sem-accent)' : 'var(--sem-border)',
              color: key === 'All' ? 'var(--sem-fg)' : CHIP_COLOR[key],
            }}>{key} <span className="tnum">{key === 'All' ? rows.length : chipCounts[key]}</span></button>
        ))}
      </div>

      {loading ? <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading your saved checks…</div>
        : definitions.length === 0 ? (
          <div className="p-4 text-[12px]">
            <strong>No saved checks yet.</strong>
            <p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>
              Relationships and table row counts are checked when you run everything, so this model is not unchecked.
              A saved check adds the numbers only you know are right.
            </p>
            <button type="button" className="mt-2 rounded-md border px-3 py-1 text-[12px] font-semibold"
              style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}
              onClick={onNew}>New check</button>
          </div>
        ) : (
          <>
            <div className="grid items-center border-b px-3.5 py-1.5 text-[10.5px] font-semibold uppercase tracking-[0.06em]"
              style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
              <span title="Tick the checks you want to run on their own">Tick</span><span>Check</span><span>Kind</span><span>Expected · actual</span><span>Result</span><span>Last run</span><span />
            </div>
            {shown.map(({ def, outcome, whenIso, chip }) => (
              <CheckRow key={def.id} def={def} outcome={outcome} whenIso={whenIso} chip={chip} columns={columns}
                liveModel={liveModel} isOpen={open.has(def.id)} onToggle={() => toggleOpen(def.id)}
                ticked={ticked.has(def.id)} onTick={() => toggleTick(def.id)}
                menuOpen={menu === def.id} onMenu={() => setMenu(menu === def.id ? null : def.id)} onCloseMenu={() => setMenu(null)}
                error={rowError[def.id]} onDismissError={() => setRowErrorFor(def.id, null)}
                confirming={confirmId === def.id} onConfirm={() => setConfirmId(def.id)} onCancelConfirm={() => setConfirmId(null)}
                onDelete={() => remove(def)} onToggleEnabled={() => toggleEnabled(def)}
                onEdit={onEdit} onRunOne={() => onRunOne(def.id)} onConnect={onConnect} onOpenDaxLab={onOpenDaxLab} />
            ))}
            {shown.length === 0 && <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No checks match this search and filter.</div>}
            <div className="px-3.5 py-2 text-[12px]" style={{ color: 'var(--sem-muted)' }}>
              Showing {shown.length.toLocaleString()} of {rows.length.toLocaleString()}.
            </div>
          </>
        )}
    </Card>
  );
}

function CheckRow(props: {
  def: TestDefinitionW; outcome?: ReconcileOutcomeW; whenIso?: string; chip: ResultChip; columns: string;
  liveModel: boolean; isOpen: boolean; onToggle: () => void; ticked: boolean; onTick: () => void;
  menuOpen: boolean; onMenu: () => void; onCloseMenu: () => void;
  error?: string; onDismissError: () => void;
  confirming: boolean; onConfirm: () => void; onCancelConfirm: () => void; onDelete: () => void; onToggleEnabled: () => void;
  onEdit: (def: TestDefinitionW, focus?: AuthorFocus) => void; onRunOne: () => void; onConnect: () => void;
  onOpenDaxLab?: (measureRef: string) => void;
}) {
  const { def, outcome, whenIso, chip, columns, isOpen } = props;
  const params = def.kind === 'measureValue' ? parseValueParams(def) : undefined;
  const expected = outcome?.expected ?? params?.expectedValue;
  const actual = outcome?.actual ?? (chip === 'Off' || chip === 'Not run' ? 'not run' : undefined);
  const reason = chip === 'Could not check' ? unknownCellText(outcome) : null;
  // Space on the tick box used to bubble to the row, which expanded the row AND cancelled the tick, so the
  // checkbox could be reached by keyboard and not used by it (Kane, live on the Yoga, 2026-09-15). A control
  // inside the row handles its own keys; only the row itself toggles the row.
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) return;
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); props.onToggle(); }
  };
  return <>
    <div role="button" tabIndex={0} aria-expanded={isOpen} onClick={props.onToggle} onKeyDown={onKeyDown}
      className="grid min-h-[46px] cursor-pointer items-center border-b px-3.5 text-[12.5px]"
      style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)', background: isOpen ? 'var(--sem-surface-2)' : 'var(--sem-surface)' }}>
      <span onClick={stopRowToggle} className="flex items-center">
        <input type="checkbox" className="h-[14px] w-[14px] cursor-pointer" checked={props.ticked} onChange={props.onTick}
          title={`Tick ${def.title} to run it on its own`} aria-label={`Tick ${def.title} to run on its own`} />
      </span>
      <span className="min-w-0 pr-2">
        <span className="block truncate font-semibold">{def.title}</span>
        <span className="block truncate text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
          {shortRef(def.targetRef)}{def.enabled ? '' : ' · turned off'}{asksSummary(def)}{authoredSuffix(def)}
        </span>
      </span>
      <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{kindLabel(def.kind)}</span>
      {/* A row that could not be checked used to read "· · ·", so the one thing a person needed was only
          reachable by opening it. The headline reason goes in the cell; the engine's own words stay on the
          hover and in the opened row (Astra, question 1). */}
      {reason
        ? <span className="truncate pr-2" title={outcome?.message} style={{ color: 'var(--sem-nv)' }}>{reason}</span>
        : <span className="tnum truncate pr-2">{expected ? <>{expected} · </> : null}<strong>{actual ?? '\u00b7'}</strong></span>}
      <span><Chip chip={chip} title={outcome?.message} /></span>
      <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
        {chip === 'Could not check' && !props.liveModel ? 'No live connection' : whenIso ? formatDate(whenIso) : '·'}
      </span>
      <span className="relative" onClick={stopRowToggle}>
        <button type="button" className="px-1 text-[14px]" aria-label={`More for ${def.title}`} aria-haspopup="menu" onClick={props.onMenu}>⋯</button>
        {props.menuOpen && (
          <span role="menu" aria-label={`Actions for ${def.title}`} className="absolute right-0 z-20 mt-1 block w-[170px] rounded-md border p-1"
            style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
            <button type="button" role="menuitem" className="block w-full rounded px-2 py-1 text-left text-[12px]" onClick={() => { props.onCloseMenu(); props.onRunOne(); }}>Run this one</button>
            <button type="button" role="menuitem" className="block w-full rounded px-2 py-1 text-left text-[12px]" onClick={() => { props.onCloseMenu(); props.onEdit(def); }}>Edit</button>
            <button type="button" role="menuitem" className="block w-full rounded px-2 py-1 text-left text-[12px]" onClick={() => { props.onCloseMenu(); props.onToggleEnabled(); }}>{def.enabled ? 'Turn off' : 'Turn on'}</button>
            <button type="button" role="menuitem" className="block w-full rounded px-2 py-1 text-left text-[12px]" style={{ color: 'var(--sem-bad)' }} onClick={() => { props.onCloseMenu(); props.onConfirm(); }}>Delete</button>
          </span>
        )}
      </span>
    </div>
    {props.error && (
      <div className="border-b px-3.5 py-2 text-[12px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-bad)' }}>
        {props.error} <button type="button" className="ml-2 underline" onClick={props.onDismissError}>Hide this</button>
      </div>
    )}
    {props.confirming && (
      <div className="border-b px-3.5 py-2 text-[12px]" style={{ borderColor: 'var(--sem-border)' }}>
        Delete the check &apos;{def.title}&apos;? Recorded runs keep their record. This cannot be undone.
        <span className="mt-2 flex gap-2">
          <button type="button" className="rounded-md border px-2 py-1 text-[12px]" style={{ color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }} onClick={props.onDelete}>Delete</button>
          <button type="button" className="text-[12px]" onClick={props.onCancelConfirm}>Keep it</button>
        </span>
      </div>
    )}
    {isOpen && <CheckDetail def={def} outcome={outcome} liveModel={props.liveModel} onConnect={props.onConnect}
      onEdit={props.onEdit} onRunOne={props.onRunOne} onDelete={props.onConfirm} onOpenDaxLab={props.onOpenDaxLab} />}
  </>;
}

// H3. The opened row. A trusted-answer check and a compare-with-source check read the SAME three numbers,
// so neither borrows the other's vocabulary. Only a compare-with-source check has comparisons to show.
function CheckDetail({ def, outcome, liveModel, onConnect, onEdit, onRunOne, onDelete, onOpenDaxLab }: {
  def: TestDefinitionW; outcome?: ReconcileOutcomeW; liveModel: boolean; onConnect: () => void;
  onEdit: (def: TestDefinitionW, focus?: AuthorFocus) => void; onRunOne: () => void; onDelete: () => void;
  onOpenDaxLab?: (measureRef: string) => void;
}) {
  const [showRows, setShowRows] = useState(false);
  const isTrustedAnswer = def.kind === 'measureValue';
  const reconcile = isTrustedAnswer ? undefined : parseReconcileParams(def);
  const value = isTrustedAnswer ? parseValueParams(def) : undefined;
  const sourceLabel = isTrustedAnswer ? 'the answer you trust' : (reconcile?.sqlSourceName || 'the source');
  const rows = outcome?.rows ?? [];
  const differing = rows.filter((row) => comparisonVerdict(row.verdict) === 'Fail');
  // FOUND BY DRIVING IT: the engine keeps only the worst rows, so counting the kept ones said
  // "3 differ; 2 match" about a comparison where 45 actually matched. The counts come from the
  // outcome's own totals whenever it has them; the kept rows are only a fallback.
  const differCount = outcome?.mismatches ?? differing.length;
  const matchCount = outcome?.matches ?? (rows.length - differing.length);

  if (!outcome) {
    return <div className="border-b px-6 py-3 text-[12px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div style={{ color: 'var(--sem-muted)' }}>
        {def.enabled ? 'This check has not run yet.' : 'This check is turned off, so it did not run.'}
        {!liveModel && ' There is no live test model to ask.'}
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5">
        {!liveModel && <RowAction label="Connect a test model" onClick={onConnect} tone="var(--sem-accent)" />}
        <RowAction label="Edit this check" onClick={() => onEdit(def)} />
        <RowAction label="Run this one" onClick={onRunOne} />
      </div>
    </div>;
  }

  if (outcome.missing) {
    return <div className="border-b px-6 py-3 text-[12px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)', color: 'var(--sem-nv)' }}>
      The calculation this check was about no longer exists, so there was nothing to ask.
      <span className="mt-2 flex flex-wrap gap-1.5">
        <RowAction label="Edit this check" onClick={() => onEdit(def)} />
        <RowAction label="Delete this check" onClick={onDelete} />
      </span>
    </div>;
  }

  if (isBlankStandoff(outcome.message)) {
    return <div className="border-b px-6 py-3 text-[12px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)', color: 'var(--sem-nv)' }}>
      {BLANK_STANDOFF}
      <span className="mt-2 flex flex-wrap items-center gap-1.5">
        <RowAction label="Choose what a blank means" onClick={() => onEdit(def, 'blank')} />
        to check it.
      </span>
    </div>;
  }

  return (
    <div className="border-b px-6 py-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div className="text-[12.5px] font-semibold">
        {differenceSentence(outcome.expected ?? value?.expectedValue, outcome.actual, outcome.difference ?? null, sourceLabel)}
      </div>
      {outcome.difference == null && outcome.message && <div className="mt-1 text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>{outcome.message}</div>}
      <div className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-[11.5px]">
        <span style={{ color: 'var(--sem-muted)' }}>Expected <strong style={{ color: 'var(--sem-fg)' }}>{outcome.expected ?? value?.expectedValue ?? '·'}</strong>{isTrustedAnswer ? '' : ` from ${sourceLabel}`}</span>
        <span style={{ color: 'var(--sem-muted)' }}>Actual <strong style={{ color: 'var(--sem-fg)' }}>{outcome.actual ?? '·'}</strong> from the model</span>
        <span style={{ color: 'var(--sem-muted)' }}>{isTrustedAnswer
          ? toleranceSentence(value?.toleranceAbsolute, value?.toleranceRelative)
          : toleranceSentence(reconcile?.toleranceAbsolute, reconcile?.toleranceRelative)}</span>
        <span style={{ color: 'var(--sem-muted)' }}>Filter <strong style={{ color: 'var(--sem-fg)' }}>{filterSummary(def)}</strong></span>
      </div>
      {!isTrustedAnswer && rows.length > 0 && (
        <div className="mt-2 text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>
          {comparisonSummary(differCount, matchCount, rows.length, outcome.rowsTotal ?? rows.length)}
        </div>
      )}
      <WhereItRan outcome={outcome} />
      {!liveModel && <div className="mt-2 flex items-center gap-2 text-[11.5px]" style={{ color: 'var(--sem-nv)' }}>
        This ran without a live test model.<RowAction label="Connect a test model" onClick={onConnect} tone="var(--sem-accent)" />
      </div>}
      <div className="mt-2.5 flex flex-wrap gap-1.5">
        {def.targetRef && onOpenDaxLab && <RowAction label="Open in DAX Lab" onClick={() => onOpenDaxLab(def.targetRef as string)} />}
        {!isTrustedAnswer && differing.length > 0 && <RowAction label="Show the differing comparisons" onClick={() => setShowRows((v) => !v)} />}
        <RowAction label="Edit this check" onClick={() => onEdit(def)} />
        <RowAction label="Run this one" onClick={onRunOne} />
      </div>
      {showRows && differing.length > 0 && <CompareRows rows={differing} total={outcome.rowsTotal ?? rows.length} sourceLabel={sourceLabel} />}
    </div>
  );
}

function WhereItRan({ outcome }: { outcome: ReconcileOutcomeW }) {
  const authored = outcome.authoredAgainstLabel;
  const ran = outcome.ranAgainstLabel;
  if (!authored && !ran) return null;
  const text = authored && ran && authored !== ran
    ? `Authored against ${authored}, ran against ${ran}. Those are different models, so read this result as a statement about ${ran}.`
    : authored
      ? `Authored against ${authored}${ran ? ', and ran against it' : ''}.`
      : `Ran against ${ran}. No authoring connection was recorded for this check.`;
  return <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{text}</div>;
}

function CompareRows({ rows, total, sourceLabel }: { rows: CompareRowW[]; total: number; sourceLabel: string }) {
  return <div className="mt-2.5 overflow-hidden rounded-md border" role="table" aria-label="The comparisons that differ" style={{ borderColor: 'var(--sem-border)' }}>
    <div role="row" className="grid border-b px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.05em]"
      style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
      <span role="columnheader">Where</span><span role="columnheader">{sourceLabel}</span><span role="columnheader">The model</span><span role="columnheader">Difference</span>
    </div>
    {rows.map((row, index) => (
      <div role="row" key={`${row.context}-${index}`} className="grid border-b px-3 py-1.5 text-[11px] last:border-0"
        title={row.explanation} style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', borderColor: 'var(--sem-border)', borderLeft: '3px solid var(--sem-bad)' }}>
        <span role="cell" className={row.grandTotal ? 'font-semibold' : ''}>{row.context}</span>
        <span role="cell" className="tnum">{resultText(row.sql) ?? 'no value'}</span>
        <span role="cell" className="tnum">{resultText(row.dax) ?? 'blank'}</span>
        <span role="cell" className="tnum" style={{ color: 'var(--sem-bad)' }}>{resultText(row.delta, true) ?? '·'}</span>
      </div>
    ))}
    <div className="px-3 py-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
      {rows.length.toLocaleString()} of {total.toLocaleString()} comparisons are shown here.
    </div>
  </div>;
}

// ---------------------------------------------------------------------------------------------------
// 4. Relationships, in plain words. Every specialist term the UAT flagged is gone from what a person
//    reads: no referential integrity, no orphans, no cardinality, no hidden blank row.
// ---------------------------------------------------------------------------------------------------
const BLANK_STANDOFF = 'Every cell is blank in the model and NULL in the source. This test does not treat those as equal.';
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

function Relationships({ retained, skipped, hasRun, liveModel, onConnect }: {
  retained: RetainedFamily<RelationshipReportW> | null; skipped: boolean;
  hasRun: boolean; liveModel: boolean; onConnect: () => void;
}) {
  const rels = retained?.value.relationships ?? [];
  const [open, setOpen] = useState<string | null>(null);
  // An unknown relationship is NOT fine. "3 fine" used to include one whose checks were all unknown, and a
  // card where every check was unknown showed a Pass header (Astra, finding 5; seen live on Kane's Fabric
  // model, where every relationship read Could not check under a PASS header).
  const tally: RelationshipTally = relationshipTally(rels.map(relationshipChecks));
  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-2 border-b px-3.5 py-2.5" style={{ borderColor: 'var(--sem-border)' }}>
        <h2 className="m-0 text-[13.5px] font-semibold">Relationships</h2>
        <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Checked when you run everything. Nothing to set up.</span>
        {rels.length > 0 && <span className="ml-auto flex items-center gap-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          <Chip chip={relationshipChip(tally)} title={relationshipSummary(tally)} />
          {relationshipSummary(tally)}
        </span>}
      </div>
      {/* A run that SKIPPED this family says so, and the rows below it keep their own earlier date. The old
          page rendered the skipped run's empty report and concluded the model has no relationships. */}
      {skipped && <div className="border-b px-3.5 py-2 text-[11.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-warn)' }}>
        {skippedFamilyNote('relationships', retained?.whenIso)}
      </div>}
      {rels.length === 0 ? (
        <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>
          {!hasRun ? 'Relationships are checked when you run everything. Run all enabled checks to see them.'
            : skipped ? 'Run everything to check them.'
            : !liveModel ? <>Relationships need a live test model to count rows. <button type="button" className="font-semibold underline" style={{ color: 'var(--sem-accent)' }} onClick={onConnect}>Connect a test model</button> and run again.</>
              : 'This model has no relationships to check.'}
        </div>
      ) : <>
        <div className="grid items-center border-b px-3.5 py-1.5 text-[10.5px] font-semibold uppercase tracking-[0.06em]"
          style={{ gridTemplateColumns: '1.2fr 1.1fr 0.9fr 108px 96px', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
          <span>Relationship</span><span>Rows that match</span><span>Lookup values unique</span><span>Result</span><span />
        </div>
        {rels.map((rel) => <RelationshipRow key={rel.name} rel={rel} open={open === rel.name} onToggle={() => setOpen(open === rel.name ? null : rel.name)} />)}
      </>}
    </Card>
  );
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
    <div className="grid items-center border-b px-3.5 py-2 text-[12px]" style={{ gridTemplateColumns: '1.2fr 1.1fr 0.9fr 108px 96px', borderColor: 'var(--sem-border)' }}>
      <span className="truncate">{rel.manyTable} → {rel.oneTable}</span>
      <span className="tnum truncate">{matchText(rel)}</span>
      <span>{unique === 'Pass' ? 'Yes' : unique === 'NotVerifiable' ? 'Could not check' : 'No'}</span>
      <span><Chip chip={chip} /></span>
      <span><button type="button" className="text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }} onClick={onToggle}>{open ? 'Hide details' : 'Show details'}</button></span>
    </div>
    {open && (
      <div className="border-b px-6 py-2.5 text-[11.5px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
        {/* There is no engine operation that returns the unmatched rows themselves, so this shows what the
            check actually COUNTED rather than promising records it cannot fetch (Astra point 7). */}
        <div style={{ color: 'var(--sem-muted)' }}>This is what the check counted.</div>
        <ul className="mt-1.5 list-none p-0">
          <li className="mb-1"><strong>Rows that match:</strong> {rel.referentialIntegrity.message ?? matchText(rel)}</li>
          <li className="mb-1"><strong>Lookup values unique:</strong> {rel.keyUniqueness.message ?? (unique === 'Pass' ? 'Every lookup value occurs once.' : 'This was not checked.')}</li>
          <li><strong>Column types compatible:</strong> {rel.dataTypeMatch.message ?? (types === 'Pass' ? 'The joined columns use types that work together.' : 'This was not checked.')}</li>
        </ul>
        {rel.referentialIntegrity.rootCause && <div className="mt-1.5" style={{ color: 'var(--sem-warn)' }}>This result depends on another problem first: {rel.referentialIntegrity.rootCause}</div>}
        {attention && !unmatched && <button type="button" className="mt-2 rounded-md border px-2 py-1 text-[11px] font-semibold"
          style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }} disabled={loadingRows}
          onClick={showRows}>{loadingRows ? 'Reading…' : 'Show the values that do not match'}</button>}
        {unmatched && <UnmatchedRows result={unmatched} oneTable={rel.oneTable} />}
      </div>
    )}
  </>;
}

// Each row is one lookup value the other table does not have, with how much data hangs off it, biggest
// first. Two hundred rows of the same value tell a person nothing; WHICH values are missing is the answer.
function UnmatchedRows({ result, oneTable }: { result: UnmatchedRowsW; oneTable: string }) {
  if (result.error) return <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{result.error}</div>;
  const rows = result.rows ?? [];
  if (rows.length === 0) return <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{result.note ?? 'No values came back.'}</div>;
  return <div className="mt-2">
    <div className="overflow-hidden rounded-md border" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="grid border-b px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.05em]"
        style={{ gridTemplateColumns: 'minmax(0,1fr) 140px', background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
        <span>Value with no {oneTable}</span><span>Rows carrying it</span>
      </div>
      {rows.slice(0, 20).map((row, index) => (
        <div key={index} className="grid border-b px-3 py-1 text-[11px] last:border-0"
          style={{ gridTemplateColumns: 'minmax(0,1fr) 140px', borderColor: 'var(--sem-border)' }}>
          <span className="truncate">{row[0] == null ? 'blank' : String(row[0])}</span>
          <span className="tnum">{row[1] == null ? '·' : Number(row[1]).toLocaleString()}</span>
        </div>
      ))}
    </div>
    <div className="mt-1 text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
      {rows.length > 20 && `Showing 20 of ${rows.length.toLocaleString()}${result.truncated ? ' read' : ''}. `}
      {result.note ?? 'This reads the data as it is now, so it can differ from what the check counted when it ran.'}
    </div>
  </div>;
}

// ---------------------------------------------------------------------------------------------------
// 5. Table row counts. One home, one identity: Map source here and New check > Table row count open the
//    SAME setup, because for a table count the mapping IS the check.
// ---------------------------------------------------------------------------------------------------
function countChip(row: TableMappingRowW): CountChip {
  if (!row.canCount) return 'Not checked';
  if (row.lastModelCount == null || row.lastSourceCount == null) return 'Not checked';
  if (row.lastVerdict === 'Pass' || row.lastModelCount === row.lastSourceCount) return 'Match';
  return 'Counts differ';
}

function TableRowCounts({ list, skipped, onMapSource }: {
  list: TableMappingListW | null; skipped: boolean; onMapSource: (table: string) => void;
}) {
  const rows = list?.rows ?? [];
  // The engine keeps the last counts for this model, so a run that skipped them leaves the numbers below
  // standing. The card says they are older rather than letting them read as this run's.
  const lastCounted = rows.map((row) => row.lastRunUtc).filter(Boolean).sort().pop();
  const summary = useMemo(() => {
    const counts: Record<CountChip, number> = { Match: 0, 'Counts differ': 0, 'Not checked': 0 };
    rows.forEach((row) => { counts[countChip(row)] += 1; });
    return counts;
  }, [rows]);
  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-2 border-b px-3.5 py-2.5" style={{ borderColor: 'var(--sem-border)' }}>
        <h2 className="m-0 text-[13.5px] font-semibold">Table row counts</h2>
        <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Model rows against the source table. Checked when you run everything.</span>
        {rows.length > 0 && <span className="ml-auto text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          {summary.Match} match · {summary['Counts differ']} differ · {summary['Not checked']} not checked
        </span>}
      </div>
      {skipped && <div className="border-b px-3.5 py-2 text-[11.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-warn)' }}>
        {skippedFamilyNote('table counts', lastCounted)}
      </div>}
      {list == null ? <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading the table mappings…</div>
        : rows.length === 0 ? <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>{list.note ?? 'This model has no tables to count.'}</div>
          : <>
            <div className="grid items-center border-b px-3.5 py-1.5 text-[10.5px] font-semibold uppercase tracking-[0.06em]"
              style={{ gridTemplateColumns: '1.1fr 1fr 1.2fr 116px 106px', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
              <span>Table</span><span>Model</span><span>Source</span><span>Result</span><span />
            </div>
            {rows.map((row) => (
              <div key={row.modelTable} className="grid items-center border-b px-3.5 py-2 text-[12px]"
                style={{ gridTemplateColumns: '1.1fr 1fr 1.2fr 116px 106px', borderColor: 'var(--sem-border)' }}>
                <span className="truncate">{row.modelTable}</span>
                <span className="tnum">{countText(row.lastModelCount)}</span>
                <span className="tnum truncate" title={row.lastMessage ?? row.reason ?? undefined}>
                  {row.lastSourceCount != null
                    ? `${row.lastSourceCount.toLocaleString()}${row.sqlSourceName ? ` · ${row.sqlSourceName}` : ''}`
                    : row.canCount ? (row.sqlSourceName ?? 'Mapped, not counted yet')
                      : row.editable ? 'Not mapped' : 'No source table to count against'}
                </span>
                <span><CountPill chip={countChip(row)} title={row.lastMessage ?? row.reason ?? undefined} /></span>
                <span>{row.editable
                  ? <button type="button" className="text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}
                    onClick={() => onMapSource(row.modelTable)}>{row.sqlSourceId ? 'Change source' : 'Map source'}</button>
                  : null}</span>
              </div>
            ))}
          </>}
      <div className="px-3.5 py-2 text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>{TABLE_COUNT_FOOTER}</div>
    </Card>
  );
}

// ---------------------------------------------------------------------------------------------------
// 6. History: a dated list whose runs open to their own evidence.
// ---------------------------------------------------------------------------------------------------
function History({ history, savedReports, onOpenSavedReports, onRecord, latestRun, busy }: {
  history: TestHistoryW | null; savedReports: number | null; onOpenSavedReports?: () => void;
  onRecord: () => void; latestRun: TestRunW | null; busy: boolean;
}) {
  const [openRun, setOpenRun] = useState<TestRunDetailW | null>(null);
  const [loadingId, setLoadingId] = useState<string | null>(null);
  const runs = useMemo(() => [...(history?.runs ?? [])].sort((a, b) => new Date(b.when).valueOf() - new Date(a.when).valueOf()), [history]);
  const columns = '180px 40px minmax(0,1fr) 110px 106px';
  const openOne = (runId: string) => {
    setLoadingId(runId);
    rpc<TestRunDetailW>('getTestRun', runId).then(setOpenRun).catch(() => undefined).finally(() => setLoadingId(null));
  };
  const counts = (health?: TestHealthW) => health
    ? `${health.passed} pass · ${health.failed} differ · ${health.notVerifiable + health.missing} could not check`
    : 'No grade was recorded.';
  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-center gap-2 border-b px-3.5 py-2.5" style={{ borderColor: 'var(--sem-border)' }}>
        <h2 className="m-0 text-[13.5px] font-semibold">History</h2>
        <span className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Recorded runs. Open one to see its evidence.</span>
        <button type="button" className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}
          onClick={() => onOpenSavedReports?.()}>Saved reports{savedReports == null ? '' : ` ${savedReports}`} ›</button>
      </div>
      {latestRun && !latestRun.persisted && (
        <div className="grid items-center border-b px-3.5 py-2 text-[12px]" style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)' }}>
          <span>{formatDate(latestRun.when)}</span>
          <span className="font-extrabold" style={{ color: GRADE_COLOR[latestRun.health?.grade ?? ''] ?? 'var(--sem-nv)' }}>{latestRun.health?.grade ?? '·'}</span>
          <span className="truncate" style={{ color: 'var(--sem-muted)' }}>{counts(latestRun.health)}</span>
          <span style={{ color: 'var(--sem-muted)' }}>not recorded</span>
          <span><button type="button" disabled={busy} className="rounded-md border px-2 py-1 text-[11px] disabled:opacity-50"
            style={{ borderColor: 'var(--sem-border)' }} onClick={onRecord}>Record</button></span>
        </div>
      )}
      {history == null ? <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading the recorded runs…</div>
        : runs.length === 0 ? <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>{history.note ?? 'No recorded runs yet. Record a run to keep it here.'}</div>
          : runs.map((record) => (
            <div key={record.runId} className="grid items-center border-b px-3.5 py-2 text-[12px]"
              style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)' }}>
              <span>{formatDate(record.when)}</span>
              <span className="font-extrabold" style={{ color: GRADE_COLOR[record.health?.grade ?? ''] ?? 'var(--sem-nv)' }}>{record.health?.grade ?? '·'}</span>
              {/* No old-grade label here on purpose: listTestRuns strips outcomes from EVERY row, so a
                  missing outcomes field proves nothing about how that row was scored. The label lives in
                  the opened run, where the engine actually sends gradedBeforeSecurityLeftTests. */}
              <span className="truncate" style={{ color: 'var(--sem-muted)' }}>{counts(record.health)}</span>
              <span style={{ color: 'var(--sem-muted)' }}>recorded</span>
              <span><button type="button" className="rounded-md border px-2 py-1 text-[11px]" style={{ borderColor: 'var(--sem-border)' }}
                aria-label={`Open the run recorded on ${formatDate(record.when, true)}`}
                onClick={() => openOne(record.runId)}>{loadingId === record.runId ? 'Opening…' : 'Open'}</button></span>
            </div>
          ))}
      {openRun && <RecordedRun detail={openRun} onClose={() => setOpenRun(null)} />}
    </Card>
  );
}

function RecordedRun({ detail, onClose }: { detail: TestRunDetailW; onClose: () => void }) {
  const record = detail.run;
  const checks = record?.outcomes?.checks ?? [];
  const columns = 'minmax(0,1.4fr) 128px minmax(0,1fr) 168px';
  const kindWord = (kind: string) => kind === 'savedCheck' ? 'Saved check' : kind === 'relationship' ? 'Relationship' : 'Table row count';
  return (
    <div className="border-t px-3.5 py-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-3">
        <strong className="text-[12.5px]">{record ? formatDate(record.when) : 'That run'}</strong>
        <button type="button" className="ml-auto text-[12px]" style={{ color: 'var(--sem-muted)' }} onClick={onClose}>Close</button>
      </div>
      {detail.gradedBeforeSecurityLeftTests && <div className="mt-1.5 text-[11.5px]" style={{ color: 'var(--sem-warn)' }}>
        This run was scored the old way, before access rules left this page. Read its letter on its own, not against a newer one.
      </div>}
      {detail.note && <div className="mt-1.5 text-[11.5px]" style={{ color: 'var(--sem-warn)' }}>{detail.note}</div>}
      {record?.scope?.gradeCovers && <div className="mt-1.5 text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>
        This grade is for {record.scope.gradeCovers}.
      </div>}
      {checks.length === 0
        ? <div className="mt-1.5 text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>{record?.outcomes?.note ?? 'This run kept its grade and totals but not what each check found.'}</div>
        : <>
          <div className="mt-2 grid items-center border-b py-1 text-[10.5px] font-semibold uppercase tracking-[0.06em]"
            style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
            <span>Check</span><span>Kind</span><span>Expected · actual</span><span>Result</span>
          </div>
          {/* Every row opens to what the run actually kept: the comparisons it made, and the settings it was
              run with. Before this, an opened run showed four numbers and nothing else, and the engine's own
              snapshot did not even hold the query (Astra, finding 6). */}
          {checks.map((check, index) => (
            <RecordedCheckRow key={`${check.name}-${check.check ?? ''}-${index}`} check={check} columns={columns} kindWord={kindWord} />
          ))}
          {record?.outcomes?.note && <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{record.outcomes.note}</div>}
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
    <div className="grid items-center border-b py-1.5 text-[11.5px]"
      style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)' }} title={check.note}>
      <span className="truncate">{name}</span>
      <span style={{ color: 'var(--sem-muted)' }}>{kindWord(check.kind)}</span>
      <span className="tnum truncate">{check.expected ?? '\u00b7'} · <strong>{check.actual ?? '\u00b7'}</strong></span>
      <span className="flex items-center gap-2">
        <Chip chip={resultChip(true, check)} />
        {hasDetail && <button type="button" className="text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}
          aria-label={`${open ? 'Hide' : 'Open'} what ${name} found`}
          onClick={() => setOpen((v) => !v)}>{open ? 'Hide' : 'Open'}</button>}
      </span>
    </div>
    {open && (
      <div className="border-b px-3 py-2 text-[11px]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
        {check.note && <div className="mb-1.5" style={{ color: 'var(--sem-muted)' }}>{check.note}</div>}
        {settings.length > 0 && (
          <dl className="m-0 mb-2 grid gap-x-4 gap-y-1" style={{ gridTemplateColumns: 'max-content minmax(0,1fr)' }}>
            {settings.map(([label, value]) => <Fragment key={label}>
              <dt style={{ color: 'var(--sem-muted)' }}>{label}</dt>
              <dd className="m-0 break-words font-mono text-[10.5px]">{value}</dd>
            </Fragment>)}
          </dl>
        )}
        {contexts.length > 0 && <>
          <div className="mb-1" style={{ color: 'var(--sem-muted)' }}>What it compared, as it was saved with this run.</div>
          <div className="overflow-hidden rounded-md border" role="table" aria-label={`Comparisons kept with ${name}`}
            style={{ borderColor: 'var(--sem-border)' }}>
            <div role="row" className="grid border-b px-2 py-1 text-[10px] font-semibold uppercase tracking-[0.05em]"
              style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
              <span role="columnheader">Where</span><span role="columnheader">Expected</span>
              <span role="columnheader">The model</span><span role="columnheader">Difference</span>
            </div>
            {contexts.map((context, index) => (
              <div role="row" key={`${context.context}-${index}`} className="grid border-b px-2 py-1 last:border-0"
                title={context.note} style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr', borderColor: 'var(--sem-border)' }}>
                <span role="cell" className={context.grandTotal ? 'truncate font-semibold' : 'truncate'}>{context.context}</span>
                <span role="cell" className="tnum">{resultText(context.expected) ?? '\u00b7'}</span>
                <span role="cell" className="tnum">{resultText(context.actual) ?? '\u00b7'}</span>
                <span role="cell" className="tnum">{resultText(context.difference, true) ?? '\u00b7'}</span>
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
export function EvidenceView() {
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

  return <div className="h-full overflow-auto">
    {openEvidence && <EvidenceArtifactDialog
      title={openEvidence.title || 'Saved evidence'}
      subtitle={`${openEvidence.kind === 'workflow-run' ? 'Workflow' : 'Tests'} · ${formatDate(openEvidence.createdUtc)} · saved with ${openEvidence.modelName || 'the current model'}`}
      baseName={`${(openEvidence.modelName || 'semanticus').replace(/[^\w.-]+/g, '_')}-${openEvidence.id}`}
      stateKey="tests.evidence.saved.format"
      load={() => rpc<EvidenceArtifactW>('getEvidence', openEvidence.id)}
      onClose={() => setOpenEvidence(null)} />}
    <main className="sem-centered-page w-full min-w-0 px-7 pt-6 pb-12">
      <header className="mb-4"><h1 className="m-0 text-[15px] font-semibold">Saved reports</h1><div className="mt-1 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Reports kept with this model. Each one says what was checked, when it ran and which model it describes.</div></header>
      {err && <div className="mb-3"><Banner color="var(--sem-bad)">{err}</Banner></div>}
      <EvidenceLibraryView library={library} onOpen={setOpenEvidence} />
    </main>
  </div>;
}

function ReportExportDialog({ modelName, onSaved, onClose }: { modelName?: string; onSaved?: () => void; onClose: () => void }) {
  const baseName = `${(modelName ?? 'semanticus').replace(/[^\w.-]+/g, '_')}-test-report`;
  return <EvidenceArtifactDialog
    title="Test evidence"
    subtitle={`Latest run for ${modelName ?? 'the current model'} · HTML and JSON are one sealed artifact; Markdown is the portable reading copy.`}
    baseName={baseName}
    stateKey="tests.report.format"
    load={() => rpc<TestReportResultW>('exportTestReport')}
    save={() => rpc<EvidenceSaveResultW>('saveEvidence', 'tests', null, 'human')}
    onSaved={onSaved}
    includeMarkdown
    onClose={onClose}
  />;
}

function EvidenceLibraryView({ library, onOpen }: { library: EvidenceLibraryW | null; onOpen: (item: EvidenceItemW) => void }) {
  const items = library?.items ?? [];
  if (!library) return <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>Loading the saved reports…</span></Card>;
  if (items.length === 0) return <Card className="p-4 text-[12px]">
    <strong>No saved reports yet.</strong>
    <p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>{library.note ?? 'Run the checks in Tests or a workflow, then use Report to keep one with this model.'}</p>
    {library.directoryPath && <div className="mt-2"><PathDisclosure label="Where these are kept" path={library.directoryPath} /></div>}
  </Card>;
  return <>
    {library.invalidCount > 0 && <div className="mb-3"><Banner color="var(--sem-warn)">{plural(library.invalidCount, 'saved report')} could not be read. Those are listed below and cannot be opened.</Banner></div>}
    <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fill,minmax(300px,1fr))' }}>
      {items.map((item) => (
        <Card key={item.id} className="flex flex-col gap-1.5 p-3.5 text-[12px]">
          <strong className="text-[12.5px]">{item.title || 'Saved report'}</strong>
          <span style={{ color: 'var(--sem-muted)' }}>{item.kind === 'workflow-run' ? 'Workflow run' : 'Tests run'} · {formatDate(item.createdUtc)}</span>
          <span style={{ color: 'var(--sem-muted)' }}>Saved with {item.modelName || 'the current model'}{item.producer ? ` by ${item.producer === 'agent' ? 'your assistant' : 'a person'}` : ''}</span>
          {item.verified != null && item.total != null && <span style={{ color: 'var(--sem-muted)' }}>{item.verified} of {item.total} checked{item.unknowns ? ` · ${item.unknowns} could not be checked` : ''}</span>}
          <span className="mt-1 flex items-center gap-2">
            <button type="button" disabled={!item.valid} className="rounded-md border px-2.5 py-1 text-[12px] disabled:opacity-40"
              style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }} onClick={() => onOpen(item)}>Open</button>
            {!item.valid && <span style={{ color: 'var(--sem-warn)' }}>{item.note ?? 'This report could not be read.'}</span>}
          </span>
        </Card>
      ))}
    </div>
    {library.directoryPath && <div className="mt-3"><PathDisclosure label="Where these are kept" path={library.directoryPath} /></div>}
  </>;
}
