import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, loadState, saveState } from './bridge';
import type { ColumnRow } from './wire';
import { MEditor } from './meditor';
import { formatM } from './mlang';
import { mDiagnostics } from './manalysis';
import { useConnection } from './connection';
import { gen, mQuoteIdent } from './mtransform';
import { SamplePreview, AppliedStepsPanel, sourceMName, type GridCol } from './pqtransforms';
import { DraftMark, EditMovedBanner, sameValue, useEditBase } from './editbase';
import { draftModel, draftStore, heldDraft } from './drafts';
import { OrphanDrafts } from './orphandrafts';
import { MenuPop } from './menupop';
import { isMContextCurrent, mContextToken, policyFetchAllowed, pollingExpressionForSave, reconcileExternalM, reconcileProfileSubject, reconcileSaveRevision, reconcileTableSelection } from './mcodelifecycle.mjs';

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
  wiredDateField?: string | null;   // the SOURCE field the partition's range filter compares (parsed engine-side); null if none
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

/** What a policy form holds, in the one shape the stale-copy guard compares: the form without its date column (the policy
 *  DTO does not echo it) plus the on/off switch. A table with no policy is the defaults, switched off. */
type PolicyFields = Omit<Form, 'dateColumn'> & { enabled: boolean };
function policyFields(p: RefreshPolicyInfo | null | undefined): PolicyFields {
  // The switch comes first in both shapes, the order the form's draft is built in: the draft store compares held values as JSON text.
  if (!p?.enabled) { const { dateColumn, ...rest } = DEFAULT_FORM; void dateColumn; return { enabled: false, ...rest }; }
  return {
    enabled: true,
    storePeriods: p.rollingWindowPeriods || DEFAULT_FORM.storePeriods,
    storeGranularity: (GRANS.includes(p.rollingWindowGranularity as Gran) ? p.rollingWindowGranularity : DEFAULT_FORM.storeGranularity) as Gran,
    refreshPeriods: p.incrementalPeriods || DEFAULT_FORM.refreshPeriods,
    refreshGranularity: (GRANS.includes(p.incrementalGranularity as Gran) ? p.incrementalGranularity : DEFAULT_FORM.refreshGranularity) as Gran,
    offset: p.incrementalPeriodsOffset || 0,
    mode: p.mode === 'Hybrid' ? 'Hybrid' : 'Import',
    polling: p.pollingExpression ?? '',
  };
}

/** The policy the form started from, as the fields a save replaces, for the write to name. The engine compares each inside its own write. */
const policyFence = (p: RefreshPolicyInfo | null | undefined) => JSON.stringify({
  enabled: !!p?.enabled, mode: p?.mode ?? null,
  rollingWindowGranularity: p?.rollingWindowGranularity ?? null, rollingWindowPeriods: p?.rollingWindowPeriods ?? null,
  incrementalGranularity: p?.incrementalGranularity ?? null, incrementalPeriods: p?.incrementalPeriods ?? null,
  incrementalPeriodsOffset: p?.incrementalPeriodsOffset ?? null, pollingExpression: p?.pollingExpression ?? null,
});

// What the person typed into a table's policy form and has not saved outlives the tab (join 11, the drafts class, drafts.ts): a tab
// switch, another table, a restarted engine and a reload keep it, per table. The policy fields are held by the stale-copy guard with the
// policy they were typed over; a date column the person picked is held beside them, since the policy DTO does not echo it. A draft ends
// on a save the engine took, Discard, Use the new one, and Remove policy.
const policyScope = (table: string) => 'mcode|policy|' + table;
/** Where the M text typed into one query is held (F12): the query's id, so another query or table never shows it. */
const mQueryScope = (targetId: string) => `mcode|query|${targetId}`;
function holdPolicyDate(table: string, column: string) { const m = draftModel(); if (m && table) draftStore.set(m, policyScope(table), 'dateColumn', { value: column, base: null }); }
function endPolicyDraft(table: string, m = draftModel()) { if (!m || !table) return; draftStore.clear(m, policyScope(table)); draftStore.clear(m, policyScope(table), 'dateColumn'); }
const heldPolicyDate = (table: string) => heldDraft<string>(policyScope(table), 'dateColumn')?.value;
/** The form a held draft opens on: the held fields over the defaults, and the held date column. */
function heldPolicyForm(fields: PolicyFields | undefined, date: string | undefined): Form {
  if (!fields) return { ...DEFAULT_FORM, dateColumn: date ?? '' };
  const { enabled: _on, ...rest } = fields; void _on;
  return { ...DEFAULT_FORM, ...rest, dateColumn: date ?? '' };
}

type Toast = { text: string; tone: 'ok' | 'error' };
type RefreshBusyOp = 'save-policy' | 'remove-policy' | 'create-params' | 'add-filter';
type BusyMap = Record<string, RefreshBusyOp>;
const PARAMS_BUSY_KEY = 'prerequisite:parameters';

export function MCodeView({ navTarget }: { navTarget?: { table: string; partitionId?: string; nonce: number } | null } = {}) {
  const { session } = useConnection();
  const [doc, setDoc] = useState<DocModel | null>(null);
  const [err, setErr] = useState<string | null>(null);
  // The table the tab was on is remembered per model FILE when one is known, so a restarted engine (a new session id) opens on the same
  // table and the draft held for it (join 11, the drafts class). With no model file, per session as before.
  const tableStateKey = 'pqTable:' + (draftModel() || session?.sessionId || '');
  const [selected, setSelectedState] = useState<string>(() => loadState<string>(tableStateKey, ''));
  // Table-context token, updated SYNCHRONOUSLY with every selection change: a passive effect would leave a
  // window (setSelected(B) → before React re-renders) where an in-flight save/remove continuation still sees A
  // and applies A's state. Every selection path funnels through this setter.
  const selectedRef = useRef(loadState<string>(tableStateKey, ''));
  // The held draft of the table the tab opens on: the form starts from it, open, and the server read below stands down.
  const heldAtOpen = useRef<{ fields?: PolicyFields; date?: string } | null>(null);
  if (heldAtOpen.current === null) heldAtOpen.current = { fields: heldDraft<PolicyFields>(policyScope(selectedRef.current))?.value, date: heldPolicyDate(selectedRef.current) };
  const policyDraftDirtyRef = useRef(!!heldAtOpen.current.fields);
  const setSelected = useCallback((t: string) => {
    if (selectedRef.current !== t) policyDraftDirtyRef.current = false;
    selectedRef.current = t; setSelectedState(t);
  }, []);
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
  const [policyOf, setPolicyOf] = useState('');   // the table `policy` was last read for (the live read, kept even while the form holds typed values)
  const [form, setForm] = useState<Form>(() => heldPolicyForm(heldAtOpen.current?.fields, heldAtOpen.current?.date));
  const [enabled, setEnabled] = useState(!!heldAtOpen.current.fields?.enabled);
  const [advanced, setAdvanced] = useState(false);
  const [refreshOpen, setRefreshOpen] = useState(!!(heldAtOpen.current.fields || heldAtOpen.current.date));
  // Bumped when the person moves policy words typed for a table that is not here now onto a table (OrphanDrafts below): the form reads what is held again.
  const [policyMoved, setPolicyMoved] = useState(0);
  const refreshPanelRef = useRef<HTMLElement>(null);
  // Resource-keyed busy map. Policy work, shared parameters, and a table's range filter can report independently;
  // the ref closes the same-tick double-click window before React commits the disabled state.
  const [busyByResource, setBusyByResource] = useState<BusyMap>({});
  const busyRef = useRef<BusyMap>({});
  const [actionErrors, setActionErrors] = useState<Record<string, string>>({});
  const beginBusy = useCallback((key: string, op: RefreshBusyOp) => {
    if (busyRef.current[key]) return false;
    const next = { ...busyRef.current, [key]: op };
    busyRef.current = next; setBusyByResource(next);
    return true;
  }, []);
  const endBusy = useCallback((key: string) => {
    const next = { ...busyRef.current };
    delete next[key]; busyRef.current = next; setBusyByResource(next);
  }, []);
  const clearActionError = useCallback((key: string) => setActionErrors((errors) => {
    if (!(key in errors)) return errors;
    const next = { ...errors }; delete next[key]; return next;
  }), []);
  const setActionError = useCallback((key: string, error: unknown) => {
    const text = String((error as Error)?.message ?? error);
    setActionErrors((errors) => ({ ...errors, [key]: text }));
  }, []);
  // Policy actions lock the table selector while they run. The captured table + synchronous selection ref remain
  // a second guard so a delayed continuation can never write one table's policy state into another table's UI.
  const [toast, setToast] = useState<Toast | null>(null);

  useEffect(() => { saveState(tableStateKey, selected); }, [tableStateKey, selected]);
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
  // Heal the selection: keep it if still present, else pick the first eligible table. A leftover name from the
  // previous model must not keep firing requests (and must not replace this tab with the engine error text).
  // Not before the document has loaded: a tree jump mounts this tab with no document yet, and healing against an
  // empty list wiped the jump's table, so the document then picked the model's first table (UAT-S07b-2-001).
  useEffect(() => {
    const next = reconcileTableSelection(selected, doc ? tables.map((t) => t.name) : null);
    if (next !== selected) setSelected(next);
  }, [doc, tables, selected]);
  // The honesty layer over that heal-snap: a tree jump to a table this tab can't edit (calculated — its logic is
  // DAX) used to be silently snapped back to the first eligible table, so the click looked like it did nothing.
  // Consumed once per navigation (whether the doc was already loaded or arrives after), never on doc reloads.
  useEffect(() => {
    if (!pendingNav || !doc) return;
    if (!tables.some((t) => t.name === pendingNav.table)) {
      const raw = doc.tables.find((t) => t.name === pendingNav.table);
      setToast({
        text: raw?.isCalculated
          ? `'${pendingNav.table}' has no M Code to edit because it is calculated from other model data.`
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
  // Eligible date columns: data columns loaded from the source. Calculated columns and columns with no
  // source name cannot be filtered in M, so offering them only produces a save the engine then rejects.
  const tableColumns = useMemo(() => (doc?.columns ?? []).filter((c) => c.table === selected), [doc, selected]);
  const eligibleDateColumns = useMemo(
    () => tableColumns.filter((c) => !!sourceMName(c) && /date|time/i.test(c.dataType)).map((c) => c.name),
    [tableColumns]
  );
  const eligibleAllColumns = useMemo(
    () => tableColumns.filter((c) => !!sourceMName(c)).map((c) => c.name),
    [tableColumns]
  );
  const dateColumns = eligibleDateColumns;
  const allColumns = eligibleAllColumns;

  // Has the USER picked a date column for this table? A wired-field seed may only override the default/heal
  // seed, never a human choice — reset per table, set by the selector's onChange.
  const dateColTouched = useRef(false);
  const dateRestored = useRef(false);
  if (!dateRestored.current) { dateRestored.current = true; if (heldAtOpen.current.date) dateColTouched.current = true; }

  // Load the selected table's policy → seed the form.
  useEffect(() => {
    if (!policyFetchAllowed(selected, tables.map((t) => t.name))) return;
    // A model change can refresh this document while the person is typing in the policy form. Keep that draft;
    // replacing it from the server here made Save policy silently throw away a value that had just been entered.
    if (policyDraftDirtyRef.current) return;
    // A table with a held draft opens on it, not on the server's copy (the guard below asks if the policy moved meanwhile).
    const held = heldDraft<PolicyFields>(policyScope(selected)), heldDate = heldPolicyDate(selected);
    dateColTouched.current = !!heldDate;
    if (held) {
      policyDraftDirtyRef.current = true;
      setEnabled(held.value.enabled); setForm(heldPolicyForm(held.value, heldDate));
      return;
    }
    if (!heldDate) dateColTouched.current = false;
    let cancelled = false;
    (async () => {
      try {
        const p = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + selected);
        if (cancelled || policyDraftDirtyRef.current) return;
        setPolicy(p);
        setEnabled(!!p?.enabled);
        setForm((f) => p?.enabled ? {
          dateColumn: heldDate ?? f.dateColumn, // dateColumn isn't echoed by the policy DTO; keep current / default below
          storePeriods: p.rollingWindowPeriods || DEFAULT_FORM.storePeriods,
          storeGranularity: (GRANS.includes(p.rollingWindowGranularity as Gran) ? p.rollingWindowGranularity : DEFAULT_FORM.storeGranularity) as Gran,
          refreshPeriods: p.incrementalPeriods || DEFAULT_FORM.refreshPeriods,
          refreshGranularity: (GRANS.includes(p.incrementalGranularity as Gran) ? p.incrementalGranularity : DEFAULT_FORM.refreshGranularity) as Gran,
          offset: p.incrementalPeriodsOffset || 0,
          mode: (p.mode === 'Hybrid' ? 'Hybrid' : 'Import'),
          polling: p.pollingExpression ?? '',
        } : (heldDate ? { ...DEFAULT_FORM, dateColumn: heldDate } : DEFAULT_FORM));
        setErr(null);
      } catch (e) {
        if (!cancelled) setErr('Could not load refresh settings for this table. Pick another table and try again.');
        void e;
      }
    })();
    return () => { cancelled = true; };
  }, [selected, tables, policyMoved]);

  // The live policy is read on every model change even while the form holds typed values (the read above stands down then),
  // so the guard below can see that the assistant changed the policy under a typed form (UAT stale-copy class, 2 Oct).
  useEffect(() => {
    if (!policyFetchAllowed(selected, tables.map((t) => t.name))) return;
    let cancelled = false;
    (async () => {
      try {
        const p = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + selected);
        if (!cancelled) { setPolicy(p); setPolicyOf(selected); }
      } catch { /* the read above reports it */ }
    })();
    return () => { cancelled = true; };
  }, [selected, tables]);

  // Default the date column once columns are known (prefer a date/time column).
  useEffect(() => {
    setForm((f) => f.dateColumn && (dateColumns.includes(f.dateColumn) || allColumns.includes(f.dateColumn)) ? f : { ...f, dateColumn: dateColumns[0] ?? allColumns[0] ?? '' });
  }, [dateColumns, allColumns]);

  // The EFFECTIVE wired column: the policy's wired SOURCE field resolved back through the table's columns via
  // the same sourceMName rule. Only a UNIQUE mapping counts — ambiguity or no match is no claim (the note under
  // the selector still explains).
  const wiredColumn = useMemo(() => {
    const wf = policy?.wiredDateField;
    if (!wf) return null;
    const matches = (doc?.columns ?? []).filter((c) => c.table === selected && sourceMName(c) === wf);
    return matches.length === 1 ? matches[0].name : null;
  }, [policy, doc, selected]);

  // Picker options: the date/time columns (a creation-time type heuristic) PLUS the wired column even when its
  // type falls outside it (an integer date-key is, by definition, THE valid range-filter column here) — without
  // it the form could never resubmit the only column the engine accepts, so unrelated policy edits were unsavable.
  const dateColumnOptions = useMemo(() => {
    const base = dateColumns.length ? dateColumns : allColumns;
    return wiredColumn && !base.includes(wiredColumn) ? [...base, wiredColumn] : base;
  }, [dateColumns, allColumns, wiredColumn]);

  // A wired policy overrides the default/heal guess: seed an UNTOUCHED selector with the wired column so an
  // unrelated policy edit round-trips instead of failing on the heal-seeded first column. Never a user choice.
  useEffect(() => {
    if (!wiredColumn || dateColTouched.current) return;
    setForm((f) => f.dateColumn === wiredColumn ? f : { ...f, dateColumn: wiredColumn });
  }, [wiredColumn]);

  const set = <K extends keyof Form>(k: K, v: Form[K]) => {
    policyDraftDirtyRef.current = true;
    setForm((f) => ({ ...f, [k]: v }));
  };

  // The form saves a whole policy from the copy it holds. Where that copy started is the guard's business: it follows a
  // live change while nothing is typed, asks (Keep mine / Use the new one) when words are typed, and Save re-reads first.
  const policyRaw = useRef<RefreshPolicyInfo | null>(null);   // the policy the last Save read, as the engine sent it
  const policyGuard = useEditBase<PolicyFields>({
    // The scope changes when words were moved onto a table, so the guard opens again on what is held, over the value it was typed over (and asks if that moved).
    current: policyOf === selected ? policyFields(policy) : undefined, scope: JSON.stringify([selected, policyMoved]), persist: selected ? { scope: policyScope(selected) } : undefined,
    draft: { enabled, storePeriods: form.storePeriods, storeGranularity: form.storeGranularity, refreshPeriods: form.refreshPeriods,
      refreshGranularity: form.refreshGranularity, offset: form.offset, mode: form.mode, polling: form.polling },
    editing: true,
    reseed: ({ enabled: on, ...fields }) => { policyDraftDirtyRef.current = false; setEnabled(on); setForm((f) => ({ ...f, ...fields })); },
    readNow: async () => { const info = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + selectedRef.current); policyRaw.current = info; return policyFields(info); },
  });
  const policyGuardRef = useRef(policyGuard); policyGuardRef.current = policyGuard;

  const createParams = useCallback(async () => {
    if (busyRef.current[PARAMS_BUSY_KEY] || Object.keys(busyRef.current).some((key) => key.startsWith('policy:'))) return;
    if (!beginBusy(PARAMS_BUSY_KEY, 'create-params')) return;
    clearActionError(PARAMS_BUSY_KEY);
    try {
      // An update names the expression the page showed as the one it replaces; the engine compares it inside its write.
      if (!isParamExpr(rangeStart)) await (rangeStart ? rpc('updateNamedExpression', 'RangeStart', PARAM_M('2020, 1, 1, 0, 0, 0'), 'human', rangeStart.expression ?? '') : rpc('createNamedExpression', 'RangeStart', PARAM_M('2020, 1, 1, 0, 0, 0')));
      if (!isParamExpr(rangeEnd)) await (rangeEnd ? rpc('updateNamedExpression', 'RangeEnd', PARAM_M('2021, 1, 1, 0, 0, 0'), 'human', rangeEnd.expression ?? '') : rpc('createNamedExpression', 'RangeEnd', PARAM_M('2021, 1, 1, 0, 0, 0')));
      setToast({ text: '✓ RangeStart / RangeEnd parameters are ready', tone: 'ok' });
      await loadDoc();
    } catch (e) { setActionError(PARAMS_BUSY_KEY, e); }
    finally { endBusy(PARAMS_BUSY_KEY); }
  }, [rangeStart, rangeEnd, loadDoc, beginBusy, clearActionError, setActionError, endBusy]);

  const addRangeFilter = useCallback(async () => {
    if (!table || !form.dateColumn || partitionFilters) return;
    const busyKey = `prerequisite:filter:${table.name}`;
    if (busyRef.current[busyKey] || busyRef.current[`policy:${table.name}`]) return;
    if (!beginBusy(busyKey, 'add-filter')) return;
    clearActionError(busyKey);
    try {
      const partition = table.partitions.find((p) => p.sourceType.toLowerCase() === 'm');
      if (!partition) throw new Error(`'${table.name}' has no editable M query to filter.`);
      // The filter is M, so it must use the PARTITION-OUTPUT name (SourceColumn) via the SHARED resolver — never
      // the model column name. If the source name can't be proven (calculated, or a wire-declared null/empty
      // SourceColumn), refuse: writing [ModelName] here is exactly the M the engine Save path now rejects.
      const col = (doc?.columns ?? []).find((c) => c.table === table.name && c.name === form.dateColumn);
      const mDateCol = col ? sourceMName(col) : undefined;
      if (!mDateCol) throw new Error(`'${form.dateColumn}' has no source column to filter on because it is calculated or not loaded by the M query. Pick a date column that comes from the source.`);
      const next = await gen.incrementalRefreshFilter(partition.source ?? '', mDateCol);
      if (!next.ok) throw new Error(next.error);
      // The filter was built from the query text the page showed, and the write names that text as the one it replaces.
      await rpc('setPartitionM', `partition:${table.name}/${partition.name}`, next.m, 'human', partition.source ?? '');
      setToast({ text: `✓ added the RangeStart / RangeEnd filter to query '${partition.name}'`, tone: 'ok' });
      await loadDoc();
    } catch (e) { setActionError(busyKey, e); }
    finally { endBusy(busyKey); }
  }, [table, doc, form.dateColumn, partitionFilters, loadDoc, beginBusy, clearActionError, setActionError, endBusy]);

  const save = useCallback(async () => {
    if (!selected) return;
    const t = selected;   // the table this save is FOR, whatever the selector does meanwhile
    const busyKey = `policy:${t}`;
    const filterKey = `prerequisite:filter:${t}`;
    const errorKey = `save-policy:${t}`;
    if (busyRef.current[busyKey] || busyRef.current[PARAMS_BUSY_KEY] || busyRef.current[filterKey]) return;
    if (!refreshWindowFitsArchive(form.storePeriods, form.storeGranularity, form.refreshPeriods, form.refreshGranularity)) {
      setActionError(errorKey, new Error(`The refresh window (${periodsLabel(form.refreshPeriods, form.refreshGranularity)}) is wider than the store window (${periodsLabel(form.storePeriods, form.storeGranularity)}). Shrink refresh, or store at least as far back as you refresh.`));
      return;
    }
    if (!beginBusy(busyKey, 'save-policy')) return;
    clearActionError(errorKey);
    const startedOn = draftModel();   // the model this Save was pressed on: its draft is ended under it
    const sent = { enabled, storePeriods: form.storePeriods, storeGranularity: form.storeGranularity, refreshPeriods: form.refreshPeriods,
      refreshGranularity: form.refreshGranularity, offset: form.offset, mode: form.mode, polling: form.polling };   // the words this Save carries
    try {
      // The policy is read again first: a form whose starting point is older than the live policy writes nothing until the person chose.
      if (!(await policyGuardRef.current.beforeSave())) return;
      const r = await rpc<{ warning?: string | null }>('setIncrementalRefreshPolicy', 'table:' + t, form.dateColumn || null,
        form.storePeriods, form.storeGranularity, form.refreshPeriods, form.refreshGranularity, form.offset,
        form.mode, pollingExpressionForSave(form.polling), true, 'human', policyFence(policyRaw.current));
      // The engine can save WITH an advisory (e.g. the wired field matches no column's source name) — show it,
      // don't swallow it: both doors carry the same honesty.
      setToast({ text: r?.warning ? `✓ policy saved. ${r.warning}` : '✓ incremental refresh policy saved', tone: 'ok' });
      await loadDoc();
      const p = await rpc<RefreshPolicyInfo>('getIncrementalRefreshPolicy', 'table:' + t);
      // R-B: the typed form is the policy now, and its draft ends, only when the words in it are still exactly the ones the engine took. Words typed while the Save was on its way stay in the form,
      // held over the policy just saved, and the form is not reset from the server under them.
      const typedSince = selectedRef.current === t && !sameValue(policyGuardRef.current.draftNow(), sent);
      if (typedSince) policyGuardRef.current.saved(sent, policyFields(p));
      else endPolicyDraft(t, startedOn);
      if (selectedRef.current === t) {
        setPolicy(p);
        if (!typedSince) {
          setEnabled(!!p?.enabled);
          setForm((f) => ({ ...f,
            storePeriods: p.rollingWindowPeriods || DEFAULT_FORM.storePeriods,
            storeGranularity: (GRANS.includes(p.rollingWindowGranularity as Gran) ? p.rollingWindowGranularity : DEFAULT_FORM.storeGranularity) as Gran,
            refreshPeriods: p.incrementalPeriods || DEFAULT_FORM.refreshPeriods,
            refreshGranularity: (GRANS.includes(p.incrementalGranularity as Gran) ? p.incrementalGranularity : DEFAULT_FORM.refreshGranularity) as Gran,
            offset: p.incrementalPeriodsOffset || 0,
            mode: p.mode === 'Hybrid' ? 'Hybrid' : 'Import',
            polling: p.pollingExpression ?? '',
          }));
          policyDraftDirtyRef.current = false;
        }
      }
    } catch (e) { if (!(await policyGuardRef.current.refused(e))) setActionError(errorKey, e); }
    finally { endBusy(busyKey); }
  }, [selected, form, loadDoc, beginBusy, clearActionError, setActionError, endBusy]);

  const remove = useCallback(async () => {
    if (!selected) return;
    const t = selected;
    const busyKey = `policy:${t}`;
    const errorKey = `remove-policy:${t}`;
    if (busyRef.current[PARAMS_BUSY_KEY] || busyRef.current[`prerequisite:filter:${t}`]) return;
    if (!beginBusy(busyKey, 'remove-policy')) return;
    clearActionError(errorKey);
    try {
      await rpc('removeIncrementalRefreshPolicy', 'table:' + t);
      setToast({ text: '✓ incremental refresh policy removed', tone: 'ok' });
      endPolicyDraft(t);   // the policy is gone, and with it what was typed over it
      if (selectedRef.current === t) {
        policyDraftDirtyRef.current = false;
        setEnabled(false); setPolicy(null); setForm(DEFAULT_FORM);
      }
      // The form now stands on "no policy": that is its starting point, so nothing typed over the removed policy is asked about or held.
      if (selectedRef.current === t) policyGuardRef.current.rebase(policyFields(null));
      await loadDoc();
    } catch (e) { setActionError(errorKey, e); }
    finally { endBusy(busyKey); }
  }, [selected, loadDoc, beginBusy, clearActionError, setActionError, endBusy]);

  const configureRefresh = useCallback(() => {
    setRefreshOpen(true);
    window.setTimeout(() => refreshPanelRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 0);
  }, []);

  if (!doc) return <div className="p-6 text-xs" style={{ color: 'var(--sem-muted)' }}>Loading model…</div>;
  if (!tables.length) return <div className="p-6 text-xs" style={{ color: 'var(--sem-muted)' }}>No import/query tables in this model. Incremental refresh applies to tables loaded by a query (M).</div>;

  const clOk = cl >= CL_MIN;
  const hybridOk = cl >= CL_HYBRID;
  const prereqsOk = clOk && paramsOk && partitionFilters;
  const policyBusyKey = `policy:${selected}`;
  const filterBusyKey = `prerequisite:filter:${selected}`;
  const policyBusy = busyByResource[policyBusyKey];
  const paramsBusy = busyByResource[PARAMS_BUSY_KEY];
  const filterBusy = busyByResource[filterBusyKey];
  const policyBlocked = !!policyBusy || !!paramsBusy || !!filterBusy;
  // The quiet mark and Discard: this table holds typed policy values (or a picked date column) that are not saved.
  const policyDrafted = policyGuard.drafted || heldPolicyDate(selected) !== undefined;
  const discardPolicy = () => {
    const { enabled: on, ...fields } = policyFields(policy);
    endPolicyDraft(selected);
    policyDraftDirtyRef.current = false; dateColTouched.current = false;
    setEnabled(on); setForm((f) => ({ ...f, ...fields, dateColumn: wiredColumn ?? dateColumnOptions[0] ?? '' }));
  };
  const paramsWillCreate = !rangeStart || !rangeEnd;
  const paramsWillUpdate = (!!rangeStart && !isParamExpr(rangeStart)) || (!!rangeEnd && !isParamExpr(rangeEnd));
  const paramsRunningLabel = paramsWillCreate && paramsWillUpdate
    ? 'Creating and updating…'
    : paramsWillUpdate ? 'Updating…' : 'Creating…';
  const policyActivity = policyBusy === 'save-policy'
    ? 'saving the policy'
    : policyBusy === 'remove-policy' ? 'removing the policy' : null;
  const paramsActivity = paramsBusy === 'create-params'
    ? (paramsWillCreate && paramsWillUpdate
      ? 'creating and updating the parameters'
      : paramsWillUpdate ? 'updating the parameters' : 'creating the parameters')
    : null;
  const filterActivity = filterBusy === 'add-filter' ? 'adding the range filter' : null;
  const prerequisiteActivity = paramsActivity ?? filterActivity;
  const policyActionsBusyReason = prerequisiteActivity
    ? `Policy actions are unavailable while ${prerequisiteActivity}.`
    : policyActivity ? `Other policy actions are unavailable while ${policyActivity}.` : null;
  const savedPolicyOn = !!policy?.enabled;
  const policyWindowSummary = savedPolicyOn
    ? `store ${periodsLabel(policy!.rollingWindowPeriods, policy!.rollingWindowGranularity)} · refresh ${periodsLabel(policy!.incrementalPeriods, policy!.incrementalGranularity)}`
    : 'no policy configured';
  const policySummary = `${savedPolicyOn ? 'On' : 'Off'} · ${policyWindowSummary}`;
  const policyDirty = enabled !== savedPolicyOn || (enabled && (
    !savedPolicyOn
    || form.storePeriods !== policy?.rollingWindowPeriods
    || form.storeGranularity !== policy?.rollingWindowGranularity
    || form.refreshPeriods !== policy?.incrementalPeriods
    || form.refreshGranularity !== policy?.incrementalGranularity
    || form.offset !== policy?.incrementalPeriodsOffset
    || form.mode !== (policy?.mode === 'Hybrid' ? 'Hybrid' : 'Import')
    || form.polling.trim() !== (policy?.pollingExpression ?? '').trim()
    || (!!wiredColumn && form.dateColumn !== wiredColumn)
  ));

  return (
    // One Visual Language (23 September): the scroll box is the page grid. The M query rows, the editor and its
    // Applied steps, the preview and Incremental refresh are cards on it, each on whole columns.
    <div className="h-full flex flex-col">
      {/* gridAutoRows: this scroll box is also a grid with a fixed height. Without it a card that clips its content (the
          Incremental refresh card) is sized to nothing and its controls can never be scrolled to (UAT-J9-S10a-004). */}
      <div className="flex-1 min-h-0 overflow-auto relative sem-page" style={{ gridAutoRows: 'max-content' }}>
        {err && (
          <div className="xe-error" role="alert">{err}</div>
        )}
        {toast && (
          <div className="absolute left-1/2 -translate-x-1/2 bottom-3 z-10 flex items-center gap-2 rounded-(--sem-radius-control) border px-3 py-1 sem-text-meta"
            style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)', color: 'var(--sem-fg)', width: 'max-content' }}>
            <span className="xe-msg" style={{ '--xe-dot': toast.tone === 'ok' ? 'var(--sem-good)' : 'var(--sem-bad)' } as React.CSSProperties}>{toast.text}</span>
            <button onClick={() => setToast(null)} style={{ background: 'transparent', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
          </div>
        )}

        <MLane
          doc={doc} table={table} tables={tables} selectedTable={selected} onSelectTable={setSelected}
          tableSwitchDisabled={policyBlocked} onToast={setToast} onSaved={loadDoc} initialTarget={mNav}
        />

          <section ref={refreshPanelRef} className="sem-card sem-card-flush scroll-mt-3" aria-label="Incremental refresh settings">
            <div className="sem-card-head flex-wrap">
              <span className="sem-card-title">Incremental refresh</span>
              {/* Instrument: the state is a word, not a coloured pill. */}
              <span className="sem-text-meta" style={{ color: 'var(--sem-fg)' }}>{savedPolicyOn ? 'On' : 'Off'}</span>
              <span className="sem-text-meta">{policyWindowSummary}</span>
              <span className={'sem-text-meta xe-dot ' + (prereqsOk ? 'xe-dot-good' : 'xe-dot-warn')}>{prereqsOk ? 'Setup healthy' : 'Setup needs attention'}</span>
              {policyDirty && <span className="xe-msg xe-msg-warn" style={{  }}>Unsaved changes</span>}
              <button className="sem-btn sem-btn-sm ml-auto" onClick={() => setRefreshOpen((open) => !open)} aria-expanded={refreshOpen}>
                Configure {refreshOpen ? '▴' : '▾'}
              </button>
            </div>
            {/* Policy words are held by the table's NAME. When the assistant renames or removes that table the words are not dropped, and not shown on the table the tab
                falls back to: they are listed here, and the person chooses (join 11 solui, R-B). */}
            <div className="px-4">
              <OrphanDrafts prefix="mcode|policy|" live={doc ? tables.map((t) => t.name) : null} what="table"
                nameOf={(name) => name} targetsFor={() => tables.map((t) => ({ id: t.name, label: t.name }))}
                onMoved={() => { policyDraftDirtyRef.current = false; setPolicyMoved((n) => n + 1); setRefreshOpen(true); }} />
            </div>

            {refreshOpen && (
              <div style={{ borderTop: '1px solid var(--sem-border)' }}>
                <section className="px-4 py-3" aria-labelledby="refresh-setup-heading">
                  <div id="refresh-setup-heading" className="mb-2 text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Setup</div>
                  <div className="divide-y" style={{ borderColor: 'var(--sem-border)' }}>
                    <Check ok={clOk} label={clOk ? 'Incremental refresh is available for this model' : 'This model needs an update before incremental refresh is available'}
                      detail={clOk ? 'The model supports incremental refresh policies.' : 'Update the model settings before saving a policy.'} />
                    <Check ok={paramsOk} label="RangeStart and RangeEnd date/time parameters exist"
                      detail={paramsOk ? 'Both parameters are ready.' : (rangeStart || rangeEnd ? 'One or both values need to be repaired as M parameters.' : 'The two M parameters have not been created yet.')}
                      action={!paramsOk ? { label: 'Create or update parameters', run: createParams, running: paramsBusy === 'create-params', runningLabel: paramsRunningLabel, error: actionErrors[PARAMS_BUSY_KEY] } : undefined}
                      busy={!!paramsBusy || !!policyBusy}
                      busyReason={policyActivity
                        ? `This setup action is unavailable while ${policyActivity}.`
                        : paramsActivity ? `Other actions are unavailable while ${paramsActivity}.` : null} />
                    <Check ok={partitionFilters} label="The query filters rows using RangeStart and RangeEnd"
                      detail={partitionFilters ? 'The range filter is ready.' : `The filter will use [${form.dateColumn || 'Date'}] between RangeStart and RangeEnd.`}
                      action={!partitionFilters && form.dateColumn ? { label: 'Add range filter', run: addRangeFilter, running: filterBusy === 'add-filter', runningLabel: 'Adding…', error: actionErrors[filterBusyKey] } : undefined}
                      busy={!!filterBusy || !!policyBusy}
                      busyReason={policyActivity
                        ? `This setup action is unavailable while ${policyActivity}.`
                        : filterActivity ? `Other actions are unavailable while ${filterActivity}.` : null} />
                  </div>
                </section>

                <section className="px-4 py-3" style={{ borderTop: '1px solid var(--sem-rule)' }} aria-labelledby="refresh-policy-heading">
                  <div className="flex items-center gap-3 flex-wrap">
                    <div id="refresh-policy-heading" className="text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Policy</div>
                    <label className="flex items-center gap-2 text-(length:--sem-text-label)">
                      <input type="checkbox" checked={enabled} disabled={!!policyBusy} onChange={(e) => { policyDraftDirtyRef.current = true; setEnabled(e.target.checked); }} />
                      Configure incremental refresh for <b>{selected}</b>
                    </label>
                  </div>

                  {enabled && (
                    <div className="mt-3 grid grid-cols-1 gap-x-4 gap-y-3 md:grid-cols-2 xl:grid-cols-3">
                      <PolicyField label="Date column" hintText={policy?.wiredDateField
                        ? `This query already filters on [${policy.wiredDateField}]. Edit the M or remove the range filter before moving it.`
                        : (dateColumnOptions.length === 0
                          ? 'No date column on this table is loaded from the source. Calculated columns and columns with no source name cannot be used.'
                          : undefined)}>
                        <select className="sem-field sem-field-sm" value={form.dateColumn} disabled={!!policyBusy || dateColumnOptions.length === 0} onChange={(e) => { dateColTouched.current = true; set('dateColumn', e.target.value); holdPolicyDate(selected, e.target.value); }} style={{ width: '100%', maxWidth: 'unset' }}>
                          {dateColumnOptions.length === 0 && <option value="">(no source-loaded date column)</option>}
                          {dateColumnOptions.map((c) => <option key={c} value={c}>{c}</option>)}
                        </select>
                      </PolicyField>
                      <PolicyField label="Store rows from the past" hintText="The archive window kept in the model.">
                        <div className="grid grid-cols-[72px_minmax(0,1fr)] gap-2">
                          <Num value={form.storePeriods} onChange={(v) => set('storePeriods', v)} disabled={!!policyBusy} />
                          <GranSel value={form.storeGranularity} onChange={(v) => set('storeGranularity', v)} disabled={!!policyBusy} />
                        </div>
                      </PolicyField>
                      <PolicyField label="Refresh rows from the past" hintText="The recent window loaded again during refresh.">
                        <div className="grid grid-cols-[72px_minmax(0,1fr)] gap-2">
                          <Num value={form.refreshPeriods} onChange={(v) => set('refreshPeriods', v)} disabled={!!policyBusy} />
                          <GranSel value={form.refreshGranularity} onChange={(v) => set('refreshGranularity', v)} disabled={!!policyBusy} />
                        </div>
                      </PolicyField>
                      <PolicyField label="Offset" hintText="Periods of lag or lead from today. Usually 0.">
                        <Num value={form.offset} onChange={(v) => set('offset', v)} disabled={!!policyBusy} />
                      </PolicyField>
                      <PolicyField label="Mode" hintText={hybridOk ? 'Hybrid is available for this model.' : 'Hybrid is not available for this model.'}>
                        <select className="sem-field sem-field-sm" value={form.mode} disabled={!!policyBusy} onChange={(e) => set('mode', e.target.value as Form['mode'])} style={{ width: '100%', maxWidth: 'unset' }}>
                          <option value="Import">Import</option>
                          <option value="Hybrid" disabled={!hybridOk}>Hybrid (real-time)</option>
                        </select>
                      </PolicyField>
                      <div className="md:col-span-2 xl:col-span-3 rounded-(--sem-radius-control) border px-3 py-2" style={{ borderColor: 'var(--sem-border)' }}>
                        <button className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setAdvanced((a) => !a)} aria-expanded={advanced}>{advanced ? '▾' : '▸'} Advanced: detect data changes</button>
                        {advanced && (
                          <div className="pt-2">
                            <textarea className="sem-field sem-field-sm" value={form.polling} disabled={!!policyBusy} onChange={(e) => set('polling', e.target.value)} rows={2}
                              placeholder="An M expression returning the latest change value, for example List.Max(Source[LastModified])"
                              style={{ width: '100%', maxWidth: 'unset', fontFamily: 'var(--vscode-editor-font-family, monospace)', resize: 'vertical' }} />
                            <div className="sem-text-meta">Optional. A period is loaded again only when this value has changed.</div>
                          </div>
                        )}
                      </div>
                      {!prereqsOk && <div className="md:col-span-2 xl:col-span-3 xe-dot xe-dot-warn" style={{ fontSize: 'var(--sem-text-label)' }}>{clOk
                        ? 'Applying the policy will create or repair the two parameters and add the range filter. Use the setup actions above to review each change first.'
                        : 'Update the model settings before saving an incremental refresh policy.'}</div>}
                    </div>
                  )}

                  {policyGuard.moved && <EditMovedBanner what="policy" theirs={policyGuard.theirs} onKeepMine={policyGuard.keepMine} onUseNew={() => { policyGuard.useNew(); endPolicyDraft(selected); }} disabled={!!policyBusy} />}
                  <div className="flex items-center justify-end gap-2 pt-3 flex-wrap">
                    <div className="mr-auto flex flex-col gap-1">
                      <span className="sem-text-meta">{policy?.enabled ? 'A policy is configured for this table.' : 'No policy on this table yet.'}</span>
                      {policyActionsBusyReason && <span className="sem-text-meta" role="status">{policyActionsBusyReason}</span>}
                    </div>
                    <DraftMark show={policyDrafted} />
                    {policyDrafted && !policyGuard.moved && (
                      <button className="sem-btn sem-btn-sm" onClick={discardPolicy} disabled={policyBlocked} title="Throw away what you typed and show the saved policy again.">Discard</button>
                    )}
                    {policy?.enabled && (
                      <div className="flex flex-col items-end gap-1">
                        <button className="sem-btn sem-btn-sm" onClick={remove} disabled={policyBlocked} style={{ color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }}>{policyBusy === 'remove-policy' ? 'Removing…' : 'Remove policy'}</button>
                        {actionErrors[`remove-policy:${selected}`] && <span className="xe-error" role="alert" style={{ maxWidth: 360 }}>{actionErrors[`remove-policy:${selected}`]}</span>}
                      </div>
                    )}
                    <div className="flex flex-col items-end gap-1">
                      <button className="sem-btn sem-btn-sm sem-btn-primary" onClick={save} disabled={policyBlocked || !enabled || dateColumnOptions.length === 0}>{policyBusy === 'save-policy' ? 'Applying…' : 'Save policy'}</button>
                      {actionErrors[`save-policy:${selected}`] && <span className="xe-error" role="alert" style={{ maxWidth: 360 }}>{actionErrors[`save-policy:${selected}`]}</span>}
                    </div>
                  </div>
                </section>
              </div>
            )}
          </section>
      </div>
    </div>
  );
}

// The M query lane: edit a partition's M or a shared expression/parameter with highlighting, offline
// format, live validity, and an Applied-Steps outline; Save routes through set_partition_m / update_named_expression
// (both doors + undo). Autocomplete + inferred-type hovers are the next increment (powerquery-language-services).
type MTarget = { kind: 'partition'; id: string; label: string; text: string } | { kind: 'expression'; id: string; label: string; name: string; text: string };
type CodeBusyOp = 'format' | 'save' | 'create-query' | 'duplicate' | 'create-reference';
type CodeBusy = { resource: string; op: CodeBusyOp };
function targetText(doc: DocModel, id: string): string | null {
  for (const table of doc.tables) {
    for (const partition of table.partitions) {
      if (`partition:${table.name}/${partition.name}` === id) return partition.source ?? '';
    }
  }
  for (const expression of doc.expressions) {
    if (`expr:${expression.name}` === id) return expression.expression ?? '';
  }
  return null;
}
function MLane({ doc, table, tables, selectedTable, onSelectTable, tableSwitchDisabled, onToast, onSaved, initialTarget }: {
  doc: DocModel;
  table: DocTable | null;
  tables: DocTable[];
  selectedTable: string;
  onSelectTable: (table: string) => void;
  tableSwitchDisabled?: boolean;
  onToast: (toast: Toast | null) => void;
  onSaved: () => Promise<void> | void;
  initialTarget?: { id: string; nonce: number } | null;
}) {
  const targets = useMemo<MTarget[]>(() => {
    const ps: MTarget[] = (table?.partitions ?? []).filter((p) => p.sourceType === 'M')
      .map((p) => ({ kind: 'partition', id: `partition:${table!.name}/${p.name}`, label: `Query · ${p.name}`, text: p.source ?? '' }));
    const es: MTarget[] = (doc.expressions ?? []).filter((e) => (e.kind || 'M') === 'M')
      .map((e) => ({ kind: 'expression', id: `expr:${e.name}`, label: `Shared query · ${e.name}`, name: e.name, text: e.expression ?? '' }));
    return [...ps, ...es];
  }, [table, doc.expressions]);

  // Every M query of the model, by the id its words are held under. Words held for an id that is not here (a query that was renamed or removed) are listed, never held where nobody can see them.
  const allQueries = useMemo(() => [
    ...doc.tables.flatMap((t) => t.partitions.filter((p) => p.sourceType === 'M').map((p) => ({ id: `partition:${t.name}/${p.name}`, label: `Query · ${t.name} / ${p.name}` }))),
    ...(doc.expressions ?? []).filter((e) => (e.kind || 'M') === 'M').map((e) => ({ id: `expr:${e.name}`, label: `Shared query · ${e.name}` })),
  ], [doc.tables, doc.expressions]);
  const [movedWords, setMovedWords] = useState(0);   // words moved onto a query: the editor reads what it holds again
  const [targetId, setTargetId] = useState('');
  const textRevisionRef = useRef(0);
  const currentContextRef = useRef(mContextToken(selectedTable, '', 0));
  // A tree jump names the exact partition ('partition:<Table>/<Name>' — the same id scheme as targets). Select
  // it; if it isn't there (its sourceType isn't M), say so plainly and fall back to the first target. Consumed
  // once per nav nonce so a later doc reload can't yank a selection the user has since changed.
  const consumedNav = useRef(0);
  useEffect(() => {
    if (!initialTarget || initialTarget.nonce === consumedNav.current) return;
    consumedNav.current = initialTarget.nonce;
    if (targets.some((t) => t.id === initialTarget.id)) {
      textRevisionRef.current += 1;
      currentContextRef.current = mContextToken(selectedTable, initialTarget.id, textRevisionRef.current);
      setTargetId(initialTarget.id); return;
    }
    const pname = initialTarget.id.slice(initialTarget.id.indexOf('/') + 1);
    onToast({ text: `Query '${pname}' cannot be edited as M Code.`, tone: 'error' });
    if (targets.length) {
      textRevisionRef.current += 1;
      currentContextRef.current = mContextToken(selectedTable, targets[0].id, textRevisionRef.current);
      setTargetId(targets[0].id);
    }
  }, [initialTarget, targets, onToast]);
  const target = useMemo(() => targets.find((t) => t.id === targetId) ?? targets[0] ?? null, [targets, targetId]);
  const targetIdRef = useRef(target?.id ?? ''); targetIdRef.current = target?.id ?? '';
  const editorDirtyRef = useRef(false);
  const [text, setTextState] = useState('');
  const textRef = useRef('');
  const [original, setOriginalState] = useState('');
  const originalRef = useRef('');
  const loadedTargetRef = useRef('');
  const [serverConflict, setServerConflict] = useState<string | null>(null);
  const confirmDiscard = useCallback((destination: string) =>
    !(editorDirtyRef.current || textRef.current !== originalRef.current)
      || window.confirm(`This M query has unsaved typing. Switch to ${destination} and discard it?`), []);
  currentContextRef.current = mContextToken(selectedTable, target?.id ?? '', textRevisionRef.current);
  const invalidateContext = useCallback((tableName = selectedTable, queryId = targetIdRef.current) => {
    textRevisionRef.current += 1;
    currentContextRef.current = mContextToken(tableName, queryId, textRevisionRef.current);
  }, [selectedTable]);
  const setEditorText = useCallback((next: string) => {
    textRef.current = next; setTextState(next); invalidateContext();
  }, [invalidateContext]);
  const setLoadedText = useCallback((next: string) => {
    textRef.current = next; originalRef.current = next;
    setTextState(next); setOriginalState(next); setServerConflict(null); invalidateContext();
  }, [invalidateContext]);
  const endQueryDraft = useCallback(() => { const m = draftModel(); if (m && targetIdRef.current) draftStore.clear(m, mQueryScope(targetIdRef.current), ''); }, []);
  const selectTarget = useCallback((id: string) => {
    if (id !== targetIdRef.current && !confirmDiscard('another query')) return;
    if (id !== targetIdRef.current) endQueryDraft();   // the person confirmed discarding it
    invalidateContext(selectedTable, id); setTargetId(id);
  }, [confirmDiscard, endQueryDraft, invalidateContext, selectedTable]);
  const selectTable = useCallback((name: string) => {
    if (name !== selectedTable && !confirmDiscard('another table')) return;
    if (name !== selectedTable) endQueryDraft();   // the person confirmed discarding it
    invalidateContext(name, ''); onSelectTable(name);
  }, [confirmDiscard, endQueryDraft, invalidateContext, onSelectTable, selectedTable]);
  const [profileRevision, setProfileRevision] = useState(0);
  const [validity, setValidity] = useState<{ ok: boolean; error?: string } | null>(null);
  const [codeBusy, setCodeBusy] = useState<CodeBusy | null>(null);
  const codeBusyRef = useRef<CodeBusy | null>(null);
  const beginCodeBusy = (resource: string, op: CodeBusyOp) => {
    if (codeBusyRef.current) return false;
    const next = { resource, op }; codeBusyRef.current = next; setCodeBusy(next); return true;
  };
  const endCodeBusy = (resource: string, op: CodeBusyOp) => {
    if (codeBusyRef.current?.resource !== resource || codeBusyRef.current.op !== op) return;
    codeBusyRef.current = null; setCodeBusy(null);
  };
  useEffect(() => () => { currentContextRef.current = ''; codeBusyRef.current = null; }, []);
  const [msg, setMsg] = useState<string | null>(null);
  // Transform surface: a click-to-select span for the editor + the shared Studio toast for transform feedback.
  const [selection, setSelection] = useState<{ from: number; to: number; nonce: number } | null>(null);
  const applyTransform = useCallback((m: string, stepName: string) => {
    setEditorText(m);   // dirty exactly like a manual edit; Save stays the explicit act
    setProfileRevision((revision) => revision + 1);
    onToast({ text: `Step '${stepName}' added; applies at next refresh (the preview shows loaded data)`, tone: 'ok' });
  }, [onToast, setEditorText]);
  const failTransform = useCallback((error: string) => onToast({ text: error, tone: 'error' }), [onToast]);

  // The live data sample + column-op surface is owned by SamplePreview (below): it runs the read-only
  // EVALUATE TOPN against the loaded table and hosts the per-column transforms on the preview headers.
  const { conn } = useConnection();
  const sampleTable = table?.name ?? null;
  const profileSubjectRef = useRef<string | null>(null);
  useEffect(() => {
    const next = reconcileProfileSubject(profileSubjectRef.current, sampleTable, table?.partitions ?? [], doc.expressions);
    profileSubjectRef.current = next.token;
    if (next.profileInvalidated) setProfileRevision((revision) => revision + 1);
  }, [sampleTable, table?.partitions, doc.expressions]);
  // Offline fallback columns for the sample headers (so column ops work with no live engine — generating M
  // needs no connection); SamplePreview overrides these with the loaded sample's live-typed columns. mName is
  // the M-side identity (SourceColumn, the partition-output name). Calculated columns get NO mName (they are
  // not in the partition output, so M transforms stand down); for a data column the model name is the fallback
  // only when the wire lacks the field entirely (an older engine) — a wire-declared null never falls back.
  const docColumns = useMemo<GridCol[]>(
    () => (doc.columns ?? []).filter((c) => c.table === sampleTable).map((c) => ({
      name: c.name, type: c.dataType, calc: !!c.isCalculated, mName: sourceMName(c),
    })),
    [doc.columns, sampleTable],
  );

  // P4 — create a new shared expression / parameter (edit is the target dropdown + Save; this is the missing "C").
  const [creating, setCreating] = useState(false);
  const [moreMenu, setMoreMenu] = useState(false);
  const [newName, setNewName] = useState('');
  const doCreate = async () => {
    const name = newName.trim();
    if (!name) return;
    const resource = 'code:queries', op: CodeBusyOp = 'create-query';
    if (!beginCodeBusy(resource, op)) return;
    setMsg(null);
    try {
      await rpc('createNamedExpression', name, 'let\n    Source = ""\nin\n    Source');
      setCreating(false); setNewName('');
      await onSaved();                 // parent reloads the doc → targets include the new expression
      selectTarget(`expr:${name}`);    // …and select it for editing
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { endCodeBusy(resource, op); }
  };

  // Duplicate / Reference a shared expression (create_named_expression). Duplicate copies the current text;
  // Reference creates `let Source = #"Name" in Source` pointing at the saved expression.
  const [refMode, setRefMode] = useState(false);
  const [refName, setRefName] = useState('');
  const selectExpr = async (name: string) => { await onSaved(); selectTarget(`expr:${name}`); };
  const doDuplicate = async () => {
    if (!target || target.kind !== 'expression') return;
    const resource = 'code:queries', op: CodeBusyOp = 'duplicate';
    if (!beginCodeBusy(resource, op)) return;
    setMsg(null);
    try { const name = `${target.name} (copy)`; await rpc('createNamedExpression', name, text); await selectExpr(name); }
    catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { endCodeBusy(resource, op); }
  };
  const doReference = async () => {
    if (!target || target.kind !== 'expression') return;
    const name = refName.trim();
    if (!name) return;
    const resource = 'code:queries', op: CodeBusyOp = 'create-reference';
    if (!beginCodeBusy(resource, op)) return;
    setMsg(null);
    try {
      await rpc('createNamedExpression', name, `let\n    Source = ${mQuoteIdent(target.name)}\nin\n    Source`);
      setRefMode(false); setRefName('');
      await selectExpr(name);
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { endCodeBusy(resource, op); }
  };

  // Load the target's M when the SELECTION changes (by id) — not on every doc reload, so an unrelated live change
  // can't clobber an in-progress edit. After a save the parent reloads; original is already updated so it's a no-op.
  useEffect(() => {
    const tx = targets.find((t) => t.id === target?.id)?.text ?? '';
    loadedTargetRef.current = target?.id ?? '';
    // Words typed into this query and not saved (F12) open again as typed, over the text they were typed over. If the query changed meanwhile, the next effect finds the editor dirty over a
    // newer server text and offers "Reload server M": the person's words are never replaced, and Save waits for that choice.
    const held = target ? heldDraft<string>(mQueryScope(target.id)) : undefined;
    const start = held ? held.value : tx, base = held ? held.base : tx;
    textRef.current = start; originalRef.current = base;
    setTextState(start); setOriginalState(base); setServerConflict(null); setMsg(null); invalidateContext();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.id, movedWords]);

  // A doc reload can carry M written outside this lane (range-filter setup or policy auto-wiring). Clean editors
  // reload immediately. Dirty editors keep the local text, expose a conflict with a reload action, and block Save
  // until the newer server revision is acknowledged. Both paths invalidate Profile.
  useEffect(() => {
    if (!target || loadedTargetRef.current !== target.id) return;
    const next = reconcileExternalM({ text: textRef.current, original: originalRef.current }, target.text);
    if (next.kind === 'unchanged') { setServerConflict(null); return; }
    setProfileRevision((revision) => revision + 1);
    if (next.kind === 'reloaded') {
      setLoadedText(next.text);
      setMsg('M reloaded because the query changed outside the editor.');
      return;
    }
    setServerConflict(next.serverText);
    setMsg('M changed outside the editor. Reload the newer server text before saving.');
  }, [target?.text, target, setLoadedText]);

  useEffect(() => {
    let live = true;
    // Drive the strip from the SAME validator the inline squiggles use (syntax + duplicate identifiers), so a
    // green "✓ valid M" never sits next to red squiggles. ok only when there are no error/warning diagnostics.
    // Empty text is not a query: do not wear a valid M chip on a table with nothing to edit (D-158).
    if (!text.trim()) { setValidity(null); return; }
    const id = window.setTimeout(async () => {
      const diags = (await mDiagnostics(text)).filter((d) => d.severity <= 2);
      if (!live) return;
      setValidity(diags.length === 0 ? { ok: true } : { ok: false, error: diags.length === 1 ? diags[0].message : `${diags.length} problems` });
    }, 350);
    return () => { live = false; window.clearTimeout(id); };
  }, [text]);
  const dirty = text !== original;
  editorDirtyRef.current = dirty;
  // Hold what is typed above this lane (F12), on every change, over the text it was typed over. It ends when the text is back to where it started (a save, Revert, typing it back, Reload server M).
  // Read from the refs: on the first pass after a mount the state still holds the empty start while the load effect has already put the restored words in the refs.
  useEffect(() => {
    const m = draftModel();
    if (!m || !target || loadedTargetRef.current !== target.id) return;
    const typed = textRef.current, base = originalRef.current;
    if (typed !== base) draftStore.set(m, mQueryScope(target.id), '', { value: typed, base }); else draftStore.clear(m, mQueryScope(target.id), '');
  }, [text, original, target?.id]);   // eslint-disable-line react-hooks/exhaustive-deps

  const doFormat = async () => {
    if (!target) return;
    const resource = `code:${target.id}`, op: CodeBusyOp = 'format';
    if (!beginCodeBusy(resource, op)) return;
    const operationContext = currentContextRef.current;
    const source = textRef.current;
    setMsg(null);
    try {
      const r = await formatM(source);
      if (!isMContextCurrent(operationContext, currentContextRef.current)) {
        onToast({ text: 'Format result discarded because the table, query, or M text changed while it was running.', tone: 'error' });
        return;
      }
      if (r.ok) { setEditorText(r.text); setProfileRevision((revision) => revision + 1); }
      else setMsg('Format failed: ' + r.error);
    } catch (e) {
      if (!isMContextCurrent(operationContext, currentContextRef.current)) {
        onToast({ text: 'Format result discarded because the table, query, or M text changed while it was running.', tone: 'error' });
        return;
      }
      setMsg('Format failed: ' + String((e as Error).message ?? e));
    }
    finally { endCodeBusy(resource, op); }
  };
  const doSave = async () => {
    if (!target) return;
    const resource = `code:${target.id}`, op: CodeBusyOp = 'save';
    if (!beginCodeBusy(resource, op)) return;
    const operationContext = currentContextRef.current;
    const savingTarget = target;
    const savingText = textRef.current;
    const loadedText = originalRef.current;
    setMsg(null);
    try {
      const latestDoc = await rpc<DocModel>('getDocModel', 1);
      if (!isMContextCurrent(operationContext, currentContextRef.current)) {
        onToast({ text: 'Save discarded before writing because the table, query, or M text changed.', tone: 'error' });
        return;
      }
      const latestText = targetText(latestDoc, savingTarget.id);
      if (latestText == null) throw new Error('The M query no longer exists. Reload the model before saving.');
      const saveRevision = reconcileSaveRevision(loadedText, savingText, latestText);
      if (saveRevision === 'already-saved') {
        originalRef.current = savingText; setOriginalState(savingText); setServerConflict(null);
        setMsg('✓ already saved');
        await onSaved();
        return;
      }
      if (saveRevision === 'conflict') {
        setServerConflict(latestText); setProfileRevision((revision) => revision + 1);
        setMsg('Save blocked because this M changed outside the editor. Reload the newer server text and reapply your edit.');
        await onSaved();
        return;
      }
      // The text just read is the one this write replaces, and the engine compares it inside its own write: a read here can still be a moment behind.
      if (savingTarget.kind === 'partition') await rpc('setPartitionM', savingTarget.id, savingText, 'human', latestText);
      else await rpc('updateNamedExpression', savingTarget.name, savingText, 'human', latestText);
      if (!isMContextCurrent(operationContext, currentContextRef.current)) {
        onToast({ text: 'An earlier M revision was saved. Newer local edits remain unsaved.', tone: 'error' });
        await onSaved();
        return;
      }
      originalRef.current = savingText; setOriginalState(savingText); setServerConflict(null);
      setMsg('✓ saved'); setProfileRevision((revision) => revision + 1); await onSaved();
    } catch (e) {
      if (!isMContextCurrent(operationContext, currentContextRef.current)) {
        onToast({ text: 'Save result discarded because the table, query, or M text changed while it was running.', tone: 'error' });
        return;
      }
      setMsg(String((e as Error).message ?? e));
    }
    finally { endCodeBusy(resource, op); }
  };
  const reloadServerText = () => {
    if (serverConflict == null) return;
    setLoadedText(serverConflict); setProfileRevision((revision) => revision + 1);
    setMsg('Reloaded the newer server M. Local edits were discarded.');
  };
  const codeActing = codeBusy !== null;
  const editorContextToken = mContextToken(selectedTable, target?.id ?? '', textRevisionRef.current);
  const isEditorContextCurrent = (captured: string) => isMContextCurrent(captured, currentContextRef.current);
  const codeBusyActivity = codeBusy?.op === 'format' ? 'formatting M' : codeBusy?.op === 'save' ? 'saving M' : 'creating a query';
  const codeBusyReason = codeBusy
    ? `Table and query selectors are unavailable while ${codeBusyActivity}.`
    : tableSwitchDisabled ? 'The table selector is unavailable while the policy action finishes.' : null;

  const hasEditableM = !!target && text.trim().length > 0;
  const orphanWords = <OrphanDrafts prefix="mcode|query|" live={allQueries.map((q) => q.id)} what="M query"
    nameOf={(id) => allQueries.find((q) => q.id === id)?.label ?? id.replace(/^partition:|^expr:/, '')}
    targetsFor={() => allQueries.map((q) => ({ id: q.id, label: q.label }))} onMoved={() => setMovedWords((n) => n + 1)} />;
  const contextStrip = (
    <div className="flex items-center gap-2 flex-wrap sem-text-meta">
        <span>Table</span>
        <select className="sem-field sem-field-sm" value={selectedTable} onChange={(e) => selectTable(e.target.value)} disabled={tableSwitchDisabled || codeActing} title="Choose a table">
          {tables.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
        </select>
        <span aria-hidden="true">›</span>
        <span>Editing</span>
        <select className="sem-field sem-field-sm" value={target?.id ?? ''} onChange={(e) => selectTarget(e.target.value)} disabled={!target || codeActing} title="Choose an M query">
          {targets.map((t) => <option key={t.id} value={t.id}>{t.label}</option>)}
        </select>
        {codeBusyReason && <span className="sem-text-meta" role="status">{codeBusyReason}</span>}
        {/* The state shows only on a problem: a query that is fine shows nothing (A2/P5). */}
        {hasEditableM && validity && !validity.ok && <span className="ml-auto xe-msg" style={{ '--xe-dot': validity.ok ? 'var(--sem-good)' : 'var(--sem-bad)' } as React.CSSProperties}>{validity.error || 'M needs attention'}</span>}
    </div>
  );

  if (!targets.length) return (
    <div className="sem-grid">
      {contextStrip}
      {orphanWords}
      <div className="sem-text-meta">This table has no editable M query, and the model has no shared M queries.</div>
    </div>
  );

  return (
    <div className="sem-grid">
      {contextStrip}
      {orphanWords}
      <div className="flex items-center gap-2 flex-wrap">
          <button className="sem-btn sem-btn-sm sem-btn-primary" onClick={doSave} disabled={codeActing || !dirty || serverConflict != null}>{codeBusy?.op === 'save' ? 'Saving…' : 'Save M'}</button>
          <button className="sem-btn sem-btn-sm" onClick={doFormat} disabled={codeActing} title="Pretty-print the M (offline)">{codeBusy?.op === 'format' ? 'Formatting…' : 'Format'}</button>
          {/* Revert is the one control a person needs beside Save M, and only while there is something to undo. */}
          {dirty && <button className="sem-btn sem-btn-sm" onClick={() => setEditorText(original)} disabled={codeActing}>Revert</button>}
          <DraftMark show={dirty} />
          {serverConflict != null && <button className="sem-btn sem-btn-sm" onClick={reloadServerText} disabled={codeActing}>Reload server M</button>}
          {codeBusy && <span className="sem-text-meta" role="status">Other M actions are unavailable while {codeBusyActivity}.</span>}
          {/* New query, Duplicate and Create reference are rare: one menu. Their forms open here when chosen. */}
          <span className="relative">
            <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-label="More for this query" aria-haspopup="menu" aria-expanded={moreMenu} disabled={codeActing} onClick={() => setMoreMenu((o) => !o)}>⋯</button>
            {moreMenu && <MenuPop onClose={() => setMoreMenu(false)} items={[
              { label: 'New query', detail: 'Create a new shared M query or parameter.', act: () => setCreating(true) },
              ...(target?.kind === 'expression' ? [
                { label: 'Duplicate', detail: 'Copy this query under a new name.', act: () => void doDuplicate() },
                { label: 'Create reference', detail: 'Create a new query that references this one.', act: () => { setRefMode(true); setRefName(`${target.name} (ref)`); } },
              ] : []),
            ]} />}
          </span>
          {creating && (
            <>
              <input className="sem-field sem-field-sm" autoFocus value={newName} disabled={codeActing} onChange={(e) => setNewName(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') void doCreate(); else if (e.key === 'Escape') { setCreating(false); setNewName(''); } }}
                placeholder="new query name" style={{ maxWidth: 160 }} />
              <button className="sem-btn sem-btn-sm sem-btn-primary" onClick={doCreate} disabled={codeActing || !newName.trim()}>{codeBusy?.op === 'create-query' ? 'Creating…' : 'Create'}</button>
              <button className="sem-btn sem-btn-sm" onClick={() => { setCreating(false); setNewName(''); }} disabled={codeActing}>Cancel</button>
            </>
          )}
          {target?.kind === 'expression' && !creating && refMode && (
            <>
              <input className="sem-field sem-field-sm" autoFocus value={refName} disabled={codeActing} onChange={(e) => setRefName(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') void doReference(); else if (e.key === 'Escape') { setRefMode(false); setRefName(''); } }}
                placeholder="reference name" style={{ maxWidth: 160 }} />
              <button className="sem-btn sem-btn-sm sem-btn-primary" onClick={doReference} disabled={codeActing || !refName.trim()}>{codeBusy?.op === 'create-reference' ? 'Creating…' : 'Create reference'}</button>
              <button className="sem-btn sem-btn-sm" onClick={() => { setRefMode(false); setRefName(''); }} disabled={codeActing}>Cancel</button>
            </>
          )}
          {msg && <span role={msg.startsWith('✓') ? 'status' : 'alert'} className={'sem-text-meta xe-dot ' + (msg.startsWith('✓') ? 'xe-dot-good' : 'xe-dot-bad')}>{msg}</span>}
      </div>

          <section className="sem-card sem-span-9 sem-stack" aria-label="M editor" title="Editing M changes the query, it does not run a refresh. Type to autocomplete (Ctrl+Space), or hover a name for its inferred type.">
            {/* 420 put the preview panel's own hint row exactly on the fold at 1440x900, so it rendered as a
                sliced half-line above the context bar. The editor is still resizable by hand. */}
            <MEditor value={text} onChange={setEditorText} minHeight={370} selection={selection ?? undefined} resizable />
          </section>
          <aside className="sem-span-3 min-w-0 self-start" aria-label="Applied steps">
            <AppliedStepsPanel text={text} contextToken={editorContextToken} identity={mContextToken(selectedTable, target?.id ?? '', 0)} isContextCurrent={isEditorContextCurrent} onChange={setEditorText} collapsibleOnCompact
              onSelect={(from, to) => setSelection((s) => ({ from, to, nonce: (s?.nonce ?? 0) + 1 }))}
              onError={failTransform} />
          </aside>
        {sampleTable && (
            <SamplePreview tableName={sampleTable} docColumns={docColumns} mText={text} connected={!!conn?.connected}
              contextToken={editorContextToken} isContextCurrent={isEditorContextCurrent} profileRevision={profileRevision} apply={applyTransform} fail={failTransform} />
        )}
    </div>
  );
}

// --- little building blocks ---

function periodsLabel(periods: number, granularity: string): string {
  return `${periods} ${granularity.toLowerCase()}${periods === 1 ? '' : 's'}`;
}
function granDays(gran: Gran): number {
  return gran === 'Day' ? 1 : gran === 'Month' ? 30 : gran === 'Quarter' ? 90 : 365;
}
function refreshWindowFitsArchive(storePeriods: number, storeGran: Gran, refreshPeriods: number, refreshGran: Gran): boolean {
  return storePeriods * granDays(storeGran) >= refreshPeriods * granDays(refreshGran);
}
function PolicyField({ label, hintText, children }: { label: string; hintText?: string; children: React.ReactNode }) {
  return (
    <label className="min-w-0 flex flex-col gap-1 text-(length:--sem-text-label)">
      <span style={{ color: 'var(--sem-muted)' }}>{label}</span>
      {children}
      {hintText && <span className="sem-text-meta">{hintText}</span>}
    </label>
  );
}
function Num({ value, onChange, disabled }: { value: number; onChange: (v: number) => void; disabled?: boolean }) {
  return <input className="sem-field sem-field-sm" type="number" min={0} value={value} disabled={disabled} onChange={(e) => onChange(Math.max(0, parseInt(e.target.value || '0', 10)))} style={{ width: '100%', maxWidth: 'unset' }} />;
}
function GranSel({ value, onChange, disabled }: { value: Gran; onChange: (v: Gran) => void; disabled?: boolean }) {
  return (
    <select className="sem-field sem-field-sm" value={value} disabled={disabled} onChange={(e) => onChange(e.target.value as Gran)} style={{ width: '100%', maxWidth: 'unset' }}>
      {GRANS.map((g) => <option key={g} value={g}>{g}</option>)}
    </select>
  );
}
function Check({ ok, label, detail, action, busy, busyReason }: { ok: boolean; label: string; detail?: string; action?: { label: string; run: () => void; running?: boolean; runningLabel?: string; error?: string }; busy?: boolean; busyReason?: string | null }) {
  return (
    <div className="flex items-start gap-2 py-2">
      <span className={'xe-dot ' + (ok ? 'xe-dot-good' : 'xe-dot-warn')} style={{ fontWeight: 'var(--sem-weight-strong)', width: 14 }}>{ok ? '✓' : '!'}</span>
      <div className="flex-1 min-w-0">
        <div style={{ color: 'var(--sem-fg)' }}>{label}</div>
        {detail && <div className="sem-text-meta">{detail}</div>}
      </div>
      {action && (
        <div className="flex flex-col items-end gap-1">
          <button className="sem-btn sem-btn-sm" onClick={action.run} disabled={busy}>{action.running ? (action.runningLabel ?? 'Working…') : action.label}</button>
          {action.error && <span className="xe-error" role="alert" style={{ maxWidth: 360 }}>{action.error}</span>}
          {busyReason && <span className="sem-text-meta" role="status" style={{ maxWidth: 360 }}>{busyReason}</span>}
        </div>
      )}
    </div>
  );
}
