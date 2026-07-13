import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react';
import { rpc, onActivity, onDidChange } from './bridge';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import { InterviewCard, type SuiteInterviewEvidence } from './interview';
import { uiLabel } from './copy';

// ===================================================================================================
// The Tests tab (the Prove intent, docs/tests-tab-spec.md). The three ratified invariants are LAYOUT
// decisions here, not just scoring rules:
//  I1  Not verifiable renders in a deliberately COLORLESS slate (never green, never red) and its copy
//      says why the check could not run. It never moves the grade.
//  I2  The grade and its coverage are ONE welded lockup: a single bordered unit, one flex row, so no
//      screenshot can crop the grade away from how much of the suite it actually rests on.
//  I3  Every section leads with ROOT CAUSES. Cascades render as Suspect rows that NAME their root cause
//      instead of counting as second failures.
// Wire shapes mirror Semanticus.Engine/LocalEngine.TestSuite.cs + Testing/* (camelCased).
// ===================================================================================================

interface CheckResultW { check: string; verdict: string; message?: string; rootCause?: string; count?: number }
interface RelationshipResultW {
  name: string; manyTable: string; manyColumn: string; oneTable: string; oneColumn: string;
  cardinality?: string; isActive: boolean; blankForeignKeys?: number; blankKeys?: number;
  manyRowCount?: number;
  dataTypeMatch: CheckResultW; keyUniqueness: CheckResultW; referentialIntegrity: CheckResultW;
}
interface RelationshipSuiteSummaryW { relationships: number; checked: number; passed: number; failed: number; suspect: number; notVerifiable: number; coveragePct: number }
interface TableRowCountW {
  modelTable: string; server?: string; database?: string; schema?: string; entity?: string;
  modelCount?: number; sourceCount?: number; modelObservedUtc?: string; sourceObservedUtc?: string;
  snapshotAligned: boolean; check: CheckResultW;
}
interface RelationshipReportW { relationships: RelationshipResultW[]; tableRowCounts?: TableRowCountW[]; summary: RelationshipSuiteSummaryW }
interface RoleFilterResultW { role: string; table: string; check: CheckResultW; filterPreview?: string }
interface RoleOlsW { role: string; tablesTotal: number; tablesHidden: number; columnsHidden: number; tiles: { table: string; hidden: boolean }[] }
interface SecurityReportW { filters: RoleFilterResultW[]; ols?: RoleOlsW[]; summary: { filters: number; passed: number; failed: number; notVerifiable: number; coveragePct: number } }
interface CompareRowW { context: string; sql?: number | null; dax?: number | null; delta?: number | null; verdict: string; explanation?: string; grandTotal?: boolean }
interface ReconcileOutcomeW {
  defId: string; title: string; targetRef?: string; verdict: string; message?: string; missing?: boolean;
  sql?: string; dax?: string; rows?: CompareRowW[]; rowsTotal?: number;
  matches?: number; mismatches?: number; unverifiable?: number;
  durationMs?: number; sqlDurationMs?: number; budgetMs?: number;
  timingVerdict?: string; timingDetail?: string;
  createdBy?: string; createdWhen?: string;
  toleranceNote?: string;
  // E6: TiVariantVerdict on the wire. `variant` is the variant MEASURE's name; `kind` is the identity
  // family; `explanation` names the numbers (or why a chip is not verifiable).
  variants?: { variant: string; kind?: string; verdict: string; explanation?: string }[];
}
interface CategoryHealthW { category: string; weight: number; hasChecks: boolean; checked: number; passed: number; failed: number; suspect: number; notVerifiable: number; score: number }
interface TestHealthW {
  overall: number; grade: string; gatedBy: string[]; coveragePct: number; checked: number;
  passed: number; failed: number; suspect: number; notVerifiable: number; missing: number; rootFailures: number;
  categories: CategoryHealthW[];
}
interface TestRunW {
  runId?: string; when?: string; modelName?: string; live?: boolean; health?: TestHealthW;
  relationships?: RelationshipReportW; security?: SecurityReportW; reconciles?: ReconcileOutcomeW[];
  definitionCount?: number; persisted?: boolean; note?: string; error?: string;
  durationMs?: number; environment?: string; cacheCleared?: boolean;
  interview?: SuiteInterviewEvidence[]; interviewNote?: string;
}
interface TestDefinitionW { id: string; kind: string; title: string; targetTag?: string; targetIdentity?: string; targetRef?: string; paramsJson?: string; enabled: boolean; createdBy?: string; createdWhen?: string; budgetMs?: number; bindingWarning?: string }
interface TestSuiteInfoW { definitions: TestDefinitionW[]; unreadableLines: number; note?: string }
interface TestRunRecordW { runId: string; when: string; live: boolean; health?: TestHealthW }
interface TestHistoryW { runs: TestRunRecordW[]; note?: string }
interface TestReportResultW { markdown?: string; html?: string; json?: string; contentHash?: string; note?: string; error?: string }
interface EvidenceItemW {
  id: string; kind?: string; title?: string; createdUtc?: string; producer?: string; modelName?: string;
  verdict?: string; verified?: number; total?: number; unknowns?: number; contentHash?: string;
  valid: boolean; note?: string; jsonPath?: string; htmlPath?: string; updatedUtc?: string;
}
interface EvidenceLibraryW { modelName?: string; directoryPath?: string; items: EvidenceItemW[]; invalidCount: number; note?: string }
interface ReconcileParamsW {
  measureRef?: string; groupBy?: string[]; sql?: string; sqlGrandTotal?: string;
  toleranceAbsolute?: number; toleranceRelative?: number; blankPolicy?: string; maxRows?: number;
  server?: string; database?: string; authMode?: string; tenantId?: string;
}
interface ReconcileSourceCandidateW { modelTable: string; server?: string; database?: string; schema?: string; entity?: string; relevant: boolean }
interface ReconcileMappingReviewW {
  measureRef?: string; measureName?: string; detectedServer?: string; detectedDatabase?: string;
  effectiveServer?: string; effectiveDatabase?: string; ambiguous: boolean; sources: ReconcileSourceCandidateW[];
  tested: boolean; connected: boolean; elapsedMs?: number; testError?: string; note?: string; error?: string; suggestedNextAction?: string;
}

type VerdictKey = 'Pass' | 'Fail' | 'Suspect' | 'NotVerifiable';
type SubTab = 'measures' | 'relationships' | 'security' | 'history';

const VERDICT: Record<VerdictKey, { label: string; glyph: string; color: string }> = {
  Pass: { label: 'Pass', glyph: '✓', color: 'var(--sem-good)' },
  Fail: { label: 'Fail', glyph: '✗', color: 'var(--sem-bad)' },
  Suspect: { label: 'Suspect', glyph: '▲', color: 'var(--sem-warn)' },
  NotVerifiable: { label: 'Not verifiable', glyph: '○', color: 'var(--sem-nv)' },
};
const GRADE_COLOR: Record<string, string> = {
  A: 'var(--sem-good)', B: 'var(--sem-good)', C: 'var(--sem-warn)', D: 'var(--sem-warn)', F: 'var(--sem-bad)',
};
const CHECK_LABEL: Record<string, string> = {
  DataTypeMatch: 'Types', KeyUniqueness: 'Key uniqueness', ReferentialIntegrity: 'References', StaticFilter: 'Role filter',
  Timing: 'Timing', Reconciliation: 'Reconciliation', MeasureReconciliation: 'Reconciliation', TableRowCount: 'Table row count',
};
const checkLabel = (id?: string) => (id && CHECK_LABEL[id]) || 'Check';
const verdictKey = (value?: string, missing = false): VerdictKey => {
  if (missing) return 'NotVerifiable';
  if (value === 'Pass' || value === 'Fail' || value === 'Suspect' || value === 'NotVerifiable') return value;
  if (value === 'Not verifiable') return 'NotVerifiable';
  return 'NotVerifiable';
};
const comparisonVerdict = (value: string): VerdictKey => value === 'Match' ? 'Pass' : value === 'Mismatch' ? 'Fail' : verdictKey(value);
const verdictRank: Record<VerdictKey, number> = { Fail: 0, Suspect: 1, NotVerifiable: 2, Pass: 3 };
const worstVerdict = (values: string[]): VerdictKey => values.map((v) => verdictKey(v)).sort((a, b) => verdictRank[a] - verdictRank[b])[0] ?? 'NotVerifiable';
const plural = (n: number, singular: string, pluralForm = `${singular}s`) => `${n.toLocaleString()} ${n === 1 ? singular : pluralForm}`;
// Cardinality arrives as the engine's camelCase token; the UI speaks plain words.
const CARDINALITY: Record<string, string> = { manyToOne: 'Many to one', oneToMany: 'One to many', manyToMany: 'Many to many', oneToOne: 'One to one' };
// Unmapped values fall to the neutral dot, never the raw engine token: the UI does not speak engine.
const cardinalityLabel = (value?: string) => (value && CARDINALITY[value]) || '·';
// Time-intelligence variant chips: the short family label; an unrecognized pattern falls back to the
// variant measure's own name (truncated by the chip) so the chip never prints an engine token.
const TI_KIND: Record<string, string> = { Ytd: 'YTD', Qtd: 'QTD', Mtd: 'MTD', PriorYear: 'PY', YearOverYearDelta: 'YoY' };
const tiKindLabel = (kind: string | undefined, variantName: string) => (kind && TI_KIND[kind]) || variantName;
const formatDuration = (ms?: number) => ms == null ? '·' : ms >= 60000 ? `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s` : `${ms.toLocaleString()} ms`;
const formatDate = (value?: string, dateOnly = false) => {
  if (!value) return '·';
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return '·';
  return dateOnly ? date.toLocaleDateString() : date.toLocaleString();
};
// Integers render plain; anything fractional keeps exactly two decimals so a column of money never
// drops its trailing cents (18,185,050.80, not 18,185,050.8) and deltas line up with their values.
// EXCEPT values that would round to a lying 0.00: a 1e-9-tolerance reconcile can fail on a 0.001 delta,
// and the numbers on screen must never contradict the verdict beside them, so tiny non-zeros go scientific.
const resultText = (value: number | null | undefined, delta = false) => {
  if (value == null) return null;
  if (value !== 0 && Math.abs(value) < 0.005) return value.toExponential(2);
  return value.toLocaleString(undefined, delta || !Number.isInteger(value) ? { minimumFractionDigits: 2, maximumFractionDigits: 2 } : undefined);
};

function Card({ children, className = '', style }: { children: ReactNode; className?: string; style?: CSSProperties }) {
  return <div className={`rounded-[9px] border ${className}`} style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)', ...style }}>{children}</div>;
}

function Eyebrow({ children }: { children: ReactNode }) {
  return <div className="text-[10px] font-semibold uppercase tracking-[0.07em]" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}

function Banner({ children, color }: { children: ReactNode; color: string }) {
  return <div className="rounded-md border px-3 py-2 text-[12px]" style={{ color, borderColor: color, background: 'var(--sem-surface)' }}>{children}</div>;
}

function VerdictPill({ verdict, title }: { verdict: string; title?: string }) {
  const key = verdictKey(verdict);
  const item = VERDICT[key];
  return (
    <span className="inline-flex w-max items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase leading-[18px] whitespace-nowrap"
      title={title} style={{ color: item.color, borderColor: key === 'NotVerifiable' ? 'var(--sem-border)' : item.color }}>
      <span aria-hidden>{item.glyph}</span>{item.label}
    </span>
  );
}

function Legend() {
  return (
    <div className="flex flex-wrap items-center gap-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
      {(Object.keys(VERDICT) as VerdictKey[]).map((key) => <span key={key} className="whitespace-nowrap"><i aria-hidden className="mr-1 inline-block size-1.5 rounded-full" style={{ background: VERDICT[key].color }} />{VERDICT[key].label}</span>)}
    </div>
  );
}

function SectionIntro({ title, sub, legend = false, action }: { title: string; sub: string; legend?: boolean; action?: ReactNode }) {
  return <div className="mb-3 flex items-end justify-between gap-4"><div><h2 className="m-0 text-[13px] font-semibold">{title}</h2><div className="mt-0.5 text-[12px]" style={{ color: 'var(--sem-muted)' }}>{sub}</div></div><div className="flex items-center gap-3">{legend && <Legend />}{action}</div></div>;
}

function RootBand({ count, title, body, action, onAction }: { count: number; title: string; body?: string; action?: string; onAction?: () => void }) {
  return (
    <div className="mb-3 grid min-h-[78px] overflow-hidden rounded-[9px] border" style={{ gridTemplateColumns: '4px minmax(0,1fr) auto', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div style={{ background: 'var(--sem-bad)' }} />
      <div className="flex items-center gap-4 px-4 py-3"><div className="tnum min-w-5 text-[22px] font-semibold" style={{ color: 'var(--sem-bad)' }}>{count}</div><div><strong className="text-[13px]">{title}</strong>{body && <p className="m-0 mt-1 text-[12px]" style={{ color: 'var(--sem-muted)' }}>{body}</p>}</div></div>
      {action && <button className="m-3 self-center rounded-md border px-3 py-1.5 text-[12px]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' }} onClick={onAction}>{action}</button>}
    </div>
  );
}

export function TestsView() {
  const [run, setRun] = useState<TestRunW | null>(null);
  const [suite, setSuite] = useState<TestSuiteInfoW | null>(null);
  const [history, setHistory] = useState<TestHistoryW | null>(null);
  const [busy, setBusy] = useState(false);
  const [reportOpen, setReportOpen] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [sub, setSub] = useState<SubTab>('measures');
  // The run survives tab switches, so a model edit can outdate the visible grade. Any change marks it
  // stale; only a fresh run clears it. Over-marking is honest; a stale grade shown as current is not.
  const [stale, setStale] = useState(false);
  useEffect(() => onDidChange(() => setStale(true)), []);

  const loadSuite = () => rpc<TestSuiteInfoW>('listTests').then(setSuite).catch(() => undefined);
  useEffect(() => { void loadSuite(); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (sub === 'history' && !history) rpc<TestHistoryW>('listTestRuns', 20).then(setHistory).catch(() => undefined);
  }, [sub, history]);

  const runSuite = (persist: boolean) => {
    setBusy(true);
    setErr(null);
    rpc<TestRunW>('runTests', persist)
      .then((next) => { setRun(next); setStale(false); if (next.error) setErr(next.error); })
      .catch((error: unknown) => setErr(error instanceof Error ? error.message : String(error)))
      .finally(() => setBusy(false));
  };

  const tabCounts: Record<SubTab, number> = {
    measures: run?.reconciles?.length ?? 0,
    relationships: run?.relationships?.summary.relationships ?? 0,
    // The security count is the number of ROLE CARDS rendered (the union), never filters + ols summed:
    // a role with both would count twice and the tab count would not reconcile with the list below.
    security: run?.security ? new Set([...run.security.filters.map((f) => f.role), ...(run.security.ols ?? []).map((o) => o.role)]).size : 0,
    history: history?.runs.length ?? 0,
  };

  return (
    <div className="h-full overflow-auto">
      {reportOpen && <ReportExportDialog modelName={run?.modelName} onClose={() => setReportOpen(false)} />}
      <main className="sem-centered-page w-full min-w-0 px-7 pt-6 pb-12">
        <header className="mb-3.5 flex flex-wrap items-end justify-between gap-4">
          <div className="min-w-[240px] flex-1"><h1 className="m-0 text-[15px] font-semibold">Tests</h1><div className="mt-1 truncate text-[12px]" style={{ color: 'var(--sem-muted)' }}>{run?.live && <i aria-hidden className="mr-2 inline-block size-1.5 rounded-full" style={{ background: 'var(--sem-good)' }} />}{run?.environment ?? 'Prove the model with data: probe relationships, check role filters and tie saved measures back to accepted source SQL.'}</div></div>
          <div className="flex flex-wrap gap-2">
            <button className="min-h-7 rounded-md border px-3 py-1 text-[12px] font-semibold disabled:opacity-50" disabled={busy} onClick={() => runSuite(false)} style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busy ? 'Running…' : 'Run tests'}</button>
            <button className="min-h-7 rounded-md border px-3 py-1 text-[12px] disabled:opacity-50" disabled={busy} onClick={() => runSuite(true)} title="Record this run in test history" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>Run + record <span style={{ color: 'var(--sem-muted)' }}>Pro</span></button>
            <button className="min-h-7 rounded-md border px-3 py-1 text-[12px] disabled:opacity-60" disabled={!run || busy} onClick={() => setReportOpen(true)} title="Preview, print or export the latest current-model report" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>Report… <span style={{ color: 'var(--sem-muted)' }}>Pro</span></button>
          </div>
        </header>

        {err && <div className="mb-3"><Banner color="var(--sem-bad)">{err}</Banner></div>}
        {run?.note && <div className="mb-3"><Banner color="var(--sem-warn)">{run.note}</Banner></div>}
        {run?.health && stale && <div className="mb-3"><Banner color="var(--sem-warn)">The model changed after this run. Everything below describes the earlier state; run tests again for current evidence.</Banner></div>}
        {run?.health && <OverviewBand run={run} />}
        <div className="mb-3.5"><InterviewCard suiteEvidence={run?.interview} suiteNote={run?.interviewNote} /></div>

        <nav className="mb-3.5 flex h-[38px] items-end gap-6 border-b" aria-label="Test sections" style={{ borderColor: 'var(--sem-border)' }}>
          {(Object.keys(tabCounts) as SubTab[]).map((tab) => <button key={tab} className="h-[38px] border-0 border-b-2 bg-transparent px-0.5 text-[12px] capitalize" onClick={() => setSub(tab)} style={{ color: sub === tab ? 'var(--sem-accent)' : 'var(--sem-muted)', borderBottomColor: sub === tab ? 'var(--sem-accent)' : 'transparent' }}>{tab}{/* history's count is unknown until its lazy load lands: no count beats a false 0 */}{(tab !== 'history' || history != null) && <span className="tnum ml-1 text-[11px]">{tabCounts[tab]}</span>}</button>)}
        </nav>

        {!run && !busy && sub !== 'measures' && <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>No run yet. Run the suite to probe every relationship, check every role filter and execute saved reconciliations. {suite?.definitions.length ? `${plural(suite.definitions.length, 'saved test')} will run too.` : 'No saved tests yet: ambient checks still cover relationships and security.'}</span></Card>}
        {sub === 'measures' && <Measures run={run} suite={suite} onSuiteChanged={loadSuite} />}
        {run && sub === 'relationships' && <Relationships report={run.relationships} />}
        {run && sub === 'security' && <Security report={run.security} />}
        {sub === 'history' && <History history={history} />}
      </main>
    </div>
  );
}

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
      <header className="mb-4"><h1 className="m-0 text-[15px] font-semibold">Evidence</h1><div className="mt-1 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Review the sealed Test and Workflow reports saved with this model.</div></header>
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

function OverviewBand({ run }: { run: TestRunW }) {
  const h = run.health;
  if (!h) return null;
  const relCount = run.relationships?.summary.relationships;
  const roleCount = run.security ? new Set([...(run.security.filters.map((f) => f.role)), ...((run.security.ols ?? []).map((o) => o.role))]).size : undefined;
  return (
    <section className="mb-3.5 grid gap-3.5" aria-label="Test overview" style={{ gridTemplateColumns: 'minmax(230px,250px) minmax(0,1fr)' }}>
      <Card className="flex min-h-[160px] flex-col overflow-hidden p-4">
        <Eyebrow>Health grade + coverage</Eyebrow>
        <div className="my-auto flex items-stretch overflow-hidden rounded-md border" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="flex items-center px-4 text-[38px] font-bold" style={{ color: GRADE_COLOR[h.grade] ?? 'var(--sem-nv)' }}>{h.grade}</div>
          <div className="flex flex-col justify-center border-l px-4" style={{ borderColor: 'var(--sem-border)' }}><strong className="tnum text-[18px]">{h.coveragePct}%</strong><span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>verified coverage</span></div>
        </div>
        {h.gatedBy.length > 0 && <div className="mb-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>Grade capped: {h.gatedBy.join(', ')}</div>}
        <div className="h-1 overflow-hidden rounded-sm" style={{ background: 'var(--sem-surface-2)' }}><i className="block h-full" style={{ width: `${Math.max(0, Math.min(100, h.coveragePct))}%`, background: 'var(--sem-accent)' }} /></div>
        <div className="tnum mt-2 text-[10px]" style={{ color: 'var(--sem-muted)' }}>{h.passed + h.failed} of {h.checked} checks reached a verdict</div>
      </Card>
      <Card className="grid overflow-hidden [&>*:nth-child(3n)]:border-r-0 [&>*:nth-child(n+4)]:border-b-0" style={{ gridTemplateColumns: 'repeat(3,minmax(0,1fr))' }}>
        <Kpi title="Verdicts"><div className="tnum flex gap-3 text-[16px] font-semibold"><span style={{ color: 'var(--sem-good)' }}>{h.passed}</span><span style={{ color: 'var(--sem-bad)' }}>{h.failed}</span><span style={{ color: 'var(--sem-warn)' }}>{h.suspect}</span></div><small>pass, fail, suspect</small></Kpi>
        <Kpi title="Root causes"><div className="tnum text-[22px] font-semibold" style={{ color: h.rootFailures > 0 ? 'var(--sem-bad)' : 'var(--sem-good)' }}>{h.rootFailures}</div><small>{h.failed + h.suspect} observations explained</small></Kpi>
        <Kpi title="Not verifiable"><div className="flex flex-wrap items-center gap-2"><strong className="tnum text-[22px]" style={{ color: 'var(--sem-nv)' }}>{h.notVerifiable}</strong>{h.missing > 0 && <span className="rounded border px-1.5 py-0.5 text-[10px]" style={{ color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }}>{plural(h.missing, 'missing target')}</span>}</div><small>excluded from the grade</small></Kpi>
        <Kpi title="Last run"><div className="tnum text-[15px] font-semibold">{formatDuration(run.durationMs)}</div><small>{formatDate(run.when)}</small></Kpi>
        <Kpi title="Environment"><div className="truncate text-[13px] font-semibold" title={run.environment}>{run.environment ?? (run.live ? 'Live' : 'Offline')}</div>{run.cacheCleared && <small>cache cleared for timing</small>}</Kpi>
        <Kpi title="Suite"><div className="tnum text-[15px] font-semibold">{run.definitionCount == null ? '·' : plural(run.definitionCount, 'saved test')}</div><small>{relCount == null ? '· relationships' : plural(relCount, 'relationship')}{' · '}{roleCount == null ? '· roles' : plural(roleCount, 'role')}</small></Kpi>
      </Card>
    </section>
  );
}

function Kpi({ title, children }: { title: string; children: ReactNode }) {
  return <div className="flex min-h-20 flex-col justify-center border-r border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)' }}><Eyebrow>{title}</Eyebrow><div className="mt-1">{children}</div></div>;
}

function Measures({ run, suite, onSuiteChanged }: { run: TestRunW | null; suite: TestSuiteInfoW | null; onSuiteChanged: () => void }) {
  const outcomes = run?.reconciles ?? [];
  const mappingDefs = (suite?.definitions ?? []).filter((d) => d.kind === 'measureReconcile');
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<'All' | VerdictKey>('All');
  const [limit, setLimit] = useState(60);
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const [mappingOpen, setMappingOpen] = useState(false);
  const rowRefs = useRef(new Map<string, HTMLDivElement>());
  const hasVariants = outcomes.some((o) => (o.variants?.length ?? 0) > 0);
  const counts = useMemo(() => {
    const next: Record<VerdictKey, number> = { Pass: 0, Fail: 0, Suspect: 0, NotVerifiable: 0 };
    outcomes.forEach((o) => { next[verdictKey(o.verdict, o.missing)] += 1; });
    return next;
  }, [outcomes]);
  const filtered = useMemo(() => outcomes
    .filter((o) => `${o.title} ${o.targetRef ?? ''}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()))
    .filter((o) => filter === 'All' || verdictKey(o.verdict, o.missing) === filter)
    .sort((a, b) => verdictRank[verdictKey(a.verdict, a.missing)] - verdictRank[verdictKey(b.verdict, b.missing)] || a.title.localeCompare(b.title)), [outcomes, query, filter]);
  const failing = outcomes.filter((o) => verdictKey(o.verdict, o.missing) === 'Fail')
    .sort((a, b) => a.title.localeCompare(b.title));
  const visible = filtered.slice(0, limit);
  const toggle = (id: string) => setOpen((current) => { const next = new Set(current); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  const inspect = () => {
    const first = failing[0];
    if (!first) return;
    setQuery('');
    setFilter('All');
    const sortedIndex = [...outcomes].sort((a, b) => verdictRank[verdictKey(a.verdict, a.missing)] - verdictRank[verdictKey(b.verdict, b.missing)] || a.title.localeCompare(b.title)).findIndex((outcome) => outcome.defId === first.defId);
    setLimit(Math.max(60, sortedIndex + 1));
    setOpen((current) => new Set(current).add(first.defId));
    window.requestAnimationFrame(() => window.requestAnimationFrame(() => rowRefs.current.get(first.defId)?.scrollIntoView({ behavior: 'smooth', block: 'center' })));
  };
  const columns = hasVariants ? '5px minmax(220px,1.5fr) 118px 150px 125px 100px 30px' : '5px minmax(220px,1.5fr) 118px 150px 125px 30px';

  return <section><SectionIntro title="Measures" sub="Reconcile model measures to human-accepted source SQL, with context-by-context evidence." legend action={
    <button onClick={() => setMappingOpen((value) => !value)} className="rounded-md border px-2.5 py-1.5 text-[11px] font-semibold"
      style={{ background: mappingOpen ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)', borderColor: mappingOpen ? 'var(--sem-accent)' : 'var(--sem-border)', color: mappingOpen ? 'var(--sem-accent)' : 'var(--sem-fg)' }}>
      SQL mappings <span className="tnum">{mappingDefs.length}</span>
    </button>} />
    {mappingOpen && <SqlMappingReview definitions={mappingDefs} onClose={() => setMappingOpen(false)} onSaved={onSuiteChanged} />}
    {failing.length > 0 && <RootBand count={failing.length} title={failing.length === 1 ? `Root cause in ${failing[0]?.title ?? 'a measure'}` : `Root causes in ${failing.length} measures, worst in ${failing[0]?.title ?? 'a measure'}`} body={failing[0]?.message ?? 'The model result differs from the accepted source result.'} action="Inspect evidence" onAction={inspect} />}
    <div className="mb-2.5 flex flex-wrap items-center gap-2">
      <input aria-label="Search measures" value={query} onChange={(event) => { setQuery(event.target.value); setLimit(60); }} placeholder="Search measures" className="min-h-7 min-w-[210px] rounded-md border px-2.5 text-[12px] outline-none" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' }} />
      {(['All', 'Fail', 'Suspect', 'NotVerifiable', 'Pass'] as const).map((key) => <button key={key} onClick={() => { setFilter(key); setLimit(60); }} className="rounded-full border px-2.5 py-1 text-[11px]" style={{ background: filter === key ? 'var(--sem-accent-soft)' : 'var(--sem-surface)', borderColor: filter === key ? 'var(--sem-accent)' : 'var(--sem-border)', color: key === 'All' ? 'var(--sem-fg)' : VERDICT[key].color }}>{key === 'NotVerifiable' ? 'Not verifiable' : key} <span className="tnum">{key === 'All' ? outcomes.length : counts[key]}</span></button>)}
    </div>
    {run == null || outcomes.length === 0 ? <Card className="p-4 text-[12px]" ><span style={{ color: 'var(--sem-muted)' }}>{run == null
      ? `No run yet. Run the suite to see reconciliation evidence. ${mappingDefs.length ? `${plural(mappingDefs.length, 'saved SQL mapping')} will run.` : 'Ask the AI Assistant to draft source SQL, review it, then accept and save the test.'}`
      : 'No reconciliation tests yet. Ask the AI Assistant to draft source SQL from lineage, review it, then accept and save the test. The SQL only counts once a person accepts it.'}</span></Card> :
      <Card className="overflow-hidden">
        <div className="grid h-[34px] items-center border-b text-[10px] font-semibold uppercase tracking-[0.06em]" style={{ gridTemplateColumns: columns, background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}><span /><span className="px-3">Measure</span><span className="px-3">Verdict</span><span className="px-3">Contexts</span><span className="px-3">Timing</span>{hasVariants && <span className="px-3">Variants</span>}<span /></div>
        {visible.map((outcome) => <MeasureItem key={outcome.defId} outcome={outcome} run={run} hasVariants={hasVariants} columns={columns} isOpen={open.has(outcome.defId)} toggle={() => toggle(outcome.defId)} rowRef={(node) => { if (node) rowRefs.current.set(outcome.defId, node); else rowRefs.current.delete(outcome.defId); }} />)}
        {filtered.length === 0 && <div className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No measures match these filters.</div>}
        {visible.length < filtered.length && <button className="w-full border-0 px-3 py-3 text-[12px]" onClick={() => setLimit((value) => value + 200)} style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-accent)' }}>Show {(filtered.length - visible.length).toLocaleString()} more</button>}
      </Card>}
  </section>;
}

function parseReconcileParams(def?: TestDefinitionW): ReconcileParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as ReconcileParamsW; } catch { return {}; }
}

function SqlMappingReview({ definitions, onClose, onSaved }: { definitions: TestDefinitionW[]; onClose: () => void; onSaved: () => void }) {
  const [selectedId, setSelectedId] = useState(definitions[0]?.id ?? '');
  const selected = definitions.find((d) => d.id === selectedId) ?? definitions[0];
  const params = useMemo(() => parseReconcileParams(selected), [selected]);
  const [review, setReview] = useState<ReconcileMappingReviewW | null>(null);
  const [server, setServer] = useState('');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState('azcli');
  const [tenantId, setTenantId] = useState('');
  const [busy, setBusy] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [savedWarning, setSavedWarning] = useState(false);

  useEffect(() => {
    if (!selected) { setReview(null); return; }
    const next = parseReconcileParams(selected);
    setServer(next.server ?? ''); setDatabase(next.database ?? ''); setAuthMode(next.authMode || 'azcli'); setTenantId(next.tenantId ?? '');
    setError(null); setSaved(null); setSavedWarning(false); setBusy(true); setReview(null);
    let cancelled = false;
    rpc<ReconcileMappingReviewW>('reviewReconcileMapping', {
      measureRef: selected.targetRef || next.measureRef, server: next.server || null, database: next.database || null,
      authMode: next.authMode || 'azcli', tenantId: next.tenantId || null, testConnection: false,
    }).then((result) => { if (!cancelled) { setReview(result); if (result.error) setError(result.error); } })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setBusy(false); });
    return () => { cancelled = true; };
  }, [selected]);

  const testConnection = () => {
    if (!selected) return;
    setBusy(true); setError(null); setSaved(null); setSavedWarning(false);
    rpc<ReconcileMappingReviewW>('reviewReconcileMapping', {
      measureRef: selected.targetRef || params.measureRef, server: server.trim() || null, database: database.trim() || null,
      authMode, tenantId: tenantId.trim() || null, testConnection: true,
    }).then((result) => { setReview(result); if (result.error) setError(result.error); })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };

  const saveOverride = () => {
    if (!selected) return;
    const next: ReconcileParamsW = { ...params };
    if (server.trim()) next.server = server.trim(); else delete next.server;
    if (database.trim()) next.database = database.trim(); else delete next.database;
    if (authMode && authMode !== 'azcli') next.authMode = authMode; else delete next.authMode;
    if (tenantId.trim()) next.tenantId = tenantId.trim(); else delete next.tenantId;
    setSaving(true); setError(null); setSaved(null); setSavedWarning(false);
    rpc<TestDefinitionW>('saveTest', { ...selected, paramsJson: JSON.stringify(next) })
      .then((stored) => { setSavedWarning(Boolean(stored.bindingWarning)); setSaved(stored.bindingWarning ?? 'Mapping override saved. Run tests to use it.'); onSaved(); })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setSaving(false));
  };

  const inp: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)' };
  if (definitions.length === 0) return <Card className="mb-3 p-4 text-[12px]"><div className="flex items-start justify-between gap-4"><div><strong>No saved SQL mappings</strong><p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>Ask the AI Assistant to draft independent source SQL, review it, then accept it. This page edits connectivity only; it never invents ground truth.</p></div><button onClick={onClose} className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Close</button></div></Card>;

  return <Card className="mb-3 overflow-hidden">
    <div className="flex items-start justify-between gap-4 border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
      <div><strong className="text-[13px]">Source SQL mappings</strong><p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Review detected Fabric coordinates, override endpoint/database when needed, and test identity access without running the accepted SQL.</p></div>
      <button onClick={onClose} className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Close</button>
    </div>
    <div className="grid min-h-[330px]" style={{ gridTemplateColumns: '260px minmax(0,1fr)' }}>
      <div className="border-r p-2" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
        {definitions.map((def) => <button key={def.id} onClick={() => setSelectedId(def.id)} className="mb-1 w-full rounded-md border px-3 py-2 text-left"
          style={{ background: def.id === selected?.id ? 'var(--sem-accent-soft)' : 'var(--sem-surface)', borderColor: def.id === selected?.id ? 'var(--sem-accent)' : 'var(--sem-border)' }}>
          <strong className="block truncate text-[11px]">{def.title}</strong><span className="mt-0.5 block truncate text-[10px]" style={{ color: 'var(--sem-muted)' }}>{def.targetRef ?? 'Missing measure binding'}</span>
        </button>)}
      </div>
      <div className="min-w-0 p-4">
        <div className="mb-3 flex flex-wrap items-start justify-between gap-3"><div><Eyebrow>Measure</Eyebrow><strong className="mt-1 block text-[13px]">{review?.measureName ?? selected?.targetRef ?? '·'}</strong></div>{review?.ambiguous && <span className="rounded border px-2 py-1 text-[10px] font-semibold" style={{ color: 'var(--sem-warn)', borderColor: 'var(--sem-warn)' }}>Multiple SQL sources</span>}</div>
        {error && <div className="mb-3"><Banner color="var(--sem-bad)">{error}</Banner></div>}
        {saved && <div className="mb-3"><Banner color={savedWarning ? 'var(--sem-warn)' : 'var(--sem-good)'}>{saved}</Banner></div>}
        {review?.note && <div className="mb-3 text-[11px]" style={{ color: review.ambiguous ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{review.note}</div>}
        <div className="mb-3 grid gap-3" style={{ gridTemplateColumns: 'minmax(0,2fr) minmax(160px,1fr)' }}>
          <label className="text-[10px] font-semibold uppercase tracking-[0.05em]" style={{ color: 'var(--sem-muted)' }}>SQL endpoint
            <input value={server} onChange={(e) => setServer(e.target.value)} placeholder={review?.detectedServer || 'Required'} className="mt-1 min-h-8 w-full rounded-md border px-2.5 text-[12px] normal-case tracking-normal outline-none" style={inp} />
            <span className="mt-1 block text-[10px] font-normal normal-case tracking-normal">{server.trim() ? 'Override' : review?.detectedServer ? 'Using detected endpoint' : 'No endpoint detected'}</span>
          </label>
          <label className="text-[10px] font-semibold uppercase tracking-[0.05em]" style={{ color: 'var(--sem-muted)' }}>Database
            <input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder={review?.detectedDatabase || 'Required'} className="mt-1 min-h-8 w-full rounded-md border px-2.5 text-[12px] normal-case tracking-normal outline-none" style={inp} />
            <span className="mt-1 block text-[10px] font-normal normal-case tracking-normal">{database.trim() ? 'Override' : review?.detectedDatabase ? 'Using detected database' : 'No database detected'}</span>
          </label>
        </div>
        <div className="mb-3 flex flex-wrap gap-2">{(review?.sources ?? []).map((source, index) => <span key={`${source.modelTable}-${index}`} className="rounded border px-2 py-1 text-[10px]" title={`${source.server ?? 'endpoint unknown'} / ${source.database ?? 'database unknown'}`}
          style={{ borderColor: source.relevant ? 'var(--sem-accent)' : 'var(--sem-border)', color: source.relevant ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>
          {source.modelTable}: {source.schema ? `${source.schema}.` : ''}{source.entity ?? 'entity unknown'}{source.relevant ? ' · measure source' : ''}
        </span>)}</div>
        <div className="mb-3 grid gap-3" style={{ gridTemplateColumns: '180px minmax(180px,1fr)' }}>
          <label className="text-[10px] font-semibold uppercase tracking-[0.05em]" style={{ color: 'var(--sem-muted)' }}>Authentication
            <select value={authMode} onChange={(e) => setAuthMode(e.target.value)} className="mt-1 min-h-8 w-full rounded-md border px-2 text-[12px] normal-case tracking-normal" style={inp}><option value="azcli">Azure CLI</option><option value="interactive">Entra interactive</option><option value="devicecode">Device code</option><option value="serviceprincipal">Service principal</option></select>
          </label>
          <label className="text-[10px] font-semibold uppercase tracking-[0.05em]" style={{ color: 'var(--sem-muted)' }}>Tenant (optional)
            <input value={tenantId} onChange={(e) => setTenantId(e.target.value)} disabled={authMode === 'azcli'} placeholder={authMode === 'azcli' ? 'Uses the Azure CLI tenant' : 'Tenant id or domain'} className="mt-1 min-h-8 w-full rounded-md border px-2.5 text-[12px] normal-case tracking-normal outline-none disabled:opacity-50" style={inp} />
          </label>
        </div>
        <div className="mb-3"><QueryBlock title="Human-accepted source SQL (read-only)" text={params.sql} /></div>
        <div className="mb-3 text-[10px]" style={{ color: 'var(--sem-muted)' }}>Schema and entity come from model partitions and are shown for review. The accepted SQL owns its table references; changing those requires a newly reviewed SQL draft.</div>
        {review?.tested && <div className="mb-3"><Banner color={review.connected ? 'var(--sem-good)' : 'var(--sem-bad)'}>{review.connected ? `Connected in ${(review.elapsedMs ?? 0).toLocaleString()} ms. Identity and endpoint verified; the accepted SQL was not run.` : review.testError ?? 'Connection test failed.'}</Banner></div>}
        <div className="flex flex-wrap items-center gap-2"><button onClick={testConnection} disabled={busy || saving} className="rounded-md border px-3 py-1.5 text-[12px] font-semibold disabled:opacity-50" style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busy ? 'Testing…' : 'Test connection'}</button>
          <button onClick={saveOverride} disabled={busy || saving} className="rounded-md border px-3 py-1.5 text-[12px] disabled:opacity-50" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{saving ? 'Saving…' : 'Save mapping override'} <span style={{ color: 'var(--sem-muted)' }}>Pro</span></button>
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Blank fields keep auto-detection.</span></div>
      </div>
    </div>
  </Card>;
}

function MeasureItem({ outcome, run, hasVariants, columns, isOpen, toggle, rowRef }: { outcome: ReconcileOutcomeW; run: TestRunW; hasVariants: boolean; columns: string; isOpen: boolean; toggle: () => void; rowRef: (node: HTMLDivElement | null) => void }) {
  const key = verdictKey(outcome.verdict, outcome.missing);
  // SLOW is the ENGINE's judgement only (clear-cache run vs the declared budget). Comparing durationMs to
  // the budget here would judge a warm query time against a cold budget whenever the timing pass could not
  // run, minting a false SLOW on a not-verifiable measurement.
  const slow = outcome.timingVerdict === 'Fail';
  const timedAgainstBudget = outcome.timingVerdict != null && outcome.timingVerdict !== 'NotVerifiable';
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggle(); } };
  return <><div ref={rowRef} role="button" tabIndex={0} aria-expanded={isOpen} onClick={toggle} onKeyDown={onKeyDown} className="grid min-h-14 cursor-pointer items-center border-b" style={{ gridTemplateColumns: columns, borderColor: 'var(--sem-border)', background: isOpen ? 'var(--sem-surface-2)' : 'var(--sem-surface)' }}>
    <div className="h-full" style={{ background: VERDICT[key].color }} />
    <div className="min-w-0 px-3"><strong className="block truncate text-[12px] font-semibold">{outcome.title}</strong><small className="block truncate text-[11px]" style={{ color: outcome.missing ? 'var(--sem-bad)' : 'var(--sem-muted)' }}>{outcome.missing ? 'Target missing: the bound measure no longer exists. Re-bind or delete the test.' : outcome.message ?? outcome.targetRef ?? '·'}</small></div>
    <div className="px-3"><VerdictPill verdict={key} /></div>
    <div className="px-3"><ContextCells rows={outcome.rows} /></div>
    <div className="tnum px-3 text-[12px]" title={outcome.timingDetail}>{formatDuration(outcome.durationMs)} {timedAgainstBudget && outcome.budgetMs != null && <small style={{ color: 'var(--sem-muted)' }}>/ {outcome.budgetMs.toLocaleString()}</small>} {slow && <span className="ml-1 rounded border px-1 py-0.5 text-[9px] font-semibold" style={{ color: 'var(--sem-warn)', borderColor: 'var(--sem-warn)' }}>SLOW</span>}{outcome.timingVerdict === 'NotVerifiable' && <small className="ml-1" style={{ color: 'var(--sem-muted)' }}>not timed</small>}</div>
    {hasVariants && <div className="flex flex-wrap gap-1 px-3">{outcome.variants?.length ? outcome.variants.map((variant) => { const variantVerdict = verdictKey(variant.verdict); return <span key={variant.variant} className="rounded border px-1.5 py-0.5 text-[10px]" title={`${variant.variant}: ${variant.explanation ?? VERDICT[variantVerdict].label}`} style={{ color: VERDICT[variantVerdict].color, borderColor: variantVerdict === 'NotVerifiable' ? 'var(--sem-border)' : VERDICT[variantVerdict].color }}>{tiKindLabel(variant.kind, variant.variant)}</span>; }) : <span style={{ color: 'var(--sem-muted)' }}>·</span>}</div>}
    <div className="text-[15px]" style={{ color: 'var(--sem-muted)', transform: isOpen ? 'rotate(90deg)' : undefined }}>›</div>
  </div>{isOpen && <Evidence outcome={outcome} run={run} />}</>;
}

function ContextCells({ rows }: { rows?: CompareRowW[] }) {
  if (!rows?.length) return <span style={{ color: 'var(--sem-muted)' }}>·</span>;
  return <div className="flex gap-[3px]">{rows.slice(0, 8).map((row, index) => { const key = comparisonVerdict(row.verdict); return <i key={index} aria-hidden className="size-3.5 rounded-sm border" title={row.context} style={{ background: 'var(--sem-surface-2)', borderColor: VERDICT[key].color }} />; })}</div>;
}

function Evidence({ outcome, run }: { outcome: ReconcileOutcomeW; run: TestRunW }) {
  if (!outcome.rows?.length && !outcome.sql && !outcome.dax) return <div className="border-b px-6 py-4 text-[12px]" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>{outcome.message ?? 'No row evidence was returned for this check.'}</div>;
  return <div className="border-b px-6 py-4" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
    <div className="mb-3 flex flex-wrap items-start justify-between gap-3"><div><h3 className="m-0 text-[13px] font-semibold">Reconciliation evidence</h3><p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{outcome.toleranceNote ?? 'No tolerance detail was supplied.'}</p></div><div className="flex gap-4 text-[11px]" style={{ color: 'var(--sem-muted)' }}><span>Model <strong style={{ color: 'var(--sem-fg)' }}>{run.environment ?? '·'}</strong></span><span>When <strong style={{ color: 'var(--sem-fg)' }}>{formatDate(run.when)}</strong></span></div></div>
    <div className="mb-3 grid gap-2.5" style={{ gridTemplateColumns: 'repeat(2,minmax(0,1fr))' }}><QueryBlock title="Fabric SQL ground truth" text={outcome.sql} /><QueryBlock title="DAX model query" text={outcome.dax} /></div>
    <div className="mb-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{outcome.createdBy === 'agent' ? `Ground truth drafted by the AI Assistant, accepted by a person on ${formatDate(outcome.createdWhen, true)}.` : `Ground truth authored by a person on ${formatDate(outcome.createdWhen, true)}.`} The engine never writes the SQL; a test only counts once a person accepts it.</div>
    {outcome.rows?.length ? <CompareTable outcome={outcome} /> : <Card className="p-3 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>{outcome.message ?? 'No comparison rows were returned.'}</span></Card>}
  </div>;
}

function QueryBlock({ title, text }: { title: string; text?: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => { if (!text) return; navigator.clipboard.writeText(text).then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1400); }).catch(() => undefined); };
  return <div className="min-w-0 overflow-hidden rounded-md border" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}><div className="flex h-[31px] items-center border-b px-3 text-[10px] font-semibold uppercase tracking-[0.06em]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>{title}<button disabled={!text} onClick={copy} className="ml-auto border-0 bg-transparent text-[10px] normal-case tracking-normal disabled:opacity-40" style={{ color: 'var(--sem-muted)' }}>{copied ? 'Copied' : 'Copy'}</button></div><pre className="font-mono m-0 max-h-[153px] overflow-auto whitespace-pre-wrap p-3 text-[11px] leading-5">{text ?? '·'}</pre></div>;
}

function CompareTable({ outcome }: { outcome: ReconcileOutcomeW }) {
  const rows = outcome.rows ?? [];
  return <div className="overflow-hidden rounded-md border" role="table" aria-label="Reconciliation compare table" style={{ borderColor: 'var(--sem-border)' }}><div role="row" className="grid min-w-[620px] border-b px-3 py-2 text-[10px] font-semibold uppercase tracking-[0.05em]" style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr 110px', background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}><span role="columnheader">Filter context</span><span role="columnheader">SQL result</span><span role="columnheader">Model result</span><span role="columnheader">Delta</span><span role="columnheader">Verdict</span></div>{rows.map((row, index) => { const key = comparisonVerdict(row.verdict); const delta = resultText(row.delta, true); return <div role="row" key={`${row.context}-${index}`} className="grid min-w-[620px] border-b px-3 py-2 text-[11px] last:border-0" title={row.explanation} style={{ gridTemplateColumns: '1.5fr 1fr 1fr 1fr 110px', borderColor: 'var(--sem-border)', borderLeft: key === 'Fail' ? '3px solid var(--sem-bad)' : '3px solid transparent' }}><span role="cell" className={row.grandTotal ? 'font-semibold' : ''}>{row.context}</span><span role="cell"><Result value={row.sql} emptyLabel="NULL" /></span><span role="cell"><Result value={row.dax} emptyLabel="BLANK" /></span><span role="cell" className="tnum" style={{ color: key === 'Fail' ? 'var(--sem-bad)' : key === 'Pass' ? 'var(--sem-good)' : 'var(--sem-muted)' }}>{delta ?? '·'}</span><span role="cell"><VerdictPill verdict={key} /></span></div>; })}{outcome.rowsTotal != null && outcome.rowsTotal > rows.length && <div className="px-3 py-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Showing {rows.length.toLocaleString()} of {outcome.rowsTotal.toLocaleString()} contexts (worst first)</div>}</div>;
}

function Result({ value, emptyLabel }: { value: number | null | undefined; emptyLabel: string }) {
  // Each side keeps its OWN empty vocabulary (SQL NULL vs DAX BLANK): they are different things, and the
  // blank policy that judged them is stated in the tolerance note above the table.
  const text = resultText(value);
  return <span className="tnum" style={{ color: text == null ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{text ?? emptyLabel}</span>;
}

function Relationships({ report }: { report?: RelationshipReportW }) {
  const rels = report?.relationships ?? [];
  const tables = report?.tableRowCounts ?? [];
  // The band counts EVERY failed relationship check (a duplicate-key failure is as real as an orphan
  // failure); the headline then tells the worst ORPHAN story when one exists, else the first failure's own.
  const failures = rels.filter((rel) => [rel.dataTypeMatch, rel.keyUniqueness, rel.referentialIntegrity].some((c) => verdictKey(c.verdict) === 'Fail'));
  const failedChecks = rels.flatMap((rel) => [rel.dataTypeMatch, rel.keyUniqueness, rel.referentialIntegrity].filter((c) => verdictKey(c.verdict) === 'Fail'));
  const riFailures = rels.filter((rel) => verdictKey(rel.referentialIntegrity.verdict) === 'Fail');
  const worst = [...riFailures].sort((a, b) => (b.referentialIntegrity.count ?? 0) - (a.referentialIntegrity.count ?? 0))[0];
  const firstFailedCheck = failedChecks[0];
  // The sub states the REQUIREMENT (true in every state), never an assertion the numbers above may contradict.
  const signals: { title: string; pick: (rel: RelationshipResultW) => CheckResultW; sub: string }[] = [
    { title: 'Key uniqueness', pick: (rel) => rel.keyUniqueness, sub: 'Every dimension-side key must occur once.' },
    { title: 'Data types', pick: (rel) => rel.dataTypeMatch, sub: 'Join columns must use compatible types.' },
    { title: 'Referential integrity', pick: (rel) => rel.referentialIntegrity, sub: 'Every fact row must resolve to a dimension key.' },
  ];
  return <section><SectionIntro title="Relationships" sub="Trust checks across keys, types and fact-to-dimension coverage." />
    {failedChecks.length > 0 && (worst
      ? <RootBand count={failedChecks.length} title={`${worst.referentialIntegrity.count == null ? 'Some' : worst.referentialIntegrity.count.toLocaleString()} ${worst.manyTable} rows have no matching ${worst.oneTable}`} body={failedChecks.length > 1 ? `Orphans land on the hidden blank row, so totals can still reconcile while breakdowns are wrong. ${failedChecks.length} relationship checks failed in total.` : 'Orphans land on the hidden blank row. Totals can still reconcile while breakdowns remain wrong.'} />
      : <RootBand count={failedChecks.length} title={`${plural(failedChecks.length, 'relationship check')} failed in ${plural(failures.length, 'relationship')}`} body={firstFailedCheck?.message ?? 'See the failing rows below.'} />)}
    <div className="mb-3 grid gap-2.5" style={{ gridTemplateColumns: 'repeat(3,minmax(0,1fr))' }}>{signals.map((signal) => { const checks = rels.map(signal.pick); const pass = checks.filter((check) => verdictKey(check.verdict) === 'Pass').length; const fail = checks.filter((check) => verdictKey(check.verdict) === 'Fail').length; const decided = pass + fail; return <Card key={signal.title} className="p-3.5"><Eyebrow>{signal.title}</Eyebrow><strong className="tnum mt-1 block text-[15px]" style={{ color: fail > 0 ? 'var(--sem-bad)' : decided > 0 ? 'var(--sem-good)' : 'var(--sem-nv)' }}>{fail > 0 ? plural(fail, 'failure') : `${pass} / ${decided}`}</strong><p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{signal.sub}</p></Card>; })}</div>
    {rels.length === 0 ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>No relationship checks were returned.</span></Card> : <Card className="overflow-hidden">{rels.map((rel) => <RelationshipRow key={rel.name} rel={rel} />)}</Card>}
    <div className="mb-2 mt-5"><Eyebrow>Table row counts</Eyebrow><p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>COUNTROWS in the model vs COUNT_BIG at each detected SQL table. A mismatch stays Not verifiable unless both reads are proven to share one snapshot.</p></div>
    {tables.length === 0 ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>No SQL-backed physical tables were detected.</span></Card> : <Card className="overflow-hidden">{tables.map((table) => <TableRowCountRow key={`${table.modelTable}-${table.schema}-${table.entity}`} table={table} />)}</Card>}
  </section>;
}

function TableRowCountRow({ table }: { table: TableRowCountW }) {
  const key = verdictKey(table.check.verdict);
  const observed = table.modelObservedUtc || table.sourceObservedUtc;
  return <div className="grid min-h-[66px] items-center border-b last:border-0" style={{ gridTemplateColumns: '5px 1.2fr 1fr 1fr 1.5fr', borderColor: 'var(--sem-border)' }}><div className="h-full" style={{ background: VERDICT[key].color }} /><div className="px-3"><strong className="text-[12px]">{table.modelTable}</strong><small className="block truncate text-[11px]" title={`${table.server ?? ''} / ${table.database ?? ''} / ${table.schema ?? ''}.${table.entity ?? ''}`} style={{ color: 'var(--sem-muted)' }}>{table.schema && table.entity ? `${table.schema}.${table.entity}` : 'Source mapping incomplete'}</small></div><div className="px-3"><Eyebrow>Model</Eyebrow><strong className="tnum mt-1 block text-[14px]">{table.modelCount == null ? '·' : table.modelCount.toLocaleString()}</strong><small className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{formatDate(table.modelObservedUtc)}</small></div><div className="px-3"><Eyebrow>SQL source</Eyebrow><strong className="tnum mt-1 block text-[14px]">{table.sourceCount == null ? '·' : table.sourceCount.toLocaleString()}</strong><small className="block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{formatDate(table.sourceObservedUtc)}</small></div><div className="px-3"><VerdictPill verdict={key} title={table.check.message} /><small className="mt-1 block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{table.check.message ?? (observed ? 'Row counts observed.' : 'Row counts were not observed.')}</small></div></div>;
}

function RelationshipRow({ rel }: { rel: RelationshipResultW }) {
  const worst = worstVerdict([rel.dataTypeMatch.verdict, rel.keyUniqueness.verdict, rel.referentialIntegrity.verdict]);
  const referenceVerdict = verdictKey(rel.referentialIntegrity.verdict);
  const orphanCount = rel.referentialIntegrity.count;
  const orphanRate = orphanCount != null && rel.manyRowCount ? (orphanCount / rel.manyRowCount) * 100 : undefined;
  return <div className="grid min-h-14 items-center border-b last:border-0" style={{ gridTemplateColumns: '5px 1.4fr 1fr 1fr 1fr', borderColor: 'var(--sem-border)' }}><div className="h-full" style={{ background: VERDICT[worst].color }} /><div className="px-3"><strong className="text-[12px]">{rel.manyTable} → {rel.oneTable}</strong><small className="block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{cardinalityLabel(rel.cardinality)} · {rel.isActive ? 'active' : 'inactive'}</small></div><div className="px-3">{referenceVerdict === 'NotVerifiable' || referenceVerdict === 'Suspect' ? <><VerdictPill verdict={referenceVerdict} /><small className="mt-1 block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{referenceVerdict === 'Suspect' && rel.referentialIntegrity.rootCause ? `Root cause: ${rel.referentialIntegrity.rootCause}` : rel.referentialIntegrity.message ?? 'References could not be checked.'}</small></> : <><strong className="tnum text-[15px]" style={{ color: referenceVerdict === 'Fail' ? 'var(--sem-bad)' : 'var(--sem-good)' }}>{orphanCount != null ? orphanCount.toLocaleString() : referenceVerdict === 'Pass' ? '0' : '·'}</strong><small className="block text-[11px]" style={{ color: 'var(--sem-muted)' }}>orphan rows{orphanRate != null && rel.manyRowCount != null ? ` · ${orphanRate.toLocaleString(undefined, { maximumFractionDigits: 2 })}% of ${rel.manyRowCount.toLocaleString()} facts` : ''}</small></>}</div><RelationshipCheck check={rel.keyUniqueness} passText="Keys unique" /><RelationshipCheck check={rel.dataTypeMatch} passText="Types match" /></div>;
}

function RelationshipCheck({ check, passText }: { check: CheckResultW; passText: string }) {
  const key = verdictKey(check.verdict);
  if (key === 'NotVerifiable') return <div className="px-3"><VerdictPill verdict={key} /><small className="mt-1 block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{check.message ?? `${checkLabel(check.check)} could not be checked.`}</small></div>;
  return <div className="px-3 text-[11px]" style={{ color: VERDICT[key].color }}>{key === 'Pass' ? passText : check.message ?? (key === 'Suspect' ? `${checkLabel(check.check)} is suspect.` : `${checkLabel(check.check)} failed.`)}<small className="block" style={{ color: 'var(--sem-muted)' }}>{key === 'Suspect' && check.rootCause ? `Root cause: ${check.rootCause}` : check.message && key === 'Pass' ? check.message : ''}</small></div>;
}

function Security({ report }: { report?: SecurityReportW }) {
  const filters = report?.filters ?? [];
  const ols = report?.ols ?? [];
  const roles = Array.from(new Set([...filters.map((filter) => filter.role), ...ols.map((item) => item.role)]));
  const failures = filters.filter((filter) => verdictKey(filter.check.verdict) === 'Fail');
  const first = failures[0];
  return <section><SectionIntro title="Security" sub="Role filters and object visibility read statically. Row-level assertions with real identities arrive as their own gated slice." />
    {first && <RootBand count={failures.length} title={`Role filter failed: ${first.role} on ${first.table}`} body={first.check.message ?? `The filter on ${first.table} does not restrict rows.`} />}
    {roles.length === 0 ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>No role filters or object visibility data were returned.</span></Card> : roles.map((role) => <RoleCard key={role} role={role} filters={filters.filter((filter) => filter.role === role)} ols={ols.find((item) => item.role === role)} />)}
  </section>;
}

function RoleCard({ role, filters, ols }: { role: string; filters: RoleFilterResultW[]; ols?: RoleOlsW }) {
  // A role with NO filter checks has no verdict to summarize: object visibility is informational (never a
  // check), so inventing a "Not verifiable" pill here would claim a check existed. Neutral instead.
  const hasChecks = filters.length > 0;
  const worst = worstVerdict(filters.map((filter) => filter.check.verdict));
  const reason = filters.find((filter) => verdictKey(filter.check.verdict) === worst)?.check;
  const visible = ols ? Math.max(0, ols.tablesTotal - ols.tablesHidden) : 0;
  return <Card className="mb-2 grid min-h-[76px] items-stretch overflow-hidden" style={{ gridTemplateColumns: ols ? '5px 1fr 1fr 1.2fr' : '5px 1fr 1fr' }}><div style={{ background: hasChecks ? VERDICT[worst].color : 'var(--sem-border)' }} /><div className="self-center px-4 py-3"><strong className="text-[12px]">{role}</strong><small className="mt-1 block text-[11px]" style={hasChecks ? { color: VERDICT[worst].color } : { color: 'var(--sem-muted)' }}>{hasChecks ? `${VERDICT[worst].label}${reason?.rootCause ? `: ${reason.rootCause}` : reason?.message ? `: ${reason.message}` : ''}` : 'No row filter to check; visibility only.'}</small></div><div className="self-center px-4 py-3"><Eyebrow>Row filter</Eyebrow><div className="mt-1 flex flex-wrap gap-1">{filters.length ? filters.map((filter) => <span key={`${filter.table}-${filter.check.check}`} className="font-mono rounded border px-2 py-1 text-[11px]" title={filter.table} style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{filter.filterPreview ?? `${filter.table}: ·`}</span>) : <span style={{ color: 'var(--sem-muted)' }}>·</span>}</div></div>{ols && <div className="self-center px-4 py-3"><Eyebrow>Object visibility</Eyebrow><div className="mt-1.5 grid w-max grid-cols-8 gap-1">{ols.tiles.map((tile, index) => <i key={`${tile.table}-${index}`} title={`${tile.table}: ${tile.hidden ? 'hidden from this role' : 'visible'}`} aria-label={`${tile.table}: ${tile.hidden ? 'hidden' : 'visible'}`} className="size-[17px] rounded-sm border" style={tile.hidden ? { background: 'transparent', borderStyle: 'dashed', borderColor: 'var(--sem-nv)' } : { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }} />)}</div><small className="mt-1 block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{visible} visible, {ols.tablesHidden} hidden</small></div>}</Card>;
}

function EvidenceLibraryView({ library, onOpen }: { library: EvidenceLibraryW | null; onOpen: (item: EvidenceItemW) => void }) {
  const colorOf = (item: EvidenceItemW) => !item.valid ? 'var(--sem-bad)'
    : item.verdict === 'Verified' ? 'var(--sem-good)'
      : item.verdict === 'Broken' ? 'var(--sem-bad)'
        : item.verdict === 'NeedsReview' || item.verdict === 'Overridden' ? 'var(--sem-warn)' : 'var(--sem-nv)';
  return (
    <section>
      <SectionIntro title="Saved reports" sub="Sealed Test and Workflow reports live beside the model as source-control-friendly JSON and HTML pairs. Nothing is saved automatically." />
      {library?.note && <div className="mb-3"><Banner color={library.invalidCount > 0 ? 'var(--sem-bad)' : 'var(--sem-warn)'}>{library.note}</Banner></div>}
      {library?.directoryPath && <div className="mb-3 truncate rounded-md border px-3 py-2 text-[11px]" title={library.directoryPath} style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
        Stored in <span className="tnum" style={{ color: 'var(--sem-fg)' }}>{library.directoryPath}</span>
      </div>}
      {!library ? <Card className="p-4 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading saved evidence…</Card>
        : library.items.length === 0 ? <Card className="p-4 text-[12px]"><strong>No evidence saved yet.</strong><p className="m-0 mt-1" style={{ color: 'var(--sem-muted)' }}>Open a Test report or terminal Workflow evidence view, review it, then choose Save with model. Export remains a separate local copy.</p></Card>
          : <div className="grid gap-2.5" style={{ gridTemplateColumns: 'repeat(auto-fill,minmax(330px,1fr))' }}>
            {library.items.map((item) => {
              const color = colorOf(item);
              const kind = item.kind === 'workflow-run' ? 'Workflow' : item.kind === 'test-suite' ? 'Tests' : item.kind || 'Evidence';
              return <Card key={`${item.id}-${item.updatedUtc}`} className="overflow-hidden">
                <div className="h-1" style={{ background: color }} />
                <div className="p-3">
                  <div className="flex items-start gap-2">
                    <div className="min-w-0 flex-1"><Eyebrow>{kind}</Eyebrow><div className="mt-1 truncate text-[13px] font-semibold" title={item.title}>{item.title || item.id}</div></div>
                    <span className="rounded-full border px-2 py-0.5 text-[9.5px] font-semibold uppercase" style={{ color, borderColor: color }}>{item.valid ? uiLabel(item.verdict, 'Ungraded') : 'Invalid'}</span>
                  </div>
                  <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
                    <span>{formatDate(item.createdUtc || item.updatedUtc)}</span>
                    {item.producer && <span>by {item.producer}</span>}
                    {item.total != null && <span className="tnum">{item.verified ?? 0}/{item.total} verified{item.unknowns ? ` · ${item.unknowns} unknown` : ''}</span>}
                  </div>
                  <div className="mt-2 truncate text-[10px] tnum" title={item.contentHash} style={{ color: 'var(--sem-muted)' }}>{item.contentHash ? `SHA-256 ${item.contentHash}` : item.note}</div>
                  <div className="mt-3 flex items-center justify-between gap-2">
                    <span className="truncate text-[10px]" title={item.jsonPath} style={{ color: 'var(--sem-muted)' }}>{item.valid ? 'JSON + HTML verified as one pair' : item.note}</span>
                    <button disabled={!item.valid} onClick={() => onOpen(item)} className="shrink-0 rounded-md border px-2.5 py-1 text-[11px] font-semibold disabled:opacity-40" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>Open</button>
                  </div>
                </div>
              </Card>;
            })}
          </div>}
    </section>
  );
}

function History({ history }: { history: TestHistoryW | null }) {
  const runs = useMemo(() => [...(history?.runs ?? [])].sort((a, b) => new Date(a.when).valueOf() - new Date(b.when).valueOf()), [history]);
  const regressions: { title: string; detail: string; color: string }[] = [];
  const gradeRank: Record<string, number> = { A: 0, B: 1, C: 2, D: 3, F: 4 };
  for (let index = 1; index < runs.length; index += 1) {
    const previous = runs[index - 1]?.health;
    const current = runs[index]?.health;
    if (!previous || !current) continue;
    if ((gradeRank[current.grade] ?? 0) > (gradeRank[previous.grade] ?? 0)) regressions.push({ title: `Grade dropped: ${previous.grade} to ${current.grade}`, detail: formatDate(runs[index]?.when), color: 'var(--sem-bad)' });
    const coverageDrop = previous.coveragePct - current.coveragePct;
    if (coverageDrop >= 2) regressions.push({ title: `Coverage dropped ${coverageDrop.toLocaleString()} points`, detail: `${previous.coveragePct}% to ${current.coveragePct}%`, color: 'var(--sem-bad)' });
    const rootIncrease = current.rootFailures - previous.rootFailures;
    if (rootIncrease > 0) regressions.push({ title: `Root causes increased by ${rootIncrease}`, detail: `${previous.rootFailures} to ${current.rootFailures}`, color: 'var(--sem-bad)' });
  }
  return <section><SectionIntro title="History" sub="Coverage-aware grade trend and regression signals across recorded runs. Recording and history are Pro; running is always free." />
    {history?.note ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>{history.note}</span></Card> : !history ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>Loading recorded runs…</span></Card> : runs.length === 0 ? <Card className="p-4 text-[12px]"><span style={{ color: 'var(--sem-muted)' }}>No recorded runs yet.</span></Card> : runs.length === 1 ? <Card className="p-4"><Eyebrow>One recorded run</Eyebrow><div className="mt-2 text-[13px]">{runs[0]?.health ? `${runs[0].health?.grade} · ${runs[0].health?.coveragePct}% coverage` : 'No health result'} <span className="ml-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{formatDate(runs[0]?.when)}</span></div></Card> : <div className="grid gap-3.5" style={{ gridTemplateColumns: '2fr 1fr' }}><HistoryChart runs={runs} /><Card className="p-4"><Eyebrow>Regression signals</Eyebrow>{regressions.length ? regressions.map((item, index) => <div key={`${item.title}-${index}`} className="border-t py-3 first:mt-2" style={{ borderColor: 'var(--sem-border)' }}><strong className="text-[12px]" style={{ color: item.color }}>{item.title}</strong><p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{item.detail}</p></div>) : <p className="m-0 mt-3 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No grade, coverage or root-cause regressions between consecutive recorded runs.</p>}</Card></div>}
  </section>;
}

function HistoryChart({ runs }: { runs: TestRunRecordW[] }) {
  const points = runs.map((run, index) => { const coverage = run.health?.coveragePct ?? 0; return { x: runs.length === 1 ? 350 : (index / (runs.length - 1)) * 700, y: 205 - Math.max(0, Math.min(100, coverage)) * 1.75, run }; });
  const line = points.map((point, index) => `${index ? 'L' : 'M'}${point.x.toFixed(1)} ${point.y.toFixed(1)}`).join(' ');
  const area = `M0 220 ${line} L700 220 Z`;
  const last = runs[runs.length - 1];
  return <Card className="p-4"><div className="flex items-center justify-between gap-3"><div><Eyebrow>Grade + coverage trend</Eyebrow><h2 className="m-0 mt-1 text-[13px] font-semibold">{last?.health ? `${last.health.grade} · ${last.health.coveragePct}% coverage` : 'Coverage trend'}</h2></div><span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}><i aria-hidden className="mr-1 inline-block size-1.5 rounded-full" style={{ background: 'var(--sem-good)' }} />Coverage, letters mark grades</span></div><div className="mt-3 h-[210px] border-b border-l" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}><svg className="h-full w-full overflow-visible" viewBox="0 0 700 220" preserveAspectRatio="none" role="img" aria-label="Grade and coverage trend chart"><path d={area} fill="color-mix(in srgb, var(--sem-good) 10%, transparent)" /><path d={line} fill="none" stroke="var(--sem-good)" strokeWidth="2" />{points.map((point, index) => <g key={point.run.runId}><circle cx={point.x} cy={point.y} r={index === points.length - 1 ? 4 : 2} fill="var(--sem-good)" /><text x={point.x} y={Math.max(10, point.y - 9)} textAnchor="middle" fontSize="13" fontWeight="700" fill={GRADE_COLOR[point.run.health?.grade ?? ''] ?? 'var(--sem-nv)'}>{point.run.health?.grade ?? ''}</text></g>)}</svg></div><div className="mt-1.5 flex justify-between text-[10px]" style={{ color: 'var(--sem-muted)' }}><span>{formatDate(runs[0]?.when, true)}</span><span>{formatDate(runs[runs.length - 1]?.when, true)}</span></div></Card>;
}
