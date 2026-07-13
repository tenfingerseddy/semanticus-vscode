import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, loadState, saveState } from './bridge';
import type { ColumnRow } from './wire';
import { MEditor } from './meditor';
import { formatM } from './mlang';
import { mDiagnostics } from './manalysis';
import { useConnection } from './connection';
import { gen, mQuoteIdent } from './mtransform';
import { SamplePreview, AppliedStepsPanel, type GridCol } from './pqtransforms';

// The M Code tab is one workspace: the M query is primary, with the selected table's incremental-refresh
// prerequisites and policy beside it. The CodeMirror M editor provides offline format, the
// std-library-aware autocomplete/hover + inline diagnostics (manalysis.ts), an Applied-Steps outline, partition /
// shared-expression editing + create, and a live "sample of loaded data" (read-only EVALUATE TOPN — there's no
// redistributable cross-platform Mashup engine, so we sample the LOADED table, never evaluate M per-step). Editing M
// is metadata-only (set_partition_m / update_named_expression); it never triggers a refresh. See docs/powerquery-tab-plan.md.

// --- wire types (camelCase projections from Semanticus.Engine/Protocol.cs DocModelDto) ---
interface DocPartition { name: string; mode: string; sourceType: string; source: string; refreshedTime?: string | null; }
interface DocExpression { name: string; kind: string; expression: string; }
interface DocTable { ref: string; name: string; isCalculated: boolean; partitions: DocPartition[]; }
interface DocModel { header: { compatibilityLevel: number }; tables: DocTable[]; columns: ColumnRow[]; expressions: DocExpression[]; }
interface RefreshPolicyInfo {
  table: string; enabled: boolean; mode: string | null;
  rollingWindowGranularity: string; rollingWindowPeriods: number;
  incrementalGranularity: string; incrementalPeriods: number; incrementalPeriodsOffset: number;
  sourceExpression: string | null; pollingExpression: string | null;
}

type Gran = 'Day' | 'Month' | 'Quarter' | 'Year';
const GRANS: Gran[] = ['Day', 'Month', 'Quarter', 'Year'];
const CL_MIN = 1450, CL_HYBRID = 1565;
// Standard Power BI RangeStart/RangeEnd parameter M — carries IsParameterQuery=true so the engine's IsParameterExpr
// check (and the service) treat them as bound date/time parameters. Names are reserved + case-sensitive.
const PARAM_M = (iso: string) => `#datetime(${iso}) meta [IsParameterQuery=true, Type="DateTime", IsParameterQueryRequired=true]`;
const isParamExpr = (e?: DocExpression) => !!e && (e.expression ?? '').replace(/\s+/g, '').toLowerCase().includes('isparameterquery=true');
const stripMComments = (s: string) => s.replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, ' ');
const filtersOnRange = (src?: string) => {
  const s = stripMComments(src ?? '');
  const relational = (name: string) => new RegExp(`(<=|<|>=|>)\\s*${name}\\b|\\b${name}\\s*(<=|<|>=|>)`).test(s);
  return relational('RangeStart') && relational('RangeEnd');
};

type Form = {
  dateColumn: string;
  storePeriods: number; storeGranularity: Gran;
  refreshPeriods: number; refreshGranularity: Gran; offset: number;
  mode: 'Import' | 'Hybrid'; polling: string;
};
const DEFAULT_FORM: Form = { dateColumn: '', storePeriods: 5, storeGranularity: 'Year', refreshPeriods: 10, refreshGranularity: 'Day', offset: 0, mode: 'Import', polling: '' };

type Toast = { text: string; tone: 'ok' | 'error' };

export function MCodeView({ navTarget }: { navTarget?: { table: string; partitionId?: string; nonce: number } | null } = {}) {
  const [doc, setDoc] = useState<DocModel | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [selected, setSelected] = useState<string>(() => loadState<string>('pqTable', ''));
  // A Model-tree "Edit M code" jump selects that table and exact partition. Nonce re-fires a repeat jump.
  // pendingNav feeds the eligibility toast below; mNav hands the clicked partition to the M lane.
  const [pendingNav, setPendingNav] = useState<{ table: string; nonce: number } | null>(null);
  const [mNav, setMNav] = useState<{ id: string; nonce: number } | null>(null);
  useEffect(() => {
    if (!navTarget?.table) return;
    setSelected(navTarget.table);
    setPendingNav({ table: navTarget.table, nonce: navTarget.nonce });
    if (navTarget.partitionId) setMNav({ id: navTarget.partitionId, nonce: navTarget.nonce });
  }, [navTarget?.nonce]);  // eslint-disable-line react-hooks/exhaustive-deps
  const [policy, setPolicy] = useState<RefreshPolicyInfo | null>(null);
  const [form, setForm] = useState<Form>(DEFAULT_FORM);
  const [enabled, setEnabled] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);

  useEffect(() => { saveState('pqTable', selected); }, [selected]);
  useEffect(() => { if (toast?.tone === 'ok') { const id = window.setTimeout(() => setToast(null), 3500); return () => window.clearTimeout(id); } }, [toast]);

  const loadDoc = useCallback(async () => {
    try { const d = await rpc<DocModel>('getDocModel', 1); setDoc(d); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }, []);
  useEffect(() => {
    void loadDoc();
    let t: number | undefined;
    const off = onDidChange(() => { window.clearTimeout(t); t = window.setTimeout(() => void loadDoc(), 300); });
    return () => { off(); window.clearTimeout(t); };
  }, [loadDoc]);

  // Tables that can carry an incremental-refresh policy: those with an M/import partition (not calculated tables).
  const tables = useMemo(() => (doc?.tables ?? []).filter((t) => !t.isCalculated), [doc]);
  // Heal the selection: keep it if still present, else pick the first eligible table.
  useEffect(() => {
    if (!tables.length) return;
    if (!tables.some((t) => t.name === selected)) setSelected(tables[0].name);
  }, [tables, selected]);
  // The honesty layer over that heal-snap: a tree jump to a table this tab can't edit (calculated — its logic is
  // DAX) used to be silently snapped back to the first eligible table, so the click looked like it did nothing.
  // Consumed once per navigation (whether the doc was already loaded or arrives after), never on doc reloads.
  useEffect(() => {
    if (!pendingNav || !doc) return;
    if (!tables.some((t) => t.name === pendingNav.table)) {
      const raw = doc.tables.find((t) => t.name === pendingNav.table);
      setToast({
        text: raw?.isCalculated
          ? `'${pendingNav.table}' has no M code to edit. It's a calculated table, so its logic is DAX, not M.`
          : `Can't find a table called '${pendingNav.table}' in this model.`,
        tone: 'error',
      });
    }
    setPendingNav(null);
  }, [pendingNav, doc, tables]);

  const table = useMemo(() => tables.find((t) => t.name === selected) ?? null, [tables, selected]);
  const cl = doc?.header.compatibilityLevel ?? 0;
  const rangeStart = useMemo(() => doc?.expressions.find((e) => e.name === 'RangeStart'), [doc]);
  const rangeEnd = useMemo(() => doc?.expressions.find((e) => e.name === 'RangeEnd'), [doc]);
  const paramsOk = isParamExpr(rangeStart) && isParamExpr(rangeEnd);
  const partitionFilters = useMemo(() => (table?.partitions ?? []).some((p) => filtersOnRange(p.source)), [table]);
  const dateColumns = useMemo(
    () => (doc?.columns ?? []).filter((c) => c.table === selected && /date|time/i.test(c.dataType)).map((c) => c.name),
    [doc, selected]
  );
  const allColumns = useMemo(() => (doc?.columns ?? []).filter((c) => c.table === selected).map((c) => c.name), [doc, selected]);

  // Load the selected table's policy → seed the form.
  useEffect(() => {
    if (!selected) return;
    let cancelled = false;
    (async () => {
      try {
        const p = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + selected);
        if (cancelled) return;
        setPolicy(p);
        setEnabled(!!p?.enabled);
        setForm((f) => p?.enabled ? {
          dateColumn: f.dateColumn, // dateColumn isn't echoed by the policy DTO; keep current / default below
          storePeriods: p.rollingWindowPeriods || DEFAULT_FORM.storePeriods,
          storeGranularity: (GRANS.includes(p.rollingWindowGranularity as Gran) ? p.rollingWindowGranularity : DEFAULT_FORM.storeGranularity) as Gran,
          refreshPeriods: p.incrementalPeriods || DEFAULT_FORM.refreshPeriods,
          refreshGranularity: (GRANS.includes(p.incrementalGranularity as Gran) ? p.incrementalGranularity : DEFAULT_FORM.refreshGranularity) as Gran,
          offset: p.incrementalPeriodsOffset || 0,
          mode: (p.mode === 'Hybrid' ? 'Hybrid' : 'Import'),
          polling: p.pollingExpression ?? '',
        } : DEFAULT_FORM);
      } catch (e) { if (!cancelled) setErr(String((e as Error).message ?? e)); }
    })();
    return () => { cancelled = true; };
  }, [selected]);

  // Default the date column once columns are known (prefer a date/time column).
  useEffect(() => {
    setForm((f) => f.dateColumn && (dateColumns.includes(f.dateColumn) || allColumns.includes(f.dateColumn)) ? f : { ...f, dateColumn: dateColumns[0] ?? allColumns[0] ?? '' });
  }, [dateColumns, allColumns]);

  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));

  const createParams = useCallback(async () => {
    setBusy(true); setErr(null);
    try {
      if (!isParamExpr(rangeStart)) await rpc(rangeStart ? 'updateNamedExpression' : 'createNamedExpression', 'RangeStart', PARAM_M('2020, 1, 1, 0, 0, 0'));
      if (!isParamExpr(rangeEnd)) await rpc(rangeEnd ? 'updateNamedExpression' : 'createNamedExpression', 'RangeEnd', PARAM_M('2021, 1, 1, 0, 0, 0'));
      setToast({ text: '✓ RangeStart / RangeEnd parameters are ready', tone: 'ok' });
      await loadDoc();
    } catch (e) { setToast({ text: `Couldn't create or update parameters: ${String((e as Error).message ?? e)}`, tone: 'error' }); }
    finally { setBusy(false); }
  }, [rangeStart, rangeEnd, loadDoc]);

  const addRangeFilter = useCallback(async () => {
    if (!table || !form.dateColumn || partitionFilters) return;
    setBusy(true); setErr(null);
    try {
      const partition = table.partitions.find((p) => p.sourceType.toLowerCase() === 'm');
      if (!partition) throw new Error(`'${table.name}' has no M partition to filter.`);
      const next = await gen.incrementalRefreshFilter(partition.source ?? '', form.dateColumn);
      if (!next.ok) throw new Error(next.error);
      await rpc('setPartitionM', `partition:${table.name}/${partition.name}`, next.m);
      setToast({ text: `✓ added the RangeStart / RangeEnd filter to '${partition.name}'`, tone: 'ok' });
      await loadDoc();
    } catch (e) { setToast({ text: `Couldn't add the range filter: ${String((e as Error).message ?? e)}`, tone: 'error' }); }
    finally { setBusy(false); }
  }, [table, form.dateColumn, partitionFilters, loadDoc]);

  const save = useCallback(async () => {
    if (!selected) return;
    setBusy(true); setErr(null);
    try {
      await rpc('setIncrementalRefreshPolicy', 'table:' + selected, form.dateColumn || null,
        form.storePeriods, form.storeGranularity, form.refreshPeriods, form.refreshGranularity, form.offset,
        form.mode, advanced && form.polling.trim() ? form.polling.trim() : null, true);
      setToast({ text: '✓ incremental refresh policy saved', tone: 'ok' });
      await loadDoc();
      const p = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + selected); setPolicy(p); setEnabled(!!p?.enabled);
    } catch (e) { setToast({ text: String((e as Error).message ?? e), tone: 'error' }); }
    finally { setBusy(false); }
  }, [selected, form, advanced, loadDoc]);

  const remove = useCallback(async () => {
    if (!selected) return;
    setBusy(true); setErr(null);
    try {
      await rpc('removeIncrementalRefreshPolicy', 'table:' + selected);
      setToast({ text: '✓ incremental refresh policy removed', tone: 'ok' });
      setEnabled(false); setPolicy(null); setForm(DEFAULT_FORM);
      await loadDoc();
    } catch (e) { setToast({ text: String((e as Error).message ?? e), tone: 'error' }); }
    finally { setBusy(false); }
  }, [selected, loadDoc]);

  if (err) return <div className="p-4"><div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div></div>;
  if (!doc) return <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading model…</div>;
  if (!tables.length) return <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No import/query tables in this model. Incremental refresh applies to tables loaded by a query (M).</div>;

  const clOk = cl >= CL_MIN;
  const hybridOk = cl >= CL_HYBRID;
  const prereqsOk = clOk && paramsOk && partitionFilters;

  return (
    <div className="h-full flex flex-col">
      {/* One table context drives both the query and its refresh policy. */}
      <div className="flex items-center gap-2 px-4 py-2 text-[11px] border-b flex-wrap" style={{ borderColor: 'var(--sem-border)' }}>
        <span style={{ color: 'var(--sem-muted)' }}>Table</span>
        <select value={selected} onChange={(e) => setSelected(e.target.value)} style={input} title="Choose a table">
          {tables.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
        </select>
        <span style={{ color: 'var(--sem-muted)' }}>Edit the query and its refresh policy together.</span>
          <span className="ml-auto" style={{ color: 'var(--sem-muted)' }}>CL {cl || 'Not set'}</span>
      </div>

      <div className="flex-1 min-h-0 overflow-auto p-4 relative">
        {toast && (
          <div className="absolute left-1/2 -translate-x-1/2 bottom-3 z-10 flex items-center gap-2 rounded-md border px-3 py-1.5 text-[11px] shadow-lg"
            style={{ background: 'var(--sem-surface)', borderColor: toast.tone === 'ok' ? 'var(--sem-good)' : 'var(--sem-bad)', color: 'var(--sem-fg)' }}>
            <span>{toast.text}</span>
            <button onClick={() => setToast(null)} style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
          </div>
        )}

        <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_400px]">
          <div className="min-w-0">
            <MLane doc={doc} table={table} onSaved={loadDoc} initialTarget={mNav} />
          </div>
          <aside className="min-w-0 flex flex-col gap-4" aria-label="Incremental refresh settings">
            {/* prerequisite checklist */}
            <Card title="Prerequisites">
              <Check ok={clOk} label={`Compatibility level ≥ ${CL_MIN}`} detail={clOk ? `model is ${cl}` : `model is ${cl}. Raise it to enable incremental refresh`} />
              <Check ok={paramsOk} label="RangeStart & RangeEnd date/time parameters exist"
                detail={paramsOk ? 'both present as parameter queries' : (rangeStart || rangeEnd ? 'present but not parameter queries (need IsParameterQuery=true)' : 'not defined yet')}
                action={!paramsOk ? { label: 'Create or update parameters', run: createParams } : undefined} busy={busy} />
              <Check ok={partitionFilters} label="A partition filters on RangeStart / RangeEnd"
                detail={partitionFilters ? 'range filter found in partition M' : `uses [${form.dateColumn || 'Date'}] >= RangeStart and [${form.dateColumn || 'Date'}] < RangeEnd`}
                action={!partitionFilters && form.dateColumn ? { label: 'Add range filter', run: addRangeFilter } : undefined} busy={busy} />
            </Card>

            {/* the policy form */}
            <Card title="Incremental refresh policy">
              <label className="flex items-center gap-2 mb-1"><input type="checkbox" checked={enabled} disabled={busy} onChange={(e) => setEnabled(e.target.checked)} /> Configure incremental refresh for <b style={{ color: 'var(--sem-fg)' }}>&nbsp;{selected}</b></label>
              {enabled && (
                <div className="flex flex-col gap-3 pl-1 pt-1">
                  <Row label="Date column">
                    <select value={form.dateColumn} disabled={busy} onChange={(e) => set('dateColumn', e.target.value)} style={input}>
                      {dateColumns.length === 0 && <option value="">(no date column found)</option>}
                      {(dateColumns.length ? dateColumns : allColumns).map((c) => <option key={c} value={c}>{c}</option>)}
                    </select>
                  </Row>
                  <Row label="Store rows from the past">
                    <Num value={form.storePeriods} onChange={(v) => set('storePeriods', v)} disabled={busy} />
                    <GranSel value={form.storeGranularity} onChange={(v) => set('storeGranularity', v)} disabled={busy} />
                    <span style={hint}>the archive window kept in the model</span>
                  </Row>
                  <Row label="Refresh rows from the past">
                    <Num value={form.refreshPeriods} onChange={(v) => set('refreshPeriods', v)} disabled={busy} />
                    <GranSel value={form.refreshGranularity} onChange={(v) => set('refreshGranularity', v)} disabled={busy} />
                    <span style={hint}>the recent window re-imported each refresh</span>
                  </Row>
                  <Row label="Offset">
                    <Num value={form.offset} onChange={(v) => set('offset', v)} disabled={busy} />
                    <span style={hint}>periods of lag/lead from today to the window head (usually 0)</span>
                  </Row>
                  <Row label="Mode">
                    <select value={form.mode} disabled={busy} onChange={(e) => set('mode', e.target.value as Form['mode'])} style={input}>
                      <option value="Import">Import</option>
                      <option value="Hybrid" disabled={!hybridOk}>Hybrid (real-time){hybridOk ? '' : `: needs CL ≥ ${CL_HYBRID}`}</option>
                    </select>
                  </Row>
                  <div>
                    <button onClick={() => setAdvanced((a) => !a)} style={{ ...linkBtn }}>{advanced ? '▾' : '▸'} Advanced: detect data changes</button>
                    {advanced && (
                      <div className="pt-2">
                        <textarea value={form.polling} disabled={busy} onChange={(e) => set('polling', e.target.value)} rows={2}
                          placeholder={`an M expression returning a max watermark, e.g.  List.Max(Source[LastModified])`}
                          style={{ ...input, width: '100%', maxWidth: 'unset', fontFamily: 'var(--vscode-editor-font-family, monospace)', resize: 'vertical' }} />
                        <div style={hint}>Optional. Only re-imports a period when this value changed since the last refresh.</div>
                      </div>
                    )}
                  </div>
                  {!prereqsOk && <div style={{ color: 'var(--sem-warn)', fontSize: 11 }}>{clOk
                    ? 'Saving will create or repair the two parameters and append the range filter automatically. Use the actions above to review each change first.'
                    : `Raise the compatibility level to ${CL_MIN} or higher before saving.`}</div>}
                </div>
              )}
              <div className="flex items-center gap-2 pt-3">
                <button onClick={save} disabled={busy || !enabled} style={primaryBtn}>Save policy</button>
                {policy?.enabled && <button onClick={remove} disabled={busy} style={{ ...btn, color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }}>Remove policy</button>}
                <span style={hint}>{policy?.enabled ? 'A policy is configured for this table.' : 'No policy on this table yet.'}</span>
              </div>
            </Card>
            <div style={{ ...hint, fontSize: 10 }}>Incremental refresh here is pure model metadata: it configures the policy; it never runs a data refresh.</div>
          </aside>
        </div>
      </div>
    </div>
  );
}

// The M query lane: edit a partition's M or a shared expression/parameter with highlighting, offline
// format, live validity, and an Applied-Steps outline; Save routes through set_partition_m / update_named_expression
// (both doors + undo). Autocomplete + inferred-type hovers are the next increment (powerquery-language-services).
type MTarget = { kind: 'partition'; id: string; label: string; text: string } | { kind: 'expression'; id: string; label: string; name: string; text: string };
function MLane({ doc, table, onSaved, initialTarget }: { doc: DocModel; table: DocTable | null; onSaved: () => Promise<void> | void; initialTarget?: { id: string; nonce: number } | null }) {
  const targets = useMemo<MTarget[]>(() => {
    const ps: MTarget[] = (table?.partitions ?? []).filter((p) => p.sourceType === 'M')
      .map((p) => ({ kind: 'partition', id: `partition:${table!.name}/${p.name}`, label: `Partition · ${p.name}`, text: p.source ?? '' }));
    const es: MTarget[] = (doc.expressions ?? []).filter((e) => (e.kind || 'M') === 'M')
      .map((e) => ({ kind: 'expression', id: `expr:${e.name}`, label: `Expression · ${e.name}`, name: e.name, text: e.expression ?? '' }));
    return [...ps, ...es];
  }, [table, doc.expressions]);

  const [targetId, setTargetId] = useState('');
  // A tree jump names the exact partition ('partition:<Table>/<Name>' — the same id scheme as targets). Select
  // it; if it isn't there (its sourceType isn't M), say so plainly and fall back to the first target. Consumed
  // once per nav nonce so a later doc reload can't yank a selection the user has since changed.
  const consumedNav = useRef(0);
  useEffect(() => {
    if (!initialTarget || initialTarget.nonce === consumedNav.current) return;
    consumedNav.current = initialTarget.nonce;
    if (targets.some((t) => t.id === initialTarget.id)) { setTargetId(initialTarget.id); return; }
    const pname = initialTarget.id.slice(initialTarget.id.indexOf('/') + 1);
    setPqToast({ text: `Partition '${pname}' isn't an M partition. There's no M source to edit.`, tone: 'error' });
    if (targets.length) setTargetId(targets[0].id);
  }, [initialTarget, targets]);
  const target = useMemo(() => targets.find((t) => t.id === targetId) ?? targets[0] ?? null, [targets, targetId]);
  const [text, setText] = useState('');
  const [original, setOriginal] = useState('');
  const [validity, setValidity] = useState<{ ok: boolean; error?: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  // Transform surface: a click-to-select span for the editor + a bottom toast for step-added / kernel errors.
  const [selection, setSelection] = useState<{ from: number; to: number; nonce: number } | null>(null);
  const [pqToast, setPqToast] = useState<Toast | null>(null);
  useEffect(() => { if (pqToast?.tone === 'ok') { const id = window.setTimeout(() => setPqToast(null), 4000); return () => window.clearTimeout(id); } }, [pqToast]);
  const applyTransform = useCallback((m: string, stepName: string) => {
    setText(m);   // dirty exactly like a manual edit — Save stays the explicit act
    setPqToast({ text: `Step '${stepName}' added; applies at next refresh (the sample shows loaded data)`, tone: 'ok' });
  }, []);
  const failTransform = useCallback((error: string) => setPqToast({ text: error, tone: 'error' }), []);

  // The live data sample + column-op surface is owned by SamplePreview (below): it runs the read-only
  // EVALUATE TOPN against the loaded table and hosts the per-column transforms on the preview headers.
  const { conn } = useConnection();
  const sampleTable = table?.name ?? null;
  // Offline fallback columns for the sample headers (so column ops work with no live engine — generating M
  // needs no connection); SamplePreview overrides these with the loaded sample's live-typed columns.
  const docColumns = useMemo<GridCol[]>(
    () => (doc.columns ?? []).filter((c) => c.table === sampleTable).map((c) => ({ name: c.name, type: c.dataType })),
    [doc.columns, sampleTable],
  );

  // P4 — create a new shared expression / parameter (edit is the target dropdown + Save; this is the missing "C").
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const doCreate = async () => {
    const name = newName.trim();
    if (!name) return;
    setBusy(true); setMsg(null);
    try {
      await rpc('createNamedExpression', name, 'let\n    Source = ""\nin\n    Source');
      setCreating(false); setNewName('');
      await onSaved();                 // parent reloads the doc → targets include the new expression
      setTargetId(`expr:${name}`);     // …and select it for editing
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  // Duplicate / Reference a shared expression (create_named_expression). Duplicate copies the current text;
  // Reference creates `let Source = #"Name" in Source` pointing at the saved expression.
  const [refMode, setRefMode] = useState(false);
  const [refName, setRefName] = useState('');
  const selectExpr = async (name: string) => { await onSaved(); setTargetId(`expr:${name}`); };
  const doDuplicate = async () => {
    if (!target || target.kind !== 'expression') return;
    setBusy(true); setMsg(null);
    try { const name = `${target.name} (copy)`; await rpc('createNamedExpression', name, text); await selectExpr(name); }
    catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  const doReference = async () => {
    if (!target || target.kind !== 'expression') return;
    const name = refName.trim();
    if (!name) return;
    setBusy(true); setMsg(null);
    try {
      await rpc('createNamedExpression', name, `let\n    Source = ${mQuoteIdent(target.name)}\nin\n    Source`);
      setRefMode(false); setRefName('');
      await selectExpr(name);
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  // Load the target's M when the SELECTION changes (by id) — not on every doc reload, so an unrelated live change
  // can't clobber an in-progress edit. After a save the parent reloads; original is already updated so it's a no-op.
  useEffect(() => {
    const tx = targets.find((t) => t.id === target?.id)?.text ?? '';
    setText(tx); setOriginal(tx); setMsg(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.id]);

  useEffect(() => {
    let live = true;
    // Drive the strip from the SAME validator the inline squiggles use (syntax + duplicate identifiers), so a
    // green "✓ valid M" never sits next to red squiggles. ok only when there are no error/warning diagnostics.
    const id = window.setTimeout(async () => {
      const diags = (await mDiagnostics(text)).filter((d) => d.severity <= 2);
      if (!live) return;
      setValidity(diags.length === 0 ? { ok: true } : { ok: false, error: diags.length === 1 ? diags[0].message : `${diags.length} problems` });
    }, 350);
    return () => { live = false; window.clearTimeout(id); };
  }, [text]);
  const dirty = text !== original;

  const doFormat = async () => { setBusy(true); setMsg(null); const r = await formatM(text); setBusy(false); if (r.ok) setText(r.text); else setMsg('Format failed: ' + r.error); };
  const doSave = async () => {
    if (!target) return;
    setBusy(true); setMsg(null);
    try {
      if (target.kind === 'partition') await rpc('setPartitionM', target.id, text);
      else await rpc('updateNamedExpression', target.name, text);
      setOriginal(text); setMsg('✓ saved'); await onSaved();
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  if (!targets.length) return <div style={hint}>This table has no M partition (Direct Lake / calculated tables have no M), and the model has no shared M expressions.</div>;

  return (
    <div className="flex gap-3 items-start">
      <div className="flex-1 min-w-0 flex flex-col gap-2">
        <div className="flex items-center gap-2 flex-wrap">
          <select value={target?.id ?? ''} onChange={(e) => setTargetId(e.target.value)} style={input} title="Choose a partition or shared expression">
            {targets.map((t) => <option key={t.id} value={t.id}>{t.label}</option>)}
          </select>
          <button onClick={doFormat} disabled={busy} style={btn} title="Pretty-print the M (offline)">Format</button>
          <button onClick={doSave} disabled={busy || !dirty} style={primaryBtn}>Save</button>
          {dirty && <button onClick={() => setText(original)} disabled={busy} style={btn}>Revert</button>}
          {!creating ? (
            <button onClick={() => setCreating(true)} disabled={busy} style={btn} title="Create a new shared M expression / parameter">+ New</button>
          ) : (
            <>
              <input autoFocus value={newName} onChange={(e) => setNewName(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') void doCreate(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                placeholder="new expression name" style={{ ...input, maxWidth: 160 }} />
              <button onClick={doCreate} disabled={busy || !newName.trim()} style={primaryBtn}>Create</button>
              <button onClick={() => { setCreating(false); setNewName(''); }} disabled={busy} style={btn}>Cancel</button>
            </>
          )}
          {target?.kind === 'expression' && !creating && (
            <>
              <span style={{ width: 1, height: 16, background: 'var(--sem-border)' }} />
              <button onClick={doDuplicate} disabled={busy} style={btn} title="Copy this expression under a new name">Duplicate</button>
              {!refMode ? (
                <button onClick={() => { setRefMode(true); setRefName(`${target.name} (ref)`); }} disabled={busy} style={btn} title="Create a new expression that references this one (let Source = … in Source)">Reference…</button>
              ) : (
                <>
                  <input autoFocus value={refName} onChange={(e) => setRefName(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') void doReference(); else if (e.key === 'Escape') { setRefMode(false); setRefName(''); } }}
                    placeholder="reference name" style={{ ...input, maxWidth: 160 }} />
                  <button onClick={doReference} disabled={busy || !refName.trim()} style={primaryBtn}>Create reference</button>
                  <button onClick={() => { setRefMode(false); setRefName(''); }} disabled={busy} style={btn}>Cancel</button>
                </>
              )}
            </>
          )}
          {validity && <span style={{ fontSize: 10.5, color: validity.ok ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{validity.ok ? '✓ valid M' : (validity.error || 'parse error')}</span>}
          {msg && <span style={{ fontSize: 10.5, color: msg.startsWith('✓') ? 'var(--sem-good)' : 'var(--sem-bad)' }}>{msg}</span>}
        </div>
        <MEditor value={text} onChange={setText} minHeight={300} selection={selection ?? undefined} />
        <div style={{ ...hint, fontSize: 10 }}>Editing M is metadata-only: it changes the query definition, it never runs a refresh. Type to autocomplete the M standard library (Ctrl+Space); hover a name for its inferred type.</div>
        {sampleTable && (
          <SamplePreview tableName={sampleTable} docColumns={docColumns} mText={text} connected={!!conn?.connected} apply={applyTransform} fail={failTransform} />
        )}
      </div>
      <div style={{ width: 240 }} className="shrink-0">
        <AppliedStepsPanel text={text} onChange={setText}
          onSelect={(from, to) => setSelection((s) => ({ from, to, nonce: (s?.nonce ?? 0) + 1 }))}
          onError={failTransform} />
      </div>
      {pqToast && (
        <div className="fixed left-1/2 -translate-x-1/2 bottom-4 z-20 flex items-center gap-2 rounded-md border px-3 py-1.5 text-[11px] shadow-lg max-w-[560px]"
          style={{ background: 'var(--sem-surface)', borderColor: pqToast.tone === 'ok' ? 'var(--sem-good)' : 'var(--sem-bad)', color: 'var(--sem-fg)' }}>
          <span>{pqToast.text}</span>
          <button onClick={() => setPqToast(null)} style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
        </div>
      )}
    </div>
  );
}

// --- little building blocks ---
const input: React.CSSProperties = { background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '2px 6px', maxWidth: 220 };
const btn: React.CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '3px 10px', cursor: 'pointer' };
const primaryBtn: React.CSSProperties = { ...btn, background: 'var(--sem-accent-soft)', borderColor: 'var(--sem-accent)' };
const linkBtn: React.CSSProperties = { background: 'transparent', border: 'none', color: 'var(--sem-accent)', cursor: 'pointer', fontSize: 11, padding: 0 };
const hint: React.CSSProperties = { color: 'var(--sem-muted)', fontSize: 10.5 };

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="px-3 py-2 text-[12px] font-semibold" style={{ borderBottom: '1px solid var(--sem-border)' }}>{title}</div>
      <div className="px-3 py-2.5 text-[11px]">{children}</div>
    </div>
  );
}
function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="flex items-center gap-2 flex-wrap"><span className="shrink-0" style={{ color: 'var(--sem-muted)', width: 168 }}>{label}</span>{children}</div>;
}
function Num({ value, onChange, disabled }: { value: number; onChange: (v: number) => void; disabled?: boolean }) {
  return <input type="number" min={0} value={value} disabled={disabled} onChange={(e) => onChange(Math.max(0, parseInt(e.target.value || '0', 10)))} style={{ ...input, width: 64 }} />;
}
function GranSel({ value, onChange, disabled }: { value: Gran; onChange: (v: Gran) => void; disabled?: boolean }) {
  return <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value as Gran)} style={{ ...input, width: 96 }}>{GRANS.map((g) => <option key={g} value={g}>{g}</option>)}</select>;
}
function Check({ ok, label, detail, action, busy }: { ok: boolean; label: string; detail?: string; action?: { label: string; run: () => void }; busy?: boolean }) {
  return (
    <div className="flex items-start gap-2 py-1">
      <span style={{ color: ok ? 'var(--sem-good)' : 'var(--sem-warn)', fontWeight: 700, width: 14 }}>{ok ? '✓' : '!'}</span>
      <div className="flex-1 min-w-0">
        <div style={{ color: 'var(--sem-fg)' }}>{label}</div>
        {detail && <div style={hint}>{detail}</div>}
      </div>
      {action && <button onClick={action.run} disabled={busy} style={btn}>{action.label}</button>}
    </div>
  );
}
